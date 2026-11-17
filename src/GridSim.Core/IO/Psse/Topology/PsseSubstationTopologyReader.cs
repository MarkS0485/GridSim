using System.Globalization;
using GridSim.Core.Model;

namespace GridSim.Core.IO.Psse.Topology;

public sealed record PsseTopologyIssue(int Substation, int SourceLine, string Message);
public sealed record PsseTopologyNode(int Substation, int Number, string Name, int Bus, bool InService,
    double? VmPu, double? VaDeg, int SourceLine);
public sealed record PsseTopologySwitch(int Substation, int FromNode, int ToNode, string CircuitId,
    string Name, int Type, bool Closed, bool NormalClosed, double ReactancePu, double ResistancePu,
    double ChargingSusceptancePu, double FromConductancePu, double FromSusceptancePu,
    double ToConductancePu, double ToSusceptancePu, int SourceLine)
{
    // The PSS/E station switching device record is NI, NJ, CKT, NAME, TYPE, STATUS, NSTAT, X, RATE1, RATE2, RATE3
    // (Siemens PSS/E 34.8.0 Data Formats, s.1.28.3). R, the charging and the end shunts are not RAW fields: a RAW read
    // sets them to zero, and only a normalised graph built in code may set them. The three ratings are kept below.

    /// <summary>RATE1 (MVA, or current expressed as MVA); 0 when not given.</summary>
    public double Rate1Mva { get; init; }
    /// <summary>RATE2 (MVA, or current expressed as MVA); 0 when not given.</summary>
    public double Rate2Mva { get; init; }
    /// <summary>RATE3 (MVA, or current expressed as MVA); 0 when not given.</summary>
    public double Rate3Mva { get; init; }

    public bool IsIdealConnection => ReactancePu == 0 && ResistancePu == 0 && ChargingSusceptancePu == 0 &&
        FromConductancePu == 0 && FromSusceptancePu == 0 && ToConductancePu == 0 && ToSusceptancePu == 0;
}
public sealed record PsseTopologyTerminal(int Substation, int Bus, int Node, string EquipmentType,
    string? EquipmentId, int? ToBus, int? ThirdBus, string? CircuitId, int SourceLine);
public sealed record PsseSubstationTopology(int Revision, IReadOnlyList<int> Substations,
    IReadOnlyList<PsseTopologyNode> Nodes, IReadOnlyList<PsseTopologySwitch> Switches,
    IReadOnlyList<PsseTopologyTerminal> Terminals);

