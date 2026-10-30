using GridSim.Core.Abstractions;
using GridSim.Core.Dynamics;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Simulation;

/// <summary>One solved instant of the running grid.</summary>
/// <param name="TapsSettled">N054 (October 2026): whether the solve's on-load tap changer loop finished
/// (<see cref="SteadyStateSolver.TapsSettled"/>): false when a tap ran out of range with its bus still out of band, when
/// the loop hit its pass cap with taps still moving, or when a solve did not converge. True by default, so every frame
/// built without an OLTC loop (a replay, a single-shot solve) reads as before.</param>
/// <param name="TapsAtLimit">N054: the transformers whose tap is at the end of its range with the controlled bus still
/// outside its deadband (<see cref="SteadyStateSolver.TapsAtLimit"/>); null (none) by default.</param>
/// <param name="TapsRegulated">GR-352 (October 2026): whether this frame's solve ran the OLTC loop at all. LiveSim runs
/// no tap passes on the sparse (large case) and sweep (radial) engines, to keep each tick fast; such a frame carries
/// <c>TapsRegulated = false</c> and <see cref="TapsSettled"/> true when the solve converged (no regulation was
/// attempted, so none was left unfinished), where it used to read <c>TapsSettled = false</c> on every frame as if a tap had run out of range. True
/// by default.</param>
public sealed record SimFrame(
    GridModel Model, PowerFlowResult Result, double Hour, int Tick,
    double FrequencyHz, double GenerationMw, double DemandMw, double RocofHzPerS = 0,
    IReadOnlyList<Assets.AssetClassRollup>? AssetFleet = null,
    double AssetFleetNetMw = 0, double AssetTrippedMw = 0,
    IReadOnlyList<Storage.SiteStepRecord>? Storage = null,
    bool TapsSettled = true, IReadOnlyList<string>? TapsAtLimit = null, bool TapsRegulated = true);

/// <summary>
/// Drives the GB model through a day: each tick advances a clock, scales demand to
/// the day/night curve, re-dispatches and re-solves to a regulated steady state.
/// Frequency is modelled as a small mean-reverting wander about the model's nominal (50 Hz for a GB case)  -  the way
/// the real system frequency constantly drifts as supply and demand chase each other. The swing integrator is driven
/// by a random ambient imbalance each tick plus any unit-trip deficit (which secondary restoration decays); the
/// tick-to-tick change in demand does not drive it (re-dispatch meets demand on every tick).
///
/// <para>N273 (October 2026 review, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04): the swing runs at the
/// model's own nominal frequency (<see cref="GridModel.FrequencyHz"/>), and the bands the shells grade the frequency
/// against are <see cref="Bands"/>, the GB bands about that nominal (<see cref="FrequencyBands.ForNominal"/>). Before, the
/// live loop ran every case at the GB 50 Hz, so the WSCC 9-bus case (60 Hz since D2) was integrated at the wrong speed
/// and shown against 50 Hz bands. Every 50 Hz case runs exactly as before.</para>
///
/// Lives in Core (uses only Core types) so it can be unit-tested without the WPF app.
/// </summary>
public sealed class LiveSim
{
    private readonly GridModel _base;
    private readonly SteadyStateSolver _solver;
    private readonly bool _big;
    private readonly bool _holdSteady;
    private readonly PowerFlowOptions? _opt;
    private readonly int _oltcPasses;
    private readonly Random _rng = new(LiveSolveSettings.LiveSimSeed);

    private double _minutes = LiveSolveSettings.StartMinuteOfDay;   // start at 08:00
    private PowerFlowResult? _lastResult;   // warm-start seed for the hold-steady path

