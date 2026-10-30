using System.Text.Json;
using GridSim.Core.Model;

namespace GridSim.Core.Analysis;

/// <summary>
/// The unit commitment data file (<c>gridsim-uc/1</c>) read by the <c>scuc</c> verb, turned into <see cref="UcOptions"/>
/// for a given case:
/// <code>
/// { "format": "gridsim-uc/1", "periods": 24, "periodHours": 1,
///   "demandScale": [0.7, 0.65, ...], "reserveMw": [100, 100, ...],
///   "units": [ { "unit": 0, "marginalCost": 12.5, "noLoadCost": 100, "startupCost": 500, "minUp": 3, "minDown": 2,
///                "rampUpMw": 80, "rampDownMw": 80, "initiallyOn": false, "initialPeriods": 5, "initialMw": 0,
///                "mustRun": false },
///              { "name": "G2", "startupCost": 200 } ],
///   "contingencies": [ 4, "Line 7-8" ] }
/// </code>
/// Every key is checked and an unknown key is refused. Every top-level key except <c>format</c> is optional (the
/// defaults of <see cref="UcOptions"/> and <see cref="UcUnitData"/>). A unit is named by <c>unit</c> (its index among
/// the case's in-service generators, from 0) or by <c>name</c> (the generator's name, which must be unique); a unit given
/// twice is refused. A contingency is a branch index (from 0) or a branch name (unique). Throws
/// <see cref="InvalidDataException"/> naming the entry.
/// </summary>
public static class UnitCommitmentFile
{
    /// <summary>The format tag of the unit commitment data file.</summary>
    public const string FileFormat = "gridsim-uc/1";

