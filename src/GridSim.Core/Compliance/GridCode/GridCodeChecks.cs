using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Harmonics.Planning;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;

namespace GridSim.Core.Compliance.GridCode;

/// <summary>
/// One check's outcome: the value found, the limit it was compared with, the signed margin (positive = inside the
/// requirement), the unit, the clause and the reason when the check was not evaluated. <see cref="Status"/> is the word the
/// result prints: PASS, FAIL, NOT EVALUATED, NOT DEMONSTRATED (an FRT or RoCoF test more severe than the requirement: a trip
/// is then allowed, so compliance is not shown; counted as not evaluated), NOT APPLICABLE or INFO (a helper value, no verdict).
/// </summary>
public sealed record GridCodeCheckResult(string Id, string Clause, RuleOutcome Outcome, double? Value, double? Limit, double? Margin,
    string Unit, string Reason)
{
    /// <summary>The printed status (see the type summary).</summary>
    public string Status { get; init; } = GridCodeChecks.StatusOf(Outcome);
    /// <summary>Which stage, voltage or side the row is about (empty for a single-row check).</summary>
    public string Case { get; init; } = "";
    /// <summary>The time (s from fault inception or from the start of the run) the margin was found at, when it has one.</summary>
    public double? AtSeconds { get; init; }
    /// <summary>Notes on what the check did and did not represent.</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>A relay stage given to the static setting checks: function <c>27</c>, <c>59</c>, <c>81U</c>, <c>81O</c> or
/// <c>81R</c>, its pickup (pu, Hz or Hz/s), its definite delay (s) and, for 81R, its measurement window (s).</summary>
public sealed record GridCodeRelayStage(string Device, string Function, double Pickup, double DelaySeconds, double? WindowSeconds = null)
{
    /// <summary>The stages of a unit's resolved protection (<see cref="ResolvedUnitProtection"/>) in these terms; functions the
    /// grid-code checks do not read (32, 40, 78) are left out.</summary>
    public static IReadOnlyList<GridCodeRelayStage> FromUnitProtection(ResolvedUnitProtection unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var list = new List<GridCodeRelayStage>();
        foreach (UnitRelayStage s in unit.Stages)
        {
            string? f = s.Function switch
            {
                UnitRelayFunction.UnderVoltage => "27", UnitRelayFunction.OverVoltage => "59",
                UnitRelayFunction.UnderFrequency => "81U", UnitRelayFunction.OverFrequency => "81O",
                UnitRelayFunction.Rocof => "81R", _ => null,
            };
            if (f is not null) list.Add(new GridCodeRelayStage(s.Device, f, s.Pickup.Value, s.Delay.Value));
        }
        return list;
    }
}

/// <summary>The fault an FRT check applies: type, impedance (null for the derived retained-voltage reactance of a
/// three-phase fault), bus (null = the PCC) and inception time.</summary>
public sealed record FrtFault(FaultType Type = FaultType.ThreePhase, double? ResistancePu = null, double? ReactancePu = null, int? BusId = null,
    double AtSeconds = 0.1);

/// <summary>The pre-fault conditions an FRT check is judged under: the clause, what the operating point fails to meet
/// (null when it meets them, or when the code sets none the study can check) and notes on what is not imposed.</summary>
public sealed record FrtPreFault(string Clause, string? Problem, IReadOnlyList<string> Notes);

/// <summary>One FRT run: the events applied, the observed run with the PCC measured and, for an unbalanced fault at the PCC,
/// the fault-point trace. <see cref="Observed"/> is exactly <see cref="RmsTransientStability.StudyEventsObserved"/> for the
/// same inputs.</summary>
public sealed record FrtRun(FrtProfile Profile, FrtFault Fault, int FaultBus, double ResistancePu, double ReactancePu, IReadOnlyList<RmsEvent> Events,
    double StepSeconds, double DurationSeconds, RmsTransientStability.ObservedStudy Observed, RmsFaultStudyResult? FaultTrace);

/// <summary>
/// The grid-code checks, one static method per check id. Pure functions of their inputs; the RMS checks call the existing
/// study entry points unchanged, so a study run here gives the same bits as the same study run elsewhere. A check that
/// cannot be evaluated is never a pass.
/// </summary>
public static class GridCodeChecks
{
    /// <summary>The FRT margin tolerance (pu): a margin at or above -1e-9 is on the line.</summary>
    public const double FrtTolerancePu = 1e-9;

    /// <summary>The window after Trec3 the FRT margin is taken over (s).</summary>
    public const double FrtWindowAfterTrec3Seconds = 1.0;

    /// <summary>The note an unbalanced FRT check carries when the PCC is not the fault point.</summary>
    public const string PhaseVoltagesNote = "phase voltages away from the fault point are not represented";

    /// <summary>How far above Uret (pu) the lowest PCC voltage while the fault is in place may lie and still count as the
    /// code's retained voltage. A milder fault does not test the line. 1e-3 pu admits a bolted fault (the RMS engine stamps
    /// it as a 1e7 pu shunt, which leaves a few micro-pu) and the flat-start retained-voltage reactance.</summary>
    public const double FrtDepthTolerancePu = 1e-3;

    /// <summary>How far from 1.0 pu (pu) the pre-fault PCC voltage may be under ECC.6.3.15.8.</summary>
    public const double PreFaultVoltageTolerancePu = 1e-3;

    /// <summary>The note every FRT result carries: what the run compares and what it does not impose.</summary>
    public const string RecoveryNote = "after clearing, the PCC voltage follows the network's own recovery; the run compares it with the line but does not shape it to the line";

    internal static string StatusOf(RuleOutcome o) => o switch
    {
        RuleOutcome.Pass => "PASS", RuleOutcome.Fail => "FAIL", RuleOutcome.NotApplicable => "NOT APPLICABLE",
        RuleOutcome.OutOfScope => "OUT OF SCOPE", _ => "NOT EVALUATED",
    };

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static GridCodeCheckResult NotEvaluated(string id, string clause, string reason, string unit = "") =>
        new(id, clause, RuleOutcome.NotEvaluated, null, null, null, unit, reason);

    private static RuleOutcome PassIf(bool ok) => ok ? RuleOutcome.Pass : RuleOutcome.Fail;

    // ------------------------------------------------------------------------------------------------ static checks

    /// <summary>
    /// <c>frt-uv-settings</c>: each undervoltage (27) stage with pickup Vp and delay td against the FRT line. T_below(Vp) is the
    /// total time the line is below Vp (<see cref="FrtProfile.TimeBelow"/>); the margin is td - T_below (s) and the stage
    /// passes when it is 0 or more. A stage whose pickup is above Urec2 would trip after any fault: FAIL with no margin.
    /// </summary>
    public static IReadOnlyList<GridCodeCheckResult> FrtUvSettings(FrtProfile profile, IReadOnlyList<GridCodeRelayStage> stages)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(stages);
        const string id = "frt-uv-settings";
        var list = new List<GridCodeCheckResult>();
        foreach (GridCodeRelayStage s in stages.Where(s => s.Function == "27"))
        {
            double below = profile.TimeBelow(s.Pickup);
            if (double.IsPositiveInfinity(below))
            {
                list.Add(new GridCodeCheckResult(id, profile.Clause, RuleOutcome.Fail, s.DelaySeconds, null, null, "s",
                    $"the pickup {R(s.Pickup)} pu is above Urec2 = {R(profile.Urec2)} pu, so the stage trips after any fault the line allows") { Case = s.Device });
                continue;
            }
            double margin = s.DelaySeconds - below;
            list.Add(new GridCodeCheckResult(id, profile.Clause, PassIf(margin >= 0.0), s.DelaySeconds, below, margin, "s",
                $"the line is below {R(s.Pickup)} pu for {R(below)} s") { Case = s.Device });
        }
        if (list.Count == 0) list.Add(new GridCodeCheckResult(id, profile.Clause, RuleOutcome.NotApplicable, null, null, null, "s", "no undervoltage (27) stage given"));
        return list;
    }

    /// <summary><c>rocof-settings</c>: each 81R stage against the withstand RoCoF: margin = pickup - limit (Hz/s). A stage
    /// whose measurement window is not the code's carries a note.</summary>
    public static IReadOnlyList<GridCodeCheckResult> RocofSettings(double limitHzPerSecond, double windowSeconds, string clause, IReadOnlyList<GridCodeRelayStage> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        const string id = "rocof-settings";
        var list = new List<GridCodeCheckResult>();
        foreach (GridCodeRelayStage s in stages.Where(s => s.Function == "81R"))
        {
            double margin = s.Pickup - limitHzPerSecond;
            var notes = new List<string>();
            if (s.WindowSeconds is double w && w != windowSeconds)
                notes.Add($"the relay measures over {R(w)} s; the requirement is stated over {R(windowSeconds)} s");
            list.Add(new GridCodeCheckResult(id, clause, PassIf(margin >= 0.0), s.Pickup, limitHzPerSecond, margin, "Hz/s",
                $"withstand {R(limitHzPerSecond)} Hz/s over {R(windowSeconds)} s") { Case = s.Device, Notes = notes });
        }
        if (list.Count == 0) list.Add(new GridCodeCheckResult(id, clause, RuleOutcome.NotApplicable, null, null, null, "Hz/s", "no RoCoF (81R) stage given"));
        return list;
    }

    /// <summary>
    /// <c>frequency-relays</c>: each 81U / 81O stage against the frequency withstand bands and the no-trip range
    /// (<paramref name="noTripLowHz"/>, <paramref name="noTripHighHz"/>). An 81U stage operates below its pickup p: p above
    /// the no-trip floor is a FAIL (margin floor - p, Hz); otherwise in the band lo &lt; p &lt;= hi the margin is delay - the
    /// band's minimum time (s). An 81O stage mirrors it (p below the no-trip ceiling FAILs; band lo &lt;= p &lt; hi). A stage
    /// beyond every band passes with its distance from the last band (Hz).
    /// </summary>
    public static IReadOnlyList<GridCodeCheckResult> FrequencyRelays(IReadOnlyList<FrequencyBand> bands, string bandsClause, double noTripLowHz, double noTripHighHz,
        string noTripClause, IReadOnlyList<GridCodeRelayStage> stages)
    {
        ArgumentNullException.ThrowIfNull(bands);
        ArgumentNullException.ThrowIfNull(stages);
        const string id = "frequency-relays";
        var list = new List<GridCodeCheckResult>();
        foreach (GridCodeRelayStage s in stages.Where(s => s.Function is "81U" or "81O"))
        {
            double p = s.Pickup;
            bool under = s.Function == "81U";
            if (under ? p > noTripLowHz : p < noTripHighHz)
            {
                double m = under ? noTripLowHz - p : p - noTripHighHz;
                list.Add(new GridCodeCheckResult(id, noTripClause, RuleOutcome.Fail, p, under ? noTripLowHz : noTripHighHz, m, "Hz",
                    $"the stage operates inside the no-trip range {R(noTripLowHz)} to {R(noTripHighHz)} Hz") { Case = s.Device });
                continue;
            }
            FrequencyBand? band = bands.FirstOrDefault(b => under ? b.LowHz < p && p <= b.HighHz : b.LowHz <= p && p < b.HighHz);
            if (band is null)
            {
                double edge = under ? bands.Min(b => b.LowHz) : bands.Max(b => b.HighHz);
                double m = under ? edge - p : p - edge;
                list.Add(new GridCodeCheckResult(id, bandsClause, RuleOutcome.Pass, p, edge, m, "Hz",
                    "the stage operates beyond every withstand band") { Case = s.Device });
                continue;
            }
            if (band.MinSeconds is not double need)
            {
                list.Add(new GridCodeCheckResult(id, bandsClause, RuleOutcome.Fail, p, null, null, "Hz",
                    $"the stage operates in the {R(band.LowHz)} to {R(band.HighHz)} Hz band, which is {band.Printed}") { Case = s.Device });
                continue;
            }
            double margin = s.DelaySeconds - need;
            list.Add(new GridCodeCheckResult(id, bandsClause, PassIf(margin >= 0.0), s.DelaySeconds, need, margin, "s",
                $"the {R(band.LowHz)} to {R(band.HighHz)} Hz band: {band.Printed}") { Case = s.Device });
        }
        if (list.Count == 0) list.Add(new GridCodeCheckResult(id, bandsClause, RuleOutcome.NotApplicable, null, null, null, "Hz", "no frequency (81U / 81O) stage given"));
        return list;
    }

    // ------------------------------------------------------------------------------------------------ FRT helper

    /// <summary>The fault reactance that gives <paramref name="uret"/> at a bus with Thevenin reactance <paramref name="xth"/>
    /// and a flat pre-fault voltage: Xf = Uret * Xth / (1 - Uret). Uret = 0 is the bolted fault (0).</summary>
    public static double RetainedVoltageReactance(double uret, double xth)
    {
        if (!(uret >= 0.0 && uret < 1.0)) throw new ArgumentOutOfRangeException(nameof(uret), uret, "Uret must lie in [0, 1) pu.");
        if (!(xth > 0.0) || !double.IsFinite(xth)) throw new ArgumentOutOfRangeException(nameof(xth), xth, "Xth must be positive and finite.");
        return uret * xth / (1.0 - uret);
    }

    /// <summary>The positive-sequence Thevenin impedance magnitude at <paramref name="busId"/> from the static three-phase
    /// fault study (<see cref="FaultAnalysis.ThreePhase"/>: each machine behind its case X''d, loads as constant admittance).</summary>
    public static double TheveninPu(GridModel model, PowerFlowResult pf, int busId) => FaultAnalysis.ThreePhase(model, pf, busId).TheveninZPu;

    /// <summary><c>frt-retained-voltage</c> (helper, no verdict): the fault reactance that gives Uret at the PCC.</summary>
    public static GridCodeCheckResult FrtRetainedVoltage(GridModel model, PowerFlowResult pf, int pccBus, FrtProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        double xth = TheveninPu(model, pf, pccBus);
        double xf = RetainedVoltageReactance(profile.Uret, xth);
        return new GridCodeCheckResult("frt-retained-voltage", profile.Clause, RuleOutcome.NotApplicable, xf, null, null, "pu",
            $"Xf = Uret Xth / (1 - Uret) with Uret = {R(profile.Uret)} pu and Xth = {R(xth)} pu (static three-phase fault study, flat pre-fault view)"
                + (profile.Uret == 0.0 ? "; Uret = 0 is a bolted fault" : ""))
        { Status = "INFO" };
    }

    // ------------------------------------------------------------------------------------------------ FRT on RMS

    /// <summary>
    /// Run the FRT study: <see cref="RmsSequenceFault"/> at the fault bus (the PCC unless given), cleared by
    /// <see cref="RmsFaultClear"/> Tclear later, through <see cref="RmsTransientStability.StudyEventsObserved"/> with the PCC
    /// measured; for an unbalanced fault at the PCC also <see cref="RmsTransientStability.StudyEventsWithFaults"/> for the
    /// phase voltages. A three-phase fault with no impedance given takes the retained-voltage reactance. The default run
    /// lasts to Trec3 + 1 s after inception.
    /// </summary>
    public static FrtRun RunFrt(GridModel model, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data, int pccBus, FrtProfile profile,
        FrtFault fault, double stepSeconds, double? durationSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(fault);
        int bus = fault.BusId ?? pccBus;
        double r = fault.ResistancePu ?? 0.0, x;
        if (fault.ReactancePu is double xf) x = xf;
        else if (fault.ResistancePu is not null) x = 0.0;
        else
        {
            if (fault.Type != FaultType.ThreePhase)
                throw new ArgumentException("An unbalanced FRT fault needs its impedance (the retained-voltage reactance is derived for a three-phase fault only).", nameof(fault));
            x = RetainedVoltageReactance(profile.Uret, TheveninPu(model, pf, bus));
        }
        if (!(fault.AtSeconds > 0.0) || !double.IsFinite(fault.AtSeconds)) throw new ArgumentOutOfRangeException(nameof(fault), "The fault inception time must be above 0 s.");
        double duration = durationSeconds ?? fault.AtSeconds + profile.Trec3 + FrtWindowAfterTrec3Seconds;
        var events = new RmsEvent[] { new RmsSequenceFault(fault.AtSeconds, bus, fault.Type, r, x), new RmsFaultClear(fault.AtSeconds + profile.Tclear, bus) };
        RmsTransientStability.ObservedStudy observed = RmsTransientStability.StudyEventsObserved(model, pf, data, events, new[] { pccBus },
            stepSeconds: stepSeconds, durationSeconds: duration);
        RmsFaultStudyResult? trace = fault.Type != FaultType.ThreePhase && bus == pccBus
            ? RmsTransientStability.StudyEventsWithFaults(model, pf, data, events, stepSeconds: stepSeconds, durationSeconds: duration)
            : null;
        return new FrtRun(profile, fault, bus, r, x, events, stepSeconds, duration, observed, trace);
    }

    /// <summary>The voltage the FRT line is compared with at each observed sample: the PCC magnitude, or the smallest phase
    /// voltage while an unbalanced fault at the PCC is in place.</summary>
    public static double[] FrtVoltages(FrtRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        IReadOnlyList<double> times = run.Observed.Buses.TimeSeconds;
        double[] mag = run.Observed.Buses.MagnitudePu[0];
        var v = (double[])mag.Clone();
        if (run.FaultTrace is { } ft && ft.Faults.Count > 0)
        {
            var byTime = new Dictionary<double, RmsFaultSample>();
            foreach (RmsFaultSample s in ft.Faults[0].Samples) byTime[s.TimeSeconds] = s;
            int joined = 0;
            for (int k = 0; k < times.Count; k++)
                if (byTime.TryGetValue(times[k], out RmsFaultSample? s))
                {
                    joined++;
                    if (!s.Energised) continue;
                    (double a, double b, double c) = s.PhaseVoltagesPu;
                    v[k] = Math.Min(a, Math.Min(b, c));
                }
            // SEC-34: the two runs are joined by sample time; a fault sample whose time matches no observed sample (a last-bit
            // difference between the runs) was silently left at the positive-sequence value, the less severe figure. Every
            // fault sample must find its observed sample, or the check is refused.
            if (joined != byTime.Count)
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"FRT: {byTime.Count - joined} of the {byTime.Count} fault-trace samples have no observed sample at the same time, so the phase voltages cannot be joined to the PCC trace."));
        }
        return v;
    }

    /// <summary>
    /// The ECC.6.3.15.8 pre-fault conditions of a GB FRT test: the unit at its rated MW (P(0-) at Pmax) and the PCC at
    /// 1.0 pu before the fault. <see cref="FrtPreFault.Problem"/> names what the operating point misses (a unit at the
    /// slack bus has no set output, so its rated MW is not confirmed). The maximum leading power factor of ECC.6.3.15.8(i)
    /// is not imposed; the result carries a note saying so.
    /// </summary>
    public static FrtPreFault GbPreFault(GridModel model, PowerFlowResult pf, int unitIndex, int pccBus, string clause)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(pf);
        Generator gen = model.Generators[unitIndex];
        var problems = new List<string>();
        Bus? bus = model.Buses.FirstOrDefault(b => b.Id == gen.Bus);
        if (bus?.Type == BusType.Slack)
            problems.Add("the unit sits at the slack bus, so its pre-fault output is the power-flow balance, not a set rated MW");
        else if (!double.IsFinite(gen.Pmax))
            problems.Add("the unit's Pmax is not given, so its rated MW output before the fault is not confirmed");
        else if (gen.Pg < gen.Pmax - 1e-9 * Math.Max(1.0, Math.Abs(gen.Pmax)))
            problems.Add($"P(0-) = {R(gen.Pg)} MW is below the rated MW {R(gen.Pmax)} MW");
        int i = pf.BusIds.ToList().IndexOf(pccBus);
        if (i < 0) problems.Add($"the PCC bus {pccBus} is not in the power-flow result");
        else if (!(Math.Abs(pf.Vm[i] - 1.0) <= PreFaultVoltageTolerancePu))
            problems.Add($"V(0-) at the PCC is {R(pf.Vm[i])} pu, not 1.0 pu");
        return new FrtPreFault(clause, problems.Count == 0 ? null : string.Join("; ", problems), new[]
        {
            $"{clause}(i) also asks for the maximum leading power factor before the fault; the study runs at the case's reactive output and does not impose it",
        });
    }

    /// <summary>
    /// <c>frt-envelope</c>. The samples with t in [0, Trec3 + 1 s] from inception are split into the fault period (the
    /// fault in place) and the time after clearing. In order:
    /// <list type="bullet">
    /// <item>the fault-period voltage goes below Uret by more than 1e-9 pu: NOT DEMONSTRATED (the test is more severe than
    /// the line, so a trip is allowed), with the time of the minimum;</item>
    /// <item>the lowest fault-period voltage lies above Uret by more than <see cref="FrtDepthTolerancePu"/>: NOT
    /// DEMONSTRATED (the test is milder than the line, so it does not show ride-through on it);</item>
    /// <item>the pre-fault conditions (<paramref name="preFault"/>) are not met: NOT DEMONSTRATED;</item>
    /// <item>the unit did not stay connected (synchronism lost, or a unit relay tripped it): FAIL; staying connected cannot
    /// be judged (a DER_A unit, or a unit relay stage the RMS run does not evaluate): NOT EVALUATED;</item>
    /// <item>the voltage after clearing goes below the line by more than 1e-9 pu: NOT DEMONSTRATED (the run is more
    /// severe than the line there);</item>
    /// <item>otherwise PASS.</item>
    /// </list>
    /// The margin reported is min of V(t) - U(t) over the whole window. After clearing the voltage is the network's own
    /// recovery; it is compared with the line, not shaped to it.
    /// </summary>
    public static GridCodeCheckResult FrtEnvelope(FrtRun run, GridModel model, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data, int unitIndex, int pccBus,
        FrtPreFault? preFault = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(data);
        const string id = "frt-envelope";
        FrtProfile p = run.Profile;
        var notes = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"{RmsSequenceFaults.TypeText(run.Fault.Type)} fault at bus {run.FaultBus} through {R(run.ResistancePu)} + j{R(run.ReactancePu)} pu at {R(run.Fault.AtSeconds)} s, cleared {R(p.Tclear)} s later"),
            RecoveryNote,
        };
        if (run.Fault.Type != FaultType.ThreePhase && run.FaultBus != pccBus) notes.Add(PhaseVoltagesNote);
        if (preFault is not null) notes.AddRange(preFault.Notes);
        IReadOnlyList<double> times = run.Observed.Buses.TimeSeconds;
        double[] v = FrtVoltages(run);
        double end = p.Trec3 + FrtWindowAfterTrec3Seconds;
        double half = 0.5 * run.StepSeconds;
        double m = double.PositiveInfinity, at = double.NaN, vAt = double.NaN, uAt = double.NaN;
        double mF = double.PositiveInfinity, atF = double.NaN, vF = double.NaN, uF = double.NaN;
        double vMin = double.PositiveInfinity, atMin = double.NaN;
        double mP = double.PositiveInfinity, atP = double.NaN;
        for (int k = 0; k < times.Count; k++)
        {
            double tr = times[k] - run.Fault.AtSeconds;
            if (tr < -half) continue;
            if (tr > end + 1e-9) break;
            double t = Math.Max(tr, 0.0);
            double u = p.LowerLimitPu(t);
            double d = v[k] - u;
            if (d < m) { m = d; at = t; vAt = v[k]; uAt = u; }
            if (tr < p.Tclear - half)
            {
                if (d < mF) { mF = d; atF = t; vF = v[k]; uF = u; }
                if (v[k] < vMin) { vMin = v[k]; atMin = t; }
            }
            else if (d < mP) { mP = d; atP = t; }
        }
        if (double.IsPositiveInfinity(m)) return NotEvaluated(id, p.Clause, "the run holds no sample after fault inception", "pu");
        if (double.IsPositiveInfinity(mF)) return NotEvaluated(id, p.Clause, "the run holds no sample while the fault is in place", "pu");
        if (mF < -FrtTolerancePu)
            return new GridCodeCheckResult(id, p.Clause, RuleOutcome.NotEvaluated, vF, uF, mF, "pu",
                "the voltage at the PCC went below the line during the fault: the test is more severe than the requirement, so a trip is allowed and compliance is not shown")
            { Status = "NOT DEMONSTRATED", AtSeconds = atF, Notes = notes };
        if (vMin - p.Uret > FrtDepthTolerancePu)
            return new GridCodeCheckResult(id, p.Clause, RuleOutcome.NotEvaluated, vMin, p.Uret, null, "pu",
                string.Create(CultureInfo.InvariantCulture, $"the lowest PCC voltage during the fault is {R(vMin)} pu, above Uret = {R(p.Uret)} pu by more than {R(FrtDepthTolerancePu)} pu: the test is milder than the line, so it does not show ride-through on it"))
            { Status = "NOT DEMONSTRATED", AtSeconds = atMin, Notes = notes };
        if (preFault?.Problem is string problem)
            return new GridCodeCheckResult(id, p.Clause, RuleOutcome.NotEvaluated, vAt, uAt, m, "pu",
                $"the pre-fault conditions of {preFault.Clause} are not met: {problem}")
            { Status = "NOT DEMONSTRATED", AtSeconds = at, Notes = notes };
        (bool? connected, string why) = StayedConnected(run.Events, run, model, pf, data, unitIndex);
        if (connected is null)
            return new GridCodeCheckResult(id, p.Clause, RuleOutcome.NotEvaluated, vAt, uAt, m, "pu", why) { AtSeconds = at, Notes = notes };
        bool below = mP < -FrtTolerancePu;
        if (!connected.Value)
        {
            if (below) notes.Add(string.Create(CultureInfo.InvariantCulture, $"the PCC voltage after clearing also went below the line ({R(mP)} pu at {R(atP)} s)"));
            return new GridCodeCheckResult(id, p.Clause, RuleOutcome.Fail, vAt, uAt, m, "pu", why) { AtSeconds = at, Notes = notes };
        }
        if (below)
            return new GridCodeCheckResult(id, p.Clause, RuleOutcome.NotEvaluated, vAt, uAt, m, "pu",
                "the voltage at the PCC went below the line after clearing: the run is more severe than the requirement there, so compliance is not shown")
            { Status = "NOT DEMONSTRATED", AtSeconds = atP, Notes = notes };
        return new GridCodeCheckResult(id, p.Clause, RuleOutcome.Pass, vAt, uAt, m, "pu", why) { AtSeconds = at, Notes = notes };
    }

    private static (bool? Connected, string Why) StayedConnected(IReadOnlyList<RmsEvent> events, FrtRun run, GridModel model, PowerFlowResult pf,
        IReadOnlyList<SubtransientInit.MachineData> data, int unitIndex)
        => StayedConnected(events, run.Observed, run.StepSeconds, run.DurationSeconds, model, pf, data, unitIndex);

    /// <summary>Whether the unit stayed connected through an observed run of <paramref name="events"/>: false when synchronism
    /// was lost or one of its unit relay stages tripped it, null when that cannot be judged, true otherwise (RC5 WP22: shared
    /// with the faults over 140 ms; the frt-envelope path above is the same arithmetic).</summary>
    internal static (bool? Connected, string Why) StayedConnected(IReadOnlyList<RmsEvent> events, RmsTransientStability.ObservedStudy observed,
        double stepSeconds, double durationSeconds, GridModel model, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data, int unitIndex)
    {
        Generator gen = model.Generators[unitIndex];
        bool converter = SubtransientInit.IsConverterUnit(gen, data[unitIndex]);
        if (!observed.Result.StayedInSynchronism) return (false, "synchronism was lost");
        if (converter && data[unitIndex].Renewable?.Der is not null)
            return (null, "the unit is a DER_A model, whose own trip the RMS trace does not carry; staying connected is not evaluated");
        string? name = gen.Name;
        if (model.UnitProtection is { } up && name is not null && up.For(name) is { Stages.Count: > 0 })
        {
            RmsTransientStability.UnitRelayStudy relays = RmsTransientStability.StudyEventsWithUnitRelays(model, pf, data, events,
                stepSeconds: stepSeconds, durationSeconds: durationSeconds);
            FullModelUnitRelayRecord? trip = relays.UnitRelays.FirstOrDefault(r => r.Unit == name && r.Tripped);
            if (trip is not null) return (false, string.Create(CultureInfo.InvariantCulture, $"the unit's {trip.Device} stage tripped it at {R(trip.OperateSeconds ?? double.NaN)} s"));
            var skipped = relays.NotEvaluated.Where(n => n.Unit == name).ToArray();
            if (skipped.Length > 0)
                return (null, "the RMS run does not evaluate the unit's " + string.Join(", ", skipped.Select(s => s.Device)) + " stage" + (skipped.Length > 1 ? "s" : "")
                    + " (" + skipped[0].Reason + "), so staying connected is not evaluated");
            return (true, "the unit stayed connected: synchronism kept and no unit relay operated");
        }
        return (true, converter ? "synchronism kept; the case carries no unit protection for the unit" : "synchronism kept");
    }

    /// <summary>
    /// <c>frt-recovery</c> (ECC.6.3.15.8(vii)): for a converter unit, tv is the first sample after clearing with V_PCC at or
    /// above <paramref name="voltagePu"/>; the margin is min over t &gt;= tv + <paramref name="delaySeconds"/> of
    /// P(t) - <paramref name="fraction"/> P(0-) (pu, system base), P from the converter trace at the unit's bus (the RMS
    /// study aggregates the converter units of a bus). A synchronous unit is NOT EVALUATED: its electrical power is not in
    /// the RMS traces. When <paramref name="preFault"/> names a pre-fault condition the operating point misses, the result is
    /// NOT DEMONSTRATED. The result holds for the one fault applied.
    /// </summary>
    public static GridCodeCheckResult FrtRecovery(FrtRun run, GridModel model, IReadOnlyList<SubtransientInit.MachineData> data, int unitIndex,
        string clause, double voltagePu = 0.9, double delaySeconds = 0.5, double fraction = 0.9, FrtPreFault? preFault = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(data);
        const string id = "frt-recovery";
        Generator gen = model.Generators[unitIndex];
        if (!SubtransientInit.IsConverterUnit(gen, data[unitIndex]))
            return NotEvaluated(id, clause, "a synchronous unit's electrical power is not in the RMS traces, so its active-power recovery is not evaluated", "pu");
        if (preFault?.Problem is string problem)
            return new GridCodeCheckResult(id, clause, RuleOutcome.NotEvaluated, null, null, null, "pu", $"the pre-fault conditions of {preFault.Clause} are not met: {problem}")
            { Status = "NOT DEMONSTRATED", Notes = preFault.Notes };
        RmsConverterTrace? trace = run.Observed.Result.Converters?.FirstOrDefault(c => c.Bus == gen.Bus);
        IReadOnlyList<double> times = run.Observed.Buses.TimeSeconds;
        if (trace is null) return NotEvaluated(id, clause, "no converter trace at the unit's bus", "pu");
        if (trace.ActivePowerPu.Count != times.Count)
            throw new InvalidOperationException("internal error: the converter trace and the bus voltage trace have different sample counts.");
        double[] v = run.Observed.Buses.MagnitudePu[0];
        double tol = 1e-6 * run.StepSeconds;
        int pre = -1;
        for (int k = 0; k < times.Count; k++) if (times[k] < run.Fault.AtSeconds - tol) pre = k;
        if (pre < 0) return NotEvaluated(id, clause, "the run holds no sample before the fault", "pu");
        double p0 = trace.ActivePowerPu[pre];
        if (!(p0 > 0.0)) return NotEvaluated(id, clause, "the unit's pre-fault active power is not positive", "pu");
        double clear = run.Fault.AtSeconds + run.Profile.Tclear;
        int kv = -1;
        for (int k = 0; k < times.Count; k++) if (times[k] >= clear - tol && v[k] >= voltagePu) { kv = k; break; }
        if (kv < 0) return NotEvaluated(id, clause, $"the PCC voltage did not return to {R(voltagePu)} pu within the run", "pu");
        double tv = times[kv];
        double m = double.PositiveInfinity, at = double.NaN, pAt = double.NaN;
        for (int k = kv; k < times.Count; k++)
        {
            if (times[k] < tv + delaySeconds - tol) continue;
            double d = trace.ActivePowerPu[k] - fraction * p0;
            if (d < m) { m = d; at = times[k]; pAt = trace.ActivePowerPu[k]; }
        }
        if (double.IsPositiveInfinity(m)) return NotEvaluated(id, clause, $"the run ends before tv + {R(delaySeconds)} s", "pu");
        var notes = new List<string> { "the recovery is shown for the one fault applied" };
        if (preFault is not null) notes.AddRange(preFault.Notes);
        return new GridCodeCheckResult(id, clause, PassIf(m >= 0.0), pAt, fraction * p0, m, "pu",
            string.Create(CultureInfo.InvariantCulture, $"the PCC voltage returned to {R(voltagePu)} pu at {R(tv)} s; P(0-) = {R(p0)} pu"))
        { AtSeconds = at, Notes = notes };
    }

    // ------------------------------------------------------------------------------------------------ reactive capability

    /// <summary>The reactive power the plant delivers into the PCC (Mvar) with the unit at (P, Q) and the PCC held at
    /// <paramref name="vPccPu"/>: the sub-model of the plant buses and the branches among them, the PCC as the slack, the
    /// unit as a fixed injection, solved by the default Newton power flow. Null when it does not converge.</summary>
    public static double? PccReactiveMvar(GridModel model, int unitIndex, int pccBus, IReadOnlyList<int> plantBuses, double pMw, double qMvar, double vPccPu)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(plantBuses);
        Generator unit = model.Generators[unitIndex];
        var set = new HashSet<int>(plantBuses) { pccBus };
        if (!plantBuses.Contains(unit.Bus)) throw new ArgumentException($"the unit's bus {unit.Bus} is not one of plantBuses.", nameof(plantBuses));
        if (plantBuses.Contains(pccBus)) throw new ArgumentException("pccBus must not be one of plantBuses.", nameof(plantBuses));
        for (int g = 0; g < model.Generators.Count; g++)
            if (g != unitIndex && plantBuses.Contains(model.Generators[g].Bus))
                throw new ArgumentException($"generator {g} at plant bus {model.Generators[g].Bus} is not the unit under test; the sub-model holds the unit alone.", nameof(plantBuses));
        var buses = new List<Bus>();
        foreach (Bus b in model.Buses)
        {
            if (!set.Contains(b.Id)) continue;
            if (b.Id == pccBus) buses.Add(new Bus(b.Id, BusType.Slack, BaseKv: b.BaseKv, Vm: vPccPu, Name: b.Name));
            else if (b.Id == unit.Bus) buses.Add(b with { Type = BusType.Pq, Pd = b.Pd - pMw, Qd = b.Qd - qMvar, Zip = null });
            else buses.Add(b with { Type = BusType.Pq, Zip = null });
        }
        var branches = model.Branches.Where(br => br.InService && set.Contains(br.From) && set.Contains(br.To)).ToList();
        var sub = new GridModel
        {
            BaseMva = model.BaseMva, Buses = buses, Branches = branches, FrequencyHz = model.FrequencyHz, Name = model.Name + " (q-capability)",
            Generators = new[] { new Generator(pccBus, 0.0, Vg: vPccPu, MBase: model.BaseMva, Name: "pcc") },
        };
        PowerFlowResult r = SolverSelector.CreateNewton(sub).Solve(sub, new PowerFlowOptions());
        if (!r.Converged) return null;
        int i = r.BusIds.ToList().IndexOf(pccBus);
        return -r.QInjMvar[i];
    }

    /// <summary>
    /// <c>q-capability</c>: at each test voltage, the unit at (Pmax, Qmax) and at (Pmax, Qmin); margins
    /// Q_pcc(Qmax) - Q_req,lag and Q_req,lead - Q_pcc(Qmin) (Mvar). <paramref name="tests"/> gives each test voltage with its
    /// required lagging (positive) and leading (negative) reactive power at the PCC.
    /// </summary>
    public static IReadOnlyList<GridCodeCheckResult> QCapability(GridModel model, int unitIndex, int pccBus, IReadOnlyList<int> plantBuses,
        double pmaxMw, double qmaxMvar, double qminMvar, IReadOnlyList<(double VoltagePu, double LagMvar, double LeadMvar)> tests, string clause)
    {
        ArgumentNullException.ThrowIfNull(tests);
        const string id = "q-capability";
        var list = new List<GridCodeCheckResult>();
        foreach ((double vp, double lag, double lead) in tests)
        {
            double? qLag = PccReactiveMvar(model, unitIndex, pccBus, plantBuses, pmaxMw, qmaxMvar, vp);
            double? qLead = PccReactiveMvar(model, unitIndex, pccBus, plantBuses, pmaxMw, qminMvar, vp);
            string at = string.Create(CultureInfo.InvariantCulture, $"V = {R(vp)} pu");
            list.Add(qLag is double a
                ? new GridCodeCheckResult(id, clause, PassIf(a - lag >= 0.0), a, lag, a - lag, "Mvar", "lagging: the unit at (Pmax, Qmax)") { Case = "lagging, " + at }
                : NotEvaluated(id, clause, "the sub-model power flow did not converge at (Pmax, Qmax)", "Mvar") with { Case = "lagging, " + at });
            list.Add(qLead is double b
                ? new GridCodeCheckResult(id, clause, PassIf(lead - b >= 0.0), b, lead, lead - b, "Mvar", "leading: the unit at (Pmax, Qmin)") { Case = "leading, " + at }
                : NotEvaluated(id, clause, "the sub-model power flow did not converge at (Pmax, Qmin)", "Mvar") with { Case = "leading, " + at });
        }
        return list;
    }

    // ------------------------------------------------------------------------------------------------ frequency response

    /// <summary>The required change of active power for a frequency f beyond the threshold f1:
    /// dP_req = -(f - f1) / (fn * s) * Pref (0 when f is not beyond f1, on the side <paramref name="over"/> gives).</summary>
    public static double RequiredChange(double f, double f1, double fn, double droop, double pref, bool over)
    {
        bool beyond = over ? f > f1 : f < f1;
        return beyond ? -(f - f1) / (fn * droop) * pref : 0.0;
    }

    /// <summary>
    /// <c>lfsm-o</c> / <c>lfsm-u</c> by frequency injection: a step to <paramref name="fStepHz"/> at t = 0; margin =
    /// |dP_actual(T_end)| - |dP_req| (pu, machine base), with dP_actual taken in the required direction (a change the wrong
    /// way counts negative).
    /// </summary>
    public static GridCodeCheckResult Lfsm(bool over, GovernorData governor, double pm0, double fStepHz, double fn, double thresholdHz, double droop,
        double prefPu, double dt, double tEnd, string clause, IReadOnlyList<string>? notes = null)
    {
        string id = over ? "lfsm-o" : "lfsm-u";
        FrequencyInjectionTrace tr = FrequencyInjection.Run(governor, pm0, _ => fStepHz, fn, dt, tEnd);
        double dp = tr.DeltaPu(tr.TimeSeconds.Length - 1);
        double req = RequiredChange(fStepHz, thresholdHz, fn, droop, prefPu, over);
        double sign = over ? -1.0 : 1.0;
        double margin = sign * dp - Math.Abs(req);
        return new GridCodeCheckResult(id, clause, PassIf(margin >= 0.0), dp, req, margin, "pu (machine base)",
            string.Create(CultureInfo.InvariantCulture, $"frequency injection: step to {R(fStepHz)} Hz, threshold {R(thresholdHz)} Hz, droop {R(droop)}, Pref {R(prefPu)} pu, read at {R(tEnd)} s"))
        { AtSeconds = tEnd, Notes = notes ?? Array.Empty<string>() };
    }

    /// <summary>The FSM required trajectory: 0 up to t1, a line from (t1, 0) to (t2, dP1), then dP1 (read from Figure 6).</summary>
    public static double FsmTrajectory(double t, double t1, double t2, double dp1) =>
        t <= t1 ? 0.0 : t <= t2 ? 0.0 + (dp1 - 0.0) * (t - t1) / (t2 - t1) : dp1;

    /// <summary>
    /// <c>fsm-step</c> by frequency injection: a step to <paramref name="fStepHz"/> at t = 0; margin = min over samples with
    /// t in [t1, T_end] of dP(t) - line(t) (dP taken in the required direction, pu machine base), and dP(t2) must reach dP1.
    /// t2 must be a sample time inside the run.
    /// </summary>
    public static GridCodeCheckResult FsmStep(GovernorData governor, double pm0, double fStepHz, double fn, double t1, double t2, double dp1Pu,
        double dt, double tEnd, string clause)
    {
        const string id = "fsm-step";
        if (!(t1 >= 0.0 && t2 > t1 && t2 <= tEnd)) throw new ArgumentException("fsm-step needs 0 <= t1 < t2 <= tEnd.");
        double k2d = Math.Round(t2 / dt);
        if (Math.Abs(k2d * dt - t2) > 1e-9 * Math.Max(1.0, t2)) throw new ArgumentException("fsm-step: t2 must be a whole number of steps.");
        FrequencyInjectionTrace tr = FrequencyInjection.Run(governor, pm0, _ => fStepHz, fn, dt, tEnd);
        double sign = fStepHz < fn ? 1.0 : -1.0;
        double m = double.PositiveInfinity, at = double.NaN;
        for (int k = 0; k < tr.TimeSeconds.Length; k++)
        {
            double t = tr.TimeSeconds[k];
            if (t < t1 - 1e-9 * dt) continue;
            double d = sign * tr.DeltaPu(k) - FsmTrajectory(t, t1, t2, dp1Pu);
            if (d < m) { m = d; at = t; }
        }
        double atT2 = sign * tr.DeltaPu((int)k2d);
        bool ok = m >= 0.0 && atT2 >= dp1Pu;
        return new GridCodeCheckResult(id, clause, PassIf(ok), atT2, dp1Pu, m, "pu (machine base)",
            string.Create(CultureInfo.InvariantCulture, $"frequency injection: step to {R(fStepHz)} Hz; t1 = {R(t1)} s, t2 = {R(t2)} s, dP1 = {R(dp1Pu)} pu; dP(t2) = {R(atT2)} pu"))
        { AtSeconds = at };
    }

    /// <summary>
    /// The network variant of <c>lfsm-o</c> / <c>lfsm-u</c>: an RMS event (a generator trip or a load step) gives the PCC
    /// frequency (<see cref="BusFrequencyMeter"/>); a converter unit's P comes from its trace (pu, system base) and is read
    /// at the end of the run against dP_req at the PCC frequency there. A synchronous unit is NOT EVALUATED (its electrical
    /// power is not in the trace; use frequency injection).
    /// </summary>
    public static GridCodeCheckResult LfsmNetwork(bool over, GridModel model, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data, int unitIndex,
        int pccBus, IReadOnlyList<RmsEvent> events, double thresholdHz, double droop, double? prefSystemPu, double stepSeconds, double durationSeconds, string clause)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(data);
        string id = over ? "lfsm-o" : "lfsm-u";
        Generator gen = model.Generators[unitIndex];
        if (!SubtransientInit.IsConverterUnit(gen, data[unitIndex]))
            return NotEvaluated(id, clause, "network variant: a synchronous unit's electrical power is not in the RMS traces (use frequency injection)", "pu (system base)");
        RmsTransientStability.ObservedStudy obs = RmsTransientStability.StudyEventsObserved(model, pf, data, events, new[] { pccBus },
            stepSeconds: stepSeconds, durationSeconds: durationSeconds);
        RmsConverterTrace? trace = obs.Result.Converters?.FirstOrDefault(c => c.Bus == gen.Bus);
        if (trace is null || trace.ActivePowerPu.Count == 0) return NotEvaluated(id, clause, "no converter trace at the unit's bus", "pu (system base)");
        var busTrace = new RmsBusTrace(obs.Buses.BusIds, obs.Buses.TimeSeconds, obs.Buses.AngleRad.ToArray());
        BusFrequencyResult meter = BusFrequencyMeter.Measure(busTrace, model.FrequencyHz, stepSeconds, events.Select(e => e.AtSeconds).ToArray());
        double f = meter.FrequencyHz[0][^1];
        double p0 = trace.ActivePowerPu[0];
        double dp = trace.ActivePowerPu[^1] - p0;
        double req = RequiredChange(f, thresholdHz, model.FrequencyHz, droop, prefSystemPu ?? p0, over);
        double margin = (over ? -1.0 : 1.0) * dp - Math.Abs(req);
        return new GridCodeCheckResult(id, clause, PassIf(margin >= 0.0), dp, req, margin, "pu (system base)",
            string.Create(CultureInfo.InvariantCulture, $"network variant: PCC frequency {R(f)} Hz at {R(obs.Buses.TimeSeconds[^1])} s"))
        { AtSeconds = obs.Buses.TimeSeconds[^1] };
    }

    /// <summary>
    /// <c>rocof-event</c>: an RMS event; the worst RoCoF at the PCC over the code's window (<see cref="BusFrequencyMeter"/>)
    /// and the unit's own 81R stages in the run (<see cref="RmsTransientStability.StudyEventsWithUnitRelays"/>). Worst RoCoF at
    /// or below the limit and an 81R trip: FAIL; at or below and no trip: PASS; above the limit: NOT DEMONSTRATED. Not
    /// evaluated when the case carries no 81R stage for the unit, or when the RMS run does not evaluate it (a converter
    /// unit's own relays are not integrated).
    /// </summary>
    public static GridCodeCheckResult RocofEvent(GridModel model, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data, int unitIndex, int pccBus,
        IReadOnlyList<RmsEvent> events, double limitHzPerSecond, double windowSeconds, double stepSeconds, double durationSeconds, string clause)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(events);
        const string id = "rocof-event";
        string? name = model.Generators[unitIndex].Name;
        ResolvedUnitProtection? unit = name is null ? null : model.UnitProtection?.For(name);
        var rocofDevices = new HashSet<string>(unit?.Stages.Where(s => s.Function == UnitRelayFunction.Rocof).Select(s => s.Device) ?? Array.Empty<string>(), StringComparer.Ordinal);
        if (rocofDevices.Count == 0) return NotEvaluated(id, clause, "the case carries no RoCoF (81R) stage for the unit", "Hz/s");
        RmsTransientStability.ObservedStudy obs = RmsTransientStability.StudyEventsObserved(model, pf, data, events, new[] { pccBus },
            stepSeconds: stepSeconds, durationSeconds: durationSeconds);
        var busTrace = new RmsBusTrace(obs.Buses.BusIds, obs.Buses.TimeSeconds, obs.Buses.AngleRad.ToArray());
        double worst = BusFrequencyMeter.Measure(busTrace, model.FrequencyHz, stepSeconds, events.Select(e => e.AtSeconds).ToArray(), windowSeconds).WorstRocofHzPerS[0];
        RmsTransientStability.UnitRelayStudy relays = RmsTransientStability.StudyEventsWithUnitRelays(model, pf, data, events, stepSeconds: stepSeconds, durationSeconds: durationSeconds);
        FullModelUnitRelayRecord? trip = relays.UnitRelays.FirstOrDefault(r => r.Unit == name && r.Tripped && rocofDevices.Contains(r.Device));
        string seen = string.Create(CultureInfo.InvariantCulture, $"worst RoCoF at the PCC {R(worst)} Hz/s over {R(windowSeconds)} s");
        if (worst > limitHzPerSecond)
            return new GridCodeCheckResult(id, clause, RuleOutcome.NotEvaluated, worst, limitHzPerSecond, null, "Hz/s",
                seen + ": above the withstand limit, so a trip is allowed and compliance is not shown") { Status = "NOT DEMONSTRATED" };
        var skipped = relays.NotEvaluated.FirstOrDefault(n => n.Unit == name && rocofDevices.Contains(n.Device));
        if (trip is null && skipped is not null)
            return new GridCodeCheckResult(id, clause, RuleOutcome.NotEvaluated, worst, limitHzPerSecond, null, "Hz/s",
                seen + $"; the RMS run does not evaluate the unit's {skipped.Device} stage ({skipped.Reason}), so riding through is not shown");
        return new GridCodeCheckResult(id, clause, PassIf(trip is null), worst, limitHzPerSecond, null, "Hz/s",
            trip is null ? seen + "; no 81R stage of the unit tripped" : seen + $"; the unit's {trip.Device} stage tripped it");
    }
}
