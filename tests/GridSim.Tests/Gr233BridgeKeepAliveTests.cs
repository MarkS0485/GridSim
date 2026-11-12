using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GridSim.Core.IO;
using GridSim.Core.Net;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-233 (November 2026): the bridge client had no keep-alive timeout, so a silently dropped connection left the
/// receive loop waiting for ever and a session "Playing" with no data. The client now pings an idle connection and,
/// when no pong comes back within <see cref="GridBridgeClient.KeepAliveTimeout"/>, aborts it: the pending queries fail
/// and <see cref="GridBridgeClient.Faulted"/> is raised (what supervised reconnect listens for). The silent peer here is
/// a loopback socket that completes the WebSocket handshake and then never reads again, so it never answers a ping.
/// </summary>
public sealed class Gr233BridgeKeepAliveTests
{
    /// <summary>A loopback TCP server that accepts one WebSocket handshake (RFC 6455 section 4.2.2) and then goes silent:
    /// it neither reads nor writes, so pings are never answered, as on a silently dropped path.</summary>
    private sealed class SilentPeer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private TcpClient? _accepted;

        public SilentPeer()
        {
            _listener.Start();
            Endpoint = new Uri($"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
            Handshake = Task.Run(AcceptAndHandshakeAsync);
        }

        public Uri Endpoint { get; }

        public Task Handshake { get; }

        private async Task AcceptAndHandshakeAsync()
        {
            _accepted = await _listener.AcceptTcpClientAsync();
            NetworkStream stream = _accepted.GetStream();
            var request = new StringBuilder();
            var one = new byte[1];
            while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                int n = await stream.ReadAsync(one);
                if (n == 0) throw new IOException("the client closed before the handshake ended");
                request.Append((char)one[0]);
            }
            string key = "";
            foreach (string line in request.ToString().Split("\r\n"))
                if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                    key = line["Sec-WebSocket-Key:".Length..].Trim();
#pragma warning disable CA5350 // RFC 6455 fixes SHA-1 for the handshake accept value; it is not used for security here
            string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
#pragma warning restore CA5350
            byte[] response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
            await stream.WriteAsync(response);
            await stream.FlushAsync();
            // From here on: silence.
        }

        public void Dispose()
        {
            _accepted?.Dispose();
            _listener.Stop();
        }
    }

    [Fact]
    public async Task An_unanswered_ping_faults_the_stream_and_fails_pending_queries()
    {
        using var peer = new SilentPeer();
        var client = new GridBridgeClient
        {
            AllowUnencryptedEndpoint = true,
            KeepAliveInterval = TimeSpan.FromMilliseconds(200),
            KeepAliveTimeout = TimeSpan.FromMilliseconds(500),
            QueryTimeout = TimeSpan.FromMinutes(5),   // so only the keep-alive can end the query below
        };
        var faulted = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Faulted += ex => faulted.TrySetResult(ex);
        try
        {
            await client.ConnectAsync(peer.Endpoint);
            await peer.Handshake.WaitAsync(TimeSpan.FromSeconds(10));

            Task<QueryResult> query = client.Query("bus_voltages", DateTime.UtcNow.AddDays(-30), new { });

            Task done = await Task.WhenAny(faulted.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.True(ReferenceEquals(done, faulted.Task), "the silent peer was not detected within 30 s");
            await Assert.ThrowsAnyAsync<Exception>(() => query.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(query.IsCompletedSuccessfully);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_defaults_detect_a_dead_peer_within_a_minute_and_a_half()
    {
        await using var client = new GridBridgeClient();
        Assert.Equal(TimeSpan.FromSeconds(30), client.KeepAliveInterval);
        Assert.Equal(TimeSpan.FromSeconds(60), client.KeepAliveTimeout);
    }

    [Fact]
    public async Task A_negative_interval_or_a_non_positive_timeout_is_refused_before_connecting()
    {
        await using var a = new GridBridgeClient { AllowUnencryptedEndpoint = true, KeepAliveInterval = TimeSpan.FromSeconds(-1) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.ConnectAsync(new Uri("ws://127.0.0.1:1/")));
        await using var b = new GridBridgeClient { AllowUnencryptedEndpoint = true, KeepAliveTimeout = TimeSpan.Zero };
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.ConnectAsync(new Uri("ws://127.0.0.1:1/")));
    }
}
