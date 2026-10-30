using GridSim.Core.Model;

namespace GridSim.Core.Dynamics;

/// <summary>
/// The network after one generator trips: the same model without that unit. N158 (October 2026, H hygiene stream of
/// the inertia re-appraisal; the remainder of N094): the CLI <c>trip</c> verb built this copy twice by hand (once for the
/// asset-dynamics run, once for the distributed-inertia run), listing the properties itself. Both copies took the
/// in-service list (<see cref="GridModel.Generators"/>) as the whole fleet, so every out-of-service unit vanished from
/// the copy (<see cref="GridModel.AllGenerators"/>), and the distributed copy also dropped the asset bundle and the
/// protections. This one helper goes through <see cref="GridModel.CopyWith"/>, which carries every property and keeps
/// the out-of-service units.
///
/// <para>Bit-identity: the copy's in-service list is exactly the old one (the original in-service units minus the
/// tripped one, in the same order; <see cref="GridModel.CopyWith"/> returns that very list when the model has
/// out-of-service units, and an equal list otherwise), and every engine reads only that list. The base, buses,
/// branches, OLTCs, boundaries, frequency and name are the same objects as before. The only differences are the
/// out-of-service units now kept in <see cref="GridModel.AllGenerators"/> (no engine reads it) and, on the distributed
/// path, the asset bundle and protections now carried (the distributed model reads neither; the CLI refuses
/// <c>--asset-dynamics</c> together with <c>--inertia-model distributed</c>). So no trip result moves.</para>
/// </summary>
public static class PostTripModel
{
    /// <summary>A copy of <paramref name="model"/> without <paramref name="tripped"/> (matched by reference, as the CLI
    /// picked it from <c>model.Generators</c>). Fails loud if the unit is not one of the model's in-service generators:
    /// a trip of a unit the model does not run would silently answer a different question.</summary>
    public static GridModel Without(GridModel model, Generator tripped)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(tripped);
        bool found = false;
        var remaining = new List<Generator>(model.Generators.Count);
        foreach (Generator g in model.Generators)
        {
            if (ReferenceEquals(g, tripped)) { found = true; continue; }
            remaining.Add(g);
        }
        if (!found)
            throw new ArgumentException($"The tripped unit ({tripped.Name ?? $"generator @ bus {tripped.Bus}"}) is not one of "
                + $"{model.Name}'s in-service generators.", nameof(tripped));
        return model.CopyWith(generators: remaining);
    }
}
