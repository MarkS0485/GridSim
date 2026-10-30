using System.Numerics;
using GridSim.Core.Abstractions;
using GridSim.Core.Model;

namespace GridSim.Core.PowerFlow;

/// <summary>
/// Backend-agnostic helpers shared by the dense (<see cref="NewtonRaphsonPowerFlow"/>)
/// and sparse (<see cref="SparseNewtonRaphsonPowerFlow"/>) polar Newton-Raphson solvers.
///
/// These cover the parts of a power-flow solve that do not touch the Jacobian/LU
/// representation: aggregating generator/load injections onto buses (the P/Q specs and
/// the flat/warm start), the reactive-power outer-loop bookkeeping that switches a
/// PV bus to PQ when its generators hit a Q-limit, assembling the
/// <see cref="PowerFlowResult"/> (injections, losses, line flows) - and the Newton step
/// itself: the shared unknown numbering, the canonical linear solve and the damped update.
/// Only the Y-bus, injection and Jacobian ASSEMBLY stay in the two solver files.
///
/// Every helper is a pure function of its inputs, and assembly is exact (same products, same
/// summation order), so on the CPU the two solvers produce bit-for-bit identical results.
/// <c>DenseSparseParityTests</c> pins that on every bundled case the dense engine can hold.
/// </summary>
internal static class NewtonShell
{
    /// <summary>Slack in MVA when comparing a PV bus's reactive output to its generator Q-limits.</summary>
    public const double QLimitTolMva = 1e-3;

    /// <summary>Voltage deadband (pu) for deciding a pinned bus has clearly overshot its setpoint
    /// and should be reverted PQ-&gt;PV. Kept well above solver tolerance so numerical noise at a
    /// correctly-limited bus (Vm essentially equal to Vg) never triggers a spurious revert.</summary>
    public const double RevertVoltTolPu = 1e-4;

    /// <summary>Largest Newton correction (radians for an angle, p.u. for a magnitude) applied undamped. A
    /// bigger step - typically the first one from a flat start on a large stiff network - is scaled down so
    /// its largest component is exactly this, which stops it overshooting into a negative-voltage region.
    /// Near the solution every step is tiny, the scale is 1 and Newton keeps its quadratic convergence, so
    /// the damping changes the path to the root, never the root itself.</summary>
    public const double MaxUndampedStep = 0.4;

    /// <summary>Floor (p.u.) on a PQ bus voltage magnitude after a Newton update. A magnitude at or below zero
    /// has no physical meaning and makes the polar equations degenerate (dP/dV terms divide by Vm), so an
    /// overshooting iterate is held here and the next step corrects it. Never active at a converged point.</summary>
    public const double VmFloorPu = 0.2;

