using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation;
using GridSim.Core.Events;
using GridSim.Core.Evidence;
using GridSim.Core.Gda;
using GridSim.Core.Interpose;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.PyPsa;
using GridSim.Core.Model;
using GridSim.Core.Net;
using GridSim.Core.PowerFlow;
using GridSim.Core.Scenarios;
using GridSim.Core.Synchrophasor;
using GridSim.Core.Viz;
using Xunit;

namespace GridSim.Tests.JsonSourceGen;

/// <summary>
/// UI programme Phase 0.1 (source-generated JSON in Core, so Core runs under NativeAOT): the equivalence proof for every
/// JSON READER and for the simple writers (the exporters with their own documents are in
/// <see cref="JsonSourceGenExportEquivalenceTests"/>).
/// <para>The "old path" is the System.Text.Json reflection serializer with a copy of the options each call site used
/// before 2.0.49 (the <c>Legacy*</c> options below, copied from the 2.0.48 source; they are the old behaviour, frozen,
/// and must never be edited to follow the production code). The "new path" is the production source-generated context
/// itself (each call site's <c>Json</c> field). For every input:</para>
/// <list type="bullet">
/// <item>a load gives deep-equal objects on both paths (doubles by their bits, see <see cref="JsonSourceGenDeepEquality"/>),
/// or both paths fail with the same exception type and message;</item>
/// <item>a write gives the same bytes on both paths;</item>
/// <item>a v1 case folder also builds into deep-equal <see cref="GridModel"/>s.</item>
/// </list>
/// The inputs are every fixture and bundled file of each kind in the repository (data/, tests/, docs/_captures/), found
/// by walking the tree and reading each file's <c>schema</c> tag, plus representative inputs built here where the
/// repository holds none. Each test also requires a minimum number of inputs, so a walk that silently found nothing
/// fails rather than passes.
/// </summary>
public class JsonSourceGenEquivalenceTests
{
    // ---- The old options, copied verbatim from the 2.0.48 call sites (the reflection path) ----------------------------

