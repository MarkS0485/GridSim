using GridSim.Core.Interpose.Protocols;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N366: the IEC 104 controlling station's k window and STOPDT. The station sends no I-frames, so V(S) is always 0 and
/// every N(R) the outstation sends must be 0; any other N(R) acknowledges frames never sent and closes the connection.
/// STOPDT act acknowledges pending I-frames first, is supervised by t1, and after STOPDT con an I-frame closes the link.
/// Pure frame-and-clock tests, no socket.
/// </summary>
public class N366Iec104KWindowAndStopDtTests
{
    private static readonly byte[] StartDtAct = { 0x68, 0x04, 0x07, 0x00, 0x00, 0x00 };
    private static readonly byte[] StartDtCon = { 0x68, 0x04, 0x0B, 0x00, 0x00, 0x00 };
    private static readonly byte[] StopDtAct = { 0x68, 0x04, 0x13, 0x00, 0x00, 0x00 };
    private static readonly byte[] StopDtCon = { 0x68, 0x04, 0x23, 0x00, 0x00, 0x00 };

    private static byte[] SFrame(int nr) => new byte[] { 0x68, 0x04, 0x01, 0x00, (byte)((nr << 1) & 0xFE), (byte)((nr >> 7) & 0xFF) };

    /// <summary>An I-frame with send sequence <paramref name="ns"/> and receive sequence <paramref name="nr"/>.</summary>
    private static byte[] IFrame(int ns, int nr = 0)
    {
        byte[] f = Iec104TestFrames.IFrame(ns, new byte[] { 13, 0x01, 0x03, 0x00, 0x01, 0x00 });
        f[4] = (byte)((nr << 1) & 0xFE);
        f[5] = (byte)((nr >> 7) & 0xFF);
        return f;
    }

    private static Iec104ControllingStation Started()
    {
        var st = new Iec104ControllingStation();
        Assert.Equal(StartDtAct, st.Connected(0));
        Assert.Null(st.FrameReceived(StartDtCon, 1));
        Assert.True(st.DataTransferStarted);
        return st;
    }

    // ---- k window -------------------------------------------------------------------------------

    [Fact]
    public void N366_w_is_no_more_than_two_thirds_of_k()
    {
        Assert.Equal(12, Iec104ControllingStation.K);
        Assert.True(3 * Iec104ControllingStation.W <= 2 * Iec104ControllingStation.K);
        Assert.Equal(0, new Iec104ControllingStation().SendSequence);
    }

    [Fact]
    public void N366_an_s_frame_with_n_r_zero_is_accepted()
    {
        Iec104ControllingStation st = Started();
        Assert.Null(st.FrameReceived(SFrame(0), 2));
        Assert.False(st.CloseRequested);
        Assert.Equal(0, st.AcknowledgementErrors);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(32767)]
    public void N366_an_s_frame_acknowledging_i_frames_never_sent_closes_the_connection(int nr)
    {
        Iec104ControllingStation st = Started();
        Assert.Null(st.FrameReceived(SFrame(nr), 2));
        Assert.True(st.CloseRequested);
        Assert.Equal(1, st.AcknowledgementErrors);
        Assert.Contains($"S-frame N(R) {nr} acknowledges I-frames this station never sent", st.CloseReason);
        Assert.Null(st.Poll(100_000));   // nothing more is sent once a close is requested
    }

    [Fact]
    public void N366_an_i_frame_with_a_non_zero_n_r_is_not_counted_and_closes_the_connection()
    {
        Iec104ControllingStation st = Started();
        Assert.Null(st.FrameReceived(IFrame(0, nr: 0), 2));
        Assert.Equal(1, st.ReceiveSequence);
        Assert.Null(st.FrameReceived(IFrame(1, nr: 5), 3));
        Assert.True(st.CloseRequested);
        Assert.Equal(1, st.ReceiveSequence);          // not counted
        Assert.Equal(1, st.UnacknowledgedIFrames);
        Assert.Equal(0, st.SequenceErrors);           // the N(S) was right; the N(R) was not
        Assert.Contains("I-frame N(R) 5", st.CloseReason);
    }

    [Fact]
    public void N366_the_outstation_is_acknowledged_well_inside_its_k_window()
    {
        // Twenty in-order I-frames: an S-frame after every w = 8, so the outstation never has more than 8 < k outstanding.
        Iec104ControllingStation st = Started();
        int maxOutstanding = 0;
        for (int i = 0; i < 20; i++)
        {
            st.FrameReceived(IFrame(i), 2 + i);
            maxOutstanding = Math.Max(maxOutstanding, st.UnacknowledgedIFrames);
        }
        Assert.True(maxOutstanding < Iec104ControllingStation.K, $"{maxOutstanding} unacknowledged, k {Iec104ControllingStation.K}");
        Assert.False(st.CloseRequested);
    }

