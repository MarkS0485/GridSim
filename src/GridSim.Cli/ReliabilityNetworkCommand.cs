using System.Globalization;
using System.Text;
using System.Text.Json;
using GridSim.Core.Analysis;
using GridSim.Core.Analysis.PlanningStudies;
using GridSim.Core.Analysis.Reliability.Network;
using GridSim.Core.Analysis.StudyCases;
using GridSim.Core.Model;
using GridSim.Core.Protection.Devices;

namespace GridSim.Cli;

/// <summary>
/// RC4 R4-13: <c>gridsim reliability-network &lt;study.json&gt; [--out &lt;result.json&gt;] [--threads N]</c>. A strict
/// <c>gridsim-reliability-network/1</c> study: reliability and restoration joined on a real case. Failure data attaches to
/// R4-01 groups; the R4-07 device set and declared switches isolate; normally-open ties back-feed; every restored network is
/// verified by the incumbent restoration enumerator. <c>method</c> is <c>enumeration</c> (deterministic first order) or
/// <c>monte-carlo</c> (seeded, R4-02 streams). The case is bound by SHA-256. The result is written only to a new file.
/// The RC3 <c>reliability-study</c> and <c>restoration-study</c> verbs are unchanged.
/// </summary>
internal static class ReliabilityNetworkCommand
{
    private const string Verb = "reliability-network";
    private const string Usage = "usage: gridsim reliability-network <study.json> [--out <result.json>] [--threads N]";

    private sealed record CaseRow { public string? Path { get; init; } public string? BuiltIn { get; init; } public required string Sha256 { get; init; } }
    private sealed record FileRow { public required string Path { get; init; } }
    private sealed record BranchRef { public required int Index { get; init; } public required int From { get; init; } public required int To { get; init; } }
    private sealed record GroupRow
    {
        public required string Group { get; init; }
        public double? FailuresPerKmYear { get; init; }
        public double? FailuresPerItemYear { get; init; }
        public double? RepairHours { get; init; }
        public double? SwitchingHours { get; init; }
    }
    private sealed record OverrideRow
    {
        public required BranchRef Branch { get; init; }
        public double? FailuresPerYear { get; init; }
        public double? RepairHours { get; init; }
        public double? SwitchingHours { get; init; }
    }
    private sealed record PointRow { public required string Id { get; init; } public required BranchRef Branch { get; init; } }
    private sealed record LoadRow { public required string Id { get; init; } public required int Bus { get; init; } public required int Customers { get; init; } public double? AverageLoadMw { get; init; } }
    private sealed record AcRow { public required bool Enabled { get; init; } public double? MinPu { get; init; } public double? MaxPu { get; init; } }
    private sealed record McRow
    {
        public required ulong Seed { get; init; }
        public required long Years { get; init; }
        public string? RepairDistribution { get; init; }
        public string? Domain { get; init; }
    }
    private sealed record Request
    {
        public required int SchemaVersion { get; init; }
        public required string Study { get; init; }
        public required string Method { get; init; }
        public required CaseRow Case { get; init; }
        public required FileRow Groups { get; init; }
        public FileRow? Devices { get; init; }
        public required GroupRow[] FailureData { get; init; }
        public OverrideRow[]? Overrides { get; init; }
        public PointRow[]? Switches { get; init; }
        public PointRow[]? Ties { get; init; }
        public required LoadRow[] LoadPoints { get; init; }
        public double? SwitchingHours { get; init; }
        public required double HoursPerYear { get; init; }
        public required AcRow AcVerification { get; init; }
        public int? Order { get; init; }
        public McRow? MonteCarlo { get; init; }
    }

