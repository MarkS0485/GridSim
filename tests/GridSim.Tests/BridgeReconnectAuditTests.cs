using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using GridSim.Core.Diagnostics;
using GridSim.Core.Licensing;
using GridSim.Core.Net;
using GridSim.Web.Sessions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// 2.1.074 RC-4-Fixes: each supervised bridge reconnect step (attempt scheduled, attempt succeeded or failed, give-up) is
/// written to the host's security-event recorder as an AvailabilityDegraded event. Exception type names only; the
/// endpoint never appears. The loopback server is on localhost with an OS-chosen port.
/// </summary>
public sealed class BridgeReconnectAuditTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private sealed class Server : IDisposable
    {
        private readonly HttpListener _listener;
        public Uri Endpoint { get; }
        public bool RefuseAfterFirst { get; set; }

        public Server()
        {
            for (int attempt = 1; ; attempt++)
            {
                int port;
                using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
                var candidate = new HttpListener();
                candidate.Prefixes.Add($"http://localhost:{port}/");
                try { candidate.Start(); }
                catch (HttpListenerException) when (attempt < 10) { candidate.Close(); continue; }
                _listener = candidate;
                Endpoint = new Uri($"ws://localhost:{port}/");
                break;
            }
        }

        public async Task<WebSocket> AcceptAsync()
        {
            HttpListenerContext ctx = await _listener.GetContextAsync();
            return (await ctx.AcceptWebSocketAsync(null)).WebSocket;
        }

        // Answer every later connection with a plain 500, so a reconnect attempt fails.
        public void RefuseTheRest() => _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    HttpListenerContext ctx = await _listener.GetContextAsync();
                    ctx.Response.StatusCode = 500;
                    ctx.Response.Close();
                }
            }
            catch (Exception) { /* the listener was closed */ }
        });

        public void Dispose() => _listener.Close();
    }

    private static Task Instantly(TimeSpan d, CancellationToken ct) => Task.CompletedTask;

    [Fact]
    public async Task A_reconnect_that_succeeds_is_audited_as_scheduled_then_reconnected()
    {
        using var server = new Server();
        var events = new List<(SecurityEventKind Kind, string Reason)>();
        void Audit(SecurityEventKind k, string r) { lock (events) events.Add((k, r)); }
        ResilienceOptions options = ResilienceOptions.Default with { AllowUnencryptedEndpoint = true, MaxReconnectAttempts = 3 };

        await using SimSession session = SimSession.CreateBridgeCore("t-audit-ok", server.Endpoint, "gb-full", "live", options,
            _ => GateResult.Ok(), reconnect: true, reconnectDelay: Instantly, securityAudit: Audit);
        BridgeReconnectSupervisor supervisor = session.ReconnectSupervisor!;
        var done = new TaskCompletionSource<BridgeReconnectAttempt>(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.AttemptCompleted += a => done.TrySetResult(a);

        WebSocket first = await server.AcceptAsync().WaitAsync(Deadline);
        await first.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "restarting", CancellationToken.None);
        _ = await server.AcceptAsync().WaitAsync(Deadline);
        Assert.True((await done.Task.WaitAsync(Deadline)).Succeeded);

        lock (events)
        {
            Assert.Equal(2, events.Count);
            Assert.All(events, e => Assert.Equal(SecurityEventKind.AvailabilityDegraded, e.Kind));
            Assert.Contains("reconnect attempt 1", events[0].Reason, StringComparison.Ordinal);
            Assert.Contains("scheduled", events[0].Reason, StringComparison.Ordinal);
            Assert.Contains("reconnected on attempt 1", events[1].Reason, StringComparison.Ordinal);
            Assert.All(events, e => Assert.DoesNotContain("localhost", e.Reason, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_reconnect_that_gives_up_audits_every_attempt_and_the_give_up()
    {
        using var server = new Server();
        var events = new List<(SecurityEventKind Kind, string Reason)>();
        void Audit(SecurityEventKind k, string r) { lock (events) events.Add((k, r)); }
        ResilienceOptions options = ResilienceOptions.Default with { AllowUnencryptedEndpoint = true, MaxReconnectAttempts = 2 };

        await using SimSession session = SimSession.CreateBridgeCore("t-audit-fail", server.Endpoint, "gb-full", "live", options,
            _ => GateResult.Ok(), reconnect: true, reconnectDelay: Instantly, securityAudit: Audit);
        BridgeReconnectSupervisor supervisor = session.ReconnectSupervisor!;
        var gaveUp = new TaskCompletionSource<BridgeReconnectGiveUp>(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.GaveUp += g => gaveUp.TrySetResult(g);

        WebSocket first = await server.AcceptAsync().WaitAsync(Deadline);
        server.RefuseTheRest();
        await first.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "restarting", CancellationToken.None);
        await gaveUp.Task.WaitAsync(Deadline);

        lock (events)
        {
            Assert.Contains(events, e => e.Reason.Contains("reconnect attempt 1 failed", StringComparison.Ordinal));
            Assert.Contains(events, e => e.Reason.Contains("reconnect attempt 2 failed", StringComparison.Ordinal));
            Assert.Contains("gave up reconnecting after 2 attempts", events[^1].Reason, StringComparison.Ordinal);
            Assert.All(events, e => Assert.Equal(SecurityEventKind.AvailabilityDegraded, e.Kind));
            Assert.All(events, e => Assert.DoesNotContain("localhost", e.Reason, StringComparison.Ordinal));
        }
    }
}
