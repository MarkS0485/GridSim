using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Synchrophasor;
using Xunit;
using static GridSim.Tests.Doc15Frames;

namespace GridSim.Tests;

/// <summary>
/// DOC-15 (docs-session stream 15, October 2026), the Core side. D-DOC-110: a DATA frame's time is SOC + FRACSEC /
/// TIME_BASE computed exactly in integer 100 ns ticks (it went through a double, and some instants landed one tick
/// short). D-DOC-111: TIME_BASE 0 is refused by name (and a FRACSEC that is not below TIME_BASE makes the frame
/// malformed). D-DOC-008: the PMU mapper bounds-checks every channel, not only the reference. D-DOC-109: the linear PMU
/// estimator refuses an unknown bus and an unusable sigma or covariance by name. Each refusal is paired with the
/// allowed case.
/// </summary>
public class Doc15SynchrophasorTests
{
    private const uint Soc = 1_700_000_001;   // 2023-11-14T22:13:21Z
    private static readonly Pmu OneVoltage = new(101, "STN_A", new[] { Voltage });

    // The decoded time of a one-PMU DATA frame with this FRACSEC at this TIME_BASE, through the stream overload, after
    // checking the single-PMU overload gives the same instant.
    private static DateTime Decoded(uint timeBase, uint fracsec)
    {
        C37118ConfigFrame cfg = C37118ConfigParser.Parse(Config(timeBase, Soc - 1, OneVoltage));
        byte[] data = Data(Soc, fracsec, new[] { (1.0f, 0.0f) });
        C37118StreamDataFrame stream = C37118Decoder.DecodeData(data, cfg);
        C37118DataFrame single = C37118Decoder.DecodeData(data, cfg.TimeBase, cfg.Pmus[0]);
        Assert.Equal(FrameDecodeStatus.Ok, stream.Status);
        Assert.Equal(FrameDecodeStatus.Ok, single.Status);
        Assert.Equal(stream.TimeUtc, single.TimeUtc);
        Assert.Equal(DateTimeKind.Utc, stream.TimeUtc.Kind);
        return stream.TimeUtc;
    }

    // The instant the decoder computed before D-DOC-110, by the very expression it used (evaluated on this runtime).
    private static DateTime OldFrameTime(uint fracsec, uint timeBase)
        => DateTime.UnixEpoch.AddSeconds(Soc).AddSeconds(fracsec / (double)timeBase);

    private static DateTime Exact(long fractionTicks) => DateTime.UnixEpoch.AddTicks(Soc * TimeSpan.TicksPerSecond + fractionTicks);

    // ================================================================================================================
    // D-DOC-110: exact integer-tick frame times
    // ================================================================================================================

    [Theory]
    // FRACSEC at TIME_BASE 1,000,000 | exact fraction in ticks. Before: one tick short (the double quotient fell just
    // below the exact value and DateTime.AddSeconds truncates to whole ticks).
    [InlineData(820_000u, 8_200_000L)]   // the 42nd frame of a second at 50 frames/s: before .8199999 s, after .82 s
    [InlineData(43_000u, 430_000L)]      // 1,000 frames/s, frame 43: before .0429999 s, after .043 s
    [InlineData(35u, 350L)]              // 35 us: before 349 ticks (34.9 us), after 350 ticks
    public void D110_instants_the_double_put_one_tick_short_are_now_exact(uint fracsec, long fractionTicks)
    {
        DateTime t = Decoded(1_000_000, fracsec);
        Assert.Equal(Exact(fractionTicks).Ticks, t.Ticks);
        // The defect, proved on this runtime: the old expression lands exactly one tick (100 ns) earlier.
        Assert.Equal(Exact(fractionTicks).Ticks - 1, OldFrameTime(fracsec, 1_000_000).Ticks);
    }

    [Fact]
    public void D110_every_frame_of_a_1000_per_second_stream_is_exact_and_59_of_them_moved_by_one_tick()
    {
        // At TIME_BASE 1,000,000 one FRACSEC count is 10 ticks, so the exact instant is FRACSEC x 10 ticks. All 1,000
        // frames of one second at 1,000 frames/s are exact now; 59 of them were one tick short before, and no instant
        // moved by anything but +1 tick.
        int moved = 0;
        for (uint k = 0; k < 1_000; k++)
        {
            uint fracsec = k * 1_000;
            DateTime t = Decoded(1_000_000, fracsec);
            Assert.Equal(Exact(fracsec * 10L).Ticks, t.Ticks);
            long shift = t.Ticks - OldFrameTime(fracsec, 1_000_000).Ticks;
            Assert.True(shift == 0 || shift == 1, $"FRACSEC {fracsec}: moved {shift} ticks");
            if (shift == 1) moved++;
        }
        Assert.Equal(59, moved);

        // 50 frames/s: only FRACSEC 820,000 moved.
        var moved50 = Enumerable.Range(0, 50).Select(k => (uint)k * 20_000u)
            .Where(f => Decoded(1_000_000, f).Ticks != OldFrameTime(f, 1_000_000).Ticks).ToArray();
        Assert.Equal(new[] { 820_000u }, moved50);
    }

