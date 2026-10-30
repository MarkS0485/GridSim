using System;
using System.Collections.Generic;
using System.Numerics;
using GridSim.Core.Distribution;
using GridSim.Core.Harmonics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Three-phase / unbalanced harmonic penetration. The sequence behaviour is the physics fingerprint: a
/// balanced triplen is pure zero-sequence, a balanced 5th is pure negative-sequence, and an unbalanced
/// injection produces all three sequences - exactly what the positive-sequence engine cannot see.
/// </summary>
public class ThreePhaseHarmonicsTests
{
    // A two-bus feeder: source (bus 0) + one load bus (bus 1), a balanced diagonal line (decoupled phases).
    private static MultiPhaseModel Feeder()
    {
        var z = PhaseImpedance.Diagonal(new Complex(0.01, 0.1));
        return new MultiPhaseModel(
            new[] { new PhaseBus(0, true, MultiPhaseModel.BalancedNominal(1.0)), new PhaseBus(1, false) },
            new[] { new PhaseBranch(0, 1, z) },
            Array.Empty<PhaseLoad>());
    }

    private static double Mag(Complex c) => c.Magnitude;

    [Fact]
    public void A_balanced_triplen_is_pure_zero_sequence()
    {
        MultiPhaseModel model = Feeder();
        var src = ThreePhaseHarmonicSource.Balanced(1, "test", 1.0, new[] { new HarmonicComponent(3, 100.0) });
        ThreePhasePenetrationResult res = ThreePhaseHarmonicPenetration.Solve(model, new[] { src }, new[] { 3 });

        Complex[] seq = res.Sequence(0, 0); // [zero, positive, negative] at bus 1, order 3
        Assert.True(Mag(seq[0]) > 1e-6, "zero-sequence present");
        Assert.True(Mag(seq[1]) < 1e-9 * Math.Max(1.0, Mag(seq[0])), $"positive seq should vanish: {seq[1]}");
        Assert.True(Mag(seq[2]) < 1e-9 * Math.Max(1.0, Mag(seq[0])), $"negative seq should vanish: {seq[2]}");
    }

    [Fact]
    public void A_balanced_fifth_is_pure_negative_sequence()
    {
        MultiPhaseModel model = Feeder();
        var src = ThreePhaseHarmonicSource.Balanced(1, "test", 1.0, new[] { new HarmonicComponent(5, 100.0) });
        ThreePhasePenetrationResult res = ThreePhaseHarmonicPenetration.Solve(model, new[] { src }, new[] { 5 });

        Complex[] seq = res.Sequence(0, 0);
        Assert.True(Mag(seq[2]) > 1e-6, "negative-sequence present");
        Assert.True(Mag(seq[0]) < 1e-9 * Math.Max(1.0, Mag(seq[2])), $"zero seq should vanish: {seq[0]}");
        Assert.True(Mag(seq[1]) < 1e-9 * Math.Max(1.0, Mag(seq[2])), $"positive seq should vanish: {seq[1]}");
    }

    [Fact]
    public void An_unbalanced_injection_produces_all_three_sequences()
    {
        MultiPhaseModel model = Feeder();
        // A single-phase (phase A only) 5th-harmonic source - the classic unbalanced case.
        var a = Complex.FromPolarCoordinates(0.5, 0.0);
        var byOrder = new Dictionary<int, Complex[]> { [5] = new[] { a, Complex.Zero, Complex.Zero } };
        var src = new ThreePhaseHarmonicSource(1, "single-phase", byOrder);

        ThreePhasePenetrationResult res = ThreePhaseHarmonicPenetration.Solve(model, new[] { src }, new[] { 5 });
        Complex[] seq = res.Sequence(0, 0);

        Assert.True(Mag(seq[0]) > 1e-4, "zero-sequence present");
        Assert.True(Mag(seq[1]) > 1e-4, "positive-sequence present");
        Assert.True(Mag(seq[2]) > 1e-4, "negative-sequence present");
    }

    [Fact]
    public void A_balanced_diagonal_feeder_keeps_the_three_phase_magnitudes_equal()
    {
        // Non-regression bridge: with a decoupled (diagonal) line and a balanced injection, the three phase
        // voltages have equal magnitude - the balanced case reduces to three identical single-phase solves.
        MultiPhaseModel model = Feeder();
        var src = ThreePhaseHarmonicSource.Balanced(1, "test", 1.0, new[] { new HarmonicComponent(7, 100.0) });
        ThreePhasePenetrationResult res = ThreePhaseHarmonicPenetration.Solve(model, new[] { src }, new[] { 7 });

        Complex[] v = res.Voltage[0][0]; // per-phase voltage at bus 1
        Assert.Equal(Mag(v[0]), Mag(v[1]), 9);
        Assert.Equal(Mag(v[1]), Mag(v[2]), 9);
        Assert.True(Mag(v[0]) > 1e-6, "a non-trivial response");
    }
}
