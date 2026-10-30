// HOW TO FEED (UI programme Phase 1.6; integrated 2026-10-20). The tab is fed through the dashboard's tab hooks, as in
// WPF (DashboardViewModel.UpdateTabs: InverterFleet.Update(f) on every frame the dashboard takes; the tab keeps no
// time-integrated state, so the WPF ResetTabs hook never reset it). That call is in TabModels.Update, which the window's
// AppDashboardModel runs. The main window hosts the tab bound to that model:
//
//     <tabs:InverterFleetView x:Name="InverterFleetTab" DataContext="{Binding Tabs.InverterFleet}" />
//
// The view no longer feeds itself from the run pipeline (that self-attach was removed with the integration), so a
// frame on which the dashboard's own update throws feeds this tab nothing, as in WPF.

using Avalonia.Controls;
using GridSim.Ui.Tabs.InverterFleet;

namespace GridSim.App.Tabs;

/// <summary>
/// The INVERTER FLEET tab of the WPF window (UI programme Phase 1.6) as one self-contained view. Its data context is
/// <see cref="Model"/>, which the window sets to the tab model its dashboard feeds. See the comment at the top of this
/// file.
/// </summary>
public partial class InverterFleetView : UserControl
{
    /// <summary>A tab showing a fresh model's starting values (OFF, " - ") until it is given its model.</summary>
    public InverterFleetView()
    {
        InitializeComponent();
        DataContext = new InverterFleetModel();
    }

    /// <summary>
    /// The model every value binds to: the data context. A view whose data context is not an
    /// <see cref="InverterFleetModel"/> has been wired wrongly, and reading its model says so (fail loud).
    /// </summary>
    public InverterFleetModel Model
    {
        get => DataContext as InverterFleetModel
            ?? throw new InvalidOperationException("The INVERTER FLEET view has no InverterFleetModel as its DataContext.");
        set => DataContext = value ?? throw new ArgumentNullException(nameof(value));
    }
}
