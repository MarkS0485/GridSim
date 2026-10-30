using GridSim.Core.Analysis;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Overlay;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Layout;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Ui.Models;

namespace GridSim.Ui.Hosting;

/// <summary>
/// The simulation host of the desktop control suite, with no user-interface framework: it loads a model (a local case
/// into a <see cref="LiveSim"/>, or a pre-solved run into a <see cref="PresolvedReplay"/>), runs the play loop and
/// produces frames. A front end drives it and shows the frames on its own thread.
///
/// <para>UI programme Phase 0.2 (the strangler extraction): this is the code the WPF <c>MainWindow</c> used to hold
/// inline (<c>SwitchTo</c>, <c>LoadReplay</c>, <c>StepOnce</c> and the <c>DispatcherTimer</c> that drove it), moved here
/// unchanged in what it computes. The same model is read the same way, the same <see cref="LiveSim"/> is built with the
/// same arguments, each step asks for the same tick number and step length, and the boundary transfers are evaluated the
/// same way, so every frame is bit for bit the frame the window produced before (<c>UiHostFrameEqualityTests</c> proves
/// it against a copy of the old code). Only the driver changed: a <see cref="CancellationToken"/> loop
/// (<see cref="RunAsync"/>, the pattern of the web app's per-connection session) instead of a WPF timer.</para>
///
/// <para>Threading. The loop runs on a thread-pool thread; everything else (load, replay moves, play/pause, speed, step,
/// trip) is called from the front end's thread. One lock guards the feed and cursor state; the solve itself runs outside
/// the lock, so a long solve never blocks the front end. Two rules keep the frames in the old order:</para>
/// <list type="bullet">
/// <item><description>Solves never overlap: the loop makes one frame, then awaits the front end's delivery of it before
/// it makes the next, as the window's timer did nothing while a solve or its display was still running.</description></item>
/// <item><description>A frame made before a load or a hand-driven replay move is stale: <see cref="Epoch"/> moves on at
/// every such change, and the front end drops a frame whose <see cref="HostFrame.Epoch"/> is behind, as the window
/// dropped a solve that finished after the model was switched.</description></item>
/// </list>
/// </summary>
public sealed class SimHost
{
    /// <summary>The loop interval at a playback rate of x1, in milliseconds (the old window timer's interval).</summary>
    public const double BasePlaybackMs = 800;

    /// <summary>The default simulated minutes per step: 20 min, the last STEP choice (the day sweep).</summary>
    public const double DefaultStepMinutes = 20;

    private readonly object _gate = new();
    // Completed to wake the loop at once instead of at the end of its interval (the first step after a load); the loop
    // swaps in a fresh one after each wake. Under _gate. A task, not a semaphore, so the host holds nothing to dispose.
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ---- feed state, all under _gate ----
    private LiveSim? _live;            // non-null while a local model runs
    private PresolvedReplay? _replay;  // non-null while a pre-solved run is loaded
    private string? _activeSolver;     // the live simulation's resolved engine
    private int _tick;                 // the last tick a live step was asked for (0 after a load)
    private int _epoch;                // moves on at every load, detach and hand-driven replay move
    private bool _playing;             // the loop makes frames only while this is set
    private double _stepMinutes = DefaultStepMinutes;
    private double _playbackRate = 1;

    // ======================================== state ========================================

    /// <summary>The current epoch: a frame whose <see cref="HostFrame.Epoch"/> differs is stale and must be dropped.</summary>
    public int Epoch { get { lock (_gate) return _epoch; } }

    /// <summary>True while a pre-solved run is loaded.</summary>
    public bool IsReplay { get { lock (_gate) return _replay is not null; } }

    /// <summary>True while a local model runs in a <see cref="LiveSim"/> (the one feed a trip can reach).</summary>
    public bool HasLiveSolver { get { lock (_gate) return _live is not null; } }

    /// <summary>The live simulation's resolved power-flow engine, or null with no live model.</summary>
    public string? ActiveSolver { get { lock (_gate) return _activeSolver; } }

