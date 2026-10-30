namespace GridSim.Tests;

/// <summary>
/// P0 (October 2026, suite runtime): a process-wide limit on how many DENSE Newton solves of a large case (above
/// <see cref="BusThreshold"/> buses, e.g. case2869pegase) the test suite runs at the same moment.
///
/// Why: a few tests now run their independent solves side by side to shorten the suite (see
/// <c>S10TestSupport.AssertSolvesIdentical</c> and <c>DenseSparseParityTests</c>). A dense solve of a 2,869-bus case
/// holds n x n matrices of several hundred MB, and the suite runs on an 8 GB laptop where the test host already peaks
/// above 2 GB. Before those changes, the two helpers' large dense solves could overlap at most two at a time (one in
/// each of the two test classes, whose tests ran their solves one after another); this gate keeps those helpers at
/// two whatever the scheduling, so the change cannot push the machine into paging or a low-memory kill. Other tests'
/// solves are not routed through it (they did not change). It only orders solves in time: what each one computes is
/// unaffected.
/// </summary>
internal static class BigDenseSolveGate
{
    /// <summary>Models with more buses than this count as large for the gate.</summary>
    public const int BusThreshold = 2000;

    /// <summary>At most this many large dense solves at once (the overlap the serial tests already allowed).</summary>
    public const int MaxConcurrent = 2;

    private static readonly SemaphoreSlim Gate = new(MaxConcurrent, MaxConcurrent);

    // Exclusive callers (RunExclusive) queue here before taking any slot (GR-882).
    private static readonly object ExclusiveLock = new();

    /// <summary>Run <paramref name="solve"/>, waiting for a slot first when <paramref name="busCount"/> is large.</summary>
    public static T Run<T>(int busCount, Func<T> solve)
    {
        if (busCount <= BusThreshold) return solve();
        Gate.Wait();
        try { return solve(); }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// D-DOC-211 (October 2026): run <paramref name="work"/> so that it is the ONLY large dense solve routed through this
    /// gate (case9241pegase's dense solve holds several GB on its own). It takes every slot but one before starting the
    /// work; the work's own large solve then takes the last slot through <see cref="Run{T}"/> as usual, so nothing else
    /// large can start beside it, and nothing deadlocks. The slots are taken one at a time and always all released,
    /// whatever the work throws.
    /// </summary>
    public static void RunExclusive(Action work)
    {
        // GR-882: two exclusive callers taking slots at once could each hold some and wait on the other for ever, so
        // exclusive callers queue on their own lock first; only one at a time ever takes the MaxConcurrent - 1 slots.
        lock (ExclusiveLock)
        {
            int taken = 0;
            try
            {
                for (; taken < MaxConcurrent - 1; taken++) Gate.Wait();
                work();
            }
            finally
            {
                if (taken > 0) Gate.Release(taken);
            }
        }
    }
}
