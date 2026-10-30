using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N173 (October 2026): <see cref="AcOptimalPowerFlow.Solve"/> stops at pass 0 when that pass's DC dispatch is infeasible
/// in a way no later pass can repair (the loss loop only raises demand). These tests pin each proved case (capability,
/// dead island, line limits with zero output allowed), each case that is NOT proved and so keeps the full loop
/// (demand below total Pmin, line limits with a unit's Pmin above 0, a negative resistance), the multi-island path, and
/// that a converged run is untouched.
/// </summary>
public class N173AcopfEarlyStopTests
{
    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

    private static GridModel WithPmin(GridModel m, double pmin)
        => m.CopyWith(generators: m.Generators.Select(g => g with { Pmin = pmin }).ToList());

    private static GridModel ScaledLoads(GridModel m, double s)
        => m.CopyWith(buses: m.Buses.Select(b => b with { Pd = b.Pd * s, Qd = b.Qd * s }).ToList());

    /// <summary>case9 with its three generator transformers (1-4, 3-6, 8-2) rated 50 MVA: 150 MW deliverable for 315 MW of
    /// demand while 820 MW of capability passes the merit check, so the security LP itself is Infeasible.</summary>
    private static GridModel Case9GeneratorLinksAt50()
    {
        GridModel m = IeeeCases.Case9();
        var branches = m.Branches.Select(b =>
            (b.From, b.To) is (1, 4) or (3, 6) or (8, 2) ? b with { RateAMva = 50 } : b).ToList();
        return m.CopyWith(branches: branches);
    }

    private static void AssertStoppedAtPass0(GridModel m, AcOpfResult r)
    {
        Assert.False(r.Converged);
        Assert.True(r.StoppedAtInfeasibleFirstPass);
        Assert.Equal(AcOpfFailure.DcDispatchInfeasible, r.Failure);
        Assert.Null(r.AcFailure);
        Assert.Equal(0, r.Iterations);
        Assert.True(double.IsNaN(r.TotalLossMw));
        Assert.True(double.IsNaN(r.TotalCostPerHour));
        // The dispatch is pass 0's: the DC-OPF of the unscaled model, bit for bit.
        DcOptimalPowerFlow.DcOpfResult dc = DcOptimalPowerFlow.Solve(m);
        Assert.False(dc.Feasible);
        Assert.Equal(dc.Dispatch.Count, r.Dispatch.Count);
        for (int i = 0; i < dc.Dispatch.Count; i++) Assert.Equal(Bits(dc.Dispatch[i].Pg), Bits(r.Dispatch[i].Pg));
    }

    [Fact]
    public void Demand_above_total_capability_stops_at_pass_0()
    {
        // case9 at 3x load: 945 MW of demand against 250 + 300 + 270 = 820 MW of Pmax. More demand cannot cure it.
        GridModel m = ScaledLoads(IeeeCases.Case9(), 3.0);
        AssertStoppedAtPass0(m, AcOptimalPowerFlow.Solve(m));
    }

    [Fact]
    public void Line_limits_proved_infeasible_stop_at_pass_0_when_every_unit_may_produce_nothing()
    {
        GridModel m = Case9GeneratorLinksAt50();
        m = m.CopyWith(generators: m.Generators.Select(g => g with { Pmin = 0 }).ToList());
        Assert.Equal(LpStatus.Infeasible, DcOptimalPowerFlow.Solve(m).SecurityLpStatus);
        AssertStoppedAtPass0(m, AcOptimalPowerFlow.Solve(m));
    }

    [Fact]
    public void Line_limits_with_a_unit_whose_pmin_is_above_zero_keep_the_full_loop()
    {
        // Every unit given Pmin 10 MW, so s = 0 is not a feasible point and the interval argument does not apply.
        GridModel m = WithPmin(Case9GeneratorLinksAt50(), 10);
        Assert.Equal(LpStatus.Infeasible, DcOptimalPowerFlow.Solve(m).SecurityLpStatus);
        AcOpfResult r = AcOptimalPowerFlow.Solve(m);
        Assert.False(r.Converged);
        Assert.False(r.StoppedAtInfeasibleFirstPass);
    }

    [Fact]
    public void Demand_below_total_pmin_keeps_the_full_loop()
    {
        // case9 at 5 % load (15.75 MW) with every unit's Pmin set to 20 MW (60 MW in all). Infeasible at pass 0, but more
        // demand could cure it.
        GridModel m = WithPmin(ScaledLoads(IeeeCases.Case9(), 0.05), 20);
        Assert.False(DcOptimalPowerFlow.Solve(m).Feasible);
        Assert.False(AcOptimalPowerFlow.Solve(m).StoppedAtInfeasibleFirstPass);
    }

