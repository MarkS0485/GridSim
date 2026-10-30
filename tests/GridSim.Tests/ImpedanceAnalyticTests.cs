using System;
using System.Numerics;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Impedance;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Machine-epsilon analytic checks of the converter admittance and the canonical grid impedance, plus an
/// INDEPENDENT hand-computed SRF-PLL Y_dq oracle (a separate explicit real/imaginary arithmetic path, not
/// GridSim's own <see cref="Complex"/> assembly) - the decisive-ish anchor the critique demanded, alongside
/// the Routh oracle in <see cref="ImpedanceModalAgreementTests"/>.
/// </summary>
public class ImpedanceAnalyticTests
{
    private const double Vd0 = 1.02, Id0 = 0.80, Iq0 = 0.20;
    private const double FbwHz = 10.0, Zeta = 0.707;

    private static GridFollowingImpedance Gfl(ImpedanceModelDetail detail = ImpedanceModelDetail.PllOuter, double wci = 0.0)
    {
        var op = new ConverterOperatingPoint(Vd0, 0.0, Id0, Iq0, Vd0 * Id0, -Vd0 * Iq0);
        return GridFollowingImpedance.FromParameters(7, op, detail, ProvenanceTag.CuratedPlaceholder, FbwHz, Zeta, wci);
    }

    private static readonly double Wci = 2.0 * Math.PI * GridFollowingImpedance.IdealCurrentLoopHz;

    // Independent SRF-PLL closed loop, explicit real/imaginary arithmetic (NOT GridSim's Complex path).
    private static (double re, double im) GpllIndependent(double omega)
    {
        double wn = 2.0 * Math.PI * FbwHz;
        double kp = 2.0 * Zeta * wn / Vd0;
        double ki = wn * wn / Vd0;
        double numRe = ki, numIm = kp * omega;
        double denRe = (-omega * omega) + (Vd0 * ki), denIm = Vd0 * kp * omega;
        double d = (denRe * denRe) + (denIm * denIm);
        return (((numRe * denRe) + (numIm * denIm)) / d, ((numIm * denRe) - (numRe * denIm)) / d);
    }

    // Independent current-loop roll-off G_ci(jw) = wci/(jw+wci), explicit real/imaginary.
    private static (double re, double im) GciIndependent(double omega)
    {
        double d = (Wci * Wci) + (omega * omega);
        return ((Wci * Wci) / d, (-Wci * omega) / d);
    }

    private static Complex Gci(double omega) { (double re, double im) = GciIndependent(omega); return new Complex(re, im); }

    [Fact]
    public void Pll_transfer_matches_independent_arithmetic_to_machine_epsilon()
    {
        GridFollowingImpedance g = Gfl();
        foreach (double f in new[] { 0.1, 1.0, 5.0, 10.0, 25.0, 100.0 })
        {
            double w = 2.0 * Math.PI * f;
            Complex mine = g.PllTransfer(w);
            (double re, double im) = GpllIndependent(w);
            Assert.Equal(re, mine.Real, 12);
            Assert.Equal(im, mine.Imaginary, 12);
        }
    }

    [Fact]
    public void Dq_admittance_matches_independent_hand_computed_oracle()
    {
        GridFollowingImpedance g = Gfl();
        foreach (double f in new[] { 2.0, 8.0, 15.0, 40.0 })
        {
            double w = 2.0 * Math.PI * f;
            (double gRe, double gIm) = GpllIndependent(w);
            Complex gpll = new(gRe, gIm);
            Complex gci = Gci(w);
            // Hand-assembled full 2x2: [[G_ci*Id0/Vd0, Iq0*Gpll],[G_ci*Iq0/Vd0, -Id0*Gpll]].
            Complex2x2 y = g.AdmittanceDq(w);
            Complex ydd = gci * (Id0 / Vd0), yqd = gci * (Iq0 / Vd0);
            Assert.Equal(ydd.Real, y.M11.Real, 12);
            Assert.Equal(ydd.Imaginary, y.M11.Imaginary, 12);
            Assert.Equal((Iq0 * gpll).Real, y.M12.Real, 12);
            Assert.Equal((Iq0 * gpll).Imaginary, y.M12.Imaginary, 12);
            Assert.Equal(yqd.Real, y.M21.Real, 12);
            Assert.Equal(yqd.Imaginary, y.M21.Imaginary, 12);
            Assert.Equal((-Id0 * gpll).Real, y.M22.Real, 12);
            Assert.Equal((-Id0 * gpll).Imaginary, y.M22.Imaginary, 12);
        }
    }

    [Fact]
    public void Positive_sequence_equals_modified_sequence_transform_closed_form()
    {
        GridFollowingImpedance g = Gfl();
        foreach (double f in new[] { 1.0, 12.0, 30.0 })
        {
            double w = 2.0 * Math.PI * f;
            Complex gpll = g.PllTransfer(w);
            // Y_pp = 1/2 (G_ci/Vd0 - G_pll)(Id0 + j Iq0).
            Complex expected = 0.5 * ((Gci(w) / Vd0) - gpll) * new Complex(Id0, Iq0);
            Complex mine = g.PositiveSequenceAdmittance(w);
            Assert.Equal(expected.Real, mine.Real, 12);
            Assert.Equal(expected.Imaginary, mine.Imaginary, 12);
        }
    }

    [Fact]
    public void Canonical_thevenin_impedance_is_R_plus_jX_scaled_by_frequency()
    {
        const double scr = 2.5, xr = 8.0, pRated = 1.0, f0 = 50.0;
        (double r, double x) = GridImpedanceAssembler.TheveninRx(scr, xr, pRated);
        // |Z| = 1/(SCR*P) and X/R = xr.
        Assert.Equal(1.0 / (scr * pRated), Math.Sqrt((r * r) + (x * x)), 12);
        Assert.Equal(xr, x / r, 12);
        foreach (double f in new[] { 1.0, 20.0, 50.0, 120.0 })
        {
            Complex z = GridImpedanceAssembler.CanonicalTheveninImpedance(scr, xr, pRated, f, f0);
            Assert.Equal(r, z.Real, 12);
            Assert.Equal(x * (f / f0), z.Imaginary, 12);
        }
    }

    [Fact]
    public void Single_gfl_vs_thevenin_return_ratio_matches_closed_form()
    {
        const double scr = 2.0, xr = 10.0, pRated = 1.0, f0 = 50.0;
        GridFollowingImpedance g = Gfl();
        foreach (double f in new[] { 0.5, 5.0, 18.0, 45.0, 90.0 })
        {
            double w = 2.0 * Math.PI * f;
            Complex z = GridImpedanceAssembler.CanonicalTheveninImpedance(scr, xr, pRated, f, f0);
            Complex y = g.PositiveSequenceAdmittance(w);
            Complex lMine = z * y;
            // Independent closed form.
            (double gRe, double gIm) = GpllIndependent(w);
            Complex gpll = new(gRe, gIm);
            Complex yInd = 0.5 * ((Gci(w) / Vd0) - gpll) * new Complex(Id0, Iq0);
            (double rr, double xx) = GridImpedanceAssembler.TheveninRx(scr, xr, pRated);
            Complex zInd = new(rr, xx * (f / f0));
            Complex lInd = zInd * yInd;
            Assert.Equal(lInd.Real, lMine.Real, 12);
            Assert.Equal(lInd.Imaginary, lMine.Imaginary, 12);
        }
    }
}
