using System.Globalization;

namespace GridSim.Core.Model.Substations;

// =====================================================================================================================
// RC4 R4-11 part A: the native node-breaker model.
//
// A node-breaker description is a graph: connectivity nodes joined by switches, with equipment terminals sitting on the
// nodes. It is a NEW layer beside the bus-branch GridModel and does not touch it: SubstationProjection turns a
// SubstationModel plus switch states into an ordinary GridModel, so every solver sees only what it saw before.
//
// Identities are scoped to the file: a connectivity node id is unique across the whole model, a switch id likewise, and
// an equipment id is unique across all equipment. Equipment ids double as the names of the projected branches and
// generators, which is how a result is traced back to its node-breaker source.
// =====================================================================================================================

/// <summary>What a switching device is. A closed switch of any kind but an earth switch joins its two nodes; a closed
/// earth switch earths the node it sits on instead.</summary>
public enum SwitchKind
{
    /// <summary>A circuit breaker.</summary>
    Breaker,
    /// <summary>A disconnector (isolator).</summary>
    Disconnector,
    /// <summary>An earth switch: closed, it earths its node; it never joins two nodes.</summary>
    EarthSwitch,
    /// <summary>A load-break switch (conducts when closed, like a breaker).</summary>
    LoadBreak,
}

/// <summary>A voltage level of a substation: all its connectivity nodes share one nominal voltage.</summary>
public sealed record VoltageLevel(string Id, double NominalKv);

/// <summary>A bay (a group of nodes and switches serving one feeder, coupler or diameter): a label for the editor and
/// the diagrams; it carries no electrical meaning of its own.</summary>
public sealed record SubstationBay(string Id, string VoltageLevel, string? Name = null);

/// <summary>A connectivity node: a point of zero impedance that switches and equipment terminals attach to.</summary>
public sealed record ConnectivityNode(string Id, string VoltageLevel, string? Bay = null);

/// <summary>A switching device. <see cref="ToNode"/> is null for an earth switch (it joins <see cref="FromNode"/> to
/// earth) and set for every other kind.</summary>
public sealed record NbSwitch(string Id, SwitchKind Kind, string FromNode, string? ToNode, bool NormalOpen, string? Bay = null);

/// <summary>One substation: voltage levels, bays, connectivity nodes and switches.</summary>
public sealed record Substation(string Id, string? Name,
    IReadOnlyList<VoltageLevel> VoltageLevels, IReadOnlyList<SubstationBay> Bays,
    IReadOnlyList<ConnectivityNode> Nodes, IReadOnlyList<NbSwitch> Switches);

/// <summary>A line between two connectivity nodes of the same nominal voltage (per unit on the model base).</summary>
public sealed record NbLine(string Id, string NodeA, string NodeB, double R, double X, double B = 0, double RateAMva = 0);

/// <summary>A two-winding transformer (per unit on the model base; the tap is on the HV side, as <see cref="Branch.TapRatio"/>).</summary>
public sealed record NbTransformer(string Id, string NodeHv, string NodeLv, double R, double X, double B = 0,
    double TapRatio = 1.0, double PhaseShiftDeg = 0.0, double RateAMva = 0);

/// <summary>A load at one node (MW, MVAr).</summary>
public sealed record NbLoad(string Id, string Node, double Pd, double Qd = 0);

/// <summary>A generator at one node. <see cref="Slack"/> marks the reference machine of its electrical bus.</summary>
public sealed record NbGenerator(string Id, string Node, double Pg, double Vg = 1.0, double Qg = 0,
    double Qmin = double.NegativeInfinity, double Qmax = double.PositiveInfinity,
    double Pmin = 0, double Pmax = double.PositiveInfinity, bool Slack = false);

/// <summary>A shunt at one node: the MW / MVAr it draws at 1.0 pu voltage (as <see cref="Bus.Gs"/> / <see cref="Bus.Bs"/>).</summary>
public sealed record NbShunt(string Id, string Node, double Gs, double Bs);

/// <summary>What sits on a terminal.</summary>
public enum NbEquipmentKind { Line, Transformer, Load, Generator, Shunt }

/// <summary>One equipment terminal. A branch has two (one to one with its two ends), anything else has one.</summary>
/// <param name="Id">"equipment.end": a.b for a line, hv.lv for a transformer, t for a one-terminal device.</param>
public sealed record NbTerminal(string Id, string EquipmentId, NbEquipmentKind Kind, string End, string Node);

