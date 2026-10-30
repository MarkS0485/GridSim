using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Source-level contract checks for the S10 CLI changes (October 2026), in the style of the other Cli*ContractTests:
/// they read Program.cs as text, so they hold without running the CLI.
/// </summary>
public class CliS10ContractTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Program() => File.ReadAllText(Path.Combine(Root(), "src", "GridSim.Cli", "Program.cs")).Replace("\r\n", "\n");

    [Fact]
    public void F15_acopf_is_labelled_as_a_dc_dispatch_with_a_loss_loop_not_a_full_ac_opf()
    {
        string src = Program();
        // The old header promised a loss-aware AC-OPF "by successive linearisation"; it is a DC dispatch in a loss loop.
        Assert.DoesNotContain("loss-aware dispatch by successive linearisation", src, StringComparison.Ordinal);
        Assert.Contains("ACOPF (DC dispatch + AC loss loop) - {m.Name} ({m.BusCount} buses)  [not a full AC-OPF: no voltage, reactive-power or AC branch-flow limits]", src, StringComparison.Ordinal);
        // --help says so too.
        Assert.Contains("acopf is a DC-OPF dispatch with an AC loss loop, not a full AC-OPF", src, StringComparison.Ordinal);
        // And the CLI reference.
        string ch19 = File.ReadAllText(Path.Combine(Root(), "docs", "manual", "0019 CLI reference.md"));
        Assert.Contains("NOT a full AC optimal power flow", ch19, StringComparison.Ordinal);
    }

    [Fact]
    public void F17_bus_vlimits_is_refused_outside_the_contingency_verbs()
    {
        string src = Program();
        Assert.Contains("\"--bus-vlimits\",", src, StringComparison.Ordinal);
        // Moved 2026-10 (D-DOC-118, docs-session finding, Ruling R1: Mark delegated 2026-10-04): nminus1-sweep now takes the
        // voltage-limit flags too (it judged the band only, so it disagreed with nminus1 on per-bus cases). Before: "error:
        // --bus-vlimits applies to nminus1, nminus2 and n-1-1 only". After: the message names nminus1-sweep as well; the
        // flag is still refused on every other verb.
        Assert.Contains("error: --bus-vlimits applies to nminus1, nminus2, n-1-1 and nminus1-sweep only", src, StringComparison.Ordinal);
    }
}
