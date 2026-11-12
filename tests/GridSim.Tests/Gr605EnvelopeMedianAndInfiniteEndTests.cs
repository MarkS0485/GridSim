using GridSim.Core.Assets;
using GridSim.Core.Dynamics;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-605 (fixed in code before this test; the regression test was missing): the asset envelope's median of an even
/// sample count (every sweep with at least one parameter: 2^k corners) is the mean of the two middle nadirs, not the
/// upper one; a swept parameter with an infinite end (an element switched off by an infinite threshold) is held at the
/// class's authored value while the others swing in the tornado, so no tornado nadir is infinite or NaN; a NaN end is
/// refused by name.
/// </summary>
public class Gr605EnvelopeMedianAndInfiniteEndTests
{
    private static readonly IReadOnlyList<FrequencyEvent> Loss = new[] { new FrequencyEvent(1.0, -1000.0) };

    private static GridModel ModelWithEmbeddedDg()
    {
        AssetClass dg = AssetTestHelpers.GflClass("embedded-dg", rocofTripHzPerS: 0.5, headroomUpFraction: 0.0, headroomDownFraction: 0.0);
        AssetDynamicsModel bundle = AssetTestHelpers.Bundle(new[] { dg }, new AssetBinding("storage", "embedded-dg"));
        return AssetTestHelpers.Model(bundle, new Generator(1, Pg: 2000, MBase: 2000, H: 0, Type: "storage"));
    }

    [Fact]
    public void Gr605_the_median_of_two_corners_is_the_mean_of_both()
    {
        var sweep = new[] { new AssetSweepParam("embedded-dg", AssetParams.RocofTripHzPerS, Low: 0.05, High: 10.0) };
        AssetEnvelopeResult env = AssetEnvelope.Sweep(ModelWithEmbeddedDg(), AssetTestHelpers.BaseDemandMw, Loss, new DynamicsConfig(), sweep);
        Assert.Equal(2, env.Samples.Count);
        Assert.True(env.NadirMinHz < env.NadirMaxHz);
        Assert.Equal(0.5 * (env.NadirMinHz + env.NadirMaxHz), env.NadirMedianHz);   // the old rule gave NadirMaxHz
    }

    [Fact]
    public void Gr605_an_infinite_end_does_not_poison_the_other_parameters_tornado()
    {
        var sweep = new[]
        {
            new AssetSweepParam("embedded-dg", AssetParams.RocofTripHzPerS, Low: 0.05, High: double.PositiveInfinity),
            new AssetSweepParam("embedded-dg", AssetParams.DroopGainMwPerHzPerMw, Low: 1.0, High: 4.0),
        };
        AssetEnvelopeResult env = AssetEnvelope.Sweep(ModelWithEmbeddedDg(), AssetTestHelpers.BaseDemandMw, Loss, new DynamicsConfig(), sweep);
        Assert.Equal(4, env.Samples.Count);
        Assert.All(env.Tornado, t =>
        {
            Assert.True(double.IsFinite(t.NadirAtLow), $"{t.ParamName} low nadir {t.NadirAtLow}");
            Assert.True(double.IsFinite(t.NadirAtHigh), $"{t.ParamName} high nadir {t.NadirAtHigh}");
        });
        Assert.True(double.IsFinite(env.NadirMedianHz));
    }

    [Fact]
    public void Gr605_a_nan_end_is_refused_by_name()
    {
        var sweep = new[] { new AssetSweepParam("embedded-dg", AssetParams.DroopGainMwPerHzPerMw, Low: double.NaN, High: 4.0) };
        var ex = Assert.Throws<ArgumentException>(() =>
            AssetEnvelope.Sweep(ModelWithEmbeddedDg(), AssetTestHelpers.BaseDemandMw, Loss, new DynamicsConfig(), sweep));
        Assert.Contains("embedded-dg::" + AssetParams.DroopGainMwPerHzPerMw, ex.Message, StringComparison.Ordinal);
    }
}
