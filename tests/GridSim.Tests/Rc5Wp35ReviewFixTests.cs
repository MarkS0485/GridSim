using System.Numerics;
using GridSim.Core.Lines.Cables;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP35 review fixes (review R5): an independent low-frequency check of the positive-sequence resistance, and the
/// structure of ideal cross-bonding for cables with armour.
///
/// Sources: the closed-form DC resistance rho / (pi r^2) of the core, which no part of the solver computes; and an
/// independent 40-digit mpmath model of the same system (Schelkunoff surface impedances with mpmath's Bessel functions,
/// Ametani's loop sums, Wedepohl's form and the Pollaczek integral by mpmath quadrature, the cross-bonding average and the
/// Kron reduction), written from the published formulas, not from the C#. That model reproduces all twelve WP35 T7 pins
/// to their printed digits; the figures quoted below are from it.
/// </summary>
public sealed class Rc5Wp35ReviewFixTests
{
    private static SingleCoreCable Demo(string name, double x, bool armour)
    {
        var conductors = new List<CableConductor> { new(0.0, 0.0145, 1.72e-8), new(0.0315, 0.0325, 2.83e-8) };
        var insulation = new List<CableInsulation> { new(2.5, 0.0004), new(2.3) };
        if (armour)
        {
            conductors.Add(new CableConductor(0.0365, 0.0405, 1.8e-7, 10.0));
            insulation.Add(new CableInsulation(2.3));
        }
        return new SingleCoreCable(name, x, 1.0, conductors, insulation, armour ? 0.0425 : 0.0365);
    }

