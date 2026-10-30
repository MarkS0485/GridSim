using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// CF6 (N363): the CLI export of an RMS scheduled-event study (<c>stability --rms --events ... --events-out &lt;dir&gt;</c>):
/// machine (and converter) trajectories as CSV with the run header and a provenance line, a
/// gridsim-rms-events-export/1 summary, run-metadata.json; refused without --events; the run's verdict unchanged.
/// </summary>
public sealed class Cf6RmsEventsExportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-cf6-rmsx-" + Guid.NewGuid().ToString("N"));

    public Cf6RmsEventsExportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static MultiMachineSubtransient.Result TinyRun(int samples = 3, bool stable = true)
    {
        MultiMachineSubtransient.MachineTrace Trace(double offset)
        {
            double[] t = Enumerable.Range(0, samples).Select(k => 0.005 * k).ToArray();
            double[] v = t.Select(x => x + offset).ToArray();
            return new MultiMachineSubtransient.MachineTrace(t, v, v, v, v, v, v);
        }
        return new MultiMachineSubtransient.Result(new[] { Trace(0.1), Trace(0.2) }, stable);
    }

    private static readonly IReadOnlyList<RmsEvent> Events = new RmsEvent[] { new RmsFaultClear(0.2, 7), new RmsBusFault(0.1, 7) };

    [Fact]
    public void The_machines_csv_carries_the_header_the_provenance_and_every_sample_exactly()
    {
        MultiMachineSubtransient.Result run = TinyRun();
        string csv = RmsEventsExport.MachinesCsvText(run, new[] { 1, 2 }, 0, new[] { "GridSim  -  test", "run:     x" }, "INDICATIVE note");
        string[] lines = csv.Split('\n');
        Assert.Equal("# GridSim  -  test", lines[0]);
        Assert.Equal("# run:     x", lines[1]);
        Assert.StartsWith("# provenance: Synthetic (a model trajectory", lines[2], StringComparison.Ordinal);
        Assert.EndsWith("INDICATIVE note", lines[2], StringComparison.Ordinal);
        Assert.Equal(RmsEventsExport.MachinesHeader, lines[3]);
        string[] rows = lines.Skip(4).Where(l => l.Length > 0).ToArray();
        Assert.Equal(3 * 2, rows.Length);
        // Every number reads back to the same bits (shortest round-trip form).
        string[] r1 = rows[1].Split(',');
        Assert.Equal("1", r1[1]);
        Assert.Equal("2", r1[2]);
        Assert.Equal(BitConverter.DoubleToInt64Bits(run.Machines[1].DeltaRad[0]),
            BitConverter.DoubleToInt64Bits(double.Parse(r1[3], System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Null(RmsEventsExport.ConvertersCsvText(run, new[] { 1, 2 }, 0, Array.Empty<string>(), "n"));
    }

    [Fact]
    public void Island_references_are_marked_and_misaligned_labels_are_refused()
    {
        MultiMachineSubtransient.Result run = TinyRun();
        string csv = RmsEventsExport.MachinesCsvText(run, new[] { 1, 5 }, 1, Array.Empty<string>(), "n");
        Assert.Contains("# traces 1 to 1 are synthetic island references", csv, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => RmsEventsExport.MachinesCsvText(run, new[] { 1 }, 0, Array.Empty<string>(), "n"));
        Assert.Throws<ArgumentOutOfRangeException>(() => RmsEventsExport.MachinesCsvText(run, new[] { 1, 2 }, 3, Array.Empty<string>(), "n"));
    }

    [Fact]
    public void The_summary_lists_the_events_in_time_order_with_the_verdict_and_provenance()
    {
        string json = RmsEventsExport.SummaryJsonText(TinyRun(stable: false), new[] { 1, 2 }, 0, Events, "WSCC-9", 60.0, 0.005, 3.2, true, "INDICATIVE");
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement r = doc.RootElement;
        Assert.Equal("gridsim-rms-events-export/1", r.GetProperty("schema").GetString());
        Assert.Equal("WSCC-9", r.GetProperty("case").GetString());
        JsonElement[] evs = r.GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal(new[] { 0.1, 0.2 }, evs.Select(e => e.GetProperty("atSeconds").GetDouble()));
        Assert.Equal(1, evs[0].GetProperty("index").GetInt32());   // the input index is kept
        Assert.False(r.GetProperty("verdict").GetProperty("stayedInSynchronism").GetBoolean());
        Assert.Equal("Synthetic", r.GetProperty("provenance").GetProperty("tag").GetString());
        Assert.True(r.GetProperty("indicative").GetBoolean());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("files").GetProperty("converters").ValueKind);
        Assert.Throws<ArgumentException>(() => RmsEventsExport.SummaryJsonText(TinyRun(), new[] { 1, 2 }, 0, Array.Empty<RmsEvent>(), "c", 50, 0.005, 3, true, "n"));
    }

    [Fact]
    public void Cli_events_out_writes_the_export_and_keeps_the_verdict()
    {
        string outDir = Path.Combine(_dir, "x");
        string[] args = { "stability", "case9", "--rms", "--events", "fault:7@0.1,clear:7@0.183" };
        CliProcess.Result plain = CliProcess.RunBuilt(args);
        CliProcess.Result withOut = CliProcess.RunBuilt(args.Concat(new[] { "--events-out", outDir }).ToArray());
        Assert.True(plain.ExitCode == 0, plain.ToString());
        Assert.True(withOut.ExitCode == 0, withOut.ToString());
        Assert.Contains("exported RMS event study (", withOut.StdOut, StringComparison.Ordinal);
        string verdict(string s) => s.Split('\n').First(l => l.StartsWith("STABLE", StringComparison.Ordinal) || l.StartsWith("UNSTABLE", StringComparison.Ordinal)).Split("  *  ")[0];
        Assert.Equal(verdict(plain.StdOut), verdict(withOut.StdOut));

        Assert.True(File.Exists(Path.Combine(outDir, RmsEventsExport.MachinesCsv)));
        Assert.True(File.Exists(Path.Combine(outDir, RmsEventsExport.SummaryJson)));
        Assert.True(File.Exists(Path.Combine(outDir, "run-metadata.json")));
        string csv = File.ReadAllText(Path.Combine(outDir, RmsEventsExport.MachinesCsv));
        Assert.Contains("# provenance: Synthetic", csv, StringComparison.Ordinal);
        Assert.Contains(RmsEventsExport.MachinesHeader, csv, StringComparison.Ordinal);
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, RmsEventsExport.SummaryJson)));
        Assert.Equal(2, doc.RootElement.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public void Cli_events_out_without_events_is_refused()
    {
        CliProcess.Result r = CliProcess.RunBuilt("stability", "case9", "--rms", "--events-out", Path.Combine(_dir, "y"));
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("--events-out writes the export of an RMS event study", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Decimation_writes_every_nth_step_and_the_default_is_the_full_file()
    {
        MultiMachineSubtransient.Result run = TinyRun(samples: 5);
        string full = RmsEventsExport.MachinesCsvText(run, new[] { 1, 2 }, 0, Array.Empty<string>(), "n");
        Assert.Equal(full, RmsEventsExport.MachinesCsvText(run, new[] { 1, 2 }, 0, Array.Empty<string>(), "n", everySteps: 1));
        string every2 = RmsEventsExport.MachinesCsvText(run, new[] { 1, 2 }, 0, Array.Empty<string>(), "n", everySteps: 2);
        string[] rows = every2.Split('\n').SkipWhile(l => l != RmsEventsExport.MachinesHeader).Skip(1).Where(l => l.Length > 0).ToArray();
        Assert.Equal(3 * 2, rows.Length);   // steps 0, 2, 4, two machines each
        Assert.Equal(new[] { "0", "0.01", "0.02" }, rows.Where((_, i) => i % 2 == 0).Select(r => r.Split(',')[0]).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => RmsEventsExport.MachinesCsvText(run, new[] { 1, 2 }, 0, Array.Empty<string>(), "n", everySteps: 0));
        using JsonDocument doc = JsonDocument.Parse(RmsEventsExport.SummaryJsonText(run, new[] { 1, 2 }, 0, Events, "c", 50, 0.005, 3, true, "n", everySteps: 2));
        Assert.Equal(2, doc.RootElement.GetProperty("everySteps").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("samplesWritten").GetInt32());
    }

    [Fact]
    public void Cli_events_every_needs_events_out_and_a_positive_whole_number()
    {
        CliProcess.Result alone = CliProcess.RunBuilt("stability", "case9", "--rms", "--events", "fault:7@0.1,clear:7@0.183", "--events-every", "5");
        Assert.True(alone.ExitCode == 2, alone.ToString());
        CliProcess.Result zero = CliProcess.RunBuilt("stability", "case9", "--rms", "--events", "fault:7@0.1,clear:7@0.183",
            "--events-out", Path.Combine(_dir, "z"), "--events-every", "0");
        Assert.True(zero.ExitCode == 2, zero.ToString());
    }

    [Fact]
    public void The_flag_has_its_chapter_19_row()
    {
        string ch19 = File.ReadAllText(Path.Combine(CliProcess.Root(), "docs", "manual", "0019 CLI reference.md"));
        Assert.Contains("| `--events-out` | - |", ch19, StringComparison.Ordinal);
    }
}
