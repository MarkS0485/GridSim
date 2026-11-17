namespace GridSim.Core.IO.Psse.Topology;

public enum PsseTerminalBindingStatus
{
    BoundToBranch,
    BoundToMachine,
    BoundToLoadRecord,
    BoundToShuntRecord,
    LegacyModelOmitted,
    UnsupportedEquipmentType,
    MissingSourceRecord,
    AmbiguousSourceRecord,
}

/// <summary>A substation terminal's identity binding to the incumbent parse result, with an explicit disposition.</summary>
public sealed record PsseTerminalBinding(PsseTopologyTerminal Terminal, PsseTerminalBindingStatus Status,
    int? BranchIndex, int? GeneratorIndex, string Detail)
{
    public PsseBranchKey? BranchKey { get; init; }
    public PsseMachineKey? MachineKey { get; init; }
    public int? LoadSourceIndex { get; init; }
    public PsseLoadKey? LoadKey { get; init; }
    public int? ShuntSourceIndex { get; init; }
    public PsseShuntKey? ShuntKey { get; init; }
}

/// <summary>
/// Binds native terminal identities to the existing PSS/E parser's stable source keys. It does not infer an identity
/// from endpoints alone, and reports records the legacy model omitted or this binding seam cannot yet resolve.
/// </summary>
public static class PsseTerminalIdentityBinder
{
    public static IReadOnlyList<PsseTerminalBinding> Bind(PsseSubstationTopology topology, PsseParseResult parsed)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(parsed);
        if (topology.Revision != parsed.Revision)
            throw new InvalidDataException("PSS/E topology and equipment identities must come from the same revision.");