    /// <summary>
    /// THE canonical Newton step, shared by the dense and sparse engines so they are bit-identical
    /// (Mark's ruling 2, October 2026). Solves <c>J dx = f</c> with the CPU direct sparse LU (reverse
    /// Cuthill-McKee column order, partial pivoting); if that reports the Jacobian singular, retries with
    /// ILU(0)-preconditioned BiCGSTAB. Returns <c>false</c> - stop, report non-convergence - when neither
    /// produces a step or the step is not finite: a NaN/Inf step must never be applied.
    ///
    /// <para>Why one solver for both engines: two correct LU factorisations (dense row-major elimination
    /// and sparse left-looking elimination) round differently, so each step - and so the converged
    /// voltages - differed in the last bits (about 1e-14 measured on case300 / case1354pegase). Neither
    /// rounding is "more correct": both are backward stable. The sparse one is the only elimination order
    /// that also runs at gb-full scale, so it is the canonical one.</para>
    ///
    /// <para><paramref name="dx"/> is the output only. Whatever it holds on entry (the caller's previous step) is
    /// never used as a starting guess: the direct LU ignores it, and <c>BiCgStab.Solve</c> clears it to zero before
    /// its first iteration, so the BiCGSTAB fallback always starts from zero. <paramref name="maxAbs"/> returns the
    /// largest step component, for the damping rule.</para>
    ///
    /// <para><paramref name="directFallback"/>: when the primary backend is NOT the CPU direct LU (a GPU/ROCm
    /// iterative solver, which returns null on breakdown, stagnation or non-convergence), a null from it is
    /// first retried on this CPU direct LU - exactly what those backends document - before the ILU0/BiCGSTAB
    /// fallback. Before October 2026 (R038) a GPU failure went straight to ILU0/BiCGSTAB and the exact solver
    /// was never tried. Pass null on the CPU path (the primary already is the direct LU).</para>
    /// </summary>
    public static bool SolveStep(
        ISparseLinearSolver sparse, Numerics.CsrMatrix jac, double[] f, double[] dx, PowerFlowOptions opt,
        out double maxAbs, CpuSparseLuSolver? directFallback = null)
    {
        int m = f.Length;
        maxAbs = 0;

        // A NaN in the Jacobian (a stored Vm of 0 at a PQ bus gives P/Vm = 0/0 on the first iteration, or a non-finite
        // start) cannot be solved: the step reports failure and the engine stops as not converged, exactly as for a
        // singular matrix. The sparse LU refuses NaN with an ArgumentException, which used to escape both engines as an
        // unhandled error instead of a status.
        // GR-479: an infinite entry likewise (the sparse LU now refuses it too); the step fails as it always did, through
        // the LU's null and the Krylov fallback's failure before.
        foreach (double jv in jac.Val)
            if (!double.IsFinite(jv)) return false;
        // Bug #40: a non-finite mismatch (a NaN load, say) likewise. The direct solvers now refuse a non-finite right-hand
        // side by name; before, it came back as a non-finite correction, which the check at the end of this method refused,
        // so the step fails here exactly as it failed there (a non-finite right-hand side always gives a non-finite
        // solution entry) and the engine reports the same status.
        foreach (double fv in f)
            if (!double.IsFinite(fv)) return false;

        // Sparse direct LU (exact); fall back to ILU0/BiCGSTAB only if it reports singular.
        double[]? sol = sparse.Solve(jac, f);
        if (sol is null && directFallback is not null && !ReferenceEquals(directFallback, sparse))
            sol = directFallback.Solve(jac, f);   // R038: the GPU solver gave up; try the exact CPU LU first
        if (sol is null)
        {
            Numerics.Ilu0 precond = Numerics.Ilu0.Factor(jac);
            if (!Numerics.BiCgStab.Solve(jac, f, dx, precond, tol: Math.Min(1e-10, opt.Tolerance * 1e-3), maxIter: Numerics.NumericTolerances.DefaultKrylovMaxIterations(m)))   // N008: the shared cap (4m + 200)
                return false; // cannot solve this step; the caller stops and reports non-convergence
        }
        else
        {
            Array.Copy(sol, dx, m);
        }

        // Fail loud on a garbage step rather than applying it (that diverges, or worse, "converges" on NaN).
        for (int k = 0; k < m; k++)
        {
            double v = dx[k];
            if (!double.IsFinite(v)) return false;
            double av = Math.Abs(v);
            if (av > maxAbs) maxAbs = av;
        }
        return true;
    }

    /// <summary>
    /// THE canonical Newton update, shared by both engines: apply step <paramref name="dx"/> to the angles of
    /// every non-slack bus (equation positions <paramref name="pPos"/>) and the magnitudes of every PQ bus
    /// (<paramref name="qPos"/>), damped by <see cref="MaxUndampedStep"/> and floored at <see cref="VmFloorPu"/>.
    /// Bus order (ascending index) and the exact arithmetic are part of the bit-identity contract.
    /// </summary>
    public static void ApplyStep(int n, int[] pPos, int[] qPos, double[] dx, double maxAbs, double[] vm, double[] va)
    {
        double damp = maxAbs > MaxUndampedStep ? MaxUndampedStep / maxAbs : 1.0;
        for (int i = 0; i < n; i++)
        {
            if (pPos[i] >= 0) va[i] += damp * dx[pPos[i]];
            if (qPos[i] >= 0) { vm[i] += damp * dx[qPos[i]]; if (vm[i] < VmFloorPu) vm[i] = VmFloorPu; }
        }
    }

