using Xunit;

namespace GridSim.Tests;

/// <summary>
/// 2.1.074 RC-4-Fixes: compose.yml runs the web container with a read-only root file system, no Linux capabilities and
/// no privilege gain. These tests read the file; they do NOT start a container, so that the image runs and passes its
/// health check under these settings is unverified until someone runs <c>docker compose up</c> (see docs/assurance).
/// </summary>
public sealed class ComposeHardeningTests
{
    private static string Compose()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "compose.yml")).Replace("\r\n", "\n");
    }

    // The uncommented lines of the web service, so a mention in a comment cannot satisfy a check.
    private static string[] Instructions() =>
        Compose().Split('\n').Where(l => !l.TrimStart().StartsWith('#')).ToArray();

    [Fact]
    public void The_container_has_a_read_only_root_file_system_with_an_in_memory_tmp()
    {
        string[] lines = Instructions();
        Assert.Contains("    read_only: true", lines);
        int tmpfs = Array.IndexOf(lines, "    tmpfs:");
        Assert.True(tmpfs >= 0);
        Assert.Equal("      - /tmp", lines[tmpfs + 1]);
    }

    [Fact]
    public void Every_capability_is_dropped_and_privilege_gain_is_refused()
    {
        string[] lines = Instructions();
        int drop = Array.IndexOf(lines, "    cap_drop:");
        Assert.True(drop >= 0);
        Assert.Equal("      - ALL", lines[drop + 1]);
        int opt = Array.IndexOf(lines, "    security_opt:");
        Assert.True(opt >= 0);
        Assert.Equal("      - no-new-privileges:true", lines[opt + 1]);
        Assert.DoesNotContain(lines, l => l.TrimStart().StartsWith("privileged:", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.TrimStart().StartsWith("cap_add:", StringComparison.Ordinal));
    }

    [Fact]
    public void The_key_ring_volume_stays_writable_by_default()
    {
        Assert.Contains(Instructions(), l => l.Contains("/keys:${GRIDSIM_KEYS_MODE:-rw}", StringComparison.Ordinal));
    }
}
