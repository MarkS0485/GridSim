using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Physics.Experimental;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Web.Projection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// DOC-13 (October 2026), D-DOC-087: loss allocation, power-flow tracing, carbon tracing and boundary analysis now hold
/// to the convergence contract (<see cref="ConvergenceContract.RequireConverged"/>): a non-converged state is refused
/// with <see cref="NonConvergedStateException"/> naming the study, instead of having its transient flows allocated,
/// traced or summed across a boundary and reported like a solved state. Converged results are computed exactly as
/// before (the bit-identity tests of each analysis are unchanged). The live views skip these analyses on a frame that
/// did not converge. Every test is new.
/// </summary>
public sealed class Doc13ConvergenceContractTests
{
    /// <summary>IEEE 9 with named branches and a boundary, and a state stopped before it converged.</summary>
    private static (GridModel Model, PowerFlowResult NotConverged, PowerFlowResult Converged) Case9()
    {
        GridModel m = IeeeCases.Case9();
        var named = m.Branches.Select((b, i) => b with { Name = $"B{i}" }).ToList();
        GridModel withB = m.CopyWith(branches: named, boundaries: new[] { new Boundary("cut", 100, new[] { new BoundaryBranch("B0") }) });
        // MaxIterations 0: the flat-start mismatch is evaluated and the solve stops, not converged.
        PowerFlowResult stopped = new NewtonRaphsonPowerFlow().Solve(withB, new PowerFlowOptions { MaxIterations = 0 });
        Assert.False(stopped.Converged);
        PowerFlowResult solved = new NewtonRaphsonPowerFlow().Solve(withB);
        Assert.True(solved.Converged);
        return (withB, stopped, solved);
    }

    public static IEnumerable<object[]> Studies() => new[]
    {
        new object[] { "Boundary analysis" },
        new object[] { "Loss allocation" },
        new object[] { "Power-flow tracing" },
        new object[] { "Power-flow tracing (branch contributions)" },
        new object[] { "Power-flow tracing (boundary transfers)" },
        new object[] { "Carbon tracing" },
    };

    private static void Run(string study, GridModel m, PowerFlowResult r)
    {
        switch (study)
        {
            case "Boundary analysis": _ = BoundaryAnalysis.Evaluate(m, r); break;
            case "Loss allocation": _ = LossAllocation.Allocate(m, r); break;
            case "Power-flow tracing": _ = PowerFlowTracing.Trace(m, r); break;
            case "Power-flow tracing (branch contributions)": _ = PowerFlowTracing.BranchContributions(m, r); break;
            case "Power-flow tracing (boundary transfers)": _ = PowerFlowTracing.BoundaryTransfers(m, r); break;
            case "Carbon tracing": _ = CarbonTracing.Trace(m, r); break;
            default: throw new ArgumentOutOfRangeException(nameof(study), study, null);
        }
    }

