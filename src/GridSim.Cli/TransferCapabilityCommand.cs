using System.Text;
using System.Text.Json;
using GridSim.Core.Analysis;
using GridSim.Core.Analysis.TransmissionSecurity;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Cli;

/// <summary>
/// RC3 P4: `gridsim transfer-capability &lt;study.json&gt; [--out &lt;result.json&gt;]`. A separate verb rather than a
/// mode of transmission-security, so the preventive study's strict v1 schema, flag set and exit mapping stay exactly
/// as published. Case identity, strict parsing and result writing reuse the transmission-security helpers.
/// </summary>
internal static class TransferCapabilityCommand
{
    private sealed record CaseInput
    {
        public string? Path { get; init; }
        public string? BuiltIn { get; init; }
        public required string Sha256 { get; init; }
    }
    private sealed record VoltageInput
    {
        public required double MinPu { get; init; }
        public required double MaxPu { get; init; }
    }
    private sealed record ParticipantInput
    {
        public required int Index { get; init; }
        public required int Bus { get; init; }
        public required double Weight { get; init; }
    }
    private sealed record OutageInput
    {
        public required int Index { get; init; }
        public required int From { get; init; }
        public required int To { get; init; }
    }
    private sealed record InterfaceBranchInput
    {
        public required int Index { get; init; }
        public required int From { get; init; }
        public required int To { get; init; }
        public required int Sign { get; init; }
    }
    private sealed record InterfaceInput
    {
        public required string Name { get; init; }
        public required InterfaceBranchInput[] Branches { get; init; }
        public double? LimitMw { get; init; }
    }
    private sealed record SearchInput
    {
        public required double MaxMw { get; init; }
        public required double StepMw { get; init; }
        public required double ToleranceMw { get; init; }
        public required int MaxTrials { get; init; }
        public int? Probes { get; init; }
    }
    private sealed record Request
    {
        public required int SchemaVersion { get; init; }
        public required CaseInput Case { get; init; }
        public required ParticipantInput[] Sources { get; init; }
        public required ParticipantInput[] Sinks { get; init; }
        public required OutageInput[] Outages { get; init; }
        public required VoltageInput Voltage { get; init; }
        public required string PostFaultRating { get; init; }
        public required SearchInput Search { get; init; }
        public InterfaceInput[]? Interfaces { get; init; }
    }

