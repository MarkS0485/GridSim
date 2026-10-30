using System.Threading;
using GridSim.Core.Abstractions;
using GridSim.Core.Dispatch;
using GridSim.Core.Model;

namespace GridSim.Core.PowerFlow;

/// <summary>
/// Solves the network to a regulated steady state: a Newton-Raphson power flow with
/// an outer on-load-tap-changer loop. After each solve it nudges every OLTC's tap
/// one step toward its target (raising a controlled bus on the untapped To end by lowering
/// the tap, or one on the tapped From end by raising it, and vice-versa), then re-solves, until the taps settle inside their deadbands  -  the
/// digital echo of Grid Supply Point transformers holding distribution voltage as
/// load shifts. With no OLTCs it's just the plain power flow.
/// <para>
/// <b>Which transformer, and which OLTCs regulate (N046, October 2026).</b> An <see cref="Oltc"/> record names its
/// transformer and its controlled bus. The record now drives the first branch carrying that name AND having the
/// controlled bus at one of its ends (<see cref="MapOltcRecordsToBranches"/>); only when no same-named branch touches
/// the controlled bus (a remote controlled bus) does it fall back to the first branch of that name, the historical
/// rule. Before N046 the match was by name alone, so with names shared between transformers on different buses
/// (gb-full has 58 such names) a record could step a transformer it does not belong to. An OLTC whose controlled
/// bus is generator-regulated (PV or slack in the model) HOLDS its tap, as the passive plant does
/// (<see cref="Assets.PassivePlantFleet"/>): the machine's AVR fixes that voltage, so no tap position can move it,
/// and regulating it anyway ran the tap one step per pass to its limit. On gb-full 71 records aim at PV buses held
/// at 1.02 pu against a 1.0 +/- 0.01 pu target, and every one of those taps was driven from 1.0 to
/// 1.0999999999999996 (see <see cref="TapsHeldAtGeneratorBus"/>).
/// </para>
/// <para>
/// <b>Generator-regulated as SOLVED (N071, October 2026).</b> The hold is decided from the bus types each converged
/// solve ended with (<see cref="PowerFlowResult.SolvedBusTypes"/>), not only the model's stored types: a PV bus whose
/// units the reactive-limit loop pinned at Qmin / Qmax is a PQ bus in that solve (no AVR holds it), so its tap changer
/// regulates it. Before N071 such a tap stayed held while the bus sat outside its band, and the loop still reported the
/// taps settled. The model's types still decide before the first solve and whenever a solve did not converge.
/// </para>
/// <para>
/// <b>Not thread-safe.</b> A single instance keeps mutable per-solve state
/// (<see cref="RegulatedModel"/>, <see cref="OltcPasses"/>), so it must not be shared across threads.
/// Use one instance per thread for parallel N-1 sweeps. Concurrent <see cref="Solve"/> calls on the
/// same instance are detected and rejected with an <see cref="InvalidOperationException"/>.
/// </para>
/// </summary>
public sealed class SteadyStateSolver
{
    /// <summary>Default cap on OLTC outer-loop passes (R192). The web REST API's own pass cap
    /// (<c>RestApi.RestOltcPassCap</c>) is pinned to this value by a test.</summary>
    public const int DefaultMaxOltcPasses = 25;

    /// <summary>A tap move smaller than this (pu ratio) is no move: the OLTC loop has settled (R192).</summary>
    internal const double TapChangeEpsilon = 1e-12;

    /// <summary>The engine supplied by the caller, or null to pick one per model with
    /// <see cref="SolverSelector.CreateNewton"/> at each <see cref="Solve"/>.</summary>
    private readonly IPowerFlowSolver? _nr;

    /// <summary>Re-entrancy guard (0 = idle, 1 = a Solve is in progress). Not a lock: concurrent use is
    /// a caller bug, so we detect and throw rather than serialise. See the class-level thread-safety note.</summary>
    private int _solving;

