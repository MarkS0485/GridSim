using GridSim.Core.Diagnostics;
using GridSim.Core.Model;

namespace GridSim.Core.Estimation.Dse;

/// <summary>
/// A recursive <b>Ensemble</b> Kalman Filter network-state tracker - a Monte-Carlo sibling of the EKF/UKF. It
/// carries an ensemble of state members, propagates each through the <see cref="IProcessModel"/> predict step
/// (with sampled process noise), and corrects them with the perturbed-observation update using the ensemble's
/// own sample covariances. The estimate is the ensemble mean. Deterministic for a fixed seed (all random
/// draws are ordered), so it is testable like the estate's other seeded machinery.
///
/// <para>N264 (October 2026, stream LF4; Ruling R1 and Mark's determinism rule): every window draws its random numbers
/// from its OWN stream, <c>new Random(WindowSeed(seed, k))</c>, where k counts the windows the filter has COMMITTED so far
/// (the first window is k = 0 and uses the seed itself, so its numbers are unchanged). Before N264 one
/// <see cref="Random"/> ran across all windows, and draws taken by a step that then failed could not be given back, so
/// every later window saw different noise from a run in which the failed window never arrived. Now a failed step does
/// not advance k: the next window uses exactly the stream it would have used had the failed window never been seen,
/// and the filter (ensemble and stream) is exactly as before the failure. The cost is that every EnKF number from the
/// second window on moved once.</para>
///
/// <para>Same restriction posture as the EKF/UKF and gated the same way: the predict step integrates a process
/// model, the method is registered in <see cref="RestrictionReview"/> and asserted at construction, and the
/// emitted mean is the filtered present, stamped at the window time; inputs stay guard-limited upstream.</para>
/// </summary>
public sealed class EnsembleKalmanStateEstimator
{
    /// <summary>The reviewed method name.</summary>
    public const string MethodName = "EnKf";

    private readonly GridModel _model;
    private readonly int _n;
    private readonly double _slackRefRad;
    private readonly int _ensembleSize;
    private readonly double _initialStateStd;
    private readonly int _seed;

    /// <summary>N264: the number of windows committed so far; window k's draws come from <see cref="WindowSeed"/>(seed, k).
    /// Advanced only at the commit point of <see cref="Step"/>, so a failed step leaves it where it was.</summary>
    private long _committedWindows;

    private IProcessModel? _process;
    private int _stateSize;
    private double[][] _members = Array.Empty<double[]>();
    private bool _initialized;

    public EnsembleKalmanStateEstimator(
        GridModel model, IProcessModel? process = null, int ensembleSize = 80,
        double initialStateStd = 0.5, int seed = 20260713)
    {
        RestrictionReview.AssertPermitted(MethodName, usesDynamicsModel: true);
        // D-DOC-229 (October 2026): the constructor checks what it is given. The sample covariances divide by
        // ensembleSize - 1, so fewer than two members gives a division by zero (1 member: every variance 0 / 0 = NaN) and a
        // filter that reports nothing usable; a negative or non-finite initial spread is not a spread.
        if (ensembleSize < 2)
            throw new ArgumentOutOfRangeException(nameof(ensembleSize), ensembleSize,
                "The ensemble Kalman filter needs at least 2 members (its sample covariances divide by ensembleSize - 1).");
        if (!(initialStateStd >= 0.0) || !double.IsFinite(initialStateStd))
            throw new ArgumentOutOfRangeException(nameof(initialStateStd), initialStateStd,
                "The initial ensemble spread must be a finite number >= 0 (a standard deviation).");
        _model = model;
        _n = model.BusCount;
        _process = process;
        _ensembleSize = ensembleSize;
        _initialStateStd = initialStateStd;
        _seed = seed;

        double refRad = 0;
        foreach (Bus b in model.Buses)
            if (b.Type == BusType.Slack) { refRad = b.VaDeg * Math.PI / 180.0; break; }
        _slackRefRad = refRad;

        // D-DOC-229 (October 2026): the filter's own state-size check, as the EKF has had since R107. Each member is an
        // s-vector, the gain is s x m and (with a correlated Q, D-DOC-113) Q is factorised s x s; only the streaming
        // engine's factory used to check the size, so a Core caller constructing the filter directly had no limit. Same
        // limit and same exception as the EKF and the engine (the engine still checks first, so the serve message is
        // unchanged).
        int stateSize = model.Buses.Count(bus => bus.Type != BusType.Slack) + _n;
        if (stateSize > DenseEstimationLimits.RecursiveFilterMaxState)
            throw new NotSupportedException(
                $"The ensemble Kalman filter is dense (an s x m gain and, for a correlated process noise, an s x s factor every window) and is limited to " +
                $"{DenseEstimationLimits.RecursiveFilterMaxState} states; this case has {stateSize} ({_n} buses). " +
                "Use the snapshot WLS estimator, which switches to the sparse backend at this size.");
    }

