using System.Linq;
using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.IO;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// T0 (fail-loud gate) - the distributed model must collapse to the lumped model in its degenerate
/// limit. case9's generators form a single synchronous domain, so the distributed model delegates to
/// <see cref="FrequencyDynamics"/> and the result is identical to the lumped model, both with the
/// explicit reduction-limit config and by natural single-domain assignment.
/// </summary>
public class DistributedReductionTests
{
    private const double Demand = 315.0;
    private static readonly FrequencyEvent[] Loss = { new(1.0, -100.0) };

    private static void AssertIdentical(FrequencyResponse a, FrequencyResponse b)
    {
        Assert.Equal(a.InitialRocofHzPerS, b.InitialRocofHzPerS);
        Assert.Equal(a.NadirHz, b.NadirHz);
        Assert.Equal(a.NadirSeconds, b.NadirSeconds);
        Assert.Equal(a.SettleHz, b.SettleHz);
        Assert.Equal(a.Points.Count, b.Points.Count);
        Assert.True(a.Points.SequenceEqual(b.Points), "trajectory must be identical to the lumped model");
    }

    [Fact]
    public void Reduction_limit_config_is_identical_to_the_lumped_model()
    {
        var grid = IeeeCases.Case9();
        // Moved 2026-10 (B1 N275, Ruling R1: Mark delegated EE calls 2026-10-04; source: D2 notes O4, RoCoF = f0 * deltaP / 2E):
        // the distributed model is a model-based entry point and now refuses a config whose nominal is not the model's, so
        // both sides run case9 at its own 60 Hz (N017). Before: both at the config default 50 Hz. After: both at
        // grid.FrequencyHz. The assertion (the degenerate distributed model IS the lumped model, bit for bit) is unchanged.
        FrequencyResponse lumped = FrequencyDynamics.Simulate(grid.SystemInertiaGvaS * 1000.0, Demand, Loss,
            new DynamicsConfig { NominalHz = grid.FrequencyHz });

        var cfg = new DynamicsConfig
        {
            NominalHz = grid.FrequencyHz,
            InertiaModel = InertiaModel.Distributed,
            Distributed = DistributedConfig.ReductionLimit,
        };
        FrequencyResponse distributed = new DistributedInertiaModel().Simulate(grid, Demand, Loss, cfg);

        AssertIdentical(lumped, distributed);
    }

    [Fact]
    public void Single_domain_case_reduces_naturally_without_forcing()
    {
        var grid = IeeeCases.Case9();
        // Moved 2026-10 (B1 N275, Ruling R1: Mark delegated EE calls 2026-10-04; source: D2 notes O4, RoCoF = f0 * deltaP / 2E):
        // the distributed model is a model-based entry point and now refuses a config whose nominal is not the model's, so
        // both sides run case9 at its own 60 Hz (N017). Before: both at the config default 50 Hz. After: both at
        // grid.FrequencyHz. The assertion (the degenerate distributed model IS the lumped model, bit for bit) is unchanged.
        FrequencyResponse lumped = FrequencyDynamics.Simulate(grid.SystemInertiaGvaS * 1000.0, Demand, Loss,
            new DynamicsConfig { NominalHz = grid.FrequencyHz });

        var model = new DistributedInertiaModel();
        FrequencyResponse distributed = model.Simulate(
            grid, Demand, Loss, new DynamicsConfig { NominalHz = grid.FrequencyHz, InertiaModel = InertiaModel.Distributed });

        AssertIdentical(lumped, distributed);
        Assert.NotNull(model.LastDistributedResult);
        Assert.Single(model.LastDistributedResult!.Domains);
    }
}
