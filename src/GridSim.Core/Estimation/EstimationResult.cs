using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Numerics;

namespace GridSim.Core.Estimation;

/// <summary>Terminal outcome of a state estimation. <see cref="EstimationResult.Converged"/> is true
/// <b>only</b> for <see cref="Converged"/>; every other value means the estimate is not trustworthy -
/// the same doctrine as <see cref="PowerFlowStatus"/>.</summary>
/// <summary>The chi-square bad-data test's outcome: whether it ran, and if so whether J passed.</summary>
public enum ChiSquareOutcome
{
    /// <summary>The test did not run (no converged, observable, redundant estimate; or detection off).</summary>
    NotRun,
    /// <summary>The test ran and J was within the threshold.</summary>
    Passed,
    /// <summary>The test ran and J exceeded the threshold.</summary>
    Failed,
}

public enum EstimationStatus
{
    /// <summary>The Gauss-Newton iteration met tolerance and every estimated value is finite.</summary>
    Converged,
    /// <summary>Ran to the iteration cap without meeting tolerance.</summary>
    MaxIterations,
    /// <summary>A NaN/Inf appeared in the state or a correction - the estimate is invalid.</summary>
    NonFinite,
    /// <summary>The measurement set cannot determine the state (singular gain matrix or an
    /// unmeasured island); the reported state is the start point, not an estimate.</summary>
    Unobservable,
    /// <summary>The Feasible method could not produce an acceptable state. Three routes set it (R086: this doc
    /// named only the first): the converged state violates a hard physical law (energy / Ohm / Joule / KCL /
    /// complex power; see <see cref="EstimationResult.Physics"/>); it breaches an equipment limit while
    /// <see cref="FeasibilityConstraints.RejectOnLimitViolation"/> is set (see
    /// <see cref="EstimationResult.LimitViolations"/>); or the constrained KKT solve failed on an observable
    /// measurement set (since 1.95.038; before, that read as Unobservable).</summary>
    Infeasible,
}

/// <summary>
/// One measurement's fit at the estimated state, in the measurement's own engineering units.
/// <see cref="NormalizedResidual"/> is |r|/sqrtOmega (Omega = residual covariance diagonal) when bad-data
/// analysis ran, else NaN; a critical measurement (removing it would make the system unobservable)
/// has no redundancy to normalise against and is flagged instead.
/// </summary>
public sealed record MeasurementResidual(
    Measurement Measurement,
    double Predicted,
    double Residual,
    double NormalizedResidual = double.NaN,
    bool IsCritical = false);

/// <summary>One measurement identified as bad data and removed: the largest normalized residual
/// of removal pass <see cref="Pass"/>.</summary>
public sealed record BadDataRemoval(Measurement Measurement, double NormalizedResidual, int Pass);

/// <summary>The estimated grid state plus everything needed to judge it: convergence, the weighted
/// objective, the chi-square consistency verdict and the per-measurement residual table.</summary>
public sealed class EstimationResult
{
    // Not 'required': set by every construction path (Assemble), and mutated post-construction by the
    // feasibility acceptance gate (Ring C) which can demote a Converged estimate to Infeasible.
    public EstimationStatus Status { get; internal set; }

    /// <summary>True only for <see cref="EstimationStatus.Converged"/>.</summary>
    public bool Converged { get; internal set; }

    /// <summary>The physical-law audit of the estimated state, when the feasibility method
    /// (<see cref="EstimationMethod.Feasible"/>) ran its acceptance gate; null otherwise. A
    /// <see cref="EstimationStatus.Infeasible"/> result carries the violated law here.</summary>
    public Physics.PhysicsLawReport? Physics { get; internal set; }

    /// <summary>Equipment-limit breaches (generator Q-capability, thermal rating, voltage band) found
    /// by the feasibility gate; empty when the state is within all limits. A non-empty list forces
    /// <see cref="EstimationStatus.Infeasible"/> only when <see cref="FeasibilityConstraints.RejectOnLimitViolation"/>
    /// is set; otherwise the breaches are reported and the status stands (R087: this doc said it always forced
    /// Infeasible).</summary>
    public IReadOnlyList<Physics.PhysicsFeasibility.LimitViolation> LimitViolations { get; internal set; }
        = System.Array.Empty<Physics.PhysicsFeasibility.LimitViolation>();

