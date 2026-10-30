using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Source-level anti-drift checks for the S1 contingency CLI wiring (October 2026): D04 (one option set and the OLTC
/// loop in every contingency verb), D03 (netting against the base case), D02 (failing single outages reported apart),
/// and F01 (the verdict is the exit code; <c>losses &lt;unknown&gt;</c> no longer crashes). The test project does not
/// reference the CLI, so, like <c>CliInputHandlingTests</c>, these read src/GridSim.Cli. The behaviour itself was run
/// by hand.
/// </summary>
public class CliContingencyVerdictTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {AppContext.BaseDirectory}");
        return dir!.FullName;
    }

    private static string Program() =>
        File.ReadAllText(Path.Combine(Root(), "src", "GridSim.Cli", "Program.cs")).Replace("\r\n", "\n");

    /// <summary>The body of a top-level local function, from its signature to the next "\n}\n".</summary>
    private static string Body(string src, string signature)
    {
        int at = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{signature}' not found");
        int end = src.IndexOf("\n}\n", at, StringComparison.Ordinal);
        Assert.True(end > at, $"end of '{signature}' not found");
        return src[at..end];
    }

    [Theory]
    [InlineData("void RunNMinus1(GridModel model)")]
    [InlineData("void RunNMinus2(GridModel model,")]
    [InlineData("void RunNMinus1Minus1(GridModel model,")]
    public void The_branch_screens_use_the_study_set_net_against_the_base_case_and_return_a_verdict(string signature)
    {
        string body = Body(Program(), signature);
        Assert.Contains("ContingencyAnalysis.StudyOptions", body);        // D04: one option set
        Assert.Contains("ContingencyBaseCase(", body);                    // D03: the base case solved first
        Assert.Contains("ContingencyAnalysis.NetAgainstBase(", body);     // D03: netting
        Assert.Contains("ContingencyVerdict(", body);                     // F01: the verdict is the exit code
        Assert.DoesNotContain("new PowerFlowOptions", body);
    }

    [Theory]
    [InlineData("void RunNMinus2(GridModel model,")]
    [InlineData("void RunNMinus1Minus1(GridModel model,")]
    public void The_pruned_screens_report_their_failing_single_outages(string signature)
    {
        string body = Body(Program(), signature);
        Assert.Contains("Report(", body);                                 // D02: the report form of the screen
        Assert.Contains("PrintExcludedSingles(report.ExcludedSingleOutages)", body);
        Assert.Contains("report.ExcludedSingleOutages.Count", body);      // they count against the verdict
    }

    [Fact]
    public void The_verdict_exits_3_when_anything_is_not_secure_and_a_bad_base_case_fails()
    {
        string src = Program();
        Assert.Contains("Environment.ExitCode = 3;", Body(src, "static void ContingencyVerdict("));
        string baseCase = Body(src, "ContingencyResult? ContingencyBaseCase(");
        // GR-213: the base case is solved by the study solver on the selected backend (StudySolverOnBackend, which on the
        // CPU is exactly ContingencyAnalysis.CreateStudySolver), not by the CPU-only CreateStudySolver as before.
        Assert.Contains("StudySolverOnBackend(model), ContingencyAnalysis.StudyOptions", baseCase);
        Assert.Contains("ContingencyAnalysis.WithOltcLoop(backend.NewtonSolver(model))", Body(src, "IPowerFlowSolver StudySolverOnBackend("));
        Assert.Contains("Environment.ExitCode = 3;", baseCase);
    }

    [Fact]
    public void A_row_that_only_carries_the_base_cases_violations_is_marked_base_only_not_secure()
    {
        // D03: netting must not make a still-violating state read as violation-free. The status says 'base only' and
        // the footer counts those rows; both tables (nminus1 and the N-2 / N-1-1 printer) use the one status function.
        string src = Program();
        string status = Body(src, "static string ContingencyStatus(");
        Assert.Contains("c.PreExistingViolations.Count > 0 ? \"base only\"", status);
        Assert.Contains("ContingencyStatus(c)", Body(src, "void RunNMinus1(GridModel model)"));
        Assert.Contains("ContingencyStatus(c)", Body(src, "static int PrintNkTable("));
        Assert.Contains("PrintBaseOnlyNote(results)", Body(src, "static int PrintNkTable("));
    }

    [Fact]
    public void Screen_exits_3_when_a_loss_breaches_a_limit()
    {
        string body = Body(Program(), "void RunScreen(GridModel model");
        Assert.Contains("if (breaches > 0)", body);
        Assert.Contains("Environment.ExitCode = 3;", body);
    }

    [Fact]
    public void The_sweep_uses_the_study_set_with_the_oltc_loop_nets_and_returns_a_verdict()
    {
        string body = Body(Program(), "void RunNMinus1Sweep(");
        Assert.Contains("PowerFlowOptions opt = ContingencyAnalysis.StudyOptions;", body);
        Assert.Contains("ContingencyAnalysis.WithOltcLoop(backend.NewtonSolver(m))", body);
        Assert.Contains("ContingencyAnalysis.ScreenBaseCase(m, factory(), opt)", body);
        Assert.Contains("ContingencyAnalysis.NetAgainstBase(", body);
        Assert.Contains("Environment.ExitCode = 3;", body);
        // The old sweep options switched reactive limits off; that must not come back (D04).
        Assert.DoesNotContain("EnforceQLimits = false", body);
    }

    [Theory]
    [InlineData("void RunStress(")]
    [InlineData("void RunChain(")]
    public void Stress_and_chain_use_the_study_set_with_the_oltc_loop(string signature)
    {
        string body = Body(Program(), signature);
        Assert.Contains("PowerFlowOptions opt = ContingencyAnalysis.StudyOptions;", body);
        Assert.Contains("ContingencyAnalysis.WithOltcLoop(backend.NewtonSolver(baseM))", body);
        Assert.DoesNotContain("new PowerFlowOptions", body);
    }

    [Fact]
    public void Losses_loads_its_case_through_the_shared_loader()
    {
        // F01: "losses nosuchcase" threw an unhandled ArgumentException out of LoadNamed. Now: unknown name exit 2,
        // missing .m exit 1 (the process-wide handler), second case argument exit 2.
        string body = Body(Program(), "void RunLossAllocation(");
        Assert.Contains("SingleCasePositional(\"losses\", a)", body);
        Assert.Contains("LoadSelected(\"losses\", (pos, () => LoadCaseArgument(pos, root)))", body);
        Assert.DoesNotContain("LoadNamed(pos", body);
    }
}
