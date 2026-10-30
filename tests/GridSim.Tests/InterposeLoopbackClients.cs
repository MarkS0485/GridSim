using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace GridSim.Tests;

// ---------------------------------------------------------------------------------------------------------------------
// N230 (October 2026, Mark's ruling: loopback test). TEST-ONLY RTU clients for the process-level `interpose` tests
// (InterposeLoopbackProcessTests.cs). They play the field device: they connect to the interposer listening on the
// loopback interface and push protocol frames at it, exactly as the CF5 (N340) tests play a PMU for pmu-ingest --live.
//
// Nothing here leaves the machine: every socket is 127.0.0.1, and every wait is bounded by a deadline, so a hang fails
// the test instead of blocking the run.
//
// The frame builders are written out byte by byte from the protocol layouts, deliberately WITHOUT the product codecs
// (Iec104Frame.ToBytes, ModbusFrame), so the tests check the product against an independent encoding rather than
// against itself. Every layout is commented with its octets.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>One connection the loopback client makes: the frames it sends, and how it ends.</summary>
/// <param name="Frames">The frames to send, in order.</param>
/// <param name="CloseAfterSending">True: close the connection as soon as the frames are sent (the interposer sees the
/// client go away and counts a client reset). False: stay connected and read until the interposer closes the socket
/// (it does so when its process stops), collecting anything the interposer sent back.</param>
/// <param name="Fragment">True: send all the frames as one byte run cut at awkward places (inside headers and bodies),
/// so the interposer's TCP reassembly is exercised. False: each frame is written on its own.</param>
/// <param name="Iec104Outstation">True: play a REAL IEC 104 outstation (main's ruling on N230 question 3). After the
/// connect it sends nothing until the controlling station (the interposer) sends STARTDT act; anything else first is a
/// failure. It then sends STARTDT con, then <paramref name="Frames"/>; while it waits for the interposer to close it
/// answers TESTFR act with TESTFR con. False: a raw byte pusher (Modbus, and the "connects and leaves" case).</param>
internal sealed record LoopbackSession(
    IReadOnlyList<byte[]> Frames, bool CloseAfterSending = false, bool Fragment = false, bool Iec104Outstation = false);

/// <summary>What one connection observed.</summary>
/// <param name="Connected">Whether the client connected at all (false when the run was cancelled first, for example
/// because the interposer process ended before it ever listened).</param>
/// <param name="Received">Every byte the interposer sent on this connection, the STARTDT act of the handshake
/// included. For a session that closed after sending, only what arrived before it closed.</param>
internal sealed record LoopbackSessionResult(bool Connected, byte[] Received);

