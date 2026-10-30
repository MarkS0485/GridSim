using System.Globalization;
using GridSim.Cli;
using GridSim.Core.Abstractions;
using GridSim.Core.IO;
using GridSim.Core.Analysis;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Core.Storage;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// DOC-11 (October 2026): the docs session's OPF, contingency and storage findings (review-progress/2026-10/
/// DOCS-FINDINGS-TRIAGE.md stream 11): D-DOC-046 (taps), 047 (ptdf unknown bus), 048 (--cpu before the verb), 049 (chain
/// --prices and the price policies), 050 (gda-replay policy and threshold flags), 065 (BESS spec validation), 118
/// (nminus1-sweep per-bus voltage limits), 129 (cpf bracketing), 137 (BESS output), 139 (one honest cost unit), 157 (blank
/// branch names), 189 (cpf labelled honestly) and 197 (the synthetic price day, the price policies and the price-file
/// reader). N240 (the DC-OPF tie-break) has its own class, N240DcOpfTieBreakTests.
/// </summary>
public sealed class Doc11OpfContingencyStorageTests
{
    private const string InternalError = "internal error";

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-doc11-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Delete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void AssertExit(int expected, CliProcess.Result r)
    {
        Assert.True(r.ExitCode == expected, $"expected exit {expected}\n{r}");
        Assert.DoesNotContain(InternalError, r.StdErr, StringComparison.Ordinal);
    }

    private static string Data(params string[] parts) => Path.Combine(new[] { AppContext.BaseDirectory, "data" }.Concat(parts).ToArray());

    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

    // =================================================================================================================
    // D-DOC-046: taps
    // =================================================================================================================

    private static GridModel TapModel(params Oltc[] oltcs) => new()
    {
        BaseMva = 100,
        Name = "tap-opt",
        Buses = new[]
        {
            new Bus(1, BusType.Slack, Vm: 1.0, BaseKv: 400),
            new Bus(2, BusType.Pq, Pd: 85, Qd: 28, BaseKv: 132),
            new Bus(3, BusType.Pq, Pd: 60, Qd: 22, BaseKv: 132),
        },
        Generators = new[] { new Generator(1, 0, Vg: 1.0, Qmin: -500, Qmax: 500) },
        Branches = new[]
        {
            new Branch(1, 2, 0.004, 0.060, 0.0, TapRatio: 1.0, Name: "T1", IsTransformer: true),
            new Branch(1, 3, 0.004, 0.085, 0.0, TapRatio: 1.0, Name: "T2", IsTransformer: true),
        },
        Oltcs = oltcs.Length > 0 ? oltcs : new[]
        {
            new Oltc("T1", ControlsBus: 2, TargetVpu: 1.0, MinTap: 0.9, MaxTap: 1.1, StepPu: 0.0125),
            new Oltc("T2", ControlsBus: 3, TargetVpu: 1.0, MinTap: 0.9, MaxTap: 1.1, StepPu: 0.0125),
        },
    };

    [Fact]
    public void D046_an_oltc_that_names_no_branch_or_an_unknown_bus_is_refused_naming_it()
    {
        GridModel m = TapModel(
            new Oltc("T1", ControlsBus: 2),
            new Oltc("T9", ControlsBus: 3),
            new Oltc("T2", ControlsBus: 77));
        IReadOnlyList<string> problems = TapOptimizer.OltcProblems(m);
        Assert.Equal(new[]
        {
            "OLTC 'T9' names no branch of the case",
            "OLTC 'T2' controls bus 77, which the case does not have",
        }, problems);
        var ex = Assert.Throws<ArgumentException>(() => TapOptimizer.Flatten(m));
        Assert.Contains("OLTC 'T9' names no branch of the case", ex.Message, StringComparison.Ordinal);

        // A name two branches share is ambiguous.
        GridModel twice = TapModel().CopyWith(branches: new[]
        {
            new Branch(1, 2, 0.004, 0.060, 0.0, TapRatio: 1.0, Name: "T1", IsTransformer: true),
            new Branch(1, 3, 0.004, 0.085, 0.0, TapRatio: 1.0, Name: "T1", IsTransformer: true),
        });
        Assert.Equal(new[]
        {
            "OLTC 'T1' names 2 branches (a tap changer must name exactly one)",
            "OLTC 'T2' names no branch of the case",
        }, TapOptimizer.OltcProblems(twice));
        Assert.Empty(TapOptimizer.OltcProblems(TapModel()));
    }

