using System.Linq;
using GridSim.Core.Interpose;
using GridSim.Core.Interpose.Protocols;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-798: an IEC 104 ASDU that is a negative confirmation (P/N set) or carries an "unknown" cause of transmission
/// (44 to 47) may echo objects, but they are not live values: they are marked invalid and counted. Only the T bit used
/// to be read, so such objects became good points.
/// </summary>
public sealed class Iec104CauseOfTransmissionTests
{
    // An I-format APDU around an ASDU: 68, length, four control octets (send and receive sequence 0), then the ASDU.
    private static byte[] Apdu(byte[] asdu)
        => new byte[] { 0x68, (byte)(4 + asdu.Length), 0x00, 0x00, 0x00, 0x00 }.Concat(asdu).ToArray();

    // Type 5 (step position), one object, IOA 10, step +5, good; the COT octet is the third byte.
    private static byte[] StepAsdu(byte cot) => new byte[] { 5, 0x01, cot, 0x00, 0x01, 0x00, 0x0A, 0x00, 0x00, 0x05, 0x00 };

    [Theory]
    [InlineData(44)]   // unknown type identification
    [InlineData(45)]   // unknown cause of transmission
    [InlineData(46)]   // unknown common address
    [InlineData(47)]   // unknown information object address
    [InlineData(0x40 | 7)]   // P/N set: a negative activation confirmation
    public void A_negative_or_unknown_cause_asdu_gives_invalid_points_and_is_counted(int cot)
    {
        var proto = new Iec104Protocol();
        RtuPoint p = Assert.Single(proto.Decode(Apdu(StepAsdu((byte)cot)), new QualityFold()));
        Assert.True(p.Quality.HasFlag(RtuQuality.Invalid));
        Assert.Equal(1, proto.RefusedCausePoints);
    }

    [Theory]
    [InlineData(3)]    // spontaneous
    [InlineData(20)]   // interrogated by station interrogation
    public void A_data_cause_still_gives_a_good_point(int cot)
    {
        var proto = new Iec104Protocol();
        RtuPoint p = Assert.Single(proto.Decode(Apdu(StepAsdu((byte)cot)), new QualityFold()));
        Assert.Equal(RtuQuality.Good, p.Quality);
        Assert.Equal(0, proto.RefusedCausePoints);
    }
}
