using System;
using System.IO;
using System.Text.Json.Nodes;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-886: run-to-run determinism across PROCESSES, not only within one test process. The same solve export, written
/// by two separate CLI runs, is identical in every byte of its numerical content; only the meta block (the run's UTC
/// time, elapsed milliseconds and machine) may differ. Doubles are written round-trip, so equal text is equal bits.
/// </summary>
public sealed class CrossProcessDeterminismProcessTests
{
    private static string WithoutMeta(string json)
    {
        JsonNode root = JsonNode.Parse(json) ?? throw new InvalidDataException("empty export");
        ((JsonObject)root).Remove("meta");
        return root.ToJsonString();
    }

    [Theory]
    [InlineData("case9")]
    [InlineData("case118")]
    public void Two_cli_runs_write_the_same_solve_export_apart_from_meta(string caseName)
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-xproc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string a = Path.Combine(dir, "a.json"), b = Path.Combine(dir, "b.json");
            CliProcess.Result ra = CliProcess.RunBuilt(caseName, "--enableExport=" + a);
            Assert.True(ra.ExitCode == 0, ra.ToString());
            CliProcess.Result rb = CliProcess.RunBuilt(caseName, "--enableExport=" + b);
            Assert.True(rb.ExitCode == 0, rb.ToString());
            Assert.Equal(WithoutMeta(File.ReadAllText(a)), WithoutMeta(File.ReadAllText(b)));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