    /// <summary>Equation/unknown positions for one Q-limit pass: the P equation (angle unknown) of every
    /// non-slack bus in ascending bus order, then the Q equation (magnitude unknown) of every PQ bus in
    /// ascending bus order. Returns the system size m. Both engines number the system this way, which is
    /// what lets them share one Jacobian sparsity pattern and one linear solve.</summary>
    public static int EquationPositions(BusType[] type, int[] pPos, int[] qPos)
    {
        int n = type.Length;
        Array.Fill(pPos, -1);
        Array.Fill(qPos, -1);
        int m = 0;
        for (int i = 0; i < n; i++) if (type[i] != BusType.Slack) pPos[i] = m++;
        for (int i = 0; i < n; i++) if (type[i] == BusType.Pq) qPos[i] = m++;
        return m;
    }

    /// <summary>The Q-limit outer-loop pass cap, validated: at least one pass is needed to solve anything (a cap
    /// of 0 used to skip the solve entirely and assemble the start point as the result).</summary>
    public static int QLimitPasses(PowerFlowOptions opt)
    {
        if (opt.MaxQLimitPasses < 1)
            throw new ArgumentOutOfRangeException(nameof(opt), opt.MaxQLimitPasses,
                "PowerFlowOptions.MaxQLimitPasses must be at least 1 when EnforceQLimits is on.");
        return opt.MaxQLimitPasses;
    }

    /// <summary>Per-bus aggregates and start values needed to drive a Newton solve.</summary>
    public sealed class BusSpec
    {
        public BusSpec(int n)
        {
            Type0 = new BusType[n];
            Vm = new double[n];
            Va = new double[n];
            Pspec = new double[n];
            Qspec = new double[n];
            Qd = new double[n];
            Pd = new double[n];
            Zip = new ZipLoad?[n];
            QMinBus = new double[n];
            QMaxBus = new double[n];
            Vg = new double[n];
        }

        /// <summary>Original (pre-outer-loop) bus types.</summary>
        public BusType[] Type0 { get; }
        /// <summary>Voltage magnitudes (pu), seeded from generator setpoints / flat / warm start.</summary>
        public double[] Vm { get; }
        /// <summary>Voltage angles (radians).</summary>
        public double[] Va { get; }
        /// <summary>Specified real-power injection (pu).</summary>
        public double[] Pspec { get; }
        /// <summary>Specified reactive-power injection (pu).</summary>
        public double[] Qspec { get; }
        /// <summary>Reactive load per bus (MVAr), retained for Q-limit recovery.</summary>
        public double[] Qd { get; }
        /// <summary>Real load per bus (MW), retained for the voltage-dependent (ZIP) load correction.</summary>
        public double[] Pd { get; }
        /// <summary>Per-bus voltage-dependent load model (null = constant power).</summary>
        public ZipLoad?[] Zip { get; }
        /// <summary>True when at least one bus carries a voltage-varying ZIP load - the gate that keeps the
        /// constant-power path (every existing case) on exactly the historical code.</summary>
        public bool HasZip { get; set; }
        /// <summary>Aggregate generator Qmin per bus (MVAr).</summary>
        public double[] QMinBus { get; }
        /// <summary>Aggregate generator Qmax per bus (MVAr).</summary>
        public double[] QMaxBus { get; }
        /// <summary>Generator voltage setpoint per bus (pu); used to restore Vm on a PQ-&gt;PV revert.</summary>
        public double[] Vg { get; }
    }

    /// <summary>The voltage-dependent (ZIP) load DEVIATION from nominal at bus <paramref name="i"/>, in p.u.
    /// on the system base: <c>(Pload(V) - Pd)/base</c>, <c>(Qload(V) - Qd)/base</c>. These are subtracted from
    /// the P/Q mismatch. Zero at V = 1 and for any constant-power bus (Zip == null).</summary>
    public static (double DP, double DQ) ZipDeviationPu(BusSpec spec, int i, double vm, double baseMva)
    {
        ZipLoad? zip = spec.Zip[i];
        if (zip is null) return (0.0, 0.0);
        return (spec.Pd[i] * (zip.PMultiplier(vm) - 1.0) / baseMva,
                spec.Qd[i] * (zip.QMultiplier(vm) - 1.0) / baseMva);
    }

