using GridSim.Core.Abstractions;
using GridSim.Core.Analysis;
using GridSim.Core.Estimation;
using GridSim.Core.Harmonics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Web.Api;
using GridSim.Web.Projection;
using GridSim.Web.Sessions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Ruling R1 (Mark delegated 2026-10-04, review-progress/2026-10/QUESTIONS-FOR-MARK.md "Rulings R1"): new tests for the
/// defaults and exit codes the ruling moved (agent "R1 defaults"). Each test names its finding. The existing
/// expectations the ruling moved carry their own "Moved 2026-10 (...)" comments where they live; these tests pin the
/// new behaviour and, wherever a choice restores the old view, that the old numbers are still reachable exactly.
/// </summary>
public class R1DefaultsTests
{
    private static string Program() =>
        File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Cli", "Program.cs"));

    private static void SameBits(double a, double b, string what) =>
        Assert.True(BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b), $"{what}: {a:R} vs {b:R}");

    // ---- N092 per-bus voltage limits on by default --------------------------------------------------------------

    [Fact]
    public void N092_per_bus_limits_are_the_cli_default_and_global_vlimits_restores_the_band()
    {
        string src = Program();
        // The default is per-bus; only --global-vlimits turns it off.
        Assert.Contains("bool busVlimits = !globalVlimits;", src, StringComparison.Ordinal);
        Assert.Contains("\"--global-vlimits\",", src, StringComparison.Ordinal);
        // Moved 2026-10 (D-DOC-118, docs-session finding, Ruling R1: Mark delegated 2026-10-04): nminus1-sweep now takes the
        // N092 default and --global-vlimits. Before: "error: --global-vlimits applies to nminus1, nminus2 and n-1-1 only".
        // After: "... applies to nminus1, nminus2, n-1-1 and nminus1-sweep only".
        Assert.Contains("error: --global-vlimits applies to nminus1, nminus2, n-1-1 and nminus1-sweep only", src, StringComparison.Ordinal);
        Assert.Contains("error: --bus-vlimits and --global-vlimits contradict each other", src, StringComparison.Ordinal);
        // The Core default is unchanged (a Core caller's options mean what they always meant).
        Assert.False(new PowerFlowOptions().PerBusVoltageLimits);
    }

    [Fact]
    public void N092_contradictory_and_misplaced_vlimit_flags_exit_2()
    {
        CliProcess.Result both = CliProcess.RunBuilt("nminus1", "case9", "--bus-vlimits", "--global-vlimits");
        Assert.True(both.ExitCode == 2, both.ToString());
        Assert.Contains("contradict each other", both.StdErr, StringComparison.Ordinal);

        CliProcess.Result elsewhere = CliProcess.RunBuilt("pf", "case9", "--global-vlimits");
        Assert.True(elsewhere.ExitCode == 2, elsewhere.ToString());
        // Moved 2026-10 (D-DOC-118, as above): the refusal on pf now names nminus1-sweep among the verbs that take it.
        // Before: "--global-vlimits applies to nminus1, nminus2 and n-1-1 only". After: "... n-1-1 and nminus1-sweep only".
        Assert.Contains("--global-vlimits applies to nminus1, nminus2, n-1-1 and nminus1-sweep only", elsewhere.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void N092_bus_vlimits_is_a_no_op_alias_of_the_new_default()
    {
        // The old opt-in now changes nothing: the default run and the --bus-vlimits run print the same report.
        CliProcess.Result byDefault = CliProcess.RunBuilt("nminus1", "case9");
        CliProcess.Result alias = CliProcess.RunBuilt("nminus1", "case9", "--bus-vlimits");
        Assert.Equal(byDefault.ExitCode, alias.ExitCode);
        Assert.Equal(byDefault.StdOut, alias.StdOut);
    }

    [Theory]
    [InlineData("nminus1-case9", "nminus1 case9")]
    [InlineData("nminus2-case9", "nminus2 case9 --top 5")]
    [InlineData("n-1-1-case9", "n-1-1 case9 --candidates 4")]
    // Moved 2026-10 (D-DOC-157, docs-session finding, Ruling R1: Mark delegated 2026-10-04): the fixture
    // nminus1-cigre-global-vlimits.txt printed the base-case overload of CIGRE MV's unnamed transformer with an empty name.
    // An unnamed branch is now named by its end buses, as the outage column already names it. Before: "... any outage:
    // branch  104%; undervoltage 0.923 p.u." After: "... any outage: branch 0-1 104%; undervoltage 0.923 p.u." (the only
    // byte that changes in that fixture; every number, the verdict and the exit code are as before).
    [InlineData("nminus1-cigre", "nminus1 cigre")]
    public void N092_global_vlimits_reproduces_the_pre_ruling_report_byte_for_byte(string name, string line)
    {
        // The <name>-global-vlimits fixtures are the pre-N092 captures of the default run: the case9 ones are the
        // fixtures of CliResultFilesProcessTests at 1.95.138 (the built-in case9 then carried no per-bus limits, so its
        // default report did not move under N092; since N199 it carries case9.m's limits and its nminus1 default moved,
        // while these band-view fixtures, still matched exactly, did not), nminus1-cigre is the 1.95.136 CLI's output (CIGRE MV carries 0.9-1.1 pu limits, so its
        // default report did move). The old view must still be reachable exactly, verdict and exit code included.
        CliProcess.Result r = CliProcess.RunBuilt(CliResultFilesProcessTests.Args(line + " --global-vlimits"));
        Assert.True(r.ExitCode == 3, r.ToString());
        Assert.Equal(CliResultFilesProcessTests.Fixture(name + "-global-vlimits"), CliResultFilesProcessTests.Normalise(r.StdOut));
    }

    [Fact]
    public void N092_cigre_default_judges_its_undervoltage_against_its_own_limit()
    {
        // Before -> after (measured, r1-defaults notes): the 0.923 pu undervoltage is outside the band (0.94) but inside
        // CIGRE MV's own 0.9 pu limit, so the base case goes from 2 pre-existing violations to 1 (branch 104 %).
        CliProcess.Result r = CliProcess.RunBuilt("nminus1", "cigre");
        Assert.True(r.ExitCode == 3, r.ToString());
        Assert.Contains("Verdict: NOT SECURE, 1 base-case violation(s) and 14 insecure outage(s) (exit 3).", r.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("undervoltage 0.923", r.StdOut, StringComparison.Ordinal);
    }

    // ---- N057 always sparse ---------------------------------------------------------------------------------------

    [Fact]
    public void N057_every_meshed_case_routes_sparse_and_solves_to_the_dense_bits()
    {
        foreach (GridModel m in new[] { IeeeCases.Case9(), IeeeCases.Case14(), IeeeCases.Case30() })
        {
            Assert.Equal(SolverKind.SparseNewton, SolverSelector.ResolveNewton(m));
            Assert.IsType<SparseNewtonRaphsonPowerFlow>(SolverSelector.CreateNewton(m));
            var opt = new PowerFlowOptions { EnforceQLimits = true };
            PowerFlowResult byDefault = new SteadyStateSolver().Solve(m, opt);
            PowerFlowResult dense = new SteadyStateSolver(new NewtonRaphsonPowerFlow()).Solve(m, opt);
            Assert.Equal(dense.Converged, byDefault.Converged);
            Assert.Equal(dense.Iterations, byDefault.Iterations);
            for (int i = 0; i < m.BusCount; i++)
            {
                SameBits(dense.Vm[i], byDefault.Vm[i], $"{m.Name} Vm[{i}]");
                SameBits(dense.VaDeg[i], byDefault.VaDeg[i], $"{m.Name} Va[{i}]");
            }
            SameBits(dense.TotalLossMw, byDefault.TotalLossMw, m.Name + " loss");
        }
    }

    [Fact]
    public void N057_the_estimator_gpu_and_export_keep_the_old_crossover()
    {
        Assert.Equal(200, new EstimationOptions().SparseBusThreshold);
        string backend = File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Cli", "SolverBackend.cs"));
        Assert.Contains("Device == Vendor.Cpu ? SolverSelector.DefaultSparseThreshold : SolverSelector.DenseSparseCrossoverBuses", backend, StringComparison.Ordinal);
        Assert.Contains("public IPowerFlowSolver ExportSolver(GridModel m) => SolverSelector.Resolve(m, SolverSelector.DenseSparseCrossoverBuses)", backend, StringComparison.Ordinal);
        string src = Program();
        Assert.Contains("IPowerFlowSolver raw = bfs ? new BackwardForwardSweep() : backend.ExportSolver(m);", src, StringComparison.Ordinal);
        Assert.Contains("IPowerFlowSolver raw = backend.ExportSolver(m);", src, StringComparison.Ordinal);   // crossval
    }

    // ---- N131 G5 six-pulse default harmonic source -----------------------------------------------------------------

    [Fact]
    public void N131_the_default_synthetic_source_is_the_g5_six_pulse_profile_and_ideal_is_the_1_over_h()
    {
        Assert.Equal("6-pulse-g5", HarmonicSourceLibrary.DefaultSyntheticSourceType);
        Assert.Equal("ideal", HarmonicSourceLibrary.IdealSixPulseType);
        // "ideal" is exactly the 1/h six-pulse spectrum, and "6-pulse" keeps that meaning.
        IReadOnlyList<HarmonicComponent> ideal = HarmonicSourceLibrary.ByType["ideal"];
        IReadOnlyList<HarmonicComponent> sixPulse = HarmonicSourceLibrary.ByType["6-pulse"];
        Assert.Equal(sixPulse.Count, ideal.Count);
        for (int i = 0; i < ideal.Count; i++)
        {
            Assert.Equal(sixPulse[i].Order, ideal[i].Order);
            SameBits(sixPulse[i].MagnitudePercent, ideal[i].MagnitudePercent, $"order {ideal[i].Order}");
            SameBits(100.0 / ideal[i].Order, ideal[i].MagnitudePercent, $"1/h at order {ideal[i].Order}");
        }
        // The G5 profile's orders all sit inside the default study's analysed orders, so nothing is dropped.
        Assert.Empty(HarmonicPenetration.ComponentsOutsideOrders(
            new[] { HarmonicSourceLibrary.Make(HarmonicSourceLibrary.DefaultSyntheticSourceType, 1, 0.5) }, HarmonicModelOptions.Study));
    }

    [Fact]
    public void N131_web_default_uses_g5_and_the_ideal_choice_reproduces_the_old_study_bit_for_bit()
    {
        GridModel m = IeeeCases.Case9();
        PowerFlowResult r = new SteadyStateSolver().Solve(m);

        HarmonicsDto g5 = SimSession.StudyHarmonics(m, r, HarmonicSourceChoice.Synthetic);
        HarmonicsDto ideal = SimSession.StudyHarmonics(m, r, HarmonicSourceChoice.SyntheticIdeal);
        Assert.Null(g5.Error);
        Assert.Null(ideal.Error);
        Assert.Contains("6-pulse-g5", g5.SourceNote, StringComparison.Ordinal);
        Assert.Contains("G5 Issue 5 Table B.3", g5.ProvenanceTag, StringComparison.Ordinal);
        Assert.Equal("assumed-typical - synthetic 6-pulse source [VERIFY]", ideal.ProvenanceTag);

        // The old default study, built directly: one 1/h six-pulse source of 0.5 pu at the largest-demand bus.
        int srcBus = m.Buses.OrderByDescending(b => b.Pd).First().Id;
        HarmonicPenetrationResult pen = HarmonicPenetration.Solve(m,
            new[] { HarmonicSourceLibrary.Make("6-pulse", srcBus, 0.5) }, HarmonicModelOptions.Study);
        HarmonicIndicesResult idx = HarmonicIndices.FromPenetration(pen, HarmonicIndices.SolvedFundamentalVm(pen, r));
        Assert.Equal(idx.WorstBusId, ideal.Indices.WorstBusId);
        SameBits(idx.WorstThdPct, ideal.Indices.WorstThdPct, "worst THD (ideal)");
        // And the G5 default is a different (larger 5th harmonic) study.
        Assert.NotEqual(BitConverter.DoubleToInt64Bits(ideal.Indices.WorstThdPct), BitConverter.DoubleToInt64Bits(g5.Indices.WorstThdPct));
    }

    // ---- N161 cpf on the study options -----------------------------------------------------------------------------

    /// <summary>The continuation loop written out with the study solver and options (the reference for N161).</summary>
    private static double ReferenceStudyLoadability(GridModel model, double step, double minStep, int maxSteps)
    {
        IPowerFlowSolver solver = ContingencyAnalysis.WithOltcLoop(SolverSelector.CreateNewton(model));
        double lambda = 1.0, lastGood = 0.0;
        int steps = 0;
        while (step >= minStep && steps++ < maxSteps)
        {
            PowerFlowResult r = solver.Solve(ContinuationPowerFlow.ScaleLoads(model, lambda), ContingencyAnalysis.StudyOptions);
            if (r.Converged) { lastGood = lambda; lambda += step; }
            else { lambda -= step; step *= 0.5; lambda += step; }
        }
        return lastGood;
    }

    [Theory]
    [InlineData("9")]
    [InlineData("14")]
    [InlineData("30")]
    public void N161_study_loadability_is_the_study_loop_bit_for_bit_and_the_trace_agrees(string n)
    {
        GridModel m = n switch { "9" => IeeeCases.Case9(), "14" => IeeeCases.Case14(), _ => IeeeCases.Case30() };
        double expected = ReferenceStudyLoadability(m, 0.25, 1e-3, 200);
        double actual = ContinuationPowerFlow.MaxLoadability(m,
            ContingencyAnalysis.WithOltcLoop(SolverSelector.CreateNewton(m)), ContingencyAnalysis.StudyOptions);
        SameBits(expected, actual, $"case{n} study max lambda");
        PvCurve curve = ContinuationPowerFlow.TracePvCurve(m,
            ContingencyAnalysis.WithOltcLoop(SolverSelector.CreateNewton(m)), ContingencyAnalysis.StudyOptions);
        SameBits(actual, curve.MaxLambda, $"case{n} trace max lambda");
        Assert.True(actual > 1.0, $"case{n}: the base case converges under the study options (got {actual})");
    }

    [Fact]
    public void N161_the_cpf_verb_passes_the_study_solver_and_options()
    {
        string src = Program();
        Assert.Contains("IPowerFlowSolver cpfSolver = ContingencyAnalysis.WithOltcLoop(backend.NewtonSolver(m));", src, StringComparison.Ordinal);
        Assert.Contains("PowerFlowOptions cpfOptions = ContingencyAnalysis.StudyOptions;", src, StringComparison.Ordinal);
        // Moved 2026-10 (D-DOC-129, docs-session finding, Ruling R1: Mark delegated 2026-10-04): the verb printed "nose
        // bracketed" even when --max-steps ran out, because MaxLoadability returns only the multiplier. It now calls
        // MaxLoadabilityWithEnd, the same loop (the multiplier is the same bits, Doc11OpfContingencyStorageTests D129) that
        // also says how the walk ended; the study solver and options are passed exactly as before. Before:
        // "ContinuationPowerFlow.MaxLoadability(m, cpfSolver, cpfOptions, step, minStep, maxSteps)". After: the line below.
        Assert.Contains("ContinuationPowerFlow.MaxLoadabilityWithEnd(m, cpfSolver, cpfOptions, step, minStep, maxSteps)", src, StringComparison.Ordinal);
    }

    // ---- N154 /ws/sim wave field opt-in ----------------------------------------------------------------------------

    [Fact]
    public void N154_ws_sim_strips_the_wave_field_unless_asked_and_keeps_the_rest_of_experimental()
    {
        SimFrame f = WebProjectionFixtures.Solve(WebProjectionFixtures.MixedFleet());
        var wave = new WaveFieldDto(0.5, 1, new[] { new WaveNodeDto(1, 1.0, 0.0), new WaveNodeDto(2, 0.5, 0.3) });
        var experimental = new ExperimentalDto("Pass", 1e-12, 0.7, "split", Array.Empty<TraceFlowDto>(), 200.0, false, wave);
        FrameEnvelope env = FrameProjector.Project(f) with { Experimental = experimental };

        var shape = new SimWebSocket.FrameShape();
        FrameEnvelope byDefault = shape.Apply(env);
        Assert.NotNull(byDefault.Experimental);
        Assert.Null(byDefault.Experimental!.WaveField);
        Assert.Equal(experimental with { WaveField = null }, byDefault.Experimental);   // everything else kept

        shape.IncludeWaveField = true;
        shape.IncludeAssetGraph = true;
        Assert.Same(env, shape.Apply(env));                    // asked for: the frame is passed through untouched

        FrameEnvelope noWave = env with { Experimental = experimental with { WaveField = null } };
        shape.IncludeWaveField = false;
        Assert.Same(noWave, shape.Apply(noWave));              // nothing to strip: the same instance
    }

    // ---- N078 prior-solved rows are not chi-square redundancy --------------------------------------------------------

    [Fact]
    public void N078_prior_solved_rows_leave_j_and_the_degrees_of_freedom_but_not_the_fit()
    {
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet baseSet = SyntheticMeasurements.Sample(model, truth);
        // Retag every voltage-magnitude row: once as the solved prior, once as the schedule pseudo it replaces.
        MeasurementSet Tagged(string source) => baseSet with
        {
            Measurements = baseSet.Measurements.Select(x => x.Kind == MeasurementKind.VmPu ? x with { Source = source } : x).ToArray(),
        };
        int k = baseSet.Measurements.Count(x => x.Kind == MeasurementKind.VmPu);
        Assert.True(k > 0);

        var opt = new EstimationOptions { DetectBadData = false };   // no removal loop: the two fits are the same fit
        EstimationResult prior = new WlsStateEstimator().Estimate(model, Tagged(MeasuredInjectionPrior.SolvedPriorSource), opt);
        EstimationResult schedule = new WlsStateEstimator().Estimate(model, Tagged("pseudo-voltage-schedule"), opt);
        Assert.Equal(EstimationStatus.Converged, prior.Status);
        Assert.Equal(EstimationStatus.Converged, schedule.Status);

        // The fit is untouched: the tag changes no weight, so the state is the same bits.
        Assert.Equal(schedule.Iterations, prior.Iterations);
        for (int i = 0; i < model.BusCount; i++)
        {
            SameBits(schedule.Vm[i], prior.Vm[i], $"Vm[{i}]");
            SameBits(schedule.VaDeg[i], prior.VaDeg[i], $"Va[{i}]");
        }
        // The prior-solved rows are still listed with their residuals.
        Assert.Equal(schedule.Residuals.Count, prior.Residuals.Count);

        // Degrees of freedom: exactly k fewer. J: the schedule J less the prior rows' share.
        Assert.Equal(schedule.DegreesOfFreedom - k, prior.DegreesOfFreedom);
        double priorShare = prior.Residuals.Where(x => x.Measurement.Source == MeasuredInjectionPrior.SolvedPriorSource)
            .Sum(x => (x.Residual / x.Measurement.Sigma) * (x.Residual / x.Measurement.Sigma));
        Assert.True(priorShare > 0);
        Assert.True(Math.Abs(schedule.ObjectiveJ - priorShare - prior.ObjectiveJ) <= 1e-9 * schedule.ObjectiveJ,
            $"J {prior.ObjectiveJ:R} + prior share {priorShare:R} vs {schedule.ObjectiveJ:R}");
    }

    [Fact]
    public void N078_the_chi_square_threshold_uses_the_reduced_degrees_of_freedom()
    {
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet exact = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { NoiseScale = 0 });
        MeasurementSet Tagged(string source) => exact with
        {
            Measurements = exact.Measurements.Select(x => x.Kind == MeasurementKind.VmPu ? x with { Source = source } : x).ToArray(),
        };
        EstimationResult prior = new WlsStateEstimator().Estimate(model, Tagged(MeasuredInjectionPrior.SolvedPriorSource));
        EstimationResult schedule = new WlsStateEstimator().Estimate(model, Tagged("pseudo-voltage-schedule"));
        Assert.Equal(ChiSquareOutcome.Passed, schedule.ChiSquareTest);
        Assert.Equal(ChiSquareOutcome.Passed, prior.ChiSquareTest);
        SameBits(ChiSquare.Quantile(prior.DegreesOfFreedom, 0.95), prior.ChiSquareThreshold, "threshold at the reduced dof");
        SameBits(ChiSquare.Quantile(schedule.DegreesOfFreedom, 0.95), schedule.ChiSquareThreshold, "threshold at the full dof");
        Assert.True(prior.ChiSquareThreshold < schedule.ChiSquareThreshold);
    }

    // ---- N079 default output under results/ ------------------------------------------------------------------------

    [Fact]
    public void N079_no_verb_writes_its_default_output_beside_the_executable()
    {
        string src = Program();
        Assert.DoesNotContain("Path.Combine(root, \"results\"", src, StringComparison.Ordinal);
        Assert.DoesNotContain("Path.Combine(baseDir, \"gridviz.json\")", src, StringComparison.Ordinal);
        Assert.DoesNotContain("?? \"emt-out\"", src, StringComparison.Ordinal);
        Assert.DoesNotContain("?? \"harmonic-export.json\"", src, StringComparison.Ordinal);
        Assert.DoesNotContain("\"docs\", \"benchmark-results.md\"", src, StringComparison.Ordinal);
        Assert.Contains("static string DefaultResultsPath(params string[] parts) =>", src, StringComparison.Ordinal);
    }

    [Fact]
    public void N079_export_writes_its_default_scene_under_results_in_the_current_directory()
    {
        CliProcess.Result r = CliProcess.RunBuilt("export", "case9", "--frames", "2");
        Assert.True(r.ExitCode == 0, r.ToString());
        string line = r.StdOut.Split('\n').First(l => l.StartsWith("Exported", StringComparison.Ordinal)).Trim();
        string written = line[(line.IndexOf("-> ", StringComparison.Ordinal) + 3)..];
        Assert.EndsWith(Path.Combine("results", "gridviz.json"), written, StringComparison.Ordinal);
        // The run's working folder is a fresh temp folder (CliProcess), never the CLI's own build folder.
        Assert.DoesNotContain(Path.Combine("src", "GridSim.Cli", "bin"), written, StringComparison.OrdinalIgnoreCase);
    }
}
