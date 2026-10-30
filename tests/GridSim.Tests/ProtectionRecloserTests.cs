using System.Collections.Generic;
using GridSim.Core.Assets;
using GridSim.Core.Assets.Protection;
using GridSim.Core.Diagnostics;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Phase 3 - the topology-aware recloser: a recloser class (with an auto-reclose delay) OPENS its target
/// branch on its trip condition and RECLOSES after the delay; <see cref="TopologyOps"/> applies the open
/// branch to the model and partitions it (islanding/blackout). An open branch is a topology change, NOT an
/// infeed trip.
/// </summary>
public class ProtectionRecloserTests
{
    private static AssetClass RecloserClass()
        => new("recloser", AssetKind.Protection, "auto-recloser",
            new Dictionary<string, double>
            {
                [AssetParams.UfTripHz] = 49.0,
                [AssetParams.UfTripDwellS] = 0.0,
                [AssetParams.AutoRecloseDelayS] = 2.0,
            },
            new Dictionary<string, AssetClassProvenance> { [AssetClass.DefaultKey] = new(ProvenanceTag.CuratedPlaceholder, "[VERIFY]") });

    private static AssetStepContext Ctx(double hz, double clockS)
        => new(LocalHz: hz, LocalRocofHzPerS: 0, LocalPhaseDeg: 0, DtSeconds: 0.1, ClockSeconds: clockS);

    [Fact]
    public void Recloser_opens_its_branch_on_under_frequency_and_recloses_after_the_delay()
    {
        var c = new ProtectionController(RecloserClass(), bus: 2, capacityMw: 0, targetBranch: "L23");

        AssetActuation open = c.Update(Ctx(hz: 48.5, clockS: 0.0));   // < 49.0 -> opens L23
        Assert.Contains("L23", c.OpenBranches);
        Assert.False(open.TrippedNow, "opening a branch is a topology change, not an infeed trip");
        Assert.True(c.Tripped);       // the device has operated

        c.Update(Ctx(hz: 48.5, clockS: 1.0));            // within the 2 s reclose delay
        Assert.Contains("L23", c.OpenBranches);

        c.Update(Ctx(hz: 48.5, clockS: 2.0));            // reclose
        Assert.Empty(c.OpenBranches);
        Assert.False(c.Tripped);
    }

    [Fact]
    public void TopologyOps_applies_an_open_branch_and_partitions()
    {
        var buses = new List<Bus>
        {
            new(1, BusType.Slack, BaseKv: 132),
            new(2, BusType.Pq, Pd: 40, BaseKv: 132),
        };
        var branches = new List<Branch> { new(1, 2, 0.01, 0.1, Name: "L12") };
        var m = new GridModel { BaseMva = 100, Buses = buses, Generators = new List<Generator> { new(1, Pg: 40) }, Branches = branches };

        (GridModel adj, IslandReport rep) = TopologyOps.Apply(m, new HashSet<string> { "L12" });

        // Opening L12 islands bus 2 (load only) -> blacked out, its 40 MW load lost.
        Assert.Equal(1, rep.BlackedOutIslands);
        Assert.Equal(40.0, rep.LostLoadMw, 6);
        Assert.DoesNotContain(adj.Buses, b => b.Id == 2);
    }

    [Fact]
    public void No_open_branches_is_a_no_op()
    {
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new List<Bus> { new(1, BusType.Slack) },
            Generators = new List<Generator> { new(1, Pg: 10) },
            Branches = System.Array.Empty<Branch>(),
        };
        (GridModel adj, IslandReport rep) = TopologyOps.Apply(m, new HashSet<string>());
        Assert.False(rep.AnyChange);
        Assert.Same(m, adj);
    }
}
