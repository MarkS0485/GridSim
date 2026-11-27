using System.Globalization;
using GridSim.Core.Analysis.Opf;

namespace GridSim.Core.Analysis.DistributionOptimisation;

/// <summary>
/// The result rows of the <c>dist-opt</c> verb, in the long table of the optimisation verbs (record, element, field,
/// value; <see cref="OptimisationReport"/> writes them as CSV or JSON). Rows are in a fixed order.
/// </summary>
public static class DistOptReport
{
    private static string Id(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>The rows of a capacitor placement.</summary>
    public static IReadOnlyList<OptimisationReport.Row> Rows(CapPlaceResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var rows = new List<OptimisationReport.Row>
        {
            new("summary", "", "mode", "capplace"),
            new("summary", "", "status", r.Status.ToString()),
            new("summary", "", "message", r.Message),
            new("summary", "", "base_loss_mw", r.BaseLossMw),
            new("summary", "", "base_objective", r.BaseObjective),
            new("summary", "", "loss_mw", r.LossMw),
            new("summary", "", "loss_cost_per_year", r.LossCostPerYear),
            new("summary", "", "capital_cost", r.CapitalCost),
            new("summary", "", "objective", r.Objective),
            new("summary", "", "vmin_pu", r.VminPu),
            new("summary", "", "vmax_pu", r.VmaxPu),
            new("summary", "", "violation_pu", r.ViolationPu),
            new("summary", "", "feasible", r.Feasible),
            new("summary", "", "power_flows", r.PowerFlows),
            new("summary", "", "rounds", r.Rounds),
            new("summary", "", "confirmation_solves", r.ConfirmationSolves),
            new("summary", "", "confirmation_improvements", r.ConfirmationImprovements),
            new("summary", "", "unconfirmed_banks", r.UnconfirmedBanks.Count),
        };
        foreach (CapPlaceBank b in r.Banks)
        {
            rows.Add(new("bank", Id(b.BusId), "step", b.Step));
            rows.Add(new("bank", Id(b.BusId), "mvar", b.SizeMvar));
        }
        foreach (int bus in r.UnconfirmedBanks) rows.Add(new("unconfirmed", Id(bus), "bus", bus));
        foreach (CapPlaceStep s in r.Steps)
        {
            string e = Id(s.Round);
            rows.Add(new("greedy", e, "bus", s.BusId));
            rows.Add(new("greedy", e, "mvar", s.SizeMvar));
            rows.Add(new("greedy", e, "objective", s.Objective));
            rows.Add(new("greedy", e, "loss_mw", s.LossMw));
        }
        foreach (DistOptBusVoltage v in r.Voltages) rows.Add(new("bus", Id(v.BusId), "vm_pu", v.VmPu));
        return rows;
    }

    /// <summary>The rows of a volt/VAr optimisation.</summary>
    public static IReadOnlyList<OptimisationReport.Row> Rows(VvoResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var rows = new List<OptimisationReport.Row>
        {
            new("summary", "", "mode", "vvo"),
            new("summary", "", "objective_kind", r.Objective.ToString().ToLowerInvariant()),
            new("summary", "", "status", r.Status.ToString()),
            new("summary", "", "message", r.Message),
            new("summary", "", "base_objective", r.BaseObjectiveValue),
            new("summary", "", "objective", r.ObjectiveValue),
            new("summary", "", "base_loss_mw", r.BaseLossMw),
            new("summary", "", "loss_mw", r.LossMw),
            new("summary", "", "base_demand_mw", r.BaseDemandMw),
            new("summary", "", "demand_mw", r.DemandMw),
            new("summary", "", "base_violation_pu", r.BaseViolationPu),
            new("summary", "", "violation_pu", r.ViolationPu),
            new("summary", "", "vmin_pu", r.VminPu),
            new("summary", "", "vmax_pu", r.VmaxPu),
            new("summary", "", "feasible", r.Feasible),
            new("summary", "", "sweeps", r.Sweeps),
            new("summary", "", "power_flows", r.PowerFlows),
            new("summary", "", "confirmation_solves", r.ConfirmationSolves),
            new("summary", "", "confirmation_improvements", r.ConfirmationImprovements),
        };
        foreach (VvoControlState c in r.Controls)
        {
            string e = $"{c.Kind} {c.Name}";
            rows.Add(new("control", e, "start_position", c.StartPosition));
            rows.Add(new("control", e, "start_value", c.StartValue));
            rows.Add(new("control", e, "position", c.Position));
            rows.Add(new("control", e, "value", c.Value));
        }
        for (int i = 0; i < r.Moves.Count; i++)
        {
            VvoMove mv = r.Moves[i];
            string e = Id(i + 1);
            rows.Add(new("move", e, "sweep", mv.Sweep));
            rows.Add(new("move", e, "control", $"{mv.Kind} {mv.Name}"));
            rows.Add(new("move", e, "position", mv.Position));
            rows.Add(new("move", e, "objective", mv.Objective));
            rows.Add(new("move", e, "violation_pu", mv.ViolationPu));
        }
        foreach (string n in r.Notes) rows.Add(new("note", "", "text", n));
        foreach (DistOptBusVoltage v in r.Voltages) rows.Add(new("bus", Id(v.BusId), "vm_pu", v.VmPu));
        return rows;
    }

    /// <summary>The rows of a phase balancing.</summary>
    public static IReadOnlyList<OptimisationReport.Row> Rows(PhaseBalanceResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var rows = new List<OptimisationReport.Row>
        {
            new("summary", "", "mode", "phase-balance"),
            new("summary", "", "method", r.Method),
            new("summary", "", "proxy_evaluations", r.ProxyEvaluations),
            new("summary", "", "proxy_before_w", r.ProxyBeforeW),
            new("summary", "", "proxy_after_w", r.ProxyAfterW),
            new("summary", "", "head_branch", r.HeadBranch),
            new("summary", "", "head_node", r.HeadNode),
            new("summary", "", "solve_before", r.StatusBefore.ToString()),
            new("summary", "", "solve_after", r.StatusAfter.ToString()),
            new("summary", "", "neutral_before_a", r.NeutralBeforeA),
            new("summary", "", "neutral_after_a", r.NeutralAfterA),
            new("summary", "", "vuf_before", r.VufBefore),
            new("summary", "", "vuf_after", r.VufAfter),
        };
        for (int p = 0; p < r.Phases.Count; p++)
        {
            rows.Add(new("phase", r.Phases[p], "power_before_w", r.PhasePowerBeforeW[p]));
            rows.Add(new("phase", r.Phases[p], "power_after_w", r.PhasePowerAfterW[p]));
        }
        foreach (PhaseLoadMove m in r.Moves)
        {
            rows.Add(new("load", m.Load, "from_phase", m.FromPhase));
            rows.Add(new("load", m.Load, "to_phase", m.ToPhase));
            rows.Add(new("load", m.Load, "power_w", m.PowerW));
        }
        foreach (string n in r.Notes) rows.Add(new("note", "", "text", n));
        return rows;
    }
}
