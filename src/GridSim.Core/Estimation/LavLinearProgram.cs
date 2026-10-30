namespace GridSim.Core.Estimation;

/// <summary>How a <see cref="LavLinearProgram.Solve"/> call ended.</summary>
public enum LavLpStatus
{
    /// <summary>An optimal vertex was reached: no basis row can be released with a descent of the weighted sum.</summary>
    Optimal,

    /// <summary>No s rows of H are linearly independent (to the pivot tolerance): the linearised problem does not
    /// determine the correction (unobservable at this state).</summary>
    Singular,

    /// <summary>The pivot cap was reached before optimality (degenerate cycling or a numerical stall). Never a result.</summary>
    IterationLimit,

    /// <summary>A descent direction had no usable breakpoint (every candidate pivot was below the pivot tolerance), so
    /// the objective appeared to fall without bound. Cannot happen with H of full column rank except through round-off;
    /// reported, never taken as a result.</summary>
    NoBreakpoint,

    /// <summary>A vertex move produced a basis the refactorisation found singular (round-off in the move's pivot test).
    /// Reported, never taken as a result; distinct from <see cref="Singular"/>, which is about the start.</summary>
    BasisBreakdown,
}

/// <summary>The outcome of one <see cref="LavLinearProgram.Solve"/> call.</summary>
/// <param name="Status">How the solve ended; only <see cref="LavLpStatus.Optimal"/> carries a usable correction.</param>
/// <param name="Dx">The correction minimising sum_i w_i |r_i - H_i dx| (meaningful only when Optimal).</param>
/// <param name="Basis">The s measurement rows the optimum interpolates exactly (residual 0), in basis-position order.</param>
/// <param name="Objective">sum_i w_i |r_i - H_i dx| at <paramref name="Dx"/>.</param>
/// <param name="Pivots">Vertex moves taken (0 when the starting vertex was already optimal).</param>
public sealed record LavLpSolution(LavLpStatus Status, double[] Dx, int[] Basis, double Objective, int Pivots);

/// <summary>
/// N331 (CF5): the weighted least-absolute-values correction as a linear program, solved exactly by a vertex-descent
/// (simplex) method specialised to L1 fitting, in the form Abur and Celik use for LAV state estimation ("A fast algorithm
/// for the weighted least absolute value state estimation", IEEE Trans. Power Systems 6(1), 1991; Abur and Exposito,
/// "Power System State Estimation", 2004, ch. 6) and Barrodale and Roberts' L1 simplex uses for the line search.
///
/// The problem, at one Gauss-Newton iterate: given the measurement Jacobian H (m x s, full column rank), the residual
/// r = z - h(x) and weights w_i = 1 / sigma_i, find dx minimising f(dx) = sum_i w_i |r_i - H_i dx|. As a linear program
/// (dx free, e+ and e- at or above 0, H dx + e+ - e- = r, minimise w.(e+ + e-)) its optimal vertices interpolate s
/// linearly independent rows exactly. This class walks those vertices directly instead of building the 2s + 2m column
/// tableau:
///
/// 1. Vertex: a basis B of s independent rows; dx = H_B^-1 r_B (those rows' residuals are 0).
/// 2. Optimality (the dual test): with g = sum over i not in B of w_i sign(res_i) H_i and v = H_B^-T g, the vertex is
///    optimal when |v_k| &lt;= w_(B_k) for every basis position k (v is the dual y_B; the nonbasic duals are w_i sign(res_i)).
/// 3. Otherwise release the position k with the largest excess |v_k| - w_(B_k): moving along d = sign(v_k) H_B^-1 e_k keeps
///    the other basis rows at residual 0 and lifts row k off zero, at initial slope w_(B_k) - |v_k| &lt; 0.
/// 4. Line search (Barrodale-Roberts): f along d is convex and piecewise linear; each nonbasic row i with a_i = H_i d
///    != 0 is a breakpoint at t_i = res_i / a_i &gt; 0 where the slope rises by 2 w_i |a_i| (a row already at residual 0
///    is a breakpoint at t = 0 rising by w_i |a_i|). Walk the breakpoints in increasing t until the slope reaches 0;
///    that row enters the basis at position k. Each move is a strict decrease of f unless it is a degenerate (t = 0)
///    move, and f is bounded below, so the walk ends; a pivot cap guards degenerate cycling (fail loud).
/// 5. Degenerate vertices (end pass E, 2026-10-11): a nonbasic row whose residual is exactly 0 (after the round-off
///    snap <see cref="ZeroResidualTolerance"/>) keeps a side, +1 or -1, which says whether e+ or e- of that row is the
///    basic variable at value 0. It enters g on that side, and it is a breakpoint at t = 0 only when the move drives it
///    against its side. This makes step 2 the exact reduced-cost test of one LP basis. The first version gave such a
///    row the dual 0, which is no basis's dual: on four collinear points it released and re-entered the same two rows
///    until the pivot cap, and on an exact measurement set (every good residual 0 at the truth) the estimator failed.
///    After a move that does not lower f (beyond <see cref="DegenerateDecreaseTolerance"/>) the next move uses Bland's
///    rule (release the lowest row index, enter the first breakpoint), which cannot cycle. An optimum is reported only
///    from a fresh factorisation of its basis, so the result depends on the basis alone, not on the path to it.
///
/// Numerics: the basis inverse is held dense (s x s) and updated by the rank-one row-replacement formula, refactorised
/// from scratch every <see cref="RefactorInterval"/> moves; dx and every residual are recomputed from the current
/// inverse after each move (no drift carried in the residuals), and the basis rows' residuals are set to exactly 0.
/// Every loop runs serially in a fixed order with plain IEEE operations (no fused multiply-add, no transcendental), every
/// choice breaks ties by the lowest index after a relative margin, so the result is bit-identical on every CPU tier and
/// OS and on every run. Cost per move O(s^2 + nnz(H)); the starting basis costs O(m s^2) when no warm basis is given.
/// </summary>
public static class LavLinearProgram
{
    /// <summary>Basis-inverse refactorisation interval (moves). Bounds the round-off the rank-one updates accumulate.</summary>
    public const int RefactorInterval = 32;

