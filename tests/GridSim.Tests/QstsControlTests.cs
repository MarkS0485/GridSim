using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Core.Qsts;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The timed controllers of the QSTS engine against analytic expectations: OLTC deadband, definite and inverse delays,
/// continuous vs sequential timing, catch-up at coarse steps, tap counting and line drop compensation; capacitor
/// voltage / var / time control with delays and dead time; inverter volt-var, volt-watt and power-factor functions at
/// their IEEE 1547-2018 break points; storage SOC energy balance to the bit.
/// </summary>
public sealed class QstsControlTests
{
    private static readonly PowerFlowOptions Opt = QstsEngineTests.Opt;

    private static QstsResult Run(GridModel m, QstsStudy s) => new QstsEngine(new BackwardForwardSweep(), Opt).Run(m, s);

    // ---- OLTC ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_oltc_inside_its_deadband_never_operates()
    {
        GridModel m = QstsEngineTests.Feeder(target: 1.0, deadband: 0.05);
        QstsResult q = Run(m, new QstsStudy { StepSeconds = 1, Steps = 120, OltcDefault = new OltcTiming(DelaySeconds: 30) });
        Assert.True(q.Completed);
        Assert.Equal(0, q.TotalTapOperations);
        Assert.Empty(q.Operations);
    }

    private static int TapsNeeded()
    {
        var loop = new SteadyStateSolver(new BackwardForwardSweep());
        loop.Solve(QstsEngineTests.Feeder(), Opt);
        return loop.OltcPasses;
    }

    [Fact]
    public void Definite_delay_continuous_timing_first_tap_at_the_delay_then_at_the_inter_tap_delay()
    {
        int n = TapsNeeded();
        Assert.True(n >= 3, $"the feeder needs several taps (needs {n})");
        QstsResult q = Run(QstsEngineTests.Feeder(), new QstsStudy { StepSeconds = 1, Steps = 120, OltcDefault = new OltcTiming(DelaySeconds: 30, InterTapSeconds: 5) });
        Assert.Equal(n, q.TapOperations[0]);
        Assert.Equal(n, q.Operations.Count);
        for (int i = 0; i < n; i++) Assert.Equal(30L + 5L * i, q.Operations[i].TimeSeconds);
        // The final tap is the steady-state loop's (same step rule).
        var loop = new SteadyStateSolver(new BackwardForwardSweep());
        loop.Solve(QstsEngineTests.Feeder(), Opt);
        Assert.Equal(loop.RegulatedModel.Branches[0].TapRatio, q.Steps[^1].Taps[0]);
        // The log chains: each operation starts where the previous one ended.
        for (int i = 1; i < n; i++) Assert.Equal(q.Operations[i - 1].To, q.Operations[i].From);
    }

    [Fact]
    public void Sequential_timing_waits_the_full_delay_before_every_tap()
    {
        int n = TapsNeeded();
        QstsResult q = Run(QstsEngineTests.Feeder(), new QstsStudy
        {
            StepSeconds = 1, Steps = 30 * n + 5,
            OltcDefault = new OltcTiming(DelaySeconds: 30, InterTapSeconds: 5, Sequence: OltcSequence.Sequential),
        });
        Assert.Equal(n, q.TapOperations[0]);
        for (int i = 0; i < n; i++) Assert.Equal(30L * (i + 1), q.Operations[i].TimeSeconds);
    }

    [Fact]
    public void Inverse_time_acts_sooner_by_the_deviation_over_the_half_band()
    {
        GridModel m = QstsEngineTests.Feeder(target: 1.03, deadband: 0.005);
        // The regulated voltage before any tap (the plain power flow), and the weight |v - target| / half-band.
        PowerFlowResult r0 = new BackwardForwardSweep().Solve(m, Opt);
        double v0 = r0.Vm[1];
        double w = Math.Abs(v0 - 1.03) / 0.005;
        Assert.True(w > 2, $"the deviation must be several half-bands (w = {w})");
        // Accumulate exactly as the engine does (one addition of 1 s x w per step) and find the first step that covers 60 s.
        double acc = 0; int k = 0;
        while (!(acc >= 60.0)) { acc += 1.0 * w; k++; }
        QstsResult q = Run(m, new QstsStudy { StepSeconds = 1, Steps = 120, OltcDefault = new OltcTiming(DelaySeconds: 60, InterTapSeconds: 1000, Characteristic: OltcCharacteristic.Inverse) });
        Assert.Equal((long)k, q.Operations[0].TimeSeconds);
        Assert.True(q.Operations[0].TimeSeconds < 60);
    }

