using GridSim.Core.Analysis;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Security-constrained unit commitment (<see cref="UnitCommitment"/>): the three-unit four-period instance against full
/// enumeration, minimum up/down times and spinning reserve against an enumerating reference, a congested line and a
/// secured outage against hand calculation, a ramp limit, the system price, every whole on/off value, bit-for-bit
/// repeatability and the refusals.
/// </summary>
public class UnitCommitmentTests
{
    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

    /// <summary>Costs carry the interior point solve's relative accuracy (1e-9); see MilpBranchAndBoundTests.</summary>
    private static void Close(double expected, double actual) =>
        Assert.True(Math.Abs(expected - actual) <= 1e-9 * (1 + Math.Abs(expected)), $"expected {expected:R}, got {actual:R}");

    // The three units of the instance (also in MilpBranchAndBoundTests): Pmin, Pmax, cost/MWh, no-load/h, start-up.
    private static readonly (double Pmin, double Pmax, double Mc, double Nl, double Su)[] Fleet =
        { (50, 200, 10, 100, 500), (40, 150, 20, 50, 200), (20, 100, 35, 20, 50) };

    /// <summary>One bus carrying 100 MW of load and the three units (no branch: a single connected bus).</summary>
    private static GridModel CopperPlate() => new()
    {
        Name = "uc-copper-plate",
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, Pd: 100) },
        Generators = Fleet.Select((f, i) => new Generator(1, 0, Pmin: f.Pmin, Pmax: f.Pmax, Name: "ABC"[i].ToString())).ToArray(),
        Branches = Array.Empty<Branch>(),
    };

    private static UcUnitData[] FleetData(int minUpC = 1, int minDownB = 1) => Fleet.Select((f, i) => new UcUnitData
    {
        MarginalCost = f.Mc, NoLoadCost = f.Nl, StartupCost = f.Su, InitiallyOn = false,
        MinUpPeriods = i == 2 ? minUpC : 1, MinDownPeriods = i == 1 ? minDownB : 1,
    }).ToArray();

    private static readonly double[] Scale = { 1.5, 3.0, 4.5, 2.5 };

    [Fact]
    public void The_three_unit_instance_matches_enumeration_at_18320()
    {
        UcResult r = UnitCommitment.Solve(CopperPlate(), new UcOptions { Periods = 4, DemandScale = Scale, Units = FleetData() });
        Assert.True(r.Optimal, r.Message);
        Close(18320.0, r.TotalCost);
        Assert.Equal(new[] { true, true, true, true }, r.Units[0].On);
        Assert.Equal(new[] { false, true, true, true }, r.Units[1].On);
        Assert.Equal(new[] { false, false, true, false }, r.Units[2].On);
        Assert.Equal(500 + 200 + 50, r.StartupCost, 9);
        // Every period balances, and an off unit produces nothing.
        foreach (UcPeriod p in r.Periods) Assert.Equal(p.DemandMw, p.GenerationMw, 6);
        foreach (UcUnitSchedule s in r.Units)
            for (int t = 0; t < 4; t++) if (!s.On[t]) Assert.Equal(0.0, s.OutputMw[t]);
        // Period 1 (150 MW): only A runs, inside its limits, so the system price is A's marginal cost.
        Assert.Equal(10.0, r.Periods[0].SystemPrice, 6);
    }

    [Theory]
    [InlineData(1, 1, 0.0)]
    [InlineData(3, 1, 0.0)]
    [InlineData(1, 3, 0.0)]
    [InlineData(3, 3, 0.0)]
    [InlineData(1, 1, 60.0)]
    [InlineData(3, 2, 80.0)]
    public void Minimum_times_and_reserve_match_an_enumerating_reference(int minUpC, int minDownB, double reserveMw)
    {
        double[] reserve = Enumerable.Repeat(reserveMw, 4).ToArray();
        UcResult r = UnitCommitment.Solve(CopperPlate(), new UcOptions
        {
            Periods = 4, DemandScale = Scale, ReserveMw = reserve, Units = FleetData(minUpC, minDownB),
        });
        double reference = Enumerate(Scale.Select(s => 100 * s).ToArray(), reserve, minUpC, minDownB);
        if (double.IsPositiveInfinity(reference))
        {
            Assert.False(r.Optimal, "the reference finds no feasible schedule");
            return;
        }
        Assert.True(r.Optimal, r.Message);
        Close(reference, r.TotalCost);
        for (int t = 0; t < 4; t++) Assert.True(r.Periods[t].HeadroomMw >= reserveMw - 1e-6, $"period {t + 1} headroom {r.Periods[t].HeadroomMw}");
    }

    /// <summary>Every on/off schedule of the three units (all off before period 1), checked against minimum up time of C
    /// and minimum down time of B (a run or gap cut short by the horizon's end is allowed, as in the formulation), the
    /// balance and the reserve; each feasible schedule dispatched by merit order above the minimums.</summary>
    private static double Enumerate(double[] demand, double[] reserve, int minUpC, int minDownB)
    {
        int T = demand.Length;
        double best = double.PositiveInfinity;
        for (int code = 0; code < 1 << (3 * T); code++)
        {
            bool On(int g, int t) => t < 0 ? false : ((code >> (g * T + t)) & 1) == 1;
            bool ok = true;
            for (int t = 0; t < T && ok; t++)
            {
                // C started at t must stay on for minUpC periods (within the horizon).
                if (On(2, t) && !On(2, t - 1))
                    for (int q = t; q < Math.Min(T, t + minUpC); q++) if (!On(2, q)) ok = false;
                // B stopped at t (on at t-1, off at t) must stay off for minDownB periods. B is off before period 1, but
                // that initial state counts as long satisfied, so only stops inside the horizon count.
                if (t > 0 && !On(1, t) && On(1, t - 1))
                    for (int q = t; q < Math.Min(T, t + minDownB); q++) if (On(1, q)) ok = false;
            }
            if (!ok) continue;
            double cost = 0;
            for (int t = 0; t < T && ok; t++)
            {
                double mn = 0, mx = 0;
                for (int g = 0; g < 3; g++) if (On(g, t)) { mn += Fleet[g].Pmin; mx += Fleet[g].Pmax; }
                if (demand[t] < mn - 1e-9 || demand[t] > mx + 1e-9 || mx - demand[t] < reserve[t] - 1e-9) { ok = false; break; }
                double rem = demand[t] - mn;
                foreach (int g in new[] { 0, 1, 2 }.OrderBy(g => Fleet[g].Mc))
                {
                    if (!On(g, t)) continue;
                    double add = Math.Min(rem, Fleet[g].Pmax - Fleet[g].Pmin);
                    rem -= add;
                    cost += Fleet[g].Mc * (Fleet[g].Pmin + add) + Fleet[g].Nl;
                }
                for (int g = 0; g < 3; g++) if (On(g, t) && !On(g, t - 1)) cost += Fleet[g].Su;
            }
            if (ok) best = Math.Min(best, cost);
        }
        return best;
    }

    /// <summary>Two buses joined by <paramref name="lines"/> identical lines (each rated <paramref name="rate"/> MVA): a
    /// cheap unit (10 per MWh) at bus 1, a dear one (30) at bus 2, the load at bus 2.</summary>
    private static GridModel TwoBus(double loadMw, int lines, double rate) => new()
    {
        Name = "uc-two-bus",
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pv, Pd: loadMw) },
        Generators = new[]
        {
            new Generator(1, 0, Pmin: 0, Pmax: 500, Name: "cheap"),
            new Generator(2, 0, Pmin: 0, Pmax: 500, Name: "dear"),
        },
        Branches = Enumerable.Range(0, lines).Select(i => new Branch(1, 2, 0.0, 0.1, RateAMva: rate, Name: $"L{i + 1}")).ToArray(),
    };

    private static readonly UcUnitData[] CheapDear =
    {
        new() { MarginalCost = 10 },
        new() { MarginalCost = 30 },
    };

    [Fact]
    public void A_congested_line_limits_the_cheap_unit_to_its_rating()
    {
        // 250 MW at bus 2, one 100 MVA line: cheap 100 MW (the line full), dear 150 MW; cost 100 x 10 + 150 x 30 = 5,500.
        UcResult r = UnitCommitment.Solve(TwoBus(250, 1, 100), new UcOptions { Periods = 1, Units = CheapDear });
        Assert.True(r.Optimal, r.Message);
        Assert.Equal(100.0, r.Units[0].OutputMw[0], 6);
        Assert.Equal(150.0, r.Units[1].OutputMw[0], 6);
        Close(5500.0, r.TotalCost);
        UcBinding b = Assert.Single(r.Bindings);
        Assert.Equal(-1, b.Contingency);
        Assert.Equal("L1", b.Name);
        // Without the limit the cheap unit carries everything.
        UcResult free = UnitCommitment.Solve(TwoBus(250, 1, 100), new UcOptions { Periods = 1, Units = CheapDear, EnforceLineLimits = false });
        Assert.Equal(250.0, free.Units[0].OutputMw[0], 6);
    }

    [Fact]
    public void A_secured_outage_of_one_of_two_parallel_lines_holds_the_transfer_to_one_rating()
    {
        // 150 MW at bus 2, two identical 100 MVA lines. Intact, the cheap unit can send all 150 (75 per line). With the
        // outage of L1 secured, L2 alone must carry the transfer (LODF = 1), so the cheap unit stops at 100 MW.
        UcResult intact = UnitCommitment.Solve(TwoBus(150, 2, 100), new UcOptions { Periods = 1, Units = CheapDear });
        Assert.True(intact.Optimal, intact.Message);
        Assert.Equal(150.0, intact.Units[0].OutputMw[0], 6);
        UcResult secured = UnitCommitment.Solve(TwoBus(150, 2, 100), new UcOptions { Periods = 1, Units = CheapDear, Contingencies = new[] { 0 } });
        Assert.True(secured.Optimal, secured.Message);
        Assert.Equal(100.0, secured.Units[0].OutputMw[0], 6);
        Assert.Equal(50.0, secured.Units[1].OutputMw[0], 6);
        UcBinding b = Assert.Single(secured.Bindings);
        Assert.Equal(0, b.Contingency);
        Assert.Equal("L2", b.Name);
        Assert.Equal(100.0, Math.Abs(b.FlowMw), 6);
    }

    [Fact]
    public void A_ramp_limit_holds_the_cheap_unit_back_between_periods()
    {
        // Load 100 then 300 MW, no line limit. The cheap unit starts on at 100 MW and may rise 100 MW per period: 200 MW
        // in period 2, the dear unit the other 100. Without the ramp the cheap unit takes all 300.
        var data = new[]
        {
            new UcUnitData { MarginalCost = 10, RampUpMw = 100, InitialMw = 100 },
            new UcUnitData { MarginalCost = 30, InitiallyOn = false },
        };
        UcResult r = UnitCommitment.Solve(TwoBus(100, 1, 0), new UcOptions { Periods = 2, DemandScale = new[] { 1.0, 3.0 }, Units = data });
        Assert.True(r.Optimal, r.Message);
        Assert.Equal(100.0, r.Units[0].OutputMw[0], 6);
        Assert.Equal(200.0, r.Units[0].OutputMw[1], 6);
        Assert.Equal(100.0, r.Units[1].OutputMw[1], 6);
        UcResult free = UnitCommitment.Solve(TwoBus(100, 1, 0), new UcOptions
        {
            Periods = 2, DemandScale = new[] { 1.0, 3.0 }, Units = new[] { new UcUnitData { MarginalCost = 10 }, new UcUnitData { MarginalCost = 30 } },
        });
        Assert.Equal(300.0, free.Units[0].OutputMw[1], 6);
    }

    [Fact]
    public void The_case_cost_curve_and_start_up_cost_are_used_when_no_unit_data_is_given()
    {
        // MATPOWER case9: quadratic gencost, start-up 1500 / 2000 / 3000, all units initially on (the default), so nothing
        // starts; one period at the case's own load (315 MW).
        GridModel m = S10TestSupport.LoadMatpower("lib/case9.m").Model;
        UcResult r = UnitCommitment.Solve(m, new UcOptions { Periods = 1, EnforceLineLimits = false });
        Assert.True(r.Optimal, r.Message);
        Assert.Equal(0.0, r.StartupCost);
        Assert.Equal(315.0, r.Periods[0].GenerationMw, 6);
        Assert.Contains(r.Notes, n => n.Contains("quadratic cost cut into 4 chord segment(s)"));
    }

    [Fact]
    public void The_commitment_is_bit_identical_from_run_to_run()
    {
        var o = new UcOptions { Periods = 4, DemandScale = Scale, ReserveMw = new double[] { 50, 50, 0, 50 }, Units = FleetData(2, 2) };
        UcResult r1 = UnitCommitment.Solve(CopperPlate(), o);
        UcResult r2 = UnitCommitment.Solve(CopperPlate(), o);
        Assert.True(r1.Optimal, r1.Message);
        Assert.Equal(r1.Nodes, r2.Nodes);
        Assert.Equal(Bits(r1.TotalCost), Bits(r2.TotalCost));
        for (int g = 0; g < 3; g++) Assert.Equal(r1.Units[g].OutputMw.Select(Bits), r2.Units[g].OutputMw.Select(Bits));
    }

    [Fact]
    public void Input_it_cannot_build_exactly_is_refused()
    {
        GridModel cp = CopperPlate();
        Assert.Throws<ArgumentOutOfRangeException>(() => UnitCommitment.Solve(cp, new UcOptions { Periods = 0 }));
        Assert.Throws<ArgumentException>(() => UnitCommitment.Solve(cp, new UcOptions { Periods = 2, DemandScale = new[] { 1.0 } }));
        Assert.Throws<ArgumentException>(() => UnitCommitment.Solve(cp, new UcOptions { Periods = 1, Units = new UcUnitData[2] }));
        var noPmax = new GridModel
        {
            BaseMva = 100, Buses = new[] { new Bus(1, BusType.Slack, Pd: 10) },
            Generators = new[] { new Generator(1, 0) }, Branches = Array.Empty<Branch>(),
        };
        ArgumentException e = Assert.Throws<ArgumentException>(() => UnitCommitment.Solve(noPmax, new UcOptions { Periods = 1 }));
        Assert.Contains("no finite Pmax", e.Message);
        var islands = new GridModel
        {
            BaseMva = 100, Buses = new[] { new Bus(1, BusType.Slack, Pd: 10), new Bus(2, BusType.Pq, Pd: 10) },
            Generators = new[] { new Generator(1, 0, Pmax: 50) }, Branches = Array.Empty<Branch>(),
        };
        Assert.Contains("DC islands", Assert.Throws<ArgumentException>(() => UnitCommitment.Solve(islands, new UcOptions { Periods = 1 })).Message);
        // The outage of the only line islands bus 2.
        Assert.Contains("islands the network", Assert.Throws<ArgumentException>(() =>
            UnitCommitment.Solve(TwoBus(50, 1, 100), new UcOptions { Periods = 1, Units = CheapDear, Contingencies = new[] { 0 } })).Message);
        Assert.Contains("not in the case", Assert.Throws<ArgumentException>(() =>
            UnitCommitment.Solve(TwoBus(50, 1, 100), new UcOptions { Periods = 1, Units = CheapDear, Contingencies = new[] { 7 } })).Message);
    }

    [Fact]
    public void The_data_file_builds_the_same_commitment_as_the_options()
    {
        const string json = """
            { "format": "gridsim-uc/1", "periods": 4, "demandScale": [1.5, 3.0, 4.5, 2.5],
              "units": [ { "unit": 0, "marginalCost": 10, "noLoadCost": 100, "startupCost": 500, "initiallyOn": false },
                         { "name": "B", "marginalCost": 20, "noLoadCost": 50, "startupCost": 200, "initiallyOn": false },
                         { "name": "C", "marginalCost": 35, "noLoadCost": 20, "startupCost": 50, "initiallyOn": false } ] }
            """;
        UcOptions o = UnitCommitmentFile.Read(json, "test.json", CopperPlate());
        UcResult r = UnitCommitment.Solve(CopperPlate(), o);
        Assert.True(r.Optimal, r.Message);
        Close(18320.0, r.TotalCost);
        // The contingency list by index and by name, and the period count from the command line.
        const string two = """{ "format": "gridsim-uc/1", "contingencies": [ "L2" ], "units": [ { "unit": 1, "minUp": 2 } ] }""";
        UcOptions t = UnitCommitmentFile.Read(two, "two.json", TwoBus(150, 2, 100), periodsOverride: 3);
        Assert.Equal(3, t.Periods);
        Assert.Equal(new[] { 1 }, t.Contingencies);
        Assert.Equal(2, t.Units![1]!.MinUpPeriods);
        Assert.Null(t.Units[0]);
    }

    [Theory]
    [InlineData("""{ "format": "gridsim-uc/2" }""", "\"format\" must be")]
    [InlineData("""{ "format": "gridsim-uc/1", "horizon": 4 }""", "unknown key 'horizon'")]
    [InlineData("""{ "format": "gridsim-uc/1", "periods": 2, "demandScale": [1] }""", "has 1 value(s) for 2 period(s)")]
    [InlineData("""{ "format": "gridsim-uc/1", "units": [ { "unit": 0 }, { "name": "A" } ] }""", "given more than once")]
    [InlineData("""{ "format": "gridsim-uc/1", "units": [ { "name": "Z" } ] }""", "0 in-service generators are named 'Z'")]
    [InlineData("""{ "format": "gridsim-uc/1", "units": [ { "unit": 0, "name": "A" } ] }""", "exactly one of")]
    [InlineData("""{ "format": "gridsim-uc/1", "units": [ { "unit": 0, "minUp": 1.5 } ] }""", "\"minUp\" must be a whole number")]
    [InlineData("""{ "format": "gridsim-uc/1", "contingencies": [ 9 ] }""", "branch 9 is not in the case")]
    [InlineData("""not json""", "not valid JSON")]
    public void A_malformed_data_file_is_refused_naming_the_problem(string json, string expected)
    {
        InvalidDataException e = Assert.Throws<InvalidDataException>(() => UnitCommitmentFile.Read(json, "bad.json", CopperPlate()));
        Assert.Contains(expected, e.Message);
        Assert.Contains("bad.json", e.Message);
    }
}
