using System.Text;
using System.Text.Json;
using Xunit;

namespace GridSim.Tests.V2;

/// <summary>
/// Reads the v2 per-kind parameter contracts (<c>schemas/v2/kinds/*.kind.json</c>, schema <c>gridsim-kind/2</c>)
/// straight from the repository with System.Text.Json, and checks their shape.
///
/// <para><b>Why the tests read the files themselves.</b> The hand-rolled schema validator (Phase 1 slice 1) and the
/// common and library schemas (slice 2) are built in parallel with these kind files (slice 3). These tests must not
/// depend on that validator, so the format below is checked here, independently and strictly. If the two ever
/// disagree, both are wrong until reconciled: the format is fixed by the plan, and unknown members are errors.</para>
///
/// <para><b>The fixed format.</b> Top level: exactly <c>schema</c> ("gridsim-kind/2"), <c>kind</c> (equal to the file
/// name), <c>title</c>, <c>description</c> and <c>params</c>. Each parameter: <c>type</c> (number, integer, enum, string
/// or boolean), <c>unit</c> (required for number and integer, forbidden otherwise, from the closed vocabulary),
/// optional <c>min</c> and <c>max</c> (number and integer only), <c>values</c> (required for enum, forbidden
/// otherwise), <c>required</c>, optional <c>markGated</c>, <c>description</c> and <c>codeDefaults</c> (an array, which
/// may be empty when today's code holds no value for the parameter). Each code default: exactly <c>consumer</c>,
/// <c>ref</c> and <c>value</c>.</para>
/// </summary>
internal static class KindFileSet
{
    /// <summary>
    /// The fifteen parameter kinds: each declares typed parameters with units and code defaults, checked against the
    /// code. The <c>site</c> kind is a slot-only kind (two template-reference slots, no code defaults) and is listed in
    /// <see cref="SlotOnlyKinds"/>. Renamed 2026-10 (DOC-17 D-DOC-222): was <c>Slice3Kinds</c>, after the work slice
    /// that wrote these files, a name that told a reader nothing; same fifteen kinds, same order.
    /// </summary>
    internal static readonly string[] ParameterKinds =
    {
        "synchronousMachine", "exciter", "pss", "governor", "gridFollowingInverter", "gridFormingInverter",
        "lineType", "transformerType", "oltc", "protection", "storage", "harmonicSpectrum", "load",
        "gridCodeProfile", "solverProfile",
    };

    /// <summary>
    /// The slot-only kinds (today only <c>site</c>). Renamed 2026-10 (DOC-17 D-DOC-222): was <c>OtherSliceKinds</c>.
    /// They are not in <see cref="KindNames"/> (the code-default and coverage tests do not apply to them), but since
    /// DOC-17 the format and ASCII checks run on them too (<see cref="AllKindNames"/>), so the per-kind tests no longer
    /// skip <c>site</c>.
    /// </summary>
    internal static readonly string[] SlotOnlyKinds = { "site" };

    /// <summary>The closed unit vocabulary of the v2 schema (plan, deliverable 1, <c>common.schema.json</c>).
    /// Matched exactly and case-sensitively: "hz" is not "Hz".</summary>
    internal static readonly string[] UnitVocabulary =
    {
        "1", "pu@system", "pu@machine", "pu@transformer", "s", "Hz", "Hz/s", "MW", "MVA", "Mvar", "kV", "km",
        "ohm/km", "uS/km", "%", "MWh", "MW/min", "MW/Hz/MW", "degC", "deg",
        // Moved 2026-10 (Q24, authorised): five units appended so the seven v1 asset-class keys that had no unit
        // (the old UnitGapRegister) can be declared with their true unit instead of being left out. Before: the
        // twenty units above. After: those twenty plus these five, in this order.
        "MW.s/Hz", "MW/Hz", "MW/s/MW", "V", "min",
    };

    internal static readonly string[] ParamTypes = { "number", "integer", "enum", "string", "boolean" };

    private static readonly string[] TopLevelMembers = { "schema", "kind", "title", "description", "params" };

