using GridSim.Core.Interpose;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-329: the protocol registry hands every caller its own protocol instance, so the Modbus stack's outstanding
/// requests (and any other per-link state) are never shared between two pipelines in one process.
/// </summary>
public class Gr329ProtocolRegistryTests
{
    [Theory]
    [InlineData("modbus")]
    [InlineData("iec104")]
    public void Each_lookup_returns_a_fresh_instance(string name)
    {
        Assert.True(RtuProtocolRegistry.TryGet(name, out IRtuProtocol first));
        Assert.True(RtuProtocolRegistry.TryGet(name, out IRtuProtocol second));
        Assert.NotSame(first, second);
        Assert.Equal(first.GetType(), second.GetType());
        Assert.Equal(name, first.Name);
    }

    [Fact]
    public void An_unknown_name_is_not_found()
    {
        Assert.False(RtuProtocolRegistry.TryGet("no-such-protocol", out _));
    }

    [Fact]
    public void The_listing_still_names_every_registered_protocol_in_order()
    {
        string[] names = RtuProtocolRegistry.All.Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "iec104", "modbus" }, names);
    }
}
