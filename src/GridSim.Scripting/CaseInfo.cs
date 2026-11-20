using System.Collections.Immutable;
using GridSim.Core.Model;

namespace GridSim.Scripting;

/// <summary>
/// A loaded case as the script sees it: the model as loaded, the SHA-256 of the input it came from, and the names every
/// generator, load and line answers to. The names are fixed at load time, so a TRIP never renames anything.
/// <para>Naming rule (grammar 1): a generator is its model name, else <c>G&lt;bus&gt;</c>; a load (a bus with demand) is its bus
/// name, else <c>L&lt;bus&gt;</c>; a line (any branch, transformers included) is its model name, else <c>&lt;from&gt;-&lt;to&gt;</c>.
/// A bus is its model name, else its number as text (<c>"4"</c>). A name met a second time gets <c>#2</c>, <c>#3</c> and so on, in model order.</para>
/// </summary>
public sealed class CaseInfo
{
    public CaseInfo(string pathAsWritten, string inputKind, string sha256, GridModel model)
    {
        PathAsWritten = pathAsWritten;
        InputKind = inputKind;
        Sha256 = sha256;
        Model = model;

        GeneratorNames = Unique(model.AllGenerators.Select(g => Present(g.Name) ?? "G" + g.Bus.ToString(System.Globalization.CultureInfo.InvariantCulture)), "generator");
        LineNames = Unique(model.Branches.Select(b => Present(b.Name) ?? b.From.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + b.To.ToString(System.Globalization.CultureInfo.InvariantCulture)), "line");

        var loadBus = new List<int>();
        var loadBase = new List<string>();
        for (int i = 0; i < model.Buses.Count; i++)
        {
            Bus b = model.Buses[i];
            if (b.Pd == 0 && b.Qd == 0) continue;
            loadBus.Add(i);
            loadBase.Add(Present(b.Name) ?? "L" + b.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        LoadBusIndex = loadBus;
        LoadNames = Unique(loadBase, "load");

        BusNames = Unique(model.Buses.Select(b => Present(b.Name) ?? b.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)), "bus");
        BusIndex = Index(BusNames);
        GeneratorIndex = Index(GeneratorNames);
        LineIndex = Index(LineNames);
        LoadIndex = Index(LoadNames);
    }

    public string PathAsWritten { get; }
    /// <summary>"matpower", "psse-raw" or "json-folder".</summary>
    public string InputKind { get; }
    /// <summary>SHA-256 (lower-case hex) of the input: the file's bytes, or for a folder the hash of its sorted file hashes.</summary>
    public string Sha256 { get; }
    public GridModel Model { get; }

    /// <summary>Names of <see cref="GridModel.AllGenerators"/>, same order.</summary>
    public IReadOnlyList<string> GeneratorNames { get; }
    /// <summary>Names of <see cref="GridModel.Branches"/>, same order.</summary>
    public IReadOnlyList<string> LineNames { get; }
    /// <summary>Names of the loads (buses with demand at load time), in bus order.</summary>
    public IReadOnlyList<string> LoadNames { get; }
    /// <summary>For load k, the index in <see cref="GridModel.Buses"/>.</summary>
    public IReadOnlyList<int> LoadBusIndex { get; }

    /// <summary>Names of <see cref="GridModel.Buses"/>, same order: the model name, else the bus number as text.</summary>
    public IReadOnlyList<string> BusNames { get; }

    public ImmutableDictionary<string, int> BusIndex { get; }

    public ImmutableDictionary<string, int> GeneratorIndex { get; }
    public ImmutableDictionary<string, int> LineIndex { get; }
    public ImmutableDictionary<string, int> LoadIndex { get; }

    private static string? Present(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static ImmutableDictionary<string, int> Index(IReadOnlyList<string> names)
    {
        var b = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < names.Count; i++) b[names[i]] = i;
        return b.ToImmutable();
    }

    private static IReadOnlyList<string> Unique(IEnumerable<string> bases, string what)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string b in bases)
        {
            counts.TryGetValue(b, out int c);
            counts[b] = ++c;
            string name = c == 1 ? b : b + "#" + c.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!seen.Add(name))
                throw new ScriptException(ErrorPhase.Execution, default, $"the case has two {what}s that both resolve to the name '{name}'; the script cannot address them apart");
            names.Add(name);
        }
        return names;
    }
}
