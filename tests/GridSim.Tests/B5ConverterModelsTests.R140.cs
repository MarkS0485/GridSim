using System.Numerics;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Impedance;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R140: the grid-forming impedance's power loop used two unit-inconsistent scalings (synchronising resistance
/// dp = 0.01 x damping, MW/Hz read as pu; time constant + 1e-4 x synthetic inertia, MW.s/Hz read as seconds), so the
/// verdict scaled with the absolute MW size of the unit. It is now derived on the converter's rating S: per-unit droop
/// dp = m_p = S / (D f0), virtual inertia constant H_v = J f0 / (2 S), time constant 1 / (2 pi f_pl) + 2 H_v m_p (= J / D
/// with an authored damping), the VSM / droop equivalence of D'Arco and Suul (2014). Pins: the same per-unit control law
/// on a small and a large unit gives the same per-unit impedance (it did not); a model with no damping and no synthetic
/// inertia (every flag-built model) is bit-identical to the pre-R140 arithmetic; bad inputs fail loud.
/// </summary>
public sealed partial class B5ConverterModelsTests
{
    private static readonly ConverterOperatingPoint R140Op = new(1.0, 0.0, 0.8, -0.1, 0.8, 0.1);

    private static GridFormingImpedance Gfm(double xv, double plHz, double damping, double syn, double rating, double f0 = 50.0)
        => GridFormingImpedance.FromParameters(3, f0, R140Op, ImpedanceModelDetail.PllOuter, ProvenanceTag.CuratedPlaceholder,
            xv, plHz, damping, syn, rating);

    private static void AssertSameComplex(Complex a, Complex b, string what)
    {
        AssertBits(a.Real, b.Real, what + " re");
        AssertBits(a.Imaginary, b.Imaginary, what + " im");
    }

    private static readonly double[] R140Omegas = { 0.0, 1.0, 2.0 * Math.PI * 0.7, 2.0 * Math.PI * 5.0, 2.0 * Math.PI * 49.0, 2.0 * Math.PI * 400.0 };

    [Fact]
    public void R140_the_same_per_unit_law_on_a_small_and_a_large_unit_gives_the_same_per_unit_impedance()
    {
        // 100 MVA with D = 40 MW/Hz, J = 120 MW.s/Hz, and 1000 MVA with ten times both: the same 5 % droop
        // (m_p = S / (D f0) = 0.05) and the same H_v = J f0 / (2 S) = 30 s. Before R140 the large unit's dp was 4 pu against
        // 0.4 pu, and its time constant 0.108 s longer (1e-4 x 1,200 against 1e-4 x 120).
        GridFormingImpedance small = Gfm(0.2, 5.0, 40.0, 120.0, 100.0);
        GridFormingImpedance large = Gfm(0.2, 5.0, 400.0, 1_200.0, 1_000.0);
        foreach (double w in R140Omegas)
        {
            Complex2x2 a = small.ImpedanceDq(w), b = large.ImpedanceDq(w);
            AssertSameComplex(a.M11, b.M11, $"w={w} M11");
            AssertSameComplex(a.M12, b.M12, $"w={w} M12");
            AssertSameComplex(a.M21, b.M21, $"w={w} M21");
            AssertSameComplex(a.M22, b.M22, $"w={w} M22");
        }
    }

    [Fact]
    public void R140_the_synchronising_resistance_is_the_per_unit_droop_and_the_lag_is_J_over_D()
    {
        const double xv = 0.2, pl = 5.0, d = 15.0, j = 8.0, s = 50.0, f0 = 50.0;   // gfm-bess-2024's D and J on 50 MVA
        GridFormingImpedance m = Gfm(xv, pl, d, j, s, f0);
        double rv = ConverterDefaults.GfmVirtualROverX * xv;
        double dp = s / (d * f0);                                   // 0.0666... pu: a 6.7 % droop
        // At w = 0 the diagonal is R_v + D_p exactly (the inductance and the lag vanish).
        Complex z0 = m.ImpedanceDq(0.0).M11;
        AssertBits(rv + dp, z0.Real, "Z(0) = R_v + D_p");
        AssertBits(0.0, z0.Imaginary, "Z(0) imaginary");
        // The lag: Z_p(jw) = D_p / (1 + j w tau) with tau = 1 / (2 pi f_pl) + J / D (= 0.0318... + 0.5333... s).
        double tau = 1.0 / (2.0 * Math.PI * pl) + j / d;
        double w = 2.0 * Math.PI * 1.3;
        Complex expected = rv + new Complex(0.0, w) * (xv / (2.0 * Math.PI * f0)) + dp / (1.0 + new Complex(0.0, w) * tau);
        Complex got = m.ImpedanceDq(w).M11;
        Assert.True(Complex.Abs(got - expected) <= 1e-14 * Complex.Abs(expected), $"Z({w}) {got} vs {expected}");
    }

