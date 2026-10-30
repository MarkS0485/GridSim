using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Physics.Experimental;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N155 (October 2026): the CLI <c>experimental wave</c> subcommand asks <see cref="ContinuumWaveModel.CannotFormReason"/>
/// before the classical reduction, so a case with no per-machine inertia is refused before the dense network matrices
/// are built (about 1.46 GB on gb-full). The reason must be exactly what <see cref="ContinuumWaveModel.FromSolvedCase"/>
/// would have thrown, and null exactly when <see cref="ContinuumWaveModel.CanForm"/> is true, so the refusal says the
/// same thing as before and the exit code (1) is unchanged.
/// </summary>
public class N155WaveCannotFormTests
{
    private static (GridModel Model, PowerFlowResult Result) Solve(GridModel m)
    {
        var solver = new SteadyStateSolver();
        PowerFlowResult r = solver.Solve(m);
        return (solver.RegulatedModel, r);
    }

    [Fact]
    public void A_case_with_inertia_has_no_reason_and_CanForm_agrees()
    {
        (GridModel m, PowerFlowResult r) = Solve(IeeeCases.Case9());
        Assert.Null(ContinuumWaveModel.CannotFormReason(m, r));
        Assert.True(ContinuumWaveModel.CanForm(m, r));
    }

    [Fact]
    public void A_case_without_inertia_gives_exactly_the_message_FromSolvedCase_throws()
    {
        (GridModel m, PowerFlowResult r) = Solve(IeeeCases.Case14());   // MATPOWER data: H = 0
        string? reason = ContinuumWaveModel.CannotFormReason(m, r);
        Assert.NotNull(reason);
        Assert.False(ContinuumWaveModel.CanForm(m, r));
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => ContinuumWaveModel.FromSolvedCase(m, r));
        Assert.Equal(ex.Message, reason);
    }

    [Fact]
    public void A_non_converged_result_and_a_case_with_no_generators_give_the_first_checks_messages()
    {
        (GridModel m, PowerFlowResult r) = Solve(IeeeCases.Case9());
        var stopped = new SteadyStateSolver();
        PowerFlowResult notConverged = stopped.Solve(IeeeCases.Case9(), new PowerFlowOptions { MaxIterations = 1 });
        Assert.False(notConverged.Converged);
        Assert.Equal(Assert.Throws<InvalidOperationException>(() => ContinuumWaveModel.FromSolvedCase(m, notConverged)).Message,
            ContinuumWaveModel.CannotFormReason(m, notConverged));

        GridModel none = m.CopyWith(generators: Array.Empty<Generator>());
        Assert.Equal(Assert.Throws<InvalidOperationException>(() => ContinuumWaveModel.FromSolvedCase(none, r)).Message,
            ContinuumWaveModel.CannotFormReason(none, r));
    }

    [Fact]
    public void The_cli_refuses_a_no_inertia_case_before_the_build_with_exit_1()
    {
        CliProcess.Result p = CliProcess.RunBuilt("experimental", "wave", "case14");
        Assert.True(p.ExitCode == 1, p.ToString());
        Assert.Contains("has no inertia constant", p.StdErr, StringComparison.Ordinal);   // the message as before
        Assert.Contains("refused before the classical reduction", p.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cli_still_runs_a_case_with_inertia()
    {
        CliProcess.Result p = CliProcess.RunBuilt("experimental", "wave", "case9");
        Assert.True(p.ExitCode == 0, p.ToString());
        Assert.Contains("dominant inter-area mode", p.StdOut, StringComparison.Ordinal);
    }
}
