using GridSim.Core.Numerics;

namespace GridSim.Core.Analysis;

/// <summary>
/// The security-constrained DC-OPF linear program solved WITHOUT the dense matrices, giving bit-for-bit the
/// answer of the dense reference (October 2026 review, R005).
///
/// THE PROBLEM. <see cref="DcOptimalPowerFlow"/> used to build the LP as a dense (1 + L) x (G + L) matrix (one
/// balance row, one thermal row per rated line; G dispatch columns, L line slacks) and hand it to the dense
/// <see cref="BoundedSimplex"/>, which copies it into an m x (n + m) working matrix and keeps a dense m x m basis
/// inverse that it rebuilds by an O(m^3) Gauss-Jordan every 64 iterations. On gb-full (5,876 rated lines, 5,318
/// generators) that is about 1.9 GB of arrays and hours of arithmetic.
///
/// THE APPROACH. This class runs EXACTLY the same algorithm as <see cref="BoundedSimplex.Solve"/> - the same
/// two-phase bounded-variable primal simplex, the same Dantzig pricing with the same first-index tie-break, the
/// same ratio test with the same tie margin, the same eta updates, the same refactorisation schedule, pivot
/// choice and clamp - performing the same floating-point operations in the same order on every value that can
/// be non-zero. It only changes how the matrices are STORED:
///   * The constraint matrix is never formed. A dispatch column is the balance-row 1 plus one PTDF entry per
///     rated line, and generators at the same bus share one PTDF column, so the PTDF is held once per distinct
///     generator bus (rated lines x distinct buses, about 73 MB on gb-full). A slack or artificial column is a
///     single +/-1 and is never stored at all.
///   * The basis inverse (and the Gauss-Jordan working matrix it is rebuilt from) is held by
///     <see cref="DcOpfHybridColumns"/>: a column that still has one non-zero is stored as that one value, and
///     only the columns the simplex has actually filled in are stored densely.
///   * Pricing evaluates one reduced cost per (bus, objective coefficient) group of generators: generators with
///     an identical column and identical cost have bit-identical reduced costs, because each is computed by the
///     same sequence of operations on the same numbers.
/// The skipped operations are only multiplications by an exact zero and the additions or subtractions of their
/// zero results. In IEEE 754 arithmetic those change at most the sign of a zero, never a non-zero value, and a
/// signed zero cannot reach a non-zero result anywhere in this algorithm (see <see cref="DcOpfHybridColumns"/>).
/// So the dispatch, the status and every intermediate non-zero are the dense reference's, bit for bit; the tests
/// in <c>DcOpfSecuritySimplexParityTests</c> compare the two solvers bit-for-bit on the bundled cases and on
/// randomised degenerate LPs.
///
/// No floor, tolerance or iteration cap is changed: every constant is read from <see cref="BoundedSimplex"/>
/// (N010: its absolute floors are deliberately left as they are).
/// </summary>
internal sealed class DcOpfSecuritySimplex
{
    /// <summary>The LP, in the DC-OPF's structured form:
    ///   minimise  sum_j C[j] x[j]
    ///   subject to  sum_{i &lt; G} x[i] = B[0]                                          (balance row)
    ///               sum_{i &lt; G} Ptdf[r, GenColumn[i]] x[i] + x[G + r] = B[1 + r]      (thermal row r, r &lt; L)
    ///               Lo[j] &lt;= x[j] &lt;= Hi[j]
    /// with Ptdf row-major (L rows by <paramref name="DistinctColumns"/> columns). This is the dense reference's
    /// A matrix, row for row and entry for entry.</summary>
    internal sealed record Problem(
        int Generators, int Lines, int DistinctColumns, int[] GenColumn, double[] Ptdf,
        double[] B, double[] C, double[] Lo, double[] Hi)
    {
        /// <summary>N240: the relative pricing tie margin, exactly as <see cref="BoundedSimplex.Solve"/>'s
        /// <c>pricingTieMargin</c>. 0 (the default) is the strict rule; the DC-OPF builds its LPs with
        /// <see cref="BoundedSimplex.DcOpfPricingTieMargin"/>.</summary>
        public double PricingTieMargin { get; init; }
    }

