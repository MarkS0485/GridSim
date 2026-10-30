using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;
using GridSim.SchemaEditor.Ui.Views;

namespace GridSim.SchemaEditor.Ui;

/// <summary>What the editor is doing with the keyboard.</summary>
internal enum EditorMode
{
    /// <summary>Moving around the hierarchy; keys are commands.</summary>
    Browse,

    /// <summary>Editing a field's value in place.</summary>
    EditField,

    /// <summary>Typing an answer on the message line (rename, filter, go to, add).</summary>
    Prompt,

    /// <summary>A yes / no (/ cancel) question on the message line.</summary>
    Confirm,
}

/// <summary>Side effects the editor asks the host for; everything else it does is in memory.</summary>
internal enum EditorEffect
{
    None,
    Save,
    SaveConfirmed,
    Check,
    Quit,
}

/// <summary>
/// The editor's state machine: one <see cref="Handle"/> per key, which changes the in-memory state (the view stack,
/// the cursor, the mode, the message) and may run deterministic <see cref="CaseSession"/> operations, and returns the
/// effect the host must run (save, check, quit). It never touches the disk or the console, so a test drives it with a
/// list of keys and reads the screen back from the <see cref="Renderer"/>.
/// </summary>
internal sealed partial class Editor
{
    private Func<string, bool>? _promptAccept;
    private Func<char, EditorEffect>? _confirm;
    private string _confirmKeys = "YN";