    /// <summary>Relative optimality tolerance: position k is released only when |v_k| exceeds w_(B_k) by more than this
    /// times w_(B_k). Far above the round-off of v (a few ulp of its terms), far below any real descent.</summary>
    public const double OptimalityTolerance = 1e-10;

    /// <summary>Relative pivot tolerance: a breakpoint row whose |a_i| is at or below this times max|H_i| max|d| is not
    /// allowed to enter (it would make the new basis numerically singular). Also the rank test of the starting basis.</summary>
    public const double PivotTolerance = 1e-10;

    /// <summary>Relative tie margin for the release choice and for equal breakpoints: within this relative distance the
    /// lowest index wins, so near-ties never depend on the last bit of a sum (the N240 / N262 rule of the GridSim LPs).</summary>
    public const double TieMargin = 1e-12;

    /// <summary>End pass E (2026-10-11): relative zero test of a nonbasic residual. A residual at or below this times
    /// |r_i| + sum_j |H_ij| sum_k |Binv_jk r_Bk| (the size of every term it was computed from) is round-off of an exact
    /// zero and is set to 0. The bound of that round-off is a few ulp per term (about 2e-13 relative for 2,000 terms in
    /// the worst case); 1e-12 sits above it, and a genuine residual this small moves the objective by less than 1e-12 of
    /// the vertex's own numbers.</summary>
    public const double ZeroResidualTolerance = 1e-12;

    /// <summary>End pass E (2026-10-11): a move whose vertex lowers the objective by no more than this times the previous
    /// objective is degenerate (round-off level), and the next choice is made by Bland's rule so the walk cannot cycle.</summary>
    public const double DegenerateDecreaseTolerance = 1e-12;

    /// <summary>The pivot cap is this many moves per measurement row (plus <see cref="PivotCapFloor"/>).</summary>
    public const int PivotsPerRow = 20;

    /// <summary>The pivot cap's floor (moves), so a tiny problem still gets room for its degenerate moves.</summary>
    public const int PivotCapFloor = 200;

