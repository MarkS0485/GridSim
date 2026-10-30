using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F17 (October 2026 release gap): per-bus Vmin / Vmax from JSON (vminPu / vmaxPu) and MATPOWER bus columns 12-13
/// (VMAX, VMIN). The limits are always READ into the model; a check USES them only when asked
/// (<see cref="PowerFlowOptions.PerBusVoltageLimits"/>, the CLI --bus-vlimits flag), because every MATPOWER file
/// carries the columns and using them unasked would move existing contingency verdicts. These tests prove the
/// default is unchanged on the bundled cases and that the opt-in judges each bus against its own limits.
/// </summary>
public class PerBusVoltageLimitTests
{
    private static GridModel StripLimits(GridModel m) =>
        m.CopyWith(buses: m.Buses.Select(b => b with { VminPu = null, VmaxPu = null }).ToList());

    private static void AssertSameScreen(string label, ContingencyResult a, ContingencyResult b)
    {
        Assert.True(a.Converged == b.Converged, $"{label}: converged");
        Assert.Equal(a.Violations, b.Violations);
        Assert.Equal(a.Details, b.Details);
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.MinVm), BitConverter.DoubleToInt64Bits(b.MinVm));
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.MaxVm), BitConverter.DoubleToInt64Bits(b.MaxVm));
    }

    [Fact]
    public void Matpower_bus_columns_12_and_13_are_read_as_vmax_and_vmin()
    {
        GridModel c118 = IeeeCases.Case118();
        Assert.All(c118.Buses, b => { Assert.Equal(0.94, b.VminPu); Assert.Equal(1.06, b.VmaxPu); });

        // case30 mixes 0.95-1.05 and 0.95-1.1 buses; the feeder case33bw gives its slack 1.0 / 1.0 (a fixed voltage).
        GridModel c30 = IeeeCases.Case30();
        Assert.Contains(c30.Buses, b => b.VmaxPu == 1.1);
        Assert.Contains(c30.Buses, b => b.VmaxPu == 1.05);
        GridModel bw = S10TestSupport.LoadMatpower("dist/case33bw.m").Model;
        Bus slack = bw.Buses.Single(b => b.Type == BusType.Slack);
        Assert.Equal(1.0, slack.VminPu);
        Assert.Equal(1.0, slack.VmaxPu);
    }

    [Fact]
    public void A_short_or_unusable_limit_pair_leaves_the_bus_on_the_global_band_with_a_note()
    {
        const string text = @"function mpc = lim
mpc.baseMVA = 100;
mpc.bus = [ 1 3 0 0 0 0 1 1 0 138 1 1.1 0.9; 2 1 10 0 0 0 1 1 0 138 1 0.9 1.1; 3 1 5 0 0 0 1 1 0 138; ];
mpc.gen = [ 1 0 0 100 -100 1 100 1 100 0; ];
mpc.branch = [ 1 2 0 0.1 0 0 0 0 0 0 1 -360 360; 2 3 0 0.1 0 0 0 0 0 0 1 -360 360; ];";
        MatpowerParseResult r = MatpowerParser.ParseWithDiagnostics(text, frequencyHz: 50);
        Assert.Equal(0.9, r.Model.Buses[0].VminPu);
        Assert.Null(r.Model.Buses[1].VminPu);   // VMIN 1.1 above VMAX 0.9: not usable
        Assert.Null(r.Model.Buses[2].VmaxPu);   // row too short to carry the columns: absent, no note
        Assert.Contains(r.Notes, n => n.StartsWith("1 bus(es) carry VMAX/VMIN columns that are not usable", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(S10TestSupport.BundledMatpowerData), MemberType = typeof(S10TestSupport))]
    public void By_default_the_base_case_screen_is_unchanged_by_the_per_bus_data(string rel)
    {
        // The base-case screen of every bundled case, with the study options and without any, gives exactly the
        // verdict of the same model with every per-bus limit removed: the data alone changes nothing.
        if (rel.Contains("9241", StringComparison.Ordinal)) return;   // the 9,241-bus case is covered by the parity suites; keep this sweep quick
        GridModel m = S10TestSupport.LoadMatpower(rel).Model;
        GridModel bare = StripLimits(m);
        foreach (PowerFlowOptions? opt in new[] { null, ContingencyAnalysis.StudyOptions })
            AssertSameScreen($"{rel} [{(opt is null ? "defaults" : "study")}]",
                ContingencyAnalysis.ScreenBaseCase(m, null, opt), ContingencyAnalysis.ScreenBaseCase(bare, null, opt));
    }

    [Theory]
    [InlineData("14")]
    [InlineData("30")]
    [InlineData("118")]
    public void By_default_the_n_minus_1_screen_is_unchanged_by_the_per_bus_data(string name)
    {
        GridModel m = name switch { "14" => IeeeCases.Case14(), "30" => IeeeCases.Case30(), _ => IeeeCases.Case118() };
        GridModel bare = StripLimits(m);
        IReadOnlyList<ContingencyResult> a = ContingencyAnalysis.ScreenBranches(m, null, ContingencyAnalysis.StudyOptions);
        IReadOnlyList<ContingencyResult> b = ContingencyAnalysis.ScreenBranches(bare, null, ContingencyAnalysis.StudyOptions);
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++) AssertSameScreen($"case{name} outage {i}", a[i], b[i]);
    }

    // A two-bus case solved to a known voltage: bus 2 floats below 1.0 under its load.
    private static GridModel TwoBus(double? vmin2, double? vmax2) => new()
    {
        Name = "two", BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, Vm: 1.0), new Bus(2, BusType.Pq, Pd: 60, Qd: 20, VminPu: vmin2, VmaxPu: vmax2) },
        Generators = new[] { new Generator(1, 0) },
        Branches = new[] { new Branch(1, 2, 0.01, 0.1) },
    };

    [Fact]
    public void The_opt_in_judges_each_bus_against_its_own_limits()
    {
        var perBus = ContingencyAnalysis.StudyOptions with { PerBusVoltageLimits = true };

        // Solve once to learn bus 2's voltage, then put its own lower limit just above and just below it.
        ContingencyResult probe = ContingencyAnalysis.ScreenBaseCase(TwoBus(null, null), null, perBus);
        Assert.True(probe.Converged);
        double v2 = probe.MinVm;
        Assert.True(v2 > VoltageLimits.OperationalLowPu && v2 < 1.0, $"probe voltage {v2:R} should sit inside the band");
        Assert.Empty(probe.Violations);

        ContingencyResult tight = ContingencyAnalysis.ScreenBaseCase(TwoBus(v2 + 0.001, null), null, perBus);
        Assert.Equal(new[] { $"undervoltage {v2:F3} p.u." }, tight.Violations);
        ContingencyViolation d = Assert.Single(tight.Details);
        Assert.Equal(ContingencyViolationKind.Undervoltage, d.Kind);
        Assert.Equal(2, d.Element);

        // The same model without the opt-in: the band check, so no violation.
        Assert.Empty(ContingencyAnalysis.ScreenBaseCase(TwoBus(v2 + 0.001, null), null, ContingencyAnalysis.StudyOptions).Violations);

        // A bus whose own lower limit is looser than the band is NOT violated by a voltage under the band edge.
        ContingencyResult loose = ContingencyAnalysis.ScreenBaseCase(TwoBus(0.5, null), null, perBus);
        Assert.Empty(loose.Violations);

        // A tight upper limit below the solved voltage is an overvoltage.
        ContingencyResult high = ContingencyAnalysis.ScreenBaseCase(TwoBus(null, v2 - 0.001), null, perBus);
        Assert.Equal(new[] { $"overvoltage {v2:F3} p.u." }, high.Violations);
    }

    [Fact]
    public void The_opt_in_on_a_model_with_no_per_bus_data_is_the_band_check()
    {
        GridModel m = StripLimits(IeeeCases.Case118());
        var perBus = ContingencyAnalysis.StudyOptions with { PerBusVoltageLimits = true };
        IReadOnlyList<ContingencyResult> a = ContingencyAnalysis.ScreenBranches(m, null, perBus);
        IReadOnlyList<ContingencyResult> b = ContingencyAnalysis.ScreenBranches(m, null, ContingencyAnalysis.StudyOptions);
        for (int i = 0; i < a.Count; i++) AssertSameScreen($"case118 outage {i}", a[i], b[i]);
    }

    [Fact]
    public void AllWithinOperational_without_per_bus_is_the_historical_expression()
    {
        GridModel m = IeeeCases.Case118();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        bool historical = r.Vm.Min() >= VoltageLimits.OperationalLowPu && r.Vm.Max() <= VoltageLimits.OperationalHighPu;
        Assert.Equal(historical, VoltageLimits.AllWithinOperational(m, r.BusIds, r.Vm, perBus: false));
        // case118's own limits are exactly the operational band, so the per-bus answer agrees here too.
        Assert.Equal(historical, VoltageLimits.AllWithinOperational(m, r.BusIds, r.Vm, perBus: true));
    }

    [Fact]
    public void Validate_refuses_unusable_per_bus_limits()
    {
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, VminPu: 1.1, VmaxPu: 0.9), new Bus(2, BusType.Pq, VmaxPu: double.NaN) },
            Generators = new[] { new Generator(1, 0) },
            Branches = new[] { new Branch(1, 2, 0, 0.1) },
        };
        IReadOnlyList<GridDiagnostic> d = m.Validate();
        Assert.Contains(d, x => x.Message.Contains("Bus 1 VminPu 1.1 is above its VmaxPu 0.9"));
        Assert.Contains(d, x => x.Message.Contains("Bus 2 VmaxPu NaN is not a finite voltage above 0 pu"));
    }

    // ---- JSON ------------------------------------------------------------------------------------------------------

    private static string TempCase(string busesJson)
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-f17-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "system.json"), """{ "name": "f17", "baseMva": 100, "frequencyHz": 50 }""");
        File.WriteAllText(Path.Combine(dir, "buses.json"), busesJson);
        File.WriteAllText(Path.Combine(dir, "generation.json"), """[ { "name": "G", "bus": 1, "role": "slack", "capacityMva": 200 } ]""");
        File.WriteAllText(Path.Combine(dir, "transmission.json"), """[ { "name": "L", "from": 1, "to": 2, "rPu": 0.01, "xPu": 0.1 } ]""");
        return dir;
    }

    [Fact]
    public void Json_vminpu_and_vmaxpu_are_read_and_absent_means_none()
    {
        string dir = TempCase("""[ { "id": 1, "baseKv": 132 }, { "id": 2, "baseKv": 132, "vminPu": 0.97, "vmaxPu": 1.03 } ]""");
        try
        {
            GridModel m = JsonGridLoader.LoadDirectory(dir);
            Assert.Null(m.Buses[0].VminPu);
            Assert.Null(m.Buses[0].VmaxPu);
            Assert.Equal(0.97, m.Buses[1].VminPu);
            Assert.Equal(1.03, m.Buses[1].VmaxPu);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("""{ "id": 2, "baseKv": 132, "vminPu": 0 }""", "bus 2 vminPu 0")]
    [InlineData("""{ "id": 2, "baseKv": 132, "vmaxPu": -1 }""", "bus 2 vmaxPu -1")]
    [InlineData("""{ "id": 2, "baseKv": 132, "vminPu": 1.05, "vmaxPu": 0.95 }""", "bus 2 vminPu 1.05 above vmaxPu 0.95")]
    public void Json_unusable_limits_are_refused_naming_the_bus(string bus2, string expected)
    {
        string dir = TempCase($$"""[ { "id": 1, "baseKv": 132 }, {{bus2}} ]""");
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => JsonGridLoader.LoadDirectory(dir));
            Assert.Contains("buses.json", ex.Message);
            Assert.Contains(expected, ex.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_bundled_json_cases_carry_no_per_bus_limits()
    {
        foreach (string d in new[] { "uk", "gb-synthetic", "gb-full" })
            Assert.False(VoltageLimits.HasPerBusLimits(JsonGridLoader.LoadDirectory(Path.Combine(S10TestSupport.DataDir, d))), d);
    }
}
