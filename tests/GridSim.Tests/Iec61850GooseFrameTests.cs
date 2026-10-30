using System;
using GridSim.Core.IO.Telemetry;
using Xunit;

namespace GridSim.Tests;

/// <summary>The IEC 61850 GOOSE header codec: EtherType/APPID/length round-trip with the raw goosePdu, and a
/// wrong EtherType is rejected.</summary>
public class Iec61850GooseFrameTests
{
    [Fact]
    public void Goose_header_round_trips_appid_and_pdu()
    {
        var pdu = new byte[] { 0x61, 0x0A, 0x80, 0x03, 0x41, 0x42, 0x43, 0x81, 0x01, 0x05 }; // arbitrary BER-ish bytes
        var header = new GooseHeader(AppId: 0x3001, Pdu: pdu);

        byte[] bytes = Iec61850GooseFrame.ToBytes(header);
        Assert.Equal(0x88, bytes[0]);
        Assert.Equal(0xB8, bytes[1]);

        GooseHeader back = Iec61850GooseFrame.Parse(bytes);
        Assert.Equal(0x3001, back.AppId);
        Assert.Equal(pdu, back.Pdu);
    }

    [Fact]
    public void Wrong_ethertype_is_rejected()
    {
        byte[] bytes = Iec61850GooseFrame.ToBytes(new GooseHeader(1, Array.Empty<byte>()));
        bytes[0] = 0x08; bytes[1] = 0x00; // IPv4 EtherType
        Assert.Throws<ArgumentException>(() => Iec61850GooseFrame.Parse(bytes));
    }
}
