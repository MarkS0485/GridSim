using GridSim.Core.IO.Json.CaseEditing;

namespace GridSim.SchemaEditor.Ui.Views;

/// <summary>
/// One screen of the hierarchy: a virtualised list (a row count and a row by index, never a materialised screenful)
/// with a breadcrumb segment and a column header. Rows are rebuilt lazily whenever the session's revision moves, so a
/// view never shows stale data after an edit, undo or save.
/// </summary>
internal abstract class View
{
    private long _builtRevision = -1;

    protected View(CaseSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
    }

    /// <summary>The open case.</summary>
    public CaseSession Session { get; }

    /// <summary>This view's part of the breadcrumb ("Generation", "solar", "- solar (113 units)").</summary>
    public abstract string Crumb { get; }

    /// <summary>The column header line.</summary>
    public abstract string Header { get; }

    /// <summary>Number of rows.</summary>
    public int Count
    {
        get
        {
            Refresh();
            return RowCount();
        }
    }

    /// <summary>The display text of row <paramref name="i"/> (no highlight; the renderer adds that).</summary>
    public string Row(int i)
    {
        Refresh();
        return RowText(i);
    }

    /// <summary>True when what this view shows no longer exists (its record was deleted, say); the editor then leaves it.</summary>
    public virtual bool IsGone => false;

    /// <summary>The view Enter (or Right) opens from row <paramref name="i"/>, or null when the row does not open.</summary>
    public virtual View? Open(int i) => null;

    /// <summary>A help line for row <paramref name="i"/> (shown on the message line), or null.</summary>
    public virtual string? HelpLine(int i) => null;

    /// <summary>The record a row stands for (record lists), or null.</summary>
    public virtual RecordRef? RecordAt(int i) => null;

    /// <summary>The field a row stands for (forms), or null.</summary>
    public virtual FormField? FieldAt(int i) => null;

    /// <summary>The row showing a record, or -1 (used to make the selection follow a record after an edit).</summary>
    public virtual int RowOf(RecordRef record) => -1;

    /// <summary>Where Add puts a new record from this view, or null when Add does not apply here.</summary>
    public virtual AddContext? AddHere(int row) => null;

    /// <summary>The station a row stands for (the station list), or null.</summary>
    public virtual Station? StationAt(int i) => null;

    /// <summary>Rebuild cached rows when the session has changed.</summary>
    public void Refresh()
    {
        if (_builtRevision == Session.Revision) return;
        _builtRevision = Session.Revision;
        Rebuild();
    }

    /// <summary>Recompute the rows from the session.</summary>
    protected virtual void Rebuild() { }

    protected abstract int RowCount();

    protected abstract string RowText(int i);

    /// <summary>Fit text into a column: truncated (with no ellipsis, to keep alignment exact) or padded.</summary>
    protected static string Cell(string text, int width)
        => text.Length >= width ? text[..Math.Max(0, width - 1)] + " " : text.PadRight(width);
}

/// <summary>A field of a form row: the document, the object holding it, and the field's spec.</summary>
internal sealed record FormField(int Document, string ObjectPointer, FieldSpec Spec);

/// <summary>
/// Where Add puts a record from a view: the document, the array, the insert position, and values preset by context
/// (in a station: its name and type, and the bus of the selected unit; in a voltage group: its baseKv).
/// </summary>
internal sealed record AddContext(int Document, string ArrayPointer, int InsertAt, IReadOnlyList<KeyValuePair<string, string>> Presets);

/// <summary>A view and where the user is in it.</summary>
internal sealed class Frame
{
    public Frame(View view) => View = view;

    /// <summary>The view.</summary>
    public View View { get; }

    /// <summary>The highlighted position among the visible rows.</summary>
    public int Cursor { get; set; }

    /// <summary>The first visible row on screen.</summary>
    public int Scroll { get; set; }

    /// <summary>The filter text ("" = none): only rows containing it (ignoring case) are visible.</summary>
    public string Filter { get; set; } = "";

    private long _visibleRevision = -1;
    private string _visibleFilter = "";
    private List<int> _visible = new();

    /// <summary>The view rows currently visible (all rows, or those matching the filter), rebuilt when needed.</summary>
    public IReadOnlyList<int> Visible
    {
        get
        {
            View.Refresh();
            if (_visibleRevision != View.Session.Revision || !string.Equals(_visibleFilter, Filter, StringComparison.Ordinal) || (Filter.Length == 0 && _visible.Count != View.Count))
            {
                _visibleRevision = View.Session.Revision;
                _visibleFilter = Filter;
                int n = View.Count;
                _visible = Filter.Length == 0
                    ? Enumerable.Range(0, n).ToList()
                    : Enumerable.Range(0, n).Where(i => View.Row(i).Contains(Filter, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            return _visible;
        }
    }

    /// <summary>The view row under the cursor, or -1 when the list is empty.</summary>
    public int CurrentRow
    {
        get
        {
            IReadOnlyList<int> v = Visible;
            if (v.Count == 0) return -1;
            Cursor = Math.Clamp(Cursor, 0, v.Count - 1);
            return v[Cursor];
        }
    }

    /// <summary>Put the cursor on a view row if it is visible.</summary>
    public void SelectRow(int row)
    {
        IReadOnlyList<int> v = Visible;
        for (int i = 0; i < v.Count; i++)
            if (v[i] == row) { Cursor = i; return; }
    }

    /// <summary>Keep the cursor inside a body of <paramref name="rows"/> lines.</summary>
    public void EnsureVisible(int rows)
    {
        int n = Visible.Count;
        if (n == 0) { Cursor = 0; Scroll = 0; return; }
        Cursor = Math.Clamp(Cursor, 0, n - 1);
        rows = Math.Max(1, rows);
        if (Cursor < Scroll) Scroll = Cursor;
        if (Cursor >= Scroll + rows) Scroll = Cursor - rows + 1;
        Scroll = Math.Clamp(Scroll, 0, Math.Max(0, n - rows));
    }
}
