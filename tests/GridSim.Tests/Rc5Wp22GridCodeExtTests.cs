using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GridSim.Core.Compliance.GridCode;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Harmonics.Planning;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP22: the GB grid-code extension checks (<see cref="FfciCheck"/>, <see cref="LongFaultCheck"/>,
/// <see cref="StepResponseChecks"/>, <see cref="StepResponseMetrics"/>) and their requirement file
/// gb-ecc-issue6-rev33-ext. Sources: GB Grid Code ECC Issue 6 Revision 33 (11 June 2025): ECC.6.3.16.1.1-.10 with Figures
/// ECC.6.3.16(a)-(c), ECC.6.3.15.9.2.1(a)/(b) with Figures ECC.6.3.15.9(a)/(b), ECC.A.4A.3, ECC.A.6.2.4.1, ECC.A.7.2.3.1.
/// Hand values were computed in Python (IEEE double) before they were pinned; <c>Assert.Equal(double, double)</c> without
/// a precision is bit-exact.
/// </summary>
public sealed class Rc5Wp22GridCodeExtTests
{
    private static GridCodeExtRequirementSet Gb => GridCodeExtRequirements.LoadGb();

    // ---- 1. Step metrics: a first-order response ---------------------------------------------------------------------

    private static (double[] T, double[] Y) FirstOrder(double tau, double h, double end)
    {
        int n = (int)Math.Round(end / h);
        var t = new double[n + 1];
        var y = new double[n + 1];
        for (int k = 0; k <= n; k++) { t[k] = k * h; y[k] = 1.0 - Math.Exp(-t[k] / tau); }
        return (t, y);
    }

    /// <summary>
    /// y = 1 - e^(-t/tau) reaches the fraction f at t = -tau ln(1 - f): t10 = tau ln(10/9), t90 = tau ln 10, so
    /// t90 - t10 = tau ln 9. Bound: the crossing found lies inside the sample interval that brackets the level (the samples
    /// on either side straddle it and y is monotone), so it is within one sample h = 1e-4 s of the true crossing.
    /// </summary>
    [Fact]
    public void T1_first_order_crossings_are_within_one_sample_of_tau_ln()
    {
        (double[] t, double[] y) = FirstOrder(0.4, 1e-4, 5.0);
        double t90 = StepResponseMetrics.CrossingSeconds(t, y, 0.0, 0.0, 1.0, 0.9)!.Value;
        double t10 = StepResponseMetrics.CrossingSeconds(t, y, 0.0, 0.0, 1.0, 0.1)!.Value;
        Assert.True(Math.Abs(t90 - 0.9210340371976184) <= 1e-4, $"t90 {t90:R}");          // 0.4 ln 10
        Assert.True(Math.Abs(t10 - 0.04214420626313054) <= 1e-4, $"t10 {t10:R}");         // 0.4 ln(10/9)
        Assert.True(Math.Abs((t90 - t10) - 0.4 * Math.Log(9.0)) <= 2e-4, $"t90 - t10 {t90 - t10:R}");
    }

    [Fact]
    public void T1_a_crossing_that_is_never_reached_is_null_and_a_settling_never_reached_is_null()
    {
        (double[] t, double[] y) = FirstOrder(0.4, 1e-3, 1.0);
        Assert.Null(StepResponseMetrics.CrossingSeconds(t, y, 0.0, 0.0, 1.0, 1.0));       // 1 - e^-2.5 < 1
        Assert.Null(StepResponseMetrics.SettlingSeconds(t, y, 0.0, 1.0, 0.005));
    }

    /// <summary>tau = 0.45 s gives t90 = 0.45 ln 10 = 1.0361632918473207 s, over the 1 s zero-to-maximum limit of
    /// ECC.A.7.2.3.1(ii): FAIL. The unit range is 50 Mvar so the 100 Mvar step covers it (the time limit applies).</summary>
    [Fact]
    public void T1_tau_045_misses_the_one_second_ninety_percent_limit()
    {
        const double h = 1e-3, step = 1.0;
        int n = 12000;
        var t = new double[n + 1];
        var q = new double[n + 1];
        for (int k = 0; k <= n; k++)
        {
            t[k] = k * h;
            q[k] = t[k] < step ? 0.0 : 100.0 * (1.0 - Math.Exp(-(t[k] - step) / 0.45));
        }
        var rows = StepResponseChecks.JudgeVctrl(Gb.Vctrl, t, q, step, leadToLag: false, qmaxMvar: 50.0, qminMvar: -50.0);
        GridCodeCheckResult ninety = Assert.Single(rows, r => r.Case == "90 %");
        Assert.Equal(RuleOutcome.Fail, ninety.Outcome);
        Assert.True(Math.Abs(ninety.Value!.Value - 1.0361632918473207) <= 1e-3, $"t90 {ninety.Value:R}");
        Assert.Equal(1.0, ninety.Limit);
        // With tau = 0.4 s the same trace passes (t90 = 0.921 s).
        for (int k = 0; k <= n; k++) q[k] = t[k] < step ? 0.0 : 100.0 * (1.0 - Math.Exp(-(t[k] - step) / 0.4));
        rows = StepResponseChecks.JudgeVctrl(Gb.Vctrl, t, q, step, leadToLag: false, qmaxMvar: 50.0, qminMvar: -50.0);
        Assert.Equal(RuleOutcome.Pass, Assert.Single(rows, r => r.Case == "90 %").Outcome);
        Assert.Equal(RuleOutcome.Pass, Assert.Single(rows, r => r.Case == "commencement").Outcome);
        double commence = Assert.Single(rows, r => r.Case == "commencement").Value!.Value;
        // 2.1.126: commencement is a move beyond 1 % of the steady change (1 Mvar here): 100 (1 - e^(-x / 0.4)) > 1 for
        // x > 0.4 ln(1 / 0.99) = 0.00402 s, so the first such sample is 5 ms after the step (at 4 ms the move is 0.995 Mvar).
        Assert.True(Math.Abs(commence - 0.005) <= 1e-12, $"commencement {commence:R}");
        Assert.Equal(RuleOutcome.Pass, Assert.Single(rows, r => r.Case == "oscillation").Outcome); // a monotone approach does not oscillate
        Assert.Equal(RuleOutcome.NotEvaluated, Assert.Single(rows, r => r.Case == "MVAr seconds").Outcome);
    }

    [Fact]
    public void T1_a_step_smaller_than_the_range_is_not_demonstrated_against_the_time_limit()
    {
        const double h = 1e-3;
        var t = Enumerable.Range(0, 8001).Select(k => k * h).ToArray();
        var q = t.Select(x => x < 1.0 ? 0.0 : 30.0 * (1.0 - Math.Exp(-(x - 1.0) / 0.2))).ToArray();
        GridCodeCheckResult r = Assert.Single(StepResponseChecks.JudgeVctrl(Gb.Vctrl, t, q, 1.0, false, 50.0, -50.0), x => x.Case == "90 %");
        Assert.Equal("NOT DEMONSTRATED", r.Status);
        Assert.Equal(RuleOutcome.NotEvaluated, r.Outcome);
    }

    /// <summary>2.1.126: a passive move is not commencement. A current source holds Iq = -0.5 pu while the voltage steps from
    /// 1.0 to 0.9 pu, so Q = V Iq rises from -50 to -45 Mvar with no control action at all. Judged on Q alone the first sample
    /// after the step had moved towards the new value; judged on the reactive current, nothing moves: FAIL.</summary>
    [Fact]
    public void T1_a_passive_q_move_with_a_constant_reactive_current_does_not_commence()
    {
        const double h = 1e-3;
        var t = Enumerable.Range(0, 8001).Select(k => k * h).ToArray();
        var iq = t.Select(_ => -0.5).ToArray();
        var q = t.Select(x => (x < 1.0 ? 1.0 : 0.9) * -0.5 * 100.0).ToArray();
        var rows = StepResponseChecks.JudgeVctrl(Gb.Vctrl, t, q, 1.0, false, 50.0, -50.0, null, iq);
        GridCodeCheckResult c0 = Assert.Single(rows, r => r.Case == "commencement");
        Assert.Equal(RuleOutcome.Fail, c0.Outcome);
        Assert.Null(c0.Value);
        Assert.Contains("reactive current does not change", c0.Reason, StringComparison.Ordinal);
    }

    // ---- 2. Overshoot of a second-order step -------------------------------------------------------------------------

    /// <summary>
    /// y = 1 - e^(-zeta wn t)(cos wd t + zeta / sqrt(1 - zeta^2) sin wd t), zeta = 0.5, wn = 1 rad/s: the peak is at
    /// t = pi / wd with overshoot e^(-pi zeta / sqrt(1 - zeta^2)) = 0.16303353482158048 (Python). Bound: the largest sample
    /// lies within h/2 of the peak, where y' = 0, so it is below the peak by at most max|y''| (h/2)^2 / 2 &lt;= 1.25e-9 for
    /// h = 1e-4 (|y''| &lt;= wn^2 (1 + overshoot) &lt; 1.2); 1e-4 holds with room.
    /// </summary>
    [Fact]
    public void T2_second_order_overshoot_matches_the_closed_form()
    {
        const double zeta = 0.5, wn = 1.0, h = 1e-4;
        double wd = wn * Math.Sqrt(1 - zeta * zeta);
        int n = 300000;
        var t = new double[n + 1];
        var y = new double[n + 1];
        for (int k = 0; k <= n; k++)
        {
            t[k] = k * h;
            y[k] = 1.0 - Math.Exp(-zeta * wn * t[k]) * (Math.Cos(wd * t[k]) + zeta / Math.Sqrt(1 - zeta * zeta) * Math.Sin(wd * t[k]));
        }
        double os = StepResponseMetrics.Overshoot(t, y, 0.0, 0.0, 1.0)!.Value;
        Assert.True(Math.Abs(os - 0.16303353482158048) <= 1e-4, $"overshoot {os:R}");
        // The first crossing of 100 % is at (pi - acos zeta) / wd (y = 1 there), within one sample.
        double tr = StepResponseMetrics.CrossingSeconds(t, y, 0.0, 0.0, 1.0, 1.0)!.Value;
        Assert.True(Math.Abs(tr - (Math.PI - Math.Acos(zeta)) / wd) <= 1e-4, $"first reach {tr:R}");
    }

    // ---- 3. FFCI envelope logic on a synthetic requirement -----------------------------------------------------------

    /// <summary>A synthetic requirement (not the GB numbers): the line through (0.8 pu, 0) and (0.4 pu, 0.8), cap 0.8,
    /// inception below 0.8 pu, the envelope 0 to 10 ms, 0.5 at 50 ms, 1 at 100 ms, judged up to 200 ms.</summary>
    private static FfciRequirement Synthetic() => Gb.Ffci with
    {
        InceptionVoltage = new ExtValue(0.8, "test", null, false),
        MaxReactiveCurrent = new ExtValue(0.8, "test", null, false),
        LineV1 = 0.8, LineI1 = 0.0, LineV2 = 0.4, LineI2 = 0.8,
        Envelope = new[] { (0.0, 0.0), (0.01, 0.0), (0.05, 0.5), (0.1, 1.0) },
        MaxClearance = new ExtValue(0.2, "test", null, false),
    };

    /// <summary>Samples every 5 ms; V = 1.0 up to 0.1 s, 0.5 from 0.1 s until clearance at 0.25 s, then 1.0. The
    /// pre-fault current is 0.1 pu, above the line's 0 pu at 0.8 pu, so since 2.1.126 the line starts at the pre-fault current:
    /// the target at 0.5 pu is 0.1 + (0.8 - 0.1) x 0.3 / 0.4 = 0.6250000000000001 (Python).</summary>
    private static (double[] T, double[] V, double[] I) Trace(Func<double, double> iqOfTau, double afterClear)
    {
        var t = new double[80];
        var v = new double[80];
        var iq = new double[80];
        for (int k = 0; k < 80; k++)
        {
            t[k] = k * 0.005;
            bool faulted = t[k] >= 0.1 && t[k] < 0.25;
            v[k] = faulted ? 0.5 : 1.0;
            iq[k] = t[k] < 0.1 ? 0.1 : faulted ? iqOfTau(t[k] - 0.1) : afterClear;
        }
        return (t, v, iq);
    }

    [Fact]
    public void T3_a_current_above_the_envelope_passes_with_the_pinned_margin()
    {
        (double[] t, double[] v, double[] iq) = Trace(_ => 0.7, 0.7);
        GridCodeCheckResult r = FfciCheck.Evaluate(Synthetic(), t, v, iq, 0.25);
        Assert.Equal(RuleOutcome.Pass, r.Outcome);
        Assert.Equal(0.07499999999999984, r.Margin);    // 0.7 - (0.1 + 1 x (0.6250000000000001 - 0.1)), Python
        Assert.Equal(0.1, r.AtSeconds);                 // the first sample with f = 1 (tau = 0.2 - 0.1)
    }

    [Fact]
    public void T3_a_slow_rise_fails_at_the_known_time_with_the_known_margin()
    {
        (double[] t, double[] v, double[] iq) = Trace(tau => 0.1 + 0.5 * Math.Min(tau, 0.15) / 0.15, 0.6);
        GridCodeCheckResult r = FfciCheck.Evaluate(Synthetic(), t, v, iq, 0.25);
        Assert.Equal(RuleOutcome.Fail, r.Outcome);
        Assert.Equal(-0.19166666666666676, r.Margin);   // (0.1 + 0.5 x 0.1 / 0.15) - 0.6250000000000001, Python
        Assert.Equal(0.1, r.AtSeconds);
    }

    [Fact]
    public void T3_samples_after_clearance_are_in_the_blocking_period_and_not_judged()
    {
        (double[] t, double[] v, double[] iq) = Trace(_ => 0.7, 0.0);   // the current falls to 0 after clearance
        GridCodeCheckResult r = FfciCheck.Evaluate(Synthetic(), t, v, iq, 0.25);
        Assert.Equal(RuleOutcome.Pass, r.Outcome);
        Assert.Equal(0.07499999999999984, r.Margin);
    }

    [Fact]
    public void T3_no_dip_below_the_inception_voltage_is_not_evaluated_and_an_early_clear_is_not_demonstrated()
    {
        var t = Enumerable.Range(0, 40).Select(k => k * 0.005).ToArray();
        GridCodeCheckResult none = FfciCheck.Evaluate(Synthetic(), t, t.Select(_ => 0.85).ToArray(), t.Select(_ => 0.1).ToArray(), 0.15);
        Assert.Equal(RuleOutcome.NotEvaluated, none.Outcome);
        (double[] tt, double[] v, double[] iq) = Trace(_ => 0.1, 0.1);
        GridCodeCheckResult early = FfciCheck.Evaluate(Synthetic(), tt, v, iq, 0.105);   // cleared 5 ms after inception
        Assert.Equal("NOT DEMONSTRATED", early.Status);
    }

    [Fact]
    public void T3_the_gb_line_and_envelope_read_back()
    {
        FfciRequirement g = Gb.Ffci;
        Assert.Equal(1.0, FfciCheck.TargetCurrent(g, 0.5, 0.0));          // labelled point (0.5 pu, 1.0 pu)
        Assert.Equal(1.0, FfciCheck.TargetCurrent(g, 0.2, 0.0));          // capped at 1.0 pu (ECC.6.3.16.1.5)
        Assert.Equal(0.3, FfciCheck.TargetCurrent(g, 0.9, 0.3));          // never below the pre-fault current
        Assert.Equal(0.0, FfciCheck.EnvelopeFraction(g, 0.02));
        Assert.Equal(0.65, FfciCheck.EnvelopeFraction(g, 0.06));
        Assert.Equal(1.0, FfciCheck.EnvelopeFraction(g, 0.12));
        Assert.Equal(1.0, FfciCheck.EnvelopeFraction(g, 0.14));
    }

    // ---- 4. FFCI on the REGC_A converter model -----------------------------------------------------------------------

    /// <summary>
    /// The REGC_A-style converter (<see cref="RmsConverterState"/>) with Q0 = 0, V0 = 1, rating 1, Kqv = 2.2, Tg = 0.02 s,
    /// Tr = 0 (the instant lag), Imax 1.5, held at a 0.5 pu retained voltage: the reactive command is
    /// Kqv (V0 - V) = 1.1 pu, and the exact zero-order-hold lag gives Iq(n dt) = 1.1 (1 - a^n), a = e^(-dt / Tg), so at
    /// 60 ms Iq = 1.1 (1 - e^(-3)). Bound 1e-6: 60 steps of one multiply-add each (about 2 u per step) and DetMath.Exp
    /// (within a few u) leave the value within 1e-13; the spec's 1e-6 holds with room. The trace then passes the GB
    /// envelope: 1.1 (1 - e^(-tau/0.02)) is at or above 0.65 tau-scaled and reaches 1.0 by 120 ms (1.1 (1 - e^-6) = 1.097).
    /// </summary>
    [Fact]
    public void T4_regc_a_reactive_current_at_60_ms_and_the_gb_envelope()
    {
        var p = new RmsConverterParameters(Tg: 0.02, Tr: 0.0, Kqv: 2.2, Imax: 1.5);
        var unit = new RmsConverterUnit(1, "ppm", 1.0, 0.5, 0.0, Complex.One, p);
        var state = new RmsConverterState(unit);
        const double dt = 0.001;
        var t = new List<double> { 0.0 };
        var v = new List<double> { 1.0 };
        var iq = new List<double> { 0.0 };
        double atSixty = double.NaN;
        for (int k = 1; k <= 200; k++)
        {
            t.Add(k * dt);
            v.Add(0.5);
            iq.Add(state.ReactiveCurrent);           // the current at inception + (k - 1) steps
            if (k - 1 == 60) atSixty = state.ReactiveCurrent;
            state.Step((k - 1) * dt, 0.5, dt);
        }
        Assert.True(Math.Abs(atSixty - 1.1 * (1.0 - Math.Exp(-0.06 / 0.02))) <= 1e-6, $"Iq(60 ms) {atSixty:R}");
        GridCodeCheckResult r = FfciCheck.Evaluate(Gb.Ffci, t, v, iq, 0.141);
        Assert.Equal(RuleOutcome.Pass, r.Outcome);
        // 2.1.126: judged only after 20 ms, where the forbidden area starts (at inception the edge is I0 = 0 and the margin
        // was exactly 0). The target is 1.0 pu (V = 0.5 pu, I0 = 0), so the edge is f(tau) and the margin is
        // min over the judged samples of 1.1 (1 - e^(-tau/0.02)) - f(tau): the least is at tau = 120 ms, where f reaches 1,
        // 1.1 (1 - e^-6) - 1 = 0.0972733726... (independent Python sweep of every sample: 0.09727337260566715 at 0.12 s).
        // Bound 1e-12: the trace carries 120 rounded multiply-adds of the lag (about 1e-14) and tau is a difference of two
        // sample times (its rounding moves f by at most 0.35 x 1e-17 / 0.06).
        Assert.True(Math.Abs(r.Margin!.Value - (1.1 * (1.0 - Math.Exp(-6.0)) - 1.0)) <= 1e-12, $"margin {r.Margin:R}");
        Assert.True(Math.Abs(r.AtSeconds!.Value - 0.12) <= 1e-12, $"at {r.AtSeconds:R}");
    }

    // ---- 4b. FFCI through the study --------------------------------------------------------------------------------

    /// <summary>Bus 1: a GENCLS grid (H = 1000, X'd = 0.1); line 1-2 X = 0.2; bus 2 (PCC): the unit, a converter at 50 MW on
    /// 100 MVA (Pmax 50), or a synchronous GENCLS. The same case as Rc5Wp8 T5.</summary>
    private static (GridModel Model, PowerFlowResult Pf, IReadOnlyList<SubtransientInit.MachineData> Data) TwoBus(bool converterUnit)
    {
        var buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pv, BaseKv: 132) };
        Generator unit = converterUnit
            ? new Generator(2, 50.0, MBase: 100, Pmax: 50, Name: "unit", ConverterInterfaced: true)
            : new Generator(2, 0.0, MBase: 100, H: 5, Pmax: 100, SubtransientXdPu: 0.1, Name: "unit");
        var gens = new[] { new Generator(1, 0.0, MBase: 100, H: 1000, SubtransientXdPu: 0.1, Name: "grid"), unit };
        var model = new GridModel { BaseMva = 100, Buses = buses, Generators = gens, Branches = new[] { new Branch(1, 2, 0.0, 0.2) } };
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);
        return (model, pf, new[] { Gencls.Data(1000.0, 0.0, 0.1), Gencls.Data(5.0, 0.0, 0.1) });
    }

    private static GridCodeRequest Request((GridModel Model, PowerFlowResult Pf, IReadOnlyList<SubtransientInit.MachineData> Data) c, string tech,
        ModuleType mt, params GridCodeCheckRequest[] checks) => new()
        {
            Label = "wp22", Code = "gb-ecc", ModuleType = mt, Technology = tech, ConnectionKv = 33, Model = c.Model, Data = c.Data,
            UnitIndex = 1, PccBus = 2, Checks = checks,
        };

    [Fact]
    public void T4_ffci_runs_the_very_frt_envelope_simulation()
    {
        var c = TwoBus(converterUnit: true);
        GridCodeResult res = GridCodeStudy.Run(Request(c, GridCodeTechnology.Ppm, ModuleType.B,
            new FrtEnvelopeRequest(new FrtFault()), new FfciRequest(new FrtFault())));
        Assert.Equal(2, res.FrtRuns.Count);
        Assert.Equal(res.FrtRuns[0].Observed.Buses.MagnitudePu[0], res.FrtRuns[1].Observed.Buses.MagnitudePu[0]);
        Assert.Equal(res.FrtRuns[0].Observed.Result.Converters![0].ReactiveCurrentPu, res.FrtRuns[1].Observed.Result.Converters![0].ReactiveCurrentPu);
        GridCodeCheckResult f = res.Checks[1];
        Assert.Equal("ffci-envelope", f.Id);
        // 2.1.126: the outcome derived from the requirement and the model, not from the run. Type B PPM below 110 kV: Uret
        // 0.10 pu, clearance 0.14 s, so the PCC sits at 0.10 pu plus the rise the unit's own current makes through
        // Xth || Xf = 0.03 pu (at most 1.2 pu, so at most 0.136 pu; checked on the trace below). At V <= 0.5 pu the target
        // is the 1.0 pu cap. The unit is the default REGC_A-style converter (Kqv 2, Tr = Tg = 0.02 s, Imax 1.2 pu on its
        // 100 MVA rating) from I0 = Q0 / V0 = (1 - cos(asin 0.1)) / 0.2 = 0.02506 pu. Stepping the two exact zero-order-hold
        // lags at the 10 ms study step (independent Python, with the fault-period voltage held at 0.10 and at 0.14 pu) gives
        // the least margin at 120 ms, where f reaches 1, between 0.196054 and 0.196239 pu: PASS, and the margin within
        // [0.19605, 0.19624] for any fault-period voltage in that range.
        double[] vp = res.FrtRuns[1].Observed.Buses.MagnitudePu[0];
        IReadOnlyList<double> tp = res.FrtRuns[1].Observed.Buses.TimeSeconds;
        for (int k = 0; k < tp.Count; k++)
            if (tp[k] > 0.1 + 1e-9 && tp[k] < 0.24 - 1e-9) Assert.InRange(vp[k], 0.10 - 1e-9, 0.14);
        Assert.Equal(RuleOutcome.Pass, f.Outcome);
        Assert.InRange(f.Margin!.Value, 0.19605, 0.19624);
        Assert.True(Math.Abs(f.AtSeconds!.Value - 0.12) <= 1e-9, $"at {f.AtSeconds:R}");
        Assert.Equal("pu (unit rating)", f.Unit);
        Assert.Contains(res.Notes, n => n.Contains("gb-ecc-issue6-rev33-ext", StringComparison.Ordinal));
    }

    [Fact]
    public void T4_ffci_is_not_applicable_to_synchronous_or_grid_forming_units_and_not_evaluated_for_unbalanced_faults()
    {
        var sync = TwoBus(converterUnit: false);
        GridCodeCheckResult s = Assert.Single(GridCodeStudy.Run(Request(sync, GridCodeTechnology.Synchronous, ModuleType.B, new FfciRequest(new FrtFault()))).Checks);
        Assert.Equal(RuleOutcome.NotApplicable, s.Outcome);
        var conv = TwoBus(converterUnit: true);
        GridCodeCheckResult g = Assert.Single(GridCodeStudy.Run(Request(conv, GridCodeTechnology.PpmGridForming, ModuleType.B, new FfciRequest(new FrtFault()))).Checks);
        Assert.Equal(RuleOutcome.NotApplicable, g.Outcome);
        Assert.Equal("ECC.6.3.16.1.1", g.Clause);
        GridCodeCheckResult u = Assert.Single(GridCodeStudy.Run(Request(conv, GridCodeTechnology.Ppm, ModuleType.B,
            new FfciRequest(new FrtFault(FaultType.SingleLineToGround, 0.0, 0.05)))).Checks);
        Assert.Equal(RuleOutcome.NotEvaluated, u.Outcome);
        Assert.Equal("ECC.6.3.16.1.13", u.Clause);
    }

    [Fact]
    public void T4_extension_checks_are_refused_under_rfg()
    {
        var c = TwoBus(converterUnit: true);
        var ex = Assert.Throws<InvalidDataException>(() => GridCodeStudy.Run(Request(c, GridCodeTechnology.Ppm, ModuleType.B, new FfciRequest(new FrtFault())) with
        {
            Code = "eu-rfg",
        }));
        Assert.Contains("gb-ecc", ex.Message, StringComparison.Ordinal);
    }

    // ---- 5. frt-long staging -----------------------------------------------------------------------------------------

    [Fact]
    public void T5_profile_voltages_are_read_only_at_labelled_durations()
    {
        LongFaultProfile two = new("test", "test figure", "test", new[] { "C" }, new[] { "ppm" },
            new[] { new DurationCorner(0.14, 0.3), new DurationCorner(0.5, 0.6), new DurationCorner(1.0, 0.6), new DurationCorner(1.0, 0.7), new DurationCorner(2.0, 0.7) },
            new[] { 0 });
        Assert.Null(two.LabelledVoltage(0.3));          // inside the sloped segment
        Assert.Equal(0.3, two.LabelledVoltage(0.14));
        Assert.Equal(0.6, two.LabelledVoltage(0.7));
        Assert.Equal(0.6, two.LabelledVoltage(1.0));    // the lower value at a vertical step is on the line
        Assert.Equal(0.7, two.LabelledVoltage(1.5));
        Assert.Null(two.LabelledVoltage(3.0));          // beyond the profile
        LongFaultProfile sync = LongFaultCheck.ProfileFor(Gb, ModuleType.D, GridCodeTechnology.Synchronous)!;
        LongFaultProfile ppm = LongFaultCheck.ProfileFor(Gb, ModuleType.C, GridCodeTechnology.Ppm)!;
        Assert.Equal(0.85, sync.LabelledVoltage(1.088));
        Assert.Equal(0.85, sync.LabelledVoltage(60.0));
        Assert.Null(sync.LabelledVoltage(0.5));
        Assert.Equal(0.8, ppm.LabelledVoltage(2.0));
        Assert.Equal(0.8, ppm.LabelledVoltage(2.5));
        Assert.Equal(0.85, ppm.LabelledVoltage(10.0));
        Assert.Null(LongFaultCheck.ProfileFor(Gb, ModuleType.B, GridCodeTechnology.Synchronous));
    }

    /// <summary>
    /// Two dip stages are staged on the classical two-bus case: 0.6 pu for 0.5 s, then (a second run) 0.7 pu. The PCC
    /// voltage over every dip sample is recomputed here from the observed trace, independently of the check, and must
    /// either lie within 1e-3 pu of the target, or the check must say NOT DEMONSTRATED.
    /// </summary>
    [Theory]
    [InlineData(0.6)]
    [InlineData(0.7)]
    public void T5_the_pcc_follows_the_staged_dip_within_1e_3_or_the_check_is_not_demonstrated(double target)
    {
        var c = TwoBus(converterUnit: false);
        LongFaultRun run = LongFaultCheck.RunStaged(c.Model, c.Pf, c.Data, 2, 2, 0.1, 0.5, target, 0.05, 0.01, 0.5, 8);
        IReadOnlyList<double> t = run.Observed.Buses.TimeSeconds;
        double[] v = run.Observed.Buses.MagnitudePu[0];
        double worst = 0.0;
        int samples = 0;
        for (int k = 0; k < t.Count; k++)
        {
            if (t[k] < 0.1 - 0.005 || t[k] >= 0.6 - 0.005) continue;
            samples++;
            worst = Math.Max(worst, Math.Abs(v[k] - target));
        }
        Assert.Equal(50, samples);
        Assert.Equal(worst, Math.Abs(run.WorstDeviationPu));
        // 2.1.126: on this linear GENCLS case the staging must converge (no staging problem, every dip sample within 1e-3 pu)
        // and take the pinned number of passes (observed on the first run; the fixed point is exact for a linear network, so
        // a change in the count means the staging changed).
        Assert.Null(run.StagingProblem);
        Assert.True(worst <= LongFaultCheck.TolerancePu, $"worst deviation {worst:R} pu");
        Assert.True(run.Passes == ExpectedPasses(target), $"passes {run.Passes}");
        Assert.Equal(run.MinDeviationPu, Enumerable.Range(0, t.Count).Where(k => !(t[k] < 0.1 - 0.005 || t[k] >= 0.6 - 0.005)).Min(k => v[k] - target));
        LongFaultProfile prof = LongFaultCheck.ProfileFor(Gb, ModuleType.C, GridCodeTechnology.Synchronous)!;
        GridCodeCheckResult r = LongFaultCheck.Judge(run, prof, c.Model, c.Pf, c.Data, 1, null);
        Assert.Equal(RuleOutcome.Pass, r.Outcome);
    }

    // Observed on the first run (2.1.126), a record: one pass already holds every dip sample within 1e-3 pu on this case.
    private static int ExpectedPasses(double target) => target == 0.6 ? 1 : 1;

    [Fact]
    public void T5_a_dip_that_strays_from_the_profile_is_not_demonstrated_whichever_way()
    {
        var c = TwoBus(converterUnit: false);
        LongFaultRun run = LongFaultCheck.RunStaged(c.Model, c.Pf, c.Data, 2, 2, 0.1, 0.5, 0.6, 0.05, 0.01, 0.5, 8);
        LongFaultProfile prof = LongFaultCheck.ProfileFor(Gb, ModuleType.C, GridCodeTechnology.Synchronous)!;
        // 2.1.126 (the frt-envelope tolerances): the unit stays connected in this run, so a dip that went below the target is a
        // harsher test passed (with a note), and one whose lowest voltage stayed above the target by more than 1e-3 pu is
        // milder: NOT DEMONSTRATED.
        Assert.True(run.Observed.Result.StayedInSynchronism);
        GridCodeCheckResult below = LongFaultCheck.Judge(run with { WorstDeviationPu = -0.002, MinDeviationPu = -0.002, StagingProblem = null }, prof, c.Model, c.Pf, c.Data, 1, null);
        Assert.Equal(RuleOutcome.Pass, below.Outcome);
        Assert.Contains(below.Notes, n => n.Contains("a harsher test than the profile", StringComparison.Ordinal));
        GridCodeCheckResult above = LongFaultCheck.Judge(run with { WorstDeviationPu = 0.002, MinDeviationPu = 0.0011, StagingProblem = null }, prof, c.Model, c.Pf, c.Data, 1, null);
        Assert.Equal("NOT DEMONSTRATED", above.Status);
        Assert.Contains("milder", above.Reason, StringComparison.Ordinal);
        GridCodeCheckResult edge = LongFaultCheck.Judge(run with { WorstDeviationPu = 0.002, MinDeviationPu = 0.001, StagingProblem = null }, prof, c.Model, c.Pf, c.Data, 1, null);
        Assert.Equal(RuleOutcome.Pass, edge.Outcome);   // exactly 1e-3 above is on the tolerance
        // A lost synchronism (the run's verdict set false here): FAIL in a milder test, NOT DEMONSTRATED in a harsher one
        // (more than 1e-9 pu below), FAIL on the target.
        LongFaultRun lost = run with { Observed = run.Observed with { Result = run.Observed.Result with { StayedInSynchronism = false } }, StagingProblem = null };
        Assert.Equal(RuleOutcome.Fail, LongFaultCheck.Judge(lost with { MinDeviationPu = 0.002 }, prof, c.Model, c.Pf, c.Data, 1, null).Outcome);
        GridCodeCheckResult harsher = LongFaultCheck.Judge(lost with { MinDeviationPu = -2e-9 }, prof, c.Model, c.Pf, c.Data, 1, null);
        Assert.Equal("NOT DEMONSTRATED", harsher.Status);
        Assert.Contains("more severe", harsher.Reason, StringComparison.Ordinal);
        Assert.Equal(RuleOutcome.Fail, LongFaultCheck.Judge(lost with { MinDeviationPu = -1e-9 }, prof, c.Model, c.Pf, c.Data, 1, null).Outcome);
    }

    [Fact]
    public void T5_a_duration_off_the_step_grid_is_rounded_up_never_down()
    {
        Assert.Equal(1.2, LongFaultCheck.WholeStepDip(1.2, 0.01));                     // 120 steps, kept as given
        Assert.Equal(109 * 0.01, LongFaultCheck.WholeStepDip(1.088, 0.01));            // 108.8 steps -> 109
        Assert.True(LongFaultCheck.WholeStepDip(1.088, 0.01) >= 1.088);
        Assert.Equal(1.088, LongFaultCheck.WholeStepDip(1.088, 0.001));
    }

    [Fact]
    public void T5_frt_long_through_the_study_marks_sloped_durations_and_hvdc_and_type_b()
    {
        var c = TwoBus(converterUnit: false);
        GridCodeResult res = GridCodeStudy.Run(Request(c, GridCodeTechnology.Synchronous, ModuleType.C, new LongFaultRequest(new[] { 0.5 })));
        GridCodeCheckResult r = Assert.Single(res.Checks);
        Assert.Equal(RuleOutcome.NotEvaluated, r.Outcome);
        Assert.Contains("NOT TO SCALE", r.Reason, StringComparison.Ordinal);
        Assert.Equal(RuleOutcome.NotApplicable, Assert.Single(GridCodeStudy.Run(Request(c, GridCodeTechnology.Synchronous, ModuleType.B, new LongFaultRequest())).Checks).Outcome);
        var conv = TwoBus(converterUnit: true);
        GridCodeCheckResult h = Assert.Single(GridCodeStudy.Run(Request(conv, GridCodeTechnology.Hvdc, ModuleType.C, new LongFaultRequest())).Checks);
        Assert.Equal(RuleOutcome.NotEvaluated, h.Outcome);
        Assert.Equal("ECC.6.3.15.9.1.1", h.Clause);
        Assert.Throws<InvalidDataException>(() => GridCodeStudy.Run(Request(c, GridCodeTechnology.Synchronous, ModuleType.C, new LongFaultRequest(new[] { 200.0 }))));
    }

    // ---- 5b. AVR open-circuit step -----------------------------------------------------------------------------------

    /// <summary>A round-rotor machine with no saturation (T'do = 7.4 s, T''do = 0.03 s) and a SEXS with TA = TB (the lead-lag
    /// is unity), TE = 0 and gain K. On open circuit the closed loop is T'do T''do s^2 + (T'do + T''do) s + 1 + K, so
    /// zeta = (T'do + T''do) / (2 sqrt(T'do T''do (1 + K))): K = 10 gives zeta 2.38 (no overshoot), K = 400 gives 0.394.
    /// The steady state of the loop (the discrete map has the same fixed point) is E = K vref / (1 + K). Since 2.1.126 the
    /// reference after the step is the one that holds 1.0 pu, vref = 1.0 + 1.0 / K (the SEXS steady error at Efd = 1.0), so
    /// E = 1.0: the check judges the AVR against the voltage it was set to (before, vref = 0.9 + 0.9 / K + 0.1 settled at
    /// (K + 0.9) / (K + 1), short of the 1.0 pu it was judged against).</summary>
    private static SubtransientInit.MachineData Machine() => new(5.0, 0.0, 1.8, 0.3, 0.23, 1.75, 0.65, 0.23, 7.4, 0.03, 0.4, 0.05);

    /// <summary>K = 10: the poles of T'do T''do s^2 + (T'do + T''do) s + 1 + K are -1.5525 and -31.916 /s (Python), so after
    /// 30 s the transient is below e^-46 of the 0.1 pu step and the end value equals the fixed point 1.0 to rounding (1e-12
    /// allows a few hundred roundings of O(1) numbers). The response 1 - 0.1 (1.0511 e^(s1 t) - 0.0511 e^(s2 t)) never
    /// overshoots, so it does not reach 1.0 pu inside 0.6 s (FAIL), and it enters 1.0 +- 0.005 pu at 1.9617 s (the
    /// continuous solution, Python), under 3 s (PASS). Bound 0.01 s on the settling time: the kernel's start-of-step inputs
    /// delay each of the two lags by up to one 1 ms step and the reading takes the next sample (one more step), so the
    /// discrete time lies within 3 ms of the continuous one for this slow pole; 0.01 s leaves a factor of 3.</summary>
    [Fact]
    public void T5b_an_overdamped_avr_settles_at_1_pu_late_to_reach_it_and_in_time_to_settle()
    {
        RmsExciter ex = RmsExciter.FromSexs(1.0, 1.0, 10.0, 0.0);
        OpenCircuitTrace tr = StepResponseChecks.OpenCircuitStep(Machine(), ex, 0.9, 1.0, 0.001, 30.0);
        Assert.Equal(0.9, tr.TerminalVoltagePu[0]);
        Assert.True(Math.Abs(tr.TerminalVoltagePu[^1] - 1.0) <= 1e-12, $"V(end) {tr.TerminalVoltagePu[^1]:R}");
        var rows = StepResponseChecks.JudgeAvr(Gb.Avr, tr);
        Assert.Equal(RuleOutcome.Fail, rows[0].Outcome);       // does not reach 1.0 pu within 0.6 s
        Assert.True(rows[0].Value is null || rows[0].Value >= 0.6, $"first reach {rows[0].Value:R}");
        Assert.Equal(RuleOutcome.Pass, rows[1].Outcome);
        Assert.True(Math.Abs(rows[1].Value!.Value - 1.9617378465168285) <= 0.01, $"settling {rows[1].Value:R}");
    }

    [Fact]
    public void T5b_a_fast_avr_passes_both_limits_and_runs_the_same_bits_twice()
    {
        OpenCircuitTrace a = StepResponseChecks.OpenCircuitStep(Machine(), RmsExciter.FromSexs(1.0, 1.0, 400.0, 0.0), 0.9, 1.0, 0.001, 10.0);
        OpenCircuitTrace b = StepResponseChecks.OpenCircuitStep(Machine(), RmsExciter.FromSexs(1.0, 1.0, 400.0, 0.0), 0.9, 1.0, 0.001, 10.0);
        Assert.Equal(a.TerminalVoltagePu, b.TerminalVoltagePu);
        Assert.True(Math.Abs(a.TerminalVoltagePu[^1] - 1.0) <= 1e-12, $"V(end) {a.TerminalVoltagePu[^1]:R}");
        var rows = StepResponseChecks.JudgeAvr(Gb.Avr, a);
        Assert.Equal(RuleOutcome.Pass, rows[0].Outcome);
        Assert.True(rows[0].Value < 0.6);
        Assert.Equal(RuleOutcome.Pass, rows[1].Outcome);
    }

    [Fact]
    public void T5b_avr_step_without_an_exciter_is_not_evaluated_and_a_ppm_is_not_applicable()
    {
        var c = TwoBus(converterUnit: false);
        GridCodeCheckResult r = Assert.Single(GridCodeStudy.Run(Request(c, GridCodeTechnology.Synchronous, ModuleType.C, new AvrStepRequest())).Checks);
        Assert.Equal(RuleOutcome.NotEvaluated, r.Outcome);
        var rows = StepResponseChecks.Avr(Gb.Avr, ModuleType.C, GridCodeTechnology.Ppm, Machine(), RmsExciter.FromSexs(1.0, 1.0, 10.0, 0.0), 0.001, 10.0);
        Assert.Equal(RuleOutcome.NotApplicable, Assert.Single(rows).Outcome);
    }

    // ---- 6. The requirement file ----------------------------------------------------------------------------------

    [Fact]
    public void T6_the_extension_file_is_pinned_has_no_cr_and_every_value_has_a_clause()
    {
        byte[] bytes = GridCodeExtRequirements.Bytes(GridCodeExtRequirements.GbId);
        Assert.Equal("a7e1fc4bbea5729b0967695214ca56f415800b989e64cca4d70fa418b61e8d5c", Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Assert.DoesNotContain((byte)'\r', bytes);
        using JsonDocument doc = JsonDocument.Parse(bytes);
        int checkedObjects = 0;
        void Walk(JsonElement e, bool parentHasClause, string path)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                bool has = e.TryGetProperty("clause", out _);
                bool numbers = e.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.Number);
                if (numbers)
                {
                    Assert.True(has || parentHasClause, $"{path} holds numbers but no clause");
                    checkedObjects++;
                }
                foreach (JsonProperty p in e.EnumerateObject()) Walk(p.Value, has || parentHasClause, path + "." + p.Name);
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (JsonElement x in e.EnumerateArray()) Walk(x, parentHasClause, $"{path}[{i++}]");
            }
        }
        Walk(doc.RootElement, false, "$");
        Assert.True(checkedObjects >= 20, $"only {checkedObjects} value objects found");
        // Every value carries a page key (null: the source is a Word document, see 'pages').
        Assert.Contains("\"pages\"", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal("gb-ecc-issue6-rev33", Gb.Extends);
    }

    [Fact]
    public void T6_the_built_in_requirement_list_is_unchanged()
        => Assert.Equal(new[] { "eu-rfg-2016-631", "gb-ecc-issue6-rev33" }, GridCodeRequirements.BuiltInIds.ToArray());

    // ---- 7. Bit-identity of the existing checks --------------------------------------------------------------------

    /// <summary>The result file with the lines that carry the engine version or the input files' hashes removed, LF only:
    /// the hash of what the checks computed.</summary>
    private static string Normalised(string text) => string.Join("\n", text.Replace("\r", "", StringComparison.Ordinal).Split('\n')
        .Where(l => !l.Contains("\"engine\":", StringComparison.Ordinal) && !l.Contains("\"studySha256\":", StringComparison.Ordinal)
            && !l.Contains("\"dynData\":", StringComparison.Ordinal)));

    private static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    /// <summary>
    /// Pins taken before any WP22 edit, from the Release CLI built from dev main 4ed1dfe8 (2.1.123; 2.1.124 changed no
    /// source; gridsim.dll sha256 3b00251449ab84ae..., GridSim.Core.dll c2e12126c116a12e...): grid-code on the Rc5Wp8 T12
    /// static two-bus study and on a case14 frt-envelope / frt-retained-voltage / frt-recovery study (case14.raw and
    /// case14.dyr with its governor records left out), each result normalised as above, and the first three lines of
    /// grid-code --list.
    /// </summary>
    [Fact]
    public void T7_existing_grid_code_output_is_byte_identical_to_the_pre_wp22_build()
    {
        string dir = TestTemp.Under("gridsim-wp22-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "twobus.m"), string.Join("\n",
                "function mpc = twobus", "mpc.version = '2';", "mpc.baseMVA = 100;",
                "mpc.bus = [", "1 2 0 0 0 0 1 1 0 33 1 1.1 0.9;", "2 3 0 0 0 0 1 1 0 33 1 1.1 0.9;", "];",
                "mpc.gen = [", "1 100 0 50 -50 1 100 1 100 0;", "2 0 0 999 -999 1 100 1 0 0;", "];",
                "mpc.branch = [", "1 2 0 0.1 0 0 0 0 0 0 1 -360 360;", "];", ""));
            File.WriteAllText(Path.Combine(dir, "static.json"), string.Join("\n",
                "{",
                "  \"schema\": \"gridsim-gridcode-study/1\", \"label\": \"two-bus\", \"case\": \"twobus.m\", \"unit\": { \"bus\": 1 }, \"pccBus\": 2, \"plantBuses\": [1],",
                "  \"code\": \"gb-ecc\", \"moduleType\": \"B\", \"technology\": \"synchronous\", \"connectionKv\": 33,",
                "  \"checks\": [",
                "    { \"id\": \"q-capability\" },",
                "    { \"id\": \"frt-uv-settings\", \"stages\": [ { \"device\": \"27-1\", \"function\": \"27\", \"pickup\": 0.2, \"delaySeconds\": 0.5 } ] },",
                "    { \"id\": \"frequency-relays\", \"stages\": [ { \"device\": \"81U-1\", \"function\": \"81U\", \"pickup\": 47.0, \"delaySeconds\": 0.5 } ] },",
                "    { \"id\": \"rocof-settings\", \"stages\": [ { \"device\": \"81R-1\", \"function\": \"81R\", \"pickup\": 1.0, \"delaySeconds\": 0.5 } ] }",
                "  ]",
                "}", ""));
            string fixtures = Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse");
            File.Copy(Path.Combine(fixtures, "case14.raw"), Path.Combine(dir, "case14.raw"));
            File.WriteAllText(Path.Combine(dir, "c14.dyr"), string.Join("\n", File.ReadAllLines(Path.Combine(fixtures, "case14.dyr"))
                .Where(l => !l.Contains("TGOV1", StringComparison.Ordinal) && !l.Contains("HYGOV", StringComparison.Ordinal) && !l.Contains("IEEEG1", StringComparison.Ordinal))) + "\n");
            File.WriteAllText(Path.Combine(dir, "frt.json"), string.Join("\n",
                "{",
                "  \"schema\": \"gridsim-gridcode-study/1\", \"label\": \"case14-frt\", \"case\": \"case14.raw\", \"dynData\": \"c14.dyr\", \"unit\": { \"bus\": 3 }, \"pccBus\": 3,",
                "  \"code\": \"gb-ecc\", \"moduleType\": \"C\", \"technology\": \"synchronous\", \"connectionKv\": 33,",
                "  \"checks\": [ { \"id\": \"frt-envelope\" }, { \"id\": \"frt-retained-voltage\" }, { \"id\": \"frt-recovery\" } ]",
                "}", ""));
            string sOut = Path.Combine(dir, "s.res"), fOut = Path.Combine(dir, "f.res");
            CliProcess.Result s = CliProcess.RunBuilt("grid-code", Path.Combine(dir, "static.json"), "--out", sOut);
            Assert.True(s.ExitCode == 0, s.ToString());
            CliProcess.Result f = CliProcess.RunBuilt("grid-code", Path.Combine(dir, "frt.json"), "--out", fOut);
            Assert.True(f.ExitCode == 3, f.ToString());
            Assert.Equal("b260a30de1c09fa63e3dcbdb2b4c2fdd2b6ed957b61429bac214fbd67f294721", Sha(Normalised(File.ReadAllText(sOut))));
            Assert.Equal("73a7396bea989c652ab5e4e7663462b8053608e3ad0066097ae5c0a32e3251e0", Sha(Normalised(File.ReadAllText(fOut))));
            Assert.DoesNotContain("requirementsExtSha256", File.ReadAllText(fOut), StringComparison.Ordinal);

            string[] list = CliProcess.RunBuilt("grid-code", "--list").StdOut.Replace("\r", "", StringComparison.Ordinal).Split('\n');
            Assert.Equal("eu-rfg-2016-631  Commission Regulation (EU) 2016/631 establishing a network code on requirements for grid connection of generators (RfG), OJ L 112, 27.4.2016", list[0]);
            Assert.Equal("gb-ecc-issue6-rev33  GB Grid Code, European Connection Conditions (ECC), Issue 6 Revision 33 (11 June 2025)", list[1]);
            Assert.Equal("checks: frt-envelope, frt-retained-voltage, frt-recovery, frt-uv-settings, q-capability, lfsm-o, lfsm-u, fsm-step, rocof-settings, rocof-event, frequency-relays", list[2]);
            // 2.1.126: --list is the tagged releases' output, byte for byte (three lines); the extension checks have --list-ext.
            Assert.Equal(4, list.Length);
            Assert.Equal("", list[3]);
            string[] ext = CliProcess.RunBuilt("grid-code", "--list-ext").StdOut.Replace("\r", "", StringComparison.Ordinal).Split('\n');
            Assert.StartsWith("gb-ecc-issue6-rev33-ext  ", ext[0], StringComparison.Ordinal);
            Assert.Equal("gb-ecc extension checks (run only when named): ffci-envelope, frt-long, avr-step, vctrl-step", ext[1]);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void T7_an_extension_check_records_the_extension_requirements_hash()
    {
        string dir = TestTemp.Under("gridsim-wp22x-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string fixtures = Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse");
            File.Copy(Path.Combine(fixtures, "case14.raw"), Path.Combine(dir, "case14.raw"));
            File.WriteAllText(Path.Combine(dir, "c14.dyr"), string.Join("\n", File.ReadAllLines(Path.Combine(fixtures, "case14.dyr"))
                .Where(l => !l.Contains("TGOV1", StringComparison.Ordinal) && !l.Contains("HYGOV", StringComparison.Ordinal) && !l.Contains("IEEEG1", StringComparison.Ordinal))) + "\n");
            File.WriteAllText(Path.Combine(dir, "avr.json"), string.Join("\n",
                "{",
                "  \"schema\": \"gridsim-gridcode-study/1\", \"label\": \"case14-avr\", \"case\": \"case14.raw\", \"dynData\": \"c14.dyr\", \"unit\": { \"bus\": 1 }, \"pccBus\": 1,",
                "  \"code\": \"gb-ecc\", \"moduleType\": \"C\", \"technology\": \"synchronous\", \"connectionKv\": 33,",
                "  \"checks\": [ { \"id\": \"avr-step\", \"stepSeconds\": 0.001 } ]",
                "}", ""));
            string o = Path.Combine(dir, "a.res");
            CliProcess.Result r = CliProcess.RunBuilt("grid-code", Path.Combine(dir, "avr.json"), "--out", o);
            Assert.True(r.ExitCode is 0 or 3, r.ToString());
            string text = File.ReadAllText(o);
            Assert.Contains("\"requirementsExtSha256\": \"a7e1fc4bbea5729b0967695214ca56f415800b989e64cca4d70fa418b61e8d5c\"", text, StringComparison.Ordinal);
            Assert.Contains("\"id\": \"avr-step\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("the unit's dynamic data carries no exciter model", text, StringComparison.Ordinal);   // bus 1 has a SEXS
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
