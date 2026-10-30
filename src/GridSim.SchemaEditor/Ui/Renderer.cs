using System.Globalization;
using GridSim.Core;
using GridSim.Core.Licensing;
using GridSim.SchemaEditor.Ui.Views;

namespace GridSim.SchemaEditor.Ui;

/// <summary>
/// Draws the editor into a <see cref="ScreenBuffer"/> (pure: no Console calls). Layout (SCHEMA-EDITOR-PLAN.md
/// section 6): row 0 title, row 1 breadcrumb, row 2 column headers, rows 3 to H-3 the list (highlighted row in reverse
/// video), row H-2 the message line, row H-1 the key bar. Below 80 x 24 only a one-line notice is drawn.
/// </summary>
internal static class Renderer
{
    /// <summary>The smallest window the editor draws in.</summary>
    public const int MinWidth = 80;

    /// <summary>The smallest window the editor draws in.</summary>
    public const int MinHeight = 24;

    /// <summary>The column where a form's value starts (the in-place edit is drawn there).</summary>
    public const int FormValueColumn = 38;

    /// <summary>True when a window of this size is below the minimum, so only the size notice is drawn (and the host
    /// accepts only the quit keys).</summary>
    public static bool IsTooSmall(int width, int height) => width < MinWidth || height < MinHeight;

    /// <summary>Draw the whole screen.</summary>
    public static void Render(Editor e, ScreenBuffer b)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(b);
        b.Caret = null;
        if (IsTooSmall(b.Width, b.Height))
        {
            b.Put(0, 0, $"Window too small: the case editor needs {MinWidth}x{MinHeight}, this one is {b.Width.ToString(CultureInfo.InvariantCulture)}x{b.Height.ToString(CultureInfo.InvariantCulture)}. Enlarge it (Q quits; other keys are ignored).");
            return;
        }

        // Row 0: title.
        b.FillRow(0, CellStyle.Reverse);
        b.Put(0, 0, Title(e, b.Width), CellStyle.Reverse);

        // Row 1: breadcrumb (the deepest levels are kept when it is too long).
        b.Put(0, 1, Breadcrumb(e.Stack.Select(f => f.View.Crumb).ToList(), b.Width), CellStyle.Bold);

        // Row 2: column headers.
        Frame top = e.Top;
        b.Put(0, 2, top.View.Header, CellStyle.Dim);

        // Body.
        int bodyRows = b.Height - 5;
        e.BodyRows = bodyRows;
        top.EnsureVisible(bodyRows);
        IReadOnlyList<int> visible = top.Visible;
        if (visible.Count == 0)
            b.Put(0, 3, top.Filter.Length > 0 ? $"(no row matches the filter '{top.Filter}')" : "(empty)", CellStyle.Dim);
        for (int k = 0; k < bodyRows && top.Scroll + k < visible.Count; k++)
        {
            int at = top.Scroll + k;
            int y = 3 + k;
            bool current = at == top.Cursor;
            if (current) b.FillRow(y, CellStyle.Reverse);
            b.Put(0, y, top.View.Row(visible[at]), current ? CellStyle.Reverse : CellStyle.Normal);

            // The in-place edit replaces the value cell of the highlighted form row.
            if (current && e.Mode == EditorMode.EditField && e.Line is LineEditor line)
            {
                int room = b.Width - FormValueColumn - 1;
                (string shown, int caret) = Window(line.Text, line.Caret, room);
                b.Put(FormValueColumn, y, new string(' ', room), CellStyle.Normal);
                b.Put(FormValueColumn, y, shown, line.WillReplace ? CellStyle.Reverse : CellStyle.Normal);
                b.Caret = (FormValueColumn + caret, y);
            }
        }

