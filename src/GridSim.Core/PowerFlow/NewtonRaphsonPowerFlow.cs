using GridSim.Core.Abstractions;
using GridSim.Core.Model;

namespace GridSim.Core.PowerFlow;

/// <summary>
/// The Newton-Raphson power flow in polar form. Each inner iteration linearises the
/// bus power-balance equations around the current voltages (the Jacobian), solves
/// for the corrections through an <see cref="ILinearSolver"/>, and applies them.
///
/// An outer loop enforces generator reactive-power limits: if a PV bus would need
/// more (or less) VARs than its generators can supply, the reactive output is
/// pinned at the limit and the bus is released to PQ  -  its voltage is then free to
/// sag or rise, exactly as a real machine hitting its excitation limit. The flow is
/// re-solved until no further switching is needed.
///
/// <para><b>Bit-identical to <see cref="SparseNewtonRaphsonPowerFlow"/> on the CPU (Mark's ruling 2,
/// October 2026).</b> This engine keeps its own dense admittance, injection and Jacobian assembly, but on
/// the CPU every Newton step is solved and applied by the shared canonical routines
/// (<see cref="NewtonShell.SolveStep"/> / <see cref="NewtonShell.ApplyStep"/>): the dense Jacobian is copied
/// into the same CSR pattern the sparse engine builds and handed to the same sparse LU. The two engines then
/// differ only in assembly, which is exact (same products, same summation order), so every voltage, angle,
/// injection, flow and iteration count matches to the bit. <c>DenseSparseParityTests</c> proves it on every
/// bundled case the dense engine can hold.</para>
///
/// <para>A non-CPU dense backend (the ILGPU / ROCm GPU solvers) is still honoured as given: its
/// factorisation rounds differently by nature, so GPU results are NOT part of the bit-identity contract.
/// It uses the same step damping, voltage floor and non-finite guard as the CPU path.</para>
/// </summary>
public sealed class NewtonRaphsonPowerFlow : IPowerFlowSolver
{
    /// <summary>A non-canonical (GPU) dense backend, or null for the canonical CPU path.</summary>
    private readonly ILinearSolver? _gpuLinear;

    /// <summary>The canonical CPU step solver (null when a GPU backend was supplied). One instance per engine
    /// so its RCM-ordering cache behaves exactly like the sparse engine's.</summary>
    private readonly CpuSparseLuSolver? _canonical;

    /// <summary>
    /// <paramref name="linearSolver"/> null or the CPU <see cref="DenseLuSolver"/> selects the canonical CPU
    /// step (bit-identical to the sparse engine). Any other backend (GPU) is used as given.
    /// </summary>
    public NewtonRaphsonPowerFlow(ILinearSolver? linearSolver = null)
    {
        if (linearSolver is null or DenseLuSolver) _canonical = new CpuSparseLuSolver();
        else _gpuLinear = linearSolver;
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
        YBus ybus = YBus.Build(model);
        int n = ybus.N;

        var G = new double[n, n];
        var B = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) { G[i, j] = ybus.Y[i, j].Real; B[i, j] = ybus.Y[i, j].Imaginary; }

