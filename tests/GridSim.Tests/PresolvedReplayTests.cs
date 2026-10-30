using System;
using System.IO;
using System.Linq;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// <see cref="PresolvedReplay"/> loads a run directory of exported grid states, keys per-frame telemetry off the
/// sidecar CSV (falling back to computed values when a row is missing), and exposes a clamped playback cursor.
/// </summary>
public class PresolvedReplayTests : IDisposable
{
    private readonly string _dir;

    public PresolvedReplayTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"gridsim-replay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_dir, "exports"));

        // Three identical case9 snapshots at ticks 1, 50, 100.
        var trace = new SolveTrace();
        GridModel model = IeeeCases.Case9();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(model, new PowerFlowOptions { Trace = trace });
        foreach (int tick in new[] { 1, 50, 100 })
        {
            var meta = new SolveExportMeta("case9", "cpu", "c", "2026-07-02T00:00:00Z", 1.0, "test");
            SolveExport.WriteJson(Path.Combine(_dir, "exports", $"slice-{tick:D9}.json"), model, r, trace, meta);
        }

        // A timeseries.csv covering ticks 1 and 100 only (tick 50 must fall back to computed telemetry).
        File.WriteAllText(Path.Combine(_dir, "timeseries.csv"),
            "# header comment\n" +
            "tick,t_seconds,freq_hz,rocof_hz_s,gen_mw,demand_mw,converged\n" +
            "1,0.0,50.000,0.000,320.0,315.0,true\n" +
            "100,0.099,49.850,-1.200,321.0,316.0,true\n");
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } GC.SuppressFinalize(this); }

    [Fact]
    public void Loads_all_snapshots_sorted_by_index()
    {
        PresolvedReplay replay = PresolvedReplay.Load(_dir);
        Assert.Equal(3, replay.Count);
        Assert.Equal(new[] { 1, 50, 100 }, replay.Frames.Select(f => f.Tick).ToArray());
        Assert.True(PresolvedReplay.IsRunDirectory(_dir));
    }

    [Fact]
    public void Fills_scalar_telemetry_from_the_csv()
    {
        PresolvedReplay replay = PresolvedReplay.Load(_dir);
        Assert.Equal(50.000, replay.Frames[0].FrequencyHz, 3);   // tick 1
        Assert.Equal(-1.200, replay.Frames[2].RocofHzPerS, 3);   // tick 100
        Assert.Equal(316.0, replay.Frames[2].DemandMw, 3);
    }

    [Fact]
    public void Falls_back_to_computed_telemetry_when_the_csv_row_is_missing()
    {
        PresolvedReplay replay = PresolvedReplay.Load(_dir);
        SimFrame midway = replay.Frames[1]; // tick 50 - not in the CSV
        // Moved 2026-10 (D2 N017, Ruling R1: Mark delegated EE calls 2026-10-04; source: Anderson and Fouad 2003, Example
        // 2.6, see IeeeCases.Wscc9NominalFrequencyHz): a frame with no CSV row shows the model's nominal frequency, and the
        // case9 export now carries the WSCC system's 60 Hz. Before: 50.0. After: 60.0 (the same expression,
        // snap.Model.FrequencyHz, PresolvedReplay.cs:93).
        Assert.Equal(60.0, midway.FrequencyHz, 6);                             // the model's nominal
        Assert.Equal(IeeeCases.Case9().FrequencyHz, midway.FrequencyHz, 6);
        Assert.Equal(IeeeCases.Case9().Buses.Sum(b => b.Pd), midway.DemandMw, 3); // computed from model
    }

    [Fact]
    public void The_cursor_advances_seeks_and_clamps()
    {
        PresolvedReplay replay = PresolvedReplay.Load(_dir);
        Assert.Equal(0, replay.Index);
        Assert.False(replay.AtEnd);

        replay.Advance();
        Assert.Equal(1, replay.Index);
        replay.Advance(10);                 // clamps at the end
        Assert.Equal(2, replay.Index);
        Assert.True(replay.AtEnd);

        replay.SeekFraction(0.0);
        Assert.Equal(0, replay.Index);
        replay.SeekFraction(0.5);
        Assert.Equal(1, replay.Index);
        replay.Restart();
        Assert.Equal(0, replay.Index);
    }

    [Fact]
    public void The_layout_model_has_map_coordinates()
    {
        PresolvedReplay replay = PresolvedReplay.Load(_dir);
        // GraphLayout.EnsureLayout assigns non-degenerate X/Y so the schematic can render.
        Assert.Contains(replay.LayoutModel.Buses, b => b.X != 0 || b.Y != 0);
    }

    [Fact]
    public void Loads_state_json_naming_with_a_time_utc_sidecar_and_refuses_a_malformed_row()
    {
        // The gda-replay path uses state-*.json snapshots and a state-estimate.csv keyed by 'index' with an ISO
        // 'time_utc' column (not slice-*.json + 't_seconds'). Exercise that naming + the time_utc->label/hour branch.
        // Test changed 2026-10-19 (renamed from ..._tolerates_a_malformed_row): GR-872 made a row whose key cell is
        // missing or not a whole number a broken file that fails loud (it used to be skipped, silently dropping that
        // frame's telemetry). The good file now loads without the garbage row, and the garbage row is proved REFUSED.
        string dir = Path.Combine(Path.GetTempPath(), $"gridsim-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "exports"));
        try
        {
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
                "0,2024-06-01T09:00:00Z,320.0,315.0\n" +
                "3,2024-06-01T12:30:00Z,321.0,316.0\n");

            PresolvedReplay replay = PresolvedReplay.Load(dir);
            Assert.Equal(2, replay.Count);
            Assert.Equal(new[] { 0, 3 }, replay.Frames.Select(f => f.Tick).ToArray());   // state-*.json indices parsed
            // time_utc drove the label verbatim and the hour-of-day (12:30 UTC -> 12.5 h) for the second frame.
            Assert.Equal("2024-06-01T12:30:00Z", replay.Label(1));
            Assert.Equal(12.5, replay.Frames[1].Hour, 3);
            Assert.Equal(316.0, replay.Frames[1].DemandMw, 3);

            // GR-872: the same file with a malformed row is refused, naming the file, the line and the key column.
            File.WriteAllText(Path.Combine(dir, "state-estimate.csv"),
                "index,time_utc,gen_mw,demand_mw\n" +
                "0,2024-06-01T09:00:00Z,320.0,315.0\n" +
                "garbage-row,too-few\n" +
                "3,2024-06-01T12:30:00Z,321.0,316.0\n");
            var ex = Assert.Throws<InvalidDataException>(() => PresolvedReplay.Load(dir));
            Assert.Contains("state-estimate.csv line 3: the 'index' cell is missing or not a whole number.", ex.Message, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }
}
