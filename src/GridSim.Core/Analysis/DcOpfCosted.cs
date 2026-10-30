using GridSim.Core.Model;
using GridSim.Core.Numerics;

namespace GridSim.Core.Analysis;

/// <summary>
/// The DC-OPF on a connected network with each generator's own cost curve (F19, October 2026), and the nodal prices
/// (LMPs) and line shadow prices that come with it. Reached only through
/// <see cref="DcOptimalPowerFlow.Solve(GridModel, DcOptimalPowerFlow.DcOpfOptions)"/> with
/// <see cref="DcOptimalPowerFlow.DcOpfOptions.UseGeneratorCosts"/> set; the fuel-type path is untouched.
///
/// <para><b>Formulation.</b> The constraints are exactly those of the linear security LP
/// (<see cref="DcOptimalPowerFlow.BuildSecurityLp"/>): one balance row, and per rated line a thermal row with a slack in
/// [0, 2 RateA]. Each generator becomes one or more LP columns at its bus:</para>
/// <list type="bullet">
/// <item>a polynomial curve: one column in [Pmin, Pmax] with the linear coefficient b as its cost (and, when a &gt; 0,
/// the quadratic term handled by <see cref="DcOpfQuadratic"/>);</item>
/// <item>a piecewise-linear curve: one column per segment of [Pmin, Pmax] cut at the curve's breakpoints, each costed at
/// its segment's slope (the first column runs from Pmin to the first breakpoint, the others from 0 to their width).
/// Because the curve is convex the slopes rise, so an optimal LP fills the segments in order, and the unit's output is
/// the sum of its columns (the standard LP form of a convex piecewise-linear cost).</item>
/// </list>
/// <para>With only linear and piecewise-linear costs the LP is solved by the existing structured simplex
/// (<see cref="DcOpfSecuritySimplex"/>), and its optimal-basis duals give the prices. With any quadratic term the LP of
/// the linear parts gives a feasible vertex and <see cref="DcOpfQuadratic"/> takes it to the exact optimum; its
/// multipliers give the prices.</para>
///
/// <para><b>Prices.</b> With y0 the balance row's dual and y_r thermal row r's (each the change in optimal cost per unit
/// increase of the row's right-hand side), the price at bus k is <c>LMP_k = y0 + sum_r y_r PTDF[r, k]</c>: one more MW
/// of demand at k raises the balance right-hand side by 1 and each thermal right-hand side (RateA - f0) by PTDF[r, k]
/// (f0 being the flow the fixed loads cause). y0 is the energy component (the price at the island's reference bus),
/// the sum the congestion component; the DC model is lossless, so there is no loss component. A line's shadow price is
/// the cost saved per MW of extra rating: -y_r when it is held at +RateA, +y_r at -RateA. At a degenerate optimum the
/// duals (so the prices) need not be unique; the ones reported are those of the optimal basis or active set found.</para>
/// </summary>
internal static class DcOpfCosted
{
    /// <summary>One LP / QP column: the generator it belongs to, its bounds, linear cost and Hessian entry (2a).</summary>
    private readonly record struct Column(int Gen, double Lo, double Hi, double Linear, double Hessian);

