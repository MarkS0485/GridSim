using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using GridSim.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F11 (release register section 6): the real GridSim.Web host, started in memory with WebApplicationFactory, not a
/// re-implementation of its routes. Before F11 nothing exercised Program.cs end to end: the middleware order, the
/// replay-only switch, the anonymous-visitor rule, the security.txt short-circuit, the version in the footer and the
/// OpenAPI document. Each test gets its own data-protection key folder under the temp directory, so the run never
/// writes keys into the source tree and never reads a real estate key ring.
/// </summary>
public sealed class WebHostTests
{
    /// <summary>A factory for the web app with the given settings layered on top (Development environment).</summary>
    private static WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings)
    {
        string keys = Path.Combine(Path.GetTempPath(), "gridsim-webhost-keys-" + Guid.NewGuid().ToString("N"));
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:KeysDirectory", keys);
            foreach ((string k, string v) in settings) b.UseSetting(k, v);
        });
    }

    private static HttpClient Client(WebApplicationFactory<Program> f)
        => f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task Home_page_serves_anonymously_and_shows_the_build_version()
    {
        await using var f = Factory();
        using HttpClient c = Client(f);
        using HttpResponseMessage r = await c.GetAsync(new Uri("/", UriKind.Relative));
        // Anonymous visitors are never bounced to a login page (single sign-on is off by default and only
        // recognises an existing estate login when it is on).
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        string html = await r.Content.ReadAsStringAsync();
        Assert.Contains("build-version", html, StringComparison.Ordinal);
        Assert.Contains($"v{BuildInfo.Version} ({BuildInfo.Commit})", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Openapi_document_carries_the_central_version()
    {
        await using var f = Factory(("GridSim:ReplayOnly", "false"));
        using HttpClient c = Client(f);
        using JsonDocument doc = JsonDocument.Parse(await c.GetStringAsync(new Uri("/api/openapi.json", UriKind.Relative)));
        Assert.Equal("3.0.3", doc.RootElement.GetProperty("openapi").GetString());
        Assert.Equal(BuildInfo.Version, doc.RootElement.GetProperty("info").GetProperty("version").GetString());
    }

    [Fact]
    public async Task Api_lists_cases_and_solves_case9_when_not_replay_only()
    {
        await using var f = Factory(("GridSim:ReplayOnly", "false"));
        using HttpClient c = Client(f);
        using JsonDocument cases = JsonDocument.Parse(await c.GetStringAsync(new Uri("/api/cases", UriKind.Relative)));
        Assert.Contains(cases.RootElement.EnumerateArray(), e => e.GetProperty("id").GetString() == "case9");

        using HttpResponseMessage solve = await c.PostAsJsonAsync(new Uri("/api/solve", UriKind.Relative), new { @case = "case9" });
        Assert.Equal(HttpStatusCode.OK, solve.StatusCode);
        string body = await solve.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(body));

        using HttpResponseMessage unknown = await c.GetAsync(new Uri("/api/topology?case=no-such-case", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Sim_websocket_accepts_a_connection_when_not_replay_only()
    {
        await using var f = Factory(("GridSim:ReplayOnly", "false"));
        using WebSocket ws = await f.Server.CreateWebSocketClient().ConnectAsync(new Uri(f.Server.BaseAddress, "/ws/sim"), CancellationToken.None);
        Assert.Equal(WebSocketState.Open, ws.State);

        // The documented contract: after a 'start' the server sends the one-time topology frame first.
        byte[] start = System.Text.Encoding.UTF8.GetBytes("{\"action\":\"start\",\"case\":\"case9\",\"source\":\"live\",\"fps\":5}");
        await ws.SendAsync(start, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var ms = new MemoryStream();
        var buffer = new byte[64 * 1024];
        WebSocketReceiveResult res;
        do
        {
            res = await ws.ReceiveAsync(buffer, timeout.Token);
            ms.Write(buffer, 0, res.Count);
        } while (!res.EndOfMessage);
        string first = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        Assert.Contains("topology", first, StringComparison.Ordinal);

        // Close our side only: waiting for the close reply is not part of the contract tested here. (Before N088 the
        // handler ended its loop on our close frame without echoing one; the full handshake is now pinned by
        // Sim_websocket_completes_the_close_handshake_when_the_client_closes.)
        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
    }

    [Fact]
    public async Task Replay_only_maps_neither_the_api_nor_the_sim_websocket()
    {
        await using var f = Factory(("GridSim:ReplayOnly", "true"));
        using HttpClient c = Client(f);
        foreach (string path in new[] { "/api/cases", "/api/openapi.json", "/ws/sim" })
        {
            using HttpResponseMessage r = await c.GetAsync(new Uri(path, UriKind.Relative));
            Assert.True(r.StatusCode == HttpStatusCode.NotFound, $"{path} answered {(int)r.StatusCode} on a replay-only instance");
        }
        using HttpResponseMessage post = await c.PostAsJsonAsync(new Uri("/api/solve", UriKind.Relative), new { @case = "case9" });
        Assert.NotEqual(HttpStatusCode.OK, post.StatusCode);
    }

    /// <summary>The security.txt settings used where a test needs the file served (N086: it is a deployment setting).</summary>
    private static readonly (string, string)[] SecurityTxtSettings =
    {
        ("Site:SecurityTxt:Contact", "mailto:security@example.com"),
        ("Site:SecurityTxt:Expires", "2099-12-31T23:59:59Z"),
    };

    [Fact]
    public async Task Security_txt_is_served_before_anything_else()
    {
        // Moved 2026-10 (N086, authorised by Mark's 2026-10-02 best-judgement ruling): the default deployment no longer
        // serves a security.txt (it named one person's estate contact in every deployment); the file is now served
        // only when a deployment configures Site:SecurityTxt:*, so this test configures it. Before: Factory() with no
        // settings, expecting 200 and "Contact: mailto:". After: Factory(SecurityTxtSettings), same expectations; the
        // default is pinned by Security_txt_answers_404_when_no_contact_is_configured.
        await using var f = Factory(SecurityTxtSettings);
        using HttpClient c = Client(f);
        using HttpResponseMessage r = await c.GetAsync(new Uri("/.well-known/security.txt", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Contact: mailto:", await r.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Security_txt_answers_404_when_no_contact_is_configured()
    {
        // N086: a neutral deployment publishes no security contact (and the old static wwwroot copy is gone, so the
        // static-file middleware cannot serve one either).
        await using var f = Factory();
        using HttpClient c = Client(f);
        using HttpResponseMessage r = await c.GetAsync(new Uri("/.well-known/security.txt", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Security_txt_is_exactly_the_configured_file()
    {
        await using var f = Factory(SecurityTxtSettings);
        using HttpClient c = Client(f);
        using HttpResponseMessage r = await c.GetAsync(new Uri("/.well-known/security.txt", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", r.Content.Headers.ContentType?.ToString());
        Assert.Equal("Contact: mailto:security@example.com\nExpires: 2099-12-31T23:59:59.000Z\nPreferred-Languages: en\n",
            await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Home_page_is_neutral_by_default()
    {
        // N086: no analytics, no host-specific share card or Organization block, no personal footer links.
        await using var f = Factory();
        using HttpClient c = Client(f);
        string html = await c.GetStringAsync(new Uri("/", UriKind.Relative));
        foreach (string absent in new[] { "googletagmanager", "gtag(", "analytics.js", "data-collector", "og:image",
                     "twitter:image", "application/ld+json", "twinscrollgridbalancer", "x.com/", "github.com/" })
            Assert.DoesNotContain(absent, html, StringComparison.OrdinalIgnoreCase);
        // The neutral, product-level metadata stays.
        Assert.Contains("og:title", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Home_page_carries_the_configured_site_values()
    {
        await using var f = Factory(
            ("Site:GoogleTagId", "G-TEST123456"),
            ("Site:AnalyticsScriptUrl", "https://stats.example.com/a.js"),
            ("Site:AnalyticsCollectorUrl", "https://stats.example.com"),
            ("Site:ShareImageUrl", "https://www.example.com/og.png"),
            ("Site:OrganisationName", "Example Org"),
            ("Site:FooterLinks:0:Text", "Source"),
            ("Site:FooterLinks:0:Url", "https://code.example.com/gridsim"));
        using HttpClient c = Client(f);
        string html = await c.GetStringAsync(new Uri("/", UriKind.Relative));
        Assert.Contains("https://www.googletagmanager.com/gtag/js?id=G-TEST123456", html, StringComparison.Ordinal);
        Assert.Contains("gtag('config', 'G-TEST123456');", html, StringComparison.Ordinal);
        Assert.Contains("src=\"https://stats.example.com/a.js\"", html, StringComparison.Ordinal);
        Assert.Contains("data-collector=\"https://stats.example.com\"", html, StringComparison.Ordinal);
        Assert.Contains("content=\"https://www.example.com/og.png\"", html, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"Example Org\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://code.example.com/gridsim\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_malformed_site_setting_stops_start_up()
    {
        // Fail loud: the host refuses to start rather than render a broken page.
        using var f = Factory(("Site:ShareImageUrl", "images/og.png"));
        var ex = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        Assert.Contains("Site:ShareImageUrl", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Data_protection_application_name_defaults_to_the_product_name()
    {
        // N087: an unset Auth:ApplicationName used to fall back to one estate's name in code.
        using var f = Factory();
        var dp = f.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.DataProtection.DataProtectionOptions>>();
        Assert.Equal("GridSim", dp.Value.ApplicationDiscriminator);

        using var g = Factory(("Auth:ApplicationName", "SomeEstate"));
        var dpg = g.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.DataProtection.DataProtectionOptions>>();
        Assert.Equal("SomeEstate", dpg.Value.ApplicationDiscriminator);
    }

    [Fact]
    public async Task Sim_websocket_completes_the_close_handshake_when_the_client_closes()
    {
        // N088: the client starts the close; the server must answer with its own close frame (echoing the client's
        // status) so the client's CloseAsync completes with the socket Closed, not dropped mid-handshake.
        await using var f = Factory(("GridSim:ReplayOnly", "false"));
        using WebSocket ws = await f.Server.CreateWebSocketClient().ConnectAsync(new Uri(f.Server.BaseAddress, "/ws/sim"), CancellationToken.None);
        Assert.Equal(WebSocketState.Open, ws.State);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "client done", timeout.Token);
        Assert.Equal(WebSocketState.Closed, ws.State);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, ws.CloseStatus);
        Assert.Equal("client done", ws.CloseStatusDescription);
    }

    [Fact]
    public async Task Dashboard_runs_the_solver_named_in_the_address_and_refuses_an_unknown_one()
    {
        // N063: ?solver= picks a live session's engine (the header picker navigates to it); a bad name fails loud
        // on the load-error panel instead of silently running Auto.
        await using var f = Factory(("GridSim:ReplayOnly", "false"));
        using HttpClient c = Client(f);
        string sparse = await c.GetStringAsync(new Uri("/dashboard/case9?solver=sparse", UriKind.Relative));
        Assert.Contains("Newton-Raphson (sparse)", sparse, StringComparison.Ordinal);
        Assert.Contains("solver-pick", sparse, StringComparison.Ordinal);
        string bad = await c.GetStringAsync(new Uri("/dashboard/case9?solver=quantum", UriKind.Relative));
        // (The apostrophes are HTML-encoded in the page, so match the parts either side of them.)
        Assert.Contains("Unknown solver", bad, StringComparison.Ordinal);
        Assert.Contains("quantum", bad, StringComparison.Ordinal);
        Assert.Contains("Use one of: auto, dense, sparse, sweep.", bad, StringComparison.Ordinal);
        Assert.DoesNotContain("solver-pick", bad, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sim_websocket_start_with_an_unknown_solver_answers_an_error_frame()
    {
        await using var f = Factory(("GridSim:ReplayOnly", "false"));
        using WebSocket ws = await f.Server.CreateWebSocketClient().ConnectAsync(new Uri(f.Server.BaseAddress, "/ws/sim"), CancellationToken.None);
        byte[] start = System.Text.Encoding.UTF8.GetBytes("{\"action\":\"start\",\"case\":\"case9\",\"solver\":\"quantum\"}");
        await ws.SendAsync(start, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var ms = new MemoryStream();
        var buffer = new byte[64 * 1024];
        WebSocketReceiveResult res;
        do
        {
            res = await ws.ReceiveAsync(buffer, timeout.Token);
            ms.Write(buffer, 0, res.Count);
        } while (!res.EndOfMessage);
        using JsonDocument frame = JsonDocument.Parse(ms.ToArray());
        Assert.Equal("error", frame.RootElement.GetProperty("type").GetString());
        Assert.Contains("Unknown solver 'quantum'", frame.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
    }

    /// <summary>Start case9 on /ws/sim and return the first 'state' frame's assetGraph property kind.</summary>
    private static async Task<JsonValueKind> FirstStateAssetGraphKind(string startJson)
    {
        await using var f = Factory(("GridSim:ReplayOnly", "false"));
        using WebSocket ws = await f.Server.CreateWebSocketClient().ConnectAsync(new Uri(f.Server.BaseAddress, "/ws/sim"), CancellationToken.None);
        await ws.SendAsync(System.Text.Encoding.UTF8.GetBytes(startJson), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var buffer = new byte[64 * 1024];
        for (int k = 0; k < 10; k++)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult res;
            do
            {
                res = await ws.ReceiveAsync(buffer, timeout.Token);
                ms.Write(buffer, 0, res.Count);
            } while (!res.EndOfMessage);
            using JsonDocument d = JsonDocument.Parse(ms.ToArray());
            if (d.RootElement.GetProperty("type").GetString() != "state") continue;
            JsonValueKind kind = d.RootElement.GetProperty("assetGraph").ValueKind;
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
            return kind;
        }
        throw new InvalidOperationException("no state frame within 10 messages");
    }

    [Fact]
    public async Task Sim_websocket_sends_the_asset_graph_only_when_asked()
    {
        // N022: the per-frame ASSETS graph is opt-in on /ws/sim.
        Assert.Equal(JsonValueKind.Null, await FirstStateAssetGraphKind("{\"action\":\"start\",\"case\":\"case9\"}"));
        Assert.Equal(JsonValueKind.Object, await FirstStateAssetGraphKind("{\"action\":\"start\",\"case\":\"case9\",\"assetGraph\":true}"));
    }

    [Fact]
    public async Task Sso_endpoints_are_off_by_default()
    {
        // F05: appsettings.json ships Sso:Enabled=false, so the ticket endpoints answer 404 and nothing redirects.
        await using var f = Factory();
        using HttpClient c = Client(f);
        using HttpResponseMessage bridge = await c.GetAsync(new Uri("/sso/bridge", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, bridge.StatusCode);
    }
}
