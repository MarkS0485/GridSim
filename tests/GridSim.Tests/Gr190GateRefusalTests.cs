using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-190 (November 2026): the loader gate (the licence's bus limit, an ambiguous MATPOWER name) used to call
/// Environment.Exit(2) from inside the loader, so a batch verb could not record the refused case and run the rest: the
/// whole batch ended with exit 2. The refusal is now a GateRefusedException. A batch verb records it as that case's LOAD
/// FAIL row and runs the others; a single-case run still ends with "error: &lt;message&gt;" and exit 2 (pinned by
/// N216BundledPathAnchorTests and Lf5RemainingTests.N229 for dcpf, and below for the batch verbs run on one case).
/// </summary>
public sealed class Gr190GateRefusalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-gr190-" + Guid.NewGuid().ToString("N"));

    public Gr190GateRefusalTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    // The free tier whatever licence the machine holds (as N216BundledPathAnchorTests): a key that does not verify
    // resolves to the free Personal tier (1,500 buses).
    private Dictionary<string, string?> FreeTier() => new()
    {
        ["GRIDSIM_LICENCE"] = "not-a-key",
        ["XDG_DATA_HOME"] = _dir,
    };

    /// <summary>The shipped 2,869-bus PEGASE case copied outside the data folder: not bundled, so the free tier refuses it.</summary>
    private string LicensedCopy()
    {
        string shipped = Path.Combine(CliProcess.Root(), "data", "matpower", "pegase", "case2869pegase.m");
        Assert.True(File.Exists(shipped), shipped);
        string copy = Path.Combine(_dir, "big2869.m");
        File.Copy(shipped, copy);
        return copy;
    }

    [Fact]
    public void Bench_records_a_refused_case_as_a_load_fail_row_and_benches_the_rest()
    {
        string md = Path.Combine(_dir, "bench.md");
        CliProcess.Result r = CliProcess.RunBuiltWithEnvironment(FreeTier(),
            "bench", "--cases", "case9," + LicensedCopy(), "--runs", "1", "--parallel", "1", "--out", md);
        // Before: the gate ended the whole run with exit 2 at the refused case. Now the refused case is a LOAD FAIL row,
        // case9 is still benched, and the run exits 1 for the load failure (the bench's rule for a case that does not load).
        Assert.True(r.ExitCode == 1, r.ToString());
        Assert.Contains("LOAD FAIL", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("2,869", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("| case9 | 9 |", r.StdOut, StringComparison.Ordinal);
        Assert.True(File.Exists(md), "the report is still written");
    }

    [Fact]
    public void A_single_case_bench_still_ends_with_the_gate_refusal_and_exit_2()
    {
        CliProcess.Result r = CliProcess.RunBuiltWithEnvironment(FreeTier(),
            "bench", "--cases", LicensedCopy(), "--runs", "1", "--parallel", "1", "--out", Path.Combine(_dir, "bench.md"));
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("error: ", r.StdErr, StringComparison.Ordinal);
        Assert.Contains("2,869", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_case_verb_ends_with_the_gate_refusal_and_exit_2_as_before()
    {
        CliProcess.Result r = CliProcess.RunBuiltWithEnvironment(FreeTier(), "fault", LicensedCopy(), "--bus", "1");
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("error: ", r.StdErr, StringComparison.Ordinal);
        Assert.Contains("2,869", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", r.StdErr, StringComparison.Ordinal);
    }
}