/// <summary>
/// A loopback RTU client: connects to 127.0.0.1:port (retrying while the interposer process starts up), runs each
/// <see cref="LoopbackSession"/> in turn on its own connection, and reports what each one observed.
/// </summary>
internal static class LoopbackRtuClient
{
    /// <summary>The hard ceiling on one whole client run (start-up of the CLI process included).</summary>
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A free TCP port on the loopback interface, chosen by the OS (bind port 0, read the port, release it). The
    /// interposer is then started on it with --listen 127.0.0.1:port. There is a short window in which another process
    /// could take the port; the OS does not hand a just-released ephemeral port straight back out, so in practice this
    /// does not happen (the same approach as the CF5 tests' "nothing listens here" port).
    /// </summary>
    internal static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    /// <summary>
    /// Run the sessions one after another on a background task. <paramref name="cancel"/> stops only the CONNECT wait
    /// (so a test whose interposer never listened does not wait out the whole deadline): a session that has connected
    /// runs to its end, which the interposer's exit always brings about.
    /// </summary>
    internal static Task<IReadOnlyList<LoopbackSessionResult>> RunAsync(
        int port, IReadOnlyList<LoopbackSession> sessions, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        if (sessions.Count == 0) throw new ArgumentException("A loopback client run needs at least one session.", nameof(sessions));
        DateTime deadlineUtc = DateTime.UtcNow + Deadline;
        return Task.Run<IReadOnlyList<LoopbackSessionResult>>(() =>
        {
            var results = new List<LoopbackSessionResult>(sessions.Count);
            foreach (LoopbackSession session in sessions)
            {
                using TcpClient? client = ConnectWithRetry(port, deadlineUtc, cancel);
                if (client is null)
                {
                    // Cancelled before the interposer ever accepted us: record it and stop (later sessions cannot
                    // connect either).
                    results.Add(new LoopbackSessionResult(false, Array.Empty<byte>()));
                    break;
                }
                NetworkStream stream = client.GetStream();
                var received = new List<byte>();
                if (session.Iec104Outstation)
                {
                    // A real outstation waits for the controlling station to start data transfer, then confirms it.
                    byte[] first = ReadExactly(stream, 6, deadlineUtc);
                    received.AddRange(first);
                    if (!first.AsSpan().SequenceEqual(Iec104TestFrames.StartDtAct()))
                        throw new InvalidDataException("loopback outstation: the first APDU from the interposer was " +
                            Convert.ToHexString(first) + ", not STARTDT act (680407000000).");
                    byte[] con = Iec104TestFrames.StartDtCon();
                    stream.Write(con, 0, con.Length);
                    stream.Flush();
                }
                Send(stream, session);
                if (session.CloseAfterSending)
                {
                    // A clean close (FIN after the data): the interposer reads the frames, then a zero-byte read, and
                    // drops this client (client-resets + 1).
                    client.Client.Shutdown(SocketShutdown.Send);
                    results.Add(new LoopbackSessionResult(true, received.ToArray()));
                }
                else
                {
                    received.AddRange(DrainUntilServerCloses(stream, deadlineUtc, session.Iec104Outstation));
                    results.Add(new LoopbackSessionResult(true, received.ToArray()));
                }
            }
            return results;
        }, CancellationToken.None);
    }

