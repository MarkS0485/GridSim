using Xunit;

namespace GridSim.Tests;

/// <summary>
/// S4 follow-ups (October 2026): end-to-end checks on the real gridsim binary for the CLI items of the "S4 EMT and
/// follow-ups" stream (N-ids from review-progress/2026-10/NEW-FINDINGS.md). Each test names its finding.
/// </summary>
public sealed class CliS4FollowUpsProcessTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-s4-followups-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- N140: nminus1-sweep islanded vs not solved -----------------------------------------------------------------

    /// <summary>
    /// N140: case14's 1-2 outage does not solve but does not split the network (N107: NO SOLUTION in nminus1). The
    /// sweep used to count it as islanded (CSV islanded 1); it is now 0 islanded, 1 not solved, with not_solved as the
    /// last CSV column and every earlier column where it was.
    /// </summary>
    [Fact]
    public void N140_sweep_splits_islanded_from_not_solved()
    {
        string dir = TempDir();
        try
        {
            string md = Path.Combine(dir, "sweep.md");
            CliProcess.Result r = CliProcess.RunBuilt("nminus1-sweep", "case14", "--parallel", "1", "--out", md);
            Assert.True(r.ExitCode == 3, r.ToString());   // case14 is not N-1 secure (unchanged)
            Assert.Contains("| Case | Buses | Branches | Screened | Secure | Violations | Islanded | Not solved | Solves/s | Wall s | Base viol |", r.StdOut, StringComparison.Ordinal);
            Assert.Contains("| case14 | 14 | 20 | 20 | 16 | 3 | 0 | 1 |", r.StdOut, StringComparison.Ordinal);

            string[] csv = File.ReadAllLines(Path.ChangeExtension(md, ".csv"));
            string header = csv.Single(l => l.StartsWith("case,", StringComparison.Ordinal));
            Assert.Equal("case,buses,branches,screened,secure,violations,islanded,solves_per_s,wall_s,sampled,commit,base_violations,not_solved", header);
            string[] row = csv.Single(l => l.StartsWith("case14,", StringComparison.Ordinal)).Split(',');
            Assert.Equal(13, row.Length);
            Assert.Equal(new[] { "case14", "14", "20", "20", "16", "3", "0" }, row[..7]);   // islanded: was 1
            Assert.Equal("1", row[11]);    // base_violations, unchanged position
            Assert.Equal("1", row[12]);    // not_solved, new
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// <summary>N140: case9's islanding outages (1-4, 3-6, 8-2 split off a generator bus) stay islanded.</summary>
    [Fact]
    public void N140_a_real_split_is_still_islanded()
    {
        string dir = TempDir();
        try
        {
            string md = Path.Combine(dir, "sweep.md");
            CliProcess.Result r = CliProcess.RunBuilt("nminus1-sweep", "case9", "--parallel", "1", "--out", md);
            Assert.True(r.ExitCode == 3, r.ToString());
            string[] row = File.ReadAllLines(Path.ChangeExtension(md, ".csv")).Single(l => l.StartsWith("case9,", StringComparison.Ordinal)).Split(',');
            Assert.Equal("3", row[6]);     // islanded
            Assert.Equal("0", row[12]);    // not_solved
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    // ---- N093: MATPOWER parse notes on every verb -------------------------------------------------------------------

    /// <summary>
    /// N093: a verb other than convert that loads a .m file used to discard the parse notes. pf on the bundled
    /// case14.m now prints them on stderr (here the assumed nominal frequency, which every MATPOWER file needs) and
    /// leaves stdout as it was (no note lines in it).
    /// </summary>
    [Fact]
    public void N093_pf_on_a_matpower_file_prints_the_parse_notes_on_stderr()
    {
        string m = Path.Combine(AppContext.BaseDirectory, "data", "matpower", "lib", "case14.m");
        Assert.True(File.Exists(m), m);
        CliProcess.Result r = CliProcess.RunBuilt("pf", m);
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("note: case14.m: nominal frequency not given (MATPOWER files carry none); assumed 50 Hz", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("note:", r.StdOut, StringComparison.Ordinal);
    }

    /// <summary>N093: a JSON case folder has no MATPOWER parse notes, so nothing is added.</summary>
    [Fact]
    public void N093_a_json_case_prints_no_parse_notes()
    {
        CliProcess.Result r = CliProcess.RunBuilt("pf", "uk");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.DoesNotContain("note: ", r.StdErr, StringComparison.Ordinal);
    }

    // ---- N121: acopf --prices -------------------------------------------------------------------------------------

    /// <summary>N121: acopf reports the final DC pass's nodal and line prices with --prices (it was refused), labelled as
    /// lossless DC prices at the loss-inflated demand.</summary>
    [Fact]
    public void N121_acopf_with_costs_reports_the_final_dc_pass_prices()
    {
        string m = Path.Combine(AppContext.BaseDirectory, "data", "matpower", "nesta", "pglib_opf_case5_pjm.m");
        CliProcess.Result plain = CliProcess.RunBuilt("acopf", m, "--costs", "case");
        CliProcess.Result r = CliProcess.RunBuilt("acopf", m, "--costs", "case", "--prices");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("nodal prices of the final DC pass (LMP, cost units/MWh, lossless DC at the loss-inflated demand; no marginal-loss component): 5 bus(es)", r.StdOut, StringComparison.Ordinal);
        // The dispatch lines are the same with and without the flag (asking for prices does not move the dispatch).
        Assert.StartsWith(plain.StdOut.TrimEnd(), r.StdOut, StringComparison.Ordinal);
    }

    /// <summary>N121: --prices is still refused on a verb that has no prices, now naming both verbs that do.</summary>
    [Fact]
    public void N121_prices_is_still_refused_elsewhere()
    {
        CliProcess.Result r = CliProcess.RunBuilt("pf", "case9", "--prices");
        Assert.Equal(2, r.ExitCode);
        // Moved 2026-10 (D-DOC-049, docs-session finding, Ruling R1: Mark delegated 2026-10-04; main ruled that the --prices
        // gate no longer blocks chain's price policies): chain with --bess now takes --prices. Before: "error: --prices
        // applies to dcopf and acopf only". After: "error: --prices applies to dcopf, acopf and chain (with --bess) only";
        // pf still exits 2.
        Assert.Contains("error: --prices applies to dcopf, acopf and chain (with --bess) only", r.StdErr, StringComparison.Ordinal);
    }
}
