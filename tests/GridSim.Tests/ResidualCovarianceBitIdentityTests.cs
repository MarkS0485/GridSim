using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// October 2026 review, WS11 R010/R117: the sparse residual-covariance stage (normalised residuals and
/// criticality flags) no longer runs a full forward and back solve, with two fresh s-length allocations, per
/// measurement. It uses <see cref="SparseLuFactorization.InverseQuadraticForm"/> (only the part of each
/// solve that can reach the row's own entries, in a reused workspace) spread over the cores. These tests pin
/// that the new stage is BIT-IDENTICAL to the original loop, kept here verbatim as the reference, serially and
/// in parallel, on bundled cases from 118 to 1,354 buses, on a low-redundancy set with critical rows, and on
/// adversarial matrices (partial pivoting, duplicate and empty rows, non-finite factors).
/// </summary>
public class ResidualCovarianceBitIdentityTests
{
    private static string DataDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "data");
    }

    /// <summary>The pre-R010 sparse NormalizedResiduals loop, verbatim (one full Solve per row).</summary>
    private static (double[] Normalized, bool[] Critical) ReferenceLoop(
        MeasurementModel mm, SparseJacobianRows jac, SparseLuFactorization lu, double[] residualPu, double[] w, int stride)
    {
        int m = mm.MeasurementCount;
        int s = mm.StateSize;
        var normalized = new double[m];
        var critical = new bool[m];
        var hDense = new double[s];
        for (int r = 0; r < m; r += stride)
        {
            List<(int Col, double Val)> row = jac.Rows[r];
            for (int p = 0; p < row.Count; p++) hDense[row[p].Col] = row[p].Val;
            double[] t = lu.Solve(hDense);
            for (int p = 0; p < row.Count; p++) hDense[row[p].Col] = 0.0;

            double hGh = 0;
            for (int p = 0; p < row.Count; p++) hGh += row[p].Val * t[row[p].Col];

            double sigmaSq = 1.0 / w[r];
            double omega = sigmaSq - hGh;
            if (omega <= sigmaSq * BadDataAnalysis.CriticalFloor)
            {
                critical[r] = true;
                normalized[r] = double.NaN;
            }
            else
            {
                normalized[r] = Math.Abs(residualPu[r]) / Math.Sqrt(omega);
            }
        }
        return (normalized, critical);
    }

    /// <summary>A sparse gain factor at (vm, va) plus the Jacobian rows, residuals and weights the
    /// covariance stage consumes.</summary>
    private static (MeasurementModel Mm, SparseJacobianRows Jac, SparseLuFactorization Lu, double[] Res, double[] W)
        Prepare(GridModel model, MeasurementSet set, double[] vm, double[] va)
    {
        var mm = new MeasurementModel(model, set);
        int m = mm.MeasurementCount;
        var w = new double[m];
        for (int r = 0; r < m; r++) w[r] = 1.0 / (mm.SigmaPu[r] * mm.SigmaPu[r]);
        var h = new double[m];
        mm.EvaluateH(vm, va, h);
        var res = new double[m];
        for (int r = 0; r < m; r++) res[r] = mm.ZPu[r] - h[r];

        // The gain H^T W H assembled independently of the step (any valid factor will do: the new stage and
        // the reference loop are compared on the SAME factor).
        var builder = new CsrMatrix.Builder(mm.StateSize);
        var jac = new SparseJacobianRows(m);
        mm.EvaluateJacobian(vm, va, jac);
        for (int r = 0; r < m; r++)
            foreach ((int c1, double v1) in jac.Rows[r])
                foreach ((int c2, double v2) in jac.Rows[r])
                    builder.Add(c1, c2, w[r] * v1 * v2);
        SparseLuFactorization lu = SparseLu.Factor(builder.Build())
            ?? throw new InvalidOperationException("gain is singular in the test setup");
        return (mm, jac, lu, res, w);
    }

    private static void AssertBitsEqual(double[] expected, double[] actual, int stride)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int r = 0; r < expected.Length; r += stride)
            Assert.True(BitConverter.DoubleToInt64Bits(expected[r]) == BitConverter.DoubleToInt64Bits(actual[r]),
                $"row {r}: expected {expected[r]:R}, got {actual[r]:R}");
    }

    private static void AssertMatchesReference(GridModel model, MeasurementSet set, double[] vm, double[] va,
        int stride, bool expectCritical)
    {
        (MeasurementModel mm, SparseJacobianRows jac, SparseLuFactorization lu, double[] res, double[] w) =
            Prepare(model, set, vm, va);
        (double[] refN, bool[] refC) = ReferenceLoop(mm, jac, lu, res, w, stride);
        (double[] serN, bool[] serC) = BadDataAnalysis.NormalizedResiduals(mm, jac, lu, res, w, maxDegreeOfParallelism: 1);
        (double[] parN, bool[] parC) = BadDataAnalysis.NormalizedResiduals(mm, jac, lu, res, w);
        (double[] twoN, bool[] twoC) = BadDataAnalysis.NormalizedResiduals(mm, jac, lu, res, w, maxDegreeOfParallelism: 2);

        AssertBitsEqual(refN, serN, stride);
        AssertBitsEqual(serN, parN, 1);
        AssertBitsEqual(serN, twoN, 1);
        for (int r = 0; r < refC.Length; r += stride) Assert.Equal(refC[r], serC[r]);
        Assert.Equal(serC, parC);
        Assert.Equal(serC, twoC);
        if (expectCritical) Assert.Contains(true, serC);
    }

    private static (double[] Vm, double[] Va) Radians(PowerFlowResult pf)
        => ((double[])pf.Vm.Clone(), pf.VaDeg.Select(d => d * Math.PI / 180.0).ToArray());

    [Theory]
    [InlineData("118")]
    [InlineData("300")]
    public void Sparse_residual_covariance_is_bit_identical_to_the_full_solve_loop(string name)
    {
        GridModel model = name == "118" ? IeeeCases.Case118() : IeeeCases.Case300();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { Seed = 7 });
        // Off-truth state (a flat-ish perturbation) so the residuals are not all noise-sized.
        (double[] vm, double[] va) = Radians(truth);
        for (int i = 0; i < vm.Length; i++) { vm[i] *= 1.0 + 1e-3 * ((i % 7) - 3); va[i] += 1e-3 * ((i % 5) - 2); }
        AssertMatchesReference(model, set, vm, va, stride: 1, expectCritical: false);
    }

    [Fact]
    public void Sparse_residual_covariance_is_bit_identical_on_pegase1354()
    {
        GridModel model = MatpowerParser.ParseFile(Path.Combine(DataDir(), "matpower", "pegase", "case1354pegase.m"));
        PowerFlowResult truth = new SparseNewtonRaphsonPowerFlow().Solve(model);
        Assert.True(truth.Converged);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { Seed = 11 });
        (double[] vm, double[] va) = Radians(truth);
        // Every 7th row against the full-solve reference keeps the test about a second; the new stage is
        // still computed (and serial/parallel compared) over every row.
        AssertMatchesReference(model, set, vm, va, stride: 7, expectCritical: false);
    }

    [Fact]
    public void Sparse_residual_covariance_is_bit_identical_with_critical_measurements()
    {
        // Make a leaf bus observed ONLY by the P and Q flows on its single branch: two rows for its two
        // states (Va, Vm), so both are critical (Omega ~ 0, no redundancy behind them).
        GridModel model = IeeeCases.Case118();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet full = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { Seed = 3 });
        var degree = new Dictionary<int, int>();
        foreach (Branch br in model.Branches.Where(b => b.InService))
        {
            degree[br.From] = degree.GetValueOrDefault(br.From) + 1;
            degree[br.To] = degree.GetValueOrDefault(br.To) + 1;
        }
        Bus leaf = model.Buses.First(b => b.Type == BusType.Pq && degree.GetValueOrDefault(b.Id) == 1);
        Branch link = model.Branches.First(b => b.InService && (b.From == leaf.Id || b.To == leaf.Id));
        int neighbour = link.From == leaf.Id ? link.To : link.From;
        bool TouchesLeaf(Measurement x) =>
            x.IsBranch ? x.From == leaf.Id || x.To == leaf.Id
                       : x.Bus == leaf.Id || (x.Bus == neighbour && x.Kind is MeasurementKind.PInjMw or MeasurementKind.QInjMvar);
        var kept = full.Measurements.Where(x => !TouchesLeaf(x)).ToList();
        Measurement[] flows = full.Measurements
            .Where(x => x.IsBranch && x.From == link.From && x.To == link.To && x.End == BranchEnd.From)
            .Where(x => x.Kind is MeasurementKind.PFlowMw or MeasurementKind.QFlowMvar)
            .ToArray();
        Assert.Equal(2, flows.Length);
        kept.AddRange(flows);
        MeasurementSet set = full with { Measurements = kept };
        (double[] vm, double[] va) = Radians(truth);
        AssertMatchesReference(model, set, vm, va, stride: 1, expectCritical: true);
    }

    [Fact]
    public void Sparse_step_normalized_residuals_match_the_reference_at_a_converged_estimate()
    {
        // End to end through the estimator's own SparseWlsStep (its private prepared structure and RCM
        // ordering) at a converged sparse estimate with gross errors injected: the step's output must equal
        // the full-solve loop run on the step's own factor, recovered here by refactoring the same gain.
        GridModel model = IeeeCases.Case300();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { Seed = 5 });
        var corrupted = set.Measurements.ToArray();
        for (int k = 0; k < corrupted.Length; k += 97)
            corrupted[k] = corrupted[k] with { Value = corrupted[k].Value + 25 * corrupted[k].Sigma };
        set = set with { Measurements = corrupted };

        EstimationResult est = new WlsStateEstimator().Estimate(model, set, new EstimationOptions
        {
            SparseBusThreshold = 0, DetectBadData = false,
        });
        Assert.Equal(EstimationStatus.Converged, est.Status);
        double[] vm = (double[])est.Vm.Clone();
        double[] va = est.VaDeg.Select(d => d * Math.PI / 180.0).ToArray();

        var mm = new MeasurementModel(model, set);
        int m = mm.MeasurementCount;
        var w = new double[m];
        for (int r = 0; r < m; r++) w[r] = 1.0 / (mm.SigmaPu[r] * mm.SigmaPu[r]);
        var h = new double[m];
        mm.EvaluateH(vm, va, h);
        var res = new double[m];
        for (int r = 0; r < m; r++) res[r] = mm.ZPu[r] - h[r];

        var step = new SparseWlsStep();
        step.Begin(model, mm, w);
        (double[] viaStep, bool[] critStep) = step.NormalizedResiduals(vm, va, res);

        // The step's own factor: its prepared CSC arrays (filled at vm, va by the call above) refactored with
        // its own RCM ordering - the factorisation is deterministic, so this is the factor the step used.
        T Field<T>(string name) => (T)typeof(SparseWlsStep)
            .GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(step)!;
        SparseLuFactorization lu = SparseLu.FactorPrepared(mm.StateSize, Field<int[]>("_ap"), Field<int[]>("_ai"),
            Field<double[]>("_ax"), Field<int[]>("_q")) ?? throw new InvalidOperationException("singular");
        SparseJacobianRows jac = Field<SparseJacobianRows>("_jac");
        (double[] refN, bool[] refC) = ReferenceLoop(mm, jac, lu, res, w, stride: 1);
        AssertBitsEqual(refN, viaStep, 1);
        Assert.Equal(refC, critStep);
        Assert.Contains(viaStep, v => v > 3.0);   // the gross errors are visible
    }

    // ---- The evaluator itself on adversarial matrices -------------------------------------------------

    /// <summary>The reference the evaluator must reproduce: dense scatter (last entry wins), full Solve,
    /// in-order gather and sum.</summary>
    private static double ReferenceForm(SparseLuFactorization lu, int n, (int Col, double Val)[] h)
    {
        var dense = new double[n];
        foreach ((int c, double v) in h) dense[c] = v;
        double[] x = lu.Solve(dense);
        double q = 0;
        foreach ((int c, double v) in h) q += v * x[c];
        return q;
    }

    [Fact]
    public void Inverse_quadratic_form_matches_the_full_solve_on_random_unsymmetric_matrices()
    {
        var rng = new Random(20261002);
        for (int trial = 0; trial < 40; trial++)
        {
            int n = 5 + rng.Next(60);
            var b = new CsrMatrix.Builder(n);
            for (int i = 0; i < n; i++)
            {
                // Weak diagonal so partial pivoting genuinely swaps rows.
                b.Add(i, i, 0.01 + rng.NextDouble());
                int extra = 1 + rng.Next(4);
                for (int e = 0; e < extra; e++) b.Add(i, rng.Next(n), rng.NextDouble() * 4 - 2);
            }
            SparseLuFactorization? lu = SparseLu.Factor(b.Build());
            if (lu is null) continue;
            SparseLuFactorization.InverseQuadraticForm form = lu.CreateInverseQuadraticForm();
            var work = new double[n];
            for (int q = 0; q < 25; q++)
            {
                int len = rng.Next(6);   // includes the empty row
                var h = new (int, double)[len];
                for (int p = 0; p < len; p++) h[p] = (rng.Next(n), rng.NextDouble() * 2 - 1);
                if (len >= 2 && q % 3 == 0) h[1] = (h[0].Item1, h[1].Item2);   // a duplicate column: last wins
                double expected = ReferenceForm(lu, n, h);
                double actual = form.Evaluate(h, work);
                Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
                    $"trial {trial} q {q}: expected {expected:R}, got {actual:R}");
                Assert.All(work, v => Assert.True(BitConverter.DoubleToInt64Bits(v) == 0L, "workspace not restored to +0"));
            }
        }
    }

    [Theory]
    [InlineData(SimdTier.Avx512)]
    [InlineData(SimdTier.Avx256)]
    [InlineData(SimdTier.Sse128)]
    [InlineData(SimdTier.Scalar)]
    public void Blocked_inverse_quadratic_form_matches_the_full_solve_per_lane_at_every_simd_tier(SimdTier tier)
    {
        // Every tier's arm runs on any machine (a wider-than-hardware vector runs in software, slowly but
        // exactly), so a broken arm fails here wherever the suite runs.
        const int K = SparseLuFactorization.InverseQuadraticForm.BlockWidth;
        var rng = new Random(4242 + (int)tier);
        for (int trial = 0; trial < 25; trial++)
        {
            int n = 5 + rng.Next(50);
            var b = new CsrMatrix.Builder(n);
            for (int i = 0; i < n; i++)
            {
                b.Add(i, i, 0.01 + rng.NextDouble());
                int extra = 1 + rng.Next(4);
                for (int e = 0; e < extra; e++) b.Add(i, rng.Next(n), rng.NextDouble() * 4 - 2);
            }
            SparseLuFactorization? lu = SparseLu.Factor(b.Build());
            if (lu is null) continue;
            SparseLuFactorization.InverseQuadraticForm form = lu.CreateInverseQuadraticForm();

            int m = 1 + rng.Next(30);
            var rows = new List<(int Col, double Val)>[m];
            for (int r = 0; r < m; r++)
            {
                int len = rng.Next(6);   // empty rows included
                rows[r] = new List<(int Col, double Val)>(len);
                for (int p = 0; p < len; p++) rows[r].Add((rng.Next(n), rng.NextDouble() * 2 - 1));
                if (len >= 2 && r % 4 == 0) rows[r][1] = (rows[r][0].Col, rows[r][1].Val);   // duplicate column
            }
            // A deliberately scrambled lane order (blocks need not be sorted to be exact).
            int[] order = Enumerable.Range(0, m).OrderBy(_ => rng.Next()).ToArray();
            var q = new double[m];
            var work = new double[n * K];
            for (int start = 0; start < m; start += K)
            {
                form.EvaluateBlock(rows, order, start, Math.Min(K, m - start), q, work, tier);
                Assert.All(work, v => Assert.True(BitConverter.DoubleToInt64Bits(v) == 0L, "block workspace not restored to +0"));
            }
            for (int r = 0; r < m; r++)
            {
                double expected = ReferenceForm(lu, n, rows[r].ToArray());
                Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(q[r]),
                    $"tier {tier} trial {trial} row {r}: expected {expected:R}, got {q[r]:R}");
            }
        }
    }

    [Fact]
    public void Blocked_inverse_quadratic_form_rejects_bad_block_arguments()
    {
        var b = new CsrMatrix.Builder(3);
        for (int i = 0; i < 3; i++) b.Add(i, i, 2.0);
        SparseLuFactorization.InverseQuadraticForm form = SparseLu.Factor(b.Build())!.CreateInverseQuadraticForm();
        var rows = new[] { new List<(int Col, double Val)> { (0, 1.0) } };
        int[] order = { 0 };
        var q = new double[1];
        Assert.Throws<ArgumentOutOfRangeException>(() => form.EvaluateBlock(rows, order, 0, 0, q, new double[24]));
        Assert.Throws<ArgumentOutOfRangeException>(() => form.EvaluateBlock(rows, order, 0, 9, q, new double[24]));
        Assert.Throws<ArgumentException>(() => form.EvaluateBlock(rows, order, 0, 1, q, new double[3]));
    }

    [Fact]
    public void Inverse_quadratic_form_with_non_finite_factors_reproduces_the_reference_exactly()
    {
        // Whichever column the ordering eliminates first, the second column's update is
        // 1.5e308 + (2/3) * 1e308 = 2.17e308, which overflows to +Inf; the pivot test still passes (an infinite
        // pivot is not small), so the factor exists with a non-finite entry and the evaluator must take the
        // full reference path.
        var b = new CsrMatrix.Builder(2);
        b.Add(0, 0, 1e308); b.Add(0, 1, 1.5e308);
        b.Add(1, 0, 1.5e308); b.Add(1, 1, -1e308);
        SparseLuFactorization? lu = SparseLu.Factor(b.Build());
        Assert.NotNull(lu);
        SparseLuFactorization.InverseQuadraticForm form = lu!.CreateInverseQuadraticForm();
        Assert.False(form.HasFiniteFactors);
        var work = new double[2];
        foreach ((int, double)[] h in new[] { new[] { (0, 1.0) }, new[] { (1, 2.0) }, new[] { (0, 0.5), (1, -0.25) } })
        {
            double expected = ReferenceForm(lu, 2, h);
            double actual = form.Evaluate(h, work);
            Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
                $"expected {expected:R}, got {actual:R}");
            Assert.All(work, v => Assert.Equal(0.0, v));
        }

        // The blocked path takes the same reference route for every lane.
        var rows = new[]
        {
            new List<(int Col, double Val)> { (0, 1.0) }, new List<(int Col, double Val)>(),
            new List<(int Col, double Val)> { (0, 0.5), (1, -0.25) },
        };
        var q = new double[3];
        var blockWork = new double[2 * SparseLuFactorization.InverseQuadraticForm.BlockWidth];
        form.EvaluateBlock(rows, new[] { 0, 1, 2 }, 0, 3, q, blockWork);
        for (int r = 0; r < 3; r++)
        {
            double expected = ReferenceForm(lu, 2, rows[r].ToArray());
            Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(q[r]),
                $"row {r}: expected {expected:R}, got {q[r]:R}");
        }
        Assert.All(blockWork, v => Assert.Equal(0.0, v));
    }

    [Fact]
    public void Inverse_quadratic_form_rejects_a_workspace_of_the_wrong_length()
    {
        var b = new CsrMatrix.Builder(3);
        for (int i = 0; i < 3; i++) b.Add(i, i, 2.0);
        SparseLuFactorization lu = SparseLu.Factor(b.Build())!;
        SparseLuFactorization.InverseQuadraticForm form = lu.CreateInverseQuadraticForm();
        Assert.Throws<ArgumentException>(() => form.Evaluate(new[] { (0, 1.0) }, new double[2]));
    }

    [Fact]
    public void Sparse_covariance_stage_no_longer_runs_a_full_solve_per_row()
    {
        // Anti-drift: the per-row full Solve (and its allocations) must not come back into the sparse path.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        string src = File.ReadAllText(Path.Combine(dir!.FullName, "src", "GridSim.Core", "Estimation", "BadDataAnalysis.cs"));
        int i = src.IndexOf("MeasurementModel mm, SparseJacobianRows jac, SparseLuFactorization lu", StringComparison.Ordinal);
        Assert.True(i > 0);
        int end = src.IndexOf("private static void ClassifyRow", i, StringComparison.Ordinal);
        string body = src[i..end];
        Assert.DoesNotContain("lu.Solve(", body);
        Assert.Contains("CreateInverseQuadraticForm", body);
    }
}