    /// <summary>Counters from the last solve (for tests and the performance notes).</summary>
    /// <param name="TieBreaks">N240: how many pivots the pricing tie margin decided differently from the strict rule (the
    /// entering column moved to a lower index). When it is 0 the pivot path is, statement for statement, the path of the
    /// strict rule, so every number is bit-identical to a run with the margin at 0.</param>
    internal sealed record Diagnostics(int PhaseOneIterations, int PhaseTwoIterations, int Refactorisations, int MaxDenseInverseColumns,
        int TieBreaks = 0);

    // ---- Problem data -----------------------------------------------------------------------------------
    private readonly int _g;       // generators (dispatch columns 0 .. g-1)
    private readonly int _lines;   // rated lines (slack columns g .. g+L-1, thermal rows 1 .. L)
    private readonly int _u;       // distinct PTDF columns
    private readonly int[] _genCol;
    private readonly double[] _ptdf;
    private readonly int _m;       // rows = 1 + L
    private readonly int _n;       // structural columns = g + L
    private readonly int _total;   // structural + one artificial per row

    // ---- Simplex state (the dense reference's variables of the same names) -------------------------------
    private readonly double[] _sign;    // +1, or -1 for a row the reference negated (its A row and rhs flipped)
    private readonly double[] _rhs;
    private readonly double[] _loX, _hiX, _cost, _phase1, _val;
    private readonly bool[] _atUpper, _inBasis;
    private readonly int[] _basis;
    private DcOpfHybridColumns _bInv;

    // ---- Diagnostics ---------------------------------------------------------------------------------------
    private int _iter1, _iter2, _refactors, _maxDense, _tieBreaks;

    // ---- N240: the pricing tie margin of this solve (0 = the strict rule, the default).
    private double _pricingTieMargin;

    // ---- F19: the Phase II multipliers of the optimal basis (a copy taken when optimality is proved; read only by
    // ---- SolveDetailed, never by the simplex itself, so recording it cannot change any number the LP computes).
    private double[]? _finalY;

    private DcOpfSecuritySimplex(Problem p)
    {
        _g = p.Generators; _lines = p.Lines; _u = p.DistinctColumns; _genCol = p.GenColumn; _ptdf = p.Ptdf;
        _m = 1 + _lines; _n = _g + _lines; _total = _n + _m;
        _sign = new double[_m];
        _rhs = new double[_m];
        _loX = new double[_total]; _hiX = new double[_total]; _cost = new double[_total]; _phase1 = new double[_total];
        _val = new double[_total];
        _atUpper = new bool[_total]; _inBasis = new bool[_total];
        _basis = new int[_m];
        _bInv = new DcOpfHybridColumns(_m, _m);
    }

    /// <summary>Solve the structured LP. Returns exactly what <see cref="BoundedSimplex.Solve"/> returns for the
    /// equivalent dense matrix with its default tolerance and iteration cap: the status and the structural x.</summary>
    internal static (LpStatus Status, double[] X, Diagnostics Diagnostics) Solve(Problem p)
    {
        Detailed d = SolveDetailed(p);
        return (d.Status, d.X, d.Diagnostics);
    }

    /// <summary>The outcome of <see cref="SolveDetailed"/>: what <see cref="Solve"/> returns plus the optimal basis's
    /// dual values (F19).</summary>
    /// <param name="Status">The LP status.</param>
    /// <param name="X">The structural solution (generator columns, then one slack per thermal row).</param>
    /// <param name="Diagnostics">Iteration counters.</param>
    /// <param name="Duals">The dual value (simplex multiplier) of every row of the ORIGINAL problem, in the original
    /// row signs: entry 0 the balance row, entry 1 + r thermal row r. It is <c>y = c_B B^-1</c> for the final optimal
    /// basis, the same vector the last pricing pass used to prove optimality, mapped back through any row negation the
    /// start-up made (a negated row's multiplier changes sign). Each entry is the change in the optimal cost per unit
    /// increase in that row's right-hand side (while the basis stays optimal). Null unless the status is Optimal.</param>
    internal sealed record Detailed(LpStatus Status, double[] X, Diagnostics Diagnostics, double[]? Duals);