    public Editor(CaseSession session, string displayPath)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(displayPath);
        Session = session;
        DisplayPath = displayPath;
        Stack.Add(new Frame(session.Format == CaseFormat.V1Folder ? new RootView(session) : new V2RootView(session)));
        Message = OpeningMessage(session);
    }

    /// <summary>The open case.</summary>
    public CaseSession Session { get; }

    /// <summary>The case path as the user gave it (for the title bar).</summary>
    public string DisplayPath { get; }

    /// <summary>The view stack; [0] is the root.</summary>
    public List<Frame> Stack { get; } = new();

    /// <summary>The frame on top of the stack.</summary>
    public Frame Top => Stack[^1];

    /// <summary>The keyboard mode.</summary>
    public EditorMode Mode { get; private set; } = EditorMode.Browse;

    /// <summary>The line being edited (EditField and Prompt modes).</summary>
    public LineEditor? Line { get; private set; }

    /// <summary>The field being edited (EditField mode).</summary>
    public FormField? Editing { get; private set; }

    /// <summary>The label shown before the prompt text (Prompt mode).</summary>
    public string PromptLabel { get; private set; } = "";

    /// <summary>The question shown in Confirm mode.</summary>
    public string ConfirmText { get; private set; } = "";

    /// <summary>The message line text.</summary>
    public string Message { get; private set; }

    /// <summary>True when the message reports a refusal or failure (drawn bold).</summary>
    public bool MessageIsError { get; private set; }

    /// <summary>The number of list rows on screen (set by the host from the window height).</summary>
    public int BodyRows { get; set; } = 20;

    /// <summary>True once the editor should exit.</summary>
    public bool Finished { get; set; }

    /// <summary>True when a save was asked for on the way out: quit once it succeeds.</summary>
    public bool QuitAfterSave { get; set; }

    /// <summary>The last save or check report.</summary>
    public SaveReport? LastReport { get; set; }

    private bool _bundledWarned;

    private static string OpeningMessage(CaseSession s)
    {
        var parts = new List<string>();
        if (s.Preflight.LoaderError is not null)
            parts.Add((s.Format == CaseFormat.V1Folder ? "The loader refuses this case as it is: " : "The v2 checks find problems: ") + s.Preflight.LoaderError.Split('\n')[0].Trim());
        if (s.Preflight.SchemaErrorCount > 0) parts.Add($"{s.Preflight.SchemaErrorCount} schema problem(s) (F9 lists them).");
        if (s.LeftoverFiles.Count > 0) parts.Add($"Leftover from an interrupted save: {string.Join(", ", s.LeftoverFiles)}.");
        return parts.Count == 0 ? "Case opened. F1 lists the keys." : string.Join(" ", parts);
    }

    /// <summary>Set the message line.</summary>
    public void Say(string message, bool error = false)
    {
        Message = message;
        MessageIsError = error;
    }

    /// <summary>Show the outcome of an operation; refusals are errors. A first change to a bundled case adds a note once (risk R7).</summary>
    public void Report(OpResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        string text = r.Message;
        if (r.Changed && Session.IsBundled && !_bundledWarned)
        {
            _bundledWarned = true;
            text += " Note: this is a Bundled Network; tests and docs pin its numbers.";
        }
        Say(text, !r.Ok);
        AfterChange(r.Select);
    }

    /// <summary>Ask a question; <paramref name="keys"/> are the accepted answers (upper case), Esc answers 'C' or else 'N'.</summary>
    public void AskConfirm(string question, string keys, Func<char, EditorEffect> answer)
    {
        ConfirmText = question;
        _confirmKeys = keys;
        _confirm = answer;
        Mode = EditorMode.Confirm;
    }

    /// <summary>Ask for a line of text on the message line; <paramref name="accept"/> returns false to keep the prompt open.</summary>
    public void AskText(string label, string initial, Func<string, bool> accept)
    {
        PromptLabel = label;
        Line = new LineEditor(initial, replaceOnFirstKey: false);
        _promptAccept = accept;
        Mode = EditorMode.Prompt;
    }

    /// <summary>Push a view.</summary>
    public void Push(View view) => Stack.Add(new Frame(view));

    /// <summary>Handle one key.</summary>
    public EditorEffect Handle(ConsoleKeyInfo key)
    {
        switch (Mode)
        {
            case EditorMode.EditField:
                return HandleEdit(key);
            case EditorMode.Prompt:
            {
                LineResult r = Line!.Handle(key);
                if (r == LineResult.Cancelled) { Mode = EditorMode.Browse; Line = null; Say("Cancelled."); }
                else if (r == LineResult.Accepted)
                {
                    // The answer may open a further question (the next field of an Add, a confirm), so the prompt's
                    // own state is captured first and only cleared when nothing new took its place.
                    Func<string, bool> accept = _promptAccept!;
                    LineEditor line = Line;
                    string label = PromptLabel;
                    Mode = EditorMode.Browse;
                    bool done = accept(line.Text);
                    if (!done)
                    {
                        // Refused answer: the same prompt stays open with the text, so it can be corrected.
                        Mode = EditorMode.Prompt;
                        Line = line;
                        PromptLabel = label;
                        _promptAccept = accept;
                    }
                    else if (Mode != EditorMode.Prompt && Mode != EditorMode.EditField) Line = null;
                }
                return EditorEffect.None;
            }
            case EditorMode.Confirm:
            {
                char c = char.ToUpperInvariant(key.KeyChar);
                if (key.Key == ConsoleKey.Escape) c = _confirmKeys.Contains('C', StringComparison.Ordinal) ? 'C' : 'N';
                if (!_confirmKeys.Contains(c, StringComparison.Ordinal)) return EditorEffect.None;
                Mode = EditorMode.Browse;
                return _confirm!(c);
            }
            default:
                return HandleBrowse(KeyMap.Lookup(key));
        }
    }

    private EditorEffect HandleBrowse(Command cmd)
    {
        Frame top = Top;
        int row = top.CurrentRow;
        // Moving clears the last message, so the line shows the highlighted row's help again.
        if (cmd is Command.Up or Command.Down or Command.PageUp or Command.PageDown or Command.Home or Command.End) Say("");
        switch (cmd)
        {
            case Command.Up: top.Cursor--; break;
            case Command.Down: top.Cursor++; break;
            case Command.PageUp: top.Cursor -= Math.Max(1, BodyRows - 1); break;
            case Command.PageDown: top.Cursor += Math.Max(1, BodyRows - 1); break;
            case Command.Home: top.Cursor = 0; break;
            case Command.End: top.Cursor = int.MaxValue / 2; break;
            case Command.Open: OpenRow(row); break;
            case Command.Back:
                if (Stack.Count > 1) { Stack.RemoveAt(Stack.Count - 1); Say(""); }
                else return HandleBrowse(Command.Quit);
                break;
            case Command.EditKeep:
                if (top.View.FieldAt(row) is FormField f) BeginEdit(f, replace: false);
                else Say("F2 edits a field of a form; open a record first.");
                break;
            case Command.Toggle: Toggle(row); break;
            case Command.Picker: Pick(row); break;
            case Command.Reset:
                if (top.View.FieldAt(row) is FormField rf) Report(CaseOps.ResetField(Session, rf.Document, rf.ObjectPointer, rf.Spec.Name));
                else Say("Del resets a field of a form to absent.");
                break;
            case Command.Undo:
                if (!Session.CanUndo) Say("Nothing to undo.");
                else { string d = Session.Undo(); Say("Undone: " + d + " (Shift+U redoes)."); AfterChange(null); }
                break;
            case Command.Redo:
                if (!Session.CanRedo) Say("Nothing to redo.");
                else { string d = Session.Redo(); Say("Redone: " + d); AfterChange(null); }
                break;
            case Command.Normalise: Report(CaseOps.NormaliseKeys(Session)); break;
            case Command.Check: return EditorEffect.Check;
            case Command.Save: return EditorEffect.Save;
            case Command.Quit:
                if (!Session.IsDirty) { Finished = true; return EditorEffect.Quit; }
                // Short enough for an 80-column message line; the title bar names the modified files.
                AskConfirm($"Unsaved changes ({Session.DirtyDocuments.Count().ToString(System.Globalization.CultureInfo.InvariantCulture)} file(s)). Save before quitting? Y yes, N no, Esc cancel.",
                    "YNC", c =>
                    {
                        if (c == 'Y') { QuitAfterSave = true; return EditorEffect.Save; }
                        if (c == 'N') { Finished = true; return EditorEffect.Quit; }
                        Say("Quit cancelled.");
                        return EditorEffect.None;
                    });
                break;
            case Command.Help:
                Push(TextListView.Plain(Session, "Help", "key      what it does", KeyMap.Bindings.Select(b => b.KeyText.PadRight(9) + b.Help).ToList()));
                break;
            default:
                return HandleMore(cmd, row);
        }
        top.EnsureVisible(BodyRows);
        return EditorEffect.None;
    }

    /// <summary>Commands added by later slices (inventory commands); here only the ones not handled above.</summary>
    private partial EditorEffect HandleMore(Command cmd, int row);

    private void OpenRow(int row)
    {
        if (row < 0) { Say("The list is empty."); return; }
        View v = Top.View;
        if (v is PickerView picker) { Report(picker.Pick(row)); Stack.RemoveAt(Stack.Count - 1); return; }
        if (v is RecordFormView form && !form.Descends(row))
        {
            if (form.FieldAt(row) is FormField f) BeginEdit(f, replace: true);
            else Say("This member is not in the schema; it cannot be edited here.", true);
            return;
        }
        View? next = v.Open(row);
        if (next is null) { Say("Nothing to open here."); return; }
        Push(next);
        Say("");
    }

    /// <summary>Start editing a field in place. Enter: typing replaces the value; F2: the current text is kept.</summary>
    private void BeginEdit(FormField f, bool replace)
    {
        CaseDocument d = Session.Documents[f.Document];
        JsonSourceNode? obj = d.Exists ? d.Text.Resolve(f.ObjectPointer) : null;
        JsonSourceNode? v = obj is { Kind: JsonSourceKind.Object } ? obj.FindMember(f.Spec.Name, d.KeyComparer)?.Value : null;
        string text = v switch
        {
            null => "",
            { Kind: JsonSourceKind.String } => v.StringValue!,
            // One line for the one-line editor: a space where the layout would break a line. With "\n" the newlines
            // reached the screen buffer, which draws control characters as '?', so the user saw and edited "{?  ...".
            { IsContainer: true } => JsonLayout.Format(JsonFresh.FromElement(d.Text.ElementOf(v)), null, "", " "),
            _ => d.Text.RawText(v),
        };
        Editing = f;
        Line = new LineEditor(text, replace);
        Mode = EditorMode.EditField;
        Say(f.Spec.Title + ": " + (v is null ? "absent (" + f.Spec.AbsentText + ")" : "Enter accepts, Esc cancels") + ".");
    }

    private EditorEffect HandleEdit(ConsoleKeyInfo key)
    {
        LineResult r = Line!.Handle(key);
        if (r == LineResult.Cancelled) { Mode = EditorMode.Browse; Line = null; Editing = null; Say("Edit cancelled."); return EditorEffect.None; }
        if (r == LineResult.Accepted)
        {
            FormField f = Editing!;
            OpResult res = CaseOps.SetField(Session, f.Document, f.ObjectPointer, f.Spec.Name, Line.Text);
            if (!res.Ok) { Say(res.Message, true); return EditorEffect.None; }   // stay in the edit so it can be fixed
            Mode = EditorMode.Browse;
            Line = null;
            Editing = null;
            Report(res);
        }
        return EditorEffect.None;
    }

    private void Toggle(int row)
    {
        if (Top.View.FieldAt(row) is not FormField f) { Say("Space toggles a yes / no field or steps a list field of a form."); return; }
        CaseDocument d = Session.Documents[f.Document];
        JsonSourceNode? obj = d.Exists ? d.Text.Resolve(f.ObjectPointer) : null;
        JsonSourceNode? v = obj is { Kind: JsonSourceKind.Object } ? obj.FindMember(f.Spec.Name, d.KeyComparer)?.Value : null;
        switch (f.Spec.Kind)
        {
            case FieldKind.Boolean:
            {
                bool current = v is null ? f.Spec.DefaultLiteral == "true" : v.Kind == JsonSourceKind.True;
                Report(CaseOps.SetField(Session, f.Document, f.ObjectPointer, f.Spec.Name, current ? "false" : "true"));
                break;
            }
            case FieldKind.Enum:
            {
                IReadOnlyList<string> values = f.Spec.EnumTexts;
                string? now = v is null ? null : v.Kind == JsonSourceKind.String ? v.StringValue : d.Text.RawText(v);
                int at = now is null ? -1 : values.ToList().IndexOf(now);
                Report(CaseOps.SetField(Session, f.Document, f.ObjectPointer, f.Spec.Name, values[(at + 1) % values.Count]));
                break;
            }
            default:
                Say("Space toggles yes / no fields and steps list fields; Enter edits this one.");
                break;
        }
    }

    private void Pick(int row)
    {
        if (Top.View.FieldAt(row) is not FormField f || f.Spec.Kind is not (FieldKind.Enum or FieldKind.Boolean))
        {
            Say("F4 / P picks a value of a list or yes / no field.");
            return;
        }
        IReadOnlyList<string> values = f.Spec.Kind == FieldKind.Boolean ? new[] { "true", "false" } : f.Spec.EnumTexts;
        Push(new PickerView(Session, f, values));
        Say("Enter picks the value, Esc goes back without changing it.");
    }

    /// <summary>
    /// After any change: leave views whose subject is gone (a deleted record), clamp cursors, and make the top list's
    /// selection follow the record an operation moved or created.
    /// </summary>
    public void AfterChange(RecordRef? select)
    {
        while (Stack.Count > 1 && Top.View.IsGone) Stack.RemoveAt(Stack.Count - 1);
        if (select is RecordRef r)
        {
            int at = Top.View.RowOf(r);
            if (at >= 0) Top.SelectRow(at);
        }
        Top.EnsureVisible(BodyRows);
    }
}

/// <summary>The values of a list (or yes / no) field; Enter sets the field to the highlighted value.</summary>
internal sealed class PickerView : View
{
    private readonly FormField _field;
    private readonly IReadOnlyList<string> _values;

    public PickerView(CaseSession session, FormField field, IReadOnlyList<string> values) : base(session)
    {
        _field = field;
        _values = values;
    }

    public override string Crumb => "pick " + _field.Spec.Name;

    public override string Header => "value";

    protected override int RowCount() => _values.Count;

    protected override string RowText(int i) => _values[i];

    /// <summary>Set the field to value <paramref name="i"/>.</summary>
    public OpResult Pick(int i) => CaseOps.SetField(Session, _field.Document, _field.ObjectPointer, _field.Spec.Name, _values[i]);
}
