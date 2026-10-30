using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Estimation;

/// <summary>Which estimation method the solve loop runs. The project convention: alternatives
/// live behind a seam and cross-validate each other (see <see cref="IWlsStep"/>). Further
/// members (Hachtel-constrained, Huber/LAV robust, QR-orthogonal) land here as they are built.</summary>
public enum EstimationMethod
{
    /// <summary>Classical weighted least squares (Gauss-Newton on the normal equations).</summary>
    Wls,

    /// <summary>Huber M-estimator via iteratively-reweighted least squares: quadratic loss for
    /// standardized residuals within k = 1.345, linear beyond. Outliers are DOWN-WEIGHTED
    /// smoothly instead of removed - robust to several simultaneous gross errors where the
    /// sequential LNR loop is slow or fragile; matches WLS closely on clean Gaussian data.</summary>
    Huber,

    /// <summary>
    /// Least-absolute-values, solved EXACTLY (N356, CF6; ruled by main under Ruling R1). Since N356 this member means the
    /// exact linear-programming form that CF5 built as <see cref="LavLp"/>: the estimate minimises the LAV objective
    /// sum_i |z_i - h_i(x)| / sigma_i (successive linear programming, see <see cref="LavLp"/> for the algorithm). Before
    /// N356 it was the IRLS approximation, which is now <see cref="LavIrls"/>. <see cref="LavLp"/> is an alias of this
    /// member: the two run the identical code path and give bit-identical results.
    ///
    /// <para>What the result means: LAV fits the trustworthy majority (some rows are interpolated exactly, the gross
    /// errors carry their whole error). Its objective is NOT the weighted sum of squares, and it is NOT chi-square
    /// distributed, so a chi-square verdict on a LAV estimate is not a test of anything. The estimator still fills
    /// <see cref="EstimationResult.ObjectiveJ"/> and the chi-square fields (against the declared sigmas, for
    /// comparability with the other methods), and reports the LAV objective itself in
    /// <see cref="EstimationResult.LavObjective"/>; <see cref="EstimationResult.ChiSquareIsVerdict"/> is false, and the
    /// CLI prints the LAV objective with no PASS / FAIL.</para>
    /// </summary>
    Lav,

    /// <summary>Equality-constrained WLS: rows tagged <c>virtual-zero-injection</c> become HARD
    /// equalities via the KKT augmented system (a switching node's zero injection is a network
    /// fact, honoured exactly). Dense backend only for now - above the sparse threshold the
    /// estimator fails loud rather than silently degrading.</summary>
    Constrained,

    /// <summary>Physics-constrained WLS: the equality-constrained solve PLUS a box guard that keeps
    /// the state physically admissible during iteration (Vm &gt; 0, within band) and an acceptance
    /// gate that rejects a converged-but-physics-impossible state as <see cref="EstimationStatus.Infeasible"/>.
    /// This prevents the estimator producing a physically-impossible result. Dense backend only.</summary>
    Feasible,

    /// <summary>Schweppe-Huber Generalized-M estimator: Huber IRLS on the <b>leverage-adjusted</b>
    /// standardized (Schweppe) residual r_Si = r_i / (sigma_i * sqrt(1 - K_ii)) - i.e. the NORMALIZED
    /// residual - instead of the raw one. A gross error on a high-leverage, low-redundancy measurement
    /// (a radial GSP's sole injection meter) drags the fit onto itself, so its raw residual stays small and
    /// plain Huber/LNR never down-weight it (masking). Dividing by sqrt(1 - K_ii) inflates precisely at high
    /// leverage and defeats the mask. Critical measurements (sole observers, K_ii ~ 1) are left at weight 1 -
    /// they cannot be down-weighted without losing observability. Reduces bit-for-bit to WLS on clean data
    /// and to <see cref="Huber"/> when leverage is uniform. A measurement-only static filter (no review gate).</summary>
    SchweppeGm,

    /// <summary>Orthogonal (Householder QR) weighted least squares: solves the Gauss-Newton correction from the
    /// weighted Jacobian directly, without forming the normal equations G = H^TWH. Forming G squares the
    /// condition number; the QR path does not, so it is the numerically-robust choice for ill-conditioned
    /// cases (tight PMU/virtual weights alongside loose pseudo-measurements). Same solution as
    /// <see cref="Wls"/> on well-conditioned data; dense backend.</summary>
    Qr,

    /// <summary>
    /// ALIAS of <see cref="Lav"/> since N356 (CF6): kept as its own member (so the CF5 callers and the CLI name
    /// <c>lav-lp</c> keep working) but normalised to <see cref="Lav"/> on entry to the estimator, so it runs the identical
    /// code path. The algorithm, as CF5 built it:
    /// N331 (CF5): least-absolute-values solved EXACTLY, by successive linear programming (Abur and Celik's
    /// formulation). At each Gauss-Newton iterate the correction minimises sum_i |r_i - H_i dx| / sigma_i exactly
    /// (<see cref="LavLinearProgram"/>, an L1 vertex-descent simplex warm-started from the previous iterate's basis), the
    /// step is trust-capped like every method's and then accepted by a proximal (Levenberg-Marquardt style) ratio test on
    /// the TRUE LAV objective sum |z - h(x)| / sigma: a step whose realised-to-predicted decrease ratio is below
    /// <see cref="WlsStateEstimator.LavLpAcceptRatio"/> is rejected and the LP solved again with a proximal weight that grows by
    /// <see cref="WlsStateEstimator.LavLpProximalGrowth"/> (at most <see cref="WlsStateEstimator.LavLpMaxRetries"/> times), and the weight shrinks again after a
    /// step whose ratio exceeds <see cref="WlsStateEstimator.LavLpExpandRatio"/>. There is no step halving (GR-073: this text used to say
    /// "halved"). The test keeps the iterate from cycling between two vertices of the linearisation (the trial's case30
    /// seed 1 failure). It
    /// converges when the applied step is below the tolerance, or when the linearised optimum predicts no decrease
    /// (the LAV optimum). An LP that is not optimal, or a direction whose predicted decrease no retry can realise, ends
    /// the estimate as <see cref="EstimationStatus.MaxIterations"/>, never Converged. No IRLS loop and no bad-data
    /// removal; chi-square is reported against the declared sigmas as for the robust methods (LAV minimises sum |r| /
    /// sigma, not J, so a chi-square failure on clean data is not itself a defect). Dense basis inverse (s x s):
    /// refused above <see cref="DenseEstimationLimits.StateCovarianceMaxState"/> states.
    /// </summary>
    LavLp,

    /// <summary>Least-absolute-values via IRLS (weight proportional to 1/|standardized residual|), the form
    /// <see cref="Lav"/> had before N356 (CF6), kept unchanged bit for bit under this name (CLI
    /// <c>--method lav-irls</c>). The fit follows the trustworthy majority, but only APPROXIMATELY.
    /// APPROXIMATE (N331, CF5): the LAV optimum interpolates some rows exactly, so their IRLS weights hit the
    /// 1 / <see cref="WlsStateEstimator.LavResidualFloor"/> ceiling and the active set flips between passes; the weights
    /// do not settle (case14 seeds 1-3: weight factors moving by 100 to 900 every pass to the cap of
    /// <see cref="WlsStateEstimator.IrlsMaxOuterIterations"/>), and the state reported is the last pass's, not the LAV
    /// optimum (flagged by <see cref="EstimationResult.IrlsCapReached"/>). Use <see cref="Lav"/> for the exact optimum.
    /// Appended last so every older member keeps its ordinal.</summary>
    LavIrls,
}

/// <summary>Knobs for a <see cref="WlsStateEstimator"/> run.</summary>
public sealed record EstimationOptions
{
    /// <summary>Convergence threshold on ||delta-x||inf (radians / pu).</summary>
    public double Tolerance { get; init; } = 1e-8;

    public int MaxIterations { get; init; } = 30;

    /// <summary>Start from Vm = 1 pu, Va = the slack reference angle. When false (or when
    /// <see cref="WarmStart"/> is set) the start point comes from the warm start / the model.</summary>
    public bool FlatStart { get; init; } = true;

    /// <summary>Seed the iteration from a previously solved or estimated state (matched by bus id) -
    /// the natural chaining for per-timestamp replay estimation.</summary>
    public PowerFlowResult? WarmStart { get; init; }

    /// <summary>Run the chi-square consistency test and largest-normalized-residual removal loop after
    /// convergence (needs redundancy: degrees of freedom &gt; 0).</summary>
    public bool DetectBadData { get; init; } = true;

    /// <summary>A measurement is only removable while its normalized residual exceeds this
    /// (the classical 3sigma identification threshold).</summary>
    public double NormalizedResidualThreshold { get; init; } = 3.0;

