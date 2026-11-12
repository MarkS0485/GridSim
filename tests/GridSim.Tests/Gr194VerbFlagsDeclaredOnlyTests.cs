using GridSim.Cli;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-194: the per-verb flag gate took every --flag written anywhere in a verb's section of the CLI reference (manual
/// ch. 19), prose included, so a flag named in passing was accepted for a verb that never reads it and silently ignored
/// ("validate --rms": the validate prose says "dynamic data for `stability --rms --dyn-data`"; "trip --prices": the
/// global switches prose shows "gridsim --cpu dcopf case9 --prices"). A verb's flags are now those DECLARED in a usage
/// line or in the first column of a flag table. Pinned: prose-only flags are refused; declared flags (usage line, table
/// first cell, global switch table, a mode's table) are still accepted; the line rule itself.
/// </summary>
public sealed class Gr194VerbFlagsDeclaredOnlyTests
{
    private static string Chapter() =>
        File.ReadAllText(Path.Combine(CliProcess.Root(), "docs", "manual", "0019 CLI reference.md")).Replace("\r\n", "\n");

    private static string? Check(params string[] rest) =>
        VerbFlags.Check(Chapter(), rest, rest.Length > 0 && CliOutput.WriterVerbs.Contains(rest[0]));

    [Theory]
    [InlineData("validate", "--rms")]              // named in validate's prose ("stability --rms --dyn-data")
    [InlineData("validate", "--dyn-data")]
    [InlineData("trip", "--rms")]                  // trip's --dyn-data row mentions "stability --rms"
    [InlineData("modal", "--rms")]
    [InlineData("fault", "--fault-bus")]           // fault's prose cites "protection-check --fault-bus"
    [InlineData("acopf-full", "--costs")]          // acopf-full's prose cites "dcopf --costs"
    [InlineData("fleet", "--corners")]             // the evidence group's prose cites "evidence --corners"
    [InlineData("replay-event", "--device-ffr")]
    [InlineData("screen", "--switched-shunts")]    // only in the global table's --shunt-control meaning cell
    [InlineData("screen", "--licence")]            // only in the --importLicence meaning cell (gda's DNO selector)
    public void Gr194_a_flag_named_only_in_prose_is_refused(string verb, string flag)
    {
        string? err = Check(verb, flag, "x");
        Assert.NotNull(err);
        Assert.StartsWith($"error: {verb} does not take {flag} ", err, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("stability", "--rms")]             // stability's own usage line and table
    [InlineData("trip", "--dyn-data")]             // trip's table
    [InlineData("gda", "--dense")]                 // gda's table (row added with GR-194: the code reads it)
    [InlineData("gda", "--sparse")]
    [InlineData("gda", "--licence")]
    [InlineData("nminus2", "--bus-vlimits")]       // the no-op alias row
    [InlineData("pf", "--switched-shunts")]
    [InlineData("trip", "--inertia-model")]        // a global switch's table row
    [InlineData("bench", "--note")]                // a mode's (bench --perf) table
    public void Gr194_a_declared_flag_is_still_accepted(string verb, string flag)
        => Assert.Null(Check(verb, flag, "x"));

    [Theory]
    [InlineData("`gridsim trip [<case>] [--export <csv>]`", "`gridsim trip [<case>] [--export <csv>]`")]
    [InlineData("  `gridsim pmu-ingest --live tcp://h:1`", "`gridsim pmu-ingest --live tcp://h:1`")]
    [InlineData("| `--top` | 12 | Rows to print; see `--csv`. |", " `--top` ")]
    [InlineData("| `--inertia-model lumped\\|distributed` | `lumped` | x |", " `--inertia-model lumped\\")]
    [InlineData("The file `stability --rms` reads.", "")]
    [InlineData("|", "")]
    [InlineData("", "")]
    public void Gr194_only_usage_lines_and_table_first_cells_declare_flags(string line, string declared)
        => Assert.Equal(declared, VerbFlags.Declared(line));
}
