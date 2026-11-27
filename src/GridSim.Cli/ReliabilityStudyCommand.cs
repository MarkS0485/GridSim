using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Analysis.Reliability;

namespace GridSim.Cli;

/// <summary>
/// RC3 P8: <c>gridsim reliability-study &lt;study.json&gt; [--out &lt;result.json&gt;]</c>. Analytical radial-feeder
/// reliability indices (<see cref="ReliabilityStudy"/>) from explicitly stated failure rates, repair and switching
/// times, customers, average load and dependency assumptions. It reads no case: the feeder and its data are the request.
/// A separate verb from <c>restoration-study</c> because the schemas, verdicts and evidence have nothing in common.
/// </summary>
internal static class ReliabilityStudyCommand
{
    private const string Verb = "reliability-study";
    private sealed record Request
    {
        public required int SchemaVersion { get; init; }
        public required ReliabilityAssumptions Assumptions { get; init; }
        public double? SwitchingHours { get; init; }
        public required ReliabilityComponent[] Components { get; init; }
        public required ReliabilityLoadPoint[] LoadPoints { get; init; }
    }
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
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
            if (request.SchemaVersion != 1 || request.Assumptions is null || request.Components is null || request.LoadPoints is null)
                throw new InvalidDataException("Schema version 1 and all required objects are required.");
            var resolved = new ReliabilityRequest(request.Components, request.LoadPoints, request.Assumptions, request.SwitchingHours);
            ReliabilityStudyResult result = ReliabilityStudy.Run(resolved);
            string json = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                study = Verb,
                provenance = new { studySha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    engine = GridSim.Core.BuildInfo.InformationalVersion },
                effectiveOptions = resolved,
                stepOrder = ReliabilityStudy.StepOrder,
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
            return result.Status == "COMPLETE" ? CliExit.Ok : CliExit.Verdict;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            Console.Error.WriteLine(Verb + ": " + CliExit.UserMessage(ex));
            return CliExit.InputError;
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
}
