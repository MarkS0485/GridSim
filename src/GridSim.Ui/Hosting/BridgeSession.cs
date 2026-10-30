using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Net;
using GridSim.Core.Simulation;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Models;
using GridSim.Ui.Presentation;
using GridSim.Ui.Settings;

namespace GridSim.Ui.Hosting;

/// <summary>
/// The dashboard calls the GDA bridge makes (UI programme slice 1.8): the WPF window's bridge code calls these members
/// of its DashboardViewModel directly. A front end gives the session one object that makes the same calls on its own
/// dashboard (the Avalonia app: its DashboardHost, which is the only thing that feeds the window's dashboard), so the
/// bridge reaches the dashboard and the tabs through the same one feed as a local case or a replay.
/// <para>Every member is called on the UI thread.</para>
/// </summary>
public interface IBridgeFeed
{
    /// <summary>The dashboard's clock (WPF <c>_vm.Clock</c>), the time stamp of most bridge event-log lines.</summary>
    string Clock { get; }

    /// <summary>The header lamp's text (WPF <c>_vm.Status</c>): read and prefixed with "WHAT-IF  *  " by a what-if.</summary>
    string Status { get; set; }

    /// <summary>One event-log line (WPF <c>_vm.Log</c>).</summary>
    void Log(string time, string line);

    /// <summary>One alarm (WPF <c>_vm.AddAlarm</c>).</summary>
    void AddAlarm(string text, UiColour colour);

    /// <summary>The bridge status pill (WPF <c>_vm.SetBridgeStatus</c>).</summary>
    void SetBridgeStatus(string text, UiColour colour);

    /// <summary>The live HUD (WPF <c>_vm.UpdateHud</c>).</summary>
    void UpdateHud(double? inertiaMwS, double? conditionIndex, double? rocofHeadroomHzS);

    /// <summary>The OPERATOR, MARKET, NESO RP1 and MECHANICAL lake blocks (WPF <c>_vm.UpdateMarketData</c>).</summary>
    void UpdateMarketData(BridgeProtocol.OperatorBlock? op, BridgeProtocol.MarketBlock? mk, DateTime? tickUtc);

    /// <summary>The stream has stopped for good (WPF <c>_vm.MarkStreamLost</c>).</summary>
    void MarkStreamLost(string reason);

    /// <summary>The what-if results panel (WPF <c>_vm.ShowQueryResult</c>).</summary>
    void ShowQueryResult(QueryResult qr, string title, bool hasOverlay, bool solveNotConverged);

    /// <summary>Close the what-if results panel (WPF <c>_vm.ClearQueryResult</c>).</summary>
    void ClearQueryResult();

    /// <summary>The map hint for a bridge feed (WPF <c>_vm.MapHint = DashboardViewModel.MapHintBridge</c>).</summary>
    void ShowBridgeHint();

    /// <summary>Close the selection fly-out (WPF <c>_vm.ClearSelection</c>).</summary>
    void ClearSelection();

    /// <summary>A new or possibly different model: per-model caches rebuild on its frames (WPF <c>_vm.ModelChanged</c>).</summary>
    void ModelChanged();
}

/// <summary>
/// The window side of the GDA bridge (UI programme slice 1.8): what the WPF window's bridge code does to its map, its
/// frame record and its transport bar, plus the thread hop onto the UI thread. The Avalonia app's run view model
/// implements it.
/// </summary>
public interface IBridgeDisplay
{
    /// <summary>
    /// Queue <paramref name="action"/> on the UI thread without waiting (WPF <c>Dispatcher.BeginInvoke</c>). The bridge
    /// client raises its events on its receive loop, which must never block waiting on the UI thread.
    /// </summary>
    void Post(Action action);

    /// <summary>
    /// Show one streamed (or what-if) frame through the same path a replay frame takes (WPF <c>RenderReplayFrame</c>):
    /// the dashboard, every tab and the map, with the frame's boundary transfers. Returns false when the frame was
    /// refused (the SOLVE FAULT path was shown instead), true when every view accepted it and it is now the current
    /// frame.
    /// </summary>
    bool RenderFrame(SimFrame frame);

    /// <summary>A topology arrived: lay the map out on <paramref name="model"/> and fit it (WPF <c>_renderer.Load</c>, <c>FitToView</c>).</summary>
    void ShowTopology(GridModel model);

    /// <summary>The map's condition and stress colour overlays (WPF <c>_renderer.SetOverlay</c>).</summary>
    void SetOverlay(IReadOnlyDictionary<int, double>? conditionByBus, IReadOnlyDictionary<string, double>? stressByBranch);

    /// <summary>Forget the current frame (WPF <c>_last = null</c>), so hover and menus cannot read another model's data.</summary>
    void ClearCurrentFrame();

    /// <summary>The bridge display was held (true) or released (false): the play/pause button follows (WPF PauseBtn).</summary>
    void PausedChanged(bool paused);

    /// <summary>The frame now on screen (WPF <c>_last</c>), or null.</summary>
    SimFrame? CurrentFrame { get; }

    /// <summary>The host's tick (WPF <c>_host.Tick</c>; 0 while the bridge feeds, the host being detached).</summary>
    int HostTick { get; }

    /// <summary>Reconnect to <paramref name="source"/> as choosing it does (WPF <c>LoadBridge(source)</c>, from the reconnect menu).</summary>
    Task ReconnectAsync(ModelSource source);
}

/// <summary>One item of the bridge's map context menu: its text, whether it can be chosen, and what it runs.</summary>
/// <param name="Header">The item's text, exactly as WPF writes it.</param>
/// <param name="Enabled">False for an item shown with the reason it cannot be sent (WPF <c>IsEnabled = false</c>).</param>
/// <param name="Run">What choosing it does; null for the separator.</param>
public sealed record BridgeMenuItem(string Header, bool Enabled, Func<Task>? Run)
{
    /// <summary>The separator WPF puts before "Return to live stream".</summary>
    public static BridgeMenuItem Separator { get; } = new("-", false, null);

