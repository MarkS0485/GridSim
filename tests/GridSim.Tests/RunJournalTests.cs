using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GridSim.Cli;
using GridSim.Core.Journal;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 (run journal): recording. T1 (a run is byte for byte the same with the journal on or off), T9 (concurrent
/// writers, rotation, a torn line, the lock file, the culture), the configuration rules, and the replay-scratch guard of
/// the output policy.
/// </summary>
public sealed class RunJournalTests : IDisposable
{
    private readonly string _dir = JournalTestSupport.NewDir("journal");

    public void Dispose() => JournalTestSupport.Delete(_dir);

    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static Dictionary<string, byte[]> Files(string dir) => Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
        .ToDictionary(f => Path.GetRelativePath(dir, f).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal);

    // The dynamics-study fixture of DynamicsStudyCliProcessTests: case14.raw, its dyr and a study file naming both by hash.
    private string DynamicsStudy()
    {
        string fx = Path.Combine(_dir, "dyn");
        Directory.CreateDirectory(fx);
        File.Copy(DynamicsStudyTestSupport.Psse("case14.raw"), Path.Combine(fx, "case14.raw"));
        File.Copy(DynamicsStudyTestSupport.Study("case14-study.dyr"), Path.Combine(fx, "case14.dyr"));
        var study = new
        {
            schemaVersion = 1,
            raw = new { path = "case14.raw", sha256 = Sha(Path.Combine(fx, "case14.raw")) },
            dyr = new { path = "case14.dyr", sha256 = Sha(Path.Combine(fx, "case14.dyr")) },
            initialisation = new { powerFlow = new { tolerancePu = 1e-8, maxIterations = 100 }, unmodelled = "refuse" },
            integration = new { stepSeconds = 0.005, durationSeconds = 1.0 },
            criteria = new { slipLimitRad = 2.0 * Math.PI, maxAngleSpreadDeg = 180.0 },
            angleReference = new { bus = 1, id = "1" },
            channels = new object[] { new { kind = "busVoltage", bus = 4 } },
            cases = new object[]
            {
                new { name = "fault-4", kind = "events", events = new object[]
                {
                    new { kind = "busFault", at = 0.1, bus = 4 },
                    new { kind = "faultClear", at = 0.15, bus = 4 },
                } },
            },
            execution = new { maxParallelism = 1 },
        };
        string path = Path.Combine(fx, "study.json");
        File.WriteAllText(path, JsonSerializer.Serialize(study));
        return path;
    }

    [Fact]
    public void T1_a_run_is_byte_for_byte_the_same_with_the_journal_on_or_off()
    {
        string study = DynamicsStudy();
        string[][] commands =
        [
            ["pf", "case14", "--json", "r.json"],
            ["nminus1", "case9", "--csv", "r.csv"],
            ["dynamics-study", study, "--out", "result.json"],
            ["case14", "--once", "--enableExport=x.json"],
        ];
        string work = Path.Combine(_dir, "work");
        string flagJournal = Path.Combine(_dir, "j1", "journal.jsonl");
        string envJournal = Path.Combine(_dir, "j2", "journal.jsonl");
        int n = 0;
        foreach (string[] cmd in commands)
        {
            n++;
            var runs = new List<(CliProcess.Result Result, Dictionary<string, byte[]> Files)>();
            foreach (int variant in new[] { 0, 1, 2 })
            {
                JournalTestSupport.Delete(work);
                Directory.CreateDirectory(work);
                string[] line = variant == 1 ? new[] { "--journal", flagJournal }.Concat(cmd).ToArray() : cmd;
                var env = variant == 2 ? new Dictionary<string, string?> { ["GRIDSIM_JOURNAL"] = envJournal } : null;
                CliProcess.Result r = JournalTestSupport.Run(work, env, line);
                runs.Add((r, Files(work)));
            }
            (CliProcess.Result off, Dictionary<string, byte[]> offFiles) = runs[0];
            Assert.True(off.ExitCode is 0 or 3, off.ToString());
            Assert.NotEmpty(offFiles);
            for (int v = 1; v < 3; v++)
            {
                (CliProcess.Result on, Dictionary<string, byte[]> onFiles) = runs[v];
                string what = $"{string.Join(' ', cmd)} (journal {(v == 1 ? "--journal" : "GRIDSIM_JOURNAL")})";
                Assert.True(off.ExitCode == on.ExitCode, what + ": exit code");
                Assert.True(off.StdOut == on.StdOut, what + ": stdout");
                Assert.True(off.StdErr == on.StdErr, what + ": stderr\n" + off.StdErr + "\n---\n" + on.StdErr);
                Assert.Equal(offFiles.Keys.Order(StringComparer.Ordinal), onFiles.Keys.Order(StringComparer.Ordinal));
                foreach (string f in offFiles.Keys)
                {
                    if (f == "x.json")
                    {
                        // The solve export carries the time it was written and the solve's wall time (meta.timestampUtc,
                        // meta.elapsedMs), so two runs never share those bytes, journal or not. Every other field is
                        // compared bit for bit.
                        FileComparison c = ResultCompare.Compare(f, offFiles[f], onFiles[f], "run", s => s, s => s, VolatileFields.Default);
                        Assert.True(c.Identical, what + ": " + string.Join(", ", c.Fields.Select(x => x.Field)));
                    }
                    else Assert.True(offFiles[f].AsSpan().SequenceEqual(onFiles[f]), what + ": " + f + " differs");
                }
            }
            Assert.Equal(n, JournalTestSupport.Lines(flagJournal));
            Assert.Equal(n, JournalTestSupport.Lines(envJournal));
        }

        // A result file never shows the journal switch.
        JournalTestSupport.Delete(work);
        Directory.CreateDirectory(work);
        Assert.Equal(0, JournalTestSupport.Run(work, "pf", "case14", "--journal", flagJournal, "--json", "r.json").ExitCode);
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(work, "r.json")));
        Assert.Equal(new[] { "pf", "case14", "--json", "r.json" }, doc.RootElement.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray());

        // The entries name the run: verb, argv without the switch, the input and the output with their hashes.
        IReadOnlyList<JournalLine> lines = JournalFile.Read(flagJournal);
        Assert.Equal(Enumerable.Range(1, 5).Select(i => (long)i), lines.Select(l => l.Entry!.Seq));
        JournalEntry last = lines[^1].Entry!;
        Assert.Equal("pf", last.Verb);
        Assert.DoesNotContain("--journal", last.Argv);
        JournalOutput o = Assert.Single(last.Outputs);
        Assert.Equal(Sha(Path.Combine(work, "r.json")), o.Sha256);
        Assert.Equal("r.json", o.CwdRel);
        Assert.Equal("builtin", Assert.Single(last.Tokens, t => t.Index == 1).Kind);
        Assert.Equal("safe", last.ReplayClass);
        Assert.Equal("run", lines[3].Entry!.Verb);
        Assert.Equal("input", Assert.Single(lines[2].Entry!.Tokens, t => t.Kind == "input").Kind);
    }

    [Fact]
    public void A_bad_journal_switch_is_a_usage_error_and_off_switches_it_off()
    {
        Assert.Equal(2, JournalTestSupport.Run(_dir, "pf", "case9", "--journal").ExitCode);
        Assert.Equal(2, JournalTestSupport.Run(_dir, "--journal", @"\\server\share\j.jsonl", "pf", "case9").ExitCode);
        Assert.Equal(2, JournalTestSupport.Run(_dir, "--journal", "a.jsonl", "--journal", "b.jsonl", "pf", "case9").ExitCode);
        string j = Path.Combine(_dir, "j.jsonl");
        var off = new Dictionary<string, string?> { ["GRIDSIM_JOURNAL"] = "off" };
        Assert.Equal(0, JournalTestSupport.Run(_dir, off, "--journal", j, "pf", "case9").ExitCode);
        Assert.False(File.Exists(j));
        // Not journaled: the version, help and the licence verb.
        JournalTestSupport.Run(_dir, "--journal", j, "--version");
        JournalTestSupport.Run(_dir, "--journal", j, "pf", "--help");
        JournalTestSupport.Run(_dir, "--journal", j, "licence", "status");
        Assert.False(File.Exists(j));
        Assert.Equal(0, JournalTestSupport.Run(_dir, "--journal=" + j, "pf", "case9").ExitCode);
        Assert.Equal(1, JournalTestSupport.Lines(j));
    }

    [Fact]
    public void T6_the_replay_scratch_variable_makes_the_output_policy_refuse_a_target_outside_it()
    {
        string scratch = Path.Combine(_dir, "scratch");
        Directory.CreateDirectory(scratch);
        var env = new Dictionary<string, string?> { [CliFiles.ReplayScratchVariable] = scratch };
        CliProcess.Result outside = JournalTestSupport.Run(_dir, env, "dcpf", "case14", "--json", Path.Combine(_dir, "outside.json"));
        Assert.Equal(2, outside.ExitCode);
        Assert.Contains("a re-run writes inside its scratch folder only", outside.StdErr, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_dir, "outside.json")));
        Assert.Equal(0, JournalTestSupport.Run(_dir, env, "dcpf", "case14", "--json", Path.Combine(scratch, "inside.json")).ExitCode);
        Assert.True(File.Exists(Path.Combine(scratch, "inside.json")));
    }

    [Fact]
    public void T9_sixteen_concurrent_writers_give_sixteen_lines_with_unique_sequence_numbers()
    {
        string j = Path.Combine(_dir, "concurrent", "journal.jsonl");
        var procs = new List<Process>();
        for (int i = 0; i < 16; i++)
        {
            var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } h ? h : "dotnet")
            {
                WorkingDirectory = _dir, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            psi.ArgumentList.Add(CliProcess.BuiltCliDll());
            foreach (string a in new[] { "--journal", j, "dcpf", "case9" }) psi.ArgumentList.Add(a);
            psi.Environment.Remove("GRIDSIM_LICENCE");
            psi.Environment["GRIDSIM_LICENCE_FILE"] = Path.Combine(_dir, "no-licence.gridsim");
            psi.Environment["GRIDSIM_JOURNAL_CONFIG"] = Path.Combine(_dir, "no-config.json");
            Process p = Process.Start(psi)!;
            p.OutputDataReceived += (_, _) => { };
            p.ErrorDataReceived += (_, _) => { };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            procs.Add(p);
        }
        foreach (Process p in procs)
        {
            Assert.True(p.WaitForExit(150_000));
            Assert.Equal(0, p.ExitCode);
            p.Dispose();
        }
        IReadOnlyList<JournalLine> lines = JournalFile.Read(j);
        Assert.Equal(16, lines.Count);
        Assert.All(lines, l => Assert.Null(l.Error));
        Assert.Equal(Enumerable.Range(1, 16).Select(i => (long)i), lines.Select(l => l.Entry!.Seq).Order());
        Assert.Equal(0, new FileInfo(JournalFile.LockPath(j)).Length);   // the lock file holds no data
    }

    private static JournalEntry Entry(string verb = "pf") => new()
    {
        RecordedUtc = JournalJson.Utc(new DateTime(2026, 11, 23, 10, 15, 2, DateTimeKind.Utc).AddTicks(1234567)),
        Version = "2.1.104", InformationalVersion = "2.1.104+abc", Commit = "abc", Verb = verb,
        Argv = [verb, "case14", "--json", "r.json"], Cwd = "/work", ElapsedMs = 1840, PeakWorkingSetBytes = 212_000_000,
        ExitCode = 0, ReplayClass = "safe", Seq = 1,
    };

    [Fact]
    public void T9_rotation_keeps_the_sequence_continuous_across_segments()
    {
        string j = Path.Combine(_dir, "rot", "journal.jsonl");
        for (int i = 0; i < 12; i++) JournalFile.Append(j, seq => { JournalEntry e = Entry(); e.Seq = seq; return JournalJson.Line(e); }, maxSegmentBytes: 1200);
        IReadOnlyList<(string Path, long First, long Last)> segments = JournalFile.Segments(j);
        Assert.True(segments.Count >= 2, "no rotation at a 1200-byte segment size");
        long expectFirst = 1;
        foreach ((string path, long first, long last) in segments)
        {
            Assert.Equal(expectFirst, first);
            Assert.StartsWith("journal-", Path.GetFileName(path), StringComparison.Ordinal);
            expectFirst = last + 1;
        }
        Assert.Equal(Enumerable.Range(1, 12).Select(i => (long)i), JournalFile.Read(j).Select(l => l.Entry!.Seq));
    }

    [Fact]
    public void T9_a_torn_last_line_is_skipped_and_the_next_append_still_lands()
    {
        string j = Path.Combine(_dir, "torn", "journal.jsonl");
        for (int i = 0; i < 2; i++) JournalFile.Append(j, seq => { JournalEntry e = Entry(); e.Seq = seq; return JournalJson.Line(e); });
        File.AppendAllText(j, "{\"schema\":\"gridsim-journal/1\",\"seq\":3,\"recorded");   // a writer that died mid-line
        Assert.Equal(new long[] { 1, 2 }, JournalFile.Read(j).Select(l => l.Entry!.Seq));
        long seq3 = JournalFile.Append(j, seq => { JournalEntry e = Entry(); e.Seq = seq; return JournalJson.Line(e); });
        Assert.Equal(3, seq3);
        IReadOnlyList<JournalLine> lines = JournalFile.Read(j);
        Assert.Equal(4, lines.Count);
        Assert.Equal("damaged entry", lines[2].Error);   // the torn line, now closed, is reported as damaged
        Assert.Equal(3, lines[3].Entry!.Seq);
        Assert.Equal(0, new FileInfo(JournalFile.LockPath(j)).Length);
    }

    [Fact]
    public void T9_the_entry_is_written_in_the_invariant_culture_whatever_the_current_culture()
    {
        string invariant = JournalJson.Line(Entry());
        string german = "";
        var t = new Thread(() =>
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
            german = JournalJson.Line(Entry());
        });
        t.Start();
        t.Join();
        Assert.Equal(invariant, german);
        Assert.Contains("\"recordedUtc\":\"2026-11-23T10:15:02.1234567Z\"", invariant, StringComparison.Ordinal);
        Assert.Contains("\"elapsedMs\":1840,\"peakWorkingSetBytes\":212000000", invariant, StringComparison.Ordinal);
        JournalEntry back = JournalJson.Parse(invariant, out string? error)!;
        Assert.Null(error);
        Assert.Equal(invariant, JournalJson.Line(back));
    }

    [Fact]
    public void The_configuration_follows_its_order_and_off_always_wins()
    {
        string cfg = Path.Combine(_dir, "journal.json");
        Assert.Null(JournalConfig.Resolve(null, false, null, cfg, out string? e0, out string? w0));
        Assert.Null(e0);
        Assert.Null(w0);
        Assert.Null(JournalConfig.Resolve("a.jsonl", false, "off", cfg, out _, out _));
        Assert.Null(JournalConfig.Resolve("", false, null, cfg, out string? e1, out _));
        Assert.NotNull(e1);
        Assert.Null(JournalConfig.Resolve(@"\\server\share\j.jsonl", false, null, cfg, out string? e2, out _));
        Assert.Contains("network or device", e2, StringComparison.Ordinal);
        JournalSettings env = JournalConfig.Resolve(null, true, Path.Combine(_dir, "e.jsonl"), cfg, out _, out _)!;
        Assert.Equal("GRIDSIM_JOURNAL", env.Source);
        Assert.True(env.Snapshot);
        File.WriteAllText(cfg, "{ not json");
        Assert.Null(JournalConfig.Resolve(null, false, null, cfg, out string? e3, out string? w3));
        Assert.Null(e3);
        Assert.NotNull(w3);
        File.WriteAllText(cfg, """{"schema":"gridsim-journal-config/1","enabled":true,"path":null,"snapshot":true,"maxSegmentBytes":1000}""");
        JournalSettings c = JournalConfig.Resolve(null, false, null, cfg, out _, out _)!;
        Assert.Equal(JournalConfig.DefaultJournalPath(), c.Path);
        Assert.True(c.Snapshot);
        Assert.Equal(1000, c.MaxSegmentBytes);
        File.WriteAllText(cfg, """{"schema":"gridsim-journal-config/1","enabled":false}""");
        Assert.Null(JournalConfig.Resolve(null, false, null, cfg, out _, out string? w4));
        Assert.Null(w4);
    }

    [Fact]
    public void The_shared_classification_records_exactly_the_inputs_the_output_policy_records()
    {
        string input = Path.Combine(_dir, "in.m");
        File.Copy(Path.Combine(CliProcess.Root(), "data", "matpower", "lib", "case9.m"), input);
        string[] args = ["dcpf", input, "--json", Path.Combine(_dir, "o.json"), "--cases", input + ",case9", "--enableExport=" + Path.Combine(_dir, "e.json")];
        // CliFiles.Init records the "input" kinds of this very list (it is built from it), so the journal's inputs are the
        // output policy's inputs. Init is not called here: its state is process-wide and CliFilesTests own it.
        List<CliFiles.ClassifiedToken> c = CliFiles.Classify(args);
        Assert.Equal(new[] { (1, (int?)null, input), (5, (int?)0, input) }, c.Where(t => t.Kind == "input").Select(t => (t.Index, t.Part, t.Full)));
        Assert.Equal(new[] { 3, 6 }, c.Where(t => t.Kind == "output").Select(t => t.Index));
        Assert.Equal(Path.Combine(_dir, "e.json"), c.Single(t => t.Index == 6).Full);
    }
}
