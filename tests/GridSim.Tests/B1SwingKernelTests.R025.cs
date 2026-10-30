using System.Numerics;
using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R025: a failed Kron reduction (one isolated bus anywhere) silently swapped the physical inter-domain stiffness for the
/// uniform 0.5 Hz heuristic, and nothing said so. Now isolated / machine-less parts of the network are left out of the
/// reduction (they cannot couple anything), the source of the coupling is reported (DistributedResult.Coupling, the
/// divergence report, the CLI), and the anti-synchronising pairs that are dropped are counted.
/// </summary>
public sealed partial class B1SwingKernelTests
{
    private static GridModel R025TwoDomain(bool withIsolatedBus) => new()
    {
        Name = "r025",
        BaseMva = 100,
        Buses = withIsolatedBus
            ? new Bus[] { new(1, BusType.Slack), new(2, BusType.Pq), new(3, BusType.Pq), new(9, BusType.Pq) }
            : new Bus[] { new(1, BusType.Slack), new(2, BusType.Pq), new(3, BusType.Pq) },
        Generators = new Generator[]
        {
            new(Bus: 1, Pg: 800, MBase: 1000, H: 5.0, Type: "ccgt"),
            new(Bus: 3, Pg: 400, MBase: 500, H: 3.0, Type: "hydro"),
        },
        Branches = new Branch[] { new(1, 2, R: 0.0, X: 0.05), new(2, 3, R: 0.01, X: 0.08) },
    };

    private static List<SpeedDomain> R025Masses(GridModel m)
        => DomainAssignment.Assign(m, new DistributedConfig()).domains.Where(d => d.IsSwingMass).ToList();

    [Fact]
    public void R025_an_isolated_bus_no_longer_swaps_the_network_coupling_for_the_heuristic()
    {
        GridModel clean = R025TwoDomain(false), withIsland = R025TwoDomain(true);
        var dist = new DistributedConfig();
        (double[,] kClean, CouplingReport rClean) = DomainCoupling.Synchronising(clean, R025Masses(clean), dist);
        (double[,] kIsland, CouplingReport rIsland) = DomainCoupling.Synchronising(withIsland, R025Masses(withIsland), dist);

        Assert.Equal(CouplingSource.Kron, rClean.Source);
        Assert.Equal(0, rClean.DroppedBusCount);
        Assert.Equal(CouplingSource.Kron, rIsland.Source);   // before R025: the uniform heuristic, silently
        Assert.Equal(1, rIsland.DroppedBusCount);
        AssertBits(kClean[0, 1], kIsland[0, 1], "K01");
        AssertBits(kClean[1, 0], kIsland[1, 0], "K10");

        // The old code really did fall back on this model (the full reduction meets the isolated bus's zero pivot).
        YBus y = YBus.Build(withIsland);
        Assert.Throws<InvalidOperationException>(() => KronReduction.Reduce(y.Y, new[] { y.IndexOf(1), y.IndexOf(3) }));
    }

    [Fact]
    public void R025_the_coupling_is_bit_identical_to_the_pre_R025_matrix_wherever_that_succeeded()
    {
        foreach (GridModel m in new[] { R025TwoDomain(false), R060Chain(), R060Chain(0.02, 0.4) })
        {
            List<SpeedDomain> masses = R025Masses(m);
            double[,] oldK = OldSynchronisingMatrix(m, masses);
            double[,] newK = DomainCoupling.SynchronisingMatrix(m, masses, new DistributedConfig());
            for (int i = 0; i < masses.Count; i++)
                for (int j = 0; j < masses.Count; j++)
                    AssertBits(oldK[i, j], newK[i, j], $"{m.Name} K[{i},{j}]");
        }
    }