    /// <summary>Run one predict-update step and return the filtered present state (the ensemble mean).</summary>
    public DseEstimate Step(MeasurementSet window, double dtSeconds = 1.0)
    {
        window.Validate(_model);
        // GR-070: the filter's own per-window cap (the streaming engine checks the same first, so serve's message is
        // unchanged): the innovation covariance is m x m and inverted densely every window, so a Core caller passing a
        // 20,000-row window directly used to form and invert a multi-gigabyte matrix.
        if (window.Measurements.Count > DenseEstimationLimits.InnovationFilterMaxMeasurements)
            throw new NotSupportedException(
                $"The ensemble Kalman filter inverts a dense m x m innovation covariance every window and is limited to " +
                $"{DenseEstimationLimits.InnovationFilterMaxMeasurements} measurements per window; this window has {window.Measurements.Count}. " +
                "Use the extended Kalman filter (information form, no m x m inverse) or the snapshot WLS estimator.");
        var mm = new MeasurementModel(_model, window);
        int s = mm.StateSize;

        // D-DOC-117 (October 2026, the EKF finding; the ensemble filter had the same defect): the step is TRANSACTIONAL
        // for the filter's state. The members used to be predicted, perturbed and updated IN PLACE, so a step that threw
        // half way (a singular innovation covariance, an invalid Q) left the ensemble part-predicted, and the first window
        // marked the filter initialised before it could fail. Now the step works on copies of the members and commits the
        // members, state size, default process model and initialised flag together at the end. Every number is formed by
        // the same operations on the same values, so a step that succeeds is unchanged to the bit.
        // N264 (LF4): the random stream is no longer shared across windows, so it needs no rolling back either. This
        // window's stream is derived from (seed, committed window count); a failed step commits nothing, so the next
        // window derives the same stream again and sees exactly the noise it would have seen had the failed window never
        // arrived. Within the window the draw order is unchanged: the initial members (first window only), then the
        // process noise member by member in state order, then the observation perturbations member by member.
        var rng = new Random(WindowSeed(_seed, _committedWindows));
        double Gaussian() => GaussianFrom(rng);
        double[][] prior;
        if (!_initialized) prior = InitialMembers(mm, rng);
        else if (s != _stateSize) throw new InvalidOperationException($"state layout changed ({_stateSize}->{s}).");
        else prior = _members;
        IProcessModel process = _process ?? new RandomWalkProcessModel(s - _n);

        int m = mm.MeasurementCount;
        int ne = _ensembleSize;
        var members = new double[ne][];
        for (int e = 0; e < ne; e++) members[e] = (double[])prior[e].Clone();

        // Predict: propagate EACH member through the process model's transition (Predict modifies the
        // state in place to the one-step-ahead prediction), THEN add sampled process noise. Calling
        // Predict once on a throwaway zero vector discarded the transition entirely and silently reduced
        // any non-persistence process model to persistence-plus-noise.
        // D-DOC-113 (October 2026): the noise is now drawn from the FULL Q. It used to be drawn from the diagonal of Q only
        // (each state independently with variance q_ii), so a process model with correlated noise (any off-diagonal
        // q_ij) was silently sampled as uncorrelated. See ProcessNoiseSampler: a diagonal Q (the default random walk,
        // and any Q whose off-diagonal entries are all zero) takes the old statements verbatim, so every existing result
        // is unchanged to the bit; a correlated Q is sampled as L g with L the deterministic lower Cholesky factor of Q
        // (semi-definite allowed) and g a vector of s standard normal draws taken in state order.
        var f = new double[s, s];
        var q = new double[s, s];
        var noise = new ProcessNoiseSampler(process.Name);
        for (int e = 0; e < ne; e++)
        {
            process.Predict(members[e], dtSeconds, f, q);
            noise.AddSample(q, members[e], Gaussian);
        }

        // Observation ensemble h(x_e).
        var hEns = new double[ne][];
        for (int e = 0; e < ne; e++)
        {
            Unpack(mm, members[e], out double[] vm, out double[] va);
            var h = new double[m];
            mm.EvaluateH(vm, va, h);
            hEns[e] = h;
        }

        // Means and anomalies.
        var xMean = Mean(members, s);
        var hMean = Mean(hEns, m);

        // Pyy = (1/(ne-1)) sum (h-hbar)(h-hbar)^T + R ; Pxy = (1/(ne-1)) sum (x-xbar)(h-hbar)^T.
        var pyy = new double[m, m];
        var pxy = new double[s, m];
        double inv = 1.0 / (ne - 1);
        for (int e = 0; e < ne; e++)
        {
            var dh = new double[m];
            for (int r = 0; r < m; r++) dh[r] = hEns[e][r] - hMean[r];
            var dx = new double[s];
            for (int i = 0; i < s; i++) dx[i] = members[e][i] - xMean[i];
            for (int r = 0; r < m; r++)
            {
                for (int c = 0; c < m; c++) pyy[r, c] += inv * dh[r] * dh[c];
                for (int i = 0; i < s; i++) pxy[i, r] += inv * dx[i] * dh[r];
            }
        }
        for (int r = 0; r < m; r++) pyy[r, r] += mm.SigmaPu[r] * mm.SigmaPu[r]; // + R

        double[,] pyyInv = DseLinAlg.Invert(pyy, m);
        // Kalman gain K = Pxy Pyy^-1 (s x m).
        var k = new double[s, m];
        for (int i = 0; i < s; i++)
            for (int r = 0; r < m; r++)
            {
                double sum = 0;
                for (int t = 0; t < m; t++) sum += pxy[i, t] * pyyInv[t, r];
                k[i, r] = sum;
            }

        // Update each member against a perturbed observation.
        for (int e = 0; e < ne; e++)
        {
            var innov = new double[m];
            for (int r = 0; r < m; r++)
            {
                double zPert = mm.ZPu[r] + mm.SigmaPu[r] * Gaussian();
                innov[r] = zPert - hEns[e][r];
            }
            for (int i = 0; i < s; i++)
            {
                double sum = 0;
                for (int r = 0; r < m; r++) sum += k[i, r] * innov[r];
                members[e][i] += sum;
            }
        }

        // D-DOC-117: commit (nothing below can throw).
        _members = members;
        _stateSize = s;
        _process = process;
        _initialized = true;
        _committedWindows++;   // N264: the next window takes the next derived stream

        return BuildEstimate(mm, window.TakenUtc);
    }

