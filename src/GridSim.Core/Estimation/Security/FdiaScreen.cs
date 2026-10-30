using GridSim.Core.Model;
using GridSim.Core.Physics;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Estimation.Security;

/// <summary>A protected (PMU-anchored) measurement that the estimated state disagrees with by more than its own
/// noise - the signature of a feasible-landing FDIA attack the envelope check cannot see.</summary>
/// <param name="Anchor">The protected measurement.</param>
/// <param name="ResidualPu">z - h(x_hat) at the anchor, in the model's internal per-unit.</param>
/// <param name="SigmaPu">The anchor's sigma in the same per-unit.</param>
public sealed record ProtectedAnchorViolation(Measurement Anchor, double ResidualPu, double SigmaPu);

/// <summary>The outcome of screening an estimate for a residual-cloaked False Data Injection attack.</summary>
/// <param name="ChiSquarePassed">Whether conventional bad-data detection accepted the estimate.</param>
/// <param name="PhysicallyImpossible">A hard physical law (energy/Ohm/Joule/KCL/complex-power) is violated.</param>
/// <param name="LimitViolations">Every operational-envelope breach of the estimated state (voltage band, thermal,
/// generator Q-capability), significant or not.</param>
/// <param name="SuspectedFdia">Measurement-consistent yet physically implausible, OR inconsistent with a protected
/// anchor - the FDIA signature.</param>
/// <param name="Summary">Human-readable verdict.</param>
/// <param name="SignificantLimitViolations">The subset of <paramref name="LimitViolations"/> whose breach exceeds the
/// estimate's own uncertainty (see <see cref="FdiaScreen.Screen"/>). Only these count towards
/// <paramref name="SuspectedFdia"/>; the others are reported but are within measurement noise.</param>
/// <param name="AnchorInconsistent">True when the estimated state disagrees with a protected (PMU-anchored)
/// measurement by more than the significance margin - catches a feasible-landing attack.</param>
/// <param name="ProtectedAnchorViolations">Each protected anchor the state disagrees with significantly.</param>
public sealed record FdiaScreenReport(
    bool ChiSquarePassed,
    bool PhysicallyImpossible,
    IReadOnlyList<PhysicsFeasibility.LimitViolation> LimitViolations,
    bool SuspectedFdia,
    string Summary,
    IReadOnlyList<PhysicsFeasibility.LimitViolation> SignificantLimitViolations,
    bool AnchorInconsistent = false,
    IReadOnlyList<ProtectedAnchorViolation>? ProtectedAnchorViolations = null);

/// <summary>
/// A second-layer detector for residual-cloaked False Data Injection (FDIA) attacks. Model-based bad-data
/// detection (chi-square + largest-normalized-residual) is <b>provably bypassable</b>: an attack a = H c lies
/// in the range of the measurement Jacobian, leaves every residual unchanged, and shifts the estimated state
/// by c completely undetected (Liu-Ning-Reiter). This screen catches the sub-class of such attacks that
/// pushes the state into a <b>physically-implausible region</b> - a measurement-consistent (chi-square PASS)
/// estimate whose state nevertheless violates a hard physical law or the operational envelope (voltage band,
/// thermal rating, generator reactive capability). It reuses GridSim's physics-conformance audit as an
/// <b>independent validator the attacker must also satisfy</b>.
///
/// <para><b>Feasible-landing attacks and protected anchors.</b> A perfectly crafted attack that lands on another
/// <i>feasible, in-limit</i> operating point is invisible to the envelope and physics checks, because its state
/// is self-consistent. This is caught by a second, independent handle the attacker does not control: a set of
/// <i>protected</i> (PMU-anchored) measurements the attacker cannot forge. If the estimated state disagrees with
/// a protected anchor by more than that anchor's own noise, the state was shifted and the screen flags it
/// (<see cref="FdiaScreenReport.AnchorInconsistent"/>). Supply the trusted PMU rows as
/// <c>protectedAnchors</c>; they must be sources the deployment trusts (not ones a distrusted-source control has
/// down-weighted). Without anchors the screen behaves exactly as before. The screen <b>reports</b>, it never
/// silently rejects - an operator sees "consistent but implausible, or inconsistent with a trusted anchor" and
/// investigates.</para>
/// </summary>
public static class FdiaScreen
{
    /// <summary>The default significance factor k: a limit breach counts as an FDIA indicator only when it exceeds
    /// k standard deviations of the estimated quantity. 3 is the same three-sigma rule the bad-data tests use
    /// (the default normalized-residual and HTI test thresholds).</summary>
    public const double DefaultSignificanceSigmas = 3.0;