    /// <summary>True for the separator.</summary>
    public bool IsSeparator => Run is null;
}

/// <summary>
/// One live GDA bridge feed (UI programme slice 1.8): the WPF window's bridge code (src/GridSim.Wpf/MainWindow.xaml.cs:
/// LoadBridge, SelectedBridgeBack, the three picker handlers, ApplyBridgeStream, DisposeBridgeAsync, SetBridgePaused,
/// the reconnect and what-if context menus, RunFeasibility, RunTripNearest, RunScenario, RunQuery, ToWhatIfFrame and
/// ReturnToLive) moved out of window code, statement for statement and with the same texts, so a second front end runs
/// it without copying it. The map, the frame record and the transport bar are reached through
/// <see cref="IBridgeDisplay"/>; the dashboard through <see cref="Feed"/>.
/// <para>
/// The WPF window keeps its own copy, unchanged: its bridge code is pinned line by line by the WPF source-contract tests
/// (Doc14DesktopPresentationTests, WpfPresentationSourceContractTests, PresentationConstantsAntiDriftTests), so moving it
/// here would have meant retargeting those pins for no change in behaviour. This class is therefore the Avalonia app's
/// bridge, written to the WPF code, and BridgeChoicesWpfParityTests fails the run if the shared constants drift.
/// </para>
/// <para>
/// Transport: <see cref="GridBridgeClient"/> with the default resilience limits, and an unencrypted ws:// endpoint only
/// when the settings file opts in (<see cref="AppSettings.BridgeAllowUnencrypted"/>); the client refuses it otherwise,
/// with its own words, which the status pill and the event log then show (CONNECT FAILED).
/// </para>
/// <para>
/// Desktop only, by registration: a front end creates a session only when the bridge is enabled for its platform (the
/// Avalonia app: <c>ShellOptions.BridgeEnabled</c>, false on Android and iOS), and without one a bridge source is refused.
/// </para>
/// <para>Threading: every public member is called on the UI thread; the client's events are moved onto it with
/// <see cref="IBridgeDisplay.Post"/>, and each one checks that it still belongs to the current client (a late event of a
/// client that has been replaced is dropped, as in WPF).</para>
/// </summary>
public sealed class BridgeSession : ObservableModel
{
    // ---- WPF texts that the tests and the front end read ----

    /// <summary>The single item of the menu offered after a fault (WPF ShowBridgeReconnectMenu).</summary>
    public const string ReconnectItem = "Reconnect to the bridge";

    /// <summary>The item that leaves a what-if (WPF ShowBridgeContextMenu).</summary>
    public const string ReturnToLiveItem = "Return to live stream";

    private readonly AppSettings _settings;
    private readonly IBridgeDisplay _display;

    private GridBridgeClient? _bridge;                   // non-null when streaming live from the GDA bridge
    private ModelSource? _current;                       // the bridge source now loaded (for the reconnect menu)
    private string _bridgeCase = BridgeChoices.Zones[0].Case;        // streamed network, chosen by the zone picker
    private string _bridgeSource = BridgeChoices.Sources[0].Source;  // reconstruction the stream replays, chosen by the source picker
    private bool _bridgeControlsInit;                    // guards the pickers from firing a restart during programmatic setup
    private bool _bridgeLive;                            // set once the first live frame lands, so the pill flips CONNECTING->LIVE
    private bool _bridgeFaulted;                         // the stream faulted: nothing on screen is live; right-click offers reconnect
    private bool _restoreLivePill;                       // a finished query left a non-live pill: the next live frame restores LIVE
    private bool _bridgeRestartRunning, _bridgeRestartPending;   // serialise stream restarts (one Stop/Start pair at a time)
    private DateTime? _bridgeTickUtc;                    // N144: the latest tick's UTC instant (MARKET price trace per settlement period)
    private DateTime _bridgeQueryAt;                     // historical instant on-demand queries are evaluated at (the stream anchor)
    private SimFrame? _lastLive;                         // most recent live frame, restored on "return to live"
    private BridgeProtocol.OverlaysBlock? _lastOverlays; // most recent live overlays, restored on "return to live"
    private bool _whatIf;                                // true while a query result is overlaid: live frames are held off
    private bool _paused;                                // D-DOC-141: the display is held (the stream keeps running)
    // D-DOC-141: the newest HUD scalars and OPERATOR/MARKET blocks that arrived while PAUSED, applied on PLAY.
    private BridgeProtocol.ScalarsBlock? _heldHud;
    private (BridgeProtocol.OperatorBlock? Op, BridgeProtocol.MarketBlock? Mk, DateTime? TickUtc)? _heldMarket;

    private BridgeZoneChoice _selectedZone = BridgeChoices.Zones[0];
    private BridgeStartChoice? _selectedStart = BridgeChoices.Starts[0];
    private BridgeSourceChoice _selectedSource = BridgeChoices.Sources[0];

