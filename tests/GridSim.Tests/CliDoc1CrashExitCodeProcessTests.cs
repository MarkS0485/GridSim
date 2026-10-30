using System.Globalization;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// DOC-1 (October 2026): the CLI crash and exit-code net, docs-session findings D-DOC-001..015, 053, 085, 130 and 159
/// (review-progress/2026-10/DOCS-FINDINGS-TRIAGE.md, stream 1). Every test runs the real gridsim binary as a separate
/// process (CliProcess.RunBuilt) and checks the two things a script sees: the exit code the CLI reference (user manual
/// ch. 19) documents, and the message on stderr that names what is wrong. Every run that used to crash is also checked
/// for the crash reporter's "internal error" line, which must never appear for bad input.
///
/// Exit codes (ch. 19): 0 ok, 1 input error (missing or unloadable input, invalid data), 2 usage error (something on
/// the command line names nothing that exists, or a forward-inference rejection), 3 verdict or convergence failure.
/// </summary>
public sealed class CliDoc1CrashExitCodeProcessTests
{
    /// <summary>The crash reporter's first line (CrashReport.Write): it means a defect, never bad input.</summary>
    private const string InternalError = "internal error";

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-doc1-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Delete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Assert the exit code, the full result in the failure message so a red test shows what ran.</summary>
    private static void AssertExit(int expected, CliProcess.Result r)
    {
        Assert.True(r.ExitCode == expected, $"expected exit {expected}\n{r}");
        Assert.DoesNotContain(InternalError, r.StdErr, StringComparison.Ordinal);
    }

    /// <summary>
    /// A NESO-format frequency CSV (header "dtm,f") whose rows are stamped NOW, so every row is newer than the 169 h
    /// forward-inference horizon and the reader must refuse it.
    /// </summary>
    private static string FreshFrequencyCsv(string dir)
    {
        string path = Path.Combine(dir, "fresh.csv");
        DateTime now = DateTime.UtcNow;
        var lines = new List<string> { "dtm,f" };
        for (int s = 0; s < 5; s++)
            lines.Add(now.AddSeconds(s).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + ",50.0" + s.ToString(CultureInfo.InvariantCulture));
        File.WriteAllLines(path, lines);
        return path;
    }

