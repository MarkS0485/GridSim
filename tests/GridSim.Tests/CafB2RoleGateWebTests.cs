using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.Security;
using GridSim.Web.Sessions;
using GridSim.Web.Site;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// NCSC CAF B2 (November 2026): the web host enforces <see cref="RoleGuard"/> on its solve surface. Before, the gate was
/// a primitive no host called (gap register item 1). These tests pin the closure through the real GridSim.Web host in
/// memory and through <see cref="AccessControl"/> directly:
/// <list type="bullet">
/// <item>the band each surface needs (POST /api/solve Analyst, GET /api/topology Viewer, every /ws/sim control Operator),
/// an allowed band passes, and each denied band on each surface is refused (403, or an error frame) before anything
/// runs, with no side effect (no case lookup, no session created or replaced);</item>
/// <item>each denial writes exactly one AccessDenied entry through the host's audit writer, both while the resident
/// estimator's run holds the log open and after it has finished and closed it; an allowed call writes none;</item>
/// <item>a host with no audit log still refuses, and a failure to record never replaces the refusal;</item>
/// <item>the default anonymous role (Operator on a loopback-only instance, Viewer otherwise), the setting's fail-loud
/// parsing, and the mapping of a signed-in identity's GridSim.&lt;role&gt; claims.</item>
/// </list>
/// </summary>
public sealed class CafB2RoleGateWebTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-b2-rolegate-" + Guid.NewGuid().ToString("N"));

    public CafB2RoleGateWebTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private string LogPath => Path.Combine(_dir, "audit.jsonl");

    /// <summary>The web app with the given settings layered on top (as WebHostTests builds it).</summary>
    private WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings)
    {
        string keys = Path.Combine(_dir, "keys-" + Guid.NewGuid().ToString("N"));
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:KeysDirectory", keys);
            foreach ((string k, string v) in settings) b.UseSetting(k, v);
        });
    }

    /// <summary>A solve-capable host whose anonymous callers hold <paramref name="role"/>.</summary>
    private WebApplicationFactory<Program> HostAs(OperationRole role, params (string Key, string Value)[] more) =>
        Factory(new[] { ("GridSim:ReplayOnly", "false"), ("Cycle:Enabled", "false"), ("GridSim:AnonymousRole", role.ToString()) }
            .Concat(more).ToArray());

    /// <summary>The same host with the resident estimator on (an empty windows folder) and an audit log.</summary>
    private WebApplicationFactory<Program> AuditedHostAs(OperationRole role)
    {
        string windows = Path.Combine(_dir, "windows");
        Directory.CreateDirectory(windows);
        return HostAs(role, ("Realtime:Enabled", "true"), ("Realtime:Case", "case9"), ("Realtime:WindowsDir", windows),
            ("Audit:LogPath", LogPath));
    }

    private static HttpClient Client(WebApplicationFactory<Program> f)
        => f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    private static Task Send(WebSocket ws, string text) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    /// <summary>Read exactly the next frame.</summary>
    private static async Task<JsonDocument> ReadNext(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult res;
        do
        {
            res = await ws.ReceiveAsync(buffer, ct);
            ms.Write(buffer, 0, res.Count);
        } while (!res.EndOfMessage);
        return JsonDocument.Parse(ms.ToArray());
    }

    /// <summary>Read frames until one of type <paramref name="type"/> arrives (at most 20).</summary>
    private static async Task<JsonDocument> ReadUntil(WebSocket ws, string type, CancellationToken ct)
    {
        for (int k = 0; k < 20; k++)
        {
            JsonDocument d = await ReadNext(ws, ct);
            if (d.RootElement.GetProperty("type").GetString() == type) return d;
            d.Dispose();
        }
        throw new InvalidOperationException($"no '{type}' frame within 20 messages");
    }

    /// <summary>How many AccessDenied security entries the log holds (0 when there is no log yet).</summary>
    private int AccessDeniedEntries() =>
        !File.Exists(LogPath) ? 0 : AuditLogFile.Load(LogPath).Count(e =>
            SecurityAudit.TryRead(e, out SecurityEventKind kind, out _) && kind == SecurityEventKind.AccessDenied);

    private static async Task<string> ErrorText(HttpResponseMessage r)
    {
        using JsonDocument body = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("error").GetString()!;
    }

    /// <summary>Every /ws/sim control, as a frame that would otherwise act (values present where one is needed).</summary>
    private static readonly (string Action, string Frame)[] SimControls =
    {
        ("start", "{\"action\":\"start\",\"case\":\"case9\"}"),
        ("play", "{\"action\":\"play\"}"),
        ("pause", "{\"action\":\"pause\"}"),
        ("step", "{\"action\":\"step\"}"),
        ("seek", "{\"action\":\"seek\",\"value\":0.5}"),
        ("speed", "{\"action\":\"speed\",\"value\":5}"),
        ("trip", "{\"action\":\"trip\",\"value\":10}"),
    };

    // ---------------------------------------------------------------------------------------------------------------
    // The surface map: one place states each band, and it is RoleGuard's own bands.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void B2_each_surface_needs_the_documented_band()
    {
        Assert.Equal(OperationRole.Analyst, AccessControl.SolveRole);
        Assert.Equal("api.solve", AccessControl.SolveOperation);
        Assert.Equal(OperationRole.Viewer, AccessControl.TopologyRole);
        Assert.Equal("api.topology", AccessControl.TopologyOperation);
        Assert.Equal(OperationRole.Operator, AccessControl.SimControlRole);
        Assert.Equal("ws.sim.", AccessControl.SimOperationPrefix);
        // The /ws/sim controls the gate covers are exactly the actions the socket understands.
        Assert.Equal(GridSim.Web.Api.SimWebSocket.Actions.ToArray(), SimControls.Select(c => c.Action).ToArray());
    }

    // ---------------------------------------------------------------------------------------------------------------
    // POST /api/solve (Analyst) and GET /api/topology (Viewer).
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task B2_a_viewer_is_refused_a_solve_with_403_before_the_body_or_the_case_is_looked_at_and_no_log_still_refuses()
    {
        // No Audit:LogPath: the host has no audit log, and the denial is still refused.
        await using var f = HostAs(OperationRole.Viewer);
        Assert.False(f.Services.GetRequiredService<AccessControl>().AuditsDenials);
        using HttpClient c = Client(f);

        using (HttpResponseMessage r = await c.PostAsJsonAsync(new Uri("/api/solve", UriKind.Relative), new { @case = "case9" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal("Access denied: 'api.solve' requires role Analyst or higher, but the caller has Viewer.", await ErrorText(r));
        }
        // No side effect: the gate runs first, so an unknown case is a 403 (not the catalog's 404) and a body with no case
        // is a 403 (not the 400): nothing was looked up, loaded or solved for the refused caller.
        using (HttpResponseMessage r = await c.PostAsJsonAsync(new Uri("/api/solve", UriKind.Relative), new { @case = "no-such-case" }))
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        using (HttpResponseMessage r = await c.PostAsJsonAsync(new Uri("/api/solve", UriKind.Relative), new { other = 1 }))
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.False(File.Exists(LogPath));

        // Topology is a read: a viewer may have it.
        using (HttpResponseMessage r = await c.GetAsync(new Uri("/api/topology?case=case9", UriKind.Relative)))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Theory]
    [InlineData(OperationRole.Analyst)]
    [InlineData(OperationRole.Operator)]
    [InlineData(OperationRole.Administrator)]
    public async Task B2_an_analyst_or_higher_may_solve_and_read_topology(OperationRole role)
    {
        await using var f = HostAs(role);
        using HttpClient c = Client(f);
        using (HttpResponseMessage r = await c.PostAsJsonAsync(new Uri("/api/solve", UriKind.Relative), new { @case = "case9" }))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using (HttpResponseMessage r = await c.GetAsync(new Uri("/api/topology?case=case9", UriKind.Relative)))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        // An allowed caller still gets the ordinary answers (the gate changes nothing past it).
        using (HttpResponseMessage r = await c.PostAsJsonAsync(new Uri("/api/solve", UriKind.Relative), new { @case = "no-such-case" }))
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // /ws/sim (Operator for every control).
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(OperationRole.Viewer)]
    [InlineData(OperationRole.Analyst)]
    public async Task B2_every_sim_control_below_operator_gets_an_error_frame_and_creates_no_session(OperationRole role)
    {
        await using var f = HostAs(role);
        SessionManager sessions = f.Services.GetRequiredService<SessionManager>();
        using WebSocket ws = await f.Server.CreateWebSocketClient().ConnectAsync(new Uri(f.Server.BaseAddress, "/ws/sim"), CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        foreach ((string action, string frame) in SimControls)
        {
            await Send(ws, frame);
            // The very next frame is the refusal: no topology, no state, nothing from a session.
            using JsonDocument e = await ReadNext(ws, timeout.Token);
            Assert.Equal("error", e.RootElement.GetProperty("type").GetString());
            Assert.Equal($"Access denied: 'ws.sim.{action}' requires role Operator or higher, but the caller has {role}.",
                e.RootElement.GetProperty("message").GetString());
            Assert.Equal(0, sessions.ActiveSessions);
        }
        // The connection stays open after a refusal, and malformed frames are still answered as before (the gate sits
        // after the action is known to be real).
        await Send(ws, "{\"action\":\"dance\"}");
        using (JsonDocument e = await ReadNext(ws, timeout.Token))
            Assert.Contains("unknown action 'dance'", e.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(WebSocketState.Open, ws.State);
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
    }

    [Fact]
    public async Task B2_an_operator_may_start_and_drive_a_session()
    {
        await using var f = HostAs(OperationRole.Operator);
        using WebSocket ws = await f.Server.CreateWebSocketClient().ConnectAsync(new Uri(f.Server.BaseAddress, "/ws/sim"), CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await Send(ws, "{\"action\":\"start\",\"case\":\"case9\"}");
        using (await ReadUntil(ws, "topology", timeout.Token)) { }
        Assert.Equal(1, f.Services.GetRequiredService<SessionManager>().ActiveSessions);
        await Send(ws, "{\"action\":\"pause\"}");
        await Send(ws, "{\"action\":\"step\"}");
        using (await ReadUntil(ws, "state", timeout.Token)) { }
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The audit: one AccessDenied entry per denial, through the host's audit writer.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task B2_each_denial_on_each_surface_writes_exactly_one_access_denied_entry_in_the_host_audit_log()
    {
        await using (var f = AuditedHostAs(OperationRole.Viewer))
        {
            Assert.True(f.Services.GetRequiredService<AccessControl>().AuditsDenials);
            using HttpClient c = Client(f);
            Assert.Equal(0, AccessDeniedEntries());

            // An allowed read writes nothing.
            using (HttpResponseMessage ok = await c.GetAsync(new Uri("/api/topology?case=case9", UriKind.Relative)))
                Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal(0, AccessDeniedEntries());

            using (HttpResponseMessage r = await c.PostAsJsonAsync(new Uri("/api/solve", UriKind.Relative), new { @case = "case9" }))
                Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal(1, AccessDeniedEntries());

            using WebSocket ws = await f.Server.CreateWebSocketClient().ConnectAsync(new Uri(f.Server.BaseAddress, "/ws/sim"), CancellationToken.None);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            int expected = 1;
            foreach ((string action, string frame) in SimControls)
            {
                await Send(ws, frame);
                using JsonDocument e = await ReadNext(ws, timeout.Token);
                Assert.Equal("error", e.RootElement.GetProperty("type").GetString());
                expected++;
                Assert.Equal(expected, AccessDeniedEntries());
                // The entry names the operation and both bands, never a secret.
                AuditLogEntry last = AuditLogFile.Load(LogPath).Last(x =>
                    SecurityAudit.TryRead(x, out SecurityEventKind k, out _) && k == SecurityEventKind.AccessDenied);
                Assert.Contains($"outcome=event=AccessDenied severity=Low reason=operation=ws.sim.{action} required=Operator actual=Viewer subject=anonymous correlation=",
                    last.Summary, StringComparison.Ordinal);
            }
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
        }
        // The log still verifies end to end after the host has stopped.
        Assert.True(AuditLogFile.Verify(LogPath).IsValid);
        Assert.Equal(1 + SimControls.Length, AccessDeniedEntries());
    }

    [Fact]
    public void B2_a_denial_is_recorded_through_the_open_run_and_after_the_run_has_closed_the_log()
    {
        AuditRunRecorder run = AuditRunRecorder.Start(LogPath, "web@host", "web-realtime", ProvenanceTag.DerivedFromRecorded);
        var access = new AccessControl(OperationRole.Viewer, anonymousRoleIsDefault: false,
            new AccessDenialAudit(run, AuditRecorderOptions.None));
        IdentityContext who = access.IdentityFor(null, "req-1");

        // While the run is open: through its recorder.
        AccessDeniedException first = Assert.Throws<AccessDeniedException>(() =>
            access.Require(who, AccessControl.SolveRole, AccessControl.SolveOperation));
        Assert.Equal(OperationRole.Viewer, first.Actual);
        Assert.Equal(1, run.SecurityEventCount);
        // An allowed call writes nothing.
        access.Require(who, AccessControl.TopologyRole, AccessControl.TopologyOperation);
        Assert.Equal(1, run.SecurityEventCount);

        // The run finishes and closes the log (as the resident estimator does once its windows are done).
        Assert.NotNull(run.Finish(0));
        Assert.True(run.IsFinished);
        Assert.Throws<AccessDeniedException>(() => access.Require(who, AccessControl.SimControlRole, "ws.sim.start"));

        IReadOnlyList<AuditLogEntry> entries = AuditLogFile.Load(LogPath);
        Assert.Equal(3, entries.Count);   // denial, the run's own entry, denial: no extra run entry for the reopen
        Assert.Equal("actor=web@host action=security inputs=- outcome=event=AccessDenied severity=Low reason=operation=api.solve required=Analyst actual=Viewer subject=anonymous correlation=req-1",
            entries[0].Summary);
        Assert.Equal("actor=web@host action=web-realtime inputs=- outcome=exit=0", entries[1].Summary);
        Assert.Equal("actor=web@host action=security inputs=- outcome=event=AccessDenied severity=Low reason=operation=ws.sim.start required=Operator actual=Viewer subject=anonymous correlation=req-1",
            entries[2].Summary);
        Assert.True(AuditLogFile.Verify(LogPath).IsValid);
        // The anchor was rewritten for the reopened entry, so the next run opens the log against it.
        Assert.Equal(3, AuditAnchorFile.ReadIfExists(LogPath + ".anchor")!.Count);
        Assert.Equal(4, AuditRunRecorder.Start(LogPath, "web@host", "web-realtime", ProvenanceTag.DerivedFromRecorded).Finish(0)!.Count);
    }

    [Fact]
    public void B2_a_log_that_cannot_be_written_never_replaces_the_refusal_and_no_log_records_nothing()
    {
        AuditRunRecorder run = AuditRunRecorder.Start(LogPath, "web@host", "web-realtime", ProvenanceTag.DerivedFromRecorded);
        run.Finish(0);
        var access = new AccessControl(OperationRole.Viewer, anonymousRoleIsDefault: false,
            new AccessDenialAudit(run, AuditRecorderOptions.None));
        IdentityContext who = access.IdentityFor(null, "req-2");

        // Another writer holds the log: the denial cannot be recorded, and the caller still gets the AccessDeniedException
        // (the failure is reported on standard error), never an InvalidOperationException in its place.
        using (AuditLogWriter.Open(LogPath))
            Assert.Throws<AccessDeniedException>(() => access.Require(who, AccessControl.SolveRole, AccessControl.SolveOperation));
        Assert.Single(AuditLogFile.Load(LogPath));   // only the run's own entry

        // No audit log at all: refused, nothing anywhere.
        var unaudited = new AccessControl(OperationRole.Viewer, anonymousRoleIsDefault: false, AccessDenialAudit.None);
        Assert.False(unaudited.AuditsDenials);
        Assert.Throws<AccessDeniedException>(() => unaudited.Require(who, AccessControl.SolveRole, AccessControl.SolveOperation));
        Assert.Single(AuditLogFile.Load(LogPath));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The default, the setting and the identity mapping.
    // ---------------------------------------------------------------------------------------------------------------

    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))).Build();

    [Theory]
    [InlineData(null, OperationRole.Operator)]                         // the default list: loopback names only
    [InlineData("localhost", OperationRole.Operator)]
    [InlineData("127.0.0.1;[::1]", OperationRole.Operator)]
    [InlineData("gridsim.example.com", OperationRole.Viewer)]
    [InlineData("localhost;gridsim.example.com", OperationRole.Viewer)]
    [InlineData("*.example.com", OperationRole.Viewer)]
    [InlineData("*", OperationRole.Viewer)]
    public void B2_the_default_anonymous_role_is_operator_only_when_every_answered_name_is_loopback(string? allowedHosts, OperationRole expected)
    {
        HostAllowList hosts = HostAllowList.Parse(allowedHosts);
        Assert.Equal(expected == OperationRole.Operator, hosts.LoopbackOnly);
        AccessControl a = AccessControl.FromConfiguration(Config(), hosts, AccessDenialAudit.None);
        Assert.Equal(expected, a.AnonymousRole);
        Assert.True(a.AnonymousRoleIsDefault);
    }

    [Theory]
    [InlineData("viewer", OperationRole.Viewer)]
    [InlineData("Analyst", OperationRole.Analyst)]
    [InlineData(" OPERATOR ", OperationRole.Operator)]
    [InlineData("Administrator", OperationRole.Administrator)]
    public void B2_the_anonymous_role_setting_names_a_band_case_insensitively_and_overrides_the_default(string value, OperationRole expected)
    {
        AccessControl a = AccessControl.FromConfiguration(Config(("GridSim:AnonymousRole", value)), HostAllowList.Parse("gridsim.example.com"), AccessDenialAudit.None);
        Assert.Equal(expected, a.AnonymousRole);
        Assert.False(a.AnonymousRoleIsDefault);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("-1")]
    [InlineData("Root")]
    [InlineData("Operator,Viewer")]
    [InlineData("GridSim.Operator")]
    public void B2_a_malformed_anonymous_role_stops_start_up_naming_the_setting(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AccessControl.FromConfiguration(Config(("GridSim:AnonymousRole", value)), HostAllowList.Parse(null), AccessDenialAudit.None));
        Assert.Contains("GridSim:AnonymousRole", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Viewer, Analyst, Operator, Administrator", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void B2_a_malformed_anonymous_role_stops_the_web_host_naming_the_setting()
    {
        using var f = Factory(("GridSim:AnonymousRole", "Superuser"));
        var ex = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        string text = ex.ToString();
        Assert.Contains("GridSim:AnonymousRole", text, StringComparison.Ordinal);
        Assert.Contains("Superuser", text, StringComparison.Ordinal);
    }

    private static ClaimsPrincipal SignedIn(string? name, params string[] roles)
    {
        var claims = new List<Claim>();
        if (name is not null) claims.Add(new Claim(ClaimTypes.Name, name));
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test", ClaimTypes.Name, ClaimTypes.Role));
    }

    [Fact]
    public void B2_a_signed_in_caller_holds_the_highest_gridsim_role_claim_and_never_less_than_an_anonymous_one()
    {
        var a = new AccessControl(OperationRole.Viewer, anonymousRoleIsDefault: true, AccessDenialAudit.None);

        IdentityContext anon = a.IdentityFor(null, "r1");
        Assert.Equal(new IdentityContext("anonymous", OperationRole.Viewer, "r1"), anon);
        // An unauthenticated principal (no authentication type) is anonymous whatever claims it carries.
        var unauthenticated = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, "GridSim.Administrator") }));
        Assert.Equal(OperationRole.Viewer, a.IdentityFor(unauthenticated, "r2").Role);

        Assert.Equal(new IdentityContext("alice", OperationRole.Analyst, "r3"), a.IdentityFor(SignedIn("alice", "GridSim.Analyst"), "r3"));
        Assert.Equal(OperationRole.Operator, a.IdentityFor(SignedIn("bob", "GridSim.Analyst", "gridsim.operator", "GridSim.Viewer"), "r4").Role);
        // An estate role without the GridSim. prefix, or an unknown band, grants nothing.
        Assert.Equal(OperationRole.Viewer, a.IdentityFor(SignedIn("carol", "Administrator", "GridSim.Root", "GridSim.3"), "r5").Role);
        // Signed in with no name claim: named "signed-in", never confused with anonymous.
        Assert.Equal("signed-in", a.IdentityFor(SignedIn(null), "r6").Subject);

        // A signed-in caller never holds less than an anonymous one.
        var loopback = new AccessControl(OperationRole.Operator, anonymousRoleIsDefault: true, AccessDenialAudit.None);
        Assert.Equal(OperationRole.Operator, loopback.IdentityFor(SignedIn("dave", "GridSim.Viewer"), "r7").Role);
        Assert.Equal(OperationRole.Administrator, loopback.IdentityFor(SignedIn("erin", "GridSim.Administrator"), "r8").Role);
    }

    // U+2028 LINE SEPARATOR, built from its code so the source stays plain ASCII. It is not a control character, so it
    // would pass a control-character fold, yet the audit summary refuses it (GR-853): the reason must not carry it.
    private static readonly string LineSeparator = char.ConvertFromUtf32(0x2028);

    [Fact]
    public void B2_the_recorded_reason_reduces_the_subject_and_correlation_to_audit_tokens()
    {
        var who = new IdentityContext("eve smith" + LineSeparator + "event=Forged", OperationRole.Viewer, "c 1");
        var denial = new AccessDeniedException("api.solve", OperationRole.Analyst, OperationRole.Viewer);
        Assert.Equal("operation=api.solve required=Analyst actual=Viewer subject=eve_smith_event_Forged correlation=c_1",
            AccessDenialAudit.ReasonFor(who, denial));
    }
}
