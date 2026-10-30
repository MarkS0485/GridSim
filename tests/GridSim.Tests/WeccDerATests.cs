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
/// WECC expansion, slice 5: DER_A (Pourbeik, WECC REMTF memo rev. 6/19/19). The partial voltage tripping follows the
/// memo's Appendix pseudo code exactly (the multiplier between vl0 and vl1, the latch after tvl1 with Vmin taken at that
/// instant, the recovery to Vrfrac of the tripped part, the full trip after tvl0); the frequency trip acts after tfl / tfh
/// and is blind below Vpr; the primary frequency response settles the power order at P0 plus the droop exactly (the
/// feedback is the power order, memo item g); the proportional voltage control adds Kqv times the deadbanded error. In
/// WSCC-9 a DER_A unit starts at rest and, after a fault that latches its partial trip, settles at Vrfrac of its power.
/// DYR: 'DER_A' records in the specification's order; PSS/E DERA1 / DERAU1 refused (record order not public).
/// </summary>
public class WeccDerATests
{
    private const double Dt = 0.005;

    private static DeraParameters Der() => new()
    {
        Trv = 0.0, Trf = 0.02, Dbd1 = -0.05, Dbd2 = 0.05, Kqv = 0.0, Vref0 = 0.0, Tp = 0.02, Tiq = 0.02, Ddn = 20.0, Dup = 0.0,
        Fdbd1 = -0.0006, Fdbd2 = 0.0006, Femax = 99.0, Femin = -99.0, Pmax = 1.1, Pmin = 0.0, DPmax = 99.0, DPmin = -99.0, Tpord = 0.02,
        Kpg = 0.1, Kig = 10.0, Imax = 1.2, Vl0 = 0.44, Vl1 = 0.49, Vh0 = 1.2, Vh1 = 1.15, Tvl0 = 0.16, Tvl1 = 0.16, Tvh0 = 0.16, Tvh1 = 0.16,
        Vrfrac = 0.5, Fl = 0.98, Fh = 1.02, Tfl = 0.16, Tfh = 0.16, Tg = 0.02, Rrpwr = 2.0, Tv = 0.0, Vpr = 0.8,
        Pflag = 1, Pqflag = 0, FreqFlag = 0, FtripFlag = 0, VtripFlag = 1, TypeFlag = 1.0, Xe = 0.25, Iqh1 = 1.0, Iql1 = -1.0,
    };

    private static DerAState Unit(DeraParameters p, double p0 = 0.6, double q0 = 0.1, double v0 = 1.0) =>
        new(new RmsConverterUnit(1, "d", RatingPu: 1.0, P0: p0, Q0: q0, V0: v0, new RmsConverterParameters()) { Wecc = WeccPlant.DerA(p) });

    // One step at terminal voltage magnitude v and angle theta.
    private static Complex Drive(DerAState s, double t, double v, double theta = 0.0)
    {
        Complex vt = Complex.FromPolarCoordinates(v, theta);
        Complex i = s.Commit(vt);
        s.Step(t, vt, new[] { vt }, Dt);
        return i;
    }

    public static IEnumerable<object[]> Modes() => new[]
    {
        new object[] { 1, 0, 0, 1, 0.6 },
        new object[] { 0, 1, 1, 0, 0.6 },
        new object[] { 0, 0, 1, 1, -0.4 },   // storage, charging
    };

    [Theory]
    [MemberData(nameof(Modes))]
    public void Every_mode_starts_at_rest(int pflag, int pqflag, int freqFlag, int vtrip, double p0)
    {
        DeraParameters p = Der() with { Pflag = pflag, Pqflag = pqflag, FreqFlag = freqFlag, VtripFlag = vtrip, Kqv = 2.0,
            TypeFlag = p0 < 0 ? 0.0 : 1.0, Pmin = p0 < 0 ? -1.0 : 0.0 };
        DerAState s = Unit(p, p0);
        Complex i0 = Drive(s, 0.0, 1.0);
        Assert.Equal(p0, i0.Real, 15);
        double worst = 0.0;
        for (int k = 1; k < 1000; k++) worst = Math.Max(worst, (Drive(s, k * Dt, 1.0) - i0).Magnitude);
        Assert.True(worst <= 1e-14, $"moved {worst}");
    }

