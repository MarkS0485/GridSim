using System.Numerics;
using GridSim.Core.Abstractions;
using GridSim.Core.Model;

namespace GridSim.Core.PowerFlow;

/// <summary>
/// A backward/forward-sweep (BFS) power flow for <b>radial and weakly-meshed distribution</b>
/// networks - the "two-way" solve. Transmission-oriented Newton-Raphson struggles on feeders with
/// high R/X ratios; BFS is the standard, robust method there. Each iteration is two passes over the
/// feeder tree:
/// <list type="number">
/// <item><b>Backward</b> (leaves -> source): sum the load/shunt currents up each branch to get the
/// series current flowing through it.</item>
/// <item><b>Forward</b> (source -> leaves): apply the series voltage drop <c>V_child = V_parent/tau -
/// Z*I</c> to update every bus voltage.</item>
/// </list>
/// It requires a single slack (the source) and a connected in-service topology. On top of the plain
/// radial solve it adds two capabilities:
/// <list type="bullet">
/// <item><b>Weakly-meshed feeders.</b> A spanning tree is grown from the source and the few extra
/// (non-tree) branches become <i>breakpoints</i>. A breakpoint (loop) impedance matrix is built once
/// by superposition, and each sweep injects compensating breakpoint currents so the loops close
/// (Shirmohammadi-style multi-port compensation). Only a small number of loops is accepted - a densely
/// meshed transmission grid is still declined as "not a distribution feeder".</item>
/// <item><b>PV / voltage-controlled buses.</b> Non-slack generators that regulate voltage hold their
/// bus at the setpoint |V| by a reactive-power correction each sweep, using the path reactance as the
/// dV/dQ sensitivity, clamped to the generator's Q-limits (a PV bus that saturates reverts to PQ).</item>
/// </list>
/// It falls back to the old "not radial -> non-converged" report only when the network is genuinely
/// unsupported: disconnected, no slack, or too meshed to be a weakly-meshed feeder. Non-regulating
/// generators are constant-power (PQ) injections; loads are constant power unless the bus carries a
/// voltage-dependent <see cref="ZipLoad"/>, which is drawn at the swept voltage exactly as the Newton
/// solvers draw it (<see cref="LoadAtVoltage"/>). Line charging, bus shunts
/// and transformer taps (including non-unity magnitude and phase shift) are carried through exactly:
/// the forward voltage step and backward current accumulation apply the ideal-transformer ratio on the
/// tapped branch (divide V / multiply I by the complex tap, per side), so the converged voltages solve
/// the same MATPOWER pi+tap model the Newton solvers use - not just at unity tap.
/// <para><b>Same inputs, same physics as Newton (R036/R037, October 2026).</b> The sweep is auto-selected for
/// radial models (<see cref="SolverSelector"/>), so it must not quietly solve a different problem. It honours
/// <see cref="PowerFlowOptions.FlatStart"/> (a warm start from the bus Vm/VaDeg otherwise, as Newton),
/// <see cref="PowerFlowOptions.EnforceQLimits"/> (off = PV buses hold their setpoint with unlimited reactive
/// output, as Newton), <see cref="PowerFlowOptions.Cancellation"/> (checked every sweep) and ZIP loads; it
/// builds each tap with <see cref="AdmittanceStamp"/>'s rule (a ratio of 0 or below means 1, the phase shift is
/// always kept); the slack is held at its own VaDeg; and every bus voltage setpoint comes from the one shared
/// rule <see cref="VoltageSetpoint"/>. Its reactive-limit clamp is continuous (a saturated PV bus comes back
/// off its limit as soon as the voltage allows), which is the converged state of Newton's outer loop with
/// <see cref="PowerFlowOptions.RevertQLimits"/> on.</para>
/// </summary>
public sealed class BackwardForwardSweep : IPowerFlowSolver
{
    /// <summary>
    /// Largest number of independent loops we will compensate. Weakly-meshed feeders carry only a
    /// handful of tie/loop branches; beyond this the network is a meshed transmission grid (e.g. the
    /// IEEE 14-bus has 7 independent loops) and BFS is the wrong tool, so we decline rather than
    /// return a doubtful answer.
    /// </summary>
    private const int MaxCompensatedLoops = 6;

    /// <summary>Floor on |V| used when dividing by a bus voltage, and on path reactance for the PV
    /// dV/dQ sensitivity - guards against divide-by-near-zero on a collapsed feeder.</summary>
    internal const double SafetyFloor = 1e-6;   // internal (was private) only so a test can pin it; value unchanged

    /// <summary>Floor (pu) on the tolerance the PV / loop controls settle to: they use
    /// max(Tolerance, this), a looser target than the voltages (R189).</summary>
    internal const double MinControlTolPu = 1e-6;