    /// <summary>The last tick a live step was asked for: 0 after a load, a replay or a detach.</summary>
    public int Tick { get { lock (_gate) return _tick; } }

    /// <summary>Where the replay cursor is, or null with no replay loaded.</summary>
    public ReplayCursor? ReplayCursor { get { lock (_gate) return _replay is null ? null : CursorLocked(); } }

    /// <summary>
    /// Whether the loop makes frames. A live load sets it (live models animate); a replay load and a detach clear it (a
    /// replay opens paused on frame 0); a replay that reaches its last frame clears it.
    /// </summary>
    public bool Playing
    {
        get { lock (_gate) return _playing; }
        set { lock (_gate) _playing = value; }
    }

    /// <summary>Simulated minutes each live step advances (the STEP control); read when a step starts.</summary>
    public double StepMinutes
    {
        get { lock (_gate) return _stepMinutes; }
        set { lock (_gate) _stepMinutes = value; }
    }

    /// <summary>Playback rate (real-time cadence multiplier): the loop waits <see cref="BasePlaybackMs"/> / rate.</summary>
    public double PlaybackRate
    {
        get { lock (_gate) return _playbackRate; }
        set
        {
            if (!(value > 0)) throw new ArgumentOutOfRangeException(nameof(value), value, "The playback rate must be a positive number.");
            lock (_gate) _playbackRate = value;
        }
    }

    /// <summary>The loop's current wait between frames.</summary>
    public TimeSpan Interval { get { lock (_gate) return TimeSpan.FromMilliseconds(BasePlaybackMs / _playbackRate); } }

    // ======================================== loading ========================================

    /// <summary>
    /// Read a local model source the way the window always did: a MATPOWER file through the unit normaliser (it fixes raw
    /// ohm/kW distribution files), else a JSON case folder; then a tube-map layout for a case without coordinates
    /// (IEEE/MATPOWER). Throws whatever the reader throws.
    /// </summary>
    public static GridModel ReadModel(ModelSource s) => ReadCase(s).Model;

    /// <summary>
    /// <see cref="ReadModel"/> with what the v2 loader carries beside the model (UI programme ASSETS slice 5-view): a
    /// MATPOWER file through the unit normaliser, else a JSON v2 case manifest (<see cref="ModelSource.V2Case"/>) through
    /// <see cref="V2CaseLoader.LoadWithNotes"/> (its asset overlay and notes returned beside the model), else a v1 JSON
    /// case folder; then the tube-map layout for a case without coordinates. Throws whatever the reader throws: a v2 case
    /// the loader refuses (schema, imports, overrides, or an overlay that fails its checker) fails loud, exactly as a v1
    /// folder the v1 loader refuses.
    /// </summary>
    public static (GridModel Model, CaseAssetOverlay? Overlay, IReadOnlyList<string> Notes) ReadCase(ModelSource s)
    {
        ArgumentNullException.ThrowIfNull(s);
        GridModel model;
        CaseAssetOverlay? overlay = null;
        IReadOnlyList<string> notes = Array.Empty<string>();
        if (s.MFile is not null)
            model = MatpowerUnitNormalizer.Normalize(MatpowerParser.ParseFile(s.MFile));   // fix raw ohm/kW distribution files
        else if (s.V2Case is not null)
        {
            V2CaseLoadResult loaded = LoadV2(s.V2Case);
            model = loaded.Model;
            overlay = loaded.Overlay;   // carried beside the model, never handed to the simulation
            notes = loaded.Notes;
        }
        else
            model = JsonGridLoader.LoadDirectory(s.Dir);
        return (GraphLayout.EnsureLayout(model), overlay, notes);   // coordinate-less cases (IEEE/MATPOWER) get a tube-map layout
    }