    /// <summary>The ZIP load's contribution to the Jacobian diagonal at bus <paramref name="i"/>:
    /// <c>d(Pload/base)/dV</c> adds to N_ii (dP/dV), <c>d(Qload/base)/dV</c> adds to L_ii (dQ/dV). Zero for a
    /// constant-power bus, so the Jacobian is unchanged unless a bus genuinely varies with voltage.</summary>
    public static (double DPdV, double DQdV) ZipJacobianDiagPu(BusSpec spec, int i, double vm, double baseMva)
    {
        ZipLoad? zip = spec.Zip[i];
        if (zip is null) return (0.0, 0.0);
        return (spec.Pd[i] * zip.DPMultiplier(vm) / baseMva,
                spec.Qd[i] * zip.DQMultiplier(vm) / baseMva);
    }

    /// <summary>Which Q-limit a bus was pinned at when it was switched PV-&gt;PQ (for revert bookkeeping).</summary>
    public enum QPin
    {
        /// <summary>Not pinned by the outer loop (never converted, or reverted).</summary>
        None = 0,
        /// <summary>Pinned at aggregate Qmax (over-excited limit).</summary>
        Max = 1,
        /// <summary>Pinned at aggregate Qmin (under-excited limit).</summary>
        Min = -1,
    }

    /// <summary>
    /// Aggregates generators and loads onto buses and seeds the start point. The
    /// <paramref name="indexOf"/> map resolves a bus id to its 0-based solver index
    /// (dense passes <c>YBus.IndexOf</c>, sparse passes its own dictionary lookup).
    /// </summary>
    public static BusSpec BuildBusSpec(GridModel model, int n, Func<int, int> indexOf, PowerFlowOptions opt)
    {
        var spec = new BusSpec(n);

        var pgByBus = new double[n];
        var qgByBus = new double[n];
        var vgByBus = new double[n];
        var hasGen = new bool[n];
        foreach (Generator g in model.Generators)
        {
            int i = indexOf(g.Bus);
            pgByBus[i] += g.Pg;
            qgByBus[i] += g.Qg;
            spec.QMinBus[i] += g.Qmin;
            spec.QMaxBus[i] += g.Qmax;
            vgByBus[i] = g.Vg;   // the shared setpoint rule (VoltageSetpoint): the last generator at the bus wins
            hasGen[i] = true;
        }

        double @base = model.BaseMva;
        for (int i = 0; i < n; i++)
        {
            Bus bus = model.Buses[i];
            // A bus typed PV with no generator on it has nothing to hold its voltage: solve it as PQ (R283, October
            // 2026), as MATPOWER's bustypes() and the backward/forward sweep already do. Left as PV it was held at
            // its stored Vm with unlimited reactive power (a phantom generator) when Q limits were off, and with
            // them on it was pinned at Qmin = Qmax = 0 and could then be "reverted" to PV with Vm = Vg = 0, a
            // zero-voltage bus that made the next solve non-finite.
            spec.Type0[i] = bus.Type == BusType.Pv && !hasGen[i] ? BusType.Pq : bus.Type;
            bool vc = spec.Type0[i] is BusType.Slack or BusType.Pv;
            spec.Vg[i] = vgByBus[i];
            // GR-046: a slack bus's voltage magnitude is a specification, like its angle two lines down, so a slack
            // with no generator holds its stored Vm whatever FlatStart says (as the backward/forward sweep always did).
            // It used to take 1.0 under FlatStart, so the same model solved to two different states by start option.
            spec.Vm[i] = vc && hasGen[i] ? vgByBus[i]
                       : spec.Type0[i] == BusType.Slack ? bus.Vm
                       : opt.FlatStart ? 1.0 : bus.Vm;
            if (spec.Type0[i] == BusType.Slack && !hasGen[i] && !(bus.Vm > 0))
                throw new InvalidOperationException(
                    $"Bus {bus.Id} is a slack bus with no generator and a stored Vm of {bus.Vm} pu: its stored Vm is its voltage set point and must be above 0 (GR-046).");
            spec.Va[i] = bus.Type == BusType.Slack ? bus.VaDeg * Math.PI / 180.0
                       : opt.FlatStart ? 0.0 : bus.VaDeg * Math.PI / 180.0;
            spec.Qd[i] = bus.Qd;
            spec.Pd[i] = bus.Pd;
            // Pspec/Qspec hold the NOMINAL net injection (generation minus 1.0-pu load), exactly as before.
            // A voltage-varying ZIP load is applied as a deviation from nominal in the solver (zero at V=1
            // and identically zero for constant-power buses), so this stays bit-for-bit unchanged.
            spec.Pspec[i] = (pgByBus[i] - bus.Pd) / @base;
            spec.Qspec[i] = (qgByBus[i] - bus.Qd) / @base;
            if (bus.Zip is { VariesWithVoltage: true } zip) { spec.Zip[i] = zip; spec.HasZip = true; }
        }

        return spec;
    }