    /// <summary>Screen a converged estimate: is it measurement-consistent yet physically implausible?</summary>
    /// <param name="physicsTolMva">
    /// The power-balance tolerance for the hard physical-law gate, in MVA. This screens an ESTIMATE, not an
    /// exact power-flow solution: a WLS/robust fit reconciling noisy, over-determined measurements never
    /// satisfies KCL/complex-power balance to machine precision - its inherent imbalance is of order the
    /// measurement noise (a few % of base on a well-conditioned case). The gate must therefore flag a GROSS
    /// physical impossibility (a residual-cloaked attack drives it orders of magnitude higher - e.g. a
    /// forced out-of-band voltage produces a &gt;500 MVA imbalance on a 100 MVA base, vs ~2-3 MVA for a good
    /// noisy fit), not the fit noise itself. Defaults to <c>0.1 * BaseMva</c> when null - comfortably above a
    /// good estimate's imbalance and well below a gross violation. (Reusing the machine-epsilon
    /// <see cref="FeasibilityConstraints.AuditTolMva"/> here would flag essentially every real estimate.)
    /// </param>
    /// <param name="significanceSigmas">
    /// The significance factor k for limit breaches (October 2026). The same reasoning as for the physics tolerance
    /// applies to the operational limits: an estimate is the true value plus estimation error, so a true state
    /// sitting ON a limit (a PV generator pinned at Qmin, a branch loaded to its rating) gives an estimate that
    /// crosses it about half the time by a noise-sized amount. Comparing with zero margin made the screen report
    /// "SUSPECTED FALSE-DATA INJECTION" (exit 3) on clean synthetic data: case118 at Qg -14.13 against Qmin -14.00
    /// MVAr with 1 MVAr telemetry. A breach is therefore an FDIA indicator only when it exceeds k x u, where u is an
    /// UPPER BOUND on the standard deviation of the estimated quantity taken from the telemetry that measures it.
    /// The bound is exact WLS algebra: the covariance of an estimated measured quantity is
    /// H G^-1 H^T = R - Omega with Omega positive semi-definite, so its standard deviation is at most the
    /// measurement's sigma. Per limit kind: voltage band - the VmPu sigma at that bus; generator Q - the QInjMvar
    /// sigma at that bus (Qg = Qinj + Qload and Qload is fixed for the state); thermal rating - for each end,
    /// (|P| sigma_P + |Q| sigma_Q) / |S| (a first-order bound on the std of |S| that holds for any correlation), the
    /// larger of the two ends because the rating is checked on the more-loaded end and max is 1-Lipschitz. Where
    /// no telemetry measures the quantity there is no bound and the margin is zero (as before). Every breach is
    /// still listed in <see cref="FdiaScreenReport.LimitViolations"/>; only the significant ones raise the flag.
    /// </param>
    /// <param name="protectedAnchors">Trusted (PMU-anchored) measurements the attacker cannot forge. When given,
    /// the estimated state is checked against each: a residual larger than <paramref name="significanceSigmas"/>
    /// times the anchor's own sigma is an inconsistency that flags a feasible-landing attack. Null or empty keeps
    /// the previous behaviour (envelope and physics only).</param>
    public static FdiaScreenReport Screen(
        GridModel model, EstimationResult result, FeasibilityConstraints? constraints = null, double? physicsTolMva = null,
        double significanceSigmas = DefaultSignificanceSigmas,
        IReadOnlyList<Measurement>? protectedAnchors = null)
    {
        // R261: screening needs a converged estimate whose chi-square test actually ran. A non-converged estimate
        // is not a state; a test that never ran is not a pass (it used to be read as one).
        if (!result.Converged)
            throw new InvalidOperationException($"FDIA screen: the estimate did not converge ({result.Status}); there is no state to screen.");
        if (result.ChiSquareTest == ChiSquareOutcome.NotRun)
            throw new InvalidOperationException("FDIA screen: the chi-square bad-data test did not run for this estimate (detection off or no redundancy), so the measurement-consistency half of the screen has no verdict.");
        // GR-515: a LAV estimate's chi-square is documented as no verdict (ChiSquareIsVerdict false); the CLI refuses it,
        // and the Core API now does too, instead of reading its Passed as "measurement-consistent".
        if (!result.ChiSquareIsVerdict)
            throw new InvalidOperationException("FDIA screen: this estimate's chi-square is not a verdict (a LAV estimate), so the measurement-consistency half of the screen has nothing to read.");
        if (!(significanceSigmas >= 0) || !double.IsFinite(significanceSigmas))
            throw new ArgumentOutOfRangeException(nameof(significanceSigmas), significanceSigmas, "the significance factor must be a finite number >= 0.");
        FeasibilityConstraints feas = constraints ?? new FeasibilityConstraints();
        PowerFlowResult projected = result.ToPowerFlowResult(model);

        double tolMva = physicsTolMva ?? 0.1 * model.BaseMva;
        // KCL (laws 6 and 8) is audited against the injections the estimate is PHYSICALLY bound to - its hard
        // zero-injection rows, still present in the residual table - and not against the case file's static Pg - Pd.
        // The plain Audit compared every non-slack injection with the case dispatch, so a correct estimate of real
        // telemetry (whose injections differ from the case file by megawatts at many buses) read "impossible" and,
        // with chi-square passing, "SUSPECTED FALSE-DATA INJECTION". This is the same rule the Feasible gate uses
        // (WlsStateEstimator, AuditAgainst with ZeroInjectionSpecification). Laws 1, 2 and 3 are identities of the
        // projected state and stay gated exactly as before.
        PhysicsLawReport laws = PhysicsLaws.AuditAgainst(model, projected,
            WlsStateEstimator.ZeroInjectionSpecification(result.Residuals.Select(r => r.Measurement)), tolMva);
        bool impossible = !laws.Gate(PhysicsCheckRegistry.HardGateLaws);
        IReadOnlyList<PhysicsFeasibility.LimitViolation> limits = PhysicsFeasibility.Check(model, projected, feas);

        // Keep only the breaches larger than the estimate's own uncertainty.
        var sigmas = new TelemetrySigmas(result.Residuals);
        var significant = new List<PhysicsFeasibility.LimitViolation>(limits.Count);
        foreach (PhysicsFeasibility.LimitViolation v in limits)
        {
            double u = sigmas.UncertaintyBound(v, projected);
            double breach = Math.Abs(v.Value - v.Limit);
            if (breach > significanceSigmas * u) significant.Add(v);
        }
        int withinNoise = limits.Count - significant.Count;

        // Protected-anchor check: does the estimated state disagree with a trusted PMU measurement by more than its
        // own noise? A feasible-landing attack passes chi-square and the envelope, but it cannot move an anchor it
        // did not forge, so the state it produces no longer reproduces that anchor.
        IReadOnlyList<ProtectedAnchorViolation> anchorViolations = protectedAnchors is { Count: > 0 }
            ? CheckProtectedAnchors(model, result, protectedAnchors, significanceSigmas)
            : Array.Empty<ProtectedAnchorViolation>();
        bool anchorInconsistent = anchorViolations.Count > 0;

        bool consistent = result.ChiSquareTest == ChiSquareOutcome.Passed;
        bool suspected = consistent && (impossible || significant.Count > 0 || anchorInconsistent);

        string noiseNote = withinNoise > 0
            ? $" {withinNoise} limit breach(es) lie within {significanceSigmas:G3} sigma of the estimate's uncertainty and are not counted."
            : "";
        string anchorNote = anchorInconsistent
            ? $" The state disagrees with {anchorViolations.Count} protected anchor(s) beyond {significanceSigmas:G3} sigma."
            : "";
        string reason = impossible
            ? "physically impossible"
            : significant.Count > 0
                ? "outside the operational envelope"
                : "inconsistent with a protected anchor";
        string summary = suspected
            ? $"chi-square PASSED but the estimated state is {reason}" +
              $" ({significant.Count} significant limit violation(s)) - possible residual-cloaked FDIA; investigate." + anchorNote + noiseNote
            : consistent
                ? "chi-square PASSED and the state is physically plausible and anchor-consistent - no FDIA indicator." + noiseNote
                : "chi-square FAILED - conventional bad-data detection applies (not a cloaked attack).";

        return new FdiaScreenReport(consistent, impossible, limits, suspected, summary, significant,
            anchorInconsistent, anchorViolations);
    }

