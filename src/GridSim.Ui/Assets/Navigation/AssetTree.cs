using System.Globalization;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Assets;
using GridSim.Core.Presentation;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Assets.Overlay;
using GridSim.Ui.Assets.Overviews;
using GridSim.Ui.Assets.Pages;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Presentation;

namespace GridSim.Ui.Assets.Navigation;

// UI programme ASSETS A3: the explorer tree of the ASSETS tab (plan section 3.2), built from the solved model only
// (Run mode; the case records and the overlay join in slices B1 and 5). Every node names the page it opens
// (AssetPageKey) and the A2 route that opens it (the Open* call the WPF explorer makes for the same click), so a tree
// selection shows exactly the page the WPF route shows, made by the same code.
//
// The shape, top to bottom, and its ordering rules (all deterministic; ties never depend on dictionary order):
//   ASSETS (home)
//   SUBSTATIONS      > one group per base voltage, highest first ("400 kV"; buses with no base kV last, "no base kV")
//                      > the buses, in model order (one node per bus id: a repeated id opens its first bus, as every
//                        page does)
//   GENERATORS       > one group per fuel (the fuel name the pages show, UiFuels; ordinal), interconnectors left out
//                      as on the GENERATORS page
//                      > one station per unit name within the fuel (ordinal by name, the unnamed units last as
//                        "(unnamed)", the SchemaEditor's StationIndex rule)
//                      > the units, in model order
//   GENERATOR PHASORS (the phasor page; no children)
//   INTERCONNECTORS  > the links, in model order
//   CABLES & LINES   > one group per voltage band (the band the CABLES & LINES page shows, CircuitInfo.Band: a GB
//                      ladder level when the base voltage is within 0.5 kV of one, otherwise the voltage's own kV, the
//                      ASSETS fix S10 rule; highest first) > the circuits, in model order
//   TRANSFORMERS     > one group per "hi->lo kV" band (the band the TRANSFORMERS page shows, TransformerInfo.Band;
//                      highest HV first, then highest LV; a branch with no base kV at either end last)
//                      > the transformers, in model order
//   TAP CHANGERS     > the OLTC records, in model order (each opens the transformer the solver drives, as its row)
// A class node opens its fleet page (the home card's route); a group node opens its class's fleet page too (the group
// is a way through the tree, not a page of its own in this slice).
//
// UI programme ASSETS B1/B2: every node carries an AssetKey (Keys/AssetKey), the one identity of what it stands for; the
// A3 tree above is built exactly as before (Build(model)). Given an AssetIndex (Build(model, index): the navigator once the
// shell has declared the model's source), the tree goes on with the B2 class nodes (plan S8), each shown only when it has
// members, in this order:
//   DEMANDS          > the demand records of the case file, in case order (none without a case file)
//   BOUNDARIES       > the model's boundaries, in order
//   PROTECTIONS      > the placed protections, in order
//   ASSET CLASSES    > the classes, in library order, then a "bindings" group with the binding rules, in order
//   OUT OF SERVICE   > the units marked out of service, in fleet order
// Each opens an AssetOverviewPage (Overviews/), a member's page by its own key.
//
// UI programme ASSETS slice 5-view (plan Q12: both views over one OverlayIndex), when the index carries the case's asset
// overlay (carried, not used by the solver):
//   OVERLAY          > one group per overlay kind, kinds in ordinal order of their names (the by-kind view)
//                      > every record of that kind, in overlay order: the record's CANONICAL node (its key has no At)
//   and, under every model asset node a record attaches to (its bus, line, transformer, generator or demand; a record
//   with no reference of its own, such as a substation, attaches to the buses its children point at), the records the
//   asset holds, each with its whole child subtree: ATTACHMENT nodes (IsAttachment), each with a key of its own
//   (OverlayRef.At = the node's id) that shows the same record. A record whose parent is attached to the same asset
//   shows under its parent there, not twice.
// Every overlay node opens an AssetOverviewPage of the overlay (Overlay/OverlayPages); the A2/B2 nodes are unchanged.

/// <summary>What a tree node stands for.</summary>
public enum AssetNodeRole
{
    /// <summary>The landing page (ASSETS).</summary>
    Home,

    /// <summary>An asset class (GENERATORS, TRANSFORMERS, ...): opens its fleet page.</summary>
    Class,

    /// <summary>A grouping inside a class (a voltage level, a fuel, a station): opens its class's fleet page.</summary>
    Group,

    /// <summary>One asset of the model: opens its unit page.</summary>
    Unit,
}

/// <summary>Where an asset sits on the network map: a bus to select, or a branch (by model index) to select.</summary>
/// <param name="BusId">The bus to select, or null.</param>
/// <param name="BranchIndex">The branch to select (its index in the model's branch list), or null.</param>
public readonly record struct AssetMapTarget(int? BusId, int? BranchIndex)
{
    /// <summary>
    /// UI programme Phase 5d (2026-10-21): the asset overlay's line route the page is about (its record id), or null.
    /// Show on map then frames the route's polyline on the NETWORK map's Line routes layer when the route is drawn there;
    /// otherwise it selects <see cref="BranchIndex"/> (the line the route carries) as for any branch.
    /// </summary>
    public string? LineRouteId { get; init; }
}

