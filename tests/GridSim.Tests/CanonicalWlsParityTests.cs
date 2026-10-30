using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Ruling (2026-10-15): every plain WLS and robust estimate on the CPU goes through one canonical sparse step, so the
/// estimate is bit-identical whatever the dense / sparse threshold says. Before, a case at or below 200 buses used a
/// dense LU and a larger one the sparse LU, and the two differed in the last bits. Checked exactly (Assert.Equal on the
/// doubles), at the extreme thresholds, on a noisy set (so bad-data detection and the chi-square path run).
/// </summary>
public class CanonicalWlsParityTests
{
    public static TheoryData<string, EstimationMethod> Cases => new()
    {
        { "case14", EstimationMethod.Wls },
        { "case118", EstimationMethod.Wls },
        { "case118", EstimationMethod.Huber },
    };

    private static GridModel Load(string name) => name switch
    {
        "case14" => IeeeCases.Case14(),
        "case118" => IeeeCases.Case118(),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_estimate_is_bit_identical_across_the_dense_sparse_threshold(string name, EstimationMethod method)
    {
        GridModel model = Load(name);
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth);

        EstimationResult low = new WlsStateEstimator().Estimate(model, set, new EstimationOptions { Method = method, SparseBusThreshold = 0 });
        EstimationResult high = new WlsStateEstimator().Estimate(model, set, new EstimationOptions { Method = method, SparseBusThreshold = int.MaxValue });

        Assert.Equal(low.Status, high.Status);
        Assert.Equal(low.Iterations, high.Iterations);
        Assert.Equal(low.Vm, high.Vm);
        Assert.Equal(low.VaDeg, high.VaDeg);
        Assert.Equal(low.ObjectiveJ, high.ObjectiveJ);
        Assert.Equal(low.ChiSquareTest, high.ChiSquareTest);
        Assert.Equal(low.Removed.Count, high.Removed.Count);
        for (int i = 0; i < low.Residuals.Count; i++)
            Assert.Equal(low.Residuals[i].NormalizedResidual, high.Residuals[i].NormalizedResidual);
    }
}
