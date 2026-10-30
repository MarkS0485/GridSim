using System;
using System.Linq;
using GridSim.Core.Dynamics;
using GridSim.Core.Events;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Blind cross-event validation — the honest test of whether the framework predicts or merely calibrates.
/// See docs/technical/0014 Cascade validation and blind prediction.md for the full protocol and numbers.
///
/// <para>Key structural finding, pinned here: the asset-dynamics CASCADE (embedded DG tripping on RoCoF)
/// occurred in exactly ONE major GB event — 9 Aug 2019 — and the ALoMCP programme then raised the relays
/// 0.125→1.0 Hz/s specifically to eliminate the mechanism. So there is NO second GB cascade event to
/// blind-predict; the cascade is permanently a mechanism/envelope model, not a validated predictor. What
/// CAN be cross-checked is the BASE swing+response model, which these tests exercise with shared unfitted
/// constants and per-event MEASURED inputs.</para>
/// </summary>
public class BlindPredictionTests
{
    // The single shared, unfitted config (NOT tuned per event). Per-event inputs (inertia, demand, losses,
    // response-service volumes, pre-event frequency) are all recorded/measured and come from the event.
    private static DynamicsConfig SharedConfig => new() { DurationSeconds = 150 };

    private static (double model, double recorded) Replay(string id)
    {
        GridEvent ev = EventCatalog.Get(id);
        DynamicsConfig cfg = ev.ApplyRecordedServices(SharedConfig);
        FrequencyResponse r = FrequencyDynamics.Simulate(
            ev.PreEventInertiaMwS, ev.PreEventDemandMw, ev.ToFrequencyEvents(), cfg);
        return (r.NadirHz, ev.Outcome.NadirHz);
    }

    [Fact]
    public void Base_model_reproduces_2019_when_fed_the_recorded_facts()
    {
        // REPLAY (not prediction): fed all recorded losses + the recorded LFDD, the kernel reproduces the
        // recorded nadir. Residual ~0.02 Hz. This confirms the integrator, it does not validate prediction.
        // LF6 N317 (October 2026): the 2019 event now carries its recorded response services and demand effect, and the
        // nadir is the LFDD instant (replica: 48.84096136852519 Hz at 76.0 s, residual 0.041 Hz; before, 48.8203 Hz at
        // the model's first arrest at 14.06 s). The bound below is unchanged.
        (double model, double recorded) = Replay("gb-2019-08-09");
        Assert.Equal(48.8, recorded, 1);
        Assert.True(Math.Abs(model - recorded) < 0.10, $"2019 replay {model:F3} vs recorded {recorded:F3}");
    }

    [Fact]
    public void Base_model_forward_estimates_2023_to_within_a_few_tenths_Hz()
    {
        // FORWARD-ish: measured losses + measured DC/DM/DR volumes + measured pre-event frequency, NO LFDD
        // (none occurred) and NO per-event fitting. The model lands ~0.30 Hz too DEEP (model ~48.98 vs
        // recorded 49.275) - the right region, not tight. An honest imperfect result, pinned here so a
        // regression would show. It reflects the base arrest model (damping + response taus), NOT the asset
        // framework (2023 had no embedded cascade).
        (double model, double recorded) = Replay("gb-2023-12-22");
        Assert.Equal(49.275, recorded, 3);
        double residual = recorded - model;   // positive => model too deep
        Assert.InRange(residual, 0.15, 0.45);  // ~0.30 Hz too deep; documents the real miss
    }

    [Fact]
    public void Parameter_free_initial_rocof_is_physically_consistent_for_both_events()
    {
        // RoCoF = f0*dP/(2E) has ZERO free parameters. From each event's recorded inertia and its first-1s
        // loss it lands in the right band (both ~0.13-0.16 Hz/s), consistent with the recorded window-
        // averaged figures (2019 -0.151/-0.17; 2023 -0.102 at a 1 s window).
        foreach (string id in new[] { "gb-2019-08-09", "gb-2023-12-22" })
        {
            GridEvent ev = EventCatalog.Get(id);
            double earlyLoss = ev.Losses.Where(l => l.AtSeconds <= 1.0).Sum(l => Math.Abs(l.DeltaPMw));
            double rocof = 50.0 * earlyLoss / (2.0 * ev.PreEventInertiaMwS);
            Assert.InRange(rocof, 0.10, 0.20);
        }
    }

    [Fact]
    public void The_cascade_has_no_second_gb_event_to_blind_predict()
    {
        // The structural limit, asserted as a fact: 2019 carries embedded-generation RoCoF-trip losses;
        // 2023 (post-ALoMCP) carries none. There is no held-out GB cascade, so the cascade model is
        // un-blind-validatable on GB data by construction.
        bool Has(string id, string needle) =>
            EventCatalog.Get(id).Losses.Any(l => l.What.Contains(needle, StringComparison.OrdinalIgnoreCase));

        Assert.True(Has("gb-2019-08-09", "distributed generation"), "2019 is a cascade event");
        Assert.False(Has("gb-2023-12-22", "distributed generation"), "2023 has no embedded cascade (post-ALoMCP)");
    }
}
