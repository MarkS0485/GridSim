namespace GridSim.Core.Estimation;

/// <summary>
/// The one place that states how large a system each DENSE estimation method accepts (October 2026 review,
/// WS11 R119/R107). Before this, the concept "the largest system we run a dense O(s^3) solve for" had a
/// different value in every file and two methods had no cap at all: the QR step allocated two dense m x s
/// matrices on any case (about 3.9 GB on gb-full), and the Kalman filter, reachable from
/// <c>serve --method ekf</c> on any case, holds and inverts dense s x s covariances every window (about 3 GB
/// and tens of minutes per window on gb-full). Every dense method now checks one of these named limits and
/// FAILS LOUD above it instead of exhausting memory or running for hours.
///
/// <para>The limits are not collapsed into a single number on purpose: the methods differ in what they hold
/// and how often they pay for it, and moving an existing value would silently change which bundled cases a
/// method accepts. The existing values are kept exactly (so no case that ran before is refused, and none that
/// was refused now runs) and the two new caps reuse an existing value of the same kind. Units differ for the
/// same reason they always have: the constrained-family cap was defined in buses, the others in states
/// (about two states per bus: the non-slack angles plus every magnitude).</para>
/// </summary>
public static class DenseEstimationLimits
{
    /// <summary>
    /// The dense whole-system METHODS - <see cref="EstimationMethod.Constrained"/>, <see cref="EstimationMethod.Feasible"/>
    /// (both a dense (s+c) x (s+c) KKT system per iteration) and, since R119, <see cref="EstimationMethod.Qr"/>
    /// (a dense m x s weighted Jacobian factorised per iteration) - accept at most this many buses. The value is
    /// the existing constrained-method cap (3,000 buses, about 6,000 states): it was decoupled from the
    /// dense-vs-sparse selection threshold so that 200 - 3,000-bus cases the dense KKT handles comfortably are
    /// not refused. gb-full (3,539 buses) is above it for all three.
    /// </summary>
    public const int DenseMethodMaxBuses = 3000;

    /// <summary>
    /// The sparse backend's numerical observability diagnosis (an s x s dense rank proof that names the free
    /// state directions) runs at or below this state size; above it only the topology sweep runs (Rank = -1).
    /// Unchanged value (was <c>SparseWlsStep.ObservabilityDiagnosisMaxState</c>).
    /// </summary>
    public const int RankProofMaxState = 2500;

    /// <summary>
    /// The opt-in WLS state covariance diag(G^-1) (<see cref="EstimationOptions.ComputeStateCovariance"/>), a dense
    /// form, factorisation and s back-substitutions, is computed at or below this state size; above it the
    /// per-bus bounds stay null. Unchanged value (was <c>WlsStateEstimator.CovarianceStateLimit</c>).
    /// </summary>
    public const int StateCovarianceMaxState = 2000;

    /// <summary>
    /// The recursive Kalman filter (<see cref="Dse.KalmanStateEstimator"/>) carries a dense s x s covariance and
    /// inverts two dense s x s matrices EVERY window, so it shares the rank proof's cap (the other "dense O(s^3)
    /// per call, called repeatedly" limit): construction fails loud above it (R107). Before October 2026 there
    /// was no cap at all.
    /// </summary>
    public const int RecursiveFilterMaxState = RankProofMaxState;

    /// <summary>
    /// The innovation-form recursive filters - <see cref="Dse.UnscentedKalmanStateEstimator"/> and
    /// <see cref="Dse.EnsembleKalmanStateEstimator"/> - form and invert a dense m x m innovation covariance EVERY window
    /// (m = the window's measurement count), and the unscented filter also propagates 2s + 1 sigma points through h(x).
    /// Both filters check both limits themselves: their constructors refuse a state size above
    /// <see cref="RecursiveFilterMaxState"/> (D-DOC-229) and their <c>Step</c> refuses a window with more measurements
    /// than this cap (GR-070; this text used to say neither filter checked a size). The streaming engine behind
    /// <c>serve --method ukf|enkf</c> (R062, October 2026) checks the same two limits first, failing loud above either.
    /// It reuses the existing
    /// "dense O(n^3) per call, called repeatedly" value rather than inventing a new one.
    /// </summary>
    public const int InnovationFilterMaxMeasurements = RecursiveFilterMaxState;
}