    /// <summary>
    /// A session that reads <see cref="AppSettings.BridgeAllowUnencrypted"/> from <paramref name="settings"/> at each
    /// connect and drives <paramref name="display"/>. No socket is opened until <see cref="ConnectAsync"/>.
    /// </summary>
    public BridgeSession(AppSettings settings, IBridgeDisplay display)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _display = display ?? throw new ArgumentNullException(nameof(display));
    }

    /// <summary>
    /// The dashboard the session writes to (set by the front end's dashboard feed when it attaches, cleared when it
    /// detaches; while null, the dashboard takes nothing from the bridge, as a detached dashboard takes nothing else).
    /// </summary>
    public IBridgeFeed? Feed { get; set; }

    /// <summary>True while a bridge client is connected or connecting (WPF <c>_bridge is not null</c>).</summary>
    public bool IsActive => _bridge is not null;

    /// <summary>True once the stream has faulted: right-click on the map then offers only <see cref="ReconnectItem"/>.</summary>
    public bool IsFaulted => _bridgeFaulted;

    /// <summary>True once the first live frame of the current stream has been shown (the pill reads LIVE).</summary>
    public bool IsLive => _bridgeLive;

    /// <summary>True while a what-if result is on the map and live frames are held off it.</summary>
    public bool IsWhatIf => _whatIf;

    /// <summary>True while the display is held (D-DOC-141 PAUSE); the stream keeps running on the server.</summary>
    public bool IsPaused => _paused;

    /// <summary>The GDA case the stream carries (WPF <c>_bridgeCase</c>).</summary>
    public string CaseName => _bridgeCase;

    /// <summary>The reconstruction the stream replays (WPF <c>_bridgeSource</c>).</summary>
    public string SourceName => _bridgeSource;

    /// <summary>The historical instant the stream starts at and what-if queries are evaluated at (WPF <c>_bridgeQueryAt</c>).</summary>
    public DateTime QueryAt => _bridgeQueryAt;

    /// <summary>The bridge source now loaded, or null before the first connect.</summary>
    public ModelSource? Current => _current;

    /// <summary>The newest live frame received (shown, held or overlaid by a what-if), or null.</summary>
    public SimFrame? LastLiveFrame => _lastLive;

    /// <summary>
    /// The event-log line the last connect wrote: "Connected to bridge ..." once the stream started, "Bridge connect
    /// failed: ..." when it did not; null while a connect is under way or before the first.
    /// </summary>
    public string? LastConnectLine { get; private set; }

    /// <summary>
    /// Leave the bridge for a local case or a run (WPF SwitchTo: <c>_ = DisposeBridgeAsync()</c> and
    /// <c>_lastOverlays = null</c>): the client is detached at once, so no late frame is shown, and disposed in the
    /// background (the returned task, which never throws).
    /// </summary>
    public Task LeaveAsync()
    {
        _lastOverlays = null;
        return DisposeBridgeAsync();
    }

    /// <summary>The stream restart now running (null before one), so a test can wait for it.</summary>
    public Task? PendingRestart { get; private set; }

    /// <summary>The ZONE picker's choice (WPF BridgeZonePicker). Choosing another zone while streaming restarts the
    /// stream on it; while not streaming (or during programmatic setup) it only moves the picker, as in WPF.</summary>
    public BridgeZoneChoice SelectedZone
    {
        get => _selectedZone;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!BridgeChoices.Zones.Contains(value))
                throw new ArgumentException($"{value.Label} is not one of the ZONE choices.", nameof(value));
            if (EqualityComparer<BridgeZoneChoice>.Default.Equals(_selectedZone, value)) return;
            Set(ref _selectedZone, value);
            // WPF BridgeZonePicker_SelectionChanged: programmatic setup, or not in bridge mode, changes nothing else.
            if (_bridgeControlsInit || _bridge is null) return;
            _bridgeCase = value.Case;
            PendingRestart = ApplyStreamAsync();
        }
    }

    /// <summary>The FROM picker's choice (WPF BridgeStartPicker). Choosing another start while streaming restarts the stream.</summary>
    public BridgeStartChoice? SelectedStart
    {
        get => _selectedStart;
        set
        {
            if (value is not null && !BridgeChoices.Starts.Contains(value))
                throw new ArgumentException($"{value.Label} is not one of the FROM choices.", nameof(value));
            if (EqualityComparer<BridgeStartChoice?>.Default.Equals(_selectedStart, value)) return;
            Set(ref _selectedStart, value);
            if (_bridgeControlsInit || _bridge is null) return;   // WPF BridgeStartPicker_SelectionChanged
            PendingRestart = ApplyStreamAsync();
        }
    }

    /// <summary>The SRC picker's choice (WPF BridgeSourcePicker). The source is recorded whatever the mode (as WPF, before
    /// its guard); choosing another while streaming restarts the stream on it.</summary>
    public BridgeSourceChoice SelectedSource
    {
        get => _selectedSource;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!BridgeChoices.Sources.Contains(value))
                throw new ArgumentException($"{value.Label} is not one of the SRC choices.", nameof(value));
            if (EqualityComparer<BridgeSourceChoice>.Default.Equals(_selectedSource, value)) return;
            Set(ref _selectedSource, value);
            _bridgeSource = value.Source;                          // WPF: recorded before the guard
            if (_bridgeControlsInit || _bridge is null) return;
            PendingRestart = ApplyStreamAsync();
        }
    }

    /// <summary>
    /// The launch arguments' pre-selection (WPF OnLoaded, `--zone`, `--back` and `--bridge-source`), made before any
    /// connect and guarded like the defaults, so it never fires a restart: a zone index also sets the streamed case, a
    /// source index the reconstruction, as WPF sets <c>_bridgeCase</c> and <c>_bridgeSource</c> there.
    /// </summary>
    public void Preselect(int? zoneIndex, int? backIndex, int? sourceIndex)
    {
        _bridgeControlsInit = true;
        try
        {
            if (zoneIndex is int zi) { SelectedZone = BridgeChoices.Zones[zi]; _bridgeCase = BridgeChoices.Zones[zi].Case; }
            if (backIndex is int bi) SelectedStart = BridgeChoices.Starts[bi];
            if (sourceIndex is int si) { SelectedSource = BridgeChoices.Sources[si]; _bridgeSource = BridgeChoices.Sources[si].Source; }
        }
        finally { _bridgeControlsInit = false; }
    }

    // ======================================== connect (WPF LoadBridge) ========================================

    /// <summary>
    /// Connect to <paramref name="s"/>'s bridge endpoint and enter live-stream mode (the session half of WPF
    /// <c>LoadBridge</c>; the front end has already detached its host, set the "bridge" engine text and its transport
    /// bar). The frames are pushed by the server, which owns the physics: the topology arrives once (the map is built
    /// and fitted), and each horizon-guarded <c>state</c> tick is shown through the same path a replay frame takes.
    /// Never throws for a connect or start that fails: that is shown on the pill (CONNECT FAILED) and in the event log,
    /// and the client is disposed, as in WPF.
    /// </summary>
    public async Task ConnectAsync(ModelSource s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Bridge is null) throw new ArgumentException($"{s.Label} is not a bridge source.", nameof(s));

        LastConnectLine = null;
        GridBridgeClient? prev = _bridge;               // dispose the outgoing client after swapping in the new one
        var client = new GridBridgeClient();
        // The default limits, and ws:// only when the settings file opts in (the client refuses it otherwise).
        (ResilienceOptions.Default with { AllowUnencryptedEndpoint = _settings.BridgeAllowUnencrypted }).ApplyTo(client);
        _bridge = client;
        _current = s;
        Feed?.ShowBridgeHint();
        // Drop the previous model's frame alongside the live-frame cache: during CONNECTING the map is about to hold
        // the NEW topology while the current frame still held the OLD one, so hover, right-click and trip-nearest would
        // resolve new-topology ids against old data (WPF fix #7).
        _whatIf = false; _display.ClearCurrentFrame(); _lastLive = null; _bridgeLive = false; _bridgeTickUtc = null;
        _bridgeFaulted = false; _restoreLivePill = false;
        Feed?.ClearSelection();
        Feed?.ClearQueryResult();
        Feed?.ModelChanged();

        // Sync the ZONE picker to the current case without firing a restart (the stream is started below).
        _bridgeControlsInit = true;
        try { SelectedZone = BridgeChoices.Zones.FirstOrDefault(z => z.Case == _bridgeCase) ?? BridgeChoices.Zones[0]; }
        finally { _bridgeControlsInit = false; }
        // D-DOC-141: the stream is server-paced and starts at once, so the bar opens PLAYING (label PAUSE).
        SetPausedField(false);
        _heldHud = null; _heldMarket = null;
        Feed?.SetBridgeStatus($"CONNECTING  *  {_bridgeCase}", BridgeChoices.WarnColour);
        RaiseState();

        // Topology (once): build and fit the map on the UI thread; ignored if the session has since switched away. Posted,
        // never waited for: the receive loop must never block on the UI thread (WPF fix #9).
        client.TopologyReceived += model => _display.Post(() =>
        {
            if (!ReferenceEquals(_bridge, client)) return;
            Feed?.ModelChanged();   // a topology is a (possibly different) model: per-model caches rebuild on its frames
            _display.ShowTopology(model);
        });
        // Every accepted (forward-inference-guarded) tick is shown through the shared replay path, unless a what-if
        // result is overlaid: the live frame is then remembered but held off the map until RETURN TO LIVE. StateReceived
        // also carries the scalars (the HUD) and the overlays (the condition and stress colours).
        client.StateReceived += (f, scalars, overlays) => _display.Post(() =>
        {
            if (!ReferenceEquals(_bridge, client)) return;
            _lastLive = f;
            _lastOverlays = overlays;
            // D-DOC-141: while PAUSED the display is held: the frame, HUD and overlays are remembered (the newest wins)
            // and shown on PLAY. The stream itself keeps running on the server.
            if (_paused) { _heldHud = scalars; return; }
            Feed?.UpdateHud(scalars?.InertiaMwS, scalars?.ConditionIndex, scalars?.RocofHeadroomHzS);
            if (!_bridgeLive) { _bridgeLive = true; Feed?.SetBridgeStatus($"LIVE  *  streaming {_bridgeCase}", BridgeChoices.OkColour); RaiseState(); }
            // A finished scalars-only (or failed) query must not leave WHAT-IF / QUERY FAILED on the pill while live
            // frames render; the query's outcome stays in the results panel, the event log and (on failure) the alarms.
            else if (_restoreLivePill && !_whatIf) { _restoreLivePill = false; Feed?.SetBridgeStatus($"LIVE  *  streaming {_bridgeCase}", BridgeChoices.OkColour); }
            if (!_whatIf)
            {
                _display.RenderFrame(f);
                _display.SetOverlay(overlays?.ConditionByBus, overlays?.StressByBranch);
            }
        });
        // N144: the tick's own UTC instant, for the MARKET price trace's one-sample-per-settlement-period rule.
        // RawDataReceived fires for every accepted tick just BEFORE MarketDataReceived in the same call, and both are
        // posted in that order, so the market handler always reads its own tick's instant.
        client.RawDataReceived += (f, mix, wx, ics, ts) => _display.Post(() =>
        {
            if (!ReferenceEquals(_bridge, client)) return;
            _bridgeTickUtc = ts;
        });
        // The per-tick operator and market series ride their own blocks; they feed OPERATOR, MARKET, NESO RP1 and
        // MECHANICAL, mirroring the HUD side channel above.
        client.MarketDataReceived += (f, op, mk) => _display.Post(() =>
        {
            if (!ReferenceEquals(_bridge, client)) return;
            // D-DOC-141: held while PAUSED (the newest tick's blocks are applied on PLAY).
            if (_paused) { _heldMarket = (op, mk, _bridgeTickUtc); return; }
            Feed?.UpdateMarketData(op, mk, _bridgeTickUtc);
        });
        // Estimated-source frames carry per-tick estimation quality: the chi-square verdict and fit statistics go on
        // the pill, so a measurement-fitted state is never shown without its honesty layer.
        client.EstimateReceived += (f, src, est) => _display.Post(() =>
        {
            // While PAUSED (D-DOC-141) the pill keeps saying so; the next frame after PLAY brings the verdict back.
            if (!ReferenceEquals(_bridge, client) || est is null || _paused) return;
            Feed?.SetBridgeStatus($"ESTIMATED  *  {_bridgeCase}  *  {est.QualitySummary()}",
                est.QualityOk ? BridgeChoices.OkColour : BridgeChoices.BadColour);
        });
        client.Faulted += ex => _display.Post(() =>
        {
            if (!ReferenceEquals(_bridge, client)) return;
            // A receive-loop fault includes a horizon rejection (a tick newer than the 169 h horizon).
            bool horizon = ex is ForwardInferenceException;
            // Fail loud: no more frames will arrive, so nothing on screen is live any more. The lamp reads STREAM LOST,
            // the bridge-only HUD and lake blocks are cleared, an alarm is raised, and right-click offers to reconnect.
            _bridgeFaulted = true;
            _bridgeLive = false;
            Feed?.SetBridgeStatus(horizon ? "HORIZON REJECTION  *  stream stopped  *  right-click the map to reconnect"
                                          : "BRIDGE FAULT  *  disconnected  *  right-click the map to reconnect", BridgeChoices.BadColour);
            Feed?.MarkStreamLost(horizon ? "HORIZON REJECTION" : "BRIDGE FAULT");
            Feed?.Log(Feed.Clock, $"Bridge fault: {ex.Message}");
            RaiseState();
        });
        // One unreadable bridge message: logged (with the running count) while the stream carries on (R275).
        client.MessageRejected += ex => _display.Post(() =>
        {
            if (!ReferenceEquals(_bridge, client)) return;
            Feed?.Log(Feed.Clock, $"Bridge frame rejected ({client.RejectedMessages} so far): {ex.Message}");
        });
        // GR-228: a failure in this app's own handling of a good frame is a GridSim fault, logged as such (not as a bad
        // server message); the stream carries on.
        client.SubscriberFailed += ex => _display.Post(() =>
        {
            if (!ReferenceEquals(_bridge, client)) return;
            Feed?.Log(Feed.Clock, ex.Message);
        });

        if (prev is not null)
        {
#pragma warning disable CA1031 // WPF: best-effort disposal of the outgoing client; the new one is already in charge.
            try { await prev.DisposeAsync().ConfigureAwait(true); } catch (Exception) { /* best-effort */ }
#pragma warning restore CA1031
        }

        try
        {
            await client.ConnectAsync(new Uri(s.Bridge)).ConfigureAwait(true);
            // Stream the chosen DNO zone from the chosen historical start, comfortably behind the no-forward-inference
            // horizon. The same anchor instant is where on-demand what-if queries are evaluated (always historical).
            _bridgeQueryAt = DateTime.UtcNow - SelectedBridgeBack();
            await client.StartAsync(_bridgeCase, _bridgeQueryAt, fps: BridgeChoices.StreamFps, source: _bridgeSource).ConfigureAwait(true);
            LastConnectLine = ConnectedText(s.Bridge, _bridgeCase, _bridgeSource, _bridgeQueryAt);
            Feed?.Log("00:00", LastConnectLine);
        }
