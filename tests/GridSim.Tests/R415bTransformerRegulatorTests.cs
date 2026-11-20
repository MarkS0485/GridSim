using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using GridSim.Core.Distribution;
using GridSim.Core.Distribution.Meshed;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-15b: three-phase transformer banks, fixed-tap and line-drop-compensated step-voltage regulators in the meshed phase
/// frame, and the shipped IEEE 4-node transformer cases and the IEEE 123-node feeder through the frame adapter. Every oracle is
/// derived in the test (closed-form phasor and loop equations, hand-solved small linear systems); the IEEE feeders are the
/// published comparison. Published source: IEEE PES Distribution System Analysis Subcommittee test feeders,
/// https://cmte.ieee.org/pes-testfeeders/resources/ (feeder4.zip, feeder123.zip; read 2026-11-17). The page states no licence for
/// the data; no data file is added by this work order, the tables below are the printed node voltages, as the older sweep tests
/// carry them.
/// </summary>
public class R415bTransformerRegulatorTests
{
    private static readonly double Sqrt3 = Math.Sqrt(3.0);
    private static Complex Cis(double deg) => Complex.FromPolarCoordinates(1.0, deg * Math.PI / 180.0);
    private static double Abs(Complex c) => Math.Sqrt(c.Real * c.Real + c.Imaginary * c.Imaginary);
    private static double Deg(Complex c) => Math.Atan2(c.Imaginary, c.Real) * 180.0 / Math.PI;
    private static Complex[] Balanced(double vln) => [vln, vln * Cis(-120), vln * Cis(120)];
    private static readonly string[] Abc = ["a", "b", "c"];

