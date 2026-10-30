using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.Events;
using GridSim.Core.Model;
using GridSim.Core.Replay;
using GridSim.Core.Scenarios;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The H=0 counterfactual engine: it refuses to run unless the baseline reproduces the record; it
/// collapses to the scalar swing when no fleet acts; |bare RoCoF| rises monotonically as real inertia
/// falls; and the headline bifurcation is REAL - at H=0 an all-GFM fleet FORMS a bounded RoCoF (holds)
/// while an all-GFL fleet is forming-dependent (NaN sentinel) and cascades. Plus the transform-separation
/// invariants: SetGfmFraction never mutates an H; ScaleSynchronousInertia never mutates a binding; the
/// virtual former Ev is never summed into real E.
/// </summary>
public class H0StudyEngineTests
{
    // --- builders ---
    private static GridEvent SelfConsistent(double inertiaMwS = 155_000, double demandMw = 32_000, double lossMw = 1320)
    {
        var cfg = new DynamicsConfig();
        var ev = new[] { new FrequencyEvent(1.0, -lossMw) };
        FrequencyResponse truth = FrequencyDynamics.Simulate(inertiaMwS, demandMw, ev, cfg);
        return new GridEvent(
            "syn", "self-consistent", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            inertiaMwS, demandMw,
            new List<PowerStep> { new(1.0, -lossMw, "loss", ProvenanceTag.Synthetic) },
            new List<RecordedAction>(),
            new RecordedOutcome(truth.InitialRocofHzPerS, truth.NadirHz, truth.SettleHz, 0, null, ProvenanceTag.Synthetic),
            ProvenanceTag.Synthetic);
    }

    private static GridModel Model(bool withConverters = true, double gfmSyn = 1600.0, double gfmDamp = 200.0)
    {
        var gens = new List<Generator>
        {
            new(1, Pg: 20_000, MBase: 10_000, H: 4.0, Name: "sync", Type: "ccgt"),   // E = 40 000 MW.s at scale 1
        };
        if (withConverters)
        {
            gens.Add(new Generator(1, Pg: 2_000, MBase: 3_000, H: 0, Name: "w1", Type: "wind"));
            gens.Add(new Generator(1, Pg: 1_500, MBase: 2_500, H: 0, Name: "w2", Type: "wind"));
            gens.Add(new Generator(1, Pg: 800, MBase: 1_500, H: 0, Name: "s1", Type: "storage"));
            gens.Add(new Generator(1, Pg: 600, MBase: 1_000, H: 0, Name: "s2", Type: "storage"));
        }
        AssetClass gfl = AssetTestHelpers.GflClass("gfl-test", rocofTripHzPerS: 1.0);
        AssetClass gfm = AssetTestHelpers.GfmClass("gfm-test", synInertiaMwsPerHz: gfmSyn, dampingMwPerHz: gfmDamp);
        AssetDynamicsModel bundle = AssetTestHelpers.Bundle(
            new[] { gfl, gfm },
            new AssetBinding("wind*", "gfl-test"), new AssetBinding("storage", "gfm-test"));
        return new GridModel
        {
            BaseMva = 100,
            Buses = new List<Bus> { new(1, BusType.Slack, BaseKv: 400, Name: "GB") },
            Generators = gens,
            Branches = Array.Empty<Branch>(),
            AssetDynamics = bundle,
        };
    }

    private static H0StudySpec Spec(GridModel model, IReadOnlyList<double> scales, IReadOnlyList<double> fractions,
        IReadOnlyList<Corner> corners, GridEvent? ev = null) => new(
        ev ?? SelfConsistent(), model, scales, fractions, corners, "gfm-test", "gfl-test",
        new DynamicsConfig { EnableAssetDynamics = true }, ReplayTolerance.Default);

    // --- gate ---
    [Fact]
    public void The_gate_throws_when_the_baseline_is_not_reproduced()
    {
        GridEvent bad = SelfConsistent() with { };
        bad = bad with { Outcome = bad.Outcome with { NadirHz = bad.Outcome.NadirHz + 3.0 } };
        H0StudySpec spec = Spec(Model(), new[] { 1.0 }, new[] { 0.0 }, new[] { Corner.Nominal }, bad);
        Assert.Throws<BaselineNotReproducedException>(() => H0StudyEngine.Run(spec));
    }

