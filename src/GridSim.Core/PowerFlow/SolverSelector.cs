using GridSim.Core.Abstractions;
using GridSim.Core.Model;

namespace GridSim.Core.PowerFlow;

/// <summary>Which power-flow engine to run. <see cref="Auto"/> lets <see cref="SolverSelector"/> pick.</summary>
public enum SolverKind
{
    /// <summary>Pick the engine from the network shape (radial -> sweep, else sparse Newton since N057; below the
    /// caller's own threshold, when one is given, dense).</summary>
    Auto,
    /// <summary>Dense Newton-Raphson - by explicit choice (bit-identical to the sparse engine on the CPU; the default
    /// for small meshed networks until N057).</summary>
    DenseNewton,
    /// <summary>Sparse Newton-Raphson - the default for every meshed network since N057.</summary>
    SparseNewton,
    /// <summary>Backward/forward sweep - for radial distribution feeders the transmission NR can't solve well.</summary>
    DistributionSweep,
}

/// <summary>
/// Single source of truth for choosing a power-flow engine per network. Both the CLI and the WPF
/// dashboard route through here so a given case always solves with the same engine. The three
/// engines all implement <see cref="IPowerFlowSolver"/>, so any of them drops into
/// <see cref="SteadyStateSolver"/>.
/// </summary>
public static class SolverSelector
{
    /// <summary>
    /// The power-flow routing threshold: Newton solves are dense up to this many buses and sparse above. Since N057
    /// (Ruling R1, Mark delegated 2026-10-04) it is 0, so EVERY CPU Newton power flow routes to the sparse engine
    /// (a model with at least one bus is above 0), as MATPOWER solves every size sparse.
    /// <para>Why: both engines have shared the sparse-LU step since 1.95.001, so the two are bit-identical on the CPU
    /// and the choice is speed and memory only. Re-bench (S4 solver stream, Release, warm, median ms dense / sparse,
    /// results bit-identical on every case): case9 0.247 / 0.209, case14 0.293 / 0.195, case30 0.806 / 0.547, case57
    /// 1.681 / 0.821, case118 7.954 / 2.477, case145 13.816 / 4.898, case300 80.815 / 7.348, case588_sdet 465.008 /
    /// 37.734, case1354pegase 1852.764 / 59.151, gb-synthetic 3582.322 / 28.455; sparse is faster at every size. The old
    /// value was 200 (bench 2026-07-14, measured before the engines shared the LU step).</para>
    /// <para>Dense stays available by explicit choice: <see cref="Create"/> with <see cref="SolverKind.DenseNewton"/>
    /// (the web / WPF solver pickers), a <c>sparseThreshold</c> argument, and the CLI's own flags
    /// (<c>gda --solve --dense</c>, <c>bench</c>). The live per-tick loop keeps its own threshold
    /// (<c>LiveSolveSettings.SparseThresholdBuses</c>, 1,500), because there the engine choice also picks the solve
    /// options, and the GPU routing keeps the old crossover (<see cref="DenseSparseCrossoverBuses"/>): it was not
    /// measured bit-identical and no slower there. (The plain WLS estimator no longer switches on size: since 1.95.299
    /// every plain WLS / robust CPU estimate runs the canonical sparse step, CanonicalWlsStep, at every size.)</para>
    /// </summary>
    public const int DefaultSparseThreshold = 0;

    /// <summary>
    /// The measured dense / sparse crossover from before N057 (200 buses; bench 2026-07-14: dense/sparse ms - case118
    /// 8/60, case300 1964/23, case1354 7130/59, case2869 65820/150, case9241 dense OOM; the estimator crossover is even
    /// lower - case300 1235/161). N057 split the old shared constant: the power-flow routing moved to
    /// <see cref="DefaultSparseThreshold"/> (0, always sparse) while the users of this one keep exactly their old
    /// behaviour: the GPU dense / sparse routing (the dense engine there is the GPU dense LU, outside the CPU
    /// bit-identity contract) and the solve-export trace (only the dense engine captures the Jacobian, so the export
    /// keeps the engine it always used). The WLS state estimator's plain path used this crossover for its dense / sparse
    /// KKT choice until 1.95.299, when it moved to the canonical sparse step at every size (the dense observability
    /// gate still applies at 200 buses and below).
    /// </summary>
    public const int DenseSparseCrossoverBuses = 200;

