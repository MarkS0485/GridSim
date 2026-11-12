using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation;
using GridSim.Core.Estimation.Dse;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-855: with ContinueOnError the real-time DSE engine counted EVERY InvalidOperationException as a failed window, a
/// programming fault included. Domain refusals now carry the DomainRefusal mark (in Exception.Data, so their type is
/// unchanged and every exact-type test still holds); the engine counts InvalidDataException and marked
/// InvalidOperationException, and an unmarked InvalidOperationException fails the run loudly.
/// </summary>
public class Gr855DseDomainRefusalTests
{
    private static readonly DateTime Now = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Base = Now.AddHours(-170);

    private static MeasurementSet[] Windows()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet t = WlsEstimatorTests.ExactFullSet(model, truth);
        return new[] { t with { TakenUtc = Base }, t with { TakenUtc = Base.AddSeconds(1) } };
    }

    private static RealtimeDseEngine Throwing(Func<Exception> fault) =>
        RealtimeDseEngine.ForStep((_, _) => throw fault(), "test");

    [Fact]
    public void Gr855_a_marked_domain_refusal_is_counted_as_a_failed_window()
    {
        RealtimeRunSummary s = Throwing(() => DomainRefusal.Mark(new InvalidOperationException("singular covariance")))
            .Run(Windows(), new RealtimeEstimatorOptions { UtcNow = Now });
        Assert.Equal(2, s.WindowsFailed);
        Assert.Equal(0, s.WindowsProcessed);
    }

    [Fact]
    public void Gr855_bad_input_data_is_still_counted_as_a_failed_window()
    {
        RealtimeRunSummary s = Throwing(() => new InvalidDataException("bad window"))
            .Run(Windows(), new RealtimeEstimatorOptions { UtcNow = Now });
        Assert.Equal(2, s.WindowsFailed);
    }

    [Fact]
    public void Gr855_an_unmarked_invalid_operation_is_a_programming_fault_and_fails_the_run()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Throwing(() => new InvalidOperationException("Collection was modified"))
                .Run(Windows(), new RealtimeEstimatorOptions { UtcNow = Now }));
        Assert.Equal("Collection was modified", ex.Message);
        Assert.False(DomainRefusal.IsMarked(ex));
    }

    [Fact]
    public void Gr855_the_filters_numerical_refusals_carry_the_mark_and_keep_their_type()
    {
        var nonFinite = new double[,] { { double.PositiveInfinity } };
        var ex = Assert.Throws<InvalidOperationException>(() => DseLinAlg.CholeskySemiDefinite(nonFinite, 1));
        Assert.True(DomainRefusal.IsMarked(ex));
        var notPd = new double[,] { { -1.0 } };
        var ex2 = Assert.Throws<InvalidOperationException>(() => DseLinAlg.Cholesky(notPd, 1));
        Assert.True(DomainRefusal.IsMarked(ex2));
    }

    [Fact]
    public void Gr855_the_mark_is_data_only()
    {
        var ex = new InvalidOperationException("x");
        Assert.False(DomainRefusal.IsMarked(ex));
        Assert.Same(ex, DomainRefusal.Mark(ex));
        Assert.True(DomainRefusal.IsMarked(ex));
        Assert.Equal(true, ex.Data[DomainRefusal.DataKey]);
    }
}
