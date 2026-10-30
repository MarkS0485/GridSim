using System.Globalization;
using System.Text.Json;

namespace GridSim.Core.IO.Json.Schema;

/// <summary>The value type a kind parameter takes. Number and integer carry a unit; enum carries its allowed values.</summary>
public enum KindParamType
{
    Number,
    Integer,
    Enum,
    String,
    Boolean,
}

/// <summary>
/// One <c>codeDefaults</c> entry: the C# value a named consumer uses today. <see cref="Ref"/> is opaque to the
/// registry (the kind-spec agreement tests define and check its forms). <see cref="Value"/> is a detached
/// number, string or boolean.
/// </summary>
public sealed record KindCodeDefault(string Consumer, string Ref, JsonElement Value);

/// <summary>One parameter of a kind, exactly as declared in its <c>kinds/&lt;kind&gt;.kind.json</c>.</summary>
public sealed record KindParamSpec(
    string Name,
    KindParamType Type,
    string? Unit,
    double? Min,
    double? Max,
    IReadOnlyList<string> Values,
    bool Required,
    bool MarkGated,
    string Description,
    IReadOnlyList<KindCodeDefault> CodeDefaults);

/// <summary>One kind's parameter contract, loaded from <see cref="SourceFile"/>.</summary>
public sealed record KindSpec(
    string Kind,
    string Title,
    string Description,
    IReadOnlyDictionary<string, KindParamSpec> Params,
    string SourceFile);

/// <summary>
/// The per-kind parameter contracts (<c>schemas/v2/kinds/*.kind.json</c>, format <c>gridsim-kind/2</c>), and the
/// check of a template's <c>params</c> against its kind.
///
/// Loading is fail-loud in two layers. First every kind file is validated against <c>kind.schema.json</c> from the
/// schema set (unknown members anywhere are refused there). Then the rules that schema subset cannot express are
/// checked here: a number or integer parameter must declare a unit; only number and integer may carry a unit, min
/// or max; an enum must list its values and only an enum may; min may not exceed max; the file must be named
/// after its kind; a kind may be declared once; and each codeDefaults value must have the parameter's type (and,
/// for an enum, be one of its values). Every problem across every file is collected and thrown together.
///
/// A codeDefaults value is NOT range-checked against min/max: codeDefaults record what the code does today,
/// disagreements included, and a code value outside a declared range is a data finding for the agreement tests to
/// report, not something the registry should hide by refusing to load.
/// </summary>
public sealed class KindSpecRegistry
{
    /// <summary>The document tag every kind file carries, matched exactly.</summary>
    public const string SchemaTag = "gridsim-kind/2";

    /// <summary>The schema file (in the loaded schema set) kind files are validated against.</summary>
    public const string KindSchemaFile = "kind.schema.json";

    /// <summary>The file-name suffix of a kind spec.</summary>
    public const string KindFileSuffix = ".kind.json";

    private readonly Dictionary<string, KindSpec> _kinds;

    private KindSpecRegistry(Dictionary<string, KindSpec> kinds) => _kinds = kinds;

    /// <summary>Every loaded kind, sorted by name.</summary>
    public IReadOnlyList<KindSpec> Kinds => _kinds.Values.OrderBy(k => k.Kind, StringComparer.Ordinal).ToList();

    /// <summary>True when the kind is declared.</summary>
    public bool Contains(string kind) => _kinds.ContainsKey(kind);

    /// <summary>The spec for a kind, or null when it is not declared.</summary>
    public KindSpec? Find(string kind) => _kinds.GetValueOrDefault(kind);

    /// <summary>The spec for a kind; throws (fail loud) when it is not declared.</summary>
    public KindSpec Get(string kind)
        => _kinds.TryGetValue(kind, out KindSpec? k) ? k
         : throw new InvalidDataException($"Unknown kind '{kind}': no {kind}{KindFileSuffix} is loaded (known: {string.Join(", ", _kinds.Keys.OrderBy(x => x, StringComparer.Ordinal))}).");

