using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

public class SyntheticMeasurementTests
{
    private static (GridModel Model, PowerFlowResult Truth) Solved(GridModel model)
    {
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(truth.Converged);
        return (model, truth);
    }

    [Fact]
    public void Sampling_is_deterministic_per_seed()
    {
        (GridModel model, PowerFlowResult truth) = Solved(IeeeCases.Case14());
        MeasurementSet a = SyntheticMeasurements.Sample(model, truth);
        MeasurementSet b = SyntheticMeasurements.Sample(model, truth);
        Assert.Equal(a.Measurements.Count, b.Measurements.Count);
        for (int i = 0; i < a.Measurements.Count; i++)
            Assert.Equal(a.Measurements[i], b.Measurements[i]); // bit-identical values

        MeasurementSet c = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { Seed = 7 });
        Assert.NotEqual(a.Measurements[0].Value, c.Measurements[0].Value);
    }

    [Fact]
    public void Zero_noise_sampling_reproduces_the_truth_exactly()
    {
        (GridModel model, PowerFlowResult truth) = Solved(IeeeCases.Case9());
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { NoiseScale = 0 });

        Measurement vm1 = set.Measurements.First(m => m.Kind == MeasurementKind.VmPu);
        Assert.Equal(truth.Vm[0], vm1.Value);

        EstimationResult est = new WlsStateEstimator().Estimate(model, set);
        Assert.Equal(EstimationStatus.Converged, est.Status);
        for (int i = 0; i < model.BusCount; i++)
            Assert.True(Math.Abs(est.Vm[i] - truth.Vm[i]) < 1e-10);
    }

    [Fact]
    public void Coverage_strides_meters_evenly()
    {
        (GridModel model, PowerFlowResult truth) = Solved(IeeeCases.Case14());
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions
        {
            VmCoverage = 0.5,
            InjCoverage = 0,
            FlowCoverage = 0,
        });
        Assert.Equal(model.BusCount / 2, set.Measurements.Count); // 7 of 14 buses
        Assert.All(set.Measurements, m => Assert.Equal(MeasurementKind.VmPu, m.Kind));
    }

    [Fact]
    public void The_set_validates_and_is_horizon_safe()
    {
        (GridModel model, PowerFlowResult truth) = Solved(IeeeCases.Case9());
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth);
        set.Validate(model);
        Assert.NotNull(set.TakenUtc);
        Assert.Contains("seed=20260713", set.Note, StringComparison.Ordinal);

        string path = Path.Combine(Path.GetTempPath(), $"gridsim-synth-{Guid.NewGuid():N}.json");
        try
        {
            set.Save(path);
            MeasurementSet loaded = MeasurementSet.Load(path); // horizon guard runs on load
            Assert.Equal(set.Measurements.Count, loaded.Measurements.Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_noisy_redundant_set_estimates_within_statistical_bounds()
    {
        (GridModel model, PowerFlowResult truth) = Solved(IeeeCases.Case14());
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth); // nominal noise, full coverage

        EstimationResult est = new WlsStateEstimator().Estimate(model, set);
        Assert.Equal(EstimationStatus.Converged, est.Status);
        Assert.True(est.ChiSquarePassed, $"J = {est.ObjectiveJ} vs threshold {est.ChiSquareThreshold}");
        Assert.Empty(est.Removed);
        for (int i = 0; i < model.BusCount; i++)
        {
            // Full redundancy squeezes the estimate well inside a single meter's sigma.
            Assert.True(Math.Abs(est.Vm[i] - truth.Vm[i]) < 0.01, $"Vm[{i}] off by {est.Vm[i] - truth.Vm[i]}");
            Assert.True(Math.Abs(est.VaDeg[i] - truth.VaDeg[i]) < 0.5, $"Va[{i}] off by {est.VaDeg[i] - truth.VaDeg[i]}");
        }

        EstimationResult again = new WlsStateEstimator().Estimate(model, set);
        for (int i = 0; i < model.BusCount; i++)
            Assert.Equal(est.Vm[i], again.Vm[i]); // estimation over a fixed set is bit-deterministic
    }
}
