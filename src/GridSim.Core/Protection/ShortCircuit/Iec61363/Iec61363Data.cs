using System;
using System.Collections.Generic;
using System.Text.Json;

namespace GridSim.Core.Protection.ShortCircuit.Iec61363;

/// <summary>Per-generator data for the ship-system study: reactances pu on the generator's MBase, AC short-circuit time constants and the
/// armature (DC) time constant in seconds. X''d is the case's own (it must be real, not the synthesised default). Nothing is defaulted.</summary>
public sealed record Iec61363GeneratorData(int Bus, string? Name, double XdTransPu, double XdSyncPu, double TdSubS, double TdTransS, double TaS);

/// <summary>An induction motor added to the fault network: locked-rotor current ratio (I_LR / I_rM), the time constant of its AC
/// contribution and of its DC component (s). Both time constants are the owner's; the motor treatment of the standard is AWAITING STANDARD TEXT.</summary>
public sealed record Iec61363MotorData(string Name, int Bus, double RatedMva, double LockedRotorCurrentRatio, double AcTimeConstantS, double TaS);

/// <summary>The sidecar an IEC 61363 run reads (<c>gridsim-iec61363-data/1</c>).</summary>
public sealed record Iec61363Data(IReadOnlyList<Iec61363GeneratorData> Generators, IReadOnlyList<Iec61363MotorData> Motors)
{
    public const string Schema = "gridsim-iec61363-data/1";
    public static Iec61363Data Empty { get; } = new(Array.Empty<Iec61363GeneratorData>(), Array.Empty<Iec61363MotorData>());

    public static Iec61363Data Parse(string json)
    {
        var problems = new List<string>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new FormatException("iec61363 data is not valid JSON: " + ex.Message); }
        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("iec61363 data must be a JSON object");
            foreach (JsonProperty p in root.EnumerateObject())
                if (p.Name is not ("schema" or "generators" or "motors")) problems.Add($"unknown key '{p.Name}'");
            if (!root.TryGetProperty("schema", out JsonElement sch) || sch.ValueKind != JsonValueKind.String || sch.GetString() != Schema)
                problems.Add($"schema must be \"{Schema}\"");
            var gens = new List<Iec61363GeneratorData>();
            if (root.TryGetProperty("generators", out JsonElement ge) && ge.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (JsonElement e in ge.EnumerateArray())
                {
                    string w = $"generators[{i++}]";
                    int bus = BusNumber(e, w, problems);
                    string? name = Str(e, "name") is { Length: > 0 } s ? s : null;
                    double x1 = Num(e, "xdTransPu", w, problems), xd = Num(e, "xdSyncPu", w, problems);
                    double t2 = Num(e, "tdSubS", w, problems), t1 = Num(e, "tdTransS", w, problems), ta = Num(e, "taS", w, problems);
                    if (x1 > 0 && xd > 0 && !(x1 < xd)) problems.Add($"{w}: xdTransPu must be below xdSyncPu");
                    if (t2 > 0 && t1 > 0 && !(t2 < t1)) problems.Add($"{w}: tdSubS must be below tdTransS");
                    gens.Add(new Iec61363GeneratorData(bus, name, x1, xd, t2, t1, ta));
                }
            }
            var motors = new List<Iec61363MotorData>();
            if (root.TryGetProperty("motors", out JsonElement me) && me.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (JsonElement e in me.EnumerateArray())
                {
                    string w = $"motors[{i++}]";
                    string name = Str(e, "name");
                    if (name.Length == 0) problems.Add($"{w}: name is required");
                    motors.Add(new Iec61363MotorData(name, BusNumber(e, w, problems), Num(e, "ratedMva", w, problems),
                        Num(e, "lockedRotorCurrentRatio", w, problems), Num(e, "acTimeConstantS", w, problems), Num(e, "taS", w, problems)));
                }
            }
            if (problems.Count > 0) throw new FormatException("iec61363 data: " + string.Join("; ", problems));
            return new Iec61363Data(gens, motors);
        }
    }

    private static string Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

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
