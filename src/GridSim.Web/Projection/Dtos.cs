namespace GridSim.Web.Projection;

// Raw-number DTOs the browser binds to. Formatting and the voltage/loading colour ramps happen
// client-side (see wwwroot/js). These same records serialize to JSON for the REST/WS API, so there
// is one source of truth for every number. One file per concern is added as tabs land; this file
// holds the topology + NETWORK-map + headline surface (task #2).

/// <summary>A network node - a bus placed on the GB layout box. <c>X</c>/<c>Y</c> are 0..1 layout coords.</summary>
public sealed record NodeDto(int Id, double X, double Y, double BaseKv, string Name, string BusType);

/// <summary>A network edge - a line or transformer. <c>From</c>/<c>To</c> are bus ids; <c>Index</c> is the
/// branch index, which is the join key for per-frame edge state (<see cref="EdgeStateDto"/>).</summary>
public sealed record EdgeDto(int Index, int From, int To, bool IsTransformer, string Name, double RateAMva);

/// <summary>A generator on the fleet overlay: which bus, its size (MW), fuel colour, and whether it spins.
/// <c>Mw</c> is NAMEPLATE (MBase), a one-time topology figure used only to size the fleet marker and the map
/// fly-out's "Capacity" row; it is never live output and must never size a trip (trips go through
/// <c>SimSession.TripLargestInfeed</c>, which reads the live frame). <c>PowerFlowOnly</c> marks a solver device
/// (slack balancer, voltage-control unit): the map neither draws it as plant nor counts it as capacity.</summary>
public sealed record FleetDto(int Bus, double Mw, string Fuel, string FuelColor, bool Synchronous, string Name,
    bool PowerFlowOnly = false);

/// <summary>A transmission boundary cut and the branch (edge) indices whose flows sum across it.</summary>
public sealed record BoundaryCutDto(string Name, double LimitMw, IReadOnlyList<int> EdgeIndices);

/// <summary>
/// The one-time topology payload (type <c>"topology"</c>), cached by the client and reused for every frame -
/// mirrors the bridge protocol's topology-once/state-per-tick split.
/// </summary>
public sealed record TopologyDto(
    string Type,
    string CaseId,
    string CaseLabel,
    double BaseMva,
    int BusCount,
    bool Replay,
    int FrameCount,
    IReadOnlyList<NodeDto> Nodes,
    IReadOnlyList<EdgeDto> Edges,
    IReadOnlyList<FleetDto> Fleet,
    IReadOnlyList<BoundaryCutDto> Boundaries,
    // Additive (FastSchematic port): true when the case ships real GB coordinates (buses laid out in the shared
    // equirectangular GB box), so the coastline/DNO/GSP geography overlays line up. Synthetic/IEEE imports whose
    // layout was fabricated by GraphLayout return false, and the geography toggles stay off/no-op (mirrors
    // WPF FastSchematic.ShowCoast being meaningful only for geolocated models).
    bool IsGeolocated = false)
{
    public TopologyDto() : this("topology", "", "", 100, 0, false, 0,
        Array.Empty<NodeDto>(), Array.Empty<EdgeDto>(), Array.Empty<FleetDto>(), Array.Empty<BoundaryCutDto>()) { }
}

/// <summary>Per-node solved state for a frame: voltage magnitude/angle and net injection.</summary>
public sealed record NodeStateDto(int Id, double Vm, double VaDeg, double PInjMw, double QInjMvar);

/// <summary>Per-edge solved state for a frame, aligned by <c>Index</c> to a topology <see cref="EdgeDto"/>.</summary>
public sealed record EdgeStateDto(int Index, double PFromMw, double QFromMvar, double PLossMw, double LoadingFrac);

/// <summary>A transmission boundary's flow vs its limit for a frame.</summary>
public sealed record BoundaryStateDto(string Name, double FlowMw, double LimitMw, double LoadingPercent, bool Overloaded);

/// <summary>Rolling per-session history for sparklines (oldest -> newest), capped to a fixed window.</summary>
public sealed record TracesDto(
    IReadOnlyList<double> FrequencyHz,
    IReadOnlyList<double> DemandMw,
    IReadOnlyList<double> InertiaGvaS);