/// <summary>An inconsistent or unresolvable node-breaker description, refused with every problem named.</summary>
public sealed class SubstationTopologyException : Exception
{
    public SubstationTopologyException(IReadOnlyList<string> problems)
        : base("node-breaker topology refused: " + string.Join(" ", problems)) => Problems = problems;
    public IReadOnlyList<string> Problems { get; }
}

/// <summary>How a switch with no stated state is treated. There is no inferred closed switch: the default is to refuse.</summary>
public enum UnspecifiedStatePolicy
{
    /// <summary>A switch with no stated state is an error (the default).</summary>
    Refuse,
    /// <summary>A switch with no stated state takes its declared normal state (a declared policy, never a guess).</summary>
    UseNormalState,
}

/// <summary>
/// A node-breaker network: substations (nodes and switches) and the equipment attached to their nodes. Immutable data;
/// <see cref="Validate"/> checks it and <see cref="SubstationProjection"/> reduces it to a bus-branch model.
/// </summary>
public sealed class SubstationModel
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public string Name { get; init; } = "nodebreaker";
    public double BaseMva { get; init; } = GridModel.DefaultBaseMva;
    public double FrequencyHz { get; init; } = global::GridSim.Core.Dynamics.FrequencyLimits.NominalHz;
    public IReadOnlyList<Substation> Substations { get; init; } = Array.Empty<Substation>();
    public IReadOnlyList<NbLine> Lines { get; init; } = Array.Empty<NbLine>();
    public IReadOnlyList<NbTransformer> Transformers { get; init; } = Array.Empty<NbTransformer>();
    public IReadOnlyList<NbLoad> Loads { get; init; } = Array.Empty<NbLoad>();
    public IReadOnlyList<NbGenerator> Generators { get; init; } = Array.Empty<NbGenerator>();
    public IReadOnlyList<NbShunt> Shunts { get; init; } = Array.Empty<NbShunt>();

    /// <summary>Every connectivity node, in substation then declaration order.</summary>
    public IEnumerable<ConnectivityNode> AllNodes => Substations.SelectMany(s => s.Nodes);

    /// <summary>Every switch, in substation then declaration order.</summary>
    public IEnumerable<NbSwitch> AllSwitches => Substations.SelectMany(s => s.Switches);

    /// <summary>Every equipment terminal: lines, transformers, loads, generators, shunts; each group in ordinal order
    /// of the equipment id (so the order never depends on how the file was written).</summary>
    public IReadOnlyList<NbTerminal> Terminals()
    {
        var t = new List<NbTerminal>();
        foreach (NbLine l in Lines.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            t.Add(new NbTerminal(l.Id + ".a", l.Id, NbEquipmentKind.Line, "a", l.NodeA));
            t.Add(new NbTerminal(l.Id + ".b", l.Id, NbEquipmentKind.Line, "b", l.NodeB));
        }
        foreach (NbTransformer x in Transformers.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            t.Add(new NbTerminal(x.Id + ".hv", x.Id, NbEquipmentKind.Transformer, "hv", x.NodeHv));
            t.Add(new NbTerminal(x.Id + ".lv", x.Id, NbEquipmentKind.Transformer, "lv", x.NodeLv));
        }
        foreach (NbLoad x in Loads.OrderBy(x => x.Id, StringComparer.Ordinal))
            t.Add(new NbTerminal(x.Id + ".t", x.Id, NbEquipmentKind.Load, "t", x.Node));
        foreach (NbGenerator x in Generators.OrderBy(x => x.Id, StringComparer.Ordinal))
            t.Add(new NbTerminal(x.Id + ".t", x.Id, NbEquipmentKind.Generator, "t", x.Node));
        foreach (NbShunt x in Shunts.OrderBy(x => x.Id, StringComparer.Ordinal))
            t.Add(new NbTerminal(x.Id + ".t", x.Id, NbEquipmentKind.Shunt, "t", x.Node));
        return t;
    }

    /// <summary>The nominal voltage of a node's voltage level, or null when the node or level is unknown.</summary>
    public double? NominalKvOf(string node)
    {
        foreach (Substation s in Substations)
            foreach (ConnectivityNode n in s.Nodes)
                if (n.Id == node)
                    return s.VoltageLevels.FirstOrDefault(v => v.Id == n.VoltageLevel)?.NominalKv;
        return null;
    }

    /// <summary>The normal state of every switch (true = open), keyed by switch id.</summary>
    public IReadOnlyDictionary<string, bool> NormalStates() =>
        AllSwitches.ToDictionary(s => s.Id, s => s.NormalOpen, StringComparer.Ordinal);

    /// <summary>
    /// Every consistency problem, in a fixed order (empty when consistent): duplicate ids, unknown references, a node
    /// with no switch and no terminal (dangling), a node held by a single switch and no terminal (a dangling stub), a
    /// switch joining nodes of different nominal voltage (or one node to itself), a line joining two voltage levels,
    /// non-finite numbers.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        var p = new List<string>();
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        var nodeKv = new Dictionary<string, double>(StringComparer.Ordinal);
        var swIds = new HashSet<string>(StringComparer.Ordinal);
        var subIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (Substation s in Substations)
        {
            if (string.IsNullOrEmpty(s.Id) || !subIds.Add(s.Id)) p.Add($"substation id '{s.Id}' is empty or repeated.");
            var levels = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (VoltageLevel v in s.VoltageLevels)
            {
                if (!levels.TryAdd(v.Id, v.NominalKv)) p.Add($"substation '{s.Id}': voltage level '{v.Id}' is repeated.");
                if (!double.IsFinite(v.NominalKv) || v.NominalKv <= 0)
                    p.Add($"substation '{s.Id}': voltage level '{v.Id}' needs a nominal voltage above 0 kV (got {v.NominalKv.ToString("R", Inv)}).");
            }
            var bays = new HashSet<string>(StringComparer.Ordinal);
            foreach (SubstationBay b in s.Bays)
            {
                if (!bays.Add(b.Id)) p.Add($"substation '{s.Id}': bay '{b.Id}' is repeated.");
                if (!levels.ContainsKey(b.VoltageLevel)) p.Add($"substation '{s.Id}': bay '{b.Id}' names unknown voltage level '{b.VoltageLevel}'.");
            }
            foreach (ConnectivityNode n in s.Nodes)
            {
                if (string.IsNullOrEmpty(n.Id) || !nodeIds.Add(n.Id))
                    p.Add($"connectivity node id '{n.Id}' (substation '{s.Id}') is empty or repeated; node ids are unique across the whole file.");
                if (levels.TryGetValue(n.VoltageLevel, out double kv)) nodeKv.TryAdd(n.Id, kv);
                else p.Add($"substation '{s.Id}': node '{n.Id}' names unknown voltage level '{n.VoltageLevel}'.");
                if (n.Bay is not null && !bays.Contains(n.Bay)) p.Add($"substation '{s.Id}': node '{n.Id}' names unknown bay '{n.Bay}'.");
            }
        }
        var degree = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Substation s in Substations)
        {
            var own = new HashSet<string>(s.Nodes.Select(n => n.Id), StringComparer.Ordinal);
            foreach (NbSwitch sw in s.Switches)
            {
                if (string.IsNullOrEmpty(sw.Id) || !swIds.Add(sw.Id)) p.Add($"switch id '{sw.Id}' (substation '{s.Id}') is empty or repeated.");
                if (sw.Bay is not null && !s.Bays.Any(b => b.Id == sw.Bay)) p.Add($"switch '{sw.Id}' names unknown bay '{sw.Bay}'.");
                if (!own.Contains(sw.FromNode)) { p.Add($"switch '{sw.Id}' joins unknown node '{sw.FromNode}' (a switch joins nodes of its own substation '{s.Id}')."); continue; }
                degree[sw.FromNode] = degree.GetValueOrDefault(sw.FromNode) + 1;
                if (sw.Kind == SwitchKind.EarthSwitch)
                {
                    if (sw.ToNode is not null) p.Add($"earth switch '{sw.Id}' must not name a second node (it joins '{sw.FromNode}' to earth).");
                    continue;
                }
                if (sw.ToNode is null) { p.Add($"switch '{sw.Id}' ({sw.Kind}) has no second node."); continue; }
                if (!own.Contains(sw.ToNode)) { p.Add($"switch '{sw.Id}' joins unknown node '{sw.ToNode}' (a switch joins nodes of its own substation '{s.Id}')."); continue; }
                degree[sw.ToNode] = degree.GetValueOrDefault(sw.ToNode) + 1;
                if (sw.FromNode == sw.ToNode) p.Add($"switch '{sw.Id}' joins node '{sw.FromNode}' to itself.");
                else if (nodeKv.TryGetValue(sw.FromNode, out double a) && nodeKv.TryGetValue(sw.ToNode, out double b) && a != b)
                    p.Add($"switch '{sw.Id}' joins nodes of different nominal voltage: '{sw.FromNode}' is {a.ToString("R", Inv)} kV and '{sw.ToNode}' is {b.ToString("R", Inv)} kV.");
            }
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        void Id(string kind, string id) { if (string.IsNullOrEmpty(id) || !ids.Add(id)) p.Add($"{kind} id '{id}' is empty or repeated; equipment ids are unique across all equipment."); }
        void Node(string equip, string node) { if (!nodeIds.Contains(node)) p.Add($"{equip} sits on unknown connectivity node '{node}'."); }
        void Finite(string equip, params double[] v) { if (v.Any(x => !double.IsFinite(x))) p.Add($"{equip} has a non-finite number."); }
        foreach (NbLine l in Lines)
        {
            Id("line", l.Id); Node($"line '{l.Id}'", l.NodeA); Node($"line '{l.Id}'", l.NodeB); Finite($"line '{l.Id}'", l.R, l.X, l.B, l.RateAMva);
            if (nodeKv.TryGetValue(l.NodeA, out double a) && nodeKv.TryGetValue(l.NodeB, out double b) && a != b)
                p.Add($"line '{l.Id}' joins nodes of different nominal voltage ({a.ToString("R", Inv)} kV at '{l.NodeA}', {b.ToString("R", Inv)} kV at '{l.NodeB}'); use a transformer.");
            if (l.NodeA == l.NodeB) p.Add($"line '{l.Id}' has both ends on node '{l.NodeA}'.");
        }
        foreach (NbTransformer x in Transformers)
        {
            Id("transformer", x.Id); Node($"transformer '{x.Id}'", x.NodeHv); Node($"transformer '{x.Id}'", x.NodeLv);
            Finite($"transformer '{x.Id}'", x.R, x.X, x.B, x.TapRatio, x.PhaseShiftDeg, x.RateAMva);
            if (x.NodeHv == x.NodeLv) p.Add($"transformer '{x.Id}' has both windings on node '{x.NodeHv}'.");
        }
        foreach (NbLoad x in Loads) { Id("load", x.Id); Node($"load '{x.Id}'", x.Node); Finite($"load '{x.Id}'", x.Pd, x.Qd); }
        foreach (NbGenerator x in Generators) { Id("generator", x.Id); Node($"generator '{x.Id}'", x.Node); Finite($"generator '{x.Id}'", x.Pg, x.Vg, x.Qg); }
        foreach (NbShunt x in Shunts) { Id("shunt", x.Id); Node($"shunt '{x.Id}'", x.Node); Finite($"shunt '{x.Id}'", x.Gs, x.Bs); }

        var terminalCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (NbTerminal t in Terminals()) terminalCount[t.Node] = terminalCount.GetValueOrDefault(t.Node) + 1;
        foreach (ConnectivityNode n in AllNodes)
        {
            int sw = degree.GetValueOrDefault(n.Id), tm = terminalCount.GetValueOrDefault(n.Id);
            if (sw == 0 && tm == 0)
                p.Add($"connectivity node '{n.Id}' is dangling: no switch and no equipment terminal is attached to it.");
            else if (sw == 1 && tm == 0)
                p.Add($"connectivity node '{n.Id}' is dangling: it is held by one switch only and carries no equipment terminal.");
        }
        if (!double.IsFinite(BaseMva) || BaseMva <= 0) p.Add("the system base must be a finite MVA above 0.");
        return p;
    }

    /// <summary>Throw <see cref="SubstationTopologyException"/> naming every problem, or return when consistent.</summary>
    public void Validate()
    {
        IReadOnlyList<string> p = Problems();
        if (p.Count > 0) throw new SubstationTopologyException(p);
    }
}