    /// <summary>
    /// Solve min sum_i w_i |r_i - H_i dx|. <paramref name="rows"/> holds H row by row as (column, value) pairs (columns
    /// 0..s-1, any order, no duplicates); <paramref name="r"/> and <paramref name="w"/> have one entry per row
    /// (w_i &gt; 0, finite). <paramref name="warmBasis"/>, when given and still independent, is the starting vertex (the
    /// previous iterate's optimum: the LAV active set rarely changes between Gauss-Newton iterations); otherwise a
    /// starting basis is chosen by QR with row pivoting (seeded with the warm rows that are still clearly independent).
    /// Refuses malformed input (ArgumentException).
    /// </summary>
    public static LavLpSolution Solve(IReadOnlyList<IReadOnlyList<(int Col, double Val)>> rows, double[] r, double[] w,
        int s, int[]? warmBasis = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(w);
        int m = rows.Count;
        if (s <= 0) throw new ArgumentOutOfRangeException(nameof(s), s, "the state size must be positive.");
        if (r.Length != m || w.Length != m)
            throw new ArgumentException($"r has {r.Length} and w {w.Length} entries; H has {m} rows. They must agree.", nameof(r));
        if (m < s)
            throw new ArgumentException($"{m} measurement rows cannot determine {s} states (fewer rows than states).", nameof(rows));

        // Dense copies of the rows (needed for the inverse and the dot products) and validation, in one pass.
        var hRows = new double[m][];
        var rowMax = new double[m];
        for (int i = 0; i < m; i++)
        {
            if (!double.IsFinite(r[i])) throw new ArgumentException($"residual {i} is {r[i]}; the LAV LP needs finite residuals.", nameof(r));
            if (!(w[i] > 0) || !double.IsFinite(w[i])) throw new ArgumentException($"weight {i} is {w[i]}; every weight must be finite and above 0.", nameof(w));
            IReadOnlyList<(int Col, double Val)> row = rows[i] ?? throw new ArgumentException($"row {i} is null.", nameof(rows));
            var dense = new double[s];
            foreach ((int col, double val) in row)
            {
                if ((uint)col >= (uint)s) throw new ArgumentException($"row {i} names column {col}, outside 0..{s - 1}.", nameof(rows));
                if (!double.IsFinite(val)) throw new ArgumentException($"row {i} column {col} is {val}; H must be finite.", nameof(rows));
                if (dense[col] != 0.0) throw new ArgumentException($"row {i} names column {col} twice.", nameof(rows));
                dense[col] = val;
                double absVal = Math.Abs(val);
                if (absVal > rowMax[i]) rowMax[i] = absVal;
            }
            hRows[i] = dense;
        }

        int[]? basis = null;
        double[,]? binv = null;
        if (warmBasis is not null && IsValidBasisShape(warmBasis, m, s))
        {
            basis = (int[])warmBasis.Clone();
            binv = Invert(hRows, rowMax, basis, s);   // null when the warm rows are no longer independent at this state
            if (binv is null) basis = null;
        }
        if (basis is null)
        {
            // Greedy start, seeded with the warm basis's rows that exist in this problem (a warm basis from a problem
            // with extra rows, or one that became dependent, still says which rows the optimum interpolated).
            basis = GreedyBasis(hRows, rowMax, w, s, warmBasis);
            if (basis is null) return new LavLpSolution(LavLpStatus.Singular, new double[s], Array.Empty<int>(), double.NaN, 0);
            binv = Invert(hRows, rowMax, basis, s);
            if (binv is null) return new LavLpSolution(LavLpStatus.Singular, new double[s], Array.Empty<int>(), double.NaN, 0);
        }

        var inBasis = new int[m];   // basis position + 1, or 0 for a nonbasic row
        for (int k = 0; k < s; k++) inBasis[basis[k]] = k + 1;

        // End pass E (2026-10-11): the side of every nonbasic row, +1 or -1. In the linear program each nonbasic row has
        // exactly one of e+ (residual above 0) or e- (residual below 0) in the basis; a row whose residual is (snapped
        // to) exactly 0 still has one of them basic, at value 0 (a degenerate vertex). Before this field the walk gave
        // such a row the dual 0 and a half-size breakpoint, which is not the dual of any basis of the LP: at a
        // degenerate vertex (four collinear points, an exact measurement set) it released and re-entered the same rows
        // for ever and stopped on the pivot cap. A row's side is its residual's sign when that is nonzero, and the side
        // it was last moved to when the residual is 0 (+1 before it has ever moved), so the dual test below is the
        // simplex method's reduced-cost test for one definite basis.
        var side = new int[m];
        Array.Fill(side, 1);

        var dx = new double[s];
        var dxScale = new double[s];
        var res = new double[m];
        var g = new double[s];
        var v = new double[s];
        var d = new double[s];
        var a = new double[m];
        var breakT = new List<(double T, int Row)>(m);
        int pivotCap = PivotCapFloor + PivotsPerRow * m;
        int sinceRefactor = 0;
        int pivots = 0;

        // True while binv is a fresh factorisation of the current basis (no rank-one updates since). A vertex is
        // reported only from a fresh factorisation, so the answer depends on the basis alone, never on the path that
        // reached it: a cold start and a warm start at the same basis return the same bits.
        bool fresh = true;

        // The objective of the vertex the last move left (NaN before the first move). When the vertex just reached does
        // not improve on it by more than DegenerateDecreaseTolerance, the next choice uses Bland's rule.
        double previousObjective = double.NaN;

        while (true)
        {
            // Vertex: dx = H_B^-1 r_B, residuals from scratch; basis rows exactly 0. dxScale_j = sum_k |Binv_jk r_Bk|
            // bounds the size of the terms dx_j was summed from, so the round-off of a residual is known.
            for (int j = 0; j < s; j++)
            {
                double acc = 0.0, mag = 0.0;
                for (int k = 0; k < s; k++)
                {
                    double term = binv![j, k] * r[basis[k]];
                    acc += term;
                    mag += Math.Abs(term);
                }
                dx[j] = acc;
                dxScale[j] = mag;
            }
            double objective = 0.0;
            for (int i = 0; i < m; i++)
            {
                if (inBasis[i] != 0) { res[i] = 0.0; continue; }
                double hi = 0.0;
                double scale = Math.Abs(r[i]);
                double[] row = hRows[i];
                for (int j = 0; j < s; j++)
                    if (row[j] != 0.0)
                    {
                        hi += row[j] * dx[j];
                        scale += Math.Abs(row[j]) * dxScale[j];
                    }
                double ri = r[i] - hi;
                // A residual within ZeroResidualTolerance of the size of the numbers it was computed from is round-off
                // of an exact 0 (the row is consistent with the basis rows): it is set to exactly 0 and keeps its side.
                // Without this, the sign of round-off (4e-16 one way or the other) decided the basis, and a degenerate
                // vertex flickered between two bases whose objectives differ only in the last bit.
                if (Math.Abs(ri) <= ZeroResidualTolerance * scale) ri = 0.0;
                res[i] = ri;
                if (ri > 0.0) side[i] = 1;
                else if (ri < 0.0) side[i] = -1;
                objective += w[i] * Math.Abs(ri);
            }
            if (!double.IsFinite(objective))
                return new LavLpSolution(LavLpStatus.Singular, dx, basis, objective, pivots);

            // Bland's rule (lowest row index) for the next move when the vertex just reached did not strictly improve on
            // the one before it: Dantzig's largest-excess choice can cycle among the bases of a degenerate vertex, and
            // Bland's choice with the short step below cannot (Bland 1977). Moves that do improve keep the faster rule.
            bool bland = !double.IsNaN(previousObjective)
                && !(objective < previousObjective - DegenerateDecreaseTolerance * Math.Abs(previousObjective));

            // Dual test: g = sum over nonbasic rows of w_i side_i H_i, v = H_B^-T g. A zero-residual row counts on its
            // side: it is the reduced cost of the basis that holds that row's e+ (side +1) or e- (side -1).
            Array.Clear(g);
            for (int i = 0; i < m; i++)
            {
                if (inBasis[i] != 0) continue;
                double sw = side[i] > 0 ? w[i] : -w[i];
                double[] row = hRows[i];
                for (int j = 0; j < s; j++) if (row[j] != 0.0) g[j] += sw * row[j];
            }
            for (int k = 0; k < s; k++)
            {
                double acc = 0.0;
                for (int j = 0; j < s; j++) acc += binv![j, k] * g[j];
                v[k] = acc;
            }

            // Release choice. Dantzig: the largest relative excess (|v_k| - w) / w, first index among near-ties.
            // Bland: the eligible position whose basis row has the lowest index.
            int leave = -1;
            double best = OptimalityTolerance;
            for (int k = 0; k < s; k++)
            {
                double wk = w[basis[k]];
                double excess = (Math.Abs(v[k]) - wk) / wk;
                if (bland)
                {
                    if (excess > OptimalityTolerance && (leave < 0 || basis[k] < basis[leave])) leave = k;
                }
                else if (excess > best * (1.0 + TieMargin) && excess > OptimalityTolerance) { best = excess; leave = k; }
            }
            if (leave < 0)
            {
                // Optimal at this basis. Reported only from a fresh factorisation: if binv carries rank-one updates,
                // refactorise and evaluate the same vertex again (not a move: the pivot count is unchanged). The second
                // look normally agrees; if round-off made it disagree, the walk simply continues from there.
                if (fresh) return new LavLpSolution(LavLpStatus.Optimal, dx, basis, objective, pivots);
                binv = Invert(hRows, rowMax, basis, s);
                sinceRefactor = 0;
                fresh = true;
                if (binv is null) return new LavLpSolution(LavLpStatus.BasisBreakdown, dx, basis, objective, pivots);
                continue;
            }
            if (pivots >= pivotCap) return new LavLpSolution(LavLpStatus.IterationLimit, dx, basis, objective, pivots);

            // Direction d = sigma H_B^-1 e_leave: row `leave` moves off zero to the residual -sigma t.
            double sigma = v[leave] > 0 ? 1.0 : -1.0;
            double dMax = 0.0;
            for (int j = 0; j < s; j++) { d[j] = sigma * binv![j, leave]; double ad = Math.Abs(d[j]); if (ad > dMax) dMax = ad; }

            // Breakpoints along d. A nonzero residual crosses zero at t = res / a when that is positive. A zero residual
            // is a breakpoint at t = 0 only when the move drives it against its side (side * a > 0: its basic e
            // variable would go negative); moving it further along its side is already in the slope. Every crossing
            // raises the slope by 2 w |a| (the row's term turns from -w |a| to +w |a|).
            breakT.Clear();
            for (int i = 0; i < m; i++)
            {
                a[i] = 0.0;
                if (inBasis[i] != 0) continue;
                double ai = 0.0;
                double[] row = hRows[i];
                for (int j = 0; j < s; j++) if (row[j] != 0.0) ai += row[j] * d[j];
                a[i] = ai;
                if (Math.Abs(ai) <= PivotTolerance * rowMax[i] * dMax) continue;   // cannot enter: near-singular basis
                if (res[i] == 0.0)
                {
                    if (side[i] * ai > 0.0) breakT.Add((0.0, i));
                }
                else
                {
                    double t = res[i] / ai;
                    if (t > 0) breakT.Add((t, i));
                }
            }
            // Increasing t; equal t (within the tie margin) ordered by row. "Within the margin" is not transitive, so for a
            // chain of three or more near-equal breakpoints the comparer is not a strict weak order and the result depends
            // on the sort algorithm and the input order. Both are fixed: the rows are added in row order above, and the
            // sort is GridSim's frozen copy of the .NET 10 introsort (FrozenIntroSort), so the walk is the same on every
            // runtime (and, on .NET 10, exactly what List.Sort gave).
            Numerics.FrozenIntroSort.Sort(breakT, (x, y) =>
            {
                double scale = Math.Max(Math.Abs(x.T), Math.Abs(y.T));
                if (Math.Abs(x.T - y.T) <= TieMargin * scale) return x.Row.CompareTo(y.Row);
                return x.T.CompareTo(y.T);
            });

            // Long step (Barrodale-Roberts): walk the breakpoints until the slope reaches 0; the rows passed change
            // side. Under Bland's rule the short step: the first breakpoint enters (the textbook minimum-ratio pivot,
            // lowest row index among ties), which is what Bland's termination proof needs.
            double slope = w[basis[leave]] - Math.Abs(v[leave]);   // < 0 by the release test
            int enter = -1;
            int passedCount = 0;
            foreach ((double t, int row) in breakT)
            {
                slope += 2.0 * w[row] * Math.Abs(a[row]);
                if (slope >= 0.0 || bland) { enter = row; break; }
                passedCount++;
            }
            if (enter < 0) return new LavLpSolution(LavLpStatus.NoBreakpoint, dx, basis, objective, pivots);
            for (int p = 0; p < passedCount; p++)
            {
                int row = breakT[p].Row;
                side[row] = a[row] > 0.0 ? -1 : 1;   // its residual res - t a has crossed to the sign of -a
            }

            // Basis change: position `leave` now holds `enter`; the released row goes to the side it moved to.
            int leaving = basis[leave];
            basis[leave] = enter;
            inBasis[leaving] = 0;
            inBasis[enter] = leave + 1;
            side[leaving] = sigma > 0 ? -1 : 1;
            previousObjective = objective;
            pivots++;
            fresh = false;

            sinceRefactor++;
            if (sinceRefactor >= RefactorInterval || !UpdateInverse(binv!, hRows[enter], leave, s))
            {
                binv = Invert(hRows, rowMax, basis, s);
                sinceRefactor = 0;
                fresh = true;
                if (binv is null) return new LavLpSolution(LavLpStatus.BasisBreakdown, dx, basis, objective, pivots);
            }
        }
    }

