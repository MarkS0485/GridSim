using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis;

/// <summary>One step of a restoration sequence: the branch just energised and the resulting state's
/// health - voltage-band violations and any generator that has run out of reactive capability.</summary>
public sealed record RestorationStepResult(
    int Step,
    string Energized,
    bool Solved,
    double MinVpu,
    double MaxVpu,
    IReadOnlyList<int> OverVoltageBuses,
    IReadOnlyList<int> UnderVoltageBuses,
    IReadOnlyList<int> GeneratorsAtReactiveLimit)
{
    /// <summary>A step is safe when it solves and leaves no voltage-band violation and no generator pinned
    /// against its reactive limit (which is exactly what makes an energisation step unsafe in practice).</summary>
    public bool Feasible => Solved
        && OverVoltageBuses.Count == 0 && UnderVoltageBuses.Count == 0
        && GeneratorsAtReactiveLimit.Count == 0;
}

/// <summary>The health of a whole restoration sequence, step by step.</summary>
public sealed record RestorationReport(IReadOnlyList<RestorationStepResult> Steps)
{
    /// <summary>True when every step is feasible - the sequence is safe to follow as ordered.</summary>
    public bool AllFeasible => Steps.All(s => s.Feasible);

    /// <summary>The index of the first unsafe step, or -1 if the whole sequence is safe.</summary>
    public int FirstInfeasibleStep
    {
        get { for (int i = 0; i < Steps.Count; i++) if (!Steps[i].Feasible) return i; return -1; }
    }
}

