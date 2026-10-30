using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// October 2026 review, WS11 R118: the observability gain-rank elimination now runs the bit-exact SIMD
/// scaled-row kernel over contiguous storage and stops each row update at the pivot row's nonzero extent.
/// These tests pin that its rank AND its dead (free) columns are exactly those of the original scalar
/// full-width loop, kept here verbatim, on estimator gains (observable, thin and unobservable sets) and on
/// adversarial rank-deficient matrices with tiny below-floor entries.
/// </summary>
public class ObservabilityRankBitIdentityTests
{
    private const double RankFloor = GridSim.Core.Numerics.NumericTolerances.RelativePivotFloor;

    /// <summary>The pre-R118 GainRank, verbatim.</summary>
    private static (int Rank, List<int> DeadColumns) ReferenceRank(double[,] gain, int s)
    {
        var a = new double[s, s];
        Array.Copy(gain, a, gain.Length);

        double norm = 0;
        foreach (double v in a) { double av = Math.Abs(v); if (av > norm) norm = av; }
        double floor = norm * RankFloor;

        var dead = new List<int>();
        int row = 0;
        for (int col = 0; col < s; col++)
        {
            int p = row;
            double max = row < s ? Math.Abs(a[row, col]) : 0;
            for (int r = row + 1; r < s; r++)
            {
                double v = Math.Abs(a[r, col]);
                if (v > max) { max = v; p = r; }
            }
            if (row >= s || max <= floor)
            {
                dead.Add(col);
                continue;
            }

            if (p != row)
                for (int j = col; j < s; j++)
                    (a[row, j], a[p, j]) = (a[p, j], a[row, j]);

            double pivot = a[row, col];
            for (int r = row + 1; r < s; r++)
            {
                double factor = a[r, col] / pivot;
                if (factor == 0) continue;
                for (int j = col; j < s; j++) a[r, j] -= factor * a[row, j];
            }
            row++;
        }
        return (row, dead);
    }

    private static void AssertSame(double[,] gain, int s)
    {
        double[,] copy = (double[,])gain.Clone();
        (int refRank, List<int> refDead) = ReferenceRank(gain, s);
        (int rank, List<int> dead) = ObservabilityAnalysis.GainRank(gain, s);
        Assert.Equal(refRank, rank);
        Assert.Equal(refDead, dead);
        // The input is not modified (the elimination runs on a copy).
        for (int i = 0; i < s; i++)
            for (int j = 0; j < s; j++)
                Assert.True(BitConverter.DoubleToInt64Bits(copy[i, j]) == BitConverter.DoubleToInt64Bits(gain[i, j]));
    }

    private static double[,] EstimatorGain(GridModel model, MeasurementSet set)
    {
        var mm = new MeasurementModel(model, set);
        int m = mm.MeasurementCount, s = mm.StateSize;
        var w = new double[m];
        for (int r = 0; r < m; r++) w[r] = 1.0 / (mm.SigmaPu[r] * mm.SigmaPu[r]);
        var vm = Enumerable.Repeat(1.0, model.BusCount).ToArray();
        var va = new double[model.BusCount];
        var h = new double[m];
        mm.EvaluateH(vm, va, h);
        var jac = new double[m, s];
        mm.EvaluateJacobian(vm, va, jac);
        var gain = new double[s, s];
        DenseWlsStep.FormNormalEquations(mm.ZPu, h, w, jac, gain, new double[s]);
        return gain;
    }

