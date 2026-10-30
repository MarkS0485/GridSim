// HOW IT IS ATTACHED (UI programme Phase 1.4; integrated into the main window 2026-10-20). The main window's NETWORK
// tab is this view (x:Name NetworkDashboard), showing the window's one dashboard:
//
//     NetworkDashboard.Host = viewModel.DashboardHost;   // the host MainWindowViewModel made and attached to its Run
//     NetworkDashboard.ShowsWindowParts = false;         // the window shows the lamp, clock and banner over every tab
//
// with the network map in its MapSlot (DashboardView.Map). The window's view model owns the host and attaches it to the
// run pipeline before the first case is opened, so the dashboard sees every load; the host is the only path by which a
// frame reaches the dashboard and, through its tab hooks, every tab. Used on its own (a test, a preview), the view makes
// a host of its own with no tabs behind it; DashboardView.Attach(run) then feeds it. The ReturnToLiveRequested and
// OpenInExplorerRequested events are the two WPF buttons whose actions belong to the window: since UI programme slice
// 1.8 the main window handles RETURN TO LIVE (the GDA bridge session's ReturnToLive); OPEN IN EXPLORER opens the
// ASSETS tab, which is gated, so nothing happens on that click until the window handles it.

using Avalonia.Controls;
using Avalonia.Interactivity;
using GridSim.App.Run;

namespace GridSim.App.Dashboard;

/// <summary>
/// The dashboard tiles of the WPF window (UI programme Phase 1.4) as one self-contained view: the status lamp and
/// clock, the NETWORK tab's telemetry column, map overlays, what-if panel, boundaries, alarms and event log, and the
/// STATE NOT VALID banner. Its data context is the <see cref="Host"/>'s model; <see cref="Attach"/> starts feeding it
/// from a run pipeline. See the comment at the top of this file for how the main window uses it.
/// </summary>
public partial class DashboardView : UserControl
{
    private DashboardHost _host = new();
    private bool _showsWindowParts = true;

    /// <summary>A dashboard showing the model's starting values (INITIALISING, " - " tiles) until it is attached.</summary>
    public DashboardView()
    {
        InitializeComponent();
        DataContext = _host.Model;
    }

    /// <summary>The model every tile binds to (the same object as the data context).</summary>
    public AppDashboardModel Model => _host.Model;

    /// <summary>
    /// The host that feeds the model. Setting it shows that host's model instead (the main window gives the view the
    /// host its view model owns). Swapping away from a host that is still attached is a wiring mistake and throws:
    /// the old host would go on feeding a model nobody sees.
    /// </summary>
    public DashboardHost Host
    {
        get => _host;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(value, _host)) return;
            if (_host.Run is not null)
                throw new InvalidOperationException("The dashboard's current host is still attached to a run pipeline; detach it first.");
            _host = value;
            DataContext = _host.Model;
        }
    }

    /// <summary>
    /// True (the default) when the view shows its own status lamp and clock row and its own STATE NOT VALID banner, as
    /// it must on its own. The main window sets it false: there, as in WPF, the lamp and clock sit in the window's
    /// header and the banner lies over every tab, so the NETWORK tab does not show a second copy of either.
    /// </summary>
    public bool ShowsWindowParts
    {
        get => _showsWindowParts;
        set
        {
            _showsWindowParts = value;
            HeaderStrip.IsVisible = value;
            BannerHost.IsVisible = value;
        }
    }

    /// <summary>The map shown under the centre overlays (the main window puts its network map here).</summary>
    public object? Map
    {
        get => MapSlot.Content;
        set => MapSlot.Content = value;
    }

    /// <summary>Raised by the what-if panel's RETURN TO LIVE button (WPF ReturnToLive_Click: a GDA bridge action).</summary>
    public event EventHandler? ReturnToLiveRequested;

    /// <summary>Raised by the fly-out's OPEN IN EXPLORER button (WPF OpenInExplorer_Click: opens the ASSETS tab).</summary>
    public event EventHandler? OpenInExplorerRequested;

    /// <summary>Start feeding the dashboard from <paramref name="run"/> (see <see cref="DashboardHost.Attach"/>).</summary>
    public void Attach(RunViewModel run) => _host.Attach(run);

    /// <summary>Stop feeding the dashboard; it keeps what it shows (see <see cref="DashboardHost.Detach"/>).</summary>
    public void Detach() => _host.Detach();

    // WPF ClearSelection_Click: the fly-out's close button.
    private void CloseSelection_Click(object? sender, RoutedEventArgs e) => _host.Model.ClearSelection();

    // WPF ClearAlarms_Click: the alarm panel's CLEAR button.
    private void ClearAlarms_Click(object? sender, RoutedEventArgs e) => _host.Model.ClearAlarms();

    // WPF ReturnToLive_Click and OpenInExplorer_Click act on the bridge and the ASSETS tab, which the window owns.
    private void ReturnToLive_Click(object? sender, RoutedEventArgs e) => ReturnToLiveRequested?.Invoke(this, EventArgs.Empty);

    private void OpenInExplorer_Click(object? sender, RoutedEventArgs e) => OpenInExplorerRequested?.Invoke(this, EventArgs.Empty);
}
