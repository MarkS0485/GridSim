using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.IO.Cim;
using GridSim.Core.IO.Cim.Study;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Cli;

/// <summary>
/// RC3 P3: <c>gridsim cgmes-study &lt;study.json&gt; [--out &lt;result.json&gt;] [--export &lt;model.xml&gt;]</c>. A strict v1 study
/// file names one CGMES profile bundle (each profile with its declared type, version, path and SHA-256, and the bundle
/// SHA-256 over the ordered manifest) and runs the full job through <see cref="CgmesStudyRunner"/>: import, topology,
/// control application, AC solve, optional P1 verification, export and read-back. Mirrors
/// <see cref="TransmissionSecurityCommand"/>: duplicate and unknown properties refused, temp-file and no-overwrite writes.
/// </summary>
internal static class CgmesStudyCommand
{
    private sealed record ProfileInput
    {
        public required string Profile { get; init; }
        public required string Version { get; init; }
        public required string Path { get; init; }
        public required string Sha256 { get; init; }
    }
    private sealed record BundleInput
    {
        public required string Sha256 { get; init; }
        public required ProfileInput[] Profiles { get; init; }
    }
    private sealed record ImportInput
    {
        public string Topology { get; init; } = "auto";
        public string TapPositions { get; init; } = "ssh";
        public string ShuntSections { get; init; } = "ssh";
        public bool StartingVoltages { get; init; } = true;
        public bool ApparentPowerRatings { get; init; } = true;
        public bool RequireSshSetPoints { get; init; } = true;
    }
    private sealed record SystemInput
    {
        public double? BaseMva { get; init; }
        public double? FrequencyHz { get; init; }
    }
    private sealed record ControlsInput
    {
        public bool Apply { get; init; } = true;
        public string Shunts { get; init; } = "asWritten";
        public double? TolerancePu { get; init; }
        public int? MaxSolves { get; init; }
    }
    private sealed record VoltageInput
    {
        public required double MinPu { get; init; }
        public required double MaxPu { get; init; }
    }
    private sealed record SwitchInput
    {
        public required string Mrid { get; init; }
        public required bool Open { get; init; }
    }
    private sealed record OutageInput { public required string Mrid { get; init; } }
    private sealed record DcInput { public required int MaxRounds { get; init; } }
    private sealed record SecurityInput
    {
        public required OutageInput[] Outages { get; init; }
        public required string PostFaultRating { get; init; }
        public DcInput? Dc { get; init; }
    }
    private sealed record Request
    {
        public required int SchemaVersion { get; init; }
        public required BundleInput Bundle { get; init; }
        public ImportInput? Import { get; init; }
        public SystemInput? System { get; init; }
        public ControlsInput? Controls { get; init; }
        public required VoltageInput Voltage { get; init; }
        public SwitchInput[]? SwitchActions { get; init; }
        public SecurityInput? Security { get; init; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private static readonly JsonSerializerOptions OutputJson = CreateOutputOptions();

    private static JsonSerializerOptions CreateOutputOptions()
    {
        var options = new JsonSerializerOptions(Json);
        options.Converters.Add(new FiniteDoubleWriter());
        return options;
    }

    public static int Run(List<string> arguments)
    {
        string? output = null, export = null, archivePath = null;
        bool usage = arguments.Count < 2 || arguments[1].StartsWith('-');
        for (int i = 2; !usage && i < arguments.Count; i += 2)
        {
            if (i + 1 >= arguments.Count || arguments[i + 1].StartsWith('-')) { usage = true; break; }
            if (arguments[i] == "--out" && output is null) output = arguments[i + 1];
            else if (arguments[i] == "--export" && export is null) export = arguments[i + 1];
            else if (arguments[i] == "--archive" && archivePath is null) archivePath = arguments[i + 1];   // R4-11b
            else usage = true;
        }
        if (usage)
        {
            Console.Error.WriteLine("usage: gridsim cgmes-study <study.json> [--out <result.json>] [--export <model.xml>] [--archive <bundle.zip>]");
            return CliExit.Usage;
        }
        var temporaries = new List<string>();
        try
        {
            string input = Path.GetFullPath(arguments[1]);
            output = output is null ? null : Path.GetFullPath(output);
            export = export is null ? null : Path.GetFullPath(export);
            foreach (string? target in new[] { output, export })
                if (target is not null && (File.Exists(target) || Directory.Exists(target)))
                    throw new IOException($"Output path '{target}' already exists; use a new path for this invocation.");
            if (output is not null && export is not null && string.Equals(output, export, StringComparison.OrdinalIgnoreCase))
                throw new IOException("--out and --export name the same file.");
            byte[] bytes = File.ReadAllBytes(input);
            using (JsonDocument document = JsonDocument.Parse(bytes)) CheckDuplicates(document.RootElement);
            Request request = JsonSerializer.Deserialize<Request>(bytes, Json) ?? throw new InvalidDataException("Study is null.");
            if (request.SchemaVersion != 1 || request.Bundle is null || request.Voltage is null || request.Bundle.Profiles is null
                || string.IsNullOrWhiteSpace(request.Bundle.Sha256))
                throw new InvalidDataException("Schema version 1 with bundle (sha256, profiles) and voltage is required.");
            if (request.Bundle.Profiles.Length == 0 || request.Bundle.Profiles.Any(p => p is null))
                throw new InvalidDataException("bundle.profiles must list at least one profile and no null entry.");

            // Read each profile once; the study works on these bytes only.
            string directory = Path.GetDirectoryName(input)!;
            var profiles = new List<CgmesProfileInput>();
            // R4-11b: with --archive the profile paths name entries of that ZIP (read in sorted order, path traversal and
            // duplicate profiles refused) instead of files; hashes and everything after are unchanged.
            Dictionary<string, byte[]>? archived = null;
            if (archivePath is not null)
                archived = GridSim.Core.IO.Cim.Zip.CgmesArchiveReader.ReadFile(Path.GetFullPath(archivePath)).Entries
                    .ToDictionary(e => e.Path, e => e.Bytes, StringComparer.Ordinal);
            foreach (ProfileInput p in request.Bundle.Profiles)
            {
                if (archived is not null)
                {
                    string entry = p.Path.Replace('\\', '/');
                    if (!archived.TryGetValue(entry, out byte[]? entryBytes))
                        throw new InvalidDataException($"Profile {p.Profile}: '{entry}' is not an entry of the archive '{archivePath}'.");
                    profiles.Add(new CgmesProfileInput(p.Profile, p.Version, entry, p.Sha256, entryBytes));
                    continue;
                }
                if (p.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Profile {p.Profile} ({p.Path}): zipped bundles are not supported in v1; list each profile document.");
                string full = Path.GetFullPath(p.Path, directory);
                profiles.Add(new CgmesProfileInput(p.Profile, p.Version, p.Path.Replace('\\', '/'), p.Sha256, File.ReadAllBytes(full)));
            }
            string identity = CgmesBundleInspector.BundleIdentity(profiles);
            if (!string.Equals(identity, request.Bundle.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Bundle SHA256 mismatch. Resolved identity: " + identity);

            ImportInput im = request.Import ?? new ImportInput();
            var options = new CimImportOptions(
                im.Topology switch
                {
                    "auto" => CimTopology.Auto, "topologicalNodes" => CimTopology.TopologicalNodes, "switches" => CimTopology.Switches,
                    _ => throw new InvalidDataException("import.topology must be auto, topologicalNodes or switches."),
                },
                Positions(im.TapPositions, "import.tapPositions"), Positions(im.ShuntSections, "import.shuntSections"),
                im.StartingVoltages, im.ApparentPowerRatings, im.RequireSshSetPoints);
            ControlsInput ci = request.Controls ?? new ControlsInput();
            var defaults = new CimControlOptions();
            var controls = new CimControlOptions(ci.TolerancePu ?? defaults.TolerancePu, ci.MaxSolves ?? defaults.MaxSolves,
                ci.Shunts switch
                {
                    "asWritten" => CimShuntControlMode.AsWritten, "discrete" => CimShuntControlMode.Discrete,
                    "continuous" => CimShuntControlMode.Continuous, "held" => CimShuntControlMode.Held,
                    _ => throw new InvalidDataException("controls.shunts must be asWritten, discrete, continuous or held."),
                });
            if (!(controls.TolerancePu > 0) || !double.IsFinite(controls.TolerancePu) || controls.MaxSolves < 1)
                throw new InvalidDataException("controls.tolerancePu must be a finite number above 0 and controls.maxSolves at least 1.");
            CgmesSecurityRequest? security = null;
            if (request.Security is SecurityInput s)
            {
                if (s.Outages is null || s.Outages.Length == 0 || s.Outages.Any(o => o is null))
                    throw new InvalidDataException("security.outages must list at least one outage.");
                PostFaultRating rating = s.PostFaultRating switch
                {
                    "A" => PostFaultRating.RateA, "B" => PostFaultRating.RateB, "C" => PostFaultRating.RateC,
                    _ => throw new InvalidDataException("security.postFaultRating must be A, B or C."),
                };
                security = new CgmesSecurityRequest(s.Outages.Select(o => o.Mrid).ToArray(), rating,
                    s.Dc?.MaxRounds ?? new GridSim.Core.Analysis.ScopfOptions().MaxRounds);
            }
            if (request.SwitchActions is { } sa && sa.Any(a => a is null))
                throw new InvalidDataException("switchActions cannot hold a null entry.");
            var studyRequest = new CgmesStudyRequest(profiles, options, request.Voltage.MinPu, request.Voltage.MaxPu,
                request.System?.BaseMva ?? 100.0, request.System?.FrequencyHz, ci.Apply, controls,
                request.SwitchActions?.Select(a => new CgmesSwitchAction(a.Mrid, a.Open)).ToArray(), security,
                Path.GetFileNameWithoutExtension(input));

            CgmesStudyRun run = CgmesStudyRunner.Run(studyRequest, model => Gated.AdmitCgmes(archivePath is null ? profiles.Select(p => Path.GetFullPath(p.Path, directory)) : new[] { Path.GetFullPath(archivePath) }, model));
            CgmesStudyResult result = run.Result;
            string json = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                provenance = new
                {
                    studySha256 = CgmesBundleInspector.Hash(bytes),
                    bundleSha256 = identity,
                    inputs = result.Bundle.Profiles.Select(p => new { p.Profile, p.DeclaredVersion, p.Path, p.Sha256 }),
                    exportSha256 = run.ExportDocument is null ? null : result.Exchange.DocumentSha256,
                    engine = GridSim.Core.BuildInfo.InformationalVersion,
                },
                effectiveOptions = new
                {
                    import = options,
                    system = new { baseMva = result.Import.BaseMva, result.Import.BaseMvaSource, frequencyHz = result.Import.FrequencyHz, result.Import.FrequencySource },
                    controls = new { apply = ci.Apply, controls.TolerancePu, controls.MaxSolves, shunts = controls.Shunts },
                    voltage = new { request.Voltage.MinPu, request.Voltage.MaxPu },
                    switchActions = studyRequest.SwitchActions ?? [],
                    security,
                    tolerances = new
                    {
                        voltagePu = CgmesStudyRunner.VoltageTolerancePu, angleDeg = CgmesStudyRunner.AngleToleranceDeg,
                        powerMwOrMvar = CgmesStudyRunner.PowerToleranceMw,
                    },
                },
                result,
            }, OutputJson);

            // Write the exchange document first, then the result, each through a temporary file and a final move.
            if (export is not null && run.ExportDocument is not null) Publish(export, run.ExportDocument, temporaries);
            if (output is not null) Publish(output, json + "\n", temporaries);
            Console.WriteLine(json);
            if (export is not null && run.ExportDocument is null)
                Console.Error.WriteLine("cgmes-study: no exchange document was produced (see result.exchange.reason); --export not written.");
            return result.Status == "SECURE" ? CliExit.Ok : CliExit.Verdict;
        }
        catch (GateRefusedException) { throw; }
        catch (Exception ex) when (ex is CgmesStudyInputException or InvalidDataException or IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or InvalidOperationException or KeyNotFoundException or System.Xml.XmlException)
        {
            Console.Error.WriteLine("cgmes-study: " + CliExit.UserMessage(ex));
            return CliExit.CaseLoadFailure(ex);
        }
        finally
        {
            foreach (string t in temporaries) if (File.Exists(t)) File.Delete(t);
        }
    }

    private static void Publish(string path, string text, List<string> temporaries)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        temporaries.Add(temporary);
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: false);
        temporaries.Remove(temporary);
    }

    private static CimTapPositions Positions(string value, string field) => value switch
    {
        "ssh" => CimTapPositions.SteadyStateHypothesis,
        "sv" => CimTapPositions.StateVariables,
        _ => throw new InvalidDataException($"{field} must be ssh or sv."),
    };

    private sealed class FiniteDoubleWriter : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDouble();
        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        {
            if (double.IsFinite(value)) writer.WriteNumberValue(value);
            else writer.WriteNullValue();
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
}
