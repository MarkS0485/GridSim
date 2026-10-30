using GridSim.Core.Analysis;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

public class BoundaryTests
{
    private static GridModel Uk() => JsonGridLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "data", "uk"));

    [Fact]
    public void Boundaries_load_and_evaluate()
    {
        var m = Uk();
        Assert.Equal(4, m.Boundaries.Count);

        var r = new SteadyStateSolver().Solve(m);
        var flows = BoundaryAnalysis.Evaluate(m, r);
        Assert.Equal(m.Boundaries.Count, flows.Count);
        Assert.Contains(flows, f => f.Name.Contains("B6"));
    }

    [Fact]
    public void All_boundaries_are_within_limit_in_the_base_case()
    {
        var m = Uk();
        var r = new SteadyStateSolver().Solve(m);
        foreach (BoundaryFlow f in BoundaryAnalysis.Evaluate(m, r))
        {
            Assert.False(f.Overloaded, $"{f.Name} overloaded: {f.FlowMw:F0}/{f.LimitMw:F0} MW");
            Assert.Equal(Math.Abs(f.FlowMw) / f.LimitMw * 100.0, f.LoadingPercent, 3);
        }
    }

    [Fact]
    public void B6_carries_scottish_export_south()
    {
        var m = Uk();
        var r = new SteadyStateSolver().Solve(m);
        BoundaryFlow b6 = BoundaryAnalysis.Evaluate(m, r).First(f => f.Name.Contains("B6"));
        Assert.True(b6.FlowMw > 0, "Scotland should be exporting south across B6");
        Assert.InRange(b6.LoadingPercent, 5, 95);
    }

    [Fact]
    public void Branches_have_ratings_and_loading()
    {
        var m = Uk();
        var r = new SteadyStateSolver().Solve(m);
        Assert.All(r.LineFlows, f => Assert.True(f.RatingMva > 0, $"{f.Name} has no rating"));
        Assert.Contains(r.LineFlows, f => f.LoadingPercent > 0);
        // GSP transformers carry their whole zone's demand -> should be the busiest.
        LineFlow top = r.LineFlows.OrderByDescending(f => f.LoadingPercent).First();
        Assert.Contains("GSP", top.Name);
    }
}
