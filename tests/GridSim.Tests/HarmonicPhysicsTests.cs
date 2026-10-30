using GridSim.Core.Harmonics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Physics;
using GridSim.Core.Physics.Experimental;
using Xunit;

namespace GridSim.Tests;

/// <summary>The opt-in per-order harmonic-KCL physics law: a correctly-solved penetration balances
/// Y(h)V(h) = I(h) at machine precision, and (like the reciprocity check) it never gates a state.</summary>
public class HarmonicPhysicsTests
{
    [Fact]
    public void Harmonic_kcl_holds_for_a_solved_penetration()
    {
        GridModel model = IeeeCases.Case9();
        var opt = new HarmonicModelOptions { Orders = new[] { 5, 7, 11 } };
        var sources = new[] { HarmonicSourceLibrary.Make("6-pulse", model.Buses[4].Id, 0.8) };
        HarmonicPenetrationResult pen = HarmonicPenetration.Solve(model, sources, opt);

        LawResult law = HarmonicKclCheck.Evaluate(model, sources, pen, opt);
        Assert.Equal(12, law.Number);
        Assert.Equal(LawStatus.Holds, law.Status);
        Assert.True(law.Residual < 1e-8, $"harmonic KCL residual {law.Residual}");
    }

    [Fact]
    public void The_harmonic_kcl_law_never_gates_a_state()
    {
        // Experimental / opt-in: not one of the hard-gate laws.
        Assert.DoesNotContain(12, PhysicsCheckRegistry.HardGateLaws);
    }
}
