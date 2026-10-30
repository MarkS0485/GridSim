using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Physics;
using GridSim.Core.Physics.Experimental;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The experimental, opt-in reciprocity law (Y = Y^T for a passive network). Pins its degenerate
/// limit - a phase-shifter-free passive network is exactly reciprocal - and that it stays out of the
/// hard gate. A build-error asymmetry is caught; a phase-shifter is tolerated as non-reciprocal
/// by design.
/// </summary>
public class ReciprocityCheckTests
{
    [Theory]
    [InlineData("9")]
    [InlineData("14")]
    [InlineData("30")]
    public void A_phase_shifter_free_passive_network_is_exactly_reciprocal(string name)
    {
        GridModel m = name switch
        {
            "9" => IeeeCases.Case9(),
            "14" => IeeeCases.Case14(),
            "30" => IeeeCases.Case30(),
            _ => IeeeCases.Case14(),
        };
        // These cases carry no phase-shift, so reciprocity holds at machine epsilon (the degenerate
        // limit: the experimental law collapses to a trivial pass).
        bool anyShift = m.Branches.Any(b => b.PhaseShiftDeg != 0.0);
        LawResult r = ReciprocityCheck.Evaluate(m);

        Assert.True(r.Residual < 1e-9, $"reciprocity residual {r.Residual}");
        Assert.Equal(anyShift ? LawStatus.Constitutive : LawStatus.Holds, r.Status);
        Assert.NotEqual(LawStatus.Violated, r.Status);
        Assert.Equal(11, r.Number);
    }

    [Fact]
    public void It_is_not_a_hard_gate_law()
    {
        // Reciprocity (11) is experimental and never gates a state - the hard gate is unchanged.
        Assert.DoesNotContain(11, PhysicsCheckRegistry.HardGateLaws);
    }

    [Fact]
    public void An_empty_network_is_vacuously_reciprocal()
    {
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack) },
            Generators = System.Array.Empty<Generator>(),
            Branches = System.Array.Empty<Branch>(),
        };
        LawResult r = ReciprocityCheck.Evaluate(m);
        Assert.Equal(LawStatus.Holds, r.Status);
        Assert.Equal(0.0, r.Residual);
    }
}
