using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Estimation;

/// <summary>
/// One estimation-step backend: everything a Gauss-Newton pass needs that depends on how the
/// normal equations are represented - the estimator-side sibling of <see cref="ILinearSolver"/> /
/// <see cref="IPowerFlowSolver"/>. The estimator owns the loop (start point, damping, convergence,
/// chi-square, bad-data removal); the step backend owns representation: how H is held, how the gain is
/// formed and solved, how observability is gated, and how the residual covariance is computed.
/// Today's implementations are dense LU (<= threshold) and sparse Gilbert-Peierls (above it);
/// alternative METHODS (Hachtel-constrained, QR-orthogonal, robust reweighting) drop in behind
/// this same seam without touching the loop.
/// </summary>
internal interface IWlsStep
{
    /// <summary>Bind to a fixed (model, measurement set, weights) - called once per bad-data
    /// pass; buffers and structural work (sparsity patterns, orderings) belong here.</summary>
    void Begin(GridModel model, MeasurementModel mm, double[] w);

    /// <summary>
    /// Re-bind to new measurement weights for the SAME (model, measurement set) the last <see cref="Begin"/>
    /// bound - the robust IRLS outer loop, which only changes weights. October 2026 review (R120): the loop
    /// used to call <see cref="Begin"/> again on every outer pass, so the sparse backend rebuilt its gain
    /// pattern, slot map and RCM ordering (pure structure, independent of the weights) up to ten times, and
    /// the dense backends re-allocated their m x s and s x s buffers. Backends whose prepared state does not
    /// depend on the weights only swap the array reference; that is bit-identical to a fresh
    /// <see cref="Begin"/> because every per-iteration buffer is cleared and refilled before use. Backends
    /// whose preparation DOES read the weights (the constrained pseudo-weight) re-run their full
    /// <see cref="Begin"/>. Throws when called before <see cref="Begin"/> or with a weight array of the
    /// wrong length.
    /// </summary>
    void RebindWeights(double[] w);

    /// <summary>Observability gate at the given start point. Dense proves gain-matrix rank and
    /// names free directions; sparse runs the island sweep (rank -1) and lets the factorization
    /// detect numerical singularity later.</summary>
    ObservabilityReport GateObservability(double[] vm, double[] va);

    /// <summary>Compute the undamped Gauss-Newton correction deltax at the current state.
    /// False when the gain is singular (the step's own unobservability signal).</summary>
    bool TrySolve(double[] vm, double[] va, double[] dxOut);

    /// <summary>Residual-covariance diagonals for bad-data analysis at the (converged) state:
    /// normalized residuals + criticality flags.</summary>
    (double[] Normalized, bool[] Critical) NormalizedResiduals(double[] vm, double[] va, double[] residualPu);
}

/// <summary>Dense normal equations on <see cref="ILinearSolver"/> - the <= threshold backend.</summary>
internal sealed class DenseWlsStep : IWlsStep
{
    private readonly ILinearSolver _linear;
    private GridModel _model = null!;
    private MeasurementModel _mm = null!;
    private double[] _w = null!;
    private double[] _h = null!;
    private double[,] _jac = null!;
    private double[,] _gain = null!;

    public DenseWlsStep(ILinearSolver linear) => _linear = linear;

    public void Begin(GridModel model, MeasurementModel mm, double[] w)
    {
        _model = model;
        _mm = mm;
        _w = w;
        _h = new double[mm.MeasurementCount];
        _jac = new double[mm.MeasurementCount, mm.StateSize];
        _gain = new double[mm.StateSize, mm.StateSize];
    }

    /// <summary>Weights only: H, h and the gain are cleared and refilled from scratch on every use.</summary>
    public void RebindWeights(double[] w)
    {
        if (_mm is null)
            throw new InvalidOperationException("RebindWeights called before Begin: there is no measurement set to re-weight.");
        if (w.Length != _mm.MeasurementCount)
            throw new ArgumentException($"RebindWeights: {w.Length} weights for {_mm.MeasurementCount} measurements.", nameof(w));
        _w = w;
    }

    public ObservabilityReport GateObservability(double[] vm, double[] va)
    {
        var rhs = new double[_mm.StateSize];
        _mm.EvaluateH(vm, va, _h);
        _mm.EvaluateJacobian(vm, va, _jac);
        FormNormalEquations(_mm.ZPu, _h, _w, _jac, _gain, rhs);
        return ObservabilityAnalysis.Analyze(_model, _mm, _gain);
    }

