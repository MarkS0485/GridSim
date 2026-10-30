using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation;
using GridSim.Core.Synchrophasor;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R295 (Q4, 2026-10): PMU angles are UTC-referenced (IEEE C37.118.1-2011 section 4.2), so the mapper re-references
/// every angle row against a NAMED reference voltage channel before emitting it as slack-referenced, and fails loud
/// when it cannot: no reference named, the reference PMU dropped, or the reference not a mapped voltage channel.
/// </summary>
public class PmuAngleReferenceTests
{
    private static readonly DateTime Now = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Historical = Now.AddHours(-(ForwardInferenceGuard.HorizonHours + 5));

    // PMU 1 at the slack bus 1 (voltage only); PMU 2 at bus 7 (voltage and a branch current 7 -> 8).
    private static readonly PmuChannelMap[] Maps =
    {
        new(1, new[] { new PmuVoltageChannel(0, BusId: 1, BaseVolts: 100.0, VmSigmaPu: 0.002, VaSigmaDeg: 0.05) },
            Array.Empty<PmuCurrentChannel>()),
        new(2, new[] { new PmuVoltageChannel(0, BusId: 7, BaseVolts: 100.0, VmSigmaPu: 0.002, VaSigmaDeg: 0.05) },
            new[] { new PmuCurrentChannel(1, FromBusId: 7, ToBusId: 8, Circuit: null, BaseAmps: 10.0, IMagSigmaPu: 0.01, IAngSigmaDeg: 0.1) }),
    };

    private static readonly PmuAngleReference Slack = new(PmuId: 1, PhasorIndex: 0);

    // The UTC-frame angles of one instant, all shifted by a common offset (the frame rotation).
    private static PdcSuperframe Frame(double offsetRad, bool dropSlackPmu = false)
    {
        var samples = new Dictionary<int, PmuSample>
        {
            [2] = new PmuSample(2, Historical,
                new[] { Phasor.FromPolar(101.0, -0.30 + offsetRad), Phasor.FromPolar(4.0, -0.70 + offsetRad) }, 50.0, 0.0),
        };
        if (!dropSlackPmu)
            samples[1] = new PmuSample(1, Historical, new[] { Phasor.FromPolar(100.0, 0.20 + offsetRad) }, 50.0, 0.0);
        return new PdcSuperframe(Historical, samples, dropSlackPmu ? new[] { 1 } : Array.Empty<int>());
    }

    private static double Row(MeasurementSet s, MeasurementKind k, int bus)
        => s.Measurements.Single(m => m.Kind == k && (m.Bus == bus || m.From == bus)).Value;

    [Fact]
    public void Angles_are_differences_from_the_reference_channel()
    {
        MeasurementSet s = PmuMeasurementSetMapper.Map(Frame(0.0), Maps, utcNow: Now, angleReference: Slack);
        Assert.Equal(0.0, Row(s, MeasurementKind.VaDeg, 1));
        Assert.Equal(-0.50 * 180.0 / Math.PI, Row(s, MeasurementKind.VaDeg, 7), 9);
        Assert.Equal(-0.90 * 180.0 / Math.PI, Row(s, MeasurementKind.IAngDeg, 7), 9);
    }

    [Fact]
    public void A_common_utc_frame_rotation_cancels()
    {
        // The whole UTC frame rotated by 1.234 rad (what a frequency deviation does between instants): the
        // slack-referenced rows do not move. Before R295 every row moved by 70.7 degrees.
        MeasurementSet a = PmuMeasurementSetMapper.Map(Frame(0.0), Maps, utcNow: Now, angleReference: Slack);
        MeasurementSet b = PmuMeasurementSetMapper.Map(Frame(1.234), Maps, utcNow: Now, angleReference: Slack);
        Assert.Equal(Row(a, MeasurementKind.VaDeg, 7), Row(b, MeasurementKind.VaDeg, 7), 9);
        Assert.Equal(Row(a, MeasurementKind.IAngDeg, 7), Row(b, MeasurementKind.IAngDeg, 7), 9);
    }

    [Fact]
    public void The_difference_is_wrapped_into_plus_minus_180_degrees()
    {
        // Reference at +3.0 rad, bus 7 at -3.0 rad: the raw difference is -6.0 rad; wrapped it is 2 pi - 6 rad.
        var samples = new Dictionary<int, PmuSample>
        {
            [1] = new PmuSample(1, Historical, new[] { Phasor.FromPolar(100.0, 3.0) }, 50.0, 0.0),
            [2] = new PmuSample(2, Historical, new[] { Phasor.FromPolar(100.0, -3.0), Phasor.FromPolar(1.0, 3.0) }, 50.0, 0.0),
        };
        MeasurementSet s = PmuMeasurementSetMapper.Map(new PdcSuperframe(Historical, samples, Array.Empty<int>()),
            Maps, utcNow: Now, angleReference: Slack);
        Assert.Equal((2.0 * Math.PI - 6.0) * 180.0 / Math.PI, Row(s, MeasurementKind.VaDeg, 7), 9);
    }

    [Fact]
    public void The_anchor_puts_a_non_slack_reference_into_the_slack_frame()
    {
        // Reference = the bus 7 channel, whose slack-frame angle is known to be -12 degrees.
        var reference = new PmuAngleReference(PmuId: 2, PhasorIndex: 0, AnchorDeg: -12.0);
        MeasurementSet s = PmuMeasurementSetMapper.Map(Frame(0.0), Maps, utcNow: Now, angleReference: reference);
        Assert.Equal(-12.0, Row(s, MeasurementKind.VaDeg, 7));
        Assert.Equal(0.50 * 180.0 / Math.PI - 12.0, Row(s, MeasurementKind.VaDeg, 1), 9);
    }

    [Fact]
    public void No_named_reference_is_refused_when_there_are_angles_to_emit()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PmuMeasurementSetMapper.Map(Frame(0.0), Maps, utcNow: Now));
        Assert.Contains("UTC", ex.Message);
    }

    [Fact]
    public void A_dropped_reference_pmu_is_refused_not_emitted_absolute()
    {
        var ex = Assert.Throws<InvalidDataException>(
            () => PmuMeasurementSetMapper.Map(Frame(0.0, dropSlackPmu: true), Maps, utcNow: Now, angleReference: Slack));
        Assert.Contains("did not report", ex.Message);
    }

    [Fact]
    public void A_reference_that_is_not_a_mapped_voltage_channel_is_refused()
    {
        // PMU 2 phasor 1 is a CURRENT channel; PMU 9 is not mapped at all.
        Assert.Throws<ArgumentException>(() => PmuMeasurementSetMapper.Map(Frame(0.0), Maps, utcNow: Now,
            angleReference: new PmuAngleReference(2, 1)));
        Assert.Throws<ArgumentException>(() => PmuMeasurementSetMapper.Map(Frame(0.0), Maps, utcNow: Now,
            angleReference: new PmuAngleReference(9, 0)));
    }

    [Fact]
    public void The_horizon_guard_still_runs_first()
    {
        var fresh = new PdcSuperframe(Now.AddHours(-1), new Dictionary<int, PmuSample>(), Array.Empty<int>());
        Assert.Throws<ForwardInferenceException>(() => PmuMeasurementSetMapper.Map(fresh, Maps, utcNow: Now));
    }
}