    public int MaxBadDataRemovals { get; init; } = 20;

    /// <summary>Confidence level of the chi-square acceptance test.</summary>
    public double ChiSquareConfidence { get; init; } = 0.95;

    /// <summary>Trust cap on one Gauss-Newton step's angle components (radians). When any
    /// component exceeds its cap the whole step is scaled down (direction preserved) - the
    /// standard damping that stops a stressed real-data start from overshooting voltage
    /// magnitudes through zero. Caps never bind near the optimum, so converged answers
    /// (and the machine-precision recovery gates) are unchanged.</summary>
    public double MaxAngleStepRad { get; init; } = 0.2;

    /// <summary>Trust cap on one step's voltage-magnitude components (pu).</summary>
    public double MaxVmStepPu { get; init; } = 0.1;

    // N057 (Ruling R1): the power-flow routing threshold went to 0 (always sparse); the estimator keeps the old 200-bus
    // crossover through its own constant, so every estimate takes exactly the dense / sparse step it took before.
    /// <summary>At or below this bus count the observability gate is the dense gain-rank proof; above it, the sparse
    /// diagnosis (200 buses, <see cref="SolverSelector.DenseSparseCrossoverBuses"/>). Since the 2026-10-15 ruling it
    /// chooses ONLY that report: every plain WLS / robust estimate on the CPU is solved through the one canonical sparse
    /// step (<see cref="CanonicalWlsStep"/>), so the estimate itself is bit-identical whatever this value is.</summary>
    public int SparseBusThreshold { get; init; } = SolverSelector.DenseSparseCrossoverBuses;

    /// <summary>The estimation method (see <see cref="EstimationMethod"/>).</summary>
    public EstimationMethod Method { get; init; } = EstimationMethod.Wls;

    /// <summary>The physical-feasibility envelope for <see cref="EstimationMethod.Feasible"/> - voltage
    /// band/positivity, the interior-guard fraction, the acceptance-gate toggle/tolerance. Ignored by
    /// every other method (they pass no feasibility constraints into the step).</summary>
    public FeasibilityConstraints Feasibility { get; init; } = new();

    /// <summary>Skip the residual-covariance stage (m gain solves) when the chi-square test already
    /// PASSED - the removal loop never runs then, and normalized residuals are purely
    /// diagnostic. Big win for large-m backfill; the residual table carries NaN normalized
    /// residuals in that case, so the default keeps today's fully-populated diagnostics.</summary>
    public bool LeanResiduals { get; init; }

    /// <summary>Opt-in per-iteration diagnostic capture (see <see cref="EstimationTrace"/>).</summary>
    public EstimationTrace? Trace { get; init; }

    /// <summary>
    /// Observability restoration on bad-data removal (A-6): when the LNR loop removes a REAL-POWER
    /// measurement (<see cref="MeasurementKind.PInjMw"/>/<see cref="MeasurementKind.PFlowMw"/>),
    /// substitute a pseudo-measurement evaluated at the START state (the warm start / solved prior)
    /// with a deliberately looser sigma (3x the removed one, floored at 5% of the value), tagged
    /// <c>source="removal-backfill-prior"</c> and itself never removable. Without this, removing the
    /// only informative constraint in a weakly-redundant region (a radial GSP's sole injection) leaves
    /// the state free to run away from every remaining measurement - the fitted answer stays anchored
    /// to the physically-consistent prior instead. Reactive kinds are deliberately NOT backfilled:
    /// in a VAr-blind measurement layer the Q rows are pseudos and the prior embeds them, so a
    /// backfilled defective Q re-injects its own defect and churns the removal loop. Off by default:
    /// it changes what "removed" means (excluded -> replaced-by-prior), so callers opt in.
    /// </summary>
    public bool BackfillRemovedFromPrior { get; init; }

    /// <summary>
    /// Compute the per-state error covariance diag(G^-1) at a converged estimate and expose it as
    /// per-bus 1-sigma bounds (<see cref="EstimationResult.StateVmSigma"/>/<see cref="EstimationResult.StateVaSigmaDeg"/>) -
    /// the estimator's own confidence in each state, the natural companion to a real-time estimate and the
    /// prior covariance a recursive filter needs. Off by default: it forms and factors a dense gain
    /// (one O(s^3) factorization + s solves), so it is gated to states within the dense covariance limit;
    /// above it the bounds stay null pending a sparse selective-inversion path.
    /// </summary>
    public bool ComputeStateCovariance { get; init; }

    /// <summary>The Huber transition threshold k on the standardized residual, shared by
    /// <see cref="EstimationMethod.Huber"/> and <see cref="EstimationMethod.SchweppeGm"/> (quadratic within,
    /// linear beyond). Default 1.345 gives ~95% Gaussian efficiency; leaving it preserves today's behaviour.</summary>
    public double HuberK { get; init; } = 1.345;

    /// <summary>
    /// Refuse a knob that has no meaning, before the estimate runs (fail loud, naming it). An invalid confidence made
    /// the chi-square threshold NaN, so the bad-data loop removed rows while the test was reported NotRun; a HuberK at
    /// or below 0 gave negative IRLS weights; a non-positive step cap reversed the step; a NaN or non-positive
    /// tolerance could never converge, and +Infinity converged at once.
    /// </summary>
    public void Validate()
    {
        static void Positive(double v, string name)
        {
            if (!(v > 0) || !double.IsFinite(v))
                throw new ArgumentOutOfRangeException(name, v, $"EstimationOptions.{name} must be a finite number above 0.");
        }
        Positive(Tolerance, nameof(Tolerance));
        if (MaxIterations < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxIterations), MaxIterations, "EstimationOptions.MaxIterations must be 1 or more.");
        Positive(NormalizedResidualThreshold, nameof(NormalizedResidualThreshold));
        if (MaxBadDataRemovals < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxBadDataRemovals), MaxBadDataRemovals, "EstimationOptions.MaxBadDataRemovals must be 0 or more.");
        // GR-531: the backfill prior is the start state. With FlatStart and no WarmStart that is the flat start (1 pu,
        // the reference angle everywhere), so a backfilled flow would be a fabricated near-zero row with a fairly tight
        // sigma; it used to run anyway and steer the removal loop (twenty removals on the manual's planted case14).
        if (BackfillRemovedFromPrior && WarmStart is null && FlatStart)
            throw new ArgumentException(
                "EstimationOptions.BackfillRemovedFromPrior evaluates the removed rows at the start state, and with FlatStart and no WarmStart that is the flat start, not a prior. Give a WarmStart (a solved or estimated state) or set FlatStart = false.");
        if (!(ChiSquareConfidence > 0 && ChiSquareConfidence < 1))
            throw new ArgumentOutOfRangeException(nameof(ChiSquareConfidence), ChiSquareConfidence, "EstimationOptions.ChiSquareConfidence must lie strictly between 0 and 1.");
        Positive(MaxAngleStepRad, nameof(MaxAngleStepRad));
        Positive(MaxVmStepPu, nameof(MaxVmStepPu));
        Positive(HuberK, nameof(HuberK));
        if (SparseBusThreshold < 0)
            throw new ArgumentOutOfRangeException(nameof(SparseBusThreshold), SparseBusThreshold, "EstimationOptions.SparseBusThreshold must be 0 or more.");
        if (!Enum.IsDefined(Method))
            throw new ArgumentOutOfRangeException(nameof(Method), Method, "EstimationOptions.Method is not a defined method.");
    }
}

/// <summary>
/// The classical weighted-least-squares state estimator: given redundant, noisy measurements
/// z = h(x) + e over a network, find the state x^ = (Va, Vm) minimising J(x) = sum (z_i-h_i(x))^2/sigma_i^2.
/// The estimator owns the LOOP - start point, trust-capped damping, convergence, chi-square consistency,
/// largest-normalized-residual bad-data removal - while everything representation-dependent
/// (how H is held, how the gain is formed/solved, observability gating, residual covariance)
/// lives behind the <see cref="IWlsStep"/> seam, with dense-LU and sparse Gilbert-Peierls
/// backends today and alternative methods (constrained, robust, orthogonal) to follow. Unlike a
/// power flow - which needs a complete, exactly-determined P/Q spec at every bus - the estimator
/// takes whatever measurements exist, wherever they are, and lets redundancy fight noise. A
/// singular gain matrix means the measurements cannot determine the state (unobservable) and is
/// reported honestly, never as NaN. Critical measurements - the sole observers of a state
/// direction - are flagged and protected from removal.
/// </summary>
public sealed class WlsStateEstimator
{
    /// <summary>Source tag on prior-backfill pseudo-measurements (see
    /// <see cref="EstimationOptions.BackfillRemovedFromPrior"/>). Backfills are never removable.</summary>
    public const string BackfillSource = "removal-backfill-prior";