    [Fact]
    public void Inverse_time_never_acts_faster_than_its_minimum_delay()
    {
        GridModel m = QstsEngineTests.Feeder(target: 1.03, deadband: 0.005);
        QstsResult q = Run(m, new QstsStudy { StepSeconds = 1, Steps = 120, OltcDefault = new OltcTiming(DelaySeconds: 60, InterTapSeconds: 1000, Characteristic: OltcCharacteristic.Inverse, MinDelaySeconds: 40) });
        Assert.Equal(40L, q.Operations[0].TimeSeconds);   // weight capped at 60 / 40 = 1.5, exact: 40 additions of 1.5 = 60
    }

    [Fact]
    public void At_coarse_steps_a_regulator_catches_up_on_the_operations_it_would_have_made_in_the_interval()
    {
        int n = TapsNeeded();
        QstsResult q = Run(QstsEngineTests.Feeder(), new QstsStudy { StepSeconds = 900, Steps = 3, OltcDefault = new OltcTiming(DelaySeconds: 30, InterTapSeconds: 5) });
        Assert.DoesNotContain(q.Operations, o => o.Step == 0);   // armed at step 0, no operation yet
        Assert.Equal(n, q.Operations.Count(o => o.Step == 1));
        Assert.All(q.Operations, o => Assert.Equal(900L, o.TimeSeconds));
    }

    [Fact]
    public void Tap_count_is_the_number_of_logged_operations_over_a_day()
    {
        QstsResult q = new QstsEngine(new BackwardForwardSweep(), Opt).Run(QstsEngineTests.DayFeeder(), QstsEngineTests.DayStudy());
        Assert.True(q.Completed);
        Assert.Equal(q.Operations.Count(o => o.Kind == "oltc"), q.TotalTapOperations);
        Assert.Equal(q.Operations.Count(o => o.Kind == "capacitor"), q.TotalCapacitorOperations);
        Assert.True(q.TotalTapOperations > 0, "the day must move the tap");
    }

    [Fact]
    public void Line_drop_compensation_regulates_V_minus_Z_I_at_the_regulated_end()
    {
        GridModel m = QstsEngineTests.Feeder(target: 1.06, deadband: 0.002);
        const double R = 0.04, X = 0.08;
        PowerFlowResult r0 = new BackwardForwardSweep().Solve(m, Opt);
        // By hand: S leaving T1 into bus 2 = -(P_to + j Q_to) / base; I = conj(S / V); V_comp = V - (R + jX) I.
        LineFlow lf = r0.LineFlows[0];
        double v = r0.Vm[1], va = r0.VaDeg[1] * (Math.PI / 180.0);
        (double sin, double cos) = DetMath.SinCos(va);
        System.Numerics.Complex V = new(v * cos, v * sin);
        System.Numerics.Complex S = new(-lf.PToMw / m.BaseMva, -lf.QToMvar / m.BaseMva);
        System.Numerics.Complex I = System.Numerics.Complex.Conjugate(S / V);
        double expected = (V - new System.Numerics.Complex(R, X) * I).Magnitude;
        QstsResult q = Run(m, new QstsStudy
        {
            StepSeconds = 60, Steps = 1,
            OltcSettings = new Dictionary<string, OltcTiming> { ["T1"] = new OltcTiming(LdcRPu: R, LdcXPu: X) },
        });
        QstsOperation first = q.Operations[0];
        Assert.Equal(expected, first.Sensed, 14);   // the engine's real-arithmetic phasor vs System.Numerics: last-digit level
        Assert.True(first.Sensed < v, "compensation lowers the regulated voltage under load");
        // With LDC the regulator raises the bus further than without it.
        QstsResult plain = Run(m, new QstsStudy { StepSeconds = 60, Steps = 1 });
        Assert.True(q.Steps[0].Taps[0] < plain.Steps[0].Taps[0]);
    }

