using GridSim.Core.Estimation;
using GridSim.Core.Interpose;
using GridSim.Core.IO.Telemetry;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-815: a 32-bit value is assembled from a register and its companion (register + 1, same response), but only the
/// first register's quality was read, so a companion flagged invalid or substituted still produced a full-weight value,
/// and a companion of another point type was combined without a word. Pinned: an unusable companion drops the value; a
/// degraded companion widens the sigma exactly as on the first register; good companions give the value and sigma
/// exactly as before; a companion of a different point type is refused as a configuration fault.
/// </summary>
public sealed class Gr815CompanionRegisterQualityTests
{
    private static readonly DateTime Now = new(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Historical = Now.AddHours(-200);

    private static RtuPointMap Map() => new()
    {
        Case = "t",
        Bindings = new List<RtuPointBinding>
        {
            new() { Address = "MB:U1:3:0", Kind = MeasurementKind.PFlowMw, From = 1, To = 2, Sigma = 2.0, DataType = RtuRegisterType.U32 },
        },
    };

    // Registers 0 and 1 of one response (same FrameSeq): 0x0001, 0x0002 high word first = 65538.
    private static List<RtuPoint> Window(RtuQuality firstQ, RtuQuality secondQ, RtuPointType secondType = RtuPointType.Analog) => new()
    {
        new RtuPoint("MB:U1:3:0", RtuPointType.Analog, 1, firstQ, null) { FrameSeq = 7 },
        new RtuPoint("MB:U1:3:1", secondType, 2, secondQ, null) { FrameSeq = 7 },
    };

    private static MeasurementSet MapIt(List<RtuPoint> w) => RtuMeasurementSetMapper.Map(w, Map(), Historical, QualityFold.Default, "t", Now);

    [Fact]
    public void Gr815_good_registers_give_the_value_and_sigma_exactly_as_before()
    {
        Measurement m = Assert.Single(MapIt(Window(RtuQuality.Good, RtuQuality.Good)).Measurements);
        Assert.Equal(65538.0, m.Value);
        Assert.Equal(2.0, m.Sigma);
    }

    [Theory]
    [InlineData(RtuQuality.Invalid)]
    [InlineData(RtuQuality.CommLost)]
    [InlineData(RtuQuality.Blocked)]
    public void Gr815_an_unusable_companion_drops_the_value(RtuQuality bad)
        => Assert.Empty(MapIt(Window(RtuQuality.Good, bad)).Measurements);

    [Fact]
    public void Gr815_a_degraded_companion_widens_the_sigma_as_on_the_first_register()
    {
        double onFirst = Assert.Single(MapIt(Window(RtuQuality.Substituted, RtuQuality.Good)).Measurements).Sigma;
        double onSecond = Assert.Single(MapIt(Window(RtuQuality.Good, RtuQuality.Substituted)).Measurements).Sigma;
        Assert.Equal(2.0 * QualityFold.Default.SigmaMultiplier(RtuQuality.Substituted), onSecond);
        Assert.Equal(onFirst, onSecond);
        Assert.True(onSecond > 2.0);
    }

    [Fact]
    public void Gr815_a_companion_of_another_point_type_is_refused()
    {
        var ex = Assert.Throws<InvalidDataException>(() => MapIt(Window(RtuQuality.Good, RtuQuality.Good, RtuPointType.Counter)));
        Assert.Contains("register 'MB:U1:3:1' is a Counter point", ex.Message, StringComparison.Ordinal);
    }
}
