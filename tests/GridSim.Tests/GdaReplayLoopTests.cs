using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.Diagnostics;
using GridSim.Core.Gda;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The historical state estimator: it reconstructs the grid state at each past timestamp of a demand series
/// (heavier demand => more loss), refuses any future-dated series, and round-trips its input through JSON.
/// </summary>
public class GdaReplayLoopTests
{
    private static readonly DateTime Now = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
    private static GridModel Case9() => IeeeCases.Case9();

    [Fact]
    public void Reconstructs_every_timestamp_of_a_synthetic_day()
    {
        GdaTimeSeries series = GdaTimeSeries.Synthetic("day", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 24, 60);
        GdaReplayResult r = GdaReplayLoop.Replay(Case9(), series, utcNow: Now);

        Assert.Equal(24, r.Steps);
        Assert.Equal(0, r.NonConverged);
        Assert.All(r.StepResults, s => Assert.True(s.Converged));
    }

    [Fact]
    public void Heavier_demand_reconstructs_higher_loss()
    {
        var series = new GdaTimeSeries("two", new List<GdaTimeStep>
        {
            new(new DateTime(2024, 1, 1, 4, 0, 0, DateTimeKind.Utc), 0.7),
            new(new DateTime(2024, 1, 1, 16, 0, 0, DateTimeKind.Utc), 1.2),
        });
        GdaReplayResult r = GdaReplayLoop.Replay(Case9(), series, utcNow: Now);

        Assert.True(r.StepResults[1].TotalLossMw > r.StepResults[0].TotalLossMw,
            $"heavier demand should show more loss: {r.StepResults[0].TotalLossMw:F2} vs {r.StepResults[1].TotalLossMw:F2} MW");
    }

    [Fact]
    public void A_future_dated_series_is_rejected_at_ingestion()
    {
        var series = new GdaTimeSeries("future", new List<GdaTimeStep>
        {
            new(Now.AddHours(-100), 1.0),
            new(Now.AddHours(-1), 1.0), // inside the horizon
        });
        Assert.Throws<ForwardInferenceException>(() => GdaReplayLoop.Replay(Case9(), series, utcNow: Now));
    }

    [Fact]
    public void OnSolved_fires_once_per_step()
    {
        GdaTimeSeries series = GdaTimeSeries.Synthetic("day", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 6, 60);
        int calls = 0;
        GdaReplayLoop.Replay(Case9(), series, utcNow: Now, onSolved: (_, _, _, _, _) => calls++);
        Assert.Equal(6, calls);
    }

    [Fact]
    public void Series_round_trips_through_json_and_load_enforces_the_horizon()
    {
        GdaTimeSeries original = GdaTimeSeries.Synthetic("day", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 5, 30);
        string path = Path.Combine(Path.GetTempPath(), $"gridsim-ts-{Guid.NewGuid():N}.json");
        try
        {
            original.Save(path);
            GdaTimeSeries loaded = GdaTimeSeries.Load(path, Now);
            Assert.Equal(original.Steps.Count, loaded.Steps.Count);
            Assert.Equal(original.Steps[0].SystemLoadFactor, loaded.Steps[0].SystemLoadFactor, 9);
            Assert.Equal(original.Steps[0].TimeUtc, loaded.Steps[0].TimeUtc);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_itself_rejects_a_series_dated_just_inside_the_horizon()
    {
        // The horizon is enforced by GdaTimeSeries.Load directly (not only by the Replay loop). A series whose
        // last stamp sits just INSIDE the 169 h horizon must be refused on load; the boundary case must pass.
        var inside = new GdaTimeSeries("inside", new List<GdaTimeStep>
        {
            new(Now.AddHours(-300), 1.0),
            new(Now.AddHours(-ForwardInferenceGuard.HorizonHours + 0.1), 1.0), // 0.1 h inside the horizon
        });
        var onBoundary = new GdaTimeSeries("boundary", new List<GdaTimeStep>
        {
            new(Now.AddHours(-300), 1.0),
            new(Now.AddHours(-ForwardInferenceGuard.HorizonHours), 1.0),       // exactly on the horizon
        });

        string insidePath = Path.Combine(Path.GetTempPath(), $"gridsim-ts-{Guid.NewGuid():N}.json");
        string boundaryPath = Path.Combine(Path.GetTempPath(), $"gridsim-ts-{Guid.NewGuid():N}.json");
        try
        {
            inside.Save(insidePath);
            onBoundary.Save(boundaryPath);
            Assert.Throws<ForwardInferenceException>(() => GdaTimeSeries.Load(insidePath, Now));
            GdaTimeSeries.Load(boundaryPath, Now);   // on the boundary => loads
        }
        finally { File.Delete(insidePath); File.Delete(boundaryPath); }
    }
}
