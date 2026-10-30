using System.Runtime.ExceptionServices;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using GridSim.Core.Analysis;
using GridSim.Core.Dynamics;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Hosting;
using GridSim.Ui.Models;
using GridSim.Ui.Settings;
using GridSim.Ui.Shell;
using GridSim.Ui.Tabs.Generation;

namespace GridSim.App.Run;

/// <summary>
/// The run pipeline of the Avalonia app (UI programme Phase 1.2): it loads a case or a pre-solved run through the
/// shared <see cref="SimHost"/>, drives the host's play loop, and turns each frame into its text record
/// (<see cref="LivePanelText"/>, which the error area and the status read; since the integration of 2026-10-20 the window
/// shows no live panel: the dashboard tiles, fed through the dashboard host from this class's feed events, show the
/// values in the WPF form).
/// <para>
/// Everything that decides what is loaded and computed is the host's, exactly as the WPF window uses it: the model is
/// read, licence-checked (the same gate, cap and refusal text) and built into a <c>LiveSim</c> inside
/// <see cref="SimHost.LoadLive"/>, a run folder is read by <see cref="SimHost.LoadReplay"/>, and every frame is the
/// host's. This class only follows the WPF window's display rules (its <c>SwitchTo</c>, <c>LoadReplay</c>,
/// <c>Pause_Click</c>, <c>Speed_Click</c>, <c>StepPicker_SelectionChanged</c>, <c>CycleStep</c> and
/// <c>ShowHostFrame</c>), with the same texts.
/// </para>
/// <para>
/// Threading. The host's loop runs on the thread pool; each frame is handed to the UI thread with
/// <see cref="Dispatcher.UIThread"/> and the loop waits until it has been shown before it makes the next, so solves
/// never overlap and the UI thread never waits for a solve. Opening another case and closing the window both cancel
/// the loop and wait (without blocking the UI thread) until it has ended, so no loop is ever left running and no frame
/// from an old loop can be shown. A loop that itself fails is rethrown on the UI thread, as the WPF window does, so it
/// is never left as an unobserved task.
/// </para>
/// </summary>
public sealed class RunViewModel : ObservableObject, IRunActions
{
    // ---- WPF texts, copied exactly (MainWindow.xaml.cs and DashboardViewModel.cs) ----

    /// <summary>The play/pause button while frames are being made (WPF PauseBtn).</summary>
    public const string PauseLabel = " PAUSE";

    /// <summary>The play/pause button on a paused replay (WPF PauseBtn).</summary>
    public const string PlayLabel = " PLAY";

    /// <summary>The play/pause button on a paused live case (WPF PauseBtn).</summary>
    public const string ResumeLabel = " RESUME";

    /// <summary>The WPF alarm raised when a frame's power flow did not converge.</summary>
    public const string NotConvergedAlarm = "POWER FLOW DID NOT CONVERGE - state not valid";

    /// <summary>The WPF banner shown over a tab while the frame shown did not converge.</summary>
    public const string StateNotValidBanner =
        "STATE NOT VALID  -  the power flow did not converge; every value on this tab comes from an unconverged iterate";

    /// <summary>The WPF status lamp's text for a solve that threw, or a frame that could not be shown.</summary>
    public const string SolveFaultStatus = " SOLVE FAULT";

    /// <summary>
    /// The refusal for a live GDA bridge source on a run view model made without a bridge (UI programme slice 1.8: the
    /// bridge is desktop only, so a head where it is not registered, such as a mobile head, refuses a bridge source in
    /// plain words rather than ignoring it). The text is unchanged from before the bridge was ported.
    /// </summary>
    public const string BridgeNotAvailableText =
        "The live GDA bridge feed is not available in this app yet; open a local case or a run folder instead.";

    /// <summary>The WPF ActiveSolverText while a bridge feeds (LoadBridge).</summary>
    public const string BridgeSolverText = "bridge";

    /// <summary>Why STEP ONCE does nothing on a bridge feed: the server paces the stream (WPF has no step there).</summary>
    public const string BridgeStepText = "A bridge stream is paced by the server, so there is no frame to step; use PAUSE to hold the display.";

    private readonly LicenceGate _licence;

    private ModelSource? _current;           // the case or run now loaded (WPF _current)
    private bool _paused;                    // WPF _paused
    private bool _stepPending;               // a live Step once is waiting for its one frame
    private double _playback = 1;            // WPF _playback, the SPEED button's rate
    private SolverChoice _solver = TransportChoices.Solvers[0];                       // WPF: Auto first
    private StepChoice _step = TransportChoices.Steps[TransportChoices.Steps.Count - 1];  // WPF: 20 min, the last
    private CancellationTokenSource? _loopCts;

    // ---- shown text ----
    private bool _hasRun, _isReplay, _stateValid = true;
    private string _caseName = "", _loadedText = "", _activeSolverText = "", _frameReadout = "";
    private string _playPauseLabel = PauseLabel, _speedLabel = RunCommands.SpeedLabel(1);
    private string _status = "";
    private LivePanelText? _panel;
    private string? _loadError, _frameError;

