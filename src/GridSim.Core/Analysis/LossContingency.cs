using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis;

/// <summary>
/// Per-contingency loss change: the delta between the post-outage and base-case
/// total system losses. A positive value means the outage increases losses.
/// </summary>
public sealed record LossContingencyResult(
    string Outage,
    bool Converged,
    double BaseLossMw,
    double PostLossMw,
    double DeltaLossMw,
    double MaxBranchLoadingPercent);

/// <summary>
/// Extends the N-1 contingency sweep with a loss dimension: for each branch outage,
/// how much do total system losses change? Identifies the outages that cause the
/// largest loss increase — the information a transmission-loss pricing or constraint-
/// management desk needs.
/// </summary>
public static class LossContingency
{
    public static IReadOnlyList<LossContingencyResult> Sweep(
        GridModel model, PowerFlowResult baseCase,
        IPowerFlowSolver? solver = null, PowerFlowOptions? options = null)
    {
        baseCase.RequireConverged("Loss contingency");   // the convergence contract (October 2026, WS4)
        solver ??= SolverSelector.CreateNewton(model);   // shared dense/sparse rule (was always dense; R004)
        double baseLoss = baseCase.TotalLossMw;
        var results = new LossContingencyResult[model.Branches.Count];

        for (int i = 0; i < model.Branches.Count; i++)
            results[i] = ScreenOne(model, i, baseLoss, solver, options);

        return results;
    }

    public static IReadOnlyList<LossContingencyResult> SweepParallel(
        GridModel model, PowerFlowResult baseCase,
        Func<IPowerFlowSolver>? solverFactory = null, PowerFlowOptions? options = null,
        int maxDegreeOfParallelism = -1, CancellationToken ct = default)
    {
        baseCase.RequireConverged("Loss contingency");   // the convergence contract (October 2026, WS4)
        solverFactory ??= () => SolverSelector.CreateNewton(model);   // shared dense/sparse rule (R004)
        double baseLoss = baseCase.TotalLossMw;
        var results = new LossContingencyResult[model.Branches.Count];
        using var local = new ThreadLocal<IPowerFlowSolver>(solverFactory);
        var po = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism <= 0 ? Environment.ProcessorCount : maxDegreeOfParallelism,
            CancellationToken = ct,
        };
        Parallel.For(0, model.Branches.Count, po, i =>
            results[i] = ScreenOne(model, i, baseLoss, local.Value!, options));
        return results;
    }

    private static LossContingencyResult ScreenOne(
        GridModel model, int branchIndex, double baseLoss,
        IPowerFlowSolver solver, PowerFlowOptions? options)
    {
        Branch outaged = model.Branches[branchIndex];
        string label = (string.IsNullOrEmpty(outaged.Name)
            ? $"{outaged.From}-{outaged.To}" : outaged.Name)
            + (outaged.IsTransformer ? " (tx)" : "");

        GridModel reduced = Without(model, branchIndex);

        // N299 (October 2026, stream P; the N232 short cut, LF2, applied to the loss sweep): an outage that leaves a part
        // of the network with no reference bus and a real-power balance that part can never meet cannot converge on
        // GridSim's own Newton engines (proof on ContingencyAnalysis.ProvablyUnsolvablePart), so its row is the
        // not-converged row below without spending the solve (the BiCGSTAB fallback ran to its iteration cap, about 6 s
        // per such outage on SyntheticGb). Bit-identity: a solve that does not converge, or that throws (a cancelled
        // token included, which the catch below has always turned into this same row), gives exactly
        // (label, false, baseLoss, 0, 0, 0); nothing of the failed result is read. Every other outage takes the unchanged
        // path (the check only reads the model). A caller's own solver is always called, as in the contingency screens.
        // The tolerance is the one the engine applies: the caller's options, or the defaults when none are given.
        if (ContingencyAnalysis.IsBuiltInNewton(solver)
            && ContingencyAnalysis.ProvablyUnsolvablePart(reduced, (options ?? new PowerFlowOptions()).Tolerance) is not null)
        {
            // A cancelled sweep stays cancelled (the solve this skips would have thrown at its first iteration).
            options?.Cancellation.ThrowIfCancellationRequested();
            return new LossContingencyResult(label, false, baseLoss, 0, 0, 0);
        }

        // Only a NUMERICAL failure of the solve (a singular or non-finite system, InvalidOperationException or
        // ArithmeticException) is a not-converged outage row. Cancellation is rethrown (a cancelled sweep used to come
        // back as a complete table with every remaining outage "not converged"), and any other exception is a defect,
        // not an outage result, so it is not dressed up as one (the contingency screen's rule since R225).
        PowerFlowResult r;
        try { r = solver.Solve(reduced, options); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is InvalidOperationException or ArithmeticException)
        {
            return new LossContingencyResult(label, false, baseLoss, 0, 0, 0);
        }

        if (!r.Converged)
            return new LossContingencyResult(label, false, baseLoss, 0, 0, 0);

        double maxLoad = 0;
        foreach (LineFlow f in r.LineFlows)
            if (f.LoadingPercent > maxLoad) maxLoad = f.LoadingPercent;

        return new LossContingencyResult(label, true, baseLoss, r.TotalLossMw,
            r.TotalLossMw - baseLoss, maxLoad);
    }

    private static GridModel Without(GridModel m, int branchIndex)
    {
        var branches = new List<Branch>(m.Branches.Count - 1);
        for (int j = 0; j < m.Branches.Count; j++)
            if (j != branchIndex) branches.Add(m.Branches[j]);
        return m.CopyWith(branches: branches);   // keeps asset dynamics and protections too (R270)
    }
}
