namespace GridSim.Core.Model.Substations;

/// <summary>Open or close one switch.</summary>
public enum SwitchAction { Open, Close }

/// <summary>One switching action, as ordered by the operator.</summary>
public sealed record SwitchingAction(string SwitchId, SwitchAction Action, string? Note = null);

/// <summary>
/// One recorded step of a scenario. Step 0 is the starting state (no action). Every later step is projected afresh from
/// the NATIVE graph and the cumulative switch states, never from the buses of the step before, so opening and reclosing
/// a switch returns exactly the groups and bus ids it started with.
/// </summary>
/// <param name="Changed">False when the action asked for the state the switch was already in (recorded, not refused).</param>
/// <param name="GroupsRemoved">The node groups (joined with "+") of the previous step that no longer exist.</param>
/// <param name="GroupsAdded">The node groups of this step that did not exist before.</param>
public sealed record SwitchingStep(int Index, SwitchingAction? Action, bool Changed,
    IReadOnlyDictionary<string, bool> StatesAfter, SubstationProjectionResult Projection,
    IReadOnlyList<string> GroupsRemoved, IReadOnlyList<string> GroupsAdded);

/// <summary>An ordered list of switching actions applied to a starting state, each step recorded.</summary>
public static class SwitchingScenario
{
    /// <summary>Run <paramref name="actions"/> in order from <paramref name="initial"/>; returns step 0 plus one step per action.</summary>
    public static IReadOnlyList<SwitchingStep> Run(SubstationModel model, IReadOnlyDictionary<string, bool> initial,
        IReadOnlyList<SwitchingAction> actions, SubstationProjectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(actions);
        var known = new HashSet<string>(model.AllSwitches.Select(s => s.Id), StringComparer.Ordinal);
        var steps = new List<SwitchingStep>();
        var cumulative = new Dictionary<string, bool>(initial, StringComparer.Ordinal);
        SubstationProjectionResult first = SubstationProjection.Project(model, cumulative, options);
        steps.Add(new SwitchingStep(0, null, false, first.SwitchStates, first, Array.Empty<string>(),
            first.Groups.Select(Key).ToArray()));
        SubstationProjectionResult previous = first;
        for (int i = 0; i < actions.Count; i++)
        {
            SwitchingAction a = actions[i];
            if (!known.Contains(a.SwitchId))
                throw new SubstationTopologyException(new[] { $"switching action {i + 1} names unknown switch '{a.SwitchId}'." });
            bool wantOpen = a.Action == SwitchAction.Open;
            // The current state of the switch: stated, or (under the declared policy) its normal state.
            bool current = previous.SwitchStates[a.SwitchId];
            cumulative[a.SwitchId] = wantOpen;
            SubstationProjectionResult now = SubstationProjection.Project(model, cumulative, options);
            var before = previous.Groups.Select(Key).ToHashSet(StringComparer.Ordinal);
            var after = now.Groups.Select(Key).ToHashSet(StringComparer.Ordinal);
            steps.Add(new SwitchingStep(i + 1, a, current != wantOpen, now.SwitchStates, now, previous.Groups.Select(Key).Where(k => !after.Contains(k)).ToArray(),
                now.Groups.Select(Key).Where(k => !before.Contains(k)).ToArray()));
            previous = now;
        }
        return steps;
    }

    private static string Key(NodeGroup g) => string.Join("+", g.Nodes);
}
