using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GridSim.Ui.Models;

namespace GridSim.App.Dialogs;

/// <summary>
/// The case picker (File > Open case...). Shown with <c>ShowDialog&lt;ModelSource?&gt;</c>: it closes with the chosen
/// source, or with null when cancelled. Open is disabled until a case is selected, so it can never return nothing
/// after the user pressed Open.
/// </summary>
public sealed partial class OpenCaseWindow : Window
{
    /// <summary>For the AXAML loader and the designer only; the app uses the other constructor.</summary>
    public OpenCaseWindow() : this(Array.Empty<ModelSource>(), -1, Array.Empty<string>()) { }

    /// <summary>
    /// A picker over <paramref name="sources"/>, opening on index <paramref name="selectedIndex"/> (-1: the first, if
    /// any), listing the folders the scan skipped in <paramref name="skipped"/>.
    /// </summary>
    public OpenCaseWindow(IReadOnlyList<ModelSource> sources, int selectedIndex, IReadOnlyCollection<string> skipped)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(skipped);
        InitializeComponent();
        Sources.ItemsSource = sources;
        if (sources.Count > 0)
            Sources.SelectedIndex = selectedIndex >= 0 && selectedIndex < sources.Count ? selectedIndex : 0;
        EmptyText.IsVisible = sources.Count == 0;
        if (skipped.Count > 0)
        {
            SkippedText.Text = "Folders that could not be read were skipped:" + Environment.NewLine + string.Join(Environment.NewLine, skipped);
            SkippedText.IsVisible = true;
        }
        OpenButton.IsEnabled = Sources.SelectedItem is not null;
        Sources.SelectionChanged += (_, _) => OpenButton.IsEnabled = Sources.SelectedItem is not null;
    }

    /// <summary>The list control (for tests).</summary>
    public ListBox SourceList => Sources;

    /// <summary>Close with the selected case (also what the Open button does).</summary>
    public void Accept()
    {
        if (Sources.SelectedItem is ModelSource chosen) Close(chosen);
    }

    private void Open_Click(object? sender, RoutedEventArgs e) => Accept();

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void Sources_DoubleTapped(object? sender, TappedEventArgs e) => Accept();
}
