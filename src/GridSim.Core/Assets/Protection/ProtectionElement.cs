namespace GridSim.Core.Assets.Protection;

/// <summary>The locally-sampled quantity a protection stage watches. All are carried by <see cref="AssetStepContext"/>.</summary>
public enum MonitoredQuantity
{
    Frequency,        // LocalHz
    Rocof,            // |LocalRocofHzPerS| * uplift
    Voltage,          // LocalVoltagePu (Phase 2: fed from the solved model)
    LoadingFraction,  // LoadingFraction (Phase 2)
    PhaseShift,       // phase-window delta (vector-shift proxy)
}

/// <summary>Whether the stage operates when the quantity goes above or below its threshold.</summary>
public enum ThresholdSense { Above, Below }

/// <summary>
/// What operating does. Frequency/injection-domain actions (Tier A) map onto the two channels
/// <see cref="AssetActuation"/> already exposes; topology actions (Tier B, Phase 3) request a branch
/// open/reclose and are inert in the pure lumped model until the topology layer honours them.
/// </summary>
public enum ProtectionAction
{
    TripInfeed,     // -> AssetActuation.TrippedInfeedMw (generator/inverter LoM): remove the protected infeed
    ShedDemand,     // -> +ActivePowerMw step (LFDD): load removed reduces the deficit
    InjectStep,     // -> signed ActivePowerMw step (injection proxy)
    OpenBranch,     // -> topology request (Phase 3): open TargetBranch and re-solve
    /// <summary>Reserved, NOT implemented: refused by <see cref="ProtectionEvaluator"/> (R073). Reclosing is done by
    /// an <see cref="OpenBranch"/> stage's finite <see cref="ProtectionStage.AutoRestoreDelayS"/>.</summary>
    RestoreBranch,
}

/// <summary>
/// One definite-time protection stage: a monitored quantity crossing a threshold (optionally held for a
/// dwell/definite-time window) triggers an action. This is the generalisation of the inverter trip block:
/// RoCoF-LoM is <c>{Rocof, Above, rocofTripHzPerS, rocofTripConfirmS, TripInfeed, uplift}</c>; LFDD is
/// <c>{Frequency, Below, 48.8, dwell, ShedDemand, blockMw}</c>. A finite <see cref="AutoRestoreDelayS"/>
/// makes the operation auto-reset (reclose); the default (+inf) latches.
/// </summary>
public readonly record struct ProtectionStage(
    MonitoredQuantity Quantity,
    ThresholdSense Sense,
    double Threshold,
    double DwellSeconds,
    ProtectionAction Action,
    double ActionMw = 0.0,            // shed/inject/trip magnitude; 0 => use the bound instance's CapacityMw
    double LocalUpliftFactor = 1.0,   // sub-cycle local>COI (RoCoF), as GridFollowingInverter._localUplift
    double AutoRestoreDelayS = double.PositiveInfinity,   // reclose delay; +inf = latching
    // Block operation until clock >= this (avoids a startup transient). Set only by code that builds stages
    // directly: no asset-class parameter populates it, and an 'armDelayS' key in a class is refused at load as
    // unknown (R073), so an author cannot believe it took effect.
    double ArmDelayS = 0.0,
    string? TargetBranch = null);     // for OpenBranch (Phase 3)