    [Fact]
    public void D046_the_schedule_reports_the_search_convergence_and_each_oltcs_own_regulation()
    {
        GridModel m = TapModel();
        TapSchedule s = TapOptimizer.Flatten(m);
        Assert.True(s.Solved);
        Assert.True(s.OptimizerConverged);
        Assert.True(s.OptimizerIterations > 0);
        Assert.Equal(2, s.Regulation.Count);
        // The check reads the controlled bus's voltage at the recommended taps, from a fresh solve of that schedule.
        var branches = m.Branches.ToArray();
        for (int i = 0; i < branches.Length; i++)
            branches[i] = branches[i] with { TapRatio = s.Taps.Single(t => t.Transformer == branches[i].Name).RecommendedTap };
        GridModel tuned = m.CopyWith(branches: branches);
        PowerFlowResult pf = SolverSelector.CreateNewton(tuned).Solve(tuned, new PowerFlowOptions());
        foreach (OltcRegulationCheck c in s.Regulation)
        {
            Oltc o = m.Oltcs.Single(x => x.Transformer == c.Transformer);
            Assert.Equal(o.ControlsBus, c.ControlsBus);
            Assert.Equal(o.TargetVpu, c.TargetVpu);
            Assert.Equal(o.DeadbandPu, c.DeadbandPu);
            Assert.Equal(Bits(pf.Vm[pf.BusIds.ToList().IndexOf(c.ControlsBus)]), Bits(c.VmPu));
            Assert.Equal(Math.Abs(c.VmPu - c.TargetVpu) <= c.DeadbandPu, c.WithinDeadband);
        }

        // A search cut off at a tiny iteration cap says so.
        TapSchedule capped = TapOptimizer.Flatten(m, optimizer: new NelderMead.Options { InitialStep = TapOptimizer.InitialTapStepPu, MaxIterations = 2 });
        Assert.False(capped.OptimizerConverged);
        Assert.Equal(2, capped.OptimizerIterations);
    }

    [Fact]
    public void D046_taps_refuses_a_case_whose_oltc_names_no_branch_exit_1()
    {
        string dir = TempDir();
        try
        {
            string caseDir = Path.Combine(dir, "uk-bad-oltc");
            Directory.CreateDirectory(caseDir);
            foreach (string f in Directory.GetFiles(Data("uk"))) File.Copy(f, Path.Combine(caseDir, Path.GetFileName(f)));
            string oltc = Path.Combine(caseDir, "onloadtapchanger.json");
            File.WriteAllText(oltc, File.ReadAllText(oltc).Replace("\"T1 GSP N Scotland\"", "\"T1 GSP Nowhere\"", StringComparison.Ordinal));
            CliProcess.Result r = CliProcess.RunBuilt("taps", caseDir);
            AssertExit(1, r);
            Assert.Contains("OLTC 'T1 GSP Nowhere' names no branch of the case", r.StdErr, StringComparison.Ordinal);
            Assert.DoesNotContain("TAP SCHEDULE", r.StdOut, StringComparison.Ordinal);
        }
        finally { Delete(dir); }
    }

    // =================================================================================================================
    // D-DOC-047: ptdf unknown bus
    // =================================================================================================================

    [Fact]
    public void D047_ptdf_with_a_bus_the_case_does_not_have_exits_2()
    {
        CliProcess.Result r = CliProcess.RunBuilt("ptdf", "case9", "--bus", "99");
        AssertExit(2, r);
        Assert.Contains("error: ptdf --bus 99: case 'IEEE 9-bus (WSCC)' has no bus 99.", r.StdErr, StringComparison.Ordinal);
        Assert.Equal("", r.StdOut);
    }

    // =================================================================================================================
    // D-DOC-048: --cpu before the verb
    // =================================================================================================================

