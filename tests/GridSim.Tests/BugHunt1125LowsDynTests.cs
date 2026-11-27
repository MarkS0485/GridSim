using System.Globalization;
using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Models;
using Xunit;

namespace GridSim.Tests;

/// <summary>Bug hunt batch 5, DYN lows (DYN-09 to DYN-22): one regression test per fix.</summary>
public sealed class BugHunt1125LowsDynTests
{
    [Fact]
    public void Dyn09_a_schedule_that_expands_to_no_step_is_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() => RmsStepSchedule.Fixed(0.005).Expand(0.001));
        Assert.Contains("holds no step", ex.Message, StringComparison.Ordinal);
        Assert.Single(RmsStepSchedule.Fixed(0.005).Expand(0.005).Steps);
        Assert.Empty(RmsStepSchedule.Fixed(0.005).Expand(0.0).Steps);   // a zero-length run is still no step and no error
    }

    [Fact]
    public void Dyn10_a_run_beyond_the_step_cap_is_refused_by_name()
    {
        var ex = Assert.Throws<ArgumentException>(() => RmsStepSchedule.Fixed(1e-3).Expand(1e9));
        Assert.Contains("more than the 100000000 a run may take", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dyn11_nan_is_refused_by_the_older_constructors()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LeadLag(double.NaN, 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LeadLag(0.1, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Washout(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Integrator(double.NaN));
        Assert.Throws<ArgumentException>(() => new Integrator(1.0, double.NaN, 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PiController(double.NaN, 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Tgov1(double.NaN, 0.5, 1.0, 5.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HydroGovernor(double.NaN, 0.3, 5.0, 0.5, 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pss1a(double.NaN, 10, 0.1, 0.02, 0.1, 0.02));
        _ = new LeadLag(0.1, 1.0);
        _ = new Tgov1(0.05, 0.5, 1.0, 5.0);
    }

    [Fact]
    public void Dyn13_ieeeg3_set_reference_takes_the_power_initialize_takes()
    {
        var g = new Ieeeg3(tg: 0.05, tp: 0.04, uo: 0.1, uc: -0.1, pMax: 1.0, pMin: 0.0, sigma: 0.05, delta: 0.3, tr: 5.0, tw: 1.0,
            a11: 0.5, a13: 1.0, a21: 1.5, a23: 0.8);
        g.Initialize(0.5);
        double before = g.Step(0.0, 0.005);
        g.Initialize(0.5);
        g.SetReference(0.5);   // the same power again: an equilibrium, not a move to gate 0.5
        double pm = 0;
        for (int k = 0; k < 2000; k++) pm = g.Step(0.0, 0.005);
        Assert.Equal(before, pm, 9);
        Assert.Equal(0.5, pm, 9);
    }

    [Fact]
    public void Dyn16_refusal_numbers_are_invariant()
    {
        CultureInfo was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var ex = Assert.Throws<ArgumentException>(() => new Gast(0.05, 0.4, 0.1, 3.0, 1.0, 2.0, valveMinPu: 0.5, valveMaxPu: 0.25));
            Assert.Contains("VMIN = 0.5, VMAX = 0.25", ex.Message, StringComparison.Ordinal);
        }
        finally { CultureInfo.CurrentCulture = was; }
    }

    [Fact]
    public void Dyn22_device_inertia_in_the_base_configuration_counts_toward_the_requirement()
    {
        TsgbSizing plain = TsgbSizer.SizeForTargets(100_000, 30_000, 1_800, 0.125, 49.2);
        TsgbSizing withBase = TsgbSizer.SizeForTargets(100_000, 30_000, 1_800, 0.125, 49.2, new DynamicsConfig { DeviceRotationalInertiaMwS = 50_000 });
        Assert.True(plain.SyntheticInertiaMwS > 50_000, plain.SyntheticInertiaMwS.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(plain.SyntheticInertiaMwS - 50_000, withBase.SyntheticInertiaMwS, 6);
        // The same total inertia is studied, so the RoCoF is the same.
        Assert.Equal(plain.AchievedRocofHzPerS, withBase.AchievedRocofHzPerS, 9);
    }
}