    /// <summary>The weighted L1 objective sum_i w_i |r_i - H_i dx| (a helper for callers and tests).</summary>
    public static double Objective(IReadOnlyList<IReadOnlyList<(int Col, double Val)>> rows, double[] r, double[] w, double[] dx)
    {
        double f = 0.0;
        for (int i = 0; i < rows.Count; i++)
        {
            double hi = 0.0;
            foreach ((int col, double val) in rows[i]) hi += val * dx[col];
            f += w[i] * Math.Abs(r[i] - hi);
        }
        return f;
    }

    private static bool IsValidBasisShape(int[] basis, int m, int s)
    {
        if (basis.Length != s) return false;
        var seen = new HashSet<int>();
        foreach (int b in basis) if ((uint)b >= (uint)m || !seen.Add(b)) return false;
        return true;
    }

    /// <summary>The independence floor of the starting basis: a row can be chosen only while the part of it orthogonal to
    /// the rows already chosen has a 2-norm above this fraction of its own 2-norm. Far above round-off.</summary>
    public const double GreedyRankTolerance = 1e-6;

    /// <summary>A warm-basis row (seed) is kept in the start only while its orthogonal remainder is at least this
    /// fraction of its norm: a seed must be clearly independent, never merely above the floor.</summary>
    public const double SeedRankFraction = 0.1;

