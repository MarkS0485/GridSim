using GridSim.Core.Diagnostics;
using GridSim.Core.Model;

namespace GridSim.Core.Estimation.Dse;

/// <summary>One filtered present-state estimate from a recursive DSE step, with per-bus 1-sigma bounds.</summary>
public sealed record DseEstimate(
    IReadOnlyList<int> BusIds,
    double[] Vm,
    double[] VaDeg,
    double[] StateVmSigma,
    double[] StateVaSigmaDeg,
    bool Converged,
    DateTime? StateTimeUtc)
{
    /// <summary>What <see cref="Converged"/> means for a recursive estimator (R255): the filter state and its
    /// variances are all finite. A Kalman update is one step, not an iteration, so there is no convergence test;
    /// this alias names the actual verdict. It does NOT certify the estimate against the measurements.</summary>
    public bool Finite => Converged;
}

/// <summary>
/// A recursive (Extended Kalman Filter) network-state tracker - Dynamic State Estimation at the
/// network-state level. Unlike the snapshot <see cref="WlsStateEstimator"/>, it carries a state estimate and
/// its covariance <b>across measurement windows</b>: each step predicts the state forward through an
/// <see cref="IProcessModel"/> (persistence by default), then corrects it against the window's measurements.
/// The correction is done in <b>information form</b> - the posterior information matrix is the prior
/// information plus the WLS gain H^T R^-1 H - so it reuses exactly the same measurement Jacobian the
/// snapshot estimator does and there is no dense m x m innovation inverse.
///
/// <para>This is the boundary case <c>UnderstandingRestrictions.md</c> gates: the predict step integrates a
/// process model a step ahead, so the method is registered in <see cref="RestrictionReview"/> and asserted at
/// construction. The <b>emitted</b> estimate is the FILTERED PRESENT state, stamped at the window time - a
/// filter, not a predictor. Inputs remain guard-limited upstream (the loader / streaming engine refuse any
/// window newer than the horizon).</para>
/// </summary>
public sealed class KalmanStateEstimator
{
    /// <summary>The <c>EstimationMethod</c>-style name under which this filter is reviewed.</summary>
    public const string MethodName = "Ekf";

    private readonly GridModel _model;
    private readonly int _n;
    private readonly double _slackRefRad;
    private readonly double _initialStateVariance;

    private IProcessModel? _process;
    private int _stateSize;
    private double[] _x = Array.Empty<double>();  // packed state [Va non-slack; Vm all]
    private double[,] _p = new double[0, 0];       // state covariance
    private bool _initialized;

    /// <param name="model">The (fixed) network the filter tracks.</param>
    /// <param name="process">The predict-step process model; null uses <see cref="RandomWalkProcessModel"/> (persistence).</param>
    /// <param name="initialStateVariance">Diffuse-prior variance seeded on the diagonal of the initial covariance.</param>
    public KalmanStateEstimator(GridModel model, IProcessModel? process = null, double initialStateVariance = 1.0)
    {
        // The Dynamics->Estimation review gate (UnderstandingRestrictions.md invariant #5): a recursive filter
        // with a predict step must be reviewed before it can run.
        RestrictionReview.AssertPermitted(MethodName, usesDynamicsModel: true);

        _model = model;
        _n = model.BusCount;
        _process = process;
        _initialStateVariance = initialStateVariance;

        double refRad = 0;
        foreach (Bus b in model.Buses)
            if (b.Type == BusType.Slack) { refRad = b.VaDeg * Math.PI / 180.0; break; }
        _slackRefRad = refRad;

        // R107: every window holds and inverts dense s x s matrices (s = non-slack angles + every magnitude),
        // so the filter fails loud at construction above the shared dense-filter limit rather than exhausting
        // memory or running for tens of minutes per window (gb-full: s = 7,077, about 3 GB per window).
        int stateSize = model.Buses.Count(bus => bus.Type != BusType.Slack) + _n;
        if (stateSize > DenseEstimationLimits.RecursiveFilterMaxState)
            throw new NotSupportedException(
                $"The Kalman filter is dense (s x s covariance inverted every window) and is limited to " +
                $"{DenseEstimationLimits.RecursiveFilterMaxState} states; this case has {stateSize} ({_n} buses). " +
                "Use the snapshot WLS estimator, which switches to the sparse backend at this size.");
    }