    public bool TrySolve(double[] vm, double[] va, double[] dxOut)
    {
        _mm.EvaluateH(vm, va, _h);
        _mm.EvaluateJacobian(vm, va, _jac);
        FormNormalEquations(_mm.ZPu, _h, _w, _jac, _gain, dxOut);
        try
        {
            _linear.SolveInPlace(_gain, dxOut); // dxOut now holds deltax
            return true;
        }
        catch (InvalidOperationException)
        {
            return false; // collapsed pivot: the state wandered off the observable manifold
        }
    }

    public (double[] Normalized, bool[] Critical) NormalizedResiduals(double[] vm, double[] va, double[] residualPu)
    {
        var rhs = new double[_mm.StateSize];
        _mm.EvaluateJacobian(vm, va, _jac);
        FormNormalEquations(_mm.ZPu, _h, _w, _jac, _gain, rhs);
        return BadDataAnalysis.NormalizedResiduals(_mm, _jac, _gain, _w, residualPu);
    }

    /// <summary>G = H^TWH and g = H^TW(z-h), dense. W is diagonal so both are single passes over H.</summary>
    internal static void FormNormalEquations(
        double[] z, double[] h, double[] w, double[,] jac, double[,] gain, double[] rhs)
        => FormNormalEquations(z, h, w, jac, gain, rhs, SimdCapabilities.Tier);

    /// <summary><see cref="FormNormalEquations(double[],double[],double[],double[,],double[,],double[])"/> at an explicit
    /// vector <paramref name="tier"/> (the R102 test seam; production passes <see cref="SimdCapabilities.Tier"/>). D1:
    /// every tier gives the same bits (<c>D1CrossTierTests</c>).</summary>
    internal static void FormNormalEquations(
        double[] z, double[] h, double[] w, double[,] jac, double[,] gain, double[] rhs, SimdTier tier)
    {
        int m = z.Length;
        int s = rhs.Length;
        Array.Clear(gain);
        Array.Clear(rhs);
        if (s == 0) return;

        // Contiguous row-major storage: element (i,j) sits at origin + i*s + j, so the inner
        // c2-sweep is a vectorised rank-1 axpy into gain row c (upper triangle only).
        ref double gainOrigin = ref gain[0, 0];
        ref double jacOrigin = ref jac[0, 0];
        for (int r = 0; r < m; r++)
        {
            double wr = w[r];
            double wres = wr * (z[r] - h[r]);
            int rowBase = r * s;
            for (int c = 0; c < s; c++)
            {
                double hrc = jac[r, c];
                if (hrc == 0) continue;
                rhs[c] += hrc * wres;
                double whrc = wr * hrc;
                // gain[c, c2] += whrc * jac[r, c2] for c2 = c..s-1.
                // D1 (October 2026, Mark's determinism rule 2026-10-04; fixes R279's WLS half): separate multiply and add
                // (fastFma: false), never a fused multiply-add. With the fused arm the gain, and so every dense estimate
                // and the QR / constrained steps' observability and bad-data gains built here, took different last bits
                // on an FMA CPU and a non-FMA one (and with DOTNET_EnableHWIntrinsic=0). The feasible and constrained KKT
                // assemblies already used false.
                SimdKernels.AddScaledRow(ref gainOrigin, c * s + c, ref jacOrigin, rowBase + c, s - c, whrc, fastFma: false, tier);
            }
        }
        // Mirror the upper triangle (H^TWH is symmetric).
        for (int c = 0; c < s; c++)
            for (int c2 = c + 1; c2 < s; c2++)
                gain[c2, c] = gain[c, c2];
    }
}

/// <summary>Sparse normal equations on the Gilbert-Peierls LU - the gb-full-scale backend.
/// H is held as per-measurement sparse rows from the shared evaluation path. The gain matrix's
/// SPARSITY PATTERN is fixed for a whole pass (it is structural - the measurement topology),
/// so <see cref="Begin"/> builds the CSC structure, per-row value slots and the RCM ordering
/// exactly once; each iteration only refills values and refactors numerically. That removes the
/// per-iteration CSR rebuild + re-sort + re-ordering the first implementation paid.</summary>
internal sealed class SparseWlsStep : IWlsStep
{
    private GridModel _model = null!;
    private MeasurementModel _mm = null!;
    private double[] _w = null!;
    private double[] _h = null!;
    private SparseJacobianRows _jac = null!;

