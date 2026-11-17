namespace GridSim.Core.IO.Psse.Topology;

public enum PsseBusTransitionKind
{
    /// <summary>The same nodes form the same model bus before and after.</summary>
    Unchanged,
    /// <summary>The nodes of one previous bus now form more than one bus (or part of them is de-energised).</summary>
    Split,
    /// <summary>Nodes of more than one previous bus now form one bus.</summary>
    Merged,
    /// <summary>The node set is the same but the model bus id differs.</summary>
    Renumbered,
    /// <summary>The previous bus has no energised node afterwards.</summary>
    DeEnergised,
    /// <summary>The bus has no energised node before and is energised afterwards.</summary>
    Energised,
}

/// <summary>How one model bus of the earlier state maps onto the later state, by connectivity node.</summary>
public sealed record PsseBusTransition(int? BeforeBusId, IReadOnlyList<int> AfterBusIds, IReadOnlyList<PsseNodeKey> Nodes,
    PsseBusTransitionKind Kind);

/// <summary>How one native terminal row moves between the two states.</summary>
public sealed record PsseTerminalTransition(PsseTopologyTerminal Terminal, int? BeforeBusId, PsseTerminalModelState BeforeState,
    int? AfterBusId, PsseTerminalModelState AfterState);

/// <summary>The old and new connectivity groups and their mapping to the solved network for one switch action.</summary>
public sealed record PsseTopologyTransitionResult(
    IReadOnlyList<PsseSwitchKey> ChangedSwitches,
    IReadOnlyList<PsseBusTransition> Buses,
    IReadOnlyList<PsseTerminalTransition> Terminals);

/// <summary>
/// Compares two projections of the same hash-bound case. Each state is rebuilt from the native graph and its own full
/// switch overlay (never by applying a second action to an already reduced bus set); this only reads both results.
/// </summary>
public static class PsseTopologyTransition
{
    public static PsseTopologyTransitionResult Compare(PsseTopologyModelResult before, PsseTopologyModelResult after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (!string.Equals(before.SourceSha256, after.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A topology transition compares two projections of the same source case.");
        if (!before.SwitchStates.Keys.ToHashSet().SetEquals(after.SwitchStates.Keys))
            throw new InvalidDataException("Both projections must state every switch of the case.");

        PsseSwitchKey[] changed = before.SwitchStates.Where(kv => after.SwitchStates[kv.Key] != kv.Value).Select(kv => kv.Key)
            .OrderBy(k => k.Substation).ThenBy(k => k.FromNode).ThenBy(k => k.ToNode).ThenBy(k => k.CircuitId, StringComparer.Ordinal)
            .ToArray();

        var transitions = new List<PsseBusTransition>();
        PsseNodeKey[] allNodes = before.NodeToModelBus.Keys.OrderBy(n => n.Substation).ThenBy(n => n.Node).ToArray();
        foreach (IGrouping<int?, PsseNodeKey> group in allNodes.GroupBy(n => before.NodeToModelBus[n])
                     .OrderBy(g => g.Key is null ? 1 : 0).ThenBy(g => g.Min(n => (n.Substation, n.Node))))
        {
            PsseNodeKey[] nodes = group.ToArray();
            int[] afterIds = nodes.Select(n => after.NodeToModelBus.TryGetValue(n, out int? id) ? id : null)
                .Where(id => id is not null).Select(id => id!.Value).Distinct().Order().ToArray();
            bool someDead = nodes.Any(n => !after.NodeToModelBus.TryGetValue(n, out int? id) || id is null);
            PsseBusTransitionKind kind;
            if (group.Key is null)
                kind = afterIds.Length == 0 ? PsseBusTransitionKind.Unchanged : PsseBusTransitionKind.Energised;
            else if (afterIds.Length == 0) kind = PsseBusTransitionKind.DeEnergised;
            else if (afterIds.Length > 1 || someDead) kind = PsseBusTransitionKind.Split;
            else
            {
                int afterId = afterIds[0];
                bool sameSet = after.NodeToModelBus.Where(kv => kv.Value == afterId).Select(kv => kv.Key).ToHashSet().SetEquals(nodes);
                kind = !sameSet ? PsseBusTransitionKind.Merged
                    : afterId == group.Key ? PsseBusTransitionKind.Unchanged : PsseBusTransitionKind.Renumbered;
            }
            transitions.Add(new PsseBusTransition(group.Key, Array.AsReadOnly(afterIds), Array.AsReadOnly(nodes), kind));
        }

        var afterByLine = after.Terminals.ToDictionary(t => t.Terminal.SourceLine);
        PsseTerminalTransition[] terminals = before.Terminals.Select(t =>
        {
            PsseTerminalPlacement a = afterByLine[t.Terminal.SourceLine];
            return new PsseTerminalTransition(t.Terminal, t.ModelBusId, t.State, a.ModelBusId, a.State);
        }).ToArray();
        return new PsseTopologyTransitionResult(Array.AsReadOnly(changed), Array.AsReadOnly(transitions.ToArray()),
            Array.AsReadOnly(terminals));
    }
}
