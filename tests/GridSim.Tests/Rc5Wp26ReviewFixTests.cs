using GridSim.Core.Analysis;
using GridSim.Core.Analysis.Ras;
using GridSim.Core.Api;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP26 review fixes (2.1.134): closeBranch never recloses the contingency's own outage; a level where every action
/// was skipped ends the run (no false RAS LOOP); duplicate keys and out-of-range setTap / switchShunt values are refused;
/// and the cases the first tests did not reach: a trip of the reference unit (re-elected, and skipped when no unit can
/// take over), <c>logic: any</c>, arming on a unit outage and on a bus outage, and the final check under rating B.
/// <para>The same two-bus case and closed forms as <see cref="Rc5Wp26RasTests"/>: bus 1 (PV, V = 1) with G1 150 MW and
/// G1b 50 MW, bus 2 the slack with a 200 MW load, lossless lines A and B of X = 0.2 pu rated 120 MVA (rating B 160 MVA
/// where a test says so). A line carrying P between buses at 1 pu has sin(delta) = P X, Q_from = (1 - cos delta) / X, and
/// the transfer is within tol = 1e-8 pu of the schedule, so |dS| &lt;= (P + Q tan delta) / S x tol (first order).</para>
/// </summary>
public sealed class Rc5Wp26ReviewFixTests
{
    private const double Tol = 1e-8;
    private const double BaseMva = 100.0;

    private static GridModel TwoBus(double rateB = 0) => new()
    {
        Name = "ras-two-bus",
        BaseMva = BaseMva,
        Buses = new[] { new Bus(1, BusType.Pv, Vm: 1.0), new Bus(2, BusType.Slack, Pd: 200, Vm: 1.0) },
        Generators = new[] { new Generator(1, 150, Vg: 1.0, Name: "G1"), new Generator(1, 50, Vg: 1.0, Name: "G1b"), new Generator(2, 0, Vg: 1.0, Name: "GS") },
        Branches = new[]
        {
            new Branch(1, 2, 0, 0.2, RateAMva: 120, Name: "A", RateBMva: rateB),
            new Branch(1, 2, 0, 0.2, RateAMva: 120, Name: "B", RateBMva: rateB),
        },
    };

    private static readonly ElementKeys.MachineId[] Machines = { new(1, "1"), new(1, "2"), new(2, "1") };
    private static readonly ElementKeys.BranchId[] Branches = { new(1, 2, 0, "A", 0), new(1, 2, 0, "B", 0) };

    private static RasSet Ras(GridModel m, params string[] schemes) =>
        RasFile.Parse("{ \"schema\": \"gridsim-ras/1\", \"schemes\": [" + string.Join(",", schemes) + "] }").Resolve(m, Machines, Branches);

    private static RasContingencyResult One(GridModel m, RasSet ras, ElementOutage outage, PowerFlowOptions? opt = null) =>
        ContingencyAnalysis.ScreenWithRas(m, new[] { outage }, ras, ContingencyAnalysis.CreateStudySolver(m), opt ?? ContingencyAnalysis.StudyOptions).Results.Single();

    private static readonly ElementOutage OutA = new(OutageKind.Branch, 0);

    private static (double Mva, double BoundMva) Line(double pMw)
    {
        double p = pMw / BaseMva, x = 0.2;
        double sin = p * x, cos = Math.Sqrt(1 - sin * sin);
        double q = (1 - cos) / x;
        double s = Math.Sqrt(p * p + q * q);
        return (s * BaseMva, (p + q * sin / cos) / s * Tol * BaseMva + 1e-12);
    }

    /// <summary>The sending-end |S| (MVA) of a lossless X = 0.2 line from a 1 pu source to a PQ bus drawing P + jQ (pu), and
    /// its first-order error bound for a solve whose bus-2 mismatches are each within tol: (|dS/dP| + |dS/dQ|) x tol, the
    /// partials by central differences at h = 1e-6 (truncation error h^2 times the third derivative, far below tol).
    /// The receiving voltage: V^4 + (2QX - 1) V^2 + X^2 (P^2 + Q^2) = 0, upper root; the sending end Q_from = Q + |S|^2 X / V^2.</summary>
    private static (double Mva, double BoundMva) PqEnd(double p, double q)
    {
        static double S(double p, double q)
        {
            const double x = 0.2;
            double a = 1 - 2 * q * x;
            double v2 = (a + Math.Sqrt(a * a - 4 * x * x * (p * p + q * q))) / 2;
            double qf = q + (p * p + q * q) * x / v2;
            return Math.Sqrt(p * p + qf * qf);
        }
        const double h = 1e-6;
        double dp = (S(p + h, q) - S(p - h, q)) / (2 * h), dq = (S(p, q + h) - S(p, q - h)) / (2 * h);
        return (S(p, q) * BaseMva, (Math.Abs(dp) + Math.Abs(dq)) * Tol * BaseMva + 1e-12);
    }

