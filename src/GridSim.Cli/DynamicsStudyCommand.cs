using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Analysis.DynamicsStudy;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;

namespace GridSim.Cli;

/// <summary>
/// RC3 P6: <c>gridsim dynamics-study &lt;study.json&gt; [--out &lt;result.json&gt;]</c>. Parses the strict v1 study
/// document, binds the RAW and DYR files by SHA-256 of their exact bytes, loads the RAW case through the incumbent loader
/// (licence enforced), and hands a typed request to <see cref="DynamicsStudyRunner"/>. The result JSON puts the model
/// coverage report before any verdict. The existing stability verbs and the --dyn-data paths are untouched.
/// </summary>
internal static class DynamicsStudyCommand
{
    private static readonly JsonSerializerOptions OutputJson = CreateOutputOptions();

    private static JsonSerializerOptions CreateOutputOptions()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        o.Converters.Add(new FiniteDoubleWriter());
        return o;
    }

    private sealed record InputFile(string Path, string Sha256);

    public static int Run(List<string> arguments, string root, Func<string, GridModel> load)
    {
        // The study file is the whole interpretation: a global RAW reading switch would change the case it binds.
        // CLI-07 (post-RC3): this verb is dispatched before the global switches are parsed, so the earlier test of the
        // Gated properties could never fire. The raw tokens are scanned here, before the argument-count check, so the
        // specific message is printed instead of the generic usage line.
        string[] rawSwitches = { "--allow-unmodelled", "--seq-data", "--tap-control", "--shunt-control", "--psse-revision", "--frequency-hz" };
        var inherited = new List<string>();
        foreach (string sw in rawSwitches)
            if (arguments.Skip(1).Contains(sw, StringComparer.Ordinal)) inherited.Add(sw);
        if (inherited.Count > 0)
        {
            Console.Error.WriteLine($"error: dynamics-study takes its interpretation options from the study file; {string.Join(", ", inherited)} would change how the RAW case is read, so it is refused (the unmodelled policy is initialisation.unmodelled).");
            return CliExit.Usage;
        }
        // RC5: --unit-protection <file> is the one option besides --out; each at most once, each with a value.
        string? outArgument = null, unitArgument = null, pluginsArgument = null, assignmentsArgument = null;
        bool valid = arguments.Count >= 2 && arguments.Count % 2 == 0 && !arguments[1].StartsWith('-');
        for (int i = 2; valid && i < arguments.Count; i += 2)
        {
            if (arguments[i + 1].StartsWith('-')) valid = false;
            else if (arguments[i] == "--out" && outArgument is null) outArgument = arguments[i + 1];
            else if (arguments[i] == "--unit-protection" && unitArgument is null) unitArgument = arguments[i + 1];
            // RC5 WP12: user models from a sidecar file, and plug-in models only from an explicit configuration.
            else if (arguments[i] == "--model-assignments" && assignmentsArgument is null) assignmentsArgument = arguments[i + 1];
            else if (arguments[i] == "--model-plugins" && pluginsArgument is null) pluginsArgument = arguments[i + 1];
            else valid = false;
        }
        if (!valid)
        {
            Console.Error.WriteLine("usage: gridsim dynamics-study <study.json> [--out <result.json>]");
            return CliExit.Usage;
        }
        if (pluginsArgument is not null && assignmentsArgument is null)
        {
            Console.Error.WriteLine("error: dynamics-study: --model-plugins needs --model-assignments to say where each plug-in model is bound (a governor, exciter, stabiliser, injection, machine or unit relay to a unit; a load to a bus; a branch relay to a branch).");
            return CliExit.Usage;
        }
        string? temporary = null;
        try
        {
            string input = Path.GetFullPath(arguments[1]);
            string? output = outArgument is null ? null : Path.GetFullPath(outArgument);
            // RC5 (2.1.127): in a re-run by breaking-change validate, the result must lie in the re-run's scratch folder.
            // Inert in every other run (the result is still written by this verb itself, as before).
            if (output is not null) CliFiles.ReplayGuard(output, "dynamics-study result");
            if (output is not null && (File.Exists(output) || Directory.Exists(output)))
                throw new IOException("Result path already exists; use a new path for this invocation.");
            byte[] studyBytes = File.ReadAllBytes(input);
            using JsonDocument document = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(studyBytes));
            JsonElement rootEl = document.RootElement;
            CheckDuplicates(rootEl);
            Obj(rootEl, "study", new[] { "schemaVersion", "raw", "dyr", "initialisation", "integration", "criteria", "angleReference", "channels", "cases" },
                new[] { "execution", "seq", "adaptive", "devices", "iec61400" });
            if (Int(rootEl.GetProperty("schemaVersion"), "schemaVersion") != 1)
                throw new InvalidDataException("schemaVersion must be 1.");
            if (rootEl.TryGetProperty("seq", out _))
                throw new InvalidDataException("seq: a SEQ attachment is not supported by dynamics-study v1 (balanced RMS study only); remove it.");
            string baseDir = Path.GetDirectoryName(input)!;
            (string rawPath, string rawHash) = FileRef(rootEl.GetProperty("raw"), "raw", baseDir, ".raw");
            (string dyrPath, string dyrHash) = FileRef(rootEl.GetProperty("dyr"), "dyr", baseDir, ".dyr");

            byte[] rawBytes = GridSim.Core.Security.SideFile.ReadAllBytes(rawPath, GridSim.Core.Security.SideFile.CaseMaxBytes, "raw");
            byte[] dyrBytes = GridSim.Core.Security.SideFile.ReadAllBytes(dyrPath, GridSim.Core.Security.SideFile.CaseMaxBytes, "dyr");
            RequireIdentity("raw", rawHash, Hash(rawBytes));
            RequireIdentity("dyr", dyrHash, Hash(dyrBytes));

            // Parse every remaining part before any load.
            JsonElement init = rootEl.GetProperty("initialisation");
            Obj(init, "initialisation", new[] { "powerFlow", "unmodelled" }, Array.Empty<string>());
            JsonElement pfEl = init.GetProperty("powerFlow");
            Obj(pfEl, "initialisation.powerFlow", new[] { "tolerancePu", "maxIterations" }, Array.Empty<string>());
            DynamicsUnmodelledPolicy policy = Str(init.GetProperty("unmodelled"), "initialisation.unmodelled") switch
            {
                "refuse" => DynamicsUnmodelledPolicy.Refuse,
                "leaveOut" => DynamicsUnmodelledPolicy.LeaveOut,
                string s => throw new InvalidDataException($"initialisation.unmodelled must be \"refuse\" or \"leaveOut\" (got \"{s}\")."),
            };
            var initialisation = new DynamicsInitialisation(Num(pfEl.GetProperty("tolerancePu"), "initialisation.powerFlow.tolerancePu"),
                Int(pfEl.GetProperty("maxIterations"), "initialisation.powerFlow.maxIterations"), policy);
            JsonElement integ = rootEl.GetProperty("integration");
            Obj(integ, "integration", new[] { "stepSeconds", "durationSeconds" }, new[] { "stepSchedule", "errorControl" });
            double step = Num(integ.GetProperty("stepSeconds"), "integration.stepSeconds");
            double duration = Num(integ.GetProperty("durationSeconds"), "integration.durationSeconds");
            // RC5 WP21: the opt-in step schedule or error-controlled step for the events rows (absent: the incumbent study).
            GridSim.Core.Dynamics.Models.RmsStepSchedule? stepSchedule = integ.TryGetProperty("stepSchedule", out JsonElement schedEl)
                ? StepSchedule(schedEl) : null;
            GridSim.Core.Dynamics.Models.RmsErrorControlOptions? errorControl = integ.TryGetProperty("errorControl", out JsonElement ecEl)
                ? ErrorControl(ecEl) : null;
            JsonElement crit = rootEl.GetProperty("criteria");
            Obj(crit, "criteria", new[] { "slipLimitRad", "maxAngleSpreadDeg" }, Array.Empty<string>());
            var criteria = new DynamicsCriteria(Num(crit.GetProperty("slipLimitRad"), "criteria.slipLimitRad"),
                Num(crit.GetProperty("maxAngleSpreadDeg"), "criteria.maxAngleSpreadDeg"));
            DynamicsMachineId reference = Machine(rootEl.GetProperty("angleReference"), "angleReference");
            var channels = Arr(rootEl.GetProperty("channels"), "channels").Select((c, i) => Channel(c, $"channels[{i}]")).ToList();
            var cases = Arr(rootEl.GetProperty("cases"), "cases").Select((c, i) => Case(c, $"cases[{i}]")).ToList();
            int parallelism = 1;
            if (rootEl.TryGetProperty("execution", out JsonElement exec))
            {
                Obj(exec, "execution", new[] { "maxParallelism" }, Array.Empty<string>());
                parallelism = Int(exec.GetProperty("maxParallelism"), "execution.maxParallelism");
            }

            // WP6 (RC5): the optional network device models (absent: the study is exactly the incumbent's).
            List<GridSim.Core.Dynamics.Models.RmsNetworkDeviceData>? devices = rootEl.TryGetProperty("devices", out JsonElement devEl)
                ? Arr(devEl, "devices").Select((d, i) => Device(d, $"devices[{i}]")).ToList() : null;
            // RC5 WP23: the optional IEC 61400 wind turbine units (absent: the study is exactly the incumbent's).
            List<Iec61400StudyUnit>? iecUnits = rootEl.TryGetProperty("iec61400", out JsonElement iecEl)
                ? Arr(iecEl, "iec61400").Select((u, i) => Iec61400StudyFile.Unit(u, $"iec61400[{i}]")).ToList() : null;
            // RC4 R4-14: the optional variable-step companion run (absent: the study is exactly the incumbent's).
            (string Path, string? Sha256)? unitFile = null;
            DynamicsAdaptiveRequest? adaptive = rootEl.TryGetProperty("adaptive", out JsonElement adaptiveEl) ? Adaptive(adaptiveEl, baseDir, out unitFile) : null;
            // RC5: the units' own relays (opt-in): adaptive.unitProtection in the study file, or --unit-protection.
            string unitSource = "adaptive.unitProtection";
            if (unitArgument is not null)
            {
                if (unitFile is not null)
                    throw new InvalidDataException("--unit-protection: the study file already names a unit protection file (adaptive.unitProtection); give it once.");
                unitFile = (Path.GetFullPath(unitArgument), null);
                unitSource = "--unit-protection";
            }
            byte[]? unitBytes = null;
            if (unitFile is { } uf)
            {
                if (!File.Exists(uf.Path)) throw new InvalidDataException($"{unitSource}: file '{uf.Path}' does not exist.");
                unitBytes = GridSim.Core.Security.SideFile.ReadAllBytes(uf.Path, GridSim.Core.Security.SideFile.CaseMaxBytes, unitSource);
                if (uf.Sha256 is string expected) RequireIdentity(unitSource, expected, Hash(unitBytes));
            }

            // The incumbent loader (licence, notes), then the RAW parse with the same (default) options for the native keys.
            GridModel loaded = load(rawPath);
            PsseParseResult raw = PsseRawParser.ParseWithDiagnostics(Decode(rawBytes), Path.GetFileNameWithoutExtension(rawPath), new PsseReadOptions());
            if (raw.Model.AllGenerators.Count != loaded.AllGenerators.Count || raw.Model.Buses.Count != loaded.Buses.Count
                || raw.Model.Branches.Count != loaded.Branches.Count)
                throw new InvalidDataException("The RAW case read for its native identities is not the case the loader gave.");
            if (Hash(File.ReadAllBytes(rawPath)) != Hash(rawBytes) || Hash(File.ReadAllBytes(dyrPath)) != Hash(dyrBytes))
                throw new IOException("Case inputs changed during loading.");
            string schemas = Gated.FindV2Schemas(root) ?? throw new InvalidDataException("dynamics-study needs the v2 schemas (schemas/v2 beside the program or above it), and none was found.");
            GridSim.Core.Protection.UnitProtectionSet? unitSet = null;
            if (unitBytes is not null)
            {
                unitSet = GridSim.Core.IO.Json.UnitProtectionFile.Parse(unitBytes, Path.GetFileName(unitFile!.Value.Path), schemas);
                // With the adaptive member the units act in both companion runs; without it, in the full-model run only.
                if (adaptive is not null) adaptive = adaptive with { UnitProtection = unitSet };
            }

            // RC5 WP12: the model assignments sidecar and the plug-in configuration (both opt-in; absent, the request is the incumbent's).
            GridSim.Core.Dynamics.Plugins.PluginSet? pluginSet = null;
            IReadOnlyList<UserModelAssignment>? userModels = null;
            IReadOnlyList<PluginModelAssignment>? pluginModels = null;
            IReadOnlyList<DeviceModelAssignment>? deviceModels = null;   // RC5 WP32: injection, machine and load models
            IReadOnlyList<RelayModelAssignment>? relayModels = null;     // RC5 wp32b: user relays (unit or branch)
            byte[]? assignmentBytes = null;
            if (assignmentsArgument is not null)
            {
                string assignmentsPath = Path.GetFullPath(assignmentsArgument);
                if (!File.Exists(assignmentsPath)) throw new InvalidDataException($"--model-assignments: file '{assignmentsArgument}' does not exist.");
                assignmentBytes = File.ReadAllBytes(assignmentsPath);
                if (pluginsArgument is not null) pluginSet = GridSim.Core.Dynamics.Plugins.PluginLoader.Load(pluginsArgument);
                (userModels, pluginModels) = ModelAssignmentsFile.Parse(assignmentBytes, assignmentsPath, pluginSet, step);
                (deviceModels, relayModels) = ModelAssignmentsFile.ParseModels(assignmentBytes, assignmentsPath, pluginSet, step);
            }

            var request = new DynamicsStudyRequest(raw, Decode(dyrBytes), Path.GetFileName(dyrPath), schemas, initialisation, step, duration,
                criteria, reference, channels, cases, parallelism, UserModels: userModels is { Count: > 0 } ? userModels : null, Adaptive: adaptive)
                {
                    UnitProtection = adaptive is null ? unitSet : null, Devices = devices, PluginModels = pluginModels is { Count: > 0 } ? pluginModels : null,
                    StepSchedule = stepSchedule, ErrorControl = errorControl, Iec61400Units = iecUnits,
                    DeviceModels = deviceModels is { Count: > 0 } ? deviceModels : null,
                    RelayModels = relayModels is { Count: > 0 } ? relayModels : null,
                };
            DynamicsStudyResult result = DynamicsStudyRunner.Run(request);
            object resultDocument = new
            {
                schemaVersion = 1,
                provenance = new
                {
                    studySha256 = Hash(studyBytes),
                    inputs = new[] { new InputFile(Path.GetFileName(rawPath), Hash(rawBytes)), new InputFile(Path.GetFileName(dyrPath), Hash(dyrBytes)) },
                    rawRevision = raw.Revision,
                    engine = GridSim.Core.BuildInfo.InformationalVersion,
                },
                effectiveOptions = new
                {
                    initialisation, integration = new { stepSeconds = step, durationSeconds = duration }, criteria,
                    angleReference = reference, nominalHz = raw.Model.FrequencyHz, maxParallelism = parallelism,
                },
                coverage = result.Coverage,
                engine = result.Engine,
                initialisation = result.Initialisation,
                cases = result.Cases,
                counts = result.Counts,
                status = result.Status,
                likeForLike = result.LikeForLike,
                reasons = result.Reasons,
            };
            string json = JsonSerializer.Serialize(resultDocument, OutputJson);
            if (result.Adaptive is not null)
            {
                // R4-14: the companion run is appended; a request without it is serialised exactly as before.
                System.Text.Json.Nodes.JsonNode node = JsonSerializer.SerializeToNode(resultDocument, OutputJson)!;
                node["effectiveOptions"]!["adaptive"] = JsonSerializer.SerializeToNode(new { adaptive!.Options, adaptive.CctToleranceSeconds, devices = adaptive.Devices?.Devices.Count, curves = adaptive.Curves is null ? "built-in families" : "directory" }, OutputJson);
                node["adaptive"] = JsonSerializer.SerializeToNode(result.Adaptive, OutputJson);
                if (unitSet is not null)
                {
                    // RC5: appended only when unit protection was asked for; every other result is serialised as before.
                    node["effectiveOptions"]!["adaptive"]!["unitProtection"] = JsonSerializer.SerializeToNode(new { source = unitSource, units = unitSet.Units.Count }, OutputJson);
                    node["unitProtection"] = JsonSerializer.SerializeToNode(UnitSummary(unitSet, unitSource, Path.GetFileName(unitFile!.Value.Path), Hash(unitBytes!), result.Adaptive), OutputJson);
                    // RC5 stage 6: the same events on the full-model engine with the units' relays acting (incl. 40), appended.
                    if (result.FullModelUnitRelays is not null)
                        node["unitProtection"]!["fullModel"] = JsonSerializer.SerializeToNode(result.FullModelUnitRelays, OutputJson);
                }
                json = node.ToJsonString(OutputJson);
            }
            else if (unitSet is not null)
            {
                // RC5: unit protection without the adaptive member: the full-model companion run only, appended.
                System.Text.Json.Nodes.JsonNode node = JsonSerializer.SerializeToNode(resultDocument, OutputJson)!;
                node["effectiveOptions"]!["unitProtection"] = JsonSerializer.SerializeToNode(new { source = unitSource, units = unitSet.Units.Count }, OutputJson);
                node["unitProtection"] = JsonSerializer.SerializeToNode(FullModelUnitSummary(unitSet, unitSource, Path.GetFileName(unitFile!.Value.Path), Hash(unitBytes!)), OutputJson);
                node["unitProtection"]!["fullModel"] = JsonSerializer.SerializeToNode(result.FullModelUnitRelays!, OutputJson);
                json = node.ToJsonString(OutputJson);
            }
            if (relayModels is { Count: > 0 } && unitSet is null && result.FullModelUnitRelays is not null)
            {
                // RC5 wp32b: user relays without unit protection: their full-model companion run, appended (with unit protection it
                // is the unitProtection.fullModel member above). Every other result is serialised as before.
                System.Text.Json.Nodes.JsonNode node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
                node["userRelays"] = JsonSerializer.SerializeToNode(new { fullModel = result.FullModelUnitRelays }, OutputJson);
                json = node.ToJsonString(OutputJson);
            }
            if (stepSchedule is not null || errorControl is not null)
            {
                // RC5 WP21: appended only with a step schedule or the error-controlled step; every other result is serialised as before.
                System.Text.Json.Nodes.JsonNode node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
                if (stepSchedule is not null)
                    node["effectiveOptions"]!["integration"]!["stepSchedule"] = JsonSerializer.SerializeToNode(
                        stepSchedule.Windows.Select(w => new { fromS = w.FromSeconds, dtS = w.StepSeconds }).ToList(), OutputJson);
                if (errorControl is not null)
                {
                    node["effectiveOptions"]!["integration"]!["errorControl"] = JsonSerializer.SerializeToNode(errorControl, OutputJson);
                    // 2.1.126: the error-controlled step is EXPERIMENTAL; the result says so where it records the option.
                    node["effectiveOptions"]!["integration"]!["errorControlStatus"] = GridSim.Core.Dynamics.Models.RmsErrorControl.ExperimentalNote;
                }
                json = node.ToJsonString(OutputJson);
            }
            if (iecUnits is { Count: > 0 })
            {
                // RC5 WP23: appended only with IEC 61400 units; every other result is serialised as before.
                System.Text.Json.Nodes.JsonNode node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
                node["effectiveOptions"]!["iec61400"] = JsonSerializer.SerializeToNode(iecUnits.Select(u => new
                {
                    machine = u.Machine, model = u.Data.Description,
                    source = GridSim.Core.Dynamics.Models.Iec61400.Iec61400Data.SourceNote,
                }).ToList(), OutputJson);
                json = node.ToJsonString(OutputJson);
            }
            if (assignmentBytes is not null)
            {
                // RC5 WP12: appended only when user models were assigned; every other result is serialised as before.
                System.Text.Json.Nodes.JsonNode node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
                node["provenance"]!["modelAssignments"] = JsonSerializer.SerializeToNode(new
                {
                    file = Path.GetFileName(assignmentsArgument!),
                    sha256 = Hash(assignmentBytes),
                    userModels = (userModels ?? Array.Empty<UserModelAssignment>()).Select(a => new { machine = a.Machine, model = a.Model.Id, sha256 = a.Model.Hash }).ToList(),
                }, OutputJson);
                if (pluginModels is { Count: > 0 })
                    node["provenance"]!["plugins"] = JsonSerializer.SerializeToNode(new
                    {
                        configSha256 = pluginSet!.Config.ConfigSha256,
                        contractVersion = GridSim.Core.Dynamics.Plugins.PluginContract.Version,
                        label = GridSim.Core.Dynamics.Plugins.PluginContract.OutsideLabel,
                        models = pluginModels.Select(a => new
                        {
                            machine = a.Machine, model = a.Model.Id, role = a.Model.Definition.RoleText, assembly = a.Model.Definition.AssemblyName,
                            sha256 = a.Model.Hash, contractVersion = a.Model.Definition.ContractVersion,
                            parameters = a.Model.Definition.Parameters.Select((p, k) => new { name = p.Name, value = a.Model.Values[k] }).ToList(),
                        }).ToList(),
                    }, OutputJson);
                // RC5 WP32: appended only when the file gives injection, machine or load models; every other result is as before.
                if (deviceModels is { Count: > 0 })
                    node["provenance"]!["deviceModels"] = JsonSerializer.SerializeToNode(deviceModels.Select(a => new
                    {
                        role = a.Device.Role, bind = a.Where, model = a.Device.Id, sha256 = a.Device.Hash,
                        source = a.Device.IsPlugin ? "plugin" : "declarative",
                        assembly = a.Device.Plugin?.Definition.AssemblyName,
                        contractVersion = a.Device.Plugin?.Definition.ContractVersion,
                        configSha256 = a.Device.IsPlugin ? pluginSet!.Config.ConfigSha256 : null,
                        parameters = a.Device.Plugin is { } dp ? dp.Definition.Parameters.Select((p, k) => new { name = p.Name, value = dp.Values[k] }).ToList() : null,
                        label = a.Device.IsPlugin ? GridSim.Core.Dynamics.Plugins.PluginContract.OutsideLabel : null,
                    }).ToList(), OutputJson);
                // RC5 wp32b: appended only when the file gives relays; every other result is as before.
                if (relayModels is { Count: > 0 })
                    node["provenance"]!["relayModels"] = JsonSerializer.SerializeToNode(relayModels.Select(a => new
                    {
                        role = "relay", bind = a.Where, model = a.Id, sha256 = a.Hash,
                        source = a.IsPlugin ? "plugin" : "declarative",
                        assembly = a.Plugin?.Definition.AssemblyName,
                        contractVersion = a.Plugin?.Definition.ContractVersion,
                        configSha256 = a.IsPlugin ? pluginSet!.Config.ConfigSha256 : null,
                        parameters = a.Plugin is { } rp ? rp.Definition.Parameters.Select((p, k) => new { name = p.Name, value = rp.Values[k] }).ToList() : null,
                        label = a.IsPlugin ? GridSim.Core.Dynamics.Plugins.PluginContract.OutsideLabel : null,
                    }).ToList(), OutputJson);
                json = node.ToJsonString(OutputJson);
            }
            if (output is not null)
            {
                temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, json + "\n", new UTF8Encoding(false));
                File.Move(temporary, output, overwrite: false);
                temporary = null;
            }
            Console.WriteLine(json);
            return result.Status is DynamicsDisposition.Stable or DynamicsDisposition.Evaluated ? CliExit.Ok : CliExit.Verdict;
        }
        catch (GateRefusedException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            Console.Error.WriteLine("dynamics-study: " + CliExit.UserMessage(ex));
            return CliExit.CaseLoadFailure(ex);
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Strict JSON reading: every object's fields are checked against its required and optional names.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>RC5 WP21: integration.stepSchedule, an array of { "fromS", "dtS" } windows (checked by the schedule itself).</summary>
    private static GridSim.Core.Dynamics.Models.RmsStepSchedule StepSchedule(JsonElement e)
    {
        var windows = new List<GridSim.Core.Dynamics.Models.RmsStepWindow>();
        int i = 0;
        foreach (JsonElement w in Arr(e, "integration.stepSchedule"))
        {
            string what = $"integration.stepSchedule[{i++}]";
            Obj(w, what, new[] { "fromS", "dtS" }, Array.Empty<string>());
            windows.Add(new GridSim.Core.Dynamics.Models.RmsStepWindow(Num(w.GetProperty("fromS"), what + ".fromS"), Num(w.GetProperty("dtS"), what + ".dtS")));
        }
        try { return new GridSim.Core.Dynamics.Models.RmsStepSchedule(windows); }
        catch (ArgumentException ex) { throw new InvalidDataException("integration.stepSchedule: " + ex.Message); }
    }

    /// <summary>RC5 WP21: integration.errorControl; every member optional, the defaults of RmsErrorControlOptions otherwise.</summary>
    private static GridSim.Core.Dynamics.Models.RmsErrorControlOptions ErrorControl(JsonElement e)
    {
        const string what = "integration.errorControl";
        Obj(e, what, Array.Empty<string>(), new[] { "relativeTolerance", "absoluteTolerance", "maxStepSeconds", "minStepSeconds", "segmentSeconds", "maxPasses", "safetyFactor" });
        var d = new GridSim.Core.Dynamics.Models.RmsErrorControlOptions();
        double N(string name, double dflt) => e.TryGetProperty(name, out JsonElement v) ? Num(v, what + "." + name) : dflt;
        var o = new GridSim.Core.Dynamics.Models.RmsErrorControlOptions(N("relativeTolerance", d.RelativeTolerance), N("absoluteTolerance", d.AbsoluteTolerance),
            N("maxStepSeconds", d.MaxStepSeconds), N("minStepSeconds", d.MinStepSeconds), N("segmentSeconds", d.SegmentSeconds),
            e.TryGetProperty("maxPasses", out JsonElement mp) ? Int(mp, what + ".maxPasses") : d.MaxPasses, N("safetyFactor", d.SafetyFactor));
        try { o.Validate(); }
        catch (ArgumentException ex) { throw new InvalidDataException(what + ": " + ex.Message); }
        return o;
    }

    private static void Obj(JsonElement e, string what, string[] required, string[] optional)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{what} must be an object.");
        foreach (JsonProperty p in e.EnumerateObject())
            if (!required.Contains(p.Name) && !optional.Contains(p.Name))
                throw new InvalidDataException($"{what}: unknown field '{p.Name}'.");
        foreach (string r in required)
            if (!e.TryGetProperty(r, out _)) throw new InvalidDataException($"{what}: missing required field '{r}'.");
    }

    /// <summary>
    /// WP6 (RC5): one network device model: <c>{"kind": "svc" | "statcom" | "lcc" | "vsc", "name": "...", ...}</c> with the
    /// model's parameters by name (CSVGN1: k t1 t2 t3 t4 t5 rminMvar vmax vmin cbaseMvar mbaseMva; STATCOM1: mbaseMva xs kp ki
    /// tc imaxCap imaxInd; LCC1: alphaMinDeg gammaMinDeg tvdc tidc vblockPu vunblockPu tblockSeconds vrampPuPerSecond
    /// crampPuPerSecond c0Amps v1Kv c1Amps v2Kv c2Amps v3Kv c3Amps, optional tp; VSC1: tp tq imaxPu mbaseMva, optional
    /// priority "reactive" | "active", firstKv, secondKv, dcFollowsLimitedPower). WP16: "mtdc" names a multi-terminal DC link
    /// (MTDC1): optional dcBaseKv and busCapacitances [{dcBus, microFarads}], and terminals, one per converter: bus, mode
    /// "power" | "dcVoltage" | "droop", imaxPu, mbaseMva, optional tp, tq, kv, droop, capacitanceMicroFarads, priority and
    /// mmc {submodules, csmFarads, larmHenries, optional kpW, kiW, tw}.
    /// </summary>
    private static GridSim.Core.Dynamics.Models.RmsNetworkDeviceData Device(JsonElement e, string what)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("kind", out JsonElement k)) throw new InvalidDataException($"{what} needs a kind.");
        string kind = Str(k, what + ".kind");
        double N(string name) => Num(e.GetProperty(name), what + "." + name);
        string[] Req(params string[] p) => new[] { "kind", "name" }.Concat(p).ToArray();
        switch (kind)
        {
            case "svc":
            {
                string[] p = { "k", "t1", "t2", "t3", "t4", "t5", "rminMvar", "vmax", "vmin", "cbaseMvar", "mbaseMva" };
                Obj(e, what, Req(p), Array.Empty<string>());
                return new GridSim.Core.Dynamics.Models.SvcDeviceData(Str(e.GetProperty("name"), what + ".name"),
                    new GridSim.Core.Dynamics.Models.Csvgn1Data(N("k"), N("t1"), N("t2"), N("t3"), N("t4"), N("t5"), N("rminMvar"), N("vmax"), N("vmin"),
                        N("cbaseMvar"), N("mbaseMva")));
            }
            case "statcom":
            {
                string[] p = { "mbaseMva", "xs", "kp", "ki", "tc", "imaxCap", "imaxInd" };
                Obj(e, what, Req(p), Array.Empty<string>());
                return new GridSim.Core.Dynamics.Models.StatcomDeviceData(Str(e.GetProperty("name"), what + ".name"),
                    new GridSim.Core.Dynamics.Models.Statcom1Data(N("mbaseMva"), N("xs"), N("kp"), N("ki"), N("tc"), N("imaxCap"), N("imaxInd")));
            }
            case "lcc":
            {
                string[] p = { "alphaMinDeg", "gammaMinDeg", "tvdc", "tidc", "vblockPu", "vunblockPu", "tblockSeconds", "vrampPuPerSecond",
                    "crampPuPerSecond", "c0Amps", "v1Kv", "c1Amps", "v2Kv", "c2Amps", "v3Kv", "c3Amps" };
                Obj(e, what, Req(p), new[] { "tp" });
                var data = new GridSim.Core.Dynamics.Models.Lcc1Data(N("alphaMinDeg"), N("gammaMinDeg"), N("tvdc"), N("tidc"), N("vblockPu"),
                    N("vunblockPu"), N("tblockSeconds"), N("vrampPuPerSecond"), N("crampPuPerSecond"), N("c0Amps"), N("v1Kv"), N("c1Amps"),
                    N("v2Kv"), N("c2Amps"), N("v3Kv"), N("c3Amps"))
                    { Tp = e.TryGetProperty("tp", out _) ? N("tp") : 0.0 };
                return new GridSim.Core.Dynamics.Models.LccLinkDynamicsData(Str(e.GetProperty("name"), what + ".name"), data);
            }
            case "vsc":
            {
                Obj(e, what, Req("tp", "tq", "imaxPu", "mbaseMva"), new[] { "priority", "firstKv", "secondKv", "dcFollowsLimitedPower" });
                GridSim.Core.Dynamics.Models.VscCurrentPriority priority = !e.TryGetProperty("priority", out JsonElement pe)
                    ? GridSim.Core.Dynamics.Models.VscCurrentPriority.Reactive
                    : Str(pe, what + ".priority") switch
                    {
                        "reactive" => GridSim.Core.Dynamics.Models.VscCurrentPriority.Reactive,
                        "active" => GridSim.Core.Dynamics.Models.VscCurrentPriority.Active,
                        string s => throw new InvalidDataException($"{what}.priority must be \"reactive\" or \"active\" (got \"{s}\")."),
                    };
                var data = new GridSim.Core.Dynamics.Models.Vsc1Data(N("tp"), N("tq"), N("imaxPu"), N("mbaseMva"))
                {
                    Priority = priority,
                    FirstKv = e.TryGetProperty("firstKv", out _) ? N("firstKv") : 0.0,
                    SecondKv = e.TryGetProperty("secondKv", out _) ? N("secondKv") : 0.0,
                    DcFollowsLimitedPower = e.TryGetProperty("dcFollowsLimitedPower", out JsonElement fe) && Flag(fe, what + ".dcFollowsLimitedPower"),
                };
                return new GridSim.Core.Dynamics.Models.VscLinkDynamicsData(Str(e.GetProperty("name"), what + ".name"), data);
            }
            case "mtdc":
            {
                Obj(e, what, Req("terminals"), new[] { "dcBaseKv", "busCapacitances" });
                var controls = new List<GridSim.Core.Dynamics.Models.MtdcTerminalControl>();
                List<JsonElement> ts = Arr(e.GetProperty("terminals"), what + ".terminals");
                for (int i = 0; i < ts.Count; i++) controls.Add(MtdcTerminal(ts[i], $"{what}.terminals[{i}]"));
                var caps = new List<GridSim.Core.Dynamics.Models.MtdcDcBusCapacitance>();
                if (e.TryGetProperty("busCapacitances", out JsonElement ce))
                {
                    List<JsonElement> cs = Arr(ce, what + ".busCapacitances");
                    for (int i = 0; i < cs.Count; i++)
                    {
                        string w = $"{what}.busCapacitances[{i}]";
                        Obj(cs[i], w, new[] { "dcBus", "microFarads" }, Array.Empty<string>());
                        caps.Add(new GridSim.Core.Dynamics.Models.MtdcDcBusCapacitance(Int(cs[i].GetProperty("dcBus"), w + ".dcBus"), Num(cs[i].GetProperty("microFarads"), w + ".microFarads")));
                    }
                }
                return new GridSim.Core.Dynamics.Models.MtdcGridDynamicsData(Str(e.GetProperty("name"), what + ".name"), controls)
                {
                    DcBaseKv = e.TryGetProperty("dcBaseKv", out _) ? N("dcBaseKv") : 0.0,
                    BusCapacitances = caps,
                };
            }
            default:
                throw new InvalidDataException($"{what}.kind must be \"svc\", \"statcom\", \"lcc\", \"vsc\" or \"mtdc\" (got \"{kind}\").");
        }
    }

    /// <summary>WP16: one MTDC1 converter control.</summary>
    private static GridSim.Core.Dynamics.Models.MtdcTerminalControl MtdcTerminal(JsonElement e, string what)
    {
        Obj(e, what, new[] { "bus", "mode", "imaxPu", "mbaseMva" }, new[] { "tp", "tq", "kv", "droop", "capacitanceMicroFarads", "priority", "mmc" });
        double N(string name) => Num(e.GetProperty(name), what + "." + name);
        double O(string name) => e.TryGetProperty(name, out JsonElement v) ? Num(v, what + "." + name) : 0.0;
        GridSim.Core.Dynamics.Models.MtdcControlMode mode = Str(e.GetProperty("mode"), what + ".mode") switch
        {
            "power" => GridSim.Core.Dynamics.Models.MtdcControlMode.Power,
            "dcVoltage" => GridSim.Core.Dynamics.Models.MtdcControlMode.DcVoltage,
            "droop" => GridSim.Core.Dynamics.Models.MtdcControlMode.Droop,
            string s => throw new InvalidDataException($"{what}.mode must be \"power\", \"dcVoltage\" or \"droop\" (got \"{s}\")."),
        };
        GridSim.Core.Dynamics.Models.VscCurrentPriority priority = !e.TryGetProperty("priority", out JsonElement pe)
            ? GridSim.Core.Dynamics.Models.VscCurrentPriority.Reactive
            : Str(pe, what + ".priority") switch
            {
                "reactive" => GridSim.Core.Dynamics.Models.VscCurrentPriority.Reactive,
                "active" => GridSim.Core.Dynamics.Models.VscCurrentPriority.Active,
                string s => throw new InvalidDataException($"{what}.priority must be \"reactive\" or \"active\" (got \"{s}\")."),
            };
        GridSim.Core.Dynamics.Models.Mmc1Data? mmc = null;
        if (e.TryGetProperty("mmc", out JsonElement me))
        {
            string w = what + ".mmc";
            Obj(me, w, new[] { "submodules", "csmFarads", "larmHenries" }, new[] { "kpW", "kiW", "tw" });
            double M(string name) => me.TryGetProperty(name, out JsonElement v) ? Num(v, w + "." + name) : 0.0;
            mmc = new GridSim.Core.Dynamics.Models.Mmc1Data(Int(me.GetProperty("submodules"), w + ".submodules"), M("csmFarads"), M("larmHenries"), M("kpW"), M("kiW"), M("tw"));
        }
        return new GridSim.Core.Dynamics.Models.MtdcTerminalControl(Int(e.GetProperty("bus"), what + ".bus"), mode, N("imaxPu"), N("mbaseMva"))
        {
            Tp = O("tp"), Tq = O("tq"), Kv = O("kv"), DroopGain = O("droop"), CapacitanceMicroFarads = O("capacitanceMicroFarads"),
            Priority = priority, Mmc = mmc,
        };
    }

    private static bool Flag(JsonElement e, string what) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new InvalidDataException($"{what} must be true or false."),
    };

    private static double Num(JsonElement e, string what)
    {
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetDouble(out double v) || !double.IsFinite(v))
            throw new InvalidDataException($"{what} must be a finite number.");
        return v;
    }

    private static int Int(JsonElement e, string what)
    {
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out int v)) throw new InvalidDataException($"{what} must be a whole number.");
        return v;
    }

    private static string Str(JsonElement e, string what)
    {
        if (e.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{what} must be a string.");
        return e.GetString()!;
    }

    private static List<JsonElement> Arr(JsonElement e, string what)
    {
        if (e.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{what} must be an array.");
        return e.EnumerateArray().ToList();
    }

    private static (string Path, string Sha256) FileRef(JsonElement e, string what, string baseDir, string extension)
    {
        Obj(e, what, new[] { "path", "sha256" }, Array.Empty<string>());
        string rel = Str(e.GetProperty("path"), what + ".path");
        string sha = Str(e.GetProperty("sha256"), what + ".sha256");
        if (string.IsNullOrWhiteSpace(rel) || string.IsNullOrWhiteSpace(sha)) throw new InvalidDataException($"{what} needs a path and a sha256.");
        // R1-02: a file the study names is a side file: no network or device path, resolved against the study's folder.
        string full = GridSim.Core.Security.SideFile.Resolve(rel, baseDir, what + ".path");
        if (!full.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{what}.path must name a PSS/E {extension} file.");
        return (full, sha);
    }

    private static DynamicsAdaptiveRequest Adaptive(JsonElement e, string baseDir, out (string Path, string? Sha256)? unitFile)
    {
        const string what = "adaptive";
        Obj(e, what, Array.Empty<string>(), new[] { "relativeTolerance", "absoluteTolerance", "initialStepSeconds", "minStepSeconds", "maxStepSeconds",
            "eventToleranceSeconds", "cctToleranceSeconds", "devices", "curves", "unitProtection" });
        unitFile = null;
        if (e.TryGetProperty("unitProtection", out JsonElement up))
        {
            (string upPath, string upHash) = FileRef(up, what + ".unitProtection", baseDir, ".json");
            unitFile = (upPath, upHash);
        }
        var d = new GridSim.Core.Dynamics.Models.Adaptive.AdaptiveOptions();
        double Opt(string name, double fallback) => e.TryGetProperty(name, out JsonElement v) ? Num(v, what + "." + name) : fallback;
        var options = new GridSim.Core.Dynamics.Models.Adaptive.AdaptiveOptions(Opt("relativeTolerance", d.RelativeTolerance), Opt("absoluteTolerance", d.AbsoluteTolerance),
            Opt("initialStepSeconds", d.InitialStepSeconds), Opt("minStepSeconds", d.MinStepSeconds), Opt("maxStepSeconds", d.MaxStepSeconds),
            Opt("eventToleranceSeconds", d.EventTimeToleranceSeconds));
        try { options.Validate(); }
        catch (ArgumentException ex) { throw new InvalidDataException(what + ": " + ex.Message); }
        double cct = Opt("cctToleranceSeconds", 1e-4);
        GridSim.Core.Protection.Devices.CurveLibrary? curves = null;
        if (e.TryGetProperty("curves", out JsonElement cv))
        {
            // R1-02: the curves folder is a side file too, and no file in it may be a link (it could lead anywhere).
            string dir = GridSim.Core.Security.SideFile.Resolve(Str(cv, what + ".curves"), baseDir, what + ".curves");
            if (!Directory.Exists(dir)) throw new InvalidDataException($"{what}.curves: directory '{dir}' does not exist.");
            foreach (string f in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories))
                if (new FileInfo(f).LinkTarget is not null || new DirectoryInfo(f).LinkTarget is not null)
                    throw new InvalidDataException($"{what}.curves: '{Path.GetRelativePath(dir, f)}' is a link; a curves folder holds plain files only.");
            try { curves = GridSim.Core.Protection.Devices.CurveLibrary.LoadDirectory(dir); }
            catch (GridSim.Core.Analysis.StudyCases.StudyCaseException ex) { throw new InvalidDataException($"{what}.curves: {ex.Message}"); }
        }
        GridSim.Core.Protection.Devices.DeviceSet? devices = null;
        if (e.TryGetProperty("devices", out JsonElement dv))
        {
            (string path, string hash) = FileRef(dv, what + ".devices", baseDir, ".json");
            byte[] bytes = GridSim.Core.Security.SideFile.ReadAllBytes(path, GridSim.Core.Security.SideFile.CaseMaxBytes, what + ".devices");
            RequireIdentity(what + ".devices", hash, Hash(bytes));
            try { devices = GridSim.Core.Protection.Devices.DeviceSet.Parse(bytes); }
            catch (GridSim.Core.Analysis.StudyCases.StudyCaseException ex) { throw new InvalidDataException($"{what}.devices: {ex.Message}"); }
        }
        return new DynamicsAdaptiveRequest(options, cct, devices, curves);
    }

    /// <summary>RC5: the result's unitProtection member: the file's identity, the units it names and every unit relay trip of
    /// the adaptive rows (the full per-stage records stay in adaptive.rows[].unitRelays).</summary>
    // RC5: the same identity members as UnitSummary, without the adaptive trip list (no adaptive run was made).
    private static object FullModelUnitSummary(GridSim.Core.Protection.UnitProtectionSet units, string source, string file, string sha256) => new
    {
        source,
        file,
        sha256,
        schema = GridSim.Core.IO.Json.UnitProtectionFile.SchemaTag,
        unitKey = "a RAW machine is named by its PSS/E key <bus>:<id> (the generator record's bus number and machine ID)",
        units = units.Units.Select(u => u.Unit).ToList(),
        templates = units.Classes.Select(c => c.Name).ToList(),
    };

    private static object UnitSummary(GridSim.Core.Protection.UnitProtectionSet units, string source, string file, string sha256, DynamicsAdaptiveEvidence evidence)
    {
        var trips = new List<object>();
        foreach (DynamicsAdaptiveRow row in evidence.Rows)
            foreach (GridSim.Core.Dynamics.Models.Adaptive.AdaptiveUnitRelayRecord r in row.UnitRelays ?? Array.Empty<GridSim.Core.Dynamics.Models.Adaptive.AdaptiveUnitRelayRecord>())
                if (r.Tripped) trips.Add(new { row = row.Index, @case = row.Name, unit = r.Unit, device = r.Device, tripSeconds = r.TripSeconds, tripQuantity = r.TripQuantity, settings = r.Settings });
        return new
        {
            source,
            file,
            sha256,
            schema = GridSim.Core.IO.Json.UnitProtectionFile.SchemaTag,
            unitKey = "a RAW machine is named by its PSS/E key <bus>:<id> (the generator record's bus number and machine ID)",
            units = units.Units.Select(u => u.Unit).ToList(),
            templates = units.Classes.Select(c => c.Name).ToList(),
            trips,
        };
    }

    private static DynamicsMachineId Machine(JsonElement e, string what)
    {
        Obj(e, what, new[] { "bus", "id" }, Array.Empty<string>());
        return new DynamicsMachineId(Int(e.GetProperty("bus"), what + ".bus"), Str(e.GetProperty("id"), what + ".id"));
    }

    private static DynamicsBranchId Branch(JsonElement e, string what)
    {
        Obj(e, what, new[] { "from", "to", "circuit" }, Array.Empty<string>());
        return new DynamicsBranchId(Int(e.GetProperty("from"), what + ".from"), Int(e.GetProperty("to"), what + ".to"),
            Str(e.GetProperty("circuit"), what + ".circuit"));
    }

    private static DynamicsChannelRequest Channel(JsonElement e, string what)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("kind", out JsonElement k)) throw new InvalidDataException($"{what} needs a kind.");
        string kind = Str(k, what + ".kind");
        switch (kind)
        {
            case "rotorAngle": case "speed": case "frequency":
                Obj(e, what, new[] { "kind", "machine" }, Array.Empty<string>());
                return new DynamicsChannelRequest(kind switch { "rotorAngle" => DynamicsChannelKind.RotorAngle, "speed" => DynamicsChannelKind.Speed, _ => DynamicsChannelKind.Frequency },
                    Machine(e.GetProperty("machine"), what + ".machine"));
            case "busVoltage":
                Obj(e, what, new[] { "kind", "bus" }, Array.Empty<string>());
                return new DynamicsChannelRequest(DynamicsChannelKind.BusVoltage, Bus: Int(e.GetProperty("bus"), what + ".bus"));
            default:
                throw new InvalidDataException($"{what}: unsupported channel kind '{kind}' (rotorAngle, speed, frequency, busVoltage).");
        }
    }

    private static DynamicsCaseRequest Case(JsonElement e, string what)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("kind", out JsonElement k)) throw new InvalidDataException($"{what} needs a kind.");
        string kind = Str(k, what + ".kind");
        if (kind == "events")
        {
            Obj(e, what, new[] { "name", "kind", "events" }, Array.Empty<string>());
            string name = Str(e.GetProperty("name"), what + ".name");
            return new DynamicsCaseRequest(name, DynamicsCaseKind.Events,
                Events: Arr(e.GetProperty("events"), what + ".events").Select((x, i) => Event(x, $"{what}.events[{i}]")).ToList());
        }
        if (kind == "cct")
        {
            Obj(e, what, new[] { "name", "kind", "cct" }, Array.Empty<string>());
            string name = Str(e.GetProperty("name"), what + ".name");
            JsonElement c = e.GetProperty("cct");
            string cw = what + ".cct";
            Obj(c, cw, new[] { "faultBus", "clearingTrips", "onsetSeconds", "maxClearingSeconds" }, new[] { "refinedStepSeconds", "iterations", "scanSteps" });
            return new DynamicsCaseRequest(name, DynamicsCaseKind.CriticalClearing, Cct: new DynamicsCctRequest(
                Int(c.GetProperty("faultBus"), cw + ".faultBus"),
                Arr(c.GetProperty("clearingTrips"), cw + ".clearingTrips").Select((x, i) => Branch(x, $"{cw}.clearingTrips[{i}]")).ToList(),
                Num(c.GetProperty("onsetSeconds"), cw + ".onsetSeconds"),
                Num(c.GetProperty("maxClearingSeconds"), cw + ".maxClearingSeconds"),
                c.TryGetProperty("refinedStepSeconds", out JsonElement rs) ? Num(rs, cw + ".refinedStepSeconds") : null,
                c.TryGetProperty("iterations", out JsonElement it) ? Int(it, cw + ".iterations") : 18,
                c.TryGetProperty("scanSteps", out JsonElement sc) ? Int(sc, cw + ".scanSteps") : 16));
        }
        throw new InvalidDataException($"{what}: unsupported case kind '{kind}' (events, cct).");
    }

    private static DynamicsEventRequest Event(JsonElement e, string what)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("kind", out JsonElement k)) throw new InvalidDataException($"{what} needs a kind.");
        string kind = Str(k, what + ".kind");
        switch (kind)
        {
            case "busFault": case "faultClear":
                Obj(e, what, new[] { "kind", "at", "bus" }, Array.Empty<string>());
                return new DynamicsEventRequest(kind == "busFault" ? DynamicsEventKind.BusFault : DynamicsEventKind.FaultClear,
                    Num(e.GetProperty("at"), what + ".at"), Bus: Int(e.GetProperty("bus"), what + ".bus"));
            case "branchTrip": case "branchReclose":
                Obj(e, what, new[] { "kind", "at", "branch" }, Array.Empty<string>());
                return new DynamicsEventRequest(kind == "branchTrip" ? DynamicsEventKind.BranchTrip : DynamicsEventKind.BranchReclose,
                    Num(e.GetProperty("at"), what + ".at"), Branch: Branch(e.GetProperty("branch"), what + ".branch"));
            case "machineTrip":
                Obj(e, what, new[] { "kind", "at", "machine" }, Array.Empty<string>());
                return new DynamicsEventRequest(DynamicsEventKind.MachineTrip, Num(e.GetProperty("at"), what + ".at"),
                    Machine: Machine(e.GetProperty("machine"), what + ".machine"));
            case "loadStep":
                Obj(e, what, new[] { "kind", "at", "bus", "pMw", "qMvar" }, Array.Empty<string>());
                return new DynamicsEventRequest(DynamicsEventKind.LoadStep, Num(e.GetProperty("at"), what + ".at"),
                    Bus: Int(e.GetProperty("bus"), what + ".bus"), PMw: Num(e.GetProperty("pMw"), what + ".pMw"),
                    QMvar: Num(e.GetProperty("qMvar"), what + ".qMvar"));
            case "sequenceFault":
                Obj(e, what, new[] { "kind", "at", "bus", "type" }, new[] { "rPu", "xPu" });
                return new DynamicsEventRequest(DynamicsEventKind.SequenceFault, Num(e.GetProperty("at"), what + ".at"),
                    Bus: Int(e.GetProperty("bus"), what + ".bus"))
                {
                    FaultType = FaultTypeOf(e.GetProperty("type"), what + ".type"),
                    FaultRPu = e.TryGetProperty("rPu", out JsonElement sr) ? Num(sr, what + ".rPu") : null,
                    FaultXPu = e.TryGetProperty("xPu", out JsonElement sx) ? Num(sx, what + ".xPu") : null,
                };
            case "lineFault":
                Obj(e, what, new[] { "kind", "at", "branch", "fraction", "type" }, new[] { "rPu", "xPu" });
                return new DynamicsEventRequest(DynamicsEventKind.LineFault, Num(e.GetProperty("at"), what + ".at"),
                    Branch: Branch(e.GetProperty("branch"), what + ".branch"))
                {
                    FaultType = FaultTypeOf(e.GetProperty("type"), what + ".type"),
                    Fraction = Num(e.GetProperty("fraction"), what + ".fraction"),
                    FaultRPu = e.TryGetProperty("rPu", out JsonElement lr) ? Num(lr, what + ".rPu") : null,
                    FaultXPu = e.TryGetProperty("xPu", out JsonElement lx) ? Num(lx, what + ".xPu") : null,
                };
            case "lineFaultClear":
                Obj(e, what, new[] { "kind", "at", "branch" }, Array.Empty<string>());
                return new DynamicsEventRequest(DynamicsEventKind.LineFaultClear, Num(e.GetProperty("at"), what + ".at"),
                    Branch: Branch(e.GetProperty("branch"), what + ".branch"));
            default:
                throw new InvalidDataException($"{what}: unsupported event kind '{kind}' (busFault, faultClear, branchTrip, branchReclose, machineTrip, loadStep, sequenceFault, lineFault, lineFaultClear).");
        }
    }

    /// <summary>WP2: a fault type word of a sequenceFault or lineFault event: threePhase, slg, ll or llg.</summary>
    private static GridSim.Core.Protection.FaultType FaultTypeOf(JsonElement e, string what) => Str(e, what) switch
    {
        "threePhase" => GridSim.Core.Protection.FaultType.ThreePhase,
        "slg" => GridSim.Core.Protection.FaultType.SingleLineToGround,
        "ll" => GridSim.Core.Protection.FaultType.LineToLine,
        "llg" => GridSim.Core.Protection.FaultType.DoubleLineToGround,
        string other => throw new InvalidDataException($"{what}: unsupported fault type '{other}' (threePhase, slg, ll, llg)."),
    };

    private static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate property: " + property.Name);
                CheckDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement child in element.EnumerateArray()) CheckDuplicates(child);
    }

    private static string Decode(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void RequireIdentity(string what, string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{what} SHA256 mismatch. Resolved identity: " + actual);
    }

    private sealed class FiniteDoubleWriter : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDouble();
        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        {
            if (double.IsFinite(value)) writer.WriteNumberValue(value);
            else writer.WriteNullValue();
        }
    }
}