    // Prepared gain structure: CSC arrays (== the symmetric pattern's CSR arrays), the
    // fill-reducing ordering, per-measurement-row value slots, and a scatter scratch.
    private int[] _ap = null!;
    private int[] _ai = null!;
    private double[] _ax = null!;
    private int[] _q = null!;
    private int[][] _rowCols = null!;   // per measurement row: its structural state columns (sorted)
    private int[][] _rowSlots = null!;  // per measurement row: ax slot for each (c1,c2) pair, row-major
    private double[] _scatter = null!;

    public void Begin(GridModel model, MeasurementModel mm, double[] w)
    {
        _model = model;
        _mm = mm;
        _w = w;
        int m = mm.MeasurementCount;
        int s = mm.StateSize;
        _h = new double[m];
        _jac = new SparseJacobianRows(m);
        _scatter = new double[s];

        // 1. Structural columns per measurement row (distinct, sorted).
        _rowCols = new int[m][];
        var buf = new List<int>(64);
        for (int r = 0; r < m; r++)
        {
            buf.Clear();
            mm.StructuralColumns(r, buf);
            buf.Sort();
            int wri = 0;
            for (int i = 0; i < buf.Count; i++)
                if (i == 0 || buf[i] != buf[i - 1]) buf[wri++] = buf[i];
            _rowCols[r] = buf.GetRange(0, wri).ToArray();
        }

        // 2. Gain pattern G = H^TWH: union of each row's column-pair outer product. Built once
        //    through the deduplicating Builder (values are dummies; only the pattern matters).
        var pattern = new CsrMatrix.Builder(s);
        for (int r = 0; r < m; r++)
        {
            int[] cols = _rowCols[r];
            for (int i = 0; i < cols.Length; i++)
                for (int j = 0; j < cols.Length; j++)
                    pattern.Add(cols[i], cols[j], 1.0);
        }
        CsrMatrix g = pattern.Build();

        // G is symmetric, so the CSR arrays double as the CSC arrays (column k's row list ==
        // row k's column list, both sorted). Values are refilled every iteration.
        _ap = g.RowPtr;
        _ai = g.ColIdx;
        _ax = new double[g.Val.Length];
        _q = SparseLu.Ordering(g);

        // 3. Per-row slot map: ax index of each (c1,c2) pair - binary search once, not per iteration.
        _rowSlots = new int[m][];
        for (int r = 0; r < m; r++)
        {
            int[] cols = _rowCols[r];
            var slots = new int[cols.Length * cols.Length];
            int idx = 0;
            for (int i = 0; i < cols.Length; i++)
            {
                int colSeg = cols[i]; // CSC column == CSR row by symmetry
                int lo = _ap[colSeg], hi = _ap[colSeg + 1];
                for (int j = 0; j < cols.Length; j++)
                {
                    int at = Array.BinarySearch(_ai, lo, hi - lo, cols[j]);
                    slots[idx++] = at; // pattern guarantees presence
                }
            }
            _rowSlots[r] = slots;
        }
    }

    /// <summary>Weights only: the gain pattern, RCM ordering and slot map depend on the measurement
    /// STRUCTURE, not the weights; the gain values, Jacobian rows and scatter scratch are cleared and refilled
    /// on every use (FillGain), so this is bit-identical to a fresh <see cref="Begin"/>.</summary>
    public void RebindWeights(double[] w)
    {
        if (_mm is null)
            throw new InvalidOperationException("RebindWeights called before Begin: there is no measurement set to re-weight.");
        if (w.Length != _mm.MeasurementCount)
            throw new ArgumentException($"RebindWeights: {w.Length} weights for {_mm.MeasurementCount} measurements.", nameof(w));
        _w = w;
    }

    /// <summary>Above this state size the O(s^3) dense rank proof is skipped and only the topology sweep
    /// runs (Rank = -1). A control-room "area" sits well inside this; the whole estate does not.</summary>
    private const int ObservabilityDiagnosisMaxState = DenseEstimationLimits.RankProofMaxState;   // R119: one table