    // Frequency is a real swing-equation integrator advanced at the step's dt (see FrequencyIntegrator),
    // so at fine steps you watch RoCoF/nadir/recovery, and a trip is a live loss of infeed rather than
    // a canned trace. Ambient load noise keeps the dial alive; a trip deficit recovers via slow redispatch.
    private readonly IFrequencyIntegrator _freqSim;
    private readonly double _baseDemandMw;
    private readonly double _ambientMw;         // stdev-ish of the ambient load-imbalance noise, MW
    private double _tripDeficitMw;              // active loss of infeed (MW, negative), decays as reserve picks up
    private readonly object _tripGate = new();  // guards cross-thread trip injection vs the background Step read-modify-write
    private double _pendingTripMw;              // trips injected from another thread (UI), folded into _tripDeficitMw in Step
    private const double SecondaryTauS = 300;  // ~5 min redispatch/secondary restoration time constant

    // Recloser topology: branches currently held open by protections, and the net load lost to blackout
    // (applied to frequency once, on the tick it changes, not every tick).
    private IReadOnlySet<string> _openBranches = System.Collections.Immutable.ImmutableHashSet<string>.Empty;
    private double _lastBlackoutNetMw;

    /// <param name="holdSteady">
    /// When <c>true</c> the sim holds its own loads constant (no GB day/night curve) and keeps
    /// re-solving the same operating point, warm-started from the previous result. Intended for
    /// IEEE/MATPOWER test cases that have no GB demand profile. Default <c>false</c> keeps the
    /// original GB daily-curve behaviour unchanged.
    /// </param>
    /// <param name="solver">
    /// Which power-flow engine to run. <see cref="SolverKind.Auto"/> (default) picks per network
    /// shape via <see cref="SolverSelector"/>: radial distribution feeders -> backward/forward sweep,
    /// large networks -> sparse Newton-Raphson, everything else -> dense Newton-Raphson.
    /// </param>
    /// <param name="dynamicsConfig">
    /// Optional frequency-dynamics settings. When it opts into asset dynamics
    /// (<see cref="DynamicsConfig.EnableAssetDynamics"/>) AND the model carries an authored bundle, the
    /// live frequency runs through the asset-aware integrator (inverter FFR + Loss-of-Mains trips);
    /// otherwise the existing <see cref="FrequencyIntegrator"/> path is used unchanged. When it opts into passive
    /// plant (<see cref="DynamicsConfig.EnablePassivePlant"/>) the bound transformer OLTC and line/transformer
    /// thermal classes run per tick (see <see cref="Assets.PassivePlantFleet"/>).
    /// </param>
    /// <param name="storage">Optional battery sites carried tick to tick (SoC, temperature, ageing): each tick
    /// their policy is asked for a power, the chemistry delivers what it can, and the result is injected before
    /// the solve. Null leaves the loop exactly as before.</param>
    public LiveSim(GridModel baseModel, bool holdSteady = false, SolverKind solver = SolverKind.Auto,
        DynamicsConfig? dynamicsConfig = null, Storage.StorageFleet? storage = null)
    {
        _base = baseModel ?? throw new ArgumentNullException(nameof(baseModel));
        // N273 (stream B2): the swing runs at the model's nominal. No config gives the defaults at the model's nominal (for a
        // 50 Hz model exactly the default config the integrators built from a null before, so every 50 Hz run is
        // bit-identical); a config at another nominal is refused, naming both (the N275 rule every model-based frequency
        // entry point follows). The shells build theirs with AuthoredBundleConfig, which carries the model's nominal.
        dynamicsConfig = SwingKernel.ConfigForModel(baseModel, dynamicsConfig, nameof(LiveSim));
        NominalHz = dynamicsConfig.NominalHz;
        Bands = FrequencyBands.ForNominal(NominalHz);
        _storage = storage;
        storage?.Validate(baseModel);
        _holdSteady = holdSteady;
        // Resolve the engine (live sparse threshold is lower than the CLI's memory ceiling: past
        // ~1500 buses a dense per-tick solve gets too slow for a smooth animation).
        (IPowerFlowSolver pf, SolverKind resolved, ActiveSolver) =
            SolverSelector.Create(solver, baseModel, sparseThreshold: LiveSolveSettings.SparseThresholdBuses);
        _solver = new SteadyStateSolver(pf);
        // Sparse (large GDA networks): single NR pass, no Q-limit switching, skip the heavy OLTC
        // loop  -  keeps each live solve to ~a second. Radial sweep needs no OLTC loop either.
        // Small dense meshed model: the full steady-state with OLTC regulation.
        _big = resolved == SolverKind.SparseNewton;
        _opt = _big ? new PowerFlowOptions { MaxIterations = LiveSolveSettings.SparseMaxIterations, EnforceQLimits = false } : null;
        _oltcPasses = _big || resolved == SolverKind.DistributionSweep ? 0 : LiveSolveSettings.OltcPasses;

        // Frequency dynamics: spinning energy from the machines' inertia (fall back to a nominal 4 s
        // on the fleet's MVA when a case carries no H data), base demand for damping/response scaling.
        _baseDemandMw = Math.Max(1.0, baseModel.Buses.Sum(b => b.Pd));
        double inertiaMwS = baseModel.SystemInertiaGvaS * 1000.0;
        // The sub-100-MW.s substitution fabricates REAL inertia; suppress it under grid-forming, where a
        // low/zero real-inertia system is the whole point and the frequency is FORMED (E_real + Ev), not floored.
        if (dynamicsConfig is not { Forming: not FormingMode.None })
        {
            if (inertiaMwS < LiveSolveSettings.MinInertiaMwS)
                inertiaMwS = LiveSolveSettings.FallbackInertiaH * baseModel.Generators.Sum(g => g.MBase > 0 ? g.MBase : Math.Abs(g.Pg));
            if (inertiaMwS < LiveSolveSettings.MinInertiaMwS) inertiaMwS = LiveSolveSettings.FallbackInertiaH * _baseDemandMw;
        }
        if (dynamicsConfig is { EnableAssetDynamics: true } && HasAuthoredBundle(baseModel))
        {
            var fleet = Assets.AssetDynamicsFleet.Build(baseModel, dynamicsConfig.NominalHz);
            // R044 (October 2026, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04): the fleet's trips and its
            // protection sheds are restored by secondary redispatch over the same SecondaryTauS as a manual trip. Before,
            // only _tripDeficitMw decayed: an asset trip stayed in the swing for good (the fleet's cumulative deficit is
            // subtracted every sub-step and the swing has no integral term) and an LFDD shed stayed applied, so after one
            // Loss-of-Mains cascade the frequency sat off nominal on governor droop for the rest of the session, against
            // this class's own promise that a trip deficit recovers via slow redispatch. The tripped units stay tripped
            // (AssetTrippedMw still reports them); what recovers is the balance, as the rest of the system is redispatched.
            var aware = new AssetAwareFrequencyIntegrator(inertiaMwS, _baseDemandMw, fleet, dynamicsConfig,
                secondaryRestorationTauS: SecondaryTauS);
            _freqSim = aware;
            _assetFleet = fleet;
        }
        else
        {
            _freqSim = new FrequencyIntegrator(inertiaMwS, _baseDemandMw, dynamicsConfig);
        }
        _ambientMw = LiveSolveSettings.AmbientNoiseFraction * _baseDemandMw;   // gentle load-imbalance jitter so the dial breathes

        // Passive plant (R058): opt-in. An empty fleet (nothing bound) is kept as null so the loop takes exactly
        // the same path as with the flag off - the reduction gate, bit-identical by construction.
        if (dynamicsConfig is { EnablePassivePlant: true })
        {
            Assets.PassivePlantFleet plant = Assets.PassivePlantFleet.Build(baseModel);
            _passivePlant = plant.IsEmpty ? null : plant;
        }
    }

