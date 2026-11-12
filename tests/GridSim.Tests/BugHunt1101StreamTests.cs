using System.Net;
using System.Net.Sockets;
using GridSim.Cli.Interpose;
using GridSim.Core.Estimation;
using GridSim.Core.Interpose;
using GridSim.Core.Synchrophasor;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Static bug hunt of 2026-11-01: live stream transports. #160 (separate connect, first-frame and collection budgets on
/// the C37.118 TCP receive), #173 (UDP listener on loopback by default, sender rule off loopback), #174 (interpose on
/// loopback by default, client allow-list, idle clients dropped) and #177 (a bound on non-DATA frames).
/// Every device here is simulated in-process on loopback, on a dedicated thread (not the thread pool, see #160).
/// </summary>
public sealed partial class BugHunt1101WebTests
{
    private static readonly Doc15Frames.Pmu LivePmu = new(7, "STN_A", new[] { Doc15Frames.Voltage, Doc15Frames.Current });
    private const uint LiveSoc = 1_700_000_000;   // 2023: well before the horizon
    private static byte[] LiveConfig() => Doc15Frames.Config(1_000_000, LiveSoc, LivePmu);
    private static byte[] LiveData(int k) => Doc15Frames.Data(LiveSoc + 1, (uint)(20_000 * k), new[] { (1.0f, 0.01f * k), (0.5f, -0.2f) });

    // A simulated TCP PMU on its own thread: accept, read the two COMMAND frames, wait delayBeforeReply, write frames.
    private static int StartSlowTcpPmu(TimeSpan delayBeforeReply, IEnumerable<byte[]> frames)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        List<byte[]> toSend = frames.ToList();
        var t = new Thread(() =>
        {
            try
            {
                using TcpClient c = listener.AcceptTcpClient();
                using NetworkStream s = c.GetStream();
                s.ReadTimeout = 20_000;
                var cmd = new byte[C37118Command.FrameSize * 2];
                int got = 0;
                while (got < cmd.Length) { int r = s.Read(cmd, got, cmd.Length - got); if (r == 0) return; got += r; }
                Thread.Sleep(delayBeforeReply);
                foreach (byte[] f in toSend) { s.Write(f); s.Flush(); }
                Thread.Sleep(3000);   // keep the connection open while the receiver reads
            }
            catch (IOException) { }
            catch (SocketException) { }
            finally { listener.Stop(); }
        }) { IsBackground = true, Name = "BH1101 slow PMU" };
        t.Start();
        return port;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // #160
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void B160_the_collection_timeout_starts_at_the_first_frame_not_before_the_connect()
    {
        // The device answers 1.5 s after the commands, then sends CONFIG and two DATA frames at once. With one clock
        // started before the connect (the old code) a 1 s Timeout expired before the CONFIG arrived and the capture
        // was empty. Now the first frame has its own 5 s budget and the 1 s starts at it.
        int port = StartSlowTcpPmu(TimeSpan.FromMilliseconds(1500), new[] { LiveConfig(), LiveData(0), LiveData(1) });
        var options = new C37118LiveOptions(2, TimeSpan.FromSeconds(1), FirstFrameTimeout: TimeSpan.FromSeconds(5));
        C37118LiveCapture cap = C37118LiveReceiver.ReceiveTcp("127.0.0.1", port, 7, options);
        Assert.False(cap.TimedOut);
        Assert.Equal(2, cap.DataFrames);
        Assert.Equal(LiveConfig().Concat(LiveData(0)).Concat(LiveData(1)).ToArray(), cap.Bytes);
    }