    public ObservabilityReport GateObservability(double[] vm, double[] va)
    {
        // Within the cap, form the gain at the start point and run the full numerical diagnosis (naming the
        // free directions + distinguishing metering-deficient buses from islands); above it, topology only.
        if (_mm.StateSize > ObservabilityDiagnosisMaxState)
            return ObservabilityAnalysis.AnalyzeTopology(_model, _mm);
        FillGain(vm, va, rhs: null);
        return ObservabilityAnalysis.AnalyzeSparse(_model, _mm, _ap, _ai, _ax, ObservabilityDiagnosisMaxState);
    }

    /// <summary>Refill gain values + rhs at the current state (fixed structure).</summary>
    private void FillGain(double[] vm, double[] va, double[]? rhs)
    {
        _jac.Clear();
        _mm.EvaluateJacobian(vm, va, _jac);
        Array.Clear(_ax);
        if (rhs is not null) Array.Clear(rhs);

        for (int r = 0; r < _mm.MeasurementCount; r++)
        {
            double wr = _w[r];
            List<(int Col, double Val)> numeric = _jac.Rows[r];
#if DEBUG
            // Structure-reuse guard: the per-iteration numeric Jacobian must never touch a state
            // column outside the structural pattern Begin derived once. If it did, the scatter/slot
            // refill would silently drop that term. Pins the "measurement structure is fixed across
            // the pass" claim at runtime (PerformanceOpts.md 4.1 has no such check by construction).
            for (int p = 0; p < numeric.Count; p++)
                System.Diagnostics.Debug.Assert(
                    Array.BinarySearch(_rowCols[r], numeric[p].Col) >= 0,
                    "SparseWlsStep: numeric Jacobian column outside the structural pattern from Begin");
#endif
            for (int p = 0; p < numeric.Count; p++)
                _scatter[numeric[p].Col] = numeric[p].Val;
            if (rhs is not null)
            {
                double wres = wr * (_mm.ZPu[r] - _h[r]);
                for (int p = 0; p < numeric.Count; p++)
                    rhs[numeric[p].Col] += numeric[p].Val * wres;
            }

            int[] cols = _rowCols[r];
            int[] slots = _rowSlots[r];
            int idx = 0;
            for (int i = 0; i < cols.Length; i++)
            {
                double wv1 = wr * _scatter[cols[i]];
                if (wv1 == 0) { idx += cols.Length; continue; }
                for (int j = 0; j < cols.Length; j++)
                    _ax[slots[idx++]] += wv1 * _scatter[cols[j]];
            }

            for (int p = 0; p < numeric.Count; p++)
                _scatter[numeric[p].Col] = 0.0;
        }
    }

    public bool TrySolve(double[] vm, double[] va, double[] dxOut)
    {
        _mm.EvaluateH(vm, va, _h);
        FillGain(vm, va, dxOut);
        SparseLuFactorization? lu = SparseLu.FactorPrepared(_mm.StateSize, _ap, _ai, _ax, _q);
        if (lu is null) return false;
        double[] dx = lu.Solve(dxOut);
        Array.Copy(dx, dxOut, _mm.StateSize);
        return true;
    }

    public (double[] Normalized, bool[] Critical) NormalizedResiduals(double[] vm, double[] va, double[] residualPu)
    {
        FillGain(vm, va, rhs: null);
        SparseLuFactorization lu = SparseLu.FactorPrepared(_mm.StateSize, _ap, _ai, _ax, _q)
            ?? throw new InvalidOperationException(
                "Near-singular gain matrix in sparse residual-covariance factorization.");
        return BadDataAnalysis.NormalizedResiduals(_mm, _jac, lu, residualPu, _w);
    }
}

/// <summary>
/// Equality-constrained WLS (--method constrained): rows tagged
/// <c>Source == "virtual-zero-injection"</c> are promoted from soft large-ish-sigma
/// pseudo-measurements to HARD equalities via the KKT (Hachtel-style augmented) system
///
///     [ G_f  C^T ] [deltax]   [ g_f ]
///     [ C    0  ] [lambda ] = [ r_c ]
///
/// where G_f/g_f are the free rows' normal equations and C the constraint Jacobian.
/// A pure switching node's zero net injection is a network FACT, not telemetry - the
/// constrained method honours it exactly instead of "within 10 MVAr", and conditions
/// better than the huge-weight trick. Dense backend (<= SolverSelector threshold);
/// the sparse constrained backend is future work and selection above the threshold
/// fails loud rather than silently degrading.
/// </summary>
internal sealed class ConstrainedDenseWlsStep : IWlsStep
{
    /// <summary>The source tag that promotes a measurement row to a hard equality.</summary>
    public const string ConstraintSource = "virtual-zero-injection";

