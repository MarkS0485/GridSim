using GridSim.Core.Diagnostics;
using GridSim.Core.Model;

namespace GridSim.Core.Estimation.Dse;

/// <summary>
/// A recursive <b>Unscented</b> Kalman Filter network-state tracker - the derivative-free sibling of
/// <see cref="KalmanStateEstimator"/>. Instead of linearising the measurement function it propagates a
/// deterministic set of sigma points through the exact nonlinear h(x), so it captures the measurement
/// nonlinearity to second order without a Jacobian. Same recursive-tracking contract: it carries the state
/// and covariance across windows, predicts through an <see cref="IProcessModel"/>, and corrects against the
/// window's measurements.
///
/// <para>Restriction posture is identical to the EKF and gated the same way: the predict step integrates a
/// process model a step ahead, the method is registered in <see cref="RestrictionReview"/> and asserted at
/// construction, and the emitted estimate is the <b>filtered present</b>, stamped at the window time.</para>
/// </summary>
public sealed class UnscentedKalmanStateEstimator
{
    /// <summary>The reviewed method name.</summary>
    public const string MethodName = "Ukf";

    private readonly GridModel _model;
    private readonly int _n;
    private readonly double _slackRefRad;
    private readonly double _initialStateVariance;
    private readonly double _alpha, _beta, _kappa;

    private IProcessModel? _process;
    private int _stateSize;
    private double[] _x = Array.Empty<double>();
    private double[,] _p = new double[0, 0];
    private bool _initialized;

    public UnscentedKalmanStateEstimator(
        GridModel model, IProcessModel? process = null, double initialStateVariance = 1.0,
        double alpha = 1e-3, double beta = 2.0, double kappa = 0.0)
    {
        RestrictionReview.AssertPermitted(MethodName, usesDynamicsModel: true);
        _model = model;
        _n = model.BusCount;
        _process = process;
        _initialStateVariance = initialStateVariance;
        _alpha = alpha;
        _beta = beta;
        _kappa = kappa;

        double refRad = 0;
        foreach (Bus b in model.Buses)
            if (b.Type == BusType.Slack) { refRad = b.VaDeg * Math.PI / 180.0; break; }
        _slackRefRad = refRad;

        // D-DOC-229 (October 2026): the filter's own state-size check, as the EKF has had since R107. The unscented filter
        // holds a dense s x s covariance, factorises it and propagates 2s + 1 sigma points every window; only the
        // streaming engine's factory used to check the size, so a Core caller constructing the filter directly had no
        // limit at all. Same limit and same exception as the EKF and the engine (the engine still checks first, so its
        // message is unchanged for serve).
        int stateSize = model.Buses.Count(bus => bus.Type != BusType.Slack) + _n;
        if (stateSize > DenseEstimationLimits.RecursiveFilterMaxState)
            throw new NotSupportedException(
                $"The unscented Kalman filter is dense (s x s covariance factorised and 2s + 1 sigma points every window) and is limited to " +
                $"{DenseEstimationLimits.RecursiveFilterMaxState} states; this case has {stateSize} ({_n} buses). " +
                "Use the snapshot WLS estimator, which switches to the sparse backend at this size.");

        // N264 (October 2026): the unscented-transform parameters are checked, by name. The sigma points are spread by
        // sqrt(c) with c = s + lambda = alpha^2 (s + kappa), and the weights divide by c, so:
        //  - alpha must be a finite number above 0 (alpha = 0 gives c = 0: every weight a division by zero; a NaN alpha
        //    gives NaN weights);
        //  - beta must be finite (it enters the central covariance weight; any finite value is a valid prior choice);
        //  - kappa must be finite and s + kappa above 0 (s + kappa <= 0 gives c <= 0, the square root of a negative or
        //    zero spread: NaN sigma points, or all sigma points on the mean).
        // Only Core callers could pass such values (serve uses the defaults 1e-3, 2, 0); every value that worked before
        // is accepted unchanged and nothing is computed differently, so every accepted filter is bit for bit as before.
        RequireUnscentedParameters(alpha, beta, kappa, stateSize);
    }