    // The bound passive plant (delayed OLTC taps, thermal derate) when opted in and non-empty, else null.
    private readonly Assets.PassivePlantFleet? _passivePlant;

    // The passive-plant updates computed after the previous converged solve, applied to the next tick's model
    // before its solve. Null until the first converged solve has been observed.
    private IReadOnlyList<Assets.BranchPlantState>? _passiveState;

    /// <summary>The live passive-plant fleet (opted in and non-empty), or null.</summary>
    public Assets.PassivePlantFleet? PassivePlant => _passivePlant;

    /// <summary>The passive-plant updates that will be applied before the next solve (null before the first observation).</summary>
    public IReadOnlyList<Assets.BranchPlantState>? PassivePlantState => _passiveState;

    private readonly Assets.AssetDynamicsFleet? _assetFleet;
    private readonly Storage.StorageFleet? _storage;

    /// <summary>The storage sites carried through the loop, or null.</summary>
    public Storage.StorageFleet? StorageFleet => _storage;

    /// <summary>The live authored-asset fleet (per-class rollups for the dashboard), or null when off.</summary>
    public Assets.AssetDynamicsFleet? AssetFleet => _assetFleet;

    /// <summary>R044 (stream B2): the MW secondary restoration has put back against the asset fleet's standing deficit
    /// (trips net of held protection sheds); 0 when the asset fleet is off or nothing has tripped or been shed.</summary>
    public double AssetRestoredMw => _freqSim is AssetAwareFrequencyIntegrator a ? a.RestoredMw : 0.0;

