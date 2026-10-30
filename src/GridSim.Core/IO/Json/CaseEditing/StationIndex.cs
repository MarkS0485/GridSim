using GridSim.Core.IO.Json.Lossless;

namespace GridSim.Core.IO.Json.CaseEditing;

/// <summary>
/// One station: the generating units sharing an effective type and a name (Mark's decision D3: stations are the
/// existing names grouped by fuel type; no schema change). <see cref="Name"/> is null for units with no name.
/// </summary>
/// <param name="Type">The effective type (the generator's type, or ccgt when absent, as the loader reads it).</param>
/// <param name="Name">The shared name, or null for unnamed units.</param>
/// <param name="Units">The units' indices in generation.json, in file order.</param>
/// <param name="CapacityMva">The sum of the units' capacityMva (absent = 0), for display only.</param>
/// <param name="AnyTypeAbsent">True when at least one unit has no type member (shown with a '*').</param>
internal sealed record Station(string Type, string? Name, IReadOnlyList<int> Units, double CapacityMva, bool AnyTypeAbsent)
{
    /// <summary>The station's display name: its name, or "(unnamed)".</summary>
    public string DisplayName => Name ?? StationIndex.Unnamed;
}

/// <summary>One fuel type: its stations (ordinal by name, then first unit) and totals.</summary>
internal sealed record FuelGroup(string Type, IReadOnlyList<Station> Stations, int UnitCount, double CapacityMva, bool AnyTypeAbsent);

/// <summary>
/// The Generation > Fuel type > Station > unit hierarchy of a v1 case, built deterministically from generation.json:
/// fuel types ordinal by effective type, stations ordinal by name (unnamed last via the sentinel's sort position),
/// then by first file index, units in file-index order. gb-full has 210 duplicated generator names (none spanning two
/// fuel types; the largest group " other" has 153 units), so grouping must not depend on dictionary order.
/// </summary>
internal static class StationIndex
{
    /// <summary>The display name of the station of units without a name.</summary>
    public const string Unnamed = "(unnamed)";

    /// <summary>Build the hierarchy from the session's current generation.json (empty when the file is absent).</summary>
    public static IReadOnlyList<FuelGroup> Build(CaseSession s)
    {
        ArgumentNullException.ThrowIfNull(s);
        CaseDocument gen = s.Doc("generation.json");
        if (!gen.Exists || gen.Text.Root.Kind != JsonSourceKind.Array) return Array.Empty<FuelGroup>();
        StringComparer cmp = gen.KeyComparer;

        // (type, name-or-null) -> unit indices; Ordinal keys, insertion order irrelevant (sorted below).
        var stations = new Dictionary<(string Type, string? Name), List<int>>();
        var typeAbsent = new HashSet<int>();
        IReadOnlyList<JsonSourceNode> units = gen.Text.Root.Elements;
        for (int i = 0; i < units.Count; i++)
        {
            if (units[i].Kind != JsonSourceKind.Object) continue;
            string? type = RecordRead.Str(units[i], "type", cmp);
            if (type is null) typeAbsent.Add(i);
            var key = (type ?? JsonDefaults.GeneratorType, RecordRead.Str(units[i], "name", cmp));
            if (!stations.TryGetValue(key, out List<int>? list)) stations[key] = list = new List<int>();
            list.Add(i);
        }

        double Cap(int i) => RecordRead.Num(units[i], "capacityMva", cmp) ?? 0.0;

        var groups = new List<FuelGroup>();
        foreach (IGrouping<string, KeyValuePair<(string Type, string? Name), List<int>>> g in
                 stations.GroupBy(kv => kv.Key.Type, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var list = g.Select(kv => new Station(kv.Key.Type, kv.Key.Name, kv.Value, kv.Value.Sum(Cap), kv.Value.Any(typeAbsent.Contains)))
                        .OrderBy(st => st.Name is null ? 1 : 0)
                        .ThenBy(st => st.Name ?? "", StringComparer.Ordinal)
                        .ThenBy(st => st.Units[0])
                        .ToList();
            int unitCount = list.Sum(st => st.Units.Count);
            // Summed in station order then unit order, so the display total is deterministic.
            double cap = 0;
            foreach (Station st in list) foreach (int u in st.Units) cap += Cap(u);
            groups.Add(new FuelGroup(g.Key, list, unitCount, cap, list.Any(st => st.AnyTypeAbsent)));
        }
        return groups;
    }

    /// <summary>The station a unit belongs to (by file index), or null when the index is not a generator record.</summary>
    public static Station? StationOf(IReadOnlyList<FuelGroup> groups, int unit)
    {
        ArgumentNullException.ThrowIfNull(groups);
        foreach (FuelGroup g in groups)
            foreach (Station st in g.Stations)
                if (st.Units.Contains(unit)) return st;
        return null;
    }
}
