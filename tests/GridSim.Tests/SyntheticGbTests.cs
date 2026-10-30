using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Synthetic;
using Xunit;

namespace GridSim.Tests;

public class SyntheticGbTests
{
    private static GridModel Build() => SyntheticGb.Build();

    [Fact]
    public void Builds_a_network_at_scale()
    {
        var m = Build();
        Assert.InRange(m.BusCount, 1800, 3500);
        Assert.InRange(m.Branches.Count, m.BusCount - 50, m.BusCount + 300);   // tree + rings
        Assert.Equal(1, m.Buses.Count(b => b.Type == BusType.Slack));

        // the whole voltage ladder is present
        foreach (double kv in new[] { 400.0, 275.0, 132.0, 33.0, 11.0, 6.6 })
            Assert.Contains(m.Buses, b => b.BaseKv == kv);

        // names are unique  -  the asset pages and OLTCs match branches by name
        Assert.Equal(m.Branches.Count, m.Branches.Select(b => b.Name).Distinct().Count());
        Assert.Equal(m.BusCount, m.Buses.Select(b => b.Name).Distinct().Count());
    }

    [Fact]
    public void Is_deterministic_for_a_seed()
    {
        var a = SyntheticGb.Build(new SyntheticGb.Options { Seed = 7 });
        var b = SyntheticGb.Build(new SyntheticGb.Options { Seed = 7 });
        Assert.Equal(a.BusCount, b.BusCount);
        Assert.Equal(a.Buses.Sum(x => x.Pd), b.Buses.Sum(x => x.Pd), 6);
        Assert.Equal(a.Branches.Count, b.Branches.Count);
    }

    [Fact]
    public void Converges_with_the_sparse_solver()
    {
        var m = Build();
        var solver = new SteadyStateSolver(new SparseNewtonRaphsonPowerFlow());
        var r = solver.Solve(m, new PowerFlowOptions { MaxIterations = 60, EnforceQLimits = false }, maxOltcPasses: 30);

        Assert.True(r.Converged, $"did not converge (mismatch {r.MaxMismatch:E2})");
        Assert.All(r.Vm, v => Assert.InRange(v, 0.92, 1.08));
        double demand = m.Buses.Sum(b => b.Pd);
        Assert.True(r.TotalLossMw > 0 && r.TotalLossMw < 0.04 * demand, $"losses {r.TotalLossMw:F0} MW look wrong");
    }

    [Fact]
    public void Round_trips_through_the_json_case_format()
    {
        var m = Build();
        string dir = Path.Combine(Path.GetTempPath(), $"gridsim-synth-{Guid.NewGuid():N}");
        try
        {
            GridSim.Gda.GdaModelWriter.WriteDirectory(m, dir, addGridInfeed: false);
            GridModel loaded = JsonGridLoader.LoadDirectory(dir);

            Assert.Equal(m.BusCount, loaded.BusCount);
            Assert.Equal(m.Branches.Count, loaded.Branches.Count);
            Assert.Equal(m.Generators.Count, loaded.Generators.Count);
            Assert.Equal(1, loaded.Buses.Count(b => b.Type == BusType.Slack));

            // embedded plant is a fixed injection  -  its bus must stay PQ on reload
            foreach (Generator g in loaded.Generators.Where(g => g.Name!.Contains("embedded")))
                Assert.Equal(BusType.Pq, loaded.Buses.First(b => b.Id == g.Bus).Type);

            // and the reloaded case still solves
            var r = new SteadyStateSolver(new SparseNewtonRaphsonPowerFlow())
                .Solve(loaded, new PowerFlowOptions { MaxIterations = 60, EnforceQLimits = false }, maxOltcPasses: 30);
            Assert.True(r.Converged, $"reloaded case did not converge (mismatch {r.MaxMismatch:E2})");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