    [Fact]
    public void B160_a_device_that_never_answers_still_times_out_on_the_first_frame_budget()
    {
        int port = StartSlowTcpPmu(TimeSpan.FromSeconds(10), new[] { LiveConfig() });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        C37118LiveCapture cap = C37118LiveReceiver.ReceiveTcp("127.0.0.1", port, 7,
            new C37118LiveOptions(1, TimeSpan.FromSeconds(30), FirstFrameTimeout: TimeSpan.FromMilliseconds(600)));
        Assert.True(cap.TimedOut);
        Assert.Empty(cap.Bytes);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"waited {sw.Elapsed}: the first-frame budget was not applied");
    }

    [Fact]
    public void B160_the_budgets_default_to_the_timeout_and_must_be_positive()
    {
        var o = new C37118LiveOptions(1, TimeSpan.FromSeconds(7));
        Assert.Equal(TimeSpan.FromSeconds(7), o.ConnectBudget);
        Assert.Equal(TimeSpan.FromSeconds(7), o.FirstFrameBudget);
        Assert.Throws<ArgumentOutOfRangeException>(() => (o with { ConnectTimeout = TimeSpan.Zero }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (o with { FirstFrameTimeout = TimeSpan.FromSeconds(-1) }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (o with { MaxNonDataFrames = 0 }).Validate());
    }

    // ---------------------------------------------------------------------------------------------------------------
    // #173
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void B173_a_udp_listener_beyond_loopback_needs_a_named_sender_or_the_explicit_opt_out()
    {
        var plain = new C37118LiveOptions(1, TimeSpan.FromSeconds(1));
        var ex = Assert.Throws<ArgumentException>(() => plain.RequireUdpSenderRule(IPAddress.Any));
        Assert.Contains("--live-udp-source", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--live-udp-any-sender", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => plain.RequireUdpSenderRule(IPAddress.Parse("192.0.2.10")));
        Assert.Throws<ArgumentException>(() => plain.RequireUdpSenderRule(IPAddress.IPv6Any));
        plain.RequireUdpSenderRule(IPAddress.Loopback);
        plain.RequireUdpSenderRule(IPAddress.IPv6Loopback);
        (plain with { UdpSource = IPAddress.Parse("192.0.2.20") }).RequireUdpSenderRule(IPAddress.Any);
        (plain with { AllowAnyUdpSender = true }).RequireUdpSenderRule(IPAddress.Any);

        // ReceiveUdp refuses before it opens the port (a port nothing else uses, so a bind would otherwise succeed).
        Assert.Throws<ArgumentException>(() => C37118LiveReceiver.ReceiveUdp(IPAddress.Any, 47_713, plain));
        Assert.Equal("127.0.0.1", C37118LiveReceiver.ParseAddress("udp://:4713").Host);
    }

    [Fact]
    public void B173_the_cli_refuses_an_every_interface_listener_without_a_sender_rule_exit_2()
    {
        CliProcess.Result r = CliProcess.RunBuilt("pmu-ingest", "--live", "udp://0.0.0.0:47714", "--live-frames", "1", "--live-seconds", "1");
        Assert.True(r.ExitCode == 2, $"expected exit 2\n{r}");
        Assert.Contains("--live-udp-source", r.StdErr, StringComparison.Ordinal);
        Assert.Contains("--live-udp-any-sender", r.StdErr, StringComparison.Ordinal);

        CliProcess.Result both = CliProcess.RunBuilt("pmu-ingest", "--live", "udp://0.0.0.0:47714", "--live-frames", "1", "--live-seconds", "1",
            "--live-udp-source", "192.0.2.1", "--live-udp-any-sender");
        Assert.True(both.ExitCode == 2, $"expected exit 2\n{both}");
        CliProcess.Result tcp = CliProcess.RunBuilt("pmu-ingest", "--live", "tcp://127.0.0.1:47715", "--live-frames", "1", "--live-udp-any-sender");
        Assert.True(tcp.ExitCode == 2, $"expected exit 2\n{tcp}");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // #177
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void B177_a_udp_sender_flooding_non_data_frames_is_stopped_not_buffered_until_the_timeout()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var options = new C37118LiveOptions(1, TimeSpan.FromSeconds(20), MaxNonDataFrames: 3);
        var ex = Assert.Throws<InvalidDataException>(() => C37118LiveReceiver.ReceiveUdpOn(udp, options, ep =>
        {
            var tx = new Thread(() =>
            {
                using var c = new UdpClient();
                for (int k = 0; k < 6; k++) c.Send(LiveConfig(), ep);
            }) { IsBackground = true };
            tx.Start();
        }));
        Assert.Contains("more than 3 frames that are not DATA frames", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void B177_a_tcp_device_flooding_non_data_frames_is_stopped()
    {
        int port = StartSlowTcpPmu(TimeSpan.Zero, Enumerable.Repeat(0, 6).Select(_ => LiveConfig()));
        var ex = Assert.Throws<InvalidDataException>(() => C37118LiveReceiver.ReceiveTcp("127.0.0.1", port, 7,
            new C37118LiveOptions(1, TimeSpan.FromSeconds(20), MaxNonDataFrames: 3)));
        Assert.Contains("more than 3 frames that are not DATA frames", ex.Message, StringComparison.Ordinal);
        // The default is generous (a device sends one CONFIG per configuration) but finite.
        Assert.Equal(1024, new C37118LiveOptions(1, TimeSpan.FromSeconds(1)).MaxNonDataFrames);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // #174
    // ---------------------------------------------------------------------------------------------------------------

    // A Modbus TCP read-holding-registers response, one register.
    private static byte[] MbapFrame(byte tx) => new byte[] { 0x00, tx, 0x00, 0x00, 0x00, 0x05, 0x01, 0x03, 0x02, 0x04, 0x06 };

    private static byte[]? PollFrame(IRtuTransport t, TimeSpan limit)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < limit)
            if (t.TryReadFrame(out byte[] f, TimeSpan.FromMilliseconds(50))) return f;
        return null;
    }

    [Fact]
    public void B174_a_client_outside_the_allow_list_is_refused_and_never_takes_the_slot()
    {
        var policy = new InterposeAccessPolicy(new[] { IPAddress.Parse("192.0.2.1") }, InterposeAccessPolicy.DefaultIdleTimeoutMs);
        using var t = new TcpMbapFrameTransport("127.0.0.1", 0, policy: policy);
        using var c = new TcpClient();
        c.Connect(IPAddress.Loopback, t.LocalEndpoint.Port);
        c.GetStream().Write(MbapFrame(1));
        Assert.Null(PollFrame(t, TimeSpan.FromMilliseconds(600)));
        Assert.True(t.RefusedClients >= 1);

        // Listed: loopback may connect and deliver.
        using var t2 = new TcpMbapFrameTransport("127.0.0.1", 0, policy: new InterposeAccessPolicy(new[] { IPAddress.Loopback }, 0));
        using var c2 = new TcpClient();
        c2.Connect(IPAddress.Loopback, t2.LocalEndpoint.Port);
        c2.GetStream().Write(MbapFrame(2));
        Assert.Equal(MbapFrame(2), PollFrame(t2, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, t2.RefusedClients);
    }

    [Fact]
    public void B174_a_silent_client_is_dropped_after_the_idle_timeout_and_the_real_rtu_gets_the_slot()
    {
        long now = 0;
        var policy = new InterposeAccessPolicy(null, 1_000);
        using var t = new TcpMbapFrameTransport("127.0.0.1", 0, clockMs: () => now, policy: policy);
        int port = t.LocalEndpoint.Port;

        // The squatter connects and says nothing.
        using var squatter = new TcpClient();
        squatter.Connect(IPAddress.Loopback, port);
        Assert.Null(PollFrame(t, TimeSpan.FromMilliseconds(300)));   // accepted, nothing to read
        // The real RTU queues behind it.
        using var rtu = new TcpClient();
        rtu.Connect(IPAddress.Loopback, port);
        rtu.GetStream().Write(MbapFrame(9));
        Assert.Null(PollFrame(t, TimeSpan.FromMilliseconds(300)));   // still the squatter's slot (the old behaviour, for ever)

        now += 1_000;   // the squatter has been silent for the idle timeout
        Assert.False(t.TryReadFrame(out _, TimeSpan.FromMilliseconds(50)));
        Assert.Equal(1, t.IdleDrops);
        Assert.Equal(1, t.ClientResets);
        Assert.Equal(MbapFrame(9), PollFrame(t, TimeSpan.FromSeconds(5)));   // the RTU now holds the slot
    }

    [Fact]
    public void B174_an_active_client_is_never_idle_dropped_and_zero_turns_the_timer_off()
    {
        long now = 0;
        using var t = new TcpMbapFrameTransport("127.0.0.1", 0, clockMs: () => now, policy: new InterposeAccessPolicy(null, 1_000));
        using var c = new TcpClient();
        c.Connect(IPAddress.Loopback, t.LocalEndpoint.Port);
        for (byte k = 1; k <= 3; k++)
        {
            now += 900;   // under the timeout between frames
            c.GetStream().Write(MbapFrame(k));
            Assert.Equal(MbapFrame(k), PollFrame(t, TimeSpan.FromSeconds(5)));
        }
        Assert.Equal(0, t.IdleDrops);

        long now2 = 0;
        using var off = new TcpMbapFrameTransport("127.0.0.1", 0, clockMs: () => now2, policy: new InterposeAccessPolicy(null, 0));
        using var c2 = new TcpClient();
        c2.Connect(IPAddress.Loopback, off.LocalEndpoint.Port);
        Assert.Null(PollFrame(off, TimeSpan.FromMilliseconds(200)));
        now2 += 10_000_000;
        Assert.False(off.TryReadFrame(out _, TimeSpan.FromMilliseconds(50)));
        Assert.Equal(0, off.IdleDrops);
    }

    [Fact]
    public void B174_the_cli_listens_on_loopback_by_default_and_refuses_an_open_listener_without_a_client_rule()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-bh1101-interpose-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string crosswalk = Path.Combine(dir, "modbus-crosswalk.json");
            new RtuPointMap
            {
                Case = "case14",
                Bindings = new List<RtuPointBinding> { new() { Address = "MB:U1:3:@100", Kind = MeasurementKind.PInjMw, Bus = 4, Scale = 0.1 } },
            }.Save(crosswalk);

            CliProcess.Result open = CliProcess.RunBuilt("interpose", "--protocol", "modbus", "--listen", "0.0.0.0:0",
                "--crosswalk", crosswalk, "--live-seconds", "1");
            Assert.True(open.ExitCode == 2, $"expected exit 2\n{open}");
            Assert.Contains("--allow-client", open.StdErr, StringComparison.Ordinal);
            Assert.Contains("--allow-any-client", open.StdErr, StringComparison.Ordinal);

            CliProcess.Result def = CliProcess.RunBuilt("interpose", "--protocol", "modbus", "--port", "0",
                "--crosswalk", crosswalk, "--live-seconds", "0.5");
            Assert.Contains("listening 127.0.0.1:0", def.StdOut, StringComparison.Ordinal);
            Assert.Contains("clients: this machine only (loopback)", def.StdOut, StringComparison.Ordinal);

            CliProcess.Result bad = CliProcess.RunBuilt("interpose", "--protocol", "modbus", "--port", "0",
                "--crosswalk", crosswalk, "--live-seconds", "0.5", "--idle-seconds", "-1");
            Assert.True(bad.ExitCode == 2, $"expected exit 2\n{bad}");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }
}
