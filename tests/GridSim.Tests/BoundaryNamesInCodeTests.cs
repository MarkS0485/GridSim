using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Physics.Experimental;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N138 (October 2026): the N097 rule (a boundary branch name names exactly one branch) for models built in code, not
/// only JSON cases. Before, <see cref="BoundaryAnalysis.Evaluate"/> skipped a name that matched nothing (the boundary's
/// flow silently left that branch out) and took the last of several namesakes; loss allocation took the first.
/// </summary>
public class BoundaryNamesInCodeTests
{
    /// <summary>IEEE 9 with every branch named B0..B8 and one boundary whose branch names are given.</summary>
    private static GridModel Case9With(params string[] boundaryBranches)
    {
        GridModel m = Core.IO.IeeeCases.Case9();
        var named = m.Branches.Select((b, i) => b with { Name = $"B{i}" }).ToList();
        return m.CopyWith(branches: named,
            boundaries: new[] { new Boundary("cut", 100, boundaryBranches.Select(n => new BoundaryBranch(n)).ToList()) });
    }

    [Fact]
    public void Evaluate_refuses_a_boundary_branch_name_that_matches_no_branch()
    {
        GridModel m = Case9With("B0", "NOPE");
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        var ex = Assert.Throws<InvalidDataException>(() => BoundaryAnalysis.Evaluate(m, r));
        Assert.Contains("boundary 'cut' names branch 'NOPE', which matches no branch of the case", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_refuses_a_name_several_branches_share()
    {
        GridModel m = Case9With("B0");
        // Rename branch 1 to "B0" too: two namesakes. The old reader silently took the last one.
        var dup = m.Branches.Select((b, i) => i == 1 ? b with { Name = "B0" } : b).ToList();
        GridModel d = m.CopyWith(branches: dup);
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(d);
        var ex = Assert.Throws<InvalidDataException>(() => BoundaryAnalysis.Evaluate(d, r));
        Assert.Contains("which 2 branches share", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_resolving_boundary_reads_exactly_as_before()
    {
        // Same flows, bit for bit, as the branch's own sending-end flow (the reading itself did not change).
        GridModel m = Case9With("B0", "B3");
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        BoundaryFlow bf = BoundaryAnalysis.Evaluate(m, r).Single();
        LineFlow f0 = r.LineFlows.Single(f => f.Name == "B0"), f3 = r.LineFlows.Single(f => f.Name == "B3");
        Assert.Equal(0.0 + f0.PFromMw + f3.PFromMw, bf.FlowMw);
    }

    [Fact]
    public void Validate_reports_bad_boundary_names_as_errors()
    {
        IReadOnlyList<GridDiagnostic> d = Case9With("NOPE").Validate();
        Assert.Contains(d, x => x.Severity == ValidationSeverity.Error
            && x.Message == "Boundary 'cut' names branch 'NOPE', which matches no branch of the case.");
        Assert.DoesNotContain(Case9With("B0").Validate(), x => x.Message.Contains("boundary", StringComparison.OrdinalIgnoreCase)
            && x.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void The_contingency_screen_refuses_a_bad_name_but_reads_an_outaged_boundary_branch_as_absent()
    {
        var ex = Assert.Throws<InvalidDataException>(() => ContingencyAnalysis.ScreenBranches(Case9With("NOPE")));
        Assert.Contains("NOPE", ex.Message, StringComparison.Ordinal);
        // Outaging B0 itself removes it from the reduced model: that is not a bad name, and the screen runs.
        IReadOnlyList<ContingencyResult> results = ContingencyAnalysis.ScreenBranches(Case9With("B0"));
        Assert.Equal(9, results.Count);
    }

    [Fact]
    public void Loss_allocation_and_boundary_tracing_refuse_a_bad_name()
    {
        GridModel m = Case9With("NOPE");
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.Throws<InvalidDataException>(() => LossAllocation.Allocate(m, r));
        Assert.Throws<InvalidDataException>(() => PowerFlowTracing.BoundaryTransfers(m, r));
    }

    [Fact]
    public void The_bridge_model_reader_refuses_a_bad_name()
    {
        var block = new BridgeProtocol.ModelBlock
        {
            Buses = new() { new() { Id = 1, Type = "Slack", BaseKv = 400 }, new() { Id = 2, Type = "Pq", BaseKv = 400, Pd = 10 } },
            Generators = new() { new() { Bus = 1, Pg = 10 } },
            Branches = new() { new() { From = 1, To = 2, X = 0.1, Name = "L12" } },
            Boundaries = new() { new() { Name = "cut", LimitMw = 50, Branches = new() { new() { Branch = "L21" } } } },
        };
        var ex = Assert.Throws<InvalidDataException>(() => BridgeProtocol.BuildModel(block));
        // Test changed 2026-10-19: since 1.95.372 (GR-147) BridgeProtocol.BuildModel runs GridModel.Validate first,
        // which reports the same N138 problem with a capital letter ("Boundary 'cut' names branch 'L21', ...").
        Assert.Contains("Boundary 'cut' names branch 'L21'", ex.Message, StringComparison.Ordinal);
        block.Boundaries[0].Branches![0].Branch = "L12";
        Assert.Single(BridgeProtocol.BuildModel(block).Boundaries);
    }
}