/// <summary>
/// One node of the ASSETS explorer tree (UI programme ASSETS A3): a label, a muted detail line, the page it opens and
/// the route that opens it. Nodes are built once per model (<see cref="AssetTree.Build"/>) and never change, except
/// <see cref="IsExpanded"/>, which the view and the navigator share.
/// </summary>
public sealed class AssetTreeNode : ObservableModel
{
    private readonly List<AssetTreeNode> _children = new();

    /// <summary>ASSETS A3 follow-up (2026-10-24): for a SUBSTATIONS voltage-level group, its base kV (0 for the group of
    /// buses with none), which the fleet page it opens is filtered to; null for every other node.</summary>
    public double? GroupKv { get; internal set; }

    /// <summary>ASSETS A3 follow-up (2026-10-24): for a CABLES &amp; LINES band group, its band (kV), which the fleet page
    /// it opens is filtered to; null for every other node.</summary>
    public int? GroupBand { get; internal set; }

    internal AssetTreeNode(string id, AssetNodeRole role, string label, string detail, string kind, AssetPageKey page,
        Action<AssetExplorerModel>? route, IReadOnlyList<int>? busIds = null, AssetMapTarget? mapTarget = null, AssetKey? key = null,
        bool isAttachment = false)
    {
        IsAttachment = isAttachment;
        Id = id;
        // ASSETS B1: a node made without a key is the landing page, a class or a group, keyed by its section id.
        Key = key ?? role switch
        {
            AssetNodeRole.Home => AssetKey.Home,
            AssetNodeRole.Class => AssetKey.ForClass(id),
            AssetNodeRole.Group => AssetKey.ForGroup(id),
            _ => throw new ArgumentException($"unit node {id} needs its asset key", nameof(key)),
        };
        Role = role;
        Label = label;
        Detail = detail;
        Kind = kind;
        Page = page;
        Route = route;
        BusIds = busIds ?? Array.Empty<int>();
        MapTarget = mapTarget;
    }

    /// <summary>A stable id: the same asset gets the same id when the tree is rebuilt for a new frame's model (for
    /// example "bus:123", "gen:5", "tx:40", "class:GeneratorFleet", "group:subs:400").</summary>
    public string Id { get; }

    /// <summary>Home, class, group or unit.</summary>
    public AssetNodeRole Role { get; }

    /// <summary>What this node stands for (UI programme ASSETS B1): unique in its tree.</summary>
    public AssetKey Key { get; }

    /// <summary>The text the tree shows.</summary>
    public string Label { get; }

    /// <summary>A muted second text (a count, or a unit's index and bus).</summary>
    public string Detail { get; }

    /// <summary>The kind words search matches (for example "generator CCGT", "transformer", "tap changer oltc").</summary>
    public string Kind { get; }

    /// <summary>The page this node opens.</summary>
    public AssetPageKey Page { get; }

    /// <summary>The bus ids this node touches (a bus, a unit's bus, a branch's two ends, an OLTC's controlled bus), for
    /// search by bus id.</summary>
    public IReadOnlyList<int> BusIds { get; }

    /// <summary>Where this asset is on the network map, or null (home, classes and groups).</summary>
    public AssetMapTarget? MapTarget { get; }

    /// <summary>
    /// True for an overlay record shown under a model asset it attaches to (slice 5-view, plan Q12): a second place for a
    /// record whose canonical node is under OVERLAY. It opens the same page; search lists only the canonical node.
    /// </summary>
    public bool IsAttachment { get; }

    /// <summary>The parent node (null for the top level).</summary>
    public AssetTreeNode? Parent { get; private set; }

    /// <summary>The child nodes, in tree order.</summary>
    public IReadOnlyList<AssetTreeNode> Children => _children;

    /// <summary>True when the node has children.</summary>
    public bool HasChildren => _children.Count > 0;

    /// <summary>The A2 route that opens <see cref="Page"/> on an explorer (null for the landing page, which is the
    /// explorer's GoHome).</summary>
    internal Action<AssetExplorerModel>? Route { get; }

    private bool _isExpanded;

    /// <summary>Whether the node's children are shown (the view binds it both ways; the navigator opens the path to a
    /// selected node).</summary>
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    /// <summary>The labels from the top of the tree down to this node, joined by " > " (the crumb).</summary>
    public string Path
    {
        get
        {
            var parts = new List<string>();
            for (AssetTreeNode? n = this; n is not null; n = n.Parent) parts.Add(n.Label);
            parts.Reverse();
            return string.Join(" > ", parts);
        }
    }

    internal AssetTreeNode Add(AssetTreeNode child)
    {
        if (child.Parent is not null) throw new InvalidOperationException($"node {child.Id} already has a parent");
        child.Parent = this;
        _children.Add(child);
        return child;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Id} '{Label}'";
}

/// <summary>
/// The whole explorer tree for one model (UI programme ASSETS A3): its top-level nodes in order, and the lookups the
/// navigator needs (by id, by page, by bus id). See the file header for the shape and its ordering rules.
/// </summary>
public sealed class AssetTree
{
    /// <summary>The top-level labels, as the WPF landing page's card titles spell them (home first).</summary>
    public const string HomeLabel = "ASSETS", SubstationsLabel = "SUBSTATIONS", GeneratorsLabel = "GENERATORS",
        PhasorsLabel = "GENERATOR PHASORS", InterconnectorsLabel = "INTERCONNECTORS", CircuitsLabel = "CABLES & LINES",
        TransformersLabel = "TRANSFORMERS", TapChangersLabel = "TAP CHANGERS";

