using System;
using System.IO;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N223 (CF3, October 2026): process-level tests (the built CLI, a real process) for the DOC-1 failure paths that no
/// bundled input reaches, with the input built in the test:
/// <list type="bullet">
/// <item>D-DOC-013: <c>fault</c> exits 3 when the converter current iteration does not converge. The case is a weak
///   radial feeder whose converter is huge against it (100,000 MVA behind a 0.5 pu line on a 100 MVA base): its
///   fast-fault-current loop gain (K = 2 times its rated current times the 0.5 pu path) is about 1,000, so even the
///   iteration's smallest damping (0.02) cannot settle it and it chatters between its current limits for all 1,000
///   iterations.</item>
/// <item>D-DOC-006: <c>chain --bess ... --policy lp</c> exits 3 when the storage schedule LP has no optimal schedule.
///   A battery that starts empty (state of charge 0, below LFP's 5 % floor) and can charge at 1 MW into 1,000 MWh cannot
///   reach its floor within two steps, so the LP is infeasible.</item>
/// </list>
/// D-DOC-011 (gpucheck FAIL) still needs a GPU that fails a probe; its exit rule is the FailureTally that CliExitTests
/// drives directly, and on a machine without a GPU the process exits 0 (D011 test in CliDoc1CrashExitCodeProcessTests).
/// </summary>
public sealed partial class Cf3OpenCodeFindingsTests
{
    private static string N223TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "gridsim-cf3-n223-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void N223Delete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A v1 JSON case: slack machine at bus 1, a stiff 132 kV line 1-2, a weak line 2-3, and a 100,000 MVA solar
    /// (converter) plant at bus 3 producing nothing (capacity factor 0).</summary>
    private static string N223WeakConverterCase()
    {
        string dir = N223TempDir();
        File.WriteAllText(Path.Combine(dir, "system.json"), "{ \"name\": \"n223-weak-converter\", \"baseMva\": 100, \"frequencyHz\": 50 }");
        File.WriteAllText(Path.Combine(dir, "buses.json"),
            "[ { \"id\": 1, \"baseKv\": 132 }, { \"id\": 2, \"baseKv\": 132 }, { \"id\": 3, \"baseKv\": 132 } ]");
        File.WriteAllText(Path.Combine(dir, "generation.json"),
            "[ { \"name\": \"G1\", \"bus\": 1, \"type\": \"gas\", \"capacityMva\": 1000, \"role\": \"slack\" },\n" +
            "  { \"name\": \"Huge PV\", \"bus\": 3, \"type\": \"solar\", \"capacityMva\": 100000, \"capacityFactor\": 0.0 } ]");
        File.WriteAllText(Path.Combine(dir, "transmission.json"),
            "[ { \"name\": \"L12\", \"from\": 1, \"to\": 2, \"rPu\": 0.001, \"xPu\": 0.01 },\n" +
            "  { \"name\": \"L23\", \"from\": 2, \"to\": 3, \"rPu\": 0.0, \"xPu\": 0.5 } ]");
        return dir;
    }

    [Fact]
    public void N223_D013_a_converter_fault_iteration_that_does_not_converge_exits_3()
    {
        string dir = N223WeakConverterCase();
        try
        {
            CliProcess.Result r = CliProcess.RunBuilt("fault", dir, "--bus", "2");
            Assert.True(r.ExitCode == 3, r.ToString());
            Assert.Contains("fault: the converter current iteration of the fault study did not converge; the faulted state is not reliable. Exit 3.",
                r.StdErr, StringComparison.Ordinal);
            // The study is still printed (the result is shown, then refused).
            // Test changed 2026-10-19 (comment only, no assertion moved): since 1.95.304 (commit aed7aa5f) the faulted state
            // of a non-converged converter iteration is marked not converged and the relays are no longer assessed on it.
            Assert.Contains("WARNING: converter current iteration did not converge", r.StdOut, StringComparison.Ordinal);
        }
        finally { N223Delete(dir); }
    }

    [Fact]
    public void N223_D013_protection_check_on_the_same_fault_reports_relays_not_assessed_and_exits_3()
    {
        // 2026-10-19: protection-check --fault-bus on the same non-converging converter iteration used to reach Assess,
        // which refuses a non-converged state, and reported "fault study failed" with exit 1. It now matches `fault`:
        // the study is printed, the relays are not assessed, exit 3.
        string dir = N223WeakConverterCase();
        try
        {
            CliProcess.Result r = CliProcess.RunBuilt("protection-check", dir, "--fault-bus", "2");
            Assert.True(r.ExitCode == 3, r.ToString());
            Assert.Contains("protection-check: the converter current iteration of the fault study did not converge", r.StdErr, StringComparison.Ordinal);
            Assert.Contains("relays: not assessed", r.StdOut, StringComparison.Ordinal);
            Assert.DoesNotContain("fault study at bus 2 failed", r.StdErr, StringComparison.Ordinal);
        }
        finally { N223Delete(dir); }
    }

    [Fact]
    public void N223_D013_the_same_feeder_with_a_normal_converter_still_exits_0()
    {
        // The control: a 50 MVA plant on the same feeder (loop gain about 0.5) settles, so the exit 3 above is the
        // iteration, not the case.
        string dir = N223WeakConverterCase();
        try
        {
            File.WriteAllText(Path.Combine(dir, "generation.json"),
                "[ { \"name\": \"G1\", \"bus\": 1, \"type\": \"gas\", \"capacityMva\": 1000, \"role\": \"slack\" },\n" +
                "  { \"name\": \"PV\", \"bus\": 3, \"type\": \"solar\", \"capacityMva\": 50, \"capacityFactor\": 0.0 } ]");
            CliProcess.Result r = CliProcess.RunBuilt("fault", dir, "--bus", "2");
            Assert.True(r.ExitCode == 0, r.ToString());
            Assert.DoesNotContain("did not converge", r.StdOut + r.StdErr, StringComparison.Ordinal);
        }
        finally { N223Delete(dir); }
    }

    [Fact]
    public void N223_D006_an_infeasible_storage_schedule_lp_exits_3()
    {
        string dir = N223TempDir();
        try
        {
            CliProcess.Result r = CliProcess.RunBuilt("chain", "case9", "--steps", "2", "--bess", "lfp:5:1:1000:0",
                "--prices", "synthetic", "--policy", "lp", "--out", Path.Combine(dir, "c.md"));
            Assert.True(r.ExitCode == 3, r.ToString());
            Assert.Contains("error: chain storage: --policy lp: the storage schedule LP did not solve (status Infeasible); no schedule to run.",
                r.StdErr, StringComparison.Ordinal);
        }
        finally { N223Delete(dir); }
    }
}
