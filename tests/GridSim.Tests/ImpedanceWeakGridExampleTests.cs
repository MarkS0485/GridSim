using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Impedance;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GitHub issue #11: on the documented weak-grid example (case9 bus 2, grid-following, PLL 80 Hz, damping 0.707,
/// canonical Thevenin X/R 10, 60 Hz) the SISO and MIMO criteria gave different verdicts. The decisive check is an
/// independent Routh-Hurwitz count of the right-half-plane roots of the closed-loop characteristic polynomial (the
/// numerator of det(I + Y_dq Z_dq), <see cref="ImpedanceModalAgreementTests.CharPoly"/>), which uses no Nyquist winding.
/// It agrees with MIMO at every swept SCR; SISO, which drops the PLL's cross-sequence coupling, is wrong at seven of the
/// ten. So the CLI default for a grid-following converter with its PLL modelled is now MIMO (criterion "auto"), and this
/// test pins both the oracle agreement and the SISO disagreement that justified the change. For the ideal-PLL
/// (outer-loop-only) detail the opposite holds, also pinned below.
/// <para>The operating point is the CLI's printed one (Vd0 1.0250, Id0 0.8283, Iq0 -0.0338 pu); the Routh count is not
/// near a boundary at any row, so the 4-digit rounding does not matter. The loop gain is Y_conv / SCR whatever the
/// rating (R008), so the rating 1.92 of case9 bus 2 does not enter the oracle.</para>
/// </summary>
public sealed class ImpedanceWeakGridExampleTests
{
    private const double Vd0 = 1.025, Id0 = 0.8283, Iq0 = -0.0338, PllBwHz = 80, Zeta = 0.707, F0 = 60, XOverR = 10;

    private static GridFollowingImpedance Converter() => GridFollowingImpedance.FromParameters(2,
        new ConverterOperatingPoint(Vd0, 0.0, Id0, Iq0, Vd0 * Id0, -Vd0 * Iq0), ImpedanceModelDetail.PllOuter,
        ProvenanceTag.CuratedPlaceholder, PllBwHz, Zeta, 0.0);

    private static NyquistVerdict Scan(StabilityCriterion c, double scr) => ImpedanceStabilityScan.Run(new ImpedanceStabilityOptions
    {
        // The CLI's defaults for the documented command (fmin 0.1, fmax 250, 50 points per decade).
        FMinHz = 0.1, FMaxHz = 250, PointsPerDecade = 50, F0Hz = F0, Criterion = c, UseCanonicalThevenin = true,
        Scr = scr, XOverR = XOverR, PRatedPu = 1.92, ScrSteps = 0,
    }, Converter()).Nyquist;

