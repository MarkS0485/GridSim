using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Harmonics.Planning;
using GridSim.Core.Model;
using GridSim.Core.Protection;

namespace GridSim.Core.Compliance.GridCode;

/// <summary>
/// <c>ffci-envelope</c>: GB fast fault current injection (ECC.6.3.16.1.1 to .5) read from an RMS fault run. Units are pu
/// on the unit's own rating (ECC.6.3.16.1.7: 1.0 pu current is the MVA rating); reactive current is positive when the unit
/// exports reactive power (capacitive), the sign of the converter traces.
/// <list type="bullet">
/// <item>Fault inception is the first sample with the PCC voltage below 0.9 pu (ECC.6.3.16.1.4); the pre-fault current
/// I0 is the unit's reactive current at the sample before it.</item>
/// <item>The target current IR(V) at retained voltage V is read from Figure ECC.6.3.16(a) (figure-read, marked NOT TO
/// SCALE): a straight line from (V1, Is) to the labelled point (V2, I2) = (0.5, 1.0), with Is = max(I0, I1) and
/// (V1, I1) = (0.9, -0.312) the figure's other labelled point, never below I0 and never above 1.0 pu (ECC.6.3.16.1.5):
/// <c>IR(V) = min(Imax, max(I0, Is + (I2 - Is)(V1 - V)/(V1 - V2)))</c>, with V the PCC voltage at that sample. For a
/// pre-fault current above I1 this starts the line at the pre-fault current, GridSim's reading of ECC.6.3.16.1.3 (IR
/// "shall as a minimum increase with the fall in the retained voltage" and depends on the pre-fault operating condition),
/// so any fall below 0.9 pu asks for an increase. The result notes say so.</item>
/// <item>The edge of the forbidden area is <c>I0 + f(tau)(IR(V) - I0)</c>, f the envelope of Figures ECC.6.3.16(b)/(c)
/// (0 to 20 ms, 0.65 at 60 ms, 1 at 120 ms, linear between), tau the time from inception. The forbidden area starts where
/// f first rises above 0 (20 ms), so only samples after that are judged: before it the edge is I0 itself, which a unit in
/// steady state meets with a margin of exactly 0, so judging there would say nothing.</item>
/// <item>Samples at or after fault clearance are not judged (blocking is permitted there, ECC.6.3.16.1.8), nor samples
/// more than 140 ms after inception (the figures stop there).</item>
/// </list>
/// PASS when the reactive current is on or above the edge at every judged sample; FAIL otherwise, with the worst margin
/// and its time from inception. No tolerance is applied: a margin of exactly 0 is on the edge. Agreed deviations
/// (ECC.6.3.16.1.10) are not allowed for.
/// </summary>
public static class FfciCheck
{
    public const string Id = "ffci-envelope";
    public const string Unit = "pu (unit rating)";

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The target current of Figure ECC.6.3.16(a) at retained voltage <paramref name="voltagePu"/> for a pre-fault
    /// current <paramref name="preFaultPu"/> (pu of the rating).</summary>
    public static double TargetCurrent(FfciRequirement req, double voltagePu, double preFaultPu)
    {
        ArgumentNullException.ThrowIfNull(req);
        double start = Math.Max(preFaultPu, req.LineI1);
        double line = start + (req.LineI2 - start) * (req.LineV1 - voltagePu) / (req.LineV1 - req.LineV2);
        return Math.Min(req.MaxReactiveCurrent.Value, Math.Max(preFaultPu, line));
    }

    /// <summary>The envelope fraction f(tau) of Figures ECC.6.3.16(b)/(c): linear between the transcribed points, the first
    /// point's value before it and the last point's after it.</summary>
    public static double EnvelopeFraction(FfciRequirement req, double tauSeconds)
    {
        ArgumentNullException.ThrowIfNull(req);
        IReadOnlyList<(double Seconds, double Fraction)> p = req.Envelope;
        if (tauSeconds <= p[0].Seconds) return p[0].Fraction;
        for (int i = 0; i + 1 < p.Count; i++)
            if (tauSeconds <= p[i + 1].Seconds)
                return p[i].Fraction + (p[i + 1].Fraction - p[i].Fraction) * (tauSeconds - p[i].Seconds) / (p[i + 1].Seconds - p[i].Seconds);
        return p[^1].Fraction;
    }

