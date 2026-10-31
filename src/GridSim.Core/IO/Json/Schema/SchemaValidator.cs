using System.Globalization;
using System.Text.Json;

namespace GridSim.Core.IO.Json.Schema;

/// <summary>
/// One validation problem: the JSON pointer (RFC 6901) of the offending value inside the document, and a
/// plain-English message. <paramref name="File"/> is set when the error came from a check that spans several
/// documents (for example a controller reference into another library file), and left null otherwise.
/// </summary>
public sealed record SchemaError(string Pointer, string Message, string? File = null)
{
    /// <summary>"file: /pointer: message", with "(root)" standing in for the empty root pointer.</summary>
    public override string ToString()
        => (File is null ? "" : File + ": ") + JsonSchemaSubset.Show(Pointer) + ": " + Message;
}

/// <summary>
/// Validates a JSON value against a schema in a loaded <see cref="JsonSchemaSubset"/>. It never stops at the first
/// problem: every error is collected with the JSON pointer of the value it concerns, so an author fixing a file sees
/// the whole list in one go. The caller fails loud on any error; <see cref="ValidateOrThrow"/> does that for you.
///
/// Semantics follow JSON Schema 2020-12 for the supported keywords, with these points worth stating plainly:
/// <list type="bullet">
/// <item><c>integer</c> accepts any number with no fractional part (so <c>1.0</c> is an integer), as the spec says;</item>
/// <item><c>additionalProperties</c> sees only the <c>properties</c> written in the same schema object, never those
/// of a sibling <c>$ref</c> or <c>if</c>/<c>then</c> (again as the spec says);</item>
/// <item><c>minimum</c>, <c>maximum</c>, <c>exclusiveMinimum</c> apply only to numbers, <c>minItems</c> only to
/// arrays, <c>pattern</c> only to strings, and <c>properties</c>/<c>required</c>/<c>additionalProperties</c> only to
/// objects: a value of another type is left to the <c>type</c> keyword to judge;</item>
/// <item><c>enum</c> and <c>const</c> compare with <see cref="JsonElement.DeepEquals"/> (numbers by value).</item>
/// </list>
/// Termination is guaranteed by the load-time cycle check in <see cref="JsonSchemaSubset"/>: any recursion that
/// does not step into a child value is acyclic, and stepping into children is bounded by the document's depth.
/// </summary>
public sealed class SchemaValidator
{
    private readonly JsonSchemaSubset _set;

    public SchemaValidator(JsonSchemaSubset set)
    {
        ArgumentNullException.ThrowIfNull(set);
        _set = set;
    }

    /// <summary>The schema set this validator checks against.</summary>
    public JsonSchemaSubset Schemas => _set;

    /// <summary>Validate <paramref name="instance"/> against the root schema of <paramref name="schemaFile"/>; every error, in document order.</summary>
    public IReadOnlyList<SchemaError> Validate(JsonElement instance, string schemaFile)
    {
        var errors = new List<SchemaError>();
        Apply(_set.RootOf(schemaFile), instance, "", errors);
        return errors;
    }

    /// <summary>
    /// Validate <paramref name="instance"/> against the schema at <paramref name="pointer"/> inside
    /// <paramref name="schemaFile"/>, for example one generator record against <c>#/$defs/generator</c> of
    /// generation.schema.json (the case editor checks a candidate record before it applies an edit). Error pointers
    /// are relative to the instance. Throws when the file or pointer names no schema. Additive (slice E2 of the
    /// SchemaEditor plan): the other overloads are unchanged.
    /// </summary>
    public IReadOnlyList<SchemaError> Validate(JsonElement instance, string schemaFile, string pointer)
    {
        var errors = new List<SchemaError>();
        Apply(_set.NodeAt(schemaFile, pointer), instance, "", errors);
        return errors;
    }

