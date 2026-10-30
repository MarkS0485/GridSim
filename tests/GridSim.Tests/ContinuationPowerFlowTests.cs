using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>Continuation power flow: the base case is loadable beyond 1.0, the reported margin is finite, and
/// a grossly-overloaded system (far past the nose) does not solve.</summary>
public class ContinuationPowerFlowTests
{
    [Fact]
    public void Finds_a_loadability_margin_beyond_the_base_case()
    {
        GridModel model = IeeeCases.Case9();
        Assert.True(new NewtonRaphsonPowerFlow().Solve(model).Converged); // base solves

        double margin = ContinuationPowerFlow.MaxLoadability(model);
        Assert.True(margin > 1.0, $"expected loadable beyond base, got {margin}");
        Assert.True(margin < 100.0, $"margin {margin} implausibly large");

        // Far past the nose the system must collapse (not converge).
        PowerFlowResult over = new NewtonRaphsonPowerFlow().Solve(ContinuationPowerFlow.ScaleLoads(model, margin + 50.0));
        Assert.False(over.Converged, "a grossly-overloaded system should not solve");
    }
}
