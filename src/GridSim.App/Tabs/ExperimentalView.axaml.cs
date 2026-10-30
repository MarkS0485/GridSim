// FEED (UI programme Phase 1.6, third batch; integrated 2026-10-20). The tab is fed through the dashboard's tab hooks
// exactly as the WPF DashboardViewModel.UpdateTabs feeds it: TabModels.Experimental (built with UiPoster below) is
// updated at its WPF position (after HumanScale, before Cycle) with Experimental.Update(f); there is no ForgetModel,
// ResetTimeline or ClearStream hook in WPF. It is gated as WPF gates it (R020: the models run only while the tab
// shows): the main window's tab-selection handler, beside the SPEED DOMAINS gate, sets
//     _vm.Tabs.Experimental.Active = TabHost.SelectedItem is TabItem { Header: "EXPERIMENTAL" };
// and the window hosts it after INVERTER FLEET, as WPF:
//     <TabItem Header="EXPERIMENTAL"><tabs:ExperimentalView x:Name="ExperimentalTab" DataContext="{Binding Tabs.Experimental}" /></TabItem>

using Avalonia.Controls;
using Avalonia.Threading;

namespace GridSim.App.Tabs;

/// <summary>
/// The EXPERIMENTAL tab (UI programme Phase 1.6). Everything it shows is bound to the shared ExperimentalModel, whose
/// models run on a worker thread and whose results come back to the UI thread through <see cref="UiPoster"/>.
/// </summary>
public sealed partial class ExperimentalView : UserControl
{
    public ExperimentalView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The UI-thread poster the Avalonia EXPERIMENTAL model is built with: on the Avalonia UI thread, an action is
    /// posted back to it (Dispatcher.UIThread.Post, the Avalonia twin of the WPF continuation on the dispatcher's
    /// synchronisation context); called from any other thread there is no UI loop to return to, so the model computes
    /// and shows the frame inline, as the WPF tab did with no synchronisation context.
    /// </summary>
    public static Action<Action>? UiPoster() =>
        Dispatcher.UIThread.CheckAccess() ? apply => Dispatcher.UIThread.Post(apply) : null;
}