    private readonly ILinearSolver _linear;
    private GridModel _model = null!;
    private MeasurementModel _mm = null!;
    private double[] _w = null!;
    private bool[] _isConstraint = null!;
    private int _c;
    private double[] _h = null!;
    private double[,] _jac = null!;
    private double[,] _kkt = null!;   // (s+c) x (s+c)
    private double[,] _gain = null!;  // s x s combined-information gain (gate + covariance)

    // The pseudo-weight a constraint row carries wherever a plain weighted gain stands in for the KKT system
    // (the observability gate and the residual-covariance approximation). ONE value for both sites, relative to
    // the strongest free measurement: NumericTolerances.HardConstraintWeightScale * max(w_free). Before October
    // 2026 (R142/R143) the gate used an absolute 1e6 and the covariance an absolute 1e8, so the rank test ran
    // on a different matrix from the one whose covariance was reported, and neither was guaranteed to dominate
    // (a PMU angle row at 1e-4 rad sigma already has w = 1e8).
    private double _constraintWeight;

    /// <summary>The pseudo-weight constraint rows carry in the gate and covariance gains (see the field).</summary>
    internal double ConstraintWeight => _constraintWeight;

    public ConstrainedDenseWlsStep(ILinearSolver linear) => _linear = linear;

    /// <summary>
    /// The row equilibration factor of a KKT constraint block (R257 follow-up / ruling Q12 for the Feasible method,
    /// N044 for this one): alpha = 2^floor(e/2) with e = ILogB(max diag G) over the free block's diagonal
    /// <c>kkt[0..s-1, 0..s-1]</c>, i.e. sqrt(max diag G) rounded down to a power of two (the ratio lies in [1, 2)).
    /// Multiplying a constraint row, its C^T column and its right-hand side by it brings the eliminated constraint
    /// pivots (about |C|^2 / |G|) up to the gain's own order, clear of the relative pivot floor, and changes only
    /// the multipliers (divided by alpha), never dx. A power of two makes every scaled entry exact in binary, and
    /// the exponent is integer arithmetic, identical on every CPU. A gain with no positive finite diagonal (no free
    /// information, or a non-finite entry the solve will reject anyway) gives alpha = 1: left unscaled.
    /// </summary>
    internal static double ConstraintRowScale(double[,] kkt, int s)
    {
        double maxDiag = 0.0;
        for (int c1 = 0; c1 < s; c1++)
        {
            double d = kkt[c1, c1];
            if (d > maxDiag) maxDiag = d;   // NaN never compares greater, so it cannot become the scale
        }
        return maxDiag > 0.0 && double.IsFinite(maxDiag) ? Math.ScaleB(1.0, Math.ILogB(maxDiag) >> 1) : 1.0;
    }

    public void Begin(GridModel model, MeasurementModel mm, double[] w)
    {
        _model = model;
        _mm = mm;
        _w = w;
        int m = mm.MeasurementCount;
        int s = mm.StateSize;
        _isConstraint = new bool[m];
        _c = 0;
        for (int r = 0; r < m; r++)
            if (string.Equals(mm.Measurements[r].Source, ConstraintSource, StringComparison.Ordinal))
            {
                _isConstraint[r] = true;
                _c++;
            }

        // One relative constraint pseudo-weight (R142/R143), scaled to the strongest FREE measurement.
        double maxFree = 0.0;
        bool anyFree = false;
        for (int r = 0; r < m; r++)
        {
            if (_isConstraint[r]) continue;
            anyFree = true;
            if (!(w[r] > 0.0) || !double.IsFinite(w[r]))
                throw new InvalidOperationException(
                    $"Constrained WLS: measurement {r} has weight {w[r]}; every free measurement needs a positive finite weight (1/sigma^2).");
            if (w[r] > maxFree) maxFree = w[r];
        }
        if (!anyFree)
            throw new InvalidOperationException(
                "Constrained WLS: every measurement is a virtual-zero-injection constraint, so there is no telemetry to estimate from.");
        _constraintWeight = NumericTolerances.HardConstraintWeightScale * maxFree;

        _h = new double[m];
        _jac = new double[m, s];
        _kkt = new double[s + _c, s + _c];
        _gain = new double[s, s];
    }