    /// <summary>
    /// A strictly radial (tree) network - one slack and exactly <c>buses-1</c> in-service branches.
    /// Meshed transmission (any loop, e.g. case9) is not radial, so this only catches distribution
    /// feeders, which are routed to the backward/forward-sweep solver.
    /// <para>Only IN-SERVICE branches count (R036, October 2026), as this summary and the power-flow docs always
    /// said: an open tie on a feeder is electrically absent, and the sweep itself ignores it. Counting every
    /// branch sent a feeder carrying one open normally-open tie to Newton instead of the sweep.</para>
    /// <para>CONNECTED as well as n-1 branches (October 2026 docs-programme finding): a graph with n-1 edges is a
    /// tree only if it is connected. A model split into islands can still carry exactly n-1 in-service branches (one
    /// island holds a loop, another bus hangs free), and the old count-only test sent it to the sweep, which then
    /// returned NonFinite. Such a model is now not radial and goes to Newton, which reports its own failure. A branch
    /// naming a bus that is not in the model also makes the network not radial.</para>
    /// </summary>
    public static bool LooksRadial(GridModel m)
    {
        ArgumentNullException.ThrowIfNull(m);
        // A model with LCC HVDC links goes to Newton, which solves them; the sweep does not model them.
        if (m.HasCoupledDevices) return false;
        // The cheap tests first: exactly n-1 in-service branches and exactly one slack.
        if (m.Branches.Count(b => b.InService) != m.BusCount - 1) return false;
        if (m.Buses.Count(b => b.Type == BusType.Slack) != 1) return false;
        return InServiceGraphIsConnected(m);
    }

    /// <summary>True when every bus is reachable from the first bus over in-service branches (a breadth-first
    /// search). False when any in-service branch names a bus that is not in the model.</summary>
    private static bool InServiceGraphIsConnected(GridModel m)
    {
        int n = m.BusCount;
        if (n == 0) return false;
        var indexOf = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) indexOf[m.Buses[i].Id] = i;

        // Adjacency lists over bus indices, built from the in-service branches only.
        var adjacent = new List<int>[n];
        for (int i = 0; i < n; i++) adjacent[i] = new List<int>();
        foreach (Branch br in m.Branches)
        {
            if (!br.InService) continue;
            if (!indexOf.TryGetValue(br.From, out int f) || !indexOf.TryGetValue(br.To, out int t)) return false;
            adjacent[f].Add(t);
            adjacent[t].Add(f);
        }

        var seen = new bool[n];
        var queue = new Queue<int>();
        seen[0] = true;
        queue.Enqueue(0);
        int reached = 1;
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            foreach (int j in adjacent[i])
            {
                if (seen[j]) continue;
                seen[j] = true;
                reached++;
                queue.Enqueue(j);
            }
        }
        return reached == n;
    }

    /// <summary>Resolve <see cref="SolverKind.Auto"/> to a concrete engine from the network shape.</summary>
    public static SolverKind Resolve(GridModel m, int sparseThreshold = DefaultSparseThreshold) =>
        LooksRadial(m) ? SolverKind.DistributionSweep : ResolveNewton(m, sparseThreshold);

    /// <summary>
    /// The Newton-Raphson engine for <paramref name="m"/>: dense up to <paramref name="sparseThreshold"/> buses,
    /// sparse past it - never the backward/forward sweep. For callers that solve every network with Newton
    /// (the protection cascade, the CLI chain/stress/stability verbs). Since 1.95.001 the two Newton engines are
    /// bit-identical on the CPU, so this choice changes speed and memory only, never a number (October 2026,
    /// R003/R134/R135/R147: before, the same case was routed by 200 here and by 1,000, 1,500 or 3,000 elsewhere).
    /// With the default threshold (0 since N057) every non-empty model gets the sparse engine.
    /// </summary>
    public static SolverKind ResolveNewton(GridModel m, int sparseThreshold = DefaultSparseThreshold) =>
        m.BusCount > sparseThreshold ? SolverKind.SparseNewton : SolverKind.DenseNewton;

    /// <summary>The CPU Newton engine <see cref="ResolveNewton"/> picks for <paramref name="m"/>.</summary>
    public static IPowerFlowSolver CreateNewton(GridModel m, int sparseThreshold = DefaultSparseThreshold) =>
        ResolveNewton(m, sparseThreshold) == SolverKind.SparseNewton
            ? new SparseNewtonRaphsonPowerFlow()
            : new NewtonRaphsonPowerFlow();

    /// <summary>
    /// Build the engine for <paramref name="kind"/> (resolving <see cref="SolverKind.Auto"/> against
    /// <paramref name="m"/>) on the CPU backends, with a human-readable label for the UI.
    /// </summary>
    public static (IPowerFlowSolver solver, SolverKind resolved, string label) Create(
        SolverKind kind, GridModel m, int sparseThreshold = DefaultSparseThreshold)
    {
        SolverKind resolved = kind == SolverKind.Auto ? Resolve(m, sparseThreshold) : kind;
        IPowerFlowSolver solver = resolved switch
        {
            SolverKind.DistributionSweep => new BackwardForwardSweep(),
            SolverKind.SparseNewton => new SparseNewtonRaphsonPowerFlow(),
            _ => new NewtonRaphsonPowerFlow(),
        };
        return (solver, resolved, Label(resolved));
    }

    /// <summary>The engine's display name (as resolved - never returns "Auto").</summary>
    public static string Label(SolverKind kind) => kind switch
    {
        SolverKind.DistributionSweep => "Backward-forward sweep",
        SolverKind.SparseNewton => "Newton-Raphson (sparse)",
        _ => "Newton-Raphson (dense)",
    };
}
