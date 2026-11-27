using System.Globalization;
using System.Reflection;
using GridSim.Core.Estimation;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Core.Qsts;
using Xunit;

namespace GridSim.Tests;

/// <summary>Bug hunt batch 5, NUM lows (NUM-06 to NUM-26) and R6-04: one regression test per fix.</summary>
public sealed class BugHunt1125LowsNumTests
{
    private static GridModel ThreeBus(double tapB, IReadOnlyList<Oltc>? oltcs = null, double xB = 0.12) => new()
    {
        Name = "three", BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, Vm: 1.0, BaseKv: 400),
            new Bus(2, BusType.Pv, Pd: 40, Qd: 12, BaseKv: 400),
            new Bus(3, BusType.Pq, Pd: 60, Qd: 30, BaseKv: 132),
        },
        Generators = new[]
        {
            new Generator(1, Pg: 0, Vg: 1.00, Qmin: -500, Qmax: 500),
            new Generator(2, Pg: 40, Vg: 1.00, Qmin: -100, Qmax: 150),
        },
        Branches = new[]
        {
            new Branch(1, 2, 0.01, 0.10, 0.05, Name: "A"),
            new Branch(2, 3, 0.02, xB, 0, TapRatio: tapB, Name: "B", IsTransformer: true),
        },
        Oltcs = oltcs ?? Array.Empty<Oltc>(),
    };

    [Fact]
    public void Num06_an_oltc_on_a_stored_ratio_of_0_steps_from_1()
    {
        var oltc = new[] { new Oltc("B", ControlsBus: 3, TargetVpu: 1.0, MinTap: 0.9, MaxTap: 1.1, StepPu: 0.0125, DeadbandPu: 0.005) };
        var zero = new SteadyStateSolver();
        zero.Solve(ThreeBus(0.0, oltc), new PowerFlowOptions());
        var one = new SteadyStateSolver();
        one.Solve(ThreeBus(1.0, oltc), new PowerFlowOptions());
        Assert.True(one.OltcPasses > 0, "the scenario must make the OLTC step");
        // The stored 0 is the line convention (1.0): the same taps as a stored 1.0, never about 0.0125.
        Assert.Equal(one.RegulatedModel.Branches[1].TapRatio, zero.RegulatedModel.Branches[1].TapRatio);
    }

    [Fact]
    public void Num07_the_slack_reactive_note_names_the_bus_id()
    {
        var type0 = new[] { BusType.Pq, BusType.Slack };
        string? note = NewtonShell.DiagnoseSlackQ(2, type0, new[] { 0.0, 5.0 }, new[] { 0.0, 0.0 }, new[] { 0.0, -10.0 }, new[] { 0.0, 10.0 }, 100.0,
            buses: new[] { new Bus(7, BusType.Pq), new Bus(101, BusType.Slack) });
        Assert.NotNull(note);
        Assert.StartsWith("Slack bus 101:", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Num08_an_impedance_whose_admittance_overflows_is_a_validation_error()
    {
        GridModel m = ThreeBus(1.0, xB: 1e-320).CopyWith(branches: new[] { new Branch(1, 2, 0.01, 0.10, 0.05, Name: "A"), new Branch(2, 3, 1e-320, 1e-320, 0, Name: "B", IsTransformer: true) });
        Assert.Contains(m.Validate(), d => d.Severity == ValidationSeverity.Error && d.Message.Contains("so small", StringComparison.Ordinal));
        Assert.DoesNotContain(ThreeBus(1.0).Validate(), d => d.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void Num09_growing_an_empty_factor_array_ends()
    {
        MethodInfo grow = typeof(SparseLuFactorization).GetMethod("Grow", BindingFlags.NonPublic | BindingFlags.Static, new[] { typeof(int[]).MakeByRefType(), typeof(int) })!;
        object[] args = { Array.Empty<int>(), 5 };
        grow.Invoke(null, args);
        Assert.True(((int[])args[0]).Length >= 5);
    }

    [Fact]
    public void Num11_the_synthetic_set_note_is_invariant()
    {
        CultureInfo was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            GridModel m = GridSim.Core.IO.IeeeCases.Case9();
            PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(m);
            MeasurementSet set = SyntheticMeasurements.Sample(m, truth, new SyntheticMeasurementOptions { NoiseScale = 0.5, VmCoverage = 0.8 });
            Assert.Contains("noise=0.5", set.Note, StringComparison.Ordinal);
            Assert.Contains("vm:0.8", set.Note, StringComparison.Ordinal);
        }
        finally { CultureInfo.CurrentCulture = was; }
    }

    [Fact]
    public void Num14_the_sweep_refuses_a_second_slack()
    {
        var m = new GridModel
        {
            Name = "two-slack", BaseMva = 10,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 11), new Bus(2, BusType.Pq, Pd: 1, BaseKv: 11), new Bus(3, BusType.Slack, BaseKv: 11) },
            Generators = new[] { new Generator(1, Pg: 0, Vg: 1.0), new Generator(3, Pg: 0, Vg: 1.0) },
            Branches = new[] { new Branch(1, 2, 0.01, 0.02), new Branch(2, 3, 0.01, 0.02) },
        };
        var ex = Assert.Throws<InvalidDataException>(() => new BackwardForwardSweep().Solve(m));
        Assert.Contains("holds one slack bus", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Num16_oltc_numbers_are_validated()
    {
        foreach (Oltc bad in new[]
        {
            new Oltc("B", 3, StepPu: 0), new Oltc("B", 3, StepPu: -0.01), new Oltc("B", 3, MinTap: 1.1, MaxTap: 0.9),
            new Oltc("B", 3, DeadbandPu: -0.01), new Oltc("B", 3, TargetVpu: double.NaN),
        })
            Assert.Contains(ThreeBus(1.0, new[] { bad }).Validate(), d => d.Severity == ValidationSeverity.Error && d.Message.StartsWith("OLTC on transformer 'B' has", StringComparison.Ordinal));
        Assert.DoesNotContain(ThreeBus(1.0, new[] { new Oltc("B", 3) }).Validate(), d => d.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void Num17_a_qsts_study_above_the_step_cap_is_refused()
    {
        GridModel m = ThreeBus(1.0);
        var ex = Assert.Throws<InvalidDataException>(() => new QstsStudy { StepSeconds = 1, Steps = QstsStudy.MaxSteps + 1 }.Validate(m));
        Assert.Contains("steps must be at most 100000000", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Num22_an_all_integer_rounding_that_breaks_a_row_is_not_an_incumbent()
    {
        // min x, x and y integer in [0, 5], 1000 x - 1000 y = 0.0009: no integer point exists. The relaxation is whole within
        // the 1e-6 tolerance (x = y + 9e-7), so rounding gave (0, 0) as "optimal" with the row broken by 0.0009.
        var a = new List<(int, int, double)> { (0, 0, 1000.0), (0, 1, -1000.0) };
        var lp = new SparseLpProblem(1, 2, a, new[] { 0.0009 }, new[] { 1.0, 0.0 }, new[] { 0.0, 0.0 }, new[] { 5.0, 5.0 });
        MilpResult r = MilpBranchAndBound.Solve(new MilpProblem(lp, new[] { true, true }));
        if (r.X is double[] x) Assert.True(Math.Abs(1000 * x[0] - 1000 * x[1] - 0.0009) <= 1e-6, $"{r.Status}: x = {x[0]}, y = {x[1]}");
        Assert.NotEqual(MilpStatus.Optimal, r.Status);
    }

    [Fact]
    public void Num23_generation_is_the_units_output_with_load_on_their_buses()
    {
        GridModel m = ThreeBus(1.0);
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged);
        double gen = SolvedGeneration.GeneratorOutputMw(m, r);
        // Bus 2 holds 40 MW of unit output and 40 MW of load: net 0, which the old sum counted as no generation.
        double losses = gen - m.Buses.Sum(b => b.Pd);
        Assert.InRange(losses, 0.0, 5.0);
        Assert.True(gen > r.PInjMw.Where(p => p > 0).Sum() + 30, $"{gen}");
    }

    [Fact]
    public void R6_04_an_lcc_tap_search_still_moving_at_the_cap_is_refused()
        => Assert.Throws<InvalidOperationException>(() => LccDcLinks.MoveTap(1.0, 1e-9, 0.0, 2.0, -1, _ => true));
}