    // ---- capacitors -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_voltage_controlled_bank_switches_in_after_its_delay()
    {
        GridModel m = QstsEngineTests.Feeder(oltc: false, loadMw: 1.6);
        var cap = new QstsCapacitor("C6", 6, 0.3, 0, 3, 0, CapacitorControl.Voltage, 0.97, 1.05, DelaySeconds: 45);
        QstsResult q = Run(m, new QstsStudy { StepSeconds = 1, Steps = 200, Capacitors = new[] { cap } });
        Assert.True(q.Operations.Count >= 1);
        Assert.Equal(45L, q.Operations[0].TimeSeconds);
        if (q.Operations.Count > 1) Assert.Equal(90L, q.Operations[1].TimeSeconds);   // a new excursion timer after each step
        Assert.True(q.Operations[0].Sensed < 0.97);
    }

    [Fact]
    public void A_bank_does_not_switch_back_in_inside_its_discharge_dead_time()
    {
        // Load profile: heavy (bank in), very light (bank out on high voltage), heavy again (bank wants in).
        GridModel m = QstsEngineTests.Feeder(oltc: false, loadMw: 1.6);
        var prof = new QstsProfile("p", 60, new[] { 1.0, 0.05, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0 });
        var cap = new QstsCapacitor("C6", 6, 0.6, 0, 1, 0, CapacitorControl.Voltage, 0.97, 1.0, DeadTimeSeconds: 300);
        QstsResult q = Run(m, new QstsStudy { StepSeconds = 60, Steps = 10, Profiles = new[] { prof }, Loads = new[] { new QstsLoadBinding(null, "p") }, Capacitors = new[] { cap } });
        QstsOperation[] ops = q.Operations.ToArray();
        Assert.Equal(0L, ops[0].TimeSeconds); Assert.Equal(1.0, ops[0].To);     // in at once (no delay)
        Assert.Equal(60L, ops[1].TimeSeconds); Assert.Equal(0.0, ops[1].To);    // out on the light-load high voltage
        Assert.Equal(360L, ops[2].TimeSeconds); Assert.Equal(1.0, ops[2].To);   // back in only 300 s after switching out
    }

    [Fact]
    public void A_time_controlled_bank_is_in_between_its_on_and_off_times()
    {
        GridModel m = QstsEngineTests.Feeder(oltc: false);
        var cap = new QstsCapacitor("C4", 4, 0.2, 0, 2, 0, CapacitorControl.Time, 8 * 3600, 20 * 3600);
        QstsResult q = Run(m, new QstsStudy { StepSeconds = 900, Steps = 96, Capacitors = new[] { cap } });
        foreach (QstsStepRecord s in q.Steps)
        {
            bool inside = s.TimeSeconds >= 8 * 3600 && s.TimeSeconds < 20 * 3600;
            Assert.Equal(inside ? 2 : 0, s.CapacitorSteps[0]);
        }
        Assert.Equal(4, q.CapacitorOperations[0]);
    }

    [Fact]
    public void A_var_controlled_bank_switches_on_the_monitored_branch_reactive_flow()
    {
        GridModel m = QstsEngineTests.Feeder(oltc: false);
        PowerFlowResult r0 = new BackwardForwardSweep().Solve(m, Opt);
        double q0 = r0.LineFlows[2].QFromMvar;   // L34
        var cap = new QstsCapacitor("C5", 5, 0.4, 0, 2, 0, CapacitorControl.Var, q0 - 0.1, q0 - 1.5, MonitoredBranch: "L34");
        QstsResult q = Run(m, new QstsStudy { StepSeconds = 60, Steps = 1, Capacitors = new[] { cap } });
        Assert.True(q.Steps[0].CapacitorSteps[0] >= 1);
        Assert.Equal(q0, q.Operations[0].Sensed);
    }

