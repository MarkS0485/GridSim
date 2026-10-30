using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>Static bug hunt of 2026-10-29 (GridSim 2.0.183), power-flow findings. See BugHunt1029DynamicsTests.cs.</summary>
public sealed partial class BugHunt1029Tests
{
    // ---------------------------------------------------------------------------------------------------------------
    // BH04: the island pre-pass must carry every coupled device, dropping (and naming) only those on dead buses.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Buses 1 slack+gen, 2 load, 3 gen (no slack), 4 load. Branch order L34, L12, L23 (so dropping L34 shifts the
    /// other two down one position). Opening L12 and L34 leaves island {1} with its slack, island {2,3} with a
    /// generator but no slack (bus 3 is elected) and island {4} with nothing (blacked out).
    /// </summary>
    private static GridModel BH04Model()
    {
        var buses = new List<Bus>
        {
            new(1, BusType.Slack, BaseKv: 132),
            new(2, BusType.Pq, Pd: 20, BaseKv: 132),
            new(3, BusType.Pv, BaseKv: 132),
            new(4, BusType.Pq, Pd: 30, BaseKv: 132),
        };
        var gens = new List<Generator>
        {
            new(1, Pg: 60, H: 4, MBase: 100, Name: "G1"),
            new(3, Pg: 40, H: 3, MBase: 100, Name: "G3"),
            new(2, Pg: 0, Name: "G2 off", InService: false),   // out of service on a LIVE bus: stays in the fleet
            new(4, Pg: 0, Name: "G4 off", InService: false),   // out of service on a DEAD bus: leaves with its island
        };
        var branches = new List<Branch>
        {
            new(3, 4, 0.01, 0.1, Name: "L34", InService: false),
            new(1, 2, 0.01, 0.1, Name: "L12", InService: false),
            new(2, 3, 0.01, 0.1, Name: "L23"),
        };
        return new GridModel
        {
            BaseMva = 100, Buses = buses, Generators = gens, Branches = branches,
            StaticVarCompensators = new[] { new StaticVarCompensator(2, -20, 20, 0, 0.99, 1.01, "SVC 2") },
            InductionMachines = new[]
            {
                new InductionMachine(2, "M2", InductionPowerCode.Mechanical, 5, 10, 0, 0.01, 0.1, 3.0, 0.01, 0.1),
                new InductionMachine(4, "M4", InductionPowerCode.Mechanical, 5, 10, 0, 0.01, 0.1, 3.0, 0.01, 0.1),
            },
            ZeroSequenceMutuals = new[]
            {
                new ZeroSequenceMutual(1, 1, 2, 2, 2, 3, 0.001, 0.01),   // L12 with L23: both kept, re-pointed to 0 and 1
                new ZeroSequenceMutual(0, 3, 4, 2, 2, 3, 0.001, 0.01),   // L34 with L23: L34 left, so the mutual leaves
            },
        };
    }

