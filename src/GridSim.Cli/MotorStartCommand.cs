using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Analysis.MotorStart;

namespace GridSim.Cli;

/// <summary>
/// RC3 P8: <c>gridsim motor-start &lt;study.json&gt; [--out &lt;result.json&gt;]</c>. Parses the strict v1 study document,
/// binds the native v2 case by the digest of its consumed-file manifest (the transmission-security rule: the manifest,
/// every network, asset and library file and the v2 schema set, each by SHA-256), loads it through the native motor
/// adapter with loader balancing suppressed (licence enforced), and hands a typed request to
/// <see cref="MotorStartRunner"/>. The existing steady-state induction machine solve and every other verb are untouched.
/// </summary>
internal static class MotorStartCommand
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

    public static int Run(List<string> arguments, string root)
    {
        if (arguments.Count is not (2 or 4) || arguments[1].StartsWith('-') ||
            (arguments.Count == 4 && (arguments[2] != "--out" || arguments[3].StartsWith('-'))))
        {
            Console.Error.WriteLine("usage: gridsim motor-start <study.json> [--out <result.json>]");
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
            JsonElement r = document.RootElement;
            TransmissionSecurityCommand.CheckDuplicates(r);
            Obj(r, "study", new[] { "schemaVersion", "case", "motor", "initial", "start", "integration", "termination", "preStart" }, new[] { "report" });
            if (Int(r.GetProperty("schemaVersion"), "schemaVersion") != 1) throw new InvalidDataException("schemaVersion must be 1.");

            JsonElement c = r.GetProperty("case");
            Obj(c, "case", new[] { "path", "sha256" }, Array.Empty<string>());
            string rel = Str(c.GetProperty("path"), "case.path");
            string sha = Str(c.GetProperty("sha256"), "case.sha256");
            if (string.IsNullOrWhiteSpace(rel) || string.IsNullOrWhiteSpace(sha)) throw new InvalidDataException("case needs a path and a sha256.");
            string casePath = Path.GetFullPath(rel, Path.GetDirectoryName(input)!);
            if (Directory.Exists(casePath))
                throw new InvalidDataException("case.path names a folder: a native v1 case folder has no motor rows, so motor-start v1 takes a gridsim-case/2 manifest whose network files place the motor.");
            if (!casePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("case.path must name a gridsim-case/2 manifest (.json).");

            string motor = Str(r.GetProperty("motor"), "motor");
            JsonElement init = r.GetProperty("initial");
            Obj(init, "initial", new[] { "speedPu", "connected" }, Array.Empty<string>());
            var initial = new MotorStartInitialState(Num(init.GetProperty("speedPu"), "initial.speedPu"), Bool(init.GetProperty("connected"), "initial.connected"));
            JsonElement start = r.GetProperty("start");
            Obj(start, "start", new[] { "atSeconds", "method" }, Array.Empty<string>());
            MotorStartMethod method = Str(start.GetProperty("method"), "start.method") switch
            {
                "directOnLine" => MotorStartMethod.DirectOnLine,
                string s => throw new InvalidDataException($"start.method must be \"directOnLine\" (got \"{s}\"); other starting methods are not supported in v1."),
            };
            JsonElement integ = r.GetProperty("integration");
            Obj(integ, "integration", new[] { "stepSeconds" }, Array.Empty<string>());
            JsonElement term = r.GetProperty("termination");
            Obj(term, "termination", new[] { "runningSpeedPu", "horizonSeconds", "stallDwellSeconds", "stallAcceleratingTorquePu" }, Array.Empty<string>());
            var termination = new MotorStartTermination(Num(term.GetProperty("runningSpeedPu"), "termination.runningSpeedPu"),
                Num(term.GetProperty("horizonSeconds"), "termination.horizonSeconds"), Num(term.GetProperty("stallDwellSeconds"), "termination.stallDwellSeconds"),
                Num(term.GetProperty("stallAcceleratingTorquePu"), "termination.stallAcceleratingTorquePu"));
            JsonElement pre = r.GetProperty("preStart");
            Obj(pre, "preStart", new[] { "pgToleranceMw", "vmTolerancePu", "maxSlackMovementMw" }, Array.Empty<string>());
            var tolerance = new MotorStartPreStartTolerance(Num(pre.GetProperty("pgToleranceMw"), "preStart.pgToleranceMw"),
                Num(pre.GetProperty("vmTolerancePu"), "preStart.vmTolerancePu"), Num(pre.GetProperty("maxSlackMovementMw"), "preStart.maxSlackMovementMw"));
            int sampleEvery = 1;
            double? dipLimit = null;
            if (r.TryGetProperty("report", out JsonElement rep))
            {
                Obj(rep, "report", Array.Empty<string>(), new[] { "sampleEverySteps", "maxVoltageDipPercent" });
                if (rep.TryGetProperty("sampleEverySteps", out JsonElement se)) sampleEvery = Int(se, "report.sampleEverySteps");
                if (rep.TryGetProperty("maxVoltageDipPercent", out JsonElement dl)) dipLimit = Num(dl, "report.maxVoltageDipPercent");
            }
            var request = new MotorStartRequest(motor, initial, Num(start.GetProperty("atSeconds"), "start.atSeconds"), method,
                Num(integ.GetProperty("stepSeconds"), "integration.stepSeconds"), termination, tolerance, sampleEvery, dipLimit);

            // Case identity before any load, then the load, then the identity again (an input changed mid-load is refused).
            List<TransmissionSecurityCommand.InputFile> files = TransmissionSecurityCommand.Inputs(casePath, root);
            string identity = TransmissionSecurityCommand.Hash(Encoding.UTF8.GetBytes(string.Concat(files.Select(f => f.Path + "\t" + f.Sha256 + "\n"))));
            TransmissionSecurityCommand.RequireIdentity(sha, identity);
            MotorStartCaseInput loaded = Gated.MotorNative(casePath, root);
            if (!files.SequenceEqual(TransmissionSecurityCommand.Inputs(casePath, root)))
                throw new IOException("Case inputs changed during loading.");

            MotorStartResult result = MotorStartRunner.Run(loaded, request);
            string json = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                provenance = new
                {
                    studySha256 = TransmissionSecurityCommand.Hash(studyBytes),
                    caseSha256 = identity,
                    inputs = files,
                    loadPolicy = "Native dispatch suppressed",
                    engine = GridSim.Core.BuildInfo.InformationalVersion,
                },
                result,
            }, OutputJson);
            if (output is not null)
            {
                temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, json + "\n", new UTF8Encoding(false));
                File.Move(temporary, output, overwrite: false);
                temporary = null;
            }
            Console.WriteLine(json);
            return result.Outcome == MotorStartOutcome.Running ? CliExit.Ok : CliExit.Verdict;
        }
        catch (GateRefusedException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            Console.Error.WriteLine("motor-start: " + CliExit.UserMessage(ex));
            return CliExit.CaseLoadFailure(ex);
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void Obj(JsonElement e, string what, string[] required, string[] optional)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{what} must be an object.");
        foreach (JsonProperty p in e.EnumerateObject())
            if (!required.Contains(p.Name) && !optional.Contains(p.Name))
                throw new InvalidDataException($"{what}: unknown field '{p.Name}'.");
        foreach (string q in required)
            if (!e.TryGetProperty(q, out _)) throw new InvalidDataException($"{what}: missing required field '{q}'.");
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

    private static bool Bool(JsonElement e, string what)
    {
        if (e.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException($"{what} must be true or false.");
        return e.GetBoolean();
    }

    private static string Str(JsonElement e, string what)
    {
        if (e.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{what} must be a string.");
        return e.GetString()!;
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