    // ---- inverters ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Volt_var_curves_take_the_ieee_1547_values_exactly_at_their_break_points()
    {
        VoltVarCurve b = VoltVarCurve.Ieee1547CategoryB;
        Assert.Equal(0.44, b.At(0.92)); Assert.Equal(0.44, b.At(0.80));
        Assert.Equal(0.0, b.At(0.98)); Assert.Equal(0.0, b.At(1.00)); Assert.Equal(0.0, b.At(1.02));
        Assert.Equal(-0.44, b.At(1.08)); Assert.Equal(-0.44, b.At(1.20));
        Assert.Equal(0.22, b.At(0.95), 15);   // midway down the injecting slope
        VoltVarCurve a = VoltVarCurve.Ieee1547CategoryA;
        Assert.Equal(0.25, a.At(0.90)); Assert.Equal(0.0, a.At(1.0)); Assert.Equal(-0.25, a.At(1.10));
        Assert.Equal(-0.125, a.At(1.05), 15);
        VoltWattCurve w = VoltWattCurve.Ieee1547Default;
        Assert.Equal(1.0, w.At(1.06)); Assert.Equal(1.0, w.At(1.0)); Assert.Equal(0.0, w.At(1.10)); Assert.Equal(0.5, w.At(1.08), 14);
    }

    private static GridModel LightFeeder() => QstsEngineTests.Feeder(oltc: false, loadMw: 0.2);

    [Fact]
    public void Volt_var_settles_on_its_curve_at_the_solved_voltage()
    {
        GridModel m = LightFeeder();
        var pv = new QstsPvUnit("PV6", 6, 4.0, 4.4, null, new InverterSettings(InverterFunction.VoltVar));
        QstsResult q = Run(m, new QstsStudy { StepSeconds = 60, Steps = 1, Pv = new[] { pv } });
        Assert.True(q.Completed && q.Steps[0].Settled);
        double v6 = q.Steps[0].Vm![5];
        Assert.True(v6 > 1.02, $"export must raise bus 6 into the absorbing slope (v6 = {v6})");
        double expected = VoltVarCurve.Ieee1547CategoryB.At(v6) * 4.4;
        Assert.True(Math.Abs(q.Steps[0].PvQ[0] - expected) <= 1e-6 * 4.4, $"Q {q.Steps[0].PvQ[0]} vs curve {expected}");
        Assert.True(q.Steps[0].PvQ[0] < 0, "absorbing");
        // Absorbing lowers the voltage against unity power factor.
        QstsResult unity = Run(m, new QstsStudy { StepSeconds = 60, Steps = 1, Pv = new[] { pv with { Inverter = new InverterSettings() } } });
        Assert.True(unity.Steps[0].Vm![5] > v6);
    }

    [Fact]
    public void Volt_watt_curtails_active_power_on_its_curve()
    {
        GridModel m = LightFeeder();
        var pv = new QstsPvUnit("PV6", 6, 8.0, 8.0, null, new InverterSettings(InverterFunction.VoltWatt, VoltWatt: new VoltWattCurve(1.03, 1.0, 1.07, 0.0)));
        QstsResult q = Run(m, new QstsStudy { StepSeconds = 60, Steps = 1, Pv = new[] { pv } });
        Assert.True(q.Completed && q.Steps[0].Settled);
        double v6 = q.Steps[0].Vm![5];
        double cap = new VoltWattCurve(1.03, 1.0, 1.07, 0.0).At(v6) * 8.0;
        Assert.True(Math.Abs(q.Steps[0].PvP[0] - cap) <= 1e-6 * 8.0, $"P {q.Steps[0].PvP[0]} vs cap {cap} at v {v6}");
        Assert.True(q.Steps[0].PvP[0] < 8.0);
        Assert.True(q.Energy.PvCurtailedMwh > 0);
    }