    private static readonly string[] ParamMembers =
        { "type", "unit", "min", "max", "values", "required", "markGated", "description", "codeDefaults" };

    private static readonly string[] CodeDefaultMembers = { "consumer", "ref", "value" };

    /// <summary>The prefixes of the four reference forms (see <see cref="CodeDefaultRefResolver"/>).</summary>
    internal static readonly string[] RefForms = { "member:", "instance:", "param:", "source:" };

    /// <summary>Lenient on comments and trailing commas only, exactly as the v1 JSON loader is; strict on content.</summary>
    internal static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The repository root (the folder holding GridSim.slnx).</summary>
    internal static string RepoRoot() => GridSim.Tests.PresentationConstantsAntiDriftTests.RepoRoot();

    /// <summary>The folder holding the kind files.</summary>
    internal static string KindsDirectory() => Path.Combine(RepoRoot(), "schemas", "v2", "kinds");

    /// <summary>The path of one kind file.</summary>
    internal static string PathOf(string kind) => Path.Combine(KindsDirectory(), kind + ".kind.json");

    /// <summary>Parse one kind file. The returned element is a detached clone, so no document is left undisposed.</summary>
    internal static JsonElement Load(string kind)
    {
        string path = PathOf(kind);
        if (!File.Exists(path)) throw new FileNotFoundException($"kind file missing: {path}");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8), ReadOptions);
        return doc.RootElement.Clone();
    }

    /// <summary>All fifteen parameter-kind files, keyed by kind name.</summary>
    internal static IReadOnlyDictionary<string, JsonElement> LoadAll()
        => ParameterKinds.ToDictionary(k => k, Load, StringComparer.Ordinal);

    /// <summary>The parameter names one kind declares.</summary>
    internal static IReadOnlyList<string> ParamNames(JsonElement kindRoot)
        => kindRoot.GetProperty("params").EnumerateObject().Select(p => p.Name).ToList();

    /// <summary>xUnit theory data: one row per parameter kind.</summary>
    public static TheoryData<string> KindNames()
    {
        var data = new TheoryData<string>();
        foreach (string k in ParameterKinds) data.Add(k);
        return data;
    }

    /// <summary>
    /// xUnit theory data: one row per kind file of either sort (the parameter kinds, then the slot-only kinds). Added
    /// 2026-10 (DOC-17 D-DOC-222) so the per-kind format and ASCII checks cover <c>site</c> as well.
    /// </summary>
    public static TheoryData<string> AllKindNames()
    {
        var data = new TheoryData<string>();
        foreach (string k in ParameterKinds.Concat(SlotOnlyKinds)) data.Add(k);
        return data;
    }

    // ------------------------------------------------------------------------------------------ shape checker

    /// <summary>
    /// Every way <paramref name="root"/> departs from the fixed kind-file format, as plain-English messages with a
    /// JSON-pointer-like path. An empty list means the document has exactly the fixed shape. Never throws on bad
    /// input: a malformed document must be REPORTED, not crash the checker.
    /// </summary>
    internal static IReadOnlyList<string> ShapeErrors(JsonElement root, string expectedKind)
    {
        var errs = new List<string>();
        if (root.ValueKind != JsonValueKind.Object) { errs.Add("/: the document must be a JSON object"); return errs; }

        CheckMembers(root, "", TopLevelMembers, TopLevelMembers, errs);

        if (root.TryGetProperty("schema", out JsonElement schema)
            && !(schema.ValueKind == JsonValueKind.String && schema.GetString() == "gridsim-kind/2"))
            errs.Add("/schema: must be exactly \"gridsim-kind/2\"");
        if (root.TryGetProperty("kind", out JsonElement kind)
            && !(kind.ValueKind == JsonValueKind.String && kind.GetString() == expectedKind))
            errs.Add($"/kind: must equal the file name \"{expectedKind}\"");
        foreach (string text in new[] { "title", "description" })
            if (root.TryGetProperty(text, out JsonElement t) && !IsNonEmptyString(t))
                errs.Add($"/{text}: must be a non-empty string");

        if (!root.TryGetProperty("params", out JsonElement ps)) return errs;
        if (ps.ValueKind != JsonValueKind.Object) { errs.Add("/params: must be an object"); return errs; }
        if (!ps.EnumerateObject().Any()) errs.Add("/params: must declare at least one parameter");
        DuplicateNames(ps, "/params", errs);
        foreach (JsonProperty p in ps.EnumerateObject())
            ParamErrors(p.Value, "/params/" + p.Name, errs);
        return errs;
    }

    private static void ParamErrors(JsonElement p, string at, List<string> errs)
    {
        if (p.ValueKind != JsonValueKind.Object) { errs.Add($"{at}: a parameter must be an object"); return; }
        CheckMembers(p, at, ParamMembers, new[] { "type", "required", "description", "codeDefaults" }, errs);

        string? type = p.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (p.TryGetProperty("type", out _) && (type is null || !ParamTypes.Contains(type, StringComparer.Ordinal)))
            errs.Add($"{at}/type: must be one of {string.Join(", ", ParamTypes)}");
        bool numeric = type is "number" or "integer";

        // unit: required for number/integer, forbidden otherwise, and only from the closed vocabulary.
        bool hasUnit = p.TryGetProperty("unit", out JsonElement unit);
        if (numeric && !hasUnit) errs.Add($"{at}/unit: a {type} parameter must declare a unit");
        if (!numeric && type is not null && hasUnit) errs.Add($"{at}/unit: only number and integer parameters carry a unit");
        if (hasUnit && !(unit.ValueKind == JsonValueKind.String && UnitVocabulary.Contains(unit.GetString(), StringComparer.Ordinal)))
            errs.Add($"{at}/unit: \"{unit}\" is not in the closed unit vocabulary");

        // min / max: numbers, numeric parameters only, min <= max.
        double? min = NumberMember(p, "min", at, numeric, type, errs);
        double? max = NumberMember(p, "max", at, numeric, type, errs);
        if (min is double lo && max is double hi && lo > hi) errs.Add($"{at}: min {lo} exceeds max {hi}");

        // values: required for enum (non-empty, distinct strings), forbidden otherwise.
        bool hasValues = p.TryGetProperty("values", out JsonElement values);
        if (type == "enum" && !hasValues) errs.Add($"{at}/values: an enum parameter must list its values");
        if (type != "enum" && type is not null && hasValues) errs.Add($"{at}/values: only enum parameters carry values");
        var enumValues = new List<string>();
        if (hasValues)
        {
            if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() == 0)
                errs.Add($"{at}/values: must be a non-empty array");
            else
                foreach (JsonElement v in values.EnumerateArray())
                {
                    if (!IsNonEmptyString(v)) { errs.Add($"{at}/values: every value must be a non-empty string"); continue; }
                    if (enumValues.Contains(v.GetString()!, StringComparer.Ordinal)) errs.Add($"{at}/values: duplicate value \"{v.GetString()}\"");
                    enumValues.Add(v.GetString()!);
                }
        }

        foreach (string flag in new[] { "required", "markGated" })
            if (p.TryGetProperty(flag, out JsonElement f) && f.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                errs.Add($"{at}/{flag}: must be true or false");
        if (p.TryGetProperty("description", out JsonElement d) && !IsNonEmptyString(d))
            errs.Add($"{at}/description: must be a non-empty string");

        if (!p.TryGetProperty("codeDefaults", out JsonElement cds)) return;
        if (cds.ValueKind != JsonValueKind.Array) { errs.Add($"{at}/codeDefaults: must be an array"); return; }
        var seenRefs = new HashSet<string>(StringComparer.Ordinal);
        int i = 0;
        foreach (JsonElement c in cds.EnumerateArray())
        {
            string cat = $"{at}/codeDefaults/{i++}";
            if (c.ValueKind != JsonValueKind.Object) { errs.Add($"{cat}: must be an object"); continue; }
            CheckMembers(c, cat, CodeDefaultMembers, CodeDefaultMembers, errs);
            if (c.TryGetProperty("consumer", out JsonElement consumer) && !IsNonEmptyString(consumer))
                errs.Add($"{cat}/consumer: must be a non-empty string");
            if (c.TryGetProperty("ref", out JsonElement r))
            {
                if (!IsNonEmptyString(r) || !RefForms.Any(f => r.GetString()!.StartsWith(f, StringComparison.Ordinal)))
                    errs.Add($"{cat}/ref: must start with one of {string.Join(" ", RefForms)}");
                else if (!seenRefs.Add(r.GetString()!))
                    errs.Add($"{cat}/ref: the same reference appears twice on one parameter (one entry per consumer, never repeated)");
            }
            if (c.TryGetProperty("value", out JsonElement v) && type is not null)
                ValueErrors(v, type, enumValues, min, max, cat + "/value", errs);
        }
    }

    /// <summary>A code default's value must have the parameter's own type. A number parameter may also take the
    /// strings "+Infinity" / "-Infinity", because several C# fallbacks are <c>double.PositiveInfinity</c> ("never
    /// trips") and JSON has no infinite number. Finite numeric values must lie inside the declared range.</summary>
    private static void ValueErrors(JsonElement v, string type, List<string> enumValues, double? min, double? max,
        string at, List<string> errs)
    {
        switch (type)
        {
            case "number":
                if (v.ValueKind == JsonValueKind.String && v.GetString() is "+Infinity" or "-Infinity") return;
                if (v.ValueKind != JsonValueKind.Number) { errs.Add($"{at}: must be a number (or \"+Infinity\" / \"-Infinity\")"); return; }
                break;
            case "integer":
                if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out _)) { errs.Add($"{at}: must be an integer"); return; }
                break;
            case "enum":
                if (!(v.ValueKind == JsonValueKind.String && enumValues.Contains(v.GetString()!, StringComparer.Ordinal)))
                    errs.Add($"{at}: must be one of the enum values");
                return;
            case "string":
                if (v.ValueKind != JsonValueKind.String) errs.Add($"{at}: must be a string");
                return;
            case "boolean":
                if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) errs.Add($"{at}: must be true or false");
                return;
        }
        double x = v.GetDouble();
        if (min is double lo && x < lo) errs.Add($"{at}: {x} is below the declared min {lo}");
        if (max is double hi && x > hi) errs.Add($"{at}: {x} is above the declared max {hi}");
    }

    private static double? NumberMember(JsonElement p, string name, string at, bool numeric, string? type, List<string> errs)
    {
        if (!p.TryGetProperty(name, out JsonElement e)) return null;
        if (!numeric && type is not null) { errs.Add($"{at}/{name}: only number and integer parameters carry {name}"); return null; }
        if (e.ValueKind != JsonValueKind.Number) { errs.Add($"{at}/{name}: must be a number"); return null; }
        return e.GetDouble();
    }

    /// <summary>Unknown members, missing required members and duplicate member names on one object.</summary>
    private static void CheckMembers(JsonElement obj, string at, string[] allowed, string[] required, List<string> errs)
    {
        DuplicateNames(obj, at.Length == 0 ? "/" : at, errs);
        foreach (JsonProperty m in obj.EnumerateObject())
            if (!allowed.Contains(m.Name, StringComparer.Ordinal))
                errs.Add($"{at}/{m.Name}: unknown member (allowed: {string.Join(", ", allowed)})");
        foreach (string r in required)
            if (!obj.TryGetProperty(r, out _))
                errs.Add($"{at}/{r}: required member is missing");
    }

    /// <summary>System.Text.Json keeps every duplicate name (and a lookup silently takes the last), so a repeated key
    /// would otherwise pass unseen.</summary>
    private static void DuplicateNames(JsonElement obj, string at, List<string> errs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty m in obj.EnumerateObject())
            if (!seen.Add(m.Name)) errs.Add($"{at}: member \"{m.Name}\" appears more than once");
    }

    private static bool IsNonEmptyString(JsonElement e)
        => e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString());
}
