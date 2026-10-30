using System.Globalization;
using GridSim.Core.Model;
using GridSim.Core.Numerics;

namespace GridSim.Core.Analysis;

/// <summary>A unit's commitment data for <see cref="UnitCommitment"/>. Every field is optional; what is not given falls
/// back as each property says.</summary>
public sealed record UcUnitData
{
    /// <summary>A flat marginal cost (per MWh) that replaces the unit's cost curve. Null: the case's cost curve
    /// (<see cref="Generator.Cost"/>), else the fuel-type cost of the DC-OPF.</summary>
    public double? MarginalCost { get; init; }

    /// <summary>Cost per hour while on, on top of the curve. Null: 0 (a polynomial curve's constant term is counted
    /// anyway, as part of the curve's cost at Pmin).</summary>
    public double? NoLoadCost { get; init; }

    /// <summary>Cost of each start. Null: the case's gencost STARTUP column, else 0.</summary>
    public double? StartupCost { get; init; }

    /// <summary>Once started, the unit stays on for at least this many periods (default 1: no limit).</summary>
    public int MinUpPeriods { get; init; } = 1;

    /// <summary>Once stopped, the unit stays off for at least this many periods (default 1: no limit).</summary>
    public int MinDownPeriods { get; init; } = 1;

    /// <summary>The most the output can rise from one period to the next while on (MW; default no limit). A start may
    /// reach max(ramp, Pmin) in its first period.</summary>
    public double RampUpMw { get; init; } = double.PositiveInfinity;

    /// <summary>The most the output can fall from one period to the next while on (MW; default no limit). A stop may
    /// leave from up to max(ramp, Pmin).</summary>
    public double RampDownMw { get; init; } = double.PositiveInfinity;

    /// <summary>Whether the unit is on before the first period (default true).</summary>
    public bool InitiallyOn { get; init; } = true;

    /// <summary>How many periods the unit has already been in its initial state (default 1000: any minimum up or down
    /// time already met).</summary>
    public int InitialPeriods { get; init; } = 1000;

    /// <summary>The output before the first period (MW), for the first period's ramp limit. Null: the first period is
    /// not ramp-limited.</summary>
    public double? InitialMw { get; init; }

    /// <summary>True: the unit is on in every period (no on/off decision). A unit whose Pmin is below 0 (storage
    /// charging, an exporting link) is always treated as must-run.</summary>
    public bool MustRun { get; init; }
}

/// <summary>The settings of <see cref="UnitCommitment.Solve"/>.</summary>
public sealed record UcOptions
{
    /// <summary>The number of periods (at least 1).</summary>
    public int Periods { get; init; } = 24;

    /// <summary>The length of a period in hours (default 1): energy and no-load costs are per hour, start-up costs per
    /// start.</summary>
    public double PeriodHours { get; init; } = 1.0;

    /// <summary>Each period's demand as a multiple of the case's bus loads (every bus scaled alike). Null: 1 in every
    /// period. Length <see cref="Periods"/>.</summary>
    public double[]? DemandScale { get; init; }

    /// <summary>Each period's spinning reserve requirement (MW): the headroom Pmax - P summed over the committed units
    /// must reach it. Null: none. Length <see cref="Periods"/>.</summary>
    public double[]? ReserveMw { get; init; }

    /// <summary>Per-unit data aligned to <c>model.Generators</c> (the in-service fleet); null entries or a null list use
    /// the defaults of <see cref="UcUnitData"/>.</summary>
    public IReadOnlyList<UcUnitData?>? Units { get; init; }

    /// <summary>Enforce the rated branches' MVA ratings (RATE_A) on the DC flows in every period (default true).</summary>
    public bool EnforceLineLimits { get; init; } = true;

    /// <summary>Branch indices (into <c>model.Branches</c>) whose outage is secured in every period: every rated
    /// branch's post-outage DC flow (by the line outage distribution factors) must stay within its rating. An outage
    /// that islands the network is refused.</summary>
    public IReadOnlyList<int> Contingencies { get; init; } = Array.Empty<int>();

