using GridSim.Core.Estimation;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Physics;

/// <summary>
/// The OPERATIONAL feasibility envelope, distinct from the physical LAWS in <see cref="PhysicsLaws"/>.
/// Where the laws answer "is this a valid network state at all?", these answer "does the state stay
/// inside the equipment's capability?" - a generator cannot exceed its reactive capability, a branch
/// cannot exceed its thermal rating, a bus voltage is bounded. A state can satisfy every physical law
/// yet be operationally infeasible (a generator producing more VArs than it physically can), so the
/// constrained estimator's acceptance gate checks BOTH: hard laws plus these capability limits.
///
/// These are the same inequalities the Ring B active-set backend enforces at solve time; checking them
/// here in the gate is the layered design's backstop and the definition the active-set is built to hold.
/// </summary>
public static class PhysicsFeasibility
{
    // ---- Named tolerances (October 2026 review, WS13 R188). Values unchanged.

    /// <summary>Slack (pu) on the voltage band: a bus is outside the band only when it is beyond the edge by more
    /// than this, so a value sitting on the edge to rounding is not a violation.</summary>
    public const double VmBandTolPu = 1e-9;

    /// <summary>Slack (MVAr) on a generator bus's reactive capability. This IS the power-flow Q-limit slack
    /// (<see cref="NewtonShell.QLimitTolMva"/>), referenced rather than copied, so the audit and the solver can
    /// never disagree on whether a bus is at its limit.</summary>
    public const double GenQTolMvar = NewtonShell.QLimitTolMva;

    /// <summary>Slack (percentage points) on a branch's loading before it is reported as over its rating.</summary>
    public const double RatingTolPercent = 1e-6;

    /// <summary>One equipment-limit breach: what/where, the value, and the bound it crossed.</summary>
    public sealed record LimitViolation(string Kind, int A, int B, double Value, double Limit, string Detail);

