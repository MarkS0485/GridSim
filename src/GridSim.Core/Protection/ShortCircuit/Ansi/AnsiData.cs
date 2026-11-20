using System;
using System.Collections.Generic;
using System.Text.Json;

namespace GridSim.Core.Protection.ShortCircuit.Ansi;

/// <summary>One fault source's ANSI data: its X/R, whether its current is "local" (generation close enough that its AC component decays) and
/// the reactance multipliers of the first-cycle and interrupting networks. Nothing here is defaulted. The multipliers are the owner's: the
/// table of values per machine type comes from IEEE C37.010 / C37.5 (AWAITING STANDARD TEXT). A null interrupting multiplier makes the
/// interrupting duty OUT OF SCOPE.</summary>
public sealed record AnsiSourceData(
    int Bus, string? Name, string Class, double XOverR, bool Local, double FirstCycleMultiplier, double? InterruptingMultiplier);

/// <summary>A motor to be added: its subtransient reactance on its own base, plus the source data.</summary>
public sealed record AnsiMotorData(string Name, double RatedMva, double XdppPu, AnsiSourceData Source);

/// <summary>The sidecar an ANSI run reads (<c>gridsim-ansi-data/1</c>).</summary>
public sealed record AnsiData(IReadOnlyList<AnsiSourceData> Sources, IReadOnlyList<AnsiMotorData> Motors)
{
    public const string Schema = "gridsim-ansi-data/1";
    public static AnsiData Empty { get; } = new(Array.Empty<AnsiSourceData>(), Array.Empty<AnsiMotorData>());

    public static AnsiData Parse(string json)
    {
        var problems = new List<string>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new FormatException("ansi data is not valid JSON: " + ex.Message); }
        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("ansi data must be a JSON object");
            foreach (JsonProperty p in root.EnumerateObject())
                if (p.Name is not ("schema" or "sources" or "motors")) problems.Add($"unknown key '{p.Name}'");
            if (!root.TryGetProperty("schema", out JsonElement sch) || sch.ValueKind != JsonValueKind.String || sch.GetString() != Schema)
                problems.Add($"schema must be \"{Schema}\"");
            var sources = new List<AnsiSourceData>();
            if (root.TryGetProperty("sources", out JsonElement se) && se.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (JsonElement e in se.EnumerateArray()) sources.Add(Source(e, $"sources[{i++}]", problems, null));
            }
            var motors = new List<AnsiMotorData>();
            if (root.TryGetProperty("motors", out JsonElement me) && me.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (JsonElement e in me.EnumerateArray())
                {
                    string w = $"motors[{i++}]";
                    string name = Str(e, "name");
                    if (name.Length == 0) problems.Add($"{w}: name is required");
                    double mva = Num(e, "ratedMva", w, problems), xd = Num(e, "xdppPu", w, problems);
                    motors.Add(new AnsiMotorData(name, mva, xd, Source(e, w, problems, name)));
                }
            }
            if (problems.Count > 0) throw new FormatException("ansi data: " + string.Join("; ", problems));
            return new AnsiData(sources, motors);
        }
    }

    private static AnsiSourceData Source(JsonElement e, string w, List<string> problems, string? forcedName)
    {
        int bus = (int)Num(e, "bus", w, problems, true);
        string? name = forcedName ?? (Str(e, "name") is { Length: > 0 } s ? s : null);
        string cls = Str(e, "class");
        if (cls.Length == 0) problems.Add($"{w}: 'class' (a label for the machine type) is required");
        double xr = Num(e, "xOverR", w, problems);
        bool local = false;
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("local", out JsonElement le) && (le.ValueKind == JsonValueKind.True || le.ValueKind == JsonValueKind.False))
            local = le.GetBoolean();
        else problems.Add($"{w}: 'local' (true or false) is required");
        double m1 = Num(e, "firstCycleMultiplier", w, problems);
        double? m2 = null;
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("interruptingMultiplier", out JsonElement ie))
        {
            if (ie.ValueKind != JsonValueKind.Null) m2 = Num(e, "interruptingMultiplier", w, problems);
        }
        else problems.Add($"{w}: 'interruptingMultiplier' is required (a number, or null when the value is not known)");
        return new AnsiSourceData(bus, name, cls, xr, local, m1, m2);
    }

    private static string Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static double Num(JsonElement e, string key, string where, List<string> problems, bool allowAnySign = false)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out JsonElement v) || v.ValueKind != JsonValueKind.Number)
        { problems.Add($"{where}: '{key}' is required and must be a number"); return double.NaN; }
        double d = v.GetDouble();
        if (!double.IsFinite(d) || (!allowAnySign && !(d > 0))) { problems.Add($"{where}: '{key}' must be finite and above 0"); return double.NaN; }
        return d;
    }
}