    /// <summary>
    /// A starting basis by QR with row pivoting (Businger-Golub on H^T). Every row is scaled to unit 2-norm; the seed rows
    /// (the warm basis's rows that exist here, in its order) are taken first while each is clearly independent
    /// (<see cref="SeedRankFraction"/>); then at every step the row whose remaining orthogonal part is LARGEST is taken
    /// (ties within the tie margin to the lowest index) and every other row is orthogonalised against it. Choosing the
    /// largest remainder, not just any above a floor, keeps the basis well conditioned: a first-come rule (Gaussian
    /// elimination, then Gram-Schmidt with a floor) picked a basis on case118 at flat start whose inverse met a pivot of
    /// 2e-14 (the flat-start Jacobian's Laplacian-like blocks make many near-dependent subsets). Serial, fixed order.
    /// Null when fewer than s rows clear <see cref="GreedyRankTolerance"/>. Cost O(m s^2), memory m x s.
    /// </summary>
    private static int[]? GreedyBasis(double[][] hRows, double[] rowMax, double[] w, int s, int[]? seed)
    {
        _ = w;   // the choice is by geometry only; weights decide nothing at the start (the vertex walk optimises)
        int m = hRows.Length;
        var rem = new double[m][];
        var norm2 = new double[m];
        var alive = new bool[m];
        for (int i = 0; i < m; i++)
        {
            if (rowMax[i] == 0.0) continue;
            var v = new double[s];
            double n2 = 0.0;
            for (int j = 0; j < s; j++) { v[j] = hRows[i][j] / rowMax[i]; n2 += v[j] * v[j]; }
            double inv = 1.0 / Math.Sqrt(n2);
            n2 = 0.0;
            for (int j = 0; j < s; j++) { v[j] *= inv; n2 += v[j] * v[j]; }
            rem[i] = v;
            norm2[i] = n2;   // 1 to round-off
            alive[i] = true;
        }

        var chosen = new List<int>(s);
        var qv = new double[s];
        void Take(int pick)
        {
            double nrm = Math.Sqrt(norm2[pick]);
            for (int j = 0; j < s; j++) qv[j] = rem[pick][j] / nrm;
            alive[pick] = false;
            chosen.Add(pick);
            for (int i = 0; i < m; i++)
            {
                if (!alive[i]) continue;
                double[] v = rem[i];
                double dot = 0.0;
                for (int j = 0; j < s; j++) dot += qv[j] * v[j];
                if (dot != 0.0)
                    for (int j = 0; j < s; j++) v[j] -= dot * qv[j];
                double n2 = 0.0;
                for (int j = 0; j < s; j++) n2 += v[j] * v[j];
                norm2[i] = n2;
            }
        }

        if (seed is not null)
            foreach (int b in seed)
            {
                if (chosen.Count == s) break;
                if ((uint)b >= (uint)m || !alive[b]) continue;
                if (norm2[b] >= SeedRankFraction * SeedRankFraction) Take(b);
            }
        double floor2 = GreedyRankTolerance * GreedyRankTolerance;
        while (chosen.Count < s)
        {
            int pick = -1;
            double best = floor2;
            for (int i = 0; i < m; i++)
                if (alive[i] && norm2[i] > best * (1.0 + TieMargin)) { best = norm2[i]; pick = i; }
            if (pick < 0) return null;
            Take(pick);
        }
        return chosen.ToArray();
    }