    /// <summary>
    /// Judge a sampled trace: <paramref name="voltagePu"/> the PCC voltage and <paramref name="reactiveCurrentPu"/> the unit's
    /// reactive current (pu of its rating) at <paramref name="times"/>; the fault cleared at <paramref name="clearSeconds"/>
    /// (absolute time). See the type summary for the rule.
    /// </summary>
    public static GridCodeCheckResult Evaluate(FfciRequirement req, IReadOnlyList<double> times, IReadOnlyList<double> voltagePu,
        IReadOnlyList<double> reactiveCurrentPu, double clearSeconds, IReadOnlyList<string>? notes = null)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(voltagePu);
        ArgumentNullException.ThrowIfNull(reactiveCurrentPu);
        if (times.Count != voltagePu.Count || times.Count != reactiveCurrentPu.Count)
            throw new ArgumentException("times, voltages and currents must have the same length.");
        var n = new List<string>(notes ?? Array.Empty<string>());
        int k0 = -1;
        for (int k = 0; k < times.Count; k++) if (voltagePu[k] < req.InceptionVoltage.Value) { k0 = k; break; }
        if (k0 < 0)
            return NotEvaluated(req, $"the PCC voltage never fell below {R(req.InceptionVoltage.Value)} pu, so no fault inception is seen ({req.InceptionVoltage.Clause})", n);
        if (k0 == 0) return NotEvaluated(req, "the run holds no sample before fault inception, so the pre-fault reactive current is not known", n);
        double tInc = times[k0];
        double i0 = reactiveCurrentPu[k0 - 1];
        // The envelope asks for an increase only after its leading zero part (to 20 ms in the GB figures).
        double zeroUntil = req.Envelope[0].Seconds;
        foreach ((double sec, double frac) in req.Envelope) { if (frac > 0.0) break; zeroUntil = sec; }
        double m = double.PositiveInfinity, at = double.NaN, iqAt = double.NaN, reqAt = double.NaN;
        bool anyFault = false, anyRequired = false, cutAt140 = false;
        for (int k = k0; k < times.Count; k++)
        {
            if (times[k] >= clearSeconds) break;
            double tau = times[k] - tInc;
            if (tau > req.MaxClearance.Value) { cutAt140 = true; break; }
            anyFault = true;
            // Judged only where the forbidden area exists (after the envelope's leading zero part).
            if (!(tau > zeroUntil)) continue;
            anyRequired = true;
            double f = EnvelopeFraction(req, tau);
            double target = TargetCurrent(req, voltagePu[k], i0);
            double required = i0 + f * (target - i0);
            double d = reactiveCurrentPu[k] - required;
            if (d < m) { m = d; at = tau; iqAt = reactiveCurrentPu[k]; reqAt = required; }
        }
        if (!anyFault) return NotEvaluated(req, "the fault was cleared at inception, so no sample lies in the fault period", n);
        if (cutAt140)
            n.Add(string.Create(CultureInfo.InvariantCulture, $"the fault lasted beyond {R(req.MaxClearance.Value)} s from inception; the figures stop there ({req.MaxClearance.Clause}), so later samples are not judged"));
        n.Add(string.Create(CultureInfo.InvariantCulture, $"fault inception at {R(tInc)} s (PCC voltage first below {R(req.InceptionVoltage.Value)} pu); pre-fault reactive current {R(i0)} pu"));
        n.Add($"no allowance is made for deviations agreed under {req.AgreedDeviationsClause}");
        string lineNote = $"{req.LineFigure} is figure-read (NOT TO SCALE): the target runs in a straight line from ({R(req.LineV1)} pu, {R(Math.Max(i0, req.LineI1))} pu) to ({R(req.LineV2)} pu, {R(req.LineI2)} pu)";
        if (i0 > req.LineI1)
            lineNote += $"; the line starts at the pre-fault current rather than the labelled {R(req.LineI1)} pu, GridSim's reading of {req.LineClause} (the current must increase with the fall in retained voltage)";
        n.Add(lineNote);
        n.Add(string.Create(CultureInfo.InvariantCulture, $"judged from {R(zeroUntil)} s after inception, where the forbidden area of {req.EnvelopeFigure} starts"));
        if (!anyRequired)
            return new GridCodeCheckResult(Id, req.Clause, RuleOutcome.NotEvaluated, null, null, null, Unit,
                "the fault was cleared before the envelope asks for any increase in reactive current, so the injection is not shown")
            { Status = "NOT DEMONSTRATED", Notes = n };
        string reason = m >= 0.0
            ? $"the reactive current stays on or above the edge of the forbidden area ({req.EnvelopeFigure}) until clearance"
            : $"the reactive current goes below the edge of the forbidden area ({req.EnvelopeFigure})";
        return new GridCodeCheckResult(Id, req.Clause, m >= 0.0 ? RuleOutcome.Pass : RuleOutcome.Fail, iqAt, reqAt, m, Unit, reason)
        { AtSeconds = at, Notes = n };
    }

    private static GridCodeCheckResult NotEvaluated(FfciRequirement req, string reason, IReadOnlyList<string> notes) =>
        new(Id, req.Clause, RuleOutcome.NotEvaluated, null, null, null, Unit, reason) { Notes = notes };

    /// <summary>
    /// Run the check on the GB FRT run of a converter unit: a balanced fault through <see cref="GridCodeChecks.RunFrt"/>
    /// (the same events and bits as <c>frt-envelope</c>), the unit's reactive current from its converter trace scaled to its
    /// rating (<see cref="MachineBase.EffectiveMva"/>). NOT APPLICABLE for a synchronous unit or a grid-forming one
    /// (ECC.6.3.19 instead); NOT EVALUATED for an unbalanced fault, for a unit whose trace is shared with other units at its
    /// bus, and for a unit the RMS study does not run as converter plant.
    /// </summary>
    public static GridCodeCheckResult Run(FfciRequirement req, FrtRun run, GridModel model, IReadOnlyList<SubtransientInit.MachineData> data, int unitIndex,
        ModuleType moduleType, string technology)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(data);
        if (technology == GridCodeTechnology.PpmGridForming)
            return new GridCodeCheckResult(Id, req.GridFormingClause, RuleOutcome.NotApplicable, null, null, null, Unit, req.GridFormingText);
        if (!req.Technologies.Contains(technology, StringComparer.Ordinal))
            return new GridCodeCheckResult(Id, req.Clause, RuleOutcome.NotApplicable, null, null, null, Unit,
                "fast fault current injection applies to power park modules and HVDC equipment, not to a synchronous unit");
        if (!req.ModuleTypes.Contains(moduleType.ToString(), StringComparer.Ordinal))
            return new GridCodeCheckResult(Id, req.Clause, RuleOutcome.NotApplicable, null, null, null, Unit, $"{req.Clause} applies to type B, C and D modules only");
        if (run.Fault.Type != FaultType.ThreePhase)
            return new GridCodeCheckResult(Id, req.UnbalancedClause, RuleOutcome.NotEvaluated, null, null, null, Unit, req.UnbalancedText);
        Generator gen = model.Generators[unitIndex];
        if (!SubtransientInit.IsConverterUnit(gen, data[unitIndex]))
            return NotEvaluated(req, "the RMS study runs the unit as a synchronous machine, so it has no converter reactive-current trace", Array.Empty<string>());
        var traces = run.Observed.Result.Converters?.Where(c => c.Bus == gen.Bus).ToArray() ?? Array.Empty<RmsConverterTrace>();
        if (traces.Length == 0) return NotEvaluated(req, "no converter trace at the unit's bus", Array.Empty<string>());
        string own = gen.Name ?? $"converter@bus {gen.Bus}";
        if (traces.Length != 1 || traces[0].Name != own)
            return NotEvaluated(req, "the RMS study aggregates more than one converter at the unit's bus into one trace, so the unit's own current is not known", Array.Empty<string>());
        RmsConverterTrace tr = traces[0];
        IReadOnlyList<double> times = run.Observed.Buses.TimeSeconds;
        if (tr.ReactiveCurrentPu.Count != times.Count)
            throw new InvalidOperationException("internal error: the converter trace and the bus voltage trace have different sample counts.");
        double rating = MachineBase.EffectiveMva(gen, model.BaseMva) / model.BaseMva;
        var iq = tr.ReactiveCurrentPu.Select(x => x / rating).ToArray();
        var notes = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"{RmsSequenceFaults.TypeText(run.Fault.Type)} fault at bus {run.FaultBus} through {R(run.ResistancePu)} + j{R(run.ReactancePu)} pu at {R(run.Fault.AtSeconds)} s, cleared {R(run.Profile.Tclear)} s later; the injection is shown for this one fault"),
            string.Create(CultureInfo.InvariantCulture, $"the unit's reactive current on its rating {R(rating * model.BaseMva)} MVA, positive sequence; the voltage is read at the PCC (bus {run.Observed.Buses.BusIds[0]}) and the current at the unit's terminals (bus {gen.Bus})"),
        };
        return Evaluate(req, times, run.Observed.Buses.MagnitudePu[0], iq, run.Fault.AtSeconds + run.Profile.Tclear, notes);
    }
}
