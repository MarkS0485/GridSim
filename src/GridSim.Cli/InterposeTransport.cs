using System.Net;
using System.Net.Sockets;
using GridSim.Core.Interpose;
using GridSim.Core.Interpose.Protocols;

namespace GridSim.Cli.Interpose;

/// <summary>A source of delimited protocol frames. Blocking reads are bounded by a timeout so the
/// interposer's console loop stays responsive to a keypress / <c>--ticks</c> cap.</summary>
internal interface IRtuTransport : IDisposable
{
    /// <summary>Try to read one complete frame within <paramref name="timeout"/>. Returns false on timeout,
    /// no pending connection, or a disconnect - the caller simply loops.</summary>
    bool TryReadFrame(out byte[] frame, TimeSpan timeout);

    /// <summary>How many times a connected client went away (a clean close, a reset (RST) or any other read
    /// failure) and the transport dropped it to wait for the next one. Printed in the interposer's stop summary.</summary>
    long ClientResets { get; }

    /// <summary>N230: octets received and thrown away without ever being part of a frame: junk skipped while
    /// resynchronising to the next frame start, and a partial frame left in the buffer when its client went away.
    /// Printed in the interposer's stop summary (discarded-bytes) and its audit outcome.</summary>
    long DiscardedBytes { get; }
}

/// <summary>Classifies socket read failures for the TCP transports.</summary>
internal static class TransportErrors
{
    /// <summary>
    /// True only when a blocking <see cref="NetworkStream.Read(byte[], int, int)"/> gave up because its
    /// <see cref="NetworkStream.ReadTimeout"/> elapsed. .NET reports that as an <see cref="IOException"/> wrapping a
    /// <see cref="SocketException"/> with <see cref="SocketError.TimedOut"/>. A reset by the peer (RST) is ALSO an
    /// IOException, wrapping <see cref="SocketError.ConnectionReset"/>, so the inner code must be checked. Until
    /// October 2026 every IOException was taken as a timeout, so after an RST the transport kept reading the dead
    /// socket for ever and never accepted another client.
    /// </summary>
    public static bool IsReadTimeout(IOException ex) =>
        ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut or SocketError.WouldBlock };
}

/// <summary>
/// The shared TCP plumbing of the frame transports: bind a listener, accept ONE client at a time, read with a
/// timeout into a reassembly buffer, and drop the client (so the next one can connect) when it goes away. The
/// subclasses supply only the framing rule, <see cref="TryExtract"/>.
/// </summary>
internal abstract class TcpFrameTransportBase : IRtuTransport
{
    private readonly TcpListener _listener;
    private readonly byte[] _rx = new byte[8192];
    private TcpClient? _client;
    private NetworkStream? _stream;

    /// <summary>Bytes received from the current client and not yet returned as a frame.</summary>
    protected List<byte> Buffer { get; } = new();

    /// <inheritdoc/>
    public long ClientResets { get; private set; }

    /// <inheritdoc/>
    public long DiscardedBytes { get; protected set; }

    // N230: the monotonic millisecond clock the link-layer hooks are given (Environment.TickCount64 unless a test
    // supplies its own, so the IEC 104 timers can be driven deterministically).
    private readonly Func<long> _clockMs;

    /// <summary>The local endpoint actually bound (useful when port 0 asked the OS to pick one).</summary>
    public IPEndPoint LocalEndpoint => (IPEndPoint)_listener.LocalEndpoint;

    protected TcpFrameTransportBase(string host, int port, Func<long>? clockMs = null)
    {
        _clockMs = clockMs ?? (static () => Environment.TickCount64);
        IPAddress addr = host is "0.0.0.0" or "*" or "" ? IPAddress.Any
            : IPAddress.TryParse(host, out IPAddress? p) ? p
            : Dns.GetHostAddresses(host)[0];
        _listener = new TcpListener(addr, port);
        _listener.Start();
    }