    /// <summary>N264: refuse unscented-transform parameters that give no usable sigma-point spread (see the constructor),
    /// naming the parameter and its value. <paramref name="stateSize"/> is the filter's state size s.</summary>
    private static void RequireUnscentedParameters(double alpha, double beta, double kappa, int stateSize)
    {
        string F(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        if (!double.IsFinite(alpha) || !(alpha > 0.0))
            throw new ArgumentOutOfRangeException(nameof(alpha), alpha,
                $"The unscented Kalman filter's alpha is {F(alpha)}; it must be a finite number above 0 (it sets the sigma-point spread alpha^2 (s + kappa)).");
        if (!double.IsFinite(beta))
            throw new ArgumentOutOfRangeException(nameof(beta), beta,
                $"The unscented Kalman filter's beta is {F(beta)}; it must be a finite number (2 is optimal for a Gaussian prior).");
        if (!double.IsFinite(kappa))
            throw new ArgumentOutOfRangeException(nameof(kappa), kappa,
                $"The unscented Kalman filter's kappa is {F(kappa)}; it must be a finite number.");
        double c = alpha * alpha * (stateSize + kappa);
        if (!(stateSize + kappa > 0.0) || !(c > 0.0) || !double.IsFinite(c))
            throw new ArgumentOutOfRangeException(nameof(kappa), kappa,
                $"The unscented Kalman filter's kappa is {F(kappa)} with {stateSize} states: the sigma-point spread alpha^2 (s + kappa) = {F(c)} " +
                "must be a finite number above 0 (s + kappa above 0, and alpha not so small or large that the spread underflows or overflows).");
    }

    public bool IsInitialized => _initialized;

    public DseEstimate Step(MeasurementSet window, double dtSeconds = 1.0)
    {
        window.Validate(_model);
        // GR-070: the filter's own per-window cap (the streaming engine checks the same first, so serve's message is
        // unchanged): the innovation covariance is m x m and inverted densely every window, so a Core caller passing a
        // 20,000-row window directly used to form and invert a multi-gigabyte matrix.
        if (window.Measurements.Count > DenseEstimationLimits.InnovationFilterMaxMeasurements)
            throw new NotSupportedException(
                $"The unscented Kalman filter inverts a dense m x m innovation covariance every window and is limited to " +
                $"{DenseEstimationLimits.InnovationFilterMaxMeasurements} measurements per window; this window has {window.Measurements.Count}. " +
                "Use the extended Kalman filter (information form, no m x m inverse) or the snapshot WLS estimator.");
        var mm = new MeasurementModel(_model, window);
        int s = mm.StateSize;

        // D-DOC-117 (October 2026, the EKF finding; the unscented filter had the same defect): the step is TRANSACTIONAL.
        // Predict used to run IN PLACE on the live state _x before the Cholesky factor and the innovation inverse, either
        // of which can throw, so a custom process model was left applied to the state after a failed step, and the first
        // window marked the filter initialised before it could fail. Now the prior is read from the committed state (or a
        // fresh initial state), Predict runs on a copy, and the state, covariance, state size, default process model and
        // initialised flag are committed together at the end. Same operations on the same values, so a step that succeeds
        // is unchanged to the bit.
        double[] xPrior;
        double[,] pPrior;
        if (!_initialized) (xPrior, pPrior) = InitialState(mm);
        else if (s != _stateSize)
            throw new InvalidOperationException($"state layout changed between windows ({_stateSize} -> {s}).");
        else { xPrior = _x; pPrior = _p; }

        IProcessModel process = _process ?? new RandomWalkProcessModel(s - _n);

        // --- Predict (persistence transition; general F via the process model): P^- = F P F^T + Q ---
        var f = new double[s, s];
        var q = new double[s, s];
        double[] xMinus = (double[])xPrior.Clone();
        process.Predict(xMinus, dtSeconds, f, q);
        double[,] pMinus = DseLinAlg.AddInPlace(DseLinAlg.Congruence(f, pPrior), q);

        // --- Unscented update ---
        int sp = 2 * s + 1;
        double lambda = _alpha * _alpha * (s + _kappa) - s;
        double c = s + lambda;
        // N264: the spread as actually computed. The constructor checks alpha^2 (s + kappa) > 0, but c = s + lambda is
        // formed by cancellation (lambda is about -s when alpha is small), so a tiny alpha can still round c to 0 here
        // (alpha = 1e-10 with s = 100 gives lambda = -100 exactly). Refuse before anything is committed (the step is
        // transactional, D-DOC-117), rather than divide by zero. For every c that was above 0 this is a no-op.
        if (!(c > 0.0) || !double.IsFinite(c))
            throw new InvalidOperationException(
                $"The unscented Kalman filter's sigma-point spread s + lambda is {c.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"with {s} states (alpha {_alpha.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}, kappa " +
                $"{_kappa.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}); it must be a finite number above 0. Use a larger alpha.");
        var wm = new double[sp];
        var wc = new double[sp];
        wm[0] = lambda / c;
        wc[0] = lambda / c + (1 - _alpha * _alpha + _beta);
        for (int i = 1; i < sp; i++) wm[i] = wc[i] = 1.0 / (2.0 * c);

        double[,] l = DseLinAlg.Cholesky(pMinus, s);
        double sqrtC = Math.Sqrt(c);
        var sigma = new double[sp][];
        sigma[0] = (double[])xMinus.Clone();
        for (int i = 0; i < s; i++)
        {
            var plus = (double[])xMinus.Clone();
            var minus = (double[])xMinus.Clone();
            for (int r = 0; r < s; r++)
            {
                double col = sqrtC * l[r, i];
                plus[r] += col;
                minus[r] -= col;
            }
            sigma[1 + i] = plus;
            sigma[1 + s + i] = minus;
        }

        int m = mm.MeasurementCount;
        // Propagate sigma points through the measurement function h.
        var z = new double[sp][];
        for (int p = 0; p < sp; p++)
        {
            Unpack(mm, sigma[p], out double[] vm, out double[] va);
            var hp = new double[m];
            mm.EvaluateH(vm, va, hp);
            z[p] = hp;
        }

        // Predicted measurement mean.
        var zMean = new double[m];
        for (int p = 0; p < sp; p++)
            for (int r = 0; r < m; r++) zMean[r] += wm[p] * z[p][r];

        // Innovation covariance S (+ R) and cross-covariance Pxz.
        var sInnov = new double[m, m];
        var pxz = new double[s, m];
        for (int p = 0; p < sp; p++)
        {
            var dz = new double[m];
            for (int r = 0; r < m; r++) dz[r] = z[p][r] - zMean[r];
            var dxs = new double[s];
            for (int i = 0; i < s; i++) dxs[i] = sigma[p][i] - xMinus[i];
            double w = wc[p];
            for (int r = 0; r < m; r++)
            {
                double wdz = w * dz[r];
                for (int cc = 0; cc < m; cc++) sInnov[r, cc] += wdz * dz[cc];
                for (int i = 0; i < s; i++) pxz[i, r] += w * dxs[i] * dz[r];
            }
        }
        for (int r = 0; r < m; r++) sInnov[r, r] += mm.SigmaPu[r] * mm.SigmaPu[r]; // + R (diagonal)

        // Kalman gain K = Pxz S^-1, state update, covariance update P = P^- - K S K^T.
        double[,] sInv = DseLinAlg.Invert(sInnov, m);
        var k = new double[s, m];
        for (int i = 0; i < s; i++)
            for (int r = 0; r < m; r++)
            {
                double sum = 0;
                for (int t = 0; t < m; t++) sum += pxz[i, t] * sInv[t, r];
                k[i, r] = sum;
            }

        var innovation = new double[m];
        for (int r = 0; r < m; r++) innovation[r] = mm.ZPu[r] - zMean[r];
        double[] xPost = xMinus;   // x^- is this step's own copy (D-DOC-117), so it is updated in place as before
        for (int i = 0; i < s; i++)
        {
            double sum = 0;
            for (int r = 0; r < m; r++) sum += k[i, r] * innovation[r];
            xPost[i] += sum;
        }

        // P = P^- - K S K^T. Compute KS (s x m) then (KS) K^T (s x s).
        var ks = new double[s, m];
        for (int i = 0; i < s; i++)
            for (int r = 0; r < m; r++)
            {
                double sum = 0;
                for (int t = 0; t < m; t++) sum += k[i, t] * sInnov[t, r];
                ks[i, r] = sum;
            }
        var p2 = new double[s, s];
        for (int i = 0; i < s; i++)
            for (int j = 0; j < s; j++)
            {
                double sum = pMinus[i, j];
                for (int r = 0; r < m; r++) sum -= ks[i, r] * k[j, r];
                p2[i, j] = sum;
            }
        // GR-523: P^- - K S K^T is not exactly symmetric in floating point, and the next window's Cholesky reads only
        // the lower triangle, so the stored covariance is made exactly symmetric.
        DseLinAlg.SymmetriseInPlace(p2, s);
        // GR-537: a posterior variance that is not a positive finite number means the covariance lost positive
        // definiteness in this update (the cancellation hazard of large negative central weights). It used to be
        // stored and caught only by the next window's Cholesky; it is refused here, before the commit, so the filter
        // keeps its previous state.
        for (int i = 0; i < s; i++)
            if (!(p2[i, i] > 0) || !double.IsFinite(p2[i, i]))
                throw new InvalidOperationException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"UKF: the posterior variance of state {i} is {p2[i, i]:R}; the covariance update lost positive definiteness (try a larger alpha)."));

        // D-DOC-117: commit (nothing below can throw).
        _x = xPost;
        _p = p2;
        _stateSize = s;
        _process = process;
        _initialized = true;

        return BuildEstimate(mm, window.TakenUtc);
    }

