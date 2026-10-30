using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Ui.Assets.Keys;

namespace GridSim.Ui.Assets.Editing;

// UI programme Phase 4.1 (ASSETS plan section 3.3: "RecordActions (add, duplicate, delete with referrers, rename with
// cascade, reset to absent)"; section 3.5's context-menu commands). Each action is exactly one engine operation (CaseOps),
// so each is one transaction and one undo step, and every integrity rule is the engine's: a delete is refused while
// anything refers to the record and the refusal lists the referrers; a rename changes the key and every reference to it
// in one step across files (and is refused when the new key collides or the old branch name is shared ambiguously); an
// add is refused when a required field is missing, a value does not parse, a key collides or a reference does not
// resolve. Nothing here decides any of that; it only puts the engine's answers in public form.

/// <summary>One record that refers to another (the "where used" list, and a refused delete's referrers).</summary>
/// <param name="Field">The referring field ("bus", "transformer", "dynamicsClass").</param>
/// <param name="What">The referring record as messages name it.</param>
/// <param name="Record">The referring record (a nested record, such as one branch of a boundary, is its holder's
/// position as the engine reports it).</param>
public sealed record WhereUsedRow(string Field, string What, CaseRef Record);

/// <summary>One field a new record needs (for the Add prompt): its name, label and kind, and its choices if an enum.</summary>
public sealed record NewRecordField(string Field, string Label, string Kind, IReadOnlyList<string> Choices);

/// <summary>The record actions of Author mode (UI programme Phase 4.1), each one engine operation and one undo step.</summary>
public static class RecordActions
{
    /// <summary>The key field of the records of <paramref name="record"/>'s collection ("id" for buses, "name" for lines,
    /// transformers and asset classes), or null when its records have no key (so there is nothing to rename).</summary>
    public static string? KeyField(CaseAuthoring authoring, CaseRef record)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        ArgumentNullException.ThrowIfNull(record);
        return CaseOps.KeyFieldOf(authoring.RoleOf(record));
    }

    /// <summary>Everything that refers to <paramref name="record"/> through its key (empty for a record with no key, or
    /// nothing referring to it).</summary>
    public static IReadOnlyList<WhereUsedRow> WhereUsed(CaseAuthoring authoring, CaseRef record)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        ArgumentNullException.ThrowIfNull(record);
        if (record.Index < 0) return Array.Empty<WhereUsedRow>();
        CaseSession s = authoring.Session;
        return CaseOps.ReferrersOf(s, record.ToRecordRef())
            .Select(r => new WhereUsedRow(r.Field, r.What, CaseRef.Of(r.Record, s.Documents[r.Record.Document].FileName)))
            .ToList();
    }

    /// <summary>The required fields of a new record of <paramref name="collection"/> (what the Add prompt asks for), in
    /// schema order.</summary>
    public static IReadOnlyList<NewRecordField> RequiredFields(CaseAuthoring authoring, AuthoringRecordSet collection)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        ArgumentNullException.ThrowIfNull(collection);
        CaseSession s = authoring.Session;
        RecordShape shape = CaseOps.ItemShape(s, s.Documents[collection.Document], collection.ArrayPointer)
            ?? throw new ArgumentException($"{collection.FileName} {collection.ArrayPointer}: not a list of records.", nameof(collection));
        return shape.Fields.Where(f => f.Required)
            .Select(f => new NewRecordField(f.Name, f.Title, f.Kind.ToString(), f.EnumTexts)).ToList();
    }

    /// <summary>
    /// Add a record to <paramref name="collection"/> from typed field values (canonical field names), at the end (or at
    /// <paramref name="insertAt"/>). Refused when a required field is missing, a value does not parse or validate, a key
    /// collides or a reference does not resolve. A v1 file that does not exist yet is created.
    /// </summary>
    public static AuthoringResult Add(CaseAuthoring authoring, AuthoringRecordSet collection, IReadOnlyList<KeyValuePair<string, string>> values, int insertAt = -1)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(values);
        // The engine appends when the position is out of range.
        int at = insertAt < 0 ? int.MaxValue : insertAt;
        return authoring.Apply(s => CaseOps.AddRecord(s, collection.Document, collection.ArrayPointer, at, values));
    }

    /// <summary>Duplicate a record directly after itself (a bus copy takes the largest id + 1; a named copy keeps the name
    /// and the message says to rename it).</summary>
    public static AuthoringResult Duplicate(CaseAuthoring authoring, CaseRef record)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        ArgumentNullException.ThrowIfNull(record);
        if (record.Index < 0) return AuthoringResult.Refused(record.Display + " is the whole file; it cannot be duplicated.");
        return authoring.Apply(s => CaseOps.DuplicateRecord(s, record.ToRecordRef()));
    }

    /// <summary>Delete a record; refused while anything refers to it, the refusal listing the referrers.</summary>
    public static AuthoringResult Delete(CaseAuthoring authoring, CaseRef record)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        ArgumentNullException.ThrowIfNull(record);
        if (record.Index < 0) return AuthoringResult.Refused(record.Display + " is the whole file; it cannot be deleted from the editor.");
        return authoring.Apply(s => CaseOps.DeleteRecord(s, record.ToRecordRef()));
    }

    /// <summary>
    /// Rename a record's key (bus id, line or transformer name, asset-class name) and every reference to it, in one step.
    /// With <paramref name="dryRun"/> nothing changes and the message says what would follow (for the confirmation).
    /// </summary>
    public static AuthoringResult Rename(CaseAuthoring authoring, CaseRef record, string typed, bool dryRun = false)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(typed);
        if (record.Index < 0) return AuthoringResult.Refused(record.Display + " has no key to rename.");
        return authoring.Apply(s => CaseOps.RenameKey(s, record.ToRecordRef(), typed, dryRun));
    }

    /// <summary>Reset one field of a record to absent (the field grid's reset, as an action).</summary>
    public static AuthoringResult ResetToAbsent(CaseAuthoring authoring, CaseRef record, string field)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(field);
        return authoring.FieldGrid(record).Reset(field);
    }
}
