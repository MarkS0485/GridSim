using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GridSim.Core.Analysis;
using GridSim.Core.Analysis.Ras;
using GridSim.Core.Api;
using GridSim.Core.Journal;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP26: user-authored remedial action schemes (gridsim-ras/1) inside the contingency screens.
/// <para>Sources: closed forms only (GridSim's own design; the concepts follow the NERC Glossary's "Remedial Action
/// Scheme"). The two-bus base case: bus 1 (PV, V = 1) carries G1 150 MW and G1b 50 MW, bus 2 is the slack (V = 1) with a
/// 200 MW load, and two parallel lossless lines A and B (R = 0, X = 0.2 pu, rating A 120 MVA) join them. With both ends at
/// V = 1, a line carrying P has sin(delta) = P X and Q_from = (1 - cos delta) / X, and |S_from| = |S_to|.</para>
/// <para>Tolerances. Newton stops at a largest bus mismatch below tol = 1e-8 pu (PowerFlowOptions.Tolerance). In the
/// two-bus case the only mismatch is bus 1's real power (bus 1 is PV, bus 2 the slack), so the transfer P is within tol
/// of the scheduled value. Then dS/dP = (P + Q dQ/dP) / S with dQ/dP = tan(delta), so |dS| &lt;= (P + Q tan delta) / S x
/// tol pu, first order (the second-order term is about tol^2). Floating-point rounding of the solve and the closed form
/// is below 1e-12 MVA. Each assert uses that bound, computed in the test.</para>
/// </summary>
public sealed class Rc5Wp26RasTests : IDisposable
{
    private const double Tol = 1e-8;   // PowerFlowOptions' default tolerance (pu), used by StudyOptions
    private const double BaseMva = 100.0;
    private readonly string _dir = JournalTestSupport.NewDir("wp26");

    public void Dispose() => JournalTestSupport.Delete(_dir);

    // ---- the base case and its closed forms ---------------------------------------------------------------------------

    private static GridModel TwoBus(bool withC = false) => new()
    {
        Name = "ras-two-bus",
        BaseMva = BaseMva,
        Buses = new[] { new Bus(1, BusType.Pv, Vm: 1.0), new Bus(2, BusType.Slack, Pd: 200, Vm: 1.0) },
        Generators = new[] { new Generator(1, 150, Vg: 1.0, Name: "G1"), new Generator(1, 50, Vg: 1.0, Name: "G1b"), new Generator(2, 0, Vg: 1.0, Name: "GS") },
        Branches = withC
            ? new[] { new Branch(1, 2, 0, 0.2, RateAMva: 120, Name: "A"), new Branch(1, 2, 0, 0.2, RateAMva: 120, Name: "B"),
                      new Branch(1, 2, 0, 0.2, RateAMva: 120, Name: "C", InService: false) }
            : new[] { new Branch(1, 2, 0, 0.2, RateAMva: 120, Name: "A"), new Branch(1, 2, 0, 0.2, RateAMva: 120, Name: "B") },
    };

    // The PSS/E identities of the two-bus case, so the file can use the spec's keys ("1-2:A", "1:2").
    private static readonly ElementKeys.MachineId[] Machines = { new(1, "1"), new(1, "2"), new(2, "1") };

    private static ElementKeys.BranchId[] BranchIds(bool withC) => withC
        ? new ElementKeys.BranchId[] { new(1, 2, 0, "A", 0), new(1, 2, 0, "B", 0), new(1, 2, 0, "C", 0) }
        : new ElementKeys.BranchId[] { new(1, 2, 0, "A", 0), new(1, 2, 0, "B", 0) };

    /// <summary>|S| (MVA) of a lossless X = 0.2 line carrying P MW between two buses at 1 pu, and its first-order error
    /// bound (MVA) for a transfer known to within tol pu.</summary>
    private static (double Mva, double BoundMva) Line(double pMw)
    {
        double p = pMw / BaseMva, x = 0.2;
        double sin = p * x, cos = Math.Sqrt(1 - sin * sin);
        double q = (1 - cos) / x;
        double s = Math.Sqrt(p * p + q * q);
        double bound = (p + q * sin / cos) / s * Tol * BaseMva + 1e-12;
        return (s * BaseMva, bound);
    }

    private static RasSet Ras(string json, bool withC = false) => RasFile.Parse(json).Resolve(TwoBus(withC), Machines, BranchIds(withC));

    private static RasScreenReport Screen(GridModel m, RasSet ras, params ElementOutage[] outages) =>
        ContingencyAnalysis.ScreenWithRas(m, outages, ras, ContingencyAnalysis.CreateStudySolver(m), ContingencyAnalysis.StudyOptions);

    private static readonly ElementOutage OutA = new(OutageKind.Branch, 0), OutB = new(OutageKind.Branch, 1);

    private const string Ras1 = """
        { "name": "RAS-1", "priority": 1,
          "arming":   [ { "kind": "outage", "branch": "1-2:A" }, { "kind": "preFlowAbove", "branch": "1-2:B", "mva": 80 } ],
          "triggers": [ { "kind": "loadingAbove", "branch": "1-2:B", "percent": 100, "rating": "A" } ],
          "logic": "all",
          "actions":  [ { "kind": "tripGenerator", "unit": "1:2" } ],
          "maxOperations": 1 }
        """;

    private const string Ras2 = """
        { "name": "RAS-2", "priority": 2, "arming": [ { "kind": "always" } ],
          "triggers": [ { "kind": "loadingAbove", "branch": "1-2:B", "percent": 100 } ],
          "actions": [ { "kind": "setGeneratorP", "unit": "1:1", "mw": 100 } ] }
        """;

