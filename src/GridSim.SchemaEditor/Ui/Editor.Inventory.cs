using System.Globalization;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;
using GridSim.SchemaEditor.Ui.Views;

namespace GridSim.SchemaEditor.Ui;

/// <summary>
/// The inventory commands (SCHEMA-EDITOR-PLAN.md section 6, slice E6): Add, Duplicate, Delete, Rename (a station,
/// or a key with every reference), filter and next, find names across sections, go to a bus, jump from a bus field,
/// and where used. Every change goes through <see cref="CaseOps"/>, so each is one undo step and every refusal says
/// why; questions (delete, rename cascades) are asked on the message line before anything changes.
/// </summary>
internal sealed partial class Editor
{
    private partial EditorEffect HandleMore(Command cmd, int row)
    {
        switch (cmd)
        {
            case Command.Add: Add(row); break;
            case Command.Duplicate:
                if (CurrentRecord(row) is RecordRef dup) Report(CaseOps.DuplicateRecord(Session, dup));
                else Say("D duplicates a record of a list.");
                break;
            case Command.Delete: Delete(row); break;
            case Command.Rename: Rename(row); break;
            case Command.Filter:
                AskText("Filter (empty shows all):", Top.Filter, text =>
                {
                    Top.Filter = text;
                    Top.Cursor = 0;
                    Top.Scroll = 0;
                    Say(text.Length == 0 ? "Filter cleared." : $"{Top.Visible.Count.ToString(CultureInfo.InvariantCulture)} row(s) contain '{text}'. N moves to the next.");
                    return true;
                });
                break;
            case Command.FilterNext:
                if (Top.Filter.Length == 0) Say("No filter: / sets one.");
                else if (Top.Visible.Count > 0) { Top.Cursor = (Top.Cursor + 1) % Top.Visible.Count; Say(""); }
                break;
            case Command.Find:
                AskText("Find name:", "", text =>
                {
                    if (text.Length == 0) { Say("Nothing to find."); return true; }
                    Push(FindView(text));
                    Say($"{Top.Visible.Count.ToString(CultureInfo.InvariantCulture)} record(s) named like '{text}'. Enter opens one.");
                    return true;
                });
                break;
            case Command.GotoBus:
                AskText("Go to bus id:", "", text =>
                {
                    if (!JsonLiteral.TryParseInt32(text, out int id, out string why)) { Say(why, true); return false; }
                    return GoToBus(id);
                });
                break;
            case Command.Jump: Jump(row); break;
            case Command.WhereUsed: WhereUsed(row); break;
            default:
                if (cmd != Command.None) Say($"{cmd} does nothing here.");
                break;
        }
        Top.EnsureVisible(BodyRows);
        return EditorEffect.None;
    }

