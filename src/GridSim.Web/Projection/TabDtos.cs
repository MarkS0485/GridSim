namespace GridSim.Web.Projection;

// Per-tab raw-number DTOs (extend FrameEnvelope). Formatting + colour happen in the razor components.

// ---- GENERATION & INERTIA ----
public sealed record FuelRowDto(string Fuel, string Color, double Mw, double SharePercent, double InertiaGvaS, bool Synchronous);

public sealed record ImportLinkDto(string Name, double Mw, bool Importing);

public sealed record GenInertiaDto(
    double TotalGenMw, double SyncMw, double ConverterMw, double SyncFraction,
    double InertiaGvaS, double FossilMw, double RenewableMw, double RenewableFraction,
    double CarbonKgPerKwh, double NetImportMw, string StabilityNote,
    IReadOnlyList<FuelRowDto> Fuels, IReadOnlyList<ImportLinkDto> ImportLinks,
    // Estimated behind-the-meter (embedded) generation - solar + embedded wind. 0 outside the raw replay.
    double EmbeddedGenMw = 0);

// ---- OPERATOR ----
public sealed record OpBoundaryRowDto(string Name, double FlowMw, double LimitMw, double HeadroomMw, double LoadingPercent, bool Overloaded);
public sealed record OpFuelRowDto(string Fuel, string Color, double Mw, double SharePercent);

public sealed record OpBmActionDto(string BmUnit, string Kind, double LevelMw, double PriceGbpMwh, string Fuel);
public sealed record OpConstraintDto(string Name, double FlowMw, double LimitMw, double CostGbp, bool Binding);

/// <summary>Lake-only OPERATOR data (present only in bridge mode; null for in-process live/replay).</summary>
public sealed record OperatorLakeDto(
    double? ResponseMw, double? ReserveMw, double? DeratedMarginMw, double? NetInterconnectorMw,
    double? OutturnInertiaGvaS, double? MarketInertiaGvaS, string? Warning,
    IReadOnlyList<OpBmActionDto> BalancingActions, IReadOnlyList<OpConstraintDto> Constraints);

public sealed record OperatorDto(
    double FrequencyHz, double RocofHzPerS, double InertiaGvaS,
    double DemandMw, double GenerationMw, double LossMw,
    double SyncGw, double ConverterGw, double SyncFraction,
    double LargestLossMw, string LargestLossName,
    IReadOnlyList<OpBoundaryRowDto> Boundaries, IReadOnlyList<OpFuelRowDto> Fuel,
    OperatorLakeDto? Lake = null);

// ---- MARKET ----
public sealed record MktInterconnectorRowDto(string Name, double FlowMw, bool Importing);

/// <summary>Lake-only MARKET prices (present only in bridge mode; null for in-process live/replay).</summary>
// ImbalancePriceHistory (R099 parity with the WPF PriceTrace sparkline): the session's rolling record of the
// imbalance prices the lake has streamed, oldest -> newest, capped at TabProjections.ImbalancePriceHistoryLength.
// Null when the session has not received a price yet.
public sealed record MarketLakeDto(
    double? ImbalancePriceGbpMwh, double? SystemSellPriceGbpMwh, double? MarketIndexGbpMwh,
    double? NetImbalanceVolumeMwh, double? DayAheadGbpMwh, double? BsuosGbpMwh,
    double? ConstraintCostGbp, double? CarbonIntensityGco2PerKwh, double? DemandForecastMw,
    double? BalancingCostGbp = null, double? BalancingVolumeMwh = null, int? BalancingActionsN = null,
    double? BmCostGbp = null, double? BmVolumeMwh = null, int? BmUnitsN = null,
    IReadOnlyList<double>? ImbalancePriceHistory = null);

// One accepted balancing action for the MARKET console (offer = To > From, bid = To < From; So = SO-flagged).
public sealed record BalancingActionRowDto(string Time, string Unit, double FromMw, double ToMw, bool So);

