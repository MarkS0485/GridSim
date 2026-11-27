using System.Numerics;
using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.Numerics;

namespace GridSim.Core.PowerFlow;

/// <summary>
/// RC5 WP25: the fast-decoupled load flow, XB or BX (Stott and Alsac, IEEE Trans. PAS-93, 1974; van Amerongen, IEEE
/// Trans. PWRS 4(4), 1989), iterated as MATPOWER's <c>fdpf.m</c>. Opt-in only: it is chosen by name (<c>pf --engine fdxb</c>
/// or <c>fdbx</c>); <see cref="SolverSelector"/> never returns it, so every default solve is unchanged. psspy <c>fdns</c>
/// and <c>fncl</c> are refused: PSS/E's FDNS is a fixed-slope decoupled Newton method not confirmed to match this engine,
/// and FNCL is PSS/E's own variant.
/// <list type="bullet">
/// <item>B' and B'' come from <see cref="FdlfMatrices"/> (makeB.m's rules). B' is reduced to the non-slack buses and
/// factorised once; B'' is reduced to the PQ buses and factorised once per reactive-limit pass (a PV bus that becomes PQ
/// adds a row and column).</item>
/// <item>One iteration is a P-theta half-iteration, <c>dtheta = B'^-1 (dP / V)</c>, then a Q-V half-iteration,
/// <c>dV = B''^-1 (dQ / V)</c>, with dP and dQ the specified less the calculated injection (pu). The mismatch is tested
/// after each half-iteration, as fdpf.m does.</item>
/// <item>The convergence test is Newton's: the largest |dP| over the non-slack buses and |dQ| over the PQ buses below
/// <see cref="PowerFlowOptions.Tolerance"/> (pu). fdpf.m tests the mismatch divided by |V|; GridSim keeps one test for
/// every engine. <see cref="PowerFlowOptions.MaxIterations"/> counts full iterations.</item>
/// <item>Reactive limits: after a converged pass, <see cref="NewtonShell.ApplyQLimits"/> (the same rule, tolerance and
/// revert options as Newton) and a new pass from the state reached, as MATPOWER's runpf does.</item>
/// <item>Voltage-dependent (ZIP) loads enter the mismatch as in Newton. No retry from a DC start: a failure is reported
/// as it ended.</item>
/// </list>
/// Refused (InvalidOperationException): a model with HVDC links, FACTS devices, static var compensators or induction
/// machines (their coupling runs through the Newton engines only), and a solve trace (it records the Newton engines).
/// </summary>
public sealed class FastDecoupledPowerFlow : IPowerFlowSolver
{
    /// <summary>A fast-decoupled engine of the given variant.</summary>
    public FastDecoupledPowerFlow(FdlfVariant variant)
    {
        if (!Enum.IsDefined(variant)) throw new ArgumentOutOfRangeException(nameof(variant), variant, "FdlfVariant must be XB or BX.");
        Variant = variant;
    }

    /// <summary>XB or BX.</summary>
    public FdlfVariant Variant { get; }

