using System.Globalization;
using System.Numerics;
using System.Text.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.Harmonics;
using GridSim.Core.Model;

namespace GridSim.Core.IO;

/// <summary>The measurements of one harmonic order: the candidate source buses to estimate and the bus voltage phasors.</summary>
/// <param name="Order">The harmonic order (a whole number of 2 or more).</param>
/// <param name="CandidateBuses">The buses whose injections are estimated, in file order.</param>
/// <param name="Voltages">The measured bus voltage phasors, in file order.</param>
public sealed record HarmonicMeasurementOrder(int Order, IReadOnlyList<int> CandidateBuses, IReadOnlyList<HarmonicVoltageMeasurement> Voltages);

/// <summary>
/// RC5 WP27: a <c>gridsim-harmonic-meas/1</c> document, the input of the <c>harmonic-se</c> verb. Strict: an unknown key, a
/// missing key or a wrong type is refused, naming the place.
/// <code>
/// {
///   "schema": "gridsim-harmonic-meas/1",
///   "takenUtc": "2026-11-01T12:00:00Z",
///   "baseMva": 100,
///   "orders": [
///     { "order": 5, "candidates": [3],
///       "voltages": [ { "bus": 1, "re": 0.001, "im": -0.002, "sigma": 1e-5 } ] }
///   ]
/// }
/// </code>
/// Phasors are per unit on the case base, the same quantity the harmonic penetration study reports. <c>sigma</c> is the
/// standard deviation of each of the real and the imaginary part of a measurement (pu). Measurements are telemetry, so
/// <c>takenUtc</c> is required and may not lie in the future. Only bus voltage phasors are read: the estimator
/// (<see cref="HarmonicStateEstimator"/>) has no branch current measurement, so a <c>currents</c> key is refused by name.
/// </summary>
public sealed record HarmonicMeasurementSet(DateTime TakenUtc, double BaseMva, IReadOnlyList<HarmonicMeasurementOrder> Orders)
{
    /// <summary>The schema identifier.</summary>
    public const string SchemaId = "gridsim-harmonic-meas/1";