    // Connect to the interposer, retrying while its process starts (dotnet host start-up and JIT take a second or more
    // before it binds). Null when cancelled first. A deadline overrun throws: the interposer never listened.
    private static TcpClient? ConnectWithRetry(int port, DateTime deadlineUtc, CancellationToken cancel)
    {
        while (true)
        {
            if (cancel.IsCancellationRequested) return null;
            if (DateTime.UtcNow >= deadlineUtc)
                throw new TimeoutException($"loopback client: nothing accepted a connection on 127.0.0.1:{port} within {Deadline.TotalSeconds} s.");
            var client = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                client.Connect(IPAddress.Loopback, port);
                client.NoDelay = true;   // send each frame as it is written; the fragment test cuts frames on purpose
                return client;
            }
            catch (SocketException)
            {
                // Refused: the interposer is not listening yet. Wait a little and try again.
                client.Dispose();
                Thread.Sleep(100);
            }
        }
    }

    private static void Send(NetworkStream stream, LoopbackSession session)
    {
        if (session.Fragment)
        {
            byte[] all = session.Frames.SelectMany(f => f).ToArray();
            foreach (byte[] piece in Fragments(all))
            {
                stream.Write(piece, 0, piece.Length);
                stream.Flush();
            }
            return;
        }
        foreach (byte[] frame in session.Frames)
        {
            stream.Write(frame, 0, frame.Length);
            stream.Flush();
        }
    }

    // Cut a byte run at awkward places (inside the APCI / MBAP headers and inside the bodies), as the CF5 tests do.
    private static IEnumerable<byte[]> Fragments(byte[] all)
    {
        int[] cuts = { 1, 3, 7, 13, 29, 2, 61 };
        int at = 0, k = 0;
        while (at < all.Length)
        {
            int n = Math.Min(cuts[k++ % cuts.Length], all.Length - at);
            yield return all.AsSpan(at, n).ToArray();
            at += n;
        }
    }

    // Read exactly n octets before the deadline (the interposer's first APDU). A close or a timeout first throws.
    private static byte[] ReadExactly(NetworkStream stream, int n, DateTime deadlineUtc)
    {
        var b = new byte[n];
        int got = 0;
        while (got < n)
        {
            double remainingMs = (deadlineUtc - DateTime.UtcNow).TotalMilliseconds;
            if (remainingMs <= 0) throw new TimeoutException("loopback outstation: no STARTDT act from the interposer before the deadline.");
            stream.ReadTimeout = (int)Math.Max(1, remainingMs);
            int r;
            try { r = stream.Read(b, got, n - got); }
            catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut or SocketError.WouldBlock })
            {
                throw new TimeoutException("loopback outstation: no STARTDT act from the interposer before the deadline.", ex);
            }
            if (r == 0) throw new IOException("loopback outstation: the interposer closed before sending STARTDT act.");
            got += r;
        }
        return b;
    }

    // Stay connected and read until the interposer closes the socket (a zero-byte read, or a reset when its process
    // ends), returning every byte it sent. As an IEC 104 outstation, answer each TESTFR act with TESTFR con. A read that
    // times out at the deadline throws: the interposer did not stop.
    private static byte[] DrainUntilServerCloses(NetworkStream stream, DateTime deadlineUtc, bool iec104Outstation)
    {
        var received = new List<byte>();
        var buffer = new byte[1024];
        int answeredUpTo = 0;   // octets of 'received' already split into APDUs and answered
        while (true)
        {
            double remainingMs = (deadlineUtc - DateTime.UtcNow).TotalMilliseconds;
            if (remainingMs <= 0)
                throw new TimeoutException("loopback client: the interposer did not close the connection before the deadline.");
            stream.ReadTimeout = (int)Math.Max(1, remainingMs);
            int n;
            try
            {
                n = stream.Read(buffer, 0, buffer.Length);
            }
            catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut or SocketError.WouldBlock })
            {
                throw new TimeoutException("loopback client: the interposer did not close the connection before the deadline.", ex);
            }
            catch (IOException)
            {
                return received.ToArray();   // reset by the peer: its process ended with the socket open
            }
            if (n == 0) return received.ToArray();   // clean close
            received.AddRange(buffer.AsSpan(0, n).ToArray());
            if (!iec104Outstation) continue;
            // Split off every whole APDU received so far and answer the TESTFR acts.
            while (received.Count - answeredUpTo >= 2 && received.Count - answeredUpTo >= received[answeredUpTo + 1] + 2)
            {
                int length = received[answeredUpTo + 1] + 2;
                bool testFrAct = length == 6 && received[answeredUpTo] == 0x68 && received[answeredUpTo + 2] == 0x43;
                answeredUpTo += length;
                if (!testFrAct) continue;
                byte[] con = Iec104TestFrames.TestFrCon();
                try { stream.Write(con, 0, con.Length); stream.Flush(); }
                catch (IOException) { return received.ToArray(); }   // the interposer is closing
            }
        }
    }
}

/// <summary>
/// IEC 60870-5-104 frames, built octet by octet (monitor direction: what an RTU sends). The APDU is the APCI (start
/// 0x68, length of what follows, four control octets) plus, for an I-format frame, the ASDU: type id, variable
/// structure qualifier (count 1, SQ = 0), cause of transmission (3 = spontaneous, 6 = activation), originator 0, common
/// address 1 (two octets, little endian), then one information object: IOA (three octets, little endian), the
/// element, and for the time-tagged types a CP56Time2a time tag.
/// </summary>
internal static class Iec104TestFrames
{
    /// <summary>U-format STARTDT act (control octet 1 = 0x07).</summary>
    internal static byte[] StartDtAct() => UFrame(0x07);

    /// <summary>U-format STOPDT act (0x13).</summary>
    internal static byte[] StopDtAct() => UFrame(0x13);

    /// <summary>U-format TESTFR act (0x43).</summary>
    internal static byte[] TestFrAct() => UFrame(0x43);

