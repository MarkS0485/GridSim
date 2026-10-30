namespace GridSim.Core.Estimation;

/// <summary>
/// The physical-feasibility envelope the <see cref="EstimationMethod.Feasible"/> solve is held
/// inside. During iteration the interior guard in the Gauss-Newton step enforces voltage POSITIVITY only
/// (Vm stays above <see cref="VmFloor"/>); the voltage BAND (<see cref="VmMin"/> / <see cref="VmMax"/> /
/// <see cref="PerBusVm"/>) is NOT enforced during iteration - it is audited after convergence by
/// <c>PhysicsFeasibility.Check</c>, reported on <see cref="EstimationResult.LimitViolations"/>, and rejects the
/// state only when <see cref="RejectOnLimitViolation"/> is set. The acceptance gate re-audits the converged
/// state against the hard physical laws. Nonlinear inequalities (generator Q-capability, branch thermal ratings,
/// angle-difference limits) are enforced by the active-set backend as it lands - the flags here toggle them
/// without changing this spec. (R087, October 2026 review: this doc used to say the band was enforced during
/// iteration and that VmMin &lt; VmMax was validated at use; neither was true. <see cref="Validate"/> now does the
/// latter, and the Feasible estimator calls it before solving.)
/// </summary>
public sealed record FeasibilityConstraints
{
    /// <summary>Absolute voltage-magnitude floor the state is never allowed to cross (Vm &gt; 0).
    /// The interior guard keeps Vm strictly above this; a bus can never go to zero/negative.</summary>
    public double VmFloor { get; init; } = 1e-6;

    /// <summary>
    /// Fail loud on an empty or meaningless envelope: VmFloor must be positive and finite, the global band must
    /// satisfy VmFloor &lt; VmMin &lt; VmMax, and every per-bus band must satisfy min &lt; max, and the angle limit must be
    /// in (0, pi]. Called by the Feasible estimator before it solves (R087).
    /// </summary>
    public void Validate()
    {
        if (!(VmFloor > 0) || !double.IsFinite(VmFloor))
            throw new ArgumentException($"FeasibilityConstraints.VmFloor must be a positive finite voltage (got {VmFloor}).");
        if (!(VmMin > VmFloor) || !(VmMax > VmMin) || !double.IsFinite(VmMax))
            throw new ArgumentException(
                $"FeasibilityConstraints voltage band is empty or invalid: need VmFloor < VmMin < VmMax (got {VmFloor} / {VmMin} / {VmMax}).");
        if (PerBusVm is not null)
            foreach ((int bus, (double min, double max)) in PerBusVm)
                if (!(max > min) || !(min > 0) || !double.IsFinite(max))
                    throw new ArgumentException($"FeasibilityConstraints per-bus band for bus {bus} is empty or invalid (min {min}, max {max}).");
        if (!(AngleDiffMaxRad > 0) || AngleDiffMaxRad > Math.PI)
            throw new ArgumentException($"FeasibilityConstraints.AngleDiffMaxRad must be in (0, pi] (got {AngleDiffMaxRad}).");
        // GR-516: the step guard's fraction is in (0, 1] (0 froze every downward step, above 1 let a step cross the floor
        // for the backstop to clamp, NaN switched the guard off), and the audit tolerance is a finite number above 0.
        if (!(FractionToBoundary > 0 && FractionToBoundary <= 1))
            throw new ArgumentException($"FeasibilityConstraints.FractionToBoundary must be in (0, 1] (got {FractionToBoundary}).");
        if (!(AuditTolMva > 0) || !double.IsFinite(AuditTolMva))
            throw new ArgumentException($"FeasibilityConstraints.AuditTolMva must be a finite number above 0 (got {AuditTolMva}).");
    }

    /// <summary>Lower voltage band (pu). The default 0.9-1.1 band matches the solver's physical-band
    /// convention; widen per case if a genuine operating point sits outside it.</summary>
    public double VmMin { get; init; } = 0.9;

    /// <summary>Upper voltage band (pu).</summary>
    public double VmMax { get; init; } = 1.1;

    /// <summary>Optional per-bus voltage bounds (bus id -> min,max), overriding the global band.</summary>
    public IReadOnlyDictionary<int, (double Min, double Max)>? PerBusVm { get; init; }

    /// <summary>Maximum across-branch angle difference (radians) - the static-stability ceiling.
    /// Enforced by the active-set backend; pi/2 is the classic limit.</summary>
    public double AngleDiffMaxRad { get; init; } = Math.PI / 2;

    /// <summary>Constrain each generator's reactive output to its [Qmin,Qmax] capability. On by default:
    /// a generator physically cannot exceed its reactive capability, so this is a device limit, not a
    /// preference.</summary>
    public bool EnforceGeneratorQ { get; init; } = true;

    /// <summary>Constrain each branch apparent-power flow to its thermal rating (RateA) at solve time
    /// (the more-loaded end held via the linearised S^2 = P^2 + Q^2). OFF by default: unlike a generator's
    /// reactive capability, a thermal rating is an OPERATIONAL limit a real grid legitimately exceeds for
    /// a time, so forcing the estimate onto the rating would distort the true (over-rating) state rather
    /// than report it. Turn on for scenario/operational-screening solves where a rating-respecting
    /// dispatch is wanted; the overload is otherwise surfaced on
    /// <see cref="EstimationResult.LimitViolations"/> and gated only under
    /// <see cref="RejectOnLimitViolation"/>.</summary>
    public bool EnforceBranchRatings { get; init; }

    /// <summary>Interior-guard fraction-to-boundary: a Vm step is scaled so the magnitude closes at
    /// most this fraction of its distance to the bound, keeping the iterate strictly interior.</summary>
    public double FractionToBoundary { get; init; } = 0.995;

    /// <summary>Run the physical-law acceptance gate on the converged state.</summary>
    public bool AuditGate { get; init; } = true;

    /// <summary>Also demote a state to Infeasible when it breaches an OPERATIONAL limit (generator
    /// Q-capability, thermal rating, voltage band), not just a hard physical law. Off by default: a
    /// real grid legitimately runs branches near/over rating and buses outside the nominal band, so
    /// these are reported (<see cref="EstimationResult.LimitViolations"/>) but do not by themselves
    /// mark a state physically impossible. Turn on for strict operational-feasibility screening.</summary>
    public bool RejectOnLimitViolation { get; init; }

    /// <summary>Tolerance (MVA scale) for the acceptance-gate audit.</summary>
    public double AuditTolMva { get; init; } = 1e-4;

    /// <summary>The Vm bounds for a given bus id (per-bus override else the global band).</summary>
    public (double Min, double Max) VmBand(int busId) =>
        PerBusVm is not null && PerBusVm.TryGetValue(busId, out (double Min, double Max) b) ? b : (VmMin, VmMax);
}
