using System.Globalization;
using System.Text.Json;
using GridSim.Core.Abstractions;
using GridSim.Core.Harmonics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Net;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Web.Projection;

namespace GridSim.Web.Sessions;

/// <summary>How a session is fed.</summary>
public enum FeedSource
{
    /// <summary>Drive a <see cref="LiveSim"/> in-process (steps a day of the GB model, or holds an IEEE case steady).</summary>
    Live,
    /// <summary>Scrub a pre-solved run directory via <see cref="PresolvedReplay"/>.</summary>
    Replay,
    /// <summary>Stream from a live GDA bridge server via <see cref="GridBridgeClient"/> (real lake data).</summary>
    Bridge,
}

/// <summary>
/// One connection's simulation: owns exactly one feed (a <see cref="LiveSim"/> or a
/// <see cref="PresolvedReplay"/>), a tick/cursor, and play/pause/speed. It drives itself on a background
/// loop and raises <see cref="FrameProduced"/> for each new frame; both the Blazor circuit and the raw
/// WebSocket endpoint subscribe to that. Not shared across connections - the live feed's solver is not
/// thread-safe, so each session steps under its own gate.
/// </summary>
public sealed class SimSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private readonly LiveSim? _live;
    private readonly PresolvedReplay? _replay;
    // CAF B5 supervised reconnect: no longer readonly, because a bridge session that reconnects replaces its client (a
    // GridBridgeClient wraps one socket and cannot be reused). It is never set back to null, so every "is this a bridge
    // session" test below (_bridge is not null) is unchanged. Volatile: the reconnect swaps it on the supervisor's thread
    // while the UI threads read it. The swap itself is in ReconnectBridgeAsync, under _gate.
    private volatile GridBridgeClient? _bridge;
    // What a reconnect needs to build the next client exactly as the first was built: the endpoint and the host's
    // checked resilience settings (message cap, in-flight cap, transport rule, backoff schedule).
    private readonly Uri? _bridgeEndpoint;
    private readonly ResilienceOptions _bridgeResilience = ResilienceOptions.Default;
    // The supervisor that drives the backoff schedule after a lost stream; null unless the host turned it on
    // (GridSim:BridgeReconnect). Disposed in DisposeAsync before the client.
    private readonly BridgeReconnectSupervisor? _reconnect;
    // Set (under _gate) once DisposeAsync has begun, so a reconnect still in flight never installs a new client that
    // nothing would dispose.
    private bool _bridgeSwapClosed;
    private readonly string _bridgeCase = "";
    private readonly string _bridgeSource = "presolved";
    // Raw source (source="raw"): the per-tick raw blocks RawDataReceived carries, captured on the receive-loop
    // thread immediately before OnBridgeState in the same tick so BuildEnvelope can read them. Plus the selectable
    // rocof_1s range from the bridge /meta (for the transport scrubber + date-picker) and the shared meta client.
    private BridgeProtocol.GenerationMixBlock? _rawMix;
    private BridgeProtocol.WeatherBlock? _rawWeather;
    private BridgeProtocol.RawInterconnectorsBlock? _rawIcs;   // real per-tick interconnector flows (raw view)
    private BridgeProtocol.ScalarsBlock? _rawScalars;   // real freq/rocof/inertia for the raw headline (via StateReceived)
    private DateTime? _rawMin;
    private DateTime? _rawMax;
    private DateTime? _rawAt;                            // the latest raw tick's wall-clock instant (scrubber position)
    // GR-730: the /meta answer is a few hundred bytes; a 1 MiB cap stops a misbehaving endpoint from filling memory.
    private static readonly HttpClient _meta = new() { Timeout = PresentationLimits.MetaFetchTimeout, MaxResponseContentBufferSize = 1 << 20 };
    private int _tick;
    private double _stepMinutes;
    private readonly MechanicalService _mechanical;
    private readonly ScadaService _scada = new();
    private readonly MachineService _machine = new();

    // Resident prioritised parallel analysis cycle (live sessions only; null when disabled). Runs the heavy
    // downstream analyses off this session's gate; BuildEnvelope only snapshots its latest telemetry.
    private readonly SessionCycleService? _cycle;
    private double _lastBaseSolveMs;   // wall time of the most recent live base solve, for the cycle telemetry

    // Rolling sparkline history (oldest -> newest), capped.
    private const int HistoryLen = 120;
    private readonly Queue<double> _freqHist = new();
    private readonly Queue<double> _demandHist = new();
    private readonly Queue<double> _inertiaHist = new();
    private readonly Queue<double> _genHist = new();
    // MARKET imbalance-price sparkline: N037, one sample per GB settlement period (the price is half-hourly; the
    // bridge repeats it on every tick), capped at TabProjections.ImbalancePriceHistoryLength. Held here, not in the
    // tab, because the dashboard unmounts a tab when the user switches away and the history must survive that.
    private readonly Queue<double> _imbalancePriceHist = new();
    private long? _imbalancePricePeriod;   // settlement-period key of the newest sample (null before the first)
    private DateTime? _bridgeTickUtc;      // the current bridge tick's 't' instant (every source; captured in OnBridgeRaw)

    // GR-224: the worker that projects bridge frames off the receive loop (a bounded FIFO, created on the first frame and
    // stopped in DisposeAsync). See OnBridgeState.
    private BridgeProjectionWorker<BridgeFrameWork>? _projector;

    /// <summary>GR-224: how many bridge frames may wait for projection behind the one being projected. A session whose
    /// projection falls further behind than this is stopped with a clear error (nothing is dropped).</summary>
    internal const int BridgeProjectionQueueCapacity = 64;

    /// <summary>GR-224: what a bridge frame's projection reads that the receive loop overwrites with the NEXT tick (the
    /// per-tick raw blocks, overlays, HUD scalars and tick instant), captured under the gate when the frame arrives so the
    /// projection, which now runs later, sees its own frame's values and not a newer frame's.</summary>
    private sealed record BridgeSnapshot(
        BridgeProtocol.ScalarsBlock? RawScalars,
        BridgeProtocol.GenerationMixBlock? RawMix,
        BridgeProtocol.RawInterconnectorsBlock? RawIcs,
        MapOverlaysDto? Overlays,
        MapHudDto? Hud,
        DateTime? TickUtc);

    /// <summary>GR-224: one bridge frame queued for projection, with its own <see cref="BridgeSnapshot"/>.</summary>
    private sealed record BridgeFrameWork(
        SimFrame Frame,
        BridgeProtocol.OperatorBlock? Op,
        BridgeProtocol.MarketBlock? Mk,
        BridgeSnapshot Snapshot);
    // NESO RP1 purchased-time trace for the falling-trend escalation of the LFDD grade (Q1, 2026-10): the same Core
    // trace the WPF NesoRp1Vm keeps, one point per built envelope, cleared with the other histories on a seek.
    private readonly GridSim.Core.Presentation.FrequencyRiskPresenter.PurchasedTimeTrace _purchasedTrace = new();

    // ---- harmonics on-demand study ------------------------------------------
    // The HARMONICS tab drives the frequency-domain study from a button (it is far too heavy per frame).
    private const int HarmonicsBusLimit = PresentationLimits.HeavyModelBusLimit;   // the same heavy-model gate as FrameProjector (R204)
    private SimFrame? _lastFrame;                    // latest solved frame (Model + Result) for on-demand studies
    private HarmonicsDto? _harmonicsCache;           // cached study, keyed by the model instance and source choice below
    private GridModel? _harmonicsCacheModel;
    private HarmonicSourceChoice _harmonicsCacheChoice;   // N151: a different source choice is a different study

    public string SessionId { get; }
    public FeedSource Source { get; }
    public TopologyDto Topology { get; private set; }
    public FrameEnvelope? Current { get; private set; }
    public bool Playing { get; private set; }
    public double Fps { get; private set; } = 1.0;
    /// <summary>Non-null when the feed has faulted (e.g. bridge connect failed) - surfaced in the UI.</summary>
    public string? Error { get; private set; }

    /// <summary>GR-236: a non-fatal notice (a bridge message that was rejected and skipped while the stream carried on).
    /// Shown beside the dashboard; unlike <see cref="Error"/> it does not replace it.</summary>
    public string? Warning { get; private set; }

    /// <summary>The estimation quality of the latest bridge frame that carried an <c>estimate</c> block (a
    /// <c>source=estimated</c> stream): the chi-square verdict and fit statistics, so a measurement-fitted state is
    /// never shown without its honesty layer. Null until such a frame arrives (and always on other feeds). Until
    /// October 2026 the web console had no handler for the block, so it showed no quality at all.</summary>
    public BridgeProtocol.EstimateBlock? LatestEstimate { get; private set; }

    public bool IsReplay => Source == FeedSource.Replay;
    public bool IsBridge => Source == FeedSource.Bridge;
    /// <summary>True for the pure RAW public-data replay (bridge source="raw"): real 1s frequency, no solve.</summary>
    public bool IsRaw => IsBridge && _bridgeSource == "raw";
    /// <summary>Selectable raw-replay range (rocof_1s bounds from the bridge /meta), null until fetched.</summary>
    public DateTime? RawMin => _rawMin;
    public DateTime? RawMax => _rawMax;
    /// <summary>
    /// GR-241: the newest instant a raw seek may land on: the raw range's end, or the forward-inference horizon
    /// (<see cref="GridSim.Core.Diagnostics.ForwardInferenceGuard.Horizon"/> of the real clock), whichever is EARLIER. Null when no range is known.
    /// </summary>
    public DateTime? RawSeekMax => RawSeekLimit(_rawMax, DateTime.UtcNow);

    /// <summary>The earlier of the range end and the horizon at <paramref name="utcNow"/>; null when the range end is unknown.</summary>
    internal static DateTime? RawSeekLimit(DateTime? rawMax, DateTime utcNow)
    {
        if (rawMax is not DateTime hi) return null;
        DateTime horizon = GridSim.Core.Diagnostics.ForwardInferenceGuard.Horizon(utcNow);
        DateTime hiUtc = GridSim.Core.Diagnostics.UtcTime.Normalise(hi);
        return hiUtc < horizon ? hiUtc : horizon;
    }

    /// <summary>
    /// GR-241: clamp a requested raw-seek instant into the raw range AND the forward-inference horizon. An instant before
    /// <paramref name="rawMin"/> becomes rawMin; one after the limit (<see cref="RawSeekLimit"/>) becomes the limit. With no
    /// known range only the horizon applies. The clock is a parameter so a test needs no wall-clock wait.
    /// </summary>
    internal static DateTime ClampSeekInstant(DateTime at, DateTime? rawMin, DateTime? rawMax, DateTime utcNow)
    {
        DateTime t = GridSim.Core.Diagnostics.UtcTime.Normalise(at);
        DateTime ceiling = RawSeekLimit(rawMax, utcNow) ?? GridSim.Core.Diagnostics.ForwardInferenceGuard.Horizon(utcNow);
        if (t > ceiling) t = ceiling;
        if (rawMin is DateTime lo)
        {
            DateTime loUtc = GridSim.Core.Diagnostics.UtcTime.Normalise(lo);
            if (t < loUtc) t = loUtc;
        }
        return t;
    }

    /// <summary>The seek instant clamped against this session's own range and the real clock (GR-241).</summary>
    public DateTime ClampSeek(DateTime at) => ClampSeekInstant(at, _rawMin, _rawMax, DateTime.UtcNow);
    /// <summary>The latest raw tick's UTC instant - the transport scrubber/date-picker position. Null until the
    /// first raw frame arrives.</summary>
    public DateTime? RawAt { get { lock (_gate) return _rawAt; } }   // GR-873: written on the receive loop

    /// <summary>The live sim's integration timestep (minutes) each Step advances. Fixed for replay/bridge.</summary>
    public double StepMinutes => _stepMinutes;

    /// <summary>The resolved solver actually running (Auto -> concrete) for a live session; the feed source for
    /// replay/bridge. Shown in the header.
    /// <para>N063: a live session's engine is chosen at creation (<see cref="RequestedSolver"/>, passed through
    /// <c>SessionManager.Create</c>); the dashboard's SOLVER picker recreates the session to change it.</para></summary>
    public string ActiveSolver =>
        _live is not null ? _live.ActiveSolver
        : IsReplay ? "presolved replay"
        : IsBridge ? $"bridge | {_bridgeSource}"
        : "-";
    /// <summary>True once a topology is known (always true for live/replay; awaits the server for bridge).</summary>
    public bool TopologyReady => Topology.BusCount > 0;

    /// <summary>Raised when topology arrives, an error occurs, or play state changes (for the UI to re-render).</summary>
    public event Action? StateChanged;

    /// <summary>Replay cursor position (0-based); 0 for a live session.</summary>
    public int ReplayIndex => _replay?.Index ?? 0;

    /// <summary>Replay frame count; 0 for a live session.</summary>
    public int ReplayCount => _replay?.Count ?? 0;

    /// <summary>Raised for each new frame (initial frame, each auto-advance, and each manual step/seek).</summary>
    public event Action<FrameEnvelope>? FrameProduced;

    private SimSession(string sessionId, FeedSource source, TopologyDto topology,
        LiveSim? live, PresolvedReplay? replay, double stepMinutes,
        GridBridgeClient? bridge = null, string bridgeCase = "", Uri? bridgeEndpoint = null,
        string bridgeSource = "presolved", SessionCycleService? cycle = null,
        ResilienceOptions? bridgeResilience = null, bool bridgeReconnect = false,
        Func<TimeSpan, CancellationToken, Task>? reconnectDelay = null)
    {
        SessionId = sessionId;
        Source = source;
        Topology = topology;
        // N273 (October 2026, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04): the nominal the displays grade
        // against is the case's own: a live session's LiveSim runs at the model's nominal; a replay plays its model's; a
        // bridge feed is the GB lake (50 Hz). Every GB session gets FrequencyBands.Gb, so its displays are unchanged.
        NominalHz = live?.NominalHz ?? replay?.LayoutModel.FrequencyHz ?? GridSim.Core.Dynamics.FrequencyLimits.NominalHz;
        Bands = GridSim.Core.Dynamics.FrequencyBands.ForNominal(NominalHz);
        _live = live;
        _replay = replay;
        _bridge = bridge;
        _bridgeCase = bridgeCase;
        _bridgeSource = bridgeSource;
        // Raw mixes cadences (1-second frequency vs 30-minute settlement data), so start faster than 1x - the
        // half-hourly channels then step often instead of feeling frozen (the user can still slow to 1x/0.5x to
        // study the per-second frequency). Only raw; other feeds keep the 1x default.
        if (_bridgeSource == "raw") Fps = PresentationLimits.RawDefaultFps;
        _stepMinutes = stepMinutes;
        _cycle = cycle;
        if (_cycle is not null) _cycle.Updated += RaiseState;   // a completed cycle refreshes the CYCLE tab
        // F-047: a bridge session has no step of its own (it passes 0; the server paces the ticks), so its MECHANICAL
        // ramp divides by the real gap between tick instants. It used to fall back to a 1.0 minute divisor, which
        // overstated every ramp on a 30-minute lake 30 times. Live and replay keep their fixed step, which must be valid.
        _mechanical = bridge is not null ? MechanicalService.ForTimestampedFeed() : new MechanicalService(stepMinutes);

        if (_bridge is not null)
        {
            _bridgeEndpoint = bridgeEndpoint;
            _bridgeResilience = bridgeResilience ?? ResilienceOptions.Default;
            // CAF B5: the supervised reconnect, when the host turned it on. It is built BEFORE the first connect starts,
            // so a stream lost straight after connecting is already supervised. Its schedule is the host's
            // ResilienceOptions (BackoffDelay, MaxReconnectAttempts); every attempt rebuilds the client through
            // ReconnectBridgeAsync, so the forward-inference guard and every limit apply to it exactly as to the first.
            if (bridgeReconnect)
            {
                _reconnect = new BridgeReconnectSupervisor(_bridgeResilience, ReconnectBridgeAsync, reconnectDelay);
                _reconnect.ReconnectScheduled += OnReconnectScheduled;
                _reconnect.AttemptCompleted += OnReconnectAttemptCompleted;
                _reconnect.GaveUp += OnReconnectGaveUp;
            }
            WireBridge(_bridge);
            _ = ConnectBridgeAsync(bridgeEndpoint!, _cts.Token);
            _loop = Task.CompletedTask;   // bridge is server-driven; no local stepping loop
        }
        else
        {
            // Emit an initial frame so a client paints immediately without waiting for play.
            // GR-223: if the first frame throws, the session is never handed to anyone who would dispose it, so the
            // analysis cycle it already started (a full N-1 on the thread pool) and the token source are released here
            // before the failure propagates; repeated attempts used to pile up background work.
            try { Current = ProduceInitial(); }
            catch
            {
                if (_cycle is not null) { _cycle.Updated -= RaiseState; _ = _cycle.DisposeAsync().AsTask(); }
                _cts.Cancel();
                _cts.Dispose();
                throw;
            }
            _loop = Task.Run(() => RunLoopAsync(_cts.Token));
        }
    }

    // ---- factories ----------------------------------------------------------

    public static SimSession CreateLive(string sessionId, string caseId, string caseLabel, GridModel model,
        bool holdSteady, double stepMinutes = 20, GridSim.Core.Scheduling.CycleOptions? cycleOptions = null,
        SolverKind solver = SolverKind.Auto)
    {
        // When the case ships an authored asset-dynamics or protections bundle, run the live frequency through the
        // asset-aware integrator so the INVERTER FLEET tab is populated (clearly labelled UnvalidatedAtScale) and the
        // protections act. D-DOC-079: the shared Core rule; this tested AssetDynamics only, so a protections-only case
        // ran with its protections idle.
        var dynCfg = LiveSim.AuthoredBundleConfig(model);
        // N063: the engine is the caller's choice; Auto (the default) is exactly the pre-N063 construction.
        var live = new LiveSim(model, holdSteady, solver, dynCfg);
        TopologyDto topo = FrameProjector.BuildTopology(caseId, caseLabel, model, replay: false, frameCount: 0);
        // The prioritised parallel analysis cycle runs only for live in-process sessions and only when
        // configured (Cycle:Enabled). Bridge/replay feeds don't drive it.
        SessionCycleService? cycle = cycleOptions is not null ? new SessionCycleService(model, cycleOptions) : null;
        return new SimSession(sessionId, FeedSource.Live, topo, live, replay: null, stepMinutes, cycle: cycle)
        {
            RequestedSolver = solver,
        };
    }

    /// <summary>N273 (October 2026, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04): the nominal frequency (Hz) of this session's case.</summary>
    public double NominalHz { get; }

    /// <summary>N273 (October 2026, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04): the GB frequency bands about <see cref="NominalHz"/> the header lamp and sparkline use.</summary>
    public GridSim.Core.Dynamics.FrequencyBands Bands { get; }

    /// <summary>N063: the engine requested when this session was created (<see cref="SolverKind.Auto"/> for replay and
    /// bridge sessions, which run no solver of their own). <see cref="ActiveSolver"/> names what Auto resolved to.</summary>
    public SolverKind RequestedSolver { get; private init; } = SolverKind.Auto;

    public static SimSession CreateReplay(string sessionId, string caseId, string caseLabel, PresolvedReplay replay)
    {
        // Replay's LayoutModel already carries map coordinates; still run through BuildTopology for the
        // fleet/edge projection (EnsureLayout is idempotent when coords already exist).
        TopologyDto topo = FrameProjector.BuildTopology(caseId, caseLabel, replay.LayoutModel, replay: true, replay.Count);
        // Replay frames step one SETTLEMENT PERIOD (30 min) apart - the
        // stepMinutes:0 fallback gave MechanicalService a 1-minute dt and
        // overstated every ramp MW/min ~30x vs the WPF twin.
        return new SimSession(sessionId, FeedSource.Replay, topo, live: null, replay, stepMinutes: 30);
    }

    /// <summary>
    /// Stream from a live GDA bridge server. Topology is not known until the server sends it, so this starts
    /// with an empty topology and fills it on the first <c>topology</c> message (the UI shows "connecting" until
    /// then). <paramref name="bridgeCase"/> is the server-side case to request.
    /// </summary>
    /// <param name="licence">Bug #172: the licence verdict for the topology the server sends (SessionManager passes the
    /// catalogue's gate). REQUIRED: a bridge session that is never licence-checked cannot be built. A denied topology is
    /// never shown: the session reports the licence message as its error and stops the feed.</param>
    /// <param name="reconnect">CAF B5: true to supervise the stream: when it is lost, reconnect on the backoff schedule of
    /// <paramref name="resilience"/> (<see cref="ResilienceOptions.BackoffDelay"/>, up to
    /// <see cref="ResilienceOptions.MaxReconnectAttempts"/>, 0 meaning no bound), and report a final give-up as the
    /// session's <see cref="Error"/>. False (the default) keeps the unsupervised behaviour: a lost stream stays lost.</param>
    public static SimSession CreateBridge(string sessionId, Uri endpoint, string bridgeCase,
        string bridgeSource, ResilienceOptions? resilience, Func<GridModel, GridSim.Core.Licensing.GateResult> licence,
        bool reconnect = false) =>
        CreateBridgeCore(sessionId, endpoint, bridgeCase, bridgeSource, resilience, licence, reconnect, reconnectDelay: null);

    /// <summary>Test seam for <see cref="CreateBridge"/>: the same session, with the supervisor's backoff wait replaced
    /// by <paramref name="reconnectDelay"/> (null: the real <see cref="Task.Delay(TimeSpan, CancellationToken)"/>), so a
    /// test can drive a reconnect without sleeping. The schedule it is asked to wait is still exactly
    /// <see cref="ResilienceOptions.BackoffDelay"/>.</summary>
    internal static SimSession CreateBridgeCore(string sessionId, Uri endpoint, string bridgeCase,
        string bridgeSource, ResilienceOptions? resilience, Func<GridModel, GridSim.Core.Licensing.GateResult> licence,
        bool reconnect, Func<TimeSpan, CancellationToken, Task>? reconnectDelay)
    {
        ArgumentNullException.ThrowIfNull(licence);
        var client = new GridBridgeClient();
        // The host's checked settings (message cap, in-flight cap, and whether ws:// is allowed); the defaults, which
        // require wss://, when none are given.
        (resilience ?? ResilienceOptions.Default).ApplyTo(client);
        var session = new SimSession(sessionId, FeedSource.Bridge, new TopologyDto(), live: null, replay: null,
            stepMinutes: 0, bridge: client, bridgeCase: bridgeCase, bridgeEndpoint: endpoint,
            bridgeSource: bridgeSource, bridgeResilience: resilience, bridgeReconnect: reconnect,
            reconnectDelay: reconnectDelay);
        session._bridgeLicence = licence;
        return session;
    }

    /// <summary>CAF B5: this bridge session's reconnect supervisor, or null when supervision is off (or this is not a
    /// bridge session). Internal, for the tests.</summary>
    internal BridgeReconnectSupervisor? ReconnectSupervisor => _reconnect;

    // Bug #172: the licence check a bridge session applies to the topology its server sends (null only for non-bridge
    // sessions), and whether it refused one (no frame of a refused feed is ever shown).
    private Func<GridModel, GridSim.Core.Licensing.GateResult>? _bridgeLicence;
    private volatile bool _bridgeLicenceRefused;

    // ---- controls -----------------------------------------------------------

    public void Play()
    {
        if (_bridge is not null) { lock (_gate) Playing = true; _ = BridgeStartAsync(resume: true); RaiseState(); return; }
        lock (_gate) Playing = true;
    }

    public void Pause()
    {
        if (_bridge is not null) { lock (_gate) Playing = false; _ = BridgeControl("pause", () => _bridge.StopAsync(_cts.Token)); RaiseState(); return; }
        lock (_gate) Playing = false;
    }

    public void SetSpeed(double fps)
    {
        lock (_gate) Fps = Math.Clamp(fps, PresentationLimits.MinFps, PresentationLimits.MaxFps);
        if (_bridge is not null) _ = BridgeControl("speed change", () => _bridge.SetSpeedAsync(Fps, _cts.Token));
    }

    /// <summary>
    /// Live only: set the integration timestep (minutes) each <see cref="LiveSim.Step"/> integrates, so a fine
    /// dt lets you watch RoCoF/nadir in slow motion. Guarded to a finite positive value; a no-op for replay
    /// (fixed 30-min settlement periods) and bridge (server-driven). The per-session
    /// <see cref="MechanicalService"/> follows the new step too, so its MW/min ramps divide by the interval the
    /// next frame actually spans (R067: it used to keep the construction-time step, so after 20 min -> 1 s every
    /// ramp was understated 1,200 times). The ramp history is kept: the next frame is integrated over the new
    /// step, so (Pg next - Pg now) / new step is the true rate.
    /// </summary>
    public void SetStepMinutes(double minutes)
    {
        if (_live is null) return;                                            // live-only
        if (double.IsNaN(minutes) || double.IsInfinity(minutes) || minutes <= 0) return;
        lock (_gate)
        {
            _stepMinutes = minutes;
            _mechanical.SetStepMinutes(minutes);
        }
    }

    /// <summary>The step (minutes) the MECHANICAL ramp readout currently divides by. Equals
    /// <see cref="StepMinutes"/> for a live session.</summary>
    public double MechanicalStepMinutes { get { lock (_gate) return _mechanical.StepMinutes; } }

    /// <summary>Advance exactly one frame, whether playing or paused. No-op in bridge mode (server-driven).</summary>
    public void StepOnce()
    {
        if (_bridge is not null) return;
        FrameEnvelope frame;
        lock (_gate) frame = ProduceNextLocked();
        Emit(frame);
    }

    /// <summary>Replay only: jump to a fraction (0..1) of the run and emit that frame.</summary>
    public void SeekFraction(double fraction)
    {
        if (_replay is null) return;
        FrameEnvelope frame;
        lock (_gate)
        {
            _replay.SeekFraction(fraction);
            // timeline discontinuity: drop all per-session integrators/history
            _mechanical.Reset();
            _machine.Reset();
            _scada.Reset();
            _freqHist.Clear(); _demandHist.Clear(); _inertiaHist.Clear(); _genHist.Clear();
            _purchasedTrace.Clear();
            frame = BuildEnvelope(_replay.Current, _replay.CurrentLabel);
            Current = frame;
        }
        Emit(frame);
    }

    /// <summary>
    /// Raw/bridge: jump the stream to a wall-clock instant and keep playing from there (the server re-starts the
    /// raw stream at <paramref name="at"/>). The transport scrubber + date-picker clamp <paramref name="at"/> into
    /// <see cref="RawMin"/>..<see cref="RawMax"/>. No-op for a local (in-process) feed - use <see cref="SeekFraction"/>
    /// for a local replay.
    /// </summary>
    public void SeekTo(DateTime at)
    {
        if (_bridge is null) return;
        lock (_gate) Playing = true;
        // UtcTime.Normalise: an Unspecified picker value is UTC by contract, not machine-local time (R277).
        _ = BridgeControl("seek", () => _bridge.SeekAsync(ClampSeek(at), _bridgeSource, _cts.Token));
        RaiseState();
    }

    /// <summary>The raw-replay landing instant: 1 January of the current year, 00:00 UTC, clamped into the /meta
    /// range the lake actually holds. (R099: this summary used to say "about 2 h before the newest data", which the
    /// code does not do.)</summary>
    private DateTime RawDefaultStart()
    {
        // Always begin playback at 1 Jan of the CURRENT year, 00:00 UTC, for consistency across
        // sessions (and so the pre-warmed first month is the hot path). Clamp into the available raw
        // range [_rawMin.._rawMax] if that instant falls outside what the lake actually holds.
        DateTime want = new DateTime(DateTime.UtcNow.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        if (_rawMin is DateTime min && want < min) want = min;
        if (_rawMax is DateTime max && want > max) want = max;
        return want;
    }

    /// <summary>Fetch the raw replay's selectable range (rocof_1s min/max) from the bridge <c>/meta</c> HTTP endpoint
    /// (derived from the WS endpoint). Best-effort: on any failure the range stays null and <see cref="RawDefaultStart"/>
    /// falls back. The server caches the scan, so this is fast after the first call.</summary>
    private async Task FetchRawRangeAsync(Uri wsEndpoint, CancellationToken ct)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(await _meta.GetStringAsync(MetaUrlFrom(wsEndpoint), ct));
            JsonElement root = doc.RootElement;
            _rawMin = ParseMetaTs(root, "frequency_min_timestamp");
            _rawMax = ParseMetaTs(root, "frequency_max_timestamp");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* session tearing down */ }
        catch (Exception ex)
        {
            // Best-effort: the range stays null and the default start falls back. GR-730: the failure is said, not
            // swallowed, so an operator can see why the date picker has no bounds.
            Console.Error.WriteLine($"GridSim.Web: warning: raw replay range unavailable from the bridge ({ex.GetType().Name}: {ex.Message}); the date picker has no bounds.");
        }
    }

    private static DateTime? ParseMetaTs(JsonElement root, string key) =>
        root.TryGetProperty(key, out JsonElement el) && el.ValueKind == JsonValueKind.String &&
        DateTime.TryParse(el.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime dt)
            ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : null;

    /// <summary>ws(s)://host:port/ws/bridge -> http(s)://host:port/meta.</summary>
    private static Uri MetaUrlFrom(Uri ws) => new UriBuilder(ws)
    {
        Scheme = ws.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase) ? "https" : "http",
        Path = "/meta",
        Query = "",
    }.Uri;

    /// <summary>Live only: trip an infeed (loss of generation), MW. A no-op for replay/bridge feeds, so callers
    /// check <see cref="CanTrip"/> first and tell the user when a trip is not available.</summary>
    public void Trip(double lostMw)
    {
        lock (_gate) _live?.TripInfeed(lostMw);
    }

    /// <summary>
    /// True only for a live in-process feed, the one feed a trip can actually reach. Replay frames are pre-solved
    /// and a bridge stream is driven by the server, so on those feeds every trip control must be hidden or must
    /// say it is not available (never silently do nothing).
    /// </summary>
    public bool CanTrip => _live is not null;

    /// <summary>The result of a map trip request, worded for the operator.</summary>
    /// <param name="Tripped">True when a loss of infeed was injected into the live frequency integrator.</param>
    /// <param name="Message">What happened, in plain English (shown on the map either way).</param>
    /// <param name="UnitName">The unit that was tripped, when one was.</param>
    /// <param name="LostMw">The live output (MW) that was removed, when a unit was tripped; 0 otherwise.</param>
    public sealed record TripOutcome(bool Tripped, string Message, string? UnitName = null, double LostMw = 0.0);

    /// <summary>
    /// Map trip (right-click on a bus, or the T key): remove the largest unit that is generating right now, at
    /// <paramref name="busId"/> or, when it is null, on the whole system. The unit is chosen by the shared Core
    /// rule (<see cref="InfeedSelection.LargestOnline"/>) against the latest solved frame, so it is sized from LIVE
    /// output, never from nameplate, and never picks a power-flow-only device (slack balancer, voltage control).
    /// When nothing qualifies, or the feed cannot be tripped, nothing is injected and the outcome says why.
    /// </summary>
    public TripOutcome TripLargestInfeed(int? busId)
    {
        lock (_gate)
        {
            if (_live is null)
                return new TripOutcome(false,
                    "TRIP is not available on this feed: replay and bridge frames are not simulated here.");
            if (_lastFrame is null)
                return new TripOutcome(false, "No solved frame yet, so there is nothing to trip.");

            GridModel model = _lastFrame.Model;
            (Generator Unit, int Index)? pick = InfeedSelection.LargestOnline(model, busId);
            if (pick is null)
            {
                string where = busId is int b ? $" at {BusLabel(model, b)}" : "";
                return new TripOutcome(false, $"No generation to trip{where}.");
            }

            Generator g = pick.Value.Unit;
            string name = g.Name ?? $"unit {pick.Value.Index} @ {BusLabel(model, g.Bus)}";
            _live.TripInfeed(g.Pg);
            return new TripOutcome(true,
                $"TRIP {name} (-{g.Pg.ToString("F0", CultureInfo.InvariantCulture)} MW)", name, g.Pg);
        }
    }

    /// <summary>A bus's display name for operator messages ("bus N" when it has none, or is not in the model).</summary>
    private static string BusLabel(GridModel model, int busId)
    {
        foreach (Bus b in model.Buses)
            if (b.Id == busId) return b.Name ?? $"bus {busId}";
        return $"bus {busId}";
    }

    // ---- bridge feed --------------------------------------------------------

    private void WireBridge(GridBridgeClient c)
    {
        c.TopologyReceived += OnBridgeTopology;
        c.StateReceived += OnBridgeScalars;      // real scalars (inertia for the raw headline), before OnBridgeState
        c.RawDataReceived += OnBridgeRaw;        // raw mix/weather, fired just BEFORE OnBridgeState in the same tick
        c.MarketDataReceived += OnBridgeState;
        c.Faulted += OnBridgeFault;
        c.MessageRejected += OnBridgeMessageRejected;   // one bad frame: shown, but the stream carries on (R275)
        // GR-228: a failure in this session's own frame handling is a GridSim fault, reported as such.
        c.SubscriberFailed += ex => { Error = ex.Message; RaiseState(); };
        c.EstimateReceived += OnBridgeEstimate;          // last in the tick: the estimated frame's quality block
    }

    // Keep the latest estimate block for the dashboard's quality pill. A frame without a block (a presolved or live
    // stream, or the server's live fallback) leaves the last one in place only while the source is estimated; any
    // other source clears it, so a stale PASS can never sit beside a non-estimated state.
    private void OnBridgeEstimate(SimFrame f, string? source, BridgeProtocol.EstimateBlock? est)
    {
        if (est is not null) LatestEstimate = est;
        else if (!string.Equals(source, "estimated", StringComparison.OrdinalIgnoreCase)) LatestEstimate = null;
        else return;
        RaiseState();
    }

    /// <summary>Capture the per-tick real scalars (frequency/RoCoF/inertia). Fires before <see cref="OnBridgeState"/>
    /// in the same receive-loop call, so the measured inertia is in place when <see cref="BuildEnvelope"/> runs.</summary>
    private void OnBridgeScalars(SimFrame f, BridgeProtocol.ScalarsBlock? scalars, BridgeProtocol.OverlaysBlock? overlays)
    {
        // N063: the per-asset condition / stress overlays and the HUD scalars (fragility, RoCoF headroom) used to be
        // dropped here, so the map's Condition / Stress modes and its bridge HUD never had data. They now ride the
        // envelope (FrameEnvelope.MapOverlays / MapHud), finite-guarded, as the WPF app feeds its renderer and HUD.
        MapOverlaysDto? builtOverlays = MapProjection.BuildOverlays(overlays);
        MapHudDto? builtHud = MapProjection.BuildHud(scalars);
        // GR-873: this runs on the bridge receive-loop thread while BuildEnvelope reads the same fields under _gate,
        // so the writes take the lock too (built outside it; the lock only covers the assignments).
        lock (_gate)
        {
            _rawScalars = scalars;
            _bridgeOverlays = builtOverlays;
            _bridgeHud = builtHud;
        }
    }

    /// <summary>
    /// N063: the GDA per-asset drill-down for one bus (the bridge <c>op="asset"</c> query), as the WPF substation page
    /// runs it. Asked at the instant of the bridge tick currently shown, so the card describes the same moment as the
    /// rest of the page. Fails loud (the caller shows the message): not a bridge session, or no tick received yet.
    /// </summary>
    public Task<QueryResult> QueryAssetAsync(int busId, CancellationToken ct = default)
    {
        if (_bridge is null)
            throw new InvalidOperationException("Per-asset GDA data is only available on a bridge session.");
        DateTime? tick;
        lock (_gate) tick = _bridgeTickUtc;   // GR-873: a DateTime? is not read atomically; the receive loop writes it
        DateTime at = tick
            ?? throw new InvalidOperationException("No bridge tick has arrived yet, so there is no instant to query.");
        return _bridge.Query("asset", at, new { busId }, ct);
    }

    private MapOverlaysDto? _bridgeOverlays;   // latest bridge overlays (null = none this tick / not a bridge)
    private MapHudDto? _bridgeHud;             // latest bridge HUD scalars (null = not a bridge session)

    /// <summary>Capture the raw source's per-tick MIX + WEATHER blocks. Fires (on the receive-loop thread)
    /// immediately before <see cref="OnBridgeState"/> for the same tick, so these are in place when
    /// <see cref="BuildEnvelope"/> runs. Null-captured for non-raw sources (which never emit the event).</summary>
    private void OnBridgeRaw(SimFrame f, BridgeProtocol.GenerationMixBlock? mix, BridgeProtocol.WeatherBlock? weather,
        BridgeProtocol.RawInterconnectorsBlock? ics, DateTime at)
    {
        // GR-873: written on the receive-loop thread, read under _gate by BuildEnvelope, so written under it too.
        lock (_gate)
        {
            _rawMix = mix;
            _rawWeather = weather;
            _rawIcs = ics;
            if (at != DateTime.MinValue) _rawAt = at;
            // Every source's tick (the bridge refuses an undated state frame before this fires), for the per-period
            // imbalance-price sample (N037).
            _bridgeTickUtc = at != DateTime.MinValue ? at : null;
        }
    }

    private async Task ConnectBridgeAsync(Uri endpoint, CancellationToken ct)
    {
        try
        {
            await _bridge!.ConnectAsync(endpoint, ct);
            // Stored lakes (presolved|estimated|solves) are a self-running historical replay:
            // auto-play on connect so topology + frames start flowing immediately (the dashboard
            // is otherwise gated on topology with no reachable Play control). Live sessions still
            // wait for an explicit Play.
            if (IsRaw)
            {
                // Raw needs a real start timestamp: fetch the rocof_1s range from /meta, then auto-play from a
                // recent default within it (RawDefaultStart). The scrubber + date-picker then seek within the range.
                await FetchRawRangeAsync(endpoint, ct);
                lock (_gate) Playing = true;
                await BridgeStartAsync();
                RaiseState();
            }
            else if (_bridgeSource is "presolved" or "estimated" or "solves")
            {
                lock (_gate) Playing = true;
                await BridgeStartAsync();
                RaiseState();
            }
        }
        catch (Exception ex) { Error = $"Bridge connect failed: {ex.Message}"; RaiseState(); }
    }

    // GR-237: resume (Play after Pause) continues from the last tick received instead of sending a fresh start from the
    // beginning (raw jumped back to 1 January, a live stream to the default start). A stored lake has no timestamp start
    // token in the protocol, so it still restarts from "first".
    private async Task BridgeStartAsync(bool resume = false)
    {
        double fps; DateTime? resumeAt;
        lock (_gate) { fps = Fps; resumeAt = resume ? (IsRaw ? _rawAt : _bridgeTickUtc) : null; }   // GR-873
        // Stored lakes (presolved|estimated|solves) play back the historical library itself: start
        // at the FIRST available frame and loop continuously (the bridge resolves "first" per case
        // and wraps the whole span). Live streams from a safely-past timestamp instead - derived from
        // the SINGLE horizon knob plus a two-day settlement-lag margin (matching the WPF 9-days-back
        // default) so the server's forward-inference guard accepts it.
        // A failed stream start (half-open/reset/closed socket) must SURFACE like every other bridge
        // failure in this class - not be swallowed by the old Safe() helper, which left the session 'Playing' forever
        // with no error and no data.
        try
        {
            await BridgeSendStartAsync(_bridge!, fps, resumeAt, _cts.Token);
        }
        catch (OperationCanceledException) { /* session tearing down */ }
        catch (Exception ex) { Error = $"Bridge start failed: {ex.Message}"; lock (_gate) Playing = false; RaiseState(); }
    }

    /// <summary>
    /// Send the start request for this session's source on <paramref name="client"/> (split out of
    /// <see cref="BridgeStartAsync"/> unchanged, so the supervised reconnect sends exactly the same request). Throws on a
    /// failure; the caller reports it. Every start instant is historical: a stored lake starts from its "first" frame,
    /// raw and live from the instant given (a tick the client already accepted through the horizon guard) or, with none,
    /// from the same safely-past default as before.
    /// </summary>
    private async Task BridgeSendStartAsync(GridBridgeClient client, double fps, DateTime? resumeAt, CancellationToken ct)
    {
        bool stored = _bridgeSource is "presolved" or "estimated" or "solves";
        if (IsRaw)
            await client.StartAsync(_bridgeCase, resumeAt ?? RawDefaultStart(), fps, "raw", ct: ct);
        else if (stored)
            await client.StartAsync(_bridgeCase, "first", fps, _bridgeSource, loop: true, ct: ct);
        else
        {
            DateTime start = resumeAt ?? GridSim.Core.Diagnostics.ForwardInferenceGuard.Horizon(DateTime.UtcNow)
                .AddDays(-PresentationLimits.SettlementLagMarginDays);
            await client.StartAsync(_bridgeCase, start, fps, _bridgeSource, ct);
        }
    }

    private void OnBridgeTopology(GridModel model)
    {
        // GR-711: an empty topology is an error the user sees, not a dashboard left on "waiting for topology" for ever
        // (TopologyReady is BusCount > 0). Since GR-147 the bridge reader refuses a model with no slack bus, so this is a
        // second line of defence.
        if (model.BusCount == 0)
        {
            Error = $"Bridge topology for '{_bridgeCase}' has no buses; nothing can be shown.";
            RaiseState();
            return;
        }
        // Bug #172: the licence gate, which bridge sessions never called before. A free-tier host streamed a remote
        // model of any size. A refused topology is never shown and the feed is stopped; the message says why.
        if (_bridgeLicence is not null)
        {
            GridSim.Core.Licensing.GateResult verdict = _bridgeLicence(model);
            if (!verdict.Allowed)
            {
                _bridgeLicenceRefused = true;
                _reconnect?.Stop();   // CAF B5: a refused feed is never reconnected
                Error = verdict.Message;
                lock (_gate) Playing = false;
                _ = BridgeControl("pause", () => _bridge!.StopAsync(_cts.Token));
                RaiseState();
                return;
            }
        }
        Topology = FrameProjector.BuildTopology(_bridgeCase, $"Bridge * {_bridgeCase}", model, replay: false, frameCount: 0);
        RaiseState();
    }

    // GR-224: this runs on the bridge client's receive-loop thread. It used to build the whole envelope here, so a slow
    // projection held up the receive loop (and the socket behind it). It now only captures this frame's receive-time state
    // and queues the frame; ProjectBridgeFrame builds and publishes it on the session's own worker, in arrival order.
    // Nothing is dropped: projecting a frame also advances the histories, the imbalance-price samples, the SCADA alarm log,
    // the mechanical ramp and the frequency-risk trace, so a skipped frame would lose a history point, an alarm transition
    // or a ramp step. A queue that fills (BridgeProjectionQueueCapacity frames behind) stops the session with an error.
    private void OnBridgeState(SimFrame f, BridgeProtocol.OperatorBlock? op, BridgeProtocol.MarketBlock? mk)
    {
        if (_bridgeLicenceRefused) return;   // bug #172: nothing of a refused feed is shown
        BridgeSnapshot snapshot;
        BridgeProjectionWorker<BridgeFrameWork> projector;
        lock (_gate)
        {
            if (_bridgeSwapClosed) return;   // the session is closing: nothing more is queued
            snapshot = new BridgeSnapshot(_rawScalars, _rawMix, _rawIcs, _bridgeOverlays, _bridgeHud, _bridgeTickUtc);
            _projector ??= new BridgeProjectionWorker<BridgeFrameWork>(
                ProjectBridgeFrame, OnProjectionFault, BridgeProjectionQueueCapacity);
            projector = _projector;
        }
        projector.Enqueue(new BridgeFrameWork(f, op, mk, snapshot));
    }

    // GR-224: the worker's projection of one queued frame: exactly what OnBridgeState did inline, with the frame's own
    // snapshot in place of the live per-tick fields.
    private void ProjectBridgeFrame(BridgeFrameWork work)
    {
        if (_bridgeLicenceRefused) return;
        // GR-228: a failure in this session's own frame handling is a GridSim fault, reported as such, and the stream
        // carries on (as when the bridge client caught it on the receive loop). The worker is not stopped by it.
        try
        {
            FrameEnvelope frame;
            lock (_gate) frame = BuildEnvelope(work.Frame, null, work.Op, work.Mk, work.Snapshot);
            Current = frame;
            Emit(frame);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex.Message;
            RaiseState();
        }
    }

    // GR-224: the projection worker failed (its queue overflowed, or a projection threw). Fail loud: the session shows the
    // reason as its error, stops playing and asks the server to stop the stream. Not reconnected: a reconnect would only
    // restart the same flood.
    private void OnProjectionFault(Exception ex)
    {
        Error = ex is InvalidOperationException
            ? ex.Message
            : $"Bridge frame projection failed ({ex.GetType().Name}): {ex.Message}";
        Console.Error.WriteLine($"GridSim.Web: error: bridge session {SessionId}: {Error}");
        lock (_gate) Playing = false;
        GridBridgeClient? bridge;
        lock (_gate) bridge = _bridge;
        if (bridge is { } live && !_cts.IsCancellationRequested)
            _ = BridgeControl("pause", () => live.StopAsync(_cts.Token));
        RaiseState();
    }

    /// <summary>
    /// N037: add this tick's imbalance price to the MARKET sparkline, one sample per settlement period, keyed on the
    /// bridge tick's own timestamp. Market blocks only arrive on bridge ticks, and the bridge client refuses an undated
    /// state frame, so the timestamp is always known when a price is; the per-sample fallback is kept only so a
    /// price can never be dropped silently if that ever changes. Runs under <see cref="_gate"/>.
    /// </summary>
    private void AppendImbalancePriceForTick(BridgeProtocol.MarketBlock? mk, DateTime? tickUtc)
    {
        if (tickUtc is DateTime at)
            TabProjections.AppendImbalancePrice(_imbalancePriceHist, mk?.ImbalancePriceGbpMwh, at, ref _imbalancePricePeriod);
        else
            TabProjections.AppendImbalancePrice(_imbalancePriceHist, mk?.ImbalancePriceGbpMwh);
    }

    private void OnBridgeFault(Exception ex)
    {
        Error = $"Bridge error: {ex.Message}"; RaiseState();
        // CAF B5: with supervision on, the lost stream is re-established on the backoff schedule (a feed the licence
        // refused is never reconnected). The supervisor refuses to retry a horizon rejection and says so.
        if (_reconnect is not null && !_bridgeLicenceRefused) _reconnect.ConnectionLost(ex);
    }

    // ---- CAF B5: supervised reconnect -----------------------------------------------------------------------------

    /// <summary>
    /// One reconnect attempt, called by the supervisor after its backoff wait: retire the old client, build a fresh one
    /// exactly as <see cref="CreateBridge"/> built the first (the same resilience settings, the same handlers, so the same
    /// exact 169 h horizon guard with no allowance), connect it, and, if the session was playing, restart the stream
    /// from the last tick already received (or the source's usual historical start). Throws when the attempt failed, so
    /// the supervisor schedules the next one. Nothing here moves or relaxes a guard: the start instant is a tick the
    /// previous client already accepted, "first" for a stored lake, or the safely-past default.
    /// </summary>
    private async Task ReconnectBridgeAsync(int attempt, CancellationToken ct)
    {
        Uri endpoint = _bridgeEndpoint
            ?? throw new InvalidOperationException("This bridge session has no endpoint to reconnect to.");
        GridBridgeClient fresh;
        GridBridgeClient? old;
        lock (_gate)
        {
            // The session is closing (or the supervisor stopped): build and install nothing.
            if (_bridgeSwapClosed || ct.IsCancellationRequested)
                throw new OperationCanceledException("The bridge session is closing; the reconnect was abandoned.", ct);
            fresh = new GridBridgeClient();
            _bridgeResilience.ApplyTo(fresh);   // the same message cap, in-flight cap and transport rule as the first
            old = _bridge;
            _bridge = fresh;
        }
        WireBridge(fresh);
        if (old is not null)
        {
            // The old client's handlers come off first, so nothing it raises while closing reaches this session.
            UnwireBridge(old);
            try { await old.DisposeAsync(); } catch { /* already down */ }
        }

        await fresh.ConnectAsync(endpoint, ct);
        bool playing;
        double fps;
        DateTime? resumeAt;
        lock (_gate) { playing = Playing; fps = Fps; resumeAt = IsRaw ? _rawAt : _bridgeTickUtc; }   // GR-873
        if (playing) await BridgeSendStartAsync(fresh, fps, resumeAt, ct);

        // The fault left Error set (the whole dashboard is replaced by it); the stream is back, so it is cleared and the
        // reconnect is said as a notice.
        Error = null;
        Warning = string.Create(CultureInfo.InvariantCulture, $"Bridge reconnected on attempt {attempt}.");
        RaiseState();
    }

    // The handlers WireBridge attached that a retired client must lose (the SubscriberFailed lambda cannot be removed,
    // and is harmless on a disposed client).
    private void UnwireBridge(GridBridgeClient c)
    {
        c.TopologyReceived -= OnBridgeTopology;
        c.StateReceived -= OnBridgeScalars;
        c.RawDataReceived -= OnBridgeRaw;
        c.MarketDataReceived -= OnBridgeState;
        c.Faulted -= OnBridgeFault;
        c.MessageRejected -= OnBridgeMessageRejected;
        c.EstimateReceived -= OnBridgeEstimate;
    }

    // The supervisor's reports. Each is written to the host's log (standard error, as the web host's other bridge
    // warnings are) and shown on the session: a pending attempt as a Warning, the final give-up as the Error.
    private void OnReconnectScheduled(BridgeReconnectSchedule s)
    {
        string of = s.MaxAttempts > 0 ? string.Create(CultureInfo.InvariantCulture, $" of {s.MaxAttempts}") : "";
        string line = string.Create(CultureInfo.InvariantCulture,
            $"Bridge connection lost ({s.Cause.Message}); reconnect attempt {s.Attempt}{of} in {s.Delay.TotalSeconds:0.###} s.");
        Console.Error.WriteLine($"GridSim.Web: warning: bridge session {SessionId}: {line}");
        Warning = line;
        RaiseState();
    }

    private void OnReconnectAttemptCompleted(BridgeReconnectAttempt a)
    {
        Console.Error.WriteLine(a.Succeeded
            ? string.Create(CultureInfo.InvariantCulture, $"GridSim.Web: bridge session {SessionId}: reconnected on attempt {a.Attempt}.")
            : string.Create(CultureInfo.InvariantCulture, $"GridSim.Web: warning: bridge session {SessionId}: reconnect attempt {a.Attempt} failed ({a.Failure!.GetType().Name}: {a.Failure.Message})."));
    }

    private void OnReconnectGaveUp(BridgeReconnectGiveUp g)
    {
        Console.Error.WriteLine($"GridSim.Web: error: bridge session {SessionId}: {g.Reason}");
        Error = g.Reason;
        lock (_gate) Playing = false;
        RaiseState();
    }

    // A single unreadable bridge message was skipped. Surface it (with the running count) rather than let it vanish.
    // GR-236: the stream carries on after a rejected message (R275), so this is a Warning, not an Error: setting Error
    // replaced the whole dashboard with "Couldn't load" for ever, although good frames kept arriving.
    private void OnBridgeMessageRejected(BridgeMessageException ex)
    {
        Warning = $"Bridge frame rejected ({_bridge?.RejectedMessages ?? 0} so far): {ex.Message}";
        RaiseState();
    }

    private void RaiseState() { try { StateChanged?.Invoke(); } catch { } }

    // Known limitation "Web output conventions" (chapter 36): a bridge pause, speed change or seek that failed (a closed
    // or half-open socket, a command the server refused) used to go through a Safe() helper that swallowed every
    // exception, so the dashboard looked as if the stream had obeyed. Each now runs through BridgeControl, which
    // reports a failure as a Warning (not an Error: the stream may well still be running, and Error replaces the whole
    // dashboard, see GR-236) and raises the state so the page shows it. The command itself, and what is sent, is
    // unchanged. A session that is closing stays silent, as before: DisposeAsync cancels _cts before it disposes it,
    // so a failure seen once _cts is cancelled (whatever its type) is the teardown, not something to report.
    private async Task BridgeControl(string what, Func<Task> op)
    {
        string? failure = await DescribeBridgeControlFailure(what, op);
        if (failure is null || _cts.IsCancellationRequested) return;
        Warning = failure;
        RaiseState();
    }

    /// <summary>Run one bridge control command and return the warning text for its failure, or null when it succeeded
    /// or was cancelled (<see cref="OperationCanceledException"/>). Kept separate and internal so the rule can be
    /// tested without a live bridge.</summary>
    internal static async Task<string?> DescribeBridgeControlFailure(string what, Func<Task> op)
    {
        ArgumentNullException.ThrowIfNull(op);
        try
        {
            await op();
            return null;
        }
        catch (OperationCanceledException) { return null; }   // cancelled: the session is tearing down
        catch (Exception ex)
        {
            return $"Bridge {what} failed ({ex.GetType().Name}: {ex.Message}); the stream may not have followed it.";
        }
    }

    // ---- driving loop -------------------------------------------------------

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            double fps;
            lock (_gate) fps = Fps;
            try { await Task.Delay(TimeSpan.FromSeconds(1.0 / fps), ct); }
            catch (OperationCanceledException) { break; }

            FrameEnvelope? frame = null;
            Exception? failed = null;
            lock (_gate)
            {
                if (Playing)
                {
                    // In replay, stop at the end rather than looping.
                    if (_replay is not null && _replay.AtEnd) Playing = false;
                    else
                    {
                        // GR-222: a step (the solve or any projection) that throws used to fault this loop silently: no
                        // error, the lamp frozen on the last frame and the session still "Playing". It now pauses the
                        // session and says why; the loop keeps running, so play can be pressed again.
                        try { frame = ProduceNextLocked(); }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Playing = false;
                            Error = $"The session stopped: producing the next frame failed ({ex.GetType().Name}: {ex.Message}).";
                            failed = ex;
                        }
                    }
                }
            }
            if (failed is not null) { RaiseState(); continue; }
            if (frame is not null) Emit(frame);
        }
    }

    // ---- projection (all under _gate) ---------------------------------------

    private FrameEnvelope ProduceInitial()
    {
        if (_replay is not null)
        {
            _replay.Restart();
            return BuildEnvelope(_replay.Current, _replay.CurrentLabel);
        }
        // Live: step tick 0 for an initial solved operating point.
        var sw0 = System.Diagnostics.Stopwatch.StartNew();
        SimFrame f0 = _live!.Step(_tick, _stepMinutes);
        _lastBaseSolveMs = sw0.Elapsed.TotalMilliseconds;
        return BuildEnvelope(f0, null);
    }

    private FrameEnvelope ProduceNextLocked()
    {
        FrameEnvelope frame;
        if (_replay is not null)
        {
            _replay.Advance();
            // Enrich replay frames too (GS-H5): a bare Project() leaves every tab but NETWORK null and
            // stops the stateful services (ramp/SCADA alarms/history) accumulating during play.
            frame = BuildEnvelope(_replay.Current, _replay.CurrentLabel);
        }
        else
        {
            _tick++;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            SimFrame f = _live!.Step(_tick, _stepMinutes);
            _lastBaseSolveMs = sw.Elapsed.TotalMilliseconds;
            frame = BuildEnvelope(f, null);
        }
        Current = frame;
        return frame;
    }

    /// <summary>Project the base frame and enrich it with every tab's DTO (stateless projections + the
    /// per-session stateful services). Lake blocks are non-null only in bridge mode. Runs under <see cref="_gate"/>.</summary>
    private FrameEnvelope BuildEnvelope(SimFrame f, string? label,
        BridgeProtocol.OperatorBlock? op = null, BridgeProtocol.MarketBlock? mk = null, BridgeSnapshot? snapshot = null)
    {
        _lastFrame = f;   // snapshot for the on-demand harmonics study (RunHarmonicsStudy)

        // GR-224: a bridge frame is projected from the values captured when it arrived (snapshot); any other caller (live,
        // replay) passes none and reads the session's fields, as before.
        BridgeProtocol.ScalarsBlock? rawScalars = snapshot is null ? _rawScalars : snapshot.RawScalars;
        BridgeProtocol.GenerationMixBlock? rawMix = snapshot is null ? _rawMix : snapshot.RawMix;
        BridgeProtocol.RawInterconnectorsBlock? rawIcs = snapshot is null ? _rawIcs : snapshot.RawIcs;
        MapOverlaysDto? bridgeOverlays = snapshot is null ? _bridgeOverlays : snapshot.Overlays;
        MapHudDto? bridgeHud = snapshot is null ? _bridgeHud : snapshot.Hud;
        DateTime? tickUtc = snapshot is null ? _bridgeTickUtc : snapshot.TickUtc;

        if (IsRaw)
        {
            // Pure RAW replay: a degenerate no-solve frame. Build a LEAN envelope from the real per-tick scalars +
            // the published generation MIX, and attach ONLY the raw-safe panels. Every solve/physics tab is skipped
            // (meaningless on an empty result, hidden in the replay-only UI, and a needless per-frame cost).
            // System inertia for the charts/traces. The per-tick ScalarsBlock.InertiaMwS is often
            // absent on the raw replay, but the lake DOES carry the published inertia on the Operator
            // block (GVA.s) - so fall back to that (Outturn, else Synchronous, else Market) instead of
            // charting a flat zero.
            double inertiaGvaS = (rawScalars?.InertiaMwS ?? 0.0) / 1000.0;
            if (inertiaGvaS <= 0.0)
                inertiaGvaS = op?.OutturnInertiaGvaS ?? op?.SynchronousInertiaGvaS ?? op?.MarketInertiaGvaS ?? 0.0;
            FuelBreakdown rfb = rawMix is not null
                ? new FuelBreakdown(rawMix, inertiaGvaS)
                : new FuelBreakdown(f.Model);
            Push(_freqHist, f.FrequencyHz);
            Push(_demandHist, f.DemandMw);
            Push(_inertiaHist, rfb.InertiaGvaS);
            Push(_genHist, rfb.TotalGenMw);
            FrameEnvelope renv = FrameProjector.ProjectRaw(f, rfb.InertiaGvaS, label);
            // Real raw interconnector flows (moving with the replay). Only ACTIVE links are shown - links at ~0 MW
            // (idle / unavailable / rounding to "0") are dropped so the list isn't padded with zero-flow rows.
            const double IcActiveMw = PresentationLimits.IdleInterconnectorMw;
            var rawIcLinks = rawIcs?.Rows().Where(ic => Math.Abs(ic.Mw) >= IcActiveMw)
                             .Select(ic => new ImportLinkDto(ic.Name, ic.Mw, ic.Mw > 0)).ToList()
                             ?? new List<ImportLinkDto>();
            var rawMktIcs = rawIcs?.Rows().Where(ic => Math.Abs(ic.Mw) >= IcActiveMw)
                            .Select(ic => new MktInterconnectorRowDto(ic.Name, ic.Mw, ic.Mw > 0)).ToList()
                            ?? new List<MktInterconnectorRowDto>();
            double rawNet = rawIcs?.Net ?? rawIcLinks.Sum(l => l.Mw);
            MarketDto rawMarket = TabProjections.Market(f, rfb, mk);
            if (rawMktIcs.Count > 0) rawMarket = rawMarket with { Interconnectors = rawMktIcs };
            AppendImbalancePriceForTick(mk, tickUtc);
            rawMarket = TabProjections.WithImbalancePriceHistory(rawMarket, _imbalancePriceHist);
            return renv with
            {
                Traces = new TracesDto(_freqHist.ToArray(), _demandHist.ToArray(), _inertiaHist.ToArray()),
                GenInertia = TabProjections.GenInertia(f, rfb) with { ImportLinks = rawIcLinks, NetImportMw = rawNet, EmbeddedGenMw = rawMix?.EmbeddedMw() ?? 0.0 },
                Operator = TabProjections.Operator(f, rfb, op),
                Market = rawMarket,
                // Kept in the raw view - both are safe on the degenerate frame: Rp1 reads the raw FuelBreakdown +
                // real frequency; HumanScale's twist section is guarded when the bus angles are absent.
                // Feed RP1 the REAL raw interconnector flows (same source as GEN & INERTIA / MARKET) so its import
                // figures match the other tabs rather than the static topology dispatch.
                Rp1 = TabProjections.Rp1(f, rfb, rawIcs?.Rows(), _purchasedTrace),
                HumanScale = HumanScaleProjection.Project(f, rfb.InertiaGvaS),
                Charts = BuildRawCharts(),
                MapOverlays = bridgeOverlays,
                MapHud = bridgeHud,
                // THE MACHINE (raw): grid-as-one-machine rotation/torque from real frequency+demand+mix, no solve.
                // MachineView (regulation strain) stays null - that electrical gauge is hidden under the Raw guard.
                Machine = TabProjections.MachineRaw(f, rfb, _freqHist.ToArray(), RawFreqTrend()),
            };
        }

        // Hand this solved frame to the resident analysis cycle (non-blocking; runs off the gate). We only
        // ever read back its LATEST telemetry below, so a frame is never stalled by the fan-out.
        _cycle?.Offer(f, _lastBaseSolveMs);
        FrameEnvelope env = FrameProjector.Project(f, label);
        var fb = new FuelBreakdown(f.Model);
        Push(_freqHist, f.FrequencyHz);
        Push(_demandHist, f.DemandMw);
        Push(_inertiaHist, fb.InertiaGvaS);
        AppendImbalancePriceForTick(mk, tickUtc);
        return env with
        {
            Traces = new TracesDto(_freqHist.ToArray(), _demandHist.ToArray(), _inertiaHist.ToArray()),
            GenInertia = TabProjections.GenInertia(f, fb),
            Operator = TabProjections.Operator(f, fb, op),
            Market = TabProjections.WithImbalancePriceHistory(TabProjections.Market(f, fb, mk), _imbalancePriceHist),
            Rp1 = TabProjections.Rp1(f, fb, trace: _purchasedTrace),
            // F-047: a bridge frame is projected at its own tick instant (_bridgeTickUtc, set under _gate by
            // OnBridgeRaw just before OnBridgeState for the same tick, and captured with the frame since GR-224), so the
            // ramp uses the real tick gap.
            Mechanical = _mechanical.IsTimestamped ? _mechanical.ProjectAtTick(f, tickUtc) : _mechanical.Project(f),
            Scada = _scada.Project(f),
            Machine = _machine.Project(f, fb),
            // Read AFTER Project(): it recomputes LastView (regulation strain) as a side effect. `with`
            // initializers evaluate top-to-bottom, so Machine is projected before this reads its extras.
            MachineView = _machine.LastView,
            Domains = TabProjections.Domains(f),
            // ASSETS explorer: the per-frame asset graph (deeper R/X/B/H/tap/OLTC/Q fields the navigable
            // detail pages need beyond topology+state). Built from the live model + solved result.
            AssetGraph = AssetGraphProjection.Build(f.Model, f.Result),
            // CYCLE: the latest telemetry from the resident analysis cycle (null until its first cycle lands).
            Cycle = _cycle?.Latest,
            CycleError = _cycle?.LastError,
            // Bridge only (null on a live / replay session): N063 condition / stress overlays and HUD scalars.
            MapOverlays = bridgeOverlays,
            MapHud = bridgeHud,
        };
    }

    private static void Push(Queue<double> q, double v)
    {
        q.Enqueue(v);
        while (q.Count > HistoryLen) q.Dequeue();
    }

    // Per-second frequency trend for the raw MACHINE readout: the last two history samples (consecutive sim-seconds).
    private double RawFreqTrend() => _freqHist.Count >= 2 ? _freqHist.Last() - _freqHist.ElementAt(_freqHist.Count - 2) : 0.0;

    // Rolling multi-series history for the CHARTS tab. Frequency/generation/demand/inertia are the raw published
    // channels; RPM (hz -> 3000-rpm nominal) and delivered torque (T = P/omega, the grid-as-one-machine view) are
    // derived per-sample from the same history so they move exactly with the real data - still no solve.
    private ChartsDto BuildRawCharts()
    {
        double[] freq = _freqHist.ToArray();
        double[] dem = _demandHist.ToArray();
        double[] gen = _genHist.ToArray();
        double[] inr = _inertiaHist.ToArray();
        int n = freq.Length;
        var rpm = new double[n];
        var torque = new double[n];
        for (int i = 0; i < n; i++)
        {
            double hz = freq[i];
            rpm[i] = hz / GridSim.Core.Dynamics.FrequencyLimits.NominalHz * PresentationLimits.TwoPoleSyncRpm;
            double g = i < gen.Length ? gen[i] : 0.0;
            double omega = 2.0 * Math.PI * hz;              // electrical angular speed proxy (2*pi*f)
            torque[i] = omega > PresentationLimits.MinOmegaRadPerS ? g / omega : 0.0;     // MW / (rad/s) == MN.m
        }
        return new ChartsDto(freq, rpm, gen, dem, inr, torque);
    }

    // ---- harmonics on-demand study ------------------------------------------

    /// <summary>
    /// Run the frequency-domain harmonic study for the session's CURRENT model, on demand (from the HARMONICS
    /// tab's RUN STUDY button). The penetration study is a per-order complex solve over the whole network
    /// (default 24 orders) plus a driving-point resonance scan - far too heavy for the per-frame path - so it
    /// is CACHED by model identity (a re-press / re-render returns the cached rollup without recomputing) and
    /// GATED by bus count (a very large case returns <see cref="HarmonicsDto.Skipped"/> with a reason rather
    /// than block the circuit). The heavy solve runs OUTSIDE <see cref="_gate"/> against the frame's immutable
    /// model/result snapshot (each tick materialises a fresh <see cref="GridModel"/>), so it never stalls the
    /// stepping loop.
    /// </summary>
    public HarmonicsDto RunHarmonicsStudy() => RunHarmonicsStudyWith(HarmonicSourceChoice.Synthetic);

    /// <summary>
    /// N151: the on-demand study with a chosen set of harmonic sources (<see cref="HarmonicSourceChoice"/>):
    /// the default synthetic source (exactly <see cref="RunHarmonicsStudy()"/>) or the opt-in per-converter rule of
    /// Core at the rated or dispatched level. Cached by model instance AND source choice, gated by bus count, exactly
    /// as the default study.
    /// </summary>
    public HarmonicsDto RunHarmonicsStudyWith(HarmonicSourceChoice choice)
    {
        SimFrame? f;
        HarmonicsDto? cached;
        GridModel? cachedModel;
        HarmonicSourceChoice cachedChoice;
        lock (_gate) { f = _lastFrame; cached = _harmonicsCache; cachedModel = _harmonicsCacheModel; cachedChoice = _harmonicsCacheChoice; }

        if (f is null) return HarmonicsDto.Skipped();                 // no solved frame yet
        GridModel model = f.Model;

        // Cache hit: same model instance and same source choice as the last study -> free (re-press / re-render never
        // recomputes).
        if (ReferenceEquals(cachedModel, model) && cachedChoice == choice && cached is not null) return cached;

        HarmonicsDto dto;
        if (model.BusCount > HarmonicsBusLimit)
        {
            // Gate: refuse to block the UI thread on a very large case (mirrors the experimental heavy-model guard).
            dto = HarmonicsDto.Skipped() with
            {
                ProvenanceTag = $"skipped: {model.BusCount} buses exceeds the {HarmonicsBusLimit}-bus on-demand gate",
            };
        }
        else
        {
            dto = StudyHarmonics(model, f.Result, choice);
        }

        lock (_gate) { _harmonicsCache = dto; _harmonicsCacheModel = model; _harmonicsCacheChoice = choice; }
        return dto;
    }

    /// <summary>
    /// The on-demand harmonic study for one solved model, with the engine's expected failure turned into a
    /// visible verdict. A <see cref="SingularHarmonicNetworkException"/> (part of the network has no path to
    /// ground at some harmonic order, so no harmonic voltage exists there) is a property of the MODEL, not a
    /// crash: it comes back as <see cref="HarmonicsDto.Failure"/> carrying the engine's own message, and is cached
    /// like a result because the same model always fails the same way. Any other exception still propagates, and
    /// the tab reports it (it must never leave the RUN STUDY button stuck or take the circuit down).
    /// </summary>
    public static HarmonicsDto StudyHarmonics(GridModel model, PowerFlowResult result) =>
        StudyHarmonics(model, result, HarmonicSourceChoice.Synthetic);

    /// <summary>
    /// N151: <see cref="StudyHarmonics(GridModel, PowerFlowResult)"/> with a chosen set of sources. The synthetic
    /// choice is exactly the default study. A per-converter choice that cannot give a study (no converter-interfaced
    /// unit injects, or a unit's rating / output is not a usable number: Core's <see cref="ArgumentException"/>) comes
    /// back as <see cref="HarmonicsDto.Failure"/> with the reason, the web form of the CLI's exit 1.
    /// </summary>
    public static HarmonicsDto StudyHarmonics(GridModel model, PowerFlowResult result, HarmonicSourceChoice choice)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(result);
        if (!Enum.IsDefined(choice))
            throw new ArgumentOutOfRangeException(nameof(choice), choice, "unknown harmonic source choice");
        try
        {
            return choice switch
            {
                // N131: the default synthetic source is the G5 Issue 5 Table B.3 six-pulse profile; the idealised 1/h
                // spectrum stays available as its own choice.
                HarmonicSourceChoice.Synthetic => ComputeHarmonics(model, result, HarmonicSourceLibrary.DefaultSyntheticSourceType),
                HarmonicSourceChoice.SyntheticIdeal => ComputeHarmonics(model, result, HarmonicSourceLibrary.IdealSixPulseType),
                _ => ComputeConverterHarmonics(model, result,
                    choice == HarmonicSourceChoice.ConverterRated ? ConverterHarmonicLevel.Rated : ConverterHarmonicLevel.Dispatched),
            };
        }
        catch (ArgumentException ex) when (choice is not (HarmonicSourceChoice.Synthetic or HarmonicSourceChoice.SyntheticIdeal))
        {
            // The per-converter rule could not build a study from this model (no injecting converter unit, or a
            // rating / output that is not a usable number). An input property, shown as a failed study; the default
            // synthetic study keeps its old behaviour (any ArgumentException still propagates there).
            return HarmonicsDto.Failure(ex.Message);
        }
        catch (SingularHarmonicNetworkException ex)
        {
            return HarmonicsDto.Failure(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            // HarmonicIndices.SolvedFundamentalVm: the frame's fundamental did not converge (or a bus has no usable
            // |V_1|), so no distortion ratio exists. A visible verdict, never a THD over a meaningless denominator.
            return HarmonicsDto.Failure(ex.Message);
        }
    }

    /// <summary>The actual engine calls, mirroring the CLI <c>harmonics</c> command's synthetic-source default:
    /// ONE six-pulse source of library type <paramref name="sourceType"/> at the largest-demand bus (the G5 Issue 5
    /// Table B.3 profile by default since N131, or the idealised 1/h) (not one per converter: see
    /// <see cref="HarmonicsDto.PerConverterLimitation"/>, carried to the tab in <see cref="HarmonicsDto.SourceNote"/>), the penetration solve <c>Y(h) V = I</c>, voltage-THD
    /// indices, a driving-point resonance scan at the worst-THD bus, and the IEEE-519 / G5-5 compliance
    /// verdicts - mapped through <see cref="HarmonicsProjection.FromCore"/>. The solved fundamental voltages
    /// (<c>result.Vm</c>, bus-aligned) normalise the THD/IHD ratios.</summary>
    private static HarmonicsDto ComputeHarmonics(GridModel model, PowerFlowResult result, string sourceType)
    {
        if (model.BusCount == 0) return HarmonicsDto.Skipped();

        int srcBus = model.Buses.OrderByDescending(b => b.Pd).FirstOrDefault()?.Id ?? model.Buses[0].Id;
        const double SourceCurrentPu = 0.5;
        var sources = new[] { HarmonicSourceLibrary.Make(sourceType, srcBus, fundamentalCurrentPu: SourceCurrentPu) };
        // The physical study model, the same one the CLI 'harmonics' verb runs (R001): network + machine
        // source impedance + loads as damping shunts.
        HarmonicModelOptions opt = HarmonicModelOptions.Study;

        HarmonicPenetrationResult pen = HarmonicPenetration.Solve(model, sources, opt);
        // The solved fundamental |V_1| per bus, matched by bus id, through the same Core function the CLI 'harmonics'
        // verb uses, so the tab and the CLI print the same THD for the same network and sources. It throws
        // InvalidOperationException on a non-converged fundamental (StudyHarmonics turns that into a Failure verdict);
        // it used to fall back to 1.0 pu silently when the result carried no voltages.
        double[] vm = HarmonicIndices.SolvedFundamentalVm(pen, result);
        HarmonicIndicesResult idx = HarmonicIndices.FromPenetration(pen, vm);

        int scanBus = idx.WorstBusId != 0 ? idx.WorstBusId : model.Buses[0].Id;
        ImpedanceScanResult scan = HarmonicImpedanceScan.DrivingPoint(model, scanBus, opt);

        return HarmonicsProjection.FromCore(
            model, pen,
            provenanceTag: sourceType == HarmonicSourceLibrary.IdealSixPulseType
                ? "assumed-typical - synthetic 6-pulse source [VERIFY]"
                : "typical - synthetic six-pulse source, G5 Issue 5 Table B.3 profile [VERIFY]",
            fundamentalVm: vm,
            scan: scan) with
        {
            // R099: say exactly which source was injected, and that per-converter sources are not modelled.
            SourceNote = HarmonicsDto.SyntheticSourceNote(sourceType, SourceCurrentPu, srcBus),
            // N151 (N075): components at orders the study did not analyse. Always empty for the synthetic six-pulse
            // sources (G5 orders 5 to 49 odd, 1/h orders 5 to 25, all inside the analysed 3 to 49); carried so the tab
            // would say so if that ever changed.
            ComponentsOutsideOrders = pen.ComponentsOutsideOrders,
        };
    }

    /// <summary>
    /// N151: the per-converter study, the web form of the CLI's <c>harmonics --converter-sources rated|dispatched</c>
    /// (N036): one G5 Issue 5 Table B.3 source per converter-interfaced unit from Core's
    /// <see cref="ConverterHarmonicSources.FromModel"/> (balanced, so triplen components are left out and listed), over
    /// <see cref="ConverterHarmonicSources.StudyOptions"/> (the default study model over orders 2 to 50), then the same
    /// indices, driving-point scan and compliance as the default study. Throws <see cref="ArgumentException"/> when the
    /// rule cannot build a study (StudyHarmonics turns that into a Failure verdict).
    /// </summary>
    private static HarmonicsDto ComputeConverterHarmonics(GridModel model, PowerFlowResult result, ConverterHarmonicLevel level)
    {
        if (model.BusCount == 0) return HarmonicsDto.Skipped();

        // N150 (1.95.243): the dispatched level reads each unit's SOLVED output from this tick's solve (the slack's P and
        // every PV unit's Q), not its schedule; the rated level ignores the operating point.
        ConverterHarmonicSourceSet set = ConverterHarmonicSources.FromModel(model, level, balanced: true, operatingPoint: result);
        string levelWord = level == ConverterHarmonicLevel.Rated ? "rated" : "dispatched";
        if (set.Sources.Count == 0)
            throw new ArgumentException(
                $"Per-converter sources ({levelWord}): {model.Name} has no converter-interfaced unit that injects " +
                $"({set.UnitsNotModelled.Count} converter unit(s) not modelled" +
                $"{(set.UnitsNotModelled.Count > 0 ? ": " + string.Join("; ", set.UnitsNotModelled) : "")}).");

        HarmonicModelOptions opt = ConverterHarmonicSources.StudyOptions;
        HarmonicPenetrationResult pen = HarmonicPenetration.Solve(model, set.Sources, opt);
        double[] vm = HarmonicIndices.SolvedFundamentalVm(pen, result);
        HarmonicIndicesResult idx = HarmonicIndices.FromPenetration(pen, vm);
        int scanBus = idx.WorstBusId != 0 ? idx.WorstBusId : model.Buses[0].Id;
        ImpedanceScanResult scan = HarmonicImpedanceScan.DrivingPoint(model, scanBus, opt);

        int afe = set.Sources.Count(s => s.SourceType == ConverterHarmonicSources.SelfCommutatedProfile);
        var conv = new HarmonicConverterSourcesDto(levelWord, afe, set.Sources.Count - afe, set.OmittedComponents, set.UnitsNotModelled);
        return HarmonicsProjection.FromCore(
            model, pen,
            provenanceTag: "curated placeholder - G5 Issue 5 Table B.3 profile per converter [VERIFY]",
            fundamentalVm: vm,
            scan: scan) with
        {
            SourceNote = HarmonicsDto.ConverterSourceNote(conv),
            ComponentsOutsideOrders = pen.ComponentsOutsideOrders,
            ConverterSources = conv,
        };
    }

    private void Emit(FrameEnvelope frame)
    {
        try { FrameProduced?.Invoke(frame); }
        catch { /* a dead subscriber must not kill the session loop */ }
    }

    private int _disposed;

    // D-DOC-076: the SessionManager's slot for this session, handed back once on the first DisposeAsync. Null for a
    // session built directly through a factory (tests), which holds no slot.
    private Action? _release;

    /// <summary>D-DOC-076: called once by <see cref="SessionManager.Create"/> with the action that gives this
    /// session's slot back under the session cap. A second attach is a programming error and is refused.</summary>
    internal void AttachRelease(Action release)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (Interlocked.CompareExchange(ref _release, release, null) is not null)
            throw new InvalidOperationException("This session already holds a session slot.");
    }

    public async ValueTask DisposeAsync()
    {
        // Idempotent: the /ws/sim start handler can dispose a session and then, on a failed Create, dispose
        // the same instance again from the socket's finally block. A second _cts.Cancel()/Dispose() would
        // throw ObjectDisposedException out of the endpoint and break the control channel.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // D-DOC-076: give the session slot back first, so a teardown that throws below cannot leak it (the guard
        // above makes this run exactly once per session).
        Interlocked.Exchange(ref _release, null)?.Invoke();
        _cts.Cancel();
        if (_cycle is not null)
        {
            _cycle.Updated -= RaiseState;
            await _cycle.DisposeAsync();
        }
        try { await _loop; } catch { /* loop cancelled */ }
        // CAF B5: stop the reconnect supervisor first (no further attempt), then close the swap so an attempt that was
        // already past its wait cannot install a new client after this point. After this lock, _bridge is final.
        if (_reconnect is not null) await _reconnect.DisposeAsync();
        lock (_gate) _bridgeSwapClosed = true;
        if (_bridge is not null)
        {
            _bridge.TopologyReceived -= OnBridgeTopology;
            _bridge.StateReceived -= OnBridgeScalars;
            _bridge.RawDataReceived -= OnBridgeRaw;
            _bridge.MarketDataReceived -= OnBridgeState;
            _bridge.Faulted -= OnBridgeFault;
            _bridge.MessageRejected -= OnBridgeMessageRejected;
            _bridge.EstimateReceived -= OnBridgeEstimate;
            try { await _bridge.DisposeAsync(); } catch { /* already down */ }
        }
        // GR-224: the bridge is closed and unwired, so nothing more is queued; stop the projection worker (an item being
        // projected finishes, queued ones are abandoned) before the cancellation source goes.
        BridgeProjectionWorker<BridgeFrameWork>? projector;
        lock (_gate) projector = _projector;
        if (projector is not null) await projector.DisposeAsync();
        _cts.Dispose();
    }
}
