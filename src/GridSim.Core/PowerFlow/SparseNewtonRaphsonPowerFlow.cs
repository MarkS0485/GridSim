using System.Numerics;
using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.Numerics;

namespace GridSim.Core.PowerFlow;

/// <summary>
/// The same polar Newton-Raphson power flow as <see cref="NewtonRaphsonPowerFlow"/>,
/// but assembled entirely sparsely: a sparse complex Y-bus and a sparse Jacobian built by
/// walking each bus's neighbours. Each Newton step is solved by the direct sparse LU
/// (reverse Cuthill-McKee ordering, partial pivoting; ILU(0)/BiCGSTAB only if the LU reports
/// singular). Memory is O(nnz), which is what lets the full node-level GDA replica  -  tens
/// of thousands of buses  -  reach steady state, where the dense O(n^2)-memory path cannot.
///
/// On the CPU it is BIT-IDENTICAL to the dense engine on every case (Mark's ruling 2,
/// October 2026): both run the one canonical step in <see cref="NewtonShell.SolveStep"/> /
/// <see cref="NewtonShell.ApplyStep"/>, and differ only in exact assembly arithmetic.
/// <c>DenseSparseParityTests</c> pins it.
/// </summary>
public sealed class SparseNewtonRaphsonPowerFlow : IPowerFlowSolver
{
    private readonly ISparseLinearSolver _sparse;

    /// <summary>The CPU direct LU a non-CPU backend falls back to when it returns null (R038); null when the
    /// primary backend already is the CPU direct LU. One instance per engine, so its RCM-ordering cache works.</summary>
    private readonly CpuSparseLuSolver? _directFallback;

    /// <summary>The sparse linear backend defaults to the CPU direct LU; pass a GPU implementation
    /// to run each Newton step's solve on the device (a step the device cannot solve is retried on the CPU
    /// direct LU before the iterative fallback).</summary>
    public SparseNewtonRaphsonPowerFlow(ISparseLinearSolver? sparseLinearSolver = null)
    {
        _sparse = sparseLinearSolver ?? new CpuSparseLuSolver();
        _directFallback = _sparse is CpuSparseLuSolver ? null : new CpuSparseLuSolver();
    }

    /// <summary>
    /// Set by the most recent <see cref="Solve"/> when the slack bus's solved reactive injection
    /// exceeded its generators' aggregate Qmin/Qmax (see <see cref="NewtonShell.DiagnoseSlackQ"/>).
    /// Diagnostic only  -  the slack is never reassigned. <c>null</c> when within limits.
    /// </summary>
    public string? SlackQNote { get; private set; }

    /// <summary>Convenience flag mirroring <see cref="SlackQNote"/> being non-null.</summary>
    public bool SlackQViolated => SlackQNote is not null;

    public PowerFlowResult Solve(GridModel model, PowerFlowOptions? options = null)
    {
        // HVDC links (LCC, VSC): the AC solve below alternates with their DC solves (HvdcCoupling). A model with none
        // takes the AC solve directly, exactly as before.
        if (model.HasCoupledDevices) return HvdcCoupling.Solve(model, options, SolveAc);
        return SolveAc(model, options);
    }

    private PowerFlowResult SolveAc(GridModel model, PowerFlowOptions? options)
    {
        var opt = options ?? new PowerFlowOptions();
        int n = model.BusCount;
        var index = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) index[model.Buses[i].Id] = i;

        ComplexCsr y = BuildY(model, index);

        // Per-bus aggregates and start point (shared with the dense solver).
        double @base = model.BaseMva;
        NewtonShell.BusSpec spec = NewtonShell.BuildBusSpec(model, n, id => index[id], opt);
        BusType[] type0 = spec.Type0;
        double[] Vm = spec.Vm;
        double[] Va = spec.Va;
        double[] Pspec = spec.Pspec;
        double[] Qspec = spec.Qspec;
        double[] Qd = spec.Qd;
        double[] qMinBus = spec.QMinBus;
        double[] qMaxBus = spec.QMaxBus;
        double[] Vg = spec.Vg;

