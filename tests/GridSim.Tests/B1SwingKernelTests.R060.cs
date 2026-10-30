using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R060: the distributed model places each frequency event where it happens. Before, every event landed on the speed
/// domain with the largest summed rated MVA (documented as "the domain hosting the single largest unit", which it was
/// not), so the per-domain nadirs, RoCoFs and per-bus RoCoF map ignored which unit tripped. Now: an event at a bus lands
/// on the domain whose machines are there, or is shared by synchronising coupling to the impact bus (Anderson and Fouad,
/// distribution of a power impact); an unlocated event lands on the domain of the largest single unit.
/// </summary>
public sealed partial class B1SwingKernelTests
{
    // Three speed domains along a line: ccgt (bus 1) - load bus 2 - recip (bus 3) - hydro (bus 4).
    private static GridModel R060Chain(double x12 = 0.05, double x23 = 0.15) => new()
    {
        Name = "r060-chain",
        BaseMva = 100,
        Buses = new Bus[] { new(1, BusType.Slack), new(2, BusType.Pq), new(3, BusType.Pq), new(4, BusType.Pq) },
        Generators = new Generator[]
        {
            new(Bus: 1, Pg: 4000, MBase: 5000, H: 5.0, Type: "ccgt"),
            new(Bus: 3, Pg: 1500, MBase: 2000, H: 1.0, Type: "recip"),
            new(Bus: 4, Pg: 800, MBase: 1000, H: 3.0, Type: "hydro"),
        },
        Branches = new Branch[] { new(1, 2, R: 0.0, X: x12), new(2, 3, R: 0.0, X: x23), new(3, 4, R: 0.0, X: 0.05) },
    };

    private static readonly DynamicsConfig R060Cfg = new() { InertiaModel = InertiaModel.Distributed, DurationSeconds = 10 };

    [Fact]
    public void R060_an_unlocated_event_on_one_unit_per_domain_is_bit_identical_to_the_old_placement()
    {
        // One unit per domain: the largest single unit (ccgt, 5000 MVA) is also in the domain with the largest total
        // MVA, so the new rule picks the same domain as the old one and nothing moves.
        GridModel m = R060Chain();
        var loss = new[] { new FrequencyEvent(1.0, -300.0) };
        var dm = new DistributedInertiaModel();
        FrequencyResponse viaModel = dm.Simulate(m, 6300, loss, R060Cfg);

        var dist = new DistributedConfig();
        (IReadOnlyList<SpeedDomain> domains, _) = DomainAssignment.Assign(m, dist);
        List<SpeedDomain> masses = domains.Where(d => d.IsSwingMass).ToList();
        double[,] k = DomainCoupling.SynchronisingMatrix(m, masses, dist);
        MultiDomainSwing.Result legacy = MultiDomainSwing.Simulate(domains, k, new double[3, 3], 6300, loss, R060Cfg, dist);   // no shares: old rule
        AssertSameBits(legacy.Coi, viaModel, "COI");
        EventPlacement p = Assert.Single(dm.LastDistributedResult!.EventPlacements!);
        Assert.Equal(new[] { 1.0, 0.0, 0.0 }, p.Shares);
        Assert.Contains("largest single unit", p.Rule);
    }

    [Fact]
    public void R060_an_unlocated_event_lands_on_the_domain_of_the_largest_single_unit_not_the_largest_total()
    {
        // ccgt: one 1000 MVA unit. hydro: three 600 MVA units (1800 MVA in total). The old rule put the loss on hydro.
        GridModel m = new()
        {
            BaseMva = 100,
            Buses = new Bus[] { new(1, BusType.Slack), new(2, BusType.Pq), new(3, BusType.Pq), new(4, BusType.Pq) },
            Generators = new Generator[]
            {
                new(Bus: 1, Pg: 800, MBase: 1000, H: 5.0, Type: "ccgt"),
                new(Bus: 2, Pg: 400, MBase: 600, H: 3.0, Type: "hydro"),
                new(Bus: 3, Pg: 400, MBase: 600, H: 3.0, Type: "hydro"),
                new(Bus: 4, Pg: 400, MBase: 600, H: 3.0, Type: "hydro"),
            },
            Branches = new Branch[] { new(1, 2, R: 0.0, X: 0.05), new(2, 3, R: 0.0, X: 0.05), new(3, 4, R: 0.0, X: 0.05) },
        };
        var dm = new DistributedInertiaModel();
        dm.Simulate(m, 2000, new[] { new FrequencyEvent(1.0, -300.0) }, R060Cfg);
        DistributedResult r = dm.LastDistributedResult!;
        EventPlacement p = Assert.Single(r.EventPlacements!);
        Assert.Equal(2, r.Domains.Count);
        Assert.Equal(1.0, p.Shares[0]);   // domain 0 is the 2-pole (ccgt) domain: domains are ordered by pole count
        Assert.Equal(0.0, p.Shares[1]);
        Assert.Contains("1000 MVA at bus 1", p.Rule);
        // The domain that takes the step falls first: its peak RoCoF is the larger in magnitude.
        Assert.True(Math.Abs(r.Domains[0].InitialRocofHzPerS) > Math.Abs(r.Domains[1].InitialRocofHzPerS));
    }

