namespace GridSim.Core.Dynamics;

/// <summary>
/// Which inertia / frequency model to run. Mirrors the <see cref="PowerFlow.SolverKind"/>
/// pattern: a single enum selected at runtime and resolved through
/// <see cref="InertiaModelSelector"/>. <see cref="LumpedScalar"/> is the default so existing
/// behaviour is unchanged.
/// </summary>
public enum InertiaModel
{
    /// <summary>
    /// The single lumped centre-of-inertia (COI) scalar swing model - system inertia is
    /// one number (E = sum H*MBase) and one swing equation. The validated default.
    /// </summary>
    LumpedScalar,

    /// <summary>
    /// Experimental spatially-resolved, multi-domain ("speed domain") inertia model:
    /// per-domain masses coupled across the network with propagation delay. Additive and
    /// opt-in; collapses exactly to <see cref="LumpedScalar"/> in its degenerate limit.
    /// </summary>
    Distributed,
}