    /// <summary>How many equal segments a quadratic cost curve is cut into between Pmin and Pmax (default 4). The
    /// segments are the curve's chords, so the cost is exact at the break points and slightly above the curve between
    /// them; a linear or piecewise-linear curve is used exactly.</summary>
    public int QuadraticSegments { get; init; } = 4;

    /// <summary>The branch-and-bound settings (default <see cref="MilpOptions"/>).</summary>
    public MilpOptions Milp { get; init; } = new();

    /// <summary>Refuse a problem with more columns than this (default 200,000): the search solves a fresh relaxation per
    /// node, so a very large commitment is better split by horizon than started and abandoned.</summary>
    public int MaxColumns { get; init; } = 200_000;
}

/// <summary>One unit's schedule: on/off, output and starts per period, and its cost.</summary>
public sealed record UcUnitSchedule(int Index, int Bus, string Name, bool[] On, double[] OutputMw, bool[] Started, double Cost);

/// <summary>One period's totals: demand, generation, committed headroom (spinning reserve available), the reserve
/// asked for, and the system marginal price (the balance row's dual in the relaxation with the commitment fixed; NaN
/// when that relaxation did not solve).</summary>
public sealed record UcPeriod(int Period, double DemandMw, double GenerationMw, double HeadroomMw, double ReserveMw, double SystemPrice);

/// <summary>A branch at its rating in some period: pre-outage (Contingency -1) or after the outage of branch
/// Contingency.</summary>
public sealed record UcBinding(int Period, int Branch, string Name, int Contingency, double FlowMw, double RateMw);

/// <summary>The commitment's result. <see cref="Status"/> is the branch-and-bound's; the schedules and periods are
/// filled when there is an incumbent (status Optimal or NodeCap with an incumbent), empty otherwise.</summary>
public sealed record UcResult(
    MilpStatus Status, double TotalCost, double EnergyCost, double NoLoadCost, double StartupCost, double Bound,
    IReadOnlyList<UcPeriod> Periods, IReadOnlyList<UcUnitSchedule> Units, IReadOnlyList<UcBinding> Bindings,
    int Rows, int Columns, int IntegerColumns, int Nodes, int LpIterations, IReadOnlyList<string> Notes, string Message)
{
    /// <summary>True when the commitment was proven optimal within the gap.</summary>
    public bool Optimal => Status == MilpStatus.Optimal;
}

/// <summary>
/// Security-constrained unit commitment (roadmap P0-3): which units run in each period, and at what output, at least
/// total cost, as a mixed-integer linear program solved by <see cref="MilpBranchAndBound"/>.
///
/// <para><b>Formulation</b> (per unit g, period t; the standard three-binary form with the output split into
/// segments above Pmin). On/off u, start v (both whole); stop w (continuous, whole whenever u and v are);
/// output P = Pmin u + sum_k s_k with 0 &lt;= s_k &lt;= segment length.</para>
/// <list type="bullet">
/// <item>Balance: sum_g P = demand(t) (the case's bus loads times the period's scale).</item>
/// <item>Capacity: P &lt;= Pmax u (and P &gt;= Pmin u by construction).</item>
/// <item>Logic: u(t) - u(t-1) = v(t) - w(t), from the unit's initial state.</item>
/// <item>Minimum up / down time (Rajan and Takriti): the starts in the last UT periods &lt;= u(t); the stops in the last
/// DT periods &lt;= 1 - u(t); a unit inside its initial minimum time is held in its state.</item>
/// <item>Ramps: P(t) - P(t-1) &lt;= RU u(t-1) + max(RU, Pmin) v(t), and the mirror for falling output with stops.</item>
/// <item>Spinning reserve: sum_g (Pmax u - P) &gt;= reserve(t).</item>
/// <item>Network: each rated branch's DC flow, PTDF x generation plus the flow the loads alone drive (from the same DC
/// solve as <c>dcpf</c>), within +/- RATE_A; for each listed outage k, flow + LODF(l, k) x flow(k) within +/- RATE_A.</item>
/// <item>Cost: the curve's slopes on the segments, its cost at Pmin and the no-load cost per hour on u, the start-up
/// cost on v.</item>
/// </list>
///
/// <para>One connected network only (several DC islands are refused, naming the count). A committable unit needs a
/// finite Pmax. The answer's integer columns are exactly whole (see <see cref="MilpBranchAndBound"/>); the system
/// price is the balance row's dual in the relaxation with the commitment fixed (the usual pricing of a commitment).
/// Deterministic: every loop runs in a fixed order and the search is bit-identical from run to run.</para>
/// </summary>
public static class UnitCommitment
{
    /// <summary>A branch whose post-outage or pre-outage flow is within this of its rating (MW) is reported binding.</summary>
    public const double BindingToleranceMw = 1e-3;

