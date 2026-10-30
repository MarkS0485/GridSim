using System.Reflection;
using System.Text.Json;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.Schema;
using Xunit;
using static GridSim.Tests.V2.V2TestSupport;

namespace GridSim.Tests.V2;

/// <summary>
/// dream-of-json-ification Phase 1, slice 4: every bundled v1 JSON case (uk, gb-full, gb-synthetic and every
/// data/intl case folder) projects to a v2 case manifest plus network file that validate against the real
/// schemas/v2, and mapping the v2 documents back to v1 DTOs gives DTOs equal to the originals field for field
/// (doubles by their bits). The projector is test-only (<see cref="V1Projector"/>); nothing in production reads v2,
/// and no v1 file, DTO or loader is touched.
///
/// Memory and time: each case is read, projected, validated and compared on its own, and each JsonDocument is
/// disposed as soon as the case is done, so the largest case (gb-full, about 4 MB of v1 JSON) never shares memory
/// with another.
/// </summary>
public class V1ToV2ProjectionTests
{
    private static readonly Lazy<SchemaValidator> Validator = new(() => new SchemaValidator(RealSchemas()));

    /// <summary>A v1 file that is not part of the case the loader builds: the intl importer's own summary.</summary>
    private const string IntlDiagnosticsFile = "diagnostics.json";

    /// <summary>
    /// DATA FINDINGS: bundled v1 content that v2 deliberately refuses, by case, as "document:JSON pointer". They
    /// are reported to Mark (NOTES-FOR-MAIN.md), never fixed by loosening the schema and never by editing v1 data
    /// here. Each entry is exact: when the data is corrected, the entry goes in the same change (the test fails
    /// until it does, so a stale entry cannot linger).
    /// <list type="bullet">
    /// <item>intl/continental-europe, generation.json entry 676 (a 212 MVA hydro unit at bus 1721): its name is a
    /// single space. v2 requires every generator name to hold a non-blank character, because names are how
    /// boundaries, tap changers and overrides address elements.</item>
    /// </list>
    /// </summary>
    private static readonly Dictionary<string, string[]> KnownDataFindings = new(StringComparer.Ordinal)
    {
        // intl/continental-europe generators/676 (a single-space name) was fixed in the data in 1.95.084 (Q26, Mark's
        // best-judgement ruling: fix the data, v2 keeps refusing blank names); its entry left with the fix.
    };

    /// <summary>The bundled v1 cases, as paths relative to data/: the three GB cases and every intl case folder.</summary>
    public static TheoryData<string> BundledCases()
    {
        var data = new TheoryData<string>();
        foreach (string c in BundledCaseDirs()) data.Add(c);
        return data;
    }

    private static List<string> BundledCaseDirs()
    {
        var cases = new List<string> { "uk", "gb-full", "gb-synthetic" };
        string intl = Path.Combine(RepoRoot(), "data", "intl");
        foreach (string dir in Directory.EnumerateDirectories(intl).OrderBy(d => d, StringComparer.Ordinal))
            cases.Add("intl/" + Path.GetFileName(dir));
        return cases;
    }

    [Fact]
    public void The_bundled_case_list_has_the_three_GB_cases_and_at_least_one_intl_case()
    {
        List<string> cases = BundledCaseDirs();
        Assert.Contains("uk", cases);
        Assert.Contains("gb-full", cases);
        Assert.Contains("gb-synthetic", cases);
        Assert.Contains(cases, c => c.StartsWith("intl/", StringComparison.Ordinal));
        // Every intl folder must be a loadable case (has buses.json), or it is not a case and must not be listed.
        foreach (string c in cases)
            Assert.True(File.Exists(Path.Combine(RepoRoot(), "data", c, "buses.json")), $"data/{c} has no buses.json");
    }