    /// <summary>
    /// Evaluate h(x_hat) at every protected anchor and return the anchors the estimated state cannot reproduce
    /// within <paramref name="significanceSigmas"/> times their own sigma. The state is taken from the estimate,
    /// mapped to the model's bus index order (as <see cref="EstimationResult.ToPowerFlowResult"/> does), and h is
    /// evaluated through the shared <see cref="MeasurementModel"/>, so the anchor residual is exactly the estimator's
    /// own z - h(x_hat), with no second model.
    /// </summary>
    private static IReadOnlyList<ProtectedAnchorViolation> CheckProtectedAnchors(
        GridModel model, EstimationResult result, IReadOnlyList<Measurement> anchors, double significanceSigmas)
    {
        int n = model.BusCount;
        var index = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) index[model.Buses[i].Id] = i;

        var vm = new double[n];
        var va = new double[n];
        for (int i = 0; i < result.BusIds.Count; i++)
        {
            int idx = index[result.BusIds[i]];
            vm[idx] = result.Vm[i];
            va[idx] = result.VaDeg[i] * Math.PI / 180.0;
        }

        // GR-514: an anchor is a protected, trusted measurement, so one that cannot be used (a NaN or infinite value, or
        // a sigma that is not a finite number above 0) fails CLOSED: it is refused, naming it. It used to be skipped
        // (or its NaN residual compared false), so a corrupted anchor gave no verdict and the screen passed.
        for (int a = 0; a < anchors.Count; a++)
        {
            Measurement m = anchors[a];
            if (!double.IsFinite(m.Value) || !(m.Sigma > 0) || !double.IsFinite(m.Sigma))
                throw new ArgumentException(
                    $"Protected anchor #{a} ({m.Kind} @ {m.Describe()}) has value {m.Value} and sigma {m.Sigma}; an anchor needs a finite value and a finite sigma above 0.",
                    nameof(anchors));
        }
        var anchorSet = new MeasurementSet { BaseMva = model.BaseMva, Measurements = anchors };
        var anchorModel = new MeasurementModel(model, anchorSet);
        var h = new double[anchorModel.MeasurementCount];
        anchorModel.EvaluateH(vm, va, h);

