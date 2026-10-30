using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.Gda;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R058 (October 2026): the passive plant (delayed OLTC, line/transformer thermal derate) was authored and bound by
/// data/gb-full but never run. These tests pin the four defects that would have bitten once it was wired (starting
/// tap, step direction for a To-side controlled bus, compounding derate, branch identity with shared names) and the
/// wiring itself: opt-in in LiveSim and the GDA replay loop, with a run that binds no passive plant bit-identical
/// to a run with the option off (the reduction gate).
/// </summary>
public class PassivePlantWiringTests
{
    private static readonly DateTime Now = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Dictionary<string, AssetClassProvenance> Prov = new()
    {
        [AssetClass.DefaultKey] = new(ProvenanceTag.CuratedPlaceholder, "test OLTC/thermal constants [VERIFY]"),
    };

    // An OLTC class with short delays so a test sees a step within a few ticks.
    private static AssetClass Oltc(double derateFull = 0.1, double minTap = 0.9, double maxTap = 1.1) => new(
        "tx-test", AssetKind.Transformer, "on-load-tap-changer",
        new Dictionary<string, double>
        {
            [AssetParams.TargetVpu] = 1.0,
            [AssetParams.TapStepPu] = 0.0125,
            [AssetParams.TapDeadbandPu] = 0.01,
            [AssetParams.MinTap] = minTap,
            [AssetParams.MaxTap] = maxTap,
            [AssetParams.InterTapDelayS] = 5.0,
            [AssetParams.InitialTapDelayS] = 10.0,
            [AssetParams.ThermalTauMin] = 1.0,
            [AssetParams.DerateAtFullThermal] = derateFull,
        }, Prov);

    private static AssetClass Cable() => new(
        "cable-test", AssetKind.Line, "dynamic-thermal-rating",
        new Dictionary<string, double>
        {
            [AssetParams.ThermalTauMin] = 1.0,
            [AssetParams.DerateAtFullThermal] = 0.2,
        }, Prov);

    private static AssetDynamicsModel Bundle(params AssetBinding[] rules)
        => new(new AssetClassLibrary(new[] { Oltc(), Cable() }), new AssetBindingSet(rules));

    private static AssetDynamicsModel TxAndLineBound()
        => Bundle(new AssetBinding("transformer", "tx-test"), new AssetBinding("line", "cable-test"));

    /// <summary>
    /// A three-bus radial case: a 1.0 pu slack feeds bus 2 through a transformer T1 (From 1, To 2) and bus 3 hangs
    /// off bus 2 on line L23. The load sags bus 2 below the OLTC deadband, so a To-side OLTC must LOWER its tap
    /// (V_to ~ V_from / a) to bring it back.
    /// </summary>
    private static GridModel ThreeBus(AssetDynamicsModel? dyn, double tap = 1.0, IReadOnlyList<Oltc>? oltcs = null)
        => new()
        {
            BaseMva = 100,
            Buses = new[]
            {
                new Bus(1, BusType.Slack, Vm: 1.0, BaseKv: 132),
                new Bus(2, BusType.Pq, Pd: 40, Qd: 25, BaseKv: 33),
                new Bus(3, BusType.Pq, Pd: 20, Qd: 10, BaseKv: 33),
            },
            Generators = new[] { new Generator(1, 60, Vg: 1.0) },
            Branches = new[]
            {
                new Branch(1, 2, 0.005, 0.10, TapRatio: tap, RateAMva: 100, Name: "T1", IsTransformer: true),
                new Branch(2, 3, 0.02, 0.05, B: 0.001, RateAMva: 50, Name: "L23"),
            },
            Oltcs = oltcs ?? Array.Empty<Oltc>(),
            AssetDynamics = dyn,
        };

    // ---------------- defect 1: the starting tap ----------------

    [Fact]
    public void Fleet_starts_each_transformer_at_its_branch_tap_not_at_nominal()
    {
        PassivePlantFleet fleet = PassivePlantFleet.Build(ThreeBus(TxAndLineBound(), tap: 0.975));
        // In-band voltage: the tap must not move, so the very first update must carry the branch's own 0.975.
        IReadOnlyList<BranchPlantState> u = fleet.Advance(1.0, 0.0, _ => 0.5, _ => 1.0);
        BranchPlantState t1 = u.Single(s => s.BranchName == "T1");
        Assert.Equal(0.975, t1.Update.TapRatio!.Value);
        // Reset returns to the branch tap too, not to 1.0.
        fleet.Reset();
        Assert.Equal(0.975, ((TransformerDynamics)fleet.Plant[0]).Tap);
    }