    /// <summary>The group label of buses (and branches) with no base voltage.</summary>
    public const string NoBaseKvLabel = "no base kV";

    /// <summary>The station label of units without a name (the SchemaEditor's StationIndex.Unnamed).</summary>
    public const string UnnamedStation = "(unnamed)";

    /// <summary>The labels of the B2 class nodes (UI programme ASSETS B2, plan S8), in tree order.</summary>
    public const string DemandsLabel = "DEMANDS", BoundariesLabel = "BOUNDARIES", ProtectionsLabel = "PROTECTIONS",
        AssetClassesLabel = "ASSET CLASSES", OutOfServiceLabel = "OUT OF SERVICE";

    /// <summary>The label of the OVERLAY class node (slice 5-view: the case's asset overlay, carried, not used by the
    /// solver).</summary>
    public const string OverlayLabel = "OVERLAY";

    /// <summary>The label of the group of binding rules under ASSET CLASSES.</summary>
    public const string BindingsLabel = "bindings";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly List<AssetTreeNode> _roots = new();
    private readonly Dictionary<string, AssetTreeNode> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<AssetPageKey, AssetTreeNode> _byPage = new();
    private readonly Dictionary<int, AssetTreeNode> _byBus = new();
    private readonly Dictionary<AssetKey, AssetTreeNode> _byKey = new();

    private AssetTree() { }

    /// <summary>The top-level nodes, in order: ASSETS, SUBSTATIONS, GENERATORS, GENERATOR PHASORS, INTERCONNECTORS,
    /// CABLES &amp; LINES, TRANSFORMERS, TAP CHANGERS.</summary>
    public IReadOnlyList<AssetTreeNode> Roots => _roots;

    /// <summary>The landing-page node.</summary>
    public AssetTreeNode Home => _roots[0];

    /// <summary>How many nodes the tree holds.</summary>
    public int Count => _byId.Count;

    /// <summary>The node with <paramref name="id"/>, or null.</summary>
    public AssetTreeNode? ById(string id) => _byId.GetValueOrDefault(id);

    /// <summary>
    /// The node that stands for <paramref name="page"/>: the home node, the class node of a fleet page, the unit node of
    /// a unit page. A group node or a tap-changer node is never the canonical node (they open a page another node owns).
    /// Null when no node opens that page (a unit page opened by a key the tree does not hold, for example a transformer
    /// opened by name only, or a generator page opened for an interconnector unit from a substation's plant list).
    /// </summary>
    public AssetTreeNode? NodeFor(AssetPageKey? page) => page is null ? null : _byPage.GetValueOrDefault(page);

    /// <summary>The node whose <see cref="AssetTreeNode.Key"/> is <paramref name="key"/>, or null (ASSETS B1).</summary>
    public AssetTreeNode? NodeFor(AssetKey? key) => key is null ? null : _byKey.GetValueOrDefault(key);

    /// <summary>The substation node of bus <paramref name="busId"/>, or null.</summary>
    public AssetTreeNode? Substation(int busId) => _byBus.GetValueOrDefault(busId);

    /// <summary>True when <paramref name="node"/> belongs to this tree.</summary>
    public bool Contains(AssetTreeNode node) => node is not null && _byId.TryGetValue(node.Id, out AssetTreeNode? n) && ReferenceEquals(n, node);

    /// <summary>Every node, depth first in tree order.</summary>
    public IEnumerable<AssetTreeNode> PreOrder()
    {
        var stack = new Stack<AssetTreeNode>();
        for (int i = _roots.Count - 1; i >= 0; i--) stack.Push(_roots[i]);
        while (stack.Count > 0)
        {
            AssetTreeNode n = stack.Pop();
            yield return n;
            for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
        }
    }

    // Register a node under its id (unique, or the build is wrong and fails loud) and, when it is the canonical node of
    // its page, under its page.
    private AssetTreeNode Reg(AssetTreeNode n, bool canonical)
    {
        if (!_byId.TryAdd(n.Id, n)) throw new InvalidOperationException($"two asset tree nodes share the id {n.Id}");
        if (!_byKey.TryAdd(n.Key, n)) throw new InvalidOperationException($"two asset tree nodes share the key {n.Key}");
        if (canonical && !_byPage.TryAdd(n.Page, n))
            throw new InvalidOperationException($"two asset tree nodes open the same page {n.Page}");
        return n;
    }

    /// <summary>The tree before any frame: the landing page and the seven empty classes.</summary>
    public static AssetTree Empty() => Build(null);

    /// <summary>The tree of <paramref name="m"/> (null: the empty classes only).</summary>
    public static AssetTree Build(GridModel? m) => Build(m, null);

