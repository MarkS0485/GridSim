using GridSim.Core.IO.Telemetry;

namespace GridSim.Core.Interpose.Protocols;

/// <summary>
/// N230 (October 2026, main's ruling R1 on question 3): the IEC 60870-5-104 CONTROLLING STATION link behaviour of the
/// interposer. <c>interpose</c> listens and the outstation (the RTU) connects to it, but in IEC 104 terms the
/// interposer is the controlling station, so it must drive the session: an outstation does not send monitor data until
/// the controlling station has started data transfer, and it stops sending when its window of unacknowledged I-frames
/// is full. Before this, interpose never sent a byte, so a real outstation would wait for STARTDT act for ever (or stall
/// after k frames waiting for an acknowledgement).
///
/// <para>What it does, and nothing more (minimal and deterministic; the received-data path is unchanged, every frame
/// still goes to the pipeline exactly as before):</para>
/// <list type="bullet">
/// <item>On connect: send STARTDT act. A STARTDT con from the outstation is recorded (<see cref="DataTransferStarted"/>).</item>
/// <item>TESTFR act received: answer TESTFR con at once.</item>
/// <item>I-frames received: count them in V(R) (mod 32768) and acknowledge with an S-frame carrying N(R) = V(R) at the
/// latest after <see cref="W"/> unacknowledged I-frames, or <see cref="T2Ms"/> after the first unacknowledged one.</item>
/// <item>No frame received for <see cref="T3Ms"/>: send TESTFR act (once per idle period of t3; the idle clock restarts
/// when it is sent and whenever a frame arrives).</item>
/// </list>
/// <para>GR-803 and GR-823: the station now also asks for the connection to be CLOSED (<see cref="CloseRequested"/>, with
/// <see cref="CloseReason"/>; the transport drops the client, counted as a reset) when a STARTDT act or a TESTFR act it
/// sent is not confirmed within <see cref="T1Ms"/> (t1), and when an I-frame's send sequence number N(S) is not the
/// receive sequence V(R) it expects (an I-frame lost or repeated). Before, an unanswered STARTDT act or TESTFR act stalled
/// the link silently and a sequence gap went unnoticed. A frame with the wrong N(S) is not counted in V(R) and not
/// acknowledged; the transport still hands it up (it was received), and nothing more is answered on that connection.</para>
/// <para>N366, the k window: this station sends no I-frames, so its send sequence V(S) is always 0 and it never has an
/// I-frame outstanding (0 is within any k). The window is therefore enforced from the other side: every N(R) the
/// outstation sends (in an S-frame or an I-frame) acknowledges this station's I-frames and must equal V(S) = 0. Any other
/// N(R) acknowledges frames that were never sent, a protocol error, and the station asks for the connection to be closed
/// (as for an N(S) gap). The receive side keeps the outstation inside ITS k window by acknowledging after w = 8 &lt; k = 12
/// I-frames (the standard's recommendation is w no more than two thirds of k).</para>
/// <para>N366, STOPDT: <see cref="StopDataTransfer"/> acknowledges any unacknowledged I-frames and sends STOPDT act,
/// supervised by t1 like STARTDT act. STOPDT con ends data transfer (<see cref="DataTransferStarted"/> false,
/// <see cref="DataTransferStopped"/> true). An I-frame that arrives after STOPDT con is a protocol error and closes the
/// connection; I-frames between STOPDT act and its con are still counted and acknowledged (the outstation may finish
/// sending what it had queued). <see cref="StartDataTransfer"/> restarts transfer on the same connection.</para>
/// <para>All times come in as a monotonic millisecond clock value from the caller, so the behaviour is a pure function of
/// the frames and the times (unit-testable without a socket).</para>
///
/// <para>[VERIFY] The parameter values are the defaults of IEC 60870-5-104 clause 5 as commonly cited (k = 12, w = 8,
/// t1 = 15 s, t2 = 10 s, t3 = 20 s). The standard's text is not on disk.</para>
/// </summary>
public sealed class Iec104ControllingStation
{
    /// <summary>w: acknowledge at the latest after this many received I-frames (IEC 60870-5-104 clause 5 default
    /// [VERIFY]).</summary>
    public const int W = 8;