    /// <summary>
    /// Solve the structured LP and also return the dual values of the optimal basis (F19: nodal prices and line
    /// shadow prices). The simplex arithmetic is EXACTLY that of <see cref="Solve"/> (it is the same run: the duals are
    /// copied out of the final pricing pass, after the last operation that touches the solution), so X and the status
    /// are bit-identical to <see cref="Solve"/>'s.
    /// </summary>
    internal static Detailed SolveDetailed(Problem p)
    {
        Validate(p);
        var s = new DcOpfSecuritySimplex(p);
        LpStatus status = s.Run(p);
        var x = new double[s._n];
        Array.Copy(s._val, x, s._n);
        double[]? duals = null;
        if (status == LpStatus.Optimal && s._finalY is not null)
        {
            // Map the working-row multipliers back to the original rows: the start-up negated row i (A row and right-hand
            // side) when its initial residual was negative, so its multiplier in the original sign is sign_i * y_i.
            duals = new double[s._m];
            for (int i = 0; i < s._m; i++) duals[i] = s._sign[i] * s._finalY[i];
        }
        return new Detailed(status, x, new Diagnostics(s._iter1, s._iter2, s._refactors, s._maxDense, s._tieBreaks), duals);
    }

    private static void Validate(Problem p)
    {
        if (p.Generators < 0 || p.Lines < 0 || p.DistinctColumns < 0)
            throw new ArgumentException("DcOpfSecuritySimplex: negative problem size.", nameof(p));
        int n = p.Generators + p.Lines;
        if (p.GenColumn.Length != p.Generators) throw new ArgumentException("DcOpfSecuritySimplex: GenColumn must have one entry per generator.", nameof(p));
        if (p.Ptdf.Length != (long)p.Lines * p.DistinctColumns) throw new ArgumentException("DcOpfSecuritySimplex: Ptdf must be Lines x DistinctColumns.", nameof(p));
        if (p.B.Length != 1 + p.Lines) throw new ArgumentException("DcOpfSecuritySimplex: B must have 1 + Lines entries.", nameof(p));
        if (p.C.Length != n || p.Lo.Length != n || p.Hi.Length != n) throw new ArgumentException("DcOpfSecuritySimplex: C, Lo and Hi must have Generators + Lines entries.", nameof(p));
        foreach (int u in p.GenColumn)
            if (u < 0 || u >= p.DistinctColumns) throw new ArgumentException($"DcOpfSecuritySimplex: GenColumn entry {u} is outside 0..{p.DistinctColumns - 1}.", nameof(p));
        BoundedSimplex.CheckTieMargin(p.PricingTieMargin);   // N240
    }

    // =========================================================================================================
    // The working matrix A (after the reference's row negation), read entry by entry without being stored.
    // =========================================================================================================

    /// <summary>A[i, j] for a dispatch column j &lt; g: the balance-row 1 or the PTDF entry, times the row sign.
    /// The reference negates a row with <c>A = -A</c>; <c>-1.0 * x</c> is bit-identical to <c>-x</c>.</summary>
    private double GenEntry(int i, int j) => i == 0 ? _sign[0] * 1.0 : _sign[i] * _ptdf[(i - 1) * _u + _genCol[j]];

    /// <summary>The row and value of the single non-zero of a slack (j in g..n-1) or artificial (j &gt;= n) column.</summary>
    private (int Row, double Value) UnitColumn(int j)
    {
        if (j < _n) { int row = 1 + (j - _g); return (row, _sign[row] * 1.0); }
        return (j - _n, 1.0);
    }

    // =========================================================================================================
    // BoundedSimplex.Solve, step for step.
    // =========================================================================================================

