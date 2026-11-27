using System;
using System.Collections.Generic;
using System.Text.Json;

namespace GridSim.Core.Protection.ShortCircuit.Ena;

/// <summary>Per-generator decay data (reactances pu on the generator's MBase, short-circuit time constants in s).
/// The subtransient reactance is NOT repeated here: it is the case's own X''d (it must be a real value, not the
/// synthesised default).</summary>
public sealed record G74GeneratorData(int Bus, string? Name, double XdTransPu, double XdSyncPu, double TdSubS, double TdTransS);

/// <summary>An induction motor to be added to the fault network at <see cref="Bus"/>.</summary>
public sealed record G74MotorData(
    string Name, int Bus, double RatedMva, double LockedRotorCurrentRatio,
    MotorDecayModel Decay, double TimeConstantS, double RatedMw, double PolePairs)
{
    /// <summary>Rated power per pole pair (MW) for the IEC q factor; NaN when not given.</summary>
    public double MwPerPolePair => RatedMw > 0.0 && PolePairs > 0.0 ? RatedMw / PolePairs : double.NaN;
}

/// <summary>The sidecar a G74 run reads (<c>gridsim-g74-data/1</c>): decay data for generators and induction motors. The
/// case format carries neither machine time constants nor motors, so they come from here; nothing is defaulted.</summary>
public sealed record G74Data(IReadOnlyList<G74GeneratorData> Generators, IReadOnlyList<G74MotorData> Motors)
{
    public const string Schema = "gridsim-g74-data/1";
    public static G74Data Empty { get; } = new(Array.Empty<G74GeneratorData>(), Array.Empty<G74MotorData>());

    /// <summary>Parse and range-check a data document. Throws <see cref="FormatException"/> with every problem found.</summary>
    public static G74Data Parse(string json)
    {
        var problems = new List<string>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new FormatException("g74 data is not valid JSON: " + ex.Message); }
        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("g74 data must be a JSON object");
            foreach (JsonProperty p in root.EnumerateObject())
                if (p.Name is not ("schema" or "generators" or "motors")) problems.Add($"unknown key '{p.Name}'");
            if (!root.TryGetProperty("schema", out JsonElement sch) || sch.ValueKind != JsonValueKind.String || sch.GetString() != Schema)
                problems.Add($"schema must be \"{Schema}\"");
            var gens = new List<G74GeneratorData>();
            if (root.TryGetProperty("generators", out JsonElement ge) && ge.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (JsonElement e in ge.EnumerateArray())
                {
                    string w = $"generators[{i++}]";
                    int bus = BusNumber(e, w, problems);
                    string? name = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("name", out JsonElement ne) && ne.ValueKind == JsonValueKind.String ? ne.GetString() : null;
                    double x1 = Num(e, "xdTransPu", w, problems), xd = Num(e, "xdSyncPu", w, problems);
                    double t2 = Num(e, "tdSubS", w, problems), t1 = Num(e, "tdTransS", w, problems);
                    if (x1 > 0 && xd > 0 && !(x1 < xd)) problems.Add($"{w}: xdTransPu must be below xdSyncPu");
                    if (t2 > 0 && t1 > 0 && !(t2 < t1)) problems.Add($"{w}: tdSubS must be below tdTransS");
                    gens.Add(new G74GeneratorData(bus, name, x1, xd, t2, t1));
                }
            }
            var motors = new List<G74MotorData>();
            if (root.TryGetProperty("motors", out JsonElement me) && me.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (JsonElement e in me.EnumerateArray())
                {
                    string w = $"motors[{i++}]";
                    string name = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("name", out JsonElement ne) && ne.ValueKind == JsonValueKind.String ? ne.GetString()! : "";
                    if (name.Length == 0) problems.Add($"{w}: name is required");
                    int bus = BusNumber(e, w, problems);
                    double mva = Num(e, "ratedMva", w, problems), ilr = Num(e, "lockedRotorCurrentRatio", w, problems);
                    string model = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("decay", out JsonElement de) && de.ValueKind == JsonValueKind.String ? de.GetString()! : "";
                    MotorDecayModel dm = MotorDecayModel.Exponential;
                    double tau = double.NaN, mw = double.NaN, pp = double.NaN;
                    if (model == "exponential") tau = Num(e, "timeConstantS", w, problems);
                    else if (model == "iec60909-q") { dm = MotorDecayModel.Iec60909Q; mw = Num(e, "ratedMw", w, problems); pp = Num(e, "polePairs", w, problems); }
                    else problems.Add($"{w}: decay must be \"exponential\" or \"iec60909-q\" (no default decay is assumed)");
                    motors.Add(new G74MotorData(name, bus, mva, ilr, dm, tau, mw, pp));
                }
            }
            if (problems.Count > 0) throw new FormatException("g74 data: " + string.Join("; ", problems));
            return new G74Data(gens, motors);
        }
    }

    /// <summary>ANA-15: a bus number must be a whole number in the int range ("bus": 2.7 was read as bus 2, and a value out of
    /// range as an unspecified int); a problem line otherwise.</summary>
    private static int BusNumber(JsonElement e, string where, List<string> problems)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("bus", out JsonElement v) || v.ValueKind != JsonValueKind.Number)
        { problems.Add($"{where}: 'bus' is required and must be a number"); return 0; }
        if (v.TryGetInt32(out int bus)) return bus;
        double d = v.GetDouble();   // a whole number written with a point (2.0) is still that bus
        if (double.IsFinite(d) && d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue) return (int)d;
        problems.Add($"{where}: 'bus' must be a whole bus number (got {v.GetRawText()})");
        return 0;
    }

    private static double Num(JsonElement e, string key, string where, List<string> problems, bool allowAnySign = false)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out JsonElement v) || v.ValueKind != JsonValueKind.Number)
        { problems.Add($"{where}: '{key}' is required and must be a number"); return double.NaN; }
        double d = v.GetDouble();
        if (!double.IsFinite(d) || (!allowAnySign && !(d > 0))) { problems.Add($"{where}: '{key}' must be finite and above 0"); return double.NaN; }
        return d;
    }
}
