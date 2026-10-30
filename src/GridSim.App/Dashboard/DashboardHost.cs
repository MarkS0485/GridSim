using Avalonia.Threading;
using GridSim.App.Run;
using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.Simulation;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Hosting;
using GridSim.Ui.Presentation;
using GridSim.Ui.Schematic;

namespace GridSim.App.Dashboard;

/// <summary>
/// Owns the dashboard model and feeds it from a <see cref="RunViewModel"/> (UI programme Phase 1.4), making the same
/// <see cref="DashboardModel"/> calls, in the same order, with the same texts, as the WPF window's code-behind
/// (<c>src/GridSim.Wpf/MainWindow.xaml.cs</c>) makes on its <c>DashboardViewModel</c>:
/// <list type="bullet">
/// <item><description>start-up (the WPF constructor and OnLoaded): the licence line in the event log at "08:00", and the
/// legend set to the map's starting colour mode (line loading). Done once, on the first <see cref="Attach"/>.</description></item>
/// <item><description><see cref="RunViewModel.FeedLoading"/> (WPF SwitchTo, before the load): HideHud, ClearBridgeStatus.</description></item>
/// <item><description><see cref="RunViewModel.LoadRefused"/>: the refusal logged at "--:--".</description></item>
/// <item><description><see cref="RunViewModel.ModelSwitched"/> (WPF SwitchTo / LoadReplay): MapHint (local or replay),
/// ClearSelection, ModelChanged.</description></item>
/// <item><description><see cref="RunViewModel.FrameArriving"/> (WPF ShowHostFrame / RenderReplayFrame): Update, or the
/// SOLVE FAULT path (lamp text and colour, event log, alarm) when the solve threw or the update did. Slice 1.7: a frame
/// whose update threw is refused (<see cref="RunFrameEventArgs.Reject"/>), so the run pipeline shows it nowhere else,
/// not on the map either, as the WPF window's one guard does.</description></item>
/// <item><description><see cref="RunViewModel.FeedLoaded"/>: the "Loaded ..." line logged at "08:00" (live) or "00:00" (replay).</description></item>
/// <item><description><see cref="RunViewModel.TimelineReset"/> (WPF Pause_Click on a finished replay): ResetTimeline.</description></item>
/// </list>
/// UI programme slice 1.8: the GDA bridge calls (MapHint, UpdateHud, SetBridgeStatus, ShowQueryResult,
/// ClearQueryResult, MarkStreamLost, UpdateMarketData, the WHAT-IF lamp, the bridge log lines and alarms) also come
/// through this host: it is the run pipeline's bridge session's <see cref="IBridgeFeed"/> while attached, and a streamed
/// frame arrives as <see cref="RunFrameKind.Bridge"/> on the same <see cref="RunViewModel.FrameArriving"/> path.
/// Not here: the map's selection and trip calls (Select, AnalyseTrip, the colour-mode button's SetColorLegend) and the
/// WPF start-up warnings about geography, settings and the model scan, which belong to the main window. The model's
/// four tab hooks feed the tabs it was made with (<see cref="AppDashboardModel"/>), so this host is the one path by
/// which a frame reaches the dashboard and every tab.
///
/// <para>Threading: the run pipeline raises every event on the UI thread, and every handler checks that it is on it
/// (fail loud), because the model's collections are bound to the view.</para>
/// </summary>
public sealed class DashboardHost : IBridgeFeed
{
    /// <summary>WPF: the status-lamp text for a solve that threw or a frame that could not be shown.</summary>
    public const string SolveFaultStatus = " SOLVE FAULT";

    /// <summary>WPF: the colour of the SOLVE FAULT lamp and alarm, <c>Palette.Loading(1.2)</c>.</summary>
    public static readonly UiColour SolveFaultColour = ColourRamps.Loading(1.2);

    private bool _startupLogged;   // the WPF start-up lines are written once, as the WPF window writes them once

    /// <summary>A host over a dashboard of its own with no tabs behind it (a standalone <see cref="DashboardView"/>).</summary>
    public DashboardHost() : this(new AppDashboardModel()) { }

