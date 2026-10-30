using System.Collections.Generic;
using GridSim.Core.Abstractions;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Model;

namespace GridSim.Core.Dynamics;

/// <summary>
/// The default inertia model: the existing single lumped centre-of-inertia scalar. A thin
/// adapter over <see cref="FrequencyDynamics.Simulate"/> - the old path is literally the same
/// code, only routed through the <see cref="IInertiaModel"/> seam - so selecting
/// <see cref="InertiaModel.LumpedScalar"/> is exactly today's behaviour.
/// </summary>
public sealed class LumpedScalarInertiaModel : IInertiaModel
{
    /// <summary>The lumped model has no spatial detail.</summary>
    public DistributedResult? LastDistributedResult => null;

    /// <inheritdoc/>
    public double CoiInertiaGvaS(GridModel model) => model.SystemInertiaGvaS;

    /// <inheritdoc/>
    public FrequencyResponse Simulate(
        GridModel model, double baseDemandMw, IEnumerable<FrequencyEvent> events, DynamicsConfig? config = null)
        // GVA.s -> MW.s, exactly as LiveSim builds the scalar E today. N275 (October 2026, stream B1): at the model's own
        // nominal frequency (SwingKernel.ConfigForModel: no config gives the defaults at it, a config at another nominal is
        // refused); for a 50 Hz model with no config the call is the one it always was, bit for bit.
        => FrequencyDynamics.Simulate(model.SystemInertiaGvaS * 1000.0, baseDemandMw, events,
            SwingKernel.ConfigForModel(model, config, "LumpedScalarInertiaModel.Simulate"));
}
