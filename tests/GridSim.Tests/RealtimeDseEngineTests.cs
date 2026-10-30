using System;
using System.Collections.Generic;
using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The end-goal artifact: a guard-safe, real-time, dynamic (tracking) state estimator. Streaming a run of
/// historical, one-second-apart windows of consistent data, the tracking filter must converge to the truth;
/// a window newer than the horizon must be skipped (never estimated); and the run must be deterministic.
/// </summary>
public class RealtimeDseEngineTests
{
    private static readonly DateTime Now = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
    // A base instant safely behind the horizon; windows are one second apart from here.
    private static readonly DateTime Base = Now.AddHours(-170);

    private static (GridModel Model, MeasurementSet Template, PowerFlowResult Truth) Fixture()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        return (model, WlsEstimatorTests.ExactFullSet(model, truth), truth);
    }

    private static MeasurementSet[] Stream(MeasurementSet template, int count, int startSecond = 0)
    {
        var windows = new MeasurementSet[count];
        for (int k = 0; k < count; k++)
            windows[k] = template with { TakenUtc = Base.AddSeconds(startSecond + k) };
        return windows;
    }

    [Fact]
    public void Tracks_the_truth_over_a_historical_stream()
    {
        (GridModel model, MeasurementSet template, PowerFlowResult truth) = Fixture();
        var engine = new RealtimeDseEngine(model);

        DseFrame? last = null;
        RealtimeRunSummary summary = engine.Run(Stream(template, 30), new RealtimeEstimatorOptions { UtcNow = Now },
            f => last = f);

        Assert.Equal(30, summary.WindowsProcessed);
        Assert.NotNull(last);
        for (int i = 0; i < model.BusCount; i++)
            Assert.True(Math.Abs(last!.Estimate.Vm[i] - truth.Vm[i]) < 1e-3,
                $"bus {truth.BusIds[i]}: tracked Vm {last.Estimate.Vm[i]} vs truth {truth.Vm[i]}");
    }

    [Fact]
    public void A_fresh_window_in_the_stream_is_skipped()
    {
        (GridModel model, MeasurementSet template, _) = Fixture();
        var windows = new List<MeasurementSet>(Stream(template, 5));
        windows.Insert(2, template with { TakenUtc = Now.AddHours(-1) }); // FRESH

        var engine = new RealtimeDseEngine(model);
        int frames = 0;
        RealtimeRunSummary summary = engine.Run(windows, new RealtimeEstimatorOptions { UtcNow = Now }, _ => frames++);

        Assert.Equal(5, summary.WindowsProcessed);
        Assert.Equal(1, summary.WindowsRejectedFresh);
        Assert.Equal(5, frames);
    }

    [Fact]
    public void An_undated_window_is_refused()
    {
        (GridModel model, MeasurementSet template, _) = Fixture();
        var engine = new RealtimeDseEngine(model);
        Assert.Throws<ForwardInferenceException>(
            () => engine.Run(new[] { template with { TakenUtc = null } }, new RealtimeEstimatorOptions { UtcNow = Now }));
    }

    [Fact]
    public void Streaming_dse_is_deterministic()
    {
        (GridModel model, MeasurementSet template, _) = Fixture();

        double[] RunOnce()
        {
            var vms = new List<double>();
            new RealtimeDseEngine(model).Run(Stream(template, 12), new RealtimeEstimatorOptions { UtcNow = Now },
                f => vms.AddRange(f.Estimate.Vm));
            return vms.ToArray();
        }

        double[] a = RunOnce();
        double[] b = RunOnce();
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++) Assert.Equal(a[i], b[i]);
    }
}
