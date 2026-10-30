using System;
using System.Linq;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// CF5 (N330): the sparse projection statistics (<see cref="ProjectionStatistics.ComputeSparse"/>), checked against a
/// reference computed by hand, plus the measurement that kept them out of Schweppe-GM (they still make every injection
/// row a leverage point on clean case14).
/// </summary>
public class Cf5ProjectionStatisticsTests
{
    // The hand example. Nine rows in three columns (a, b, c). Every column is zero in at least five of the nine rows, so
    // the coordinate-wise median is (0, 0, 0) and each direction is the row itself, normalised:
    //   r0..r2 = (1,0,0), (2,0,0), (3,0,0); r3..r5 = (0,1,0), (0,2,0), (0,3,0); r6, r7 = (0,0,1), (0,0,2); r8 = (10,10,0).
    //
    // Direction e_a (rows 0, 1, 2): overlap {r0, r1, r2, r8}, projections 1, 2, 3, 10. Median (2 + 3) / 2 = 2.5;
    //   deviations 1.5, 0.5, 0.5, 7.5, their median 1.0, MAD = 1.4826. P = 1.5, 0.5, 0.5, 7.5 over 1.4826.
    // Direction e_b (rows 3, 4, 5): the same numbers on {r3, r4, r5, r8}.
    // Direction e_c (rows 6, 7): overlap {r6, r7}, projections 1, 2, median 1.5, deviations 0.5, 0.5, MAD = 1.4826 x 0.5.
    //   P = 0.5 / (0.5 x 1.4826) = 1 / 1.4826 for both.
    // Direction (1,1,0)/sqrt 2 (row 8): overlap {r0..r5, r8}, projections (1, 2, 3, 1, 2, 3, 20) / sqrt 2. Median 2 / sqrt 2;
    //   deviations (1, 0, 1, 1, 0, 1, 18) / sqrt 2, their median 1 / sqrt 2, MAD = 1.4826 / sqrt 2. P = (1, 0, 1, 1, 0, 1, 18)
    //   over 1.4826.
    // PS = max over the directions that reach each row:
    //   r0 1.5, r1 0.5, r2 1.0, r3 1.5, r4 0.5, r5 1.0, r6 1.0, r7 1.0, r8 18 (all over 1.4826).
    private static readonly double[][] HandRows =
    {
        new[] { 1.0, 0.0, 0.0 }, new[] { 2.0, 0.0, 0.0 }, new[] { 3.0, 0.0, 0.0 },
        new[] { 0.0, 1.0, 0.0 }, new[] { 0.0, 2.0, 0.0 }, new[] { 0.0, 3.0, 0.0 },
        new[] { 0.0, 0.0, 1.0 }, new[] { 0.0, 0.0, 2.0 },
        new[] { 10.0, 10.0, 0.0 },
    };

    private static readonly double[] HandPsNumerators = { 1.5, 0.5, 1.0, 1.5, 0.5, 1.0, 1.0, 1.0, 18.0 };

    [Fact]
    public void Sparse_projection_statistics_match_the_hand_computed_reference()
    {
        double[] ps = ProjectionStatistics.ComputeSparse(HandRows);
        Assert.Equal(HandRows.Length, ps.Length);
        for (int i = 0; i < ps.Length; i++)
        {
            double expected = HandPsNumerators[i] / ProjectionStatistics.MadNormalConsistency;
            // The only rounding is the 1/sqrt 2 normalisation of row 8's direction (a few ulp); the axis directions are
            // exact, so most rows agree to the last bit and every row to 1e-14 relative.
            Assert.True(Math.Abs(ps[i] - expected) <= 1e-14 * expected,
                $"row {i}: PS {ps[i]:R}, hand reference {expected:R}");
        }
    }

    [Fact]
    public void The_axis_rows_of_the_hand_reference_are_exact_to_the_bit()
    {
        // Rows 0, 3 (P = 1.5 / 1.4826 from an axis direction, larger than their 1 / 1.4826 from row 8's direction) and
        // rows 6, 7 (only the e_c direction reaches them) involve no rounded normalisation.
        double[] ps = ProjectionStatistics.ComputeSparse(HandRows);
        Assert.Equal(1.5 / 1.4826, ps[0]);
        Assert.Equal(1.5 / 1.4826, ps[3]);
        Assert.Equal(0.5 / (1.4826 * 0.5), ps[6]);
        Assert.Equal(0.5 / (1.4826 * 0.5), ps[7]);
    }

    [Fact]
    public void The_leverage_row_of_the_hand_reference_is_down_weighted_and_the_rest_are_not()
    {
        double[] ps = ProjectionStatistics.ComputeSparse(HandRows);
        // Cutoff for a two-nonzero row: sqrt(chi-square 0.975 quantile, 2 dof). The exact quantile is -2 ln 0.025 =
        // 7.3778, sqrt 2.7162; ChiSquare.Quantile is the Wilson-Hilferty approximation (documented on the class: under
        // 1% for k >= 3, sagging at small k), which at k = 2 gives 7.3360, sqrt 2.70851 (0.28% low).
        // Corrected 2026-10-11 (end pass, first run): the range assumed the exact quantile, but the helper the test
        // calls is Wilson-Hilferty and is pinned elsewhere (the estimator's chi-square thresholds), so the range now
        // brackets what it returns, 2.7085094926663924: Assert.InRange(d, 2.71, 2.72) -> Assert.InRange(d, 2.7085, 2.7086).
        // The leverage verdicts below hold for either cutoff (rows 0..7 have PS at most 1.012, row 8 has 12.14).
        // GR-539: ChiSquare.Quantile now returns the exact closed form at 1 and 2 dof, so at k = 2 it is -2 ln 0.025 =
        // 7.377758908227871, sqrt 2.716203031481239: Assert.InRange(d, 2.7085, 2.7086) -> Assert.InRange(d, 2.7162, 2.7163).
        double d = Math.Sqrt(ChiSquare.Quantile(2, 0.975));
        Assert.InRange(d, 2.7162, 2.7163);
        for (int i = 0; i < 8; i++) Assert.Equal(1.0, ProjectionStatistics.LeverageWeight(ps[i], d));
        double a8 = ProjectionStatistics.LeverageWeight(ps[8], d);
        Assert.True(a8 < 0.1, $"row 8 (PS {ps[8]:R}) should be a leverage point; weight {a8:R}");
    }

