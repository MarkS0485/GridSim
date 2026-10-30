using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dispatch;
using GridSim.Core.Model;
using GridSim.Core.Simulation;

namespace GridSim.Core.Gda;

/// <summary>
/// One historical timestamp's demand state: a system-wide load factor and, optionally, per-bus factors (the real
/// per-timestamp GDA path, data-gated). <see cref="ApplyTo"/> turns it into a balanced operating case ready for
/// the power flow.
/// </summary>
public sealed record GdaTimeStep(
    DateTime TimeUtc,
    double SystemLoadFactor,
    IReadOnlyDictionary<int, double>? PerBusFactor = null)
{
    /// <summary>Derive the point-in-time operating case: scale demand, then balance the slack.</summary>
    public GridModel ApplyTo(GridModel baseModel)
    {
        if (PerBusFactor is null || PerBusFactor.Count == 0)
            return GridScenario.AtLoad(baseModel, SystemLoadFactor);

        // GR-159: every per-bus factor must name a bus of the model and be a finite number, 0 or above. A key naming no
        // bus was ignored and a NaN or negative factor applied, both silently.
        var ids = new HashSet<int>(baseModel.Buses.Select(b => b.Id));
        foreach (KeyValuePair<int, double> kv in PerBusFactor)
        {
            if (!ids.Contains(kv.Key))
                throw new InvalidDataException($"GDA time step {TimeUtc:O}: perBusFactor names bus {kv.Key}, which is not in the model.");
            if (!(double.IsFinite(kv.Value) && kv.Value >= 0))
                throw new InvalidDataException($"GDA time step {TimeUtc:O}: perBusFactor for bus {kv.Key} is {kv.Value}; it must be a finite number, 0 or above.");
        }

        // Real per-bus path: scale each bus by its own recorded factor, then balance.
        var buses = baseModel.Buses
            .Select(b => PerBusFactor.TryGetValue(b.Id, out double f) ? b with { Pd = b.Pd * f, Qd = b.Qd * f } : b)
            .ToList();
        // CopyWith carries asset dynamics and protections (R264): this per-bus path used to drop both while the
        // system-factor path (GridScenario.AtLoad) kept them, so replay behaviour depended on which path ran.
        GridModel scaled = baseModel.CopyWith(buses: buses);
        return Dispatcher.Balance(scaled);
    }
}

