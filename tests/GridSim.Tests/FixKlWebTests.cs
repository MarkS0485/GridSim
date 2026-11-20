using System.Net;
using System.Text;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Security;
using GridSim.Web.Site;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GridSim.Tests;

/// <summary>Post-RC3 batch K web fixes: APP-03 (private-model rule on POST /api/solve).</summary>
public sealed class FixKlWebTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-fixkl-" + Guid.NewGuid().ToString("N"));

    public FixKlWebTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings)
    {
        string keys = Path.Combine(_dir, "keys-" + Guid.NewGuid().ToString("N"));
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:KeysDirectory", keys);
            b.UseSetting("GridSim:ReplayOnly", "false");
            b.UseSetting("Cycle:Enabled", "false");
            foreach ((string k, string v) in settings) b.UseSetting(k, v);
        });
    }

    private static HttpClient Client(WebApplicationFactory<Program> f)
        => f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    private string MakeRoot()
    {
        string root = Path.Combine(_dir, "root");
        string exports = Path.Combine(root, "results", "kl-run", "exports");
        Directory.CreateDirectory(exports);
        var trace = new SolveTrace();
        GridModel m = IeeeCases.Case9();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Trace = trace });
        Assert.True(r.Converged);
        var meta = new SolveExportMeta("kl-run", "cpu", "c", "2026-10-05T00:00:00Z", 1.0, "kl-test");
        SolveExport.WriteJson(Path.Combine(exports, "slice-000000001.json"), m, r, trace, meta);
        return root;
    }

    private string LogPath => Path.Combine(_dir, "audit.jsonl");

    private int AccessDeniedEntries() =>
        !File.Exists(LogPath) ? 0 : AuditLogFile.Load(LogPath).Count(e =>
            SecurityAudit.TryRead(e, out SecurityEventKind kind, out _) && kind == SecurityEventKind.AccessDenied);

    private static StringContent Body(string id) => new($"{{\"case\":\"{id}\"}}", Encoding.UTF8, "application/json");

    [Fact]
    public void App09_compose_passes_the_anonymous_role_and_no_longer_says_there_is_no_authorisation()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string compose = File.ReadAllText(Path.Combine(dir!.FullName, "compose.yml"));
        Assert.Contains("GridSim__AnonymousRole: \"${GRIDSIM_ANONYMOUS_ROLE:-}\"", compose, StringComparison.Ordinal);
        Assert.Contains("GridSim__AllowedOrigins:", compose, StringComparison.Ordinal);
        Assert.Contains("GridSim__MaxSessionsPerClient:", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("no authorisation yet", compose, StringComparison.Ordinal);
    }

    [Fact]
    public async Task App06_every_session_kind_forwards_each_new_error_once()
    {
        GridModel case9 = IeeeCases.Case9();
        GridSim.Web.Sessions.SimSession s = GridSim.Web.Sessions.SimSession.CreateLive("t", "case9", "IEEE 9", case9, holdSteady: true);
        try
        {
            var outbound = new GridSim.Web.Api.SimOutbound();
            long gen = outbound.BeginSession();
            GridSim.Web.Api.SimWebSocket.WireErrorForwarding(s, outbound, gen);
            int Drain() { int n = 0; while (outbound.Reader.TryRead(out _)) n++; return n; }
            Assert.Equal(0, Drain());
            s.SetErrorForTests("The session stopped: step failed.");
            Assert.Equal(1, Drain());
            s.SetErrorForTests("The session stopped: step failed.");   // unchanged: not repeated
            Assert.Equal(0, Drain());
            s.SetErrorForTests(null);                                   // a reconnect clears it
            Assert.Equal(0, Drain());
            s.SetErrorForTests("The session stopped: step failed.");   // a later failure is forwarded again
            Assert.Equal(1, Drain());
            s.SetErrorForTests("Bridge gave up.");
            Assert.Equal(1, Drain());
        }
        finally { await s.DisposeAsync(); }
    }

    [Fact]
    public async Task App05_a_second_trip_of_the_same_unit_is_refused_with_the_desktop_wording()
    {
        GridModel case9 = IeeeCases.Case9();
        GridSim.Web.Sessions.SimSession s = GridSim.Web.Sessions.SimSession.CreateLive("t", "case9", "IEEE 9", case9, holdSteady: true);
        try
        {
            var first = s.TripLargestInfeed(null);
            Assert.True(first.Tripped, first.Message);
            var second = s.TripLargestInfeed(null);
            Assert.False(second.Tripped);
            Assert.Contains("has already been tripped in this run", second.Message, StringComparison.Ordinal);
            Assert.Equal(0.0, second.LostMw);
        }
        finally { await s.DisposeAsync(); }
    }

    /// <summary>Sets the connection's remote address from the X-Test-Peer header (TestServer has none), ahead of the app.</summary>
    private sealed class PeerFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (ctx, nxt) =>
                {
                    if (ctx.Request.Headers.TryGetValue("X-Test-Peer", out var v) && System.Net.IPAddress.TryParse(v.ToString(), out var ip))
                        ctx.Connection.RemoteIpAddress = ip;
                    await nxt();
                });
                next(app);
            };
    }

    private static async Task<HttpStatusCode> Get(HttpClient c, string host, string? peer, params (string, string)[] extra)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri("/", UriKind.Relative));
        req.Headers.Host = host;
        if (peer is not null) req.Headers.Add("X-Test-Peer", peer);
        foreach ((string k, string v) in extra) req.Headers.Add(k, v);
        using HttpResponseMessage r = await c.SendAsync(req);
        return r.StatusCode;
    }

    [Fact]
    public async Task App02_a_loopback_probe_is_answered_under_a_custom_AllowedHosts_but_other_hosts_and_peers_are_refused()
    {
        await using var f = Factory(("AllowedHosts", "gridsim.example.com")).WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, PeerFilter>()));
        using HttpClient c = Client(f);
        Assert.NotEqual(HttpStatusCode.BadRequest, await Get(c, "localhost", "127.0.0.1"));
        Assert.NotEqual(HttpStatusCode.BadRequest, await Get(c, "localhost:8080", "::1"));
        Assert.NotEqual(HttpStatusCode.BadRequest, await Get(c, "gridsim.example.com", "203.0.113.5"));
        Assert.Equal(HttpStatusCode.BadRequest, await Get(c, "attacker.example", "127.0.0.1"));
        Assert.Equal(HttpStatusCode.BadRequest, await Get(c, "localhost", "203.0.113.5"));
        Assert.Equal(HttpStatusCode.BadRequest, await Get(c, "localhost", null));
        Assert.Equal(HttpStatusCode.BadRequest, await Get(c, "localhost", "127.0.0.1", ("X-Forwarded-For", "203.0.113.5")));
    }

    [Fact]
    public async Task App03_solve_of_a_private_model_is_403_for_an_analyst_with_one_audit_entry_and_a_bundled_case_still_solves()
    {
        string root = MakeRoot();
        string windows = Path.Combine(_dir, "windows");
        Directory.CreateDirectory(windows);
        await using (var analyst = Factory(("GridSim:AnonymousRole", "Analyst"), ("GridSim:CaseRoots", root),
            ("Realtime:Enabled", "true"), ("Realtime:Case", "case9"), ("Realtime:WindowsDir", windows), ("Audit:LogPath", LogPath)))
        {
            using HttpClient c = Client(analyst);
            using (HttpResponseMessage bundled = await c.PostAsync(new Uri("/api/solve", UriKind.Relative), Body("case9")))
                Assert.Equal(HttpStatusCode.OK, bundled.StatusCode);
            Assert.Equal(0, AccessDeniedEntries());
            using (HttpResponseMessage denied = await c.PostAsync(new Uri("/api/solve", UriKind.Relative), Body("run-kl-run")))
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal(1, AccessDeniedEntries());
            using (HttpResponseMessage unknown = await c.PostAsync(new Uri("/api/solve", UriKind.Relative), Body("no-such-case")))
                Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.Equal(1, AccessDeniedEntries());
        }
        // An Operator passes the private rule (the replay is then refused as a replay, 400, not as a role denial).
        await using (var op = Factory(("GridSim:AnonymousRole", "Operator"), ("GridSim:CaseRoots", root)))
        {
            using HttpClient c = Client(op);
            using HttpResponseMessage r = await c.PostAsync(new Uri("/api/solve", UriKind.Relative), Body("run-kl-run"));
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }
    }
}
