using System.Numerics;
using GridSim.Core.Lines;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R4-04: line constants from geometry. Every oracle is a closed form or a separate derivation written in this file;
/// no published matrix is used (the plan warns that a remembered one did not match).
/// </summary>
public class LineConstantsTests
{
    private const double Mu0 = 4e-7 * Math.PI;
    private const double Eps0 = 8.8541878128e-12;

    private static ConductorData C(string n, double x, double h, double r = 0.0, double gmr = 0.01, double rad = 0.01,
        ConductorRole role = ConductorRole.Phase) => new(n, role, x, h, r, gmr, rad);

    private static LineGeometry G(EarthReturnMethod m, double rho, bool transposed, params ConductorData[] c) =>
        new(c, 50.0, rho, m, transposed);

    [Fact]
    public void Perfect_ground_single_conductor_matches_the_closed_forms()
    {
        var res = LineConstantsSolver.Solve(G(EarthReturnMethod.PerfectGround, 100, false, C("a", 0, 10)));
        double x = 2 * Math.PI * 50 * Mu0 / (2 * Math.PI) * Math.Log(2.0 * 10 / 0.01) * 1000;
        Assert.Equal(0.47758, x, 4); // the plan's figure, recomputed here
        Assert.Equal(x, res.PhaseZ[0, 0].Imaginary, 12);
        Assert.Equal(0.0, res.PhaseZ[0, 0].Real);
        double c = 2 * Math.PI * Eps0 / Math.Log(2.0 * 10 / 0.01) * 1000;
        Assert.Equal(7.319e-9, c, 12);
        Assert.InRange(res.PhaseC[0, 0] / c, 1 - 1e-12, 1 + 1e-12);
        Assert.Equal("perfect-ground", res.Method);
    }

    [Fact]
    public void Earth_integral_converges_to_perfect_ground_like_sqrt_resistivity()
    {
        double pg = LineConstantsSolver.Solve(G(EarthReturnMethod.PerfectGround, 1, false, C("a", 0, 10))).PhaseZ[0, 0].Imaginary;
        double Err(double rho) => Math.Abs(LineConstantsSolver.Solve(G(EarthReturnMethod.EarthIntegral, rho, false, C("a", 0, 10))).PhaseZ[0, 0].Imaginary - pg) / pg;
        double e3 = Err(1e-3), e5 = Err(1e-5), e7 = Err(1e-7);
        Assert.True(e3 < 0.03, "1e-3: " + e3);
        Assert.InRange(e3 / e5, 8.5, 11.5); // sqrt(100) = 10
        Assert.InRange(e5 / e7, 8.5, 11.5);
        Assert.True(e7 < 5e-4, "1e-7: " + e7);
    }

    [Fact]
    public void Low_frequency_earth_resistance_tends_to_pi_squared_f_1e_minus_4_ohm_per_km()
    {
        var g = new LineGeometry(new[] { C("a", 0, 0.05, 0.0, 0.004, 0.005) }, 50.0, 100.0, EarthReturnMethod.EarthIntegral, false);
        double r = LineConstantsSolver.Solve(g).PhaseZ[0, 0].Real;
        double expect = Math.PI * Math.PI * 50 * 1e-4; // omega mu0 / 8 per km
        Assert.InRange(r / expect, 0.999, 1.001);
    }

    [Fact]
    public void Earth_integral_agrees_with_an_independent_log_variable_trapezoid_rule()
    {
        double omega = 2 * Math.PI * 50, alpha = omega * Mu0 / 100.0, hsum = 20.0, x = 3.0;
        Complex expect = Complex.Zero;
        const double du = 0.004;
        for (double u = -40; u < Math.Log(60 / hsum); u += du)
        {
            double l = Math.Exp(u);
            Complex den = l + Complex.Sqrt(new Complex(l * l, alpha));
            expect += Math.Exp(-hsum * l) * Math.Cos(x * l) / den * l * du;
        }
        Complex got = EarthReturn.Integral(hsum, x, alpha);
        Assert.True(Complex.Abs(got - expect) / Complex.Abs(expect) < 1e-9, $"{got} vs {expect}");
    }

