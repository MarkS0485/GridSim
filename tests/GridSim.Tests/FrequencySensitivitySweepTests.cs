using System.Collections.Generic;
using GridSim.Core.Dynamics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The unfitted-parameter sensitivity sweep (<see cref="FrequencySensitivitySweep"/>): instead of one nadir
/// number it returns a band, answering "if DampingPctPerHz is 20% off, how much does the nadir move?" (the
/// per-parameter swing) and "how uncertain is the nadir given all the unfitted constants?" (the combined
/// band). The initial RoCoF is inertia-only, so its band across the sweep must be ~0.
/// </summary>
public class FrequencySensitivitySweepTests
{
    private const double InertiaMwS = 200_000;
    private const double DemandMw = 30_000;
    private static readonly FrequencyEvent[] Loss = { new(AtSeconds: 1.0, DeltaPMw: -1800) };

    [Fact]
    public void Reports_a_nadir_band_and_ranks_the_dominant_parameter()
    {
        FrequencySensitivityResult r = FrequencySensitivitySweep.Sweep(InertiaMwS, DemandMw, Loss, fractionalRange: 0.20);

        // Three parameters swept, ranked by how much each moves the nadir.
        Assert.Equal(3, r.PerParameter.Count);
        Assert.NotNull(r.Dominant);
        for (int i = 1; i < r.PerParameter.Count; i++)
            Assert.True(r.PerParameter[i - 1].NadirSwingHz >= r.PerParameter[i].NadirSwingHz, "per-parameter swings must be ranked");

        // The combined band brackets the nominal nadir and is non-trivial (the constants genuinely matter).
        Assert.True(r.NadirMinHz <= r.Nominal.NadirHz && r.Nominal.NadirHz <= r.NadirMaxHz,
            $"nominal nadir {r.Nominal.NadirHz:F4} should sit inside [{r.NadirMinHz:F4}, {r.NadirMaxHz:F4}]");
        Assert.True(r.NadirBandHz > 1e-3, $"a 20% parameter uncertainty should move the nadir measurably, band {r.NadirBandHz:F4} Hz");

        // At least one parameter's individual swing is a meaningful fraction of the total band.
        Assert.True(r.Dominant!.NadirSwingHz > 0, "the dominant parameter should have a nonzero swing");
    }

    [Fact]
    public void Initial_rocof_is_invariant_under_the_sweep()
    {
        // The swing constants shape only the nadir/recovery; the initial RoCoF depends only on inertia and
        // the loss, so the sweep must not move it - a sanity check on the whole mechanism.
        FrequencySensitivityResult r = FrequencySensitivitySweep.Sweep(InertiaMwS, DemandMw, Loss);
        Assert.True(r.InitialRocofBandHzPerS < 1e-9, $"RoCoF should be sweep-invariant, band {r.InitialRocofBandHzPerS}");
    }

    [Fact]
    public void Damping_alone_can_be_isolated()
    {
        // Isolate one parameter: fix everything but DampingPctPerHz, vary it +/-20%, read the nadir move.
        FrequencySensitivityResult r = FrequencySensitivitySweep.Sweep(
            InertiaMwS, DemandMw, Loss, fractionalRange: 0.20,
            parameters: new[] { FrequencyFitParam.Damping });

        ParameterSensitivity d = Assert.Single(r.PerParameter);
        Assert.Equal(FrequencyFitParam.Damping, d.Parameter);
        Assert.Equal(0.8, d.LowValue, 9);   // 1.0 * 0.8
        Assert.Equal(1.2, d.HighValue, 9);  // 1.0 * 1.2
        // More damping arrests the fall sooner -> a shallower dip -> a HIGHER nadir; so nadir(high) > nadir(low).
        Assert.True(d.NadirAtHigh > d.NadirAtLow, $"more damping should lift the nadir: low {d.NadirAtLow:F4}, high {d.NadirAtHigh:F4}");
        Assert.True(d.NadirSwingHz > 0);
    }
}
