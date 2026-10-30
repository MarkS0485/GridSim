using GridSim.Core.Abstractions;
using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R072 (October 2026): one shared loadability loop, and the restore verb.
///
/// The CLI stress verb used to carry its own copy of the "highest load factor converged before the first failure" rule;
/// it now runs through ContinuationPowerFlow.ScanGrid, the same walk loop the continuation (cpf) uses. These tests prove
/// the refactor moved no number: MaxLoadability is compared bit for bit with the continuation loop exactly as it was
/// written before (copied here verbatim as the reference), and ScanGrid is compared with the verb's old prefix rule
/// (also copied verbatim) on the verb's own scaling, solver and options.
/// </summary>
public class R072LoadabilityAndRestoreTests
{
    // The continuation loop exactly as ContinuationPowerFlow.Run read before R072 (the reference for bit-identity).
    private static double OldMaxLoadability(GridModel model, double step, double minStep, int maxSteps)
    {
        IPowerFlowSolver solver = SolverSelector.CreateNewton(model);
        double lambda = 1.0;
        double lastGood = 0.0;
        int steps = 0;
        while (step >= minStep && steps++ < maxSteps)
        {
            PowerFlowResult r = solver.Solve(ContinuationPowerFlow.ScaleLoads(model, lambda));
            if (r.Converged)
            {
                lastGood = lambda;
                lambda += step;
            }
            else
            {
                lambda -= step;
                step *= 0.5;
                lambda += step;
            }
        }
        return lastGood;
    }

    // The stress verb's grid, exactly as the verb builds it.
    private static List<double> StressFactors(double from, double to, double step)
    {
        var factors = new List<double>();
        for (double f = from; f <= to + 1e-9; f += step) factors.Add(Math.Round(f, 4));
        return factors;
    }

    // One stress load point, exactly as the verb solves it (AtLoad re-dispatch, contingency options, OLTC loop).
    private static PowerFlowResult StressSolve(GridModel baseM, double f) =>
        ContingencyAnalysis.WithOltcLoop(SolverSelector.CreateNewton(baseM)).Solve(GridScenario.AtLoad(baseM, f), ContingencyAnalysis.StudyOptions);

