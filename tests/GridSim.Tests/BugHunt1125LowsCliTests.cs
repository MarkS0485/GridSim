using System.Text.Json;
using GridSim.Cli;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Bug hunt batch 5, CLI lows (CLI-11 to CLI-21) and the REVIEW-B6 nits folded in with them (R6-03). Each test is the
/// smallest command line that reached the finding.
/// </summary>
public sealed class BugHunt1125LowsCliTests : IDisposable
{
    private readonly string _dir = JournalTestSupport.NewDir("bh1125-lows-cli");

    public void Dispose() => JournalTestSupport.Delete(_dir);

    private CliProcess.Result Run(params string[] args) => JournalTestSupport.Run(_dir, args);

    private static void Usage(CliProcess.Result r, string message)
    {
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains(message, r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("internal error", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli11_case9_with_ticks_runs_the_live_loop()
    {
        CliProcess.Result once = Run("case9", "--once");
        CliProcess.Result ticks = Run("case9", "--ticks", "2");
        Assert.True(ticks.ExitCode == 0, ticks.ToString());
        // The detailed report is what the bare run prints; --ticks prints the live loop's tick lines instead.
        Assert.NotEqual(once.StdOut, ticks.StdOut);
    }

    [Fact]
    public void Cli12_bench_estimate_refuses_a_run_count_below_1_and_the_flags_it_does_not_read()
    {
        Usage(Run("bench", "--estimate", "--runs", "0"), "--runs expected a whole number, 1 or more");
        Usage(Run("bench", "--estimate", "--out", "be2"), "bench --estimate does not use --out");
        Assert.False(Directory.Exists(Path.Combine(_dir, "results", "bench-estimate")));
    }

    [Fact]
    public void Cli13_batchsolve_refuses_counts_below_1()
    {
        Usage(Run("batchsolve", "--count", "-1"), "--count expected a whole number, 1 or more");
        Usage(Run("batchsolve", "--n", "0"), "--n expected a whole number, 1 or more");
        Usage(Run("batchsolve", "--runs", "0"), "--runs expected a whole number, 1 or more");
    }

    [Fact]
    public void Cli14_estimate_synth_and_transient_check_their_ranges_before_any_work()
    {
        Usage(Run("estimate", "case9", "--synth", "--noise", "-1"), "--noise expected a number that is finite and 0 or more");
        Usage(Run("estimate", "case9", "--synth", "--coverage-vm", "5"), "--coverage-vm expected a fraction from 0 to 1");
        Usage(Run("estimate", "case9", "--synth", "--coverage-flow", "-0.5"), "--coverage-flow expected a fraction from 0 to 1");
        Usage(Run("transient", "--trip", "100", "--trip-at", "-5"), "--trip-at expected a number that is finite and 0 or more");
        Assert.False(Directory.Exists(Path.Combine(_dir, "results")) && Directory.EnumerateFileSystemEntries(Path.Combine(_dir, "results"), "estimate*").Any());
    }

    [Fact]
    public void Cli15_trip_takes_a_bus_number_on_a_case_with_unnamed_generators()
    {
        CliProcess.Result bad = Run("trip", "case9", "99");
        Usage(bad, "Its generators have no names, so give the bus number of one: 1, 2, 3.");
        CliProcess.Result ok = Run("trip", "case9", "2");
        Assert.True(ok.ExitCode is 0 or 3, ok.ToString());
        Assert.DoesNotContain("matched no generator", ok.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli16_an_existing_boundary_csv_is_refused_with_the_never_overwrite_message()
    {
        // Not a bug at this version: the verb's own guard runs first and names the rule. Pinned so it stays so.
        string csv = Path.Combine(_dir, "b.csv");
        File.WriteAllText(csv, "x\n");
        string study = Path.Combine(_dir, "s.json");
        File.WriteAllText(study, "{}");
        CliProcess.Result r = Run("cosim", study, "--boundary-csv", csv);
        Usage(r, "already exists; name a new file (it is never overwritten)");
        Assert.DoesNotContain("an input of this run", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli17_a_body_that_throws_leaves_no_file_and_the_name_stays_free()
    {
        string target = Path.Combine(_dir, "wp27", "r.json");
        Assert.Throws<ArgumentException>(() => Wp27CliSupport.WriteJson(target, w => { w.WriteString("a", "b"); w.WriteNumber("bad", double.NaN); }));
        Assert.False(File.Exists(target));
        Assert.False(Directory.Exists(Path.Combine(_dir, "wp27")) && Directory.EnumerateFiles(Path.Combine(_dir, "wp27")).Any());
        Wp27CliSupport.WriteJson(target, w => w.WriteNumber("good", 1.5));
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(target));
        Assert.Equal(1.5, doc.RootElement.GetProperty("good").GetDouble());
        Assert.Throws<IOException>(() => Wp27CliSupport.WriteJson(target, w => w.WriteNumber("again", 2)));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_dir, "wp27")));
    }

    [Fact]
    public void Cli19_the_help_banner_no_longer_points_at_program_cs()
    {
        CliProcess.Result r = Run("--help");
        Assert.DoesNotContain("Program.cs", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("0019 CLI reference.md", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli21_a_result_flag_naming_a_file_the_csv_writer_derives_is_refused()
    {
        var requests = new List<(string, string)>();
        string? err = CliOutput.Parse(new List<string> { "dcpf", "case9", "--csv", Path.Combine(_dir, "a.csv"), "--out", Path.Combine(_dir, "a-summary.csv") }, requests);
        Assert.NotNull(err);
        Assert.Contains("the CSV result", err, StringComparison.Ordinal);
        requests.Clear();
        Assert.Null(CliOutput.Parse(new List<string> { "dcpf", "case9", "--csv", Path.Combine(_dir, "a.csv"), "--json", Path.Combine(_dir, "a.json") }, requests));
    }

    [Fact]
    public void R6_03_token_files_of_the_2_1_160_shape_are_recognised_for_this_machine_only()
    {
        Assert.Equal(4242, RpcTokenFiles.MidShapePid("rpc-node1-4242-0a1b2c3d.token", "Node1"));
        Assert.Null(RpcTokenFiles.MidShapePid("rpc-node2-4242-0a1b2c3d.token", "Node1"));
        // Today's shape (with the hash) is not taken for the older one.
        string today = RpcTokenFiles.Name(RpcTokenFiles.HostName("Node1"), 4242, "0a1b2c3d");
        Assert.Null(RpcTokenFiles.MidShapePid(today, "Node1"));
        Assert.Equal(4242, RpcTokenFiles.Pid(today, RpcTokenFiles.HostName("Node1")));
    }
}
