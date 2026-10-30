using System.Collections.Generic;
using System.Numerics;
using GridSim.Core.Assets;
using GridSim.Core.Assets.Protection;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using GridSim.Core.Protection.ShortCircuit;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// October 2026 review, WS6 protection group: R041 (distance and thermal elements no longer depend on branch
/// orientation), R232 (reclosers and placements fail loud), R284 (IEC 60909 positive/negative networks without
/// line charging or bus shunts) and R285 (singular fault networks fail loud on the dense path; branch fault
/// currents use the shared stamp).
/// </summary>
public class ProtectionWs6Tests
{
    // ---- R041 ----------------------------------------------------------------------------------------------

    private static GridModel Case9Reversed(int branchIndex)
    {
        GridModel m = IeeeCases.Case9();
        var br = m.Branches.ToList();
        Branch b = br[branchIndex];
        br[branchIndex] = b with { From = b.To, To = b.From };   // tap 1, no shift: electrically identical
        return m.CopyWith(branches: br);
    }

    private static HashSet<(int Branch, string Device)> OperatingDistance(GridModel m, int faultBus)
    {
        PowerFlowResult pre = new NewtonRaphsonPowerFlow().Solve(m);
        (_, ProtectionReport rep) = ProtectionAssessment.AssessThreePhaseFault(m, pre, faultBus);
        return rep.Verdicts.Where(v => v.Operate && v.Device.StartsWith("21", StringComparison.Ordinal))
            .Select(v => (v.LocationId, v.Device)).ToHashSet();
    }

    [Fact]
    public void Distance_verdicts_do_not_depend_on_branch_orientation()
    {
        // Fault at bus 4. Branch 1 is 4-5, stored with the faulted bus on its From end: the From-end relay sees
        // S ~ 0 and no zone, while the relay at bus 5 sees a forward Zone 1 fault. Before the fix only the From
        // end was evaluated, so branch 1 showed no 21 as stored and Zone 1 when reversed.
        HashSet<(int, string)> asStored = OperatingDistance(IeeeCases.Case9(), 4);
        HashSet<(int, string)> reversed = OperatingDistance(Case9Reversed(1), 4);
        Assert.Contains(asStored, v => v.Item1 == 1);
        Assert.Equal(reversed.OrderBy(v => v).ToList(), asStored.OrderBy(v => v).ToList());
    }

