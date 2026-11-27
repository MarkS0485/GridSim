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
    /// <summary>The machine identifier, trimmed once here (ANA-22): "1 " and "1" are the same machine everywhere (one place
    /// trimmed, the user-model overlay and the duplicate checks did not, so the second of two models on one unit silently
    /// replaced the first).</summary>
    public string Id { get; init; } = Id is null ? Id! : Id.Trim();

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
    /// <summary>WP2: a three-phase, single line to ground, line to line or double line to ground fault at a bus, bolted or
    /// through a fault impedance (<see cref="GridSim.Core.Dynamics.Models.RmsSequenceFault"/>); cleared by a FaultClear.</summary>
    SequenceFault,
    /// <summary>WP2: the same fault a fraction along a line (<see cref="GridSim.Core.Dynamics.Models.RmsBranchSequenceFault"/>).</summary>
    LineFault,
    /// <summary>WP2: the clearance of a fault along a line (<see cref="GridSim.Core.Dynamics.Models.RmsBranchFaultClear"/>).</summary>
    LineFaultClear,
}

/// <summary>One requested event. Exactly the identity fields its kind needs are set (checked by the runner).</summary>
public sealed record DynamicsEventRequest(DynamicsEventKind Kind, double AtSeconds, int? Bus = null,
    DynamicsBranchId? Branch = null, DynamicsMachineId? Machine = null, double? PMw = null, double? QMvar = null)
{
    /// <summary>WP2: the fault type of a SequenceFault or LineFault event.</summary>
    public GridSim.Core.Protection.FaultType? FaultType { get; init; }
    /// <summary>WP2: the fault resistance (pu on the system base) of a SequenceFault or LineFault event; 0 when not given.</summary>
    public double? FaultRPu { get; init; }
    /// <summary>WP2: the fault reactance (pu on the system base) of a SequenceFault or LineFault event; 0 when not given.</summary>
    public double? FaultXPu { get; init; }
    /// <summary>WP2: the fraction along the line (from its From bus) of a LineFault event.</summary>
    public double? Fraction { get; init; }
}

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
/// <param name="Adaptive">RC4 R4-14: when set, every row is also run by the variable-step kernel and reported beside the fixed-step
/// rows (see <see cref="DynamicsAdaptiveRequest"/>); null leaves the study exactly as it was.</param>
/// <param name="MaxParallelism">1 = serial; above 1, rows run on up to that many workers, each with its own mutable
/// simulation state, results kept in request order.</param>
public sealed record DynamicsStudyRequest(
    PsseParseResult Raw, string DyrText, string DyrName, string SchemasDir,
    DynamicsInitialisation Initialisation, double StepSeconds, double DurationSeconds,
    DynamicsCriteria Criteria, DynamicsMachineId AngleReference,
    IReadOnlyList<DynamicsChannelRequest> Channels, IReadOnlyList<DynamicsCaseRequest> Cases,
    int MaxParallelism = 1,
    IReadOnlyList<UserModelAssignment>? UserModels = null,
    DynamicsAdaptiveRequest? Adaptive = null)   // RC4 R4-14: opt-in variable-step companion run; null = the incumbent study, unchanged
{
    /// <summary>
    /// RC5 (opt-in): the units' own relays for the full-model companion run only, with or without <see cref="Adaptive"/>:
    /// each events row is also re-run on the full-model fixed-step engine with them acting (the result's
    /// <see cref="DynamicsStudyResult.FullModelUnitRelays"/>); the fixed-step rows and any adaptive rows are unchanged. Null
    /// (every existing request) leaves the study exactly as it was. Give unit protection once: setting this and
    /// <see cref="DynamicsAdaptiveRequest.UnitProtection"/> (which drives both companion runs) is an input error.
    /// </summary>
    public GridSim.Core.Protection.UnitProtectionSet? UnitProtection { get; init; }

    /// <summary>
    /// WP6 (RC5, opt-in): RMS models for network devices of the case (an SVC, a STATCOM, an LCC or a VSC link, each bound by
    /// name; see <see cref="GridSim.Core.Dynamics.Models.RmsNetworkDeviceData"/>). Null or empty (every existing request): the
    /// study leaves those devices out of the RMS network, exactly as before.
    /// </summary>
    public IReadOnlyList<GridSim.Core.Dynamics.Models.RmsNetworkDeviceData>? Devices { get; init; }

    /// <summary>
    /// RC5 WP12 (opt-in): plug-in governor and exciter models bound to machines (<see cref="PluginModelAssignment"/>), applied
    /// after <see cref="UserModels"/> by the same route. Null or empty (every existing request): the study is exactly as before.
    /// Every result that uses one carries the plug-in id, the assembly SHA-256, the contract version and the label
    /// OUTSIDE THE VERIFIED CONTRACT in its coverage notes.
    /// </summary>
    public IReadOnlyList<PluginModelAssignment>? PluginModels { get; init; }

    /// <summary>
    /// WP21 (RC5, opt-in): a step schedule for the events rows (<see cref="GridSim.Core.Dynamics.Models.RmsStepSchedule"/>): the
    /// full model takes fixed steps inside each window. The no-event interval and the critical clearing time rows keep
    /// <see cref="StepSeconds"/>. Null (every existing request): the study is exactly as before.
    /// </summary>
    public GridSim.Core.Dynamics.Models.RmsStepSchedule? StepSchedule { get; init; }

    /// <summary>
    /// WP21 (RC5, opt-in): the error-controlled step for the events rows
    /// (<see cref="GridSim.Core.Dynamics.Models.RmsErrorControl"/>). The no-event interval and the critical clearing time rows
    /// keep <see cref="StepSeconds"/>. Null (every existing request): the study is exactly as before. Not with
    /// <see cref="StepSchedule"/>.
    /// </summary>
    public GridSim.Core.Dynamics.Models.RmsErrorControlOptions? ErrorControl { get; init; }

    /// <summary>
    /// RC5 WP23 (opt-in): units of the case modelled as IEC 61400-27-1 style Type 4A or 4B wind turbines
    /// (<see cref="GridSim.Core.Dynamics.Models.Iec61400.Iec61400Data"/>, built from public sources), each named by its native
    /// identity. Such a unit takes no DYR record; its coverage row reads BOUND with the model's description. Null or empty (every
    /// existing request): the study is exactly as before. Not with <see cref="Adaptive"/>, <see cref="StepSchedule"/> or
    /// <see cref="ErrorControl"/>.
    /// </summary>
    public IReadOnlyList<Iec61400StudyUnit>? Iec61400Units { get; init; }

    /// <summary>
    /// RC5 WP32 (opt-in): injection, machine and load models (<see cref="DeviceModelAssignment"/>): a plug-in or declarative
    /// injection model, or a contract 2 machine plug-in, driving a unit as converter plant on its own bus; a plug-in or
    /// declarative load model taking part of a bus load. Every result that uses a plug-in carries its provenance and the label
    /// OUTSIDE THE VERIFIED CONTRACT in its coverage notes. Null or empty (every existing request): the study is exactly as
    /// before. Not with <see cref="Adaptive"/>, <see cref="StepSchedule"/> or <see cref="ErrorControl"/>.
    /// </summary>
    public IReadOnlyList<DeviceModelAssignment>? DeviceModels { get; init; }

    /// <summary>
    /// RC5 wp32b (opt-in): user relays (<see cref="RelayModelAssignment"/>): a relay plug-in or a declarative relay bound to a
    /// unit or to a branch. With them, each events row is also re-run on the full-model fixed-step engine with the relays acting
    /// (and the units' own relays, when unit protection is given), as additional evidence in
    /// <see cref="DynamicsStudyResult.FullModelUnitRelays"/>; the rows of the study are unchanged and decide its status. Null or
    /// empty (every existing request): the study is exactly as before. Not with <see cref="Adaptive"/>,
    /// <see cref="StepSchedule"/> or <see cref="ErrorControl"/>.
    /// </summary>
    public IReadOnlyList<RelayModelAssignment>? RelayModels { get; init; }
}

/// <summary>RC5 WP23: one unit of a dynamics study modelled as an IEC 61400-27-1 style Type 4 wind turbine.</summary>
/// <param name="Machine">The unit, by native identity (bus, id).</param>
/// <param name="Data">The model data (per unit on the unit's MVA base).</param>
public sealed record Iec61400StudyUnit(DynamicsMachineId Machine, GridSim.Core.Dynamics.Models.Iec61400.Iec61400Data Data);