    // ---- STOPDT ---------------------------------------------------------------------------------

    [Fact]
    public void N366_stopdt_acknowledges_pending_i_frames_then_sends_stopdt_act()
    {
        Iec104ControllingStation st = Started();
        st.FrameReceived(IFrame(0), 2);
        st.FrameReceived(IFrame(1), 3);
        Assert.Equal(SFrame(2).Concat(StopDtAct).ToArray(), st.StopDataTransfer(4));
        Assert.Equal(0, st.UnacknowledgedIFrames);
    }

    [Fact]
    public void N366_stopdt_with_nothing_pending_sends_only_stopdt_act()
    {
        Iec104ControllingStation st = Started();
        Assert.Equal(StopDtAct, st.StopDataTransfer(2));
    }

    [Fact]
    public void N366_stopdt_con_stops_transfer_and_frames_before_it_are_still_counted()
    {
        Iec104ControllingStation st = Started();
        st.StopDataTransfer(2);
        Assert.Null(st.FrameReceived(IFrame(0), 3));   // queued data between act and con: counted
        Assert.Equal(1, st.ReceiveSequence);
        Assert.Null(st.FrameReceived(StopDtCon, 4));
        Assert.True(st.DataTransferStopped);
        Assert.False(st.DataTransferStarted);
        Assert.False(st.CloseRequested);
    }

    [Fact]
    public void N366_an_i_frame_after_stopdt_con_closes_the_connection()
    {
        Iec104ControllingStation st = Started();
        st.StopDataTransfer(2);
        st.FrameReceived(StopDtCon, 3);
        Assert.Null(st.FrameReceived(IFrame(0), 4));
        Assert.True(st.CloseRequested);
        Assert.Contains("after STOPDT con", st.CloseReason);
        Assert.Equal(0, st.ReceiveSequence);
    }

    [Fact]
    public void N366_stopdt_act_not_confirmed_within_t1_closes_the_connection()
    {
        Iec104ControllingStation st = Started();
        st.StopDataTransfer(1_000);
        Assert.Null(st.Poll(1_000 + Iec104ControllingStation.T1Ms - 1));
        Assert.False(st.CloseRequested);
        Assert.Null(st.Poll(1_000 + Iec104ControllingStation.T1Ms));
        Assert.True(st.CloseRequested);
        Assert.Contains("STOPDT act not confirmed within t1", st.CloseReason);
    }

    [Fact]
    public void N366_an_unsolicited_stopdt_con_is_ignored()
    {
        Iec104ControllingStation st = Started();
        Assert.Null(st.FrameReceived(StopDtCon, 2));
        Assert.False(st.DataTransferStopped);
        Assert.True(st.DataTransferStarted);
        Assert.Null(st.FrameReceived(IFrame(0), 3));
        Assert.Equal(1, st.ReceiveSequence);
    }

    [Fact]
    public void N366_startdt_restarts_transfer_after_stopdt()
    {
        Iec104ControllingStation st = Started();
        st.StopDataTransfer(2);
        st.FrameReceived(StopDtCon, 3);
        Assert.Equal(StartDtAct, st.StartDataTransfer(4));
        Assert.Null(st.FrameReceived(StartDtCon, 5));
        Assert.True(st.DataTransferStarted);
        Assert.False(st.DataTransferStopped);
        Assert.Null(st.FrameReceived(IFrame(0), 6));
        Assert.Equal(1, st.ReceiveSequence);
        Assert.False(st.CloseRequested);
    }

    [Fact]
    public void N366_caller_misuse_of_stop_and_start_fails_loud()
    {
        Iec104ControllingStation st = Started();
        Assert.Throws<InvalidOperationException>(() => st.StartDataTransfer(2));   // not stopped
        st.StopDataTransfer(3);
        Assert.Throws<InvalidOperationException>(() => st.StopDataTransfer(4));    // already outstanding
        Assert.Throws<InvalidOperationException>(() => new Iec104ControllingStation().StopDataTransfer(0));   // no connection
    }

    [Fact]
    public void N366_a_new_connection_clears_the_stop_state()
    {
        Iec104ControllingStation st = Started();
        st.StopDataTransfer(2);
        st.FrameReceived(StopDtCon, 3);
        st.FrameReceived(SFrame(4), 4);   // also an acknowledgement error, to be cleared
        st.Connected(10);
        Assert.False(st.DataTransferStopped);
        Assert.False(st.CloseRequested);
        Assert.Equal(0, st.AcknowledgementErrors);
        Assert.Null(st.Poll(10 + Iec104ControllingStation.T1Ms - 1));   // no stale STOPDT t1
    }
}
