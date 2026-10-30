// HOW TO FEED (UI programme Phase 1.6; integrated 2026-10-20). The tab is fed through the dashboard's tab hooks, as in
// WPF (DashboardViewModel.UpdateTabs: Machine.Update(f) on every frame the dashboard takes; ResetTabs, first on every
// timeline break: Machine.Reset(), the load-bearing reset of its integrators). Those calls are TabModels.Update and
// TabModels.ResetTimeline, which the window's AppDashboardModel runs. The main window hosts the tab bound to that model:
//
//     <tabs:MachineView x:Name="MachineTab" DataContext="{Binding Tabs.Machine}" />
//
// The view no longer feeds itself from the run pipeline (that self-attach was removed with the integration), so a
// frame on which the dashboard's own update throws feeds this tab nothing, as in WPF.

using Avalonia.Controls;
using GridSim.Ui.Tabs.Machine;

namespace GridSim.App.Tabs;

/// <summary>
/// THE MACHINE tab of the WPF window (UI programme Phase 1.6) as one self-contained view: fuel in, the spinning grid
/// and its shaft by voltage, power out, the mechanical and electrical dial banks, the mains clock, the battery and the
/// tap-changer fleet. Its data context is <see cref="Model"/>, which the window sets to the tab model its dashboard
/// feeds. See the comment at the top of this file.
/// </summary>
public partial class MachineView : UserControl
{
    /// <summary>A tab showing a fresh model's starting values (" - ", 50.00 Hz, 3000 rpm) until it is given its model.</summary>
    public MachineView()
    {
        InitializeComponent();
        DataContext = new MachineModel();
    }

    /// <summary>
    /// The model every value binds to: the data context. A view whose data context is not a <see cref="MachineModel"/>
    /// has been wired wrongly, and reading its model says so (fail loud) instead of showing nothing.
    /// </summary>
    public MachineModel Model
    {
        get => DataContext as MachineModel
            ?? throw new InvalidOperationException("THE MACHINE view has no MachineModel as its DataContext.");
        set => DataContext = value ?? throw new ArgumentNullException(nameof(value));
    }
}
