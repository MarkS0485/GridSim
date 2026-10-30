using System;
using GridSim.Core.IO.Telemetry;
using Xunit;

namespace GridSim.Tests;

/// <summary>The IEC 60870-5-104 APCI codec: I/S/U frames round-trip through encode+decode, recovering the
/// format, the 15-bit send/receive sequence numbers, and the ASDU payload.</summary>
public class Iec104FrameTests
{
    [Fact]
    public void I_format_round_trips_with_sequence_numbers_and_asdu()
    {
        var asdu = new byte[] { 0x0D, 0x01, 0x03, 0x00, 0x01, 0x00, 0x2A }; // an arbitrary ASDU body
        var apdu = new Iec104Apdu(Iec104Format.I, SendSeq: 300, RecvSeq: 130, Asdu: asdu);

        byte[] bytes = Iec104Frame.ToBytes(apdu);
        Assert.Equal(0x68, bytes[0]);
        Assert.Equal(4 + asdu.Length, bytes[1]);

        Iec104Apdu back = Iec104Frame.Parse(bytes);
        Assert.Equal(Iec104Format.I, back.Format);
        Assert.Equal(300, back.SendSeq);
        Assert.Equal(130, back.RecvSeq);
        Assert.Equal(asdu, back.Asdu);
    }

    [Fact]
    public void S_format_carries_only_the_receive_sequence()
    {
        Iec104Apdu back = Iec104Frame.Parse(Iec104Frame.ToBytes(
            new Iec104Apdu(Iec104Format.S, SendSeq: 0, RecvSeq: 4096, Asdu: Array.Empty<byte>())));
        Assert.Equal(Iec104Format.S, back.Format);
        Assert.Equal(4096, back.RecvSeq);
        Assert.Empty(back.Asdu);
    }

    [Fact]
    public void U_format_is_unnumbered()
    {
        Iec104Apdu back = Iec104Frame.Parse(Iec104Frame.ToBytes(
            new Iec104Apdu(Iec104Format.U, 0, 0, Array.Empty<byte>())));
        Assert.Equal(Iec104Format.U, back.Format);
    }
}