    /// <summary>(SCR, closed-loop RHP roots by Routh, SISO verdict stable?). Measured 2026-10-31.</summary>
    public static TheoryData<double, int, bool> Rows() => new()
    {
        { 0.1, 0, false }, { 0.3, 0, false }, { 0.375, 0, false }, { 0.65, 0, true }, { 0.925, 1, true },
        { 1.0, 1, true }, { 1.25, 1, true }, { 1.475, 1, true }, { 1.6, 0, true }, { 3.0, 0, true },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void MIMO_agrees_with_the_Routh_oracle_and_SISO_does_not_where_the_coupling_matters(double scr, int routhRhp, bool sisoStable)
    {
        int routh = ImpedanceModalAgreementTests.RouthRhpCount(
            ImpedanceModalAgreementTests.CharPoly(Vd0, Id0, Iq0, PllBwHz, Zeta, scr, XOverR, 1.0, F0, Converter().CurrentLoopWci));
        Assert.Equal(routhRhp, routh);
        NyquistVerdict mimo = Scan(StabilityCriterion.Mimo, scr);
        Assert.Equal(routh, mimo.UnstableModeCount);
        Assert.Equal(routh == 0, mimo.Stable);
        // SISO is pinned as measured, including where it is wrong (the reason the default changed).
        Assert.Equal(sisoStable, Scan(StabilityCriterion.Siso, scr).Stable);
    }

    /// <summary>The documented fast-PLL example (case9 --pll-bw 300, SCR 3 and the sweep around the new critical SCR
    /// 5.62): (SCR, closed-loop RHP roots by Routh, SISO verdict stable?). SISO called SCR 3 STABLE with a critical SCR of
    /// 1.12; the closed loop has a right-half-plane root up to between 5.5 and 5.75.</summary>
    public static TheoryData<double, int, bool> FastPllRows() => new()
    {
        { 1.0, 1, false }, { 1.25, 1, true }, { 3.0, 1, true }, { 5.5, 1, true }, { 5.75, 0, true }, { 10.0, 0, true },
    };

    [Theory]
    [MemberData(nameof(FastPllRows))]
    public void Fast_PLL_MIMO_agrees_with_the_Routh_oracle(double scr, int routhRhp, bool sisoStable)
    {
        var conv = GridFollowingImpedance.FromParameters(2, new ConverterOperatingPoint(Vd0, 0.0, Id0, Iq0, Vd0 * Id0, -Vd0 * Iq0),
            ImpedanceModelDetail.PllOuter, ProvenanceTag.CuratedPlaceholder, 300.0, Zeta, 0.0);
        int routh = ImpedanceModalAgreementTests.RouthRhpCount(
            ImpedanceModalAgreementTests.CharPoly(Vd0, Id0, Iq0, 300.0, Zeta, scr, XOverR, 1.0, F0, conv.CurrentLoopWci));
        Assert.Equal(routhRhp, routh);
        NyquistVerdict Run(StabilityCriterion c) => ImpedanceStabilityScan.Run(new ImpedanceStabilityOptions
        {
            FMinHz = 0.1, FMaxHz = 250, PointsPerDecade = 50, F0Hz = F0, Criterion = c, UseCanonicalThevenin = true,
            Scr = scr, XOverR = XOverR, PRatedPu = 1.92, ScrSteps = 0,
        }, conv).Nyquist;
        NyquistVerdict mimo = Run(StabilityCriterion.Mimo);
        Assert.Equal(routh, mimo.UnstableModeCount);
        Assert.Equal(sisoStable, Run(StabilityCriterion.Siso).Stable);
    }

    [Fact]
    public void The_documented_example_reads_STABLE_under_the_default_criterion()
    {
        // case9 --pll-bw 80 --scr 0.3: the default for a grid-following converter is MIMO, which agrees with the oracle.
        NyquistVerdict v = Scan(StabilityCriterion.Mimo, 0.3);
        Assert.True(v.Stable);
        Assert.Equal(0, v.Encirclements);
    }
    // ---- outer-loop-only (ideal PLL, G_pll = 1 / Vd0) -------------------------------------------------------------------

    private static double[] Mul(double[] a, double[] b)
    {
        var r = new double[a.Length + b.Length - 1];
        for (int i = 0; i < a.Length; i++) for (int j = 0; j < b.Length; j++) r[i + j] += a[i] * b[j];
        return r;
    }

    private static double[] Add(double[] a, double[] b)
    {
        var r = new double[Math.Max(a.Length, b.Length)];
        for (int i = 0; i < a.Length; i++) r[i] += a[i];
        for (int i = 0; i < b.Length; i++) r[i] += b[i];
        return r;
    }

    private static double[] Scale(double[] a, double k) => a.Select(v => v * k).ToArray();

    /// <summary>The closed-loop characteristic polynomial (ascending) of the outer-loop-only converter against the
    /// canonical Thevenin: the numerator of det(I + Y_dq Z_dq) with Y = [[a G_ci, Iq0 / Vd0], [b G_ci, -Id0 / Vd0]],
    /// G_ci = wci / (s + wci), cleared of the Hurwitz Dc = s + wci (adds only a left-half-plane root).</summary>
    private static double[] OuterLoopCharPoly(double vd0, double id0, double iq0, double scr, double xOverR, double f0, double wci)
    {
        double[] dc = { wci, 1.0 };
        (double r, double x) = GridImpedanceAssembler.TheveninRx(scr, xOverR, 1.0);
        double[] z = { r, x / (2.0 * Math.PI * f0) };
        double[] zoff = { x };
        double a = id0 / vd0, b = iq0 / vd0;
        double[] gYdd = { a * wci }, gYqd = { b * wci };
        double[] gYdq = Scale(dc, iq0 / vd0), gYqq = Scale(dc, -id0 / vd0);
        double[] gT11 = Add(Mul(gYdd, z), Mul(gYdq, zoff));
        double[] gT12 = Add(Scale(Mul(gYdd, zoff), -1.0), Mul(gYdq, z));
        double[] gT21 = Add(Mul(gYqd, z), Mul(gYqq, zoff));
        double[] gT22 = Add(Scale(Mul(gYqd, zoff), -1.0), Mul(gYqq, z));
        return Add(Mul(Add(dc, gT11), Add(dc, gT22)), Scale(Mul(gT12, gT21), -1.0));
    }

    [Theory]
    [InlineData(1.0)] [InlineData(1.25)] [InlineData(2.0)] [InlineData(3.0)] [InlineData(5.0)] [InlineData(10.0)]
    public void Outer_loop_only_SISO_agrees_with_the_Routh_oracle_and_the_improper_MIMO_loop_does_not(double scr)
    {
        // case9 --outer-loop-only: the ideal-PLL closed loop has one real right-half-plane root at every swept SCR (1 to
        // 10). SISO counts it (UNSTABLE, one mode). The MIMO winding reads STABLE: with the PLL frozen at 1 / Vd0 the
        // q-axis admittance never rolls off, the dq return difference is unbounded along the jw axis, and the winding
        // misses the root. Hence "auto" keeps SISO for this detail and the CLI refuses --criterion mimo with it.
        var conv = GridFollowingImpedance.FromParameters(2, new ConverterOperatingPoint(Vd0, 0.0, Id0, Iq0, Vd0 * Id0, -Vd0 * Iq0),
            ImpedanceModelDetail.OuterLoopOnly, ProvenanceTag.CuratedPlaceholder, 20.0, Zeta, 0.0);
        int routh = ImpedanceModalAgreementTests.RouthRhpCount(OuterLoopCharPoly(Vd0, Id0, Iq0, scr, XOverR, F0, conv.CurrentLoopWci));
        NyquistVerdict Run(StabilityCriterion c) => ImpedanceStabilityScan.Run(new ImpedanceStabilityOptions
        {
            FMinHz = 0.1, FMaxHz = 250, PointsPerDecade = 50, F0Hz = F0, Criterion = c, UseCanonicalThevenin = true,
            Scr = scr, XOverR = XOverR, PRatedPu = 1.92, ScrSteps = 0,
        }, conv).Nyquist;
        Assert.Equal(1, routh);
        NyquistVerdict siso = Run(StabilityCriterion.Siso);
        Assert.False(siso.Stable);
        Assert.Equal(routh, siso.UnstableModeCount);
        Assert.True(Run(StabilityCriterion.Mimo).Stable);   // the improper loop's false STABLE, pinned as measured
    }
}

public sealed class ImpedanceCriterionCliTests
{
    [Fact]
    public void The_default_criterion_is_MIMO_for_grid_following_and_the_documented_example_reads_STABLE()
    {
        CliProcess.Result r = CliProcess.RunBuilt("impedance-stability", "case9", "--pll-bw", "80", "--scr", "0.3", "--scr-min", "0.1");
        Assert.True(r.ExitCode == 0, r.StdErr);
        Assert.Contains("detail PllOuter, criterion Mimo", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("-> STABLE", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Auto_keeps_SISO_for_grid_forming_and_for_the_ideal_PLL()
    {
        CliProcess.Result gfm = CliProcess.RunBuilt("impedance-stability", "case9", "--converter", "gfm");
        Assert.True(gfm.ExitCode == 0, gfm.StdErr);
        Assert.Contains("criterion Siso", gfm.StdOut, StringComparison.Ordinal);
        CliProcess.Result olo = CliProcess.RunBuilt("impedance-stability", "case9", "--outer-loop-only");
        Assert.True(olo.ExitCode == 0, olo.StdErr);
        Assert.Contains("detail OuterLoopOnly, criterion Siso", olo.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void MIMO_with_the_ideal_PLL_is_refused()
    {
        CliProcess.Result r = CliProcess.RunBuilt("impedance-stability", "case9", "--outer-loop-only", "--criterion", "mimo");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("--criterion mimo cannot be used with --outer-loop-only", r.StdErr, StringComparison.Ordinal);
    }
}