public static class PsseSubstationTopologyReader
{
    public static PsseSubstationTopology Read(PsseParseResult parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        if (parsed.Revision < 35)
            throw new InvalidDataException("Node-breaker substation topology requires PSS/E revision 35.");
        var issues = new List<PsseTopologyIssue>();
        var ids = new HashSet<int>();
        var substationOrder = new List<int>();
        var nodes = new List<PsseTopologyNode>();
        var switches = new List<PsseTopologySwitch>();
        var terminals = new List<PsseTopologyTerminal>();
        // RawBusIds retain native IDs even when the legacy model drops isolated buses, without admitting generated star buses.
        IEnumerable<int> nativeBusIds = parsed.RawBusIds.Count > 0
            ? parsed.RawBusIds
            : parsed.BusMeta.Keys.Concat(parsed.Model.Buses.Select(b => b.Id)).Except(parsed.StarPointBuses);
        var busIds = nativeBusIds.ToHashSet();
        var stationByBus = new Dictionary<int, int>();

        foreach (PsseSubstationBlock block in parsed.Substations)
        {
            int stationId = Integer(block.Header, 0, "substation number", -1, block.HeaderLine, issues);
            double latitude = Real(block.Header, 2, "substation latitude", stationId, block.HeaderLine, issues, 0.0);
            double longitude = Real(block.Header, 3, "substation longitude", stationId, block.HeaderLine, issues, 0.0);
            double groundingOhms = Real(block.Header, 4, "substation grounding resistance", stationId, block.HeaderLine, issues, 0.1);
            if (stationId is < 1 or > 99999)
                issues.Add(new(stationId, block.HeaderLine, "Substation number must be between 1 and 99999."));
            if (!double.IsFinite(latitude) || latitude is < -90 or > 90)
                issues.Add(new(stationId, block.HeaderLine, "Substation latitude must be between -90 and 90 degrees."));
            if (!double.IsFinite(longitude) || longitude is < -180 or > 180)
                issues.Add(new(stationId, block.HeaderLine, "Substation longitude must be between -180 and 180 degrees."));
            if (!(double.IsFinite(groundingOhms) && groundingOhms >= 0))
                issues.Add(new(stationId, block.HeaderLine, "Substation grounding resistance must be finite and non-negative."));
            if (!ids.Add(stationId))
                issues.Add(new(stationId, block.HeaderLine, "Substation number is duplicated."));
            else
                substationOrder.Add(stationId);
            if (!ValidateLineCounts(block, stationId, issues)) continue;

            var nodeNumbers = new HashSet<int>();
            var busByNode = new Dictionary<int, (int Bus, bool InService)>();
            var switchIdentities = new HashSet<(int From, int To, string Circuit)>();
            for (int i = 0; i < block.Nodes.Count; i++)
            {
                IReadOnlyList<string?> row = block.Nodes[i];
                int line = block.NodeLines[i];
                int number = Integer(row, 0, "node number", stationId, line, issues);
                int bus = Integer(row, 2, "bus number", stationId, line, issues);
                int status = Integer(row, 3, "node status", stationId, line, issues, 1);
                double? vm = OptionalReal(row, 4, "node voltage", stationId, line, issues);
                double? va = OptionalReal(row, 5, "node angle", stationId, line, issues);
                if (number is < 1 or > 999 || !nodeNumbers.Add(number))
                    issues.Add(new(stationId, line, $"Node number {number} is invalid or duplicated in this substation."));
                if (!busIds.Contains(bus))
                    issues.Add(new(stationId, line, $"Node {number} references bus {bus}, which is absent from the RAW bus records."));
                else if (stationByBus.TryGetValue(bus, out int owner) && owner != stationId)
                    issues.Add(new(stationId, line, $"Bus {bus} is assigned to nodes in substations {owner} and {stationId}."));
                else
                    stationByBus[bus] = stationId;
                if (status is not (0 or 1))
                    issues.Add(new(stationId, line, $"Node {number} status must be 0 or 1."));
                if (vm is double v && v != -1 && !(double.IsFinite(v) && v > 0))
                    issues.Add(new(stationId, line, $"Node {number} voltage must be -1 or finite and positive."));
                if (va is double a && !double.IsFinite(a))
                    issues.Add(new(stationId, line, $"Node {number} angle must be finite."));
                busByNode[number] = (bus, status == 1);
                nodes.Add(new(stationId, number, Text(row, 1), bus, status == 1, vm, va, line));
            }

            for (int i = 0; i < block.SwitchingDevices.Count; i++)
            {
                IReadOnlyList<string?> row = block.SwitchingDevices[i];
                int line = block.SwitchingDeviceLines[i];
                int from = Integer(row, 0, "switch from node", stationId, line, issues);
                int to = Integer(row, 1, "switch to node", stationId, line, issues);
                int type = Integer(row, 4, "switch type", stationId, line, issues);
                int status = Integer(row, 5, "switch status", stationId, line, issues, 1);
                int normal = Integer(row, 6, "switch normal status", stationId, line, issues, 1);
                double x = Real(row, 7, "switch reactance", stationId, line, issues, .0001);
                double rate1 = Real(row, 8, "switch RATE1", stationId, line, issues, 0);
                double rate2 = Real(row, 9, "switch RATE2", stationId, line, issues, 0);
                double rate3 = Real(row, 10, "switch RATE3", stationId, line, issues, 0);
                if (!nodeNumbers.Contains(from) || !nodeNumbers.Contains(to) || from == to)
                    issues.Add(new(stationId, line, "Switch endpoints must name two distinct nodes in this substation."));
                else
                {
                    // A node's bus is "the electrical bus [that] represents this node and others that are connected by
                    // closed switching devices" (PSS/E Data Formats s.1.28.2). A switch between nodes of two different
                    // buses is therefore consistent only while it is open in the file (for example an open coupler
                    // between two bus-branch buses); closed in the file it contradicts the bus-branch records.
                    if (busByNode[from].Bus != busByNode[to].Bus && status != 0)
                        issues.Add(new(stationId, line, $"A switch closed in the file must connect nodes assigned to the same bus-branch bus (nodes {from} and {to} are on buses {busByNode[from].Bus} and {busByNode[to].Bus})."));
                }
                if (type is < 1 or > 3)
                    issues.Add(new(stationId, line, "Switch type must be 1, 2 or 3."));
                if (status is not (0 or 1) || normal is not (0 or 1))
                    issues.Add(new(stationId, line, "Switch current and normal status must be 0 or 1."));
                if (!(double.IsFinite(x) && x != 0))
                    issues.Add(new(stationId, line, "Switch reactance must be finite and non-zero in PSS/E RAW data."));
                if (new[] { rate1, rate2, rate3 }.Any(value => !double.IsFinite(value)))
                    issues.Add(new(stationId, line, "Switch ratings must be finite."));
                string circuitId = Text(row, 2, "1");
                var identity = (Math.Min(from, to), Math.Max(from, to), circuitId.Trim().ToUpperInvariant());
                if (!switchIdentities.Add(identity))
                    issues.Add(new(stationId, line, "Switch identity is duplicated (endpoint order and circuit ID are ignored)."));
                switches.Add(new(stationId, from, to, circuitId, Text(row, 3), type,
                    status == 1, normal == 1, x, 0, 0, 0, 0, 0, 0, line)
                { Rate1Mva = rate1, Rate2Mva = rate2, Rate3Mva = rate3 });
            }

            for (int i = 0; i < block.Terminals.Count; i++)
            {
                IReadOnlyList<string?> row = block.Terminals[i];
                int line = block.TerminalLines[i];
                int bus = Integer(row, 0, "terminal bus", stationId, line, issues);
                int node = Integer(row, 1, "terminal node", stationId, line, issues);
                string kind = Text(row, 2).ToUpperInvariant();
                string? id = Text(row, 3, "1");
                int? toBus = null;
                int? thirdBus = null;
                string? circuit = null;
                if (!busIds.Contains(bus))
                    issues.Add(new(stationId, line, $"Terminal references bus {bus}, which is absent from the model."));
                else if (!stationByBus.TryGetValue(bus, out int localStation) || localStation != stationId)
                    issues.Add(new(stationId, line, $"Terminal's local bus {bus} is not assigned to a node in substation {stationId}."));
                if (node != 0 && !nodeNumbers.Contains(node))
                    issues.Add(new(stationId, line, $"Terminal references node {node}, which is absent from this substation."));
                if (node != 0 && busByNode.TryGetValue(node, out var assigned) && assigned.Bus != bus)
                    issues.Add(new(stationId, line, $"Terminal on bus {bus} references node {node}, which is assigned to bus {assigned.Bus}."));
                if (node == 0 && busByNode.Values.Any(n => n.Bus == bus))
                    issues.Add(new(stationId, line, $"Terminal on bus {bus} omits its substation node."));
                if (kind is "B" or "2" or "3")
                {
                    toBus = Integer(row, 3, "branch to bus", stationId, line, issues);
                    if (kind == "3")
                        thirdBus = Integer(row, 4, "transformer third bus", stationId, line, issues);
                    circuit = Text(row, kind == "3" ? 5 : 4, "1");
                    id = circuit;
                    if (!busIds.Contains(toBus.Value))
                        issues.Add(new(stationId, line, $"Terminal references absent to-bus {toBus.Value}."));
                    if (thirdBus.HasValue && !busIds.Contains(thirdBus.Value))
                        issues.Add(new(stationId, line, $"Terminal references absent third bus {thirdBus.Value}."));
                }
                else if (kind is "L" or "F" or "M" or "I")
                {
                    id = Text(row, 3, "1");
                }
                else if (kind == "S")
                {
                    id = null;
                }
                else if (kind is "D" or "V" or "N" or "A")
                {
                    id = Text(row, 3);
                }
                else
                {
                    issues.Add(new(stationId, line, $"Unsupported terminal record type '{kind}'."));
                }
                terminals.Add(new(stationId, bus, node, kind, id, toBus, thirdBus, circuit, line));
            }
        }
        if (issues.Count > 0)
            throw new InvalidDataException("PSS/E substation topology is invalid:" + Environment.NewLine +
                string.Join(Environment.NewLine, issues.Select(i => $"  substation {i.Substation}, line {i.SourceLine}: {i.Message}")));
        return new(parsed.Revision, Array.AsReadOnly(substationOrder.ToArray()),
            Array.AsReadOnly(nodes.ToArray()), Array.AsReadOnly(switches.ToArray()), Array.AsReadOnly(terminals.ToArray()));
    }