    /// <summary>Per-bus solved voltage (p.u.), for U/O-voltage protections; buses absent from the result default to 1.0.</summary>
    private static Func<int, double> BuildVoltageOf(PowerFlowResult r)
    {
        var vm = new Dictionary<int, double>(r.BusIds.Count);
        for (int i = 0; i < r.BusIds.Count; i++) vm[r.BusIds[i]] = i < r.Vm.Length ? r.Vm[i] : 1.0;
        return id => vm.TryGetValue(id, out double v) ? v : 1.0;
    }

    /// <summary>Per-bus max incident-branch loading fraction, for thermal-overload protections (0 if no incident branch).</summary>
    private static Func<int, double> BuildLoadingOf(PowerFlowResult r, GridModel solved)
    {
        var load = new Dictionary<int, double>();
        int n = Math.Min(r.LineFlows.Count, solved.Branches.Count);
        for (int i = 0; i < n; i++)
        {
            double frac = r.LineFlows[i].LoadingPercent / 100.0;
            Branch b = solved.Branches[i];
            if (frac > load.GetValueOrDefault(b.From)) load[b.From] = frac;
            if (frac > load.GetValueOrDefault(b.To)) load[b.To] = frac;
        }
        return id => load.GetValueOrDefault(id, 0.0);
    }

    /// <summary>
    /// True when the model carries an authored bundle the asset-aware integrator runs: an asset-dynamics bundle
    /// (<c>asset-classes.json</c> + bindings) OR a protections bundle (<c>protections.json</c>). Either one alone is
    /// enough; the fleet builds from whichever is present (<see cref="Assets.AssetDynamicsFleet.Build"/>).
    /// </summary>
    public static bool HasAuthoredBundle(GridModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.AssetDynamics is not null || model.Protections is not null;
    }

    /// <summary>
    /// The dynamics settings a live shell (desktop, web session, cycle runner) passes to <see cref="LiveSim"/> for a
    /// case: asset dynamics switched ON when the case carries an authored bundle (<see cref="HasAuthoredBundle"/>),
    /// otherwise null (the plain integrator, unchanged).
    /// <para>D-DOC-079: the three shells each tested <c>model.AssetDynamics is not null</c> only, so a case with a
    /// <c>protections.json</c> and no asset-dynamics bundle ran with its protections idle there (no LFDD, no recloser, no
    /// protection trip), while <c>trip</c> and <see cref="LiveSim"/> itself arm on either bundle. They now share this
    /// one rule. No bundled case is affected: every bundled case with a protections bundle (gb-synthetic) also carries
    /// an asset-dynamics bundle, so its configuration and every number it produces are unchanged.</para>
    /// </summary>
    /// <remarks>N273 (stream B2): the config carries the model's nominal frequency (<see cref="DynamicsConfig.NominalHz"/>),
    /// which LiveSim requires; for a 50 Hz case it is the same 50.0 as before.</remarks>
    public static DynamicsConfig? AuthoredBundleConfig(GridModel model) =>
        HasAuthoredBundle(model) ? new DynamicsConfig { EnableAssetDynamics = true, NominalHz = model.FrequencyHz } : null;

