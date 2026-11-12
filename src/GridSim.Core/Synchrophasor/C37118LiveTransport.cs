using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace GridSim.Core.Synchrophasor;

/// <summary>
/// N340 (CF5): the IEEE C37.118.2 COMMAND frame (frame type 4) a client sends a PMU or PDC: SYNC (0xAA, type 4 and the
/// version), FRAMESIZE 18, IDCODE, SOC, FRACSEC, the 16-bit CMD word and the CRC-CCITT. The CMD values are the standard's
/// (C37.118.2-2011 Table 15 as recorded here [VERIFY]): 1 turn transmission off, 2 turn it on, 3 send the HEADER frame,
/// 4 send CONFIG-1, 5 send CONFIG-2, 6 send CONFIG-3.
/// </summary>
public static class C37118Command
{
    /// <summary>CMD 0x0001: turn off transmission of DATA frames.</summary>
    public const ushort TurnOffTransmission = 0x0001;
    /// <summary>CMD 0x0002: turn on transmission of DATA frames.</summary>
    public const ushort TurnOnTransmission = 0x0002;
    /// <summary>CMD 0x0005: send the CONFIG-2 frame.</summary>
    public const ushort SendConfig2 = 0x0005;

    /// <summary>The size of a COMMAND frame with no extended data: 14-byte header, CMD, CHK.</summary>
    public const int FrameSize = 18;

    /// <summary>The version nibble written in SYNC byte 1 (1: C37.118-2005, the most widely accepted by devices).</summary>
    public const int Version = 1;

    /// <summary>Build a COMMAND frame for the device <paramref name="idCode"/> (0..65535), stamped
    /// <paramref name="utc"/> (whole seconds in SOC, FRACSEC 0), with the CRC written.</summary>
    public static byte[] Build(int idCode, ushort command, DateTime utc)
    {
        if (idCode is < 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(idCode), idCode, "a C37.118 IDCODE is a 16-bit unsigned number (0 to 65535).");
        long soc = (long)(DateTime.SpecifyKind(utc, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalSeconds;
        if (soc is < 0 or > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(utc), utc, "the command time must fall inside the 32-bit SOC range (1970 to 2106).");
        var f = new byte[FrameSize];
        f[0] = 0xAA;
        f[1] = (byte)(((int)C37118FrameType.Command << 4) | Version);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(2), FrameSize);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(4), (ushort)idCode);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(6), (uint)soc);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(10), 0);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(14), command);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(16), C37118Crc.Compute(f.AsSpan(0, 16)));
        return f;
    }

    /// <summary>Read the CMD word of a COMMAND frame, refusing (InvalidDataException) a frame that is not a well-formed,
    /// CRC-correct COMMAND frame. Used by the loopback tests' simulated PMU and available to a PDC front end.</summary>
    public static ushort Parse(ReadOnlySpan<byte> frame)
    {
        C37118FrameCheck.RequireWholeFrame(frame, "COMMAND frame");
        C37118CommonHeader h = C37118CommonHeader.Parse(frame);
        if (h.FrameType != C37118FrameType.Command)
            throw new InvalidDataException($"the frame is a {h.FrameType} frame, not a COMMAND frame.");
        if (frame.Length < FrameSize)
            throw new InvalidDataException($"a COMMAND frame is at least {FrameSize} bytes (got {frame.Length}).");
        if (!C37118Decoder.VerifyCrc(frame)) throw new InvalidDataException("the COMMAND frame fails its CRC.");
        return BinaryPrimitives.ReadUInt16BigEndian(frame[14..]);
    }
}

/// <summary>N340 (CF5): the frame-level checks shared by the live transports. Fail loud: a frame that does not frame
/// cleanly is refused naming what is wrong; nothing is guessed or resynchronised.</summary>
public static class C37118FrameCheck
{
    /// <summary>The smallest legal C37.118 frame: the 14-byte common header and the 2-byte CRC.</summary>
    public const int MinFrameSize = 16;