    /// <summary>Read <paramref name="json"/> (labelled <paramref name="label"/> in messages) against
    /// <paramref name="model"/>; <paramref name="periodsOverride"/> (when given) replaces the file's period count, which
    /// must then agree with any per-period arrays the file holds.</summary>
    public static UcOptions Read(string json, string label, GridModel model, int? periodsOverride = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(model);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new InvalidDataException($"unit commitment file {label}: not valid JSON ({ex.Message})."); }
        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"unit commitment file {label}: the top level must be an object.");
            string? format = null;
            int? periods = null;
            double? hours = null;
            double[]? scale = null, reserve = null;
            JsonElement? unitList = null, contList = null;
            string where = $"unit commitment file {label}";
            foreach (JsonProperty p in root.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "format": format = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null; break;
                    case "periods": periods = Int(p.Value, where, "periods"); break;
                    case "periodHours": hours = Num(p.Value, where, "periodHours"); break;
                    case "demandScale": scale = Nums(p.Value, where, "demandScale"); break;
                    case "reserveMw": reserve = Nums(p.Value, where, "reserveMw"); break;
                    case "units": unitList = p.Value; break;
                    case "contingencies": contList = p.Value; break;
                    default:
                        throw new InvalidDataException($"{where}: unknown key '{p.Name}' (allowed: format, periods, periodHours, demandScale, reserveMw, units, contingencies).");
                }
            }
            if (format != FileFormat) throw new InvalidDataException($"{where}: \"format\" must be \"{FileFormat}\".");

            int T = periodsOverride ?? periods ?? scale?.Length ?? reserve?.Length ?? 24;
            if (T < 1) throw new InvalidDataException($"{where}: the number of periods must be at least 1.");
            if (scale is not null && scale.Length != T) throw new InvalidDataException($"{where}: \"demandScale\" has {scale.Length} value(s) for {T} period(s).");
            if (reserve is not null && reserve.Length != T) throw new InvalidDataException($"{where}: \"reserveMw\" has {reserve.Length} value(s) for {T} period(s).");

            IReadOnlyList<Generator> gens = model.Generators;
            UcUnitData?[]? units = null;
            if (unitList is { } ul)
            {
                if (ul.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{where}: \"units\" must be an array.");
                units = new UcUnitData?[gens.Count];
                int entry = 0;
                foreach (JsonElement e in ul.EnumerateArray())
                {
                    entry++;
                    string w = $"{where}, units entry {entry}";
                    (int index, UcUnitData data) = Unit(e, w, gens);
                    if (units[index] is not null) throw new InvalidDataException($"{w}: unit {index} is given more than once.");
                    units[index] = data;
                }
            }

            var contingencies = new List<int>();
            if (contList is { } cl)
            {
                if (cl.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{where}: \"contingencies\" must be an array.");
                int entry = 0;
                foreach (JsonElement e in cl.EnumerateArray())
                {
                    entry++;
                    string w = $"{where}, contingencies entry {entry}";
                    int k;
                    if (e.ValueKind == JsonValueKind.Number)
                    {
                        k = Int(e, w, "branch");
                        if ((uint)k >= (uint)model.Branches.Count) throw new InvalidDataException($"{w}: branch {k} is not in the case ({model.Branches.Count} branches).");
                    }
                    else if (e.ValueKind == JsonValueKind.String)
                    {
                        string name = e.GetString()!;
                        var hits = Enumerable.Range(0, model.Branches.Count).Where(i => model.Branches[i].Name == name).ToList();
                        if (hits.Count != 1) throw new InvalidDataException($"{w}: {hits.Count} branches are named '{name}'; a contingency name must match exactly one.");
                        k = hits[0];
                    }
                    else throw new InvalidDataException($"{w}: must be a branch index or a branch name.");
                    if (contingencies.Contains(k)) throw new InvalidDataException($"{w}: branch {k} is listed twice.");
                    contingencies.Add(k);
                }
            }

            var o = new UcOptions
            {
                Periods = T,
                DemandScale = scale,
                ReserveMw = reserve,
                Units = units,
                Contingencies = contingencies,
            };
            return hours is double hh ? o with { PeriodHours = hh } : o;
        }
    }

    private static (int Index, UcUnitData Data) Unit(JsonElement e, string w, IReadOnlyList<Generator> gens)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{w}: must be an object.");
        int? index = null;
        string? name = null;
        var d = new UcUnitData();
        foreach (JsonProperty p in e.EnumerateObject())
        {
            switch (p.Name)
            {
                case "unit": index = Int(p.Value, w, "unit"); break;
                case "name":
                    if (p.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{w}: \"name\" must be a string.");
                    name = p.Value.GetString(); break;
                case "marginalCost": d = d with { MarginalCost = Num(p.Value, w, "marginalCost") }; break;
                case "noLoadCost": d = d with { NoLoadCost = Num(p.Value, w, "noLoadCost") }; break;
                case "startupCost": d = d with { StartupCost = Num(p.Value, w, "startupCost") }; break;
                case "minUp": d = d with { MinUpPeriods = Int(p.Value, w, "minUp") }; break;
                case "minDown": d = d with { MinDownPeriods = Int(p.Value, w, "minDown") }; break;
                case "rampUpMw": d = d with { RampUpMw = Num(p.Value, w, "rampUpMw") }; break;
                case "rampDownMw": d = d with { RampDownMw = Num(p.Value, w, "rampDownMw") }; break;
                case "initiallyOn": d = d with { InitiallyOn = Bool(p.Value, w, "initiallyOn") }; break;
                case "initialPeriods": d = d with { InitialPeriods = Int(p.Value, w, "initialPeriods") }; break;
                case "initialMw": d = d with { InitialMw = Num(p.Value, w, "initialMw") }; break;
                case "mustRun": d = d with { MustRun = Bool(p.Value, w, "mustRun") }; break;
                default:
                    throw new InvalidDataException($"{w}: unknown key '{p.Name}' (allowed: unit, name, marginalCost, noLoadCost, startupCost, minUp, minDown, rampUpMw, rampDownMw, initiallyOn, initialPeriods, initialMw, mustRun).");
            }
        }
        if ((index is null) == (name is null)) throw new InvalidDataException($"{w}: give exactly one of \"unit\" (an index) and \"name\".");
        if (index is int i)
        {
            if ((uint)i >= (uint)gens.Count) throw new InvalidDataException($"{w}: unit {i} is not in the case ({gens.Count} in-service generators).");
            return (i, d);
        }
        var hits = Enumerable.Range(0, gens.Count).Where(k => gens[k].Name == name).ToList();
        if (hits.Count != 1) throw new InvalidDataException($"{w}: {hits.Count} in-service generators are named '{name}'; a unit name must match exactly one.");
        return (hits[0], d);
    }

    private static int Int(JsonElement v, string where, string key)
    {
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int i)) throw new InvalidDataException($"{where}: \"{key}\" must be a whole number.");
        return i;
    }

    private static double Num(JsonElement v, string where, string key)
    {
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double d) || !double.IsFinite(d)) throw new InvalidDataException($"{where}: \"{key}\" must be a finite number.");
        return d;
    }

    private static bool Bool(JsonElement v, string where, string key) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new InvalidDataException($"{where}: \"{key}\" must be true or false."),
    };

    private static double[] Nums(JsonElement v, string where, string key)
    {
        if (v.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{where}: \"{key}\" must be an array of numbers.");
        var list = new List<double>();
        int i = 0;
        foreach (JsonElement e in v.EnumerateArray()) { i++; list.Add(Num(e, where, $"{key}[{i}]")); }
        return list.ToArray();
    }
}
