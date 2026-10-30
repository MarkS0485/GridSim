using GridSim.Cli;
using GridSim.Core.Assets;
using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N314 (LF5): the F front-doors stream's leftovers. trip's system inertia is the N289 rotating-mass total (bit-identical
/// on every bundled case, a converter-typed unit with H no longer counted); AssetEnvelope copies its model through the
/// shared CopyWith; fit-frequency takes its nominal from the trace or --nominal-hz; classical stability takes --dyn-data
/// as modal does; MultiDeviceFrequencyIntegrator is internal and RmsConverterUnit.Store is marked internal-use.
/// </summary>
public sealed partial class Lf5RemainingTests
{
    // ---- trip: the N289 inertia total --------------------------------------------------------------------------------

    /// <summary>
    /// The MWs total is exactly the N289 sum (rotating mass only, fleet order) and the GVAs total is exactly it over 1000,
    /// the expression SystemInertiaGvaS always was. On every bundled case checked here no converter-typed unit carries
    /// H above 0, so the total also equals trip's old all-unit sum bit for bit: no trip number moves on them.
    /// </summary>
    [Fact]
    public void N314_system_inertia_mws_is_the_n289_sum_and_equals_the_old_trip_sum_on_bundled_cases()
    {
        foreach (GridModel m in new[]
        {
            IeeeCases.Case9(),
            JsonGridLoader.LoadDirectory(RepoPath("data", "uk")),
            JsonGridLoader.LoadDirectory(RepoPath("data", "gb-synthetic")),
        })
        {
            double n289 = m.Generators.Where(GridModel.CountsAsRotatingMass).Sum(g => g.H * g.MBase);
            Assert.Equal(n289, m.SystemInertiaMwS);
            Assert.Equal(n289 / 1000.0, m.SystemInertiaGvaS);
            double oldTripSum = m.Generators.Sum(g => g.H * g.MBase);   // trip's sum before N314
            Assert.Equal(oldTripSum, m.SystemInertiaMwS);
            foreach (Generator g in m.Generators)
                Assert.Equal(GridModel.CountsAsRotatingMass(g) ? g.H * g.MBase : 0.0, GridModel.RotatingInertiaMwS(g));
        }
    }

    [Fact]
    public void N314_a_converter_typed_unit_adds_no_inertia_whatever_its_H()
    {
        GridModel uk = JsonGridLoader.LoadDirectory(RepoPath("data", "uk"));
        int k = uk.Generators.ToList().FindIndex(g => g.Name == "North Scotland Onshore Wind");
        Assert.True(k >= 0);
        Generator[] gens = uk.Generators.ToArray();
        gens[k] = gens[k] with { H = 5.0 };
        GridModel withH = uk.CopyWith(generators: gens);
        Assert.Equal(0.0, GridModel.RotatingInertiaMwS(withH.Generators[k]));
        Assert.Equal(uk.SystemInertiaMwS, withH.SystemInertiaMwS);
        Assert.True(withH.Generators.Sum(g => g.H * g.MBase) > withH.SystemInertiaMwS);   // the old sum would have counted it
    }

    /// <summary>
    /// The process: data/uk with its North Scotland onshore wind unit given H = 5 s prints exactly what data/uk prints for
    /// the same trip (the wind unit's "inertia" is a converter's synthetic figure, never rotating mass); before N314 the
    /// System inertia line rose by 5 x 3000 MW.s and every swing number moved.
    /// </summary>
    [Fact]
    public void N314_trip_ignores_a_converter_units_H_and_prints_the_bundled_numbers()
    {
        string dir = CopyCaseFolder(RepoPath("data", "uk"));
        try
        {
            string genPath = Path.Combine(dir, "generation.json");
            string text = File.ReadAllText(genPath);
            const string find = "\"name\": \"North Scotland Onshore Wind\", \"bus\": 1,  \"type\": \"onshore\",      \"capacityMva\": 3000, \"capacityFactor\": 0.50, \"voltageSetpoint\": 1.04, \"inertiaH\": 0.0";
            Assert.Contains(find, text, StringComparison.Ordinal);
            File.WriteAllText(genPath, text.Replace(find, find[..^3] + "5.0", StringComparison.Ordinal));

            CliProcess.Result bundled = CliProcess.RunBuilt("trip", "--dir", RepoPath("data", "uk"), "Torness");
            CliProcess.Result variant = CliProcess.RunBuilt("trip", "--dir", dir, "Torness");
            AssertExit(0, bundled);
            AssertExit(0, variant);
            Assert.Equal(bundled.StdOut, variant.StdOut);
            GridModel uk = JsonGridLoader.LoadDirectory(RepoPath("data", "uk"));
            Assert.Contains($"System inertia: {uk.SystemInertiaMwS / 1000:F1} ->", variant.StdOut, StringComparison.Ordinal);
        }
        finally { Clean(dir); }
    }

    [Fact]
    public void N314_trip_reads_the_shared_total_in_source()
    {
        string program = File.ReadAllText(RepoPath("src", "GridSim.Cli", "Program.cs"));
        Assert.Contains("double totalInertia = model.SystemInertiaMwS;", program, StringComparison.Ordinal);
        Assert.Contains("GridModel.RotatingInertiaMwS(gen)", program, StringComparison.Ordinal);
        Assert.DoesNotContain("double totalInertia = model.Generators.Sum(g => g.H * g.MBase);", program, StringComparison.Ordinal);
    }

    // ---- AssetEnvelope: the shared copy ------------------------------------------------------------------------------

    /// <summary>CopyWith(assetDynamics:) replaces only the bundle and carries every other part by reference, as the
    /// hand-built copy in AssetEnvelope did; null keeps the model's own bundle.</summary>
    [Fact]
    public void N314_copywith_asset_dynamics_replaces_only_the_bundle()
    {
        GridModel m = JsonGridLoader.LoadDirectory(RepoPath("data", "gb-synthetic"));
        Assert.NotNull(m.AssetDynamics);
        var replaced = new AssetDynamicsModel(new AssetClassLibrary(m.AssetDynamics!.Library.Classes.ToList()), m.AssetDynamics.Bindings);
        GridModel c = m.CopyWith(assetDynamics: replaced);
        Assert.Same(replaced, c.AssetDynamics);
        Assert.Same(m.Buses, c.Buses);
        Assert.Same(m.Branches, c.Branches);
        Assert.Same(m.Oltcs, c.Oltcs);
        Assert.Same(m.Boundaries, c.Boundaries);
        Assert.Same(m.Protections, c.Protections);
        Assert.Same(m.Generators, c.Generators);
        Assert.Equal(m.AllGenerators.ToArray(), c.AllGenerators.ToArray());
        Assert.Equal(m.BaseMva, c.BaseMva);
        Assert.Equal(m.FrequencyHz, c.FrequencyHz);
        Assert.Equal(m.Name, c.Name);
        Assert.Same(m.AssetDynamics, m.CopyWith().AssetDynamics);
    }

    [Fact]
    public void N314_asset_envelope_no_longer_hand_builds_a_model()
    {
        string src = File.ReadAllText(RepoPath("src", "GridSim.Core", "Assets", "AssetEnvelope.cs"));
        Assert.DoesNotContain("new GridModel", src, StringComparison.Ordinal);
        Assert.Contains("model.CopyWith(assetDynamics: new AssetDynamicsModel(library, spec.Bindings))", src, StringComparison.Ordinal);
    }

    // ---- fit-frequency: the trace's nominal ---------------------------------------------------------------------------

    [Fact]
    public void N314_trace_reader_takes_the_nominal_the_first_sample_names()
    {
        var s60 = FrequencyTraceFile.Parse(new[] { "seconds,Hz", "0,60", "1,59.9", "2,59.8" }, "t.csv", null, out double nom60);
        Assert.Equal(60.0, nom60);
        Assert.Equal(3, s60.Count);
        FrequencyTraceFile.Parse(new[] { "0,50.02", "1,49.9", "2,49.8" }, "t.csv", null, out double nom50);
        Assert.Equal(50.0, nom50);
        // A later row is held to the first row's nominal: 54.9 is within 5 Hz of 50 but the trace named 60.
        var ex = Assert.Throws<InvalidDataException>(() => FrequencyTraceFile.Parse(new[] { "0,60", "1,59.9", "2,54.9" }, "t.csv", null, out _));
        Assert.Contains("line 3: 54.9 Hz is 5 Hz or more from the 60 Hz nominal", ex.Message, StringComparison.Ordinal);
        Assert.Contains("this is not a 60 Hz loss-of-infeed trace", ex.Message, StringComparison.Ordinal);
        // A first sample near neither standard nominal is refused, pointing at --nominal-hz.
        var none = Assert.Throws<InvalidDataException>(() => FrequencyTraceFile.Parse(new[] { "seconds,Hz", "0,55", "1,54.9", "2,54.8" }, "t.csv", null, out _));
        Assert.Contains("line 2: 55 Hz, the first sample, is not within 5 Hz of a standard nominal (50 or 60 Hz)", none.Message, StringComparison.Ordinal);
        Assert.Contains("--nominal-hz", none.Message, StringComparison.Ordinal);
        Assert.StartsWith("t.csv", none.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void N314_trace_reader_takes_a_declared_nominal_and_the_old_form_stays_50_hz()
    {
        var s = FrequencyTraceFile.Parse(new[] { "0,25", "1,24.8", "2,24.9" }, "t.csv", 25.0, out double used);
        Assert.Equal(25.0, used);
        Assert.Equal(new[] { 25.0, 24.8, 24.9 }, s.Select(x => x.Hz).ToArray());
        // The declared nominal wins over what the samples would name.
        Assert.Throws<InvalidDataException>(() => FrequencyTraceFile.Parse(new[] { "0,60", "1,59.9", "2,59.8" }, "t.csv", 50.0, out _));
        // The two-argument form is the F stream's 50 Hz rule, unchanged (a 60 Hz trace refused with its old message).
        var ex = Assert.Throws<InvalidDataException>(() => FrequencyTraceFile.Parse(new[] { "0,60", "1,59.9", "2,59.8" }, "t.csv"));
        Assert.Contains("line 1: 60 Hz is 5 Hz or more from the 50 Hz nominal", ex.Message, StringComparison.Ordinal);
        // An unusable declared nominal is a programming error here (the CLI refuses it first, exit 2).
        Assert.Throws<ArgumentOutOfRangeException>(() => FrequencyTraceFile.Parse(new[] { "0,1", "1,1", "2,1" }, "t.csv", 5.0, out _));
    }

    [Fact]
    public void N314_nominal_rules_and_the_seed_at_50_hz_are_the_old_seed()
    {
        Assert.Equal(new[] { 50.0, 60.0 }, FrequencyTraceFile.StandardNominalsHz.ToArray());
        Assert.False(FrequencyTraceFile.IsAcceptableNominal(5.0));
        Assert.True(FrequencyTraceFile.IsAcceptableNominal(5.000001));
        Assert.False(FrequencyTraceFile.IsAcceptableNominal(double.NaN));
        Assert.False(FrequencyTraceFile.IsAcceptableNominal(double.PositiveInfinity));
        Assert.False(FrequencyTraceFile.IsAcceptableNominal(-50.0));
        Assert.Equal(50.0, FrequencyTraceFile.StandardNominalFor(54.999));
        Assert.Equal(60.0, FrequencyTraceFile.StandardNominalFor(55.001));
        Assert.Null(FrequencyTraceFile.StandardNominalFor(55.0));   // exactly 5 Hz from both: neither (the margin is strict)
        Assert.Null(FrequencyTraceFile.StandardNominalFor(65.0));
        // Bit-identity of every 50 Hz fit: the seed at 50 Hz equals the old seed, field for field.
        Assert.Equal(FrequencyFitCli.Seed(null), FrequencyFitCli.Seed(null, 50.0));
        Assert.Equal(FrequencyFitCli.Seed(49.96), FrequencyFitCli.Seed(49.96, 50.0));
        Assert.Equal(60.0, FrequencyFitCli.Seed(59.97, 60.0).NominalHz);
        Assert.Equal(59.97, FrequencyFitCli.Seed(59.97, 60.0).InitialFrequencyHz);
    }

    private const double FitInertiaMwS = 200_000, FitDemandMw = 30_000, FitLossMw = 1200, FitLossAt = 1.0;

    /// <summary>A synthetic trace from the swing model at <paramref name="nominalHz"/> with truth damping 1.6 %/Hz.</summary>
    private static (string Path, List<FrequencyTraceSample> Samples) WriteTrace(string dir, double nominalHz)
    {
        var truth = new DynamicsConfig { DampingPctPerHz = 1.6, NominalHz = nominalHz };
        FrequencyResponse r = FrequencyDynamics.Simulate(FitInertiaMwS, FitDemandMw, new[] { new FrequencyEvent(FitLossAt, -FitLossMw) }, truth);
        var samples = new List<FrequencyTraceSample>();
        var lines = new List<string> { "seconds,Hz" };
        for (int i = 0; i * 0.5 <= 25.0; i++)
        {
            double t = i * 0.5;
            int k = Math.Min((int)Math.Round(t / truth.StepSeconds), r.Points.Count - 1);
            samples.Add(new FrequencyTraceSample(t, r.Points[k].Hz));
            lines.Add($"{R(t)},{R(r.Points[k].Hz)}");
        }
        string path = Path.Combine(dir, $"trace{nominalHz:0}.csv");
        File.WriteAllLines(path, lines);
        return (path, samples);
    }

    /// <summary>A 60 Hz trace (refused before N314) is fitted at 60 Hz: the printed damping is exactly the Core fit's at
    /// the trace's nominal, and it recovers the truth the trace was made with.</summary>
    [Fact]
    public void N314_fit_frequency_fits_a_60_hz_trace_at_60_hz()
    {
        string dir = TempDir();
        try
        {
            (string path, List<FrequencyTraceSample> samples) = WriteTrace(dir, 60.0);
            CliProcess.Result r = CliProcess.RunBuilt("fit-frequency", path, "--inertia", "200", "--demand", "30000", "--loss", "1200");
            AssertExit(0, r);
            Assert.Contains("nominal 60 Hz", r.StdOut, StringComparison.Ordinal);
            DynamicsConfig seed = FrequencyFitCli.Seed(FrequencyFitCli.PreEventHz(samples, FitLossAt), 60.0);
            FrequencyFitResult fit = FrequencyConstantsFitter.Fit(FitInertiaMwS, FitDemandMw, new FrequencyEvent(FitLossAt, -FitLossMw),
                samples, seed, new[] { FrequencyFitParam.Damping });
            Assert.True(fit.Converged);
            Assert.True(Math.Abs(fit.DampingPctPerHz - 1.6) < 0.02, $"recovered damping {fit.DampingPctPerHz} vs truth 1.6");
            string row = r.StdOut.Split('\n').Single(l => l.StartsWith("damping (% of demand per Hz)", StringComparison.Ordinal));
            Assert.Contains(R(fit.DampingPctPerHz), row, StringComparison.Ordinal);
            Assert.Contains($"RMS error {R(fit.RmsErrorHz)} Hz", r.StdOut, StringComparison.Ordinal);
        }
        finally { Clean(dir); }
    }

    /// <summary>A 50 Hz trace prints the same bytes with or without --nominal-hz 50 (the inferred nominal is the old one).</summary>
    [Fact]
    public void N314_fit_frequency_50_hz_is_unchanged_and_the_flag_is_checked()
    {
        string dir = TempDir();
        try
        {
            (string path, _) = WriteTrace(dir, 50.0);
            string[] baseArgs = { "fit-frequency", path, "--inertia", "200", "--demand", "30000", "--loss", "1200" };
            CliProcess.Result plain = CliProcess.RunBuilt(baseArgs);
            CliProcess.Result flagged = CliProcess.RunBuilt(baseArgs.Concat(new[] { "--nominal-hz", "50" }).ToArray());
            AssertExit(0, plain);
            AssertExit(0, flagged);
            Assert.Equal(plain.StdOut, flagged.StdOut);
            Assert.Contains("nominal 50 Hz", plain.StdOut, StringComparison.Ordinal);

            foreach (string bad in new[] { "0", "5", "-50" })
            {
                CliProcess.Result r = CliProcess.RunBuilt(baseArgs.Concat(new[] { "--nominal-hz", bad }).ToArray());
                AssertExit(2, r);
                Assert.Contains("error: --nominal-hz expected a finite nominal frequency above 5 Hz", r.StdErr, StringComparison.Ordinal);
            }
            // A 50 Hz trace declared at 60 Hz: every sample is 10 Hz off, so the trace is refused (exit 1) naming line 2.
            CliProcess.Result wrong = CliProcess.RunBuilt(baseArgs.Concat(new[] { "--nominal-hz", "60" }).ToArray());
            AssertExit(1, wrong);
            Assert.Contains("line 2:", wrong.StdErr, StringComparison.Ordinal);
            Assert.Contains("from the 60 Hz nominal", wrong.StdErr, StringComparison.Ordinal);

            // A trace near neither 50 nor 60 Hz: exit 1 asking for the flag; with the flag it runs.
            string odd = Path.Combine(dir, "odd.csv");
            File.WriteAllLines(odd, new[] { "seconds,Hz", "0,25", "0.5,25", "1.5,24.9", "3,24.85", "6,24.9", "10,24.95" });
            CliProcess.Result noFlag = CliProcess.RunBuilt("fit-frequency", odd, "--inertia", "200", "--demand", "30000", "--loss", "1200");
            AssertExit(1, noFlag);
            Assert.Contains("give the nominal with --nominal-hz", noFlag.StdErr, StringComparison.Ordinal);
            CliProcess.Result withFlag = CliProcess.RunBuilt("fit-frequency", odd, "--inertia", "200", "--demand", "30000", "--loss", "1200", "--nominal-hz", "25");
            Assert.True(withFlag.ExitCode is 0 or 3, withFlag.ToString());
            Assert.Contains("nominal 25 Hz", withFlag.StdOut, StringComparison.Ordinal);
        }
        finally { Clean(dir); }
    }

    // ---- classical stability --dyn-data ----------------------------------------------------------------------------------

    private static string DynFixture => RepoPath("tests", "GridSim.Tests", "fixtures", "dynamics", "wscc9-genrou.dynamics.json");

    /// <summary>The fixture rewritten so every value the classical model reads equals the built-in case9 data: X'd the
    /// Anderson-Fouad map (0.0608, 0.1198, 0.1813), damping 0, H the case's own. Subtransient reactances and the leakage
    /// are lowered only to keep the GENROU ordering rules (X''d below X'd, Xl below X''d) the loader checks.</summary>
    private static string BuiltInEquivalentFixture(string dir)
    {
        string text = File.ReadAllText(DynFixture);
        (string Find, string Replace)[] edits =
        {
            ("\"xdPu\": 1.0, \"xdpPu\": 0.30, \"xdppPu\": 0.22, \"xqPu\": 0.65, \"xqpPu\": 0.40, \"xqppPu\": 0.22,",
             "\"xdPu\": 1.0, \"xdpPu\": 0.0608, \"xdppPu\": 0.05, \"xqPu\": 0.65, \"xqpPu\": 0.40, \"xqppPu\": 0.05,"),
            ("\"xlPu\": 0.15,", "\"xlPu\": 0.04,"),
            ("\"inertiaH\": 6.4, \"damping\": 0.5,", "\"inertiaH\": 6.4, \"damping\": 0.0,"),
            ("\"xdPu\": 1.8, \"xdpPu\": 0.30, \"xdppPu\": 0.25, \"xqPu\": 1.7, \"xqpPu\": 0.55, \"xqppPu\": 0.25,",
             "\"xdPu\": 1.8, \"xdpPu\": 0.1198, \"xdppPu\": 0.1, \"xqPu\": 1.7, \"xqpPu\": 0.55, \"xqppPu\": 0.1,"),
            ("\"xdpPu\": 0.18,", "\"xdpPu\": 0.1813,"),
        };
        foreach ((string find, string replace) in edits)
        {
            Assert.Contains(find, text, StringComparison.Ordinal);
            text = text.Replace(find, replace, StringComparison.Ordinal);
        }
        string path = Path.Combine(dir, "builtin-equivalent.dynamics.json");
        File.WriteAllText(path, text);
        return path;
    }

    // The lines a --dyn-data run adds or rewords: the header (X'd source), the machine data line and its notes, and the
    // provenance footer. Every other line (every number) must be the plain run's, byte for byte.
    private static string WithoutDynDataLines(string stdout) => string.Join('\n', stdout.Split('\n').Where(l =>
        !l.Contains("X'd = ", StringComparison.Ordinal) && !l.StartsWith("machine data:", StringComparison.Ordinal)
        && !l.StartsWith("  note: generator", StringComparison.Ordinal) && !l.StartsWith("Provenance:", StringComparison.Ordinal)));

    /// <summary>A file whose X'd, H and D equal case9's built-in values gives the plain classical study bit for bit
    /// (single bus with the CCT, a fixed clear, and the screen): the file reaches exactly the inputs the map fed.</summary>
    [Fact]
    public void N314_classical_stability_with_built_in_equivalent_data_is_bit_identical()
    {
        string dir = TempDir();
        try
        {
            string file = BuiltInEquivalentFixture(dir);
            foreach (string[] args in new[]
            {
                new[] { "stability", "case9" },
                new[] { "stability", "case9", "--fault-bus", "5", "--clear", "200" },
                new[] { "stability", "case9", "--screen" },
            })
            {
                CliProcess.Result plain = CliProcess.RunBuilt(args);
                CliProcess.Result dyn = CliProcess.RunBuilt(args.Concat(new[] { "--dyn-data", file }).ToArray());
                AssertExit(0, plain);
                AssertExit(0, dyn);
                Assert.Contains("X'd = 3 from builtin-equivalent.dynamics.json", dyn.StdOut, StringComparison.Ordinal);
                Assert.Contains("machine data: 'WSCC 9-bus GENROU test machines'  *  H, X'd and damping of 3 machine(s) from the file", dyn.StdOut, StringComparison.Ordinal);
                Assert.Contains("Provenance: the machine data file's values are not all Recorded / DerivedFromRecorded", dyn.StdOut, StringComparison.Ordinal);
                Assert.Equal(WithoutDynDataLines(plain.StdOut), WithoutDynDataLines(dyn.StdOut));
            }
        }
        finally { Clean(dir); }
    }

    /// <summary>The fixture's own data (X'd 0.30 / 0.30 / 0.18, bus 2 damped 0.5) reach the study: the CCT moves off the
    /// built-in value, and the refusal the classical engine used to give is gone.</summary>
    [Fact]
    public void N314_classical_stability_takes_the_files_machine_data()
    {
        CliProcess.Result plain = CliProcess.RunBuilt("stability", "case9");
        CliProcess.Result dyn = CliProcess.RunBuilt("stability", "case9", "--dyn-data", DynFixture);
        AssertExit(0, plain);
        AssertExit(0, dyn);
        Assert.DoesNotContain("applies to the RMS engine only", dyn.StdErr, StringComparison.Ordinal);
        string Cct(CliProcess.Result r) => r.StdOut.Split('\n').Single(l => l.StartsWith("Critical clearing time", StringComparison.Ordinal));
        Assert.NotEqual(Cct(plain), Cct(dyn));   // X'd five times the Anderson-Fouad value at bus 1: a shorter CCT
        Assert.Contains("X'd = 3 from wscc9-genrou.dynamics.json", dyn.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void N314_classical_stability_bad_dynamics_file_exits_1()
    {
        string dir = TempDir();
        try
        {
            string bad = Path.Combine(dir, "bad.dynamics.json");
            File.WriteAllText(bad, File.ReadAllText(DynFixture).Replace("\"tdopS\": 6.0,", "", StringComparison.Ordinal));
            foreach (string[] args in new[] { new[] { "stability", "case9", "--dyn-data", bad }, new[] { "stability", "case9", "--screen", "--dyn-data", bad } })
            {
                CliProcess.Result r = CliProcess.RunBuilt(args);
                AssertExit(1, r);
                Assert.Contains("error: stability: --dyn-data:", r.StdErr, StringComparison.Ordinal);
                Assert.Equal("", r.StdOut.Trim());
            }
        }
        finally { Clean(dir); }
    }

    /// <summary>Core: the CCT search's new damping input. Null and an all-zero array give the same bracket bit for bit;
    /// a damped bracket is consistent with a direct damped study (its stable end holds, its unstable end loses); bad
    /// arrays are refused.</summary>
    [Fact]
    public void N314_cct_search_damping_input()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);
        var xdp = model.Generators.Select(g => IeeeCases.Wscc9TransientReactancePu[g.Bus]).ToArray();
        ClassicalInit.FaultScreen screen = ClassicalInit.PrepareFaultScreen(model, pf, xdp, holdNonMachines: true);
        CctBracket undamped = ClassicalInit.CriticalClearingBracket(screen, 7);
        Assert.Equal(undamped, ClassicalInit.CriticalClearingBracket(screen, 7, machineDamping: new double[model.Generators.Count]));

        double[] d = { 2.0, 5.0, 1.0 };
        CctBracket damped = ClassicalInit.CriticalClearingBracket(screen, 7, machineDamping: d);
        ClassicalInit.Prepared p = screen.ForFault(7);
        double[] dm = ClassicalInit.DampingByMachine(model, p, d)!;
        Assert.Equal(p.MachineIndex.Select(k => d[k]).ToArray(), dm);
        bool Survives(double clear)
        {
            var fault = new ClassicalMultiMachine.Fault(ClassicalInit.CctOnsetSeconds, ClassicalInit.CctOnsetSeconds + clear, p.YReducedFaulted!);
            return ClassicalMultiMachine.Simulate(p.H, dm, p.Pm, p.Eq, p.Delta0, p.YReduced, model.FrequencyHz,
                ClassicalInit.CctDefaultStepSeconds, ClassicalInit.CctOnsetSeconds + clear + ClassicalInit.CctPostClearWindowSeconds, fault).StayedInSynchronism;
        }
        Assert.True(Survives(damped.StableSteps * damped.StepSeconds));
        if (damped.UnstableSteps is int u) Assert.False(Survives(u * damped.StepSeconds));

        Assert.Null(ClassicalInit.DampingByMachine(model, p, null));
        Assert.Throws<ArgumentException>(() => ClassicalInit.DampingByMachine(model, p, new double[2]));
        Assert.Throws<ArgumentException>(() => ClassicalInit.DampingByMachine(model, p, new[] { 0.0, double.NaN, 0.0 }));
    }

    // ---- internal marks -------------------------------------------------------------------------------------------------

    [Fact]
    public void N314_live_fleet_integrator_is_internal_and_the_converter_store_is_marked()
    {
        Assert.False(typeof(MultiDeviceFrequencyIntegrator).IsPublic);
        string mdfi = File.ReadAllText(RepoPath("src", "GridSim.Core", "Dynamics", "MultiDeviceFrequencyIntegrator.cs"));
        Assert.Contains("// Internal use (F stream, R079, Ruling R1): the live twin of FleetDynamics, pinned equal to it by B1;", mdfi, StringComparison.Ordinal);
        string conv = File.ReadAllText(RepoPath("src", "GridSim.Core", "Dynamics", "Models", "RmsConverter.cs"));
        Assert.Contains("Internal use (F stream, R079, Ruling R1): no case format supplies a converter's DC store", conv, StringComparison.Ordinal);
    }
}