    /// <summary>
    /// With a linear solver, the dense Newton engine on that backend (the CLI's GPU dense path; the CPU
    /// <see cref="DenseLuSolver"/> is the canonical step). With none, the engine is chosen PER MODEL at each
    /// <see cref="Solve"/> by the shared rule (<see cref="SolverSelector.CreateNewton"/>: always sparse since N057; it was sparse past 200 buses):
    /// this constructor used to build the dense engine for every model, so REST /solve and the viz export ran
    /// dense on gb-full (R109, October 2026). The two engines are bit-identical on the CPU, so this changes
    /// speed and memory only.
    /// </summary>
    public SteadyStateSolver(ILinearSolver? linearSolver = null)
        => _nr = linearSolver is null ? null : new NewtonRaphsonPowerFlow(linearSolver);

    /// <summary>Drive the OLTC loop with a specific power-flow backend (e.g. the sparse solver for large networks).</summary>
    public SteadyStateSolver(IPowerFlowSolver powerFlow)
        => _nr = powerFlow;

    /// <summary>Number of OLTC re-solve passes taken by the last <see cref="Solve"/>. Mutable per-solve
    /// state: see the class-level thread-safety note (one instance per thread).</summary>
    public int OltcPasses { get; private set; }

    /// <summary>How many OLTC records of the last <see cref="Solve"/> held their tap because their controlled bus is
    /// generator-regulated (PV or slack), N046: on the last converged pass the loop judged, by the bus types that solve
    /// ended with (N071); by the model's types when no solve converged. Counted only for records that map to a transformer.
    /// Mutable per-solve state: see the class-level thread-safety note (one instance per thread).</summary>
    public int TapsHeldAtGeneratorBus { get; private set; }

    /// <summary>
    /// Whether the last <see cref="Solve"/> left every OLTC settled: true when a pass moved no tap (or the model
    /// has no OLTCs); false when the loop stopped because a solve did not converge (taps are never stepped on a
    /// non-converged or NaN state) or because the pass cap was reached with taps still moving (R039, October
    /// 2026: both used to be silent). The returned result's own <see cref="PowerFlowResult.Converged"/> still
    /// says whether its power flow converged; this says whether the tap regulation finished.
    /// <para>Also false when a pass moved no tap only because a tap is PINNED at its <see cref="Oltc.MinTap"/> or
    /// <see cref="Oltc.MaxTap"/> while its controlled bus is still outside the deadband (October 2026 docs-programme
    /// finding: that used to count as settled, so a bus left out of band by an exhausted tap range was reported as
    /// regulated). Those transformers are named in <see cref="TapsAtLimit"/>, which tells "ran out of range" apart
    /// from "stopped on a bad solve" or "hit the pass cap".</para>
    /// </summary>
    public bool TapsSettled { get; private set; }

