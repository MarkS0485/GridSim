using GridSim.Cli;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-195: a global switch given to a verb that does not read it was accepted and silently ignored ("gridsim pf case9
/// --once" ran pf as if --once were absent). The owner's decision is a stderr warning, not a refusal. Pinned on the pure
/// table (src/GridSim.Cli/GlobalSwitchUse.cs, linked into this project): a non-reading verb gets one warning per switch,
/// naming the verbs that do read it; a reading verb gets none; a verb that refuses the switch itself (or already warns)
/// gets none from here; switches outside the table give none; the order is fixed. One process test pins the stderr line
/// and that the run still succeeds. The Program.cs wiring is pinned at source level.
/// </summary>
public sealed class Gr195GlobalSwitchWarningTests
{
    [Fact]
    public void Gr195_a_verb_that_does_not_read_a_switch_is_warned_once_per_switch()
    {
        IReadOnlyList<string> w = GlobalSwitchUse.Warnings("pf", new[] { "--once", "--bfs", "--forming" });
        Assert.Equal(new[]
        {
            "warning: pf does not read --forming, so it has no effect on this run (it applies to trip, transient, scenario, fleet).",
            "warning: pf does not read --once, so it has no effect on this run (only the case run with no verb reads it).",
            "warning: pf does not read --bfs, so it has no effect on this run (it applies to harmonics, qsts, hosting (and the case run with no verb)).",
        }, w);
    }

    [Theory]
    [InlineData("trip", "--inertia-model")]
    [InlineData("transient", "--asset-dynamics")]
    [InlineData("scenario", "--forming")]
    [InlineData("fleet", "--forming-inertia")]
    [InlineData("interpose", "--ticks")]
    [InlineData("qsts", "--bfs")]
    [InlineData("harmonics", "--enableExport")]
    [InlineData("impedance-stability", "--enableExport")]
    [InlineData("TRIP", "--forming-damping")]      // verbs match without regard to case, as the dispatch does
    public void Gr195_a_verb_that_reads_the_switch_is_not_warned(string verb, string sw)
        => Assert.Empty(GlobalSwitchUse.Warnings(verb, new[] { sw }));

    [Theory]
    [InlineData("evidence", "--asset-dynamics")]    // refused by the event verbs (RefuseIgnoredGlobalSwitches)
    [InlineData("replay-event", "--forming")]
    [InlineData("h0-study", "--inertia-model")]     // the event verbs print their own distributed-model warning
    [InlineData("fit-frequency", "--forming-damping")]
    public void Gr195_a_verb_that_deals_with_the_switch_itself_gets_no_second_message(string verb, string sw)
        => Assert.Empty(GlobalSwitchUse.Warnings(verb, new[] { sw }));

    [Theory]
    [InlineData("--gpu")]
    [InlineData("--cpu")]
    [InlineData("--frequency-hz")]
    [InlineData("--audit-log")]
    [InlineData("--export-tight")]
    public void Gr195_switches_outside_the_table_give_no_warning(string sw)
    {
        Assert.Empty(GlobalSwitchUse.Warnings("pf", new[] { sw }));
        Assert.Null(GlobalSwitchUse.ReadersOf(sw));
    }

    [Fact]
    public void Gr195_the_table_covers_the_switches_the_wiring_reports()
    {
        Assert.Equal(new[]
        {
            "--inertia-model", "--asset-dynamics", "--forming", "--forming-inertia", "--forming-damping",
            "--once", "--ticks", "--bfs", "--enableExport", "--export-jacobian",
        }, GlobalSwitchUse.CoveredSwitches);

        // Program.cs reports exactly these switches to the table, after the per-verb flag gate.
        string src = File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Cli", "Program.cs")).Replace("\r\n", "\n");
        int gate = src.IndexOf("string? flagError = VerbFlags.Check(", StringComparison.Ordinal);
        int wire = src.IndexOf("GlobalSwitchUse.Warnings(rest[0], givenSwitches)", StringComparison.Ordinal);
        Assert.True(gate > 0 && wire > gate, "the GR-195 warning must run after the per-verb flag gate");
        foreach (string sw in GlobalSwitchUse.CoveredSwitches)
            Assert.Contains($"givenSwitches.Add(\"{sw}\");", src, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr195_pf_with_once_warns_and_still_succeeds()
    {
        CliProcess.Result r = CliProcess.RunBuilt("pf", "case9", "--once");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("warning: pf does not read --once, so it has no effect on this run", r.StdErr, StringComparison.Ordinal);
    }
}
