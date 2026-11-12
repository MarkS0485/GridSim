using GridSim.Core.IO.Cim;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-635: the CIM bridge always produced a 50 Hz model whatever the source system. The CGMES profiles the importer
/// reads carry no system frequency, so ToGridModel and Bridge take an optional frequencyHz. Pinned: the default (null)
/// still gives exactly 50 Hz; a stated 60 Hz is carried to the model and to Bridge's model; a frequency that is zero,
/// negative, NaN or infinite is refused by name.
/// </summary>
public class Gr635CimBridgeFrequencyTests
{
    private static CimNetwork Net() => new(
        new[] { new CimBus("n1", "N1", 132.0), new CimBus("n2", "N2", 132.0) },
        new[] { new CimBranch("l1", "L1", "n1", "n2", 1.7424, 17.424, 0.0) },
        Array.Empty<CimLoad>(),
        new[] { new CimGenerator("g1", "G1", "n1", 0.0, TargetKv: 132.0, VoltageControl: true) },
        Array.Empty<CimTransformer>());

    [Fact]
    public void Gr635_the_default_frequency_is_still_50_hz()
    {
        Assert.Equal(50.0, CimGridModelBridge.ToGridModel(Net()).FrequencyHz);
    }

    [Fact]
    public void Gr635_a_stated_frequency_reaches_the_model_through_both_entry_points()
    {
        Assert.Equal(60.0, CimGridModelBridge.ToGridModel(Net(), frequencyHz: 60.0).FrequencyHz);
        Assert.Equal(60.0, CimGridModelBridge.Bridge(Net(), frequencyHz: 60.0).Model.FrequencyHz);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-50.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Gr635_an_unusable_frequency_is_refused_by_name(double hz)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => CimGridModelBridge.ToGridModel(Net(), frequencyHz: hz));
        Assert.Equal("frequencyHz", ex.ParamName);
    }
}