/// <summary>
/// Evaluates a network-restoration (black-start) sequence for reactive feasibility: it energises the listed
/// branches one at a time, re-solves the growing live network with generator reactive limits ENFORCED, and
/// at each step reports voltage-band violations and any generator that has hit its Qmin/Qmax. This captures
/// the constraint a bare cascade trip does not - as a generator picks up a lightly-loaded EHV line, the
/// line's charging drives a Ferranti over-voltage the machine must absorb, and once it hits its
/// reactive-absorption limit the over-voltage stands: the step is unsafe, and a different ordering (or a
/// shunt reactor) is needed. The power flow already switches PV to PQ at a generator's reactive limit; this
/// wraps it into a sequence checker so an operator can see WHERE a restoration order breaks.
/// </summary>
public static class RestorationSequencer
{
    /// <summary>Assess <paramref name="energizeOrder"/> (branch names) against <paramref name="model"/>.
    /// Sequenced branches start out of service and are energised one per step - whatever their stored
    /// <see cref="Branch.InService"/> flag, since a black-start model naturally carries its to-be-closed branches
    /// open; other branches keep their model service state. Voltages are checked against
    /// [<paramref name="vMin"/>, <paramref name="vMax"/>] on the live (slack-connected) buses. Every name in the
    /// order must match a branch, and no branch may be sequenced twice: both fail loud.</summary>
    public static RestorationReport Evaluate(
        GridModel model,
        IReadOnlyList<string> energizeOrder,
        double vMin = VoltageLimits.StatutoryLowPu,    // the shared statutory band (R136)
        double vMax = VoltageLimits.StatutoryHighPu,
        double qLimitTolMvar = 1.0,
        PowerFlowOptions? options = null)
    {
        var sequenced = new HashSet<string>(StringComparer.Ordinal);
        var branchNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (Branch b in model.Branches) if (b.Name is { } bn) branchNames.Add(bn);
        foreach (string name in energizeOrder)
        {
            // Fail loud on a sequence that cannot mean what it says: a misspelt name would silently energise
            // nothing at its step, and a repeat would report a step that changes nothing.
            if (!branchNames.Contains(name))
                throw new ArgumentException($"Restoration sequence names branch '{name}', which is not in model '{model.Name}'.", nameof(energizeOrder));
            if (!sequenced.Add(name))
                throw new ArgumentException($"Restoration sequence names branch '{name}' more than once.", nameof(energizeOrder));
        }
        var energized = new HashSet<string>(StringComparer.Ordinal);
        PowerFlowOptions opt = (options ?? new PowerFlowOptions()) with { EnforceQLimits = true };

        // Aggregate generator reactive limits per bus (skip the default unbounded machines).
        var qMin = new Dictionary<int, double>();
        var qMax = new Dictionary<int, double>();
        foreach (Generator g in model.Generators)
        {
            qMin[g.Bus] = qMin.GetValueOrDefault(g.Bus) + (double.IsInfinity(g.Qmin) ? double.NegativeInfinity : g.Qmin);
            qMax[g.Bus] = qMax.GetValueOrDefault(g.Bus) + (double.IsInfinity(g.Qmax) ? double.PositiveInfinity : g.Qmax);
        }
        var qdByBus = new Dictionary<int, double>();
        foreach (Bus b in model.Buses) qdByBus[b.Id] = b.Qd;
        int slackId = model.Buses.First(b => b.Type == BusType.Slack).Id;

        var steps = new List<RestorationStepResult>(energizeOrder.Count);
        for (int step = 0; step < energizeOrder.Count; step++)
        {
            energized.Add(energizeOrder[step]);

            // In-service branches this step (R228, October 2026): a SEQUENCED branch is closed exactly when its
            // step has been reached, regardless of its stored InService flag (the old filter applied b.InService
            // to sequenced branches too, so a branch stored open was never energised and every step solved the
            // same network); a non-sequenced branch keeps its model state. Then solve only the LIVE (slack-
            // connected) subnetwork - a de-energised bus has no admittance and would make the Jacobian singular.
            var inService = new List<Branch>(model.Branches.Count);
            foreach (Branch b in model.Branches)
            {
                bool isSequenced = b.Name is { } nm && sequenced.Contains(nm);
                if (isSequenced)
                {
                    if (energized.Contains(b.Name!)) inService.Add(b.InService ? b : b with { InService = true });
                }
                else if (b.InService) inService.Add(b);
            }
            HashSet<int> live = LiveBuses(inService, slackId, model.Buses.Count);
            List<Branch> liveBranches = inService.Where(b => live.Contains(b.From) && live.Contains(b.To)).ToList();

            // The live sub-model carries the OLTCs whose transformer and controlled bus are both live (R228): the
            // energised network is regulated exactly as SteadyStateSolver regulates any solve. They used to be
            // dropped, so restoration ran with every tap frozen. CopyWith carries asset dynamics, protections and
            // boundaries (R270).
            var liveBranchNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (Branch b in liveBranches) if (b.Name is { } ln) liveBranchNames.Add(ln);
            var liveModel = model.CopyWith(
                buses: model.Buses.Where(b => live.Contains(b.Id)).ToList(),
                generators: model.Generators.Where(g => live.Contains(g.Bus)).ToList(),
                branches: liveBranches,
                oltcs: model.Oltcs.Where(o => liveBranchNames.Contains(o.Transformer) && live.Contains(o.ControlsBus)).ToList());

            PowerFlowResult r = new SteadyStateSolver(SolverSelector.CreateNewton(liveModel)).Solve(liveModel, opt);   // R004

            var over = new List<int>();
            var under = new List<int>();
            var pinned = new List<int>();
            double minV = double.PositiveInfinity, maxV = double.NegativeInfinity;
            if (r.Converged)
            {
                for (int i = 0; i < r.BusIds.Count; i++)
                {
                    int id = r.BusIds[i];
                    double vm = r.Vm[i];
                    minV = Math.Min(minV, vm); maxV = Math.Max(maxV, vm);
                    if (vm > vMax) over.Add(id);
                    else if (vm < vMin) under.Add(id);

                    if (qMax.ContainsKey(id))
                    {
                        double qGen = r.QInjMvar[i] + qdByBus.GetValueOrDefault(id); // gen = injection + load
                        if (qGen >= qMax[id] - qLimitTolMvar && double.IsFinite(qMax[id])) pinned.Add(id);
                        else if (qGen <= qMin[id] + qLimitTolMvar && double.IsFinite(qMin[id])) pinned.Add(id);
                    }
                }
            }
            if (!double.IsFinite(minV)) { minV = 0; maxV = 0; }

            steps.Add(new RestorationStepResult(
                step, energizeOrder[step], r.Converged, minV, maxV, over, under, pinned));
        }

        return new RestorationReport(steps);
    }

    // Bus ids reachable from the slack over the given in-service branches (the live, solvable network).
    private static HashSet<int> LiveBuses(List<Branch> inService, int slackId, int busCount)
    {
        var adj = new Dictionary<int, List<int>>(busCount);
        List<int> Edges(int id) => adj.TryGetValue(id, out List<int>? e) ? e : (adj[id] = new List<int>());
        foreach (Branch br in inService)
        {
            Edges(br.From).Add(br.To);
            Edges(br.To).Add(br.From);
        }
        var live = new HashSet<int> { slackId };
        var stack = new Stack<int>();
        stack.Push(slackId);
        while (stack.Count > 0)
        {
            int u = stack.Pop();
            if (!adj.TryGetValue(u, out List<int>? nbrs)) continue;
            foreach (int v in nbrs) if (live.Add(v)) stack.Push(v);
        }
        return live;
    }
}
