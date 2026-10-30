using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Pins the <see cref="SparseWlsStep"/> structure-once / refill-per-iteration claim (PerformanceOpts.md
/// §4.1): the gain sparsity pattern, ordering and slot map are built once in <c>Begin</c> and only values
/// are refilled each iteration. That is safe ONLY while the measurement structure is genuinely fixed across
/// a pass and re-derived when the measurement SET changes. This test changes the measurement set between
/// runs and asserts the sparse backend re-derives correctly (recovers truth both times); the DEBUG-only
/// structural assertion added inside <c>FillGain</c> fires if a refill ever touches a column outside the
/// pattern <c>Begin</c> saw.
/// </summary>
public class SparseWlsStructureReusePinTests
{
    private static readonly EstimationOptions Sparse = new() { SparseBusThreshold = 0 };

    [Fact]
    public void Sparse_backend_re_derives_structure_when_the_measurement_set_changes()
    {
        GridModel model = IeeeCases.Case118();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet full = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { NoiseScale = 0 });

        // Pass 1: the full set. Recovers truth exactly (each Estimate calls Begin, building the pattern).
        EstimationResult first = new WlsStateEstimator().Estimate(model, full, Sparse);
        Assert.Equal(EstimationStatus.Converged, first.Status);
        AssertRecoversTruth(model, truth, first);

        // Mutate the STRUCTURE: drop a redundant slice of flow measurements so the gain pattern (and the
        // per-row structural columns) genuinely differ. The set stays observable (heavy redundancy).
        var trimmed = full.Measurements
            .Where((m, i) => !(m.Kind == MeasurementKind.PFlowMw && i % 7 == 0))
            .ToList();
        Assert.True(trimmed.Count < full.Measurements.Count, "expected the trim to remove some measurements");
        MeasurementSet changed = full with { Measurements = trimmed };

        // Pass 2: a fresh estimator re-derives the (different) structure and still recovers truth.
        EstimationResult second = new WlsStateEstimator().Estimate(model, changed, Sparse);
        Assert.Equal(EstimationStatus.Converged, second.Status);
        AssertRecoversTruth(model, truth, second);
    }

    private static void AssertRecoversTruth(GridModel model, PowerFlowResult truth, EstimationResult est)
    {
        for (int i = 0; i < model.BusCount; i++)
        {
            Assert.True(System.Math.Abs(est.Vm[i] - truth.Vm[i]) < 1e-8, $"Vm[{i}] off by {est.Vm[i] - truth.Vm[i]}");
            Assert.True(System.Math.Abs(est.VaDeg[i] - truth.VaDeg[i]) < 1e-7);
        }
    }
}