    private LpStatus Run(Problem p)
    {
        double tol = BoundedSimplex.DefaultTolerance;
        int maxIter = BoundedSimplex.DefaultMaxIterations;
        _pricingTieMargin = p.PricingTieMargin;

        for (int i = 0; i < _m; i++) { _rhs[i] = p.B[i]; _sign[i] = 1.0; }
        for (int j = 0; j < _n; j++)
        {
            _loX[j] = p.Lo[j]; _hiX[j] = p.Hi[j]; _cost[j] = p.C[j];
            if (!double.IsInfinity(p.Lo[j])) { _val[j] = p.Lo[j]; _atUpper[j] = false; }
            else if (!double.IsInfinity(p.Hi[j])) { _val[j] = p.Hi[j]; _atUpper[j] = true; }
            else { _val[j] = 0.0; _atUpper[j] = false; }
        }

        // Initial residual r_i = rhs_i - sum_{j < n} A[i, j] val[j] (j ascending), negating the row when it is
        // negative. The dispatch terms are evaluated in full (every generator, in index order, as the reference
        // does); a slack column touches only its own row.
        for (int i = 0; i < _m; i++)
        {
            double r = _rhs[i];
            for (int j = 0; j < _g; j++) r -= GenEntry(i, j) * _val[j];
            // The only slack column with a non-zero in row i is row i's own (thermal rows only); every other
            // slack term is 0 x val, which is value-neutral. _sign[i] is still +1 here (rows are negated below).
            if (i > 0) { int s = _g + (i - 1); r -= (_sign[i] * 1.0) * _val[s]; }
            if (r < 0) { _sign[i] = -1.0; _rhs[i] = -_rhs[i]; r = -r; }
            int art = _n + i;
            _loX[art] = 0.0; _hiX[art] = double.PositiveInfinity;
            _cost[art] = 0.0; _phase1[art] = 1.0;
            _val[art] = r; _atUpper[art] = false;
        }

        for (int i = 0; i < _m; i++) { _basis[i] = _n + i; _inBasis[_n + i] = true; }
        // The identity inverse: every column a single 1 on the diagonal.
        _bInv = new DcOpfHybridColumns(_m, _m);
        for (int i = 0; i < _m; i++) _bInv.SetSingle(i, i, 1.0);

        // ---- Phase I: minimise the sum of artificials. ----
        LpStatus s1 = RunSimplex(_phase1, tol, maxIter, phaseOne: true);
        if (s1 == LpStatus.IterationLimit) return LpStatus.IterationLimit;

        double artSum = 0;
        for (int i = 0; i < _m; i++) if (_basis[i] >= _n) artSum += Math.Abs(_val[_basis[i]]);
        for (int j = _n; j < _total; j++) if (!_inBasis[j]) artSum += Math.Abs(_val[j]);
        if (!(artSum <= BoundedSimplex.PhaseOneFeasibilityTolerance)) return LpStatus.Infeasible;   // NaN reads infeasible, as in BoundedSimplex

        for (int j = _n; j < _total; j++) { _loX[j] = 0.0; _hiX[j] = 0.0; if (!_inBasis[j]) { _val[j] = 0.0; _atUpper[j] = false; } }
        return RunSimplex(_cost, tol, maxIter, phaseOne: false);
    }

