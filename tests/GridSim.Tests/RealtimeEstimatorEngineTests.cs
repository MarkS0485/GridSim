using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The resident streaming estimator (real-time engineering on permitted-age / replayed data). It must:
/// process a stream of historical windows and recover the truth each time (warm-started continuity),
/// REFUSE a window newer than the horizon (never estimate it), refuse an undated window, and be
/// deterministic. Completes restriction invariants #1/#4 for the streamed path.
/// </summary>
public class RealtimeEstimatorEngineTests
{
    private static readonly DateTime Now = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

    private static DateTime Historical(int extraHoursPast) =>
        Now.AddHours(-(ForwardInferenceGuard.HorizonHours + extraHoursPast));

    private static (GridModel Model, MeasurementSet Template, PowerFlowResult Truth) Fixture()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet template = WlsEstimatorTests.ExactFullSet(model, truth);
        return (model, template, truth);
    }

    private static MeasurementSet At(MeasurementSet template, DateTime? takenUtc) =>
        template with { TakenUtc = takenUtc };

    [Fact]
    public void Streams_historical_windows_and_recovers_truth_each_time()
    {
        (GridModel model, MeasurementSet template, PowerFlowResult truth) = Fixture();
        var windows = new[]
        {
            At(template, Historical(1)),
            At(template, Historical(2)),
            At(template, Historical(3)),
        };

        var frames = new List<RealtimeEstimateFrame>();
        var engine = new RealtimeEstimatorEngine(model);
        RealtimeRunSummary summary = engine.Run(windows, new RealtimeEstimatorOptions { UtcNow = Now }, frames.Add);

        Assert.Equal(3, summary.WindowsProcessed);
        Assert.Equal(3, summary.WindowsConverged);
        Assert.Equal(0, summary.WindowsRejectedFresh);
        Assert.Equal(0, summary.WindowsFailed);
        Assert.Equal(Historical(1), summary.FirstStateTimeUtc);
        Assert.Equal(Historical(3), summary.LastStateTimeUtc);

        Assert.Equal(3, frames.Count);
        for (int f = 0; f < frames.Count; f++)
        {
            Assert.Equal(f, frames[f].Sequence);
            Assert.Equal(EstimationStatus.Converged, frames[f].Result.Status);
            for (int i = 0; i < model.BusCount; i++)
                Assert.True(Math.Abs(frames[f].Result.Vm[i] - truth.Vm[i]) < 1e-10,
                    $"frame {f} bus {truth.BusIds[i]}: Vm {frames[f].Result.Vm[i]} vs {truth.Vm[i]}");
        }
    }

    [Fact]
    public void A_window_newer_than_the_horizon_is_skipped_never_estimated()
    {
        (GridModel model, MeasurementSet template, _) = Fixture();
        var windows = new[]
        {
            At(template, Historical(1)),   // ok
            At(template, Now.AddHours(-1)), // FRESH - must be refused
            At(template, Historical(2)),   // ok
        };

        var frames = new List<RealtimeEstimateFrame>();
        var engine = new RealtimeEstimatorEngine(model);
        RealtimeRunSummary summary = engine.Run(windows, new RealtimeEstimatorOptions { UtcNow = Now }, frames.Add);

        Assert.Equal(2, summary.WindowsProcessed);
        Assert.Equal(1, summary.WindowsRejectedFresh);
        Assert.Equal(2, frames.Count);
        Assert.All(frames, fr => Assert.True(fr.StateTimeUtc <= ForwardInferenceGuard.Horizon(Now)));
    }

    [Fact]
    public void An_undated_window_is_refused()
    {
        (GridModel model, MeasurementSet template, _) = Fixture();
        var engine = new RealtimeEstimatorEngine(model);
        Assert.Throws<ForwardInferenceException>(
            () => engine.Run(new[] { At(template, null) }, new RealtimeEstimatorOptions { UtcNow = Now }));
    }

    [Fact]
    public void Streaming_is_deterministic()
    {
        (GridModel model, MeasurementSet template, _) = Fixture();
        MeasurementSet[] windows =
        {
            At(template, Historical(1)),
            At(template, Historical(2)),
        };

        double[] RunOnce()
        {
            var vms = new List<double>();
            new RealtimeEstimatorEngine(model).Run(windows, new RealtimeEstimatorOptions { UtcNow = Now },
                fr => vms.AddRange(fr.Result.Vm));
            return vms.ToArray();
        }

        double[] a = RunOnce();
        double[] b = RunOnce();
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++) Assert.Equal(a[i], b[i]); // bit-identical
    }
}