    /// <summary>Validate a whole document against the root schema of <paramref name="schemaFile"/>.</summary>
    public IReadOnlyList<SchemaError> Validate(JsonDocument document, string schemaFile)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Validate(document.RootElement, schemaFile);
    }

    /// <summary>
    /// Validate and fail loud: throws an <see cref="InvalidDataException"/> naming <paramref name="documentName"/>
    /// (normally the file name) and listing every error, the same "file: message" shape <c>JsonGridLoader</c> uses
    /// for a v1 case file so an author can find the problem.
    /// </summary>
    public void ValidateOrThrow(JsonElement instance, string schemaFile, string documentName)
        => ThrowIfAny(Validate(instance, schemaFile), documentName);

    /// <summary>Throw an <see cref="InvalidDataException"/> naming the document and listing every error, if there are any.</summary>
    public static void ThrowIfAny(IReadOnlyList<SchemaError> errors, string documentName)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (errors.Count == 0) return;
        throw new InvalidDataException($"{documentName}: {errors.Count} validation error(s):" + Environment.NewLine + "  " +
            string.Join(Environment.NewLine + "  ", errors.Select(e => e.ToString())));
    }

    // ---------------------------------------------------------------------------------------------------------------

    private void Apply(SchemaNode node, JsonElement inst, string path, List<SchemaError> errors)
    {
        JsonElement s = node.Element;

        if (s.TryGetProperty("$ref", out _))
            Apply(_set.RefTarget(node), inst, path, errors);

        if (s.TryGetProperty("type", out JsonElement type) && !TypeMatches(type, inst))
            errors.Add(new SchemaError(path, $"expected type {Describe(type)}, found {KindOf(inst)}."));

        if (s.TryGetProperty("enum", out JsonElement en) && !en.EnumerateArray().Any(e => JsonElement.DeepEquals(e, inst)))
            errors.Add(new SchemaError(path, $"value {Short(inst)} is not one of the allowed values {en.GetRawText()}."));

        if (s.TryGetProperty("const", out JsonElement cn) && !JsonElement.DeepEquals(cn, inst))
            errors.Add(new SchemaError(path, $"value {Short(inst)} must be exactly {cn.GetRawText()}."));

        if (inst.ValueKind == JsonValueKind.Number) ApplyNumeric(s, inst, path, errors);

        if (inst.ValueKind == JsonValueKind.String && s.TryGetProperty("pattern", out JsonElement pat))
        {
            string text = pat.GetString()!;
            // GR-659: a pattern that runs past a match timeout is reported as an error at this pointer; the
            // RegexMatchTimeoutException used to escape a validator that never throws for a bad document. Since 2.1.000
            // the patterns run on the linear-time engine with no timeout (JsonSchemaSubset.CompilePattern), so this
            // branch is a guard for a caller-built Regex only.
            bool matched;
            try { matched = _set.PatternFor(text).IsMatch(inst.GetString()!); }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                errors.Add(new SchemaError(path, $"string {Short(inst)} could not be checked against the pattern {pat.GetRawText()} within the match time limit."));
                matched = true;   // reported above; not reported a second time as a mismatch
            }
            if (!matched)
                errors.Add(new SchemaError(path, $"string {Short(inst)} does not match the pattern {pat.GetRawText()}."));
        }

        if (inst.ValueKind == JsonValueKind.Array) ApplyArray(node, inst, path, errors);
        if (inst.ValueKind == JsonValueKind.Object) ApplyObject(node, inst, path, errors);

        if (s.TryGetProperty("anyOf", out JsonElement anyOf)) ApplyAnyOf(node, anyOf.GetArrayLength(), inst, path, errors);
        if (s.TryGetProperty("oneOf", out JsonElement oneOf)) ApplyOneOf(node, oneOf.GetArrayLength(), inst, path, errors);

        if (s.TryGetProperty("if", out _))
        {
            // 'if' never reports its own errors: it only chooses whether 'then' or 'else' applies.
            bool passed = Passes(_set.Child(node, "/if"), inst, path);
            if (passed && s.TryGetProperty("then", out _)) Apply(_set.Child(node, "/then"), inst, path, errors);
            if (!passed && s.TryGetProperty("else", out _)) Apply(_set.Child(node, "/else"), inst, path, errors);
        }
    }

    private static void ApplyNumeric(JsonElement s, JsonElement inst, string path, List<SchemaError> errors)
    {
        if (!inst.TryGetDouble(out double x) || !double.IsFinite(x))
        {
            // Only reachable for a literal too large for a double; refuse it rather than compare an infinity.
            errors.Add(new SchemaError(path, $"number {inst.GetRawText()} is outside the range of a double."));
            return;
        }
        if (s.TryGetProperty("minimum", out JsonElement min) && x < min.GetDouble())
            errors.Add(new SchemaError(path, $"{Num(x)} is below the minimum {min.GetRawText()}."));
        if (s.TryGetProperty("maximum", out JsonElement max) && x > max.GetDouble())
            errors.Add(new SchemaError(path, $"{Num(x)} is above the maximum {max.GetRawText()}."));
        if (s.TryGetProperty("exclusiveMinimum", out JsonElement xmin) && x <= xmin.GetDouble())
            errors.Add(new SchemaError(path, $"{Num(x)} must be strictly greater than {xmin.GetRawText()}."));
    }

    private void ApplyArray(SchemaNode node, JsonElement inst, string path, List<SchemaError> errors)
    {
        JsonElement s = node.Element;
        int n = inst.GetArrayLength();
        if (s.TryGetProperty("minItems", out JsonElement mi) && n < mi.GetInt32())
            errors.Add(new SchemaError(path, $"array has {n} item(s); at least {mi.GetInt32()} required."));
        if (s.TryGetProperty("items", out _))
        {
            SchemaNode items = _set.Child(node, "/items");
            for (int i = 0; i < n; i++)
                Apply(items, inst[i], path + "/" + i.ToString(CultureInfo.InvariantCulture), errors);
        }
    }

    private void ApplyObject(SchemaNode node, JsonElement inst, string path, List<SchemaError> errors)
    {
        JsonElement s = node.Element;

        if (s.TryGetProperty("required", out JsonElement req))
            foreach (JsonElement r in req.EnumerateArray())
                if (!inst.TryGetProperty(r.GetString()!, out _))
                    errors.Add(new SchemaError(path, $"missing required member '{r.GetString()}'."));

        bool hasProps = s.TryGetProperty("properties", out JsonElement props);
        bool hasAdditional = s.TryGetProperty("additionalProperties", out JsonElement additional);

        foreach (JsonProperty m in inst.EnumerateObject())
        {
            string childPath = path + "/" + JsonSchemaSubset.EscapeToken(m.Name);
            if (hasProps && props.TryGetProperty(m.Name, out _))
            {
                Apply(_set.Child(node, "/properties/" + JsonSchemaSubset.EscapeToken(m.Name)), m.Value, childPath, errors);
                continue;
            }
            if (!hasAdditional) continue;
            if (additional.ValueKind == JsonValueKind.False)
                errors.Add(new SchemaError(childPath, $"unknown member '{m.Name}' is not allowed here." +
                    (hasProps ? $" Allowed: {string.Join(", ", props.EnumerateObject().Select(p => p.Name))}." : "")));
            else if (additional.ValueKind == JsonValueKind.Object)
                Apply(_set.Child(node, "/additionalProperties"), m.Value, childPath, errors);
            // additionalProperties: true allows anything.
        }
    }

    private void ApplyAnyOf(SchemaNode node, int count, JsonElement inst, string path, List<SchemaError> errors)
    {
        var branchErrors = new List<List<SchemaError>>(count);
        for (int i = 0; i < count; i++)
        {
            var e = new List<SchemaError>();
            Apply(_set.Child(node, "/anyOf/" + i.ToString(CultureInfo.InvariantCulture)), inst, path, e);
            if (e.Count == 0) return;   // one passing branch is enough
            branchErrors.Add(e);
        }
        errors.Add(new SchemaError(path, $"value {Short(inst)} matches none of the {count} anyOf alternatives:"));
        AddBranchErrors("anyOf", branchErrors, errors);
    }

    private void ApplyOneOf(SchemaNode node, int count, JsonElement inst, string path, List<SchemaError> errors)
    {
        var branchErrors = new List<List<SchemaError>>(count);
        var passing = new List<int>();
        for (int i = 0; i < count; i++)
        {
            var e = new List<SchemaError>();
            Apply(_set.Child(node, "/oneOf/" + i.ToString(CultureInfo.InvariantCulture)), inst, path, e);
            if (e.Count == 0) passing.Add(i);
            branchErrors.Add(e);
        }
        if (passing.Count == 1) return;
        if (passing.Count == 0)
        {
            errors.Add(new SchemaError(path, $"value {Short(inst)} matches none of the {count} oneOf alternatives:"));
            AddBranchErrors("oneOf", branchErrors, errors);
        }
        else
        {
            errors.Add(new SchemaError(path, $"value {Short(inst)} matches {passing.Count} oneOf alternatives " +
                $"({string.Join(", ", passing)}); exactly one is required."));
        }
    }

    // Each failing branch's own errors are kept (labelled with the branch) so the author can see WHY no branch fitted.
    private static void AddBranchErrors(string keyword, List<List<SchemaError>> branchErrors, List<SchemaError> errors)
    {
        for (int i = 0; i < branchErrors.Count; i++)
            foreach (SchemaError e in branchErrors[i])
                errors.Add(e with { Message = $"[{keyword} alternative {i}] {e.Message}" });
    }

    private bool Passes(SchemaNode node, JsonElement inst, string path)
    {
        var e = new List<SchemaError>();
        Apply(node, inst, path, e);
        return e.Count == 0;
    }

    // ---------------------------------------------------------------------------------------------------------------

    private static bool TypeMatches(JsonElement type, JsonElement inst)
    {
        if (type.ValueKind == JsonValueKind.String) return IsType(type.GetString()!, inst);
        foreach (JsonElement t in type.EnumerateArray())
            if (IsType(t.GetString()!, inst)) return true;
        return false;
    }

    private static bool IsType(string name, JsonElement inst) => name switch
    {
        "object" => inst.ValueKind == JsonValueKind.Object,
        "array" => inst.ValueKind == JsonValueKind.Array,
        "string" => inst.ValueKind == JsonValueKind.String,
        "number" => inst.ValueKind == JsonValueKind.Number,
        "integer" => inst.ValueKind == JsonValueKind.Number && IsIntegral(inst),
        "boolean" => inst.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => inst.ValueKind == JsonValueKind.Null,
        _ => false,   // unreachable: type names are checked at load
    };

    /// <summary>A number with no fractional part (1 and 1.0 both count, as in JSON Schema).</summary>
    internal static bool IsIntegral(JsonElement number)
        => number.TryGetDouble(out double d) && double.IsFinite(d) && Math.Floor(d) == d;

    private static string KindOf(JsonElement inst) => inst.ValueKind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => IsIntegral(inst) ? "integer" : "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => inst.ValueKind.ToString(),
    };

    private static string Describe(JsonElement type)
        => type.ValueKind == JsonValueKind.String ? type.GetString()!
         : string.Join(" or ", type.EnumerateArray().Select(t => t.GetString()));

    // A short rendering of a value for a message: scalars in full, containers summarised so a message stays one line.
    private static string Short(JsonElement inst) => inst.ValueKind switch
    {
        JsonValueKind.Object => "(an object)",
        JsonValueKind.Array => "(an array)",
        _ => inst.GetRawText(),
    };

    private static string Num(double x) => x.ToString("R", CultureInfo.InvariantCulture);
}