    /// <summary>
    /// Load a JSON v2 case the way every verb loads one (dispatch on), against the v2 schemas and kind specs THIS build
    /// carries: the copies embedded in GridSim.Core (the files under schemas/v2, pinned byte-equal to them), written to a
    /// fresh temporary folder for the load and removed after it, as the case editor's save gate does. Library imports
    /// resolve against the <c>data</c> folder beside the app when there is one (else a case with imports is refused,
    /// naming the missing data root). Throws whatever the loader throws.
    /// </summary>
    internal static V2CaseLoadResult LoadV2(string manifestPath)
    {
        ArgumentNullException.ThrowIfNull(manifestPath);
        string schemas = Path.Combine(Path.GetTempPath(), "gridsim-v2-schemas-" + Guid.NewGuid().ToString("N"));
        try
        {
            BundledSchemas.WriteV2SchemaFolder(schemas);
            string data = Path.Combine(AppContext.BaseDirectory, "data");
            return V2CaseLoader.LoadWithNotes(manifestPath, schemas, Directory.Exists(data) ? data : null, dispatch: true);
        }
        finally
        {
            try { if (Directory.Exists(schemas)) Directory.Delete(schemas, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a leftover temp folder is harmless */ }
        }
    }

    /// <summary>
    /// True when a model source is a Bundled Network (LICENSE.md section 1), exempt from bus limits: the shipped
    /// reduced/synthetic GB cases and the committed MATPOWER library, all of which sit under a <c>data/</c>
    /// folder. Materialised <c>gda-out/</c> models are licensed user networks.
    /// </summary>
    public static bool IsBundledSource(ModelSource s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return BundledNetworks.IsBundledPath(s.MFile ?? s.V2Case ?? s.Dir);   // R002: the one shared list
    }

    /// <summary>
    /// Load a local model source into a live simulation and start playing. On a refusal nothing changes: whatever ran
    /// before keeps running, as before the move.
    /// <list type="number">
    /// <item><description>Read the model (<see cref="ReadModel"/>); a failure is refused as <c>Load failed: ...</c>.</description></item>
    /// <item><description>Software licence gate (LICENSE.md section 2): bus count is the only licensed quantity. Bundled
    /// Networks are exempt; a licensed user model above the tier's cap is refused with the gate's message rather than
    /// silently loaded.</description></item>
    /// <item><description>MATPOWER/test cases have no GB demand profile: hold their loads steady and keep re-solving
    /// (warm-started) rather than driving the day/night curve; GB JSON/GDA models keep the curve. The engine is
    /// <paramref name="solver"/>; Auto resolves per network (radial->sweep, large->sparse). When the case ships an
    /// authored asset-dynamics or protections bundle, live frequency runs through the asset-aware integrator
    /// (D-DOC-079: the shared Core rule).</description></item>
    /// </list>
    /// </summary>
    public LiveLoadResult LoadLive(ModelSource s, SolverKind solver, LicenceGate licence)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(licence);
        GridModel model;
        CaseAssetOverlay? overlay;
        IReadOnlyList<string> notes;
        // Slice 5-view: a v2 case's overlay and notes come back beside the model; a refusal is "Load failed: ..." as for v1.
        try { (model, overlay, notes) = ReadCase(s); }
        catch (Exception ex) { return new LiveLoadResult(null, null, $"Load failed: {ex.Message}"); }

        GateResult lic = licence.Check(model.BusCount, IsBundledSource(s));
        if (!lic.Allowed) return new LiveLoadResult(null, null, lic.Message);

        var dynCfg = LiveSim.AuthoredBundleConfig(model);
        var sim = new LiveSim(model, holdSteady: s.MFile is not null, solver: solver, dynamicsConfig: dynCfg);
        lock (_gate)
        {
            _live = sim;
            _replay = null;
            _activeSolver = sim.ActiveSolver;
            _tick = 0;
            _epoch++;
            _playing = true;   // live models animate
        }
        return new LiveLoadResult(model, sim.ActiveSolver, null) { Overlay = overlay, Notes = notes };
    }

