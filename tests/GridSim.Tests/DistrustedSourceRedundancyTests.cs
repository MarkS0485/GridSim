using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// CAF-9 / gap B2 (D1 distrusted-source containment, November 2026): the estimator side of the control,
/// <see cref="EstimationOptions.DistrustedSources"/>.
/// <list type="bullet">
/// <item>Feature off is bit-identical: with no option, an explicit null, an empty control, or a control that names a
/// source no row carries, the state vector, J, the chi-square threshold and verdict, the degrees of freedom, the
/// iteration count and the removals are exactly equal (exact <c>Assert.Equal</c>, no tolerance) to a run with default
/// options, on two cases (case14 and case30, noisy synthetic sets).</item>
/// <item>Feature on: every row of a distrusted source is demoted (sigma multiplied by the inflation factor, value and
/// source unchanged) and left out of the chi-square redundancy: the degrees of freedom fall by exactly the number of
/// distrusted rows, and the threshold is the quantile at that smaller count.</item>
/// <item>Fail loud: HTI refuses the option, and a row with no source tag is refused when a distrusted source is named.</item>
/// </list>
/// </summary>
public sealed class DistrustedSourceRedundancyTests
{
    /// <summary>The source tag the tests give to the rows they distrust.</summary>
    private const string SuspectTag = "feed-x";