    private static string File1(params string[] schemes) => "{ \"schema\": \"gridsim-ras/1\", \"schemes\": [" + string.Join(",", schemes) + "] }";

    // ---- 1. the overload without schemes ------------------------------------------------------------------------------

    [Fact]
    public void T1_outage_of_A_puts_200_MW_on_B_at_the_closed_form_loading()
    {
        GridModel m = TwoBus();
        // sin(delta) = 2 x 0.2 = 0.4: delta = 23.578178478201835 deg, Q_from = 0.41742430504416006 pu,
        // S = 204.3096436892199 MVA, 170.26 % of 120 MVA.
        (double s, double bound) = Line(200);
        Assert.True(Math.Abs(s - 204.3096436892199) < 1e-9);
        Assert.True(Math.Abs(Math.Asin(0.4) * 180 / Math.PI - 23.578178478201835) < 1e-12);
        ContingencyResult row = ContingencyAnalysis.ScreenElementOutage(m, OutA, ContingencyAnalysis.CreateStudySolver(m), ContingencyAnalysis.StudyOptions);
        Assert.True(row.Converged);
        Assert.True(Math.Abs(row.MaxBranchLoadingPercent - s / 120 * 100) <= bound / 120 * 100, $"{row.MaxBranchLoadingPercent:R}");
        Assert.Equal(new[] { "branch B 170%" }, row.Violations);
    }

    // ---- 2. RAS-1 trips G1b: 150 MW on B, still overloaded ------------------------------------------------------------

    [Fact]
    public void T2_RAS1_trips_G1b_at_level_1_and_the_state_is_still_overloaded()
    {
        GridModel m = TwoBus();
        RasContingencyResult r = Screen(m, Ras(File1(Ras1)), OutA).Results.Single();

        // Before the scheme: the screen's row, 200 MW on B.
        (double s0, double b0) = Line(200);
        Assert.True(Math.Abs(r.PreRas.MaxBranchLoadingPercent - s0 / 1.2) <= b0 / 1.2);
        Assert.Equal(new[] { "RAS-1" }, r.Armed);

        // Level 1: RAS-1 alone; its one action applied.
        RasLevelRecord level = Assert.Single(r.Levels);
        Assert.Equal(1, level.Level);
        Assert.Equal(new[] { "RAS-1" }, level.Schemes);
        RasActionRecord act = Assert.Single(r.Actions);
        Assert.Equal((1, "RAS-1", 1, RasActionKind.TripGenerator, "1:2", true), (act.Level, act.Scheme, act.Action, act.Kind, act.Element, act.Applied));

        // After: 150 MW on B (the slack makes up 50 MW): sin(delta) = 0.3, Q = (1 - sqrt(0.91)) / 0.2 = 0.2303039929152717,
        // S = 151.75769928253123 MVA, 126.46 % of 120.
        // The bound at 150 MW: (P + Q tan delta) / S x tol = 1.03614 x 1e-8 pu = 1.04e-6 MVA to three figures (plus 1e-12
        // for rounding).
        (double s1, double b1) = Line(150);
        Assert.True(Math.Abs(s1 - 151.75769928253123) < 1e-9);
        Assert.True(Math.Abs(b1 - 1.0361e-6) < 1e-9, $"{b1:R}");
        Assert.True(Math.Abs((1 - Math.Sqrt(0.91)) / 0.2 - 0.2303039929152717) < 1e-15);
        Assert.True(r.PostRas.Converged);
        Assert.True(Math.Abs(r.PostRas.MaxBranchLoadingPercent - s1 / 1.2) <= b1 / 1.2, $"{r.PostRas.MaxBranchLoadingPercent:R}");
        Assert.Equal(new[] { "branch B 126%" }, r.PostRas.Violations);

        // maxOperations 1: it cannot operate again, so the loop stops at level 1 with the violation.
        Assert.Equal(1, r.LastLevel);
        Assert.Equal(RasStatus.ViolationsAfterRas, r.Status);
        Assert.Equal("VIOLATIONS AFTER RAS", RasStatusText.Of(r.Status));
    }

    // ---- 3. a second scheme operates in the same level ----------------------------------------------------------------

    [Fact]
    public void T3_RAS2_operates_in_the_same_level_and_the_state_is_secure()
    {
        GridModel m = TwoBus();
        RasContingencyResult r = Screen(m, Ras(File1(Ras1, Ras2)), OutA).Results.Single();
        Assert.Equal(new[] { "RAS-1", "RAS-2" }, r.Armed);
        RasLevelRecord level = Assert.Single(r.Levels);
        Assert.Equal(new[] { "RAS-1", "RAS-2" }, level.Schemes);
        Assert.Equal(new[] { "RAS-1", "RAS-2" }, r.Fired);
        Assert.Equal(new[] { true, true }, r.Actions.Select(a => a.Applied));

        // 100 MW on B: sin(delta) = 0.2, S = 100.50896200520818 MVA, 83.76 % of 120.
        (double s, double b) = Line(100);
        Assert.True(Math.Abs(s - 100.50896200520818) < 1e-9);
        Assert.True(Math.Abs(r.PostRas.MaxBranchLoadingPercent - s / 1.2) <= b / 1.2, $"{r.PostRas.MaxBranchLoadingPercent:R}");
        Assert.Empty(r.PostRas.Violations);
        Assert.Equal(RasStatus.SecureAfterRas, r.Status);
        Assert.Equal(1, r.LastLevel);
    }