    /// <summary>Refuse (InvalidDataException) a buffer that is not exactly one frame: SYNC 0xAA, a FRAMESIZE of at least
    /// <see cref="MinFrameSize"/>, and a FRAMESIZE equal to the buffer's length. The CRC is not judged here (a frame with a
    /// bad CRC is a frame; the pipeline counts it).</summary>
    public static void RequireWholeFrame(ReadOnlySpan<byte> frame, string what)
    {
        if (frame.Length < MinFrameSize)
            throw new InvalidDataException($"{what}: {frame.Length} bytes is shorter than the smallest C37.118 frame ({MinFrameSize}).");
        if (frame[0] != 0xAA)
            throw new InvalidDataException($"{what}: byte 0 is 0x{frame[0]:X2}, not the C37.118 SYNC 0xAA.");
        int size = BinaryPrimitives.ReadUInt16BigEndian(frame[2..]);
        if (size != frame.Length)
            throw new InvalidDataException($"{what}: FRAMESIZE says {size} bytes but {frame.Length} arrived (one datagram must hold exactly one frame).");
    }
}

/// <summary>
/// N340 (CF5): reassembles C37.118 frames from a TCP byte stream. Bytes are appended as they arrive; whole frames are
/// taken off the front by their FRAMESIZE. FAIL LOUD, never resynchronise: a byte where a SYNC 0xAA must start a frame,
/// or a FRAMESIZE below 16, throws InvalidDataException naming the stream offset, because after a framing fault nothing
/// says where the next frame begins (guessing could decode noise as measurements). Each frame taken is checked by its CRC
/// and counted; a bad-CRC frame is still returned (the pmu-ingest pipeline counts it and exits 3, as for a recorded one).
/// </summary>
public sealed class C37118StreamFramer
{
    private readonly List<byte> _buffer = new();
    private long _consumed;   // stream offset of _buffer[0]

    /// <summary>Frames taken so far.</summary>
    public int Frames { get; private set; }

    /// <summary>Frames taken whose CRC did not match.</summary>
    public int CrcFailures { get; private set; }

    /// <summary>Bytes received and not yet part of a whole frame (a frame still arriving).</summary>
    public int Pending => _buffer.Count;

    /// <summary>Append received bytes.</summary>
    public void Append(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes) _buffer.Add(b);
    }

    /// <summary>Take the next whole frame off the front, or return false when none has fully arrived. Throws
    /// InvalidDataException on a framing fault (see the class note).</summary>
    public bool TryTakeFrame(out byte[] frame)
    {
        frame = Array.Empty<byte>();
        if (_buffer.Count == 0) return false;
        if (_buffer[0] != 0xAA)
            throw new InvalidDataException($"stream byte {_consumed} is 0x{_buffer[0]:X2} where a C37.118 frame must start (SYNC 0xAA); the stream cannot be framed past it.");
        if (_buffer.Count < 4) return false;
        int size = (_buffer[2] << 8) | _buffer[3];
        if (size < C37118FrameCheck.MinFrameSize)
            throw new InvalidDataException($"the frame at stream byte {_consumed} declares {size} bytes, below the smallest C37.118 frame ({C37118FrameCheck.MinFrameSize}).");
        if (_buffer.Count < size) return false;
        frame = _buffer.GetRange(0, size).ToArray();
        _buffer.RemoveRange(0, size);
        _consumed += size;
        Frames++;
        if (!C37118Decoder.VerifyCrc(frame)) CrcFailures++;
        return true;
    }
}