    /// <summary>
    /// Load a pre-solved run directory and park on frame 0, not playing (the replay is driven by the cursor). A run that
    /// cannot be read is refused as <c>Replay load failed: ...</c>, one with no frames as
    /// <c>Run has no exported frames.</c>; on a refusal nothing changes.
    /// </summary>
    public ReplayLoadResult LoadReplay(string dir)
    {
        ArgumentNullException.ThrowIfNull(dir);
        PresolvedReplay replay;
        try { replay = PresolvedReplay.Load(dir); }
        catch (Exception ex) { return new ReplayLoadResult(null, null, null, null, null, $"Replay load failed: {ex.Message}"); }
        if (replay.Count == 0) return new ReplayLoadResult(null, null, null, null, null, "Run has no exported frames.");

        lock (_gate)
        {
            _replay = replay;
            _live = null;              // no live solver in replay mode
            _activeSolver = null;
            _tick = 0;
            _epoch++;
            _playing = false;          // a replay opens paused on frame 0
            return new ReplayLoadResult(replay.Title, replay.LayoutModel, replay.Current, CursorLocked(),
                replay.Label(replay.Count - 1), null);
        }
    }

    /// <summary>
    /// Drop the local feed (a live bridge stream takes over: the server owns the physics and the pace). Nothing plays and
    /// any frame still on its way is stale.
    /// </summary>
    public void Detach()
    {
        lock (_gate)
        {
            _live = null;
            _replay = null;
            _activeSolver = null;
            _tick = 0;
            _epoch++;
            _playing = false;
        }
    }

    // ======================================== hand-driven moves ========================================

    /// <summary>Inject a loss of infeed (MW) into the live frequency integrator; false when there is no live model.</summary>
    public bool TripInfeed(double lostMw)
    {
        LiveSim? sim;
        lock (_gate) sim = _live;
        if (sim is null) return false;
        sim.TripInfeed(lostMw);   // thread-safe in Core: folded in at the next step
        return true;
    }

    /// <summary>Wake the loop now instead of at the end of its interval (the window's immediate first step after a load).</summary>
    public void RequestStep()
    {
        lock (_gate) _wake.TrySetResult();
    }

    /// <summary>Replay: back to frame 0.</summary>
    public ReplayPosition ReplayRestart() => MoveReplay(r => r.Restart());

    /// <summary>Replay: one frame back.</summary>
    public ReplayPosition ReplayRewind() => MoveReplay(r => r.Rewind());

    /// <summary>Replay: one frame on.</summary>
    public ReplayPosition ReplayAdvance() => MoveReplay(r => r.Advance());

    /// <summary>Replay: jump to <paramref name="index"/> (the replay clamps it to the run).</summary>
    public ReplayPosition ReplaySeek(int index) => MoveReplay(r => r.Seek(index));

    private ReplayPosition MoveReplay(Action<PresolvedReplay> move)
    {
        lock (_gate)
        {
            PresolvedReplay replay = _replay ?? throw new InvalidOperationException("No replay is loaded.");
            int before = replay.Index;
            move(replay);
            _epoch++;   // a frame the loop made before this move no longer describes the cursor
            return new ReplayPosition(replay.Current, CursorLocked(), before);
        }
    }

    private ReplayCursor CursorLocked() =>
        new(_replay!.Index, _replay.Count, _replay.CurrentLabel, _replay.AtEnd);

    // ======================================== the loop ========================================