    [Fact]
    public void R025_the_heuristic_is_reported_with_its_reason_on_the_result_and_in_the_divergence_report()
    {
        // No branch reactance at all: the uniform target-mode stiffness, now labelled.
        GridModel bare = new()
        {
            BaseMva = 100,
            Buses = new Bus[] { new(1, BusType.Slack), new(2, BusType.Pq) },
            Generators = new Generator[]
            {
                new(Bus: 1, Pg: 800, MBase: 1000, H: 5.0, Type: "ccgt"),
                new(Bus: 2, Pg: 400, MBase: 500, H: 3.0, Type: "hydro"),
            },
            Branches = Array.Empty<Branch>(),
        };
        var loss = new[] { new FrequencyEvent(1.0, -300.0) };
        var cfg = new DynamicsConfig { InertiaModel = InertiaModel.Distributed };
        var dm = new DistributedInertiaModel();
        FrequencyResponse coi = dm.Simulate(bare, 1200, loss, cfg);
        CouplingReport c = dm.LastDistributedResult!.Coupling!;
        Assert.Equal(CouplingSource.UniformNoBranchReactance, c.Source);
        Assert.True(c.IsHeuristic);
        Assert.Contains("no branch reactance", c.Reason);

        DivergenceReport rep = DivergenceReport.Compute(FrequencyDynamics.Simulate(bare.SystemInertiaGvaS * 1000.0, 1200, loss), coi, dm.LastDistributedResult!);
        Assert.Contains("NOT network-derived", rep.Mechanism);

        // A network-derived run says nothing extra (its mechanism text is the pre-R025 one).
        var dm2 = new DistributedInertiaModel();
        FrequencyResponse coi2 = dm2.Simulate(R025TwoDomain(false), 1200, loss, cfg);
        Assert.Equal(CouplingSource.Kron, dm2.LastDistributedResult!.Coupling!.Source);
        Assert.Equal("inter-domain synchronising coupling redistributes the disturbance across speed domains",
            DivergenceReport.Compute(coi2, coi2, dm2.LastDistributedResult!).Mechanism);
    }

    [Fact]
    public void R025_machines_in_separate_islands_are_reported_as_negligible_coupling()
    {
        GridModel islands = new()
        {
            BaseMva = 100,
            Buses = new Bus[] { new(1, BusType.Slack), new(2, BusType.Pq), new(3, BusType.Slack), new(4, BusType.Pq) },
            Generators = new Generator[]
            {
                new(Bus: 1, Pg: 800, MBase: 1000, H: 5.0, Type: "ccgt"),
                new(Bus: 3, Pg: 400, MBase: 500, H: 3.0, Type: "hydro"),
            },
            Branches = new Branch[] { new(1, 2, R: 0.0, X: 0.05), new(3, 4, R: 0.0, X: 0.05) },
        };
        (_, CouplingReport r) = DomainCoupling.Synchronising(islands, R025Masses(islands), new DistributedConfig());
        Assert.Equal(CouplingSource.UniformNegligibleCoupling, r.Source);
        Assert.Contains("negligible", r.Reason);
    }

    [Fact]
    public void R025_the_delegating_single_domain_path_carries_no_coupling_report()
    {
        // The single-domain path delegates to the lumped model and carries no coupling report at all.
        var dm = new DistributedInertiaModel();
        GridModel case9 = IeeeCases.Case9();
        dm.Simulate(case9, 315, new[] { new FrequencyEvent(1.0, -100.0) },
            new DynamicsConfig { InertiaModel = InertiaModel.Distributed, NominalHz = case9.FrequencyHz });
        Assert.Null(dm.LastDistributedResult!.Coupling);
    }

    /// <summary>The pre-R025 SynchronisingMatrix, verbatim (1.95.215), the oracle for the bit-identity test.</summary>
    private static double[,] OldSynchronisingMatrix(GridModel model, IReadOnlyList<SpeedDomain> masses)
    {
        int n = masses.Count;
        var k = new double[n, n];
        if (n <= 1) return k;
        var busDomain = new Dictionary<int, int>();
        var busInertia = new Dictionary<int, double>();
        for (int d = 0; d < n; d++)
            foreach (int gi in masses[d].MemberGenIndices)
            {
                Generator g = model.Generators[gi];
                double e = g.H * g.MBase;
                if (!busInertia.TryGetValue(g.Bus, out double cur) || e > cur)
                {
                    busInertia[g.Bus] = Math.Max(cur, e);
                    busDomain[g.Bus] = d;
                }
            }
        YBus ybus = YBus.Build(model);
        List<int> keepBusIds = busDomain.Keys.ToList();
        List<int> keepIdx = keepBusIds.Select(id => ybus.IndexOf(id)).ToList();
        Complex[,] reduced = KronReduction.Reduce(ybus.Y, keepIdx);
        int m = keepBusIds.Count;
        for (int a = 0; a < m; a++)
            for (int b = a + 1; b < m; b++)
            {
                int da = busDomain[keepBusIds[a]];
                int db = busDomain[keepBusIds[b]];
                if (da == db) continue;
                double kij = model.BaseMva * reduced[a, b].Imaginary;
                if (kij <= 0) continue;
                k[da, db] += kij;
                k[db, da] += kij;
            }
        return k;
    }
}
