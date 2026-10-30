using System.Linq;
using GridSim.Core.Abstractions;
using GridSim.Core.Dynamics;
using GridSim.Core.IO;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// M0 - the selectable inertia-model seam. Guards that introducing <see cref="IInertiaModel"/> /
/// <see cref="InertiaModelSelector"/> is a pure addition: the default is the lumped scalar model,
/// and routing through the seam is bit-for-bit identical to calling <see cref="FrequencyDynamics"/>
/// directly. (T0 - the distributed model collapsing to lumped - arrives with M1.)
/// </summary>
public class InertiaModelSeamTests
{
    private const double BaseDemandMw = 315.0; // case9 order-of-magnitude load
    private static readonly FrequencyEvent[] Loss = { new(1.0, -100.0) };

    [Fact]
    public void Default_dynamics_config_selects_the_lumped_scalar_model()
    {
        Assert.Equal(InertiaModel.LumpedScalar, new DynamicsConfig().InertiaModel);
    }

    [Fact]
    public void Selector_builds_the_lumped_model_with_a_label_and_no_spatial_detail()
    {
        (IInertiaModel model, InertiaModel resolved, string label) = InertiaModelSelector.Create(InertiaModel.LumpedScalar);

        Assert.IsType<LumpedScalarInertiaModel>(model);
        Assert.Equal(InertiaModel.LumpedScalar, resolved);
        Assert.False(string.IsNullOrWhiteSpace(label));
        Assert.Null(model.LastDistributedResult);
    }

    [Fact]
    public void Lumped_model_coi_inertia_equals_the_model_scalar()
    {
        var grid = IeeeCases.Case9();
        IInertiaModel model = new LumpedScalarInertiaModel();
        Assert.Equal(grid.SystemInertiaGvaS, model.CoiInertiaGvaS(grid));
    }

    [Fact]
    public void Routing_through_the_seam_is_bit_for_bit_identical_to_calling_FrequencyDynamics()
    {
        var grid = IeeeCases.Case9();
        IInertiaModel seam = new LumpedScalarInertiaModel();

        FrequencyResponse viaSeam = seam.Simulate(grid, BaseDemandMw, Loss);
        // Moved 2026-10 (B1 N275, Ruling R1: Mark delegated EE calls 2026-10-04; source: D2 notes O4, RoCoF = f0 * deltaP / 2E):
        // the seam is a model-based entry point, so it now runs case9 at case9's own 60 Hz (N017) instead of the config
        // default 50 Hz. Before: the direct call used the default config (50 Hz), the seam the same. After: the direct
        // call is given the model's nominal, which is what the seam now resolves with no config. The assertion (routing
        // through the seam is bit-for-bit the direct call) is unchanged.
        FrequencyResponse direct = FrequencyDynamics.Simulate(grid.SystemInertiaGvaS * 1000.0, BaseDemandMw, Loss,
            new DynamicsConfig { NominalHz = grid.FrequencyHz });

        Assert.Equal(direct.InitialRocofHzPerS, viaSeam.InitialRocofHzPerS);
        Assert.Equal(direct.NadirHz, viaSeam.NadirHz);
        Assert.Equal(direct.NadirSeconds, viaSeam.NadirSeconds);
        Assert.Equal(direct.SettleHz, viaSeam.SettleHz);
        Assert.Equal(direct.Points.Count, viaSeam.Points.Count);
        Assert.True(direct.Points.SequenceEqual(viaSeam.Points), "trajectory must be identical through the seam");
    }

    [Fact]
    public void Selector_builds_the_distributed_model()
    {
        (IInertiaModel model, InertiaModel resolved, string label) = InertiaModelSelector.Create(InertiaModel.Distributed);

        Assert.IsType<GridSim.Core.Dynamics.Distributed.DistributedInertiaModel>(model);
        Assert.Equal(InertiaModel.Distributed, resolved);
        Assert.False(string.IsNullOrWhiteSpace(label));
    }
}