/// <summary>Rolling multi-series history for the CHARTS tab (oldest -> newest), capped. RPM + torque are
/// frequency/demand-derived (the grid-as-one-machine view); the rest are raw published channels.</summary>
public sealed record ChartsDto(
    IReadOnlyList<double> FrequencyHz,
    IReadOnlyList<double> Rpm,
    IReadOnlyList<double> GenerationMw,
    IReadOnlyList<double> DemandMw,
    IReadOnlyList<double> InertiaGvaS,
    IReadOnlyList<double> TorqueMnm);

/// <summary>The headline scalars every view shares (the top-of-dashboard tiles).</summary>
public sealed record HeadlineDto(
    double FrequencyHz,
    double RocofHzPerS,
    double GenerationMw,
    double DemandMw,
    double LossMw,
    double InertiaGvaS,
    double AvgVoltagePu,
    bool Converged,
    // Q10 (2026-10): the VOLTAGE ALERT lamp is judged on the lowest and highest bus voltage (the WPF rule,
    // StatusLampRules.IsVoltageAlert), not the average, which hides a single bus out of band.
    double MinVoltagePu,
    double MaxVoltagePu);

/// <summary>
/// One solved instant, projected for the browser (type <c>"state"</c>). Carries the shared headline plus the
/// NETWORK-map per-frame arrays. Later tasks extend this envelope with the other tabs' sub-DTOs.
/// </summary>
public sealed record FrameEnvelope(
    string Type,
    int Tick,
    double Hour,
    string Clock,
    string? Label,
    HeadlineDto Headline,
    IReadOnlyList<NodeStateDto> Nodes,
    IReadOnlyList<EdgeStateDto> Edges,
    IReadOnlyList<BoundaryStateDto> Boundaries,
    // Per-tab sub-DTOs, enriched onto the base frame (null when a tab isn't computed, e.g. one-shot solve).
    TracesDto? Traces = null,
    GenInertiaDto? GenInertia = null,
    OperatorDto? Operator = null,
    MarketDto? Market = null,
    Rp1Dto? Rp1 = null,
    MechanicalDto? Mechanical = null,
    ScadaDto? Scada = null,
    MachineDto? Machine = null,
    // Additive THE MACHINE instrument-dial fields (regulation strain) not carried by the shared MachineDto;
    // produced alongside it by MachineService and consumed by MachineTab's electrical gauge bank.
    MachineViewDto? MachineView = null,
    // (R099: an AssetsDto? Assets slot used to sit here. Nothing ever set or read it - the ASSETS explorer reads
    // Topology, Frame and AssetGraph directly - so it was removed rather than left as a field that is always null.)
    DomainsDto? Domains = null,
    InverterFleetDto? InverterFleet = null,
    ConformanceDto? Conformance = null,
    ExperimentalDto? Experimental = null,
    // Parity scaffolding (task: full-parity tabs). Additive optional slots - null until the owning tab wires
    // its projection. HumanScale is populated per-frame (cheap); Harmonics is a guarded stub (heavy study);
    // MapOverlays carries the GDA bridge condition/stress overlays (N063: filled from the bridge OverlaysBlock on a
    // bridge session, null otherwise).
    HumanScaleDto? HumanScale = null,
    HarmonicsDto? Harmonics = null,
    MapOverlaysDto? MapOverlays = null,
    // ASSETS explorer (parity upgrade): the per-frame asset-graph carrying the deeper per-asset electrical
    // fields (R/X/B/H/tap/OLTC/Q) the navigable detail pages need beyond the topology+state surface. Built in
    // SimSession.BuildEnvelope from the live GridModel + solved PowerFlowResult; null on a bare one-shot solve.
    AssetGraphDto? AssetGraph = null,
    // CYCLE: telemetry of the resident prioritised parallel analysis cycle (SessionCycleService), snapshotted
    // each frame. Null when the cycle engine is disabled or hasn't produced its first cycle yet.
    CycleDto? Cycle = null,
    // CYCLE: why the resident analysis cycle stopped (null while healthy). Shown in place of a stale verdict.
    string? CycleError = null,
    // CHARTS tab: rolling multi-series history (freq/rpm/gen/demand/inertia/torque). Populated in the raw replay
    // envelope; null on solved frames (the full suite has its own per-tab traces).
    ChartsDto? Charts = null,
    // N063: the network map's bridge HUD scalars (fragility = the lake's condition index, RoCoF headroom), from the
    // bridge ScalarsBlock; null on a live / replay session.
    MapHudDto? MapHud = null);

