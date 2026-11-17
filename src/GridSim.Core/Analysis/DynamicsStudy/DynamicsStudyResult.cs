namespace GridSim.Core.Analysis.DynamicsStudy;

/// <summary>Row dispositions and study statuses of the dynamics study.</summary>
public static class DynamicsDisposition
{
    /// <summary>A fixed-time row: every connected machine stayed in synchronism and the angle spread met its limit.</summary>
    public const string Stable = "STABLE";
    /// <summary>A fixed-time row: loss of synchronism or the angle spread limit exceeded.</summary>
    public const string Unstable = "UNSTABLE";
    /// <summary>A CCT row whose stable and unstable endpoints were both found and re-verified.</summary>
    public const string CctBracketed = "CCT BRACKETED";
    /// <summary>A CCT row whose search saw a stable pocket above the first loss: an observed result, no global claim.</summary>
    public const string CctNonMonotone = "CCT NON-MONOTONE";
    /// <summary>A CCT row where even the earliest clearing on the grid loses synchronism.</summary>
    public const string UnstableAtZero = "UNSTABLE AT ZERO CLEARING";
    /// <summary>A CCT row where no clearing time in the search window loses synchronism.</summary>
    public const string StableThroughWindow = "STABLE THROUGH WINDOW";
    /// <summary>The row was simulated but its evidence is incomplete (unsettled converter steps, an endpoint that did not
    /// re-verify).</summary>
    public const string Incomplete = "INCOMPLETE";
    /// <summary>The engine refused or failed the row (named reason).</summary>
    public const string Failed = "FAILED";
    /// <summary>A gate before the rows (coverage, power flow, no-event equilibrium) stopped the study.</summary>
    public const string NotRun = "NOT RUN";
    /// <summary>The run was cancelled before this row started.</summary>
    public const string NotAttempted = "NOT ATTEMPTED";

    /// <summary>The study status when a CCT-only batch was evaluated in full (it has no fixed-time verdict).</summary>
    public const string Evaluated = "EVALUATED";
}

/// <summary>The engine, its scheme and the rules that place events and judge synchronism.</summary>
public sealed record DynamicsEngineDeclaration(string Engine, string Scheme, double StepSeconds, double DurationSeconds,
    double NominalHz, double SlipLimitRad, double MaxAngleSpreadDeg, string EventLandingRule, string CctGridRule,
    string CctCriterion, string AngleConvention, int MaxParallelism);

/// <summary>The power-flow initialisation.</summary>
public sealed record DynamicsPowerFlowEvidence(string Status, bool Converged, int Iterations, double MaxMismatchPu,
    double TolerancePu, int MaxIterations, string Solver);

/// <summary>One machine's initial state as the RMS preparation set it from the solved case.</summary>
public sealed record DynamicsInitialMachine(int Slot, int Bus, string Id, int GeneratorIndex, double DeltaRad,
    double EqpPu, double EdpPu, double EqppPu, double EdppPu, double PmPu, double EfdPu, double HSystemSeconds,
    double TerminalVmPu, double TerminalVaDeg, string? Exciter, string? Governor);

/// <summary>
/// The no-event interval run before any disturbance: the initial-state residuals and the drift over the study duration,
/// each against a limit derived from the solver tolerance (see <see cref="Justification"/>).
/// </summary>
public sealed record DynamicsEquilibriumEvidence(
    double DurationSeconds, double StepSeconds, double ToleranceBasisPu, double SafetyFactor, double MinHSystemSeconds,
    double InitialVoltageResidualPu, double InitialVoltageLimitPu, int InitialVoltageResidualBus,
    double InitialAccelerationResidualPu, double InitialAccelerationLimitPu,
    double MaxSpeedDriftPu, double SpeedDriftLimitPu,
    double MaxAngleDriftRad, double AngleDriftLimitRad,
    double MaxVoltageDriftPu, double VoltageDriftLimitPu,
    bool StayedInSynchronism, bool Held, IReadOnlyList<string> Exceeded, string Justification);

/// <summary>The initialisation evidence: power flow, initial phasors and the no-event equilibrium.</summary>
public sealed record DynamicsInitialisationEvidence(DynamicsPowerFlowEvidence PowerFlow,
    IReadOnlyList<DynamicsInitialMachine> Machines, DynamicsEquilibriumEvidence? Equilibrium);

