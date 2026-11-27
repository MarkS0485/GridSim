using System.Globalization;
using GridSim.Core.Model;

namespace GridSim.Core.Api;

/// <summary>The kinds of element the scripting object model addresses (RC5 WP11).</summary>
public enum ElementKind
{
    /// <summary>A bus, keyed by its id.</summary>
    Bus,
    /// <summary>A generator (the whole fleet, out-of-service units included).</summary>
    Generator,
    /// <summary>The aggregated demand of a bus, keyed by the bus id.</summary>
    Load,
    /// <summary>A branch (line or transformer).</summary>
    Branch,
    /// <summary>The fixed shunt (Gs, Bs) of a bus, keyed by the bus id.</summary>
    Shunt,
}

/// <summary>The spelling of element kinds.</summary>
public static class ElementKinds
{
    /// <summary>Every kind, in the order they are listed.</summary>
    public static IReadOnlyList<ElementKind> All { get; } = [ElementKind.Bus, ElementKind.Generator, ElementKind.Load, ElementKind.Branch, ElementKind.Shunt];

    /// <summary>The kind's name: bus, generator, load, branch or shunt.</summary>
    public static string Name(ElementKind kind) => kind switch
    {
        ElementKind.Bus => "bus",
        ElementKind.Generator => "generator",
        ElementKind.Load => "load",
        ElementKind.Branch => "branch",
        ElementKind.Shunt => "shunt",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The kind a name spells (ordinal, lower case), or false.</summary>
    public static bool TryParse(string? name, out ElementKind kind)
    {
        foreach (ElementKind k in All)
            if (string.Equals(name, Name(k), StringComparison.Ordinal)) { kind = k; return true; }
        kind = default;
        return false;
    }

    /// <summary>The kind names, comma separated, for a refusal.</summary>
    public static string List => string.Join(", ", All.Select(Name));
}

/// <summary>
/// One addressable element: its kind, its key (text, compared ordinally) and the index it stands for (into
/// <see cref="GridModel.Buses"/> for a bus, load or shunt; <see cref="GridModel.AllGenerators"/> for a generator;
/// <see cref="GridModel.Branches"/> for a branch).
/// </summary>
public sealed record ElementKey(ElementKind Kind, string Key, int Index);

/// <summary>
/// The key rules of the object model. Keys are fixed when the case is loaded and listed in model order, never hash order.
/// <list type="bullet">
/// <item>bus: the bus id as text ("5").</item>
/// <item>generator: <c>&lt;bus&gt;:&lt;machine id&gt;</c> when the PSS/E machine keys are known; otherwise the model name, else
/// <c>G&lt;bus&gt;</c> (the scripting naming rule). A key met a second time gets <c>#2</c>, <c>#3</c> and so on.</item>
/// <item>load: the bus id of a bus that carries demand when the case is loaded.</item>
/// <item>branch: <c>&lt;from&gt;-&lt;to&gt;:&lt;circuit&gt;</c> (a three-winding transformer winding:
/// <c>&lt;from&gt;-&lt;to&gt;-&lt;third&gt;:&lt;circuit&gt;:&lt;winding&gt;</c>) when the PSS/E branch keys are known; otherwise the
/// model name, else <c>&lt;from&gt;-&lt;to&gt;</c>, with <c>#n</c> on a repeat.</item>
/// <item>shunt: the bus id of a bus that has a fixed shunt (Gs or Bs not zero) when the case is loaded.</item>
/// </list>
/// </summary>
public static class ElementKeys
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>A PSS/E machine identity: bus number and machine id (as written, blanks removed).</summary>
    public readonly record struct MachineId(int Bus, string Id);

    /// <summary>A PSS/E branch identity.</summary>
    public readonly record struct BranchId(int From, int To, int Third, string Circuit, int Winding);

    /// <summary>The generator keys of <paramref name="model"/> (whole fleet, in order).</summary>
    public static IReadOnlyList<string> Generators(GridModel model, IReadOnlyList<MachineId>? machines)
    {
        ArgumentNullException.ThrowIfNull(model);
        IReadOnlyList<Generator> fleet = model.AllGenerators;
        if (machines is not null && machines.Count == fleet.Count && Enumerable.Range(0, fleet.Count).All(i => machines[i].Bus == fleet[i].Bus))
            return Unique(machines.Select(m => m.Bus.ToString(Inv) + ":" + m.Id.Trim()));
        return Unique(fleet.Select(g => Present(g.Name) ?? "G" + g.Bus.ToString(Inv)));
    }

    /// <summary>The branch keys of <paramref name="model"/> (in order).</summary>
    public static IReadOnlyList<string> Branches(GridModel model, IReadOnlyList<BranchId>? keys)
    {
        ArgumentNullException.ThrowIfNull(model);
        IReadOnlyList<Branch> br = model.Branches;
        if (keys is not null && keys.Count == br.Count)
            return Unique(keys.Select(k => k.Third == 0
                ? $"{k.From.ToString(Inv)}-{k.To.ToString(Inv)}:{k.Circuit.Trim()}"
                : $"{k.From.ToString(Inv)}-{k.To.ToString(Inv)}-{k.Third.ToString(Inv)}:{k.Circuit.Trim()}:{k.Winding.ToString(Inv)}"));
        return Unique(br.Select(b => Present(b.Name) ?? b.From.ToString(Inv) + "-" + b.To.ToString(Inv)));
    }

    private static string? Present(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>The keys with the n-th occurrence of a base numbered <c>#n</c> (n from 2), in order, as the scripting
    /// naming rule does. Two keys that still collide (a written name equal to a numbered one) are refused, naming it.</summary>
    internal static IReadOnlyList<string> Unique(IEnumerable<string> bases)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (string b in bases)
        {
            counts.TryGetValue(b, out int c);
            counts[b] = ++c;
            string name = c == 1 ? b : b + "#" + c.ToString(Inv);
            if (!seen.Add(name))
                throw new InvalidDataException($"two elements both resolve to the key '{name}'; the object model cannot address them apart.");
            names.Add(name);
        }
        return names;
    }
}
