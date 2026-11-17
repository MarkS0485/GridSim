using GridSim.Core.IO.Psse;

namespace GridSim.Core.Analysis.DynamicsStudy;

/// <summary>
/// RC3 P6: how the dynamics study treats a DYR record or value GridSim cannot honour exactly.
/// </summary>
public enum DynamicsUnmodelledPolicy
{
    /// <summary>The default: any skipped or approximated required model stops the study before any simulation (the
    /// coverage report is still produced and the study is INCOMPLETE).</summary>
    Refuse,

    /// <summary>The reader's permissive path: unsupported records and values are left out, the rows are simulated and
    /// reported, but every disposition is labelled indicative and the study status is INCOMPLETE (no like-for-like
    /// verdict).</summary>
    LeaveOut,
}

/// <summary>A machine by its native RAW identity: bus number and machine ID (the key DYR records use).</summary>
public sealed record DynamicsMachineId(int Bus, string Id)
{
    /// <summary>The label used in messages and channel names.</summary>
    public override string ToString() => $"machine '{Id}' at bus {Bus}";
}

/// <summary>A branch by its native RAW identity: from bus, to bus and circuit identifier.</summary>
public sealed record DynamicsBranchId(int From, int To, string Circuit)
{
    /// <summary>The label used in messages.</summary>
    public override string ToString() => $"branch {From}-{To} circuit '{Circuit}'";
}

/// <summary>The event kinds the v1 dynamics study resolves onto the RMS engine. Anything else is refused at parse.</summary>
public enum DynamicsEventKind
{
    /// <summary>A bolted three-phase fault to ground at a bus.</summary>
    BusFault,
    /// <summary>The clearance of a bus fault in place.</summary>
    FaultClear,
    /// <summary>A branch opened at both ends.</summary>
    BranchTrip,
    /// <summary>A branch an earlier event tripped, closed again.</summary>
    BranchReclose,
    /// <summary>A synchronous machine disconnected (its mechanical power zeroed, left out of the synchronism verdict).</summary>
    MachineTrip,
    /// <summary>A constant-admittance load step at a bus (MW, Mvar; negative to shed).</summary>
    LoadStep,
}

/// <summary>One requested event. Exactly the identity fields its kind needs are set (checked by the runner).</summary>
public sealed record DynamicsEventRequest(DynamicsEventKind Kind, double AtSeconds, int? Bus = null,
    DynamicsBranchId? Branch = null, DynamicsMachineId? Machine = null, double? PMw = null, double? QMvar = null);

/// <summary>The monitored channel kinds.</summary>
public enum DynamicsChannelKind
{
    /// <summary>Rotor angle of a machine relative to the study's reference machine, degrees.</summary>
    RotorAngle,
    /// <summary>Rotor speed deviation of a machine, pu of synchronous speed (the engine's own state).</summary>
    Speed,
    /// <summary>Rotor electrical frequency of a machine, Hz: f0 x (1 + speed deviation).</summary>
    Frequency,
    /// <summary>Voltage magnitude (pu) and angle (degrees, engine synchronous frame) of a bus.</summary>
    BusVoltage,
}

/// <summary>One monitored channel: a machine channel names <paramref name="Machine"/>, a bus channel <paramref name="Bus"/>.</summary>
public sealed record DynamicsChannelRequest(DynamicsChannelKind Kind, DynamicsMachineId? Machine = null, int? Bus = null);

/// <summary>The kinds of study row a batch holds.</summary>
public enum DynamicsCaseKind
{
    /// <summary>A fixed event list, judged STABLE or UNSTABLE against the study criteria.</summary>
    Events,
    /// <summary>A critical clearing time bracket for a bolted bus fault cleared by explicit branch trips.</summary>
    CriticalClearing,
}

