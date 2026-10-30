using System;
using System.Numerics;
using GridSim.Core.Distribution;
using Xunit;

namespace GridSim.Tests;

/// <summary>The 3x3 complex phase-impedance inverse (series admittance Y = Z^-1): Z * (Z^-1 v) = v to machine
/// precision, and a diagonal inverts elementwise.</summary>
public class PhaseImpedanceTests
{
    [Fact]
    public void Inverse_round_trips_a_full_matrix_to_machine_precision()
    {
        var m = new Complex[3, 3]
        {
            { new(0.10, 0.40), new(0.03, 0.10), new(0.02, 0.08) },
            { new(0.03, 0.10), new(0.11, 0.42), new(0.031, 0.11) },
            { new(0.02, 0.08), new(0.031, 0.11), new(0.09, 0.39) },
        };
        var z = new PhaseImpedance(m);
        PhaseImpedance zInv = z.Invert();

        var v = new[] { new Complex(1.0, 0.2), new Complex(-0.5, 0.9), new Complex(0.3, -0.7) };
        Complex[] round = z.Multiply(zInv.Multiply(v));
        for (int p = 0; p < 3; p++)
            Assert.True((round[p] - v[p]).Magnitude < 1e-12, $"component {p}: {round[p]} vs {v[p]}");
    }

    [Fact]
    public void Diagonal_inverts_elementwise()
    {
        var z = new Complex(0.02, 0.05);
        PhaseImpedance inv = PhaseImpedance.Diagonal(z).Invert();
        Complex expected = Complex.One / z;
        Assert.True((inv[0, 0] - expected).Magnitude < 1e-12);
        Assert.True(inv[0, 1].Magnitude < 1e-14); // off-diagonal stays zero
    }
}
