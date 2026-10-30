using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Forming;
using GridSim.Core.Harmonics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Integrations that used to run outside their stability region, or on inputs with no meaning, and return the garbage as
/// a result now refuse: the explicit COI swing below its damping stability bound, the grid-forming sub-step count past
/// its cap, a RoCoF screen row whose remaining inertia is too small to integrate, and harmonic order lists that are
/// empty, non-positive or duplicated.
/// </summary>
public class IntegrationStabilityGuardTests
{
    [Fact]
    public void The_explicit_swing_refuses_an_inertia_below_its_damping_stability_bound()
    {
        // 39,500 MW demand at 1 %/Hz: D = 395 MW/Hz; at 20 ms the bound is E > dt*f0*D/4 = 98.75 MW.s.
        var events = new[] { new FrequencyEvent(1.0, -100.0) };
        var ex = Assert.Throws<InvalidOperationException>(() => FrequencyDynamics.Simulate(50.0, 39_500.0, events));
        Assert.Contains("too small to integrate", ex.Message, StringComparison.Ordinal);
        // Comfortably above the bound the swing runs as before.
        FrequencyResponse ok = FrequencyDynamics.Simulate(150_000.0, 39_500.0, events);
        Assert.True(double.IsFinite(ok.NadirHz));
    }

    [Fact]
    public void A_screen_row_with_too_little_inertia_is_flagged_not_integrated()
    {
        // The only unit carries all the inertia: losing it leaves the 1 MW.s floor, far below the bound.
        var rows = RocofScreening.Screen(5_000.0, 4_000.0, new[] { new Contingency("only-unit", 500.0, 5_000.0) });
        RocofScreeningResult r = Assert.Single(rows);
        Assert.False(r.Integrated);
        Assert.True(double.IsNaN(r.NadirHz));
        Assert.True(r.RocofBreach && r.StatutoryBreach && r.LfddRisk);
        // Closed-form initial RoCoF: -f0 * P / (2 E) = -50 * 500 / 2.
        Assert.Equal(-50.0 * 500.0 / (2.0 * 1.0), r.InitialRocofHzPerS);

        // A loss that leaves plenty of inertia is integrated as before.
        RocofScreeningResult fine = Assert.Single(RocofScreening.Screen(200_000.0, 4_000.0, new[] { new Contingency("small", 100.0, 1_000.0) }));
        Assert.True(fine.Integrated);
        Assert.True(double.IsFinite(fine.NadirHz));
    }

    [Fact]
    public void The_forming_sub_step_count_is_never_silently_capped()
    {
        // Ev = 0.5 MW.s at a 20-minute outer step needs far more than 4,000 sub-steps for the gain bound.
        var ex = Assert.Throws<InvalidOperationException>(() => FormingKernel.SubStepCount(1200.0, 50.0, 0.5));
        Assert.Contains("above the 4000 cap", ex.Message, StringComparison.Ordinal);
        // An ordinary step is unaffected: 20 ms at 1,000 MW.s needs one sub-step.
        Assert.Equal(1, FormingKernel.SubStepCount(0.02, 50.0, 1_000.0));
    }

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 5, 5, 7 })]
    [InlineData(new[] { 0, 5 })]
    [InlineData(new[] { -3, 5 })]
    public void Harmonic_order_lists_that_mean_nothing_are_refused(int[] orders)
    {
        Assert.ThrowsAny<ArgumentException>(() => new HarmonicModelOptions { Orders = orders });
    }

    [Fact]
    public void A_scan_left_with_no_order_is_refused_and_so_is_its_sharpness()
    {
        var opt = HarmonicModelOptions.Study with { Orders = new[] { 3, 9, 15 }, BalancedHarmonicSequences = true };
        Assert.Throws<ArgumentException>(() => HarmonicImpedanceScan.DrivingPoint(GridSim.Core.IO.IeeeCases.Case9(), 5, opt));
    }
}
