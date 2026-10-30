using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// DOC-13 (October 2026), stream 13 of review-progress/2026-10/DOCS-FINDINGS-TRIAGE.md: the model checks.
/// D-DOC-083: <see cref="GridModel.Validate"/> now refuses a system base or nominal frequency that is not a finite positive
/// number, a branch from a bus to itself and a branch with zero series impedance. D-DOC-082: an island (buses joined by
/// in-service branches) with no slack of its own is refused, so a multi-island JSON case with one slack no longer loads
/// and solves with exit 0. D-DOC-208: the Validate checks that had no test get one here. Every test is new.
/// </summary>
public sealed class Doc13ModelValidateTests
{
    private static Bus B(int id, BusType type = BusType.Pq, double kv = 132) => new(id, type, BaseKv: kv);

    private static GridModel Model(
        IReadOnlyList<Bus> buses,
        IReadOnlyList<Branch>? branches = null,
        IReadOnlyList<Generator>? gens = null,
        IReadOnlyList<Oltc>? oltcs = null,
        double baseMva = 100,
        double frequencyHz = 50) => new()
        {
            BaseMva = baseMva,
            FrequencyHz = frequencyHz,
            Buses = buses,
            Branches = branches ?? Array.Empty<Branch>(),
            Generators = gens ?? Array.Empty<Generator>(),
            Oltcs = oltcs ?? Array.Empty<Oltc>(),
        };

    /// <summary>A clean two-bus model: slack 1, load 2, one line.</summary>
    private static GridModel TwoBus(double baseMva = 100, double frequencyHz = 50, Branch? line = null) => Model(
        new[] { B(1, BusType.Slack), B(2) },
        new[] { line ?? new Branch(1, 2, 0.01, 0.1, Name: "L12") },
        new[] { new Generator(1, Pg: 10) },
        baseMva: baseMva, frequencyHz: frequencyHz);

    private static IReadOnlyList<string> Errors(GridModel m) =>
        m.Validate().Where(d => d.Severity == ValidationSeverity.Error).Select(d => d.Message).ToList();

    private static IReadOnlyList<string> Warnings(GridModel m) =>
        m.Validate().Where(d => d.Severity == ValidationSeverity.Warning).Select(d => d.Message).ToList();

    // ---- D-DOC-083: system base and nominal frequency ----------------------------------------------------------------