    /// <summary>The first window's ensemble: the flat start plus an independent N(0, initialStateStd^2) draw per member
    /// and state, in member-then-state order (unchanged). D-DOC-117: returned rather than stored, so a first window that
    /// throws leaves the filter uninitialised.</summary>
    private double[][] InitialMembers(MeasurementModel mm, Random rng)
    {
        int s = mm.StateSize;
        var baseState = new double[s];
        for (int c = 0; c < s; c++)
        {
            mm.StateBusIndex(c, out bool angle);
            baseState[c] = angle ? _slackRefRad : 1.0;
        }
        var members = new double[_ensembleSize][];
        for (int e = 0; e < _ensembleSize; e++)
        {
            members[e] = new double[s];
            for (int c = 0; c < s; c++) members[e][c] = baseState[c] + _initialStateStd * GaussianFrom(rng);
        }
        return members;
    }

    /// <summary>One standard normal draw (Box-Muller, cosine branch) from <paramref name="rng"/>: two uniforms in the order
    /// u1 then u2, and the deterministic DetMath log and cosine. Unchanged since before N264 apart from taking the window's
    /// stream as an argument.</summary>
    private static double GaussianFrom(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * DetMath.Log(u1)) * DetMath.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>
    /// N264: the seed of window <paramref name="windowIndex"/>'s random stream (k = the number of windows committed before
    /// it). Window 0 uses <paramref name="seed"/> itself, so the first window, initial ensemble included, is bit for bit
    /// what it always was. Window k &gt;= 1 uses the k-th output of the SplitMix64 generator started at the seed (Steele, Lea
    /// and Flood, "Fast splittable pseudorandom number generators", OOPSLA 2014): z = (uint)seed + k * 0x9E3779B97F4A7C15
    /// (mod 2^64), then the SplitMix64 finaliser z = (z ^ (z &gt;&gt; 30)) * 0xBF58476D1CE4E5B9;
    /// z = (z ^ (z &gt;&gt; 27)) * 0x94D049BB133111EB; z ^= z &gt;&gt; 31, and the top 31 bits (z &gt;&gt; 33) as a non-negative
    /// int. Pure 64-bit integer arithmetic (wrapping), so it is identical on every CPU, OS and runtime. The finaliser
    /// decorrelates neighbouring windows, which a plain seed + k would not do as well.
    /// </summary>
    internal static int WindowSeed(int seed, long windowIndex)
    {
        if (windowIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(windowIndex), windowIndex,
                "The window index counts committed windows and cannot be negative.");
        if (windowIndex == 0) return seed;
        unchecked
        {
            ulong z = (ulong)(uint)seed + (ulong)windowIndex * 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (int)(z >> 33);
        }
    }

