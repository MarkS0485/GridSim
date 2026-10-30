using GridSim.Core.Analysis;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F19 (October 2026): generator cost curves in the DC-OPF objective (MATPOWER gencost and the JSON cost file) and the
/// nodal prices (LMPs) and line shadow prices from the optimum's duals.
///
/// <para><b>References used.</b></para>
/// <list type="bullet">
/// <item>PUBLISHED: the PJM 5-bus economic-dispatch example (F. Li and R. Bo, "Small Test Systems for Power System
/// Economic Studies", IEEE PES General Meeting 2010), shipped as data/matpower/nesta/pglib_opf_case5_pjm.m. The pglib
/// file header records the paper's DC-OPF dispatch in its "Generator Setpoint Replacement Notes" (Pg 40.0, 170.0,
/// 323.49, 0.0, 466.51 MW), to the two decimals printed there.</item>
/// <item>INDEPENDENT SOLVER: the HiGHS solver (highspy 1.x, via a Python script that rebuilds GridSim's DC model:
/// b = 1/x, in-service branches, first slack as reference) on pglib case5_pjm, case3_lmbd, case30_as and MATPOWER case9 /
/// case30. Its prices were taken by central finite difference of the optimal cost (+/- 1e-3 MW at each bus), so they do
/// not depend on any dual sign convention. Values below are its output, printed to 9-12 significant digits. Its QP
/// dispatch on case30 / case30_as was only good to about 4e-4 MW (see those tests: the closed form is used there).</item>
/// <item>CLOSED FORM: case9 has no binding line, so its optimum is the equal-marginal-cost dispatch
/// 2 a_i P_i + b_i = lambda with sum P_i = demand, solved by hand in the test.</item>
/// </list>
/// <para>The published PJM LMPs (16.98, 26.38, 30.00, 39.94, 10.00 $/MWh in the paper's table) are NOT in
/// docs/reference: they are quoted from the paper as remembered and marked [VERIFY]; the HiGHS run gives the same
/// values to every printed digit.</para>
/// </summary>
public class GeneratorCostsAndPricesTests
{
    private static GridModel Case(string rel) => S10TestSupport.LoadMatpower(rel).Model;

    private static readonly DcOptimalPowerFlow.DcOpfOptions Curves = new() { UseGeneratorCosts = true, ComputePrices = true };

    private static double Lmp(DcOptimalPowerFlow.DcOpfResult r, int bus) => r.Prices!.Buses.Single(p => p.BusId == bus).Lmp;

    // ---- Published reference: PJM 5-bus ---------------------------------------------------------------------------

    [Fact]
    public void Pjm_5_bus_dispatch_matches_the_published_dc_opf_and_its_prices_match_highs()
    {
        DcOptimalPowerFlow.DcOpfResult r = DcOptimalPowerFlow.Solve(Case("nesta/pglib_opf_case5_pjm.m"), Curves);
        Assert.True(r.Feasible);
        Assert.True(r.UsedGeneratorCosts);
        Assert.Equal(LpStatus.Optimal, r.SecurityLpStatus);

        // Published (Li and Bo 2010, as recorded in the pglib header), to the 0.01 MW printed there.
        double[] published = { 40.0, 170.0, 323.49, 0.0, 466.51 };
        for (int i = 0; i < 5; i++) Assert.True(Math.Abs(r.Dispatch[i].Pg - published[i]) <= 0.005, $"gen {i}: {r.Dispatch[i].Pg:R}");

        // HiGHS: dispatch and cost.
        double[] highs = { 40.0, 170.0, 323.494846269051, 0.0, 466.505153730949 };
        for (int i = 0; i < 5; i++) Assert.True(Math.Abs(r.Dispatch[i].Pg - highs[i]) <= 1e-6, $"gen {i}: {r.Dispatch[i].Pg:R}");
        Assert.True(Math.Abs(r.TotalCost - 17479.896925381025) <= 1e-6, $"cost {r.TotalCost:R}");

        // HiGHS prices (finite difference), and the paper's printed values [VERIFY: quoted, not in docs/reference].
        double[] lmp = { 16.977358824, 26.384459519, 30.000000001, 39.942736323, 10.000000000 };
        double[] paper = { 16.98, 26.38, 30.00, 39.94, 10.00 };
        for (int b = 1; b <= 5; b++)
        {
            Assert.True(Math.Abs(Lmp(r, b) - lmp[b - 1]) <= 1e-6, $"bus {b}: LMP {Lmp(r, b):R}");
            Assert.True(Math.Abs(Lmp(r, b) - paper[b - 1]) <= 0.005, $"bus {b}: LMP {Lmp(r, b):R}");
        }

        // The one binding line is branch 5 (bus 4 - bus 5, rated 240 MW), held at -240 MW, with a positive shadow price.
        DcOptimalPowerFlow.LineBinding bind = Assert.Single(r.BindingLines);
        Assert.Equal(5, bind.Branch);
        Assert.True(Math.Abs(bind.FlowMw + 240.0) <= 1e-6, $"flow {bind.FlowMw:R}");
        DcOptimalPowerFlow.LinePrice sp = Assert.Single(r.Prices!.Lines);
        Assert.Equal(5, sp.Branch);
        Assert.True(sp.ShadowPrice > 0, $"shadow price {sp.ShadowPrice:R}");
    }

