using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.Estimation;
using GridSim.Core.Estimation.Dse;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R062 (October 2026 review, remainder): the estimation capability that was test-only is now reachable from the CLI.
/// These tests pin the Core pieces the CLI calls - the HTI bad-data loop (<see cref="HtiBadDataEstimation"/>), the
/// estimate-export residual reader behind <c>estimate-noise-floor</c> (<see cref="EstimateExportResiduals"/>), and the
/// UKF / EnKF streaming engines behind <c>serve --method ukf|enkf</c> - plus source-level checks on the CLI wiring
/// (the test project does not reference the CLI).
/// </summary>
public class R062EstimationWiringTests
{
    private static (GridModel Model, PowerFlowResult Truth, MeasurementSet Exact) Case9()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        return (model, truth, WlsEstimatorTests.ExactFullSet(model, truth));
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
    }

    // ---- HTI ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Hti_on_a_clean_set_removes_nothing_and_is_bit_identical_to_plain_wls()
    {
        // With nothing to remove the HTI loop is one pass of the same estimator with the same options (the LNR cap
        // only matters once chi-square fails), so the state must match the plain estimate exactly.
        (GridModel model, _, MeasurementSet exact) = Case9();
        EstimationResult plain = new WlsStateEstimator().Estimate(model, exact, new EstimationOptions());
        HtiEstimationResult hti = HtiBadDataEstimation.Estimate(new WlsStateEstimator(), model, exact, new EstimationOptions());

        Assert.Equal(HtiStopReason.ChiSquarePassed, hti.StopReason);
        Assert.Empty(hti.Removed);
        Assert.Equal(1, hti.Passes);
        Assert.Equal(plain.Iterations, hti.TotalIterations);
        Assert.Equal(plain.ObjectiveJ, hti.Estimate.ObjectiveJ);
        for (int i = 0; i < model.BusCount; i++)
        {
            Assert.Equal(plain.Vm[i], hti.Estimate.Vm[i]);
            Assert.Equal(plain.VaDeg[i], hti.Estimate.VaDeg[i]);
        }
    }

    [Fact]
    public void Hti_identifies_and_removes_a_gross_error_and_the_final_fit_passes()
    {
        (GridModel model, PowerFlowResult truth, MeasurementSet exact) = Case9();
        var rows = new List<Measurement>(exact.Measurements);
        int bad = rows.FindIndex(m => m.Kind == MeasurementKind.VmPu);
        int badBus = rows[bad].Bus;
        rows[bad] = rows[bad] with { Value = rows[bad].Value + 0.25 };
        var set = exact with { Measurements = rows };

        HtiEstimationResult hti = HtiBadDataEstimation.Estimate(new WlsStateEstimator(), model, set, new EstimationOptions());

        Assert.True(hti.Estimate.Converged);
        Assert.Equal(HtiStopReason.ChiSquarePassed, hti.StopReason);
        Assert.Contains(hti.Removed, r => r.Identified.Measurement.Kind == MeasurementKind.VmPu && r.Identified.Measurement.Bus == badBus);
        Assert.All(hti.Removed, r => Assert.True(r.Identified.TestStatistic > 3.0));
        Assert.Empty(hti.Estimate.Removed);   // the WLS loop itself removed nothing: HTI did
        Assert.Equal(set.Measurements.Count - hti.Removed.Count, hti.Estimate.Residuals.Count);
        // With the gross error gone the exact set is fitted to the truth again.
        for (int i = 0; i < model.BusCount; i++)
            Assert.True(Math.Abs(hti.Estimate.Vm[i] - truth.Vm[i]) < 1e-6, $"bus {truth.BusIds[i]}: Vm {hti.Estimate.Vm[i]} vs {truth.Vm[i]}");
    }

    [Fact]
    public void Hti_respects_the_removal_cap_and_says_so()
    {
        (GridModel model, _, MeasurementSet exact) = Case9();
        var rows = new List<Measurement>(exact.Measurements);
        int bad = rows.FindIndex(m => m.Kind == MeasurementKind.VmPu);
        rows[bad] = rows[bad] with { Value = rows[bad].Value + 0.25 };

        HtiEstimationResult hti = HtiBadDataEstimation.Estimate(new WlsStateEstimator(), model, exact with { Measurements = rows },
            new EstimationOptions { MaxBadDataRemovals = 0 });

        Assert.Empty(hti.Removed);
        Assert.Equal(HtiStopReason.RemovalCapReached, hti.StopReason);
        Assert.Equal(ChiSquareOutcome.Failed, hti.Estimate.ChiSquareTest);   // the failure is reported, not hidden
    }

    [Fact]
    public void Hti_refuses_what_it_cannot_mean()
    {
        (GridModel model, _, MeasurementSet exact) = Case9();
        var est = new WlsStateEstimator();
        Assert.Throws<ArgumentException>(() => HtiBadDataEstimation.Estimate(est, model, exact, new EstimationOptions { DetectBadData = false }));
        foreach (EstimationMethod robust in new[] { EstimationMethod.Huber, EstimationMethod.Lav, EstimationMethod.SchweppeGm })
            Assert.Throws<NotSupportedException>(() => HtiBadDataEstimation.Estimate(est, model, exact, new EstimationOptions { Method = robust }));
        Assert.Throws<NotSupportedException>(() => HtiBadDataEstimation.Estimate(est, model, exact, new EstimationOptions { BackfillRemovedFromPrior = true }));
        Assert.Throws<ArgumentOutOfRangeException>(() => HtiBadDataEstimation.Estimate(est, model, exact, new EstimationOptions(), testThreshold: double.NaN));
    }

    // ---- estimate-noise-floor input ----------------------------------------------------------------------------

    [Fact]
    public void Export_residuals_read_back_exactly_and_give_the_same_noise_floor_report()
    {
        (GridModel model, PowerFlowResult truth, MeasurementSet exact) = Case9();
        // Deterministic noise so the normalised residuals are non-trivial.
        var rng = new Random(20261002);
        var noisy = exact.Measurements.Select(m => m with { Value = m.Value + m.Sigma * (rng.NextDouble() - 0.5) }).ToList();
        var set = exact with { Measurements = noisy };
        EstimationResult est = new WlsStateEstimator().Estimate(model, set);
        Assert.True(est.Converged);

        string dir = Path.Combine(Path.GetTempPath(), "gridsim-r062-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "estimate-export.json");
            EstimateExport.WriteJson(path, model, set, est, new SolveExportMeta("case9", "cpu", "test", "2026-10-02T00:00:00Z", 0, "test"));
            EstimateExportResidualTable table = EstimateExportResiduals.Load(path);

            Assert.True(table.Converged);
            Assert.Equal(est.Residuals.Count, table.Residuals.Count);
            for (int r = 0; r < est.Residuals.Count; r++)
            {
                MeasurementResidual a = est.Residuals[r], b = table.Residuals[r];
                Assert.Equal(a.Measurement.Kind, b.Measurement.Kind);
                Assert.Equal(a.Measurement.Source, b.Measurement.Source);
                Assert.Equal(a.Measurement.Value, b.Measurement.Value);
                Assert.Equal(a.Measurement.Sigma, b.Measurement.Sigma);
                Assert.Equal(a.Measurement.Describe(), b.Measurement.Describe());
                Assert.Equal(a.Predicted, b.Predicted);
                Assert.Equal(a.Residual, b.Residual);
                Assert.Equal(a.NormalizedResidual, b.NormalizedResidual);   // NaN equals NaN under Assert.Equal(double)
                Assert.Equal(a.IsCritical, b.IsCritical);
            }

            NoiseFloorReport direct = NoiseFloorAnalysis.Characterise(est.Residuals);
            NoiseFloorReport viaFile = NoiseFloorAnalysis.Characterise(table.Residuals);
            Assert.Equal(direct.TotalSamples, viaFile.TotalSamples);
            Assert.Equal(direct.SkippedNonFinite, viaFile.SkippedNonFinite);
            Assert.Equal(direct.Classes, viaFile.Classes);   // record equality: every statistic bit-identical
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Export_reader_fails_loud_on_a_foreign_document()
    {
        string path = Path.Combine(Path.GetTempPath(), "gridsim-r062-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"schema\":\"gridsim-solve-export/1\"}");
            Assert.Throws<InvalidDataException>(() => EstimateExportResiduals.Load(path));
            File.WriteAllText(path, "not json");
            Assert.Throws<InvalidDataException>(() => EstimateExportResiduals.Load(path));
        }
        finally { File.Delete(path); }
    }

    // ---- serve --method ukf|enkf ---------------------------------------------------------------------------------

    private static readonly DateTime Now = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

    private static MeasurementSet[] Stream(MeasurementSet template, int count)
        => Enumerable.Range(0, count).Select(k => template with { TakenUtc = Now.AddHours(-170).AddSeconds(k) }).ToArray();

    [Fact]
    public void Ukf_engine_frames_are_bit_identical_to_stepping_the_filter_directly()
    {
        (GridModel model, _, MeasurementSet exact) = Case9();
        var frames = new List<DseFrame>();
        RealtimeDseEngine engine = RealtimeDseEngine.Unscented(model);
        Assert.Equal(UnscentedKalmanStateEstimator.MethodName, engine.MethodName);
        RealtimeRunSummary summary = engine.Run(Stream(exact, 6), new RealtimeEstimatorOptions { UtcNow = Now }, frames.Add);
        Assert.Equal(6, summary.WindowsProcessed);

        var filter = new UnscentedKalmanStateEstimator(model);
        for (int k = 0; k < 6; k++)
        {
            DseEstimate direct = filter.Step(exact, 1.0);   // one-second windows: dt = 1 s each (and the first-window default)
            Assert.True(frames[k].Estimate.Converged);
            Assert.Equal(direct.Vm, frames[k].Estimate.Vm);
            Assert.Equal(direct.VaDeg, frames[k].Estimate.VaDeg);
        }
    }

    [Fact]
    public void Enkf_engine_frames_are_bit_identical_to_stepping_the_filter_directly()
    {
        (GridModel model, _, MeasurementSet exact) = Case9();
        var frames = new List<DseFrame>();
        RealtimeDseEngine engine = RealtimeDseEngine.Ensemble(model);
        Assert.Equal(EnsembleKalmanStateEstimator.MethodName, engine.MethodName);
        engine.Run(Stream(exact, 4), new RealtimeEstimatorOptions { UtcNow = Now }, frames.Add);

        var filter = new EnsembleKalmanStateEstimator(model);
        for (int k = 0; k < 4; k++)
        {
            DseEstimate direct = filter.Step(exact, 1.0);
            Assert.Equal(direct.Vm, frames[k].Estimate.Vm);
            Assert.Equal(direct.VaDeg, frames[k].Estimate.VaDeg);
        }
    }

    [Fact]
    public void Innovation_form_filters_refuse_an_oversized_window_loudly()
    {
        (GridModel model, _, MeasurementSet exact) = Case9();
        // Pad a window past the per-window measurement cap (the guard runs before the filter touches it).
        var rows = new List<Measurement>();
        while (rows.Count <= DenseEstimationLimits.InnovationFilterMaxMeasurements) rows.AddRange(exact.Measurements);
        MeasurementSet big = exact with { Measurements = rows, TakenUtc = Now.AddHours(-170) };
        var opt = new RealtimeEstimatorOptions { UtcNow = Now };
        Assert.Throws<NotSupportedException>(() => RealtimeDseEngine.Unscented(model).Run(new[] { big }, opt));
        Assert.Throws<NotSupportedException>(() => RealtimeDseEngine.Ensemble(model).Run(new[] { big }, opt));
    }

    [Fact]
    public void The_ekf_engine_still_reports_its_method()
        => Assert.Equal(KalmanStateEstimator.MethodName, new RealtimeDseEngine(IeeeCases.Case9()).MethodName);

    // ---- Stale documentation and experimental marking (source level) ---------------------------------------------

    [Fact]
    public void Stale_docs_are_corrected_and_test_only_classes_are_marked_experimental()
    {
        string decoder = RepoFile("src", "GridSim.Core", "Synchrophasor", "C37118Decoder.cs");
        Assert.DoesNotContain("The full CONFIG-2 parser, fixed-integer format, and multi-PMU/analog/digital fields are follow-ups", decoder);
        Assert.Contains("<see cref=\"C37118ConfigParser\"/>", decoder);

        string design = RepoFile("docs", "technical", "design", "robust-gm-leverage.md");
        Assert.DoesNotContain("Status: proposed, not yet implemented", design);
        Assert.Contains("| High-breakdown LTS / LMS (5) | NOT IMPLEMENTED |", design);

        Assert.DoesNotContain("Only wls ships today", RepoFile("src", "GridSim.Cli", "Program.cs"));

        foreach (string[] file in new[]
        {
            new[] { "src", "GridSim.Core", "Estimation", "LinearPmuStateEstimator.cs" },
            new[] { "src", "GridSim.Core", "Estimation", "Dse", "FrequencyRocofFilter.cs" },
            new[] { "src", "GridSim.Core", "Harmonics", "HarmonicStateEstimator.cs" },
            new[] { "src", "GridSim.Core", "Harmonics", "CoupledHarmonicPowerFlow.cs" },
            new[] { "src", "GridSim.Core", "Harmonics", "ThreePhaseHarmonics.cs" },
            new[] { "src", "GridSim.Core", "Synchrophasor", "PhasorDataConcentrator.cs" },
            new[] { "src", "GridSim.Core", "Synchrophasor", "PmuMeasurementSetMapper.cs" },
        })
            Assert.Contains("<b>Experimental</b> (R062, October 2026)", RepoFile(file));
    }

    // ---- CLI wiring (source level) ------------------------------------------------------------------------------

    [Fact]
    public void Cli_wires_hti_fdia_noise_floor_and_the_new_serve_methods()
    {
        string src = RepoFile("src", "GridSim.Cli", "Program.cs");
        int start = src.IndexOf("var knownFlags = new HashSet<string>", StringComparison.Ordinal);
        string flags = src[start..src.IndexOf("};", start, StringComparison.Ordinal)];
        foreach (string f in new[] { "\"--hti\",", "\"--fdia-screen\",", "\"--miscal-tol\",", "\"--min-samples\"," })
            Assert.Contains(f, flags);

        Assert.Contains("HtiBadDataEstimation.Estimate(new WlsStateEstimator(), model, set, eopt)", src);
        Assert.Contains("FdiaScreen.Screen(model, est)", src);
        Assert.Contains("if (fdiaFailed) Environment.ExitCode = 3;", src);
        Assert.Contains("NoiseFloorAnalysis.Characterise(residuals, tol, minSamples)", src);
        Assert.Contains("\"ukf\" => GridSim.Core.Simulation.RealtimeDseEngine.Unscented(model),", src);
        Assert.Contains("\"enkf\" => GridSim.Core.Simulation.RealtimeDseEngine.Ensemble(model),", src);
        Assert.Contains("if (method is not (\"wls\" or \"ekf\" or \"ukf\" or \"enkf\"))", src);

        string doc = RepoFile("docs", "manual", "0019 CLI reference.md");
        foreach (string s in new[] { "`--hti`", "`--fdia-screen`", "### estimate-noise-floor", "`ukf`", "`enkf`" })
            Assert.Contains(s, doc);
    }
}