    private static double[] Mean(double[][] rows, int len)
    {
        var mean = new double[len];
        foreach (double[] row in rows) for (int i = 0; i < len; i++) mean[i] += row[i];
        for (int i = 0; i < len; i++) mean[i] /= rows.Length;
        return mean;
    }

    private void Unpack(MeasurementModel mm, double[] x, out double[] vm, out double[] va)
    {
        vm = new double[_n];
        va = new double[_n];
        for (int i = 0; i < _n; i++) va[i] = _slackRefRad;
        for (int c = 0; c < x.Length; c++)
        {
            int bus = mm.StateBusIndex(c, out bool angle);
            if (angle) va[bus] = x[c]; else vm[bus] = x[c];
        }
    }

    private DseEstimate BuildEstimate(MeasurementModel mm, DateTime? stateTime)
    {
        double[] mean = Mean(_members, _stateSize);
        Unpack(mm, mean, out double[] vm, out double[] va);
        var vaDeg = new double[_n];
        for (int i = 0; i < _n; i++) vaDeg[i] = va[i] * 180.0 / Math.PI;

        // Per-state variance from the ensemble spread -> per-bus sigma.
        var vmSigma = new double[_n];
        var vaSigmaRad = new double[_n];
        bool finite = true;   // R255: was hard-coded true; now the same finiteness verdict the EKF reports
        for (int c = 0; c < _stateSize; c++)
        {
            double var = 0;
            foreach (double[] mbr in _members) { double d = mbr[c] - mean[c]; var += d * d; }
            var /= _ensembleSize - 1;
            finite &= double.IsFinite(var) && double.IsFinite(mean[c]);
            int bus = mm.StateBusIndex(c, out bool angle);
            double sigma = Math.Sqrt(var > 0 ? var : 0);
            if (angle) vaSigmaRad[bus] = sigma; else vmSigma[bus] = sigma;
        }
        var vaSigmaDeg = new double[_n];
        for (int i = 0; i < _n; i++) vaSigmaDeg[i] = vaSigmaRad[i] * 180.0 / Math.PI;

        return new DseEstimate(mm.BusIds, vm, vaDeg, vmSigma, vaSigmaDeg, finite, stateTime);
    }
}

/// <summary>
/// D-DOC-113 (October 2026): draws one process-noise sample w ~ N(0, Q) and adds it to a predicted ensemble member, for
/// <see cref="EnsembleKalmanStateEstimator"/>. Before, the filter drew each state's noise independently with variance
/// q_ii, so any correlation in Q (off-diagonal q_ij) was silently dropped.
///
/// <para>The rule, which is fully deterministic for a fixed random stream:</para>
/// <list type="bullet">
/// <item><b>Diagonal Q</b> (every off-diagonal entry exactly zero, either sign: the default random-walk model and any other
/// uncorrelated model): x_i += sqrt(max(q_ii, 0)) * g_i for i = 0, 1, ..., s - 1, one standard normal draw g_i per state in
/// state order. This is the pre-October-2026 statement, verbatim, so every result with a valid diagonal Q is unchanged to
/// the bit. N264 (LF4): a diagonal entry that is not a variance (negative, NaN or infinite) used to be read as 0 (negative)
/// or carried into the ensemble (NaN, infinity); it is now refused before any draw, naming the process model and the
/// entry, as the correlated path refuses a negative pivot (the streaming engine counts the window failed).</item>
/// <item><b>Correlated Q</b>: Q must be exactly symmetric and finite. Its lower Cholesky factor L (Q = L L^T) is formed by
/// <see cref="DseLinAlg.CholeskySemiDefinite"/> (column by column, a zero pivot allowed for a direction with no variance,
/// a negative one refused); then s standard normal draws g_0 .. g_{s-1} are taken in state order and
/// x_i += sum_{k = 0..i} L[i, k] g_k, the sum accumulated from k = 0 upwards starting at +0. The draw count per member (s)
/// and order are the same as on the diagonal path. An invalid Q (not symmetric, not finite, not positive semi-definite)
/// throws <see cref="InvalidOperationException"/> naming the process model, so the streaming engine counts the window as
/// failed instead of sampling a covariance that does not exist.</item>
/// </list>
/// The factor of the last Q seen is kept and reused while each member's Q is the same to the bit (the usual case: Q does
/// not depend on the member), so a correlated Q costs one O(s^3 / 6) factorisation per window, not one per member.
/// </summary>
internal sealed class ProcessNoiseSampler
{
    private readonly string _processName;
    private double[,]? _factoredQ;   // a copy of the Q whose factor is cached
    private double[,]? _l;           // its lower Cholesky factor