    /// <summary>The constraint pseudo-weight is derived from the weights, so re-weighting re-runs the full
    /// <see cref="Begin"/> (the robust methods never select this backend; this keeps the contract honest).</summary>
    public void RebindWeights(double[] w)
    {
        if (_mm is null)
            throw new InvalidOperationException("RebindWeights called before Begin: there is no measurement set to re-weight.");
        if (w.Length != _mm.MeasurementCount)
            throw new ArgumentException($"RebindWeights: {w.Length} weights for {_mm.MeasurementCount} measurements.", nameof(w));
        Begin(_model, _mm, w);
    }

    public ObservabilityReport GateObservability(double[] vm, double[] va)
    {
        // Gate on the combined information: free rows at their weights plus constraints at the shared
        // relative pseudo-weight - rank-deficiency here is rank-deficiency of [H_f; C].
        _mm.EvaluateH(vm, va, _h);
        _mm.EvaluateJacobian(vm, va, _jac);
        int s = _mm.StateSize;
        Array.Clear(_gain);
        if (s == 0) return ObservabilityAnalysis.Analyze(_model, _mm, _gain);
        // R118: the upper-triangle rank-1 updates run the bit-exact vector kernel over the contiguous row-major
        // storage (a separate multiply and add per lane, never fused: the same bits as the scalar loop).
        ref double gainOrigin = ref _gain[0, 0];
        ref double jacOrigin = ref _jac[0, 0];
        for (int r = 0; r < _mm.MeasurementCount; r++)
        {
            double wr = _isConstraint[r] ? _constraintWeight : _w[r];
            for (int c1 = 0; c1 < s; c1++)
            {
                double h1 = _jac[r, c1];
                if (h1 == 0) continue;
                double wh = wr * h1;
                // _gain[c1, c2] += wh * _jac[r, c2] for c2 = c1 .. s-1
                SimdKernels.AddScaledRow(ref gainOrigin, c1 * s + c1, ref jacOrigin, r * s + c1, s - c1, wh, fastFma: false);
            }
        }
        for (int c1 = 0; c1 < s; c1++)
            for (int c2 = c1 + 1; c2 < s; c2++)
                _gain[c2, c1] = _gain[c1, c2];
        return ObservabilityAnalysis.Analyze(_model, _mm, _gain);
    }

