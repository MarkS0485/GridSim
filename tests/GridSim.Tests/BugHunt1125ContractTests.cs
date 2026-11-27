using System.Globalization;
using System.Reflection;
using System.Text;
using GridSim.Core.Analysis.StudyCases;
using GridSim.Core.Diagnostics;
using GridSim.Core.Harmonics.Planning;
using GridSim.Core.IO;
using GridSim.Core.Journal;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.Presentation.Workbench.Sld;
using GridSim.Core.Protection.Placed;
using GridSim.Core.Qsts;
using GridSim.Core.Viz;
using GridSim.Cli;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Bug hunt of 2026-11-25, batch 3 (the CLI contract and robustness, rulings 6 and 7): every "internal error" found is a
/// clean refusal, an unwritable output exits 1 through one shared path, a UTF-8 BOM is accepted on JSON inputs, the study
/// verbs take the global switches, crossval never starts a bare-name interpreter on Windows, licence lines are invariant,
/// the journal and its snapshot store survive a crafted line and a torn object, and (Mark's ruling) a profiler variable
/// stops GridSim only while a profiler switch is on.
/// </summary>
public sealed class BugHunt1125ContractTests : IDisposable
{
    private readonly string _dir = JournalTestSupport.NewDir("bh1125-cli");

    public void Dispose() => JournalTestSupport.Delete(_dir);

    private string P(params string[] parts) => Path.Combine(new[] { _dir }.Concat(parts).ToArray());

    private static void AssertClean(CliProcess.Result r, int exit)
    {
        Assert.True(r.ExitCode == exit, r.ToString());
        Assert.DoesNotContain("internal error", r.StdErr, StringComparison.Ordinal);
    }

    // ---- CLI-02 / CLI-04 / CLI-05 / CLI-06 / SEC-07 through the built CLI ------------------------------------------------

    [Fact]
    public void Cli02_an_output_that_cannot_be_written_exits_1_naming_it()
    {
        string ro = P("ro.md");
        File.WriteAllText(ro, "x");
        File.SetAttributes(ro, FileAttributes.ReadOnly);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(ro, UnixFileMode.UserRead);
        try
        {
            CliProcess.Result r = JournalTestSupport.Run(_dir, "stress", "case9", "--out", "ro.md");
            AssertClean(r, 1);
            Assert.Contains("could not write the output", r.StdErr, StringComparison.Ordinal);
        }
        finally { File.SetAttributes(ro, FileAttributes.Normal); }
    }

    [Theory]
    [InlineData("st.csv")]
    [InlineData("st.CSV")]   // R3-07: the same file on a case-insensitive volume
    public void Cli04_a_report_named_csv_is_refused_before_anything_is_written(string name)
    {
        CliProcess.Result r = JournalTestSupport.Run(_dir, "stress", "case9", "--out", name);
        AssertClean(r, 2);
        Assert.Contains("its table is written beside it", r.StdErr, StringComparison.Ordinal);
        Assert.False(File.Exists(P(name)));
    }

    /// <summary>R3-10: the global switches before a study verb are skipped in any order.</summary>
    [Fact]
    public void R3_10_global_switches_before_a_study_verb_in_any_order()
    {
        CliProcess.Result r = JournalTestSupport.Run(_dir, "--frequency-hz", "50", "--gpu", "cable-rating", "missing.json");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("does not take --gpu", r.StdErr, StringComparison.Ordinal);
    }

