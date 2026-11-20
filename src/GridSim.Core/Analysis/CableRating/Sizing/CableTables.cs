using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace GridSim.Core.Analysis.CableRating.Sizing;

/// <summary>Where a table's numbers come from. The tables of IEC 60364-5-52 and BS 7671 are paid text: nothing is shipped filled, and a file
/// that carries any data must say who filled it, from what, and on what date.</summary>
public sealed record TableProvenance(string Source, string Edition, string FilledBy, string Date);

/// <summary>Tabulated current-carrying capacity (A) against conductor cross-section for one installation method, conductor, insulation and number of
/// loaded conductors. <see cref="Method"/> is the owner's installation-method label (for example the reference method letter of the standard).</summary>
public sealed record AmpacityTable(string Method, string Conductor, string Insulation, int LoadedConductors, IReadOnlyList<double> SizesMm2, IReadOnlyList<double> Amps);

/// <summary>A correction factor against a key: <c>kind</c> "ambient" (key = ambient temperature in C) or "grouping" (key = number of circuits). Exact
/// keys only: no interpolation is done.</summary>
public sealed record FactorTable(string Kind, string Method, string Insulation, IReadOnlyList<double> Keys, IReadOnlyList<double> Factors);

/// <summary>Tabulated voltage drop in mV per ampere per metre for one conductor, insulation and phase count ("1ph" or "3ph").</summary>
public sealed record VoltDropTable(string Conductor, string Insulation, string Phases, IReadOnlyList<double> SizesMm2, IReadOnlyList<double> MvPerAPerM);

/// <summary>Conductor resistance and reactance (ohm/km) per cross-section, for the computed voltage drop when no mV/A/m table is held.</summary>
public sealed record ImpedanceTable(string Conductor, string Insulation, IReadOnlyList<double> SizesMm2, IReadOnlyList<double> ROhmPerKm, IReadOnlyList<double> XOhmPerKm);

/// <summary>The adiabatic constant k (A s^0.5 / mm^2) for a conductor and insulation.</summary>
public sealed record AdiabaticK(string Conductor, string Insulation, double K);

