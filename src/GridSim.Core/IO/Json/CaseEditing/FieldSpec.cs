using System.Text.Json;

namespace GridSim.Core.IO.Json.CaseEditing;

/// <summary>How a field is edited, derived from its schema.</summary>
internal enum FieldKind
{
    /// <summary>A JSON number (strict grammar, finite; written shortest round-trip).</summary>
    Number,

    /// <summary>A whole number in the 32-bit range (written as digits).</summary>
    Integer,

    /// <summary>true or false (Space toggles).</summary>
    Boolean,

    /// <summary>Free text (optionally constrained by a pattern).</summary>
    String,

    /// <summary>One of a fixed list of values (Space cycles, a picker lists them).</summary>
    Enum,

    /// <summary>A nested object with its own fields (Enter descends).</summary>
    Object,

    /// <summary>An array of records (Enter opens the list).</summary>
    Array,

    /// <summary>An object of name -> value pairs (asset-class params and provenance).</summary>
    Map,

    /// <summary>Anything the schema does not describe as one of the above (oneOf, anyOf): edited as raw JSON text.</summary>
    Raw,
}

/// <summary>Where a schema object lives: a file of the loaded set and a JSON pointer inside it.</summary>
internal readonly record struct SchemaLocation(string File, string Pointer)
{
    public override string ToString() => File + "#" + Pointer;
}

/// <summary>
/// One field of a record, taken from the schema (the hybrid model, SCHEMA-EDITOR-PLAN.md section 5): the schema gives
/// the field list and order, label, help, type, enum, range, pattern and requiredness; the loader DTO, by reflection,
/// gives the value an ABSENT field takes (the schema subset refuses the 'default' keyword, and the DTO is what the
/// loader really uses). Nothing about a field is hard-coded in C#.
/// </summary>
/// <param name="Name">The member name, canonical spelling (case-sensitive).</param>
/// <param name="Title">The label (schema 'title', which carries the unit).</param>
/// <param name="Description">The help line (schema 'description').</param>
/// <param name="Kind">How the field is edited.</param>
/// <param name="Required">True when the record schema lists the member as required (it cannot be reset to absent).</param>
/// <param name="EnumValues">The allowed values of an <see cref="FieldKind.Enum"/> field (strings or integers), else empty.</param>
/// <param name="Minimum">The schema 'minimum', if any.</param>
/// <param name="Maximum">The schema 'maximum', if any.</param>
/// <param name="ExclusiveMinimum">The schema 'exclusiveMinimum', if any.</param>
/// <param name="Pattern">The schema 'pattern' of a string field, if any.</param>
/// <param name="Nested">The record shape of a nested object, of an array's items, or of a map's values; null for scalars.</param>
/// <param name="IsBusReference">True when the field holds a bus id (a $ref to the shared busId definition).</param>
/// <param name="DefaultLiteral">The JSON literal of the DTO default when it is a plain value (for example "1" or "\"pq\""), else null.</param>
/// <param name="AbsentText">What an absent field means, for display: the DTO default literal, or "derived: ..." from the schema text.</param>
internal sealed record FieldSpec(
    string Name,
    string Title,
    string Description,
    FieldKind Kind,
    bool Required,
    IReadOnlyList<JsonElement> EnumValues,
    double? Minimum,
    double? Maximum,
    double? ExclusiveMinimum,
    string? Pattern,
    SchemaLocation? Nested,
    bool IsBusReference,
    string? DefaultLiteral,
    string AbsentText)
{
    /// <summary>The enum values as display text (strings unquoted, numbers as written).</summary>
    public IReadOnlyList<string> EnumTexts => EnumValues.Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText()).ToList();
}

/// <summary>
/// The shape of one record (an object schema): its location, title, description and fields in schema order.
/// </summary>
internal sealed class RecordShape
{
    private readonly Dictionary<string, FieldSpec> _byName;

    public RecordShape(SchemaLocation location, string title, string description, IReadOnlyList<FieldSpec> fields, bool closed)
    {
        Location = location;
        Title = title;
        Description = description;
        Fields = fields;
        Closed = closed;
        _byName = fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
    }

    /// <summary>Where the record schema lives.</summary>
    public SchemaLocation Location { get; }

    /// <summary>The record's title (for example "Generator").</summary>
    public string Title { get; }

    /// <summary>The record's description (help text).</summary>
    public string Description { get; }

    /// <summary>The fields in schema 'properties' order (the order forms show and inserts follow).</summary>
    public IReadOnlyList<FieldSpec> Fields { get; }

    /// <summary>True when the schema refuses members it does not list (additionalProperties: false).</summary>
    public bool Closed { get; }

    /// <summary>The field with exactly this canonical name, or null.</summary>
    public FieldSpec? Field(string name) => _byName.GetValueOrDefault(name);

    /// <summary>
    /// The field whose canonical name equals <paramref name="name"/> ignoring case, or null. Used to read a v1 record
    /// (the loader matches keys case-insensitively) and to find non-canonical spellings for the normalise command.
    /// </summary>
    public FieldSpec? FieldIgnoringCase(string name)
        => _byName.GetValueOrDefault(name) ?? Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The position of a field in schema order, or -1.</summary>
    public int IndexOf(string name)
    {
        for (int i = 0; i < Fields.Count; i++)
            if (string.Equals(Fields[i].Name, name, StringComparison.Ordinal)) return i;
        return -1;
    }
}
