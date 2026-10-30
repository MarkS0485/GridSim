using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N107 (October 2026): a contingency state with no converged solution says why. "islanded" (status ISLANDED) when the
/// outage split the network into more connected parts than the case has; "no solution" (status NO SOLUTION) when the
/// network is still connected and the power flow found no solution. Every such state used to read "islanded / no
/// solution". Labels only: the converged flag, the numbers and the verdict are unchanged.
/// </summary>
public class ContingencyIslandedLabelTests
{
    private static int BranchIndex(GridModel m, int from, int to)
        => Enumerable.Range(0, m.Branches.Count).Single(j => m.Branches[j].From == from && m.Branches[j].To == to);

    [Fact]
    public void A_radial_generator_branch_outage_is_islanded()
    {
        // case9 branch 1-4 is the only connection of generator bus 1: losing it splits the network.
        GridModel m = IeeeCases.Case9();
        ContingencyResult r = ContingencyAnalysis.ScreenOutage(m, new[] { BranchIndex(m, 1, 4) },
            ContingencyAnalysis.CreateStudySolver(m), ContingencyAnalysis.StudyOptions);
        Assert.False(r.Converged);
        Assert.True(r.Islanded);
        Assert.Equal(new[] { "islanded" }, r.Violations);
    }

    [Fact]
    public void A_connected_collapse_is_no_solution_not_islanded()
    {
        // case14 without branch 1-2 is still connected (bus 1 keeps 1-5); with reactive limits on it has no solution (the
        // N082 voltage collapse, pinned by S4SolverConvergenceTests).
        GridModel m = IeeeCases.Case14();
        ContingencyResult r = ContingencyAnalysis.ScreenOutage(m, new[] { BranchIndex(m, 1, 2) },
            ContingencyAnalysis.CreateStudySolver(m), ContingencyAnalysis.StudyOptions);
        Assert.False(r.Converged);
        Assert.False(r.Islanded);
        Assert.Equal(new[] { "no solution" }, r.Violations);
    }

    [Fact]
    public void A_converged_outage_is_never_marked_islanded()
    {
        GridModel m = IeeeCases.Case9();
        ContingencyResult r = ContingencyAnalysis.ScreenOutage(m, new[] { BranchIndex(m, 4, 5) },
            ContingencyAnalysis.CreateStudySolver(m), ContingencyAnalysis.StudyOptions);
        Assert.True(r.Converged);
        Assert.False(r.Islanded);
    }

    [Fact]
    public void Connected_parts_counts_buses_joined_by_in_service_branches()
    {
        GridModel m = IeeeCases.Case9();
        Assert.Equal(1, ContingencyAnalysis.ConnectedParts(m));
        GridModel cut = m.CopyWith(branches: m.Branches.Where(b => !(b.From == 1 && b.To == 4)).ToList());
        Assert.Equal(2, ContingencyAnalysis.ConnectedParts(cut));
        Assert.True(ContingencyAnalysis.Splits(m, cut));
        // An out-of-service branch joins nothing.
        GridModel opened = m.CopyWith(branches: m.Branches.Select(b => b.From == 1 && b.To == 4 ? b with { InService = false } : b).ToList());
        Assert.Equal(2, ContingencyAnalysis.ConnectedParts(opened));
    }
}
