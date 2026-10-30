using System.Reflection;
using GridSim.Core.Dynamics;
using Xunit;

namespace GridSim.Tests;

/// <summary>R243: FrequencyLimits.ActiveRocofLimitHzPerS is no longer a settable process-wide static (a global that let
/// one caller change every other study's verdict). Nothing ever set it, so no result changes: the value is still the
/// modern 1.0 Hz/s, and a bespoke limit is passed explicitly through the existing overloads.</summary>
public sealed partial class HHygieneTests
{
    [Fact]
    public void R243_active_rocof_limit_has_no_setter_and_keeps_its_value()
    {
        PropertyInfo? p = typeof(FrequencyLimits).GetProperty(nameof(FrequencyLimits.ActiveRocofLimitHzPerS),
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(p);
        Assert.Null(p!.SetMethod);   // no public or private setter: nothing can change it at run time
        Assert.Equal(1.0, FrequencyLimits.ActiveRocofLimitHzPerS);
        Assert.Equal(FrequencyLimits.RocofLimitModernHzPerS, FrequencyLimits.ActiveRocofLimitHzPerS);
        Assert.Equal(1e-9, FrequencyLimits.RocofBreachToleranceHzPerS);
        Assert.DoesNotContain("limitHzPerS + 1e-9", CoreSrc("Dynamics", "FrequencyLimits.cs"));
    }

    [Fact]
    public void R243_default_assessment_equals_the_explicit_modern_limit()
    {
        // The default overloads read the (now fixed) value; scoring with the explicit 1.0 Hz/s must agree exactly, and a
        // bespoke limit goes through the explicit overload without touching anything global.
        FrequencyResponse r = FrequencyDynamics.Simulate(60_000, 25_000, new[] { new FrequencyEvent(1.0, -1_800) });
        Assert.Equal(FrequencyLimits.Assess(r, FrequencyLimits.RocofLimitModernHzPerS), FrequencyLimits.Assess(r));
        FrequencyAssessment legacy = FrequencyLimits.Assess(r, FrequencyLimits.RocofLimitLegacyHzPerS);
        Assert.Equal(FrequencyLimits.RocofLimitLegacyHzPerS, legacy.RocofLimitAppliedHzPerS);
        Assert.Equal(FrequencyLimits.RocofLimitModernHzPerS, FrequencyLimits.Assess(r).RocofLimitAppliedHzPerS);
    }
}