    /// <summary>
    /// A host over <paramref name="model"/> (UI programme integration, 2026-10-20: the main window's one dashboard,
    /// which feeds the tabs through its hooks, is made by the window's view model and fed only through this host).
    /// </summary>
    public DashboardHost(AppDashboardModel model)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
    }

    /// <summary>The dashboard model every tile binds to (and, for the window's, every tab is fed from).</summary>
    public AppDashboardModel Model { get; }

    /// <summary>The run pipeline the dashboard is fed from, or null while detached.</summary>
    public RunViewModel? Run { get; private set; }

    /// <summary>
    /// Start feeding the dashboard from <paramref name="run"/>. From now on every load, refusal and frame of that run
    /// pipeline reaches the model. Attaching while already attached is a wiring mistake and throws (call
    /// <see cref="Detach"/> first); nothing is replayed, so attach before the first case is opened to see it all.
    /// </summary>
    public void Attach(RunViewModel run)
    {
        ArgumentNullException.ThrowIfNull(run);
        Dispatcher.UIThread.VerifyAccess();
        if (Run is not null)
            throw new InvalidOperationException("The dashboard is already attached to a run pipeline; detach it first.");

        if (!_startupLogged)
        {
            _startupLogged = true;
            Model.Log("08:00", run.LicenceText);            // WPF constructor: _vm.Log("08:00", $"Licence: ...")
            Model.SetColorLegend(MapColourMode.Loading);    // WPF OnLoaded: the legend follows the map's starting mode
        }

        run.FeedLoading += OnFeedLoading;
        run.LoadRefused += OnLoadRefused;
        run.ModelSwitched += OnModelSwitched;
        run.FrameArriving += OnFrameArriving;
        run.FeedLoaded += OnFeedLoaded;
        run.TimelineReset += OnTimelineReset;
        if (run.Bridge is { } bridge) bridge.Feed = this;   // slice 1.8: the bridge's dashboard calls come through here
        Run = run;
    }

    /// <summary>Stop feeding the dashboard. It keeps showing what it showed last. Safe to call when not attached.</summary>
    public void Detach()
    {
        Dispatcher.UIThread.VerifyAccess();
        RunViewModel? run = Run;
        if (run is null) return;
        run.FeedLoading -= OnFeedLoading;
        run.LoadRefused -= OnLoadRefused;
        run.ModelSwitched -= OnModelSwitched;
        run.FrameArriving -= OnFrameArriving;
        run.FeedLoaded -= OnFeedLoaded;
        run.TimelineReset -= OnTimelineReset;
        if (run.Bridge is { } bridge && ReferenceEquals(bridge.Feed, this)) bridge.Feed = null;
        Run = null;
    }

    // ---------------------------------------------------------------- the bridge's calls (slice 1.8, IBridgeFeed)
    // Each is the WPF bridge code's call on its DashboardViewModel, made on this host's model; each checks that it is on
    // the UI thread (fail loud), as every other feed call here does.

    /// <inheritdoc />
    string IBridgeFeed.Clock => Model.Clock;

    /// <inheritdoc />
    string IBridgeFeed.Status
    {
        get => Model.Status;
        set { Dispatcher.UIThread.VerifyAccess(); Model.Status = value; }
    }

    /// <inheritdoc />
    void IBridgeFeed.Log(string time, string line) { Dispatcher.UIThread.VerifyAccess(); Model.Log(time, line); }

    /// <inheritdoc />
    void IBridgeFeed.AddAlarm(string text, UiColour colour) { Dispatcher.UIThread.VerifyAccess(); Model.AddAlarm(text, colour); }

    /// <inheritdoc />
    void IBridgeFeed.SetBridgeStatus(string text, UiColour colour) { Dispatcher.UIThread.VerifyAccess(); Model.SetBridgeStatus(text, colour); }

    /// <inheritdoc />
    void IBridgeFeed.UpdateHud(double? inertiaMwS, double? conditionIndex, double? rocofHeadroomHzS)
    {
        Dispatcher.UIThread.VerifyAccess();
        Model.UpdateHud(inertiaMwS, conditionIndex, rocofHeadroomHzS);
    }

    /// <inheritdoc />
    void IBridgeFeed.UpdateMarketData(BridgeProtocol.OperatorBlock? op, BridgeProtocol.MarketBlock? mk, DateTime? tickUtc)
    {
        Dispatcher.UIThread.VerifyAccess();
        Model.UpdateMarketData(op, mk, tickUtc);
    }

    /// <inheritdoc />
    void IBridgeFeed.MarkStreamLost(string reason) { Dispatcher.UIThread.VerifyAccess(); Model.MarkStreamLost(reason); }

    /// <inheritdoc />
    void IBridgeFeed.ShowQueryResult(QueryResult qr, string title, bool hasOverlay, bool solveNotConverged)
    {
        Dispatcher.UIThread.VerifyAccess();
        Model.ShowQueryResult(qr, title, hasOverlay, solveNotConverged);
    }

    /// <inheritdoc />
    void IBridgeFeed.ClearQueryResult() { Dispatcher.UIThread.VerifyAccess(); Model.ClearQueryResult(); }

    /// <inheritdoc />
    void IBridgeFeed.ShowBridgeHint() { Dispatcher.UIThread.VerifyAccess(); Model.MapHint = DashboardModel.MapHintBridge; }

    /// <inheritdoc />
    void IBridgeFeed.ClearSelection() { Dispatcher.UIThread.VerifyAccess(); Model.ClearSelection(); }

    /// <inheritdoc />
    void IBridgeFeed.ModelChanged() { Dispatcher.UIThread.VerifyAccess(); Model.ModelChanged(); }

    // ---------------------------------------------------------------- the WPF calls, point for point

    // WPF SwitchTo: "the live HUD + status pill ... are bridge-only", hidden before any local load.
    private void OnFeedLoading(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.VerifyAccess();
        Model.HideHud();
        Model.ClearBridgeStatus();
    }

    // WPF SwitchTo / LoadReplay: a refused load is logged with the host's own words and changes nothing else.
    private void OnLoadRefused(object? sender, string refusal)
    {
        Dispatcher.UIThread.VerifyAccess();
        Model.Log("--:--", refusal);
    }

    // WPF SwitchTo / LoadReplay: the hint follows the feed, the fly-out closes, and the model and timeline restart.
    private void OnModelSwitched(object? sender, RunLoadedEventArgs e)
    {
        Dispatcher.UIThread.VerifyAccess();
        Model.MapHint = e.IsReplay ? DashboardModel.MapHintReplay : DashboardModel.MapHintLocal;
        Model.ClearSelection();
        Model.ModelChanged();
    }

    // WPF SwitchTo logs at "08:00" (the live clock's start), LoadReplay at "00:00".
    private void OnFeedLoaded(object? sender, RunLoadedEventArgs e)
    {
        Dispatcher.UIThread.VerifyAccess();
        Model.Log(e.IsReplay ? "00:00" : "08:00", e.LoadedText);
    }

    // WPF Pause_Click: a finished replay played again starts a fresh timeline.
    private void OnTimelineReset(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.VerifyAccess();
        Model.ResetTimeline();
    }

    // WPF ShowHostFrame (live) and RenderReplayFrame (replay), with their SOLVE FAULT paths and texts.
    private void OnFrameArriving(object? sender, RunFrameEventArgs e)
    {
        Dispatcher.UIThread.VerifyAccess();
        switch (e.Kind)
        {
            case RunFrameKind.LiveFaulted:
            {
                // A solver exception must not silently freeze the dashboard on the last good frame.
                Exception ex = e.Fault ?? new InvalidOperationException("unknown solve fault");
                SolveFault($"Solve failed at tick {e.Tick}: {ex.Message}", ex.Message);
                break;
            }
            case RunFrameKind.LiveSolved:
                // An exception while showing the frame must not end the loop or freeze the dashboard: SOLVE FAULT.
                try
                {
                    SimFrame f = e.Frame ?? throw new InvalidOperationException("A solved frame arrived without its frame.");
                    Model.Update(f, e.Boundaries, e.SolveMs);
                }
#pragma warning disable CA1031 // Not swallowed: shown on the lamp, in the event log and as an alarm, as WPF shows it.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    SolveFault($"Render failed at tick {e.Tick}: {ex.Message}", ex.Message);
                    e.Reject(ex);   // slice 1.7: WPF's one guard, so the frame goes no further (not to the map)
                }
                break;
            case RunFrameKind.Replay:
            case RunFrameKind.Bridge:   // slice 1.8: WPF shows a bridge frame through RenderReplayFrame too
                try
                {
                    SimFrame f = e.Frame ?? throw new InvalidOperationException("A replay frame arrived without its frame.");
                    // WPF RenderReplayFrame evaluates a replay frame's boundary transfers itself, so the boundary panel
                    // fills on a replay too. D-DOC-087: the analysis refuses a non-converged state; such a frame shows
                    // no boundary transfers. No solve time on a replay (the two-argument Update, as WPF).
                    IReadOnlyList<BoundaryFlow> boundaries = f.Result.Converged
                        ? BoundaryAnalysis.Evaluate(f.Model, f.Result)
                        : Array.Empty<BoundaryFlow>();
                    Model.Update(f, boundaries);
                }
#pragma warning disable CA1031 // Not swallowed: shown on the lamp, in the event log and as an alarm, as WPF shows it.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    SolveFault($"Render failed: {ex.Message}", ex.Message);
                    e.Reject(ex);   // slice 1.7: WPF's one guard, so the frame goes no further (not to the map)
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(e), e.Kind, "Unknown run frame kind.");
        }
    }

    /// <summary>The WPF SOLVE FAULT path: the lamp's text and colour, one event-log line, one alarm.</summary>
    private void SolveFault(string logLine, string message)
    {
        Model.Status = SolveFaultStatus;
        Model.StatusColour = SolveFaultColour;
        Model.Log(Model.Clock, logLine);
        Model.AddAlarm($"SOLVE FAULT: {message}", SolveFaultColour);
    }
}