    [Theory]
    [MemberData(nameof(BundledCases))]
    public void A_bundled_v1_case_projects_to_valid_v2_and_maps_back_field_for_field(string relativeDir)
    {
        string dir = Path.Combine(RepoRoot(), "data", relativeDir);

        // Every JSON file in the folder is one the loader reads (and so one the projector maps), or the intl
        // importer's diagnostics summary, which is not part of the case. A new v1 file would otherwise be skipped.
        foreach (string f in Directory.EnumerateFiles(dir, "*.json").Select(p => Path.GetFileName(p)!))
            Assert.True(V1Projector.LoaderFiles.Contains(f) || f == IntlDiagnosticsFile, $"data/{relativeDir}/{f} is neither a loader file nor known metadata");

        V1Case v1 = V1Projector.ReadV1(dir);
        Assert.NotEmpty(v1.Buses);   // a real case, not an empty folder
        V2Projection v2 = V1Projector.Project(v1);

        using JsonDocument caseDoc = JsonDocument.Parse(v2.CaseJson, JsonSchemaSubset.ReaderOptions);
        using JsonDocument networkDoc = JsonDocument.Parse(v2.NetworkJson, JsonSchemaSubset.ReaderOptions);

        // Every schema error, tagged with the document it came from. It must be EXACTLY the case's recorded data
        // findings (normally none): a new error fails, and so does a recorded finding that has gone away.
        IReadOnlyList<SchemaError> caseErrors = Validator.Value.Validate(caseDoc.RootElement, "case.schema.json");
        IReadOnlyList<SchemaError> networkErrors = Validator.Value.Validate(networkDoc.RootElement, "network.schema.json");
        string[] found = caseErrors.Select(e => "case:" + e.Pointer).Concat(networkErrors.Select(e => "network:" + e.Pointer))
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        string[] expected = (KnownDataFindings.TryGetValue(relativeDir, out string[]? known) ? known : Array.Empty<string>())
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.True(expected.SequenceEqual(found, StringComparer.Ordinal),
            $"data/{relativeDir}: schema errors differ from the recorded data findings [{string.Join(", ", expected)}]:{Environment.NewLine}" +
            $"case manifest, {caseErrors.Count} error(s):{Environment.NewLine}{Dump(caseErrors.Take(50))}{Environment.NewLine}" +
            $"network, {networkErrors.Count} error(s):{Environment.NewLine}{Dump(networkErrors.Take(50))}");

        V1Case back = V1Projector.Back(caseDoc.RootElement, networkDoc.RootElement);
        List<string> diffs = V1Projector.Differences(v1, back);
        Assert.True(diffs.Count == 0, $"data/{relativeDir}: v2 does not map back to the original v1 DTOs:{Environment.NewLine}{string.Join(Environment.NewLine, diffs)}");
    }

    // ----- the projector covers every v1 field -----

