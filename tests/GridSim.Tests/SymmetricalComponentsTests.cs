using System;
using System.Numerics;
using GridSim.Core.Distribution;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The Fortescue symmetrical-component transform: abc-&gt;012-&gt;abc round-trips to machine precision, a
/// balanced set has only a positive sequence, and the positive-sequence helper agrees with the transform.
/// </summary>
public class SymmetricalComponentsTests
{
    [Fact]
    public void Phase_sequence_round_trip_is_machine_precision()
    {
        var abc = new[] { new Complex(1.02, 0.05), new Complex(-0.5, -0.88), new Complex(-0.48, 0.9) };
        Complex[] back = SymmetricalComponents.ToPhase(SymmetricalComponents.ToSequence(abc));
        for (int p = 0; p < 3; p++)
            Assert.True((back[p] - abc[p]).Magnitude < 1e-12, $"phase {p}: {back[p]} vs {abc[p]}");
    }

    [Fact]
    public void A_balanced_set_has_only_a_positive_sequence()
    {
        Complex[] abc = MultiPhaseModel.BalancedNominal(1.0); // 1<0, 1<-120, 1<+120
        Complex[] seq = SymmetricalComponents.ToSequence(abc);
        Assert.True(seq[0].Magnitude < 1e-12, $"zero seq {seq[0]}");
        Assert.True(seq[2].Magnitude < 1e-12, $"negative seq {seq[2]}");
        Assert.True(Math.Abs(seq[1].Magnitude - 1.0) < 1e-12, $"positive seq {seq[1]}");
    }

    [Fact]
    public void Positive_sequence_helper_agrees_with_the_transform()
    {
        var abc = new[] { new Complex(0.9, 0.1), new Complex(-0.4, -0.8), new Complex(-0.45, 0.7) };
        Complex viaHelper = SymmetricalComponents.PositiveSequence(abc);
        Complex viaTransform = SymmetricalComponents.ToSequence(abc)[1];
        Assert.True((viaHelper - viaTransform).Magnitude < 1e-14);
    }
}
