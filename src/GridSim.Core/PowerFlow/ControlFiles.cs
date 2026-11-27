using System.Text.Json;
using GridSim.Core.Estimation;
using GridSim.Core.Model;

namespace GridSim.Core.PowerFlow;

/// <summary>A tie branch's metered end from an area-interchange file: the branch by its two buses (and its name when
/// there are parallel circuits) and the bus at which it is metered.</summary>
public sealed record MeteredEndEntry(int From, int To, int MeteredBus, string? Name);

/// <summary>The contents of a <c>gridsim-area-interchange/1</c> file.</summary>
public sealed record AreaInterchangeFile(IReadOnlyList<AreaInterchangeSpec> Areas, IReadOnlyList<AreaMembership> ReportAreas,
    IReadOnlyList<MeteredEndEntry> MeteredEnds)
{
    /// <summary>
    /// The metered ends by branch index in <paramref name="model"/>. Each entry must match exactly one in-service
    /// branch between its two buses (either orientation; by name when given), and its metered bus must be one of them.
    /// </summary>
    public IReadOnlyDictionary<int, BranchEnd> ResolveMeteredEnds(GridModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var map = new Dictionary<int, BranchEnd>();
        int entry = 0;
        foreach (MeteredEndEntry e in MeteredEnds)
        {
            entry++;
            string who = $"area-interchange file, meteredEnds entry {entry} ({e.From}-{e.To})";
            if (e.MeteredBus != e.From && e.MeteredBus != e.To) throw new InvalidDataException($"{who}: meteredBus {e.MeteredBus} is not one of the branch's buses.");
            var hits = new List<int>();
            for (int k = 0; k < model.Branches.Count; k++)
            {
                Branch b = model.Branches[k];
                if (!b.InService) continue;
                bool ends = (b.From == e.From && b.To == e.To) || (b.From == e.To && b.To == e.From);
                if (ends && (e.Name is null || string.Equals(b.Name, e.Name, StringComparison.Ordinal))) hits.Add(k);
            }
            if (hits.Count == 0) throw new InvalidDataException($"{who}: no in-service branch matches.");
            if (hits.Count > 1) throw new InvalidDataException($"{who}: {hits.Count} in-service branches match; give the name.");
            if (map.ContainsKey(hits[0])) throw new InvalidDataException($"{who}: that branch is given twice.");
            map[hits[0]] = model.Branches[hits[0]].From == e.MeteredBus ? BranchEnd.From : BranchEnd.To;
        }
        return map;
    }
}

/// <summary>Strict readers for the two control files of <see cref="AreaInterchangePowerFlow"/> and
/// <see cref="RemoteVoltagePowerFlow"/>. Every key is checked (an unknown key is refused); errors throw
/// <see cref="InvalidDataException"/> naming the entry.</summary>
public static class ControlFiles
{
    /// <summary>The format tag of the area-interchange file.</summary>
    public const string AreaInterchangeFormat = "gridsim-area-interchange/1";

    /// <summary>The format tag of the remote voltage regulation file.</summary>
    public const string RemoteVoltageFormat = "gridsim-remote-vreg/1";

    /// <summary>
    /// Read <c>{ "format": "gridsim-area-interchange/1", "areas": [ { "area": 2, "swingBus": 2, "desiredExportMw": -30,
    /// "toleranceMw": 0.1, "buses": [2, 3] } ], "reportAreas": [ { "area": 1, "buses": [1] } ], "meteredEnds": [ { "from": 1,
    /// "to": 2, "meteredBus": 2, "name": "L12" } ] }</c>. In "areas" every key is required; "reportAreas" and
    /// "meteredEnds" are optional, and so is "name" in a metered end.
    /// </summary>
    public static AreaInterchangeFile ReadAreaInterchange(string json, string label)
    {
        string what = $"area-interchange file {label}";
        using JsonDocument doc = Parse(json, what);
        JsonElement root = doc.RootElement;
        string? format = null;
        JsonElement? areas = null, report = null, metered = null;
        foreach (JsonProperty p in root.EnumerateObject())
        {
            switch (p.Name)
            {
                case "format": format = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null; break;
                case "areas": areas = p.Value; break;
                case "reportAreas": report = p.Value; break;
                case "meteredEnds": metered = p.Value; break;
                default: throw new InvalidDataException($"{what}: unknown key '{p.Name}' (allowed: format, areas, reportAreas, meteredEnds).");
            }
        }
        if (format != AreaInterchangeFormat) throw new InvalidDataException($"{what}: \"format\" must be \"{AreaInterchangeFormat}\".");
        if (areas is not { ValueKind: JsonValueKind.Array } arr) throw new InvalidDataException($"{what}: \"areas\" must be an array.");

        var specs = new List<AreaInterchangeSpec>();
        int n = 0;
        foreach (JsonElement e in arr.EnumerateArray())
        {
            string where = $"{what}, areas entry {++n}";
            RequireObject(e, where);
            int? area = null, swing = null; double? des = null, tol = null; IReadOnlyList<int>? buses = null;
            foreach (JsonProperty p in e.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "area": area = Int(p.Value, where, "area"); break;
                    case "swingBus": swing = Int(p.Value, where, "swingBus"); break;
                    case "desiredExportMw": des = Num(p.Value, where, "desiredExportMw"); break;
                    case "toleranceMw": tol = Num(p.Value, where, "toleranceMw"); break;
                    case "buses": buses = Ints(p.Value, where, "buses"); break;
                    default: throw new InvalidDataException($"{where}: unknown key '{p.Name}' (allowed: area, swingBus, desiredExportMw, toleranceMw, buses).");
                }
            }
            if (area is null || swing is null || des is null || tol is null || buses is null)
                throw new InvalidDataException($"{where}: area, swingBus, desiredExportMw, toleranceMw and buses are required.");
            specs.Add(new AreaInterchangeSpec(area.Value, swing.Value, des.Value, tol.Value, buses));
        }

