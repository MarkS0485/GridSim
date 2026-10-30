using GridSim.Core.Assets;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N046 (October 2026, best-judgement ruling): the steady-state solver's instantaneous OLTC loop
///   - HOLDS the tap of an OLTC whose controlled bus is generator-regulated (PV or slack), as the passive plant does:
///     no tap position can move a bus an AVR holds, and regulating it ran the tap to its limit (on gb-full 71 taps
///     went from 1.0 to 1.0999999999999996);
///   - matches an OLTC record to its transformer by name AND end bus (the first same-named branch touching the
///     controlled bus), falling back to the first branch of the name only for a remote controlled bus.
/// </summary>
public class OltcGeneratorHeldTests
{
    private static readonly PowerFlowOptions Opt = new() { MaxIterations = 60, EnforceQLimits = false };

    // Slack 1 feeds bus 2 through transformer T1; bus 2 carries a generator holding 1.02 pu (PV), so an OLTC aimed at
    // bus 2 with a 1.0 +/- 0.01 pu target can never pull it into its band.
    private static GridModel PvControlled(BusType controlledType) => new()
    {
        BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, Vm: 1.0),
            new Bus(2, controlledType, Pd: 50, Qd: 10, Vm: 1.02),
            new Bus(3, BusType.Pq, Pd: 30, Qd: 10),
        },
        Generators = controlledType == BusType.Pv
            ? new[] { new Generator(1, 0), new Generator(2, 20, Vg: 1.02) }
            : new[] { new Generator(1, 0) },
        Branches = new[]
        {
            new Branch(1, 2, 0.005, 0.05, Name: "T1", IsTransformer: true),
            new Branch(2, 3, 0.02, 0.10, Name: "L23"),
        },
        Oltcs = new[] { new Oltc("T1", ControlsBus: 2, TargetVpu: 1.0, DeadbandPu: 0.01) },
    };

    [Fact]
    public void A_tap_whose_controlled_bus_is_generator_held_does_not_move()
    {
        GridModel m = PvControlled(BusType.Pv);
        var solver = new SteadyStateSolver();
        PowerFlowResult r = solver.Solve(m, Opt);
        Assert.True(r.Converged);
        Assert.True(solver.TapsSettled);
        Assert.Equal(0, solver.OltcPasses);
        Assert.Equal(1, solver.TapsHeldAtGeneratorBus);
        Assert.Equal(1.0, solver.RegulatedModel.Branches[0].TapRatio);
        Assert.Equal(1.02, r.Vm[1], 12);   // the AVR holds bus 2 regardless of the tap
    }

    [Fact]
    public void The_same_network_with_a_load_bus_still_regulates()
    {
        // Control case: bus 2 as PQ, so the OLTC is free to act and the hold rule does not apply.
        GridModel m = PvControlled(BusType.Pq);
        var solver = new SteadyStateSolver();
        PowerFlowResult r = solver.Solve(m, Opt);
        Assert.True(r.Converged);
        Assert.Equal(0, solver.TapsHeldAtGeneratorBus);
        Assert.True(solver.OltcPasses >= 1);
        Assert.NotEqual(1.0, solver.RegulatedModel.Branches[0].TapRatio);
        Assert.InRange(r.Vm[1], 0.99, 1.01);
    }

    [Fact]
    public void A_slack_controlled_bus_also_holds()
    {
        GridModel m = PvControlled(BusType.Pq);
        m = m.CopyWith(oltcs: new[] { new Oltc("T1", ControlsBus: 1, TargetVpu: 1.05, DeadbandPu: 0.01) });
        var solver = new SteadyStateSolver();
        solver.Solve(m, Opt);
        Assert.Equal(1, solver.TapsHeldAtGeneratorBus);
        Assert.Equal(0, solver.OltcPasses);
        Assert.Equal(1.0, solver.RegulatedModel.Branches[0].TapRatio);
    }

    // Two transformers share the name "SGT": the first joins 1-3, the second 1-2. The OLTC record regulates bus 2.
    private static GridModel SharedName(int controlsBus) => new()
    {
        BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, Vm: 1.0),
            new Bus(2, BusType.Pq, Pd: 80, Qd: 30),
            new Bus(3, BusType.Pq, Pd: 40, Qd: 15),
            new Bus(4, BusType.Pq, Pd: 10, Qd: 2),
        },
        Generators = new[] { new Generator(1, 0) },
        Branches = new[]
        {
            new Branch(1, 3, 0.008, 0.08, Name: "SGT", IsTransformer: true),
            new Branch(1, 2, 0.008, 0.08, Name: "SGT", IsTransformer: true),
            new Branch(2, 4, 0.02, 0.10, Name: "L24"),
        },
        Oltcs = new[] { new Oltc("SGT", ControlsBus: controlsBus, TargetVpu: 1.02, DeadbandPu: 0.01) },
    };

    [Fact]
    public void A_record_drives_the_same_named_transformer_that_touches_its_controlled_bus()
    {
        GridModel m = SharedName(controlsBus: 2);
        Assert.Equal(new[] { 1 }, SteadyStateSolver.MapOltcRecordsToBranches(m));   // name-only matching gave 0
        var solver = new SteadyStateSolver();
        PowerFlowResult r = solver.Solve(m, Opt);
        Assert.True(r.Converged);
        Assert.True(solver.TapsSettled);
        Assert.True(solver.OltcPasses >= 1);
        Assert.Equal(1.0, solver.RegulatedModel.Branches[0].TapRatio);      // 1-3 is not this record's transformer
        Assert.NotEqual(1.0, solver.RegulatedModel.Branches[1].TapRatio);   // 1-2 is, and it regulates bus 2
        Assert.InRange(r.Vm[1], 1.01, 1.03);
    }

    [Fact]
    public void A_remote_controlled_bus_keeps_the_first_branch_of_the_name()
    {
        // Bus 4 is on neither end of either "SGT": the historical first-of-name rule applies.
        Assert.Equal(new[] { 0 }, SteadyStateSolver.MapOltcRecordsToBranches(SharedName(controlsBus: 4)));
    }

    [Fact]
    public void A_record_naming_no_branch_maps_to_nothing()
    {
        GridModel m = SharedName(controlsBus: 2).CopyWith(oltcs: new[] { new Oltc("NOPE", ControlsBus: 2) });
        Assert.Equal(new[] { -1 }, SteadyStateSolver.MapOltcRecordsToBranches(m));
        var solver = new SteadyStateSolver();
        solver.Solve(m, Opt);
        Assert.Equal(0, solver.OltcPasses);
        Assert.Equal(0, solver.TapsHeldAtGeneratorBus);
    }

    [Fact]
    public void Gb_full_holds_its_71_generator_bus_taps_and_regulates_the_rest()
    {
        GridModel m = JsonGridLoader.LoadDirectory(Path.Combine(PresentationConstantsAntiDriftTests.RepoRoot(), "data", "gb-full"));
        var types = m.Buses.ToDictionary(b => b.Id, b => b.Type);
        int[] map = SteadyStateSolver.MapOltcRecordsToBranches(m);
        var solver = new SteadyStateSolver();
        PowerFlowResult r = solver.Solve(m);
        Assert.True(r.Converged);
        Assert.True(solver.TapsSettled);
        Assert.Equal(71, solver.TapsHeldAtGeneratorBus);
        int held = 0, moved = 0;
        for (int k = 0; k < m.Oltcs.Count; k++)
        {
            Assert.True(map[k] >= 0);
            double before = m.Branches[map[k]].TapRatio, after = solver.RegulatedModel.Branches[map[k]].TapRatio;
            if (types[m.Oltcs[k].ControlsBus] is BusType.Pv or BusType.Slack)
            {
                held++;
                Assert.Equal(before, after);   // used to run to 1.0999999999999996
            }
            else if (before != after) moved++;
        }
        Assert.Equal(71, held);
        Assert.Equal(936, moved);   // 1007 taps moved before N046, 71 of them at generator-held buses
    }

    [Fact]
    public void The_passive_plant_reads_its_controlled_bus_from_the_record_the_solver_matches()
    {
        // Source-level pin: the passive plant uses the solver's own matcher, so the two cannot drift.
        string src = File.ReadAllText(Path.Combine(PresentationConstantsAntiDriftTests.RepoRoot(),
            "src", "GridSim.Core", "Assets", "PassivePlantFleet.cs"));
        Assert.Contains("SteadyStateSolver.MapOltcRecordsToBranches(model)", src);
        Assert.DoesNotContain("firstIndexOfName", src);
    }
}