    /// <summary>Solve one connected network (the caller splits islands). Every generator must carry a cost.</summary>
    internal static DcOptimalPowerFlow.DcOpfResult SolveConnected(GridModel model, bool enforceLineLimits, bool computePrices)
    {
        int g = model.Generators.Count;
        DcOptimalPowerFlow.CheckGeneratorLimits(model);
        var curves = new GeneratorCostCurve[g];
        for (int i = 0; i < g; i++)
        {
            Generator gen = model.Generators[i];
            string who = $"DC-OPF: generator {i} ('{gen.Name ?? gen.Type}' at bus {gen.Bus})";
            curves[i] = GeneratorCostCurve.From(
                gen.Cost ?? throw new InvalidDataException($"{who} has no cost curve; with generator costs in use every in-service unit needs one."),
                who);
        }
        var pmin = model.Generators.Select(gen => gen.Pmin).ToArray();
        var pmax = model.Generators.Select(gen => double.IsInfinity(gen.Pmax) ? DcOptimalPowerFlow.UnboundedPmaxMw : gen.Pmax).ToArray();
        double demand = model.Buses.Sum(b => b.Pd);

        // Balance feasibility first (the same test as the merit-order path): demand inside [sum Pmin, sum Pmax].
        double sumMin = pmin.Sum();
        double remaining = demand - sumMin;
        double range = 0.0;
        for (int i = 0; i < g; i++) range += pmax[i] - pmin[i];
        if (!(remaining >= -DcOptimalPowerFlow.BalanceToleranceMw && range + DcOptimalPowerFlow.BalanceToleranceMw >= remaining))
        {
            // No dispatch meets the demand: report every unit at the limit it is pushed to, not a fabricated optimum.
            double[] pin = remaining > 0 ? pmax : pmin;
            return Build(model, curves, (double[])pin.Clone(), demand, feasible: false, new List<DcOptimalPowerFlow.LineBinding>(),
                status: null, prices: null, failure: null);
        }

        // Columns.
        var cols = new List<Column>();
        bool anyQuadratic = false;
        for (int i = 0; i < g; i++)
        {
            GeneratorCostCurve cv = curves[i];
            if (!cv.IsPiecewiseLinear || pmin[i] == pmax[i])
            {
                double lin = cv.IsPiecewiseLinear ? cv.MarginalCostAt(pmin[i]) : cv.Linear;
                double h = cv.IsPiecewiseLinear ? 0.0 : 2.0 * cv.Quadratic;
                if (h != 0.0) anyQuadratic = true;
                cols.Add(new Column(i, pmin[i], pmax[i], lin, h));
                continue;
            }
            // Edges: Pmin, every breakpoint strictly inside (Pmin, Pmax), Pmax.
            var edges = new List<double> { pmin[i] };
            foreach (double x in cv.PointsMw) if (x > pmin[i] && x < pmax[i]) edges.Add(x);
            edges.Add(pmax[i]);
            for (int s = 0; s + 1 < edges.Count; s++)
            {
                double mid = 0.5 * (edges[s] + edges[s + 1]);
                double slope = cv.SegmentSlope(cv.SegmentOf(mid));
                if (s == 0) cols.Add(new Column(i, edges[0], edges[1], slope, 0.0));
                else cols.Add(new Column(i, 0.0, edges[s + 1] - edges[s], slope, 0.0));
            }
        }

        DistributionFactors df = DistributionFactors.Build(model);
        var rated = new List<int>();
        if (enforceLineLimits)
            for (int l = 0; l < model.Branches.Count; l++) if (model.Branches[l].RateAMva > 0) rated.Add(l);

        // N109 (Ruling R1, Mark delegated 2026-10-04): leave out, and report, the rated lines no dispatch can relieve,
        // by the same screen as the fuel-type path (DcOptimalPowerFlow.ScreenUnrelievable: every unit anywhere in its
        // range, the balance row dropped). With none left out, rated is the same list object as before, so the LP is the
        // one solved before N109. allRated keeps every rated line for the overload report of a non-optimal solve.
        List<int> allRated = rated;
        IReadOnlyList<DcOptimalPowerFlow.UnrelievableLine> unrelievable = Array.Empty<DcOptimalPowerFlow.UnrelievableLine>();
        if (rated.Count > 0)
        {
            var colOfBus = new Dictionary<int, double[]>();
            var columnOfGen = new double[g][];
            for (int i = 0; i < g; i++)
            {
                int bus = model.Generators[i].Bus;
                if (!colOfBus.TryGetValue(bus, out double[]? column)) { column = df.PtdfColumn(bus); colOfBus[bus] = column; }
                columnOfGen[i] = column;
            }
            double[] f0 = DcOptimalPowerFlow.FlowsFrom(model, df, new double[g]);
            List<int> ratedForScreen = rated;
            List<int> keep = DcOptimalPowerFlow.ScreenUnrelievable(model, ratedForScreen,
                (r, i) => columnOfGen[i][ratedForScreen[r]], f0, pmin, pmax, out List<DcOptimalPowerFlow.UnrelievableLine> left);
            if (left.Count > 0)
            {
                rated = keep.Select(k => ratedForScreen[k]).ToList();
                unrelievable = left;
            }
        }

        Attempt first = Run(model, df, cols, rated, demand, anyQuadratic);
        if (first.Status == LpStatus.Optimal)
        {
            double[] pg = Sum(cols, first.X, g);
            double[] flow = DcOptimalPowerFlow.FlowsFrom(model, df, pg);
            List<DcOptimalPowerFlow.LineBinding> binding = DcOptimalPowerFlow.BindingFrom(model, flow);
            DcOptimalPowerFlow.DcOpfPrices? prices = computePrices
                ? DcOptimalPowerFlow.PricesFrom(model, df, rated, first.BalanceDual, first.LineDuals, flow, binding)
                : null;
            // N109: "a security LP ran" is judged on every rated line (allRated), as before N109, so a case whose every
            // rated line was left out still reports that its line limits were considered.
            return Build(model, curves, pg, demand, feasible: true, binding,
                status: allRated.Count > 0 ? LpStatus.Optimal : null, prices, failure: null) with { UnrelievableLines = unrelievable };
        }

        // Not optimal. With line limits in the problem, solve the balance-only problem to report the economic dispatch
        // with its overloaded lines (the merit-order path does the same with its merit order), flagged not feasible.
        if (allRated.Count > 0)
        {
            Attempt econ = Run(model, df, cols, new List<int>(), demand, anyQuadratic);
            double[] pg = econ.Status == LpStatus.Optimal ? Sum(cols, econ.X, g) : (double[])pmin.Clone();
            double[] flow = DcOptimalPowerFlow.FlowsFrom(model, df, pg);
            var over = new List<DcOptimalPowerFlow.LineBinding>();
            foreach (int l in allRated)   // N109: every rated line, as before N109
                if (Math.Abs(flow[l]) >= model.Branches[l].RateAMva - DcOptimalPowerFlow.BindingToleranceMw)
                    over.Add(new DcOptimalPowerFlow.LineBinding(l, model.Branches[l].Name ?? "", flow[l], model.Branches[l].RateAMva));
            return Build(model, curves, pg, demand, feasible: false, over, first.Status, prices: null, first.Failure)
                with { NetworkConstrained = true, UnrelievableLines = unrelievable };
        }
        return Build(model, curves, (double[])pmin.Clone(), demand, feasible: false, new List<DcOptimalPowerFlow.LineBinding>(),
            first.Status, prices: null, first.Failure ?? $"the dispatch problem ended {first.Status}");
    }

