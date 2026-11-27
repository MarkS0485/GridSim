using System.Threading;

namespace GridSim.Core.PowerFlow;

/// <summary>Knobs for the power-flow solve.</summary>
public sealed record PowerFlowOptions
{
    /// <summary>
    /// Opt-in cooperative cancellation. The Newton-Raphson engines check this at the top of every inner
    /// iteration (~ms granularity), so a long solve dispatched as deferrable work can abort at a cycle
    /// deadline instead of overrunning the window. Default <see cref="CancellationToken.None"/> is a
    /// no-op - <see cref="CancellationToken.ThrowIfCancellationRequested"/> never throws for it, so every
    /// existing solve is bit-for-bit unchanged.
    /// </summary>
    public CancellationToken Cancellation { get; init; } = default;

    /// <summary>Convergence tolerance on the largest power mismatch (p.u.). Validated when set: a finite number in
    /// (0, <see cref="MaxTolerance"/>]. +Infinity (or a units slip such as 1e300) used to report any start point as
    /// Converged at iteration 0, and a NaN or non-positive tolerance could never converge.</summary>
    public double Tolerance
    {
        get => _tolerance;
        init
        {
            if (!(value > 0.0) || !(value <= MaxTolerance))
                throw new ArgumentOutOfRangeException(nameof(Tolerance), value,
                    $"PowerFlowOptions.Tolerance must be a finite number above 0 and at most {MaxTolerance} pu.");
            _tolerance = value;
        }
    }

    private readonly double _tolerance = 1e-8;

    /// <summary>The loosest tolerance a power flow may be asked for (pu): above it a "converged" answer means nothing.</summary>
    public const double MaxTolerance = 1e-2;

