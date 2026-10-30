using System.Numerics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The compensating control for the AdmittanceStamp migration: with all seven
/// former inline copies now routed through <see cref="AdmittanceStamp"/> - including the physics audit
/// (PhysicsLaws) and the reciprocity check - the audit no longer holds an INDEPENDENT copy of the stamp
/// arithmetic. This test restores that independence at the test layer: it re-derives the pi-model 2x2 from
/// a second, textbook expression and asserts <see cref="AdmittanceStamp"/> matches it BIT-FOR-BIT across
/// taps, phase shifts and line charging. A defect introduced into the centralised stamp would fail here.
/// </summary>
public class AdmittanceStampPinTests
{
    public static TheoryData<double, double, double, double, double> Params => new()
    {
        // r,      x,     b,      tapRatio, phaseShiftDeg
        { 0.01,   0.10,  0.02,   1.0,      0.0 },   // plain line
        { 0.0,    0.08,  0.0,    1.0,      0.0 },   // lossless reactance (Gij == 0)
        { 0.02,   0.06,  0.05,   0.975,    0.0 },   // off-nominal tap
        { 0.015,  0.12,  0.03,   1.05,     30.0 },  // phase-shifting transformer
        { 0.008,  0.09,  0.04,   0.0,      -15.0 }, // tapRatio <= 0 => treated as 1
    };

    [Theory]
    [MemberData(nameof(Params))]
    public void Branch_stamp_matches_an_independent_textbook_derivation_bit_for_bit(
        double r, double x, double b, double tapRatio, double phaseShiftDeg)
    {
        BranchStamp s = AdmittanceStamp.Branch(r, x, b, tapRatio, phaseShiftDeg);

        // Independent reference: the standard pi-model with tap on the FROM side (Zollenkopf / MATPOWER
        // form). Deliberately written out separately from the production code under test.
        Complex ysRef = 1.0 / new Complex(r, x);
        Complex bcRef = new Complex(0.0, b / 2.0);
        double aRef = tapRatio <= 0 ? 1.0 : tapRatio;
        Complex tapRef = Complex.FromPolarCoordinates(aRef, phaseShiftDeg * System.Math.PI / 180.0);
        Complex yffRef = (ysRef + bcRef) / (aRef * aRef);
        Complex yttRef = ysRef + bcRef;
        Complex yftRef = -ysRef / Complex.Conjugate(tapRef);
        Complex ytfRef = -ysRef / tapRef;

        AssertBitEqual(yffRef, s.Yff);
        AssertBitEqual(yttRef, s.Ytt);
        AssertBitEqual(yftRef, s.Yft);
        AssertBitEqual(ytfRef, s.Ytf);
    }

    private static void AssertBitEqual(Complex expected, Complex actual)
    {
        Assert.Equal(System.BitConverter.DoubleToInt64Bits(expected.Real), System.BitConverter.DoubleToInt64Bits(actual.Real));
        Assert.Equal(System.BitConverter.DoubleToInt64Bits(expected.Imaginary), System.BitConverter.DoubleToInt64Bits(actual.Imaginary));
    }
}
