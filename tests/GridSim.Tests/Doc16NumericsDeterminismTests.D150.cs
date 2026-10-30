using System.Numerics;
using GridSim.Core.Abstractions;
using GridSim.Core.Analysis;
using GridSim.Core.Dynamics;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using GridSim.Core.Protection.ShortCircuit;
using GridSim.Core.Storage;
using GridSim.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>DOC-16 D150: see the summary of <see cref="Doc16NumericsDeterminismTests"/>.</summary>
public partial class Doc16NumericsDeterminismTests
{
    // =================================================================================================================
    // D-DOC-150: the sweep's convergence label.
    // =================================================================================================================

    private static GridModel TwoBus(double pd, double qd) => new()
    {
        Name = "two-bus", BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, Vm: 1.0, BaseKv: 132), new Bus(2, BusType.Pq, Pd: pd, Qd: qd, BaseKv: 132) },
        Branches = new[] { new Branch(1, 2, 0.02, 0.06) },
        Generators = new[] { new Generator(1, Pg: 0) },
    };

    [Fact]
    public void D150_a_converged_sweep_is_labelled_by_the_voltage_step_and_newton_by_the_mismatch()
    {
        foreach ((double pd, double qd) in new[] { (50.0, 20.0), (120.0, 60.0), (5.0, 1.0) })
        {
            GridModel m = TwoBus(pd, qd);
            Assert.True(SolverSelector.LooksRadial(m));
            var opt = new PowerFlowOptions();
            PowerFlowResult s = new BackwardForwardSweep().Solve(m, opt);
            Assert.True(s.Converged);
            Assert.NotNull(s.SweepVoltageStepPu);
            Assert.True(s.SweepVoltageStepPu < opt.Tolerance, $"the sweep converged with a voltage step {s.SweepVoltageStepPu}");
            string label = s.ConvergedLabel();
            Assert.StartsWith("converged on the voltage step (|dV| ", label, StringComparison.Ordinal);
            Assert.Contains("the sweep does not test the power mismatch", label, StringComparison.Ordinal);
            _out.WriteLine($"two-bus {pd}+j{qd} MW: sweep {label}, mismatch {s.MaxMismatch:E3} pu (tolerance {opt.Tolerance:E0}), {s.Iterations} iterations");

            PowerFlowResult n = new SparseNewtonRaphsonPowerFlow().Solve(m, opt);
            Assert.True(n.Converged);
            Assert.Null(n.SweepVoltageStepPu);
            Assert.Equal("converged", n.ConvergedLabel());
            Assert.True(n.MaxMismatch < opt.Tolerance);   // Newton's own test is the mismatch
        }
    }

    [Fact]
    public void D150_cli_pf_on_a_radial_feeder_prints_the_sweeps_own_test()
    {
        // A radial MATPOWER feeder by path (case33bw has no built-in name); its five ties are open, so it routes to the sweep.
        CliProcess.Result r = CliProcess.RunBuilt("pf", Path.Combine(S10TestSupport.DataDir, "matpower", "dist", "case33bw.m"));
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("converged on the voltage step (|dV| ", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("the sweep does not test the power mismatch) in ", r.StdOut, StringComparison.Ordinal);
        CliProcess.Result mesh = CliProcess.RunBuilt("pf", "case9");
        Assert.True(mesh.ExitCode == 0, mesh.ToString());
        Assert.Contains("  converged in ", mesh.StdOut, StringComparison.Ordinal);   // Newton: unchanged wording
        Assert.DoesNotContain("voltage step", mesh.StdOut, StringComparison.Ordinal);
    }
}