/// <summary>One event as requested and as the engine applied it.</summary>
/// <param name="InputIndex">Position in the row's event list.</param>
/// <param name="Order">Position in the engine's processing order (time order, input order for equal times).</param>
/// <param name="SimultaneousGroup">Events with exactly equal requested times share a group number.</param>
/// <param name="Kind">The event kind.</param>
/// <param name="Equipment">The native identity it names.</param>
/// <param name="EngineEvent">The engine event it resolved to (engine indices).</param>
/// <param name="RequestedSeconds">The requested time.</param>
/// <param name="AppliedSeconds">The step time the engine applies it at (first step k with k h + h 1e-6 at or after it).</param>
/// <param name="AppliedStep">That step's index k.</param>
/// <param name="LandsOnRequestedTime">True when the applied time is within the stage tolerance of the requested one.</param>
/// <param name="ShiftSeconds">Applied minus requested.</param>
public sealed record DynamicsAppliedEvent(int InputIndex, int Order, int SimultaneousGroup, string Kind, string Equipment,
    string EngineEvent, double RequestedSeconds, double AppliedSeconds, int AppliedStep, bool LandsOnRequestedTime,
    double ShiftSeconds);

/// <summary>One monitored channel's trace, one value per integration step.</summary>
public sealed record DynamicsChannelTrace(string Label, string Kind, string Quantity, string Unit,
    IReadOnlyList<double> Values);

/// <summary>A simulated fixed-time row.</summary>
public sealed record DynamicsRunEvidence(bool StayedInSynchronism, double MaxAngleSpreadDeg, double MaxAngleSpreadAtSeconds,
    int ConverterIterationFailures, int Samples, IReadOnlyList<double> TimeSeconds, IReadOnlyList<DynamicsChannelTrace> Channels);

/// <summary>The network left after a row's events, verified separately by a power flow.</summary>
public sealed record DynamicsPostEventTopology(IReadOnlyList<string> BranchesOut, int Islands, string PowerFlowStatus,
    double? MaxMismatchPu, string Note);

/// <summary>One CCT bracket, endpoints and the criteria it was searched with.</summary>
public sealed record DynamicsCctBracket(double StepSeconds, double OnsetSeconds, double MaxClearingSeconds,
    double PostClearWindowSeconds, int Iterations, int ScanSteps, string Criterion,
    int StableSteps, int? UnstableSteps, double StableSeconds, double? UnstableSeconds, bool UnstableAtZero,
    bool AtStepResolution, bool NonMonotone, double? PocketStableSeconds, int Trials, int TrialsWithConverterIterationFailures,
    bool? StableEndpointVerified, bool? UnstableEndpointVerified);

/// <summary>A CCT row: the bracket at the study step and, when asked, at the refined step, with the shifts.</summary>
public sealed record DynamicsCctEvidence(int FaultBus, IReadOnlyList<string> ClearingTrips, DynamicsCctBracket Coarse,
    DynamicsCctBracket? Refined, double? StableShiftSeconds, double? UnstableShiftSeconds, string Note);

/// <summary>One batch row's result. Exactly one per requested row, in request order.</summary>
public sealed record DynamicsCaseResult(int Index, string Name, DynamicsCaseKind Kind, string Disposition, string? Reason,
    bool Indicative, IReadOnlyList<DynamicsAppliedEvent>? Events, DynamicsRunEvidence? Run, DynamicsPostEventTopology? PostEvent,
    DynamicsCctEvidence? Cct);

/// <summary>Row counts.</summary>
public sealed record DynamicsCaseCounts(int Requested, int Attempted, int Completed, int Failed, int NotRun, int NotAttempted);

/// <summary>
/// RC3 P6: the dynamics study result. The coverage report comes first; <see cref="Status"/> is STABLE, UNSTABLE,
/// EVALUATED (a CCT-only batch) or INCOMPLETE; <see cref="LikeForLike"/> is false whenever a required model is skipped or
/// approximated, and then no STABLE / UNSTABLE status is given whatever the rows show.
/// </summary>
public sealed record DynamicsStudyResult(string Status, bool LikeForLike, IReadOnlyList<string> Reasons,
    DynamicsModelCoverageReport Coverage, DynamicsEngineDeclaration Engine, DynamicsInitialisationEvidence? Initialisation,
    IReadOnlyList<DynamicsCaseResult> Cases, DynamicsCaseCounts Counts);
