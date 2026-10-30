using GridSim.Web.Api;
using WebSite.Data.Sso;
using Xunit;

namespace GridSim.Tests;

public partial class Lf1LowHangingTests
{
    // =================================================================================================================
    // N227: /ws/sim names an unknown property (the socket test is in WebHostDoc2Tests); the SSO audience and the
    // authority allowlist compare origins, not strings.
    // =================================================================================================================

    [Fact]
    public void N227_the_control_frame_field_list_is_the_record_s_own()
    {
        Assert.Equal(new[] { "action", "case", "source", "fps", "value", "solver", "assetGraph", "waveField" },
            SimWebSocket.ControlFieldsInOrder);
    }

    [Theory]
    [InlineData("https://host", "https://host:443")]
    [InlineData("https://host/", "https://HOST:443")]
    [InlineData("http://host", "http://host:80")]
    [InlineData("https://host:8443", "https://host:8443/")]
    [InlineData("https://host", "https://host")]
    public void N227_spellings_of_the_same_origin_match(string a, string b)
    {
        Assert.True(EstateSso.SameOrigin(a, b));
        Assert.True(EstateSso.SameOrigin(b, a));
    }

    [Theory]
    [InlineData("https://host", "http://host")]          // scheme differs
    [InlineData("https://host", "https://host:8443")]    // port differs
    [InlineData("https://host:80", "https://host")]      // 80 is not the https default
    [InlineData("https://host", "https://other")]        // host differs
    [InlineData("https://host", "not a url")]
    [InlineData("https://host", "")]
    public void N227_different_origins_still_do_not_match(string a, string b)
    {
        Assert.False(EstateSso.SameOrigin(a, b));
    }

    [Fact]
    public void N227_the_canonical_origin_drops_the_default_port_and_lower_cases_the_host()
    {
        Assert.Equal("https://app.example.org", EstateSso.CanonicalOrigin("https://App.Example.org:443/"));
        Assert.Equal("http://app.example.org:8080", EstateSso.CanonicalOrigin("http://app.example.org:8080"));
        Assert.Equal("ftp://x", EstateSso.CanonicalOrigin("ftp://x/"));   // not http(s): trimmed only
    }
}
