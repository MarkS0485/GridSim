using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Analysis;
using GridSim.Core.Analysis.TransmissionSecurity;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Cli;

internal static class TransmissionSecurityCommand
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
    private sealed record DcInput { public required int MaxRounds { get; init; } }
    private sealed record CostInput { public required double[] PerMwh { get; init; } }
    private sealed record Request
    {
        public required int SchemaVersion { get; init; }
        public required CaseInput Case { get; init; }
        public required BranchOutage[] Outages { get; init; }
        public required VoltageInput Voltage { get; init; }
        public required string PostFaultRating { get; init; }
        public DcInput? Dc { get; init; }
        public CostInput? Costs { get; init; }
    }
    internal sealed record InputFile(string Path, string Sha256);
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static readonly JsonSerializerOptions OutputJson = CreateOutputOptions();
    internal static readonly JsonSerializerOptions ModelJson = new(Json)
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
            Console.Error.WriteLine("usage: gridsim transmission-security <study.json> [--out <result.json>]");
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
            using JsonDocument document = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(bytes));
            CheckDuplicates(document.RootElement);
            CheckShape(document.RootElement);
            Request request = JsonSerializer.Deserialize<Request>(GridSim.Core.IO.Utf8Json.WithoutBom(bytes).Span, Json)
                ?? throw new InvalidDataException("Study is null.");
            if (request.SchemaVersion != 1 || request.Case is null || request.Voltage is null ||
                request.Outages is null || string.IsNullOrWhiteSpace(request.Case.Sha256))
                throw new InvalidDataException("Schema version 1 and all required objects are required.");
            if (request.Outages.Any(o => o is null))
                throw new InvalidDataException("Outage rows cannot be null.");
            if ((request.Case.Path is null) == (request.Case.BuiltIn is null))
                throw new InvalidDataException("Case must specify exactly one path or builtIn.");
            PostFaultRating rating = request.PostFaultRating switch
            {
                "A" => PostFaultRating.RateA, "B" => PostFaultRating.RateB, "C" => PostFaultRating.RateC,
                _ => throw new InvalidDataException("postFaultRating must be A, B or C.")
            };
            var files = new List<InputFile>();
            GridModel model;
            string identity;
            string loadPolicy = "Incumbent built-in loader";
            if (request.Case.Path is string relative)
            {
                string path = System.IO.Path.GetFullPath(relative, System.IO.Path.GetDirectoryName(input)!);
                files = Inputs(path, root);
                loadPolicy = Directory.Exists(path) || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? "Native dispatch suppressed" : "Incumbent file loader";
                identity = Directory.Exists(path) || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? Hash(Encoding.UTF8.GetBytes(string.Concat(files.Select(f => f.Path + "	" + f.Sha256 + "\n"))))
                    : files.Single().Sha256;
                RequireIdentity(request.Case.Sha256, identity);
                model = Directory.Exists(path) || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? Gated.TransmissionNative(path, root) : load(path);
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
            var resolved = new TransmissionSecurityRequest(request.Outages, request.Voltage.MinPu,
                request.Voltage.MaxPu, rating, request.Dc?.MaxRounds ?? new ScopfOptions().MaxRounds,
                request.Costs?.PerMwh);
            TransmissionSecurityResult result = TransmissionSecurityRunner.Run(model, resolved);
            string json = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                provenance = new { studySha256 = Hash(bytes), caseSha256 = identity, inputs = files,
                    loadPolicy, engine = GridSim.Core.BuildInfo.InformationalVersion },
                effectiveOptions = resolved,
                inputGenerators = model.Generators.Select((g, i) => new { index = i, g.Bus, g.Pg, g.Qg }),
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
            return result.Status == "SECURE" ? CliExit.Ok : CliExit.Verdict;
        }
        catch (GateRefusedException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            Console.Error.WriteLine("transmission-security: " + CliExit.UserMessage(ex));
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


    private static void CheckShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("outages", out JsonElement outages) ||
            outages.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Study must contain an outages array.");
        foreach (JsonElement outage in outages.EnumerateArray())
        {
            if (outage.ValueKind != JsonValueKind.Object ||
                !outage.TryGetProperty("index", out JsonElement index) || !index.TryGetInt32(out _) ||
                !outage.TryGetProperty("from", out JsonElement from) || !from.TryGetInt32(out _) ||
                !outage.TryGetProperty("to", out JsonElement to) || !to.TryGetInt32(out _))
                throw new InvalidDataException("Every outage needs integer index, from and to identities.");
        }
    }

    internal static void CheckDuplicates(JsonElement element)
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

    internal static List<InputFile> Inputs(string path, string root) =>
        CaseIdentity.Inputs(path, root, (label, sha) => new InputFile(label, sha));
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static void RequireIdentity(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Case SHA256 mismatch. Resolved identity: " + actual);
    }
}