    /// <summary>N273 (stream B2): the nominal frequency (Hz) the live swing runs at: the model's own.</summary>
    public double NominalHz { get; }

    /// <summary>N273 (stream B2): the frequency bands the shells grade this case's frequency against: the GB bands about
    /// <see cref="NominalHz"/> (<see cref="FrequencyBands.Gb"/> itself for a 50 Hz case).</summary>
    public FrequencyBands Bands { get; }

    /// <summary>Latest integrated rate-of-change-of-frequency, Hz/s (for the dashboard/RoCoF display).</summary>
    public double RocofHzPerS => _freqSim.RocofHzPerS;

    /// <summary>
    /// Inject a loss of infeed of <paramref name="lostMw"/> MW (e.g. a generator trip). The live
    /// frequency integrator then plays out the RoCoF/nadir at the current time-step; the deficit
    /// recovers over ~5 minutes as reserve/redispatch picks up. Accumulates across trips.
    /// </summary>
    // Called from the UI thread (right-click a bus / T key) while Step() may be read-modify-writing
    // _tripDeficitMw on a thread-pool thread. Accumulate into a locked pending slot; Step folds it in
    // atomically, so a raced trip is never silently lost.
    // GR-736: the trip keeps its sign. A positive value is lost infeed (a generator at +Pg tripping: a deficit); a negative
    // one is lost absorption (a unit at negative Pg, a pumping or charging plant, tripping: a surplus). Math.Abs used to
    // turn the second into a deficit, the wrong way. A non-finite value is refused (a NaN poisoned the deficit for good).
    public void TripInfeed(double lostMw)
    {
        if (!double.IsFinite(lostMw))
            throw new ArgumentOutOfRangeException(nameof(lostMw), lostMw, "A trip must be a finite number of MW.");
        lock (_tripGate) _pendingTripMw -= lostMw;
    }

    /// <summary>The engine actually running (Auto resolved to a concrete solver), for the UI.</summary>
    public string ActiveSolver { get; }

    public int OltcPasses => _solver.OltcPasses;

    /// <summary>Advance one tick. GUI/back-compat entry point (no math capture).</summary>
    public SimFrame Step(int tick, double stepMinutes = LiveSolveSettings.DefaultStepMinutes) => Step(tick, stepMinutes, null);