    public static int Run(List<string> arguments, string root, Func<string, GridModel> load)
    {
        if (arguments.Count is not (2 or 4) || arguments[1].StartsWith('-') ||
            (arguments.Count == 4 && (arguments[2] != "--out" || arguments[3].StartsWith('-'))))
        {
            Console.Error.WriteLine("usage: gridsim transfer-capability <study.json> [--out <result.json>]");
            return CliExit.Usage;
        }
        string? temporary = null;
        try
        {
            string input = Path.GetFullPath(arguments[1]);
            string? output = arguments.Count == 4 ? Path.GetFullPath(arguments[3]) : null;
            if (output is not null && (File.Exists(output) || Directory.Exists(output)))
                throw new IOException("Result path already exists; use a new path for this invocation.");
            byte[] bytes = File.ReadAllBytes(input);
            using (JsonDocument document = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(bytes)))
            {
                TransmissionSecurityCommand.CheckDuplicates(document.RootElement);
                CheckShape(document.RootElement);
            }
            Request request = JsonSerializer.Deserialize<Request>(GridSim.Core.IO.Utf8Json.WithoutBom(bytes).Span, TransmissionSecurityCommand.Json)
                ?? throw new InvalidDataException("Study is null.");
            if (request.SchemaVersion != 1 || request.Case is null || request.Voltage is null || request.Search is null ||
                request.Outages is null || request.Sources is null || request.Sinks is null ||
                string.IsNullOrWhiteSpace(request.Case.Sha256))
                throw new InvalidDataException("Schema version 1 and all required objects are required.");
            if (request.Outages.Any(o => o is null) || request.Sources.Any(p => p is null) || request.Sinks.Any(p => p is null) ||
                request.Interfaces?.Any(i => i is null || i.Branches is null || i.Branches.Any(b => b is null)) == true)
                throw new InvalidDataException("Array rows cannot be null.");
            if ((request.Case.Path is null) == (request.Case.BuiltIn is null))
                throw new InvalidDataException("Case must specify exactly one path or builtIn.");
            PostFaultRating rating = request.PostFaultRating switch
            {
                "A" => PostFaultRating.RateA, "B" => PostFaultRating.RateB, "C" => PostFaultRating.RateC,
                _ => throw new InvalidDataException("postFaultRating must be A, B or C.")
            };

            // Case identity: the same resolution and digest rules as transmission-security.
            var files = new List<TransmissionSecurityCommand.InputFile>();
            GridModel model;
            string identity;
            string loadPolicy = "Incumbent built-in loader";
            if (request.Case.Path is string relative)
            {
                string path = Path.GetFullPath(relative, Path.GetDirectoryName(input)!);
                bool native = Directory.Exists(path) || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
                files = TransmissionSecurityCommand.Inputs(path, root);
                loadPolicy = native ? "Native dispatch suppressed" : "Incumbent file loader";
                identity = native
                    ? TransmissionSecurityCommand.Hash(Encoding.UTF8.GetBytes(string.Concat(files.Select(f => f.Path + "\t" + f.Sha256 + "\n"))))
                    : files.Single().Sha256;
                TransmissionSecurityCommand.RequireIdentity(request.Case.Sha256, identity);
                model = native ? Gated.TransmissionNative(path, root) : load(path);
                if (!files.SequenceEqual(TransmissionSecurityCommand.Inputs(path, root)))
                    throw new IOException("Case inputs changed during loading.");
            }
            else
            {
                string name = request.Case.BuiltIn!;
                if (!BuiltInCases.Names.Contains(name.ToLowerInvariant()))
                    throw new UnknownCaseException(name);
                model = load(name);
                identity = TransmissionSecurityCommand.Hash(Encoding.UTF8.GetBytes(GridSim.Core.BuildInfo.InformationalVersion + "\n" +
                    JsonSerializer.Serialize(model, TransmissionSecurityCommand.ModelJson)));
                TransmissionSecurityCommand.RequireIdentity(request.Case.Sha256, identity);
            }

            var resolved = new TransferStudyRequest(
                request.Sources.Select(p => new TransferParticipant(p.Index, p.Bus, p.Weight)).ToArray(),
                request.Sinks.Select(p => new TransferParticipant(p.Index, p.Bus, p.Weight)).ToArray(),
                request.Outages.Select(o => new BranchOutage(o.Index, o.From, o.To)).ToArray(),
                request.Voltage.MinPu, request.Voltage.MaxPu, rating,
                new TransferSearchOptions(request.Search.MaxMw, request.Search.StepMw, request.Search.ToleranceMw,
                    request.Search.MaxTrials, request.Search.Probes ?? 1),
                request.Interfaces?.Select(i => new TransferInterface(i.Name,
                    i.Branches.Select(b => new InterfaceBranch(b.Index, b.From, b.To, b.Sign)).ToArray(), i.LimitMw)).ToArray());
            TransferStudyResult result = TransferStudyRunner.Run(model, resolved);
            string studySha = TransmissionSecurityCommand.Hash(bytes);
            string json = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                study = "transfer-capability",
                provenance = new { studySha256 = studySha, caseSha256 = identity, inputs = files,
                    loadPolicy, engine = GridSim.Core.BuildInfo.InformationalVersion },
                effectiveOptions = resolved,
                inputGenerators = model.Generators.Select((g, i) => new { index = i, g.Bus, g.Pg, g.Qg, g.Pmin, g.Pmax }),
                result,
                comparator = TransferComparison.Unverified(result, GridSim.Core.BuildInfo.InformationalVersion, identity, studySha)
            }, TransmissionSecurityCommand.OutputJson);
            if (output is not null)
            {
                temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, json + "\n", new UTF8Encoding(false));
                File.Move(temporary, output, overwrite: false);
                temporary = null;
            }
            Console.WriteLine(json);
            return result.Status == "VERIFIED" ? CliExit.Ok : CliExit.Verdict;
        }
        catch (GateRefusedException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            Console.Error.WriteLine("transfer-capability: " + CliExit.UserMessage(ex));
            return CliExit.CaseLoadFailure(ex);
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Integer identities are checked before binding so a fractional or missing index is named clearly.</summary>
    private static void CheckShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Study must be a JSON object.");
        void Rows(string name, params string[] fields)
        {
            if (!root.TryGetProperty(name, out JsonElement rows) || rows.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"Study must contain a {name} array.");
            foreach (JsonElement row in rows.EnumerateArray())
                if (row.ValueKind != JsonValueKind.Object ||
                    fields.Any(f => !row.TryGetProperty(f, out JsonElement v) || !v.TryGetInt32(out _)))
                    throw new InvalidDataException($"Every {name} row needs integer {string.Join(", ", fields)}.");
        }
        Rows("sources", "index", "bus");
        Rows("sinks", "index", "bus");
        Rows("outages", "index", "from", "to");
    }
}
