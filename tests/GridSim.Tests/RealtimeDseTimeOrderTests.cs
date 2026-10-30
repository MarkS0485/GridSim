using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N025 (R256 follow-up): the real-time DSE engine coerced a zero or negative window spacing to a full second of
/// process noise. An equal timestamp now steps with dt = 0, and an earlier (out-of-order) window is refused.
/// </summary>
public class RealtimeDseTimeOrderTests
{
    private static readonly DateTime Now = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Base = Now.AddHours(-170);

    private static (GridModel Model, MeasurementSet Template) Fixture()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        return (model, WlsEstimatorTests.ExactFullSet(model, truth));
    }

    [Fact]
    public void An_out_of_order_window_is_counted_failed_and_skipped_by_default()
    {
        (GridModel model, MeasurementSet t) = Fixture();
        var windows = new[]
        {
            t with { TakenUtc = Base.AddSeconds(0) },
            t with { TakenUtc = Base.AddSeconds(2) },
            t with { TakenUtc = Base.AddSeconds(1) },   // earlier than the previous processed window
            t with { TakenUtc = Base.AddSeconds(3) },
        };
        RealtimeRunSummary s = new RealtimeDseEngine(model).Run(windows, new RealtimeEstimatorOptions { UtcNow = Now });
        Assert.Equal(3, s.WindowsProcessed);
        Assert.Equal(1, s.WindowsFailed);
    }

    [Fact]
    public void An_out_of_order_window_fails_the_run_when_errors_are_not_tolerated()
    {
        (GridModel model, MeasurementSet t) = Fixture();
        var windows = new[] { t with { TakenUtc = Base.AddSeconds(5) }, t with { TakenUtc = Base.AddSeconds(4) } };
        var ex = Assert.Throws<InvalidDataException>(() =>
            new RealtimeDseEngine(model).Run(windows, new RealtimeEstimatorOptions { UtcNow = Now, ContinueOnError = false }));
        Assert.Contains("earlier than the previous processed window", ex.Message);
    }

    [Fact]
    public void An_equal_timestamp_is_processed_with_no_extra_process_noise()
    {
        // Two identical windows at the same instant: the second steps with dt = 0, so it is processed (not refused)
        // and the run reports both.
        (GridModel model, MeasurementSet t) = Fixture();
        var windows = new[] { t with { TakenUtc = Base }, t with { TakenUtc = Base } };
        RealtimeRunSummary s = new RealtimeDseEngine(model).Run(windows, new RealtimeEstimatorOptions { UtcNow = Now });
        Assert.Equal(2, s.WindowsProcessed);
        Assert.Equal(0, s.WindowsFailed);
    }

    [Fact]
    public void The_engine_no_longer_coerces_a_non_positive_step_to_one_second()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        string src = File.ReadAllText(Path.Combine(dir!.FullName, "src", "GridSim.Core", "Simulation", "RealtimeDseEngine.cs"));
        Assert.DoesNotContain("if (dtSeconds <= 0) dtSeconds = 1.0;", src);
    }
}
