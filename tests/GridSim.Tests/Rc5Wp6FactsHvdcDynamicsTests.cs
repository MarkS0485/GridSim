using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.Models.Adaptive;
using GridSim.Core.IO;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP6: RMS models for static var compensators (CSVGN1), STATCOMs (STATCOM1), LCC HVDC (LCC1, a CDC4T-style subset) and
/// VSC HVDC (VSC1), attached either to a generator of the case (<see cref="SubtransientInit.MachineData.Facts"/>) or to a
/// network device by name (<see cref="RmsLoadedMachineData.Devices"/>). Verification is analytical: every expected value is
/// derived in the test from the circuit (a stiff source behind a reactance, or the converter equations), and the exact
/// zero-order-hold lags are checked against the closed-form step response. Not compared with PSS/E or PowerFactory runs.
/// Without device data the RMS study is the historical one bit for bit (pinned digest from the 2.1.082 build).
/// </summary>
public sealed class Rc5Wp6FactsHvdcDynamicsTests
{
    private const double Eps = 1.1102230246251565e-16;   // 2^-53, the unit roundoff

    // ------------------------------------------------------------------------------------------------ system S

    /// <summary>System S: 100 MVA base; bus 1 (slack) holds a GENCLS with X'd = 0.1 pu and H = 1000 s (a constant EMF, in
    /// effect stiff); a lossless line of X = 0.1 pu to bus 2, where the device sits as a 0 MW generator (PV at 1 pu). No load,
    /// so the power flow is flat (V = 1 at angle 0 everywhere, every current 0) and the Thevenin view of bus 2 is E = 1 behind
    /// Xth = X'd + X = 0.2 pu.</summary>
    private static (GridModel Model, PowerFlowResult Pf) SystemS(double devicePg = 0)
    {
        var m = new GridModel
        {
            Name = "wp6-s",
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pv, BaseKv: 132) },
            Generators = new[]
            {
                new Generator(1, 0, Vg: 1.0, MBase: 100, H: 1000, Name: "source"),
                new Generator(2, devicePg, Vg: 1.0, Qmin: -999, Qmax: 999, MBase: 100, Name: "device"),
            },
            Branches = new[] { new Branch(1, 2, 0, 0.1) },
        };
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Tolerance = 1e-13 });
        Assert.True(pf.Converged);
        return (m, pf);
    }

    private static List<SubtransientInit.MachineData> SData(RmsFactsData facts) => new()
    {
        Gencls.Data(1000, 0, 0.1),
        Gencls.Data(1, 0, 0.1) with { Facts = facts },
    };

    private static MultiMachineSubtransient.Result RunS(RmsFactsData facts, double dt, double duration, params RmsEvent[] events)
    {
        (GridModel m, PowerFlowResult pf) = SystemS();
        return Study(m, pf, SData(facts), events, dt, duration);
    }

    /// <summary>The RMS event study, or with no event the observed study (the event study's run with no event; StudyEvents
    /// itself asks for at least one).</summary>
    private static MultiMachineSubtransient.Result Study(GridModel m, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data,
        RmsEvent[] events, double dt, double duration) => events.Length == 0
        ? RmsTransientStability.StudyEventsObserved(m, pf, data, events, new[] { m.Buses[0].Id }, stepSeconds: dt, durationSeconds: duration).Result
        : RmsTransientStability.StudyEvents(m, pf, data, events, stepSeconds: dt, durationSeconds: duration);

    /// <summary>CSVGN1 of test 2: K = 25, T1..T4 = 0, T5 = 0.05 s, RMIN = 0, VMAX = 1, VMIN = 0, CBASE = 50 Mvar, MBASE = 100.</summary>
    private static Csvgn1Data Svc(double cbase = 50) => new(25, 0, 0, 0, 0, 0.05, 0, 1, 0, cbase, 100);

    /// <summary>STATCOM1 of tests 4 and 5: MBASE 100, Xs 0.03, Kp 0, Ki 50, Tc 0.01 s, limits 1 pu both ways.</summary>
    private static Statcom1Data Statcom() => new(100, 0.03, 0, 50, 0.01, 1.0, 1.0);

    // ------------------------------------------------------------------------------------------------ 1. flat start

    [Fact]
    public void A_generator_held_svc_and_statcom_start_flat_and_stay_there()
    {
        foreach (RmsFactsData f in new RmsFactsData[] { Svc(), Statcom(), new Statcom1Data(100, 0.02, 2.0, 0, 0, 1, 1) })
        {
            MultiMachineSubtransient.Result r = RunS(f, 0.005, 1.0);
            RmsConverterTrace t = Assert.Single(r.Converters!);
            RmsDeviceTrace d = Assert.Single(r.Devices!);
            Assert.Equal(t.TerminalVoltagePu.Count, d.Samples.Count);
            // The power flow is flat to 1e-13 pu, so the start is exact to that and nothing moves (bounds: the power-flow
            // tolerance, with margin for a few roundings per step over 200 steps).
            for (int k = 0; k < t.TerminalVoltagePu.Count; k++)
            {
                Assert.True(Math.Abs(t.TerminalVoltagePu[k] - 1.0) <= 1e-12, $"{f.Model}: V {t.TerminalVoltagePu[k]:R} at sample {k}");
                Assert.True(Math.Abs(t.ReactivePowerPu[k]) <= 1e-12 && Math.Abs(t.ActivePowerPu[k]) <= 1e-12, $"{f.Model}: S at sample {k}");
                for (int s = 0; s < d.StateNames.Count; s++)
                    Assert.True(Math.Abs(d.Samples[k][s] - d.Samples[0][s]) <= 1e-12, $"{f.Model}: {d.StateNames[s]} moved at sample {k}");
            }
            Assert.All(r.Machines[0].SpeedPu, w => Assert.True(Math.Abs(w) <= 1e-12));
        }
        // CSVGN1's initial point: Y0 = 0, so x0 = (CBASE / Sb - Y0) / (MBASE / Sb) = 0.5.
        Assert.Equal(0.5, RunS(Svc(), 0.005, 0.01).Devices![0].Samples[0][0], 15);
    }

    /// <summary>The kitchen-sink case: an LCC link (bus 2 to bus 3, a 500 MVA machine at the inverter bus), a VSC link (bus 4
    /// power at a 0.95 power factor, bus 5 DC voltage in AC voltage control), a compensator holding bus 6 at 1 pu against a
    /// 20 Mvar load (BINIT 10 Mvar in the bus shunt) and a STATCOM holding bus 7 against 20 Mvar.</summary>
    internal static GridModel Sink()
    {
        var rect = new LccConverter(2, 2, 25, 10, 0, 5, 345, 0.56, 1.0, 1.1, 0.9, 0.00625);
        var inv = new LccConverter(3, 2, 25, 18, 0, 5, 345, 0.56, 1.0, 1.1, 0.9, 0.00625);
        var vp = new VscConverter(4, VscDcControl.Power, VscAcControl.PowerFactor, 80, 0.95, 100, 0.5, 50, 0, 0, 1, 500, -500);
        var vv = new VscConverter(5, VscDcControl.DcVoltage, VscAcControl.Voltage, 300, 1.0, 100, 0.5, 50, 0, 0, 1, 500, -500);
        return new GridModel
        {
            Name = "wp6-sink", BaseMva = 100,
            Buses = new[]
            {
                new Bus(1, BusType.Slack, BaseKv: 345), new Bus(2, BusType.Pq, BaseKv: 345), new Bus(3, BusType.Pv, BaseKv: 345),
                new Bus(4, BusType.Pq, BaseKv: 345), new Bus(5, BusType.Pq, BaseKv: 345),
                new Bus(6, BusType.Pq, Qd: 20, Bs: 10, BaseKv: 345), new Bus(7, BusType.Pq, Qd: 20, BaseKv: 345),
            },
            Generators = new[]
            {
                new Generator(1, 0, Vg: 1.0, MBase: 1000, H: 5, Name: "A"),
                new Generator(3, 0, Vg: 1.0, Qmin: -500, Qmax: 500, MBase: 500, H: 5, Name: "B"),
            },
            Branches = new[]
            {
                new Branch(1, 2, 0, 0.01), new Branch(1, 3, 0, 0.02), new Branch(1, 4, 0, 0.05), new Branch(1, 5, 0, 0.05),
                new Branch(1, 6, 0, 0.1), new Branch(1, 7, 0, 0.1),
            },
            LccLinks = new[] { new LccDcLink("L1", LccMode.Power, 10, 100, 500, 0, 0, 0.15, rect, inv) },
            VscLinks = new[] { new VscDcLink("V1", 2, vp, vv) },
            StaticVarCompensators = new[] { new StaticVarCompensator(6, -100, 100, 10, 1.0, 1.0, "svc1") },
            FactsDevices = new[] { new FactsDevice("st1", FactsMode.Statcom, 7, 0, 0, 0, 1.0, 100, 0, 0.9, 1.1, 0, 0) },
        };
    }

    private static PowerFlowResult SinkPf(GridModel m)
    {
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Tolerance = 1e-12 });
        Assert.True(pf.Converged);
        return pf;
    }

    private static List<SubtransientInit.MachineData> SinkData() => new() { Gencls.Data(5, 0, 0.2), Gencls.Data(5, 0, 0.2) };

    private static Lcc1Data SinkLcc() => new(5, 18, 0.01, 0.02, 0.5, 0.8, 0.1, 0, 0, 20, 100, 50, 300, 150, 450, 300);

    private static RmsNetworkDeviceData[] SinkDevices() => new RmsNetworkDeviceData[]
    {
        new LccLinkDynamicsData("L1", SinkLcc()),
        new VscLinkDynamicsData("V1", new Vsc1Data(0.05, 0.02, 1.2, 100)),
        // SVC: the power flow's susceptance is 20 Mvar, so x0 = (50 - 20) / 100 = 0.3, inside [0, 1].
        new SvcDeviceData("svc1", Svc()),
        new StatcomDeviceData("st1", new Statcom1Data(100, 0.03, 0, 50, 0.01, 1.0, 1.0)),
    };

    [Fact]
    public void Every_network_device_starts_on_its_power_flow_injection_and_stays_there()
    {
        GridModel m = Sink();
        PowerFlowResult pf = SinkPf(m);
        var data = new RmsLoadedMachineData(SinkData(), Array.Empty<DynamicLoadData>(), SinkDevices());
        MultiMachineSubtransient.Result r = Study(m, pf, data, Array.Empty<RmsEvent>(), 0.005, 1.0);
        Assert.True(r.ConverterIterationsConverged);
        LccLinkResult lcc = pf.LccLinks![0];
        VscLinkResult vsc = pf.VscLinks![0];
        SvcResult svc = pf.StaticVarCompensators![0];
        FactsResult st = pf.FactsDevices![0];
        double V2(int bus) { double v = pf.Vm[pf.BusIds.ToList().IndexOf(bus)]; return v * v; }
        // The injections the power flow's AC model carried (system pu, exporting positive).
        var want = new (string Name, double P, double Q)[]
        {
            ("L1 rectifier", -lcc.RectifierMw / 100, -lcc.RectifierMvar / 100),
            ("L1 inverter", lcc.InverterMw / 100, -lcc.InverterMvar / 100),
            ("V1 converter 1", vsc.First.Mw / 100, vsc.First.Mvar / 100),
            ("V1 converter 2", vsc.Second.Mw / 100, vsc.Second.Mvar / 100),
            ("svc1", 0.0, (svc.SusceptanceMvar - 10) / 100 * V2(6)),
            ("st1", 0.0, st.ShuntMvar / 100),
        };
        Assert.Equal(want.Length, r.Converters!.Count);
        // Bound: the power flow's 1e-12 pu mismatch, seen through the driving-point impedances (at most 0.1 pu) and the
        // devices' voltage sensitivities (at most about 2 pu of power per pu of voltage), with a factor 10 of margin.
        const double tol = 1e-11;
        for (int u = 0; u < want.Length; u++)
        {
            RmsConverterTrace t = r.Converters[u];
            Assert.Equal(want[u].Name, t.Name);
            Assert.True(Math.Abs(t.ActivePowerPu[0] - want[u].P) <= tol, $"{t.Name}: P0 {t.ActivePowerPu[0]:R} vs {want[u].P:R}");
            Assert.True(Math.Abs(t.ReactivePowerPu[0] - want[u].Q) <= tol, $"{t.Name}: Q0 {t.ReactivePowerPu[0]:R} vs {want[u].Q:R}");
            // Flat over the second: the same bound, the power-flow residual integrated by H = 5 s machines and Ki = 50
            // controllers staying well inside it.
            for (int k = 0; k < t.TerminalVoltagePu.Count; k++)
            {
                Assert.True(Math.Abs(t.TerminalVoltagePu[k] - t.TerminalVoltagePu[0]) <= 1e-10, $"{t.Name}: V moved at sample {k}");
                Assert.True(Math.Abs(t.ActivePowerPu[k] - t.ActivePowerPu[0]) <= 1e-10 && Math.Abs(t.ReactivePowerPu[k] - t.ReactivePowerPu[0]) <= 1e-10,
                    $"{t.Name}: S moved at sample {k}");
            }
        }
        Assert.Equal(4, r.Devices!.Count);
        foreach (RmsDeviceTrace d in r.Devices)
            for (int k = 0; k < d.Samples.Count; k++)
                for (int s = 0; s < d.StateNames.Count; s++)
                    Assert.True(Math.Abs(d.Samples[k][s] - d.Samples[0][s]) <= 1e-8 * (1 + Math.Abs(d.Samples[0][s])), $"{d.Name}: {d.StateNames[s]} moved at sample {k}");
        foreach (MultiMachineSubtransient.MachineTrace mt in r.Machines)
            Assert.All(mt.SpeedPu, w => Assert.True(Math.Abs(w) <= 1e-10));
        // The SVC's initial reactor fraction (x0 = (0.5 - 0.2) / 1) and the LCC's initial current (the power flow's).
        Assert.Equal(0.3, r.Devices.Single(d => d.Name == "svc1").Samples[0][0], 12);
        Assert.Equal(lcc.CurrentA / 1000, r.Devices.Single(d => d.Name == "L1").Samples[0][0], 15);
    }

    [Fact]
    public void With_devices_bound_the_machine_at_the_inverter_bus_no_longer_carries_the_link()
    {
        GridModel m = Sink();
        PowerFlowResult pf = SinkPf(m);
        // Default (the historical path, unchanged): machine B at the inverter bus is given the bus's whole injection, the
        // link's delivered power included.
        SubtransientInit.Prepared plain = SubtransientInit.FromSolvedCase(m, pf, SinkData());
        Assert.Equal(pf.LccLinks![0].InverterMw / 100, plain.Pm[1], 9);
        // With the link bound, its power is its own unit's and machine B carries its 0 MW schedule.
        SubtransientInit.Prepared bound = SubtransientInit.FromSolvedCase(m, pf, new RmsLoadedMachineData(SinkData(), Array.Empty<DynamicLoadData>(), SinkDevices()));
        Assert.True(Math.Abs(bound.Pm[1]) <= 1e-10, $"Pm(B) {bound.Pm[1]:R}");
        Assert.Equal(6, bound.ConverterUnits.Count);
    }

    // ------------------------------------------------------------------------------------------------ 2. SVC droop

    /// <summary>
    /// CSVGN1 of <see cref="Svc"/> on system S. Y0 = 0, so x0 = 0.5 and Vref = V0 - x0 / K = 0.98. A 20 Mvar inductive load step
    /// (B = -0.2 pu) at 0.1 s. At steady state inside the limits Y = Y0 - K (MBASE / Sb)(V - V0) = -25 (V - 1), and bus 2 is
    /// V = E / (1 - Xth (Y - 0.2)) = 1 / (1 - 0.2 (Y - 0.2)), so 5 V^2 - 3.96 V - 1 = 0:
    /// V = (3.96 + sqrt(3.96^2 + 20)) / 10 = 0.9933407737631846, Y = 0.16648065592038408, x = 0.5 - Y = 0.3335193440796159,
    /// Q = Y V^2 = 0.16427077385037384. At 0.1 s the state has not moved (Y = 0), so V = 1 / 1.04. Linearised, the lag sees
    /// x' = (K (V - Vref) - x) / T5 with dV/dx = -Xth V^2 (dY/dx = -1), so tau = T5 / (1 + K Xth V^2) = 8.4265 ms; the
    /// zero-order-hold step with the voltage held across 0.1 ms steps gives 8.385 ms, within 0.5 %.
    /// </summary>
    [Fact]
    public void Svc_settles_on_its_droop_line_with_the_linearised_time_constant()
    {
        const double dt = 1e-4;
        MultiMachineSubtransient.Result r = RunS(Svc(), dt, 2.0, new RmsLoadStep(0.1, 2, 0, 20));
        RmsConverterTrace t = r.Converters![0];
        RmsDeviceTrace d = r.Devices![0];
        Assert.Equal(0.5, d.Samples[0][0], 15);
        Assert.True(Math.Abs(t.TerminalVoltagePu[1000] - 1.0 / 1.04) <= 1e-12, $"V(0.1+) {t.TerminalVoltagePu[1000]:R}");
        double vInf = (3.96 + Math.Sqrt(3.96 * 3.96 + 20)) / 10;
        Assert.Equal(0.9933407737631846, vInf, 15);
        Assert.True(Math.Abs(t.TerminalVoltagePu[^1] - vInf) <= 1e-9, $"V(2 s) {t.TerminalVoltagePu[^1]:R}");
        Assert.True(Math.Abs(d.Samples[^1][1] - 0.16648065592038408) <= 25 * 1e-9, $"Y {d.Samples[^1][1]:R}");
        Assert.True(Math.Abs(d.Samples[^1][0] - 0.3335193440796159) <= 25 * 1e-9, $"x {d.Samples[^1][0]:R}");
        Assert.True(Math.Abs(t.ReactivePowerPu[^1] - 0.16427077385037384) <= 1e-8, $"Q {t.ReactivePowerPu[^1]:R}");
        // The time constant from the decay of |V - V_inf| between 1e-3 and 1e-6 of its jump.
        double d0 = Math.Abs(1.0 / 1.04 - vInf);
        double T(double frac)
        {
            for (int k = 1001; k < t.TerminalVoltagePu.Count; k++)
                if (Math.Abs(t.TerminalVoltagePu[k] - vInf) <= frac * d0) return k * dt;
            throw new InvalidOperationException("no crossing");
        }
        double tau = (T(1e-6) - T(1e-3)) / Math.Log(1e3);
        double tauLin = 0.05 / (1 + 25 * 0.2 * vInf * vInf);
        Assert.Equal(0.0084265457259329, tauLin, 12);
        Assert.True(Math.Abs(tau - tauLin) <= 0.02 * tauLin, $"tau {tau:R} vs {tauLin:R}");
    }

    // ------------------------------------------------------------------------------------- 3 and 4. at the limits

    /// <summary>CBASE = MBASE = 100, so x0 = 1 (all reactor, Y0 = 0) at the top of [0, 1]. A 300 Mvar step (B = -3.0) drives
    /// x to RMIN / MBASE = 0, so Y = CBASE / Sb = 1.0 and V = E / (1 - 0.2 (1 - 3)) = 1 / 1.4 = 0.7142857142857143
    /// (= E_th / (1 - X' 1), E_th = 1 / 1.6, X' = 0.2 / 1.6), Q = Y V^2 = 0.5102040816326531.</summary>
    [Fact]
    public void Svc_at_its_capacitive_limit_is_a_fixed_capacitor()
    {
        MultiMachineSubtransient.Result r = RunS(Svc(cbase: 100), 1e-3, 2.0, new RmsLoadStep(0.1, 2, 0, 300));
        RmsConverterTrace t = r.Converters![0];
        Assert.Equal(1.0, r.Devices![0].Samples[0][0], 15);
        Assert.True(Math.Abs(t.TerminalVoltagePu[^1] - 1.0 / 1.4) <= 1e-12, $"V {t.TerminalVoltagePu[^1]:R}");
        Assert.True(Math.Abs(t.ReactivePowerPu[^1] - 1.0 / 1.96) <= 1e-12, $"Q {t.ReactivePowerPu[^1]:R}");
    }

    /// <summary>STATCOM1 of <see cref="Statcom"/>, Iq0 = 0, Vref = 1. The same 300 Mvar step: the integrator would need
    /// e = Vref - V - Xs Iq = 1 - 0.75 - 0.03 = 0.22 &gt; 0 at Iq = 1, so it is held at the limit ImaxCap = 1 and the current
    /// lag settles there; bus 2 is then V = E_th + X' Iq = 0.625 + 0.125 = 0.75 and Q = V Iq = 0.75 pu = 75 Mvar. At the same
    /// 100 MVA rating the SVC of the test above gives 51.02 Mvar: a STATCOM's output falls with V, an SVC's with V^2.</summary>
    [Fact]
    public void Statcom_at_its_current_limit_gives_a_reactive_power_linear_in_voltage()
    {
        MultiMachineSubtransient.Result r = RunS(Statcom(), 1e-3, 2.0, new RmsLoadStep(0.1, 2, 0, 300));
        RmsConverterTrace t = r.Converters![0];
        Assert.True(Math.Abs(t.TerminalVoltagePu[^1] - 0.75) <= 1e-12, $"V {t.TerminalVoltagePu[^1]:R}");
        Assert.True(Math.Abs(t.ReactivePowerPu[^1] - 0.75) <= 1e-12, $"Q {t.ReactivePowerPu[^1]:R}");
        Assert.True(Math.Abs(r.Devices![0].Samples[^1][0] - 1.0) <= 1e-12);
        Assert.True(t.ReactivePowerPu[^1] > RunS(Svc(cbase: 100), 1e-3, 2.0, new RmsLoadStep(0.1, 2, 0, 300)).Converters![0].ReactivePowerPu[^1]);
    }

    /// <summary>A 20 Mvar step, unlimited: e = 0 at steady state, so V = Vref - Xs Iq = 1 - 0.03 Iq; the network gives
    /// V = E_th + X' Iq = (1 + 0.2 Iq) / 1.04. So 0.04 = 0.2312 Iq: Iq = 0.17301038062283738, V = 0.9948096885813149. The
    /// slow closed-loop pole is about 12.7 /s, so after 2.9 s the remaining error is below 1e-15.</summary>
    [Fact]
    public void Statcom_settles_on_its_slope()
    {
        MultiMachineSubtransient.Result r = RunS(Statcom(), 1e-3, 3.0, new RmsLoadStep(0.1, 2, 0, 20));
        Assert.True(Math.Abs(r.Converters![0].TerminalVoltagePu[^1] - 0.9948096885813149) <= 1e-9, $"V {r.Converters[0].TerminalVoltagePu[^1]:R}");
        Assert.True(Math.Abs(r.Devices![0].Samples[^1][0] - 0.04 / 0.2312) <= 1e-8, $"Iq {r.Devices[0].Samples[^1][0]:R}");
    }

    // ------------------------------------------------------------------------------------------------ 6 to 7. LCC

    /// <summary>A link with no DC resistance and no commutating reactance or resistance (two bridges, 230 kV, ratio 1, tap 1),
    /// the inverter at gamma = 15 degrees and the rectifier free from 5 to 25 degrees, 100 MW ordered at the rectifier. Then
    /// Vdi = Vdr = 2 k 230 cos 15 deg (constant, whatever the current) and the order's current is P / Vd.</summary>
    private static (LccDcLink Link, LccLinkResult Pf) PlainLink(int rectBus = 1, int invBus = 3)
    {
        var rect = new LccConverter(rectBus, 2, 25, 5, 0, 0, 230, 1.0, 1.0, 1.1, 0.9, 0.00625);
        var inv = new LccConverter(invBus, 2, 25, 15, 0, 0, 230, 1.0, 1.0, 1.1, 0.9, 0.00625);
        double vd = 2 * 3 * Math.Sqrt(2) / Math.PI * 230 * Math.Cos(15 * Math.PI / 180);
        var link = new LccDcLink("P", LccMode.Power, 0, 100, vd, 0, 0, 0.15, rect, inv);
        LccLinkResult pf = LccDcLinks.SolveLink(link, 1.0, 1.0);
        Assert.Equal(LccControl.RectifierCurrent, pf.Control);
        Assert.Equal(1.0, pf.InverterTap);
        Assert.Equal(1.0, pf.RectifierTap);
        return (link, pf);
    }

    private static Lcc1Data PlainLcc(double tidc, double cramp = 0, params RmsOrderStep[] steps) =>
        new(5, 15, 0.01, tidc, 0.5, 0.8, 0.1, 0, cramp, 50, 100, 500, 200, 1000, 300, 2000) { OrderSteps = steps };

    /// <summary>
    /// The order steps from 100 to 110 MW at t = 0. With Vdc constant the order current is Io1 = 110 / Vd and the current lag
    /// gives Id(t) = Io1 + (Io0 - Io1) e^(-t / TIDC), so P(t) = Vd Id = 100 + 10 (1 - e^(-t / 0.02)) MW: 106.32120558828558
    /// at 0.02 s and 109.93262053000915 at 0.1 s. The lag is the exact zero-order hold (the input is held across each step),
    /// so the samples are the continuous solution itself; the only error is rounding, at most two roundings per step of the
    /// recursion, 2 eps n P. (The spec's forward-Euler comparison with (1 - dt / T)^n does not apply: the blocks of
    /// ControlBlocks are exact for a held input.)
    /// </summary>
    [Fact]
    public void Lcc_power_order_step_follows_the_current_lag_exactly()
    {
        (LccDcLink link, LccLinkResult pf) = PlainLink();
        var plan = new LccLinkPlan(link, pf, PlainLcc(0.02, 0, new RmsOrderStep(0.0, 110)), 100, Complex.One, Complex.One);
        var s = new LccLinkState(plan, null);
        const double dt = 1e-5;
        Assert.Equal(100.0, s.Injections(1, 1).Op.RectifierMw, 12);
        for (int n = 0; n <= 10000; n++)
        {
            double t = n * dt;
            double p = s.Injections(1, 1).Op.RectifierMw;
            double want = 100 + 10 * (1 - Math.Exp(-t / 0.02));
            Assert.True(Math.Abs(p - want) <= 2 * Eps * (n + 1) * 110 + 1e-12, $"P({t:R}) {p:R} vs {want:R}");
            if (n == 2000) Assert.True(Math.Abs(p - 106.32120558828558) <= 1e-10, $"P(0.02) {p:R}");
            if (n == 10000) Assert.True(Math.Abs(p - 109.93262053000915) <= 3e-10, $"P(0.1) {p:R}");
            s.Advance(t, Complex.One, Complex.One, dt);
        }
        Assert.Equal(106.32120558828558, 100 + 10 * (1 - Math.Exp(-1)), 12);
    }

    /// <summary>The same link in the RMS network: buses 1 (rectifier) and 3 (inverter) each hold a stiff GENCLS (H = 1e4 s,
    /// X'd = 1e-4 pu) and are joined by a 0.1 pu line; the order steps from 100 to 110 MW at 0.1 s. The AC voltages move
    /// by about 1e-5, so the rectifier power reaches 100 + 10 (1 - 1/e) MW one TIDC after the step within 1 %.</summary>
    [Fact]
    public void Lcc_power_order_step_in_the_network_has_the_current_lag_time_constant()
    {
        (LccDcLink link, _) = PlainLink();
        var m = new GridModel
        {
            Name = "wp6-lcc", BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 230), new Bus(3, BusType.Pv, BaseKv: 230) },
            Generators = new[] { new Generator(1, 0, Vg: 1.0, MBase: 100, Name: "R"), new Generator(3, 0, Vg: 1.0, Qmin: -999, Qmax: 999, MBase: 100, Name: "I") },
            Branches = new[] { new Branch(1, 3, 0, 0.1) },
            LccLinks = new[] { link },
        };
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Tolerance = 1e-12 });
        Assert.True(pf.Converged, pf.LccNote);
        var data = new RmsLoadedMachineData(new List<SubtransientInit.MachineData> { Gencls.Data(1e4, 0, 1e-4), Gencls.Data(1e4, 0, 1e-4) },
            Array.Empty<DynamicLoadData>(), new RmsNetworkDeviceData[] { new LccLinkDynamicsData("P", PlainLcc(0.02, 0, new RmsOrderStep(0.1, 110))) });
        const double dt = 1e-4;
        MultiMachineSubtransient.Result r = Study(m, pf, data, Array.Empty<RmsEvent>(), dt, 0.3);
        RmsConverterTrace rect = r.Converters![0];
        double p0 = -rect.ActivePowerPu[0] * 100, p1 = -rect.ActivePowerPu[^1] * 100;
        Assert.True(Math.Abs(p0 - pf.LccLinks![0].RectifierMw) <= 1e-9);
        double target = p0 + (p1 - p0) * (1 - Math.Exp(-1));
        int k = 1000;
        while (-rect.ActivePowerPu[k] * 100 < target) k++;
        double pa = -rect.ActivePowerPu[k - 1] * 100, pb = -rect.ActivePowerPu[k] * 100;
        double tCross = (k - 1 + (target - pa) / (pb - pa)) * dt - 0.1;
        Assert.True(Math.Abs(tCross - 0.02) <= 0.01 * 0.02, $"tau {tCross:R}");
        Assert.True(Math.Abs(p1 - 110) <= 0.01, $"P(0.3) {p1:R}");
    }

    /// <summary>VBLOCK 0.5, VUNBL 0.8, TBLOCK 0.1 s, CRAMP 5 pu/s of Id0, TIDC 0 (the current is its order). The rectifier
    /// voltage is 0.3 pu from 0.05 s to 0.25 s: the step that reads it blocks the link, so both ends inject nothing from the
    /// next sample. At 0.25 s the voltage is back above VUNBL and the link has been blocked 0.2 s &gt;= TBLOCK, so it unblocks
    /// and the current recovers along Id = 5 Id0 (t - 0.25), reaching Id0 at 0.45 s.</summary>
    [Fact]
    public void Lcc_blocks_below_vblock_and_recovers_along_the_current_ramp()
    {
        (LccDcLink link, LccLinkResult pf) = PlainLink();
        var plan = new LccLinkPlan(link, pf, PlainLcc(0.0, 5.0), 100, Complex.One, Complex.One);
        var s = new LccLinkState(plan, null);
        const double dt = 1e-3;
        double id0 = pf.CurrentA / 1000, vd = pf.RectifierDcKv;
        for (int n = 0; n <= 600; n++)
        {
            double t = n * dt;
            (Complex sr, Complex si, LccOperatingPoint op) = s.Injections(1, 1);
            if (n >= 51 && n <= 250) { Assert.Equal(Complex.Zero, sr); Assert.Equal(Complex.Zero, si); Assert.True(s.Blocked); }
            if (n <= 50) Assert.Equal(id0, op.CurrentKa, 15);
            if (n > 250 && n <= 450)
            {
                double want = 5 * id0 * (t - 0.25);
                Assert.True(Math.Abs(op.CurrentKa - want) <= 1e-12 * id0, $"Id({t:R}) {op.CurrentKa:R} vs {want:R}");
                Assert.True(Math.Abs(op.RectifierMw - vd * want) <= 1e-9, $"P({t:R})");
            }
            if (n > 450) Assert.Equal(id0, op.CurrentKa, 12);
            double vr = t >= 0.05 - 1e-12 && t < 0.25 - 1e-12 ? 0.3 : 1.0;
            s.Advance(t, new Complex(vr, 0), Complex.One, dt);
        }
    }

    // ------------------------------------------------------------------------------------------------ 8 and 9. VSC

    /// <summary>A VSC link: converter 1 draws 80 MW at unity power factor (bus 1), converter 2 holds 300 kV (bus 2); losses
    /// 100 kW + 0.5 kW/A each, at least 50 kW; a 2 ohm DC line. Its power-flow state from <see cref="VscDcLinks.SolveDc"/>.</summary>
    private static (VscDcLink Link, VscLinkResult Pf) PlainVsc()
    {
        var a = new VscConverter(1, VscDcControl.Power, VscAcControl.PowerFactor, 80, 1.0, 100, 0.5, 50, 0, 0, 1, 500, -500);
        var b = new VscConverter(2, VscDcControl.DcVoltage, VscAcControl.PowerFactor, 300, 1.0, 100, 0.5, 50, 0, 0, 1, 500, -500);
        var link = new VscDcLink("VV", 2, a, b);
        var dc = VscDcLinks.SolveDc(link);
        var pf = new VscLinkResult("VV", dc.CurrentKa * 1000, new VscConverterResult(1, dc.FirstMw, 0, dc.FirstDcKv, dc.FirstLossMw, 80, false),
            new VscConverterResult(2, dc.SecondMw, 0, dc.SecondDcKv, dc.SecondLossMw, double.NaN, false));
        return (link, pf);
    }

    /// <summary>The order steps from 80 to 100 MW drawn at t = 0. The power lag (exact zero-order hold) gives
    /// P_f(t) = P0 + dP (1 - e^(-t / Tp)), Tp = 0.05 s, P0 = -0.8, dP = -0.2 pu (injected), to rounding. At every step the DC
    /// voltage converter delivers what the power converter draws less the line loss R I^2 and both converter losses, each
    /// max(MINLOSS, A + B |I|), with I the current that draws the held P_f: (Vdc + R I) I + loss_1(I) = drawn.</summary>
    [Fact]
    public void Vsc_power_step_follows_its_lag_and_the_dc_terminal_delivers_the_power_less_the_losses()
    {
        (VscDcLink link, VscLinkResult pf) = PlainVsc();
        var plan = new VscLinkPlan(link, pf, new Vsc1Data(0.05, 0.02, 1.5, 100) { OrderSteps = new[] { new RmsOrderStep(0.0, 100) } },
            100, Complex.One, Complex.One);
        var s = new VscLinkState(plan, null);
        const double dt = 1e-4;
        static double Loss(double i) => Math.Max(0.05, 0.1 + 0.5 * Math.Abs(i));   // MW, i in kA
        for (int n = 0; n <= 3000; n++)
        {
            double t = n * dt;
            double want = -0.8 - 0.2 * (1 - Math.Exp(-t / 0.05));
            Assert.True(Math.Abs(s.PowerFiltered - want) <= 2 * Eps * (n + 1) + 1e-15, $"P_f({t:R}) {s.PowerFiltered:R} vs {want:R}");
            // The power converter injects P_f at 1 pu.
            Complex i1 = s.TrialCurrent(0, Complex.One);
            Assert.True(Math.Abs(i1.Real - s.PowerFiltered) <= 1e-15);
            double drawn = -s.PowerFiltered * 100, ika = s.DcCurrentKa;
            Assert.True(Math.Abs((300 + 2 * ika) * ika + Loss(ika) - drawn) <= 1e-10, $"DC current at {t:R}");
            double delivered = s.DcTerminalPower * 100;
            Assert.True(Math.Abs(delivered - (drawn - 2 * ika * ika - 2 * Loss(ika))) <= 1e-10, $"DC terminal at {t:R}: {delivered:R}");
            s.Advance(t, Complex.One, Complex.One, dt);
        }
    }

    /// <summary>Reactive priority at |V| = 0.5 with Imax = 1.1 and Iq_cmd = 1.0: Iq = 1.0 and |Id| &lt;= sqrt(1.21 - 1) =
    /// 0.45825756949558394 (the order's 2.0 is clipped there); active priority keeps Id = 1.1 and leaves no room for Iq.</summary>
    [Fact]
    public void Vsc_current_limit_keeps_the_reactive_current_first()
    {
        (double id, double iq) = VscQuasiSteady.Limit(2.0, 1.0, 1.1, VscCurrentPriority.Reactive);
        Assert.Equal(1.0, iq);
        Assert.True(Math.Abs(id - 0.45825756949558394) <= 4 * Eps, $"Id {id:R}");
        (double id2, double iq2) = VscQuasiSteady.Limit(2.0, 1.0, 1.1, VscCurrentPriority.Active);
        Assert.Equal(1.1, id2);
        Assert.True(Math.Abs(iq2) <= 1e-7);
        // Through the link state at 0.5 pu: the converter's current magnitude stays at its limit.
        (VscDcLink link, VscLinkResult pf) = PlainVsc();
        var s = new VscLinkState(new VscLinkPlan(link, pf, new Vsc1Data(0.05, 0.02, 1.1, 100), 100, Complex.One, Complex.One), null);
        Complex i = s.TrialCurrent(0, new Complex(0.5, 0));
        Assert.True(i.Magnitude <= 1.1 * (1 + 1e-15));
        Assert.Equal(-1.1, i.Real, 15);   // Id_cmd = -0.8 / 0.5 = -1.6, clipped at -1.1 (no reactive order)
    }

    // ------------------------------------------------------------------------------------------------ 10. DYR

    /// <summary>The LCC case of <see cref="PsseLccTests"/> (LINK A from bus 2 to bus 3) with a 0 MW unit 'S' at the rectifier
    /// bus to carry the SVC model.</summary>
    private static string Raw() => PsseLccTests.Case().Replace("0 / END OF GENERATOR DATA",
        "2, 'S', 0, 0, 50, -50, 1, 0, 100, 0, 1, 0, 0, 1, 1, 100, 0, 0, 1, 1\n0 / END OF GENERATOR DATA", StringComparison.Ordinal);

    private const string Genrou = "  1 'GENROU' 1  6 0.05 0.2 0.05 3 0 1.8 1.7 0.3 0.55 0.25 0.2 0 0 /\n";
    private const string Csvgn1 = "  2 'CSVGN1' S  25 0 0 0 0 0.05 0 1 0 50 /\n";
    private const string Cdc4t = "'LINK A' 'CDC4T' 5 18 0.01 0.02 0.5 0.8 0.1 0 0 0 0 0 0 0 20 100 50 300 150 450 300 0 /\n";
    private const string GenclsRec = "  1 'GENCLS' 1  5 0 /\n";

    private static PsseDyrResult Read(string dyr, bool extended = true, bool allow = false) =>
        PsseDyrReader.Read(dyr, PsseRawParser.ParseWithDiagnostics(Raw(), "wp6"), "wp6", new PsseDyrOptions { ExtendedLibrary = extended, AllowUnmodelled = allow });

    [Fact]
    public void Dyr_reads_csvgn1_and_cdc4t_into_the_documented_parameters()
    {
        PsseDyrResult r = Read(GenclsRec + Csvgn1 + Cdc4t);
        var svc = Assert.IsType<Csvgn1Data>(Assert.Single(r.Facts).Value);
        Assert.Equal(1, r.Facts.Single().Key);
        Assert.Equal(new Csvgn1Data(25, 0, 0, 0, 0, 0.05, 0, 1, 0, 50, 100), svc);
        var lcc = Assert.IsType<LccLinkDynamicsData>(Assert.Single(r.NetworkDevices));
        Assert.Equal("LINK A", lcc.DeviceName);
        Lcc1Data d = lcc.Lcc;
        Assert.Equal((5.0, 18.0, 0.01, 0.02, 0.5, 0.8, 0.1), (d.AlphaMinDeg, d.GammaMinDeg, d.Tvdc, d.Tidc, d.VblockPu, d.VunblockPu, d.TblockSeconds));
        Assert.Equal((0.0, 0.0, 20.0), (d.VrampPuPerSecond, d.CrampPuPerSecond, d.C0Amps));
        Assert.Equal((100.0, 50.0, 300.0, 150.0, 450.0, 300.0), (d.V1Kv, d.C1Amps, d.V2Kv, d.C2Amps, d.V3Kv, d.C3Amps));
        Assert.Single(r.DcRecords);
    }

    [Fact]
    public void Dyr_refuses_wrong_counts_unmodelled_values_and_the_models_it_does_not_read()
    {
        Assert.Contains("CSVGN1 needs exactly 10 values", Assert.Throws<InvalidDataException>(() => Read(GenclsRec + "  2 'CSVGN1' S  25 0 0 0 0 0.05 0 1 0 /\n")).Message, StringComparison.Ordinal);
        Assert.Contains("CDC4T needs exactly 22 values", Assert.Throws<InvalidDataException>(() => Read(GenclsRec + "'LINK A' 'CDC4T' 5 18 /\n")).Message, StringComparison.Ordinal);
        string svsmo3 = Assert.Throws<InvalidDataException>(() => Read(GenclsRec + "  2 'SVSMO3' S  1 2 3 /\n")).Message;
        Assert.Contains("SVSMO3", svsmo3, StringComparison.Ordinal);
        Assert.Contains("one public source", svsmo3, StringComparison.Ordinal);
        foreach (string m in new[] { "SVSMO1", "CSTCON" })
            Assert.Contains(m, Assert.Throws<InvalidDataException>(() => Read(GenclsRec + $"  2 '{m}' S  1 2 3 /\n")).Message, StringComparison.Ordinal);
        string vscdct = Assert.Throws<InvalidDataException>(() => Read(GenclsRec + "'VDC 1' 'VSCDCT' 1 2 3 /\n")).Message;
        Assert.Contains("VSCDCT", vscdct, StringComparison.Ordinal);
        Assert.Contains("record order not public", vscdct, StringComparison.Ordinal);
        Assert.Contains("quoted DC line name", Assert.Throws<InvalidDataException>(() => Read(GenclsRec + "  2 'CDC4T' 1 5 18 /\n")).Message, StringComparison.Ordinal);
        Assert.Contains("does not hold as a two-terminal DC line", Assert.Throws<InvalidDataException>(() => Read(GenclsRec + Cdc4t.Replace("LINK A", "LINK B", StringComparison.Ordinal))).Message, StringComparison.Ordinal);
        // A non-zero VBYPAS: refused, or left out (and listed) with unmodelled dynamics allowed.
        string bypass = Cdc4t.Replace("0.1 0 0 0", "0.1 250 0 0", StringComparison.Ordinal);
        Assert.Contains("VBYPAS", Assert.Throws<InvalidDataException>(() => Read(GenclsRec + bypass)).Message, StringComparison.Ordinal);
        PsseDyrResult allowed = Read(GenclsRec + bypass, allow: true);
        Assert.Single(allowed.NetworkDevices);
        Assert.Contains(allowed.Notes, n => n.Contains("VBYPAS", StringComparison.Ordinal));
        // A CSVGN1 on a unit with a machine record is refused.
        Assert.Contains("an SVC unit takes no machine", Assert.Throws<InvalidDataException>(() => Read(GenclsRec + Csvgn1 + "  2 'GENCLS' S  5 0 /\n")).Message, StringComparison.Ordinal);
    }

    /// <summary>Without --dyr-extended the reader is the 2.1.082 one, error for error: these two messages were produced by the
    /// 2.1.082 build from the same RAW and DYR text.</summary>
    [Fact]
    public void Without_the_extended_library_the_dyr_errors_are_the_old_ones()
    {
        string a = Assert.Throws<InvalidDataException>(() => Read(Genrou + Csvgn1, extended: false)).Message;
        Assert.Equal("PSS/E DYR the DYR file: 1 item(s) GridSim cannot represent exactly:" + Environment.NewLine
            + "  models this reader does not map: CSVGN1 x1 (first at line 2)" + Environment.NewLine
            + "Read it with unmodelled dynamics allowed (gridsim: --allow-unmodelled) to leave them out and use the rest.", a);
        string b = Assert.Throws<InvalidDataException>(() => Read(Genrou + Cdc4t, extended: false)).Message;
        Assert.Equal("PSS/E RAW line 2 (DYR data): field 1 (IBUS) is 'LINK A'; it must be a finite number.", b);
    }

    // ------------------------------------------------------------------------------------------------ refusals

    [Fact]
    public void Device_binding_refuses_what_it_cannot_honour()
    {
        GridModel m = Sink();
        PowerFlowResult pf = SinkPf(m);
        void Refused(string contains, params RmsNetworkDeviceData[] devices)
        {
            var ex = Assert.ThrowsAny<ArgumentException>(() => SubtransientInit.FromSolvedCase(m, pf, new RmsLoadedMachineData(SinkData(), Array.Empty<DynamicLoadData>(), devices)));
            Assert.Contains(contains, ex.Message, StringComparison.Ordinal);
        }
        Refused("has no LCC link named 'L9'", new LccLinkDynamicsData("L9", SinkLcc()));
        Refused("given a model twice", new SvcDeviceData("svc1", Svc()), new SvcDeviceData("svc1", Svc()));
        // CBASE 10 Mvar cannot reach the power flow's 20 Mvar: x0 = (0.1 - 0.2) / 1 < 0.
        Refused("outside its dynamic range", new SvcDeviceData("svc1", Svc(cbase: 10)));
        // 0.2 pu of reactive current against a 0.1 limit.
        Refused("outside its limits", new StatcomDeviceData("st1", new Statcom1Data(100, 0.03, 0, 50, 0.01, 0.1, 1.0)));
        // The power-flow current (about 199 A at a measured 502.7 kV) above the VDCOL limit: with V2 = 550 kV the limit at
        // 502.7 kV is C1 + (C2 - C1)(502.7 - 100) / 450 = 94.7 A.
        Refused("VDCOL", new LccLinkDynamicsData("L1", SinkLcc() with { V3Kv = 600, C3Amps = 100, V2Kv = 550, C2Amps = 100 }));
        // A generator-held SVC must be scheduled at 0 MW.
        (GridModel s2, PowerFlowResult spf2) = SystemS(devicePg: 10);
        Assert.Contains("must be 0", Assert.Throws<ArgumentException>(() => SubtransientInit.FromSolvedCase(s2, spf2, SData(Svc()))).Message, StringComparison.Ordinal);
        // The adaptive-step study refuses a case with device models (converter plant), as it refuses every converter.
        Assert.Throws<ArgumentException>(() => RmsTransientStability.StudyEventsAdaptive(m, pf,
            new RmsLoadedMachineData(SinkData(), Array.Empty<DynamicLoadData>(), SinkDevices()), new RmsEvent[] { new RmsLoadStep(0.1, 6, 0, 10) },
            new AdaptiveOptions(RelativeTolerance: 1e-6, AbsoluteTolerance: 1e-9, InitialStepSeconds: 1e-3, MaxStepSeconds: 0.1), durationSeconds: 0.5));
    }

    // ------------------------------------------------------------------------------------------------ 11, 12. bits

    private static string Digest(MultiMachineSubtransient.Result r, bool devices = false)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            foreach (MultiMachineSubtransient.MachineTrace m in r.Machines)
                foreach (IReadOnlyList<double> s in new[] { m.DeltaRad, m.SpeedPu, m.EqpPu, m.EdpPu, m.EqppPu, m.EddpPu })
                    foreach (double v in s) w.Write(BitConverter.DoubleToInt64Bits(v));
            foreach (RmsConverterTrace c in r.Converters ?? Array.Empty<RmsConverterTrace>())
                foreach (IReadOnlyList<double> s in new[] { c.TerminalVoltagePu, c.ActivePowerPu, c.ReactivePowerPu })
                    foreach (double v in s) w.Write(BitConverter.DoubleToInt64Bits(v));
            if (devices)
                foreach (RmsDeviceTrace d in r.Devices ?? Array.Empty<RmsDeviceTrace>())
                    foreach (double[] s in d.Samples)
                        foreach (double v in s) w.Write(BitConverter.DoubleToInt64Bits(v));
        }
        return Convert.ToHexString(SHA256.HashData(ms.ToArray()));
    }

    /// <summary>The kitchen-sink case (an LCC link, a VSC link, a compensator and a STATCOM) through the RMS study with no
    /// device data: the devices are left out, as they always were. The digest of the machine traces was computed with the
    /// 2.1.082 build (the build before WP6) from the same case, data and event.</summary>
    [Fact]
    public void Without_device_data_an_hvdc_and_facts_case_gives_the_old_bits()
    {
        const string Before = "8E379F057FEA2B2C066E2ECFE48203510DB765F1449A8D4B2BA47CE8DF10B0D1";
        GridModel m = Sink();
        PowerFlowResult pf = SinkPf(m);
        var ev = new RmsEvent[] { new RmsLoadStep(0.1, 6, 0, 10) };
        Assert.Equal(Before, Digest(RmsTransientStability.StudyEvents(m, pf, SinkData(), ev, stepSeconds: 0.005, durationSeconds: 1.0)));
        Assert.Equal(Before, Digest(RmsTransientStability.StudyEvents(m, pf, new RmsLoadedMachineData(SinkData(), Array.Empty<DynamicLoadData>()), ev,
            stepSeconds: 0.005, durationSeconds: 1.0)));
        Assert.Equal(Before, Digest(RmsTransientStability.StudyEvents(m, pf, new RmsLoadedMachineData(SinkData(), Array.Empty<DynamicLoadData>(),
            Array.Empty<RmsNetworkDeviceData>()), ev, stepSeconds: 0.005, durationSeconds: 1.0)));
    }

    [Fact]
    public void Repeated_and_parallel_runs_with_devices_give_the_same_bits()
    {
        GridModel m = Sink();
        PowerFlowResult pf = SinkPf(m);
        var data = new RmsLoadedMachineData(SinkData(), Array.Empty<DynamicLoadData>(), SinkDevices());
        var ev = new RmsEvent[] { new RmsLoadStep(0.1, 6, 0, 10), new RmsBusFault(0.2, 2), new RmsFaultClear(0.25, 2) };
        string Go() => Digest(RmsTransientStability.StudyEvents(m, pf, data, ev, stepSeconds: 0.005, durationSeconds: 1.0), devices: true);
        string want = Go();
        Assert.Equal(want, Go());
        var par = new string[4];
        Parallel.For(0, par.Length, i => par[i] = Go());
        Assert.All(par, d => Assert.Equal(want, d));
        // The fault at the rectifier bus blocked the link (VBLOCK 0.5) and it came back.
        RmsDeviceTrace lcc = RmsTransientStability.StudyEvents(m, pf, data, ev, stepSeconds: 0.005, durationSeconds: 1.0).Devices!.Single(d => d.Name == "L1");
        Assert.Contains(lcc.Samples, s => s[3] == 1.0);
        Assert.Equal(0.0, lcc.Samples[^1][3]);
    }

    /// <summary>The built CLI in the three SIMD modes the release gate runs gives the same machine and device rows for the LCC
    /// case with a GENCLS, a CSVGN1 unit at the rectifier bus and a CDC4T link, through a fault at the rectifier bus.</summary>
    [Fact]
    public void The_cli_dyr_extended_run_with_devices_is_the_same_bits_in_every_simd_mode()
    {
        string root = TestTemp.Under("gridsim-wp6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string rawPath = Path.Combine(root, "lcc.raw"), dyrPath = Path.Combine(root, "lcc.dyr");
            File.WriteAllText(rawPath, Raw());
            File.WriteAllText(dyrPath, GenclsRec + Csvgn1 + Cdc4t);
            var modes = new (string Name, Dictionary<string, string?> Env)[]
            {
                ("default", new Dictionary<string, string?>()),
                ("noavx2", new Dictionary<string, string?> { ["DOTNET_EnableAVX2"] = "0" }),
                ("nohw", new Dictionary<string, string?> { ["DOTNET_EnableHWIntrinsic"] = "0" }),
            };
            string? want = null;
            foreach ((string name, Dictionary<string, string?> env) in modes)
            {
                string outDir = Path.Combine(root, name);
                CliProcess.Result r = CliProcess.RunBuiltWithEnvironment(env, "stability", rawPath, "--rms", "--dyn-data", dyrPath, "--dyr-extended",
                    "--events", "fault:2@0.1,clear:2@0.15", "--events-out", outDir);
                Assert.True(r.ExitCode == 0, r.ToString());
                Assert.Contains("2 device model(s) from the dynamics file", r.StdOut, StringComparison.Ordinal);
                string rows = string.Join("\n", File.ReadAllLines(Path.Combine(outDir, RmsEventsExport.MachinesCsv)).Where(l => !l.StartsWith('#')))
                    + "\n" + string.Join("\n", File.ReadAllLines(Path.Combine(outDir, RmsEventsExport.DevicesCsv)).Where(l => !l.StartsWith('#')));
                Assert.Contains("LCC1", rows, StringComparison.Ordinal);
                want ??= rows;
                Assert.Equal(want, rows);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
