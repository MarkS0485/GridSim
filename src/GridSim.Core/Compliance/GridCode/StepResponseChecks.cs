using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Harmonics.Planning;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Compliance.GridCode;

/// <summary>An open-circuit terminal-voltage trace: sample times (s, the step at t = 0) and the terminal voltage (pu).</summary>
public sealed record OpenCircuitTrace(IReadOnlyList<double> TimeSeconds, IReadOnlyList<double> TerminalVoltagePu, double FieldVoltage0Pu);

/// <summary>
/// The GB excitation and voltage-control step checks:
/// <list type="bullet">
/// <item><c>avr-step</c> (ECC.A.6.2.4.1): a synchronous unit on open circuit, its AVR reference stepped so the terminal
/// voltage goes from 90 % to 100 % of nominal; the time the voltage first reaches 100 % must be under 0.6 s and the time it
/// settles within 5 % of the change (|V - 1.0| &lt;= 0.005 pu) under 3 s.</item>
/// <item><c>vctrl-step</c> (ECC.A.7.2.3.1): a power park module or HVDC converter in an RMS run with a voltage step made by
/// the given events; the reactive response must commence within 0.2 s, reach 90 % of its change within 1 s (zero to
/// maximum) or 2 s (maximum leading to maximum lagging), and within 5 s of that oscillate peak to peak by less than 5 % of
/// the change (<see cref="StepResponseMetrics.OscillationPeakToPeak"/>: a monotone approach is no oscillation). The MVAr-seconds rule of (i) is not evaluated (its figure labels no level).</item>
/// </list>
/// </summary>
public static class StepResponseChecks
{
    public const string AvrId = "avr-step";
    public const string VctrlId = "vctrl-step";