    /// <summary>Above this state size the opt-in dense state-covariance (diag(G^-1)) is skipped - the dense
    /// form/factor is O(s^3) in time and O(s^2) in memory, so gb-scale awaits a sparse selective-inversion
    /// path. gb-spine (~630 states) sits comfortably under it; gb-full does not. The value lives in the one
    /// dense-limits table (R119).</summary>
    private const int CovarianceStateLimit = DenseEstimationLimits.StateCovarianceMaxState;

    // ---- Named IRLS and backfill constants (October 2026 review, WS13 R175). Values unchanged.

    /// <summary>Cap on the IRLS outer loop (weight re-derivations) of the robust methods (Huber, LAV,
    /// Schweppe-GM). The plain WLS runs exactly one outer pass.</summary>
    public const int IrlsMaxOuterIterations = 10;

    /// <summary>
    /// Cap on the IRLS outer loop of the bounded-influence M-estimators, <see cref="EstimationMethod.Huber"/> and
    /// <see cref="EstimationMethod.SchweppeGm"/> (N331, 1.95.249; LAV keeps <see cref="IrlsMaxOuterIterations"/>).
    /// Their weights converge linearly: on clean case14 synthetic sets the largest relative weight change falls by
    /// about 0.65 per pass (Schweppe-GM seed 3: 0.562, 0.083, 0.052, ... 0.00255 at pass 10, 0.00073 at pass 13), so
    /// the old cap of 10 stopped them short of <see cref="IrlsWeightTolerance"/> and reported a state that is not the
    /// estimator's fixed point (flagged since R175). An estimate that settles within 10 passes runs the identical loop,
    /// so its numbers are bit-identical; only one that hit the old cap changes, to its settled value. The cap stays a
    /// guard (an estimate still unsettled at 50 is flagged exactly as before).
    /// </summary>
    public const int IrlsMaxOuterIterationsMEstimator = 50;

    /// <summary>Floor on |standardised residual| in the LAV weight 1/|r|, so a row fitted exactly does not get an
    /// infinite weight (documented in docs 06-state-estimation).</summary>
    public const double LavResidualFloor = 1e-3;

    /// <summary>The IRLS outer loop stops once no weight factor moves by more than this (relative to the row's
    /// base weight) between passes. The same value as <see cref="LavResidualFloor"/> by coincidence only.</summary>
    public const double IrlsWeightTolerance = 1e-3;

    /// <summary>A prior-backfilled measurement's sigma is at least this multiple of the removed row's own sigma
    /// (see <see cref="EstimationOptions.BackfillRemovedFromPrior"/>): honestly loose.</summary>
    public const double BackfillSigmaMultiplier = 3.0;

    /// <summary>... and at least this fraction of the backfilled value's magnitude.</summary>
    public const double BackfillRelativeSigmaFloor = 0.05;

    private readonly ILinearSolver _linear;

    // ---- N078 (Ruling R1, Mark delegated 2026-10-04): what counts as chi-square redundancy -------------------------
    //
    // The chi-square consistency test compares J(x^) = sum r_i^2 / sigma_i^2 with the chi-square quantile at
    // m - s degrees of freedom: every row beyond the s states is one independent check on the fit. A `prior-solved` row
    // (MeasuredInjectionPrior.SolvedPriorSource: --prior solve turns each voltage-schedule pseudo-row into the voltage
    // of a power flow of the set's OWN injection rows) is a deterministic function of measurements already in the set,
    // so it adds no independent evidence; yet it used to count as one more degree of freedom and add its (small)
    // residual to J. On GDA SP02 of 1 Sep 2026 that let 315 such rows add 315 to the degrees of freedom while
    // contributing J 32.21, and a misfit hid in the headroom (chi-square PASS at Vm 1.27 pu; the flat-prior control
    // failed). The ruling: prior-solved rows no longer count, neither in J nor in the degrees of freedom. They stay in
    // the fit (their weight still shapes the estimate, as a prior should), in the residual list and in the bad-data
    // identification; only the consistency test and the reported J / dof leave them out. A set with no prior-solved
    // row (every --prior flat or --prior schedule run, every synthetic set) sums exactly the same rows in exactly the
    // same order as before, so its J, dof and verdict are bit-identical.

    /// <summary>True when a measurement row counts toward the chi-square test (its residual in J, itself in the
    /// degrees of freedom): every row except the <c>prior-solved</c> rows (N078).</summary>
    internal static bool CountsAsRedundancy(Measurement measurement) =>
        !string.Equals(measurement.Source, MeasuredInjectionPrior.SolvedPriorSource, StringComparison.Ordinal);

    /// <summary>The number of rows of <paramref name="mm"/> that count toward the chi-square test (N078).</summary>
    private static int RedundancyRows(MeasurementModel mm)
    {
        int count = 0;
        for (int r = 0; r < mm.MeasurementCount; r++)
            if (CountsAsRedundancy(mm.Measurements[r])) count++;
        return count;
    }

    public WlsStateEstimator(ILinearSolver? linearSolver = null)
        => _linear = linearSolver ?? new DenseLuSolver();

    /// <summary>A negative state variance no larger than this fraction of the largest one is round-off and reads 0.</summary>
    public const double StateVarianceRoundOffRelative = 1e-12;

