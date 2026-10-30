using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using GridSim.Core.Numerics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.Models.Wecc;
using GridSim.Core.IO;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// WECC expansion, slice 7: REGFM_A1, the droop grid-forming inverter (PNNL-35110, September 2023). On a fixed terminal it
/// is at rest; on a terminal turning at an off-nominal frequency it settles exactly on its P-f droop (P = Pref - df / (f0 mp));
/// the fault current limit holds |I| = ImaxF with the unlimited current's angle; in WSCC-9 it starts at rest and rides through
/// a fault deterministically. A study compares, for the same events, a type 4 wind unit with grid-following controls
/// (REGC_A + REEC_A + WTGT_A), REGFM_A1 and GridSim's own virtual synchronous machine.
/// </summary>
public class WeccRegfmTests
{
    private const double Dt = 0.005;
    private readonly ITestOutputHelper _out;

    public WeccRegfmTests(ITestOutputHelper output) => _out = output;

    internal static RegfmA1Parameters Gfm() => new() { Omega0 = 2.0 * Math.PI * 60.0, Pmax = 1.0, Pmin = 0.0, Qmax = 0.6, Qmin = -0.6 };

    private static RegfmA1State Unit(RegfmA1Parameters p, double p0 = 0.6, double q0 = 0.1, double v0 = 1.0) =>
        new(new RmsConverterUnit(1, "g", RatingPu: 1.0, P0: p0, Q0: q0, V0: v0, new RmsConverterParameters { NominalHz = 60.0 }) { Wecc = WeccPlant.RegfmA1(p) });