    // --- reduction to the scalar swing when no fleet acts ---
    [Fact]
    // GR-406: renamed from With_no_converter_fleet_the_cell_equals_the_scalar_swing_to_machine_epsilon: the name said machine epsilon; the assertions are precision 9. Tightening waits for a measured value.
    public void With_no_converter_fleet_the_cell_equals_the_scalar_swing_to_nine_decimals()
    {
        GridEvent ev = SelfConsistent();
        GridModel model = Model(withConverters: false);   // only the synchronous machine -> empty active fleet
        H0StudyResult r = H0StudyEngine.Run(Spec(model, new[] { 1.0 }, new[] { 0.0 }, new[] { Corner.Nominal }, ev));
        H0StudyCell cell = r.Cells.Single();

        // Rebuild the exact config the engine hands the cell and run the plain scalar swing.
        double lastLossAt = ev.ExogenousLossEvents().Max(l => l.AtSeconds);
        DynamicsConfig cfg = ev.ApplyRecordedServices(new DynamicsConfig { EnableAssetDynamics = true });
        cfg = cfg with { DurationSeconds = Math.Max(cfg.DurationSeconds, lastLossAt + 30.0) };
        cfg = ScenarioCorners.Apply(cfg, Corner.Nominal);
        double eReal = model.SystemInertiaGvaS * 1000.0;
        FrequencyResponse scalar = FrequencyDynamics.Simulate(eReal, ev.PreEventDemandMw, ev.ExogenousLossEvents(), cfg);

        Assert.Equal(scalar.InitialRocofHzPerS, cell.RocofHzPerS, 9);
        Assert.Equal(scalar.NadirHz, cell.NadirHz, 9);
        Assert.Equal(0, cell.TrippedCount);
    }

    // --- monotonicity of the analytic bare RoCoF as real inertia falls ---
    [Fact]
    public void Bare_inertia_rocof_rises_in_magnitude_as_sync_inertia_falls()
    {
        H0StudyResult r = H0StudyEngine.Run(Spec(Model(), new[] { 1.0, 0.5, 0.25 }, new[] { 0.0 }, new[] { Corner.Best }));
        var cells = r.Cells.OrderByDescending(c => c.SyncInertiaScale).ToList();
        for (int i = 1; i < cells.Count; i++)
        {
            Assert.True(cells[i].SyncInertiaScale < cells[i - 1].SyncInertiaScale);
            Assert.True(Math.Abs(cells[i].BareInertiaRocofHzPerS) > Math.Abs(cells[i - 1].BareInertiaRocofHzPerS),
                $"|bare RoCoF| should rise as inertia falls ({cells[i - 1].BareInertiaRocofHzPerS} -> {cells[i].BareInertiaRocofHzPerS})");
        }
    }

    // --- the headline bifurcation at H=0 (margin-based ordering, not brittle hard outcomes) ---
    [Fact]
    public void At_H0_all_GFL_is_forming_dependent_and_cascades_while_all_GFM_holds()
    {
        var fractions = new[] { 0.0, 0.25, 0.5, 0.75, 1.0 };
        H0StudyResult r = H0StudyEngine.Run(Spec(Model(), new[] { 0.0 }, fractions, new[] { Corner.Best }));

        H0StudyCell allGfl = r.Cells.Single(c => c.GfmFraction == 0.0);
        H0StudyCell allGfm = r.Cells.Single(c => c.GfmFraction == 1.0);

        // All-GFL at H=0: nothing forms the frequency -> forming-dependent, NaN sentinel, Loss-of-Mains cascade.
        Assert.True(allGfl.FormingDependent);
        Assert.True(double.IsNaN(allGfl.RocofHzPerS));
        Assert.True(double.IsNaN(allGfl.NadirHz));
        Assert.True(allGfl.CascadeLossOfMains);
        Assert.True(allGfl.TrippedCount > 0);

        // All-GFM at H=0: the virtual former holds -> a bounded, finite RoCoF, nadir clear of LFDD, no cascade.
        Assert.False(allGfm.FormingDependent);
        Assert.True(double.IsFinite(allGfm.RocofHzPerS));
        Assert.True(Math.Abs(allGfm.RocofHzPerS) < 2.0, $"formed RoCoF should be bounded, was {allGfm.RocofHzPerS}");
        Assert.True(allGfm.NadirHz > FrequencyLimits.LfddFirstStageHz, $"GFM should hold above LFDD, nadir {allGfm.NadirHz}");
        Assert.False(allGfm.CascadeLossOfMains);
        Assert.Equal(0, allGfm.TrippedCount);

        // Qualitative ordering with a margin: the formable cells' nadir does not DECREASE as the GFM fraction
        // rises (more grid-forming -> at least as well held). A documented margin absorbs governor interplay.
        const double margin = 0.25;   // Hz
        var formable = r.Cells.Where(c => !c.FormingDependent).OrderBy(c => c.GfmFraction).ToList();
        for (int i = 1; i < formable.Count; i++)
            Assert.True(formable[i].NadirHz >= formable[i - 1].NadirHz - margin,
                $"nadir should not fall as GFM rises ({formable[i - 1].GfmFraction}:{formable[i - 1].NadirHz} -> {formable[i].GfmFraction}:{formable[i].NadirHz})");
    }

