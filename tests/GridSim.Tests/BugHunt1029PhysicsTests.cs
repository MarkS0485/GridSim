using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Physics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>Static bug hunt of 2026-10-29 (GridSim 2.0.183), physics-audit findings. See BugHunt1029DynamicsTests.cs.</summary>
public sealed partial class BugHunt1029Tests
{
    private static PowerFlowResult BHWithFlows(PowerFlowResult r, IReadOnlyList<LineFlow> flows) => new()
    {
        Converged = r.Converged, Iterations = r.Iterations, MaxMismatch = r.MaxMismatch, BusIds = r.BusIds,
        Vm = r.Vm, VaDeg = r.VaDeg, PInjMw = r.PInjMw, QInjMvar = r.QInjMvar,
        TotalLossMw = r.TotalLossMw, TotalLossMvar = r.TotalLossMvar, LineFlows = flows,
    };

    // ---------------------------------------------------------------------------------------------------------------
    // BH67: law 2 must not report Holds after comparing nothing.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void BH67_law_2_reads_not_applicable_when_no_reported_flow_is_aligned_with_a_branch()
    {
        GridModel m = IeeeCases.Case9();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged);
        // The honest result: law 2 holds, every branch compared.
        LawResult good = PhysicsLaws.Audit(m, r).Laws.Single(l => l.Number == 2);
        Assert.Equal(LawStatus.Holds, good.Status);

        // Every reported flow reversed (From and To swapped), so none is aligned with its branch. Before the fix law 2
        // reported Holds with residual 0, having compared nothing.
        var reversed = r.LineFlows.Select(f => f with { From = f.To, To = f.From }).ToList();
        LawResult law2 = PhysicsLaws.Audit(m, BHWithFlows(r, reversed)).Laws.Single(l => l.Number == 2);
        Assert.Equal(LawStatus.NotApplicable, law2.Status);
        Assert.Contains("nothing was compared", law2.Detail, StringComparison.Ordinal);

        // Some aligned, some not: the verdict stands on those compared and the detail says how many.
        var half = r.LineFlows.Select((f, i) => i % 2 == 0 ? f : f with { From = f.To, To = f.From }).ToList();
        LawResult partial = PhysicsLaws.Audit(m, BHWithFlows(r, half)).Laws.Single(l => l.Number == 2);
        Assert.Equal(LawStatus.Holds, partial.Status);
        Assert.Contains($"{(m.Branches.Count + 1) / 2} of {m.Branches.Count} audited branches", partial.Detail, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // BH68: law detail strings print numbers in the invariant culture.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void BH68_law_detail_strings_print_a_decimal_point_under_a_comma_decimal_culture()
    {
        GridModel m = IeeeCases.Case9();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        string invariant = PhysicsLaws.Audit(m, r).Laws.Single(l => l.Number == 1).Detail;
        string german = BHUnderGermanCulture(() => PhysicsLaws.Audit(m, r).Laws.Single(l => l.Number == 1).Detail);
        Assert.Equal(invariant, german);
        Assert.Contains(".", german, StringComparison.Ordinal);
        Assert.DoesNotContain(",", german, StringComparison.Ordinal);
    }
}