    /// <summary>k: the most I-frames a station may have sent and not had acknowledged (IEC 60870-5-104 clause 5 default
    /// [VERIFY]). N366: this station sends none, so its own count is always 0; the constant documents the window the
    /// outstation is kept inside by acknowledging after <see cref="W"/> (w must not exceed two thirds of k).</summary>
    public const int K = 12;

    /// <summary>t2: acknowledge an unacknowledged I-frame at the latest this many milliseconds after it arrived
    /// (clause 5 default 10 s [VERIFY]).</summary>
    public const long T2Ms = 10_000;

    /// <summary>t3: send TESTFR act after this many milliseconds with no frame received (clause 5 default 20 s
    /// [VERIFY]).</summary>
    public const long T3Ms = 20_000;

    /// <summary>t1: a STARTDT act or TESTFR act not confirmed this many milliseconds after it was sent closes the
    /// connection (clause 5 default 15 s [VERIFY]). GR-803.</summary>
    public const long T1Ms = 15_000;

    // Sequence numbers are 15 bits.
    private const int SequenceModulus = 32768;

    private long _firstUnacknowledgedMs;
    private long _idleSinceMs;
    private bool _connected;
    private bool _awaitingStartDtCon;   // GR-803: STARTDT act sent, its con not yet received
    private long _startDtSentMs;
    private long _testFrSentMs;
    private bool _awaitingStopDtCon;    // N366: STOPDT act sent, its con not yet received
    private long _stopDtSentMs;

    /// <summary>V(S): this station's send sequence number. N366: it sends no I-frames, so this is always 0, and every
    /// N(R) the outstation sends must equal it.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "V(S) is per-connection protocol state; it is 0 only because this station sends no I-frames.")]
    public int SendSequence => 0;

    /// <summary>N366: true once STOPDT con was received after this station's STOPDT act (and until STARTDT con).</summary>
    public bool DataTransferStopped { get; private set; }

    /// <summary>N366: S- or I-frames whose N(R) acknowledged I-frames this station never sent, on this connection.</summary>
    public long AcknowledgementErrors { get; private set; }

    /// <summary>GR-803 / GR-823: true once the station has decided the connection must be closed (t1 expired, or an
    /// I-frame arrived out of sequence). It stays true until the next <see cref="Connected"/>; while it is true nothing
    /// more is sent.</summary>
    public bool CloseRequested { get; private set; }

    /// <summary>Why <see cref="CloseRequested"/> was set (null while it is not).</summary>
    public string? CloseReason { get; private set; }

    /// <summary>GR-823: I-frames whose N(S) was not the expected V(R), on this connection.</summary>
    public long SequenceErrors { get; private set; }

    /// <summary>V(R): the receive sequence number, the count of I-frames received on this connection mod 32768.</summary>
    public int ReceiveSequence { get; private set; }

    /// <summary>I-frames received and not yet acknowledged.</summary>
    public int UnacknowledgedIFrames { get; private set; }

    /// <summary>True once the outstation confirmed STARTDT (STARTDT con received on this connection).</summary>
    public bool DataTransferStarted { get; private set; }

    /// <summary>True between sending TESTFR act and receiving TESTFR con.</summary>
    public bool AwaitingTestFrameConfirmation { get; private set; }

    /// <summary>S-frames sent on this connection.</summary>
    public long AcknowledgementsSent { get; private set; }

    /// <summary>TESTFR act frames sent on this connection (after t3 idle).</summary>
    public long TestFramesSent { get; private set; }

    /// <summary>TESTFR con frames sent on this connection (answers to the outstation's TESTFR act).</summary>
    public long TestFrameConfirmationsSent { get; private set; }

    /// <summary>A new outstation connection: reset every counter and return STARTDT act to send.</summary>
    public byte[] Connected(long nowMs)
    {
        _connected = true;
        ReceiveSequence = 0;
        UnacknowledgedIFrames = 0;
        DataTransferStarted = false;
        AwaitingTestFrameConfirmation = false;
        AcknowledgementsSent = 0;
        TestFramesSent = 0;
        TestFrameConfirmationsSent = 0;
        _firstUnacknowledgedMs = nowMs;
        _idleSinceMs = nowMs;
        CloseRequested = false;
        CloseReason = null;
        SequenceErrors = 0;
        _awaitingStartDtCon = true;
        _startDtSentMs = nowMs;
        _awaitingStopDtCon = false;
        DataTransferStopped = false;
        AcknowledgementErrors = 0;
        return UFrame(Iec104UFunction.StartDtAct);
    }