    /// <summary>Current filtered state as a <see cref="DseEstimate"/> (available after at least one step).</summary>
    public bool IsInitialized => _initialized;

    /// <summary>
    /// Run one predict-update step against a measurement window and return the filtered present state.
    /// <paramref name="dtSeconds"/> is the elapsed time since the previous window (drives the process noise).
    /// </summary>
    public DseEstimate Step(MeasurementSet window, double dtSeconds = 1.0)
    {
        window.Validate(_model);
        var mm = new MeasurementModel(_model, window);
        int s = mm.StateSize;

        // D-DOC-117 (October 2026): the step is TRANSACTIONAL. Nothing the filter carries between windows (the state x, the
        // covariance P, the initialised flag, the state size, the default process model) is written until every operation
        // that can throw has succeeded. Before, the process model's Predict ran IN PLACE on the live state _x before the
        // two inverses, so a custom (non-persistence) process model was left applied to the state when an inverse then
        // threw (a singular prior covariance), and the next window predicted from an already-predicted state; the first
        // window also marked the filter initialised before it could fail. Now the prior is read from the committed state
        // (or a fresh initial state on the first window), Predict runs on a COPY, and the posterior is committed at the
        // end. Each number is formed by exactly the same operations on the same values as before, so a step that succeeds
        // is unchanged to the bit (KalmanDenseKernelBitIdentityTests pins this against a reference filter).
        double[] xPrior;
        double[,] pPrior;
        if (!_initialized) (xPrior, pPrior) = InitialState(mm);
        else if (s != _stateSize)
            throw new InvalidOperationException(
                $"state layout changed between windows ({_stateSize} -> {s}); the filter tracks one fixed network.");
        else { xPrior = _x; pPrior = _p; }

        IProcessModel process = _process ?? new RandomWalkProcessModel(s - _n);

        // R107 (October 2026 review, second part): every s x s matrix of the step lives in a buffer owned by the
        // filter and reused window after window, instead of about eleven fresh s x s arrays per window (f, q, the
        // congruence copy, the gain, and an n x 2n working matrix plus an n x n result for each of the two inverses;
        // 474,830,376 B over three windows on case588_sdet). Every buffer that a fresh array used to supply is cleared
        // to +0 first, exactly the contents of a new array, and every buffer that was a function's result is fully
        // overwritten by the same arithmetic, so each number is unchanged to the bit. The posterior is written into a
        // spare and swapped with _p only once the step has succeeded, so a step that throws (a singular inverse)
        // leaves the filter state exactly as before, as the old code did.
        EnsureBuffers(s);
        double[,] f = _fBuf!, q = _qBuf!;

        // --- Predict: x^- = f(x), P^- = F P F^T + Q ---
        Array.Clear(f);
        Array.Clear(q);
        // D-DOC-117: Predict works on a copy of the prior (it used to run on _x itself and the copy was taken after); the
        // copy holds the same values, so x^- is the same to the bit, and the live state is untouched if anything throws.
        double[] xMinus = (double[])xPrior.Clone();
        process.Predict(xMinus, dtSeconds, f, q);         // persistence leaves x^- equal to the prior, f = I
        double[,] pMinus = DseLinAlg.AddInPlace(DseLinAlg.CongruenceInto(f, pPrior, _pMinusBuf!), q);

        // --- Update (information form): Y = inv(P^-) + H^T R^-1 H, x = x^- + inv(Y) H^T R^-1 (z - h(x^-)) ---
        Unpack(mm, xMinus, out double[] vm, out double[] va);
        int m = mm.MeasurementCount;
        var h = new double[m];
        mm.EvaluateH(vm, va, h);
        // The Jacobian is m x s; m can differ between windows, so its buffer is kept while the shape repeats.
        if (_jacBuf is null || _jacBuf.GetLength(0) != m || _jacBuf.GetLength(1) != s) _jacBuf = new double[m, s];
        else Array.Clear(_jacBuf);
        double[,] jac = _jacBuf;
        mm.EvaluateJacobian(vm, va, new DenseJacobianSink(jac));

        double[,] g = _gBuf!;
        Array.Clear(g);
        var rhs = new double[s];
        AccumulateInformation(mm, h, jac, g, rhs);

        double[,] y = DseLinAlg.AddInPlace(DseLinAlg.InvertInto(pMinus, s, _invWork!, _yBuf!), g); // Y = inv(P^-) + G
        double[,] p = DseLinAlg.InvertInto(y, s, _invWork!, _pSpare!);                            // posterior covariance
        double[] dx = DseLinAlg.MatVec(p, rhs, s);
        var xPost = new double[s];
        for (int i = 0; i < s; i++) xPost[i] = xMinus[i] + dx[i];
        // GR-523: the Gauss-Jordan inverse is not exactly symmetric, and the asymmetry used to compound window after
        // window. The stored covariance is made exactly symmetric (after this window's state update, which is unchanged).
        DseLinAlg.SymmetriseInPlace(p, s);

        // D-DOC-117: commit. Nothing below can throw, so the filter moves from the old window's state to this one's as a
        // whole or not at all.
        _x = xPost;
        (_p, _pSpare) = (p, pPrior);   // the old covariance's buffer becomes next window's spare
        _stateSize = s;
        _process = process;
        _initialized = true;

        return BuildEstimate(mm, window.TakenUtc);
    }