    /// <inheritdoc />
    public PowerFlowResult Solve(GridModel model, PowerFlowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        PowerFlowOptions opt = options ?? new PowerFlowOptions();
        AlternativeEngines.RefuseUnsupported(model, opt, Variant == FdlfVariant.XB ? "fast-decoupled (XB)" : "fast-decoupled (BX)");

        int n = model.BusCount;
        Func<int, int> indexOf = NewtonShell.ModelIndex(model);
        NewtonShell.BusSpec spec = NewtonShell.BuildBusSpec(model, n, indexOf, opt);
        SparseYRows y = SparseYRows.Build(model);
        FdlfMatrixPair b = FdlfMatrices.Build(model, Variant);
        double @base = model.BaseMva;

        BusType[] type0 = spec.Type0;
        double[] vm = spec.Vm, va = spec.Va;
        var effType = (BusType[])type0.Clone();
        var pinned = new NewtonShell.QPin[n];
        var revertCount = new int[n];
        var p = new double[n];
        var q = new double[n];

        // B' is over the non-slack buses, which the reactive-limit loop never changes: factorised once.
        var pvpq = new List<int>();
        for (int i = 0; i < n; i++) if (type0[i] != BusType.Slack) pvpq.Add(i);
        SparseLuFactorization? bpLu = pvpq.Count == 0 ? null : SparseLu.Factor(FdlfMatrices.Reduce(b.BPrime, pvpq));

        bool converged = false, diverged = false;
        int totalIters = 0;
        double maxMis = double.PositiveInfinity;
        int qPasses = opt.EnforceQLimits ? NewtonShell.QLimitPasses(opt) : 1;
        bool switchPending = false;
        for (int qp = 0; qp < qPasses; qp++)
        {
            switchPending = false;
            (converged, int iters, maxMis, bool passDiverged) = Run(y, b, bpLu, pvpq, effType, spec, vm, va, p, q, opt, @base);
            diverged = passDiverged;
            totalIters += iters;
            if (!opt.EnforceQLimits || !converged) break;
            bool changed = NewtonShell.ApplyQLimits(n, type0, effType, q, spec.Qd, spec.QMinBus, spec.QMaxBus, spec.Qspec, @base,
                vm, spec.Vg, pinned, revertCount, opt.RevertQLimits, spec.Zip);
            if (!changed && !opt.RevertQLimits)
                changed = NewtonShell.RevertBrokenPins(n, type0, effType, vm, spec.Vg, pinned, revertCount);
            switchPending = changed;
            if (!changed) break;
        }
        // The Newton engines' R282 rule: the pass cap was reached right after a switch, so solve once more on the final
        // types and report the state not converged.
        if (switchPending)
        {
            (converged, int capIters, maxMis, bool capDiverged) = Run(y, b, bpLu, pvpq, effType, spec, vm, va, p, q, opt, @base);
            diverged = capDiverged;
            totalIters += capIters;
            converged = false;
        }

        y.Injections(AlternativeEngines.Phasors(vm, va), p, q);
        return NewtonShell.AssembleResult(model, n, vm, va, p, q, @base, converged, totalIters, maxMis, indexOf, effType, diverged: !converged && diverged);
    }

    /// <summary>One fast-decoupled pass on the bus types <paramref name="type"/>; returns whether it converged, the full
    /// iterations taken and the last mismatch.</summary>
    private static (bool Converged, int Iterations, double MaxMismatch, bool Diverged) Run(
        SparseYRows y, FdlfMatrixPair b, SparseLuFactorization? bpLu, List<int> pvpq, BusType[] type,
        NewtonShell.BusSpec spec, double[] vm, double[] va, double[] p, double[] q, PowerFlowOptions opt, double @base)
    {
        int n = type.Length;
        var pq = new List<int>();
        for (int i = 0; i < n; i++) if (type[i] == BusType.Pq) pq.Add(i);
        var fP = new double[pvpq.Count];
        var fQ = new double[pq.Count];

        double mis = Mismatch(y, pvpq, pq, spec, vm, va, p, q, fP, fQ, @base);
        double startMis = mis;   // the divergence stop's reference (PowerFlowOptions.DivergeLimitPu)
        if (mis < opt.Tolerance) return (true, 0, mis, false);
        if (pvpq.Count > 0 && bpLu is null) return (false, 0, mis, false);   // B' singular: no step can be taken
        SparseLuFactorization? bppLu = pq.Count == 0 ? null : SparseLu.Factor(FdlfMatrices.Reduce(b.BDoublePrime, pq));
        if (pq.Count > 0 && bppLu is null) return (false, 0, mis, false);   // B'' singular

        int iter = 0;
        while (iter < opt.MaxIterations)
        {
            opt.Cancellation.ThrowIfCancellationRequested();
            iter++;
            // P-theta half-iteration.
            if (pvpq.Count > 0)
            {
                for (int k = 0; k < pvpq.Count; k++) fP[k] /= vm[pvpq[k]];
                double[] dVa = bpLu!.Solve(fP);
                for (int k = 0; k < pvpq.Count; k++) va[pvpq[k]] += dVa[k];
            }
            mis = Mismatch(y, pvpq, pq, spec, vm, va, p, q, fP, fQ, @base);
            if (mis < opt.Tolerance) return (true, iter, mis, false);
            if (!double.IsFinite(mis)) return (false, iter, mis, false);
            if (NewtonShell.DivergedBeyondLimit(opt, startMis, mis)) return (false, iter, mis, true);
            // Q-V half-iteration.
            if (pq.Count > 0)
            {
                for (int k = 0; k < pq.Count; k++) fQ[k] /= vm[pq[k]];
                double[] dVm = bppLu!.Solve(fQ);
                for (int k = 0; k < pq.Count; k++) vm[pq[k]] += dVm[k];
            }
            mis = Mismatch(y, pvpq, pq, spec, vm, va, p, q, fP, fQ, @base);
            if (mis < opt.Tolerance) return (true, iter, mis, false);
            if (!double.IsFinite(mis)) return (false, iter, mis, false);
            if (NewtonShell.DivergedBeyondLimit(opt, startMis, mis)) return (false, iter, mis, true);
        }
        return (false, iter, mis, false);
    }