    /// <summary>R3-05: a damaged object that cannot be removed is reported, not claimed as kept.</summary>
    [WindowsFact("a file held open blocks its delete on Windows only")]
    public void R3_05_a_damaged_snapshot_object_that_cannot_be_replaced_is_reported()
    {
        string objects = P("objects2");
        byte[] bytes = Encoding.UTF8.GetBytes("whole");
        string sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        string path = JournalSnapshot.ObjectPath(objects, sha);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes[..2]);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal("damaged", JournalSnapshot.Put(objects, sha, bytes));
        Assert.Null(JournalSnapshot.Put(objects, sha, bytes));
    }

    [Theory]
    [InlineData("--max-iter", "0")]
    [InlineData("--max-iter", "-3")]
    [InlineData("--tol", "0")]
    [InlineData("--tol", "-1")]
    public void Cli05_estimate_refuses_a_bad_iteration_count_or_tolerance_before_making_its_folder(string flag, string value)
    {
        CliProcess.Result r = JournalTestSupport.Run(_dir, "estimate", "case9", "--synth", flag, value, "--out", "e1");
        AssertClean(r, 2);
        Assert.False(Directory.Exists(P("e1")));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{\"sources\":")]
    [InlineData("")]
    public void Cli06_harmonic_pf_refuses_spectra_that_are_not_a_json_object(string text)
    {
        File.WriteAllText(P("s.json"), text);
        AssertClean(JournalTestSupport.Run(_dir, "harmonic-pf", "case9", "--spectra", "s.json"), 1);
    }

    [Fact]
    public void Cli07_the_study_verbs_take_the_global_switches_and_name_value_flags()
    {
        CliProcess.Result before = JournalTestSupport.Run(_dir, "--cpu", "cable-rating", "missing.json");
        Assert.DoesNotContain("unknown command", before.StdErr, StringComparison.Ordinal);
        CliProcess.Result after = JournalTestSupport.Run(_dir, "cable-rating", "missing.json", "--cpu");
        Assert.DoesNotContain("usage:", after.StdErr, StringComparison.Ordinal);
        CliProcess.Result equals = JournalTestSupport.Run(_dir, "cable-rating", "missing.json", "--out=r.json");
        Assert.DoesNotContain("usage:", equals.StdErr, StringComparison.Ordinal);
        Assert.Equal(before.ExitCode, after.ExitCode);
        Assert.Equal(before.ExitCode, equals.ExitCode);
        CliProcess.Result gpu = JournalTestSupport.Run(_dir, "--gpu", "cable-rating", "missing.json");
        Assert.Equal(2, gpu.ExitCode);
        Assert.Contains("does not take --gpu", gpu.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Sec07_a_value_a_viz_scene_cannot_hold_is_refused_by_name()
    {
        GridModel m = IeeeCases.Case9();
        GridModel big = m.CopyWith(buses: m.Buses.Select((b, i) => i == 0 ? b with { BaseKv = 1e39 } : b).ToList());
        var ex = Assert.Throws<InvalidDataException>(() => VizExport.Build(big, 1));
        Assert.Contains($"bus {m.Buses[0].Id}'s base kV", ex.Message, StringComparison.Ordinal);
    }

    // ---- CLI-10: no bare-name interpreter on Windows ---------------------------------------------------------------------

    [Fact]
    public void Cli10_windows_never_lists_a_bare_interpreter_name_and_finds_the_launcher_by_its_path()
    {
        string sys = Path.Combine("C:", "Windows"), launcher = Path.Combine(sys, "py.exe");
        var files = new HashSet<string> { launcher };
        IReadOnlyList<PythonLocator.Candidate> c = PythonLocator.Candidates(
            name => name switch { "SystemRoot" => sys, "PATH" => "", _ => null }, true, files.Contains, out string? err);
        Assert.Null(err);
        Assert.Equal(new[] { launcher }, c.Select(x => x.Command).ToArray());
        Assert.Empty(PythonLocator.Candidates(_ => null, true, _ => false, out _));
        // R3-02 / R3-03: no bare name on Linux or macOS either, and a relative PATH folder is not searched.
        Assert.Empty(PythonLocator.Candidates(name => name == "PATH" ? ".:bin" : null, false, _ => true, out _));
        Assert.Empty(PythonLocator.Candidates(name => name == "PATH" ? @".;bin;"".venv\Scripts""" : null, true, _ => true, out _));
        Assert.Equal(new[] { @"C:\Py\py.exe" },
            PythonLocator.Candidates(name => name == "PATH" ? @"""C:\Py""" : null, true, f => f == @"C:\Py\py.exe", out _).Select(x => x.Command).ToArray());
    }

    // ---- NUM-02, ANA-09, ANA-24, ANA-04 ----------------------------------------------------------------------------------

    [Fact]
    public void Num02_a_null_qsts_profile_is_refused_by_name()
    {
        var ex = Assert.Throws<InvalidDataException>(() => QstsFile.Parse(
            "{\"format\":\"gridsim-qsts/1\",\"stepSeconds\":60,\"steps\":2,\"profiles\":{\"p\":null},\"loads\":[]}", "s.json", _dir, IeeeCases.Case9()));
        Assert.Contains("an object", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ana09_a_swing_trace_channel_that_is_not_an_object_is_refused()
    {
        Assert.Throws<SwingTraceException>(() => SwingTrace.Parse("{\"cases\":[{\"name\":\"a\",\"run\":{\"timeSeconds\":[0,1],\"channels\":[1]}}]}"));
    }

    [Fact]
    public void Ana24_a_rule_that_is_not_an_object_is_a_problem_line()
    {
        var ex = Assert.Throws<FormatException>(() => ConnectionRuleSet.Parse("{\"schema\":\"gridsim-connection-rules/1\",\"id\":\"x\",\"rules\":[1]}"));
        Assert.Contains("rules[0]: a rule must be an object", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ana04_a_json_input_with_a_utf8_bom_is_read()
    {
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("{\"a\":1}")];
        using (var d = StudyCaseJson.Parse(withBom, "study")) Assert.Equal(1, d.RootElement.GetProperty("a").GetInt32());
        Assert.Equal(7, Utf8Json.WithoutBom(withBom).Length);
        Assert.Equal(7, Utf8Json.WithoutBom(Encoding.UTF8.GetBytes("{\"a\":1}")).Length);
        // End to end: a study-case style input through the CLI's byte readers (harmonic-pf's spectra go through text).
        File.WriteAllBytes(P("bom.json"), [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("{}")]);
        CliProcess.Result r = JournalTestSupport.Run(_dir, "cable-rating", "bom.json");
        Assert.DoesNotContain("0xEF", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("internal error", r.StdErr, StringComparison.Ordinal);
    }

    // ---- SEC-03: licence lines ---------------------------------------------------------------------------------------------

    [Fact]
    public void Sec03_licence_lines_are_invariant_in_any_culture()
    {
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "th-TH", "de-DE", "fr-FR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = new CultureInfo(name);
                var gate = new LicenceGate(Entitlement.FreeDefault);
                Assert.Contains("1,500 buses", gate.Describe(), StringComparison.Ordinal);
            }
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    // ---- SEC-04: the SLD working copy -------------------------------------------------------------------------------------

    [Fact]
    public void Sec04_the_sld_working_copy_is_a_user_only_folder()
    {
        using SldCaseDocument d = SldCaseDocument.OpenWorkingCopy(Path.Combine(CliProcess.Root(), "data", "uk"), new LicenceGate(Entitlement.FreeDefault));
        string copy = (string)typeof(SldCaseDocument).GetField("_workingCopy", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(d)!;
        Assert.True(Directory.Exists(copy));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(copy));
    }

    // ---- SEC-05 / SEC-06: the journal ---------------------------------------------------------------------------------------

    [Fact]
    public void Sec05_a_line_of_another_schema_cannot_set_the_sequence_and_the_sequence_never_wraps()
    {
        string j = P("j1", "journal.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(j)!);
        File.WriteAllText(j, "{\"schema\":\"x\",\"seq\":9223372036854775807}\n");
        long seq = JournalFile.Append(j, s => "{\"schema\":\"" + JournalEntry.SchemaTag + "\",\"seq\":" + s.ToString(CultureInfo.InvariantCulture) + "}");
        Assert.Equal(1, seq);

        string k = P("j2", "journal.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(k)!);
        File.WriteAllText(k, JournalJson.Line(new JournalEntry { Seq = long.MaxValue }) + "\n");
        var ex = Assert.Throws<InvalidDataException>(() => JournalFile.Append(k, s => "{}"));
        Assert.Contains("start a new journal", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sec06_a_snapshot_object_cut_short_is_replaced_by_the_next_put()
    {
        string objects = P("objects");
        byte[] bytes = Encoding.UTF8.GetBytes("the whole input file");
        string sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        string path = JournalSnapshot.ObjectPath(objects, sha);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes[..5]);   // a write cut short
        Assert.Null(JournalSnapshot.Get(objects, sha, out string? why));
        Assert.Contains("damaged", why, StringComparison.Ordinal);
        Assert.Null(JournalSnapshot.Put(objects, sha, bytes));
        Assert.Equal(bytes, JournalSnapshot.Get(objects, sha, out _));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp-*"));
    }

    // ---- SEC-08: profiler variables ---------------------------------------------------------------------------------------

    private static Dictionary<string, string?> Env(params (string Name, string? Value)[] v) => v.ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal);

    [Theory]
    [InlineData("CORECLR_PROFILER_PATH_64")]
    [InlineData("CORECLR_PROFILER")]
    [InlineData("DOTNET_PROFILER_PATH_32")]
    [InlineData("CORECLR_NOTIFICATION_PROFILERS")]
    public void Sec08_a_profiler_variable_stops_gridsim_only_while_a_profiler_switch_is_on(string name)
    {
        Assert.Null(TelemetryGuard.CheckEnvironment(Env((name, "C:\\agent\\x64\\profiler.dll"))));
        Assert.Null(TelemetryGuard.CheckEnvironment(Env((name, "x"), ("CORECLR_ENABLE_PROFILING", "0"))));
        Assert.Null(TelemetryGuard.CheckEnvironment(Env((name, "x"), ("CORECLR_ENABLE_PROFILING", "true"))));
        Assert.Null(TelemetryGuard.CheckEnvironment(Env((name, "x"), ("CORECLR_ENABLE_PROFILING", " 0x0 "))));
        // R3-01: any value the runtime reads as non-zero is on, with a profiler path it stops GridSim.
        foreach (string on in new[] { "2", "10", "ff", "0x2", "-1", "01", "1x", " 1 " })
            Assert.NotNull(TelemetryGuard.CheckEnvironment(Env((name, "x"), ("CORECLR_ENABLE_PROFILING", on))));
        foreach (string off in new[] { "", " ", "0", "00", "0x", "garbage", "-0" })
            Assert.False(TelemetryGuard.ProfilerSwitchOn(off), off);
        Assert.NotNull(TelemetryGuard.CheckEnvironment(Env((name, "x"), ("CORECLR_ENABLE_PROFILING", "1"))));
        Assert.NotNull(TelemetryGuard.CheckEnvironment(Env((name, "x"), ("DOTNET_ENABLE_PROFILING", "0x1"))));
    }
}
