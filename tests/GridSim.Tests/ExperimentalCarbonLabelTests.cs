using GridSim.Web.Projection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R302: the EXPERIMENTAL carbon card showed "0 gCO2/kWh" whenever tracing was skipped (every gb-full frame, where
/// the 0.0 is a placeholder), telling the user the GB system was at zero carbon intensity.
/// </summary>
public class ExperimentalCarbonLabelTests
{
    [Fact]
    public void Skipped_tracing_is_shown_as_skipped_not_as_zero_carbon()
    {
        // Before: "0 gCO2/kWh" for the skipped placeholder.
        Assert.Equal("- (skipped on this large network)", ExperimentalDto.FormatCarbon(0.0, skipped: true));
        // The flag wins whatever the placeholder holds.
        Assert.Equal("- (skipped on this large network)", ExperimentalDto.FormatCarbon(412.0, skipped: true));
    }

    [Fact]
    public void A_traced_value_is_shown_including_a_real_zero()
    {
        Assert.Equal("412 gCO\u2082/kWh", ExperimentalDto.FormatCarbon(412.4, skipped: false));
        Assert.Equal("0 gCO\u2082/kWh", ExperimentalDto.FormatCarbon(0.0, skipped: false));   // all zero-carbon fleet
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-5.0)]
    public void An_invalid_intensity_is_not_shown_as_a_number(double g)
        => Assert.Equal("-", ExperimentalDto.FormatCarbon(g, skipped: false));
}
