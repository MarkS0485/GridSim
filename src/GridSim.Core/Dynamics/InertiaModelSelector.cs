using GridSim.Core.Abstractions;
using GridSim.Core.Dynamics.Distributed;

namespace GridSim.Core.Dynamics;

/// <summary>
/// Single source of truth for choosing an inertia/frequency model, mirroring
/// <see cref="PowerFlow.SolverSelector"/>. The CLI, WPF and Web surfaces all route through here
/// so a given run always uses the selected model. Both models implement
/// <see cref="IInertiaModel"/>, so either drops into any consumer.
/// </summary>
public static class InertiaModelSelector
{
    /// <summary>
    /// Resolve the requested model. There is no shape-based auto-selection (unlike the power-flow
    /// solver): the inertia model is an explicit user choice, defaulting to
    /// <see cref="InertiaModel.LumpedScalar"/>.
    /// </summary>
    public static InertiaModel Resolve(InertiaModel kind) => kind;

    /// <summary>Build the model for <paramref name="kind"/>, with a human-readable label for the UI.</summary>
    public static (IInertiaModel model, InertiaModel resolved, string label) Create(InertiaModel kind)
    {
        IInertiaModel model = kind switch
        {
            InertiaModel.Distributed => new DistributedInertiaModel(),
            _ => new LumpedScalarInertiaModel(),
        };
        return (model, kind, Label(kind));
    }

    /// <summary>The model's display name.</summary>
    public static string Label(InertiaModel kind) => kind switch
    {
        InertiaModel.Distributed => "Distributed (multi-domain)",
        _ => "Lumped scalar (COI)",
    };
}
