using System.Runtime;

namespace GridSim.Core.Simulation;

/// <summary>
/// Scoped control over the garbage collector for compute-heavy or real-time
/// sections. The GC can't be turned off outright for a long-running process, but:
///
/// <list type="bullet">
/// <item><see cref="LowLatency"/> switches to <c>SustainedLowLatency</c> for the
/// scope  -  full blocking gen2 collections are deferred, so a replay or live loop
/// runs without GC stutter. Safe to hold for a long time; collections still happen,
/// just not the disruptive ones.</item>
/// <item><see cref="NoGc"/> uses a true no-GC region for a <em>bounded</em> burst of
/// work: pre-reserve a byte budget and the GC won't run until it's spent (then it
/// resumes). Use only around finite batches  -  never an open-ended loop.</item>
/// </list>
///
/// Both return an <see cref="IDisposable"/> that restores the previous mode.
/// </summary>
public static class GcScope
{
    public static IDisposable LowLatency() => new LatencyScope(GCLatencyMode.SustainedLowLatency);

    /// <summary>
    /// Begin a no-GC region reserving <paramref name="budgetBytes"/> (default 256 MB).
    /// If the runtime can't honour it the scope is a no-op rather than throwing.
    /// </summary>
    public static IDisposable NoGc(long budgetBytes = 256L * 1024 * 1024) => new NoGcScope(budgetBytes);

    // GR-862: the latency mode is process-global, so overlapping scopes (two sessions, or a scope on each of two threads)
    // used to restore it out of order: the first scope to end put back the mode from before ALL of them while the other
    // was still running, and the last one then restored a mode that was itself a scope's. The scopes are now counted: the
    // first one saves the mode and sets it, the last one to end restores the saved mode. A single scope behaves as before.
    private static readonly object LatencyGate = new();
    private static int _latencyDepth;
    private static GCLatencyMode _latencyBefore;

    private sealed class LatencyScope : IDisposable
    {
        private int _disposed;
        public LatencyScope(GCLatencyMode mode)
        {
            lock (LatencyGate)
            {
                if (_latencyDepth++ == 0)
                {
                    _latencyBefore = GCSettings.LatencyMode;
                    try { GCSettings.LatencyMode = mode; } catch { /* not supported on this config */ }
                }
            }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;   // a second Dispose does not unbalance the count
            lock (LatencyGate)
            {
                if (--_latencyDepth == 0)
                {
                    try { GCSettings.LatencyMode = _latencyBefore; } catch { /* ignore */ }
                }
            }
        }
    }

    private sealed class NoGcScope : IDisposable
    {
        private readonly bool _started;
        public NoGcScope(long budget)
        {
            try { _started = GC.TryStartNoGCRegion(budget); } catch { _started = false; }
        }
        public void Dispose()
        {
            if (_started && GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
            {
                try { GC.EndNoGCRegion(); } catch { /* budget already exceeded  -  region ended itself */ }
            }
        }
    }
}
