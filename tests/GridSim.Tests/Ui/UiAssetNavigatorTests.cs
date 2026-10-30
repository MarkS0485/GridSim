using System.Collections;
using System.Globalization;
using System.Reflection;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.Presentation;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Assets.Pages;
using GridSim.Ui.Commands;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Diagrams;
using GridSim.Ui.Hosting;
using GridSim.Ui.Models;
using GridSim.Ui.Presentation;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme ASSETS A3: the ASSETS tab's navigator (GridSim.Ui.Assets.Navigation, UI-neutral). It is new code, so these
/// are new tests (no WPF oracle for the tree): the tree's shape for the shipped MATPOWER case9, the reduced GB case and
/// gb-synthetic (every bus, generator, link, circuit, transformer and OLTC exactly once, under the right class, in the
/// stated order, with ties broken the stated way, the same ids built twice); search (name, kind words, bus id, ignoring
/// case; the match cap and its note; Enter's "go to bus id"); back / forward over the same page objects; and, the
/// load-bearing one, that selecting a node opens exactly the A2 page the WPF route opens: the same page type and, frame
/// after frame, every public value equal (strings ordinal, numbers by their bits, colour tokens by their bytes, every row
/// of every list), against a plain A2 AssetExplorerModel driven by the WPF call for the same click.
/// </summary>
public sealed class UiAssetNavigatorTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---------------------------------------------------------------- frames

    private static ModelSource SourceOf(string name)
    {
        if (name == "case9")
        {
            string file = Path.Combine(AppContext.BaseDirectory, "data", "matpower", "lib", "case9.m");
            Assert.True(File.Exists(file), $"bundled case not found: {file}");
            return new ModelSource("MATPOWER  case9", Path.GetDirectoryName(file)!, file);
        }
        string dir = Path.Combine(AppContext.BaseDirectory, "data", name);
        Assert.True(File.Exists(Path.Combine(dir, "system.json")), $"bundled case not found: {dir}");
        return new ModelSource(name, dir);
    }

    /// <summary>A live host over a bundled case, and the dashboard that stamps its frames with the clock.</summary>
    private sealed class Feed
    {
        private readonly SimHost _host = new();
        private readonly DashboardModel _dash = new();

        public Feed(string name)
        {
            LiveLoadResult loaded = _host.LoadLive(SourceOf(name), SolverKind.Auto, new LicenceGate(Entitlement.FreeDefault));
            Assert.True(loaded.Loaded, loaded.Refusal);
        }

        public (SimFrame Frame, string Clock) Next()
        {
            HostFrame hf = Assert.IsType<HostFrame>(_host.ProduceNext());
            Assert.Equal(HostFrameKind.LiveSolved, hf.Kind);
            _dash.Update(hf.Frame!, hf.Boundaries, hf.SolveMs);
            return (hf.Frame!, _dash.Clock);
        }
    }

    private static readonly Dictionary<string, (SimFrame Frame, string Clock)[]> FrameCache = new(StringComparer.Ordinal);

    // Three solved frames of a case (cached per test run: the cases are solved once).
    private static (SimFrame Frame, string Clock)[] Frames(string name)
    {
        lock (FrameCache)
        {
            if (FrameCache.TryGetValue(name, out var cached)) return cached;
            var feed = new Feed(name);
            var frames = new[] { feed.Next(), feed.Next(), feed.Next() };
            FrameCache[name] = frames;
            return frames;
        }
    }

    public static TheoryData<string> Cases => new() { "case9", "uk", "gb-synthetic" };

    // ---------------------------------------------------------------- the tree's shape

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_tree_holds_every_asset_once_under_its_class_in_the_stated_order(string name)
    {
        GridModel m = Frames(name)[0].Frame.Model;
        AssetTree t = AssetTree.Build(m);

        // The top level: the landing page and the seven classes, as the WPF landing cards name them.
        Assert.Equal(new[] { "ASSETS", "SUBSTATIONS", "GENERATORS", "GENERATOR PHASORS", "INTERCONNECTORS", "CABLES & LINES", "TRANSFORMERS", "TAP CHANGERS" },
            t.Roots.Select(r => r.Label));
        Assert.Equal(AssetNodeRole.Home, t.Roots[0].Role);
        Assert.All(t.Roots.Skip(1), r => Assert.Equal(AssetNodeRole.Class, r.Role));
        Assert.Equal(new[] { AssetPageKind.Home, AssetPageKind.SubstationFleet, AssetPageKind.GeneratorFleet, AssetPageKind.PhasorFleet,
            AssetPageKind.InterconnectorFleet, AssetPageKind.CircuitFleet, AssetPageKind.TransformerFleet, AssetPageKind.TapChangerFleet },
            t.Roots.Select(r => r.Page.Kind));
        AssetTreeNode subs = t.Roots[1], gens = t.Roots[2], phasors = t.Roots[3], links = t.Roots[4], circuits = t.Roots[5], txs = t.Roots[6], taps = t.Roots[7];

        // ---- counts, computed here independently of the tree ----
        bool Link(Generator g) => UiFuels.Classify(g.Type).Category == FuelCategory.Interconnector;
        int distinctBuses = m.Buses.Select(b => b.Id).Distinct().Count();
        int linkCount = m.Generators.Count(Link);
        Assert.Equal(distinctBuses.ToString(Inv), subs.Detail);
        Assert.Equal((m.Generators.Count - linkCount).ToString(Inv), gens.Detail);
        Assert.Equal((m.Generators.Count - linkCount).ToString(Inv), phasors.Detail);
        Assert.Equal(linkCount.ToString(Inv), links.Detail);
        Assert.Equal(m.Branches.Count(b => !b.IsTransformer).ToString(Inv), circuits.Detail);
        Assert.Equal(m.Branches.Count(b => b.IsTransformer).ToString(Inv), txs.Detail);
        Assert.Equal(m.Oltcs.Count.ToString(Inv), taps.Detail);
        Assert.Empty(phasors.Children);
        Assert.True(m.Generators.Count > 0 && m.Branches.Count > 0, "a case with no plant or branches proves nothing");

        // ---- every asset exactly once, as a unit node opening its own page ----
        List<AssetTreeNode> busUnits = Units(subs);
        Assert.Equal(m.Buses.Select(b => b.Id).Distinct().OrderBy(i => i), busUnits.Select(n => n.Page.BusA).OrderBy(i => i));
        Assert.All(busUnits, n => Assert.Equal(AssetPageKind.Substation, n.Page.Kind));
        List<AssetTreeNode> genUnits = Units(gens);
        Assert.Equal(Enumerable.Range(0, m.Generators.Count).Where(i => !Link(m.Generators[i])), genUnits.Select(n => n.Page.Index).OrderBy(i => i));
        foreach (AssetTreeNode n in genUnits)
        {
            Generator g = m.Generators[n.Page.Index];
            Assert.Equal(AssetPageKey.Generator(n.Page.Index, g.Bus, g.Name), n.Page);
            Assert.Equal(g.Name ?? $"unit @ bus {g.Bus}", n.Label);   // the GENERATORS row's label
        }
        List<AssetTreeNode> linkUnits = Units(links);
        Assert.Equal(Enumerable.Range(0, m.Generators.Count).Where(i => Link(m.Generators[i])), linkUnits.Select(n => n.Page.Index));
        Assert.All(linkUnits, n => Assert.Equal(AssetPageKind.Interconnector, n.Page.Kind));
        List<AssetTreeNode> circuitUnits = Units(circuits);
        Assert.Equal(Enumerable.Range(0, m.Branches.Count).Where(i => !m.Branches[i].IsTransformer), circuitUnits.Select(n => n.Page.Index).OrderBy(i => i));
        foreach (AssetTreeNode n in circuitUnits)
        {
            Branch b = m.Branches[n.Page.Index];
            Assert.Equal(AssetPageKey.Circuit(n.Page.Index, b.From, b.To, b.Name), n.Page);
            Assert.Equal(new AssetMapTarget(null, n.Page.Index), n.MapTarget);
        }
        List<AssetTreeNode> txUnits = Units(txs);
        Assert.Equal(Enumerable.Range(0, m.Branches.Count).Where(i => m.Branches[i].IsTransformer), txUnits.Select(n => n.Page.Index).OrderBy(i => i));
        Assert.All(txUnits, n => Assert.Equal(AssetPageKind.Transformer, n.Page.Kind));
        Assert.Equal(m.Oltcs.Count, taps.Children.Count);
        int[] driven = SteadyStateSolver.MapOltcRecordsToBranches(m);
        for (int k = 0; k < m.Oltcs.Count; k++)
        {
            AssetTreeNode n = taps.Children[k];   // in record order
            Assert.Equal("oltc:" + k.ToString(Inv), n.Id);
            Assert.Equal(m.Oltcs[k].Transformer, n.Label);
            Assert.Equal(driven[k], n.Page.Index);   // the transformer the solver drives, as the TAP CHANGERS row opens
        }

        // ---- ordering and stable ties ----
        // Substations: base voltage groups strictly descending, the no-base-kV group last; buses in model order.
        double[] kvs = subs.Children.Select(g => g.Label == AssetTree.NoBaseKvLabel ? 0.0 : double.Parse(g.Label[..^3], Inv)).ToArray();
        for (int i = 1; i < kvs.Length; i++)
            Assert.True(kvs[i] < kvs[i - 1], $"{name}: substation groups out of order at {i}: {kvs[i - 1]} then {kvs[i]}");
        var firstAt = new Dictionary<int, int>();
        for (int i = 0; i < m.Buses.Count; i++) firstAt.TryAdd(m.Buses[i].Id, i);
        foreach (AssetTreeNode g in subs.Children)
            AssertAscending(g.Children.Select(n => firstAt[n.Page.BusA]), $"{name}: buses of {g.Label}");
        // Generators: fuels ordinal; stations ordinal with the unnamed last; units in model order.
        AssertOrdinalAscending(gens.Children.Select(g => g.Label), $"{name}: fuels");
        foreach (AssetTreeNode fuel in gens.Children)
        {
            var named = fuel.Children.Where(s => s.Label != AssetTree.UnnamedStation).Select(s => s.Label).ToList();
            AssertOrdinalAscending(named, $"{name}: stations of {fuel.Label}");
            int unnamedAt = fuel.Children.ToList().FindIndex(s => s.Label == AssetTree.UnnamedStation && Units(s).All(u => m.Generators[u.Page.Index].Name is null));
            if (unnamedAt >= 0) Assert.Equal(fuel.Children.Count - 1, unnamedAt);
            foreach (AssetTreeNode st in fuel.Children)
            {
                AssertAscending(st.Children.Select(u => u.Page.Index), $"{name}: units of {st.Label}");
                Assert.All(st.Children, u => Assert.Equal(fuel.Label, UiFuels.Classify(m.Generators[u.Page.Index].Type).Name));
            }
        }
        // Circuits: bands strictly descending; circuits in model order. Transformers: (HV, LV) descending.
        int[] bands = circuits.Children.Select(g => int.Parse(g.Label[..^3], Inv)).ToArray();
        for (int i = 1; i < bands.Length; i++) Assert.True(bands[i] < bands[i - 1], $"{name}: circuit bands out of order");
        foreach (AssetTreeNode g in circuits.Children) AssertAscending(g.Children.Select(n => n.Page.Index), $"{name}: circuits of {g.Label}");
        foreach (AssetTreeNode g in txs.Children) AssertAscending(g.Children.Select(n => n.Page.Index), $"{name}: transformers of {g.Label}");

        // ---- deterministic: the same ids, in the same order, built twice ----
        Assert.Equal(t.PreOrder().Select(n => n.Id), AssetTree.Build(m).PreOrder().Select(n => n.Id));
        Assert.Equal(t.Count, t.PreOrder().Select(n => n.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.True(AssetTree.SameStructure(m, m));
    }

    private static List<AssetTreeNode> Units(AssetTreeNode n)
    {
        var all = new List<AssetTreeNode>();
        void Walk(AssetTreeNode x) { if (x.Role == AssetNodeRole.Unit) all.Add(x); foreach (AssetTreeNode c in x.Children) Walk(c); }
        Walk(n);
        return all;
    }

    private static void AssertAscending(IEnumerable<int> xs, string what)
    {
        int[] a = xs.ToArray();
        for (int i = 1; i < a.Length; i++) Assert.True(a[i] > a[i - 1], $"{what}: {a[i - 1]} then {a[i]}");
    }

    private static void AssertOrdinalAscending(IEnumerable<string> xs, string what)
    {
        string[] a = xs.ToArray();
        for (int i = 1; i < a.Length; i++) Assert.True(string.CompareOrdinal(a[i], a[i - 1]) > 0, $"{what}: '{a[i - 1]}' then '{a[i]}'");
    }

    [Fact]
    public void Before_any_frame_the_tree_is_the_landing_page_and_the_seven_empty_classes()
    {
        var nav = new AssetNavigatorModel();
        Assert.Equal(8, nav.Roots.Count);
        Assert.All(nav.Roots.Skip(1), r => { Assert.Empty(r.Children); Assert.Equal("0", r.Detail); });
        Assert.Same(nav.Tree.Home, nav.Selected);
        Assert.Equal("ASSETS", nav.Crumb);
        Assert.False(nav.CanGoBack);
        Assert.False(nav.CanGoForward);
        Assert.Equal(new[] { "OVERVIEW" }, nav.SubTabs);
        Assert.Equal(new[] { "OVERVIEW", "PROPERTIES", "CONNECTIONS", "OVERLAY", "CHECK" }, AssetNavigatorModel.AllSubTabs);
        Assert.Throws<ArgumentException>(() => nav.SelectedSubTab = "PROPERTIES");   // hidden while empty
        Assert.Equal("RUN", nav.ModeLabel);
    }

    // ---------------------------------------------------------------- selecting a node opens the WPF route's page

    /// <summary>
    /// Every class node, and every unit node of case9 (the first and last of each class elsewhere), opens through the
    /// navigator exactly the page the WPF explorer opens for the same click: the same type, and the same public values
    /// on the frame it opens on and on two more frames, against a plain A2 explorer driven by the WPF call.
    /// </summary>
    [Theory]
    [InlineData("case9")]
    [InlineData("uk")]
    public void Selecting_a_node_opens_exactly_the_page_the_WPF_route_opens(string name)
    {
        var frames = Frames(name);
        var nav = new AssetNavigatorModel();
        nav.Update(frames[0].Frame, frames[0].Clock);
        GridModel m = frames[0].Frame.Model;

        var targets = new List<AssetTreeNode> { nav.Tree.Home };
        targets.AddRange(nav.Roots.Skip(1));
        foreach (AssetTreeNode cls in nav.Roots.Skip(1))
        {
            List<AssetTreeNode> units = Units(cls);
            targets.AddRange(name == "case9" ? units : units.Take(1).Concat(units.TakeLast(1)).Distinct());
        }
        // Group nodes open their class's fleet page.
        targets.AddRange(nav.Roots.Skip(1).SelectMany(c => c.Children).Where(g => g.Role == AssetNodeRole.Group).Take(3));

        foreach (AssetTreeNode node in targets)
        {
            var reference = new AssetExplorerModel();
            reference.Update(frames[0].Frame, frames[0].Clock);
            WpfRoute(reference, node, m)();
            // Test changed 2026-10-24 (agreed by Mark): a SUBSTATIONS voltage-level group now opens its page filtered to
            // that level (ASSETS A3 follow-up), so the reference is the WPF route's page with that level's chip clicked,
            // through the same public call a chip click makes; every value is still compared exactly.
            if (node.GroupKv is double groupKv && reference.Current is IKvFilteredFleet referenceFleet) referenceFleet.ApplyKvFilter(groupKv);
            if (node.GroupBand is int groupBand && reference.Current is IBandFilteredFleet referenceBands) referenceBands.ApplyBandFilter(groupBand);

            nav.Select(node);
            Assert.Same(node, nav.Selected);
            Assert.Equal(node.Path, nav.Crumb);
            Assert.Equal(reference.Current.GetType(), nav.Current.GetType());
            for (int f = 0; f < frames.Length; f++)
            {
                if (f > 0) { reference.Update(frames[f].Frame, frames[f].Clock); nav.Update(frames[f].Frame, frames[f].Clock); }
                AssertSameValues(reference.Current, nav.Current, $"{name} {node} frame {f}", 0);
            }
            Assert.Equal(node.Page, nav.Explorer.KeyOf(nav.Current));   // the page the node names is the page on screen
            nav.Update(frames[0].Frame, frames[0].Clock);   // back to the first frame for the next node
        }
    }

    /// <summary>The call the WPF explorer makes for a click on the thing this node stands for (written out here, not
    /// read from the node): the landing card's Open(Create...()), or the unit page's Open* by index.</summary>
    private static Action WpfRoute(AssetExplorerModel x, AssetTreeNode node, GridModel m)
    {
        AssetPageKey k = node.Page;
        if (node.Id.StartsWith("oltc:", StringComparison.Ordinal))
        {
            int rec = int.Parse(node.Id["oltc:".Length..], Inv);
            int bi = SteadyStateSolver.MapOltcRecordsToBranches(m)[rec];
            return bi >= 0 ? () => x.OpenTransformer(bi, m.Branches[bi]) : () => x.OpenTransformerNamed(m.Oltcs[rec].Transformer);
        }
        return k.Kind switch
        {
            AssetPageKind.Home => x.GoHome,
            AssetPageKind.GeneratorFleet => () => x.Open(x.CreateGeneratorFleet()),
            AssetPageKind.PhasorFleet => () => x.Open(x.CreatePhasorFleet()),
            AssetPageKind.InterconnectorFleet => () => x.Open(x.CreateInterconnectorFleet()),
            AssetPageKind.TransformerFleet => () => x.Open(x.CreateTransformerFleet()),
            AssetPageKind.CircuitFleet => () => x.Open(x.CreateCircuitFleet()),
            AssetPageKind.SubstationFleet => () => x.Open(x.CreateSubstationFleet()),
            AssetPageKind.TapChangerFleet => () => x.Open(x.CreateTapChangerFleet()),
            AssetPageKind.Substation => () => x.OpenSubstation(k.BusA),
            AssetPageKind.Generator => () => x.OpenGenerator(k.Index, m.Generators[k.Index].Bus, m.Generators[k.Index].Name),
            AssetPageKind.Interconnector => () => x.OpenInterconnector(k.Index, m.Generators[k.Index].Bus, m.Generators[k.Index].Name),
            AssetPageKind.Circuit => () => x.OpenCircuit(k.Index, m.Branches[k.Index]),
            AssetPageKind.Transformer => () => x.OpenTransformer(k.Index, m.Branches[k.Index]),
            _ => throw new InvalidOperationException(k.Kind.ToString()),
        };
    }

    /// <summary>Every public value of two pages (or rows) equal: strings ordinal, doubles by their bits, colour tokens by
    /// their bytes, lists row by row; actions are compared by what they open elsewhere, not here.</summary>
    private static void AssertSameValues(object? expected, object? actual, string at, int depth)
    {
        Assert.True(depth < 6, $"{at}: too deep");
        if (expected is null || actual is null) { Assert.True(expected is null && actual is null, $"{at}: one side null"); return; }
        Type t = expected.GetType();
        if (t == typeof(double))
        {
            Assert.True(BitConverter.DoubleToInt64Bits((double)expected) == BitConverter.DoubleToInt64Bits((double)actual), $"{at}: {expected} vs {actual}");
            return;
        }
        if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(UiColour) || t == typeof(decimal))
        {
            Assert.True(Equals(expected, actual), $"{at}: '{expected}' vs '{actual}'");
            return;
        }
        if (expected is Delegate) return;
        if (expected is IEnumerable eList && actual is IEnumerable aList)
        {
            object?[] e = eList.Cast<object?>().ToArray(), a = aList.Cast<object?>().ToArray();
            Assert.True(e.Length == a.Length, $"{at}: {a.Length} items, expected {e.Length}");
            for (int i = 0; i < e.Length; i++) AssertSameValues(e[i], a[i], $"{at}[{i}]", depth + 1);
            return;
        }
        foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || typeof(Delegate).IsAssignableFrom(p.PropertyType)) continue;
            AssertSameValues(p.GetValue(expected), p.GetValue(actual), $"{at}.{p.Name}", depth + 1);
        }
    }

    // ---------------------------------------------------------------- pages opened from inside a page; back / forward

    [Fact]
    public void A_page_opened_from_inside_a_page_selects_its_node_and_back_and_forward_return_the_same_page_objects()
    {
        var frames = Frames("uk");
        var nav = new AssetNavigatorModel();
        nav.Update(frames[0].Frame, frames[0].Clock);

        AssetTreeNode genClass = nav.Roots[2];
        nav.Select(genClass);
        var fleet = Assert.IsType<GeneratorFleetModel<GenFleetRowModel>>(nav.Current);
        AssetPageModel fleetPage = nav.Current;
        GenFleetRowModel row = fleet.Units[0];
        row.OnOpen();                                  // a row click: the page calls the explorer itself
        var unit = Assert.IsType<GeneratorDetailModel>(nav.Current);
        AssetTreeNode? unitNode = nav.Selected;
        Assert.NotNull(unitNode);
        Assert.Equal(AssetPageKind.Generator, unitNode!.Page.Kind);
        Assert.Equal(row.Index, unitNode.Page.Index);
        Assert.Equal(unitNode.Path, nav.Crumb);
        Assert.True(unitNode.Parent!.IsExpanded && unitNode.Parent.Parent!.IsExpanded, "the path to the selection is open");

        unit.OpenBusSubstation(unit.BusId);            // the bus link
        var sub = Assert.IsType<SubstationDetailModel<PlantRowModel, ConnectionRowModel>>(nav.Current);
        Assert.Equal(AssetPageKey.Substation(unit.BusId), nav.Selected!.Page);
        Assert.Equal(4, nav.HistoryCount);             // home, fleet, unit, substation

        // Back twice: the same page objects (their traces kept, as the WPF BACK button kept them), the selection with them.
        nav.Update(frames[1].Frame, frames[1].Clock);
        Assert.True(nav.GoBack());
        Assert.Same(unit, nav.Current);
        Assert.Same(unitNode, nav.Selected);
        Assert.True(nav.GoBack());
        Assert.Same(fleetPage, nav.Current);
        Assert.Same(genClass, nav.Selected);
        Assert.True(nav.CanGoForward);
        Assert.True(nav.GoForward());
        Assert.Same(unit, nav.Current);
        Assert.True(nav.GoForward());
        Assert.Same(sub, nav.Current);
        Assert.False(nav.GoForward());

        // A new navigation drops the forward history.
        nav.GoBack();
        nav.Select(nav.Roots[6]);                      // TRANSFORMERS
        Assert.False(nav.CanGoForward);
        Assert.Equal(4, nav.HistoryCount);             // home, fleet, unit, transformers
        Assert.Same(nav.Roots[6], nav.Selected);

        // Selecting what is on screen changes nothing; the landing page is the explorer's own home.
        Assert.False(nav.Select(nav.Roots[6]));
        nav.Select(nav.Tree.Home);
        Assert.Same(nav.Explorer.Home, nav.Current);
        Assert.Equal("ASSETS", nav.Crumb);

        // The history keeps at most HistoryLimit pages.
        for (int i = 0; i < AssetNavigatorModel.HistoryLimit + 20; i++) nav.Select(nav.Roots[1 + i % 7]);
        Assert.Equal(AssetNavigatorModel.HistoryLimit, nav.HistoryCount);
        int back = 0;
        while (nav.GoBack()) back++;
        Assert.Equal(AssetNavigatorModel.HistoryLimit - 1, back);

        // A node of another tree is refused, loud.
        Assert.Throws<ArgumentException>(() => nav.Select(AssetTree.Build(frames[0].Frame.Model).Roots[1]));
    }

    [Fact]
    public void The_WPF_routes_OPEN_IN_EXPLORER_and_SCADA_open_by_bus_and_by_branch_and_select_the_node()
    {
        var frames = Frames("uk");
        var nav = new AssetNavigatorModel();
        nav.Update(frames[0].Frame, frames[0].Clock);
        GridModel m = frames[0].Frame.Model;

        int busId = m.Buses[^1].Id;
        nav.OpenSubstation(busId);
        Assert.Equal(AssetPageKey.Substation(busId), nav.Selected!.Page);
        Assert.Same(nav.Tree.Substation(busId), nav.Selected);
        Assert.Equal(new AssetMapTarget(busId, null), nav.MapTarget);

        int tx = Enumerable.Range(0, m.Branches.Count).First(i => m.Branches[i].IsTransformer);
        nav.OpenBranch(m.Branches[tx]);
        Assert.IsType<TransformerDetailModel<TransformerReadingModel, TransformerEventModel>>(nav.Current);
        Assert.Equal("tx:" + tx.ToString(Inv), nav.Selected!.Id);
        Assert.Equal(new AssetMapTarget(null, tx), nav.MapTarget);

        int ckt = Enumerable.Range(0, m.Branches.Count).Last(i => !m.Branches[i].IsTransformer);
        nav.OpenBranch(m.Branches[ckt]);
        Assert.IsType<CircuitDetailModel<CircuitStatusRowModel>>(nav.Current);
        Assert.Equal("circuit:" + ckt.ToString(Inv), nav.Selected!.Id);

        // SHOW ON MAP hands the branch to the shell; a fleet page has no place on the map.
        var asked = new List<AssetMapTarget>();
        nav.ShowOnMapRequested += (_, t) => asked.Add(t);
        Assert.True(nav.ShowOnMap());
        Assert.Equal(new AssetMapTarget(null, ckt), Assert.Single(asked));
        nav.Select(nav.Roots[1]);
        Assert.False(nav.CanShowOnMap);
        Assert.False(nav.ShowOnMap());
        Assert.Single(asked);
    }

    // ---------------------------------------------------------------- search

    [Fact]
    public void Search_matches_name_kind_and_bus_id_ignoring_case_and_Enter_goes_to_a_bus_id()
    {
        var frames = Frames("uk");
        var nav = new AssetNavigatorModel();
        nav.Update(frames[0].Frame, frames[0].Clock);
        GridModel m = frames[0].Frame.Model;

        // A bus name, typed in the other case, finds its substation node.
        Bus named = m.Buses.First(b => b.Name is { Length: > 3 });
        nav.SearchText = named.Name!.ToUpperInvariant();
        Assert.True(nav.IsSearching);
        Assert.Contains(nav.Tree.Substation(named.Id)!, nav.SearchHits);
        Assert.All(nav.SearchHits, h => Assert.True(h.Label.Contains(named.Name!, StringComparison.OrdinalIgnoreCase)
            || h.Kind.Contains(named.Name!, StringComparison.OrdinalIgnoreCase)
            || h.BusIds.Any(id => id.ToString(Inv).Contains(named.Name!, StringComparison.Ordinal))));

        // Kind words: every transformer unit (and the class), in tree order.
        nav.SearchText = "transformer";
        Assert.Equal(m.Branches.Count(b => b.IsTransformer), nav.SearchHits.Count(h => h.Role == AssetNodeRole.Unit && h.Page.Kind == AssetPageKind.Transformer));
        Assert.Equal(nav.Tree.PreOrder().Where(n => nav.SearchHits.Contains(n)), nav.SearchHits);

        // A bus id: every node touching that bus (its substation, its plant, its branches) is found.
        int id = m.Buses[1].Id;
        nav.SearchText = id.ToString(Inv);
        Assert.Contains(nav.Tree.Substation(id)!, nav.SearchHits);
        foreach (AssetTreeNode n in nav.Tree.PreOrder().Where(n => n.BusIds.Contains(id)))
            Assert.Contains(n, nav.SearchHits);

        // Enter on a bus id goes straight to that bus and clears the search.
        Assert.True(nav.SubmitSearch());
        Assert.Same(nav.Tree.Substation(id), nav.Selected);
        Assert.Equal("", nav.SearchText);
        Assert.False(nav.IsSearching);
        Assert.Empty(nav.SearchHits);

        // Enter on a name selects the first match; on nothing, nothing changes.
        nav.SearchText = named.Name!;
        AssetTreeNode first = nav.SearchHits[0];
        Assert.True(nav.SubmitSearch());
        Assert.Same(first, nav.Selected);
        nav.SearchText = "zz-no-such-asset-zz";
        Assert.Empty(nav.SearchHits);
        Assert.Equal("no asset matches 'zz-no-such-asset-zz'", nav.SearchNote);
        Assert.False(nav.SubmitSearch());
        Assert.Same(first, nav.Selected);
        Assert.False(nav.GoToBus(int.MinValue));
    }

    [Fact]
    public void Search_lists_at_most_the_cap_and_says_how_many_there_were()
    {
        var frames = Frames("gb-synthetic");
        var nav = new AssetNavigatorModel();
        nav.Update(frames[0].Frame, frames[0].Clock);
        nav.SearchText = "a";
        int total = nav.Tree.PreOrder().Count(n => n.Role != AssetNodeRole.Home
            && (n.Label.Contains('a', StringComparison.OrdinalIgnoreCase) || n.Kind.Contains('a', StringComparison.OrdinalIgnoreCase)));
        Assert.True(total > AssetNavigatorModel.SearchHitLimit, $"{total} matches: the case no longer exercises the cap");
        Assert.Equal(AssetNavigatorModel.SearchHitLimit, nav.SearchHits.Count);
        Assert.Equal(string.Create(Inv, $"showing {AssetNavigatorModel.SearchHitLimit} of {total:N0} matches"), nav.SearchNote);
    }

    // ---------------------------------------------------------------- the feed, the gate, the rebuild

    [Fact]
    public void The_Active_gate_skips_the_page_refresh_while_hidden_and_catches_up_on_show()
    {
        var frames = Frames("case9");
        var nav = new AssetNavigatorModel();
        nav.Update(frames[0].Frame, frames[0].Clock);
        AssetTreeNode bus = nav.Tree.Substation(frames[0].Frame.Model.Buses[0].Id)!;
        nav.Select(bus);
        var page = Assert.IsType<SubstationDetailModel<PlantRowModel, ConnectionRowModel>>(nav.Current);
        int seen = page.Trace.Count;
        nav.Active = false;
        nav.Update(frames[1].Frame, frames[1].Clock);
        nav.Update(frames[2].Frame, frames[2].Clock);
        Assert.Equal(seen, page.Trace.Count);                       // hidden: not refreshed
        Assert.Contains($"TICK {frames[2].Frame.Tick}", nav.TimeLabel);  // the time strip still follows
        nav.Active = true;
        Assert.Equal(seen + 1, page.Trace.Count);                   // shown: caught up on the newest frame
    }

    [Fact]
    public void A_new_model_of_the_same_structure_keeps_the_tree_and_a_new_structure_rebuilds_it_keeping_the_selection_by_id()
    {
        var frames = Frames("case9");
        var nav = new AssetNavigatorModel();
        nav.Update(frames[0].Frame, frames[0].Clock);
        AssetTree before = nav.Tree;
        nav.Update(frames[1].Frame, frames[1].Clock);
        Assert.Same(before, nav.Tree);   // the host's frames share the structure: no rebuild, no lost expansion

        AssetTreeNode node = Units(nav.Roots[5])[0];   // a circuit
        nav.Select(node);
        GridModel m = frames[0].Frame.Model;
        // A model with the same assets plus one more bus: a new tree, the selection carried by id.
        var buses = m.Buses.ToList();
        buses.Add(new Bus(999_999, BusType.Pq, Name: "EXTRA", BaseKv: 33));
        var grown = new GridModel { BaseMva = m.BaseMva, Buses = buses, Generators = m.Generators, Branches = m.Branches, Oltcs = m.Oltcs };
        nav.Update(frames[2].Frame with { Model = grown }, frames[2].Clock);
        Assert.NotSame(before, nav.Tree);
        Assert.Equal(node.Id, nav.Selected!.Id);
        Assert.NotSame(node, nav.Selected);
        Assert.NotNull(nav.Tree.Substation(999_999));
        Assert.True(nav.Selected.Parent!.IsExpanded);
    }

    // ---------------------------------------------------------------- the toolkit diagrams

    [Fact]
    public void A_click_on_a_toolkit_diagram_symbol_opens_what_the_page_row_or_link_opens()
    {
        var frames = Frames("uk");
        var nav = new AssetNavigatorModel();
        nav.Update(frames[0].Frame, frames[0].Clock);
        GridModel m = frames[0].Frame.Model;

        // A substation with plant and connections.
        Bus bus = m.Buses.First(b => m.Generators.Any(g => g.Bus == b.Id) && m.Branches.Count(br => br.From == b.Id || br.To == b.Id) >= 2);
        nav.OpenSubstation(bus.Id);
        var sub = Assert.IsType<SubstationDetailModel<PlantRowModel, ConnectionRowModel>>(nav.Current);
        Assert.Equal("MapInput", AssetDiagrams.RebuildOn(sub));
        DiagramModel diagram = AssetDiagrams.For(sub)!;
        Assert.Equal(SubstationDiagramBuilder.Build(sub.MapInput!).Symbols.Select(s => s.Id), diagram.Symbols.Select(s => s.Id));
        Assert.Contains(diagram.Symbols, s => s.Id == "C1/CB");
        Assert.False(nav.OpenFromDiagram("BUS"));       // the substation's own busbar
        Assert.False(nav.OpenFromDiagram("D/END"));     // the demand
        Assert.Same(sub, nav.Current);

        // Connection bay 1 opens what connection row 1 opens.
        var reference = new AssetExplorerModel();
        reference.Update(frames[0].Frame, frames[0].Clock);
        reference.OpenSubstation(bus.Id);
        ((SubstationDetailModel<PlantRowModel, ConnectionRowModel>)reference.Current).Connections[1].OnOpen();
        Assert.True(nav.OpenFromDiagram("C1/CB"));
        Assert.Equal(reference.Current.GetType(), nav.Current.GetType());
        AssertSameValues(reference.Current, nav.Current, "connection bay 1", 0);
        Assert.NotNull(nav.Selected);
        Assert.Equal(nav.Explorer.KeyOf(nav.Current), nav.Selected!.Page);

        // Generator bay 0 opens plant row 0's unit.
        nav.GoBack();
        Assert.True(nav.OpenFromDiagram("G0/END"));
        Assert.IsType<GeneratorDetailModel>(nav.Current);
        Assert.Equal(sub.Plant[0].Name, nav.Current.Title);

        // A transformer's HV and LV busbars open its HV and LV substations.
        int tx = Enumerable.Range(0, m.Branches.Count).First(i => m.Branches[i].IsTransformer);
        nav.OpenBranch(m.Branches[tx]);
        var txPage = Assert.IsType<TransformerDetailModel<TransformerReadingModel, TransformerEventModel>>(nav.Current);
        Assert.Equal("Trace", AssetDiagrams.RebuildOn(txPage));
        Assert.Equal(TransformerDiagramBuilder.Build(AssetDiagrams.InputOf(txPage)).Symbols.Select(s => s.Id), AssetDiagrams.For(txPage)!.Symbols.Select(s => s.Id));
        Assert.False(nav.OpenFromDiagram("TX/TX"));
        Assert.True(nav.OpenFromDiagram(TransformerDiagramBuilder.LvBusId));
        Assert.Equal(AssetPageKey.Substation(txPage.LvBusId), nav.Selected!.Page);
        nav.GoBack();
        Assert.True(nav.OpenFromDiagram(TransformerDiagramBuilder.HvBusId));
        Assert.Equal(AssetPageKey.Substation(txPage.HvBusId), nav.Selected!.Page);

        // Pages with no diagram have none.
        nav.Select(nav.Roots[1]);
        Assert.Null(AssetDiagrams.For(nav.Current));
        Assert.Null(AssetDiagrams.RebuildOn(nav.Current));
        Assert.False(nav.OpenFromDiagram("C0/CB"));
    }

    // ---------------------------------------------------------------- the registry command

    [Fact]
    public void Open_in_Assets_is_a_Run_mode_context_command_for_a_bus_or_a_branch()
    {
        var registry = new CommandRegistry();
        var actions = new FakeActions();
        AppCommand c = Assert.Single(AssetCommands.Register(registry, actions));
        Assert.Equal(AssetCommands.OpenInAssets, c.Id);
        Assert.Equal("Open in Assets", c.Title);
        Assert.Equal(CommandModes.Run, c.Modes);
        Assert.Equal(CommandTargets.Bus | CommandTargets.Branch, c.Targets);
        Assert.Null(c.MenuPath);
        Assert.Null(c.Gesture);
        Assert.False(c.IsEnabled);
        actions.Bus = 5;
        Assert.True(c.IsEnabled);
        actions.Bus = null;
        actions.Branch = 2;
        Assert.True(c.IsEnabled);
        Assert.Contains(c, registry.ForTarget(CommandTargets.Branch, CommandModes.Run, CommandPlatforms.Desktop));
        Assert.Contains(c, registry.ForTarget(CommandTargets.Bus, CommandModes.Run, CommandPlatforms.Desktop));
        Assert.DoesNotContain(c, registry.ForTarget(CommandTargets.Bus, CommandModes.Author, CommandPlatforms.Desktop));
    }

    private sealed class FakeActions : IAssetCommandActions
    {
        public int? Bus { get; set; }
        public int? Branch { get; set; }
        public int? ContextBusId => Bus;
        public int? ContextBranchIndex => Branch;
        public Task OpenInAssetsAsync() => Task.CompletedTask;
    }
}
