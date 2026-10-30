using GridSim.Core.Net;
using GridSim.Web.Sessions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The web host checks its bridge endpoint at start-up and applies one set of client settings to every bridge session.
/// Before, the client refused every ws:// endpoint and nothing in the host could allow it, so every documented ws://
/// deployment failed on every bridge session. A ws:// endpoint now needs GridSim:BridgeAllowUnencrypted=true, and
/// without it start-up stops naming the setting.
/// </summary>
public class BridgeEndpointSettingTests
{
    [Fact]
    public void A_wss_endpoint_needs_no_opt_in_and_keeps_encryption_required()
    {
        ResilienceOptions o = SessionManager.ReadBridgeResilience("wss://bridge.example.com/ws/bridge", null);
        Assert.False(o.AllowUnencryptedEndpoint);
    }

    [Fact]
    public void A_ws_endpoint_without_the_opt_in_stops_start_up_naming_the_setting()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SessionManager.ReadBridgeResilience("ws://bridge.example.internal:8770/ws/bridge", null));
        Assert.Contains(SessionManager.BridgeAllowUnencryptedSetting, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData(" true ")]
    public void A_ws_endpoint_with_the_opt_in_is_allowed(string allow)
    {
        ResilienceOptions o = SessionManager.ReadBridgeResilience("ws://127.0.0.1:8770/ws/bridge", allow);
        Assert.True(o.AllowUnencryptedEndpoint);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("tru")]
    public void A_malformed_opt_in_is_refused_not_read_as_false(string allow)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SessionManager.ReadBridgeResilience("wss://bridge.example.com/ws/bridge", allow));
        Assert.Contains("true or false", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://bridge.example.com/ws/bridge")]
    [InlineData("bridge.example.com:8770")]
    [InlineData("/ws/bridge")]
    public void An_endpoint_that_is_not_a_websocket_uri_is_refused(string endpoint)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SessionManager.ReadBridgeResilience(endpoint, "true"));
        Assert.Contains("GridSim:BridgeEndpoint", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void No_endpoint_is_fine_and_keeps_the_defaults()
    {
        ResilienceOptions o = SessionManager.ReadBridgeResilience(null, null);
        Assert.Equal(ResilienceOptions.Default, o);
    }

    [Fact]
    public void The_settings_reach_the_client_of_a_bridge_session()
    {
        var client = new GridBridgeClient();
        Assert.False(client.AllowUnencryptedEndpoint);
        SessionManager.ReadBridgeResilience("ws://127.0.0.1:8770/ws/bridge", "true").ApplyTo(client);
        Assert.True(client.AllowUnencryptedEndpoint);
    }
}
