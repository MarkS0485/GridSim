using Avalonia.Threading;
using GridSim.App.Assets;
using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.Simulation;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Tabs.Cycle;
using GridSim.Ui.Tabs.Domains;
using GridSim.Ui.Tabs.Experimental;
using GridSim.Ui.Tabs.Harmonics;
using GridSim.Ui.Tabs.HumanScale;
using GridSim.Ui.Tabs.InverterFleet;
using GridSim.Ui.Tabs.Machine;
using GridSim.Ui.Tabs.MachineEngineer;
using GridSim.Ui.Tabs.Market;
using GridSim.Ui.Tabs.NesoRp1;
using GridSim.Ui.Tabs.Operator;
using GridSim.Ui.Tabs.Scada;

namespace GridSim.App.Tabs;

/// <summary>
/// The tab view models of the Avalonia app (UI programme Phase 1.6, all three batches; integrated 2026-10-20). They
/// are the shared GridSim.Ui models themselves (the same code the WPF tabs now derive from), so every value a tab shows
/// is produced exactly as WPF produces it.
/// <para>
/// The four methods below are the bodies of the WPF dashboard's tab hooks (src/GridSim.Wpf/DashboardViewModel.cs:
/// UpdateTabs, ForgetModelInTabs, ResetTabs, ClearStreamTabs), statement for statement and in the WPF order. UI
/// programme ASSETS A3 (2026-10-20): ASSETS is ported, so the WPF first call of UpdateTabs, <c>Assets.Update(f, Clock)</c>,
/// is the first call here too (the ASSETS tab's navigator; the WPF explorer takes no part in the other three hooks, and
/// neither does it here).
/// <see cref="GridSim.App.Dashboard.AppDashboardModel"/> calls them at its hook points, so the tabs are fed by the
/// dashboard and only by the dashboard: a frame on which the dashboard's own update throws (before it reaches its tab
/// hook) feeds no tab, as in WPF. No view feeds itself from the run pipeline any more.
/// </para>
/// <para>
/// <see cref="TabUpdated"/> is raised after each tab's update with the tab's header, so a test can count that each tab
/// is fed exactly once per frame shown, and in the WPF order (<see cref="UpdateOrder"/>).
/// </para>
/// </summary>
public sealed class TabModels : IDisposable
{
    // ---- the tab headers, as the WPF window's TabItem headers spell them ----

    /// <summary>THE MACHINE tab's header.</summary>
    public const string MachineHeader = "THE MACHINE";

    /// <summary>The SCADA tab's header.</summary>
    public const string ScadaHeader = "SCADA";

    /// <summary>The OPERATOR tab's header.</summary>
    public const string OperatorHeader = "OPERATOR";

    /// <summary>The MARKET tab's header.</summary>
    public const string MarketHeader = "MARKET";

    /// <summary>The NESO RP1 tab's header.</summary>
    public const string NesoRp1Header = "NESO RP1";

    /// <summary>The MECHANICAL tab's header.</summary>
    public const string MechanicalHeader = "MECHANICAL";

    /// <summary>The SPEED DOMAINS tab's header (its decomposition runs only while it shows).</summary>
    public const string DomainsHeader = "SPEED DOMAINS";

    /// <summary>The INVERTER FLEET tab's header.</summary>
    public const string InverterFleetHeader = "INVERTER FLEET";

    /// <summary>THE HUMAN SCALE tab's header.</summary>
    public const string HumanScaleHeader = "HUMAN SCALE";

    /// <summary>The EXPERIMENTAL tab's header (its models run only while it shows).</summary>
    public const string ExperimentalHeader = "EXPERIMENTAL";

    /// <summary>The HARMONICS tab's header (slice 1.7; the tab is fed nothing, as in WPF).</summary>
    public const string HarmonicsHeader = HarmonicsModel.Header;

    /// <summary>The CYCLE tab's header.</summary>
    public const string CycleHeader = "CYCLE";

    /// <summary>The ASSETS tab's header (UI programme ASSETS A3; its explorer refreshes only while it shows).</summary>
    public const string AssetsHeader = "ASSETS";