public sealed record MarketDto(
    double RenewableSharePercent, double CarbonKgPerKwh, double DemandMw,
    double FossilMw, double RenewableMw,
    IReadOnlyList<MktInterconnectorRowDto> Interconnectors,
    MarketLakeDto? Lake = null,
    IReadOnlyList<BalancingActionRowDto>? Actions = null);

// ---- INVERTER FLEET (authored per-asset dynamics) ----
public sealed record InverterFleetRowDto(
    string ClassName, string Mode, int Count, double OnlineMw, int TrippedCount, double SocPercent);

public sealed record InverterFleetDto(
    double NetFleetMw, double TrippedMw, int ClassCount, int TotalUnits, int TotalTripped,
    IReadOnlyList<InverterFleetRowDto> Classes);

// ---- NESO RP1 ----
public sealed record Rp1InertiaRowDto(string Fuel, string Color, double SecondsGvaS, double SharePercent);
public sealed record Rp1GenRowDto(string Fuel, string Color, double OutputMw, double OutputPercent, double InertiaGvaS, string Kind);

/// <summary>One credible single loss in the N-1 RoCoF screen: the resulting initial RoCoF and how it sits vs limits.</summary>
public sealed record Rp1ScreenRowDto(string Name, string Fuel, string Color, double LossMw, double RocofHzPerS, bool BreachesLegacy, bool BreachesModern);

/// <summary>One interconnector's live flow (import +ve) and whether it sits in a correlated-trip corridor.</summary>
public sealed record Rp1IcRowDto(string Name, double FlowMw, bool Importing, bool Correlated, string CorridorName);

/// <summary>The largest correlated HVDC group: its members lost as one infeed (Policy 0021 s.4).</summary>
public sealed record Rp1CorrGroupDto(string Name, double Mw, IReadOnlyList<Rp1IcRowDto> Members);

/// <summary>One online generator, for the fuel drill-down (per-unit H / stored kinetic energy).</summary>
public sealed record Rp1UnitDto(string Name, string Fuel, string Color, double Mw, double CapMw, double HSeconds, double StoredKeGvaS, bool Synchronous);

public sealed record Rp1Dto(
    double PurchasedTimeSeconds,
    double InertiaTotalGvaS, double InertiaIntrinsicGvaS,
    double LargestUnitMw, string LargestUnitName,
    double CredibleLossMw,
    double DynamicMifGvaS, double RocofHzPerS,
    double SyncGw, double ConverterGw, double SyncFraction,
    string LfddGrade, string Narrative,
    IReadOnlyList<Rp1InertiaRowDto> InertiaBySource, IReadOnlyList<Rp1GenRowDto> GenStack,
    IReadOnlyList<Rp1ScreenRowDto> Screening,
    // ---- market/water inertia, the live correlated HVDC group and the drill-down constituents ----
    double InertiaMarketGvaS,
    Rp1CorrGroupDto CorrelatedGroup,
    IReadOnlyList<Rp1IcRowDto> Interconnectors,
    IReadOnlyList<Rp1UnitDto> Units,
    // Q1 (2026-10): the colour level of LfddGrade from the shared Core ladder (FrequencyRiskPresenter.GradeLevel):
    // "green", "amber" or "red". LfddGrade now carries the ladder step (NORMAL / WATCH / ADVISORY / WARNING, with
    // " down" when a falling trend escalated it), the same text the WPF NESO RP1 tab shows.
    string LfddLevel);

// ---- MECHANICAL (stateful; produced by MechanicalService) ----
public sealed record MachineRowDto(
    string Name, string Fuel, string Color,
    double Pg, double Pmax, double LoadingPercent,
    double Qg, double Qmin, double Qmax,
    double InertiaGvaS, double HSeconds,
    double RampMwPerMin, double StressPercent, bool Synchronous);

public sealed record MechanicalDto(
    double RocofNowHzPerS,
    double RocofLegacyMarginPercent, bool RocofLegacyBreached,
    double RocofModernMarginPercent, bool RocofModernBreached,
    double SumInertiaGvaS, int MachineCount,
    IReadOnlyList<MachineRowDto> Machines);