    /// <summary>
    /// The tree of <paramref name="m"/> and, with an <paramref name="assetIndex"/> built for that very model, the B2 class
    /// nodes that have members after the A3 classes (UI programme ASSETS B2; see the file header).
    /// </summary>
    public static AssetTree Build(GridModel? m, AssetIndex? assetIndex)
    {
        if (assetIndex is not null && !ReferenceEquals(assetIndex.Model, m))
            throw new ArgumentException("The asset index was built for another model.", nameof(assetIndex));
        var t = new AssetTree();
        t._roots.Add(t.Reg(new AssetTreeNode("home", AssetNodeRole.Home, HomeLabel, "every asset class in the running model",
            "home assets", AssetPageKey.Home, null), canonical: true));

        // ---- the classes, each opening its fleet page by the landing card's route ----
        AssetTreeNode Class(AssetPageKind kind, string label, string kindWords, Func<AssetExplorerModel, AssetPageModel> create, int count)
        {
            AssetTreeNode n = t.Reg(new AssetTreeNode("class:" + kind, AssetNodeRole.Class, label, Units(count), kindWords,
                AssetPageKey.Fleet(kind), x => x.Open(create(x))), canonical: true);
            t._roots.Add(n);
            return n;
        }

        IReadOnlyList<Bus> buses = m?.Buses ?? Array.Empty<Bus>();
        IReadOnlyList<Generator> gens = m?.Generators ?? Array.Empty<Generator>();
        IReadOnlyList<Branch> branches = m?.Branches ?? Array.Empty<Branch>();
        IReadOnlyList<Oltc> oltcs = m?.Oltcs ?? Array.Empty<Oltc>();

        // The first bus per id (as every page's first-wins lookup), for names, base voltages and bands.
        var busById = new Dictionary<int, Bus>(buses.Count);
        foreach (Bus b in buses) busById.TryAdd(b.Id, b);
        string BusName(int id) => busById.TryGetValue(id, out Bus? b) && b.Name is not null ? b.Name : $"bus {id}";

        bool IsLink(Generator g) => AssetIndex.IsLink(g);   // the one link rule (bug #28)
        int linkCount = gens.Count(IsLink);
        int plantCount = gens.Count(AssetIndex.IsPlant);    // the GENERATORS page's members (bug #28: no grid infeeds)

        AssetTreeNode subs = Class(AssetPageKind.SubstationFleet, SubstationsLabel, "class substations buses", x => x.CreateSubstationFleet(), busById.Count);
        AssetTreeNode genClass = Class(AssetPageKind.GeneratorFleet, GeneratorsLabel, "class generators", x => x.CreateGeneratorFleet(), plantCount);
        Class(AssetPageKind.PhasorFleet, PhasorsLabel, "class generator phasors", x => x.CreatePhasorFleet(), plantCount);
        AssetTreeNode links = Class(AssetPageKind.InterconnectorFleet, InterconnectorsLabel, "class interconnectors", x => x.CreateInterconnectorFleet(), linkCount);
        AssetTreeNode circuits = Class(AssetPageKind.CircuitFleet, CircuitsLabel, "class cables lines circuits", x => x.CreateCircuitFleet(), branches.Count(b => !b.IsTransformer));
        AssetTreeNode txs = Class(AssetPageKind.TransformerFleet, TransformersLabel, "class transformers", x => x.CreateTransformerFleet(), branches.Count(b => b.IsTransformer));
        AssetTreeNode taps = Class(AssetPageKind.TapChangerFleet, TapChangersLabel, "class tap changers oltc", x => x.CreateTapChangerFleet(), oltcs.Count);
        if (m is null) return t;

        // A group node: a way through the tree that opens its class's fleet page (the class node's route).
        AssetTreeNode Group(AssetTreeNode cls, string id, string label, int count, string kindWords) =>
            cls.Add(t.Reg(new AssetTreeNode(id, AssetNodeRole.Group, label, Units(count), kindWords, cls.Page, cls.Route), canonical: false));

        // Grid infeeds (bug #28): neither plant nor links, so they sit on neither the GENERATORS nor the INTERCONNECTORS
        // class. Each is listed under the substation bus it feeds (the model boundary it stands for), so it is still
        // reached from the tree, from search and from its substation page's "unit on this bus" row.
        var infeedsByBus = new Dictionary<int, List<int>>();
        for (int i = 0; i < gens.Count; i++)
        {
            if (!FuelTaxonomy.IsGridInfeed(gens[i].Type)) continue;
            if (!infeedsByBus.TryGetValue(gens[i].Bus, out List<int>? list)) infeedsByBus[gens[i].Bus] = list = new List<int>();
            list.Add(i);
        }

        // ---- SUBSTATIONS: by base voltage (highest first, none last), buses in model order, one per id ----
        var seenBus = new HashSet<int>();
        var byKv = new List<(double Kv, List<Bus> Members)>();
        var kvIndex = new Dictionary<double, int>();
        foreach (Bus b in buses)
        {
            if (!seenBus.Add(b.Id)) continue;
            double kv = b.BaseKv > 0 ? b.BaseKv : 0;
            if (!kvIndex.TryGetValue(kv, out int at)) { at = byKv.Count; kvIndex[kv] = at; byKv.Add((kv, new List<Bus>())); }
            byKv[at].Members.Add(b);
        }
        foreach ((double kv, List<Bus> members) in byKv.OrderByDescending(g => g.Kv))
        {
            string label = kv > 0 ? string.Create(Inv, $"{kv:F0} kV") : NoBaseKvLabel;
            AssetTreeNode g = Group(subs, "group:subs:" + kv.ToString("R", Inv), label, members.Count, "voltage level substations");
            g.GroupKv = kv;   // the SUBSTATIONS page opens filtered to this level (A3 follow-up)
            foreach (Bus b in members)
            {
                int id = b.Id;
                AssetTreeNode n = g.Add(t.Reg(new AssetTreeNode("bus:" + id.ToString(Inv), AssetNodeRole.Unit, b.Name ?? $"bus {id}",
                    "bus " + id.ToString(Inv), "substation bus", AssetPageKey.Substation(id), x => x.OpenSubstation(id),
                    new[] { id }, new AssetMapTarget(id, null), AssetKey.Substation(id)), canonical: true));
                t._byBus[id] = n;
                if (infeedsByBus.TryGetValue(id, out List<int>? infeeds))
                    foreach (int i in infeeds)
                    {
                        Generator infeed = gens[i];
                        int index = i, bus = infeed.Bus;
                        string? name = infeed.Name;
                        n.Add(t.Reg(new AssetTreeNode("gen:" + i.ToString(Inv), AssetNodeRole.Unit, name ?? $"grid infeed @ bus {bus}",
                            string.Create(Inv, $"#{i}  bus {bus}"), "grid infeed unit", AssetPageKey.Generator(index, bus, name),
                            x => x.OpenGenerator(index, bus, name), new[] { bus }, new AssetMapTarget(bus, null),
                            AssetKey.Unit(m, i, isLink: false)), canonical: true));
                    }
            }
        }

        // ---- GENERATORS: fuel > station > unit (interconnectors are their own class, as on the GENERATORS page) ----
        var fuels = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < gens.Count; i++)
        {
            if (!AssetIndex.IsPlant(gens[i])) continue;
            string fuel = UiFuels.Classify(gens[i].Type).Name;
            if (!fuels.TryGetValue(fuel, out List<int>? list)) fuels[fuel] = list = new List<int>();
            list.Add(i);
        }
        foreach ((string fuel, List<int> units) in fuels.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            AssetTreeNode fg = Group(genClass, "group:gen:" + fuel, fuel, units.Count, "fuel generators");
            // Stations: the units sharing a name; ordinal by name, the unnamed last, ties by the first unit's index.
            IEnumerable<IGrouping<string?, int>> stations = units
                .GroupBy(i => gens[i].Name, StringComparer.Ordinal)
                .OrderBy(s => s.Key is null ? 1 : 0)
                .ThenBy(s => s.Key ?? "", StringComparer.Ordinal)
                .ThenBy(s => s.First());
            foreach (IGrouping<string?, int> st in stations)
            {
                List<int> members = st.ToList();
                AssetTreeNode sn = fg.Add(t.Reg(new AssetTreeNode("station:" + members[0].ToString(Inv), AssetNodeRole.Group,
                    st.Key ?? UnnamedStation, Units(members.Count), "station generators " + fuel, genClass.Page, genClass.Route), canonical: false));
                foreach (int i in members)
                {
                    Generator g = gens[i];
                    int index = i, bus = g.Bus;
                    string? name = g.Name;
                    sn.Add(t.Reg(new AssetTreeNode("gen:" + i.ToString(Inv), AssetNodeRole.Unit, name ?? $"unit @ bus {bus}",
                        string.Create(Inv, $"#{i}  bus {bus}"), "generator unit " + fuel, AssetPageKey.Generator(index, bus, name),
                        x => x.OpenGenerator(index, bus, name), new[] { bus }, new AssetMapTarget(bus, null),
                        AssetKey.Unit(m, i, isLink: false)), canonical: true));
                }
            }
        }

