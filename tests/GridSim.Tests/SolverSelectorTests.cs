using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The dashboard and CLI both route through <see cref="SolverSelector"/> so every case solves with
/// the right engine: radial distribution feeders -> backward/forward sweep, large networks -> sparse
/// Newton-Raphson, everything else -> dense. Also checks <see cref="LiveSim"/> honours the choice.
/// </summary>
public class SolverSelectorTests
{
    /// <summary>A 4-bus radial feeder: slack + three PQ buses in a line (3 branches = buses-1).</summary>
    private static GridModel RadialFeeder() => new GridModel
    {
        Name = "radial feeder",
        BaseMva = 1,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, BaseKv: 11),
            new Bus(2, BusType.Pq, Pd: 0.10, Qd: 0.05, BaseKv: 11),
            new Bus(3, BusType.Pq, Pd: 0.10, Qd: 0.05, BaseKv: 11),
            new Bus(4, BusType.Pq, Pd: 0.10, Qd: 0.05, BaseKv: 11),
        },
        Generators = new[]
        {
            new Generator(1, Pg: 0, Qg: 0, Vg: 1.0, Qmin: -100, Qmax: 100, Pmax: 100, MBase: 1, H: 0),
        },
        Branches = new[]
        {
            new Branch(1, 2, 0.01, 0.02, 0.0),
            new Branch(2, 3, 0.01, 0.02, 0.0),
            new Branch(3, 4, 0.01, 0.02, 0.0),
        },
    };

    [Fact]
    public void LooksRadial_is_true_for_a_tree_and_false_for_meshed_case9()
    {
        Assert.True(SolverSelector.LooksRadial(RadialFeeder()));
        Assert.False(SolverSelector.LooksRadial(IeeeCases.Case9()));   // case9 has loops
    }

    [Fact]
    public void Resolve_routes_radial_to_sweep_meshed_to_dense_and_large_to_sparse()
    {
        Assert.Equal(SolverKind.DistributionSweep, SolverSelector.Resolve(RadialFeeder()));
        // Moved 2026-10 (N057, Ruling R1, Mark delegated 2026-10-04): meshed networks route sparse at every size now.
        // Before: Assert.Equal(SolverKind.DenseNewton, SolverSelector.Resolve(IeeeCases.Case9())); after: SparseNewton.
        // Dense is still reached by a threshold above the bus count (the old 200-bus crossover here).
        Assert.Equal(SolverKind.SparseNewton, SolverSelector.Resolve(IeeeCases.Case9()));
        Assert.Equal(SolverKind.DenseNewton, SolverSelector.Resolve(IeeeCases.Case9(), sparseThreshold: SolverSelector.DenseSparseCrossoverBuses));
        // With a small threshold, the 9-bus meshed case crosses into sparse territory.
        Assert.Equal(SolverKind.SparseNewton, SolverSelector.Resolve(IeeeCases.Case9(), sparseThreshold: 5));
    }

    [Fact]
    public void Create_resolves_auto_and_reports_the_engine_label()
    {
        var (solver, resolved, label) = SolverSelector.Create(SolverKind.Auto, RadialFeeder());
        Assert.IsType<BackwardForwardSweep>(solver);
        Assert.Equal(SolverKind.DistributionSweep, resolved);
        Assert.Equal("Backward-forward sweep", label);

        var (s2, r2, _) = SolverSelector.Create(SolverKind.SparseNewton, IeeeCases.Case9());
        Assert.IsType<SparseNewtonRaphsonPowerFlow>(s2);      // explicit choice is honoured verbatim
        Assert.Equal(SolverKind.SparseNewton, r2);
    }

    [Fact]
    public void LiveSim_auto_solves_a_radial_feeder_with_the_sweep()
    {
        var sim = new LiveSim(RadialFeeder(), holdSteady: true);   // Auto
        Assert.Equal("Backward-forward sweep", sim.ActiveSolver);
        SimFrame f = sim.Step(1);
        Assert.True(f.Result.Converged);
    }

    [Fact]
    public void LiveSim_explicit_engine_overrides_auto()
    {
        var sim = new LiveSim(IeeeCases.Case9(), solver: SolverKind.SparseNewton);
        Assert.Equal("Newton-Raphson (sparse)", sim.ActiveSolver);
        Assert.True(sim.Step(1).Result.Converged);
    }
}