    /// <summary>
    /// True when a robust method (Huber, LAV, Schweppe-GM) ran its IRLS outer loop to the cap
    /// (<see cref="WlsStateEstimator.IrlsMaxOuterIterations"/> for LAV, <see cref="WlsStateEstimator.IrlsMaxOuterIterationsMEstimator"/> for Huber and Schweppe-GM) in the final estimation pass WITHOUT the weights
    /// settling (the last re-weighting still moved some row's weight factor by at least
    /// <see cref="WlsStateEstimator.IrlsWeightTolerance"/>). The state is then the solve under the second-last
    /// weights, not a fixed point of the robust estimator. Always false for the other methods.
    ///
    /// <para>R175 / Q13 (October 2026): added as a flag only. <see cref="Status"/> is deliberately NOT changed (a
    /// truncated IRLS is still a converged Gauss-Newton solve, and demoting it would move every robust-method
    /// verdict), but a caller can no longer mistake a truncated robust estimate for a settled one.</para>
    /// </summary>
    public bool IrlsCapReached { get; internal set; }

    /// <summary>
    /// N356 (CF6, Ruling R1): the least-absolute-values objective sum_i |z_i - h_i(x^)| / sigma_i at the estimate, over
    /// every row the fit used, for a converged LAV estimate (<see cref="EstimationMethod.Lav"/>, its alias
    /// <see cref="EstimationMethod.LavLp"/>, or <see cref="EstimationMethod.LavIrls"/>); null for every other method and
    /// for a LAV run that did not converge. This is the number LAV minimises (J is not), so it is what a LAV run reports.
    /// </summary>
    public double? LavObjective { get; internal set; }

    /// <summary>
    /// N356 (CF6, Ruling R1): whether <see cref="ChiSquarePassed"/> / <see cref="ChiSquareTest"/> are a consistency
    /// VERDICT. True for every method whose J at the estimate is (asymptotically) chi-square distributed under the
    /// declared sigmas (WLS and its variants, and the M-estimators as before). False for the LAV methods: the LAV state
    /// minimises sum |r| / sigma, not J, and neither that objective nor J at the LAV state is chi-square distributed, so
    /// the chi-square figures are filled for comparability only and must not be shown as PASS / FAIL. The CLI prints
    /// <see cref="LavObjective"/> and a NOTE instead. Default true, so every non-LAV result is unchanged.
    /// </summary>
    public bool ChiSquareIsVerdict { get; internal set; } = true;

    /// <summary>Gauss-Newton iterations performed (across bad-data re-estimation passes).</summary>
    public required int Iterations { get; init; }

    /// <summary>||delta-x||inf of the final correction (radians / pu) - the convergence figure of merit.</summary>
    public required double MaxStateUpdate { get; init; }

    public required IReadOnlyList<int> BusIds { get; init; }
    public required double[] Vm { get; init; }      // p.u.
    public required double[] VaDeg { get; init; }   // degrees, slack-referenced

    /// <summary>The weighted least-squares objective J(x^) = sum (r_i/sigma_i)^2 at the estimate, over the rows that
    /// count as chi-square redundancy: every row except the <c>prior-solved</c> rows of <c>--prior solve</c>, which add
    /// no independent evidence (N078, Ruling R1; before, J summed every row).</summary>
    public required double ObjectiveJ { get; init; }

    /// <summary>Measurement redundancy: the count of measurements that are independent evidence (every row except the
    /// <c>prior-solved</c> rows, N078) minus state size. Negative or zero means
    /// no redundancy - the chi-square consistency test is meaningless and bad data is undetectable.</summary>
    public required int DegreesOfFreedom { get; init; }

    /// <summary>The chi-square acceptance threshold J must stay under for the residuals to be consistent
    /// with the declared sigmas; NaN when there is no redundancy to test against.</summary>
    public required double ChiSquareThreshold { get; init; }