    [Fact]
    public void D048_cpu_before_the_verb_no_longer_trips_the_verb_flag_checks()
    {
        CliProcess.Result plain = CliProcess.RunBuilt("dcopf", "case9", "--prices");
        CliProcess.Result first = CliProcess.RunBuilt("--cpu", "dcopf", "case9", "--prices");
        AssertExit(0, plain);
        AssertExit(0, first);
        Assert.Equal(plain.StdOut, first.StdOut);
        // The same for a contingency-verb flag.
        CliProcess.Result n1 = CliProcess.RunBuilt("--cpu", "nminus1", "case9", "--global-vlimits");
        Assert.DoesNotContain("applies to", n1.StdErr, StringComparison.Ordinal);
        AssertExit(3, n1);
    }

    // =================================================================================================================
    // D-DOC-049: chain --prices, the price and lp policies
    // =================================================================================================================

    [Fact]
    public void D049_chain_takes_prices_and_runs_the_lp_policy()
    {
        string dir = TempDir();
        try
        {
            string md = Path.Combine(dir, "c.md");
            CliProcess.Result r = CliProcess.RunBuilt("chain", "case9", "--steps", "4", "--bess", "lfp:5:10:20",
                "--prices", "synthetic", "--policy", "lp", "--out", md);
            AssertExit(0, r);
            Assert.Contains("policy=lp", r.StdOut, StringComparison.Ordinal);
            Assert.Contains("prices: " + SyntheticPrices.ProvenanceNote, r.StdOut, StringComparison.Ordinal);
            Assert.Contains("revenue", r.StdOut, StringComparison.Ordinal);

            // --prices alone selects lp by default.
            CliProcess.Result byDefault = CliProcess.RunBuilt("chain", "case9", "--steps", "4", "--bess", "lfp:5:10:20",
                "--prices", "synthetic", "--out", Path.Combine(dir, "d.md"));
            AssertExit(0, byDefault);
            Assert.Contains("policy=lp", byDefault.StdOut, StringComparison.Ordinal);

            // The price policy acts on the prices: with the synthetic day over 48 half-hours some step charges and some
            // discharges.
            CliProcess.Result price = CliProcess.RunBuilt("chain", "case9", "--steps", "48", "--bess", "lfp:5:10:20",
                "--prices", "synthetic", "--policy", "price", "--out", Path.Combine(dir, "p.md"));
            AssertExit(0, price);
            string[] csv = File.ReadAllLines(Path.Combine(dir, "p.csv"));
            string[] head = csv[0].Split(',');
            int req = Array.IndexOf(head, "s0_request_mw");
            var requests = csv.Skip(1).Select(l => double.Parse(l.Split(',')[req], CultureInfo.InvariantCulture)).ToList();
            Assert.Contains(requests, v => v > 0);
            Assert.Contains(requests, v => v < 0);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public void D049_price_policy_without_prices_and_storage_flags_without_bess_are_refused()
    {
        string dir = TempDir();
        try
        {
            CliProcess.Result price = CliProcess.RunBuilt("chain", "case9", "--steps", "2", "--bess", "lfp:5:10:20", "--policy", "price",
                "--out", Path.Combine(dir, "c.md"));
            AssertExit(2, price);
            Assert.Contains("error: chain storage: --policy price needs --prices", price.StdErr, StringComparison.Ordinal);

            CliProcess.Result plain = CliProcess.RunBuilt("chain", "case9", "--steps", "2", "--policy", "idle", "--out", Path.Combine(dir, "d.md"));
            AssertExit(2, plain);
            Assert.Contains("error: chain --policy is a storage flag: it needs at least one --bess site", plain.StdErr, StringComparison.Ordinal);

            // A bare --prices (no value: last on the line).
            CliProcess.Result bare = CliProcess.RunBuilt("chain", "case9", "--steps", "2", "--bess", "lfp:5:10:20", "--policy", "lp",
                "--out", Path.Combine(dir, "e.md"), "--prices");
            AssertExit(2, bare);
            Assert.Contains("--prices expects a value", bare.StdErr, StringComparison.Ordinal);

            CliProcess.Result elsewhere = CliProcess.RunBuilt("pf", "case9", "--prices");
            AssertExit(2, elsewhere);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public void D049_a_bad_price_file_is_an_input_error_naming_the_file_and_line()
    {
        string dir = TempDir();
        try
        {
            string bad = Path.Combine(dir, "bad.csv");
            File.WriteAllLines(bad, new[] { "# GBP/MWh", "t0,50", "t1,abc", "t2,60" });
            CliProcess.Result r = CliProcess.RunBuilt("chain", "case9", "--steps", "3", "--bess", "lfp:5:10:20", "--prices", bad,
                "--out", Path.Combine(dir, "c.md"));
            AssertExit(1, r);
            Assert.Contains("line 3: 'abc' is not a price", r.StdErr, StringComparison.Ordinal);

            CliProcess.Result missing = CliProcess.RunBuilt("chain", "case9", "--steps", "3", "--bess", "lfp:5:10:20", "--prices",
                Path.Combine(dir, "nope.csv"), "--out", Path.Combine(dir, "d.md"));
            AssertExit(1, missing);
            Assert.Contains("cannot read the price file", missing.StdErr, StringComparison.Ordinal);
        }
        finally { Delete(dir); }
    }

    // =================================================================================================================
    // D-DOC-050: gda-replay policy and threshold flags
    // =================================================================================================================

    [Fact]
    public void D050_gda_replay_refuses_the_price_policies_and_reads_the_shave_thresholds()
    {
        string dir = TempDir();
        try
        {
            CliProcess.Result price = CliProcess.RunBuilt("gda-replay", "--synth", "--steps", "2", "--case", "case9",
                "--bess", "lfp:5:10:20", "--policy", "price", "--out", Path.Combine(dir, "a"));
            AssertExit(2, price);
            Assert.Contains("--policy price needs a price series, and gda-replay takes none", price.StdErr, StringComparison.Ordinal);

            CliProcess.Result shave = CliProcess.RunBuilt("gda-replay", "--synth", "--steps", "2", "--case", "case9",
                "--bess", "lfp:5:10:20", "--shave-high", "100", "--shave-low", "50", "--out", Path.Combine(dir, "b"));
            Assert.DoesNotContain("does not take", shave.StdErr, StringComparison.Ordinal);
            Assert.True(shave.ExitCode is 0 or 3, shave.ToString());
            Assert.Contains("COUNTERFACTUAL", shave.StdOut, StringComparison.Ordinal);
        }
        finally { Delete(dir); }
    }

    // =================================================================================================================
    // D-DOC-065: BESS spec validation
    // =================================================================================================================

    [Theory]
    [InlineData("lfp:5:10:20:0.5:9", "expected chemistry:bus:MW:MWh[:soc] (4 or 5 fields separated by ':'), got 6")]
    [InlineData("lfp:5:10", "got 3")]
    [InlineData("lfp:5:10:20:1.5", "starting state of charge '1.5' must be a number from 0 to 1")]
    [InlineData("lfp:5:10:20:-0.1", "starting state of charge '-0.1' must be a number from 0 to 1")]
    [InlineData("lfp:5:0:20", "rated power (MW) '0' must be a number above 0")]
    [InlineData("lfp:5:-10:20", "rated power (MW) '-10' must be a number above 0")]
    [InlineData("lfp:5:10:0", "energy (MWh) '0' must be a number above 0")]
    [InlineData("lfp:5:10:NaN", "energy (MWh) 'NaN' must be a number above 0")]
    [InlineData("lfp:x:10:20", "bus 'x' is not a bus number")]
    [InlineData("unobtainium:5:10:20", "unknown chemistry 'unobtainium' (lfp, nmc, lto, na-ion, vrfb, lead-acid).")]
    public void D065_a_bad_bess_spec_is_refused_naming_the_field(string spec, string message)
    {
        var ex = Assert.Throws<ArgumentException>(() => StorageCli.ParseBessSpec(spec));
        Assert.Contains($"--bess '{spec}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("(Parameter", ex.Message, StringComparison.Ordinal);   // D-DOC-137: no C# parameter suffix
    }

    [Fact]
    public void D065_a_good_spec_parses_with_the_documented_defaults()
    {
        (ChemistryParameters chem, int bus, double mw, double mwh, double soc) = StorageCli.ParseBessSpec("lfp:5:10:20");
        Assert.Equal(BatteryChemistryLibrary.ByName("lfp"), chem);
        Assert.Equal((5, 10.0, 20.0, StorageCli.DefaultInitialSoc), (bus, mw, mwh, soc));
        Assert.Equal(1.0, StorageCli.ParseBessSpec("nmc:7:1.5:3:1").Soc);
        Assert.Equal(0.0, StorageCli.ParseBessSpec("nmc:7:1.5:3:0").Soc);
    }

    [Fact]
    public void D065_chain_refuses_a_starting_soc_above_1_exit_2()
    {
        string dir = TempDir();
        try
        {
            CliProcess.Result r = CliProcess.RunBuilt("chain", "case9", "--steps", "2", "--bess", "lfp:5:10:20:1.5", "--out", Path.Combine(dir, "c.md"));
            AssertExit(2, r);
            Assert.Contains("error: chain storage: --bess 'lfp:5:10:20:1.5': starting state of charge '1.5' must be a number from 0 to 1.", r.StdErr, StringComparison.Ordinal);
        }
        finally { Delete(dir); }
    }

    // =================================================================================================================
    // D-DOC-118: nminus1-sweep judges per-bus limits, as nminus1 does
    // =================================================================================================================

    /// <summary>nminus1's footer counts: "N outages screened  S secure  V with violations  I islanded".</summary>
    private static (int Screened, int Secure, int Violations) Nminus1Counts(string stdout)
    {
        string line = stdout.Split('\n').Single(l => l.Contains("outages screened", StringComparison.Ordinal));
        string[] w = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (int.Parse(w[0], CultureInfo.InvariantCulture), int.Parse(w[3], CultureInfo.InvariantCulture), int.Parse(w[5], CultureInfo.InvariantCulture));
    }

    private static (int Screened, int Secure, int Violations) SweepCounts(string csvPath)
    {
        string[] row = File.ReadAllLines(csvPath).Last().Split(',');
        return (int.Parse(row[3], CultureInfo.InvariantCulture), int.Parse(row[4], CultureInfo.InvariantCulture), int.Parse(row[5], CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("lib", "case9.m")]
    [InlineData("lib", "case30.m")]
    [InlineData("benchmarks", "cigre_mv.m")]
    public void D118_the_sweep_and_nminus1_agree_with_per_bus_limits_and_with_the_band(string folder, string file)
    {
        string m = Data("matpower", folder, file);
        string dir = TempDir();
        try
        {
            foreach (string[] extra in new[] { Array.Empty<string>(), new[] { "--global-vlimits" } })
            {
                CliProcess.Result n1 = CliProcess.RunBuilt(new[] { "nminus1", m }.Concat(extra).ToArray());
                string md = Path.Combine(dir, $"s{extra.Length}.md");
                CliProcess.Result sw = CliProcess.RunBuilt(new[] { "nminus1-sweep", m, "--parallel", "1", "--out", md }.Concat(extra).ToArray());
                Assert.True(sw.ExitCode == n1.ExitCode, $"{file} {string.Join(' ', extra)}\n{n1}\n{sw}");
                Assert.Equal(Nminus1Counts(n1.StdOut), SweepCounts(Path.ChangeExtension(md, ".csv")));
                Assert.Contains(extra.Length == 0 ? "vlimits=per-bus" : "vlimits=global", sw.StdOut, StringComparison.Ordinal);
            }
        }
        finally { Delete(dir); }
    }

    // =================================================================================================================
    // D-DOC-129 / D-DOC-189: cpf
    // =================================================================================================================

    [Fact]
    public void D129_the_walk_end_does_not_change_the_lambda_and_says_whether_it_bracketed()
    {
        foreach (GridModel m in new[] { IeeeCases.Case9(), IeeeCases.Case14() })
        {
            IPowerFlowSolver solver = ContingencyAnalysis.WithOltcLoop(SolverSelector.CreateNewton(m));
            double plain = ContinuationPowerFlow.MaxLoadability(m, solver, ContingencyAnalysis.StudyOptions);
            (double lambda, LoadabilityWalkEnd end) = ContinuationPowerFlow.MaxLoadabilityWithEnd(m, solver, ContingencyAnalysis.StudyOptions);
            Assert.Equal(Bits(plain), Bits(lambda));
            Assert.True(end.Bracketed);
            Assert.True(end.LowestFailedLambda > lambda);
            Assert.True(end.FinalStep < 1e-3);
            PvCurve curve = ContinuationPowerFlow.TracePvCurve(m, solver, ContingencyAnalysis.StudyOptions);
            Assert.Equal(end, curve.WalkEnd);

            (double capped, LoadabilityWalkEnd cEnd) = ContinuationPowerFlow.MaxLoadabilityWithEnd(m, solver, ContingencyAnalysis.StudyOptions, maxSteps: 3);
            Assert.False(cEnd.Bracketed);
            Assert.Equal(3, cEnd.Solves);
            Assert.Equal(Bits(ContinuationPowerFlow.MaxLoadability(m, solver, ContingencyAnalysis.StudyOptions, maxSteps: 3)), Bits(capped));
        }
    }

    [Fact]
    public void D129_cpf_says_not_bracketed_and_exits_3_when_max_steps_runs_out()
    {
        CliProcess.Result r = CliProcess.RunBuilt("cpf", "case9", "--max-steps", "3");
        AssertExit(3, r);
        Assert.Contains("NOT bracketed: --max-steps 3 ran out", r.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("nose bracketed", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("is a lower bound, not the loadability limit", r.StdErr, StringComparison.Ordinal);

        CliProcess.Result ok = CliProcess.RunBuilt("cpf", "case9");
        AssertExit(0, ok);
        Assert.Contains("nose bracketed to 0.001", ok.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void D189_cpf_is_labelled_as_stepping_and_bisection_not_continuation()
    {
        string ch19 = File.ReadAllText(Path.Combine(CliProcess.Root(), "docs", "manual", "0019 CLI reference.md"));
        Assert.Contains("Maximum loadability by stepping and bisection (`ContinuationPowerFlow.MaxLoadability`). Despite the verb's name it is\nnot a predictor-corrector continuation power flow",
            ch19.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("Continuation power flow (`ContinuationPowerFlow.MaxLoadability`)", ch19, StringComparison.Ordinal);
        string core = File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Core", "Analysis", "ContinuationPowerFlow.cs"));
        Assert.Contains("this is NOT a predictor-corrector continuation power flow", core, StringComparison.Ordinal);
    }

    // =================================================================================================================
    // D-DOC-137: storage output
    // =================================================================================================================

    [Fact]
    public void D137_signed_one_decimal_never_prints_minus_plus_and_matches_the_old_format_elsewhere()
    {
        Assert.Equal("+0.0", StorageCli.SignedOneDecimal(-0.01));
        Assert.Equal("+0.0", StorageCli.SignedOneDecimal(-0.0));
        Assert.Equal("+0.0", StorageCli.SignedOneDecimal(0.0));
        Assert.Equal("+0.0", StorageCli.SignedOneDecimal(0.04));
        // Before: the section format gave "-+0.0" for a small negative value.
        Assert.Equal("-+0.0", (-0.01).ToString("+0.0;-0.0", CultureInfo.InvariantCulture));
        // Every value that does not round to zero prints exactly as the old format printed it.
        foreach (double v in new[] { 0.05, -0.05, 0.06, -0.06, 1.25, -1.25, 12.34, -12.36, 1e6, -987.65 })
            Assert.Equal(v.ToString("+0.0;-0.0", CultureInfo.InvariantCulture), StorageCli.SignedOneDecimal(v));
    }

    [Fact]
    public void D137_a_step_that_does_not_converge_prints_no_slack_or_voltage()
    {
        string dir = TempDir();
        try
        {
            string md = Path.Combine(dir, "c.md");
            // A 400 % per-step ramp: case9 stops converging within a few steps.
            CliProcess.Result r = CliProcess.RunBuilt("chain", "case9", "--mode", "ramp", "--ramp-pct", "400", "--steps", "4",
                "--bess", "lfp:5:10:20", "--policy", "idle", "--out", md);
            AssertExit(3, r);
            string[] rows = r.StdOut.Split('\n').Where(l => l.TrimEnd().EndsWith("| N |", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(rows);
            foreach (string row in rows) Assert.EndsWith("| - | - | N |", row.TrimEnd(), StringComparison.Ordinal);
            string[] csv = File.ReadAllLines(Path.ChangeExtension(md, ".csv"));
            string[] head = csv[0].Split(',');
            foreach (string line in csv.Skip(1).Where(l => l.EndsWith(",0", StringComparison.Ordinal)))
            {
                string[] f = line.Split(',');
                foreach (string col in new[] { "slack_mw", "vmin", "vmax", "loss_mw" }) Assert.Equal("", f[Array.IndexOf(head, col)]);
            }
        }
        finally { Delete(dir); }
    }

    // =================================================================================================================
    // D-DOC-139: one honest cost unit
    // =================================================================================================================

    [Fact]
    public void D139_result_file_keys_name_gbp_only_on_the_fuel_type_merit_order()
    {
        string dir = TempDir();
        try
        {
            string fuel = Path.Combine(dir, "fuel.json"), curves = Path.Combine(dir, "curves.json");
            AssertExit(0, CliProcess.RunBuilt("dcopf", "case9", "--json", fuel));
            AssertExit(0, CliProcess.RunBuilt("dcopf", "case9", "--costs", "case", "--json", curves));
            string f = File.ReadAllText(fuel), c = File.ReadAllText(curves);
            Assert.Contains("\"total_cost_gbp_per_h\"", f, StringComparison.Ordinal);
            Assert.Contains("\"cost_gbp_per_mwh\"", f, StringComparison.Ordinal);
            Assert.DoesNotContain("gbp", c, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"total_cost_per_h\"", c, StringComparison.Ordinal);
            Assert.Contains("\"marginal_cost_per_mwh\"", c, StringComparison.Ordinal);
            Assert.Contains("\"cost_units\"", c, StringComparison.Ordinal);

            string ac = Path.Combine(dir, "ac.json");
            CliProcess.Result acr = CliProcess.RunBuilt("acopf", "case9", "--costs", "case", "--json", ac);
            AssertExit(0, acr);
            Assert.DoesNotContain("gbp", File.ReadAllText(ac), StringComparison.OrdinalIgnoreCase);
        }
        finally { Delete(dir); }
    }

    // =================================================================================================================
    // D-DOC-157: blank branch names
    // =================================================================================================================

    [Fact]
    public void D157_an_unnamed_overloaded_branch_is_named_by_its_end_buses()
    {
        GridModel cigre = GridSim.Core.IO.Matpower.MatpowerUnitNormalizer.Normalize(
            GridSim.Core.IO.Matpower.MatpowerParser.ParseFile(Data("matpower", "benchmarks", "cigre_mv.m")));
        ContingencyResult b = ContingencyAnalysis.ScreenBaseCase(cigre, ContingencyAnalysis.CreateStudySolver(cigre), ContingencyAnalysis.StudyOptions);
        string overload = b.Violations.Single(v => v.StartsWith("branch ", StringComparison.Ordinal));
        Assert.DoesNotContain("branch  ", overload, StringComparison.Ordinal);
        Assert.Matches(@"^branch \d+-\d+ \d+%$", overload);
    }

    // =================================================================================================================
    // D-DOC-197: the synthetic price day, the price policies, the price-file reader
    // =================================================================================================================

    [Fact]
    public void D197_the_synthetic_price_day_has_its_documented_shape()
    {
        double[] day = StorageCli.SyntheticPriceSeries(48, 0.5);
        for (int k = 0; k < 48; k++)
        {
            Assert.Equal(Bits(SyntheticPrices.GbDay(k * 0.5)), Bits(day[k]));
            // One day repeats: hour h, h + 24 and h - 24 give the same bits.
            Assert.Equal(Bits(day[k]), Bits(SyntheticPrices.GbDay(k * 0.5 + 24.0)));
            Assert.Equal(Bits(day[k]), Bits(SyntheticPrices.GbDay(k * 0.5 - 24.0)));
            Assert.True(double.IsFinite(day[k]) && day[k] > 0, $"hour {k * 0.5}: {day[k]}");
        }
        // Evening peak at 18:00, midday solar dip at 13:00, overnight base about 45.
        int peak = Array.IndexOf(day, day.Max()), trough = Array.IndexOf(day, day.Min());
        Assert.Equal(36, peak);     // 18:00
        Assert.Equal(26, trough);   // 13:00
        Assert.InRange(day[36], 129.0, 131.0);
        Assert.InRange(day[26], 25.0, 26.0);
        Assert.InRange(day[6], 44.9, 45.2);   // 03:00
        Assert.Contains("Synthetic", SyntheticPrices.ProvenanceNote, StringComparison.Ordinal);
        Assert.Contains("not a market record", SyntheticPrices.ProvenanceNote, StringComparison.Ordinal);
    }

    private static DispatchContext Ctx(double price = double.NaN, double soc = 0.5) =>
        new(Step: 0, Hour: 0, StepHours: 0.5, SystemDemandMw: 100, PriceGbpPerMwh: price, Soc: soc, RatedMw: 10);

    [Fact]
    public void D197_the_price_threshold_policy_charges_below_discharges_above_and_holds_between()
    {
        var p = new PriceThresholdPolicy(BuyBelow: 40, SellAbove: 90);
        Assert.Equal(0.0, p.Request(Ctx()));            // no price: never acts
        Assert.Equal(-10.0, p.Request(Ctx(40)));        // at or below the buy threshold: charge at full rating
        Assert.Equal(-10.0, p.Request(Ctx(-5)));        // a negative price: charge
        Assert.Equal(10.0, p.Request(Ctx(90)));         // at or above the sell threshold: discharge at full rating
        Assert.Equal(10.0, p.Request(Ctx(300)));
        Assert.Equal(0.0, p.Request(Ctx(40.0000001)));  // between: hold
        Assert.Equal(0.0, p.Request(Ctx(89.9999999)));
        Assert.Equal("price-threshold", p.Name);
    }

    [Fact]
    public void D197_the_response_holding_policy_restores_the_soc_band_at_a_fifth_of_rating()
    {
        var p = new ResponseHoldingPolicy();
        Assert.Equal((0.5, 0.05, 0.2), (p.TargetSoc, p.Band, p.RestoreFraction));
        Assert.Equal(2.0, p.Request(Ctx(soc: 0.6)));      // above the band: discharge 0.2 x 10 MW
        Assert.Equal(-2.0, p.Request(Ctx(soc: 0.4)));     // below: charge
        Assert.Equal(0.0, p.Request(Ctx(soc: 0.52)));     // inside: hold
        Assert.Equal("response-holding", p.Name);
    }

    [Fact]
    public void D197_the_price_file_reader_takes_values_csv_last_fields_and_comments_and_refuses_bad_files()
    {
        string dir = TempDir();
        try
        {
            string good = Path.Combine(dir, "good.csv");
            File.WriteAllLines(good, new[] { "# day-ahead, GBP/MWh", "", "00:00,45.5", "  -3.25  ", "01:00, 1e2", "# end", "77" });
            Assert.Equal(new[] { 45.5, -3.25, 100.0 }, StorageCli.ReadPriceFile(good, 3, out int extra));
            Assert.Equal(1, extra);
            Assert.Equal(new[] { 45.5, -3.25, 100.0, 77.0 }, StorageCli.ReadPriceFile(good, 4, out extra));
            Assert.Equal(0, extra);

            var shortEx = Assert.Throws<PriceFileException>(() => StorageCli.ReadPriceFile(good, 5, out _));
            Assert.Contains("has 4 values; 5 steps need one each", shortEx.Message, StringComparison.Ordinal);

            foreach ((string bad, int line) in new[] { ("abc", 2), ("NaN", 2), ("Infinity", 2), ("1,5", 0), ("4,5;", 2) })
            {
                string path = Path.Combine(dir, "bad.csv");
                File.WriteAllLines(path, new[] { "10", bad });
                if (line == 0) { Assert.Equal(new[] { 10.0, 5.0 }, StorageCli.ReadPriceFile(path, 2, out _)); continue; }   // a CSV pair: its last field
                var ex = Assert.Throws<PriceFileException>(() => StorageCli.ReadPriceFile(path, 2, out _));
                Assert.Contains($"line {line}:", ex.Message, StringComparison.Ordinal);
                Assert.Contains(path, ex.Message, StringComparison.Ordinal);
            }

            var missing = Assert.Throws<PriceFileException>(() => StorageCli.ReadPriceFile(Path.Combine(dir, "nope.csv"), 1, out _));
            Assert.Contains("cannot read the price file", missing.Message, StringComparison.Ordinal);
        }
        finally { Delete(dir); }
    }
}
