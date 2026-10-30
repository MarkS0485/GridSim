using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GridSim.Ui.Tabs.AuditLog;

namespace GridSim.Wpf;

/// <summary>
/// The desktop audit-log viewer (N151): pick a <c>gridsim-audit/1</c> file, optionally enter an external anchor (entry
/// count and head hash), and see the result of Core's <c>AuditLogDisplay</c>, the one display path the web
/// AUDIT LOG tab also uses and whose lines match <c>gridsim audit show</c>. A log that verifies shows every entry and the
/// OK line; a log that does NOT verify shows only the TAMPERED verdict, never the clean prefix (nothing after a broken
/// link can be trusted). A missing or unreadable file shows the error and no entries. Presentation only: the file is
/// read, never written. Built in code (no XAML) because it is one small dialog.
/// <para>UI programme Phase 1.6 (third batch): what the window shows is decided by the platform-neutral
/// <see cref="AuditLogModel"/> (src/GridSim.Ui/Tabs/AuditLog/AuditLogModel.cs), the logic of this window's old
/// OpenAndShow and ShowVerdict moved there line for line, so the Avalonia window shows the same verdicts from the same
/// code. This file keeps the WPF shell only: the controls, the WPF file dialog, and the theme brush for each verdict
/// tone. What the user sees is unchanged.</para>
/// </summary>
public sealed class AuditLogWindow : Window
{
    private readonly AuditLogModel _model = new();
    private readonly TextBox _count = new() { Width = 70, Margin = new Thickness(4, 0, 12, 0) };
    private readonly TextBox _head = new() { Width = 300, Margin = new Thickness(4, 0, 12, 0) };
    private readonly TextBlock _verdict = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBox _entries = new()
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
    };

    /// <summary>Build the dialog, owned by <paramref name="owner"/>.</summary>
    public AuditLogWindow(Window owner)
    {
        Owner = owner;
        Title = AuditLogModel.WindowTitle;
        Width = 1000;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // The app theme's brushes when present (Theme.xaml), plain defaults otherwise.
        if (TryFindResource("Panel") is Brush bg) Background = bg;
        Brush? text = TryFindResource("Text") as Brush;

        var open = new Button { Content = AuditLogModel.OpenButtonText, Padding = new Thickness(8, 2, 8, 2) };
        if (TryFindResource("FlatBtn") is Style flat) open.Style = flat;
        open.Click += (_, _) => OpenAndShow();

        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Children.Add(Label(AuditLogModel.AnchorCountLabel, text));
        bar.Children.Add(_count);
        bar.Children.Add(Label(AuditLogModel.AnchorHeadLabel, text));
        bar.Children.Add(_head);
        bar.Children.Add(open);

        var note = Label(AuditLogModel.Note, text);
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(0, 6, 0, 0);
        if (text is not null) _verdict.Foreground = text;

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(_verdict, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(note);
        root.Children.Add(_verdict);
        root.Children.Add(_entries);   // fills the rest
        Content = root;
    }

    private static TextBlock Label(string s, Brush? fg)
    {
        var t = new TextBlock { Text = s, VerticalAlignment = VerticalAlignment.Center };
        if (fg is not null) t.Foreground = fg;
        return t;
    }

    // Hand the typed anchor to the model, let it ask for the file through the WPF dialog, verify it through Core, and
    // show exactly what the model decided: entries only when the whole log verified; otherwise the verdict alone.
    private void OpenAndShow()
    {
        _model.AnchorCount = _count.Text;
        _model.AnchorHead = _head.Text;
        _model.Open(() =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = AuditLogModel.DialogTitle,
                Filter = AuditLogModel.DialogFilter,
            };
            return dlg.ShowDialog(this) == true ? dlg.FileName : null;
        });
        _entries.Text = _model.Entries;
        if (_model.Tone != AuditVerdictTone.Neutral) ShowVerdict(_model.Verdict, _model.Tone == AuditVerdictTone.Verified);
    }

    // The verdict text, in the theme's green or red when the theme has them (the old foreground stays otherwise).
    private void ShowVerdict(string text, bool ok)
    {
        _verdict.Text = text;
        Brush? brush = TryFindResource(ok ? "Green" : "Red") as Brush;
        if (brush is not null) _verdict.Foreground = brush;
    }
}