/// <summary>
/// A critical clearing time search. The search grid is the integration step (the engine ties them: each trial clears
/// after a whole number of steps), so <paramref name="RefinedStepSeconds"/>, when given, repeats the search with that
/// smaller step for both, and the bracket shift is reported separately from any cross-tool difference.
/// </summary>
/// <param name="FaultBus">The faulted bus.</param>
/// <param name="ClearingTrips">The branches opened at the clearing instant (may be empty: a fault cleared in place).</param>
/// <param name="OnsetSeconds">Fault onset (s).</param>
/// <param name="MaxClearingSeconds">Upper end of the clearing-time search window (s).</param>
/// <param name="RefinedStepSeconds">A second, smaller step for the refined search, or null.</param>
/// <param name="Iterations">Bisection halvings (the engine's default 18).</param>
/// <param name="ScanSteps">Verification and pocket-probe depth in steps (the engine's default 16).</param>
public sealed record DynamicsCctRequest(int FaultBus, IReadOnlyList<DynamicsBranchId> ClearingTrips, double OnsetSeconds,
    double MaxClearingSeconds, double? RefinedStepSeconds = null, int Iterations = 18, int ScanSteps = 16);

/// <summary>One row of the batch: a named event list or a critical clearing search.</summary>
public sealed record DynamicsCaseRequest(string Name, DynamicsCaseKind Kind,
    IReadOnlyList<DynamicsEventRequest>? Events = null, DynamicsCctRequest? Cct = null);

/// <summary>The exact initialisation options. Nothing else is taken from the environment.</summary>
/// <param name="PowerFlowTolerancePu">Newton tolerance on the largest bus power mismatch (pu, system base).</param>
/// <param name="PowerFlowMaxIterations">Newton step cap per pass.</param>
/// <param name="Unmodelled">The treatment of DYR records GridSim cannot honour exactly.</param>
public sealed record DynamicsInitialisation(double PowerFlowTolerancePu, int PowerFlowMaxIterations,
    DynamicsUnmodelledPolicy Unmodelled);

/// <summary>The stability criteria of fixed-time rows.</summary>
/// <param name="SlipLimitRad">The engine's synchronism criterion: any connected machine further than this from the
/// inertia-weighted centre-of-inertia angle has lost synchronism (rad). It is also the CCT search criterion.</param>
/// <param name="MaxAngleSpreadDeg">The largest allowed rotor-angle spread (max minus min over connected machines, degrees)
/// at any sample of a fixed-time row. Not applied by the CCT search, which judges synchronism only (stated in the result).</param>
public sealed record DynamicsCriteria(double SlipLimitRad, double MaxAngleSpreadDeg);

/// <summary>
/// RC3 P6: a resolved dynamics study. The RAW parse result and DYR text are the digested inputs (the CLI checks their
/// SHA-256 before building this); every equipment reference is by native identity and is resolved by the runner.
/// </summary>
/// <param name="Raw">The RAW parse result (its machine and branch keys give the native identities).</param>
/// <param name="DyrText">The DYR text.</param>
/// <param name="DyrName">The DYR file name, for provenance notes.</param>
/// <param name="SchemasDir">The v2 schema folder the gridsim-dynamics/2 document is validated against.</param>
/// <param name="Initialisation">Exact initialisation options.</param>
/// <param name="StepSeconds">Integration step (s) of every fixed-time row and of the no-event interval.</param>
/// <param name="DurationSeconds">Run length (s) of every fixed-time row and of the no-event interval.</param>
/// <param name="Criteria">Stability criteria.</param>
/// <param name="AngleReference">The machine rotor angles are reported relative to.</param>
/// <param name="Channels">Monitored channels, in output order.</param>
/// <param name="Cases">The batch rows, in canonical result order.</param>
/// <param name="MaxParallelism">1 = serial; above 1, rows run on up to that many workers, each with its own mutable
/// simulation state, results kept in request order.</param>
public sealed record DynamicsStudyRequest(
    PsseParseResult Raw, string DyrText, string DyrName, string SchemasDir,
    DynamicsInitialisation Initialisation, double StepSeconds, double DurationSeconds,
    DynamicsCriteria Criteria, DynamicsMachineId AngleReference,
    IReadOnlyList<DynamicsChannelRequest> Channels, IReadOnlyList<DynamicsCaseRequest> Cases,
    int MaxParallelism = 1);