        var effType = (BusType[])type0.Clone();
        var pinned = new NewtonShell.QPin[n];   // which limit each bus was pinned at (for revert)
        var revertCount = new int[n];           // anti-oscillation: at most one revert per bus
        var P = new double[n];
        var Q = new double[n];

        // Diagnostic capture (opt-in): admittance matrix + start point, once.
        Diagnostics.SolveTrace? trace = opt.Trace;
        if (trace is not null)
        {
            trace.Header("sparse-newton", n, @base);
            for (int i = 0; i < n; i++)
                for (int p = y.RowPtr[i]; p < y.RowPtr[i + 1]; p++)
                    trace.AddYBus(i, y.ColIdx[p], y.G[p], y.B[p]);
            int[] ids = new int[n];
            for (int i = 0; i < n; i++) ids[i] = model.Buses[i].Id;   // index == model order (see above)
            trace.CaptureSpec(ids, type0, (double[])Vm.Clone(), (double[])Va.Clone(),
                Pspec, Qspec, Qd, Vg, qMinBus, qMaxBus);
        }

        bool converged = false;
        int totalIters = 0;
        double maxMis = double.PositiveInfinity;

        int qPasses = opt.EnforceQLimits ? NewtonShell.QLimitPasses(opt) : 1;
        bool switchPending = false;   // the last pass switched a bus type that has not been solved yet
        for (int qp = 0; qp < qPasses; qp++)
        {
            switchPending = false;
            (converged, int iters, maxMis) = RunNewton(_sparse, _directFallback, y, effType, Vm, Va, Pspec, Qspec, P, Q, opt, trace, qp, spec, @base);
            totalIters += iters;
            if (!opt.EnforceQLimits) break;

            // Only switch bus types after a CONVERGED sub-solve (MATPOWER convention): the Q[] of
            // an unconverged pass is transient garbage, and pinning a PV bus on it is irreversible
            // with RevertQLimits off. The dense twin already guards this (commit 6c06d19); keep the
            // two solvers bit-for-bit identical.
            if (!converged) break;

            var before = trace is null ? null : (BusType[])effType.Clone();
            bool changed = NewtonShell.ApplyQLimits(
                n, type0, effType, Q, Qd, qMinBus, qMaxBus, Qspec, @base,
                Vm, Vg, pinned, revertCount, opt.RevertQLimits, spec.Zip);
            if (trace is not null && before is not null)
                for (int i = 0; i < n; i++)
                    if (before[i] != effType[i])
                        trace.AddQLimitEvent(i, model.Buses[i].Id, effType[i] == BusType.Pq ? pinned[i].ToString() : "Revert", qp);
            switchPending = changed;
            if (!changed) break;
        }

        // R282 (shared rule, both engines): the pass cap was reached right after a bus switched type, so the
        // switched types were never solved and the limits never settled. Solve once more so the reported state
        // is consistent with its final bus types, and report it NOT converged (status MaxIterations) - it used
        // to be reported converged on the previous types' voltages.
        if (switchPending)
        {
            (converged, int capIters, maxMis) = RunNewton(_sparse, _directFallback, y, effType, Vm, Va, Pspec, Qspec, P, Q, opt, trace, qPasses, spec, @base);
            totalIters += capIters;
            converged = false;
        }

