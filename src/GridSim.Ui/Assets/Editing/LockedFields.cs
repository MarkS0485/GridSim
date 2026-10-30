namespace GridSim.Ui.Assets.Editing;

// UI programme Phase 4.1 (ASSETS plan section 3.3, Assets/Editing; plan Phase 4: "Fields the loader refuses (line/
// transformer type, inService=false, the profiles) are shown locked with the loader's reason. A test pins that list
// against the loader."). These are fields the schema accepts (they are valid JSON v2) but GridSim's v2 loader
// (V2CaseLoader) REFUSES today, naming them: a case that sets one can be saved by nobody, because the save gate runs the
// v2 loader and refuses what it refuses (CF5, N327). The field grid shows each one LOCKED with the loader's reason, so
// nobody types a value only to have the save refused for it later. Resetting a locked field to absent is still allowed:
// that is how a case that already sets one (and so does not load) is put right.
//
// The list is v2 only: the v1 loader (JsonGridLoader) has no field it reads from the schema and then refuses (its
// refusals are about values, such as a bus that does not exist, and the save gate reports them).
//
// UiAuthoringLockedFieldsTests pins this list against the loader both ways: every entry is refused by V2CaseLoader on a
// real case (behaviour), and every refusal site in V2CaseLoader.cs (its NotYet calls and its profile list) is an entry
// here (source scan), so the two cannot drift apart.

/// <summary>
/// One field the v2 loader refuses: where it lives (the schema file and the JSON pointer of the record schema, as the
/// field catalogue names a record shape), the member, and the loader's reason in the words the grid shows.
/// </summary>
/// <param name="SchemaFile">The schema file of the record shape ("network.schema.json", "case.schema.json").</param>
/// <param name="ShapePointer">The JSON pointer of the record schema inside it ("/$defs/line"; "" for a manifest's root).</param>
/// <param name="Field">The member name (canonical spelling).</param>
/// <param name="Reason">Why the field is locked: what the loader refuses and what to do instead.</param>
/// <param name="OnlyWhen">Null when ANY value is refused; otherwise the one JSON literal that is refused (inService is
/// refused only when false: true is the default and loads), shown in the reason.</param>
public sealed record LockedField(string SchemaFile, string ShapePointer, string Field, string Reason, string? OnlyWhen = null);

/// <summary>The fields the v2 loader refuses (Phase 4.1), shown locked in the field grid with the loader's reason.</summary>
public static class LockedFields
{
    /// <summary>The v2 network schema file name (the element record shapes live in it).</summary>
    public const string NetworkSchema = "network.schema.json";

    /// <summary>The v2 case manifest schema file name.</summary>
    public const string CaseSchema = "case.schema.json";

    // The loader's own reasons, kept close to its wording (V2CaseLoader: LaterTypePhase, LaterNetworkPhase and the
    // profile refusal), with what the user can do about it.
    private const string TypeReason =
        "Locked: the v2 loader does not bind line or transformer types yet, so a case that sets this is refused at load " +
        "(and so cannot be saved). Leave it absent.";

    private const string InServiceReason =
        "Locked: the v2 loader refuses an out-of-service line or transformer (inService false) today; of the network " +
        "fields v1 cannot hold it reads only fault sequence data so far. Absent means true (in service).";

    private static string ProfileReason(string profile) =>
        "Locked: GridSim does not yet bind a " + profile + " from a case (the built-in settings apply), so a case that sets it is " +
        "refused at load (and so cannot be saved). Leave it absent.";

    /// <summary>Every locked field, in a fixed order (lines, transformers, the manifest's profiles).</summary>
    public static IReadOnlyList<LockedField> All { get; } = new[]
    {
        new LockedField(NetworkSchema, "/$defs/line", "type", TypeReason),
        new LockedField(NetworkSchema, "/$defs/line", "inService", InServiceReason, "false"),
        new LockedField(NetworkSchema, "/$defs/transformer", "type", TypeReason),
        new LockedField(NetworkSchema, "/$defs/transformer", "inService", InServiceReason, "false"),
        new LockedField(CaseSchema, "", "gridCodeProfile", ProfileReason("gridCodeProfile")),
        new LockedField(CaseSchema, "", "solverProfile", ProfileReason("solverProfile")),
    };

    /// <summary>The locked entry for a field of the record shape at (<paramref name="schemaFile"/>,
    /// <paramref name="shapePointer"/>), or null when the field is not locked. Names compare ordinally (v2 is
    /// case-sensitive).</summary>
    public static LockedField? Find(string schemaFile, string shapePointer, string field)
    {
        ArgumentNullException.ThrowIfNull(schemaFile);
        ArgumentNullException.ThrowIfNull(shapePointer);
        ArgumentNullException.ThrowIfNull(field);
        foreach (LockedField f in All)
            if (string.Equals(f.SchemaFile, schemaFile, StringComparison.Ordinal)
                && string.Equals(f.ShapePointer, shapePointer, StringComparison.Ordinal)
                && string.Equals(f.Field, field, StringComparison.Ordinal))
                return f;
        return null;
    }
}
