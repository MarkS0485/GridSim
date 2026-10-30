using GridSim.Core.Harmonics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-577 / GR-593: a balanced three-phase harmonic source refuses a spectrum that repeats an order (the last entry
/// used to win silently); distinct orders are accepted.
/// </summary>
public sealed class ThreePhaseHarmonicSourceTests
{
    [Fact]
    public void A_repeated_order_is_refused()
    {
        var spectrum = new[] { new HarmonicComponent(5, 20.0), new HarmonicComponent(7, 14.0), new HarmonicComponent(5, 10.0) };
        var ex = Assert.Throws<System.ArgumentException>(() => ThreePhaseHarmonicSource.Balanced(2, "test", 0.5, spectrum));
        Assert.Contains("order 5", ex.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Distinct_orders_are_accepted()
    {
        ThreePhaseHarmonicSource src = ThreePhaseHarmonicSource.Balanced(2, "test", 0.5,
            new[] { new HarmonicComponent(5, 20.0), new HarmonicComponent(7, 14.0) });
        Assert.Equal(new[] { 5, 7 }, System.Linq.Enumerable.OrderBy(src.Orders, o => o));
    }
}