    [Theory]
    [InlineData("14", 1.0, 1.0, 1.0)]
    [InlineData("118", 1.0, 1.0, 1.0)]
    [InlineData("118", 0.3, 0.5, 0.4)]   // thin: may leave free directions
    [InlineData("118", 0.1, 0.1, 0.1)]   // very thin: unobservable, several dead columns
    [InlineData("300", 1.0, 1.0, 1.0)]
    [InlineData("300", 0.2, 0.3, 0.3)]
    public void Gain_rank_and_dead_columns_match_the_original_loop_on_estimator_gains(
        string name, double vmCov, double injCov, double flowCov)
    {
        GridModel model = name switch { "14" => IeeeCases.Case14(), "118" => IeeeCases.Case118(), _ => IeeeCases.Case300() };
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions
        {
            Seed = 99, VmCoverage = vmCov, InjCoverage = injCov, FlowCoverage = flowCov,
        });
        double[,] gain = EstimatorGain(model, set);
        AssertSame(gain, gain.GetLength(0));
    }

    /// <summary>
    /// S5 (R118 remainder): the sparse-backend gate now eliminates in its own densified copy instead of a second copy.
    /// The in-place rank must be the verbatim loop's, and the sparse gate's report (through the CSC arrays) must equal
    /// the dense Analyze report on the same gain, field for field.
    /// </summary>
    [Theory]
    [InlineData("118", 1.0, 1.0, 1.0)]
    [InlineData("118", 0.1, 0.1, 0.1)]
    [InlineData("300", 1.0, 1.0, 1.0)]
    [InlineData("300", 0.2, 0.3, 0.3)]
    public void In_place_rank_and_the_sparse_gate_match_the_original_loop(string name, double vmCov, double injCov, double flowCov)
    {
        GridModel model = name == "118" ? IeeeCases.Case118() : IeeeCases.Case300();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions
        {
            Seed = 99, VmCoverage = vmCov, InjCoverage = injCov, FlowCoverage = flowCov,
        });
        double[,] gain = EstimatorGain(model, set);
        int s = gain.GetLength(0);
        (int refRank, List<int> refDead) = ReferenceRank(gain, s);
        (int rank, List<int> dead) = ObservabilityAnalysis.GainRankInPlace((double[,])gain.Clone(), s);
        Assert.Equal(refRank, rank);
        Assert.Equal(refDead, dead);

        // The gain as the symmetric CSC arrays SparseWlsStep holds (column by column, rows ascending, nonzeros only).
        var ap = new int[s + 1];
        var ai = new List<int>();
        var ax = new List<double>();
        for (int c = 0; c < s; c++)
        {
            for (int r = 0; r < s; r++)
                if (gain[r, c] != 0.0) { ai.Add(r); ax.Add(gain[r, c]); }
            ap[c + 1] = ai.Count;
        }
        var mm = new MeasurementModel(model, set);
        ObservabilityReport sparse = ObservabilityAnalysis.AnalyzeSparse(model, mm, ap, ai.ToArray(), ax.ToArray(), maxState: int.MaxValue);
        ObservabilityReport dense = ObservabilityAnalysis.Analyze(model, mm, gain);
        Assert.Equal(dense.Observable, sparse.Observable);
        Assert.Equal(dense.Rank, sparse.Rank);
        Assert.Equal(refRank, sparse.Rank);
        Assert.Equal(dense.StateSize, sparse.StateSize);
        Assert.Equal(dense.UnobservableBuses, sparse.UnobservableBuses);
        Assert.Equal(dense.UnobservableStates, sparse.UnobservableStates);
        Assert.Equal(dense.UnmeasuredIslandBuses, sparse.UnmeasuredIslandBuses);
        Assert.Throws<ArgumentException>(() => ObservabilityAnalysis.GainRankInPlace(new double[3, 4], 3));
    }

    [Fact]
    public void Very_thin_set_really_exercises_dead_columns()
    {
        // Guard the theory above: the 10% set must actually be rank-deficient, or it proves nothing.
        GridModel model = IeeeCases.Case118();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions
        {
            Seed = 99, VmCoverage = 0.1, InjCoverage = 0.1, FlowCoverage = 0.1,
        });
        double[,] gain = EstimatorGain(model, set);
        (int rank, List<int> dead) = ObservabilityAnalysis.GainRank(gain, gain.GetLength(0));
        Assert.True(dead.Count > 0, "expected free directions in a 10% measurement set");
        Assert.Equal(gain.GetLength(0), rank + dead.Count);
    }

    [Fact]
    public void Gain_rank_matches_the_original_loop_on_random_rank_deficient_matrices()
    {
        var rng = new Random(118);
        for (int trial = 0; trial < 60; trial++)
        {
            int s = 1 + rng.Next(40);
            int k = rng.Next(s + 1);   // target rank (0 .. s)
            // G = B B^T with B (s x k) sparse-ish: symmetric PSD of rank <= k, plus tiny noise on a few entries
            // so some dead columns carry nonzero below-floor values (the partial-swap path of the loop).
            var b = new double[s, Math.Max(k, 1)];
            for (int i = 0; i < s; i++)
                for (int j = 0; j < k; j++)
                    if (rng.NextDouble() < 0.4) b[i, j] = rng.NextDouble() * 2 - 1;
            var g = new double[s, s];
            for (int i = 0; i < s; i++)
                for (int j = 0; j < s; j++)
                {
                    double acc = 0;
                    for (int t = 0; t < k; t++) acc += b[i, t] * b[j, t];
                    g[i, j] = acc;
                }
            if (trial % 3 == 0)
                for (int e = 0; e < 3; e++) g[rng.Next(s), rng.Next(s)] += 1e-18 * rng.NextDouble();
            AssertSame(g, s);
        }
    }

    [Fact]
    public void Gain_rank_handles_the_empty_and_zero_matrices_like_the_original()
    {
        AssertSame(new double[0, 0], 0);
        AssertSame(new double[3, 3], 3);
    }
}
