using System;
using System.Collections.Generic;
using GridSim.Core.Dynamics;
using GridSim.Core.Frequency;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The RoCoF limit is dual-threshold and configurable: the legacy pre-ALoMCP loss-of-mains
/// relay setting (0.125 Hz/s) and the modern GB setting relays were retuned to (1.0 Hz/s).
/// These tests pin the two thresholds as distinct and check that an assessment flags a breach
/// against the chosen threshold  -  e.g. a ~0.5 Hz/s RoCoF breaches legacy but not modern.
/// </summary>
public class RocofThresholdTests
{
    [Fact]
    public void Legacy_and_modern_thresholds_are_distinct()
    {
        Assert.Equal(0.125, FrequencyLimits.RocofLimitLegacyHzPerS, 6);
        Assert.Equal(1.0, FrequencyLimits.RocofLimitModernHzPerS, 6);
        Assert.NotEqual(FrequencyLimits.RocofLimitLegacyHzPerS, FrequencyLimits.RocofLimitModernHzPerS);
        Assert.True(FrequencyLimits.RocofLimitModernHzPerS > FrequencyLimits.RocofLimitLegacyHzPerS);

        // Active limit defaults to the modern setting; the legacy alias stays at 0.125.
        Assert.Equal(FrequencyLimits.RocofLimitModernHzPerS, FrequencyLimits.ActiveRocofLimitHzPerS, 6);
        Assert.Equal(FrequencyLimits.RocofLimitLegacyHzPerS, FrequencyLimits.RocofLimitHzPerS, 6);
    }

    [Fact]
    public void BreachesRocof_compares_against_the_supplied_limit()
    {
        Assert.True(FrequencyLimits.BreachesRocof(0.5, FrequencyLimits.RocofLimitLegacyHzPerS));
        Assert.False(FrequencyLimits.BreachesRocof(0.5, FrequencyLimits.RocofLimitModernHzPerS));
    }

    /// <summary>~0.5 Hz/s: over the legacy 0.125 setting, comfortably under the modern 1.0 setting.</summary>
    private static FrequencyResponse HalfHzPerSecondResponse()
    {
        // RoCoF = f0 * dP / (2E) = 50 * (-2000) / (2 * 100_000) = -0.5 Hz/s (governor-free first step).
        FrequencyResponse r = FrequencyDynamics.Simulate(
            100_000, 30_000, new[] { new FrequencyEvent(1.0, -2000) });
        Assert.InRange(Math.Abs(r.InitialRocofHzPerS), 0.4, 0.6);
        return r;
    }

    [Fact]
    public void Assessment_breaches_legacy_but_not_modern_at_half_hz_per_second()
    {
        FrequencyResponse r = HalfHzPerSecondResponse();
        FrequencyAssessment a = FrequencyLimits.Assess(r);

        Assert.True(a.RocofExceedsLegacyLimit, "0.5 Hz/s should breach the legacy 0.125 Hz/s setting");
        Assert.False(a.RocofExceedsModernLimit, "0.5 Hz/s should not breach the modern 1.0 Hz/s setting");

        // Active default is modern, so the active flag follows the modern (no-breach) verdict.
        Assert.False(a.RocofExceedsActiveLimit);
        Assert.Equal(FrequencyLimits.RocofLimitModernHzPerS, a.RocofLimitAppliedHzPerS, 6);

        // Retained back-compat flag keeps legacy semantics.
        Assert.True(a.RocofExceedsLimit);
    }

    [Fact]
    public void Assessment_scores_the_active_flag_against_the_chosen_threshold()
    {
        FrequencyResponse r = HalfHzPerSecondResponse();

        FrequencyAssessment legacy = FrequencyLimits.Assess(r, FrequencyLimits.RocofLimitLegacyHzPerS);
        Assert.True(legacy.RocofExceedsActiveLimit, "chosen legacy threshold should be breached by 0.5 Hz/s");
        Assert.Equal(FrequencyLimits.RocofLimitLegacyHzPerS, legacy.RocofLimitAppliedHzPerS, 6);

        FrequencyAssessment modern = FrequencyLimits.Assess(r, FrequencyLimits.RocofLimitModernHzPerS);
        Assert.False(modern.RocofExceedsActiveLimit, "chosen modern threshold should not be breached by 0.5 Hz/s");
        Assert.Equal(FrequencyLimits.RocofLimitModernHzPerS, modern.RocofLimitAppliedHzPerS, 6);
    }

    [Fact]
    public void Trace_reports_legacy_and_modern_breaches_independently()
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // A single 0.5 Hz step over 1 s => 0.5 Hz/s worst-case RoCoF.
        double[] hz = { 50.0, 49.5, 49.5, 49.6, 49.7, 49.9 };
        var samples = new List<FrequencySample>();
        for (int i = 0; i < hz.Length; i++) samples.Add(new FrequencySample(t0.AddSeconds(i), hz[i]));

        FrequencyTraceReport rep = FrequencyLimits.AssessTrace(samples);

        Assert.InRange(rep.MaxAbsRocofHzPerS, 0.4, 0.6);
        Assert.True(rep.RocofLimitExceededLegacy);
        Assert.False(rep.RocofLimitExceededModern);
        Assert.True(rep.RocofLimitExceeded); // retained flag == legacy
    }

    [Fact]
    public void Screening_reports_dual_threshold_breach_flags()
    {
        var conts = new[] { new Contingency("mid unit", 1000, 4_000) };

        // Score the active breach against the legacy setting explicitly.
        IReadOnlyList<RocofScreeningResult> legacy =
            RocofScreening.Screen(100_000, 30_000, conts, FrequencyLimits.RocofLimitLegacyHzPerS);
        RocofScreeningResult rl = legacy[0];

        // 1000 MW on ~96 GVAs => ~0.26 Hz/s: over legacy, under modern.
        Assert.InRange(Math.Abs(rl.InitialRocofHzPerS), 0.15, 0.9);
        Assert.True(rl.RocofBreachLegacy);
        Assert.False(rl.RocofBreachModern);
        Assert.True(rl.RocofBreachActive, "active limit was set to legacy, so it should breach");
        Assert.Equal(FrequencyLimits.RocofLimitLegacyHzPerS, rl.RocofLimitAppliedHzPerS, 6);
        Assert.True(rl.RocofBreach); // retained flag == legacy

        // Same contingency scored against the modern setting: active flag flips.
        IReadOnlyList<RocofScreeningResult> modern =
            RocofScreening.Screen(100_000, 30_000, conts, FrequencyLimits.RocofLimitModernHzPerS);
        Assert.False(modern[0].RocofBreachActive);
        Assert.True(modern[0].RocofBreachLegacy); // legacy flag unchanged
    }
}
