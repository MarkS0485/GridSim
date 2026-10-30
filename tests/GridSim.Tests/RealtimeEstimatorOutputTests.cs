using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Web.Realtime;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R100 and R099 (October 2026 review): the resident real-time estimator's output had no consumer (nothing read
/// LastSummary or subscribed to its frames, and the feed's skipped files were never surfaced), and the web carried
/// unused gauge DTOs and stale summaries. The service now keeps its latest frame and exposes the feed's skipped files,
/// read by GET /api/realtime.
/// </summary>
public class RealtimeEstimatorOutputTests
{
    private static readonly DateTime Now = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Feed : IRealtimeWindowFeed
    {
        public GridModel Model { get; } = IeeeCases.Case9();
        public IEnumerable<MeasurementSet> Windows()
        {
            MeasurementSet t = WlsEstimatorTests.ExactFullSet(Model, new NewtonRaphsonPowerFlow().Solve(Model));
            for (int k = 0; k < 3; k++) yield return t with { TakenUtc = Now.AddHours(-200).AddSeconds(k) };
        }
        public IReadOnlyList<string> SkippedFiles { get; } = new[] { "bad-window.json" };
    }

    [Fact]
    public void Latest_frame_and_skipped_files_are_readable_after_a_run()
    {
        var svc = new RealtimeEstimatorHostedService(new Feed(), new RealtimeEstimatorOptions { UtcNow = Now });
        Assert.Null(svc.LatestFrame);
        svc.RunToCompletion();
        Assert.NotNull(svc.LatestFrame);
        Assert.Equal(svc.LastSummary!.WindowsProcessed, svc.LatestFrame!.Sequence + 1);
        Assert.Equal(new[] { "bad-window.json" }, svc.SkippedFiles);
    }

    [Fact]
    public void Rest_api_exposes_the_realtime_output_and_the_service_is_resolvable()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        string web = Path.Combine(dir!.FullName, "src", "GridSim.Web");
        string api = File.ReadAllText(Path.Combine(web, "Api", "RestApi.cs"));
        Assert.Contains("api.MapGet(\"/realtime\"", api);
        Assert.Contains("[\"/api/realtime\"]", api);
        Assert.Contains("AddHostedService(sp => sp.GetRequiredService<RealtimeEstimatorHostedService>())",
            File.ReadAllText(Path.Combine(web, "Program.cs")));
        Assert.False(File.Exists(Path.Combine(web, "Projection", "GaugeInputs.cs")));   // R099: unused gauge DTOs removed
    }
}
