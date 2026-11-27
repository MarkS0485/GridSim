using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using GridSim.Core.Model;

namespace GridSim.Core.Dynamics.LongTerm;

/// <summary>WP21 (RC5): a governed unit of a long-term run: its droop and its share of the AGC.</summary>
/// <param name="Generator">The unit's index in <c>model.Generators</c>.</param>
/// <param name="DroopPu">The droop R (pu on the unit's base), above 0. Its response is 1/R x MBase / f0 MW per Hz.</param>
/// <param name="MBaseMva">The unit's base (MVA); null takes the model's machine base rule.</param>
public sealed record LongTermUnit(int Generator, double DroopPu, double? MBaseMva = null);

/// <summary>WP21 (RC5): a disturbance of a long-term run, applied at the first QSS step at or after its time.</summary>
public abstract record LongTermEvent(double AtSeconds)
{
    /// <summary>A short description for the output.</summary>
    public abstract string Describe();

    /// <summary>A number in invariant round-trip form.</summary>
    private protected static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>Branch <paramref name="Branch"/> (index in <c>model.Branches</c>) opens and stays out.</summary>
public sealed record LongTermBranchTrip(double AtSeconds, int Branch) : LongTermEvent(AtSeconds)
{
    /// <inheritdoc/>
    public override string Describe() => $"branch {Branch} trip";
}

/// <summary>Generator <paramref name="Generator"/> (index in <c>model.Generators</c>) is disconnected.</summary>
public sealed record LongTermGeneratorTrip(double AtSeconds, int Generator) : LongTermEvent(AtSeconds)
{
    /// <inheritdoc/>
    public override string Describe() => $"generator {Generator} trip";
}

/// <summary>A constant-power load step at bus <paramref name="BusId"/> (MW, Mvar; negative to shed).</summary>
public sealed record LongTermLoadStep(double AtSeconds, int BusId, double PMw, double QMvar) : LongTermEvent(AtSeconds)
{
    /// <inheritdoc/>
    public override string Describe() => $"load step of {Num(PMw)} MW + j{Num(QMvar)} Mvar at bus {BusId}";
}

/// <summary>Generator <paramref name="Generator"/>'s scheduled active power becomes <paramref name="PMw"/>.</summary>
public sealed record LongTermSetPoint(double AtSeconds, int Generator, double PMw) : LongTermEvent(AtSeconds)
{
    /// <inheritdoc/>
    public override string Describe() => $"generator {Generator} set point {Num(PMw)} MW";
}

/// <summary>
/// WP21 (RC5): the input of a long-term (quasi-steady-state) run, <c>gridsim-long-term/1</c>. Units: seconds, MW, Mvar, Hz,
/// pu. See <see cref="QssEngine"/> for what each part does.
/// </summary>
/// <param name="StepSeconds">The QSS step h (s), above 0.</param>
/// <param name="DurationSeconds">The run length (s), a whole number of steps.</param>
/// <param name="LoadDampingMwPerHz">The load damping D (MW/Hz), 0 or more, shared by the loads in proportion to their
/// starting active power.</param>
/// <param name="Units">The governed units (at least one, or D above 0).</param>
/// <param name="Agc">The AGC, or null for none.</param>
/// <param name="OltcTimers">The timed tap changers. A model tap changer not listed holds its tap.</param>
/// <param name="Loads">The recovering loads.</param>
/// <param name="Events">The disturbances.</param>
public sealed record LongTermCase(double StepSeconds, double DurationSeconds, double LoadDampingMwPerHz,
    IReadOnlyList<LongTermUnit> Units, AgcSpec? Agc, IReadOnlyList<OltcTimerSpec> OltcTimers,
    IReadOnlyList<RecoveringLoadSpec> Loads, IReadOnlyList<LongTermEvent> Events)
{
    /// <summary>The schema tag of the input file.</summary>
    public const string SchemaTag = "gridsim-long-term/1";

    /// <summary>The equilibrium loop stops when the reference bus's generation is within this of its allocation (MW).</summary>
    public double EquilibriumToleranceMw { get; init; } = 1e-6;

    /// <summary>The Newton tolerance of each equilibrium solve (pu).</summary>
    public double PowerFlowTolerancePu { get; init; } = 1e-10;

    /// <summary>The cap on equilibrium passes per step.</summary>
    public int MaxEquilibriumPasses { get; init; } = 30;

    /// <summary>
    /// Read a <c>gridsim-long-term/1</c> document against <paramref name="model"/> (generators and branches by index in
    /// <c>model.Generators</c> / <c>model.Branches</c>, or by their unique name). Unknown members, a wrong schema tag, a
    /// missing required member and a reference that does not resolve are refused with <see cref="InvalidDataException"/>.
    /// </summary>
    public static LongTermCase Parse(byte[] json, GridModel model)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(model);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new InvalidDataException("long-term input: not valid JSON: " + ex.Message, ex); }
        using (doc)
        {
            JsonElement r = doc.RootElement;
            Members(r, "the document", new[] { "schema", "stepSeconds", "durationSeconds", "units" },
                new[] { "loadDampingMwPerHz", "agc", "oltcTimers", "loads", "events", "equilibriumToleranceMw", "powerFlowTolerancePu", "maxEquilibriumPasses" });
            if (Str(r.GetProperty("schema"), "schema") != SchemaTag)
                throw new InvalidDataException($"long-term input: schema must be \"{SchemaTag}\".");
            var units = new List<LongTermUnit>();
            int i = 0;
            foreach (JsonElement u in Arr(r.GetProperty("units"), "units"))
            {
                string w = $"units[{i++}]";
                Members(u, w, new[] { "generator", "droopPu" }, new[] { "mbaseMva" });
                units.Add(new LongTermUnit(Generator(u.GetProperty("generator"), model, w + ".generator"), Num(u.GetProperty("droopPu"), w + ".droopPu"),
                    u.TryGetProperty("mbaseMva", out JsonElement mb) ? Num(mb, w + ".mbaseMva") : null));
            }
            AgcSpec? agc = null;
            if (r.TryGetProperty("agc", out JsonElement a))
            {
                Members(a, "agc", new[] { "kiPerSecond", "units" }, new[] { "biasMwPerHz", "rateLimitMwPerSecond" });
                var au = new List<AgcUnit>();
                i = 0;
                foreach (JsonElement x in Arr(a.GetProperty("units"), "agc.units"))
                {
                    string w = $"agc.units[{i++}]";
                    Members(x, w, new[] { "generator", "participation" }, Array.Empty<string>());
                    au.Add(new AgcUnit(Generator(x.GetProperty("generator"), model, w + ".generator"), Num(x.GetProperty("participation"), w + ".participation")));
                }
                agc = new AgcSpec(Num(a.GetProperty("kiPerSecond"), "agc.kiPerSecond"),
                    a.TryGetProperty("biasMwPerHz", out JsonElement b) ? Num(b, "agc.biasMwPerHz") : null,
                    a.TryGetProperty("rateLimitMwPerSecond", out JsonElement rl) ? Num(rl, "agc.rateLimitMwPerSecond") : null, au);
            }
            var timers = new List<OltcTimerSpec>();
            if (r.TryGetProperty("oltcTimers", out JsonElement ot))
            {
                i = 0;
                foreach (JsonElement x in Arr(ot, "oltcTimers"))
                {
                    string w = $"oltcTimers[{i++}]";
                    Members(x, w, new[] { "transformer", "firstDelaySeconds", "nextDelaySeconds" }, Array.Empty<string>());
                    timers.Add(new OltcTimerSpec(Str(x.GetProperty("transformer"), w + ".transformer"), Num(x.GetProperty("firstDelaySeconds"), w + ".firstDelaySeconds"),
                        Num(x.GetProperty("nextDelaySeconds"), w + ".nextDelaySeconds")));
                }
            }
            var loads = new List<RecoveringLoadSpec>();
            if (r.TryGetProperty("loads", out JsonElement ld))
            {
                i = 0;
                foreach (JsonElement x in Arr(ld, "loads"))
                {
                    string w = $"loads[{i++}]";
                    Members(x, w, new[] { "bus", "tpSeconds", "tqSeconds", "alphaS", "alphaT", "betaS", "betaT" }, Array.Empty<string>());
                    loads.Add(new RecoveringLoadSpec(Int(x.GetProperty("bus"), w + ".bus"), Num(x.GetProperty("tpSeconds"), w + ".tpSeconds"),
                        Num(x.GetProperty("tqSeconds"), w + ".tqSeconds"), Num(x.GetProperty("alphaS"), w + ".alphaS"), Num(x.GetProperty("alphaT"), w + ".alphaT"),
                        Num(x.GetProperty("betaS"), w + ".betaS"), Num(x.GetProperty("betaT"), w + ".betaT")));
                }
            }
            var events = new List<LongTermEvent>();
            if (r.TryGetProperty("events", out JsonElement ev))
            {
                i = 0;
                foreach (JsonElement x in Arr(ev, "events"))
                {
                    string w = $"events[{i++}]";
                    string kind = x.TryGetProperty("kind", out JsonElement k) ? Str(k, w + ".kind") : throw new InvalidDataException($"long-term input: {w} needs a kind.");
                    switch (kind)
                    {
                        case "branchTrip":
                            Members(x, w, new[] { "kind", "atSeconds", "branch" }, Array.Empty<string>());
                            events.Add(new LongTermBranchTrip(Num(x.GetProperty("atSeconds"), w + ".atSeconds"), Branch(x.GetProperty("branch"), model, w + ".branch")));
                            break;
                        case "generatorTrip":
                            Members(x, w, new[] { "kind", "atSeconds", "generator" }, Array.Empty<string>());
                            events.Add(new LongTermGeneratorTrip(Num(x.GetProperty("atSeconds"), w + ".atSeconds"), Generator(x.GetProperty("generator"), model, w + ".generator")));
                            break;
                        case "loadStep":
                            Members(x, w, new[] { "kind", "atSeconds", "bus", "pMw", "qMvar" }, Array.Empty<string>());
                            events.Add(new LongTermLoadStep(Num(x.GetProperty("atSeconds"), w + ".atSeconds"), Int(x.GetProperty("bus"), w + ".bus"),
                                Num(x.GetProperty("pMw"), w + ".pMw"), Num(x.GetProperty("qMvar"), w + ".qMvar")));
                            break;
                        case "setPoint":
                            Members(x, w, new[] { "kind", "atSeconds", "generator", "pMw" }, Array.Empty<string>());
                            events.Add(new LongTermSetPoint(Num(x.GetProperty("atSeconds"), w + ".atSeconds"), Generator(x.GetProperty("generator"), model, w + ".generator"),
                                Num(x.GetProperty("pMw"), w + ".pMw")));
                            break;
                        default:
                            throw new InvalidDataException($"long-term input: {w}.kind \"{kind}\" is not one of branchTrip, generatorTrip, loadStep, setPoint.");
                    }
                }
            }
            var c = new LongTermCase(Num(r.GetProperty("stepSeconds"), "stepSeconds"), Num(r.GetProperty("durationSeconds"), "durationSeconds"),
                r.TryGetProperty("loadDampingMwPerHz", out JsonElement d) ? Num(d, "loadDampingMwPerHz") : 0.0, units, agc, timers, loads, events);
            if (r.TryGetProperty("equilibriumToleranceMw", out JsonElement et)) c = c with { EquilibriumToleranceMw = Num(et, "equilibriumToleranceMw") };
            if (r.TryGetProperty("powerFlowTolerancePu", out JsonElement pt)) c = c with { PowerFlowTolerancePu = Num(pt, "powerFlowTolerancePu") };
            if (r.TryGetProperty("maxEquilibriumPasses", out JsonElement mp)) c = c with { MaxEquilibriumPasses = Int(mp, "maxEquilibriumPasses") };
            return c;
        }
    }

    private static void Members(JsonElement e, string what, string[] required, string[] optional)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"long-term input: {what} must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty p in e.EnumerateObject())
        {
            if (!seen.Add(p.Name)) throw new InvalidDataException($"long-term input: {what} has member \"{p.Name}\" twice.");
            if (Array.IndexOf(required, p.Name) < 0 && Array.IndexOf(optional, p.Name) < 0)
                throw new InvalidDataException($"long-term input: {what} has an unknown member \"{p.Name}\".");
        }
        foreach (string r in required)
            if (!seen.Contains(r)) throw new InvalidDataException($"long-term input: {what} needs \"{r}\".");
    }

    private static IEnumerable<JsonElement> Arr(JsonElement e, string what)
    {
        if (e.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"long-term input: {what} must be an array.");
        return e.EnumerateArray();
    }

    private static double Num(JsonElement e, string what)
    {
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetDouble(out double v) || !double.IsFinite(v))
            throw new InvalidDataException($"long-term input: {what} must be a finite number.");
        return v;
    }

    private static int Int(JsonElement e, string what)
    {
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out int v)) throw new InvalidDataException($"long-term input: {what} must be an integer.");
        return v;
    }

    private static string Str(JsonElement e, string what)
    {
        if (e.ValueKind != JsonValueKind.String) throw new InvalidDataException($"long-term input: {what} must be a string.");
        return e.GetString()!;
    }

    private static int Generator(JsonElement e, GridModel model, string what)
    {
        if (e.ValueKind == JsonValueKind.Number)
        {
            int k = Int(e, what);
            if (k < 0 || k >= model.Generators.Count)
                throw new InvalidDataException($"long-term input: {what} {k} is not an in-service generator (0 to {model.Generators.Count - 1}).");
            return k;
        }
        string name = Str(e, what);
        int found = -1;
        for (int k = 0; k < model.Generators.Count; k++)
            if (string.Equals(model.Generators[k].Name, name, StringComparison.Ordinal))
            {
                if (found >= 0) throw new InvalidDataException($"long-term input: {what} \"{name}\" names more than one generator.");
                found = k;
            }
        return found >= 0 ? found : throw new InvalidDataException($"long-term input: {what} \"{name}\" is not an in-service generator of the case.");
    }

    private static int Branch(JsonElement e, GridModel model, string what)
    {
        if (e.ValueKind == JsonValueKind.Number)
        {
            int k = Int(e, what);
            if (k < 0 || k >= model.Branches.Count)
                throw new InvalidDataException($"long-term input: {what} {k} is not a branch (0 to {model.Branches.Count - 1}).");
            return k;
        }
        string name = Str(e, what);
        int found = -1;
        for (int k = 0; k < model.Branches.Count; k++)
            if (string.Equals(model.Branches[k].Name, name, StringComparison.Ordinal))
            {
                if (found >= 0) throw new InvalidDataException($"long-term input: {what} \"{name}\" names more than one branch.");
                found = k;
            }
        return found >= 0 ? found : throw new InvalidDataException($"long-term input: {what} \"{name}\" is not a branch of the case.");
    }
}