    /// <summary>Dense Gauss-Jordan inverse of H_B (rows of H named by <paramref name="basis"/>) with partial pivoting
    /// (largest |pivot|, first row among ties), on the row-equilibrated matrix (each row divided by its largest entry, so
    /// a |V| row with entries near 1 and an injection row with entries in the tens are judged alike): H_B = D R gives
    /// H_B^-1 = R^-1 D^-1, so column k of R^-1 is divided by row k's scale. Null when an equilibrated pivot is at or below
    /// <see cref="PivotTolerance"/> (singular).</summary>
    private static double[,]? Invert(double[][] hRows, double[] rowMax, int[] basis, int s)
    {
        var aug = new double[s, 2 * s];
        for (int k = 0; k < s; k++)
        {
            double[] row = hRows[basis[k]];
            double d = rowMax[basis[k]];
            if (d == 0.0) return null;
            for (int j = 0; j < s; j++) aug[k, j] = row[j] / d;
            aug[k, s + k] = 1.0;
        }
        for (int c = 0; c < s; c++)
        {
            int p = -1;
            double pmax = PivotTolerance;
            for (int k = c; k < s; k++) { double av = Math.Abs(aug[k, c]); if (av > pmax) { pmax = av; p = k; } }
            if (p < 0) return null;
            if (p != c)
                for (int j = 0; j < 2 * s; j++) (aug[c, j], aug[p, j]) = (aug[p, j], aug[c, j]);
            double piv = aug[c, c];
            for (int j = 0; j < 2 * s; j++) aug[c, j] /= piv;
            for (int k = 0; k < s; k++)
            {
                if (k == c) continue;
                double f = aug[k, c];
                if (f == 0.0) continue;
                for (int j = 0; j < 2 * s; j++) aug[k, j] -= f * aug[c, j];
            }
        }
        // aug's right half is R^-1, whose column k pairs with basis position k; divide by the row scales: H_B^-1.
        var inv = new double[s, s];
        for (int i = 0; i < s; i++)
            for (int j = 0; j < s; j++) inv[i, j] = aug[i, s + j] / rowMax[basis[j]];
        return inv;
    }