    [Fact]
    public void R140_no_damping_and_no_synthetic_inertia_is_the_pre_R140_arithmetic_bit_for_bit()
    {
        foreach (double xv in new[] { 0.2, 0.15, 0.05 })
            foreach (double pl in new[] { 5.0, 2.0, 1e-6 })
                foreach (double rating in new[] { 1.0, 192.0, 2_500.0 })
                {
                    GridFormingImpedance m = Gfm(xv, pl, 0.0, 0.0, rating);
                    foreach (double w in R140Omegas)
                        AssertSameComplex(OldGfmDiagonal(xv, pl, 0.0, 0.0, 50.0, w), m.ImpedanceDq(w).M11, $"xv {xv} pl {pl} S {rating} w {w}");
                }
    }

    [Theory]
    [InlineData(0.0, 10.0, 0.0)]
    [InlineData(double.NaN, 10.0, 0.0)]
    [InlineData(-5.0, 10.0, 0.0)]
    [InlineData(double.PositiveInfinity, 10.0, 0.0)]
    [InlineData(100.0, -1.0, 0.0)]
    [InlineData(100.0, double.NaN, 0.0)]
    [InlineData(100.0, 10.0, -2.0)]
    [InlineData(100.0, 10.0, double.PositiveInfinity)]
    public void R140_a_bad_rating_damping_or_synthetic_inertia_is_refused(double rating, double damping, double syn)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Gfm(0.2, 5.0, damping, syn, rating));

    // GR-561: a virtual reactance that is not a finite number above zero, or a NaN power-loop bandwidth, used to be
    // accepted (a zero or negative X_v can make Z singular, which the admittance turned into an open circuit).
    [Theory]
    [InlineData(0.0, 5.0)]
    [InlineData(-0.2, 5.0)]
    [InlineData(double.NaN, 5.0)]
    [InlineData(double.PositiveInfinity, 5.0)]
    [InlineData(0.2, double.NaN)]
    public void GR561_a_bad_virtual_reactance_or_power_loop_bandwidth_is_refused(double xv, double plHz)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Gfm(xv, plHz, 0.0, 0.0, 100.0));

    [Fact]
    public void GR561_the_source_no_longer_turns_a_singular_impedance_into_a_zero_admittance()
        => Assert.DoesNotContain("inv ?? Complex2x2.Diagonal(Complex.Zero)", CoreSrc("Dynamics", "Impedance", "GridFormingImpedance.cs"));

    [Fact]
    public void R140_the_unit_inconsistent_scalings_are_gone_from_the_source()
    {
        string src = CoreSrc("Dynamics", "Impedance", "GridFormingImpedance.cs");
        Assert.DoesNotContain("GfmDampingToSyncResistanceScale", src);
        Assert.DoesNotContain("GfmSynInertiaToTauScale", src);
        Assert.DoesNotContain("GfmMinSyncResistancePu", src);
        Assert.Contains("ratingMva / (dampingMwPerHz * f0Hz)", src);
    }

    /// <summary>The pre-R140 diagonal of ImpedanceDq (1.95.223, with the then constants written as their values), for the
    /// no-damping, no-synthetic-inertia inputs the bit-identity test feeds it.</summary>
    private static Complex OldGfmDiagonal(double xv, double pl, double damping, double syn, double f0, double omega)
    {
        double w0 = 2.0 * Math.PI * f0;
        double lv = xv / w0;
        double rv = 0.05 * xv;
        double dp = Math.Max(0.05, damping > 0 ? 0.01 * damping : 0.30);
        double taup = Math.Max(1e-3, (1.0 / (2.0 * Math.PI * Math.Max(1e-3, pl))) + (syn > 0 ? 1e-4 * syn : 0.0));
        Complex s = new(0.0, omega);
        Complex zRl = rv + (s * lv);
        Complex zp = dp / (1.0 + (s * taup));
        return zRl + zp;
    }
}
