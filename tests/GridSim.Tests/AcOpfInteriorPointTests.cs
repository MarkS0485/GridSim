using System.Globalization;
using GridSim.Core.Analysis;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The full AC optimal power flow (<see cref="AcOpfInteriorPoint"/>, a primal-dual interior point method, MATPOWER's MIPS
/// step for step): the published MATPOWER <c>runopf</c> objectives, the exact analytic derivatives against central finite
/// differences, the KKT conditions at the optimum (a unit inside its limits is priced at its own marginal cost), bit-for-bit
/// repeatability, failing loud when it does not converge, and every refusal.
/// </summary>
public class AcOpfInteriorPointTests
{
    private static GridModel Case(string rel) => S10TestSupport.LoadMatpower(rel).Model;

    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

    // ---- MATPOWER runopf (default MIPS solver) objectives, as MATPOWER prints them (two decimals) ------------------

    [Theory]
    [InlineData("lib/case9.m", "5296.69")]
    [InlineData("lib/case14.m", "8081.53")]
    [InlineData("lib/case30.m", "576.89")]
    [InlineData("lib/case57.m", "41737.79")]
    [InlineData("lib/case118.m", "129660.70")]
    [InlineData("lib/case300.m", "719725.11")]
    [InlineData("pegase/case1354pegase.m", "74069.35")]
    [InlineData("pegase/case2869pegase.m", "133999.29")]
    public void Objective_matches_matpower_runopf_to_the_printed_two_decimals(string rel, string published)
    {
        AcOpfIpmResult r = AcOpfInteriorPoint.Solve(Case(rel));
        Assert.True(r.Converged, $"{rel}: {r.Status}, {r.Message}");
        // MATPOWER prints the objective rounded to two decimals; the optimum here rounds to exactly that figure.
        Assert.Equal(published, Math.Round(r.ObjectivePerHour, 2, MidpointRounding.AwayFromZero).ToString("F2", CultureInfo.InvariantCulture));
        // The balance closes: generation = load + losses, and losses are positive on these networks.
        Assert.True(r.LossesMw > 0, $"{rel}: losses {r.LossesMw}");
    }

    [Fact]
    public void Case9_dispatch_matches_matpower_runopf()
    {
        AcOpfIpmResult r = AcOpfInteriorPoint.Solve(Case("lib/case9.m"));
        Assert.True(r.Converged);
        // MATPOWER runopf('case9'): Pg = 89.80, 134.32, 94.19 MW.
        Assert.Equal(new[] { "89.80", "134.32", "94.19" },
            r.Generators.Select(g => Math.Round(g.PgMw, 2, MidpointRounding.AwayFromZero).ToString("F2", CultureInfo.InvariantCulture)));
    }

    // ---- Machine epsilon: repeatable bit for bit -------------------------------------------------------------------

    [Theory]
    [InlineData("lib/case30.m")]    // rated branches, a binding flow limit
    [InlineData("lib/case118.m")]
    public void Two_solves_are_bit_identical(string rel)
    {
        GridModel m = Case(rel);
        AcOpfIpmResult a = AcOpfInteriorPoint.Solve(m);
        AcOpfIpmResult b = AcOpfInteriorPoint.Solve(m);
        Assert.Equal(a.Iterations, b.Iterations);
        Assert.Equal(Bits(a.ObjectivePerHour), Bits(b.ObjectivePerHour));
        Assert.Equal(a.Buses.SelectMany(x => new[] { x.VmPu, x.VaDeg, x.LmpP, x.LmpQ, x.MuVmin, x.MuVmax }).Select(Bits),
            b.Buses.SelectMany(x => new[] { x.VmPu, x.VaDeg, x.LmpP, x.LmpQ, x.MuVmin, x.MuVmax }).Select(Bits));
        Assert.Equal(a.Generators.SelectMany(x => new[] { x.PgMw, x.QgMvar, x.MuPmin, x.MuPmax, x.MuQmin, x.MuQmax }).Select(Bits),
            b.Generators.SelectMany(x => new[] { x.PgMw, x.QgMvar, x.MuPmin, x.MuPmax, x.MuQmin, x.MuQmax }).Select(Bits));
        Assert.Equal(a.Branches.SelectMany(x => new[] { x.PfMw, x.QfMvar, x.PtMw, x.QtMvar, x.MuSf, x.MuSt }).Select(Bits),
            b.Branches.SelectMany(x => new[] { x.PfMw, x.QfMvar, x.PtMw, x.QtMvar, x.MuSf, x.MuSt }).Select(Bits));
        Assert.Equal(a.Log.Select(l => Bits(l.Objective)), b.Log.Select(l => Bits(l.Objective)));
    }

    [Fact]
    public void Case9_optimum_is_pinned_bit_for_bit()
    {
        // The exact doubles of this build's optimum. Every loop runs in a fixed order and every trigonometric value comes
        // from DetMath, so these hold on every CPU (AVX-512, AVX2, no FMA) and every OS; CI runs the hardware-intrinsics-off
        // leg against the same pins.
        AcOpfIpmResult r = AcOpfInteriorPoint.Solve(Case("lib/case9.m"));
        Assert.Equal(11, r.Iterations);
        Assert.Equal("5296.68652362981", r.ObjectivePerHour.ToString("R", CultureInfo.InvariantCulture));
        Assert.Equal(new[] { "89.79861381248718", "134.32065247283484", "94.18743862777458" },
            r.Generators.Select(g => g.PgMw.ToString("R", CultureInfo.InvariantCulture)));
    }

    // ---- The KKT conditions at the optimum -------------------------------------------------------------------------

    [Theory]
    [InlineData("lib/case9.m")]
    [InlineData("lib/case30.m")]
    [InlineData("lib/case118.m")]
    public void A_unit_inside_its_limits_is_priced_at_its_own_marginal_cost(string rel)
    {
        // Stationarity in Pg: dC/dPg - LMP_P(bus) - muPmin + muPmax = 0. For a unit away from both P limits the bound
        // multipliers are zero, so the nodal price at its bus equals its marginal cost 2 a P + b. In Qg there is no cost,
        // so a unit away from both Q limits sees a reactive price of zero at its bus.
        GridModel m = Case(rel);
        AcOpfIpmResult r = AcOpfInteriorPoint.Solve(m);
        Assert.True(r.Converged);
        int checkedP = 0, checkedQ = 0;
        foreach (AcOpfGenResult g in r.Generators)
        {
            Generator gen = m.Generators[g.Index];
            AcOpfBusResult bus = r.Buses.Single(b => b.BusId == g.Bus);
            if (g.PgMw > gen.Pmin + 1.0 && g.PgMw < gen.Pmax - 1.0)
            {
                IReadOnlyList<double> k = gen.Cost!.Coefficients;   // a, b, c (MATPOWER order, highest first)
                double marginal = 2 * k[0] * g.PgMw + k[1];
                Assert.True(Math.Abs(bus.LmpP - marginal) < 1e-3, $"{rel} gen {g.Index}: LMP {bus.LmpP:R} vs marginal cost {marginal:R}");
                checkedP++;
            }
            if (g.QgMvar > gen.Qmin + 1.0 && g.QgMvar < gen.Qmax - 1.0)
            {
                Assert.True(Math.Abs(bus.LmpQ) < 1e-3, $"{rel} gen {g.Index}: reactive price {bus.LmpQ:R}");
                checkedQ++;
            }
        }
        Assert.True(checkedP > 0 && checkedQ > 0, $"{rel}: no interior unit checked ({checkedP} P, {checkedQ} Q)");
    }

    [Fact]
    public void Case30_binds_branch_6_8_and_its_prices_separate()
    {
        AcOpfIpmResult r = AcOpfInteriorPoint.Solve(Case("lib/case30.m"));
        Assert.True(r.Converged);
        // Branch 9 (6-8) runs at its 32 MVA rating at the from end, with a positive shadow price, and is listed as binding.
        AcOpfBranchResult br = r.Branches.Single(b => b.Index == 9);
        Assert.Equal((6, 8), (br.From, br.To));
        Assert.True(br.MuSf > 0);
        Assert.Equal(br.RateMva, Math.Sqrt(br.PfMw * br.PfMw + br.QfMvar * br.QfMvar), 3);
        Assert.Contains(r.Binding, b => b.Kind == "Sf" && b.Element.StartsWith("branch 9 ", StringComparison.Ordinal));
        // Congestion separates the nodal prices.
        Assert.True(r.Buses.Max(b => b.LmpP) - r.Buses.Min(b => b.LmpP) > 0.1);
        // Every voltage is within its limits (to the method's feasibility tolerance).
        GridModel m = Case("lib/case30.m");
        foreach (AcOpfBusResult b in r.Buses)
        {
            Bus bus = m.Buses.Single(x => x.Id == b.BusId);
            Assert.InRange(b.VmPu, bus.VminPu!.Value - 1e-6, bus.VmaxPu!.Value + 1e-6);
        }
    }

    // ---- The exact derivatives against central finite differences ---------------------------------------------------

    [Theory]
    [InlineData("lib/case9.m")]
    [InlineData("lib/case30.m")]   // rated branches: the flow-limit rows enter the Hessian
    public void Analytic_gradient_and_hessian_match_finite_differences(string rel)
    {
        GridModel m = Case(rel);
        (int nx, int neq, int niq) = AcOpfInteriorPoint.Dimensions(m);
        // A point away from the start (angles spread, voltages and outputs moved) and non-trivial multipliers, from a
        // fixed linear congruential sequence so the check is the same on every run.
        ulong state = 0x9E3779B97F4A7C15UL;
        double Next() { state = state * 6364136223846793005UL + 1442695040888963407UL; return ((state >> 11) * (1.0 / 9007199254740992.0)) - 0.5; }
        double[] x = AcOpfInteriorPoint.StartingPoint(m);
        for (int j = 0; j < nx; j++) x[j] += 0.1 * Next();
        var lam = new double[neq];
        var mu = new double[niq];
        for (int r = 0; r < neq; r++) lam[r] = 10 * Next();
        for (int r = 0; r < niq; r++) mu[r] = Math.Abs(10 * Next());

        double[] grad = AcOpfInteriorPoint.LagrangianGradient(m, x, lam, mu);
        double[,] hess = AcOpfInteriorPoint.LagrangianHessian(m, x, lam, mu);
        const double step = 1e-6;
        for (int j = 0; j < nx; j++)
        {
            double[] xp = (double[])x.Clone(), xm = (double[])x.Clone();
            xp[j] += step; xm[j] -= step;
            double fd = (AcOpfInteriorPoint.Lagrangian(m, xp, lam, mu) - AcOpfInteriorPoint.Lagrangian(m, xm, lam, mu)) / (2 * step);
            Assert.True(Math.Abs(fd - grad[j]) <= 1e-5 * (1 + Math.Abs(grad[j])), $"{rel}: dL/dx[{j}] analytic {grad[j]:R}, finite difference {fd:R}");

            double[] gp = AcOpfInteriorPoint.LagrangianGradient(m, xp, lam, mu);
            double[] gm = AcOpfInteriorPoint.LagrangianGradient(m, xm, lam, mu);
            for (int i = 0; i < nx; i++)
            {
                double fdh = (gp[i] - gm[i]) / (2 * step);
                Assert.True(Math.Abs(fdh - hess[i, j]) <= 1e-4 * (1 + Math.Abs(hess[i, j])),
                    $"{rel}: d2L/dx[{i}]dx[{j}] analytic {hess[i, j]:R}, finite difference {fdh:R}");
            }
        }
        // The Hessian is symmetric exactly (every off-diagonal term is added to both triangles with the same value).
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < i; j++)
                Assert.Equal(Bits(hess[i, j]), Bits(hess[j, i]));
    }

    // ---- Failing loud ----------------------------------------------------------------------------------------------

    [Fact]
    public void Iteration_cap_is_reported_not_passed_off_as_an_optimum()
    {
        AcOpfIpmResult r = AcOpfInteriorPoint.Solve(Case("lib/case9.m"), new AcOpfIpmOptions { MaxIterations = 3 });
        Assert.False(r.Converged);
        Assert.Equal(AcOpfIpmStatus.IterationCap, r.Status);
        Assert.Equal(3, r.Iterations);
        Assert.Equal(4, r.Log.Count);   // the start and three iterations
        Assert.Empty(r.Binding);         // no binding list for a point that is not an optimum
        Assert.Contains("3 iteration", r.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_infeasible_case_does_not_converge()
    {
        // Three times the load of case9 is beyond the units' combined Pmax (820 MW against 945 MW of load).
        GridModel m = Case("lib/case9.m");
        m = m.CopyWith(buses: m.Buses.Select(b => b with { Pd = 3 * b.Pd, Qd = 3 * b.Qd }).ToArray());
        AcOpfIpmResult r = AcOpfInteriorPoint.Solve(m);
        Assert.False(r.Converged, $"converged on an infeasible case: {r.Message}");
        Assert.NotEqual(AcOpfIpmStatus.Converged, r.Status);
        Assert.False(string.IsNullOrEmpty(r.Message));
    }

    // ---- Refusals --------------------------------------------------------------------------------------------------

    private static void Refused(GridModel m, string fragment)
    {
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => AcOpfInteriorPoint.Solve(m));
        Assert.Contains(fragment, ex.Message, StringComparison.Ordinal);
    }

    private static GridModel WithGen(GridModel m, int g, Func<Generator, Generator> change)
        => m.CopyWith(generators: m.Generators.Select((x, i) => i == g ? change(x) : x).ToArray());

    [Fact]
    public void A_unit_without_a_cost_curve_is_refused()
        => Refused(WithGen(Case("lib/case9.m"), 1, g => g with { Cost = null }), "has no cost curve");

    [Fact]
    public void A_piecewise_linear_cost_is_refused()
        => Refused(WithGen(Case("lib/case9.m"), 0, g => g with { Cost = new GeneratorCost(GeneratorCostModel.PiecewiseLinear, 0, 0, new[] { 0.0, 0.0, 100.0, 2000.0 }) }),
            "piecewise-linear");

    [Fact]
    public void A_dispatchable_load_is_refused()
        => Refused(WithGen(Case("lib/case9.m"), 2, g => g with { Pmin = -50, Pmax = 0 }), "dispatchable load");

    [Fact]
    public void Crossed_unit_limits_are_refused()
    {
        Refused(WithGen(Case("lib/case9.m"), 0, g => g with { Pmin = 300, Pmax = 200 }), "above Pmax");
        Refused(WithGen(Case("lib/case9.m"), 0, g => g with { Qmin = 300, Qmax = 200 }), "above Qmax");
    }

    [Fact]
    public void A_voltage_dependent_load_is_refused()
    {
        GridModel m = Case("lib/case9.m");
        Refused(m.CopyWith(buses: m.Buses.Select((b, i) => i == 4 ? b with { Zip = new ZipLoad(Ap: 1, Cp: 0) } : b).ToArray()), "voltage-dependent");
    }

    [Fact]
    public void A_network_without_a_slack_bus_is_refused()
    {
        GridModel m = Case("lib/case9.m");
        Refused(m.CopyWith(buses: m.Buses.Select(b => b.Type == BusType.Slack ? b with { Type = BusType.Pv } : b).ToArray()), "no slack bus");
    }

    [Fact]
    public void Two_islands_are_refused()
    {
        // Opening both branches at bus 9 (8-9 and 9-4) leaves bus 9 on its own.
        GridModel m = Case("lib/case9.m");
        GridModel cut = m.CopyWith(branches: m.Branches.Select(b => (b.From == 9 || b.To == 9) ? b with { InService = false } : b).ToArray());
        Refused(cut, "more than one island");
    }

    [Fact]
    public void A_zero_impedance_branch_is_refused()
    {
        GridModel m = Case("lib/case9.m");
        Refused(m.CopyWith(branches: m.Branches.Select((b, i) => i == 3 ? b with { R = 0, X = 0 } : b).ToArray()), "zero impedance");
    }

    [Fact]
    public void Options_out_of_range_are_refused()
    {
        GridModel m = Case("lib/case9.m");
        Assert.Throws<ArgumentOutOfRangeException>(() => AcOpfInteriorPoint.Solve(m, new AcOpfIpmOptions { MaxIterations = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => AcOpfInteriorPoint.Solve(m, new AcOpfIpmOptions { FeasibilityTolerance = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => AcOpfInteriorPoint.Solve(m, new AcOpfIpmOptions { StepToBoundary = 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => AcOpfInteriorPoint.Solve(m, new AcOpfIpmOptions { Centring = double.NaN }));
    }
}
