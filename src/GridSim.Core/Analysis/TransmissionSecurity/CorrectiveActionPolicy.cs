using GridSim.Core.Model;

namespace GridSim.Core.Analysis.TransmissionSecurity;

/// <summary>
/// The permitted action set of a corrective study. Actions are scheduled as simultaneous independent ramps that all
/// start when the first outage occurs and must complete within <paramref name="AvailableMinutes"/> (the time assumed
/// to elapse before the second outage). The proposal must be balanced: the MW changes sum to zero within
/// <paramref name="BalanceToleranceMw"/>; the slack unit then supplies only the change in AC losses, which the
/// intermediate solve reports. A partial subset of a proposal is never applied. Units at the slack bus cannot be
/// eligible: the slack balances losses and holds no scheduled setpoint.
/// </summary>
public sealed record CorrectiveActionPolicy(IReadOnlyList<GeneratorActionEnvelope> Eligible, double AvailableMinutes,
    double BalanceToleranceMw = 1e-6)
{
    /// <summary>The declared scheduling rule (not a claim of chronological optimality).</summary>
    public const string Scheduling = "Simultaneous independent ramps from the first outage over a common window; " +
        "each change is capped by the tighter of its MW cap and its ramp times its available minutes. " +
        "No partial subset is applied.";

    private const string Down = "down", Up = "up";
    private static string I(FormattableString text) => FormattableString.Invariant(text);

    /// <summary>Refuses a policy that cannot be applied to <paramref name="model"/> (input error, before any solve).</summary>
    public void Check(GridModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (Eligible is null || Eligible.Any(e => e is null))
            throw new ArgumentException("The permitted action set must be a list of unit envelopes.");
        if (!double.IsFinite(AvailableMinutes) || AvailableMinutes < 0 ||
            !double.IsFinite(BalanceToleranceMw) || BalanceToleranceMw < 0)
            throw new ArgumentException("Available minutes and balance tolerance must be finite and non-negative.");
        var seen = new HashSet<int>();
        foreach (GeneratorActionEnvelope e in Eligible)
        {
            if (e.Generator < 0 || e.Generator >= model.Generators.Count || !seen.Add(e.Generator))
                throw new ArgumentException(I($"Action envelope unit {e.Generator} is unknown or repeated."));
            Generator unit = model.Generators[e.Generator];
            if (unit.Bus != e.Bus)
                throw new ArgumentException(I($"Action envelope unit {e.Generator} is at bus {unit.Bus}, not {e.Bus}."));
            if (model.Buses.Any(b => b.Id == unit.Bus && b.Type == BusType.Slack))
                throw new ArgumentException(I($"Action envelope unit {e.Generator} is at the slack bus, which balances losses and holds no scheduled setpoint."));
            if (!Enum.IsDefined(e.Direction) || !double.IsFinite(e.MinFinalMw) || !double.IsFinite(e.MaxFinalMw) ||
                e.MinFinalMw > e.MaxFinalMw || !double.IsFinite(e.MaxDeltaMw) || e.MaxDeltaMw < 0 ||
                !double.IsFinite(e.RampMwPerMin) || e.RampMwPerMin < 0 || !double.IsFinite(e.WindowMinutes) || e.WindowMinutes < 0)
                throw new ArgumentException(I($"Action envelope unit {e.Generator} needs a direction, ordered finite final bounds and finite non-negative cap, ramp and window."));
        }
    }

    /// <summary>
    /// Validates a whole proposal against the permitted set. Every action gets one row with its exact margins; the
    /// proposal is admissible only when every row is accepted and the MW changes balance. Start setpoints are the
    /// scheduled Pg of the units in <paramref name="model"/>; the realised starting output observed by the first AC
    /// solve is recorded alongside for audit.
    /// </summary>
    public (IReadOnlyList<ActionCheck> Checks, string? Refusal) Validate(GridModel model,
        IReadOnlyList<GeneratorAction> proposal, SecurityState firstState)
    {
        var rows = new List<ActionCheck>(proposal.Count);
        var seen = new HashSet<int>();
        foreach (GeneratorAction action in proposal)
        {
            var reasons = new List<string>();
            bool known = action.Generator >= 0 && action.Generator < model.Generators.Count;
            if (!known || model.Generators[action.Generator].Bus != action.Bus)
            {
                rows.Add(new(action.Generator, action.Bus, action.DeltaMw, null, null, null, null, null, null, null, null,
                    null, null, null, null, null, null, false, ["Unknown unit, or the unit is not at the stated bus."]));
                continue;
            }
            if (!seen.Add(action.Generator)) reasons.Add("The unit appears more than once in the proposal.");
            if (!double.IsFinite(action.DeltaMw)) reasons.Add("The MW change is not finite.");
            Generator unit = model.Generators[action.Generator];
            double start = unit.Pg;
            double final = start + action.DeltaMw;
            double? realised = firstState.Limits.FirstOrDefault(l => l.Kind == "GeneratorP" && l.Index == action.Generator)?.Observed;
            GeneratorActionEnvelope? envelope = Eligible.FirstOrDefault(e => e.Generator == action.Generator);
            if (envelope is null)
            {
                reasons.Add(model.Buses.Any(b => b.Id == unit.Bus && b.Type == BusType.Slack)
                    ? "Ineligible: the unit is at the slack bus and is not in the permitted action set."
                    : "Ineligible: the unit is not in the permitted action set.");
                rows.Add(new(action.Generator, action.Bus, action.DeltaMw, start, realised, final, null, null, null,
                    unit.Pmin, unit.Pmax, null, null, null, null, null, null, false, reasons));
                continue;
            }
            if ((envelope.Direction == ActionDirection.Up && action.DeltaMw < 0) ||
                (envelope.Direction == ActionDirection.Down && action.DeltaMw > 0))
                reasons.Add(I($"Direction {(action.DeltaMw < 0 ? Down : Up)} is not permitted (allowed: {envelope.Direction})."));
            if (final < envelope.MinFinalMw) reasons.Add(I($"Final setpoint {final} MW is below the permitted minimum {envelope.MinFinalMw} MW by {envelope.MinFinalMw - final} MW."));
            if (final > envelope.MaxFinalMw) reasons.Add(I($"Final setpoint {final} MW is above the permitted maximum {envelope.MaxFinalMw} MW by {final - envelope.MaxFinalMw} MW."));
            if (final < unit.Pmin) reasons.Add(I($"Final setpoint {final} MW is below the unit's Pmin {unit.Pmin} MW by {unit.Pmin - final} MW."));
            if (final > unit.Pmax) reasons.Add(I($"Final setpoint {final} MW is above the unit's Pmax {unit.Pmax} MW by {final - unit.Pmax} MW."));
            double minutes = Math.Min(envelope.WindowMinutes, AvailableMinutes);
            double rampCap = envelope.RampMwPerMin * minutes;
            double cap = Math.Min(envelope.MaxDeltaMw, rampCap);
            double magnitude = Math.Abs(action.DeltaMw);
            double? required = envelope.RampMwPerMin > 0 ? magnitude / envelope.RampMwPerMin : null;
            double? shortfall = magnitude > cap ? magnitude - cap : null;
            if (shortfall is double s)
                reasons.Add(magnitude > envelope.MaxDeltaMw && envelope.MaxDeltaMw <= rampCap
                    ? I($"Change of {magnitude} MW exceeds the unit's MW cap {envelope.MaxDeltaMw} MW by {s} MW.")
                    : I($"Change of {magnitude} MW exceeds the ramp capacity {rampCap} MW ({envelope.RampMwPerMin} MW/min over {minutes} min) by {s} MW."));
            rows.Add(new(action.Generator, action.Bus, action.DeltaMw, start, realised, final, envelope.Direction,
                envelope.MinFinalMw, envelope.MaxFinalMw, unit.Pmin, unit.Pmax, envelope.MaxDeltaMw, envelope.RampMwPerMin,
                minutes, cap, required, shortfall, reasons.Count == 0, reasons));
        }
        double net = 0;
        foreach (GeneratorAction action in proposal) net += action.DeltaMw;
        string? refusal = rows.Any(r => !r.Accepted)
            ? I($"{rows.Count(r => !r.Accepted)} of {rows.Count} proposed action(s) rejected; no partial subset is applied.")
            : !double.IsFinite(net) || Math.Abs(net) > BalanceToleranceMw
                ? I($"Unbalanced proposal: the MW changes sum to {net} MW (tolerance {BalanceToleranceMw} MW).")
                : null;
        return (rows, refusal);
    }
}

/// <summary>
/// One proposed action checked against the permitted set. MW quantities are scheduled setpoints;
/// <paramref name="RealisedStartMw"/> is the unit's output observed by the first AC solve (null where it cannot be
/// observed). <paramref name="AvailableMinutes"/> is the smaller of the unit window and the study window,
/// <paramref name="CapMw"/> the tighter of the MW cap and ramp times those minutes, <paramref name="RequiredMinutes"/>
/// the time the change needs at the unit's ramp, and <paramref name="ShortfallMw"/> the MW by which the change exceeds
/// its cap (null when within it). Unknown or ineligible units carry null envelope fields.
/// </summary>
public sealed record ActionCheck(int Generator, int Bus, double DeltaMw, double? StartMw, double? RealisedStartMw,
    double? FinalMw, ActionDirection? AllowedDirection, double? MinFinalMw, double? MaxFinalMw, double? UnitPminMw,
    double? UnitPmaxMw, double? MaxDeltaMw, double? RampMwPerMin, double? AvailableMinutes, double? CapMw,
    double? RequiredMinutes, double? ShortfallMw, bool Accepted, IReadOnlyList<string> Reasons);
