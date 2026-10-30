using GridSim.Core.Dynamics;
using GridSim.Core.Events;
using GridSim.Core.Model;
using GridSim.Core.Replay;

namespace GridSim.Core.Scenarios;

/// <summary>
/// One point-in-time reconstructed grid plus the H=0 counterfactual sweep over it: how does the system
/// respond as REAL synchronous inertia is scaled toward zero (<see cref="SyncInertiaScales"/>) and the
/// converter fleet is progressively made grid-FORMING (<see cref="GfmFractions"/>)? The reconstructed
/// <see cref="BaseModel"/> must carry an asset-class library + bindings so a real converter fleet
/// exists to transform; <see cref="H0StudyEngine.Run"/> refuses to run until <see cref="Event"/>'s
/// no-device baseline reproduces the record (the same credibility gate the scenario sweep uses).
/// </summary>
public sealed record H0StudySpec(
    GridEvent Event,
    GridModel BaseModel,
    IReadOnlyList<double> SyncInertiaScales,
    IReadOnlyList<double> GfmFractions,
    IReadOnlyList<Corner> Corners,
    string GfmClass,
    string GflClass,
    DynamicsConfig BaseConfig,
    ReplayTolerance Tolerance)
{
    /// <summary>Asset class the default H0 sweep uses for its grid-forming fleet (R194).</summary>
    public const string DefaultGfmClass = "gfm-vsm-study-2024";

    /// <summary>Asset class the default H0 sweep uses for its grid-following fleet (R194).</summary>
    public const string DefaultGflClass = "gfl-wind-2024";

    /// <summary>
    /// The default sweep: the synchronous-inertia scale trajectory 1 -&gt; 0 (0 is a genuine H=0 grid),
    /// GFM fractions 0 -&gt; 1, and the best/worst declared-uncertainty corners. <see cref="BaseConfig"/>
    /// turns the authored per-asset fleet ON (required to reach the converter cascade + the forming
    /// path); <see cref="DynamicsConfig.Forming"/> stays None here - the engine composes the forming
    /// former per cell from the GFM fleet.
    /// </summary>
    public static H0StudySpec Default(GridEvent ev, GridModel model) => new(
        ev, model,
        new[] { 1.0, 0.5, 0.25, 0.0 },
        new[] { 0.0, 0.25, 0.5, 0.75, 1.0 },
        new[] { Corner.Best, Corner.Worst },
        DefaultGfmClass, DefaultGflClass,
        new DynamicsConfig { EnableAssetDynamics = true },
        ReplayTolerance.Default);
}
