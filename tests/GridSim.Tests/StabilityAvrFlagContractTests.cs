using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R061 (CLI part): `stability --rms --avr none|sexs|ieeet1 [--pss]` wires the RMS study's new exciter input. --avr none
/// (the default) must pass no exciters, so the study takes exactly its old path. Source-level (the test project does
/// not reference the CLI).
/// </summary>
public class StabilityAvrFlagContractTests
{
    private static string Program()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "GridSim.Cli", "Program.cs"));
    }

    [Fact]
    public void Avr_flag_is_validated_and_none_passes_no_exciters()
    {
        string src = Program();
        Assert.Contains("FailFlag<string>(\"--avr\", bad, \"none, sexs or ieeet1\")", src);
        Assert.Contains("List<RmsExciter?>? exciters = null;", src);
        Assert.Contains("if (avr != \"none\")", src);
        Assert.Contains("error: --pss needs an AVR", src);
        Assert.Contains("error: --avr and --pss apply to the RMS engine only", src);
    }

    [Fact]
    public void Every_rms_study_call_receives_the_exciters()
    {
        string src = Program();
        // Moved 2026-10 (N068, Ruling R1, Mark delegated 2026-10-04): the RMS CCT number and its NON-MONOTONE flag come
        // from ONE bracket search, so the pinned call is now CriticalClearingBracket (whose StableSeconds is exactly what
        // CriticalClearingTimeSeconds returns: that method is the bracket's stable end, same arguments). The intent is
        // unchanged: every RMS study call receives the exciters. Before: the two pins named
        // "RmsTransientStability.CriticalClearingTimeSeconds(model, pf, data, b|faultBus, exciters: exciters)", which kept
        // a second, identical search alive for the flag (each CCT search ran twice).
        Assert.Contains("RmsTransientStability.CriticalClearingBracket(model, pf, data, b, exciters: exciters)", src);
        Assert.Contains("RmsTransientStability.CriticalClearingBracket(model, pf, data, faultBus, exciters: exciters)", src);
        Assert.Contains("exciters: exciters);", src);
        // And the search runs once: no RMS CriticalClearingTimeSeconds call is left beside the bracket in the CLI.
        Assert.DoesNotContain("RmsTransientStability.CriticalClearingTimeSeconds(", src);
    }
}
