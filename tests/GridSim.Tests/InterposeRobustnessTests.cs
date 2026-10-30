using System.Net;
using System.Net.Sockets;
using GridSim.Cli.Interpose;
using GridSim.Core.Estimation;
using GridSim.Core.Interpose;
using GridSim.Core.Interpose.Protocols;
using GridSim.Core.IO;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The October 2026 interposer findings (logged by the docs session while writing manual ch. 25 and technical
/// ch. 20): quality multipliers and folds that had no test, a crosswalk mismatch that killed the verb, a client
/// reset that wedged the transport for ever, and window files that overwrote each other. Each test names the defect.
/// </summary>
public class InterposeRobustnessTests
{
    private static readonly DateTime Now = new(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Historical = Now.AddHours(-200);

    // ---- quality multipliers and folds (none had a test) --------------------------------------------------

    [Fact]
    public void Default_sigma_multipliers_are_substituted_x2_restart_x4_stale_x4_and_they_multiply()
    {
        QualityFold q = QualityFold.Default;
        Assert.Equal(1.0, q.SigmaMultiplier(RtuQuality.Good));
        Assert.Equal(2.0, q.SigmaMultiplier(RtuQuality.Substituted));
        Assert.Equal(4.0, q.SigmaMultiplier(RtuQuality.Restart));
        Assert.Equal(4.0, q.SigmaMultiplier(RtuQuality.NonTopical));
        // Several adverse flags multiply: substituted and restarting is 2 x 4 = 8; all three is 2 x 4 x 4 = 32.
        Assert.Equal(8.0, q.SigmaMultiplier(RtuQuality.Substituted | RtuQuality.Restart));
        Assert.Equal(32.0, q.SigmaMultiplier(RtuQuality.Substituted | RtuQuality.Restart | RtuQuality.NonTopical));
    }

    [Fact]
    public void The_multipliers_reach_the_mapped_sigma()
    {
        var map = new RtuPointMap
        {
            Bindings = new[]
            {
                new RtuPointBinding { Address = "A", Kind = MeasurementKind.VmPu, Bus = 1, Sigma = 0.01 },
                new RtuPointBinding { Address = "B", Kind = MeasurementKind.VmPu, Bus = 2, Sigma = 0.01 },
                new RtuPointBinding { Address = "C", Kind = MeasurementKind.VmPu, Bus = 3, Sigma = 0.01 },
            },
        };
        var points = new List<RtuPoint>
        {
            new("A", RtuPointType.Analog, 1.0, RtuQuality.Substituted, Historical),
            new("B", RtuPointType.Analog, 1.0, RtuQuality.Restart, Historical),
            new("C", RtuPointType.Analog, 1.0, RtuQuality.Substituted | RtuQuality.Restart, Historical),
        };
        MeasurementSet set = RtuMeasurementSetMapper.Map(points, map, Historical, utcNow: Now);
        Assert.Equal(new[] { 0.01 * 2.0, 0.01 * 4.0, 0.01 * 8.0 }, set.Measurements.Select(m => m.Sigma).ToArray());
    }

    [Theory]
    [InlineData((byte)0x01, RtuQuality.Good)]                                   // online, nothing else
    [InlineData((byte)0x00, RtuQuality.Invalid)]                                // online bit clear
    [InlineData((byte)0x03, RtuQuality.Restart)]                                // online + restart
    [InlineData((byte)0x05, RtuQuality.CommLost)]                               // online + comm-lost
    [InlineData((byte)0x21, RtuQuality.Overflow)]                               // online + over-range
    [InlineData((byte)0x27, RtuQuality.Restart | RtuQuality.CommLost | RtuQuality.Overflow)]
    public void Dnp3_flags_fold_to_the_documented_quality(byte flags, RtuQuality expected)
        => Assert.Equal(expected, QualityFold.FromDnp3(flags));

    [Fact]
    public void A_dnp3_restart_point_is_kept_and_down_weighted_but_comm_lost_or_offline_is_dropped()
    {
        Assert.False(QualityFold.ShouldDrop(QualityFold.FromDnp3(0x03)));                 // restart: admitted
        Assert.Equal(4.0, QualityFold.Default.SigmaMultiplier(QualityFold.FromDnp3(0x03)));
        Assert.True(QualityFold.ShouldDrop(QualityFold.FromDnp3(0x05)));                  // comm-lost
        Assert.True(QualityFold.ShouldDrop(QualityFold.FromDnp3(0x00)));                  // offline
        Assert.True(QualityFold.ShouldDrop(QualityFold.FromDnp3(0x21)));                  // over-range
    }

    [Theory]
    [InlineData(true, false, false, RtuQuality.Good)]
    [InlineData(false, false, false, RtuQuality.Invalid)]
    [InlineData(true, true, false, RtuQuality.Invalid)]
    [InlineData(true, false, true, RtuQuality.Invalid)]
    [InlineData(false, true, true, RtuQuality.Invalid)]
    public void Goose_validity_test_and_commissioning_fold_to_good_or_invalid(
        bool valid, bool test, bool needsCommissioning, RtuQuality expected)
    {
        RtuQuality q = QualityFold.FromGoose(valid, test, needsCommissioning);
        Assert.Equal(expected, q);
        Assert.Equal(expected != RtuQuality.Good, QualityFold.ShouldDrop(q));
    }

    // ---- a crosswalk mismatch is per window, not fatal ----------------------------------------------------

    private static byte[] Response(int txn, int unit, byte fc, params ushort[] regs)
    {
        var pdu = new List<byte> { fc, (byte)(regs.Length * 2) };
        foreach (ushort r in regs) { pdu.Add((byte)(r >> 8)); pdu.Add((byte)(r & 0xFF)); }
        int length = 1 + pdu.Count;
        var f = new List<byte> { (byte)(txn >> 8), (byte)txn, 0, 0, (byte)(length >> 8), (byte)length, (byte)unit };
        f.AddRange(pdu);
        return f.ToArray();
    }

    private static byte[] Request(int txn, int unit, byte fc, int start, int qty) => new byte[]
    {
        (byte)(txn >> 8), (byte)txn, 0, 0, 0, 6, (byte)unit, fc,
        (byte)(start >> 8), (byte)start, (byte)(qty >> 8), (byte)qty,
    };

    [Fact]
    public void A_window_that_does_not_fit_its_binding_is_counted_discarded_and_does_not_poison_the_next()
    {
        // F32 at register 41 needs register 42 in the same response. Window 1 polls 40..41 only (mismatch);
        // window 2 polls 41..42 (fits). Before the fix the failed window was never cleared, so window 2 re-mapped
        // window 1's points and failed too, for ever.
        var map = new RtuPointMap
        {
            Bindings = new[] { new RtuPointBinding { Address = "MB:U1:3:@41", Kind = MeasurementKind.PInjMw, Bus = 3, DataType = RtuRegisterType.F32 } },
        };
        var pipe = new InterposePipeline(new ModbusProtocol(), map);
        pipe.Ingest(Request(1, 1, 3, 40, 2));
        pipe.Ingest(Response(1, 1, 3, 1, 2));
        Assert.Throws<InvalidDataException>(() => pipe.FlushWindow(Historical, utcNow: Now));
        Assert.Equal(1, pipe.MapErrors);
        Assert.Equal(0, pipe.PendingPoints);   // the bad window is gone

        pipe.Ingest(Request(2, 1, 3, 41, 2));
        pipe.Ingest(Response(2, 1, 3, 0x42F6, 0xE979));
        MeasurementSet? ok = pipe.FlushWindow(Historical, utcNow: Now);
        Assert.Equal(123.456001281738, Assert.Single(ok!.Measurements).Value, 9);
        Assert.Equal(1, pipe.MapErrors);
    }

    [Fact]
    public void Overlapping_32_bit_bindings_are_refused_at_load()
    {
        var ex = Assert.Throws<InvalidDataException>(() => RtuPointMap.ValidateRegisterLayout(new[]
        {
            new RtuPointBinding { Address = "MB:U1:3:@40", Kind = MeasurementKind.PInjMw, Bus = 3, DataType = RtuRegisterType.F32 },
            new RtuPointBinding { Address = "MB:U1:3:@41", Kind = MeasurementKind.QInjMvar, Bus = 3 },
        }));
        Assert.Contains("MB:U1:3:@41", ex.Message);

        // Adjacent but not overlapping (40-41 and 42-43) is fine.
        RtuPointMap.ValidateRegisterLayout(new[]
        {
            new RtuPointBinding { Address = "MB:U1:3:@40", Kind = MeasurementKind.PInjMw, Bus = 3, DataType = RtuRegisterType.F32 },
            new RtuPointBinding { Address = "MB:U1:3:@42", Kind = MeasurementKind.QInjMvar, Bus = 3, DataType = RtuRegisterType.F32 },
        });
    }

    [Fact]
    public void A_32_bit_binding_on_the_last_register_is_refused_at_load()
    {
        var ex = Assert.Throws<InvalidDataException>(() => RtuPointMap.ValidateRegisterLayout(new[]
        {
            new RtuPointBinding { Address = "MB:U1:3:@65535", Kind = MeasurementKind.PInjMw, Bus = 3, DataType = RtuRegisterType.U32 },
        }));
        Assert.Contains("last Modbus register", ex.Message);
    }

    [Fact]
    public void Load_runs_the_register_layout_check()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pm-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
            { "schema": "gridsim-rtu-pointmap/1", "bindings": [
              { "address": "MB:U1:3:@40", "kind": "PInjMw", "bus": 3, "dataType": "S32" },
              { "address": "MB:U1:3:@41", "kind": "PInjMw", "bus": 4 } ] }
            """);
            Assert.Throws<InvalidDataException>(() => RtuPointMap.Load(path));
        }
        finally { File.Delete(path); }
    }

    // ---- window files never overwrite ---------------------------------------------------------------------

    [Fact]
    public void A_second_window_with_the_same_name_is_kept_under_a_dup_name()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"win-{Guid.NewGuid():N}");
        try
        {
            var a = new MeasurementSet { Case = "a", TakenUtc = Historical, Measurements = new List<Measurement>() };
            var b = new MeasurementSet { Case = "b", TakenUtc = Historical, Measurements = new List<Measurement>() };
            string file = Path.Combine(dir, "20260712T000000000Z-000000.json");
            string first = a.SaveWithoutOverwrite(file);
            string second = b.SaveWithoutOverwrite(file);
            string third = b.SaveWithoutOverwrite(file);
            Assert.Equal(Path.GetFullPath(file), first);
            Assert.Equal(Path.Combine(Path.GetFullPath(dir), "20260712T000000000Z-000000-dup1.json"), second);
            Assert.EndsWith("-dup2.json", third);
            // The first file still holds window a: nothing was overwritten.
            Assert.Equal("a", MeasurementSet.Load(first, DateTime.MaxValue).Case);
            Assert.Equal("b", MeasurementSet.Load(second, DateTime.MaxValue).Case);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void NeverOverwrite_opens_the_requested_name_when_it_is_free()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"nw-{Guid.NewGuid():N}");
        try
        {
            string path = NeverOverwrite.WriteAllText(Path.Combine(dir, "x.txt"), "1");
            Assert.Equal(Path.Combine(Path.GetFullPath(dir), "x.txt"), path);
            Assert.Equal("1", File.ReadAllText(path));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    // ---- the transport survives a client reset ------------------------------------------------------------

    /// <summary>Call TryReadFrame until it yields a frame or the deadline passes.</summary>
    private static byte[]? ReadWithin(IRtuTransport t, TimeSpan limit)
    {
        DateTime end = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < end)
            if (t.TryReadFrame(out byte[] f, TimeSpan.FromMilliseconds(50))) return f;
        return null;
    }

    [Fact]
    public void After_a_client_resets_the_connection_the_next_client_is_accepted()
    {
        using var t = new TcpMbapFrameTransport("127.0.0.1", 0);
        int port = t.LocalEndpoint.Port;
        byte[] frame1 = Response(1, 1, 3, 1030);
        byte[] frame2 = Response(2, 1, 3, 1040);

        // Client 1 sends a frame, then aborts with a reset (linger 0 makes Close send RST, not FIN).
        using (var c1 = new TcpClient())
        {
            c1.Connect(IPAddress.Loopback, port);
            c1.GetStream().Write(frame1);
            Assert.Equal(frame1, ReadWithin(t, TimeSpan.FromSeconds(5)));
            c1.LingerState = new LingerOption(true, 0);
            c1.Close();
        }

        // Client 2 connects. Before the fix the transport took the reset as a read timeout, kept the dead socket
        // and never accepted client 2, so this read never returned a frame.
        using var c2 = new TcpClient();
        c2.Connect(IPAddress.Loopback, port);
        c2.GetStream().Write(frame2);
        Assert.Equal(frame2, ReadWithin(t, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, t.ClientResets);
    }

    [Fact]
    public void After_a_client_closes_cleanly_the_next_client_is_accepted()
    {
        using var t = new TcpDelimitedFrameTransport("127.0.0.1", 0, startByte: 0x68);
        int port = t.LocalEndpoint.Port;
        byte[] sFrame = { 0x68, 0x04, 0x01, 0x00, 0x02, 0x00 };   // an IEC-104 S-format APDU

        using (var c1 = new TcpClient())
        {
            c1.Connect(IPAddress.Loopback, port);
            c1.GetStream().Write(sFrame);
            Assert.Equal(sFrame, ReadWithin(t, TimeSpan.FromSeconds(5)));
        }   // Dispose closes cleanly (FIN)

        using var c2 = new TcpClient();
        c2.Connect(IPAddress.Loopback, port);
        c2.GetStream().Write(sFrame);
        Assert.Equal(sFrame, ReadWithin(t, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, t.ClientResets);
    }

    [Fact]
    public void A_read_timeout_is_not_a_reset()
    {
        using var t = new TcpMbapFrameTransport("127.0.0.1", 0);
        using var c = new TcpClient();
        c.Connect(IPAddress.Loopback, t.LocalEndpoint.Port);
        // Nothing is sent: several timed-out reads must keep the same client (no reset counted).
        for (int k = 0; k < 4; k++) Assert.False(t.TryReadFrame(out _, TimeSpan.FromMilliseconds(30)));
        Assert.Equal(0, t.ClientResets);
        byte[] frame = Response(3, 1, 3, 7);
        c.GetStream().Write(frame);
        Assert.Equal(frame, ReadWithin(t, TimeSpan.FromSeconds(5)));
    }
}