    [Theory]
    [MemberData(nameof(Studies))]
    public void D087_each_analysis_refuses_a_non_converged_state_naming_the_study(string study)
    {
        (GridModel m, PowerFlowResult stopped, _) = Case9();
        NonConvergedStateException ex = Assert.Throws<NonConvergedStateException>(() => Run(study, m, stopped));
        string expectedName = study.StartsWith("Power-flow tracing", StringComparison.Ordinal) ? "Power-flow tracing" : study;
        Assert.Equal(expectedName, ex.Study);
        Assert.Contains("did not converge", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Studies))]
    public void D087_each_analysis_still_runs_on_a_converged_state(string study)
    {
        (GridModel m, _, PowerFlowResult solved) = Case9();
        Run(study, m, solved);   // no exception
    }

    [Fact]
    public void D087_the_web_frame_projector_shows_no_boundary_rows_and_no_trace_for_a_non_converged_frame()
    {
        (GridModel m, PowerFlowResult stopped, PowerFlowResult solved) = Case9();
        static SimFrame Frame(GridModel g, PowerFlowResult r) =>
            new(g, r, Hour: 0, Tick: 0, FrequencyHz: 50, GenerationMw: g.Generators.Sum(x => x.Pg), DemandMw: g.Buses.Sum(b => b.Pd));

        ExperimentalDto bad = FrameProjector.Project(Frame(m, stopped)).Experimental!;
        Assert.True(bad.TraceNotConverged);
        Assert.Empty(bad.TopFlows);

        ExperimentalDto good = FrameProjector.Project(Frame(m, solved)).Experimental!;
        Assert.False(good.TraceNotConverged);
    }

    [Fact]
    public void D087_every_caller_outside_Core_checks_convergence_first()
    {
        // Source contract: the live views and the CLI dashboard guard each call (the analyses would throw otherwise).
        string cli = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Cli", "Program.cs");
        Assert.Contains("IReadOnlyList<BoundaryFlow> bnd = r.Converged ? BoundaryAnalysis.Evaluate(m, r) : Array.Empty<BoundaryFlow>();", cli, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyList<BoundaryFlow> boundaries = r.Converged ? BoundaryAnalysis.Evaluate(m, r) : Array.Empty<BoundaryFlow>();", cli, StringComparison.Ordinal);
        Assert.Contains("not evaluated (the power flow did not converge)", cli, StringComparison.Ordinal);

        string web = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Web", "Projection", "FrameProjector.cs");
        Assert.Contains("r.Converged ? BoundaryAnalysis.Evaluate(f.Model, r) : Array.Empty<BoundaryFlow>()", web, StringComparison.Ordinal);
        string tabs = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Web", "Projection", "TabProjections.cs");
        Assert.Contains("f.Result.Converged ? BoundaryAnalysis.Evaluate(f.Model, f.Result) : Array.Empty<BoundaryFlow>()", tabs, StringComparison.Ordinal);

        // Test retargeted 2026-10-20 (UI programme 0.2): the live step (the WPF StepOnce) moved from
        // src/GridSim.Wpf/MainWindow.xaml.cs to src/GridSim.Ui/Hosting/SimHost.cs (ProduceNext), which the window now drives;
        // the first assertion reads it there, unchanged. Strengthened: the window must not evaluate a live frame's boundaries
        // itself any more (it shows the host's), so the guarded line cannot be bypassed by a second, unguarded copy.
        string host = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Hosting", "SimHost.cs");
        Assert.Contains("f.Result.Converged ? BoundaryAnalysis.Evaluate(f.Model, f.Result) : Array.Empty<BoundaryFlow>()", host, StringComparison.Ordinal);
        string wpf = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Wpf", "MainWindow.xaml.cs");
        Assert.DoesNotContain(" BoundaryAnalysis.Evaluate(", wpf, StringComparison.Ordinal);   // only the qualified, guarded replay/bridge call below
        Assert.Contains("? GridSim.Core.Analysis.BoundaryAnalysis.Evaluate(f.Model, f.Result)", wpf, StringComparison.Ordinal);
        // Test retargeted 2026-10-20 (UI programme 1.6): src/GridSim.Wpf/Views/ExperimentalVm.cs moved to
        // src/GridSim.Ui/Tabs/Experimental/ExperimentalModel.cs; assertion unchanged, and strengthened: the WPF
        // view-model must derive from that model and may not trace a frame itself (a second, unguarded copy).
        string vm = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Ui", "Tabs", "Experimental", "ExperimentalModel.cs");
        Assert.Contains("if (!skipped && !notConverged)", vm, StringComparison.Ordinal);
        string adapter = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Wpf", "Views", "ExperimentalVm.cs");
        Assert.Contains("public sealed class ExperimentalVm : ExperimentalModel", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("PowerFlowTracing.Trace(", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("CarbonTracing.Trace(", adapter, StringComparison.Ordinal);
    }
}
