using GridSim.Core.Abstractions;
using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using GridSim.Core.Viz;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Workstream 4, "one convergence contract" (October 2026 review): a non-converged state must never flow into a
/// result reported as success. Pins R039 (OLTC loop), R040 (fault analysis, protection cascade/assessment and the
/// other studies that start from a solved state), R024 (DC-OPF stall reported feasible), R296 (viz frames), R225
/// (contingency screens mislabel solver errors and swallow cancellation).
/// </summary>
public class ConvergenceContractTests
{
    /// <summary>case9 solved with a 1-iteration cap: a genuinely non-converged state.</summary>
    private static (GridModel Model, PowerFlowResult State) Unconverged()
    {
        GridModel m = IeeeCases.Case9();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { MaxIterations = 1, FlatStart = true });
        Assert.False(r.Converged);
        return (m, r);
    }

    [Fact]
    public void RequireConverged_names_the_study_and_the_state()
    {
        (_, PowerFlowResult bad) = Unconverged();
        var ex = Assert.Throws<NonConvergedStateException>(() => bad.RequireConverged("Some study"));
        Assert.Equal("Some study", ex.Study);
        Assert.Equal(bad.Status, ex.Status);
        Assert.Contains("did not converge", ex.Message);

        PowerFlowResult good = new NewtonRaphsonPowerFlow().Solve(IeeeCases.Case9());
        Assert.Same(good, good.RequireConverged("Some study"));
    }

    [Fact]
    public void Fault_analysis_refuses_a_non_converged_prefault()
    {
        // R040: it used to read the iterate's voltages and report the faulted state as converged.
        (GridModel m, PowerFlowResult bad) = Unconverged();
        var ex = Assert.Throws<NonConvergedStateException>(() => FaultAnalysis.ThreePhase(m, bad, m.Buses[4].Id));
        Assert.Equal("Fault analysis", ex.Study);
    }

    [Fact]
    public void Protection_cascade_and_assessment_refuse_a_non_converged_state()
    {
        (GridModel m, PowerFlowResult bad) = Unconverged();
        Assert.Throws<NonConvergedStateException>(() => ProtectionCascade.Simulate(m, bad));
        Assert.Throws<NonConvergedStateException>(() => ProtectionAssessment.Assess(m, bad));
    }

    [Fact]
    public void Loss_contingency_refuses_a_non_converged_base_case()
    {
        (GridModel m, PowerFlowResult bad) = Unconverged();
        Assert.Throws<NonConvergedStateException>(() => LossContingency.Sweep(m, bad));
    }

    // ---- R039: the OLTC loop ----------------------------------------------------------------------------

    private static GridModel OltcModel() => new()
    {
        BaseMva = 100,
        Name = "oltc-contract",
        Buses = new[]
        {
            new Bus(1, BusType.Slack, Vm: 1.0),
            new Bus(2, BusType.Pq, Pd: 80, Qd: 30),
            new Bus(3, BusType.Pq, Pd: 40, Qd: 15),
        },
        Generators = new[] { new Generator(1, 0) },
        Branches = new[]
        {
            new Branch(1, 2, 0.008, 0.08, Name: "T1", TapRatio: 1.0),
            new Branch(2, 3, 0.02, 0.10, Name: "L23"),
        },
        Oltcs = new[] { new Oltc("T1", ControlsBus: 2, TargetVpu: 1.02, MinTap: 0.9, MaxTap: 1.1, StepPu: 0.0125, DeadbandPu: 0.01) },
    };

    [Fact]
    public void Oltc_loop_settles_and_says_so()
    {
        var s = new SteadyStateSolver();
        PowerFlowResult r = s.Solve(OltcModel());
        Assert.True(r.Converged);
        Assert.True(s.TapsSettled);
        Assert.True(s.OltcPasses > 1);
    }

    [Fact]
    public void Oltc_loop_never_steps_a_tap_on_a_non_converged_state()
    {
        // A 1-iteration cap from a flat start cannot converge: the loop must stop at once, leave the taps alone
        // and report TapsSettled = false. It used to step taps on the iterate's voltages for up to 25 passes.
        GridModel m = OltcModel();
        var s = new SteadyStateSolver();
        PowerFlowResult r = s.Solve(m, new PowerFlowOptions { MaxIterations = 1, FlatStart = true });
        Assert.False(r.Converged);
        Assert.False(s.TapsSettled);
        Assert.Equal(0, s.OltcPasses);
        Assert.Same(m, s.RegulatedModel);
        Assert.Equal(1.0, s.RegulatedModel.Branches[0].TapRatio);
    }

    [Fact]
    public void Oltc_loop_that_hits_its_pass_cap_reports_unsettled()
    {
        var s = new SteadyStateSolver();
        s.Solve(OltcModel(), maxOltcPasses: 1);
        Assert.Equal(1, s.OltcPasses);
        Assert.False(s.TapsSettled);
    }

    [Fact]
    public void A_model_without_oltcs_is_trivially_settled()
    {
        var s = new SteadyStateSolver();
        s.Solve(IeeeCases.Case9());
        Assert.True(s.TapsSettled);
    }

    // ---- R024: DC-OPF ------------------------------------------------------------------------------------

    [Fact]
    public void Dc_opf_reports_its_security_lp_outcome()
    {
        GridModel m = IeeeCases.Case9();
        DcOptimalPowerFlow.DcOpfResult unconstrained = DcOptimalPowerFlow.Solve(m, enforceLineLimits: false);
        Assert.Null(unconstrained.SecurityLpStatus);   // no LP ran
    }

    // ---- R296: viz frames ----------------------------------------------------------------------------------

    [Fact]
    public void Viz_frames_carry_convergence_and_the_model_frequency()
    {
        GridModel m60 = IeeeCases.Case9();
        m60 = new GridModel
        {
            Name = m60.Name, BaseMva = m60.BaseMva, FrequencyHz = 60.0,
            Buses = m60.Buses, Generators = m60.Generators, Branches = m60.Branches,
        };
        VizScene scene = VizExport.Build(m60, frames: 4);
        Assert.All(scene.Frames, f => Assert.True(f.Converged));
        Assert.All(scene.Frames, f => Assert.Equal(60.0f, f.FreqHz));   // was hard-coded 50 Hz
    }

    // ---- R225: contingency screens -------------------------------------------------------------------------

    private sealed class ThrowingSolver(Exception ex) : IPowerFlowSolver
    {
        public PowerFlowResult Solve(GridModel model, PowerFlowOptions? options = null) => throw ex;
    }

    [Fact]
    public void Contingency_screen_records_the_real_solver_error()
    {
        GridModel m = IeeeCases.Case9();
        ContingencyResult r = ContingencyAnalysis.ScreenOutage(m, new[] { 0 }, new ThrowingSolver(new ArgumentException("bad data")));
        Assert.False(r.Converged);
        string v = Assert.Single(r.Violations);
        Assert.Equal("solver error: ArgumentException: bad data", v);   // was "islanded / no solution"
    }

    [Fact]
    public void Contingency_screen_lets_cancellation_through()
    {
        GridModel m = IeeeCases.Case9();
        Assert.Throws<OperationCanceledException>(() =>
            ContingencyAnalysis.ScreenOutage(m, new[] { 0 }, new ThrowingSolver(new OperationCanceledException())));
    }

    // ---- R282: the Q-limit pass cap ------------------------------------------------------------------------

    [Fact]
    public void Q_limit_loop_that_hits_its_pass_cap_with_a_switch_pending_is_not_converged()
    {
        // One pass: case300 switches buses to PQ on its first Q-limit check, so the cap is hit with switched types
        // that were never solved. It used to report Converged on the previous types' voltages.
        GridModel m = IeeeCases.Case300();
        var full = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = true });
        Assert.True(full.Converged);
        var opt = new PowerFlowOptions { EnforceQLimits = true, MaxQLimitPasses = 1 };
        PowerFlowResult d = new NewtonRaphsonPowerFlow().Solve(m, opt);
        PowerFlowResult s = new SparseNewtonRaphsonPowerFlow().Solve(m, opt);
        Assert.False(d.Converged);
        Assert.Equal(PowerFlowStatus.MaxIterations, d.Status);
        // The shared rule keeps the engines bit-identical on this path too.
        Assert.Equal(d.Iterations, s.Iterations);
        for (int i = 0; i < d.Vm.Length; i++)
            Assert.Equal(BitConverter.DoubleToInt64Bits(d.Vm[i]), BitConverter.DoubleToInt64Bits(s.Vm[i]));
    }

    [Fact]
    public void A_q_limit_pass_cap_below_one_is_refused()
    {
        var opt = new PowerFlowOptions { EnforceQLimits = true, MaxQLimitPasses = 0 };
        Assert.Throws<ArgumentOutOfRangeException>(() => new NewtonRaphsonPowerFlow().Solve(IeeeCases.Case9(), opt));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SparseNewtonRaphsonPowerFlow().Solve(IeeeCases.Case9(), opt));
        // With Q limits off the pass cap is not used, so it is not validated.
        Assert.True(new NewtonRaphsonPowerFlow().Solve(IeeeCases.Case9(), opt with { EnforceQLimits = false }).Converged);
    }
}
