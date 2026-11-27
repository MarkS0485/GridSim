using System.Globalization;
using GridSim.Core.Model.Substations;

namespace GridSim.Core.IO.Scl;

// =====================================================================================================================
// RC5 WP30: SclDocument + gridsim-scl-params/1 -> SubstationModel (the node-breaker model, gridsim-substations/1).
//
// The SCL schema lists the conducting-equipment type codes but documents none of them. The meanings used here are the
// names OpenSCD gives the codes in its conducting-equipment dialog (OMICRONEnergyOSS/oscd-scl-dialogs,
// wizards/conductingequipment.ts, Apache-2.0), a public source; no IEC 61850 standard text was used. Only these codes
// are imported:
//   CBR  Circuit Breaker            -> NbSwitch Breaker (two terminals)
//   DIS  Disconnector               -> NbSwitch Disconnector (two terminals), or EarthSwitch when one terminal is
//                                      grounded (cNodeName="grounded", the convention OpenSCD documents for earth switches)
//   LIN  Power Overhead Line,
//   CAB  Power Cable                -> NbLine, impedance from the params file
//   GEN  Generator                  -> NbGenerator, P and V from the params file
//   IFL  Infeeding Line             -> NbGenerator marked slack (an external grid), V from the params file
//   CTR  Current Transformer,
//   VTR  Voltage Transformer        -> measurement points (no impedance). A current transformer with two terminals sits
//                                      in series: its two nodes are one electrical point and are merged.
//   PowerTransformer (PTR) with 2 windings -> NbTransformer; with 3 windings -> NbTransformer3W (params file)
// Every other code is refused by name, and the whole import with it: leaving a capacitor, a reactor or a motor out of
// the network would change it. The SCL substation section holds no switch state, so every switch needs its state in the
// params file: a switch without one is refused by name, unless the caller asks to assume closed (scl-import
// --assume-closed), and then every switch so taken is listed in the notes and recorded in the model's own notes, which
// the written file carries.
// =====================================================================================================================