    /// <summary>One phase of the bounded-variable primal simplex (the reference's <c>RunSimplex</c>).</summary>
    private LpStatus RunSimplex(double[] obj, double tol, int maxIter, bool phaseOne)
    {
        var alpha = new double[_m];
        var y = new double[_m];
        var costOfRow = new double[_m];
        var costRows = new List<int>(_m);
        var yRows = new List<int>(_m);
        var aq = new double[_m];
        var fRows = new List<int>(_m);
        int sinceRefactor = 0;

        // Generator pricing groups for this phase: generators with the same PTDF column and the same objective
        // coefficient (compared bit for bit) get one shared reduced cost.
        (int[] groupOf, int[] groupCol, double[] groupObj) = BuildPricingGroups(obj);
        var dGroup = new double[groupCol.Length];

        for (int iter = 0; iter < maxIter; iter++)
        {
            if (phaseOne) _iter1++; else _iter2++;
            if (sinceRefactor >= BoundedSimplex.RefactorInterval) { Refactorize(); RecomputeBasics(); sinceRefactor = 0; }
            _maxDense = Math.Max(_maxDense, _bInv.DenseColumnCount);

            // Dual multipliers y = cB . Binv: y[j] = sum_i (ascending) obj[basis[i]] * Binv[i, j].
            costRows.Clear();
            for (int i = 0; i < _m; i++)
            {
                costOfRow[i] = obj[_basis[i]];
                if (costOfRow[i] != 0.0) costRows.Add(i);
            }
            yRows.Clear();
            for (int j = 0; j < _m; j++)
            {
                y[j] = _bInv.WeightedColumnSum(j, costOfRow, costRows);
                if (y[j] != 0.0) yRows.Add(j);
            }

            // Generator reduced costs, one per group: d = obj; for i ascending: d -= y[i] * A[i, j].
            for (int k = 0; k < dGroup.Length; k++) dGroup[k] = groupObj[k];
            foreach (int i in yRows)
            {
                double yi = y[i];
                if (i == 0)
                {
                    double a0 = _sign[0] * 1.0;
                    for (int k = 0; k < dGroup.Length; k++) dGroup[k] -= yi * a0;
                }
                else
                {
                    double si = _sign[i];
                    int off = (i - 1) * _u;
                    for (int k = 0; k < dGroup.Length; k++) dGroup[k] -= yi * (si * _ptdf[off + groupCol[k]]);
                }
            }

            // Price the nonbasic variables in index order; pick the most promising (Dantzig) direction.
            int q = -1; double best = tol; int qDir = 0;
            for (int j = 0; j < _total; j++)
            {
                if (_inBasis[j]) continue;
                if (_loX[j] == _hiX[j]) continue;
                double d;
                if (j < _g) d = dGroup[groupOf[j]];
                else
                {
                    (int row, double a) = UnitColumn(j);
                    d = obj[j];
                    d -= y[row] * a;
                }
                bool free = double.IsInfinity(_loX[j]) && double.IsInfinity(_hiX[j]);
                if ((!_atUpper[j] || free) && d < -best) { best = -d; q = j; qDir = +1; }
                else if ((_atUpper[j] || free) && d > best) { best = d; q = j; qDir = -1; }
            }
            if (q < 0)
            {
                // F19: keep the Phase II multipliers that proved optimality (a copy; nothing below reads it).
                if (!phaseOne) _finalY = (double[])y.Clone();
                return LpStatus.Optimal;
            }

            // N240: the reference's lowest-index tie-break (BoundedSimplex.RunSimplex), with the same reduced costs: the
            // first eligible column before q whose score is within the relative margin of the best. Skipped at margin 0.
            if (_pricingTieMargin > 0)
            {
                double floor = best - _pricingTieMargin * best;
                for (int j = 0; j < q; j++)
                {
                    if (_inBasis[j]) continue;
                    if (_loX[j] == _hiX[j]) continue;
                    double d;
                    if (j < _g) d = dGroup[groupOf[j]];
                    else
                    {
                        (int row, double a) = UnitColumn(j);
                        d = obj[j];
                        d -= y[row] * a;
                    }
                    (double score, int dir) = BoundedSimplex.PricingScore(d, _atUpper[j], double.IsInfinity(_loX[j]) && double.IsInfinity(_hiX[j]), tol);
                    if (dir != 0 && score >= floor) { q = j; qDir = dir; _tieBreaks++; break; }
                }
            }

            // Entering column in the basis metric: alpha = Binv . A[:, q].
            Array.Clear(aq);
            if (q < _g) { for (int i = 0; i < _m; i++) aq[i] = GenEntry(i, q); }
            else { (int row, double a) = UnitColumn(q); aq[row] = a; }
            _bInv.MultiplyVector(aq, alpha);

            // Ratio test (identical to the reference).
            double tMax = double.PositiveInfinity; int leave = -1; bool leaveToUpper = false;
            double gap = _hiX[q] - _loX[q];
            if (!double.IsInfinity(gap)) tMax = gap;
            for (int i = 0; i < _m; i++)
            {
                double rate = qDir * alpha[i];
                int bi = _basis[i];
                if (rate > tol)
                {
                    double room = _val[bi] - _loX[bi];
                    double t = room / rate;
                    if (t < tMax - BoundedSimplex.RatioTieMargin) { tMax = t; leave = i; leaveToUpper = false; }
                }
                else if (rate < -tol)
                {
                    double hib = _hiX[bi];
                    if (double.IsInfinity(hib)) continue;
                    double room = hib - _val[bi];
                    double t = room / -rate;
                    if (t < tMax - BoundedSimplex.RatioTieMargin) { tMax = t; leave = i; leaveToUpper = true; }
                }
            }

            if (double.IsInfinity(tMax)) return LpStatus.Unbounded;
            if (tMax < 0) tMax = 0;

            // N112: refuse a pivot below the eta floor before the step and the basis change, exactly as the reference does
            // (BoundedSimplex.EtaPivotFloor; unreachable with this solver's fixed tolerance 1e-9).
            if (leave >= 0) BoundedSimplex.RequirePivotAboveFloor(alpha[leave], tol, nameof(DcOpfSecuritySimplex));

            for (int i = 0; i < _m; i++) _val[_basis[i]] -= qDir * tMax * alpha[i];
            _val[q] += qDir * tMax;

            if (leave < 0)
            {
                _atUpper[q] = !_atUpper[q];
                _val[q] = _atUpper[q] ? _hiX[q] : _loX[q];
                sinceRefactor++;
                continue;
            }

            int outVar = _basis[leave];
            _inBasis[outVar] = false; _atUpper[outVar] = leaveToUpper;
            _val[outVar] = leaveToUpper ? _hiX[outVar] : _loX[outVar];
            _basis[leave] = q; _inBasis[q] = true;

            // Eta update of Binv: row leave /= piv, then row i -= alpha[i] * row leave for alpha[i] != 0.
            double piv = alpha[leave];   // at or above the eta floor: checked before the step (N112)
            _bInv.DivideRow(leave, piv);
            fRows.Clear();
            for (int i = 0; i < _m; i++) if (i != leave && alpha[i] != 0.0) fRows.Add(i);
            double saved = alpha[leave];
            alpha[leave] = 0.0;
            _bInv.Eliminate(leave, alpha, fRows, denseF: fRows.Count * 4 > _m);
            alpha[leave] = saved;
            sinceRefactor++;
        }
        return LpStatus.IterationLimit;
    }