        ComputeInjections(y, Vm, Va, P, Q);
        SlackQNote = NewtonShell.DiagnoseSlackQ(n, type0, Q, Qd, qMinBus, qMaxBus, @base, Vm, spec.Zip);
        // N071: report the bus types the solve ended with (a PV bus pinned at a Q limit reads PQ), index == model order.
        return NewtonShell.AssembleResult(model, n, Vm, Va, P, Q, @base, converged, totalIters, maxMis, id => index[id], effType);
    }

    private static (bool, int, double) RunNewton(
        ISparseLinearSolver sparse,
        CpuSparseLuSolver? directFallback,
        ComplexCsr y, BusType[] type, double[] Vm, double[] Va,
        double[] Pspec, double[] Qspec, double[] P, double[] Q, PowerFlowOptions opt,
        Diagnostics.SolveTrace? trace, int pass, NewtonShell.BusSpec spec, double baseMva)
    {
        int n = y.N;
        // Position of each bus's P and Q equation/unknown in the linear system.
        // (Shared numbering with the dense engine - see NewtonShell.EquationPositions.)
        var pPos = new int[n];
        var qPos = new int[n];
        int m = NewtonShell.EquationPositions(type, pPos, qPos);

        if (trace is not null)
        {
            var pvpq = new List<int>(); var pq = new List<int>();
            for (int i = 0; i < n; i++) if (pPos[i] >= 0) pvpq.Add(i);
            for (int i = 0; i < n; i++) if (qPos[i] >= 0) pq.Add(i);
            trace.BeginPass(pass, type, pvpq, pq);
        }

        var f = new double[m];
        var dx = new double[m];

        // The Jacobian sparsity is fixed for this pass (a pure function of Y's pattern + pPos/qPos, which
        // only change between Q-limit passes - i.e. between RunNewton calls). Build the structural pattern
        // and the per-Y-entry value slots ONCE, then refill values in place each Newton iteration instead
        // of rebuilding, re-sorting and re-allocating the CSR from triplets every iteration. The same
        // instance is handed to the solver each iteration, so CpuSparseLuSolver's ordering cache hits and
        // the RCM is computed once.
        var slots = new int[4 * y.RowPtr[n]];
        CsrMatrix jac = BuildJacobianStructure(y, pPos, qPos, m, slots);

        bool converged = false;
        double maxMis = double.PositiveInfinity;
        // Iteration cap (October 2026 docs-programme finding): the loop used to be 'for (; iter < MaxIterations; iter++)'
        // with the mismatch test at the TOP of the body, so after the last allowed step the loop ended without ever
        // evaluating the new state: a solve that the final step had converged was reported MaxIterations, with the
        // stale mismatch from BEFORE that step. MaxIterations was in effect the cap minus one. Now the mismatch is
        // evaluated after every step, including the last; the cap only stops further steps. A solve that converges
        // before the cap takes exactly the same path and returns exactly the same numbers as before (identical
        // arithmetic, same iteration count); only a solve that reaches the cap can change, and then only by having its
        // final state judged honestly.
        int iter = 0;
        for (; ; iter++)
        {
            opt.Cancellation.ThrowIfCancellationRequested(); // no-op for None; aborts a deferred solve at its deadline
            ComputeInjections(y, Vm, Va, P, Q);

            for (int i = 0; i < n; i++)
            {
                if (pPos[i] >= 0) f[pPos[i]] = Pspec[i] - P[i];
                if (qPos[i] >= 0) f[qPos[i]] = Qspec[i] - Q[i];
            }
            // Voltage-dependent (ZIP) load deviation (zero for constant-power buses).
            if (spec.HasZip)
                for (int i = 0; i < n; i++)
                {
                    if (spec.Zip[i] is null) continue;
                    (double dP, double dQ) = NewtonShell.ZipDeviationPu(spec, i, Vm[i], baseMva);
                    if (pPos[i] >= 0) f[pPos[i]] -= dP;
                    if (qPos[i] >= 0) f[qPos[i]] -= dQ;
                }
            maxMis = 0;
            for (int k = 0; k < m; k++) maxMis = Math.Max(maxMis, Math.Abs(f[k]));
            if (maxMis < opt.Tolerance) { converged = true; break; }
            if (iter >= opt.MaxIterations) break;   // cap reached: the final state's mismatch has just been evaluated

            double[] mism = trace is null ? Array.Empty<double>() : (double[])f.Clone();
            RefillJacobian(jac, slots, y, Vm, Va, P, Q);
            // ZIP load adds its dP/dV and dQ/dV to the N and L diagonals (in-place, after the refill).
            if (spec.HasZip)
                for (int i = 0; i < n; i++)
                {
                    if (qPos[i] < 0 || spec.Zip[i] is null) continue; // only PQ buses carry a voltage column
                    (double dPdV, double dQdV) = NewtonShell.ZipJacobianDiagPu(spec, i, Vm[i], baseMva);
                    if (pPos[i] >= 0) jac.Val[Locate(jac, pPos[i], qPos[i])] += dPdV; // N_ii
                    jac.Val[Locate(jac, qPos[i], qPos[i])] += dQdV;                    // L_ii
                }
            // The canonical step and update, shared with the dense engine (bit-identity contract):
            // direct sparse LU, ILU0/BiCGSTAB only if it reports singular, never apply a non-finite step.
            if (!NewtonShell.SolveStep(sparse, jac, f, dx, opt, out double maxAbs, directFallback))
                break; // can't solve this step; stop (NR reports non-convergence)

            // Sparse Jacobian isn't serialised (it would be huge for the cases sparse is used on);
            // the mismatch and the raw Newton step are captured. Damping (below) is applied after.
            trace?.CaptureIteration(iter, maxMis, mism, dx, null);

            NewtonShell.ApplyStep(n, pPos, qPos, dx, maxAbs, Vm, Va);
        }
        return (converged, iter, maxMis);
    }

    /// <summary>Bus power injections P/Q (pu) over the nonzero cells of Y. Internal so the estimate projection
    /// (<see cref="Estimation.EstimationResult.ToPowerFlowResult"/>, R108) uses the identical code path.</summary>
    internal static void ComputeInjections(ComplexCsr y, double[] Vm, double[] Va, double[] P, double[] Q)
    {
        int n = y.N;
        int[] rp = y.RowPtr, ci = y.ColIdx;
        double[] G = y.G, B = y.B;
        for (int i = 0; i < n; i++)
        {
            double pi = 0, qi = 0, vai = Va[i];
            for (int k = rp[i]; k < rp[i + 1]; k++)
            {
                int j = ci[k];
                double th = vai - Va[j];
                (double sin, double cos) = DetMath.SinCos(th);
                double vmj = Vm[j];
                pi += vmj * (G[k] * cos + B[k] * sin);
                qi += vmj * (G[k] * sin - B[k] * cos);
            }
            P[i] = Vm[i] * pi;
            Q[i] = Vm[i] * qi;
        }
    }

    /// <summary>Build the Jacobian's fixed sparsity pattern for this pass and the map from each Y entry's
    /// four blocks (H,N,M,L) to their value slots. The pattern is the STRUCTURAL superset - all four blocks
    /// wherever Y has an entry and both indices are in the reduced system - so it never varies across
    /// iterations even when a block is exactly zero at some point (e.g. N/M of a lossless branch at flat
    /// start). Placeholder 1.0 values keep the builder from dropping any cell; refill overwrites them.</summary>
    internal static CsrMatrix BuildJacobianStructure(ComplexCsr y, int[] pPos, int[] qPos, int m, int[] slots)
    {
        int n = y.N;
        int[] rp = y.RowPtr, ci = y.ColIdx;
        var jb = new CsrMatrix.Builder(m);
        for (int i = 0; i < n; i++)
        {
            int pi = pPos[i], qi = qPos[i];
            if (pi < 0 && qi < 0) continue;
            for (int k = rp[i]; k < rp[i + 1]; k++)
            {
                int j = ci[k];
                int pj = pPos[j], qj = qPos[j];
                if (pi >= 0 && pj >= 0) jb.Add(pi, pj, 1.0);
                if (pi >= 0 && qj >= 0) jb.Add(pi, qj, 1.0);
                if (qi >= 0 && pj >= 0) jb.Add(qi, pj, 1.0);
                if (qi >= 0 && qj >= 0) jb.Add(qi, qj, 1.0);
            }
        }
        CsrMatrix jac = jb.Build();

        for (int i = 0; i < n; i++)
        {
            int pi = pPos[i], qi = qPos[i];
            for (int k = rp[i]; k < rp[i + 1]; k++)
            {
                int j = ci[k];
                int pj = pPos[j], qj = qPos[j];
                int b = 4 * k;
                slots[b + 0] = pi >= 0 && pj >= 0 ? Locate(jac, pi, pj) : -1; // H
                slots[b + 1] = pi >= 0 && qj >= 0 ? Locate(jac, pi, qj) : -1; // N
                slots[b + 2] = qi >= 0 && pj >= 0 ? Locate(jac, qi, pj) : -1; // M
                slots[b + 3] = qi >= 0 && qj >= 0 ? Locate(jac, qi, qj) : -1; // L
            }
        }
        return jac;
    }

    /// <summary>Val index of cell (row, col) - the pattern guarantees it is present.</summary>
    private static int Locate(CsrMatrix a, int row, int col)
        => Array.BinarySearch(a.ColIdx, a.RowPtr[row], a.RowPtr[row + 1] - a.RowPtr[row], col);

    /// <summary>Refill the fixed-pattern Jacobian's values at the current state. Same expressions and same
    /// order as the old per-iteration build; each (Y entry, block) maps to a unique cell, so a plain write
    /// (not accumulate) is correct and every structural cell is written. Cells that are exactly zero this
    /// iteration are written explicitly as 0.0 - dropped again at the solver's CSR->CSC conversion.</summary>
    internal static void RefillJacobian(CsrMatrix jac, int[] slots, ComplexCsr y,
        double[] Vm, double[] Va, double[] P, double[] Q)
    {
        int n = y.N;
        int[] rp = y.RowPtr, ci = y.ColIdx;
        double[] G = y.G, B = y.B;
        Array.Clear(jac.Val);
        for (int i = 0; i < n; i++)
        {
            for (int k = rp[i]; k < rp[i + 1]; k++)
            {
                int j = ci[k];
                double Gij = G[k], Bij = B[k];
                double H, N, M, L;
                if (i == j)
                {
                    H = -Q[i] - Bij * Vm[i] * Vm[i];
                    N = P[i] / Vm[i] + Gij * Vm[i];
                    M = P[i] - Gij * Vm[i] * Vm[i];
                    L = Q[i] / Vm[i] - Bij * Vm[i];
                }
                else
                {
                    double th = Va[i] - Va[j];
                    (double sin, double cos) = DetMath.SinCos(th);
                    H = Vm[i] * Vm[j] * (Gij * sin - Bij * cos);
                    N = Vm[i] * (Gij * cos + Bij * sin);
                    M = -Vm[i] * Vm[j] * (Gij * cos + Bij * sin);
                    L = Vm[i] * (Gij * sin - Bij * cos);
                }
                int b = 4 * k;
                int s;
                if ((s = slots[b + 0]) >= 0) jac.Val[s] = H;
                if ((s = slots[b + 1]) >= 0) jac.Val[s] = N;
                if ((s = slots[b + 2]) >= 0) jac.Val[s] = M;
                if ((s = slots[b + 3]) >= 0) jac.Val[s] = L;
            }
        }
    }

    internal static ComplexCsr BuildY(GridModel model, Dictionary<int, int> index)
    {
        var b = new ComplexCsr.Builder(model.BusCount);
        foreach (Branch br in model.Branches)
        {
            if (!br.InService) continue;
            int f = index[br.From], t = index[br.To];
            BranchStamp s = AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, br.PhaseShiftDeg);
            b.Add(f, f, s.Yff.Real, s.Yff.Imaginary);
            b.Add(t, t, s.Ytt.Real, s.Ytt.Imaginary);
            b.Add(f, t, s.Yft.Real, s.Yft.Imaginary);
            b.Add(t, f, s.Ytf.Real, s.Ytf.Imaginary);
        }
        foreach (Bus bus in model.Buses)
        {
            if (bus.Gs == 0 && bus.Bs == 0) continue;
            int i = index[bus.Id];
            b.Add(i, i, bus.Gs / model.BaseMva, bus.Bs / model.BaseMva);
        }
        return b.Build();
    }

}