    public EstimationResult Estimate(GridModel model, MeasurementSet measurements, EstimationOptions? options = null)
    {
        var opt = options ?? new EstimationOptions();
        // N356 (CF6): LavLp is an alias of Lav (the exact LAV). Normalise it here, once, so the two names run the one
        // code path below and cannot drift apart. Every later test of the method reads opt.Method == Lav.
        if (opt.Method == EstimationMethod.LavLp) opt = opt with { Method = EstimationMethod.Lav };
        opt.Validate();
        measurements.Validate(model);
        int n = model.BusCount;

        var vm = new double[n];
        var va = new double[n];
        InitialState(model, opt, vm, va);
        // The start state doubles as the backfill prior: for --prior=solve pipelines it is the
        // measured-injection solved state, the best model-consistent guess available. (A-6)
        double[] vmPrior = (double[])vm.Clone();
        double[] vaPrior = (double[])va.Clone();

        // N331 (CF5): the exact LAV holds a dense s x s basis inverse and dense copies of the Jacobian rows.
        // N356 (CF6): the exact LAV is now Lav (LavLp normalised to it above); the message names both CLI spellings
        // and the approximate IRLS form under its new name.
        if (opt.Method == EstimationMethod.Lav && 2 * n - 1 > DenseEstimationLimits.StateCovarianceMaxState)
            throw new NotSupportedException(
                $"--method lav (lav-lp) holds a dense basis inverse and is limited to {DenseEstimationLimits.StateCovarianceMaxState} states " +
                $"(the dense-method cap, DenseEstimationLimits.StateCovarianceMaxState); this case has {n} buses (about {2 * n - 1} states). " +
                "Use --method lav-irls (LAV by IRLS, approximate, no state cap) or --method huber at this size.");

        IWlsStep step;
        if (opt.Method is EstimationMethod.Constrained or EstimationMethod.Feasible)
        {
            // Constrained/Feasible are inherently dense (KKT on the dense gain). The feasibility bound
            // is a property of the METHOD, not the dense-vs-sparse selection knob - decoupled when the
            // selection threshold dropped to the measured crossover (~200), which would otherwise have
            // refused 200-3,000-bus cases the dense KKT handles comfortably. Feasible adds the Ring A
            // interior guard (in ApplyStep) and the Ring C acceptance gate (below) on top of the same
            // equality-constrained KKT backend.
            // The cap is the shared dense-method limit (R119), the same value as before.
            const int constrainedDenseLimit = DenseEstimationLimits.DenseMethodMaxBuses;
            if (n > constrainedDenseLimit)
                throw new NotSupportedException(
                    $"--method {opt.Method.ToString().ToLowerInvariant()} currently ships dense only (fails loud above " +
                    $"{constrainedDenseLimit} buses rather than silently running unconstrained).");
            if (opt.Method == EstimationMethod.Feasible) opt.Feasibility.Validate();   // R087: an empty envelope fails loud
            step = opt.Method == EstimationMethod.Feasible
                ? new FeasibleWlsStep(_linear, opt.Feasibility)
                : new ConstrainedDenseWlsStep(_linear);
        }
        else if (opt.Method == EstimationMethod.Qr)
        {
            // R119: QR factorises a dense m x s weighted Jacobian every iteration (two m x s matrices live at
            // once), so it shares the dense-method cap instead of having none: on gb-full the two matrices alone
            // are about 3.9 GB. Fails loud rather than exhausting memory.
            if (n > DenseEstimationLimits.DenseMethodMaxBuses)
                throw new NotSupportedException(
                    $"--method qr is dense only (fails loud above {DenseEstimationLimits.DenseMethodMaxBuses} buses; this case has {n}). " +
                    "Use --method wls, which switches to the sparse backend at this size.");
            step = new QrWlsStep(); // orthogonal, dense - avoids forming the normal equations
        }
        else
        {
            // Ruling (2026-10-15): one canonical CPU step for every size (CanonicalWlsStep: the sparse LU), so an
            // estimate never depends on which side of a size threshold the case falls. SparseBusThreshold now chooses
            // only the observability REPORT (dense rank proof at or below it). A non-CPU linear solver (a GPU dense
            // backend) is the one non-canonical path, as for the Newton engines.
            step = _linear is DenseLuSolver
                ? new CanonicalWlsStep(opt.SparseBusThreshold, n)
                : new DenseWlsStep(_linear);
        }

        var active = measurements.Measurements.ToList();
        var removed = new List<BadDataRemoval>();

        MeasurementModel mm;
        ObservabilityReport observability;
        EstimationStatus status;
        int totalIters = 0;
        double maxUpdate = double.PositiveInfinity;
        double[] normalized = Array.Empty<double>();
        bool[] critical = Array.Empty<bool>();
        double chiThreshold = double.NaN;
        bool chiPassed = true;
        // R175 (Q13): true when the final pass's IRLS outer loop hit IrlsMaxOuterIterations unsettled.
        bool irlsCapReached = false;

        for (int pass = 0; ; pass++)
        {
            mm = new MeasurementModel(model, measurements with { Measurements = active.ToArray() });
            int m = mm.MeasurementCount;
            int s = mm.StateSize;
            var w = new double[m];
            for (int r = 0; r < m; r++) w[r] = 1.0 / (mm.SigmaPu[r] * mm.SigmaPu[r]);
            // N356 (CF6): the IRLS LAV is LavIrls now (Lav is the exact LP form, which keeps no IRLS weights).
            double[] wEff = opt.Method is EstimationMethod.Huber or EstimationMethod.LavIrls or EstimationMethod.SchweppeGm
                ? (double[])w.Clone() : w;

            step.Begin(model, mm, wEff);

            // Observability gate at the current state: a set that cannot determine the state
            // must be reported as such - iterating on a singular gain only manufactures noise.
            observability = step.GateObservability(vm, va);
            if (!observability.Observable)
            {
                // GR-533: the same reset as before the convergence gate below. A later bad-data pass that fails here
                // (with BackfillRemovedFromPrior m is unchanged) must not carry the previous pass's normalised
                // residuals, criticality and chi-square verdict into this pass's measurement list.
                normalized = Array.Empty<double>();
                critical = Array.Empty<bool>();
                chiThreshold = double.NaN;
                chiPassed = true;
                status = EstimationStatus.Unobservable;
                break;
            }

            var dx = new double[s];
            status = EstimationStatus.MaxIterations;
            int iter = 0;
            // Robust methods wrap the Gauss-Newton inner loop in an IRLS outer loop: after each
            // converged inner solve, weights are re-derived from the standardized residuals
            // (Huber: 1 within k, k/|r| beyond; LAV: 1/|r|) and the solve repeats until the
            // weights settle. Outliers are down-weighted smoothly, never removed.
            // N331 (1.95.249): Huber and Schweppe-GM converge linearly and get their own cap; LAV keeps the old one (its
            // IRLS does not settle at all; N331 records that separately).
            int maxOuter = opt.Method switch
            {
                EstimationMethod.Huber or EstimationMethod.SchweppeGm => IrlsMaxOuterIterationsMEstimator,
                EstimationMethod.LavIrls => IrlsMaxOuterIterations,   // N356 (CF6): the IRLS LAV, under its new name
                _ => 1,
            };
            // R175 (Q13): reset every pass, so the flag describes the pass whose state is reported. It becomes
            // true only when the loop below runs out of outer passes with the weights still moving.
            irlsCapReached = false;
            int[]? lavBasis = null;   // N331 (CF5): LavLp's previous optimal basis, the next LP's warm start
            double lavMu = 0.0;       // N331 (CF5): LavLp's proximal weight, carried between iterations (0: the plain LAV step)
            for (int outer = 0; outer < maxOuter; outer++)
            {
                // Later outer passes only change weights (R120): re-weight, never rebuild the structure.
                if (outer > 0) step.RebindWeights(wEff);
                int inner = 0;
                status = EstimationStatus.MaxIterations;
                for (; inner < opt.MaxIterations; inner++)
                {
                    if (opt.Method == EstimationMethod.Lav)   // N356 (CF6): Lav is the exact LP form (LavLp normalised to it)
                    {
                        // N331 (CF5): the exact LAV correction, trust-capped and accepted on the ratio of the true LAV
                        // objective's decrease to the predicted one (proximal retries otherwise). A failed LP or an
                        // unrealisable decrease ends the estimate short of Converged.
                        LavLpOutcome lav = LavLpStep(mm, opt, vm, va, dx, ref lavBasis, ref lavMu);
                        if (lav == LavLpOutcome.Failed) { status = EstimationStatus.MaxIterations; break; }
                        if (lav == LavLpOutcome.Unobservable)
                        {
                            observability = step.GateObservability(vm, va) with { Observable = false };
                            status = EstimationStatus.Unobservable;
                            break;
                        }
                        // Stationary: the linearised optimum predicts no decrease; dx is 0, so the step below applies
                        // nothing and the tolerance test converges.
                    }
                    else if (!step.TrySolve(vm, va, dx))
                    {
                        // R257: for the Feasible method a singular KKT can come from its CONSTRAINT block
                        // (degenerate active inequalities) while the measurements still observe the state.
                        // Re-run the observability gate: if it still passes, the failure is a constraint
                        // degeneracy and is reported as Infeasible, not as a false Unobservable.
                        ObservabilityReport recheck = step.GateObservability(vm, va);
                        if (opt.Method == EstimationMethod.Feasible && recheck.Observable)
                        {
                            observability = recheck;
                            status = EstimationStatus.Infeasible;
                            break;
                        }
                        // Rank/islands were fine at the start point, so a singular gain mid-iteration
                        // means the state wandered somewhere the measurements no longer pin down.
                        observability = recheck with { Observable = false };
                        status = EstimationStatus.Unobservable;
                        break;
                    }

                    (bool finite, maxUpdate, double stepScale, double rawUpdate) = ApplyStep(mm, opt, dx, vm, va,
                        opt.Method == EstimationMethod.Feasible ? opt.Feasibility : null);
                    opt.Trace?.Record(pass, iter + inner, maxUpdate, stepScale);
                    if (!finite) { status = EstimationStatus.NonFinite; break; }
                    // R262: when the Feasible interior guard has scaled the step down (scale < 1), a small APPLIED
                    // step is the guard's doing, not convergence - the guard could even freeze it at zero. Converge
                    // only when the UNSCALED Gauss-Newton correction is also below tolerance. With no guard (every
                    // other method) the scale is 1 and this is exactly the previous test.
                    if (maxUpdate < opt.Tolerance && (stepScale >= 1.0 || rawUpdate < opt.Tolerance))
                    { status = EstimationStatus.Converged; inner++; break; }
                }
                iter += inner;
                if (status != EstimationStatus.Converged
                    || opt.Method is not (EstimationMethod.Huber or EstimationMethod.LavIrls or EstimationMethod.SchweppeGm)) break;

                // IRLS reweight at the converged inner solution.
                var hIrls = new double[m];
                mm.EvaluateH(vm, va, hIrls);
                double maxFactorChange = 0;
                double huberK = opt.HuberK;

                // SchweppeGm judges each row against its LEVERAGE-ADJUSTED (normalized) residual
                // r_Si = |r_i| / (sigma_i * sqrt(1 - K_ii)), obtained directly from the step's
                // NormalizedResiduals - so a masked high-leverage outlier is inflated and down-weighted.
                double[] rNorm = Array.Empty<double>();
                bool[] rCritical = Array.Empty<bool>();
                if (opt.Method == EstimationMethod.SchweppeGm)
                {
                    var resPu = new double[m];
                    for (int r = 0; r < m; r++) resPu[r] = mm.ZPu[r] - hIrls[r];
                    // R260: the leverage K_ii and the residual covariance Omega are defined with the
                    // DECLARED weights W = diag(1/sigma^2) (robust-gm-leverage.md sec. 2). The step was bound
                    // to the IRLS weights wEff, so from the second pass on Omega_ii was inflated by the row's
                    // own previous down-weight (sigma^2/f) and the down-weighting partly cancelled itself
                    // (fixed point f = (k sigma/|r|)^(2/3) instead of Huber's k sigma/|r|). Rebind to the
                    // declared weights for this evaluation; the next outer pass rebinds to wEff for the solve.
                    // On the first pass wEff holds exactly the declared values, so that pass is unchanged.
                    // A weights-only re-bind (R120): same measurement set, so no structural rebuild.
                    step.RebindWeights(w);
                    (rNorm, rCritical) = step.NormalizedResiduals(vm, va, resPu);
                }

                for (int r = 0; r < m; r++)
                {
                    double factor;
                    if (opt.Method == EstimationMethod.SchweppeGm)
                    {
                        // A critical row (sole observer) cannot be down-weighted without losing
                        // observability - leave it at its declared weight.
                        double rSi = rNorm[r];
                        factor = rCritical[r] || !double.IsFinite(rSi)
                            ? 1.0
                            : rSi <= huberK ? 1.0 : huberK / rSi;
                    }
                    else
                    {
                        double rstd = Math.Abs((mm.ZPu[r] - hIrls[r]) / mm.SigmaPu[r]);
                        factor = opt.Method switch
                        {
                            EstimationMethod.Huber => rstd <= huberK ? 1.0 : huberK / rstd,
                            EstimationMethod.LavIrls => 1.0 / Math.Max(rstd, LavResidualFloor),   // N356 (CF6): renamed from Lav
                            _ => 1.0,
                        };
                    }
                    double next = w[r] * factor;
                    maxFactorChange = Math.Max(maxFactorChange, Math.Abs(next - wEff[r]) / w[r]);
                    wEff[r] = next;
                }
                if (maxFactorChange < IrlsWeightTolerance) break;
                // R175 (Q13): the weights have not settled and this was the last outer pass allowed, so the loop
                // ends on the cap. Only a flag: no number and no status changes.
                if (outer == maxOuter - 1) irlsCapReached = true;
            }
            totalIters += iter;

            // Reset the bad-data arrays BEFORE the convergence gate. With BackfillRemovedFromPrior the
            // measurement count is unchanged across passes, so a pass that FAILS here must not carry the
            // previous pass's normalized/criticality arrays - Assemble's `normalized.Length == m` guard
            // would otherwise zip stale values against the new measurement list (every row >= worst gets
            // its removed predecessor's residual). Empty arrays make that guard fall back to NaN.
            normalized = Array.Empty<double>();
            critical = Array.Empty<bool>();
            chiThreshold = double.NaN;
            chiPassed = true;
            if (status != EstimationStatus.Converged) break;

            // N078: the redundancy the chi-square test may count leaves out the prior-solved rows (see CountsAsRedundancy).
            int mChi = RedundancyRows(mm);
            if (!opt.DetectBadData || mChi - s <= 0) break;
            // N331 (CF5): the exact LAV joins the robust branch (it fits, never removes). N356 (CF6): Lav is the exact form
            // (LavLp was normalised to it on entry), LavIrls the IRLS one; both fit, never remove.
            if (opt.Method is EstimationMethod.Huber or EstimationMethod.Lav or EstimationMethod.SchweppeGm or EstimationMethod.LavIrls)
            {
                // Robust methods already down-weighted the outliers; report chi-square against
                // the ORIGINAL sigmas for comparability and skip the removal loop entirely.
                var hRob = new double[m];
                mm.EvaluateH(vm, va, hRob);
                double jRob = 0;
                for (int r = 0; r < m; r++)
                {
                    if (!CountsAsRedundancy(mm.Measurements[r])) continue;   // N078
                    double rPu = mm.ZPu[r] - hRob[r];
                    jRob += rPu * rPu * w[r];
                }
                chiThreshold = ChiSquare.Quantile(mChi - s, opt.ChiSquareConfidence);
                chiPassed = jRob <= chiThreshold;
                break;
            }

            // Converged with redundancy: is the fit consistent with the declared sigmas?
            var h = new double[m];
            mm.EvaluateH(vm, va, h);
            var residualPu = new double[m];
            double objective = 0;
            for (int r = 0; r < m; r++)
            {
                residualPu[r] = mm.ZPu[r] - h[r];   // every row's residual: the bad-data analysis below reads them all
                if (CountsAsRedundancy(mm.Measurements[r]))   // N078: J counts only the rows that are evidence
                    objective += residualPu[r] * residualPu[r] * w[r];
            }
            chiThreshold = ChiSquare.Quantile(mChi - s, opt.ChiSquareConfidence);
            chiPassed = objective <= chiThreshold;
            if (chiPassed && opt.LeanResiduals) break; // consistent fit: skip the m covariance solves

            (normalized, critical) = step.NormalizedResiduals(vm, va, residualPu);
            if (chiPassed) break;

            // Identify: the largest normalized residual among removable measurements - not critical,
            // and not a prior-backfill pseudo (removing the replacement would re-open the hole it plugs).
            int worst = -1;
            double worstRn = opt.NormalizedResidualThreshold;
            for (int r = 0; r < m; r++)
                if (!critical[r] && mm.Measurements[r].Source != BackfillSource &&
                    double.IsFinite(normalized[r]) && normalized[r] > worstRn)
                {
                    worst = r;
                    worstRn = normalized[r];
                }
            if (worst < 0 || removed.Count >= opt.MaxBadDataRemovals)
                break; // chi-square failed but nothing identifiable/removable: report the failure honestly

            Measurement suspect = mm.Measurements[worst];
            removed.Add(new BadDataRemoval(suspect, worstRn, pass));
            active.RemoveAt(worst);
            // Observability restoration (A-6) - REAL-POWER kinds only. P injections/flows carry the
            // telemetered network state, and losing a radial region's sole informative P constraint
            // is what lets the state run away. Reactive rows in the GB measurement layer are
            // themselves pseudos (no VAr telemetry exists), and the solved prior EMBEDS the measured
            // Q - backfilling a systematically-defective Q row re-injects the defect and churns the
            // removal loop to its cap on otherwise-healthy periods.
            if (opt.BackfillRemovedFromPrior &&
                suspect.Kind is MeasurementKind.PInjMw or MeasurementKind.PFlowMw)
            {
                // Plug the hole with the prior's view of the same quantity, honestly loose and
                // loudly tagged. h is per-unit; convert power kinds back.
                var hPrior = new double[m];
                mm.EvaluateH(vmPrior, vaPrior, hPrior);
                double value = hPrior[worst] * model.BaseMva;
                double sigma = Math.Max(BackfillSigmaMultiplier * suspect.Sigma, BackfillRelativeSigmaFloor * Math.Abs(value));
                active.Add(suspect with { Value = value, Sigma = sigma, Source = BackfillSource });
            }
            // Next pass re-estimates warm from the current state with the suspect excluded.
        }

        EstimationResult result = Assemble(model, mm, opt, status, totalIters, maxUpdate, vm, va,
            normalized, critical, chiThreshold, chiPassed, removed, observability);
        result.IrlsCapReached = irlsCapReached;   // R175 (Q13): reported, never hidden

        // N356 (CF6, Ruling R1): a LAV estimate (exact or IRLS) reports the objective LAV minimises, sum |r_i| / sigma_i,
        // and says that its chi-square numbers are not a verdict (the LAV objective is not chi-square distributed, and
        // J at the LAV state is not the minimum of J, so comparing it with a chi-square quantile tests nothing). Every
        // other method keeps ChiSquareIsVerdict = true and LavObjective = null: their results are unchanged bit for bit.
        if (opt.Method is EstimationMethod.Lav or EstimationMethod.LavIrls)
        {
            result.ChiSquareIsVerdict = false;
            if (status == EstimationStatus.Converged) result.LavObjective = LavObjectiveAt(mm, vm, va);
        }
        // Ring C - acceptance gate: a converged feasible-method estimate is audited against the hard
        // physical laws; a violation (a physically-impossible state that slipped past the interior
        // guard, e.g. from linearisation slop) is rejected as Infeasible rather than shipped. The gate
        // evaluates h(x^) via ToPowerFlowResult - pure evaluation, no solver call, so no-forward-inference holds.
        if (opt.Method == EstimationMethod.Feasible && opt.Feasibility.AuditGate
            && result.Status == EstimationStatus.Converged)
        {
            PowerFlowResult projected = result.ToPowerFlowResult(model);
            // R011/R012: KCL (laws 6 and 8) is audited against the injections this estimate is
            // PHYSICALLY bound to - the zero-injection equalities the KKT enforces - and NOT against the
            // case file's static Pg - Pd. A telemetered injection is a noisy observation of a device, so
            // the estimate's departure from it is a statistical residual (judged by the chi-square test
            // and the bad-data loop), not a breach of Kirchhoff's law; and the case file's dispatch is not
            // what the real grid was doing at all. Laws 1, 2 and 3 are identities of the projected state
            // and stay gated exactly as before.
            Physics.PhysicsLawReport report = Physics.PhysicsLaws.AuditAgainst(
                model, projected, ZeroInjectionSpecification(mm), opt.Feasibility.AuditTolMva);
            result.Physics = report;

            // Reject a converged state that either breaks a hard physical law OR exceeds an equipment
            // limit (generator Q-capability, thermal rating, voltage band) - a physically-impossible or
            // operationally-infeasible estimate is not shipped.
            IReadOnlyList<Physics.PhysicsFeasibility.LimitViolation> limits =
                Physics.PhysicsFeasibility.Check(model, projected, opt.Feasibility);
            result.LimitViolations = limits;

            bool lawBreach = !report.Gate(Physics.PhysicsCheckRegistry.HardGateLaws);
            bool limitBreach = opt.Feasibility.RejectOnLimitViolation && limits.Count > 0;
            if (lawBreach || limitBreach)
            {
                result.Status = EstimationStatus.Infeasible;
                result.Converged = false;
            }
        }
        return result;
    }

