using GridSim.Core.Dynamics;
using Xunit;

namespace GridSim.Tests;

/// <summary>R171 / R173: the design-event time and the TSGB sizer's search constants are named (no number moves).
/// R254: the sizer says whether its size holds the nadir target (a new flag; the numbers are unchanged).</summary>
public sealed partial class HHygieneTests
{
    [Fact]
    public void R173_sizer_constants_equal_the_literals_they_replaced()
    {
        Assert.Equal(1.0, FrequencyDynamics.DesignEventSeconds);
        Assert.Equal(100.0, TsgbSizer.InitialFfrBracketMw);
        Assert.Equal(8, TsgbSizer.MaxBracketGrowths);
        Assert.Equal(1.8, TsgbSizer.BracketGrowthFactor);
        Assert.Equal(44, TsgbSizer.FfrBisectionIterations);
        Assert.Equal(1e-9, TsgbSizer.MinRocofTargetHzPerS);
    }

    [Fact]
    public void R171_R173_literals_did_not_survive_beside_their_names()
    {
        string sizer = CoreSrc("Dynamics", "TsgbSizer.cs");
        Assert.DoesNotContain("FrequencyEvent(1.0", sizer);
        Assert.DoesNotContain("Math.Max(100.0", sizer);
        Assert.DoesNotContain("hi *= 1.8", sizer);
        Assert.DoesNotContain("it < 44", sizer);
        Assert.DoesNotContain("> 1e-9", sizer);
        Assert.DoesNotContain("FrequencyEvent(1.0", CoreSrc("Dynamics", "RocofScreening.cs"));
    }

    /// <summary>The sizer exactly as it was before R173 / R254 (literals, no flag), written out verbatim.</summary>
    private static (double Synthetic, double Ffr, double Rocof, double Nadir) OldSizeForTargets(
        double systemInertiaMwS, double demandMw, double lossMw, double targetMaxRocofHzPerS, double targetMinNadirHz)
    {
        DynamicsConfig baseCfg = new DynamicsConfig();
        double f0 = baseCfg.NominalHz;
        double loss = Math.Abs(lossMw);
        double synthetic = 0;
        if (targetMaxRocofHzPerS > 1e-9)
        {
            double eRequired = f0 * loss / (2.0 * targetMaxRocofHzPerS);
            synthetic = Math.Max(0.0, eRequired - systemInertiaMwS);
        }
        double lo = 0, hi = Math.Max(100.0, loss);
        for (int grow = 0; grow < 8 && NadirWith(synthetic, hi) < targetMinNadirHz; grow++) hi *= 1.8;
        for (int it = 0; it < 44; it++)
        {
            double mid = 0.5 * (lo + hi);
            if (NadirWith(synthetic, mid) >= targetMinNadirHz) hi = mid; else lo = mid;
        }
        double ffr = hi;
        FrequencyResponse final = Run(synthetic, ffr);
        return (synthetic, ffr, final.InitialRocofHzPerS, final.NadirHz);

        double NadirWith(double synth, double ffrMw) => Run(synth, ffrMw).NadirHz;
        FrequencyResponse Run(double synth, double ffrMw) =>
            FrequencyDynamics.Simulate(systemInertiaMwS, demandMw, new[] { new FrequencyEvent(1.0, -loss) },
                baseCfg with { DeviceRotationalInertiaMwS = synth, FastResponseMw = ffrMw });
    }

