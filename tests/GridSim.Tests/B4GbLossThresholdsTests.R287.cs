using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.Events;
using GridSim.Core.Model;
using GridSim.Core.Replay;
using GridSim.Core.Scenarios;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R287: the H=0 study's analytic initial RoCoF (<see cref="H0StudyCell.BareInertiaRocofHzPerS"/>, f0 dP / (2 E)) used
/// the sum of EVERY staged loss, including steps tens of seconds after the first (gb-2019-08-09: 2,768 MW over 90 s
/// instead of the 1,521 MW of the first 5 s), and the result called that sum "LargestLossMw". It now uses the losses in
/// the first <see cref="GridEvent.RocofSettingWindowSeconds"/> (5 s), the rule the replay's inertia cross-check already
/// used (moved onto <see cref="GridEvent"/> so there is one rule), and the result carries the total, the RoCoF-setting
/// loss and the largest single step under their own names.
/// </summary>
public sealed partial class B4GbLossThresholdsTests
{
    [Fact]
    public void R287_the_rocof_setting_loss_of_the_built_in_events()
    {
        GridEvent aug = EventCatalog.Get("gb-2019-08-09");
        Assert.Equal(2768.0, aug.TotalLossMw);                // 150+737+244+390+100+210+200+550+187
        Assert.Equal(1521.0, aug.RocofSettingLossMw());        // 150+737+244+390 (0.1, 0.5, 1.0, 1.0 s; the RoCoF trip was at 3.0 s before LF6 N317, same sum)
        Assert.Equal(-1521.0, aug.RocofSettingSignedLossMw());
        Assert.Equal(737.0, aug.LargestSingleLossMw());        // Hornsea 1 deload

        GridEvent dec = EventCatalog.Get("gb-2023-12-22");
        Assert.Equal(1345.0, dec.TotalLossMw);
        Assert.Equal(1000.0, dec.RocofSettingLossMw());        // IFA at 0.5 s; Cottam at 9.5 s is outside
        Assert.Equal(-1000.0, dec.RocofSettingSignedLossMw());

        GridEvent screen = EventCatalog.Get("gb-largest-loss-1320");
        AssertBits(screen.LossEvents().Sum(l => l.DeltaPMw), screen.RocofSettingSignedLossMw(), "single-step event");
        Assert.Equal(5.0, GridEvent.RocofSettingWindowSeconds);
    }

    [Fact]
    public void R287_the_replay_cross_check_rule_is_unchanged_bit_for_bit()
    {
        // The pre-R287 inline expression of ReplayValidator, verbatim, against the shared method, on every built-in event
        // and on a late-loss event (no loss in the window: the fallback to the total).
        static double Old(GridEvent ev)
        {
            const double rocofWindowSeconds = 5.0;
            double rocofSettingLossMw = ev.Losses
                .Where(l => l.AtSeconds <= rocofWindowSeconds)
                .Sum(l => Math.Abs(l.DeltaPMw));
            if (rocofSettingLossMw <= 0) rocofSettingLossMw = ev.TotalLossMw;
            return rocofSettingLossMw;
        }
        var events = EventCatalog.Builtin().ToList();
        events.Add(events[0] with { Losses = new List<PowerStep> { new(12.0, -600, "late", ProvenanceTag.Synthetic) } });
        foreach (GridEvent ev in events)
            AssertBits(Old(ev), ev.RocofSettingLossMw(), ev.Id);
        Assert.Equal(600.0, events[^1].RocofSettingLossMw());
        Assert.Equal(-600.0, events[^1].RocofSettingSignedLossMw());
    }

    [Fact]
    public void R287_the_h0_analytic_rocof_uses_the_first_five_seconds_of_a_staged_event()
    {
        // A self-consistent staged event: 1,000 MW at 1 s, then 500 MW at 10 s (outside the 5 s window).
        var cfg = new DynamicsConfig();
        var steps = new[] { new FrequencyEvent(1.0, -1000.0), new FrequencyEvent(10.0, -500.0) };
        FrequencyResponse truth = FrequencyDynamics.Simulate(155_000, 32_000, steps, cfg);
        var ev = new GridEvent(
            "syn-staged", "staged", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), 155_000, 32_000,
            new List<PowerStep>
            {
                new(1.0, -1000.0, "first", ProvenanceTag.Synthetic),
                new(10.0, -500.0, "later", ProvenanceTag.Synthetic),
            },
            new List<RecordedAction>(),
            new RecordedOutcome(truth.InitialRocofHzPerS, truth.NadirHz, truth.SettleHz, 0, null, ProvenanceTag.Synthetic),
            ProvenanceTag.Synthetic);

        // The H0StudyEngineTests model shape without converters (the study needs an asset library for its classes).
        var gens = new List<Generator> { new(1, Pg: 20_000, MBase: 10_000, H: 4.0, Name: "sync", Type: "ccgt") };
        AssetClass gfl = AssetTestHelpers.GflClass("gfl-test", rocofTripHzPerS: 1.0);
        AssetClass gfm = AssetTestHelpers.GfmClass("gfm-test", synInertiaMwsPerHz: 1600.0, dampingMwPerHz: 200.0);
        AssetDynamicsModel bundle = AssetTestHelpers.Bundle(
            new[] { gfl, gfm }, new AssetBinding("wind*", "gfl-test"), new AssetBinding("storage", "gfm-test"));
        var model = new GridModel
        {
            BaseMva = 100,
            Buses = new List<Bus> { new(1, BusType.Slack, BaseKv: 400, Name: "GB") },
            Generators = gens,
            Branches = Array.Empty<Branch>(),
            AssetDynamics = bundle,
        };
        var spec = new H0StudySpec(ev, model, new[] { 1.0, 0.5 }, new[] { 0.0 }, new[] { Corner.Nominal },
            "gfm-test", "gfl-test", new DynamicsConfig { EnableAssetDynamics = true }, ReplayTolerance.Default);
        H0StudyResult r = H0StudyEngine.Run(spec);

        Assert.Equal(1500.0, r.TotalExogenousLossMw);      // the old "LargestLossMw"
        Assert.Equal(1000.0, r.RocofSettingLossMw);
        Assert.Equal(1000.0, r.LargestSingleLossMw);
        foreach (H0StudyCell c in r.Cells)
        {
            double eReal = c.SystemInertiaGvaS * 1000.0;
            // The engine's expression with the windowed signed loss; before R287 it was f0 * (-1500) / (2 E).
            AssertBits(r.NominalHz * -1000.0 / (2.0 * eReal), c.BareInertiaRocofHzPerS, $"bare RoCoF at scale {c.SyncInertiaScale}");
        }
        // At scale 1.0: E = 4 s x 10,000 MVA = 40,000 MW.s, so -0.625 Hz/s (it was -0.9375 Hz/s).
        Assert.Equal(-0.625, r.Cells.Single(c => c.SyncInertiaScale == 1.0).BareInertiaRocofHzPerS, 12);
    }

    [Fact]
    public void R287_the_export_keeps_the_legacy_key_and_adds_the_honest_names()
    {
        string export = Src("src", "GridSim.Core", "IO", "H0StudyExport.cs");
        Assert.Contains("largestLossMw = result.TotalExogenousLossMw", export);
        Assert.Contains("rocofSettingLossMw = result.RocofSettingLossMw", export);
        Assert.Contains("largestSingleLossMw = result.LargestSingleLossMw", export);
        string replay = Src("src", "GridSim.Core", "Replay", "ReplayValidator.cs");
        Assert.Contains("ev.RocofSettingLossMw()", replay);
    }
}
