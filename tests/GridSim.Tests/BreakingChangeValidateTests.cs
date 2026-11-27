using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GridSim.Core.Analysis.StudyCases;
using GridSim.Core.Journal;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 (run journal): <c>gridsim breaking-change validate</c> and <c>import</c>. T2 (a mixed journal re-runs IDENTICAL and
/// leaves the originals alone), T3 (a perturbed result: the exact figures), T4 (missing and changed inputs, the snapshot
/// store), T5 (the report does not depend on --parallel), T6 (confinement and the never list), T10 (exit precedence),
/// SKIPPED-SAME-BUILD and import.
/// </summary>
public sealed class BreakingChangeValidateTests : IDisposable
{
    private readonly string _dir = JournalTestSupport.NewDir("bcv");

    public void Dispose() => JournalTestSupport.Delete(_dir);

    private static string Root => CliProcess.Root();

    private string P(params string[] parts) => Path.Combine(new[] { _dir }.Concat(parts).ToArray());

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private CliProcess.Result Validate(string journal, params string[] more) =>
        JournalTestSupport.Run(_dir, new[] { "breaking-change", "validate", "--journal", journal }.Concat(more).ToArray());

    private static JsonElement Entries(string reportJson)
    {
        using JsonDocument d = JsonDocument.Parse(File.ReadAllText(reportJson));
        return d.RootElement.GetProperty("tables").GetProperty("entries").Clone();
    }

    private string CopyCase14(string name)
    {
        string p = P("in", name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.Copy(Path.Combine(Root, "data", "matpower", "lib", "case14.m"), p, overwrite: true);
        return p;
    }

    // ---- T2 and T5 -----------------------------------------------------------------------------------------------

    [Fact]
    public void T2_T5_twenty_mixed_jobs_re_run_identical_leave_the_originals_alone_and_the_report_does_not_depend_on_parallel()
    {
        string work = P("work");
        Directory.CreateDirectory(work);
        string journal = P("journal", "journal.jsonl");
        string c14 = CopyCase14("c14.m");
        string uk = P("in", "uk");
        Directory.CreateDirectory(uk);
        foreach (string f in Directory.GetFiles(Path.Combine(Root, "data", "uk"))) File.Copy(f, Path.Combine(uk, Path.GetFileName(f)));
        File.WriteAllText(P("in", "chain.network.json"), """
            { "schema": "gridsim-network/2",
              "buses": [ { "id": 1, "name": "G", "baseKv": 20, "type": "slack", "vm": 1.0 }, { "id": 2, "name": "A", "baseKv": 132, "type": "pq" },
                         { "id": 3, "name": "B", "baseKv": 132, "type": "pq" }, { "id": 4, "name": "L", "baseKv": 20, "type": "pq" } ],
              "generators": [ { "name": "G1", "bus": 1, "type": "", "capacityMva": 100, "outputMw": 0, "voltageSetpoint": 1.0, "role": "slack", "subtransientXdPu": 0.2 } ],
              "demands": [ { "name": "load", "bus": 4, "pMw": 60, "qMvar": 20 } ],
              "lines": [ { "name": "L23", "from": 2, "to": 3, "rPu": 0.01, "xPu": 0.2, "ratingMva": 100 } ],
              "transformers": [
                { "name": "T1", "from": 1, "to": 2, "rPu": 0, "xPu": 0.1, "ratingMva": 100, "windings": { "from": "Delta", "to": "WyeGrounded" } },
                { "name": "T2", "from": 3, "to": 4, "rPu": 0, "xPu": 0.15, "ratingMva": 100, "windings": { "from": "WyeGrounded", "to": "Delta" } } ] }
            """);
        string chain = P("in", "chain.case.json");
        File.WriteAllText(chain, """{ "schema": "gridsim-case/2", "name": "chain4", "system": { "baseMva": 100, "frequencyHz": 50 }, "network": ["chain.network.json"] }""");
        string prob = P("in", "prob.json");
        File.WriteAllText(prob, """{ "schemaVersion": 1, "study": "adequacy", "seed": 7, "units": [ {"id":"g1","capacityMw":100,"availability":0.9}, {"id":"g2","capacityMw":100,"availability":0.9} ], "load": { "constantMw": 120, "hours": 1 }, "methods": [ {"method":"convolution","samples":1} ] }""");
        string harm = P("in", "harm.json");
        File.WriteAllText(harm, """{ "schemaVersion": 1, "study": "harmonic-assess", "pcc": { "lineKv": 11, "sscMva": 60, "xOverR": 10 }, "equipment": [ { "id": "drive", "technology": "active-front-end", "ratingKva": 600 } ] }""");

        string[][] jobs =
        [
            ["pf", "case14", "--json", "pf.json"],                                     // a writer verb
            ["dcpf", c14, "--csv", "out/dcpf.csv"],                                    // CSV with sibling tables, an input file
            ["nminus1", "case9", "--json", "n1.json"],                                 // exit 3 with a result
            ["dcopf", "case9", "--json", "dcopf.json", "--csv", "dcopf.csv"],
            ["ptdf", "case14", "--bus", "9", "--top", "2", "--out", "ptdf.json"],
            ["losses", c14, "--json", "losses.json"],
            ["iec60909", "case9", "--bus", "5", "--json", "sc.json"],
            ["fault-series", chain, "--branch", "0", "--open", "1", "--json", "fs.json"],   // a v2 manifest
            ["taps", uk, "--json", "taps.json"],                                       // a case folder
            ["probabilistic", prob, "--out", "own.json", "--json", "prob.json"],       // a study verb with its own --out
            ["case14", "--once", "--enableExport"],                                    // a default results/ output
            ["harmonic-assess", harm, "--json", "h.json"],
            ["sensitivity", "case9", "--kind", "dvdq", "--bus", "5", "--json", "sens.json"],
            ["qv-curve", "case9", "--bus", "5", "--vmin", "0.3", "--csv", "qv.csv"],
            ["fault", "case9", "--bus", "5", "--json", "fault.json"],
            ["acopf-full", "case9", "--json", "acf.json"],
            ["pf", c14, "--json", "pf2.json"],
            ["dc-contingency", "case9", "--json", "dcc.json"],
            ["physics-check", "case9", "--json", "phys.json"],
            ["grade", "--chain", "iec-si:100:0.1,iec-si:5000:0.3", "--current", "1000", "--json", "grade.json"],
        ];
        foreach (string[] job in jobs)
        {
            CliProcess.Result r = JournalTestSupport.Run(work, new[] { "--journal", journal }.Concat(job).ToArray());
            Assert.True(r.ExitCode is 0 or 3, string.Join(' ', job) + "\n" + r);
        }
        IReadOnlyList<JournalLine> lines = JournalFile.Read(journal);
        Assert.Equal(20, lines.Count);
        Assert.True(lines.Select(l => l.Entry!.Verb).Distinct().Count() >= 8);
        Assert.Contains(lines, l => l.Entry!.Tokens.Any(t => t.Kind == "case-folder"));
        Assert.Contains(lines, l => l.Entry!.CaseFiles.Any(c => c.Label.StartsWith("network/", StringComparison.Ordinal)));
        Assert.Contains(lines, l => l.Entry!.Outputs.Any(o => o.CwdRel == "results/exports/case14-solve.json"));
        Assert.Contains(lines, l => l.Entry!.Outputs.Count >= 3);   // dcpf's CSV and its siblings

        // Every file the runs read or wrote, with its bytes and time, before validate.
        var before = Directory.GetFiles(_dir, "*", SearchOption.AllDirectories).Where(f => !f.StartsWith(P("journal"), StringComparison.Ordinal))
            .ToDictionary(f => f, f => (Bytes: File.ReadAllBytes(f), Time: File.GetLastWriteTimeUtc(f)));

        JournalTestSupport.FromAnotherBuild(journal);
        CliProcess.Result serial = Validate(journal, "--parallel", "1", "--json", P("v1.json"), "--csv", P("v1.csv"));
        Assert.True(serial.ExitCode == 0, serial.ToString());
        JsonElement entries = Entries(P("v1.json"));
        Assert.Equal(20, entries.GetArrayLength());
        Assert.All(entries.EnumerateArray(), e => Assert.True(e.GetProperty("status").GetString() == ReplayStatus.Identical,
            $"seq {e.GetProperty("seq")} {e.GetProperty("verb")}: {e.GetProperty("status")} {e.GetProperty("reason")} {e.GetProperty("worst_field")}"));
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), entries.EnumerateArray().Select(e => e.GetProperty("seq").GetInt64()));

        CliProcess.Result wide = Validate(journal, "--parallel", "8", "--json", P("v8.json"), "--csv", P("v8.csv"));
        Assert.Equal(0, wide.ExitCode);
        Assert.Equal(serial.StdOut, wide.StdOut);
        foreach (string f in new[] { "v1.csv", "v1-field_differences.csv", "v1-breaking_changes.csv", "v1-summary.csv" })
            Assert.Equal(File.ReadAllBytes(P(f)), File.ReadAllBytes(P(f.Replace("v1", "v8", StringComparison.Ordinal))));
        JsonNode a = JsonNode.Parse(File.ReadAllText(P("v1.json")))!, b = JsonNode.Parse(File.ReadAllText(P("v8.json")))!;
        a.AsObject().Remove("args");
        b.AsObject().Remove("args");
        Assert.Equal(a.ToJsonString(), b.ToJsonString());

        // The originals were only read.
        foreach ((string f, (byte[] bytes, DateTime time)) in before)
        {
            Assert.True(File.Exists(f), f + " is gone");
            Assert.True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(f)), f + " changed");
            Assert.Equal(time, File.GetLastWriteTimeUtc(f));
        }
    }

    [Fact]
    public void T5_the_scheduler_gives_the_same_results_whatever_the_parallelism_and_completion_order()
    {
        var jobs = Enumerable.Range(0, 12).Select(i => new ReplayJob(i, i + 1, (i * 37) % 11 * 100, (i % 3) * 300L * 1024 * 1024)).ToList();
        string Run(int parallel, int seed)
        {
            var rng = new Random(seed);
            int[] delays = jobs.Select(_ => rng.Next(0, 30)).ToArray();
            var attempts = new int[jobs.Count];
            string[] results = ReplayScheduler.Run(jobs, parallel, 2L * 1024 * 1024 * 1024, job =>
            {
                Thread.Sleep(delays[job.Position]);
                int attempt = Interlocked.Increment(ref attempts[job.Position]);
                // Job 5 "crashes" whenever it runs beside others; alone (the serial retry) it completes.
                return job.Position == 5 && attempt == 1 && parallel > 1 ? "crash" : $"seq {job.Seq} ok";
            }, r => r == "crash");
            var outcomes = results.Select((r, i) => new ReplayOutcome { Seq = jobs[i].Seq, Verb = "pf", RecordedVersion = "2.1.104", Status = ReplayStatus.Identical, Reason = r }).ToList();
            return ReplayReport.ConsoleText(outcomes, "2.1.104");
        }
        string reference = Run(1, 1);
        Assert.DoesNotContain("crash", reference, StringComparison.Ordinal);
        foreach ((int parallel, int seed) in new[] { (8, 2), (3, 3), (8, 4), (12, 5) })
            Assert.Equal(reference, Run(parallel, seed));
        // Longest recorded run first, ties by sequence number.
        IReadOnlyList<int> order = ReplayScheduler.StartOrder(jobs);
        for (int k = 1; k < order.Count; k++)
            Assert.True(jobs[order[k - 1]].ElapsedMs > jobs[order[k]].ElapsedMs
                || (jobs[order[k - 1]].ElapsedMs == jobs[order[k]].ElapsedMs && jobs[order[k - 1]].Seq < jobs[order[k]].Seq));
        Assert.Equal(4, ReplayScheduler.AutoParallel(4, 64L << 30, 100));
        Assert.Equal(1, ReplayScheduler.AutoParallel(16, 100L << 20, 100));   // under one 256 MiB slot
        Assert.Equal(3, ReplayScheduler.AutoParallel(16, 64L << 30, 3));
        Assert.Equal(ReplayScheduler.MinEstimateBytes, ReplayScheduler.Estimate(1000));
        Assert.Equal(1250L << 20, ReplayScheduler.Estimate(1000L << 20));
    }

    // ---- T3 and T10 ----------------------------------------------------------------------------------------------

    private static string WithOutput(string journalLine, string file)
    {
        JournalEntry e = JournalJson.Parse(journalLine, out _)!;
        byte[] bytes = File.ReadAllBytes(file);
        foreach (JournalOutput o in e.Outputs.Where(o => o.Abs == Path.GetFullPath(file)))
        {
            o.Sha256 = Sha(bytes);
            o.Bytes = bytes.LongLength;
        }
        return JournalJson.Line(e);
    }

    [Fact]
    public void T3_T10_a_perturbed_result_is_changed_with_exact_figures_and_exit_precedence_holds()
    {
        string work = P("work");
        Directory.CreateDirectory(work);
        string journal = P("j", "journal.jsonl");
        Assert.Equal(0, JournalTestSupport.Run(work, "--journal", journal, "pf", "case14", "--json", "pf.json").ExitCode);
        string pf = Path.Combine(work, "pf.json");

        JsonNode doc = JsonNode.Parse(File.ReadAllText(pf))!;
        JsonArray buses = doc["tables"]!["buses"]!.AsArray();
        double vm = buses[3]!["vm_pu"]!.GetValue<double>();
        double va = buses[3]!["va_deg"]!.GetValue<double>();
        double p = buses[5]!["p_inj_mw"]!.GetValue<double>();
        double vmPerturbed = Math.BitIncrement(vm);
        double vaPerturbed = va * (1.0 + Math.Pow(2, -20));
        buses[3]!["vm_pu"] = vmPerturbed;
        buses[3]!["va_deg"] = vaPerturbed;
        buses[5]!["p_inj_mw"] = -p;
        File.WriteAllText(pf, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.WriteAllText(journal, WithOutput(File.ReadAllLines(journal)[0], pf) + "\n", new UTF8Encoding(false));
        JournalTestSupport.FromAnotherBuild(journal);

        CliProcess.Result r = Validate(journal, "--json", P("v.json"));
        Assert.True(r.ExitCode == 3, r.ToString());
        Assert.Contains("unexplained=1", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("UNEXPLAINED", r.StdOut, StringComparison.Ordinal);
        using JsonDocument report = JsonDocument.Parse(File.ReadAllText(P("v.json")));
        JsonElement entry = report.RootElement.GetProperty("tables").GetProperty("entries")[0];
        Assert.Equal(ReplayStatus.Changed, entry.GetProperty("status").GetString());
        Assert.True(entry.GetProperty("unexplained").GetBoolean());
        Assert.Equal("", entry.GetProperty("explained_by").GetString());
        Assert.Equal(0, entry.GetProperty("min_agreeing_bits").GetInt32());   // the sign flip is the worst field
        Assert.Equal("pf.json $.tables.buses[*].p_inj_mw", entry.GetProperty("worst_field").GetString());
        Assert.Equal(1, report.RootElement.GetProperty("summary").GetProperty("changed_unexplained").GetInt32());

        var fields = report.RootElement.GetProperty("tables").GetProperty("field_differences").EnumerateArray()
            .ToDictionary(f => f.GetProperty("field").GetString()!, f => f);
        JsonElement f1 = fields["$.tables.buses[*].vm_pu"];
        Assert.Equal(1, f1.GetProperty("max_ulps").GetInt64());
        Assert.Equal(52, f1.GetProperty("min_agreeing_bits").GetInt32());
        Assert.Equal(BitConverter.DoubleToInt64Bits(Math.Abs(vm - vmPerturbed)), BitConverter.DoubleToInt64Bits(f1.GetProperty("max_abs_diff").GetDouble()));
        JsonElement f2 = fields["$.tables.buses[*].va_deg"];
        ulong ulps2 = ResultDiff.Ulps(vaPerturbed, va);
        Assert.Equal((long)ulps2, f2.GetProperty("max_ulps").GetInt64());
        Assert.Equal(53 - (64 - System.Numerics.BitOperations.LeadingZeroCount(ulps2)), f2.GetProperty("min_agreeing_bits").GetInt32());
        Assert.Equal(BitConverter.DoubleToInt64Bits(Math.Abs(va - vaPerturbed)), BitConverter.DoubleToInt64Bits(f2.GetProperty("max_abs_diff").GetDouble()));
        Assert.Equal(BitConverter.DoubleToInt64Bits(Math.Abs(va - vaPerturbed) / Math.Max(Math.Abs(va), Math.Abs(vaPerturbed))),
            BitConverter.DoubleToInt64Bits(f2.GetProperty("max_rel_diff").GetDouble()));
        JsonElement f3 = fields["$.tables.buses[*].p_inj_mw"];
        Assert.Equal(0, f3.GetProperty("min_agreeing_bits").GetInt32());
        Assert.Equal(2 * Math.Abs(p), f3.GetProperty("max_abs_diff").GetDouble());
        Assert.Equal(3, fields.Count);

        // T10: a CHANGED entry and a CANNOT-REPLAY entry together exit 1 (the batch rule: an input error outranks a verdict).
        string gone = CopyCase14("gone.m");
        Assert.Equal(0, JournalTestSupport.Run(work, "--journal", journal, "pf", gone, "--json", "pf2.json").ExitCode);
        File.Delete(gone);
        JournalTestSupport.FromAnotherBuild(journal);
        CliProcess.Result both = Validate(journal);
        Assert.Equal(1, both.ExitCode);
        Assert.Contains("changed=1 cannot-replay=1", both.StdOut, StringComparison.Ordinal);
        Assert.Contains($"input missing: {gone}", both.StdOut, StringComparison.Ordinal);
        // Filters that name nothing are a usage error; filters pick the entries.
        Assert.Equal(2, Validate(journal, "--verb", "nminus2").ExitCode);
        Assert.Equal(2, Validate(journal, "--case", "*.nothing").ExitCode);
        Assert.Equal(3, Validate(journal, "--case", "case14").ExitCode);
        Assert.Equal(1, Validate(journal, "--case", "gone.m").ExitCode);
        Assert.Equal(1, Validate(journal, "--since", "v2.1.104-RC-6").ExitCode);
        Assert.Equal(2, Validate(journal, "--since", "2.1.999").ExitCode);
        // No journal at all is an input error.
        Assert.Equal(1, JournalTestSupport.Run(_dir, "breaking-change", "validate").ExitCode);
        Assert.Equal(1, Validate(P("nothing.jsonl")).ExitCode);
    }

    // ---- T4 ------------------------------------------------------------------------------------------------------

    [Fact]
    public void T4_a_missing_or_changed_input_cannot_be_re_run_unless_the_snapshot_store_has_it()
    {
        string work = P("work");
        Directory.CreateDirectory(work);
        string journal = P("j", "journal.jsonl");
        string plain = CopyCase14("plain.m"), kept = CopyCase14("kept.m"), edited = CopyCase14("edited.m");
        Assert.Equal(0, JournalTestSupport.Run(work, "--journal", journal, "pf", plain, "--json", "a.json").ExitCode);
        Assert.Equal(0, JournalTestSupport.Run(work, "--journal", journal, "--journal-snapshot", "pf", kept, "--json", "b.json").ExitCode);
        Assert.Equal(0, JournalTestSupport.Run(work, "--journal", journal, "pf", edited, "--json", "c.json").ExitCode);
        string shaEdited = Sha(File.ReadAllBytes(edited));
        File.Delete(plain);
        File.Delete(kept);
        File.AppendAllText(edited, "\n% an edit after the run\n");
        string shaNow = Sha(File.ReadAllBytes(edited));
        Assert.True(Directory.Exists(P("j", "objects")));

        JournalTestSupport.FromAnotherBuild(journal);
        CliProcess.Result r = Validate(journal, "--json", P("v.json"));
        Assert.True(r.ExitCode == 1, r.ToString());
        JsonElement[] e = Entries(P("v.json")).EnumerateArray().ToArray();
        Assert.Equal(ReplayStatus.CannotReplay, e[0].GetProperty("status").GetString());
        Assert.Equal($"input missing: {plain}", e[0].GetProperty("reason").GetString());
        Assert.Equal(ReplayStatus.Identical, e[1].GetProperty("status").GetString());   // re-run from the snapshot copy
        Assert.Equal(ReplayStatus.CannotReplay, e[2].GetProperty("status").GetString());
        Assert.Equal($"input changed: {edited} sha256 {shaEdited} != {shaNow}", e[2].GetProperty("reason").GetString());
    }

    [Fact]
    public void T4_an_indirect_input_named_in_a_provenance_hash_that_changed_is_not_a_change_of_result()
    {
        byte[] a = Encoding.UTF8.GetBytes("""{"provenance":{"studySha256":"aa","caseSha256":"bb","engine":"2.1.103+x"},"result":{"x":1.5}}""");
        byte[] b = Encoding.UTF8.GetBytes("""{"provenance":{"studySha256":"aa","caseSha256":"cc","engine":"2.1.104+y"},"result":{"x":1.5}}""");
        FileComparison c = ResultCompare.Compare("r.json", a, b, "transmission-security", s => s, s => s, VolatileFields.Default);
        Assert.Equal("$.provenance.caseSha256", c.IndirectInputChanged);
        // The engine alone is build identity: masked, so the same document from another build is identical.
        byte[] b2 = Encoding.UTF8.GetBytes("""{"provenance":{"studySha256":"aa","caseSha256":"bb","engine":"2.1.104+y"},"result":{"x":1.5}}""");
        FileComparison same = ResultCompare.Compare("r.json", a, b2, "transmission-security", s => s, s => s, VolatileFields.Default);
        Assert.True(same.Identical);
        Assert.Null(same.IndirectInputChanged);
        // A flattened study document in the shared result keeps its provenance as provenance_* summary keys.
        byte[] fa = Encoding.UTF8.GetBytes("""{"schema":"gridsim-cli-result/1","tool":"gridsim 1","args":[],"summary":{"provenance_study_sha256":"aa","provenance_engine":"1"},"tables":{}}""");
        byte[] fb = Encoding.UTF8.GetBytes("""{"schema":"gridsim-cli-result/1","tool":"gridsim 2","args":["x"],"summary":{"provenance_study_sha256":"ab","provenance_engine":"2"},"tables":{}}""");
        Assert.Equal("$.summary.provenance_study_sha256", ResultCompare.Compare("r.json", fa, fb, "flicker", s => s, s => s, VolatileFields.Default).IndirectInputChanged);
        // A recorded schema tag this build no longer writes for the file cannot be compared.
        byte[] old = Encoding.UTF8.GetBytes("""{"schema":"gridsim-cli-result/0","summary":{}}""");
        Assert.Equal("gridsim-cli-result/0", ResultCompare.Compare("r.json", old, fa, "pf", s => s, s => s, VolatileFields.Default).SchemaNotReadable);
    }

    [Fact]
    public void The_comparer_counts_bits_csv_columns_and_bytes_exactly()
    {
        Assert.Equal(53, ResultCompare.AgreeingBits(0));
        Assert.Equal(52, ResultCompare.AgreeingBits(1));
        Assert.Equal(51, ResultCompare.AgreeingBits(2));
        Assert.Equal(51, ResultCompare.AgreeingBits(3));
        Assert.Equal(1, ResultCompare.AgreeingBits((1UL << 52) - 1));
        Assert.Equal(0, ResultCompare.AgreeingBits(1UL << 52));
        Assert.Equal(0, ResultCompare.AgreeingBits(ulong.MaxValue));
        byte[] a = Encoding.UTF8.GetBytes("bus,vm_pu,name\n1,1.0,\"a,b\"\n2,0.5,x\n");
        byte[] b = Encoding.UTF8.GetBytes("bus,vm_pu,name\n1,1.0000000000000002,\"a,b\"\n2,0.5,y\n");
        FileComparison c = ResultCompare.Compare("t.csv", a, b, "pf", s => s, s => s, VolatileFields.Default);
        Assert.Equal(2, c.Fields.Count);
        Assert.Equal(("vm_pu", "number", 52, 1UL), (c.Fields[0].Field, c.Fields[0].Kind, c.Fields[0].MinAgreeingBits, c.Fields[0].MaxUlps!.Value));
        Assert.Equal(("name", "string"), (c.Fields[1].Field, c.Fields[1].Kind));
        FileComparison k = ResultCompare.Compare("t-summary.csv", Encoding.UTF8.GetBytes("key,value\nverb,pf\nloss_mw,1.5\n"),
            Encoding.UTF8.GetBytes("key,value\nverb,pf\nloss_mw,-1.5\n"), "pf", s => s, s => s, VolatileFields.Default);
        Assert.Equal(("loss_mw", 0, 3.0), (k.Fields.Single().Field, k.Fields.Single().MinAgreeingBits, k.Fields.Single().MaxAbsDiff!.Value));
        FileComparison m = ResultCompare.Compare("case.m", Encoding.UTF8.GetBytes("a"), Encoding.UTF8.GetBytes("b"), "reduce", s => s, s => s, VolatileFields.Default);
        Assert.Equal("bytes", m.Fields.Single().Kind);
        // NaN against a number agrees in no bit; a path string maps to its token placeholder on each side.
        FileComparison n = ResultCompare.Compare("r.json", Encoding.UTF8.GetBytes("""{"x":1.0,"p":"/a/in.m"}"""), Encoding.UTF8.GetBytes("""{"x":"NaN","p":"/s/in/1/in.m"}"""),
            "pf", s => s == "/a/in.m" ? "<input:1>" : s, s => s == "/s/in/1/in.m" ? "<input:1>" : s, VolatileFields.Default);
        Assert.Equal(0, n.Fields.Single().MinAgreeingBits);
        Assert.Equal("$.x", n.Fields.Single().Field);
    }

    // ---- T6 ------------------------------------------------------------------------------------------------------

    private string RealOutput(string name)
    {
        string f = P("rec", name);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, "{}");
        return f;
    }

    private string Crafted(params JournalEntry[] entries)
    {
        string j = P("crafted", "journal.jsonl");
        foreach (JournalEntry e in entries) JournalFile.Append(j, seq => { e.Seq = seq; return JournalJson.Line(e); });
        return j;
    }

    private JournalEntry Craft(string verb, string[] argv, string? cwdRel = null)
    {
        string out1 = RealOutput(verb + "-" + Guid.NewGuid().ToString("N")[..6] + ".json");
        return new JournalEntry
        {
            RecordedUtc = JournalJson.Utc(DateTime.UtcNow), Version = "2.1.090", InformationalVersion = "2.1.090+aa", Commit = "aa",
            Verb = verb, Argv = argv.ToList(), Cwd = _dir, ReplayClass = "safe",
            Outputs = [new JournalOutput { Abs = out1, Rel = out1, CwdRel = cwdRel, Sha256 = Sha(File.ReadAllBytes(out1)), Bytes = 2 }],
        };
    }

    [Fact]
    public void T6_confinement_and_the_never_list_refuse_by_name()
    {
        // Network and device paths: exit 2, for validate and for recording.
        Assert.Equal(2, Validate(@"\\server\share\journal.jsonl").ExitCode);
        Assert.Equal(2, Validate(@"\\.\C:\journal.jsonl").ExitCode);
        string j0 = Crafted(Craft("pf", ["pf", "case9"], "x.json"));
        Assert.Equal(2, Validate(j0, "--scratch", @"\\server\share\scratch").ExitCode);
        Assert.Equal(2, Validate(j0, "--scratch", P("crafted", "inside")).ExitCode);   // inside the journal's folder
        string notEmpty = P("full");
        Directory.CreateDirectory(notEmpty);
        File.WriteAllText(Path.Combine(notEmpty, "f"), "x");
        Assert.Equal(2, Validate(j0, "--scratch", notEmpty).ExitCode);
        Assert.Equal(2, JournalTestSupport.Run(_dir, "--journal", @"\\server\share\j.jsonl", "pf", "case9").ExitCode);

        JournalTestSupport.Delete(P("crafted"));
        string journal = Crafted(
            Craft("pf", ["pf", "case9"], "../../escape.json"),                          // a default output that climbs out
            Craft("serve", ["serve", "case9"]),
            Craft("synth", ["synth"]),
            Craft("dcpf", ["dcpf", "case9", "--audit-forward", "siem.jsonl"]),
            Craft("convert", ["convert", "case9", "c.m"]),
            Craft("no-such-verb", ["no-such-verb"]),
            Craft("pf", ["pf", "case9"]));                                              // an output the command line does not name
        CliProcess.Result r = Validate(journal, "--json", P("v.json"));
        Assert.True(r.ExitCode == 1, r.ToString());
        string[] reasons = Entries(P("v.json")).EnumerateArray().Select(e => e.GetProperty("status").GetString() + ": " + e.GetProperty("reason").GetString()).ToArray();
        Assert.Equal(new[]
        {
            "CANNOT-REPLAY: refused: writes outside the scratch folder",
            "CANNOT-REPLAY: not replayable: a live or network verb",
            "CANNOT-REPLAY: not replayable: it writes the bundled data",
            "CANNOT-REPLAY: not replayable: --audit-forward writes to an external log",
            "CANNOT-REPLAY: not replayable: it writes its converted case to a positional path, outside the output flags",
            "CANNOT-REPLAY: verb removed",
        }, reasons.Take(6));
        Assert.StartsWith("CANNOT-REPLAY: not replayable: an output was written outside the paths the command line names", reasons[6], StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "escape.json")));
    }

    [Fact]
    public void T6_a_snapshot_object_reached_through_a_link_is_refused()
    {
        string objects = P("store", "objects");
        Directory.CreateDirectory(objects);
        string outside = P("elsewhere");
        Directory.CreateDirectory(outside);
        byte[] bytes = Encoding.UTF8.GetBytes("payload");
        string sha = Sha(bytes);
        File.WriteAllBytes(Path.Combine(outside, sha[2..]), bytes);
        string link = Path.Combine(objects, sha[..2]);
        // 2.1.130: no silent early return. A user may always make a symbolic link on Linux and macOS, so a failure there
        // fails the test. On Windows a symbolic link needs Developer Mode or elevation; a junction needs neither, so it is
        // made instead, and a junction that cannot be made fails the test too.
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && OperatingSystem.IsWindows())
        {
            using var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            mk.WaitForExit();
            Assert.True(Directory.Exists(link), "neither a symbolic link nor a junction could be made, so the refusal cannot be checked");
        }
        Assert.Null(JournalSnapshot.Get(objects, sha, out string? why));
        Assert.Equal("snapshot object refused: it passes through a symbolic link or junction", why);
        // The same object stored in place is read back.
        string plain = P("store2", "objects");
        Assert.Null(JournalSnapshot.Put(plain, sha, bytes));
        Assert.Null(JournalSnapshot.Put(plain, sha, bytes));   // already there: not written again
        Assert.Equal(bytes, JournalSnapshot.Get(plain, sha, out _));
        Assert.True(new FileInfo(JournalSnapshot.ObjectPath(plain, sha)).IsReadOnly);
    }

    // ---- skipped and import --------------------------------------------------------------------------------------

    [Fact]
    public void An_entry_recorded_by_this_build_is_skipped_and_not_re_run()
    {
        string work = P("work");
        Directory.CreateDirectory(work);
        string journal = P("j", "journal.jsonl");
        Assert.Equal(0, JournalTestSupport.Run(work, "--journal", journal, "pf", "case9", "--json", "pf.json").ExitCode);
        CliProcess.Result r = Validate(journal);
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("SKIPPED-SAME-BUILD", r.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("[1/1]", r.StdErr, StringComparison.Ordinal);   // nothing was re-run
        // The same version with another commit (a local or dev build) is re-run.
        JournalTestSupport.FromAnotherBuild(journal);
        CliProcess.Result again = Validate(journal);
        Assert.Equal(0, again.ExitCode);
        Assert.Contains("seq 1 pf (", again.StdOut, StringComparison.Ordinal);
        Assert.Contains("): IDENTICAL", again.StdOut, StringComparison.Ordinal);
        Assert.Contains("[1/1] seq 1 done", again.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_builds_entries_from_result_files_and_they_re_run()
    {
        string work = P("work");
        Directory.CreateDirectory(work);
        string c14 = CopyCase14("imp.m"), lost = CopyCase14("lost.m");
        Assert.Equal(0, JournalTestSupport.Run(work, "pf", "case14", "--json", "kept/pf.json").ExitCode);
        Assert.Equal(0, JournalTestSupport.Run(work, "dcpf", c14, "--json", "kept/dcpf.json").ExitCode);
        Assert.Equal(0, JournalTestSupport.Run(work, "losses", lost, "--json", "lost/losses.json").ExitCode);
        // Make them look recorded by RC-4 (2.1.073): the tool line is build identity, not a result.
        foreach (string f in new[] { "kept/pf.json", "kept/dcpf.json", "lost/losses.json" })
        {
            string path = Path.Combine(work, f);
            File.WriteAllText(path, System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(path), "\"tool\": \"[^\"]*\"", "\"tool\": \"gridsim 2.1.073+0123abcd\""));
        }
        File.WriteAllText(Path.Combine(work, "kept", "not-a-result.json"), "{\"x\":1}");
        string journal = P("j", "journal.jsonl");
        CliProcess.Result imp = JournalTestSupport.Run(work, "breaking-change", "import", "kept", "--journal", journal);
        Assert.True(imp.ExitCode == 0, imp.ToString());
        Assert.Contains("2 result file(s) imported", imp.StdOut, StringComparison.Ordinal);
        Assert.Contains("1 file(s) skipped", imp.StdOut, StringComparison.Ordinal);
        File.Delete(lost);   // gone after the losses run: that entry cannot be re-run
        CliProcess.Result imp2 = JournalTestSupport.Run(work, "breaking-change", "import", Path.Combine("lost", "losses.json"), "--journal", journal);
        Assert.True(imp2.ExitCode == 0, imp2.ToString());
        Assert.Contains("1 input(s) missing", imp2.StdOut, StringComparison.Ordinal);

        IReadOnlyList<JournalLine> lines = JournalFile.Read(journal);
        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.Equal("inputs hashed at import, not at run", l.Entry!.Imported));
        Assert.Equal("2.1.073", lines[0].Entry!.Version);
        Assert.Equal(work, lines[0].Entry!.Cwd);

        CliProcess.Result v = Validate(journal, "--json", P("v.json"));
        Assert.True(v.ExitCode == 1, v.ToString());
        JsonElement[] e = Entries(P("v.json")).EnumerateArray().ToArray();
        Assert.Equal(new[] { ReplayStatus.Identical, ReplayStatus.Identical, ReplayStatus.CannotReplay }, e.Select(x => x.GetProperty("status").GetString()));
        Assert.Equal("input missing: " + lost, e[2].GetProperty("reason").GetString());
        Assert.Equal(1, JournalTestSupport.Run(work, "breaking-change", "import", "missing-folder", "--journal", journal).ExitCode);
    }
}
