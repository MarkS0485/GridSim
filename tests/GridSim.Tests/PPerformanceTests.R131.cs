using GridSim.Core.Dynamics;
using GridSim.Core.Presentation;
using Xunit;

namespace GridSim.Tests;

public partial class PPerformanceTests
{
    // =================================================================================================================
    // R131: the web TRIP / DEVICE tab and the TSGB what-if card skip the recompute when every input is the same to the
    // bit (ExactInputMemo); the computation they skip is a pure function of those inputs.
    // =================================================================================================================

    [Fact]
    public void R131_the_memo_is_unchanged_only_for_the_same_bits_and_text()
    {
        var memo = new ExactInputMemo();
        Assert.False(memo.IsUnchanged("auto", 1.0, 2.0));          // nothing remembered yet
        memo.Remember("auto", 1.0, 2.0);
        Assert.True(memo.IsUnchanged("auto", 1.0, 2.0));
        Assert.False(memo.IsUnchanged("manual", 1.0, 2.0));        // the text is an input
        Assert.False(memo.IsUnchanged(null, 1.0, 2.0));
        Assert.False(memo.IsUnchanged("auto", 1.0, Math.BitIncrement(2.0)));   // one ulp is a different input
        Assert.False(memo.IsUnchanged("auto", 1.0));               // a different count
        Assert.False(memo.IsUnchanged("auto", 2.0, 1.0));          // a different order

        // Signed zeros and NaN payloads are compared by their bits: +0 and -0 differ (Equals would call them equal).
        memo.Remember(null, 0.0, double.NaN);
        Assert.True(memo.IsUnchanged(null, 0.0, double.NaN));
        Assert.False(memo.IsUnchanged(null, -0.0, double.NaN));
        Assert.False(memo.IsUnchanged(null, 0.0, BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(double.NaN) ^ 1L)));

        memo.Forget();
        Assert.False(memo.IsUnchanged(null, 0.0, double.NaN));
        Assert.Throws<ArgumentNullException>(() => memo.IsUnchanged("x", null!));
        Assert.Throws<ArgumentNullException>(() => memo.Remember("x", null!));
    }

    [Theory]
    [InlineData(250_000.0, 30_000.0, 1_800.0, 50.0)]
    [InlineData(80_000.0, 20_000.0, 1_320.0, 50.0)]
    [InlineData(150_000.0, 25_000.0, 1_000.0, 60.0)]
    public void R131_the_skipped_computation_gives_the_same_bits_on_the_same_inputs(double inertiaMwS, double demandMw, double lossMw, double nominalHz)
    {
        // The skip is sound only if a second run on the same inputs reproduces the first to the bit. Run the TRIP tab's
        // auto-mode chain twice (base swing, TsgbSizer, device swing) and the card's chain, and compare every number.
        static (FrequencyResponse Base, TsgbSizing Sizing, FrequencyResponse Device) Chain(double h, double d, double l, double f0)
        {
            var evt = new[] { new FrequencyEvent(1.0, -l) };
            DynamicsConfig cfg = TripWhatIf.StudyConfig(f0);
            FrequencyResponse b = FrequencyDynamics.Simulate(h, d, evt, cfg);
            TsgbSizing s = TsgbSizer.SizeForTargets(h, d, l, TripWhatIf.SizingMaxRocofHzPerS,
                TripWhatIf.SizingMinNadirHz(FrequencyBands.ForNominal(f0)), cfg);
            FrequencyResponse dev = FrequencyDynamics.Simulate(h, d, evt,
                cfg with { DeviceRotationalInertiaMwS = s.SyntheticInertiaMwS, FastResponseMw = s.FastResponseMw });
            return (b, s, dev);
        }

        var first = Chain(inertiaMwS, demandMw, lossMw, nominalHz);
        var second = Chain(inertiaMwS, demandMw, lossMw, nominalHz);
        R131AssertSame(first.Base, second.Base, "base");
        R131AssertSame(first.Device, second.Device, "device");
        Assert.True(SameBits(first.Sizing.SyntheticInertiaMwS, second.Sizing.SyntheticInertiaMwS));
        Assert.True(SameBits(first.Sizing.FastResponseMw, second.Sizing.FastResponseMw));
        Assert.True(SameBits(first.Sizing.AchievedRocofHzPerS, second.Sizing.AchievedRocofHzPerS));
        Assert.True(SameBits(first.Sizing.AchievedNadirHz, second.Sizing.AchievedNadirHz));
        Assert.Equal(first.Sizing.Feasible, second.Sizing.Feasible);
    }

    private static void R131AssertSame(FrequencyResponse a, FrequencyResponse b, string what)
    {
        Assert.True(SameBits(a.NadirHz, b.NadirHz), $"{what}: nadir");
        Assert.True(SameBits(a.InitialRocofHzPerS, b.InitialRocofHzPerS), $"{what}: RoCoF");
        Assert.True(SameBits(a.SettleHz, b.SettleHz), $"{what}: settle");
        Assert.Equal(a.Points.Count, b.Points.Count);
        for (int i = 0; i < a.Points.Count; i++)
        {
            Assert.Equal(a.Points[i].Time, b.Points[i].Time);
            Assert.True(SameBits(a.Points[i].Hz, b.Points[i].Hz), $"{what}: point {i}");
        }
    }

    [Fact]
    public void R131_both_what_if_surfaces_check_the_memo_on_every_input_they_read()
    {
        // Source pins: the memo guards the computation with exactly the inputs the computation reads, and remembers them
        // only after it completed. If a new input is added to the computation, this test is the reminder to key on it.
        string trip = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Web", "Components", "Tabs", "TripTab.razor");
        Assert.Contains("if (_memo.IsUnchanged(_mode, inertiaIn, demandIn, _lossMw, _manualInertiaMwS, _manualFfrMw, NominalHz)) return;", trip, StringComparison.Ordinal);
        Assert.Contains("RecomputeNow();\r\n        _memo.Remember(_mode, inertiaIn, demandIn, _lossMw, _manualInertiaMwS, _manualFfrMw, NominalHz);".Replace("\r\n", Nl(trip), StringComparison.Ordinal), trip, StringComparison.Ordinal);
        string card = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Web", "Components", "Tabs", "TsgbWhatIf.razor");
        Assert.Contains("if (_memo.IsUnchanged(null, LargestLossMw, InertiaGvaS, DemandMw, _inertiaGva, _ffrGw, NominalHz)) return;", card, StringComparison.Ordinal);
        Assert.Contains("RecomputeNow();\r\n        _memo.Remember(null, LargestLossMw, InertiaGvaS, DemandMw, _inertiaGva, _ffrGw, NominalHz);".Replace("\r\n", Nl(card), StringComparison.Ordinal), card, StringComparison.Ordinal);
    }

    /// <summary>The line ending a source file uses (the checkout may hold either).</summary>
    private static string Nl(string source) => source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
}