    /// <summary>
    /// Rank-one update of the basis inverse when position <paramref name="k"/> of H_B is replaced by row
    /// <paramref name="hNew"/>: Binv' = Binv - c u^T / delta, c = Binv e_k, u = Binv^T hNew - e_k, delta = hNew . c.
    /// False (no change made) when delta is too small for a stable update; the caller then refactorises.
    /// </summary>
    private static bool UpdateInverse(double[,] binv, double[] hNew, int k, int s)
    {
        var c = new double[s];
        double delta = 0.0, cMax = 0.0, hMax = 0.0;
        for (int i = 0; i < s; i++)
        {
            c[i] = binv[i, k];
            if (hNew[i] != 0.0) delta += hNew[i] * c[i];
            cMax = Math.Max(cMax, Math.Abs(c[i]));
            hMax = Math.Max(hMax, Math.Abs(hNew[i]));
        }
        if (!(Math.Abs(delta) > PivotTolerance * cMax * hMax)) return false;
        var u = new double[s];
        for (int j = 0; j < s; j++)
        {
            double acc = 0.0;
            for (int i = 0; i < s; i++) if (hNew[i] != 0.0) acc += hNew[i] * binv[i, j];
            u[j] = acc;
        }
        u[k] -= 1.0;
        for (int i = 0; i < s; i++)
        {
            double ci = c[i] / delta;
            if (ci == 0.0) continue;
            for (int j = 0; j < s; j++) binv[i, j] -= ci * u[j];
        }
        return true;
    }
}