/// <summary>
/// A historical demand time-series over a base network - the input to the offline state estimator
/// (<see cref="Simulation.GdaReplayLoop"/>). <b>Every timestamp is checked against the historical horizon</b> on
/// load, so the series can only ever describe the past. Ships a <see cref="Synthetic"/> generator (from
/// <see cref="LoadProfile.DailyFactor"/>) so the estimator runs today; drop real per-timestamp GDA demand in later.
/// </summary>
public sealed record GdaTimeSeries(string Name, IReadOnlyList<GdaTimeStep> Steps)
{
    // Phase 0.1 (source-generated JSON, so the core can run under NativeAOT): exactly the options this file has always
    // used, now bound to the source-generated GdaTimeSeriesJsonContext (below) so no runtime reflection is needed.
    // Internal, not private, so JsonSourceGenEquivalenceTests reads and writes through this very instance.
    internal static readonly GdaTimeSeriesJsonContext Json = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // An authored input: a misspelt key ("perBusFactors") is refused rather than ignored (it applied the system
        // factor to every bus).
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    });

    /// <summary>A synthetic daily-shape series (trough 04:00, peak 16:00) for demonstrating the estimator.</summary>
    public static GdaTimeSeries Synthetic(string name, DateTime startUtc, int steps, double stepMinutes, double baseFactor = 1.0)
    {
        var list = new List<GdaTimeStep>(Math.Max(0, steps));
        for (int i = 0; i < steps; i++)
        {
            DateTime t = startUtc.AddMinutes(i * stepMinutes);
            list.Add(new GdaTimeStep(t, baseFactor * LoadProfile.DailyFactor(t.TimeOfDay.TotalHours)));
        }
        return new GdaTimeSeries(name, list);
    }

    /// <summary>Load a series from JSON, enforcing the historical horizon on every timestamp.</summary>
    public static GdaTimeSeries Load(string path, DateTime? utcNow = null)
    {
        // N256: the step fields are checked on the raw document FIRST, so a step with a null timeUtc is refused naming
        // the step (the deserialiser would otherwise fail first with a bare JsonException).
        RequireStepFields(path);
        GdaTimeSeries s = JsonSerializer.Deserialize(File.ReadAllText(path), Json.GdaTimeSeries)
            ?? throw new InvalidDataException($"Not a GDA time-series: {path}");
        // D-DOC-177 (October 2026, docs-session finding): System.Text.Json binds a positional record's missing member to
        // null, so a file with no "steps" array (an unrelated JSON object, a typo such as "step") loaded with Steps null
        // and the horizon check below then failed with the framework's "Value cannot be null. (Parameter 'source')",
        // which names neither the file nor the problem. Refused here instead, naming both; a null entry inside the array
        // is refused the same way. An empty array is a series of no steps, as before.
        if (s.Steps is null)
            throw new InvalidDataException(
                $"'{path}' is not a GDA time-series: it has no \"steps\" array (expected {{ \"name\": ..., \"steps\": [ {{ \"timeUtc\": ..., \"systemLoadFactor\": ... }} ] }}).");
        for (int i = 0; i < s.Steps.Count; i++)
            if (s.Steps[i] is null)
                throw new InvalidDataException($"'{path}': steps[{i}] is null; every step needs a timeUtc and a systemLoadFactor.");
        ForwardInferenceGuard.AssertHistorical(s.Steps.Select(x => x.TimeUtc), utcNow, "GDA time-series");
        return s;
    }

    /// <summary>
    /// N256 (October 2026): every step must WRITE its "timeUtc" and its "systemLoadFactor" (key case is free, as the
    /// reader's), each with a non-null value. System.Text.Json fills a positional record's missing member with its
    /// default, so a step with no timeUtc read as 0001-01-01 (which then passed the horizon check, being in the past)
    /// and a step with no systemLoadFactor read as 0, a network with no load, both without a word. Each is refused,
    /// naming the file, the step index and the missing key (up to twenty steps listed). A load factor that is written
    /// must also be a finite number not below 0.
    /// </summary>
    private static void RequireStepFields(string path)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return;   // not a series object: the reader refuses it
        JsonElement? steps = null;
        foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            if (p.Name.Equals("steps", StringComparison.OrdinalIgnoreCase)) steps = p.Value;
        if (steps is not { ValueKind: JsonValueKind.Array } arr) return;   // already refused above when absent

        var bad = new List<string>();
        int i = 0;
        foreach (JsonElement step in arr.EnumerateArray())
        {
            if (step.ValueKind == JsonValueKind.Null) { i++; continue; }   // a null step is refused by name after reading
            JsonElement? time = null, factor = null;
            if (step.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty p in step.EnumerateObject())
                {
                    if (p.Name.Equals("timeUtc", StringComparison.OrdinalIgnoreCase)) time = p.Value;
                    else if (p.Name.Equals("systemLoadFactor", StringComparison.OrdinalIgnoreCase)) factor = p.Value;
                }
            if (time is not { } t || t.ValueKind == JsonValueKind.Null) bad.Add($"steps[{i}] has no timeUtc");
            if (factor is not { } f || f.ValueKind == JsonValueKind.Null) bad.Add($"steps[{i}] has no systemLoadFactor");
            else if (f.ValueKind != JsonValueKind.Number || !(f.GetDouble() >= 0) || !double.IsFinite(f.GetDouble()))
                bad.Add($"steps[{i}] has systemLoadFactor {f.GetRawText()} (a finite number of 0 or more is needed)");
            i++;
        }
        if (bad.Count > 0)
            throw new InvalidDataException(
                $"'{path}': {bad.Count} step problem(s); every step needs a timeUtc and a systemLoadFactor (a missing one used to " +
                $"read as 0001-01-01 or as zero load): {string.Join("; ", bad.Take(20))}{(bad.Count > 20 ? "; ..." : "")}.");
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json.GdaTimeSeries));
    }
}

/// <summary>
/// Phase 0.1: the source-generated JSON metadata for a GDA time series and every type it reaches, so it is read and
/// written without runtime reflection (NativeAOT). Metadata mode only: the bytes come from the same converters the
/// reflection path used, never from a separately generated fast path.
/// The runtime options (naming, key case, null and number handling, indentation) are passed in by the call site.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(GdaTimeSeries))]
internal sealed partial class GdaTimeSeriesJsonContext : JsonSerializerContext;