    /// <summary>
    /// One pass of the reactive-power outer loop.
    ///
    /// <para><b>Forward (always, unchanged):</b> for each bus that started as PV and is still PV,
    /// if the generators' reactive output has crossed a Q-limit, pin the output at the limit and
    /// release the bus to PQ (its <paramref name="qspec"/> is fixed at the limit). The limit it was
    /// pinned at is recorded in <paramref name="pinned"/>.</para>
    ///
    /// <para><b>Revert (opt-in, <paramref name="revert"/> = true):</b> a bus already switched to PQ
    /// is put back to PV when the pinned reactive output is no longer the binding constraint  -
    /// detected from the solved voltage overshooting the setpoint. A gen pinned at <b>Qmax</b> is
    /// injecting the maximum reactive it can; if that is <em>more</em> than needed to hold the
    /// setpoint the extra VARs push the bus voltage <em>above</em> <paramref name="vg"/>, so
    /// <c>Vm &gt; Vg</c> means the max limit is not actually binding and the bus should hold its
    /// setpoint again (revert to PV, restore Vm = Vg). Symmetrically a gen pinned at <b>Qmin</b>
    /// that leaves <c>Vm &lt; Vg</c> is absorbing more than needed and reverts.</para>
    ///
    /// <para>Note: this is the physically-consistent direction for the standard polar NR equations
    /// used here (more injected Q raises local voltage). It is the inverse of the wording in some
    /// informal descriptions that phrase the trigger as "the bus still wants more VARs" - a bus
    /// that wants more VARs while pinned at Qmax is correctly limited and must stay PQ, otherwise
    /// reverting it would let Q run past the enforced limit.</para>
    ///
    /// <para><b>Anti-oscillation:</b> a bus may be reverted at most once per solve
    /// (<paramref name="revertCount"/> capped at 1). If a reverted bus re-violates its limit the
    /// forward rule simply re-pins it and it then stays pinned  -  so the outer loop is guaranteed
    /// to terminate (bounded also by <see cref="PowerFlowOptions.MaxQLimitPasses"/>), and the final
    /// state always respects the enforced limits.</para>
    ///
    /// Returns whether any bus switched, so the caller knows to re-solve.
    /// </summary>
    public static bool ApplyQLimits(
        int n, BusType[] type0, BusType[] effType,
        double[] q, double[] qd, double[] qMinBus, double[] qMaxBus, double[] qspec, double @base,
        double[] vm, double[] vg, QPin[] pinned, int[] revertCount, bool revert, ZipLoad?[]? zip = null)
    {
        bool changed = false;
        for (int i = 0; i < n; i++)
        {
            if (type0[i] != BusType.Pv) continue;

            if (effType[i] == BusType.Pv)
            {
                // Reactive the generators are producing: the net injection plus the load ACTUALLY drawn at the
                // solved voltage (R065). For a constant-power bus that is exactly qd[i], as it always was; a
                // voltage-dependent ZIP load draws Qd*QMultiplier(V), and measuring against the nominal Qd
                // mis-read the generators' output by Qd*(QMultiplier(V)-1).
                double qGen = q[i] * @base + LoadAtVoltage.QMvar(qd[i], zip?[i], vm[i]);
                if (qGen > qMaxBus[i] + QLimitTolMva)
                {
                    effType[i] = BusType.Pq;
                    qspec[i] = (qMaxBus[i] - qd[i]) / @base;
                    pinned[i] = QPin.Max;
                    changed = true;
                }
                else if (qGen < qMinBus[i] - QLimitTolMva)
                {
                    effType[i] = BusType.Pq;
                    qspec[i] = (qMinBus[i] - qd[i]) / @base;
                    pinned[i] = QPin.Min;
                    changed = true;
                }
            }
            else if (revert && pinned[i] != QPin.None && revertCount[i] < 1)
            {
                // Pinned-PQ bus: revert to PV if its solved voltage shows the limit isn't binding.
                bool revertMax = pinned[i] == QPin.Max && vm[i] > vg[i] + RevertVoltTolPu;
                bool revertMin = pinned[i] == QPin.Min && vm[i] < vg[i] - RevertVoltTolPu;
                if (revertMax || revertMin)
                {
                    effType[i] = BusType.Pv;
                    vm[i] = vg[i];      // re-impose the voltage setpoint as the start point
                    pinned[i] = QPin.None;
                    revertCount[i]++;
                    changed = true;
                }
            }
        }
        return changed;
    }

