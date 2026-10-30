using GridSim.Core.Assets;
using GridSim.Core.Assets.Protection;
using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N321 (LF7, October 2026; Ruling R1: Mark delegated EE / product calls 2026-10-04), three leftovers of LF5:
/// <list type="bullet">
/// <item><c>screen</c> (RocofScreening.Screen(model) and the CLI's RunScreen) uses the N289 rotating-mass total
/// GridModel.SystemInertiaMwS, as <c>trip</c> does since N314: a converter-typed unit's H counts nothing and its loss
/// removes nothing. Bit-identical on every bundled case (pinned against the old all-unit formula).</item>
/// <item>OneAxisMachine.Parameters validates Xd (finite, &gt; 0, at least X'd), the D-DOC-056 rule SubtransientMachine
/// and (N266) TwoAxisMachine apply.</item>
/// <item>The JSON v2 case writer writes the asset-dynamics and protection bundles (local templates, bindings, element
/// templates, placed devices), and reads them back equal.</item>
/// </list>
/// </summary>
public sealed partial class LF7GbSyntheticTests
{
    private static string RepoPath(params string[] parts) => Path.Combine(new[] { CliProcess.Root() }.Concat(parts).ToArray());

    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-lf7-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Clean(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ================================================================================== screen: the rotating-mass total

    /// <summary>The screen as it was before N321: H x MBase summed over EVERY unit, and each loss taking out its own
    /// H x MBase, through the same public overload Screen(model) calls.</summary>
    private static IReadOnlyList<RocofScreeningResult> OldScreen(GridModel m)
    {
        double total = m.Generators.Sum(g => g.H * g.MBase);
        double demand = m.Buses.Sum(b => b.Pd);
        IEnumerable<Contingency> conts = m.Generators.Where(g => g.Pg > 0)
            .Select(g => new Contingency(g.Name ?? $"gen@{g.Bus}", g.Pg, g.H * g.MBase));
        return RocofScreening.Screen(total, demand, conts, FrequencyLimits.ActiveRocofLimitHzPerS,
            new DynamicsConfig { NominalHz = m.FrequencyHz }, FrequencyBands.ForModel(m));
    }

    private static void AssertSameScreen(IReadOnlyList<RocofScreeningResult> expected, IReadOnlyList<RocofScreeningResult> actual, string what)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            RocofScreeningResult e = expected[i], a = actual[i];
            Assert.Equal(e.Name, a.Name);
            Assert.True(Bits(e.LostMw) == Bits(a.LostMw), $"{what} row {i} LostMw {e.LostMw:R} vs {a.LostMw:R}");
            Assert.True(Bits(e.RemainingInertiaGvaS) == Bits(a.RemainingInertiaGvaS), $"{what} row {i} remaining {e.RemainingInertiaGvaS:R} vs {a.RemainingInertiaGvaS:R}");
            Assert.True(Bits(e.InitialRocofHzPerS) == Bits(a.InitialRocofHzPerS), $"{what} row {i} RoCoF {e.InitialRocofHzPerS:R} vs {a.InitialRocofHzPerS:R}");
            Assert.True(Bits(e.NadirHz) == Bits(a.NadirHz), $"{what} row {i} nadir {e.NadirHz:R} vs {a.NadirHz:R}");
            Assert.Equal(e, a);   // and every flag
        }
    }

    [Fact]
    public void N321_the_screen_is_bit_identical_to_the_old_all_unit_sum_on_bundled_cases()
    {
        // No bundled case has a converter-typed unit with H above 0 (R239 / N289), so the rotating-mass total and the
        // all-unit sum are the same terms in the same order: every screened number is unchanged, bit for bit.
        foreach (GridModel m in new[]
        {
            JsonGridLoader.LoadDirectory(RepoPath("data", "uk")),
            JsonGridLoader.LoadDirectory(RepoPath("data", "gb-synthetic")),
        })
        {
            Assert.Equal(Bits(m.Generators.Sum(g => g.H * g.MBase)), Bits(m.SystemInertiaMwS));
            AssertSameScreen(OldScreen(m), RocofScreening.Screen(m), m.Name);
        }
    }

    [Fact]
    public void N321_a_converter_units_H_adds_no_inertia_to_the_screen()
    {
        GridModel uk = JsonGridLoader.LoadDirectory(RepoPath("data", "uk"));
        int k = uk.Generators.ToList().FindIndex(g => g.Name == "North Scotland Onshore Wind");
        Assert.True(k >= 0);
        Generator[] gens = uk.Generators.ToArray();
        gens[k] = gens[k] with { H = 5.0 };
        GridModel withH = uk.CopyWith(generators: gens);

        // After N321: the converter's H changes nothing, row for row, bit for bit (its own loss is still screened: its
        // output is lost infeed, and it takes out no inertia).
        AssertSameScreen(RocofScreening.Screen(uk), RocofScreening.Screen(withH), "uk with wind H = 5");
        RocofScreeningResult wind = RocofScreening.Screen(withH).Single(r => r.Name == "North Scotland Onshore Wind");
        Assert.Equal(Bits(uk.SystemInertiaMwS / 1000.0), Bits(wind.RemainingInertiaGvaS));

        // Before N321 (the old formula): the 15,000 MW.s of the converter's H counted, so every remaining inertia rose.
        RocofScreeningResult oldTop = OldScreen(withH).First(r => r.Name != "North Scotland Onshore Wind");
        RocofScreeningResult newTop = RocofScreening.Screen(withH).Single(r => r.Name == oldTop.Name);
        Assert.True(oldTop.RemainingInertiaGvaS > newTop.RemainingInertiaGvaS);
    }

    [Fact]
    public void N321_screen_reads_the_shared_total_in_source()
    {
        string program = File.ReadAllText(RepoPath("src", "GridSim.Cli", "Program.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
        int at = program.IndexOf("void RunScreen(GridModel model, int top, string? csvPath = null)", StringComparison.Ordinal);
        Assert.True(at >= 0, "RunScreen not found");
        string body = program[at..program.IndexOf("\n}\n", at, StringComparison.Ordinal)];
        Assert.Contains("double totalInertiaMwS = model.SystemInertiaMwS;", body, StringComparison.Ordinal);
        Assert.Contains("double totalGva = model.SystemInertiaGvaS;", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Sum(g => g.H * g.MBase)", body, StringComparison.Ordinal);

        string screening = File.ReadAllText(RepoPath("src", "GridSim.Core", "Dynamics", "RocofScreening.cs"));
        Assert.Contains("double total = model.SystemInertiaMwS;", screening, StringComparison.Ordinal);
        Assert.Contains("GridModel.RotatingInertiaMwS(g)", screening, StringComparison.Ordinal);
        Assert.DoesNotContain("Sum(g => g.H * g.MBase)", screening, StringComparison.Ordinal);
    }

    [Fact]
    public void N321_the_screen_verb_ignores_a_converter_units_H_and_prints_the_bundled_screen()
    {
        // data/uk with its North Scotland onshore wind unit given H = 5 s screens exactly as data/uk does. Before N321 the
        // System inertia line rose by 15.0 GVAs and every remaining-inertia, RoCoF and nadir figure moved.
        string dir = TempDir();
        try
        {
            foreach (string f in Directory.GetFiles(RepoPath("data", "uk"))) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
            string genPath = Path.Combine(dir, "generation.json");
            string text = File.ReadAllText(genPath);
            const string find = "\"name\": \"North Scotland Onshore Wind\", \"bus\": 1,  \"type\": \"onshore\",      \"capacityMva\": 3000, \"capacityFactor\": 0.50, \"voltageSetpoint\": 1.04, \"inertiaH\": 0.0";
            Assert.Contains(find, text, StringComparison.Ordinal);
            File.WriteAllText(genPath, text.Replace(find, find[..^3] + "5.0", StringComparison.Ordinal));

            CliProcess.Result bundled = CliProcess.RunBuilt("screen", "--dir", RepoPath("data", "uk"));
            CliProcess.Result variant = CliProcess.RunBuilt("screen", "--dir", dir);
            Assert.Equal(bundled.ExitCode, variant.ExitCode);
            Assert.True(bundled.ExitCode is 0 or 3, bundled.ToString());
            Assert.Equal(bundled.StdOut, variant.StdOut);
        }
        finally { Clean(dir); }
    }

    // ================================================================================== OneAxisMachine validates Xd

    private static OneAxisMachine.Parameters OneAxis(double xd, double xdp = 0.3) => new(
        InertiaSeconds: 4.0, DampingPu: 0.0, Xd: xd, Xdp: xdp, Xq: 1.7, Tdop: 6.0,
        ExternalReactance: 0.2, InfiniteBusVoltagePu: 1.0);

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.8)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0.29)]   // below X'd = 0.3
    public void N321_one_axis_machine_refuses_an_invalid_xd(double xd)
    {
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => OneAxis(xd));
        Assert.Equal("Xd", ex.ParamName);
        Assert.Contains("Xd must be a finite number > 0 and at least X'd", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1.8)]    // the tests' round-rotor machine
    [InlineData(0.3)]    // Xd = X'd: the classical reduction identity, allowed
    public void N321_one_axis_machine_keeps_a_valid_xd_exactly(double xd)
    {
        OneAxisMachine.Parameters p = OneAxis(xd);
        Assert.Equal(Bits(xd), Bits(p.Xd));
        Assert.Equal(Bits(0.3), Bits(p.Xdp));
    }

    // ================================================================================== the v2 writer writes the bundles

    private static string SchemasDir => RepoPath("schemas", "v2");

    private static GridModel GbSynthetic() => JsonGridLoader.LoadDirectory(RepoPath("data", "gb-synthetic"));

    /// <summary>A fresh model with every part of <paramref name="m"/>, with the given bundles.</summary>
    private static GridModel WithParts(GridModel m, AssetDynamicsModel? dynamics, ProtectionModel? protections) => new()
    {
        Name = m.Name,
        BaseMva = m.BaseMva,
        FrequencyHz = m.FrequencyHz,
        Buses = m.Buses,
        Generators = m.AllGenerators.ToList(),
        Branches = m.Branches.ToList(),
        Oltcs = m.Oltcs,
        Boundaries = m.Boundaries,
        AssetDynamics = dynamics,
        Protections = protections,
    };

    [Fact]
    public void N321_the_writers_template_kinds_are_the_loaders_table_read_backwards()
    {
        var written = Enum.GetValues<AssetKind>().Select(V2CaseWriter.V2KindOf).OfType<string>().ToHashSet(StringComparer.Ordinal);
        Assert.Equal(V2CaseLoader.V1KindOf.Keys.Order(StringComparer.Ordinal), written.Order(StringComparer.Ordinal));
        Assert.Equal("gridFollowingInverter", V2CaseWriter.V2KindOf(AssetKind.GridFollowingInverter));
        Assert.Equal("gridFormingInverter", V2CaseWriter.V2KindOf(AssetKind.GridFormingInverter));
        Assert.Equal("oltc", V2CaseWriter.V2KindOf(AssetKind.Transformer));
        Assert.Equal("lineType", V2CaseWriter.V2KindOf(AssetKind.Line));
        Assert.Equal("protection", V2CaseWriter.V2KindOf(AssetKind.Protection));
        Assert.Null(V2CaseWriter.V2KindOf(AssetKind.SynchronousMachine));
        // Each v2 kind maps back (loader) to the v1 kind whose AssetKind the writer started from.
        Assert.Equal("transformer", V2CaseLoader.V1KindOf["oltc"]);
        Assert.Equal("line", V2CaseLoader.V1KindOf["lineType"]);
    }

    [Fact]
    public void N321_the_shipped_gb_synthetic_bundles_are_written_and_read_back_equal()
    {
        GridModel m = GbSynthetic();
        Assert.NotNull(m.AssetDynamics);
        Assert.NotNull(m.Protections);
        string dir = TempDir();
        try
        {
            V2CaseWriteResult w = V2CaseWriter.Write(m, Path.Combine(dir, "gbs.case.json"));
            Assert.Empty(w.Notes);
            string manifest = File.ReadAllText(w.ManifestPath), network = File.ReadAllText(w.NetworkPath);
            Assert.Contains("\"localTemplates\"", manifest, StringComparison.Ordinal);
            Assert.Contains("\"bindings\"", manifest, StringComparison.Ordinal);
            Assert.Contains("\"id\": \"gridsim.local.gfl-wind-2024\"", manifest, StringComparison.Ordinal);
            Assert.Contains("\"template\": \"gridsim.local.gfl-wind-2024@1\"", manifest, StringComparison.Ordinal);
            Assert.Contains("\"protections\"", network, StringComparison.Ordinal);

            GridModel back = V2CaseLoader.Load(w.ManifestPath, SchemasDir);
            // The classes: same names in the same order, same kinds, modes, parameters (bits, in order) and provenance.
            List<AssetClass> a = m.AssetDynamics!.Library.Classes.ToList(), b = back.AssetDynamics!.Library.Classes.ToList();
            Assert.Equal(a.Select(c => c.Name), b.Select(c => c.Name));
            for (int i = 0; i < a.Count; i++)
            {
                Assert.Equal(a[i].Kind, b[i].Kind);
                Assert.Equal(a[i].Mode, b[i].Mode);
                Assert.Equal(a[i].Params.Keys, b[i].Params.Keys);
                foreach (string key in a[i].Params.Keys) Assert.Equal(Bits(a[i].Params[key]), Bits(b[i].Params[key]));
                Assert.Equal(a[i].Provenance.Keys, b[i].Provenance.Keys);
                foreach (string key in a[i].Provenance.Keys) Assert.Equal(a[i].Provenance[key], b[i].Provenance[key]);
            }
            Assert.Equal(m.AssetDynamics.Bindings.Rules, back.AssetDynamics.Bindings.Rules);
            Assert.Equal(m.Protections!.Instances, back.Protections!.Instances);
            Assert.Equal(m.Protections.Library.Classes.Select(c => c.Name), back.Protections.Library.Classes.Select(c => c.Name));
            Assert.Equal(m.AllGenerators.Select(g => g.DynamicsClass), back.AllGenerators.Select(g => g.DynamicsClass));
            Assert.Equal(m.Branches.Select(x => x.DynamicsClass), back.Branches.Select(x => x.DynamicsClass));
            // Every unit binds to the same class through the read-back rules.
            foreach ((Generator g0, Generator g1) in m.AllGenerators.Zip(back.AllGenerators))
                Assert.Equal(m.AssetDynamics.Bindings.Resolve(g0.DynamicsClass, g0.Type), back.AssetDynamics.Bindings.Resolve(g1.DynamicsClass, g1.Type));

            // Byte-stable with the bundles: the loaded-back model writes the same bytes.
            V2CaseWriteResult again = V2CaseWriter.Write(back, Path.Combine(dir, "two", "gbs.case.json"));
            Assert.Equal(File.ReadAllBytes(w.ManifestPath), File.ReadAllBytes(again.ManifestPath));
            Assert.Equal(File.ReadAllBytes(w.NetworkPath), File.ReadAllBytes(again.NetworkPath));
        }
        finally { Clean(dir); }
    }

    [Fact]
    public void N321_case_respellings_are_written_with_the_class_spelling_and_noted()
    {
        // v1 matches class names and match types ignoring case; v2 references and match types are exact (lower case).
        GridModel m = GbSynthetic();
        AssetDynamicsModel d = m.AssetDynamics!;
        var rules = d.Bindings.Rules.Select(r => r.MatchType == "solar" ? new AssetBinding("Solar", "GFL-Solar-2024") : r).ToList();
        Assert.Contains(rules, r => r.MatchType == "Solar");
        int k = m.AllGenerators.ToList().FindIndex(g => g.Type == "onshore");
        Assert.True(k >= 0);
        Generator[] gens = m.AllGenerators.ToArray();
        gens[k] = gens[k] with { DynamicsClass = "GFL-WIND-2024" };
        GridModel mixed = WithParts(m, new AssetDynamicsModel(d.Library, new AssetBindingSet(rules)), m.Protections).CopyWith(generators: gens);
        // The model itself binds exactly as before (case-insensitive).
        Assert.Equal("gfl-solar-2024", mixed.AssetDynamics!.Library.Get(mixed.AssetDynamics.Bindings.Resolve(null, "solar")!).Name);

        string dir = TempDir();
        try
        {
            V2CaseWriteResult w = V2CaseWriter.Write(mixed, Path.Combine(dir, "mixed.case.json"));
            Assert.Contains(w.Notes, n => n.StartsWith("2 class reference(s) spelled a class name in another letter case", StringComparison.Ordinal));
            Assert.Contains(w.Notes, n => n.StartsWith("1 binding match type(s) written in lower case", StringComparison.Ordinal));
            GridModel back = V2CaseLoader.Load(w.ManifestPath, SchemasDir);
            Assert.Contains(back.AssetDynamics!.Bindings.Rules, r => r == new AssetBinding("solar", "gfl-solar-2024"));
            Assert.Equal("gfl-wind-2024", back.AllGenerators[k].DynamicsClass);
        }
        finally { Clean(dir); }
    }

    [Fact]
    public void N321_a_bundle_that_cannot_be_written_exactly_is_left_out_whole_with_notes()
    {
        // A class name taken from an imported template (id@rev) cannot be a local template id: nothing of the bundles is
        // written (a part would load as a different bundle), and the notes say why, as before N321.
        GridModel m = GbSynthetic();
        AssetClass wind = m.AssetDynamics!.Library.Get("gfl-wind-2024");
        AssetClass imported = wind with { Name = "gridsim.lib.wind@2" };
        var library = new AssetClassLibrary(m.AssetDynamics.Library.Classes.Append(imported).ToList());
        GridModel bad = m.CopyWith(assetDynamics: new AssetDynamicsModel(library, m.AssetDynamics.Bindings));
        string dir = TempDir();
        try
        {
            V2CaseWriteResult w = V2CaseWriter.Write(bad, Path.Combine(dir, "bad.case.json"));
            Assert.Contains(w.Notes, n => n.StartsWith("the asset-dynamics / protection bundle cannot be written exactly", StringComparison.Ordinal)
                                          && n.Contains("class 'gridsim.lib.wind@2' cannot be a local template id", StringComparison.Ordinal));
            Assert.Contains(w.Notes, n => n.Contains("asset-dynamics bundle (asset classes and binding rules) is not written", StringComparison.Ordinal));
            Assert.Contains(w.Notes, n => n.Contains("protection bundle (placed devices and their classes) is not written", StringComparison.Ordinal));
            Assert.DoesNotContain("localTemplates", File.ReadAllText(w.ManifestPath), StringComparison.Ordinal);
            Assert.DoesNotContain("\"protections\"", File.ReadAllText(w.NetworkPath), StringComparison.Ordinal);
            GridModel back = V2CaseLoader.Load(w.ManifestPath, SchemasDir);
            Assert.Null(back.AssetDynamics);
            Assert.Null(back.Protections);
        }
        finally { Clean(dir); }
    }

    [Fact]
    public void N321_a_bundle_that_contradicts_itself_is_refused_and_nothing_is_written()
    {
        GridModel m = GbSynthetic();
        AssetDynamicsModel d = m.AssetDynamics!;
        ProtectionModel p = m.Protections!;
        string dir = TempDir();
        try
        {
            // A binding rule naming no class.
            GridModel ghostRule = WithParts(m, new AssetDynamicsModel(d.Library, new AssetBindingSet(new[] { new AssetBinding("wind", "no-such-class") })), p);
            InvalidDataException e1 = Assert.Throws<InvalidDataException>(() => V2CaseWriter.Write(ghostRule, Path.Combine(dir, "r.case.json")));
            Assert.Contains("names class 'no-such-class'", e1.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(dir, "r.case.json")));
            Assert.False(File.Exists(Path.Combine(dir, "r.network.json")));

            // A placed device naming no class.
            GridModel ghostDevice = WithParts(m, d, new ProtectionModel(p.Library, new[] { new ProtectionInstance("ghost", "no-such-class", m.Buses[0].Id, 0.0) }));
            InvalidDataException e2 = Assert.Throws<InvalidDataException>(() => V2CaseWriter.Write(ghostDevice, Path.Combine(dir, "d.case.json")));
            Assert.Contains("protective device 'ghost' uses class 'no-such-class'", e2.Message, StringComparison.Ordinal);

            // Two different classes under one name (the protection library's differs from the asset-dynamics library's).
            AssetClass lfdd = p.Library.Get("prot-lfdd-stage1");
            var otherLibrary = new AssetClassLibrary(p.Library.Classes.Select(c => c.Name == lfdd.Name ? c with { Mode = c.Mode + " (edited)" } : c).ToList());
            GridModel twoMeanings = WithParts(m, d, new ProtectionModel(otherLibrary, p.Instances));
            InvalidDataException e3 = Assert.Throws<InvalidDataException>(() => V2CaseWriter.Write(twoMeanings, Path.Combine(dir, "t.case.json")));
            Assert.Contains("two different classes named 'prot-lfdd-stage1'", e3.Message, StringComparison.Ordinal);
        }
        finally { Clean(dir); }
    }
}