    [Fact]
    public void Thermal_loading_uses_the_heavier_end()
    {
        // A crafted state: the To end carries 120 MVA on a 100 MVA line, the From end only 20 MVA.
        var m = new GridModel
        {
            Name = "thermal-ends",
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq) },
            Generators = new[] { new Generator(1, 0) },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1, RateAMva: 100, Name: "L") },
        };
        var r = new PowerFlowResult
        {
            Converged = true, Iterations = 2, MaxMismatch = 1e-10, BusIds = new[] { 1, 2 }, Vm = new[] { 1.0, 1.0 },
            VaDeg = new double[2], PInjMw = new double[2], QInjMvar = new double[2], TotalLossMw = 0, TotalLossMvar = 0,
            LineFlows = new[] { new LineFlow(1, 2, 20, 0, 0, -120, 20, -120, "L", 100, 120) },
        };
        var ctx = new ProtectionContext(m, r, new ProtectionSettings(), null);
        Assert.Equal(1.2, ctx.LoadingFraction[0], 12);
        Assert.Equal(1.0 / 1.2, ctx.ZAppToMagPu[0], 12);   // |V|^2/|S_to| at the to end
        Assert.Equal(1.0 / 0.2, ctx.ZAppMagPu[0], 12);     // from end unchanged
    }

    // ---- R232 ----------------------------------------------------------------------------------------------

    // Moved 2026-10 (B4 R137, Ruling R1: Mark delegated EE calls 2026-10-04; source EREC G99 Issue 2 s.10.6.7.1 Table
    // 10.1, every protection function has a time setting): a protection class must now author the dwell of each stage.
    // Before: no ufTripDwellS (read as 0 s). After: ufTripDwellS = 0.0 written out, the same 0 s the controller read, so
    // every behaviour these tests check is unchanged.
    private static AssetClass Cls(string name, bool recloser) => new(name, AssetKind.Protection, "test",
        recloser
            ? new Dictionary<string, double> { [AssetParams.UfTripHz] = 49.0, [AssetParams.UfTripDwellS] = 0.0, [AssetParams.AutoRecloseDelayS] = 2.0 }
            : new Dictionary<string, double> { [AssetParams.UfTripHz] = 49.0, [AssetParams.UfTripDwellS] = 0.0 },
        new Dictionary<string, AssetClassProvenance> { [AssetClass.DefaultKey] = new(ProvenanceTag.CuratedPlaceholder, "[VERIFY]") });

    [Fact]
    public void A_recloser_with_no_target_branch_fails_loud()
    {
        var ex = Assert.Throws<InvalidDataException>(() => new ProtectionController(Cls("rc", true), bus: 2, capacityMw: 0));
        Assert.Contains("TargetBranch", ex.Message);
        // A non-recloser needs no target.
        _ = new ProtectionController(Cls("uf", false), bus: 2, capacityMw: 100);
    }

    private static ProtectionModel Placement(params ProtectionInstance[] instances)
        => new(new AssetClassLibrary(new[] { Cls("rc", true), Cls("uf", false) }), instances);

    [Fact]
    public void A_valid_placement_passes()
    {
        GridModel m = IeeeCases.Case9().CopyWith(branches: IeeeCases.Case9().Branches.Select((b, i) => b with { Name = $"B{i}" }).ToList());
        Placement(new ProtectionInstance("r1", "rc", 4, 0, "B1"), new ProtectionInstance("u1", "uf", 5, 100)).ValidateAgainst(m);
    }

    [Theory]
    [InlineData("rc", 4, null, "names no TargetBranch")]
    [InlineData("rc", 4, "NOPE", "targets branch 'NOPE'")]
    [InlineData("uf", 4, "B1", "is not a recloser")]
    [InlineData("uf", 99, null, "monitors bus 99")]
    [InlineData("missing", 4, null, "not in the class library")]
    public void An_invalid_placement_fails_loud(string cls, int bus, string? target, string expected)
    {
        GridModel m = IeeeCases.Case9().CopyWith(branches: IeeeCases.Case9().Branches.Select((b, i) => b with { Name = $"B{i}" }).ToList());
        var ex = Assert.Throws<InvalidDataException>(() => Placement(new ProtectionInstance("dev", cls, bus, 0, target)).ValidateAgainst(m));
        Assert.Contains(expected, ex.Message);
    }

    // ---- R284 ----------------------------------------------------------------------------------------------

    [Fact]
    public void Iec60909_networks_neglect_line_charging_and_bus_shunts()
    {
        // case9 with a capacitor bank at bus 4: the 60909 Z1/Z2 must equal those of the same network with every
        // B and Gs/Bs removed, bit-for-bit (the standard neglects both), and must differ from the operating-point
        // network that keeps them.
        GridModel m = IeeeCases.Case9();
        var buses = m.Buses.Select(b => b.Id == 4 ? b with { Bs = 50, Gs = 1 } : b).ToList();
        GridModel withShunts = m.CopyWith(buses: buses);
        GridModel stripped = m.CopyWith(
            buses: m.Buses.Select(b => b with { Gs = 0, Bs = 0 }).ToList(),
            branches: m.Branches.Select(b => b with { B = 0 }).ToList());

        (Complex z1, _) = Iec60909.PositiveSequenceThevenin(withShunts, 5);
        (Complex z1s, _) = Iec60909.PositiveSequenceThevenin(stripped, 5);
        Assert.Equal(z1s, z1);
        (Complex z2, _) = Iec60909.NegativeSequenceThevenin(withShunts, 5);
        (Complex z2s, _) = Iec60909.NegativeSequenceThevenin(stripped, 5);
        Assert.Equal(z2s, z2);
        Assert.Equal(Iec60909.ThreePhase(stripped, 5).IkppPu, Iec60909.ThreePhase(withShunts, 5).IkppPu);
    }

    // ---- R285 ----------------------------------------------------------------------------------------------

    [Fact]
    public void Branch_fault_currents_handle_a_tap_ratio_of_zero_as_unity()
    {
        GridModel m = IeeeCases.Case9();
        var zeroTap = m.Branches.Select((b, i) => i == 0 ? b with { TapRatio = 0.0 } : b).ToList();
        IReadOnlyList<Iec60909.BranchFaultCurrent> unity = Iec60909.BranchFaultCurrents(m, 5);
        IReadOnlyList<Iec60909.BranchFaultCurrent> zero = Iec60909.BranchFaultCurrents(m.CopyWith(branches: zeroTap), 5);
        Assert.Equal(unity.Count, zero.Count);
        for (int i = 0; i < unity.Count; i++)
        {
            Assert.True(double.IsFinite(zero[i].FromEndKa) && double.IsFinite(zero[i].ToEndKa));
            Assert.Equal(unity[i].FromEndKa, zero[i].FromEndKa);
            Assert.Equal(unity[i].ToEndKa, zero[i].ToEndKa);
        }
    }

    [Fact]
    public void A_fault_on_a_sourceless_island_fails_loud_on_the_dense_path()
    {
        // Buses 10-11 form an island with no source and no earth path: its admittance block is singular. The
        // dense factor used to clamp the zero pivot and return a finite, meaningless answer; it must now throw
        // the same "singular" error the sparse path throws.
        GridModel m = IeeeCases.Case9();
        var buses = m.Buses.Concat(new[] { new Bus(10, BusType.Pq, BaseKv: 345), new Bus(11, BusType.Pq, BaseKv: 345) }).ToList();
        var branches = m.Branches.Concat(new[] { new Branch(10, 11, 0.01, 0.1) }).ToList();
        GridModel island = m.CopyWith(buses: buses, branches: branches);
        var ex = Assert.Throws<InvalidOperationException>(() => Iec60909.ThreePhase(island, 10));
        Assert.Contains("singular", ex.Message);
    }

    [Fact]
    public void A_fault_in_the_live_network_still_solves_when_a_dead_island_is_present()
    {
        // The island's rows carry no injection when the fault is elsewhere, so Y x = b is satisfied exactly and the
        // result matches the network without the island.
        GridModel m = IeeeCases.Case9();
        var buses = m.Buses.Concat(new[] { new Bus(10, BusType.Pq, BaseKv: 345), new Bus(11, BusType.Pq, BaseKv: 345) }).ToList();
        var branches = m.Branches.Concat(new[] { new Branch(10, 11, 0.01, 0.1) }).ToList();
        GridModel island = m.CopyWith(buses: buses, branches: branches);
        Assert.Equal(Iec60909.ThreePhase(m, 5).IkppPu, Iec60909.ThreePhase(island, 5).IkppPu, 9);
    }
}