    [Theory]
    [InlineData(EarthReturnMethod.PerfectGround, 1e-5)]
    [InlineData(EarthReturnMethod.EarthIntegral, 1e-4)]
    public void Two_conductor_pair_self_minus_mutual_is_the_loop_reactance(EarthReturnMethod m, double tol)
    {
        // Far above ground the image terms cancel in Zs - Zm: X = (w mu0 / 2 pi) ln(D / r').
        var g = new LineGeometry(new[] { C("a", -0.5, 1000, 0, 0.005, 0.005), C("b", 0.5, 1000, 0, 0.005, 0.005) }, 50.0, 100.0, m, false);
        var r = LineConstantsSolver.Solve(g);
        double expect = 2 * Math.PI * 50 * Mu0 / (2 * Math.PI) * Math.Log(1.0 / 0.005) * 1000;
        Assert.InRange((r.PhaseZ[0, 0] - r.PhaseZ[0, 1]).Imaginary / expect, 1 - tol, 1 + tol);
    }

    [Fact]
    public void Kron_reduction_and_capacitance_follow_the_hand_formulas()
    {
        var ph = C("a", 0, 10, 0.1, 0.01, 0.01);
        var gw = C("g", 3, 12, 0.3, 0.004, 0.005, ConductorRole.GroundWire);
        var res = LineConstantsSolver.Solve(G(EarthReturnMethod.PerfectGround, 100, false, ph, gw));
        double k = 2 * Math.PI * 50 * Mu0 / (2 * Math.PI) * 1000; // ohm/km per unit of ln
        Complex zpp = new(0.1, k * Math.Log(20 / 0.01));
        Complex zgg = new(0.3, k * Math.Log(24 / 0.004));
        double dpg = Math.Sqrt(9 + 4), ipg = Math.Sqrt(9 + 22 * 22);
        Complex zpg = new(0, k * Math.Log(ipg / dpg));
        Complex red = zpp - zpg * zpg / zgg;
        Assert.True(Complex.Abs(res.PhaseZ[0, 0] - red) < 1e-12 * Complex.Abs(red));
        double pk = 1 / (2 * Math.PI * Eps0) / 1000;
        double ppp = pk * Math.Log(20 / 0.01), pgg = pk * Math.Log(24 / 0.005), ppg = pk * Math.Log(ipg / dpg);
        Assert.InRange(res.PhaseC[0, 0] * (ppp - ppg * ppg / pgg), 1 - 1e-12, 1 + 1e-12);
        Assert.Equal(new[] { "a" }, res.PhaseConductors);
        Assert.Equal(new[] { "a", "g" }, res.AllConductors);
    }

