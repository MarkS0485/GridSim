namespace GridSim.Core.IO.Psse.Topology;

public enum PsseTerminalSetStatus { Complete, Incomplete, Unresolved }

/// <summary>
/// Audits the terminal rows present in the native substation blocks. This detects partial terminal sets; it does not
/// claim that a piece of equipment with no substation terminal row was inventoried.
/// </summary>
public sealed record PsseTerminalSetAudit(string EquipmentIdentity, int ExpectedEnds, int ObservedEnds,
    PsseTerminalSetStatus Status, IReadOnlyList<int> SourceLines, string Detail);

public static class PsseTerminalSetAuditor
{
    /// <summary>Audits the equipment inventory as well as captured rows, including devices with no terminal row.</summary>
    public static IReadOnlyList<PsseTerminalSetAudit> Audit(PsseSubstationTopology topology, PsseParseResult parsed,
        IReadOnlyList<PsseTerminalBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(bindings);
        var busesBySubstation = topology.Nodes.GroupBy(node => node.Substation)
            .ToDictionary(group => group.Key, group => group.Select(node => node.Bus).ToHashSet());
        var bindingsByLine = bindings.ToDictionary(binding => binding.Terminal.SourceLine);
        var audits = new List<PsseTerminalSetAudit>();
        foreach (IGrouping<string, PsseEquipmentSource> group in parsed.EquipmentSources.GroupBy(SourceKey))
        {
            PsseEquipmentSource[] sources = group.ToArray();
            PsseEquipmentSource source = sources[0];
            int[] sourceBuses = EquipmentBuses(source);
            int[] expectedBuses = sourceBuses.Where(bus => busesBySubstation.Values.Any(stationBuses => stationBuses.Contains(bus)))
                .Distinct().Order().ToArray();
            if (expectedBuses.Length == 0) continue;

            PsseTopologyTerminal[] terminals = topology.Terminals.Where(terminal => TerminalKey(terminal) == group.Key).ToArray();
            PsseTopologyTerminal[] localTerminals = terminals.Where(terminal => expectedBuses.Contains(terminal.Bus)).ToArray();
            int observed = localTerminals.Select(terminal => terminal.Bus).Distinct().Count();
            bool terminalRowsUnique = localTerminals.Length == observed;
            bool resolved = localTerminals.All(terminal => bindingsByLine.TryGetValue(terminal.SourceLine, out PsseTerminalBinding? binding) &&
                binding.Status is PsseTerminalBindingStatus.BoundToBranch or PsseTerminalBindingStatus.BoundToMachine or
                    PsseTerminalBindingStatus.BoundToLoadRecord or PsseTerminalBindingStatus.BoundToShuntRecord or
                    PsseTerminalBindingStatus.LegacyModelOmitted);
            bool identityUnique = sources.Length == 1;
            PsseTerminalSetStatus status = !identityUnique || !terminalRowsUnique || !resolved
                ? PsseTerminalSetStatus.Unresolved
                : observed < expectedBuses.Length ? PsseTerminalSetStatus.Incomplete
                : observed > expectedBuses.Length ? PsseTerminalSetStatus.Unresolved
                : PsseTerminalSetStatus.Complete;
            string detail = status switch
            {
                PsseTerminalSetStatus.Complete => $"Observed all {expectedBuses.Length} expected terminal end(s) in represented substations.",
                PsseTerminalSetStatus.Incomplete => $"Observed {observed} of {expectedBuses.Length} expected terminal end(s) in represented substations.",
                _ when !identityUnique => "More than one source equipment row has this terminal identity.",
                _ when !terminalRowsUnique => "More than one terminal row maps to a local equipment end.",
                _ => "At least one terminal identity is missing, ambiguous, unsupported or duplicated."
            };
            audits.Add(new PsseTerminalSetAudit(group.Key, expectedBuses.Length, observed, status,
                Array.AsReadOnly(new[] { source.SourceLine }.Concat(localTerminals.Select(terminal => terminal.SourceLine)).Distinct().Order().ToArray()), detail));
        }
        return Array.AsReadOnly(audits.OrderBy(audit => audit.EquipmentIdentity, StringComparer.Ordinal).ToArray());
    }

