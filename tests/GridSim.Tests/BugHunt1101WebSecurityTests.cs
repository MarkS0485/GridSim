using System.Net;
using System.Net.WebSockets;
using GridSim.Web.Api;
using GridSim.Web.Site;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Static bug hunt of 2026-11-01: web security defaults. #170 (host allow-list against DNS rebinding, and the /ws/sim
/// origin rule) and #179 (browser security headers and HSTS), driven through the real GridSim.Web host in memory.
/// </summary>
public sealed partial class BugHunt1101WebTests
{
    private static WebApplicationFactory<Program> WebFactory(string? environment, params (string Key, string Value)[] settings)
    {
        string keys = Path.Combine(Path.GetTempPath(), "gridsim-bh1101-keys-" + Guid.NewGuid().ToString("N"));
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            if (environment is not null) b.UseEnvironment(environment);
            b.UseSetting("Auth:KeysDirectory", keys);
            foreach ((string k, string v) in settings) b.UseSetting(k, v);
        });
    }

    // A client whose requests carry the given Host (TestServer takes the Host header from the request URI).
    private static HttpClient ClientFor(WebApplicationFactory<Program> f, string baseUrl)
    {
        HttpClient c = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        c.BaseAddress = new Uri(baseUrl);
        return c;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // #170: host allow-list.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task B170_by_default_only_the_loopback_names_are_answered_and_a_rebound_name_gets_a_400_naming_the_setting()
    {
        await using var f = WebFactory(null, ("GridSim:ReplayOnly", "false"));
        using (HttpClient local = ClientFor(f, "http://localhost/"))
        using (HttpResponseMessage ok = await local.GetAsync(new Uri("/api/cases", UriKind.Relative)))
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using (HttpClient loop = ClientFor(f, "http://127.0.0.1:5540/"))
        using (HttpResponseMessage ok = await loop.GetAsync(new Uri("/api/cases", UriKind.Relative)))
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // The DNS-rebinding request: the attacker's own name in Host. Refused before the API runs, for every surface.
        using HttpClient rebound = ClientFor(f, "http://attacker.example:5540/");
        foreach (string path in new[] { "/api/cases", "/api/topology?case=case9", "/", "/ws/sim" })
        {
            using HttpResponseMessage r = await rebound.GetAsync(new Uri(path, UriKind.Relative));
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            string body = await r.Content.ReadAsStringAsync();
            Assert.Contains("does not answer the host name 'attacker.example:5540'", body, StringComparison.Ordinal);
            Assert.Contains("AllowedHosts", body, StringComparison.Ordinal);
        }
        using HttpResponseMessage solve = await rebound.PostAsync(new Uri("/api/solve", UriKind.Relative),
            new StringContent("{\"case\":\"case9\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, solve.StatusCode);
    }

    [Fact]
    public async Task B170_a_configured_public_name_is_answered_and_others_are_not()
    {
        await using var f = WebFactory(null, ("AllowedHosts", "gridsim.example.com;*.ops.example.net"));
        using (HttpClient pub = ClientFor(f, "https://GridSim.Example.com/"))
        using (HttpResponseMessage ok = await pub.GetAsync(new Uri("/", UriKind.Relative)))
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using (HttpClient sub = ClientFor(f, "https://a.ops.example.net/"))
        using (HttpResponseMessage ok = await sub.GetAsync(new Uri("/", UriKind.Relative)))
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        foreach (string other in new[] { "http://localhost/", "https://ops.example.net/", "https://gridsim.example.com.attacker.example/" })
        {
            using HttpClient c = ClientFor(f, other);
            using HttpResponseMessage r = await c.GetAsync(new Uri("/", UriKind.Relative));
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }
    }

    [Theory]
    [InlineData("a;;b")]
    [InlineData("https://gridsim.example.com")]
    [InlineData("gridsim.example.com:443")]
    [InlineData("*;gridsim.example.com")]
    [InlineData("grid*.example.com")]
    [InlineData("*.")]
    [InlineData("::1")]
    public void B170_a_malformed_allowed_hosts_value_stops_start_up_naming_the_setting(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => HostAllowList.Parse(value));
        Assert.Contains("AllowedHosts", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void B170_the_allow_list_matches_names_case_insensitively_without_the_port()
    {
        HostAllowList d = HostAllowList.Parse(null);
        Assert.True(d.IsDefault);
        Assert.False(d.AllowsAny);
        Assert.True(d.IsAllowed("localhost:5180"));
        Assert.True(d.IsAllowed("LOCALHOST"));
        Assert.True(d.IsAllowed("127.0.0.1"));
        Assert.True(d.IsAllowed("[::1]:8080"));
        Assert.False(d.IsAllowed("127.0.0.2"));        // the default is the three names, not a network
        Assert.False(d.IsAllowed(""));
        Assert.False(d.IsAllowed(null));
        Assert.False(d.IsAllowed("localhost.attacker.example"));
        Assert.True(HostAllowList.Parse("*").AllowsAny);
        Assert.True(HostAllowList.Parse("*").IsAllowed("anything.example"));
    }

    [Fact]
    public void B170_origin_equal_to_host_is_believed_only_behind_the_allow_list_or_for_loopback()
    {
        // Behind the allow-list (the default): the Host has already been checked, so a same-host Origin passes.
        Assert.True(SimWebSocket.OriginAllowed("http://gridsim.example.com", "gridsim.example.com", null, hostListAllowsAny: false));
        // AllowedHosts=*: the rebinding case (Origin and Host both the attacker's name) is now refused...
        Assert.False(SimWebSocket.OriginAllowed("http://attacker.example:5540", "attacker.example:5540", null, hostListAllowsAny: true));
        // ...a loopback page still works, and an operator-listed origin still works.
        Assert.True(SimWebSocket.OriginAllowed("http://localhost:5180", "localhost:5180", null, hostListAllowsAny: true));
        Assert.True(SimWebSocket.OriginAllowed("https://ops.example.com", "attacker.example", "https://ops.example.com", hostListAllowsAny: true));
        // Unchanged: no Origin (not a browser page) passes; a foreign Origin fails; a malformed Origin fails.
        Assert.True(SimWebSocket.OriginAllowed(null, "attacker.example", null, hostListAllowsAny: true));
        Assert.False(SimWebSocket.OriginAllowed("https://evil.example", "gridsim.example.com", null, hostListAllowsAny: false));
        Assert.False(SimWebSocket.OriginAllowed("not a uri", "gridsim.example.com", null, hostListAllowsAny: false));
    }

    [Fact]
    public async Task B170_with_any_host_allowed_the_socket_still_refuses_a_rebinding_page()
    {
        await using var f = WebFactory(null, ("GridSim:ReplayOnly", "false"), ("AllowedHosts", "*"));
        Microsoft.AspNetCore.TestHost.WebSocketClient client = f.Server.CreateWebSocketClient();
        client.ConfigureRequest = req => req.Headers.Origin = "http://attacker.example:5540";
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            client.ConnectAsync(new Uri("ws://attacker.example:5540/ws/sim"), CancellationToken.None));
        Assert.Contains("403", ex.Message, StringComparison.Ordinal);

        // A page served from the instance's own loopback name still connects.
        Microsoft.AspNetCore.TestHost.WebSocketClient local = f.Server.CreateWebSocketClient();
        local.ConfigureRequest = req => req.Headers.Origin = "http://localhost";
        using WebSocket ws = await local.ConnectAsync(new Uri("ws://localhost/ws/sim"), CancellationToken.None);
        Assert.Equal(WebSocketState.Open, ws.State);
        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // #179: security headers and HSTS.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task B179_every_answer_carries_the_framing_nosniff_and_referrer_headers()
    {
        await using var f = WebFactory(null, ("GridSim:ReplayOnly", "false"));
        using HttpClient c = ClientFor(f, "http://localhost/");
        foreach (string path in new[] { "/", "/api/cases", "/compliance", "/no-such-page" })
        {
            using HttpResponseMessage r = await c.GetAsync(new Uri(path, UriKind.Relative));
            Assert.Equal("frame-ancestors 'none'", string.Join(",", r.Headers.GetValues("Content-Security-Policy")));
            Assert.Equal("DENY", string.Join(",", r.Headers.GetValues("X-Frame-Options")));
            Assert.Equal("nosniff", string.Join(",", r.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal("strict-origin-when-cross-origin", string.Join(",", r.Headers.GetValues("Referrer-Policy")));
        }
        // Even a refused Host gets them.
        using HttpClient bad = ClientFor(f, "http://attacker.example/");
        using HttpResponseMessage refused = await bad.GetAsync(new Uri("/", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("nosniff", string.Join(",", refused.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task B179_frame_ancestors_can_be_opened_to_a_named_portal()
    {
        await using var f = WebFactory(null, ("GridSim:FrameAncestors", "'self' https://portal.example.com"));
        using HttpClient c = ClientFor(f, "http://localhost/");
        using HttpResponseMessage r = await c.GetAsync(new Uri("/", UriKind.Relative));
        Assert.Equal("frame-ancestors 'self' https://portal.example.com", string.Join(",", r.Headers.GetValues("Content-Security-Policy")));
        Assert.False(r.Headers.Contains("X-Frame-Options"));   // X-Frame-Options cannot name a portal
    }

    [Theory]
    [InlineData("'none'; script-src *", null)]
    [InlineData("'self'\r\nX-Evil: 1", null)]
    [InlineData("'unsafe-inline'", null)]
    [InlineData("'none' https://a.example", null)]
    [InlineData(null, "maybe")]
    public void B179_a_malformed_header_setting_stops_start_up_naming_it(string? frameAncestors, string? hsts)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SecurityHeaders.Parse(frameAncestors, hsts));
        Assert.Contains(frameAncestors is null ? "GridSim:Hsts" : "GridSim:FrameAncestors", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task B179_production_sends_hsts_on_https_for_a_public_name_and_it_can_be_turned_off()
    {
        await using (var f = WebFactory("Production", ("AllowedHosts", "gridsim.example.com")))
        {
            using HttpClient c = ClientFor(f, "https://gridsim.example.com/");
            using HttpResponseMessage r = await c.GetAsync(new Uri("/compliance", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("max-age=31536000", string.Join(",", r.Headers.GetValues("Strict-Transport-Security")));
            // Plain http never carries it.
            using HttpClient h = ClientFor(f, "http://gridsim.example.com/");
            using HttpResponseMessage plain = await h.GetAsync(new Uri("/compliance", UriKind.Relative));
            Assert.False(plain.Headers.Contains("Strict-Transport-Security"));
        }
        await using (var off = WebFactory("Production", ("AllowedHosts", "gridsim.example.com"), ("GridSim:Hsts", "false")))
        {
            using HttpClient c = ClientFor(off, "https://gridsim.example.com/");
            using HttpResponseMessage r = await c.GetAsync(new Uri("/compliance", UriKind.Relative));
            Assert.False(r.Headers.Contains("Strict-Transport-Security"));
        }
    }
}