    private static void Near(Complex expected, Complex actual, double tol, string what)
        => Assert.True(Abs(expected - actual) <= tol, $"{what}: expected {expected} but got {actual} (difference {Abs(expected - actual):E3}, tolerance {tol:E1})");

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null);
        return dir!.FullName;
    }

    private static string DataPath(string name) => Path.Combine(Root(), "data", "dist3ph", name);

    // ---- two-bus bank fixture ---------------------------------------------------------------------------------------------

    private static readonly Complex ZPuTotal = new(0.01, 0.06);   // per unit on the unit rating, split equally over the two windings
    private const double UnitKva = 2000.0;

    private static MeshedResult TwoBus(BankConnection c, double kvF, double kvT, Complex[] zLoadEq, bool deltaLoad, Complex? magnetising = null,
        Complex[]? sourceV = null, bool noLoad = false)
    {
        double vLnHv = kvF * 1000.0 / Sqrt3;
        double vLnLv = kvT * 1000.0 / Sqrt3;
        var nodes = new List<FrameNode> { new("S", Abc), new("L", Abc) };
        var src = new FrameSource("S", Abc, sourceV ?? Balanced(vLnHv));
        var loads = new List<FrameLoad>();
        if (!noLoad)
        {
            if (deltaLoad)
            {
                // three delta legs of Zd = 3 Zl each: S = V_LL^2 / conj(Zd), constant impedance
                var s = zLoadEq.Select(z => (vLnLv * Sqrt3) * (vLnLv * Sqrt3) / Complex.Conjugate(3.0 * z)).ToArray();
                loads.Add(FrameLoad.Delta("ld", "L", s, vLnLv * Sqrt3, Zip.ConstantImpedance));
            }
            else
            {
                var s = zLoadEq.Select(z => vLnLv * vLnLv / Complex.Conjugate(z)).ToArray();
                loads.Add(FrameLoad.WyeEarthed("ld", "L", s, vLnLv, Zip.ConstantImpedance));
            }
        }
        BankSpec spec = BankSpec.Split("T", "S", "L", c, kvF, kvT, UnitKva, ZPuTotal.Real * 100, ZPuTotal.Imaginary * 100, magnetising ?? Complex.Zero);
        var frame = new RegulatedFrame("two-bus", nodes, [src], [], loads, banks: [spec]);
        return Newton3Ph.Solve(frame.Build([]), new MeshedOptions { ToleranceAmps = 1e-8, Floating = FloatingPolicy.PinFirstWire });
    }

    private static double ZBaseLvEq(double kvT) { double v = kvT * 1000.0 / Sqrt3; return v * v / (UnitKva * 1000.0); }

    [Theory]
    [InlineData(BankConnection.GroundedWyeGroundedWye, 12.47, 4.16, false, 0.0)]
    [InlineData(BankConnection.DeltaGroundedWye, 12.47, 4.16, false, -30.0)]
    [InlineData(BankConnection.DeltaGroundedWye, 12.47, 24.9, false, 30.0)]
    [InlineData(BankConnection.WyeDelta, 12.47, 4.16, true, -30.0)]
    [InlineData(BankConnection.WyeDelta, 12.47, 24.9, true, 30.0)]
    [InlineData(BankConnection.DeltaDelta, 12.47, 4.16, true, 0.0)]
    public void A_hand_solved_two_bus_bank_gives_the_voltage_ratio_the_30_degree_shift_and_the_line_current(BankConnection c, double kvF, double kvT, bool deltaLoad, double shiftDeg)
    {
        // Oracle: the positive-sequence equivalent per phase. Open-circuit LN voltage k V_A e^{j shift} (k = kV_LL ratio, shift
        // by ANSI: the higher-voltage side leads by 30 degrees), series leakage Zt = z_pu V_LN,lv^2 / S_unit (the total of both
        // windings, seen on the equivalent wye), load Zl per equivalent wye phase (a delta load is 3 Zl per leg).
        double vLnHv = kvF * 1000.0 / Sqrt3;
        double k = kvT / kvF;
        Complex zt = ZPuTotal * ZBaseLvEq(kvT);
        Complex zl = new Complex(1.2, 0.5) * ZBaseLvEq(kvT);
        MeshedResult r = TwoBus(c, kvF, kvT, [zl, zl, zl], deltaLoad);
        Assert.Equal(MeshedStatus.Converged, r.Status);
        Complex[] va = Balanced(vLnHv);
        for (int p = 0; p < 3; p++)
        {
            Complex voc = k * va[p] * Cis(shiftDeg);
            Complex i = voc / (zt + zl);
            Complex vEq = i * zl;
            // a delta reading is the line-line voltage p-(p+1): it leads the equivalent LN voltage of phase p by 30 degrees
            Complex vNode = deltaLoad ? r.V("L", Abc[p]) - r.V("L", Abc[(p + 1) % 3]) : r.V("L", Abc[p]);
            Complex vExpected = deltaLoad ? vEq * Sqrt3 * Cis(30) : vEq;
            Near(vExpected, vNode, 1e-4, $"{c} load voltage phase {Abc[p]}");
            // lossless ideal ratio: I_source = k I e^{-j shift}
            Complex iSrc = r.Sources.Single(q => q.Wire == Abc[p]).Current;
            Near(k * i * Cis(-shiftDeg), iSrc, 1e-5, $"source current phase {Abc[p]}");
        }
        if (!deltaLoad)
        {
            double dAng = Deg(r.V("L", "a")) - Deg(r.V("S", "a"));
            Assert.InRange(dAng, shiftDeg - 5.0, shiftDeg + 5.0);
        }
    }

    [Theory]
    [InlineData(12.47, 4.16, -30.0)]
    [InlineData(4.16, 12.47, 30.0)]
    public void An_unbalanced_delta_grounded_wye_bank_is_solved_limb_by_limb_by_hand(double kvF, double kvT, double shiftDeg)
    {
        // Each secondary limb sees its own delta winding: v_lv = m (V_x - V_y) - Zt I with m = T_wye / T_delta, so
        // I_a = Voc_a / (Zt + Zl_a) per phase exactly. Step-down limbs are A-C, B-A, C-B: line A enters limb a at A and leaves limb b
        // at A, so I_A = m (I_a - I_b). Step-up limbs are A-B, B-C, C-A: I_A = m (I_a - I_c).
        double vLnHv = kvF * 1000.0 / Sqrt3;
        double m = (kvT * 1000.0 / Sqrt3) / (kvF * 1000.0);
        bool down = kvF > kvT;
        Complex zt = ZPuTotal * ZBaseLvEq(kvT);
        Complex[] zl = [new Complex(1.0, 0.3) * ZBaseLvEq(kvT), new Complex(2.0, 0.9) * ZBaseLvEq(kvT), new Complex(0.7, 0.2) * ZBaseLvEq(kvT)];
        MeshedResult r = TwoBus(BankConnection.DeltaGroundedWye, kvF, kvT, zl, false);
        Complex[] vs = Balanced(vLnHv);
        var ia = new Complex[3];
        for (int p = 0; p < 3; p++)
        {
            Complex voc = down ? m * (vs[p] - vs[(p + 2) % 3]) : m * (vs[p] - vs[(p + 1) % 3]);
            ia[p] = voc / (zt + zl[p]);
            Near(ia[p] * zl[p], r.V("L", Abc[p]), 1e-4, $"limb voltage {Abc[p]}");
        }
        for (int p = 0; p < 3; p++)
        {
            Complex expected = down ? m * (ia[p] - ia[(p + 1) % 3]) : m * (ia[p] - ia[(p + 2) % 3]);
            Near(expected, r.Sources.Single(q => q.Wire == Abc[p]).Current, 1e-5, $"delta line current {Abc[p]}");
        }
        Assert.InRange(Deg(r.V("L", "a")) - Deg(r.V("S", "a")), shiftDeg - 8.0, shiftDeg + 8.0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_open_bank_matches_the_hand_solved_two_by_two_system(bool openDeltaPrimary)
    {
        // Two units. Secondary windings ab and bc, ca open: V_ab = Eab - Zw I_ab, V_bc = Ebc - Zw I_bc, V_ca = -(V_ab + V_bc),
        // line currents I_a = I_ab, I_b = I_bc - I_ab, I_c = -I_bc. With a balanced delta load Zd per leg and w = Zw / Zd:
        // [[1+2w, w], [w, 1+2w]] [Vab, Vbc] = [Eab, Ebc]. Eab = n_e V_A (wye primary) or n_e V_AB (delta primary), n_e = T_sec / T_pri.
        double kvF = 12.47, kvT = 4.16;
        double vLnHv = kvF * 1000.0 / Sqrt3, vLnLv = kvT * 1000.0 / Sqrt3;
        BankConnection c = openDeltaPrimary ? BankConnection.OpenDeltaOpenDelta : BankConnection.OpenWyeOpenDelta;
        Complex[] vs = Balanced(vLnHv);
        double tHv = openDeltaPrimary ? kvF * 1000.0 : kvF * 1000.0 / Sqrt3, tLv = kvT * 1000.0;
        double ne = tLv / tHv;
        Complex eab = ne * (openDeltaPrimary ? vs[0] - vs[1] : vs[0]);
        Complex ebc = ne * (openDeltaPrimary ? vs[1] - vs[2] : vs[1]);
        double vLlLv = vLnLv * Sqrt3;
        Complex zw = ZPuTotal * (vLlLv * vLlLv / (UnitKva * 1000.0));      // total of both windings on the delta winding base
        Complex zd = new Complex(1.5, 0.6) * (vLlLv * vLlLv / (UnitKva * 1000.0));
        Complex w = zw / zd;
        Complex a11 = 1.0 + 2.0 * w, a12 = w;
        Complex det = a11 * a11 - a12 * a12;
        Complex vab = (eab * a11 - ebc * a12) / det, vbc = (a11 * ebc - a12 * eab) / det, vca = -(vab + vbc);

        var nodes = new List<FrameNode> { new("S", Abc), new("L", Abc) };
        var s = Enumerable.Repeat(vLlLv * vLlLv / Complex.Conjugate(zd), 3).ToArray();
        BankSpec spec = BankSpec.Split("T", "S", "L", c, kvF, kvT, UnitKva, ZPuTotal.Real * 100, ZPuTotal.Imaginary * 100);
        var frame = new RegulatedFrame("open", nodes, [new FrameSource("S", Abc, vs)], [], [FrameLoad.Delta("ld", "L", s, vLlLv, Zip.ConstantImpedance)], banks: [spec]);
        MeshedResult r = Newton3Ph.Solve(frame.Build([]), new MeshedOptions { ToleranceAmps = 1e-8, Floating = FloatingPolicy.PinFirstWire });
        Assert.Equal(MeshedStatus.Converged, r.Status);
        Near(vab, r.V("L", "a") - r.V("L", "b"), 1e-4, "V_ab");
        Near(vbc, r.V("L", "b") - r.V("L", "c"), 1e-4, "V_bc");
        Near(vca, r.V("L", "c") - r.V("L", "a"), 1e-4, "V_ca");
        Complex iLineA = vab / zd - vca / zd;      // I_a = I_ab,load - I_ca,load = winding current of unit 1
        Complex iLineC = vca / zd - vbc / zd;      // I_c = -I_bc (winding current of unit 2 is -I_c)
        // Primary current of a unit = (T_sec / T_pri) x its secondary winding current.
        Complex iUnit1 = ne * iLineA, iUnit2 = -ne * iLineC;
        Near(iUnit1, r.Sources.Single(q => q.Wire == "a").Current, 1e-5, "I_A");
        if (!openDeltaPrimary)
        {
            Near(iUnit2, r.Sources.Single(q => q.Wire == "b").Current, 1e-5, "I_B");
            Assert.True(Abs(r.Sources.Single(q => q.Wire == "c").Current) < 1e-6, "no current in phase C of an open wye bank");
        }
        else
        {
            // primary delta windings A-B (unit 1) and B-C (unit 2): I_A = i1, I_B = i2 - i1, I_C = -i2
            Near(iUnit2 - iUnit1, r.Sources.Single(q => q.Wire == "b").Current, 1e-5, "I_B");
            Near(-iUnit2, r.Sources.Single(q => q.Wire == "c").Current, 1e-5, "I_C");
        }
    }

    [Fact]
    public void An_open_wye_open_delta_bank_at_no_load_lags_30_degrees_with_the_ratio_sqrt3_kT_over_kF()
    {
        double kvF = 12.47, kvT = 4.16;
        Complex[] vs = Balanced(kvF * 1000.0 / Sqrt3);
        MeshedResult r = TwoBus(BankConnection.OpenWyeOpenDelta, kvF, kvT, [1, 1, 1], false, noLoad: true, sourceV: vs);
        Assert.Equal(MeshedStatus.Converged, r.Status);
        double n = Sqrt3 * kvT / kvF;
        Near(n * vs[0], r.V("L", "a") - r.V("L", "b"), 1e-6, "V_ab = n V_A");
        Near(n * vs[1], r.V("L", "b") - r.V("L", "c"), 1e-6, "V_bc = n V_B");
        Near(-n * (vs[0] + vs[1]), r.V("L", "c") - r.V("L", "a"), 1e-6, "V_ca = -(V_ab + V_bc)");
        // V_AB leads V_A by 30 degrees, so V_ab (in phase with V_A) lags V_AB by 30 degrees: the ANSI step-down.
        Assert.Equal(-30.0, Deg(r.V("L", "a") - r.V("L", "b")) - Deg(vs[0] - vs[1]), 6);
    }

    [Fact]
    public void A_magnetising_branch_is_the_leakage_in_series_with_the_magnetising_admittance_at_no_load()
    {
        // Open secondary: the primary current is V_A / (z1 + 1 / Ym) and the open-circuit secondary is (T2/T1)(V_A - z1 I).
        double kvF = 12.47, kvT = 4.16;
        double vLnHv = kvF * 1000.0 / Sqrt3;
        Complex ym = BankSpec.MagnetisingFromPercent(0.4, 1.5);
        Assert.Equal(0.004, ym.Real, 15);
        Assert.Equal(0.015 * 0.015 - 0.004 * 0.004, ym.Imaginary * ym.Imaginary, 12);
        MeshedResult r = TwoBus(BankConnection.GroundedWyeGroundedWye, kvF, kvT, [1, 1, 1], false, magnetising: ym, noLoad: true);
        Assert.Equal(MeshedStatus.Converged, r.Status);
        double zBase1 = vLnHv * vLnHv / (UnitKva * 1000.0);
        Complex z1 = ZPuTotal / 2.0 * zBase1;
        Complex ymS = ym / zBase1;
        Complex[] vs = Balanced(vLnHv);
        for (int p = 0; p < 3; p++)
        {
            Complex i = vs[p] / (z1 + 1.0 / ymS);
            Near(i, r.Sources.Single(q => q.Wire == Abc[p]).Current, 1e-7, $"no-load current {Abc[p]}");
            Near((kvT / kvF) * (vs[p] - z1 * i), r.V("L", Abc[p]), 1e-6, $"open-circuit secondary {Abc[p]}");
        }
        // Without it the no-load current is exactly zero.
        MeshedResult none = TwoBus(BankConnection.GroundedWyeGroundedWye, kvF, kvT, [1, 1, 1], false, noLoad: true);
        Assert.True(none.Sources.All(q => Abs(q.Current) < 1e-9));
    }

    [Fact]
    public void Bank_specs_that_have_no_defined_shift_or_no_impedance_are_refused_by_name()
    {
        Assert.Throws<ArgumentException>(() => TransformerBanks.Build(BankSpec.Split("T", "S", "L", BankConnection.DeltaGroundedWye, 4.16, 4.16, 1000, 1, 5)));
        Assert.Throws<ArgumentException>(() => TransformerBanks.Build(new BankSpec("T", "S", "L", BankConnection.DeltaDelta, 12.47, 4.16, 1000, Complex.Zero, new Complex(0.01, 0.02))));
        Assert.Throws<ArgumentException>(() => TransformerBanks.Build(BankSpec.Split("T", "S", "L", BankConnection.OpenWyeOpenDelta, 4.16, 12.47, 1000, 1, 5)));
        Assert.Throws<ArgumentException>(() => BankSpec.MagnetisingFromPercent(2.0, 1.0));
    }

    // ---- regulators ------------------------------------------------------------------------------------------------------------

    private static RegulatedFrame RegFrame(StepRegulator reg, Complex[] vSource, FrameLoad? load)
    {
        var nodes = new List<FrameNode> { new("S", Abc), new("L", Abc) };
        var loads = new List<FrameLoad>();
        if (load is not null) loads.Add(load);
        return new RegulatedFrame("reg", nodes, [new FrameSource("S", Abc, vSource)], [], loads, regulators: [reg]);
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(16)]
    public void A_wye_regulator_at_a_fixed_tap_scales_the_voltage_by_one_plus_step_times_tap(int tap)
    {
        // Ideal unit with winding impedance zw on each side: V_to = r V_s / (1 + zw (r^2 + 1) / Zl)   (derived from v1 = E + zw i1,
        // v2 = r E - zw I_load, i1 = r I_load, I_load = V_to / Zl).
        double vln = 7200.0;
        Complex[] vs = Balanced(vln);
        Complex zl = new(4.0, 1.5);
        double zw = 1e-5;
        var reg = new StepRegulator("RG", "S", "L", RegulatorConnection.Wye, Abc, [tap, tap, tap], WindingOhm: zw);
        var load = FrameLoad.WyeEarthed("ld", "L", Enumerable.Repeat(vln * vln / Complex.Conjugate(zl), 3).ToArray(), vln, Zip.ConstantImpedance);
        MeshedResult r = Newton3Ph.Solve(RegFrame(reg, vs, load).Build([[tap, tap, tap]]), new MeshedOptions { ToleranceAmps = 1e-5 });
        Assert.Equal(MeshedStatus.Converged, r.Status);
        double ratio = 1.0 + 0.00625 * tap;
        for (int p = 0; p < 3; p++)
        {
            Complex expected = ratio * vs[p] / (1.0 + zw * (ratio * ratio + 1.0) / zl);
            Near(expected, r.V("L", Abc[p]), 1e-6, $"regulated voltage {Abc[p]}");
            Complex iTo = r.V("L", Abc[p]) / zl;
            Near(ratio * iTo, r.Sources.Single(q => q.Wire == Abc[p]).Current, 1e-6, $"input current {Abc[p]}");
        }
    }

    [Fact]
    public void An_open_delta_regulator_gives_V_ab_and_V_cb_the_ratio_of_its_taps_with_the_hand_derived_line_currents()
    {
        double vln = 7200.0;
        Complex[] vs = Balanced(vln);
        int[] taps = [6, -4];
        double r1 = 1.0 + 0.00625 * taps[0], r2 = 1.0 + 0.00625 * taps[1];
        var reg = new StepRegulator("OD", "S", "L", RegulatorConnection.OpenDelta, Abc, taps, WindingOhm: 1e-6);
        // No load: V_ab = r_ab V_AB, V_cb = r_cb V_CB, line b straight.
        MeshedResult nl = Newton3Ph.Solve(RegFrame(reg, vs, null).Build([taps]), new MeshedOptions { ToleranceAmps = 1e-5 });
        Assert.Equal(MeshedStatus.Converged, nl.Status);
        Near(r1 * (vs[0] - vs[1]), nl.V("L", "a") - nl.V("L", "b"), 1e-6, "V_ab");
        Near(r2 * (vs[2] - vs[1]), nl.V("L", "c") - nl.V("L", "b"), 1e-6, "V_cb");
        Near(vs[1], nl.V("L", "b"), 1e-6, "line b straight through");
        // Loaded (earthed wye, constant impedance): the series windings carry the line currents, so the source currents are
        // I_A = r_ab I_a, I_C = r_cb I_c, I_B = I_b - (r_ab - 1) I_a - (r_cb - 1) I_c.
        Complex zl = new(5.0, 2.0);
        var load = FrameLoad.WyeEarthed("ld", "L", Enumerable.Repeat(vln * vln / Complex.Conjugate(zl), 3).ToArray(), vln, Zip.ConstantImpedance);
        MeshedResult r = Newton3Ph.Solve(RegFrame(reg, vs, load).Build([taps]), new MeshedOptions { ToleranceAmps = 1e-5 });
        Assert.Equal(MeshedStatus.Converged, r.Status);
        Complex ia = r.V("L", "a") / zl, ib = r.V("L", "b") / zl, ic = r.V("L", "c") / zl;
        Near(r1 * ia, r.Sources.Single(q => q.Wire == "a").Current, 1e-5, "I_A");
        Near(r2 * ic, r.Sources.Single(q => q.Wire == "c").Current, 1e-5, "I_C");
        Near(ib - (r1 - 1) * ia - (r2 - 1) * ic, r.Sources.Single(q => q.Wire == "b").Current, 1e-5, "I_B");
        Complex sIn = r.Sources.Aggregate(Complex.Zero, (acc, q) => acc + vs[Array.IndexOf(Abc, q.Wire)] * Complex.Conjugate(q.Current));
        Complex sOut = r.V("L", "a") * Complex.Conjugate(ia) + r.V("L", "b") * Complex.Conjugate(ib) + r.V("L", "c") * Complex.Conjugate(ic);
        Assert.True(Abs(sIn - sOut) < 1e-5 * Abs(sOut), $"power balance {sIn} vs {sOut}");
    }

    [Fact]
    public void A_closed_delta_regulator_is_a_delta_ratio_bank_and_refuses_unequal_taps()
    {
        double vln = 7200.0;
        Complex[] vs = Balanced(vln);
        var reg = new StepRegulator("CD", "S", "L", RegulatorConnection.ClosedDelta, Abc, [4, 4, 4], WindingOhm: 1e-6);
        var frame = RegFrame(reg, vs, null);
        MeshedResult r = Newton3Ph.Solve(frame.Build([[4, 4, 4]]), new MeshedOptions { ToleranceAmps = 1e-5, Floating = FloatingPolicy.PinFirstWire });
        Assert.Equal(MeshedStatus.Converged, r.Status);
        double ratio = 1.025;
        for (int p = 0; p < 3; p++)
            Near(ratio * (vs[p] - vs[(p + 1) % 3]), r.V("L", Abc[p]) - r.V("L", Abc[(p + 1) % 3]), 1e-6, $"V_{Abc[p]}{Abc[(p + 1) % 3]}");
        Assert.Throws<ArgumentException>(() => frame.Build([[4, 4, 5]]));
        Assert.Throws<ArgumentException>(() => StepRegulators.Validate(new StepRegulator("X", "S", "L", RegulatorConnection.Wye, Abc, [1, 1, 99])));
        Assert.Throws<ArgumentException>(() => StepRegulators.Validate(new StepRegulator("X", "S", "L", RegulatorConnection.OpenDelta, Abc, [1, 1],
            Ldc: [new LdcSettings(120, 2, 20, 700, 3, 7.5), new LdcSettings(120, 2, 20, 700, 3, 7.5)])));
    }

    // ---- line-drop-compensator tap search ---------------------------------------------------------------------------------------

    [Fact]
    public void The_tap_search_finds_the_tap_computed_by_hand_from_the_compensator_settings()
    {
        // Regulator straight onto a constant-current load, so the line current does not depend on the tap:
        //   I = (|S| / V0) e^{-j phi} with V0 = 7200 V, 400 A at power factor 0.9 lagging, the source at angle 0.
        // Relay voltage: V_relay(t) = r(t) 7200 / PT - (R + jX) I / CT with PT = 60, CT = 700, R = 3, X = 7.5 (volts), set 123 V,
        // band 2 V: lower edge 122 V. The controller starts at tap 0 and steps up one tap at a time while |V_relay| < 122, so the tap
        // is the smallest t >= 0 with |120 r(t) - D| >= 122, D = (R + jX) I / CT. Solving |120 r - D| = 122 for r:
        //   120 r = Re D + sqrt(122^2 - (Im D)^2).
        double vln = 7200.0, pt = 60.0, ct = 700.0, rSet = 3.0, xSet = 7.5, vSet = 123.0, bw = 2.0;
        double i0 = 400.0, pf = 0.9;
        double phi = Math.Acos(pf);
        Complex iLine = Complex.FromPolarCoordinates(i0, -phi);
        Complex d = new Complex(rSet, xSet) * iLine / ct;
        double lo = vSet - bw / 2;
        double rStar = (d.Real + Math.Sqrt(lo * lo - d.Imaginary * d.Imaginary)) / (vln / pt);
        int expected = (int)Math.Ceiling((rStar - 1.0) / 0.00625 - 1e-12);
        Assert.InRange(expected, 1, 15);

        Complex[] vs = Balanced(vln);
        var load = FrameLoad.WyeEarthed("ld", "L", Enumerable.Repeat(new Complex(vln * i0 * pf, vln * i0 * Math.Sin(phi)), 3).ToArray(), vln, Zip.ConstantCurrent);
        LdcSettings ldc = new(vSet, bw, pt, ct, rSet, xSet);
        var reg = new StepRegulator("RG", "S", "L", RegulatorConnection.Wye, Abc, [0, 0, 0], Ldc: [ldc, ldc, ldc], Ganged: true);
        TapSearchResult res = RegulatorTapSearch.Run(RegFrame(reg, vs, load), new MeshedOptions { ToleranceAmps = 1e-5 });
        Assert.Equal(TapSearchStatus.Settled, res.Status);
        Assert.Equal(new[] { expected, expected, expected }, res.Taps[0]);
        double At(int t) => Abs(vln * (1 + 0.00625 * t) / pt - d);
        Assert.True(At(expected) >= lo && At(expected) <= vSet + bw / 2);
        Assert.True(At(expected - 1) < lo);
        Assert.All(res.Rows, row => Assert.True(row.InBand));
        Assert.Equal(At(expected), res.Rows[0].RelayVolts, 3);
        Assert.Equal(expected + 1, res.OuterIterations);   // one load flow per tap tried, plus the settled one
        TapSearchResult again = RegulatorTapSearch.Run(RegFrame(reg, vs, load), new MeshedOptions { ToleranceAmps = 1e-5 });
        Assert.Equal(res.Trace, again.Trace);
        Assert.Equal(BitConverter.DoubleToInt64Bits(res.Final.V("L", "a").Real), BitConverter.DoubleToInt64Bits(again.Final.V("L", "a").Real));
    }

    [Fact]
    public void A_band_narrower_than_a_tap_step_is_reported_as_hunting_with_the_cycle_named_and_a_tap_limit_stops_the_search()
    {
        double vln = 7200.0, pt = 60.0;
        Complex[] vs = Balanced(vln);
        // No load, R = X = 0: the relay voltage is 120 r(t) = 120, 120.75, ... A set of 120.4 with a 0.2 V band is [120.3, 120.5]:
        // tap 0 is below it, tap 1 (120.75) above it, so the controller would step 0, 1, 0, 1 ...
        LdcSettings narrow = new(120.4, 0.2, pt, 700, 0, 0);
        var reg = new StepRegulator("RG", "S", "L", RegulatorConnection.Wye, ["a"], [0], Ldc: [narrow]);
        var nodes = new List<FrameNode> { new("S", ["a"]), new("L", ["a"]) };
        var frame = new RegulatedFrame("hunt", nodes, [new FrameSource("S", ["a"], [vs[0]])], [], null, regulators: [reg]);
        TapSearchResult res = RegulatorTapSearch.Run(frame, new MeshedOptions { ToleranceAmps = 1e-5 });
        Assert.Equal(TapSearchStatus.Hunting, res.Status);
        Assert.Equal(new[] { "RG=0", "RG=1", "repeat RG=0" }, res.Trace);

        // A set far above anything reachable runs to the tap limit and settles there, flagged AtLimit.
        LdcSettings high = new(140.0, 2.0, pt, 700, 0, 0);
        var reg2 = new StepRegulator("RG", "S", "L", RegulatorConnection.Wye, ["a"], [0], Ldc: [high], MaxTap: 16);
        var frame2 = new RegulatedFrame("limit", nodes, [new FrameSource("S", ["a"], [vs[0]])], [], null, regulators: [reg2]);
        TapSearchResult lim = RegulatorTapSearch.Run(frame2, new MeshedOptions { ToleranceAmps = 1e-5 });
        Assert.Equal(TapSearchStatus.Settled, lim.Status);
        Assert.Equal(16, lim.Taps[0][0]);
        Assert.True(lim.Rows[0].AtLimit);
        Assert.False(lim.Rows[0].InBand);
        TapSearchResult capped = RegulatorTapSearch.Run(frame2, new MeshedOptions { ToleranceAmps = 1e-5 }, maxOuter: 3);
        Assert.Equal(TapSearchStatus.NotSettled, capped.Status);
        Assert.Equal(3, capped.OuterIterations);
    }

    // ---- IEEE feeders through the adapter -----------------------------------------------------------------------------------------

    private static readonly Dictionary<string, (int Bus, bool LineToLine, double[] V, double[] Deg)[]> Published = new()
    {
        // Printed node voltages (volts, degrees), IEEE 4 Node Test Feeder solution tables (feeder4.zip, "IEEE 4 Node Test Feeder
        // Revised Sept. 19, 2006.doc"), read from the document 2026-11-17: Step-Down / Step-Up, balanced / unbalanced loading,
        // columns D-Gr Y, Y-D (ungrounded wye), D-D and Open Gr.Y-D. LL: the document reads delta nodes line to line (V1 = Vab);
        // V1 = Vag for wye nodes. One printing slip: the Y-D step-down balanced node 2 phase a angle is printed "-.03" (a typo for
        // -0.3, as the Gr Y-D column prints), written -0.3 here.
        ["dGy-stepdown-balanced"] = new[] { (2, true, new[] { 12340.0, 12349.0, 12318.0 }, new[] { 29.7, -90.4, 149.6 }), (3, false, new[] { 2249.0, 2263.0, 2259.0 }, new[] { -33.7, -153.4, 86.4 }), (4, false, new[] { 1920.0, 2054.0, 1986.0 }, new[] { -39.1, -158.3, 80.9 }) },
        ["dGy-stepdown-unbalanced"] = new[] { (2, true, new[] { 12350.0, 12314.0, 12333.0 }, new[] { 29.6, -90.4, 149.8 }), (3, false, new[] { 2290.0, 2261.0, 2214.0 }, new[] { -32.4, -153.8, 85.2 }), (4, false, new[] { 2157.0, 1936.0, 1849.0 }, new[] { -34.2, -157, 73.4 }) },
        ["dGy-stepup-balanced"] = new[] { (2, true, new[] { 12361.0, 12372.0, 12348.0 }, new[] { 29.7, -90.4, 149.6 }), (3, false, new[] { 13697.0, 13710.0, 13681.0 }, new[] { 26.7, -93.4, 146.6 }), (4, false, new[] { 13653.0, 13678.0, 13644.0 }, new[] { 26.6, -93.5, 146.5 }) },
        ["dGy-stepup-unbalanced"] = new[] { (2, true, new[] { 12364.0, 12391.0, 12333.0 }, new[] { 29.8, -90.5, 149.6 }), (3, false, new[] { 13792.0, 13733.0, 13641.0 }, new[] { 27.7, -93.5, 145.4 }), (4, false, new[] { 13768.0, 13684.0, 13600.0 }, new[] { 27.7, -93.6, 145.2 }) },
        ["yD-stepdown-balanced"] = new[] { (2, false, new[] { 7112.0, 7133.0, 7124.0 }, new[] { -0.3, -120.4, 119.6 }), (3, true, new[] { 3906.0, 3915.0, 3909.0 }, new[] { -3.4, -123.6, 116.3 }), (4, true, new[] { 3437.0, 3497.0, 3388.0 }, new[] { -7.8, -129.3, 110.6 }) },
        ["yD-stepdown-unbalanced"] = new[] { (2, false, new[] { 7112.0, 7144.0, 7112.0 }, new[] { -0.2, -120.4, 119.5 }), (3, true, new[] { 3896.0, 3972.0, 3874.0 }, new[] { -2.8, -123.8, 115.7 }), (4, true, new[] { 3425.0, 3646.0, 3298.0 }, new[] { -5.8, -130.3, 108.6 }) },
        ["yD-stepup-balanced"] = new[] { (2, false, new[] { 7127.0, 7145.0, 7138.0 }, new[] { -0.3, -120.4, 119.6 }), (3, true, new[] { 23746.0, 23722.0, 23698.0 }, new[] { 56.7, -63.4, 176.7 }), (4, true, new[] { 23681.0, 23664.0, 23625.0 }, new[] { 56.6, -63.6, 176.5 }) },
        ["yD-stepup-unbalanced"] = new[] { (2, false, new[] { 7120.0, 7147.0, 7150.0 }, new[] { -0.4, -120.3, 119.6 }), (3, true, new[] { 23703.0, 24040.0, 23576.0 }, new[] { 57.2, -63.6, 176.1 }), (4, true, new[] { 23637.0, 23995.0, 23495.0 }, new[] { 57.1, -63.8, 175.9 }) },
        ["dD-stepdown-balanced"] = new[] { (2, true, new[] { 12339.0, 12349.0, 12321.0 }, new[] { 29.7, -90.4, 149.6 }), (3, true, new[] { 3911.0, 3914.0, 3905.0 }, new[] { 26.5, -93.6, 146.4 }), (4, true, new[] { 3442.0, 3497.0, 3384.0 }, new[] { 22.3, -99.4, 140.7 }) },
        ["dD-stepdown-unbalanced"] = new[] { (2, true, new[] { 12341.0, 12370.0, 12302.0 }, new[] { 29.8, -90.5, 149.5 }), (3, true, new[] { 3902.0, 3972.0, 3871.0 }, new[] { 27.2, -93.9, 145.7 }), (4, true, new[] { 3431.0, 3647.0, 3294.0 }, new[] { 24.3, -100.4, 138.6 }) },
        ["dD-stepup-balanced"] = new[] { (2, true, new[] { 12361.0, 12372.0, 12348.0 }, new[] { 29.7, -90.4, 149.6 }), (3, true, new[] { 23723.0, 23746.0, 23698.0 }, new[] { 26.7, -93.4, 146.6 }), (4, true, new[] { 23657.0, 23688.0, 23625.0 }, new[] { 26.6, -93.5, 146.5 }) },
        ["dD-stepup-unbalanced"] = new[] { (2, true, new[] { 12362.0, 12392.0, 12334.0 }, new[] { 29.8, -90.4, 149.5 }), (3, true, new[] { 23675.0, 24060.0, 23573.0 }, new[] { 27.2, -93.6, 146 }), (4, true, new[] { 23610.0, 24015.0, 23492.0 }, new[] { 27.2, -93.7, 145.9 }) },
        ["open-stepdown-balanced"] = new[] { (2, false, new[] { 6984.0, 7167.0, 7293.0 }, new[] { 0.4, -121.7, 120.5 }), (3, true, new[] { 3701.0, 4076.0, 3572.0 }, new[] { -0.9, -126.5, 110.9 }), (4, true, new[] { 3384.0, 3804.9, 3246.0 }, new[] { -3.5, -130.2, 106.5 }) },
        ["open-stepdown-unbalanced"] = new[] { (2, false, new[] { 6952.0, 7172.0, 7313.0 }, new[] { 0.7, -122, 120.5 }), (3, true, new[] { 3632.0, 4121.0, 3450.0 }, new[] { 0.1, -127.6, 108.9 }), (4, true, new[] { 3307.0, 3907.0, 3073.0 }, new[] { -1.5, -131.9, 103.1 }) },
    };

    private static MeshedResult SolveFrame(RegulatedFrame frame, bool pin, double tol = 1e-7)
        => Newton3Ph.Solve(frame.Build(frame.InitialTaps()), new MeshedOptions { ToleranceAmps = tol, MaxIterations = 60, Floating = pin ? FloatingPolicy.PinFirstWire : FloatingPolicy.Refuse });

    private static MultiPhaseFeeder LoadOpenOrClosed(string file, bool open, bool balanced)
    {
        if (!open) return MultiPhaseJsonLoader.Load(DataPath(file));
        // As N357OpenBankIeee4NodeTests builds it: the y-d file with "oy-od" and the open-connection loads of the IEEE document.
        string text = File.ReadAllText(DataPath(file)).Replace("\r\n", "\n");
        string closed = balanced ? "\"kw\": [1800, 1800, 1800], \"kvar\": [871.7797887081346, 871.7797887081346, 871.7797887081346]"
            : "\"kw\": [1275, 1800, 2375], \"kvar\": [790.1740314639554, 871.7797887081346, 780.6247497998002]";
        string openLoad = balanced ? "\"kw\": [1200, 1200, 1200], \"kvar\": [581.186525805423, 581.186525805423, 581.186525805423]"
            : "\"kw\": [850, 1200, 1583.33], \"kvar\": [526.7826876426369, 581.186525805423, 520.4154042528495]";
        Assert.Contains(closed, text, StringComparison.Ordinal);
        text = text.Replace("\"connection\": \"y-d\"", "\"connection\": \"oy-od\"", StringComparison.Ordinal).Replace(closed, openLoad, StringComparison.Ordinal);
        return MultiPhaseJsonLoader.Parse(text, "open.json");
    }

    public static IEnumerable<object[]> FourNodeCases()
    {
        foreach (string conn in new[] { "dGy", "yD", "dD" })
            foreach (string dir in new[] { "stepdown", "stepup" })
                foreach (string load in new[] { "balanced", "unbalanced" })
                    yield return [conn, dir, load];
        foreach (string load in new[] { "balanced", "unbalanced" })
            yield return ["open", "stepdown", load];
    }

    private sealed record FourNodeComparison(double FrameVsSweepV, double FrameVsSweepDeg, double FrameVsPubV, double FrameVsPubDeg, int OutsideHalfDigit, int Compared, int FrameWorseThanSweep);

    private static FourNodeComparison CompareFourNode(string conn, string dir, string load)
    {
        bool open = conn == "open";
        string file = open ? $"ieee4-yD-{dir}-{load}.json" : $"ieee4-{conn}-{dir}-{load}.json";
        MultiPhaseFeeder f = LoadOpenOrClosed(file, open, load == "balanced");
        FeederAdapterResult ad = MultiPhaseFeederAdapter.FromFeeder(f);
        Assert.True(ad.Frame is not null, string.Join("; ", ad.Unsupported));
        MeshedResult r = SolveFrame(ad.Frame!, ad.NeedsPinnedReference);
        Assert.True(r.Status == MeshedStatus.Converged, $"{file}: {r.Status} after {r.Iterations} iterations, mismatch {r.MismatchAmps:E2} A; {string.Join("; ", r.IncompleteReasons)}");
        MultiPhaseResult sw = new ThreePhaseSweep().Solve(f.Model);
        Assert.True(sw.Converged);
        double wv = 0, wd = 0, pv = 0, pd = 0;
        int outside = 0, n = 0, worse = 0;
        foreach ((int bus, bool ll, double[] vPub, double[] degPub) in Published[$"{conn}-{dir}-{load}"])
        {
            Complex[] vs = sw.V[sw.BusIds.ToList().IndexOf(bus)];
            Complex[] vf = Abc.Select(w => r.V(MultiPhaseFrameAdapter.Name(bus), w)).ToArray();
            double vb = f.VoltBaseLN(bus);
            for (int p = 0; p < 3; p++)
            {
                Complex s = (ll ? vs[p] - vs[(p + 1) % 3] : vs[p]) * vb;
                Complex fr = ll ? vf[p] - vf[(p + 1) % 3] : vf[p];
                double dvSweep = Abs(s) - vPub[p], dvFrame = Abs(fr) - vPub[p];
                double ddSweep = Math.IEEERemainder(Deg(s) - degPub[p], 360), ddFrame = Math.IEEERemainder(Deg(fr) - degPub[p], 360);
                wv = Math.Max(wv, Math.Abs(Abs(fr) - Abs(s)));
                wd = Math.Max(wd, Math.Abs(Math.IEEERemainder(Deg(fr) - Deg(s), 360)));
                pv = Math.Max(pv, Math.Abs(dvFrame));
                pd = Math.Max(pd, Math.Abs(ddFrame));
                n++;
                if (Math.Abs(dvFrame) > 0.5 || Math.Abs(ddFrame) > 0.05) outside++;
                if (Math.Abs(dvFrame) > Math.Abs(dvSweep) + 0.01 || Math.Abs(ddFrame) > Math.Abs(ddSweep) + 0.002) worse++;
            }
        }
        return new FourNodeComparison(wv, wd, pv, pd, outside, n, worse);
    }

    [Theory]
    [MemberData(nameof(FourNodeCases))]
    public void The_shipped_IEEE_4_node_transformer_cases_run_in_the_meshed_solver_and_agree_with_the_published_tables_as_the_incumbent_does(string conn, string dir, string load)
    {
        FourNodeComparison c = CompareFourNode(conn, dir, load);
        // Frame against the incumbent sweep: the same physics by a different algorithm and a different transformer model (units
        // against per-unit matrices).
        Assert.True(c.FrameVsSweepV < 0.002 && c.FrameVsSweepDeg < 0.0005, $"{conn} {dir} {load}: frame vs sweep {c.FrameVsSweepV:F4} V, {c.FrameVsSweepDeg:F5} deg");
        // Frame against the printed tables: nowhere worse than the sweep's own residual (the documented IEEE table scatter) by more than 0.01 V / 0.002 deg.
        Assert.True(c.FrameWorseThanSweep == 0, $"{conn} {dir} {load}: {c.FrameWorseThanSweep} of {c.Compared} values worse than the sweep; frame vs published worst {c.FrameVsPubV:F3} V, {c.FrameVsPubDeg:F4} deg");
        Assert.True(c.FrameVsPubV < 1.1 && c.FrameVsPubDeg < 0.15, $"{conn} {dir} {load}: frame vs published {c.FrameVsPubV:F3} V, {c.FrameVsPubDeg:F4} deg");
    }

    [Fact]
    public void Four_node_summary_is_recorded_for_the_report()
    {
        var lines = new List<string>();
        foreach (object[] cs in FourNodeCases())
        {
            FourNodeComparison c = CompareFourNode((string)cs[0], (string)cs[1], (string)cs[2]);
            lines.Add($"{cs[0]}-{cs[1]}-{cs[2]}: frame-vs-sweep {c.FrameVsSweepV:F4} V {c.FrameVsSweepDeg:F5} deg; frame-vs-published {c.FrameVsPubV:F3} V {c.FrameVsPubDeg:F4} deg; outside half digit {c.OutsideHalfDigit}/{c.Compared}");
        }
        string path = Environment.GetEnvironmentVariable("R415B_SUMMARY_FILE") ?? "";
        if (path.Length > 0) File.WriteAllLines(path, lines);
        Assert.Equal(14, lines.Count);
    }

    private static MultiPhaseFeeder Ieee123() => MultiPhaseJsonLoader.Load(DataPath("ieee123.json"));

    private static IEnumerable<string[]> Rows(string file) =>
        File.ReadAllLines(DataPath(file)).Where(l => l.Length > 0 && !l.StartsWith('#')).Skip(1).Select(l => l.Split(','));

    private static int BusId(MultiPhaseFeeder f, string node)
    {
        foreach (KeyValuePair<int, string> kv in f.BusNames) if (kv.Value == node) return kv.Key;
        return int.Parse(node, CultureInfo.InvariantCulture);
    }

    private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    [Fact]
    public void The_IEEE_123_node_feeder_with_its_published_taps_matches_the_published_voltages_in_the_meshed_solver()
    {
        MultiPhaseFeeder f = Ieee123();
        FeederAdapterResult ad = MultiPhaseFeederAdapter.FromFeeder(f, new FeederAdapterOptions { SwitchZPu = 1e-6 });
        Assert.True(ad.Frame is not null, string.Join("; ", ad.Unsupported));
        Assert.Equal(4, ad.Frame!.Regulators.Count);
        MeshedResult r = SolveFrame(ad.Frame, false, 1e-6);
        Assert.True(r.Status == MeshedStatus.Converged, $"{r.Status} after {r.Iterations} iterations, mismatch {r.MismatchAmps:E2} A");
        MultiPhaseResult sw = new ThreePhaseSweep().Solve(f.Model);
        double worstMag = 0, worstAng = 0, worstSweepMag = 0, worstSweepAng = 0;
        int compared = 0;
        foreach (string[] row in Rows("ieee123.published-voltages.csv"))
        {
            int id = BusId(f, row[0]);
            int p = "abc".IndexOf(row[1][0], StringComparison.Ordinal);
            Complex v = r.V(MultiPhaseFrameAdapter.Name(id), Abc[p]) / f.VoltBaseLN(id);
            worstMag = Math.Max(worstMag, Math.Abs(Abs(v) - Num(row[2])));
            worstAng = Math.Max(worstAng, Math.Abs(Math.IEEERemainder(Deg(v) - Num(row[3]), 360)));
            Complex vs = sw.V[sw.BusIds.ToList().IndexOf(id)][p];
            worstSweepMag = Math.Max(worstSweepMag, Math.Abs(Abs(v) - Abs(vs)));
            worstSweepAng = Math.Max(worstSweepAng, Math.Abs(Math.IEEERemainder(Deg(v) - Deg(vs), 360)));
            compared++;
        }
        Assert.Equal(268, compared);
        string path = Environment.GetEnvironmentVariable("R415B_SUMMARY_FILE123V") ?? "";
        if (path.Length > 0) File.WriteAllLines(path, new[] { $"frame vs published {worstMag:E4} pu {worstAng:F4} deg; frame vs sweep {worstSweepMag:E4} pu {worstSweepAng:F5} deg; newton iterations {r.Iterations} mismatch {r.MismatchAmps:E3} A" });
        // The incumbent test's own bounds against the printed profile: 6e-4 pu and 0.06 degree (ideal switches there; a 1e-6 pu switch here).
        Assert.True(worstMag < 6e-4 && worstAng < 0.06, $"frame vs published: {worstMag:E3} pu, {worstAng:F4} deg");
        Assert.True(worstSweepMag < 2e-5 && worstSweepAng < 0.002, $"frame vs sweep: {worstSweepMag:E3} pu, {worstSweepAng:F5} deg");
    }

    private static Dictionary<string, RegulatorControlSpec> Ieee123Control()
    {
        // Compensator data from the source document (Regulator Data.xls and the regulator block of the letterhead document):
        // RG1 3-ph wye ganged, phase A monitored, 120 V, band 2 V, PT 20, CT 700, R 3, X 7.5; RG2 phase A, 120 V, band 2 V, CT 50,
        // R = X = 0.4; RG3 phases A and C, band 1 V, CT 50, R = X = 0.4; RG4 phases A, B, C, 124 V, band 2 V, CT 300,
        // R (0.6, 1.4, 0.2), X (1.3, 2.6, 1.4).
        LdcSettings Ldc(double set, double bw, double ct, double r, double x) => new(set, bw, 20.0, ct, r, x);
        return new Dictionary<string, RegulatorControlSpec>
        {
            ["RG1"] = new([Ldc(120, 2, 700, 3, 7.5), Ldc(120, 2, 700, 3, 7.5), Ldc(120, 2, 700, 3, 7.5)], true),
            ["RG2"] = new([Ldc(120, 2, 50, 0.4, 0.4)], false),
            ["RG3"] = new([Ldc(120, 1, 50, 0.4, 0.4), Ldc(120, 1, 50, 0.4, 0.4)], false),
            ["RG4"] = new([Ldc(124, 2, 300, 0.6, 1.3), Ldc(124, 2, 300, 1.4, 2.6), Ldc(124, 2, 300, 0.2, 1.4)], false),
        };
    }

    [Fact]
    public void The_line_drop_compensator_search_runs_on_the_IEEE_123_node_feeder_from_tap_zero_and_is_deterministic()
    {
        MultiPhaseFeeder f = Ieee123();
        FeederAdapterResult ad = MultiPhaseFeederAdapter.FromFeeder(f, new FeederAdapterOptions { SwitchZPu = 1e-6, Control = Ieee123Control() });
        Assert.True(ad.Frame is not null, string.Join("; ", ad.Unsupported));
        RegulatedFrame start = ad.Frame!.WithInitialTaps(ad.Frame.Regulators.Select(r => new int[r.Phases.Count]).ToArray());
        TapSearchResult a = RegulatorTapSearch.Run(start, new MeshedOptions { ToleranceAmps = 1e-6, MaxIterations = 60 });
        TapSearchResult b = RegulatorTapSearch.Run(start, new MeshedOptions { ToleranceAmps = 1e-6, MaxIterations = 60 });
        Assert.Equal(a.Status, b.Status);
        Assert.Equal(a.Trace, b.Trace);
        // Where the printed taps stand against the compensator band, by this relay definition (an observation, recorded for the report).
        TapSearchResult atPublished = RegulatorTapSearch.Evaluate(ad.Frame, new MeshedOptions { ToleranceAmps = 1e-6, MaxIterations = 60 });
        string path2 = Environment.GetEnvironmentVariable("R415B_SUMMARY_FILE123P") ?? "";
        if (path2.Length > 0)
            File.WriteAllLines(path2, new[] { $"status at published taps {atPublished.Status}" }
                .Concat(atPublished.Rows.Select(x => $"{x.Regulator}.{x.Phase} tap {x.Tap} relay {x.RelayVolts:F3} band [{x.LowVolts:F2},{x.HighVolts:F2}] inband {x.InBand}")));
        Assert.Equal(a.Taps.SelectMany(t => t), b.Taps.SelectMany(t => t));
        string path = Environment.GetEnvironmentVariable("R415B_SUMMARY_FILE123") ?? "";
        if (path.Length > 0)
            File.WriteAllLines(path, new[] { $"status {a.Status} outer {a.OuterIterations}", "taps " + string.Join(" ", a.Taps.Select((t, i) => $"{start.Regulators[i].Name}=" + string.Join("/", t))), "published RG1=7/7/7 RG2=-1 RG3=0/-1 RG4=8/1/5" }
                .Concat(a.Rows.Select(x => $"{x.Regulator}.{x.Phase} tap {x.Tap} relay {x.RelayVolts:F3} band [{x.LowVolts:F2},{x.HighVolts:F2}] inband {x.InBand} limit {x.AtLimit}")).Concat(a.Trace.Select(t => "trace " + t)));
    }

    [Fact]
    public void The_R4_15_adapter_still_names_what_it_does_not_carry_and_the_new_one_names_a_closed_switch()
    {
        MultiPhaseFeeder f = Ieee123();
        FrameAdapterResult old = MultiPhaseFrameAdapter.FromMultiPhase(f.Model, f.VoltBaseLN(1), f.BaseMva * 1e6 / 3.0, 1e-6);
        Assert.Null(old.Model);
        Assert.Contains(old.Unsupported, s => s.Contains("regulator", StringComparison.Ordinal));
        FeederAdapterResult no = MultiPhaseFeederAdapter.FromFeeder(f);
        Assert.Null(no.Frame);
        Assert.Contains(no.Unsupported, s => s.Contains("closed switch", StringComparison.Ordinal));
    }
}