    public static IReadOnlyList<PsseTerminalSetAudit> Audit(IReadOnlyList<PsseTerminalBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        return Array.AsReadOnly(bindings.GroupBy(GroupKey)
            .Select(group =>
            {
                PsseTerminalBinding first = group.First();
                PsseTopologyTerminal terminal = first.Terminal;
                int expected = terminal.EquipmentType switch { "B" or "2" => 2, "3" => 3, _ => 1 };
                int observed = group.Select(binding => binding.Terminal.Bus).Distinct().Count();
                bool resolved = group.All(binding => binding.Status is PsseTerminalBindingStatus.BoundToBranch or
                    PsseTerminalBindingStatus.BoundToMachine or PsseTerminalBindingStatus.BoundToLoadRecord or
                    PsseTerminalBindingStatus.BoundToShuntRecord or PsseTerminalBindingStatus.LegacyModelOmitted);
                PsseTerminalSetStatus status = !resolved || observed > expected
                    ? PsseTerminalSetStatus.Unresolved
                    : observed < expected ? PsseTerminalSetStatus.Incomplete : PsseTerminalSetStatus.Complete;
                string detail = status switch
                {
                    PsseTerminalSetStatus.Complete => $"Observed all {expected} expected terminal end(s).",
                    PsseTerminalSetStatus.Incomplete => $"Observed {observed} of {expected} expected terminal end(s).",
                    _ => "At least one terminal identity is missing, ambiguous, unsupported or duplicated."
                };
                return new PsseTerminalSetAudit(group.Key, expected, observed, status,
                    Array.AsReadOnly(group.Select(binding => binding.Terminal.SourceLine).Order().ToArray()), detail);
            })
            .OrderBy(audit => audit.EquipmentIdentity, StringComparer.Ordinal)
            .ToArray());
    }

    private static string GroupKey(PsseTerminalBinding binding)
    {
        PsseTopologyTerminal terminal = binding.Terminal;
        string type = terminal.EquipmentType;
        string id = type is "B" or "2" or "3"
            ? terminal.CircuitId?.Trim().ToUpperInvariant() ?? ""
            : terminal.EquipmentId?.Trim().ToUpperInvariant() ?? "";
        int[] buses = type switch
        {
            "B" or "2" when terminal.ToBus is int to => new[] { terminal.Bus, to }.Order().ToArray(),
            "3" when terminal.ToBus is int to && terminal.ThirdBus is int third => new[] { terminal.Bus, to, third }.Order().ToArray(),
            _ => new[] { terminal.Bus }
        };
        return $"{type}|{id}|{string.Join(",", buses)}";
    }

    private static int[] EquipmentBuses(PsseEquipmentSource source) => source.ThirdBus is int third
        ? new[] { source.Bus, source.ToBus!.Value, third }
        : source.ToBus is int to ? new[] { source.Bus, to } : new[] { source.Bus };

    private static string SourceKey(PsseEquipmentSource source) => Key(source.EquipmentType,
        EquipmentBuses(source), source.EquipmentType is "B" or "2" or "3" ? source.Id :
        source.EquipmentType == "S" ? "" : source.Id);

    private static string TerminalKey(PsseTopologyTerminal terminal)
    {
        int[] buses = terminal.ThirdBus is int third
            ? new[] { terminal.Bus, terminal.ToBus!.Value, third }
            : terminal.ToBus is int to ? new[] { terminal.Bus, to } : new[] { terminal.Bus };
        string id = terminal.EquipmentType is "B" or "2" or "3" || terminal.EquipmentType == "S"
            ? terminal.CircuitId ?? "" : terminal.EquipmentId ?? "";
        return Key(terminal.EquipmentType, buses, id);
    }

    private static string Key(string type, IEnumerable<int> buses, string id) =>
        $"{type}|{id.Trim().ToUpperInvariant()}|{string.Join(",", buses.Order())}";
}
