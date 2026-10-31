using System;
using System.Collections.Generic;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Impedance;
using GridSim.Core.Dynamics.SmallSignal;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The DECISIVE stability check (critique FIX 6): a Routh-Hurwitz right-half-plane root count on the
/// closed-loop characteristic polynomial of the canonical inverter-vs-Thevenin dq loop - an oracle that is
/// INDEPENDENT of the Nyquist winding path - must equal the generalised-Nyquist <c>UnstableModeCount</c>, on
/// both a stable and an unstable SCR. Workstream C's modal RHP count corroborates where the model structures
/// align (its inverter coupling is a COI archetype, so it is corroboration, not the primary oracle).
/// </summary>
public class ImpedanceModalAgreementTests
{
    // --- Minimal real-coefficient polynomial (ascending coefficients) ---
    private static double[] Mul(double[] a, double[] b)
    {
        var r = new double[a.Length + b.Length - 1];
        for (int i = 0; i < a.Length; i++)
            for (int j = 0; j < b.Length; j++)
                r[i + j] += a[i] * b[j];
        return r;
    }

    private static double[] Add(double[] a, double[] b)
    {
        var r = new double[Math.Max(a.Length, b.Length)];
        for (int i = 0; i < a.Length; i++) r[i] += a[i];
        for (int i = 0; i < b.Length; i++) r[i] += b[i];
        return r;
    }

    private static double[] Scale(double[] a, double s)
    {
        var r = new double[a.Length];
        for (int i = 0; i < a.Length; i++) r[i] = a[i] * s;
        return r;
    }

    // Routh-Hurwitz RHP-root count of a real polynomial given ascending coefficients.
    internal static int RouthRhpCount(double[] ascending)
    {
        // To descending, trim leading zeros.
        var c = new List<double>();
        for (int i = ascending.Length - 1; i >= 0; i--) c.Add(ascending[i]);
        while (c.Count > 1 && Math.Abs(c[0]) < 1e-12) c.RemoveAt(0);
        int n = c.Count - 1;
        if (n <= 0) return 0;

        var rows = new double[n + 1][];
        int cols = (n / 2) + 1;
        for (int i = 0; i <= n; i++) rows[i] = new double[cols + 1];
        for (int i = 0; i <= n; i++)
        {
            int idx = i / 2;
            if (i % 2 == 0) rows[0][idx] = c[i];
            else rows[1][idx] = c[i];
        }

        for (int i = 2; i <= n; i++)
        {
            double pivot = rows[i - 1][0];
            if (Math.Abs(pivot) < 1e-12) pivot = 1e-12;   // epsilon method for a zero in the first column
            for (int j = 0; j + 1 < cols + 1; j++)
            {
                double a = rows[i - 2][0], b = rows[i - 2][j + 1];
                double cc = rows[i - 1][0], d = rows[i - 1][j + 1];
                rows[i][j] = (((cc * b) - (a * d)) / pivot);
            }
        }

        int sign = Math.Sign(rows[0][0] == 0 ? 1 : rows[0][0]);
        int changes = 0;
        for (int i = 1; i <= n; i++)
        {
            double val = rows[i][0];
            if (Math.Abs(val) < 1e-12) val = 1e-12;
            int s = Math.Sign(val);
            if (s != 0 && s != sign) { changes++; sign = s; }
        }
        return changes;
    }

    // Closed-loop characteristic polynomial N(s) of the canonical dq loop: the numerator of
    // det(I + Y_conv,dq * Z_grid,dq), cleared of the PLL denominator Dp AND the current-loop denominator Dc.
    // Real coefficients - a genuine Routh-amenable oracle independent of the Nyquist winding. Multiplying
    // through by the Hurwitz Dc adds only LHP roots, which do not affect the RHP count.
    //   Y = [[ (Id0/Vd0) G_ci,  Iq0 G_pll ], [ (Iq0/Vd0) G_ci, -Id0 G_pll ]],  G_ci = wci/Dc, G_pll = p/Dp.
    internal static double[] CharPoly(double vd0, double id0, double iq0, double fbwHz, double zeta,
        double scr, double xOverR, double pRatedPu, double f0Hz, double wci)
    {
        double wn = 2.0 * Math.PI * fbwHz;
        double kp = 2.0 * zeta * wn / vd0;
        double ki = wn * wn / vd0;
        double[] dp = { vd0 * ki, vd0 * kp, 1.0 };   // Dp = s^2 + Vd0 kp s + Vd0 ki
        double[] p = { ki, kp };                     // PLL numerator k_i + k_p s
        double[] dc = { wci, 1.0 };                  // Dc = s + wci  (G_ci = wci/Dc)

        (double r, double x) = GridImpedanceAssembler.TheveninRx(scr, xOverR, pRatedPu);
        double w0 = 2.0 * Math.PI * f0Hz;
        double l = x / w0;
        double[] z = { r, l };                       // R + L s
        double[] zoff = { x };                       // w0 L = X

        double a = id0 / vd0, b = iq0 / vd0;
        double[] g = Mul(dc, dp);                     // common denominator G = Dc*Dp (degree 3)
        // G * Y entries (polynomials):  G Y_dd = a wci Dp,  G Y_dq = Iq0 p Dc,  G Y_qd = b wci Dp,  G Y_qq = -Id0 p Dc.
        double[] gYdd = Scale(dp, a * wci);
        double[] gYdq = Scale(Mul(p, dc), iq0);
        double[] gYqd = Scale(dp, b * wci);
        double[] gYqq = Scale(Mul(p, dc), -id0);
        // G * T entries, T = Y*Z with Z = [[Z,-Zoff],[Zoff,Z]].
        double[] gT11 = Add(Mul(gYdd, z), Mul(gYdq, zoff));
        double[] gT12 = Add(Scale(Mul(gYdd, zoff), -1.0), Mul(gYdq, z));
        double[] gT21 = Add(Mul(gYqd, z), Mul(gYqq, zoff));
        double[] gT22 = Add(Scale(Mul(gYqd, zoff), -1.0), Mul(gYqq, z));
        // N = G^2 det(I+T) = (G + G T11)(G + G T22) - (G T12)(G T21).
        double[] d1 = Add(g, gT11);
        double[] d2 = Add(g, gT22);
        return Add(Mul(d1, d2), Scale(Mul(gT12, gT21), -1.0));
    }

