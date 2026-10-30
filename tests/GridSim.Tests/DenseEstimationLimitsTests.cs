using GridSim.Core.Estimation;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// October 2026 review, WS11 R119: every dense estimation method checks one named limit from
/// <see cref="DenseEstimationLimits"/>. The existing values are pinned unchanged (no case that ran before is
/// refused, none that was refused now runs), and the method that had no cap (QR) now fails loud above the
/// shared dense-method limit instead of allocating two multi-gigabyte m x s matrices.
/// </summary>
public class DenseEstimationLimitsTests
{
    [Fact]
    public void The_limits_keep_their_pre_review_values()
    {
        Assert.Equal(3000, DenseEstimationLimits.DenseMethodMaxBuses);       // was the local constrainedDenseLimit
        Assert.Equal(2500, DenseEstimationLimits.RankProofMaxState);         // was ObservabilityDiagnosisMaxState
        Assert.Equal(2000, DenseEstimationLimits.StateCovarianceMaxState);   // was CovarianceStateLimit
        Assert.Equal(DenseEstimationLimits.RankProofMaxState, DenseEstimationLimits.RecursiveFilterMaxState);
    }

    private static GridModel Chain(int buses) => new()
    {
        Name = "dense-cap-chain",
        BaseMva = 100,
        Buses = Enumerable.Range(1, buses)
            .Select(i => new Bus(i, i == 1 ? BusType.Slack : BusType.Pq, BaseKv: 132))
            .ToList(),
        Generators = Array.Empty<Generator>(),
        Branches = Enumerable.Range(1, buses - 1).Select(i => new Branch(i, i + 1, 0.001, 0.01)).ToList(),
    };

    private static MeasurementSet OneVoltage() => new()
    {
        BaseMva = 100,
        Measurements = new[] { new Measurement(MeasurementKind.VmPu, 1.0, 0.004, Bus: 1) },
    };

    [Theory]
    [InlineData(EstimationMethod.Qr)]
    [InlineData(EstimationMethod.Constrained)]
    [InlineData(EstimationMethod.Feasible)]
    public void Dense_methods_fail_loud_above_the_shared_bus_limit(EstimationMethod method)
    {
        GridModel model = Chain(DenseEstimationLimits.DenseMethodMaxBuses + 1);
        NotSupportedException ex = Assert.Throws<NotSupportedException>(() =>
            new WlsStateEstimator().Estimate(model, OneVoltage(), new EstimationOptions { Method = method }));
        Assert.Contains(DenseEstimationLimits.DenseMethodMaxBuses.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message);
    }

    [Fact]
    public void Qr_below_the_limit_is_not_refused_by_the_cap()
    {
        // Below the limit the cap does not fire: the (hopelessly under-measured) set is then reported
        // honestly as unobservable by the estimator itself, not refused for size.
        GridModel model = Chain(DenseEstimationLimits.DenseMethodMaxBuses / 10);
        EstimationResult r = new WlsStateEstimator().Estimate(model, OneVoltage(), new EstimationOptions { Method = EstimationMethod.Qr });
        Assert.Equal(EstimationStatus.Unobservable, r.Status);
    }
}