    public bool TryReadFrame(out byte[] frame, TimeSpan timeout)
    {
        frame = Array.Empty<byte>();

        if (_client is null)
        {
            // Wait up to the timeout for an inbound connection without blocking the loop for ever.
            if (!_listener.Server.Poll((int)Math.Max(0, timeout.TotalMilliseconds * 1000), SelectMode.SelectRead))
                return false;
            _client = _listener.AcceptTcpClient();
            _stream = _client.GetStream();
            _stream.ReadTimeout = (int)Math.Max(1, timeout.TotalMilliseconds);
            // N230: a link layer that drives the session (IEC 104: STARTDT act) speaks first.
            if (!SendToClient(OnClientConnected(_clockMs()))) return false;
        }

        // N230: the link-layer timers (IEC 104: t2 acknowledgement, t3 TESTFR act), on every pass.
        if (!SendToClient(OnPoll(_clockMs()))) return false;
        // GR-803: the link layer decided the connection must close (IEC 104 t1 expired): drop the client.
        if (LinkWantsClose) { DropClient(); return false; }

        if (TryExtract(out frame)) return Delivered(frame); // a frame may already be buffered

        try
        {
            int n = _stream!.Read(_rx, 0, _rx.Length);
            if (n == 0) { DropClient(); return false; }   // the peer closed cleanly (FIN)
            Buffer.AddRange(_rx.AsSpan(0, n).ToArray());
        }
        catch (IOException ex) when (TransportErrors.IsReadTimeout(ex))
        {
            return false;   // the read timed out with the client still connected: loop again
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // Any other read failure (a reset, an aborted or closed connection) means this client is gone. Drop the
            // dead socket so the next call accepts the next client.
            DropClient();
            return false;
        }

        return TryExtract(out frame) && Delivered(frame);
    }

    // N230: show a received frame to the link layer (which may answer it, e.g. TESTFR con or an S acknowledgement)
    // before handing it up. The frame is handed up whatever happens to the answer: it was received.
    private bool Delivered(byte[] frame)
    {
        _ = SendToClient(OnFrameReceived(frame, _clockMs()));
        // GR-823: the frame is handed up (it was received); a link layer that now wants the connection closed (IEC 104:
        // an I-frame out of sequence) has the client dropped, so the next frame comes from a fresh connection.
        if (_client is not null && LinkWantsClose) DropClient();
        return true;
    }

    /// <summary>GR-803 / GR-823: true when the link layer wants the current connection closed. Default: never.</summary>
    protected virtual bool LinkWantsClose => false;

    // N230: write link-layer bytes to the current client. False when the write failed: the client is gone and has been
    // dropped (counted as a reset), exactly as a failed read drops it.
    private bool SendToClient(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0 || _stream is null) return true;
        try
        {
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            DropClient();
            return false;
        }
    }

    /// <summary>N230: bytes to send when a client connects (null for none). Default: none (a passive transport).</summary>
    protected virtual byte[]? OnClientConnected(long nowMs) => null;

    /// <summary>N230: bytes to send in answer to a received frame (null for none). Default: none.</summary>
    protected virtual byte[]? OnFrameReceived(byte[] frame, long nowMs) => null;

    /// <summary>N230: bytes the link-layer timers want sent now (null for none). Default: none.</summary>
    protected virtual byte[]? OnPoll(long nowMs) => null;

    /// <summary>Take one whole frame off the front of <see cref="Buffer"/>, resynchronising past junk as the
    /// protocol's framing allows. False when no whole frame is buffered yet.</summary>
    protected abstract bool TryExtract(out byte[] frame);

    // A client went away: count it, release its socket and forget any half-received frame (the next client's
    // stream starts afresh; a partial frame from the old one must never be glued to it).
    private void DropClient()
    {
        ClientResets++;
        DiscardedBytes += Buffer.Count;   // N230: a partial frame left behind is lost; count it
        CloseClient();
    }

    private void CloseClient()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
        Buffer.Clear();
    }

    public void Dispose()
    {
        CloseClient();
        _listener.Stop();
    }
}

/// <summary>
/// A TCP frame reassembler for start-byte + length-delimited protocols (IEC 60870-5-104: start 0x68, then a
/// one-octet APDU length, then that many octets). It binds a listener, accepts one client at a time, buffers
/// the stream, and yields one whole frame per call. On a bad lead-in it <b>resyncs</b> by discarding bytes up
/// to the next start delimiter. This is the CLI's first socket-binding verb; reads are timeout-bounded so the
/// loop never wedges.
/// </summary>
internal sealed class TcpDelimitedFrameTransport : TcpFrameTransportBase
{
    private readonly byte _startByte;

