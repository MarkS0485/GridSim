using System.Net;
using GridSim.Web.Site;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// 2.1.074 RC-4-Fixes: the per-client request limit on /api and /console (<c>GridSim:MaxRequestsPerClientPerMinute</c>).
/// The default (600 a minute) is far above any existing test and use; the limiter is unit-tested with a hand-driven clock
/// and the real host is shown answering 429 with a Retry-After to one client while another is unaffected.
/// </summary>
public sealed class ClientRateLimiterTests : IDisposable
{
    private readonly string _dir = TestTemp.Under("gridsim-ratelimit-" + Guid.NewGuid().ToString("N"));

    public ClientRateLimiterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _ticks;
        public void AdvanceSeconds(double s) => _ticks += (long)(s * 1000);
    }

    private static IConfiguration Config(string? value) =>
        new ConfigurationBuilder().AddInMemoryCollection(value is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?> { [ClientRateLimiter.Setting] = value }).Build();

    [Fact]
    public void The_default_is_600_a_minute_and_zero_switches_it_off()
    {
        Assert.Equal(600, ClientRateLimiter.FromConfiguration(Config(null)).MaxPerMinute);
        Assert.Equal(600, ClientRateLimiter.FromConfiguration(Config("  ")).MaxPerMinute);
        ClientRateLimiter off = ClientRateLimiter.FromConfiguration(Config("0"));
        Assert.False(off.Enabled);
        for (int i = 0; i < 10_000; i++) Assert.True(off.TryAcquire("ip:1.2.3.4"));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("1000001")]
    public void A_malformed_setting_stops_start_up_naming_the_setting(string raw)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ClientRateLimiter.FromConfiguration(Config(raw)));
        Assert.Contains(ClientRateLimiter.Setting, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_client_is_refused_after_its_burst_and_served_again_as_the_bucket_refills()
    {
        var clock = new ManualClock();
        var limiter = new ClientRateLimiter(60, clock);   // 60 a minute: a burst of 60, then one a second
        for (int i = 0; i < 60; i++) Assert.True(limiter.TryAcquire("ip:203.0.113.5"), "request " + i);
        Assert.False(limiter.TryAcquire("ip:203.0.113.5"));
        Assert.True(limiter.TryAcquire("ip:203.0.113.6"));       // another client is untouched
        Assert.True(limiter.TryAcquire(null));                    // an unknown caller is never refused on a guess
        clock.AdvanceSeconds(1);
        Assert.True(limiter.TryAcquire("ip:203.0.113.5"));
        Assert.False(limiter.TryAcquire("ip:203.0.113.5"));
        Assert.Equal(1, limiter.RetryAfterSeconds);
    }

    [Fact]
    public void Only_api_and_console_are_covered()
    {
        Assert.True(ClientRateLimiter.Covers(new PathString("/api/solve")));
        Assert.True(ClientRateLimiter.Covers(new PathString("/API/cases")));
        Assert.True(ClientRateLimiter.Covers(new PathString("/console")));
        Assert.False(ClientRateLimiter.Covers(new PathString("/")));
        Assert.False(ClientRateLimiter.Covers(new PathString("/apiary")));
        Assert.False(ClientRateLimiter.Covers(new PathString("/health")));
    }

    private sealed class PeerFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (ctx, nxt) =>
                {
                    if (ctx.Request.Headers.TryGetValue("X-Test-Peer", out var v) && IPAddress.TryParse(v.ToString(), out var ip))
                        ctx.Connection.RemoteIpAddress = ip;
                    await nxt();
                });
                next(app);
            };
    }

    private WebApplicationFactory<Program> Host(string? limit) => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseSetting("Auth:KeysDirectory", Path.Combine(_dir, "keys-" + Guid.NewGuid().ToString("N")));
        b.UseSetting("GridSim:ReplayOnly", "false");
        b.UseSetting("Cycle:Enabled", "false");
        if (limit is not null) b.UseSetting(ClientRateLimiter.Setting, limit);
        b.ConfigureServices(s => s.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, PeerFilter>());
    });

    private static async Task<HttpResponseMessage> Get(HttpClient c, string path, string peer)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        req.Headers.Host = "localhost";
        req.Headers.Add("X-Test-Peer", peer);
        return await c.SendAsync(req);
    }

    [Fact]
    public async Task The_host_answers_429_with_a_Retry_After_to_one_client_and_not_to_another()
    {
        await using var f = Host("3");
        using HttpClient c = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        for (int i = 0; i < 3; i++)
        {
            using HttpResponseMessage ok = await Get(c, "/api/cases", "198.51.100.7");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        using HttpResponseMessage refused = await Get(c, "/api/cases", "198.51.100.7");
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.True(refused.Headers.TryGetValues("Retry-After", out var retry));
        Assert.Equal("20", retry!.Single());
        Assert.Contains(ClientRateLimiter.Setting, await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using HttpResponseMessage other = await Get(c, "/api/cases", "198.51.100.8");
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        using HttpResponseMessage page = await Get(c, "/health", "198.51.100.7");   // not a limited surface
        Assert.NotEqual(HttpStatusCode.TooManyRequests, page.StatusCode);
    }

    [Fact]
    public async Task With_the_default_setting_a_burst_of_requests_is_not_refused()
    {
        await using var f = Host(null);
        using HttpClient c = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        for (int i = 0; i < 30; i++)
        {
            using HttpResponseMessage r = await Get(c, "/api/cases", "198.51.100.9");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }
        Assert.Equal(600, f.Services.GetRequiredService<ClientRateLimiter>().MaxPerMinute);
    }
}