// ---- SCADA ----
// Stamp is the sim clock of the reading (R099 parity with the WPF STAMP column; every web point is refreshed each
// frame, so it is the frame's clock). Target + TargetKey are the routing key the SCADA row uses to open the asset
// explorer (R099 parity with the WPF OpenPointCmd): which detail page, and its key (a bus id for a substation, a
// model branch index for a circuit or transformer). Target None (system frequency) has no asset page.
public enum ScadaTarget { None, Substation, Circuit, Transformer }

public sealed record ScadaPointDto(string Tag, string Description, string Units, double Value, string Quality, double LoadPercent, bool Alarm,
    string Stamp = "", ScadaTarget Target = ScadaTarget.None, int TargetKey = -1);
public sealed record ScadaEventDto(string Stamp, string Priority, string Tag, string Message);
public sealed record ScadaDto(int PointCount, int GoodCount, int AlarmCount,
    IReadOnlyList<ScadaPointDto> Points, IReadOnlyList<ScadaEventDto> Events);

// ---- THE MACHINE (full mirror of the WPF MachineVm) ----
public sealed record MachineStreamDto(string Name, string Color, double Gw, double SharePercent);
public sealed record MachineLayerDto(string Label, double Gw, double SharePercent, double KvActual, double KvDevPercent, int BusCount);

public sealed record MachineDto(
    // rotation
    double Rpm, double FrequencyHz, double FreqTrendHzPerS, double InertiaGvaS, double SpinEnergyMwh,
    double SyncGw, double ConverterGw, double SyncSharePercent, double Co2TPerH, IReadOnlyList<double> FreqTrace,
    // torque story
    double TorqueCmdMnm, double TorqueActMnm, double TorqueDivMnm, double SurplusMw, double StressPercent, string DivNote,
    // banked divergence + BESS
    double AccumHzS, double TimeErrorSeconds, bool Fast, double RegenMwh, double RegenMoneyGbp,
    double BessMw, string BessMode, double BessSocPercent, double BessMovedMwh,
    // electrical: voltage + reactive
    double AvgVoltagePu, double VoltDevPercent, double MinVoltagePu, double MaxVoltagePu, IReadOnlyList<double> VoltTrace,
    double QProducedMvar, double QAbsorbedMvar, double QNetMvar,
    // streams + shaft layers + taps
    IReadOnlyList<MachineStreamDto> FuelIn, IReadOnlyList<MachineLayerDto> Layers,
    double TapAvgOffNomPercent, int TapCount, int TapsAtLimit,
    // energy
    double GenerationMw, double DemandMw, double LossMw,
    // N054 (October 2026): whether the solve's tap changer loop finished, and the taps that ran out of range with their
    // bus still out of band (SimFrame.TapsSettled / TapsAtLimit). Defaults: settled, none, so every other builder
    // (the raw-feed projection) reads as before.
    bool TapsSettled = true, int TapsAtLimitOutOfBand = 0);

// ASSETS is navigational: its tab reads Topology, Frame and AssetGraph directly, so it has no per-tick DTO of its own.
// (R099: the unused AssetsDto(int Count) placeholder that used to sit here was deleted.)

// SPEED DOMAINS - experimental distributed-inertia decomposition (mirrors the WPF tab).
public sealed record DomainRowDto(
    int Id, string Name, string Speed, string Kind, int Machines,
    double InertiaGvaS, double GvadGva, double BarFrac, double InertiaBarFrac,
    bool Dominant, bool Synchronous);

// StatusText is the WPF tab's one-line status sentence (R099 parity): see TabProjections.DomainsStatusText.
public sealed record DomainsDto(
    int DomainCount, double DeltaGvaPercent, bool HasDominant, string DominantText,
    string GapText, IReadOnlyList<DomainRowDto> Domains, string StatusText = "");