    [Fact]
    public void Fixed_power_factor_absorbs_P_tan_phi()
    {
        GridModel m = LightFeeder();
        var pv = new QstsPvUnit("PV6", 6, 2.0, 2.5, null, new InverterSettings(InverterFunction.FixedPowerFactor, PowerFactor: 0.95));
        QstsResult q = Run(m, new QstsStudy { StepSeconds = 60, Steps = 1, Pv = new[] { pv } });
        Assert.Equal(2.0, q.Steps[0].PvP[0]);
        Assert.Equal(-2.0 * Math.Sqrt(1 - 0.95 * 0.95) / 0.95, q.Steps[0].PvQ[0]);
    }

    [Fact]
    public void Var_priority_curtails_P_to_keep_Q_within_the_rating()
    {
        GridModel m = QstsEngineTests.Feeder(oltc: false, loadMw: 0.1);
        // Rated 4 MW / 4 MVA: at full sun, Q from volt-var means P must give way under var priority.
        var pv = new QstsPvUnit("PV6", 6, 4.0, 4.0, null, new InverterSettings(InverterFunction.VoltVar, VoltVar: new VoltVarCurve(0.9, 0.5, 0.95, 0, 0.96, 0, 0.97, -0.5)));
        QstsResult q = Run(m, new QstsStudy { StepSeconds = 60, Steps = 1, Pv = new[] { pv } });
        double p = q.Steps[0].PvP[0], qq = q.Steps[0].PvQ[0];
        Assert.Equal(-2.0, qq);                                   // -0.5 x 4 MVA, flat beyond V4
        Assert.Equal(Math.Sqrt(16.0 - 4.0), p);                   // sqrt(S^2 - Q^2)
        QstsResult watt = Run(m, new QstsStudy { StepSeconds = 60, Steps = 1, Pv = new[] { pv with { Inverter = pv.Inverter with { Priority = InverterPriority.Watt } } } });
        Assert.Equal(4.0, watt.Steps[0].PvP[0]);
        Assert.Equal(0.0, watt.Steps[0].PvQ[0]);                  // no room left at full P
    }

    [Fact]
    public void The_response_time_lags_volt_var_by_one_minus_ten_to_the_minus_dt_over_T()
    {
        GridModel m = LightFeeder();
        var prof = new QstsProfile("sun", 1, new[] { 0.0, 1.0, 1.0, 1.0 });
        var pv = new QstsPvUnit("PV6", 6, 4.0, 4.4, "sun", new InverterSettings(InverterFunction.VoltVar, ResponseSeconds: 5));
        QstsResult q = Run(m, new QstsStudy { StepSeconds = 1, Steps = 4, Profiles = new[] { prof }, Pv = new[] { pv } });
        double beta = 1.0 - DetMath.Exp(-1.0 * DetMath.Log(10.0) / 5.0);
        // Step 1: Q = Q0 + beta (curve(V1) - Q0) at the settled V1.
        double q0 = q.Steps[0].PvQ[0];
        double target = VoltVarCurve.Ieee1547CategoryB.At(q.Steps[1].Vm![5]) * 4.4;
        Assert.True(Math.Abs(q.Steps[1].PvQ[0] - (q0 + beta * (target - q0))) <= 1e-6 * 4.4);
        Assert.True(Math.Abs(q.Steps[1].PvQ[0]) < Math.Abs(target));
    }