    /// <summary>One solve attempt: status, column values, balance dual and one dual per rated line.</summary>
    private sealed record Attempt(LpStatus Status, double[] X, double BalanceDual, double[] LineDuals, string? Failure);

    private static Attempt Run(GridModel model, DistributionFactors df, List<Column> cols, List<int> rated, double demand, bool anyQuadratic)
    {
        int v = cols.Count, lines = rated.Count;

        // Distinct column buses (first appearance order), PTDF kept for the rated lines only, as BuildSecurityLp does.
        var colOfBus = new Dictionary<int, int>();
        var genCol = new int[v];
        var distinct = new List<int>();
        for (int j = 0; j < v; j++)
        {
            int bus = model.Generators[cols[j].Gen].Bus;
            if (!colOfBus.TryGetValue(bus, out int u)) { u = distinct.Count; colOfBus[bus] = u; distinct.Add(bus); }
            genCol[j] = u;
        }
        int nu = Math.Max(1, distinct.Count);
        var ptdf = new double[(long)lines * nu];
        for (int u = 0; u < distinct.Count; u++)
        {
            double[] column = df.PtdfColumn(distinct[u]);
            for (int r = 0; r < lines; r++) ptdf[(long)r * nu + u] = column[rated[r]];
        }
        double[] f0 = DcOptimalPowerFlow.FlowsFrom(model, df, new double[model.Generators.Count]);

        int n = v + lines;
        var b = new double[1 + lines];
        var c = new double[n];
        var lo = new double[n];
        var hi = new double[n];
        for (int j = 0; j < v; j++) { c[j] = cols[j].Linear; lo[j] = cols[j].Lo; hi[j] = cols[j].Hi; }
        b[0] = demand;
        for (int r = 0; r < lines; r++)
        {
            double rate = model.Branches[rated[r]].RateAMva;
            c[v + r] = 0.0; lo[v + r] = 0.0; hi[v + r] = 2.0 * rate;
            b[1 + r] = rate - f0[rated[r]];
        }
        // N240: the DC-OPF's lowest-index tie-break (DcOptimalPowerFlow.WithTieBreak), as on the fuel-type path.
        var problem = DcOptimalPowerFlow.WithTieBreak(new DcOpfSecuritySimplex.Problem(v, lines, nu, genCol, ptdf, b, c, lo, hi));
        DcOpfSecuritySimplex.Detailed lp = DcOpfSecuritySimplex.SolveDetailed(problem);
        if (lp.Status != LpStatus.Optimal)
            return new Attempt(lp.Status, lp.X, double.NaN, new double[lines], $"the linear dispatch problem ended {lp.Status}");

        if (!anyQuadratic)
        {
            double[] y = lp.Duals!;
            var lineDuals = new double[lines];
            Array.Copy(y, 1, lineDuals, 0, lines);
            return new Attempt(LpStatus.Optimal, lp.X, y[0], lineDuals, null);
        }

        // Quadratic terms: start the active-set QP at the linear part's optimal vertex.
        var h = new double[v];
        var lin = new double[v];
        var qlo = new double[v];
        var qhi = new double[v];
        for (int j = 0; j < v; j++) { h[j] = cols[j].Hessian; lin[j] = cols[j].Linear; qlo[j] = cols[j].Lo; qhi[j] = cols[j].Hi; }
        var rows = new double[(long)lines * v];
        var upper = new double[lines];
        var lower = new double[lines];
        var upAct = new bool[lines];
        var loAct = new bool[lines];
        for (int r = 0; r < lines; r++)
        {
            double rate = model.Branches[rated[r]].RateAMva;
            for (int j = 0; j < v; j++) rows[(long)r * v + j] = ptdf[(long)r * nu + genCol[j]];
            upper[r] = rate - f0[rated[r]];     // f0 + p.x <= RateA
            lower[r] = -rate - f0[rated[r]];    // f0 + p.x >= -RateA
            double s = lp.X[v + r];
            upAct[r] = s == 0.0;                // slack at 0: the line sits at +RateA
            loAct[r] = s == 2.0 * rate;         // slack at 2 RateA: the line sits at -RateA
        }
        var x0 = new double[v];
        Array.Copy(lp.X, x0, v);
        var qp = new DcOpfQuadratic.Problem(v, h, lin, qlo, qhi, demand, lines, rows, upper, lower);
        DcOpfQuadratic.Result res = DcOpfQuadratic.Solve(qp, x0, upAct, loAct);
        return new Attempt(res.Status, res.X, res.BalanceMultiplier, res.LineDuals, res.Failure);
    }