    /// <summary>
    /// The transformers (by <see cref="Oltc.Transformer"/> name, in model order) whose tap the last <see cref="Solve"/>
    /// left at <see cref="Oltc.MinTap"/> or <see cref="Oltc.MaxTap"/> with the controlled bus still outside
    /// <c>TargetVpu +/- DeadbandPu</c>, on the final converged pass where no tap could move. Empty when every OLTC
    /// reached its band, when the model has no OLTCs, and when the loop stopped for another reason (a non-converged
    /// solve or the pass cap; see <see cref="TapsSettled"/>). Non-empty always means <see cref="TapsSettled"/> is false.
    /// <para>Mutable per-solve state: see the class-level thread-safety note (one instance per thread).</para>
    /// </summary>
    public IReadOnlyList<string> TapsAtLimit { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// The model actually solved, after the OLTC loop has settled the tap ratios  -  i.e. the
    /// one whose <see cref="Branch.TapRatio"/>s match the returned result. With no OLTCs this
    /// is the input model unchanged. Callers that need the regulated taps (the live dashboard,
    /// SCADA) must read this rather than the model they passed in, because the loop solves on
    /// internal clones.
    /// <para>Mutable per-solve state: see the class-level thread-safety note (one instance per thread).</para>
    /// </summary>
    public GridModel RegulatedModel { get; private set; } = default!;

    /// <summary>
    /// Opt-in ramp start (Tranche A, PSS/E tap control). 0, the default: off, and every solve is exactly what it always
    /// was. N above 0: when a model with OLTCs does not converge from its own taps, <see cref="Solve"/> solves it at 1/N,
    /// 2/N, ... of its demand and generation first, letting the taps settle at each stage and carrying them forward (see
    /// <see cref="RampStart"/>). A model that converges from its own taps, or has no OLTCs, never ramps. At most
    /// <see cref="MaxLoadRampStages"/>.
    /// </summary>
    public int LoadRampStages
    {
        get => _loadRampStages;
        set
        {
            if (value < 0 || value > MaxLoadRampStages)
                throw new ArgumentOutOfRangeException(nameof(LoadRampStages), value, $"the ramp start takes 0 (off) to {MaxLoadRampStages} stages");
            _loadRampStages = value;
        }
    }

    private int _loadRampStages;

    /// <summary>The most ramp-start stages <see cref="LoadRampStages"/> takes.</summary>
    public const int MaxLoadRampStages = 1000;

    /// <summary>How many ramp-start stages the last <see cref="Solve"/> solved (the last one counted even when it did not
    /// converge); 0 when it did not ramp. Mutable per-solve state, like the rest.</summary>
    public int LoadRampStagesRun { get; private set; }

    /// <summary>The demand fraction of the ramp-start stage that did not converge in the last <see cref="Solve"/> (1 for
    /// the full-load stage); null when it did not ramp or every stage converged.</summary>
    public double? LoadRampFailedAt { get; private set; }

    public PowerFlowResult Solve(GridModel model, PowerFlowOptions? options = null, int maxOltcPasses = DefaultMaxOltcPasses)
    {
        // Guard the mutable per-solve state against concurrent use of a shared instance. This is a
        // caller bug (the class is documented single-instance-per-thread), so throw rather than serialise.
        if (Interlocked.CompareExchange(ref _solving, 1, 0) != 0)
            throw new InvalidOperationException(
                "SteadyStateSolver is not thread-safe: a Solve call is already in progress on this instance. " +
                "Use one instance per thread for parallel N-1 sweeps.");
        try
        {
            OltcPasses = 0;
            TapsSettled = false;
            TapsHeldAtGeneratorBus = 0;
            TapsAtLimit = Array.Empty<string>();
            LoadRampStagesRun = 0;
            LoadRampFailedAt = null;
            // The engine for this solve: the caller's, or the shared rule's pick for this model (the OLTC passes
            // change taps, never the bus count, so one pick serves the whole loop).
            IPowerFlowSolver nr = _nr ?? SolverSelector.CreateNewton(model);
            if (model.Oltcs.Count == 0 && model.TransformerFlowControls.Count == 0) { RegulatedModel = model; TapsSettled = true; return nr.Solve(model, options); }

            GridModel current = model;
            PowerFlowResult result = nr.Solve(current, options);

            // OLTC record -> branch index, computed ONCE: Dispatcher.Clone replaces branches by index and
            // preserves order/count, so the mapping is invariant across passes. N046: matched by name AND end bus
            // (see MapOltcRecordsToBranches); -1 when no branch carries the name. Order stability is asserted in
            // DEBUG at each reuse below.
            int[] branchOfRecord = MapOltcRecordsToBranches(current);

            // N046: an OLTC whose controlled bus is generator-regulated (PV or slack) holds its tap: the machine's
            // AVR fixes that voltage, so no tap position can move it and regulating it would only run the tap to a
            // limit. Before the first solve is judged, the bus types are the model's (the same rule as the passive
            // plant); see HeldRecords for how each converged pass refines them (N071).
            var heldRecord = new bool[model.Oltcs.Count];
            TapsHeldAtGeneratorBus = HeldRecords(model, branchOfRecord, null, heldRecord);

            // Opt-in ramp start (LoadRampStages > 0): only when the solve from the model's own taps did not converge.
            if (!result.Converged && LoadRampStages > 0)
            {
                PowerFlowResult unramped = result;
                if (!RampStart(model, ref current, ref result, nr, options, maxOltcPasses, branchOfRecord, heldRecord))
                {
                    // A partial-load stage did not converge: the answer is the model's own non-converged solve, unchanged.
                    RegulatedModel = model;
                    TapsSettled = false;
                    TapsAtLimit = Array.Empty<string>();
                    return unramped;
                }
                TapsSettled = false;
                TapsAtLimit = Array.Empty<string>();
            }

            result = TapLoop(model, ref current, result, nr, options, maxOltcPasses, branchOfRecord, heldRecord);
            RegulatedModel = current;
            return result;
        }
        finally
        {
            Interlocked.Exchange(ref _solving, 0);
        }
    }

    /// <summary>
    /// The OLTC loop itself: from the solved <paramref name="result"/> of <paramref name="current"/>, step every free tap
    /// one step toward its band, re-solve, and repeat until no tap moves or <paramref name="maxOltcPasses"/> re-solves.
    /// <paramref name="model"/> supplies the OLTC records and the stored bus types (for a ramp start's stage it is that
    /// stage's scaled model, whose records and types are the case's own). Returns the last solve; <paramref name="current"/>
    /// becomes the model it solved. Sets <see cref="TapsSettled"/>, <see cref="TapsAtLimit"/> (on a settled pass) and
    /// <see cref="TapsHeldAtGeneratorBus"/>, and adds each re-solve to <see cref="OltcPasses"/>.
    /// </summary>
    private PowerFlowResult TapLoop(GridModel model, ref GridModel current, PowerFlowResult result, IPowerFlowSolver nr,
        PowerFlowOptions? options, int maxOltcPasses, int[] branchOfRecord, bool[] heldRecord)
    {
            // Transformer flow controls (PSS/E COD 2, 3): the branch each drives (by name and From bus), -1 when none.
            int[] flowBranch = MapFlowControls(current);
            // Refilled each pass (result changes); allocated once.
            var vmByBus = new Dictionary<int, double>(result.BusIds.Count);
            var steppedThisPass = new HashSet<int>();

            for (int pass = 0; pass < maxOltcPasses; pass++)
            {
                // Never step a tap on a non-converged (or NaN) state: its voltages are a transient iterate, so
                // any tap move would be a reaction to garbage (R039). Stop with TapsSettled = false and return
                // the non-converged result as it is - its Converged flag already says so.
                if (!result.Converged) break;

                // N071: which OLTCs hold this pass is decided by the bus types THIS converged solve ended with, so a PV
                // bus whose generators were pinned at a reactive limit (solved as PQ: the AVR no longer holds its
                // voltage) is regulated by its tap changer instead of being held at whatever voltage it fell to.
                TapsHeldAtGeneratorBus = HeldRecords(model, branchOfRecord, result, heldRecord);

                vmByBus.Clear();
                for (int i = 0; i < result.BusIds.Count; i++) vmByBus[result.BusIds[i]] = result.Vm[i];

                // The branch-list copy is materialised lazily - only once a tap actually moves - so a
                // settled pass (the common last pass) pays no O(branches) copy or clone.
                List<Branch>? branches = null;
                // OLTCs that WANTED to move this pass (bus outside the deadband) but could not, because the tap is
                // already at the limit in the wanted direction. Only read when no tap moved at all.
                List<string>? pinnedAtLimit = null;
                // GR-503: the branches a record has already stepped this pass. Two records can resolve to one
                // transformer (the name-only fallback for a remote controlled bus); the second used to read the
                // first's new tap from the working copy and step it again, two steps per pass against the documented
                // "one step per transformer per pass". The first record in model order now has the pass; GridModel.
                // Validate warns about the shared branch.
                steppedThisPass.Clear();

                for (int k = 0; k < model.Oltcs.Count; k++)
                {
                    Oltc o = model.Oltcs[k];
                    int idx = branchOfRecord[k];
                    if (idx < 0 || heldRecord[k]) continue;   // no such transformer, or a generator-held bus (N046)
                    if (steppedThisPass.Contains(idx)) continue;   // GR-503: this transformer already stepped this pass
                    if (!vmByBus.TryGetValue(o.ControlsBus, out double v)) continue;
#if DEBUG
                    System.Diagnostics.Debug.Assert(
                        current.Branches[idx].Name == o.Transformer,
                        "SteadyStateSolver: branch order changed across Dispatcher.Clone - cached index is stale");
#endif
                    // Read from the working copy if it exists (reflecting an earlier in-pass tap change to
                    // the same branch, as the old code did), else from the current model.
                    Branch tx = branches is not null ? branches[idx] : current.Branches[idx];
                    double tap = tx.TapRatio;

                    // Which way does a tap step move the controlled bus? The Branch tap sits on the From end
                    // (MATPOWER convention: an ideal a:1 transformer between the From bus and the series
                    // impedance), so V_to ~ V_from / a. When the controlled bus is the To (untapped) end,
                    // LOWERING the tap RAISES it. When the controlled bus is the From (tapped) end, the
                    // relation reverses (V_from ~ a * V_to), so RAISING the tap raises it. Before October 2026
                    // (R013) the loop always used the To-end rule, so an OLTC regulating its From bus stepped
                    // the wrong way every pass and ran to a tap limit. A controlled bus on neither end (a
                    // remote bus downstream of the To side) keeps the historical To-end rule.
                    // The two step expressions are kept exactly as they always were (lower = Max(MinTap, tap - step),
                    // raise = Min(MaxTap, tap + step)), so a To-side OLTC is bit-for-bit unchanged.
                    bool controlsTappedEnd = o.ControlsBus == tx.From && o.ControlsBus != tx.To;
                    // GR-057: a tap that starts outside [MinTap, MaxTap] must never be moved AGAINST the wanted
                    // direction by the clamp (a tap above MaxTap asked to rise was clamped DOWN to MaxTap, pushing the
                    // bus further out of band). Such a tap holds, and is reported pinned at its limit below. For a tap
                    // inside the range both expressions are exactly the old ones.
                    double TapDown() => tap <= o.MinTap ? tap : Math.Max(o.MinTap, tap - o.StepPu);
                    double TapUp() => tap >= o.MaxTap ? tap : Math.Min(o.MaxTap, tap + o.StepPu);

                    double newTap = tap;
                    if (v < o.TargetVpu - o.DeadbandPu) newTap = controlsTappedEnd ? TapUp() : TapDown();      // raise controlled bus
                    else if (v > o.TargetVpu + o.DeadbandPu) newTap = controlsTappedEnd ? TapDown() : TapUp(); // lower it

                    bool outsideBand = v < o.TargetVpu - o.DeadbandPu || v > o.TargetVpu + o.DeadbandPu;
                    if (Math.Abs(newTap - tap) > TapChangeEpsilon)
                    {
                        branches ??= current.Branches.ToList();
                        if (o.ImpedanceCorrection is { } ic)
                        {
                            // A transformer with an impedance correction table: its impedance follows the tap.
                            (double rc, double xc) = ic.At(newTap);
                            branches[idx] = branches[idx] with { TapRatio = newTap, R = rc, X = xc };
                        }
                        else branches[idx] = branches[idx] with { TapRatio = newTap };
                        steppedThisPass.Add(idx);
                    }
                    else if (outsideBand)
                    {
                        // The bus is out of band but the step was clamped to (or within epsilon of) the current
                        // tap: the tap is pinned at MinTap/MaxTap. Regulation has run out of range, not finished.
                        (pinnedAtLimit ??= new List<string>()).Add(o.Transformer);
                    }
                }

                // Flow controls: the reactive power into the transformer at its From end moves with the tap (raising the
                // tap lowers the From-side internal voltage V_from / a, so less reactive power flows in); the active power
                // with the phase shift (P_from ~ (angle_from - angle_to - shift) / X, so raising the shift lowers it).
                for (int k = 0; k < model.TransformerFlowControls.Count; k++)
                {
                    TransformerFlowControl fc = model.TransformerFlowControls[k];
                    int idx = flowBranch[k];
                    if (idx < 0 || steppedThisPass.Contains(idx)) continue;
                    LineFlow lf = result.LineFlows[idx];
                    double flow = fc.Kind == TransformerFlowKind.ReactivePower ? lf.QFromMvar : lf.PFromMw;
                    Branch tx = branches is not null ? branches[idx] : current.Branches[idx];
                    double at = fc.Kind == TransformerFlowKind.ReactivePower ? tx.TapRatio : tx.PhaseShiftDeg;
                    double Down() => at <= fc.Min ? at : Math.Max(fc.Min, at - fc.Step);
                    double Up() => at >= fc.Max ? at : Math.Min(fc.Max, at + fc.Step);
                    double next = flow < fc.FlowMin ? Down() : flow > fc.FlowMax ? Up() : at;
                    bool outside = flow < fc.FlowMin || flow > fc.FlowMax;
                    if (Math.Abs(next - at) > TapChangeEpsilon)
                    {
                        branches ??= current.Branches.ToList();
                        Branch nb = fc.Kind == TransformerFlowKind.ReactivePower ? branches[idx] with { TapRatio = next } : branches[idx] with { PhaseShiftDeg = next };
                        if (fc.ImpedanceCorrection is { } ic)
                        {
                            (double rc, double xc) = ic.At(next);
                            nb = nb with { R = rc, X = xc };
                        }
                        branches[idx] = nb;
                        steppedThisPass.Add(idx);
                    }
                    else if (outside) (pinnedAtLimit ??= new List<string>()).Add(fc.Transformer);
                }

                if (branches is null)
                {
                    // No tap moved. Settled only if no OLTC is pinned at a limit with its bus still out of band.
                    TapsAtLimit = pinnedAtLimit is null ? Array.Empty<string>() : pinnedAtLimit.ToArray();
                    TapsSettled = pinnedAtLimit is null;
                    break;
                }

                current = Dispatcher.Clone(current, branches: branches);
                result = nr.Solve(current, options);
                OltcPasses++;   // one more re-solve (the passes of a ramp start's stages add up)
            }

        return result;
    }

    /// <summary>
    /// Opt-in ramp start (<see cref="LoadRampStages"/> = N above 0), run by <see cref="Solve"/> only when the solve from the
    /// model's own taps did not converge: solve the case with every bus demand (Pd, Qd) and every in-service generator's
    /// Pg scaled to 1/N, 2/N, ..., (N - 1)/N, letting the OLTC loop settle at each stage and carrying the taps (and, as the
    /// next stage's starting point, the solved voltages: Vm and angle at PQ buses, angle at PV buses) forward, and then
    /// solve the full case from the last stage's taps and voltages. A heavily loaded network whose taps start at the
    /// file's nominal ratios can be beyond its voltage collapse point there, so Newton has nothing to converge to, while
    /// the regulated case (taps moved) is an ordinary solve; this is the reason PSS/E adjusts taps during the solution
    /// rather than after it.
    /// <para>Returns false (and <see cref="Solve"/> returns the model's own non-converged solve, unchanged) when a
    /// partial-load stage does not converge, its fraction in <see cref="LoadRampFailedAt"/>. Returns true with
    /// <paramref name="current"/> and <paramref name="result"/> the full-load solve from the carried taps; when that one
    /// does not converge, <see cref="LoadRampFailedAt"/> is 1 and the result says so.</para>
    /// </summary>
    private bool RampStart(GridModel model, ref GridModel current, ref PowerFlowResult result, IPowerFlowSolver nr,
        PowerFlowOptions? options, int maxOltcPasses, int[] branchOfRecord, bool[] heldRecord)
    {
        int n = LoadRampStages;
        IReadOnlyList<Branch> taps = model.Branches;
        PowerFlowResult? warm = null;
        for (int k = 1; k <= n; k++)
        {
            bool full = k == n;
            double lambda = full ? 1.0 : (double)k / n;
            GridModel stage = RampStage(model, full ? null : lambda, taps, warm);
            PowerFlowResult r = nr.Solve(stage, options);
            LoadRampStagesRun = k;
            if (full)
            {
                if (!r.Converged) LoadRampFailedAt = 1.0;
                current = stage;
                result = r;
                return true;
            }
            if (!r.Converged) { LoadRampFailedAt = lambda; return false; }
            TapsSettled = false;
            TapsAtLimit = Array.Empty<string>();
            GridModel settled = stage;
            r = TapLoop(stage, ref settled, r, nr, options, maxOltcPasses, branchOfRecord, heldRecord);
            if (!r.Converged) { LoadRampFailedAt = lambda; return false; }
            taps = settled.Branches;
            warm = r;
        }
        throw new InvalidOperationException("SteadyStateSolver: the ramp start ended without its full-load stage.");   // unreachable: k == n returns
    }

    /// <summary>
    /// The first ramp stage's starting angles when the network has phase-shifting transformers: each bus reached from a
    /// slack bus through in-service branches starts at the slack's angle less the phase shifts along the way (a branch's
    /// <see cref="Branch.PhaseShiftDeg"/> delays its To end: angle(To) = angle(From) - shift), as PSS/E's flat start
    /// does. The UKGDS EHV cases put 30 degree vector-group shifts on every 132/33 and 33/11 kV transformer, so their
    /// solved angles sit 30 to 60 degrees from the file's flat 0 and Newton, started at 0, does not converge at any load.
    /// Null when no branch shifts (the case's own angles are kept). The shifts around every loop are taken as consistent;
    /// a bus is given the first angle the search reaches it with.
    /// </summary>
    private static Dictionary<int, double>? PhaseShiftAngles(GridModel model, IReadOnlyList<Branch> branches)
    {
        bool any = false;
        foreach (Branch br in branches) if (br.InService && br.PhaseShiftDeg != 0) { any = true; break; }
        if (!any) return null;
        var adj = new Dictionary<int, List<(int Bus, double Shift)>>();
        foreach (Branch br in branches)
        {
            if (!br.InService) continue;
            (adj.TryGetValue(br.From, out var f) ? f : adj[br.From] = new()).Add((br.To, -br.PhaseShiftDeg));
            (adj.TryGetValue(br.To, out var t) ? t : adj[br.To] = new()).Add((br.From, br.PhaseShiftDeg));
        }
        var angle = new Dictionary<int, double>();
        var queue = new Queue<int>();
        foreach (Bus b in model.Buses)
        {
            if (b.Type != BusType.Slack || angle.ContainsKey(b.Id)) continue;
            angle[b.Id] = b.VaDeg;
            queue.Enqueue(b.Id);
            while (queue.Count > 0)
            {
                int u = queue.Dequeue();
                if (!adj.TryGetValue(u, out var next)) continue;
                foreach ((int v, double d) in next)
                    if (angle.TryAdd(v, angle[u] + d)) queue.Enqueue(v);
            }
        }
        return angle;
    }

    /// <summary>One ramp-start stage: <paramref name="model"/> with <paramref name="taps"/> as its branches, every bus
    /// demand and in-service generator Pg multiplied by <paramref name="lambda"/> (null: the case's own values, untouched),
    /// and, when <paramref name="warm"/> is given, its solved voltages as the starting point (Vm and angle at PQ buses,
    /// angle at PV buses; the slack and every setpoint are the case's own). The first stage (no <paramref name="warm"/>)
    /// starts its angles from the phase shifts (<see cref="PhaseShiftAngles"/>) when the network has any.</summary>
    private static GridModel RampStage(GridModel model, double? lambda, IReadOnlyList<Branch> taps, PowerFlowResult? warm)
    {
        Dictionary<int, int>? at = null;
        if (warm is not null)
        {
            at = new Dictionary<int, int>(warm.BusIds.Count);
            for (int i = 0; i < warm.BusIds.Count; i++) at.TryAdd(warm.BusIds[i], i);
        }
        Dictionary<int, double>? shifted = warm is null ? PhaseShiftAngles(model, taps) : null;
        var buses = new Bus[model.Buses.Count];
        for (int i = 0; i < buses.Length; i++)
        {
            Bus b = model.Buses[i];
            if (lambda is double l) b = b with { Pd = b.Pd * l, Qd = b.Qd * l };
            if (at is not null && at.TryGetValue(b.Id, out int w))
            {
                if (b.Type == BusType.Pq) b = b with { Vm = warm!.Vm[w], VaDeg = warm.VaDeg[w] };
                else if (b.Type == BusType.Pv) b = b with { VaDeg = warm!.VaDeg[w] };
            }
            else if (shifted is not null && b.Type != BusType.Slack && shifted.TryGetValue(b.Id, out double a))
                b = b with { VaDeg = a };
            buses[i] = b;
        }
        IReadOnlyList<Generator>? gens = lambda is double lg ? model.Generators.Select(g => g with { Pg = g.Pg * lg }).ToArray() : null;
        return model.CopyWith(buses: buses, generators: gens, branches: taps);
    }

    /// <summary>
    /// Fill <paramref name="held"/> (aligned to <c>model.Oltcs</c>) with whether each record HOLDS its tap because its
    /// controlled bus is generator-regulated, and return how many do (records that map to no transformer are never
    /// counted). N046 introduced the hold; N071 (October 2026) decides it from the bus types the solve actually ended
    /// with, not the model's stored types:
    /// <list type="bullet">
    /// <item>With a converged <paramref name="result"/> that reports <see cref="PowerFlowResult.SolvedBusTypes"/> (both
    /// Newton engines do), a record holds when its controlled bus solved as PV or slack. A bus typed PV in the model
    /// whose generators the reactive-limit loop pinned at Qmin or Qmax solved as PQ: its voltage is no longer fixed by
    /// an AVR, so its tap changer regulates it. (So does a PV-typed bus with no generator, which both engines solve as
    /// PQ since R283: nothing holds its voltage either.) Before N071 such a bus kept its tap held at the voltage it
    /// fell to.</item>
    /// <item>Otherwise (no result yet, a non-converged result, or a solver that does not report the types) the
    /// model's stored types decide, exactly the N046 rule.</item>
    /// </list>
    /// A bus id appearing twice takes its first occurrence, as the voltage lookup does.
    /// </summary>
    internal static int HeldRecords(GridModel model, int[] branchOfRecord, PowerFlowResult? result, bool[] held)
    {
        var busTypeOf = new Dictionary<int, BusType>(model.Buses.Count);
        IReadOnlyList<BusType>? solved = result is { Converged: true } ? result.SolvedBusTypes : null;
        if (solved is not null && solved.Count == result!.BusIds.Count)
            for (int i = 0; i < solved.Count; i++) busTypeOf.TryAdd(result.BusIds[i], solved[i]);
        else
            foreach (Bus bus in model.Buses) busTypeOf.TryAdd(bus.Id, bus.Type);

        int count = 0;
        for (int k = 0; k < model.Oltcs.Count; k++)
        {
            held[k] = false;
            if (branchOfRecord[k] < 0) continue;
            if (busTypeOf.TryGetValue(model.Oltcs[k].ControlsBus, out BusType t) && t is BusType.Pv or BusType.Slack)
            {
                held[k] = true;
                count++;
            }
        }
        return count;
    }

    /// <summary>The branch index each <see cref="GridModel.TransformerFlowControls"/> record drives: the first branch
    /// whose name is the record's transformer and whose From bus is the record's; -1 when none.</summary>
    private static int[] MapFlowControls(GridModel model)
    {
        var map = new int[model.TransformerFlowControls.Count];
        for (int k = 0; k < map.Length; k++)
        {
            TransformerFlowControl fc = model.TransformerFlowControls[k];
            map[k] = -1;
            for (int i = 0; i < model.Branches.Count; i++)
                if (model.Branches[i].Name == fc.Transformer && model.Branches[i].From == fc.FromBus) { map[k] = i; break; }
        }
        return map;
    }

    /// <summary>
    /// The branch index each <see cref="GridModel.Oltcs"/> record drives (N046), aligned to <c>model.Oltcs</c>: the
    /// first branch (in model order) whose <see cref="Branch.Name"/> equals the record's
    /// <see cref="Oltc.Transformer"/> AND that has the record's <see cref="Oltc.ControlsBus"/> at its From or To end.
    /// When no same-named branch touches the controlled bus (a controlled bus remote from the transformer), the first
    /// branch of that name, which is the historical name-only rule; -1 when no branch carries the name (or the record
    /// names none). Shared by this solver and <see cref="Assets.PassivePlantFleet"/>, so the two always agree on which
    /// transformer a record belongs to.
    /// </summary>
    public static int[] MapOltcRecordsToBranches(GridModel model)
    {
        // Name -> every branch carrying it, in model order.
        var byName = new Dictionary<string, List<int>>(model.Branches.Count, StringComparer.Ordinal);
        for (int i = 0; i < model.Branches.Count; i++)
        {
            string? nm = model.Branches[i].Name;
            if (nm is null) continue;
            if (!byName.TryGetValue(nm, out List<int>? list)) byName[nm] = list = new List<int>(1);
            list.Add(i);
        }

        var map = new int[model.Oltcs.Count];
        for (int k = 0; k < model.Oltcs.Count; k++)
        {
            Oltc o = model.Oltcs[k];
            map[k] = -1;
            if (o.Transformer is null || !byName.TryGetValue(o.Transformer, out List<int>? named)) continue;
            foreach (int i in named)
            {
                Branch b = model.Branches[i];
                if (b.From == o.ControlsBus || b.To == o.ControlsBus) { map[k] = i; break; }
            }
            if (map[k] < 0) map[k] = named[0];   // remote controlled bus: the first branch of the name (historical)
        }
        return map;
    }
}