    /// <summary>
    /// Slack-generator reactive diagnostic. The slack bus's reactive injection is whatever the
    /// network needs and is never limited during the solve, so it can silently exceed the
    /// aggregate Qmin/Qmax of the machines nominally sitting on it. This reports that condition
    /// without touching the solution. Both engines call it after EVERY solve, converged or not, so on a
    /// non-converged solve the note describes the last iterate, not a solution; read it alongside the status  -  <b>reassigning the slack /
    /// distributed-slack is deliberately out of scope</b>. Returns a human-readable note when the
    /// aggregate limit is breached, else <c>null</c>.
    /// </summary>
    public static string? DiagnoseSlackQ(
        int n, BusType[] type0, double[] q, double[] qd, double[] qMinBus, double[] qMaxBus, double @base,
        double[]? vm = null, ZipLoad?[]? zip = null)
    {
        if (zip is not null && vm is null)
            throw new ArgumentException("DiagnoseSlackQ needs the solved voltages to evaluate a ZIP load.", nameof(vm));
        for (int i = 0; i < n; i++)
        {
            if (type0[i] != BusType.Slack) continue;
            // Only meaningful if machines with real limits sit on the slack bus.
            double qMin = qMinBus[i], qMax = qMaxBus[i];
            if (qMin == 0 && qMax == 0) continue;
            // Reactive the slack machines are producing (load at the solved voltage, R065; exactly qd[i] for a
            // constant-power bus).
            double qGen = q[i] * @base + (zip is null ? qd[i] : LoadAtVoltage.QMvar(qd[i], zip[i], vm![i]));
            if (qGen > qMax + QLimitTolMva)
                return FormattableString.Invariant($"Slack bus {i}: reactive output {qGen:F2} MVAr exceeds aggregate Qmax {qMax:F2} MVAr.");
            if (qGen < qMin - QLimitTolMva)
                return FormattableString.Invariant($"Slack bus {i}: reactive output {qGen:F2} MVAr below aggregate Qmin {qMin:F2} MVAr.");
        }
        return null;
    }