    [Theory]
    // A TIME_BASE that does not divide FRACSEC x 10^7: the instant is the last whole tick at or before the true time.
    [InlineData(3u, 1u, 3_333_333L)]                     // 1/3 s = 3,333,333.3 ticks
    [InlineData(3u, 2u, 6_666_666L)]                     // 2/3 s = 6,666,666.6 ticks (floor, not round)
    [InlineData(0x00FF_FFFFu, 0x00FF_FFFEu, 9_999_999L)] // the largest TIME_BASE, the last FRACSEC below it
    [InlineData(1u, 0u, 0L)]                             // TIME_BASE 1: whole seconds only
    [InlineData(1_000_000u, 999_999u, 9_999_990L)]       // the last FRACSEC below TIME_BASE
    [InlineData(1_000_000u, 500_000u, 5_000_000L)]       // unchanged by D-DOC-110 (the double was exact)
    public void D110_the_fraction_is_floored_to_whole_ticks(uint timeBase, uint fracsec, long fractionTicks)
        => Assert.Equal(Exact(fractionTicks).Ticks, Decoded(timeBase, fracsec).Ticks);

    // ================================================================================================================
    // D-DOC-111: TIME_BASE 0 refused; FRACSEC not below TIME_BASE is malformed
    // ================================================================================================================

    [Theory]
    [InlineData(0u)]              // TIME_BASE 0
    [InlineData(0xFF00_0000u)]    // only the reserved top byte set: the 24-bit TIME_BASE is still 0
    public void D111_a_config_frame_with_time_base_0_is_refused_by_name(uint timeBaseField)
    {
        var ex = Assert.Throws<C37118FrameException>(() => C37118ConfigParser.Parse(Config(timeBaseField, Soc, OneVoltage)));
        Assert.Contains("TIME_BASE 0", ex.Message, StringComparison.Ordinal);
        // Allowed: TIME_BASE 1, the smallest.
        Assert.Equal(1, C37118ConfigParser.Parse(Config(1, Soc, OneVoltage)).TimeBase);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(0x0100_0000L)]   // wider than the 24-bit field
    public void D111_every_decode_overload_refuses_an_impossible_time_base(long timeBase)
    {
        byte[] data = Data(Soc, 0, new[] { (1.0f, 0.0f) });
        PmuConfig pmu = C37118ConfigParser.Parse(Config(1_000_000, Soc - 1, OneVoltage)).Pmus[0];

        var single = Assert.Throws<ArgumentOutOfRangeException>(() => C37118Decoder.DecodeData(data, timeBase, pmu));
        Assert.Contains($"TIME_BASE is {timeBase}", single.Message, StringComparison.Ordinal);
        var floatOnly = Assert.Throws<ArgumentOutOfRangeException>(() => C37118Decoder.DecodeData(data, timeBase, 1, FloatAll));
        Assert.Contains($"TIME_BASE is {timeBase}", floatOnly.Message, StringComparison.Ordinal);
        // A hand-built configuration can still carry it (the parser refuses it in a frame).
        var stream = Assert.Throws<ArgumentOutOfRangeException>(() => C37118Decoder.DecodeData(data, new C37118ConfigFrame(timeBase, 50, new[] { pmu })));
        Assert.Contains($"TIME_BASE is {timeBase}", stream.Message, StringComparison.Ordinal);

        // Allowed: the same frame at the largest TIME_BASE.
        Assert.Equal(FrameDecodeStatus.Ok, C37118Decoder.DecodeData(data, C37118Decoder.MaxTimeBase, pmu).Status);
    }

