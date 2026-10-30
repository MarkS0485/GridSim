using System;
using System.Text;
using GridSim.Core.IO.Telemetry;
using Xunit;

namespace GridSim.Tests;

/// <summary>The DNP3 data-link codec: CRC-16/DNP against the standard check vector, and a header that
/// round-trips (start, addresses, control) with CRC validation and tamper detection.</summary>
public class Dnp3FrameTests
{
    [Fact]
    public void Crc16_dnp_matches_the_standard_check_vector()
    {
        // CRC-16/DNP of "123456789" is 0xEA82.
        Assert.Equal((ushort)0xEA82, Dnp3Frame.Crc(Encoding.ASCII.GetBytes("123456789")));
    }

    [Fact]
    public void Data_link_header_round_trips_with_addresses()
    {
        var header = new Dnp3DataLinkHeader(Control: 0xC4, Destination: 1024, Source: 65533);
        byte[] bytes = Dnp3Frame.ToBytes(header);

        Assert.Equal(0x05, bytes[0]);
        Assert.Equal(0x64, bytes[1]);

        Dnp3DataLinkHeader back = Dnp3Frame.Parse(bytes);
        Assert.Equal(0xC4, back.Control);
        Assert.Equal(1024, back.Destination);
        Assert.Equal(65533, back.Source);
    }

    [Fact]
    public void A_corrupt_header_fails_the_crc()
    {
        byte[] bytes = Dnp3Frame.ToBytes(new Dnp3DataLinkHeader(0x44, 3, 4));
        bytes[4] ^= 0xFF; // corrupt the destination
        Assert.Throws<System.IO.InvalidDataException>(() => Dnp3Frame.Parse(bytes));
    }
}