    /// <summary>Each generator's output: the sum of its columns, in column order.</summary>
    private static double[] Sum(List<Column> cols, double[] x, int g)
    {
        var pg = new double[g];
        for (int j = 0; j < cols.Count; j++) pg[cols[j].Gen] += x[j];
        return pg;
    }

    private static DcOptimalPowerFlow.DcOpfResult Build(GridModel model, GeneratorCostCurve[] curves, double[] pg, double demand,
        bool feasible, List<DcOptimalPowerFlow.LineBinding> binding, LpStatus? status, DcOptimalPowerFlow.DcOpfPrices? prices, string? failure)
    {
        int g = model.Generators.Count;
        var dispatch = new List<DcOptimalPowerFlow.GenDispatch>(g);
        double total = 0.0, gen = 0.0;
        for (int i = 0; i < g; i++)
        {
            dispatch.Add(new DcOptimalPowerFlow.GenDispatch(model.Generators[i].Bus, pg[i], curves[i].MarginalCostAt(pg[i])));
            total += curves[i].CostAt(pg[i]);
            gen += pg[i];
        }
        return new DcOptimalPowerFlow.DcOpfResult(dispatch, total, gen, demand, binding, feasible, binding.Count > 0)
        {
            SecurityLpStatus = status,
            Prices = prices,
            UsedGeneratorCosts = true,
            SolverFailure = failure,
        };
    }
}
