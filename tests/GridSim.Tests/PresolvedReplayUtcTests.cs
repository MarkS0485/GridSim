using System;
using System.IO;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R291: the gda-replay sidecar column is called <c>time_utc</c>, so a naive stamp (no 'Z', no offset) is UTC by
/// contract. It used to be parsed as machine-local time and then shifted to UTC, so on any non-UTC machine (a UK
/// machine in BST, for one) every frame's hour came out wrong. An unreadable stamp used to leave the hour silently
/// at midnight; it now fails loud.
/// </summary>
public class PresolvedReplayUtcTests
{
    // Build a two-frame gda-replay style run directory whose state-estimate.csv carries the given time_utc cells.
    private static string MakeRun(string time0, string time3)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"gridsim-utc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "exports"));
        var trace = new SolveTrace();
        GridModel model = IeeeCases.Case9();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(model, new PowerFlowOptions { Trace = trace });
        foreach (int idx in new[] { 0, 3 })
        {
            var meta = new SolveExportMeta("case9", "cpu", "c", "2026-07-02T00:00:00Z", 1.0, "test");
            SolveExport.WriteJson(Path.Combine(dir, "exports", $"state-{idx:D6}.json"), model, r, trace, meta);
        }
        File.WriteAllText(Path.Combine(dir, "state-estimate.csv"),
            "index,time_utc,gen_mw,demand_mw\n" +
            $"0,{time0},320.0,315.0\n" +
            $"3,{time3},321.0,316.0\n");
        return dir;
    }

    [Fact]
    public void A_naive_time_utc_stamp_is_read_as_utc_not_local_time()
    {
        // 12:30 with no zone marker must be 12.5 h on every machine, whatever its time zone. Before the fix a BST
        // machine read it as 12:30 local = 11:30 UTC and reported 11.5 h.
        string dir = MakeRun("2024-06-01T09:00:00", "2024-06-01T12:30:00");
        try
        {
            PresolvedReplay replay = PresolvedReplay.Load(dir);
            Assert.Equal(9.0, replay.Frames[0].Hour);
            Assert.Equal(12.5, replay.Frames[1].Hour);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }

    [Fact]
    public void An_explicit_offset_is_still_converted_to_utc()
    {
        // 13:30+01:00 is 12:30 UTC: the offset path is unchanged by the fix.
        string dir = MakeRun("2024-06-01T10:00:00+01:00", "2024-06-01T13:30:00+01:00");
        try
        {
            PresolvedReplay replay = PresolvedReplay.Load(dir);
            Assert.Equal(9.0, replay.Frames[0].Hour);
            Assert.Equal(12.5, replay.Frames[1].Hour);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }

    [Fact]
    public void An_unreadable_time_utc_stamp_fails_loud_instead_of_reading_as_midnight()
    {
        string dir = MakeRun("2024-06-01T09:00:00Z", "not-a-date");
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => PresolvedReplay.Load(dir));
            Assert.Contains("not-a-date", ex.Message);
            Assert.Contains("state-estimate.csv", ex.Message);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }
}