/// <summary>An SCL file and params file that cannot be imported, with every problem named.</summary>
public sealed class SclImportException(IReadOnlyList<string> problems)
    : Exception("SCL import refused: " + string.Join(" ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>One SCL object and what it became.</summary>
/// <param name="SclPath">The SCL path (pathName for a node).</param>
/// <param name="SclType">The SCL element or type code (CBR, LIN, PTR, ConnectivityNode, ...).</param>
/// <param name="GridSimKind">What it became (breaker, line, node, ...).</param>
/// <param name="GridSimId">The id in the node-breaker model.</param>
public sealed record SclMapped(string SclPath, string SclType, string GridSimKind, string GridSimId);

/// <summary>An instrument transformer, kept as a measurement point for later binding (no impedance, no topology).</summary>
/// <param name="Node">The node it measures at (after merging), or null when it has no terminal on a node.</param>
public sealed record SclMeasurementPoint(string Path, string Type, string? Node);

/// <summary>The result of an import.</summary>
public sealed class SclImportResult
{
    public required SubstationModel Model { get; init; }
    public required IReadOnlyList<SclMapped> Mapped { get; init; }
    public required IReadOnlyList<SclMeasurementPoint> MeasurementPoints { get; init; }
    /// <summary>Nodes merged into another (alias pathName -> kept pathName), with the current transformer that joins them.</summary>
    public required IReadOnlyList<(string Alias, string Kept, string Via)> MergedNodes { get; init; }
    /// <summary>Switches with no state in the params file, taken as closed (only with assumeClosed; otherwise refused).</summary>
    public required IReadOnlyList<string> DefaultClosedSwitches { get; init; }
    /// <summary>Plain notes: nodes no terminal uses, voltage levels left out, and so on.</summary>
    public required IReadOnlyList<string> Notes { get; init; }
    public required SclDocument Document { get; init; }
    public required string ParamsSha256 { get; init; }
}

/// <summary>Maps an <see cref="SclDocument"/> onto the node-breaker model. See the file header.</summary>
public static class SclImporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The names of the schema's predefined conducting-equipment codes, as OpenSCD shows them (Apache-2.0), used
    /// only to name a refused code.</summary>
    public static readonly IReadOnlyDictionary<string, string> CodeNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["CBR"] = "Circuit Breaker", ["DIS"] = "Disconnector", ["VTR"] = "Voltage Transformer", ["CTR"] = "Current Transformer",
        ["GEN"] = "Generator", ["CAP"] = "Capacitor Bank", ["REA"] = "Reactor", ["CON"] = "Converter", ["MOT"] = "Motor",
        ["EFN"] = "Earth Fault Neutralizer", ["PSH"] = "Power Shunt", ["BAT"] = "Battery", ["BSH"] = "Bushing",
        ["CAB"] = "Power Cable", ["GIL"] = "Gas Insulated Line", ["LIN"] = "Power Overhead Line",
        ["RES"] = "Neutral Resistor", ["RRC"] = "Rotating Reactive Component", ["SAR"] = "Surge Arrester",
        ["TCF"] = "Thyristor Controlled Frequency Converter", ["TCR"] = "Thyristor Controlled Reactive Component",
        ["IFL"] = "Infeeding Line", ["FAN"] = "Fan", ["SCR"] = "Semiconductor Controlled Rectifier",
        ["SMC"] = "Synchronous Machine", ["PMP"] = "Pump",
    };

    /// <summary>The codes the importer maps (every other code is refused).</summary>
    public static readonly IReadOnlyList<string> ImportedCodes = ["CBR", "DIS", "LIN", "CAB", "GEN", "IFL", "CTR", "VTR"];

    /// <summary>Import <paramref name="doc"/> with the electrical values of <paramref name="p"/>. Throws
    /// <see cref="SclImportException"/> naming every problem when anything cannot be imported. A switch whose state the
    /// params file does not give is refused, unless <paramref name="assumeClosed"/> is true: it is then taken as closed,
    /// listed in the notes and recorded in <see cref="SubstationModel.Notes"/>.</summary>
    public static SclImportResult Import(SclDocument doc, SclParams p, bool assumeClosed = false)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(p);
        var problems = new List<string>();
        var notes = new List<string>();
        var mapped = new List<SclMapped>();

        // ---- containers that would hold equipment outside the substation section ----
        foreach (SclRootContainer c in doc.RootContainers.Where(c => c.HoldsEquipment))
            problems.Add($"top-level {c.Kind} '{c.Name}' holds conducting equipment; {c.Kind} sections are not imported, so its equipment would be lost.");
        foreach (SclSkippedContainer c in doc.SkippedWithEquipment)
            problems.Add($"{c.Kind}{(c.Name.Length > 0 ? " '" + c.Name + "'" : "")} in {c.Where} holds conducting equipment or a power transformer; {c.Kind} elements are not imported, so that equipment would be lost; refused.");

        // ---- voltage levels ----
        var levelKv = new Dictionary<string, double>(StringComparer.Ordinal);
        var levelProblem = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (SclVoltageLevel v in doc.VoltageLevels)
        {
            if (levelKv.ContainsKey(v.Path) || levelProblem.ContainsKey(v.Path)) { problems.Add($"voltage level '{v.Path}' is repeated."); continue; }
            string? why = Kv(v, out double kv);
            if (why is null) levelKv[v.Path] = kv;
            else levelProblem[v.Path] = why;
        }

        // ---- substations ----
        foreach (string sub in doc.Substations.GroupBy(x => x, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key))
            problems.Add($"substation '{sub}' is repeated.");

        // ---- connectivity nodes ----
        var nodes = new Dictionary<string, SclConnectivityNode>(StringComparer.Ordinal);
        foreach (SclConnectivityNode n in doc.Nodes)
        {
            if (n.PathName.Length == 0) { problems.Add($"a connectivity node in bay '{n.BayPath}' has no pathName."); continue; }
            if (!nodes.TryAdd(n.PathName, n)) problems.Add($"connectivity node pathName '{n.PathName}' is repeated.");
        }

        // ---- equipment paths are unique ----
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in doc.Equipment.Select(e => e.Path).Concat(doc.Transformers.Select(t => t.Path)))
            if (!paths.Add(path)) problems.Add($"equipment path '{path}' is repeated.");

        bool NodeOk(string owner, SclTerminal t)
        {
            if (nodes.ContainsKey(t.ConnectivityNode)) return true;
            problems.Add($"{owner}: terminal '{t.Name}' names connectivity node '{t.ConnectivityNode}', which the substation section does not define.");
            return false;
        }

        // ---- current transformers in series merge their two nodes ----
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        string Find(string x) { while (parent.TryGetValue(x, out string? up)) x = up; return x; }
        var merges = new List<(string Alias, string Kept, string Via)>();
        foreach (SclEquipment e in doc.Equipment.Where(e => e.Type == "CTR"))
        {
            SclTerminal[] live = e.Terminals.Where(t => !t.Grounded).ToArray();
            // An undefined node is reported once, by the equipment pass below.
            if (live.Length != 2 || !nodes.TryGetValue(live[0].ConnectivityNode, out SclConnectivityNode? n0) || !nodes.TryGetValue(live[1].ConnectivityNode, out SclConnectivityNode? n1)) continue;
            if (n0.VoltageLevelPath != n1.VoltageLevelPath)
            {
                problems.Add($"current transformer '{e.Path}' joins node '{n0.PathName}' (voltage level '{n0.VoltageLevelPath}') and node '{n1.PathName}' (voltage level '{n1.VoltageLevelPath}'); a series current transformer has no ratio, so it cannot join two voltage levels; refused.");
                continue;
            }
            string a = Find(live[0].ConnectivityNode), b = Find(live[1].ConnectivityNode);
            if (a == b) continue;
            (string kept, string alias) = string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);
            parent[alias] = kept;
            merges.Add((alias, kept, e.Path));
        }
        // Report every alias against its final representative.
        var mergedNodes = merges.Select(m => (m.Alias, Find(m.Kept), m.Via)).ToList();

        // ---- conducting equipment ----
        var switches = new List<(SclEquipment E, SwitchKind Kind, string From, string? To)>();
        var lineHalves = new Dictionary<string, SclEquipment>(StringComparer.Ordinal);
        var gens = new Dictionary<string, SclEquipment>(StringComparer.Ordinal);
        var infeeds = new Dictionary<string, SclEquipment>(StringComparer.Ordinal);
        var measurement = new List<SclMeasurementPoint>();
        foreach (SclEquipment e in doc.Equipment)
        {
            string who = $"{e.Type} '{e.Path}'";
            SclTerminal[] live = e.Terminals.Where(t => !t.Grounded).ToArray();
            int grounded = e.Terminals.Count - live.Length;
            switch (e.Type)
            {
                case "CBR":
                case "DIS":
                    if (live.Length == 2 && grounded == 0)
                    {
                        if (NodeOk(who, live[0]) & NodeOk(who, live[1]))
                            switches.Add((e, e.Type == "CBR" ? SwitchKind.Breaker : SwitchKind.Disconnector, Find(live[0].ConnectivityNode), Find(live[1].ConnectivityNode)));
                    }
                    else if (e.Type == "DIS" && live.Length == 1 && grounded == 1)
                    {
                        if (NodeOk(who, live[0])) switches.Add((e, SwitchKind.EarthSwitch, Find(live[0].ConnectivityNode), null));
                    }
                    else
                        problems.Add($"{who} has {e.Terminals.Count.ToString(Inv)} terminal(s) ({grounded.ToString(Inv)} grounded); a switch needs two terminals on connectivity nodes"
                            + (e.Type == "DIS" ? ", or one on a node and one grounded (an earth switch)." : "."));
                    break;
                case "LIN":
                case "CAB":
                    if (grounded > 0 || live.Length is < 1 or > 2)
                        problems.Add($"{who} has {e.Terminals.Count.ToString(Inv)} terminal(s) ({grounded.ToString(Inv)} grounded); a line needs one or two terminals on connectivity nodes.");
                    else if (live.All(t => NodeOk(who, t))) lineHalves[e.Path] = e;
                    break;
                case "GEN":
                case "IFL":
                    if (live.Length != 1 || grounded > 0)
                        problems.Add($"{who} has {e.Terminals.Count.ToString(Inv)} terminal(s); a {(e.Type == "GEN" ? "generator" : "infeeding line")} needs exactly one terminal on a connectivity node.");
                    else if (NodeOk(who, live[0])) (e.Type == "GEN" ? gens : infeeds)[e.Path] = e;
                    break;
                case "CTR":
                    if (live.Length > 2) problems.Add($"{who} has {live.Length.ToString(Inv)} terminals on nodes; at most two.");
                    else if (live.Length == 0) { measurement.Add(new SclMeasurementPoint(e.Path, e.Type, null)); notes.Add($"{who} has no terminal on a node; kept as a measurement point without a node."); }
                    else if (live.All(t => NodeOk(who, t))) measurement.Add(new SclMeasurementPoint(e.Path, e.Type, Find(live[0].ConnectivityNode)));
                    break;
                case "VTR":
                    if (live.Length > 1 && live.Select(t => t.ConnectivityNode).Distinct(StringComparer.Ordinal).Count() > 1)
                        problems.Add($"{who} has terminals on two different nodes; a voltage transformer between two nodes is not imported.");
                    else if (live.Length == 0) { measurement.Add(new SclMeasurementPoint(e.Path, e.Type, null)); notes.Add($"{who} has no terminal on a node; kept as a measurement point without a node."); }
                    else if (NodeOk(who, live[0])) measurement.Add(new SclMeasurementPoint(e.Path, e.Type, Find(live[0].ConnectivityNode)));
                    break;
                default:
                    problems.Add(CodeNames.TryGetValue(e.Type, out string? meaning)
                        ? $"{who}: type {e.Type} ({meaning}) is not imported by GridSim's SCL import; refused."
                        : e.Type.StartsWith('E')
                            ? $"{who}: type {e.Type} is an extension code whose meaning the file's author defines; not imported, refused."
                            : $"{who}: type '{e.Type}' is not a code of the SCL schema; refused.");
                    break;
            }
        }

        // ---- switch states ----
        var stateOf = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (SclSwitchParams s in p.Switches)
        {
            if (!switches.Any(x => x.E.Path == s.Path)) problems.Add($"{s.JsonPath}: '{s.Path}' is not a switch (CBR or DIS) of the SCL file.");
            else if (!stateOf.TryAdd(s.Path, s.NormalOpen)) problems.Add($"{s.JsonPath}: switch '{s.Path}' is given twice.");
        }
        var defaultClosed = new List<string>();

        // ---- lines ----
        var lines = new List<(int Order, NbLine Line)>();
        var lineUsed = new HashSet<string>(StringComparer.Ordinal);
        foreach (SclLineParams lp in p.Lines)
        {
            var ends = new List<SclEquipment>();
            foreach (string end in lp.Ends)
            {
                if (!lineHalves.TryGetValue(end, out SclEquipment? e)) { problems.Add($"{lp.JsonPath}: '{end}' is not a line or cable (LIN or CAB) of the SCL file with terminals on nodes."); continue; }
                if (!lineUsed.Add(end)) { problems.Add($"{lp.JsonPath}: '{end}' is used by more than one line entry."); continue; }
                ends.Add(e);
            }
            if (ends.Count != lp.Ends.Count) continue;
            string[] nodeEnds;
            if (ends.Count == 1)
            {
                if (ends[0].Terminals.Count != 2) { problems.Add($"{lp.JsonPath}: '{ends[0].Path}' has one terminal; name the other half of the line as a second end."); continue; }
                nodeEnds = [ends[0].Terminals[0].ConnectivityNode, ends[0].Terminals[1].ConnectivityNode];
            }
            else
            {
                if (ends.Any(e => e.Terminals.Count != 1)) { problems.Add($"{lp.JsonPath}: with two ends, each must be a LIN or CAB with one terminal (the halves of one line)."); continue; }
                nodeEnds = [ends[0].Terminals[0].ConnectivityNode, ends[1].Terminals[0].ConnectivityNode];
            }
            string id = lp.Id ?? ends[0].Path;
            lines.Add((doc.Equipment.TakeWhile(e => e.Path != ends[0].Path).Count(), new NbLine(id, Find(nodeEnds[0]), Find(nodeEnds[1]), lp.R, lp.X, lp.B, lp.RateAMva)));
            foreach (SclEquipment e in ends) mapped.Add(new SclMapped(e.Path, e.Type, "line", id));
        }
        foreach (SclEquipment e in doc.Equipment.Where(e => lineHalves.ContainsKey(e.Path) && !lineUsed.Contains(e.Path)))
            problems.Add($"{e.Type} '{e.Path}' has no entry in the params file's lines (its impedance is required).");

        // ---- generators and infeeds ----
        var generators = new List<NbGenerator>();
        var genSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (SclGeneratorParams g in p.Generators)
        {
            if (!gens.TryGetValue(g.Path, out SclEquipment? e)) { problems.Add($"{g.JsonPath}: '{g.Path}' is not a generator (GEN) of the SCL file with a terminal on a node."); continue; }
            if (!genSeen.Add(g.Path)) { problems.Add($"{g.JsonPath}: generator '{g.Path}' is given twice."); continue; }
            generators.Add(new NbGenerator(g.Path, Find(e.Terminals[0].ConnectivityNode), g.Pg, g.Vg, g.Qg, g.Qmin, g.Qmax, g.Pmin, g.Pmax, g.Slack));
            mapped.Add(new SclMapped(g.Path, "GEN", "generator", g.Path));
        }
        foreach (SclInfeedParams f in p.Infeeds)
        {
            if (!infeeds.TryGetValue(f.Path, out SclEquipment? e)) { problems.Add($"{f.JsonPath}: '{f.Path}' is not an infeeding line (IFL) of the SCL file with a terminal on a node."); continue; }
            if (!genSeen.Add(f.Path)) { problems.Add($"{f.JsonPath}: infeed '{f.Path}' is given twice."); continue; }
            generators.Add(new NbGenerator(f.Path, Find(e.Terminals[0].ConnectivityNode), f.Pg, f.Vg, Slack: true));
            mapped.Add(new SclMapped(f.Path, "IFL", "external grid (slack generator)", f.Path));
        }
        foreach (string path in doc.Equipment.Select(e => e.Path).Where(k => (gens.ContainsKey(k) || infeeds.ContainsKey(k)) && !genSeen.Contains(k)))
            problems.Add($"{(gens.ContainsKey(path) ? "GEN" : "IFL")} '{path}' has no entry in the params file's {(gens.ContainsKey(path) ? "generators" : "infeeds")} (its set point is required).");
        // Document order, so the model never depends on the order of the params file.
        var docOrder = doc.Equipment.Select((e, i) => (e.Path, i)).GroupBy(x => x.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().i, StringComparer.Ordinal);
        generators = generators.OrderBy(g => docOrder[g.Id]).ToList();

        // ---- power transformers ----
        var tx2 = new List<NbTransformer>();
        var tx3 = new List<NbTransformer3W>();
        var txSeen = new HashSet<string>(StringComparer.Ordinal);
        var txByPath = new Dictionary<string, SclTransformer>(StringComparer.Ordinal);
        foreach (SclTransformer t in doc.Transformers) txByPath.TryAdd(t.Path, t);
        string? WindingNode(SclTransformer t, string winding, string jsonPath)
        {
            SclWinding? w = t.Windings.FirstOrDefault(x => x.Name == winding);
            if (w is null) { problems.Add($"{jsonPath}: power transformer '{t.Path}' has no winding named '{winding}'."); return null; }
            SclTerminal[] live = w.Terminals.Where(x => !x.Grounded).ToArray();
            if (live.Length != 1) { problems.Add($"winding '{w.Path}' has {live.Length.ToString(Inv)} terminals on nodes; a winding needs exactly one."); return null; }
            if (w.Terminals.Count > 1) notes.Add($"winding '{w.Path}': its grounded terminal (a neutral earth) is not modelled; the power flow is balanced.");
            return NodeOk($"winding '{w.Path}'", live[0]) ? Find(live[0].ConnectivityNode) : null;
        }
        bool TxOk(string path, int windings, string jsonPath, out SclTransformer t)
        {
            t = null!;
            if (!txByPath.TryGetValue(path, out SclTransformer? found)) { problems.Add($"{jsonPath}: '{path}' is not a power transformer of the SCL file."); return false; }
            t = found;
            if (!txSeen.Add(path)) { problems.Add($"{jsonPath}: power transformer '{path}' is given twice."); return false; }
            if (t.Windings.Count != windings) { problems.Add($"{jsonPath}: power transformer '{path}' has {t.Windings.Count.ToString(Inv)} windings, not {windings.ToString(Inv)}."); return false; }
            return true;
        }
        foreach (SclTransformerParams x in p.Transformers)
        {
            if (!TxOk(x.Path, 2, x.JsonPath, out SclTransformer t)) continue;
            string? hv = WindingNode(t, x.Hv, x.JsonPath), lv = WindingNode(t, x.Lv, x.JsonPath);
            if (x.Hv == x.Lv) { problems.Add($"{x.JsonPath}: hv and lv name the same winding."); continue; }
            if (hv is null || lv is null) continue;
            tx2.Add(new NbTransformer(t.Path, hv, lv, x.R, x.X, x.B, x.TapRatio, x.PhaseShiftDeg, x.RateAMva));
            mapped.Add(new SclMapped(t.Path, t.Type, "transformer", t.Path));
        }
        foreach (SclTransformer3WParams x in p.Transformers3W)
        {
            if (!TxOk(x.Path, 3, x.JsonPath, out SclTransformer t)) continue;
            if (new[] { x.Hv, x.Mv, x.Lv }.Distinct(StringComparer.Ordinal).Count() != 3) { problems.Add($"{x.JsonPath}: hv, mv and lv must name three different windings."); continue; }
            string? hv = WindingNode(t, x.Hv, x.JsonPath), mv = WindingNode(t, x.Mv, x.JsonPath), lv = WindingNode(t, x.Lv, x.JsonPath);
            if (hv is null || mv is null || lv is null) continue;
            tx3.Add(new NbTransformer3W(t.Path, hv, mv, lv, x.R12, x.X12, x.R13, x.X13, x.R23, x.X23, x.B, x.Ratio, x.ShiftDeg, x.RatedMva));
            mapped.Add(new SclMapped(t.Path, t.Type, "three-winding transformer", t.Path));
        }
        foreach (SclTransformer t in doc.Transformers.Where(t => !txSeen.Contains(t.Path)))
            problems.Add(t.Windings.Count is 2 or 3
                ? $"power transformer '{t.Path}' ({t.Windings.Count.ToString(Inv)} windings) has no entry in the params file's {(t.Windings.Count == 2 ? "transformers" : "transformers3w")} (its impedance is required)."
                : $"power transformer '{t.Path}' has {t.Windings.Count.ToString(Inv)} windings; only two- and three-winding transformers are imported; refused.");
        var txOrder = doc.Transformers.Select((t, i) => (t.Path, i)).GroupBy(x => x.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().i, StringComparer.Ordinal);
        tx2 = tx2.OrderBy(t => txOrder[t.Id]).ToList();
        tx3 = tx3.OrderBy(t => txOrder[t.Id]).ToList();

        // ---- loads (the SCL schema has no load code; they come from the params file) ----
        var loads = new List<(int NodeOrder, NbLoad Load)>();
        var nodeOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < doc.Nodes.Count; i++) nodeOrder.TryAdd(doc.Nodes[i].PathName, i);
        foreach (SclLoadParams l in p.Loads)
        {
            if (!nodes.ContainsKey(l.Node)) { problems.Add($"{l.JsonPath}: node '{l.Node}' is not a connectivity node pathName of the SCL file."); continue; }
            loads.Add((nodeOrder[l.Node], new NbLoad(l.Id, Find(l.Node), l.Pd, l.Qd)));
        }
        // Canonical order, so the model never depends on the order of the params file: the SCL file's order of the node
        // named, then the load id (ordinal).
        List<NbLoad> sortedLoads = loads.OrderBy(x => x.NodeOrder).ThenBy(x => x.Load.Id, StringComparer.Ordinal).Select(x => x.Load).ToList();

        // ---- switches, in document order ----
        var nbSwitches = new List<(string Substation, NbSwitch S)>();
        foreach ((SclEquipment e, SwitchKind kind, string from, string? to) in switches)
        {
            bool open = false;
            if (stateOf.TryGetValue(e.Path, out bool st)) open = st;
            else if (assumeClosed) defaultClosed.Add(e.Path);
            else
                problems.Add($"switch '{e.Path}' ({KindWord(kind)}) has no state: the SCL substation section holds none and the params file's switches give none. "
                    + "Give its normalOpen in the params file, or import with --assume-closed to take every such switch as closed (each is then recorded in the output).");
            nbSwitches.Add((e.Substation, new NbSwitch(e.Path, kind, from, to, open, e.BayPath)));
            mapped.Add(new SclMapped(e.Path, e.Type, KindWord(kind), e.Path));
        }

        if (problems.Count > 0) throw new SclImportException(problems);

        // ---- the nodes that are used ----
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach ((_, NbSwitch s) in nbSwitches) { used.Add(s.FromNode); if (s.ToNode is not null) used.Add(s.ToNode); }
        foreach ((_, NbLine l) in lines) { used.Add(l.NodeA); used.Add(l.NodeB); }
        foreach (NbTransformer t in tx2) { used.Add(t.NodeHv); used.Add(t.NodeLv); }
        foreach (NbTransformer3W t in tx3) { used.Add(t.NodeHv); used.Add(t.NodeMv); used.Add(t.NodeLv); }
        foreach (NbGenerator g in generators) used.Add(g.Node);
        foreach (NbLoad l in sortedLoads) used.Add(l.Node);
        foreach (SclMeasurementPoint m in measurement) if (m.Node is not null) used.Add(m.Node);

        // ---- substations, voltage levels, bays, nodes ----
        var subs = new List<Substation>();
        foreach (string sub in doc.Substations.Distinct(StringComparer.Ordinal))
        {
            var keptNodes = new List<ConnectivityNode>();
            foreach (SclConnectivityNode n in doc.Nodes.Where(n => n.Substation == sub))
            {
                if (parent.ContainsKey(n.PathName)) continue;   // merged into another node
                if (!used.Contains(n.PathName)) { notes.Add($"connectivity node '{n.PathName}' has no terminal and no load on it; not imported."); continue; }
                if (!levelKv.ContainsKey(n.VoltageLevelPath))
                {
                    problems.Add($"voltage level '{n.VoltageLevelPath}' (node '{n.PathName}'): {levelProblem.GetValueOrDefault(n.VoltageLevelPath, "no nominal voltage")}");
                    continue;
                }
                keptNodes.Add(new ConnectivityNode(n.PathName, n.VoltageLevelPath, n.BayPath));
                mapped.Add(new SclMapped(n.PathName, "ConnectivityNode", "node", n.PathName));
            }
            var levels = new List<VoltageLevel>();
            foreach (SclVoltageLevel v in doc.VoltageLevels.Where(v => v.Substation == sub))
            {
                if (levelKv.TryGetValue(v.Path, out double kv)) levels.Add(new VoltageLevel(v.Path, kv));
                else if (!keptNodes.Any(n => n.VoltageLevel == v.Path)) notes.Add($"voltage level '{v.Path}' is not imported: {levelProblem[v.Path]} (no imported node is in it).");
            }
            var bays = doc.Bays.Where(b => b.Substation == sub && levelKv.ContainsKey(b.VoltageLevelPath))
                .Select(b => new SubstationBay(b.Path, b.VoltageLevelPath, b.Name)).ToList();
            subs.Add(new Substation(sub, null, levels.AsReadOnly(), bays.AsReadOnly(), keptNodes.AsReadOnly(),
                nbSwitches.Where(s => s.Substation == sub).Select(s => s.S).ToList().AsReadOnly()));
        }
        if (problems.Count > 0) throw new SclImportException(problems);

        foreach ((string alias, string kept, string via) in mergedNodes)
            notes.Add($"connectivity node '{alias}' is merged into '{kept}': current transformer '{via}' joins them in series (no impedance).");
        var modelNotes = new List<string>();
        foreach (string s in defaultClosed)
        {
            string note = $"switch '{s}' has no state in the params file (the SCL substation section holds none); assumed closed (--assume-closed).";
            notes.Add(note);
            modelNotes.Add("scl-import: " + note);
        }

        var model = new SubstationModel
        {
            Name = p.Name ?? (doc.HeaderId.Length > 0 ? doc.HeaderId : "scl"),
            BaseMva = p.BaseMva,
            FrequencyHz = p.FrequencyHz,
            Substations = subs.AsReadOnly(),
            // Document order of each line's first end, so the model never depends on the order of the params file.
            Lines = lines.OrderBy(l => l.Order).Select(l => l.Line).ToList().AsReadOnly(),
            Transformers = tx2.AsReadOnly(),
            Transformers3W = tx3.AsReadOnly(),
            Loads = sortedLoads.AsReadOnly(),
            Generators = generators.AsReadOnly(),
            Notes = modelNotes.AsReadOnly(),
        };
        IReadOnlyList<string> modelProblems = model.Problems();
        if (modelProblems.Count > 0)
            throw new SclImportException(modelProblems.Select(m => "the imported node-breaker model is inconsistent: " + m).ToList());

        return new SclImportResult
        {
            Model = model,
            Mapped = mapped.AsReadOnly(),
            MeasurementPoints = measurement.AsReadOnly(),
            MergedNodes = mergedNodes.AsReadOnly(),
            DefaultClosedSwitches = defaultClosed.AsReadOnly(),
            Notes = notes.AsReadOnly(),
            Document = doc,
            ParamsSha256 = p.Sha256,
        };
    }

    private static string KindWord(SwitchKind kind) => kind switch { SwitchKind.Breaker => "breaker", SwitchKind.Disconnector => "disconnector", _ => "earth switch" };

    /// <summary>The nominal voltage of a level in kV, or the reason there is none. Unit V with multiplier "", k or M. The
    /// value is rounded once: the decimal exponent is shifted in the text (V to kV is e-3, MV to kV e+3) before parsing.</summary>
    private static string? Kv(SclVoltageLevel v, out double kv)
    {
        kv = 0;
        if (v.VoltageText is null) return "it has no Voltage element";
        if (v.Unit != "V") return $"its Voltage unit is '{v.Unit}', not V";
        if (!double.TryParse(v.VoltageText, NumberStyles.Float, Inv, out double x) || !double.IsFinite(x) || !(x > 0))
            return $"its Voltage '{v.VoltageText}' is not a number above 0";
        int shift;
        switch (v.Multiplier)
        {
            case "k": kv = x; return null;
            case "M": shift = 3; break;
            case "": shift = -3; break;
            default: return $"its Voltage multiplier '{v.Multiplier}' is not '', k or M";
        }
        string text = v.VoltageText;
        int e = text.IndexOfAny(['e', 'E']);
        long exponent = 0;
        if (e >= 0 && !long.TryParse(text[(e + 1)..], NumberStyles.AllowLeadingSign, Inv, out exponent))
            return $"its Voltage '{v.VoltageText}' is not a number above 0";
        string scaled = (e >= 0 ? text[..e] : text) + "e" + (exponent + shift).ToString(Inv);
        if (!double.TryParse(scaled, NumberStyles.Float, Inv, out kv) || !double.IsFinite(kv) || !(kv > 0))
            return $"its Voltage '{v.VoltageText}' in kV is not a finite number above 0";
        return null;
    }

    /// <summary>The import report as plain lines (deterministic: every list in document or ordinal order).</summary>
    public static IReadOnlyList<string> ReportLines(SclImportResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var o = new List<string>();
        SclDocument d = r.Document;
        o.Add($"SCL {d.Version}{d.Revision}{(d.Release.Length > 0 ? " release " + d.Release : "")}, header '{d.HeaderId}', sha256 {d.Sha256}" + (r.ParamsSha256.Length > 0 ? $"; params sha256 {r.ParamsSha256}" : ""));
        o.Add($"mapped: {r.Model.Substations.Count.ToString(Inv)} substation(s), {r.Model.AllNodes.Count().ToString(Inv)} node(s), {r.Model.AllSwitches.Count().ToString(Inv)} switch(es), "
            + $"{r.Model.Lines.Count.ToString(Inv)} line(s), {r.Model.Transformers.Count.ToString(Inv)} transformer(s), {r.Model.Transformers3W.Count.ToString(Inv)} three-winding transformer(s), "
            + $"{r.Model.Generators.Count.ToString(Inv)} generator(s) (infeeds included), {r.Model.Loads.Count.ToString(Inv)} load(s) from the params file");
        foreach (SclMapped m in r.Mapped.Where(m => m.SclType != "ConnectivityNode"))
            o.Add($"  {m.SclType,-4} {m.SclPath} -> {m.GridSimKind} '{m.GridSimId}'");
        foreach (SclMeasurementPoint m in r.MeasurementPoints)
            o.Add($"  measurement point {m.Type} {m.Path} at {(m.Node is null ? "no node" : "node '" + m.Node + "'")} (ignored for topology)");
        foreach (KeyValuePair<string, int> kv in d.NotImported)
            o.Add($"  NOT IMPORTED: {kv.Key} x{kv.Value.ToString(Inv)}");
        foreach ((string what, int count) in d.ReadPast)
            o.Add($"  read past: {what} {count.ToString(Inv)}");
        foreach (string g in d.GooseControlBlocks)
            o.Add($"  GOOSE control block {g} (listed only)");
        foreach (string n in r.Notes)
            o.Add($"  note: {n}");
        return o;
    }
}
