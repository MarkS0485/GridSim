using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.Models.Wecc;
using GridSim.Core.IO;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// WECC expansion, slice 4: the wind turbine mechanics (EPRI 3002006525 App. C). The two-mass drive train's discrete map
/// is checked against the hand-derived torsional mode (undamped: eigenvalues 1 and exp(+-j 2 pi Freq1 dt), so
/// trace(Phi) = 1 + 2 cos(2 pi Freq1 dt) and det(Phi) = 1, and a released twist follows theta0 cos(2 pi Freq1 t) to
/// rounding); the aerodynamics, pitch and torque controllers against their diagrams; type 4 and type 3 chains start at
/// rest and ride through a fault in WSCC-9; and the WTDTA1 / WTARA1 / WTPTA1 / WTTQA1 records from a DYR file.
/// </summary>
public class WeccWindTests
{
    private const double Dt = 0.005;

    private static RegcaParameters Regc() => new() { Iqrmax = 9999.0, Iqrmin = -9999.0 };

    private static ReecParameters ReecA(int pflag) => new()
    {
        Variant = ReecVariant.A, PFlag = pflag, VFlag = 1, QFlag = 1, Vdip = 0.9, Vup = 1.1, Kqv = 2.0, Qmax = 0.6, Qmin = -0.6,
        Vmax = 1.1, Vmin = 0.9, Kqi = 0.1, Kvi = 40.0, Pmax = 1.2, Pmin = 0.0, Imax = 1.3,
        Vq = new[] { 0.0, 0.2, 0.5, 1.0 }, Iq = new[] { 1.3, 1.3, 1.3, 1.3 }, Vp = new[] { 0.0, 0.2, 0.5, 1.0 }, Ip = new[] { 1.3, 1.3, 1.3, 1.3 },
    };

    private static WtgtaParameters Shaft() => new() { H = 5.0, Damp = 0.0, Htfrac = 0.9, Freq1 = 1.8, Dshaft = 1.5 };
    private static WtgaraParameters Aero() => new() { Ka = 0.007, Theta0 = 0.0 };
    private static WtgptaParameters PitchP() => new() { Kiw = 25.0, Kpw = 150.0, Kic = 30.0, Kpc = 3.0, Kcc = 0.0, Tp = 0.3, ThetaMax = 27.0, ThetaMin = 0.0, RThetaMax = 10.0, RThetaMin = -10.0 };
    private static WtgtrqaParameters TorqueP() => new() { Tflag = 0, Kpp = 3.0, Kip = 0.6, Tp = 0.05, Twref = 60.0, Temax = 1.2, Temin = 0.0,
        P = new[] { 0.2, 0.4, 0.6, 0.8 }, Spd = new[] { 0.58, 0.72, 0.86, 1.0 }, Trate = 0.0 };

    private static WindParameters Type3() => new(Shaft(), Aero(), PitchP(), TorqueP());

    // ---------------------------------------------------------------- drive train

    [Fact]
    public void Undamped_two_mass_map_has_exactly_the_freq1_torsional_mode()
    {
        WtgtaParameters p = Shaft() with { Dshaft = 0.0, Damp = 0.0 };
        var d = new DriveTrain(p, 1.0, 0.8);
        (double[,] phi, _) = d.StepMatrices(Dt);
        double w = 2.0 * Math.PI * 1.8;
        double trace = phi[0, 0] + phi[1, 1] + phi[2, 2];
        Assert.True(Math.Abs(trace - (1.0 + 2.0 * Math.Cos(w * Dt))) <= 1e-13, $"trace {trace}");
        double det = phi[0, 0] * (phi[1, 1] * phi[2, 2] - phi[1, 2] * phi[2, 1]) - phi[0, 1] * (phi[1, 0] * phi[2, 2] - phi[1, 2] * phi[2, 0])
            + phi[0, 2] * (phi[1, 0] * phi[2, 1] - phi[1, 1] * phi[2, 0]);
        Assert.True(Math.Abs(det - 1.0) <= 1e-13, $"det {det}");
        // K = 2 Ht Hg (2 pi Freq1)^2 / H.
        Assert.Equal(2.0 * 4.5 * 0.5 * w * w / 5.0, d.Stiffness, 12);
    }