    // --- the virtual former is never summed into real inertia (separation on the result) ---
    [Fact]
    public void Virtual_former_inertia_is_never_added_to_real_system_inertia()
    {
        H0StudyResult r = H0StudyEngine.Run(Spec(Model(), new[] { 0.0 }, new[] { 1.0 }, new[] { Corner.Best }));
        H0StudyCell cell = r.Cells.Single();
        Assert.Equal(0.0, cell.SystemInertiaGvaS);                                   // real inertia is exactly 0 at H=0
        Assert.True(cell.VirtualInertiaMwS > 0.0);                                   // ...but a virtual former exists
        Assert.Equal(cell.VirtualInertiaMwS, cell.EffectiveFormingInertiaMwS, 6);    // and it lives ONLY in the forming denominator
    }

    // --- transform-level separation invariants (critique fix g) ---
    [Fact]
    public void SetGfmFraction_never_mutates_an_H_and_tags_the_whole_converter_fleet_at_the_extremes()
    {
        GridModel m = Model();
        GridModel all = GridInertiaTransforms.SetGfmFraction(m, 1.0, "gfm-test", "gfl-test");
        GridModel none = GridInertiaTransforms.SetGfmFraction(m, 0.0, "gfm-test", "gfl-test");

        // No H is ever touched by the GFM-fraction transform (it rewrites bindings only).
        for (int i = 0; i < m.Generators.Count; i++)
        {
            Assert.Equal(m.Generators[i].H, all.Generators[i].H);
            Assert.Equal(m.Generators[i].H, none.Generators[i].H);
        }
        // Every converter (H==0) is grid-forming at fraction 1, grid-following at fraction 0; synchronous untouched.
        foreach (Generator g in all.Generators.Where(g => g.H == 0)) Assert.Equal("gfm-test", g.DynamicsClass);
        foreach (Generator g in none.Generators.Where(g => g.H == 0)) Assert.Equal("gfl-test", g.DynamicsClass);
        Assert.All(all.Generators.Where(g => g.H > 0), g => Assert.Null(g.DynamicsClass));
    }

    [Fact]
    public void ScaleSynchronousInertia_scales_only_real_inertia_and_never_a_binding()
    {
        GridModel tagged = GridInertiaTransforms.SetGfmFraction(Model(), 0.5, "gfm-test", "gfl-test");
        GridModel zero = GridInertiaTransforms.ScaleSynchronousInertia(tagged, 0.0);
        GridModel half = GridInertiaTransforms.ScaleSynchronousInertia(tagged, 0.5);

        Assert.Equal(0.0, zero.SystemInertiaGvaS);                       // scale 0 => a genuine H=0 grid
        Assert.Equal(tagged.SystemInertiaGvaS * 0.5, half.SystemInertiaGvaS, 9);
        for (int i = 0; i < tagged.Generators.Count; i++)
        {
            // Converters keep H==0 under any scale; every inline DynamicsClass survives unchanged.
            if (tagged.Generators[i].H == 0) Assert.Equal(0.0, zero.Generators[i].H);
            Assert.Equal(tagged.Generators[i].DynamicsClass, zero.Generators[i].DynamicsClass);
            Assert.Equal(tagged.Generators[i].DynamicsClass, half.Generators[i].DynamicsClass);
        }
    }

    [Fact]
    public void SetGfmFraction_fails_loud_on_an_unknown_or_non_forming_class()
    {
        GridModel m = Model();
        Assert.ThrowsAny<Exception>(() => GridInertiaTransforms.SetGfmFraction(m, 0.5, "no-such-class", "gfl-test"));
        // A grid-FOLLOWING class named as the GFM class is a hard error (it cannot form frequency).
        Assert.Throws<System.IO.InvalidDataException>(() => GridInertiaTransforms.SetGfmFraction(m, 0.5, "gfl-test", "gfl-test"));
    }
}
