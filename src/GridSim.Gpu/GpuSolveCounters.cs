namespace GridSim.Gpu;

/// <summary>
/// How many linear solves the GPU solvers made, and where each one ran. RC4: a GPU sparse solve that gives up is retried
/// on the exact CPU LU (R038), and a GPU solver handed a system it cannot take (no device, a zero diagonal, a ROCm
/// out-of-memory) solves it on the host itself, so a run selected with <c>--gpu</c> can run partly or entirely on the
/// CPU. On the RC4 MATPOWER sweep on an RTX A4000, 87 of 309 Newton steps did, and every step of case9241pegase and
/// GBnetwork. These counters make that visible: the CLI reports them beside a GPU solve and in the run metadata.
///
/// Process-wide and thread-safe. Take a <see cref="Snapshot"/> before and after a solve and subtract to get that
/// solve's counts. The counters only ever count; they change no numerical result.
/// </summary>
public static class GpuSolveCounters
{
    private static long _onDevice, _gaveUp, _onHost;

    /// <summary>The counts so far in this process.</summary>
    public static GpuSolveCount Snapshot() =>
        new(Interlocked.Read(ref _onDevice), Interlocked.Read(ref _gaveUp), Interlocked.Read(ref _onHost));

    /// <summary>A solve completed on the device.</summary>
    internal static void OnDevice() => Interlocked.Increment(ref _onDevice);

    /// <summary>The device solver gave up (returned null); the caller solves the step on the CPU LU.</summary>
    internal static void GaveUp() => Interlocked.Increment(ref _gaveUp);

    /// <summary>The GPU solver solved the system on the host itself (no device, an unusable diagonal, out of memory).</summary>
    internal static void OnHost() => Interlocked.Increment(ref _onHost);
}

/// <summary>Linear-solve counts of the GPU solvers (see <see cref="GpuSolveCounters"/>).</summary>
/// <param name="OnDevice">Solves completed on the device.</param>
/// <param name="GaveUpToCpu">Solves the device gave up on, which the caller then solved on the CPU LU.</param>
/// <param name="OnHost">Solves a GPU solver ran on the host itself.</param>
public readonly record struct GpuSolveCount(long OnDevice, long GaveUpToCpu, long OnHost)
{
    /// <summary>Every linear solve a GPU solver was asked for.</summary>
    public long Total => OnDevice + GaveUpToCpu + OnHost;

    /// <summary>Solves whose answer came from the CPU.</summary>
    public long OnCpu => GaveUpToCpu + OnHost;

    /// <summary>True when GPU solvers were asked for at least one solve and none was answered on the device.</summary>
    public bool RanEntirelyOnCpu => Total > 0 && OnDevice == 0;

    /// <summary>The counts between two snapshots.</summary>
    public static GpuSolveCount operator -(GpuSolveCount after, GpuSolveCount before) =>
        new(after.OnDevice - before.OnDevice, after.GaveUpToCpu - before.GaveUpToCpu, after.OnHost - before.OnHost);

    /// <summary>One line for a report, e.g. <c>12 linear solves: 9 on the device, 3 on the CPU (3 gave up to the CPU LU, 0 on the host)</c>.</summary>
    public string Describe() =>
        Total == 0
            ? "no GPU linear solves"
            : $"{Total} linear solve{(Total == 1 ? "" : "s")}: {OnDevice} on the device, {OnCpu} on the CPU "
              + $"({GaveUpToCpu} gave up to the CPU LU, {OnHost} on the host)"
              + (RanEntirelyOnCpu ? "; the whole solve ran on the CPU" : "");
}
