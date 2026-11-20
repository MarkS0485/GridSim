using GridSim.Core.Analysis.Contingency;
using GridSim.Core.Model;

namespace GridSim.Core.Protection.ShortCircuit;

/// <summary>One bus of the fault-level sweep: the intact value, and the largest and smallest initial symmetrical
/// short-circuit current Ik'' over every state (the intact network and each single branch outage) in which the bus still
/// has a source, with the state that sets each. A state that leaves the bus with no source is counted, not valued.</summary>
public sealed record FaultLevelBusRow(
    int BusId, double IntactIkppKa, double IntactIkppPu,
    double MaxIkppKa, double MaxIkppPu, string MaxState,
    double MinIkppKa, double MinIkppPu, string MinState,
    int StatesEvaluated, int StatesWithoutSource);

/// <summary>The fault-level sweep: one row per fault bus (request order), and the states in the order they were evaluated
/// (the intact network first, then each outage in index order).</summary>
public sealed record FaultLevelUnderOutageResult(
    ShortCircuitObjective Objective, IReadOnlyList<string> States, IReadOnlyList<FaultLevelBusRow> Buses);

/// <summary>
/// Three-phase fault level under N-1 (R4-03, report row 3.10). For the intact network and then each outage in index
/// order, the outage copy is built (the branch removed, as the contingency verbs do), and <see cref="Iec60909.ThreePhase"/>
/// is evaluated at every requested bus. <c>Iec60909</c> restricts each fault to the fault bus's own island
/// (<c>FaultIsland</c>), so an outage that splits the network is handled by the incumbent rule: a bus left in an island with
/// no source has no Ik'' in that state (it is counted in <see cref="FaultLevelBusRow.StatesWithoutSource"/>), never a
/// number. The maximum and minimum are strict comparisons in evaluation order, so a tie names the first state. The
/// per-state work is independent, so the sweep may run in parallel across outages with the same output order and the same
/// bits.
/// </summary>
public static class FaultLevelUnderOutage
{
    /// <summary>The label of the intact state.</summary>
    public const string IntactLabel = "intact";

    /// <summary>Run the sweep. <paramref name="buses"/> null means every bus (model order); <paramref name="outages"/> null
    /// means every in-service branch (model order); <paramref name="maxDegreeOfParallelism"/> 1 is serial.</summary>
    public static FaultLevelUnderOutageResult Run(
        GridModel model, IReadOnlyList<int>? buses = null, IReadOnlyList<int>? outages = null,
        ShortCircuitObjective objective = ShortCircuitObjective.Maximum, int maxDegreeOfParallelism = 1)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (maxDegreeOfParallelism < 1) throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism), maxDegreeOfParallelism, "at least 1.");
        int[] set = OutageModels.Resolve(model, outages);
        int[] faultBuses = buses is null ? model.Buses.Select(b => b.Id).ToArray() : buses.ToArray();
        var known = new HashSet<int>(model.Buses.Select(b => b.Id));
        foreach (int id in faultBuses)
            if (!known.Contains(id)) throw new ArgumentException($"fault bus {id} is not in the model.", nameof(buses));

        // State 0 is the intact network; state s >= 1 is outage set[s - 1].
        int states = set.Length + 1;
        var labels = new string[states];
        labels[0] = IntactLabel;
        for (int s = 1; s < states; s++) labels[s] = $"branch {set[s - 1]} {OutageModels.Label(model, set[s - 1])}";
        var ika = new double[states][];
        var ipu = new double[states][];
        void Evaluate(int s)
        {
            GridModel state = s == 0 ? model : OutageModels.Without(model, set[s - 1]);
            var a = new double[faultBuses.Length];
            var p = new double[faultBuses.Length];
            for (int b = 0; b < faultBuses.Length; b++)
            {
                try
                {
                    Iec60909.Duty d = Iec60909.ThreePhase(state, faultBuses[b], objective);
                    a[b] = d.IkppKa; p[b] = d.IkppPu;
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("IEC 60909 needs at least one", StringComparison.Ordinal))
                {
                    a[b] = double.NaN; p[b] = double.NaN;   // no source in this bus's island in this state
                }
            }
            ika[s] = a; ipu[s] = p;
        }
        if (maxDegreeOfParallelism == 1) for (int s = 0; s < states; s++) Evaluate(s);
        else Parallel.For(0, states, new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism }, Evaluate);

        var rows = new List<FaultLevelBusRow>(faultBuses.Length);
        for (int b = 0; b < faultBuses.Length; b++)
        {
            int max = -1, min = -1, none = 0;
            for (int s = 0; s < states; s++)
            {
                double v = ipu[s][b];
                if (double.IsNaN(v)) { none++; continue; }
                if (max < 0 || v > ipu[max][b]) max = s;
                if (min < 0 || v < ipu[min][b]) min = s;
            }
            rows.Add(new FaultLevelBusRow(faultBuses[b], ika[0][b], ipu[0][b],
                max < 0 ? double.NaN : ika[max][b], max < 0 ? double.NaN : ipu[max][b], max < 0 ? "none" : labels[max],
                min < 0 ? double.NaN : ika[min][b], min < 0 ? double.NaN : ipu[min][b], min < 0 ? "none" : labels[min],
                states - none, none));
        }
        return new FaultLevelUnderOutageResult(objective, labels, rows);
    }
}
