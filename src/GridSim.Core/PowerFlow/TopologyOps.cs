using GridSim.Core.Model;

namespace GridSim.Core.PowerFlow;

/// <summary>
/// Applies a set of recloser-opened branches to a model (marking them out of service) and runs the island
/// pre-pass so the result is solvable. Empty set ⇒ the original model unchanged (bit-identical solve).
/// The <see cref="IslandReport"/> carries the frequency-domain consequence (net load/gen lost to blackout)
/// the caller feeds into the swing model.
/// </summary>
public static class TopologyOps
{
    public static (GridModel Model, IslandReport Report) Apply(GridModel model, IReadOnlySet<string> openBranches)
    {
        if (openBranches.Count == 0)
            return IslandPartition.Prepare(model);   // no-op partition (single connected component)

        var branches = model.Branches
            .Select(b => b.Name is { } nm && openBranches.Contains(nm) ? b with { InService = false } : b)
            .ToList();

        // N094 (October 2026): through GridModel.CopyWith, which carries the out-of-service generators (F16) and every
        // other model property. The hand-built copy kept only the in-service list, so a writer given this model lost
        // the out-of-service units. Engines read Generators, which is the same list as before, so no number changes.
        GridModel opened = model.CopyWith(branches: branches);
        return IslandPartition.Prepare(opened);
    }
}