    private static (NyquistVerdict Verdict, int Routh) Analyse(
        double vd0, double id0, double iq0, double fbwHz, double zeta, double scr)
    {
        const double xr = 10.0, pRated = 1.0, f0 = 50.0;
        var op = new ConverterOperatingPoint(vd0, 0.0, id0, iq0, vd0 * id0, -vd0 * iq0);
        var conv = GridFollowingImpedance.FromParameters(2, op, ImpedanceModelDetail.PllOuter,
            ProvenanceTag.CuratedPlaceholder, fbwHz, zeta, 0.0);
        var opt = new ImpedanceStabilityOptions
        {
            FMinHz = 0.05, FMaxHz = 2000.0, PointsPerDecade = 200, F0Hz = f0,
            Criterion = StabilityCriterion.Mimo, UseCanonicalThevenin = true,
            Scr = scr, XOverR = xr, PRatedPu = pRated, ScrSteps = 0,
        };
        ImpedanceStabilityResult r = ImpedanceStabilityScan.Run(opt, conv);
        int routh = RouthRhpCount(CharPoly(vd0, id0, iq0, fbwHz, zeta, scr, xr, pRated, f0, conv.CurrentLoopWci));
        return (r.Nyquist, routh);
    }

    [Fact]
    public void Routh_rhp_count_equals_nyquist_unstable_count_on_a_stable_scr()
    {
        (NyquistVerdict v, int routh) = Analyse(vd0: 1.0, id0: 0.8, iq0: 0.2, fbwHz: 10.0, zeta: 0.707, scr: 6.0);
        Assert.Equal(0, routh);
        Assert.Equal(routh, v.UnstableModeCount);
        Assert.True(v.Stable);
    }

    [Fact]
    public void Routh_rhp_count_equals_nyquist_unstable_count_on_an_unstable_scr()
    {
        // Very weak grid (SCR 0.3) -> the constant-power/PLL negative-resistance destabilises: unstable.
        (NyquistVerdict v, int routh) = Analyse(vd0: 1.0, id0: 1.0, iq0: 0.2, fbwHz: 5.0, zeta: 0.707, scr: 0.3);
        Assert.True(routh > 0, $"expected an RHP root at this weak-grid point (routh={routh})");
        Assert.Equal(routh, v.UnstableModeCount);
        Assert.False(v.Stable);
    }

    [Fact]
    public void Workstream_c_modal_corroborates_swing_rhp_count_on_the_canonical_case()
    {
        // Corroboration (not the primary oracle): a stable swing-only case has zero RHP eigenvalues, matching
        // a stable impedance verdict. C's inverter coupling is a COI archetype, so this is a structural
        // agreement check on the shared stable-baseline case.
        GridModel model = GridSim.Core.IO.IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);
        SmallSignalModel ssm = SmallSignalAssembler.FromSolvedCase(model, pf);
        ModalResult modal = ModalAnalysis.Analyze(ssm);
        Assert.Equal(0, modal.RhpEigenvalueCount);

        (NyquistVerdict v, int routh) = Analyse(vd0: 1.0, id0: 0.8, iq0: 0.1, fbwHz: 10.0, zeta: 0.707, scr: 5.0);
        Assert.Equal(0, routh);
        Assert.Equal(modal.RhpEigenvalueCount, v.UnstableModeCount);   // both zero on the stable baseline
    }
}