    // ---- Quadratic costs -------------------------------------------------------------------------------------------

    [Fact]
    public void Case9_quadratic_costs_reach_the_closed_form_equal_marginal_cost_dispatch()
    {
        GridModel m = Case("lib/case9.m");
        DcOptimalPowerFlow.DcOpfResult r = DcOptimalPowerFlow.Solve(m, Curves);
        Assert.True(r.Feasible);
        Assert.Empty(r.BindingLines);

        // Closed form: P_i = (lambda - b_i) / (2 a_i), sum P_i = demand (no bound or line binds at the optimum).
        double demand = m.Buses.Sum(b => b.Pd);
        double sumInv = 0, sumB = 0;
        foreach (Generator g in m.Generators)
        {
            GeneratorCostCurve c = GeneratorCostCurve.From(g.Cost!, "case9");
            sumInv += 1.0 / (2 * c.Quadratic);
            sumB += c.Linear / (2 * c.Quadratic);
        }
        double lambda = (demand + sumB) / sumInv;
        for (int i = 0; i < m.Generators.Count; i++)
        {
            GeneratorCostCurve c = GeneratorCostCurve.From(m.Generators[i].Cost!, "case9");
            double p = (lambda - c.Linear) / (2 * c.Quadratic);
            Assert.True(Math.Abs(r.Dispatch[i].Pg - p) <= 1e-9, $"gen {i}: {r.Dispatch[i].Pg:R} vs {p:R}");
            Assert.True(Math.Abs(r.Dispatch[i].CostPerMwh - lambda) <= 1e-9, $"gen {i} marginal {r.Dispatch[i].CostPerMwh:R}");
        }
        // Every bus carries the system lambda (no congestion), the HiGHS value 24.044189543 and the closed form.
        foreach (DcOptimalPowerFlow.BusPrice bp in r.Prices!.Buses)
        {
            Assert.True(Math.Abs(bp.Lmp - lambda) <= 1e-9, $"bus {bp.BusId}: {bp.Lmp:R} vs {lambda:R}");
            Assert.True(Math.Abs(bp.Lmp - 24.044189543) <= 1e-6, $"bus {bp.BusId}: {bp.Lmp:R}");
        }
        Assert.True(Math.Abs(r.TotalCost - 5216.026607747310) <= 1e-6, $"cost {r.TotalCost:R}");
    }

    // case30 and case30_as: no line binds at the optimum, so the dispatch is the equal-incremental-cost one with the
    // units' bounds, 2 a_i P_i + b_i = lambda clamped to [Pmin, Pmax], sum P = demand. The reference values below are
    // that closed form solved by bisection on lambda to machine precision (an independent Python script). HiGHS agreed
    // on the cost (to 2e-10) and on every LMP (to 1e-9) but only to about 4e-4 MW on the dispatch: its QP solver stops
    // at its own optimality tolerance on this flat optimum, so the closed form is the dispatch reference here (it was
    // the first run's finding: GridSim matches it to about 1e-14 MW).
    [Fact]
    public void Case30_quadratic_costs_match_the_equal_incremental_cost_closed_form_and_highs_prices()
    {
        DcOptimalPowerFlow.DcOpfResult r = DcOptimalPowerFlow.Solve(Case("lib/case30.m"), Curves);
        Assert.True(r.Feasible);
        Assert.Empty(r.BindingLines);
        double[] exact = { 44.729907717498051, 58.262751677140628, 22.313570469599377, 32.325917787765114, 15.783926173998442, 15.783926173998442 };
        for (int i = 0; i < exact.Length; i++) Assert.True(Math.Abs(r.Dispatch[i].Pg - exact[i]) <= 1e-9, $"gen {i}: {r.Dispatch[i].Pg:R}");
        Assert.True(Math.Abs(r.TotalCost - 565.205966399922) <= 1e-6, $"cost {r.TotalCost:R}");
        foreach (DcOptimalPowerFlow.BusPrice bp in r.Prices!.Buses)
        {
            Assert.True(Math.Abs(bp.Lmp - 3.789196308699922) <= 1e-9, $"bus {bp.BusId}: {bp.Lmp:R}");   // the closed-form lambda
            Assert.True(Math.Abs(bp.Lmp - 3.789196309) <= 1e-6, $"bus {bp.BusId}: {bp.Lmp:R}");          // HiGHS
        }
    }

