using GridSim.Core.IO;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-632: the bridge's message-type peek reads the whole top level; a message with two "type" keys (which the peek and
/// the typed deserialiser used to read differently: first against last) has no type, so it is reported as a bad message.
/// </summary>
public sealed class BridgePeekTypeTests
{
    [Fact]
    public void One_type_key_is_read_wherever_it_stands()
    {
        Assert.Equal("state", BridgeProtocol.PeekType("""{ "type": "state", "t": "x" }"""));
        Assert.Equal("state", BridgeProtocol.PeekType("""{ "t": { "type": "nested" }, "Type": "state" }"""));
    }

    [Fact]
    public void Two_type_keys_have_no_type()
        => Assert.Null(BridgeProtocol.PeekType("""{ "type": "state", "x": 1, "type": "topology" }"""));

    [Fact]
    public void Malformed_or_typeless_json_has_no_type()
    {
        Assert.Null(BridgeProtocol.PeekType("""{ "x": 1 }"""));
        Assert.Null(BridgeProtocol.PeekType("""{ "type": "state", """));
        Assert.Null(BridgeProtocol.PeekType("""{ "type": 3 }"""));
    }
}