    // R107 per-window buffers (allocated on the first step, sized to the fixed state; see Step).
    private double[,]? _fBuf, _qBuf, _pMinusBuf, _gBuf, _yBuf, _pSpare, _invWork, _jacBuf;

    private void EnsureBuffers(int s)
    {
        if (_fBuf is not null && _fBuf.GetLength(0) == s) return;
        _fBuf = new double[s, s];
        _qBuf = new double[s, s];
        _pMinusBuf = new double[s, s];
        _gBuf = new double[s, s];
        _yBuf = new double[s, s];
        _pSpare = new double[s, s];
        _invWork = new double[s, 2 * s];
    }

    /// <summary>
    /// G = H^T R^-1 H and rhs = H^T R^-1 (z - h), BIT-IDENTICAL to the original full loop
    /// <code>for r: for i (hwi = jac[r,i]*wr, skip 0): rhs[i] += hwi*yr; for j in 0..s-1: g[i,j] += hwi*jac[r,j]</code>
    /// but with the inner j loop over the row's NONZERO columns only (R107: a measurement row touches a few
    /// dozen of the s columns, so the old loop did s - nnz useless multiply-adds per (r, i)). A skipped term is
    /// g[i,j] += hwi*(+-0): it leaves a nonzero g[i,j] unchanged, and a zero g[i,j] is always +0 (it starts
    /// at +0 and a sum that cancels exactly is +0), which +-0 leaves at +0, so every bit of g is unchanged.
    /// That argument needs hwi finite (inf*0 is NaN), so a row with any non-finite Jacobian entry or weight
    /// takes the original full loop. Per entry the accumulation order (rows ascending) is the original's.
    /// </summary>
    internal static void AccumulateInformation(MeasurementModel mm, double[] h, double[,] jac, double[,] g, double[] rhs)
    {
        int m = mm.MeasurementCount;
        int s = rhs.Length;
        var nz = new int[s];
        for (int r = 0; r < m; r++)
        {
            double wr = 1.0 / (mm.SigmaPu[r] * mm.SigmaPu[r]);
            double yr = mm.ZPu[r] - h[r];

            int count = 0;
            bool finite = double.IsFinite(wr);
            for (int j = 0; j < s; j++)
            {
                double v = jac[r, j];
                if (!double.IsFinite(v)) finite = false;
                if (v != 0.0) nz[count++] = j;
            }

            if (!finite)
            {
                // The original loop, verbatim, so non-finite inputs propagate exactly as before.
                for (int i = 0; i < s; i++)
                {
                    double hwi = jac[r, i] * wr;
                    if (hwi == 0.0) continue;
                    rhs[i] += hwi * yr;
                    for (int j = 0; j < s; j++) g[i, j] += hwi * jac[r, j];
                }
                continue;
            }

            for (int a = 0; a < count; a++)
            {
                int i = nz[a];
                double hwi = jac[r, i] * wr;
                if (hwi == 0.0) continue;   // underflow to zero: the original skips it too
                rhs[i] += hwi * yr;
                for (int b = 0; b < count; b++)
                {
                    int j = nz[b];
                    g[i, j] += hwi * jac[r, j];
                }
            }
        }
    }

