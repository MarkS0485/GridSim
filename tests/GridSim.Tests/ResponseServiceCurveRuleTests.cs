using System;
using GridSim.Core.Dynamics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-566 as relaxed on 2026-10-19: which response-service delivery curves <see cref="SwingResponders"/> accepts.
/// Without a knee the curve is the line through zero, so a deadband equal to the full-delivery point is a valid step (the
/// recorded 2019 static service: 198 MW at 0.3 Hz, full at 0.3 Hz). With a knee the segments divide by (knee - deadband)
/// and (full - knee), so deadband must stay strictly below full.
/// </summary>
public sealed class ResponseServiceCurveRuleTests
{
    private static SwingResponders With(SystemResponseService svc) =>
        new(new DynamicsConfig { SystemResponseServices = new[] { svc } }, 30000.0);

    [Fact]
    public void A_static_step_with_no_knee_and_deadband_equal_to_full_is_accepted()
    {
        Assert.NotNull(With(new SystemResponseService(198.0, 0.3, 0.3, 1.0)));
    }

    [Fact]
    public void A_line_with_no_knee_and_full_beyond_the_deadband_is_accepted()
    {
        Assert.NotNull(With(new SystemResponseService(500.0, 0.015, 0.5, 2.0)));
    }

    [Fact]
    public void A_knee_curve_in_order_is_accepted()
    {
        Assert.NotNull(With(new SystemResponseService(500.0, 0.015, 0.5, 2.0, KneeDeviationHz: 0.2, KneeFraction: 0.05)));
    }

    [Theory]
    [InlineData(198.0, 0.4, 0.3, 0.0, 0.0)]     // no knee, deadband beyond full
    [InlineData(198.0, 0.0, 0.0, 0.0, 0.0)]     // no knee, full at 0
    [InlineData(500.0, 0.3, 0.3, 0.3, 0.5)]     // knee form with deadband equal to full
    [InlineData(500.0, 0.015, 0.5, 0.6, 0.5)]   // knee beyond full
    [InlineData(500.0, 0.015, 0.5, 0.2, 1.5)]   // knee fraction above 1
    [InlineData(-1.0, 0.015, 0.5, 0.0, 0.0)]    // negative MW
    [InlineData(double.NaN, 0.015, 0.5, 0.0, 0.0)]
    public void A_curve_out_of_order_is_refused_by_service(double mw, double deadband, double full, double knee, double kneeFraction)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            With(new SystemResponseService(mw, deadband, full, 1.0, knee, kneeFraction)));
        Assert.Contains("Response service 0: the delivery curve is not in order", ex.Message, StringComparison.Ordinal);
    }
}
