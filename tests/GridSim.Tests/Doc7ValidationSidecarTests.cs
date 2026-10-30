using System.Text.Json;
using GridSim.Cli;
using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.Physics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// DOC-7 (October 2026): validation verbs and sidecars, docs-session findings D-DOC-039..045, 125..128, 188, 190 and
/// N219 (review-progress/2026-10/DOCS-FINDINGS-TRIAGE.md, stream 7). These are checks, scoring, sidecars and messages
/// only: no solver number changes, and every test below that touches a solve reads the solve as it always was.
/// </summary>
public sealed class Doc7ValidationSidecarTests
{
    private static string Root() => CliProcess.Root();

    private static string Tool(string name)
    {
        string path = Path.Combine(Root(), "tools", "crossval", name);
        Assert.True(File.Exists(path), path);
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    private static LawResult Law(PhysicsLawReport r, int number) => r.Laws.Single(l => l.Number == number);

    private static GridModel Uk() => JsonGridLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "data", "uk"));

    // ---- D-DOC-125 / D-DOC-040: one documented tolerance rule ---------------------------------------------------

    [Fact]
    public void D125_default_tolerance_is_the_solver_convergence_tolerance_on_the_case_base()
    {
        // The per-unit constant IS the power-flow default tolerance (one number, two names).
        Assert.Equal(PhysicsLaws.DefaultTolerancePu, new PowerFlowOptions().Tolerance);
        // On a 100 MVA base the rule reproduces the old absolute 1e-6 exactly (same double), so every 100 MVA
        // verdict is unchanged; on uk's 10,000 MVA base it is exactly 1e-4.
        Assert.Equal(1e-6, PhysicsLaws.DefaultTolMva(IeeeCases.Case14()));
        GridModel uk = Uk();
        Assert.Equal(10000.0, uk.BaseMva);
        Assert.Equal(1e-4, PhysicsLaws.DefaultTolMva(uk));
        // The Python sidecars carry the same per-unit number.
        Assert.Contains("DEFAULT_TOLERANCE_PU = 1e-8", Tool("sidecar_common.py"));
    }

    [Fact]
    public void D125_plain_audit_default_on_a_100_MVA_case_is_bit_identical_to_the_old_1e6()
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = true });
        PhysicsLawReport byDefault = PhysicsLaws.Audit(m, r);
        PhysicsLawReport explicitOld = PhysicsLaws.Audit(m, r, 1e-6);
        Assert.All(byDefault.Laws, l => Assert.Equal(1e-6, l.Tolerance));
        for (int i = 0; i < byDefault.Laws.Count; i++)
        {
            Assert.Equal(explicitOld.Laws[i].Status, byDefault.Laws[i].Status);
            Assert.Equal(explicitOld.Laws[i].Residual, byDefault.Laws[i].Residual);
        }
    }

    [Fact]
    public void D040_converged_uk_solve_export_reports_gatePass_true()
    {
        // Repro: on the 10,000 MVA uk base law 6 reads 1.06e-6 MW and law 8 2.48e-6 MVA at convergence (1e-8 pu is
        // 1e-4 MW there), so the old absolute 1e-6 gate reported gatePass false for a correct solve.
        GridModel uk = Uk();
        var trace = new SolveTrace();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(uk, new PowerFlowOptions { EnforceQLimits = true, Trace = trace });
        Assert.True(r.Converged);
        Assert.Equal(LawStatus.Violated, Law(PhysicsLaws.Audit(uk, r, 1e-6), 6).Status);   // the defect, reproduced
        var meta = new SolveExportMeta("uk", "cpu", "test", "2026-10-05T00:00:00Z", 0, "test");
        JsonElement physics = JsonDocument.Parse(SolveExport.ToJson(uk, r, trace, meta)).RootElement.GetProperty("physics");
        Assert.True(physics.GetProperty("gatePass").GetBoolean());
        Assert.Equal(ExportPhysics.CaseDispatch, physics.GetProperty("basis").GetString());
        Assert.All(physics.GetProperty("laws").EnumerateArray(), l => Assert.Equal(1e-4, l.GetProperty("tolerance").GetDouble()));
    }

    // ---- D-DOC-040: the estimate exports score the estimate against its own state --------------------------------

    private static (GridModel M, PowerFlowResult Truth) Case14Truth()
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = true });
        Assert.True(truth.Converged);
        return (m, truth);
    }

    [Fact]
    public void D040_wls_estimate_export_is_scored_against_its_own_state_not_the_case_dispatch()
    {
        (GridModel m, PowerFlowResult truth) = Case14Truth();
        MeasurementSet set = SyntheticMeasurements.Sample(m, truth, new SyntheticMeasurementOptions { NoiseScale = 1.0 });
        EstimationResult est = new WlsStateEstimator().Estimate(m, set, new EstimationOptions());
        Assert.True(est.Converged);
        // The defect: the case-dispatch audit of this noisy estimate violates KCL by megawatts.
        Assert.Equal(LawStatus.Violated, Law(PhysicsLaws.Audit(m, est.ToPowerFlowResult(m)), 6).Status);

        ExportPhysics own = EstimateExport.PhysicsReport(m, est);
        Assert.Equal(ExportPhysics.EstimateState, own.Basis);
        Assert.Equal(LawStatus.NotApplicable, Law(own.Report, 6).Status);
        Assert.Equal(LawStatus.NotApplicable, Law(own.Report, 8).Status);
        Assert.Equal(LawStatus.Holds, Law(own.Report, 1).Status);   // identities of the projected state still scored
        Assert.Equal(LawStatus.Holds, Law(own.Report, 2).Status);
        Assert.Equal(LawStatus.Holds, Law(own.Report, 3).Status);

        var meta = new SolveExportMeta("case14", "cpu", "test", "2026-10-05T00:00:00Z", 0, "test");
        JsonElement physics = JsonDocument.Parse(EstimateExport.ToJson(m, set, est, meta)).RootElement.GetProperty("physics");
        Assert.True(physics.GetProperty("gatePass").GetBoolean());
        Assert.Equal(ExportPhysics.EstimateState, physics.GetProperty("basis").GetString());

        // The estimated-solve export carries the same audit when it is passed in (the CLI's --export-solve does).
        JsonElement solvePhysics = JsonDocument.Parse(SolveExport.ToJson(m, est.ToPowerFlowResult(m), new SolveTrace(), meta,
            physics: own)).RootElement.GetProperty("physics");
        Assert.True(solvePhysics.GetProperty("gatePass").GetBoolean());
        Assert.Equal(ExportPhysics.EstimateState, solvePhysics.GetProperty("basis").GetString());
    }

    [Fact]
    public void D040_feasible_estimate_export_writes_the_estimators_own_gate_report()
    {
        (GridModel m, PowerFlowResult truth) = Case14Truth();
        MeasurementSet sampled = SyntheticMeasurements.Sample(m, truth, new SyntheticMeasurementOptions { NoiseScale = 1.0 });
        var ms = sampled.Measurements.Where(x => !(x.Bus == 7 && x.Kind is MeasurementKind.PInjMw or MeasurementKind.QInjMvar)).ToList();
        ms.Add(new Measurement(MeasurementKind.PInjMw, 0.0, 1.0, Bus: 7, Source: "virtual-zero-injection"));
        ms.Add(new Measurement(MeasurementKind.QInjMvar, 0.0, 1.0, Bus: 7, Source: "virtual-zero-injection"));
        MeasurementSet set = sampled with { Measurements = ms };
        EstimationResult est = new WlsStateEstimator().Estimate(m, set, new EstimationOptions { Method = EstimationMethod.Feasible });
        Assert.True(est.Converged);
        Assert.NotNull(est.Physics);

        ExportPhysics own = EstimateExport.PhysicsReport(m, est);
        Assert.Same(est.Physics, own.Report);   // exactly the report the estimator decided on
        Assert.Equal(ExportPhysics.ZeroInjectionConstraints, own.Basis);
        Assert.Equal(LawStatus.Holds, Law(own.Report, 6).Status);
    }

    // ---- D-DOC-041 / D-DOC-190: reactive balance on the solved bus types; law 8 text --------------------------------

    [Fact]
    public void D041_pv_bus_pinned_at_a_reactive_limit_is_checked_against_that_limit()
    {
        // case118 with reactive limits enforced pins several PV buses (bus 19 among them at Qmin).
        GridModel m = IeeeCases.Case118();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = true });
        Assert.True(r.Converged);
        Assert.NotNull(r.SolvedBusTypes);
        int idx = r.BusIds.ToList().IndexOf(19);
        Assert.Equal(BusType.Pv, m.Buses[idx].Type);
        Assert.Equal(BusType.Pq, r.SolvedBusTypes![idx]);

        PhysicsLawReport clean = PhysicsLaws.Audit(m, r);
        Assert.Equal(LawStatus.Holds, Law(clean, 8).Status);
        Assert.Contains("held at a reactive limit", Law(clean, 8).Detail);

        // Move bus 19's Qmin by 5 MVAr in the audited model only: the solved state no longer sits on its limit, so law
        // 8 must now fail by those 5 MVAr. Under the old rule (declared types) bus 19 was PV and never checked.
        var gens = m.Generators.Select(g => g.Bus == 19 ? g with { Qmin = g.Qmin - 5.0 } : g).ToList();
        GridModel moved = m.CopyWith(generators: gens);
        PhysicsLawReport tampered = PhysicsLaws.Audit(moved, r);
        Assert.Equal(LawStatus.Violated, Law(tampered, 8).Status);
        Assert.InRange(Law(tampered, 8).Residual, 5.0 - 1e-6, 5.0 + 1e-6);
        Assert.Equal(LawStatus.Holds, Law(tampered, 6).Status);   // real power untouched
    }

    [Fact]
    public void D041_pv_bus_with_no_generator_is_checked_as_pq()
    {
        var m = new GridModel
        {
            Name = "doc7-pv-no-gen",
            BaseMva = 100,
            Buses = new[]
            {
                new Bus(1, BusType.Slack, Vm: 1.0),
                new Bus(2, BusType.Pv, Pd: 30, Qd: 10, Vm: 1.0),   // declared PV, no generator: solved as PQ (R283)
                new Bus(3, BusType.Pq, Pd: 20, Qd: 5),
            },
            Generators = new[] { new Generator(1, 0, Vg: 1.0) },
            Branches = new[] { new Branch(1, 2, 0.01, 0.05), new Branch(2, 3, 0.01, 0.05) },
        };
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = true });
        Assert.True(r.Converged);
        PhysicsLawReport clean = PhysicsLaws.Audit(m, r);
        Assert.Equal(LawStatus.Holds, Law(clean, 8).Status);
        Assert.Contains("1 with no generator", Law(clean, 8).Detail);

        // 3 MVAr more reactive load at bus 2 in the audited model only: now visible in law 8 (it was skipped before).
        GridModel moved = m.CopyWith(buses: new[] { m.Buses[0], m.Buses[1] with { Qd = 13 }, m.Buses[2] });
        LawResult law8 = Law(PhysicsLaws.Audit(moved, r), 8);
        Assert.Equal(LawStatus.Violated, law8.Status);
        Assert.InRange(law8.Residual, 3.0 - 1e-6, 3.0 + 1e-6);
    }

    [Fact]
    public void D190_law_8_detail_names_law_6_only()
    {
        (GridModel m, PowerFlowResult r) = Case14Truth();
        string detail = Law(PhysicsLaws.Audit(m, r), 8).Detail;
        Assert.Contains("same residual as law 6", detail);
        Assert.DoesNotContain("laws 1 and 6", detail);
    }

    // ---- D-DOC-188: law 9 says it was not scored -------------------------------------------------------------------

    [Fact]
    public void D188_law_9_without_a_sample_reads_not_scored()
    {
        (GridModel m, PowerFlowResult r) = Case14Truth();
        LawResult law9 = Law(PhysicsLaws.Audit(m, r), 9);
        Assert.Equal(LawStatus.NotApplicable, law9.Status);
        Assert.True(double.IsNaN(law9.Residual));
        Assert.StartsWith("NOT SCORED:", law9.Detail, StringComparison.Ordinal);
        // With a sample it is still scored exactly as before.
        Assert.Equal(LawStatus.Holds, Law(PhysicsLaws.Audit(m, r, synchronism: new SynchronismSample(40, 0.2)), 9).Status);
    }

    // ---- D-DOC-039: the conformance allowance list ----------------------------------------------------------------

    [Fact]
    public void D039_allowance_list_is_explicit_reasoned_and_names_shipped_files()
    {
        Assert.Equal(3, ConformanceAllowances.KnownUnconverged.Count);
        string[] all = Directory.EnumerateFiles(Path.Combine(Root(), "data", "matpower"), "*.m", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension).ToArray()!;
        foreach (ConformanceAllowances.Allowance a in ConformanceAllowances.KnownUnconverged)
        {
            Assert.Contains(a.CaseStem, all);
            Assert.Equal("MaxIterations", a.ExpectedStatus);
            Assert.Equal(PowerFlowStatus.MaxIterations.ToString(), a.ExpectedStatus);
            Assert.False(string.IsNullOrWhiteSpace(a.Reason));
            Assert.All(a.Reason, c => Assert.True(c < 128, $"non-ASCII in the reason for {a.CaseStem}"));
        }
    }

    [Fact]
    public void D039_allowance_matches_only_the_named_case_and_the_expected_status()
    {
        Assert.NotNull(ConformanceAllowances.For("pglib_opf_case3_lmbd", "MaxIterations"));
        Assert.Null(ConformanceAllowances.For("pglib_opf_case3_lmbd", "NonFinite"));        // a different failure
        Assert.Null(ConformanceAllowances.For("PGLIB_OPF_CASE3_LMBD", "MaxIterations"));    // exact, ordinal
        Assert.Null(ConformanceAllowances.For("case118", "MaxIterations"));                 // not on the list
        Assert.Null(ConformanceAllowances.For("pglib_opf_case588_sdet", "MaxIterations"));  // converges at 100 iterations
        Assert.True(ConformanceAllowances.IsListed("pglib_opf_case240_pserc"));
        Assert.False(ConformanceAllowances.IsListed("case9"));
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]   // shipped corpus: only allowed failures
    [InlineData(1, 0, 0, 1)]   // a converged case fails the gate
    [InlineData(0, 1, 0, 3)]   // a new unconverged case
    [InlineData(0, 0, 1, 3)]   // a case that cannot be loaded or run
    [InlineData(2, 1, 0, 3)]   // 3 wins over 1
    public void D039_exit_rule(int gateFailures, int unallowed, int skipped, int expected)
        => Assert.Equal(expected, ConformanceAllowances.ExitCode(gateFailures, unallowed, skipped));

    // ---- N219: Python discovery --------------------------------------------------------------------------------------

    private static Func<string, string?> Env(params (string Key, string? Value)[] vars)
    {
        var d = vars.ToDictionary(v => v.Key, v => v.Value);
        return k => d.TryGetValue(k, out string? v) ? v : null;
    }

    [Fact]
    public void N219_override_wins_and_fails_loud_when_it_names_nothing()
    {
        string forced = Path.Combine("C:", "tools", "py", "python.exe");
        var files = new HashSet<string> { forced };
        IReadOnlyList<PythonLocator.Candidate> c = PythonLocator.Candidates(
            Env(("GRIDSIM_PYTHON", forced), ("PATH", "X"), ("VIRTUAL_ENV", "V")), true, files.Contains, out string? err);
        Assert.Null(err);
        Assert.Single(c);
        Assert.Equal(forced, c[0].Command);

        c = PythonLocator.Candidates(Env(("GRIDSIM_PYTHON", Path.Combine("C:", "missing", "python.exe"))), true, _ => false, out err);
        Assert.Empty(c);
        Assert.Contains("does not exist", err);

        c = PythonLocator.Candidates(Env(("GRIDSIM_PYTHON", "python3.12"), ("PATH", "A")), true, _ => false, out err);
        Assert.Empty(c);
        Assert.Contains("not on PATH", err);
    }

    [Fact]
    public void N219_active_venv_comes_before_path_and_path_order_is_kept()
    {
        string venv = Path.Combine("C:", "work", "venv");
        string venvPy = Path.Combine(venv, "Scripts", "python.exe");
        string dirA = Path.Combine("C:", "A"), dirB = Path.Combine("C:", "B");
        string pyA = Path.Combine(dirA, "python.exe"), pyB = Path.Combine(dirB, "python.exe");
        var files = new HashSet<string> { venvPy, pyA, pyB };
        IReadOnlyList<PythonLocator.Candidate> c = PythonLocator.Candidates(
            Env(("VIRTUAL_ENV", venv), ("PATH", dirB + ";" + dirA)), true, files.Contains, out string? err);
        Assert.Null(err);
        Assert.Equal(new[] { venvPy, pyB, pyA, "py", "python", "python3" }, c.Select(x => x.Command).ToArray());
        Assert.Equal("VIRTUAL_ENV", c[0].Source);

        // Without a venv the first PATH folder that holds an interpreter wins.
        c = PythonLocator.Candidates(Env(("PATH", dirB + ";" + dirA)), true, files.Contains, out err);
        Assert.Equal(pyB, c[0].Command);
    }

    [Fact]
    public void N219_unix_layout_uses_bin_python_and_no_launcher()
    {
        string venv = Path.Combine("home", "u", "venv");
        string venvPy = Path.Combine(venv, "bin", "python");
        string dir = Path.Combine("usr", "bin");
        var files = new HashSet<string> { venvPy, Path.Combine(dir, "python3") };
        IReadOnlyList<PythonLocator.Candidate> c = PythonLocator.Candidates(
            Env(("VIRTUAL_ENV", venv), ("PATH", dir)), false, files.Contains, out _);
        Assert.Equal(new[] { venvPy, Path.Combine(dir, "python3"), "python", "python3" }, c.Select(x => x.Command).ToArray());
    }

    // ---- D-DOC-127 / 126 / 043 / 044 / 045: sidecar contracts (no Python in the test run) ----------------------------

    [Theory]
    [InlineData("crossval_estimation.py")]
    [InlineData("se_residual_check.py")]
    [InlineData("crossval_harmonics.py")]
    [InlineData("physics_audit.py")]
    [InlineData("residual_check.py")]
    [InlineData("crossval.py")]
    [InlineData("crossval_impedance.py")]
    public void D127_missing_dependency_is_a_skip_through_the_shared_helper(string tool)
    {
        string py = Tool(tool);
        Assert.Contains("from sidecar_common import", py);
        Assert.True(py.Contains("require(", StringComparison.Ordinal) || py.Contains("missing(", StringComparison.Ordinal),
            $"{tool} must check its packages through sidecar_common.require / missing");
        string common = Tool("sidecar_common.py");
        Assert.Contains("EXIT_SKIPPED = 77", common);
        Assert.Contains("sys.exit(EXIT_SKIPPED)", common);
    }

    [Fact]
    public void D126_D044_D041_shared_readers_exist_and_are_used()
    {
        string common = Tool("sidecar_common.py");
        Assert.Contains("for key in (\"Case\", \"case\")", common);
        Assert.Contains("def load_at_voltage(bus, vm)", common);
        Assert.Contains("def solved_bus_types(doc)", common);
        Assert.Contains("def pinned_q_limits(doc)", common);
        foreach (string tool in new[] { "crossval.py", "residual_check.py", "physics_audit.py", "crossval_estimation.py" })
        {
            string py = Tool(tool);
            Assert.Contains("export_case(doc)", py);
            Assert.DoesNotContain("get(\"case\", \"?\")", py);
        }
        Assert.Contains("load_at_voltage(", Tool("residual_check.py"));
        Assert.Contains("solved_bus_types(doc)", Tool("residual_check.py"));
        Assert.Contains("apply_zip_loads(net, model)", Tool("crossval.py"));
        Assert.Contains("const_z_p_percent", Tool("crossval.py"));
    }

    [Fact]
    public void D043_physics_audit_reads_both_export_kinds()
    {
        string py = Tool("physics_audit.py");
        Assert.Contains("(doc.get(\"estimate\") or {}).get(\"buses\")", py);
        Assert.Contains("doc.get(\"result\")", py);
        Assert.DoesNotContain("m, res = doc[\"model\"], doc[\"result\"]", py);
    }

    [Fact]
    public void D045_impedance_sidecar_does_not_rebuild_a_real_network_grid_impedance()
    {
        string py = Tool("crossval_impedance.py");
        Assert.Contains("canonical = cp is None or bool(cp.get(\"canonical\", True))", py);
        Assert.Contains("and canonical", py);
    }

    // ---- D-DOC-042 / 128 / 125 / 039: the verbs, as a script sees them -----------------------------------------------

    [Fact]
    public void D042_physics_check_fails_a_non_converged_solve_with_exit_3()
    {
        string path = Path.Combine(Root(), "data", "matpower", "nesta", "pglib_opf_case3_lmbd.m");
        CliProcess.Result r = CliProcess.RunBuilt("physics-check", path);
        Assert.True(r.ExitCode == 3, r.ToString());
        Assert.Contains("converged=False", r.StdOut);
        Assert.Contains("GATE: FAIL  -  the power flow did not converge (status MaxIterations", r.StdOut);
        Assert.DoesNotContain("GATE: PASS", r.StdOut);
        Assert.DoesNotContain("LawResult {", r.StdOut);
    }

    [Fact]
    public void D125_physics_check_default_tolerance_is_relative_and_passes_a_clean_case()
    {
        CliProcess.Result r = CliProcess.RunBuilt("physics-check", "case14");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("tol 1.0E-006 MVA = BaseMva 100 x 1e-8", r.StdOut);
        Assert.Contains("GATE: PASS", r.StdOut);
    }

    [Fact]
    public void D128_physics_check_prints_the_worst_law_as_a_sentence()
    {
        // --tol 0: every non-zero residual violates, so the gate fails on a converged state (exit 1).
        CliProcess.Result r = CliProcess.RunBuilt("physics-check", "case14", "--tol", "0");
        Assert.True(r.ExitCode == 1, r.ToString());
        Assert.Contains("GATE: FAIL  -  physically-impossible state. Worst: law ", r.StdOut);
        Assert.Contains("above the tolerance", r.StdOut);
        Assert.DoesNotContain("LawResult {", r.StdOut);
    }

    [Fact]
    public void D039_shipped_corpus_passes_conformance_and_a_run_without_allowances_still_fails()
    {
        // Repro (1.95.195): every run of the shipped corpus exited 3 on the three PGLib cases, with 24/24 passing.
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-doc7-conf-" + Guid.NewGuid().ToString("N"));
        try
        {
            CliProcess.Result r = CliProcess.RunBuilt("conformance", "--out", Path.Combine(dir, "a"));
            Assert.True(r.ExitCode == 0, r.ToString());
            Assert.Contains("3 known unconverged (allowed); 0 unconverged and not allowed", r.StdOut);
            Assert.Contains("| pglib_opf_case3_lmbd | 3 | no | allowed |", r.StdOut);
            Assert.DoesNotContain("**FAIL**", r.StdOut);

            CliProcess.Result strict = CliProcess.RunBuilt("conformance", "--no-allowances", "--out", Path.Combine(dir, "b"));
            Assert.True(strict.ExitCode == 3, strict.ToString());
            Assert.Contains("3 case(s) did not converge without an allowance", strict.StdErr);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Theory]
    [InlineData("physics-check", "case14")]
    [InlineData("conformance", null)]
    public void D125_negative_tolerance_is_refused(string verb, string? caseArg)
    {
        string[] args = caseArg is null ? new[] { verb, "--tol", "-1" } : new[] { verb, caseArg, "--tol", "-1" };
        CliProcess.Result r = CliProcess.RunBuilt(args);
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("--tol expected a tolerance of 0 MVA or more", r.StdErr);
    }
}
