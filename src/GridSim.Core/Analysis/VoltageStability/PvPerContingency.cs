using GridSim.Core.Abstractions;
using GridSim.Core.Analysis.Contingency;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis.VoltageStability;

/// <summary>Loadability of one state. <see cref="Status"/> is OK, ISLANDED (the outage splits the network; no solve was
/// attempted) or NO SOLUTION (the outage state does not converge at multiplier 1: <see cref="MaxLambda"/> is then the highest
/// lower multiplier that does, with a negative <see cref="MarginPct"/>, or 0 when none did). <see cref="MaxLambda"/> is the
/// largest converged load multiplier, <see cref="MarginPct"/> is 100 * (MaxLambda - 1). <see cref="Bracketed"/> is the
/// walk's own statement (<see cref="LoadabilityWalkEnd"/>): when false, MaxLambda is only a lower bound.</summary>
public sealed record PvContingencyRow(
    int Outage, string Label, string Status, double MaxLambda, double MarginPct, bool Bracketed, int Solves, double LowestFailedLambda);

/// <summary>The per-contingency loadability: the intact row and one row per outage in request order.</summary>
public sealed record PvContingencyResult(PvContingencyRow Base, IReadOnlyList<PvContingencyRow> Rows);

/// <summary>
/// Maximum loadability (the nose of the PV curve) under each single-branch outage (R4-03, report row 6.1). It runs the
/// existing <see cref="ContinuationPowerFlow.MaxLoadabilityWithEnd"/> walk, with the contingency study solver and options
/// (<see cref="ContingencyAnalysis.CreateStudySolver"/>, <see cref="ContingencyAnalysis.StudyOptions"/>: what the
/// <c>cpf</c> verb passes), on the outage copy of the model. An islanding outage is classified from the topology first and
/// no solve is made. The items are independent: each gets its own solver, so the parallel run returns the same rows in the
/// same order with the same bits as the serial one.
/// </summary>
public static class PvPerContingency
{
    /// <summary>Run it. <paramref name="outages"/> null = every in-service branch (model order); the walk parameters are
    /// those of <see cref="ContinuationPowerFlow.MaxLoadability(GridModel, double, double, int)"/>.</summary>
    public static PvContingencyResult Run(
        GridModel model, IReadOnlyList<int>? outages = null, double step = 0.25, double minStep = 1e-3, int maxSteps = 200,
        int maxDegreeOfParallelism = 1)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (maxDegreeOfParallelism < 1) throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism), maxDegreeOfParallelism, "at least 1.");
        if (!(step > 0) || !(minStep > 0) || minStep > step || maxSteps < 1)
            throw new ArgumentException("the walk needs 0 < minStep <= step and maxSteps >= 1.");
        int[] set = OutageModels.Resolve(model, outages);

        PvContingencyRow Walk(int outage, GridModel state, string label)
        {
            IPowerFlowSolver solver = ContingencyAnalysis.CreateStudySolver(state);
            (double max, LoadabilityWalkEnd end) = ContinuationPowerFlow.MaxLoadabilityWithEnd(
                state, solver, ContingencyAnalysis.StudyOptions, step, minStep, maxSteps);
            // The walk starts at multiplier 1, so a result below 1 means the state does not solve at the base load: the
            // value is then the highest LOWER load that does (the cpf verb's negative margin), or 0 when none solved.
            if (max <= 0.0)
                return new PvContingencyRow(outage, label, "NO SOLUTION", 0.0, double.NaN, false, end.Solves, end.LowestFailedLambda);
            if (max < 1.0)
                return new PvContingencyRow(outage, label, "NO SOLUTION", max, (max - 1.0) * 100.0, end.Bracketed, end.Solves, end.LowestFailedLambda);
            return new PvContingencyRow(outage, label, "OK", max, (max - 1.0) * 100.0, end.Bracketed, end.Solves, end.LowestFailedLambda);
        }

        PvContingencyRow baseRow = Walk(-1, model, ContingencyAnalysis.BaseCaseLabel);
        var rows = new PvContingencyRow[set.Length];
        void One(int i)
        {
            int k = set[i];
            string label = OutageModels.Label(model, k);
            rows[i] = OutageModels.Islands(model, k)
                ? new PvContingencyRow(k, label, "ISLANDED", double.NaN, double.NaN, false, 0, double.NaN)
                : Walk(k, OutageModels.Without(model, k), label);
        }
        if (maxDegreeOfParallelism == 1) for (int i = 0; i < set.Length; i++) One(i);
        else Parallel.For(0, set.Length, new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism }, One);
        return new PvContingencyResult(baseRow, rows);
    }
}