    [Fact]
    public void Transposed_three_phase_sequence_constants_match_direct_formulas_and_the_fortescue_transform()
    {
        double h = 100, s = Math.Sqrt(3);
        var res = LineConstantsSolver.Solve(G(EarthReturnMethod.PerfectGround, 100, true,
            C("a", -1, h, 0.05, 0.01), C("b", 1, h, 0.05, 0.01), C("c", 0, h + s, 0.05, 0.01)));
        double k = 2 * Math.PI * 50 * Mu0 / (2 * Math.PI) * 1000;
        (double X, double H)[] p = { (-1, h), (1, h), (0, h + s) };
        double self = 0, mut = 0;
        for (int i = 0; i < 3; i++) self += Math.Log(2 * p[i].H / 0.01) / 3;
        for (int i = 0; i < 3; i++) for (int j = i + 1; j < 3; j++)
        {
            double d = Math.Sqrt((p[i].X - p[j].X) * (p[i].X - p[j].X) + (p[i].H - p[j].H) * (p[i].H - p[j].H));
            double im = Math.Sqrt((p[i].X - p[j].X) * (p[i].X - p[j].X) + (p[i].H + p[j].H) * (p[i].H + p[j].H));
            mut += Math.Log(im / d) / 3;
        }
        var q = res.Sequence!;
        Assert.Equal(k * (self - mut), q.Z1.Imaginary, 10);
        Assert.Equal(k * (self + 2 * mut), q.Z0.Imaginary, 10);
        Assert.Equal(0.05, q.Z1.Real, 12);
        // Image effects are small at 100 m: Z1 is close to the spacing formula ln(D / r') with D = 2 m.
        Assert.InRange(q.Z1.Imaginary / (k * Math.Log(2 / 0.01)), 0.995, 1.005);
        // Fortescue: A^-1 Z A is diagonal for a transposed line; its [0,0] and [1,1] are Z0 and Z1.
        var A = new Complex[3, 3]; var Ai = new Complex[3, 3];
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++)
        {
            double ang = 2 * Math.PI * ((i * j) % 3) / 3.0;
            A[i, j] = new Complex(Math.Cos(ang), Math.Sin(ang));
            Ai[i, j] = Complex.Conjugate(new Complex(Math.Cos(2 * Math.PI * ((i * j) % 3) / 3.0), Math.Sin(2 * Math.PI * ((i * j) % 3) / 3.0))) / 3.0;
        }
        var t = SequenceReduction.Mul(Ai, SequenceReduction.Mul(res.TransposedZ, A));
        Assert.True(Complex.Abs(t[0, 0] - q.Z0) < 1e-10 * Complex.Abs(q.Z0));
        Assert.True(Complex.Abs(t[1, 1] - q.Z1) < 1e-10 * Complex.Abs(q.Z1));
        Assert.True(Complex.Abs(t[2, 2] - q.Z1) < 1e-10 * Complex.Abs(q.Z1));
        Assert.True(q.C0FaradPerKm < q.C1FaradPerKm); // zero-sequence capacitance is the smaller one for a line over ground
    }

    [Fact]
    public void Per_unit_conversion_uses_the_stated_base()
    {
        var res = LineConstantsSolver.Solve(G(EarthReturnMethod.PerfectGround, 100, true,
            C("a", -3, 15, 0.1), C("b", 0, 15, 0.1), C("c", 3, 15, 0.1)));
        var (r, x, b) = res.PositiveSequencePerUnit(100, 132, 100);
        double zb = 132.0 * 132 / 100;
        Assert.Equal(res.Sequence!.Z1.Real * 100 / zb, r, 15);
        Assert.Equal(res.Sequence.Z1.Imaginary * 100 / zb, x, 15);
        Assert.Equal(2 * Math.PI * 50 * res.Sequence.C1FaradPerKm * 100 * zb, b, 15);
        Assert.Throws<ArgumentOutOfRangeException>(() => res.PositiveSequencePerUnit(0, 132, 100));
    }

    [Fact]
    public void Bad_geometry_is_refused_and_non_three_phase_has_no_sequence()
    {
        Assert.Throws<ArgumentException>(() => LineConstantsSolver.Solve(G(EarthReturnMethod.PerfectGround, 100, false, C("a", 0, 10), C("b", 0, 10.005))));
        Assert.Throws<ArgumentException>(() => LineConstantsSolver.Solve(G(EarthReturnMethod.PerfectGround, 100, false, C("a", 0, -1))));
        Assert.Throws<ArgumentException>(() => LineConstantsSolver.Solve(G(EarthReturnMethod.PerfectGround, 100, false, C("a", 0, 10), C("a", 1, 10))));
        Assert.Throws<ArgumentException>(() => LineConstantsSolver.Solve(new LineGeometry(new[] { C("a", 0, 10) }, 0.0, 100, EarthReturnMethod.PerfectGround, false)));
        Assert.Throws<ArgumentException>(() => LineConstantsSolver.Solve(G(EarthReturnMethod.EarthIntegral, 0, false, C("a", 0, 10))));
        Assert.Throws<ArgumentException>(() => LineConstantsSolver.Solve(G(EarthReturnMethod.PerfectGround, 100, false, C("g", 0, 10, role: ConductorRole.GroundWire))));
        var two = LineConstantsSolver.Solve(G(EarthReturnMethod.PerfectGround, 100, false, C("a", 0, 10), C("b", 2, 10)));
        Assert.Null(two.Sequence);
        Assert.StartsWith("OUT OF SCOPE", two.SequenceNote);
    }

    [Fact]
    public void Results_are_bit_repeatable()
    {
        LineConstantsResult Run() => LineConstantsSolver.Solve(G(EarthReturnMethod.EarthIntegral, 50, false,
            C("a", -4, 12, 0.07), C("b", 0, 12, 0.07), C("c", 4, 12, 0.07), C("g", 0, 16, 0.4, 0.004, 0.005, ConductorRole.GroundWire)));
        var a = Run(); var b = Run();
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.PhaseZ[i, j].Real), BitConverter.DoubleToInt64Bits(b.PhaseZ[i, j].Real));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.PhaseZ[i, j].Imaginary), BitConverter.DoubleToInt64Bits(b.PhaseZ[i, j].Imaginary));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.PhaseC[i, j]), BitConverter.DoubleToInt64Bits(b.PhaseC[i, j]));
        }
        Assert.Equal(EarthReturn.TailBound, a.IntegralTailBound);
        Assert.NotNull(a.SequenceNote); // untransposed: stated approximation
    }
}