        // ---- INTERCONNECTORS: the links, in model order ----
        for (int i = 0; i < gens.Count; i++)
        {
            if (!IsLink(gens[i])) continue;
            Generator g = gens[i];
            int index = i, bus = g.Bus;
            string? name = g.Name;
            links.Add(t.Reg(new AssetTreeNode("ic:" + i.ToString(Inv), AssetNodeRole.Unit, name ?? $"link @ bus {bus}",
                string.Create(Inv, $"#{i}  bus {bus}"), "interconnector link", AssetPageKey.Interconnector(index, bus, name),
                x => x.OpenInterconnector(index, bus, name), new[] { bus }, new AssetMapTarget(bus, null),
                AssetKey.Unit(m, i, isLink: true)), canonical: true));
        }

        // ---- CABLES & LINES: by the CABLES & LINES page's band (highest first), circuits in model order ----
        double KvOf(int id) => busById.TryGetValue(id, out Bus? b) ? b.BaseKv : 0;
        var bands = new SortedDictionary<int, List<int>>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
        for (int i = 0; i < branches.Count; i++)
        {
            Branch b = branches[i];
            if (b.IsTransformer) continue;
            int band = CircuitInfo.Band(Math.Max(KvOf(b.From), KvOf(b.To)));
            if (!bands.TryGetValue(band, out List<int>? list)) bands[band] = list = new List<int>();
            list.Add(i);
        }
        foreach ((int band, List<int> members) in bands)
        {
            AssetTreeNode g = Group(circuits, "group:circuits:" + band.ToString(Inv), CircuitInfo.Label(band), members.Count, "voltage band circuits");
            g.GroupBand = band;   // the CABLES & LINES page opens filtered to this band (A3 follow-up)
            foreach (int i in members)
            {
                Branch b = branches[i];
                int index = i;
                g.Add(t.Reg(new AssetTreeNode("circuit:" + i.ToString(Inv), AssetNodeRole.Unit, b.Name ?? $"branch {i}",
                    string.Create(Inv, $"#{i}  {BusName(b.From)} -> {BusName(b.To)}"), "circuit line cable",
                    AssetPageKey.Circuit(i, b.From, b.To, b.Name), x => x.OpenCircuit(index, b), new[] { b.From, b.To },
                    new AssetMapTarget(null, i), AssetKey.Branch(m, i)), canonical: true));
            }
        }