    [Fact]
    public void A_released_twist_oscillates_at_freq1_to_rounding()
    {
        WtgtaParameters p = Shaft() with { Dshaft = 0.0, Damp = 0.0 };
        var d = new DriveTrain(p, 1.0, 0.8);
        double theta0 = d.Twist;
        double w = 2.0 * Math.PI * 1.8;
        for (int k = 1; k <= 2000; k++)
        {
            d.Step(0.0, 0.0, Dt);   // both torques removed: the twist rings at the torsional mode about zero
            double exact = theta0 * Math.Cos(w * k * Dt);
            Assert.True(Math.Abs(d.Twist - exact) <= 1e-12 * Math.Abs(theta0) + 1e-16, $"step {k}: {d.Twist} vs {exact}");
        }
    }

    [Fact]
    public void Drive_train_starts_at_rest_with_the_twist_carrying_the_torque()
    {
        var d = new DriveTrain(Shaft(), 1.2, 0.84);
        Assert.Equal(0.84 / 1.2 / d.Stiffness, d.Twist, 15);
        Assert.All(d.Derivatives(0.84, 0.84), x => Assert.True(Math.Abs(x) <= 1e-15));
        for (int k = 0; k < 1000; k++) d.Step(0.84, 0.84, Dt);
        Assert.True(Math.Abs(d.GeneratorSpeed - 1.2) <= 1e-13 && Math.Abs(d.TurbineSpeed - 1.2) <= 1e-13);
        var one = new DriveTrain(Shaft() with { Htfrac = 0.0 }, 1.0, 0.5);
        Assert.Equal(0.0, one.Twist);
        Assert.Equal(one.TurbineSpeed, one.GeneratorSpeed);
    }

    // ---------------------------------------------------------------- aero, pitch, torque

    [Fact]
    public void Aerodynamic_power_is_the_quadratic_of_the_diagram()
    {
        var a = new WtgaraParameters { Ka = 0.007, Theta0 = 2.0 };
        Assert.Equal(0.9, Aerodynamics.MechanicalPower(a, 0.9, 2.0));
        Assert.Equal(0.9 - 0.007 * 10.0 * (10.0 - 2.0), Aerodynamics.MechanicalPower(a, 0.9, 10.0), 15);
    }

    [Fact]
    public void Pitch_moves_at_its_rate_limit_and_stops_at_its_limit()
    {
        var pc = new PitchController(PitchP() with { Tp = 0.0 }, 0.0);
        // Over-speed of 0.1 pu: the speed PI asks for far more pitch than the 10 deg/s rate allows.
        for (int k = 1; k <= 100; k++)
        {
            pc.Advance(1.1, 1.0, 0.5, 0.5, Dt);
            Assert.Equal(Math.Min(10.0 * Dt * k, 27.0), pc.Theta, 12);
        }
        for (int k = 0; k < 1000; k++) pc.Advance(1.1, 1.0, 0.5, 0.5, Dt);
        Assert.Equal(27.0, pc.Theta);   // the upper limit
        pc.Advance(0.9, 1.0, 0.5, 0.5, Dt);
        Assert.True(pc.Theta < 27.0);   // non-windup: it leaves the limit on the first reversed step
    }

    [Fact]
    public void Torque_controller_speed_reference_follows_its_table_and_freezes_in_a_dip()
    {
        WtgtrqaParameters p = TorqueP() with { Twref = 0.0, Tp = 0.0 };
        var tc = new TorqueController(p, 0.5, 0.5 / 0.79);
        Assert.Equal(0.72 + (0.86 - 0.72) * (0.5 - 0.4) / (0.6 - 0.4), tc.SpeedReference, 15);
        double torque0 = tc.Torque(0.5, tc.SpeedReference);
        tc.Advance(0.5, 0.5, 0.95, dip: true, Dt);   // over-speed in a dip: the integral is frozen
        Assert.Equal(torque0 + 3.0 * (0.95 - tc.SpeedReference), tc.Torque(0.5, 0.95), 15);
    }

