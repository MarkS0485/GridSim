using GridSim.Web.Components.Visuals;
using GridSim.Web.Projection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R132 remainder (October 2026 review, S5): in WAVE mode the map was sent the wave field on every new tick (229,908
/// bytes of JSON on gb-full, byte-identical tick after tick). <see cref="NetworkMap.SameWaveField"/> decides when a
/// field is bit-for-bit the one already on the map, so it is not sent again. These pin that it only says "same" when
/// every number would serialise identically.
/// </summary>
public class MapWaveFieldResendTests
{
    private static WaveFieldDto Field(double hz = 0.5, int source = 7, double amp1 = 0.25, double phase1 = 1.5, int bus1 = 2) =>
        new(hz, source, new List<WaveNodeDto> { new(1, 1.0, 0.0), new(bus1, amp1, phase1) });

    [Fact]
    public void Identical_numbers_are_the_same_field()
    {
        Assert.True(NetworkMap.SameWaveField(Field(), Field()));
        WaveFieldDto f = Field();
        Assert.True(NetworkMap.SameWaveField(f, f));
    }

    [Fact]
    public void Any_difference_in_any_number_is_a_new_field()
    {
        Assert.False(NetworkMap.SameWaveField(Field(), null));
        Assert.False(NetworkMap.SameWaveField(null, Field()));
        Assert.False(NetworkMap.SameWaveField(null, null));   // nothing on the map yet: send
        Assert.False(NetworkMap.SameWaveField(Field(hz: 0.5), Field(hz: 0.5000000000000001)));
        Assert.False(NetworkMap.SameWaveField(Field(source: 7), Field(source: 8)));
        Assert.False(NetworkMap.SameWaveField(Field(amp1: 0.25), Field(amp1: 0.25000000000000006)));
        Assert.False(NetworkMap.SameWaveField(Field(phase1: 0.0), Field(phase1: -0.0)));   // "0" vs "-0" in JSON
        Assert.False(NetworkMap.SameWaveField(Field(bus1: 2), Field(bus1: 3)));
        var shorter = new WaveFieldDto(0.5, 7, new List<WaveNodeDto> { new(1, 1.0, 0.0) });
        Assert.False(NetworkMap.SameWaveField(Field(), shorter));
    }

    [Fact]
    public void A_NaN_field_compares_by_bits()
    {
        Assert.True(NetworkMap.SameWaveField(Field(hz: double.NaN), Field(hz: double.NaN)));
    }
}