    /// <summary>
    /// The cap on Newton steps PER NEWTON PASS (D-DOC-218, October 2026: this used to say only "iteration cap").
    /// <list type="bullet">
    /// <item>The mismatch is evaluated after every step, the last one included, so up to <c>MaxIterations</c> steps are
    /// taken AND judged: a solve whose final allowed step converges is reported converged. (Before the October 2026 fix
    /// the state after the last step was never evaluated, so the cap was in effect one step less.) <c>0</c> evaluates
    /// the starting point only.</item>
    /// <item>With <see cref="EnforceQLimits"/> on, each PV-to-PQ switching pass (up to <see cref="MaxQLimitPasses"/>, plus
    /// one more solve when the pass cap is reached right after a switch, R282) is a Newton pass with its OWN budget of
    /// <c>MaxIterations</c> steps. <see cref="PowerFlowResult.Iterations"/> is the total over all passes, so it can exceed
    /// <c>MaxIterations</c> (at most about (<see cref="MaxQLimitPasses"/> + 1) x <c>MaxIterations</c>).</item>
    /// <item>The backward/forward sweep reads it as its cap on sweeps (one pass; it has no reactive-limit loop).</item>
    /// <item>The OLTC loop (<see cref="SteadyStateSolver"/>) has its own pass cap; each of its re-solves gets this budget
    /// again.</item>
    /// </list>
    /// </summary>
    public int MaxIterations
    {
        get => _maxIterations;
        init
        {
            // 0 evaluates the start point only (documented above); a negative cap used to mean 0 silently.
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(MaxIterations), value, "PowerFlowOptions.MaxIterations must be 0 or more.");
            _maxIterations = value;
        }
    }

    private readonly int _maxIterations = 30;

    /// <summary>
    /// The divergence stop (pu), default <see cref="DefaultDivergeLimitPu"/>. A Newton, fast-decoupled or Gauss-Seidel
    /// pass stops early, with status <see cref="PowerFlowStatus.Diverged"/>, as soon as its largest power mismatch
    /// exceeds the larger of this limit and the mismatch at the pass's own starting point (the starting-point term is
    /// there because a flat start on a large case can itself sit above the limit). The check only reads the mismatch
    /// already computed for the convergence test and stops the loop: no step is changed, so a solve that converges is
    /// bit-for-bit what it was. <see cref="double.PositiveInfinity"/> turns the stop off (every pass then runs to
    /// <see cref="MaxIterations"/>, as before). Validated when set: above 0 (NaN and zero or negative values are refused).
    /// </summary>
    public double DivergeLimitPu
    {
        get => _divergeLimitPu;
        init
        {
            if (!(value > 0.0))
                throw new ArgumentOutOfRangeException(nameof(DivergeLimitPu), value,
                    "PowerFlowOptions.DivergeLimitPu must be above 0 pu (or +Infinity to turn the stop off).");
            _divergeLimitPu = value;
        }
    }

    private readonly double _divergeLimitPu = DefaultDivergeLimitPu;

    /// <summary>The default divergence limit (pu): above this a power flow is fundamentally broken, and no starting point
    /// will fix it.</summary>
    public const double DefaultDivergeLimitPu = 10.0;

    /// <summary>Start PQ buses from a flat 1.00 instead of their model values.</summary>
    public bool FlatStart { get; init; } = false;

    /// <summary>Enforce generator reactive-power limits (PV->PQ switching).</summary>
    public bool EnforceQLimits { get; init; } = true;

    /// <summary>Cap on PV->PQ switching passes.</summary>
    public int MaxQLimitPasses { get; init; } = 100;

    /// <summary>
    /// Opt-in: also <em>revert</em> a bus that was switched PV-&gt;PQ back to PV when it
    /// should no longer be limited (MATPOWER <c>enforce_q_lims</c> style). Default OFF so the
    /// historical one-way switching  -  and every pinned published-solution regression  -  is
    /// bit-for-bit unchanged. See <see cref="NewtonShell.ApplyQLimits"/> for the revert
    /// condition and the anti-oscillation guard.
    /// </summary>
    public bool RevertQLimits { get; init; } = false;

    /// <summary>
    /// The Newton engines' automatic retry from a DC power-flow angle start (<see cref="NewtonShell.SolveWithRetry"/>),
    /// on by default. When the ordinary Newton solve stalls or diverges (<see cref="NewtonShell.Stalled(NewtonShell.NewtonRunEnd, double)"/>), the case is
    /// solved once more with the same options, the bus angles started from the DC power flow. It never runs when the
    /// ordinary solve converges, nor when the iteration cap stops a solve that is still converging, so
    /// <see cref="MaxIterations"/> is honoured, and never when <see cref="MaxIterations"/> is 1 or less (one step is no
    /// evidence of a stall). <c>false</c> turns the retry off: a solve that does not converge is then
    /// reported exactly as the ordinary solve ended.
    /// </summary>
    public bool RetryFromDcStart { get; init; } = true;

    /// <summary>
    /// Opt-in diagnostic recorder. When non-null, the solver captures everything it computes
    /// (admittance matrix, bus spec, per-iteration mismatch/correction/Jacobian, Q-limit switching)
    /// into it for export. <c>null</c> (default) = no capture, zero overhead. See
    /// <see cref="Diagnostics.SolveTrace"/>.
    /// <para>The trace records the ordinary solve only: the automatic retry (<see cref="RetryFromDcStart"/>) runs without
    /// one. When <see cref="PowerFlowResult.NewtonRetry"/> is <see cref="NewtonRetryOutcome.Converged"/>, the trace holds
    /// the failed first attempt, while the result's state, <see cref="PowerFlowResult.Iterations"/> and
    /// <see cref="PowerFlowResult.MaxMismatch"/> are the retry's (the first attempt's are in
    /// <see cref="PowerFlowResult.NewtonRetryNote"/>). For every other outcome the trace describes the reported solve.</para>
    /// </summary>
    public Diagnostics.SolveTrace? Trace { get; init; }

    /// <summary>
    /// Opt-in (F17, October 2026): judge each bus's solved voltage against its OWN limits (<c>Bus.VminPu</c> /
    /// <c>Bus.VmaxPu</c>) where it has them, instead of the one operational band. A STUDY setting, read by the
    /// contingency screen's voltage check (<c>ContingencyAnalysis</c>); the Newton engines never read it, so it changes
    /// no solved number. The Core default stays OFF, so a Core caller's options mean what they always meant. The CLI
    /// contingency verbs (nminus1, nminus2, n-1-1) turn it ON by default since N092 (Ruling R1, Mark delegated
    /// 2026-10-04: PSS/E, PowerWorld and MATPOWER judge buses against the per-bus limits in the data) and OFF with
    /// <c>--global-vlimits</c>; <c>--bus-vlimits</c> is kept as a no-op alias.
    /// </summary>
    public bool PerBusVoltageLimits { get; init; } = false;

    /// <summary>
    /// Opt-in (F20, October 2026): the branch rating a contingency screen judges POST-FAULT loading against. A STUDY
    /// setting, read only by the contingency screen (<c>ContingencyAnalysis</c>); the Newton engines never read it, so
    /// it changes no solved number. The default <see cref="PostFaultRating.RateA"/> is the historical behaviour (every
    /// state against RateA). <see cref="PostFaultRating.RateB"/> / <see cref="PostFaultRating.RateC"/> judge each OUTAGE
    /// state against the branch's RateB / RateC (the base case stays on RateA): NETS SQSS v2.10 (docs/reference S036,
    /// 8 April 2025) asks for no loading above the PRE-FAULT rating before a fault (e.g. paragraph 2.9.1) and no
    /// "unacceptable overloading" after a secured event (paragraph 5.1.8), defined (Definitions) as loading beyond the
    /// equipment's "specified time-related capability"; MATPOWER's RATE_B / RATE_C columns are the usual carriers of
    /// those short-term / emergency ratings. A branch with no RateB / RateC (0) falls back to its RateA.
    /// </summary>
    public PostFaultRating PostFaultRating { get; init; } = PostFaultRating.RateA;
}

/// <summary>F20: which rating a contingency screen holds the post-fault (outage) states to.</summary>
public enum PostFaultRating
{
    /// <summary>RateA (the continuous / pre-fault rating) for every state: the historical behaviour.</summary>
    RateA,
    /// <summary>RateB (short-term) for outage states, RateA for the base case; RateA where RateB is 0.</summary>
    RateB,
    /// <summary>RateC (emergency) for outage states, RateA for the base case; RateA where RateC is 0.</summary>
    RateC,
}
