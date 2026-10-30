// FEED (UI programme Phase 1.6, third batch; integrated 2026-10-20). The tab is fed through the dashboard's tab hooks
// exactly as the WPF DashboardViewModel.UpdateTabs feeds it: TabModels.HumanScale.Update(f, clock) at its WPF position
// (after Domains and InverterFleet, before Experimental and Cycle), with the clock the dashboard has just set from the
// frame. THE HUMAN SCALE has no ForgetModel, ResetTimeline or ClearStream hook in WPF (it keeps nothing across frames
// but its machine rows, which each frame re-keys). The main window hosts it in the WPF tab order:
//
//     <TabItem Header="HUMAN SCALE"><tabs:HumanScaleView x:Name="HumanScaleTab" DataContext="{Binding Tabs.HumanScale}" /></TabItem>

using Avalonia.Controls;

namespace GridSim.App.Tabs;

/// <summary>THE HUMAN SCALE tab (UI programme Phase 1.6). Everything it shows is bound to the shared HumanScaleModel.</summary>
public sealed partial class HumanScaleView : UserControl
{
    public HumanScaleView()
    {
        InitializeComponent();
    }
}
