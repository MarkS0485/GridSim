using GridSim.Core.Dynamics;
using GridSim.Core.Frequency;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Calibration / plausibility of <see cref="FrequencyDynamics"/> against a real NESO
/// frequency trace (<c>data/neso/frequency-sample.csv</c>, copied next to the tests by
/// the Content glob and read from <see cref="AppContext.BaseDirectory"/>).
///
/// The bundled sample is AMBIENT normal-operation data: it never trips, so it can only
/// validate the swing-equation RoCoF scale, not the governor/damping constants that shape
/// the nadir (see the provenance note on <see cref="DynamicsConfig"/>). These tests keep
/// that honest: they assert (a) the trace really is ambient, (b) the model reproduces the
/// trace's ambient RoCoF scale, and (c) a representative loss lands in the physically
/// sensible range implied by the trace + the GB limits. No constants were fitted from this
/// trace because it contains no clean trip event to fit against.
/// </summary>
public class FrequencyCalibrationTests
{
    private static string SampleCsv =>
        Path.Combine(AppContext.BaseDirectory, "data", "neso", "frequency-sample.csv");

    private static bool HaveSample => File.Exists(SampleCsv);

    private static FrequencyStats Measured() =>
        RocofUpsampler.Summarize(NesoFrequencyCsv.Read(SampleCsv));

    // A representative GB operating point: ~200 GVAs spinning, ~30 GW demand, and the
    // largest normal single-infeed loss (1320 MW). Consistent with the other dynamics tests.
    private const double SystemEMwS = 200_000, DemandMw = 30_000, LossMw = 1320, F0 = 50.0;

    [Fact]
    public void Measured_trace_is_ambient_not_a_trip()
    {
        Assert.True(HaveSample, $"bundled sample missing: {SampleCsv}");   // GR-404: data/neso/frequency-sample.csv is committed, so a missing copy fails instead of passing silently

        FrequencyStats m = Measured();

        Assert.True(m.Count > 0, "trace should contain samples");

        // Stays inside the GB operational band (50 +/- 0.2 Hz); nowhere near a trip dip.
        Assert.InRange(m.MinHz, 49.8, 50.0);
        Assert.InRange(m.MaxHz, 50.0, 50.2);
        Assert.InRange(m.MeanHz, 49.9, 50.1);

        // Ambient RoCoF is tiny  -  orders of magnitude below the 0.125 Hz/s protection
        // threshold. This is why the trace cannot pin the governor/damping constants.
        Assert.True(m.MaxAbsRocofHzPerS > 0, "ambient RoCoF should be non-zero");
        Assert.True(m.MaxAbsRocofHzPerS < 0.05,
            $"max |RoCoF| {m.MaxAbsRocofHzPerS:F4} Hz/s marks this as ambient, not a trip");
    }

    [Fact]
    public void Model_reproduces_the_ambient_rocof_scale()
    {
        Assert.True(HaveSample, $"bundled sample missing: {SampleCsv}");   // GR-404: as above

        FrequencyStats m = Measured();

        // Invert the swing equation to find the imbalance that would produce the measured
        // ambient RoCoF at our representative inertia:  deltaP = 2E * RoCoF / f0.
        double impliedDeltaP = 2.0 * SystemEMwS * m.MaxAbsRocofHzPerS / F0;

        // That imbalance should be a small, plausible ambient fluctuation (tens of MW),
        // not a trip-sized loss.
        Assert.InRange(impliedDeltaP, 1.0, 500.0);

        // Drive that same imbalance through the model at t=0 and confirm Simulate's initial
        // RoCoF matches the measured ambient RoCoF  -  the model tracks the trace's scale.
        // (Governor/damping haven't moved at t=0, so this isolates the swing math; it is a
        // trace-anchored check, distinct from the InertiaEstimator round-trip.)
        FrequencyResponse r = FrequencyDynamics.Simulate(
            SystemEMwS, DemandMw, new[] { new FrequencyEvent(0.0, -impliedDeltaP) });

        Assert.Equal(m.MaxAbsRocofHzPerS, Math.Abs(r.InitialRocofHzPerS), 3);
    }

    [Fact]
    public void Representative_trip_rocof_is_physically_sensible()
    {
        Assert.True(HaveSample, $"bundled sample missing: {SampleCsv}");   // GR-404: as above

        FrequencyStats m = Measured();

        FrequencyResponse r = FrequencyDynamics.Simulate(
            SystemEMwS, DemandMw, new[] { new FrequencyEvent(1.0, -LossMw) });

        double rocof = Math.Abs(r.InitialRocofHzPerS);

        // Closed form: f0 * dP / (2E) = 50 * 1320 / (2 * 200000) = 0.165 Hz/s.
        Assert.Equal(F0 * LossMw / (2.0 * SystemEMwS), rocof, 3);

        // Physically sensible band for a large GB infeed loss at this inertia.
        Assert.InRange(rocof, 0.05, 0.5);

        // A real trip perturbs frequency far beyond ambient noise: the trip RoCoF must be
        // at least an order of magnitude above the ambient max the trace shows.
        Assert.True(rocof > 10.0 * m.MaxAbsRocofHzPerS,
            $"trip RoCoF {rocof:F3} should dwarf ambient {m.MaxAbsRocofHzPerS:F4} Hz/s");
    }

    [Fact]
    public void Representative_trip_nadir_and_recovery_are_sensible()
    {
        Assert.True(HaveSample, $"bundled sample missing: {SampleCsv}");   // GR-404: as above

        FrequencyResponse r = FrequencyDynamics.Simulate(
            SystemEMwS, DemandMw, new[] { new FrequencyEvent(1.0, -LossMw) });

        // Falls below nominal. With no fast response a 1320 MW loss on 200 GVAs dips through
        // the statutory 49.5 Hz (~49.2 Hz here  -  realistic for an unarrested primary-only
        // response), but must stay above the 48.8 Hz low-frequency demand-disconnection floor.
        Assert.True(r.NadirHz < F0, "a generation loss must pull frequency below nominal");
        Assert.InRange(r.NadirHz, 48.8, 50.0);

        // Governor + load damping recover the frequency above the nadir.
        Assert.True(r.SettleHz > r.NadirHz,
            $"frequency should recover: nadir {r.NadirHz:F3} -> settle {r.SettleHz:F3}");
    }

    [Fact]
    public void Documented_defaults_are_unchanged()
    {
        // Guards the provenance note on DynamicsConfig: the ambient sample did NOT justify
        // fitting these, so they must stay at their documented literature-typical values.
        var cfg = new DynamicsConfig();
        Assert.Equal(50.0, cfg.NominalHz, 9);
        Assert.Equal(0.05, cfg.DroopPu, 9);
        Assert.Equal(1.0, cfg.DampingPctPerHz, 9);
        Assert.Equal(0.20, cfg.ResponsiveFraction, 9);
        Assert.Equal(8.0, cfg.GovernorTimeConstantS, 9);
    }
}
