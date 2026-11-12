using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-532: the FDIA screen's rating margin took Math.Max over the two ends' sigma bounds, and an unmetered end's bound is
/// NaN, so Math.Max(NaN, x) = NaN gave a branch with ONE metered end no margin at all. A branch with one metered end now
/// takes that end's bound; with neither end metered there is still no margin. The bound class is private to the screen,
/// so this is a source-level anti-drift check (the end-to-end screen tests run the path).
/// </summary>
public class Gr532FdiaOneEndMarginTests
{
    private static string Source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, "GridSim.slnx not found");
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "GridSim.Core", "Estimation", "Security", "FdiaScreen.cs"));
    }

    [Fact]
    public void Gr532_one_metered_end_gives_that_ends_bound_and_neither_gives_none()
    {
        string src = Source();
        Assert.Contains("double u = double.IsNaN(uFrom) ? uTo : double.IsNaN(uTo) ? uFrom : Math.Max(uFrom, uTo);", src);
        Assert.Contains("if (double.IsNaN(u)) return 0.0;   // neither end is measured", src);
        // The old form, where one NaN end wiped out the margin, must not come back.
        Assert.DoesNotContain("double u = Math.Max(EndBound(", src);
    }

    [Fact]
    public void Gr532_math_max_propagates_nan_which_is_why_the_old_form_gave_no_margin()
    {
        Assert.True(double.IsNaN(Math.Max(double.NaN, 1.0)));
        Assert.True(double.IsNaN(Math.Max(1.0, double.NaN)));
    }
}