    [Theory]
    [InlineData(1_000_000u)]       // exactly one second: not a fraction
    [InlineData(1_500_000u)]       // used to put the frame 1.5 s after its SOC
    [InlineData(0x00FF_FFFFu)]
    public void D111_a_fracsec_not_below_time_base_makes_the_frame_malformed(uint fracsec)
    {
        C37118ConfigFrame cfg = C37118ConfigParser.Parse(Config(1_000_000, Soc - 1, OneVoltage));
        byte[] data = Data(Soc, fracsec, new[] { (1.0f, 0.0f) });
        Assert.Equal(FrameDecodeStatus.Malformed, C37118Decoder.DecodeData(data, cfg).Status);
        Assert.Equal(FrameDecodeStatus.Malformed, C37118Decoder.DecodeData(data, cfg.TimeBase, cfg.Pmus[0]).Status);
        Assert.Equal(FrameDecodeStatus.Malformed, C37118Decoder.DecodeData(data, cfg.TimeBase, 1, FloatAll).Status);
        // The time quality byte (FRACSEC bits 31-24) is not part of the count: quality 0x5 with FRACSEC 999,999 is fine.
        Assert.Equal(FrameDecodeStatus.Ok, C37118Decoder.DecodeData(Data(Soc, 0x0500_0000u | 999_999u, new[] { (1.0f, 0.0f) }), cfg).Status);
    }

    // ================================================================================================================
    // D-DOC-008: the mapper bounds-checks every channel
    // ================================================================================================================

    private static readonly DateTime Now = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Historical = Now.AddHours(-(ForwardInferenceGuard.HorizonHours + 5));

    // PMU 101: voltage phasor 0 at bus 1 (the reference) and a current phasor 1 on branch 1 -> 4.
    private static PmuChannelMap Map101(int voltagePhasor = 0, int currentPhasor = 1) => new(101,
        new[] { new PmuVoltageChannel(voltagePhasor, BusId: 1, BaseVolts: 100.0, VmSigmaPu: 0.002, VaSigmaDeg: 0.05) },
        new[] { new PmuCurrentChannel(currentPhasor, FromBusId: 1, ToBusId: 4, Circuit: "c2", BaseAmps: 10.0, IMagSigmaPu: 0.01, IAngSigmaDeg: 0.1) });

    private static PdcSuperframe Superframe(params (int Pmu, int Phasors)[] reported) => new(Historical,
        reported.ToDictionary(r => r.Pmu, r => new PmuSample(r.Pmu, Historical,
            Enumerable.Range(0, r.Phasors).Select(i => new Phasor(100.0 + i, 0.0)).ToArray(), 50.0, 0.0)),
        Array.Empty<int>());

    [Fact]
    public void D008_a_non_reference_channel_past_the_reported_phasors_is_refused_by_name()
    {
        // PMU 101 reported one phasor (as after a configuration change to fewer phasors); its current channel uses
        // phasor 1. Before: a raw IndexOutOfRangeException from sample.Phasors[1].
        var ex = Assert.Throws<PmuChannelMapMismatchException>(() => PmuMeasurementSetMapper.Map(
            Superframe((101, 1)), new[] { Map101() }, utcNow: Now, angleReference: new PmuAngleReference(101, 0)));
        Assert.Contains("PMU 101 reported 1 phasor(s), but its current channel on branch 1->4 circuit 'c2' uses phasor 1", ex.Message, StringComparison.Ordinal);
        // An ArgumentException (the map does not fit), never the InvalidDataException that means "the reference PMU did
        // not report at this instant", which pmu-ingest counts instead of refusing.
        Assert.IsAssignableFrom<ArgumentException>(ex);
    }

    [Fact]
    public void D008_every_bad_channel_is_named_at_once_and_a_pmu_that_did_not_report_is_not_checked()
    {
        PmuChannelMap absent = new(202, new[] { new PmuVoltageChannel(9, 7, 100.0, 0.002, 0.05) }, Array.Empty<PmuCurrentChannel>());
        var ex = Assert.Throws<PmuChannelMapMismatchException>(() => PmuMeasurementSetMapper.Map(
            Superframe((101, 1)), new[] { Map101(voltagePhasor: -1, currentPhasor: 3), absent }, utcNow: Now));
        Assert.Contains("its voltage channel at bus 1 uses phasor -1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("uses phasor 3", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("PMU 202", ex.Message, StringComparison.Ordinal);   // did not report: no rows, nothing to check

        // Allowed: the same maps when PMU 101 reported both phasors (PMU 202 still absent): four rows, values as before.
        MeasurementSet set = PmuMeasurementSetMapper.Map(Superframe((101, 2)), new[] { Map101(), absent }, utcNow: Now,
            angleReference: new PmuAngleReference(101, 0));
        Assert.Equal(4, set.Measurements.Count);
        Assert.Equal(100.0 / 100.0, set.Measurements.Single(m => m.Kind == MeasurementKind.VmPu).Value);
        Assert.Equal(101.0 / 10.0, set.Measurements.Single(m => m.Kind == MeasurementKind.IMagPu).Value);
    }

    // ================================================================================================================
    // D-DOC-109: the linear PMU estimator checks its inputs
    // ================================================================================================================

    private static (GridModel Model, List<PmuVoltageMeasurement> Voltages) Case9Voltages(double sigma = 0.001)
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(truth.Converged);
        var v = new List<PmuVoltageMeasurement>();
        for (int i = 0; i < truth.BusIds.Count; i++)
            v.Add(new PmuVoltageMeasurement(truth.BusIds[i], Complex.FromPolarCoordinates(truth.Vm[i], truth.VaDeg[i] * Math.PI / 180.0), sigma));
        return (model, v);
    }

