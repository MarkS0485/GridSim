using GridSim.Core.Analysis;
using GridSim.Core.Analysis.TransmissionSecurity;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>AN-03 in the CLI: the corrective-sequence planner's diagnostic names the elements still over their limit, and reads as before when there are none.</summary>
public sealed class CorrectiveUnrelievedDiagnosticTests
{
    private static GridModel Model() => GridSim.Core.IO.Matpower.MatpowerParser.ParseFile(
        Path.Combine(RepoRoot(), "data", "matpower", "lib", "case9.m"));

    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public void The_diagnostic_names_the_unrelieved_elements_when_there_are_some()
    {
        var plan = new CorrectiveRedispatch(RedispatchOutcome.Applied, Model(), 12.5, ["branch L1"], LpStatus.Optimal)
        { UnrelievedAfter = ["branch L7", "boundary B2"] };
        string text = CorrectiveSequenceRunner.PlanDiagnostic(plan);
        Assert.Contains("Still over the limit after the redispatch", text, StringComparison.Ordinal);
        Assert.Contains("branch L7, boundary B2", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_diagnostic_is_unchanged_when_nothing_is_unrelieved()
    {
        var plan = new CorrectiveRedispatch(RedispatchOutcome.Applied, Model(), 12.5, ["branch L1"], LpStatus.Optimal);
        Assert.Equal("PlanCorrectiveRedispatch: Applied; moved 12.5 MW; LP Optimal; DC overloads before: branch L1.",
            CorrectiveSequenceRunner.PlanDiagnostic(plan));
    }
}
