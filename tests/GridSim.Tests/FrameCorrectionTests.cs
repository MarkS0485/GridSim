using GridSim.Core.Interpose;
using GridSim.Core.Interpose.Protocols;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The interposer's frame-correction stage (GR-416: it had no unit test). For IEC 104 a candidate must be exactly one
/// APDU: start octet 0x68, a length octet of 4 to 253 and no trailing byte (GR-814). Every candidate is counted as
/// accepted or rejected.
/// </summary>
public sealed class FrameCorrectionTests
{
    // A U-format STARTDT act APDU: 68 04 07 00 00 00 (length 4).
    private static byte[] StartDt() => new byte[] { 0x68, 0x04, 0x07, 0x00, 0x00, 0x00 };

    [Fact]
    public void An_exact_iec104_apdu_is_accepted_and_counted()
    {
        FrameCorrection fc = FrameCorrection.ForProtocol(new Iec104Protocol());
        Assert.True(fc.Validate(StartDt()).Ok);
        Assert.Equal(1, fc.Accepted);
        Assert.Equal(0, fc.Rejected);
    }

    [Fact]
    public void A_buffer_with_bytes_after_the_apdu_is_rejected()
    {
        // Two APDUs in one buffer used to pass, and the second was lost without being counted.
        byte[] two = new byte[12];
        StartDt().CopyTo(two, 0);
        StartDt().CopyTo(two, 6);
        FrameCorrection fc = FrameCorrection.ForProtocol(new Iec104Protocol());
        FrameCorrectionResult r = fc.Validate(two);
        Assert.False(r.Ok);
        Assert.NotNull(r.Reason);
        Assert.Equal(1, fc.Rejected);
    }

    [Theory]
    [InlineData(254)]
    [InlineData(255)]
    [InlineData(3)]
    public void A_length_octet_outside_4_to_253_is_rejected(int lengthOctet)
    {
        var f = new byte[lengthOctet + 2];
        f[0] = 0x68;
        f[1] = (byte)lengthOctet;
        Assert.False(FrameCorrection.ForProtocol(new Iec104Protocol()).Validate(f).Ok);
    }

    [Fact]
    public void A_wrong_start_octet_or_a_short_frame_is_rejected()
    {
        byte[] bad = StartDt();
        bad[0] = 0x69;
        FrameCorrection fc = FrameCorrection.ForProtocol(new Iec104Protocol());
        Assert.False(fc.Validate(bad).Ok);
        Assert.False(fc.Validate(new byte[] { 0x68, 0x04, 0x07 }).Ok);
        Assert.Equal(2, fc.Rejected);
    }
}
