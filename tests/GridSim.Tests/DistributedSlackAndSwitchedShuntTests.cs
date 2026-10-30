using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F22 (October 2026): the distributed slack (participation factors) and discrete switched shunts, as outer loops around
/// the unchanged Newton engines. Every check runs on BOTH engines and requires the two to agree to the bit (the loops
/// add only plain arithmetic in model order on top of engine solves that are themselves bit-identical).
/// </summary>
public class DistributedSlackAndSwitchedShuntTests
{
    private static readonly PowerFlowOptions Opt = new() { MaxIterations = 100, EnforceQLimits = true };

    private static GridModel Case(string rel) => S10TestSupport.LoadMatpower(rel).Model;

    // ---- Distributed slack -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("lib/case9.m", ParticipationRule.Pmax)]
    [InlineData("lib/case14.m", ParticipationRule.Equal)]
    [InlineData("lib/case30.m", ParticipationRule.Pg)]
    [InlineData("lib/case118.m", ParticipationRule.Pmax)]
    public void The_distributed_slack_settles_shares_the_imbalance_by_factor_and_is_bit_identical_on_both_engines(string rel, ParticipationRule rule)
    {
        GridModel m = Case(rel);
        var dense = new DistributedSlackPowerFlow(new NewtonRaphsonPowerFlow(), rule);
        var sparse = new DistributedSlackPowerFlow(new SparseNewtonRaphsonPowerFlow(), rule);
        PowerFlowResult d = dense.Solve(m, Opt);
        PowerFlowResult s = sparse.Solve(m, Opt);
        S10TestSupport.AssertBitIdentical(rel + " distributed slack", d, s);
        Assert.Equal(dense.LastPasses, sparse.LastPasses);
        Assert.Equal(BitConverter.DoubleToInt64Bits(dense.LastImbalanceMw), BitConverter.DoubleToInt64Bits(sparse.LastImbalanceMw));
        Assert.True(d.Converged, $"{rel}: {dense.LastNote}");
        Assert.Null(dense.LastNote);

        // Generation = demand + losses.
        double demand = m.Buses.Sum(b => b.Pd);
        double gen = dense.LastDispatchMw.Sum();
        Assert.True(Math.Abs(gen - (demand + d.TotalLossMw)) <= 1e-3, $"{rel}: generation {gen:R} vs demand + losses {demand + d.TotalLossMw:R}");

        // The reported dispatch is exactly the allocation of the reported imbalance K, all of K was placed, and every
        // unit off its limits moved in proportion to its factor (the same K / alpha ratio for all of them).
        double k = dense.LastImbalanceMw;
        double[] pg0 = m.Generators.Select(g => g.Pg).ToArray();
        double[] alpha = dense.LastFactors.ToArray();
        (double[] expect, double unplaced) = DistributedSlackPowerFlow.Allocate(pg0, alpha,
            m.Generators.Select(g => g.Pmin).ToArray(), m.Generators.Select(g => g.Pmax).ToArray(), k);
        Assert.Equal(expect.Select(BitConverter.DoubleToInt64Bits), dense.LastDispatchMw.Select(BitConverter.DoubleToInt64Bits));
        Assert.True(Math.Abs(unplaced) <= DistributedSlackPowerFlow.ToleranceMw);
        double? ratio = null;
        for (int i = 0; i < m.Generators.Count; i++)
        {
            Generator g = m.Generators[i];
            double p = dense.LastDispatchMw[i];
            if (alpha[i] == 0.0 || p == g.Pmin || p == g.Pmax) continue;
            double r = (p - g.Pg) / alpha[i];
            ratio ??= r;
            Assert.True(Math.Abs(r - ratio.Value) <= 1e-9 * Math.Max(1.0, Math.Abs(ratio.Value)), $"{rel} gen {i}: share ratio {r:R} vs {ratio:R}");
        }
    }

    [Fact]
    public void With_only_the_reference_unit_taking_part_it_is_the_single_slack_solve_bit_for_bit()
    {
        // The reference bus's own output never enters the Newton equations, so allocating the whole imbalance to its
        // unit changes nothing the engines solve: the result must be the plain engine's, to the bit.
        GridModel m = Case("lib/case30.m");
        int slack = m.Buses.First(b => b.Type == BusType.Slack).Id;
        double[] factors = m.Generators.Select(g => g.Bus == slack ? 1.0 : 0.0).ToArray();
        foreach (IPowerFlowSolver engine in new IPowerFlowSolver[] { new NewtonRaphsonPowerFlow(), new SparseNewtonRaphsonPowerFlow() })
        {
            PowerFlowResult plain = engine.Solve(m, Opt);
            var ds = new DistributedSlackPowerFlow(engine, factors);
            PowerFlowResult r = ds.Solve(m, Opt);
            S10TestSupport.AssertBitIdentical("reference-only distributed slack", plain, r);
            Assert.True(ds.LastPasses <= 2);
        }
    }