    public bool TrySolve(double[] vm, double[] va, double[] dxOut)
    {
        int s = _mm.StateSize;
        int m = _mm.MeasurementCount;
        _mm.EvaluateH(vm, va, _h);
        _mm.EvaluateJacobian(vm, va, _jac);

        Array.Clear(_kkt);
        var rhs = new double[s + _c];

        // Pass 1: the free rows' normal equations G_f, g_f into the top-left block (constraint rows skipped). The
        // free rows are visited in the same order as before N044, so G_f and g_f are bit-identical.
        for (int r = 0; r < m; r++)
        {
            if (_isConstraint[r]) continue;
            double wr = _w[r];
            double wres = wr * (_mm.ZPu[r] - _h[r]);
            for (int c1 = 0; c1 < s; c1++)
            {
                double h1 = _jac[r, c1];
                if (h1 == 0) continue;
                rhs[c1] += h1 * wres;
                double wh = wr * h1;
                // _kkt[c1, c2] += wh * _jac[r, c2] for c2 = c1 .. s-1 (bit-exact vector kernel, R118; the KKT
                // row stride is s + c).
                SimdKernels.AddScaledRow(ref _kkt[0, 0], c1 * (s + _c) + c1, ref _jac[0, 0], r * s + c1, s - c1, wh, fastFma: false);
            }
        }

        // Pass 2: the hard constraint rows alpha*C, alpha*C^T and their right-hand sides alpha*r_c, with alpha the
        // exact power-of-two row equilibration of the Feasible method (N044; see ConstraintRowScale). Before N044
        // the rows went in unscaled: with standard sigmas the free gain is 1e6-1e8 and a zero-injection row is of
        // order 1-100, so the eliminated constraint pivot fell under the solver's relative pivot floor and case57
        // and case118 with hard zero-injection rows were declared Unobservable at iteration 0. Scaling the rows
        // only divides the multipliers by alpha; dx is mathematically unchanged, and with no constraint row nothing
        // is touched at all.
        double alpha = ConstraintRowScale(_kkt, s);
        int ci = 0;
        for (int r = 0; r < m; r++)
        {
            if (!_isConstraint[r]) continue;
            int row = s + ci;
            for (int col = 0; col < s; col++)
            {
                double v = alpha * _jac[r, col];
                _kkt[row, col] = v;   // alpha C
                _kkt[col, row] = v;   // alpha C^T
            }
            rhs[row] = alpha * (_mm.ZPu[r] - _h[r]);
            ci++;
        }

        for (int c1 = 0; c1 < s; c1++)
            for (int c2 = c1 + 1; c2 < s; c2++)
                _kkt[c2, c1] = _kkt[c1, c2];

        try
        {
            _linear.SolveInPlace(_kkt, rhs); // indefinite KKT: partial-pivot LU handles it
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        Array.Copy(rhs, dxOut, s);
        return true;
    }

    public (double[] Normalized, bool[] Critical) NormalizedResiduals(double[] vm, double[] va, double[] residualPu)
    {
        // Identification runs over the FREE rows with the plain covariance (documented
        // approximation: constraints enter as very-high-weight rows). Constraint rows are
        // marked critical outright - a network fact is never removable telemetry.
        _mm.EvaluateJacobian(vm, va, _jac);
        int s = _mm.StateSize;
        var wEff = new double[_mm.MeasurementCount];
        for (int r = 0; r < wEff.Length; r++) wEff[r] = _isConstraint[r] ? _constraintWeight : _w[r];
        var rhs = new double[s];
        DenseWlsStep.FormNormalEquations(_mm.ZPu, _h, wEff, _jac, _gain, rhs);
        (double[] normalized, bool[] critical) = BadDataAnalysis.NormalizedResiduals(_mm, _jac, _gain, wEff, residualPu);
        for (int r = 0; r < critical.Length; r++)
            if (_isConstraint[r]) { critical[r] = true; normalized[r] = double.NaN; }
        return (normalized, critical);
    }
}

/// <summary>Orthogonal (Householder QR) least-squares step - solves the Gauss-Newton correction from the
/// weighted Jacobian directly, WITHOUT forming the ill-conditioned normal equations G = H^TWH (which square
/// the condition number). Numerically the robust path for stressed cases; dense (forms the full weighted
/// Jacobian). Observability and residual covariance still use the gain, formed only for those.</summary>
internal sealed class QrWlsStep : IWlsStep
{
    private GridModel _model = null!;
    private MeasurementModel _mm = null!;
    private double[] _w = null!;
    private double[] _h = null!;
    private double[,] _jac = null!;
    private double[,] _gain = null!;
    private double[,] _a = null!;   // sqrt(W) H, every entry rewritten each iteration (R119: was a fresh m x s per iteration)
    private double[] _b = null!;    // sqrt(W) (z - h), every entry rewritten each iteration

    public void Begin(GridModel model, MeasurementModel mm, double[] w)
    {
        _model = model;
        _mm = mm;
        _w = w;
        _h = new double[mm.MeasurementCount];
        _jac = new double[mm.MeasurementCount, mm.StateSize];
        _gain = new double[mm.StateSize, mm.StateSize];
        _a = new double[mm.MeasurementCount, mm.StateSize];
        _b = new double[mm.MeasurementCount];
    }

    /// <summary>Weights only: the weighted Jacobian and the gain are rebuilt from scratch on every use.</summary>
    public void RebindWeights(double[] w)
    {
        if (_mm is null)
            throw new InvalidOperationException("RebindWeights called before Begin: there is no measurement set to re-weight.");
        if (w.Length != _mm.MeasurementCount)
            throw new ArgumentException($"RebindWeights: {w.Length} weights for {_mm.MeasurementCount} measurements.", nameof(w));
        _w = w;
    }

    public ObservabilityReport GateObservability(double[] vm, double[] va)
    {
        var rhs = new double[_mm.StateSize];
        _mm.EvaluateH(vm, va, _h);
        _mm.EvaluateJacobian(vm, va, _jac);
        DenseWlsStep.FormNormalEquations(_mm.ZPu, _h, _w, _jac, _gain, rhs);
        return ObservabilityAnalysis.Analyze(_model, _mm, _gain);
    }