    // N230: the IEC 104 controlling-station link behaviour (STARTDT act, TESTFR con, S acknowledgements, TESTFR act on
    // t3), or null for a passive transport (the transport unit tests construct it without one).
    private readonly Iec104ControllingStation? _station;

    public TcpDelimitedFrameTransport(string host, int port, byte startByte,
        Iec104ControllingStation? station = null, Func<long>? clockMs = null) : base(host, port, clockMs)
    {
        _startByte = startByte;
        _station = station;
    }

    /// <summary>N230: the controlling-station state of the current (or last) connection, or null when passive.</summary>
    public Iec104ControllingStation? Station => _station;

    protected override byte[]? OnClientConnected(long nowMs) => _station?.Connected(nowMs);

    protected override byte[]? OnFrameReceived(byte[] frame, long nowMs) => _station?.FrameReceived(frame, nowMs);

    protected override byte[]? OnPoll(long nowMs) => _station?.Poll(nowMs);

    protected override bool LinkWantsClose => _station?.CloseRequested == true;   // GR-803 / GR-823

    protected override bool TryExtract(out byte[] frame)
    {
        frame = Array.Empty<byte>();

        int start = Buffer.IndexOf(_startByte);
        if (start < 0) { DiscardedBytes += Buffer.Count; Buffer.Clear(); return false; }   // no delimiter yet: drop noise
        if (start > 0) { DiscardedBytes += start; Buffer.RemoveRange(0, start); }       // resync: discard bytes before the delimiter
        if (Buffer.Count < 2) return false;

        int total = Buffer[1] + 2;                          // length octet + the 2 leading octets
        if (Buffer.Count < total) return false;             // frame not fully arrived

        frame = Buffer.GetRange(0, total).ToArray();
        Buffer.RemoveRange(0, total);
        return true;
    }
}

/// <summary>
/// A TCP frame reassembler for Modbus TCP, whose MBAP header carries no start delimiter: the frame length is a
/// 2-octet big-endian field at offset 4 counting the unit id + PDU, so a whole frame is 6 + that. Basic resync:
/// if the protocol-id field (offset 2-3) is non-zero the lead-in isn't a valid MBAP header, so drop one octet.
/// </summary>
internal sealed class TcpMbapFrameTransport : TcpFrameTransportBase
{
    public TcpMbapFrameTransport(string host, int port) : base(host, port) { }

    // Modbus/TCP MBAP length field = unit identifier (1 byte) + PDU. The PDU is at most 253 bytes (function code +
    // data; Modbus Application Protocol v1.1b3 section 4.1), so the length is 2..254 and a whole ADU is at most
    // 6 + 254 = 260 bytes. R150: the bound used to be 260 on the LENGTH, accepting frames up to 266 bytes as valid
    // instead of resynchronising.
    private const int MinMbapLength = 2;     // unit id + function code
    private const int MaxMbapLength = 254;   // unit id + the 253-byte maximum PDU

    protected override bool TryExtract(out byte[] frame)
    {
        frame = Array.Empty<byte>();
        while (Buffer.Count >= 6)
        {
            int proto = (Buffer[2] << 8) | Buffer[3];
            int length = (Buffer[4] << 8) | Buffer[5];
            if (proto != 0 || length is < MinMbapLength or > MaxMbapLength) { DiscardedBytes++; Buffer.RemoveAt(0); continue; } // resync one octet
            int total = 6 + length;
            if (Buffer.Count < total) return false;
            frame = Buffer.GetRange(0, total).ToArray();
            Buffer.RemoveRange(0, total);
            return true;
        }
        return false;
    }
}

/// <summary>Opens the transport a protocol needs. TCP paths exist today (IEC-104 delimited, Modbus MBAP);
/// serial and layer-2 transports register here as their protocols land.</summary>
internal static class TransportFactory
{
    public static IRtuTransport Open(IRtuProtocol protocol, string host, int port) => protocol.Name switch
    {
        // N230: interpose is the IEC 104 controlling station, so its transport drives the link (STARTDT act, ...).
        "iec104" => new TcpDelimitedFrameTransport(host, port, startByte: 0x68, station: new Iec104ControllingStation()),
        "modbus" => new TcpMbapFrameTransport(host, port),
        _ => throw new NotSupportedException(
            $"no transport is wired for protocol '{protocol.Name}' yet (transport kind {protocol.Transport})."),
    };
}
