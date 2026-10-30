using Xunit;

namespace GridSim.Tests;

/// <summary>
/// S4 "CLI and loader" (October 2026): end-to-end checks on the real gridsim binary for the fail-loud and CLI-output
/// items of that stream (N-ids from review-progress/2026-10/NEW-FINDINGS.md). Each test names its finding. They run
/// the built CLI through <see cref="CliProcess"/>, so they prove what a script sees: the exit code, stdout and stderr.
/// </summary>
public sealed class CliS4CliAndLoaderProcessTests
{
    // A fresh copy of a bundled JSON case folder (from the test output's data folder) with the first occurrence of
    // `find` in `file` replaced. Fails loud when the text is not there. The caller deletes the folder.
    internal static string EditedCaseCopy(string caseFolder, string file, string find, string replace)
    {
        string src = Path.Combine(AppContext.BaseDirectory, "data", caseFolder);
        Assert.True(Directory.Exists(src), $"bundled case folder missing: {src}");
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-s4-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (string f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        string path = Path.Combine(dir, file);
        string text = File.ReadAllText(path);
        int at = text.IndexOf(find, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{find}' not found in {file}");
        File.WriteAllText(path, text[..at] + replace + text[(at + find.Length)..]);
        return dir;
    }

    internal static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---- N054: taps not settled ----------------------------------------------------------------------------------

    /// <summary>
    /// N054: a tap changer asked for a voltage its range cannot reach ends at the end of its range with its bus out of
    /// band. The solve report used to read exactly like a regulated state; now it names the tap. Reporting only: the
    /// power flow converged, so the exit code stays 0.
    /// </summary>
    [Fact]
    public void N054_once_report_names_a_tap_that_ran_out_of_range()
    {
        string dir = EditedCaseCopy("uk", "onloadtapchanger.json", "\"targetVpu\": 1.0,", "\"targetVpu\": 1.25,");
        try
        {
            CliProcess.Result r = CliProcess.RunBuilt("--dir", dir, "--once");
            Assert.True(r.ExitCode == 0, r.ToString());
            Assert.Contains("Taps NOT SETTLED: 1 tap changer(s) at the end of their range with the controlled bus still outside its deadband: T1 GSP N Scotland.", r.StdOut, StringComparison.Ordinal);
        }
        finally { TryDelete(dir); }
    }

    /// <summary>N054: the bundled GB reduced model settles its taps, so its report carries no such line.</summary>
    [Fact]
    public void N054_settled_taps_add_no_line()
    {
        CliProcess.Result r = CliProcess.RunBuilt("uk", "--once");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.DoesNotContain("NOT SETTLED", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("Inertia 171.7 GVAs", r.StdOut, StringComparison.Ordinal);   // N058: a case with H keeps its figure
    }

    // ---- N058: case14 kV 0 and inertia 0 --------------------------------------------------------------------------

    /// <summary>
    /// N058: case14 carries baseKV 0 on every bus and no generator H (MATPOWER has no H column). The report said
    /// "Inertia 0.0 GVAs" and printed kV 0 on every bus; now it says the values are not in the data. Labels only.
    /// </summary>
    [Fact]
    public void N058_case14_says_kv_and_inertia_are_not_in_the_data()
    {
        CliProcess.Result r = CliProcess.RunBuilt("case14", "--once");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("Inertia not in the data (no generator H)", r.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("Inertia 0.0 GVAs", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("kV '-': base voltage not in the data for 14 of 14 bus(es) (the case gives 0).", r.StdOut, StringComparison.Ordinal);
    }

    // ---- N076: screen --csv column name ---------------------------------------------------------------------------

    /// <summary>N076: the screen CSV's RoCoF column holds the peak over the run, and is now named so (a contract change).</summary>
    [Fact]
    public void N076_screen_csv_names_the_peak_rocof_column()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-n076-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string csv = Path.Combine(dir, "screen.csv");
            CliProcess.Result r = CliProcess.RunBuilt("screen", "uk", "--csv", csv);
            Assert.True(r.ExitCode is 0 or 3, r.ToString());   // the verdict decides; the file is written either way
            string header = File.ReadLines(csv).First();
            Assert.Equal("name,lost_mw,remaining_inertia_gva_s,peak_rocof_hz_per_s,nadir_hz,rocof_breach,statutory_breach,lfdd_risk", header);
        }
        finally { TryDelete(dir); }
    }

    // ---- N074: positional case / file wherever it sits ---------------------------------------------------------------

    /// <summary>N074: impedance-stability reads its case after a flag (it used to study case9 whatever came later).</summary>
    [Fact]
    public void N074_impedance_stability_reads_a_case_after_a_flag()
    {
        CliProcess.Result r = CliProcess.RunBuilt("impedance-stability", "--ppd", "5", "case14");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("IEEE 14-bus", r.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("WSCC", r.StdOut, StringComparison.Ordinal);
    }

    /// <summary>N074: two case arguments are refused rather than one being ignored.</summary>
    [Fact]
    public void N074_impedance_stability_refuses_two_cases()
    {
        CliProcess.Result r = CliProcess.RunBuilt("impedance-stability", "case9", "case14");
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("impedance-stability takes at most one case argument", r.StdErr, StringComparison.Ordinal);
    }

    /// <summary>N074: replay reads its file after a flag; a missing file named after a flag is an input error (exit 1), so
    /// the path was really used (the old code dropped it and played the bundled sample).</summary>
    [Fact]
    public void N074_replay_uses_a_file_given_after_a_flag()
    {
        CliProcess.Result r = CliProcess.RunBuilt("replay", "--frames", "1", "no-such-trace.csv");
        Assert.True(r.ExitCode == 1, r.ToString());
        Assert.Contains("no-such-trace.csv", r.StdErr + r.StdOut, StringComparison.Ordinal);
    }

    /// <summary>N074: freq likewise; and two file arguments exit 2.</summary>
    [Fact]
    public void N074_freq_uses_a_file_after_a_flag_and_refuses_two()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-n074-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            CliProcess.Result r = CliProcess.RunBuilt("freq", "--json", Path.Combine(dir, "f.json"), "no-such-freq.csv");
            Assert.True(r.ExitCode == 1, r.ToString());
            Assert.Contains("no-such-freq.csv", r.StdErr + r.StdOut, StringComparison.Ordinal);
            CliProcess.Result two = CliProcess.RunBuilt("freq", "a.csv", "b.csv");
            Assert.True(two.ExitCode == 2, two.ToString());
            Assert.Contains("freq takes at most one file argument", two.StdErr, StringComparison.Ordinal);
        }
        finally { TryDelete(dir); }
    }

    // ---- N103: flags of another verb -----------------------------------------------------------------------------

    /// <summary>N103: a flag that belongs to another verb exits 2 naming it (trip used to run as if --top were absent).</summary>
    [Theory]
    [InlineData("trip", "--top", "5")]
    [InlineData("interpose", "--catalog", "c.json")]
    [InlineData("ptdf", "--branch", "3")]
    public void N103_a_flag_of_another_verb_exits_2(string verb, string flag, string value)
    {
        CliProcess.Result r = CliProcess.RunBuilt(verb, flag, value);
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains($"error: {verb} does not take {flag} ", r.StdErr, StringComparison.Ordinal);
        Assert.Equal("", r.StdOut);   // refused before the verb runs
    }

    // ---- N104: modal --json ---------------------------------------------------------------------------------------

    /// <summary>N104: modal --json is console JSON; a .json file name after it exits 2 and points at --export.</summary>
    [Fact]
    public void N104_modal_json_with_a_file_name_points_at_export()
    {
        CliProcess.Result r = CliProcess.RunBuilt("modal", "case9", "--json", "modes.json");
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("use --export modes.json", r.StdErr, StringComparison.Ordinal);
        CliProcess.Result ok = CliProcess.RunBuilt("modal", "case9", "--json");
        Assert.True(ok.ExitCode is 0 or 1, ok.ToString());   // console JSON still works (exit 1 only for an unstable mode)
        Assert.StartsWith("{", ok.StdOut.TrimStart(), StringComparison.Ordinal);
    }

    // ---- N122: pf writes result files -----------------------------------------------------------------------------

    /// <summary>N122: pf is a result-writer verb: --json writes the result (exit code inside, every bus); stdout is the
    /// same as without the flag.</summary>
    [Fact]
    public void N122_pf_writes_a_result_file_and_keeps_stdout()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-n122-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string json = Path.Combine(dir, "pf.json");
            CliProcess.Result plain = CliProcess.RunBuilt("pf", "case14");
            CliProcess.Result r = CliProcess.RunBuilt("pf", "case14", "--json", json);
            Assert.True(r.ExitCode == 0, r.ToString());
            Assert.Equal(plain.StdOut, r.StdOut);
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json));
            Assert.Equal("pf", doc.RootElement.GetProperty("verb").GetString());
            Assert.Equal(0, doc.RootElement.GetProperty("exitCode").GetInt32());
            Assert.True(doc.RootElement.GetProperty("summary").GetProperty("converged").GetBoolean());
            Assert.Equal(14, doc.RootElement.GetProperty("tables").GetProperty("buses").GetArrayLength());
        }
        finally { TryDelete(dir); }
    }

    // ---- N116: iec60909 earth faults on a model with no earthing data --------------------------------------------

    /// <summary>
    /// N116: an earth fault (slg, dlg) at one bus of a model that carries no transformer winding connection (case9, like
    /// every bundled case) has no earthed neutral, so its duty reflects missing data. It used to print an all-zero
    /// earth-fault current and exit 0; now it still prints the duty, says why on stderr and exits 3, like the
    /// --all-buses sweep, the fault verb and protection-check --fault-type.
    /// </summary>
    [Theory]
    [InlineData("slg")]
    [InlineData("dlg")]
    public void N116_iec60909_earth_fault_without_winding_data_exits_3_and_says_why(string type)
    {
        CliProcess.Result r = CliProcess.RunBuilt("iec60909", "case9", "--bus", "5", "--type", type);
        Assert.True(r.ExitCode == 3, r.ToString());
        Assert.Contains("carries no transformer winding connections at all", r.StdErr, StringComparison.Ordinal);
        Assert.Contains("earth-fault duty above reflects missing data. Exit 3.", r.StdErr, StringComparison.Ordinal);
        // The duty is still printed: the refusal is about trusting it, not about hiding what the data gives.
        Assert.Contains("IEC 60909 - ", r.StdOut, StringComparison.Ordinal);
    }

    /// <summary>N116: three-phase and line-to-line faults never touch the zero sequence, so they are unaffected (exit 0).</summary>
    [Theory]
    [InlineData("3ph")]
    [InlineData("ll")]
    public void N116_iec60909_faults_without_earth_path_are_unaffected(string type)
    {
        CliProcess.Result r = CliProcess.RunBuilt("iec60909", "case9", "--bus", "5", "--type", type);
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.DoesNotContain("winding connections", r.StdErr, StringComparison.Ordinal);
    }

    /// <summary>N116: the result file of a refused earth fault records why (earthing_data false) and the exit code 3.</summary>
    [Fact]
    public void N116_iec60909_result_file_records_missing_earthing_data()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-n116-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string json = Path.Combine(dir, "slg.json");
            CliProcess.Result r = CliProcess.RunBuilt("iec60909", "case9", "--bus", "5", "--type", "slg", "--json", json);
            Assert.True(r.ExitCode == 3, r.ToString());
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json));
            Assert.Equal(3, doc.RootElement.GetProperty("exitCode").GetInt32());
            Assert.False(doc.RootElement.GetProperty("summary").GetProperty("earthing_data").GetBoolean());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
