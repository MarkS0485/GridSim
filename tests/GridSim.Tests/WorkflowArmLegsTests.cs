using Xunit;

namespace GridSim.Tests;

/// <summary>
/// UI programme Phase 0.5 (2026-10-24): the release-gate CI matrix also runs the full suite on the Linux and Windows
/// ARM64 runners, in the normal and hwintrinsic-off modes, against the same pins as every other leg (desktop ARM64 is
/// held to the full bit-identity rule). Pinned so an edit cannot quietly drop an ARM leg or make one advisory.
/// </summary>
public class WorkflowArmLegsTests
{
    private static string Ci()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, ".github", "workflows", "ci.yml")).Replace("\r\n", "\n");
    }

    [Fact]
    public void The_ci_matrix_runs_linux_and_windows_arm64_in_normal_and_hwintrinsic_off_modes()
    {
        string ci = Ci();
        foreach (string leg in new[]
                 {
                     "- { os: ubuntu-24.04-arm, simd: normal }",
                     "- { os: ubuntu-24.04-arm, simd: hwintrinsic-off }",
                     "- { os: windows-11-arm,   simd: normal }",
                     "- { os: windows-11-arm,   simd: hwintrinsic-off }",
                 })
            Assert.Contains(leg, ci, StringComparison.Ordinal);

        // The ARM legs are matrix entries of the gated job (the "Build & test" job waits for the whole matrix), not a
        // separate job that could be left out of the gate, and nothing anywhere is advisory.
        int matrix = ci.IndexOf("\n  matrix:\n", StringComparison.Ordinal);
        int vulnerable = ci.IndexOf("\n  vulnerable:\n", StringComparison.Ordinal);
        Assert.True(matrix >= 0 && vulnerable > matrix, "the matrix job is not where it was");
        string matrixJob = ci[matrix..vulnerable];
        Assert.Contains("ubuntu-24.04-arm", matrixJob, StringComparison.Ordinal);
        Assert.Contains("windows-11-arm", matrixJob, StringComparison.Ordinal);
        Assert.DoesNotContain("continue-on-error", ci, StringComparison.Ordinal);
        Assert.Contains("needs: [ matrix, vulnerable, container ]", ci, StringComparison.Ordinal);

        // The hwintrinsic-off legs prove the switch took effect on ARM too (simdcheck must report the scalar tier).
        Assert.Contains("[[ \"$SIMD\" == \"hwintrinsic-off\" ]] && want=\"Active vector tier: Scalar\"", ci, StringComparison.Ordinal);
    }
}