    [Theory]
    [InlineData(0.0)]
    [InlineData(-100.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void D083_a_system_base_that_is_not_a_finite_positive_power_is_an_error_naming_it(double baseMva)
    {
        IReadOnlyList<string> e = Errors(TwoBus(baseMva: baseMva));
        Assert.Single(e);
        Assert.Contains("BaseMva", e[0], StringComparison.Ordinal);
        Assert.Contains("not a finite power above 0 MVA", e[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-50.0)]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    public void D083_a_nominal_frequency_that_is_not_a_finite_positive_frequency_is_an_error_naming_it(double hz)
    {
        IReadOnlyList<string> e = Errors(TwoBus(frequencyHz: hz));
        Assert.Single(e);
        Assert.Contains("FrequencyHz", e[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1.0, 50.0)]
    [InlineData(10000.0, 60.0)]
    [InlineData(50.0 / 3.0, 50.0)]   // case533mt_hi's own base
    public void D083_ordinary_bases_and_frequencies_are_clean(double baseMva, double hz)
        => Assert.Empty(TwoBus(baseMva: baseMva, frequencyHz: hz).Validate());

    // ---- D-DOC-083: self-loops and zero impedance ---------------------------------------------------------------------

    [Fact]
    public void D083_a_self_loop_is_an_error_naming_the_branch_and_bus()
    {
        GridModel m = Model(new[] { B(1, BusType.Slack), B(2) },
            new[] { new Branch(1, 2, 0.01, 0.1), new Branch(2, 2, 0.01, 0.1, Name: "Loop") },
            new[] { new Generator(1, Pg: 10) });
        IReadOnlyList<string> e = Errors(m);
        Assert.Single(e);
        Assert.Contains("'Loop'", e[0], StringComparison.Ordinal);
        Assert.Contains("connects bus 2 to itself", e[0], StringComparison.Ordinal);
    }

    [Fact]
    public void D083_an_out_of_service_self_loop_is_still_an_error()
    {
        GridModel m = Model(new[] { B(1, BusType.Slack), B(2) },
            new[] { new Branch(1, 2, 0.01, 0.1), new Branch(1, 1, 0.01, 0.1, InService: false) },
            new[] { new Generator(1, Pg: 10) });
        Assert.Contains(Errors(m), x => x.Contains("connects bus 1 to itself", StringComparison.Ordinal)
                                         && x.Contains("#1 (1->1)", StringComparison.Ordinal));
    }

    [Fact]
    public void D083_zero_series_impedance_is_an_error_whatever_the_sign_of_zero()
    {
        // A loop, not InlineData: xUnit treats -0.0 and 0.0 as duplicate data.
        foreach ((double r, double x) in new[] { (0.0, 0.0), (-0.0, 0.0), (0.0, -0.0), (-0.0, -0.0) })
        {
            IReadOnlyList<string> e = Errors(TwoBus(line: new Branch(1, 2, r, x, Name: "Tie")));
            Assert.Single(e);
            Assert.Contains("'Tie'", e[0], StringComparison.Ordinal);
            Assert.Contains("zero series impedance (R = X = 0)", e[0], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void D083_an_out_of_service_zero_impedance_branch_is_still_an_error()
    {
        GridModel m = Model(new[] { B(1, BusType.Slack), B(2) },
            new[] { new Branch(1, 2, 0.01, 0.1), new Branch(1, 2, 0, 0, Name: "Spare", InService: false) },
            new[] { new Generator(1, Pg: 10) });
        Assert.Contains(Errors(m), x => x.Contains("'Spare'", StringComparison.Ordinal) && x.Contains("zero series impedance", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0.0, 1e-4)]    // the MATPOWER-style small-reactance tie
    [InlineData(0.01, 0.0)]    // a pure resistance (distribution feeders)
    public void D083_a_branch_with_either_part_non_zero_is_clean(double r, double x)
        => Assert.Empty(TwoBus(line: new Branch(1, 2, r, x)).Validate());

    [Fact]
    public void D083_the_JSON_loader_refuses_a_zero_impedance_line_by_name()
    {
        string dir = JsonCase(
            buses: "[ { \"id\": 1, \"baseKv\": 132 }, { \"id\": 2, \"baseKv\": 132 } ]",
            lines: "[ { \"name\": \"Tie12\", \"from\": 1, \"to\": 2, \"rPu\": 0, \"xPu\": 0 } ]");
        try
        {
            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => JsonGridLoader.LoadDirectory(dir));
            Assert.Contains("'Tie12'", ex.Message, StringComparison.Ordinal);
            Assert.Contains("zero series impedance", ex.Message, StringComparison.Ordinal);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public void D083_the_JSON_loader_refuses_a_zero_system_base()
    {
        string dir = JsonCase(
            buses: "[ { \"id\": 1, \"baseKv\": 132 }, { \"id\": 2, \"baseKv\": 132 } ]",
            lines: "[ { \"name\": \"L12\", \"from\": 1, \"to\": 2, \"rPu\": 0.01, \"xPu\": 0.1 } ]",
            system: "{ \"name\": \"t\", \"baseMva\": 0, \"frequencyHz\": 50 }");
        try
        {
            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => JsonGridLoader.LoadDirectory(dir));
            Assert.Contains("BaseMva 0", ex.Message, StringComparison.Ordinal);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public void D083_the_MATPOWER_parser_refuses_a_self_loop()
    {
        const string s = @"function mpc = loop
mpc.baseMVA = 100;
mpc.bus = [ 1 3 0 0 0 0 1 1 0 138 1 1.1 0.9; 2 1 10 0 0 0 1 1 0 138 1 1.1 0.9; ];
mpc.gen = [ 1 0 0 100 -100 1 100 1 100 0; ];
mpc.branch = [ 1 2 0.01 0.1 0 0 0 0 0 0 1 -360 360; 2 2 0.01 0.1 0 0 0 0 0 0 1 -360 360; ];";
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => MatpowerParser.Parse(s));
        Assert.Contains("connects bus 2 to itself", ex.Message, StringComparison.Ordinal);
    }

    // ---- D-DOC-082: an island with no slack ---------------------------------------------------------------------------

    /// <summary>Slack island {1, 2} and a second island {3, 4} (joined by a line of its own) with no slack.</summary>
    private static GridModel TwoIslandsOneSlack(bool secondSlack = false) => Model(
        new[] { B(1, BusType.Slack), B(2), B(3, secondSlack ? BusType.Slack : BusType.Pq), B(4) },
        new[] { new Branch(1, 2, 0.01, 0.1), new Branch(3, 4, 0.01, 0.1) },
        new[] { new Generator(1, Pg: 10) });

    [Fact]
    public void D082_an_island_with_no_slack_is_an_error_naming_its_size_and_buses()
    {
        IReadOnlyList<string> e = Errors(TwoIslandsOneSlack());
        Assert.Single(e);
        Assert.Contains("An island of 2 bus(es) (bus 3, 4) has no slack (reference) bus", e[0], StringComparison.Ordinal);
    }

    [Fact]
    public void D082_one_slack_per_island_is_the_multi_island_warning_and_no_error()
    {
        GridModel m = TwoIslandsOneSlack(secondSlack: true);
        Assert.Empty(Errors(m));
        Assert.Contains(Warnings(m), w => w.Contains("2 slack (reference) buses", StringComparison.Ordinal));
    }

    [Fact]
    public void D082_a_bus_cut_off_by_an_out_of_service_branch_is_an_island_without_a_slack()
    {
        // The solver's view: only in-service branches join buses.
        GridModel m = Model(new[] { B(1, BusType.Slack), B(2), B(3) },
            new[] { new Branch(1, 2, 0.01, 0.1), new Branch(2, 3, 0.01, 0.1, InService: false) },
            new[] { new Generator(1, Pg: 10) });
        Assert.Contains(Errors(m), x => x.Contains("An island of 1 bus(es) (bus 3)", StringComparison.Ordinal));
    }

    [Fact]
    public void D082_a_big_island_lists_its_first_five_buses_and_islands_come_in_lowest_id_order()
    {
        var buses = new List<Bus> { B(1, BusType.Slack), B(2) };
        for (int id = 10; id <= 16; id++) buses.Add(B(id));
        buses.Add(B(5));
        var lines = new List<Branch> { new(1, 2, 0.01, 0.1) };
        for (int id = 10; id < 16; id++) lines.Add(new Branch(id, id + 1, 0.01, 0.1));   // island 10..16 (7 buses)
        IReadOnlyList<string> e = Errors(Model(buses, lines, new[] { new Generator(1, Pg: 10) }));
        Assert.Equal(2, e.Count);
        Assert.Contains("An island of 1 bus(es) (bus 5)", e[0], StringComparison.Ordinal);           // lowest id first
        Assert.Contains("An island of 7 bus(es) (bus 10, 11, 12, 13, 14, ...)", e[1], StringComparison.Ordinal);
    }

    [Fact]
    public void D082_the_island_check_is_not_reported_on_top_of_a_missing_slack()
    {
        // No slack at all is its own error; the islands are not listed again.
        GridModel m = Model(new[] { B(1), B(2) }, Array.Empty<Branch>(), Array.Empty<Generator>());
        IReadOnlyList<string> e = Errors(m);
        Assert.Single(e);
        Assert.Contains("No slack", e[0], StringComparison.Ordinal);
    }

    [Fact]
    public void D082_a_multi_island_JSON_case_with_one_slack_is_refused_at_load()
    {
        // The reproduction: two islands, a slack generator only in the first. It used to load and solve with exit 0.
        string dir = JsonCase(
            buses: "[ { \"id\": 1, \"baseKv\": 132 }, { \"id\": 2, \"baseKv\": 132 }, { \"id\": 3, \"baseKv\": 132 }, { \"id\": 4, \"baseKv\": 132 } ]",
            lines: "[ { \"name\": \"L12\", \"from\": 1, \"to\": 2, \"rPu\": 0.01, \"xPu\": 0.1 }, { \"name\": \"L34\", \"from\": 3, \"to\": 4, \"rPu\": 0.01, \"xPu\": 0.1 } ]");
        try
        {
            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => JsonGridLoader.LoadDirectory(dir));
            Assert.Contains("An island of 2 bus(es) (bus 3, 4) has no slack", ex.Message, StringComparison.Ordinal);
        }
        finally { Delete(dir); }
    }

    // ---- D-DOC-082 / 083: no bundled case is affected (the proof the triage asked for) ------------------------------

    public static IEnumerable<object[]> BundledJsonCases() =>
        Directory.EnumerateFiles(Path.Combine(CliProcess.Root(), "data"), "system.json", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Path.Combine(CliProcess.Root(), "data"), Path.GetDirectoryName(f)!))
            .OrderBy(x => x, StringComparer.Ordinal)
            .Select(x => new object[] { x });

    public static IEnumerable<object[]> BundledMatpowerCases() =>
        Directory.EnumerateFiles(Path.Combine(CliProcess.Root(), "data"), "*.m", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Path.Combine(CliProcess.Root(), "data"), f))
            .OrderBy(x => x, StringComparer.Ordinal)
            .Select(x => new object[] { x });

    [Theory]
    [MemberData(nameof(BundledJsonCases))]
    public void D082_D083_every_bundled_JSON_case_validates_with_no_error(string relative)
    {
        GridModel m = JsonGridLoader.LoadDirectory(Path.Combine(CliProcess.Root(), "data", relative));   // throws on any error
        Assert.Empty(Errors(m));
    }

    [Theory]
    [MemberData(nameof(BundledMatpowerCases))]
    public void D082_D083_every_bundled_MATPOWER_case_validates_with_no_error(string relative)
    {
        GridModel m = MatpowerParser.ParseFile(Path.Combine(CliProcess.Root(), "data", relative));   // throws on any error
        Assert.Empty(Errors(m));
    }

    [Fact]
    public void D082_D083_the_bundled_PyPSA_GB_reduced_network_validates_with_no_error()
    {
        string data = Path.Combine(CliProcess.Root(), "data");
        GridModel op = MatpowerParser.ParseFile(Path.Combine(data, Core.IO.PyPsa.GbReferenceModels.Gb29RelativePath));
        GridModel m = Core.IO.PyPsa.PypsaGbReduced.Load(Path.Combine(data, Core.IO.PyPsa.GbReferenceModels.PypsaReducedRelativeDir), op).Model;
        Assert.Empty(Errors(m));
    }

    [Fact]
    public void D082_D083_every_built_in_IEEE_case_validates_with_no_error()
    {
        foreach (GridModel m in new[] { IeeeCases.Case9(), IeeeCases.Case14(), IeeeCases.Case30(), IeeeCases.Case57(),
                     IeeeCases.Case118(), IeeeCases.Case300(), IeeeCases.Case145(), IeeeCases.Case24Rts(), IeeeCases.CigreMv() })
            Assert.True(Errors(m).Count == 0, $"{m.Name}: {string.Join("; ", Errors(m))}");
    }

    // ---- D-DOC-208: the Validate checks that had no test ----------------------------------------------------------------

    [Fact]
    public void D208_a_dangling_To_end_is_an_error()
    {
        GridModel m = Model(new[] { B(1, BusType.Slack) }, new[] { new Branch(1, 9, 0.01, 0.1, Name: "Out") }, new[] { new Generator(1, Pg: 1) });
        Assert.Contains(Errors(m), x => x.Contains("Branch 'Out' To references nonexistent bus 9", StringComparison.Ordinal));
    }

    [Fact]
    public void D208_an_out_of_service_generator_on_a_nonexistent_bus_is_an_error()
    {
        GridModel m = Model(new[] { B(1, BusType.Slack), B(2) }, new[] { new Branch(1, 2, 0.01, 0.1) },
            new[] { new Generator(1, Pg: 1), new Generator(7, Pg: 0, Name: "Mothballed", InService: false) });
        Assert.Contains(Errors(m), x => x.Contains("Out-of-service generator 'Mothballed' references nonexistent bus 7", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0.0, null, "VminPu 0 is not a finite voltage above 0 pu")]
    [InlineData(double.NaN, null, "VminPu NaN is not a finite voltage above 0 pu")]
    [InlineData(null, -1.0, "VmaxPu -1 is not a finite voltage above 0 pu")]
    [InlineData(1.1, 0.9, "VminPu 1.1 is above its VmaxPu 0.9")]
    public void D208_unusable_per_bus_voltage_limits_are_errors(double? vmin, double? vmax, string fragment)
    {
        GridModel m = Model(new[] { B(1, BusType.Slack), new Bus(2, BusType.Pq, BaseKv: 132, VminPu: vmin, VmaxPu: vmax) },
            new[] { new Branch(1, 2, 0.01, 0.1) }, new[] { new Generator(1, Pg: 1) });
        Assert.Contains(Errors(m), x => x.Contains("Bus 2 " + fragment, StringComparison.Ordinal));
    }

    [Fact]
    public void D208_equal_per_bus_limits_are_a_fixed_voltage_bus_and_clean()
    {
        GridModel m = Model(new[] { new Bus(1, BusType.Slack, BaseKv: 132, VminPu: 1.0, VmaxPu: 1.0), B(2) },
            new[] { new Branch(1, 2, 0.01, 0.1) }, new[] { new Generator(1, Pg: 1) });
        Assert.Empty(m.Validate());
    }

    [Fact]
    public void D208_an_oltc_controlling_a_nonexistent_bus_warns()
    {
        GridModel m = Model(new[] { B(1, BusType.Slack, 400), B(2, BusType.Pq, 132) },
            new[] { new Branch(1, 2, 0.001, 0.1, Name: "T1", IsTransformer: true) }, new[] { new Generator(1, Pg: 1) },
            new[] { new Oltc("T1", 99) });
        Assert.Contains(Warnings(m), w => w.Contains("OLTC on transformer 'T1' controls nonexistent bus 99", StringComparison.Ordinal));
        Assert.Empty(Errors(m));
    }

    [Fact]
    public void D208_an_oltc_controlling_a_bus_off_its_transformer_warns()
    {
        GridModel m = Model(new[] { B(1, BusType.Slack, 400), B(2, BusType.Pq, 132), B(3, BusType.Pq, 132) },
            new[] { new Branch(1, 2, 0.001, 0.1, Name: "T1", IsTransformer: true), new Branch(2, 3, 0.01, 0.1) },
            new[] { new Generator(1, Pg: 1) }, new[] { new Oltc("T1", 3) });
        Assert.Contains(Warnings(m), w => w.Contains("controls bus 3, which is not an endpoint of the transformer (LV side is bus 2)", StringComparison.Ordinal));
    }

    [Fact]
    public void GR505_an_oltc_is_judged_against_the_same_named_transformer_the_loop_drives()
    {
        // Two transformers named "T": the record controls bus 4, the LV end of the SECOND. Validate used to judge it
        // against the first "T" (buses 1-2) and warn "not an endpoint"; the OLTC loop drives the second one.
        GridModel m = Model(new[] { B(1, BusType.Slack, 400), B(2, BusType.Pq, 132), B(3, BusType.Pq, 400), B(4, BusType.Pq, 132) },
            new[]
            {
                new Branch(1, 2, 0.001, 0.1, Name: "T", IsTransformer: true), new Branch(1, 3, 0.001, 0.01),
                new Branch(3, 4, 0.001, 0.1, Name: "T", IsTransformer: true),
            },
            new[] { new Generator(1, Pg: 1) }, new[] { new Oltc("T", 4) });
        Assert.DoesNotContain(Warnings(m), w => w.Contains("OLTC on transformer 'T'", StringComparison.Ordinal));
    }

    [Fact]
    public void GR503_two_oltc_records_driving_one_transformer_warn()
    {
        // Both records control a bus off every "T1", so both fall back to the first "T1"; the loop steps it once per
        // pass (for the first record) and Validate says so.
        GridModel m = Model(new[] { B(1, BusType.Slack, 400), B(2, BusType.Pq, 132), B(3, BusType.Pq, 132), B(4, BusType.Pq, 132) },
            new[] { new Branch(1, 2, 0.001, 0.1, Name: "T1", IsTransformer: true), new Branch(2, 3, 0.01, 0.1), new Branch(2, 4, 0.01, 0.1) },
            new[] { new Generator(1, Pg: 1) }, new[] { new Oltc("T1", 3), new Oltc("T1", 4) });
        Assert.Contains(Warnings(m), w => w.Contains("OLTC records 0 and 1 (controlling buses 3 and 4) both drive transformer 'T1' (branch 0)", StringComparison.Ordinal));
    }

    [Fact]
    public void D208_an_oltc_on_a_transformer_of_equal_end_voltages_is_not_judged()
    {
        GridModel m = Model(new[] { B(1, BusType.Slack, 132), B(2, BusType.Pq, 132) },
            new[] { new Branch(1, 2, 0.001, 0.1, Name: "T1", IsTransformer: true) }, new[] { new Generator(1, Pg: 1) },
            new[] { new Oltc("T1", 1) });
        Assert.Empty(m.Validate());
    }

    [Fact]
    public void D208_a_boundary_naming_no_branch_is_an_error()
    {
        GridModel m = TwoBus();
        GridModel withBoundary = new()
        {
            BaseMva = m.BaseMva, Buses = m.Buses, Generators = m.Generators, Branches = m.Branches,
            Boundaries = new[] { new Boundary("B6", 100, new[] { new BoundaryBranch("Nope") }) },
        };
        Assert.Contains(Errors(withBoundary), x => x.Contains("Boundary 'B6' names branch 'Nope', which matches no branch", StringComparison.Ordinal));
    }

    [Fact]
    public void D208_every_finite_input_check_names_its_field()
    {
        GridModel m = Model(
            new[] { B(1, BusType.Slack), new Bus(2, BusType.Pq, Qd: double.NaN, Gs: double.PositiveInfinity, Bs: double.NaN, BaseKv: 132) },
            new[] { new Branch(1, 2, double.NaN, 0.1, B: double.NaN, TapRatio: double.NaN, PhaseShiftDeg: double.NaN, Name: "L") },
            new[] { new Generator(1, Pg: 1, Qg: double.NaN, Vg: double.NaN, Name: "G") });
        IReadOnlyList<string> e = Errors(m);
        foreach (string field in new[] { "Bus 2 Qd", "Bus 2 Gs", "Bus 2 Bs", "Branch 'L' R", "Branch 'L' B", "Branch 'L' TapRatio",
                     "Branch 'L' PhaseShiftDeg", "Generator 'G' Qg", "Generator 'G' Vg" })
            Assert.Contains(e, x => x.StartsWith(field + " is not a finite number", StringComparison.Ordinal));
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    /// <summary>A minimal v1 JSON case folder: a slack generator on bus 1 and whatever buses and lines are given.</summary>
    private static string JsonCase(string buses, string lines, string? system = null)
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-doc13-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "system.json"), system ?? "{ \"name\": \"t\", \"baseMva\": 100, \"frequencyHz\": 50 }");
        File.WriteAllText(Path.Combine(dir, "buses.json"), buses);
        File.WriteAllText(Path.Combine(dir, "generation.json"),
            "[ { \"name\": \"G1\", \"bus\": 1, \"type\": \"gas\", \"capacityMva\": 100, \"role\": \"slack\" } ]");
        File.WriteAllText(Path.Combine(dir, "transmission.json"), lines);
        return dir;
    }

    private static void Delete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
