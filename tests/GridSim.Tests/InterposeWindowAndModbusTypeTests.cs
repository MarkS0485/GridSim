using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.Estimation;
using GridSim.Core.Interpose;
using GridSim.Core.Interpose.Protocols;
using GridSim.Core.IO.Telemetry;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R032: a point repeated within a window is ONE measurement (its latest value), not N over-weighted copies.
/// R271: Modbus responses paired with their requests get absolute register addresses; differently sized
/// unpaired blocks on one unit/fc are refused rather than colliding. R272: register data types (signed and
/// 32-bit values) decode correctly.
/// </summary>
public class InterposeWindowAndModbusTypeTests
{
    private static readonly DateTime Now = new(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Historical = Now.AddHours(-200);

    private static RtuPointMap Map(params RtuPointBinding[] bindings) => new() { Case = "t", Bindings = bindings };

    // ---- R032 ---------------------------------------------------------------------------------------

    [Fact]
    public void A_point_repeated_in_a_window_becomes_one_measurement_with_its_latest_value()
    {
        var map = Map(new RtuPointBinding { Address = "IOA:4001", Kind = MeasurementKind.VmPu, Bus = 4, Sigma = 0.01 },
                      new RtuPointBinding { Address = "IOA:4002", Kind = MeasurementKind.VmPu, Bus = 5, Sigma = 0.01 });
        var points = new List<RtuPoint>();
        for (int k = 0; k < 10; k++)   // ten reports of the same key voltage
            points.Add(new RtuPoint("IOA:4001", RtuPointType.Analog, 1.00 + k * 0.001, RtuQuality.Good,
                Historical.AddSeconds(k)));
        points.Add(new RtuPoint("IOA:4002", RtuPointType.Analog, 0.98, RtuQuality.Good, Historical));

        MeasurementSet set = RtuMeasurementSetMapper.Map(points, map, Historical, utcNow: Now);

        // Before R032: 11 measurements, ten of them the same bus-4 voltage with full weight each.
        Assert.Equal(2, set.Measurements.Count);
        Measurement v4 = set.Measurements.Single(m => m.Bus == 4);
        Assert.Equal(1.009, v4.Value, 12);   // the latest-stamped sample
        Assert.Equal(0.01, v4.Sigma);        // its sigma is unchanged (no sqrt(N) shrink: it is one reading)
        Assert.Equal(4, set.Measurements[0].Bus);   // first-appearance order kept
    }

    [Fact]
    public void The_latest_device_stamp_wins_even_if_it_arrived_first()
    {
        var map = Map(new RtuPointBinding { Address = "IOA:1", Kind = MeasurementKind.PInjMw, Bus = 2, Sigma = 1 });
        var points = new List<RtuPoint>
        {
            new("IOA:1", RtuPointType.Analog, 50.0, RtuQuality.Good, Historical.AddSeconds(5)),
            new("IOA:1", RtuPointType.Analog, 40.0, RtuQuality.Good, Historical.AddSeconds(1)),   // late, older
        };
        Assert.Equal(50.0, RtuMeasurementSetMapper.Map(points, map, Historical, utcNow: Now).Measurements.Single().Value);
    }

    [Fact]
    public void Without_stamps_the_later_arrival_wins_and_an_invalid_latest_sample_drops_the_point()
    {
        var map = Map(new RtuPointBinding { Address = "IOA:1", Kind = MeasurementKind.PInjMw, Bus = 2, Sigma = 1 });
        var unstamped = new List<RtuPoint>
        {
            new("IOA:1", RtuPointType.Analog, 10.0, RtuQuality.Good, null),
            new("IOA:1", RtuPointType.Analog, 20.0, RtuQuality.Good, null),
        };
        Assert.Equal(20.0, RtuMeasurementSetMapper.Map(unstamped, map, Historical, utcNow: Now).Measurements.Single().Value);

        // The point's current state is invalid: it is dropped, not replaced by an older good reading.
        var nowInvalid = new List<RtuPoint>
        {
            new("IOA:1", RtuPointType.Analog, 10.0, RtuQuality.Good, null),
            new("IOA:1", RtuPointType.Analog, 20.0, RtuQuality.Invalid, null),
        };
        Assert.Empty(RtuMeasurementSetMapper.Map(nowInvalid, map, Historical, utcNow: Now).Measurements);
    }

    // ---- Modbus frame helpers -----------------------------------------------------------------------

    private static byte[] Response(int txn, int unit, byte fc, params ushort[] regs)
    {
        var pdu = new List<byte> { fc, (byte)(regs.Length * 2) };
        foreach (ushort r in regs) { pdu.Add((byte)(r >> 8)); pdu.Add((byte)(r & 0xFF)); }
        int length = 1 + pdu.Count;
        var f = new List<byte> { (byte)(txn >> 8), (byte)txn, 0, 0, (byte)(length >> 8), (byte)length, (byte)unit };
        f.AddRange(pdu);
        return f.ToArray();
    }

    private static byte[] Request(int txn, int unit, byte fc, int start, int qty) => new byte[]
    {
        (byte)(txn >> 8), (byte)txn, 0, 0, 0, 6, (byte)unit, fc,
        (byte)(start >> 8), (byte)start, (byte)(qty >> 8), (byte)qty,
    };

    // ---- R271 ---------------------------------------------------------------------------------------

    [Fact]
    public void A_request_is_parsed_for_its_start_and_quantity()
    {
        Assert.True(ModbusFrame.TryParseReadRequest(Request(9, 1, 3, 100, 4), out ModbusReadRequest req));
        Assert.Equal(new ModbusReadRequest(9, 1, 3, 100, 4), req);
        Assert.False(ModbusFrame.TryParseReadRequest(Response(9, 1, 3, 1, 2, 3), out _));
    }

    [Fact]
    public void Paired_responses_get_absolute_register_addresses_so_two_blocks_never_collide()
    {
        var proto = new ModbusProtocol();
        Assert.Empty(proto.Decode(Request(1, 1, 3, 0, 2), QualityFold.Default));
        Assert.Empty(proto.Decode(Request(2, 1, 3, 100, 2), QualityFold.Default));

        IReadOnlyList<RtuPoint> volts = proto.Decode(Response(1, 1, 3, 1030, 1020), QualityFold.Default);
        IReadOnlyList<RtuPoint> flows = proto.Decode(Response(2, 1, 3, 500, 600), QualityFold.Default);

        Assert.Equal(new[] { "MB:U1:3:@0", "MB:U1:3:@1" }, volts.Select(p => p.Address).ToArray());
        Assert.Equal(new[] { "MB:U1:3:@100", "MB:U1:3:@101" }, flows.Select(p => p.Address).ToArray());
    }

    [Fact]
    public void A_differently_sized_unpaired_block_on_the_same_unit_and_fc_is_refused()
    {
        // Before R271 register 0 of both blocks became 'MB:U1:3:0' and the crosswalk bound both to one measurement.
        var proto = new ModbusProtocol();
        Assert.Equal(2, proto.Decode(Response(1, 1, 3, 1030, 1020), QualityFold.Default).Count);
        var ex = Assert.Throws<InvalidDataException>(() => proto.Decode(Response(2, 1, 3, 5, 6, 7), QualityFold.Default));
        Assert.Contains("collide", ex.Message);

        // Another unit, or the same size again, is fine.
        Assert.Equal(3, proto.Decode(Response(3, 2, 3, 5, 6, 7), QualityFold.Default).Count);
        Assert.Equal(2, proto.Decode(Response(4, 1, 3, 1031, 1021), QualityFold.Default).Count);
    }

    [Fact]
    public void A_response_whose_count_contradicts_its_request_is_refused()
    {
        var proto = new ModbusProtocol();
        proto.Decode(Request(5, 1, 4, 10, 3), QualityFold.Default);
        Assert.Throws<InvalidDataException>(() => proto.Decode(Response(5, 1, 4, 1, 2), QualityFold.Default));
    }

    [Fact]
    public void The_pipeline_counts_a_refused_block_as_a_decode_error()
    {
        var map = Map(new RtuPointBinding { Address = "MB:U1:3:0", Kind = MeasurementKind.VmPu, Bus = 1, Scale = 0.001 });
        var pipe = new InterposePipeline(new ModbusProtocol(), map);
        pipe.Ingest(Response(1, 1, 3, 1030, 1020));
        pipe.Ingest(Response(2, 1, 3, 500, 600, 700));
        Assert.Equal(1, pipe.DecodeErrors);
        Assert.Equal(2, pipe.PendingPoints);   // only the first block's registers are in the window
    }

    // ---- R272 ---------------------------------------------------------------------------------------

    [Fact]
    public void A_signed_register_decodes_negative()
    {
        // -120 MW encoded as 0xFF88. Before R272 it arrived as +65,416 MW.
        var map = Map(new RtuPointBinding
        { Address = "MB:U1:3:0", Kind = MeasurementKind.PFlowMw, From = 1, To = 2, DataType = RtuRegisterType.S16 });
        var pipe = new InterposePipeline(new ModbusProtocol(), map);
        pipe.Ingest(Response(1, 1, 3, 0xFF88));
        Assert.Equal(-120.0, pipe.FlushWindow(Historical, utcNow: Now)!.Measurements.Single().Value);
    }

    [Fact]
    public void The_default_type_keeps_the_unsigned_reading()
    {
        var map = Map(new RtuPointBinding { Address = "MB:U1:3:0", Kind = MeasurementKind.PFlowMw, From = 1, To = 2 });
        var pipe = new InterposePipeline(new ModbusProtocol(), map);
        pipe.Ingest(Response(1, 1, 3, 0xFF88));
        Assert.Equal(65416.0, pipe.FlushWindow(Historical, utcNow: Now)!.Measurements.Single().Value);
    }

    [Theory]
    [InlineData(RtuRegisterType.U32, RtuWordOrder.HighWordFirst, (ushort)0x0001, (ushort)0x0002, 65538.0)]
    [InlineData(RtuRegisterType.U32, RtuWordOrder.LowWordFirst, (ushort)0x0002, (ushort)0x0001, 65538.0)]
    [InlineData(RtuRegisterType.S32, RtuWordOrder.HighWordFirst, (ushort)0xFFFF, (ushort)0xFF88, -120.0)]
    [InlineData(RtuRegisterType.F32, RtuWordOrder.HighWordFirst, (ushort)0x42F6, (ushort)0xE979, 123.456001281738)]
    public void Thirty_two_bit_values_combine_two_registers_of_the_same_response(
        RtuRegisterType type, RtuWordOrder order, ushort r0, ushort r1, double expected)
    {
        var map = Map(new RtuPointBinding
        {
            Address = "MB:U1:3:@40", Kind = MeasurementKind.PInjMw, Bus = 3, DataType = type, WordOrder = order,
        });
        var pipe = new InterposePipeline(new ModbusProtocol(), map);
        pipe.Ingest(Request(1, 1, 3, 40, 2));
        pipe.Ingest(Response(1, 1, 3, r0, r1));
        Assert.Equal(expected, pipe.FlushWindow(Historical, utcNow: Now)!.Measurements.Single().Value, 9);
    }

    [Fact]
    public void A_32_bit_binding_whose_second_register_is_not_in_the_response_fails_loud()
    {
        var map = Map(new RtuPointBinding
        { Address = "MB:U1:3:@41", Kind = MeasurementKind.PInjMw, Bus = 3, DataType = RtuRegisterType.F32 });
        var pipe = new InterposePipeline(new ModbusProtocol(), map);
        pipe.Ingest(Request(1, 1, 3, 40, 2));
        pipe.Ingest(Response(1, 1, 3, 1, 2));   // registers 40 and 41 only; 42 is missing
        var ex = Assert.Throws<InvalidDataException>(() => pipe.FlushWindow(Historical, utcNow: Now));
        Assert.Contains("MB:U1:3:@42", ex.Message);
    }

    [Fact]
    public void A_register_type_on_a_non_modbus_address_fails_at_load()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pm-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
            { "schema": "gridsim-rtu-pointmap/1", "bindings": [
              { "address": "IOA:1001", "kind": "VmPu", "bus": 4, "dataType": "S16" } ] }
            """);
            var ex = Assert.Throws<InvalidDataException>(() => RtuPointMap.Load(path));
            Assert.Contains("IOA:1001", ex.Message);
        }
        finally { File.Delete(path); }
    }
}
