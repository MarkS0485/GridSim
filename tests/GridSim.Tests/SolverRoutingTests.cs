using System.Text.RegularExpressions;
using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R003 / R134 / R135 / R147 (October 2026 review): the dense/sparse crossover was 200 buses in
/// <see cref="SolverSelector"/> but 1,000, 1,500 or 3,000 in the CLI and the protection cascade, so the same
/// case ran on different engines depending on the entry point (case1354pegase: dense ~10 s vs sparse ~50 ms).
/// Every dense/sparse choice now routes through one rule. Since 1.95.001 the two engines are bit-identical on
/// the CPU, so the rule changes speed only - these tests pin the rule and guard against local thresholds
/// creeping back into the CLI.
/// </summary>
public class SolverRoutingTests
{
    /// <summary>A meshed (not radial) model with <paramref name="n"/> buses: a ring.</summary>
    private static GridModel Ring(int n) => new()
    {
        Name = $"ring-{n}",
        BaseMva = 100,
        Buses = Enumerable.Range(1, n).Select(i => new Bus(i, i == 1 ? BusType.Slack : BusType.Pq)).ToArray(),
        Branches = Enumerable.Range(1, n).Select(i => new Branch(i, i % n + 1, 0.01, 0.1)).ToArray(),
        Generators = new[] { new Generator(1, Pg: 0) },
    };

    /// <summary>A radial (tree) model with <paramref name="n"/> buses: a chain.</summary>
    private static GridModel Chain(int n) => new()
    {
        Name = $"chain-{n}",
        BaseMva = 100,
        Buses = Enumerable.Range(1, n).Select(i => new Bus(i, i == 1 ? BusType.Slack : BusType.Pq)).ToArray(),
        Branches = Enumerable.Range(1, n - 1).Select(i => new Branch(i, i + 1, 0.01, 0.1)).ToArray(),
        Generators = new[] { new Generator(1, Pg: 0) },
    };

    // Moved 2026-10 (N057, Ruling R1, Mark delegated 2026-10-04): the power-flow routing is now always sparse (MATPOWER
    // solves every size sparse; re-benched faster at every size and bit-identical, since both engines share the
    // sparse-LU step). The shared threshold moved 200 -> 0, so the 3-, 199- and 200-bus rings that routed dense now
    // route sparse. Before: Assert.Equal(200, SolverSelector.DefaultSparseThreshold) and InlineData(3 | 199 | 200,
    // SolverKind.DenseNewton); after: 0 and SolverKind.SparseNewton. The old 200-bus crossover lives on, unchanged, as
    // SolverSelector.DenseSparseCrossoverBuses for the WLS estimator and the GPU routing (pinned just below).
    [Theory]
    [InlineData(3, SolverKind.SparseNewton)]
    [InlineData(199, SolverKind.SparseNewton)]
    [InlineData(200, SolverKind.SparseNewton)]
    [InlineData(201, SolverKind.SparseNewton)]
    [InlineData(2999, SolverKind.SparseNewton)]
    [InlineData(3001, SolverKind.SparseNewton)]
    public void Newton_rule_switches_to_sparse_past_the_shared_threshold(int buses, SolverKind expected)
    {
        Assert.Equal(0, SolverSelector.DefaultSparseThreshold);
        Assert.Equal(expected, SolverSelector.ResolveNewton(Ring(buses)));
        Assert.Equal(expected, SolverSelector.Resolve(Ring(buses)));   // meshed: Resolve == ResolveNewton
    }

    // N057: the split-off crossover keeps the old rule exactly (dense up to 200 buses, sparse above) for its users.
    [Theory]
    [InlineData(3, SolverKind.DenseNewton)]
    [InlineData(199, SolverKind.DenseNewton)]
    [InlineData(200, SolverKind.DenseNewton)]
    [InlineData(201, SolverKind.SparseNewton)]
    [InlineData(3001, SolverKind.SparseNewton)]
    public void The_old_crossover_keeps_its_rule_for_the_estimator_and_the_gpu(int buses, SolverKind expected)
    {
        Assert.Equal(200, SolverSelector.DenseSparseCrossoverBuses);
        Assert.Equal(expected, SolverSelector.ResolveNewton(Ring(buses), SolverSelector.DenseSparseCrossoverBuses));
        Assert.Equal(200, new GridSim.Core.Estimation.EstimationOptions().SparseBusThreshold);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(500)]
    public void Newton_rule_never_picks_the_sweep_even_for_a_radial_network(int buses)
    {
        GridModel radial = Chain(buses);
        Assert.Equal(SolverKind.DistributionSweep, SolverSelector.Resolve(radial));
        Assert.NotEqual(SolverKind.DistributionSweep, SolverSelector.ResolveNewton(radial));
    }

    // Moved 2026-10 (N057, Ruling R1, Mark delegated 2026-10-04): always sparse. Before: InlineData(150,
    // typeof(NewtonRaphsonPowerFlow)) (150 buses was under the old 200 threshold); after: the sparse engine.
    [Theory]
    [InlineData(150, typeof(SparseNewtonRaphsonPowerFlow))]
    [InlineData(250, typeof(SparseNewtonRaphsonPowerFlow))]
    public void CreateNewton_builds_the_resolved_engine(int buses, Type expected)
    {
        IPowerFlowSolver s = SolverSelector.CreateNewton(Ring(buses));
        Assert.IsType(expected, s);
    }

    [Fact]
    public void No_local_dense_sparse_threshold_remains_in_the_cli()
    {
        // Anti-drift: any 'BusCount > N ? ...Sparse... : ...Dense...' ternary in the CLI is a second, local copy of
        // the routing rule - exactly the defect R003 fixed. Route through SolverBackend.Solver/NewtonSolver/Pick.
        string root = RepoRoot();
        var offenders = new List<string>();
        var pattern = new Regex(@"BusCount\s*>\s*\d+\s*\?[^;]*?(SparseSolver|SparseNewtonRaphsonPowerFlow|SparseLinear)", RegexOptions.Singleline);
        foreach (string file in Directory.GetFiles(Path.Combine(root, "src", "GridSim.Cli"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
            foreach (Match mt in pattern.Matches(File.ReadAllText(file)))
                offenders.Add($"{Path.GetFileName(file)}: {mt.Value.Split('\n')[0].Trim()}");
        }
        Assert.True(offenders.Count == 0, "local dense/sparse thresholds found:\n" + string.Join("\n", offenders));
    }

    /// <summary>The repository root (the folder holding GridSim.slnx), found by walking up from the test binary.
    /// Fails loudly if it is not found, so the anti-drift scan can never silently scan nothing.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {AppContext.BaseDirectory}");
        return dir!.FullName;
    }
}
