using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GridSim.Cli;

/// <summary>
/// RC3 P8: the shared plumbing of the specialist study verbs (<c>emt-line</c>, <c>arc-flash</c>, <c>cable-rating</c>):
/// <c>gridsim &lt;verb&gt; &lt;study.json&gt; [--out &lt;result.json&gt;]</c>, strict v1 JSON (unknown, missing, null-for-required
/// and duplicated properties refused), the study file's SHA-256 in the result, and result writing through a temporary
/// file and a no-overwrite move. Mirrors the transmission-security pattern; the existing commands are untouched.
/// </summary>
internal static class SpecialistStudyCli
{
    /// <summary>Strict input options.</summary>
    public static readonly JsonSerializerOptions InputJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    /// <summary>Output options: indented, camel case, a non-finite double written as null.</summary>
    public static readonly JsonSerializerOptions OutputJson = CreateOutput();

    private static JsonSerializerOptions CreateOutput()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(allowIntegerValues: false), new FiniteDoubleWriter() }
        };
        return o;
    }

    /// <summary>The model serialisation used for a built-in case identity (as the other study verbs).</summary>
    public static readonly JsonSerializerOptions ModelJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    /// <summary>A parsed invocation: the study bytes, the full input path and the optional output path.</summary>
    public sealed record Invocation(string InputPath, byte[] Bytes, string? OutputPath);

    /// <summary>
    /// Run a study verb. <paramref name="body"/> receives the invocation and returns the result JSON object and the exit
    /// code. Usage errors exit 2; malformed or refused input exits 1 (via <see cref="CliExit.CaseLoadFailure"/>).
    /// </summary>
    public static int Run(string verb, List<string> arguments, Func<Invocation, (object Json, int Exit)> body)
    {
        if (arguments.Count is not (2 or 4) || arguments[1].StartsWith('-') ||
            (arguments.Count == 4 && (arguments[2] != "--out" || arguments[3].StartsWith('-'))))
        {
            Console.Error.WriteLine($"usage: gridsim {verb} <study.json> [--out <result.json>]");
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
            using (JsonDocument document = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(bytes))) CheckDuplicates(document.RootElement);
            (object result, int exit) = body(new Invocation(input, bytes, output));
            string json = JsonSerializer.Serialize(result, OutputJson);
            if (output is not null)
            {
                temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, json + "\n", new UTF8Encoding(false));
                File.Move(temporary, output, overwrite: false);
                temporary = null;
            }
            Console.WriteLine(json);
            return exit;
        }
        catch (GateRefusedException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or NotSupportedException)
        {
            Console.Error.WriteLine(verb + ": " + CliExit.UserMessage(ex));
            return CliExit.CaseLoadFailure(ex);
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Deserialise the strict request and check its schema version.</summary>
    public static T Parse<T>(byte[] bytes, Func<T, int> schemaVersion) where T : class
    {
        T request = JsonSerializer.Deserialize<T>(GridSim.Core.IO.Utf8Json.WithoutBom(bytes).Span, InputJson) ?? throw new InvalidDataException("Study is null.");
        if (schemaVersion(request) != 1) throw new InvalidDataException("schemaVersion must be 1.");
        return request;
    }

    /// <summary>Lower-case hex SHA-256.</summary>
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Refuse a duplicated property anywhere in the document.</summary>
    public static void CheckDuplicates(JsonElement element)
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
