// HOW TO FEED (UI programme Phase 1.6; integrated 2026-10-20). The tab is fed through the dashboard's tab hooks, as in
// WPF (DashboardViewModel.UpdateTabs: Mechanical.Update(f, Clock) on every frame the dashboard takes, with the clock the
// dashboard has just set from that frame; ResetTabs: Mechanical.Reset()). Those calls are in TabModels.Update and
// TabModels.ResetTimeline, which the window's AppDashboardModel runs. The main window hosts the tab bound to that model:
//
//     <tabs:MachineEngineerView x:Name="MechanicalTab" DataContext="{Binding Tabs.Mechanical}" />
//
// and the view shows that model's inspector in its drill-down overlay (Inspector.Model = Model.Inspector, redone each
// time the data context changes). The view no longer feeds itself from the run pipeline (that self-attach was removed
// with the integration), so a frame on which the dashboard's own update throws feeds this tab nothing, as in WPF. The
// lake's bottom-up inertia comes from the GDA bridge (UI programme slice 1.8): TabModels.UpdateMarketData calls
// Mechanical.SetIntrinsic(op?.SynchronousInertiaGvaS), as the WPF UpdateMarketData does.

using Avalonia.Controls;
using Avalonia.Interactivity;
using GridSim.Ui.Tabs.MachineEngineer;

namespace GridSim.App.Tabs;

/// <summary>
/// The MECHANICAL / PLANT ENGINEER tab of the WPF window (UI programme Phase 1.6) as one self-contained view, with its
/// drill-down inspector overlay. Its data context is <see cref="Model"/>, which the window sets to the tab model its
/// dashboard feeds. See the comment at the top of this file.
/// </summary>
public partial class MachineEngineerView : UserControl
{
    /// <summary>A tab showing a fresh model's starting values ("-") until it is given its model.</summary>
    public MachineEngineerView()
    {
        InitializeComponent();
        DataContext = new MachineEngineerModel();   // OnDataContextChanged points the overlay at its inspector
    }

    /// <summary>
    /// The model every value binds to: the data context. A view whose data context is not a
    /// <see cref="MachineEngineerModel"/> has been wired wrongly, and reading its model says so (fail loud).
    /// </summary>
    public MachineEngineerModel Model
    {
        get => DataContext as MachineEngineerModel
            ?? throw new InvalidOperationException("The MECHANICAL view has no MachineEngineerModel as its DataContext.");
        set => DataContext = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The overlay always shows the current model's inspector (WPF DataContext="{Binding Inspector}").</summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MachineEngineerModel m) Inspector.Model = m.Inspector;
    }

    // WPF: each machine row is a button bound to InspectMachineCommand with the row as its parameter.
    private void Machine_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MachineRowModel row }) Model.InspectMachine(row);
    }
}
