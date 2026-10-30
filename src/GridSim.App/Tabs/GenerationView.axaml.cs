// HOW TO FEED (UI programme Phase 1.6; integrated 2026-10-20). As in WPF, the GENERATION & INERTIA tab is a view of the
// dashboard's own model (the WPF tab bound the window's DashboardViewModel), which the dashboard host feeds from the run
// pipeline. The main window shares it, in one line, after the window's view model has made and attached its host:
//
//     GenerationTab.Model = viewModel.Dashboard;
//
// (GenerationTab is the placed control's x:Name, <tabs:GenerationView x:Name="GenerationTab" />.) The SIMULATE
// LARGEST-UNIT TRIP button runs the model's TripCompare, and the trace refreshes on the model's TripCompared event, so a
// map trip, which runs the swing study on the same model (RunViewModel.TripAtBus calls
// GridSim.Ui.Tabs.Generation.TripSwingStudy.Apply), refreshes this tab too, as in WPF.

using Avalonia.Controls;
using Avalonia.Interactivity;
using GridSim.Ui.Dashboard;

namespace GridSim.App.Tabs;

/// <summary>
/// The GENERATION tab of the WPF window (UI programme Phase 1.6): the headline strip, the generation mix and inertia by
/// class, the import links, the grid as one machine, synchronous vs converter, fossil vs renewable, the stability
/// regime and the TSGB device with its trip compare and frequency-response trace. Its data context is
/// <see cref="Model"/>, the dashboard's model. See the comment at the top of this file for the one line that wires it.
/// </summary>
public partial class GenerationView : UserControl
{
    private DashboardModel _model = new();

    /// <summary>A tab showing a fresh dashboard model's starting values (" - ") until <see cref="Model"/> is set.</summary>
    public GenerationView()
    {
        InitializeComponent();
        DataContext = _model;
        _model.TripCompared += OnTripCompared;
    }

    /// <summary>
    /// The dashboard model the tab shows (the same object as the data context). Setting it moves the trace's
    /// subscription to the new model (WPF: DataContextChanged moved the TripCompared handler) and, when that model has
    /// already run a trip compare, shows it.
    /// </summary>
    public DashboardModel Model
    {
        get => _model;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(value, _model)) return;
            _model.TripCompared -= OnTripCompared;
            _model = value;
            _model.TripCompared += OnTripCompared;
            DataContext = _model;
            if (_model.BaselineResponse is not null) OnTripCompared();
        }
    }

    // WPF OnTripCompared: the trace shows the two responses of the last compare, its limit lines about the case's
    // nominal (N297: the bands the compare was graded against).
    private void OnTripCompared() => FreqTrace.SetResponses(_model.BaselineResponse, _model.TsgbResponse, _model.TripBands);

    // WPF Trip_Click: the largest secured unit's trip, with and without the TSGB device.
    private void Trip_Click(object? sender, RoutedEventArgs e) => _model.TripCompare();
}
