using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Analysis.Restoration;
using GridSim.Core.Model;

namespace GridSim.Cli;

/// <summary>
/// RC3 P8: <c>gridsim restoration-study &lt;study.json&gt; [--out &lt;result.json&gt;]</c>. Deterministic enumeration of
/// admissible switching sequences, each step verified by the preventive study's AC verifier
/// (<see cref="RestorationStudy"/>). Its own verb, separate from <c>restore</c> (which is unchanged) and from
/// <c>reliability-study</c> (a different schema and verdict). The strict v1 JSON, case identity binding and
/// no-overwrite result writing mirror the preventive command, which stays untouched.
/// </summary>
internal static class RestorationStudyCommand
{
    private const string Verb = "restoration-study";
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
    private sealed record SearchInput
    {
        public long? MaxNodes { get; init; }
    }
    private sealed record Request
    {
        public required int SchemaVersion { get; init; }
        public required CaseInput Case { get; init; }
        public required VoltageInput Voltage { get; init; }
        public required RestorationAction[] Actions { get; init; }
        public PrecedenceConstraint[]? Precedence { get; init; }
        public SearchInput? Search { get; init; }
    }
    private sealed record InputFile(string Path, string Sha256);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private static readonly JsonSerializerOptions OutputJson = CreateOutputOptions();
    private static readonly JsonSerializerOptions ModelJson = new(Json)
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private static JsonSerializerOptions CreateOutputOptions()
    {
        var options = new JsonSerializerOptions(Json);
        options.Converters.Add(new FiniteDoubleWriter());
        return options;
    }

    public static int Run(List<string> arguments, string root, Func<string, GridModel> load)
    {
        if (arguments.Count is not (2 or 4) || arguments[1].StartsWith('-') ||
            (arguments.Count == 4 && (arguments[2] != "--out" || arguments[3].StartsWith('-'))))
        {
            Console.Error.WriteLine($"usage: gridsim {Verb} <study.json> [--out <result.json>]");
            return CliExit.Usage;
        }
        string? temporary = null;
        try
        {
            string input = System.IO.Path.GetFullPath(arguments[1]);
            string? output = arguments.Count == 4 ? System.IO.Path.GetFullPath(arguments[3]) : null;
            if (output is not null && (File.Exists(output) || Directory.Exists(output)))
                throw new IOException("Result path already exists; use a new path for this invocation.");
            byte[] bytes = File.ReadAllBytes(input);
            using (JsonDocument document = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(bytes))) CheckDuplicates(document.RootElement);
            Request request = JsonSerializer.Deserialize<Request>(GridSim.Core.IO.Utf8Json.WithoutBom(bytes).Span, Json)
                ?? throw new InvalidDataException("Study is null.");
            if (request.SchemaVersion != 1 || request.Case is null || request.Voltage is null || request.Actions is null ||
                string.IsNullOrWhiteSpace(request.Case.Sha256))
                throw new InvalidDataException("Schema version 1 and all required objects are required.");
            if (request.Actions.Any(a => a is null || a.Branch is null) || (request.Precedence?.Any(p => p is null) ?? false))
                throw new InvalidDataException("Action and precedence rows cannot be null.");
            if ((request.Case.Path is null) == (request.Case.BuiltIn is null))
                throw new InvalidDataException("Case must specify exactly one path or builtIn.");

            var files = new List<InputFile>();
            GridModel model;
            string identity;
            string loadPolicy = "Incumbent built-in loader";
            if (request.Case.Path is string relative)
            {
                string path = System.IO.Path.GetFullPath(relative, System.IO.Path.GetDirectoryName(input)!);
                bool native = Directory.Exists(path) || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
                files = Inputs(path, root);
                loadPolicy = native ? "Native dispatch suppressed" : "Incumbent file loader";
                identity = native
                    ? Hash(Encoding.UTF8.GetBytes(string.Concat(files.Select(f => f.Path + "\t" + f.Sha256 + "\n"))))
                    : files.Single().Sha256;
                RequireIdentity(request.Case.Sha256, identity);
                model = native ? Gated.TransmissionNative(path, root) : load(path);
                if (!files.SequenceEqual(Inputs(path, root)))
                    throw new IOException("Case inputs changed during loading.");
            }
            else
            {
                string name = request.Case.BuiltIn!;
                if (!BuiltInCases.Names.Contains(name.ToLowerInvariant()))
                    throw new UnknownCaseException(name);
                model = load(name);
                identity = Hash(Encoding.UTF8.GetBytes(GridSim.Core.BuildInfo.InformationalVersion + "\n" +
                    JsonSerializer.Serialize(model, ModelJson)));
                RequireIdentity(request.Case.Sha256, identity);
            }
            var resolved = new RestorationRequest(request.Actions, request.Voltage.MinPu, request.Voltage.MaxPu,
                request.Precedence ?? [], request.Search?.MaxNodes ?? RestorationStudy.DefaultMaxNodes);
            RestorationStudyResult result = RestorationStudy.Run(model, resolved);
            string json = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                study = Verb,
                provenance = new { studySha256 = Hash(bytes), caseSha256 = identity, inputs = files,
                    loadPolicy, engine = GridSim.Core.BuildInfo.InformationalVersion },
                effectiveOptions = resolved,
                stepOrder = RestorationStudy.StepOrder,
                inputBranches = resolved.Actions.Select(a =>
                {
                    Branch b = model.Branches[a.Branch.Index];
                    return new { action = a.Id, index = a.Branch.Index, b.From, b.To, b.Name, storedInService = b.InService, b.RateAMva };
                }),
                result
            }, OutputJson);
            if (output is not null)
            {
                temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, json + "\n", new UTF8Encoding(false));
                File.Move(temporary, output, overwrite: false);
                temporary = null;
            }
            Console.WriteLine(json);
            return result.Status == "OPTIMAL" ? CliExit.Ok : CliExit.Verdict;
        }
        catch (GateRefusedException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            Console.Error.WriteLine(Verb + ": " + CliExit.UserMessage(ex));
            return CliExit.CaseLoadFailure(ex);
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
        }
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

    // Case identity: the same manifest rules as the preventive study (see TransmissionSecurityCommand.Inputs).
    private static List<InputFile> Inputs(string path, string root) =>
        CaseIdentity.Inputs(path, root, (label, sha) => new InputFile(label, sha));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void RequireIdentity(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Case SHA256 mismatch. Resolved identity: " + actual);
    }
}