    public ProcessNoiseSampler(string processName) => _processName = processName;

    /// <summary>Add one draw of N(0, q) to <paramref name="x"/> (length s; q is s x s), taking draws from
    /// <paramref name="gaussian"/> in state order.</summary>
    public void AddSample(double[,] q, double[] x, Func<double> gaussian)
    {
        int s = x.Length;
        if (IsDiagonal(q, s))
        {
            // N264 (LF4): fail loud on a diagonal entry that is not a variance, before any draw. Before, Math.Max(q_ii, 0)
            // silently read a negative variance as 0, and a NaN or an infinity went straight into the ensemble.
            for (int i = 0; i < s; i++)
                if (!(q[i, i] >= 0.0) || double.IsPositiveInfinity(q[i, i]))
                    throw new InvalidOperationException(
                        $"the process-noise covariance Q of process model '{_processName}' has Q[{i},{i}] = " +
                        $"{q[i, i].ToString("R", System.Globalization.CultureInfo.InvariantCulture)}, which is not a variance " +
                        "(a variance must be a finite number >= 0).");
            // The pre-D-DOC-113 statement, verbatim (bit-identical for every valid diagonal Q; Math.Max keeps a -0.0 entry
            // exactly as before).
            for (int i = 0; i < s; i++) x[i] += Math.Sqrt(Math.Max(q[i, i], 0)) * gaussian();
            return;
        }

        if (_l is null || _factoredQ is null || !SameBits(q, _factoredQ, s))
        {
            for (int i = 0; i < s; i++)
                for (int j = 0; j < i; j++)
                    if (!(q[i, j] == q[j, i]))
                        throw new InvalidOperationException(
                            $"the process-noise covariance Q of process model '{_processName}' is not symmetric: " +
                            $"Q[{i},{j}] = {q[i, j].ToString("R", System.Globalization.CultureInfo.InvariantCulture)} but " +
                            $"Q[{j},{i}] = {q[j, i].ToString("R", System.Globalization.CultureInfo.InvariantCulture)}.");
            try { _l = DseLinAlg.CholeskySemiDefinite(q, s); }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"the process-noise covariance Q of process model '{_processName}': {ex.Message}", ex);
            }
            _factoredQ = (double[,])q.Clone();
        }

        var g = new double[s];
        for (int i = 0; i < s; i++) g[i] = gaussian();
        for (int i = 0; i < s; i++)
        {
            double sum = 0.0;
            for (int k = 0; k <= i; k++) sum += _l[i, k] * g[k];
            x[i] += sum;
        }
    }

    /// <summary>True when every off-diagonal entry is exactly zero (either sign). A NaN off-diagonal is not zero, so it
    /// takes the checked (correlated) path and is refused there.</summary>
    private static bool IsDiagonal(double[,] q, int s)
    {
        for (int i = 0; i < s; i++)
            for (int j = 0; j < s; j++)
                if (i != j && q[i, j] != 0.0) return false;
        return true;
    }

    /// <summary>Bitwise equality of two s x s matrices (so a cached factor is reused only for exactly the same Q).</summary>
    private static bool SameBits(double[,] a, double[,] b, int s)
    {
        for (int i = 0; i < s; i++)
            for (int j = 0; j < s; j++)
                if (BitConverter.DoubleToInt64Bits(a[i, j]) != BitConverter.DoubleToInt64Bits(b[i, j])) return false;
        return true;
    }
}
