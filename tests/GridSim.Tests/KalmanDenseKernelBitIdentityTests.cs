using GridSim.Core.Estimation;
using GridSim.Core.Estimation.Dse;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// October 2026 review, WS11 R107: the Kalman filter's dense kernels were made cheaper without changing a
/// bit - the Gauss-Jordan inverse runs over the live columns only with the bit-exact vector kernel, the
/// identity congruence F P F^T is formed directly, and the information gain H^T R^-1 H skips each row's
/// zero columns - and the filter now fails loud above a documented state limit. These tests compare each
/// kernel with the original code, kept here verbatim, and pin a multi-window filter run against a reference
/// filter built from those verbatim kernels.
/// </summary>
public class KalmanDenseKernelBitIdentityTests
{
    // ---- The pre-R107 kernels, verbatim ---------------------------------------------------------------

    private static double[,] ReferenceInvert(double[,] a, int n)
    {
        var m = new double[n, 2 * n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) m[i, j] = a[i, j];
            m[i, n + i] = 1.0;
        }

        double norm = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) { double av = Math.Abs(a[i, j]); if (av > norm) norm = av; }
        double floor = GridSim.Core.Numerics.NumericTolerances.PivotFloor(norm > 0 ? norm : 1.0);

        for (int col = 0; col < n; col++)
        {
            int piv = col;
            double max = Math.Abs(m[col, col]);
            for (int r = col + 1; r < n; r++)
            {
                double v = Math.Abs(m[r, col]);
                if (v > max) { max = v; piv = r; }
            }
            if (max <= floor)
                throw new InvalidOperationException($"singular matrix in DSE covariance inverse (pivot {max:E3} at column {col}).");
            if (piv != col)
                for (int j = 0; j < 2 * n; j++) (m[col, j], m[piv, j]) = (m[piv, j], m[col, j]);

            double d = m[col, col];
            for (int j = 0; j < 2 * n; j++) m[col, j] /= d;
            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                double fac = m[r, col];
                if (fac == 0.0) continue;
                for (int j = 0; j < 2 * n; j++) m[r, j] -= fac * m[col, j];
            }
        }

        var inv = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) inv[i, j] = m[i, n + j];
        return inv;
    }

    private static double[,] ReferenceCongruence(double[,] f, double[,] p)
    {
        int n = f.GetLength(0);
        var fp = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int k = 0; k < n; k++)
            {
                double fik = f[i, k];
                if (fik == 0.0) continue;
                for (int j = 0; j < n; j++) fp[i, j] += fik * p[k, j];
            }
        var result = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double s = 0;
                for (int k = 0; k < n; k++) s += fp[i, k] * f[j, k];
                result[i, j] = s;
            }
        return result;
    }

    private static void ReferenceInformation(MeasurementModel mm, double[] h, double[,] jac, double[,] g, double[] rhs)
    {
        int m = mm.MeasurementCount, s = rhs.Length;
        for (int r = 0; r < m; r++)
        {
            double wr = 1.0 / (mm.SigmaPu[r] * mm.SigmaPu[r]);
            double yr = mm.ZPu[r] - h[r];
            for (int i = 0; i < s; i++)
            {
                double hwi = jac[r, i] * wr;
                if (hwi == 0.0) continue;
                rhs[i] += hwi * yr;
                for (int j = 0; j < s; j++) g[i, j] += hwi * jac[r, j];
            }
        }
    }

    private static void AssertBits(double[,] expected, double[,] actual)
    {
        Assert.Equal(expected.GetLength(0), actual.GetLength(0));
        Assert.Equal(expected.GetLength(1), actual.GetLength(1));
        for (int i = 0; i < expected.GetLength(0); i++)
            for (int j = 0; j < expected.GetLength(1); j++)
                Assert.True(BitConverter.DoubleToInt64Bits(expected[i, j]) == BitConverter.DoubleToInt64Bits(actual[i, j]),
                    $"[{i},{j}]: expected {expected[i, j]:R}, got {actual[i, j]:R}");
    }

    private static void AssertBits(double[] expected, double[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.True(BitConverter.DoubleToInt64Bits(expected[i]) == BitConverter.DoubleToInt64Bits(actual[i]),
                $"[{i}]: expected {expected[i]:R}, got {actual[i]:R}");
    }

    // ---- Kernel-level identity -------------------------------------------------------------------------

    [Fact]
    public void Gauss_jordan_inverse_is_bit_identical_to_the_original_full_width_loop()
    {
        var rng = new Random(107);
        for (int trial = 0; trial < 40; trial++)
        {
            int n = 1 + rng.Next(45);
            var a = new double[n, n];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                    if (rng.NextDouble() < 0.3) a[i, j] = rng.NextDouble() * 2 - 1;
                a[i, i] += trial % 2 == 0 ? 0.05 : 3.0;   // weak and strong diagonals (pivoting on and off)
                if (trial % 5 == 0 && i > 0) a[i, i - 1] = -0.0;   // signed zeros in the input
            }
            double[,] expected;
            try { expected = ReferenceInvert(a, n); }
            catch (InvalidOperationException) { Assert.Throws<InvalidOperationException>(() => DseLinAlg.Invert(a, n)); continue; }
            AssertBits(expected, DseLinAlg.Invert(a, n));
        }
    }

    [Fact]
    public void Symmetrise_makes_each_off_diagonal_pair_its_mean_and_leaves_the_diagonal()
    {
        // GR-523: the covariance carried between windows is made exactly symmetric.
        var a = new double[,] { { 2.0, 1.0, 0.25 }, { 1.0 + 1e-15, 3.0, -0.5 }, { 0.25, -0.5 - 2e-16, 4.0 } };
        DseLinAlg.SymmetriseInPlace(a, 3);
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(a[i, j]), BitConverter.DoubleToInt64Bits(a[j, i]));
        Assert.Equal(2.0, a[0, 0]);
        Assert.Equal(3.0, a[1, 1]);
        Assert.Equal(4.0, a[2, 2]);
        Assert.Equal(0.5 * (1.0 + (1.0 + 1e-15)), a[0, 1]);
    }

    [Fact]
    public void Gauss_jordan_inverse_still_fails_loud_on_a_singular_matrix()
    {
        var a = new double[,] { { 1, 2 }, { 2, 4 } };
        Assert.Throws<InvalidOperationException>(() => ReferenceInvert(a, 2));
        Assert.Throws<InvalidOperationException>(() => DseLinAlg.Invert(a, 2));
    }

    [Fact]
    public void Identity_congruence_fast_path_is_bit_identical_including_signed_zeros()
    {
        var rng = new Random(7);
        int n = 23;
        var f = new double[n, n];
        for (int i = 0; i < n; i++) f[i, i] = 1.0;
        f[3, 4] = -0.0;   // a negative-zero off-diagonal is still the identity
        var p = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                p[i, j] = (i + j) % 4 == 0 ? -0.0 : (i + j) % 4 == 1 ? 0.0 : rng.NextDouble() * 2 - 1;
        AssertBits(ReferenceCongruence(f, p), DseLinAlg.Congruence(f, p));
    }

    [Fact]
    public void Congruence_takes_the_general_loop_for_non_identity_or_non_finite_input()
    {
        var rng = new Random(8);
        int n = 9;
        var f = new double[n, n];
        var p = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) { f[i, j] = rng.NextDouble(); p[i, j] = rng.NextDouble(); }
        AssertBits(ReferenceCongruence(f, p), DseLinAlg.Congruence(f, p));

        // Identity F but a non-finite P: the general loop's NaN propagation must be reproduced exactly.
        var id = new double[n, n];
        for (int i = 0; i < n; i++) id[i, i] = 1.0;
        p[2, 5] = double.PositiveInfinity;
        p[6, 1] = double.NaN;
        AssertBits(ReferenceCongruence(id, p), DseLinAlg.Congruence(id, p));
    }

    [Theory]
    [InlineData("9")]
    [InlineData("118")]
    public void Information_gain_is_bit_identical_to_the_original_full_loop(string name)
    {
        GridModel model = name == "9" ? IeeeCases.Case9() : IeeeCases.Case118();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { Seed = 21 });
        var mm = new MeasurementModel(model, set);
        int m = mm.MeasurementCount, s = mm.StateSize;
        double[] vm = (double[])truth.Vm.Clone();
        double[] va = truth.VaDeg.Select(d => d * Math.PI / 180.0 + 1e-3).ToArray();
        var h = new double[m];
        mm.EvaluateH(vm, va, h);
        var jac = new double[m, s];
        mm.EvaluateJacobian(vm, va, jac);

        var gRef = new double[s, s];
        var rRef = new double[s];
        ReferenceInformation(mm, h, jac, gRef, rRef);
        var g = new double[s, s];
        var rhs = new double[s];
        KalmanStateEstimator.AccumulateInformation(mm, h, jac, g, rhs);
        AssertBits(gRef, g);
        AssertBits(rRef, rhs);

        // A non-finite Jacobian entry takes the original loop for that row (NaN spreads exactly as before).
        jac[1, 0] = double.PositiveInfinity;
        var gRef2 = new double[s, s];
        var rRef2 = new double[s];
        ReferenceInformation(mm, h, jac, gRef2, rRef2);
        var g2 = new double[s, s];
        var r2 = new double[s];
        KalmanStateEstimator.AccumulateInformation(mm, h, jac, g2, r2);
        AssertBits(gRef2, g2);
        AssertBits(rRef2, r2);
    }

    // ---- End to end: a multi-window run against a reference filter built from the verbatim kernels ---------

    /// <summary>The pre-R107 EKF step loop, verbatim apart from calling the verbatim kernels above.</summary>
    private sealed class ReferenceEkf
    {
        private readonly GridModel _model;
        private readonly int _n;
        private readonly double _slackRefRad;
        private RandomWalkProcessModel? _process;
        private double[] _x = Array.Empty<double>();
        private double[,] _p = new double[0, 0];
        private bool _init;

        public ReferenceEkf(GridModel model)
        {
            _model = model;
            _n = model.BusCount;
            foreach (Bus b in model.Buses)
                if (b.Type == BusType.Slack) { _slackRefRad = b.VaDeg * Math.PI / 180.0; break; }
        }

        public (double[] X, double[,] P) Step(MeasurementSet window, double dt)
        {
            var mm = new MeasurementModel(_model, window);
            int s = mm.StateSize;
            if (!_init)
            {
                _x = new double[s];
                _p = new double[s, s];
                for (int c = 0; c < s; c++)
                {
                    mm.StateBusIndex(c, out bool angle);
                    _x[c] = angle ? _slackRefRad : 1.0;
                    _p[c, c] = 1.0;
                }
                _init = true;
            }
            _process ??= new RandomWalkProcessModel(s - _n);
            var f = new double[s, s];
            var q = new double[s, s];
            _process.Predict(_x, dt, f, q);
            double[] xMinus = (double[])_x.Clone();
            double[,] pMinus = ReferenceCongruence(f, _p);
            for (int i = 0; i < s; i++)
                for (int j = 0; j < s; j++) pMinus[i, j] += q[i, j];

            var vm = new double[_n];
            var va = new double[_n];
            for (int i = 0; i < _n; i++) va[i] = _slackRefRad;
            for (int c = 0; c < s; c++)
            {
                int bus = mm.StateBusIndex(c, out bool angle);
                if (angle) va[bus] = xMinus[c]; else vm[bus] = xMinus[c];
            }
            int m = mm.MeasurementCount;
            var h = new double[m];
            mm.EvaluateH(vm, va, h);
            var jac = new double[m, s];
            mm.EvaluateJacobian(vm, va, jac);
            var g = new double[s, s];
            var rhs = new double[s];
            ReferenceInformation(mm, h, jac, g, rhs);

            double[,] y = ReferenceInvert(pMinus, s);
            for (int i = 0; i < s; i++)
                for (int j = 0; j < s; j++) y[i, j] += g[i, j];
            double[,] p = ReferenceInvert(y, s);
            var dx = new double[s];
            for (int i = 0; i < s; i++)
            {
                double acc = 0;
                for (int j = 0; j < s; j++) acc += p[i, j] * rhs[j];
                dx[i] = acc;
            }
            for (int i = 0; i < s; i++) _x[i] = xMinus[i] + dx[i];
            // GR-523: the filter now stores an exactly symmetric covariance (each off-diagonal pair replaced by its
            // mean, in row order, after the state update), so the reference does the same to stay its bit-for-bit twin.
            for (int i = 0; i < s; i++)
                for (int j = i + 1; j < s; j++)
                {
                    double mean = 0.5 * (p[i, j] + p[j, i]);
                    p[i, j] = mean;
                    p[j, i] = mean;
                }
            _p = p;
            return (_x, _p);
        }
    }

    [Theory]
    [InlineData("9")]
    [InlineData("57")]
    public void Ekf_multi_window_run_is_bit_identical_to_the_reference_filter(string name)
    {
        GridModel model = name == "9" ? IeeeCases.Case9() : IeeeCases.Case57();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        var ekf = new KalmanStateEstimator(model);
        var reference = new ReferenceEkf(model);
        for (int k = 0; k < 4; k++)
        {
            MeasurementSet window = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { Seed = 300 + k });
            double dt = k == 2 ? 0.0 : 1.0 + k;   // includes the zero-noise duplicate-instant step
            DseEstimate est = ekf.Step(window, dt);
            (double[] x, double[,] p) = reference.Step(window, dt);

            // Compare through the public estimate: Vm, Va (deg) and the 1-sigma bounds from diag(P).
            var mm = new MeasurementModel(model, window);
            var vmRef = new double[model.BusCount];
            var vaRef = new double[model.BusCount];
            double slackRad = model.Buses.First(b => b.Type == BusType.Slack).VaDeg * Math.PI / 180.0;
            for (int i = 0; i < model.BusCount; i++) vaRef[i] = slackRad;
            var vmSig = new double[model.BusCount];
            var vaSigRad = new double[model.BusCount];
            for (int c = 0; c < mm.StateSize; c++)
            {
                int bus = mm.StateBusIndex(c, out bool angle);
                double sigma = Math.Sqrt(p[c, c] > 0 ? p[c, c] : 0.0);
                if (angle) { vaRef[bus] = x[c]; vaSigRad[bus] = sigma; } else { vmRef[bus] = x[c]; vmSig[bus] = sigma; }
            }
            AssertBits(vmRef, est.Vm);
            AssertBits(vaRef.Select(v => v * 180.0 / Math.PI).ToArray(), est.VaDeg);
            AssertBits(vmSig, est.StateVmSigma);
            AssertBits(vaSigRad.Select(v => v * 180.0 / Math.PI).ToArray(), est.StateVaSigmaDeg);
        }
    }

    /// <summary>
    /// R107 second part (S5, October 2026): the filter now keeps its s x s matrices in buffers reused window after
    /// window, and its m x s Jacobian buffer while the measurement count repeats. Pinned against the verbatim
    /// reference filter over windows whose measurement counts change (coverage below 1), repeat, and change back, with
    /// a rejected step (negative dt, thrown by the process model) in the middle that must leave the state untouched.
    /// </summary>
    [Theory]
    [InlineData("9")]
    [InlineData("57")]
    public void Ekf_with_reused_buffers_matches_the_reference_when_the_measurement_count_changes(string name)
    {
        GridModel model = name == "9" ? IeeeCases.Case9() : IeeeCases.Case57();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        var ekf = new KalmanStateEstimator(model);
        var reference = new ReferenceEkf(model);
        double[] coverage = { 1.0, 0.8, 0.8, 1.0, 0.6, 1.0 };
        var counts = new HashSet<int>();
        for (int k = 0; k < coverage.Length; k++)
        {
            MeasurementSet window = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions
            {
                Seed = 900 + k, FlowCoverage = coverage[k], InjCoverage = coverage[k],
            });
            counts.Add(new MeasurementModel(model, window).MeasurementCount);
            if (k == 3)
                Assert.Throws<ArgumentOutOfRangeException>(() => ekf.Step(window, -1.0));   // rejected: no state change
            double dt = k == 4 ? 0.0 : 0.5 + k;
            DseEstimate est = ekf.Step(window, dt);
            (double[] x, double[,] p) = reference.Step(window, dt);

            var mm = new MeasurementModel(model, window);
            var vmRef = new double[model.BusCount];
            var vaRef = new double[model.BusCount];
            double slackRad = model.Buses.First(b => b.Type == BusType.Slack).VaDeg * Math.PI / 180.0;
            for (int i = 0; i < model.BusCount; i++) vaRef[i] = slackRad;
            var vmSig = new double[model.BusCount];
            var vaSigRad = new double[model.BusCount];
            for (int c = 0; c < mm.StateSize; c++)
            {
                int bus = mm.StateBusIndex(c, out bool angle);
                double sigma = Math.Sqrt(p[c, c] > 0 ? p[c, c] : 0.0);
                if (angle) { vaRef[bus] = x[c]; vaSigRad[bus] = sigma; } else { vmRef[bus] = x[c]; vmSig[bus] = sigma; }
            }
            AssertBits(vmRef, est.Vm);
            AssertBits(vaRef.Select(v => v * 180.0 / Math.PI).ToArray(), est.VaDeg);
            AssertBits(vmSig, est.StateVmSigma);
            AssertBits(vaSigRad.Select(v => v * 180.0 / Math.PI).ToArray(), est.StateVaSigmaDeg);
        }
        Assert.True(counts.Count >= 2, "the windows must exercise more than one measurement count");
    }

    [Fact]
    public void InvertInto_and_CongruenceInto_overwrite_dirty_buffers_to_the_same_bits()
    {
        var rng = new Random(11);
        const int n = 9;
        var a = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) a[i, j] = (i == j ? 5.0 : 0.0) + rng.NextDouble() - 0.5;
        var work = new double[n, 2 * n];
        var inv = new double[n, n];
        foreach (double[,] dirty in new[] { work, inv })
            for (int i = 0; i < dirty.GetLength(0); i++)
                for (int j = 0; j < dirty.GetLength(1); j++) dirty[i, j] = double.NaN;
        AssertBits(ReferenceInvert(a, n), DseLinAlg.InvertInto(a, n, work, inv));
        AssertBits(ReferenceInvert(a, n), DseLinAlg.InvertInto(a, n, work, inv));   // and again on the used buffers

        var identity = new double[n, n];
        for (int i = 0; i < n; i++) identity[i, i] = 1.0;
        var f = (double[,])a.Clone();
        var result = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) result[i, j] = -7.0;
        AssertBits(ReferenceCongruence(identity, a), DseLinAlg.CongruenceInto(identity, a, result));
        AssertBits(ReferenceCongruence(f, a), DseLinAlg.CongruenceInto(f, a, result));
        Assert.Throws<ArgumentException>(() => DseLinAlg.InvertInto(a, n, work, a));
        Assert.Throws<ArgumentException>(() => DseLinAlg.InvertInto(a, n, new double[n, n], inv));
        Assert.Throws<ArgumentException>(() => DseLinAlg.CongruenceInto(f, a, a));
    }

    [Fact]
    public void Ekf_fails_loud_above_the_dense_filter_state_limit()
    {
        // A synthetic radial chain just big enough to exceed the cap: (buses - 1) angles + buses magnitudes.
        int buses = DenseEstimationLimits.RecursiveFilterMaxState / 2 + 2;
        GridModel model = Chain(buses);
        Assert.True(2 * buses - 1 > DenseEstimationLimits.RecursiveFilterMaxState);
        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => new KalmanStateEstimator(model));
        Assert.Contains(DenseEstimationLimits.RecursiveFilterMaxState.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message);

        // The largest chain at or under the limit is still constructible (the cap is a bound, not a ban).
        int okBuses = (DenseEstimationLimits.RecursiveFilterMaxState + 1) / 2;
        Assert.True(2 * okBuses - 1 <= DenseEstimationLimits.RecursiveFilterMaxState);
        _ = new KalmanStateEstimator(Chain(okBuses));
    }

    private static GridModel Chain(int buses) => new()
    {
        Name = "ekf-cap-chain",
        BaseMva = 100,
        Buses = Enumerable.Range(1, buses)
            .Select(i => new Bus(i, i == 1 ? BusType.Slack : BusType.Pq, BaseKv: 132))
            .ToList(),
        Generators = Array.Empty<Generator>(),
        Branches = Enumerable.Range(1, buses - 1).Select(i => new Branch(i, i + 1, 0.001, 0.01)).ToList(),
    };
}