    [Fact]
    public void T3b_the_order_within_a_level_follows_priority()
    {
        GridModel m = TwoBus();
        string first = Ras2.Replace("\"priority\": 2", "\"priority\": 0", StringComparison.Ordinal);
        RasContingencyResult r = Screen(m, Ras(File1(Ras1, first)), OutA).Results.Single();
        Assert.Equal(new[] { "RAS-2", "RAS-1" }, Assert.Single(r.Levels).Schemes);
        Assert.Equal(new[] { "RAS-2", "RAS-1" }, r.Actions.Select(a => a.Scheme));
        Assert.Equal(RasStatus.SecureAfterRas, r.Status);
    }

    // ---- 4. arming not met --------------------------------------------------------------------------------------------

    [Fact]
    public void T4_another_contingency_leaves_RAS1_unarmed()
    {
        GridModel m = TwoBus();
        RasContingencyResult r = Screen(m, Ras(File1(Ras1)), OutB).Results.Single();
        Assert.Empty(r.Armed);
        Assert.Empty(r.Levels);
        Assert.Empty(r.Actions);
        Assert.Same(r.PreRas, r.PostRas);
        Assert.Equal(RasStatus.ViolationsAfterRas, r.Status);
        Assert.Equal(new[] { "branch A 170%" }, r.PostRas.Violations);
        Assert.Equal(0, r.LastLevel);
    }

    [Fact]
    public void T4b_a_pre_flow_threshold_above_the_base_flow_leaves_RAS1_unarmed()
    {
        // The base flow on B is 100 MW: S = 100.50896200520818 MVA, within 1e-6 MVA. 101 MVA is above it, 100 below.
        (double s, double b) = Line(100);
        Assert.True(s + b < 101 && s - b > 100);
        GridModel m = TwoBus();
        RasContingencyResult high = Screen(m, Ras(File1(Ras1.Replace("\"mva\": 80", "\"mva\": 101", StringComparison.Ordinal))), OutA).Results.Single();
        Assert.Empty(high.Armed);
        Assert.Empty(high.Levels);
        RasContingencyResult low = Screen(m, Ras(File1(Ras1.Replace("\"mva\": 80", "\"mva\": 100", StringComparison.Ordinal))), OutA).Results.Single();
        Assert.Equal(new[] { "RAS-1" }, low.Armed);
        Assert.Single(low.Levels);
    }

    // ---- 5. the loop guard --------------------------------------------------------------------------------------------

    [Fact]
    public void T5_a_scheme_that_never_clears_its_trigger_ends_as_RAS_LOOP_at_max_levels()
    {
        GridModel m = TwoBus(withC: true);
        // Branch C is out of service in the case. Closing it and opening it again leaves B as loaded as before.
        const string loop = """
            { "name": "RAS-L", "arming": [ { "kind": "outage", "branch": "1-2:A" } ],
              "triggers": [ { "kind": "loadingAbove", "branch": "1-2:B", "percent": 100 } ],
              "actions": [ { "kind": "closeBranch", "branch": "1-2:C" }, { "kind": "tripBranch", "branch": "1-2:C" } ],
              "maxOperations": 99 }
            """;
        RasContingencyResult r = Screen(m, Ras(File1(loop), withC: true), OutA).Results.Single();
        Assert.Equal(RasStatus.RasLoop, r.Status);
        Assert.Equal(RasFile.DefaultMaxLevels, r.Levels.Count);
        Assert.Equal(Enumerable.Range(1, 5), r.Levels.Select(l => l.Level));
        Assert.All(r.Levels, l => Assert.Equal(new[] { "RAS-L" }, l.Schemes));
        Assert.Equal(10, r.Actions.Count);
        Assert.All(r.Actions, a => Assert.True(a.Applied, a.Detail));
        Assert.Equal(Enumerable.Repeat(new[] { RasActionKind.CloseBranch, RasActionKind.TripBranch }, 5).SelectMany(x => x), r.Actions.Select(a => a.Kind));
        // The state is back where it started: 200 MW on B.
        (double s, double b) = Line(200);
        Assert.True(Math.Abs(r.PostRas.MaxBranchLoadingPercent - s / 1.2) <= b / 1.2);

        // maxLevels in the file sets the limit.
        string three = "{ \"schema\": \"gridsim-ras/1\", \"maxLevels\": 3, \"schemes\": [" + loop + "] }";
        RasContingencyResult r3 = Screen(m, Ras(three, withC: true), OutA).Results.Single();
        Assert.Equal((RasStatus.RasLoop, 3, 6), (r3.Status, r3.Levels.Count, r3.Actions.Count));
    }

    [Fact]
    public void T5b_an_action_on_an_element_already_out_is_skipped_with_the_reason()
    {
        GridModel m = TwoBus();
        const string skip = """
            { "name": "RAS-S", "arming": [ { "kind": "outage", "branch": "1-2:A" } ],
              "triggers": [ { "kind": "loadingAbove", "branch": "1-2:B", "percent": 100 } ],
              "actions": [ { "kind": "tripBranch", "branch": "1-2:A" } ] }
            """;
        RasContingencyResult r = Screen(m, Ras(File1(skip)), OutA).Results.Single();
        RasActionRecord a = Assert.Single(r.Actions);
        Assert.False(a.Applied);
        Assert.StartsWith("ACTION SKIPPED", a.Detail, StringComparison.Ordinal);
        Assert.Same(r.PreRas, r.PostRas);   // nothing applied: the screen's own row
        Assert.Equal(RasStatus.ViolationsAfterRas, r.Status);
    }

    // ---- 6. the notConverged trigger ----------------------------------------------------------------------------------

