using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Web.Api;
using GridSim.Web.Model;
using GridSim.Web.Projection;
using GridSim.Web.Sessions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// DOC-2 (docs-session findings, stream 2), the host half: D-DOC-075 a non-converged REST solve is not a 200,
/// D-DOC-076 the session cap, D-DOC-077 an unknown bridge @source is refused, D-DOC-153 the Realtime:Case start-up
/// message, D-DOC-170 the /Error page, launchSettings.json and 400 for a malformed case, D-DOC-172 the OpenAPI document
/// and the /ws/sim source field, D-DOC-178 /ws/sim error frames, D-DOC-182 the nginx example's API caching, and
/// D-DOC-225 the replay topology cache (with its measurement).
/// </summary>
public sealed class WebHostDoc2Tests
{
    private readonly ITestOutputHelper _out;

    public WebHostDoc2Tests(ITestOutputHelper output) => _out = output;

    private static WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings)
    {
        string keys = Path.Combine(Path.GetTempPath(), "gridsim-doc2-keys-" + Guid.NewGuid().ToString("N"));
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:KeysDirectory", keys);
            foreach ((string k, string v) in settings) b.UseSetting(k, v);
        });
    }

    private static HttpClient Client(WebApplicationFactory<Program> f)
        => f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // ------------------------------------------------------------------------------------------------------------
    // D-DOC-075: POST /api/solve answers 200 only for a converged solve.
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rest_solve_of_a_converging_case_says_converged_in_the_body()
    {
        await using var f = Factory(("GridSim:ReplayOnly", "false"));
        using HttpClient c = Client(f);
        using HttpResponseMessage r = await c.PostAsJsonAsync(new Uri("/api/solve", UriKind.Relative), new { @case = "case9" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using JsonDocument d = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        Assert.True(d.RootElement.GetProperty("converged").GetBoolean());
        Assert.Equal("Converged", d.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Object, d.RootElement.GetProperty("topology").ValueKind);
        Assert.Equal(JsonValueKind.Object, d.RootElement.GetProperty("state").ValueKind);
    }

    /// <summary>Execute an endpoint result against a bare request and return the status and body text.</summary>
    private static async Task<(int Status, string Body)> Execute(IResult result)
    {
        var services = new ServiceCollection().AddLogging();
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new NonFiniteDoubleConverter()));
        await using ServiceProvider sp = services.BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = sp };
        using var body = new MemoryStream();
        ctx.Response.Body = body;
        await result.ExecuteAsync(ctx);
        return (ctx.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task Rest_solve_of_a_non_converged_result_answers_422_with_converged_false()
    {
        // No bundled case fails to converge through the endpoint, so the response is driven with a genuinely
        // non-converged case9 solve: one Newton iteration from flat start stops at the iteration cap.
        GridModel model = IeeeCases.Case9();
        PowerFlowResult stuck = new NewtonRaphsonPowerFlow().Solve(model, new PowerFlowOptions { MaxIterations = 1 });
        Assert.False(stuck.Converged);
        var frame = new SimFrame(model, stuck, Hour: 0, Tick: 0, FrequencyHz: 50, GenerationMw: 0, DemandMw: 0);
        TopologyDto topo = FrameProjector.BuildTopology("case9", "case9", model, replay: false, frameCount: 0);

        (int status, string text) = await Execute(RestApi.SolveResponse(stuck, topo, FrameProjector.Project(frame)));
        // Before D-DOC-075 this answer was 200 {topology, state}, indistinguishable from a converged solve.
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, status);
        using JsonDocument d = JsonDocument.Parse(text);
        Assert.False(d.RootElement.GetProperty("converged").GetBoolean());
        Assert.Equal(stuck.Status.ToString(), d.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, d.RootElement.GetProperty("iterations").GetInt32());
        Assert.Contains("did not converge", d.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Object, d.RootElement.GetProperty("state").ValueKind);   // still there, for inspection

        PowerFlowResult solved = new NewtonRaphsonPowerFlow().Solve(model, new PowerFlowOptions());
        Assert.True(solved.Converged);
        (int okStatus, _) = await Execute(RestApi.SolveResponse(solved, topo, FrameProjector.Project(frame with { Result = solved })));
        Assert.Equal(StatusCodes.Status200OK, okStatus);
    }

    // ------------------------------------------------------------------------------------------------------------
    // D-DOC-170: a malformed case is 400, the /Error page exists, and dotnet run starts in Development.
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_missing_or_blank_case_is_400_and_an_unknown_one_still_404()
    {
        await using var f = Factory(("GridSim:ReplayOnly", "false"));
        using HttpClient c = Client(f);
        foreach (object body in new object[] { new { }, new { @case = "" }, new { @case = "   " }, new { other = "case9" } })
        {
            using HttpResponseMessage r = await c.PostAsJsonAsync(new Uri("/api/solve", UriKind.Relative), body);
            // Before: 404 "Unknown case ''."
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Contains("must name a case", await r.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        foreach (string path in new[] { "/api/topology?case=", "/api/topology?case=%20", "/api/topology" })
        {
            using HttpResponseMessage r = await c.GetAsync(new Uri(path, UriKind.Relative));
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }
        using HttpResponseMessage unknown = await c.PostAsJsonAsync(new Uri("/api/solve", UriKind.Relative), new { @case = "no-such-case" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task The_error_page_named_by_the_exception_handler_is_served()
    {
        await using var f = Factory();
        using HttpClient c = Client(f);
        using HttpResponseMessage r = await c.GetAsync(new Uri("/Error", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        string html = await r.Content.ReadAsStringAsync();
        Assert.Contains("Something went wrong", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing at this address", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Dotnet_run_starts_the_web_app_in_development()
    {
        // Without launchSettings.json, `dotnet run --project src/GridSim.Web` started in Production: replay-only, and
        // static web assets (blazor.web.js) 404 outside a published build.
        string path = Path.Combine(Root(), "src", "GridSim.Web", "Properties", "launchSettings.json");
        using JsonDocument d = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement profile = d.RootElement.GetProperty("profiles").EnumerateObject().First().Value;
        Assert.Equal("Project", profile.GetProperty("commandName").GetString());
        Assert.Equal("Development", profile.GetProperty("environmentVariables").GetProperty("ASPNETCORE_ENVIRONMENT").GetString());
        Assert.StartsWith("http://localhost:", profile.GetProperty("applicationUrl").GetString(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------------
    // D-DOC-172: the OpenAPI document is valid, and the /ws/sim source field is checked, not ignored.
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_openapi_operation_documents_its_responses()
    {
        await using var f = Factory(("GridSim:ReplayOnly", "false"));
        using HttpClient c = Client(f);
        using JsonDocument doc = JsonDocument.Parse(await c.GetStringAsync(new Uri("/api/openapi.json", UriKind.Relative)));
        int operations = 0;
        foreach (JsonProperty path in doc.RootElement.GetProperty("paths").EnumerateObject())
            foreach (JsonProperty op in path.Value.EnumerateObject())
            {
                operations++;
                // Before: four of five paths carried "responses": null (a static field read before it was set).
                JsonElement responses = op.Value.GetProperty("responses");
                Assert.True(responses.ValueKind == JsonValueKind.Object, $"{path.Name} {op.Name}: responses is {responses.ValueKind}");
                Assert.NotEmpty(responses.EnumerateObject());
            }
        Assert.Equal(5, operations);
        JsonElement solve = doc.RootElement.GetProperty("paths").GetProperty("/api/solve").GetProperty("post").GetProperty("responses");
        Assert.True(solve.TryGetProperty("422", out _));
        Assert.True(solve.TryGetProperty("400", out _));
    }

    /// <summary>Send one text frame on an open socket.</summary>
    private static Task Send(WebSocket ws, string text) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    /// <summary>Read frames until one of type <paramref name="type"/> arrives (at most 20), and return it.</summary>
    private static async Task<JsonDocument> ReadUntil(WebSocket ws, string type, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        for (int k = 0; k < 20; k++)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult res;
            do
            {
                res = await ws.ReceiveAsync(buffer, ct);
                ms.Write(buffer, 0, res.Count);
            } while (!res.EndOfMessage);
            JsonDocument d = JsonDocument.Parse(ms.ToArray());
            if (d.RootElement.GetProperty("type").GetString() == type) return d;
            d.Dispose();
        }
        throw new InvalidOperationException($"no '{type}' frame within 20 messages");
    }

    [Fact]
    public async Task Sim_websocket_answers_every_frame_it_cannot_act_on_with_an_error_frame()
    {
        // D-DOC-178: each of these used to be dropped without a reply.
        await using var f = Factory(("GridSim:ReplayOnly", "false"), ("Cycle:Enabled", "false"));
        using WebSocket ws = await f.Server.CreateWebSocketClient().ConnectAsync(new Uri(f.Server.BaseAddress, "/ws/sim"), CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        async Task Expect(string send, string inMessage)
        {
            await Send(ws, send);
            using JsonDocument e = await ReadUntil(ws, "error", timeout.Token);
            Assert.Contains(inMessage, e.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
        }

        await Expect("this is not json", "malformed control frame");
        await Expect("null", "malformed control frame");
        await Expect("[1,2,3]", "malformed control frame");
        await Expect("{}", "control frame has no action");
        await Expect("{\"action\":\"dance\"}", "unknown action 'dance'");
        await Expect("{\"action\":\"play\"}", "send a 'start' frame first");
        await Expect("{\"action\":\"start\"}", "'start' needs a case");

        await ws.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
        using (JsonDocument bin = await ReadUntil(ws, "error", timeout.Token))
            Assert.Contains("must be text", bin.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);

        // D-DOC-172: a source that does not match the case's feed is refused instead of ignored; so is an unknown one.
        await Expect("{\"action\":\"start\",\"case\":\"case9\",\"source\":\"replay\"}", "'case9' is a live feed");
        await Expect("{\"action\":\"start\",\"case\":\"case9\",\"source\":\"satellite\"}", "unknown source 'satellite'");

        // A matching source starts normally; then a control that needs a value and has none is refused.
        await Send(ws, "{\"action\":\"start\",\"case\":\"case9\",\"source\":\"live\"}");
        using (await ReadUntil(ws, "topology", timeout.Token)) { }
        await Expect("{\"action\":\"seek\"}", "'seek' needs a numeric value");
        await Expect("{\"action\":\"speed\"}", "'speed' needs a numeric value");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
    }

    [Fact]
    public async Task N227_sim_websocket_refuses_an_unknown_property_by_name()
    {
        // LF1 N227 (October 2026): a misspelt field used to be dropped without a word (the start ran with the default).
        await using var f = Factory(("GridSim:ReplayOnly", "false"), ("Cycle:Enabled", "false"));
        using WebSocket ws = await f.Server.CreateWebSocketClient().ConnectAsync(new Uri(f.Server.BaseAddress, "/ws/sim"), CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await Send(ws, "{\"action\":\"start\",\"case\":\"case9\",\"assetGrph\":true}");
        using (JsonDocument e = await ReadUntil(ws, "error", timeout.Token))
        {
            string msg = e.RootElement.GetProperty("message").GetString()!;
            Assert.Contains("unknown property 'assetGrph'", msg, StringComparison.Ordinal);
            Assert.Contains("action, case, source, fps, value, solver, assetGraph, waveField", msg, StringComparison.Ordinal);
        }
        await Send(ws, "{\"action\":\"start\",\"case\":\"case9\",\"fsp\":5,\"Sorce\":\"live\"}");
        using (JsonDocument e = await ReadUntil(ws, "error", timeout.Token))
            Assert.Contains("unknown properties 'fsp', 'Sorce'", e.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);

        // Known names in any letter case still work, as before (the Web defaults read names case-insensitively).
        await Send(ws, "{\"Action\":\"start\",\"CASE\":\"case9\",\"source\":\"live\",\"fps\":5}");
        using (await ReadUntil(ws, "topology", timeout.Token)) { }

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
    }

    [Fact]
    public async Task A_failed_start_leaves_no_session_behind()
    {
        // After a start that disposes the old session and then fails, the next control is told there is no session,
        // instead of silently driving the disposed one.
        await using var f = Factory(("GridSim:ReplayOnly", "false"), ("Cycle:Enabled", "false"));
        using WebSocket ws = await f.Server.CreateWebSocketClient().ConnectAsync(new Uri(f.Server.BaseAddress, "/ws/sim"), CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await Send(ws, "{\"action\":\"start\",\"case\":\"case9\"}");
        using (await ReadUntil(ws, "topology", timeout.Token)) { }
        await Send(ws, "{\"action\":\"start\",\"case\":\"no-such-case\"}");
        using (JsonDocument e = await ReadUntil(ws, "error", timeout.Token))
            Assert.Contains("Unknown case", e.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
        await Send(ws, "{\"action\":\"play\"}");
        using (JsonDocument e = await ReadUntil(ws, "error", timeout.Token))
            Assert.Contains("send a 'start' frame first", e.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, f.Services.GetRequiredService<SessionManager>().ActiveSessions);
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
    }

    // ------------------------------------------------------------------------------------------------------------
    // D-DOC-076: the session cap; D-DOC-077: an unknown bridge source is refused.
    // ------------------------------------------------------------------------------------------------------------

    private static SessionManager Manager(params (string Key, string Value)[] settings)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        return new SessionManager(new CaseCatalog(AppContext.BaseDirectory), config);
    }

    [Fact]
    public async Task Sessions_beyond_the_cap_are_refused_and_a_closed_one_frees_its_slot()
    {
        SessionManager m = Manager(("GridSim:MaxSessions", "2"), ("Cycle:Enabled", "false"));
        Assert.Equal(2, m.MaxSessions);
        SimSession a = m.Create("case9");
        SimSession b = m.Create("case9");
        Assert.Equal(2, m.ActiveSessions);
        var ex = Assert.Throws<SessionLimitException>(() => m.Create("case9"));
        Assert.Contains("GridSim:MaxSessions", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, m.ActiveSessions);

        await a.DisposeAsync();
        await a.DisposeAsync();   // a second dispose gives nothing back twice
        Assert.Equal(1, m.ActiveSessions);
        SimSession c = m.Create("case9");
        Assert.Equal(2, m.ActiveSessions);

        // A create that fails holds no slot.
        await b.DisposeAsync();
        Assert.Throws<KeyNotFoundException>(() => m.Create("no-such-case"));
        Assert.Equal(1, m.ActiveSessions);
        await c.DisposeAsync();
        Assert.Equal(0, m.ActiveSessions);
    }

    [Fact]
    public void The_session_cap_defaults_to_64_and_a_bad_value_is_refused()
    {
        Assert.Equal(SessionManager.DefaultMaxSessions, Manager().MaxSessions);
        Assert.Equal(64, SessionManager.DefaultMaxSessions);
        foreach (string bad in new[] { "0", "-1", "lots", "2.5" })
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Manager(("GridSim:MaxSessions", bad)));
            Assert.Contains("GridSim:MaxSessions", ex.Message, StringComparison.Ordinal);
        }
        // And at host start-up, not on the first request.
        using var f = Factory(("GridSim:MaxSessions", "0"));
        var startup = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        Assert.Contains("GridSim:MaxSessions", startup.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_bridge_source_is_refused_not_replaced_by_presolved()
    {
        // A bridge is configured (an address that is never contacted: the parse refuses first), so the refusal below is
        // the @source check, not "no bridge endpoint". Before D-DOC-077 '@estimate' quietly opened the presolved lake.
        // Test changed 2026-10-19: the web host now refuses an unencrypted ws:// bridge endpoint at start-up unless
        // GridSim:BridgeAllowUnencrypted=true (NCSC CAF B3, SessionManager.ReadBridgeResilience), so the never-contacted
        // address is given as wss://. The @source refusal under test is unchanged.
        SessionManager m = Manager(("GridSim:BridgeEndpoint", "wss://bridge.invalid/ws/bridge"), ("Cycle:Enabled", "false"));
        foreach (string id in new[] { "bridge:gb-full@estimate", "bridge:gb-full@", "bridge:gb-full@ raw x" })
        {
            var ex = Assert.Throws<ArgumentException>(() => m.Create(id));
            Assert.Contains("Unknown bridge source", ex.Message, StringComparison.Ordinal);
            Assert.Contains("presolved, estimated, live, solves, raw", ex.Message, StringComparison.Ordinal);
        }
        Assert.Equal(0, m.ActiveSessions);
        Assert.Equal("bridge", m.FeedOf("bridge:gb-full@estimated"));
        Assert.Equal("bridge", m.FeedOf("live-bridge"));
        Assert.Equal("live", m.FeedOf("case9"));
        Assert.Null(m.FeedOf("no-such-case"));
    }

    // ------------------------------------------------------------------------------------------------------------
    // D-DOC-153: an unloadable Realtime:Case stops start-up with a message naming the setting.
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_unloadable_realtime_case_stops_start_up_naming_the_setting()
    {
        using var f = Factory(("GridSim:ReplayOnly", "false"), ("Realtime:Enabled", "true"), ("Realtime:Case", "no-such-case"));
        var ex = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        string text = ex.ToString();
        Assert.Contains("configuration setting Realtime:Case names 'no-such-case'", text, StringComparison.Ordinal);
        Assert.Contains("Realtime:Enabled=false", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------------
    // D-DOC-182: the nginx example does not cache the API.
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Nginx_example_keeps_the_api_out_of_the_static_asset_cache()
    {
        string conf = File.ReadAllText(Path.Combine(Root(), "docs", "developer", "deploy", "nginx-gridsim.example.conf"));
        // The static-asset rule matches any path ending in .json, so /api/openapi.json fell into it (verified: true).
        Match staticRule = Regex.Match(conf, @"location ~\* \\\.\(\?:[^)]*\bjson\b");
        Assert.True(staticRule.Success, "the static-asset rule is expected to cover .json files");
        // A '^~' prefix location for /api/ stops nginx from trying the regex locations at all.
        Match api = Regex.Match(conf, @"location \^~ /api/ \{(?<body>[^}]*)\}");
        Assert.True(api.Success, "the nginx example needs a 'location ^~ /api/' block");
        Assert.DoesNotContain("expires", api.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains("no-store", api.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains("proxy_pass http://127.0.0.1:5540;", api.Groups["body"].Value, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------------
    // D-DOC-225: the replay topology is built once per run, rebuilt only when the run changes.
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Write a run directory of <paramref name="frames"/> identical solved snapshots of <paramref name="model"/>
    /// under a fresh base folder's results/, and return the base folder (the catalog discovers results/ beneath it).</summary>
    private static string WriteRun(Func<GridModel> model, int frames, string runName)
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "gridsim-doc2-replay-" + Guid.NewGuid().ToString("N"));
        string exports = Path.Combine(baseDir, "results", runName, "exports");
        Directory.CreateDirectory(exports);
        var trace = new SolveTrace();
        GridModel m = model();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Trace = trace });
        Assert.True(r.Converged);
        for (int k = 1; k <= frames; k++)
        {
            var meta = new SolveExportMeta(runName, "cpu", "c", "2026-10-05T00:00:00Z", 1.0, "doc2-test");
            SolveExport.WriteJson(Path.Combine(exports, $"slice-{k:D9}.json"), m, r, trace, meta);
        }
        return baseDir;
    }

    [Fact]
    public void Replay_topology_is_built_once_and_rebuilt_when_the_run_changes()
    {
        string baseDir = WriteRun(IeeeCases.Case9, 3, "doc2-run");
        var catalog = new CaseCatalog(baseDir);
        CaseEntry e = Assert.Single(catalog.Entries, x => x.Id == "run-doc2-run");
        var cache = new ReplayTopologyCache();
        int loads = 0;
        PresolvedReplay Load(string id) { loads++; return catalog.LoadReplay(id); }

        TopologyDto first = cache.Get(e, Load);
        TopologyDto second = cache.Get(e, Load);
        Assert.Same(first, second);
        Assert.Equal(1, loads);
        Assert.Equal(1, cache.Builds);
        Assert.Equal(3, first.FrameCount);
        // The cached topology is exactly what the old per-call path built.
        PresolvedReplay direct = catalog.LoadReplay(e.Id);
        Assert.Equal(
            JsonSerializer.Serialize(FrameProjector.BuildTopology(e.Id, e.Label, direct.LayoutModel, replay: true, frameCount: direct.Count)),
            JsonSerializer.Serialize(first));

        // A snapshot added in place changes the fingerprint: the next call rebuilds and sees 4 frames.
        string exports = Path.Combine(baseDir, "results", "doc2-run", "exports");
        File.Copy(Path.Combine(exports, "slice-000000001.json"), Path.Combine(exports, "slice-000000004.json"));
        TopologyDto third = cache.Get(e, Load);
        Assert.Equal(2, loads);
        Assert.Equal(4, third.FrameCount);

        // A failed load caches nothing and passes the error through.
        var broken = new ReplayTopologyCache();
        Assert.Throws<InvalidDataException>(() => broken.Get(e, _ => throw new InvalidDataException("corrupt")));
        Assert.Equal(0, broken.Builds);
        Assert.Throws<ArgumentException>(() => cache.Get(new CaseEntry("case9", "x", CaseKind.BuiltIn), Load));
    }

    [Fact]
    public void Replay_topology_cache_measurement_on_case118()
    {
        // The measurement for D-DOC-225: the first call pays what EVERY call paid before (load the whole run), a second
        // call is served from the cache. Times go to the test output; the assertion is only that the second call did
        // not load the run again (wall-clock thresholds would make the test flaky on a busy machine).
        string baseDir = WriteRun(IeeeCases.Case118, 6, "doc2-case118");
        var catalog = new CaseCatalog(baseDir);
        CaseEntry e = Assert.Single(catalog.Entries, x => x.Id == "run-doc2-case118");
        var cache = new ReplayTopologyCache();
        var sw = Stopwatch.StartNew();
        TopologyDto cold = cache.Get(e, catalog.LoadReplay);
        double coldMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        TopologyDto warm = cache.Get(e, catalog.LoadReplay);
        double warmMs = sw.Elapsed.TotalMilliseconds;
        Assert.Same(cold, warm);
        Assert.Equal(1, cache.Builds);
        Assert.Equal(118, cold.BusCount);
        _out.WriteLine($"D-DOC-225 case118, 6 frames: first call (= every call before the fix) {coldMs:F1} ms; cached call {warmMs:F3} ms.");
    }
}