    /// <summary>
    /// A run view model whose live cases are checked against <paramref name="licence"/> (WPF App.Licence). A map trip
    /// runs the GENERATION tab's swing study on <paramref name="tripDashboard"/> and writes its event-log lines and its
    /// TRIP alarm there, as the WPF trip does on the window's dashboard (UI programme integration, 2026-10-20); a run
    /// view model made without one refuses a trip (see <see cref="TripAtBus"/>).
    /// <para>
    /// UI programme slice 1.8: with <paramref name="bridgeSettings"/> the run view model also carries a GDA bridge
    /// session (<see cref="Bridge"/>), connecting with that settings file's rules (BridgeAllowUnencrypted). Without one
    /// (the bridge not registered, as on a mobile head) a bridge source is refused with <see cref="BridgeNotAvailableText"/>.
    /// </para>
    /// </summary>
    public RunViewModel(LicenceGate licence, DashboardModel? tripDashboard = null, AppSettings? bridgeSettings = null)
    {
        _licence = licence ?? throw new ArgumentNullException(nameof(licence));
        TripDashboard = tripDashboard;
        if (bridgeSettings is not null)
        {
            Bridge = new BridgeSession(bridgeSettings, new BridgeDisplay(this));
            // The play/pause state shown follows the bridge client: while one is connected PAUSE holds its display; once it
            // is gone (a failed connect) the button is the local toggle again, as in WPF.
            Bridge.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(BridgeSession.IsActive)) OnPropertyChanged(nameof(IsPaused));
            };
        }
        // The two licence lines WPF shows: the tier in the title bar after the version, and the full description in the
        // event log at start-up (LicenceGate.Describe, the same line as the web footer and the About box).
        LicenceTierText = $"v{GridSim.Core.BuildInfo.Version}  -  {licence.Entitlement.Tier.DisplayName()} licence";
        LicenceText = $"Licence: {licence.Describe()}";
    }

    /// <summary>The simulation host every frame comes from.</summary>
    public SimHost Host { get; } = new();

    /// <summary>
    /// The dashboard a map trip runs its swing study on and logs to (the main window's one dashboard), or null for a
    /// run view model made without one (a trip is then refused, fail loud).
    /// </summary>
    public DashboardModel? TripDashboard { get; }

    /// <summary>The running loop (null before the first load); tests check that it ends when it is cancelled.</summary>
    public Task? LoopTask { get; private set; }

    /// <summary>Raised on the UI thread each time a frame has been put on the panel, with the text it now shows.</summary>
    public event EventHandler<LivePanelText>? FrameShown;

    /// <summary>Raised when the feed changes between a live case and a replay (the Run commands' enabled state).</summary>
    public event EventHandler? FeedChanged;

    /// <summary>
    /// Raised on the UI thread when a case or run has loaded, with the model the map lays out (the live model, or a
    /// replay's layout model), before its first frame is shown: the moment the WPF window calls the map's Load.
    /// </summary>
    public event EventHandler<GridModel>? ModelLoaded;

    /// <summary>
    /// Raised on the UI thread with each frame as it is put on screen, just before <see cref="FrameShown"/>: the
    /// moment the WPF window pushes the frame's state into the map (SetState, SetBoundaries). Anything a handler throws
    /// takes the WPF render-fault path (the frame is reported in the error area, the loop goes on).
    /// </summary>
    public event EventHandler<ArrivedFrame>? FrameArrived;

    /// <summary>
    /// The last frame every view accepted (the WPF window's <c>_last</c>): set only after the frame was shown without a
    /// fault, cleared when another case loads. The map's hover, selection fly-out and trips read it; null means there
    /// is nothing to read yet, and those then do nothing, as in WPF.
    /// </summary>
    public SimFrame? LastFrame { get; private set; }

    /// <summary>The model the map was laid out from (the live model or the replay's layout model), or null.</summary>
    public GridModel? LoadedModel { get; private set; }

    /// <summary>
    /// UI programme ASSETS slice 5-view: the asset overlay the loaded JSON v2 case carried beside its model (its
    /// manifest's "assets" files, checked by the loader), or null (no overlay; a v1 case, a MATPOWER file, a run or the
    /// bridge). Carried, not used by the solver: the ASSETS tab shows it, nothing else reads it. Set before
    /// <see cref="ModelLoaded"/> is raised, so the tab's source declaration can hand it on.
    /// </summary>
    public GridSim.Core.IO.Json.Overlay.CaseAssetOverlay? LoadedOverlay { get; private set; }

    /// <summary>
    /// The case, run or bridge source now loaded (the WPF <c>_current</c>), or null before the first load. UI programme
    /// ASSETS B2: the ASSETS tab reads it to know whether the model came from a case file (PROPERTIES) or not.
    /// </summary>
    public ModelSource? CurrentSource => _current;
    // ---- Phase 1.4: the dashboard's feed (RunFeedEvents.cs). Each event is raised at the point where the WPF window
    // called its DashboardViewModel, on the UI thread, so the dashboard can make the same calls in the same order. ----

    /// <summary>A local case or run is about to be loaded (WPF SwitchTo: HideHud and ClearBridgeStatus, before the load,
    /// whether or not the load then succeeds).</summary>
    public event EventHandler? FeedLoading;

    /// <summary>The host or the licence gate refused a load, with its own words (WPF: logged at "--:--").</summary>
    public event EventHandler<string>? LoadRefused;

    /// <summary>A new model and timeline are about to be shown, before its first frame (WPF: MapHint, ClearSelection,
    /// ModelChanged).</summary>
    public event EventHandler<RunLoadedEventArgs>? ModelSwitched;

    /// <summary>The load finished: for a run, after its first frame was shown (WPF: the "Loaded ..." event-log line).</summary>
    public event EventHandler<RunLoadedEventArgs>? FeedLoaded;

    /// <summary>A frame is about to be shown (WPF: the DashboardViewModel.Update call of ShowHostFrame and
    /// RenderReplayFrame, or the SOLVE FAULT path for a solve that threw).</summary>
    public event EventHandler<RunFrameEventArgs>? FrameArriving;

    /// <summary>The timeline was broken without a model switch: a finished replay played again from its first frame
    /// (WPF Pause_Click: ResetTimeline); slice 1.7: also RESTART, STEP BACK and a backward (or to-frame-0) scrub, at the
    /// points WPF calls ResetTimeline (after the cursor moved, before its frame is shown).</summary>
    public event EventHandler? TimelineReset;

    // ======================================== what the window shows ========================================

    /// <summary>True once a case or a run has been loaded: the transport bar is shown from then on.</summary>
    public bool HasRun { get => _hasRun; private set => SetProperty(ref _hasRun, value); }

    /// <summary>True while a pre-solved run is loaded (the SOLVER choice is disabled and the STEP choice hidden, as in WPF).</summary>
    public bool IsReplay
    {
        get => _isReplay;
        private set
        {
            if (!SetProperty(ref _isReplay, value)) return;
            OnPropertyChanged(nameof(IsLive));
            OnPropertyChanged(nameof(CanChooseSolver));
            OnPropertyChanged(nameof(CanChooseStep));
        }
    }

    /// <summary>
    /// True while the user has paused (the WPF window's <c>_paused</c>): a paused live case, a paused replay, or a live
    /// case being stepped once. The map's marching dashes stand still then, as in WPF.
    /// </summary>
    public bool IsPaused => Bridge is { IsActive: true } b ? b.IsPaused : _paused;

    /// <summary>
    /// True while a live case is loaded (neither a replay nor, slice 1.8, a bridge feed): the STEP choice shows only
    /// then, as WPF shows its STEP picker only for a local model.
    /// </summary>
    public bool IsLive => !_isReplay && !_isBridge;

    /// <inheritdoc />
    public bool CanChooseSolver => !_isReplay && !_isBridge;

    /// <inheritdoc />
    public bool CanChooseStep => !_isReplay && !_isBridge;

    /// <summary>
    /// False while the SPEED button has nothing to pace (slice 1.8: a bridge stream is paced by the server, so WPF
    /// disables its SPEED button then).
    /// </summary>
    public bool CanChooseSpeed => !_isBridge;

    // ======================================== the GDA bridge (slice 1.8) ========================================

    private bool _isBridge;

    /// <summary>
    /// The GDA bridge session (UI programme slice 1.8: the WPF window's bridge code, in GridSim.Ui), or null when this
    /// run view model was made without a bridge (it is then refused, with <see cref="BridgeNotAvailableText"/>).
    /// </summary>
    public BridgeSession? Bridge { get; }

    /// <summary>
    /// True while the loaded feed is a bridge source (WPF: the transport bar in bridge mode, from LoadBridge until a local
    /// case or a run loads). The ZONE, FROM and SRC pickers show only then, and the SOLVER, STEP and SPEED controls are
    /// off then, as in WPF. Whether a client is connected right now is <see cref="BridgeSession.IsActive"/>.
    /// </summary>
    public bool IsBridge
    {
        get => _isBridge;
        private set
        {
            if (!SetProperty(ref _isBridge, value)) return;
            OnPropertyChanged(nameof(IsLive));
            OnPropertyChanged(nameof(CanChooseSolver));
            OnPropertyChanged(nameof(CanChooseStep));
            OnPropertyChanged(nameof(CanChooseSpeed));
            OnPropertyChanged(nameof(CanStepOnce));
            OnPropertyChanged(nameof(IsPaused));
            FeedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>True when STEP ONCE has something to step (not on a bridge feed, which the server paces).</summary>
    public bool CanStepOnce => !_isBridge;

    /// <summary>
    /// Raised on the UI thread with the bridge's condition and stress colour overlays for the map (WPF
    /// <c>_renderer.SetOverlay</c>), after the frame they came with was shown.
    /// </summary>
    public event EventHandler<MapOverlay>? OverlayArrived;

    // WPF LoadBridge, the window half: the host is detached (no local solver or replay; the server streams solved
    // frames), the engine text says "bridge", and the transport bar enters bridge mode (no STEP, no scrubber, SOLVER and
    // SPEED off, the bridge pickers shown). Then the session connects (the dashboard half, BridgeSession.ConnectAsync).
    private async Task<string?> LoadBridgeAsync(ModelSource s)
    {
        BridgeSession bridge = Bridge!;
        _current = s;
        Host.Detach();
        ActiveSolverText = BridgeSolverText;
        _paused = false;
        _stepPending = false;
        IsReplay = false;
        FrameReadout = "";
        CaseName = s.Label;
        LoadError = null;
        ClearFrame();
        _trippedUnits.Clear();
        HasRun = true;
        LoadedModel = null;
        IsBridge = true;   // before the connect: the pickers and the disabled controls show while CONNECTING, as WPF
        await bridge.ConnectAsync(s).ConfigureAwait(true);
        // The status bar carries the event-log line the connect wrote: "Connected to bridge ..." or "Bridge connect failed:
        // ...". A failed connect leaves the transport bar in bridge mode with no client, as WPF leaves it, until another
        // source is chosen (or the same one again).
        LoadedText = bridge.LastConnectLine ?? "";
        return null;
    }

    // The session's window side (BridgeSession's IBridgeDisplay), over this run view model.
    private sealed class BridgeDisplay : IBridgeDisplay
    {
        private readonly RunViewModel _run;

        public BridgeDisplay(RunViewModel run) => _run = run;

        public void Post(Action action) => Dispatcher.UIThread.Post(action);

        public bool RenderFrame(SimFrame frame) => _run.ShowBridgeFrame(frame);

        public void ShowTopology(GridModel model)
        {
            Dispatcher.UIThread.VerifyAccess();
            // WPF: _renderer.Load(model); _renderer.FitToView() (the dashboard's ModelChanged came just before).
            _run.LoadedModel = model;
            _run.LoadedOverlay = null;   // slice 5-view: a streamed topology carries no asset overlay
            _run.ModelLoaded?.Invoke(_run, model);
        }

        public void SetOverlay(IReadOnlyDictionary<int, double>? conditionByBus, IReadOnlyDictionary<string, double>? stressByBranch)
        {
            Dispatcher.UIThread.VerifyAccess();
            _run.OverlayArrived?.Invoke(_run, new MapOverlay(conditionByBus, stressByBranch));
        }

        public void ClearCurrentFrame() => _run.ClearFrame();

        public void PausedChanged(bool paused)
        {
            // WPF: PauseBtn.Content = pause ? " PLAY" : " PAUSE" (the hold does not touch the local _paused).
            _run.PlayPauseLabel = paused ? PlayLabel : PauseLabel;
            _run.OnPropertyChanged(nameof(IsPaused));
        }

        public SimFrame? CurrentFrame => _run.LastFrame;

        public int HostTick => _run.Host.Tick;

        public Task ReconnectAsync(ModelSource source) => _run.LoadAsync(source);
    }

    // WPF RenderReplayFrame for a streamed or what-if frame: the dashboard's update (and, through its hooks, every tab),
    // then the map with the frame's boundary transfers, then the frame becomes the current one. A frame the dashboard
    // refused takes the SOLVE FAULT path and goes no further. False when it was refused or no bridge is active (WPF's
    // guard: a frame with neither a replay nor a bridge to belong to is not shown).
    private bool ShowBridgeFrame(SimFrame f)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (Bridge is not { IsActive: true }) return false;
        try
        {
            var arriving = new RunFrameEventArgs { Kind = RunFrameKind.Bridge, Frame = f };
            FrameArriving?.Invoke(this, arriving);
            if (arriving.Rejection is { } refused)
            {
                Status = SolveFaultStatus;
                FrameError = $"Render failed: {refused.Message}";
                return false;
            }
            IReadOnlyList<BoundaryFlow> boundaries = f.Result.Converged
                ? BoundaryAnalysis.Evaluate(f.Model, f.Result)
                : Array.Empty<BoundaryFlow>();
            ShowFrame(f, boundaries);
        }
#pragma warning disable CA1031 // Not swallowed: shown in the error area, as WPF shows it on the lamp and the alarms.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Status = SolveFaultStatus;
            FrameError = $"Render failed: {ex.Message}";
            return false;
        }
        return true;
    }

    /// <inheritdoc />
    public SolverChoice CurrentSolver => _solver;

    /// <inheritdoc />
    public StepChoice CurrentStep => _step;

    /// <inheritdoc />
    public double CurrentSpeed => _playback;

    /// <summary>The loaded case's or run's name (its label in the case list).</summary>
    public string CaseName { get => _caseName; private set => SetProperty(ref _caseName, value); }

    /// <summary>The WPF event-log line written when the case or run was loaded.</summary>
    public string LoadedText { get => _loadedText; private set => SetProperty(ref _loadedText, value); }

    /// <summary>WPF ActiveSolverText: "auto * &lt;engine&gt;", the engine itself, or "replay".</summary>
    public string ActiveSolverText { get => _activeSolverText; private set => SetProperty(ref _activeSolverText, value); }

    /// <summary>WPF FrameReadout for a replay: "frame i/n  *  label"; empty for a live case.</summary>
    public string FrameReadout { get => _frameReadout; private set => SetProperty(ref _frameReadout, value); }

    /// <summary>The play/pause button's text, exactly as WPF writes it.</summary>
    public string PlayPauseLabel { get => _playPauseLabel; private set => SetProperty(ref _playPauseLabel, value); }

    /// <summary>The SPEED button's text: x1, x5, x15 or x60.</summary>
    public string SpeedLabel { get => _speedLabel; private set => SetProperty(ref _speedLabel, value); }

    /// <summary>The playback rate chosen (the host's <see cref="SimHost.PlaybackRate"/> follows it).</summary>
    public double Playback => _playback;

    /// <summary>The WPF status-lamp text this panel can already give: a solve fault or not converged (else empty).</summary>
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>The tier line WPF puts in its title bar: "v&lt;version&gt;  -  &lt;tier&gt; licence".</summary>
    public string LicenceTierText { get; }

    /// <summary>The licence line WPF writes to its event log at start-up: "Licence: &lt;description&gt;".</summary>
    public string LicenceText { get; }

    /// <summary>The text of the frame shown last, or null before the first frame of the loaded case.</summary>
    public LivePanelText? Panel
    {
        get => _panel;
        private set
        {
            if (!SetProperty(ref _panel, value)) return;
            OnPropertyChanged(nameof(HasFrame));
        }
    }

    /// <summary>True once a frame of the loaded case is on the panel.</summary>
    public bool HasFrame => _panel is not null;

    /// <summary>
    /// False while the frame shown did not converge: every value on the panel then comes from an unconverged iterate,
    /// so the panel shows them in the alarm colour under the WPF STATE NOT VALID banner, never as normal values.
    /// </summary>
    public bool StateValid
    {
        get => _stateValid;
        private set
        {
            if (!SetProperty(ref _stateValid, value)) return;
            OnPropertyChanged(nameof(StateInvalid));
        }
    }

    /// <summary>The opposite of <see cref="StateValid"/>, for the banner's visibility.</summary>
    public bool StateInvalid => !_stateValid;

    /// <summary>Why the last load or command was refused (the host's or the licence gate's own words), or null.</summary>
    public string? LoadError
    {
        get => _loadError;
        set
        {
            if (!SetProperty(ref _loadError, value)) return;
            OnPropertyChanged(nameof(ErrorText));
            OnPropertyChanged(nameof(HasError));
        }
    }

    /// <summary>What is wrong with the frame shown (not converged, a solve fault, a render fault), or null.</summary>
    public string? FrameError
    {
        get => _frameError;
        private set
        {
            if (!SetProperty(ref _frameError, value)) return;
            OnPropertyChanged(nameof(ErrorText));
            OnPropertyChanged(nameof(HasError));
        }
    }

    /// <summary>The error area's text: the refusal and the frame's problem, one per line; empty when there is none.</summary>
    public string ErrorText => string.Join(Environment.NewLine, new[] { _loadError, _frameError }.Where(s => s is not null));

    /// <summary>True while the error area has something to say.</summary>
    public bool HasError => _loadError is not null || _frameError is not null;

    /// <summary>The SOLVER choice; changing it rebuilds a running live case on the new engine (WPF SolverPicker).</summary>
    public SolverChoice SelectedSolver
    {
        get => _solver;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_solver == value) return;
            ReportFailure(ChooseSolverAsync(value));
        }
    }

    // A picker's setter cannot await: run the change and show anything it throws in the error area, never lose it.
    private async void ReportFailure(Task change)
    {
        try
        {
            await change.ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Not swallowed: shown in the error area.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LoadError = ex.Message;
        }
    }

    /// <summary>The STEP choice (WPF StepPicker); the host reads it when the next step starts.</summary>
    public StepChoice SelectedStep
    {
        get => _step;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!SetProperty(ref _step, value)) return;
            Host.StepMinutes = value.Minutes;
        }
    }

    // ======================================== loading ========================================

    /// <summary>
    /// Load <paramref name="source"/> as the WPF window's <c>SwitchTo</c> does: a run folder through
    /// <see cref="SimHost.LoadReplay"/>, a local case through <see cref="SimHost.LoadLive"/> on the chosen engine and
    /// under the licence gate. Returns null when it loaded, else the refusal, which is also shown in the error area.
    /// On a refusal nothing else changes: whatever ran before goes on running, as in WPF.
    /// </summary>
    public async Task<string?> LoadAsync(ModelSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Bridge is not null)
        {
            // Slice 1.8: WPF SwitchTo sends a bridge source to LoadBridge before anything else. A run view model made
            // without a bridge (not registered on this head) refuses it in plain words, and nothing changes.
            if (Bridge is null)
            {
                LoadError = BridgeNotAvailableText;
                return BridgeNotAvailableText;
            }
            // The local loop ends first (the host is detached next: the server owns the physics and the pace).
            await StopLoopAsync().ConfigureAwait(true);
            return await LoadBridgeAsync(source).ConfigureAwait(true);
        }

        // Stop the current loop first and wait for it to end, so it can never step the next model or show a frame of the
        // old one. The host itself is untouched by this: on a refusal the same feed runs on in a fresh loop.
        await StopLoopAsync().ConfigureAwait(true);
        try
        {
            // Slice 1.8, WPF SwitchTo: "_ = DisposeBridgeAsync()", leaving any live bridge behind for a local model or a
            // run, before the load and whether or not it then succeeds. The client is detached at once (no late frame
            // reaches the views) and its socket closes in the background.
            if (Bridge is { IsActive: true } leaving) _ = leaving.LeaveAsync();
            FeedLoading?.Invoke(this, EventArgs.Empty);   // Phase 1.4: WPF SwitchTo's HideHud / ClearBridgeStatus point
            return source.ReplayDir is not null ? LoadReplay(source) : LoadLive(source);
        }
        finally
        {
            if (Host.HasLiveSolver || Host.IsReplay) StartLoop();
        }
    }

    // WPF SwitchTo, the local-model half.
    private string? LoadLive(ModelSource s)
    {
        LiveLoadResult loaded = Host.LoadLive(s, _solver.Kind, _licence);
        if (!loaded.Loaded)
        {
            LoadError = loaded.Refusal;
            LoadRefused?.Invoke(this, loaded.Refusal!);   // Phase 1.4: WPF logs the refusal at "--:--"
            return loaded.Refusal;
        }
        string activeSolver = loaded.ActiveSolver!;

        _current = s;
        IsReplay = false;
        IsBridge = false;   // slice 1.8: a local case ends the bridge mode of the transport bar (WPF SetTransportMode)
        _paused = false;
        _stepPending = false;
        PlayPauseLabel = PauseLabel;   // live models animate (the host plays from the load)
        ActiveSolverText = _solver.Kind == SolverKind.Auto ? $"auto * {activeSolver}" : activeSolver;
        FrameReadout = "";
        CaseName = s.Label;
        LoadedText = $"Loaded {s.Label}  -  {loaded.Model!.BusCount} buses, {loaded.Model.Branches.Count} branches  -  {activeSolver}";
        LoadError = null;
        ClearFrame();   // until the new model's first frame lands nothing of the old one is shown
        _trippedUnits.Clear();   // a new run: no unit has been tripped in it yet (WPF)
        HasRun = true;
        FeedChanged?.Invoke(this, EventArgs.Empty);
        // WPF SwitchTo's order: the dashboard's model switch (_vm.ModelChanged), then the map load (_renderer.Load),
        // then the "Loaded" log line.
        var loadedArgs = new RunLoadedEventArgs { IsReplay = false, LoadedText = LoadedText };
        ModelSwitched?.Invoke(this, loadedArgs);
        LoadedModel = loaded.Model;
        LoadedOverlay = loaded.Overlay;   // slice 5-view: a v2 case's overlay, carried beside the model for the ASSETS tab
        ModelLoaded?.Invoke(this, loaded.Model);   // the WPF _renderer.Load(model), before the first step
        FeedLoaded?.Invoke(this, loadedArgs);
        Host.RequestStep();   // the first step now, not one interval later (as WPF)
        return null;
    }

    // WPF LoadReplay.
    private string? LoadReplay(ModelSource s)
    {
        ReplayLoadResult replay = Host.LoadReplay(s.ReplayDir!);
        if (!replay.Loaded)
        {
            LoadError = replay.Refusal;
            LoadRefused?.Invoke(this, replay.Refusal!);   // Phase 1.4: WPF logs the refusal at "--:--"
            return replay.Refusal;
        }
        int count = replay.Cursor!.Count;

        _current = s;
        IsReplay = true;
        IsBridge = false;   // slice 1.8: a run ends the bridge mode of the transport bar (WPF SetTransportMode)
        _paused = true;
        _stepPending = false;
        PlayPauseLabel = PlayLabel;   // the host opens a replay paused
        ActiveSolverText = "replay";
        CaseName = s.Label;
        LoadedText = $"Loaded replay {replay.Title}  -  {count} frames, {replay.LayoutModel!.BusCount} buses" +
                     (count == 1 ? "" : $"  (sampled - {replay.Cursor.CurrentLabel} ... {replay.LastLabel})");
        LoadError = null;
        ClearFrame();
        // Slice 1.7, the WPF scrubber reset: lowering the maximum below the old value (a SHORTER run after a longer one)
        // would make the slider pull its value down to the new maximum and, as a user move, seek this run to its LAST
        // frame (WPF guards the same trap). So the value goes to 0 first, then the maximum changes, both as code moves.
        MoveScrubber(() =>
        {
            ScrubberValue = 0;
            ScrubberMaximum = Math.Max(0, count - 1);
        });
        HasRun = true;
        FeedChanged?.Invoke(this, EventArgs.Empty);
        // WPF LoadReplay's order: the dashboard's model switch, the map load, the first frame, the log line.
        var loadedArgs = new RunLoadedEventArgs { IsReplay = true, LoadedText = LoadedText };
        ModelSwitched?.Invoke(this, loadedArgs);
        LoadedModel = replay.LayoutModel!;
        LoadedOverlay = null;   // slice 5-view: a run folder carries no asset overlay
        ModelLoaded?.Invoke(this, replay.LayoutModel!);   // the WPF _renderer.Load(replay.LayoutModel), before the first frame
        ShowReplayFrame(replay.First!);
        FeedLoaded?.Invoke(this, loadedArgs);
        return null;
    }

    private void ClearFrame()
    {
        LastFrame = null;   // WPF: _last = null, so a hover or trip cannot read the old model's frame
        Panel = null;
        FrameError = null;
        StateValid = true;
        Status = "";
    }

    // ======================================== the loop ========================================

    // Start the host's loop on the thread pool (the WPF window's StartHostLoop). Each frame is shown on the UI thread
    // and the loop waits for that before it makes the next.
    private void StartLoop()
    {
        var cts = new CancellationTokenSource();
        CancellationToken ct = cts.Token;
        _loopCts = cts;
        Task loop = Task.Run(() => Host.RunAsync(frame => Deliver(frame, ct), ct));
        LoopTask = loop;
        // Fail loud, as WPF does: a fault in the loop itself (not a solve fault, which arrives as a frame) is rethrown on
        // the UI thread, where the app's crash handling reports it. Never left in an unobserved task.
        loop.ContinueWith(t =>
        {
            Exception ex = t.Exception!.GetBaseException();
            Dispatcher.UIThread.Post(() => ExceptionDispatchInfo.Throw(ex));
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    /// <summary>
    /// Cancel the loop and wait, without blocking the UI thread, until it has ended. Safe to call more than once and
    /// with no loop running. Never throws: a loop that failed has already been reported by <see cref="StartLoop"/>.
    /// </summary>
    public async Task StopLoopAsync()
    {
        CancellationTokenSource? cts = _loopCts;
        Task? loop = LoopTask;
        _loopCts = null;
        if (cts is null || loop is null) return;
        await cts.CancelAsync().ConfigureAwait(true);
        // Wait for the end whatever its outcome; a fault is reported once, by the continuation in StartLoop.
        await loop.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
                  .ConfigureAwait(true);
        cts.Dispose();
    }

    // The loop's delivery: show the frame on the UI thread; the loop awaits the returned task.
    private static Task InvokeOnUi(Action show) => Dispatcher.UIThread.InvokeAsync(show).GetTask();

    private Task Deliver(HostFrame frame, CancellationToken ct) => InvokeOnUi(() => ShowHostFrame(frame, ct));

    /// <summary>
    /// Raised on the UI thread when a replay has played its last recorded frame and stopped, with the words to show
    /// ("Replay finished: frame 2 of 2 ... PLAY restarts it."). A pre-solved run holds only the frames it recorded, so a
    /// short one ends after a few steps; saying so keeps the stop from looking like a stalled loop.
    /// </summary>
    public event EventHandler<string>? ReplayFinished;

    /// <summary>The text <see cref="ReplayFinished"/> carries for a replay that stopped at frame <paramref name="index"/>
    /// (0-based) of <paramref name="count"/>, with the frame's own label.</summary>
    public static string ReplayFinishedText(int index, int count, string label) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Replay finished: frame {index + 1} of {count} ({label}) is the last recorded state. PLAY restarts it.");

    private void AnnounceReplayEnd()
    {
        if (Host.ReplayCursor is { } cur)
            ReplayFinished?.Invoke(this, ReplayFinishedText(cur.Index, cur.Count, cur.CurrentLabel));
    }

    // The WPF window's ShowHostFrame, display rules unchanged.
    private void ShowHostFrame(HostFrame frame, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;   // the loop was stopped: this frame belongs to nothing now
        if (frame.Epoch != Host.Epoch)
        {
            // Stale. One thing it says still holds: a replay that reached its last frame stopped playing.
            if (frame.StoppedAtEnd && !Host.Playing) { _paused = true; PlayPauseLabel = PlayLabel; AnnounceReplayEnd(); }
            return;
        }

        if (frame.Kind == HostFrameKind.Replay)
        {
            ShowReplayFrame(frame.Frame!);
            if (frame.StoppedAtEnd) { _paused = true; PlayPauseLabel = PlayLabel; AnnounceReplayEnd(); }   // the host stopped at the end
            return;
        }

        // A live Step once wanted exactly this frame: stop the loop making more before it asks for the next.
        if (_stepPending)
        {
            _stepPending = false;
            Host.Playing = false;
        }

        int tick = frame.Tick;
        if (frame.Kind == HostFrameKind.LiveFaulted)
        {
            // A solver exception must not silently freeze the panel on the last good frame (WPF texts).
            Exception ex = frame.Fault ?? new InvalidOperationException("unknown solve fault");
            // Phase 1.4: the dashboard's SOLVE FAULT path (lamp, event log, alarm), as WPF.
            FrameArriving?.Invoke(this, new RunFrameEventArgs { Kind = RunFrameKind.LiveFaulted, Fault = ex, Tick = tick });
            Status = SolveFaultStatus;
            FrameError = $"Solve failed at tick {tick}: {ex.Message}";
            return;
        }
        // An exception while showing the frame must not end the loop or freeze the panel: the same SOLVE FAULT path.
        try
        {
            // Phase 1.4: the dashboard's Update call (WPF ShowHostFrame), with the host's boundaries and solve time.
            var arriving = new RunFrameEventArgs
            {
                Kind = RunFrameKind.LiveSolved, Frame = frame.Frame, Boundaries = frame.Boundaries, SolveMs = frame.SolveMs, Tick = tick,
            };
            FrameArriving?.Invoke(this, arriving);
            // Slice 1.7: a frame the dashboard refused (its update threw) goes no further, as in WPF, where the dashboard's
            // update and the map's sit in one guard: not on the map, not the current frame. The SOLVE FAULT path, with
            // the WPF text the dashboard also logged.
            if (arriving.Rejection is { } refused)
            {
                Status = SolveFaultStatus;
                FrameError = $"Render failed at tick {tick}: {refused.Message}";
                return;
            }
            ShowFrame(frame.Frame!, frame.Boundaries, frame.SolveMs);   // Phase 1.6: the solve time feeds the CYCLE tab
        }
#pragma warning disable CA1031 // Not swallowed: it is shown in the error area, as WPF shows it on the lamp and the alarms.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Status = SolveFaultStatus;
            FrameError = $"Render failed at tick {tick}: {ex.Message}";
        }
    }

    // The WPF RenderReplayFrame, for what this panel shows, plus the cursor readout.
    private void ShowReplayFrame(SimFrame f)
    {
        try
        {
            // Phase 1.4: the dashboard's Update call (WPF RenderReplayFrame); it evaluates the replay boundaries itself.
            var arriving = new RunFrameEventArgs { Kind = RunFrameKind.Replay, Frame = f };
            FrameArriving?.Invoke(this, arriving);
            // Slice 1.7: a frame the dashboard refused goes no further (WPF RenderReplayFrame returns false from its one
            // guard): not on the map, not the current frame, and the scrubber and read-out stay where they were.
            if (arriving.Rejection is { } refused)
            {
                Status = SolveFaultStatus;
                FrameError = $"Render failed: {refused.Message}";
                return;
            }
            // The WPF RenderReplayFrame computes the boundary transfers from the replayed frame (none for a frame that
            // did not converge: the analysis refuses it), so the map's cut labels move in replay too.
            IReadOnlyList<BoundaryFlow> boundaries = f.Result.Converged
                ? BoundaryAnalysis.Evaluate(f.Model, f.Result)
                : Array.Empty<BoundaryFlow>();
            ShowFrame(f, boundaries);
        }
#pragma warning disable CA1031 // Not swallowed: shown in the error area, as WPF shows it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Status = SolveFaultStatus;
            FrameError = $"Render failed: {ex.Message}";
            return;
        }
        if (Host.ReplayCursor is ReplayCursor cursor)
        {
            // Slice 1.7: the scrubber follows the cursor (WPF RenderReplayFrame), moved as code so it seeks nothing.
            MoveScrubber(() => ScrubberValue = cursor.Index);
            FrameReadout = $"frame {cursor.Index + 1}/{cursor.Count}  *  {cursor.CurrentLabel}";
        }
    }

    // ======================================== replay transport (slice 1.7) ========================================

    private double _scrubberValue;
    private double _scrubberMaximum;
    private bool _scrubbing;   // WPF _scrubbing: true while the scrubber is moved by code, so it seeks nothing

    // Move the scrubber by code (the WPF "_scrubbing = true; ...; _scrubbing = false" guard).
    private void MoveScrubber(Action move)
    {
        _scrubbing = true;
        try { move(); }
        finally { _scrubbing = false; }
    }

    /// <summary>
    /// The scrubber's last frame index (WPF Scrubber.Maximum): the run's frame count less one, set when a run is
    /// loaded (0 before one).
    /// </summary>
    public double ScrubberMaximum
    {
        get => _scrubberMaximum;
        private set => SetProperty(ref _scrubberMaximum, value);
    }

    /// <summary>
    /// The scrubber's position (WPF Scrubber.Value): the cursor's frame index. Set by the slider when the user drags it,
    /// which seeks the replay there (WPF Scrubber_ValueChanged, see <see cref="SeekReplay"/>); set by this view model
    /// whenever a replay frame is shown, which seeks nothing. On a live case it moves nothing.
    /// </summary>
    public double ScrubberValue
    {
        get => _scrubberValue;
        set
        {
            if (!SetProperty(ref _scrubberValue, value)) return;
            if (_scrubbing || !Host.IsReplay) return;
            SeekReplay((int)Math.Round(value));
        }
    }

    /// <summary>
    /// The WPF RESTART button (Restart_Click): a replay goes back to its first frame, the timeline starts afresh
    /// (<see cref="TimelineReset"/>: THE MACHINE's integrators and the other per-timeline state restart, WPF
    /// ResetTimeline) and the frame is shown at once. Playing or paused stays as it was, as in WPF. Does nothing on a
    /// live case, as in WPF (the button is shown only for a replay).
    /// </summary>
    public Task RestartReplayAsync()
    {
        if (!Host.IsReplay) return Task.CompletedTask;
        ReplayPosition at = Host.ReplayRestart();
        TimelineReset?.Invoke(this, EventArgs.Empty);   // WPF: _vm.ResetTimeline(), before the frame is shown
        ShowReplayFrame(at.Frame);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The WPF STEP BACK button (StepBack_Click): the cursor one frame back (it stays on the first frame at the start),
    /// then the timeline restarts, because playing forward again would integrate the same frames a second time into
    /// THE MACHINE's accumulators (Hz.s banked, regen, BESS throughput), then the frame is shown at once. Does nothing on
    /// a live case, as in WPF.
    /// </summary>
    public Task StepBackAsync()
    {
        if (!Host.IsReplay) return Task.CompletedTask;
        ReplayPosition at = Host.ReplayRewind();
        TimelineReset?.Invoke(this, EventArgs.Empty);
        ShowReplayFrame(at.Frame);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The WPF scrubber (Scrubber_ValueChanged): jump the cursor to <paramref name="index"/> (the host clamps it to the
    /// run) and show that frame. Any BACKWARD move, and any move onto the first frame, restarts the timeline exactly as
    /// RESTART does (the accumulators integrate forward, so playing on from an earlier frame without a reset would
    /// double-count the frames between); a forward move does not. Does nothing on a live case, as in WPF.
    /// </summary>
    public void SeekReplay(int index)
    {
        if (!Host.IsReplay) return;
        ReplayPosition at = Host.ReplaySeek(index);
        if (at.Cursor.Index == 0 || at.Cursor.Index < at.Before) TimelineReset?.Invoke(this, EventArgs.Empty);
        ShowReplayFrame(at.Frame);
    }

    // Put one frame on the panel (and, through FrameArrived, on the map with its boundary transfers). A frame that did
    // not converge is shown as an error, never as a normal value.
    private void ShowFrame(SimFrame f, IReadOnlyList<BoundaryFlow> boundaries, double solveMs = 0)
    {
        LivePanelText text = LivePanelText.From(f);
        Panel = text;
        StateValid = text.Converged;
        if (text.Converged)
        {
            FrameError = null;
            Status = "";
        }
        else
        {
            FrameError = NotConvergedAlarm;
            Status = LivePanelText.NotConvergedText;
        }
        FrameArrived?.Invoke(this, new ArrivedFrame(f, boundaries, solveMs));
        FrameShown?.Invoke(this, text);
        LastFrame = f;   // only a frame the views accepted drives hover, the fly-out and trips (WPF)
    }

    // ======================================== trips (the WPF map's T key and right-click) ========================================

    // Units already tripped in the running local simulation (cleared when a case is loaded), keyed by index, bus and
    // name so a namesake at another index is a different unit (WPF _trippedUnits / TripKey).
    private readonly HashSet<string> _trippedUnits = new(StringComparer.Ordinal);

    private static string TripKey(int index, Generator g) => $"{index}:{g.Bus}:{g.Name}";

    /// <summary>
    /// The WPF window's <c>TripAtBus</c>: trip the largest unit generating now at <paramref name="busId"/> (the shared
    /// Core rule, <see cref="InfeedSelection.LargestOnline"/>), by injecting its output as a loss of
    /// infeed into the live frequency integrator. Returns the WPF event-log line (the trip, or why there was none), or
    /// null when there is no accepted frame yet (WPF does nothing then).
    /// <para>
    /// UI programme integration (2026-10-20): as in WPF, every line is also written to the dashboard's event log at the
    /// dashboard's clock (<see cref="TripDashboard"/>), and a trip that is taken also runs the GENERATION tab's
    /// analytical swing study on the dashboard (<see cref="TripSwingStudy.Apply"/>, after <c>Host.TripInfeed</c>, as
    /// the WPF <c>TripAtBus</c>): the study refreshes the GENERATION tab's read-outs and trace, the log line carries the
    /// baseline RoCoF and nadir, and the TRIP alarm is raised. The returned line is that same log line. A run view
    /// model made without a dashboard refuses the trip (it throws) rather than skip the study without a word.
    /// </para>
    /// </summary>
    public string? TripAtBus(int busId)
    {
        if (LastFrame is not SimFrame last) return null;
        DashboardModel dashboard = TripDashboard
            ?? throw new InvalidOperationException("This run view model has no dashboard, so a trip's swing study, log line and alarm have nowhere to go.");
        // Replay feeds have no local solver: say so instead of silently doing nothing (WPF text).
        if (!Host.HasLiveSolver) return LogTrip(dashboard, "TRIP is not available on this feed (replay or bridge stream)");
        GridModel m = last.Model;
        (Generator Unit, int Index)? pick = InfeedSelection.LargestOnline(m, busId);
        Bus? bus = m.Buses.FirstOrDefault(b => b.Id == busId);
        if (pick is null) return LogTrip(dashboard, $"No generation to trip at {bus?.Name ?? $"bus {busId}"}");

        // A trip injects the unit's output as a deficit; it does not take the unit out of the dispatch, so the same unit
        // stays the largest infeed on later frames. It is refused, with the reason, for the life of this run (WPF).
        (Generator gen, int genIndex) = pick.Value;
        if (!_trippedUnits.Add(TripKey(genIndex, gen)))
            return LogTrip(dashboard, $"TRIP refused: {gen.Name ?? $"the unit at bus {gen.Bus}"} has already been tripped in this run " +
                                      "(a trip is a frequency what-if: the unit stays in the dispatch, so it cannot be lost twice)");

        // The loss of infeed into the live frequency integrator, then (WPF order) the swing study on the dashboard,
        // which writes the log line with the baseline RoCoF and nadir and raises the TRIP alarm.
        Host.TripInfeed(gen.Pg);
        FrequencyResponse? baseline = TripSwingStudy.Apply(dashboard, m, gen, busId);
        return TripSwingStudy.LogLine(gen, baseline);
    }

    // WPF _vm.Log(_vm.Clock, line) for a trip that was not taken; the same line is returned for the status bar.
    private static string LogTrip(DashboardModel dashboard, string line)
    {
        dashboard.Log(dashboard.Clock, line);
        return line;
    }

    /// <summary>
    /// The WPF window's <c>TripLargestInfeed</c> (the T key): the largest online unit in the whole model, tripped at its
    /// bus through <see cref="TripAtBus"/> (which refuses a unit already tripped in this run). Null when there is no
    /// accepted frame yet.
    /// </summary>
    public string? TripLargestInfeed()
    {
        if (LastFrame is not SimFrame last) return null;
        Generator? g = InfeedSelection.LargestOnline(last.Model)?.Unit;
        if (g is null) return "No generation to trip";
        return TripAtBus(g.Bus);
    }

    // ======================================== transport (IRunActions) ========================================

    /// <inheritdoc />
    public Task TogglePlayAsync()
    {
        // WPF Pause_Click. Slice 1.8, D-DOC-141: on a bridge feed the stream is paced by the server, so there is no local
        // loop to stop or start: PAUSE holds the display (map, HUD, overlays, OPERATOR / MARKET) on the frame showing,
        // PLAY shows the newest frame that arrived meanwhile and follows the stream again.
        if (Bridge is { IsActive: true } bridge)
        {
            bridge.SetPaused(!bridge.IsPaused);
            return Task.CompletedTask;
        }
        _paused = !_paused;
        _stepPending = false;
        if (_paused)
        {
            Host.Playing = false;
            PlayPauseLabel = IsReplay ? PlayLabel : ResumeLabel;
        }
        else
        {
            if (Host.ReplayCursor is { AtEnd: true })
            {
                Host.ReplayRestart();   // play again from the start
                TimelineReset?.Invoke(this, EventArgs.Empty);   // Phase 1.4: WPF ResetTimeline here (fresh integrators)
            }
            Host.Playing = true;
            PlayPauseLabel = PauseLabel;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StepOnceAsync()
    {
        if (Host.IsReplay)
        {
            // WPF StepForward_Click: the cursor on by one, shown at once.
            ShowReplayFrame(Host.ReplayAdvance().Frame);
            return Task.CompletedTask;
        }
        if (IsBridge) throw new InvalidOperationException(BridgeStepText);   // slice 1.8: the server paces a bridge stream
        if (!Host.HasLiveSolver) throw new InvalidOperationException("No case is loaded, so there is nothing to step.");

        // WPF has no live step (only PAUSE / RESUME). Here a step pauses the case and lets the loop make exactly one
        // more frame: the loop waits for each frame to be shown before it makes the next, and the frame's display turns
        // playing off again before it returns (ShowHostFrame), so no second frame can be made.
        _paused = true;
        PlayPauseLabel = ResumeLabel;
        _stepPending = true;
        Host.Playing = true;
        Host.RequestStep();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task ChooseSolverAsync(SolverChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (!TransportChoices.Solvers.Contains(choice))
            throw new ArgumentException($"{choice.Label} is not one of the SOLVER choices.", nameof(choice));
        if (_solver == choice) return;
        _solver = choice;
        OnPropertyChanged(nameof(SelectedSolver));
        // WPF SolverPicker_SelectionChanged: rebuild the running sim on the new engine, but only for a local model (a
        // replay is pre-solved; the choice applies to the next case loaded).
        if (_current is { Bridge: null, ReplayDir: null } s) await LoadAsync(s).ConfigureAwait(true);
    }

    /// <inheritdoc />
    public Task ChooseStepAsync(StepChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (!TransportChoices.Steps.Contains(choice))
            throw new ArgumentException($"{choice.Label} is not one of the STEP choices.", nameof(choice));
        SelectedStep = choice;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task FinerStepAsync() => CycleStep(-1);

    /// <inheritdoc />
    public Task CoarserStepAsync() => CycleStep(+1);

    // WPF CycleStep: move along the STEP choices, stopping at either end.
    private Task CycleStep(int delta)
    {
        int i = IndexOf(TransportChoices.Steps, _step);
        SelectedStep = TransportChoices.Steps[Math.Clamp(i + delta, 0, TransportChoices.Steps.Count - 1)];
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ChooseSpeedAsync(double rate)
    {
        if (!TransportChoices.Playbacks.Contains(rate))
            throw new ArgumentOutOfRangeException(nameof(rate), rate, "Not one of the SPEED choices.");
        _playback = rate;
        SpeedLabel = RunCommands.SpeedLabel(rate);
        Host.PlaybackRate = rate;   // the loop now waits SimHost.BasePlaybackMs / rate
        OnPropertyChanged(nameof(Playback));
        return Task.CompletedTask;
    }

    /// <summary>The WPF SPEED button: the next playback rate, wrapping round after the last.</summary>
    public Task CycleSpeedAsync()
    {
        IReadOnlyList<double> rates = TransportChoices.Playbacks;
        int i = IndexOf(rates, _playback);
        return ChooseSpeedAsync(rates[(i + 1) % rates.Count]);
    }

    private static int IndexOf<T>(IReadOnlyList<T> list, T item)
    {
        for (int i = 0; i < list.Count; i++)
            if (EqualityComparer<T>.Default.Equals(list[i], item)) return i;
        return -1;
    }
}

/// <summary>A frame being put on screen, with the boundary transfers the WPF window pushes into its map with it.</summary>
/// <param name="Frame">The frame.</param>
/// <param name="Boundaries">The ETYS boundary transfers for the frame (empty when it did not converge).</param>
/// <param name="SolveMs">Phase 1.6: the wall time of the live solve that made the frame (the host's measure, which WPF
/// hands its dashboard and the CYCLE tab); 0 for a replay frame, which was not solved here (WPF passes none).</param>
public sealed record ArrivedFrame(SimFrame Frame, IReadOnlyList<BoundaryFlow> Boundaries, double SolveMs = 0);

/// <summary>
/// UI programme slice 1.8: the GDA bridge's colour overlays for the map (WPF <c>_renderer.SetOverlay</c>): the asset
/// condition per bus id and the branch stress per branch name, either of which a tick may omit.
/// </summary>
/// <param name="ConditionByBus">Condition index (0 healthy .. 1 critical) per bus id, or null.</param>
/// <param name="StressByBranch">Stress (0 cool .. 1 hot) per branch name, or null.</param>
public sealed record MapOverlay(IReadOnlyDictionary<int, double>? ConditionByBus, IReadOnlyDictionary<string, double>? StressByBranch);