    private const string OverloadB = """{ "kind": "loadingAbove", "branch": "1-2:B", "percent": 100 }""";

    [Fact]
    public void CloseBranch_never_recloses_the_contingencys_own_outage()
    {
        GridModel m = TwoBus();
        RasContingencyResult r = One(m, Ras(m, """
            { "name": "BACK", "arming": [ { "kind": "outage", "branch": "1-2:A" } ], "triggers": [ OVERLOAD ],
              "actions": [ { "kind": "closeBranch", "branch": "1-2:A" } ] }
            """.Replace("OVERLOAD", OverloadB, StringComparison.Ordinal)), OutA);
        RasActionRecord a = Assert.Single(r.Actions);
        Assert.False(a.Applied);
        Assert.Equal("ACTION SKIPPED: the branch is the contingency's outage", a.Detail);
        // The state is still the contingency's: 200 MW on B, 170 %, not the base case's 100 MW.
        Assert.Same(r.PreRas, r.PostRas);
        Assert.Equal(RasStatus.ViolationsAfterRas, r.Status);
        Assert.Equal(new[] { "branch B 170%" }, r.PostRas.Violations);
    }

    [Fact]
    public void A_level_where_every_action_is_skipped_ends_the_run_and_is_not_a_RAS_loop()
    {
        GridModel m = TwoBus();
        // maxOperations 99 above maxLevels 5: before the fix the scheme re-fired on the unchanged state up to RAS LOOP.
        RasContingencyResult r = One(m, Ras(m, """
            { "name": "NOOP", "arming": [ { "kind": "outage", "branch": "1-2:A" } ], "triggers": [ OVERLOAD ],
              "actions": [ { "kind": "tripBranch", "branch": "1-2:A" } ], "maxOperations": 99 }
            """.Replace("OVERLOAD", OverloadB, StringComparison.Ordinal)), OutA);
        Assert.Single(r.Levels);
        RasActionRecord a = Assert.Single(r.Actions);
        Assert.False(a.Applied);
        Assert.Equal(RasStatus.ViolationsAfterRas, r.Status);
        Assert.Same(r.PreRas, r.PostRas);
    }

