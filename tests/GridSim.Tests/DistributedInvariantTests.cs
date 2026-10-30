using System;
using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// T1 (fail-loud gate) - conservation invariants for the multi-domain kernel on a genuinely
/// multi-domain system (a 2-pole thermal domain + an 8-pole hydro domain). Because the tie power is
/// antisymmetric it never moves the centre of inertia, so the inertia-weighted COI trajectory equals
/// the lumped model to machine epsilon - while the per-domain frequencies genuinely diverge.
/// </summary>
public class DistributedInvariantTests
{
    private const double Demand = 1200.0;
    private static readonly FrequencyEvent[] Loss = { new(1.0, -300.0) };

    private static GridModel TwoDomainModel() => new()
    {
        BaseMva = 100,
        Buses = new Bus[] { new(1, BusType.Slack), new(2, BusType.Pq) },
        Generators = new Generator[]
        {
            new(Bus: 1, Pg: 800, MBase: 1000, H: 5.0, Type: "ccgt"),   // 2-pole domain, E = 5000 MW.s
            new(Bus: 2, Pg: 400, MBase: 500,  H: 3.0, Type: "hydro"),  // 8-pole domain, E = 1500 MW.s
        },
        Branches = Array.Empty<Branch>(),
    };

    [Fact]
    // GR-406: renamed from Coi_trajectory_equals_the_lumped_model_to_machine_epsilon: the name said machine epsilon; the assertion is 1e-6 Hz (about 1e10 ulps at 50 Hz), so the name states the bound. Tightening waits for a measured value.
    public void Coi_trajectory_matches_the_lumped_model_within_1e_6_hz()
    {
        var grid = TwoDomainModel();
        FrequencyResponse lumped = FrequencyDynamics.Simulate(grid.SystemInertiaGvaS * 1000.0, Demand, Loss);

        var model = new DistributedInertiaModel();
        FrequencyResponse coi = model.Simulate(
            grid, Demand, Loss, new DynamicsConfig { InertiaModel = InertiaModel.Distributed });

        Assert.Equal(2, model.LastDistributedResult!.Domains.Count);   // it really ran the multi-mass kernel
        Assert.Equal(lumped.Points.Count, coi.Points.Count);
        double maxDiff = 0; int at = -1;
        for (int k = 0; k < lumped.Points.Count; k++)
        {
            double diff = Math.Abs(lumped.Points[k].Hz - coi.Points[k].Hz);
            if (diff > maxDiff) { maxDiff = diff; at = k; }
        }
        Assert.True(maxDiff < 1e-6,
            $"maxDiff={maxDiff:E4} at k={at} t={at * 0.02:F2}s  lumped={lumped.Points[at].Hz:F9} coi={coi.Points[at].Hz:F9}");
        Assert.True(Math.Abs(lumped.NadirHz - coi.NadirHz) < 1e-6, $"nadir lumped={lumped.NadirHz:F9} coi={coi.NadirHz:F9}");
        Assert.True(Math.Abs(lumped.SettleHz - coi.SettleHz) < 1e-6, $"settle lumped={lumped.SettleHz:F9} coi={coi.SettleHz:F9}");
        Assert.True(Math.Abs(lumped.InitialRocofHzPerS - coi.InitialRocofHzPerS) < 1e-6, $"rocof lumped={lumped.InitialRocofHzPerS:F9} coi={coi.InitialRocofHzPerS:F9}");
    }

    [Fact]
    public void Per_domain_frequencies_diverge_from_the_coi()
    {
        var grid = TwoDomainModel();
        var model = new DistributedInertiaModel();
        model.Simulate(grid, Demand, Loss, new DynamicsConfig { InertiaModel = InertiaModel.Distributed });

        System.Collections.Generic.IReadOnlyList<DomainTrace> domains = model.LastDistributedResult!.Domains;
        Assert.Equal(2, domains.Count);
        Assert.True(Math.Abs(domains[0].NadirHz - domains[1].NadirHz) > 1e-3,
            $"domains should diverge; got {domains[0].NadirHz} vs {domains[1].NadirHz}");
    }

    [Fact]
    public void Dominance_report_flags_the_larger_domain()
    {
        var grid = TwoDomainModel();
        var model = new DistributedInertiaModel();
        model.Simulate(grid, Demand, Loss, new DynamicsConfig { InertiaModel = InertiaModel.Distributed });

        DominanceReport dom = model.LastDistributedResult!.Dominance;
        Assert.True(dom.HasDominant);
        // GVad: ccgt 1000*0.8 = 800, hydro 500*0.8 = 400 -> deltaGVa = (800 - 400)/400 = 1.0
        Assert.Equal(1.0, dom.DeltaGVa, 6);
    }
}