    /// <summary>
    /// Run the play loop until <paramref name="ct"/> is cancelled: wait one <see cref="Interval"/> (or until
    /// <see cref="RequestStep"/>), make the next frame if playing (<see cref="ProduceNext"/>), and hand it to
    /// <paramref name="deliver"/>, awaiting it before the next frame is made. Cancellation ends the loop normally; an
    /// exception from <paramref name="deliver"/> or from a replay move faults the returned task (fail loud: the front end
    /// observes it). A solve that throws does not end the loop: it is delivered as a
    /// <see cref="HostFrameKind.LiveFaulted"/> frame.
    /// </summary>
    public async Task RunAsync(Func<HostFrame, Task> deliver, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deliver);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await WaitIntervalOrWakeAsync(ct).ConfigureAwait(false);
                HostFrame? frame = ProduceNext();
                if (frame is not null) await deliver(frame).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Asked to stop: the normal way out.
        }
    }

    /// <summary>
    /// Wait one <see cref="Interval"/>, or less when <see cref="RequestStep"/> wakes the loop; throws
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled. The interval timer is cancelled
    /// as soon as the wait ends, so an early wake leaves no timer running.
    /// </summary>
    private async Task WaitIntervalOrWakeAsync(CancellationToken ct)
    {
        Task wake;
        lock (_gate) wake = _wake.Task;
        using (var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            Task delay = Task.Delay(Interval, delayCts.Token);
            await Task.WhenAny(delay, wake).ConfigureAwait(false);
            await delayCts.CancelAsync().ConfigureAwait(false);   // stop the interval timer if the wake came first
        }
        ct.ThrowIfCancellationRequested();
        // A wake is used once: the next wait needs a fresh signal.
        lock (_gate)
            if (_wake.Task.IsCompleted) _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Make the next frame, or null when not playing or nothing is loaded. The old window's <c>StepOnce</c>, step for
    /// step:
    /// <list type="bullet">
    /// <item><description>Replay: advance unless already on the last frame, take the frame at the cursor, and stop
    /// playing once on the last frame (a replay stops at the end).</description></item>
    /// <item><description>Live: ask for the next tick at the current step length, time the base solve (R101), and
    /// evaluate the boundary transfers (D-DOC-087: the analysis refuses a non-converged state; such a frame shows no
    /// boundary transfers). A solve that throws becomes a fault frame instead of ending the loop.</description></item>
    /// </list>
    /// </summary>
    internal HostFrame? ProduceNext()
    {
        LiveSim sim;
        int tick, epoch;
        double step;
        lock (_gate)
        {
            if (!_playing) return null;
            if (_replay is not null)
            {
                if (!_replay.AtEnd) _replay.Advance();
                bool atEnd = _replay.AtEnd;
                if (atEnd) _playing = false;   // stop at the end
                return new HostFrame { Epoch = _epoch, Kind = HostFrameKind.Replay, Frame = _replay.Current, StoppedAtEnd = atEnd };
            }
            if (_live is null) return null;
            sim = _live;
            tick = ++_tick;
            step = _stepMinutes;
            epoch = _epoch;
        }

        // The solve runs outside the lock (it can take a while on a large case); a load meanwhile only makes this frame
        // stale (its epoch is behind), exactly as the window discarded a solve that finished after a model switch.
        try
        {
            // R101: time the base solve so the CYCLE tab reports it (it was always passed 0).
            var solveClock = System.Diagnostics.Stopwatch.StartNew();
            SimFrame f = sim.Step(tick, step);
            double solveMs = solveClock.Elapsed.TotalMilliseconds;
            // D-DOC-087: the analysis refuses a non-converged state; such a frame shows no boundary transfers.
            IReadOnlyList<BoundaryFlow> boundaries = f.Result.Converged ? BoundaryAnalysis.Evaluate(f.Model, f.Result) : Array.Empty<BoundaryFlow>();
            return new HostFrame { Epoch = epoch, Kind = HostFrameKind.LiveSolved, Frame = f, Boundaries = boundaries, SolveMs = solveMs, Tick = tick };
        }
        catch (Exception ex)
        {
            // Before the move the solve ran in a Task and the window read t.Exception.GetBaseException(): an
            // AggregateException wrapping this exception, reduced. Reduce it the same way, so the message is the same.
            return new HostFrame { Epoch = epoch, Kind = HostFrameKind.LiveFaulted, Fault = new AggregateException(ex).GetBaseException(), Tick = tick };
        }
    }
}