    /// <summary>Group the generators by (PTDF column, objective coefficient bits) for pricing.</summary>
    private (int[] GroupOf, int[] GroupCol, double[] GroupObj) BuildPricingGroups(double[] obj)
    {
        var groupOf = new int[_g];
        var index = new Dictionary<(int, long), int>();
        var cols = new List<int>();
        var objs = new List<double>();
        for (int j = 0; j < _g; j++)
        {
            var key = (_genCol[j], BitConverter.DoubleToInt64Bits(obj[j]));
            if (!index.TryGetValue(key, out int k))
            {
                k = cols.Count;
                index[key] = k;
                cols.Add(_genCol[j]);
                objs.Add(obj[j]);
            }
            groupOf[j] = k;
        }
        return (groupOf, cols.ToArray(), objs.ToArray());
    }

    /// <summary>The reference's <c>Refactorize</c>: rebuild Binv from the current basis columns by Gauss-Jordan
    /// elimination with partial pivoting, on the augmented matrix [B | I] (columns 0..m-1 hold B, m..2m-1 the
    /// growing inverse), with the same pivot choice, row swaps, clamp and row operations.</summary>
    private void Refactorize()
    {
        _refactors++;
        int m = _m;
        var aug = new DcOpfHybridColumns(m, 2 * m);
        for (int k = 0; k < m; k++)
        {
            int j = _basis[k];
            if (j < _g)
            {
                var col = new double[m];
                for (int i = 0; i < m; i++) col[i] = GenEntry(i, j);
                aug.SetDense(k, col);
            }
            else
            {
                (int row, double a) = UnitColumn(j);
                aug.SetSingle(k, row, a);
            }
        }
        for (int i = 0; i < m; i++) aug.SetSingle(m + i, i, 1.0);

        var f = new double[m];
        var fRows = new List<int>(m);
        for (int col = 0; col < m; col++)
        {
            // Pivot: the first row at or below col holding the strictly largest |entry| of column col.
            int piv = col;
            double[]? dc = aug.DenseColumn(col);
            if (dc is not null)
            {
                double bestAbs = Math.Abs(dc[col]);
                for (int r = col + 1; r < m; r++) { double v = Math.Abs(dc[r]); if (v > bestAbs) { bestAbs = v; piv = r; } }
            }
            else
            {
                (int row, double v) = aug.Single(col);
                double bestAbs = row == col ? Math.Abs(v) : 0.0;
                if (row > col && Math.Abs(v) > bestAbs) piv = row;
            }
            if (piv != col) aug.SwapRows(col, piv);

            double d = aug.Entry(col, col);
            // As BoundedSimplex: a basis singular to working precision fails loud instead of being clamped and carried on.
            if (!(Math.Abs(d) >= BoundedSimplex.RefactorPivotClamp))
                throw new InvalidOperationException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"DcOpfSecuritySimplex: the basis is singular at refactorisation (pivot {d:R}); no reliable solution."));
            aug.DivideRow(col, d);

