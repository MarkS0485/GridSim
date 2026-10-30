using System;
using System.Collections.Generic;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.Events;
using GridSim.Core.Replay;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The credibility gate. A self-consistent event (its "recorded" metrics ARE what the physics produces) must be
/// Reproduced; a mismatched event must be Failed; and the baseline is always device-free, so attaching a device
/// to the config cannot change the replay.
/// </summary>
public class ReplayValidatorTests
{
    // Build an event whose recorded outcome is exactly what the no-device swing model produces - so a correct
    // validator MUST reproduce it. The numbers are Synthetic (a machinery check), not a claim about reality.
    private static GridEvent SelfConsistent(double inertiaMwS, double demandMw, double lossMw)
    {
        var cfg = new DynamicsConfig();
        var events = new[] { new FrequencyEvent(1.0, -lossMw) };
        FrequencyResponse truth = FrequencyDynamics.Simulate(inertiaMwS, demandMw, events, cfg);

        return new GridEvent(
            "synthetic", "self-consistent synthetic event", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            inertiaMwS, demandMw,
            new List<PowerStep> { new(1.0, -lossMw, "loss", ProvenanceTag.Synthetic) },
            new List<RecordedAction>(),
            new RecordedOutcome(truth.InitialRocofHzPerS, truth.NadirHz, truth.SettleHz, 0, null, ProvenanceTag.Synthetic),
            ProvenanceTag.Synthetic);
    }

    [Fact]
    public void A_self_consistent_event_is_reproduced()
    {
        ReplayResult r = ReplayValidator.Validate(SelfConsistent(200_000, 35_000, 1320));
        Assert.Equal(ReplayVerdict.Reproduced, r.Verdict);
        Assert.True(r.RocofRelError < 1e-6, $"RoCoF error should be ~0, was {r.RocofRelError}");
        Assert.True(r.NadirErrorHz < 1e-6, $"nadir error should be ~0, was {r.NadirErrorHz}");
        // Inertia inferred from the recorded RoCoF must recover the event inertia.
        Assert.Equal(r.EventInertiaMwS, r.InferredInertiaMwS, 0);
    }

    [Fact]
    public void A_mismatched_event_fails()
    {
        GridEvent good = SelfConsistent(200_000, 35_000, 1320);
        // Corrupt the recorded outcome wildly (half the RoCoF, 2 Hz higher nadir).
        GridEvent bad = good with
        {
            Outcome = good.Outcome with
            {
                InitialRocofHzPerS = good.Outcome.InitialRocofHzPerS * 0.4,
                NadirHz = good.Outcome.NadirHz + 2.0,
            },
        };
        Assert.Equal(ReplayVerdict.Failed, ReplayValidator.Validate(bad).Verdict);
    }

    [Fact]
    public void A_slightly_off_event_is_marginal_not_reproduced_or_failed()
    {
        // The middle verdict was entirely untested. Nudge both headline metrics into the marginal band - outside
        // the +/-15% RoCoF / 0.15 Hz nadir tolerance but within 2x it: RoCoF recorded 1.2x the truth (16.7% error)
        // and nadir 0.2 Hz off. Neither "ok" so it isn't Reproduced; both within 2x so it isn't Failed.
        GridEvent good = SelfConsistent(200_000, 35_000, 1320);
        GridEvent marginal = good with
        {
            Outcome = good.Outcome with
            {
                InitialRocofHzPerS = good.Outcome.InitialRocofHzPerS * 1.2,
                NadirHz = good.Outcome.NadirHz - 0.2,
            },
        };
        ReplayResult r = ReplayValidator.Validate(marginal);
        Assert.Equal(ReplayVerdict.Marginal, r.Verdict);
        Assert.InRange(r.RocofRelError, 0.15, 0.30);
        Assert.InRange(r.NadirErrorHz, 0.15, 0.30);
    }

    [Fact]
    public void The_baseline_is_always_device_free()
    {
        GridEvent ev = SelfConsistent(200_000, 35_000, 1320);
        // Even if a caller passes a heavily-armed device config, the credibility baseline ignores it.
        var withDevice = new DynamicsConfig { DeviceRotationalInertiaMwS = 500_000, FastResponseMw = 5000 };
        ReplayResult a = ReplayValidator.Validate(ev);
        ReplayResult b = ReplayValidator.Validate(ev, withDevice);
        Assert.Equal(a.SimulatedRocofHzPerS, b.SimulatedRocofHzPerS, 12);
        Assert.Equal(a.SimulatedNadirHz, b.SimulatedNadirHz, 12);
    }
}
