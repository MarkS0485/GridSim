using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.Models.Wecc;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// WECC expansion, slice 2: the REPC_A plant controller (EPRI 3002006525 Fig. 3-5). A unit fed by a Thevenin source
/// (E behind jX, the converter current injected at the node: V = E + jX I, solved to 1e-14 each step) shows the closed
/// loops against exact steady states worked out here: integral voltage control returns the regulated voltage to its
/// reference; with reactive droop Kc the settled voltage sits at Vref - Kc (Q - Q0); line drop compensation sets
/// Vref = |V0 - (Rc + jXc) I0|; primary frequency response settles the plant at P0 + Ddn x the deadbanded over-frequency;
/// the reactive integral freezes below Vfrz. In the network: WSCC-9 with the bus-3 unit regulating its POI (bus 6) and
/// measuring the 3-6 branch flow, with the POI retained as a measurement node; and REPCA1 records from a DYR file.
/// </summary>
public class WeccPlantControllerTests
{
    private const double Dt = 0.005;
    private const double X = 0.2;   // Thevenin reactance (system pu)

    private static RegcaParameters Regc() => new() { Tg = 0.02, Rrpwr = 10.0, Iqrmax = 9999.0, Iqrmin = -9999.0 };

    private static ReecParameters Reec() => new()
    {
        Variant = ReecVariant.A, PfFlag = 0, VFlag = 1, QFlag = 1, Vdip = 0.8, Vup = 1.2, Trv = 0.02, Kqv = 0.0,
        Qmax = 0.6, Qmin = -0.6, Vmax = 1.2, Vmin = 0.8, Kqp = 0.0, Kqi = 1.0, Kvp = 0.0, Kvi = 40.0, Tiq = 0.02,
        Pmax = 1.0, Pmin = 0.0, Imax = 1.5, Tpord = 0.02, Tp = 0.05,
        Vq = new[] { 0.0, 0.2, 0.5, 1.0 }, Iq = new[] { 1.5, 1.5, 1.5, 1.5 }, Vp = new[] { 0.0, 0.2, 0.5, 1.0 }, Ip = new[] { 1.5, 1.5, 1.5, 1.5 },
    };

    private static RepcaParameters Repc() => new()
    {
        RefFlag = 1, VcFlag = 0, Fflag = 0, Tfltr = 0.02, Kp = 2.0, Ki = 10.0, Tft = 0.0, Tfv = 0.05, Vfrz = 0.0,
        Emax = 0.5, Emin = -0.5, Dbd1 = 0.0, Dbd2 = 0.0, Qmax = 0.6, Qmin = -0.6, Kpg = 0.5, Kig = 2.0, Tp = 0.05,
        Fdbd1 = -0.004, Fdbd2 = 0.004, Femax = 999.0, Femin = -999.0, Pmax = 1.0, Pmin = 0.0, Tg = 0.05, Ddn = 20.0, Dup = 0.0,
    };

    private sealed class Thevenin
    {
        public readonly WeccConverterState S;
        public Complex E, V;
        public Thevenin(WeccPlant plant, double p0 = 0.6, double q0 = 0.1, double v0 = 1.0)
        {
            var unit = new RmsConverterUnit(1, "u", RatingPu: 1.0, P0: p0, Q0: q0, V0: v0, new RmsConverterParameters()) { Wecc = plant };
            S = new WeccConverterState(unit);
            Complex i0 = Complex.Conjugate(new Complex(p0, q0) / v0);
            E = v0 - new Complex(0.0, X) * i0;   // V = E + jX I
            V = v0;
        }