#pragma warning disable CA1031 // Not swallowed: shown on the pill and in the event log, as WPF shows it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (ReferenceEquals(_bridge, client))
            {
                Feed?.SetBridgeStatus($"CONNECT FAILED  *  {ex.Message}", BridgeChoices.BadColour);
                LastConnectLine = $"Bridge connect failed: {ex.Message}";
                Feed?.Log("--:--", LastConnectLine);
                await DisposeBridgeAsync().ConfigureAwait(true);
            }
        }
    }

    /// <summary>The WPF event-log line for a stream that connected and started.</summary>
    public static string ConnectedText(string endpoint, string caseName, string source, DateTime at) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Connected to bridge {BridgeProtocol.DisplayEndpoint(endpoint)}  -  streaming {caseName} ({source}) from {at:yyyy-MM-dd HH:mm}Z");

    /// <summary>The historical look-back for the current start preset (WPF SelectedBridgeBack: 9 days when none is selected).</summary>
    public TimeSpan SelectedBridgeBack() => _selectedStart is BridgeStartChoice s ? s.Back : BridgeChoices.DefaultBack;

    // ======================================== restart (WPF ApplyBridgeStream) ========================================

    /// <summary>
    /// Re-anchor the live stream on the currently selected zone and historical start, without reconnecting: stop the
    /// current stream and start the new one on the same socket (WPF ApplyBridgeStream). Any what-if is cleared first.
    /// Restarts are serialised on the one socket: a restart already running only notes that another is wanted, and
    /// when it finishes it runs once more with the NEWEST selection, so a burst of picker changes coalesces into at most
    /// one extra restart. A failed restart is shown on the pill and in the event log, never thrown.
    /// </summary>
    public async Task ApplyStreamAsync()
    {
        if (_bridge is not GridBridgeClient b) return;
        _whatIf = false; Feed?.ClearQueryResult();
        _bridgeLive = false;
        // A new stream plays (D-DOC-141): a held display of the old stream has nothing left to show.
        SetPausedField(false); _heldHud = null; _heldMarket = null;
        // WPF fix #7: the restarted stream can carry a DIFFERENT case, so the cached frames are dropped before the new
        // stream's first frame arrives.
        _display.ClearCurrentFrame(); _lastLive = null; _lastOverlays = null;
        Feed?.ModelChanged();   // the restarted stream may be a different zone (a different model)
        _bridgeQueryAt = DateTime.UtcNow - SelectedBridgeBack();
        Feed?.SetBridgeStatus($"RESTARTING  *  {_bridgeCase}", BridgeChoices.WarnColour);
        if (Feed is not null) Feed.Log(Feed.Clock, string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Bridge: stream {_bridgeCase} from {_bridgeQueryAt:yyyy-MM-dd HH:mm}Z"));
        RaiseState();

        if (_bridgeRestartRunning) { _bridgeRestartPending = true; return; }
        _bridgeRestartRunning = true;
        try
        {
            do
            {
                _bridgeRestartPending = false;
                // Capture the selection BEFORE the first await, so this Stop/Start pair starts the case it was asked for.
                string caseName = _bridgeCase, source = _bridgeSource;
                DateTime at = _bridgeQueryAt;
                try
                {
                    await b.StopAsync().ConfigureAwait(true);
                    await b.StartAsync(caseName, at, fps: BridgeChoices.StreamFps, source: source).ConfigureAwait(true);
                }
#pragma warning disable CA1031 // Not swallowed: shown on the pill and in the event log, as WPF shows it.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    if (ReferenceEquals(_bridge, b))
                    {
                        Feed?.SetBridgeStatus($"RESTART FAILED  *  {ex.Message}", BridgeChoices.BadColour);
                        if (Feed is not null) Feed.Log(Feed.Clock, $"Bridge restart failed: {ex.Message}");
                    }
                }
            } while (_bridgeRestartPending && ReferenceEquals(_bridge, b));
        }
        finally { _bridgeRestartRunning = false; }
    }

    /// <summary>
    /// Detach and dispose the live bridge client, if any (WPF DisposeBridgeAsync). Late frames are ignored once it is
    /// detached. Safe to call with no client.
    /// </summary>
    public Task DisposeBridgeAsync()
    {
        GridBridgeClient? b = _bridge;
        _bridge = null;
        RaiseState();
        return b is null ? Task.CompletedTask : b.DisposeAsync().AsTask();
    }

    // ======================================== pause / hold (WPF Pause_Click, SetBridgePaused) ========================================

    /// <summary>
    /// D-DOC-141: hold (<paramref name="pause"/> true) or release the bridge display (WPF SetBridgePaused). While held,
    /// incoming frames are remembered but not shown and the pill says so; on release the newest held frame, HUD,
    /// overlays and market blocks are shown (unless a what-if result is on the map, which keeps the live frame off).
    /// </summary>
    public void SetPaused(bool pause)
    {
        SetPausedField(pause);
        if (pause)
        {
            Feed?.SetBridgeStatus($"PAUSED  *  {_bridgeCase}  *  display held, stream still running", BridgeChoices.WarnColour);
            return;
        }

        if (_heldHud is { } hud) Feed?.UpdateHud(hud.InertiaMwS, hud.ConditionIndex, hud.RocofHeadroomHzS);
        if (_heldMarket is { } held) Feed?.UpdateMarketData(held.Op, held.Mk, held.TickUtc);
        _heldHud = null; _heldMarket = null;
        if (_whatIf)
        {
            Feed?.SetBridgeStatus("WHAT-IF  *  live frames held off (RETURN TO LIVE to resume)", BridgeChoices.WarnColour);
            return;
        }
        if (_lastLive is SimFrame f)
        {
            _display.RenderFrame(f);
            _display.SetOverlay(_lastOverlays?.ConditionByBus, _lastOverlays?.StressByBranch);
            _bridgeLive = true;
            Feed?.SetBridgeStatus($"LIVE  *  streaming {_bridgeCase}", BridgeChoices.OkColour);
            RaiseState();
        }
        else Feed?.SetBridgeStatus($"CONNECTING  *  {_bridgeCase}", BridgeChoices.WarnColour);   // nothing has arrived yet
    }

    // The one place the hold changes, so the front end's play/pause button always follows it.
    private void SetPausedField(bool paused)
    {
        _paused = paused;
        _display.PausedChanged(paused);
        Raise(nameof(IsPaused));
    }

    // ======================================== RETURN TO LIVE (WPF ReturnToLive) ========================================

    /// <summary>Clear the what-if and resume showing the live stream from its most recent frame (WPF ReturnToLive).
    /// Following the stream again also releases a PAUSE hold (D-DOC-141).</summary>
    public void ReturnToLive()
    {
        _whatIf = false;
        Feed?.ClearQueryResult();
        RaiseState();
        if (_bridge is not null && _paused) { SetPaused(false); return; }
        if (_bridge is not null && _lastLive is SimFrame f)
        {
            _display.RenderFrame(f);
            _display.SetOverlay(_lastOverlays?.ConditionByBus, _lastOverlays?.StressByBranch);
            Feed?.SetBridgeStatus($"LIVE  *  streaming {_bridgeCase}", BridgeChoices.OkColour);
        }
    }

    // ======================================== the map context menu ========================================

    /// <summary>
    /// The bridge's right-click menu on the map, item for item as WPF builds it (Schematic_RightClick,
    /// ShowBridgeReconnectMenu, ShowBridgeContextMenu), for a click on <paramref name="busId"/> or, when it is on no bus,
    /// on the branch at <paramref name="branchIndex"/>. After a fault: the one reconnect item. Otherwise, with a frame on
    /// screen: a bus offers a +500 MW feasibility study and a scenario trip of its largest generator (or, when it has
    /// none, of the largest unit in the model, labelled so); a branch offers a scenario trip of itself, shown disabled
    /// with the reason when the bridge could not address it by name; and while a what-if is shown, a separator and
    /// "Return to live stream". Empty when there is nothing to offer (WPF then opens no menu). Not active: empty.
    /// </summary>
    public IReadOnlyList<BridgeMenuItem> ContextMenu(int? busId, int? branchIndex)
    {
        var items = new List<BridgeMenuItem>();
        if (_bridge is null) return items;
        if (_bridgeFaulted)
        {
            if (_current is { Bridge: not null } source)
                items.Add(new BridgeMenuItem(ReconnectItem, true, () => _display.ReconnectAsync(source)));
            return items;
        }
        if (_display.CurrentFrame is not SimFrame last) return items;
        GridModel m = last.Model;

        if (busId is int bid)
        {
            Bus? bus = m.Buses.FirstOrDefault(b => b.Id == bid);
            string bn = bus?.Name ?? $"bus {bid}";
            items.Add(new BridgeMenuItem(FeasibilityItem(bn), true, () => RunFeasibilityAsync(bid, bn)));
            // Say up front which unit would be tripped: the largest physical unit generating at this bus, or, when the
            // bus has none, the largest unit in the whole model (labelled as such; it is not "nearest" in any sense).
            bool hasLocal = InfeedSelection.LargestOnline(m, bid) is not null;
            items.Add(new BridgeMenuItem(hasLocal
                    ? $"Scenario: trip the largest generator at {bn}"
                    : $"Scenario: trip the largest unit in the model (none generating at {bn})",
                true, () => RunTripNearestAsync(bid, bn)));
        }
        else if (branchIndex is int idx && idx >= 0 && idx < m.Branches.Count)
        {
            Branch br = m.Branches[idx];
            // The server selects the element to trip BY NAME, so an unnamed branch, or a name that also matches other
            // elements, is shown disabled with the reason instead of being sent.
            string? refusal = BridgeChoices.TripRefusal(m, br.Name);
            string nm = br.Name ?? $"{br.From}-{br.To}";
            items.Add(new BridgeMenuItem(
                refusal is null ? $"Scenario: trip this  ({nm})" : $"Scenario: trip this  ({nm})  -  {refusal}",
                refusal is null, () => RunScenarioAsync(nm, $"trip {nm}")));
        }

        if (_whatIf)
        {
            if (items.Count > 0) items.Add(BridgeMenuItem.Separator);
            items.Add(new BridgeMenuItem(ReturnToLiveItem, true, () => { ReturnToLive(); return Task.CompletedTask; }));
        }
        return items;
    }

    /// <summary>The feasibility item's text for a bus named <paramref name="busName"/> (WPF ShowBridgeContextMenu).</summary>
    public static string FeasibilityItem(string busName) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Feasibility: +{BridgeChoices.FeasibilityProbeMw:F0} MW here  ({busName})");

    // ======================================== what-if queries (WPF RunFeasibility ... RunQuery) ========================================

    /// <summary>WPF RunFeasibility: a +500 MW new-load feasibility study at the bus.</summary>
    public Task RunFeasibilityAsync(int busId, string busName) =>
        RunQueryAsync("feasibility", new { kind = "new_load", bus = busId, mw = BridgeChoices.FeasibilityProbeMw },
            string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Feasibility  *  +{BridgeChoices.FeasibilityProbeMw:F0} MW at {busName}"));

    /// <summary>
    /// WPF RunTripNearest: a scenario trip of the largest PHYSICAL unit generating at the bus (the shared Core rule,
    /// <see cref="InfeedSelection"/>), or with none there, of the largest unit in the model (and the label says so).
    /// A unit the bridge could not address by name is refused with the reason in the event log, never sent.
    /// </summary>
    public Task RunTripNearestAsync(int busId, string busName)
    {
        if (_display.CurrentFrame is not SimFrame last) return Task.CompletedTask;
        GridModel m = last.Model;
        Generator? local = InfeedSelection.LargestOnline(m, busId)?.Unit;
        Generator? g = local ?? InfeedSelection.LargestOnline(m)?.Unit;
        if (g is null) { Feed?.Log(Feed.Clock, $"No generator to trip near {busName}"); return Task.CompletedTask; }
        // The server picks the unit BY NAME; refuse a name that is missing or not unique rather than trip a namesake.
        if (BridgeChoices.TripRefusal(m, g.Name) is string refusal)
        {
            Feed?.Log(Feed.Clock, $"Scenario not sent for {g.Name ?? $"the unit at bus {g.Bus}"}: {refusal}");
            return Task.CompletedTask;
        }
        string trip = g.Name!;
        return RunScenarioAsync(trip, local is not null
            ? $"trip {trip} (largest generator at {busName})"
            : $"trip {trip} (largest unit in the model; none generating at {busName})");
    }

    /// <summary>WPF RunScenario: a scenario query that trips the element named <paramref name="trip"/>.</summary>
    public Task RunScenarioAsync(string trip, string label) =>
        RunQueryAsync("scenario", new { trip }, $"Scenario  *  {label}");

    /// <summary>
    /// Fire an on-demand bridge query at the historical stream anchor and show the result (WPF RunQuery): a converged
    /// <c>solveExport</c> becomes a what-if frame shown through the same path as every frame (dashboard, tabs, map)
    /// with live frames held off and the lamp marked WHAT-IF; a solve that did not converge is never painted (the panel
    /// says so); verdict, binding constraint and violations go to the results panel. Ops without a solve show the panel
    /// only. A failed query is shown on the pill, as an alarm and in the event log, never thrown.
    /// </summary>
    public async Task RunQueryAsync(string op, object @params, string title)
    {
        if (_bridge is not GridBridgeClient b) return;
        Feed?.SetBridgeStatus($"QUERY  *  {op} ...", BridgeChoices.WarnColour);
        if (Feed is not null) Feed.Log(Feed.Clock, $"Bridge query: {title} ...");
        QueryResult qr;
        try { qr = await b.Query(op, _bridgeQueryAt, @params).ConfigureAwait(true); }   // the continuation resumes on the UI thread
#pragma warning disable CA1031 // Not swallowed: shown on the pill, as an alarm and in the event log, as WPF shows it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (ReferenceEquals(_bridge, b))
            {
                Feed?.SetBridgeStatus($"QUERY FAILED  *  {op}: {ex.Message}", BridgeChoices.BadColour);
                // The pill returns to LIVE on the next frame, so the failure is also kept in the alarm list.
                Feed?.AddAlarm($"QUERY FAILED: {op}: {ex.Message}", BridgeChoices.BadColour);
                _restoreLivePill = true;
            }
            if (Feed is not null) Feed.Log(Feed.Clock, $"Query '{op}' failed: {ex.Message}");
            return;
        }
        if (!ReferenceEquals(_bridge, b)) return;   // switched away while the query was in flight

        bool hasOverlay = false, notConverged = false;
        if (qr.Solve is SolveSnapshot snap)
        {
            if (!snap.Result.Converged)
            {
                // Fail loud: a solve that did not converge is not an operating point, so it is never painted as the
                // what-if state. The verdict and violations still show in the results panel, flagged as not converged.
                notConverged = true;
            }
            else
            {
                // The what-if frame goes through the SAME path as every frame (lamp, read-outs, boundaries, tabs and the
                // map), so the dashboard and the map describe one state. The lamp is then marked WHAT-IF.
                _whatIf = true;                      // freeze live frames while the solved what-if is on the map
                hasOverlay = _display.RenderFrame(ToWhatIfFrame(snap));   // hover and click now read the what-if state
                if (hasOverlay) { if (Feed is not null) Feed.Status = $"WHAT-IF  *  {Feed.Status.Trim()}"; }
                else _whatIf = false;                // the frame was refused (SOLVE FAULT shown); stay on the live stream
            }
        }
        Feed?.ShowQueryResult(qr, title, hasOverlay, notConverged);
        // WHAT-IF only when a what-if state is actually on the map; otherwise the stream is still showing, so the pill
        // goes back to LIVE (now, or on the next frame) and the outcome lives in the results panel.
        if (hasOverlay) Feed?.SetBridgeStatus($"WHAT-IF  *  {qr.Verdict ?? op}", BridgeChoices.WarnColour);
        else if (_bridgeLive && !_paused) Feed?.SetBridgeStatus($"LIVE  *  streaming {_bridgeCase}", BridgeChoices.OkColour);
        else _restoreLivePill = true;
        if (Feed is not null)
            Feed.Log(Feed.Clock, $"{op} -> {qr.Verdict ?? "(no verdict)"}" +
                                 (string.IsNullOrWhiteSpace(qr.BindingConstraint) ? "" : $"  binding {qr.BindingConstraint}") +
                                 $"  *  {qr.Violations.Count} violation(s)");
        RaiseState();
    }

    /// <summary>
    /// UI programme ASSETS A3: the WPF window's BridgeApplyAsset, statement for statement, for the ASSETS tab's
    /// substation page: fetch bus <paramref name="busId"/>'s real per-asset data over the bridge (<c>op="asset"</c> at the
    /// historical stream anchor) and hand it to the page. <paramref name="page"/> is the opened page's ApplyAsset (it is
    /// first told the query is under way); <paramref name="stillShowing"/> gives the ApplyAsset of the substation page on
    /// screen when it still shows that same bus, else null (the user may have navigated on). Graceful on empty or failure,
    /// as WPF: a failed query is shown on the page, an empty one says no data was resolved. Does nothing without a bridge.
    /// UI programme ASSETS fix S6: the WPF window calls its own copy only on the WPF route (the map's OPEN IN EXPLORER on a
    /// bus, in bridge mode; WPF keeps that quirk until Phase 2 retires it); the Avalonia app calls this through
    /// GridSim.Ui.Assets.Bridge.AssetInsightSource on every route to a substation page.
    /// </summary>
    public async Task ApplyAssetAsync(int busId, Action<AssetData?, string> page, Func<Action<AssetData?, string>?> stillShowing)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(stillShowing);
        if (_bridge is not GridBridgeClient b) return;
        page(null, "querying GDA per-asset data ...");
        QueryResult qr;
        try { qr = await b.Query("asset", _bridgeQueryAt, new { busId }).ConfigureAwait(true); }   // continuation on UI thread