    /// <summary>The specified less the calculated injection (pu): P at the non-slack buses into <paramref name="fP"/>, Q at
    /// the PQ buses into <paramref name="fQ"/>, the ZIP deviation subtracted as Newton subtracts it. Returns the largest
    /// magnitude (NaN when any entry is NaN).</summary>
    private static double Mismatch(SparseYRows y, List<int> pvpq, List<int> pq, NewtonShell.BusSpec spec, double[] vm, double[] va,
        double[] p, double[] q, double[] fP, double[] fQ, double @base)
    {
        y.Injections(AlternativeEngines.Phasors(vm, va), p, q);
        double max = 0;
        for (int k = 0; k < pvpq.Count; k++)
        {
            int i = pvpq[k];
            fP[k] = spec.Pspec[i] - p[i] - (spec.HasZip ? NewtonShell.ZipDeviationPu(spec, i, vm[i], @base).DP : 0.0);
            max = AlternativeEngines.MaxAbs(max, fP[k]);
        }
        for (int k = 0; k < pq.Count; k++)
        {
            int i = pq[k];
            fQ[k] = spec.Qspec[i] - q[i] - (spec.HasZip ? NewtonShell.ZipDeviationPu(spec, i, vm[i], @base).DQ : 0.0);
            max = AlternativeEngines.MaxAbs(max, fQ[k]);
        }
        return max;
    }
}

/// <summary>Shared pieces of the opt-in fast-decoupled and Gauss-Seidel engines.</summary>
internal static class AlternativeEngines
{
    /// <summary>Refuse what these engines do not solve: HVDC, FACTS, SVC and induction-machine coupling (Newton only) and a
    /// solve trace (it records the Newton engines).</summary>
    public static void RefuseUnsupported(GridModel model, PowerFlowOptions opt, string engine)
    {
        if (model.HasCoupledDevices)
            throw new InvalidOperationException(
                $"The {engine} engine does not solve HVDC links, FACTS devices, static var compensators or induction machines; use the Newton engine.");
        if (opt.Trace is not null)
            throw new InvalidOperationException($"The solve trace records the Newton engines only; the {engine} engine does not fill it.");
    }

    /// <summary>The complex bus voltages from magnitudes and angles (radians), by <see cref="DetMath.FromPolar"/>.</summary>
    public static Complex[] Phasors(double[] vm, double[] va)
    {
        var v = new Complex[vm.Length];
        for (int i = 0; i < vm.Length; i++) v[i] = DetMath.FromPolar(vm[i], va[i]);
        return v;
    }

    /// <summary>max(<paramref name="max"/>, |<paramref name="x"/>|), with a NaN carried through (Math.Max keeps NaN).</summary>
    public static double MaxAbs(double max, double x) => Math.Max(max, Math.Abs(x));
}