    /// <summary>The top-level record a row stands for: a record list row, or the record a form shows.</summary>
    private RecordRef? CurrentRecord(int row)
    {
        if (Top.View.RecordAt(row) is RecordRef r) return r;
        if (Top.View is RecordFormView form)
        {
            // A form of a top-level record ("/17" in a v1 file, "/buses/3" in a v2 network file).
            RecordRef f = CaseOps.RefOf(Session.Documents[form.Document], form.ObjectPointer);
            if (f.Index >= 0 && Session.CollectionAt(f.Document, f.ArrayPointer) is not null) return f;
        }
        return null;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Add
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Add a record where the view says (AddContext): ask for each required member the context does not supply (and
    /// the name, optional), then add. A new bus always gets id = largest + 1: the id is preset before the questions
    /// are built, so it is not asked for. To give it a particular id, add the bus and then edit (or rename, R) its id.
    /// </summary>
    private void Add(int row)
    {
        if (Top.View.AddHere(row) is not AddContext ctx) { Say("A (or Ins) adds a record to a list; open a list first."); return; }
        CaseDocument d = Session.Documents[ctx.Document];
        RecordShape? shape = CaseOps.ItemShape(Session, d, ctx.ArrayPointer);
        if (shape is null) { Say("This list does not hold records.", true); return; }

        var values = new List<KeyValuePair<string, string>>(ctx.Presets);
        if (CaseOps.KeyField(Session, ctx.Document, ctx.ArrayPointer) == "id" && !values.Any(v => v.Key == "id"))
            values.Add(new("id", (ReferenceIndex.Build(Session).MaxBusId + 1).ToString(CultureInfo.InvariantCulture)));

        var ask = shape.Fields.Where(f => f.Required && !values.Any(v => v.Key == f.Name)).ToList();
        if (shape.Field("name") is FieldSpec name && !name.Required && !values.Any(v => v.Key == "name")) ask.Insert(0, name);
        AskFields(shape, ctx, values, ask, 0);
    }

    private void AskFields(RecordShape shape, AddContext ctx, List<KeyValuePair<string, string>> values, List<FieldSpec> ask, int next)
    {
        if (next >= ask.Count)
        {
            OpResult r = CaseOps.AddRecord(Session, ctx.Document, ctx.ArrayPointer, ctx.InsertAt, values);
            Report(r);
            return;
        }
        FieldSpec f = ask[next];
        string label = $"New {shape.Title.ToLowerInvariant()}: {f.Title}{(f.Required ? "" : " (Enter skips)")} =";
        AskText(label, "", text =>
        {
            if (text.Length == 0 && f.Required) { Say($"{f.Title} is required (Esc abandons the add).", true); return false; }
            if (text.Length > 0) values.Add(new(f.Name, text));
            AskFields(shape, ctx, values, ask, next + 1);   // the next question, or the add itself
            return true;
        });
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Delete
    // ---------------------------------------------------------------------------------------------------------------

    private void Delete(int row)
    {
        // A record of any list, top-level or nested (a boundary branch in its own list), or the record a form shows.
        if (CurrentRecord(row) is not RecordRef r) { Say("X (or F8) deletes a record of a list."); return; }
        CaseDocument d = Session.Documents[r.Document];
        JsonSourceNode? rec = d.Exists ? d.Text.Resolve(r.Pointer) : null;
        if (rec is null) { Say("There is no record here.", true); return; }
        string what = RecordRead.Describe(d, rec, r.Index);
        RecordRef target = r;
        AskConfirm($"Delete {what}? Y = delete, N = keep.", "YN", c =>
        {
            if (c == 'Y') Report(CaseOps.DeleteRecord(Session, target));
            else Say("Kept.");
            return EditorEffect.None;
        });
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Rename
    // ---------------------------------------------------------------------------------------------------------------

    private void Rename(int row)
    {
        if (Top.View.StationAt(row) is Station st)
        {
            AskText($"Rename station '{st.DisplayName}' ({st.Units.Count.ToString(CultureInfo.InvariantCulture)} units) to:", st.Name ?? "", text =>
            {
                Report(CaseOps.RenameStation(Session, st, text));
                return true;
            });
            return;
        }
        if (CurrentRecord(row) is not RecordRef r || CaseOps.KeyField(Session, r.Document, r.ArrayPointer) is not string key)
        {
            Say("R renames a station, or the key of a bus, line, transformer or asset class.");
            return;
        }
        CaseDocument d = Session.Documents[r.Document];
        JsonSourceNode? rec = d.Text.Resolve(r.Pointer);
        JsonSourceNode? current = rec is null ? null : RecordRead.Member(rec, key, d.KeyComparer);
        string now = current is null ? "" : current.Kind == JsonSourceKind.String ? current.StringValue! : d.Text.RawText(current);
        AskText($"New {key} (every reference follows):", now, text =>
        {
            OpResult preview = CaseOps.RenameKey(Session, r, text, dryRun: true);
            if (!preview.Ok) { Say(preview.Message, true); return false; }
            if (preview.Message.EndsWith("unchanged.", StringComparison.Ordinal)) { Say(preview.Message); return true; }
            AskConfirm(preview.Message + " Go ahead? Y/N", "YN", c =>
            {
                if (c == 'Y') Report(CaseOps.RenameKey(Session, r, text));
                else Say("Not renamed.");
                return EditorEffect.None;
            });
            return true;
        });
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Find, go to, jump, where used
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Every record in every section whose name (or transformer, for a tap changer) contains the text, ignoring case.</summary>
    private View FindView(string text)
    {
        var lines = new List<string>();
        var targets = new List<(int Doc, string Pointer)?>();
        foreach (RecordCollection c in Session.Collections)
        {
            CaseDocument d = Session.Documents[c.Document];
            if (Session.ArrayOf(c) is not JsonSourceNode root) continue;
            for (int i = 0; i < root.Elements.Count; i++)
            {
                JsonSourceNode rec = root.Elements[i];
                string? name = RecordRead.Str(rec, "name", d.KeyComparer) ?? RecordRead.Str(rec, "transformer", d.KeyComparer);
                if (name is null || !name.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
                lines.Add(RecordRead.Describe(d, rec, i, d.Spec is null ? c.ArrayPointer : ""));
                targets.Add((d.Id, c.ArrayPointer + "/" + i.ToString(CultureInfo.InvariantCulture)));
            }
        }
        return new TextListView(Session, $"Find '{text}'", "record", lines, targets);
    }

    private bool GoToBus(int id)
    {
        ReferenceIndex ix = ReferenceIndex.Build(Session);
        if (!ix.Buses.TryGetValue(id, out RecordRef bus)) { Say($"There is no bus {id.ToString(CultureInfo.InvariantCulture)}.", true); return false; }
        Push(new RecordFormView(Session, bus.Document, bus.Pointer, "bus " + id.ToString(CultureInfo.InvariantCulture)));
        Say("");
        return true;
    }

    private void Jump(int row)
    {
        if (Top.View.FieldAt(row) is not FormField f || !f.Spec.IsBusReference) { Say("J jumps from a bus field (bus, from, to, controlled bus) to that bus."); return; }
        CaseDocument d = Session.Documents[f.Document];
        JsonSourceNode? obj = d.Text.Resolve(f.ObjectPointer);
        if (obj is null || RecordRead.Int(obj, f.Spec.Name, d.KeyComparer) is not int id) { Say("The field holds no bus id.", true); return; }
        GoToBus(id);
    }

    private void WhereUsed(int row)
    {
        if (CurrentRecord(row) is not RecordRef r) { Say("W lists what refers to a record; select one first."); return; }
        IReadOnlyList<ReferenceIndex.Referrer> refs = CaseOps.ReferrersOf(Session, r);
        CaseDocument d = Session.Documents[r.Document];
        string what = RecordRead.Describe(d, d.Text.Resolve(r.Pointer)!, r.Index);
        if (refs.Count == 0) { Say($"Nothing refers to {what}."); return; }
        Push(new TextListView(Session, "Where used: " + what, $"{refs.Count.ToString(CultureInfo.InvariantCulture)} reference(s); Enter opens one",
            refs.Select(x => x.What + "  (" + x.Field + ")").ToList(),
            refs.Select(x => ((int, string)?)(x.Record.Document, x.Record.Pointer)).ToList()));
        Say("");
    }
}