    [Fact]
    public void Pglib_case30_as_quadratic_costs_match_the_equal_incremental_cost_closed_form_and_highs_prices()
    {
        DcOptimalPowerFlow.DcOpfResult r = DcOptimalPowerFlow.Solve(Case("nesta/pglib_opf_case30_as.m"), Curves);
        Assert.True(r.Feasible);
        Assert.Empty(r.BindingLines);
        double[] exact = { 185.403587443946094, 46.872197309417018, 19.124215246636766, 10.0, 10.0, 12.0 };
        for (int i = 0; i < exact.Length; i++) Assert.True(Math.Abs(r.Dispatch[i].Pg - exact[i]) <= 1e-9, $"gen {i}: {r.Dispatch[i].Pg:R}");
        Assert.True(Math.Abs(r.TotalCost - 767.602099775784) <= 1e-6, $"cost {r.TotalCost:R}");
        foreach (DcOptimalPowerFlow.BusPrice bp in r.Prices!.Buses)
        {
            Assert.True(Math.Abs(bp.Lmp - 3.390526905829596) <= 1e-9, $"bus {bp.BusId}: {bp.Lmp:R}");
            Assert.True(Math.Abs(bp.Lmp - 3.390526906) <= 1e-6, $"bus {bp.BusId}: {bp.Lmp:R}");
        }
    }

    [Fact]
    public void Pglib_case3_quadratic_costs_with_a_binding_line_match_highs_prices_and_congestion()
    {
        // The active-set QP with a line at its limit: the case that needs both the quadratic solver and the line duals.
        DcOptimalPowerFlow.DcOpfResult r = DcOptimalPowerFlow.Solve(Case("nesta/pglib_opf_case3_lmbd.m"), Curves);
        Assert.True(r.Feasible);
        Assert.Equal(LpStatus.Optimal, r.SecurityLpStatus);
        double[] highs = { 144.333333333333, 170.666666666667, 0.0 };
        for (int i = 0; i < highs.Length; i++) Assert.True(Math.Abs(r.Dispatch[i].Pg - highs[i]) <= 1e-6, $"gen {i}: {r.Dispatch[i].Pg:R}");
        Assert.True(Math.Abs(r.TotalCost - 5693.803333333333) <= 1e-6, $"cost {r.TotalCost:R}");
        double[] lmp = { 36.753333333, 30.213333333, 41.258666666 };
        for (int b = 1; b <= 3; b++) Assert.True(Math.Abs(Lmp(r, b) - lmp[b - 1]) <= 1e-6, $"bus {b}: {Lmp(r, b):R}");
        DcOptimalPowerFlow.LineBinding bind = Assert.Single(r.BindingLines);
        Assert.Equal(1, bind.Branch);   // 3 - 2, rated 50 MW
        Assert.True(Math.Abs(bind.FlowMw + 50.0) <= 1e-6, $"flow {bind.FlowMw:R}");
        Assert.True(Assert.Single(r.Prices!.Lines).ShadowPrice > 0);
        // A unit strictly between its bounds is priced at its own marginal cost.
        foreach (int i in new[] { 0, 1 })
            Assert.True(Math.Abs(Lmp(r, r.Dispatch[i].GenBus) - r.Dispatch[i].CostPerMwh) <= 1e-9, $"gen {i}");
        // Energy + congestion = LMP, and the reference bus has no congestion component.
        foreach (DcOptimalPowerFlow.BusPrice bp in r.Prices.Buses) Assert.Equal(bp.EnergyComponent + bp.CongestionComponent, bp.Lmp);
    }

    // ---- Prices are the derivative of the optimal cost ------------------------------------------------------------

    public static IEnumerable<object[]> PriceCases() => new[]
    {
        new object[] { "nesta/pglib_opf_case5_pjm.m", true },
        new object[] { "nesta/pglib_opf_case3_lmbd.m", true },
        new object[] { "lib/case9.m", true },
        new object[] { "lib/case24_ieee_rts.m", true },
        new object[] { "nesta/pglib_opf_case5_pjm.m", false },   // the fuel-type path's duals
        new object[] { "lib/case24_ieee_rts.m", false },
    };