    /// <summary>
    /// Slack bus 1 (V = 1) feeds a unity-power-factor load P at bus 2 over two parallel lines of X = 0.4 pu each. With
    /// V1 = 1, line reactance X and load P + jQ, the receiving voltage satisfies V^4 + (2QX - 1)V^2 + X^2(P^2 + Q^2) = 0,
    /// so for Q = 0 a solution exists if and only if 1 - 4(PX)^2 &gt;= 0, that is P &lt;= 1 / (2X) (the nose point).
    /// Base (X = 0.2): P_max = 2.5 pu = 250 MW, load 140 MW solves (V = 0.95617). One line out (X = 0.4): P_max = 1.25 pu
    /// = 125 MW &lt; 140 MW, so no state exists, and every state has a bus-2 real-power mismatch of at least 0.15 pu, far
    /// above tol: Newton cannot report convergence. A 30 % shed leaves 98 MW &lt; 125 MW, and the solution is
    /// V^2 = (1 + sqrt(1 - 4(PX)^2)) / 2.
    /// </summary>
    [Fact]
    public void T6_a_notConverged_scheme_sheds_30_percent_and_the_state_solves_at_the_closed_form_voltage()
    {
        var m = new GridModel
        {
            Name = "ras-nose",
            BaseMva = BaseMva,
            Buses = new[] { new Bus(1, BusType.Slack, Vm: 1.0), new Bus(2, BusType.Pq, Pd: 140) },
            Generators = new[] { new Generator(1, 0, Vg: 1.0, Name: "GS") },
            Branches = new[] { new Branch(1, 2, 0, 0.4, Name: "L1"), new Branch(1, 2, 0, 0.4, Name: "L2") },
        };
        const string json = """
            { "schema": "gridsim-ras/1", "schemes": [
              { "name": "UVLS", "arming": [ { "kind": "always" } ], "triggers": [ { "kind": "notConverged" } ],
                "actions": [ { "kind": "shedLoad", "bus": 2, "fraction": 0.3 } ] } ] }
            """;
        RasSet ras = RasFile.Parse(json).Resolve(m);
        RasScreenReport rep = ContingencyAnalysis.ScreenWithRas(m, new[] { new ElementOutage(OutageKind.Branch, 0) }, ras,
            ContingencyAnalysis.CreateStudySolver(m), ContingencyAnalysis.StudyOptions);
        Assert.True(rep.BaseCase.Converged);
        RasContingencyResult r = Assert.Single(rep.Results);

        Assert.False(r.PreRas.Converged);   // beyond the nose
        Assert.Equal(new[] { "UVLS" }, Assert.Single(r.Levels).Schemes);
        RasActionRecord a = Assert.Single(r.Actions);
        Assert.True(a.Applied, a.Detail);
        Assert.True(r.PostRas.Converged);

        double pd = 140 - 140 * 0.3;   // the action's own arithmetic: shed = Pd x fraction
        double p = pd / BaseMva, x = 0.4;
        Assert.True(1 - 4 * (p * x) * (p * x) > 0 && 1 - 4 * (1.4 * x) * (1.4 * x) < 0);
        double v = Math.Sqrt((1 + Math.Sqrt(1 - 4 * (p * x) * (p * x))) / 2);
        // First-order bound: |dV| <= (|df/dP| + |df/dQ|) / |df/dV| x tol, with df/dP = 2 X^2 P, df/dQ = 2 X V^2 (at Q = 0)
        // and df/dV = 4V^3 - 2V; plus 1e-12 for rounding.
        double bound = (2 * x * x * p + 2 * x * v * v) / (4 * v * v * v - 2 * v) * Tol + 1e-12;
        Assert.True(Math.Abs(r.PostRas.MinVm - v) <= bound, $"{r.PostRas.MinVm:R} vs {v:R} (bound {bound:R})");
        // 0.9002 pu is below the 0.94 pu band: the state solves but is not secure.
        Assert.Equal(RasStatus.ViolationsAfterRas, r.Status);
        Assert.Single(r.PostRas.Violations, t => t.StartsWith("undervoltage", StringComparison.Ordinal));
    }