    public bool TrySolve(double[] vm, double[] va, double[] dxOut)
    {
        int m = _mm.MeasurementCount, s = _mm.StateSize;
        _mm.EvaluateH(vm, va, _h);
        _mm.EvaluateJacobian(vm, va, _jac);

        // Weighted least-squares system A dx = b with A = sqrt(W) H, b = sqrt(W)(z - h). Every entry of the
        // reused buffers is overwritten below, so reuse is bit-identical to a fresh allocation.
        double[,] a = _a;
        double[] b = _b;
        for (int r = 0; r < m; r++)
        {
            double sw = System.Math.Sqrt(_w[r]);
            for (int c = 0; c < s; c++) a[r, c] = sw * _jac[r, c];
            b[r] = sw * (_mm.ZPu[r] - _h[r]);
        }

        try
        {
            double[] dx = Numerics.HouseholderQr.SolveLeastSquares(a, b);
            System.Array.Copy(dx, dxOut, s);
            return true;
        }
        catch (System.Exception ex) when (ex is System.InvalidOperationException or System.ArgumentException)
        {
            return false; // rank-deficient / under-determined: the step's unobservability signal
        }
    }

    public (double[] Normalized, bool[] Critical) NormalizedResiduals(double[] vm, double[] va, double[] residualPu)
    {
        var rhs = new double[_mm.StateSize];
        _mm.EvaluateJacobian(vm, va, _jac);
        DenseWlsStep.FormNormalEquations(_mm.ZPu, _h, _w, _jac, _gain, rhs);
        return BadDataAnalysis.NormalizedResiduals(_mm, _jac, _gain, _w, residualPu);
    }
}

/// <summary>
/// The CANONICAL WLS step (Ruling, 2026-10-15): every plain WLS (and robust IRLS) estimate on the CPU, whatever its
/// size, solves the gain through ONE factorisation, the sparse Gilbert-Peierls LU under the frozen RCM ordering
/// (<see cref="SparseWlsStep"/>), exactly as every Newton step is solved through one canonical sparse routine. Before,
/// a case at or below 200 buses was solved by a dense partial-pivot LU and a larger one by the sparse LU, so the same
/// network estimated as 200 and as 201 buses went through different arithmetic and differed in the last bits, against
/// the bit-identity rule; now the estimate does not depend on which side of any size threshold the case falls.
///
/// <para>Only the observability REPORT keeps its size rule: at or below the old threshold the start-point gate is the
/// dense gain-rank proof (<see cref="DenseWlsStep.GateObservability"/>, the report those cases always had), above it the
/// sparse diagnosis. The gate decides only whether the estimate runs and what the report names; it does no arithmetic
/// of the estimate, so the numbers are canonical either way.</para>
/// </summary>
internal sealed class CanonicalWlsStep : IWlsStep
{
    private readonly SparseWlsStep _solve = new();
    private readonly DenseWlsStep? _denseGate;

    /// <param name="denseGateBusThreshold">At or below this bus count the observability gate is the dense rank proof.</param>
    /// <param name="busCount">The model's bus count.</param>
    public CanonicalWlsStep(int denseGateBusThreshold, int busCount)
    {
        if (busCount <= denseGateBusThreshold)
            _denseGate = new DenseWlsStep(new DenseLuSolver());
    }

    public void Begin(GridModel model, MeasurementModel mm, double[] w)
    {
        _solve.Begin(model, mm, w);
        _denseGate?.Begin(model, mm, w);
    }

    public void RebindWeights(double[] w)
    {
        _solve.RebindWeights(w);
        _denseGate?.RebindWeights(w);
    }

    public ObservabilityReport GateObservability(double[] vm, double[] va)
        => _denseGate is not null ? _denseGate.GateObservability(vm, va) : _solve.GateObservability(vm, va);

    public bool TrySolve(double[] vm, double[] va, double[] dxOut) => _solve.TrySolve(vm, va, dxOut);

    public (double[] Normalized, bool[] Critical) NormalizedResiduals(double[] vm, double[] va, double[] residualPu)
        => _solve.NormalizedResiduals(vm, va, residualPu);
}
