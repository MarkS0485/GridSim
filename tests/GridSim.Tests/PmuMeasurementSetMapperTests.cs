using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation;
using GridSim.Core.Synchrophasor;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The PMU->MeasurementSet mapper: voltage phasors become per-unit Vm/Va rows at their bus, current phasors
/// become IMag/IAng rows on their branch, the aligned instant becomes TakenUtc, and a fresh instant is
/// refused by the horizon guard (invariant #1).
/// </summary>
public class PmuMeasurementSetMapperTests
{
    private static readonly DateTime Now = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Historical = Now.AddHours(-(ForwardInferenceGuard.HorizonHours + 5));

    private static PdcSuperframe FrameAt(DateTime t)
    {
        // One PMU (id 1): a voltage phasor at 1.02 pu (base 100), 0.1 rad; a current phasor 5.0 A, -0.2 rad.
        var sample = new PmuSample(1, t,
            new[] { Phasor.FromPolar(102.0, 0.1), Phasor.FromPolar(5.0, -0.2) }, 49.99, -0.01);
        return new PdcSuperframe(t, new Dictionary<int, PmuSample> { [1] = sample }, Array.Empty<int>());
    }

    private static readonly PmuChannelMap[] Maps =
    {
        new(1,
            new[] { new PmuVoltageChannel(0, BusId: 4, BaseVolts: 100.0, VmSigmaPu: 0.002, VaSigmaDeg: 0.05) },
            new[] { new PmuCurrentChannel(1, FromBusId: 4, ToBusId: 5, Circuit: null, BaseAmps: 10.0, IMagSigmaPu: 0.01, IAngSigmaDeg: 0.1) }),
    };

    [Fact]
    public void Maps_voltage_and_current_phasors_to_measurement_rows()
    {
        // Moved 2026-10 (Q4, authorised by Mark): this test used to expect the PMU's 0.1 rad and -0.2 rad to pass
        // through unchanged as slack-referenced VaDeg / IAngDeg rows, i.e. it encoded the R295 defect. C37.118
        // angles are UTC-referenced (IEEE C37.118.1-2011 section 4.2: measured against a nominal-frequency cosine
        // synchronised to UTC), so a slack-referenced row is the difference from a named reference voltage channel.
        // A second PMU (id 2) now measures the slack bus (bus 1) at 0.05 rad UTC-frame and is named as the
        // reference: bus 4's VaDeg is 0.1 - 0.05 = 0.05 rad and the current's IAngDeg is -0.2 - 0.05 = -0.25 rad.
        // Magnitudes, buses and TakenUtc are unchanged; the rows are picked by bus because the reference PMU adds
        // its own (zero-angle) slack row.
        var samples = new Dictionary<int, PmuSample>
        {
            [1] = FrameAt(Historical).Samples[1],
            [2] = new PmuSample(2, Historical, new[] { Phasor.FromPolar(100.0, 0.05) }, 49.99, -0.01),
        };
        var frame = new PdcSuperframe(Historical, samples, Array.Empty<int>());
        PmuChannelMap[] maps =
        {
            Maps[0],
            new(2, new[] { new PmuVoltageChannel(0, BusId: 1, BaseVolts: 100.0, VmSigmaPu: 0.002, VaSigmaDeg: 0.05) },
                Array.Empty<PmuCurrentChannel>()),
        };

        MeasurementSet set = PmuMeasurementSetMapper.Map(frame, maps, utcNow: Now,
            angleReference: new PmuAngleReference(PmuId: 2, PhasorIndex: 0));

        Assert.Equal(Historical, set.TakenUtc);
        Measurement vm = set.Measurements.Single(m => m.Kind == MeasurementKind.VmPu && m.Bus == 4);
        Assert.Equal(1.02, vm.Value, 6);              // 102 V / 100 V base
        Assert.Equal(4, vm.Bus);

        Measurement va = set.Measurements.Single(m => m.Kind == MeasurementKind.VaDeg && m.Bus == 4);
        Assert.Equal((0.1 - 0.05) * 180.0 / Math.PI, va.Value, 6);

        Measurement imag = set.Measurements.Single(m => m.Kind == MeasurementKind.IMagPu);
        Assert.Equal(0.5, imag.Value, 6);             // 5 A / 10 A base
        Assert.Equal(4, imag.From);
        Assert.Equal(5, imag.To);

        Measurement iang = set.Measurements.Single(m => m.Kind == MeasurementKind.IAngDeg);
        Assert.Equal((-0.2 - 0.05) * 180.0 / Math.PI, iang.Value, 6);

        Measurement slack = set.Measurements.Single(m => m.Kind == MeasurementKind.VaDeg && m.Bus == 1);
        Assert.Equal(0.0, slack.Value);               // the reference against itself: exactly zero
    }

    [Fact]
    public void A_fresh_superframe_is_refused_by_the_guard()
    {
        Assert.Throws<ForwardInferenceException>(
            () => PmuMeasurementSetMapper.Map(FrameAt(Now.AddHours(-1)), Maps, utcNow: Now));
    }

    [Fact]
    public void A_dropped_pmu_contributes_no_rows()
    {
        var empty = new PdcSuperframe(Historical, new Dictionary<int, PmuSample>(), new[] { 1 });
        MeasurementSet set = PmuMeasurementSetMapper.Map(empty, Maps, utcNow: Now);
        Assert.Empty(set.Measurements);
    }
}
