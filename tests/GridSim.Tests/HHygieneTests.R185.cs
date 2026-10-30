using GridSim.Core.Physics.Experimental;
using Xunit;

namespace GridSim.Tests;

/// <summary>R185: ContinuumWaveModel's inertia floor, non-zero-mode threshold and damping guard are named (values
/// unchanged; consts fold to the same IL literal). The review's second half (pick the COI mode by index) moves numbers
/// and is wrong for islanded networks, so it is recorded, not done (see NonZeroModeThresholdHz).</summary>
public sealed partial class HHygieneTests
{
    [Fact]
    public void R185_wave_model_constants_equal_the_literals_they_replaced()
    {
        Assert.Equal(1e-9, ContinuumWaveModel.MinInertiaConstantS);
        Assert.Equal(1e-6, ContinuumWaveModel.NonZeroModeThresholdHz);
        Assert.Equal(1e-9, ContinuumWaveModel.MinDampedModeHz);
        string src = CoreSrc("Physics", "Experimental", "ContinuumWaveModel.cs");
        Assert.DoesNotContain("Math.Max(1e-9, h[i])", src);
        Assert.DoesNotContain("freqs[i] > 1e-6", src);
        Assert.DoesNotContain("dominant > 1e-9", src);
    }

    [Fact]
    public void R185_dominant_mode_selection_is_unchanged_on_a_two_area_chain()
    {
        // Two strongly tied pairs joined by a weak tie: the dominant (slowest non-zero) mode is the inter-area one, and
        // its shape splits the pairs. The threshold rule and the old literal agree on every mode here (the COI mode is
        // far below 1e-6 Hz and every other mode far above), so the reported numbers are those of the old code.
        double[] h = { 5.0, 5.0, 4.0, 4.0 };
        var k = new double[4, 4];
        void Tie(int a, int b, double v) { k[a, b] = v; k[b, a] = v; }
        Tie(0, 1, 20.0); Tie(2, 3, 20.0); Tie(1, 2, 0.5);   // small entries keep the COI eigenvalue round-off tiny
        ContinuumWaveModel.WaveModes w = ContinuumWaveModel.Analyze(h, k, 50.0, d: new[] { 1.0, 1.0, 1.0, 1.0 });
        Assert.True(w.CoiModeHz <= ContinuumWaveModel.NonZeroModeThresholdHz, $"COI mode {w.CoiModeHz:R} Hz");
        Assert.Equal(w.ModeFrequenciesHz[1], w.DominantInterAreaHz);
        Assert.True(w.DominantDampingRatio > 0.0);
        Assert.Equal(4, w.DominantModeShape.Count);
        Assert.True(Math.Sign(w.DominantModeShape[0].Amplitude) != Math.Sign(w.DominantModeShape[3].Amplitude),
            "the inter-area mode must split the two pairs");
    }
}
