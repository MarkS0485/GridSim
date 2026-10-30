using GridSim.Ui.Assets.Keys;

namespace GridSim.Ui.Assets.Index;

// UI programme ASSETS B2: the rows of the PROPERTIES and CONNECTIONS sub-tabs (plan section 3.2), as plain immutable
// values the view binds and the tests compare exactly. Nothing here is formatted for a culture: literals are the case
// file's own text, solved values the shortest round-trip invariant number (as the case editor writes numbers).

/// <summary>
/// One field of a case record on the PROPERTIES sub-tab (Run mode, read-only): the field as the schema describes it
/// (FieldSpec), the record's literal as written (or that it is absent and what absence means), and, where the solver has
/// its own value for it, that value beside it under a caption saying what it is (plan S9: authored and solved side by side,
/// never an authored value under a solved caption).
/// </summary>
/// <param name="Field">The member name, canonical spelling.</param>
/// <param name="Label">The schema title without its trailing unit ("Capacity").</param>
/// <param name="Unit">The unit from the title's trailing brackets ("MVA"), or "" when the title has none.</param>
/// <param name="Help">The schema description.</param>
/// <param name="Kind">How the field is typed (FieldKind: Number, Integer, Boolean, String, Enum, Object, Array, Map, Raw).</param>
/// <param name="Required">True when the schema requires the field.</param>
/// <param name="Present">True when the record writes the field.</param>
/// <param name="Literal">The JSON text exactly as written in the file (strings with their quotes); "" when absent.</param>
/// <param name="Absent">What absence means (FieldSpec.AbsentText: the loader's default literal, "derived: ...", "none" or
/// "required").</param>
/// <param name="SolvedCaption">What the solved value is ("as solved", "as solved: per-circuit equivalent of 2 circuits"),
/// or "" when the solver has no value of its own for this field.</param>
/// <param name="SolvedValue">The solved value (shortest round-trip, invariant), or "".</param>
public sealed record PropertyRow(string Field, string Label, string Unit, string Help, string Kind, bool Required, bool Present,
    string Literal, string Absent, string SolvedCaption, string SolvedValue)
{
    /// <summary>The state column: "as written", or "absent: " and what absence means.</summary>
    public string State => Present ? "as written" : "absent: " + Absent;

    /// <summary>True when the row carries a solved value.</summary>
    public bool HasSolved => SolvedCaption.Length > 0;

    /// <summary>Split a schema title into its label and unit: "Capacity (MVA)" gives ("Capacity", "MVA"); a title with
    /// no trailing bracket is all label.</summary>
    public static (string Label, string Unit) SplitTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        if (title.EndsWith(')'))
        {
            int open = title.LastIndexOf(" (", StringComparison.Ordinal);
            if (open > 0) return (title[..open], title[(open + 2)..^1]);
        }
        return (title, "");
    }
}

/// <summary>The PROPERTIES content of one asset: which record it is and its fields.</summary>
/// <param name="Record">The record (document, array, index).</param>
/// <param name="Heading">"generation.json #17 'Drax 1'": the record as messages name it.</param>
/// <param name="Shape">The record's schema title ("Generator").</param>
/// <param name="SolvedNote">Where the solved column comes from ("solved values from tick 37"), or why there is none.</param>
/// <param name="Rows">One row per schema field, in schema order, then any member the schema does not list.</param>
public sealed record AssetProperties(CaseRef Record, string Heading, string Shape, string SolvedNote, IReadOnlyList<PropertyRow> Rows);

/// <summary>One link on the CONNECTIONS sub-tab: what is connected, how, and the node a click selects.</summary>
/// <param name="Section">Which list it belongs to (<see cref="AssetConnections.ModelSection"/> or
/// <see cref="AssetConnections.WhereUsedSection"/>).</param>
/// <param name="Relation">How it is connected ("from end", "unit on this bus", "transformer", a referring field name).</param>
/// <param name="Label">What it is (a name, or the referring record's description).</param>
/// <param name="Detail">A muted second text (bus ids, a direction).</param>
/// <param name="Target">The node a click selects, or null when no node stands for it.</param>
public sealed record ConnectionRow(string Section, string Relation, string Label, string Detail, AssetKey? Target)
{
    /// <summary>True when a click selects something.</summary>
    public bool CanOpen => Target is not null;
}

/// <summary>The CONNECTIONS content of one asset: the model's adjacency and, when a case is loaded, the where-used list.</summary>
/// <param name="Model">The model adjacency (buses, branches, units, and the records tied to them).</param>
/// <param name="WhereUsed">Every reference to this asset in the case file (the case editor's ReferenceIndex); empty
/// without a case.</param>
public sealed record AssetConnections(IReadOnlyList<ConnectionRow> Model, IReadOnlyList<ConnectionRow> WhereUsed)
{
    /// <summary>The section title of the model adjacency list.</summary>
    public const string ModelSection = "CONNECTED IN THE MODEL";

    /// <summary>The section title of the where-used list.</summary>
    public const string WhereUsedSection = "WHERE USED IN THE CASE FILE";

    /// <summary>Nothing connected.</summary>
    public static AssetConnections None { get; } = new(Array.Empty<ConnectionRow>(), Array.Empty<ConnectionRow>());

    /// <summary>True when either list has a row.</summary>
    public bool Any => Model.Count > 0 || WhereUsed.Count > 0;

    /// <summary>True when the where-used list has a row.</summary>
    public bool HasWhereUsed => WhereUsed.Count > 0;
}