    /// <summary>U-format STARTDT con (0x0B): the outstation's confirmation.</summary>
    internal static byte[] StartDtCon() => UFrame(0x0B);

    /// <summary>U-format TESTFR con (0x83).</summary>
    internal static byte[] TestFrCon() => UFrame(0x83);

    /// <summary>Split a byte run received from the interposer into APDUs (start 0x68, length octet, that many octets).
    /// A run that does not split cleanly throws: the interposer must only ever send whole, well-formed APDUs.</summary>
    internal static List<byte[]> SplitApdus(byte[] run)
    {
        var apdus = new List<byte[]>();
        int at = 0;
        while (at < run.Length)
        {
            if (run.Length - at < 2 || run[at] != 0x68 || run.Length - at < run[at + 1] + 2)
                throw new InvalidDataException($"not a whole IEC 104 APDU at octet {at} of {Convert.ToHexString(run)}.");
            int length = run[at + 1] + 2;
            apdus.Add(run.AsSpan(at, length).ToArray());
            at += length;
        }
        return apdus;
    }

    // 68 04 | c1 00 00 00
    private static byte[] UFrame(byte control1) => new byte[] { 0x68, 0x04, control1, 0x00, 0x00, 0x00 };

    /// <summary>S-format acknowledgement of everything up to receive sequence <paramref name="recvSeq"/>:
    /// 68 04 | 01 00 | N(R) as 15 bits shifted left one.</summary>
    internal static byte[] SAck(int recvSeq)
        => new byte[] { 0x68, 0x04, 0x01, 0x00, (byte)((recvSeq << 1) & 0xFE), (byte)((recvSeq >> 7) & 0xFF) };

    /// <summary>An I-format APDU around <paramref name="asdu"/> with send sequence <paramref name="sendSeq"/> and
    /// receive sequence 0: 68 len | N(S) lo hi | N(R) lo hi | ASDU.</summary>
    internal static byte[] IFrame(int sendSeq, byte[] asdu)
    {
        if (asdu.Length > 249) throw new ArgumentOutOfRangeException(nameof(asdu), "an IEC 104 ASDU is at most 249 octets.");
        var b = new byte[6 + asdu.Length];
        b[0] = 0x68;
        b[1] = (byte)(4 + asdu.Length);
        b[2] = (byte)((sendSeq << 1) & 0xFE);
        b[3] = (byte)((sendSeq >> 7) & 0xFF);
        b[4] = 0x00;
        b[5] = 0x00;
        asdu.CopyTo(b, 6);
        return b;
    }

    /// <summary>An ASDU carrying ONE information object: DUI (6 octets), IOA (3 octets), the element octets, and the
    /// CP56Time2a tag when <paramref name="time"/> is given.</summary>
    internal static byte[] Asdu(byte typeId, int ioa, byte[] element, DateTime? time = null, byte cause = 3)
    {
        var a = new List<byte>
        {
            typeId,
            0x01,                                   // VSQ: SQ = 0, one object
            cause,                                  // COT: test bit 0, P/N 0
            0x00,                                   // originator address
            0x01, 0x00,                             // common address 1
            (byte)(ioa & 0xFF), (byte)((ioa >> 8) & 0xFF), (byte)((ioa >> 16) & 0xFF),
        };
        a.AddRange(element);
        if (time is DateTime t) a.AddRange(Cp56Time2a(t));
        return a.ToArray();
    }

