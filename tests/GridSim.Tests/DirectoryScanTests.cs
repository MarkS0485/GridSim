using GridSim.Core.IO;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R307: the WPF ModelCatalog enumerated gda-out/ and results/ folders with no IO guard, so one unreadable folder
/// threw out of MainWindow.OnLoaded. The shared Core <see cref="DirectoryScan"/> skips an unreadable folder and
/// records why, so the scan completes and the skip is visible.
/// </summary>
public sealed class DirectoryScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gridsim-scan-{Guid.NewGuid():N}");

    public DirectoryScanTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "b-run"));
        Directory.CreateDirectory(Path.Combine(_root, "A-case"));
        Directory.CreateDirectory(Path.Combine(_root, "c-other"));
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ } }

    [Fact]
    public void Readable_root_lists_subdirectories_sorted_case_insensitively()
    {
        var skipped = new List<string>();
        IReadOnlyList<string> subs = DirectoryScan.Subdirectories(_root, skipped);
        Assert.Equal(new[] { "A-case", "b-run", "c-other" }, subs.Select(Path.GetFileName).ToArray());
        Assert.Empty(skipped);
    }

    [Fact]
    public void Unreadable_root_is_skipped_and_recorded_not_thrown()
    {
        // A folder that vanished between discovery and listing: DirectoryNotFoundException (an IOException).
        string gone = Path.Combine(_root, "deleted-mid-scan");
        var skipped = new List<string>();
        IReadOnlyList<string> subs = DirectoryScan.Subdirectories(gone, skipped);
        Assert.Empty(subs);
        Assert.Single(skipped);
        Assert.StartsWith(gone + ": ", skipped[0]);
    }

    [Fact]
    public void A_failing_probe_counts_as_no_and_is_recorded()
    {
        var skipped = new List<string>();
        bool result = DirectoryScan.Probe("x", _ => throw new UnauthorizedAccessException("denied"), skipped);
        Assert.False(result);
        Assert.Equal(new[] { "x: denied" }, skipped);

        Assert.True(DirectoryScan.Probe("y", _ => true, skipped));
        Assert.Single(skipped);
    }

    [Fact]
    public void A_bug_in_the_probe_is_not_swallowed()
    {
        // Only filesystem failures are absorbed; anything else is a programming error and must surface.
        Assert.Throws<InvalidOperationException>(
            () => DirectoryScan.Probe("z", _ => throw new InvalidOperationException("bug"), new List<string>()));
    }
}