    /// <summary>The first window's prior: flat start (angles at the slack reference, Vm at 1 pu) and a diagonal
    /// covariance. D-DOC-117: returned rather than stored, so a first window that throws leaves the filter
    /// uninitialised (the next window starts from this same prior again).</summary>
    private (double[] X, double[,] P) InitialState(MeasurementModel mm)
    {
        int s = mm.StateSize;
        var x = new double[s];
        var p = new double[s, s];
        for (int c = 0; c < s; c++)
        {
            mm.StateBusIndex(c, out bool angle);
            x[c] = angle ? _slackRefRad : 1.0; // flat start (angles at reference, Vm at 1 pu)
            p[c, c] = _initialStateVariance;
        }
        return (x, p);
    }

    private void Unpack(MeasurementModel mm, double[] x, out double[] vm, out double[] va)
    {
        vm = new double[_n];
        va = new double[_n];
        for (int i = 0; i < _n; i++) va[i] = _slackRefRad; // slack (and default) reference angle
        for (int c = 0; c < x.Length; c++)
        {
            int bus = mm.StateBusIndex(c, out bool angle);
            if (angle) va[bus] = x[c]; else vm[bus] = x[c];
        }
    }

    private DseEstimate BuildEstimate(MeasurementModel mm, DateTime? stateTime)
    {
        Unpack(mm, _x, out double[] vm, out double[] va);
        var vaDeg = new double[_n];
        for (int i = 0; i < _n; i++) vaDeg[i] = va[i] * 180.0 / Math.PI;

        var vmSigma = new double[_n];
        var vaSigmaRad = new double[_n];
        bool finite = true;
        for (int c = 0; c < _stateSize; c++)
        {
            int bus = mm.StateBusIndex(c, out bool angle);
            double var = _p[c, c];
            finite &= double.IsFinite(var) && double.IsFinite(_x[c]);
            double sigma = Math.Sqrt(var > 0 ? var : 0.0);
            if (angle) vaSigmaRad[bus] = sigma; else vmSigma[bus] = sigma;
        }
        var vaSigmaDeg = new double[_n];
        for (int i = 0; i < _n; i++) vaSigmaDeg[i] = vaSigmaRad[i] * 180.0 / Math.PI;

        return new DseEstimate(mm.BusIds, vm, vaDeg, vmSigma, vaSigmaDeg, finite, stateTime);
    }
}

/// <summary>Small dense linear algebra for the recursive filters (invert, matrix-vector, F P F^T, add).</summary>
internal static class DseLinAlg
{
    /// <summary>
    /// Invert an n x n matrix by Gauss-Jordan elimination with partial pivoting. Throws on singularity.
    ///
    /// <para>October 2026 review (R107): BIT-IDENTICAL to the original full-width loop, faster. (1) The row
    /// operations (swap, scale, eliminate) run over columns col .. 2n-1 only. Each column evolves only from
    /// its own entries plus the step's pivot row, divisor and factors, and those are read from the pivot
    /// column; so every column at or right of the pivot - including the whole right half, the returned
    /// inverse - receives exactly the original operations in the original order, and a column left of the
    /// pivot is never read again (the original kept updating it, to no effect on anything returned). The
    /// inverse is therefore unchanged to the bit, zero signs included. (2) The
    /// elimination m[r,j] -= fac*m[col,j] runs the bit-exact vector kernel (separate multiply and subtract per
    /// lane, never fused) over the contiguous row, so every element rounds as the scalar loop did at every
    /// SIMD width.</para>
    /// </summary>
    public static double[,] Invert(double[,] a, int n) => InvertInto(a, n, new double[n, 2 * n], new double[n, n]);