    /// <summary>
    /// Check <paramref name="result"/> against the equipment limits enabled in <paramref name="feas"/>:
    /// generator reactive capability [Qmin,Qmax], branch thermal rating (RateA), and voltage band. Only
    /// finite/positive limits are checked (an unbounded Qmax or a zero rating is not a constraint).
    /// </summary>
    public static IReadOnlyList<LimitViolation> Check(GridModel model, PowerFlowResult result, FeasibilityConstraints feas)
    {
        var violations = new List<LimitViolation>();

        var idx = new Dictionary<int, int>(result.BusIds.Count);
        for (int i = 0; i < result.BusIds.Count; i++) idx[result.BusIds[i]] = i;

        // Voltage band (per-bus override else the global band).
        for (int i = 0; i < result.BusIds.Count; i++)
        {
            (double lo, double hi) = feas.VmBand(result.BusIds[i]);
            double vm = result.Vm[i];
            // A non-finite voltage is outside every band (both comparisons below are false for NaN).
            if (!double.IsFinite(vm))
                violations.Add(new("VmBand", result.BusIds[i], -1, vm, lo, $"Vm {vm} is not a finite number"));
            else if (vm < lo - VmBandTolPu)
                violations.Add(new("VmBand", result.BusIds[i], -1, vm, lo, $"Vm {vm:F4} below {lo:F3} pu"));
            else if (vm > hi + VmBandTolPu)
                violations.Add(new("VmBand", result.BusIds[i], -1, vm, hi, $"Vm {vm:F4} above {hi:F3} pu"));
        }

        // Generator reactive capability, aggregated per bus (co-located machines share the bus injection).
        // The slack bus is EXEMPT: it absorbs whatever reactive the network needs and its Qg is set by
        // the solve, not bounded - flagging it would be a spurious "violation" (the same reason
        // NewtonShell.DiagnoseSlackQ is diagnostic-only).
        if (feas.EnforceGeneratorQ)
        {
            var slack = new HashSet<int>();
            foreach (Bus b in model.Buses) if (b.Type == BusType.Slack) slack.Add(b.Id);
            var qMin = new Dictionary<int, double>();
            var qMax = new Dictionary<int, double>();
            // The bus record per id, so the reactive LOAD can be taken at the solved voltage below. Last entry
            // wins on a duplicated id, exactly as the nominal-Qd dictionary this replaces did.
            var busById = new Dictionary<int, Bus>();
            foreach (Bus b in model.Buses) busById[b.Id] = b;
            foreach (Generator g in model.Generators)
            {
                // GR-495: -Infinity Qmin and +Infinity Qmax mean "no limit". A NaN limit, a +Infinity Qmin or a
                // -Infinity Qmax is corrupt data; it used to be read as "no limit" (the infinity's sign was dropped),
                // so the unit was never checked. It is refused by name instead.
                if (double.IsNaN(g.Qmin) || double.IsPositiveInfinity(g.Qmin) || double.IsNaN(g.Qmax) || double.IsNegativeInfinity(g.Qmax))
                    throw new ArgumentException(
                        $"PhysicsFeasibility: the generator at bus {g.Bus} has reactive limits [{g.Qmin}, {g.Qmax}] MVAr; a limit must be a number, Qmin may be -Infinity and Qmax +Infinity.",
                        nameof(model));
                qMin[g.Bus] = qMin.GetValueOrDefault(g.Bus, 0.0) + g.Qmin;
                qMax[g.Bus] = qMax.GetValueOrDefault(g.Bus, 0.0) + g.Qmax;
            }
            foreach ((int busId, double lo) in qMin)
            {
                if (slack.Contains(busId) || !idx.TryGetValue(busId, out int i)) continue;
                double hi = qMax[busId];
                // Qg = Qinj + Qload, with Qload what the bus ACTUALLY draws at its solved voltage (N014 / R065).
                // LoadAtVoltage is the one rule shared with the Newton mismatch, the reactive-limit check and the
                // physics audit: a bus with no voltage-dependent ZIP gets its nominal Qd back untouched (so every
                // constant-power case is bit-for-bit as before), a ZIP bus gets Qd * QMultiplier(Vm). Before this,
                // the nominal Qd was used for every bus, so a ZIP generator bus had its Qg mis-measured by
                // Qd * (QMultiplier(Vm) - 1) and could be flagged against (or let off) its reactive capability wrongly.
                // A generator on a bus missing from the bus list keeps the old zero load.
                double qLoad = busById.TryGetValue(busId, out Bus? genBus)
                    ? LoadAtVoltage.Mw(genBus, result.Vm[i]).QMvar
                    : 0.0;
                double qg = result.QInjMvar[i] + qLoad;
                double tol = GenQTolMvar;
                if (!double.IsInfinity(lo) && qg < lo - tol)
                    violations.Add(new("GenQ", busId, -1, qg, lo, $"Qg {qg:F2} below Qmin {lo:F2} MVAr"));
                else if (!double.IsInfinity(hi) && qg > hi + tol)
                    violations.Add(new("GenQ", busId, -1, qg, hi, $"Qg {qg:F2} above Qmax {hi:F2} MVAr"));
            }
        }

        // Branch thermal rating: LineFlow already carries the rating and computed loading percentage. An
        // overload is ALWAYS reported (the "report but don't demote" contract - a real grid runs over
        // rating); whether the solve is actively held onto the rating is the separate EnforceBranchRatings
        // knob consumed by the active-set backend, not a reporting gate.
        foreach (LineFlow lf in result.LineFlows)
            if (lf.RatingMva > 0 && lf.LoadingPercent > 100.0 + RatingTolPercent)
            {
                double s = lf.LoadingPercent / 100.0 * lf.RatingMva;
                violations.Add(new("Rating", lf.From, lf.To, s, lf.RatingMva,
                    $"branch {lf.From}->{lf.To} at {lf.LoadingPercent:F1}% of {lf.RatingMva:F0} MVA"));
            }

        return violations;
    }
}