/// <summary>N340 (CF5): how a live receive stops. It ends when <see cref="DataFrames"/> DATA frames have arrived, or when
/// <see cref="Timeout"/> has passed (whichever is first); a receive that ends by timeout says so.</summary>
/// <param name="DataFrames">DATA frames to collect (1 or more).</param>
/// <param name="Timeout">The longest the receive may run (above zero).</param>
/// <param name="DataFrames">How many DATA frames to collect.</param>
/// <param name="Timeout">The longest the receive may take.</param>
/// <param name="UdpSource">GR-818: UDP only. When set, a datagram from any other source address is dropped and
/// counted (<see cref="C37118LiveCapture.ForeignDatagrams"/>), never added to the capture. The port is not compared,
/// because a PMU may send from any ephemeral port. Bug #173: on a socket bound to anything but loopback it is REQUIRED,
/// unless <paramref name="AllowAnyUdpSender"/> says otherwise on purpose.</param>
/// <param name="AllowAnyUdpSender">Bug #173 (explicit opt-out): read datagrams from any sender on a non-loopback bind
/// with no <paramref name="UdpSource"/>. CRC is not authentication, so any host that can reach the port can then inject
/// frames into the capture. False by default.</param>
/// <param name="ConnectTimeout">Bug #160: TCP only, the longest the connection to the device may take. Null means
/// <paramref name="Timeout"/>. The connect no longer shares its budget with the receive.</param>
/// <param name="FirstFrameTimeout">Bug #160: TCP only, the longest from the connection to the device's first frame (its
/// reply to "send CONFIG-2"). Null means <paramref name="Timeout"/>. <paramref name="Timeout"/> itself then runs from that
/// first frame, so a slow connect or a slow first answer no longer eats the time asked for collecting DATA frames.</param>
/// <param name="MaxNonDataFrames">Bug #177: the most frames that are not DATA frames (CONFIG, HEADER, COMMAND) one receive
/// keeps. A device that sends more is faulty or hostile, and the receive stops with InvalidDataException rather than let
/// the capture grow until the timeout (up to 86400 s). Each frame is at most 65535 bytes, so the default bounds that part
/// of the capture at 64 MiB.</param>
public sealed record C37118LiveOptions(int DataFrames, TimeSpan Timeout, IPAddress? UdpSource = null, bool AllowAnyUdpSender = false,
    TimeSpan? ConnectTimeout = null, TimeSpan? FirstFrameTimeout = null, int MaxNonDataFrames = C37118LiveOptions.DefaultMaxNonDataFrames)
{
    /// <summary>The default <see cref="MaxNonDataFrames"/>: far above the one CONFIG frame (plus one per configuration
    /// change) a real device sends.</summary>
    public const int DefaultMaxNonDataFrames = 1024;

    /// <summary>The connect budget in force (<see cref="ConnectTimeout"/>, or <see cref="Timeout"/>).</summary>
    public TimeSpan ConnectBudget => ConnectTimeout ?? Timeout;

    /// <summary>The first-frame budget in force (<see cref="FirstFrameTimeout"/>, or <see cref="Timeout"/>).</summary>
    public TimeSpan FirstFrameBudget => FirstFrameTimeout ?? Timeout;

    /// <summary>Refuse a stop rule that could never end or never start.</summary>
    public void Validate()
    {
        if (DataFrames < 1) throw new ArgumentOutOfRangeException(nameof(DataFrames), DataFrames, "a live receive needs at least one DATA frame to collect.");
        if (!(Timeout > TimeSpan.Zero)) throw new ArgumentOutOfRangeException(nameof(Timeout), Timeout, "a live receive needs a timeout above zero.");
        if (ConnectTimeout is { } c && !(c > TimeSpan.Zero)) throw new ArgumentOutOfRangeException(nameof(ConnectTimeout), c, "a connect timeout must be above zero.");
        if (FirstFrameTimeout is { } f && !(f > TimeSpan.Zero)) throw new ArgumentOutOfRangeException(nameof(FirstFrameTimeout), f, "a first-frame timeout must be above zero.");
        if (MaxNonDataFrames < 1) throw new ArgumentOutOfRangeException(nameof(MaxNonDataFrames), MaxNonDataFrames, "a live receive must keep at least the device's CONFIG frame.");
    }

    /// <summary>Bug #173: refuse to listen beyond this machine with nobody named as the sender. A UDP bind address that is
    /// not loopback, with no <see cref="UdpSource"/> and no <see cref="AllowAnyUdpSender"/>, throws ArgumentException
    /// naming both ways forward.</summary>
    public void RequireUdpSenderRule(IPAddress bindAddress)
    {
        ArgumentNullException.ThrowIfNull(bindAddress);
        if (IPAddress.IsLoopback(bindAddress) || UdpSource is not null || AllowAnyUdpSender) return;
        throw new ArgumentException(
            $"listening for C37.118 datagrams on {bindAddress} accepts them from any host that can reach the port, and a CRC is "
            + "not authentication. Name the PMU's address as the only sender (--live-udp-source <ip>), or accept any sender "
            + "on purpose (--live-udp-any-sender).", nameof(bindAddress));
    }
}