    /// <summary>Solve the commitment for <paramref name="model"/>. Throws <see cref="ArgumentException"/> (or
    /// <see cref="ArgumentOutOfRangeException"/>) naming the problem for input it cannot build exactly.</summary>
    public static UcResult Solve(GridModel model, UcOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        int T = options.Periods;
        if (T < 1) throw new ArgumentOutOfRangeException(nameof(options), T, "Unit commitment needs at least one period.");
        if (!(options.PeriodHours > 0) || !double.IsFinite(options.PeriodHours))
            throw new ArgumentOutOfRangeException(nameof(options), options.PeriodHours, "The period length must be a finite number of hours above 0.");
        if (options.QuadraticSegments < 1)
            throw new ArgumentOutOfRangeException(nameof(options), options.QuadraticSegments, "A quadratic cost needs at least one segment.");
        double[] scale = options.DemandScale ?? Enumerable.Repeat(1.0, T).ToArray();
        double[] reserve = options.ReserveMw ?? new double[T];
        if (scale.Length != T) throw new ArgumentException($"Unit commitment: {scale.Length} demand scale(s) for {T} period(s).", nameof(options));
        if (reserve.Length != T) throw new ArgumentException($"Unit commitment: {reserve.Length} reserve value(s) for {T} period(s).", nameof(options));
        for (int t = 0; t < T; t++)
        {
            if (!double.IsFinite(scale[t]) || scale[t] < 0) throw new ArgumentException($"Unit commitment: period {t + 1} demand scale {scale[t]} must be a finite number at or above 0.", nameof(options));
            if (!double.IsFinite(reserve[t]) || reserve[t] < 0) throw new ArgumentException($"Unit commitment: period {t + 1} reserve {reserve[t]} must be a finite number at or above 0.", nameof(options));
        }

        IReadOnlyList<Generator> gens = model.Generators;
        int G = gens.Count;
        if (G == 0) throw new ArgumentException("Unit commitment: the case has no in-service generator.", nameof(model));
        if (options.Units is not null && options.Units.Count != G)
            throw new ArgumentException($"Unit commitment: unit data for {options.Units.Count} unit(s); the case has {G} in-service generator(s).", nameof(options));

        IReadOnlyList<DcIsland> islands = DistributionFactors.FindIslands(model);
        if (islands.Count != 1)
            throw new ArgumentException($"Unit commitment: the network has {islands.Count} DC islands; one connected network is needed.", nameof(model));

        var notes = new List<string>();
        var units = new Unit[G];
        for (int g = 0; g < G; g++) units[g] = BuildUnit(gens[g], g, options.Units?[g] ?? new UcUnitData(), options.QuadraticSegments, notes);

        double loadMw = model.Buses.Sum(b => b.Pd);
        var demand = new double[T];
        for (int t = 0; t < T; t++) demand[t] = loadMw * scale[t];

        var lp = new LpBuilder();
        var u = new int[G, T];
        var v = new int[G, T];
        var w = new int[G, T];
        var seg = new int[G, T][];
        double h = options.PeriodHours;
        for (int g = 0; g < G; g++)
        {
            Unit un = units[g];
            int holdOn = un.Data.InitiallyOn ? Math.Max(0, un.Data.MinUpPeriods - un.Data.InitialPeriods) : 0;
            int holdOff = !un.Data.InitiallyOn ? Math.Max(0, un.Data.MinDownPeriods - un.Data.InitialPeriods) : 0;
            for (int t = 0; t < T; t++)
            {
                double ulo = 0, uhi = 1;
                if (un.MustRun || t < holdOn) ulo = 1;
                if (t < holdOff) { if (ulo == 1) throw new ArgumentException($"Unit commitment: unit {un.Name} is must-run but held off by its initial minimum down time.", nameof(options)); uhi = 0; }
                u[g, t] = lp.Col(un.FixedCostPerHour * h, ulo, uhi, true);
                v[g, t] = lp.Col(un.StartupCost, 0, 1, true);
                w[g, t] = lp.Col(0, 0, 1, false);
                seg[g, t] = new int[un.SegmentLength.Length];
                for (int k = 0; k < un.SegmentLength.Length; k++)
                    seg[g, t][k] = lp.Col(un.SegmentSlope[k] * h, 0, un.SegmentLength[k], false);
            }
        }

        // P(g, t) as (column, coefficient) terms: Pmin u + sum s.
        List<(int, double)> P(int g, int t, double factor)
        {
            var terms = new List<(int, double)>();
            if (units[g].Pmin != 0) terms.Add((u[g, t], factor * units[g].Pmin));
            foreach (int c in seg[g, t]) terms.Add((c, factor));
            return terms;
        }

        int balanceRow0 = lp.Rows;
        for (int t = 0; t < T; t++)
        {
            var terms = new List<(int, double)>();
            for (int g = 0; g < G; g++) terms.AddRange(P(g, t, 1.0));
            lp.Eq(demand[t], terms);
        }

        for (int g = 0; g < G; g++)
        {
            Unit un = units[g];
            double span = un.Pmax - un.Pmin;
            for (int t = 0; t < T; t++)
            {
                // Capacity: sum s - (Pmax - Pmin) u <= 0.
                var cap = seg[g, t].Select(c => (c, 1.0)).ToList();
                cap.Add((u[g, t], -span));
                lp.Le(0, cap);

                // Logic: u(t) - u(t-1) - v(t) + w(t) = 0 (u(-1) the initial state, moved to the right-hand side).
                if (t == 0) lp.Eq(un.Data.InitiallyOn ? 1 : 0, new() { (u[g, 0], 1.0), (v[g, 0], -1.0), (w[g, 0], 1.0) });
                else lp.Eq(0, new() { (u[g, t], 1.0), (u[g, t - 1], -1.0), (v[g, t], -1.0), (w[g, t], 1.0) });
                // Never a start and a stop in the same period: with no start-up cost that pair would otherwise let a unit
                // that stays on take the start's ramp allowance (or reset its minimum times) for free.
                lp.Le(1, new() { (v[g, t], 1.0), (w[g, t], 1.0) });

                if (un.Data.MinUpPeriods > 1)
                {
                    var terms = new List<(int, double)>();
                    for (int q = Math.Max(0, t - un.Data.MinUpPeriods + 1); q <= t; q++) terms.Add((v[g, q], 1.0));
                    terms.Add((u[g, t], -1.0));
                    lp.Le(0, terms);
                }
                if (un.Data.MinDownPeriods > 1)
                {
                    var terms = new List<(int, double)>();
                    for (int q = Math.Max(0, t - un.Data.MinDownPeriods + 1); q <= t; q++) terms.Add((w[g, q], 1.0));
                    terms.Add((u[g, t], 1.0));
                    lp.Le(1, terms);
                }

                double ru = un.Data.RampUpMw, rd = un.Data.RampDownMw;
                double su = Math.Max(ru, un.Pmin), sd = Math.Max(rd, un.Pmin);
                if (double.IsFinite(ru))
                {
                    if (t > 0)
                    {
                        var terms = P(g, t, 1.0);
                        terms.AddRange(P(g, t - 1, -1.0));
                        terms.Add((u[g, t - 1], -ru));
                        terms.Add((v[g, t], -su));
                        lp.Le(0, terms);
                    }
                    else if (un.Data.InitialMw is double p0)
                    {
                        var terms = P(g, 0, 1.0);
                        terms.Add((v[g, 0], -su));
                        lp.Le(p0 + (un.Data.InitiallyOn ? ru : 0), terms);
                    }
                }
                if (double.IsFinite(rd))
                {
                    if (t > 0)
                    {
                        var terms = P(g, t - 1, 1.0);
                        terms.AddRange(P(g, t, -1.0));
                        terms.Add((u[g, t], -rd));
                        terms.Add((w[g, t], -sd));
                        lp.Le(0, terms);
                    }
                    else if (un.Data.InitialMw is double p0)
                    {
                        var terms = P(g, 0, -1.0);
                        terms.Add((u[g, 0], -rd));
                        terms.Add((w[g, 0], -sd));
                        lp.Le(-p0, terms);
                    }
                }
            }
        }

        for (int t = 0; t < T; t++)
        {
            if (reserve[t] <= 0) continue;
            // sum (Pmax - Pmin) u - sum s >= R, written as -(...) <= -R.
            var terms = new List<(int, double)>();
            for (int g = 0; g < G; g++)
            {
                terms.Add((u[g, t], -(units[g].Pmax - units[g].Pmin)));
                foreach (int c in seg[g, t]) terms.Add((c, 1.0));
            }
            lp.Le(-reserve[t], terms);
        }

        // Network.
        var rated = new List<int>();
        var flowCol = new Dictionary<int, int[]>();   // branch -> column per period
        double[][]? ptdfByGen = null;
        double[][]? loadFlow = null;
        var contingencies = options.Contingencies ?? Array.Empty<int>();
        var lodf = new Dictionary<int, double[]>();
        if (options.EnforceLineLimits)
            for (int l = 0; l < model.Branches.Count; l++)
                if (model.Branches[l].InService && model.Branches[l].RateAMva > 0) rated.Add(l);
        if (rated.Count > 0 || contingencies.Count > 0)
        {
            DistributionFactors df = DistributionFactors.Build(model);
            foreach (int k in contingencies)
            {
                if ((uint)k >= (uint)model.Branches.Count)
                    throw new ArgumentException($"Unit commitment: contingency branch {k} is not in the case ({model.Branches.Count} branches).", nameof(options));
                if (!model.Branches[k].InService)
                    throw new ArgumentException($"Unit commitment: contingency branch {k} ({BranchName(model, k)}) is already out of service.", nameof(options));
                if (lodf.ContainsKey(k))
                    throw new ArgumentException($"Unit commitment: contingency branch {k} is listed twice.", nameof(options));
                double[] col = df.LodfColumn(k);
                if (col.Any(double.IsNaN))
                    throw new ArgumentException($"Unit commitment: the outage of branch {k} ({BranchName(model, k)}) islands the network; it cannot be secured by redispatch.", nameof(options));
                lodf[k] = col;
            }

            var needed = new SortedSet<int>(rated);
            foreach (int k in contingencies) needed.Add(k);
            if (needed.Count > 0)
            {
                var busPtdf = new Dictionary<int, double[]>();
                ptdfByGen = new double[G][];
                for (int g = 0; g < G; g++)
                {
                    int bus = gens[g].Bus;
                    if (!busPtdf.TryGetValue(bus, out double[]? col)) { col = df.PtdfColumn(bus); busPtdf[bus] = col; }
                    ptdfByGen[g] = col;
                }
                loadFlow = new double[T][];
                for (int t = 0; t < T; t++)
                {
                    var inj = new Dictionary<int, double>();
                    foreach (Bus b in model.Buses) if (b.Pd != 0) inj[b.Id] = -b.Pd * scale[t];
                    loadFlow[t] = df.DcFlows(inj, model.BaseMva);
                }
                foreach (int l in needed)
                {
                    double rate = model.Branches[l].RateAMva;
                    bool limited = options.EnforceLineLimits && rate > 0;
                    var cols = new int[T];
                    for (int t = 0; t < T; t++)
                    {
                        cols[t] = lp.Col(0, limited ? -rate : double.NegativeInfinity, limited ? rate : double.PositiveInfinity, false);
                        // sum_g PTDF(l, bus g) P(g, t) - f = -loadflow(l, t)
                        var terms = new List<(int, double)>();
                        for (int g = 0; g < G; g++)
                        {
                            double ptdf = ptdfByGen[g][l];
                            if (ptdf == 0) continue;
                            terms.AddRange(P(g, t, ptdf));
                        }
                        terms.Add((cols[t], -1.0));
                        lp.Eq(-loadFlow[t][l], terms);
                    }
                    flowCol[l] = cols;
                }
                foreach (int k in contingencies)
                {
                    foreach (int l in rated)
                    {
                        if (l == k) continue;
                        double f = lodf[k][l];
                        if (f == 0) continue;
                        double rate = model.Branches[l].RateAMva;
                        for (int t = 0; t < T; t++)
                        {
                            int q = lp.Col(0, -rate, rate, false);
                            lp.Eq(0, new() { (flowCol[l][t], 1.0), (flowCol[k][t], f), (q, -1.0) });
                        }
                    }
                }
            }
        }

        if (lp.Columns > options.MaxColumns)
            throw new ArgumentException($"Unit commitment: {lp.Columns} columns exceed the limit of {options.MaxColumns}; shorten the horizon, list fewer contingencies or raise the limit.", nameof(options));

        MilpProblem problem = lp.Build();
        MilpResult r = MilpBranchAndBound.Solve(problem, options.Milp);
        int integers = problem.Integer.Count(x => x);
        if (r.X is null)
            return new UcResult(r.Status, double.NaN, double.NaN, double.NaN, double.NaN, r.Bound, Array.Empty<UcPeriod>(),
                Array.Empty<UcUnitSchedule>(), Array.Empty<UcBinding>(), problem.Lp.Rows, problem.Lp.Columns, integers,
                r.Nodes, r.LpIterations, notes, r.Message);

        double[] x = r.X;
        // The system price: the balance rows' duals with the commitment (u, v) fixed at the answer.
        var flo = (double[])problem.Lp.Lower.Clone();
        var fhi = (double[])problem.Lp.Upper.Clone();
        for (int j = 0; j < problem.Integer.Length; j++)
            if (problem.Integer[j]) { flo[j] = x[j]; fhi[j] = x[j]; }
        SparseSimplexResult priced = SparseBoundedSimplex.Solve(problem.Lp, flo, fhi, options.Milp.LpTolerance, options.Milp.LpMaxIterations);
        if (!priced.Optimal) notes.Add($"system price not available: the relaxation with the commitment fixed ended '{priced.Message}'");

        var schedules = new List<UcUnitSchedule>(G);
        double energy = 0, noLoad = 0, startup = 0;
        var genT = new double[T];
        var headroomT = new double[T];
        for (int g = 0; g < G; g++)
        {
            Unit un = units[g];
            var on = new bool[T];
            var pOut = new double[T];
            var started = new bool[T];
            double costG = 0;
            for (int t = 0; t < T; t++)
            {
                on[t] = x[u[g, t]] == 1.0;
                started[t] = x[v[g, t]] == 1.0;
                double p = un.Pmin * x[u[g, t]];
                double segCost = 0;
                // An off unit's segments are held at 0 by its capacity row (sum s <= (Pmax - Pmin) u = 0); reading them only
                // when on keeps an off unit at exactly 0 MW and no energy cost whatever round-off the basis solve leaves.
                if (on[t])
                    for (int k = 0; k < seg[g, t].Length; k++) { p += x[seg[g, t][k]]; segCost += un.SegmentSlope[k] * h * x[seg[g, t][k]]; }
                pOut[t] = p;
                genT[t] += p;
                if (on[t]) headroomT[t] += un.Pmax - p;
                double nl = un.FixedCostPerHour * h * x[u[g, t]];
                double sc = un.StartupCost * x[v[g, t]];
                energy += segCost; noLoad += nl; startup += sc;
                costG += segCost + nl + sc;
            }
            schedules.Add(new UcUnitSchedule(g, gens[g].Bus, un.Name, on, pOut, started, costG));
        }
        var periods = new List<UcPeriod>(T);
        for (int t = 0; t < T; t++)
            periods.Add(new UcPeriod(t + 1, demand[t], genT[t], headroomT[t], reserve[t],
                priced.Optimal ? priced.Y[balanceRow0 + t] / h : double.NaN));

        var bindings = new List<UcBinding>();
        if (options.EnforceLineLimits)
        {
            foreach (int l in rated)
            {
                double rate = model.Branches[l].RateAMva;
                for (int t = 0; t < T; t++)
                {
                    double f = x[flowCol[l][t]];
                    if (Math.Abs(f) >= rate - BindingToleranceMw) bindings.Add(new UcBinding(t + 1, l, BranchName(model, l), -1, f, rate));
                }
            }
        }
        foreach (int k in contingencies)
            foreach (int l in rated)
            {
                if (l == k) continue;
                double rate = model.Branches[l].RateAMva;
                for (int t = 0; t < T; t++)
                {
                    double post = x[flowCol[l][t]] + lodf[k][l] * x[flowCol[k][t]];
                    if (Math.Abs(post) >= rate - BindingToleranceMw) bindings.Add(new UcBinding(t + 1, l, BranchName(model, l), k, post, rate));
                }
            }

        return new UcResult(r.Status, energy + noLoad + startup, energy, noLoad, startup, r.Bound, periods, schedules, bindings,
            problem.Lp.Rows, problem.Lp.Columns, integers, r.Nodes, r.LpIterations, notes, r.Message);
    }

