using System.Diagnostics;
using GridSim.Gpu;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-471: the CUDA driver probe ran inside GpuContext's init lock with no timeout, so a driver hung in cuInit hung every
/// later GPU query. It now runs on a background worker, outside the lock, bounded by a timeout, and never throws. These
/// tests drive the seam (GpuContext.ProbeWithTimeout) with stand-in probes; a real wedged driver cannot be produced here.
/// </summary>
public class Gr471DriverProbeTimeoutTests
{
    [Fact]
    public void Gr471_a_probe_that_never_returns_is_reported_as_timed_out_within_the_bound()
    {
        using var never = new ManualResetEventSlim(false);
        var sw = Stopwatch.StartNew();
        string r = GpuContext.ProbeWithTimeout(() => { never.Wait(); return "unreachable"; }, TimeSpan.FromMilliseconds(200));
        sw.Stop();
        Assert.StartsWith("driver probe timed out after 0.2 s", r);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"waited {sw.Elapsed}");   // bounded, not hung
        never.Set();   // release the stand-in worker
    }

    [Fact]
    public void Gr471_a_probe_that_returns_gives_its_own_text()
        => Assert.Equal("cuInit=0 driverVer=1", GpuContext.ProbeWithTimeout(() => "cuInit=0 driverVer=1", TimeSpan.FromSeconds(5)));

    [Fact]
    public void Gr471_a_probe_that_throws_is_reported_not_rethrown()
    {
        string r = GpuContext.ProbeWithTimeout(() => throw new DllNotFoundException("no libcuda"), TimeSpan.FromSeconds(5));
        Assert.Equal("DllNotFoundException: no libcuda", r);
    }

    [Fact]
    public void Gr471_the_production_bound_is_ten_seconds()
        => Assert.Equal(TimeSpan.FromSeconds(10), GpuContext.DriverProbeTimeout);

    [Fact]
    public void Gr471_the_probe_runs_before_the_init_lock_is_taken()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, "GridSim.slnx not found");
        string src = File.ReadAllText(Path.Combine(dir!.FullName, "src", "GridSim.Gpu", "GpuContext.cs")).Replace("\r\n", "\n");
        int ensure = src.IndexOf("private static void EnsureInitialised()", StringComparison.Ordinal);
        int probe = src.IndexOf("ProbeWithTimeout(DriverProbe, DriverProbeTimeout)", ensure, StringComparison.Ordinal);
        int lockAt = src.IndexOf("lock (InitLock)", ensure, StringComparison.Ordinal);
        Assert.True(ensure > 0 && probe > ensure && lockAt > probe, "the timed probe must come before lock (InitLock)");
        Assert.DoesNotContain("string drv = DriverProbe();", src);
    }
}