    [Fact]
    public void BH04_island_prepass_keeps_coupled_devices_and_names_the_ones_on_dead_buses()
    {
        GridModel model = BH04Model();
        (GridModel adj, IslandReport rep) = IslandPartition.Prepare(model);

        Assert.Contains(3, rep.ElectedSlackBuses);
        Assert.Contains(4, rep.BlackedOutBuses);

        // Before the fix every one of these collections came back empty (the copy was hand-built).
        StaticVarCompensator svc = Assert.Single(adj.StaticVarCompensators);
        Assert.Equal("SVC 2", svc.Name);
        InductionMachine m = Assert.Single(adj.InductionMachines);
        Assert.Equal("M2", m.Name);
        Assert.True(adj.HasCoupledDevices);

        // The mutual that survives is re-pointed at its branches' new positions (L12 -> 0, L23 -> 1).
        ZeroSequenceMutual zm = Assert.Single(adj.ZeroSequenceMutuals);
        Assert.Equal(0, zm.BranchA);
        Assert.Equal(1, zm.BranchB);
        Assert.Equal("L12", adj.Branches[zm.BranchA].Name);
        Assert.Equal("L23", adj.Branches[zm.BranchB].Name);

        // Every device that left is named, never dropped silently.
        Assert.Equal(new[] { "induction machine 'M4'", "zero-sequence mutual between branches 0 and 2" }, rep.DroppedDevices);

        // The fleet: the out-of-service unit on the dead bus left; the one on the live bus is kept.
        Assert.Equal(new[] { "G1", "G3", "G2 off" }, adj.AllGenerators.Select(g => g.Name));
        Assert.Equal(new[] { "G1", "G3" }, adj.Generators.Select(g => g.Name));
        Assert.Equal(1, adj.DroppedGeneratorRows);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // BH05: a bank with unequal blocks that steps to 0 adds its step-0 level (0 only at InitialSteps).
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void BH05_a_bank_with_unequal_blocks_solved_at_step_0_carries_its_step_0_susceptance()
    {
        // IEEE case 9, a bank at load bus 5 with three levels: step 0 a 10 Mvar reactor, step 1 nothing (the initial
        // step), step 2 a 15 Mvar capacitor. The band 0.80..0.85 pu is far below bus 5's voltage, so the bank switches OUT
        // to step 0 and stops there at the end of its range. The network solved must then be the case with bus 5's Bs
        // lowered by 10 Mvar; before the fix step 0 was skipped as "adds nothing" and the reactor was never in the solve.
        GridModel m = IeeeCases.Case9();
        var bank = new SwitchedShunt(5, 1.0, 0, 2, 1, 0.80, 0.85, "B5") { StepLevelsMvar = new[] { -10.0, 0.0, 15.0 } };
        var engine = new NewtonRaphsonPowerFlow();
        var ss = new SwitchedShuntPowerFlow(engine, new[] { bank });
        PowerFlowResult r = ss.Solve(m);
        Assert.Equal(0, Assert.Single(ss.LastSteps));

        GridModel withReactor = m.CopyWith(buses: m.Buses.Select(b => b.Id == 5 ? b with { Bs = b.Bs - 10.0 } : b).ToList());
        PowerFlowResult expected = new NewtonRaphsonPowerFlow().Solve(withReactor);
        Assert.Equal(expected.Vm, r.Vm);
        Assert.Equal(expected.VaDeg, r.VaDeg);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // BH06: a VSC converter in AC voltage control on the slack bus is refused.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void BH06_a_vsc_converter_holding_the_slack_bus_voltage_is_refused()
    {
        // Bus 1 is the slack with no generator of its own; the VSC's first converter sits there in AC voltage control.
        var vsc = new VscDcLink("V1", 1.0,
            new VscConverter(1, VscDcControl.DcVoltage, VscAcControl.Voltage, 300.0, 1.0, 0, 0, 0, 0, 0, 1.0, 50, -50),
            new VscConverter(3, VscDcControl.Power, VscAcControl.PowerFactor, 20.0, 1.0, 0, 0, 0, 0, 0, 1.0, 50, -50));
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pv, BaseKv: 132), new Bus(3, BusType.Pq, Pd: 30, BaseKv: 132) },
            Generators = new[] { new Generator(2, Pg: 40, Name: "G2") },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1, Name: "L12"), new Branch(2, 3, 0.01, 0.1, Name: "L23"), new Branch(1, 3, 0.01, 0.1, Name: "L13") },
            VscLinks = new[] { vsc },
        };
        var ex = Assert.ThrowsAny<InvalidOperationException>(() => new NewtonRaphsonPowerFlow().Solve(m));
        Assert.Contains("is the slack bus and the converter is in AC voltage control", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // BH07: a NaN-seeded warm start ends the dense engine as not converged, like the sparse engine, not an exception.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void BH07_a_nan_warm_start_is_not_converged_on_both_engines_instead_of_an_internal_error()
    {
        GridModel c9 = IeeeCases.Case9();
        GridModel nan = c9.CopyWith(buses: c9.Buses.Select(b => b.Id == 5 ? b with { Vm = double.NaN } : b).ToList());
        var opt = new PowerFlowOptions { FlatStart = false };
        PowerFlowResult dense = new NewtonRaphsonPowerFlow().Solve(nan, opt);
        PowerFlowResult sparse = new SparseNewtonRaphsonPowerFlow().Solve(nan, opt);
        Assert.False(dense.Converged);
        Assert.False(sparse.Converged);
        Assert.Equal(sparse.Status, dense.Status);
    }

    [Fact]
    public void BH04_island_prepass_copy_carries_every_other_model_property()
    {
        // The copy goes through GridModel.CopyWith, so name, base, frequency and the rest arrive unchanged.
        GridModel model = BH04Model();
        GridModel named = model.CopyWith(name: "bh04", frequencyHz: 60.0);
        (GridModel adj, _) = IslandPartition.Prepare(named);
        Assert.Equal("bh04", adj.Name);
        Assert.Equal(60.0, adj.FrequencyHz);
        Assert.Equal(100.0, adj.BaseMva);
    }
}