    /// <summary>
    /// Advance one tick, optionally capturing the full solver math of this step into
    /// <paramref name="trace"/> (attach it, then serialise with <see cref="IO.SolveExport"/>).
    /// When <paramref name="trace"/> is null this is identical to the two-arg overload - zero overhead.
    /// Used by the headless <c>transient</c> command to export the exact math on sampled slices.
    /// </summary>
    public SimFrame Step(int tick, double stepMinutes, Diagnostics.SolveTrace? trace)
    {
        // N291 (October 2026, stream B2): refuse a step that is not a finite number of minutes above zero, before any
        // state moves. A NaN step used to reach the frequency integrator (whose guard let NaN through) and turn the live
        // frequency NaN for the rest of the session; a zero or negative step re-solved the network, moved the storage and
        // passive plant by a zero / negative interval and froze the frequency without a word. Every shell passes a
        // positive step (the web transport ignores a non-positive pick; WPF and the CLI use fixed positive steps).
        if (!double.IsFinite(stepMinutes) || !(stepMinutes > 0.0))
            throw new ArgumentOutOfRangeException(nameof(stepMinutes), stepMinutes,
                $"LiveSim.Step: the step must be a finite number of minutes above zero (got {stepMinutes:R}).");

        double hour = (_minutes / 60.0) % 24.0;
        // Hold-steady mode (IEEE/MATPOWER cases with no GB demand profile): keep the base
        // model's own loads constant and re-solve the same operating point, warm-started from
        // the previous result so the re-solve converges in very few iterations. Otherwise the
        // default GB path scales demand to the day/night curve and re-dispatches.
        GridModel m;
        if (_holdSteady)
        {
            m = _lastResult is null ? _base : GridScenario.WithSolvedVoltages(_base, _lastResult);
        }
        else
        {
            double factor = LoadProfile.DailyFactor(hour);
            m = GridScenario.AtLoad(_base, factor);
        }
        // R043 (October 2026, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04): the authored fleet was built once
        // from the base model, so a Loss-of-Mains trip booked the BASE dispatch while this tick runs the load-scaled one
        // (at 04:00, factor 0.6, a trip removed 1.67 times what the unit was producing, and AssetTrippedMw showed the same
        // overstatement). Every bound unit now books the output its generator is dispatched to on this tick. Before any
        // trip nothing reads it, and on the hold-steady path it is the base dispatch every tick, so only a trip on the
        // load-scaled path books a different (the right) number.
        _assetFleet?.SetDispatch(m);
        // Storage: advance each site over this tick and inject what its chemistry delivered.
        IReadOnlyList<Storage.SiteStepRecord>? storageRecords = null;
        if (_storage is not null)
        {
            storageRecords = _storage.Advance(tick, hour, stepMinutes / 60.0, m.Buses.Sum(b => b.Pd), double.NaN);
            m = _storage.Apply(m);
        }

        // Passive plant (R058): hand tap regulation of the governed transformers to the delayed passive OLTCs (so
        // the solver's instantaneous OLTC loop does not also step them), then apply the taps and derated ratings
        // computed from the previous converged solve. Applied BEFORE the solve so this tick solves on them.
        if (_passivePlant is not null)
        {
            m = _passivePlant.WithoutSupersededOltcs(m);
            if (_passiveState is not null) m = Assets.PassivePlantFleet.ApplyTo(m, _passiveState);
        }

        // Recloser topology: if protections hold any branch open, apply it (out of service) and partition
        // for islanding/blackout before solving. The net load lost to a blackout becomes a one-off frequency
        // perturbation (load lost = surplus = frequency up). Empty set = no work (fully-connected solve).
        // Fold in any trips injected from another thread since the last step, atomically.
        lock (_tripGate) { _tripDeficitMw += _pendingTripMw; _pendingTripMw = 0; }

        if (_openBranches.Count > 0)
        {
            (GridModel adjusted, PowerFlow.IslandReport isl) = PowerFlow.TopologyOps.Apply(m, _openBranches);
            m = adjusted;
            double netBlackout = isl.LostLoadMw - isl.LostGenMw;   // +ve = net load lost (surplus)
            _tripDeficitMw += netBlackout - _lastBlackoutNetMw;    // apply only the change
            _lastBlackoutNetMw = netBlackout;
        }
        else if (_lastBlackoutNetMw != 0.0)
        {
            // Everything reclosed: the blacked-out load (net of any generation in the dead island) comes back, which is
            // a NEW step of -netBlackout MW, applied in full. D-DOC-080 (docs-session finding, ruled 2026-10-05 by
            // best judgement under Mark's delegation of EE calls; no code or number changed): the finding read this as
            // "undo the blackout" and called the result a residual of the wrong sign, because the +netBlackout added at
            // the outage has meanwhile decayed by secondary restoration while the full value is subtracted here. That
            // is the intended physics, not an arithmetic slip. _tripDeficitMw is a superposition of step imbalances,
            // each decaying with SecondaryTauS: the decay of the outage step IS the remaining generation being
            // redispatched down to match the lost load, so when the load returns that generation is short by the
            // decayed amount, B * (1 - exp(-t / SecondaryTauS)), and frequency dips (cold load pickup), then recovers
            // as the new step decays. Subtracting only the undecayed remainder would make a long outage's reconnection
            // free, which no real system offers.
            _tripDeficitMw -= _lastBlackoutNetMw;
            _lastBlackoutNetMw = 0.0;
        }

        // Materialise a default options object only when a trace is requested (_opt is null for small
        // systems), so per-step math capture works without changing the no-trace fast path.
        PowerFlowOptions? opt = trace is null ? _opt : (_opt ?? new PowerFlowOptions()) with { Trace = trace };
        PowerFlowResult r = _solver.Solve(m, opt, _oltcPasses);
        // Only a CONVERGED state may seed the next solve (R290): a non-converged iterate (or NaN) as a warm start
        // compounds the failure. Keep the last good seed instead.
        if (_holdSteady && r.Converged) _lastResult = r;
        // The OLTC loop settles taps on internal clones; take the regulated model so the
        // frame carries the solved tap ratios (the dashboard + SCADA read them from here).
        GridModel solved = _solver.RegulatedModel ?? m;

        double demand = m.Buses.Sum(b => b.Pd);
        double gen = r.PInjMw.Where(p => p > 0).Sum();

        // Feed the solved per-bus voltage + per-bus loading into the asset-aware integrator so U/O-voltage
        // and thermal-overload protections observe real network state (held across the fast sub-steps). Only a
        // CONVERGED solve is network state: the iterate a failed solve stopped at (Vm around 0.6 pu, say) used to trip
        // embedded generation and open reclosers, a cascade that was an artefact of the failed solve. On a
        // non-converged tick the protections keep the last converged state, as the warm start and passive plant do.
        if (_freqSim is AssetAwareFrequencyIntegrator awareNet && r.Converged)
            awareNet.SetNetworkState(BuildVoltageOf(r), BuildLoadingOf(r, solved));

        // Advance the swing-equation frequency by this step's dt. The net imbalance is ambient load
        // jitter plus any active trip deficit (which decays as reserve picks it up). At fine dt this
        // shows real RoCoF/recovery; at coarse dt it just settles.
        double dtSeconds = stepMinutes * 60.0;

        // Passive plant (R058): advance every controller by this tick against the state just solved, and keep the
        // updates for the next tick. Only a CONVERGED solve is an observation (R039); otherwise the previous
        // updates are held unchanged.
        if (_passivePlant is not null && r.Converged)
            _passiveState = _passivePlant.Advance(dtSeconds, _minutes * 60.0, r, solved);

        _tripDeficitMw *=DetMath.Exp(-dtSeconds / SecondaryTauS);        // secondary restoration
        if (Math.Abs(_tripDeficitMw) < LiveSolveSettings.TripDeficitZeroMw) _tripDeficitMw = 0;
        double ambient = (_rng.NextDouble() - 0.5) * 2.0 * _ambientMw;
        _freqSim.Step(dtSeconds, ambient + _tripDeficitMw);
        double freq = _freqSim.FrequencyHz;

        _minutes += stepMinutes;

        // Surface the authored-asset fleet state to the dashboard when it is running.
        IReadOnlyList<Assets.AssetClassRollup>? rollups = null;
        double fleetNetMw = 0, trippedMw = 0;
        if (_freqSim is AssetAwareFrequencyIntegrator aware)
        {
            rollups = aware.Fleet.Rollups();
            fleetNetMw = aware.NetFleetMw;
            trippedMw = aware.TrippedDeficitMw;
            _openBranches = aware.Fleet.OpenBranches();   // recloser state for the next tick's topology solve
        }
        // N054: carry the tap loop's outcome to the shells (the CLI live dashboard, WPF, web), which used to show a
        // tap that ran out of range as if it were regulating. Reporting only: nothing above reads these.
        return new SimFrame(solved, r, hour, tick, freq, gen, demand, _freqSim.RocofHzPerS,
            rollups, fleetNetMw, trippedMw, storageRecords,
            // GR-352: with no tap passes (sparse and sweep engines) the loop never ran, so the solver's TapsSettled is
            // its reset value (false), not a verdict; report "not regulated" instead of "not settled" (still false on a
            // non-converged solve, as for a regulated one).
            TapsSettled: _oltcPasses == 0 ? r.Converged : _solver.TapsSettled,
            TapsAtLimit: _oltcPasses == 0 ? null : _solver.TapsAtLimit,
            TapsRegulated: _oltcPasses > 0 || m.Oltcs.Count == 0);   // a case with no OLTC has nothing to regulate
    }
}