    /// <summary>True when J(x^) <= <see cref="ChiSquareThreshold"/> (vacuously true with no
    /// redundancy - an undetectable gross error is still possible then). Also true when the test never ran
    /// (unobservable, non-converged or non-finite runs, bad-data detection off): read
    /// <see cref="ChiSquareTest"/> for whether it actually ran (R261). N208 (October 2026): the estimate export does not
    /// copy this flag; it writes <c>chiSquarePassed</c> from <see cref="ChiSquareTest"/>, as null when the test did not
    /// run, so a test that did not run never leaves GridSim as a pass.</summary>
    public required bool ChiSquarePassed { get; init; }

    /// <summary>Whether the chi-square bad-data test actually ran, and its verdict (R261, October 2026).
    /// <see cref="ChiSquareOutcome.NotRun"/> for an unobservable, non-converged or non-finite estimate, for
    /// bad-data detection switched off, and for a set with no redundancy (m - s &lt;= 0).</summary>
    public ChiSquareOutcome ChiSquareTest { get; init; } = ChiSquareOutcome.NotRun;

    /// <summary>Residuals of the measurements the estimate was finally computed from (removed
    /// bad data excluded - see <see cref="Removed"/>).</summary>
    public required IReadOnlyList<MeasurementResidual> Residuals { get; init; }

    /// <summary>Measurements identified as gross errors and excluded, in removal order.</summary>
    public required IReadOnlyList<BadDataRemoval> Removed { get; init; }

    /// <summary>Whether the measurement set determines the state, and where it doesn't.</summary>
    public required ObservabilityReport Observability { get; init; }

    /// <summary>Per-bus 1-sigma uncertainty of the estimated voltage magnitude (pu), from the gain-matrix
    /// state covariance diag(G^-1) - the estimator's own confidence in each Vm. Null unless
    /// <see cref="EstimationOptions.ComputeStateCovariance"/> was set and the state is within the dense
    /// covariance limit (sparse selective-inversion for gb-scale is a follow-up). Indexed like <see cref="Vm"/>.</summary>
    public double[]? StateVmSigma { get; init; }

    /// <summary>Per-bus 1-sigma uncertainty of the estimated voltage angle (degrees). Slack buses carry 0
    /// (fixed reference, no angle state). Null under the same conditions as <see cref="StateVmSigma"/>;
    /// indexed like <see cref="VaDeg"/>.</summary>
    public double[]? StateVaSigmaDeg { get; init; }

    /// <summary>
    /// Project the estimate into the solved-state shape the rest of GridSim consumes (injections,
    /// losses and line flows recomputed at x^ through the same assembly path as the power-flow
    /// solvers), so replay, UI and export plumbing work on estimated states unchanged.
    ///
    /// R108 (October 2026 review): this used to build the dense n x n Y plus two more dense n x n copies (G, B) and
    /// sum injections over all n^2 pairs - about 600 MB at gb-full, once per real-time window, per Feasible gate run,
    /// per export and per FDIA screen. It now assembles the sparse Y exactly as the sparse Newton solver does and
    /// sums over the nonzero cells only (O(nnz)). The result is bit-identical to the dense form: the cells are the
    /// same values in the same column order, and the dense sum's extra terms were exact zeros (adding +/-0 to a
    /// non-zero partial sum leaves it unchanged). Pinned by EstimationProjectionTests.
    /// </summary>
    public PowerFlowResult ToPowerFlowResult(GridModel model)
    {
        int n = model.BusCount;
        var index = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) index[model.Buses[i].Id] = i;
        ComplexCsr y = SparseNewtonRaphsonPowerFlow.BuildY(model, index);

        var vm = new double[n];
        var va = new double[n];
        for (int i = 0; i < n; i++)
        {
            int idx = index[BusIds[i]];
            vm[idx] = Vm[i];
            va[idx] = VaDeg[i] * Math.PI / 180.0;
        }

        var p = new double[n];
        var q = new double[n];
        SparseNewtonRaphsonPowerFlow.ComputeInjections(y, vm, va, p, q);
        return NewtonShell.AssembleResult(
            model, n, vm, va, p, q, model.BaseMva,
            converged: Status == EstimationStatus.Converged,
            totalIters: Iterations, maxMis: MaxStateUpdate, id => index[id]);
    }
}
