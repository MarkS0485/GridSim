using System.Globalization;
using System.Text.Json;
using GridSim.Core.Model;

namespace GridSim.Core.Analysis.Opf;

/// <summary>
/// One outage of an AC-SCOPF (<see cref="AcScopfOptions.Outages"/>): the branches it removes (indices into
/// <see cref="GridModel.Branches"/>) and the units it removes (indices into <see cref="GridModel.Generators"/>, the
/// in-service list). One branch is an N-1 branch outage, two branches an N-2 outage, one unit a generator outage, and any
/// mix is allowed. <see cref="Name"/> is an optional label.
/// </summary>
public sealed record ScopfOutage(IReadOnlyList<int> Branches, IReadOnlyList<int> Generators)
{
    /// <summary>An optional name; <see cref="Label"/> uses it when given.</summary>
    public string? Name { get; init; }

    /// <summary>The name, else the elements: for example "branches 0+1", "generator 2" or "branch 3, generator 1".</summary>
    public string Label
    {
        get
        {
            if (!string.IsNullOrEmpty(Name)) return Name;
            var parts = new List<string>(2);
            if (Branches.Count > 0)
                parts.Add((Branches.Count == 1 ? "branch " : "branches ") + string.Join("+", Branches.Select(k => k.ToString(CultureInfo.InvariantCulture))));
            if (Generators.Count > 0)
                parts.Add((Generators.Count == 1 ? "generator " : "generators ") + string.Join("+", Generators.Select(k => k.ToString(CultureInfo.InvariantCulture))));
            return string.Join(", ", parts);
        }
    }

    /// <summary>A one-branch outage.</summary>
    public static ScopfOutage OfBranch(int branch) => new(new[] { branch }, Array.Empty<int>());

    /// <summary>A one-unit outage.</summary>
    public static ScopfOutage OfGenerator(int generator) => new(Array.Empty<int>(), new[] { generator });
}

/// <summary>
/// The outage list of an AC-SCOPF: N-2 branch pairs and the <c>gridsim-scopf-outages/1</c> file.
/// <para>File: <c>{"format": "gridsim-scopf-outages/1", "outages": [{"branches": [0, 1], "generators": [], "name": "double
/// circuit A"}, ...]}</c>. <c>branches</c> and <c>generators</c> are lists of indices (branches into the case's branch
/// list, units into its in-service unit list); either may be left out, but not both. <c>name</c> is optional. Unknown
/// members are refused.</para>
/// </summary>
public static class ScopfOutageFile
{
    /// <summary>The format name of the outage file.</summary>
    public const string Format = "gridsim-scopf-outages/1";

    /// <summary>The most N-2 pairs <see cref="Pairs"/> makes unless a larger cap is given (the count grows as n^2 / 2).</summary>
    public const int DefaultMaxOutages = 5000;

    /// <summary>
    /// Every unordered pair of <paramref name="branches"/>, in lexicographic index order: (b0, b1), (b0, b2), ...,
    /// (b1, b2), ... with the branches sorted. A branch listed twice is refused, and so is a pair count above
    /// <paramref name="maxOutages"/> (the AC-SCOPF screens every pair each round).
    /// </summary>
    public static IReadOnlyList<ScopfOutage> Pairs(IReadOnlyList<int> branches, int maxOutages = DefaultMaxOutages)
    {
        ArgumentNullException.ThrowIfNull(branches);
        if (maxOutages < 1) throw new ArgumentOutOfRangeException(nameof(maxOutages), maxOutages, "N-2 pairs: the cap must be at least 1.");
        int[] sorted = branches.ToArray();
        Array.Sort(sorted);
        for (int i = 1; i < sorted.Length; i++)
            if (sorted[i] == sorted[i - 1]) throw new InvalidDataException($"N-2 pairs: branch {sorted[i]} is listed twice.");
        long n = sorted.Length;
        long count = n * (n - 1) / 2;
        if (count > maxOutages)
            throw new InvalidDataException(
                $"N-2 pairs: {n} branches give {count} pairs, above the cap of {maxOutages}; list fewer branches, or raise the cap (--max-outages).");
        var list = new List<ScopfOutage>((int)count);
        for (int i = 0; i < sorted.Length; i++)
            for (int j = i + 1; j < sorted.Length; j++)
                list.Add(new ScopfOutage(new[] { sorted[i], sorted[j] }, Array.Empty<int>()));
        return list;
    }

    /// <summary>Read a <c>gridsim-scopf-outages/1</c> file. Indices are checked against the case by
    /// <see cref="AcScopf.Solve"/>; this checks the shape. Refusals are <see cref="InvalidDataException"/>.</summary>
    public static IReadOnlyList<ScopfOutage> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new InvalidDataException($"SCOPF outages: not valid JSON ({ex.Message})."); }
        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("SCOPF outages: the file must be a JSON object.");
            bool formatSeen = false;
            List<ScopfOutage>? outages = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty prop in root.EnumerateObject())
            {
                if (!seen.Add(prop.Name)) throw new InvalidDataException($"SCOPF outages: member '{prop.Name}' is given twice.");
                switch (prop.Name)
                {
                    case "format":
                        if (prop.Value.ValueKind != JsonValueKind.String || prop.Value.GetString() != Format)
                            throw new InvalidDataException($"SCOPF outages: format must be \"{Format}\".");
                        formatSeen = true;
                        break;
                    case "outages":
                        if (prop.Value.ValueKind != JsonValueKind.Array) throw new InvalidDataException("SCOPF outages: outages must be a list of objects.");
                        outages = new List<ScopfOutage>();
                        foreach (JsonElement e in prop.Value.EnumerateArray()) outages.Add(ReadOne(e, outages.Count));
                        break;
                    default:
                        throw new InvalidDataException($"SCOPF outages: unknown member '{prop.Name}' (known: format, outages).");
                }
            }
            if (!formatSeen) throw new InvalidDataException($"SCOPF outages: the file needs \"format\": \"{Format}\".");
            if (outages is null || outages.Count == 0) throw new InvalidDataException("SCOPF outages: the file lists no outages.");
            return outages;
        }
    }

    private static ScopfOutage ReadOne(JsonElement e, int position)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"SCOPF outages: outage {position} must be an object.");
        int[] branches = Array.Empty<int>(), gens = Array.Empty<int>();
        string? name = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty p in e.EnumerateObject())
        {
            if (!seen.Add(p.Name)) throw new InvalidDataException($"SCOPF outages: outage {position} gives '{p.Name}' twice.");
            switch (p.Name)
            {
                case "branches": branches = Indices(p, position); break;
                case "generators": gens = Indices(p, position); break;
                case "name":
                    if (p.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"SCOPF outages: outage {position}'s name must be a string.");
                    name = p.Value.GetString();
                    break;
                default:
                    throw new InvalidDataException($"SCOPF outages: outage {position} has unknown member '{p.Name}' (known: branches, generators, name).");
            }
        }
        if (branches.Length == 0 && gens.Length == 0) throw new InvalidDataException($"SCOPF outages: outage {position} removes nothing (give branches, generators or both).");
        return new ScopfOutage(branches, gens) { Name = name };
    }

    private static int[] Indices(JsonProperty p, int position)
    {
        if (p.Value.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"SCOPF outages: outage {position}'s {p.Name} must be a list of indices.");
        var list = new List<int>();
        foreach (JsonElement v in p.Value.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int k) || k < 0)
                throw new InvalidDataException($"SCOPF outages: outage {position}'s {p.Name} holds {v.GetRawText()}, which is not an index (a whole number of 0 or more).");
            list.Add(k);
        }
        return list.ToArray();
    }
}