        // Row H-2: message, prompt or question.
        int my = b.Height - 2;
        switch (e.Mode)
        {
            case EditorMode.Prompt when e.Line is LineEditor pl:
            {
                string label = e.PromptLabel + " ";
                (string shown, int caret) = Window(pl.Text, pl.Caret, b.Width - label.Length - 1);
                b.Put(0, my, label, CellStyle.Bold);
                b.Put(label.Length, my, shown);
                b.Caret = (label.Length + caret, my);
                break;
            }
            case EditorMode.Confirm:
                b.Put(0, my, e.ConfirmText, CellStyle.Bold);
                break;
            default:
            {
                string text = e.Message;
                if (string.IsNullOrEmpty(text))
                {
                    int row = top.CurrentRow;
                    text = (row >= 0 ? top.View.HelpLine(row) : null)
                        ?? (visible.Count == 0 ? "" : $"row {(top.Cursor + 1).ToString(CultureInfo.InvariantCulture)}/{visible.Count.ToString(CultureInfo.InvariantCulture)}");
                }
                b.Put(0, my, text, e.MessageIsError ? CellStyle.Bold : CellStyle.Normal);
                break;
            }
        }

        // Row H-1: key bar.
        b.FillRow(b.Height - 1, CellStyle.Reverse);
        b.Put(0, b.Height - 1, KeyBar(e.Mode), CellStyle.Reverse);
    }

    /// <summary>
    /// The title bar text for a screen <paramref name="width"/> columns wide: version, case, format and bus count
    /// always; then the flags that matter most ([BUNDLED], [MODIFIED: files], the modified list shortened to a count
    /// when it does not fit); the licence tier only when there is room for it too.
    /// </summary>
    public static string Title(Editor e, int width)
    {
        ArgumentNullException.ThrowIfNull(e);
        var dirty = e.Session.DirtyDocuments.Select(d => d.FileName).ToList();
        string caseText = $"{e.DisplayPath} ({FormatName(e)})  {e.Session.BusCount.ToString(CultureInfo.InvariantCulture)} buses";
        string head = $"GRIDSIM CASE EDITOR {BuildInfo.Version}  {caseText}";
        string bundled = e.Session.IsBundled ? "  [BUNDLED]" : "";
        string modified = dirty.Count == 0 ? "" : "  [MODIFIED: " + string.Join(", ", dirty) + "]";
        if (head.Length + bundled.Length + modified.Length > width && dirty.Count > 0)
            modified = $"  [MODIFIED: {dirty.Count.ToString(CultureInfo.InvariantCulture)} file(s)]";
        string flags = bundled + modified;
        string tier = "  " + e.Session.Gate.Entitlement.Tier.DisplayName();
        if (head.Length + tier.Length + flags.Length <= width) return head + tier + flags;
        if (head.Length + flags.Length <= width) return head + flags;
        // Still too wide: the flags matter more than the product name, so the head shortens.
        string shortHead = $"GRIDSIM EDITOR {BuildInfo.Version}  {caseText}";
        return (shortHead.Length + flags.Length <= width ? shortHead : caseText) + flags;
    }

    /// <summary>
    /// The breadcrumb, "Case > Generation > ..."; when it is wider than the screen the leading levels are replaced by
    /// "... > " so the deepest (current) levels stay visible.
    /// </summary>
    public static string Breadcrumb(IReadOnlyList<string> crumbs, int width)
    {
        ArgumentNullException.ThrowIfNull(crumbs);
        string full = string.Join(" > ", crumbs);
        if (full.Length <= width) return full;
        for (int skip = 1; skip < crumbs.Count; skip++)
        {
            string tail = "... > " + string.Join(" > ", crumbs.Skip(skip));
            if (tail.Length <= width) return tail;
        }
        return "... > " + crumbs[^1];
    }

    private static string FormatName(Editor e) => e.Session.Format switch
    {
        GridSim.Core.IO.Json.CaseEditing.CaseFormat.V1Folder => "v1",
        _ => "v2",
    };

    /// <summary>The key bar for a mode.</summary>
    public static string KeyBar(EditorMode mode) => mode switch
    {
        EditorMode.EditField or EditorMode.Prompt => "Enter Accept  Esc Cancel  Left/Right Move  Home/End  Bksp/Del Erase",
        EditorMode.Confirm => "Answer with the letters shown  Esc Cancel",
        _ => KeyMap.BrowseBar(),
    };

    /// <summary>The part of a line that fits <paramref name="room"/> columns, keeping the caret visible.</summary>
    internal static (string Shown, int Caret) Window(string text, int caret, int room)
    {
        room = Math.Max(1, room);
        if (text.Length < room) return (text, caret);
        int start = Math.Max(0, caret - room + 1);
        return (text.Substring(start, Math.Min(room, text.Length - start)), caret - start);
    }
}
