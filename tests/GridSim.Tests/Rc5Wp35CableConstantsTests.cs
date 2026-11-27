using System.Numerics;
using System.Text.Json;
using GridSim.Core.Lines;
using GridSim.Core.Lines.Cables;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP35: single-core cable constants (core, sheath, armour; buried; both-ends, single-point and cross bonding).
/// Oracles, none read from the code under test:
/// <list type="bullet">
/// <item>SciPy 1.18 <c>scipy.special.iv/kv</c> (AMOS) for the Bessel functions, cross-checked against mpmath at 40 digits
/// (script tools/crossval/cables/bessel.py);</item>
/// <item>closed forms: the DC resistance of a round wire and of a tube, the skin-effect asymptote with its next terms, the
/// coaxial capacitance, the O(t^2) thin-tube limit;</item>
/// <item>an independent 30-digit mpmath calculation of the same published formulas (Schelkunoff 1934; Ametani, IEEE Trans.
/// PAS-99(3), 1980; Pollaczek 1931; Wedepohl and Wilcox, Proc. IEE 120(2), 1973), script
/// tools/crossval/cables/cable_reference.py;</item>
/// <item>the structure ideal cross-bonding must have (derived in the test).</item>
/// </list>
/// Spec test 7 (a published worked example) is not here: no public printed example with complete cable data was found.
/// </summary>
public sealed class Rc5Wp35CableConstantsTests : IDisposable
{
    private const double Eps = 2.220446049250313e-16;

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

    private static CableGeometry Flat(bool armour, CableEarthMethod earth, CableBonding bonding, double f = 50.0, double rho = 100.0) =>
        new([Demo("A", -0.1, armour), Demo("B", 0.0, armour), Demo("C", 0.1, armour)], f, rho, earth, bonding, true);

    private static double Rel(Complex got, Complex want) => Complex.Abs(got - want) / Complex.Abs(want);

    // ---- 1. Bessel functions against SciPy -------------------------------------------------------------------------

    public static TheoryData<double, double, double, double, double, double, double, double, double, double> BesselPoints => new()
    {
        // z.re, z.im, I0 (re, im), I1, K0, K1: scipy.special.iv / kv (each within 4e-16 of mpmath at 40 digits).
        { 0.1, 0.1, 0.999993750001085, 0.004999996527777995, 0.04987489587674697, 0.05012489578994142, 2.0758476818230407, -0.7700335531279032, 4.832450480128302, -5.089840352561297 },
        { 1.0, 1.0, 0.9376084768060292, 0.4965299476091221, 0.36502802882708785, 0.6141603349229038, 0.08019772694651779, -0.35727745928533017, 0.024568305523740397, -0.4597194738011893 },
        { 5.0, -3.0, -22.77149306774528, -10.30089325471318, -21.558139100709024, -8.433950001963673, -0.003412124888762906, -0.0004077699810708553, -0.003640325420296299, -0.0005782773148110775 },
        { 20.0, 20.0, 26598964.05183031, 25006015.587322086, 25950009.825167008, 25030292.65594365, 1.4864176818768232e-11, -4.839903836408663e-10, 9.073785147526937e-12, -4.902220504724677e-10 },
        // 100 exp(j pi / 4) as Python forms it.
        { 70.71067811865476, 70.71067811865476, 7.3687068780949275e+28, 1.9069114093623873e+29, 7.274995682716308e+28, 1.902784139040356e+29, -9.898417996730807e-33, -2.2365355260414372e-32, -1.0012209214435171e-32, -2.2409553461611156e-32 },
    };

    [Theory]
    [MemberData(nameof(BesselPoints))]
    public void T1_Bessel_I0_I1_K0_K1_match_SciPy_to_1e_13(double zr, double zi, double i0r, double i0i, double i1r, double i1i,
        double k0r, double k0i, double k1r, double k1i)
    {
        ComplexBessel.Values v = ComplexBessel.Unscaled(new Complex(zr, zi));
        // 1e-13 (spec): the native algorithm is under 1e-14 against mpmath over the whole right half plane tested, and
        // SciPy is within 4e-16 of mpmath at these points.
        Assert.True(Rel(v.I0, new Complex(i0r, i0i)) < 1e-13, $"I0 {v.I0}");
        Assert.True(Rel(v.I1, new Complex(i1r, i1i)) < 1e-13, $"I1 {v.I1}");
        Assert.True(Rel(v.K0, new Complex(k0r, k0i)) < 1e-13, $"K0 {v.K0}");
        Assert.True(Rel(v.K1, new Complex(k1r, k1i)) < 1e-13, $"K1 {v.K1}");
    }