    [Theory]
    [MemberData(nameof(PriceCases))]
    public void Every_lmp_equals_the_finite_difference_of_the_optimal_cost(string rel, bool curves)
    {
        // An independent check of the dual bookkeeping (row signs, the PTDF-transpose solve): the price at bus k must be
        // d(optimal cost)/d(demand at k). Central difference, +/- 1e-4 MW; inside one basis / active set the cost is
        // linear (LP) or quadratic (QP) in the demand, so the difference is exact up to rounding.
        GridModel m = Case(rel);
        var opts = new DcOptimalPowerFlow.DcOpfOptions { UseGeneratorCosts = curves, ComputePrices = true };
        DcOptimalPowerFlow.DcOpfResult r = DcOptimalPowerFlow.Solve(m, opts);
        Assert.True(r.Feasible, rel);
        const double d = 1e-4;
        foreach (Bus bus in m.Buses)
        {
            double Cost(double delta)
            {
                var buses = m.Buses.Select(b => b.Id == bus.Id ? b with { Pd = b.Pd + delta } : b).ToList();
                DcOptimalPowerFlow.DcOpfResult x = DcOptimalPowerFlow.Solve(m.CopyWith(buses: buses), opts with { ComputePrices = false });
                Assert.True(x.Feasible);
                return x.TotalCost;
            }
            double fd = (Cost(d) - Cost(-d)) / (2 * d);
            double lmp = Lmp(r, bus.Id);
            Assert.True(Math.Abs(fd - lmp) <= 1e-5 * Math.Max(1.0, Math.Abs(lmp)), $"{rel} bus {bus.Id}: LMP {lmp:R} vs finite difference {fd:R}");
        }
    }

    // ---- Existing paths unchanged ----------------------------------------------------------------------------------

    public static IEnumerable<object[]> UnchangedCases() => new[]
    {
        "lib/case9.m", "lib/case14.m", "lib/case30.m", "lib/case57.m", "lib/case118.m", "lib/case145.m", "lib/case300.m",
        "lib/case24_ieee_rts.m", "nesta/pglib_opf_case5_pjm.m", "nesta/pglib_opf_case3_lmbd.m", "nesta/pglib_opf_case30_as.m",
        "nesta/pglib_opf_case162_ieee_dtc.m", "nesta/pglib_opf_case240_pserc.m", "rts/pglib_opf_case73_ieee_rts.m",
        "benchmarks/cigre_mv.m", "dist/case33bw.m",
    }.Select(f => new object[] { f });

    private static void AssertSameDispatch(string label, DcOptimalPowerFlow.DcOpfResult a, DcOptimalPowerFlow.DcOpfResult b)
    {
        static long B(double v) => BitConverter.DoubleToInt64Bits(v);
        Assert.Equal(a.Feasible, b.Feasible);
        Assert.Equal(a.NetworkConstrained, b.NetworkConstrained);
        Assert.Equal(a.SecurityLpStatus, b.SecurityLpStatus);
        Assert.True(B(a.TotalCost) == B(b.TotalCost), $"{label}: cost {a.TotalCost:R} vs {b.TotalCost:R}");
        Assert.True(B(a.TotalGenMw) == B(b.TotalGenMw), $"{label}: generation");
        Assert.True(B(a.TotalLoadMw) == B(b.TotalLoadMw), $"{label}: load");
        Assert.Equal(a.Dispatch.Count, b.Dispatch.Count);
        for (int i = 0; i < a.Dispatch.Count; i++)
        {
            Assert.Equal(a.Dispatch[i].GenBus, b.Dispatch[i].GenBus);
            Assert.True(B(a.Dispatch[i].Pg) == B(b.Dispatch[i].Pg), $"{label}: gen {i} Pg {a.Dispatch[i].Pg:R} vs {b.Dispatch[i].Pg:R}");
            Assert.True(B(a.Dispatch[i].CostPerMwh) == B(b.Dispatch[i].CostPerMwh), $"{label}: gen {i} cost");
        }
        Assert.Equal(a.BindingLines.Count, b.BindingLines.Count);
        for (int i = 0; i < a.BindingLines.Count; i++)
        {
            Assert.Equal(a.BindingLines[i].Branch, b.BindingLines[i].Branch);
            Assert.True(B(a.BindingLines[i].FlowMw) == B(b.BindingLines[i].FlowMw), $"{label}: binding {i} flow");
        }
    }

