using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Core.Synthetic;
using GridSim.Gda;
using GridSim.Gda.Ltds;
using GridSim.Gda.Npg;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N109 (Ruling R1, Mark delegated 2026-10-04): the security DC-OPF names and leaves out the rated lines no dispatch can
/// relieve (as the corrective redispatch leaves out what no generator can move), and lists them in the result.
/// </summary>
public sealed class N109UnrelievableLinesTests
{
    private static string Root() => CliProcess.Root();

    // =============================================================================================================
    // N109: the security DC-OPF names and leaves out lines no dispatch can relieve
    // =============================================================================================================

    /// <summary>Two buses joined by one line rated 10 MW; 100 MW of load at bus 2. Gen A at bus 1 (the reference,
    /// 0..200 MW), gen B at bus 2 (0..<paramref name="pmaxB"/> MW). The line carries 100 - PgB, so with pmaxB 50 its
    /// flow is 50..100 MW whatever the dispatch: no dispatch can bring it within 10 MW.</summary>
    private static GridModel TwoBus(double pmaxB, double rate = 10.0) => new()
    {
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 100) },
        Generators = new[]
        {
            new Generator(1, Pg: 0, Qg: 0, Vg: 1.0, Qmin: -100, Qmax: 100, Pmin: 0, Pmax: 200, Type: "ccgt"),
            new Generator(2, Pg: 0, Qg: 0, Vg: 1.0, Qmin: -100, Qmax: 100, Pmin: 0, Pmax: pmaxB, Type: "nuclear"),
        },
        Branches = new[] { new Branch(1, 2, 0.01, 0.1, RateAMva: rate, Name: "L12") },
    };

    [Fact]
    public void N109_a_line_no_dispatch_can_relieve_is_left_out_named_and_still_reported_overloaded()
    {
        DcOptimalPowerFlow.DcOpfResult r = DcOptimalPowerFlow.Solve(TwoBus(pmaxB: 50));
        // Before N109 this LP was Infeasible (the one row cannot be met). Now the row is left out and the LP is Optimal.
        Assert.Equal(LpStatus.Optimal, r.SecurityLpStatus);
        Assert.True(r.Feasible);
        DcOptimalPowerFlow.UnrelievableLine u = Assert.Single(r.UnrelievableLines);
        Assert.Equal(0, u.Branch);
        Assert.Equal("L12", u.Name);
        Assert.Equal(10.0, u.RateMw);
        Assert.Equal(50.0, u.ReachableMinMw, 9);
        Assert.Equal(100.0, u.ReachableMaxMw, 9);
        // Cheapest dispatch without the row: nuclear (10 GBP/MWh) to its 50 MW, the CCGT the rest. The line is still over.
        Assert.Equal(50.0, r.Dispatch[1].Pg, 9);
        Assert.Equal(50.0, r.Dispatch[0].Pg, 9);
        DcOptimalPowerFlow.LineBinding b = Assert.Single(r.BindingLines);
        Assert.True(Math.Abs(b.FlowMw) > b.RateMw);
    }

    [Fact]
    public void N109_a_relievable_line_is_kept_and_nothing_is_reported()
    {
        // Gen B can reach 95 MW, so the line can be held within 10 MW (B from 90 to 95): the LP keeps the row, and the
        // cheaper unit B runs at its 95 MW Pmax (line flow 5 MW).
        DcOptimalPowerFlow.DcOpfResult r = DcOptimalPowerFlow.Solve(TwoBus(pmaxB: 95));
        Assert.Empty(r.UnrelievableLines);
        Assert.Equal(LpStatus.Optimal, r.SecurityLpStatus);
        Assert.Equal(95.0, r.Dispatch[1].Pg, 6);
    }

    [Fact]
    public void N109_the_dense_reference_and_the_cost_curve_path_leave_out_the_same_line()
    {
        GridModel m = TwoBus(pmaxB: 50);
        DcOptimalPowerFlow.DcOpfResult structured = DcOptimalPowerFlow.Solve(m);
        DcOptimalPowerFlow.DcOpfResult dense = DcOptimalPowerFlow.SolveDenseReference(m);
        Assert.Equal(structured.UnrelievableLines, dense.UnrelievableLines);
        Assert.Equal(structured.Dispatch.Select(d => BitConverter.DoubleToInt64Bits(d.Pg)), dense.Dispatch.Select(d => BitConverter.DoubleToInt64Bits(d.Pg)));

        var costed = m.CopyWith(generators: m.Generators.Select((g, i) => g with
        {
            Cost = new GeneratorCost(GeneratorCostModel.Polynomial, 0, 0, new[] { i == 0 ? 65.0 : 10.0, 0.0 }),
        }).ToList());
        DcOptimalPowerFlow.DcOpfResult c = DcOptimalPowerFlow.Solve(costed, new DcOptimalPowerFlow.DcOpfOptions { UseGeneratorCosts = true });
        Assert.Equal(structured.UnrelievableLines, c.UnrelievableLines);
        Assert.True(c.Feasible);
    }

    [Fact]
    public void N109_islands_report_left_out_lines_with_full_model_branch_indices()
    {
        // Island 1: case-like secure two-bus pair; island 2: the unrelievable two-bus pair, ids + 10.
        GridModel a = TwoBus(pmaxB: 95);
        GridModel b = TwoBus(pmaxB: 50);
        var buses = a.Buses.Concat(b.Buses.Select(x => x with { Id = x.Id + 10 })).ToList();
        var gens = a.Generators.Concat(b.Generators.Select(g => g with { Bus = g.Bus + 10 })).ToList();
        var branches = a.Branches.Concat(b.Branches.Select(x => x with { From = x.From + 10, To = x.To + 10, Name = "L1112" })).ToList();
        GridModel m = a.CopyWith(buses: buses, generators: gens, branches: branches);
        DcOptimalPowerFlow.DcOpfResult r = DcOptimalPowerFlow.Solve(m);
        Assert.Equal(2, r.Islands!.Count);
        DcOptimalPowerFlow.UnrelievableLine u = Assert.Single(r.UnrelievableLines);
        Assert.Equal(1, u.Branch);
        Assert.Equal("L1112", u.Name);
        Assert.True(r.Feasible);
    }

    [Theory]
    [InlineData("lib/case24_ieee_rts.m")]
    [InlineData("nesta/pglib_opf_case5_pjm.m")]
    [InlineData("lib/case118.m")]
    public void N109_bundled_cases_whose_lp_was_feasible_leave_nothing_out(string rel)
    {
        GridModel m = GridSim.Core.IO.Matpower.MatpowerUnitNormalizer.Normalize(
            GridSim.Core.IO.Matpower.MatpowerParser.ParseFile(Path.Combine(Root(), "data", "matpower", rel)));
        DcOptimalPowerFlow.DcOpfResult r = DcOptimalPowerFlow.Solve(m);
        Assert.Empty(r.UnrelievableLines);
    }

    [Fact]
    public void N109_a_jointly_infeasible_case_still_prints_the_security_lp_infeasible_line()
    {
        // case9 with its three generator links rated 50 MVA: each link alone can be held within 50 MW (a unit's output
        // range includes it), so nothing is left out, but 150 MW cannot meet 315 MW: the LP is Infeasible, as before.
        GridModel m = IeeeCases.Case9();
        m = m.CopyWith(branches: m.Branches.Select(b => (b.From, b.To) is (1, 4) or (3, 6) or (8, 2) ? b with { RateAMva = 50 } : b).ToList(),
                       generators: m.Generators.Select(g => g with { Pmin = 0 }).ToList());
        DcOptimalPowerFlow.DcOpfResult dc = DcOptimalPowerFlow.Solve(m);
        Assert.Equal(LpStatus.Infeasible, dc.SecurityLpStatus);
        Assert.Empty(dc.UnrelievableLines);

        string dir = Path.Combine(Path.GetTempPath(), "gridsim-n109-" + Guid.NewGuid().ToString("N"));
        try
        {
            GdaModelWriter.WriteDirectory(m, dir, addGridInfeed: false);
            CliProcess.Result r = CliProcess.RunBuilt("dcopf", dir);
            // Exit 3: a jointly infeasible dispatch is a validation failure (N172, Ruling R1, merged with N109 at 1.95.146).
            Assert.True(r.ExitCode == 3, r.ToString());
            Assert.Contains("(security LP Infeasible)", r.StdOut, StringComparison.Ordinal);
            Assert.DoesNotContain("cannot be brought within rating", r.StdOut, StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void N109_the_margin_is_ten_times_the_simplex_phase_one_tolerance()
    {
        Assert.Equal(1e-6, DcOptimalPowerFlow.UnrelievableMarginMw);
        Assert.Equal(10 * BoundedSimplex.PhaseOneFeasibilityTolerance, DcOptimalPowerFlow.UnrelievableMarginMw, 15);
    }
}
