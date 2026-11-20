using GridSim.Core.Model;
using GridSim.Core.Model.Substations;

namespace GridSim.Core.Presentation.Workbench.Sld;

/// <summary>What a drawn element is. A <see cref="Busbar"/> is a bus (or a node-breaker connectivity node); a line, transformer or
/// switching device sits between two busbars; a generator, load, shunt or earth switch hangs from one.</summary>
public enum SldKind { Busbar, Line, Transformer, Breaker, Disconnector, LoadBreak, Generator, Load, Shunt, EarthSwitch }

/// <summary>One busbar of the diagram: a bus of a bus-branch case or a connectivity node of a node-breaker model.
/// <see cref="Key"/> is the bus id (bus-branch) or -1 (node-breaker).</summary>
public sealed record SldNode(string Id, int Index, string Label, double BaseKv, int Key);

/// <summary>
/// One element of the case as the diagram sees it (R4-18). <see cref="Id"/> is stable and unique: <c>bus/{busId}</c>,
/// <c>branch/{index}</c> (index into the model's branch list), <c>gen/{index}</c> (index into the whole fleet, out-of-service
/// units included), <c>load/{busId}</c> and <c>shunt/{busId}</c>; for a node-breaker model <c>node/{id}</c>, <c>switch/{id}</c>,
/// <c>line/{id}</c>, <c>xfmr/{id}</c>, <c>gen/{id}</c>, <c>load/{id}</c>, <c>shunt/{id}</c>. <see cref="Node"/> is the index of the
/// busbar the element sits on (the first end of an edge) and <see cref="OtherNode"/> the second end (-1 for a hanging element).
/// <see cref="InService"/> is false for an open switch, an out-of-service branch or unit.
/// </summary>
public sealed record SldElement(string Id, SldKind Kind, int Source, int Node, int OtherNode, string Label, bool InService,
    double BaseKv, double RatingMva, double V1 = 0, double V2 = 0)
{
    public bool IsEdge => OtherNode >= 0;
}

/// <summary>
/// The shared, view-independent description of what a single-line diagram must contain: one busbar per bus, one edge per branch (or
/// switch), one hanging symbol per generator, bus load and bus shunt, in model order, plus an explicit omission list for the parts
/// of the model the diagram does not draw (tap changers, boundaries, DC links, protection devices ...). Both clients draw exactly
/// this, so the bijection "model element to diagram object" is checkable: <see cref="Elements"/> against the geometry's symbols.
/// </summary>
public sealed class SldModel
{
    private SldModel(GridModel? grid, SubstationModel? substations, IReadOnlyList<SldNode> nodes, IReadOnlyList<SldElement> elements, IReadOnlyList<string> omissions)
    {
        Grid = grid;
        Substations = substations;
        Nodes = nodes;
        Elements = elements;
        Omissions = omissions;
    }

    /// <summary>The bus-branch model this was built from (null for a node-breaker diagram).</summary>
    public GridModel? Grid { get; }

    /// <summary>The node-breaker model this was built from (null for a bus-branch diagram).</summary>
    public SubstationModel? Substations { get; }

    public IReadOnlyList<SldNode> Nodes { get; }

    /// <summary>Every drawn non-busbar element: edges (branches, switches) first, then generators, loads, shunts, earth switches; each in model order.</summary>
    public IReadOnlyList<SldElement> Elements { get; }

    /// <summary>What the model has and the diagram does not draw, one sentence per kind (empty when nothing is left out).</summary>
    public IReadOnlyList<string> Omissions { get; }

    /// <summary>The ids of every object the diagram must show: every busbar, then every element, in order.</summary>
    public IEnumerable<string> ExpectedIds() => Nodes.Select(n => n.Id).Concat(Elements.Select(e => e.Id));

    public static string BusId(int bus) => "bus/" + WorkbenchText.Canon(bus);
    public static string BranchId(int index) => "branch/" + WorkbenchText.Canon(index);
    public static string GeneratorId(int index) => "gen/" + WorkbenchText.Canon(index);
    public static string LoadId(int bus) => "load/" + WorkbenchText.Canon(bus);
    public static string ShuntId(int bus) => "shunt/" + WorkbenchText.Canon(bus);