    [Fact]
    public void A_model_with_two_reference_buses_is_refused()
    {
        GridModel m = Case("lib/case9.m");
        var buses = m.Buses.Select(b => b.Id == 2 ? b with { Type = BusType.Slack } : b).ToList();
        var ds = new DistributedSlackPowerFlow(new SparseNewtonRaphsonPowerFlow(), ParticipationRule.Equal);
        var ex = Assert.Throws<InvalidOperationException>(() => ds.Solve(m.CopyWith(buses: buses), Opt));
        Assert.Contains("2 reference (slack) buses", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_allocation_fills_by_factor_and_holds_units_at_their_limits()
    {
        // Three units, factors 0.5 / 0.25 / 0.25, 40 MW to share; unit 1 has only 5 MW of room.
        double[] pg0 = { 10, 10, 10 };
        double[] alpha = { 0.5, 0.25, 0.25 };
        double[] lo = { 0, 0, 0 };
        double[] hi = { 100, 15, 100 };
        (double[] pg, double unplaced) = DistributedSlackPowerFlow.Allocate(pg0, alpha, lo, hi, 40);
        Assert.Equal(0.0, unplaced, 12);
        Assert.Equal(15.0, pg[1], 12);                 // held at Pmax
        Assert.Equal(10.0 + 35.0 * 0.5 / 0.75, pg[0], 12);   // the rest of the 40 MW, 2:1
        Assert.Equal(10.0 + 35.0 * 0.25 / 0.75, pg[2], 12);
        // Nothing left to place it on: reported, not dropped.
        (_, double left) = DistributedSlackPowerFlow.Allocate(pg0, alpha, lo, new double[] { 20, 15, 20 }, 40);
        Assert.Equal(15.0, left, 12);
    }

    [Fact]
    public void Bad_factors_are_refused()
    {
        GridModel m = Case("lib/case9.m");
        Assert.Throws<ArgumentException>(() => new DistributedSlackPowerFlow(new NewtonRaphsonPowerFlow(), new double[] { 1, 2 }).Solve(m, Opt));
        Assert.Throws<ArgumentException>(() => new DistributedSlackPowerFlow(new NewtonRaphsonPowerFlow(), new double[] { 1, -1, 1 }).Solve(m, Opt));
        Assert.Throws<InvalidOperationException>(() => new DistributedSlackPowerFlow(new NewtonRaphsonPowerFlow(), new double[] { 0, 0, 0 }).Solve(m, Opt));
    }

    // ---- Switched shunts ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_bank_below_its_band_switches_in_until_the_voltage_is_inside_and_both_engines_agree()
    {
        GridModel m = Case("lib/case14.m");
        PowerFlowResult plain = new SparseNewtonRaphsonPowerFlow().Solve(m, Opt);
        int bi = plain.BusIds.ToList().IndexOf(14);
        double v0 = plain.Vm[bi];
        // A band just above the present voltage, with 5 MVAr steps: the bank must switch in and lift bus 14 into it.
        var bank = new SwitchedShunt(14, 5.0, 0, 10, 0, v0 + 0.005, v0 + 0.05, "C14");
        var dense = new SwitchedShuntPowerFlow(new NewtonRaphsonPowerFlow(), new[] { bank });
        var sparse = new SwitchedShuntPowerFlow(new SparseNewtonRaphsonPowerFlow(), new[] { bank });
        PowerFlowResult d = dense.Solve(m, Opt);
        PowerFlowResult s = sparse.Solve(m, Opt);
        S10TestSupport.AssertBitIdentical("switched shunt", d, s);
        Assert.Equal(dense.LastSteps, sparse.LastSteps);
        Assert.True(d.Converged);
        Assert.True(dense.ShuntsSettled, dense.LastNote);
        Assert.True(dense.LastSteps[0] > 0);
        Assert.InRange(d.Vm[bi], bank.VminPu, bank.VmaxPu);
        // The same state solved by hand with the bank's final steps in Bs.
        GridModel byHand = m.CopyWith(buses: m.Buses.Select(b => b.Id == 14 ? b with { Bs = b.Bs + dense.LastSteps[0] * 5.0 } : b).ToList());
        S10TestSupport.AssertBitIdentical("switched shunt by hand", new SparseNewtonRaphsonPowerFlow().Solve(byHand, Opt), s);
    }

    [Fact]
    public void A_bank_inside_its_band_does_nothing_and_the_solve_is_the_engines_own()
    {
        GridModel m = Case("lib/case14.m");
        PowerFlowResult plain = new SparseNewtonRaphsonPowerFlow().Solve(m, Opt);
        var bank = new SwitchedShunt(14, 5.0, 0, 10, 0, 0.5, 1.5);
        var ss = new SwitchedShuntPowerFlow(new SparseNewtonRaphsonPowerFlow(), new[] { bank });
        S10TestSupport.AssertBitIdentical("idle bank", plain, ss.Solve(m, Opt));
        Assert.Equal(1, ss.LastPasses);
        Assert.True(ss.ShuntsSettled);
    }

    [Fact]
    public void A_band_narrower_than_one_step_is_reported_not_settled()
    {
        GridModel m = Case("lib/case14.m");
        PowerFlowResult plain = new SparseNewtonRaphsonPowerFlow().Solve(m, Opt);
        double v0 = plain.Vm[plain.BusIds.ToList().IndexOf(14)];
        // A 50 MVAr step against a 0.0001 pu band just above the voltage: one step overshoots, the next undershoots.
        var bank = new SwitchedShunt(14, 50.0, -10, 10, 0, v0 + 0.00005, v0 + 0.00015);
        var ss = new SwitchedShuntPowerFlow(new SparseNewtonRaphsonPowerFlow(), new[] { bank });
        PowerFlowResult r = ss.Solve(m, Opt);
        Assert.True(r.Converged);
        Assert.False(ss.ShuntsSettled);
        Assert.NotNull(ss.LastNote);
    }

    [Theory]
    [InlineData("""{ "shunts": [] }""", "\"format\" must be")]
    [InlineData("""{ "format": "gridsim-switched-shunts/1", "shunts": [ { "bus": 1 } ] }""", "are required")]
    [InlineData("""{ "format": "gridsim-switched-shunts/1", "shunts": [ { "bus": 1, "stepMvar": 5, "maxSteps": 2, "vminPu": 0.9, "vmaxPu": 1.1, "x": 1 } ] }""", "unknown key 'x'")]
    public void A_bad_switched_shunt_file_is_refused(string json, string expected)
    {
        var ex = Assert.Throws<InvalidDataException>(() => SwitchedShuntPowerFlow.ReadFile(json, "s.json"));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_good_switched_shunt_file_reads_and_bad_banks_are_refused()
    {
        IReadOnlyList<SwitchedShunt> s = SwitchedShuntPowerFlow.ReadFile(
            """{ "format": "gridsim-switched-shunts/1", "shunts": [ { "bus": 5, "stepMvar": 10, "maxSteps": 4, "vminPu": 0.97, "vmaxPu": 1.03, "name": "C5" } ] }""", "s.json");
        Assert.Equal(new SwitchedShunt(5, 10, 0, 4, 0, 0.97, 1.03, "C5"), Assert.Single(s));
        Assert.Throws<ArgumentException>(() => new SwitchedShuntPowerFlow(new SparseNewtonRaphsonPowerFlow(), new[] { new SwitchedShunt(5, 0, 0, 4, 0, 0.97, 1.03) }));
        Assert.Throws<ArgumentException>(() => new SwitchedShuntPowerFlow(new SparseNewtonRaphsonPowerFlow(), new[] { new SwitchedShunt(5, 10, 0, 4, 5, 0.97, 1.03) }));
        Assert.Throws<ArgumentException>(() => new SwitchedShuntPowerFlow(new SparseNewtonRaphsonPowerFlow(), new[] { new SwitchedShunt(5, 10, 0, 4, 0, 1.03, 0.97) }));
    }

    [Fact]
    public void A_shortfall_with_every_unit_at_its_limit_stops_at_once_with_the_at_limit_note()
    {
        // GR-052: every unit 20 MW below its schedule and capped there, so about 60 MW of demand plus losses can never
        // be shared. The loop used to burn all 50 passes, grow K by the shortfall each pass and end with the generic
        // "did not settle" note; it now stops on the second pass (the first that sees every unit bound).
        GridModel m0 = Case("lib/case9.m");
        GridModel m = m0.CopyWith(generators: m0.Generators.Select(g => g with { Pg = g.Pg - 20.0, Pmax = g.Pg - 20.0 }).ToArray());
        var ds = new DistributedSlackPowerFlow(new SparseNewtonRaphsonPowerFlow(), ParticipationRule.Equal);
        PowerFlowResult r = ds.Solve(m, Opt);
        Assert.False(r.Converged);
        Assert.Equal(PowerFlowStatus.MaxIterations, r.Status);
        Assert.Equal(2, ds.LastPasses);
        Assert.NotNull(ds.LastNote);
        Assert.Contains("every participating unit is at a limit", ds.LastNote, StringComparison.Ordinal);
        // K is the first pass's excess, not 50 accumulated shortfalls.
        Assert.InRange(ds.LastImbalanceMw, 55.0, 70.0);
    }

    // ---- CLI wiring (source level) ---------------------------------------------------------------------------------

    [Fact]
    public void The_pf_verb_and_its_flags_are_known_and_documented()
    {
        string root = CliProcess.Root();
        string src = File.ReadAllText(Path.Combine(root, "src", "GridSim.Cli", "Program.cs"));
        Assert.Contains("\"--distributed-slack\",", src, StringComparison.Ordinal);
        Assert.Contains("\"--switched-shunts\",", src, StringComparison.Ordinal);
        Assert.Contains("rest[0].Equals(\"pf\", StringComparison.OrdinalIgnoreCase)", src, StringComparison.Ordinal);
        string ch19 = File.ReadAllText(Path.Combine(root, "docs", "manual", "0019 CLI reference.md"));
        Assert.Contains("### pf", ch19, StringComparison.Ordinal);
        Assert.Contains("gridsim-switched-shunts/1", ch19, StringComparison.Ordinal);
    }
}
