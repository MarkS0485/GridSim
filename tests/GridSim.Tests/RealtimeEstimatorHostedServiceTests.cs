using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Web.Realtime;
using Xunit;

namespace GridSim.Tests;

/// <summary>The resident real-time estimator hosted service: started, it drives the streaming engine over its
/// feed and records the run - the DI-hosted background estimator for the web app.</summary>
public class RealtimeEstimatorHostedServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class TestFeed : IRealtimeWindowFeed
    {
        public GridModel Model { get; }
        private readonly MeasurementSet _template;
        public TestFeed()
        {
            Model = IeeeCases.Case9();
            PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(Model);
            _template = WlsEstimatorTests.ExactFullSet(Model, truth);
        }
        public IEnumerable<MeasurementSet> Windows()
        {
            DateTime baseT = Now.AddHours(-200);
            for (int k = 0; k < 5; k++) yield return _template with { TakenUtc = baseT.AddSeconds(k) };
        }
    }

    [Fact]
    public void Service_core_runs_the_engine_over_its_feed()
    {
        var frames = new List<RealtimeEstimateFrame>();
        var svc = new RealtimeEstimatorHostedService(new TestFeed(), new RealtimeEstimatorOptions { UtcNow = Now });
        svc.FrameEmitted += frames.Add;

        RealtimeRunSummary summary = svc.RunToCompletion();

        Assert.Equal(5, summary.WindowsProcessed);
        Assert.Equal(5, frames.Count);
        Assert.Same(summary, svc.LastSummary);
    }

    [Fact]
    public async Task Hosted_lifecycle_starts_and_stops_cleanly()
    {
        var svc = new RealtimeEstimatorHostedService(new TestFeed(), new RealtimeEstimatorOptions { UtcNow = Now });
        await svc.StartAsync(CancellationToken.None);
        await svc.StopAsync(CancellationToken.None); // must not throw
    }
}
