using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GridSim.Core.Compliance.GridCode;

/// <summary>The module type of a power generating module (RfG Art. 5; GB ECC uses the same types).</summary>
public enum ModuleType { B, C, D }

/// <summary>The technology of the unit under test, in the study file's spelling.</summary>
public static class GridCodeTechnology
{
    public const string Synchronous = "synchronous", Ppm = "ppm", Hvdc = "hvdc", DcPpm = "dc-ppm", PpmGridForming = "ppm-gfm";

    /// <summary>The five spellings.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Synchronous, Ppm, Hvdc, DcPpm, PpmGridForming };

    /// <summary>True for the converter-connected technologies.</summary>
    public static bool IsConverter(string technology) => technology != Synchronous;
}

/// <summary>A fixed GB FRT profile with the modules it applies to.</summary>
public sealed record GbFrtProfileEntry(FrtProfile Profile, string AppliesTo, IReadOnlyList<string> ModuleTypes, IReadOnlyList<string> Technologies,
    double? KvAtLeast, double? KvBelow);

/// <summary>A fixed GB reactive requirement: |Q| at least <see cref="QPerPmax"/> x Pmax on each side at each test voltage.</summary>
public sealed record GbReactiveEntry(string Clause, string AppliesTo, IReadOnlyList<string> ModuleTypes, IReadOnlyList<string> Technologies,
    double PowerFactor, double QPerPmax, IReadOnlyList<double> TestVoltagesPu, bool FigureRead);

/// <summary>One bound of an RfG range: a number, or the name of another profile parameter.</summary>
public sealed record RfgBound(double? Value, string? Parameter)
{
    public override string ToString() => Parameter ?? Value!.Value.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>An RfG parameter range: every lower bound and every upper bound must hold; <see cref="MaxExtended"/> replaces
/// the upper bounds when the study asks for the extended clearing time.</summary>
public sealed record RfgParameterRange(string Name, IReadOnlyList<RfgBound> Min, IReadOnlyList<RfgBound> Max, IReadOnlyList<RfgBound>? MaxExtended, string Printed);

/// <summary>An RfG FRT table (3.1, 3.2, 7.1, 7.2).</summary>
public sealed record RfgFrtTable(string Table, string Clause, IReadOnlyList<string> Technologies, double? KvAtLeast, double? KvBelow,
    IReadOnlyDictionary<string, RfgParameterRange> Parameters);

/// <summary>One row of a frequency withstand table: the band, its minimum time (null = unlimited) and the text as printed.</summary>
public sealed record FrequencyBand(string Area, double LowHz, double HighHz, double? MinSeconds, string Printed);

/// <summary>A RoCoF withstand requirement.</summary>
public sealed record RocofRequirement(string Clause, IReadOnlyList<string> Technologies, double LimitHzPerSecond, double WindowSeconds);

/// <summary>An RfG Table 8 / 9 row.</summary>
public sealed record ReactiveLimitRow(string Area, string Printed, double QSpanPerPmax, double VoltageSpanPu);

/// <summary>An RfG reactive limit table (Table 8 or 9).</summary>
public sealed record ReactiveLimitTable(string Clause, IReadOnlyList<string> Technologies, IReadOnlyList<ReactiveLimitRow> Rows);

/// <summary>
/// One edition of a grid code's requirement data (<c>gridsim-gridcode-requirements/1</c>), read from the embedded file
/// <c>Compliance/GridCode/Requirements/&lt;id&gt;.json</c>. Every value carries the clause it was read from. The GB file holds
/// fixed values; the RfG file holds ranges the TSO chooses inside, checked by <see cref="RfgRanges"/>.
/// </summary>
public sealed class GridCodeRequirementSet
{
    public const string Schema = "gridsim-gridcode-requirements/1";