    /// <summary>The first window's prior (flat start, diagonal covariance). D-DOC-117: returned rather than stored,
    /// so a first window that throws leaves the filter uninitialised.</summary>
    private (double[] X, double[,] P) InitialState(MeasurementModel mm)
    {
        int s = mm.StateSize;
        var x = new double[s];
        var p = new double[s, s];
        for (int col = 0; col < s; col++)
        {
            mm.StateBusIndex(col, out bool angle);
            x[col] = angle ? _slackRefRad : 1.0;
            p[col, col] = _initialStateVariance;
        }
        return (x, p);
    }

    private void Unpack(MeasurementModel mm, double[] x, out double[] vm, out double[] va)
    {
        vm = new double[_n];
        va = new double[_n];
        for (int i = 0; i < _n; i++) va[i] = _slackRefRad;
        for (int col = 0; col < x.Length; col++)
        {
            int bus = mm.StateBusIndex(col, out bool angle);
            if (angle) va[bus] = x[col]; else vm[bus] = x[col];
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
        for (int col = 0; col < _stateSize; col++)
        {
            int bus = mm.StateBusIndex(col, out bool angle);
            double var = _p[col, col];
            finite &= double.IsFinite(var) && double.IsFinite(_x[col]);
            double sigma = Math.Sqrt(var > 0 ? var : 0.0);
            if (angle) vaSigmaRad[bus] = sigma; else vmSigma[bus] = sigma;
        }
        var vaSigmaDeg = new double[_n];
        for (int i = 0; i < _n; i++) vaSigmaDeg[i] = vaSigmaRad[i] * 180.0 / Math.PI;

        return new DseEstimate(mm.BusIds, vm, vaDeg, vmSigma, vaSigmaDeg, finite, stateTime);
    }
}