    [Fact]
    public void R060_an_event_at_a_machine_bus_lands_wholly_on_that_machines_domain()
    {
        GridModel m = R060Chain();
        var dm = new DistributedInertiaModel();
        FrequencyResponse located = dm.Simulate(m, 6300, new[] { new FrequencyEvent(1.0, -300.0, AtBus: 4) }, R060Cfg);
        EventPlacement p = Assert.Single(dm.LastDistributedResult!.EventPlacements!);
        Assert.Equal(new[] { 0.0, 0.0, 1.0 }, p.Shares);   // hydro (8-pole) is the third domain
        Assert.Contains("bus 4 hosts the machines", p.Rule);

        // Same as the kernel driven with that one-hot placement, bit for bit.
        var dist = new DistributedConfig();
        (IReadOnlyList<SpeedDomain> domains, _) = DomainAssignment.Assign(m, dist);
        List<SpeedDomain> masses = domains.Where(d => d.IsSwingMass).ToList();
        double[,] k = DomainCoupling.SynchronisingMatrix(m, masses, dist);
        MultiDomainSwing.Result direct = MultiDomainSwing.Simulate(domains, k, new double[3, 3], 6300,
            new[] { new FrequencyEvent(1.0, -300.0) }, R060Cfg, dist, new[] { new[] { 0.0, 0.0, 1.0 } });
        AssertSameBits(direct.Coi, located, "located COI");

        // And it differs per domain from the unlocated (ccgt-home) placement: the hydro domain now falls hardest.
        var dm2 = new DistributedInertiaModel();
        dm2.Simulate(m, 6300, new[] { new FrequencyEvent(1.0, -300.0) }, R060Cfg);
        Assert.True(dm.LastDistributedResult!.Domains[2].NadirHz < dm2.LastDistributedResult!.Domains[2].NadirHz);
    }

    [Fact]
    public void R060_an_event_at_a_load_bus_is_shared_by_synchronising_coupling_to_the_impact_bus()
    {
        // Bus 2 sits between the ccgt (X = 0.05) and the recip (X = 0.15); the hydro is behind the recip. The reduced
        // transfer susceptance from bus 2 is 1 / 0.05 = 20 pu to bus 1 and 1 / 0.15 pu to bus 3 (nothing eliminated
        // between them), and 0 to bus 4 (no direct path once bus 3 is kept). Shares 0.75 / 0.25 / 0.
        GridModel m = R060Chain(0.05, 0.15);
        var dist = new DistributedConfig();
        (IReadOnlyList<SpeedDomain> domains, _) = DomainAssignment.Assign(m, dist);
        List<SpeedDomain> masses = domains.Where(d => d.IsSwingMass).ToList();
        (double[] shares, string rule) = DomainCoupling.ImpactShares(m, masses, 2);
        Assert.Equal(0.75, shares[0], 12);
        Assert.Equal(0.25, shares[1], 12);
        Assert.Equal(0.0, shares[2]);
        Assert.Contains("synchronising coupling", rule);
        Assert.Equal(1.0, shares.Sum(), 15);
    }

    [Fact]
    public void R060_the_placement_moves_the_domains_but_not_the_centre_of_inertia()
    {
        GridModel m = R060Chain();
        FrequencyResponse a = new DistributedInertiaModel().Simulate(m, 6300, new[] { new FrequencyEvent(1.0, -300.0, AtBus: 1) }, R060Cfg);
        FrequencyResponse b = new DistributedInertiaModel().Simulate(m, 6300, new[] { new FrequencyEvent(1.0, -300.0, AtBus: 2) }, R060Cfg);
        FrequencyResponse c = new DistributedInertiaModel().Simulate(m, 6300, new[] { new FrequencyEvent(1.0, -300.0, AtBus: 4) }, R060Cfg);
        // The tie power is antisymmetric and the shares add to one, so the COI is the same up to the rounding of the
        // per-domain sums (the T1 invariant); nothing physical.
        for (int i = 0; i < a.Points.Count; i++)
        {
            Assert.True(Math.Abs(a.Points[i].Hz - b.Points[i].Hz) < 1e-9, $"point {i}: {a.Points[i].Hz:R} vs {b.Points[i].Hz:R}");
            Assert.True(Math.Abs(a.Points[i].Hz - c.Points[i].Hz) < 1e-9, $"point {i}: {a.Points[i].Hz:R} vs {c.Points[i].Hz:R}");
        }
    }

    [Fact]
    public void R060_a_bus_the_model_lacks_or_one_with_no_path_to_any_machine_is_refused()
    {
        GridModel m = R060Chain();
        Assert.Throws<ArgumentException>(() =>
            new DistributedInertiaModel().Simulate(m, 6300, new[] { new FrequencyEvent(1.0, -300.0, AtBus: 99) }, R060Cfg));

        // Bus 5 exists but no branch reaches it.
        GridModel isolated = new()
        {
            Name = "r060-isolated",
            BaseMva = m.BaseMva,
            Buses = m.Buses.Append(new Bus(5, BusType.Pq)).ToArray(),
            Generators = m.Generators,
            Branches = m.Branches,
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new DistributedInertiaModel().Simulate(isolated, 6300, new[] { new FrequencyEvent(1.0, -300.0, AtBus: 5) }, R060Cfg));
        Assert.Contains("bus 5", ex.Message);
    }

    [Fact]
    public void R060_the_lumped_engines_ignore_the_location()
    {
        var unlocated = new[] { new FrequencyEvent(1.0, -1320.0) };
        var located = new[] { new FrequencyEvent(1.0, -1320.0, AtBus: 7) };
        AssertSameBits(FrequencyDynamics.Simulate(200_000, 30_000, unlocated), FrequencyDynamics.Simulate(200_000, 30_000, located), "lumped");
    }
}