/// <summary>One traced generator->load flow for the EXPERIMENTAL panel.</summary>
public sealed record TraceFlowDto(int GenBus, int LoadBus, double Mw);

/// <summary>The experimental-models rollup for the current frame: reciprocity, the continuum-wave
/// dominant mode and its split, the top traced power flows, and the flow-based carbon intensity.
/// <see cref="WaveDegraded"/> is true when the continuum-wave model could not form this frame (no converged solve,
/// no generators, or a generator without inertia; or the Core model failed loud), so the tab can show the WPF
/// degraded-state note (R099). <see cref="FlowGraphCyclic"/> is true when tracing ran but the flow graph has a
/// loop, so the proportional trace is not defined; the tab then says so instead of reporting an empty result as
/// "no dominant flows" (R099).</summary>
public sealed record ExperimentalDto(
    string ReciprocityStatus,
    double ReciprocityResidual,
    double WaveDominantHz,
    string WaveSplit,
    IReadOnlyList<TraceFlowDto> TopFlows,
    double CarbonGPerKwh,
    bool HeavyModelsSkipped,
    WaveFieldDto? WaveField = null,
    bool WaveDegraded = false,
    bool FlowGraphCyclic = false,
    bool TraceNotConverged = false)
{
    /// <summary>D-DOC-087 (October 2026): the tracing and carbon cards' text for a frame whose power flow did not
    /// converge. Tracing refuses such a state (the convergence contract), so no flows and no intensity are shown.</summary>
    public const string TraceNotConvergedNote =
        "The power flow did not converge on this frame, so there is nothing to trace (no flows, no carbon intensity).";

    /// <summary>The wave card's degraded-state note (the WPF "needs per-machine inertia", expanded to say exactly
    /// what the model needs).</summary>
    public const string WaveDegradedNote =
        "Wave model cannot form on this frame: it needs a converged power flow and a per-machine inertia " +
        "constant (H > 0) for every generator.";

    /// <summary>The tracing card's text when no flows are listed and tracing was not skipped: a cyclic flow graph
    /// (proportional trace not defined, the WPF wording) is told apart from a genuinely empty result.
    /// Static so it is not serialised into the API payload.</summary>
    public static string TraceEmptyText(bool flowGraphCyclic) =>
        flowGraphCyclic
            ? "Flow graph has a cycle - proportional trace not defined."
            : "No dominant traced flows on this frame.";

    /// <summary>
    /// The carbon card's text. When tracing was skipped (large network) <see cref="CarbonGPerKwh"/> is a 0.0
    /// placeholder, not a measurement, so it reads "- (skipped on this large network)" (R302: it used to read
    /// "0 gCO2/kWh", telling a gb-full user the GB system was at zero carbon intensity; the WPF twin shows " - ").
    /// A traced value of exactly 0 (an all-zero-carbon fleet) is real and shown as 0; a non-finite or negative
    /// value is not a valid intensity and shows "-". Static so it is not serialised into the API payload.
    /// </summary>
    public static string FormatCarbon(double gPerKwh, bool skipped)
    {
        if (skipped) return "- (skipped on this large network)";
        if (!double.IsFinite(gPerKwh) || gPerKwh < 0) return "-";
        return $"{gPerKwh:0} gCO\u2082/kWh";
    }
}

/// <summary>A per-bus propagating-wave field for the map's WAVE animation.</summary>
public sealed record WaveFieldDto(double FrequencyHz, int SourceBus, IReadOnlyList<WaveNodeDto> Nodes);

/// <summary>One bus's ripple: amplitude (0..1) and phase delay (radians) from the disturbance source.</summary>
public sealed record WaveNodeDto(int Bus, double Amp, double Phase);

/// <summary>One physical law's verdict for the CONFORMANCE panel: its status pill and residual.
/// <see cref="Hard"/> marks a gate law (energy/Ohm/Joule/KCL/complex-power) whose violation is a
/// physically-impossible state.</summary>
public sealed record LawPillDto(int Number, string Name, string Status, double Residual, string Unit, bool Hard);

/// <summary>The physical-law audit of the current frame's state: the per-law pills and the gate
/// verdict (true = every hard law holds = physically admissible).</summary>
public sealed record ConformanceDto(bool GatePass, IReadOnlyList<LawPillDto> Laws);