    [Fact]
    public void A_negative_resistance_keeps_the_full_loop()
    {
        // A negative R could make a loss estimate negative and so lower the demand of a later pass: nothing is proved.
        GridModel m = ScaledLoads(IeeeCases.Case9(), 3.0);
        var branches = m.Branches.Select((b, i) => i == 1 ? b with { R = -0.001 } : b).ToList();
        m = m.CopyWith(branches: branches);
        Assert.False(AcOptimalPowerFlow.Solve(m).StoppedAtInfeasibleFirstPass);
    }

    [Fact]
    public void A_negative_shunt_conductance_keeps_the_full_loop()
    {
        GridModel m = ScaledLoads(IeeeCases.Case9(), 3.0);
        var buses = m.Buses.Select((b, i) => i == 4 ? b with { Gs = -1.0 } : b).ToList();
        m = m.CopyWith(buses: buses);
        Assert.False(AcOptimalPowerFlow.Solve(m).StoppedAtInfeasibleFirstPass);
    }

    /// <summary>case9 twice as two islands (the copy's bus ids + 100), the copy's loads scaled by <paramref name="s"/>.</summary>
    private static GridModel TwoIslandCase9(double s)
    {
        GridModel a = IeeeCases.Case9();
        var buses = a.Buses.ToList();
        var gens = a.Generators.ToList();
        var branches = a.Branches.ToList();
        buses.AddRange(a.Buses.Select(b => b with { Id = b.Id + 100, Pd = b.Pd * s, Qd = b.Qd * s }));
        gens.AddRange(a.Generators.Select(g => g with { Bus = g.Bus + 100 }));
        branches.AddRange(a.Branches.Select(b => b with { From = b.From + 100, To = b.To + 100 }));
        return a.CopyWith(buses: buses, generators: gens, branches: branches);
    }

    [Fact]
    public void An_island_above_its_capability_stops_a_multi_island_run_at_pass_0()
    {
        GridModel m = TwoIslandCase9(3.0);
        Assert.Equal(2, DistributionFactors.FindIslands(m).Count);
        AssertStoppedAtPass0(m, AcOptimalPowerFlow.Solve(m));
    }

    [Fact]
    public void A_dead_island_with_demand_stops_a_multi_island_run_at_pass_0()
    {
        // The copy loses its units: nothing can ever supply its 315 MW.
        GridModel m = TwoIslandCase9(1.0);
        m = m.CopyWith(generators: m.Generators.Where(g => g.Bus < 100).ToList());
        Assert.Equal(2, DistributionFactors.FindIslands(m).Count);
        AssertStoppedAtPass0(m, AcOptimalPowerFlow.Solve(m));
    }

    // Moved 2026-10 (N109, Ruling R1, Mark delegated 2026-10-04): gb-synthetic's security LP was Infeasible only because of
    // 53 rated lines no dispatch can relieve; the DC-OPF now leaves them out and names them, so its LP is Optimal and the
    // loss loop runs. Before: LP Infeasible, acopf stopped at pass 0 (DcDispatchInfeasible), CLI exit 3 with the pass-0
    // message. After: LP Optimal with 53 lines left out at pass 0 (86 at the final, loss-inflated pass), acopf converges
    // in 5 passes (losses 1425.828 MW, cost 686,468 GBP/h in the CLI), CLI exit 0 naming the left-out lines. The pass-0 stop itself stays pinned on case9 above.
    // Moved 2026-10 (N204, Ruling R1 follow-up, Mark delegated 2026-10-04): the gb-synthetic builder now sizes its radial
    // ladder to its peak demand, so 51 of the 53 unrelievable lines (radial branches below their own demand) are gone.
    // Before -> after: lines left out at pass 0 53 -> 2 (MID SGT 400/275 1, LDN SGT 400/275 1); acopf passes 5 -> 4;
    // left out at the final pass 86 -> 5; CLI "left out 86" -> "left out 5", "losses 1425.828 MW" -> "losses 1385.085 MW",
    // "converged in 5 iteration(s)" -> "converged in 4 iteration(s)"; exit 0 unchanged.
    [Fact]
    public void Gb_synthetic_stops_at_pass_0_with_the_same_verdict()
    {
        GridModel m = GridSim.Core.Synthetic.SyntheticGb.Build(new());
        DcOptimalPowerFlow.DcOpfResult dc = DcOptimalPowerFlow.Solve(m);
        Assert.Equal(LpStatus.Optimal, dc.SecurityLpStatus);
        Assert.Equal(2, dc.UnrelievableLines.Count);
        AcOpfResult r = AcOptimalPowerFlow.Solve(m);
        Assert.True(r.Converged);
        Assert.False(r.StoppedAtInfeasibleFirstPass);
        // Moved 2026-10 (N240, Ruling R1: Mark delegated 2026-10-04; main ruled a fixed, documented tie-break in the DC-OPF
        // simplex): gb-synthetic's DC-OPF is degenerate, and each loss pass now stops at the lowest-index optimal vertex (see
        // the gb-synthetic row of DcOpfSecuritySimplexParityTests). A different dispatch has different AC losses, so the loss
        // loop settles in 6 passes instead of 4 (losses 1,385.085 -> 1,235.507 MW, cost 684,072 -> 675,277 GBP/h). Before: 4.
        Assert.Equal(6, r.Iterations);
        Assert.Equal(5, r.UnrelievableLines.Count);   // the final pass, at the loss-inflated demand
    }