    /// <summary>2.1.126: the share of its steady change the reactive current must move by before the response counts as
    /// commenced (vctrl-step). 1 % keeps rounding and solver noise out while any real control action exceeds it.</summary>
    public const double CommenceThresholdFraction = 0.01;

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// The open-circuit run of a machine and its excitation system. With no stator current (Id = Iq = 0) the kernel's flux
    /// equations (<see cref="MultiMachineSubtransient"/>, <see cref="SubtransientMachine"/>) reduce to
    /// <c>T'do dE'q/dt = Efd - E'q - E''q Se(E''q)</c> and <c>T''do dE''q/dt = E'q - E''q</c>, with E'd = E''d = 0 and the
    /// terminal voltage E''q; both advance on the same exact zero-order hold with the inputs held across the step, the field
    /// from the controller at the start-of-step voltage error, as in the kernel. The machine starts at equilibrium at
    /// <paramref name="fromPu"/> (E'q = E''q = from, Efd = from + from Se(from), the field current equal to Efd); at t = 0
    /// the reference steps to the value that holds <paramref name="toPu"/> in steady state, toPu + the controller's steady
    /// error at the field toPu + Se(toPu). The speed deviation is 0 and the
    /// electrical power 0. A controller that reads the terminal phasors on a bound machine base is refused.
    /// </summary>
    public static OpenCircuitTrace OpenCircuitStep(SubtransientInit.MachineData machine, IRmsFieldController controller, double fromPu, double toPu,
        double stepSeconds, double durationSeconds)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(controller);
        if (controller is IRmsTerminalFieldController { NeedsTerminalQuantities: true })
            throw new InvalidOperationException("the exciter reads the terminal voltage and current phasors on its bound machine base, which the open-circuit run does not set up.");
        if (!(machine.Tdop > 0.0) || !(machine.Tdopp > 0.0))
            throw new InvalidOperationException("the machine has no d-axis field and damper time constants (a classical machine), so it has no open-circuit field response.");
        int steps = LongFaultCheck.WholeSteps(durationSeconds, stepSeconds, "the run length");
        double ratio = SubtransientMachine.QAxisSaturationRatio(machine.Xd, machine.Xq, machine.Xdpp, machine.Xl);
        double Sat(double eqpp) => machine.Saturation is { } s ? s.GentpjIncrements(eqpp, 0.0, ratio, 0.0).D : 0.0;
        var eqp = new FirstOrderLag(1.0, machine.Tdop);
        var eqpp = new FirstOrderLag(1.0, machine.Tdopp);
        eqp.Initialize(fromPu);
        eqpp.Initialize(fromPu);
        double efd0 = fromPu + Sat(fromPu);
        controller.Initialize(efd0);
        // The reference after the step is the one that holds the machine at toPu in steady state: toPu plus the controller's
        // steady error at the field that holds toPu (2.1.126; a proportional AVR settles short of a plain +0.1 step, which
        // would then be judged against a target it was never set to).
        double vref = toPu + controller.EquilibriumErrorPu(toPu + Sat(toPu));
        var t = new double[steps + 1];
        var v = new double[steps + 1];
        for (int k = 0; k <= steps; k++)
        {
            double e1 = eqp.Output, e2 = eqpp.Output;
            t[k] = k * stepSeconds;
            v[k] = e2;
            if (k == steps) break;
            double sat = Sat(e2);
            double ifd = e1 + sat;
            double err = vref - e2;
            double efd = controller is IRmsFieldCurrentController { NeedsFieldCurrent: true } fc
                ? fc.Step(err, 0.0, ifd, stepSeconds)
                : controller.Step(err, 0.0, stepSeconds);
            eqp.Step(efd - sat, stepSeconds);
            eqpp.Step(e1, stepSeconds);
        }
        return new OpenCircuitTrace(t, v, efd0);
    }

    /// <summary><c>avr-step</c> on a trace (step at t = 0): two rows, the time to first reach <c>to</c> and the settling time
    /// into |V - to| &lt;= band (band = the fraction of the change). Each passes only when strictly under its limit.</summary>
    public static IReadOnlyList<GridCodeCheckResult> JudgeAvr(AvrRequirement req, OpenCircuitTrace trace, IReadOnlyList<string>? notes = null)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(trace);
        var n = new List<string>(notes ?? Array.Empty<string>());
        IReadOnlyList<double> t = trace.TimeSeconds, v = trace.TerminalVoltagePu;
        double? over = StepResponseMetrics.Overshoot(t, v, 0.0, req.FromPu, req.ToPu);
        if (over is double os) n.Add(string.Create(CultureInfo.InvariantCulture, $"overshoot {R(os)} of the change (the clause asks for a damped oscillatory response and sets no overshoot limit)"));
        var rows = new List<GridCodeCheckResult>();
        double? reach = StepResponseMetrics.CrossingSeconds(t, v, 0.0, req.FromPu, req.ToPu, 1.0);
        rows.Add(reach is double r
            ? new GridCodeCheckResult(AvrId, req.Clause, r < req.FirstReachSeconds ? RuleOutcome.Pass : RuleOutcome.Fail, r, req.FirstReachSeconds, req.FirstReachSeconds - r, "s",
                string.Create(CultureInfo.InvariantCulture, $"the terminal voltage first reaches {R(req.ToPu)} pu {R(r)} s after the step (limit: less than {R(req.FirstReachSeconds)} s)"))
            { Case = "first reach", Notes = n }
            : new GridCodeCheckResult(AvrId, req.Clause, RuleOutcome.Fail, null, req.FirstReachSeconds, null, "s",
                string.Create(CultureInfo.InvariantCulture, $"the terminal voltage never reaches {R(req.ToPu)} pu in the {R(t[^1])} s run"))
            { Case = "first reach", Notes = n });
        double band = req.SettleBandFraction * Math.Abs(req.ToPu - req.FromPu);
        double? settle = StepResponseMetrics.SettlingSeconds(t, v, 0.0, req.ToPu, band);
        rows.Add(settle is double s
            ? new GridCodeCheckResult(AvrId, req.Clause, s < req.SettleSeconds ? RuleOutcome.Pass : RuleOutcome.Fail, s, req.SettleSeconds, req.SettleSeconds - s, "s",
                string.Create(CultureInfo.InvariantCulture, $"the terminal voltage stays within {R(band)} pu of {R(req.ToPu)} pu from {R(s)} s after the step (limit: less than {R(req.SettleSeconds)} s)"))
            { Case = "settling" }
            : new GridCodeCheckResult(AvrId, req.Clause, RuleOutcome.Fail, null, req.SettleSeconds, null, "s",
                string.Create(CultureInfo.InvariantCulture, $"the terminal voltage is still outside {R(band)} pu of {R(req.ToPu)} pu at the end of the {R(t[^1])} s run"))
            { Case = "settling" });
        return rows;
    }

    /// <summary>
    /// <c>avr-step</c> for a unit: NOT APPLICABLE unless the unit is a type C or D synchronous module; NOT EVALUATED when it has
    /// no exciter model, when the open-circuit run cannot be set up, or when the run is not longer than the settling limit.
    /// </summary>
    public static IReadOnlyList<GridCodeCheckResult> Avr(AvrRequirement req, ModuleType moduleType, string technology, SubtransientInit.MachineData? machine,
        IRmsFieldController? exciter, double stepSeconds, double durationSeconds)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (!req.Technologies.Contains(technology, StringComparer.Ordinal) || !req.ModuleTypes.Contains(moduleType.ToString(), StringComparer.Ordinal))
            return new[] { new GridCodeCheckResult(AvrId, req.Clause, RuleOutcome.NotApplicable, null, null, null, "s", $"{req.Clause} applies to type C and D synchronous modules") };
        if (machine is null || exciter is null)
            return new[] { new GridCodeCheckResult(AvrId, req.Clause, RuleOutcome.NotEvaluated, null, null, null, "s", "the unit's dynamic data carries no exciter model, so its AVR response is not evaluated") };
        if (!(durationSeconds > req.SettleSeconds))
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"avr-step: the run must last longer than the settling limit {R(req.SettleSeconds)} s."));
        OpenCircuitTrace trace;
        try { trace = OpenCircuitStep(machine, exciter, req.FromPu, req.ToPu, stepSeconds, durationSeconds); }
        catch (InvalidOperationException ex)
        {
            return new[] { new GridCodeCheckResult(AvrId, req.Clause, RuleOutcome.NotEvaluated, null, null, null, "s", "the open-circuit run cannot be set up: " + ex.Message) };
        }
        return JudgeAvr(req, trace, new[]
        {
            "open circuit: the machine's own flux equations with no stator current and no network (the unit isolated, breaker open), not an RMS network run",
            string.Create(CultureInfo.InvariantCulture, $"step of {R(stepSeconds)} s; field voltage at the start {R(trace.FieldVoltage0Pu)} pu"),
        });
    }

    /// <summary>
    /// <c>vctrl-step</c> on sampled traces: <paramref name="qMvar"/> the unit's reactive power and the step at
    /// <paramref name="stepSeconds"/>. The change the time limit is stated for is <paramref name="qmaxMvar"/> (zero to maximum
    /// lagging, a rise), -<paramref name="qminMvar"/> (zero to maximum leading, a fall) or their difference (lead to lag); a
    /// smaller observed change makes the 90 % row NOT DEMONSTRATED (compared exactly, so a change a rounding short of the range
    /// is not demonstrated rather than passed). Q before the step is the last sample before it; the steady value is the last sample.
    /// Commencement (2.1.126) is read from the unit's reactive current <paramref name="reactiveCurrentPu"/> when given (else
    /// from Q): the first sample after the step that has moved from the pre-step value, in the direction of its steady change,
    /// by more than <see cref="CommenceThresholdFraction"/> of that change. Q = V Iq moves with the voltage alone, so a
    /// passive move of Q (a current source whose voltage steps) is no control action; the current moves only when the
    /// control acts.
    /// </summary>
    public static IReadOnlyList<GridCodeCheckResult> JudgeVctrl(VctrlRequirement req, IReadOnlyList<double> times, IReadOnlyList<double> qMvar, double stepSeconds,
        bool leadToLag, double qmaxMvar, double qminMvar, IReadOnlyList<string>? notes = null, IReadOnlyList<double>? reactiveCurrentPu = null)
    {
        if (!double.IsFinite(qmaxMvar) || !double.IsFinite(qminMvar) || !(qmaxMvar > qminMvar))
            throw new ArgumentException("vctrl-step needs finite qmaxMvar > qminMvar (the unit's reactive range).");
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(qMvar);
        var n = new List<string>(notes ?? Array.Empty<string>());
        int pre = -1;
        for (int k = 0; k < times.Count; k++) if (times[k] < stepSeconds) pre = k;
        var rows = new List<GridCodeCheckResult>();
        GridCodeCheckResult Ne(string c, string why) => new(VctrlId, req.Clause, RuleOutcome.NotEvaluated, null, null, null, "s", why) { Case = c, Notes = n };
        rows.Add(new GridCodeCheckResult(VctrlId, req.MvarSecondsClause, RuleOutcome.NotEvaluated, null, null, null, "Mvar s", req.MvarSecondsText) { Case = "MVAr seconds" });
        if (pre < 0 || pre == times.Count - 1)
        {
            rows.Insert(0, Ne("commencement", "the run holds no sample before the step, or none after it"));
            return rows;
        }
        double q0 = qMvar[pre], qf = qMvar[^1], dq = qf - q0;
        if (dq == 0.0)
        {
            rows.Insert(0, Ne("commencement", "the reactive power does not change, so there is no response to measure"));
            return rows;
        }
        IReadOnlyList<double> x = reactiveCurrentPu ?? qMvar;
        if (x.Count != times.Count) throw new ArgumentException("the reactive current trace must have one sample per time.");
        string what = reactiveCurrentPu is null ? "reactive power" : "reactive current";
        double x0 = x[pre], dx = x[^1] - x0;
        double? tc = null;
        if (dx != 0.0)
        {
            double sign = dx > 0.0 ? 1.0 : -1.0, threshold = CommenceThresholdFraction * Math.Abs(dx);
            for (int k = pre + 1; k < times.Count; k++) if (sign * (x[k] - x0) > threshold) { tc = times[k] - stepSeconds; break; }
        }
        n.Add(string.Create(CultureInfo.InvariantCulture, $"commencement: the first sample whose {what} has moved from its pre-step value towards its steady change by more than {R(CommenceThresholdFraction)} of that change"));
        var head = new List<GridCodeCheckResult>
        {
            new(VctrlId, req.Clause + "(i)", tc <= req.CommenceSeconds ? RuleOutcome.Pass : RuleOutcome.Fail, tc, req.CommenceSeconds, req.CommenceSeconds - tc, "s",
                tc is double c0
                    ? string.Create(CultureInfo.InvariantCulture, $"the {what} first moves towards its new value {R(c0)} s after the step (limit {R(req.CommenceSeconds)} s)")
                    : dx == 0.0 ? $"the {what} does not change, so the control does not act" : $"the {what} never moves towards its new value")
            { Case = "commencement", Notes = n },
        };
        double limit = leadToLag ? req.NinetyLeadToLagSeconds : req.NinetyZeroToMaxSeconds;
        double fullRangeMvar = leadToLag ? qmaxMvar - qminMvar : dq > 0.0 ? qmaxMvar : -qminMvar;
        double? t90 = StepResponseMetrics.CrossingSeconds(times, qMvar, stepSeconds, q0, qf, req.NinetyFraction);
        string kind = leadToLag ? "maximum leading to maximum lagging" : "zero to maximum";
        if (Math.Abs(dq) < fullRangeMvar)
            head.Add(new GridCodeCheckResult(VctrlId, req.Clause + "(ii)", RuleOutcome.NotEvaluated, t90, limit, null, "s",
                string.Create(CultureInfo.InvariantCulture, $"the step changes the reactive power by {R(Math.Abs(dq))} Mvar, less than the {R(fullRangeMvar)} Mvar of a {kind} change the time limit is stated for"))
            { Status = "NOT DEMONSTRATED", Case = "90 %" });
        else
            head.Add(new GridCodeCheckResult(VctrlId, req.Clause + "(ii)", t90 <= limit ? RuleOutcome.Pass : RuleOutcome.Fail, t90, limit, limit - t90, "s",
                string.Create(CultureInfo.InvariantCulture, $"90 % of the {R(dq)} Mvar change is reached {R(t90 ?? double.NaN)} s after the step (limit {R(limit)} s, {kind})"))
            { Case = "90 %" });
        if (t90 is not double t9)
            head.Add(Ne("oscillation", "90 % of the change is never reached, so the oscillation window has no start"));
        else if (times[^1] - stepSeconds < t9 + req.OscillationWindowSeconds)
            head.Add(Ne("oscillation", string.Create(CultureInfo.InvariantCulture, $"the run ends before {R(req.OscillationWindowSeconds)} s after reaching 90 %")));
        else
        {
            double p2p = StepResponseMetrics.OscillationPeakToPeak(times, qMvar, stepSeconds, t9, t9 + req.OscillationWindowSeconds) ?? 0.0;
            double lim = req.OscillationFraction * Math.Abs(dq);
            head.Add(new GridCodeCheckResult(VctrlId, req.Clause + "(iv)", p2p < lim ? RuleOutcome.Pass : RuleOutcome.Fail, p2p, lim, lim - p2p, "Mvar",
                string.Create(CultureInfo.InvariantCulture, $"peak to peak {R(p2p)} Mvar over the {R(req.OscillationWindowSeconds)} s after reaching 90 % (limit: less than {R(req.OscillationFraction)} of the {R(Math.Abs(dq))} Mvar steady change)"))
            { Case = "oscillation" });
        }
        head.AddRange(rows);
        return head;
    }

    /// <summary>
    /// <c>vctrl-step</c> for a unit: the RMS run of <paramref name="events"/> (the voltage step) with the PCC measured, the
    /// unit's reactive power from its own converter trace. NOT APPLICABLE unless the unit is a power park module or HVDC
    /// converter; NOT EVALUATED when the RMS study does not run it as converter plant or shares its trace with other units.
    /// </summary>
    public static IReadOnlyList<GridCodeCheckResult> Vctrl(VctrlRequirement req, string technology, GridModel model, PowerFlowResult pf,
        IReadOnlyList<SubtransientInit.MachineData> data, int unitIndex, int pccBus, IReadOnlyList<RmsEvent> events, double stepSeconds, double durationSeconds,
        bool leadToLag, double qmaxMvar, double qminMvar)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(events);
        GridCodeCheckResult Single(RuleOutcome o, string why) => new(VctrlId, req.Clause, o, null, null, null, "s", why);
        if (!req.Technologies.Contains(technology, StringComparer.Ordinal))
            return new[] { Single(RuleOutcome.NotApplicable, $"{req.Clause} applies to onshore power park modules and HVDC converters (Appendix E7)") };
        Generator gen = model.Generators[unitIndex];
        if (!SubtransientInit.IsConverterUnit(gen, data[unitIndex]))
            return new[] { Single(RuleOutcome.NotEvaluated, "the RMS study runs the unit as a synchronous machine, so it has no converter reactive-power trace") };
        if (events.Count == 0) throw new ArgumentException("vctrl-step needs the events that make the voltage step.");
        RmsTransientStability.ObservedStudy obs = RmsTransientStability.StudyEventsObserved(model, pf, data, events, new[] { pccBus },
            stepSeconds: stepSeconds, durationSeconds: durationSeconds);
        var traces = obs.Result.Converters?.Where(c => c.Bus == gen.Bus).ToArray() ?? Array.Empty<RmsConverterTrace>();
        string own = gen.Name ?? $"converter@bus {gen.Bus}";
        if (traces.Length != 1 || traces[0].Name != own)
            return new[] { Single(RuleOutcome.NotEvaluated, "the RMS study has no trace of the unit's own reactive power (none, or one shared with other converters at its bus)") };
        var q = traces[0].ReactivePowerPu.Select(x => x * model.BaseMva).ToArray();
        IReadOnlyList<double> iq = traces[0].ReactiveCurrentPu;
        double first = events.Min(e => e.AtSeconds);
        double vBefore = double.NaN, vAfter = obs.Buses.MagnitudePu[0][^1];
        for (int k = 0; k < obs.Buses.TimeSeconds.Count; k++) if (obs.Buses.TimeSeconds[k] < first) vBefore = obs.Buses.MagnitudePu[0][k];
        return JudgeVctrl(req, obs.Buses.TimeSeconds, q, first, leadToLag, qmaxMvar, qminMvar, new[]
        {
            string.Create(CultureInfo.InvariantCulture, $"the step: {string.Join("; ", events.Select(e => e.Describe()))}; PCC voltage {R(vBefore)} pu before, {R(vAfter)} pu at the end of the run"),
            "the reactive power is the unit's own, at its terminals; the clause measures at the connection point",
        }, iq);
    }
}