    [Theory]
    [MemberData(nameof(UnchangedCases))]
    public void Default_options_and_asking_for_prices_leave_the_fuel_type_dispatch_bit_identical(string rel)
    {
        GridModel m = Case(rel);
        foreach (bool limits in new[] { true, false })
        {
            // A case the old call refuses must be refused the same way by the new ones.
            Exception? oldEx = Record.Exception(() => DcOptimalPowerFlow.Solve(m, enforceLineLimits: limits));
            if (oldEx is not null)
            {
                Exception? e1 = Record.Exception(() => DcOptimalPowerFlow.Solve(m, new DcOptimalPowerFlow.DcOpfOptions { EnforceLineLimits = limits, ComputePrices = true }));
                Assert.NotNull(e1);
                Assert.Equal(oldEx.GetType(), e1!.GetType());
                Assert.Equal(oldEx.Message, e1.Message);
                continue;
            }
            DcOptimalPowerFlow.DcOpfResult old = DcOptimalPowerFlow.Solve(m, enforceLineLimits: limits);
            DcOptimalPowerFlow.DcOpfResult viaOptions = DcOptimalPowerFlow.Solve(m, new DcOptimalPowerFlow.DcOpfOptions { EnforceLineLimits = limits });
            DcOptimalPowerFlow.DcOpfResult withPrices = DcOptimalPowerFlow.Solve(m, new DcOptimalPowerFlow.DcOpfOptions { EnforceLineLimits = limits, ComputePrices = true });
            AssertSameDispatch($"{rel} limits={limits} options", old, viaOptions);
            AssertSameDispatch($"{rel} limits={limits} prices", old, withPrices);
            Assert.Null(old.Prices);
            Assert.False(old.UsedGeneratorCosts);
            if (withPrices.Feasible && (withPrices.SecurityLpStatus is null || withPrices.SecurityLpStatus == LpStatus.Optimal))
                Assert.NotNull(withPrices.Prices);
        }
    }

    [Fact]
    public void The_detailed_simplex_returns_the_plain_solves_bits()
    {
        // SolveDetailed only copies the duals out after the last operation; X and the status are the same bits.
        foreach (string rel in new[] { "lib/case24_ieee_rts.m", "nesta/pglib_opf_case5_pjm.m", "lib/case118.m" })
        {
            GridModel m = Case(rel);
            double[] cost = m.Generators.Select(g => DcOptimalPowerFlow.FuelCostPerMwh(g.Type)).ToArray();
            double[] pmin = m.Generators.Select(g => g.Pmin).ToArray();
            double[] pmax = m.Generators.Select(g => double.IsInfinity(g.Pmax) ? DcOptimalPowerFlow.UnboundedPmaxMw : g.Pmax).ToArray();
            DcOptimalPowerFlow.SecurityLp lp = DcOptimalPowerFlow.BuildSecurityLp(m, cost, pmin, pmax);
            (LpStatus s0, double[] x0, _) = DcOpfSecuritySimplex.Solve(lp.Problem);
            DcOpfSecuritySimplex.Detailed d = DcOpfSecuritySimplex.SolveDetailed(lp.Problem);
            Assert.Equal(s0, d.Status);
            Assert.Equal(x0.Select(BitConverter.DoubleToInt64Bits), d.X.Select(BitConverter.DoubleToInt64Bits));
            if (s0 == LpStatus.Optimal) Assert.Equal(lp.Problem.B.Length, d.Duals!.Length);
        }
    }

