using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Follow-up to gap B2's no-redundancy refusal: without any distrusted source, a set whose prior-solved rows (the N078
/// rule: a power flow of the set's own injection rows, not independent evidence) are needed to reach the state count
/// reported degrees of freedom of zero or less. It is now refused before any estimate, naming the counts, as the
/// distrust case is. A set that keeps redundancy after the prior-solved rows are left out, and a set with no
/// prior-solved row, run exactly as before.
/// </summary>
public class PriorSolvedNoRedundancyRefusedTests
{
    private static (GridModel Model, MeasurementSet Set) Case14()
    {
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        return (model, SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { NoiseScale = 0 }));
    }

    [Fact]
    public void A_set_left_with_no_redundancy_by_its_prior_solved_rows_is_refused_by_count()
    {
        (GridModel model, MeasurementSet set) = Case14();
        const int keep = 20;   // case14 has 27 states (14 magnitudes and 13 angles), so 20 evidence rows leave none spare
        Measurement[] rows = set.Measurements
            .Select((x, i) => i < keep ? x : x with { Source = MeasuredInjectionPrior.SolvedPriorSource })
            .ToArray();
        int m = rows.Length;
        Assert.True(m > 27, "the fixture must be observable in row count, so only the N078 rule removes the redundancy");

        var ex = Assert.Throws<InvalidDataException>(() =>
            new WlsStateEstimator().Estimate(model, set with { Measurements = rows }));
        Assert.Contains($"{m - keep} of {m} rows are prior-solved rows", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"remaining {keep} rows of evidence for 27 states", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"would be {keep - 27}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_set_that_keeps_redundancy_beyond_its_prior_solved_rows_is_estimated()
    {
        (GridModel model, MeasurementSet set) = Case14();
        Measurement[] rows = set.Measurements
            .Select(x => x.Kind == MeasurementKind.VmPu ? x with { Source = MeasuredInjectionPrior.SolvedPriorSource } : x)
            .ToArray();
        EstimationResult est = new WlsStateEstimator().Estimate(model, set with { Measurements = rows });
        Assert.Equal(EstimationStatus.Converged, est.Status);
        Assert.True(est.DegreesOfFreedom > 0);
    }

    [Fact]
    public void A_set_with_no_prior_solved_row_is_not_touched_by_the_guard()
    {
        // Twenty plain rows for 27 states: no prior-solved row and no distrust, so the guard is skipped and the
        // estimator reports the set as before (unobservable), rather than refusing it.
        (GridModel model, MeasurementSet set) = Case14();
        EstimationResult est = new WlsStateEstimator().Estimate(model, set with { Measurements = set.Measurements.Take(20).ToArray() });
        Assert.Equal(EstimationStatus.Unobservable, est.Status);
    }
}
