using System.Buffers;
using System.Collections;
using System.Reflection;
using System.Text.Json;
using GridSim.Core.Assets;
using GridSim.Core.IO.Json;

namespace GridSim.Tests.V2;

/// <summary>
/// Everything one v1 case folder holds that <see cref="JsonGridLoader"/> reads, as the loader's own DTOs. A file
/// that is absent gives an empty list (or a null system), exactly as the loader's ReadArray / ReadOne do.
/// </summary>
internal sealed class V1Case
{
    public SystemDto? System { get; set; }
    public List<BusDto> Buses { get; set; } = new();
    public List<GenDto> Generators { get; set; } = new();
    public List<DemandDto> Demands { get; set; } = new();
    public List<LineDto> Lines { get; set; } = new();
    public List<TransformerDto> Transformers { get; set; } = new();
    public List<OltcDto> Oltcs { get; set; } = new();
    public List<BoundaryDto> Boundaries { get; set; } = new();
    public List<AssetClassDto> AssetClasses { get; set; } = new();
    public List<AssetBindingDto> AssetBindings { get; set; } = new();
    public List<ProtectionInstanceDto> Protections { get; set; } = new();
}

/// <summary>
/// A v1 case projected to v2, in memory: the case manifest (gridsim-case/2) and its single network file
/// (gridsim-network/2), each as UTF-8 JSON bytes.
/// </summary>
internal sealed record V2Projection(byte[] CaseJson, byte[] NetworkJson);

/// <summary>
/// TEST-ONLY projector between the v1 JSON case format and the v2 schema (dream-of-json-ification Phase 1,
/// slice 4). Nothing in production reads v2, so this lives in the test project and touches no numeric path.
///
/// What it does:
/// <list type="bullet">
/// <item><see cref="ReadV1"/> reads a v1 case folder into the loader's own DTOs, with the loader's own serializer
/// options (taken from JsonGridLoader by reflection, so the two can never drift).</item>
/// <item><see cref="Project"/> writes those DTOs as a v2 case manifest plus one v2 network file.</item>
/// <item><see cref="Back"/> reads a v2 case manifest plus network file back into v1 DTOs, refusing loudly any v2
/// content v1 cannot carry.</item>
/// <item><see cref="Differences"/> compares two DTO graphs field for field (doubles by their bits).</item>
/// </list>
///
/// The projection rules (also stated in network.schema.json and case.schema.json):
/// <list type="bullet">
/// <item>ABSENCE RULE. An optional v2 member that is absent takes exactly the v1 DTO default. So the projector
/// omits a value that is bit-for-bit equal to the DTO default (a fresh <c>new XDto()</c>), and the back-mapper
/// starts from a fresh DTO and sets only what is present. Members the v2 schema requires are always written.</item>
/// <item>system.json becomes the case's name and system block; asset-classes.json becomes the case's
/// localTemplates (id <c>gridsim.local.&lt;class name&gt;</c>, revision 1, status active); asset-bindings.json
/// becomes the case's bindings; every other file becomes an array of the one network file.</item>
/// <item>A v1 class name used by an element (dynamicsClass, protection class, binding class) becomes the pinned
/// reference <c>gridsim.local.&lt;class name&gt;@1</c>.</item>
/// <item>A v1 class with no <c>_default</c> provenance gets the provenance v1 itself falls back to for such a class
/// (<see cref="AssetClass.ProvenanceFor"/>), because a v2 template requires <c>_default</c>; the back-mapper drops
/// exactly that synthesised entry again.</item>
/// </list>
/// </summary>
internal static class V1Projector
{
    /// <summary>The id prefix of a template projected from a v1 asset class.</summary>
    public const string LocalIdPrefix = "gridsim.local.";

    /// <summary>The revision every projected v1 class gets.</summary>
    public const int LocalRev = 1;

    /// <summary>The network file name the projected case manifest lists.</summary>
    public const string NetworkFileName = "network.json";

    /// <summary>
    /// The provenance v1 gives a parameter of a class that has neither its own entry nor a <c>_default</c>
    /// (<see cref="AssetClass.ProvenanceFor"/>, the literal in AssetClassLibrary.cs). Repeated here because it is
    /// an inline literal there, not a constant; a test pins the two together, so they cannot drift silently.
    /// </summary>
    public const string V1FallbackTag = "CuratedPlaceholder";

    /// <summary>The note half of <see cref="V1FallbackTag"/>.</summary>
    public const string V1FallbackNote = "[VERIFY] unspecified provenance";