    private static string BranchName(GridModel model, int l) =>
        model.Branches[l].Name ?? $"{model.Branches[l].From}-{model.Branches[l].To}";

    /// <summary>A unit as the formulation uses it: Pmin, Pmax, the segments above Pmin (length and slope), the fixed
    /// cost per hour while on (curve cost at Pmin plus no-load) and the start-up cost.</summary>
    private sealed record Unit(string Name, double Pmin, double Pmax, double[] SegmentLength, double[] SegmentSlope,
        double FixedCostPerHour, double StartupCost, bool MustRun, UcUnitData Data);

    private static Unit BuildUnit(Generator gen, int g, UcUnitData d, int quadSegments, List<string> notes)
    {
        string name = gen.Name ?? $"gen {g} (bus {gen.Bus})";
        double pmin = gen.Pmin, pmax = gen.Pmax;
        if (!double.IsFinite(pmin) || double.IsNaN(pmax) || pmax < pmin)
            throw new ArgumentException($"Unit commitment: unit {name} has the range Pmin {pmin} .. Pmax {pmax}.");
        if (!double.IsFinite(pmax))
            throw new ArgumentException($"Unit commitment: unit {name} has no finite Pmax; a committed unit needs one.");
        if (d.MinUpPeriods < 1 || d.MinDownPeriods < 1)
            throw new ArgumentException($"Unit commitment: unit {name} minimum up / down times must be at least 1 period.");
        if (d.InitialPeriods < 0)
            throw new ArgumentException($"Unit commitment: unit {name} initial periods must be at least 0.");
        if (!(d.RampUpMw > 0) || !(d.RampDownMw > 0))
            throw new ArgumentException($"Unit commitment: unit {name} ramp limits must be above 0 MW (or unlimited).");
        foreach ((string what, double? val) in new[] { ("marginal cost", d.MarginalCost), ("no-load cost", d.NoLoadCost), ("start-up cost", d.StartupCost), ("initial output", d.InitialMw) })
            if (val is double z && !double.IsFinite(z))
                throw new ArgumentException($"Unit commitment: unit {name} {what} is not a finite number.");
        bool mustRun = d.MustRun || pmin < 0;

        double fixedCost = d.NoLoadCost ?? 0;
        double[] len, slope;
        if (d.MarginalCost is double mc)
        {
            len = pmax > pmin ? new[] { pmax - pmin } : Array.Empty<double>();
            slope = pmax > pmin ? new[] { mc } : Array.Empty<double>();
            fixedCost += mc * pmin;
        }
        else if (gen.Cost is GeneratorCost gc)
        {
            GeneratorCostCurve curve = GeneratorCostCurve.From(gc, name);
            var xs = new List<double> { pmin };
            if (curve.IsPiecewiseLinear)
            {
                foreach (double bp in curve.PointsMw) if (bp > pmin && bp < pmax) xs.Add(bp);
            }
            else if (curve.Quadratic != 0)
            {
                for (int k = 1; k < quadSegments; k++) xs.Add(pmin + (pmax - pmin) * k / quadSegments);
            }
            if (pmax > pmin) xs.Add(pmax);
            len = new double[xs.Count - 1];
            slope = new double[xs.Count - 1];
            for (int k = 0; k + 1 < xs.Count; k++)
            {
                len[k] = xs[k + 1] - xs[k];
                slope[k] = (curve.CostAt(xs[k + 1]) - curve.CostAt(xs[k])) / len[k];
            }
            fixedCost += curve.CostAt(pmin);
            if (!curve.IsPiecewiseLinear && curve.Quadratic != 0 && pmax > pmin)
                notes.Add($"unit {name}: quadratic cost cut into {quadSegments} chord segment(s) between {Fmt(pmin)} and {Fmt(pmax)} MW");
        }
        else
        {
            double fuel = DcOptimalPowerFlow.FuelCostPerMwh(gen.Type);
            len = pmax > pmin ? new[] { pmax - pmin } : Array.Empty<double>();
            slope = pmax > pmin ? new[] { fuel } : Array.Empty<double>();
            fixedCost += fuel * pmin;
        }
        double startup = d.StartupCost ?? gen.Cost?.StartupCost ?? 0;
        if (!double.IsFinite(startup) || startup < 0)
            throw new ArgumentException($"Unit commitment: unit {name} start-up cost {startup} must be a finite number at or above 0.");
        return new Unit(name, pmin, pmax, len, slope, fixedCost, startup, mustRun, d);
    }

    private static string Fmt(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Columns and equality rows; an inequality gets its own non-negative slack column.</summary>
    private sealed class LpBuilder
    {
        private readonly List<(int, int, double)> _a = new();
        private readonly List<double> _b = new(), _c = new(), _lo = new(), _hi = new();
        private readonly List<bool> _int = new();

        public int Rows => _b.Count;
        public int Columns => _c.Count;

        public int Col(double cost, double lo, double hi, bool integer)
        {
            _c.Add(cost); _lo.Add(lo); _hi.Add(hi); _int.Add(integer);
            return _c.Count - 1;
        }

        public void Eq(double rhs, List<(int Col, double Val)> terms)
        {
            int r = _b.Count;
            _b.Add(rhs);
            foreach ((int c, double v) in terms) if (v != 0) _a.Add((r, c, v));
        }

        public void Le(double rhs, List<(int Col, double Val)> terms)
        {
            int s = Col(0, 0, double.PositiveInfinity, false);
            terms.Add((s, 1.0));
            Eq(rhs, terms);
        }

        public MilpProblem Build() => new(
            new SparseLpProblem(_b.Count, _c.Count, _a, _b.ToArray(), _c.ToArray(), _lo.ToArray(), _hi.ToArray()), _int.ToArray());
    }
}