/// <summary>
/// A cable-sizing table file (<c>gridsim-cable-tables/1</c>). Shipped files under <c>data/cable-tables</c> are EMPTY: the owner of the standard
/// fills them (with provenance). A quantity whose table is missing or empty is OUT OF SCOPE in the result; nothing is guessed or defaulted.
/// </summary>
public sealed record CableTables(
    string Id, TableProvenance Provenance, IReadOnlyList<AmpacityTable> Ampacity, IReadOnlyList<FactorTable> Factors,
    IReadOnlyList<VoltDropTable> VoltDrop, IReadOnlyList<ImpedanceTable> Impedance, IReadOnlyList<AdiabaticK> AdiabaticK)
{
    public const string Schema = "gridsim-cable-tables/1";

    public bool IsEmpty => Ampacity.Count == 0 && Factors.Count == 0 && VoltDrop.Count == 0 && Impedance.Count == 0 && AdiabaticK.Count == 0;
    public string Status => IsEmpty ? "EMPTY" : "FILLED";

    public static CableTables Empty(string id) =>
        new(id, new TableProvenance("", "", "", ""), Array.Empty<AmpacityTable>(), Array.Empty<FactorTable>(), Array.Empty<VoltDropTable>(),
            Array.Empty<ImpedanceTable>(), Array.Empty<AdiabaticK>());

    public static CableTables Parse(string json)
    {
        var problems = new List<string>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new FormatException("cable tables are not valid JSON: " + ex.Message); }
        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("cable tables must be a JSON object");
            foreach (JsonProperty p in root.EnumerateObject())
                if (p.Name is not ("schema" or "id" or "provenance" or "ampacity" or "factors" or "voltDrop" or "impedance" or "adiabaticK")) problems.Add($"unknown key '{p.Name}'");
            if (!root.TryGetProperty("schema", out JsonElement sch) || sch.ValueKind != JsonValueKind.String || sch.GetString() != Schema)
                problems.Add($"schema must be \"{Schema}\"");
            string id = Str(root, "id");
            if (id.Length == 0) problems.Add("id is required");
            TableProvenance prov = new("", "", "", "");
            if (root.TryGetProperty("provenance", out JsonElement pe) && pe.ValueKind == JsonValueKind.Object)
                prov = new TableProvenance(Str(pe, "source"), Str(pe, "edition"), Str(pe, "filledBy"), Str(pe, "date"));

            var amp = new List<AmpacityTable>();
            foreach ((JsonElement e, string w) in Items(root, "ampacity"))
            {
                double[] sizes = Arr(e, "sizesMm2", w, problems), amps = Arr(e, "amps", w, problems);
                Check(sizes, amps, w, problems);
                int lc = (int)Num(e, "loadedConductors", w, problems);
                amp.Add(new AmpacityTable(Str(e, "method"), Str(e, "conductor"), Str(e, "insulation"), lc, sizes, amps));
                Need(e, w, problems, "method", "conductor", "insulation");
            }
            var fac = new List<FactorTable>();
            foreach ((JsonElement e, string w) in Items(root, "factors"))
            {
                string kind = Str(e, "kind");
                if (kind is not ("ambient" or "grouping")) problems.Add($"{w}: kind must be ambient or grouping");
                double[] keys = Arr(e, "keys", w, problems), f = Arr(e, "factors", w, problems);
                Check(keys, f, w, problems, increasing: false);
                if (f.Any(x => !(x > 0.0))) problems.Add($"{w}: factors must be above 0");
                fac.Add(new FactorTable(kind, Str(e, "method"), Str(e, "insulation"), keys, f));
            }
            var vd = new List<VoltDropTable>();
            foreach ((JsonElement e, string w) in Items(root, "voltDrop"))
            {
                double[] sizes = Arr(e, "sizesMm2", w, problems), mv = Arr(e, "mvPerAPerM", w, problems);
                Check(sizes, mv, w, problems);
                string ph = Str(e, "phases");
                if (ph is not ("1ph" or "3ph")) problems.Add($"{w}: phases must be 1ph or 3ph");
                Need(e, w, problems, "conductor", "insulation");
                vd.Add(new VoltDropTable(Str(e, "conductor"), Str(e, "insulation"), ph, sizes, mv));
            }
            var imp = new List<ImpedanceTable>();
            foreach ((JsonElement e, string w) in Items(root, "impedance"))
            {
                double[] sizes = Arr(e, "sizesMm2", w, problems), r = Arr(e, "rOhmPerKm", w, problems), x = Arr(e, "xOhmPerKm", w, problems);
                Check(sizes, r, w, problems); Check(sizes, x, w, problems, positive: false);
                Need(e, w, problems, "conductor", "insulation");
                imp.Add(new ImpedanceTable(Str(e, "conductor"), Str(e, "insulation"), sizes, r, x));
            }
            var ks = new List<AdiabaticK>();
            foreach ((JsonElement e, string w) in Items(root, "adiabaticK"))
            {
                Need(e, w, problems, "conductor", "insulation");
                ks.Add(new AdiabaticK(Str(e, "conductor"), Str(e, "insulation"), Num(e, "k", w, problems)));
            }
            var t = new CableTables(id, prov, amp, fac, vd, imp, ks);
            if (!t.IsEmpty && (prov.Source.Length == 0 || prov.FilledBy.Length == 0 || prov.Date.Length == 0 || prov.Edition.Length == 0))
                problems.Add("a table file with data must carry provenance: source, edition, filledBy and date (the tables are paid text; say where each number came from)");
            if (problems.Count > 0) throw new FormatException("cable tables: " + string.Join("; ", problems));
            return t;
        }
    }

    private static IEnumerable<(JsonElement, string)> Items(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out JsonElement a) || a.ValueKind != JsonValueKind.Array) yield break;
        int i = 0;
        foreach (JsonElement e in a.EnumerateArray()) yield return (e, $"{key}[{i++}]");
    }

    private static void Need(JsonElement e, string w, List<string> problems, params string[] keys)
    {
        foreach (string k in keys) if (Str(e, k).Length == 0) problems.Add($"{w}: '{k}' is required");
    }

    private static void Check(double[] key, double[] val, string w, List<string> problems, bool increasing = true, bool positive = true)
    {
        if (key.Length == 0) problems.Add($"{w}: the table has no entries");
        if (key.Length != val.Length) problems.Add($"{w}: the two arrays must have the same length");
        for (int i = 1; i < key.Length; i++) if (increasing && !(key[i] > key[i - 1])) { problems.Add($"{w}: keys must be strictly increasing"); break; }
        if (positive && val.Any(v => !(v > 0.0))) problems.Add($"{w}: values must be above 0");
    }

    private static string Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static double Num(JsonElement e, string key, string where, List<string> problems)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out JsonElement v) || v.ValueKind != JsonValueKind.Number)
        { problems.Add($"{where}: '{key}' is required and must be a number"); return double.NaN; }
        double d = v.GetDouble();
        if (!double.IsFinite(d) || !(d > 0.0)) { problems.Add($"{where}: '{key}' must be finite and above 0"); return double.NaN; }
        return d;
    }

    private static double[] Arr(JsonElement e, string key, string where, List<string> problems)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out JsonElement v) || v.ValueKind != JsonValueKind.Array)
        { problems.Add($"{where}: '{key}' is required and must be an array of numbers"); return Array.Empty<double>(); }
        var l = new List<double>();
        foreach (JsonElement x in v.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Number || !double.IsFinite(x.GetDouble())) { problems.Add($"{where}: '{key}' must hold finite numbers only"); return Array.Empty<double>(); }
            l.Add(x.GetDouble());
        }
        return l.ToArray();
    }
}