        var violations = new List<ProtectedAnchorViolation>();
        for (int r = 0; r < anchorModel.MeasurementCount; r++)
        {
            double sigmaPu = anchorModel.SigmaPu[r];
            if (!(sigmaPu > 0) || !double.IsFinite(sigmaPu)) continue;   // an unusable anchor gives no verdict
            double residualPu = anchorModel.ZPu[r] - h[r];
            if (Math.Abs(residualPu) > significanceSigmas * sigmaPu)
                violations.Add(new ProtectedAnchorViolation(anchorModel.Measurements[r], residualPu, sigmaPu));
        }
        return violations;
    }

    /// <summary>
    /// The declared sigmas of the telemetry the estimate used, indexed by location, and the per-limit uncertainty
    /// bound built from them (see <see cref="Screen"/>). Removed bad data is not in the residual table, so it does not
    /// count. Several rows at one BUS (same kind) measure the same quantity, and H G^-1 H^T = R - Omega with Omega
    /// positive semi-definite gives Var(h_i(x^)) &lt;= sigma_i^2 for every one of them, so the SMALLEST sigma is the
    /// tightest valid bound (GR-077; the largest used to be taken, so a loose pseudo row or a distrusted row at the
    /// bus widened the margin enough to hide an envelope breach). Branch rows keep the LARGEST sigma per (From, To,
    /// end): parallel circuits share that key, so the rows may measure different quantities, and only the widest is
    /// a bound for whichever circuit is the one.
    /// </summary>
    private sealed class TelemetrySigmas
    {
        private readonly Dictionary<(MeasurementKind Kind, int Bus), double> _bus = new();
        private readonly Dictionary<(MeasurementKind Kind, int From, int To, BranchEnd End), double> _branch = new();

        public TelemetrySigmas(IReadOnlyList<MeasurementResidual> residuals)
        {
            foreach (MeasurementResidual r in residuals)
            {
                Measurement m = r.Measurement;
                if (!(m.Sigma > 0) || !double.IsFinite(m.Sigma)) continue;
                if (m.IsBranch)
                {
                    var key = (m.Kind, m.From, m.To, m.End);
                    _branch[key] = Math.Max(_branch.GetValueOrDefault(key, 0.0), m.Sigma);
                }
                else
                {
                    var key = (m.Kind, m.Bus);
                    _bus[key] = _bus.TryGetValue(key, out double seen) ? Math.Min(seen, m.Sigma) : m.Sigma;   // GR-077
                }
            }
        }

        /// <summary>The upper bound u on the standard deviation of the violated quantity, or 0 (no margin) when no
        /// telemetry measures it.</summary>
        public double UncertaintyBound(PhysicsFeasibility.LimitViolation v, PowerFlowResult projected)
        {
            switch (v.Kind)
            {
                case "VmBand":
                    return _bus.GetValueOrDefault((MeasurementKind.VmPu, v.A), 0.0);
                case "GenQ":
                    return _bus.GetValueOrDefault((MeasurementKind.QInjMvar, v.A), 0.0);
                case "Rating":
                    double best = 0.0;
                    bool found = false;
                    foreach (LineFlow lf in projected.LineFlows)
                    {
                        if (lf.From != v.A || lf.To != v.B) continue;
                        double u = Math.Max(EndBound(lf.From, lf.To, BranchEnd.From, lf.PFromMw, lf.QFromMvar),
                                            EndBound(lf.From, lf.To, BranchEnd.To, lf.PToMw, lf.QToMvar));
                        if (double.IsNaN(u)) return 0.0;   // an end is unmeasured: no bound, no margin
                        best = Math.Max(best, u);
                        found = true;
                    }
                    return found ? best : 0.0;
                default:
                    return 0.0;   // an unknown limit kind gets no margin (fail towards flagging)
            }
        }

        // First-order bound on std(|S|) at one end: (|P| sigma_P + |Q| sigma_Q) / |S|. NaN when that end lacks a P or
        // a Q measurement (no bound). A zero |S| gives the larger sigma (the limit of the expression is at most that).
        private double EndBound(int from, int to, BranchEnd end, double p, double q)
        {
            if (!_branch.TryGetValue((MeasurementKind.PFlowMw, from, to, end), out double sp)) return double.NaN;
            if (!_branch.TryGetValue((MeasurementKind.QFlowMvar, from, to, end), out double sq)) return double.NaN;
            double s = Math.Sqrt(p * p + q * q);
            return s > 0 ? (Math.Abs(p) * sp + Math.Abs(q) * sq) / s : Math.Max(sp, sq);
        }
    }
}
