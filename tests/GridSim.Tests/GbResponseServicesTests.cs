using System.Linq;
using GridSim.Core.Dynamics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The data-grounded GB arrest layer (DC/DM/DR at real NESO EAC held volumes) arrests the fall: adding it
/// raises the nadir versus no response services, and carries the real procured MW.
/// </summary>
public class GbResponseServicesTests
{
    private static readonly FrequencyEvent[] Loss = { new(1.0, -1000.0) };

    [Fact]
    public void Grounded_volumes_match_the_eac_clears()
    {
        var svc = GbResponseServices.Typical();
        Assert.Equal(3, svc.Count);
        Assert.Equal(950.0, svc[0].Mw);   // DC
        Assert.Equal(520.0, svc[1].Mw);   // DM
        Assert.Equal(490.0, svc[2].Mw);   // DR
        Assert.Equal(0.5, svc[0].FullDeliveryDeviationHz);   // DC full at 0.5 Hz
        Assert.Equal(0.2, svc[1].FullDeliveryDeviationHz);   // DM full at 0.2 Hz
    }

    [Fact]
    public void The_arrest_layer_raises_the_nadir()
    {
        const double e = 150_000.0, demand = 30_000.0;   // ~150 GVA.s typical GB inertia
        FrequencyResponse without = FrequencyDynamics.Simulate(e, demand, Loss, new DynamicsConfig());
        FrequencyResponse with = FrequencyDynamics.Simulate(e, demand, Loss,
            new DynamicsConfig { SystemResponseServices = GbResponseServices.Typical() });

        Assert.True(with.NadirHz > without.NadirHz,
            $"the DC/DM/DR arrest layer should lift the nadir: {without.NadirHz} -> {with.NadirHz}");
        // Initial RoCoF is unchanged (response services don't affect the instantaneous fall).
        Assert.Equal(without.InitialRocofHzPerS, with.InitialRocofHzPerS, 6);
    }

    [Fact]
    public void Scale_reduces_the_held_volume()
    {
        var half = GbResponseServices.Typical(scale: 0.5);
        Assert.Equal(475.0, half[0].Mw);   // DC halved
    }
}
