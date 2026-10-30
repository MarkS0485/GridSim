using System.Globalization;
using System.Text.Json;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// DOC-6 (October 2026): the <c>gridsim emt</c> items of the docs-session findings, stream 6 (D-DOC-019..025, 121, 122,
/// 123), run against the real binary (CliProcess.RunBuilt). Each refusal is checked for exit 2 (usage error, user manual
/// ch. 19), for the stderr words that name what is wrong, for no crash-reporter line and for no output written; each
/// fix that changes a file is checked in the file.
/// </summary>
public sealed class Doc6EmtCliProcessTests
{
    private const string InternalError = "internal error";

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-doc6-emt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Delete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Run <c>gridsim emt case9 --duration 0.002 --out &lt;dir&gt;/out</c> plus the given arguments.</summary>
    private static (CliProcess.Result R, string Out) Emt(string dir, params string[] extra)
    {
        string outDir = Path.Combine(dir, "out");
        var args = new List<string> { "emt", "case9", "--duration", "0.002", "--out", outDir };
        args.AddRange(extra);
        return (CliProcess.RunBuilt(args.ToArray()), outDir);
    }

    private static void AssertRefused(CliProcess.Result r, string outDir, params string[] stderrHas)
    {
        Assert.True(r.ExitCode == 2, $"expected exit 2\n{r}");
        Assert.DoesNotContain(InternalError, r.StdErr, StringComparison.Ordinal);
        foreach (string s in stderrHas) Assert.Contains(s, r.StdErr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(outDir), "a refused run must write nothing");
    }

    private static void AssertRan(CliProcess.Result r)
    {
        Assert.True(r.ExitCode == 0, $"expected exit 0\n{r}");
        Assert.DoesNotContain(InternalError, r.StdErr, StringComparison.Ordinal);
    }

    // ---- D-DOC-019: event times ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("fault:5@-1:0.01", "time -1 s is not a number of seconds 0 or later")]
    [InlineData("fault:5@0.5:0.01", "time 0.5 s is after the end of the run (--duration 0.002 s)")]
    [InlineData("clear:5@0.01", "after the end of the run")]
    [InlineData("switch:1000000@-0.001:open", "is not a number of seconds 0 or later")]
    public void D019_an_event_time_outside_the_run_exits_2(string ev, string message)
    {
        string dir = TempDir();
        try
        {
            // The clear needs a fault at the bus to be meaningful; its own time is what is refused.
            var (r, outDir) = ev.StartsWith("clear", StringComparison.Ordinal)
                ? Emt(dir, "--event", "fault:5@0.001:0.01", "--event", ev)
                : Emt(dir, "--event", ev);
            AssertRefused(r, outDir, message);
        }
        finally { Delete(dir); }
    }

    // ---- D-DOC-020: a second fault at a faulted bus -----------------------------------------------------------------

