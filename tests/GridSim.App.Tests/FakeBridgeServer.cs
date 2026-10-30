using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace GridSim.App.Tests;

/// <summary>
/// A fake GDA bridge for the app's bridge tests (UI programme slice 1.8): a WebSocket server on the LOOPBACK interface
/// only (127.0.0.1, a port the OS picks), speaking the bridge protocol of Core's BridgeProtocol. Nothing here, and no
/// test using it, ever reaches another host: the listener binds IPAddress.Loopback, and the endpoint it hands out is
/// ws://127.0.0.1:port/.
/// <para>
/// It is a plain TCP listener that answers the WebSocket opening handshake itself (RFC 6455 section 4.2: the
/// Sec-WebSocket-Accept is the base64 SHA-1 of the client's key and the fixed GUID) and then wraps the stream with
/// <see cref="WebSocket.CreateFromStream(Stream, bool, string?, TimeSpan)"/> as the server side. So it needs no web host,
/// no URL reservation and no administrator rights on any OS. Each accepted connection is a <see cref="FakeBridgeConnection"/>
/// that records every message the client sends (start, stop, query) and lets the test send topology, state, result and
/// error messages, or close the socket (a lost stream).
/// </para>
/// <para>Every wait is bounded by a deadline, so a hang fails the test instead of stalling the run.</para>
/// </summary>
internal sealed class FakeBridgeServer : IAsyncDisposable, IDisposable
{
    private const string HandshakeGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly BlockingCollection<FakeBridgeConnection> _accepted = new();
    private readonly List<FakeBridgeConnection> _all = new();
    private readonly Task _acceptLoop;
    private bool _disposed;

    public FakeBridgeServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>The loopback port the server listens on.</summary>
    public int Port { get; }

    /// <summary>The unencrypted loopback endpoint (the client accepts it only with BridgeAllowUnencrypted).</summary>
    public string Endpoint => $"ws://127.0.0.1:{Port}/ws/bridge";

    /// <summary>How many connections have been accepted so far.</summary>
    public int AcceptedCount { get { lock (_all) return _all.Count; } }

    /// <summary>The next connection the client opens; fails after <paramref name="timeoutMs"/>.</summary>
    public FakeBridgeConnection NextConnection(Action pump, int timeoutMs = 20_000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (_accepted.TryTake(out FakeBridgeConnection? c, 2)) return c;
            pump();
            if (clock.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException($"The fake bridge accepted no connection on 127.0.0.1:{Port} within {timeoutMs} ms.");
        }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient tcp = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                FakeBridgeConnection connection = await FakeBridgeConnection.AcceptAsync(tcp, _cts.Token).ConfigureAwait(false);
                lock (_all) _all.Add(connection);
                _accepted.Add(connection);
            }
        }
        catch (OperationCanceledException) { /* disposed */ }
        catch (ObjectDisposedException) { /* disposed */ }
        catch (SocketException) { /* disposed while accepting */ }
    }

    /// <summary>The WebSocket accept key for a client key (RFC 6455 section 4.2.2).</summary>
    internal static string AcceptKey(string clientKey) =>
#pragma warning disable CA5350 // RFC 6455 fixes SHA-1 for the handshake key; it is not used for security here.
        Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(clientKey.Trim() + HandshakeGuid)));
#pragma warning restore CA5350

    /// <summary>Stop listening and drop every connection (nothing here waits on the UI thread, so blocking is safe).</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try { await _acceptLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        List<FakeBridgeConnection> all;
        lock (_all) all = _all.ToList();
        foreach (FakeBridgeConnection c in all) c.Dispose();
        _cts.Dispose();
        _accepted.Dispose();
    }
}

/// <summary>One client connection to the <see cref="FakeBridgeServer"/>, after the handshake.</summary>
internal sealed class FakeBridgeConnection : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly WebSocket _ws;
    private readonly ConcurrentQueue<JsonObject> _received = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly List<JsonObject> _history = new();

    private FakeBridgeConnection(TcpClient tcp, WebSocket ws)
    {
        _tcp = tcp;
        _ws = ws;
        ReceiveLoop = Task.Run(ReceiveLoopAsync);
    }

    /// <summary>The loop reading the client's messages; it ends when the client closes the socket.</summary>
    public Task ReceiveLoop { get; }

    /// <summary>True once the client has closed its side (or the socket broke).</summary>
    public bool ClientClosed { get; private set; }

    /// <summary>Every message the client has sent so far, in order.</summary>
    public IReadOnlyList<JsonObject> History { get { lock (_history) return _history.ToList(); } }

    internal static async Task<FakeBridgeConnection> AcceptAsync(TcpClient tcp, CancellationToken ct)
    {
        NetworkStream stream = tcp.GetStream();
        // Read the HTTP upgrade request up to the blank line, byte by byte (it is a few hundred bytes), so nothing of
        // the first WebSocket frame is consumed with it.
        var request = new StringBuilder();
        var one = new byte[1];
        while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            int n = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (n == 0) throw new IOException("The client closed the connection during the handshake.");
            request.Append((char)one[0]);
            if (request.Length > 16 * 1024) throw new IOException("The handshake request is too long.");
        }
        string key = request.ToString().Split("\r\n")
            .Select(l => l.Split(':', 2))
            .Where(p => p.Length == 2 && p[0].Trim().Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[1].Trim())
            .Single();
        string response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                          $"Sec-WebSocket-Accept: {FakeBridgeServer.AcceptKey(key)}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct).ConfigureAwait(false);
        WebSocket ws = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.Zero);
        return new FakeBridgeConnection(tcp, ws);
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        ClientClosed = true;
                        if (_ws.State == WebSocketState.CloseReceived)
                            await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
                        return;
                    }
                    ms.Write(buffer, 0, r.Count);
                }
                while (!r.EndOfMessage);
                var msg = (JsonObject)JsonNode.Parse(Encoding.UTF8.GetString(ms.ToArray()))!;
                lock (_history) _history.Add(msg);
                _received.Enqueue(msg);
            }
        }
        catch (WebSocketException) { ClientClosed = true; }
        catch (IOException) { ClientClosed = true; }
        catch (ObjectDisposedException) { ClientClosed = true; }
    }

    /// <summary>
    /// The next message the client sends whose <c>action</c> is <paramref name="action"/>, skipping (and keeping in
    /// <see cref="History"/>) any other; fails after <paramref name="timeoutMs"/>.
    /// </summary>
    public JsonObject Next(string action, Action pump, int timeoutMs = 20_000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            while (_received.TryDequeue(out JsonObject? m))
                if ((string?)m["action"] == action) return m;
            pump();
            Thread.Sleep(2);
            if (clock.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException($"The client sent no '{action}' within {timeoutMs} ms (history: {string.Join(", ", History.Select(h => (string?)h["action"]))}).");
        }
    }

    /// <summary>Send one text message to the client.</summary>
    public async Task SendAsync(string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    /// <summary>Close the socket from the server side, cleanly (the client reads it as the bridge closing the connection).</summary>
    public async Task CloseAsync()
    {
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try { await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test", CancellationToken.None).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    /// <summary>Drop the connection at once (server disposal).</summary>
    public void Dispose()
    {
        try { _ws.Abort(); } catch (ObjectDisposedException) { }
        _ws.Dispose();
        _tcp.Dispose();
        _sendLock.Dispose();
    }
}