    /// <summary>
    /// CP56Time2a, seven octets: milliseconds of the minute (u16 little endian, seconds x 1000 + ms), minute (bits
    /// 0-5, IV bit 7 clear), hour (bits 0-4, SU bit 7 clear), day of month (bits 0-4; day of week bits 5-7 left 0),
    /// month (bits 0-3), year (bits 0-6, years since 2000).
    /// </summary>
    internal static byte[] Cp56Time2a(DateTime utc)
    {
        if (utc.Kind != DateTimeKind.Utc) throw new ArgumentException("CP56Time2a test stamps are built from UTC times.", nameof(utc));
        if (utc.Year is < 2000 or > 2127) throw new ArgumentOutOfRangeException(nameof(utc), "CP56Time2a holds years 2000 to 2127.");
        var t = new byte[7];
        BinaryPrimitives.WriteUInt16LittleEndian(t, (ushort)(utc.Second * 1000 + utc.Millisecond));
        t[2] = (byte)utc.Minute;
        t[3] = (byte)utc.Hour;
        t[4] = (byte)utc.Day;
        t[5] = (byte)utc.Month;
        t[6] = (byte)(utc.Year - 2000);
        return t;
    }

    /// <summary>Short float element (types 13 / 36): IEEE 754 single, little endian, then QDS.</summary>
    internal static byte[] ShortFloat(float value, byte qds = 0x00)
    {
        var e = new byte[5];
        BinaryPrimitives.WriteSingleLittleEndian(e, value);
        e[4] = qds;
        return e;
    }

    /// <summary>Normalised or scaled element (types 9 / 34 and 11 / 35): a signed 16-bit integer, little endian, then
    /// QDS. For a normalised value the decoded quantity is raw / 32768.</summary>
    internal static byte[] Int16(short raw, byte qds = 0x00)
    {
        var e = new byte[3];
        BinaryPrimitives.WriteInt16LittleEndian(e, raw);
        e[2] = qds;
        return e;
    }

    /// <summary>Binary counter element (types 15 / 37): signed 32-bit little endian, then the sequence octet (IV bit 7
    /// clear).</summary>
    internal static byte[] Counter(int count)
    {
        var e = new byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(e, count);
        return e;
    }

    /// <summary>Bitstring element (types 7 / 33): 32 bits, least significant octet first, then QDS.</summary>
    internal static byte[] Bitstring(uint bits)
    {
        var e = new byte[5];
        BinaryPrimitives.WriteUInt32LittleEndian(e, bits);
        return e;
    }

    /// <summary>Step position element (types 5 / 32): VTI (a 7-bit two's complement step in bits 0-6, the transient
    /// bit 7), then QDS.</summary>
    internal static byte[] StepPosition(int step, bool transient)
    {
        if (step is < -64 or > 63) throw new ArgumentOutOfRangeException(nameof(step), "a VTI step is -64 to +63.");
        byte vti = (byte)(step & 0x7F);
        if (transient) vti |= 0x80;
        return new byte[] { vti, 0x00 };
    }
}

/// <summary>
/// Modbus TCP frames, built octet by octet: the MBAP header (transaction id, protocol id 0, length of what follows,
/// unit id) and a read holding / input registers PDU (function 3 / 4).
/// </summary>
internal static class ModbusTestFrames
{
    /// <summary>A read request: MBAP (length 6) | fc | start hi lo | quantity hi lo.</summary>
    internal static byte[] ReadRequest(int transactionId, byte unit, byte functionCode, int start, int quantity)
        => new byte[]
        {
            (byte)(transactionId >> 8), (byte)transactionId, 0x00, 0x00, 0x00, 0x06, unit,
            functionCode, (byte)(start >> 8), (byte)start, (byte)(quantity >> 8), (byte)quantity,
        };

    /// <summary>A read response: MBAP (length 3 + byte count) | fc | byte count | registers, each big endian.</summary>
    internal static byte[] ReadResponse(int transactionId, byte unit, byte functionCode, params ushort[] registers)
    {
        int byteCount = registers.Length * 2;
        int length = 3 + byteCount;   // unit id + function code + byte count octet + the data
        var b = new List<byte>
        {
            (byte)(transactionId >> 8), (byte)transactionId, 0x00, 0x00, (byte)(length >> 8), (byte)length, unit,
            functionCode, (byte)byteCount,
        };
        foreach (ushort r in registers) { b.Add((byte)(r >> 8)); b.Add((byte)r); }
        return b.ToArray();
    }
}
