using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using GridSim.Core.Synchrophasor;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// CF5 (N340): the live IEEE C37.118.2 transport (TCP and UDP) into the recorded pmu-ingest path. Every test runs a
/// simulated PMU in-process on the loopback interface; nothing leaves the machine. Frames are built by Doc15Frames
/// (FLOAT rectangular phasors), stamped in 2023 so the horizon rule passes them.
/// </summary>
public class Cf5LiveC37118Tests
{
    private static readonly Doc15Frames.Pmu Pmu7 = new(7, "STN_A", new[] { Doc15Frames.Voltage, Doc15Frames.Current });
    private const uint Soc = 1_700_000_000;   // 2023-11-14, well before the horizon

    private static byte[] Config() => Doc15Frames.Config(1_000_000, Soc, Pmu7);

    private static byte[] Data(int k) => Doc15Frames.Data(Soc + 1, (uint)(20_000 * k), new[] { (1.0f, 0.01f * k), (0.5f, -0.2f) });

    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(20);

    // A simulated PMU: accept one client, read its two COMMAND frames (send CONFIG-2, transmission on), then write the
    // given chunks (each written separately, so the receiver must reassemble frames split across reads).
    private static (int Port, Task<List<ushort>> Pmu) StartTcpPmu(IEnumerable<byte[]> chunks, bool waitForStop = true)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        List<byte[]> toSend = chunks.ToList();
        Task<List<ushort>> pmu = Task.Run(() =>
        {
            var commands = new List<ushort>();
            try
            {
                using TcpClient c = listener.AcceptTcpClient();
                using NetworkStream s = c.GetStream();
                s.ReadTimeout = 20_000;
                for (int k = 0; k < 2; k++) commands.Add(C37118Command.Parse(ReadExactly(s, C37118Command.FrameSize)));
                foreach (byte[] chunk in toSend) { s.Write(chunk); s.Flush(); }
                if (waitForStop)
                {
                    try { commands.Add(C37118Command.Parse(ReadExactly(s, C37118Command.FrameSize))); }
                    catch (IOException) { /* the receiver may close first; the stop command is best effort */ }
                }
            }
            finally { listener.Stop(); }
            return commands;
        });
        return (port, pmu);
    }

    private static byte[] ReadExactly(NetworkStream s, int n)
    {
        var b = new byte[n];
        int got = 0;
        while (got < n)
        {
            int r = s.Read(b, got, n - got);
            if (r == 0) throw new IOException("closed");
            got += r;
        }
        return b;
    }

    // Split a byte run into chunks at awkward places (inside headers and bodies).
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

    [Fact]
    public async Task Tcp_receive_sends_the_commands_reassembles_fragments_and_returns_the_frames_exactly()
    {
        byte[] expected = Config().Concat(Data(0)).Concat(Data(1)).Concat(Data(2)).ToArray();
        (int port, Task<List<ushort>> pmu) = StartTcpPmu(Fragments(expected));
        C37118LiveCapture cap = C37118LiveReceiver.ReceiveTcp("127.0.0.1", port, 7, new C37118LiveOptions(3, Generous));
        Assert.Equal(expected, cap.Bytes);
        Assert.Equal(4, cap.Frames);
        Assert.Equal(3, cap.DataFrames);
        Assert.Equal(0, cap.CrcFailures);
        Assert.False(cap.TimedOut);
        List<ushort> cmds = await pmu;
        Assert.Equal(C37118Command.SendConfig2, cmds[0]);
        Assert.Equal(C37118Command.TurnOnTransmission, cmds[1]);
        if (cmds.Count > 2) Assert.Equal(C37118Command.TurnOffTransmission, cmds[2]);
        // The capture decodes through the recorded path's own decoder.
        C37118ConfigFrame cfg = C37118ConfigParser.Parse(Config());
        C37118StreamDataFrame d = C37118Decoder.DecodeData(Data(1), cfg);
        Assert.Equal(FrameDecodeStatus.Ok, d.Status);
    }

    [Fact]
    public void Tcp_receive_stops_at_the_data_frames_asked_for()
    {
        byte[] all = Config().Concat(Data(0)).Concat(Data(1)).Concat(Data(2)).ToArray();
        (int port, _) = StartTcpPmu(new[] { all });
        C37118LiveCapture cap = C37118LiveReceiver.ReceiveTcp("127.0.0.1", port, 7, new C37118LiveOptions(2, Generous));
        Assert.Equal(2, cap.DataFrames);
        Assert.Equal(Config().Concat(Data(0)).Concat(Data(1)).ToArray(), cap.Bytes);
    }

    [Fact]
    public void Tcp_receive_refuses_a_stream_that_does_not_frame_and_names_the_offset()
    {
        byte[] junk = new byte[] { 0x55 }.Concat(Config()).ToArray();
        (int port, _) = StartTcpPmu(new[] { junk }, waitForStop: false);
        var ex = Assert.Throws<InvalidDataException>(() => C37118LiveReceiver.ReceiveTcp("127.0.0.1", port, 7, new C37118LiveOptions(1, Generous)));
        Assert.Contains("stream byte 0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tcp_receive_counts_a_bad_crc_frame_and_keeps_it_for_the_pipeline_to_count()
    {
        byte[] bad = Data(1);
        bad[20] ^= 0xFF;
        (int port, _) = StartTcpPmu(new[] { Config(), Data(0), bad });
        C37118LiveCapture cap = C37118LiveReceiver.ReceiveTcp("127.0.0.1", port, 7, new C37118LiveOptions(2, Generous));
        Assert.Equal(1, cap.CrcFailures);
        Assert.Equal(Config().Concat(Data(0)).Concat(bad).ToArray(), cap.Bytes);
    }

    [Fact]
    public void Tcp_receive_that_runs_out_of_time_says_so()
    {
        (int port, _) = StartTcpPmu(new[] { Config() }, waitForStop: true);
        C37118LiveCapture cap = C37118LiveReceiver.ReceiveTcp("127.0.0.1", port, 7, new C37118LiveOptions(3, TimeSpan.FromSeconds(1)));
        Assert.True(cap.TimedOut);
        Assert.Equal(0, cap.DataFrames);
        Assert.Equal(Config(), cap.Bytes);
    }

    [Fact]
    public void Tcp_receive_with_no_device_fails_loud()
    {
        // Bind a port, then close it, so nothing listens there.
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        Assert.ThrowsAny<IOException>(() => C37118LiveReceiver.ReceiveTcp("127.0.0.1", port, 7, new C37118LiveOptions(1, TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public async Task Udp_receive_takes_one_frame_per_datagram_and_drops_and_counts_a_datagram_that_is_not_one_frame()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        Task? sender = null;
        C37118LiveCapture cap = C37118LiveReceiver.ReceiveUdpOn(udp, new C37118LiveOptions(2, Generous), ep =>
        {
            sender = Task.Run(() =>
            {
                using var tx = new UdpClient();
                tx.Send(Config(), ep);
                tx.Send(Data(0).Concat(Data(1)).ToArray(), ep);   // two frames in one datagram: not one frame
                tx.Send(Data(2), ep);
                tx.Send(Data(3), ep);
            });
        });
        await sender!;
        Assert.Equal(1, cap.MalformedDatagrams);
        Assert.Equal(2, cap.DataFrames);
        Assert.Equal(3, cap.Frames);
        Assert.Equal(Config().Concat(Data(2)).Concat(Data(3)).ToArray(), cap.Bytes);
    }

    [Fact]
    public void The_stream_framer_reassembles_and_refuses_bad_framing()
    {
        byte[] all = Config().Concat(Data(0)).ToArray();
        var f = new C37118StreamFramer();
        var got = new List<byte[]>();
        foreach (byte[] chunk in Fragments(all))
        {
            f.Append(chunk);
            while (f.TryTakeFrame(out byte[] frame)) got.Add(frame);
        }
        Assert.Equal(2, got.Count);
        Assert.Equal(Config(), got[0]);
        Assert.Equal(Data(0), got[1]);
        Assert.Equal(0, f.Pending);

        var tooSmall = new C37118StreamFramer();
        tooSmall.Append(new byte[] { 0xAA, 0x01, 0x00, 0x0F });
        Assert.Throws<InvalidDataException>(() => tooSmall.TryTakeFrame(out _));
        var noSync = new C37118StreamFramer();
        noSync.Append(new byte[] { 0x00 });
        Assert.Throws<InvalidDataException>(() => noSync.TryTakeFrame(out _));
    }

    [Fact]
    public void Command_frames_round_trip_and_refuse_bad_input()
    {
        byte[] cmd = C37118Command.Build(7, C37118Command.SendConfig2, new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc));
        Assert.Equal(C37118Command.FrameSize, cmd.Length);
        Assert.Equal(C37118FrameType.Command, C37118CommonHeader.Parse(cmd).FrameType);
        Assert.Equal(7, C37118CommonHeader.Parse(cmd).IdCode);
        Assert.Equal(Soc, C37118CommonHeader.Parse(cmd).Soc);
        Assert.Equal(C37118Command.SendConfig2, C37118Command.Parse(cmd));
        Assert.Throws<ArgumentOutOfRangeException>(() => C37118Command.Build(70000, C37118Command.SendConfig2, DateTime.UtcNow));
        cmd[15] ^= 0x01;
        Assert.Throws<InvalidDataException>(() => C37118Command.Parse(cmd));
        Assert.Throws<InvalidDataException>(() => C37118Command.Parse(Config()));
    }

    [Theory]
    [InlineData("tcp://10.0.0.5:4712", true, "10.0.0.5", 4712)]
    [InlineData("udp://:4713", false, "0.0.0.0", 4713)]
    [InlineData("UDP://127.0.0.1:4713", false, "127.0.0.1", 4713)]
    public void Live_addresses_parse(string address, bool tcp, string host, int port)
        => Assert.Equal((tcp, host, port), C37118LiveReceiver.ParseAddress(address));

    [Theory]
    [InlineData("4712")]
    [InlineData("http://x:1")]
    [InlineData("tcp://:4712")]
    [InlineData("tcp://host")]
    [InlineData("tcp://host:0")]
    [InlineData("udp://:70000")]
    public void Bad_live_addresses_are_refused(string address)
        => Assert.Throws<ArgumentException>(() => C37118LiveReceiver.ParseAddress(address));

    [Fact]
    public void Live_options_refuse_a_receive_that_could_not_end_or_start()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new C37118LiveOptions(0, Generous).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new C37118LiveOptions(1, TimeSpan.Zero).Validate());
    }

    // ------------------------------------------------------------------ the CLI

    [Fact]
    public void Pmu_ingest_live_over_tcp_runs_the_recorded_pipeline_on_what_arrived()
    {
        byte[] all = Config().Concat(Data(0)).Concat(Data(1)).Concat(Data(2)).ToArray();
        (int port, _) = StartTcpPmu(new[] { all });
        CliProcess.Result r = CliProcess.RunBuilt("pmu-ingest", "--live", $"tcp://127.0.0.1:{port}", "--live-frames", "3", "--live-seconds", "20", "--live-idcode", "7");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("live: 4 frame(s) received from tcp://127.0.0.1:", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("4 frame(s): 1 CONFIG (0 bad CRC), 3 DATA (3 decoded, 0 bad CRC, 0 malformed, 0 before any CONFIG), 0 other", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Pmu_ingest_live_flags_are_checked()
    {
        CliProcess.Result alone = CliProcess.RunBuilt("pmu-ingest", "capture.bin", "--live-frames", "3");
        Assert.True(alone.ExitCode == 2, alone.ToString());
        CliProcess.Result both = CliProcess.RunBuilt("pmu-ingest", "capture.bin", "--live", "tcp://127.0.0.1:4712", "--live-frames", "3");
        Assert.True(both.ExitCode == 2, both.ToString());
        CliProcess.Result noFrames = CliProcess.RunBuilt("pmu-ingest", "--live", "tcp://127.0.0.1:4712");
        Assert.True(noFrames.ExitCode == 2, noFrames.ToString());
        CliProcess.Result badAddr = CliProcess.RunBuilt("pmu-ingest", "--live", "serial://COM1", "--live-frames", "3");
        Assert.True(badAddr.ExitCode == 2, badAddr.ToString());
        CliProcess.Result udpId = CliProcess.RunBuilt("pmu-ingest", "--live", "udp://:4713", "--live-frames", "3", "--live-idcode", "7");
        Assert.True(udpId.ExitCode == 2, udpId.ToString());
    }
}