            // Multipliers f[r] = b[r, col] for r != col, read before any row is updated.
            fRows.Clear();
            Array.Clear(f);
            dc = aug.DenseColumn(col);
            if (dc is not null)
            {
                for (int r = 0; r < m; r++) if (r != col && dc[r] != 0.0) { f[r] = dc[r]; fRows.Add(r); }
            }
            else
            {
                (int row, double v) = aug.Single(col);
                if (row != col && v != 0.0) { f[row] = v; fRows.Add(row); }
            }
            aug.Eliminate(col, f, fRows, denseF: fRows.Count * 4 > m);
        }

        // The right half is the new inverse.
        var inv = new DcOpfHybridColumns(m, m);
        for (int j = 0; j < m; j++)
        {
            double[]? d = aug.DenseColumn(m + j);
            if (d is not null) inv.SetDense(j, d);
            else { (int row, double v) = aug.Single(m + j); inv.SetSingle(j, row, v); }
        }
        _bInv = inv;
    }

    /// <summary>The reference's <c>RecomputeBasics</c>: basic values = Binv . (rhs - A_N x_N), with the
    /// nonbasic sum taken over columns in ascending order exactly as there.</summary>
    private void RecomputeBasics()
    {
        int m = _m;
        var r = new double[m];
        for (int i = 0; i < m; i++)
        {
            double sum = _rhs[i];
            for (int j = 0; j < _g; j++)
            {
                if (_inBasis[j]) continue;
                double v = _val[j];
                if (v == 0.0) continue; // A x 0 is a zero: value-neutral
                sum -= GenEntry(i, j) * v;
            }
            // Slack of this row (a thermal row only), then the artificial of this row: the only other non-zeros.
            if (i > 0)
            {
                int s = _g + (i - 1);
                if (!_inBasis[s]) sum -= (_sign[i] * 1.0) * _val[s];
            }
            int art = _n + i;
            if (!_inBasis[art]) sum -= 1.0 * _val[art];
            r[i] = sum;
        }
        var x = new double[m];
        _bInv.MultiplyVector(r, x);
        for (int i = 0; i < m; i++) _val[_basis[i]] = x[i];
    }
}
