using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using GridSim.Core.Dynamics;
using GridSim.Core.Estimation;
using GridSim.Core.Frequency;
using GridSim.Core.Interpose;
using GridSim.Core.Interpose.Protocols;
using GridSim.Core.Replay;
using GridSim.Core.Synchrophasor;
using GridSim.Gda.Interpose;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// October 2026: documented limitations of the interposer, the C37.118 decoder, the quarantine store and the replay
/// validator that are now fixed, one test (or a few) per finding: GR-304 (DATA frames of another stream), GR-826 (the
/// protocol version and CONFIG-1), GR-801 / GR-329 / GR-317 (Modbus transaction ids, exception responses and state
/// across connections), GR-818 (the UDP sender filter), GR-828 (the quarantine marker and the window time) and GR-800
/// (the windowed RoCoF on an irregular grid).
/// </summary>
public sealed class LimitationFixesStreamAndLinkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gridsim-limfix-" + Guid.NewGuid().ToString("N"));

    private static readonly Doc15Frames.Pmu Pmu7 = new(7, "STN_A", new[] { Doc15Frames.Voltage });
    private const uint Soc = 1_700_000_000;

    // Rewrite a frame's header IDCODE (bytes 4-5) and its CRC.
    private static byte[] WithIdCode(byte[] frame, ushort idCode)
    {
        byte[] f = (byte[])frame.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(4), idCode);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(f.Length - 2), C37118Crc.Compute(f.AsSpan(0, f.Length - 2)));
        return f;
    }

    // Rewrite SYNC byte 1 (frame type in bits 6..4, version in bits 3..0) and the CRC.
    private static byte[] WithSync1(byte[] frame, byte sync1)
    {
        byte[] f = (byte[])frame.Clone();
        f[1] = sync1;
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(f.Length - 2), C37118Crc.Compute(f.AsSpan(0, f.Length - 2)));
        return f;
    }

    // ---- GR-304 / GR-826: C37.118 ---------------------------------------------------------------------------------

    [Fact]
    public void GR304_a_data_frame_of_another_stream_is_refused_against_a_parsed_configuration()
    {
        C37118ConfigFrame cfg = C37118ConfigParser.Parse(Doc15Frames.Config(1_000_000, Soc, Pmu7));
        Assert.Equal(7, cfg.StreamIdCode);   // the CONFIG frame's own header IDCODE (Doc15Frames writes 7)

        byte[] data = Doc15Frames.Data(Soc + 1, 0, new[] { (1.0f, 0.0f) });
        Assert.Equal(FrameDecodeStatus.Ok, C37118Decoder.DecodeData(data, cfg).Status);

        // The same bytes from stream 8: same size, so before GR-304 it decoded Ok with stream 7's configuration.
        C37118StreamDataFrame other = C37118Decoder.DecodeData(WithIdCode(data, 8), cfg);
        Assert.Equal(FrameDecodeStatus.WrongStream, other.Status);
        Assert.Equal(8, other.StreamIdCode);
        Assert.Empty(other.Blocks);

        // A configuration built by hand carries no stream IDCODE and is not checked (unchanged behaviour).
        var handBuilt = cfg with { StreamIdCode = null };
        Assert.Equal(FrameDecodeStatus.Ok, C37118Decoder.DecodeData(WithIdCode(data, 8), handBuilt).Status);
    }

    [Fact]
    public void GR826_config1_is_flagged_and_protocol_version_zero_is_refused()
    {
        byte[] config2 = Doc15Frames.Config(1_000_000, Soc, Pmu7);
        Assert.False(C37118ConfigParser.Parse(config2).IsConfig1);

        // CONFIG-1 (type 2), version 1: parsed with the same fields, flagged as the capability frame.
        C37118ConfigFrame config1 = C37118ConfigParser.Parse(WithSync1(config2, 0x21));
        Assert.True(config1.IsConfig1);
        Assert.Equal(1_000_000, config1.TimeBase);

        // CONFIG-2 with version nibble 0: no edition defines it.
        var ex = Assert.Throws<C37118FrameException>(() => C37118ConfigParser.Parse(WithSync1(config2, 0x30)));
        Assert.Contains("protocol version 0", ex.Message, StringComparison.Ordinal);
    }

    // ---- GR-801 / GR-329 / GR-317: Modbus -------------------------------------------------------------------------

    private static IReadOnlyList<RtuPoint> Decode(ModbusProtocol p, byte[] frame) => p.Decode(frame, QualityFold.Default);

    // An exception response: MBAP (length 3) | fc | 0x80 | exception code.
    private static byte[] ExceptionResponse(int transactionId, byte unit, byte functionCode, byte code)
        => new byte[] { (byte)(transactionId >> 8), (byte)transactionId, 0x00, 0x00, 0x00, 0x03, unit, (byte)(functionCode | 0x80), code };

    [Fact]
    public void GR801_an_exception_response_ends_its_transaction()
    {
        var p = new ModbusProtocol();
        Assert.Empty(Decode(p, ModbusTestFrames.ReadRequest(5, 1, 3, 100, 2)));
        Assert.Empty(Decode(p, ExceptionResponse(5, 1, 3, 0x02)));   // illegal data address: no points
        // A later response reusing id 5 with no new request is NOT paired with the answered request: positional addresses.
        IReadOnlyList<RtuPoint> pts = Decode(p, ModbusTestFrames.ReadResponse(5, 1, 3, 11, 12));
        Assert.Equal(new[] { "MB:U1:3:0", "MB:U1:3:1" }, pts.Select(x => x.Address).ToArray());
    }

    [Fact]
    public void GR801_two_different_requests_under_one_outstanding_id_make_its_response_refused()
    {
        var p = new ModbusProtocol();
        Decode(p, ModbusTestFrames.ReadRequest(9, 1, 3, 100, 2));
        Decode(p, ModbusTestFrames.ReadRequest(9, 1, 3, 200, 2));   // pipelined, same id, other block
        var ex = Assert.Throws<InvalidDataException>(() => Decode(p, ModbusTestFrames.ReadResponse(9, 1, 3, 1, 2)));
        Assert.Contains("transaction 9", ex.Message, StringComparison.Ordinal);
        // The id is clean again: a new request and its response pair normally.
        Decode(p, ModbusTestFrames.ReadRequest(9, 1, 3, 300, 1));
        Assert.Equal("MB:U1:3:@300", Decode(p, ModbusTestFrames.ReadResponse(9, 1, 3, 7)).Single().Address);
    }

    [Fact]
    public void GR801_the_same_request_sent_twice_is_a_retry_not_an_ambiguity()
    {
        var p = new ModbusProtocol();
        Decode(p, ModbusTestFrames.ReadRequest(3, 1, 4, 10, 1));
        Decode(p, ModbusTestFrames.ReadRequest(3, 1, 4, 10, 1));
        Assert.Equal("MB:U1:4:@10", Decode(p, ModbusTestFrames.ReadResponse(3, 1, 4, 42)).Single().Address);
    }

    [Fact]
    public void GR329_reset_session_forgets_the_old_connection()
    {
        var p = new ModbusProtocol();
        Decode(p, ModbusTestFrames.ReadRequest(1, 1, 3, 100, 1));
        // A positional block of 2 seen on the old connection.
        Decode(p, ModbusTestFrames.ReadResponse(77, 1, 3, 1, 2));
        ((IRtuSessionState)p).ResetSession();
        // The old request no longer pairs: positional address.
        Assert.Equal("MB:U1:3:0", Decode(p, ModbusTestFrames.ReadResponse(1, 1, 3, 5)).Single().Address);
        ((IRtuSessionState)p).ResetSession();
        // And the old positional block size no longer refuses a differently sized block.
        Assert.Equal(3, Decode(p, ModbusTestFrames.ReadResponse(78, 1, 3, 1, 2, 3)).Count);
    }

    // ---- GR-818: the UDP sender filter -----------------------------------------------------------------------------

    [Fact]
    public void GR818_udp_datagrams_from_another_sender_are_dropped_and_counted()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        // Only 192.0.2.1 (TEST-NET-1, never this machine) may send, so the loopback datagrams are all foreign.
        var options = new C37118LiveOptions(1, TimeSpan.FromSeconds(2), IPAddress.Parse("192.0.2.1"));
        // Harness changed 2.0.161: the two datagrams are sent from the bound callback itself (the socket is bound, so the
        // OS queues them for the receive), not from a thread-pool task. Under a full suite run the task could start after
        // the 2 s window and the test then saw no datagram at all. The receiver, the window and every assertion are as
        // before; only when the datagrams leave changed (they now leave before the receive starts, never after it ends).
        C37118LiveCapture cap = C37118LiveReceiver.ReceiveUdpOn(udp, options, ep =>
        {
            using var tx = new UdpClient();
            tx.Send(Doc15Frames.Config(1_000_000, Soc, Pmu7), ep);
            tx.Send(Doc15Frames.Data(Soc + 1, 0, new[] { (1.0f, 0.0f) }), ep);
        });
        Assert.True(cap.TimedOut);
        Assert.Equal(0, cap.Frames);
        Assert.Empty(cap.Bytes);
        Assert.True(cap.ForeignDatagrams >= 1, $"foreign datagrams {cap.ForeignDatagrams}");
    }

    // ---- GR-828: the quarantine store ------------------------------------------------------------------------------

    [Fact]
    public void GR828_the_marker_is_written_when_the_store_opens_and_a_window_needs_its_time()
    {
        var store = new QuarantineLiveStore(_root, "rtu_live");
        Assert.True(File.Exists(Path.Combine(_root, "Parquet", "rtu_live", "_QUARANTINE.json")));   // before any partition
        var untimed = new MeasurementSet
        {
            Case = "case14",
            BaseMva = 100.0,
            TakenUtc = null,
            Measurements = new[] { new Measurement(MeasurementKind.VmPu, 1.0, 0.01, Bus: 1, Source: "rtu") },
        };
        Assert.Throws<ArgumentException>(() => store.WriteWindow(untimed, "iec104", 1.0 / 60));
        Assert.Equal(0, store.PartitionsWritten);
    }

    // ---- GR-800: the windowed RoCoF needs a uniform grid -----------------------------------------------------------

    [Fact]
    public void GR800_an_irregular_trajectory_is_refused_by_the_windowed_rocof()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        static FrequencyResponse Response(IReadOnlyList<FrequencyPoint> pts) => new(0.0, 49.9, 1.0, 49.95, pts);

        var uniform = Enumerable.Range(0, 20).Select(i => new FrequencyPoint(t0.AddSeconds(0.1 * i), 50.0 - 0.01 * i, 0.0)).ToList();
        double ok = ReplayValidator.MaxWindowedRocof(Response(uniform), 1.0);
        Assert.True(double.IsFinite(ok));

        var irregular = new List<FrequencyPoint>(uniform);
        irregular[5] = irregular[5] with { Time = t0.AddSeconds(0.55) };   // steps 0.15 s then 0.05 s around it
        var ex = Assert.Throws<InvalidOperationException>(() => ReplayValidator.MaxWindowedRocof(Response(irregular), 1.0));
        Assert.Contains("uniform grid", ex.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }
}
