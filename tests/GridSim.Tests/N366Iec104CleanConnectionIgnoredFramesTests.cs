using GridSim.Core.Interpose.Protocols;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N366 follow-up: since the k window landed, the S-frame N(R) 3 that opens
/// Iec104ControllingStationTests.S_and_unknown_u_frames_and_non_apci_candidates_are_not_answered_and_do_not_count_as_i_frames
/// acknowledges I-frames this station never sent and closes the connection, so the frames after it in that test now meet
/// a closed connection and test less than they did. These put each of those frames to a CLEAN, open connection: an
/// outstation's own STARTDT / STOPDT act and an unknown U function are ignored without closing, and a candidate that is
/// not an APCI (too short, wrong start octet) is dropped without touching any state. The existing test is unchanged.
/// </summary>
public class N366Iec104CleanConnectionIgnoredFramesTests
{
    private static readonly byte[] StartDtAct = { 0x68, 0x04, 0x07, 0x00, 0x00, 0x00 };
    private static readonly byte[] StartDtCon = { 0x68, 0x04, 0x0B, 0x00, 0x00, 0x00 };

    public static TheoryData<string, byte[]> IgnoredFrames => new()
    {
        { "STARTDT act from the outstation (not its role)", new byte[] { 0x68, 0x04, 0x07, 0x00, 0x00, 0x00 } },
        { "STOPDT act from the outstation (not its role)", new byte[] { 0x68, 0x04, 0x13, 0x00, 0x00, 0x00 } },
        { "an unknown U function (bits 2..7 all set)", new byte[] { 0x68, 0x04, 0xFF, 0x00, 0x00, 0x00 } },
        { "shorter than an APCI", new byte[] { 0x68, 0x02, 0x07, 0x00 } },
        { "empty", Array.Empty<byte>() },
        { "no start octet", new byte[] { 0x55, 0x04, 0x00, 0, 0, 0 } },
    };

    private static Iec104ControllingStation Started()
    {
        var st = new Iec104ControllingStation();
        Assert.Equal(StartDtAct, st.Connected(0));
        Assert.Null(st.FrameReceived(StartDtCon, 1));
        Assert.True(st.DataTransferStarted);
        return st;
    }

    [Theory]
    [MemberData(nameof(IgnoredFrames))]
    public void N366_an_ignored_frame_on_a_clean_connection_is_not_answered_and_keeps_the_connection_open(string what, byte[] frame)
    {
        Iec104ControllingStation st = Started();
        Assert.Null(st.FrameReceived(frame, 2));
        Assert.False(st.CloseRequested, what);
        Assert.Null(st.CloseReason);
        Assert.True(st.DataTransferStarted, what);
        Assert.False(st.DataTransferStopped, what);
        Assert.Equal(0, st.ReceiveSequence);
        Assert.Equal(0, st.UnacknowledgedIFrames);
        Assert.Equal(0, st.AcknowledgementErrors);
        Assert.Equal(0, st.SequenceErrors);
        Assert.Equal(0, st.AcknowledgementsSent);
    }

    [Fact]
    public void N366_all_ignored_frames_in_a_row_leave_the_connection_open_and_counting_i_frames_from_zero()
    {
        Iec104ControllingStation st = Started();
        long t = 2;
        foreach (object[] row in IgnoredFrames)
            Assert.Null(st.FrameReceived((byte[])row[1], t++));
        Assert.False(st.CloseRequested);

        // The next I-frame is still N(S) 0, counted as the first.
        Assert.Null(st.FrameReceived(Iec104TestFrames.IFrame(0, new byte[] { 13, 0x01, 0x03, 0x00, 0x01, 0x00 }), t));
        Assert.False(st.CloseRequested);
        Assert.Equal(1, st.ReceiveSequence);
        Assert.Equal(1, st.UnacknowledgedIFrames);
    }
}
