using System.Numerics;
using GridSim.Core.Abstractions;
using GridSim.Core.Model;

namespace GridSim.Core.PowerFlow;

/// <summary>
/// RC5 WP25: the Gauss-Seidel load flow, iterated as MATPOWER's <c>gausspf.m</c>, with an optional acceleration factor.
/// Opt-in only: it is chosen by name (<c>pf --engine gs</c>, psspy <c>solv</c>); <see cref="SolverSelector"/> never
/// returns it, so every default solve is unchanged. psspy <c>nsol</c> is refused: it is PSS/E's decoupled Newton, not
/// Gauss-Seidel.
/// <para>One iteration, as gausspf.m orders it: each PQ bus k in model order, then each PV bus k in model order, is
/// updated in place with <c>V_k &lt;- V_k + alpha ((conj(S_k / V_k) - sum_j Y_kj V_j) / Y_kk)</c>, the sum taken over the
/// latest voltages (so it holds V_k itself). At a PV bus S_k takes the specified P and the Q calculated at the latest
/// voltages first. After the PV loop every PV bus's magnitude is put back to its set point, angle kept. alpha = 1 is
/// gausspf.m exactly; <see cref="Acceleration"/> sets it (0 &lt; alpha &lt; 2).</para>
/// <para>The spec for this package said buses are visited in model order; gausspf.m visits the PQ buses first, then
/// the PV buses, and this follows gausspf.m.</para>
/// <para>The convergence test is Newton's (the largest |dP| over the non-slack buses and |dQ| over the PQ buses below
/// <see cref="PowerFlowOptions.Tolerance"/>, pu), checked before the first iteration and after each one.
/// <see cref="PowerFlowOptions.MaxIterations"/> counts iterations; Gauss-Seidel needs many more than Newton, so a caller
/// usually raises it. Reactive limits, ZIP loads and the refusals are as in <see cref="FastDecoupledPowerFlow"/>.</para>
/// </summary>
public sealed class GaussSeidelPowerFlow : IPowerFlowSolver
{
    /// <summary>A Gauss-Seidel engine with acceleration factor <paramref name="acceleration"/> (1 = gausspf.m).</summary>
    public GaussSeidelPowerFlow(double acceleration = 1.0)
    {
        if (!(acceleration > 0.0 && acceleration < 2.0))
            throw new ArgumentOutOfRangeException(nameof(acceleration), acceleration,
                "The Gauss-Seidel acceleration factor must be above 0 and below 2.");
        Acceleration = acceleration;
    }

    /// <summary>The acceleration factor alpha (0 &lt; alpha &lt; 2; 1 is plain Gauss-Seidel).</summary>
    public double Acceleration { get; }

