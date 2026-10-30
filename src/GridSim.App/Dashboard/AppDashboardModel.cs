using GridSim.App.Tabs;
using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.Simulation;
using GridSim.Ui.Dashboard;

namespace GridSim.App.Dashboard;

/// <summary>
/// The Avalonia app's dashboard model (UI programme Phase 1.4; the one dashboard of the window since the integration
/// of 2026-10-20): the shared <see cref="DashboardModel"/> itself, so every value the dashboard tiles show is produced
/// by the same code, in the same order, as the WPF dashboard's, plus the tabs it feeds.
///
/// <para>The WPF <c>DashboardViewModel</c> derives from the same class and fills its four tab hooks with the WPF
/// per-tab view models. This class does the same with <see cref="TabModels"/>: each hook below runs the matching
/// <see cref="TabModels"/> method, whose body is the WPF hook's body in the WPF order (ASSETS first since UI programme
/// ASSETS A3, as WPF).
/// The shared model calls the hooks at the WPF points: <see cref="DashboardModel.Update"/> feeds the tabs after its own
/// panels (so its <see cref="DashboardModel.Clock"/> is this frame's), <see cref="DashboardModel.ModelChanged"/> makes
/// them forget the model, <see cref="DashboardModel.ResetTimeline"/> resets them first, and
/// <see cref="DashboardModel.MarkStreamLost"/> clears the stream-fed blocks. So a frame on which the dashboard's own
/// update throws, before its tab hook, feeds no tab, as in WPF; and since no tab is fed any other way, no tab can be
/// fed a frame twice.</para>
///
/// <para>A dashboard made without tabs (<see cref="AppDashboardModel()"/>: the standalone <see cref="DashboardView"/>
/// a test or a preview shows on its own) has nothing behind its hooks, which then do nothing; that is stated, not an
/// accident: <see cref="Tabs"/> is null for it.</para>
///
/// <para>Unlike the WPF adapter, no brush or visibility properties are added: the Avalonia view binds the neutral colour
/// tokens through <see cref="UiColourBrushConverter"/> and the bools straight to <c>IsVisible</c>.</para>
/// </summary>
public sealed class AppDashboardModel : DashboardModel
{
    /// <summary>A dashboard with no tabs behind its hooks (a standalone dashboard view).</summary>
    public AppDashboardModel() { }

    /// <summary>The window's dashboard, which feeds <paramref name="tabs"/> through its four hooks, as WPF.</summary>
    public AppDashboardModel(TabModels tabs)
    {
        Tabs = tabs ?? throw new ArgumentNullException(nameof(tabs));
    }

    /// <summary>The tabs this dashboard feeds, or null for a dashboard made without tabs.</summary>
    public TabModels? Tabs { get; }

    private bool _authorPaused;

    /// <summary>
    /// UI programme 4.2 follow-up (2026-10-24): true while the window is in AUTHOR mode. The run is paused and the map
    /// shows the case being edited, which is not solved, so the live tiles beside it (telemetry, what-if and boundaries)
    /// would otherwise read as values of that case: the view veils them and labels them as the paused run's last values.
    /// Nothing is cleared, so leaving AUTHOR shows the run exactly as it was. Here and not on the shared DashboardModel:
    /// the WPF window has no AUTHOR mode, and the shared model's properties are pinned against the WPF view model.
    /// </summary>
    public bool AuthorPaused
    {
        get => _authorPaused;
        set => Set(ref _authorPaused, value);
    }

    /// <summary>WPF UpdateTabs: every tab is fed the frame just applied, with this frame's clock, in the WPF order.</summary>
    protected override void UpdateTabs(SimFrame f, IReadOnlyList<BoundaryFlow> boundaries, double baseSolveMs) =>
        Tabs?.Update(f, boundaries, Clock, baseSolveMs);

    /// <summary>WPF ForgetModelInTabs: the SCADA register and the resident CYCLE service are dropped.</summary>
    protected override void ForgetModelInTabs() => Tabs?.ForgetModel();

    /// <summary>WPF ResetTabs (run first by ResetTimeline): THE MACHINE's integrators, then NESO RP1, OPERATOR, MARKET
    /// and MECHANICAL.</summary>
    protected override void ResetTabs() => Tabs?.ResetTimeline();

    /// <summary>WPF ClearStreamTabs (a lost bridge stream): OPERATOR, MARKET and NESO RP1 are cleared.</summary>
    protected override void ClearStreamTabs() => Tabs?.ClearStream();

    /// <summary>
    /// UI programme slice 1.8, the WPF DashboardViewModel.UpdateMarketData: push the per-tick OPERATOR and MARKET series
    /// streamed from the GDA lake into the tabs, through their existing entry points (SetOperatorData with this
    /// dashboard's clock, SetMarketData, SetRp1Data and SetIntrinsic, in the WPF order; see
    /// <see cref="TabModels.UpdateMarketData"/>). <paramref name="tickUtc"/> is the bridge tick's UTC instant (N144:
    /// the MARKET price trace takes one sample per settlement period). A dashboard made without tabs has nothing to feed.
    /// </summary>
    public void UpdateMarketData(BridgeProtocol.OperatorBlock? op, BridgeProtocol.MarketBlock? mk, DateTime? tickUtc = null) =>
        Tabs?.UpdateMarketData(op, mk, tickUtc, Clock);
}