    /// <summary>Pivot floor of the small breakpoint-impedance solve: a pivot (or diagonal) of magnitude below
    /// this is skipped and its loop correction left at zero.
    ///
    /// <para>ABSOLUTE, deliberately (R189, decided under Q13, October 2026). The loop matrix is a sum of branch
    /// series impedances in per unit on the system base, so its entries sit in a known, bounded range (about 1e-5
    /// to 10 pu on real feeders); 1e-14 pu is many orders below any real loop impedance, so on a real network it
    /// only catches a genuinely degenerate loop set (two breakpoints closing the same loop). A relative floor
    /// would buy nothing on that range and could change which corrections apply. It is also safe in the
    /// fail-loud sense: a skipped pivot leaves that loop's closure residual uncorrected, the residual check
    /// below then keeps <c>loopOk</c> false, and the solve reports NOT converged instead of a wrong answer.</para>
    /// </summary>
    internal const double LoopPivotFloor = 1e-14;

    public PowerFlowResult Solve(GridModel model, PowerFlowOptions? options = null)
    {
        // The sweep does not model two-terminal LCC links; solving without them would report a different network.
        if (model.LccLinks.Count > 0)
            throw new InvalidOperationException($"The backward/forward sweep does not model LCC HVDC links; the model has {model.LccLinks.Count}. Use a Newton engine.");
        if (model.VscLinks.Count > 0)
            throw new InvalidOperationException($"The backward/forward sweep does not model VSC HVDC links; the model has {model.VscLinks.Count}. Use a Newton engine.");
        if (model.MtdcLinks.Count > 0)
            throw new InvalidOperationException($"The backward/forward sweep does not model multi-terminal DC links; the model has {model.MtdcLinks.Count}. Use a Newton engine.");
        if (model.FactsDevices.Count > 0 || model.StaticVarCompensators.Count > 0)
            throw new InvalidOperationException($"The backward/forward sweep does not model FACTS devices or static var compensators; the model has {model.FactsDevices.Count} and {model.StaticVarCompensators.Count}. Use a Newton engine.");
        if (model.InductionMachines.Count > 0)
            throw new InvalidOperationException($"The backward/forward sweep does not model induction machines; the model has {model.InductionMachines.Count}. Use a Newton engine.");
        var opt = options ?? new PowerFlowOptions();
        if (opt.EnforceQLimits) NewtonShell.QLimitPasses(opt);   // same pass-cap validation as Newton (fails loud on < 1)
        opt.Cancellation.ThrowIfCancellationRequested();
        int n = model.BusCount;
        var index = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) index[model.Buses[i].Id] = i;

        int slack = -1;
        for (int i = 0; i < n; i++) if (model.Buses[i].Type == BusType.Slack) { slack = i; break; }
        if (slack < 0) throw new InvalidDataException("BackwardForwardSweep needs a slack (source) bus.");

        // Net constant-power injection per bus (generation - demand), p.u.; loads draw, gens inject.
        var sLoad = new Complex[n];                        // power DRAWN at the bus (load - gen), nominal load
        var yShunt = new Complex[n];                       // bus shunt admittance to ground (p.u.)
        var zip = new ZipLoad?[n];                         // voltage-dependent load per bus (null = constant power)
        for (int i = 0; i < n; i++)
        {
            Bus b = model.Buses[i];
            sLoad[i] = new Complex(b.Pd, b.Qd) / model.BaseMva;
            yShunt[i] = new Complex(b.Gs, b.Bs) / model.BaseMva;
            if (b.Zip is { VariesWithVoltage: true } z) zip[i] = z;
        }
        foreach (Generator g in model.Generators)
        {
            // Bug hunt 2026-10-29, finding 9: a unit on a bus the model does not have is refused by name, as both Newton
            // engines refuse it (their bus lookup throws). It used to be skipped, so the sweep solved a different dispatch
            // and said nothing. Only reachable on a model that skipped GridModel.Validate.
            if (!index.TryGetValue(g.Bus, out int gi))
                throw new InvalidDataException($"Backward-forward sweep: a generator sits at bus {g.Bus}, which the model does not have.");
            sLoad[gi] -= new Complex(g.Pg, g.Qg) / model.BaseMva;   // generation reduces net draw
        }