    // ---- storage ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Storage_state_of_charge_follows_the_energy_balance_to_the_bit()
    {
        GridModel m = QstsEngineTests.Feeder(oltc: false);
        // Dyadic numbers (0.5 h steps, efficiencies 0.5 and 1, powers in quarters of a MW) make every term exact.
        var sched = new QstsProfile("sched", 1800, new[] { 1.0, 0.5, -1.0, -0.25, 0.75, 0.0 });
        var st = new QstsStorageUnit("B3", 3, 1.0, 4.0, 0.5, 0.0, 1.0, 0.5, 1.0, StorageMode.Schedule, ScheduleProfile: "sched");
        QstsResult q = Run(m, new QstsStudy { StepSeconds = 1800, Steps = 6, Profiles = new[] { sched }, Storage = new[] { st } });
        double e = 2.0;
        double[] expectE = { 2.0, 1.5, 1.25, 1.5, 1.5625, 1.1875 };
        for (int k = 0; k < 6; k++)
        {
            Assert.Equal(expectE[k], q.Steps[k].Soc[0] * 4.0);
            Assert.Equal(sched.Values[k], q.Steps[k].StorageP[0]);
            double p = sched.Values[k];
            e = p > 0 ? e - p / 1.0 * 0.5 : p < 0 ? e + 0.5 * -p * 0.5 : e;
        }
        Assert.Equal(e / 4.0, q.FinalSoc[0]);
        Assert.Equal(1.1875 / 4.0, q.FinalSoc[0]);
        Assert.Equal(0.5 * (1.0 + 0.5 + 0.75), q.Energy.StorageDischargeMwh);
        Assert.Equal(0.5 * (1.0 + 0.25), q.Energy.StorageChargeMwh);
    }

    [Fact]
    public void Storage_never_discharges_below_its_minimum_state_of_charge()
    {
        GridModel m = QstsEngineTests.Feeder(oltc: false);
        var sched = new QstsProfile("sched", 1800, new[] { 1.0, 1.0, 1.0, 1.0 });
        var st = new QstsStorageUnit("B3", 3, 1.0, 2.0, 0.5, 0.25, 1.0, 1.0, 1.0, StorageMode.Schedule, ScheduleProfile: "sched");
        QstsResult q = Run(m, new QstsStudy { StepSeconds = 1800, Steps = 4, Profiles = new[] { sched }, Storage = new[] { st } });
        // E0 = 1 MWh, Emin = 0.5 MWh: one half-hour at 1 MW empties the usable 0.5 MWh; nothing after.
        Assert.Equal(1.0, q.Steps[0].StorageP[0]);
        Assert.Equal(0.0, q.Steps[1].StorageP[0]);
        Assert.Equal(0.25, q.FinalSoc[0]);
    }

    [Fact]
    public void Peak_shaving_holds_the_monitored_flow_at_its_threshold()
    {
        GridModel m = QstsEngineTests.Feeder(oltc: false);
        PowerFlowResult r0 = new BackwardForwardSweep().Solve(m, Opt);
        double p0 = r0.LineFlows[0].PFromMw;
        double high = p0 - 0.5;
        var st = new QstsStorageUnit("B6", 6, 2.0, 4.0, 0.5, 0.1, 0.9, 0.95, 0.95, StorageMode.PeakShave, MonitoredBranch: "T1", HighMw: high, LowMw: high - 2.0);
        QstsResult q = Run(m, new QstsStudy { StepSeconds = 900, Steps = 2, Storage = new[] { st } });
        Assert.True(q.Completed && q.Steps[0].Settled);
        double flow = q.LastResult!.LineFlows[0].PFromMw;
        Assert.True(Math.Abs(flow - high) <= 1e-6, $"flow {flow} vs threshold {high}");
        // Discharging at the feeder end also saves losses upstream, so a little under 0.5 MW removes 0.5 MW of flow.
        Assert.True(q.Steps[0].StorageP[0] > 0.4 && q.Steps[0].StorageP[0] < 0.5, $"storage {q.Steps[0].StorageP[0]} MW");
        Assert.True(q.Steps[1].Soc[0] < 0.5);
    }

    [Fact]
    public void Self_consumption_storage_covers_its_bus_demand_net_of_pv()
    {
        GridModel m = QstsEngineTests.Feeder(oltc: false);
        var pv = new QstsPvUnit("PV4", 4, 3.0, 3.0, null, new InverterSettings());
        var st = new QstsStorageUnit("B4", 4, 5.0, 10.0, 0.5, 0.0, 1.0, 1.0, 1.0, StorageMode.SelfConsumption);
        QstsResult q = Run(m, new QstsStudy { StepSeconds = 900, Steps = 1, Pv = new[] { pv }, Storage = new[] { st } });
        Assert.Equal(1.0 - 3.0, q.Steps[0].StorageP[0]);   // charges the 2 MW surplus
    }
}