    [Fact]
    public void Acopf_without_cost_curves_is_unchanged_and_with_them_reports_the_curve_cost()
    {
        GridModel m = Case("lib/case9.m");
        AcOpfResult a = AcOptimalPowerFlow.Solve(m);
        AcOpfResult b = AcOptimalPowerFlow.Solve(m, useGeneratorCosts: false);
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.TotalCostPerHour), BitConverter.DoubleToInt64Bits(b.TotalCostPerHour));
        Assert.Equal(a.Dispatch.Select(d => BitConverter.DoubleToInt64Bits(d.Pg)), b.Dispatch.Select(d => BitConverter.DoubleToInt64Bits(d.Pg)));

        AcOpfResult c = AcOptimalPowerFlow.Solve(m, useGeneratorCosts: true);
        Assert.True(c.Converged);
        double curveCost = 0;
        for (int i = 0; i < m.Generators.Count; i++) curveCost += GeneratorCostCurve.From(m.Generators[i].Cost!, "g").CostAt(c.Dispatch[i].Pg);
        Assert.Equal(curveCost, c.TotalCostPerHour, 9);
        // The loss loop covers the losses: generation = demand + losses, above the lossless 315 MW.
        Assert.True(c.Dispatch.Sum(d => d.Pg) > 315.0);
        Assert.Throws<ArgumentException>(() => AcOptimalPowerFlow.Solve(m, costPerGen: new[] { 1.0, 2.0, 3.0 }, useGeneratorCosts: true));
    }

    // ---- Piecewise-linear costs and curve checks -------------------------------------------------------------------

    private static GridModel TwoUnitCase(GeneratorCost c1, GeneratorCost c2, double load, double rate = 0)
        => new()
        {
            Name = "two-unit",
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pv), new Bus(3, BusType.Pq, Pd: load) },
            Generators = new[] { new Generator(1, 0, Pmax: 200, Cost: c1), new Generator(2, 0, Pmax: 200, Cost: c2) },
            Branches = new[] { new Branch(1, 3, 0, 0.1, RateAMva: rate), new Branch(2, 3, 0, 0.1), new Branch(1, 2, 0, 0.1) },
        };

    private static GeneratorCost Pwl(params double[] xy) => new(GeneratorCostModel.PiecewiseLinear, 0, 0, xy);
    private static GeneratorCost Poly(params double[] k) => new(GeneratorCostModel.Polynomial, 0, 0, k);

    [Fact]
    public void A_convex_piecewise_linear_cost_fills_its_segments_in_order_and_prices_at_the_marginal_slope()
    {
        // Unit 1: 0-100 MW at 10/MWh, 100-200 MW at 30/MWh. Unit 2: flat 20/MWh. 150 MW of load: unit 1 takes its cheap
        // 100 MW, unit 2 the other 50 MW (20 < 30), and the price is unit 2's 20/MWh.
        GridModel m = TwoUnitCase(Pwl(0, 0, 100, 1000, 200, 4000), Poly(20, 0), 150);
        DcOptimalPowerFlow.DcOpfResult r = DcOptimalPowerFlow.Solve(m, Curves);
        Assert.True(r.Feasible);
        Assert.Equal(100.0, r.Dispatch[0].Pg, 9);
        Assert.Equal(50.0, r.Dispatch[1].Pg, 9);
        Assert.Equal(1000.0 + 20 * 50.0, r.TotalCost, 9);
        foreach (DcOptimalPowerFlow.BusPrice bp in r.Prices!.Buses) Assert.Equal(20.0, bp.Lmp, 9);
        // At a breakpoint the reported marginal cost is the segment below it (the left derivative): 10/MWh.
        Assert.Equal(10.0, r.Dispatch[0].CostPerMwh, 12);

        // 350 MW: unit 1's cheap 100 MW, all 200 MW of unit 2, then 50 MW of unit 1's dear segment: price 30/MWh.
        DcOptimalPowerFlow.DcOpfResult r2 = DcOptimalPowerFlow.Solve(TwoUnitCase(Pwl(0, 0, 100, 1000, 200, 4000), Poly(20, 0), 350), Curves);
        Assert.True(r2.Feasible);
        Assert.Equal(150.0, r2.Dispatch[0].Pg, 9);
        Assert.Equal(200.0, r2.Dispatch[1].Pg, 9);
        Assert.Equal(30.0, r2.Dispatch[0].CostPerMwh, 12);
        Assert.Equal(1000.0 + 30 * 50.0 + 20 * 200.0, r2.TotalCost, 9);
        foreach (DcOptimalPowerFlow.BusPrice bp in r2.Prices!.Buses) Assert.Equal(30.0, bp.Lmp, 9);
    }

    [Fact]
    public void A_binding_line_separates_the_prices_on_a_hand_case()
    {
        // Cheap unit 1 (10/MWh) behind line 1-3 rated 60 MW; dear unit 2 (40/MWh). The load bus prices at the dear unit.
        GridModel m = TwoUnitCase(Poly(10, 0), Poly(40, 0), 150, rate: 60);
        DcOptimalPowerFlow.DcOpfResult r = DcOptimalPowerFlow.Solve(m, Curves);
        Assert.True(r.Feasible);
        Assert.True(r.NetworkConstrained);
        Assert.Equal(10.0, Lmp(r, 1), 9);   // the reference bus: the cheap unit is marginal there
        Assert.Equal(40.0, Lmp(r, 2), 9);   // the dear unit is marginal at its own bus
        // By hand (equal reactances, a triangle): f13 = 100 - P2/3 <= 60, so P2 = 120 and P1 = 30. PTDF of line 1-3 is
        // -1/3 at bus 2 and -2/3 at bus 3 (reference bus 1), so 40 = 10 + y (-1/3) gives the line dual y = -90 and the
        // load bus pays 10 + (-90)(-2/3) = 70/MWh; the line's shadow price is -y = 90/MWh per MW of rating.
        Assert.Equal(30.0, r.Dispatch[0].Pg, 9);
        Assert.Equal(120.0, r.Dispatch[1].Pg, 9);
        Assert.Equal(70.0, Lmp(r, 3), 9);
        DcOptimalPowerFlow.LinePrice sp = Assert.Single(r.Prices!.Lines);
        Assert.Equal(0, sp.Branch);
        Assert.Equal(90.0, sp.ShadowPrice, 9);
        Assert.Equal(60.0, sp.FlowMw, 9);
    }

    [Theory]
    [InlineData(new double[] { 1.0, 0.0, 5.0, 0.0 }, "order 3")]                 // a real cubic term
    [InlineData(new double[] { -0.1, 5.0, 0.0 }, "concave")]                    // negative quadratic
    public void A_polynomial_the_dc_opf_cannot_optimise_exactly_is_refused(double[] k, string expected)
    {
        var ex = Assert.Throws<InvalidDataException>(() => GeneratorCostCurve.From(Poly(k), "unit X"));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Contains("unit X", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_padded_polynomial_and_a_constant_are_accepted()
    {
        GeneratorCostCurve c = GeneratorCostCurve.From(Poly(0, 0, 0.5, 3, 7), "u");
        Assert.Equal(0.5, c.Quadratic); Assert.Equal(3.0, c.Linear); Assert.Equal(7.0, c.Constant);
        GeneratorCostCurve k = GeneratorCostCurve.From(Poly(7), "u");
        Assert.Equal(0.0, k.MarginalCostAt(50)); Assert.Equal(7.0, k.CostAt(50));
    }

    [Theory]
    [InlineData(new double[] { 0, 0, 100, 3000, 200, 4000 }, "not convex")]
    [InlineData(new double[] { 0, 0, 0, 100 }, "strictly increase")]
    [InlineData(new double[] { 0, 0 }, "at least 2 points")]
    public void A_bad_piecewise_linear_curve_is_refused(double[] xy, string expected)
    {
        var ex = Assert.Throws<InvalidDataException>(() => GeneratorCostCurve.From(Pwl(xy), "unit Y"));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_unit_with_no_curve_is_refused_when_curves_are_in_use()
    {
        GridModel m = TwoUnitCase(Poly(10, 0), Poly(40, 0), 100);
        GridModel bare = m.CopyWith(generators: new[] { m.Generators[0], m.Generators[1] with { Cost = null } });
        var ex = Assert.Throws<InvalidDataException>(() => DcOptimalPowerFlow.Solve(bare, Curves));
        Assert.Contains("has no cost curve", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Demand_beyond_the_fleet_is_infeasible_with_curves_and_reports_no_prices()
    {
        DcOptimalPowerFlow.DcOpfResult r = DcOptimalPowerFlow.Solve(TwoUnitCase(Poly(10, 0), Poly(40, 0), 500), Curves);
        Assert.False(r.Feasible);
        Assert.Null(r.Prices);
    }

    [Fact]
    public void Two_islands_with_curves_are_priced_each_at_its_own_marginal_unit()
    {
        // Island A: buses 1-2, unit at 10/MWh. Island B: buses 3-4, unit at 25/MWh. No branch joins them.
        var m = new GridModel
        {
            Name = "two islands",
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 30), new Bus(3, BusType.Pv), new Bus(4, BusType.Pq, Pd: 20) },
            Generators = new[] { new Generator(1, 0, Pmax: 100, Cost: Poly(10, 0)), new Generator(3, 0, Pmax: 100, Cost: Poly(25, 5)) },
            Branches = new[] { new Branch(1, 2, 0, 0.1), new Branch(3, 4, 0, 0.1) },
        };
        DcOptimalPowerFlow.DcOpfResult r = DcOptimalPowerFlow.Solve(m, Curves);
        Assert.True(r.Feasible);
        Assert.NotNull(r.Islands);
        Assert.Equal(30.0, r.Dispatch[0].Pg, 9);
        Assert.Equal(20.0, r.Dispatch[1].Pg, 9);
        Assert.Equal(10 * 30.0 + 25 * 20.0 + 5, r.TotalCost, 9);
        Assert.Equal(10.0, Lmp(r, 2), 9);
        Assert.Equal(25.0, Lmp(r, 4), 9);
    }

    // ---- The JSON cost file ----------------------------------------------------------------------------------------

    [Fact]
    public void The_cost_file_overlays_the_case_by_index_and_by_name_and_round_trips()
    {
        GridModel m = TwoUnitCase(Poly(10, 0), Poly(40, 0), 150);
        m = m.CopyWith(generators: new[] { m.Generators[0] with { Name = "A" }, m.Generators[1] with { Name = "B" } });
        string json = """
            { "format": "gridsim-costs/1",
              "generators": [
                { "index": 1, "model": "piecewiseLinear", "points": [[0, 0], [100, 1000], [200, 4000]] },
                { "name": "B", "model": "polynomial", "coefficients": [20, 0], "startup": 5 } ] }
            """;
        GridModel c = GeneratorCostFile.Apply(m, json, "test.json");
        Assert.Equal(GeneratorCostModel.PiecewiseLinear, c.Generators[0].Cost!.Model);
        Assert.Equal(new double[] { 0, 0, 100, 1000, 200, 4000 }, c.Generators[0].Cost!.Coefficients);
        Assert.Equal(new double[] { 20, 0 }, c.Generators[1].Cost!.Coefficients);
        Assert.Equal(5.0, c.Generators[1].Cost!.StartupCost);
        // The written file reads back to the same curves.
        GridModel back = GeneratorCostFile.Apply(m, GeneratorCostFile.Write(c), "written.json");
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(c.Generators[i].Cost!.Model, back.Generators[i].Cost!.Model);
            Assert.Equal(c.Generators[i].Cost!.Coefficients, back.Generators[i].Cost!.Coefficients);
        }
        // And it dispatches as the hand case above: 100 MW + 50 MW at 20/MWh.
        DcOptimalPowerFlow.DcOpfResult r = DcOptimalPowerFlow.Solve(c, Curves);
        Assert.Equal(100.0, r.Dispatch[0].Pg, 9);
        Assert.Equal(50.0, r.Dispatch[1].Pg, 9);
    }

    [Theory]
    [InlineData("""{ "generators": [] }""", "\"format\" must be")]
    [InlineData("""{ "format": "gridsim-costs/1", "generators": [], "extra": 1 }""", "unknown key 'extra'")]
    [InlineData("""{ "format": "gridsim-costs/1", "generators": [ { "index": 3, "model": "polynomial", "coefficients": [1] } ] }""", "outside 1..2")]
    [InlineData("""{ "format": "gridsim-costs/1", "generators": [ { "index": 1, "model": "polynomial", "coefficients": [1] }, { "index": 1, "model": "polynomial", "coefficients": [2] } ] }""", "given a cost twice")]
    [InlineData("""{ "format": "gridsim-costs/1", "generators": [ { "index": 1, "name": "A", "model": "polynomial", "coefficients": [1] } ] }""", "not both")]
    [InlineData("""{ "format": "gridsim-costs/1", "generators": [ { "index": 1, "model": "cubic", "coefficients": [1] } ] }""", "must be \"polynomial\" or \"piecewiseLinear\"")]
    [InlineData("""{ "format": "gridsim-costs/1", "generators": [ { "index": 1, "model": "polynomial", "points": [[0, 0], [1, 1]] } ] }""", "needs \"coefficients\"")]
    [InlineData("""{ "format": "gridsim-costs/1", "generators": [ { "index": 1, "model": "polynomial", "coefficients": [1, 0, 0, 0] } ] }""", "order 3")]
    [InlineData("""{ "format": "gridsim-costs/1", "generators": [ { "index": 1, "model": "polynomial", "coefficients": [1], "typo": 2 } ] }""", "unknown key 'typo'")]
    [InlineData("""{ "format": "gridsim-costs/1", "generators": [ { "name": "Z", "model": "polynomial", "coefficients": [1] } ] }""", "no generator is named 'Z'")]
    public void A_bad_cost_file_is_refused_naming_the_entry(string json, string expected)
    {
        GridModel m = TwoUnitCase(Poly(10, 0), Poly(40, 0), 150);
        m = m.CopyWith(generators: new[] { m.Generators[0] with { Name = "A" }, m.Generators[1] with { Name = "B" } });
        var ex = Assert.Throws<InvalidDataException>(() => GeneratorCostFile.Apply(m, json, "bad.json"));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Contains("bad.json", ex.Message, StringComparison.Ordinal);
    }

    // ---- CLI wiring (source level) ---------------------------------------------------------------------------------

    [Fact]
    public void Dcopf_costs_and_prices_flags_are_known_documented_and_refused_elsewhere()
    {
        string root = CliProcess.Root();
        string src = File.ReadAllText(Path.Combine(root, "src", "GridSim.Cli", "Program.cs"));
        Assert.Contains("\"--costs\",", src, StringComparison.Ordinal);
        Assert.Contains("\"--prices\",", src, StringComparison.Ordinal);
        Assert.Contains("error: --costs applies to dcopf and acopf only", src, StringComparison.Ordinal);
        // Moved 2026-10 (N121, authorised by Mark's 2026-10-02 best-judgement ruling): this pinned the gap N121 closes
        // (acopf could not report prices). Before: "error: --prices applies to dcopf only". After: the gate also lets
        // acopf through, so the message names both verbs; --prices is still refused on every other verb.
        // Moved 2026-10 (D-DOC-049, docs-session finding, Ruling R1: Mark delegated 2026-10-04; main ruled that the --prices
        // gate no longer blocks chain's price policies). Before: "error: --prices applies to dcopf and acopf only". After:
        // "error: --prices applies to dcopf, acopf and chain (with --bess) only"; every other verb still refuses it.
        Assert.Contains("error: --prices applies to dcopf, acopf and chain (with --bess) only", src, StringComparison.Ordinal);
        string ch19 = File.ReadAllText(Path.Combine(root, "docs", "manual", "0019 CLI reference.md"));
        Assert.Contains("`--costs`", ch19, StringComparison.Ordinal);
        Assert.Contains("`--prices`", ch19, StringComparison.Ordinal);
        Assert.Contains("gridsim-costs/1", ch19, StringComparison.Ordinal);
    }
}
