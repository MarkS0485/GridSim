using System.Numerics;
using GridSim.Core.Distribution;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-119: a multi-phase branch whose end is not a bus of the model is refused when the model is built, as a load or a
/// capacitor on an unknown bus already is (R235). The three-phase harmonic study used to take the unknown end as a
/// path to ground, damping every resonance at the real end without a word.
/// </summary>
public sealed class MultiPhaseBranchEndpointTests
{
    private static readonly PhaseImpedance Z = PhaseImpedance.Diagonal(new Complex(0.02, 0.06));

    private static PhaseBus[] Buses() => new[]
    {
        new PhaseBus(0, true, MultiPhaseModel.BalancedNominal(1.0)),
        new PhaseBus(1, false), new PhaseBus(3, false),
    };

    [Fact]
    public void A_branch_to_an_unknown_bus_is_refused_by_name()
    {
        var ex = Assert.Throws<System.ArgumentException>(() => new MultiPhaseModel(
            Buses(), new[] { new PhaseBranch(0, 1, Z), new PhaseBranch(1, 3, Z), new PhaseBranch(3, 99, Z) },
            System.Array.Empty<PhaseLoad>()));
        Assert.Contains("bus 99", ex.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_branch_from_an_unknown_bus_is_refused_by_name()
    {
        var ex = Assert.Throws<System.ArgumentException>(() => new MultiPhaseModel(
            Buses(), new[] { new PhaseBranch(0, 1, Z), new PhaseBranch(2, 3, Z) },
            System.Array.Empty<PhaseLoad>()));
        Assert.Contains("bus 2", ex.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_radial_feeder_with_known_ends_still_builds()
        => Assert.Equal(2, new MultiPhaseModel(Buses(), new[] { new PhaseBranch(0, 1, Z), new PhaseBranch(1, 3, Z) },
            System.Array.Empty<PhaseLoad>()).Branches.Count);
}