    /// <summary>
    /// The KCL specification for the Feasible acceptance gate (R011/R012): every active injection row
    /// tagged as a hard zero-injection constraint (<see cref="ConstrainedDenseWlsStep.ConstraintSource"/>)
    /// gives that bus's specified real (PInjMw) or reactive (QInjMvar) injection. These are network facts
    /// (a switching node has no device attached), enforced exactly by the KKT, so the converged state must
    /// meet them to the audit tolerance. Constraint rows of other kinds (a flow, a voltage) are enforced by
    /// the KKT too but are not nodal injections, so they are not part of Kirchhoff's current law. Two
    /// constraint rows for the same bus and component that disagree are contradictory physics, so they
    /// fail loud rather than one silently winning.
    /// </summary>
    internal static Physics.SpecifiedInjections ZeroInjectionSpecification(MeasurementModel mm)
        => ZeroInjectionSpecification(mm.Measurements);

    /// <summary>The same specification built from any list of measurements (for example the residual table of a
    /// finished estimate, which still carries its hard zero-injection rows): the FDIA screen uses it so it audits KCL
    /// exactly where the state is physically bound, never against the case file's static dispatch.</summary>
    internal static Physics.SpecifiedInjections ZeroInjectionSpecification(IEnumerable<Measurement> measurements)
    {
        ArgumentNullException.ThrowIfNull(measurements);
        var pMw = new Dictionary<int, double>();
        var qMvar = new Dictionary<int, double>();
        foreach (Measurement meas in measurements)
        {
            if (!string.Equals(meas.Source, ConstrainedDenseWlsStep.ConstraintSource, StringComparison.Ordinal))
                continue;
            Dictionary<int, double>? target = meas.Kind switch
            {
                MeasurementKind.PInjMw => pMw,
                MeasurementKind.QInjMvar => qMvar,
                _ => null,
            };
            if (target is null) continue;
            if (target.TryGetValue(meas.Bus, out double existing) && existing != meas.Value)
                throw new ArgumentException(
                    $"Two hard zero-injection constraints at bus {meas.Bus} ({meas.Kind}) disagree: {existing} and {meas.Value}. " +
                    "A bus cannot be bound to two different injections.");
            target[meas.Bus] = meas.Value;
        }
        return new Physics.SpecifiedInjections(pMw, qMvar, "the estimator's hard zero-injection constraints");
    }