    /// <inheritdoc />
    public PowerFlowResult Solve(GridModel model, PowerFlowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        PowerFlowOptions opt = options ?? new PowerFlowOptions();
        AlternativeEngines.RefuseUnsupported(model, opt, "Gauss-Seidel");

        int n = model.BusCount;
        Func<int, int> indexOf = NewtonShell.ModelIndex(model);
        NewtonShell.BusSpec spec = NewtonShell.BuildBusSpec(model, n, indexOf, opt);
        SparseYRows y = SparseYRows.Build(model);
        double @base = model.BaseMva;

        BusType[] type0 = spec.Type0;
        double[] vm = spec.Vm, va = spec.Va;
        var effType = (BusType[])type0.Clone();
        var pinned = new NewtonShell.QPin[n];
        var revertCount = new int[n];
        var p = new double[n];
        var q = new double[n];

        bool converged = false, diverged = false;
        int totalIters = 0;
        double maxMis = double.PositiveInfinity;
        int qPasses = opt.EnforceQLimits ? NewtonShell.QLimitPasses(opt) : 1;
        bool switchPending = false;
        for (int qp = 0; qp < qPasses; qp++)
        {
            switchPending = false;
            (converged, int iters, maxMis, bool passDiverged) = Run(y, effType, spec, vm, va, p, q, opt, @base);
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
        if (switchPending)
        {
            (converged, int capIters, maxMis, bool capDiverged) = Run(y, effType, spec, vm, va, p, q, opt, @base);
            diverged = capDiverged;
            totalIters += capIters;
            converged = false;
        }

        y.Injections(AlternativeEngines.Phasors(vm, va), p, q);
        return NewtonShell.AssembleResult(model, n, vm, va, p, q, @base, converged, totalIters, maxMis, indexOf, effType, diverged: !converged && diverged);
    }

    /// <summary>One Gauss-Seidel pass on the bus types <paramref name="type"/>. The state is read from and written back to
    /// <paramref name="vm"/> / <paramref name="va"/>: a slack bus keeps its values, a PV bus keeps its magnitude (the set
    /// point it started the pass with) and takes the angle of its complex voltage, a PQ bus takes both.</summary>
    private (bool Converged, int Iterations, double MaxMismatch, bool Diverged) Run(
        SparseYRows y, BusType[] type, NewtonShell.BusSpec spec, double[] vm, double[] va, double[] p, double[] q,
        PowerFlowOptions opt, double @base)
    {
        int n = type.Length;
        var pv = new List<int>();
        var pq = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (type[i] == BusType.Pv) pv.Add(i);
            else if (type[i] == BusType.Pq) pq.Add(i);
        }
        Complex[] v = AlternativeEngines.Phasors(vm, va);
        double alpha = Acceleration;

        double mis = Mismatch(y, type, spec, v, p, q, @base);
        // A bus with no admittance on its diagonal (nothing connected) has no Gauss-Seidel update: not converged.
        foreach (int k in pv.Concat(pq))
            if (y.Diag[k] < 0 || y.Val[y.Diag[k]] == Complex.Zero) return (mis < opt.Tolerance, 0, mis, false);
        double startMis = mis;   // the divergence stop's reference (PowerFlowOptions.DivergeLimitPu)
        int iter = 0;
        bool converged = mis < opt.Tolerance, diverged = false;
        while (!converged && !diverged && iter < opt.MaxIterations && double.IsFinite(mis))
        {
            opt.Cancellation.ThrowIfCancellationRequested();
            iter++;
            foreach (int k in pq)
            {
                double vmk = Complex.Abs(v[k]);
                Complex s = new(SpecP(spec, k, vmk, @base), SpecQ(spec, k, vmk, @base));
                Complex tmp = (Complex.Conjugate(s / v[k]) - y.RowTimes(k, v)) / y.Val[y.Diag[k]];
                v[k] += alpha * tmp;
            }
            foreach (int k in pv)
            {
                double qk = (v[k] * Complex.Conjugate(y.RowTimes(k, v))).Imaginary;
                Complex s = new(SpecP(spec, k, vm[k], @base), qk);
                Complex tmp = (Complex.Conjugate(s / v[k]) - y.RowTimes(k, v)) / y.Val[y.Diag[k]];
                v[k] += alpha * tmp;
            }
            foreach (int k in pv) v[k] = vm[k] * v[k] / Complex.Abs(v[k]);
            mis = Mismatch(y, type, spec, v, p, q, @base);
            converged = mis < opt.Tolerance;
            diverged = !converged && NewtonShell.DivergedBeyondLimit(opt, startMis, mis);
        }

        foreach (int k in pv) va[k] = Numerics.DetMath.Atan2(v[k].Imaginary, v[k].Real);
        foreach (int k in pq)
        {
            vm[k] = Complex.Abs(v[k]);
            va[k] = Numerics.DetMath.Atan2(v[k].Imaginary, v[k].Real);
        }
        return (converged, iter, mis, diverged);
    }

    private static double SpecP(NewtonShell.BusSpec spec, int i, double vmi, double @base)
        => spec.Pspec[i] - (spec.HasZip ? NewtonShell.ZipDeviationPu(spec, i, vmi, @base).DP : 0.0);

    private static double SpecQ(NewtonShell.BusSpec spec, int i, double vmi, double @base)
        => spec.Qspec[i] - (spec.HasZip ? NewtonShell.ZipDeviationPu(spec, i, vmi, @base).DQ : 0.0);

    /// <summary>Newton's mismatch test at the complex voltages <paramref name="v"/>: the largest |dP| over the non-slack
    /// buses and |dQ| over the PQ buses (NaN when any is NaN).</summary>
    private static double Mismatch(SparseYRows y, BusType[] type, NewtonShell.BusSpec spec, Complex[] v, double[] p, double[] q, double @base)
    {
        y.Injections(v, p, q);
        double max = 0;
        for (int i = 0; i < type.Length; i++)
        {
            if (type[i] == BusType.Slack) continue;
            double vmi = Complex.Abs(v[i]);
            max = AlternativeEngines.MaxAbs(max, SpecP(spec, i, vmi, @base) - p[i]);
            if (type[i] == BusType.Pq) max = AlternativeEngines.MaxAbs(max, SpecQ(spec, i, vmi, @base) - q[i]);
        }
        return max;
    }
}