    // JsonGridLoader (v1 case folder files).
    private static readonly JsonSerializerOptions LegacyCaseFolder = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    // MeasurementSet, EventCatalog and RtuPointMap (all three used exactly these settings).
    private static readonly JsonSerializerOptions LegacyCamelEnums = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    // HarmonicSourceSet.
    private static readonly JsonSerializerOptions LegacyHarmonicSet = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    // GdaTimeSeries.
    private static readonly JsonSerializerOptions LegacyGda = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    // SolveImport.
    private static readonly JsonSerializerOptions LegacySolveImport = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // ScenarioSpec (DeviceConfig).
    private static readonly JsonSerializerOptions LegacyDevice = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // PmuChannelMapFile.
    private static readonly JsonSerializerOptions LegacyPmuMap = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = false,
    };

    // PypsaGbReduced boundary files.
    private static readonly JsonSerializerOptions LegacyPypsa = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    // EvidenceReport.
    private static readonly JsonSerializerOptions LegacyEvidence = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    // VizExport.
    private static readonly JsonSerializerOptions LegacyViz = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    // PmuLiveQuarantine sidecar.
    private static readonly JsonSerializerOptions LegacySidecar = new() { WriteIndented = true };

    // The bridge: BridgeProtocol.JsonOptions is still the unchanged reflection options object, so the old path is it.
    private static JsonSerializerOptions LegacyBridge => BridgeProtocol.JsonOptions;

    // ---- Helpers -------------------------------------------------------------------------------------------------

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>Every *.json file under data/, tests/ (not build output) and docs/_captures/, in a fixed order.</summary>
    private static IReadOnlyList<string> RepoJsonFiles()
    {
        string root = Root();
        var files = new List<string>();
        foreach (string top in new[] { "data", "tests", Path.Combine("docs", "_captures") })
        {
            string d = Path.Combine(root, top);
            if (!Directory.Exists(d)) continue;
            foreach (string f in Directory.EnumerateFiles(d, "*.json", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                if (rel.Contains("/bin/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal)) continue;
                files.Add(f);
            }
        }
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    /// <summary>The root "schema" string of a JSON file, or null (not an object, no schema, or not parseable).</summary>
    private static string? SchemaOf(string path)
    {
        try
        {
            using JsonDocument d = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return d.RootElement.ValueKind == JsonValueKind.Object
                && d.RootElement.TryGetProperty("schema", out JsonElement s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static IReadOnlyList<string> FilesWithSchema(string schema) =>
        RepoJsonFiles().Where(f => SchemaOf(f) == schema).ToList();

    private static (T? Value, Exception? Error) Try<T>(Func<T?> f)
    {
        try { return (f(), null); }
        catch (Exception ex) { return (default, ex); }
    }

    /// <summary>
    /// Deserialise <paramref name="json"/> on both paths and require the same outcome: deep-equal objects, or the same
    /// exception type and message on both. Returns true when both loaded (false when both failed alike).
    /// </summary>
    private static bool SameLoad<T>(string json, JsonSerializerOptions legacy, JsonTypeInfo<T> current, string what)
    {
        (T? a, Exception? ea) = Try(() => JsonSerializer.Deserialize<T>(json, legacy));
        (T? b, Exception? eb) = Try(() => JsonSerializer.Deserialize(json, current));
        if (ea is not null || eb is not null)
        {
            Assert.True(ea is not null && eb is not null,
                $"{what}: only one path failed (reflection: {ea?.GetType().Name} {ea?.Message}; source-generated: {eb?.GetType().Name} {eb?.Message}).");
            Assert.Equal(ea!.GetType(), eb!.GetType());
            Assert.Equal(ea.Message, eb.Message);
            return false;
        }
        JsonSourceGenDeepEquality.AssertDeepEqual(a, b, what);
        return true;
    }

    /// <summary>The same bytes, compared as UTF-8 (a string compare would also do; the bytes are what a file holds).</summary>
    private static void SameBytes(string legacy, string current, string what)
    {
        byte[] a = Encoding.UTF8.GetBytes(legacy), b = Encoding.UTF8.GetBytes(current);
        if (a.AsSpan().SequenceEqual(b)) return;
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        Assert.Fail($"{what}: the bytes differ at offset {i} of {a.Length} / {b.Length}: reflection '...{Excerpt(legacy, i)}...' vs source-generated '...{Excerpt(current, i)}...'.");
    }

    private static string Excerpt(string s, int at)
    {
        int from = Math.Max(0, Math.Min(at, s.Length) - 40);
        return s.Substring(from, Math.Min(80, s.Length - from));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gridsim-p01-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { } }
    }

    // ---- v1 case folders -------------------------------------------------------------------------------------------

    /// <summary>Every v1 case folder in the repository (a folder holding a system.json).</summary>
    private static IReadOnlyList<string> CaseFolders() =>
        RepoJsonFiles().Where(f => System.IO.Path.GetFileName(f) == "system.json")
            .Select(f => System.IO.Path.GetDirectoryName(f)!).Distinct().ToList();

    [Fact]
    public void Every_v1_case_folder_file_loads_to_the_same_dtos_on_both_paths()
    {
        IReadOnlyList<string> folders = CaseFolders();
        Assert.True(folders.Count >= 10, $"expected the bundled v1 case folders, found {folders.Count}");
        int files = 0;
        foreach (string dir in folders)
            foreach (CaseFileSpec spec in CaseFileSpec.V1Files)
            {
                string path = System.IO.Path.Combine(dir, spec.FileName);
                if (!File.Exists(path)) continue;
                string json = File.ReadAllText(path);
                Type t = spec.IsArray ? typeof(List<>).MakeGenericType(spec.DtoType) : spec.DtoType;
                (object? a, Exception? ea) = Try(() => JsonSerializer.Deserialize(json, t, LegacyCaseFolder));
                (object? b, Exception? eb) = Try(() => JsonSerializer.Deserialize(json, JsonGridLoader.Json.Options.GetTypeInfo(t)));
                Assert.True((ea is null) == (eb is null), $"{path}: only one path failed ({ea?.Message} / {eb?.Message}).");
                if (ea is not null) { Assert.Equal(ea.Message, eb!.Message); continue; }
                JsonSourceGenDeepEquality.AssertDeepEqual(a, b, path);
                files++;
            }
        Assert.True(files >= 40, $"expected the case folders' files, compared {files}");
    }

    [Fact]
    public void Every_v1_case_folder_builds_the_same_grid_model_on_both_paths()
    {
        IReadOnlyList<string> folders = CaseFolders();
        Assert.True(folders.Count >= 10, $"expected the bundled v1 case folders, found {folders.Count}");
        foreach (string dir in folders)
        {
            // The old path: the eleven files read by reflection with the old options, built by the unchanged Build.
            List<T> Arr<T>(string file)
            {
                string p = System.IO.Path.Combine(dir, file);
                return File.Exists(p) ? JsonSerializer.Deserialize<List<T>>(File.ReadAllText(p), LegacyCaseFolder) ?? new List<T>() : new List<T>();
            }
            string sys = System.IO.Path.Combine(dir, "system.json");
            var legacyDtos = new CaseDtos
            {
                System = JsonSerializer.Deserialize<SystemDto>(File.ReadAllText(sys), LegacyCaseFolder),
                Buses = Arr<BusDto>("buses.json"),
                Generators = Arr<GenDto>("generation.json"),
                Demands = Arr<DemandDto>("demand.json"),
                Lines = Arr<LineDto>("transmission.json"),
                Transformers = Arr<TransformerDto>("transformer.json"),
                Oltcs = Arr<OltcDto>("onloadtapchanger.json"),
                AssetClasses = Arr<AssetClassDto>("asset-classes.json"),
                AssetBindings = Arr<AssetBindingDto>("asset-bindings.json"),
                Protections = Arr<ProtectionInstanceDto>("protections.json"),
                Boundaries = () => Arr<BoundaryDto>("boundaries.json"),
            };
            string name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(dir));
            (GridModel? old, Exception? eo) = Try(() => JsonGridLoader.Build(legacyDtos, name, dispatch: true));
            (GridModel? cur, Exception? ec) = Try(() => JsonGridLoader.LoadDirectory(dir));
            Assert.True((eo is null) == (ec is null), $"{dir}: only one path failed ({eo?.Message} / {ec?.Message}).");
            if (eo is not null) { Assert.Equal(eo.Message, ec!.Message); continue; }
            long leaves = JsonSourceGenDeepEquality.AssertDeepEqual(old, cur, dir);
            Assert.True(leaves > 10, $"{dir}: compared only {leaves} values");
        }
    }

    [Fact]
    public void Malformed_case_files_fail_alike_on_both_paths()
    {
        // The R268 rules (unknown key, duplicate key) and a wrong type, each refused the same way by both paths.
        string[] bad =
        {
            """[{"id": 1, "type": "slack", "baseKv": 400, "rating_MVA": 5}]""",
            """[{"id": 1, "type": "slack", "baseKv": 400, "baseKV": 132}]""",
            """[{"id": "one", "type": "slack"}]""",
            """[{"id": 1, "type": "slack", /* a comment */ "baseKv": 400,},]""",
        };
        foreach (string json in bad)
            SameLoad(json, LegacyCaseFolder, JsonGridLoader.Json.ListBusDto, json);
    }

    // ---- Measurement sets, event catalogs, harmonic source sets, RTU point maps -----------------------------------

    [Fact]
    public void Every_measurement_set_loads_and_saves_the_same_on_both_paths()
    {
        IReadOnlyList<string> files = FilesWithSchema(MeasurementSet.SchemaId);
        Assert.True(files.Count >= 10, $"expected the measurement-set fixtures, found {files.Count}");
        foreach (string f in files)
        {
            string json = File.ReadAllText(f);
            SameLoad(json, LegacyCamelEnums, MeasurementSet.JsonInfo, f);
            MeasurementSet? set = JsonSerializer.Deserialize(json, MeasurementSet.JsonInfo);
            if (set is null) continue;
            SameBytes(JsonSerializer.Serialize(set, LegacyCamelEnums), JsonSerializer.Serialize(set, MeasurementSet.JsonInfo), f);
        }
    }

    [Fact]
    public void A_sampled_measurement_set_saves_the_same_bytes_and_round_trips()
    {
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth) with { Note = "phase 0.1" };
        using var tmp = new TempDir();
        string path = System.IO.Path.Combine(tmp.Path, "m.json");
        set.Save(path);
        SameBytes(JsonSerializer.Serialize(set, LegacyCamelEnums), File.ReadAllText(path), "case14 measurement set");
        MeasurementSet back = MeasurementSet.Load(path);
        JsonSourceGenDeepEquality.AssertDeepEqual(
            JsonSerializer.Deserialize<MeasurementSet>(File.ReadAllText(path), LegacyCamelEnums), back, "round trip");
    }

    [Fact]
    public void Every_event_catalog_and_the_built_ins_load_and_save_the_same()
    {
        IReadOnlyList<string> files = FilesWithSchema(EventCatalog.SchemaId);
        Assert.True(files.Count >= 3, $"expected the event-catalog files, found {files.Count}");
        foreach (string f in files)
            SameLoad(File.ReadAllText(f), LegacyCamelEnums, EventCatalog.Json.CatalogDoc, f);

        IReadOnlyList<GridEvent> builtin = EventCatalog.Builtin();
        var doc = new EventCatalog.CatalogDoc(EventCatalog.SchemaId, builtin);
        using var tmp = new TempDir();
        string path = System.IO.Path.Combine(tmp.Path, "events.json");
        EventCatalog.Save(path, builtin);
        string written = File.ReadAllText(path);
        SameBytes(JsonSerializer.Serialize(doc, LegacyCamelEnums), written, "built-in event catalog");
        JsonSourceGenDeepEquality.AssertDeepEqual(builtin, EventCatalog.Load(path), "built-in catalog round trip");
    }

    [Fact]
    public void Every_harmonic_source_set_loads_and_saves_the_same()
    {
        IReadOnlyList<string> files = FilesWithSchema(HarmonicSourceSet.SchemaId);
        Assert.True(files.Count >= 3, $"expected the harmonic source-set files, found {files.Count}");
        foreach (string f in files)
        {
            string json = File.ReadAllText(f);
            if (!SameLoad(json, LegacyHarmonicSet, HarmonicSourceSet.JsonInfo, f)) continue;
            HarmonicSourceSet set = JsonSerializer.Deserialize(json, HarmonicSourceSet.JsonInfo)!;
            SameBytes(JsonSerializer.Serialize(set, LegacyHarmonicSet), JsonSerializer.Serialize(set, HarmonicSourceSet.JsonInfo), f);
        }
    }

    [Fact]
    public void Every_rtu_point_map_loads_and_saves_the_same()
    {
        IReadOnlyList<string> files = FilesWithSchema(RtuPointMap.SchemaId);
        Assert.True(files.Count >= 3, $"expected the RTU point-map files, found {files.Count}");
        using var tmp = new TempDir();
        foreach (string f in files)
        {
            string json = File.ReadAllText(f);
            if (!SameLoad(json, LegacyCamelEnums, RtuPointMap.JsonInfo, f)) continue;
            RtuPointMap map = JsonSerializer.Deserialize(json, RtuPointMap.JsonInfo)!;
            string path = System.IO.Path.Combine(tmp.Path, System.IO.Path.GetFileName(f));
            map.Save(path);
            SameBytes(JsonSerializer.Serialize(map, LegacyCamelEnums), File.ReadAllText(path), f);
        }
    }

    // ---- Solve-export import and the bridge --------------------------------------------------------------------------

    /// <summary>Solve exports to read back: every one in the repository plus fresh ones from three IEEE cases.</summary>
    private static IReadOnlyList<(string Name, string Json)> SolveExports()
    {
        var list = FilesWithSchema("gridsim-solve-export/1").Select(f => (f, File.ReadAllText(f))).ToList();
        foreach ((string name, GridModel model) in new[] { ("case9", IeeeCases.Case9()), ("case14", IeeeCases.Case14()), ("case30", IeeeCases.Case30()) })
        {
            var trace = new SolveTrace();
            PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(model, new PowerFlowOptions { Trace = trace });
            var meta = new SolveExportMeta(name, "cpu", "deadbeef", "2026-10-20T00:00:00Z", 1.0, "test");
            list.Add((name, SolveExport.ToJson(model, r, trace, meta)));
        }
        return list;
    }

    [Fact]
    public void Every_solve_export_reads_back_the_same_on_both_paths()
    {
        IReadOnlyList<(string Name, string Json)> exports = SolveExports();
        Assert.True(exports.Count >= 4, $"expected the solve exports, found {exports.Count}");
        foreach ((string name, string json) in exports)
            Assert.True(SameLoad(json, LegacySolveImport, SolveImport.Json.RootDto, name), $"{name} did not load");
    }

    [Fact]
    public void Bridge_messages_read_the_same_on_both_paths()
    {
        int n = 0;
        foreach ((string name, string export) in SolveExports())
        {
            using JsonDocument d = JsonDocument.Parse(export);
            string model = d.RootElement.GetProperty("model").GetRawText();
            string result = d.RootElement.GetProperty("result").GetRawText();
            string topology = $$"""{ "type": "topology", "model": {{model}} }""";
            string state = $$"""{ "type": "state", "t": "2024-01-01T00:00:00Z", "result": {{result}}, "source": "presolved" }""";
            string reply = $$"""{ "type": "result", "op": "inertia", "id": "7", "at": "2024-01-01T00:00:00Z", "payload": { "solveExport": {{export}}, "verdict": "ok" } }""";
            Assert.True(SameLoad(topology, LegacyBridge, BridgeProtocol.Json.TopologyMessage, name + " topology"));
            Assert.True(SameLoad(state, LegacyBridge, BridgeProtocol.Json.StateMessage, name + " state"));
            Assert.True(SameLoad(reply, LegacyBridge, BridgeProtocol.Json.ResultMessage, name + " result"));
            n++;
        }
        Assert.True(SameLoad("""{ "type": "error", "message": "stream ended" }""", LegacyBridge, BridgeProtocol.Json.NoteMessage, "note"));
        Assert.True(n >= 4);
    }

    [Fact]
    public void Bridge_requests_write_the_same_bytes_as_the_old_anonymous_objects()
    {
        // The old path serialised these anonymous objects with BridgeProtocol.JsonOptions; the new path writes the named
        // records through the source-generated context. The request values cover the edge cases (a non-ASCII and an
        // escaped character in the case name, a fractional fps).
        const string c = "gb-full \"x\" \u00e9";
        const string at = "2024-01-01T00:00:00.0000000Z";
        Pair(new { action = "start", @case = c, start = at, fps = 2.5, source = "presolved" },
             new BridgeRequests.StartRequest("start", c, at, 2.5, "presolved"), BridgeProtocol.Json.StartRequest);
        Pair(new { action = "start", @case = c, start = "first", fps = 1.0, source = "estimated", loop = true },
             new BridgeRequests.StartLoopingRequest("start", c, "first", 1.0, "estimated", true), BridgeProtocol.Json.StartLoopingRequest);
        Pair(new { action = "stop" }, new BridgeRequests.StopRequest("stop"), BridgeProtocol.Json.StopRequest);
        Pair(new { action = "set_speed", fps = 0.1 }, new BridgeRequests.SetSpeedRequest("set_speed", 0.1), BridgeProtocol.Json.SetSpeedRequest);
        Pair(new { action = "seek", start = at, source = "live" }, new BridgeRequests.SeekRequest("seek", at, "live"), BridgeProtocol.Json.SeekRequest);
        Pair(new { action = "query", op = "propagation", id = "12", at, @params = new { } },
             new BridgeRequests.QueryRequest<BridgeRequests.NoParams>("query", "propagation", "12", at, new BridgeRequests.NoParams()),
             BridgeProtocol.Json.QueryNoParams);

        static void Pair<TOld, TNew>(TOld old, TNew current, JsonTypeInfo<TNew> info)
        {
            byte[] a = JsonSerializer.SerializeToUtf8Bytes(old, LegacyBridge);
            byte[] b = JsonSerializer.SerializeToUtf8Bytes(current, info);
            SameBytes(Encoding.UTF8.GetString(a), Encoding.UTF8.GetString(b), typeof(TNew).Name);
        }
    }

    // ---- GDA time series, device configs, PMU channel maps, PyPSA boundaries ----------------------------------------

    [Fact]
    public void A_gda_time_series_saves_the_same_bytes_and_loads_the_same()
    {
        GdaTimeSeries s = GdaTimeSeries.Synthetic("phase-0.1", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 12, 30, 0.93);
        using var tmp = new TempDir();
        string path = System.IO.Path.Combine(tmp.Path, "gda.json");
        s.Save(path);
        string written = File.ReadAllText(path);
        SameBytes(JsonSerializer.Serialize(s, LegacyGda), written, "synthetic GDA series");
        Assert.True(SameLoad(written, LegacyGda, GdaTimeSeries.Json.GdaTimeSeries, "synthetic GDA series"));
        JsonSourceGenDeepEquality.AssertDeepEqual(JsonSerializer.Deserialize<GdaTimeSeries>(written, LegacyGda), GdaTimeSeries.Load(path), "round trip");
        // An unknown key is refused alike (UnmappedMemberHandling.Disallow).
        SameLoad(written.Replace("\"steps\"", "\"stepz\"", StringComparison.Ordinal), LegacyGda, GdaTimeSeries.Json.GdaTimeSeries, "misspelt key");
    }

    [Fact]
    public void The_bundled_device_config_loads_and_saves_the_same()
    {
        string f = System.IO.Path.Combine(Root(), "data", "devices", "tsgb-fleet.json");
        string json = File.ReadAllText(f);
        Assert.True(SameLoad(json, LegacyDevice, DeviceConfig.Json.DeviceConfig, f));
        DeviceConfig d = DeviceConfig.Load(f);
        using var tmp = new TempDir();
        string path = System.IO.Path.Combine(tmp.Path, "device.json");
        d.Save(path);
        SameBytes(JsonSerializer.Serialize(d, LegacyDevice), File.ReadAllText(path), f);
    }

    [Fact]
    public void Pmu_channel_maps_load_the_same_on_both_paths()
    {
        const string good = """
        {
          // a comment is allowed
          "schema": "gridsim-pmu-channels/1", "case": "case9", "baseMva": 100,
          "reference": { "pmu": 7, "phasor": 0, "anchorDeg": 0.5 },
          "pmus": [
            { "pmu": 7,
              "voltages": [ { "phasor": 0, "bus": 1, "baseVolts": 230000, "vmSigmaPu": 0.001, "vaSigmaDeg": 0.01 } ],
              "currents": [ { "phasor": 1, "from": 1, "to": 4, "circuit": "1", "baseAmps": 1000, "iMagSigmaPu": 0.002, "iAngSigmaDeg": 0.02 } ] },
            null
          ]
        }
        """;
        Assert.True(SameLoad(good, LegacyPmuMap, PmuChannelMapFile.Json.FileDto, "pmu map"));
        // Key case is free; a trailing comma is refused alike.
        Assert.True(SameLoad(good.Replace("\"baseMva\"", "\"BASEMVA\"", StringComparison.Ordinal), LegacyPmuMap, PmuChannelMapFile.Json.FileDto, "key case"));
        Assert.False(SameLoad(good.Replace("\"anchorDeg\": 0.5 }", "\"anchorDeg\": 0.5, }", StringComparison.Ordinal), LegacyPmuMap, PmuChannelMapFile.Json.FileDto, "trailing comma"));
    }

    [Fact]
    public void Every_gridsim_format_boundary_file_loads_the_same()
    {
        string root = Root();
        string[] files =
        {
            System.IO.Path.Combine(root, "data", "matpower", "gb", "pypsa-gb", "reduced", "boundaries.json"),
            System.IO.Path.Combine(root, "data", "matpower", "gb", "pypsa-gb", "zonal", "boundaries.json"),
        };
        foreach (string f in files)
            Assert.True(SameLoad(File.ReadAllText(f), LegacyPypsa, PypsaGbReduced.StrictJson.ListBoundaryFile, f), f);
        // The side table is not a boundary list: both paths refuse it alike.
        Assert.False(SameLoad(File.ReadAllText(System.IO.Path.Combine(root, "data", "matpower", "gb", "pypsa-gb", "boundary-sides.json")),
            LegacyPypsa, PypsaGbReduced.StrictJson.ListBoundaryFile, "boundary-sides.json"));
    }

    // ---- Evidence, visualiser scene, PMU quarantine sidecar --------------------------------------------------------

    private static GridEvent SelfConsistentEvent(double inertiaMwS = 200_000, double demandMw = 35_000, double lossMw = 1320)
    {
        var cfg = new GridSim.Core.Dynamics.DynamicsConfig();
        var ev = new[] { new GridSim.Core.Dynamics.FrequencyEvent(1.0, -lossMw) };
        GridSim.Core.Dynamics.FrequencyResponse truth = GridSim.Core.Dynamics.FrequencyDynamics.Simulate(inertiaMwS, demandMw, ev, cfg);
        return new GridEvent(
            "syn", "self-consistent", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            inertiaMwS, demandMw,
            new List<PowerStep> { new(1.0, -lossMw, "loss", ProvenanceTag.Synthetic) },
            new List<RecordedAction>(),
            new RecordedOutcome(truth.InitialRocofHzPerS, truth.NadirHz, truth.SettleHz, 0, null, ProvenanceTag.Synthetic),
            ProvenanceTag.Synthetic);
    }

    [Fact]
    public void The_evidence_dossier_writes_the_same_bytes_as_the_old_anonymous_envelope()
    {
        var device = new DeviceConfig("tsgb", 40_000, 2000);
        GridEvent good = SelfConsistentEvent();
        EvidenceReport[] reports =
        {
            EvidenceReport.Build(EvidenceSpec.Default(good, device)),
            EvidenceReport.Build(EvidenceSpec.Default(good, device) with { AvoidedCost = new AvoidedCostInputs(6000, 0.5) }),
            // A baseline that does not reproduce: the counterfactual sections are withheld (null, so omitted).
            EvidenceReport.Build(EvidenceSpec.Default(good with { Outcome = good.Outcome with { NadirHz = good.Outcome.NadirHz + 3.0 } }, device)),
        };
        foreach (EvidenceReport r in reports)
            SameBytes(JsonSerializer.Serialize(new { schema = "gridsim-evidence/1", report = r }, LegacyEvidence), r.ToJson(), r.Summary);
    }

    [Fact]
    public void The_visualiser_scene_writes_the_same_bytes()
    {
        foreach (GridModel m in new[] { IeeeCases.Case9(), IeeeCases.Case14() })
        {
            VizScene scene = VizExport.Build(m, 6);
            SameBytes(JsonSerializer.Serialize(scene, LegacyViz), VizExport.ToJson(scene), m.Name);
        }
    }

    [Fact]
    public void The_pmu_quarantine_sidecar_writes_the_same_bytes_as_the_old_anonymous_object()
    {
        uint soc = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var pmu = new Doc15Frames.Pmu(7, "STN_A", new[] { Doc15Frames.Voltage, Doc15Frames.Current });
        byte[][] frames =
        {
            Doc15Frames.Config(1_000_000, soc, pmu),
            Doc15Frames.Data(soc, 0, new[] { (1.0f, 0.0f), (0.5f, -0.2f) }),
            Doc15Frames.Data(soc, 20_000, new[] { (1.0f, 0.01f), (0.5f, -0.2f) }),
        };
        var received = new DateTime(2026, 10, 11, 9, 30, 15, 250, DateTimeKind.Utc);
        using var tmp = new TempDir();
        foreach ((DateTime? oldest, DateTime? newest) in new (DateTime?, DateTime?)[] { (received.AddSeconds(-2), received.AddSeconds(-1)), (null, null) })
        {
            (string cap, string side) = PmuLiveQuarantine.Write(tmp.Path, frames, "tcp://127.0.0.1:4712", received, received.AddHours(-169), 2, oldest, newest);
            string written = File.ReadAllText(side);
            using JsonDocument d = JsonDocument.Parse(written);
            JsonElement r = d.RootElement;
            // The old anonymous object, rebuilt member by member from the same inputs the writer used.
            var old = new
            {
                schema = PmuLiveQuarantine.SidecarSchema,
                capture = System.IO.Path.GetFileName(cap),
                sha256 = r.GetProperty("sha256").GetString(),
                bytes = frames.Sum(f => (long)f.Length),
                frames = frames.Length,
                freshDataFrames = 2,
                source = "tcp://127.0.0.1:4712",
                receivedUtc = r.GetProperty("receivedUtc").GetString(),
                horizonUtc = r.GetProperty("horizonUtc").GetString(),
                horizonHours = ForwardInferenceGuard.HorizonHours,
                oldestFrameUtc = oldest is DateTime o ? UtcTime.Normalise(o).ToString("o", System.Globalization.CultureInfo.InvariantCulture) : null,
                newestFrameUtc = newest is DateTime n ? UtcTime.Normalise(n).ToString("o", System.Globalization.CultureInfo.InvariantCulture) : null,
                sourceClass = "telemetry",
                evidential = false,
                recorded = false,
                provenance = new { tag = PmuLiveQuarantine.Tag.ToString(), note = "lowest input trust; never Recorded; see the note" },
                note = PmuLiveQuarantine.Note,
            };
            SameBytes(JsonSerializer.Serialize(old, LegacySidecar), written, side);
        }
    }

    // ---- Every enum each context can reach is written as before ------------------------------------------------------

    /// <summary>
    /// For every source-generated context and every enum type its root types reach (through public properties,
    /// collection element types and nullable wrappers), each defined value is written to the same bytes as the old
    /// reflection path wrote it: by name where the old options had the string enum converter, as a number where they
    /// did not. This holds even for enum members no fixture happens to carry.
    /// </summary>
    [Fact]
    public void Every_reachable_enum_value_is_written_as_before()
    {
        var cases = new (string What, JsonSerializerOptions Legacy, JsonSerializerOptions Current, Type[] Roots)[]
        {
            ("measurement set", LegacyCamelEnums, MeasurementSet.JsonOptions, new[] { typeof(MeasurementSet) }),
            ("event catalog", LegacyCamelEnums, EventCatalog.Json.Options, new[] { typeof(EventCatalog.CatalogDoc) }),
            ("rtu point map", LegacyCamelEnums, RtuPointMap.JsonOptions, new[] { typeof(RtuPointMap) }),
            ("harmonic source set", LegacyHarmonicSet, HarmonicSourceSet.JsonOptions, new[] { typeof(HarmonicSourceSet) }),
            ("evidence", LegacyEvidence, EvidenceReport.Json.Options, new[] { typeof(EvidenceReport) }),
            ("device", LegacyDevice, DeviceConfig.Json.Options, new[] { typeof(DeviceConfig) }),
            ("gda", LegacyGda, GdaTimeSeries.Json.Options, new[] { typeof(GdaTimeSeries) }),
            ("solve import", LegacySolveImport, SolveImport.Json.Options, new[] { typeof(SolveImport.RootDto) }),
            ("bridge", LegacyBridge, BridgeProtocol.Json.Options,
                new[] { typeof(BridgeProtocol.TopologyMessage), typeof(BridgeProtocol.StateMessage), typeof(BridgeProtocol.ResultMessage) }),
            ("case folder", LegacyCaseFolder, JsonGridLoader.Json.Options, CaseFileSpec.V1Files.Select(f => f.DtoType).ToArray()),
            ("viz", LegacyViz, VizExport.Json.Options, new[] { typeof(VizScene) }),
        };
        int checkedValues = 0;
        foreach ((string what, JsonSerializerOptions legacy, JsonSerializerOptions current, Type[] roots) in cases)
            foreach (Type e in ReachableEnums(roots))
                foreach (object v in Enum.GetValues(e))
                {
                    SameBytes(JsonSerializer.Serialize(v, e, legacy), JsonSerializer.Serialize(v, current.GetTypeInfo(e)), $"{what}: {e.Name}.{v}");
                    checkedValues++;
                }
        Assert.True(checkedValues >= 20, $"expected the reachable enums, checked {checkedValues} values");
    }

    /// <summary>The enum types reachable from <paramref name="roots"/> through public instance properties, array and
    /// generic arguments (collections, nullables).</summary>
    internal static IReadOnlyList<Type> ReachableEnums(IEnumerable<Type> roots)
    {
        var seen = new HashSet<Type>();
        var enums = new SortedSet<Type>(Comparer<Type>.Create((a, b) => string.CompareOrdinal(a.FullName, b.FullName)));
        var queue = new Queue<Type>(roots);
        while (queue.Count > 0)
        {
            Type t = queue.Dequeue();
            if (!seen.Add(t)) continue;
            if (t.IsEnum) { enums.Add(t); continue; }
            if (t.IsArray) { queue.Enqueue(t.GetElementType()!); continue; }
            if (t.IsGenericType) foreach (Type g in t.GetGenericArguments()) queue.Enqueue(g);
            if (t.IsPrimitive || t == typeof(string) || t.Namespace?.StartsWith("System", StringComparison.Ordinal) == true) continue;
            foreach (System.Reflection.PropertyInfo p in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                queue.Enqueue(p.PropertyType);
        }
        return enums.ToList();
    }

    // ---- Init-only members with initialisers (see GridSim.Core.IO.Json.InitMemberDefaults) --------------------------

    /// <summary>The root types every source-generated READER deserialises.</summary>
    private static IReadOnlyList<Type> ReaderRoots() => new[]
    {
        typeof(MeasurementSet), typeof(EventCatalog.CatalogDoc), typeof(HarmonicSourceSet), typeof(RtuPointMap), typeof(GdaTimeSeries),
        typeof(DeviceConfig), typeof(SolveImport.RootDto), typeof(PmuChannelMapFile.FileDto), typeof(PypsaGbReduced.BoundaryFile),
        typeof(BridgeProtocol.TopologyMessage), typeof(BridgeProtocol.StateMessage), typeof(BridgeProtocol.ResultMessage),
        typeof(BridgeProtocol.NoteMessage),
    }.Concat(CaseFileSpec.V1Files.Select(f => f.DtoType)).ToList();

    /// <summary>
    /// The source generator builds a type with init-only properties in one object initializer that sets every one of
    /// them, so an initialiser is lost when the file leaves its member out. GridSim describes the four types that have
    /// such initialisers by hand (InitMemberDefaults). This scan fails if any other type a reader can reach gains an
    /// init-only property whose initialiser is not the type's default: it would then need adding there.
    /// </summary>
    [Fact]
    public void Every_init_only_member_with_an_initialiser_is_described_by_InitMemberDefaults()
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>(ReaderRoots());
        var found = new SortedSet<string>(StringComparer.Ordinal);
        int types = 0;
        while (queue.Count > 0)
        {
            Type t = queue.Dequeue();
            if (!seen.Add(t)) continue;
            if (t.IsArray) { queue.Enqueue(t.GetElementType()!); continue; }
            if (t.IsGenericType) foreach (Type g in t.GetGenericArguments()) queue.Enqueue(g);
            if (t.IsPrimitive || t.IsEnum || t.IsInterface || t.IsAbstract
                || t.Namespace?.StartsWith("System", StringComparison.Ordinal) == true) continue;
            types++;
            System.Reflection.PropertyInfo[] props = t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            foreach (System.Reflection.PropertyInfo p in props) queue.Enqueue(p.PropertyType);
            // Only a type that can be made without arguments has initialisers outside a constructor's own defaults (a
            // positional record's absent members take its constructor's default values, which the generator honours).
            if (t.GetConstructor(Type.EmptyTypes) is null) continue;
            object fresh = Activator.CreateInstance(t)!;
            foreach (System.Reflection.PropertyInfo p in props)
            {
                bool initOnly = p.SetMethod is { } set
                    && set.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
                if (!initOnly) continue;
                object? v = p.GetValue(fresh);
                object? dflt = p.PropertyType.IsValueType ? Activator.CreateInstance(p.PropertyType) : null;
                if (!Equals(v, dflt)) found.Add(t.FullName!);
            }
        }
        Assert.True(types >= 30, $"expected the readers' types, scanned {types}");
        Assert.Equal(InitMemberDefaults.Types.Select(t => t.FullName!).OrderBy(n => n, StringComparer.Ordinal), found);
    }

    /// <summary>
    /// For each of the four hand-described types: a document with every member present, then the same document with
    /// each member left out in turn, then each member written as null, loads the same on both paths. Leaving a member out
    /// keeps its initialiser (the defect InitMemberDefaults removes); a null is null; the written bytes match too.
    /// </summary>
    [Fact]
    public void Each_init_only_member_left_out_or_null_loads_as_under_reflection()
    {
        const string binding = """{ "address": "IOA:1", "kind": "PFlowMw", "bus": 0, "from": 1, "to": 2, "circuit": "1", "end": "To", "baseKv": 132, "baseAmps": 500, "scale": 0.1, "sigma": 0.02, "datumAngleDeg": 3, "dataType": "S16", "wordOrder": "LowWordFirst" }""";
        var docs = new (string What, string Json, JsonSerializerOptions Legacy, Func<string, (object? Value, Exception? Error)> Current)[]
        {
            ("measurement set",
             """{ "schema": "gridsim-measurements/1", "case": "c", "baseMva": 250, "takenUtc": "2024-01-01T00:00:00Z", "note": "n", "measurements": [ { "kind": "VmPu", "value": 1.01, "sigma": 0.004, "bus": 1 } ] }""",
             LegacyCamelEnums, j => Try<object?>(() => JsonSerializer.Deserialize(j, MeasurementSet.JsonInfo))),
            ("harmonic source set",
             """{ "schema": "gridsim-harmonics/1", "case": "c", "baseMva": 250, "takenUtc": "2024-01-01T00:00:00Z", "note": "n", "sources": [] }""",
             LegacyHarmonicSet, j => Try<object?>(() => JsonSerializer.Deserialize(j, HarmonicSourceSet.JsonInfo))),
            ("rtu point map",
             $$"""{ "schema": "gridsim-rtu-pointmap/1", "case": "c", "baseMva": 250, "bindings": [ {{binding}} ] }""",
             LegacyCamelEnums, j => Try<object?>(() => JsonSerializer.Deserialize(j, RtuPointMap.JsonInfo))),
            ("rtu binding",
             binding,
             LegacyCamelEnums, j => Try<object?>(() => JsonSerializer.Deserialize(j, (JsonTypeInfo<RtuPointBinding>)RtuPointMap.JsonOptions.GetTypeInfo(typeof(RtuPointBinding))))),
        };
        Type[] types = { typeof(MeasurementSet), typeof(HarmonicSourceSet), typeof(RtuPointMap), typeof(RtuPointBinding) };
        int variants = 0;
        for (int k = 0; k < docs.Length; k++)
        {
            (string what, string json, JsonSerializerOptions legacy, Func<string, (object?, Exception?)> current) = docs[k];
            using JsonDocument full = JsonDocument.Parse(json);
            var names = full.RootElement.EnumerateObject().Select(p => p.Name).ToList();
            var variantsOf = new List<(string Label, string Json)> { ("all members", json) };
            foreach (string name in names)
            {
                variantsOf.Add(($"without {name}", Rewrite(full.RootElement, name, null)));
                variantsOf.Add(($"{name} null", Rewrite(full.RootElement, name, "null")));
            }
            variantsOf.Add(("empty object", "{}"));
            foreach ((string label, string text) in variantsOf)
            {
                (object? a, Exception? ea) = Try(() => JsonSerializer.Deserialize(text, types[k], legacy));
                variants++;   // every variant counts, whether both paths loaded it or both refused it alike
                (object? b, Exception? eb) = current(text);
                Assert.True((ea is null) == (eb is null), $"{what}, {label}: only one path failed ({ea?.Message} / {eb?.Message}).");
                if (ea is not null) { Assert.Equal(ea.GetType(), eb!.GetType()); Assert.Equal(ea.Message, eb.Message); continue; }
                JsonSourceGenDeepEquality.AssertDeepEqual(a, b, $"{what}, {label}");
                SameBytes(JsonSerializer.Serialize(a, types[k], legacy),
                    JsonSerializer.Serialize(b, (types[k] == typeof(RtuPointBinding) ? RtuPointMap.JsonOptions : CurrentOptions(types[k])).GetTypeInfo(types[k])),
                    $"{what}, {label} (written back)");
            }
        }
        // 6 + 6 + 4 + 14 members, each present, absent and null, plus an empty object per type: 68 variants.
        Assert.Equal(68, variants);

        static JsonSerializerOptions CurrentOptions(Type t) =>
            t == typeof(MeasurementSet) ? MeasurementSet.JsonOptions : t == typeof(HarmonicSourceSet) ? HarmonicSourceSet.JsonOptions : RtuPointMap.JsonOptions;

        // The object with one member removed (replacement null) or replaced by a raw JSON value.
        static string Rewrite(JsonElement obj, string member, string? replacement)
        {
            var sb = new StringBuilder("{");
            bool first = true;
            foreach (JsonProperty p in obj.EnumerateObject())
            {
                if (p.Name == member && replacement is null) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append(JsonSerializer.Serialize(p.Name)).Append(':')
                  .Append(p.Name == member ? replacement : p.Value.GetRawText());
            }
            return sb.Append('}').ToString();
        }
    }
}