    [Fact]
    public void Partial_trip_latches_after_tvl1_and_recovers_vrfrac_of_the_tripped_part()
    {
        DerAState s = Unit(Der());
        double t = 0.0;
        // 0.47 pu (between vl0 = 0.44 and vl1 = 0.49) for 0.3 s: the multiplier is (V - vl0)/(vl1 - vl0) = 0.6 at once
        // (Trv = Tv = 0), and the latch sets after tvl1 = 0.16 s with Vmin = 0.47.
        for (int k = 0; k < 60; k++, t += Dt) Drive(s, t, 0.47);
        Assert.Equal((0.47 - 0.44) / (0.49 - 0.44), s.TripMultiplier, 12);
        // Back to 1 pu: the recovered multiplier is Vrfrac (vl1 - Vmin)/(vl1 - vl0) + (Vmin - vl0)/(vl1 - vl0) = 0.5 x 0.4 + 0.6.
        for (int k = 0; k < 200; k++, t += Dt) Drive(s, t, 1.0);
        double expected = 0.5 * ((0.49 - 0.47) / (0.49 - 0.44)) + (0.47 - 0.44) / (0.49 - 0.44);
        Assert.Equal(expected, s.TripMultiplier, 12);
        Assert.Equal(expected, s.Multiplier(1.0), 15);
        Complex i = Drive(s, t, 1.0);
        Assert.Equal(expected * 0.6, i.Real, 9);   // the power order stays 0.6; the multiplier scales the current
        Assert.Equal(0.6, s.PowerOrder, 12);
    }

    [Fact]
    public void A_dip_shorter_than_tvl1_recovers_fully_and_a_long_one_below_vl0_trips_for_good()
    {
        DerAState s = Unit(Der());
        double t = 0.0;
        for (int k = 0; k < 20; k++, t += Dt) Drive(s, t, 0.3);     // 0.1 s below vl0: less than tvl0 and tvl1
        for (int k = 0; k < 100; k++, t += Dt) Drive(s, t, 1.0);
        Assert.Equal(1.0, s.TripMultiplier, 12);

        DerAState u = Unit(Der());
        t = 0.0;
        for (int k = 0; k < 40; k++, t += Dt) Drive(u, t, 0.3);     // 0.2 s below vl0: past tvl0 = 0.16 s
        for (int k = 0; k < 100; k++, t += Dt) Drive(u, t, 1.0);
        Assert.Equal(0.0, u.TripMultiplier);
        Assert.Equal(0.0, u.Multiplier(1.0));
    }

    [Fact]
    public void High_voltage_trip_mirrors_the_low_side()
    {
        DerAState s = Unit(Der());
        double t = 0.0;
        for (int k = 0; k < 60; k++, t += Dt) Drive(s, t, 1.17);    // between vh1 = 1.15 and vh0 = 1.2, past tvh1
        Assert.Equal((1.2 - 1.17) / (1.2 - 1.15), s.TripMultiplier, 12);
        for (int k = 0; k < 100; k++, t += Dt) Drive(s, t, 1.0);
        double expected = 0.5 * ((1.17 - 1.15) / (1.2 - 1.15)) + (1.2 - 1.17) / (1.2 - 1.15);
        Assert.Equal(expected, s.TripMultiplier, 12);
    }

    [Fact]
    public void Frequency_trip_acts_after_tfh_and_is_blind_below_vpr()
    {
        double ws = 2.0 * Math.PI * 50.0;
        static double Theta(double ws, double df, double t) => ws * df * t;
        DerAState s = Unit(Der() with { FtripFlag = 1 });
        for (int k = 0; k < 200; k++) Drive(s, k * Dt, 1.0, Theta(ws, 0.03, k * Dt));   // 3 % over-frequency for 1 s
        Assert.True(s.FrequencyTripped);
        for (int k = 200; k < 260; k++) Drive(s, k * Dt, 1.0, Theta(ws, 0.03, k * Dt));
        Complex i = Drive(s, 260 * Dt, 1.0, Theta(ws, 0.03, 260 * Dt));
        Assert.True(i.Magnitude < 1e-3, $"tripped unit still injects {i.Magnitude}");

        DerAState low = Unit(Der() with { FtripFlag = 1, Vl0 = 0.1, Vl1 = 0.2, Vpr = 0.8 });
        for (int k = 0; k < 200; k++) Drive(low, k * Dt, 0.7, Theta(ws, 0.03, k * Dt));  // same frequency at 0.7 pu < Vpr
        Assert.False(low.FrequencyTripped);
    }

    [Fact]
    public void Primary_frequency_response_settles_the_power_order_at_p0_plus_the_droop()
    {
        double ws = 2.0 * Math.PI * 50.0;
        DerAState s = Unit(Der() with { FreqFlag = 1 });
        const double df = 0.005;
        for (int k = 0; k < 4000; k++) Drive(s, k * Dt, 1.0, ws * df * (k * Dt));
        Assert.Equal(1.0 + df, s.FilteredFrequency, 9);
        double expected = 0.6 + 20.0 * (-(df - 0.0006));   // Ddn x db(1 - f)
        Assert.True(Math.Abs(s.PowerOrder - expected) <= 1e-9, $"Pord {s.PowerOrder} vs {expected}");
    }

