using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Security;
using GridSim.Core.Simulation;
using GridSim.Web.Api;
using GridSim.Web.Model;
using GridSim.Web.Projection;
using GridSim.Web.Sessions;
using GridSim.Web.Site;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Web batch 4 (November 2026). New regression tests only; no existing test is touched.
/// <list type="bullet">
/// <item>Private-case start gate: a /ws/sim start naming a results/ or gda-out/ model is refused below Operator by the
/// existing control gate (one AccessDenied entry, no session), and the dashboard applies the same rule.</item>
/// <item>GR-738: the security.txt Expires is re-checked per request.</item>
/// <item>GR-870: the replay topology cache waits on an asynchronous lock and reuses a file fingerprint for a short time.</item>
/// </list>
/// GR-737 (usage log rotation) needs no new test here: BugHunt1101UsageLogTests already pins the rotation and the bounded
/// start-up read.
/// </summary>
public sealed class GA7E3Batch4WebTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-ga7e3b4-" + Guid.NewGuid().ToString("N"));

    public GA7E3Batch4WebTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------------

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

    /// <summary>A discovery root holding a real one-frame replay run under results/ (id run-b4-run, a private id).</summary>
    private string MakeRoot(string name)
    {
        string root = Path.Combine(_dir, name);
        WriteRunUnder(Path.Combine(root, "results", "b4-run", "exports"), 1);
        return root;
    }

    private static void WriteRunUnder(string exports, int frames)
    {
        Directory.CreateDirectory(exports);
        var trace = new SolveTrace();
        GridModel m = IeeeCases.Case9();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Trace = trace });
        Assert.True(r.Converged);
        for (int k = 1; k <= frames; k++)
        {
            var meta = new SolveExportMeta("b4-run", "cpu", "c", "2026-10-05T00:00:00Z", 1.0, "b4-test");
            SolveExport.WriteJson(Path.Combine(exports, $"slice-{k:D9}.json"), m, r, trace, meta);
        }
    }

    private string LogPath => Path.Combine(_dir, "audit.jsonl");

    private int AccessDeniedEntries() =>
        !File.Exists(LogPath) ? 0 : AuditLogFile.Load(LogPath).Count(e =>
            SecurityAudit.TryRead(e, out SecurityEventKind kind, out _) && kind == SecurityEventKind.AccessDenied);

    /// <summary>A host with the resident estimator on (so the audit log exists), anonymous callers holding
    /// <paramref name="role"/>, and discovery limited to <paramref name="root"/>.</summary>
    private WebApplicationFactory<Program> AuditedHost(OperationRole role, string root)
    {
        string windows = Path.Combine(_dir, "windows");
        Directory.CreateDirectory(windows);
        return Factory(("GridSim:AnonymousRole", role.ToString()), ("GridSim:CaseRoots", root),
            ("Realtime:Enabled", "true"), ("Realtime:Case", "case9"), ("Realtime:WindowsDir", windows),
            ("Audit:LogPath", LogPath));
    }

    private static Task Send(WebSocket ws, string text) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

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

    // ---------------------------------------------------------------------------------------------------------------
    // Private-case start gate
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(OperationRole.Viewer)]
    [InlineData(OperationRole.Analyst)]
    public async Task PrivateStart_ws_start_of_a_results_model_below_operator_is_refused_once_with_no_session(OperationRole role)
    {
        // The /ws/sim control gate already needs Operator for every control frame, start included, and runs before any
        // case is looked at, so no role below Operator can reach a private id. This pins that: one error frame, one
        // AccessDenied entry (through AccessControl.Require, the only denial path), no session built.
        string root = MakeRoot("ws-" + role);
        await using var f = AuditedHost(role, root);
        SessionManager sessions = f.Services.GetRequiredService<SessionManager>();
        Assert.True(f.Services.GetRequiredService<CaseCatalog>().IsPrivate("run-b4-run"));
        using WebSocket ws = await f.Server.CreateWebSocketClient().ConnectAsync(new Uri(f.Server.BaseAddress, "/ws/sim"), CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Assert.Equal(0, AccessDeniedEntries());

        await Send(ws, "{\"action\":\"start\",\"case\":\"run-b4-run\"}");
        using JsonDocument e = await ReadNext(ws, timeout.Token);
        Assert.Equal("error", e.RootElement.GetProperty("type").GetString());
        Assert.Equal($"Access denied: 'ws.sim.start' requires role Operator or higher, but the caller has {role}.",
            e.RootElement.GetProperty("message").GetString());
        Assert.Equal(0, sessions.ActiveSessions);
        Assert.Equal(1, AccessDeniedEntries());
        Assert.Equal(WebSocketState.Open, ws.State);
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
    }

    [Fact]
    public async Task PrivateStart_ws_start_of_a_results_model_as_operator_streams_a_topology()
    {
        // The positive control: the same frame from an Operator host is accepted and writes no denial.
        string root = MakeRoot("ws-operator");
        await using var f = AuditedHost(OperationRole.Operator, root);
        using WebSocket ws = await f.Server.CreateWebSocketClient().ConnectAsync(new Uri(f.Server.BaseAddress, "/ws/sim"), CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await Send(ws, "{\"action\":\"start\",\"case\":\"run-b4-run\"}");
        string? type = null;
        for (int k = 0; k < 20 && type != "topology"; k++)
        {
            using JsonDocument d = await ReadNext(ws, timeout.Token);
            type = d.RootElement.GetProperty("type").GetString();
            Assert.NotEqual("error", type);
        }
        Assert.Equal("topology", type);
        Assert.Equal(0, AccessDeniedEntries());
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
    }

    [Theory]
    [InlineData(OperationRole.Viewer)]
    [InlineData(OperationRole.Operator)]
    public async Task PrivateStart_dashboard_prerender_of_a_results_model_builds_no_session_and_writes_no_entry(OperationRole role)
    {
        // A plain GET is the prerender pass only (no circuit is started by the test client). For a private id it must
        // decide nothing: no session, no audit entry, no error panel, the neutral loading state. The decision (open for
        // Operator, error panel plus exactly one AccessDenied entry below it) is made on the interactive pass, which
        // needs a browser circuit and is not reachable from this host; see the pure-helper and source tests below.
        string root = MakeRoot("dash-prerender-" + role);
        await using var f = AuditedHost(role, root);
        using HttpClient c = Client(f);
        using HttpResponseMessage r = await c.GetAsync(new Uri("/dashboard/run-b4-run", UriKind.Relative));
        string body = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain("requires role Operator or higher", body, StringComparison.Ordinal);
        Assert.DoesNotContain("load-error", body, StringComparison.Ordinal);
        Assert.Contains("Initialising session", body, StringComparison.Ordinal);
        Assert.Equal(0, f.Services.GetRequiredService<SessionManager>().ActiveSessions);
        Assert.Equal(0, AccessDeniedEntries());
    }

    [Fact]
    public async Task PrivateStart_dashboard_prerender_of_a_bundled_case_is_unchanged()
    {
        // A bundled id is never deferred: its prerender still builds the session and shows the dashboard.
        string root = MakeRoot("dash-bundled");
        await using var f = AuditedHost(OperationRole.Viewer, root);
        using HttpClient c = Client(f);
        using HttpResponseMessage r = await c.GetAsync(new Uri("/dashboard/case9", UriKind.Relative));
        Assert.Contains("solver-pick", await r.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, AccessDeniedEntries());
    }

    [Theory]
    [InlineData(true, false, true)]    // private, prerender: wait
    [InlineData(true, true, false)]    // private, interactive: decide now
    [InlineData(false, false, false)]  // bundled, prerender: unchanged
    [InlineData(false, true, false)]   // bundled, interactive: unchanged
    public void PrivateStart_the_prerender_deferral_rule(bool isPrivate, bool isInteractive, bool expectedWait)
    {
        Assert.Equal(expectedWait, AccessControl.MustWaitForInteractive(isPrivate, isInteractive));
    }

    [Fact]
    public void PrivateStart_dashboard_source_decides_once_on_the_interactive_pass()
    {
        // The interactive pass cannot be driven here, so pin its shape: the deferral comes first, and the one Require call
        // sits in StartSession, before Sessions.Create, so a refusal writes exactly one entry and builds no session.
        string razor = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Web", "Components", "Pages", "Dashboard.razor");
        Assert.Contains("MustWaitForInteractive(Catalog.IsPrivate(CaseId), RendererInfo.IsInteractive)", razor, StringComparison.Ordinal);
        Assert.Equal(1, razor.Split("Access.Require(").Length - 1);
        Assert.True(razor.IndexOf("Access.Require(", StringComparison.Ordinal) < razor.IndexOf("Sessions.Create(CaseId", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // GR-738: security.txt Expires per request
    // ---------------------------------------------------------------------------------------------------------------

    private static SiteSettings SiteWith(DateTimeOffset startUtc, params (string Key, string Value)[] settings) =>
        SiteSettings.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build(), startUtc);

    [Fact]
    public void Gr738_security_txt_is_served_until_its_expiry_and_not_after()
    {
        var start = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        SiteSettings s = SiteWith(start,
            ("Site:SecurityTxt:Contact", "mailto:security@example.com"),
            ("Site:SecurityTxt:Expires", "2027-01-31T23:59:59Z"));
        Assert.NotNull(s.SecurityTxt);
        Assert.Equal(new DateTimeOffset(2027, 1, 31, 23, 59, 59, TimeSpan.Zero), s.SecurityTxtExpiresUtc);
        Assert.Same(s.SecurityTxt, s.SecurityTxtAt(start));
        Assert.Same(s.SecurityTxt, s.SecurityTxtAt(s.SecurityTxtExpiresUtc!.Value.AddTicks(-1)));
        // At the expiry moment and after it the answer is null (404): the same rule start-up applies (expiry <= now is stale).
        Assert.Null(s.SecurityTxtAt(s.SecurityTxtExpiresUtc.Value));
        Assert.Null(s.SecurityTxtAt(s.SecurityTxtExpiresUtc.Value.AddDays(1)));
        // The start-up refusal of an already-past expiry is unchanged.
        Assert.Throws<InvalidOperationException>(() => SiteWith(new DateTimeOffset(2027, 2, 1, 0, 0, 0, TimeSpan.Zero),
            ("Site:SecurityTxt:Contact", "mailto:security@example.com"),
            ("Site:SecurityTxt:Expires", "2027-01-31T23:59:59Z")));
    }

    [Fact]
    public void Gr738_no_contact_means_no_security_txt_at_any_time()
    {
        SiteSettings s = SiteWith(DateTimeOffset.UtcNow);
        Assert.Null(s.SecurityTxt);
        Assert.Null(s.SecurityTxtExpiresUtc);
        Assert.Null(s.SecurityTxtAt(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Gr738_the_host_answers_through_the_per_request_check()
    {
        // The middleware must ask SecurityTxtAt with the current clock, not read the start-up text directly.
        string program = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Web", "Program.cs");
        Assert.Contains("site.SecurityTxtAt(DateTimeOffset.UtcNow)", program, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteAsync(ctx.Response, site.SecurityTxt)", program, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // GR-870: replay topology cache
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>A clock that moves only when told to, in milliseconds.</summary>
    private sealed class StepClock : TimeProvider
    {
        private long _ms;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref _ms);
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ms, (long)by.TotalMilliseconds);
    }

    private (CaseEntry Entry, CaseCatalog Catalog, string Exports) ReplayEntry(string name, int frames)
    {
        string baseDir = Path.Combine(_dir, name);
        string exports = Path.Combine(baseDir, "results", "b4-run", "exports");
        WriteRunUnder(exports, frames);
        var catalog = new CaseCatalog(baseDir);
        CaseEntry e = Assert.Single(catalog.Entries, x => x.Id == "run-b4-run");
        return (e, catalog, exports);
    }

    [Fact]
    public async Task Gr870_a_fingerprint_is_reused_inside_the_lifetime_and_refreshed_after_it()
    {
        (CaseEntry e, CaseCatalog catalog, string exports) = ReplayEntry("cache-ttl", 2);
        var clock = new StepClock();
        var cache = new ReplayTopologyCache(TimeSpan.FromSeconds(5), clock);
        Assert.Equal(TimeSpan.FromSeconds(5), cache.StatLifetime);

        TopologyDto first = await cache.GetAsync(e, catalog.LoadReplay);
        Assert.Equal(2, first.FrameCount);
        File.Copy(Path.Combine(exports, "slice-000000001.json"), Path.Combine(exports, "slice-000000003.json"));

        // Inside the lifetime the kept fingerprint is used, so the added snapshot is not seen yet and nothing is rebuilt.
        clock.Advance(TimeSpan.FromSeconds(4));
        TopologyDto warm = await cache.GetAsync(e, catalog.LoadReplay);
        Assert.Same(first, warm);
        Assert.Equal(1, cache.Builds);

        // Past the lifetime the files are stated again, the change is seen, and the entry is rebuilt.
        clock.Advance(TimeSpan.FromSeconds(2));
        TopologyDto rebuilt = await cache.GetAsync(e, catalog.LoadReplay);
        Assert.Equal(3, rebuilt.FrameCount);
        Assert.Equal(2, cache.Builds);
    }

    [Fact]
    public async Task Gr870_the_default_cache_still_stats_the_files_on_every_call()
    {
        (CaseEntry e, CaseCatalog catalog, string exports) = ReplayEntry("cache-zero", 1);
        var cache = new ReplayTopologyCache();
        Assert.Equal(TimeSpan.Zero, cache.StatLifetime);
        Assert.Equal(1, (await cache.GetAsync(e, catalog.LoadReplay)).FrameCount);
        File.Copy(Path.Combine(exports, "slice-000000001.json"), Path.Combine(exports, "slice-000000002.json"));
        Assert.Equal(2, (await cache.GetAsync(e, catalog.LoadReplay)).FrameCount);
        Assert.Equal(2, cache.Builds);
    }

    [Fact]
    public void Gr870_a_negative_lifetime_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayTopologyCache(TimeSpan.FromSeconds(-1), TimeProvider.System));
    }

    [Fact]
    public async Task Gr870_a_waiting_caller_holds_no_thread_and_can_be_cancelled()
    {
        (CaseEntry e, CaseCatalog catalog, _) = ReplayEntry("cache-wait", 1);
        var cache = new ReplayTopologyCache();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        PresolvedReplay SlowLoad(string id)
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(60));
            return catalog.LoadReplay(id);
        }

        // The first caller takes the run's lock and sits in its (synchronous) load on a pool thread.
        Task<TopologyDto> builder = Task.Run(() => cache.GetAsync(e, SlowLoad));
        Assert.True(started.Wait(TimeSpan.FromSeconds(60)));

        // A second caller comes back at once with an unfinished task: it is waiting asynchronously, not blocking this thread.
        bool secondLoaded = false;
        using var cts = new CancellationTokenSource();
        Task<TopologyDto> waiter = cache.GetAsync(e, id => { secondLoaded = true; return catalog.LoadReplay(id); }, cts.Token);
        Assert.False(waiter.IsCompleted);

        // Cancelling ends the wait with a cancellation; the second load never ran.
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.False(secondLoaded);

        release.Set();
        Assert.Equal(1, (await builder).FrameCount);
        Assert.Equal(1, cache.Builds);
    }

    [Fact]
    public async Task Gr870_the_solve_slot_is_held_until_asynchronous_work_finishes()
    {
        await using var f = Factory(("GridSim:MaxConcurrentSolves", "1"));
        using HttpClient c = Client(f);   // builds the host so WebLimits exists
        Assert.NotNull(c);
        WebLimits limits = f.Services.GetRequiredService<WebLimits>();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<SolveRun<int>> held = limits.RunSolveTaskAsync(async () => await gate.Task);
        Assert.Equal(0, limits.FreeSolveSlots);
        SolveRun<int> second = await limits.RunSolveTaskAsync(() => Task.FromResult(2));
        Assert.Equal(SolveRunStatus.Busy, second.Status);

        gate.SetResult(7);
        SolveRun<int> done = await held;
        Assert.Equal(SolveRunStatus.Completed, done.Status);
        Assert.Equal(7, done.Value);
        Assert.Equal(1, limits.FreeSolveSlots);
    }

    [Fact]
    public async Task Gr870_api_topology_of_a_replay_is_served_cold_and_warm_through_the_async_path()
    {
        string root = MakeRoot("api-replay");
        await using var f = Factory(("GridSim:AnonymousRole", "Operator"), ("GridSim:CaseRoots", root));
        using HttpClient c = Client(f);
        var cache = f.Services.GetRequiredService<ReplayTopologyCache>();
        Assert.Equal(ReplayTopologyCache.DefaultStatLifetime, cache.StatLifetime);
        for (int k = 0; k < 2; k++)
        {
            using JsonDocument doc = JsonDocument.Parse(await c.GetStringAsync(new Uri("/api/topology?case=run-b4-run", UriKind.Relative)));
            Assert.Equal(1, doc.RootElement.GetProperty("frameCount").GetInt32());
        }
        Assert.Equal(1, cache.Builds);   // the second call was served from the cache
        Assert.Equal(WebLimits.DefaultMaxConcurrentSolves, f.Services.GetRequiredService<WebLimits>().FreeSolveSlots);   // every slot given back
    }
}