    /// <summary>
    /// N366: stop data transfer on this connection. Any unacknowledged I-frames are acknowledged first (an S-frame with
    /// N(R) = V(R)), then STOPDT act is sent; its con is supervised by t1 in <see cref="Poll"/>. Returns the bytes to
    /// send, or null when the connection is already being closed. Calling it while a STOPDT act is already outstanding
    /// is a caller bug and throws.
    /// </summary>
    public byte[]? StopDataTransfer(long nowMs)
    {
        RequireConnected();
        if (CloseRequested) return null;
        if (_awaitingStopDtCon)
            throw new InvalidOperationException("IEC 104 controlling station: STOPDT act is already outstanding.");
        byte[]? ack = UnacknowledgedIFrames > 0 ? Acknowledge() : null;
        _awaitingStopDtCon = true;
        _stopDtSentMs = nowMs;
        byte[] stop = UFrame(Iec104UFunction.StopDtAct);
        return ack is null ? stop : ack.Concat(stop).ToArray();
    }

    /// <summary>N366: restart data transfer after a STOPDT (sends STARTDT act, supervised by t1 as on connect). Calling it
    /// while transfer is not stopped is a caller bug and throws.</summary>
    public byte[]? StartDataTransfer(long nowMs)
    {
        RequireConnected();
        if (CloseRequested) return null;
        if (!DataTransferStopped)
            throw new InvalidOperationException(
                "IEC 104 controlling station: data transfer is not stopped (no STOPDT con received).");
        _awaitingStartDtCon = true;
        _startDtSentMs = nowMs;
        return UFrame(Iec104UFunction.StartDtAct);
    }

    /// <summary>
    /// One frame received from the outstation (as the transport delimited it). Returns the bytes to send in answer, or
    /// null for none. A candidate that is not an APCI (shorter than 6 octets or not starting 0x68) is ignored here: the
    /// pipeline's frame correction rejects and counts it.
    /// </summary>
    public byte[]? FrameReceived(ReadOnlySpan<byte> frame, long nowMs)
    {
        RequireConnected();
        if (CloseRequested) return null;   // GR-803 / GR-823: the connection is being closed; answer nothing more
        if (frame.Length < 6 || frame[0] != 0x68) return null;
        _idleSinceMs = nowMs;   // any frame ends the idle period (t3)
        byte c1 = frame[2];

        if ((c1 & 0x01) == 0)
        {
            // GR-823: N(S) is the 15-bit send sequence number in control octets 1 and 2 (shifted left one). It must be
            // V(R), the count of I-frames received so far; anything else means an I-frame was lost or repeated.
            int ns = (frame[2] >> 1) | (frame[3] << 7);
            // N366: after STOPDT con the outstation must send no more I-frames.
            if (DataTransferStopped)
            {
                RequestClose(System.FormattableString.Invariant(
                    $"I-frame N(S) {ns} received after STOPDT con (data transfer is stopped)"));
                return null;
            }
            // N366, k window: an I-frame's N(R) (control octets 3 and 4) acknowledges this station's I-frames.
            if (!AcknowledgesOnlyWhatWasSent(frame, "I-frame")) return null;
            if (ns != ReceiveSequence)
            {
                SequenceErrors++;
                RequestClose(System.FormattableString.Invariant(
                    $"I-frame N(S) {ns} received where V(R) {ReceiveSequence} was expected (an I-frame was lost or repeated)"));
                return null;
            }
            // I-format: count it in V(R); acknowledge once w are outstanding.
            ReceiveSequence = (ReceiveSequence + 1) % SequenceModulus;
            if (UnacknowledgedIFrames == 0) _firstUnacknowledgedMs = nowMs;
            UnacknowledgedIFrames++;
            return UnacknowledgedIFrames >= W ? Acknowledge() : null;
        }

        // S-format acknowledges I-frames sent by this station. N366: it sends none, so N(R) must be V(S) = 0.
        if ((c1 & 0x03) == 0x01)
        {
            AcknowledgesOnlyWhatWasSent(frame, "S-frame");
            return null;
        }

        // U-format.
        switch (c1)
        {
            case Iec104UFunction.StartDtCon:
                DataTransferStarted = true;
                DataTransferStopped = false;   // N366: a restart after STOPDT
                _awaitingStartDtCon = false;   // GR-803
                return null;
            case Iec104UFunction.TestFrAct:
                TestFrameConfirmationsSent++;
                return UFrame(Iec104UFunction.TestFrCon);
            case Iec104UFunction.TestFrCon:
                AwaitingTestFrameConfirmation = false;
                return null;
            case Iec104UFunction.StopDtCon:
                // N366: only an answer to this station's STOPDT act stops transfer; an unsolicited con is ignored (as an
                // outstation's own STARTDT / STOPDT act is), so a stray frame cannot silence the link.
                if (_awaitingStopDtCon)
                {
                    _awaitingStopDtCon = false;
                    DataTransferStopped = true;
                    DataTransferStarted = false;
                }
                return null;
            default:
                return null;   // STARTDT act / STOPDT from an outstation is not its role; ignored, still counted upstream
        }
    }