/// <summary>N340 (CF5): what a live receive collected: the frames back to back exactly as received (a capture the
/// recorded pmu-ingest path reads unchanged), and the transport's own counts.</summary>
/// <param name="Bytes">Every frame received, in order, back to back.</param>
/// <param name="Frames">Frames received.</param>
/// <param name="DataFrames">Of which DATA frames.</param>
/// <param name="CrcFailures">Frames whose CRC did not match (still in <paramref name="Bytes"/>; the pipeline counts them).</param>
/// <param name="MalformedDatagrams">UDP only: datagrams that were not exactly one frame (dropped, counted).</param>
/// <param name="TimedOut">True when the receive ended on its timeout before collecting the DATA frames asked for.</param>
/// <param name="ForeignDatagrams">GR-818: UDP only, with <see cref="C37118LiveOptions.UdpSource"/> set: datagrams from
/// another source address (dropped, counted). Always 0 otherwise.</param>
public sealed record C37118LiveCapture(byte[] Bytes, int Frames, int DataFrames, int CrcFailures, int MalformedDatagrams, bool TimedOut,
    int ForeignDatagrams = 0);

/// <summary>
/// N340 (CF5): a live IEEE C37.118.2 receiver, TCP or UDP, that turns a device's stream into the same byte capture
/// <c>pmu-ingest</c> reads from a file, so every downstream rule (CRC, CONFIG parsing, decoding, sample quality, the
/// concentrator, the channel map and the forward-inference horizon) is the recorded path's, unchanged. Opt-in only (the
/// CLI's <c>--live</c>); nothing else calls it.
/// <list type="bullet">
/// <item>TCP: GridSim is the client (the PMU / PDC listens). It sends "send CONFIG-2" and "turn on transmission" to the
/// device's IDCODE, frames the stream with <see cref="C37118StreamFramer"/> (fail loud on a framing fault), and on
/// stopping sends "turn off transmission".</item>
/// <item>UDP: GridSim listens on a local port for the device's spontaneous stream, one frame per datagram; a datagram that
/// is not exactly one frame (<see cref="C37118FrameCheck.RequireWholeFrame"/>) is dropped and counted.</item>
/// </list>
/// The horizon rule still applies downstream: a device streaming the present is refused (REJECTED), exactly as a fresh
/// recorded capture is; a PDC or historian replaying history over C37.118 is read.
/// </summary>
public static class C37118LiveReceiver
{
    /// <summary>The read slice: each blocking read waits at most this long, so the overall timeout is honoured.</summary>
    public static readonly TimeSpan ReadSlice = TimeSpan.FromMilliseconds(200);

