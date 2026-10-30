using GridSim.Core.IO;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// DOC-13 (October 2026), D-DOC-084: the bridge model-block reader (<see cref="BridgeProtocol.BuildModel"/>, also the
/// solve-export re-import). It reads the bus angle and the per-bus voltage limits (since D-DOC-067; pinned again here),
/// now refuses an unusable limit, and refuses a bus type it cannot parse instead of reading it as Pq without a word.
/// An absent type still means Pq. Every test is new.
/// </summary>
public sealed class Doc13BridgeReaderTests
{
    private static BridgeProtocol.ModelBlock Block(params BridgeProtocol.BusDto[] buses) => new()
    {
        Name = "t",
        BaseMva = 100,
        Buses = buses.ToList(),
        Generators = new() { new BridgeProtocol.GenDto { Bus = 1, Pg = 10 } },
        Branches = new() { new BridgeProtocol.BranchDto { From = 1, To = 2, R = 0.01, X = 0.1 } },
    };

    private static BridgeProtocol.BusDto Bus(int id, string? type) => new() { Id = id, Type = type, BaseKv = 132 };

    [Theory]
    [InlineData("Slack", BusType.Slack)]
    [InlineData("slack", BusType.Slack)]
    [InlineData("PV", BusType.Pv)]
    [InlineData("pq", BusType.Pq)]
    [InlineData(" Pq ", BusType.Pq)]
    public void D084_the_enum_names_parse_in_any_letter_case(string type, BusType expected)
    {
        GridModel m = BridgeProtocol.BuildModel(Block(Bus(1, "Slack"), Bus(2, type)));
        Assert.Equal(expected, m.Buses[1].Type);
    }

    [Fact]
    public void D084_an_absent_type_still_means_Pq()
    {
        GridModel m = BridgeProtocol.BuildModel(Block(Bus(1, "Slack"), Bus(2, null)));
        Assert.Equal(BusType.Pq, m.Buses[1].Type);
    }

    [Theory]
    [InlineData("Slak")]
    [InlineData("ref")]
    [InlineData("2")]
    [InlineData("-1")]
    [InlineData("Slack,Pv")]
    [InlineData("")]
    public void D084_an_unparseable_type_is_refused_naming_the_bus_and_value(string type)
    {
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => BridgeProtocol.BuildModel(Block(Bus(1, "Slack"), Bus(2, type))));
        Assert.Contains("Bus 2", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"type '{type}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Slack, Pv, Pq", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void D084_the_angle_and_the_per_bus_limits_are_read_exactly()
    {
        BridgeProtocol.BusDto two = Bus(2, "Pq");
        two.VaDeg = -12.345678901234567;
        two.VminPu = 0.94;
        two.VmaxPu = 1.06;
        GridModel m = BridgeProtocol.BuildModel(Block(Bus(1, "Slack"), two));
        Assert.Equal(-12.345678901234567, m.Buses[1].VaDeg);
        Assert.Equal(0.94, m.Buses[1].VminPu);
        Assert.Equal(1.06, m.Buses[1].VmaxPu);
        Assert.Null(m.Buses[0].VminPu);   // absent: the global band applies
    }

    [Theory]
    [InlineData(double.NaN, 1.1, "vminPu NaN")]
    [InlineData(0.0, 1.1, "vminPu 0")]
    [InlineData(0.9, double.PositiveInfinity, "vmaxPu Infinity")]
    [InlineData(1.1, 0.9, "vminPu 1.1 above vmaxPu 0.9")]
    public void D084_an_unusable_per_bus_limit_is_refused(double vmin, double vmax, string fragment)
    {
        BridgeProtocol.BusDto two = Bus(2, "Pq");
        two.VminPu = vmin;
        two.VmaxPu = vmax;
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => BridgeProtocol.BuildModel(Block(Bus(1, "Slack"), two)));
        Assert.Contains("bus 2 " + fragment, ex.Message, StringComparison.Ordinal);
    }
}
