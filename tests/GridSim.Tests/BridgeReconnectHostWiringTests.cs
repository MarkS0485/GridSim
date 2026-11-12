using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using GridSim.Core.IO;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.Net;
using GridSim.Core.Simulation;
using GridSim.Ui.Hosting;
using GridSim.Ui.Models;
using GridSim.Ui.Presentation;
using GridSim.Ui.Settings;
using GridSim.Web.Sessions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// NCSC CAF B5 / NIS principle 4: the two hosts that create bridge clients drive the reconnect supervisor. Each test runs
/// a real bridge client against a WebSocket server on the LOOPBACK interface only (localhost, a port the OS picks; nothing
/// here reaches another host), has the server drop the first connection, and checks that the host reconnects on its own:
/// a second connection is opened, the supervisor reports attempt 1 as a success, and the host shows it. The backoff wait
/// is injected (it records the delay asked for and returns at once), so no test sleeps through the schedule. Supervision
/// is off by default in both hosts, and these tests turn it on explicitly.
/// </summary>
public sealed class BridgeReconnectHostWiringTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);   // a hang fails the test, never stalls the run

    // ---- the web host (SimSession) ----

    [Fact]
    public async Task The_web_bridge_session_reconnects_by_itself_after_the_server_drops_the_stream()
    {
        using var server = new LoopbackBridge();
        var asked = new List<TimeSpan>();
        Task Wait(TimeSpan d, CancellationToken ct) { lock (asked) asked.Add(d); return Task.CompletedTask; }
        ResilienceOptions options = ResilienceOptions.Default with { AllowUnencryptedEndpoint = true, MaxReconnectAttempts = 3 };

        // source "live": the session waits for Play, so nothing but the connection itself is exercised.
        await using SimSession session = SimSession.CreateBridgeCore("t-b5-web", server.Endpoint, "gb-full", "live", options,
            _ => GateResult.Ok(), reconnect: true, reconnectDelay: Wait);
        BridgeReconnectSupervisor supervisor = session.ReconnectSupervisor
            ?? throw new InvalidOperationException("supervision was asked for but the session built no supervisor");
        var completed = new TaskCompletionSource<BridgeReconnectAttempt>(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.AttemptCompleted += a => completed.TrySetResult(a);

        WebSocket first = await server.AcceptAsync().WaitAsync(Deadline);
        // The server drops the stream (a clean remote close, as a restarting bridge does).
        await first.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "restarting", CancellationToken.None);

        WebSocket second = await server.AcceptAsync().WaitAsync(Deadline);   // the session opened a new connection
        BridgeReconnectAttempt attempt = await completed.Task.WaitAsync(Deadline);
        await supervisor.Outage.WaitAsync(Deadline);

        Assert.True(attempt.Succeeded, attempt.Failure?.Message);
        Assert.Equal(1, attempt.Attempt);
        lock (asked) Assert.Equal(new[] { options.BackoffDelay(1) }, asked);   // exactly the schedule's first wait
        Assert.Equal(BridgeReconnectState.Connected, supervisor.State);
        Assert.Equal(0, supervisor.Attempt);
        Assert.Equal(2, server.Accepted);
        Assert.Equal(WebSocketState.Open, second.State);
        // The fault's error is cleared once the stream is back, and the reconnect is said.
        Assert.Null(session.Error);
        Assert.Equal("Bridge reconnected on attempt 1.", session.Warning);
    }

    [Fact]
    public async Task A_web_bridge_session_without_supervision_builds_no_supervisor()
    {
        // The default (GridSim:BridgeReconnect unset): exactly the behaviour before supervision existed.
        await using SimSession session = SimSession.CreateBridge("t-b5-off", new Uri("wss://bridge.example.invalid/ws"),
            "gb-full", "live", null, _ => GateResult.Ok());
        Assert.Null(session.ReconnectSupervisor);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("true", true)]
    [InlineData(" False ", false)]
    public void The_web_reconnect_setting_reads_true_or_false_and_is_off_when_unset(string? raw, bool expected)
    {
        Assert.Equal(expected, SessionManager.ReadBridgeReconnect(raw));
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    public void A_web_reconnect_setting_that_is_not_true_or_false_stops_start_up(string raw)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SessionManager.ReadBridgeReconnect(raw));
        Assert.Contains(SessionManager.BridgeReconnectSetting, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("0", 0)]
    [InlineData("5", 5)]
    public void The_web_reconnect_bound_defaults_to_no_bound_and_reads_a_whole_number(string? raw, int expected)
    {
        Assert.Equal(expected, SessionManager.ReadBridgeReconnectMaxAttempts(raw));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("three")]
    [InlineData("2.5")]
    public void A_web_reconnect_bound_that_is_not_a_whole_number_of_0_or_more_stops_start_up(string raw)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SessionManager.ReadBridgeReconnectMaxAttempts(raw));
        Assert.Contains(SessionManager.BridgeReconnectMaxAttemptsSetting, ex.Message, StringComparison.Ordinal);
    }

    // ---- the desktop host (the Avalonia app's BridgeSession) ----

    [Fact]
    public async Task The_desktop_bridge_session_reconnects_by_itself_after_the_server_drops_the_stream()
    {
        using var server = new LoopbackBridge();
        var asked = new List<TimeSpan>();
        Task Wait(TimeSpan d, CancellationToken ct) { lock (asked) asked.Add(d); return Task.CompletedTask; }
        var settings = new AppSettings { BridgeAllowUnencrypted = true, BridgeAutoReconnect = true, BridgeReconnectMaxAttempts = 3 };
        var display = new FakeDisplay();
        var feed = new FakeFeed();
        var session = new BridgeSession(settings, display) { Feed = feed, ReconnectDelay = Wait };
        display.Session = session;
        var source = new ModelSource("Live bridge", "", Bridge: server.Endpoint.ToString());

        Task connecting = session.ConnectAsync(source);
        WebSocket first = await server.AcceptAsync().WaitAsync(Deadline);
        await connecting.WaitAsync(Deadline);
        Assert.StartsWith("Connected to bridge ", session.LastConnectLine, StringComparison.Ordinal);
        BridgeReconnectSupervisor supervisor = session.ReconnectSupervisor
            ?? throw new InvalidOperationException("BridgeAutoReconnect is on but the session built no supervisor");
        var completed = new TaskCompletionSource<BridgeReconnectAttempt>(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.AttemptCompleted += a => completed.TrySetResult(a);

        await first.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "restarting", CancellationToken.None);

        WebSocket second = await server.AcceptAsync().WaitAsync(Deadline);
        BridgeReconnectAttempt attempt = await completed.Task.WaitAsync(Deadline);
        await supervisor.Outage.WaitAsync(Deadline);

        Assert.True(attempt.Succeeded, attempt.Failure?.Message);
        Assert.Equal(1, attempt.Attempt);
        lock (asked) Assert.Equal(new[] { ResilienceOptions.Default.BackoffDelay(1) }, asked);
        Assert.Equal(BridgeReconnectState.Connected, supervisor.State);
        Assert.Equal(2, server.Accepted);
        Assert.Equal(WebSocketState.Open, second.State);
        Assert.True(session.IsActive);
        Assert.False(session.IsFaulted);
        // The fault was shown as before, then each step of the reconnect was said in the event log.
        IReadOnlyList<string> log = feed.Lines();
        Assert.Contains(log, l => l == "Bridge fault: The bridge closed the connection.");
        Assert.Contains(log, l => l.StartsWith("Bridge reconnect: attempt 1 of 3 in 1 s (", StringComparison.Ordinal));
        Assert.Contains(log, l => l == "Bridge reconnected on attempt 1");

        // Leaving the bridge stops the supervision: a later loss would not be reconnected.
        await session.LeaveAsync().WaitAsync(Deadline);
        Assert.Equal(BridgeReconnectState.Idle, supervisor.State);
    }

    [Fact]
    public async Task The_desktop_bridge_session_builds_no_supervisor_while_the_setting_is_off()
    {
        var settings = new AppSettings();   // BridgeAutoReconnect false by default
        Assert.False(settings.BridgeAutoReconnect);
        Assert.Equal(0, settings.BridgeReconnectMaxAttempts);
        var display = new FakeDisplay();
        var session = new BridgeSession(settings, display) { Feed = new FakeFeed() };
        display.Session = session;
        // An unencrypted endpoint the settings do not allow: the client refuses it before any socket is opened (no network
        // is touched), the connect fails as before, and nothing is supervised.
        await session.ConnectAsync(new ModelSource("Live bridge", "", Bridge: "ws://127.0.0.1:9/ws/bridge")).WaitAsync(Deadline);
        Assert.StartsWith("Bridge connect failed: ", session.LastConnectLine, StringComparison.Ordinal);
        Assert.Null(session.ReconnectSupervisor);
    }

    // ---- helpers ----

    /// <summary>A WebSocket server on localhost only (HttpListener, as BugHunt1029IoMoreTests uses), accepting
    /// connections one at a time when the test asks.</summary>
    private sealed class LoopbackBridge : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly List<WebSocket> _sockets = new();

        public LoopbackBridge()
        {
            int port;
            using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            Endpoint = new Uri($"ws://localhost:{port}/");
        }

        public Uri Endpoint { get; }

        public int Accepted { get { lock (_sockets) return _sockets.Count; } }

        public async Task<WebSocket> AcceptAsync()
        {
            HttpListenerContext ctx = await _listener.GetContextAsync();
            HttpListenerWebSocketContext ws = await ctx.AcceptWebSocketAsync(null);
            lock (_sockets) _sockets.Add(ws.WebSocket);
            return ws.WebSocket;
        }

        public void Dispose()
        {
            lock (_sockets) foreach (WebSocket s in _sockets) s.Dispose();
            _listener.Close();
        }
    }

    /// <summary>The window side of the bridge, for a test: Post runs the action at once under one lock (the "UI thread"),
    /// and the reconnect is choosing the source again, as the app's run view model does it.</summary>
    private sealed class FakeDisplay : IBridgeDisplay
    {
        private readonly object _ui = new();

        public BridgeSession? Session { get; set; }

        public void Post(Action action) { lock (_ui) action(); }

        public bool RenderFrame(SimFrame frame) => true;

        public void ShowTopology(GridModel model) { }

        public void SetOverlay(IReadOnlyDictionary<int, double>? conditionByBus, IReadOnlyDictionary<string, double>? stressByBranch) { }

        public void ClearCurrentFrame() { }

        public void PausedChanged(bool paused) { }

        public SimFrame? CurrentFrame => null;

        public int HostTick => 0;

        public Task ReconnectAsync(ModelSource source) =>
            (Session ?? throw new InvalidOperationException("the display has no session")).ConnectAsync(source);
    }

    /// <summary>The dashboard side of the bridge, for a test: records the event-log lines.</summary>
    private sealed class FakeFeed : IBridgeFeed
    {
        private readonly List<string> _lines = new();

        public IReadOnlyList<string> Lines() { lock (_lines) return _lines.ToArray(); }

        public string Clock => "00:00";

        public string Status { get; set; } = "";

        public void Log(string time, string line) { lock (_lines) _lines.Add(line); }

        public void AddAlarm(string text, UiColour colour) { }

        public void SetBridgeStatus(string text, UiColour colour) { }

        public void UpdateHud(double? inertiaMwS, double? conditionIndex, double? rocofHeadroomHzS) { }

        public void UpdateMarketData(BridgeProtocol.OperatorBlock? op, BridgeProtocol.MarketBlock? mk, DateTime? tickUtc) { }

        public void MarkStreamLost(string reason) { }

        public void ShowQueryResult(QueryResult qr, string title, bool hasOverlay, bool solveNotConverged) { }

        public void ClearQueryResult() { }

        public void ShowBridgeHint() { }

        public void ClearSelection() { }

        public void ModelChanged() { }
    }
}