    /// <summary>
    /// The timers, called on every pass of the receive loop: an S-frame when t2 has run since the first unacknowledged
    /// I-frame, and TESTFR act when t3 has run with no frame received. Returns the bytes to send, or null for none.
    /// </summary>
    public byte[]? Poll(long nowMs)
    {
        RequireConnected();
        if (CloseRequested) return null;
        // GR-803: t1 supervision of the two U-frames this station sends and expects confirmed.
        if (_awaitingStartDtCon && nowMs - _startDtSentMs >= T1Ms)
        {
            RequestClose(System.FormattableString.Invariant($"STARTDT act not confirmed within t1 ({T1Ms} ms)"));
            return null;
        }
        if (AwaitingTestFrameConfirmation && nowMs - _testFrSentMs >= T1Ms)
        {
            RequestClose(System.FormattableString.Invariant($"TESTFR act not confirmed within t1 ({T1Ms} ms)"));
            return null;
        }
        if (_awaitingStopDtCon && nowMs - _stopDtSentMs >= T1Ms)   // N366
        {
            RequestClose(System.FormattableString.Invariant($"STOPDT act not confirmed within t1 ({T1Ms} ms)"));
            return null;
        }
        byte[]? ack = UnacknowledgedIFrames > 0 && nowMs - _firstUnacknowledgedMs >= T2Ms ? Acknowledge() : null;
        byte[]? test = null;
        if (nowMs - _idleSinceMs >= T3Ms)
        {
            TestFramesSent++;
            AwaitingTestFrameConfirmation = true;
            _testFrSentMs = nowMs;   // GR-803
            _idleSinceMs = nowMs;   // one TESTFR act per idle period of t3
            test = UFrame(Iec104UFunction.TestFrAct);
        }
        if (ack is null) return test;
        if (test is null) return ack;
        return ack.Concat(test).ToArray();
    }

    // An S-frame with N(R) = V(R): 68 04 | 01 00 | N(R) shifted left one, as two octets.
    private byte[] Acknowledge()
    {
        UnacknowledgedIFrames = 0;
        AcknowledgementsSent++;
        return new byte[]
        {
            0x68, 0x04, 0x01, 0x00, (byte)((ReceiveSequence << 1) & 0xFE), (byte)((ReceiveSequence >> 7) & 0xFF),
        };
    }

    // N366: N(R) is the 15-bit receive sequence in control octets 3 and 4 (shifted left one) of an S- or I-frame. It counts
    // the I-frames of this station the peer has received, and this station sends none, so it must be V(S) = 0. False (and
    // a close requested) when it is not.
    private bool AcknowledgesOnlyWhatWasSent(ReadOnlySpan<byte> frame, string kind)
    {
        int nr = (frame[4] >> 1) | (frame[5] << 7);
        if (nr == SendSequence) return true;
        AcknowledgementErrors++;
        RequestClose(System.FormattableString.Invariant(
            $"{kind} N(R) {nr} acknowledges I-frames this station never sent (V(S) {SendSequence}, k {K})"));
        return false;
    }

    private void RequestClose(string reason)
    {
        CloseRequested = true;
        CloseReason = reason;
    }

    // A U-frame: 68 04 | function 00 00 00.
    private static byte[] UFrame(byte function) => new byte[] { 0x68, 0x04, function, 0x00, 0x00, 0x00 };

    private void RequireConnected()
    {
        if (!_connected)
            throw new InvalidOperationException("IEC 104 controlling station: no connection (call Connected first).");
    }
}