    /// <summary>
    /// Load every <c>*.kind.json</c> in <paramref name="kindsDir"/>, validating each against
    /// <see cref="KindSchemaFile"/> in <paramref name="schemas"/>. Throws on a missing directory or any invalid file.
    /// An existing but empty directory gives an empty registry (a later check then fails loud on any kind it needs).
    /// </summary>
    public static KindSpecRegistry LoadDirectory(string kindsDir, JsonSchemaSubset schemas)
    {
        if (!Directory.Exists(kindsDir)) throw new DirectoryNotFoundException($"Kind spec directory not found: {kindsDir}");
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(kindsDir, "*" + KindFileSuffix, SearchOption.TopDirectoryOnly)
                                          .OrderBy(p => p, StringComparer.Ordinal))
            sources[Path.GetFileName(path)] = File.ReadAllText(path);
        return FromSources(sources, schemas);
    }

    /// <summary>Load kind specs from in-memory text keyed by file name (what <see cref="LoadDirectory"/> calls; also used by tests).</summary>
    public static KindSpecRegistry FromSources(IReadOnlyDictionary<string, string> sources, JsonSchemaSubset schemas)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(schemas);
        if (!schemas.Contains(KindSchemaFile))
            throw new InvalidDataException($"The schema set has no {KindSchemaFile}, so kind specs cannot be validated.");

        var validator = new SchemaValidator(schemas);
        var errors = new List<string>();
        var kinds = new Dictionary<string, KindSpec>(StringComparer.Ordinal);

        foreach ((string file, string text) in sources.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            JsonElement root;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(text, JsonSchemaSubset.ReaderOptions);
                root = doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                errors.Add($"{file}: not valid JSON: {ex.Message}");
                continue;
            }

            // Layer 1: the schema. If the shape is wrong, the semantic layer below would only trip over it.
            IReadOnlyList<SchemaError> schemaErrors = validator.Validate(root, KindSchemaFile);
            if (schemaErrors.Count > 0)
            {
                errors.AddRange(schemaErrors.Select(e => $"{file}: {e}"));
                continue;
            }

            // Layer 2: the rules the schema subset cannot express.
            KindSpec spec = Parse(file, root, errors);
            if (file != spec.Kind + KindFileSuffix)
                errors.Add($"{file}: declares kind '{spec.Kind}' but must then be named '{spec.Kind}{KindFileSuffix}'.");
            if (!kinds.TryAdd(spec.Kind, spec))
                errors.Add($"{file}: kind '{spec.Kind}' is already declared in {kinds[spec.Kind].SourceFile}.");
        }

        if (errors.Count > 0)
            throw new InvalidDataException("Invalid v2 kind spec(s) (" + errors.Count + " problem(s)):" + Environment.NewLine + "  " +
                string.Join(Environment.NewLine + "  ", errors));
        return new KindSpecRegistry(kinds);
    }

    // Parse a schema-valid kind file into a KindSpec, adding a message for every semantic rule broken.
    private static KindSpec Parse(string file, JsonElement root, List<string> errors)
    {
        string kind = root.GetProperty("kind").GetString()!;
        var ps = new Dictionary<string, KindParamSpec>(StringComparer.Ordinal);

        foreach (JsonProperty p in root.GetProperty("params").EnumerateObject())
        {
            JsonElement s = p.Value;
            string at = $"{file}: /params/{JsonSchemaSubset.EscapeToken(p.Name)}";
            KindParamType type = s.GetProperty("type").GetString() switch
            {
                "number" => KindParamType.Number,
                "integer" => KindParamType.Integer,
                "enum" => KindParamType.Enum,
                "string" => KindParamType.String,
                _ => KindParamType.Boolean,   // the schema's enum admits nothing else
            };
            bool numeric = type is KindParamType.Number or KindParamType.Integer;

            string? unit = s.TryGetProperty("unit", out JsonElement u) ? u.GetString() : null;
            double? min = s.TryGetProperty("min", out JsonElement mn) ? mn.GetDouble() : null;
            double? max = s.TryGetProperty("max", out JsonElement mx) ? mx.GetDouble() : null;
            List<string> values = s.TryGetProperty("values", out JsonElement vs)
                ? vs.EnumerateArray().Select(v => v.GetString()!).ToList() : new List<string>();

            if (numeric && unit is null) errors.Add($"{at}: a {Name(type)} parameter must declare its unit.");
            if (!numeric && unit is not null) errors.Add($"{at}: only number and integer parameters may declare a unit.");
            if (!numeric && (min is not null || max is not null)) errors.Add($"{at}: only number and integer parameters may declare min or max.");
            if (min is not null && max is not null && min > max) errors.Add($"{at}: min {Num(min.Value)} is above max {Num(max.Value)}.");
            if (type == KindParamType.Enum && values.Count == 0) errors.Add($"{at}: an enum parameter must list its values.");
            if (type != KindParamType.Enum && s.TryGetProperty("values", out _)) errors.Add($"{at}: only an enum parameter may list values.");
            if (values.Count != values.Distinct(StringComparer.Ordinal).Count()) errors.Add($"{at}: values lists the same value twice.");

            var defaults = new List<KindCodeDefault>();
            if (s.TryGetProperty("codeDefaults", out JsonElement cds))
            {
                int i = 0;
                foreach (JsonElement cd in cds.EnumerateArray())
                {
                    JsonElement value = cd.GetProperty("value").Clone();
                    string cdAt = $"{at}/codeDefaults/{i.ToString(CultureInfo.InvariantCulture)}";
                    // A codeDefault records what the C# does today, and some fallbacks really are infinite
                    // (double.PositiveInfinity = "never trips"). JSON has no infinite number, so for a number parameter
                    // the exact strings "+Infinity" and "-Infinity" are accepted HERE ONLY; a template value must
                    // still be a finite number (ValueTypeProblem below is unchanged for template params).
                    bool infiniteDefault = type == KindParamType.Number && value.ValueKind == JsonValueKind.String
                        && value.GetString() is "+Infinity" or "-Infinity";
                    string? bad = infiniteDefault ? null : ValueTypeProblem(type, value, values);
                    if (bad is not null) errors.Add($"{cdAt}: value {value.GetRawText()} {bad}");
                    defaults.Add(new KindCodeDefault(cd.GetProperty("consumer").GetString()!, cd.GetProperty("ref").GetString()!, value));
                    i++;
                }
            }

            ps[p.Name] = new KindParamSpec(p.Name, type, unit, min, max, values,
                s.GetProperty("required").GetBoolean(),
                s.TryGetProperty("markGated", out JsonElement mg) && mg.GetBoolean(),
                s.GetProperty("description").GetString()!,
                defaults);
        }

        return new KindSpec(kind, root.GetProperty("title").GetString()!, root.GetProperty("description").GetString()!, ps, file);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Template params against a kind
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Check one template's <c>params</c> object against its kind. Every problem is returned with a JSON pointer
    /// built on <paramref name="paramsPointer"/> (the pointer of the <c>params</c> object in its document). Rules:
    /// <list type="bullet">
    /// <item>an unknown kind, or a key the kind does not declare, is an error;</item>
    /// <item>a required parameter that is missing is an error, for a placeholder too (a placeholder lists the
    /// kind's required keys with null values, so its shape is explicit);</item>
    /// <item>placeholder: every value must be null. Active: no value may be null;</item>
    /// <item>a value (bare, or the <c>v</c> of the object form) must have the parameter's type: a number for
    /// number, a whole number for integer, one of the listed strings for enum, a string for string, true or false
    /// for boolean;</item>
    /// <item>a unit written in the object form must equal the spec's unit exactly, and is refused on a parameter
    /// that has no unit;</item>
    /// <item>a number or integer must lie within the spec's min and max (both inclusive) when declared.</item>
    /// </list>
    /// </summary>
    public IReadOnlyList<SchemaError> CheckParams(string kind, JsonElement parameters, bool placeholder, string paramsPointer)
    {
        var errors = new List<SchemaError>();
        if (!_kinds.TryGetValue(kind, out KindSpec? spec))
        {
            errors.Add(new SchemaError(paramsPointer, $"unknown kind '{kind}': no {kind}{KindFileSuffix} is loaded."));
            return errors;
        }
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new SchemaError(paramsPointer, $"params must be an object, found {parameters.ValueKind}."));
            return errors;
        }

        foreach (KindParamSpec p in spec.Params.Values.Where(p => p.Required))
            if (!parameters.TryGetProperty(p.Name, out _))
                errors.Add(new SchemaError(paramsPointer, $"kind '{kind}' requires parameter '{p.Name}', which is missing."));

        foreach (JsonProperty m in parameters.EnumerateObject())
        {
            string at = paramsPointer + "/" + JsonSchemaSubset.EscapeToken(m.Name);
            if (!spec.Params.TryGetValue(m.Name, out KindParamSpec? p))
            {
                errors.Add(new SchemaError(at, $"unknown parameter '{m.Name}' for kind '{kind}' (declared: {string.Join(", ", spec.Params.Keys)})."));
                continue;
            }

            JsonElement v = m.Value;
            if (v.ValueKind == JsonValueKind.Null)
            {
                if (!placeholder) errors.Add(new SchemaError(at, "an active template may not have a null value; set it or leave the key out."));
                continue;
            }
            if (placeholder)
            {
                errors.Add(new SchemaError(at, "a placeholder template must have every value null."));
                continue;
            }

            // The object form {v, unit, prov}: check the unit here, then judge v like a bare value.
            if (v.ValueKind == JsonValueKind.Object)
            {
                if (v.TryGetProperty("unit", out JsonElement unit))
                {
                    string written = unit.GetString() ?? "";
                    if (p.Unit is null)
                        errors.Add(new SchemaError(at + "/unit", $"parameter '{m.Name}' is a {Name(p.Type)} and takes no unit, but '{written}' is written."));
                    else if (!string.Equals(written, p.Unit, StringComparison.Ordinal))
                        errors.Add(new SchemaError(at + "/unit", $"unit '{written}' does not match the kind's unit '{p.Unit}' for '{m.Name}' (no conversion is done; write the spec's unit)."));
                }
                if (!v.TryGetProperty("v", out JsonElement inner))
                {
                    errors.Add(new SchemaError(at, "the object form needs a 'v' member."));
                    continue;
                }
                at += "/v";
                v = inner;
            }

            string? bad = ValueTypeProblem(p.Type, v, p.Values);
            if (bad is not null)
            {
                errors.Add(new SchemaError(at, $"value {v.GetRawText()} {bad}"));
                continue;
            }
            if (p.Type is KindParamType.Number or KindParamType.Integer)
            {
                double x = v.GetDouble();
                if (p.Min is double lo && x < lo)
                    errors.Add(new SchemaError(at, $"{Num(x)} {p.Unit} is below the minimum {Num(lo)} for '{m.Name}'."));
                if (p.Max is double hi && x > hi)
                    errors.Add(new SchemaError(at, $"{Num(x)} {p.Unit} is above the maximum {Num(hi)} for '{m.Name}'."));
            }
        }
        return errors;
    }

    /// <summary>Null when <paramref name="v"/> is a legal value of <paramref name="type"/>, else the reason (completing "value X ...").</summary>
    private static string? ValueTypeProblem(KindParamType type, JsonElement v, IReadOnlyList<string> enumValues) => type switch
    {
        KindParamType.Number when v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double d) || !double.IsFinite(d)
            => "is not a finite number.",
        KindParamType.Integer when v.ValueKind != JsonValueKind.Number || !SchemaValidator.IsIntegral(v)
            => "is not a whole number.",
        KindParamType.Enum when v.ValueKind != JsonValueKind.String || !enumValues.Contains(v.GetString()!, StringComparer.Ordinal)
            => $"is not one of the allowed values: {string.Join(", ", enumValues)}.",
        KindParamType.String when v.ValueKind != JsonValueKind.String
            => "is not a string.",
        KindParamType.Boolean when v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            => "is not true or false.",
        _ => null,
    };

    private static string Name(KindParamType t) => t switch
    {
        KindParamType.Number => "number",
        KindParamType.Integer => "integer",
        KindParamType.Enum => "enum",
        KindParamType.String => "string",
        _ => "boolean",
    };

    private static string Num(double x) => x.ToString("R", CultureInfo.InvariantCulture);
}
