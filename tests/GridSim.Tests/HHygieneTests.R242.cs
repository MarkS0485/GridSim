using GridSim.Core.Dynamics;
using Xunit;

namespace GridSim.Tests;

/// <summary>R242: the governor lag, the one responder lag in the swing family that divided by T directly (T = 0 gave
/// Infinity / NaN, T below the step overshot or diverged), now goes through SwingKernel.GovernorLagIncrement in all seven
/// copies: max(step, T) like every other lag, and a negative or NaN T fails loud. For T at or above the step the increment
/// is the old expression bit for bit. The sensitivity sweep's range is checked to lie strictly between 0 and 1.</summary>
public sealed partial class HHygieneTests
{
    [Fact]
    public void R242_guarded_increment_is_bit_identical_to_the_old_expression_whenever_T_is_at_least_the_step()
    {
        double[] steps = { 0.02, 0.01, 0.005, 0.02 / 7.0, 1e-3 };
        double[] taus = { 8.0, 5.6, 11.2, 0.5, 30.0, 0.02, 1e-3 };   // includes T == step exactly
        foreach (double step in steps)
            foreach (double tau in taus)
            {
                if (tau < step) continue;
                foreach (double target in Sweep())
                    foreach (double pGov in new[] { 0.0, -0.0, 12.5, -480.25, 1e4 })
                    {
                        double old = pGov + (target - pGov) / tau * step;
                        double now = pGov + SwingKernel.GovernorLagIncrement(target, pGov, tau, step);
                        AssertBits(old, now, $"step {step} tau {tau} target {target} pGov {pGov}");
                    }
            }
    }

    [Fact]
    public void R242_a_lag_shorter_than_the_step_is_deadbeat_not_divergent()
    {
        // T = 0 used to divide by zero; T = step / 10 used to overshoot by a factor of ten each step. Both now reach the
        // target in exactly one step (the same convention as the fast-response and service lags).
        foreach (double tau in new[] { 0.0, 0.002 })
        {
            double pGov = 0.0;
            pGov += SwingKernel.GovernorLagIncrement(100.0, pGov, tau, 0.02);
            Assert.Equal(100.0, pGov);
        }
        var cfg = new DynamicsConfig { GovernorTimeConstantS = 0.0 };
        FrequencyResponse r = FrequencyDynamics.Simulate(200_000, 30_000, new[] { new FrequencyEvent(1.0, -1_320) }, cfg);
        Assert.True(double.IsFinite(r.NadirHz) && double.IsFinite(r.SettleHz) && double.IsFinite(r.InitialRocofHzPerS),
            $"T = 0 must give a finite run (nadir {r.NadirHz}, settle {r.SettleHz})");
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(-1e-12)]
    [InlineData(double.NaN)]
    public void R242_a_negative_or_NaN_governor_time_constant_fails_loud(double tau)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => SwingKernel.GovernorLagIncrement(1.0, 0.0, tau, 0.02));
        Assert.Contains("governor time constant", ex.Message);
        var cfg = new DynamicsConfig { GovernorTimeConstantS = tau };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FrequencyDynamics.Simulate(200_000, 30_000, new[] { new FrequencyEvent(1.0, -1_320) }, cfg));
    }

    [Fact]
    public void R242_every_copy_of_the_governor_lag_uses_the_guarded_increment()
    {
        string[][] files =
        {
            new[] { "Dynamics", "FrequencyDynamics.cs" },
            new[] { "Dynamics", "FrequencyIntegrator.cs" },
            new[] { "Dynamics", "AssetAwareFrequencyIntegrator.cs" },
            new[] { "Dynamics", "AssetFrequencyDynamics.cs" },
            new[] { "Dynamics", "MultiDeviceFrequencyIntegrator.cs" },
            new[] { "Dynamics", "Distributed", "MultiDomainSwing.cs" },
            new[] { "Fleet", "FleetDynamics.cs" },
        };
        // Moved 2026-10 (B1 R080, Ruling R1: Mark delegated EE calls 2026-10-04; source: the R080 finding, "factor the
        // per-substep responder update into one shared function used by all five"): the seven governor-lag copies are now
        // ONE, in SwingResponders.Advance, which every engine below constructs. Before: each engine file had to contain
        // "SwingKernel.GovernorLagIncrement(". After: no engine may keep its own lag (the unchanged DoesNotMatch), each must
        // build the shared kernel, and the kernel's one copy must use the guarded increment. The guard's purpose (no
        // unguarded governor lag anywhere in the family) is unchanged and now holds by construction.
        foreach (string[] f in files)
        {
            string src = CoreSrc(f);
            Assert.DoesNotMatch(@"/\s*_?cfg\.GovernorTimeConstantS\s*\*", src);
            Assert.Contains("new SwingResponders(", src);
            Assert.DoesNotContain("GovernorLagIncrement(", src);
        }
        Assert.Contains("SwingKernel.GovernorLagIncrement(", CoreSrc("Dynamics", "SwingResponders.cs"));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(-0.2)]
    [InlineData(double.NaN)]
    public void R242_sensitivity_range_outside_zero_to_one_is_refused(double range)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => FrequencySensitivitySweep.Sweep(
            200_000, 30_000, new[] { new FrequencyEvent(1.0, -1_320) }, fractionalRange: range));
        Assert.Equal("fractionalRange", ex.ParamName);
    }
}
