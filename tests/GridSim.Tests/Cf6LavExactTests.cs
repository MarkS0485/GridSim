using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// CF6 (N356, ruled by main under Ruling R1): <see cref="EstimationMethod.Lav"/> is the exact least-absolute-values
/// estimate (CF5's linear-programming form), <see cref="EstimationMethod.LavLp"/> is an alias of it with identical
/// results, and the old IRLS form is <see cref="EstimationMethod.LavIrls"/>, unchanged and marked approximate. A LAV
/// estimate reports its own objective, sum |r| / sigma (<see cref="EstimationResult.LavObjective"/>), and its chi-square
/// figures are not a verdict (<see cref="EstimationResult.ChiSquareIsVerdict"/> false). Every other method's result is
/// unchanged (ChiSquareIsVerdict true, LavObjective null).
/// </summary>
public class Cf6LavExactTests
{
    private static (GridModel Model, PowerFlowResult Truth, MeasurementSet Set) Case14(int seed)
    {
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(truth.Converged);
        return (model, truth, SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { Seed = seed }));
    }

    /// <summary>The Q13 / N064 set: exact case14 measurements with one P injection pushed 30 sigma.</summary>
    private static (GridModel Model, PowerFlowResult Truth, MeasurementSet Bad) Q13Set()
    {
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet exact = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { NoiseScale = 0 });
        var ms = exact.Measurements.ToList();
        int victim = ms.FindIndex(x => x.Kind == MeasurementKind.PInjMw);
        ms[victim] = ms[victim] with { Value = ms[victim].Value + 30 * ms[victim].Sigma };
        return (model, truth, exact with { Measurements = ms });
    }

    private static EstimationResult Run(GridModel model, MeasurementSet set, EstimationMethod method) =>
        new WlsStateEstimator().Estimate(model, set, new EstimationOptions { Method = method });

    // ------------------------------------------------------------------ lav == lav-lp, bit for bit

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Lav_and_its_alias_lav_lp_give_bit_identical_results(int seed)
    {
        (GridModel model, _, MeasurementSet set) = Case14(seed);
        EstimationResult lav = Run(model, set, EstimationMethod.Lav);
        EstimationResult alias = Run(model, set, EstimationMethod.LavLp);
        Assert.Equal(EstimationStatus.Converged, lav.Status);
        Assert.Equal(lav.Status, alias.Status);
        Assert.Equal(lav.Iterations, alias.Iterations);
        Assert.Equal(BitConverter.DoubleToInt64Bits(lav.ObjectiveJ), BitConverter.DoubleToInt64Bits(alias.ObjectiveJ));
        Assert.NotNull(lav.LavObjective);
        Assert.Equal(BitConverter.DoubleToInt64Bits(lav.LavObjective!.Value), BitConverter.DoubleToInt64Bits(alias.LavObjective!.Value));
        for (int i = 0; i < model.BusCount; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(lav.Vm[i]), BitConverter.DoubleToInt64Bits(alias.Vm[i]));
            Assert.Equal(BitConverter.DoubleToInt64Bits(lav.VaDeg[i]), BitConverter.DoubleToInt64Bits(alias.VaDeg[i]));
        }
    }

    [Theory]
    // J printed by CF5's probe for `--method lav-lp` (Cf5LavLpTests pins the same three values on LavLp); Lav is now
    // the same estimator, so it lands on the same J.
    [InlineData(1, 111.66)]
    [InlineData(2, 87.62)]
    [InlineData(3, 89.79)]
    public void Lav_is_now_the_exact_form_with_no_irls_cap(int seed, double jPrinted)
    {
        (GridModel model, _, MeasurementSet set) = Case14(seed);
        EstimationResult lav = Run(model, set, EstimationMethod.Lav);
        Assert.Equal(EstimationStatus.Converged, lav.Status);
        Assert.False(lav.IrlsCapReached);
        Assert.InRange(lav.ObjectiveJ, jPrinted - 0.005, jPrinted + 0.005);
    }

    // ------------------------------------------------------------------ the Q13 / N064 set

    [Fact]
    public void On_the_q13_set_exact_lav_converges_without_the_cap_and_recovers_the_truth()
    {
        // The set on which the IRLS LAV runs to its cap (Q13WsThirteenDecisionTests, N064AuditShowAndIrlsExportTests).
        // The exact optimum interpolates s good rows; the bad row carries the whole 30 sigma, so the state is the truth
        // to round-off (the same bounds as Cf5LavLpTests.Lav_lp_rejects_a_gross_error_exactly_on_an_exact_set).
        (GridModel model, PowerFlowResult truth, MeasurementSet bad) = Q13Set();
        EstimationResult lav = Run(model, bad, EstimationMethod.Lav);
        Assert.Equal(EstimationStatus.Converged, lav.Status);
        Assert.False(lav.IrlsCapReached);
        for (int i = 0; i < model.BusCount; i++)
        {
            Assert.True(Math.Abs(lav.Vm[i] - truth.Vm[i]) < 1e-6, $"bus {i}: Vm {lav.Vm[i]:R} vs truth {truth.Vm[i]:R}");
            Assert.True(Math.Abs(lav.VaDeg[i] - truth.VaDeg[i]) < 1e-4, $"bus {i}: Va {lav.VaDeg[i]:R} vs truth {truth.VaDeg[i]:R}");
        }
    }

    [Fact]
    public void Lav_irls_keeps_the_old_behaviour_on_the_q13_set_cap_reached_status_converged()
    {
        // The IRLS loop is the pre-N356 Lav code under a new name, so the moved Q13 expectation holds here verbatim.
        (GridModel model, _, MeasurementSet bad) = Q13Set();
        EstimationResult irls = Run(model, bad, EstimationMethod.LavIrls);
        Assert.Equal(EstimationStatus.Converged, irls.Status);
        Assert.True(irls.IrlsCapReached);
        Assert.False(irls.ChiSquareIsVerdict);
        Assert.NotNull(irls.LavObjective);
    }

    [Fact]
    public void The_irls_cap_constants_are_unchanged()
    {
        Assert.Equal(10, WlsStateEstimator.IrlsMaxOuterIterations);
        Assert.Equal(50, WlsStateEstimator.IrlsMaxOuterIterationsMEstimator);
        Assert.Equal(1e-3, WlsStateEstimator.LavResidualFloor);
    }

    // ------------------------------------------------------------------ the LAV objective and the verdict flag

    [Theory]
    [InlineData(EstimationMethod.Lav)]
    [InlineData(EstimationMethod.LavLp)]
    [InlineData(EstimationMethod.LavIrls)]
    public void A_lav_estimate_reports_its_objective_and_no_chi_square_verdict(EstimationMethod method)
    {
        (GridModel model, _, MeasurementSet set) = Case14(2);
        EstimationResult e = Run(model, set, method);
        Assert.Equal(EstimationStatus.Converged, e.Status);
        Assert.False(e.ChiSquareIsVerdict);
        Assert.NotNull(e.LavObjective);
        Assert.True(double.IsFinite(e.LavObjective!.Value) && e.LavObjective.Value > 0.0);

        // The objective recomputed from the residual table, in engineering units (residual / sigma is unit-free, so
        // the per-unit sum the estimator forms is the same number up to the per-unit scaling round-off).
        double fromTable = 0.0;
        foreach (MeasurementResidual r in e.Residuals) fromTable += Math.Abs(r.Residual) / r.Measurement.Sigma;
        Assert.True(Math.Abs(fromTable - e.LavObjective.Value) <= 1e-9 * (1.0 + fromTable),
            $"LAV objective {e.LavObjective.Value:R} vs the residual table's {fromTable:R}");
    }

    [Theory]
    [InlineData(EstimationMethod.Wls)]
    [InlineData(EstimationMethod.Huber)]
    [InlineData(EstimationMethod.SchweppeGm)]
    [InlineData(EstimationMethod.Qr)]
    public void Every_other_method_keeps_its_chi_square_verdict_and_has_no_lav_objective(EstimationMethod method)
    {
        (GridModel model, _, MeasurementSet set) = Case14(2);
        EstimationResult e = Run(model, set, method);
        Assert.True(e.ChiSquareIsVerdict);
        Assert.Null(e.LavObjective);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void The_exact_lav_objective_is_no_larger_than_the_irls_approximation(int seed)
    {
        // The exact form minimises sum |r| / sigma; IRLS only approximates the minimiser, so its objective cannot be
        // smaller (up to round-off) on the same set.
        (GridModel model, _, MeasurementSet set) = Case14(seed);
        double exact = Run(model, set, EstimationMethod.Lav).LavObjective!.Value;
        double irls = Run(model, set, EstimationMethod.LavIrls).LavObjective!.Value;
        Assert.True(exact <= irls * (1.0 + 1e-9), $"exact LAV objective {exact:R} above the IRLS one {irls:R}");
    }

    [Fact]
    public void Hti_refuses_lav_irls_as_well()
    {
        (GridModel model, _, MeasurementSet set) = Case14(1);
        Assert.Throws<NotSupportedException>(() =>
            HtiBadDataEstimation.Estimate(new WlsStateEstimator(), model, set, new EstimationOptions { Method = EstimationMethod.LavIrls }));
    }

    [Fact]
    public void Lav_irls_is_appended_so_older_members_keep_their_ordinals()
    {
        Assert.Equal(0, (int)EstimationMethod.Wls);
        Assert.Equal(2, (int)EstimationMethod.Lav);
        Assert.Equal(7, (int)EstimationMethod.LavLp);
        Assert.Equal(8, (int)EstimationMethod.LavIrls);
    }

    // ------------------------------------------------------------------ export and audit (main's ruling on CF6-F1)

    private static readonly SolveExportMeta ExportMeta = new("case14", "cpu", "c", "2026-10-11T00:00:00Z", 1.0, "test");

    [Theory]
    [InlineData(EstimationMethod.Lav)]
    [InlineData(EstimationMethod.LavIrls)]
    public void The_estimate_export_of_a_lav_run_has_no_chi_square_verdict_and_carries_the_objective(EstimationMethod method)
    {
        (GridModel model, _, MeasurementSet set) = Case14(2);
        EstimationResult e = Run(model, set, method);
        System.Text.Json.JsonElement est = System.Text.Json.JsonDocument.Parse(EstimateExport.ToJson(model, set, e, ExportMeta)).RootElement.GetProperty("estimate");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, est.GetProperty("chiSquarePassed").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, est.GetProperty("chiSquareTest").ValueKind);
        Assert.False(est.GetProperty("chiSquareVerdict").GetBoolean());
        Assert.Equal(e.LavObjective!.Value, est.GetProperty("lavObjective").GetDouble());
        string[] names = est.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[]
        {
            "status", "converged", "iterations", "maxStateUpdate", "objectiveJ", "degreesOfFreedom",
            "chiSquareThreshold", "chiSquarePassed", "chiSquareTest", "irlsCapReached", "chiSquareVerdict", "lavObjective", "buses",
        }, names);
    }

    [Fact]
    public void The_export_of_a_wls_run_keeps_its_exact_shape()
    {
        (GridModel model, _, MeasurementSet set) = Case14(2);
        EstimationResult e = Run(model, set, EstimationMethod.Wls);
        System.Text.Json.JsonElement est = System.Text.Json.JsonDocument.Parse(EstimateExport.ToJson(model, set, e, ExportMeta)).RootElement.GetProperty("estimate");
        Assert.False(est.TryGetProperty("lavObjective", out _));
        Assert.False(est.TryGetProperty("chiSquareVerdict", out _));
    }

    [Fact]
    public void The_audit_line_of_a_lav_run_says_no_verdict_and_gives_the_objective()
    {
        (GridModel model, _, MeasurementSet set) = Case14(2);
        EstimationResult e = Run(model, set, EstimationMethod.Lav);
        string line = EstimationAuditTrail.Outcome("Lav", e, set.Measurements.Count);
        Assert.Contains(" chi2=no-verdict lav_objective=", line, StringComparison.Ordinal);
        Assert.DoesNotContain("chi2=PASS", line, StringComparison.Ordinal);
        Assert.DoesNotContain("chi2=FAIL", line, StringComparison.Ordinal);
        string wls = EstimationAuditTrail.Outcome("Wls", Run(model, set, EstimationMethod.Wls), set.Measurements.Count);
        Assert.DoesNotContain("lav_objective", wls, StringComparison.Ordinal);
    }

    [Fact]
    public void The_dense_cap_refusal_names_the_cap_and_suggests_lav_irls()
    {
        string core = File.ReadAllText(Path.Combine(RepoRoot(), "src", "GridSim.Core", "Estimation", "WlsStateEstimator.cs"));
        Assert.Contains("(the dense-method cap, DenseEstimationLimits.StateCovarianceMaxState)", core, StringComparison.Ordinal);
        Assert.Contains("Use --method lav-irls (LAV by IRLS, approximate, no state cap)", core, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ CLI wiring (source level)

    [Fact]
    public void Cli_maps_lav_lav_lp_and_lav_irls_and_prints_the_lav_objective_without_a_verdict()
    {
        string cli = File.ReadAllText(Path.Combine(RepoRoot(), "src", "GridSim.Cli", "Program.cs"));
        Assert.Contains("\"lav\" => EstimationMethod.Lav,", cli);
        Assert.Contains("\"lav-lp\" => EstimationMethod.LavLp,", cli);
        Assert.Contains("\"lav-irls\" => EstimationMethod.LavIrls,", cli);
        Assert.Contains("EstimationMethod.Lav or EstimationMethod.LavLp => \"LAV (linear programming)\"", cli);
        Assert.Contains("NOTE: --method lav-irls is LAV by IRLS, an approximation", cli);
        Assert.Contains("objective: sum |r| / sigma = ", cli);
        Assert.Contains("consistency: no chi-square verdict", cli);
        Assert.Contains("NOTE: the LAV objective is not chi-square distributed", cli);
        string ch19 = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "manual", "0019 CLI reference.md"));
        Assert.Contains("`lav-irls`", ch19);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("GridSim.slnx not found above the test binaries.");
    }
}
