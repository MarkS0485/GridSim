using System;
using System.Collections.Generic;
using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// T3 (fail-loud gate) - qualitative properties of the distributed model that a lumped scalar cannot
/// show. These assert bands and orderings, not equalities: the speed-domain dominance reproduces the
/// LOS sec. 5.5 worked example; the propagation overlay makes physically-distant domains respond later; and
/// the electromechanical wave speed lands in the published 500-1500 km/s band.
/// </summary>
public class DistributedQualitativeTests
{
    // ---- T3a: dominance reproduces the LOS sec. 5.5 worked example (domains of ~12/9/4/2 GVA) ----
    [Fact]
    public void Dominance_reproduces_the_los_worked_example()
    {
        var model = new GridModel
        {
            BaseMva = 100,
            Buses = new Bus[] { new(1, BusType.Slack), new(2, BusType.Pq), new(3, BusType.Pq), new(4, BusType.Pq) },
            Generators = new Generator[]
            {
                new(Bus: 1, Pg: 12000, MBase: 12000, H: 5.0, Type: "ccgt"),   // 2-pole -> GVad 12000
                new(Bus: 2, Pg: 9000,  MBase: 9000,  H: 1.0, Type: "recip"),  // 4-pole -> GVad 9000
                new(Bus: 3, Pg: 4000,  MBase: 4000,  H: 3.0, Type: "hydro"),  // 8-pole -> GVad 4000
                new(Bus: 4, Pg: 2000,  MBase: 2000,  H: 0.0, Type: "wind"),   // virtual -> GVad 2000
            },
            Branches = Array.Empty<Branch>(),
        };

        (_, DominanceReport dom) = DomainAssignment.Assign(model, new DistributedConfig());

        Assert.True(dom.HasDominant);
        Assert.Equal(0.333, dom.DeltaGVa, 3);   // (12000 - 9000)/9000 ~ 0.333 >= 0.10
        Assert.Equal(12000.0, dom.DomainsByGvadDesc[0].Gvad, 3);
    }

    // ---- T3b/c: propagation - distant domains respond later; wave speed in band ----
    private static GridModel ChainModel()
    {
        // Three machines of different speed (-> three domains) strung along a line, west to east.
        return new GridModel
        {
            BaseMva = 100,
            Buses = new Bus[]
            {
                new(1, BusType.Slack, X: 0.0, Y: 0.5),   // west end - the trip
                new(2, BusType.Pq,    X: 0.4, Y: 0.5),
                new(3, BusType.Pq,    X: 0.9, Y: 0.5),    // east end - furthest
            },
            Generators = new Generator[]
            {
                new(Bus: 1, Pg: 4000, MBase: 5000, H: 5.0, Type: "ccgt"),   // largest -> the loss lands here
                new(Bus: 2, Pg: 1500, MBase: 2000, H: 1.0, Type: "recip"),
                new(Bus: 3, Pg: 800,  MBase: 1000, H: 3.0, Type: "hydro"),
            },
            Branches = new Branch[] { new(1, 2, R: 0.0, X: 0.05), new(2, 3, R: 0.0, X: 0.05) },
        };
    }

    private static readonly FrequencyEvent[] Loss = { new(0.5, -200.0) };

    private static DynamicsConfig PropagationCfg() => new()
    {
        InertiaModel = InertiaModel.Distributed,
        // No governor / damping / FFR, so non-trip domains move ONLY when the delayed wave reaches them.
        ResponsiveFraction = 0.0,
        DampingPctPerHz = 0.0,
        FastResponseMw = 0.0,
        DurationSeconds = 10.0,
        Distributed = new DistributedConfig { EnablePropagation = true },
    };

    [Fact]
    public void Propagation_makes_distant_domains_respond_later()
    {
        var model = new DistributedInertiaModel();
        model.Simulate(ChainModel(), 6300, Loss, PropagationCfg());

        PropagationReport? prop = model.LastDistributedResult!.Propagation;
        Assert.NotNull(prop);
        IReadOnlyList<double> arrival = prop!.DomainArrivalSeconds;
        Assert.Equal(3, arrival.Count);
        Assert.All(arrival, a => Assert.True(a >= 0.0, "every domain must eventually respond"));

        // Ordered by physical distance from the west-end trip: domain0 (trip) <= domain1 < domain2.
        Assert.True(arrival[0] <= arrival[1], $"trip domain first: {arrival[0]} vs {arrival[1]}");
        Assert.True(arrival[1] < arrival[2], $"further domain later: {arrival[1]} vs {arrival[2]}");
    }

    [Fact]
    public void Wave_speed_is_in_the_literature_band()
    {
        var model = new DistributedInertiaModel();
        model.Simulate(ChainModel(), 6300, Loss, PropagationCfg());

        double v = model.LastDistributedResult!.Propagation!.WaveSpeedKmPerS;
        Assert.InRange(v, 500.0, 1500.0);
    }

    [Fact]
    public void Propagation_is_off_by_default_and_leaves_no_report()
    {
        var model = new DistributedInertiaModel();
        // Default distributed config: propagation off.
        model.Simulate(ChainModel(), 6300, Loss, new DynamicsConfig { InertiaModel = InertiaModel.Distributed });
        Assert.Null(model.LastDistributedResult!.Propagation);
    }

    [Fact]
    public void Provided_tau_map_overrides_the_first_order_model()
    {
        // Externally-supplied tau (e.g. GDA's propagation_delays over the bridge). Rep buses: domain0=bus1
        // (ccgt, the trip), domain1=bus2 (on the propagation path). Force a big 2.0 s delay on the trip<->
        // domain1 corridor; domain1 must then arrive ~ 0.5 s (event) + 2.0 s = ~2.5 s, far later than its
        // ~0.7 s first-order arrival - proving the supplied tau drives it, not the computed model.
        var provided = new Dictionary<(int, int), double> { [(1, 2)] = 2.0 };
        var cfg = new DynamicsConfig
        {
            InertiaModel = InertiaModel.Distributed,
            ResponsiveFraction = 0.0,
            DampingPctPerHz = 0.0,
            FastResponseMw = 0.0,
            DurationSeconds = 10.0,
            Distributed = new DistributedConfig { EnablePropagation = true, ProvidedTauSeconds = provided },
        };
        var model = new DistributedInertiaModel();
        model.Simulate(ChainModel(), 6300, Loss, cfg);

        IReadOnlyList<double> arrival = model.LastDistributedResult!.Propagation!.DomainArrivalSeconds;
        Assert.Equal(3, arrival.Count);
        Assert.InRange(arrival[1], 2.0, 3.0);   // ~ 0.5 event + 2.0 supplied tau (not the ~0.7 s first-order value)
    }
}
