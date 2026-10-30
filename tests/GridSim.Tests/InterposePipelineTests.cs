using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Estimation;
using GridSim.Core.Interpose;
using GridSim.Core.Interpose.Protocols;
using GridSim.Core.IO.Telemetry;
using Xunit;

namespace GridSim.Tests;

/// <summary>End-to-end interposer flow with no socket: raw IEC-104 frame bytes -> correction -> object-model
/// decode -> window -> guarded MeasurementSet. Exercises the whole Phase-0 vertical slice in one pass.</summary>
public class InterposePipelineTests
{
    private static readonly DateTime Now = new(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Historical = Now.AddHours(-200);

    /// <summary>Build a full IEC-104 I-format APDU carrying a single M_ME_NC_1 short-float object.</summary>
    private static byte[] ShortFloatFrame(int ioa, float value, byte qds = 0x00)
    {
        var asdu = new List<byte> { 13, 0x01, 0x03, 0x00, 0x01, 0x00 }; // type13, SQ0 count1, cot3, orig0, casdu1
        asdu.Add((byte)(ioa & 0xFF)); asdu.Add((byte)((ioa >> 8) & 0xFF)); asdu.Add((byte)((ioa >> 16) & 0xFF));
        Span<byte> f = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(f, value);
        asdu.AddRange(f.ToArray());
        asdu.Add(qds);
        return Iec104Frame.ToBytes(new Iec104Apdu(Iec104Format.I, SendSeq: 1, RecvSeq: 0, Asdu: asdu.ToArray()));
    }

    private static RtuPointMap Crosswalk() => new()
    {
        Case = "case14",
        Bindings = new List<RtuPointBinding> { new() { Address = "IOA:1001", Kind = MeasurementKind.VmPu, Bus = 4, Sigma = 0.01 } },
    };

    [Fact]
    public void Protocol_decodes_a_frame_to_an_addressed_point()
    {
        IReadOnlyList<RtuPoint> points = new Iec104Protocol().Decode(ShortFloatFrame(1001, 1.03f), QualityFold.Default);
        RtuPoint p = Assert.Single(points);
        Assert.Equal("IOA:1001", p.Address);
        Assert.Equal(1.03, p.Value, 5);
        Assert.Equal(RtuQuality.Good, p.Quality);
    }

    [Fact]
    public void Frame_to_measurement_set_end_to_end()
    {
        var pipeline = new InterposePipeline(new Iec104Protocol(), Crosswalk(), caseName: "case14");

        pipeline.Ingest(ShortFloatFrame(1001, 1.02f));
        Assert.Equal(1, pipeline.PendingPoints);
        Assert.Equal(1, pipeline.Correction.Accepted);

        MeasurementSet? set = pipeline.FlushWindow(Historical, utcNow: Now);

        Assert.NotNull(set);
        Measurement m = Assert.Single(set!.Measurements);
        Assert.Equal(MeasurementKind.VmPu, m.Kind);
        Assert.Equal(4, m.Bus);
        Assert.Equal(1.02, m.Value, 5);
        Assert.Equal(0, pipeline.PendingPoints); // window cleared after flush
    }

    [Fact]
    public void Supervisory_frame_carries_no_measurement()
    {
        var pipeline = new InterposePipeline(new Iec104Protocol(), Crosswalk());
        byte[] sFrame = Iec104Frame.ToBytes(new Iec104Apdu(Iec104Format.S, 0, 5, Array.Empty<byte>()));

        Assert.Empty(pipeline.Ingest(sFrame));
        Assert.Equal(0, pipeline.PendingPoints);
        Assert.Null(pipeline.FlushWindow(Historical, utcNow: Now));
    }

    [Fact]
    public void Empty_window_flushes_to_null()
    {
        var pipeline = new InterposePipeline(new Iec104Protocol(), Crosswalk());
        Assert.Null(pipeline.FlushWindow(Historical, utcNow: Now));
    }
}