    [Fact]
    public void T1b_Bessel_refuses_an_argument_without_a_positive_real_part()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ComplexBessel.Scaled(new Complex(0.0, 1.0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ComplexBessel.Scaled(new Complex(-1.0, 0.5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ComplexBessel.Scaled(new Complex(double.NaN, 0.0)));
    }

    // ---- 2. and 3. Core impedance: DC limit and skin-effect asymptote ----------------------------------------------

    [Fact]
    public void T2_Core_resistance_tends_to_the_DC_value_rho_over_pi_r_squared()
    {
        // R_dc = 1.72e-8 / (pi 0.0125^2), computed in Python. At 1e-6 Hz |m r| = 2.7e-4: z/R_dc = (m r / 2) I0/I1 = 1 + (m r)^2/8
        // - (m r)^4/192 + ...; (m r)^2 is imaginary, so the real part is off by about |m r|^4/192 = 3e-17, far inside 1e-9.
        Complex z = TubularImpedance.SolidCore(2.0 * Math.PI * 1e-6, 1.72e-8, 1.0, 0.0125);
        Assert.Equal(3.503955227111167e-5, z.Real, 1e-9 * 3.503955227111167e-5);
    }

    [Fact]
    public void T3_Core_resistance_follows_the_skin_effect_asymptote_at_m_r_50()
    {
        const double rho = 1.72e-8, r = 0.0125;
        double omega = (50.0 / r) * (50.0 / r) * rho / EarthReturn.Mu0;   // |m r| = 50
        Complex z = TubularImpedance.SolidCore(omega, rho, 1.0, r);
        double delta = Math.Sqrt(2.0 * rho / (omega * EarthReturn.Mu0));
        double ratio = z.Real / (rho / (2.0 * Math.PI * r * delta));
        Assert.True(Math.Abs(ratio - 1.0) < 0.02, $"ratio {ratio}");
        // Next terms: I0/I1 ~ 1 + 1/(2 z) + 3/(8 z^2) + 3/(8 z^3) + 63/(128 z^4) with z = m r, m = (1 + j)/delta. Taking
        // Re(m (...)) / Re(m): the 1/(2z) term gives delta/(2 r), the 3/(8 z^2) term 3 delta^2/(16 r^2), the z^-3 term is
        // purely imaginary after multiplying by m, and the z^-4 term is at most 63/128 |m r|^-4 sqrt(2) = 1.1e-7. The bound
        // 1e-6 is about ten times that.
        double next = 1.0 + delta / (2.0 * r) + 3.0 * delta * delta / (16.0 * r * r);
        Assert.True(Math.Abs(ratio - next) < 1e-6, $"ratio {ratio} vs {next}");
    }

    // ---- 4. Capacitance --------------------------------------------------------------------------------------------

    [Fact]
    public void T4_Core_capacitance_is_the_coaxial_closed_form()
    {
        // C = 2 pi eps0 2.5 / ln(0.0315 / 0.0145) = 1.7926564081207227e-10 F/m (Python, IEEE double).
        CableConstantsResult one = CableConstantsSolver.Solve(new CableGeometry([Demo("A", 0.0, false)], 50.0, 100.0,
            CableEarthMethod.Wedepohl, CableBonding.BothEnds, true));
        double c = one.Line.PhaseC[0, 0] / 1000.0;
        Assert.True(Math.Abs(c - 1.7926564081207227e-10) <= 1e-15 * 1.7926564081207227e-10, $"C {c:R}");
        // Three cables: the earth screens them from each other, so C1 = C0 = the core layer's C.
        CableConstantsResult three = CableConstantsSolver.Solve(Flat(false, CableEarthMethod.Wedepohl, CableBonding.BothEnds));
        Assert.Equal(0.0, three.Line.PhaseC[0, 1]);
        Assert.Equal(three.Line.Sequence!.C1FaradPerKm, three.Line.Sequence.C0FaradPerKm);
        Assert.True(Math.Abs(three.Line.Sequence.C1FaradPerKm / 1000.0 - 1.7926564081207227e-10) <= 1e-15 * 1.7926564081207227e-10);
        // G = w C tan(delta) on the core layer.
        double g = three.PhaseG[0, 0];
        Assert.Equal(2.0 * Math.PI * 50.0 * three.Line.PhaseC[0, 0] * 0.0004, g, 4 * Eps * g);
    }

    // ---- 5. Tubes --------------------------------------------------------------------------------------------------

    [Fact]
    public void T5_Tube_impedances_tend_to_the_DC_resistance_and_to_the_thin_tube_limit_as_t_squared()
    {
        // DC: all three surface impedances tend to rho / (pi (b^2 - a^2)); the leading correction is imaginary, the real
        // one is of order |m b|^4, about 1e-14 at 1e-6 Hz.
        const double rho = 2.83e-8, a = 0.0315, b = 0.0325;
        double rdc = rho / (Math.PI * (b * b - a * a));
        TubularImpedance.Tube dc = TubularImpedance.Tubular(2.0 * Math.PI * 1e-6, rho, 1.0, a, b);
        Assert.Equal(rdc, dc.Inner.Real, 1e-9 * rdc);
        Assert.Equal(rdc, dc.Outer.Real, 1e-9 * rdc);
        Assert.Equal(rdc, dc.Mutual.Real, 1e-9 * rdc);

        // Thin tube at 50 Hz, mean radius 0.03 m: z -> rho / (2 pi r t). The departure is O(t^2) (from (t/r)^2 and
        // (t/delta)^2), so each tenfold thinning divides it by 100; the next order changes that ratio by about (t/r)^2,
        // under 0.2% at t = 1 mm, so [95, 105] holds with margin. Asserted on every step and every impedance.
        double[] t = [1e-3, 1e-4, 1e-5];
        var dev = new double[3, 3];
        for (int s = 0; s < 3; s++)
        {
            TubularImpedance.Tube z = TubularImpedance.Tubular(2.0 * Math.PI * 50.0, rho, 1.0, 0.03 - t[s] / 2.0, 0.03 + t[s] / 2.0);
            double thin = rho / (2.0 * Math.PI * 0.03 * t[s]);
            dev[s, 0] = Complex.Abs(z.Inner / thin - 1.0);
            dev[s, 1] = Complex.Abs(z.Outer / thin - 1.0);
            dev[s, 2] = Complex.Abs(z.Mutual / thin - 1.0);
        }
        for (int s = 0; s < 2; s++)
            for (int k = 0; k < 3; k++)
                Assert.InRange(dev[s, k] / dev[s + 1, k], 95.0, 105.0);
    }

    // ---- 6. Bonding ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, CableEarthMethod.Wedepohl)]
    [InlineData(true, CableEarthMethod.Pollaczek)]
    public void T6_Both_ends_bonding_gives_a_symmetric_phase_matrix(bool armour, CableEarthMethod earth)
    {
        CableConstantsResult r = CableConstantsSolver.Solve(Flat(armour, earth, CableBonding.BothEnds));
        double max = 0.0;
        foreach (Complex v in r.Line.PrimitiveZ) max = Math.Max(max, Complex.Abs(v));
        // The primitive matrix is exactly symmetric by construction; the Kron solve rounds each entry to a few ulp of
        // the largest primitive entry (measured 1.6e-16 relative in a float port), so 1e-15 of it (the spec's figure)
        // leaves a margin of about six.
        for (int i = 0; i < 3; i++)
            for (int k = 0; k < 3; k++)
                Assert.True(Complex.Abs(r.Line.PhaseZ[i, k] - r.Line.PhaseZ[k, i]) <= 1e-15 * max, $"({i},{k})");
        // Physical check: circulating sheath currents raise the positive-sequence resistance above single-point bonding.
        CableConstantsResult single = CableConstantsSolver.Solve(Flat(armour, earth, CableBonding.SinglePoint));
        Assert.True(r.Line.Sequence!.Z1.Real > single.Line.Sequence!.Z1.Real);
    }

    [Theory]
    [InlineData(CableEarthMethod.Wedepohl)]
    [InlineData(CableEarthMethod.Pollaczek)]
    public void T6b_Ideal_cross_bonding_differs_from_single_point_by_a_rank_one_term(CableEarthMethod earth)
    {
        // Derivation: after the rotation average, each core couples equally to every sheath loop (Z_cs row i is a_i times
        // ones), so the Kron correction Z_cs Z_ss^-1 Z_sc is S a a^T: a rank-one matrix. Single-point bonding is the core
        // block itself, so Z_cross - Z_single = -S a a^T and every 2x2 minor of the difference vanishes. (The spec's
        // "invariant under phase rotation" does not hold for a flat formation: the core-to-core earth mutuals still
        // differ between adjacent and outer cables.) Bound: each entry carries under 1e-14 relative error (Bessel and
        // quadrature) times |Z_prim| / |D| (about 1.2), so a minor is off by under 3e-14 |D|^2; 1e-13 is asserted.
        CableConstantsResult cross = CableConstantsSolver.Solve(Flat(false, earth, CableBonding.CrossBonded));
        CableConstantsResult single = CableConstantsSolver.Solve(Flat(false, earth, CableBonding.SinglePoint));
        var d = new Complex[3, 3];
        double max = 0.0;
        for (int i = 0; i < 3; i++)
            for (int k = 0; k < 3; k++)
            {
                d[i, k] = cross.Line.PhaseZ[i, k] - single.Line.PhaseZ[i, k];
                max = Math.Max(max, Complex.Abs(d[i, k]));
            }
        Assert.True(max > 0.1, "the cross-bonding correction is not trivially zero");
        for (int i = 0; i < 3; i++)
            for (int j = i + 1; j < 3; j++)
                for (int k = 0; k < 3; k++)
                    for (int l = k + 1; l < 3; l++)
                    {
                        double minor = Complex.Abs(d[i, k] * d[j, l] - d[i, l] * d[j, k]);
                        Assert.True(minor <= 1e-13 * max * max, $"minor ({i}{j},{k}{l}) = {minor}");
                    }
    }

    // ---- 7. Independent reference (replaces the published example, which was not available) ------------------------

    public static TheoryData<string, string, bool, double, double, double, double> Reference => new()
    {
        // earth, bonding, armour, Z1 (R, X), Z0 (R, X) in ohm/km: tools/crossval/cables/cable_reference.py (mpmath, 30 digits).
        { "wedepohl", "both", false, 0.06709768460812512, 0.1235281345269901, 0.167917234181138, 0.07520031973264917 },
        { "wedepohl", "single", false, 0.02892402075813799, 0.15069263212973008, 0.17732119247610825, 1.8295782860546517 },
        { "wedepohl", "cross", false, 0.028943078022857373, 0.15057632690624664, 0.16793441492022534, 0.07519926118833312 },
        { "pollaczek", "both", false, 0.06709768496574398, 0.12352813022386326, 0.1679172532212727, 0.07520032419786206 },
        { "pollaczek", "single", false, 0.02892402822465455, 0.15069263170571548, 0.17731805227778336, 1.8295785798781066 },
        { "pollaczek", "cross", false, 0.0289430852241349, 0.15057632642824675, 0.1679344339597627, 0.07519926558362798 },
        { "wedepohl", "both", true, 0.09348128086399503, 0.11254619997921718, 0.11033305930243717, 0.07364264931407144 },
        { "wedepohl", "single", true, 0.030829958682656953, 0.20943060683464282, 0.1792271304006272, 1.8883162607595645 },
        { "wedepohl", "cross", true, 0.030844926968438335, 0.209314993758797, 0.11033762237538045, 0.07363752849147598 },
        { "pollaczek", "both", true, 0.09348127961482207, 0.11254619911762796, 0.11033306537165674, 0.07364264844811198 },
        { "pollaczek", "single", true, 0.030829965853247123, 0.2094306064337142, 0.17922398990637595, 1.8883165546061051 },
        { "pollaczek", "cross", true, 0.030844933875952694, 0.20931499332049677, 0.11033762842440581, 0.07363752760663435 },
    };

    [Theory]
    [MemberData(nameof(Reference))]
    public void T7_Sequence_impedances_match_the_independent_mpmath_reference(string earth, string bonding, bool armour,
        double z1r, double z1x, double z0r, double z0x)
    {
        CableEarthMethod e = earth == "pollaczek" ? CableEarthMethod.Pollaczek : CableEarthMethod.Wedepohl;
        CableBonding b = bonding switch { "both" => CableBonding.BothEnds, "single" => CableBonding.SinglePoint, _ => CableBonding.CrossBonded };
        SequenceConstants s = CableConstantsSolver.Solve(Flat(armour, e, b)).Line.Sequence!;
        // Bound: every primitive entry is within 1e-14 relative (Bessel functions and quadrature, both measured against
        // mpmath); the reduction to Z1 and Z0 loses at most max|Z_prim| / |Z_seq| (under 5.5 for these cases) plus a few
        // ulp of the solve: under 6e-14, and a float port of the same algorithm agreed to 6e-15. 1e-12 is asserted.
        Assert.True(Rel(s.Z1, new Complex(z1r, z1x)) < 1e-12, $"Z1 {s.Z1}");
        Assert.True(Rel(s.Z0, new Complex(z0r, z0x)) < 1e-12, $"Z0 {s.Z0}");
    }

    // ---- Earth return ----------------------------------------------------------------------------------------------

    [Fact]
    public void Wedepohl_agrees_with_Pollaczek_to_the_order_of_its_omitted_terms_and_warns_outside_its_range()
    {
        double omega = 2.0 * Math.PI * 50.0;
        double u = BuriedEarthReturn.WedepohlArgument(omega, 100.0, 2.0, 0.1);
        Complex w = BuriedEarthReturn.ImpedancePerMetre(CableEarthMethod.Wedepohl, omega, 100.0, 1.0, 1.0, 0.1, 0.1);
        Complex p = BuriedEarthReturn.ImpedancePerMetre(CableEarthMethod.Pollaczek, omega, 100.0, 1.0, 1.0, 0.1, 0.1);
        // The omitted terms of the small-argument expansion are of order u^2 ln u.
        Assert.True(Rel(w, p) < u * u * (1.0 + Math.Abs(Math.Log(u))), $"rel {Rel(w, p)} u {u}");
        Assert.Equal(BuriedEarthReturn.WedepohlGamma, Math.Exp(ComplexBessel.EulerGamma), 4 * Eps * 1.8);

        CableConstantsResult high = CableConstantsSolver.Solve(Flat(false, CableEarthMethod.Wedepohl, CableBonding.BothEnds, 1e5, 10.0));
        Assert.True(high.MaxWedepohlArgument > BuriedEarthReturn.WedepohlLimit);
        Assert.Single(high.Warnings);
        Assert.Contains("Pollaczek", high.Warnings[0], StringComparison.Ordinal);
        Assert.Empty(CableConstantsSolver.Solve(Flat(false, CableEarthMethod.Pollaczek, CableBonding.BothEnds, 1e5, 10.0)).Warnings);
        Assert.Empty(CableConstantsSolver.Solve(Flat(false, CableEarthMethod.Wedepohl, CableBonding.BothEnds)).Warnings);
    }

    [Fact]
    public void Unusable_cable_systems_are_refused_by_name()
    {
        var two = new CableGeometry([Demo("A", -0.1, false), Demo("B", 0.1, false)], 50.0, 100.0, CableEarthMethod.Wedepohl, CableBonding.CrossBonded, true);
        Assert.Contains("three cables", Assert.Throws<ArgumentException>(() => CableConstantsSolver.Solve(two)).Message, StringComparison.Ordinal);
        var mixed = new CableGeometry([Demo("A", -0.2, false), Demo("B", 0.0, true), Demo("C", 0.2, false)], 50.0, 100.0, CableEarthMethod.Wedepohl, CableBonding.CrossBonded, true);
        Assert.Throws<ArgumentException>(() => CableConstantsSolver.Solve(mixed));
        var overlap = new CableGeometry([Demo("A", 0.0, false), Demo("B", 0.05, false)], 50.0, 100.0, CableEarthMethod.Wedepohl, CableBonding.BothEnds, true);
        Assert.Contains("overlap", Assert.Throws<ArgumentException>(() => CableConstantsSolver.Solve(overlap)).Message, StringComparison.Ordinal);
        var shallow = new CableGeometry([Demo("A", 0.0, false) with { DepthM = 0.01 }], 50.0, 100.0, CableEarthMethod.Wedepohl, CableBonding.BothEnds, true);
        Assert.Throws<ArgumentException>(() => CableConstantsSolver.Solve(shallow));
        var order = new SingleCoreCable("A", 0.0, 1.0, [new(0.0, 0.0145, 1.72e-8), new(0.0100, 0.0325, 2.83e-8)], [new(2.5), new(2.3)], 0.0365);
        Assert.Throws<ArgumentException>(() => CableConstantsSolver.Solve(new CableGeometry([order], 50.0, 100.0, CableEarthMethod.Wedepohl, CableBonding.BothEnds, true)));
        var noJacket = new SingleCoreCable("A", 0.0, 1.0, [new(0.0, 0.0145, 1.72e-8)], [new(2.5)], 0.0145);
        Assert.Throws<ArgumentException>(() => CableConstantsSolver.Solve(new CableGeometry([noJacket], 50.0, 100.0, CableEarthMethod.Wedepohl, CableBonding.BothEnds, true)));
    }

    // ---- CLI -------------------------------------------------------------------------------------------------------

    private const string CableJson = """
        {"schema":"gridsim-cableconstants/1","earth":"pollaczek","bonding":"both","frequency_hz":50,"earth_resistivity_ohm_m":100,"transposed":true,
         "cables":[
          {"name":"A","x_m":-0.1,"depth_m":1.0,"outer_radius_m":0.0365,"conductors":[
            {"inner_radius_m":0,"outer_radius_m":0.0145,"resistivity_ohm_m":1.72e-8,"insulation":{"relative_permittivity":2.5,"loss_tangent":0.0004}},
            {"inner_radius_m":0.0315,"outer_radius_m":0.0325,"resistivity_ohm_m":2.83e-8,"insulation":{"relative_permittivity":2.3}}]},
          {"name":"B","x_m":0.0,"depth_m":1.0,"outer_radius_m":0.0365,"conductors":[
            {"inner_radius_m":0,"outer_radius_m":0.0145,"resistivity_ohm_m":1.72e-8,"insulation":{"relative_permittivity":2.5,"loss_tangent":0.0004}},
            {"inner_radius_m":0.0315,"outer_radius_m":0.0325,"resistivity_ohm_m":2.83e-8,"insulation":{"relative_permittivity":2.3}}]},
          {"name":"C","x_m":0.1,"depth_m":1.0,"outer_radius_m":0.0365,"conductors":[
            {"inner_radius_m":0,"outer_radius_m":0.0145,"resistivity_ohm_m":1.72e-8,"insulation":{"relative_permittivity":2.5,"loss_tangent":0.0004}},
            {"inner_radius_m":0.0315,"outer_radius_m":0.0325,"resistivity_ohm_m":2.83e-8,"insulation":{"relative_permittivity":2.3}}]}]}
        """;

    private readonly string _dir = TestTemp.Under("gridsim-wp35-" + Guid.NewGuid().ToString("N"));

    public Rc5Wp35CableConstantsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string Write(string name, string text)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllText(p, text);
        return p;
    }

    private static long Bits(JsonElement e) => BitConverter.DoubleToInt64Bits(e.GetDouble());

    [Fact]
    public void Cli_cable_run_matches_the_core_solver_bit_for_bit()
    {
        string f = Write("cables.json", CableJson);
        string o = Path.Combine(_dir, "out.json");
        CliProcess.Result r = CliProcess.RunBuilt("line-constants", "--cable", f, "--length-km", "10", "--base-kv", "132", "--base-mva", "100", "--json", o);
        Assert.True(r.ExitCode == 0, r.ToString());
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(o));
        JsonElement s = doc.RootElement.GetProperty("summary");
        Assert.Equal("gridsim-cableconstants/1", s.GetProperty("schema").GetString());
        Assert.Equal("pollaczek", s.GetProperty("earth").GetString());
        Assert.Equal("both", s.GetProperty("bonding").GetString());

        CableConstantsResult core = CableConstantsSolver.Solve(Flat(false, CableEarthMethod.Pollaczek, CableBonding.BothEnds));
        SequenceConstants q = core.Line.Sequence!;
        Assert.Equal(BitConverter.DoubleToInt64Bits(q.Z1.Real), Bits(s.GetProperty("z1_r_ohm_per_km")));
        Assert.Equal(BitConverter.DoubleToInt64Bits(q.Z1.Imaginary), Bits(s.GetProperty("z1_x_ohm_per_km")));
        Assert.Equal(BitConverter.DoubleToInt64Bits(q.Z0.Real), Bits(s.GetProperty("z0_r_ohm_per_km")));
        Assert.Equal(BitConverter.DoubleToInt64Bits(q.Z0.Imaginary), Bits(s.GetProperty("z0_x_ohm_per_km")));
        Assert.Equal(BitConverter.DoubleToInt64Bits(q.C1FaradPerKm), Bits(s.GetProperty("c1_f_per_km")));
        (double pr, double px, double pb) = core.Line.PositiveSequencePerUnit(10.0, 132.0, 100.0);
        Assert.Equal(BitConverter.DoubleToInt64Bits(pr), Bits(s.GetProperty("r1_pu")));
        Assert.Equal(BitConverter.DoubleToInt64Bits(px), Bits(s.GetProperty("x1_pu")));
        Assert.Equal(BitConverter.DoubleToInt64Bits(pb), Bits(s.GetProperty("b1_pu")));
        JsonElement t = doc.RootElement.GetProperty("tables");
        Assert.Equal(36, t.GetProperty("primitive_z").GetArrayLength());
        Assert.Equal(36, t.GetProperty("conductor_y").GetArrayLength());
        Assert.Equal(9, t.GetProperty("phase_z").GetArrayLength());
        Assert.Equal(9, t.GetProperty("phase_g").GetArrayLength());
        Assert.Contains("Cable constants", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_cable_input_errors_exit_1_and_usage_errors_exit_2()
    {
        string good = Write("good.json", CableJson);
        Assert.Equal(1, CliProcess.RunBuilt("line-constants", "--cable", Path.Combine(_dir, "missing.json")).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("line-constants", "--cable", Write("schema.json", CableJson.Replace("cableconstants/1", "cableconstants/2"))).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("line-constants", "--cable", Write("bond.json", CableJson.Replace("\"both\"", "\"solid\""))).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("line-constants", "--cable", Write("earth.json", CableJson.Replace("\"pollaczek\"", "\"carson\""))).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("line-constants", "--cable", Write("key.json", CableJson.Replace("\"depth_m\":1.0,\"outer", "\"depth_m\":1.0,\"colour\":1,\"outer"))).ExitCode);
        CliProcess.Result overlap = CliProcess.RunBuilt("line-constants", "--cable", Write("overlap.json", CableJson.Replace("\"x_m\":0.1", "\"x_m\":0.01")));
        Assert.Equal(1, overlap.ExitCode);
        Assert.Contains("overlap", overlap.StdErr, StringComparison.Ordinal);

        Assert.Equal(2, CliProcess.RunBuilt("line-constants", "--cable").ExitCode);                                       // no value
        Assert.Equal(2, CliProcess.RunBuilt("line-constants", "--cable", good, good).ExitCode);                           // file and --cable
        Assert.Equal(2, CliProcess.RunBuilt("line-constants", "--cable", good, "--length-km", "10").ExitCode);            // one of three
    }

    [Fact]
    public void Help_names_the_cable_flag()
    {
        CliProcess.Result h = CliProcess.RunBuilt("line-constants", "--help");
        Assert.Equal(0, h.ExitCode);
        Assert.Contains("--cable", h.StdOut, StringComparison.Ordinal);
        Assert.Contains("gridsim-cableconstants/1", h.StdOut, StringComparison.Ordinal);
    }
}