    private static Complex Drive(RegfmA1State s, double t, Complex v)
    {
        Complex inj = s.Commit(v);
        s.Step(t, v, new[] { v }, Dt);
        return s.TerminalCurrent(inj, v);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 0)]
    public void Regfm_a1_starts_at_rest_in_every_voltage_mode(int vflag, int qvflag)
    {
        RegfmA1State s = Unit(Gfm() with { VFlag = vflag, QVFlag = qvflag });
        Complex i0 = Drive(s, 0.0, Complex.One);
        Assert.Equal(0.6, i0.Real, 13);
        Assert.Equal(-0.1, i0.Imaginary, 13);
        double worst = 0.0;
        for (int k = 1; k < 1000; k++) worst = Math.Max(worst, (Drive(s, k * Dt, Complex.One) - i0).Magnitude);
        Assert.True(worst <= 1e-13, $"moved {worst}");
    }

    [Fact]
    public void On_an_off_nominal_terminal_regfm_a1_settles_exactly_on_its_droop()
    {
        // The terminal turns at df = -0.06 Hz (0.1 %): the unit's internal frequency must follow, so mp (Pref - Pinv) = df / f0
        // and P = P0 - df / (f0 mp) = 0.6 + 0.1.
        RegfmA1State s = Unit(Gfm());
        double df = -0.06, wdev = 2.0 * Math.PI * df;
        Complex i = Complex.Zero;
        for (int k = 0; k < 4000; k++) i = Drive(s, k * Dt, Complex.FromPolarCoordinates(1.0, wdev * k * Dt));
        Complex v = Complex.FromPolarCoordinates(1.0, wdev * 3999 * Dt);
        double p = (v * Complex.Conjugate(i)).Real;
        Assert.True(Math.Abs(p - 0.7) <= 1e-6, $"P {p}");
        Assert.True(Math.Abs(s.FrequencyPu - (1.0 + df / 60.0)) <= 1e-9, $"f {s.FrequencyPu}");
    }

    [Fact]
    public void The_fault_current_limit_holds_imaxf_with_the_unlimited_angle()
    {
        RegfmA1State s = Unit(Gfm() with { ImaxF = 1.5 });
        Drive(s, 0.0, Complex.One);
        var v = new Complex(0.1, 0.0);
        Complex free = (s.DroopSource - v) / new Complex(0.0, 0.15);
        Complex i = s.TerminalCurrent(s.Commit(v), v);
        Assert.Equal(1.5, i.Magnitude, 12);
        Assert.True(Math.Abs(DetMath.Phase(i) - DetMath.Phase(free)) <= 1e-14);
        Assert.True(s.CurrentLimited && s.InDip);
    }

    // ---------------------------------------------------------------- network

    [Fact]
    public void In_wscc9_a_regfm_a1_unit_starts_at_rest_rides_through_a_fault_and_is_deterministic()
    {
        GridModel m = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        List<SubtransientInit.MachineData> data = WeccRegcBReecDTests.Case9Data(m, WeccPlant.RegfmA1(Gfm()));
        MultiMachineSubtransient.Result rest = RmsTransientStability.Study(m, pf, data, 3, 50.0, 0.1, stepSeconds: Dt, durationSeconds: 3.0);
        RmsConverterTrace c = rest.Converters![0];
        Assert.True(c.ActivePowerPu.Max(x => Math.Abs(x - 0.85)) <= 1e-9);
        Assert.True(rest.Machines.Max(mt => mt.SpeedPu.Max(Math.Abs)) <= 1e-9);
        MultiMachineSubtransient.Result r1 = RmsTransientStability.Study(m, pf, data, 9, 0.1, 0.1, stepSeconds: Dt, durationSeconds: 3.0);
        MultiMachineSubtransient.Result r2 = RmsTransientStability.Study(m, pf, data, 9, 0.1, 0.1, stepSeconds: Dt, durationSeconds: 3.0);
        Assert.True(r1.StayedInSynchronism);
        Assert.Equal(0, r1.ConverterIterationFailures);
        Assert.Equal(WeccRenewableDigestTests.Digest(r1), WeccRenewableDigestTests.Digest(r2));
    }

    [Fact]
    public void Regfm_a1_fault_run_has_the_pinned_digest()
    {
        GridModel m = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        MultiMachineSubtransient.Result r = RmsTransientStability.Study(m, pf, WeccRegcBReecDTests.Case9Data(m, WeccPlant.RegfmA1(Gfm())), 9, 0.1, 0.1, stepSeconds: Dt, durationSeconds: 2.0);
        string d = WeccRenewableDigestTests.Digest(r);
        Assert.True(d == "8334B452AEB348494135E4CDF00CB678B2FE0C38E244CB6C8804B870512B5E27", "digest " + d);
    }

    internal const string GfmRecord = "     2 'REGFM_A1' 1  0.15 0.05 0.0 5.86 1.15 0.0 0.01 1.0 0.0 0.01 0.1 376.99111843077515 0.01 0.01 0.01 0.6 -0.6 1 1 3.0 20.0 2.0 /";

    [Fact]
    public void A_regfm_a1_record_maps_in_the_specification_order_and_a_psse_regfma1_record_is_refused()
    {
        PsseParseResult raw = PsseRawParser.ParseFile(WeccRenewableNetworkTests.Fixture("case14.raw"));
        PsseDyrResult r = PsseDyrReader.Read(WeccRenewableNetworkTests.Dyr(GfmRecord), raw, "case14");
        WeccPlant plant = r.Renewables.Single().Value;
        Assert.Equal("REGFM_A1", plant.Chain);
        RegfmA1Parameters g = plant.Gfm!;
        Assert.Equal((0.15, 0.05, 0.0, 5.86, 1.15, 0.0, 0.01, 1.0, 0.0, 0.01, 0.1), (g.XL, g.Mq, g.Kpv, g.Kiv, g.Emax, g.Emin, g.Mp, g.Pmax, g.Pmin, g.Kppmax, g.Kipmax));
        Assert.Equal((376.99111843077515, 0.01, 0.01, 0.01, 0.6, -0.6, 1, 1, 3.0, 20.0, 2.0), (g.Omega0, g.TPf, g.TQf, g.TVf, g.Qmax, g.Qmin, g.VFlag, g.QVFlag, g.Kpqmax, g.Kiqmax, g.ImaxF));
        var ex = Assert.Throws<InvalidDataException>(() => PsseDyrReader.Read(WeccRenewableNetworkTests.Dyr(GfmRecord.Replace("'REGFM_A1'", "'REGFMA1'", StringComparison.Ordinal)), raw, "case14"));
        Assert.Contains("GridSim models REGFM_A1, but the order of the PSS/E", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- grid-following vs grid-forming study

    private static (MultiMachineSubtransient.Result R, double[] H) RunCase9(string kind, IReadOnlyList<RmsEvent> events, double seconds)
    {
        GridModel m = IeeeCases.Case9();
        if (kind == "VSM")
            m = m.CopyWith(generators: m.Generators.Select(g => g.Bus == 3 ? g with { ConverterInterfaced = true, GridForming = true, ConverterFaultCurrentPu = 2.0 } : g).ToArray());
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        WeccPlant? plant = kind switch
        {
            "GFL" => new WeccPlant(new RegcaParameters { Lvplsw = 0 }, new ReecParameters { Qmax = 0.6, Qmin = -0.6, Imax = 1.2, QFlag = 1, VFlag = 1,
                Vq = new[] { 0.0, 0.2, 0.5, 1.0 }, Iq = new[] { 1.2, 1.2, 1.2, 1.2 }, Vp = new[] { 0.0, 0.2, 0.5, 1.0 }, Ip = new[] { 1.2, 1.2, 1.2, 1.2 } })
                { Wind = new WindParameters(new WtgtaParameters(), null, null, null) },
            "GFM" => WeccPlant.RegfmA1(Gfm() with { ImaxF = 1.2 }),
            _ => null,
        };
        List<SubtransientInit.MachineData> data = WeccRegcBReecDTests.Case9Data(m, plant!);
        if (plant is null) data = data.Select((d, k) => m.Generators[k].Bus == 3 ? d with { Renewable = null } : d).ToList();
        double[] h = m.Generators.Where(g => g.Bus != 3).Select(g => Math.Max(g.H, 1.0) * g.MBase / m.BaseMva).ToArray();
        return (RmsTransientStability.StudyEvents(m, pf, data, events, stepSeconds: Dt, durationSeconds: seconds), h);
    }

    [Fact]
    public void Study_grid_following_type_4_wind_vs_regfm_a1_vs_gridsim_vsm_on_the_same_events()
    {
        // Two events on WSCC-9 with the bus 3 unit (85 MW on 128 MVA) as each kind: (1) a 50 MW load step at bus 5 at 0.5 s (a
        // frequency event: the converter's power change in the first step after it, the centre-of-inertia RoCoF over the first
        // 100 ms and 500 ms, and the nadir over 10 s; machines have no governors here, so the frequency falls to the level the
        // converters' own droop and the loads' voltage dependence leave); (2) a 100 ms bolted fault at bus 9 (the machines'
        // angle swing). No published reference: these are GridSim's numbers for Mark's inertia question.
        var sb = new StringBuilder();
        foreach (string kind in new[] { "GFL", "GFM", "VSM" })
        {
            (MultiMachineSubtransient.Result r, double[] h) = RunCase9(kind, new RmsEvent[] { new RmsLoadStep(0.5, 5, 50.0, 0.0) }, 10.0);
            Assert.Equal(0, r.ConverterIterationFailures);
            int n = r.Machines[0].SpeedPu.Count;
            double[] coi = new double[n];
            double hs = h.Sum();
            for (int k = 0; k < n; k++) for (int i = 0; i < h.Length; i++) coi[k] += h[i] * r.Machines[i].SpeedPu[k] / hs;
            int k0 = (int)Math.Round(0.5 / Dt);
            RmsConverterTrace c = r.Converters![0];
            double dp1 = (c.ActivePowerPu[k0] - c.ActivePowerPu[k0 - 1]) * 100.0, dp2 = (c.ActivePowerPu[k0 + 1] - c.ActivePowerPu[k0 - 1]) * 100.0;
            double rocof100 = (coi[k0 + 20] - coi[k0]) * 60.0 / 0.1, rocof500 = (coi[k0 + 100] - coi[k0]) * 60.0 / 0.5;
            double nadir = coi.Min() * 60.0;
            int kn = Array.IndexOf(coi, coi.Min());
            sb.AppendLine(FormattableString.Invariant(
                $"{kind} load step: converter dP at the event step {dp1:F4} MW, after one more step {dp2:F4} MW, at 1 s {(c.ActivePowerPu[k0 + 200] - c.ActivePowerPu[k0 - 1]) * 100.0:F4} MW; COI RoCoF first 100 ms {rocof100:F6} Hz/s, first 500 ms {rocof500:F6} Hz/s; nadir {nadir:F6} Hz below nominal at {kn * Dt:F3} s; at 10 s {coi[^1] * 60.0:F6} Hz"));
        }
        foreach (string kind in new[] { "GFL", "GFM", "VSM" })
        {
            (MultiMachineSubtransient.Result r, _) = RunCase9(kind, new RmsEvent[] { new RmsBusFault(0.1, 9), new RmsFaultClear(0.2, 9) }, 5.0);
            Assert.True(r.StayedInSynchronism);
            double[] d12 = r.Machines[0].DeltaRad.Zip(r.Machines[1].DeltaRad, (a, b) => (b - a) * 180.0 / Math.PI).ToArray();
            RmsConverterTrace c = r.Converters![0];
            double iDuring = Math.Sqrt(c.ActiveCurrentPu[30] * c.ActiveCurrentPu[30] + c.ReactiveCurrentPu[30] * c.ReactiveCurrentPu[30]) / 1.28;
            sb.AppendLine(FormattableString.Invariant(
                $"{kind} fault: machine 2 - machine 1 angle start {d12[0]:F4} deg, max {d12.Max():F4} deg at {Array.IndexOf(d12, d12.Max()) * Dt:F3} s, min {d12.Min():F4} deg, end {d12[^1]:F4} deg; converter |I| at 0.15 s {iDuring:F4} pu of rating, Q {c.ReactivePowerPu[30] * 100.0:F3} Mvar, V {c.TerminalVoltagePu[30]:F4}; iteration failures {r.ConverterIterationFailures}"));
        }
        _out.WriteLine(sb.ToString());
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "gridsim-gfl-gfm-study.txt"), sb.ToString());
    }
}