    public static int Run(List<string> arguments, string root, Func<string, GridModel> load)
    {
        if (!TryArguments(arguments, out string input, out string? output, out int threads))
        {
            Console.Error.WriteLine(Usage);
            return CliExit.Usage;
        }
        string? temporary = null;
        try
        {
            string studyPath = Path.GetFullPath(input);
            string? outPath = output is null ? null : Path.GetFullPath(output);
            if (outPath is not null && (File.Exists(outPath) || Directory.Exists(outPath)))
                throw new IOException("Result path already exists; use a new path for this invocation.");
            byte[] bytes = File.ReadAllBytes(studyPath);
            using (JsonDocument document = JsonDocument.Parse(bytes)) TransmissionSecurityCommand.CheckDuplicates(document.RootElement);
            Request req = JsonSerializer.Deserialize<Request>(bytes, TransmissionSecurityCommand.Json) ?? throw new InvalidDataException("Study is null.");
            if (req.SchemaVersion != 1) throw new InvalidDataException("schemaVersion must be 1.");
            if (req.Study != Verb) throw new InvalidDataException($"study must be \"{Verb}\".");
            NetworkMethod method = req.Method switch
            {
                "enumeration" => NetworkMethod.Enumeration,
                "monte-carlo" => NetworkMethod.MonteCarlo,
                _ => throw new InvalidDataException("method must be enumeration or monte-carlo."),
            };
            if ((method == NetworkMethod.MonteCarlo) != (req.MonteCarlo is not null))
                throw new InvalidDataException(method == NetworkMethod.MonteCarlo
                    ? "method monte-carlo needs monteCarlo { seed, years }."
                    : "monteCarlo belongs to method monte-carlo.");

            (GridModel model, object caseProvenance) = LoadCase(req.Case, studyPath, root, load);
            string dir = Path.GetDirectoryName(studyPath)!;
            byte[] groupBytes = File.ReadAllBytes(Path.GetFullPath(req.Groups.Path, dir));
            BoundGroups groups = BoundGroups.Bind(ModelGroupsDocument.Parse(groupBytes), model);
            IReadOnlyList<PlacedDevice> devices = [];
            string? deviceSha = null;
            if (req.Devices is not null)
            {
                byte[] deviceBytes = File.ReadAllBytes(Path.GetFullPath(req.Devices.Path, dir));
                deviceSha = TransmissionSecurityCommand.Hash(deviceBytes);
                devices = DeviceSet.Parse(deviceBytes).Devices;
            }
            AcVerificationOptions ac = req.AcVerification.Enabled
                ? new AcVerificationOptions(true, req.AcVerification.MinPu ?? throw new InvalidDataException("acVerification.minPu is required when enabled."),
                    req.AcVerification.MaxPu ?? throw new InvalidDataException("acVerification.maxPu is required when enabled."))
                : req.AcVerification.MinPu is null && req.AcVerification.MaxPu is null
                    ? new AcVerificationOptions(false)
                    : throw new InvalidDataException("acVerification voltage bounds belong to enabled true.");
            NetworkMonteCarlo? mc = req.MonteCarlo is null ? null
                : new NetworkMonteCarlo(req.MonteCarlo.Seed, req.MonteCarlo.Years, req.MonteCarlo.RepairDistribution ?? "exponential",
                    req.MonteCarlo.Domain ?? "reliability-network", threads);
            var request = new NetworkReliabilityRequest(
                model, groups,
                req.FailureData.Select(g => new FailureGroupData(g.Group, g.FailuresPerKmYear, g.FailuresPerItemYear, g.RepairHours, g.SwitchingHours)).ToArray(),
                (req.Overrides ?? []).Select(o => new FailureOverride(o.Branch.Index, o.Branch.From, o.Branch.To, o.FailuresPerYear, o.RepairHours, o.SwitchingHours)).ToArray(),
                devices,
                (req.Switches ?? []).Select(s => new SwitchPoint(s.Id, s.Branch.Index, s.Branch.From, s.Branch.To)).ToArray(),
                (req.Ties ?? []).Select(t => new TiePoint(t.Id, t.Branch.Index, t.Branch.From, t.Branch.To)).ToArray(),
                req.LoadPoints.Select(l => new NetworkLoadPoint(l.Id, l.Bus, l.Customers, l.AverageLoadMw)).ToArray(),
                req.SwitchingHours, req.HoursPerYear, ac, method, mc, req.Order ?? 1);
            NetworkReliabilityResult result = NetworkReliabilityStudy.Run(request);

            string json = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                format = NetworkReliabilityStudy.FormatVersion,
                study = Verb,
                provenance = new
                {
                    studySha256 = TransmissionSecurityCommand.Hash(bytes),
                    groupsSha256 = TransmissionSecurityCommand.Hash(groupBytes),
                    devicesSha256 = deviceSha,
                    @case = caseProvenance,
                    engine = GridSim.Core.BuildInfo.InformationalVersion,
                },
                effectiveOptions = new
                {
                    method = req.Method, hoursPerYear = req.HoursPerYear, switchingHours = req.SwitchingHours,
                    acVerification = ac, order = request.Order, failureData = request.FailureData, overrides = request.Overrides,
                    switches = request.Switches, ties = request.Ties, loadPoints = request.LoadPoints,
                    monteCarlo = mc is null ? null : new { mc.Seed, mc.Years, mc.RepairDistribution, mc.Domain },
                },
                stepOrder = NetworkReliabilityStudy.StepOrder,
                result,
            }, TransmissionSecurityCommand.OutputJson);
            if (outPath is not null)
            {
                temporary = outPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, json + "\n", new UTF8Encoding(false));
                File.Move(temporary, outPath, overwrite: false);
                temporary = null;
            }
            CliResultFromJson.Fill(json);
            Console.WriteLine(json);
            return result.Status == "COMPLETE" ? CliExit.Ok : CliExit.Verdict;
        }
        catch (GateRefusedException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException
            or InvalidOperationException or KeyNotFoundException or PlanningManifestException)
        {
            Console.Error.WriteLine(Verb + ": " + CliExit.UserMessage(ex));
            return CliExit.CaseLoadFailure(ex);
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool TryArguments(List<string> a, out string input, out string? output, out int threads)
    {
        input = ""; output = null; threads = 0;
        if (a.Count < 2 || a[1].StartsWith('-')) return false;
        input = a[1];
        for (int i = 2; i < a.Count; i++)
        {
            if (a[i] == "--out" && i + 1 < a.Count && !a[i + 1].StartsWith('-') && output is null) output = a[++i];
            else if (a[i] == "--threads" && i + 1 < a.Count && int.TryParse(a[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int t) && t >= 1 && threads == 0) { threads = t; i++; }
            else return false;
        }
        return true;
    }

    private static (GridModel, object) LoadCase(CaseRow c, string studyPath, string root, Func<string, GridModel> load)
    {
        if ((c.Path is null) == (c.BuiltIn is null)) throw new InvalidDataException("Case must specify exactly one path or builtIn.");
        if (string.IsNullOrWhiteSpace(c.Sha256)) throw new InvalidDataException("case.sha256 is required.");
        if (c.Path is string relative)
        {
            string path = Path.GetFullPath(relative, Path.GetDirectoryName(studyPath)!);
            var files = TransmissionSecurityCommand.Inputs(path, root);
            bool native = Directory.Exists(path) || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            string identity = native
                ? TransmissionSecurityCommand.Hash(Encoding.UTF8.GetBytes(string.Concat(files.Select(f => f.Path + "\t" + f.Sha256 + "\n"))))
                : files.Single().Sha256;
            TransmissionSecurityCommand.RequireIdentity(c.Sha256, identity);
            GridModel model = native ? Gated.TransmissionNative(path, root) : load(path);
            if (!files.SequenceEqual(TransmissionSecurityCommand.Inputs(path, root)))
                throw new IOException("Case inputs changed during loading.");
            return (model, new { caseSha256 = identity, inputs = files, loadPolicy = native ? "Native dispatch suppressed" : "Incumbent file loader" });
        }
        string name = c.BuiltIn!;
        if (!BuiltInCases.Names.Contains(name.ToLowerInvariant())) throw new UnknownCaseException(name);
        GridModel built = load(name);
        string id = TransmissionSecurityCommand.Hash(Encoding.UTF8.GetBytes(GridSim.Core.BuildInfo.InformationalVersion + "\n" +
            JsonSerializer.Serialize(built, TransmissionSecurityCommand.ModelJson)));
        TransmissionSecurityCommand.RequireIdentity(c.Sha256, id);
        return (built, new { caseSha256 = id, loadPolicy = "Incumbent built-in loader" });
    }
}
