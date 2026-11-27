using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// 2.1.154: the global switch --timings, on the real gridsim binary.
///
/// With the switch off every output is byte-identical to what it was: the same stdout, the same stderr and the same
/// result file, with no "timings" property. With it on, stdout and the result file are the same bytes apart from the
/// "timings" block at the end of the document, stderr is the same text with a timing table after it, and the block is
/// well formed: non-negative figures, the stage names, and the stages a verb could not separate named as merged.
/// </summary>
public sealed class StageTimingsProcessTests
{
    // Verbs that write a result document, with an argument line that runs them on a bundled case. The exit code is the
    // verb's own (nminus1 on case9 reports an insecure outage: 3).
    public static readonly TheoryData<string, int, string> JsonVerbs = new()
    {
        { "pf", 0, "pf case9" },
        { "dcpf", 0, "dcpf case9" },
        { "dcopf", 0, "dcopf case9" },
        { "acopf", 0, "acopf case9" },
        { "cpf", 0, "cpf case9" },
        { "nminus1", 3, "nminus1 case9" },
        { "iec60909", 0, "iec60909 case9 --bus 5" },
    };

    private static string TempDir()
    {
        string d = TestTemp.Under("gridsim-stg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // The "timings" property is the last one of the document: ,\n  "timings": { ... \n  }\n}
    private static readonly Regex TimingsBlock = new(@",\r?\n  ""timings"": \{.*?\r?\n  \}(?=\r?\n\}\s*$)", RegexOptions.Singleline);

    [Theory]
    [MemberData(nameof(JsonVerbs))]
    public void Switch_off_changes_nothing_and_on_adds_only_the_timings_block(string name, int exitCode, string line)
    {
        string dir = TempDir();
        try
        {
            string json = Path.Combine(dir, name + ".json");
            string[] verbArgs = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            CliProcess.Result off = CliProcess.RunBuilt(verbArgs.Concat(new[] { "--json", json }).ToArray());
            Assert.True(off.ExitCode == exitCode, off.ToString());
            string offText = File.ReadAllText(json);
            File.Delete(json);
            Assert.DoesNotContain("\"timings\"", offText, StringComparison.Ordinal);
            Assert.DoesNotContain("timings (wall-clock", off.StdErr, StringComparison.Ordinal);

            CliProcess.Result on = CliProcess.RunBuilt(verbArgs.Concat(new[] { "--json", json, "--timings" }).ToArray());
            Assert.True(on.ExitCode == exitCode, on.ToString());
            string onText = File.ReadAllText(json);

            // stdout: the same bytes. result file: the same bytes once the timings block is taken out. The switch is
            // not part of the recorded argument list.
            Assert.Equal(off.StdOut, on.StdOut);
            Assert.Matches(TimingsBlock, onText);
            Assert.Equal(offText, TimingsBlock.Replace(onText, ""));
            // stderr: the same text, then the timing table.
            Assert.StartsWith(off.StdErr, on.StdErr, StringComparison.Ordinal);
            Assert.StartsWith("timings (wall-clock ms):", on.StdErr[off.StdErr.Length..], StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("stability case9")]
    [InlineData("ptdf case9 --bus 5")]
    public void Switch_off_changes_nothing_on_verbs_without_a_result_file(string line)
    {
        string[] verbArgs = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        CliProcess.Result off = CliProcess.RunBuilt(verbArgs);
        CliProcess.Result on = CliProcess.RunBuilt(verbArgs.Concat(new[] { "--timings" }).ToArray());
        Assert.Equal(off.ExitCode, on.ExitCode);
        Assert.Equal(off.StdOut, on.StdOut);
        Assert.StartsWith(off.StdErr, on.StdErr, StringComparison.Ordinal);
        Assert.StartsWith("timings (wall-clock ms):", on.StdErr[off.StdErr.Length..], StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(JsonVerbs))]
    public void Timings_block_is_well_formed(string name, int exitCode, string line)
    {
        string dir = TempDir();
        try
        {
            string json = Path.Combine(dir, name + ".json");
            CliProcess.Result r = CliProcess.RunBuilt(line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Concat(new[] { "--json", json, "--timings" }).ToArray());
            Assert.True(r.ExitCode == exitCode, r.ToString());
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(json));
            JsonElement root = doc.RootElement;
            Assert.Equal("gridsim-cli-result/1", root.GetProperty("schema").GetString());
            Assert.DoesNotContain("--timings", root.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
            JsonElement t = root.GetProperty("timings");
            Assert.Equal("ms", t.GetProperty("unit").GetString());

            var known = new[] { "startup", "parse", "build", "assemble", "factor", "solve", "write", "total" };
            double sum = 0;
            foreach (string stage in new[] { "startup", "parse", "write", "total" })
                Assert.True(t.TryGetProperty(stage, out _), $"{name}: no {stage}");
            foreach (string stage in known)
            {
                if (!t.TryGetProperty(stage, out JsonElement v)) continue;
                double ms = v.GetDouble();
                Assert.True(ms >= 0 && double.IsFinite(ms), $"{name}: {stage} = {ms}");
                if (stage != "total") sum += ms;
            }
            double total = t.GetProperty("total").GetDouble();
            Assert.True(total > 0, $"{name}: total {total}");
            Assert.True(sum <= total + 0.01, $"{name}: the stages add to {sum} ms, above the total {total} ms");

            // Every covered verb reports a solve stage, and every measurable stage is either reported, merged or listed as
            // not measured, exactly one of the three.
            Assert.True(t.TryGetProperty("solve", out _), $"{name}: no solve stage");
            string[] merged = t.GetProperty("stages_merged").EnumerateArray().Select(e => e.GetString()!).ToArray();
            string[] missing = t.GetProperty("stages_not_measured").EnumerateArray().Select(e => e.GetString()!).ToArray();
            foreach (string stage in new[] { "parse", "build", "assemble", "factor" })
            {
                int places = (t.TryGetProperty(stage, out _) ? 1 : 0) + (merged.Contains(stage) ? 1 : 0) + (missing.Contains(stage) ? 1 : 0);
                Assert.True(places == 1, $"{name}: stage {stage} is in {places} places");
            }
            Assert.DoesNotContain("solve", missing);
            // The Newton and OPF engines do not separate assemble / factor / solve without reaching into the kernel.
            if (name is "pf" or "dcpf")
                Assert.Equal(new[] { "build", "assemble", "factor", "solve" }, merged);
            // pf reports the iteration count of its one solve, the same number its own summary holds.
            if (name == "pf")
            {
                Assert.Equal(root.GetProperty("summary").GetProperty("iterations").GetInt64(), t.GetProperty("iterations").GetInt64());
                Assert.True(t.GetProperty("iterations").GetInt64() > 0);
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_verb_with_no_stage_instrumentation_lists_what_it_did_not_measure()
    {
        string dir = TempDir();
        try
        {
            string json = Path.Combine(dir, "ptdf.json");
            CliProcess.Result r = CliProcess.RunBuilt("ptdf", "case9", "--bus", "5", "--json", json, "--timings");
            Assert.Equal(0, r.ExitCode);
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(json));
            JsonElement t = doc.RootElement.GetProperty("timings");
            Assert.False(t.TryGetProperty("solve", out _));
            Assert.Contains("solve", t.GetProperty("stages_not_measured").EnumerateArray().Select(e => e.GetString()));
            Assert.True(t.GetProperty("total").GetDouble() >= 0);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Timing_table_on_stderr_names_the_stages()
    {
        CliProcess.Result r = CliProcess.RunBuilt("pf", "case9", "--timings");
        Assert.Equal(0, r.ExitCode);
        string table = r.StdErr[r.StdErr.IndexOf("timings (wall-clock ms):", StringComparison.Ordinal)..];
        foreach (string stage in new[] { "startup", "parse", "solve", "write", "total", "iterations" })
            Assert.Contains(stage, table, StringComparison.Ordinal);
        Assert.Contains("combined:", table, StringComparison.Ordinal);
    }
}
