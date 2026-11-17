namespace GridSim.Core.IO.Psse.Topology;

public enum PsseSwitchProjectionDisposition
{
    ContractedIdealConnection,
    RetainedAsBranchEquipment,
    Open,
    OutOfServiceEndpoint,
    UnsupportedInternalFiniteBranch,
    UnsupportedZeroSeriesWithShunts,
}

public sealed record PsseProjectedElectricalBus(int GroupId, int BusId,
    IReadOnlyList<PsseTopologyNode> Nodes, IReadOnlyList<int> NativeBusIds);

public sealed record PsseProjectedSwitch(PsseSwitchKey Key, int FromBus, int ToBus,
    PsseSwitchProjectionDisposition Disposition, double ResistancePu, double ReactancePu,
    double ChargingSusceptancePu, double FromConductancePu, double FromSusceptancePu,
    double ToConductancePu, double ToSusceptancePu, int SourceLine);

/// <summary>
/// Deterministic electrical bus identities formed by contracting only ideal closed switches. This is a topology
/// projection, not a <see cref="GridSim.Core.Model.GridModel"/> builder; attached equipment and controls still need
/// complete identity rebinding before any solve can be requested.
/// </summary>
public sealed record PsseSubstationElectricalProjectionResult(
    IReadOnlyList<PsseProjectedElectricalBus> Buses,
    IReadOnlyDictionary<int, int> GroupToProjectedBusId,
    IReadOnlyDictionary<int, int> ProjectedBusIdToGroup,
    IReadOnlyDictionary<int, IReadOnlyList<int>> NativeBusToProjectedBusIds,
    IReadOnlyDictionary<int, int> TerminalLineToProjectedBusId,
    IReadOnlyList<PsseTopologyTerminal> UnmappedTerminals,
    IReadOnlyList<PsseProjectedSwitch> Switches,
    bool HasUnsupportedSwitchDisposition,
    bool TerminalMappingsComplete);

public static class PsseSubstationElectricalProjection
{
    public static PsseSubstationElectricalProjectionResult Project(PsseParseResult parsed,
        PsseSubstationTopology topology, IReadOnlyDictionary<PsseSwitchKey, bool> switchStates)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(switchStates);
        if (parsed.Revision != topology.Revision)
            throw new InvalidDataException("PSS/E electrical projection and source case must use the same revision.");

        PsseSubstationConnectivityResult connectivity = PsseSubstationProjection.Project(topology, switchStates);
        PsseTopologyNode[] nodes = topology.Nodes.OrderBy(node => node.Substation).ThenBy(node => node.Number).ToArray();
        var nodeIndex = nodes.Select((node, index) => (node, index))
            .ToDictionary(item => (item.node.Substation, item.node.Number), item => item.index);
        var sets = new DisjointSet(nodes.Length);
        foreach (PsseTopologySwitch sw in topology.Switches.OrderBy(sw => sw.Substation)
                     .ThenBy(sw => Math.Min(sw.FromNode, sw.ToNode)).ThenBy(sw => Math.Max(sw.FromNode, sw.ToNode))
                     .ThenBy(sw => sw.CircuitId, StringComparer.Ordinal))
        {
            PsseSwitchKey key = PsseSubstationProjection.Key(sw);
            if (connectivity.EffectiveSwitchStates[key] && sw.IsIdealConnection)
                sets.Union(nodeIndex[(sw.Substation, sw.FromNode)], nodeIndex[(sw.Substation, sw.ToNode)]);
        }

        var components = new Dictionary<int, List<PsseTopologyNode>>();
        for (int i = 0; i < nodes.Length; i++)
        {
            int root = sets.Find(i);
            if (!components.TryGetValue(root, out List<PsseTopologyNode>? component))
                components.Add(root, component = new List<PsseTopologyNode>());
            component.Add(nodes[i]);
        }
        List<List<PsseTopologyNode>> ordered = components.Values
            .OrderBy(component => component[0].Substation).ThenBy(component => component[0].Number).ToList();
        int nextBusId = 1;
        if (ordered.Count > 0)
        {
            int maxExistingBus = parsed.RawBusIds.Concat(parsed.StarPointBuses)
                .Concat(parsed.Model.Buses.Select(bus => bus.Id)).DefaultIfEmpty(0).Max();
            try { nextBusId = checked(maxExistingBus + 1); }
            catch (OverflowException error) { throw new InvalidDataException("Cannot allocate projected bus IDs above the native bus range.", error); }
        }