    // ---- 7. file refusals ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{ "schema": "gridsim-ras/1", "schemes": [ { "name": "X", "arming": [ { "kind": "always" } ], "triggers": [ { "kind": "loadingBelow", "branch": "1-2:B", "percent": 1 } ], "actions": [ { "kind": "tripBranch", "branch": "1-2:B" } ] } ] }""", "unknown trigger kind 'loadingBelow'")]
    [InlineData("""{ "schema": "gridsim-ras/1", "schemes": [ { "name": "X", "arming": [ { "kind": "sometimes" } ], "triggers": [ { "kind": "notConverged" } ], "actions": [ { "kind": "tripBranch", "branch": "1-2:B" } ] } ] }""", "unknown arming kind 'sometimes'")]
    [InlineData("""{ "schema": "gridsim-ras/1", "schemes": [ { "name": "X", "arming": [ { "kind": "always" } ], "triggers": [ { "kind": "notConverged" } ], "actions": [ { "kind": "explode" } ] } ] }""", "unknown action kind 'explode'")]
    [InlineData("""{ "schema": "gridsim-ras/1", "schemes": [ { "name": "X", "arming": [ { "kind": "always" } ], "triggers": [ { "kind": "notConverged" } ], "actions": [ { "kind": "tripBranch", "branch": "1-2:B" } ] }, { "name": "X", "arming": [ { "kind": "always" } ], "triggers": [ { "kind": "notConverged" } ], "actions": [ { "kind": "tripBranch", "branch": "1-2:B" } ] } ] }""", "two schemes are named 'X'")]
    [InlineData("""{ "schema": "gridsim-ras/1", "schemes": [ { "name": "X", "arming": [ { "kind": "always" } ], "triggers": [ { "kind": "notConverged" } ], "actions": [ { "kind": "tripBranch", "branch": "1-2:B", "delay": 1 } ] } ] }""", "unknown key 'delay'")]
    [InlineData("""{ "schema": "gridsim-ras/1", "schemes": [ { "name": "X", "arming": [], "triggers": [ { "kind": "notConverged" } ], "actions": [ { "kind": "tripBranch", "branch": "1-2:B" } ] } ] }""", "'arming' is empty")]
    [InlineData("""{ "schema": "gridsim-ras/1", "schemes": [ { "name": "X", "arming": [ { "kind": "always" } ], "triggers": [ { "kind": "notConverged" } ], "actions": [ { "kind": "shedLoad", "bus": 2, "fraction": 1.5 } ] } ] }""", "fraction must be above 0 and at most 1")]
    [InlineData("""{ "schema": "gridsim-ras/1", "maxLevels": 0, "schemes": [ { "name": "X", "arming": [ { "kind": "always" } ], "triggers": [ { "kind": "notConverged" } ], "actions": [ { "kind": "tripBranch", "branch": "1-2:B" } ] } ] }""", "maxLevels must be a whole number from 1 to 100")]
    [InlineData("""{ "schema": "gridsim-ras/2", "schemes": [] }""", "is not 'gridsim-ras/1'")]
    public void T7_the_reader_refuses_by_name(string json, string expected)
    {
        var ex = Assert.Throws<InvalidDataException>(() => RasFile.Parse(json));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void T7b_an_unknown_element_key_or_a_missing_rating_is_refused_on_resolve()
    {
        string unknownBranch = File1(Ras1.Replace("\"branch\": \"1-2:A\"", "\"branch\": \"1-2:Z\"", StringComparison.Ordinal));
        var e1 = Assert.Throws<InvalidDataException>(() => Ras(unknownBranch));
        Assert.Contains("no branch has the key '1-2:Z'", e1.Message, StringComparison.Ordinal);
        string unknownUnit = File1(Ras1.Replace("\"unit\": \"1:2\"", "\"unit\": \"1:9\"", StringComparison.Ordinal));
        Assert.Contains("no generating unit has the key '1:9'", Assert.Throws<InvalidDataException>(() => Ras(unknownUnit)).Message, StringComparison.Ordinal);
        string unknownBus = File1("""{ "name": "X", "arming": [ { "kind": "always" } ], "triggers": [ { "kind": "voltageBelow", "bus": 7, "pu": 0.9 } ], "actions": [ { "kind": "tripBranch", "branch": "1-2:B" } ] }""");
        Assert.Contains("no bus has the id '7'", Assert.Throws<InvalidDataException>(() => Ras(unknownBus)).Message, StringComparison.Ordinal);
        string noRateB = File1(Ras1.Replace("\"rating\": \"A\"", "\"rating\": \"B\"", StringComparison.Ordinal));
        Assert.Contains("has no rating B", Assert.Throws<InvalidDataException>(() => Ras(noRateB)).Message, StringComparison.Ordinal);
        // An unresolved set is refused by the screen.
        Assert.Throws<ArgumentException>(() => ContingencyAnalysis.ScreenWithRas(TwoBus(), new[] { OutA }, RasFile.Parse(File1(Ras1))));
    }

    // ---- flowAbove reads a signed end ---------------------------------------------------------------------------------

    [Fact]
    public void T7c_flowAbove_reads_the_real_power_into_the_named_end()
    {
        GridModel m = TwoBus();
        string from = """
            { "name": "F", "arming": [ { "kind": "always" } ], "triggers": [ { "kind": "flowAbove", "branch": "1-2:B", "mw": 199 } ],
              "actions": [ { "kind": "tripGenerator", "unit": "1:2" } ] }
            """;
        // Outage of A: 200 MW into B at bus 1 (above 199), -200 MW at bus 2 (below 199).
        Assert.Single(Screen(m, Ras(File1(from)), OutA).Results.Single().Levels);
        string to = from.Replace("\"mw\": 199", "\"mw\": 199, \"atEnd\": \"to\"", StringComparison.Ordinal);
        Assert.Empty(Screen(m, Ras(File1(to)), OutA).Results.Single().Levels);
    }

    // ---- the state the schemes act on is the screen's own post-contingency state --------------------------------------

    /// <summary>
    /// A scheme that sets the 4-7 transformer's tap to its own value changes no number, so after it the schemes' state must
    /// be the screen's post-contingency state exactly. Checked bit for bit for every branch, generator and bus outage of
    /// case14 (the generator outages include the slack's unit, whose reference moves, and the bus outages remove branches
    /// and units). The pre-scheme rows are bit for bit the screen's rows netted against its base case.
    /// </summary>
    [Fact]
    public void T8_the_schemes_state_is_the_screens_state_for_branch_generator_and_bus_outages()
    {
        GridModel m = S10TestSupport.LoadMatpower("lib/case14.m").Model;
        int k = Enumerable.Range(0, m.Branches.Count).Single(i => m.Branches[i].From == 4 && m.Branches[i].To == 7);
        string ratio = m.Branches[k].TapRatio.ToString("R", CultureInfo.InvariantCulture);
        string json = "{ \"schema\": \"gridsim-ras/1\", \"schemes\": [ { \"name\": \"TAP\", \"arming\": [ { \"kind\": \"always\" } ], "
            + "\"triggers\": [ { \"kind\": \"voltageAbove\", \"bus\": 1, \"pu\": 0.5 } ], "
            + "\"actions\": [ { \"kind\": \"setTap\", \"branch\": \"4-7\", \"ratio\": " + ratio + " } ] } ] }";
        RasSet ras = RasFile.Parse(json).Resolve(m);
        var outages = ContingencyAnalysis.InServiceBranchIndices(m).Select(i => new ElementOutage(OutageKind.Branch, i))
            .Concat(ContingencyAnalysis.SingleOutages(m, false, true, true)).ToList();
        PowerFlowOptions opt = ContingencyAnalysis.StudyOptions;
        RasScreenReport rep = ContingencyAnalysis.ScreenWithRas(m, outages, ras, ContingencyAnalysis.CreateStudySolver(m), opt);
        ContingencyResult baseCase = ContingencyAnalysis.ScreenBaseCase(m, ContingencyAnalysis.CreateStudySolver(m), opt);
        Assert.Equal(outages.Count, rep.Results.Count);
        int applied = 0;
        for (int i = 0; i < outages.Count; i++)
        {
            RasContingencyResult r = rep.Results[i];
            ContingencyResult screen = ContingencyAnalysis.NetAgainstBase(
                ContingencyAnalysis.ScreenElementOutage(m, outages[i], ContingencyAnalysis.CreateStudySolver(m), opt), baseCase);
            Same(screen, r.PreRas, $"pre {outages[i]}");
            if (r.Actions.Count > 0 && r.Actions[0].Applied)
            {
                applied++;
                Assert.NotSame(r.PreRas, r.PostRas);
                Same(r.PreRas, r.PostRas, $"post {outages[i]}");
            }
            else Assert.Same(r.PreRas, r.PostRas);
        }
        Assert.True(applied > 20, $"only {applied} outages applied the action");
    }

    private static void Same(ContingencyResult a, ContingencyResult b, string label)
    {
        Assert.True(a.Converged == b.Converged, label);
        Assert.True(BitConverter.DoubleToInt64Bits(a.MaxBranchLoadingPercent) == BitConverter.DoubleToInt64Bits(b.MaxBranchLoadingPercent), label);
        Assert.True(BitConverter.DoubleToInt64Bits(a.WorstBoundaryLoadingPercent) == BitConverter.DoubleToInt64Bits(b.WorstBoundaryLoadingPercent), label);
        Assert.True(BitConverter.DoubleToInt64Bits(a.MinVm) == BitConverter.DoubleToInt64Bits(b.MinVm), label);
        Assert.True(BitConverter.DoubleToInt64Bits(a.MaxVm) == BitConverter.DoubleToInt64Bits(b.MaxVm), label);
        Assert.Equal(a.Violations, b.Violations);
        Assert.Equal(a.PreExistingViolations, b.PreExistingViolations);
        Assert.Equal(a.Outage, b.Outage);
        Assert.Equal(a.Islanded, b.Islanded);
    }

    [Fact]
    public void T9_the_pair_screen_with_schemes_keeps_the_plain_pair_screen_rows_and_order()
    {
        GridModel m = S10TestSupport.LoadMatpower("lib/case14.m").Model;
        // Never armed (bus 1 is above 0.5 pu in the base case), so every row is the plain screen's.
        RasSet ras = RasFile.Parse("""
            { "schema": "gridsim-ras/1", "schemes": [ { "name": "N", "arming": [ { "kind": "preVoltageBelow", "bus": 1, "pu": 0.5 } ],
              "triggers": [ { "kind": "notConverged" } ], "actions": [ { "kind": "shedLoad", "bus": 2, "mw": 1 } ] } ] }
            """).Resolve(m);
        PowerFlowOptions opt = ContingencyAnalysis.StudyOptions;
        RasScreenReport rep = ContingencyAnalysis.ScreenNMinusKWithRas(m, 2, ras, () => ContingencyAnalysis.CreateStudySolver(m), opt, candidateCount: 6);
        NkScreenReport plain = ContingencyAnalysis.ScreenNMinusKReport(m, 2, () => ContingencyAnalysis.CreateStudySolver(m), opt, candidateCount: 6);
        ContingencyResult baseCase = ContingencyAnalysis.ScreenBaseCase(m, ContingencyAnalysis.CreateStudySolver(m), opt);
        IReadOnlyList<ContingencyResult> netted = ContingencyAnalysis.NetAgainstBase(plain.Results, baseCase);
        Assert.Equal(15, rep.Results.Count);   // C(6, 2)
        Assert.Equal(netted.Count, rep.Results.Count);
        for (int i = 0; i < netted.Count; i++)
        {
            Same(netted[i], rep.Results[i].PreRas, $"pair {i}");
            Assert.Empty(rep.Results[i].Armed);
        }
        Assert.Equal(plain.ExcludedSingleOutages.Select(c => c.Outage), rep.ExcludedSingleOutages.Select(c => c.Outage));
    }

    // ---- the command line ---------------------------------------------------------------------------------------------

    private const string TwoBusM = """
        function mpc = two
        mpc.version = '2';
        mpc.baseMVA = 100;
        mpc.bus = [
        	1	2	0	0	0	0	1	1	0	132	1	1.1	0.9;
        	2	3	200	0	0	0	1	1	0	132	1	1.1	0.9;
        ];
        mpc.gen = [
        	1	150	0	999	-999	1	100	1	999	0	0	0	0	0	0	0	0	0	0	0	0;
        	1	50	0	999	-999	1	100	1	999	0	0	0	0	0	0	0	0	0	0	0	0;
        	2	0	0	999	-999	1	100	1	999	0	0	0	0	0	0	0	0	0	0	0	0;
        ];
        mpc.branch = [
        	1	2	0	0.2	0	120	120	120	0	0	1	-360	360;
        	1	2	0	0.2	0	120	120	120	0	0	1	-360	360;
        ];
        """;

    // The MATPOWER case has no names, so the keys are "1-2" and "1-2#2" (branches) and "G1", "G1#2", "G2" (units).
    private const string TwoBusRas = """
        { "schema": "gridsim-ras/1", "schemes": [
          { "name": "RAS-1", "priority": 1,
            "arming": [ { "kind": "outage", "branch": "1-2" }, { "kind": "preFlowAbove", "branch": "1-2#2", "mva": 80 } ],
            "triggers": [ { "kind": "loadingAbove", "branch": "1-2#2", "percent": 100 } ],
            "actions": [ { "kind": "tripGenerator", "unit": "G1#2" } ] },
          { "name": "RAS-2", "priority": 2, "arming": [ { "kind": "always" } ],
            "triggers": [ { "kind": "loadingAbove", "branch": "1-2#2", "percent": 100 } ],
            "actions": [ { "kind": "setGeneratorP", "unit": "G1", "mw": 100 } ] } ] }
        """;

    private string Work(string name)
    {
        string w = Path.Combine(_dir, name);
        Directory.CreateDirectory(w);
        File.WriteAllText(Path.Combine(w, "two.m"), TwoBusM);
        File.WriteAllText(Path.Combine(w, "r.json"), TwoBusRas);
        return w;
    }

    [Fact]
    public void T10_nminus1_with_ras_writes_the_scheme_columns_and_one_row_per_action()
    {
        string w = Work("cli");
        CliProcess.Result r = JournalTestSupport.Run(w, "nminus1", "two.m", "--ras", "r.json", "--json", "o.json", "--csv", "o.csv");
        Assert.True(r.ExitCode == 3, r.ToString());   // the outage of B is not secure after the schemes
        Assert.Contains("SECURE AFTER RAS", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("Verdict: NOT SECURE, 1 contingency(ies) not secure after schemes (exit 3).", r.StdOut, StringComparison.Ordinal);
        using JsonDocument d = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(w, "o.json")));
        JsonElement rows = d.RootElement.GetProperty("tables").GetProperty("outages");
        Assert.Equal(2, rows.GetArrayLength());
        JsonElement a = rows[0];
        Assert.Equal(("RAS-1; RAS-2", 1, "SECURE AFTER RAS"),
            (a.GetProperty("ras_fired").GetString(), a.GetProperty("ras_level").GetInt32(), a.GetProperty("post_ras_status").GetString()));
        (double s, double b) = Line(100);
        Assert.True(Math.Abs(a.GetProperty("post_ras_max_loading_pct").GetDouble() - s / 1.2) <= b / 1.2);
        JsonElement other = rows[1];
        Assert.Equal(("", 0, "VIOLATIONS AFTER RAS"),
            (other.GetProperty("ras_fired").GetString(), other.GetProperty("ras_level").GetInt32(), other.GetProperty("post_ras_status").GetString()));
        JsonElement acts = d.RootElement.GetProperty("tables").GetProperty("ras_actions");
        Assert.Equal(2, acts.GetArrayLength());
        Assert.Equal(("tripGenerator", "G1#2", true), (acts[0].GetProperty("kind").GetString(), acts[0].GetProperty("element").GetString(), acts[0].GetProperty("applied").GetBoolean()));
        Assert.Equal(("setGeneratorP", "G1", true), (acts[1].GetProperty("kind").GetString(), acts[1].GetProperty("element").GetString(), acts[1].GetProperty("applied").GetBoolean()));
        Assert.True(File.Exists(Path.Combine(w, "o-ras_actions.csv")));
        Assert.Equal("NOT SECURE", d.RootElement.GetProperty("summary").GetProperty("verdict").GetString());
        Assert.Equal(1, d.RootElement.GetProperty("summary").GetProperty("secure_after_ras").GetInt32());

        // nminus2 takes the file too. The one pair takes both lines out and islands bus 1, so no scheme can act: not secure.
        CliProcess.Result r2 = JournalTestSupport.Run(w, "nminus2", "two.m", "--ras", "r.json", "--exhaustive", "--json", "o2.json");
        Assert.True(r2.ExitCode == 3, r2.ToString());
        using JsonDocument d2 = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(w, "o2.json")));
        Assert.Equal(1, d2.RootElement.GetProperty("tables").GetProperty("outages").GetArrayLength());
    }

    [Fact]
    public void T11_ras_is_refused_where_it_does_not_apply_and_a_bad_file_is_an_input_error()
    {
        string w = Work("refuse");
        CliProcess.Result pf = JournalTestSupport.Run(w, "pf", "two.m", "--ras", "r.json");
        Assert.True(pf.ExitCode == 2, pf.ToString());
        Assert.Contains("--ras applies to nminus1 and nminus2 only", pf.StdErr, StringComparison.Ordinal);
        CliProcess.Result lanes = JournalTestSupport.Run(w, "nminus1", "two.m", "--ras", "r.json", "--batched-lanes", "2");
        Assert.True(lanes.ExitCode == 2, lanes.ToString());
        Assert.Contains("error: nminus1 --ras screens the outages one by one; drop --batched-lanes or --ras.", lanes.StdErr, StringComparison.Ordinal);
        CliProcess.Result dispatch = JournalTestSupport.Run(w, "nminus1", "two.m", "--ras", "r.json", "--outage-types", "gen", "--outage-dispatch", "droop");
        Assert.True(dispatch.ExitCode == 2, dispatch.ToString());
        Assert.Contains("error: --ras runs with the reference-bus dispatch only; drop --outage-dispatch or --ras.", dispatch.StdErr, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(w, "bad.json"), TwoBusRas.Replace("\"G1#2\"", "\"G9\"", StringComparison.Ordinal));
        CliProcess.Result bad = JournalTestSupport.Run(w, "nminus1", "two.m", "--ras", "bad.json");
        Assert.True(bad.ExitCode == 1, bad.ToString());
        Assert.Contains("no generating unit has the key 'G9'", bad.StdErr, StringComparison.Ordinal);
    }

    // ---- bit-identity of the verbs without --ras -----------------------------------------------------------------------

    /// <summary>The SHA-256 of a CLI result document with its "tool" line (the build stamp) and its "buildStatus" line left out.</summary>
    private static string ShaWithoutTool(byte[] bytes)
    {
        string[] lines = Rc5LineEndings.ToCrLf(Encoding.UTF8.GetString(bytes)).Split('\n');
        string kept = string.Join('\n', lines.Where(l => !l.StartsWith("  \"tool\": ", StringComparison.Ordinal) && !l.StartsWith("  \"buildStatus\": ", StringComparison.Ordinal)));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(kept)));
    }

    // Pinned from the 2.1.131 Release CLI (dev main 591aaca7) before this package's edits, run in a fresh folder with the
    // licence isolated: "nminus1 <case> --json n1.json" and "nminus2 <case> --json n2.json" (the result file's name is in its
    // "args" list, so the run here names it the same way); the JSON without its tool line, and the whole standard output.
    [Theory]
    [InlineData("nminus1", "case14", "b72cea310a3d051ad17bc8703b6b9845bb2f866dc04166080ea7fca477b1bcad", "c25c7cd3f1796e20a2b3a142049800a3030f08fe4ee7000517bd5109b2cee913")]
    [InlineData("nminus1", "case118", "d446974d6d81d91e10bd99947105d2d4402d14f77d678afe2b3afb71e68fe51d", "b09ae403af4a1e7bb35913e6f132dc7f50d537467ba7d65b17669ba4b1fd325b")]
    [InlineData("nminus2", "case14", "5ca81fe7f9b46e7efe31078efe1e98587bd096a823f7edb266b9ab24e8d92fea", "d9ecbf6c08cb1c4ee00048ff78d0683b80fb0fe87617167205825d35d209ac58")]
    public void T12_the_screens_without_ras_write_the_2_1_131_bytes(string verb, string caseName, string jsonSha, string stdoutSha)
    {
        string w = Path.Combine(_dir, verb + "-" + caseName);
        Directory.CreateDirectory(w);
        string file = verb == "nminus1" ? "n1.json" : "n2.json";
        CliProcess.Result r = JournalTestSupport.Run(w, verb, caseName, "--json", file);
        Assert.True(r.ExitCode == 3, r.ToString());
        Assert.Equal(jsonSha, ShaWithoutTool(File.ReadAllBytes(Path.Combine(w, file))));
        Assert.Equal(stdoutSha, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Rc5LineEndings.ToCrLf(r.StdOut)))));
    }

    // ---- the run journal records the scheme file, and validate re-runs the job ----------------------------------------

    [Fact]
    public void T13_the_journal_hashes_the_ras_file_and_validate_re_runs_the_job_identical()
    {
        string w = Work("journal");
        string journal = Path.Combine(_dir, "j", "journal.jsonl");
        CliProcess.Result run = JournalTestSupport.Run(w, "--journal", journal, "nminus1", "two.m", "--ras", "r.json", "--json", "o.json");
        Assert.True(run.ExitCode == 3, run.ToString());
        JournalEntry e = JournalJson.Parse(File.ReadAllLines(journal)[0], out _)!;
        string sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(w, "r.json"))));
        Assert.Contains(e.Tokens, t => t.Kind == "input" && t.Raw == "r.json" && t.Sha256 == sha);
        Assert.Equal("safe", e.ReplayClass);

        JournalTestSupport.FromAnotherBuild(journal);
        string report = Path.Combine(_dir, "v.json");
        CliProcess.Result v = JournalTestSupport.Run(_dir, "breaking-change", "validate", "--journal", journal, "--json", report);
        Assert.True(v.ExitCode == 0, v.ToString());
        using JsonDocument d = JsonDocument.Parse(File.ReadAllBytes(report));
        JsonElement entry = Assert.Single(d.RootElement.GetProperty("tables").GetProperty("entries").EnumerateArray());
        Assert.Equal(ReplayStatus.Identical, entry.GetProperty("status").GetString());

        // An edited scheme file is detected by its hash: the job is not re-run against the new file.
        string rasPath = Path.Combine(w, "r.json");
        File.WriteAllText(rasPath, TwoBusRas.Replace("\"mw\": 100", "\"mw\": 90", StringComparison.Ordinal));
        string shaNow = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(rasPath)));
        Assert.NotEqual(sha, shaNow);
        string report2 = Path.Combine(_dir, "v2.json");
        CliProcess.Result v2 = JournalTestSupport.Run(_dir, "breaking-change", "validate", "--journal", journal, "--json", report2);
        Assert.True(v2.ExitCode == 1, v2.ToString());
        using JsonDocument d2 = JsonDocument.Parse(File.ReadAllBytes(report2));
        JsonElement edited = Assert.Single(d2.RootElement.GetProperty("tables").GetProperty("entries").EnumerateArray());
        Assert.Equal(ReplayStatus.CannotReplay, edited.GetProperty("status").GetString());
        string reason = edited.GetProperty("reason").GetString()!;
        Assert.StartsWith("input changed: ", reason, StringComparison.Ordinal);
        Assert.EndsWith($"r.json sha256 {sha} != {shaNow}", reason, StringComparison.Ordinal);
    }
}