    // ---------------------------------------------------------------- chains

    private static WeccConverterState Unit(WeccPlant plant, double p0, double q0 = 0.1, double v0 = 1.02) =>
        new(new RmsConverterUnit(1, "w", RatingPu: 1.0, P0: p0, Q0: q0, V0: v0, new RmsConverterParameters()) { Wecc = plant });

    private static double DriveAtRest(WeccConverterState s, double v, int steps)
    {
        Complex i0 = s.Commit(new Complex(v, 0.0));
        s.Step(0.0, new Complex(v, 0.0), new[] { new Complex(v, 0.0) }, Dt);
        double worst = 0.0;
        for (int k = 1; k < steps; k++)
        {
            Complex i = s.Commit(new Complex(v, 0.0));
            s.Step(k * Dt, new Complex(v, 0.0), new[] { new Complex(v, 0.0) }, Dt);
            worst = Math.Max(worst, (i - i0).Magnitude);
        }
        return worst;
    }

    [Fact]
    public void Type4_and_type3_chains_start_at_rest()
    {
        WeccConverterState t4 = Unit(new WeccPlant(Regc(), ReecA(1)) { Wind = new WindParameters(Shaft()) }, 0.8);
        Assert.True(DriveAtRest(t4, 1.02, 2000) <= 1e-13);
        Assert.Equal(1.0, t4.Drive!.GeneratorSpeed, 12);

        WeccConverterState t3 = Unit(new WeccPlant(Regc(), ReecA(1)) { Wind = Type3() }, 0.7);
        double w0 = 0.86 + (1.0 - 0.86) * (0.7 - 0.6) / (0.8 - 0.6);   // the power-speed table at P0 = 0.7
        Assert.Equal(w0, t3.Drive!.GeneratorSpeed, 15);
        Assert.True(DriveAtRest(t3, 1.02, 2000) <= 1e-13);
        Assert.Equal(w0, t3.Drive.GeneratorSpeed, 11);
        Assert.Equal(0.0, t3.Pitch!.Theta, 12);
    }