    /// <summary>
    /// N356 (CF6): the LAV objective sum_i |z_i - h_i(x)| / sigma_i at the state (vm, va), over EVERY active row of the
    /// final measurement model (the rows the LAV fit minimised over; a LAV run removes nothing, so these are the input
    /// rows). Dimensionless, like J. Summed in row order in one serial loop, so the value is deterministic. Pure
    /// evaluation: no solve. A non-finite term (a non-finite state cannot reach here: only a Converged estimate is
    /// evaluated, and Converged needs every value finite) would fail loud rather than be reported.
    /// </summary>
    private static double LavObjectiveAt(MeasurementModel mm, double[] vm, double[] va)
    {
        int m = mm.MeasurementCount;
        var h = new double[m];
        mm.EvaluateH(vm, va, h);
        double sum = 0.0;
        for (int r = 0; r < m; r++)
            sum += Math.Abs(mm.ZPu[r] - h[r]) / mm.SigmaPu[r];
        if (!double.IsFinite(sum))
            throw new InvalidOperationException(
                "N356: the LAV objective of a converged estimate is not finite; the estimate cannot be reported as a LAV result.");
        return sum;
    }

    /// <summary>N331 (CF5): the result of one <see cref="LavLpStep"/>.</summary>
    private enum LavLpOutcome
    {
        /// <summary>dx holds a step that lowers the true LAV objective (already trust-capped and accepted by the proximal
        /// ratio test).</summary>
        Step,
        /// <summary>The linearised optimum predicts no decrease (or a step below the tolerance): dx is that step or 0.</summary>
        Stationary,
        /// <summary>The LP found no s independent rows at this state.</summary>
        Unobservable,
        /// <summary>The LP stopped short of optimal, or no proximal retry realised a predicted decrease: not a result.</summary>
        Failed,
    }

    /// <summary>N331 (CF5): proximal-weight retries of one LavLp correction before a predicted decrease is declared
    /// unrealisable. The weight grows by <see cref="LavLpProximalGrowth"/> per retry, so 40 retries take it from its
    /// start through 4^40 (about 1e24) times: the step is then far below any tolerance.</summary>
    public const int LavLpMaxRetries = 40;

    /// <summary>N331 (CF5): a LavLp step whose predicted decrease (true objective at x minus the linearised objective at
    /// x + dx) is at or below this times (1 + the objective) is the LAV optimum to round-off: the step is taken as
    /// stationary rather than searched. 1e-12 relative is a few hundred ulp of a sum of m terms.</summary>
    public const double LavLpStationaryRelativeDecrease = 1e-12;

    /// <summary>N331 (CF5): a LavLp step is accepted when the actual decrease of the true LAV objective is at least this
    /// fraction of the decrease the linearised problem predicted (the ratio test of trust-region methods; a plain
    /// "any decrease" rule let case30 seed 1 crawl with ever smaller gains).</summary>
    public const double LavLpAcceptRatio = 0.1;

    /// <summary>N331 (CF5): above this ratio of actual to predicted decrease the linearisation is trusted more: the
    /// proximal weight is divided by <see cref="LavLpProximalGrowth"/>.</summary>
    public const double LavLpExpandRatio = 0.75;

    /// <summary>N331 (CF5): the factor the proximal weight grows by on a rejected step and shrinks by on a very good one.</summary>
    public const double LavLpProximalGrowth = 4.0;

    /// <summary>N331 (CF5): the first nonzero proximal weight, as a fraction of the largest w_i max|H_i| of the problem
    /// (the scale at which a proximal row competes with a measurement row). A weight that shrinks below this fraction
    /// returns to 0 (the pure LAV step).</summary>
    public const double LavLpProximalStart = 1e-3;