    /// <summary>
    /// The order <see cref="Update"/> feeds the tabs in: the WPF UpdateTabs order (Assets, Machine, Scada, Operator,
    /// Market, NesoRp1, Mechanical, Domains, InverterFleet, HumanScale, Experimental, Cycle). UI programme ASSETS A3:
    /// ASSETS, WPF's first, is first here too.
    /// </summary>
    public static readonly IReadOnlyList<string> UpdateOrder = new[]
    {
        AssetsHeader, MachineHeader, ScadaHeader, OperatorHeader, MarketHeader, NesoRp1Header, MechanicalHeader, DomainsHeader,
        InverterFleetHeader, HumanScaleHeader, ExperimentalHeader, CycleHeader,
    };

    /// <summary>
    /// The ASSETS tab (UI programme ASSETS A3): the explorer tree and its navigator over the A2 explorer, which makes the
    /// Avalonia page types (<see cref="AppAssetPageFactory"/>: the A2 pages under plain names). Its Active gate is set by
    /// the window when the ASSETS tab is selected.
    /// </summary>
    public AssetNavigatorModel Assets { get; } = new(new NavigableAssetExplorer(AppAssetPageFactory.Instance));

    /// <summary>THE MACHINE tab: the layman's one-rotating-machine view.</summary>
    public MachineModel Machine { get; } = new();

    /// <summary>The SCADA tab: the simulated supervisory console.</summary>
    public ScadaModel Scada { get; } = new();

    /// <summary>The OPERATOR tab: the control-room view.</summary>
    public OperatorModel Operator { get; } = new();

    /// <summary>The MARKET tab: the wholesale / balancing view.</summary>
    public MarketModel Market { get; } = new();

    /// <summary>The NESO RP1 tab: the situational-awareness page (its drill-downs open the inspector overlay).</summary>
    public NesoRp1Model NesoRp1 { get; } = new();

    /// <summary>The MECHANICAL tab: the per-machine / plant-engineer view, with its own inspector.</summary>
    public MachineEngineerModel Mechanical { get; } = new();

    /// <summary>The SPEED DOMAINS tab (computed only while it is showing: <see cref="DomainsModel.Active"/>).</summary>
    public DomainsModel Domains { get; } = new();

    /// <summary>The INVERTER FLEET tab: the authored per-asset dynamics.</summary>
    public InverterFleetModel InverterFleet { get; } = new();

    /// <summary>THE HUMAN SCALE tab: the same frames in relatable units.</summary>
    public HumanScaleModel HumanScale { get; } = new();

    /// <summary>The EXPERIMENTAL tab (computed only while it is showing: <see cref="ExperimentalModel.Active"/>). Its
    /// models run on a worker and come back to the Avalonia UI thread through <see cref="ExperimentalView.UiPoster"/>.</summary>
    public ExperimentalModel Experimental { get; } = new(ExperimentalView.UiPoster);

    /// <summary>
    /// The HARMONICS tab (UI programme slice 1.7): four fixed texts, the WPF ones. It is NOT fed by
    /// <see cref="Update"/> and is not in <see cref="UpdateOrder"/>: the WPF dashboard's tab hooks never touch the WPF
    /// HARMONICS tab (its words are written straight into MainWindow.xaml), so neither do these.
    /// </summary>
    public HarmonicsModel Harmonics { get; } = new();

    /// <summary>The CYCLE tab. A completed cycle is projected on the Avalonia UI thread (the WPF tab uses the WPF
    /// dispatcher the same way).</summary>
    public CycleModel Cycle { get; } = new(apply => Dispatcher.UIThread.Post(apply));

    /// <summary>Raised after each tab has taken a frame in <see cref="Update"/>, with that tab's header.</summary>
    public event Action<string>? TabUpdated;

