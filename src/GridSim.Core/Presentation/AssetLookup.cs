using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Presentation;

/// <summary>
/// Index-based identity for generators and branches in the asset views (WPF asset explorer, GENERATION panel).
/// <para>
/// Names are NOT unique identifiers: gb-full has two "Auchencrosh (interconnector CCT) interconnector" links (125
/// and 220 MW), dozens of "- chp" units and 58 duplicated branch names. Views that keyed rows or detail pages by
/// name collapsed namesakes into one row (last writer wins), always opened the first namesake, and read the first
/// namesake's branch flow (R055). The model index is unique and is what every solved result aligns to
/// (<see cref="PowerFlowResult.LineFlows"/> is index-aligned with <see cref="GridModel.Branches"/>), so views keep
/// the index as the identity and confirm it with the element's own fields before trusting it.
/// </para>
/// </summary>
public static class AssetLookup
{
    /// <summary>
    /// The generator at <paramref name="index"/> when it is still the same unit (same bus and name); otherwise the
    /// one unit in the model with that bus and name (the model was reordered); otherwise <c>null</c> (the unit is
    /// gone, or its bus-and-name is ambiguous). A <c>null</c> must be shown as "not in this model", never papered
    /// over with a different unit.
    /// </summary>
    public static (Generator Unit, int Index)? FindGenerator(GridModel model, int index, int bus, string? name)
    {
        ArgumentNullException.ThrowIfNull(model);
        IReadOnlyList<Generator> gens = model.Generators;
        if ((uint)index < (uint)gens.Count && Same(gens[index], bus, name)) return (gens[index], index);

        int found = -1;
        for (int i = 0; i < gens.Count; i++)
        {
            if (!Same(gens[i], bus, name)) continue;
            if (found >= 0) return null;   // two units share this bus and name: refuse to guess
            found = i;
        }
        return found >= 0 ? (gens[found], found) : null;

        static bool Same(Generator g, int bus, string? name) => g.Bus == bus && string.Equals(g.Name, name, StringComparison.Ordinal);
    }

    /// <summary>
    /// The branch at <paramref name="index"/> when it is still the same branch (same ends and name); otherwise the
    /// one branch with those ends and name; otherwise <c>null</c>. Same rules as <see cref="FindGenerator"/>.
    /// </summary>
    public static (Branch Branch, int Index)? FindBranch(GridModel model, int index, int from, int to, string? name)
    {
        ArgumentNullException.ThrowIfNull(model);
        IReadOnlyList<Branch> branches = model.Branches;
        if ((uint)index < (uint)branches.Count && Same(branches[index], from, to, name)) return (branches[index], index);

        int found = -1;
        for (int i = 0; i < branches.Count; i++)
        {
            if (!Same(branches[i], from, to, name)) continue;
            if (found >= 0) return null;
            found = i;
        }
        return found >= 0 ? (branches[found], found) : null;

        static bool Same(Branch b, int from, int to, string? name) =>
            b.From == from && b.To == to && string.Equals(b.Name, name, StringComparison.Ordinal);
    }

    /// <summary>
    /// The solved flow of the branch at <paramref name="branchIndex"/>, read by index (LineFlows is index-aligned
    /// with Branches, out-of-service branches included), or <c>null</c> when the result has no entry for it.
    /// </summary>
    public static LineFlow? FlowAt(PowerFlowResult result, int branchIndex)
    {
        ArgumentNullException.ThrowIfNull(result);
        return (uint)branchIndex < (uint)result.LineFlows.Count ? result.LineFlows[branchIndex] : null;
    }
}
