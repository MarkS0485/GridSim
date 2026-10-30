// Test oracle for UiTabsThirdBatchParityTests (UI programme Phase 1.6, third tab batch). NOT production code.
//
// Below are the two methods of the WPF AuditLogWindow that decide what the audit-log window shows, OpenAndShow and
// ShowVerdict, exactly as they were before their logic moved to src/GridSim.Ui/Tabs/AuditLog
// (src/GridSim.Wpf/AuditLogWindow.cs at 2.0.28), copied mechanically from git. The window around them was WPF
// controls built in code; it is replaced by the smallest stand-ins those two methods read and write, nothing more:
//   - _count, _head and _entries are text boxes: only their Text is read or written;
//   - _verdict is a text block: only its Text and Foreground are written;
//   - the open-file dialog returns the file name the test chose (or is cancelled), and records the Title and Filter it
//     was given, so the dialog's wording is compared too;
//   - TryFindResource returns the app theme's brush for "Green" or "Red" (Theme.xaml: #2FD27F, #FF5B5B) and its
//     "Text" brush as the verdict's starting colour (#E6EDF3), as the WPF window found them in the running app;
//   - "this" is passed to ShowDialog as the owner, as before (ignored by the stand-in).
// The two method bodies are verbatim, so every verdict, error text and entry line is the old one.

using System.IO;
using GridSim.Core.Diagnostics;
using GridSim.Core.Presentation;

namespace GridSim.Tests.Ui.OldTabsC;

/// <summary>The stand-in for the old window's controls and dialog (see the file header).</summary>
public sealed class AuditLogWindow
{
    /// <summary>A WPF TextBox: only the text.</summary>
    public sealed class TextBox { public string Text { get; set; } = ""; }

    /// <summary>A WPF TextBlock: only the text and the foreground brush.</summary>
    public sealed class TextBlock { public string Text { get; set; } = ""; public Brush? Foreground { get; set; } }

    /// <summary>The theme brushes the window looked up (the WPF Theme.xaml bytes).</summary>
    private static readonly Dictionary<string, Brush> Theme = new(StringComparer.Ordinal)
    {
        ["Green"] = new SolidColorBrush(Palette.FromHex("#2FD27F")),
        ["Red"] = new SolidColorBrush(Palette.FromHex("#FF5B5B")),
        ["Text"] = new SolidColorBrush(Palette.FromHex("#E6EDF3")),
    };

    private readonly TextBox _count = new();
    private readonly TextBox _head = new();
    private readonly TextBlock _verdict = new();
    private readonly TextBox _entries = new();

    /// <summary>The file the next dialog returns, or null to cancel it.</summary>
    public string? NextFile { get; set; }

    /// <summary>The title and filter the last dialog was opened with (null before one opened).</summary>
    public string? DialogTitle { get; private set; }
    public string? DialogFilter { get; private set; }

    /// <summary>How many times the window opened its dialog.</summary>
    public int Dialogs { get; private set; }

    /// <summary>The window starts with the theme's Text brush on the verdict, as the old constructor set it.</summary>
    public AuditLogWindow() { if (TryFindResource("Text") is Brush text) _verdict.Foreground = text; }

    public string CountText { get => _count.Text; set => _count.Text = value; }
    public string HeadText { get => _head.Text; set => _head.Text = value; }
    public string Verdict => _verdict.Text;
    public Brush? VerdictBrush => _verdict.Foreground;
    public string Entries => _entries.Text;

    /// <summary>Press OPEN LOG... (the old click handler).</summary>
    public void Open() => OpenAndShow();

    private static object? TryFindResource(string key) => Theme.TryGetValue(key, out Brush? b) ? b : null;

    /// <summary>The stand-in for Microsoft.Win32.OpenFileDialog.</summary>
    private sealed class OpenFileDialogShim(AuditLogWindow owner)
    {
        public string Title { get; set; } = "";
        public string Filter { get; set; } = "";
        public string FileName { get; private set; } = "";

        public bool? ShowDialog(AuditLogWindow _)
        {
            owner.DialogTitle = Title;
            owner.DialogFilter = Filter;
            owner.Dialogs++;
            if (owner.NextFile is null) return false;
            FileName = owner.NextFile;
            return true;
        }
    }

    // ---------------- below: verbatim from src/GridSim.Wpf/AuditLogWindow.cs at 2.0.28, except that the dialog is
    // ---------------- constructed as "new OpenFileDialogShim(this)" in place of "new Microsoft.Win32.OpenFileDialog"

    // Ask for the file, verify it through Core, and show exactly what AuditLogDisplay returns: entries only when the
    // whole log verified; otherwise the verdict alone.
    private void OpenAndShow()
    {
        _entries.Text = "";
        if (!AuditLogDisplay.TryParseAnchor(_count.Text, _head.Text, out AuditAnchor? anchor, out string? anchorError))
        {
            ShowVerdict($"NOT CHECKED: {anchorError}", ok: false);
            return;
        }
        var dlg = new OpenFileDialogShim(this)
        {
            Title = "Choose a gridsim-audit/1 log",
            Filter = "Audit logs (*.jsonl;*.log;*.json)|*.jsonl;*.log;*.json|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        AuditLogDisplayResult result;
        try
        {
            result = AuditLogDisplay.FromFile(dlg.FileName, anchor);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Missing, unreadable or oversized: an error, never an empty clean log.
            ShowVerdict($"NOT CHECKED: cannot read '{dlg.FileName}': {ex.Message}", ok: false);
            return;
        }
        if (result.Verified) _entries.Text = string.Join(Environment.NewLine, result.EntryLines);
        ShowVerdict(result.Verdict, result.Verified);
    }

    private void ShowVerdict(string text, bool ok)
    {
        _verdict.Text = text;
        Brush? brush = TryFindResource(ok ? "Green" : "Red") as Brush;
        if (brush is not null) _verdict.Foreground = brush;
    }
}