        var projectedBuses = new List<PsseProjectedElectricalBus>(ordered.Count);
        var groupToBus = new Dictionary<int, int>();
        var busToGroup = new Dictionary<int, int>();
        var nodeToBus = new Dictionary<(int Substation, int Node), int>();
        var nativeToProjected = new Dictionary<int, List<int>>();
        for (int i = 0; i < ordered.Count; i++)
        {
            int groupId = i + 1;
            int busId;
            try { busId = checked(nextBusId + i); }
            catch (OverflowException error) { throw new InvalidDataException("Projected bus ID range overflowed.", error); }
            List<PsseTopologyNode> component = ordered[i];
            int[] nativeIds = component.Select(node => node.Bus).Distinct().Order().ToArray();
            projectedBuses.Add(new PsseProjectedElectricalBus(groupId, busId,
                Array.AsReadOnly(component.ToArray()), Array.AsReadOnly(nativeIds)));
            groupToBus.Add(groupId, busId);
            busToGroup.Add(busId, groupId);
            foreach (PsseTopologyNode node in component)
            {
                nodeToBus.Add((node.Substation, node.Number), busId);
                if (!nativeToProjected.TryGetValue(node.Bus, out List<int>? mapped))
                    nativeToProjected.Add(node.Bus, mapped = new List<int>());
                if (!mapped.Contains(busId)) mapped.Add(busId);
            }
        }

        var terminalToBus = new Dictionary<int, int>();
        var unmappedTerminals = new List<PsseTopologyTerminal>();
        foreach (PsseTopologyTerminal terminal in topology.Terminals)
        {
            if (terminal.Node != 0 && nodeToBus.TryGetValue((terminal.Substation, terminal.Node), out int busId))
                terminalToBus.Add(terminal.SourceLine, busId);
            else
                unmappedTerminals.Add(terminal);
        }

        var projectedSwitches = new List<PsseProjectedSwitch>(topology.Switches.Count);
        foreach (PsseTopologySwitch sw in topology.Switches)
        {
            PsseSwitchKey key = PsseSubstationProjection.Key(sw);
            int from = nodeToBus[(sw.Substation, sw.FromNode)];
            int to = nodeToBus[(sw.Substation, sw.ToNode)];
            bool requested = connectivity.RequestedSwitchStates[key];
            bool effective = connectivity.EffectiveSwitchStates[key];
            PsseSwitchProjectionDisposition disposition = !requested
                ? PsseSwitchProjectionDisposition.Open
                : !effective ? PsseSwitchProjectionDisposition.OutOfServiceEndpoint
                : sw.IsIdealConnection ? PsseSwitchProjectionDisposition.ContractedIdealConnection
                : sw.ResistancePu == 0 && sw.ReactancePu == 0
                    ? PsseSwitchProjectionDisposition.UnsupportedZeroSeriesWithShunts
                : from == to ? PsseSwitchProjectionDisposition.UnsupportedInternalFiniteBranch
                : PsseSwitchProjectionDisposition.RetainedAsBranchEquipment;
            projectedSwitches.Add(new PsseProjectedSwitch(key, from, to, disposition,
                sw.ResistancePu, sw.ReactancePu, sw.ChargingSusceptancePu,
                sw.FromConductancePu, sw.FromSusceptancePu, sw.ToConductancePu, sw.ToSusceptancePu,
                sw.SourceLine));
        }

        bool unsupported = projectedSwitches.Any(sw =>
            sw.Disposition is (PsseSwitchProjectionDisposition.UnsupportedInternalFiniteBranch or
                PsseSwitchProjectionDisposition.UnsupportedZeroSeriesWithShunts));
        return new PsseSubstationElectricalProjectionResult(Array.AsReadOnly(projectedBuses.ToArray()),
            new System.Collections.ObjectModel.ReadOnlyDictionary<int, int>(groupToBus),
            new System.Collections.ObjectModel.ReadOnlyDictionary<int, int>(busToGroup),
            new System.Collections.ObjectModel.ReadOnlyDictionary<int, IReadOnlyList<int>>(
                nativeToProjected.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<int>)Array.AsReadOnly(kv.Value.Order().ToArray()))),
            new System.Collections.ObjectModel.ReadOnlyDictionary<int, int>(terminalToBus),
            Array.AsReadOnly(unmappedTerminals.ToArray()), Array.AsReadOnly(projectedSwitches.ToArray()), unsupported,
            unmappedTerminals.Count == 0);
    }

    private sealed class DisjointSet
    {
        private readonly int[] _parents;
        public DisjointSet(int count) => _parents = Enumerable.Range(0, count).ToArray();
        public int Find(int value)
        {
            int root = value;
            while (_parents[root] != root) root = _parents[root];
            while (_parents[value] != value)
            {
                int parent = _parents[value];
                _parents[value] = root;
                value = parent;
            }
            return root;
        }
        public void Union(int left, int right) => _parents[Find(right)] = Find(left);
    }
}
