using System.Text.Json;
using System.Text.RegularExpressions;
using GridSim.Core.Analysis.PlanningStudies;

namespace GridSim.Core.Analysis.StudyCases;

// RC4 R4-01: the study-case documents (gridsim-groups/1, gridsim-studycase/1) are strict JSON like the RC3 planning
// manifest: unknown and duplicated members, wrong types and non-finite numbers are refused, with the path named.

/// <summary>Versions of the R4-01 documents.</summary>
public static class StudyCaseVersions
{
    public const string Groups = "gridsim-groups/1";
    public const string StudyCase = "gridsim-studycase/1";
    public const string StudyCaseResult = "gridsim-studycase-result/1";
    /// <summary>The reserved scenario name of "no scenario operations": a study that names no scenario runs on it.</summary>
    public const string BaseScenario = "base";
}

/// <summary>An invalid study case, groups document or request, refused before any job runs (an input error at the CLI).
/// Derived from <see cref="PlanningManifestException"/> so that every RC3 refusal path treats it the same way.</summary>
public class StudyCaseException(string message) : PlanningManifestException(message);

/// <summary>Strict JSON reading helpers shared by the groups and study-case parsers.</summary>
public static partial class StudyCaseJson
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    public static void Obj(JsonElement e, string at, string[] allowed, string[] required)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new StudyCaseException(at + " must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty p in e.EnumerateObject())
        {
            if (!seen.Add(p.Name)) throw new StudyCaseException($"{at}: duplicate property \"{p.Name}\".");
            if (!allowed.Contains(p.Name)) throw new StudyCaseException($"{at}: unknown property \"{p.Name}\".");
        }
        foreach (string r in required)
            if (!seen.Contains(r)) throw new StudyCaseException($"{at}: required property \"{r}\" is missing.");
    }

    public static IReadOnlyList<(JsonElement Item, int Index)> Arr(JsonElement e, string at) =>
        e.ValueKind == JsonValueKind.Array
            ? e.EnumerateArray().Select((x, i) => (x, i)).ToArray()
            : throw new StudyCaseException(at + " must be an array.");

    public static string Str(JsonElement e, string at) => e.ValueKind == JsonValueKind.String
        ? e.GetString()! : throw new StudyCaseException(at + " must be a string.");

    public static string Id(JsonElement e, string at)
    {
        string id = Str(e, at);
        return IdPattern().IsMatch(id) ? id
            : throw new StudyCaseException($"{at} \"{id}\" must be 1-64 characters of letters, digits, '.', '_' or '-', starting with a letter or digit.");
    }

    public static bool Bool(JsonElement e, string at) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new StudyCaseException(at + " must be true or false.")
    };

    public static int Int(JsonElement e, string at) => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int v)
        ? v : throw new StudyCaseException(at + " must be an integer.");

    public static double Num(JsonElement e, string at) =>
        e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out double v) && double.IsFinite(v)
            ? v : throw new StudyCaseException(at + " must be a finite number.");

    /// <summary>Parses a document, turning malformed JSON into a <see cref="StudyCaseException"/> that names the document.</summary>
    public static JsonDocument Parse(byte[] bytes, string what)
    {
        try { return JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(bytes)); }
        catch (JsonException ex) { throw new StudyCaseException($"{what} is not valid JSON: {ex.Message}"); }
    }
}