    [Theory]
    [InlineData("9", 0.25, 1e-3, 200)]
    [InlineData("14", 0.25, 1e-3, 200)]
    [InlineData("30", 0.25, 1e-3, 200)]
    [InlineData("14", 0.5, 1e-2, 7)]      // the step cap ends the walk before the bracket closes
    [InlineData("9", 0.1, 0.1, 200)]      // no bisection at all: stops at the first failure
    public void MaxLoadability_is_bit_identical_to_the_pre_R072_loop(string n, double step, double minStep, int maxSteps)
    {
        GridModel m = n switch { "9" => IeeeCases.Case9(), "14" => IeeeCases.Case14(), _ => IeeeCases.Case30() };
        double expected = OldMaxLoadability(m, step, minStep, maxSteps);
        double actual = ContinuationPowerFlow.MaxLoadability(m, step, minStep, maxSteps);
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));
        Assert.True(actual > 1.0, $"case{n}: the base case should converge and the margin be positive (got {actual})");
    }

    [Theory]
    [InlineData("9")]
    [InlineData("14")]
    public void ScanGrid_reproduces_the_stress_verbs_old_prefix_rule_bit_for_bit(string n)
    {
        GridModel m = n == "9" ? IeeeCases.Case9() : IeeeCases.Case14();
        // A wide grid so the case collapses inside it (case9 and case14 both collapse well below 6x base).
        List<double> factors = StressFactors(0.5, 6.0, 0.25);

        // The verb's old code: solve every point, then the highest factor converged before the first failure.
        var converged = new bool[factors.Count];
        var vmins = new double[factors.Count];
        for (int i = 0; i < factors.Count; i++)
        {
            PowerFlowResult r = StressSolve(m, factors[i]);
            converged[i] = r.Converged;
            vmins[i] = r.Converged ? r.Vm.Min() : double.NaN;
        }
        int lastOk = -1;
        for (int i = 0; i < factors.Count; i++) { if (converged[i]) lastOk = i; else break; }

        LoadabilityGridScan scan = ContinuationPowerFlow.ScanGrid(factors, f => StressSolve(m, f), maxDegreeOfParallelism: 4);
        Assert.Equal(lastOk, scan.LastConvergedIndex);
        Assert.True(lastOk >= 0 && lastOk < factors.Count - 1, $"case{n}: expected a collapse inside the grid (lastOk {lastOk})");
        Assert.Equal(BitConverter.DoubleToInt64Bits(factors[lastOk]), BitConverter.DoubleToInt64Bits(scan.MaxFactor));
        Assert.Equal(BitConverter.DoubleToInt64Bits(vmins[lastOk]), BitConverter.DoubleToInt64Bits(scan.Results[lastOk]!.Vm.Min()));
        Assert.False(scan.ConvergedThroughout);
        Assert.Equal(factors.Count, scan.Results.Count);
    }

    [Fact]
    public void ScanGrid_is_the_same_serial_and_parallel()
    {
        GridModel m = IeeeCases.Case14();
        List<double> factors = StressFactors(0.5, 5.0, 0.5);
        LoadabilityGridScan serial = ContinuationPowerFlow.ScanGrid(factors, f => StressSolve(m, f), 1);
        LoadabilityGridScan parallel = ContinuationPowerFlow.ScanGrid(factors, f => StressSolve(m, f), 8);
        Assert.Equal(serial.LastConvergedIndex, parallel.LastConvergedIndex);
        for (int i = 0; i <= serial.LastConvergedIndex; i++)
            for (int k = 0; k < serial.Results[i]!.Vm.Length; k++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(serial.Results[i]!.Vm[k]), BitConverter.DoubleToInt64Bits(parallel.Results[i]!.Vm[k]));
    }

    [Fact]
    public void ScanGrid_counts_a_null_result_as_a_failure_and_stops_there()
    {
        GridModel m = IeeeCases.Case9();
        var factors = new List<double> { 0.8, 0.9, 1.0, 1.1 };
        // The point at 1.0 "throws" (null); 1.1 converges but comes after the failure, so it plays no part.
        LoadabilityGridScan scan = ContinuationPowerFlow.ScanGrid(factors, f => f == 1.0 ? null : StressSolve(m, f), 2);
        Assert.Equal(1, scan.LastConvergedIndex);
        Assert.Equal(0.9, scan.MaxFactor);
        Assert.Null(scan.Results[2]);
        Assert.True(scan.Results[3]!.Converged);
    }

    [Fact]
    public void ScanGrid_with_a_failing_first_point_or_an_empty_grid_reports_none()
    {
        GridModel m = IeeeCases.Case9();
        LoadabilityGridScan first = ContinuationPowerFlow.ScanGrid(new[] { 1.0, 1.1 }, _ => null, 1);
        Assert.Equal(-1, first.LastConvergedIndex);
        Assert.True(double.IsNaN(first.MaxFactor));
        Assert.False(first.ConvergedThroughout);

        LoadabilityGridScan empty = ContinuationPowerFlow.ScanGrid(Array.Empty<double>(), f => StressSolve(m, f), 1);
        Assert.Equal(-1, empty.LastConvergedIndex);
        Assert.True(double.IsNaN(empty.MaxFactor));
        Assert.False(empty.ConvergedThroughout);
    }

    [Fact]
    public void ScanGrid_reports_a_grid_that_never_collapses()
    {
        GridModel m = IeeeCases.Case9();
        LoadabilityGridScan scan = ContinuationPowerFlow.ScanGrid(new[] { 0.9, 1.0 }, f => StressSolve(m, f), 2);
        Assert.Equal(1, scan.LastConvergedIndex);
        Assert.True(scan.ConvergedThroughout);
    }

    [Fact]
    public void ScanGrid_refuses_a_parallelism_below_one()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ContinuationPowerFlow.ScanGrid(new[] { 1.0 }, _ => null, 0));
    }

    // ---- the restore verb (process tests: the built CLI) -----------------------------------------------------------

    [Fact]
    public void Restore_without_order_is_a_usage_error()
    {
        CliProcess.Result r = CliProcess.RunBuilt("restore", "case9");
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("--order is required", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_with_an_empty_entry_is_a_usage_error()
    {
        CliProcess.Result r = CliProcess.RunBuilt("restore", "case9", "--order", "#1,,#2");
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("--order", r.StdErr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("#0")]
    [InlineData("#10")]      // case9 has 9 branches
    [InlineData("no-such-branch")]
    [InlineData("#1,#1")]    // the same branch twice
    public void Restore_refuses_a_branch_it_cannot_sequence_with_exit_1(string order)
    {
        CliProcess.Result r = CliProcess.RunBuilt("restore", "case9", "--order", order);
        Assert.True(r.ExitCode == 1, r.ToString());
        Assert.StartsWith("restore: ", r.StdErr.Trim(), StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_refuses_a_flag_of_another_verb()
    {
        CliProcess.Result r = CliProcess.RunBuilt("restore", "case9", "--order", "#1", "--target", "1.0");
        Assert.True(r.ExitCode == 2, r.ToString());
    }

    [Fact]
    public void Restore_prints_one_row_per_step_and_its_exit_code_matches_the_verdict()
    {
        // case9 energised outward from the slack (bus 1): 1-4, 4-5, 4-9, 5-6, 6-3 (G3), 6-7, 7-8, 8-2 (G2), 8-9.
        // Rows in the bundled case9: #1 1-4, #2 4-5, #3 5-6, #4 3-6, #5 6-7, #6 7-8, #7 8-2, #8 8-9, #9 9-4.
        CliProcess.Result r = CliProcess.RunBuilt("restore", "case9", "--order", "#1,#2,#9,#3,#4,#5,#6,#7,#8");
        Assert.True(r.ExitCode is 0 or 3, r.ToString());
        Assert.Contains("RESTORATION - ", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("9 step(s)", r.StdOut, StringComparison.Ordinal);
        for (int k = 1; k <= 9; k++) Assert.Contains($"#{k}", r.StdOut, StringComparison.Ordinal);
        if (r.ExitCode == 0) Assert.Contains("sequence feasible", r.StdOut, StringComparison.Ordinal);
        else Assert.Contains("sequence NOT feasible", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_writes_its_result_file()
    {
        string json = Path.Combine(Path.GetTempPath(), $"gridsim-restore-{Guid.NewGuid():N}.json");
        try
        {
            CliProcess.Result r = CliProcess.RunBuilt("restore", "case9", "--order", "#1,#2", "--json", json);
            Assert.True(r.ExitCode is 0 or 3, r.ToString());
            string text = File.ReadAllText(json);
            Assert.Contains("\"restore\"", text, StringComparison.Ordinal);
            Assert.Contains("\"steps\"", text, StringComparison.Ordinal);
        }
        finally { if (File.Exists(json)) File.Delete(json); }
    }
}