        // In-service branches with series admittance, half-charging and tap.
        var br = new List<BranchModel>(model.Branches.Count);
        for (int bi = 0; bi < model.Branches.Count; bi++)
        {
            Branch e = model.Branches[bi];
            if (!e.InService) continue;   // open branch: not part of the radial network (bit-identical when all in service)
            if (!index.TryGetValue(e.From, out int f) || !index.TryGetValue(e.To, out int t))   // finding 9, as above
                throw new InvalidDataException($"Backward-forward sweep: branch {bi} ({e.From}-{e.To}) names a bus the model does not have.");
            Complex y = Complex.One / new Complex(e.R, e.X);
            // The tap by AdmittanceStamp's rule (R036): a ratio of 0 or below means 1 (the MATPOWER convention for
            // a unity-ratio branch) and the phase shift is ALWAYS kept. The old rule mapped ratio 0 to Complex.One,
            // dropping the shift of a phase shifter stored with ratio 0, and used a negative ratio raw. For any
            // positive ratio this is the same expression as before, so those results are unchanged.
            double tapMag = e.TapRatio <= 0 ? 1.0 : e.TapRatio;
            Complex tap = DetMath.FromPolar(tapMag, e.PhaseShiftDeg * Math.PI / 180.0);
            Complex bsh = new(0, e.B / 2.0);
            br.Add(new BranchModel(f, t, y, bsh, tap, e, bi));
            // Half line-charging appears as a shunt at each end. MATPOWER places the pi-section and the
            // ideal transformer on the "from" side, so the from-end charging referred through the tap
            // gains a 1/|tap|^2 factor (this is the shunt part of Yff = (y+bsh)/|tap|^2); the to-end shunt
            // stays unscaled (Ytt = y+bsh). At unity tap both reduce to B/2, so radial results are intact.
            double tapMag2 = (tap * Complex.Conjugate(tap)).Real;
            yShunt[f] += bsh / tapMag2;
            yShunt[t] += bsh;
        }

        // Build the spanning tree rooted at the slack; branches not used to first reach a bus are loop
        // (breakpoint) branches to be compensated.
        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = new List<int>();
        for (int k = 0; k < br.Count; k++) { adj[br[k].F].Add(k); adj[br[k].T].Add(k); }

