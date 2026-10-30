using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// DOC-8 (October 2026): the command-line side of the exports-and-formats findings (stream 8 of
/// review-progress/2026-10/DOCS-FINDINGS-TRIAGE.md), run against the real gridsim binary (CliProcess.RunBuilt), checking
/// what a script sees: the exit code, the files written and the message on stderr. Every test is new.
/// </summary>
public sealed class CliDoc8ProcessTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-doc8-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Delete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void AssertExit(int expected, CliProcess.Result r)
    {
        Assert.True(r.ExitCode == expected, $"expected exit {expected}\n{r}");
        Assert.DoesNotContain("internal error", r.StdErr, StringComparison.Ordinal);
    }

    private static CliProcess.Result Bench(string outPath, params string[] extra) =>
        CliProcess.RunBuilt(new[] { "bench", "--cases", "case9", "--runs", "1", "--parallel", "1", "--out", outPath }.Concat(extra).ToArray());

    // ---- D-DOC-057 / 059 / 227: bench metadata, warm-up, CPU name ----------------------------------------------------

    /// <summary>
    /// D-DOC-057: two bench reports in one folder each keep their own metadata (&lt;report&gt;.run-metadata.json); no shared
    /// run-metadata.json is written. D-DOC-059: the report says each cell is warmed up, and the metadata records it.
    /// </summary>
    [Fact]
    public void D057_D059_two_reports_in_one_folder_keep_their_own_metadata_and_say_they_are_warmed_up()
    {
        string dir = TempDir();
        try
        {
            CliProcess.Result a = Bench(Path.Combine(dir, "first.md"), "--note", "run A");
            AssertExit(0, a);
            CliProcess.Result b = Bench(Path.Combine(dir, "second.md"), "--note", "run B");
            AssertExit(0, b);

            Assert.False(File.Exists(Path.Combine(dir, "run-metadata.json")), "the shared sidecar is no longer written");
            using JsonDocument ma = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "first.run-metadata.json")));
            using JsonDocument mb = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "second.run-metadata.json")));
            Assert.Contains("note=\"run A\"", ma.RootElement.GetProperty("run").GetString(), StringComparison.Ordinal);
            Assert.Contains("note=\"run B\"", mb.RootElement.GetProperty("run").GetString(), StringComparison.Ordinal);

            JsonElement p = ma.RootElement.GetProperty("parameters");
            Assert.Equal(Path.Combine(dir, "first.md"), p.GetProperty("report").GetString());
            Assert.Equal("case9", Assert.Single(p.GetProperty("cases").EnumerateArray()).GetString());
            Assert.Equal(1, p.GetProperty("warmupSolvesPerCell").GetInt32());
            Assert.Equal(new[] { "cpu-dense", "cpu-sparse", "bfs" }, p.GetProperty("engines").EnumerateArray().Select(e => e.GetString()));

            Assert.Contains("one untimed warm-up solve", a.StdOut, StringComparison.Ordinal);
            Assert.Contains("one untimed warm-up solve", File.ReadAllText(Path.Combine(dir, "first.md")), StringComparison.Ordinal);
        }
        finally { Delete(dir); }
    }

    /// <summary>D-DOC-227: on Windows the report header names the processor, not just the architecture.</summary>
    // GR-877: skipped (not passed) off Windows; was an early return reported as a pass.
    [WindowsFact("Linux already names the processor; the Windows path is what this checks")]
    public void D227_the_header_names_the_processor_on_windows()
    {
        string dir = TempDir();
        try
        {
            CliProcess.Result r = Bench(Path.Combine(dir, "bench.md"));
            AssertExit(0, r);
            string cpuLine = r.StdOut.Split('\n').Single(l => l.TrimStart().StartsWith("cpu:", StringComparison.Ordinal));
            string model = cpuLine.Trim()["cpu:".Length..].Split("  -  ")[0].Trim();
            Assert.NotEqual(RuntimeInformation.ProcessArchitecture.ToString(), model);
            Assert.False(string.IsNullOrWhiteSpace(model), cpuLine);
            using JsonDocument meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "bench.run-metadata.json")));
            Assert.Equal(model, meta.RootElement.GetProperty("cpu").GetString());
        }
        finally { Delete(dir); }
    }

    // ---- D-DOC-058: bench --gpu=<vendor> ----------------------------------------------------------------------------

    /// <summary>
    /// D-DOC-058: --gpu=cuda probes CUDA only. Whatever the machine has, ROCm and OpenCL are not probed (no "no usable"
    /// line for them, no report line about them), and the metadata records the request.
    /// </summary>
    [Fact]
    public void D058_bench_gpu_vendor_probes_that_vendor_only()
    {
        string dir = TempDir();
        try
        {
            CliProcess.Result r = Bench(Path.Combine(dir, "bench.md"), "--gpu=cuda");
            AssertExit(0, r);
            Assert.DoesNotContain("no usable rocm", r.StdErr, StringComparison.Ordinal);
            Assert.DoesNotContain("no usable opencl", r.StdErr, StringComparison.Ordinal);
            Assert.DoesNotContain("_ROCm/AMD:", r.StdOut, StringComparison.Ordinal);
            Assert.DoesNotContain("_OpenCL:", r.StdOut, StringComparison.Ordinal);
            Assert.DoesNotContain("rocm-", r.StdOut, StringComparison.Ordinal);
            Assert.DoesNotContain("opencl-", r.StdOut, StringComparison.Ordinal);
            using JsonDocument meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "bench.run-metadata.json")));
            Assert.Equal("cuda", meta.RootElement.GetProperty("parameters").GetProperty("gpuRequested").GetString());
            // With no CUDA device the report says so, naming the request as given.
            if (r.StdErr.Contains("no usable cuda", StringComparison.Ordinal))
                Assert.Contains("_CUDA: `--gpu=cuda` requested but no device answered._", r.StdOut, StringComparison.Ordinal);
        }
        finally { Delete(dir); }
    }

    /// <summary>D-DOC-058: --gpu=cpu adds no GPU engine and probes no GPU at all.</summary>
    [Fact]
    public void D058_bench_gpu_cpu_probes_no_gpu()
    {
        string dir = TempDir();
        try
        {
            CliProcess.Result r = Bench(Path.Combine(dir, "bench.md"), "--gpu=cpu");
            AssertExit(0, r);
            Assert.DoesNotContain("[gpu] no usable", r.StdErr, StringComparison.Ordinal);
            Assert.Contains("_GPU engines not requested", r.StdOut, StringComparison.Ordinal);
            using JsonDocument meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "bench.run-metadata.json")));
            Assert.Equal(3, meta.RootElement.GetProperty("parameters").GetProperty("engines").GetArrayLength());
        }
        finally { Delete(dir); }
    }

    // ---- D-DOC-066: screen on a case with no inertia ------------------------------------------------------------------

    /// <summary>
    /// D-DOC-066: case14 carries no generator inertia (H = 0 everywhere). The screen used to print every loss at about
    /// -5810 Hz/s (the 1 MWs floor's artefact) and write it to the CSV; it now refuses, exit 1, and writes no CSV.
    /// </summary>
    [Fact]
    public void D066_screen_refuses_a_case_without_inertia_and_writes_no_csv()
    {
        string dir = TempDir();
        try
        {
            string csv = Path.Combine(dir, "screen.csv");
            CliProcess.Result r = CliProcess.RunBuilt("screen", "case14", "--csv", csv);
            AssertExit(1, r);
            Assert.Contains("error: screen: case 'IEEE 14-bus' carries no inertia data", r.StdErr, StringComparison.Ordinal);
            Assert.DoesNotContain("-5810", r.StdOut, StringComparison.Ordinal);
            Assert.DoesNotContain("Verdict", r.StdOut, StringComparison.Ordinal);
            Assert.False(File.Exists(csv));
        }
        finally { Delete(dir); }
    }

    // ---- D-DOC-176: --export into a missing folder --------------------------------------------------------------------

    [Fact]
    public void D176_trip_export_creates_a_missing_folder()
    {
        string dir = TempDir();
        try
        {
            string csv = Path.Combine(dir, "not", "yet", "trip.csv");
            CliProcess.Result r = CliProcess.RunBuilt("trip", "--export", csv);
            Assert.True(r.ExitCode is 0 or 3, r.ToString());   // the trip's own verdict; the point is the file
            Assert.DoesNotContain("internal error", r.StdErr, StringComparison.Ordinal);
            Assert.True(File.Exists(csv), r.ToString());
            Assert.Equal("dtm,f", File.ReadLines(csv).First());
        }
        finally { Delete(dir); }
    }

    [Fact]
    public void D176_stability_export_creates_a_missing_folder()
    {
        string dir = TempDir();
        try
        {
            string json = Path.Combine(dir, "deep", "folder", "classical.json");
            CliProcess.Result r = CliProcess.RunBuilt("stability", "--export", json);
            Assert.True(r.ExitCode is 0 or 3, r.ToString());
            Assert.DoesNotContain("internal error", r.StdErr, StringComparison.Ordinal);
            Assert.True(File.Exists(json), r.ToString());
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(json));
            Assert.Equal("gridsim-classical-export/1", doc.RootElement.GetProperty("schema").GetString());
        }
        finally { Delete(dir); }
    }

    // ---- D-DOC-072: one overwrite policy ------------------------------------------------------------------------------

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>
    /// D-DOC-072: an output that names a file of the case folder the run reads is refused, exit 2, and the case file is
    /// left exactly as it was (before, screen --csv wrote over it).
    /// </summary>
    [Fact]
    public void D072_an_output_never_replaces_a_file_of_the_case_being_read()
    {
        string dir = TempDir();
        try
        {
            string caseDir = Path.Combine(dir, "uk-copy");
            CopyFolder(Path.Combine(CliProcess.Root(), "data", "uk"), caseDir);
            string target = Path.Combine(caseDir, "buses.json");
            string before = Sha(target);
            CliProcess.Result r = CliProcess.RunBuilt("screen", caseDir, "--csv", target);
            AssertExit(2, r);
            Assert.Contains("refusing to write the screen CSV", r.StdErr, StringComparison.Ordinal);
            Assert.Contains("case folder", r.StdErr, StringComparison.Ordinal);
            Assert.Equal(before, Sha(target));
        }
        finally { Delete(dir); }
    }

    /// <summary>D-DOC-072: an output named on the command line that already exists (an earlier run's) is replaced.</summary>
    [Fact]
    public void D072_an_existing_output_is_replaced_by_a_re_run()
    {
        string dir = TempDir();
        try
        {
            string csv = Path.Combine(dir, "screen.csv");
            File.WriteAllText(csv, "stale");
            CliProcess.Result r = CliProcess.RunBuilt("screen", "uk", "--csv", csv);
            Assert.True(r.ExitCode is 0 or 3, r.ToString());
            Assert.StartsWith("name,lost_mw,", File.ReadAllText(csv), StringComparison.Ordinal);
        }
        finally { Delete(dir); }
    }

    /// <summary>D-DOC-072: a report path that is an existing folder is refused, exit 2, before the bench runs.</summary>
    [Fact]
    public void D072_a_folder_is_refused_as_an_output_file()
    {
        string dir = TempDir();
        try
        {
            string folder = Path.Combine(dir, "a-folder");
            Directory.CreateDirectory(folder);
            CliProcess.Result r = Bench(folder);
            AssertExit(2, r);
            Assert.Contains("it is an existing folder, not a file", r.StdErr, StringComparison.Ordinal);
            Assert.DoesNotContain("## cpu-dense", r.StdOut, StringComparison.Ordinal);   // refused before the matrix ran
        }
        finally { Delete(dir); }
    }
}
