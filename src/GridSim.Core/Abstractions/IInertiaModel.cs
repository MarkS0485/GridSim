using System.Collections.Generic;
using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Model;

namespace GridSim.Core.Abstractions;

/// <summary>
/// The swappable frequency/inertia model seam, mirroring <see cref="IPowerFlowSolver"/>.
/// An implementation turns a grid model + a demand baseline + power-step events into a
/// system <see cref="FrequencyResponse"/>. The lumped scalar model and the experimental
/// distributed model both implement this, selected at runtime via
/// <see cref="Dynamics.InertiaModelSelector"/>.
/// </summary>
public interface IInertiaModel
{
    /// <summary>
    /// Simulate the system frequency excursion for <paramref name="model"/> after the given
    /// <paramref name="events"/>. Distributed implementations return the COI-collapsed response
    /// here (so every existing scalar consumer keeps working) and expose the spatial detail via
    /// <see cref="LastDistributedResult"/>.
    /// </summary>
    FrequencyResponse Simulate(
        GridModel model, double baseDemandMw, IEnumerable<FrequencyEvent> events, DynamicsConfig? config = null);

    /// <summary>
    /// The model's centre-of-inertia aggregate (GVA.s) for <paramref name="model"/> - the number
    /// every scalar readout (RP1, MECHANICAL, the bridge HUD) expects. Equals
    /// <see cref="GridModel.SystemInertiaGvaS"/> for the lumped model, and the distributed model's
    /// inertia-weighted projection onto that same aggregate.
    /// </summary>
    double CoiInertiaGvaS(GridModel model);

    /// <summary>
    /// The spatial detail from the most recent <see cref="Simulate"/> call (per-domain traces,
    /// per-bus RoCoF, dominance, propagation), or <c>null</c> for the lumped model.
    /// </summary>
    DistributedResult? LastDistributedResult { get; }
}