    private static CableGeometry Flat(bool armour, CableEarthMethod earth, CableBonding bonding, double f) =>
        new([Demo("A", -0.1, armour), Demo("B", 0.0, armour), Demo("C", 0.1, armour)], f, 100.0, earth, bonding, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Single_point_R1_tends_to_the_core_DC_resistance_as_the_frequency_falls(bool armour)
    {
        // With single-point bonding the phase impedance is the core block: the core's surface impedance, the insulation
        // (purely reactive), the sheath (and armour) loop terms z_in + z_out - 2 z_mutual, whose real parts cancel at DC,
        // and the earth return. In Wedepohl's form the self and mutual earth terms differ only by j w mu0 / (2 pi) ln(d / r)
        // (equal depths), which is purely imaginary, so Re Z1 holds no earth term. Hence Re Z1 -> rho / (pi r^2) as f -> 0,
        // with an O(f^2) departure: the core's (m r)^4 / 192 term plus the tubes' (mpmath model: 4.855e-5 at 1 Hz without
        // armour, 7.787e-5 with it).
        double rdc = 1.72e-8 / (Math.PI * 0.0145 * 0.0145) * 1000.0;   // ohm/km
        double[] f = [1.0, 0.1, 0.01];
        var r1 = new double[3];
        for (int s = 0; s < 3; s++)
        {
            CableConstantsResult res = CableConstantsSolver.Solve(Flat(armour, CableEarthMethod.Wedepohl, CableBonding.SinglePoint, f[s]));
            Assert.Empty(res.Warnings);
            r1[s] = res.Line.Sequence!.Z1.Real;
            Assert.True(r1[s] > rdc, $"R1 {r1[s]:R} at {f[s]} Hz is not above the DC value {rdc:R}");
        }

        // Every step: the departure falls a hundredfold per decade. The next order moves that ratio by 4e-5 at most
        // (mpmath: 99.99616 and 99.99996 without armour, 99.99759 and 99.99998 with it); rounding in R1 is under 2e-13 of
        // R_dc (each surface impedance within 1e-14 relative, measured against mpmath in the WP35 review; a float port of
        // the same surface impedances was within 5.3e-15 of R_dc at these frequencies), which moves the second ratio by
        // under 0.005. So [99.9, 100.1] holds with margin, and a nonzero DC limit other than R_dc would
        // pull the ratio towards 1.
        for (int s = 0; s < 2; s++)
            Assert.InRange((r1[s] / rdc - 1.0) / (r1[s + 1] / rdc - 1.0), 99.9, 100.1);

        // The limit itself, by Richardson extrapolation of the f^2 term from 0.1 and 0.01 Hz: the mpmath model leaves
        // 1.9e-15 relative; with the rounding above the estimate is within 1e-12 of R_dc. Asserted at 1e-11.
        double limit = r1[2] - (r1[1] - r1[2]) / 99.0;
        Assert.True(Math.Abs(limit / rdc - 1.0) < 1e-11, $"extrapolated R1 {limit:R}, R_dc {rdc:R}");
    }

    [Theory]
    [InlineData(CableEarthMethod.Wedepohl)]
    [InlineData(CableEarthMethod.Pollaczek)]
    public void Ideal_cross_bonding_with_armour_differs_from_single_point_by_a_rank_two_term(CableEarthMethod earth)
    {
        // Derivation: after the rotation average, each core couples equally to the three sheaths (a_i) and equally to the
        // three armours (b_i), so the core-to-metal block is [a 1^T, b 1^T], of rank two, and so is the Kron correction
        // Z_cm Z_mm^-1 Z_mc. Single-point bonding is the core block itself, so Z_cross - Z_single = -(that correction) has
        // rank at most two: det = 0. The structure holds for any symmetric primitive matrix, so the Bessel and quadrature
        // errors cannot break it; only the rounding of the average, the Kron solve and the subtraction can, and the cofactors
        // of a rank-two matrix are its 2x2 minors (at most 1.6e-6 max^2, mpmath), which makes that contribution negligible
        // (about 1e-18 max^3 with cond(Z_mm) = 22). Evaluating the determinant rounds each of its three products by a few
        // ulp of max^3: under 6e-15 max^3 in all. A float port measured 1e-17 (Wedepohl) and 1.5e-16 (Pollaczek). 1e-14
        // is asserted. A wrong average (sheaths rotated, armour left in place) gives 0.016 max^3, and both-ends bonding 0.074.
        CableConstantsResult cross = CableConstantsSolver.Solve(Flat(true, earth, CableBonding.CrossBonded, 50.0));
        CableConstantsResult single = CableConstantsSolver.Solve(Flat(true, earth, CableBonding.SinglePoint, 50.0));
        var d = new Complex[3, 3];
        double max = 0.0;
        for (int i = 0; i < 3; i++)
            for (int k = 0; k < 3; k++)
            {
                d[i, k] = cross.Line.PhaseZ[i, k] - single.Line.PhaseZ[i, k];
                max = Math.Max(max, Complex.Abs(d[i, k]));
            }
        // mpmath: max |d| = 0.62482 ohm/km.
        Assert.True(max > 0.1, "the cross-bonding correction is not trivially zero");
        Complex det = d[0, 0] * (d[1, 1] * d[2, 2] - d[1, 2] * d[2, 1])
                    - d[0, 1] * (d[1, 0] * d[2, 2] - d[1, 2] * d[2, 0])
                    + d[0, 2] * (d[1, 0] * d[2, 1] - d[1, 1] * d[2, 0]);
        Assert.True(Complex.Abs(det) <= 1e-14 * max * max * max, $"det {Complex.Abs(det)} vs max^3 {max * max * max}");

        // The rank is two, not one: with armour the rank-one property of core-and-sheath cables (WP35 T6b) does not hold.
        // mpmath: the largest 2x2 minor is 1.6055e-6 max^2; its rounding is a few ulp of max^2.
        double largestMinor = 0.0;
        for (int i = 0; i < 3; i++)
            for (int j = i + 1; j < 3; j++)
                for (int k = 0; k < 3; k++)
                    for (int l = k + 1; l < 3; l++)
                        largestMinor = Math.Max(largestMinor, Complex.Abs(d[i, k] * d[j, l] - d[i, l] * d[j, k]));
        Assert.True(largestMinor > 1e-7 * max * max, $"largest minor {largestMinor} vs max^2 {max * max}");
    }
}