    [Fact]
    public void Proportional_voltage_control_adds_kqv_times_the_deadbanded_error()
    {
        DerAState s = Unit(Der() with { Kqv = 4.0, Pflag = 0, Vl0 = 0.1, Vl1 = 0.2 }, q0: 0.1);
        double t = 0.0;
        Complex i = default;
        for (int k = 0; k < 400; k++, t += Dt) i = Drive(s, t, 0.9);
        // Vref0 = V0 = 1: Iqv = 4 (1 - 0.9 - 0.05); the constant-Q path gives Q0 / Vt_filt.
        double iq = 0.1 / 0.9 + 4.0 * ((1.0 - 0.9) - 0.05);
        Assert.Equal(iq, -i.Imaginary, 12);
    }

    [Fact]
    public void Starts_it_cannot_hold_are_refused()
    {
        Assert.Throws<InvalidOperationException>(() => Unit(Der(), v0: 0.48));                       // V0 below vl1
        Assert.Throws<InvalidOperationException>(() => Unit(Der() with { Kqv = 1.0, Vref0 = 1.2 }));   // Vref0 - V0 outside the deadband
        Assert.Throws<InvalidOperationException>(() => Unit(Der() with { Pmax = 0.5 }));               // P0 above Pmax
        var ex = Assert.Throws<ArgumentException>(() => Unit(Der() with { Trf = 0.01, Tpord = 0.0, Tp = 0.0, Kig = 0.0 }));
        Assert.Contains("Trf = 0.01", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Kig = 0", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- network

    private static (GridModel M, PowerFlowResult Pf, List<SubtransientInit.MachineData> Data) Case9(DeraParameters p)
    {
        GridModel m = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        var xdp = new Dictionary<int, double> { [1] = 0.0608, [2] = 0.1198, [3] = 0.1813 };
        var data = m.Generators.Select(g =>
        {
            double x = xdp[g.Bus];
            var d = new SubtransientInit.MachineData(Math.Max(g.H, 1.0), 0.0, x, x, x, x, x, x, 8.0, 0.03, 0.4, 0.05);
            return g.Bus == 3 ? d with { Renewable = WeccPlant.DerA(p) } : d;
        }).ToList();
        return (m, pf, data);
    }

    [Fact]
    public void In_wscc9_a_der_a_unit_starts_at_rest_and_settles_at_vrfrac_after_a_latching_fault()
    {
        DeraParameters p = Der() with { Trv = 0.02, Tv = 0.02, Tvl1 = 0.05, Vrfrac = 0.6, Pmax = 1.0 };
        (GridModel m, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Case9(p);
        MultiMachineSubtransient.Result rest = RmsTransientStability.Study(m, pf, data, 3, 50.0, 0.1, stepSeconds: 0.005, durationSeconds: 2.0);
        Assert.True(rest.Converters![0].ActivePowerPu.Max(x => Math.Abs(x - 0.85)) <= 1e-9);
        // A 100 ms bolted fault at the unit's bus: below vl0 for 0.1 s (< tvl0 = 0.16 s, no full trip) but below vl1 for more
        // than tvl1 = 0.05 s, so the partial trip latches with Vmin clamped to vl0: the unit recovers Vrfrac of its output.
        MultiMachineSubtransient.Result r = RmsTransientStability.Study(m, pf, data, 3, 0.1, 0.1, stepSeconds: 0.005, durationSeconds: 10.0);
        Assert.True(r.StayedInSynchronism);
        Assert.Equal(0, r.ConverterIterationFailures);
        Assert.True(Math.Abs(r.Converters![0].ActivePowerPu[^1] - 0.6 * 0.85) <= 1e-3, $"P {r.Converters[0].ActivePowerPu[^1]}");
    }

    [Fact]
    public void Der_a_fault_run_has_the_pinned_digest()
    {
        DeraParameters p = Der() with { Trv = 0.02, Tv = 0.02, Tvl1 = 0.05, Vrfrac = 0.6, Pmax = 1.0, FreqFlag = 1, FtripFlag = 1, Kqv = 2.0 };
        (GridModel m, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Case9(p);
        MultiMachineSubtransient.Result r = RmsTransientStability.Study(m, pf, data, 3, 0.1, 0.1, stepSeconds: 0.005, durationSeconds: 2.0);
        string d = WeccRenewableDigestTests.Digest(r);
        Assert.True(d == "99B68C5ED387D18FB2C8F922D7DF84C8FDF737D34FDC89E0472AA036A6B7FD4F", "digest " + d);
    }

    // ---------------------------------------------------------------- DYR

    internal const string DerRecord = "     2 'DER_A' 1  0.02 0.02 -0.05 0.05 0.0 0.0 0.02 0.02 20.0 0.0 -0.0006 0.0006 99.0 -99.0 1.1 0.0 99.0 -99.0 0.02 0.1 10.0 1.2\n"
        + "   0.44 0.49 1.2 1.15 0.16 0.16 0.16 0.16 0.5 0.98 1.02 0.16 0.16 0.02 2.0 0.02 0.8 1 0 0 0 1 1.0 0.25 1.0 -1.0 /";

    [Fact]
    public void A_der_a_record_maps_in_the_specification_order_and_runs()
    {
        PsseParseResult raw = PsseRawParser.ParseFile(WeccRenewableNetworkTests.Fixture("case14.raw"));
        PsseDyrResult r = PsseDyrReader.Read(WeccRenewableNetworkTests.Dyr(DerRecord), raw, "case14");
        WeccPlant plant = r.Renewables.Single().Value;
        Assert.Equal("DER_A", plant.Chain);
        DeraParameters d = plant.Der!;
        Assert.Equal((0.02, 0.02, -0.05, 0.05, 0.0, 0.0, 0.02, 0.02, 20.0, 0.0), (d.Trv, d.Trf, d.Dbd1, d.Dbd2, d.Kqv, d.Vref0, d.Tp, d.Tiq, d.Ddn, d.Dup));
        Assert.Equal((-0.0006, 0.0006, 99.0, -99.0, 1.1, 0.0, 99.0, -99.0, 0.02, 0.1), (d.Fdbd1, d.Fdbd2, d.Femax, d.Femin, d.Pmax, d.Pmin, d.DPmax, d.DPmin, d.Tpord, d.Kpg));
        Assert.Equal((10.0, 1.2, 0.44, 0.49, 1.2, 1.15, 0.16, 0.16, 0.16, 0.16), (d.Kig, d.Imax, d.Vl0, d.Vl1, d.Vh0, d.Vh1, d.Tvl0, d.Tvl1, d.Tvh0, d.Tvh1));
        Assert.Equal((0.5, 0.98, 1.02, 0.16, 0.16, 0.02, 2.0, 0.02, 0.8), (d.Vrfrac, d.Fl, d.Fh, d.Tfl, d.Tfh, d.Tg, d.Rrpwr, d.Tv, d.Vpr));
        Assert.Equal((1, 0, 0, 0, 1, 1.0, 0.25, 1.0, -1.0), (d.Pflag, d.Pqflag, d.FreqFlag, d.FtripFlag, d.VtripFlag, d.TypeFlag, d.Xe, d.Iqh1, d.Iql1));
        Assert.Contains(r.Notes, n => n.Contains("Xe = 0.25 is the model's numerical network-interface reactance", StringComparison.Ordinal));

        GridModel m = raw.Model;
        var data = m.Generators.Select((g, k) =>
        {
            var md = new SubtransientInit.MachineData(Math.Max(g.H, 3.0), 0.0, 1.6, 0.25, 0.18, 1.55, 0.4, 0.18, 7.0, 0.03, 0.5, 0.05);
            return r.Renewables.TryGetValue(k, out WeccPlant? wp) ? md with { Renewable = wp } : md;
        }).ToList();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        MultiMachineSubtransient.Result rest = RmsTransientStability.Study(m, pf, data, 4, 50.0, 0.1, stepSeconds: 0.005, durationSeconds: 2.0);
        Assert.True(rest.Converters![0].ActivePowerPu.Max(x => Math.Abs(x - 0.4)) <= 1e-9);
    }

    [Theory]
    [InlineData("'DERA1'")]
    [InlineData("'USRMDL' 1 'DERAU1' 101 1 0 48 10 0")]
    public void Psse_der_a_records_are_refused_because_their_order_is_not_public(string head)
    {
        PsseParseResult raw = PsseRawParser.ParseFile(WeccRenewableNetworkTests.Fixture("case14.raw"));
        string rec = head.StartsWith("'USRMDL'", StringComparison.Ordinal)
            ? DerRecord.Replace("'DER_A' 1", head, StringComparison.Ordinal)
            : DerRecord.Replace("'DER_A'", head, StringComparison.Ordinal);
        var ex = Assert.Throws<InvalidDataException>(() => PsseDyrReader.Read(WeccRenewableNetworkTests.Dyr(rec), raw, "case14"));
        Assert.Contains("GridSim models DER_A, but the order of the PSS/E", ex.Message, StringComparison.Ordinal);
    }
}