    /// <summary>
    /// <see cref="Invert"/> into caller-owned storage (R107: the Kalman filter reuses both across windows).
    /// <paramref name="work"/> (n x 2n) is cleared to +0 first, the contents of the fresh array <see cref="Invert"/>
    /// used to allocate, and <paramref name="inv"/> (n x n) is fully overwritten, so the inverse is bit-identical.
    /// Returns <paramref name="inv"/>. Neither buffer may be <paramref name="a"/>.
    /// </summary>
    public static double[,] InvertInto(double[,] a, int n, double[,] work, double[,] inv)
    {
        if (work.GetLength(0) != n || work.GetLength(1) != 2 * n)
            throw new ArgumentException($"work must be {n} x {2 * n}.", nameof(work));
        if (inv.GetLength(0) != n || inv.GetLength(1) != n)
            throw new ArgumentException($"inv must be {n} x {n}.", nameof(inv));
        if (ReferenceEquals(work, a) || ReferenceEquals(inv, a))
            throw new ArgumentException("The inverse cannot be formed in the matrix being inverted.", nameof(inv));
        var m = work;
        Array.Clear(m);
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) m[i, j] = a[i, j];
            m[i, n + i] = 1.0;
        }

        double norm = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) { double av = Math.Abs(a[i, j]); if (av > norm) norm = av; }
        // The shared relative singular-pivot floor (R141; was a local 1e-14). An all-zero matrix keeps a unit
        // scale so its exact-zero pivot is still rejected.
        double floor = Numerics.NumericTolerances.PivotFloor(norm > 0 ? norm : 1.0);

        for (int col = 0; col < n; col++)
        {
            int piv = col;
            double max = Math.Abs(m[col, col]);
            for (int r = col + 1; r < n; r++)
            {
                double v = Math.Abs(m[r, col]);
                if (v > max) { max = v; piv = r; }
            }
            if (max <= floor)
                throw new InvalidOperationException($"singular matrix in DSE covariance inverse (pivot {max:E3} at column {col}).");
            if (piv != col)
                for (int j = col; j < 2 * n; j++) (m[col, j], m[piv, j]) = (m[piv, j], m[col, j]);

            double d = m[col, col];
            for (int j = col; j < 2 * n; j++) m[col, j] /= d;
            ref double origin = ref m[0, 0];
            int width = 2 * n;
            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                double fac = m[r, col];
                if (fac == 0.0) continue;
                // m[r, j] -= fac * m[col, j] for j = col .. 2n-1 (bit-exact vector kernel).
                Numerics.SimdKernels.SubtractScaledRow(ref origin, r * width + col, ref origin, col * width + col, width - col, fac);
            }
        }

        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) inv[i, j] = m[i, n + j];
        return inv;
    }

    /// <summary>y = A x for an n x n matrix A.</summary>
    public static double[] MatVec(double[,] a, double[] x, int n)
    {
        var y = new double[n];
        for (int i = 0; i < n; i++)
        {
            double s = 0;
            for (int j = 0; j < n; j++) s += a[i, j] * x[j];
            y[i] = s;
        }
        return y;
    }

    /// <summary>GR-523: replace each off-diagonal pair of the n x n matrix by its mean, 0.5 * (a[i,j] + a[j,i]), in row
    /// order (i &lt; j), so a covariance carried between windows is exactly symmetric. The diagonal is untouched. Plain
    /// operations in a fixed order: the same bits on every machine.</summary>
    public static void SymmetriseInPlace(double[,] a, int n)
    {
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                double mean = 0.5 * (a[i, j] + a[j, i]);
                a[i, j] = mean;
                a[j, i] = mean;
            }
    }

    /// <summary>A + B into a fresh matrix (A is the first argument, returned distinct).</summary>
    public static double[,] AddInPlace(double[,] a, double[,] b)
    {
        int n = a.GetLength(0);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) a[i, j] += b[i, j];
        return a;
    }

    /// <summary>Lower-triangular Cholesky factor L with A = L L^T (A symmetric positive definite). Throws when
    /// a pivot is not strictly positive: the covariance has lost positive definiteness, and the filter must
    /// fail loud rather than silently continue. Before October 2026 (R141) a non-positive pivot was replaced
    /// by an absolute 1e-12, which fabricated a covariance. A small POSITIVE pivot is a legitimate conditional
    /// variance (covariances routinely span many decades), so no relative floor is applied here.</summary>
    public static double[,] Cholesky(double[,] a, int n)
    {
        var l = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j <= i; j++)
            {
                double sum = a[i, j];
                for (int k = 0; k < j; k++) sum -= l[i, k] * l[j, k];
                if (i == j)
                {
                    // !(sum > 0) also catches NaN.
                    if (!(sum > 0.0))
                        throw new InvalidOperationException(
                            $"DSE covariance is not positive definite: Cholesky pivot {sum:E3} at row {i} is not positive.");
                    l[i, j] = Math.Sqrt(sum);
                }
                else
                    l[i, j] = sum / l[j, j];
            }
        return l;
    }

    /// <summary>
    /// D-DOC-113 (October 2026): the lower Cholesky factor L of a symmetric positive SEMI-definite matrix (A = L L^T),
    /// for sampling a process-noise covariance that may have directions with no variance. Deterministic: column by
    /// column (j = 0 .. n - 1), each pivot d_j = a_jj - sum_{k &lt; j} l_jk^2 and each entry
    /// l_ij = (a_ij - sum_{k &lt; j} l_ik l_jk) / l_jj, every sum accumulated from k = 0 upwards. With the scale
    /// t = <see cref="Numerics.NumericTolerances.PivotFloor"/>(max |a_ij|) (the shared relative floor, 1e-12 of the
    /// largest entry):
    /// <list type="bullet">
    /// <item>d_j &gt; t: l_jj = sqrt(d_j), and the column below is filled as above;</item>
    /// <item>|d_j| &lt;= t: a direction with no variance (left after the earlier columns): the whole column is zero, and
    /// every entry below must also be within t of zero (otherwise A is not semi-definite and the call throws);</item>
    /// <item>d_j &lt; -t: A is not positive semi-definite: throws.</item>
    /// </list>
    /// Throws <see cref="InvalidOperationException"/> for a non-finite A or one that is not positive semi-definite.
    /// Symmetry is the caller's check (only the lower triangle is read). <see cref="Cholesky"/> (strictly positive
    /// definite, used by the unscented filter) is unchanged.
    /// </summary>
    public static double[,] CholeskySemiDefinite(double[,] a, int n)
    {
        double scale = Numerics.NumericTolerances.MaxAbs(a);
        if (!double.IsFinite(scale))
            throw new InvalidOperationException("the matrix is not finite, so it has no Cholesky factor.");
        double t = Numerics.NumericTolerances.PivotFloor(scale);
        var l = new double[n, n];
        for (int j = 0; j < n; j++)
        {
            double d = a[j, j];
            for (int k = 0; k < j; k++) d -= l[j, k] * l[j, k];
            if (d > t)
            {
                double ljj = Math.Sqrt(d);
                l[j, j] = ljj;
                for (int i = j + 1; i < n; i++)
                {
                    double sum = a[i, j];
                    for (int k = 0; k < j; k++) sum -= l[i, k] * l[j, k];
                    l[i, j] = sum / ljj;
                }
            }
            else if (d >= -t)
            {
                // No variance left in this direction: the column stays zero, which is only consistent when nothing below
                // it is correlated with it.
                for (int i = j + 1; i < n; i++)
                {
                    double sum = a[i, j];
                    for (int k = 0; k < j; k++) sum -= l[i, k] * l[j, k];
                    if (Math.Abs(sum) > t)
                        throw new InvalidOperationException(
                            $"the matrix is not positive semi-definite: row {j} has no variance left (pivot {d:E3}) but is still " +
                            $"correlated with row {i} ({sum:E3}).");
                }
            }
            else
                throw new InvalidOperationException(
                    $"the matrix is not positive semi-definite: Cholesky pivot {d:E3} at row {j} is negative (beyond the floor {t:E3}).");
        }
        return l;
    }

    /// <summary>
    /// The congruence transform F P F^T (n x n). For F = I this is a copy of P.
    ///
    /// <para>October 2026 review (R107): the default persistence process model passes F = I exactly, for which
    /// the general loop spends a full O(n^3) triple loop to reproduce P. For that case the result is now formed
    /// directly as p[i,j] + 0.0, which is BIT-IDENTICAL to the general loop: there fp[i,j] = +0 + 1.0*p[i,j]
    /// (= p[i,j] + 0.0), and result[i,j] = +0 plus fp[i,j]*1 plus products fp[i,k]*(+-0) for k != j, which are
    /// +-0 and never change a sum (a nonzero sum is unchanged, and +0 + (-0) = +0). Adding 0.0 maps -0 to +0
    /// exactly as the loop's leading +0 accumulator does. The products are +-0 only for FINITE fp, so the fast
    /// path is taken only when F is exactly the identity (diagonal 1.0, off-diagonal zero of either sign) and
    /// every entry of P is finite; anything else runs the general loop.</para>
    /// </summary>
    public static double[,] Congruence(double[,] f, double[,] p)
    {
        int n = f.GetLength(0);
        return CongruenceInto(f, p, new double[n, n]);
    }

    /// <summary>
    /// <see cref="Congruence"/> into a caller-owned n x n <paramref name="result"/> (R107: the Kalman filter reuses it
    /// across windows). Every entry of <paramref name="result"/> is written by exactly the arithmetic
    /// <see cref="Congruence"/> uses (both paths assign each entry once, never accumulate into it), so its previous
    /// contents do not matter and the values are bit-identical. Returns <paramref name="result"/>, which must not be
    /// <paramref name="f"/> or <paramref name="p"/>.
    /// </summary>
    public static double[,] CongruenceInto(double[,] f, double[,] p, double[,] result)
    {
        int n = f.GetLength(0);
        if (result.GetLength(0) != n || result.GetLength(1) != n)
            throw new ArgumentException($"result must be {n} x {n}.", nameof(result));
        if (ReferenceEquals(result, f) || ReferenceEquals(result, p))
            throw new ArgumentException("The congruence cannot be formed in one of its inputs.", nameof(result));
        if (IsExactIdentity(f, n) && AllFinite(p, n))
        {
            var copy = result;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) copy[i, j] = p[i, j] + 0.0;
            return copy;
        }

        var fp = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int k = 0; k < n; k++)
            {
                double fik = f[i, k];
                if (fik == 0.0) continue;
                for (int j = 0; j < n; j++) fp[i, j] += fik * p[k, j];
            }
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double s = 0;
                for (int k = 0; k < n; k++) s += fp[i, k] * f[j, k]; // (FP) F^T
                result[i, j] = s;
            }
        return result;
    }

    /// <summary>True when f is exactly the n x n identity: 1.0 on the diagonal, zero (either sign) elsewhere.</summary>
    private static bool IsExactIdentity(double[,] f, int n)
    {
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (f[i, j] != (i == j ? 1.0 : 0.0)) return false;
        return true;
    }

    private static bool AllFinite(double[,] p, int n)
    {
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (!double.IsFinite(p[i, j])) return false;
        return true;
    }
}