        var parentBranch = new int[n];
        var order = new List<int>(n);                      // BFS order from the slack
        var visited = new bool[n];
        var treeBranch = new bool[br.Count];
        Array.Fill(parentBranch, -1);
        var queue = new Queue<int>();
        queue.Enqueue(slack); visited[slack] = true; order.Add(slack);
        int reached = 1;
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            foreach (int k in adj[u])
            {
                int v = br[k].F == u ? br[k].T : br[k].F;
                if (visited[v]) continue;
                visited[v] = true; parentBranch[v] = k; treeBranch[k] = true; order.Add(v); reached++;
                queue.Enqueue(v);
            }
        }

        // Loop (breakpoint) branches = in-service branches not in the spanning tree.
        var links = new List<int>(br.Count);
        for (int k = 0; k < br.Count; k++) if (!treeBranch[k]) links.Add(k);
        int nLoops = links.Count;

        // Gating: a disconnected network can't be swept from one source; a densely meshed one isn't a
        // distribution feeder. Both fall back to the old "not radial -> non-converged" report.
        if (reached != n)
            return NonRadial(model, $"disconnected (buses={n}, reached={reached})");
        if (nLoops > MaxCompensatedLoops)
            return NonRadial(model, $"too meshed for weak-mesh compensation (loops={nLoops} > {MaxCompensatedLoops})");

        // children[i] = buses whose tree-parent is i (via their parentBranch).
        var children = new List<int>[n];
        for (int i = 0; i < n; i++) children[i] = new List<int>();
        for (int oi = 1; oi < order.Count; oi++)
        {
            int v = order[oi];
            int k = parentBranch[v];
            int p = br[k].F == v ? br[k].T : br[k].F;
            children[p].Add(v);
        }

        // Slack voltage: the source's setpoint by the shared rule (VoltageSetpoint: the LAST generator listed at
        // the bus, as Newton and MATPOWER use; before R037 this took the FIRST), else the bus Vm, at the slack's
        // own angle VaDeg (as Newton holds it; this used to force angle 0, shifting every angle when VaDeg != 0).
        // A generator's set-point must be a positive finite voltage: it is refused, not replaced (Newton solves the same
        // data unchanged, so a silent 1.0 here meant the two engines solved different problems). Only a slack with NO
        // generator falls back to its stored Vm, and to 1.0 when that is not positive (the documented rule).
        double? slackSet = VoltageSetpoint.ForBus(model, model.Buses[slack].Id);
        if (slackSet is double sv && !(sv > 0 && double.IsFinite(sv)))
            throw new InvalidDataException($"Backward/forward sweep: the slack bus {model.Buses[slack].Id} generator set-point Vg {sv} is not a voltage above 0 pu.");
        double vslack = slackSet ?? model.Buses[slack].Vm;
        if (vslack <= 0) vslack = 1.0;
        double slackAngle = model.Buses[slack].VaDeg * Math.PI / 180.0;
        Complex vslackC = slackAngle == 0.0 ? new Complex(vslack, 0) : DetMath.FromPolar(vslack, slackAngle);
        var volt = new Complex[n];
        for (int i = 0; i < n; i++) volt[i] = vslackC;
        if (!opt.FlatStart)
        {
            // Warm start from the stored bus voltages (R036), exactly as Newton seeds its PQ buses when FlatStart is
            // off. The slack keeps its setpoint; a bus with a non-positive stored magnitude cannot seed a sweep
            // (it divides by |V|), so that is an input error, not something to paper over.
            for (int i = 0; i < n; i++)
            {
                if (i == slack) continue;
                Bus b = model.Buses[i];
                if (!(b.Vm > 0) || !double.IsFinite(b.Vm) || !double.IsFinite(b.VaDeg))
                    throw new InvalidDataException(
                        $"BackwardForwardSweep: bus {b.Id} has stored voltage {b.Vm} pu at {b.VaDeg} deg, which cannot seed a warm start. " +
                        "Fix the bus data or solve with FlatStart = true.");
                volt[i] = DetMath.FromPolar(b.Vm, b.VaDeg * Math.PI / 180.0);
            }
        }

        var j = new Complex[br.Count];                     // series current through each branch

        // A single backward/forward pass: accumulate the given nodal injection currents up the tree,
        // then apply the series drops out from the source. Shared by the nonlinear solve and by the
        // (linear) breakpoint-sensitivity build.
        void Sweep(Complex[] inj, Complex vRoot, Complex[] vOut, Complex[] jOut)
        {
            vOut[slack] = vRoot;
            // Backward pass (leaves -> source). jOut[k] holds the current the branch delivers INTO its
            // child bus. Folding a child's accumulated draw onto its parent must cross the child branch's
            // ideal transformer: MATPOWER's tap sits on the "from" side, so the current referred to the
            // parent gains a 1/conj(tap) factor when the parent is the tapped "from" end (I_from =
            // I_series/conj(tap)) and a conj(tap) factor when the parent is the "to" end. Both are x1 at
            // unity tap, so the radial current accumulation is unchanged there.
            for (int oi = order.Count - 1; oi >= 1; oi--)
            {
                int bus = order[oi];
                Complex acc = inj[bus];
                foreach (int c in children[bus])
                {
                    int ck = parentBranch[c];
                    BranchModel ce = br[ck];
                    acc += ce.F == bus ? jOut[ck] / Complex.Conjugate(ce.Tap)   // parent is the tapped "from" end
                                       : Complex.Conjugate(ce.Tap) * jOut[ck];  // parent is the "to" end
                }
                jOut[parentBranch[bus]] = acc;
            }
            // Forward pass (source -> leaves). Step the voltage across the series impedance and the ideal
            // transformer, consistent with the same tap convention as the result assembly (Yff/Yft).
            for (int oi = 1; oi < order.Count; oi++)
            {
                int bus = order[oi];
                int k = parentBranch[bus];
                BranchModel e = br[k];
                int parent = e.F == bus ? e.T : e.F;
                Complex zs = Complex.One / e.Y;
                if (parent == e.F)
                    // Child is the "to" side: V_to = V_from/tap - Z*J.
                    vOut[bus] = vOut[parent] / e.Tap - zs * jOut[k];
                else
                {
                    // Child is the tapped "from" side: V_from = tap*V_to - |tap|^2*Z*J. The series drop is
                    // referred back through the ideal transformer, so it picks up the |tap|^2 factor.
                    double t2 = (e.Tap * Complex.Conjugate(e.Tap)).Real;
                    vOut[bus] = e.Tap * vOut[parent] - t2 * zs * jOut[k];
                }
            }
        }

        // ---- Weakly-meshed setup: breakpoint currents and the loop-impedance matrix -----------------
        var loopFrom = new int[nLoops];
        var loopTo = new int[nLoops];
        var loopZ = new Complex[nLoops];                   // each link's own series impedance
        var loopTap = new Complex[nLoops];                 // each link's complex tap (1 for lines/cables)
        var loopI = new Complex[nLoops];                   // injected breakpoint (loop) currents = SERIES current, state
        for (int p = 0; p < nLoops; p++)
        {
            BranchModel e = br[links[p]];
            // loopI is the SERIES current I_s (the tree convention, V_from/tap - V_to = z*I_s). For a
            // tapped/phase-shifting link the two end currents differ by the ratio - I_from = I_s/conj(tap),
            // I_to = I_s - and the closure voltage carries the tap. Ignoring e.Tap solved the wrong loop
            // constraint for transformer/phase-shifter ties. At unity tap every term below is unchanged.
            loopFrom[p] = e.F; loopTo[p] = e.T; loopZ[p] = Complex.One / e.Y; loopTap[p] = e.Tap;
        }
        // M[p,q] = z_p*delta_pq - (V(from_p)-V(to_p)) caused by a unit breakpoint current at loop q, with
        // loads off and the slack grounded. Solving M*deltaI = residual closes every loop each sweep.
        Complex[,] loopM = new Complex[nLoops, nLoops];
        if (nLoops > 0)
        {
            var sV = new Complex[n];
            var sJ = new Complex[br.Count];
            for (int q = 0; q < nLoops; q++)
            {
                var inj = new Complex[n];
                inj[loopFrom[q]] += Complex.One / Complex.Conjugate(loopTap[q]);  // from-end current = I_s/conj(tap)
                inj[loopTo[q]] -= Complex.One;                                    // to-end current = I_s
                Sweep(inj, Complex.Zero, sV, sJ);
                for (int p = 0; p < nLoops; p++)
                {
                    Complex s = sV[loopFrom[p]] / loopTap[p] - sV[loopTo[p]];     // closure voltage carries the tap
                    loopM[p, q] = (p == q ? loopZ[p] : Complex.Zero) - s;
                }
            }
        }

        // ---- PV / voltage-controlled buses ----------------------------------------------------------
        // A non-slack bus with a voltage-regulating generator: hold |V| at the setpoint by adjusting the
        // reactive injection (qExtra, added on top of the generator's nominal Qg) within Q-limits.
        var pvBuses = new List<int>();
        var pvVset = new double[n];
        var pvXth = new double[n];                         // path reactance source->bus (dV/dQ sensitivity)
        var pvQmin = new double[n];
        var pvQmax = new double[n];
        var pvQnom = new double[n];                        // nominal gen reactive at the bus (p.u.)
        var qExtra = new double[n];                        // extra reactive injection, state (p.u.)
        {
            // Accumulate the series path impedance from the source to each bus (for the sensitivity).
            var zPath = new Complex[n];
            for (int oi = 1; oi < order.Count; oi++)
            {
                int bus = order[oi];
                int k = parentBranch[bus];
                int parent = br[k].F == bus ? br[k].T : br[k].F;
                zPath[bus] = zPath[parent] + Complex.One / br[k].Y;
            }
            for (int i = 0; i < n; i++)
            {
                if (i == slack || model.Buses[i].Type != BusType.Pv) continue;
                double qn = 0, qmin = 0, qmax = 0; bool hasGen = false;
                foreach (Generator g in model.Generators)
                {
                    if (!index.TryGetValue(g.Bus, out int gi) || gi != i) continue;
                    hasGen = true;
                    qn += g.Qg; qmin += g.Qmin; qmax += g.Qmax;
                }
                if (!hasGen) continue;                     // no reactive source => can't regulate; leave as PQ
                // The setpoint by the one shared rule (R037): the last generator listed at the bus, as Newton. This
                // used to take the HIGHEST Vg, so the engines disagreed whenever co-located units differed.
                double vset = VoltageSetpoint.ForBus(model, model.Buses[i].Id)!.Value;
                if (!opt.EnforceQLimits)
                {
                    // Limits off (R036): hold the setpoint with unlimited reactive output, exactly as Newton does
                    // with EnforceQLimits = false. The clamp used to apply regardless of the option.
                    qmin = double.NegativeInfinity; qmax = double.PositiveInfinity;
                }
                if (!(vset > 0 && double.IsFinite(vset)))
                    throw new InvalidDataException($"Backward/forward sweep: PV bus {model.Buses[i].Id} generator set-point Vg {vset} is not a voltage above 0 pu.");
                pvBuses.Add(i);
                pvVset[i] = vset;
                pvXth[i] = Math.Max(Math.Abs(zPath[i].Imaginary), SafetyFloor);
                pvQnom[i] = qn / model.BaseMva;
                pvQmin[i] = qmin / model.BaseMva;
                pvQmax[i] = qmax / model.BaseMva;
            }
        }

        double ctrlTol = Math.Max(opt.Tolerance, MinControlTolPu);    // controls settle to a looser tol than voltages
        bool converged = false;
        double maxDelta = double.PositiveInfinity;
        var vPrev = new Complex[n];

        // Diagnostic capture (opt-in). BFS forms no admittance matrix (it sweeps branch-by-branch),
        // so YBus stays empty - the branch impedances live in the exported model section. The net
        // per-unit injections are the real inputs; each sweep's per-bus |deltaV| is the convergence signal.
        Diagnostics.SolveTrace? trace = opt.Trace;
        if (trace is not null)
        {
            trace.Header("bfs", n, model.BaseMva);
            int[] ids = new int[n]; var t0 = new BusType[n];
            var vm0 = new double[n]; var va0 = new double[n];
            var ps = new double[n]; var qs = new double[n]; var qd = new double[n];
            var vg = new double[n]; var qmn = new double[n]; var qmx = new double[n];
            for (int i = 0; i < n; i++)
            {
                ids[i] = model.Buses[i].Id; t0[i] = model.Buses[i].Type;
                vm0[i] = volt[i].Magnitude; va0[i] = DetMath.Phase(volt[i]);
                ps[i] = -sLoad[i].Real; qs[i] = -sLoad[i].Imaginary;
                // D-DOC-068 (October 2026, docs-session finding): the reactive load in MVAr, the unit both Newton engines
                // record (NewtonShell.BusSpec.Qd). It was written here in per unit of the base, so the solve export's
                // busSpec Qd meant MVAr on one engine and pu on another. Trace capture only: the sweep's own arithmetic
                // (sLoad above) is untouched, so no solved number changes.
                qd[i] = model.Buses[i].Qd;
            }
            trace.CaptureSpec(ids, t0, vm0, va0, ps, qs, qd, vg, qmn, qmx);
            trace.BeginPass(0, t0, System.Array.Empty<int>(), System.Array.Empty<int>());
        }

        int iter = 0;
        for (; iter < opt.MaxIterations; iter++)
        {
            // Cooperative cancellation every sweep (R036), as Newton checks every iteration: a deferred radial
            // solve must be able to stop at its cycle deadline. CancellationToken.None never throws.
            opt.Cancellation.ThrowIfCancellationRequested();
            Array.Copy(volt, vPrev, n);

            // Backward sweep: node currents (load/shunt + breakpoint injections), accumulated up the tree.
            var inode = new Complex[n];
            for (int i = 0; i < n; i++)
            {
                Complex v = volt[i];
                Complex vSafe = v.Magnitude < SafetyFloor ? new Complex(SafetyFloor, 0) : v;
                Complex draw = new(sLoad[i].Real, sLoad[i].Imaginary - qExtra[i]);
                if (zip[i] is { } z)
                {
                    // Voltage-dependent load (R065): add the deviation of the ZIP draw from nominal at the present
                    // swept |V|, the same deviation the Newton mismatch subtracts. Constant-power buses skip this,
                    // so their arithmetic is unchanged.
                    Bus b = model.Buses[i];
                    double vmag = v.Magnitude;
                    draw += new Complex(b.Pd * (z.PMultiplier(vmag) - 1.0), b.Qd * (z.QMultiplier(vmag) - 1.0)) / model.BaseMva;
                }
                inode[i] = Complex.Conjugate(draw / vSafe) + yShunt[i] * v;    // drawn: load + shunt-to-ground
            }
            for (int p = 0; p < nLoops; p++) { inode[loopFrom[p]] += loopI[p] / Complex.Conjugate(loopTap[p]); inode[loopTo[p]] -= loopI[p]; }

            // Forward sweep applies the series drop from the source outward.
            Sweep(inode, vslackC, volt, j);

            maxDelta = 0;
            for (int i = 0; i < n; i++)
            {
                double d = (volt[i] - vPrev[i]).Magnitude;
                if (d > maxDelta) maxDelta = d;
            }

            if (trace is not null)
            {
                var dv = new double[n];
                for (int i = 0; i < n; i++) dv[i] = (volt[i] - vPrev[i]).Magnitude;
                trace.CaptureIteration(iter, maxDelta, dv, System.Array.Empty<double>(), null);
            }

            // PV correction: nudge each regulating bus's reactive injection toward its |V| setpoint.
            bool pvOk = true;
            foreach (int bi in pvBuses)
            {
                double vmag = volt[bi].Magnitude;
                double err = pvVset[bi] - vmag;
                double qNow = pvQnom[bi] + qExtra[bi];
                // dV ~ (Xth/|V|)*dQ  =>  dQ ~ dV*|V|/Xth (injecting Q raises the voltage).
                double qWant = qNow + err * vmag / pvXth[bi];
                bool clamped = false;
                if (qWant > pvQmax[bi]) { qWant = pvQmax[bi]; clamped = true; }
                if (qWant < pvQmin[bi]) { qWant = pvQmin[bi]; clamped = true; }
                // A saturated PV bus acts as PQ at its limit - no voltage-hold requirement then.
                if (!clamped && Math.Abs(err) > ctrlTol) pvOk = false;
                qExtra[bi] = qWant - pvQnom[bi];
            }

            // Breakpoint correction: drive each loop's closure residual (V_from - V_to - z*I) to zero.
            bool loopOk = true;
            if (nLoops > 0)
            {
                var resid = new Complex[nLoops];
                for (int p = 0; p < nLoops; p++)
                {
                    resid[p] = (volt[loopFrom[p]] / loopTap[p] - volt[loopTo[p]]) - loopZ[p] * loopI[p];
                    if (resid[p].Magnitude > ctrlTol) loopOk = false;
                }
                Complex[] dI = SolveLinear(loopM, resid, nLoops);
                for (int p = 0; p < nLoops; p++) loopI[p] += dI[p];
            }

            if (maxDelta < opt.Tolerance && pvOk && loopOk) { converged = true; iter++; break; }
        }

        return BuildResult(model, br, volt, converged, iter, maxDelta);
    }

    private readonly record struct BranchModel(int F, int T, Complex Y, Complex Bsh, Complex Tap, Branch Src, int SrcIndex);

    /// <summary>Direct complex linear solve (Gauss-Jordan with partial pivoting) for the small
    /// breakpoint-impedance system. A near-singular pivot is skipped (its correction stays zero) so a
    /// degenerate loop set can't throw - it just declines that correction.</summary>
    private static Complex[] SolveLinear(Complex[,] a, Complex[] rhs, int m)
    {
        var A = (Complex[,])a.Clone();
        var b = (Complex[])rhs.Clone();
        for (int col = 0; col < m; col++)
        {
            int piv = col; double best = A[col, col].Magnitude;
            for (int r = col + 1; r < m; r++) { double mag = A[r, col].Magnitude; if (mag > best) { best = mag; piv = r; } }
            if (best < LoopPivotFloor) continue;
            if (piv != col)
            {
                for (int c = 0; c < m; c++) (A[col, c], A[piv, c]) = (A[piv, c], A[col, c]);
                (b[col], b[piv]) = (b[piv], b[col]);
            }
            Complex d = A[col, col];
            for (int r = 0; r < m; r++)
            {
                if (r == col) continue;
                Complex f = A[r, col] / d;
                if (f == Complex.Zero) continue;
                for (int c = col; c < m; c++) A[r, c] -= f * A[col, c];
                b[r] -= f * b[col];
            }
        }
        var x = new Complex[m];
        for (int i = 0; i < m; i++) { Complex d = A[i, i]; x[i] = d.Magnitude < LoopPivotFloor ? Complex.Zero : b[i] / d; }
        return x;
    }

    /// <summary>Assemble a full <see cref="PowerFlowResult"/> from the converged voltages, using the
    /// pi+tap branch stamps so flows/injections/losses match the Newton solvers' model.</summary>
    private static PowerFlowResult BuildResult(
        GridModel model, List<BranchModel> br,
        Complex[] v, bool converged, int iter, double maxDelta)
    {
        int n = model.BusCount;
        var index = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) index[model.Buses[i].Id] = i;

        var vm = new double[n];
        var vaDeg = new double[n];
        var iInj = new Complex[n];                         // net current injected into each bus
        for (int i = 0; i < n; i++) { vm[i] = v[i].Magnitude; vaDeg[i] = DetMath.Phase(v[i]) * 180.0 / Math.PI; }

        // LineFlows MUST stay index-aligned with model.Branches (NewtonShell
        // emits zero-flow placeholders for out-of-service branches for the
        // same reason): LiveSim/ScadaVm/PhysicsLaws pair LineFlows[i] with
        // Branches[i] positionally, and the in-service-only list misaligned
        // every consumer once a recloser held a branch open.
        var flowByBranch = new LineFlow?[model.Branches.Count];
        Complex totalLoss = Complex.Zero;
        foreach (BranchModel e in br)
        {
            double t2 = (e.Tap * Complex.Conjugate(e.Tap)).Real;
            Complex yff = (e.Y + e.Bsh) / t2;
            Complex yft = -e.Y / Complex.Conjugate(e.Tap);
            Complex ytf = -e.Y / e.Tap;
            Complex ytt = e.Y + e.Bsh;
            Complex iF = yff * v[e.F] + yft * v[e.T];
            Complex iT = ytf * v[e.F] + ytt * v[e.T];
            iInj[e.F] += iF; iInj[e.T] += iT;
            Complex sF = v[e.F] * Complex.Conjugate(iF) * model.BaseMva;
            Complex sT = v[e.T] * Complex.Conjugate(iT) * model.BaseMva;
            Complex loss = sF + sT;
            totalLoss += loss;
            double rate = e.Src.RateAMva;
            double load = rate > 0 ? Math.Max(sF.Magnitude, sT.Magnitude) / rate * 100.0 : 0;
            flowByBranch[e.SrcIndex] = new LineFlow(model.Buses[e.F].Id, model.Buses[e.T].Id,
                sF.Real, sF.Imaginary, sT.Real, sT.Imaginary, loss.Real, loss.Imaginary,
                // GR-500: an unnamed branch keeps an empty name, as in the Newton engines and this engine's own
                // out-of-service placeholders below. It used to be named "{from}-{to}", so a consumer keying on the
                // name saw a different label by engine, and the made-up name could equal a real branch's.
                e.Src.Name ?? "", rate, load);
        }
        var flows = new List<LineFlow>(model.Branches.Count);
        for (int bi = 0; bi < model.Branches.Count; bi++)
        {
            Branch src = model.Branches[bi];
            flows.Add(flowByBranch[bi]
                ?? new LineFlow(src.From, src.To, 0, 0, 0, 0, 0, 0, src.Name ?? "", src.RateAMva, 0));
        }

        // Add bus shunts (Gs/Bs) to the injection current, then net injection S = V*conj(Iinj).
        var pInj = new double[n];
        var qInj = new double[n];
        double maxMis = 0;
        for (int i = 0; i < n; i++)
        {
            Bus b = model.Buses[i];
            Complex iShunt = new Complex(b.Gs, b.Bs) / model.BaseMva * v[i];
            iInj[i] += iShunt;
            // The bus shunt is part of the network loss, exactly as in the Newton engines (which sum every net
            // injection): it consumes Gs*|V|^2 MW and -Bs*|V|^2 MVAr (a capacitor bank, Bs > 0, supplies MVAr).
            // Without this the sweep's totals were the branch losses only, so TotalLossMw != sum(PInjMw) on any
            // feeder with a shunt and PhysicsLaws failed a correct solve. Only a bus that has a shunt adds a term,
            // so a shunt-free feeder's totals are bit-for-bit what they were.
            if (b.Gs != 0.0 || b.Bs != 0.0)
                totalLoss += v[i] * Complex.Conjugate(iShunt) * model.BaseMva;
            Complex s = v[i] * Complex.Conjugate(iInj[i]) * model.BaseMva;   // generation - demand, MW/MVAr
            pInj[i] = s.Real; qInj[i] = s.Imaginary;
            if (b.Type != BusType.Slack)
            {
                // Power mismatch vs the specified injection (gen - load), p.u. A voltage-regulating PV
                // bus solves for its own reactive output (like the slack), so its Q isn't a spec to
                // check against - only the real-power balance is.
                bool pvRegulated = b.Type == BusType.Pv &&
                    model.Generators.Any(g => index.TryGetValue(g.Bus, out int gi) && gi == i);
                // The load drawn at the solved voltage (exactly Pd/Qd for a constant-power bus; R065).
                (double pLoad, double qLoad) = LoadAtVoltage.Mw(b, vm[i]);
                double dp = Math.Abs(s.Real - (SpecGenP(model, index, i) - pLoad));
                double mis = dp;
                if (!pvRegulated)
                    mis = Math.Max(dp, Math.Abs(s.Imaginary - (SpecGenQ(model, index, i) - qLoad)));
                maxMis = Math.Max(maxMis, mis / model.BaseMva);
            }
        }

        // A NaN/Inf in the swept voltages (e.g. a collapsed radial feeder driving |V|->0) invalidates the
        // whole state - never report it as converged. Mirror the Newton solvers' NonFinite contract.
        bool finite = double.IsFinite(maxMis);
        for (int i = 0; i < n && finite; i++)
            finite = double.IsFinite(vm[i]) && double.IsFinite(vaDeg[i]) && double.IsFinite(pInj[i]) && double.IsFinite(qInj[i]);
        PowerFlowStatus status = !finite ? PowerFlowStatus.NonFinite
                               : converged ? PowerFlowStatus.Converged
                               : PowerFlowStatus.MaxIterations;

        return new PowerFlowResult
        {
            Converged = status == PowerFlowStatus.Converged,
            Status = status,
            Iterations = iter,
            MaxMismatch = converged ? maxMis : Math.Max(maxMis, maxDelta),
            // D-DOC-150: the quantity the convergence test above compared with the tolerance, so a report can label the
            // result by the test that decided it (the sweep tests the voltage update, not the power mismatch).
            SweepVoltageStepPu = maxDelta,
            BusIds = model.Buses.Select(b => b.Id).ToArray(),
            Vm = vm,
            VaDeg = vaDeg,
            PInjMw = pInj,
            QInjMvar = qInj,
            TotalLossMw = totalLoss.Real,
            TotalLossMvar = totalLoss.Imaginary,
            LineFlows = flows,
        };
    }

    private static double SpecGenP(GridModel m, Dictionary<int, int> index, int busIdx)
        => m.Generators.Where(g => index.TryGetValue(g.Bus, out int gi) && gi == busIdx).Sum(g => g.Pg);
    private static double SpecGenQ(GridModel m, Dictionary<int, int> index, int busIdx)
        => m.Generators.Where(g => index.TryGetValue(g.Bus, out int gi) && gi == busIdx).Sum(g => g.Qg);

    private static PowerFlowResult NonRadial(GridModel model, string why)
    {
        int n = model.BusCount;
        return new PowerFlowResult
        {
            Converged = false,
            // Status defaults to Converged, which would suppress WatchList's non-convergence alarm and
            // print "Converged" for this failed solve. Flag it as an invalid (never-converged) result.
            Status = PowerFlowStatus.NonFinite,
            Iterations = 0,
            MaxMismatch = double.PositiveInfinity,
            BusIds = model.Buses.Select(b => b.Id).ToArray(),
            Vm = Enumerable.Repeat(1.0, n).ToArray(),
            VaDeg = new double[n],
            PInjMw = new double[n],
            QInjMvar = new double[n],
            TotalLossMw = 0,
            TotalLossMvar = 0,
            // Zero-flow placeholders, one per branch, so LineFlows stays index-aligned with model.Branches even on
            // this failure path (R036): consumers pair LineFlows[i] with Branches[i] positionally, and an empty
            // list broke that alignment for every caller that did not check Converged first.
            LineFlows = model.Branches
                .Select(br => new LineFlow(br.From, br.To, 0, 0, 0, 0, 0, 0, br.Name ?? "", br.RateAMva, 0))
                .ToList(),
        };
    }
}