    /// <summary>
    /// N331 (CF5): one LavLp correction, a proximal (Levenberg-Marquardt style) successive linear program. It solves
    /// min sum_i |r_i - H_i dx| / sigma_i + mu sum_j |dx_j| exactly (<see cref="LavLinearProgram"/>; the proximal term is
    /// s extra rows dx_j = 0 of weight mu, so the problem stays an L1 fit), warm-started from <paramref name="basis"/>.
    /// The step is scaled by the trust caps <see cref="ApplyStep"/> uses (so ApplyStep then applies it unscaled) and
    /// judged by the ratio rho = actual / predicted decrease of the TRUE LAV objective sum |z - h(x)| / sigma, the
    /// predicted decrease taken from the linearised objective (without the proximal term) at the capped step:
    /// rho at or above <see cref="LavLpAcceptRatio"/> accepts (and above <see cref="LavLpExpandRatio"/> shrinks mu);
    /// otherwise mu grows and the LP is solved again. mu = 0 is the plain LAV step, so where the linearisation is good
    /// (near the optimum) the iteration is exactly Abur and Celik's. At the LAV optimum dx = 0 is optimal for every mu, so
    /// the proximal term never moves the converged state. A step already below the tolerance is returned as is (the
    /// loop converges on it); a predicted decrease at round-off level is stationary (dx = 0); a decrease no mu realises
    /// is a failure (never Converged). <paramref name="mu"/> carries between iterations. Serial, fixed order:
    /// bit-identical on every tier.
    /// </summary>
    private static LavLpOutcome LavLpStep(MeasurementModel mm, EstimationOptions opt, double[] vm, double[] va, double[] dx,
        ref int[]? basis, ref double mu)
    {
        int m = mm.MeasurementCount, s = mm.StateSize;
        var h = new double[m];
        mm.EvaluateH(vm, va, h);
        var jac = new SparseJacobianRows(m);
        mm.EvaluateJacobian(vm, va, jac);
        var r = new double[m + s];
        var wl = new double[m + s];
        double f0 = 0.0;
        double muScale = 0.0;   // the largest w_i max|H_i|: the proximal weight's natural scale
        var rows = new IReadOnlyList<(int Col, double Val)>[m + s];
        for (int i = 0; i < m; i++)
        {
            r[i] = mm.ZPu[i] - h[i];
            wl[i] = 1.0 / mm.SigmaPu[i];
            f0 += wl[i] * Math.Abs(r[i]);
            rows[i] = jac.Rows[i];
            foreach ((_, double val) in jac.Rows[i]) muScale = Math.Max(muScale, wl[i] * Math.Abs(val));
        }
        for (int j = 0; j < s; j++) rows[m + j] = new[] { (j, 1.0) };   // the proximal rows dx_j = 0 (r = 0)

        var vmTry = new double[vm.Length];
        var vaTry = new double[va.Length];
        var hTry = new double[m];
        var linRes = new double[m];
        for (int attempt = 0; attempt <= LavLpMaxRetries; attempt++)
        {
            // The LP of this attempt: the m measurement rows alone (mu = 0) or with the s proximal rows of weight mu.
            LavLpSolution lp;
            if (mu > 0.0)
            {
                for (int j = 0; j < s; j++) { r[m + j] = 0.0; wl[m + j] = mu; }
                lp = LavLinearProgram.Solve(rows, r, wl, s, basis);
            }
            else
            {
                lp = LavLinearProgram.Solve(new ArraySegment<IReadOnlyList<(int Col, double Val)>>(rows, 0, m),
                    r[..m], wl[..m], s, basis);
            }
            if (lp.Status == LavLpStatus.Singular) return LavLpOutcome.Unobservable;
            if (lp.Status != LavLpStatus.Optimal) return LavLpOutcome.Failed;
            basis = lp.Basis;
            Array.Copy(lp.Dx, dx, s);

            double rawMax = 0.0;
            for (int c = 0; c < s; c++) rawMax = Math.Max(rawMax, Math.Abs(dx[c]));
            if (rawMax < opt.Tolerance) return LavLpOutcome.Stationary;   // converges as is

            // The trust caps, as ApplyStep computes them (whole step scaled, direction kept).
            double scale = 1.0;
            for (int c = 0; c < s; c++)
            {
                mm.StateBusIndex(c, out bool angle);
                double cap = angle ? opt.MaxAngleStepRad : opt.MaxVmStepPu;
                double mag = Math.Abs(dx[c]);
                if (mag > cap) scale = Math.Min(scale, cap / mag);
            }
            if (scale < 1.0) for (int c = 0; c < s; c++) dx[c] *= scale;

            // Predicted decrease: the measurement rows' linearised objective at the (capped) step.
            double fLin = 0.0;
            for (int i = 0; i < m; i++)
            {
                double hi = 0.0;
                foreach ((int col, double val) in jac.Rows[i]) hi += val * dx[col];
                linRes[i] = r[i] - hi;
                fLin += wl[i] * Math.Abs(linRes[i]);
            }
            double predicted = f0 - fLin;
            if (!(predicted > LavLpStationaryRelativeDecrease * (1.0 + f0)))
            {
                if (mu == 0.0)
                {
                    // The plain LAV step predicts nothing: the state is the LAV optimum of this linearisation.
                    Array.Clear(dx, 0, s);
                    return LavLpOutcome.Stationary;
                }
                // A large mu can shrink the step until it predicts nothing; that says nothing about optimality. Retry
                // with the plain step's verdict (mu = 0) before concluding.
                mu = 0.0;
                continue;
            }

            // Actual decrease of the true objective.
            Array.Copy(vm, vmTry, vm.Length);
            Array.Copy(va, vaTry, va.Length);
            for (int c = 0; c < s; c++)
            {
                int bus = mm.StateBusIndex(c, out bool isAngle);
                if (isAngle) vaTry[bus] += dx[c]; else vmTry[bus] += dx[c];
            }
            mm.EvaluateH(vmTry, vaTry, hTry);
            double f = 0.0;
            for (int i = 0; i < m; i++) f += wl[i] * Math.Abs(mm.ZPu[i] - hTry[i]);
            double rho = (f0 - f) / predicted;
            if (rho >= LavLpAcceptRatio && double.IsFinite(f))
            {
                if (rho > LavLpExpandRatio)
                {
                    mu /= LavLpProximalGrowth;
                    if (mu < LavLpProximalStart * muScale) mu = 0.0;
                }
                return LavLpOutcome.Step;
            }
            mu = mu == 0.0 ? LavLpProximalStart * muScale : mu * LavLpProximalGrowth;
        }
        Array.Clear(dx, 0, s);
        return LavLpOutcome.Failed;
    }

    /// <summary>Apply one (trust-capped) Gauss-Newton step; shared by all step backends.
    /// Returns the applied trust scale too (1.0 = uncapped) for the estimation trace.</summary>
    private static (bool Finite, double MaxUpdate, double Scale, double RawUpdate) ApplyStep(
        MeasurementModel mm, EstimationOptions opt, double[] dx, double[] vm, double[] va,
        FeasibilityConstraints? feas)
    {
        int s = dx.Length;
        double scale = 1.0;
        for (int c = 0; c < s; c++)
        {
            mm.StateBusIndex(c, out bool angle);
            double cap = angle ? opt.MaxAngleStepRad : opt.MaxVmStepPu;
            double mag = Math.Abs(dx[c]);
            if (mag > cap) scale = Math.Min(scale, cap / mag);
        }

        // Ring A - interior guard: never let a voltage magnitude reach zero. For any Vm component whose
        // step would drive it toward the floor, scale the WHOLE step so it closes at most
        // FractionToBoundary of the gap - a fraction-to-boundary rule that keeps the iterate strictly
        // positive without changing the descent direction (the fix for "undamped GN walked Vm through
        // zero"). feas is non-null ONLY for EstimationMethod.Feasible, so every other method is byte-for-
        // byte the previous behaviour.
        if (feas is not null)
        {
            for (int c = 0; c < s; c++)
            {
                int bus = mm.StateBusIndex(c, out bool angle);
                if (angle) continue;
                double step = dx[c] * scale;
                if (step >= 0) continue;                     // Vm rising: cannot hit the floor
                double gap = vm[bus] - feas.VmFloor;         // room above the floor
                if (gap <= 0) { scale = 0; break; }          // already at/below floor: freeze
                if (-step > feas.FractionToBoundary * gap)
                    scale = Math.Min(scale, feas.FractionToBoundary * gap / -dx[c]);
            }
        }

        double maxUpdate = 0;
        double rawUpdate = 0;   // the unscaled correction's largest component (R262)
        bool finite = true;
        for (int c = 0; c < s; c++)
        {
            rawUpdate = Math.Max(rawUpdate, Math.Abs(dx[c]));
            double stepC = dx[c] * scale;
            finite &= double.IsFinite(stepC);
            maxUpdate = Math.Max(maxUpdate, Math.Abs(stepC));
            int bus = mm.StateBusIndex(c, out bool isAngle);
            if (isAngle) va[bus] += stepC; else vm[bus] += stepC;
        }
        // Hard backstop: a voltage magnitude is never allowed below the floor. GR-518: the amount the backstop moves a
        // voltage is part of this iteration's update, so the convergence test (maxUpdate below the tolerance) judges the
        // state actually kept. It used to test the step as computed, so a clamped iteration could read converged on a
        // state the step never produced. With no clamp (every iterate above the floor) nothing changes.
        if (feas is not null)
            for (int i = 0; i < vm.Length; i++)
                if (vm[i] < feas.VmFloor)
                {
                    maxUpdate = Math.Max(maxUpdate, feas.VmFloor - vm[i]);
                    vm[i] = feas.VmFloor;
                }
        for (int i = 0; i < vm.Length && finite; i++)
            finite &= double.IsFinite(vm[i]) & double.IsFinite(va[i]);
        return (finite, maxUpdate, scale, rawUpdate);
    }

