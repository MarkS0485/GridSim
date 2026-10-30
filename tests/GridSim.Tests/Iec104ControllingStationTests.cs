using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using GridSim.Cli.Interpose;
using GridSim.Core.Interpose.Protocols;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N230 (main's ruling on question 3): the IEC 60870-5-104 controlling-station link behaviour of the interposer,
/// <see cref="Iec104ControllingStation"/>, driven with a fake millisecond clock so the t2 and t3 timers are exact and
/// instant. The process-level tests (InterposeLoopbackProcessTests) cover STARTDT, TESTFR con and the w = 8
/// acknowledgement end to end; these cover the timers, the sequence arithmetic and the edge cases, and one test runs the
/// real transport over a loopback socket with the fake clock to show the t2 acknowledgement goes out on the wire.
/// </summary>
public class Iec104ControllingStationTests
{
    private static readonly byte[] StartDtAct = { 0x68, 0x04, 0x07, 0x00, 0x00, 0x00 };
    private static readonly byte[] StartDtCon = { 0x68, 0x04, 0x0B, 0x00, 0x00, 0x00 };
    private static readonly byte[] TestFrAct = { 0x68, 0x04, 0x43, 0x00, 0x00, 0x00 };
    private static readonly byte[] TestFrCon = { 0x68, 0x04, 0x83, 0x00, 0x00, 0x00 };

    private static byte[] SAck(int nr) => new byte[] { 0x68, 0x04, 0x01, 0x00, (byte)((nr << 1) & 0xFE), (byte)((nr >> 7) & 0xFF) };

    // A minimal I-frame (an ASDU of six octets is enough: the station reads only the APCI).
    private static byte[] IFrame(int ns) => Iec104TestFrames.IFrame(ns, new byte[] { 13, 0x01, 0x03, 0x00, 0x01, 0x00 });

    [Fact]
    public void The_defaults_are_the_clause_5_values()
    {
        Assert.Equal(8, Iec104ControllingStation.W);
        Assert.Equal(10_000, Iec104ControllingStation.T2Ms);
        Assert.Equal(20_000, Iec104ControllingStation.T3Ms);
        Assert.Equal(15_000, Iec104ControllingStation.T1Ms);   // GR-803
    }

    [Fact]
    public void Connect_sends_startdt_act_and_startdt_con_starts_data_transfer()
    {
        var st = new Iec104ControllingStation();
        Assert.Equal(StartDtAct, st.Connected(0));
        Assert.False(st.DataTransferStarted);
        Assert.Null(st.FrameReceived(StartDtCon, 5));
        Assert.True(st.DataTransferStarted);
    }

    [Fact]
    public void Use_before_a_connection_fails_loud()
    {
        var st = new Iec104ControllingStation();
        Assert.Throws<InvalidOperationException>(() => st.Poll(0));
        Assert.Throws<InvalidOperationException>(() => st.FrameReceived(StartDtCon, 0));
    }

    [Fact]
    public void Testfr_act_is_answered_with_testfr_con()
    {
        var st = new Iec104ControllingStation();
        st.Connected(0);
        Assert.Equal(TestFrCon, st.FrameReceived(TestFrAct, 1));
        Assert.Equal(TestFrCon, st.FrameReceived(TestFrAct, 2));
        Assert.Equal(2, st.TestFrameConfirmationsSent);
    }

    [Fact]
    public void Every_eighth_i_frame_is_acknowledged_with_nr_equal_to_the_count()
    {
        var st = new Iec104ControllingStation();
        st.Connected(0);
        var acks = new List<byte[]>();
        for (int k = 0; k < 24; k++)
            if (st.FrameReceived(IFrame(k), k) is byte[] a) acks.Add(a);
        Assert.Equal(new[] { SAck(8), SAck(16), SAck(24) }, acks);
        Assert.Equal(24, st.ReceiveSequence);
        Assert.Equal(0, st.UnacknowledgedIFrames);
        Assert.Equal(3, st.AcknowledgementsSent);
    }

    [Fact]
    public void S_and_unknown_u_frames_and_non_apci_candidates_are_not_answered_and_do_not_count_as_i_frames()
    {
        var st = new Iec104ControllingStation();
        st.Connected(0);
        Assert.Null(st.FrameReceived(SAck(3), 1));                                       // S-format
        Assert.Null(st.FrameReceived(new byte[] { 0x68, 0x04, 0x13, 0, 0, 0 }, 2));     // STOPDT act (not its role)
        Assert.Null(st.FrameReceived(new byte[] { 0x68, 0x02, 0x07, 0x00 }, 3));        // shorter than an APCI
        Assert.Null(st.FrameReceived(new byte[] { 0x55, 0x04, 0x00, 0, 0, 0 }, 4));     // no start octet
        Assert.Equal(0, st.ReceiveSequence);
        Assert.Equal(0, st.UnacknowledgedIFrames);
    }

    [Fact]
    public void T2_acknowledges_an_outstanding_i_frame_ten_seconds_after_the_first_unacknowledged_one()
    {
        var st = new Iec104ControllingStation();
        st.Connected(0);
        Assert.Null(st.FrameReceived(IFrame(0), 1_000));
        Assert.Null(st.FrameReceived(IFrame(1), 5_000));
        Assert.Null(st.Poll(10_999));                       // 9.999 s after the first: not yet
        Assert.Equal(SAck(2), st.Poll(11_000));             // exactly t2 after the first: N(R) = 2
        Assert.Null(st.Poll(11_001));                       // nothing outstanding any more
        Assert.Equal(0, st.UnacknowledgedIFrames);
    }

    [Fact]
    public void T3_sends_testfr_act_once_per_idle_period_and_any_frame_restarts_the_idle_clock()
    {
        var st = new Iec104ControllingStation();
        st.Connected(0);
        // GR-803: STARTDT is confirmed at once, as a real outstation does; without it t1 (15 s) would now close the
        // connection before t3 is reached. Received at 0, so the idle clock is unchanged.
        Assert.Null(st.FrameReceived(StartDtCon, 0));
        Assert.Null(st.Poll(19_999));
        Assert.Equal(TestFrAct, st.Poll(20_000));
        Assert.True(st.AwaitingTestFrameConfirmation);
        Assert.Null(st.Poll(20_001));                       // not resent within the next t3
        Assert.Null(st.FrameReceived(TestFrCon, 20_500));    // confirmed; the frame also restarts the idle clock
        Assert.False(st.AwaitingTestFrameConfirmation);
        Assert.Null(st.Poll(40_499));
        Assert.Equal(TestFrAct, st.Poll(40_500));           // t3 after the last frame received
        Assert.Equal(2, st.TestFramesSent);
    }

    [Fact]
    public void T2_and_t3_falling_due_together_send_the_acknowledgement_then_testfr_act()
    {
        var st = new Iec104ControllingStation();
        st.Connected(0);
        Assert.Null(st.FrameReceived(StartDtCon, 0));   // GR-803: confirmed, so t1 does not close the link before 20 s
        Assert.Null(st.FrameReceived(IFrame(0), 0));
        // At 20 s both are due: t2 since the I-frame and t3 since the last frame.
        Assert.Equal(SAck(1).Concat(TestFrAct).ToArray(), st.Poll(20_000));
    }

    [Fact]
    public void The_receive_sequence_wraps_at_32768()
    {
        var st = new Iec104ControllingStation();
        st.Connected(0);
        byte[]? last = null;
        for (int k = 0; k < 32768 + 8; k++) last = st.FrameReceived(IFrame(k % 32768), 0) ?? last;
        Assert.Equal(8, st.ReceiveSequence);
        Assert.Equal(SAck(8), last);                        // 32776 mod 32768 = 8
    }

    [Fact]
    public void A_new_connection_starts_afresh()
    {
        var st = new Iec104ControllingStation();
        st.Connected(0);
        st.FrameReceived(StartDtCon, 1);
        for (int k = 0; k < 5; k++) st.FrameReceived(IFrame(k), 2);
        Assert.Equal(StartDtAct, st.Connected(100));
        Assert.False(st.DataTransferStarted);
        Assert.Equal(0, st.ReceiveSequence);
        Assert.Equal(0, st.UnacknowledgedIFrames);
        Assert.Null(st.Poll(100 + Iec104ControllingStation.T2Ms));   // the old connection's frames are not acknowledged
    }

    [Fact]
    public void GR803_an_unconfirmed_startdt_act_closes_the_connection_after_t1()
    {
        var st = new Iec104ControllingStation();
        st.Connected(0);
        Assert.Null(st.Poll(Iec104ControllingStation.T1Ms - 1));
        Assert.False(st.CloseRequested);
        Assert.Null(st.Poll(Iec104ControllingStation.T1Ms));
        Assert.True(st.CloseRequested);
        Assert.Contains("STARTDT", st.CloseReason, StringComparison.Ordinal);
        // Nothing more is sent on the closing connection; a new connection starts afresh.
        Assert.Null(st.FrameReceived(TestFrAct, Iec104ControllingStation.T1Ms + 1));
        Assert.Equal(StartDtAct, st.Connected(100_000));
        Assert.False(st.CloseRequested);
    }

    [Fact]
    public void GR803_an_unconfirmed_testfr_act_closes_the_connection_after_t1()
    {
        var st = new Iec104ControllingStation();
        st.Connected(0);
        st.FrameReceived(StartDtCon, 0);
        Assert.Equal(TestFrAct, st.Poll(Iec104ControllingStation.T3Ms));
        Assert.Null(st.Poll(Iec104ControllingStation.T3Ms + Iec104ControllingStation.T1Ms - 1));
        Assert.False(st.CloseRequested);
        Assert.Null(st.Poll(Iec104ControllingStation.T3Ms + Iec104ControllingStation.T1Ms));
        Assert.True(st.CloseRequested);
        Assert.Contains("TESTFR", st.CloseReason, StringComparison.Ordinal);
    }

    [Fact]
    public void GR823_an_i_frame_out_of_sequence_closes_the_connection_and_is_not_counted()
    {
        var st = new Iec104ControllingStation();
        st.Connected(0);
        st.FrameReceived(StartDtCon, 0);
        Assert.Null(st.FrameReceived(IFrame(0), 1));
        Assert.Null(st.FrameReceived(IFrame(2), 2));   // N(S) 1 is missing
        Assert.True(st.CloseRequested);
        Assert.Equal(1, st.SequenceErrors);
        Assert.Equal(1, st.ReceiveSequence);           // the out-of-sequence frame is not counted
        Assert.Contains("N(S) 2", st.CloseReason, StringComparison.Ordinal);
        Assert.Null(st.Poll(1 + Iec104ControllingStation.T2Ms));   // nothing more is sent, not even the t2 acknowledgement
    }

    [Fact]
    public void GR823_a_repeated_i_frame_closes_the_connection()
    {
        var st = new Iec104ControllingStation();
        st.Connected(0);
        st.FrameReceived(StartDtCon, 0);
        st.FrameReceived(IFrame(0), 1);
        st.FrameReceived(IFrame(0), 2);   // the same N(S) again
        Assert.True(st.CloseRequested);
        Assert.Equal(1, st.SequenceErrors);
    }

    // ---- the real transport, a loopback socket, the fake clock ------------------------------------------------------

    [Fact]
    public void The_transport_sends_startdt_act_on_accept_and_the_t2_acknowledgement_on_the_wire()
    {
        long now = 0;
        using var t = new TcpDelimitedFrameTransport("127.0.0.1", 0, startByte: 0x68,
            station: new Iec104ControllingStation(), clockMs: () => now);
        using var c = new TcpClient();
        c.Connect(IPAddress.Loopback, t.LocalEndpoint.Port);
        NetworkStream s = c.GetStream();
        s.ReadTimeout = 5_000;

        // A pass of the receive loop accepts the client and sends STARTDT act (loop until it has arrived).
        DateTime end = DateTime.UtcNow.AddSeconds(5);
        while (c.Available < 6 && DateTime.UtcNow < end) _ = t.TryReadFrame(out _, TimeSpan.FromMilliseconds(50));
        Assert.Equal(StartDtAct, ReadExactly(s, 6));
        Assert.NotNull(t.Station);

        // One I-frame, received at fake time 1 s.
        now = 1_000;
        byte[] i = IFrame(0);
        s.Write(i);
        byte[]? got = null;
        end = DateTime.UtcNow.AddSeconds(5);
        while (got is null && DateTime.UtcNow < end)
            if (t.TryReadFrame(out byte[] f, TimeSpan.FromMilliseconds(50))) got = f;
        Assert.Equal(i, got);   // handed up unchanged: the data path is not altered
        Assert.Equal(0, c.Available);   // no acknowledgement yet

        // t2 later, the next pass of the loop sends S(N(R) = 1).
        now = 1_000 + Iec104ControllingStation.T2Ms;
        Assert.False(t.TryReadFrame(out _, TimeSpan.FromMilliseconds(50)));
        Assert.Equal(SAck(1), ReadExactly(s, 6));
        Assert.Equal(0, t.ClientResets);
        Assert.Equal(0, t.DiscardedBytes);
    }

    private static byte[] ReadExactly(NetworkStream s, int n)
    {
        var b = new byte[n];
        int got = 0;
        while (got < n)
        {
            int r = s.Read(b, got, n - got);
            if (r == 0) throw new System.IO.IOException("closed");
            got += r;
        }
        return b;
    }
}