    [Fact]
    public void D109_a_voltage_at_a_bus_the_model_does_not_have_is_refused_by_name()
    {
        (GridModel model, List<PmuVoltageMeasurement> v) = Case9Voltages();
        v.Add(new PmuVoltageMeasurement(999, Complex.One, 0.001));
        // Before: a raw KeyNotFoundException from the bus lookup.
        var ex = Assert.Throws<InvalidDataException>(() => LinearPmuStateEstimator.Estimate(model, v, Array.Empty<PmuCurrentMeasurement>()));
        Assert.Contains("voltage phasor 9 (bus 999): bus 999 is not a bus of the model", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.0, 0.0, "sigma is 0; it must be a finite number above 0")]
    [InlineData(-0.001, 0.0, "sigma is -0.001; it must be a finite number above 0")]
    [InlineData(double.NaN, 0.0, "sigma is NaN; it must be a finite number above 0")]
    [InlineData(double.PositiveInfinity, 0.0, "sigma is Infinity; it must be a finite number above 0")]
    [InlineData(0.001, double.NaN, "the real / imaginary covariance is NaN; it must be finite")]
    [InlineData(0.001, 0.0000011, "is not smaller in size than sigma^2")]   // |cov| just above sigma^2 (1e-6): not positive definite
    [InlineData(0.001, -0.000002, "is not smaller in size than sigma^2")]   // a negative covariance twice sigma^2: indefinite
    public void D109_an_unusable_sigma_or_covariance_is_refused_by_name(double sigma, double cov, string expected)
    {
        (GridModel model, List<PmuVoltageMeasurement> v) = Case9Voltages();
        v[2] = v[2] with { Sigma = sigma, CovOffDiag = cov };
        var ex = Assert.Throws<InvalidDataException>(() => LinearPmuStateEstimator.Estimate(model, v, Array.Empty<PmuCurrentMeasurement>()));
        Assert.Contains($"voltage phasor 2 (bus {v[2].BusId})", ex.Message, StringComparison.Ordinal);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);

        // A current phasor is checked the same way.
        (GridModel m2, List<PmuVoltageMeasurement> v2) = Case9Voltages();
        Branch br = m2.Branches[0];
        var ec = Assert.Throws<InvalidDataException>(() => LinearPmuStateEstimator.Estimate(m2, v2,
            new[] { new PmuCurrentMeasurement(br.From, br.To, null, Complex.Zero, sigma, cov) }));
        Assert.Contains($"current phasor 0 (branch {br.From}->{br.To})", ec.Message, StringComparison.Ordinal);
        Assert.Contains(expected, ec.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void D109_a_non_finite_value_is_refused_and_every_problem_is_named_at_once()
    {
        (GridModel model, List<PmuVoltageMeasurement> v) = Case9Voltages();
        v[0] = v[0] with { Value = new Complex(double.NaN, 0.0) };
        v[1] = v[1] with { Sigma = 0.0 };
        v.Add(new PmuVoltageMeasurement(12345, Complex.One, 0.001));
        var ex = Assert.Throws<InvalidDataException>(() => LinearPmuStateEstimator.Estimate(model, v, Array.Empty<PmuCurrentMeasurement>()));
        Assert.Contains("voltage phasor 0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("is not finite", ex.Message, StringComparison.Ordinal);
        Assert.Contains("voltage phasor 1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("bus 12345 is not a bus of the model", ex.Message, StringComparison.Ordinal);

        // Allowed: a good set (a covariance just inside sigma^2) still estimates.
        (GridModel okModel, List<PmuVoltageMeasurement> ok) = Case9Voltages();
        ok[0] = ok[0] with { CovOffDiag = 0.0000009 };
        Assert.True(LinearPmuStateEstimator.Estimate(okModel, ok, Array.Empty<PmuCurrentMeasurement>()).Converged);
    }
}