    /// <summary>
    /// Assembles the final result from a solved point. The per-unit injections
    /// <paramref name="p"/>/<paramref name="q"/> must already have been recomputed by the
    /// caller's backend at the converged voltages. <paramref name="indexOf"/> resolves bus
    /// ids for the line-flow calculation. <paramref name="solvedTypes"/> (N071) is the bus types the Newton solve ended
    /// with, indexed like <paramref name="vm"/> (model order); copied into <see cref="PowerFlowResult.SolvedBusTypes"/>.
    /// Null (the estimation projection) leaves that property null. It changes no number.
    /// </summary>
    public static PowerFlowResult AssembleResult(
        GridModel model, int n, double[] vm, double[] va, double[] p, double[] q, double @base,
        bool converged, int totalIters, double maxMis, Func<int, int> indexOf, BusType[]? solvedTypes = null)
    {
        var vaDeg = new double[n];
        var pInj = new double[n];
        var qInj = new double[n];
        double lossP = 0, lossQ = 0;
        bool finite = double.IsFinite(maxMis);
        for (int i = 0; i < n; i++)
        {
            vaDeg[i] = va[i] * 180.0 / Math.PI;
            pInj[i] = p[i] * @base;
            qInj[i] = q[i] * @base;
            lossP += pInj[i];
            lossQ += qInj[i];
            finite &= double.IsFinite(vm[i]) & double.IsFinite(va[i]) & double.IsFinite(pInj[i]) & double.IsFinite(qInj[i]);
        }

        // A non-finite state can never be "converged": a NaN mismatch fails the `< tol` test in the loop, but
        // guard here too so a stale/underflowed mismatch can't smuggle a NaN solution out as a good answer.
        PowerFlowStatus status = !finite ? PowerFlowStatus.NonFinite
                               : converged ? PowerFlowStatus.Converged
                               : PowerFlowStatus.MaxIterations;
        bool trustworthy = status == PowerFlowStatus.Converged;

        return new PowerFlowResult
        {
            Converged = trustworthy,
            Status = status,
            Iterations = totalIters,
            MaxMismatch = maxMis,
            BusIds = model.Buses.Select(b => b.Id).ToArray(),
            Vm = vm,
            VaDeg = vaDeg,
            PInjMw = pInj,
            QInjMvar = qInj,
            TotalLossMw = lossP,
            TotalLossMvar = lossQ,
            LineFlows = ComputeLineFlows(model, indexOf, vm, va),
            SolvedBusTypes = solvedTypes is null ? null : (BusType[])solvedTypes.Clone(),
        };
    }

    /// <summary>
    /// Per-branch complex power flows and loading, computed from the pi-model admittances.
    /// Identical for both backends; only the bus-id resolution differs, via <paramref name="indexOf"/>.
    /// </summary>
    public static IReadOnlyList<LineFlow> ComputeLineFlows(GridModel model, Func<int, int> indexOf, double[] vm, double[] va)
    {
        var flows = new List<LineFlow>(model.Branches.Count);
        foreach (Branch br in model.Branches)
        {
            if (!br.InService)
            {
                // Out of service: carries no flow, but keep the entry so LineFlows stays index-aligned with Branches.
                flows.Add(new LineFlow(br.From, br.To, 0, 0, 0, 0, 0, 0, br.Name ?? "", br.RateAMva, 0));
                continue;
            }
            int f = indexOf(br.From);
            int t = indexOf(br.To);

            Complex vf = DetMath.FromPolar(vm[f], va[f]);
            Complex vt = DetMath.FromPolar(vm[t], va[t]);

            BranchStamp s = AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, br.PhaseShiftDeg);

            Complex iF = s.Yff * vf + s.Yft * vt;
            Complex iT = s.Ytf * vf + s.Ytt * vt;
            Complex sF = vf * Complex.Conjugate(iF) * model.BaseMva;
            Complex sT = vt * Complex.Conjugate(iT) * model.BaseMva;

            double mva = Math.Max(sF.Magnitude, sT.Magnitude);
            double loading = br.RateAMva > 0 ? mva / br.RateAMva * 100.0 : 0.0;

            flows.Add(new LineFlow(
                br.From, br.To,
                sF.Real, sF.Imaginary,
                sT.Real, sT.Imaginary,
                sF.Real + sT.Real, sF.Imaginary + sT.Imaginary,
                br.Name ?? "", br.RateAMva, loading));
        }
        return flows;
    }
}
