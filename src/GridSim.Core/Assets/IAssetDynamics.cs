namespace GridSim.Core.Assets;

/// <summary>
/// One locally-sampled observation handed to an asset controller for a single step. The frequency,
/// RoCoF and phase are what the device measures <b>at its own bus</b> - which for a PLL inverter is
/// emphatically NOT the centre-of-inertia value in the general (distributed) case. (Today every path is lumped:
/// <c>AssetDynamicsFleet.StepCoi</c> hands every asset the centre-of-inertia sample; R074 removed the unused
/// per-domain sampler.)
/// </summary>
public readonly record struct AssetStepContext(
    double LocalHz,
    double LocalRocofHzPerS,
    double LocalPhaseDeg,
    double DtSeconds,
    double ClockSeconds,
    double LocalVoltagePu = 1.0,
    double LoadingFraction = 0.0)   // branch loading / rating - drives passive-plant thermal + OLTC dynamics
{
    /// <summary>
    /// D-DOC-038 (October 2026, docs-session finding): the voltage a path that solves NO network hands its assets. The
    /// lumped centre-of-inertia paths (trip, the batch asset solve, the distributed sampler path) used to hand every
    /// asset 1.0 pu and a loading of 0, as if those were measurements, so a uvTripPu above 1.0 (or an lvrtVoltagePu above
    /// 1.0, an ovTripPu below 1.0, a negative overloadTripFraction) operated at once on a voltage nobody had computed,
    /// and every sensible setting could never operate there. NaN is "not known": every comparison with NaN is false, so
    /// no voltage or loading stage operates on it (and the caller reports that those stages were not evaluated). With
    /// the bundled settings (LVRT 0.5 / 0.8 pu) a stage never operated at 1.0 pu either, so no bundled number moves.
    /// </summary>
    public const double NoNetworkVoltagePu = double.NaN;

    /// <summary>D-DOC-038: the loading fraction a path that solves no network hands its assets ("not known").</summary>
    public const double NoNetworkLoadingFraction = double.NaN;
}

/// <summary>
/// What an ACTIVE asset does to the swing this substep: the active-power it injects (a term in
/// <c>pImbalance</c>), and - if its protection fired this step - the rising-edge trip signals the
/// fleet folds into the deficit exactly as <see cref="Dynamics.RocofScreening"/> does (lost infeed
/// becomes deficit; a synchronous trip would also remove <see cref="TrippedInertiaMwS"/> from E).
/// </summary>
public readonly record struct AssetActuation(
    double ActivePowerMw,
    bool TrippedNow,
    double TrippedInfeedMw,
    double TrippedInertiaMwS);

/// <summary>Base seam: one stateful instance per bound asset. Never a swing-power term on its own.</summary>
public interface IAssetDynamics
{
    string ClassName { get; }
    AssetKind Kind { get; }
    int Bus { get; }
    bool Tripped { get; }
    void Reset();
}

/// <summary>Active devices (inverters): contribute a swing-power term, sampled on the fast substep clock.</summary>
public interface IActiveAssetDynamics : IAssetDynamics
{
    /// <summary>Advance the controller one step against its locally-sampled frequency and return its actuation.</summary>
    AssetActuation Update(in AssetStepContext ctx);

    /// <summary>The current injected power (MW; 0 when tripped) - for the dashboard rollup.</summary>
    double CurrentMw { get; }

    /// <summary>State of charge as a fraction (NaN when the asset does not model storage) - for the dashboard.</summary>
    double SocFraction { get; }

    /// <summary>
    /// D-DOC-038 (October 2026): whether the asset has a trip stage that reads the NETWORK state (a voltage stage: LVRT,
    /// under- or over-voltage; or a loading stage: overload). On a path that solves no network those stages cannot be
    /// evaluated (see <see cref="AssetStepContext.NoNetworkVoltagePu"/>), and the caller says so. Default false.
    /// </summary>
    bool UsesNetworkState => false;

    /// <summary>
    /// R043 (October 2026 review, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04): tell the asset the output
    /// (MW) its generator is dispatched to NOW, so a trip books the infeed actually lost. The fleet is built once from the
    /// base model, while the live loop rescales every non-slack dispatch to the day's load factor each tick
    /// (<c>GridScenario.AtLoad</c>): a Loss-of-Mains trip at 04:00 (factor 0.6) used to remove the base-case output, 1 / 0.6
    /// = 1.67 times what the unit was producing. Default: nothing (a device that books no dispatch, such as a placed
    /// protection device, whose capacity is its own setting). An asset that has already tripped keeps the loss it booked.
    /// </summary>
    void SetDispatch(double pgMw) { }
}

/// <summary>
/// The slow-tick state a passive plant asset emits: an optional overriding tap ratio, a dynamic
/// rating derate (0 = full rating, 1 = fully derated), and a normalised thermal state. NEVER a
/// swing-power term - passive plant shapes the network/loading, not the imbalance.
/// </summary>
public readonly record struct PlantStateUpdate(
    double? TapRatio,
    double RatingDerateFraction,
    double ThermalStatePu);

/// <summary>Passive plant (transformers, cables): emit tap/rating/thermal state on the steady-state tick.</summary>
public interface IPassiveAssetDynamics : IAssetDynamics
{
    PlantStateUpdate Advance(in AssetStepContext ctx);
}