        // One step: solve the node voltage against the converter's current, commit, advance the controls.
        public void Step(double t)
        {
            for (int it = 0; it < 200; it++)
            {
                Complex vn = E + new Complex(0.0, X) * S.TrialCurrent(V);
                bool done = (vn - V).Magnitude < 1e-14;
                V = vn;
                if (done) break;
            }
            S.Commit(V);
            S.Step(t, V, new[] { V }, Dt);
        }
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(1, 1, 0)]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 1)]
    [InlineData(0, 1, 1)]
    public void Every_mode_starts_at_rest(int refFlag, int vcFlag, int fflag)
    {
        var th = new Thevenin(new WeccPlant(Regc(), Reec()) { Repc = Repc() with { RefFlag = refFlag, VcFlag = vcFlag, Fflag = fflag, Rc = 0.01, Xc = 0.05, Kc = 0.02 } });
        Complex v0 = th.V;
        double worst = 0.0;
        for (int k = 0; k < 600; k++) { th.Step(k * Dt); worst = Math.Max(worst, (th.V - v0).Magnitude); }
        Assert.True(worst <= 1e-12, $"moved {worst}");
        Assert.Equal(1.0, th.S.MeasuredFrequency, 12);
    }

    [Fact]
    public void Integral_voltage_control_returns_the_regulated_voltage_to_its_reference()
    {
        var th = new Thevenin(new WeccPlant(Regc(), Reec()) { Repc = Repc() });
        double vref = th.S.PlantController!.Vref;
        Assert.Equal(1.0, vref, 15);
        th.E *= 0.97;   // the grid voltage drops 3 %
        for (int k = 0; k < 4000; k++) th.Step(k * Dt);
        Assert.True(Math.Abs(th.V.Magnitude - vref) <= 1e-6, $"V settled at {th.V.Magnitude}, reference {vref}");
        Assert.True(th.S.MeasuredPower.Q > 0.1 + 0.1, $"Q {th.S.MeasuredPower.Q} should rise to support the voltage");
    }

    [Fact]
    public void Reactive_droop_settles_the_voltage_at_vref_minus_kc_times_the_q_change()
    {
        const double kc = 0.05;
        var th = new Thevenin(new WeccPlant(Regc(), Reec()) { Repc = Repc() with { Kc = kc } });
        double vref = th.S.PlantController!.Vref;
        Assert.Equal(1.0 + kc * 0.1, vref, 15);   // Vref = V0 + Kc Q0
        th.E *= 0.97;
        for (int k = 0; k < 4000; k++) th.Step(k * Dt);
        double q = th.S.MeasuredPower.Q;
        Assert.True(Math.Abs(th.V.Magnitude + kc * q - vref) <= 1e-6, $"V {th.V.Magnitude} + Kc Q {kc * q} vs Vref {vref}");
        Assert.True(th.V.Magnitude < 1.0 - 1e-4, "with droop the voltage settles below its initial value");
    }

    [Fact]
    public void Line_drop_compensation_takes_vref_from_the_compensated_initial_voltage()
    {
        const double rc = 0.01, xc = 0.05;
        var th = new Thevenin(new WeccPlant(Regc(), Reec()) { Repc = Repc() with { VcFlag = 1, Rc = rc, Xc = xc } });
        Complex i0 = Complex.Conjugate(new Complex(0.6, 0.1) / 1.0);
        Assert.Equal((1.0 - new Complex(rc, xc) * i0).Magnitude, th.S.PlantController!.Vref, 15);
    }

    [Fact]
    public void Primary_frequency_response_settles_at_p0_plus_ddn_times_the_deadbanded_deviation()
    {
        var th = new Thevenin(new WeccPlant(Regc(), Reec()) { Repc = Repc() with { Fflag = 1, Kp = 0.0, Ki = 0.0 } });
        const double df = 0.01;              // 1 % over-frequency
        double ws = 2.0 * Math.PI * 50.0;
        Complex e0 = th.E;
        for (int k = 0; k < 8000; k++)
        {
            th.E = e0 * Complex.FromPolarCoordinates(1.0, ws * df * (k * Dt));   // the grid rotates at 1 + df
            th.Step(k * Dt);
        }
        Assert.Equal(1.0 + df, th.S.MeasuredFrequency, 9);
        double droop = 20.0 * (-(df - 0.004));   // Ddn x db(1 - f): below fdbd1 = -0.004
        Assert.True(Math.Abs(th.S.MeasuredPower.P - (0.6 + droop)) <= 1e-6, $"P {th.S.MeasuredPower.P}, expected {0.6 + droop}");
    }

    [Fact]
    public void Reactive_integral_freezes_below_vfrz()
    {
        // REEC on constant Q (QFlag = 0): the unit follows Qext and does not lift the voltage itself.
        var th = new Thevenin(new WeccPlant(Regc(), Reec() with { Vdip = 0.5, QFlag = 0 }) { Repc = Repc() with { Kp = 0.0, Vfrz = 0.95 } });
        double s20 = th.S.PlantController!.ReactiveIntegral;
        th.E *= 0.9;   // regulated voltage below Vfrz: the integral may not move
        for (int k = 0; k < 400; k++) th.Step(k * Dt);
        Assert.True(th.V.Magnitude < 0.95);
        Assert.Equal(s20, th.S.PlantController.ReactiveIntegral);   // frozen exactly
        th.E /= 0.9;   // back above Vfrz: it integrates again
        th.E *= 0.97;
        for (int k = 400; k < 600; k++) th.Step(k * Dt);
        Assert.NotEqual(s20, th.S.PlantController.ReactiveIntegral);
    }

    // ---------------------------------------------------------------- frequency measurement filter (D15)

    [Fact]
    public void The_frequency_filter_defaults_to_20_ms_and_must_be_a_finite_number_at_least_zero()
    {
        Assert.Equal(0.02, RepcaParameters.DefaultFrequencyFilterSeconds);
        Assert.Equal(0.02, new RepcaParameters().FrequencyFilterSeconds);
        Assert.Empty(Repc().Check());
        Assert.Empty((Repc() with { FrequencyFilterSeconds = 0.0 }).Check());
        foreach (double bad in new[] { -0.01, double.NaN, double.PositiveInfinity })
            Assert.Contains((Repc() with { FrequencyFilterSeconds = bad }).Check(), s => s.StartsWith("REPC_A FrequencyFilterSeconds", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.05)]
    [InlineData(0.5)]
    public void The_frequency_filter_moves_nothing_at_rest_bit_for_bit(double tf)
    {
        // At rest the angle estimate is constant, so any filter time constant gives the same run, bit for bit, as the default.
        (GridModel m, PowerFlowResult pf, List<SubtransientInit.MachineData> d0) = Case9(PoiControl() with { Fflag = 1 });
        (_, _, List<SubtransientInit.MachineData> d1) = Case9(PoiControl() with { Fflag = 1, FrequencyFilterSeconds = tf });
        MultiMachineSubtransient.Result r0 = RmsTransientStability.Study(m, pf, d0, 7, 50.0, 0.1, stepSeconds: 0.005, durationSeconds: 2.0);
        MultiMachineSubtransient.Result r1 = RmsTransientStability.Study(m, pf, d1, 7, 50.0, 0.1, stepSeconds: 0.005, durationSeconds: 2.0);
        Assert.Equal(WeccRenewableDigestTests.Digest(r0), WeccRenewableDigestTests.Digest(r1));
    }

    [Fact]
    public void The_frequency_filter_leaves_the_off_nominal_steady_state_and_shapes_only_the_way_there()
    {
        // The grid turns at 1 % over-frequency: after the transient the measured frequency and the droop power are the same
        // for a 20 ms and a 200 ms filter; while the change arrives the measurements differ (the parameter acts).
        double ws = 2.0 * Math.PI * 50.0;
        (double F, double P, double F10) Run(double tf)
        {
            var th = new Thevenin(new WeccPlant(Regc(), Reec()) { Repc = Repc() with { Fflag = 1, Kp = 0.0, Ki = 0.0, FrequencyFilterSeconds = tf } });
            Complex e0 = th.E;
            double f10 = 0.0;
            for (int k = 0; k < 8000; k++)
            {
                th.E = e0 * Complex.FromPolarCoordinates(1.0, ws * 0.01 * (k * Dt));
                th.Step(k * Dt);
                if (k == 10) f10 = th.S.MeasuredFrequency;
            }
            return (th.S.MeasuredFrequency, th.S.MeasuredPower.P, f10);
        }
        (double fa, double pa, double fa10) = Run(0.02);
        (double fb, double pb, double fb10) = Run(0.2);
        Assert.Equal(1.01, fa, 9);
        Assert.Equal(fa, fb, 9);
        Assert.True(Math.Abs(pa - pb) <= 1e-9, $"P {pa} vs {pb}");
        Assert.True(fa10 - 1.0 > fb10 - 1.0 + 1e-3, $"after 10 steps the 20 ms filter should be further on: {fa10} vs {fb10}");
    }

    [Fact]
    public void A_start_outside_the_plant_limits_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => new Thevenin(new WeccPlant(Regc(), Reec()) { Repc = Repc() with { Qmax = 0.05 } }));
        Assert.Throws<InvalidOperationException>(() => new Thevenin(new WeccPlant(Regc(), Reec()) { Repc = Repc() with { Fflag = 1, Pmax = 0.5 } }));
        var ex = Assert.Throws<ArgumentException>(() => new Thevenin(new WeccPlant(Regc()) { Repc = Repc() }));
        Assert.Contains("REPC_A needs an electrical control model", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- network

    private static readonly Dictionary<int, double> Xdp = new() { [1] = 0.0608, [2] = 0.1198, [3] = 0.1813 };

    private static (GridModel M, PowerFlowResult Pf, List<SubtransientInit.MachineData> Data) Case9(RepcaParameters repc)
    {
        GridModel m = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        var data = m.Generators.Select(g =>
        {
            double x = Xdp[g.Bus];
            var d = new SubtransientInit.MachineData(Math.Max(g.H, 1.0), 0.0, x, x, x, x, x, x, 8.0, 0.03, 0.4, 0.05);
            return g.Bus == 3 ? d with { Renewable = new WeccPlant(Regc(), Reec() with { Imax = 1.3, Vdip = 0.9, Vup = 1.1, Vmin = 0.9, Vmax = 1.1, Kqv = 2.0 }) { Repc = repc } } : d;
        }).ToList();
        return (m, pf, data);
    }

    private static RepcaParameters PoiControl() => Repc() with { RegulatedBus = 6, FlowFromBus = 3, FlowToBus = 6 };

    [Fact]
    public void The_poi_is_retained_as_a_measurement_node_and_the_run_starts_at_rest()
    {
        (GridModel m, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Case9(PoiControl());
        SubtransientInit.Prepared p = SubtransientInit.FromSolvedCase(m, pf, data);
        RmsConverterUnit u = Assert.Single(p.ConverterUnits);
        Assert.Equal(new[] { 3, 6 }, RmsConverterUnit.NodeBuses(p.ConverterUnits));
        Assert.Equal(2, p.Network!.Converters);
        Assert.Equal(6, u.WeccMeasurement!.RegulatedBus);

        MultiMachineSubtransient.Result r = RmsTransientStability.Study(m, pf, data, 7, 50.0, 0.1, stepSeconds: 0.005, durationSeconds: 3.0);
        RmsConverterTrace c = Assert.Single(r.Converters!);
        Assert.Equal(0, r.ConverterIterationFailures);
        Assert.True(c.ActivePowerPu.Max(x => Math.Abs(x - 0.85)) <= 1e-9);
        Assert.True(r.Machines.Max(mt => mt.SpeedPu.Max(Math.Abs)) <= 1e-9);
    }

    [Fact]
    public void After_a_fault_the_plant_controller_brings_the_poi_voltage_back()
    {
        (GridModel m, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Case9(PoiControl());
        double v6 = pf.Vm[pf.BusIds.ToList().IndexOf(6)];
        // A 40 MW + 30 MVAr load step at bus 6 lowers its voltage for good; the plant controller's integral returns it to
        // the reference (the initial voltage) by raising the unit's reactive output.
        var events = new List<RmsEvent> { new RmsLoadStep(0.5, 6, 40.0, 30.0) };
        MultiMachineSubtransient.Result r = RmsTransientStability.StudyEvents(m, pf, data, events, stepSeconds: 0.005, durationSeconds: 30.0);
        Assert.True(r.StayedInSynchronism);
        Assert.Equal(0, r.ConverterIterationFailures);
        RmsConverterTrace c = Assert.Single(r.Converters!);
        Assert.Equal(6, c.RegulatedBus);
        IReadOnlyList<double> vreg = c.RegulatedBusVoltagePu!;
        Assert.Equal(v6, vreg[0], 9);
        int justAfter = 101;   // 5 ms after the step
        Assert.True(vreg[justAfter] < v6 - 1e-3, $"the load step should depress bus 6 ({vreg[justAfter]} vs {v6})");
        // The machines' fields are held, so their fluxes keep decaying (T'do = 8 s) and the network never quite stops moving;
        // the controller tracks it. It must remove almost all of the depression the step caused.
        double depression = v6 - vreg[justAfter], left = Math.Abs(vreg[^1] - v6);
        Assert.True(left <= 0.05 * depression, $"bus 6 settled at {vreg[^1]}, reference {v6}: {left} left of a {depression} depression");
        Assert.True(c.ReactivePowerPu[^1] > c.ReactivePowerPu[0] + 0.05, "the unit should export more reactive power");
    }

    [Fact]
    public void Plant_controller_fault_run_has_the_pinned_digest()
    {
        // The same digest with default, DOTNET_EnableHWIntrinsic=0 and DOTNET_EnableAVX2=0 proves the run bit-identical
        // across SIMD tiers (WeccRenewableDigestTests).
        (GridModel m, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Case9(PoiControl() with { Fflag = 1 });
        MultiMachineSubtransient.Result r = RmsTransientStability.Study(m, pf, data, 7, 0.1, 0.08, stepSeconds: 0.005, durationSeconds: 1.5);
        string d = WeccRenewableDigestTests.Digest(r);
        Assert.True(d == "CBD00D2ED73EDC5ECBE33904877D6571B571F53595C0CBE16C7CCBAA3FF0A657", "digest " + d);
    }

    [Fact]
    public void Repeated_runs_with_a_plant_controller_are_bit_identical()
    {
        (GridModel m, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Case9(PoiControl() with { Fflag = 1 });
        MultiMachineSubtransient.Result a = RmsTransientStability.Study(m, pf, data, 7, 0.1, 0.08, stepSeconds: 0.005, durationSeconds: 1.5);
        MultiMachineSubtransient.Result b = RmsTransientStability.Study(m, pf, data, 7, 0.1, 0.08, stepSeconds: 0.005, durationSeconds: 1.5);
        Assert.Equal(WeccRenewableDigestTests.Digest(a), WeccRenewableDigestTests.Digest(b));
        Assert.True(a.StayedInSynchronism);
    }

    // ---------------------------------------------------------------- DYR

    internal const string RepcRecord = "     2 'REPCA1' 1  0  2  5  '1'  0  1  0   0.02 2.0 10.0 0.0 0.05 0.0 0.0 0.0 0.0 0.5 -0.5 0.0 0.0 0.6 -0.6"
        + "  0.5 2.0 0.05 -0.004 0.004 999.0 -999.0 1.0 0.0 0.05 20.0 0.0 /";

    [Fact]
    public void Repca1_maps_value_for_value_and_resolves_its_flow_branch()
    {
        PsseParseResult raw = PsseRawParser.ParseFile(WeccRenewableNetworkTests.Fixture("case14.raw"));
        PsseDyrResult r = PsseDyrReader.Read(WeccRenewableNetworkTests.Dyr(WeccRenewableNetworkTests.RegcRecord, WeccRenewableNetworkTests.ReecRecord, RepcRecord),
            raw, "case14", new PsseDyrOptions { SourceName = "case14.dyr" });
        WeccPlant plant = r.Renewables.Single().Value;
        Assert.Equal("REGC_A + REEC_A + REPC_A", plant.Chain);
        RepcaParameters c = plant.Repc!;
        Assert.Equal((0, 2, 5, "1", 0, 1, 0), (c.RegulatedBus, c.FlowFromBus, c.FlowToBus, c.FlowCircuit, c.VcFlag, c.RefFlag, c.Fflag));
        Assert.Equal((0.02, 2.0, 10.0, 0.0, 0.05, 0.0, 0.0, 0.0, 0.0), (c.Tfltr, c.Kp, c.Ki, c.Tft, c.Tfv, c.Vfrz, c.Rc, c.Xc, c.Kc));
        Assert.Equal((0.5, -0.5, 0.0, 0.0, 0.6, -0.6, 0.5, 2.0, 0.05), (c.Emax, c.Emin, c.Dbd1, c.Dbd2, c.Qmax, c.Qmin, c.Kpg, c.Kig, c.Tp));
        Assert.Equal((-0.004, 0.004, 999.0, -999.0, 1.0, 0.0, 0.05, 20.0, 0.0), (c.Fdbd1, c.Fdbd2, c.Femax, c.Femin, c.Pmax, c.Pmin, c.Tg, c.Ddn, c.Dup));
        Branch br = raw.Model.Branches[c.FlowBranchIndex];
        Assert.Equal((2, 5), (br.From, br.To));
        Assert.Equal("PSS/E DYR case14.dyr, line(s) 11, 12, 15", plant.Source);

        // It runs: the plant output is the 2-5 flow, the unit at rest.
        GridModel m = raw.Model;
        var data = m.Generators.Select((g, k) =>
        {
            var d = new SubtransientInit.MachineData(Math.Max(g.H, 3.0), 0.0, 1.6, 0.25, 0.18, 1.55, 0.4, 0.18, 7.0, 0.03, 0.5, 0.05);
            return r.Renewables.TryGetValue(k, out WeccPlant? wp) ? d with { Renewable = wp } : d;
        }).ToList();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        MultiMachineSubtransient.Result rest = RmsTransientStability.Study(m, pf, data, 4, 50.0, 0.1, stepSeconds: 0.005, durationSeconds: 2.0);
        RmsConverterTrace t = Assert.Single(rest.Converters!);
        Assert.True(t.ActivePowerPu.Max(x => Math.Abs(x - t.ActivePowerPu[0])) <= 1e-9);
        Assert.Equal(0, rest.ConverterIterationFailures);
    }

    [Theory]
    [InlineData("  2  5  '1'", "  2  5  '7'", "REPC_A for machine '1' at bus 2 (line 15): flow branch 2-5 circuit '7' is not an in-service two-terminal branch of the RAW case")]
    [InlineData("     2 'REPCA1' 1  0  2", "     2 'REPCA1' 1  99  2", "REPC_A for machine '1' at bus 2 (line 15): regulated bus 99 is not a bus of the RAW case")]
    [InlineData("  0  1  0   0.02", "  2  1  0   0.02", "REPC_A VCFlag = 2: must be one of 0, 1")]
    public void Repca1_problems_are_refused_with_the_unit_and_line(string find, string replace, string expected)
    {
        PsseParseResult raw = PsseRawParser.ParseFile(WeccRenewableNetworkTests.Fixture("case14.raw"));
        string text = WeccRenewableNetworkTests.Dyr(WeccRenewableNetworkTests.RegcRecord, WeccRenewableNetworkTests.ReecRecord, RepcRecord.Replace(find, replace, StringComparison.Ordinal));
        Assert.NotEqual(WeccRenewableNetworkTests.Dyr(WeccRenewableNetworkTests.RegcRecord, WeccRenewableNetworkTests.ReecRecord, RepcRecord), text);
        var ex = Assert.Throws<InvalidDataException>(() => PsseDyrReader.Read(text, raw, "case14"));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plant_controller_without_an_electrical_control_is_refused()
    {
        PsseParseResult raw = PsseRawParser.ParseFile(WeccRenewableNetworkTests.Fixture("case14.raw"));
        var ex = Assert.Throws<InvalidDataException>(() => PsseDyrReader.Read(WeccRenewableNetworkTests.Dyr(WeccRenewableNetworkTests.RegcRecord, RepcRecord), raw, "case14"));
        Assert.Contains("REPC_A needs an electrical control model (REEC_A/B/C) to drive", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Stability_rms_runs_a_dyr_wecc_plant_and_reports_its_chain()
    {
        // The fixture's bus-1 TGOV1 and SEXS cannot hold that unit's dispatch (valve limit 1.1 pu against 2.32 pu, field
        // ceiling 4 pu against 4.22 pu), which their initialisations refuse; both are left out here, the rest kept.
        string text = WeccRenewableNetworkTests.Dyr(WeccRenewableNetworkTests.RegcRecord, WeccRenewableNetworkTests.ReecRecord, RepcRecord);
        text = string.Join("\n", text.Split('\n').Where(l => !l.Contains("'TGOV1'", StringComparison.Ordinal) && !l.Contains("'SEXS'", StringComparison.Ordinal)));
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-wecc-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string dyr = Path.Combine(dir, "plant.dyr");
            File.WriteAllText(dyr, text);
            CliProcess.Result r = CliProcess.RunBuilt("stability", WeccRenewableNetworkTests.Fixture("case14.raw"), "--rms", "--clear", "100", "--dyn-data", dyr);
            Assert.True(r.ExitCode == 0, r.ToString());
            Assert.Contains("1 WECC renewable unit(s) from the dynamics file:", r.StdOut, StringComparison.Ordinal);
            Assert.Contains("@ bus 2: REGC_A + REEC_A + REPC_A  (PSS/E DYR plant.dyr, line(s) 9, 10, 13)", r.StdOut, StringComparison.Ordinal);
            Assert.Contains("REGC_A Accel = 0.7 is a PSS/E network-solution acceleration factor", r.StdErr, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }
}