    private static bool ValidateLineCounts(PsseSubstationBlock b, int id, List<PsseTopologyIssue> issues)
    {
        bool valid = b.Nodes.Count == b.NodeLines.Count && b.SwitchingDevices.Count == b.SwitchingDeviceLines.Count &&
            b.Terminals.Count == b.TerminalLines.Count;
        if (!valid) issues.Add(new(id, b.HeaderLine, "Captured record and source-line counts differ."));
        return valid;
    }
    private static int Integer(IReadOnlyList<string?> row, int index, string field, int stationId, int line,
        List<PsseTopologyIssue> issues, int? fallback = null)
    {
        string? text = index < row.Count ? row[index] : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            if (fallback.HasValue) return fallback.Value;
            issues.Add(new(stationId, line, $"Missing {field}."));
            return 0;
        }
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)) return result;
        issues.Add(new(stationId, line, $"Invalid {field} '{text}'."));
        return 0;
    }
    private static double Real(IReadOnlyList<string?> row, int index, string field, int stationId, int line,
        List<PsseTopologyIssue> issues, double fallback)
    {
        string? text = index < row.Count ? row[index] : null;
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        if (double.TryParse(text.Replace('D', 'E').Replace('d', 'e'), NumberStyles.Float,
            CultureInfo.InvariantCulture, out double result)) return result;
        issues.Add(new(stationId, line, $"Invalid {field} '{text}'."));
        return double.NaN;
    }
    private static double? OptionalReal(IReadOnlyList<string?> row, int index, string field, int stationId, int line,
        List<PsseTopologyIssue> issues)
    {
        if (index >= row.Count || string.IsNullOrWhiteSpace(row[index])) return null;
        return Real(row, index, field, stationId, line, issues, double.NaN);
    }
    private static string Text(IReadOnlyList<string?> row, int index, string fallback = "")
        => index < row.Count ? row[index]?.Trim() ?? fallback : fallback;
}
