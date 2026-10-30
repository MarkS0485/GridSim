using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Interpose;
using GridSim.Core.Interpose.Protocols;
using GridSim.Core.IO.Telemetry;
using Xunit;

namespace GridSim.Tests;

/// <summary>The Modbus TCP codec: a read-registers response decodes to positionally-addressed points, and a
/// request (which carries no values) decodes to none.</summary>
public class ModbusFrameTests
{
    // MBAP: txn(2), proto(2)=0, length(2), unit(1); then FC + byteCount + registers.
    private static byte[] RegisterResponse(int txn, int unit, byte fc, params ushort[] regs)
    {
        var pdu = new List<byte> { fc, (byte)(regs.Length * 2) };
        foreach (ushort r in regs) { pdu.Add((byte)(r >> 8)); pdu.Add((byte)(r & 0xFF)); }
        int length = 1 + pdu.Count; // unit + PDU
        var f = new List<byte> { (byte)(txn >> 8), (byte)txn, 0, 0, (byte)(length >> 8), (byte)length, (byte)unit };
        f.AddRange(pdu);
        return f.ToArray();
    }

    [Fact]
    public void Read_holding_registers_response_decodes_each_register()
    {
        ModbusResponse resp = ModbusFrame.Parse(RegisterResponse(7, unit: 3, fc: 3, 11000, 5023, 42));
        Assert.Equal(3, resp.UnitId);
        Assert.Equal(3, resp.Registers.Count);
        Assert.Equal(new[] { 11000, 5023, 42 }, resp.Registers.Select(r => (int)r.Value));
        Assert.Equal(new[] { 0, 1, 2 }, resp.Registers.Select(r => r.Index));
    }

    [Fact]
    public void A_request_frame_yields_no_registers()
    {
        // FC3 request: unit+fc+start(2)+qty(2) => length 6.
        byte[] req = { 0x00, 0x07, 0x00, 0x00, 0x00, 0x06, 0x03, 0x03, 0x00, 0x00, 0x00, 0x03 };
        Assert.Empty(ModbusFrame.Parse(req).Registers);
    }

    [Fact]
    public void Protocol_addresses_points_by_unit_function_and_index()
    {
        IReadOnlyList<RtuPoint> pts = new ModbusProtocol().Decode(
            RegisterResponse(1, unit: 5, fc: 4, 230, 231), QualityFold.Default);
        Assert.Equal(2, pts.Count);
        Assert.Equal("MB:U5:4:0", pts[0].Address);
        Assert.Equal(230.0, pts[0].Value);
        Assert.Equal(RtuQuality.Good, pts[0].Quality);
        Assert.Equal("MB:U5:4:1", pts[1].Address);
    }
}