    [Fact]
    public void The_dense_form_on_the_same_rows_differs_which_is_the_point_of_the_sparse_form()
    {
        // In the dense form the e_c direction's projections are (0,0,0,0,0,0,1,2,0): median 0, MAD 0, so the direction
        // is skipped and rows 6, 7 take their PS from row 8's direction only, where they project to 0 among mostly
        // nonzero values. The two forms must not agree on this matrix.
        double[] dense = ProjectionStatistics.Compute(HandRows);
        double[] sparse = ProjectionStatistics.ComputeSparse(HandRows);
        Assert.False(dense.SequenceEqual(sparse));
    }

    [Fact]
    public void Sparse_projection_statistics_are_deterministic_bit_for_bit()
    {
        double[] a = ProjectionStatistics.ComputeSparse(HandRows);
        double[] b = ProjectionStatistics.ComputeSparse(HandRows.Select(r => (double[])r.Clone()).ToArray());
        for (int i = 0; i < a.Length; i++) Assert.Equal(BitConverter.DoubleToInt64Bits(a[i]), BitConverter.DoubleToInt64Bits(b[i]));
    }

    [Fact]
    public void Sparse_projection_statistics_refuse_bad_input_loudly()
    {
        Assert.Throws<ArgumentNullException>(() => ProjectionStatistics.ComputeSparse(null!));
        Assert.Empty(ProjectionStatistics.ComputeSparse(Array.Empty<double[]>()));
        Assert.Throws<ArgumentException>(() => ProjectionStatistics.ComputeSparse(new[] { new[] { 1.0, 0.0 }, new[] { 1.0 } }));
        Assert.Throws<ArgumentException>(() => ProjectionStatistics.ComputeSparse(new[] { new[] { 1.0, double.NaN } }));
        Assert.Throws<ArgumentException>(() => ProjectionStatistics.ComputeSparse(new[] { new[] { 1.0, 0.0 }, null! }));
        Assert.Throws<ArgumentException>(() => ProjectionStatistics.ComputeSparse(new[] { new[] { double.PositiveInfinity } }));
    }

    [Fact]
    public void On_clean_case14_the_sparse_form_still_makes_every_injection_row_a_leverage_point()
    {
        // Why the sparse form is NOT wired into Schweppe-GM (CF5, N330). Measured at the estimator's state on
        // `estimate case14 --synth --seed 3 --method schweppe-gm --gm-leverage ps` (a probe build, 2026-10-11): median
        // a_i 0.0217 for P injections, 0.0669 for Q injections, 0.181 / 0.278 for P / Q flows, 1 for |V|; the GM fit then
        // gave J = 509.80 against chi2(95 %) = 118.75 (FAIL) where the hat leverage gives 86.34 (PASS). Here at the true
        // state (a slightly different Jacobian) the same pattern must hold: injection rows far below 1, |V| rows at 1.
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(truth.Converged);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { Seed = 3 });
        var mm = new MeasurementModel(model, set);
        var rows = new SparseJacobianRows(mm.MeasurementCount);
        mm.EvaluateJacobian(truth.Vm, truth.VaDeg.Select(d => d * Math.PI / 180.0).ToArray(), rows);
        var points = Enumerable.Range(0, mm.MeasurementCount).Select(i =>
        {
            var p = new double[mm.StateSize];
            foreach ((int c, double v) in rows.Rows[i]) p[c] = v;
            return p;
        }).ToList();
        double[] ps = ProjectionStatistics.ComputeSparse(points);
        double[] a = Enumerable.Range(0, ps.Length)
            .Select(i => ProjectionStatistics.LeverageWeight(ps[i], Math.Sqrt(ChiSquare.Quantile(Math.Max(1, rows.Rows[i].Count), 0.975))))
            .ToArray();

        double MedianWeight(MeasurementKind kind)
        {
            double[] w = Enumerable.Range(0, a.Length).Where(i => mm.Measurements[i].Kind == kind).Select(i => a[i]).OrderBy(x => x).ToArray();
            Assert.NotEmpty(w);
            return w[w.Length / 2];
        }

        Assert.True(MedianWeight(MeasurementKind.PInjMw) < 0.2, $"P injection median weight {MedianWeight(MeasurementKind.PInjMw):R}: if this is near 1 now, re-try the N330 wiring");
        Assert.True(MedianWeight(MeasurementKind.QInjMvar) < 0.2);
        Assert.Equal(1.0, MedianWeight(MeasurementKind.VmPu));
    }
}