    public required string Id { get; init; }
    public required string Code { get; init; }
    public required string Title { get; init; }
    public required string FrtShape { get; init; }
    public IReadOnlyList<GbFrtProfileEntry> GbProfiles { get; init; } = Array.Empty<GbFrtProfileEntry>();
    public IReadOnlyList<RfgFrtTable> RfgTables { get; init; } = Array.Empty<RfgFrtTable>();
    public IReadOnlyList<GbReactiveEntry> GbReactive { get; init; } = Array.Empty<GbReactiveEntry>();
    public string? ReactiveNotTranscribed { get; init; }
    public string? ReactiveNotTranscribedClause { get; init; }
    public IReadOnlyList<ReactiveLimitTable> ReactiveLimits { get; init; } = Array.Empty<ReactiveLimitTable>();
    public IReadOnlyList<RocofRequirement> Rocof { get; init; } = Array.Empty<RocofRequirement>();
    public string? RocofClause { get; init; }
    public required string FrequencyBandsClause { get; init; }
    public required IReadOnlyList<FrequencyBand> FrequencyBands { get; init; }
    public required string NoTripClause { get; init; }
    public required double NoTripLowHz { get; init; }
    public required double NoTripHighHz { get; init; }
    /// <summary>The named numbers of the frequency-response and recovery blocks, keyed "block.name" (for example
    /// <c>lfsmO.thresholdHz</c>), with each block's clause under "block.clause".</summary>
    public required IReadOnlyDictionary<string, double> Numbers { get; init; }
    public required IReadOnlyDictionary<string, string> Clauses { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> ModuleTypesOf { get; init; }

    /// <summary>A number of a block, refused by name when the file does not carry it.</summary>
    public double Number(string key) => Numbers.TryGetValue(key, out double v) ? v
        : throw new InvalidDataException($"requirement set '{Id}' gives no value '{key}'.");

    /// <summary>The clause of a block.</summary>
    public string Clause(string block) => Clauses.TryGetValue(block, out string? c) ? c
        : throw new InvalidDataException($"requirement set '{Id}' has no '{block}' block.");

    /// <summary>True when the block applies to the module type (a block without a module list applies to all).</summary>
    public bool Applies(string block, ModuleType type) =>
        !ModuleTypesOf.TryGetValue(block, out IReadOnlyList<string>? types) || types.Contains(type.ToString(), StringComparer.Ordinal);
}

/// <summary>The built-in grid-code requirement files and the GB profile selection.</summary>
public static class GridCodeRequirements
{
    private const string Prefix = "GridSim.Core.gridcode.";

    /// <summary>The ids of the embedded requirement files, sorted.</summary>
    public static IReadOnlyList<string> BuiltInIds { get; } =
        typeof(GridCodeRequirements).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .Select(n => n[Prefix.Length..^5]).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    /// <summary>The file id of a code name: <c>gb-ecc</c> or <c>eu-rfg</c>.</summary>
    public static string IdOfCode(string code) => code switch
    {
        "gb-ecc" => "gb-ecc-issue6-rev33",
        "eu-rfg" => "eu-rfg-2016-631",
        _ => throw new InvalidDataException($"code '{code}' is not one of gb-ecc, eu-rfg."),
    };

    /// <summary>The raw bytes of an embedded requirement file.</summary>
    public static byte[] Bytes(string id)
    {
        using Stream? s = typeof(GridCodeRequirements).Assembly.GetManifestResourceStream(Prefix + id + ".json")
            ?? throw new ArgumentException($"no built-in grid-code requirement set '{id}' (built in: {string.Join(", ", BuiltInIds)})");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Load and parse an embedded requirement file.</summary>
    public static GridCodeRequirementSet Load(string id) => Parse(Bytes(id));

    /// <summary>
    /// The fixed GB FRT profile of a module (ECC.6.3.15.2 to .7): synchronous B (.2), synchronous C/D below 110 kV (.3),
    /// synchronous D at or above 110 kV (.4), PPM below 110 kV (.5), PPM D at or above 110 kV and DC-connected PPM (.6), HVDC (.7).
    /// A type B or C module connected at or above 110 kV is refused (such a module is type D).
    /// </summary>
    public static FrtProfile GbProfile(ModuleType moduleType, string technology, double connectionKv) =>
        GbProfileEntry(Load("gb-ecc-issue6-rev33"), moduleType, technology, connectionKv).Profile;

    internal static GbFrtProfileEntry GbProfileEntry(GridCodeRequirementSet gb, ModuleType moduleType, string technology, double connectionKv)
    {
        CheckModule(moduleType, technology, connectionKv);
        string mt = moduleType.ToString();
        foreach (GbFrtProfileEntry e in gb.GbProfiles)
        {
            if (!e.ModuleTypes.Contains(mt, StringComparer.Ordinal) || !e.Technologies.Contains(technology, StringComparer.Ordinal)) continue;
            if (e.KvAtLeast is double lo && !(connectionKv >= lo)) continue;
            if (e.KvBelow is double hi && !(connectionKv < hi)) continue;
            return e;
        }
        throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
            $"no GB FRT profile applies to a type {mt} {technology} module at {connectionKv:R} kV."));
    }