    /// <summary>The shipped MATPOWER case9 file with one line edited (exact text match, so a changed file fails loudly).</summary>
    private static string EditedCase9(string dir, string name, params (string From, string To)[] edits)
    {
        string text = File.ReadAllText(Path.Combine(CliProcess.Root(), "data", "matpower", "lib", "case9.m"));
        foreach ((string from, string to) in edits)
        {
            Assert.True(text.Contains(from, StringComparison.Ordinal), $"case9.m no longer holds '{from}'");
            text = text.Replace(from, to, StringComparison.Ordinal);
        }
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    // ---- D-DOC-001: freq / replay on a fresh CSV ----------------------------------------------------------------------

    /// <summary>D-DOC-001: freq on a CSV newer than the horizon was an unhandled ForwardInferenceException (exit 127 on
    /// main 1.95.185); it is now REJECTED and exit 2, as every other event verb reports it.</summary>
    [Fact]
    public void D001_freq_on_a_fresh_csv_is_rejected_with_exit_2()
    {
        string dir = TempDir();
        try
        {
            CliProcess.Result r = CliProcess.RunBuilt("freq", FreshFrequencyCsv(dir));
            AssertExit(2, r);
            Assert.Contains("REJECTED (forward inference):", r.StdErr, StringComparison.Ordinal);
            Assert.Contains("newer than the historical horizon", r.StdErr, StringComparison.Ordinal);
        }
        finally { Delete(dir); }
    }

    /// <summary>D-DOC-001: replay, the same reader: REJECTED and exit 2 (was exit 127).</summary>
    [Fact]
    public void D001_replay_on_a_fresh_csv_is_rejected_with_exit_2()
    {
        string dir = TempDir();
        try
        {
            CliProcess.Result r = CliProcess.RunBuilt("replay", FreshFrequencyCsv(dir), "--frames", "2");
            AssertExit(2, r);
            Assert.Contains("REJECTED (forward inference):", r.StdErr, StringComparison.Ordinal);
        }
        finally { Delete(dir); }
    }

    // ---- D-DOC-002: replay-event --trace with a fresh trace --------------------------------------------------------------

    /// <summary>D-DOC-002: ReplayValidator.Validate read the attached trace outside any handler (exit 127). Now REJECTED,
    /// exit 2, before the output folder is created.</summary>
    [Fact]
    public void D002_replay_event_with_a_fresh_trace_is_rejected_before_any_output()
    {
        string dir = TempDir();
        try
        {
            string outDir = Path.Combine(dir, "out");
            CliProcess.Result r = CliProcess.RunBuilt("replay-event", "gb-2019-08-09", "--trace", FreshFrequencyCsv(dir), "--out", outDir);
            AssertExit(2, r);
            Assert.Contains("REJECTED (forward inference):", r.StdErr, StringComparison.Ordinal);
            Assert.False(Directory.Exists(outDir), "no output folder may be written for a refused run");
        }
        finally { Delete(dir); }
    }

    // ---- D-DOC-003: stability --fault-bus not in the case ----------------------------------------------------------------

    /// <summary>D-DOC-003: a fault bus that is not in the case crashed with KeyNotFoundException (exit 127). It is now a
    /// usage error, exit 2, naming the bus and the case, on the classical and the RMS engine.</summary>
    [Theory]
    [InlineData("stability", "case9", "--fault-bus", "99")]
    [InlineData("stability", "case9", "--rms", "--fault-bus", "99")]
    public void D003_stability_unknown_fault_bus_exits_2_naming_it(params string[] args)
    {
        CliProcess.Result r = CliProcess.RunBuilt(args);
        AssertExit(2, r);
        Assert.Contains("error: stability: fault bus 99 is not in case 'IEEE 9-bus (WSCC)'.", r.StdErr, StringComparison.Ordinal);
    }

    /// <summary>D-DOC-003: a fault bus that IS in the case still runs (exit 0, the CCT line).</summary>
    [Fact]
    public void D003_stability_known_fault_bus_still_runs()
    {
        CliProcess.Result r = CliProcess.RunBuilt("stability", "case9", "--fault-bus", "7");
        AssertExit(0, r);
        Assert.Contains("Critical clearing time for a 3-phase fault at bus 7:", r.StdOut, StringComparison.Ordinal);
    }

    // ---- D-DOC-004: dcopf / acopf with Pmin above Pmax ---------------------------------------------------------------------

    /// <summary>D-DOC-004: Pmin above Pmax is invalid case data. dcopf without --costs, and acopf without --costs (with
    /// or without --prices), did not catch the solver's InvalidDataException (exit 127). Now exit 1 naming the unit.</summary>
    [Theory]
    [InlineData("dcopf", "[dcopf]")]
    [InlineData("acopf", "[acopf]")]
    [InlineData("acopf", "[acopf]", "--prices")]
    public void D004_opf_with_pmin_above_pmax_exits_1_naming_the_unit(string verb, string prefix, params string[] extra)
    {
        string dir = TempDir();
        try
        {
            // case9 generator 2 (bus 2, Pmax 300 MW): Pmin 10 -> 400 MW.
            string m = EditedCase9(dir, "pmin-above-pmax.m",
                ("\t2\t163\t6.54\t300\t-300\t1.025\t100\t1\t300\t10\t", "\t2\t163\t6.54\t300\t-300\t1.025\t100\t1\t300\t400\t"));
            CliProcess.Result r = CliProcess.RunBuilt(new[] { verb, m }.Concat(extra).ToArray());
            AssertExit(1, r);
            // Test changed 2026-10-19: since 1.95.328 (GR-651) the MATPOWER parser itself refuses PMIN above PMAX when
            // the case is loaded, so the refusal now comes from the case load (still exit 1, still naming the unit by
            // its mpc.gen row and both limits) before the verb's own DC-OPF check (prefix + " DC-OPF: generator 1 ...")
            // is reached. That check is still in place for models built other ways. The prefix is no longer printed.
            _ = prefix;
            Assert.Contains("error loading case '", r.StdErr, StringComparison.Ordinal);
            Assert.Contains("mpc.gen row 2 has QMIN above QMAX or PMIN above PMAX (Q -300 / 300, P 400 / 300).", r.StdErr, StringComparison.Ordinal);
        }
        finally { Delete(dir); }
    }

    // ---- D-DOC-005: gda-replay --bess on an unknown bus ----------------------------------------------------------------------

    /// <summary>D-DOC-005: the site's bus was checked only inside the replay loop, outside any handler (exit 127). Now a
    /// usage error, exit 2, with chain's message.</summary>
    [Fact]
    public void D005_gda_replay_bess_on_an_unknown_bus_exits_2()
    {
        string dir = TempDir();
        try
        {
            CliProcess.Result r = CliProcess.RunBuilt("gda-replay", "--synth", "--case", "case9", "--steps", "2",
                "--bess", "lfp:999:10:20", "--out", Path.Combine(dir, "gr"));
            AssertExit(2, r);
            Assert.Contains("error: gda-replay storage: storage site 'LFP 10MW/20MWh @999' is at unknown bus 999.", r.StdErr, StringComparison.Ordinal);
        }
        finally { Delete(dir); }
    }

    // ---- D-DOC-006: chain storage LP that does not solve (latent) -------------------------------------------------------

    /// <summary>
    /// D-DOC-006: the chain handler now catches StorageCli.OptimalLpPlan's InvalidOperationException (exit 3). That path
    /// cannot be reached from the command line (the lp policy needs prices, and chain refuses --prices), which is what
    /// this test pins: the run is refused up front with exit 2, so no LP ever runs. The handler itself is reviewed in
    /// the code (see the D-DOC-006 comment in Program.cs) and recorded as such in the DOC-1 notes.
    /// (D-DOC-049, October 2026: chain now takes --prices, so the lp policy is reachable with prices; WITHOUT --prices, as
    /// here, it is still refused with exit 2 and the same message, which is what this test pins.)
    /// </summary>
    [Fact]
    public void D006_chain_policy_lp_is_refused_before_any_lp_runs()
    {
        string dir = TempDir();
        try
        {
            CliProcess.Result r = CliProcess.RunBuilt("chain", "case9", "--steps", "2", "--bess", "lfp:5:10:20", "--policy", "lp",
                "--out", Path.Combine(dir, "c.md"));
            AssertExit(2, r);
            Assert.Contains("error: chain storage: --policy lp needs --prices", r.StdErr, StringComparison.Ordinal);
        }
        finally { Delete(dir); }
    }

    // ---- D-DOC-007 / D-DOC-085: --importLicence values ------------------------------------------------------------------

    /// <summary>
    /// D-DOC-007: "--importLicence=" crashed (ArgumentException, exit 127). D-DOC-085: "--importLicence --once" took the
    /// flag as the file name; "--importLicence" alone was dropped and the live view started. All three are now a usage
    /// error, exit 2, before anything is read or installed, so no test can touch the user's installed licence.
    /// </summary>
    [Theory]
    [InlineData("--importLicence=")]
    [InlineData("--importLicence", "--once")]
    [InlineData("--importLicence")]
    public void D007_D085_importLicence_without_a_file_path_exits_2(params string[] args)
    {
        CliProcess.Result r = CliProcess.RunBuilt(args);
        AssertExit(2, r);
        Assert.Contains("error: --importLicence expected a licence file path (<file.gridsim>) but got '", r.StdErr, StringComparison.Ordinal);
    }

    // ---- D-DOC-009: serve with a window file that is not JSON ------------------------------------------------------------

    /// <summary>D-DOC-009: a non-JSON window crashed serve (JsonReaderException, exit 127). The file is now named,
    /// skipped (as the web feed does), and the run ends with exit 1, an input error.</summary>
    [Fact]
    public void D009_serve_skips_an_unloadable_window_and_exits_1()
    {
        string dir = TempDir();
        try
        {
            string windows = Path.Combine(dir, "windows");
            Directory.CreateDirectory(windows);
            File.WriteAllText(Path.Combine(windows, "w1.json"), "this is not json");
            CliProcess.Result r = CliProcess.RunBuilt("serve", "case9", "--measurements-dir", windows);
            AssertExit(1, r);
            Assert.Contains("error: serve: window 'w1.json' cannot be loaded and is skipped:", r.StdErr, StringComparison.Ordinal);
            Assert.Contains("error: serve: 1 of 1 window file(s) could not be loaded (skipped; named above). Exit 1.", r.StdErr, StringComparison.Ordinal);
            Assert.Contains("done: processed 0, converged 0, rejected-fresh 0, failed 0", r.StdOut, StringComparison.Ordinal);
        }
        finally { Delete(dir); }
    }

    // ---- D-DOC-010: cpf when the base load does not converge -----------------------------------------------------------

    /// <summary>
    /// D-DOC-010: cpf exited 0 with a negative margin when the base load did not converge but a lighter one did. case9's
    /// nose is lambda 2.373046875 (ch. 19), so case9 with every load scaled by 2.5 has no solution at its own base load and
    /// one at about 0.95 of it. The numbers are still printed; the run now exits 3 and says why.
    /// </summary>
    [Fact]
    public void D010_cpf_base_case_not_converging_exits_3()
    {
        string dir = TempDir();
        try
        {
            string m = EditedCase9(dir, "case9-x2p5.m",
                ("\t5\t1\t90\t30\t", "\t5\t1\t225\t75\t"),
                ("\t7\t1\t100\t35\t", "\t7\t1\t250\t87.5\t"),
                ("\t9\t1\t125\t50\t", "\t9\t1\t312.5\t125\t"));
            CliProcess.Result r = CliProcess.RunBuilt("cpf", m);
            AssertExit(3, r);
            Assert.Contains("max loadability lambda = 0.", r.StdOut, StringComparison.Ordinal);
            Assert.Contains("cpf: the base case (lambda = 1) did not converge; only a lighter load did", r.StdErr, StringComparison.Ordinal);
        }
        finally { Delete(dir); }
    }

    /// <summary>D-DOC-010: a case whose base load converges is unchanged (cpf case9: lambda 2.373046875, exit 0).</summary>
    [Fact]
    public void D010_cpf_converging_base_case_still_exits_0()
    {
        CliProcess.Result r = CliProcess.RunBuilt("cpf", "case9");
        AssertExit(0, r);
        Assert.Contains("max loadability lambda = 2.373046875", r.StdOut, StringComparison.Ordinal);
    }

    // ---- D-DOC-011: gpucheck --------------------------------------------------------------------------------------------

    /// <summary>
    /// D-DOC-011: gpucheck printed FAIL lines and exited 0. Now the exit code follows the probes: 3 when any probe that
    /// ran failed (a FAIL line or a solve that did not converge), 0 otherwise (an absent backend is skipped, not failed).
    /// The test holds on any machine: it reads the probe lines and checks the code and the closing line agree with them.
    /// (The failure branch itself is driven directly in CliExitTests, through the shared FailureTally.)
    /// </summary>
    [Fact]
    public void D011_gpucheck_exit_code_follows_the_probes()
    {
        CliProcess.Result r = CliProcess.RunBuilt("gpucheck");
        bool anyFailed = r.StdOut.Contains("FAIL  ", StringComparison.Ordinal) || r.StdOut.Contains("ran but NOT converged", StringComparison.Ordinal);
        if (anyFailed)
        {
            AssertExit(3, r);
            Assert.Contains("gpucheck: FAILED", r.StdOut, StringComparison.Ordinal);
            Assert.Contains("error: gpucheck:", r.StdErr, StringComparison.Ordinal);
        }
        else
        {
            AssertExit(0, r);
            Assert.Contains("gpucheck: OK - every probe that ran passed", r.StdOut, StringComparison.Ordinal);
        }
        Assert.Contains("[CPU]", r.StdOut, StringComparison.Ordinal);   // the CPU backend is always probed
    }

    // ---- D-DOC-012: bench -----------------------------------------------------------------------------------------------

    /// <summary>D-DOC-012: a LOAD FAIL row made no difference to the exit code (always 0). Now exit 1 naming it; the
    /// other case is still benched. --cases (new) keeps the run small.</summary>
    [Fact]
    public void D012_bench_load_failure_exits_1()
    {
        string dir = TempDir();
        try
        {
            string md = Path.Combine(dir, "bench.md");
            CliProcess.Result r = CliProcess.RunBuilt("bench", "--cases", "case9,nosuchcase", "--runs", "1", "--parallel", "1", "--out", md);
            AssertExit(1, r);
            Assert.Contains("| nosuchcase | - | - | - | LOAD FAIL: unknown case 'nosuchcase'", r.StdOut, StringComparison.Ordinal);
            Assert.Contains("| case9 | 9 |", r.StdOut, StringComparison.Ordinal);
            Assert.Contains("error: bench:", r.StdErr, StringComparison.Ordinal);
            Assert.Contains("could not be loaded: nosuchcase (", r.StdErr, StringComparison.Ordinal);
            Assert.True(File.Exists(md), "the report is still written");
        }
        finally { Delete(dir); }
    }

    /// <summary>D-DOC-012: a solve that does not converge (case9 with ten times its load) is a failed cell: exit 3.</summary>
    [Fact]
    public void D012_bench_non_converged_solve_exits_3()
    {
        string dir = TempDir();
        try
        {
            string m = EditedCase9(dir, "case9-x10.m",
                ("\t5\t1\t90\t30\t", "\t5\t1\t900\t300\t"),
                ("\t7\t1\t100\t35\t", "\t7\t1\t1000\t350\t"),
                ("\t9\t1\t125\t50\t", "\t9\t1\t1250\t500\t"));
            CliProcess.Result r = CliProcess.RunBuilt("bench", "--cases", m, "--runs", "1", "--parallel", "1", "--out", Path.Combine(dir, "bench.md"));
            AssertExit(3, r);
            Assert.Contains("**NO**", r.StdOut, StringComparison.Ordinal);
            Assert.Contains("on cpu-dense: did not converge", r.StdErr, StringComparison.Ordinal);
        }
        finally { Delete(dir); }
    }

    /// <summary>D-DOC-012: a clean bench (case9: both Newton engines converge, the radial sweep is n/a) exits 0.</summary>
    [Fact]
    public void D012_bench_clean_run_exits_0()
    {
        string dir = TempDir();
        try
        {
            CliProcess.Result r = CliProcess.RunBuilt("bench", "--cases", "case9", "--runs", "1", "--parallel", "1", "--out", Path.Combine(dir, "bench.md"));
            AssertExit(0, r);
            Assert.Contains("n/a (meshed)", r.StdOut, StringComparison.Ordinal);
            Assert.DoesNotContain("error: bench:", r.StdErr, StringComparison.Ordinal);
        }
        finally { Delete(dir); }
    }

    // ---- D-DOC-013: fault converter iteration (healthy path) --------------------------------------------------------------

    /// <summary>
    /// D-DOC-013: fault now exits 3 when the converter current iteration does not converge, as protection-check does. No
    /// bundled case drives that iteration to non-convergence (it is adaptively damped), so this test pins the healthy
    /// path: a converged fault study exits 0 without the failure message. Recorded as such in the DOC-1 notes.
    /// </summary>
    [Fact]
    public void D013_fault_converged_study_exits_0_without_the_converter_failure()
    {
        CliProcess.Result r = CliProcess.RunBuilt("fault", "case9", "--bus", "5");
        AssertExit(0, r);
        Assert.Contains("fault current:", r.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("converter current iteration of the fault study did not converge", r.StdErr, StringComparison.Ordinal);
    }

    // ---- D-DOC-014 / D-DOC-015: --bfs on a meshed case ---------------------------------------------------------------------

    /// <summary>D-DOC-014: "case14 --bfs --enableExport" printed converged=False with an empty solver name and exited 0.
    /// Now the solver is named (declined), the export is still written, and the run exits 3.</summary>
    [Fact]
    public void D014_bfs_export_not_converged_exits_3_and_names_the_solver()
    {
        string dir = TempDir();
        try
        {
            string ex = Path.Combine(dir, "ex.json");
            CliProcess.Result r = CliProcess.RunBuilt("case14", "--bfs", "--enableExport=" + ex);
            AssertExit(3, r);
            Assert.Contains("[export] BackwardForwardSweep (declined: the network is not radial or not connected): converged=False", r.StdOut, StringComparison.Ordinal);
            Assert.Contains("did not converge", r.StdErr, StringComparison.Ordinal);
            Assert.True(File.Exists(ex), "the export is the evidence and is still written");
        }
        finally { Delete(dir); }
    }

    /// <summary>D-DOC-015: "case14 --bfs --ticks 2" printed DIV on every tick and exited 0; now exit 3 with the count.</summary>
    [Fact]
    public void D015_bfs_ticks_all_diverged_exits_3()
    {
        CliProcess.Result r = CliProcess.RunBuilt("case14", "--bfs", "--ticks", "2");
        AssertExit(3, r);
        Assert.Contains("DIV (0 NR)", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("error: 2 of 2 tick(s) did not converge (DIV)", r.StdErr, StringComparison.Ordinal);
    }

    /// <summary>D-DOC-015: converging headless ticks are unchanged (exit 0).</summary>
    [Fact]
    public void D015_converging_ticks_still_exit_0()
    {
        CliProcess.Result r = CliProcess.RunBuilt("case14", "--ticks", "2");
        AssertExit(0, r);
        Assert.DoesNotContain("tick(s) did not converge", r.StdErr, StringComparison.Ordinal);
    }

    // ---- D-DOC-053: stability / modal take their case like every other verb -----------------------------------------------

    /// <summary>D-DOC-053: a positional case and --dir that name different networks used to let --dir win silently.
    /// Now exit 2, naming both, on stability and modal, as on the other case verbs.</summary>
    [Theory]
    [InlineData("stability")]
    [InlineData("modal")]
    public void D053_positional_case_and_dir_disagreeing_exit_2(string verb)
    {
        string uk = Path.Combine(CliProcess.Root(), "data", "uk");
        CliProcess.Result r = CliProcess.RunBuilt(verb, "case14", "--dir", uk);
        AssertExit(2, r);
        Assert.Contains($"error: {verb}: a positional case 'case14' and --dir '{uk}' were both given and name different networks.", r.StdErr, StringComparison.Ordinal);
    }

    /// <summary>D-DOC-053: a missing --dir folder is an input error, exit 1, on both (modal exited 2, stability 1).</summary>
    [Theory]
    [InlineData("stability")]
    [InlineData("modal")]
    public void D053_missing_dir_folder_exits_1(string verb)
    {
        string missing = Path.Combine(Path.GetTempPath(), "gridsim-doc1-no-such-folder-" + Guid.NewGuid().ToString("N"));
        CliProcess.Result r = CliProcess.RunBuilt(verb, "--dir", missing);
        AssertExit(1, r);
        Assert.Contains($"error: {verb}: case folder not found: '{missing}'", r.StdErr, StringComparison.Ordinal);
    }

    /// <summary>D-DOC-053: a second positional case is refused (exit 2), as on the other case verbs.</summary>
    [Theory]
    [InlineData("stability")]
    [InlineData("modal")]
    public void D053_second_positional_case_exits_2(string verb)
    {
        CliProcess.Result r = CliProcess.RunBuilt(verb, "case9", "case14");
        AssertExit(2, r);
        Assert.Contains($"error: {verb} takes at most one case argument but got: case9 case14", r.StdErr, StringComparison.Ordinal);
    }

    /// <summary>D-DOC-053: a missing .m file named positionally is an input error, exit 1 (stability and modal exited 2
    /// for any named case that did not load).</summary>
    [Theory]
    [InlineData("stability")]
    [InlineData("modal")]
    public void D053_missing_case_file_exits_1(string verb)
    {
        string missing = Path.Combine(Path.GetTempPath(), "gridsim-doc1-no-such-" + Guid.NewGuid().ToString("N") + ".m");
        CliProcess.Result r = CliProcess.RunBuilt(verb, missing);
        AssertExit(1, r);
        Assert.Contains("MATPOWER case file not found:", r.StdErr, StringComparison.Ordinal);
    }

    // ---- D-DOC-130: one rule for an unknown case name ---------------------------------------------------------------------

    /// <summary>
    /// D-DOC-130: an unknown case name exited 1 on some verbs and 2 on others. One rule now (ch. 19): a case argument that
    /// names no case at all is a usage error, exit 2, with the one standard message naming it and the built-in names.
    /// </summary>
    [Theory]
    [InlineData("pf", "nosuchcase")]
    [InlineData("dcopf", "nosuchcase")]
    [InlineData("cpf", "nosuchcase")]
    [InlineData("fault", "nosuchcase", "--bus", "1")]
    [InlineData("protection-check", "nosuchcase")]
    [InlineData("ptdf", "nosuchcase", "--bus", "1")]
    [InlineData("estimate", "nosuchcase", "--synth")]
    [InlineData("chain", "nosuchcase")]
    [InlineData("chain", "nosuchcase", "--bess", "lfp:5:10:20")]
    [InlineData("stress", "nosuchcase")]
    [InlineData("nminus1-sweep", "nosuchcase")]
    [InlineData("transient", "nosuchcase")]
    [InlineData("stability", "nosuchcase")]
    [InlineData("modal", "nosuchcase")]
    [InlineData("losses", "nosuchcase")]
    [InlineData("gda-replay", "--synth", "--case", "nosuchcase")]
    public void D130_unknown_case_name_exits_2_on_every_verb(params string[] args)
    {
        CliProcess.Result r = CliProcess.RunBuilt(args);
        AssertExit(2, r);
        Assert.Contains("unknown case 'nosuchcase' (built-in names: case118, case14,", r.StdErr, StringComparison.Ordinal);
    }

    /// <summary>D-DOC-130: a named case FILE that does not exist is an input error, exit 1, on the same verbs.</summary>
    [Theory]
    [InlineData("pf")]
    [InlineData("dcopf")]
    [InlineData("fault", "--bus", "1")]
    public void D130_missing_case_file_exits_1(string verb, params string[] extra)
    {
        string missing = Path.Combine(Path.GetTempPath(), "gridsim-doc1-no-such-" + Guid.NewGuid().ToString("N") + ".m");
        CliProcess.Result r = CliProcess.RunBuilt(new[] { verb, missing }.Concat(extra).ToArray());
        AssertExit(1, r);
        Assert.Contains("MATPOWER case file not found:", r.StdErr, StringComparison.Ordinal);
    }

    /// <summary>D-DOC-130: in a batch over several cases a failed load stays a 'load fail' line and exit 1 (the batch
    /// rule), and the other case still runs.</summary>
    [Fact]
    public void D130_batch_load_failure_stays_exit_1()
    {
        string dir = TempDir();
        try
        {
            CliProcess.Result r = CliProcess.RunBuilt("transient", "--cases", "case9,nosuchcase", "--minutes", "0.001", "--out", Path.Combine(dir, "t"));
            AssertExit(1, r);
            Assert.Contains("[transient] load fail nosuchcase: unknown case 'nosuchcase'", r.StdErr, StringComparison.Ordinal);
            Assert.Contains("[transient] 1 of 2 case(s) failed to load", r.StdErr, StringComparison.Ordinal);
        }
        finally { Delete(dir); }
    }

    // ---- D-DOC-159: fault --bus not in the case ---------------------------------------------------------------------------

    /// <summary>D-DOC-159: fault exited 1 with "(Parameter 'faultBusId')"; protection-check exited 2. One rule: exit 2,
    /// naming the bus and the case, no C# parameter name, on both.</summary>
    [Theory]
    [InlineData("fault", "error: fault: fault bus 99 is not in case 'IEEE 9-bus (WSCC)'.", "fault", "case9", "--bus", "99")]
    [InlineData("protection-check", "protection-check: fault bus 99 is not in case 'IEEE 9-bus (WSCC)'.", "protection-check", "case9", "--fault-bus", "99")]
    [InlineData("fault slg", "error: fault: fault bus 99 is not in case 'IEEE 9-bus (WSCC)'.", "fault", "case9", "--bus", "99", "--type", "slg")]
    public void D159_unknown_fault_bus_exits_2_on_fault_and_protection_check(string what, string message, params string[] args)
    {
        CliProcess.Result r = CliProcess.RunBuilt(args);
        Assert.True(r.ExitCode == 2, $"{what}: expected exit 2\n{r}");
        Assert.DoesNotContain(InternalError, r.StdErr, StringComparison.Ordinal);
        Assert.Contains(message, r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("(Parameter '", r.StdErr, StringComparison.Ordinal);
    }
}