    /// <summary>The node index of a bus id of a bus-branch diagram, or -1.</summary>
    public int NodeOfBus(int busId)
    {
        for (int i = 0; i < Nodes.Count; i++) if (Nodes[i].Key == busId) return i;
        return -1;
    }

    /// <summary>Build the element list of a bus-branch model. A repeated bus id is drawn once (the first wins; the loader refuses
    /// such a case anyway); a branch or unit naming a bus the model lacks is left out and said so in <see cref="Omissions"/>.</summary>
    public static SldModel From(GridModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var nodes = new List<SldNode>();
        var index = new Dictionary<int, int>();
        var omissions = new List<string>();
        var elements = new List<SldElement>();
        var drawn = new bool[model.Buses.Count];
        for (int i = 0; i < model.Buses.Count; i++)
        {
            Bus b = model.Buses[i];
            if (index.TryAdd(b.Id, nodes.Count))
            {
                drawn[i] = true;
                nodes.Add(new SldNode(BusId(b.Id), nodes.Count, string.IsNullOrEmpty(b.Name) ? WorkbenchText.Canon(b.Id) : b.Name!, b.BaseKv, b.Id));
            }
            else omissions.Add($"bus {WorkbenchText.Canon(b.Id)} is listed twice; the second is not drawn");
        }
        var missing = new List<string>();
        for (int i = 0; i < model.Branches.Count; i++)
        {
            Branch br = model.Branches[i];
            if (!index.TryGetValue(br.From, out int a) || !index.TryGetValue(br.To, out int b2)) { missing.Add(BranchId(i)); continue; }
            elements.Add(new SldElement(BranchId(i), br.IsTransformer ? SldKind.Transformer : SldKind.Line, i, a, b2,
                string.IsNullOrEmpty(br.Name) ? WorkbenchText.Canon(br.From) + "-" + WorkbenchText.Canon(br.To) : br.Name!, br.InService,
                nodes[a].BaseKv, br.RateAMva, br.R, br.X));
        }
        for (int i = 0; i < model.AllGenerators.Count; i++)
        {
            Generator g = model.AllGenerators[i];
            if (!index.TryGetValue(g.Bus, out int a)) { missing.Add(GeneratorId(i)); continue; }
            elements.Add(new SldElement(GeneratorId(i), SldKind.Generator, i, a, -1, string.IsNullOrEmpty(g.Name) ? "G" + WorkbenchText.Canon(i) : g.Name!,
                g.InService, nodes[a].BaseKv, g.MBase, g.Pg, g.Qg));
        }
        for (int i = 0; i < model.Buses.Count; i++)
        {
            Bus b = model.Buses[i];
            if (drawn[i] && (b.Pd != 0 || b.Qd != 0))
                elements.Add(new SldElement(LoadId(b.Id), SldKind.Load, i, index[b.Id], -1, "load " + WorkbenchText.Canon(b.Id), true, b.BaseKv, 0, b.Pd, b.Qd));
        }
        for (int i = 0; i < model.Buses.Count; i++)
        {
            Bus b = model.Buses[i];
            if (drawn[i] && (b.Gs != 0 || b.Bs != 0))
                elements.Add(new SldElement(ShuntId(b.Id), SldKind.Shunt, i, index[b.Id], -1, "shunt " + WorkbenchText.Canon(b.Id), true, b.BaseKv, 0, b.Gs, b.Bs));
        }
        if (missing.Count > 0) omissions.Add($"{WorkbenchText.Canon(missing.Count)} element(s) name a bus the model does not have and are not drawn: {string.Join(", ", missing.Take(10))}");
        void Omit(string what, int n) { if (n > 0) omissions.Add($"{what}: {WorkbenchText.Canon(n)} not drawn"); }
        Omit("on-load tap changers", model.Oltcs.Count);
        Omit("boundaries", model.Boundaries.Count);
        Omit("LCC DC links", model.LccLinks.Count);
        Omit("VSC DC links", model.VscLinks.Count);
        Omit("multi-terminal DC links", model.MtdcLinks.Count);
        Omit("FACTS devices", model.FactsDevices.Count);
        Omit("static var compensators", model.StaticVarCompensators.Count);
        Omit("induction machines", model.InductionMachines.Count);
        Omit("transformer flow controls", model.TransformerFlowControls.Count);
        return new SldModel(model, null, nodes, elements, omissions);
    }