    [Fact]
    public void Type3_without_pflag_1_and_partial_turbine_sets_are_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() => Unit(new WeccPlant(Regc(), ReecA(0)) { Wind = Type3() }, 0.7));
        Assert.Contains("needs REEC_A PFlag = 1", ex.Message, StringComparison.Ordinal);
        ex = Assert.Throws<ArgumentException>(() => Unit(new WeccPlant(Regc(), ReecA(1)) { Wind = new WindParameters(Shaft(), Aero()) }, 0.7));
        Assert.Contains("a type 3 turbine needs WTGAR_A, WTGPT_A and WTGTRQ_A together", ex.Message, StringComparison.Ordinal);
        ex = Assert.Throws<ArgumentException>(() => Unit(new WeccPlant(Regc(), ReecA(1) with { Variant = ReecVariant.B, PFlag = 0,
            Vq = Array.Empty<double>(), Iq = Array.Empty<double>(), Vp = Array.Empty<double>(), Ip = Array.Empty<double>() }) { Wind = new WindParameters(Shaft()) }, 0.7));
        Assert.Contains("wind turbine mechanics need the REEC_A electrical control", ex.Message, StringComparison.Ordinal);
        var tr = Assert.Throws<InvalidOperationException>(() => Unit(new WeccPlant(Regc(), ReecA(1)) { Wind = Type3() with { Torque = TorqueP() with { Trate = 150.0 } } }, 0.7));
        Assert.Contains("TRATE = 150 MW differs from the unit's MBASE 100 MVA", tr.Message, StringComparison.Ordinal);
    }

    private static readonly Dictionary<int, double> Xdp = new() { [1] = 0.0608, [2] = 0.1198, [3] = 0.1813 };

    private static (GridModel M, PowerFlowResult Pf, List<SubtransientInit.MachineData> Data) Case9(WindParameters wind)
    {
        GridModel m = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        var data = m.Generators.Select(g =>
        {
            double x = Xdp[g.Bus];
            var d = new SubtransientInit.MachineData(Math.Max(g.H, 1.0), 0.0, x, x, x, x, x, x, 8.0, 0.03, 0.4, 0.05);
            return g.Bus == 3 ? d with { Renewable = new WeccPlant(Regc(), ReecA(1)) { Wind = wind } } : d;
        }).ToList();
        return (m, pf, data);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Wind_units_ride_through_a_fault_in_wscc9_and_return_to_their_power(bool type3)
    {
        (GridModel m, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Case9(type3 ? Type3() : new WindParameters(Shaft()));
        MultiMachineSubtransient.Result rest = RmsTransientStability.Study(m, pf, data, 3, 50.0, 0.1, stepSeconds: 0.005, durationSeconds: 2.0);
        Assert.True(rest.Converters![0].ActivePowerPu.Max(p => Math.Abs(p - 0.85)) <= 1e-9);
        MultiMachineSubtransient.Result r = RmsTransientStability.Study(m, pf, data, 3, 0.1, 0.1, stepSeconds: 0.005, durationSeconds: 8.0);
        Assert.True(r.StayedInSynchronism);
        Assert.Equal(0, r.ConverterIterationFailures);
        RmsConverterTrace c = r.Converters![0];
        Assert.Contains(true, c.InDipMode);
        Assert.False(c.InDipMode[^1]);
        Assert.Equal(0.85, c.ActivePowerPu[^1], 2);
    }

    [Fact]
    public void A_type4_unit_with_power_flag_shows_the_torsional_ripple_after_a_fault()
    {
        // With PFlag = 1 the power order is multiplied by the generator speed, so the drive train's torsional mode appears
        // in the electrical power after a nearby fault (EPRI 3002006525 section 3.3, the type 4A behaviour). The ripple's
        // period, measured between successive maxima of the power after clearing, is the drive train's damped mode.
        WtgtaParameters shaft = Shaft() with { Dshaft = 0.2 };
        (GridModel m, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Case9(new WindParameters(shaft));
        MultiMachineSubtransient.Result r = RmsTransientStability.Study(m, pf, data, 3, 0.1, 0.1, stepSeconds: 0.002, durationSeconds: 4.0);
        IReadOnlyList<double> p = r.Converters![0].ActivePowerPu;
        var peaks = new List<int>();
        for (int k = 400; k < p.Count - 1; k++) if (p[k] > p[k - 1] && p[k] >= p[k + 1]) peaks.Add(k);
        Assert.True(peaks.Count >= 3, $"{peaks.Count} peaks");
        double period = (peaks[^1] - peaks[0]) * 0.002 / (peaks.Count - 1);
        Assert.InRange(1.0 / period, 1.8 * 0.9, 1.8 * 1.1);   // within 10 % of Freq1 (damping, controls and the grid shift it slightly)
    }

    // ---------------------------------------------------------------- DYR

    internal const string ReecType3 = "     2 'REECA1' 1  0  0  1  1  1  0\n"
        + "   0.9  1.1  0.02  -0.05  0.05  2.0  1.05  -1.05  0.0  0.0  0.0  0.0  0.05  0.6  -0.6  1.1  0.9  0.0  0.1  0.0  40.0  0.0\n"
        + "   0.02  99.0  -99.0  1.2  0.0  1.3  0.02  0.0 1.3 0.2 1.3 0.5 1.3 1.0 1.3  0.0 1.3 0.2 1.3 0.5 1.3 1.0 1.3 /";
    internal const string Wtdt = "     2 'WTDTA1' 1  5.0  0.0  0.9  1.8  1.5 /";
    internal const string Wtar = "     2 'WTARA1' 1  0.007  0.0 /";
    internal const string Wtpt = "     2 'WTPTA1' 1  25.0  150.0  30.0  3.0  0.0  0.3  27.0  0.0  10.0  -10.0 /";
    internal const string Wttq = "     2 'WTTQA1' 1  0  3.0  0.6  0.05  60.0  1.2  0.0  0.2 0.58 0.4 0.72 0.6 0.86 0.8 1.0  0.0 /";

    [Fact]
    public void Wind_records_map_value_for_value_and_run()
    {
        PsseParseResult raw = PsseRawParser.ParseFile(WeccRenewableNetworkTests.Fixture("case14.raw"));
        PsseDyrResult r = PsseDyrReader.Read(WeccRenewableNetworkTests.Dyr(WeccRenewableNetworkTests.RegcRecord, ReecType3, Wtdt, Wtar, Wtpt, Wttq), raw, "case14");
        WeccPlant plant = r.Renewables.Single().Value;
        Assert.Equal("REGC_A + REEC_A + WTGT_A + WTGAR_A + WTGPT_A + WTGTRQ_A", plant.Chain);
        WindParameters w = plant.Wind!;
        Assert.Equal((5.0, 0.0, 0.9, 1.8, 1.5), (w.DriveTrain.H, w.DriveTrain.Damp, w.DriveTrain.Htfrac, w.DriveTrain.Freq1, w.DriveTrain.Dshaft));
        Assert.Equal((0.007, 0.0), (w.Aero!.Ka, w.Aero.Theta0));
        Assert.Equal((25.0, 150.0, 30.0, 3.0, 0.0, 0.3, 27.0, 0.0, 10.0, -10.0), (w.Pitch!.Kiw, w.Pitch.Kpw, w.Pitch.Kic, w.Pitch.Kpc, w.Pitch.Kcc,
            w.Pitch.Tp, w.Pitch.ThetaMax, w.Pitch.ThetaMin, w.Pitch.RThetaMax, w.Pitch.RThetaMin));
        Assert.Equal((0, 3.0, 0.6, 0.05, 60.0, 1.2, 0.0, 0.0), (w.Torque!.Tflag, w.Torque.Kpp, w.Torque.Kip, w.Torque.Tp, w.Torque.Twref, w.Torque.Temax, w.Torque.Temin, w.Torque.Trate));
        Assert.Equal(new[] { 0.2, 0.4, 0.6, 0.8 }, w.Torque.P);
        Assert.Equal(new[] { 0.58, 0.72, 0.86, 1.0 }, w.Torque.Spd);

        GridModel m = raw.Model;
        var data = m.Generators.Select((g, k) =>
        {
            var d = new SubtransientInit.MachineData(Math.Max(g.H, 3.0), 0.0, 1.6, 0.25, 0.18, 1.55, 0.4, 0.18, 7.0, 0.03, 0.5, 0.05);
            return r.Renewables.TryGetValue(k, out WeccPlant? wp) ? d with { Renewable = wp } : d;
        }).ToList();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        MultiMachineSubtransient.Result rest = RmsTransientStability.Study(m, pf, data, 4, 50.0, 0.1, stepSeconds: 0.005, durationSeconds: 2.0);
        RmsConverterTrace t = Assert.Single(rest.Converters!);
        Assert.True(t.ActivePowerPu.Max(x => Math.Abs(x - 0.4)) <= 1e-9);
    }

    [Fact]
    public void Turbine_records_without_a_drive_train_are_refused()
    {
        PsseParseResult raw = PsseRawParser.ParseFile(WeccRenewableNetworkTests.Fixture("case14.raw"));
        var ex = Assert.Throws<InvalidDataException>(() => PsseDyrReader.Read(WeccRenewableNetworkTests.Dyr(WeccRenewableNetworkTests.RegcRecord, ReecType3, Wtar), raw, "case14"));
        Assert.Contains("WTGAR_A for machine '1' at bus 2 (line 15) has no WTGT_A drive train record for its unit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Type3_fault_run_has_the_pinned_digest()
    {
        // The same digest with default, DOTNET_EnableHWIntrinsic=0 and DOTNET_EnableAVX2=0 proves the matrix exponential and
        // every wind block bit-identical across SIMD tiers.
        (GridModel m, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Case9(Type3());
        MultiMachineSubtransient.Result r = RmsTransientStability.Study(m, pf, data, 3, 0.1, 0.1, stepSeconds: 0.005, durationSeconds: 2.0);
        string d = WeccRenewableDigestTests.Digest(r);
        Assert.True(d == "9696976608807220DAC0EAFAF7A1AE069C5E2DFD9C23688D36F61C553C250EB7", "digest " + d);
    }
}