    /// <summary>A noisy synthetic set of the case (every row tagged <c>synthetic</c> by the sampler).</summary>
    private static (GridModel Model, MeasurementSet Set) NoisyCase(GridModel model, int seed)
    {
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(truth.Converged);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { Seed = seed, NoiseScale = 1.0 });
        return (model, set);
    }

    /// <summary>Re-tag the rows at <paramref name="indices"/> with <see cref="SuspectTag"/>; every other row keeps its tag.
    /// Only the tag changes, so the set is otherwise the same numbers in the same order.</summary>
    private static MeasurementSet Retag(MeasurementSet set, IReadOnlyCollection<int> indices)
    {
        var rows = set.Measurements.ToList();
        foreach (int i in indices) rows[i] = rows[i] with { Source = SuspectTag };
        return set with { Measurements = rows };
    }

    /// <summary>Every number an estimate reports that the chi-square test or the state depends on, compared exactly.</summary>
    private static void AssertBitIdentical(EstimationResult expected, EstimationResult actual)
    {
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Iterations, actual.Iterations);
        Assert.Equal(expected.Vm, actual.Vm);          // exact: double equality, element by element
        Assert.Equal(expected.VaDeg, actual.VaDeg);
        Assert.Equal(expected.ObjectiveJ, actual.ObjectiveJ);
        Assert.Equal(expected.ChiSquareThreshold, actual.ChiSquareThreshold);
        Assert.Equal(expected.ChiSquareTest, actual.ChiSquareTest);
        Assert.Equal(expected.DegreesOfFreedom, actual.DegreesOfFreedom);
        Assert.Equal(expected.Removed.Count, actual.Removed.Count);
        Assert.Equal(expected.Residuals.Count, actual.Residuals.Count);
        for (int r = 0; r < expected.Residuals.Count; r++)
        {
            Assert.Equal(expected.Residuals[r].Residual, actual.Residuals[r].Residual);
            Assert.Equal(expected.Residuals[r].Measurement.Sigma, actual.Residuals[r].Measurement.Sigma);
        }
    }

    public static TheoryData<string, int> Cases() => new()
    {
        { "case14", 20260713 },
        { "case30", 7 },
    };

    private static GridModel Load(string name) => name switch
    {
        "case14" => IeeeCases.Case14(),
        "case30" => IeeeCases.Case30(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "not a test case"),
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void B2_with_no_distrusted_source_every_number_is_bit_identical(string caseName, int seed)
    {
        (GridModel model, MeasurementSet set) = NoisyCase(Load(caseName), seed);
        var estimator = new WlsStateEstimator();

        // The reference: default options (the property is not set at all), as every caller ran before the option existed.
        EstimationResult reference = estimator.Estimate(model, set);
        Assert.Equal(EstimationStatus.Converged, reference.Status);
        Assert.NotEqual(ChiSquareOutcome.NotRun, reference.ChiSquareTest);   // the chi-square path was really exercised

        // Feature off, four ways: explicit null, an empty control, a control naming a tag no row carries, and default
        // options passed explicitly. Each must reproduce the reference bit for bit.
        AssertBitIdentical(reference, estimator.Estimate(model, set, new EstimationOptions()));
        AssertBitIdentical(reference, estimator.Estimate(model, set, new EstimationOptions { DistrustedSources = null }));
        AssertBitIdentical(reference, estimator.Estimate(model, set,
            new EstimationOptions { DistrustedSources = new DistrustedSources(Array.Empty<string>()) }));
        AssertBitIdentical(reference, estimator.Estimate(model, set,
            new EstimationOptions { DistrustedSources = new DistrustedSources(new[] { "no-such-feed" }) }));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void B2_a_distrusted_feed_that_is_absent_from_the_set_changes_nothing_under_the_robust_method(string caseName, int seed)
    {
        // The robust branch has its own J sum (against the declared sigmas); it too must be untouched when no row matches.
        (GridModel model, MeasurementSet set) = NoisyCase(Load(caseName), seed);
        var estimator = new WlsStateEstimator();
        EstimationResult reference = estimator.Estimate(model, set, new EstimationOptions { Method = EstimationMethod.Huber });
        AssertBitIdentical(reference, estimator.Estimate(model, set,
            new EstimationOptions { Method = EstimationMethod.Huber, DistrustedSources = new DistrustedSources(new[] { "no-such-feed" }) }));
    }

    [Fact]
    public void B2_the_degrees_of_freedom_exclude_exactly_the_distrusted_rows()
    {
        // Exact data (no noise) so the bad-data loop removes nothing and the row count is the whole set.
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet exact = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { NoiseScale = 0.0 });

        // Distrust three rows: the first three real-power flow rows (their states are still observed by the others).
        int[] suspects = exact.Measurements.Select((m, i) => (m, i)).Where(t => t.m.Kind == MeasurementKind.PFlowMw)
            .Take(3).Select(t => t.i).ToArray();
        Assert.Equal(3, suspects.Length);
        MeasurementSet tagged = Retag(exact, suspects);

        var estimator = new WlsStateEstimator();
        EstimationResult off = estimator.Estimate(model, tagged);
        EstimationResult on = estimator.Estimate(model, tagged,
            new EstimationOptions { DistrustedSources = new DistrustedSources(new[] { SuspectTag }) });

        Assert.Equal(EstimationStatus.Converged, off.Status);
        Assert.Equal(EstimationStatus.Converged, on.Status);
        Assert.Empty(off.Removed);
        Assert.Empty(on.Removed);

        // case14: 14 buses, one slack, so 13 angles + 14 magnitudes = 27 states.
        Assert.Equal(27, off.Observability.StateSize);
        int rowCount = tagged.Measurements.Count;
        Assert.Equal(rowCount - 27, off.DegreesOfFreedom);       // feature off: every row is redundancy
        Assert.Equal(rowCount - 3 - 27, on.DegreesOfFreedom);    // feature on: the three distrusted rows are not
        Assert.Equal(off.DegreesOfFreedom - 3, on.DegreesOfFreedom);

        // The threshold is the 95 % quantile at the smaller count, exactly.
        Assert.Equal(ChiSquare.Quantile(off.DegreesOfFreedom, 0.95), off.ChiSquareThreshold);
        Assert.Equal(ChiSquare.Quantile(off.DegreesOfFreedom - 3, 0.95), on.ChiSquareThreshold);
        Assert.Equal(ChiSquareOutcome.Passed, on.ChiSquareTest);
    }

    [Fact]
    public void B2_distrusted_rows_are_demoted_in_the_fit_and_left_out_of_J()
    {
        // Exact data, then a gross error of 1e5 sigma planted on each of the three distrusted rows. Demoted (sigma x 1e4),
        // each such row would add about (1e5 / 1e4)^2 = 100 to J if it were still counted; left out, J stays at the
        // trusted rows' own (near-zero) misfit and the chi-square test passes.
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet exact = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { NoiseScale = 0.0 });
        int[] suspects = exact.Measurements.Select((m, i) => (m, i)).Where(t => t.m.Kind == MeasurementKind.PFlowMw)
            .Take(3).Select(t => t.i).ToArray();
        var rows = exact.Measurements.ToList();
        foreach (int i in suspects) rows[i] = rows[i] with { Value = rows[i].Value + 1e5 * rows[i].Sigma, Source = SuspectTag };
        MeasurementSet tagged = exact with { Measurements = rows };

        // No bad-data removal, so the residual list is the set, row for row.
        EstimationResult on = new WlsStateEstimator().Estimate(model, tagged,
            new EstimationOptions { MaxBadDataRemovals = 0, DistrustedSources = new DistrustedSources(new[] { SuspectTag }) });

        Assert.Equal(EstimationStatus.Converged, on.Status);
        Assert.Equal(tagged.Measurements.Count, on.Residuals.Count);
        double jDistrusted = 0;
        for (int r = 0; r < tagged.Measurements.Count; r++)
        {
            Measurement given = tagged.Measurements[r];
            Measurement used = on.Residuals[r].Measurement;
            Assert.Equal(given.Value, used.Value);     // a value is never changed
            Assert.Equal(given.Source, used.Source);   // nor a source
            if (suspects.Contains(r))
            {
                Assert.Equal(given.Sigma * DistrustedSources.DefaultSigmaInflation, used.Sigma);   // demoted, exactly
                double z = on.Residuals[r].Residual / used.Sigma;
                jDistrusted += z * z;                  // what the row would have added to J had it counted
            }
            else
            {
                Assert.Same(given, used);              // a trusted row is the caller's own instance, untouched
            }
        }
        Assert.True(jDistrusted > 200.0, $"the planted errors should be worth about 300 in J if counted; got {jDistrusted}");
        Assert.True(on.ObjectiveJ < 1.0, $"J must hold no distrusted row: J = {on.ObjectiveJ}, the distrusted rows alone would add {jDistrusted}");
        Assert.Equal(ChiSquareOutcome.Passed, on.ChiSquareTest);
    }

    [Fact]
    public void B2_a_distrust_list_that_leaves_no_redundancy_is_refused_naming_the_sources_and_counts()
    {
        // Distrust every source the set carries: no row is left as evidence, so there is no redundancy for the
        // chi-square test and the degrees of freedom would be negative. That must be refused, never reported.
        (GridModel model, MeasurementSet set) = NoisyCase(IeeeCases.Case14(), 20260713);
        string[] everySource = set.Measurements.Select(r => r.Source).Distinct(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(everySource);
        var ex = Assert.Throws<InvalidDataException>(() => new WlsStateEstimator().Estimate(model, set,
            new EstimationOptions { DistrustedSources = new DistrustedSources(everySource) }));
        int rows = set.Measurements.Count;
        Assert.Contains($"demotes {rows} of {rows} rows and leaves 0 rows of evidence for 27 states", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no redundancy is left for the chi-square test", ex.Message, StringComparison.Ordinal);
        Assert.Contains("(the degrees of freedom would be -27)", ex.Message, StringComparison.Ordinal);
        foreach (string source in everySource)
            Assert.Contains(source, ex.Message, StringComparison.Ordinal);

        // A list that leaves the redundancy intact (a source no row carries) still runs, with positive degrees of
        // freedom (its numbers are pinned bit for bit by the first test in this class).
        EstimationResult ok = new WlsStateEstimator().Estimate(model, set,
            new EstimationOptions { DistrustedSources = new DistrustedSources(new[] { "no-such-feed" }) });
        Assert.True(ok.DegreesOfFreedom > 0);
    }

    [Fact]
    public void B2_HTI_refuses_distrusted_sources()
    {
        (GridModel model, MeasurementSet set) = NoisyCase(IeeeCases.Case14(), 20260713);
        var ex = Assert.Throws<NotSupportedException>(() => HtiBadDataEstimation.Estimate(new WlsStateEstimator(), model, set,
            new EstimationOptions { DistrustedSources = new DistrustedSources(new[] { "synthetic" }) }));
        Assert.Contains("--distrust", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void B2_a_row_with_no_source_tag_is_refused_when_a_source_is_distrusted()
    {
        (GridModel model, MeasurementSet set) = NoisyCase(IeeeCases.Case14(), 20260713);
        var rows = set.Measurements.ToList();
        rows[0] = rows[0] with { Source = null! };
        MeasurementSet untagged = set with { Measurements = rows };
        var ex = Assert.Throws<InvalidDataException>(() => new WlsStateEstimator().Estimate(model, untagged,
            new EstimationOptions { DistrustedSources = new DistrustedSources(new[] { SuspectTag }) }));
        Assert.Contains("measurement #0 has no source tag", ex.Message, StringComparison.Ordinal);
    }
}