        var reported = new List<AreaMembership>();
        if (report is { } rep)
        {
            if (rep.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{what}: \"reportAreas\" must be an array.");
            n = 0;
            foreach (JsonElement e in rep.EnumerateArray())
            {
                string where = $"{what}, reportAreas entry {++n}";
                RequireObject(e, where);
                int? area = null; IReadOnlyList<int>? buses = null;
                foreach (JsonProperty p in e.EnumerateObject())
                {
                    switch (p.Name)
                    {
                        case "area": area = Int(p.Value, where, "area"); break;
                        case "buses": buses = Ints(p.Value, where, "buses"); break;
                        default: throw new InvalidDataException($"{where}: unknown key '{p.Name}' (allowed: area, buses).");
                    }
                }
                if (area is null || buses is null) throw new InvalidDataException($"{where}: area and buses are required.");
                reported.Add(new AreaMembership(area.Value, buses));
            }
        }

        var ends = new List<MeteredEndEntry>();
        if (metered is { } met)
        {
            if (met.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{what}: \"meteredEnds\" must be an array.");
            n = 0;
            foreach (JsonElement e in met.EnumerateArray())
            {
                string where = $"{what}, meteredEnds entry {++n}";
                RequireObject(e, where);
                int? from = null, to = null, bus = null; string? name = null;
                foreach (JsonProperty p in e.EnumerateObject())
                {
                    switch (p.Name)
                    {
                        case "from": from = Int(p.Value, where, "from"); break;
                        case "to": to = Int(p.Value, where, "to"); break;
                        case "meteredBus": bus = Int(p.Value, where, "meteredBus"); break;
                        case "name":
                            if (p.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{where}: \"name\" must be a string.");
                            name = p.Value.GetString(); break;
                        default: throw new InvalidDataException($"{where}: unknown key '{p.Name}' (allowed: from, to, meteredBus, name).");
                    }
                }
                if (from is null || to is null || bus is null) throw new InvalidDataException($"{where}: from, to and meteredBus are required.");
                ends.Add(new MeteredEndEntry(from.Value, to.Value, bus.Value, name));
            }
        }
        return new AreaInterchangeFile(specs, reported, ends);
    }

    /// <summary>
    /// Read <c>{ "format": "gridsim-remote-vreg/1", "controls": [ { "generatorBus": 2, "regulatedBus": 3, "targetVpu": 1.0 } ] }</c>.
    /// Every key is required. The controls keep the file's order.
    /// </summary>
    public static IReadOnlyList<RemoteVoltageControl> ReadRemoteVoltage(string json, string label)
    {
        string what = $"remote-vreg file {label}";
        using JsonDocument doc = Parse(json, what);
        string? format = null;
        JsonElement? list = null;
        foreach (JsonProperty p in doc.RootElement.EnumerateObject())
        {
            if (p.Name == "format") format = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
            else if (p.Name == "controls") list = p.Value;
            else throw new InvalidDataException($"{what}: unknown key '{p.Name}' (allowed: format, controls).");
        }
        if (format != RemoteVoltageFormat) throw new InvalidDataException($"{what}: \"format\" must be \"{RemoteVoltageFormat}\".");
        if (list is not { ValueKind: JsonValueKind.Array } arr) throw new InvalidDataException($"{what}: \"controls\" must be an array.");
        var controls = new List<RemoteVoltageControl>();
        int n = 0;
        foreach (JsonElement e in arr.EnumerateArray())
        {
            string where = $"{what}, controls entry {++n}";
            RequireObject(e, where);
            int? gen = null, reg = null; double? target = null;
            foreach (JsonProperty p in e.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "generatorBus": gen = Int(p.Value, where, "generatorBus"); break;
                    case "regulatedBus": reg = Int(p.Value, where, "regulatedBus"); break;
                    case "targetVpu": target = Num(p.Value, where, "targetVpu"); break;
                    default: throw new InvalidDataException($"{where}: unknown key '{p.Name}' (allowed: generatorBus, regulatedBus, targetVpu).");
                }
            }
            if (gen is null || reg is null || target is null) throw new InvalidDataException($"{where}: generatorBus, regulatedBus and targetVpu are required.");
            controls.Add(new RemoteVoltageControl(gen.Value, reg.Value, target.Value));
        }
        return controls;
    }

    private static JsonDocument Parse(string json, string what)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new InvalidDataException($"{what}: not valid JSON ({ex.Message})."); }
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            doc.Dispose();
            throw new InvalidDataException($"{what}: the top level must be an object.");
        }
        return doc;
    }

    private static void RequireObject(JsonElement e, string where)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{where}: must be an object.");
    }

    private static int Int(JsonElement v, string where, string key)
    {
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int i)) throw new InvalidDataException($"{where}: \"{key}\" must be a whole number.");
        return i;
    }

    private static double Num(JsonElement v, string where, string key)
    {
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double d) || !double.IsFinite(d))
            throw new InvalidDataException($"{where}: \"{key}\" must be a finite number.");
        return d;
    }

    private static IReadOnlyList<int> Ints(JsonElement v, string where, string key)
    {
        if (v.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{where}: \"{key}\" must be an array of whole numbers.");
        var list = new List<int>();
        foreach (JsonElement e in v.EnumerateArray()) list.Add(Int(e, where, key));
        return list;
    }
}
