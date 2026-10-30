using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis;

/// <summary>The power crossing a boundary in a solved case, against its limit.</summary>
public sealed record BoundaryFlow(string Name, double FlowMw, double LimitMw, double LoadingPercent, bool Overloaded);

/// <summary>
/// Evaluates how much power is crossing each defined boundary and whether it's
/// within the permitted transfer  -  the security check NESO runs constantly (e.g.
/// keeping the B6 Scotland->England flow under SCOTEX). Flow is the signed sum of
/// the boundary's branches' sending-end real power.
/// </summary>
public static class BoundaryAnalysis
{
    /// <summary>
    /// The flow across every boundary of a solved model. N138 (October 2026): every boundary branch name must name
    /// exactly one branch of <paramref name="model"/> (<see cref="Boundary.BranchNameProblems"/>), otherwise
    /// <see cref="InvalidDataException"/>. A name that matched nothing used to be skipped without a word (the boundary's
    /// flow silently left that branch out) and a name several branches share took the last of them; the JSON loader
    /// has refused both since N097, and now a model built in code (the bridge, a test, a derived study model) is held
    /// to the same rule here. The flows themselves are computed exactly as before.
    /// <para>D-DOC-087 (October 2026): <paramref name="result"/> must have converged
    /// (<see cref="ConvergenceContract.RequireConverged"/>, <see cref="NonConvergedStateException"/> otherwise). The flows
    /// of a non-converged state are a transient iterate, so a boundary loading read from them is not a network state;
    /// they used to be reported like any other. Callers that display a live frame check <c>Converged</c> first.</para>
    /// </summary>
    public static IReadOnlyList<BoundaryFlow> Evaluate(GridModel model, PowerFlowResult result)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(result);
        Boundary.RequireResolvedBranchNames(model);
        result.RequireConverged("Boundary analysis");   // D-DOC-087: the convergence contract
        return EvaluateAfterOutage(model, result);
    }

    /// <summary>
    /// The same flows without the name check, for a post-outage model the contingency screen derived from a checked
    /// one: a boundary branch that is the outage is no longer in the reduced model, contributes no flow, and is
    /// correctly absent. Callers check the unreduced model first (N138).
    /// </summary>
    internal static IReadOnlyList<BoundaryFlow> EvaluateAfterOutage(GridModel model, PowerFlowResult result)
    {
        var byName = new Dictionary<string, LineFlow>();
        foreach (LineFlow f in result.LineFlows)
            if (!string.IsNullOrEmpty(f.Name)) byName[f.Name] = f;

        var flows = new List<BoundaryFlow>(model.Boundaries.Count);
        foreach (Boundary b in model.Boundaries)
        {
            double flow = 0;
            foreach (BoundaryBranch bb in b.Branches)
                if (byName.TryGetValue(bb.Branch, out LineFlow? lf)) flow += bb.Direction * lf.PFromMw;

            // A limit of exactly 0 means "no limit authored" (the JSON field's default when omitted; the watch-list
            // already reads it that way): the flow is reported, with no loading and never overloaded. Before
            // October 2026 (R224) any non-zero flow across such a boundary was an overload, so the N-1 screen
            // flagged it on every outage. A negative or non-finite limit is not a limit at all: fail loud.
            if (!double.IsFinite(b.LimitMw) || b.LimitMw < 0)
                throw new InvalidDataException(
                    $"Boundary '{b.Name}' has transfer limit {b.LimitMw} MW; a limit must be a finite non-negative MW value (0 = no limit set).");
            bool limited = b.LimitMw > 0;
            double loading = limited ? Math.Abs(flow) / b.LimitMw * 100.0 : 0.0;
            flows.Add(new BoundaryFlow(b.Name, flow, b.LimitMw, loading, limited && Math.Abs(flow) > b.LimitMw));
        }
        return flows;
    }
}