        // ---- TRANSFORMERS: by the TRANSFORMERS page's "hi->lo kV" band, transformers in model order ----
        var txBands = new List<(double Hi, double Lo, string Label, List<int> Members)>();
        var txBandIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < branches.Count; i++)
        {
            Branch b = branches[i];
            if (!b.IsTransformer) continue;
            string label = TransformerInfo.Band(busById, b);
            if (!txBandIndex.TryGetValue(label, out int at))
            {
                double hi = KvOf(b.From), lo = KvOf(b.To);
                if (lo > hi) (hi, lo) = (lo, hi);
                at = txBands.Count;
                txBandIndex[label] = at;
                txBands.Add((hi, lo, label, new List<int>()));
            }
            txBands[at].Members.Add(i);
        }
        foreach (var (_, _, label, members) in txBands
                     .OrderBy(g => g.Label == " - " ? 1 : 0).ThenByDescending(g => g.Hi).ThenByDescending(g => g.Lo)
                     .ThenBy(g => g.Label, StringComparer.Ordinal))
        {
            AssetTreeNode g = Group(txs, "group:tx:" + label, label == " - " ? NoBaseKvLabel : label, members.Count, "voltage band transformers");
            foreach (int i in members)
            {
                Branch b = branches[i];
                int index = i;
                g.Add(t.Reg(new AssetTreeNode("tx:" + i.ToString(Inv), AssetNodeRole.Unit, b.Name ?? $"TX {b.From}-{b.To}",
                    string.Create(Inv, $"#{i}  {BusName(b.From)} -> {BusName(b.To)}"), "transformer",
                    AssetPageKey.Transformer(i, b.From, b.To, b.Name), x => x.OpenTransformer(index, b), new[] { b.From, b.To },
                    new AssetMapTarget(null, i), AssetKey.Branch(m, i)), canonical: true));
            }
        }

        // ---- TAP CHANGERS: the OLTC records in model order; each opens what its TAP CHANGERS row opens ----
        int[] driven = SteadyStateSolver.MapOltcRecordsToBranches(m);
        for (int k = 0; k < oltcs.Count; k++)
        {
            Oltc o = oltcs[k];
            int branchIndex = driven[k];
            Branch? drivenBranch = branchIndex >= 0 ? branches[branchIndex] : null;
            string txName = o.Transformer;
            // The page the row's click opens: the driven transformer by index, else the transformer named (index -1).
            AssetPageKey page = drivenBranch is not null
                ? AssetPageKey.Transformer(branchIndex, drivenBranch.From, drivenBranch.To, drivenBranch.Name)
                : AssetPageKey.Transformer(-1, 0, 0, txName);
            Action<AssetExplorerModel> route = x =>
            {
                if (drivenBranch is not null) x.OpenTransformer(branchIndex, drivenBranch);
                else x.OpenTransformerNamed(txName);   // no branch carries the name: the page says so (the row's route)
            };
            var map = drivenBranch is not null ? new AssetMapTarget(null, branchIndex) : new AssetMapTarget(o.ControlsBus, null);
            taps.Add(t.Reg(new AssetTreeNode("oltc:" + k.ToString(Inv), AssetNodeRole.Unit, txName,
                string.Create(Inv, $"#{k}  regulates {BusName(o.ControlsBus)}"), "tap changer oltc", page, route,
                drivenBranch is not null ? new[] { o.ControlsBus, drivenBranch.From, drivenBranch.To } : new[] { o.ControlsBus },
                map, AssetKey.TapChanger(m, k)), canonical: false));
        }
        if (assetIndex is not null) t.AddOverviewClasses(m, assetIndex);
        if (assetIndex?.Overlay is { HasRecords: true } overlay) t.AddOverlay(assetIndex, overlay);
        return t;
    }

    // ---- UI programme ASSETS B2: the class nodes of plan S8, each shown only when it has members ----
    private void AddOverviewClasses(GridModel m, AssetIndex ix)
    {
        // A node that opens a B2 overview page: the route makes the page and has the explorer record its key, so a page
        // opened this way is found again by the navigator like any A2 page.
        static Action<AssetExplorerModel> Opens(AssetPageKey page, AssetIndex ix) => x =>
            (x as NavigableAssetExplorer ?? throw new InvalidOperationException("The B2 pages open only on a NavigableAssetExplorer."))
                .OpenTracked(new AssetOverviewPage(page, ix), page);

        AssetTreeNode Class(AssetPageKind kind, string label, string kindWords, int count)
        {
            AssetPageKey page = AssetPageKey.OverviewClass(kind);
            AssetTreeNode n = Reg(new AssetTreeNode("class:" + kind, AssetNodeRole.Class, label, Units(count), kindWords, page,
                Opens(page, ix)), canonical: true);
            _roots.Add(n);
            return n;
        }

        void Unit(AssetTreeNode parent, string id, string label, string detail, string kindWords, AssetPageKey page,
            AssetKey key, int[] busIds, AssetMapTarget? map) =>
            parent.Add(Reg(new AssetTreeNode(id, AssetNodeRole.Unit, label, detail, kindWords, page, Opens(page, ix), busIds, map, key),
                canonical: true));

        // DEMANDS: the case's demand records (each opens its record's page; on the map, the bus it folds into).
        IReadOnlyList<DemandRecord> demands = ix.Demands;
        if (demands.Count > 0)
        {
            AssetTreeNode cls = Class(AssetPageKind.DemandFleet, DemandsLabel, "class demands demand records loads", demands.Count);
            foreach (DemandRecord d in demands)
                Unit(cls, "demand:" + d.Ordinal.ToString(Inv), d.Name ?? UnnamedStation,
                    string.Create(Inv, $"#{d.Ordinal}  bus {d.Bus}"), "demand record load",
                    new AssetPageKey(AssetPageKind.Demand, d.Ordinal, d.Bus, 0, d.Name), AssetIndex.DemandKey(d), new[] { d.Bus },
                    new AssetMapTarget(d.Bus, null));
        }

        // BOUNDARIES: the model's boundaries.
        if (m.Boundaries.Count > 0)
        {
            AssetTreeNode cls = Class(AssetPageKind.BoundaryFleet, BoundariesLabel, "class boundaries boundary", m.Boundaries.Count);
            for (int k = 0; k < m.Boundaries.Count; k++)
            {
                Boundary b = m.Boundaries[k];
                Unit(cls, "boundary:" + k.ToString(Inv), b.Name, string.Create(Inv, $"#{k}  {b.Branches.Count} branches"), "boundary",
                    new AssetPageKey(AssetPageKind.Boundary, k, 0, 0, b.Name), ix.BoundaryKey(k), Array.Empty<int>(), null);
            }
        }

        // PROTECTIONS: the placed devices (on the map, the bus each monitors).
        if (ix.Protections.Count > 0)
        {
            AssetTreeNode cls = Class(AssetPageKind.ProtectionFleet, ProtectionsLabel, "class protections protection relay", ix.Protections.Count);
            for (int k = 0; k < ix.Protections.Count; k++)
            {
                var p = ix.Protections[k];
                Unit(cls, "protection:" + k.ToString(Inv), p.Name, string.Create(Inv, $"#{k}  bus {p.MonitorBus}"), "protection " + p.ClassName,
                    new AssetPageKey(AssetPageKind.Protection, k, p.MonitorBus, 0, p.Name), ix.ProtectionKey(k), new[] { p.MonitorBus },
                    new AssetMapTarget(p.MonitorBus, null));
            }
        }

        // ASSET CLASSES: the classes in library order, then the group of binding rules.
        if (ix.Classes.Count > 0 || ix.Bindings.Count > 0)
        {
            AssetTreeNode cls = Class(AssetPageKind.AssetClassFleet, AssetClassesLabel, "class asset classes dynamics", ix.Classes.Count);
            for (int k = 0; k < ix.Classes.Count; k++)
            {
                AssetClass c = ix.Classes[k];
                Unit(cls, "assetclass:" + k.ToString(Inv), c.Name, string.Create(Inv, $"#{k}  {c.Kind}"), "asset class " + c.Kind,
                    new AssetPageKey(AssetPageKind.AssetClass, k, 0, 0, c.Name), ix.ClassKey(k), Array.Empty<int>(), null);
            }
            if (ix.Bindings.Count > 0)
            {
                AssetTreeNode g = cls.Add(Reg(new AssetTreeNode("group:bindings", AssetNodeRole.Group, BindingsLabel, Units(ix.Bindings.Count),
                    "bindings binding rules", cls.Page, cls.Route), canonical: false));
                for (int k = 0; k < ix.Bindings.Count; k++)
                {
                    AssetBinding b = ix.Bindings[k];
                    Unit(g, "binding:" + k.ToString(Inv), b.MatchType, "-> " + b.ClassName, "binding rule " + b.ClassName,
                        new AssetPageKey(AssetPageKind.AssetBinding, k, 0, 0, b.MatchType), ix.BindingKey(k), Array.Empty<int>(), null);
                }
            }
        }

        // OUT OF SERVICE: the units marked out of service, in fleet order (on the map, the bus each would connect to).
        int oos = m.AllGenerators.Count(g => !g.InService);
        if (oos > 0)
        {
            AssetTreeNode cls = Class(AssetPageKind.OutOfServiceFleet, OutOfServiceLabel, "class out of service units", oos);
            for (int k = 0; k < m.AllGenerators.Count; k++)
            {
                Generator g = m.AllGenerators[k];
                if (g.InService) continue;
                Unit(cls, "oos:" + k.ToString(Inv), g.Name ?? $"unit @ bus {g.Bus}", string.Create(Inv, $"#{k}  bus {g.Bus}"),
                    "out of service unit " + g.Type, new AssetPageKey(AssetPageKind.OutOfServiceUnit, k, g.Bus, 0, g.Name),
                    AssetKey.OutOfService(m, k), new[] { g.Bus }, new AssetMapTarget(g.Bus, null));
            }
        }
    }

    // ---- UI programme ASSETS slice 5-view: the overlay, by kind and under the model assets (see the file header) ----
    private void AddOverlay(AssetIndex ix, OverlayIndex ov)
    {
        static Action<AssetExplorerModel> Opens(AssetPageKey page, AssetIndex ix) => x =>
            (x as NavigableAssetExplorer ?? throw new InvalidOperationException("The overlay pages open only on a NavigableAssetExplorer."))
                .OpenTracked(new AssetOverviewPage(page, ix), page);

        // OVERLAY > kind > record: the canonical node of every record.
        AssetTreeNode cls = Reg(new AssetTreeNode("class:" + AssetPageKind.OverlayFleet, AssetNodeRole.Class, OverlayLabel,
            Units(ov.Entries.Count), "class overlay asset overlay records carried not used by the solver", OverlayIndex.FleetPage,
            Opens(OverlayIndex.FleetPage, ix)), canonical: true);
        _roots.Add(cls);
        foreach (OverlayKindGroup g in ov.ByKind)
        {
            AssetPageKey kindPage = OverlayIndex.KindPage(g.Kind);
            AssetTreeNode kind = cls.Add(Reg(new AssetTreeNode("group:overlay:" + g.Kind, AssetNodeRole.Group, g.Title,
                Units(g.Records.Count), "overlay kind " + g.Kind, kindPage, Opens(kindPage, ix)), canonical: true));
            foreach (OverlayEntry e in g.Records)
                kind.Add(Reg(new AssetTreeNode("overlay:" + e.Id, AssetNodeRole.Unit, e.Id, e.KindTitle,
                    "overlay record " + e.Kind + " " + e.KindTitle, e.Page, Opens(e.Page, ix), key: e.Key), canonical: true));
        }

        // Under each model asset node: the records it holds, each with its child subtree (attachment nodes).
        foreach (AssetKey asset in ov.AttachedAssets)
        {
            if (NodeFor(asset) is not AssetTreeNode host) continue;   // an asset the tree has no node for (none expected)
            foreach (OverlayEntry root in ov.RootsUnder(asset)) AddAttachment(host, root, host.Id);
        }

        void AddAttachment(AssetTreeNode parent, OverlayEntry e, string placePrefix)
        {
            string id = placePrefix + "/overlay:" + e.Id;
            AssetTreeNode n = parent.Add(Reg(new AssetTreeNode(id, AssetNodeRole.Unit, e.Id, e.KindTitle,
                "overlay record " + e.Kind + " " + e.KindTitle, e.Page, Opens(e.Page, ix),
                key: AssetKey.ForOverlay(new OverlayRef(e.Id, id)), isAttachment: true), canonical: false));
            foreach (OverlayEntry c in e.Children) AddAttachment(n, c, id);
        }
    }

    private static string Units(int n) => n.ToString(Inv);

    /// <summary>
    /// True when <paramref name="a"/> and <paramref name="b"/> give the same tree: the same buses (id, name, base kV),
    /// generators (bus, name, type), branches (ends, name, transformer or not) and OLTC records (transformer, controlled
    /// bus), in the same order. A frame whose model is a new object with the same structure keeps the tree (and the
    /// view's expansion and selection) as it is.
    /// </summary>
    public static bool SameStructure(GridModel? a, GridModel? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.Buses.Count != b.Buses.Count || a.Generators.Count != b.Generators.Count
            || a.Branches.Count != b.Branches.Count || a.Oltcs.Count != b.Oltcs.Count) return false;
        for (int i = 0; i < a.Buses.Count; i++)
        {
            Bus x = a.Buses[i], y = b.Buses[i];
            if (x.Id != y.Id || !string.Equals(x.Name, y.Name, StringComparison.Ordinal) || !x.BaseKv.Equals(y.BaseKv)) return false;
        }
        for (int i = 0; i < a.Generators.Count; i++)
        {
            Generator x = a.Generators[i], y = b.Generators[i];
            if (x.Bus != y.Bus || !string.Equals(x.Name, y.Name, StringComparison.Ordinal) || !string.Equals(x.Type, y.Type, StringComparison.Ordinal)) return false;
        }
        for (int i = 0; i < a.Branches.Count; i++)
        {
            Branch x = a.Branches[i], y = b.Branches[i];
            if (x.From != y.From || x.To != y.To || x.IsTransformer != y.IsTransformer || !string.Equals(x.Name, y.Name, StringComparison.Ordinal)) return false;
        }
        for (int i = 0; i < a.Oltcs.Count; i++)
        {
            Oltc x = a.Oltcs[i], y = b.Oltcs[i];
            if (x.ControlsBus != y.ControlsBus || !string.Equals(x.Transformer, y.Transformer, StringComparison.Ordinal)) return false;
        }
        return true;
    }
}