    private static EstimationResult Assemble(
        GridModel model, MeasurementModel mm, EstimationOptions opt, EstimationStatus status,
        int iterations, double maxUpdate, double[] vm, double[] va,
        double[] normalized, bool[] critical, double chiThreshold, bool chiPassed,
        List<BadDataRemoval> removed, ObservabilityReport observability)
    {
        int n = vm.Length;
        int m = mm.MeasurementCount;
        var h = new double[m];
        mm.EvaluateH(vm, va, h);

        double objective = 0;
        double objectiveAllRows = 0;   // every row, as J was before N078: only its finiteness is read (the NonFinite gate)
        int redundancyRows = 0;   // N078: the rows that count toward J and the degrees of freedom
        var residuals = new MeasurementResidual[m];
        bool haveRn = normalized.Length == m;
        for (int r = 0; r < m; r++)
        {
            double rPu = mm.ZPu[r] - h[r];
            double wr = 1.0 / (mm.SigmaPu[r] * mm.SigmaPu[r]);
            Measurement meas = mm.Measurements[r];
            objectiveAllRows += rPu * rPu * wr;
            // N078: the reported J (ObjectiveJ) is the statistic the chi-square test judged, so a prior-solved row's
            // residual is left out of it, as the row is left out of the degrees of freedom below. Its residual is still
            // reported row by row.
            if (CountsAsRedundancy(meas))
            {
                objective += rPu * rPu * wr;
                redundancyRows++;
            }
            // Back to the measurement's OWN engineering unit: VaDeg is held in
            // radians internally (deg->rad on ingest), so its factor is 180/pi,
            // not BaseMva - that scaled angle residuals by 100x-ish garbage.
            double scale = meas.Kind switch
            {
                MeasurementKind.VmPu => 1.0,
                MeasurementKind.IMagPu => 1.0,          // current magnitude is per-unit
                MeasurementKind.VaDeg => 180.0 / Math.PI,
                MeasurementKind.IAngDeg => 180.0 / Math.PI, // current angle held in radians internally
                _ => mm.BaseMva,
            };
            residuals[r] = new MeasurementResidual(
                meas, h[r] * scale, rPu * scale,
                haveRn ? normalized[r] : double.NaN,
                haveRn && critical[r]);
        }

        bool converged = status == EstimationStatus.Converged;
        // A non-finite residual on ANY row (prior-solved ones included) still fails the estimate, exactly as before N078.
        if (converged && !double.IsFinite(objectiveAllRows))
        {
            status = EstimationStatus.NonFinite;
            converged = false;
        }

        var vaDeg = new double[n];
        for (int i = 0; i < n; i++) vaDeg[i] = va[i] * 180.0 / Math.PI;

        // Opt-in per-state confidence: diag(G^-1) at the estimate, expressed as per-bus 1-sigma bounds.
        double[]? stateVmSigma = null, stateVaSigmaDeg = null;
        if (opt.ComputeStateCovariance && converged && mm.StateSize <= CovarianceStateLimit)
        {
            double[,] gain = FormDenseGainAt(mm, vm, va);
            double[] diag = BadDataAnalysis.StateCovarianceDiagonal(gain, mm.StateSize);
            stateVmSigma = new double[n];
            var vaSigmaRad = new double[n];
            // diag(G^-1) of an SPD gain is positive. A negative value within round-off of zero (1e-12 of the largest
            // variance) is rounding and reads 0; a larger negative one (an indefinite or ill-conditioned gain), or NaN,
            // has no meaning and is reported as NaN, never as sigma 0 (the strongest possible claim of confidence).
            double maxVar = 0.0;
            foreach (double d in diag) if (d > maxVar) maxVar = d;
            double roundOff = StateVarianceRoundOffRelative * maxVar;
            for (int c = 0; c < mm.StateSize; c++)
            {
                int busIdx = mm.StateBusIndex(c, out bool angle);
                double dv = diag[c];
                double sigma = dv > 0 ? Math.Sqrt(dv) : dv >= -roundOff ? 0.0 : double.NaN;
                if (angle) vaSigmaRad[busIdx] = sigma; else stateVmSigma[busIdx] = sigma;
            }
            stateVaSigmaDeg = new double[n];
            for (int i = 0; i < n; i++) stateVaSigmaDeg[i] = vaSigmaRad[i] * 180.0 / Math.PI;
        }

        return new EstimationResult
        {
            Status = status,
            Converged = converged,
            Iterations = iterations,
            MaxStateUpdate = maxUpdate,
            BusIds = mm.BusIds,
            Vm = vm,
            VaDeg = vaDeg,
            ObjectiveJ = objective,
            DegreesOfFreedom = redundancyRows - mm.StateSize,   // N078: prior-solved rows are not redundancy
            ChiSquareThreshold = chiThreshold,
            ChiSquarePassed = chiPassed,
            // R261: chiThreshold is reset to NaN on every pass and set only when the test is evaluated, so NaN
            // means "never ran" (unobservable / non-converged / non-finite / detection off / no redundancy).
            ChiSquareTest = double.IsNaN(chiThreshold) ? ChiSquareOutcome.NotRun
                          : chiPassed ? ChiSquareOutcome.Passed : ChiSquareOutcome.Failed,
            Residuals = residuals,
            Removed = removed,
            Observability = observability,
            StateVmSigma = stateVmSigma,
            StateVaSigmaDeg = stateVaSigmaDeg,
        };
    }

    /// <summary>Form the dense gain G = H^T W H at (<paramref name="vm"/>, <paramref name="va"/>) using the
    /// DECLARED measurement weights - the covariance the classical WLS estimate variance is read from. Used
    /// only by the opt-in state-covariance path; the solve loop forms its gain inside the step backends.</summary>
    private static double[,] FormDenseGainAt(MeasurementModel mm, double[] vm, double[] va)
    {
        int m = mm.MeasurementCount, s = mm.StateSize;
        var jac = new double[m, s];
        mm.EvaluateJacobian(vm, va, new DenseJacobianSink(jac));
        var g = new double[s, s];
        if (s == 0) return g;
        // R118: each full-row rank-1 update runs the bit-exact vector kernel (separate multiply and add per lane,
        // never fused) over the contiguous row-major storage: the same bits as the scalar loop it replaced.
        ref double gOrigin = ref g[0, 0];
        ref double jacOrigin = ref jac[0, 0];
        for (int r = 0; r < m; r++)
        {
            double wr = 1.0 / (mm.SigmaPu[r] * mm.SigmaPu[r]);
            for (int i = 0; i < s; i++)
            {
                double hwi = jac[r, i] * wr;
                if (hwi == 0.0) continue;
                // g[i, jc] += hwi * jac[r, jc] for jc = 0 .. s-1
                Numerics.SimdKernels.AddScaledRow(ref gOrigin, i * s, ref jacOrigin, r * s, s, hwi, fastFma: false);
            }
        }
        return g;
    }

    private static void InitialState(GridModel model, EstimationOptions opt, double[] vm, double[] va)
    {
        // Reference angle: the first slack's fixed angle (all slack angles stay fixed throughout).
        double refRad = 0;
        foreach (Bus bus in model.Buses)
            if (bus.Type == BusType.Slack) { refRad = bus.VaDeg * Math.PI / 180.0; break; }

        if (opt.WarmStart is PowerFlowResult warm)
        {
            var byId = new Dictionary<int, int>(warm.BusIds.Count);
            for (int i = 0; i < warm.BusIds.Count; i++) byId[warm.BusIds[i]] = i;
            for (int i = 0; i < model.Buses.Count; i++)
            {
                Bus bus = model.Buses[i];
                if (byId.TryGetValue(bus.Id, out int wi))
                {
                    // GR-538: a warm start is a usable state: a finite voltage above 0 and a finite angle. A zero, negative
                    // or NaN value used to run until a division by it made the estimate NonFinite, with nothing naming
                    // the warm start as the cause.
                    if (!(warm.Vm[wi] > 0) || !double.IsFinite(warm.Vm[wi]) || !double.IsFinite(warm.VaDeg[wi]))
                        throw new ArgumentException(
                            $"The warm-start state at bus {bus.Id} is Vm {warm.Vm[wi]} pu, Va {warm.VaDeg[wi]} deg; a warm start needs a finite Vm above 0 and a finite angle.");
                    vm[i] = warm.Vm[wi];
                    va[i] = bus.Type == BusType.Slack ? bus.VaDeg * Math.PI / 180.0 : warm.VaDeg[wi] * Math.PI / 180.0;
                }
                else
                {
                    vm[i] = 1.0;
                    va[i] = bus.Type == BusType.Slack ? bus.VaDeg * Math.PI / 180.0 : refRad;
                }
            }
            return;
        }

        for (int i = 0; i < model.Buses.Count; i++)
        {
            Bus bus = model.Buses[i];
            vm[i] = opt.FlatStart ? 1.0 : bus.Vm;
            va[i] = bus.Type == BusType.Slack || !opt.FlatStart ? bus.VaDeg * Math.PI / 180.0 : refRad;
        }
    }
}