    [Theory]
    [InlineData("""{ "schema": "gridsim-ras/1", "schema": "gridsim-ras/1", "schemes": [] }""", "RAS file: the key 'schema' is given twice")]
    [InlineData("""{ "schema": "gridsim-ras/1", "schemes": [ { "name": "X", "name": "Y", "arming": [ { "kind": "always" } ], "triggers": [ { "kind": "notConverged" } ], "actions": [ { "kind": "tripBranch", "branch": "1-2:B" } ] } ] }""", "the key 'name' is given twice")]
    [InlineData("""{ "schema": "gridsim-ras/1", "schemes": [ { "name": "X", "arming": [ { "kind": "always" } ], "triggers": [ { "kind": "notConverged" } ], "actions": [ { "kind": "shedLoad", "bus": 2, "mw": 1, "mw": 50 } ] } ] }""", "the key 'mw' is given twice")]
    [InlineData("""{ "schema": "gridsim-ras/1", "schemes": [ { "name": "X", "arming": [ { "kind": "always" } ], "triggers": [ { "kind": "notConverged" } ], "actions": [ { "kind": "setTap", "branch": "1-2:B", "ratio": 105 } ] } ] }""", "ratio must be from 0.5 to 2 (got 105)")]
    [InlineData("""{ "schema": "gridsim-ras/1", "schemes": [ { "name": "X", "arming": [ { "kind": "always" } ], "triggers": [ { "kind": "notConverged" } ], "actions": [ { "kind": "setTap", "branch": "1-2:B", "ratio": 0.4 } ] } ] }""", "ratio must be from 0.5 to 2 (got 0.4)")]
    [InlineData("""{ "schema": "gridsim-ras/1", "schemes": [ { "name": "X", "arming": [ { "kind": "always" } ], "triggers": [ { "kind": "notConverged" } ], "actions": [ { "kind": "switchShunt", "bus": 2, "mvar": -200000 } ] } ] }""", "|mvar| must be at most 100000 (got -200000)")]
    public void The_reader_refuses_duplicate_keys_and_out_of_range_values(string json, string expected)
    {
        var ex = Assert.Throws<InvalidDataException>(() => RasFile.Parse(json));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_range_ends_are_accepted()
    {
        foreach (string action in new[]
        {
            """{ "kind": "setTap", "branch": "1-2:B", "ratio": 0.5 }""", """{ "kind": "setTap", "branch": "1-2:B", "ratio": 2 }""",
            """{ "kind": "switchShunt", "bus": 2, "mvar": 100000 }""", """{ "kind": "switchShunt", "bus": 2, "mvar": -100000 }""",
        })
        {
            RasSet s = RasFile.Parse("{ \"schema\": \"gridsim-ras/1\", \"schemes\": [ { \"name\": \"X\", \"arming\": [ { \"kind\": \"always\" } ], "
                + "\"triggers\": [ { \"kind\": \"notConverged\" } ], \"actions\": [ " + action + " ] } ] }");
            Assert.Single(s.Schemes);
        }
    }

    [Fact]
    public void Logic_any_operates_on_one_trigger_and_logic_all_needs_both()
    {
        GridModel m = TwoBus();
        // After A is lost bus 2 is at 1 pu (the slack), so voltageBelow 0.5 is false while B is overloaded.
        const string scheme = """
            { "name": "ANY", "arming": [ { "kind": "always" } ],
              "triggers": [ { "kind": "voltageBelow", "bus": 2, "pu": 0.5 }, OVERLOAD ], "logic": "LOGIC",
              "actions": [ { "kind": "setGeneratorP", "unit": "1:1", "mw": 100 } ] }
            """;
        RasContingencyResult any = One(m, Ras(m, scheme.Replace("OVERLOAD", OverloadB, StringComparison.Ordinal).Replace("LOGIC", "any", StringComparison.Ordinal)), OutA);
        Assert.Equal(new[] { "ANY" }, Assert.Single(any.Levels).Schemes);
        // G1 at 100 MW: 150 MW on B (G1b 50 + G1 100), 151.76 MVA, 126 % of 120.
        (double s, double b) = Line(150);
        Assert.True(Math.Abs(any.PostRas.MaxBranchLoadingPercent - s / 1.2) <= b / 1.2, $"{any.PostRas.MaxBranchLoadingPercent:R}");
        RasContingencyResult all = One(m, Ras(m, scheme.Replace("OVERLOAD", OverloadB, StringComparison.Ordinal).Replace("LOGIC", "all", StringComparison.Ordinal)), OutA);
        Assert.Empty(all.Levels);
        Assert.Same(all.PreRas, all.PostRas);
    }

    [Fact]
    public void A_trip_of_the_reference_unit_re_elects_the_reference()
    {
        GridModel m = TwoBus();
        RasContingencyResult r = One(m, Ras(m, """
            { "name": "REF", "arming": [ { "kind": "always" } ], "triggers": [ OVERLOAD ],
              "actions": [ { "kind": "tripGenerator", "unit": "2:1" } ] }
            """.Replace("OVERLOAD", OverloadB, StringComparison.Ordinal)), OutA);
        RasActionRecord a = Assert.Single(r.Actions);
        Assert.True(a.Applied, a.Detail);
        Assert.StartsWith("tripped (0 MW scheduled); ", a.Detail, StringComparison.Ordinal);
        // GS scheduled 0 MW; the reference moves to bus 1 (V = 1), and bus 2 becomes a PQ bus taking 200 MW over B alone. Its
        // voltage is no longer held: V^4 - V^2 + X^2 P^2 = 0 gives V^2 = 0.8, and the sending end carries P + j P^2 X / V^2
        // = 2 + j1 pu, sqrt(5) x 100 = 223.61 MVA, 186.34 % of 120 (the larger end; the receiving end is 200 MVA).
        Assert.True(r.PostRas.Converged);
        (double s, double b) = PqEnd(2.0, 0.0);
        Assert.True(Math.Abs(s - Math.Sqrt(5.0) * 100) < 1e-9);
        Assert.True(Math.Abs(r.PostRas.MaxBranchLoadingPercent - s / 1.2) <= b / 1.2, $"{r.PostRas.MaxBranchLoadingPercent:R} vs {s / 1.2:R}");
        // And bus 2 now sits at sqrt(0.8) = 0.894 pu, below its band.
        Assert.Equal(new[] { "branch B 186%", "undervoltage 0.894 p.u." }, r.PostRas.Violations);
        Assert.Equal(RasStatus.ViolationsAfterRas, r.Status);
    }

    [Fact]
    public void A_trip_of_the_reference_unit_with_no_unit_to_take_over_is_skipped()
    {
        var m = new GridModel
        {
            Name = "ras-one-unit",
            BaseMva = BaseMva,
            Buses = new[] { new Bus(1, BusType.Slack, Vm: 1.0), new Bus(2, BusType.Pq, Pd: 50) },
            Generators = new[] { new Generator(1, 0, Vg: 1.0, Name: "GS") },
            Branches = new[] { new Branch(1, 2, 0, 0.2, Name: "L1"), new Branch(1, 2, 0, 0.2, Name: "L2") },
        };
        RasSet ras = RasFile.Parse("""
            { "schema": "gridsim-ras/1", "schemes": [ { "name": "TRIP", "arming": [ { "kind": "always" } ],
              "triggers": [ { "kind": "voltageAbove", "bus": 1, "pu": 0.5 } ], "actions": [ { "kind": "tripGenerator", "unit": "GS" } ] } ] }
            """).Resolve(m);
        RasContingencyResult r = One(m, ras, new ElementOutage(OutageKind.Branch, 0));
        RasActionRecord a = Assert.Single(r.Actions);
        Assert.False(a.Applied);
        Assert.Equal("ACTION SKIPPED: no other in-service unit could take over the reference of bus 1", a.Detail);
        Assert.Same(r.PreRas, r.PostRas);
        Assert.Single(r.Levels);
    }

    [Fact]
    public void Arming_on_a_unit_outage_and_on_a_bus_outage()
    {
        GridModel m = TwoBus();
        const string onUnit = """
            { "name": "U", "arming": [ { "kind": "outage", "unit": "1:2" } ], "triggers": [ { "kind": "voltageAbove", "bus": 2, "pu": 0.5 } ],
              "actions": [ { "kind": "shedLoad", "bus": 2, "mw": 10 } ] }
            """;
        const string onBus = """
            { "name": "BUS", "arming": [ { "kind": "outage", "bus": 1 } ], "triggers": [ { "kind": "voltageAbove", "bus": 2, "pu": 0.5 } ],
              "actions": [ { "kind": "shedLoad", "bus": 2, "mw": 10 } ] }
            """;
        RasSet ras = Ras(m, onUnit, onBus);
        // G1b (Generators[1], key 1:2) lost: U is armed, BUS is not.
        RasContingencyResult unit = One(m, ras, new ElementOutage(OutageKind.Generator, 1));
        Assert.Equal(new[] { "U" }, unit.Armed);
        Assert.True(Assert.Single(unit.Actions).Applied);
        // Bus 1 lost: the bus outage contains its units, so both are armed.
        RasContingencyResult bus = One(m, ras, new ElementOutage(OutageKind.Bus, 0));
        Assert.Equal(new[] { "U", "BUS" }, bus.Armed);
        // A branch outage arms neither.
        Assert.Empty(One(m, ras, OutA).Armed);
    }

    [Fact]
    public void The_final_check_uses_the_post_fault_rating()
    {
        // Rating B 160 MVA. After RAS-1 trips G1b, B carries 150 MW: 151.76 MVA, 126 % of rating A but 94.8 % of rating B.
        GridModel m = TwoBus(rateB: 160);
        RasSet ras = Ras(m, """
            { "name": "RAS-1", "arming": [ { "kind": "outage", "branch": "1-2:A" } ], "triggers": [ OVERLOAD ],
              "actions": [ { "kind": "tripGenerator", "unit": "1:2" } ] }
            """.Replace("OVERLOAD", OverloadB, StringComparison.Ordinal));
        (double s, double b) = Line(150);
        Assert.True((s + b) / 160 * 100 < 100 && (s - b) / 120 * 100 > 100);
        RasContingencyResult onA = One(m, ras, OutA);
        Assert.Equal(RasStatus.ViolationsAfterRas, onA.Status);
        RasContingencyResult onB = One(m, ras, OutA, ContingencyAnalysis.StudyOptions with { PostFaultRating = PostFaultRating.RateB });
        // Before the scheme, 204.31 MVA is above rating B too (127.7 %), so the trigger (rating A) operates the same way.
        Assert.False(onB.PreRas.Secure);
        Assert.Equal(new[] { "RAS-1" }, Assert.Single(onB.Levels).Schemes);
        Assert.Equal(RasStatus.SecureAfterRas, onB.Status);
        Assert.Empty(onB.PostRas.Violations);
    }
}