    /// <summary>The RfG table of a module: 3.1 / 3.2 for types B and C and for type D below 110 kV, 7.1 / 7.2 for type D at
    /// or above 110 kV. HVDC systems and DC-connected PPMs are outside Regulation 2016/631 and are refused.</summary>
    public static RfgFrtTable RfgTable(GridCodeRequirementSet rfg, ModuleType moduleType, string technology, double connectionKv)
    {
        CheckModule(moduleType, technology, connectionKv);
        if (technology is GridCodeTechnology.Hvdc or GridCodeTechnology.DcPpm)
            throw new InvalidDataException($"technology '{technology}' is outside Regulation (EU) 2016/631 (HVDC systems and DC-connected power park modules are in Regulation (EU) 2016/1447, which is not shipped).");
        bool high = moduleType == ModuleType.D && connectionKv >= 110.0;
        foreach (RfgFrtTable t in rfg.RfgTables)
        {
            if (!t.Technologies.Contains(technology, StringComparer.Ordinal)) continue;
            if (high != (t.KvAtLeast is not null)) continue;
            return t;
        }
        throw new InvalidDataException($"no RfG FRT table applies to a type {moduleType} {technology} module.");
    }

    private static void CheckModule(ModuleType moduleType, string technology, double connectionKv)
    {
        if (!Enum.IsDefined(moduleType)) throw new InvalidDataException("moduleType must be B, C or D.");
        if (!GridCodeTechnology.All.Contains(technology, StringComparer.Ordinal))
            throw new InvalidDataException($"technology '{technology}' is not one of {string.Join(", ", GridCodeTechnology.All)}.");
        if (!(connectionKv > 0.0) || !double.IsFinite(connectionKv)) throw new InvalidDataException("connectionKv must be finite and above 0.");
        if (moduleType != ModuleType.D && connectionKv >= 110.0)
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"a module connected at {connectionKv:R} kV (110 kV or above) is type D (RfG Art. 5(2)(d)), not type {moduleType}."));
    }

    // ------------------------------------------------------------------------------------------------- parsing

    /// <summary>Parse a requirement file; every problem is refused by name.</summary>
    public static GridCodeRequirementSet Parse(byte[] json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement r = doc.RootElement;
        if (Str(r, "schema") != GridCodeRequirementSet.Schema) throw new InvalidDataException($"requirement file: schema must be \"{GridCodeRequirementSet.Schema}\".");
        string id = Str(r, "id");
        var numbers = new SortedDictionary<string, double>(StringComparer.Ordinal);
        var clauses = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var moduleTypes = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (string block in new[] { "lfsmO", "lfsmU", "fsm", "recovery" })
        {
            if (!r.TryGetProperty(block, out JsonElement b)) continue;
            foreach (JsonProperty p in b.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Number) numbers[block + "." + p.Name] = p.Value.GetDouble();
                else if (p.Name == "clause") clauses[block] = p.Value.GetString()!;
                else if (p.Name == "moduleTypes") moduleTypes[block] = Strings(p.Value);
                else if (p.Value.ValueKind == JsonValueKind.String) clauses[block + "." + p.Name] = p.Value.GetString()!;
            }
        }
        var gbProfiles = new List<GbFrtProfileEntry>();
        if (r.TryGetProperty("frtProfiles", out JsonElement fp))
            foreach (JsonElement e in fp.EnumerateArray())
                gbProfiles.Add(new GbFrtProfileEntry(
                    new FrtProfile(Num(e, "uret"), Num(e, "uclear"), Num(e, "urec1"), Num(e, "urec2"), Num(e, "tclear"), Num(e, "trec1"), Num(e, "trec2"), Num(e, "trec3"), Str(e, "clause")),
                    Str(e, "appliesTo"), Strings(e.GetProperty("moduleTypes")), Strings(e.GetProperty("technologies")), OptNum(e, "kvAtLeast"), OptNum(e, "kvBelow")));
        var rfgTables = new List<RfgFrtTable>();
        if (r.TryGetProperty("frtTables", out JsonElement ft))
            foreach (JsonElement e in ft.EnumerateArray())
            {
                var pars = new SortedDictionary<string, RfgParameterRange>(StringComparer.Ordinal);
                foreach (JsonProperty p in e.GetProperty("parameters").EnumerateObject())
                    pars[p.Name] = new RfgParameterRange(p.Name, Bounds(p.Value.GetProperty("min")), Bounds(p.Value.GetProperty("max")),
                        p.Value.TryGetProperty("maxExtended", out JsonElement mx) ? Bounds(mx) : null, Str(p.Value, "printed"));
                rfgTables.Add(new RfgFrtTable(Str(e, "table"), Str(e, "clause"), Strings(e.GetProperty("technologies")), OptNum(e, "kvAtLeast"), OptNum(e, "kvBelow"), pars));
            }
        var gbReactive = new List<GbReactiveEntry>();
        if (r.TryGetProperty("reactive", out JsonElement rq))
            foreach (JsonElement e in rq.EnumerateArray())
                gbReactive.Add(new GbReactiveEntry(Str(e, "clause"), Str(e, "appliesTo"), Strings(e.GetProperty("moduleTypes")), Strings(e.GetProperty("technologies")),
                    Num(e, "powerFactor"), Num(e, "qPerPmax"), e.GetProperty("testVoltagesPu").EnumerateArray().Select(x => x.GetDouble()).ToArray(), e.GetProperty("figureRead").GetBoolean()));
        var limits = new List<ReactiveLimitTable>();
        if (r.TryGetProperty("reactiveLimits", out JsonElement rl))
            foreach (JsonElement e in rl.EnumerateArray())
                limits.Add(new ReactiveLimitTable(Str(e, "clause"), Strings(e.GetProperty("technologies")),
                    e.GetProperty("rows").EnumerateArray().Select(x => new ReactiveLimitRow(Str(x, "area"), Str(x, "printed"), Num(x, "qSpanPerPmax"), Num(x, "voltageSpanPu"))).ToArray()));
        var rocof = new List<RocofRequirement>();
        string? rocofClause = null;
        if (r.TryGetProperty("rocof", out JsonElement rc))
        {
            if (rc.ValueKind == JsonValueKind.Array)
                foreach (JsonElement e in rc.EnumerateArray())
                    rocof.Add(new RocofRequirement(Str(e, "clause"), Strings(e.GetProperty("technologies")), Num(e, "limitHzPerSecond"), Num(e, "windowSeconds")));
            else rocofClause = Str(rc, "clause");
        }
        JsonElement fb = r.GetProperty("frequencyBands");
        var bands = fb.GetProperty("rows").EnumerateArray().Select(x => new FrequencyBand(Str(x, "area"), Num(x, "lowHz"), Num(x, "highHz"), OptNum(x, "minSeconds"), Str(x, "printed"))).ToArray();
        JsonElement nt = r.GetProperty("noTrip");
        string? notTranscribed = null, notTranscribedClause = null;
        if (r.TryGetProperty("reactiveNotTranscribed", out JsonElement rn)) { notTranscribed = Str(rn, "text"); notTranscribedClause = Str(rn, "clause"); }
        return new GridCodeRequirementSet
        {
            Id = id, Code = Str(r, "code"), Title = Str(r, "title"), FrtShape = Str(r, "frtShape"),
            GbProfiles = gbProfiles, RfgTables = rfgTables, GbReactive = gbReactive, ReactiveLimits = limits,
            ReactiveNotTranscribed = notTranscribed, ReactiveNotTranscribedClause = notTranscribedClause,
            Rocof = rocof, RocofClause = rocofClause,
            FrequencyBandsClause = Str(fb, "clause"), FrequencyBands = bands,
            NoTripClause = Str(nt, "clause"), NoTripLowHz = Num(nt, "lowHz"), NoTripHighHz = Num(nt, "highHz"),
            Numbers = numbers, Clauses = clauses, ModuleTypesOf = moduleTypes,
        };
    }

    private static IReadOnlyList<RfgBound> Bounds(JsonElement e) =>
        e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? new RfgBound(null, x.GetString()) : new RfgBound(x.GetDouble(), null)).ToArray();

    private static IReadOnlyList<string> Strings(JsonElement e) => e.EnumerateArray().Select(x => x.GetString()!).ToArray();

    private static string Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new InvalidDataException($"requirement file: '{key}' is required.");

    private static double Num(JsonElement e, string key) =>
        e.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : throw new InvalidDataException($"requirement file: '{key}' must be a number.");

    private static double? OptNum(JsonElement e, string key) =>
        e.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