    /// <summary>Receive over TCP from the device at <paramref name="host"/>:<paramref name="port"/>, addressing COMMAND
    /// frames to <paramref name="idCode"/>. Throws InvalidDataException on a framing fault and IOException / SocketException
    /// when the connection cannot be made or breaks.</summary>
    public static C37118LiveCapture ReceiveTcp(string host, int port, int idCode, C37118LiveOptions options, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), port, "a TCP port is 1 to 65535.");
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        // GR-817: every deadline is measured on the monotonic clock (a wall-clock step, such as an NTP correction, used to
        // shorten or stretch it).
        // Bug #160: three budgets, one after the other, instead of one clock started before the connect. The connect gets
        // ConnectBudget; the device's first frame gets FirstFrameBudget from the moment the connection is made; and the
        // Timeout for collecting DATA frames starts at that first frame. With one shared clock a slow connect (or a busy
        // host starting the connect thread late) left too little of a short timeout for the device's CONFIG reply, and a
        // device that answered was reported as having sent nothing.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var client = new TcpClient();
        // The connect is a blocking call on a thread of its own, not a task awaited with a timeout: a task's completion
        // is dispatched through the thread pool, so a busy pool (a host running many jobs) could hold a loopback connect
        // past the deadline and report a device that answered as absent.
        Exception? connectFault = null;
        var connector = new Thread(() =>
        {
            try { client.Connect(host, port); }
            catch (Exception ex) { connectFault = ex; }
        }) { IsBackground = true, Name = "C37118 connect" };
        connector.Start();
        while (!connector.Join(ReadSlice))
        {
            ct.ThrowIfCancellationRequested();
            if (clock.Elapsed >= options.ConnectBudget)
                throw new IOException(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"no connection to {host}:{port} within {options.ConnectBudget.TotalSeconds:0.###} s."));   // GR-817: invariant
        }
        // The connect failed (refused, unreachable, unknown host): say so plainly, keeping the cause.
        if (connectFault is not null)
            throw new IOException($"cannot connect to {host}:{port}: {connectFault.Message}", connectFault);
        using NetworkStream stream = client.GetStream();
        stream.Write(C37118Command.Build(idCode, C37118Command.SendConfig2, DateTime.UtcNow));
        stream.Write(C37118Command.Build(idCode, C37118Command.TurnOnTransmission, DateTime.UtcNow));

        var framer = new C37118StreamFramer();
        var capture = new List<byte>();
        int data = 0, nonData = 0;
        bool timedOut = false;
        var rx = new byte[8192];
        // Bug #160: the first-frame budget runs from here (the connection is made); the collection deadline is set when
        // the first frame arrives.
        clock.Restart();
        TimeSpan deadline = options.FirstFrameBudget;
        bool firstFrameSeen = false;
        try
        {
            while (data < options.DataFrames)
            {
                ct.ThrowIfCancellationRequested();
                TimeSpan left = deadline - clock.Elapsed;
                if (left <= TimeSpan.Zero) { timedOut = true; break; }
                stream.ReadTimeout = (int)Math.Max(1, Math.Min(left.TotalMilliseconds, ReadSlice.TotalMilliseconds));
                int n;
                try { n = stream.Read(rx, 0, rx.Length); }
                catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut or SocketError.WouldBlock })
                {
                    continue;   // this slice timed out; the deadline decides
                }
                if (n == 0)
                    throw new IOException($"the device at {host}:{port} closed the connection after {framer.Frames} frame(s) ({data} DATA).");
                framer.Append(rx.AsSpan(0, n));
                while (data < options.DataFrames && framer.TryTakeFrame(out byte[] frame))
                {
                    if (!firstFrameSeen)
                    {
                        // Bug #160: the device has answered; the time for collecting DATA frames starts now.
                        firstFrameSeen = true;
                        clock.Restart();
                        deadline = options.Timeout;
                    }
                    bool isData = C37118CommonHeader.Parse(frame).FrameType == C37118FrameType.Data;
                    // Bug #177: refuse an endless run of non-DATA frames before keeping one more.
                    if (!isData && ++nonData > options.MaxNonDataFrames)
                        throw TooManyNonDataFrames($"the device at {host}:{port}", options.MaxNonDataFrames, data);
                    capture.AddRange(frame);
                    if (isData) data++;
                }
            }
        }
        finally
        {
            // Best effort: ask the device to stop. A broken connection must not hide the error that broke it.
            try { stream.Write(C37118Command.Build(idCode, C37118Command.TurnOffTransmission, DateTime.UtcNow)); }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
        }
        return new C37118LiveCapture(capture.ToArray(), framer.Frames, data, framer.CrcFailures, 0, timedOut);
    }

    /// <summary>Receive over UDP on the local <paramref name="bindAddress"/>:<paramref name="port"/> (port 0 is refused:
    /// the device must know where to send). <paramref name="bound"/>, when given, is called with the bound endpoint
    /// before the first receive (tests use it to start their sender).</summary>
    public static C37118LiveCapture ReceiveUdp(IPAddress bindAddress, int port, C37118LiveOptions options,
        Action<IPEndPoint>? bound = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bindAddress);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), port, "a UDP port is 1 to 65535.");
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        // Bug #173: a bind beyond loopback must name its sender (or opt out on purpose); refused before the port is opened.
        options.RequireUdpSenderRule(bindAddress);
        using var udp = new UdpClient(new IPEndPoint(bindAddress, port));
        return ReceiveUdpOn(udp, options, bound, ct);
    }

    /// <summary>As <see cref="ReceiveUdp"/> on a socket the caller has bound (tests bind port 0 and read the port back).</summary>
    public static C37118LiveCapture ReceiveUdpOn(UdpClient udp, C37118LiveOptions options, Action<IPEndPoint>? bound = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(udp);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        // Bug #173: the same sender rule on a socket the caller bound.
        options.RequireUdpSenderRule(((IPEndPoint)udp.Client.LocalEndPoint!).Address);
        bound?.Invoke((IPEndPoint)udp.Client.LocalEndPoint!);
        // GR-817: the deadline is measured on the monotonic clock (a wall-clock step, such as an NTP correction, used to
        // shorten or stretch it).
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var capture = new List<byte>();
        int frames = 0, data = 0, crc = 0, malformed = 0, foreign = 0, nonData = 0;
        bool timedOut = false;
        // GR-818: the allowed sender, compared as IPv4 when it is an IPv4-mapped IPv6 address (a dual-stack socket reports
        // an IPv4 sender in that form).
        IPAddress? allowed = options.UdpSource is { IsIPv4MappedToIPv6: true } m ? m.MapToIPv4() : options.UdpSource;
        while (data < options.DataFrames)
        {
            ct.ThrowIfCancellationRequested();
            TimeSpan left = options.Timeout - clock.Elapsed;
            if (left <= TimeSpan.Zero) { timedOut = true; break; }
            udp.Client.ReceiveTimeout = (int)Math.Max(1, Math.Min(left.TotalMilliseconds, ReadSlice.TotalMilliseconds));
            byte[] datagram;
            IPEndPoint? from = null;
            try
            {
                datagram = udp.Receive(ref from);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock)
            {
                continue;
            }
            if (allowed is not null)
            {
                IPAddress sender = from!.Address.IsIPv4MappedToIPv6 ? from.Address.MapToIPv4() : from.Address;
                if (!sender.Equals(allowed)) { foreign++; continue; }   // GR-818: dropped and counted, never captured
            }
            try { C37118FrameCheck.RequireWholeFrame(datagram, "datagram"); }
            catch (InvalidDataException) { malformed++; continue; }   // dropped and counted: the run reports it (exit 3)
            bool isData = C37118CommonHeader.Parse(datagram).FrameType == C37118FrameType.Data;
            // Bug #177: refuse an endless run of non-DATA frames before keeping one more.
            if (!isData && ++nonData > options.MaxNonDataFrames)
                throw TooManyNonDataFrames($"the sender {from}", options.MaxNonDataFrames, data);
            frames++;
            if (!C37118Decoder.VerifyCrc(datagram)) crc++;
            capture.AddRange(datagram);
            if (isData) data++;
        }
        return new C37118LiveCapture(capture.ToArray(), frames, data, crc, malformed, timedOut, foreign);
    }

    // Bug #177: the refusal for a device that keeps sending frames that are not DATA frames.
    private static InvalidDataException TooManyNonDataFrames(string who, int max, int data) =>
        new(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{who} sent more than {max} frames that are not DATA frames (CONFIG, HEADER, COMMAND) after {data} DATA frame(s); a working device sends one CONFIG frame per configuration, so the receive stops rather than keep buffering them."));

    /// <summary>The bind address <c>udp://:port</c> (no address) means. Bug #173: loopback, not every interface.</summary>
    public const string DefaultUdpBind = "127.0.0.1";

    /// <summary>
    /// Parse a <c>--live</c> address: <c>tcp://host:port</c> (connect to the device) or <c>udp://[bind]:port</c> (listen;
    /// the bind address defaults to <see cref="DefaultUdpBind"/>, loopback: bug #173, it used to be 0.0.0.0, every
    /// interface). Refuses anything else naming the expected forms (ArgumentException).
    /// </summary>
    public static (bool Tcp, string Host, int Port) ParseAddress(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        bool tcp = address.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase);
        bool udp = address.StartsWith("udp://", StringComparison.OrdinalIgnoreCase);
        if (!tcp && !udp)
            throw new ArgumentException($"'{address}' is not a live C37.118 address; use tcp://host:port (connect to the PMU / PDC) or udp://[bind]:port (listen).", nameof(address));
        string rest = address[6..];
        int colon = rest.LastIndexOf(':');
        if (colon < 0 || !int.TryParse(rest[(colon + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
            throw new ArgumentException($"'{address}' needs a port from 1 to 65535 after the last ':'.", nameof(address));
        string host = rest[..colon];
        if (tcp && host.Length == 0) throw new ArgumentException($"'{address}' needs the device's host before the port.", nameof(address));
        return (tcp, host.Length == 0 ? (tcp ? host : DefaultUdpBind) : host, port);
    }
}
