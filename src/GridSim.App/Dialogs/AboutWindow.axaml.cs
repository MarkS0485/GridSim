using Avalonia.Controls;
using Avalonia.Interactivity;
using GridSim.Ui.Shell;

namespace GridSim.App.Dialogs;

/// <summary>
/// The About box (UI programme Phase 1.1): <see cref="AboutInfo"/>'s lines, plus buttons that open the shipped licence
/// and third-party notices (the same viewers as the Help menu items).
/// </summary>
public sealed partial class AboutWindow : Window
{
    private readonly Action? _showLicence;
    private readonly Action? _showNotices;

    /// <summary>For the AXAML loader and the designer only; the app uses the other constructor.</summary>
    public AboutWindow() : this(AboutInfo.Lines("(not resolved)"), null, null) { }

    /// <summary>An About box showing <paramref name="lines"/>; the two buttons run the given viewers.</summary>
    public AboutWindow(IReadOnlyList<string> lines, Action? showLicence, Action? showNotices)
    {
        ArgumentNullException.ThrowIfNull(lines);
        InitializeComponent();
        _showLicence = showLicence;
        _showNotices = showNotices;
        Heading.Text = AboutInfo.Heading;
        AboutText.Text = string.Join(Environment.NewLine, lines);
        ReportNote.Text = AboutInfo.ReportNote;
        LicenceButton.IsEnabled = showLicence is not null;
        NoticesButton.IsEnabled = showNotices is not null;
    }

    /// <summary>The text shown (for tests).</summary>
    public string ShownText => AboutText.Text ?? "";

    private void Licence_Click(object? sender, RoutedEventArgs e) => _showLicence?.Invoke();

    private void Notices_Click(object? sender, RoutedEventArgs e) => _showNotices?.Invoke();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
