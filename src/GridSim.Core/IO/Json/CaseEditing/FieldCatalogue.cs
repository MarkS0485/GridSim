using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Core.IO.Json.Schema;

namespace GridSim.Core.IO.Json.CaseEditing;

/// <summary>
/// Record shapes and field specs read from a loaded schema set (<see cref="JsonSchemaSubset"/>), cached per schema
/// location. For v1 the absent-value meaning comes from the loader DTOs by reflection (a fresh <c>new XDto()</c>, the
/// same absence rule the v2 projector uses); the DTO for each record schema is given by <see cref="CaseFileSpec"/>.
/// </summary>
internal sealed class FieldCatalogue
{
    private readonly JsonSchemaSubset _set;
    private readonly IReadOnlyDictionary<SchemaLocation, DtoTypeRef> _dtoOf;
    private readonly Dictionary<SchemaLocation, RecordShape> _shapes = new();
    private readonly object _gate = new();

    public FieldCatalogue(JsonSchemaSubset set, IReadOnlyDictionary<SchemaLocation, DtoTypeRef> dtoOf)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(dtoOf);
        _set = set;
        _dtoOf = dtoOf;
    }

    /// <summary>The schema set this catalogue reads.</summary>
    public JsonSchemaSubset Schemas => _set;

    private static readonly Lazy<FieldCatalogue> V1Catalogue = new(() =>
    {
        var dto = new Dictionary<SchemaLocation, DtoTypeRef>();
        foreach (CaseFileSpec f in CaseFileSpec.V1Files) dto[new SchemaLocation(f.SchemaFile, "/$defs/" + f.RecordDef)] = new(f.DtoType);
        foreach (CaseFileSpec.NestedDef n in CaseFileSpec.NestedDefList) dto[new SchemaLocation(n.SchemaFile, "/$defs/" + n.Def)] = new(n.DtoType);
        return new FieldCatalogue(BundledSchemas.V1(), dto);
    });

    /// <summary>The v1 catalogue (schemas/v1 with the loader DTOs).</summary>
    public static FieldCatalogue V1 => V1Catalogue.Value;

    private static readonly Lazy<FieldCatalogue> V2Catalogue = new(() => new FieldCatalogue(BundledSchemas.V2(), new Dictionary<SchemaLocation, DtoTypeRef>
    {
        // network.schema.json's ABSENCE RULE: an optional member that is absent takes exactly the v1 DTO default. So
        // the v2 element shapes map onto the v1 DTOs, and a member spelt as in v1 reads its default from there.
        [new SchemaLocation("network.schema.json", "/$defs/bus")] = new(typeof(BusDto)),
        [new SchemaLocation("network.schema.json", "/$defs/zip")] = new(typeof(BusZipDto)),
        [new SchemaLocation("network.schema.json", "/$defs/generator")] = new(typeof(GenDto)),
        [new SchemaLocation("network.schema.json", "/$defs/demand")] = new(typeof(DemandDto)),
        [new SchemaLocation("network.schema.json", "/$defs/line")] = new(typeof(LineDto)),
        [new SchemaLocation("network.schema.json", "/$defs/transformer")] = new(typeof(TransformerDto)),
        [new SchemaLocation("network.schema.json", "/$defs/oltc")] = new(typeof(OltcDto)),
        [new SchemaLocation("network.schema.json", "/$defs/boundary")] = new(typeof(BoundaryDto)),
        [new SchemaLocation("network.schema.json", "/$defs/protection")] = new(typeof(ProtectionInstanceDto)),
    }));

    /// <summary>
    /// The v2 catalogue (schemas/v2). The element shapes take their absent values from the v1 DTOs through the v1
    /// member names (the v2 absence rule); members v1 does not have fall back to their description's text.
    /// </summary>
    public static FieldCatalogue V2 => V2Catalogue.Value;

    /// <summary>The record shape of one v1 file's records.</summary>
    public static RecordShape V1RecordShape(CaseFileSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return V1.Shape(new SchemaLocation(spec.SchemaFile, "/$defs/" + spec.RecordDef));
    }

    /// <summary>
    /// The shape of the object schema at <paramref name="location"/> (a <c>$ref</c> there is followed). Throws when
    /// the location is not an object schema with properties.
    /// </summary>
    public RecordShape Shape(SchemaLocation location)
    {
        lock (_gate)
        {
            if (_shapes.TryGetValue(location, out RecordShape? cached)) return cached;
            RecordShape built = Build(location);
            _shapes[location] = built;
            return built;
        }
    }

    /// <summary>True when the schema at the location is an object schema with properties (so it has a record shape).</summary>
    public bool IsRecord(SchemaLocation location)
    {
        SchemaNode n = Follow(_set.NodeAt(location.File, location.Pointer));
        return n.Element.TryGetProperty("properties", out _);
    }

    /// <summary>The value spec of a map field (one entry of an asset class's params or provenance).</summary>
    public FieldSpec MapValueField(FieldSpec map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map.Kind != FieldKind.Map || map.Nested is not SchemaLocation loc)
            throw new ArgumentException($"field '{map.Name}' is not a map.", nameof(map));
        return BuildField(_set.NodeAt(loc.File, loc.Pointer), "*", required: false, dto: null, dtoInstance: null);
    }

    // ---------------------------------------------------------------------------------------------------------------

    private RecordShape Build(SchemaLocation location)
    {
        SchemaNode node = Follow(_set.NodeAt(location.File, location.Pointer));
        JsonElement s = node.Element;
        if (!s.TryGetProperty("properties", out JsonElement props))
            throw new InvalidDataException($"{location}: not an object schema with properties, so it has no record shape.");

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (s.TryGetProperty("required", out JsonElement req))
            foreach (JsonElement r in req.EnumerateArray()) required.Add(r.GetString()!);

        // The DTO default instance, when this record schema is one of the loader's DTOs.
        // Phase 0.1: the DTO type travels in a DtoTypeRef, whose annotation tells the trimmer to keep each DTO's
        // constructors and public properties, the two things read by reflection here and in BuildField.
        DtoTypeRef? dtoRef = _dtoOf.GetValueOrDefault(new SchemaLocation(node.File, node.Pointer)) ?? _dtoOf.GetValueOrDefault(location);
        Type? dto = dtoRef?.Type;
        object? instance = dtoRef is null ? null : Activator.CreateInstance(dtoRef.Type, nonPublic: true);

        var fields = new List<FieldSpec>();
        foreach (JsonProperty p in props.EnumerateObject())
        {
            SchemaNode child = _set.Child(node, "/properties/" + JsonSchemaSubset.EscapeToken(p.Name));
            fields.Add(BuildField(child, p.Name, required.Contains(p.Name), dto, instance));
        }

        bool closed = s.TryGetProperty("additionalProperties", out JsonElement ap) && ap.ValueKind == JsonValueKind.False;
        return new RecordShape(new SchemaLocation(node.File, node.Pointer), Text(s, "title") ?? location.Pointer, Text(s, "description") ?? "", fields, closed);
    }

    /// <summary>Follow <c>$ref</c>s from a node that carries nothing else meaningful (no type, enum or properties).</summary>
    private SchemaNode Follow(SchemaNode n)
    {
        for (int guard = 0; guard < 64; guard++)
        {
            JsonElement e = n.Element;
            if (!e.TryGetProperty("$ref", out _) || e.TryGetProperty("type", out _) || e.TryGetProperty("enum", out _)
                || e.TryGetProperty("properties", out _) || e.TryGetProperty("const", out _)) return n;
            n = _set.RefTarget(n);
        }
        throw new InvalidDataException($"{n.Key}: $ref chain too long.");
    }

    private FieldSpec BuildField(SchemaNode property, string name, bool required,
        [DynamicallyAccessedMembers(DtoTypeRef.Needs)] Type? dto, object? dtoInstance)
    {
        // The label and help come from the property itself first, then from what it refers to.
        string? title = Text(property.Element, "title");
        string? description = Text(property.Element, "description");
        bool busRef = false;
        double? min = null, max = null, xmin = null;
        string? pattern = null;
        FieldKind kind = FieldKind.Raw;
        IReadOnlyList<JsonElement> enumValues = Array.Empty<JsonElement>();
        SchemaLocation? nested = null;

        SchemaNode cur = property;
        for (int guard = 0; guard < 64; guard++)
        {
            JsonElement e = cur.Element;
            title ??= Text(e, "title");
            description ??= Text(e, "description");
            min ??= Num(e, "minimum");
            max ??= Num(e, "maximum");
            xmin ??= Num(e, "exclusiveMinimum");
            pattern ??= Text(e, "pattern");

            if (e.TryGetProperty("enum", out JsonElement en)) { kind = FieldKind.Enum; enumValues = en.EnumerateArray().Select(v => v.Clone()).ToList(); break; }
            if (e.TryGetProperty("const", out JsonElement cn)) { kind = FieldKind.Enum; enumValues = new[] { cn.Clone() }; break; }
            if (e.TryGetProperty("oneOf", out _) || e.TryGetProperty("anyOf", out _)) { kind = FieldKind.Raw; break; }
            if (e.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String)
            {
                switch (type.GetString())
                {
                    case "number": kind = FieldKind.Number; break;
                    case "integer": kind = FieldKind.Integer; break;
                    case "boolean": kind = FieldKind.Boolean; break;
                    case "string": kind = FieldKind.String; break;
                    case "array":
                        kind = FieldKind.Array;
                        if (e.TryGetProperty("items", out _)) nested = Location(Follow(_set.Child(cur, "/items")));
                        break;
                    case "object":
                        if (e.TryGetProperty("properties", out _)) { kind = FieldKind.Object; nested = Location(cur); }
                        else if (e.TryGetProperty("additionalProperties", out JsonElement add) && add.ValueKind == JsonValueKind.Object)
                        { kind = FieldKind.Map; nested = Location(Follow(_set.Child(cur, "/additionalProperties"))); }
                        else kind = FieldKind.Raw;
                        break;
                    default: kind = FieldKind.Raw; break;
                }
                break;
            }
            if (e.TryGetProperty("properties", out _)) { kind = FieldKind.Object; nested = Location(cur); break; }
            if (e.TryGetProperty("$ref", out JsonElement r))
            {
                if (r.GetString()!.EndsWith("/$defs/busId", StringComparison.Ordinal)) busRef = true;
                cur = _set.RefTarget(cur);
                continue;
            }
            break;   // a schema with no type at all: raw JSON
        }

        // The absent meaning: the DTO default (by reflection) when it is a plain value, else the schema's own words.
        string? defaultLiteral = null;
        if (dto is not null && dtoInstance is not null)
        {
            PropertyInfo? prop = dto.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(pi => string.Equals(JsonNamingPolicy.CamelCase.ConvertName(pi.Name), name, StringComparison.Ordinal));
            defaultLiteral = prop?.GetValue(dtoInstance) switch
            {
                double d => JsonLiteral.FormatNumber(d),
                int i => JsonLiteral.FormatInteger(i),
                bool b => JsonLiteral.FormatBool(b),
                string str => JsonLiteral.FormatString(str),
                _ => null,
            };
        }
        string absent = defaultLiteral ?? AbsentFromText(description, required);
        return new FieldSpec(name, title ?? name, description ?? "", kind, required, enumValues, min, max, xmin, pattern, nested, busRef, defaultLiteral, absent);
    }

    /// <summary>"derived: ..." from the "Absent: ..." sentence of a description, or a plain note.</summary>
    private static string AbsentFromText(string? description, bool required)
    {
        if (required) return "required";
        if (description is not null)
        {
            int at = description.IndexOf("Absent: ", StringComparison.Ordinal);
            if (at >= 0)
            {
                string rest = description[(at + "Absent: ".Length)..].Trim();
                if (rest.EndsWith('.')) rest = rest[..^1];
                return "derived: " + rest;
            }
        }
        return "none";
    }

    private static SchemaLocation Location(SchemaNode n) => new(n.File, n.Pointer);

    private static string? Text(JsonElement e, string keyword)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(keyword, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string keyword)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(keyword, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    /// <summary>Format a number the way messages show it (invariant, round-trip).</summary>
    internal static string Show(double d) => d.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>
/// Phase 0.1 (trimming and NativeAOT): a loader DTO type that <see cref="FieldCatalogue"/> reads by reflection (it makes
/// a default instance with <see cref="Activator"/> and reads its public properties to find each absent-value
/// default). The <see cref="DynamicallyAccessedMembersAttribute"/> on <see cref="Type"/> tells the trimmer to keep
/// exactly those members of every type stored here, so the reflection still finds them in a trimmed or AOT build, and
/// the trim analyser can prove it (no warning is suppressed). A plain Type in a dictionary carries no such promise.
/// </summary>
internal sealed class DtoTypeRef
{
    /// <summary>What the catalogue reads of a DTO: its constructors (public and non-public, as
    /// <c>Activator.CreateInstance(type, nonPublic: true)</c> may use either) and its public properties.</summary>
    internal const DynamicallyAccessedMemberTypes Needs =
        DynamicallyAccessedMemberTypes.PublicParameterlessConstructor
        | DynamicallyAccessedMemberTypes.NonPublicConstructors
        | DynamicallyAccessedMemberTypes.PublicProperties;

    public DtoTypeRef([DynamicallyAccessedMembers(Needs)] Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        Type = type;
    }

    /// <summary>The DTO type.</summary>
    [DynamicallyAccessedMembers(Needs)]
    public Type Type { get; }
}
