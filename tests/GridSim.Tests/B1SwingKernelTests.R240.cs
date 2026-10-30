using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R240: Bus.X / Bus.Y default to (0, 0), so a bus without layout coordinates was treated as sitting at the origin and
/// pulled its domain's centroid there. A bus at exactly (0, 0) now has no position: it is left out of the centroid, a
/// domain with no located member gets zero delay, and the report lists it.
/// </summary>
public sealed partial class B1SwingKernelTests
{
    private static GridModel R240Chain(bool hydroLocated, bool extraUnlocatedCcgt) => new()
    {
        BaseMva = 100,
        Buses = new Bus[]
        {
            new(1, BusType.Slack, X: 0.1, Y: 0.5),
            new(2, BusType.Pq, X: 0.4, Y: 0.5),
            hydroLocated ? new(3, BusType.Pq, X: 0.9, Y: 0.5) : new(3, BusType.Pq),
            new(4, BusType.Pq),   // no coordinates
        },
        Generators = extraUnlocatedCcgt
            ? new Generator[]
            {
                new(Bus: 1, Pg: 4000, MBase: 5000, H: 5.0, Type: "ccgt"),
                new(Bus: 2, Pg: 1500, MBase: 2000, H: 1.0, Type: "recip"),
                new(Bus: 3, Pg: 800, MBase: 1000, H: 3.0, Type: "hydro"),
                new(Bus: 4, Pg: 400, MBase: 500, H: 5.0, Type: "ccgt"),   // same domain as bus 1, but unlocated
            }
            : new Generator[]
            {
                new(Bus: 1, Pg: 4000, MBase: 5000, H: 5.0, Type: "ccgt"),
                new(Bus: 2, Pg: 1500, MBase: 2000, H: 1.0, Type: "recip"),
                new(Bus: 3, Pg: 800, MBase: 1000, H: 3.0, Type: "hydro"),
            },
        Branches = new Branch[] { new(1, 2, R: 0.0, X: 0.05), new(2, 3, R: 0.0, X: 0.05), new(3, 4, R: 0.0, X: 0.05) },
    };

    private static readonly DistributedConfig R240Dist = new() { EnablePropagation = true };

    [Fact]
    public void R240_a_member_bus_without_coordinates_no_longer_drags_the_centroid_to_the_origin()
    {
        GridModel without = R240Chain(hydroLocated: true, extraUnlocatedCcgt: false);
        GridModel with = R240Chain(hydroLocated: true, extraUnlocatedCcgt: true);
        List<SpeedDomain> mWithout = DomainAssignment.Assign(without, R240Dist).domains.Where(d => d.IsSwingMass).ToList();
        List<SpeedDomain> mWith = DomainAssignment.Assign(with, R240Dist).domains.Where(d => d.IsSwingMass).ToList();
        (double[,] tauA, double vA, _, IReadOnlyList<int> unA) = PropagationDelay.ComputeWithLocation(without, mWithout, R240Dist);
        (double[,] tauB, double vB, _, IReadOnlyList<int> unB) = PropagationDelay.ComputeWithLocation(with, mWith, R240Dist);
        Assert.Empty(unA);
        Assert.Empty(unB);
        // The unlocated ccgt at bus 4 used to move the 2-pole domain's centroid from x = 0.1 to (0.1 + 0) / 2 = 0.05; the
        // centroids, hence every first-order delay between located domains, are now those of the model without it. (The
        // wave speed differs: the extra unit adds inertia, which slows the wave, so compare distances, tau * v.)
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                Assert.Equal(tauA[i, j] * vA, tauB[i, j] * vB, 9);
    }

    [Fact]
    public void R240_a_domain_with_no_located_member_gets_zero_delay_and_is_reported()
    {
        GridModel m = R240Chain(hydroLocated: false, extraUnlocatedCcgt: false);
        List<SpeedDomain> masses = DomainAssignment.Assign(m, R240Dist).domains.Where(d => d.IsSwingMass).ToList();
        (double[,] tau, _, _, IReadOnlyList<int> unlocated) = PropagationDelay.ComputeWithLocation(m, masses, R240Dist);
        int hydro = masses.FindIndex(d => d.Poles == 8);
        Assert.Equal(new[] { hydro }, unlocated);
        for (int j = 0; j < masses.Count; j++)
        {
            Assert.Equal(0.0, tau[hydro, j]);
            Assert.Equal(0.0, tau[j, hydro]);
        }
        Assert.True(tau[0, 1] > 0.0, "the two located domains still have a delay");

        var dm = new DistributedInertiaModel();
        dm.Simulate(m, 6300, new[] { new FrequencyEvent(0.5, -200.0) },
            new DynamicsConfig { InertiaModel = InertiaModel.Distributed, Distributed = R240Dist, DurationSeconds = 5 });
        Assert.Equal(new[] { masses[hydro].Id }, dm.LastDistributedResult!.Propagation!.UnlocatedDomainIds);
    }

    [Fact]
    public void R240_fully_located_models_compute_what_they_did_before()
    {
        GridModel m = R240Chain(hydroLocated: true, extraUnlocatedCcgt: false);
        List<SpeedDomain> masses = DomainAssignment.Assign(m, R240Dist).domains.Where(d => d.IsSwingMass).ToList();
        (double[,] tau, double v, double max) = PropagationDelay.Compute(m, masses, R240Dist);
        // Pre-R240 arithmetic, written out: centroids at the generator buses, GB box spans 660 x 1020 km.
        double[] x = { 0.1, 0.4, 0.9 };
        double totalE = masses.Sum(d => d.AggregateInertiaMwS);
        double extent = 0.0;
        for (int i = 0; i < 3; i++)
            for (int j = i + 1; j < 3; j++)
                extent = Math.Max(extent, Math.Sqrt((x[i] - x[j]) * 660.0 * ((x[i] - x[j]) * 660.0) + 0.0 * 1020.0 * (0.0 * 1020.0)));
        double vExpected = Math.Clamp(1000.0 * Math.Sqrt(200.0 / Math.Max(totalE / extent, 1e-6)), 500.0, 1500.0);
        AssertBits(vExpected, v, "wave speed");
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                if (i != j)
                {
                    double dx = (x[i] - x[j]) * 660.0, dy = (0.5 - 0.5) * 1020.0;
                    AssertBits(Math.Sqrt(dx * dx + dy * dy) / vExpected, tau[i, j], $"tau[{i},{j}]");
                }
        Assert.True(max > 0.0);
    }
}