    /// <summary>
    /// v1 asset-class kind (the canonical spelling the bundled data uses) to v2 template kind, one to one. v1
    /// aliases (gfl, grid-forming, relay, cable, ...) are refused rather than normalised, because normalising would
    /// make the round trip unequal. NOTE FOR MAIN: the v2 kind names for the v1 'transformer' and 'line' classes
    /// (oltc, lineType; transformer changed from transformerType to oltc at the merge) are this slice's choice and must agree with the slice 3 kind specs and their
    /// coverage test; this table is the one place to change.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> KindV1ToV2 = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["gridFollowingInverter"] = "gridFollowingInverter",
        ["gridFormingInverter"] = "gridFormingInverter",
        ["synchronousMachine"] = "synchronousMachine",
        ["transformer"] = "oltc",   // main at merge: v1 transformer classes carry tap-changer parameters, which slice 3 declares on oltc
        ["line"] = "lineType",
        ["protection"] = "protection",
    };

    /// <summary>The v1 files JsonGridLoader reads. Anything else in a case folder is not part of the case.</summary>
    public static readonly IReadOnlyList<string> LoaderFiles = new[]
    {
        "system.json", "buses.json", "generation.json", "demand.json", "transmission.json", "transformer.json",
        "onloadtapchanger.json", "asset-classes.json", "asset-bindings.json", "protections.json", "boundaries.json",
    };

    /// <summary>
    /// Every property of every v1 DTO this projector maps, by DTO type. A test compares this with the DTO types
    /// by reflection, so a field added to v1 later fails that test instead of being silently dropped.
    /// </summary>
    public static readonly IReadOnlyDictionary<Type, string[]> HandledMembers = new Dictionary<Type, string[]>
    {
        [typeof(SystemDto)] = new[] { "Name", "BaseMva", "FrequencyHz", "Dispatch" },
        [typeof(BusDto)] = new[] { "Id", "Name", "BaseKv", "Type", "Vm", "VaDeg", "Gs", "Bs", "X", "Y", "Zip", "VminPu", "VmaxPu" },
        [typeof(BusZipDto)] = new[] { "Ap", "Bp", "Cp", "Aq", "Bq", "Cq" },
        [typeof(GenDto)] = new[] { "Name", "Bus", "Type", "CapacityMva", "OutputMw", "CapacityFactor", "VoltageSetpoint",
            "QMinMvar", "QMaxMvar", "InertiaH", "Role", "DynamicsClass", "ConverterInterfaced", "ConverterFaultCurrentPu",
            "PowerFlowOnly", "UnitTransformerXPu", "GridForming", "InService", "PMinMw", "PMaxMw", "QOutputMvar" },
        [typeof(DemandDto)] = new[] { "Name", "Bus", "PMw", "QMvar", "PowerFactor" },
        [typeof(LineDto)] = new[] { "Name", "From", "To", "RPu", "XPu", "BPu", "RatingMva", "Circuits", "DynamicsClass",
            "LengthKm", "Equipment", "RPu0", "XPu0", "TapRatio", "PhaseShiftDeg", "IsTransformer", "RatingBMva", "RatingCMva" },
        [typeof(TransformerDto)] = new[] { "Name", "From", "To", "RPu", "XPu", "TapRatio", "PhaseShiftDeg", "RatingMva", "DynamicsClass",
            "RatingBMva", "RatingCMva" },   // N119 (October 2026): TransformerDto gained the B / C ratings
        [typeof(OltcDto)] = new[] { "Transformer", "ControlsBus", "TargetVpu", "MinTap", "MaxTap", "StepPu", "DeadbandPu" },
        [typeof(BoundaryDto)] = new[] { "Name", "LimitMw", "Branches" },
        [typeof(BoundaryBranchDto)] = new[] { "Branch", "Direction" },
        [typeof(AssetClassDto)] = new[] { "Name", "Kind", "Mode", "Params", "Provenance" },
        [typeof(ProvenanceDto)] = new[] { "Tag", "Note" },
        [typeof(AssetBindingDto)] = new[] { "MatchType", "Class" },
        [typeof(ProtectionInstanceDto)] = new[] { "Name", "Class", "MonitorBus", "CapacityMw", "TargetBranch" },
    };

    // ------------------------------------------------------------------------------------------------------------
    // Reading v1
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// JsonGridLoader's own (private) serializer options, fetched by reflection so this reader parses a v1 file
    /// exactly as the loader does (case-insensitive keys, comments, trailing commas, unknown keys refused). If the
    /// field is ever renamed this throws, loudly, rather than falling back to different options.
    /// </summary>
    public static JsonSerializerOptions LoaderOptions { get; } =
        (JsonSerializerOptions)(typeof(JsonGridLoader).GetField("Opt", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
            ?? throw new InvalidOperationException("JsonGridLoader.Opt not found: the projector must read v1 with the loader's own options."));

    /// <summary>Read a v1 case folder into DTOs, file by file, as JsonGridLoader.LoadDirectory does.</summary>
    public static V1Case ReadV1(string dir)
    {
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"Case directory not found: {dir}");
        return new V1Case
        {
            System = ReadOne<SystemDto>(dir, "system.json"),
            Buses = ReadArray<BusDto>(dir, "buses.json"),
            Generators = ReadArray<GenDto>(dir, "generation.json"),
            Demands = ReadArray<DemandDto>(dir, "demand.json"),
            Lines = ReadArray<LineDto>(dir, "transmission.json"),
            Transformers = ReadArray<TransformerDto>(dir, "transformer.json"),
            Oltcs = ReadArray<OltcDto>(dir, "onloadtapchanger.json"),
            Boundaries = ReadArray<BoundaryDto>(dir, "boundaries.json"),
            AssetClasses = ReadArray<AssetClassDto>(dir, "asset-classes.json"),
            AssetBindings = ReadArray<AssetBindingDto>(dir, "asset-bindings.json"),
            Protections = ReadArray<ProtectionInstanceDto>(dir, "protections.json"),
        };
    }

    private static T? ReadOne<T>(string dir, string file) where T : class
    {
        string path = Path.Combine(dir, file);
        return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), LoaderOptions) : null;
    }

    private static List<T> ReadArray<T>(string dir, string file)
    {
        string path = Path.Combine(dir, file);
        if (!File.Exists(path)) return new List<T>();
        return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), LoaderOptions) ?? new List<T>();
    }

    // ------------------------------------------------------------------------------------------------------------
    // v1 -> v2
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>A v1 class name as a pinned v2 local template reference.</summary>
    public static string ToRef(string className) => LocalIdPrefix + className + "@" + LocalRev;

    /// <summary>
    /// A pinned v2 reference back to a v1 class name. Only a projected local template (gridsim.local.NAME@1) has a
    /// v1 spelling; any other reference is refused, because v1 can only name a class defined in the case itself.
    /// </summary>
    public static string FromRef(string reference)
    {
        string suffix = "@" + LocalRev;
        if (!reference.StartsWith(LocalIdPrefix, StringComparison.Ordinal) || !reference.EndsWith(suffix, StringComparison.Ordinal) ||
            reference.Length <= LocalIdPrefix.Length + suffix.Length)
            throw new NotSupportedException($"template reference '{reference}' has no v1 spelling: v1 can only name a class of its own case (a local template {LocalIdPrefix}NAME{suffix}).");
        return reference[LocalIdPrefix.Length..^suffix.Length];
    }

    /// <summary>Project a v1 case to a v2 case manifest and one v2 network file.</summary>
    public static V2Projection Project(V1Case v1)
    {
        ArgumentNullException.ThrowIfNull(v1);
        return new V2Projection(Write(w => WriteCase(w, v1)), Write(w => WriteNetwork(w, v1)));
    }

    // Write one document with a fresh writer into a byte array. Indented, so a failing test's dump is readable.
    private static byte[] Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            body(w);
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteCase(Utf8JsonWriter w, V1Case v1)
    {
        // v1 tolerated a missing system.json (all defaults, name = folder). v2 requires a name, so a nameless v1
        // case projects to a manifest WITHOUT one and fails schema validation, loudly, rather than inventing it.
        SystemDto sys = v1.System ?? new SystemDto();
        w.WriteStartObject();
        w.WriteString("schema", "gridsim-case/2");
        if (sys.Name is not null) w.WriteString("name", sys.Name);
        w.WriteStartObject("system");
        w.WriteNumber("baseMva", sys.BaseMva);          // required in v2: always written
        w.WriteNumber("frequencyHz", sys.FrequencyHz);  // required in v2: always written
        Opt(w, "dispatch", sys.Dispatch);               // F24: absent = dispatched, as in v1
        w.WriteEndObject();
        w.WriteStartArray("network");
        w.WriteStringValue(NetworkFileName);
        w.WriteEndArray();

        if (v1.AssetClasses.Count > 0)
        {
            w.WriteStartArray("localTemplates");
            foreach (AssetClassDto c in v1.AssetClasses) WriteTemplate(w, c);
            w.WriteEndArray();
        }
        if (v1.AssetBindings.Count > 0)
        {
            w.WriteStartArray("bindings");
            foreach (AssetBindingDto b in v1.AssetBindings)
            {
                w.WriteStartObject();
                w.WriteString("matchType", b.MatchType);
                w.WriteString("template", ToRef(b.Class));
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    private static void WriteTemplate(Utf8JsonWriter w, AssetClassDto c)
    {
        if (!KindV1ToV2.TryGetValue(c.Kind, out string? kind))
            throw new NotSupportedException($"asset class '{c.Name}': v1 kind '{c.Kind}' has no v2 kind in the projector's table (aliases are refused, not normalised).");
        w.WriteStartObject();
        w.WriteString("id", LocalIdPrefix + c.Name);
        w.WriteNumber("rev", LocalRev);
        w.WriteString("kind", kind);
        w.WriteString("status", "active");
        w.WriteString("title", c.Name);
        if (c.Mode.Length > 0) w.WriteString("description", c.Mode);   // v1 mode is a free-text label

        // v1 'params' absent is read by the loader as empty; v2 requires the member, so it is written (empty).
        w.WriteStartObject("params");
        foreach ((string key, double value) in c.Params ?? new Dictionary<string, double>()) w.WriteNumber(key, value);
        w.WriteEndObject();

        w.WriteStartObject("provenance");
        Dictionary<string, ProvenanceDto> prov = c.Provenance ?? new Dictionary<string, ProvenanceDto>();
        if (!prov.ContainsKey(AssetClass.DefaultKey))
            WriteProvenance(w, AssetClass.DefaultKey, V1FallbackTag, V1FallbackNote);   // what v1 itself falls back to
        foreach ((string key, ProvenanceDto p) in prov) WriteProvenance(w, key, p.Tag, p.Note);
        w.WriteEndObject();

        w.WriteEndObject();
    }

    private static void WriteProvenance(Utf8JsonWriter w, string key, string tag, string note)
    {
        w.WriteStartObject(key);
        w.WriteString("tag", tag);
        w.WriteString("note", note);
        w.WriteEndObject();
    }

    private static void WriteNetwork(Utf8JsonWriter w, V1Case v1)
    {
        w.WriteStartObject();
        w.WriteString("schema", "gridsim-network/2");
        WriteArray(w, "buses", v1.Buses, WriteBus);
        WriteArray(w, "generators", v1.Generators, WriteGenerator);
        WriteArray(w, "demands", v1.Demands, WriteDemand);
        WriteArray(w, "lines", v1.Lines, WriteLine);
        WriteArray(w, "transformers", v1.Transformers, WriteTransformer);
        WriteArray(w, "oltcs", v1.Oltcs, WriteOltc);
        WriteArray(w, "boundaries", v1.Boundaries, WriteBoundary);
        WriteArray(w, "protections", v1.Protections, WriteProtection);
        w.WriteEndObject();
    }

    // An empty v1 list (file absent or "[]") is written as an absent array; both read back as an empty list.
    private static void WriteArray<T>(Utf8JsonWriter w, string name, List<T> items, Action<Utf8JsonWriter, T> one)
    {
        if (items.Count == 0) return;
        w.WriteStartArray(name);
        foreach (T item in items) one(w, item);
        w.WriteEndArray();
    }

    // ---- the absence rule, one helper per value type: write only when not bit-for-bit the DTO default ----

    private static bool SameBits(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    private static void Opt(Utf8JsonWriter w, string name, double value, double dflt)
    {
        if (!SameBits(value, dflt)) w.WriteNumber(name, value);
    }

    private static void Opt(Utf8JsonWriter w, string name, double? value)
    {
        if (value is double v) w.WriteNumber(name, v);   // nullable DTO members default to null
    }

    private static void Opt(Utf8JsonWriter w, string name, int value, int dflt)
    {
        if (value != dflt) w.WriteNumber(name, value);
    }

    private static void Opt(Utf8JsonWriter w, string name, bool value, bool dflt)
    {
        if (value != dflt) w.WriteBoolean(name, value);
    }

    private static void Opt(Utf8JsonWriter w, string name, bool? value)
    {
        if (value is bool v) w.WriteBoolean(name, v);
    }

    private static void Opt(Utf8JsonWriter w, string name, string? value, string? dflt)
    {
        if (value is not null && !string.Equals(value, dflt, StringComparison.Ordinal)) w.WriteString(name, value);
    }

    // A v1 class name (dynamicsClass) as a template reference; null stays absent.
    private static void OptRef(Utf8JsonWriter w, string name, string? className)
    {
        if (className is not null) w.WriteString(name, ToRef(className));
    }

    private static readonly BusDto BusDefault = new();
    private static readonly GenDto GenDefault = new();
    private static readonly DemandDto DemandDefault = new();
    private static readonly LineDto LineDefault = new();
    private static readonly TransformerDto TxDefault = new();
    private static readonly OltcDto OltcDefault = new();
    private static readonly BoundaryBranchDto BranchDefault = new();
    private static readonly ProtectionInstanceDto ProtDefault = new();

    private static void WriteBus(Utf8JsonWriter w, BusDto b)
    {
        w.WriteStartObject();
        w.WriteNumber("id", b.Id);
        Opt(w, "name", b.Name, BusDefault.Name);
        // Moved 2026-10 (Q13, authorised): R179 made v1 BusDto.BaseKv a required double? with no default, so the
        // absence rule now writes it whenever the v1 file had it (every bundled bus). Before: written only when not
        // bit-for-bit 400 (the old default). After: written when present, absent when absent.
        Opt(w, "baseKv", b.BaseKv);
        Opt(w, "type", b.Type, BusDefault.Type);
        Opt(w, "vm", b.Vm, BusDefault.Vm);
        Opt(w, "vaDeg", b.VaDeg, BusDefault.VaDeg);
        Opt(w, "gs", b.Gs, BusDefault.Gs);
        Opt(w, "bs", b.Bs, BusDefault.Bs);
        Opt(w, "x", b.X, BusDefault.X);
        Opt(w, "y", b.Y, BusDefault.Y);
        Opt(w, "vminPu", b.VminPu);   // F17: per-bus voltage limits, same spelling in v1 and v2
        Opt(w, "vmaxPu", b.VmaxPu);
        if (b.Zip is BusZipDto z)
        {
            // Every coefficient is required in v2, as in the v1 loader; a missing one is left out and the schema refuses it.
            w.WriteStartObject("zip");
            Opt(w, "ap", z.Ap); Opt(w, "bp", z.Bp); Opt(w, "cp", z.Cp);
            Opt(w, "aq", z.Aq); Opt(w, "bq", z.Bq); Opt(w, "cq", z.Cq);
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    private static void WriteGenerator(Utf8JsonWriter w, GenDto g)
    {
        w.WriteStartObject();
        if (g.Name is not null) w.WriteString("name", g.Name);   // required in v2: a nameless unit fails validation
        w.WriteNumber("bus", g.Bus);
        Opt(w, "type", g.Type, GenDefault.Type);
        OptRef(w, "template", g.DynamicsClass);
        Opt(w, "capacityMva", g.CapacityMva, GenDefault.CapacityMva);
        Opt(w, "outputMw", g.OutputMw);
        Opt(w, "capacityFactor", g.CapacityFactor);
        Opt(w, "voltageSetpoint", g.VoltageSetpoint, GenDefault.VoltageSetpoint);
        Opt(w, "qMinMvar", g.QMinMvar);
        Opt(w, "qMaxMvar", g.QMaxMvar);
        Opt(w, "inertiaH", g.InertiaH, GenDefault.InertiaH);
        Opt(w, "role", g.Role, GenDefault.Role);
        Opt(w, "powerFlowOnly", g.PowerFlowOnly, GenDefault.PowerFlowOnly);
        Opt(w, "unitTransformerXPu", g.UnitTransformerXPu, GenDefault.UnitTransformerXPu);
        Opt(w, "inService", g.InService, GenDefault.InService);   // F16: v1 now carries generator status too
        Opt(w, "pMinMw", g.PMinMw);                                // F24: same spelling in v1 and v2
        Opt(w, "pMaxMw", g.PMaxMw);
        Opt(w, "qOutputMvar", g.QOutputMvar);
        // The converter block is written only when one of its three members is not the v1 default.
        if (g.ConverterInterfaced is not null || g.ConverterFaultCurrentPu is not null || g.GridForming != GenDefault.GridForming)
        {
            w.WriteStartObject("converter");
            Opt(w, "interfaced", g.ConverterInterfaced);
            Opt(w, "faultCurrentPu", g.ConverterFaultCurrentPu);
            Opt(w, "gridForming", g.GridForming, GenDefault.GridForming);
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    private static void WriteDemand(Utf8JsonWriter w, DemandDto d)
    {
        w.WriteStartObject();
        if (d.Name is not null) w.WriteString("name", d.Name);
        w.WriteNumber("bus", d.Bus);
        w.WriteNumber("pMw", d.PMw);   // required in v2
        Opt(w, "qMvar", d.QMvar);
        Opt(w, "powerFactor", d.PowerFactor, DemandDefault.PowerFactor);
        w.WriteEndObject();
    }

    private static void WriteLine(Utf8JsonWriter w, LineDto l)
    {
        w.WriteStartObject();
        if (l.Name is not null) w.WriteString("name", l.Name);
        w.WriteNumber("from", l.From);
        w.WriteNumber("to", l.To);
        OptRef(w, "template", l.DynamicsClass);
        w.WriteNumber("rPu", l.RPu);   // required in v2
        w.WriteNumber("xPu", l.XPu);   // required in v2
        Opt(w, "bPu", l.BPu, LineDefault.BPu);
        Opt(w, "ratingMva", l.RatingMva, LineDefault.RatingMva);
        Opt(w, "circuits", l.Circuits, LineDefault.Circuits);
        Opt(w, "lengthKm", l.LengthKm, LineDefault.LengthKm);
        Opt(w, "equipment", l.Equipment, LineDefault.Equipment);
        Opt(w, "r0Pu", l.RPu0, LineDefault.RPu0);
        Opt(w, "x0Pu", l.XPu0, LineDefault.XPu0);
        Opt(w, "tapRatio", l.TapRatio, LineDefault.TapRatio);            // F24: an off-nominal branch kept in line order
        Opt(w, "phaseShiftDeg", l.PhaseShiftDeg, LineDefault.PhaseShiftDeg);
        Opt(w, "isTransformer", l.IsTransformer, LineDefault.IsTransformer);
        Opt(w, "ratingBMva", l.RatingBMva, LineDefault.RatingBMva);      // F20: same spelling in v1 and v2
        Opt(w, "ratingCMva", l.RatingCMva, LineDefault.RatingCMva);
        w.WriteEndObject();
    }

    private static void WriteTransformer(Utf8JsonWriter w, TransformerDto t)
    {
        w.WriteStartObject();
        if (t.Name is not null) w.WriteString("name", t.Name);
        w.WriteNumber("from", t.From);
        w.WriteNumber("to", t.To);
        OptRef(w, "template", t.DynamicsClass);
        w.WriteNumber("rPu", t.RPu);
        w.WriteNumber("xPu", t.XPu);
        Opt(w, "tapRatio", t.TapRatio, TxDefault.TapRatio);
        Opt(w, "phaseShiftDeg", t.PhaseShiftDeg, TxDefault.PhaseShiftDeg);
        Opt(w, "ratingMva", t.RatingMva, TxDefault.RatingMva);
        Opt(w, "ratingBMva", t.RatingBMva, TxDefault.RatingBMva);   // N119: same spelling in v1 and v2
        Opt(w, "ratingCMva", t.RatingCMva, TxDefault.RatingCMva);
        w.WriteEndObject();
    }

    private static void WriteOltc(Utf8JsonWriter w, OltcDto o)
    {
        w.WriteStartObject();
        w.WriteString("transformer", o.Transformer);
        w.WriteNumber("controlsBus", o.ControlsBus);
        Opt(w, "targetVpu", o.TargetVpu, OltcDefault.TargetVpu);
        Opt(w, "minTap", o.MinTap, OltcDefault.MinTap);
        Opt(w, "maxTap", o.MaxTap, OltcDefault.MaxTap);
        Opt(w, "stepPu", o.StepPu, OltcDefault.StepPu);
        Opt(w, "deadbandPu", o.DeadbandPu, OltcDefault.DeadbandPu);
        w.WriteEndObject();
    }

    private static void WriteBoundary(Utf8JsonWriter w, BoundaryDto b)
    {
        w.WriteStartObject();
        w.WriteString("name", b.Name);
        w.WriteNumber("limitMw", b.LimitMw);
        if (b.Branches is not null)   // null and [] are different DTO states, so [] is written as []
        {
            w.WriteStartArray("branches");
            foreach (BoundaryBranchDto x in b.Branches)
            {
                w.WriteStartObject();
                w.WriteString("branch", x.Branch);
                Opt(w, "direction", x.Direction, BranchDefault.Direction);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    private static void WriteProtection(Utf8JsonWriter w, ProtectionInstanceDto p)
    {
        w.WriteStartObject();
        w.WriteString("name", p.Name);
        w.WriteString("template", ToRef(p.Class));
        w.WriteNumber("monitorBus", p.MonitorBus);
        Opt(w, "capacityMw", p.CapacityMw, ProtDefault.CapacityMw);
        Opt(w, "targetBranch", p.TargetBranch, ProtDefault.TargetBranch);
        w.WriteEndObject();
    }

    // ------------------------------------------------------------------------------------------------------------
    // v2 -> v1
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Map a v2 case manifest and its one network file back to v1 DTOs. Strict: an unexpected member throws
    /// (the schema already refuses unknown members; this is a second, independent net), and so does any v2-only
    /// content v1 cannot carry (imports, overrides, profiles, a non-local template reference, subtransientXdPu,
    /// an element out of service, a line or transformer type, transformer zero-sequence data). Expects documents
    /// that already passed schema validation.
    /// </summary>
    public static V1Case Back(JsonElement caseRoot, JsonElement networkRoot)
    {
        var sys = new SystemDto();
        var v1 = new V1Case { System = sys };
        foreach (JsonProperty p in caseRoot.EnumerateObject())
        {
            switch (p.Name)
            {
                case "schema": break;
                case "name": sys.Name = p.Value.GetString(); break;
                case "description": throw V1Cannot("a case description");
                case "system":
                    foreach (JsonProperty s in p.Value.EnumerateObject())
                    {
                        switch (s.Name)
                        {
                            case "baseMva": sys.BaseMva = s.Value.GetDouble(); break;
                            case "frequencyHz": sys.FrequencyHz = s.Value.GetDouble(); break;
                            case "dispatch": sys.Dispatch = s.Value.GetBoolean(); break;   // F24
                            default: throw Unexpected("system", s.Name);
                        }
                    }
                    break;
                case "network":
                    if (p.Value.GetArrayLength() != 1 || p.Value[0].GetString() != NetworkFileName)
                        throw V1Cannot($"a network list other than [\"{NetworkFileName}\"]");
                    break;
                case "localTemplates":
                    foreach (JsonElement t in p.Value.EnumerateArray()) v1.AssetClasses.Add(BackTemplate(t));
                    break;
                case "bindings":
                    foreach (JsonElement b in p.Value.EnumerateArray())
                        v1.AssetBindings.Add(new AssetBindingDto
                        {
                            MatchType = b.GetProperty("matchType").GetString()!,
                            Class = FromRef(b.GetProperty("template").GetString()!),
                        });
                    break;
                case "imports": throw V1Cannot("library imports");
                case "overrides": throw V1Cannot("overrides");
                case "gridCodeProfile": throw V1Cannot("a grid-code profile");
                case "solverProfile": throw V1Cannot("a solver profile");
                default: throw Unexpected("case", p.Name);
            }
        }

        foreach (JsonProperty p in networkRoot.EnumerateObject())
        {
            switch (p.Name)
            {
                case "schema": break;
                case "description": throw V1Cannot("a network description");
                case "buses": v1.Buses = p.Value.EnumerateArray().Select(BackBus).ToList(); break;
                case "generators": v1.Generators = p.Value.EnumerateArray().Select(BackGenerator).ToList(); break;
                case "demands": v1.Demands = p.Value.EnumerateArray().Select(BackDemand).ToList(); break;
                case "lines": v1.Lines = p.Value.EnumerateArray().Select(BackLine).ToList(); break;
                case "transformers": v1.Transformers = p.Value.EnumerateArray().Select(BackTransformer).ToList(); break;
                case "oltcs": v1.Oltcs = p.Value.EnumerateArray().Select(BackOltc).ToList(); break;
                case "boundaries": v1.Boundaries = p.Value.EnumerateArray().Select(BackBoundary).ToList(); break;
                case "protections": v1.Protections = p.Value.EnumerateArray().Select(BackProtection).ToList(); break;
                default: throw Unexpected("network", p.Name);
            }
        }
        return v1;
    }

    private static NotSupportedException V1Cannot(string what) => new($"v1 cannot carry {what}: this v2 document has no v1 equivalent.");

    private static InvalidDataException Unexpected(string where, string member) => new($"{where}: unexpected member '{member}'.");

    // inService true is what v1 means by every element, so it maps back; false has no v1 spelling.
    private static void InServiceOnly(JsonElement value, string element)
    {
        if (!value.GetBoolean()) throw V1Cannot($"an out-of-service {element}");
    }

    private static AssetClassDto BackTemplate(JsonElement t)
    {
        var c = new AssetClassDto { Params = new Dictionary<string, double>(), Provenance = new Dictionary<string, ProvenanceDto>() };
        foreach (JsonProperty p in t.EnumerateObject())
        {
            switch (p.Name)
            {
                case "id":
                    string id = p.Value.GetString()!;
                    if (!id.StartsWith(LocalIdPrefix, StringComparison.Ordinal)) throw V1Cannot($"template id '{id}' (not {LocalIdPrefix}NAME)");
                    c.Name = id[LocalIdPrefix.Length..];
                    break;
                case "rev":
                    if (p.Value.GetInt32() != LocalRev) throw V1Cannot($"a template revision other than {LocalRev}");
                    break;
                case "kind":
                    string kind = p.Value.GetString()!;
                    c.Kind = KindV1ToV2.OnlyOne(kv => kv.Value == kind, () => V1Cannot($"template kind '{kind}'")).Key;
                    break;
                case "status":
                    if (p.Value.GetString() != "active") throw V1Cannot("a placeholder template");
                    break;
                case "title": break;   // the class name again; the id is authoritative
                case "description": c.Mode = p.Value.GetString()!; break;
                case "params":
                    foreach (JsonProperty kv in p.Value.EnumerateObject())
                    {
                        if (kv.Value.ValueKind != JsonValueKind.Number) throw V1Cannot($"parameter '{kv.Name}' in a non-number form");
                        c.Params[kv.Name] = kv.Value.GetDouble();
                    }
                    break;
                case "controllers": throw V1Cannot("controller references");
                case "provenance":
                    foreach (JsonProperty kv in p.Value.EnumerateObject())
                    {
                        string tag = kv.Value.GetProperty("tag").GetString()!, note = kv.Value.GetProperty("note").GetString()!;
                        // Drop the _default the projector synthesised from v1's own fallback (see the class summary).
                        if (kv.Name == AssetClass.DefaultKey && tag == V1FallbackTag && note == V1FallbackNote) continue;
                        c.Provenance[kv.Name] = new ProvenanceDto { Tag = tag, Note = note };
                    }
                    break;
                default: throw Unexpected("template", p.Name);
            }
        }
        return c;
    }

    // Small helper so a LINQ Single with a custom exception reads in one line.
    private static KeyValuePair<string, string> OnlyOne(this IEnumerable<KeyValuePair<string, string>> source,
        Func<KeyValuePair<string, string>, bool> match, Func<Exception> none)
    {
        var hits = source.Where(match).ToList();
        if (hits.Count != 1) throw none();
        return hits[0];
    }

    private static BusDto BackBus(JsonElement e)
    {
        var b = new BusDto();
        foreach (JsonProperty p in e.EnumerateObject())
        {
            switch (p.Name)
            {
                case "id": b.Id = p.Value.GetInt32(); break;
                case "name": b.Name = p.Value.GetString(); break;
                case "baseKv": b.BaseKv = p.Value.GetDouble(); break;
                case "type": b.Type = p.Value.GetString()!; break;
                case "vm": b.Vm = p.Value.GetDouble(); break;
                case "vaDeg": b.VaDeg = p.Value.GetDouble(); break;
                case "gs": b.Gs = p.Value.GetDouble(); break;
                case "bs": b.Bs = p.Value.GetDouble(); break;
                case "x": b.X = p.Value.GetDouble(); break;
                case "y": b.Y = p.Value.GetDouble(); break;
                case "vminPu": b.VminPu = p.Value.GetDouble(); break;   // F17
                case "vmaxPu": b.VmaxPu = p.Value.GetDouble(); break;   // F17
                case "zip":
                    var z = new BusZipDto();
                    foreach (JsonProperty c in p.Value.EnumerateObject())
                    {
                        double v = c.Value.GetDouble();
                        switch (c.Name)
                        {
                            case "ap": z.Ap = v; break;
                            case "bp": z.Bp = v; break;
                            case "cp": z.Cp = v; break;
                            case "aq": z.Aq = v; break;
                            case "bq": z.Bq = v; break;
                            case "cq": z.Cq = v; break;
                            default: throw Unexpected("zip", c.Name);
                        }
                    }
                    b.Zip = z;
                    break;
                default: throw Unexpected("bus", p.Name);
            }
        }
        return b;
    }

    private static GenDto BackGenerator(JsonElement e)
    {
        var g = new GenDto();
        foreach (JsonProperty p in e.EnumerateObject())
        {
            switch (p.Name)
            {
                case "name": g.Name = p.Value.GetString(); break;
                case "bus": g.Bus = p.Value.GetInt32(); break;
                case "type": g.Type = p.Value.GetString()!; break;
                case "template": g.DynamicsClass = FromRef(p.Value.GetString()!); break;
                case "capacityMva": g.CapacityMva = p.Value.GetDouble(); break;
                case "outputMw": g.OutputMw = p.Value.GetDouble(); break;
                case "capacityFactor": g.CapacityFactor = p.Value.GetDouble(); break;
                case "voltageSetpoint": g.VoltageSetpoint = p.Value.GetDouble(); break;
                case "qMinMvar": g.QMinMvar = p.Value.GetDouble(); break;
                case "qMaxMvar": g.QMaxMvar = p.Value.GetDouble(); break;
                case "inertiaH": g.InertiaH = p.Value.GetDouble(); break;
                case "role": g.Role = p.Value.GetString()!; break;
                case "powerFlowOnly": g.PowerFlowOnly = p.Value.GetBoolean(); break;
                case "unitTransformerXPu": g.UnitTransformerXPu = p.Value.GetDouble(); break;
                case "pMinMw": g.PMinMw = p.Value.GetDouble(); break;             // F24
                case "pMaxMw": g.PMaxMw = p.Value.GetDouble(); break;             // F24
                case "qOutputMvar": g.QOutputMvar = p.Value.GetDouble(); break;   // F24
                case "converter":
                    foreach (JsonProperty c in p.Value.EnumerateObject())
                    {
                        switch (c.Name)
                        {
                            case "interfaced": g.ConverterInterfaced = c.Value.GetBoolean(); break;
                            case "faultCurrentPu": g.ConverterFaultCurrentPu = c.Value.GetDouble(); break;
                            case "gridForming": g.GridForming = c.Value.GetBoolean(); break;
                            default: throw Unexpected("converter", c.Name);
                        }
                    }
                    break;
                case "subtransientXdPu": throw V1Cannot("a generator subtransientXdPu");
                case "negativeSeqXPu": throw V1Cannot("a generator negativeSeqXPu");
                case "inService": g.InService = p.Value.GetBoolean(); break;   // F16: v1 carries it since October 2026
                default: throw Unexpected("generator", p.Name);
            }
        }
        return g;
    }

    private static DemandDto BackDemand(JsonElement e)
    {
        var d = new DemandDto();
        foreach (JsonProperty p in e.EnumerateObject())
        {
            switch (p.Name)
            {
                case "name": d.Name = p.Value.GetString(); break;
                case "bus": d.Bus = p.Value.GetInt32(); break;
                case "pMw": d.PMw = p.Value.GetDouble(); break;
                case "qMvar": d.QMvar = p.Value.GetDouble(); break;
                case "powerFactor": d.PowerFactor = p.Value.GetDouble(); break;
                default: throw Unexpected("demand", p.Name);
            }
        }
        return d;
    }

    private static LineDto BackLine(JsonElement e)
    {
        var l = new LineDto();
        foreach (JsonProperty p in e.EnumerateObject())
        {
            switch (p.Name)
            {
                case "name": l.Name = p.Value.GetString(); break;
                case "from": l.From = p.Value.GetInt32(); break;
                case "to": l.To = p.Value.GetInt32(); break;
                case "type": throw V1Cannot("a line type");
                case "template": l.DynamicsClass = FromRef(p.Value.GetString()!); break;
                case "rPu": l.RPu = p.Value.GetDouble(); break;
                case "xPu": l.XPu = p.Value.GetDouble(); break;
                case "bPu": l.BPu = p.Value.GetDouble(); break;
                case "ratingMva": l.RatingMva = p.Value.GetDouble(); break;
                case "circuits": l.Circuits = p.Value.GetInt32(); break;
                case "lengthKm": l.LengthKm = p.Value.GetDouble(); break;
                case "equipment": l.Equipment = p.Value.GetString(); break;
                case "r0Pu": l.RPu0 = p.Value.GetDouble(); break;
                case "x0Pu": l.XPu0 = p.Value.GetDouble(); break;
                case "tapRatio": l.TapRatio = p.Value.GetDouble(); break;            // F24
                case "phaseShiftDeg": l.PhaseShiftDeg = p.Value.GetDouble(); break;  // F24
                case "isTransformer": l.IsTransformer = p.Value.GetBoolean(); break; // F24
                case "ratingBMva": l.RatingBMva = p.Value.GetDouble(); break;        // F20
                case "ratingCMva": l.RatingCMva = p.Value.GetDouble(); break;        // F20
                case "inService": InServiceOnly(p.Value, "line"); break;
                default: throw Unexpected("line", p.Name);
            }
        }
        return l;
    }

    private static TransformerDto BackTransformer(JsonElement e)
    {
        var t = new TransformerDto();
        foreach (JsonProperty p in e.EnumerateObject())
        {
            switch (p.Name)
            {
                case "name": t.Name = p.Value.GetString(); break;
                case "from": t.From = p.Value.GetInt32(); break;
                case "to": t.To = p.Value.GetInt32(); break;
                case "template": t.DynamicsClass = FromRef(p.Value.GetString()!); break;
                case "rPu": t.RPu = p.Value.GetDouble(); break;
                case "xPu": t.XPu = p.Value.GetDouble(); break;
                case "tapRatio": t.TapRatio = p.Value.GetDouble(); break;
                case "phaseShiftDeg": t.PhaseShiftDeg = p.Value.GetDouble(); break;
                case "ratingMva": t.RatingMva = p.Value.GetDouble(); break;
                case "ratingBMva": t.RatingBMva = p.Value.GetDouble(); break;   // N119
                case "ratingCMva": t.RatingCMva = p.Value.GetDouble(); break;   // N119
                case "type": throw V1Cannot("a transformer type");
                case "r0Pu": case "x0Pu": throw V1Cannot("transformer zero-sequence impedance");
                case "windings": throw V1Cannot("transformer winding connections");
                case "neutralXPu": throw V1Cannot("transformer neutral reactance");
                case "inService": InServiceOnly(p.Value, "transformer"); break;
                default: throw Unexpected("transformer", p.Name);
            }
        }
        return t;
    }

    private static OltcDto BackOltc(JsonElement e)
    {
        var o = new OltcDto();
        foreach (JsonProperty p in e.EnumerateObject())
        {
            switch (p.Name)
            {
                case "transformer": o.Transformer = p.Value.GetString()!; break;
                case "controlsBus": o.ControlsBus = p.Value.GetInt32(); break;
                case "targetVpu": o.TargetVpu = p.Value.GetDouble(); break;
                case "minTap": o.MinTap = p.Value.GetDouble(); break;
                case "maxTap": o.MaxTap = p.Value.GetDouble(); break;
                case "stepPu": o.StepPu = p.Value.GetDouble(); break;
                case "deadbandPu": o.DeadbandPu = p.Value.GetDouble(); break;
                default: throw Unexpected("oltc", p.Name);
            }
        }
        return o;
    }

    private static BoundaryDto BackBoundary(JsonElement e)
    {
        var b = new BoundaryDto();
        foreach (JsonProperty p in e.EnumerateObject())
        {
            switch (p.Name)
            {
                case "name": b.Name = p.Value.GetString()!; break;
                case "limitMw": b.LimitMw = p.Value.GetDouble(); break;
                case "branches":
                    b.Branches = new List<BoundaryBranchDto>();
                    foreach (JsonElement x in p.Value.EnumerateArray())
                    {
                        var bb = new BoundaryBranchDto();
                        foreach (JsonProperty q in x.EnumerateObject())
                        {
                            switch (q.Name)
                            {
                                case "branch": bb.Branch = q.Value.GetString()!; break;
                                case "direction": bb.Direction = q.Value.GetInt32(); break;
                                default: throw Unexpected("boundary branch", q.Name);
                            }
                        }
                        b.Branches.Add(bb);
                    }
                    break;
                default: throw Unexpected("boundary", p.Name);
            }
        }
        return b;
    }

    private static ProtectionInstanceDto BackProtection(JsonElement e)
    {
        var d = new ProtectionInstanceDto();
        foreach (JsonProperty p in e.EnumerateObject())
        {
            switch (p.Name)
            {
                case "name": d.Name = p.Value.GetString()!; break;
                case "template": d.Class = FromRef(p.Value.GetString()!); break;
                case "monitorBus": d.MonitorBus = p.Value.GetInt32(); break;
                case "capacityMw": d.CapacityMw = p.Value.GetDouble(); break;
                case "targetBranch": d.TargetBranch = p.Value.GetString(); break;
                default: throw Unexpected("protection", p.Name);
            }
        }
        return d;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Field-for-field comparison
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Compare two DTO graphs field for field and return every difference as "path: expected X, found Y" (empty =
    /// identical). Doubles compare by their bits (so 0.0 and -0.0 differ, as they would in a hex-pinned result),
    /// strings ordinally, lists element by element, dictionaries key by key and in the same order. Every public
    /// instance property of every class is visited by reflection, so no field can be skipped by omission; a
    /// property of a type this comparer does not know throws rather than being ignored.
    /// </summary>
    public static List<string> Differences(object? expected, object? actual, string path = "$", int limit = 50)
    {
        var diffs = new List<string>();
        Compare(expected, actual, path, diffs, limit);
        return diffs;
    }

    private static void Compare(object? e, object? a, string path, List<string> diffs, int limit)
    {
        if (diffs.Count >= limit) return;
        if (e is null || a is null)
        {
            if (e is not null || a is not null) diffs.Add($"{path}: expected {Show(e)}, found {Show(a)}");
            return;
        }
        if (e.GetType() != a.GetType()) { diffs.Add($"{path}: expected type {e.GetType().Name}, found {a.GetType().Name}"); return; }

        switch (e)
        {
            case double de:
                if (!SameBits(de, (double)a)) diffs.Add($"{path}: expected {Show(e)}, found {Show(a)}");
                return;
            case string or int or bool:
                if (!e.Equals(a)) diffs.Add($"{path}: expected {Show(e)}, found {Show(a)}");
                return;
            case IDictionary ed:
                var ad = (IDictionary)a;
                var ek = ed.Keys.Cast<string>().ToList();
                var ak = ad.Keys.Cast<string>().ToList();
                if (!ek.SequenceEqual(ak, StringComparer.Ordinal))
                {
                    diffs.Add($"{path}: expected keys [{string.Join(", ", ek)}], found [{string.Join(", ", ak)}]");
                    return;
                }
                foreach (string k in ek) Compare(ed[k], ad[k], $"{path}.{k}", diffs, limit);
                return;
            case IList el:
                var al = (IList)a;
                if (el.Count != al.Count) { diffs.Add($"{path}: expected {el.Count} items, found {al.Count}"); return; }
                for (int i = 0; i < el.Count; i++) Compare(el[i], al[i], $"{path}[{i}]", diffs, limit);
                return;
        }

        Type t = e.GetType();
        if (!t.IsClass || t.Namespace is null || !(t.Namespace.StartsWith("GridSim.", StringComparison.Ordinal)))
            throw new NotSupportedException($"{path}: the comparer does not know type {t.FullName}; teach it rather than skipping the field.");
        foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Compare(p.GetValue(e), p.GetValue(a), $"{path}.{p.Name}", diffs, limit);
    }

    private static string Show(object? x) => x switch
    {
        null => "null",
        double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + $" (bits {BitConverter.DoubleToInt64Bits(d):X16})",
        string s => "\"" + s + "\"",
        _ => x.ToString() ?? "?",
    };
}
