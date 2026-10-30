using System;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The state estimator is bit-reproducible: the same model, measurements and options give the same bits every time,
/// on each step backend. This is what makes an estimate usable as forensic evidence: a recovered or re-run estimate can
/// be compared with the recorded one bit for bit, and any difference is a real difference.
///
/// <para>Each backend is forced explicitly (<see cref="EstimationOptions.SparseBusThreshold"/>: int.MaxValue keeps
/// every case dense, 0 makes every case sparse), so both are covered on small and large cases. This test compares a
/// backend with itself; it does not claim the dense and sparse backends agree to the bit with each other.</para>
/// </summary>
public class EstimatorDeterminismTests
{
    private static readonly EstimationOptions Dense = new() { SparseBusThreshold = int.MaxValue };
    private static readonly EstimationOptions Sparse = new() { SparseBusThreshold = 0 };

    /// <summary>Synthetic noisy telemetry (fixed seed) so the fit is not trivially exact and bad-data logic runs.</summary>
    private static (GridModel Model, MeasurementSet Set) NoisyCase(GridModel model)
    {
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(truth.Converged);
        return (model, SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions()));
    }

    private static void AssertBitIdentical(EstimationResult a, EstimationResult b)
    {
        Assert.Equal(a.Status, b.Status);
        Assert.Equal(a.Iterations, b.Iterations);
        Assert.Equal(a.ChiSquareTest, b.ChiSquareTest);
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.ObjectiveJ), BitConverter.DoubleToInt64Bits(b.ObjectiveJ));
        Assert.Equal(a.BusIds, b.BusIds);
        for (int i = 0; i < a.Vm.Length; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.Vm[i]), BitConverter.DoubleToInt64Bits(b.Vm[i]));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.VaDeg[i]), BitConverter.DoubleToInt64Bits(b.VaDeg[i]));
        }
        Assert.Equal(a.Residuals.Count, b.Residuals.Count);
        for (int k = 0; k < a.Residuals.Count; k++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.Residuals[k].Predicted), BitConverter.DoubleToInt64Bits(b.Residuals[k].Predicted));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.Residuals[k].Residual), BitConverter.DoubleToInt64Bits(b.Residuals[k].Residual));
        }
        Assert.Equal(a.Removed.Count, b.Removed.Count);
    }

    [Theory]
    [InlineData("case14", false)]
    [InlineData("case14", true)]
    [InlineData("case118", false)]
    [InlineData("case118", true)]
    public void Two_runs_of_the_same_estimate_are_bit_identical(string caseName, bool sparse)
    {
        GridModel model = caseName == "case14" ? IeeeCases.Case14() : IeeeCases.Case118();
        (GridModel m, MeasurementSet set) = NoisyCase(model);
        EstimationOptions options = sparse ? Sparse : Dense;

        EstimationResult first = new WlsStateEstimator().Estimate(m, set, options);
        EstimationResult second = new WlsStateEstimator().Estimate(m, set, options);

        Assert.True(first.Converged, $"{caseName} ({(sparse ? "sparse" : "dense")}) did not converge: {first.Status}");
        AssertBitIdentical(first, second);
    }
}