    /// <summary>
    /// Build the element list of a node-breaker model (R4-11): one busbar per connectivity node, one edge per switch (earth switches
    /// hang from their node), line and transformer, one hanging symbol per generator, load and shunt. A switch is "in service" when
    /// it is closed. Everything is in the model's own order (substation, then declaration), so the diagram never depends on how the
    /// file was written beyond that order.
    /// </summary>
    public static SldModel From(SubstationModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var nodes = new List<SldNode>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Substation s in model.Substations)
            foreach (ConnectivityNode n in s.Nodes)
                if (index.TryAdd(n.Id, nodes.Count))
                    nodes.Add(new SldNode("node/" + n.Id, nodes.Count, n.Id, s.VoltageLevels.FirstOrDefault(v => v.Id == n.VoltageLevel)?.NominalKv ?? 0, -1));
        var elements = new List<SldElement>();
        var omissions = new List<string>();
        var earth = new List<SldElement>();
        foreach (Substation s in model.Substations)
            foreach (NbSwitch sw in s.Switches)
            {
                if (!index.TryGetValue(sw.FromNode, out int a)) { omissions.Add($"switch {sw.Id} names unknown node {sw.FromNode}; not drawn"); continue; }
                if (sw.Kind == SwitchKind.EarthSwitch || sw.ToNode is null)
                {
                    earth.Add(new SldElement("switch/" + sw.Id, SldKind.EarthSwitch, 0, a, -1, sw.Id, !sw.NormalOpen, nodes[a].BaseKv, 0));
                    continue;
                }
                if (!index.TryGetValue(sw.ToNode, out int b)) { omissions.Add($"switch {sw.Id} names unknown node {sw.ToNode}; not drawn"); continue; }
                SldKind kind = sw.Kind switch { SwitchKind.Breaker => SldKind.Breaker, SwitchKind.Disconnector => SldKind.Disconnector, _ => SldKind.LoadBreak };
                elements.Add(new SldElement("switch/" + sw.Id, kind, 0, a, b, sw.Id, !sw.NormalOpen, nodes[a].BaseKv, 0));
            }
        foreach (NbLine l in model.Lines)
            if (index.TryGetValue(l.NodeA, out int a) && index.TryGetValue(l.NodeB, out int b))
                elements.Add(new SldElement("line/" + l.Id, SldKind.Line, 0, a, b, l.Id, true, nodes[a].BaseKv, l.RateAMva, l.R, l.X));
            else omissions.Add($"line {l.Id} names an unknown node; not drawn");
        foreach (NbTransformer t in model.Transformers)
            if (index.TryGetValue(t.NodeHv, out int a) && index.TryGetValue(t.NodeLv, out int b))
                elements.Add(new SldElement("xfmr/" + t.Id, SldKind.Transformer, 0, a, b, t.Id, true, nodes[a].BaseKv, t.RateAMva, t.R, t.X));
            else omissions.Add($"transformer {t.Id} names an unknown node; not drawn");
        foreach (NbGenerator g in model.Generators)
            if (index.TryGetValue(g.Node, out int a)) elements.Add(new SldElement("gen/" + g.Id, SldKind.Generator, 0, a, -1, g.Id, true, nodes[a].BaseKv, 0, g.Pg, g.Qg));
            else omissions.Add($"generator {g.Id} names an unknown node; not drawn");
        foreach (NbLoad l in model.Loads)
            if (index.TryGetValue(l.Node, out int a)) elements.Add(new SldElement("load/" + l.Id, SldKind.Load, 0, a, -1, l.Id, true, nodes[a].BaseKv, 0, l.Pd, l.Qd));
            else omissions.Add($"load {l.Id} names an unknown node; not drawn");
        foreach (NbShunt h in model.Shunts)
            if (index.TryGetValue(h.Node, out int a)) elements.Add(new SldElement("shunt/" + h.Id, SldKind.Shunt, 0, a, -1, h.Id, true, nodes[a].BaseKv, 0, h.Gs, h.Bs));
            else omissions.Add($"shunt {h.Id} names an unknown node; not drawn");
        elements.AddRange(earth);
        return new SldModel(null, model, nodes, elements, omissions);
    }
}
