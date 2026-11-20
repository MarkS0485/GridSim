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
        if (arguments.Count is not (2 or 4) || arguments[1].StartsWith('-') ||
            (arguments.Count == 4 && (arguments[2] != "--out" || arguments[3].StartsWith('-'))))
        {
            Console.Error.WriteLine("usage: gridsim dynamics-study <study.json> [--out <result.json>]");
            return CliExit.Usage;
        }
        string? temporary = null;
        try
        {
            string input = Path.GetFullPath(arguments[1]);
            string? output = arguments.Count == 4 ? Path.GetFullPath(arguments[3]) : null;
            if (output is not null && (File.Exists(output) || Directory.Exists(output)))
                throw new IOException("Result path already exists; use a new path for this invocation.");
            byte[] studyBytes = File.ReadAllBytes(input);
            using JsonDocument document = JsonDocument.Parse(studyBytes);
            JsonElement rootEl = document.RootElement;
            CheckDuplicates(rootEl);
            Obj(rootEl, "study", new[] { "schemaVersion", "raw", "dyr", "initialisation", "integration", "criteria", "angleReference", "channels", "cases" },
                new[] { "execution", "seq", "adaptive" });
            if (Int(rootEl.GetProperty("schemaVersion"), "schemaVersion") != 1)
                throw new InvalidDataException("schemaVersion must be 1.");
            if (rootEl.TryGetProperty("seq", out _))
                throw new InvalidDataException("seq: a SEQ attachment is not supported by dynamics-study v1 (balanced RMS study only); remove it.");
            string baseDir = Path.GetDirectoryName(input)!;
            (string rawPath, string rawHash) = FileRef(rootEl.GetProperty("raw"), "raw", baseDir, ".raw");
            (string dyrPath, string dyrHash) = FileRef(rootEl.GetProperty("dyr"), "dyr", baseDir, ".dyr");

            byte[] rawBytes = File.ReadAllBytes(rawPath);
            byte[] dyrBytes = File.ReadAllBytes(dyrPath);
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
            Obj(integ, "integration", new[] { "stepSeconds", "durationSeconds" }, Array.Empty<string>());
            double step = Num(integ.GetProperty("stepSeconds"), "integration.stepSeconds");
            double duration = Num(integ.GetProperty("durationSeconds"), "integration.durationSeconds");
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

            // RC4 R4-14: the optional variable-step companion run (absent: the study is exactly the incumbent's).
            DynamicsAdaptiveRequest? adaptive = rootEl.TryGetProperty("adaptive", out JsonElement adaptiveEl) ? Adaptive(adaptiveEl, baseDir) : null;

            // The incumbent loader (licence, notes), then the RAW parse with the same (default) options for the native keys.
            GridModel loaded = load(rawPath);
            PsseParseResult raw = PsseRawParser.ParseWithDiagnostics(Decode(rawBytes), Path.GetFileNameWithoutExtension(rawPath), new PsseReadOptions());
            if (raw.Model.AllGenerators.Count != loaded.AllGenerators.Count || raw.Model.Buses.Count != loaded.Buses.Count
                || raw.Model.Branches.Count != loaded.Branches.Count)
                throw new InvalidDataException("The RAW case read for its native identities is not the case the loader gave.");
            if (Hash(File.ReadAllBytes(rawPath)) != Hash(rawBytes) || Hash(File.ReadAllBytes(dyrPath)) != Hash(dyrBytes))
                throw new IOException("Case inputs changed during loading.");
            string schemas = Gated.FindV2Schemas(root) ?? throw new InvalidDataException("dynamics-study needs the v2 schemas (schemas/v2 beside the program or above it), and none was found.");

            var request = new DynamicsStudyRequest(raw, Decode(dyrBytes), Path.GetFileName(dyrPath), schemas, initialisation, step, duration,
                criteria, reference, channels, cases, parallelism, Adaptive: adaptive);
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

    private static void Obj(JsonElement e, string what, string[] required, string[] optional)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{what} must be an object.");
        foreach (JsonProperty p in e.EnumerateObject())
            if (!required.Contains(p.Name) && !optional.Contains(p.Name))
                throw new InvalidDataException($"{what}: unknown field '{p.Name}'.");
        foreach (string r in required)
            if (!e.TryGetProperty(r, out _)) throw new InvalidDataException($"{what}: missing required field '{r}'.");
    }

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
        string full = Path.GetFullPath(rel, baseDir);
        if (!full.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{what}.path must name a PSS/E {extension} file.");
        return (full, sha);
    }

    private static DynamicsAdaptiveRequest Adaptive(JsonElement e, string baseDir)
    {
        const string what = "adaptive";
        Obj(e, what, Array.Empty<string>(), new[] { "relativeTolerance", "absoluteTolerance", "initialStepSeconds", "minStepSeconds", "maxStepSeconds",
            "eventToleranceSeconds", "cctToleranceSeconds", "devices", "curves" });
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
            string dir = Path.GetFullPath(Str(cv, what + ".curves"), baseDir);
            if (!Directory.Exists(dir)) throw new InvalidDataException($"{what}.curves: directory '{dir}' does not exist.");
            try { curves = GridSim.Core.Protection.Devices.CurveLibrary.LoadDirectory(dir); }
            catch (GridSim.Core.Analysis.StudyCases.StudyCaseException ex) { throw new InvalidDataException($"{what}.curves: {ex.Message}"); }
        }
        GridSim.Core.Protection.Devices.DeviceSet? devices = null;
        if (e.TryGetProperty("devices", out JsonElement dv))
        {
            (string path, string hash) = FileRef(dv, what + ".devices", baseDir, ".json");
            byte[] bytes = File.ReadAllBytes(path);
            RequireIdentity(what + ".devices", hash, Hash(bytes));
            try { devices = GridSim.Core.Protection.Devices.DeviceSet.Parse(bytes); }
            catch (GridSim.Core.Analysis.StudyCases.StudyCaseException ex) { throw new InvalidDataException($"{what}.devices: {ex.Message}"); }
        }
        return new DynamicsAdaptiveRequest(options, cct, devices, curves);
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
            default:
                throw new InvalidDataException($"{what}: unsupported event kind '{kind}' (busFault, faultClear, branchTrip, branchReclose, machineTrip, loadStep); an unbalanced fault or a fault part-way along a line is not supported by dynamics-study v1.");
        }
    }

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