    [Fact]
    public void The_projector_maps_every_property_of_every_v1_dto()
    {
        // Walk every DTO type reachable from V1Case; each must be in HandledMembers with exactly its properties.
        var reachable = new HashSet<Type>();
        void Walk(Type t)
        {
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Type pt = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                if (pt.IsGenericType) pt = pt.GetGenericArguments().Last();   // List<T>, Dictionary<string, T>
                if (pt.Namespace == typeof(BusDto).Namespace && reachable.Add(pt)) Walk(pt);
            }
        }
        Walk(typeof(V1Case));
        Assert.Equal(reachable.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal),
                     V1Projector.HandledMembers.Keys.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));
        foreach ((Type t, string[] members) in V1Projector.HandledMembers)
            Assert.Equal(t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal),
                         members.OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>
    /// A synthetic v1 case in which EVERY DTO property holds a non-default value (checked below by reflection), so
    /// the fields the bundled data never uses (zip, dynamicsClass, lengthKm, equipment, rPu0, xPu0, gridForming,
    /// targetBranch, ...) are exercised too.
    /// </summary>
    private static V1Case EveryFieldSet() => new()
    {
        System = new SystemDto { Name = "Every field", BaseMva = 250, FrequencyHz = 60, Dispatch = false },   // F24 (October 2026): Dispatch added
        Buses =
        {
            new BusDto { Id = 7, Name = "B7", BaseKv = 132, Type = "slack", Vm = 1.03, VaDeg = -2.5, Gs = 0.01, Bs = -0.02, X = 0.25, Y = 0.75,
                         Zip = new BusZipDto { Ap = 0.2, Bp = 0.3, Cp = 0.5, Aq = 0.1, Bq = 0.2, Cq = 0.7 },
                         VminPu = 0.95, VmaxPu = 1.05 },   // F17 (October 2026): BusDto gained per-bus limits; the guard needs them set
            // Moved 2026-10 (N065, authorised by Mark's 2026-10-02 best-judgement ruling): JSON v2 Phase 2 requires bus baseKv in
            // network.schema.json (as the v1 loader has since R179), so this bus, projected without one, no longer validated.
            // Before: new BusDto { Id = 8, Name = "B8", Type = "pv", Vm = -0.0 }. After: the same with BaseKv = 33.
            new BusDto { Id = 8, Name = "B8", BaseKv = 33, Type = "pv", Vm = -0.0 },   // -0.0 differs from the default 1.0 and must survive by its bits
        },
        Generators =
        {
            new GenDto { Name = "G1", Bus = 7, Type = "storage", CapacityMva = 300, OutputMw = -50, CapacityFactor = 0.4, VoltageSetpoint = 1.01,
                         QMinMvar = -90, QMaxMvar = 120, InertiaH = 0.5, Role = "slack", DynamicsClass = "gfm-bess",
                         ConverterInterfaced = false, ConverterFaultCurrentPu = 0.0, PowerFlowOnly = true, UnitTransformerXPu = 0.12, GridForming = true,
                         InService = false,   // F16 (October 2026): GenDto gained InService; the guard below needs it set
                         PMinMw = 5, PMaxMw = 280, QOutputMvar = 12.5 },   // F24 (October 2026): GenDto gained these
        },
        Demands = { new DemandDto { Name = "D1", Bus = 8, PMw = 0.1 + 0.2, QMvar = 1e-300, PowerFactor = 0.9 } },
        Lines =
        {
            new LineDto { Name = "L1", From = 7, To = 8, RPu = 0.001, XPu = 0.01, BPu = 0.2, RatingMva = 500, Circuits = 3, DynamicsClass = "cable-dtr",
                          LengthKm = 12.75, Equipment = "cable", RPu0 = 0.003, XPu0 = 0.033,
                          TapRatio = 0.98, PhaseShiftDeg = 2.5, IsTransformer = true,     // F24 (October 2026): LineDto gained these
                          RatingBMva = 600, RatingCMva = 700 },                           // F20 (October 2026): and these
        },
        Transformers =
        {
            new TransformerDto { Name = "T1", From = 7, To = 8, RPu = 0.002, XPu = 0.08, TapRatio = 1.05, PhaseShiftDeg = -30, RatingMva = 240, DynamicsClass = "tx-oltc",
                                 RatingBMva = 260, RatingCMva = 280 },   // N119 (October 2026): TransformerDto gained these
        },
        Oltcs = { new OltcDto { Transformer = "T1", ControlsBus = 8, TargetVpu = 1.02, MinTap = 0.85, MaxTap = 1.15, StepPu = 0.00625, DeadbandPu = 0.02 } },
        Boundaries = { new BoundaryDto { Name = "B", LimitMw = 400, Branches = new() { new BoundaryBranchDto { Branch = "L1", Direction = -1 } } } },
        AssetClasses =
        {
            new AssetClassDto
            {
                Name = "gfm-bess", Kind = "gridFormingInverter", Mode = "grid-forming",
                Params = new() { ["synInertiaMwsPerHz"] = 12.5, ["socInit"] = 0.5 },
                Provenance = new()
                {
                    ["_default"] = new ProvenanceDto { Tag = "CuratedPlaceholder", Note = "fixture [VERIFY]" },
                    ["socInit"] = new ProvenanceDto { Tag = "Synthetic", Note = "fixture" },
                },
            },
            new AssetClassDto
            {
                // No _default: the projector synthesises v1's own fallback and the back-mapper drops it again.
                Name = "prot-lfdd", Kind = "protection", Mode = "lfdd",
                Params = new() { ["lfddStageHz"] = 48.8 },
                Provenance = new() { ["lfddStageHz"] = new ProvenanceDto { Tag = "Recorded", Note = "Grid Code" } },
            },
            new AssetClassDto { Name = "cable-dtr", Kind = "line", Mode = "dtr", Params = new() { ["thermalTauMin"] = 20 }, Provenance = new() },
            new AssetClassDto { Name = "tx-oltc", Kind = "transformer", Mode = "oltc", Params = new() { ["tapStepPu"] = 0.0125 }, Provenance = new() },
            new AssetClassDto { Name = "gfl-wind", Kind = "gridFollowingInverter", Mode = "gfl", Params = new(), Provenance = new() },
            new AssetClassDto { Name = "sm", Kind = "synchronousMachine", Mode = "m", Params = new(), Provenance = new() },
        },
        AssetBindings = { new AssetBindingDto { MatchType = "wind*", Class = "gfl-wind" } },
        Protections = { new ProtectionInstanceDto { Name = "P1", Class = "prot-lfdd", MonitorBus = 8, CapacityMw = 25, TargetBranch = "L1" } },
    };

    [Fact]
    public void The_every_field_fixture_really_sets_every_field()
    {
        // Guard on the guard: if a property of the synthetic case were left at its default, a projector bug on that
        // field would round-trip silently (default out, default back). Every property of the first element of each
        // list must differ from a fresh DTO's value.
        V1Case v1 = EveryFieldSet();
        var samples = new object[]
        {
            v1.System!, v1.Buses[0], v1.Buses[0].Zip!, v1.Generators[0], v1.Demands[0], v1.Lines[0], v1.Transformers[0], v1.Oltcs[0],
            v1.Boundaries[0], v1.Boundaries[0].Branches![0], v1.AssetClasses[0], v1.AssetClasses[0].Provenance!["socInit"],
            v1.AssetBindings[0], v1.Protections[0],
        };
        Assert.Equal(V1Projector.HandledMembers.Count, samples.Select(s => s.GetType()).Distinct().Count());
        foreach (object s in samples)
        {
            object fresh = Activator.CreateInstance(s.GetType())!;
            foreach (PropertyInfo p in s.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                Assert.True(V1Projector.Differences(p.GetValue(fresh), p.GetValue(s)).Count > 0, $"{s.GetType().Name}.{p.Name} is still its default");
        }
        // Every v1 kind in the projector's table is exercised.
        Assert.Equal(V1Projector.KindV1ToV2.Keys.OrderBy(k => k, StringComparer.Ordinal),
                     v1.AssetClasses.Select(c => c.Kind).Distinct().OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void A_v1_case_with_every_field_set_projects_to_valid_v2_and_maps_back_field_for_field()
    {
        V1Case v1 = EveryFieldSet();
        V2Projection v2 = V1Projector.Project(v1);
        using JsonDocument caseDoc = JsonDocument.Parse(v2.CaseJson, JsonSchemaSubset.ReaderOptions);
        using JsonDocument networkDoc = JsonDocument.Parse(v2.NetworkJson, JsonSchemaSubset.ReaderOptions);
        IReadOnlyList<SchemaError> errors = Validator.Value.Validate(caseDoc.RootElement, "case.schema.json")
            .Concat(Validator.Value.Validate(networkDoc.RootElement, "network.schema.json")).ToList();
        Assert.True(errors.Count == 0, Dump(errors));

        V1Case back = V1Projector.Back(caseDoc.RootElement, networkDoc.RootElement);
        List<string> diffs = V1Projector.Differences(v1, back);
        Assert.True(diffs.Count == 0, string.Join(Environment.NewLine, diffs));

        // Spot checks on the v2 spelling, so the round trip cannot pass by mapping a field to itself under the old name.
        JsonElement gen = networkDoc.RootElement.GetProperty("generators")[0];
        Assert.Equal("gridsim.local.gfm-bess@1", gen.GetProperty("template").GetString());
        Assert.True(gen.GetProperty("converter").GetProperty("gridForming").GetBoolean());
        Assert.Equal(0.0, gen.GetProperty("converter").GetProperty("faultCurrentPu").GetDouble());
        Assert.Equal(0.12, gen.GetProperty("unitTransformerXPu").GetDouble());
        JsonElement line = networkDoc.RootElement.GetProperty("lines")[0];
        Assert.Equal(0.033, line.GetProperty("x0Pu").GetDouble());
        Assert.False(line.TryGetProperty("xPu0", out _));
        JsonElement lfdd = caseDoc.RootElement.GetProperty("localTemplates")[1];
        Assert.Equal("protection", lfdd.GetProperty("kind").GetString());
        Assert.Equal(V1Projector.V1FallbackNote, lfdd.GetProperty("provenance").GetProperty("_default").GetProperty("note").GetString());
    }

    [Fact]
    public void An_absent_v2_member_means_exactly_what_the_absent_v1_key_means()
    {
        // The absence rule, checked against the real v1 deserialiser rather than a fresh DTO: a minimal v1 file
        // (required keys only) and the minimal v2 network with the same members give equal DTOs.
        // Moved 2026-10 (N065, authorised by Mark's 2026-10-02 best-judgement ruling): bus baseKv is required in v2 from Phase 2
        // (as in the v1 loader since R179), so the minimal bus carries it, in the v2 text and in the v1 text below alike.
        // Before: "buses": [ { "id": 1 } ] in both. After: [ { "id": 1, "baseKv": 132 } ] in both.
        const string v2Text = """
            { "schema": "gridsim-network/2",
              "buses": [ { "id": 1, "baseKv": 132 } ],
              "generators": [ { "name": "G", "bus": 1 } ],
              "demands": [ { "name": "D", "bus": 1, "pMw": 10 } ],
              "lines": [ { "name": "L", "from": 1, "to": 2, "rPu": 0.01, "xPu": 0.1 } ],
              "transformers": [ { "name": "T", "from": 1, "to": 2, "rPu": 0.01, "xPu": 0.1 } ],
              "oltcs": [ { "transformer": "T", "controlsBus": 2 } ],
              "boundaries": [ { "name": "B", "limitMw": 5, "branches": [ { "branch": "L" } ] } ],
              "protections": [ { "name": "P", "template": "gridsim.local.p@1", "monitorBus": 1 } ] }
            """;
        const string caseText = """{ "schema": "gridsim-case/2", "name": "n", "system": { "baseMva": 100, "frequencyHz": 50 }, "network": ["network.json"] }""";
        using JsonDocument net = JsonDocument.Parse(v2Text, JsonSchemaSubset.ReaderOptions);
        using JsonDocument cas = JsonDocument.Parse(caseText, JsonSchemaSubset.ReaderOptions);
        Assert.True(Validator.Value.Validate(net.RootElement, "network.schema.json").Count == 0);
        V1Case back = V1Projector.Back(cas.RootElement, net.RootElement);

        JsonSerializerOptions o = V1Projector.LoaderOptions;
        var v1 = new V1Case
        {
            System = new SystemDto { Name = "n" },
            Buses = JsonSerializer.Deserialize<List<BusDto>>("""[ { "id": 1, "baseKv": 132 } ]""", o)!,
            Generators = JsonSerializer.Deserialize<List<GenDto>>("""[ { "name": "G", "bus": 1 } ]""", o)!,
            Demands = JsonSerializer.Deserialize<List<DemandDto>>("""[ { "name": "D", "bus": 1, "pMw": 10 } ]""", o)!,
            Lines = JsonSerializer.Deserialize<List<LineDto>>("""[ { "name": "L", "from": 1, "to": 2, "rPu": 0.01, "xPu": 0.1 } ]""", o)!,
            Transformers = JsonSerializer.Deserialize<List<TransformerDto>>("""[ { "name": "T", "from": 1, "to": 2, "rPu": 0.01, "xPu": 0.1 } ]""", o)!,
            Oltcs = JsonSerializer.Deserialize<List<OltcDto>>("""[ { "transformer": "T", "controlsBus": 2 } ]""", o)!,
            Boundaries = JsonSerializer.Deserialize<List<BoundaryDto>>("""[ { "name": "B", "limitMw": 5, "branches": [ { "branch": "L" } ] } ]""", o)!,
            Protections = JsonSerializer.Deserialize<List<ProtectionInstanceDto>>("""[ { "name": "P", "class": "p", "monitorBus": 1 } ]""", o)!,
        };
        List<string> diffs = V1Projector.Differences(v1, back);
        Assert.True(diffs.Count == 0, string.Join(Environment.NewLine, diffs));
    }

    [Fact]
    public void The_synthesised_default_provenance_is_v1s_own_fallback()
    {
        // A v1 class without _default answers ProvenanceFor with a fixed CuratedPlaceholder note. The projector
        // writes that same provenance as the v2 template's required _default, so the two must stay equal.
        var cls = new AssetClass("x", AssetKind.Protection, "", new Dictionary<string, double> { ["k"] = 1 },
                                 new Dictionary<string, AssetClassProvenance>());
        AssetClassProvenance p = cls.ProvenanceFor("k");
        Assert.Equal(V1Projector.V1FallbackTag, p.Tag.ToString());
        Assert.Equal(V1Projector.V1FallbackNote, p.Note);
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, Enum.Parse<ProvenanceTag>(V1Projector.V1FallbackTag));
    }

    [Fact]
    public void The_kind_table_is_one_to_one_and_covers_every_AssetKind()
    {
        Assert.Equal(V1Projector.KindV1ToV2.Count, V1Projector.KindV1ToV2.Values.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Enum.GetNames<AssetKind>().Select(n => n.ToLowerInvariant()).OrderBy(n => n, StringComparer.Ordinal),
                     V1Projector.KindV1ToV2.Keys.Select(k => k.ToLowerInvariant()).OrderBy(n => n, StringComparer.Ordinal));
    }

    // ----- v2-only content has no v1 spelling, and saying so is loud -----

    private const string MinimalCase = """{ "schema": "gridsim-case/2", "name": "n", "system": { "baseMva": 100, "frequencyHz": 50 }, "network": ["network.json"] }""";

    [Theory]
    [InlineData("""{ "generators": [ { "name": "G", "bus": 1, "subtransientXdPu": 0.2 } ] }""")]
    [InlineData("""{ "generators": [ { "name": "G", "bus": 1, "negativeSeqXPu": 0.2 } ] }""")]
    // Moved 2026-10 (F16, authorised by Mark's 2026-10-02 best-judgement ruling): an out-of-service v2 generator
    // ("inService": false) was listed here as having no v1 spelling. F16 gave v1 generation.json an inService member
    // (the loader used to have no generator status at all, release gap F16), so it now maps back; see
    // An_out_of_service_generator_maps_back_to_v1 below. Before: this InlineData expected NotSupportedException.
    // After: removed from this theory; the new fact asserts the mapping.
    [InlineData("""{ "generators": [ { "name": "G", "bus": 1, "template": "gridsim.machine.genrou@1" } ] }""")]
    [InlineData("""{ "lines": [ { "name": "L", "from": 1, "to": 2, "rPu": 0, "xPu": 0.1, "type": "gridsim.linetype.x@1" } ] }""")]
    [InlineData("""{ "lines": [ { "name": "L", "from": 1, "to": 2, "rPu": 0, "xPu": 0.1, "inService": false } ] }""")]
    [InlineData("""{ "transformers": [ { "name": "T", "from": 1, "to": 2, "rPu": 0, "xPu": 0.1, "windings": { "from": "Delta", "to": "WyeGrounded" } } ] }""")]
    [InlineData("""{ "transformers": [ { "name": "T", "from": 1, "to": 2, "rPu": 0, "xPu": 0.1, "x0Pu": 0.1 } ] }""")]
    [InlineData("""{ "transformers": [ { "name": "T", "from": 1, "to": 2, "rPu": 0, "xPu": 0.1, "neutralXPu": { "from": 0.1 } } ] }""")]
    [InlineData("""{ "protections": [ { "name": "P", "template": "gridsim.local.p@2", "monitorBus": 1 } ] }""")]
    public void V2_only_network_content_cannot_be_mapped_back_to_v1(string members)
    {
        string text = """{ "schema": "gridsim-network/2", """ + members[1..];
        using JsonDocument net = JsonDocument.Parse(text, JsonSchemaSubset.ReaderOptions);
        Assert.True(Validator.Value.Validate(net.RootElement, "network.schema.json").Count == 0, "the probe itself must be valid v2");
        using JsonDocument cas = JsonDocument.Parse(MinimalCase, JsonSchemaSubset.ReaderOptions);
        Assert.Throws<NotSupportedException>(() => V1Projector.Back(cas.RootElement, net.RootElement));
    }

    [Theory]
    [InlineData("\"imports\": [ { \"library\": \"l.json\", \"templates\": [\"gridsim.*\"] } ]")]
    [InlineData("\"overrides\": [ { \"template\": \"gridsim.x@1\", \"params\": { \"h\": 1 }, \"reason\": \"r\", \"shadow\": \"explicit\" } ]")]
    [InlineData("\"gridCodeProfile\": \"gridsim.gridcode.gb@1\"")]
    [InlineData("\"solverProfile\": \"gridsim.solver.default@1\"")]
    [InlineData("\"bindings\": [ { \"matchType\": \"wind\", \"template\": \"gridsim.library.gfl@1\" } ]")]
    public void V2_only_case_content_cannot_be_mapped_back_to_v1(string member)
    {
        string text = MinimalCase[..^1] + ", " + member + " }";
        using JsonDocument cas = JsonDocument.Parse(text, JsonSchemaSubset.ReaderOptions);
        Assert.True(Validator.Value.Validate(cas.RootElement, "case.schema.json").Count == 0, "the probe itself must be valid v2");
        using JsonDocument net = JsonDocument.Parse("""{ "schema": "gridsim-network/2" }""", JsonSchemaSubset.ReaderOptions);
        Assert.Throws<NotSupportedException>(() => V1Projector.Back(cas.RootElement, net.RootElement));
    }

    [Fact]
    public void An_out_of_service_generator_maps_back_to_v1()
    {
        // F16: v1 GenDto.InService carries the v2 generator inService member both ways.
        const string text = """{ "schema": "gridsim-network/2", "generators": [ { "name": "G", "bus": 1, "inService": false } ] }""";
        using JsonDocument net = JsonDocument.Parse(text, JsonSchemaSubset.ReaderOptions);
        Assert.True(Validator.Value.Validate(net.RootElement, "network.schema.json").Count == 0, "the probe itself must be valid v2");
        using JsonDocument cas = JsonDocument.Parse(MinimalCase, JsonSchemaSubset.ReaderOptions);
        V1Case back = V1Projector.Back(cas.RootElement, net.RootElement);
        Assert.Empty(V1Projector.Differences(new GenDto { Name = "G", Bus = 1, InService = false }, back.Generators.Single()));
    }

    [Fact]
    public void In_service_true_is_what_v1_means_and_maps_back()
    {
        const string text = """{ "schema": "gridsim-network/2", "generators": [ { "name": "G", "bus": 1, "inService": true } ] }""";
        using JsonDocument net = JsonDocument.Parse(text, JsonSchemaSubset.ReaderOptions);
        using JsonDocument cas = JsonDocument.Parse(MinimalCase, JsonSchemaSubset.ReaderOptions);
        V1Case back = V1Projector.Back(cas.RootElement, net.RootElement);
        Assert.Empty(V1Projector.Differences(new GenDto { Name = "G", Bus = 1 }, back.Generators.Single()));
    }
}