    [Theory]
    [InlineData(200_000.0, 30_000.0, 1_800.0, 0.125, 49.5)]    // GB-sized, both targets bind
    [InlineData(400_000.0, 30_000.0, 300.0, 0.125, 49.5)]      // ample inertia (InertiaAndLimitsTests' case)
    [InlineData(80_000.0, 20_000.0, 1_320.0, 0.5, 49.2)]       // low inertia, web TripTab-style targets
    [InlineData(150_000.0, 25_000.0, 1_000.0, 0.0, 49.5)]      // no RoCoF target: no synthetic inertia
    [InlineData(150_000.0, 25_000.0, 1_000.0, 0.125, 50.5)]    // unreachable nadir target (above nominal)
    public void R173_R254_sizer_numbers_are_bit_identical_to_the_old_code(
        double inertia, double demand, double loss, double rocofTarget, double nadirTarget)
    {
        (double synth, double ffr, double rocof, double nadir) = OldSizeForTargets(inertia, demand, loss, rocofTarget, nadirTarget);
        TsgbSizing s = TsgbSizer.SizeForTargets(inertia, demand, loss, rocofTarget, nadirTarget);
        AssertBits(synth, s.SyntheticInertiaMwS, "synthetic inertia");
        AssertBits(ffr, s.FastResponseMw, "fast response");
        AssertBits(rocof, s.AchievedRocofHzPerS, "achieved RoCoF");
        AssertBits(nadir, s.AchievedNadirHz, "achieved nadir");
        // R254: the flag is exactly the bisection's own test applied to the final run.
        Assert.Equal(nadir >= nadirTarget, s.Feasible);
    }

    [Fact]
    public void R254_an_unreachable_nadir_target_is_flagged_not_feasible()
    {
        // A nadir target above nominal can never be held: no device keeps a loss from pulling frequency below f0.
        TsgbSizing s = TsgbSizer.SizeForTargets(150_000, 25_000, 1_000, 0.125, 50.5);
        Assert.False(s.Feasible);
        Assert.True(s.AchievedNadirHz < 50.5);
        // And a comfortable target is feasible.
        TsgbSizing ok = TsgbSizer.SizeForTargets(400_000, 30_000, 300, 0.125, 49.5);
        Assert.True(ok.Feasible);
        Assert.True(ok.AchievedNadirHz >= 49.5);
    }

    [Fact]
    public void R254_callers_surface_the_flag()
    {
        string cli = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Cli", "Program.cs");
        Assert.Contains("TsgbSizingShortfall(sizeNow)", cli);
        Assert.Contains("TsgbSizingShortfall(sizeHalf)", cli);
        // Test retargeted 2026-10-20 (UI programme 0.2): the desktop sizing read-out moved to
        // src/GridSim.Ui/Dashboard/DashboardModel.cs; assertion unchanged, and strengthened: the NOT ACHIEVED text that
        // surfaces the flag is pinned beside it.
        string dash = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Dashboard", "DashboardModel.cs");
        Assert.Contains("sizing.Feasible", dash);
        Assert.Contains("(sizing.Feasible ? \"\" : $\"  (NOT ACHIEVED: nadir {sizing.AchievedNadirHz:F2} Hz at the largest size searched)\")", dash);
        Assert.Contains("_sizing.Feasible", PresentationConstantsAntiDriftTests.Source("src", "GridSim.Web", "Components", "Tabs", "TripTab.razor"));
        Assert.Contains("_sizing.Feasible", PresentationConstantsAntiDriftTests.Source("src", "GridSim.Web", "Components", "Tabs", "TsgbWhatIf.razor"));
    }

    [Fact]
    public void R171_screening_still_applies_the_loss_at_one_second()
    {
        // The screen's response is FrequencyDynamics.Simulate with the loss at the design-event time: rebuild it by hand
        // with the literal 1.0 and compare the reported RoCoF and nadir bit for bit.
        var c = new Contingency("unit", 1_320.0, 6_000.0);
        RocofScreeningResult r = Assert.Single(RocofScreening.Screen(200_000.0, 30_000.0, new[] { c }));
        FrequencyResponse byHand = FrequencyDynamics.Simulate(200_000.0 - 6_000.0, 30_000.0,
            new[] { new FrequencyEvent(1.0, -1_320.0) }, new DynamicsConfig());
        AssertBits(byHand.InitialRocofHzPerS, r.InitialRocofHzPerS, "RoCoF");
        AssertBits(byHand.NadirHz, r.NadirHz, "nadir");
    }
}