    /// <summary>Read and check a measurement file.</summary>
    /// <exception cref="InvalidDataException">The file is not a valid <c>gridsim-harmonic-meas/1</c> document.</exception>
    /// <exception cref="ForwardInferenceException">The measurements are dated in the future.</exception>
    public static HarmonicMeasurementSet Load(string path, DateTime? utcNow = null)
    {
        string json = InputFileLimits.ReadAllText(path);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false }); }
        catch (JsonException ex) { throw new InvalidDataException($"'{path}' is not valid JSON: {ex.Message}", ex); }
        using (doc) return Parse(doc.RootElement, path, utcNow);
    }

    /// <summary>Parse a document already read.</summary>
    public static HarmonicMeasurementSet Parse(JsonElement root, string where, DateTime? utcNow = null)
    {
        Keys(root, where, "schema", "takenUtc", "baseMva", "orders", "note");
        string schema = Str(root, "schema", where);
        if (schema != SchemaId) throw new InvalidDataException($"{where}: schema must be \"{SchemaId}\" but is \"{schema}\".");
        string takenText = Str(root, "takenUtc", where);
        if (!DateTime.TryParse(takenText, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime taken))
            throw new InvalidDataException($"{where}: takenUtc '{takenText}' is not a date and time (ISO 8601, UTC).");
        ForwardInferenceGuard.RejectFuture(taken, utcNow, "harmonic measurement set");
        double baseMva = Num(root, "baseMva", where);
        if (!(baseMva > 0)) throw new InvalidDataException($"{where}: baseMva must be above 0.");
        if (!root.TryGetProperty("orders", out JsonElement orders) || orders.ValueKind != JsonValueKind.Array || orders.GetArrayLength() == 0)
            throw new InvalidDataException($"{where}: \"orders\" must be a non-empty array.");
        var list = new List<HarmonicMeasurementOrder>();
        var seenOrders = new HashSet<int>();
        int oi = 0;
        foreach (JsonElement o in orders.EnumerateArray())
        {
            string ow = $"{where}: orders[{oi}]";
            if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty("currents", out _))
                throw new InvalidDataException($"{ow}: \"currents\" is not read: the harmonic state estimator takes bus voltage phasors only, so branch current measurements cannot be used.");
            Keys(o, ow, "order", "candidates", "voltages");
            int order = Int(o, "order", ow);
            if (order < 2) throw new InvalidDataException($"{ow}: order must be a whole number of 2 or more (order 1 is the fundamental).");
            if (!seenOrders.Add(order)) throw new InvalidDataException($"{ow}: order {order} is given twice.");
            if (!o.TryGetProperty("candidates", out JsonElement cands) || cands.ValueKind != JsonValueKind.Array || cands.GetArrayLength() == 0)
                throw new InvalidDataException($"{ow}: \"candidates\" must be a non-empty array of bus numbers.");
            var candidates = new List<int>();
            foreach (JsonElement c in cands.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Number || !c.TryGetInt32(out int bus)) throw new InvalidDataException($"{ow}: every candidate must be a bus number.");
                if (candidates.Contains(bus)) throw new InvalidDataException($"{ow}: candidate bus {bus} is given twice.");
                candidates.Add(bus);
            }
            if (!o.TryGetProperty("voltages", out JsonElement vs) || vs.ValueKind != JsonValueKind.Array || vs.GetArrayLength() == 0)
                throw new InvalidDataException($"{ow}: \"voltages\" must be a non-empty array.");
            var meas = new List<HarmonicVoltageMeasurement>();
            var seenBus = new HashSet<int>();
            int vi = 0;
            foreach (JsonElement v in vs.EnumerateArray())
            {
                string vw = $"{ow}.voltages[{vi}]";
                Keys(v, vw, "bus", "re", "im", "sigma");
                int bus = Int(v, "bus", vw);
                if (!seenBus.Add(bus)) throw new InvalidDataException($"{vw}: bus {bus} is measured twice at this order.");
                double sigma = Num(v, "sigma", vw);
                if (!(sigma > 0)) throw new InvalidDataException($"{vw}: sigma must be above 0.");
                meas.Add(new HarmonicVoltageMeasurement(bus, new Complex(Num(v, "re", vw), Num(v, "im", vw)), sigma));
                vi++;
            }
            list.Add(new HarmonicMeasurementOrder(order, candidates, meas));
            oi++;
        }
        return new HarmonicMeasurementSet(taken, baseMva, list);
    }

    /// <summary>Check the set against the case: the base and every bus named.</summary>
    /// <exception cref="InvalidDataException">A bus the case does not have, or a different base.</exception>
    public void Validate(GridModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var problems = new List<string>();
        if (Math.Abs(BaseMva - model.BaseMva) > 1e-9)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"baseMva {BaseMva} does not match the case base {model.BaseMva}"));
        var buses = new HashSet<int>(model.Buses.Select(b => b.Id));
        foreach (HarmonicMeasurementOrder o in Orders)
        {
            foreach (int c in o.CandidateBuses)
                if (!buses.Contains(c)) problems.Add($"order {o.Order}: candidate bus {c} is not in the case");
            foreach (HarmonicVoltageMeasurement m in o.Voltages)
                if (!buses.Contains(m.Bus)) problems.Add($"order {o.Order}: measured bus {m.Bus} is not in the case");
        }
        if (problems.Count > 0) throw new InvalidDataException("harmonic measurement set: " + string.Join("; ", problems));
    }

    private static void Keys(JsonElement e, string where, params string[] allowed)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{where} must be an object.");
        foreach (JsonProperty p in e.EnumerateObject())
            if (Array.IndexOf(allowed, p.Name) < 0)
                throw new InvalidDataException($"{where}: unknown key '{p.Name}' (allowed: {string.Join(", ", allowed)}).");
    }

    private static string Str(JsonElement e, string key, string where)
    {
        if (!e.TryGetProperty(key, out JsonElement v)) throw new InvalidDataException($"{where}: \"{key}\" is required.");
        if (v.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{where}: \"{key}\" must be a string.");
        return v.GetString()!;
    }

    private static double Num(JsonElement e, string key, string where)
    {
        if (!e.TryGetProperty(key, out JsonElement v)) throw new InvalidDataException($"{where}: \"{key}\" is required.");
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double d) || !double.IsFinite(d))
            throw new InvalidDataException($"{where}: \"{key}\" must be a finite number.");
        return d;
    }

    private static int Int(JsonElement e, string key, string where)
    {
        if (!e.TryGetProperty(key, out JsonElement v)) throw new InvalidDataException($"{where}: \"{key}\" is required.");
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int i)) throw new InvalidDataException($"{where}: \"{key}\" must be a whole number.");
        return i;
    }
}