#pragma warning disable CA1031 // Not swallowed: shown on the page, as WPF shows it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            page(null, $"asset query failed: {ex.Message}");
            return;
        }
        if (!ReferenceEquals(_bridge, b)) return;                             // switched away mid-flight
        // Only apply if the same substation page is still open (the user may have navigated on).
        if (stillShowing() is { } cur)
        {
            bool has = qr.Asset is { IsEmpty: false };
            cur(qr.Asset,
                has ? $"GDA @ {_bridgeQueryAt:yyyy-MM-dd HH:mm}Z  *  bus {busId}"
                    : $"no GDA per-asset data resolved for bus {busId}");
        }
    }

    /// <summary>Wrap a solved snapshot as a <see cref="SimFrame"/> so map hover and selection read the what-if state (WPF ToWhatIfFrame).</summary>
    public SimFrame ToWhatIfFrame(SolveSnapshot snap)
    {
        ArgumentNullException.ThrowIfNull(snap);
        double gen = snap.Result.PInjMw.Where(p => p > 0).Sum();
        double demand = snap.Model.Buses.Sum(b => b.Pd);
        double hour = _display.CurrentFrame?.Hour ?? 0.0;
        // N273 (October 2026, stream B2): a steady what-if snapshot sits at the case's own nominal (50.0 for a GB case).
        return new SimFrame(snap.Model, snap.Result, hour, _display.HostTick, snap.Model.FrequencyHz, gen, demand, 0.0);
    }

    // The state flags a front end may bind to changed together.
    private void RaiseState()
    {
        Raise(nameof(IsActive));
        Raise(nameof(IsFaulted));
        Raise(nameof(IsLive));
        Raise(nameof(IsWhatIf));
    }
}