    [Fact]
    public void D020_a_second_fault_at_one_bus_uses_its_own_resistance()
    {
        string dir = TempDir();
        try
        {
            // Fault bus 5 through 0.001 pu at 0.5 ms, then turn the fault at bus 5 into 1e6 pu at 1 ms. Before the fix the
            // second token reused the FIRST switch (0.001 pu) and "closed" it again, a no-op, so the run was the
            // one-fault run bit for bit (main 1.95.193: both peaks 0.0670033). Now the two runs agree up to 1 ms (to
            // 1e-6 pu: the second fault switch sits in the network from the start, open at 1e9 pu, so the bits differ)
            // and part from then on.
            var (twoFaults, out2) = Emt(dir, "--probe", "v:5", "--event", "fault:5@0.0005:0.001", "--event", "fault:5@0.001:1e6");
            AssertRan(twoFaults);
            string dir1 = TempDir();
            try
            {
                var (oneFault, out1) = Emt(dir1, "--probe", "v:5", "--event", "fault:5@0.0005:0.001");
                AssertRan(oneFault);
                List<(double T, double V)> a = Rows(out1), b = Rows(out2);
                Assert.Equal(a.Count, b.Count);
                for (int k = 0; k < a.Count; k++)
                {
                    if (a[k].T < 0.001 - 1e-9)
                        Assert.True(Math.Abs(a[k].V - b[k].V) < 1e-6, $"t = {a[k].T}: {a[k].V} vs {b[k].V} before the second fault");
                }
                // After it the 1e6 pu fault has released bus 5: the runs part by far more than any rounding.
                Assert.True(Math.Abs(a[^1].V - b[^1].V) > 1e-3, $"last sample {a[^1].V} (one fault) vs {b[^1].V} (two faults)");
            }
            finally { Delete(dir1); }

            // The metadata shows two fault switches, and the second fault opened the first one.
            using JsonDocument meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(out2, "run-metadata.json")));
            JsonElement events = meta.RootElement.GetProperty("parameters").GetProperty("events");
            Assert.Equal(2, events.GetArrayLength());
            Assert.Equal(0.001, events[0].GetProperty("resistancePu").GetDouble());
            Assert.Equal(1e6, events[1].GetProperty("resistancePu").GetDouble());
            Assert.NotEqual(events[0].GetProperty("switchId").GetInt32(), events[1].GetProperty("switchId").GetInt32());
        }
        finally { Delete(dir); }
    }

    [Fact]
    public void D020_two_faults_at_one_bus_at_the_same_instant_exit_2()
    {
        string dir = TempDir();
        try
        {
            var (r, outDir) = Emt(dir, "--event", "fault:5@0.001:0.01", "--event", "fault:5@0.001:0.5");
            AssertRefused(r, outDir, "2 faults at bus 5 at the same time 0.001 s");
        }
        finally { Delete(dir); }
    }

    /// <summary>The data rows of timeseries.csv (no header comment lines, no column line): time and first probe.</summary>
    private static List<(double T, double V)> Rows(string outDir) =>
        File.ReadLines(Path.Combine(outDir, "timeseries.csv"))
            .Where(l => !l.StartsWith('#') && !l.StartsWith("t_seconds", StringComparison.Ordinal))
            .Select(l => (double.Parse(l.Split(',')[0], CultureInfo.InvariantCulture), double.Parse(l.Split(',')[1], CultureInfo.InvariantCulture)))
            .ToList();

    // ---- D-DOC-021 / 025: probes -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("i:a-b", "probe 'i:a-b': expected i:<from>-<to> with two bus numbers")]       // was dropped silently
    [InlineData("v:99", "probe 'v:99': bus 99 is not in case")]                                 // was skipped
    [InlineData("v:x", "probe 'v:x': 'x' is not a bus number")]
    [InlineData("i:1-9", "has no in-service series branch 1->9")]                               // was skipped
    [InlineData("q:1", "probe 'q:1' is not v:<bus> or i:<from>-<to>")]                          // was skipped
    [InlineData("v:1,i:a-b", "probe 'i:a-b'")]                                                  // one bad token refuses the run
    public void D021_D025_a_probe_that_names_nothing_exits_2(string probe, string message)
    {
        string dir = TempDir();
        try
        {
            var (r, outDir) = Emt(dir, "--probe", probe);
            AssertRefused(r, outDir, message);
        }
        finally { Delete(dir); }
    }

    // ---- D-DOC-022: runs of no steps ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("-1", "--duration expected a positive number of seconds but got -1")]     // was "(Parameter 'capacity')"
    [InlineData("-0.000001", "--duration expected a positive number of seconds")]         // was 0 steps, exit 0
    [InlineData("0", "--duration expected a positive number of seconds but got 0")]
    [InlineData("0.000001", "less than half of one timestep")]                            // 1 us at dt 20 us: 0 steps
    public void D022_a_duration_with_no_step_exits_2(string duration, string message)
    {
        string dir = TempDir();
        try
        {
            string outDir = Path.Combine(dir, "out");
            CliProcess.Result r = CliProcess.RunBuilt("emt", "case9", "--duration", duration, "--out", outDir);
            AssertRefused(r, outDir, message);
            Assert.DoesNotContain("capacity", r.StdErr, StringComparison.Ordinal);
        }
        finally { Delete(dir); }
    }

    // ---- D-DOC-023: switch action word ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("clsoe")]
    [InlineData("toggle")]
    [InlineData("")]
    public void D023_a_switch_word_other_than_close_or_open_exits_2(string word)
    {
        string dir = TempDir();
        try
        {
            var (r, outDir) = Emt(dir, "--event", "fault:5@0.0005:0.01", "--event", $"switch:1000000@0.001:{word}");
            AssertRefused(r, outDir, $"switch action '{word}' is not close or open");
        }
        finally { Delete(dir); }
    }

    [Fact]
    public void D023_open_and_close_in_any_case_still_run()
    {
        string dir = TempDir();
        try
        {
            var (r, _) = Emt(dir, "--event", "fault:5@0.0005:0.01", "--event", "switch:1000000@0.001:OPEN", "--event", "switch:1000000@0.0015:Close");
            AssertRan(r);
        }
        finally { Delete(dir); }
    }

    // ---- D-DOC-024: non-passive networks -------------------------------------------------------------------------------

    [Fact]
    public void D024_a_non_passive_case_exits_2_unless_allowed()
    {
        string dir = TempDir();
        try
        {
            string outDir = Path.Combine(dir, "out");
            CliProcess.Result r = CliProcess.RunBuilt("emt", "case145", "--duration", "0.0002", "--out", outDir);
            AssertRefused(r, outDir, "non-passive element(s)", "negative resistance", "pass --allow-non-passive");
            Assert.DoesNotContain("(Parameter", r.StdErr, StringComparison.Ordinal);

            CliProcess.Result allowed = CliProcess.RunBuilt("emt", "case145", "--duration", "0.0002", "--out", outDir, "--allow-non-passive");
            Assert.True(allowed.ExitCode != 2, $"--allow-non-passive must let the run start\n{allowed}");
            Assert.DoesNotContain(InternalError, allowed.StdErr, StringComparison.Ordinal);
            Assert.Contains("emt: warning: --allow-non-passive: running", allowed.StdErr, StringComparison.Ordinal);
        }
        finally { Delete(dir); }
    }

    // ---- D-DOC-121: run-metadata.json records the whole run ---------------------------------------------------------

    [Fact]
    public void D121_run_metadata_records_probes_events_and_model_switches()
    {
        string dir = TempDir();
        try
        {
            var (r, outDir) = Emt(dir, "--probe", "v:5,i:4-5", "--event", "fault:5@0.0005:0.01", "--event", "clear:5@0.001",
                "--lines", "--no-cda", "--no-loads");
            AssertRan(r);
            using JsonDocument meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "run-metadata.json")));
            JsonElement root = meta.RootElement;
            // The shared fields are still there, unchanged in name.
            Assert.True(root.TryGetProperty("gitCommit", out _));
            Assert.StartsWith("emt case9 dt=20us dur=0.002s", root.GetProperty("run").GetString(), StringComparison.Ordinal);
            JsonElement p = root.GetProperty("parameters");
            Assert.Equal("case9", p.GetProperty("case").GetString());
            Assert.Equal(100, p.GetProperty("steps").GetInt64());
            Assert.True(p.GetProperty("travellingWaveLines").GetBoolean());
            Assert.False(p.GetProperty("loads").GetBoolean());
            Assert.True(p.GetProperty("noCdaFlag").GetBoolean());
            Assert.False(p.GetProperty("criticalDampingAdjustment").GetBoolean());
            Assert.Equal(2, p.GetProperty("probes").GetArrayLength());
            Assert.Equal("v:5", p.GetProperty("probes")[0].GetProperty("name").GetString());
            Assert.Equal("branchCurrent", p.GetProperty("probes")[1].GetProperty("kind").GetString());
            Assert.Equal(2, p.GetProperty("events").GetArrayLength());
            Assert.Equal("fault:5@0.0005:0.01", p.GetProperty("events")[0].GetProperty("token").GetString());
            Assert.Equal("clear", p.GetProperty("events")[1].GetProperty("kind").GetString());
        }
        finally { Delete(dir); }
    }

    // ---- D-DOC-122: a zero fault resistance ---------------------------------------------------------------------------

    [Theory]
    [InlineData("0")]
    [InlineData("-0.01")]
    public void D122_a_fault_resistance_that_is_not_positive_exits_2_naming_it(string rf)
    {
        string dir = TempDir();
        try
        {
            var (r, outDir) = Emt(dir, "--event", $"fault:5@0.001:{rf}");
            AssertRefused(r, outDir, $"fault resistance {rf} pu must be a positive number");
            Assert.DoesNotContain("dt =", r.StdErr, StringComparison.Ordinal);   // it used to quote the CDA half-step dt
        }
        finally { Delete(dir); }
    }

    // ---- D-DOC-123: summary.md reports the CDA that ran ---------------------------------------------------------------

    [Theory]
    [InlineData("trap", false, "- rule: Trapezoidal, CDA: on\n")]
    [InlineData("trap", true, "- rule: Trapezoidal, CDA: off (--no-cda)\n")]
    [InlineData("be", false, "- rule: BackwardEuler, CDA: off (not used under backward Euler)\n")]   // was "CDA: True"
    public void D123_summary_reports_whether_cda_ran(string rule, bool noCda, string line)
    {
        string dir = TempDir();
        try
        {
            var extra = new List<string> { "--rule", rule };
            if (noCda) extra.Add("--no-cda");
            var (r, outDir) = Emt(dir, extra.ToArray());
            AssertRan(r);
            Assert.Contains(line, File.ReadAllText(Path.Combine(outDir, "summary.md")), StringComparison.Ordinal);
        }
        finally { Delete(dir); }
    }

    // ---- DOC-6 new: a negative --freq ----------------------------------------------------------------------------------

    [Fact]
    public void Doc6_a_negative_frequency_exits_2()
    {
        string dir = TempDir();
        try
        {
            var (r, outDir) = Emt(dir, "--freq", "-50");
            AssertRefused(r, outDir, "--freq expected 0 (DC) or a positive frequency in Hz but got -50");
        }
        finally { Delete(dir); }
    }
}
