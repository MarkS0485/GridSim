using Xunit;

namespace GridSim.Tests;

/// <summary>The temporary-folder helper must never record, sweep or delete the temporary folder itself or anything outside
/// it (review-tel.md, HIGH). On Windows " ", "...", ". " and ".. " all resolve to the folder itself.</summary>
public sealed class TestTempContainmentTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData(". ")]
    [InlineData(".. ")]
    [InlineData("gridsim-x ")]
    [InlineData("gridsim-x.")]
    public void Names_that_resolve_to_the_folder_itself_or_change_under_normalisation_are_refused(string name)
    {
        Assert.Throws<ArgumentException>(() => TestTemp.Under(name));
    }

    [Fact]
    public void The_temporary_folder_itself_and_paths_outside_it_are_never_safe()
    {
        string root = Path.GetFullPath(Path.GetTempPath());
        Assert.False(TestTemp.IsSafeChild(root));
        Assert.False(TestTemp.IsSafeChild(root.TrimEnd(Path.DirectorySeparatorChar)));
        Assert.False(TestTemp.IsSafeChild(Path.GetFullPath(Path.Combine(root, ".."))));
        Assert.False(TestTemp.IsSafeChild(Path.Combine(root, "gridsim-a", "nested")));
        Assert.False(TestTemp.IsSafeChild(Path.Combine(root, "...")));
        Assert.False(TestTemp.IsSafeChild(Path.Combine(root, " ")));
        Assert.True(TestTemp.IsSafeChild(Path.Combine(root, "gridsim-containment-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void Sweep_skips_a_path_that_is_not_a_safe_child_and_leaves_it_in_place()
    {
        string root = Path.GetFullPath(Path.GetTempPath());
        IReadOnlyList<string> left = TestTemp.Sweep([root, Path.Combine(root, "...")]);
        Assert.Empty(left);
        Assert.True(Directory.Exists(root));
    }
}