        var duplicateLines = topology.Terminals.GroupBy(EndpointIdentity)
            .Where(group => group.Count() > 1).SelectMany(group => group.Select(t => t.SourceLine)).ToHashSet();
        var result = new List<PsseTerminalBinding>(topology.Terminals.Count);
        foreach (PsseTopologyTerminal terminal in topology.Terminals)
        {
            if (duplicateLines.Contains(terminal.SourceLine))
            {
                result.Add(new(terminal, PsseTerminalBindingStatus.AmbiguousSourceRecord, null, null,
                    $"Duplicate native terminal rows identify the same equipment end at bus {terminal.Bus}."));
                continue;
            }

            if (terminal.EquipmentType is "B" or "2")
            {
                string kind = terminal.EquipmentType == "B" ? "branch" : "transformer";
                (PsseBranchKey Key, int Index)[] liveIndexed = parsed.BranchKeys.Select((key, index) => (key, index))
                    .Where(x => IsTwoTerminalMatch(x.key, kind, terminal)).ToArray();
                if (liveIndexed.Length == 1)
                {
                    result.Add(new(terminal, PsseTerminalBindingStatus.BoundToBranch, liveIndexed[0].Index, null,
                        $"Matched {kind} {Format(liveIndexed[0].Key)}.") { BranchKey = liveIndexed[0].Key });
                    continue;
                }
                if (liveIndexed.Length > 1)
                {
                    result.Add(new(terminal, PsseTerminalBindingStatus.AmbiguousSourceRecord, null, null,
                        $"More than one active {kind} has the complete terminal key."));
                    continue;
                }
                PsseBranchKey[] skipped = parsed.SkippedBranchKeys.Where(key => IsTwoTerminalMatch(key, kind, terminal)).ToArray();
                if (skipped.Length == 1)
                    result.Add(new(terminal, PsseTerminalBindingStatus.LegacyModelOmitted, null, null,
                        $"Matched {kind} {Format(skipped[0])}, which the legacy electrical model omitted.") { BranchKey = skipped[0] });
                else if (skipped.Length > 1)
                    result.Add(new(terminal, PsseTerminalBindingStatus.AmbiguousSourceRecord, null, null,
                        $"More than one skipped {kind} has the complete terminal key."));
                else
                    result.Add(new(terminal, PsseTerminalBindingStatus.MissingSourceRecord, null, null,
                        $"No {kind} source record matches bus pair {terminal.Bus}-{terminal.ToBus}, circuit '{terminal.CircuitId}'."));
                continue;
            }

            if (terminal.EquipmentType == "3")
            {
                PsseBranchKey[] activeUnit = parsed.BranchKeys.Where(key => IsThreeWindingUnitMatch(key, terminal)).ToArray();
                int localWinding = activeUnit.Length == 0 ? 0 : terminal.Bus == activeUnit[0].FromBus ? 1 :
                    terminal.Bus == activeUnit[0].ToBus ? 2 : terminal.Bus == activeUnit[0].ThirdBus ? 3 : 0;
                PsseBranchKey[] windingMatches = activeUnit.Where(key => key.Winding == localWinding).ToArray();
                if (windingMatches.Length == 1)
                {
                    int branchIndex = -1;
                    for (int i = 0; i < parsed.BranchKeys.Count; i++)
                        if (parsed.BranchKeys[i] == windingMatches[0]) { branchIndex = i; break; }
                    result.Add(new(terminal, PsseTerminalBindingStatus.BoundToBranch, branchIndex, null,
                        $"Matched three-winding transformer {Format(windingMatches[0])}, winding {windingMatches[0].Winding}.")
                    {
                        BranchKey = windingMatches[0],
                    });
                    continue;
                }
                if (windingMatches.Length > 1)
                {
                    result.Add(new(terminal, PsseTerminalBindingStatus.AmbiguousSourceRecord, null, null,
                        "More than one active three-winding transformer winding has the complete terminal key."));
                    continue;
                }
                PsseBranchKey? skippedUnit = parsed.SkippedBranchKeys.FirstOrDefault(key => IsThreeWindingUnitMatch(key, terminal));
                if (skippedUnit is not null || activeUnit.Length > 0)
                {
                    PsseBranchKey key = skippedUnit ?? activeUnit.First();
                    result.Add(new(terminal, PsseTerminalBindingStatus.LegacyModelOmitted, null, null,
                        $"Matched three-winding transformer {Format(key)}, but this terminal winding is absent from the legacy electrical model.")
                    {
                        BranchKey = key,
                    });
                }
                else
                {
                    result.Add(new(terminal, PsseTerminalBindingStatus.MissingSourceRecord, null, null,
                        $"No three-winding transformer matches the terminal bus set and circuit '{terminal.CircuitId}'."));
                }
                continue;
            }

            if (terminal.EquipmentType == "M")
            {
                PsseMachineKey[] matches = parsed.MachineKeys.Where(key => key.Bus == terminal.Bus &&
                    string.Equals(key.Id.Trim(), terminal.EquipmentId?.Trim() ?? "", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1)
                {
                    result.Add(new(terminal, matches.Length == 0 ? PsseTerminalBindingStatus.MissingSourceRecord : PsseTerminalBindingStatus.AmbiguousSourceRecord,
                        null, null, matches.Length == 0 ? "No machine source record matches the bus and machine identifier." :
                        "More than one machine source record matches the bus and machine identifier."));
                    continue;
                }
                int index = -1;
                for (int i = 0; i < parsed.MachineKeys.Count; i++)
                    if (parsed.MachineKeys[i] == matches[0]) { index = i; break; }
                result.Add(new(terminal, PsseTerminalBindingStatus.BoundToMachine, null, index,
                    $"Matched machine at bus {matches[0].Bus}, identifier '{matches[0].Id}'.") { MachineKey = matches[0] });
                continue;
            }

            if (terminal.EquipmentType == "L")
            {
                (PsseLoadSource Source, int Index)[] matches = parsed.LoadSources.Select((source, index) => (source, index))
                    .Where(x => x.source.Key.Bus == terminal.Bus &&
                        string.Equals(x.source.Key.Id.Trim(), terminal.EquipmentId?.Trim() ?? "1", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matches.Length != 1)
                {
                    result.Add(new(terminal, matches.Length == 0 ? PsseTerminalBindingStatus.MissingSourceRecord : PsseTerminalBindingStatus.AmbiguousSourceRecord,
                        null, null, matches.Length == 0 ? "No load source record matches the bus and load identifier." :
                        "More than one load source record matches the bus and load identifier."));
                    continue;
                }
                PsseLoadSource load = matches[0].Source;
                bool modelled = load.InService && load.BusRetained;
                result.Add(new(terminal, modelled ? PsseTerminalBindingStatus.BoundToLoadRecord : PsseTerminalBindingStatus.LegacyModelOmitted,
                    null, null, modelled
                        ? $"Matched load at bus {load.Key.Bus}, identifier '{load.Key.Id}'. Its demand is aggregated into the legacy bus model."
                        : $"Matched load at bus {load.Key.Bus}, identifier '{load.Key.Id}', but it is out of service or its bus was omitted from the legacy model.")
                {
                    LoadSourceIndex = matches[0].Index,
                    LoadKey = load.Key,
                });
                continue;
            }

            if (terminal.EquipmentType is "F" or "S")
            {
                PsseShuntKind kind = terminal.EquipmentType == "F" ? PsseShuntKind.Fixed : PsseShuntKind.Switched;
                PsseShuntSource[] matches = parsed.ShuntSources.Where(source => source.Kind == kind &&
                    source.Key.Bus == terminal.Bus && (kind == PsseShuntKind.Switched ||
                    string.Equals(source.Key.Id.Trim(), terminal.EquipmentId?.Trim() ?? "1", StringComparison.OrdinalIgnoreCase))).ToArray();
                if (matches.Length != 1)
                {
                    result.Add(new(terminal, matches.Length == 0 ? PsseTerminalBindingStatus.MissingSourceRecord : PsseTerminalBindingStatus.AmbiguousSourceRecord,
                        null, null, matches.Length == 0 ? $"No {kind.ToString().ToLowerInvariant()} shunt record matches the terminal identity." :
                        $"More than one {kind.ToString().ToLowerInvariant()} shunt source record matches the terminal identity."));
                    continue;
                }
                PsseShuntSource shunt = matches[0];
                bool modelled = shunt.InService && shunt.BusRetained;
                result.Add(new(terminal, modelled ? PsseTerminalBindingStatus.BoundToShuntRecord : PsseTerminalBindingStatus.LegacyModelOmitted,
                    null, null, modelled
                        ? $"Matched {kind.ToString().ToLowerInvariant()} shunt at bus {shunt.Key.Bus}, identifier '{shunt.Key.Id}'. Its admittance is represented in the legacy bus model."
                        : $"Matched {kind.ToString().ToLowerInvariant()} shunt at bus {shunt.Key.Bus}, identifier '{shunt.Key.Id}', but it is out of service or its bus was omitted from the legacy model.")
                {
                    ShuntSourceIndex = Array.IndexOf(parsed.ShuntSources.ToArray(), shunt),
                    ShuntKey = shunt.Key,
                });
                continue;
            }

            result.Add(new(terminal, PsseTerminalBindingStatus.UnsupportedEquipmentType, null, null,
                $"Terminal type '{terminal.EquipmentType}' is retained but has no source-record binding in this adapter."));
        }
        return Array.AsReadOnly(result.ToArray());
    }

    private static bool IsTwoTerminalMatch(PsseBranchKey key, string kind, PsseTopologyTerminal terminal) =>
        (key.Kind == kind || kind == "branch" && key.Kind == "switching device") && key.ThirdBus == 0 &&
        ((key.FromBus == terminal.Bus && key.ToBus == terminal.ToBus) ||
         (key.ToBus == terminal.Bus && key.FromBus == terminal.ToBus)) &&
        string.Equals(key.Circuit.Trim(), terminal.CircuitId?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool IsThreeWindingUnitMatch(PsseBranchKey key, PsseTopologyTerminal terminal)
    {
        if (key.Kind != "transformer3" || terminal.ToBus is not int toBus || terminal.ThirdBus is not int thirdBus ||
            !string.Equals(key.Circuit.Trim(), terminal.CircuitId?.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        int[] sourceBuses = { key.FromBus, key.ToBus, key.ThirdBus };
        int[] terminalBuses = { terminal.Bus, toBus, thirdBus };
        Array.Sort(sourceBuses);
        Array.Sort(terminalBuses);
        return sourceBuses.SequenceEqual(terminalBuses);
    }

    private static TerminalEndIdentity EndpointIdentity(PsseTopologyTerminal terminal)
    {
        string identity = terminal.EquipmentType is "B" or "2" or "3"
            ? terminal.CircuitId?.Trim().ToUpperInvariant() ?? ""
            : terminal.EquipmentId?.Trim().ToUpperInvariant() ?? "";
        if (terminal.EquipmentType is "B" or "2" && terminal.ToBus is int toBus)
            return new(terminal.EquipmentType, terminal.Substation, terminal.Bus,
                Math.Min(terminal.Bus, toBus), Math.Max(terminal.Bus, toBus), identity);
        if (terminal.EquipmentType == "3" && terminal.ToBus is int j && terminal.ThirdBus is int k)
        {
            int[] buses = { terminal.Bus, j, k };
            Array.Sort(buses);
            return new(terminal.EquipmentType, terminal.Substation, terminal.Bus, buses[1], buses[2], identity);
        }
        return new(terminal.EquipmentType, terminal.Substation, terminal.Bus, 0, 0, identity);
    }

    private readonly record struct TerminalEndIdentity(string Type, int Substation, int LocalBus,
        int OtherBus1, int OtherBus2, string Id);

    private static string Format(PsseBranchKey key) =>
        $"{key.Kind} {key.FromBus}-{key.ToBus}, circuit '{key.Circuit}'";
}
