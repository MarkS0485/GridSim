using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N238 (LF4, Ruling R1): <c>trip gb-synthetic</c> loaded the in-memory builder (no asset-classes, asset-bindings or
/// protections), not the shipped <c>data/gb-synthetic</c> folder, so <c>trip gb-synthetic --asset-dynamics</c> was refused
/// (exit 1, "needs a case with an asset bundle") and DOC-4's "trip gb-synthetic --asset-dynamics" checks never ran the
/// bundle. trip now loads the shipped folder for the name, exactly as <c>trip --dir data/gb-synthetic</c>.
/// </summary>
public sealed partial class LF4LeftoversTests
{
    private static string[] N238Figures(string stdout) => stdout.Split('\n')
        .Select(l => l.TrimEnd('\r'))
        .Where(l => l.StartsWith("Tripped:", StringComparison.Ordinal) || l.StartsWith("Peak RoCoF:", StringComparison.Ordinal)
                    || l.StartsWith("Nadir:", StringComparison.Ordinal) || l.StartsWith("Settling:", StringComparison.Ordinal)
                    || l.StartsWith("Asset dynamics:", StringComparison.Ordinal))
        .ToArray();

    [Theory]
    [InlineData("gb-synthetic")]
    [InlineData("synthetic")]
    [InlineData("GB-Synthetic")]
    public void N238_trip_by_name_runs_the_shipped_gb_synthetic_folder(string name)
    {
        string dir = Path.Combine(CliProcess.Root(), "data", "gb-synthetic");
        CliProcess.Result byName = CliProcess.RunBuilt("trip", name);
        CliProcess.Result byDir = CliProcess.RunBuilt("trip", "--dir", dir);
        Assert.True(byName.ExitCode == 0, byName.ToString());
        Assert.True(byDir.ExitCode == 0, byDir.ToString());
        string[] a = N238Figures(byDir.StdOut), b = N238Figures(byName.StdOut);
        Assert.Equal(4, a.Length);
        Assert.Equal(a, b);
        // The --dir run's nadir is pinned at 48.334 Hz (Doc5 D185, 1.95.191 capture); the name now gives the same.
        Assert.Contains("Nadir:          48.334 Hz", byName.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void N238_trip_gb_synthetic_with_asset_dynamics_now_runs_the_bundle()
    {
        // Before: exit 1, "--asset-dynamics needs a case with an asset bundle" (the builder writes none). After: the
        // bundle runs, identically to --dir data/gb-synthetic.
        string dir = Path.Combine(CliProcess.Root(), "data", "gb-synthetic");
        CliProcess.Result byName = CliProcess.RunBuilt("trip", "gb-synthetic", "--asset-dynamics");
        CliProcess.Result byDir = CliProcess.RunBuilt("trip", "--dir", dir, "--asset-dynamics");
        Assert.True(byName.ExitCode == 0, byName.ToString());
        Assert.True(byDir.ExitCode == 0, byDir.ToString());
        Assert.DoesNotContain("needs a case with an asset bundle", byName.StdErr, StringComparison.Ordinal);
        Assert.Equal(N238Figures(byDir.StdOut), N238Figures(byName.StdOut));
        Assert.Contains("Asset dynamics: on", byName.StdOut, StringComparison.Ordinal);
    }
}