    /// <summary>The WPF UpdateTabs body: each tab is fed the frame the dashboard just applied, with the dashboard's
    /// clock (the tabs that take one) and the measured base-solve time (CYCLE), in the WPF order.</summary>
    public void Update(SimFrame f, IReadOnlyList<BoundaryFlow> boundaries, string clock, double baseSolveMs)
    {
        ArgumentNullException.ThrowIfNull(f);
        ArgumentNullException.ThrowIfNull(boundaries);
        ArgumentNullException.ThrowIfNull(clock);
        Assets.Update(f, clock);   // WPF first: Assets.Update(f, Clock)
        TabUpdated?.Invoke(AssetsHeader);
        Machine.Update(f);
        TabUpdated?.Invoke(MachineHeader);
        Scada.Update(f, clock);
        TabUpdated?.Invoke(ScadaHeader);
        Operator.Update(f, boundaries, clock);
        TabUpdated?.Invoke(OperatorHeader);
        Market.Update(f, clock);
        TabUpdated?.Invoke(MarketHeader);
        NesoRp1.Update(f, boundaries, clock);
        TabUpdated?.Invoke(NesoRp1Header);
        Mechanical.Update(f, clock);
        TabUpdated?.Invoke(MechanicalHeader);
        Domains.Update(f);
        TabUpdated?.Invoke(DomainsHeader);
        InverterFleet.Update(f);
        TabUpdated?.Invoke(InverterFleetHeader);
        HumanScale.Update(f, clock);
        TabUpdated?.Invoke(HumanScaleHeader);
        Experimental.Update(f);
        TabUpdated?.Invoke(ExperimentalHeader);
        Cycle.Update(f, baseSolveMs);
        TabUpdated?.Invoke(CycleHeader);
    }

    /// <summary>The WPF ForgetModelInTabs body: the SCADA register and the resident CYCLE service are rebuilt on the
    /// next frame.</summary>
    public void ForgetModel()
    {
        Scada.ForgetModel();
        Cycle.ForgetModel();
    }

    /// <summary>The WPF ResetTabs body, in its order: THE MACHINE's integrators (the load-bearing reset), then the
    /// bridge-sourced state of NESO RP1, OPERATOR, MARKET and MECHANICAL.</summary>
    public void ResetTimeline()
    {
        Machine.Reset();
        NesoRp1.Reset();      // drop stale bridge-sourced lake state
        Operator.Reset();     // drop stale BM actions/constraints/reserve/warnings (SetOperatorData latches)
        Market.Reset();       // D-DOC-143: drop stale bridge prices, NIV, interconnectors and the price trace
        Mechanical.Reset();   // drop stale ramp/pulse accumulators + lake intrinsic
    }

    /// <summary>The WPF ClearStreamTabs body: the bridge-fed OPERATOR, MARKET and NESO RP1 blocks are cleared.</summary>
    public void ClearStream()
    {
        Operator.Reset();
        Market.Reset();
        NesoRp1.Reset();
    }

    /// <summary>
    /// UI programme slice 1.8: the WPF DashboardViewModel.UpdateMarketData body, statement for statement and in its
    /// order: the per-tick OPERATOR and MARKET blocks streamed from the GDA lake go to OPERATOR (with the dashboard's
    /// <paramref name="clock"/>), MARKET and NESO RP1 (D-DOC-200: one price per settlement period, as MARKET), and the
    /// lake's synchronous inertia to MECHANICAL. Fed only by the bridge, beside the frame (the SimFrame carries no
    /// balancing-mechanism or price data).
    /// </summary>
    public void UpdateMarketData(BridgeProtocol.OperatorBlock? op, BridgeProtocol.MarketBlock? mk, DateTime? tickUtc, string clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        Operator.SetOperatorData(op, clock);
        Market.SetMarketData(mk, tickUtc);
        NesoRp1.SetRp1Data(op, mk, tickUtc);   // D-DOC-200: one price per settlement period, as MARKET
        Mechanical.SetIntrinsic(op?.SynchronousInertiaGvaS);
    }

    /// <summary>Stop the CYCLE tab's background worker (the WPF window disposes it on close).</summary>
    public void Dispose() => Cycle.Dispose();
}