    [Fact]
    public void A_branch_tap_outside_the_class_range_fails_loud_at_build()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => PassivePlantFleet.Build(ThreeBus(TxAndLineBound(), tap: 1.2)));
        Assert.Contains("T1", ex.Message);
    }

    // ---------------- defect 2: the step direction ----------------

    [Fact]
    public void A_to_side_controlled_bus_that_runs_high_raises_the_tap_and_a_from_side_one_lowers_it()
    {
        var ctx = new AssetStepContext(50, 0, 0, DtSeconds: 1.0, ClockSeconds: 0, LocalVoltagePu: 1.05);
        var toSide = new TransformerDynamics(Oltc(), "T", initialTap: 1.0, controlsTappedEnd: false);
        var fromSide = new TransformerDynamics(Oltc(), "T", initialTap: 1.0, controlsTappedEnd: true);
        PlantStateUpdate uTo = default, uFrom = default;
        for (int s = 0; s < 10; s++) { uTo = toSide.Advance(in ctx); uFrom = fromSide.Advance(in ctx); }
        Assert.Equal(1.0125, uTo.TapRatio!.Value);     // V_to ~ V_from / a: raise a to pull a high To bus down
        Assert.Equal(0.9875, uFrom.TapRatio!.Value);   // V_from ~ a * V_to: lower a to pull a high From bus down
    }

    [Fact]
    public void The_fleet_takes_the_controlled_bus_from_the_oltc_record_else_the_to_bus()
    {
        // No Oltc record: the To bus (2) is controlled, so the untapped-end rule applies.
        var noRecord = PassivePlantFleet.Build(ThreeBus(TxAndLineBound()));
        Assert.False(((TransformerDynamics)noRecord.Plant[0]).ControlsTappedEnd);

        // An Oltc record that regulates the From bus (1): the tapped-end rule applies.
        var fromRecord = PassivePlantFleet.Build(ThreeBus(TxAndLineBound(), oltcs: new[] { new Oltc("T1", ControlsBus: 1) }));
        Assert.True(((TransformerDynamics)fromRecord.Plant[0]).ControlsTappedEnd);
    }

    [Fact]
    public void A_transformer_regulating_a_generator_bus_holds_its_tap_and_still_derates()
    {
        // Make bus 2 a PV bus (a generator holds its voltage at 1.03): no tap can move it, so regulating it would
        // climb one step per tick to the limit. The fleet must hold the tap and keep the thermal derate.
        GridModel pq = ThreeBus(TxAndLineBound());
        var m = pq.CopyWith(
            buses: pq.Buses.Select(b => b.Id == 2 ? b with { Type = BusType.Pv, Vm = 1.03 } : b).ToList(),
            generators: pq.Generators.Concat(new[] { new Generator(2, 10, Vg: 1.03) }).ToList());
        PassivePlantFleet fleet = PassivePlantFleet.Build(m);
        Assert.Equal(1, fleet.TapsHeldAtGeneratorBus);

        (GdaReplayResult r, List<Captured> steps) = Replay(m, passive: true);
        Assert.Equal(0, r.NonConverged);
        Assert.All(steps, s => Assert.Equal(1.0, s.Taps[0]));
        Assert.True(steps[^1].Ratings[0] < 100.0, "a held-tap transformer must still derate thermally");
    }

    [Fact]
    public void An_oltc_record_naming_a_bus_the_model_lacks_fails_loud()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => PassivePlantFleet.Build(ThreeBus(TxAndLineBound(), oltcs: new[] { new Oltc("T1", ControlsBus: 99) })));
        Assert.Contains("99", ex.Message);
    }

    [Fact]
    public void A_non_finite_controlled_bus_voltage_holds_the_tap_and_the_delay_timer()
    {
        var tx = new TransformerDynamics(Oltc(), "T", initialTap: 1.0, controlsTappedEnd: false);
        var high = new AssetStepContext(50, 0, 0, DtSeconds: 1.0, ClockSeconds: 0, LocalVoltagePu: 1.05);
        var unseen = high with { LocalVoltagePu = double.NaN };
        for (int s = 0; s < 9; s++) tx.Advance(in high);            // 9 s out of band, 1 s short of the delay
        for (int s = 0; s < 50; s++) Assert.Equal(1.0, tx.Advance(in unseen).TapRatio!.Value);   // held, timer frozen
        Assert.Equal(1.0125, tx.Advance(in high).TapRatio!.Value);  // the 10th observed second completes the delay
    }

    // ---------------- defect 3: the compounding derate ----------------

    [Fact]
    public void ApplyTo_reapplied_to_its_own_output_derates_from_the_original_rating()
    {
        GridModel m = ThreeBus(TxAndLineBound());
        PassivePlantFleet fleet = PassivePlantFleet.Build(m);
        // Fully loaded for long enough (60 x 10 s >> the 60 s tau) that the line reaches its 0.2 derate.
        IReadOnlyList<BranchPlantState> u = Array.Empty<BranchPlantState>();
        for (int s = 0; s < 60; s++) u = fleet.Advance(10.0, s * 10.0, _ => 1.0, _ => 1.0);

        GridModel once = PassivePlantFleet.ApplyTo(m, u);
        GridModel twice = PassivePlantFleet.ApplyTo(once, u);
        Branch l23Once = once.Branches.Single(b => b.Name == "L23");
        Branch l23Twice = twice.Branches.Single(b => b.Name == "L23");
        double expected = 50.0 * (1.0 - u.Single(s => s.BranchName == "L23").Update.RatingDerateFraction);
        Assert.Equal(expected, l23Once.RateAMva);
        Assert.Equal(l23Once.RateAMva, l23Twice.RateAMva);   // not 50 * (1 - d)^2
        Assert.Equal(once.Branches.Single(b => b.Name == "T1").RateAMva, twice.Branches.Single(b => b.Name == "T1").RateAMva);
    }

    // ---------------- branch identity with shared names ----------------

    [Fact]
    public void Same_named_transformers_on_different_buses_each_get_their_own_controller_and_update()
    {
        // gb-full has 58 transformer names shared by 133 branches; 11 of those groups join different buses.
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq), new Bus(3, BusType.Pq) },
            Generators = Array.Empty<Generator>(),
            Branches = new[]
            {
                new Branch(1, 2, 0, 0.1, RateAMva: 100, Name: "SGT x2", IsTransformer: true),
                new Branch(1, 3, 0, 0.1, RateAMva: 200, Name: "SGT x2", IsTransformer: true),
            },
            AssetDynamics = TxAndLineBound(),
        };
        PassivePlantFleet fleet = PassivePlantFleet.Build(m);
        Assert.Equal(2, fleet.Count);

        // Drive them apart: bus 2 high, bus 3 low, each seen by its own controller through the solved-state lookup.
        var r = new PowerFlowResult
        {
            Converged = true, Iterations = 1, MaxMismatch = 0,
            BusIds = new[] { 1, 2, 3 }, Vm = new[] { 1.0, 1.05, 0.95 }, VaDeg = new double[3],
            PInjMw = new double[3], QInjMvar = new double[3],
            LineFlows = new[] { new LineFlow(1, 2, 0, 0, 0, 0, 0, 0), new LineFlow(1, 3, 0, 0, 0, 0, 0, 0) },
            TotalLossMw = 0, TotalLossMvar = 0,
        };
        IReadOnlyList<BranchPlantState> u = Array.Empty<BranchPlantState>();
        for (int s = 0; s < 10; s++) u = fleet.Advance(1.0, s, r, m);
        GridModel applied = PassivePlantFleet.ApplyTo(m, u);
        Assert.Equal(1.0125, applied.Branches[0].TapRatio);   // To bus 2 high -> raise
        Assert.Equal(0.9875, applied.Branches[1].TapRatio);   // To bus 3 low -> lower
        Assert.Equal(100.0, applied.Branches[0].RateAMva);    // each keeps its own original rating (no flow, no derate)
        Assert.Equal(200.0, applied.Branches[1].RateAMva);
    }

    [Fact]
    public void An_oltc_class_bound_to_a_line_fails_loud()
    {
        AssetDynamicsModel wrong = Bundle(new AssetBinding("line", "tx-test"));
        var ex = Assert.Throws<InvalidOperationException>(() => PassivePlantFleet.Build(ThreeBus(wrong)));
        Assert.Contains("L23", ex.Message);
    }

    [Fact]
    public void Advancing_on_a_non_converged_solve_fails_loud()
    {
        GridModel m = ThreeBus(TxAndLineBound());
        PassivePlantFleet fleet = PassivePlantFleet.Build(m);
        var r = new PowerFlowResult
        {
            Converged = false, Iterations = 50, MaxMismatch = 1,
            BusIds = new[] { 1, 2, 3 }, Vm = new double[3], VaDeg = new double[3],
            PInjMw = new double[3], QInjMvar = new double[3],
            LineFlows = new[] { new LineFlow(1, 2, 0, 0, 0, 0, 0, 0), new LineFlow(2, 3, 0, 0, 0, 0, 0, 0) },
            TotalLossMw = 0, TotalLossMvar = 0,
        };
        Assert.Throws<InvalidOperationException>(() => fleet.Advance(1.0, 0.0, r, m));
    }

    [Fact]
    public void The_fleet_supersedes_the_solver_oltc_record_of_a_transformer_it_regulates()
    {
        GridModel m = ThreeBus(TxAndLineBound(), oltcs: new[] { new Oltc("T1", ControlsBus: 2), new Oltc("Other", ControlsBus: 3) });
        GridModel handed = PassivePlantFleet.Build(m).WithoutSupersededOltcs(m);
        Assert.Equal(new[] { "Other" }, handed.Oltcs.Select(o => o.Transformer).ToArray());
        // Nothing to supersede: the same instance back.
        GridModel plain = ThreeBus(TxAndLineBound());
        Assert.Same(plain, PassivePlantFleet.Build(plain).WithoutSupersededOltcs(plain));
    }

    [Fact]
    public void GR137_an_unbound_transformer_sharing_a_bound_ones_name_keeps_its_solver_oltc_record()
    {
        // Two transformers named T1 (real data repeats names); only the first is bound to a passive OLTC class (inline).
        // Each record is matched to its branch as the solver matches it (name and end bus): ControlsBus 2 -> the first T1,
        // ControlsBus 3 -> the second. Only the bound transformer's record is superseded; before GR-137 both were removed
        // by name, so the unbound T1 lost its regulation.
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[]
            {
                new Bus(1, BusType.Slack, Vm: 1.0, BaseKv: 132),
                new Bus(2, BusType.Pq, Pd: 40, Qd: 25, BaseKv: 33),
                new Bus(3, BusType.Pq, Pd: 20, Qd: 10, BaseKv: 11),
            },
            Generators = new[] { new Generator(1, 60, Vg: 1.0) },
            Branches = new[]
            {
                new Branch(1, 2, 0.005, 0.10, RateAMva: 100, Name: "T1", IsTransformer: true, DynamicsClass: "tx-test"),
                new Branch(2, 3, 0.005, 0.10, RateAMva: 50, Name: "T1", IsTransformer: true),
            },
            Oltcs = new[] { new Oltc("T1", ControlsBus: 2), new Oltc("T1", ControlsBus: 3) },
            AssetDynamics = Bundle(),
        };
        PassivePlantFleet fleet = PassivePlantFleet.Build(m);
        Assert.Equal(1, fleet.Count);
        GridModel handed = fleet.WithoutSupersededOltcs(m);
        Oltc kept = Assert.Single(handed.Oltcs);
        Assert.Equal(3, kept.ControlsBus);
    }

    // ---------------- wiring: the GDA replay loop ----------------

    private sealed record Captured(double[] Vm, double[] Va, double LossMw, double[] Taps, double[] Ratings);

    private static (GdaReplayResult R, List<Captured> Steps) Replay(GridModel m, bool passive, int steps = 6, double stepMin = 30)
    {
        GdaTimeSeries series = GdaTimeSeries.Synthetic("t", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), steps, stepMin);
        var cap = new List<Captured>();
        GdaReplayResult r = GdaReplayLoop.Replay(m, series, utcNow: Now, passivePlant: passive,
            onSolved: (_, _, solved, pf, _) => cap.Add(new Captured(
                pf.Vm.ToArray(), pf.VaDeg.ToArray(), pf.TotalLossMw,
                solved.Branches.Select(b => b.TapRatio).ToArray(), solved.Branches.Select(b => b.RateAMva).ToArray())));
        return (r, cap);
    }

    private static void AssertBitIdentical(List<Captured> a, List<Captured> b)
    {
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Vm, b[i].Vm);
            Assert.Equal(a[i].Va, b[i].Va);
            Assert.Equal(a[i].LossMw, b[i].LossMw);
            Assert.Equal(a[i].Taps, b[i].Taps);
            Assert.Equal(a[i].Ratings, b[i].Ratings);
        }
    }

    [Fact]
    public void Replay_with_passive_plant_on_but_nothing_bound_is_bit_identical_to_off()
    {
        // Active-only bindings: the model carries an asset bundle, but no branch is bound.
        GridModel m = ThreeBus(Bundle(new AssetBinding("wind", "tx-test")));
        AssertBitIdentical(Replay(m, passive: false).Steps, Replay(m, passive: true).Steps);
        // And with no bundle at all.
        GridModel bare = ThreeBus(null);
        AssertBitIdentical(Replay(bare, passive: false).Steps, Replay(bare, passive: true).Steps);
    }

    [Fact]
    public void Replay_of_gb_synthetic_with_passive_plant_on_is_bit_identical_to_off()
    {
        // gb-synthetic binds only active plant (wind, solar, storage), so its passive fleet is empty.
        GridModel m = JsonGridLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "data", "gb-synthetic"));
        Assert.True(PassivePlantFleet.Build(m).IsEmpty);
        AssertBitIdentical(Replay(m, passive: false, steps: 3).Steps, Replay(m, passive: true, steps: 3).Steps);
    }

    [Fact]
    public void Replay_with_bound_passive_plant_moves_the_tap_toward_target_and_derates_the_rating()
    {
        GridModel m = ThreeBus(TxAndLineBound());
        (GdaReplayResult off, List<Captured> offSteps) = Replay(m, passive: false);
        (GdaReplayResult on, List<Captured> onSteps) = Replay(m, passive: true);
        Assert.Equal(0, off.NonConverged);
        Assert.Equal(0, on.NonConverged);

        // Step 0 has no observation yet: identical to off.
        Assert.Equal(offSteps[0].Vm, onSteps[0].Vm);
        // Bus 2 (index 1) sags below the deadband with the tap at 1.0; the To-side OLTC lowers the tap one step per
        // 30-minute tick (each tick far exceeds the mechanical delay), so bus 2 rises toward 1.0 pu.
        Assert.True(offSteps[0].Vm[1] < 0.99, $"fixture must sag bus 2 below the deadband: {offSteps[0].Vm[1]}");
        Assert.Equal(1.0 - 0.0125, onSteps[1].Taps[0]);
        Assert.True(onSteps[1].Vm[1] > offSteps[1].Vm[1], "lowering a To-side tap must raise the To bus");
        Assert.True(onSteps[1].Ratings[1] < 50.0, "the loaded line's rating must be derated after a tick");
        Assert.All(offSteps, s => Assert.Equal(1.0, s.Taps[0]));
    }

    [Fact]
    public void Replay_with_bound_passive_plant_is_deterministic()
    {
        GridModel m = ThreeBus(TxAndLineBound());
        AssertBitIdentical(Replay(m, passive: true).Steps, Replay(m, passive: true).Steps);
    }

    // ---------------- wiring: the live loop ----------------

    private static List<SimFrame> Live(GridModel m, bool passive, int ticks = 5)
    {
        var sim = new LiveSim(m, holdSteady: true, dynamicsConfig: new DynamicsConfig { EnablePassivePlant = passive });
        var frames = new List<SimFrame>();
        for (int t = 0; t < ticks; t++) frames.Add(sim.Step(t, 1.0));
        return frames;
    }

    [Fact]
    public void LiveSim_with_passive_plant_on_but_nothing_bound_is_bit_identical_to_off()
    {
        GridModel m = ThreeBus(null);
        List<SimFrame> off = Live(m, passive: false), on = Live(m, passive: true);
        for (int i = 0; i < off.Count; i++)
        {
            Assert.Equal(off[i].Result.Vm, on[i].Result.Vm);
            Assert.Equal(off[i].Result.VaDeg, on[i].Result.VaDeg);
            Assert.Equal(off[i].FrequencyHz, on[i].FrequencyHz);
        }
        Assert.Null(new LiveSim(m, holdSteady: true, dynamicsConfig: new DynamicsConfig { EnablePassivePlant = true }).PassivePlant);
    }

    [Fact]
    public void LiveSim_with_bound_passive_plant_steps_the_tap_after_the_mechanical_delay()
    {
        GridModel m = ThreeBus(TxAndLineBound());
        List<SimFrame> on = Live(m, passive: true);
        // 1-minute ticks: each exceeds the 10 s initial delay, so tick 1 solves on the first lowered tap.
        Assert.Equal(1.0, on[0].Model.Branches[0].TapRatio);
        Assert.Equal(1.0 - 0.0125, on[1].Model.Branches[0].TapRatio);
        List<SimFrame> off = Live(m, passive: false);
        Assert.All(off, f => Assert.Equal(1.0, f.Model.Branches[0].TapRatio));
    }
}
