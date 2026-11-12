namespace GridSim.Cli;

// ---------------------------------------------------------------------------------------------------------------------
// GR-195 (November 2026): a warning when a global switch is given to a verb that does not read it.
//
// The global switches (manual ch. 19, "Global switches") are parsed and taken off the argument list before any verb
// runs, so the per-verb flag gate (VerbFlags) accepts them on every verb. Most are read by a few verbs only; on any other
// verb they used to be accepted and silently ignored ("gridsim pf case9 --once" ran pf as if --once were not there).
// The owner's decision is a warning, not a refusal: the run goes on, and stderr says the switch had no effect.
//
// The table below lists, for each switch the warning covers, the verbs whose code reads it (found from Program.cs: the
// variables each switch sets and the verbs that use them). Switches a verb refuses outright (--asset-dynamics and the
// forming switches on the event verbs and fit-frequency; --inertia-model on fit-frequency) and the --inertia-model
// distributed warning the event verbs already print are left to those checks, so a run never gets both. Not covered,
// and why: --cpu / --gpu (the backend is shared process state that many verbs reach through the solver routing),
// --frequency-hz, --psse-revision, --seq-data, --tap-control, --shunt-control and --allow-unmodelled (each already warns
// when the run loads no file it applies to), the --audit-* switches (refused on the verbs that do not take them) and
// --export-tight (refused without --enableExport). The no-verb run (a case first) reads --once, --ticks, --bfs and the
// export switches itself and is not checked here.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>GR-195: the warnings for global switches given to a verb that does not read them.</summary>
internal static class GlobalSwitchUse
{
    private static readonly string[] EventVerbs = { "replay-event", "scenario", "h0-study", "fleet", "evidence" };

    /// <summary>A switch, the verbs that read it, and the verbs that deal with it themselves (refuse it, or already
    /// warn), which get no warning from here.</summary>
    private sealed record Rule(string Switch, string[] Readers, string[] HandledBy);

    private static readonly Rule[] Rules =
    {
        new("--inertia-model", new[] { "trip", "transient" }, EventVerbs.Append("fit-frequency").ToArray()),
        new("--asset-dynamics", new[] { "trip", "transient" }, EventVerbs.Append("fit-frequency").ToArray()),
        new("--forming", new[] { "trip", "transient", "scenario", "fleet" }, new[] { "replay-event", "h0-study", "evidence", "fit-frequency" }),
        new("--forming-inertia", new[] { "trip", "transient", "scenario", "fleet" }, new[] { "replay-event", "h0-study", "evidence", "fit-frequency" }),
        new("--forming-damping", new[] { "trip", "transient", "scenario", "fleet" }, new[] { "replay-event", "h0-study", "evidence", "fit-frequency" }),
        new("--once", Array.Empty<string>(), Array.Empty<string>()),
        new("--ticks", new[] { "interpose" }, Array.Empty<string>()),
        new("--bfs", new[] { "harmonics", "qsts", "hosting" }, Array.Empty<string>()),
        new("--enableExport", new[] { "harmonics", "impedance-stability" }, Array.Empty<string>()),
        new("--export-jacobian", Array.Empty<string>(), Array.Empty<string>()),
    };

    /// <summary>The switches this check covers, in a fixed order.</summary>
    internal static IReadOnlyList<string> CoveredSwitches { get; } = Rules.Select(r => r.Switch).ToArray();

    /// <summary>The verbs that read <paramref name="globalSwitch"/> (empty: none, only the no-verb run), or null when the
    /// switch is not covered.</summary>
    internal static IReadOnlyList<string>? ReadersOf(string globalSwitch) =>
        Rules.FirstOrDefault(r => r.Switch == globalSwitch)?.Readers;

    /// <summary>
    /// One stderr warning line for each switch in <paramref name="given"/> (switch names, without values) that
    /// <paramref name="verb"/> does not read, in the order of the table. A switch not in the table, and a verb that deals
    /// with the switch itself, give none.
    /// </summary>
    internal static IReadOnlyList<string> Warnings(string verb, IReadOnlyCollection<string> given)
    {
        ArgumentNullException.ThrowIfNull(verb);
        ArgumentNullException.ThrowIfNull(given);
        string v = verb.Trim().ToLowerInvariant();
        var givenSet = new HashSet<string>(given, StringComparer.Ordinal);
        var lines = new List<string>();
        foreach (Rule rule in Rules)
        {
            if (!givenSet.Contains(rule.Switch)) continue;
            if (rule.Readers.Contains(v) || rule.HandledBy.Contains(v)) continue;
            // The dynamics switches are not read by the no-verb case run; the others are.
            bool caseRunReads = rule.Switch is not ("--inertia-model" or "--asset-dynamics" or "--forming" or "--forming-inertia" or "--forming-damping");
            string where = rule.Readers.Length == 0
                ? "only the case run with no verb reads it"
                : caseRunReads
                    ? $"it applies to {string.Join(", ", rule.Readers)} (and the case run with no verb)"
                    : $"it applies to {string.Join(", ", rule.Readers)}";
            lines.Add($"warning: {v} does not read {rule.Switch}, so it has no effect on this run ({where}).");
        }
        return lines;
    }
}