    [Fact]
    public void Cli_acopf_gb_synthetic_names_the_pass_0_stop_and_exits_3()
    {
        CliProcess.Result r = CliProcess.RunBuilt("acopf", "gb-synthetic");
        Assert.Equal(0, r.ExitCode);
        // Moved 2026-10 (LF7 N316, Ruling R1: one name, one case; Mark delegated EE calls 2026-10-04): the CLI name
        // gb-synthetic is now the shipped data/gb-synthetic folder for every verb, not the in-memory builder. The folder
        // is the builder's network field for field (R1EGbSyntheticTests) but in another branch order (lines before
        // transformers), and this DC-OPF is degenerate: the N240 lowest-index tie-break picks its optimal vertex by
        // variable index, so the folder's order can stop each loss pass at another vertex of the same cost, and the AC
        // losses, the pass count and the final pass's left-out lines follow that vertex. The builder's own figures stay
        // pinned in Gb_synthetic_stops_at_pass_0_with_the_same_verdict above (6 passes, 5 lines left out at the final pass).
        // Before (the builder through the CLI): "the final DC pass left out 5 rated line(s) no dispatch can bring within
        // rating" and "converged in 6 iteration(s)  losses 1235.507 MW". After: whatever `acopf data/gb-synthetic` (the folder)
        // prints, which this test now requires the name to print line for line; the folder's exact figures are captured
        // at the final batch (not derivable without running) and pinned here then, as an Assert.Contains on each line.
        CliProcess.Result byFolder = CliProcess.RunBuilt("acopf",
            Path.Combine(Path.GetDirectoryName(CliProcess.BuiltCliDll())!, "data", "gb-synthetic"));   // the CLI's own shipped folder
        Assert.Equal(0, byFolder.ExitCode);
        static string Line(string stdout, string start) => stdout.Split('\n').Select(l => l.TrimEnd('\r'))
            .Single(l => l.TrimStart().StartsWith(start, StringComparison.Ordinal));
        Assert.Equal(Line(byFolder.StdOut, "the final DC pass left out"), Line(r.StdOut, "the final DC pass left out"));
        Assert.Contains("rated line(s) no dispatch can bring within rating", r.StdOut, StringComparison.Ordinal);
        Assert.Equal(Line(byFolder.StdOut, "converged in"), Line(r.StdOut, "converged in"));
        Assert.DoesNotContain("no later pass can be feasible", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Feasible_runs_are_untouched()
    {
        // The N070 / N111 pins (case14: 2 passes, loss 15.85798410498625 MW, cost 13742.899205249312; two case9 islands
        // at 1.0x converge) with the new flag false.
        AcOpfResult a = AcOptimalPowerFlow.Solve(IeeeCases.Case14());
        Assert.True(a.Converged);
        Assert.False(a.StoppedAtInfeasibleFirstPass);
        Assert.Equal(2, a.Iterations);
        Assert.Equal(Bits(15.85798410498625), Bits(a.TotalLossMw));
        Assert.Equal(Bits(13742.899205249312), Bits(a.TotalCostPerHour));
        AcOpfResult b = AcOptimalPowerFlow.Solve(TwoIslandCase9(1.0));
        Assert.True(b.Converged);
        Assert.False(b.StoppedAtInfeasibleFirstPass);
    }
}