        // The sparse engine's CSR admittance, used here ONLY for its sparsity pattern: the canonical step
        // needs the Jacobian in exactly the CSR pattern the sparse engine builds (that pattern fixes the
        // RCM ordering, and so the LU's rounding). Values always come from this engine's dense arrays.
        var yIndex = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) yIndex[model.Buses[i].Id] = i;   // == YBus's own index (model order)
        Numerics.ComplexCsr yPattern = SparseNewtonRaphsonPowerFlow.BuildY(model, yIndex);

        // Per-bus aggregates and start point (shared with the sparse solver).
        double @base = model.BaseMva;
        NewtonShell.BusSpec spec = NewtonShell.BuildBusSpec(model, n, ybus.IndexOf, opt);
        BusType[] type0 = spec.Type0;
        double[] Vm = spec.Vm;
        double[] Va = spec.Va; // radians
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

        // Diagnostic capture (opt-in). Record the admittance matrix and the start point once.
        Diagnostics.SolveTrace? trace = opt.Trace;
        if (trace is not null)
        {
            trace.Header("dense-newton", n, @base);
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    if (G[i, j] != 0 || B[i, j] != 0) trace.AddYBus(i, j, G[i, j], B[i, j]);
            int[] ids = new int[n];
            foreach (Bus bus in model.Buses) ids[ybus.IndexOf(bus.Id)] = bus.Id;
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
            (converged, int iters, maxMis) = RunNewton(n, G, B, yPattern, effType, Vm, Va, Pspec, Qspec, P, Q, opt, qp, spec, @base);
            totalIters += iters;
            if (!opt.EnforceQLimits) break;

            // Only switch bus types after a CONVERGED sub-solve (MATPOWER
            // convention): the Q[] of an unconverged pass is transient garbage,
            // and pinning a PV bus on it is irreversible with RevertQLimits off.
            if (!converged) break;

            // Check PV buses against their generator Q-limits (shared bookkeeping).
            var before = trace is null ? null : (BusType[])effType.Clone();
            bool changed = NewtonShell.ApplyQLimits(
                n, type0, effType, Q, Qd, qMinBus, qMaxBus, Qspec, @base,
                Vm, Vg, pinned, revertCount, opt.RevertQLimits, spec.Zip);
            if (trace is not null && before is not null)
                for (int i = 0; i < n; i++)
                    if (before[i] != effType[i])
                        trace.AddQLimitEvent(i, model.Buses.First(b => ybus.IndexOf(b.Id) == i).Id,
                            effType[i] == BusType.Pq ? pinned[i].ToString() : "Revert", qp);
            switchPending = changed;
            if (!changed) break;
        }

        // R282 (shared rule, both engines): the pass cap was reached right after a bus switched type, so the
        // switched types were never solved and the limits never settled. Solve once more so the reported state
        // is consistent with its final bus types, and report it NOT converged (status MaxIterations) - it used
        // to be reported converged on the previous types' voltages.
        if (switchPending)
        {
            (converged, int capIters, maxMis) = RunNewton(n, G, B, yPattern, effType, Vm, Va, Pspec, Qspec, P, Q, opt, qPasses, spec, @base);
            totalIters += capIters;
            converged = false;
        }

        // Final injections at the solved point, then shared result assembly.
        ComputeInjections(n, G, B, Vm, Va, P, Q);
        SlackQNote = NewtonShell.DiagnoseSlackQ(n, type0, Q, Qd, qMinBus, qMaxBus, @base, Vm, spec.Zip);
        // N071: report the bus types the solve ended with (a PV bus pinned at a Q limit reads PQ), index == model order.
        return NewtonShell.AssembleResult(model, n, Vm, Va, P, Q, @base, converged, totalIters, maxMis, ybus.IndexOf, effType);
    }

    private (bool converged, int iters, double maxMis) RunNewton(
        int n, double[,] G, double[,] B, Numerics.ComplexCsr yPattern, BusType[] type, double[] Vm, double[] Va,
        double[] Pspec, double[] Qspec, double[] P, double[] Q, PowerFlowOptions opt, int pass,
        NewtonShell.BusSpec spec, double baseMva)
    {
        var pvpq = new List<int>();
        var pq = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (type[i] != BusType.Slack) pvpq.Add(i);
            if (type[i] == BusType.Pq) pq.Add(i);
        }
        int npvpq = pvpq.Count, npq = pq.Count, m = npvpq + npq;

        // The shared equation numbering: row r < npvpq is bus pvpq[r]'s P equation, row npvpq + r is bus
        // pq[r]'s Q equation - the same positions NewtonShell.EquationPositions gives the sparse engine.
        var pPos = new int[n];
        var qPos = new int[n];
        if (NewtonShell.EquationPositions(type, pPos, qPos) != m)
            throw new InvalidOperationException("Dense and shared Newton equation numbering disagree (internal error).");

        // Reusable buffers  -  rebuilt each iteration, never reallocated.
        var J = new double[m, m];
        var f = new double[m];
        var dx = new double[m];

        // Canonical CPU path: the sparse engine's Jacobian pattern for this pass (structure only; the
        // 4*nnz slot map the sparse engine uses for its refill is not needed here).
        Numerics.CsrMatrix? jac = _canonical is null ? null
            : SparseNewtonRaphsonPowerFlow.BuildJacobianStructure(yPattern, pPos, qPos, m, new int[4 * yPattern.RowPtr[n]]);

        Diagnostics.SolveTrace? trace = opt.Trace;
        trace?.BeginPass(pass, type, pvpq, pq);
        bool captureJac = trace is not null && (trace.CaptureJacobian || n <= trace.JacobianMaxN);
        if (trace is not null && !captureJac) trace.NoteJacobianOmittedForSize();

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
            ComputeInjections(n, G, B, Vm, Va, P, Q);

            for (int k = 0; k < npvpq; k++) f[k] = Pspec[pvpq[k]] - P[pvpq[k]];
            for (int k = 0; k < npq; k++) f[npvpq + k] = Qspec[pq[k]] - Q[pq[k]];

            // Voltage-dependent (ZIP) load: subtract its deviation from nominal (zero for constant-power buses).
            if (spec.HasZip)
            {
                for (int k = 0; k < npvpq; k++)
                    f[k] -= NewtonShell.ZipDeviationPu(spec, pvpq[k], Vm[pvpq[k]], baseMva).DP;
                for (int k = 0; k < npq; k++)
                    f[npvpq + k] -= NewtonShell.ZipDeviationPu(spec, pq[k], Vm[pq[k]], baseMva).DQ;
            }

            maxMis = 0;
            for (int k = 0; k < m; k++) maxMis = Math.Max(maxMis, Math.Abs(f[k]));
            if (maxMis < opt.Tolerance) { converged = true; break; }
            if (iter >= opt.MaxIterations) break;   // cap reached: the final state's mismatch has just been evaluated

            BuildJacobianInto(J, G, B, Vm, Va, P, Q, pvpq, pq, spec, baseMva);
            // Capture BEFORE the solve: the linear solver factorises J/overwrites f in place.
            double[] mism = trace is null ? System.Array.Empty<double>() : (double[])f.Clone();
            double[,]? jacCopy = captureJac ? (double[,])J.Clone() : null;
            double maxAbs;
            if (jac is not null)
            {
                // Canonical CPU step: same CSR, same LU, same fallback and guard as the sparse engine.
                CopyIntoPattern(J, jac);
                if (!NewtonShell.SolveStep(_canonical!, jac, f, dx, opt, out maxAbs))
                    break; // can't solve this step; stop (NR reports non-convergence)
            }
            else
            {
                // GPU dense backend, used as given (not part of the bit-identity contract).
                // A singular (or NaN) Jacobian on the GPU backend stops the pass as not converged, exactly as the CPU
                // path does; it used to escape Solve as an exception, so studies that rely on non-convergence (the CPF
                // nose, the tap search) crashed on --gpu.
                try { _gpuLinear!.SolveInPlace(J, f); } // f now holds the corrections deltax
                catch (InvalidOperationException) { break; }
                Array.Copy(f, dx, m);
                maxAbs = 0;
                bool finite = true;
                for (int k = 0; k < m; k++)
                {
                    if (!double.IsFinite(dx[k])) { finite = false; break; }
                    maxAbs = Math.Max(maxAbs, Math.Abs(dx[k]));
                }
                if (!finite) break; // never apply a NaN/Inf step; NR reports non-convergence
            }
            trace?.CaptureIteration(iter, maxMis, mism, dx, jacCopy);

            NewtonShell.ApplyStep(n, pPos, qPos, dx, maxAbs, Vm, Va);
        }
        return (converged, iter, maxMis);
    }

    /// <summary>
    /// Copy the dense Jacobian into the shared CSR pattern, cell for cell (a plain copy: no arithmetic, so
    /// the values are bit-for-bit this engine's). Every cell OUTSIDE the pattern must be exactly zero - it is
    /// a product with a zero admittance - and is checked, so a pattern/assembly mismatch fails loudly here
    /// instead of silently dropping a Jacobian term.
    /// </summary>
    private static void CopyIntoPattern(double[,] J, Numerics.CsrMatrix jac)
    {
        int m = jac.N;
        int[] rp = jac.RowPtr, ci = jac.ColIdx;
        double[] val = jac.Val;
        for (int r = 0; r < m; r++)
        {
            int p = rp[r], end = rp[r + 1];
            for (int c = 0; c < m; c++)
            {
                if (p < end && ci[p] == c) { val[p++] = J[r, c]; continue; }
                // A NaN off the pattern is a product of a zero admittance with a non-finite state (0 x NaN): not a pattern
                // mismatch but a diverged or NaN-seeded iterate. It is left to NewtonShell.SolveStep's non-finite guard,
                // which sees the same NaN in the in-pattern cells and ends the solve as not converged, exactly as the
                // sparse engine does. (It used to escape as this "internal error", NaN != 0.0 being true.)
                if (J[r, c] != 0.0 && !double.IsNaN(J[r, c]))
                    throw new InvalidOperationException(
                        $"Dense Jacobian cell ({r},{c}) = {J[r, c]:R} lies outside the shared sparsity pattern (internal error).");
            }
        }
    }

    /// <summary>Bus power injections P/Q (pu) at the given voltages. Internal so the state
    /// estimator's measurement model evaluates h(x) through the exact same code path - one
    /// implementation, no copy-drift between the solver and the estimator.</summary>
    internal static void ComputeInjections(int n, double[,] G, double[,] B, double[] Vm, double[] Va, double[] P, double[] Q)
    {
        for (int i = 0; i < n; i++)
        {
            double pi = 0, qi = 0;
            for (int k = 0; k < n; k++)
            {
                double th = Va[i] - Va[k];
                (double sin, double cos) = DetMath.SinCos(th);
                double vmk = Vm[k];
                pi += vmk * (G[i, k] * cos + B[i, k] * sin);
                qi += vmk * (G[i, k] * sin - B[i, k] * cos);
            }
            P[i] = Vm[i] * pi;
            Q[i] = Vm[i] * qi;
        }
    }

    private static void BuildJacobianInto(
        double[,] J, double[,] G, double[,] B, double[] Vm, double[] Va, double[] P, double[] Q,
        List<int> pvpq, List<int> pq, NewtonShell.BusSpec spec, double baseMva)
    {
        int npvpq = pvpq.Count, npq = pq.Count;

        // Trig via DetMath.SinCos (GridSim's port of fdlibm), the call the sparse engine's refill makes: the platform
        // Math.Sin / Math.Cos / Math.SinCos are libm-dependent and may round differently on another OS or runtime, so
        // every trigonometric call on the Newton path goes through DetMath, and dense, sparse and cross-OS results stay
        // bit-identical (GR-053: this comment used to name Math.SinCos).
        for (int r = 0; r < npvpq; r++)
        {
            int i = pvpq[r];
            for (int c = 0; c < npvpq; c++)
            {
                int j = pvpq[c];
                (double sin, double cos) = DetMath.SinCos(Va[i] - Va[j]);
                J[r, c] = (i == j)
                    ? -Q[i] - B[i, i] * Vm[i] * Vm[i]
                    : Vm[i] * Vm[j] * (G[i, j] * sin - B[i, j] * cos);
            }
            for (int c = 0; c < npq; c++)
            {
                int j = pq[c];
                (double sin, double cos) = DetMath.SinCos(Va[i] - Va[j]);
                J[r, npvpq + c] = (i == j)
                    ? P[i] / Vm[i] + G[i, i] * Vm[i]
                    : Vm[i] * (G[i, j] * cos + B[i, j] * sin);
                // ZIP load adds d(Pload/base)/dV to the N diagonal (dP/dV).
                if (spec.HasZip && i == j) J[r, npvpq + c] += NewtonShell.ZipJacobianDiagPu(spec, i, Vm[i], baseMva).DPdV;
            }
        }

        for (int r = 0; r < npq; r++)
        {
            int i = pq[r];
            for (int c = 0; c < npvpq; c++)
            {
                int j = pvpq[c];
                (double sin, double cos) = DetMath.SinCos(Va[i] - Va[j]);
                J[npvpq + r, c] = (i == j)
                    ? P[i] - G[i, i] * Vm[i] * Vm[i]
                    : -Vm[i] * Vm[j] * (G[i, j] * cos + B[i, j] * sin);
            }
            for (int c = 0; c < npq; c++)
            {
                int j = pq[c];
                (double sin, double cos) = DetMath.SinCos(Va[i] - Va[j]);
                J[npvpq + r, npvpq + c] = (i == j)
                    ? Q[i] / Vm[i] - B[i, i] * Vm[i]
                    : Vm[i] * (G[i, j] * sin - B[i, j] * cos);
                // ZIP load adds d(Qload/base)/dV to the L diagonal (dQ/dV).
                if (spec.HasZip && i == j) J[npvpq + r, npvpq + c] += NewtonShell.ZipJacobianDiagPu(spec, i, Vm[i], baseMva).DQdV;
            }
        }
    }

}
