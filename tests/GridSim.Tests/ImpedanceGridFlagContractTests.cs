using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N026 (R245 follow-up): the CLI hard-coded the canonical Thevenin grid, so the real-network impedance path (made
/// fail-loud by R245) was reachable only from code. `impedance-stability --grid canonical|real` now selects it.
/// Source-level (the test project does not reference the CLI).
/// </summary>
public class ImpedanceGridFlagContractTests
{
    private static string Program()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "GridSim.Cli", "Program.cs"));
    }

    [Fact]
    public void Grid_flag_selects_the_real_network_path_and_rejects_typos()
    {
        string src = Program();
        Assert.Contains("UseCanonicalThevenin = !realGrid,", src);
        Assert.Contains("ImpedanceStabilityScan.Run(opt, model, realGrid ? m : null)", src);
        Assert.Contains("FailFlag<bool>(\"--grid\", bad, \"canonical or real\")", src);
        Assert.DoesNotContain("UseCanonicalThevenin = true,", src);
    }

    [Fact]
    public void Scr_sweep_flags_are_refused_with_the_real_grid()
    {
        Assert.Contains("the SCR sweep applies to --grid canonical only", Program());
    }
}
