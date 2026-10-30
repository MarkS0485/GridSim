using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.Model;
using GridSim.Tests.Editor;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Keys;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme ASSETS B1: the one asset key (GridSim.Ui.Assets.Keys) and the model-to-record map
/// (GridSim.Ui.Assets.Index.ModelRecordMap), pinned against the loader on every bundled case and fixture, as the case
/// editor pins its OLTC mirror against SteadyStateSolver.MapOltcRecordsToBranches. New code, so new tests:
/// <list type="bullet">
/// <item><description>AssetKey: value equality, the factories' refusals, the display texts;</description></item>
/// <item><description>ModelRecordMap, every bundled v1 case: each element maps to the record of its file at its position
/// and back (both directions, every element), and the record is the one the loader BUILT the element from: the loader's
/// own read of that record (its DTO) gives the element's values bit for bit (a line's r and x divided by its circuit count,
/// b and the rating multiplied by it; demand summed into its bus in record order);</description></item>
/// <item><description>a tagged copy (every unit, demand record, line and transformer carrying its record position in a
/// field the loader passes through), with out-of-service units: the loader itself says which record built which element,
/// and the map agrees;</description></item>
/// <item><description>the v2 projection of every bundled case, and a two-network-file split of one, through the v2 loader;
/// every v2 fixture manifest the loader accepts;</description></item>
/// <item><description>a case changed on disk after the load is refused, naming the disagreement.</description></item>
/// </list>
/// </summary>
public sealed class UiAssetIndexTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly ITestOutputHelper _out;

    public UiAssetIndexTests(ITestOutputHelper output) => _out = output;

    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

    // ================================================================ AssetKey

    [Fact]
    public void AssetKey_is_equal_by_kind_and_reference_and_refuses_a_reference_of_the_wrong_sort()
    {
        AssetKey a = AssetKey.ForModel(AssetKeyKind.Generator, ModelRef.Unit(5, 12, "Drax 1"));
        AssetKey b = AssetKey.ForModel(AssetKeyKind.Generator, ModelRef.Unit(5, 12, "Drax 1"));
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        // Any field, or the kind, makes another key; names compare ordinally.
        Assert.NotEqual(a, AssetKey.ForModel(AssetKeyKind.Interconnector, ModelRef.Unit(5, 12, "Drax 1")));
        Assert.NotEqual(a, AssetKey.ForModel(AssetKeyKind.Generator, ModelRef.Unit(6, 12, "Drax 1")));
        Assert.NotEqual(a, AssetKey.ForModel(AssetKeyKind.Generator, ModelRef.Unit(5, 13, "Drax 1")));
        Assert.NotEqual(a, AssetKey.ForModel(AssetKeyKind.Generator, ModelRef.Unit(5, 12, "drax 1")));
        Assert.NotEqual(a, AssetKey.ForModel(AssetKeyKind.Generator, ModelRef.Unit(5, 12, null)));
        Assert.False(a.Equals(null));
        Assert.True((AssetKey?)null == null);

        var rec = new CaseRef(3, "", 17, "demand.json");
        Assert.Equal(AssetKey.ForCase(AssetKeyKind.Demand, rec), AssetKey.ForCase(AssetKeyKind.Demand, new CaseRef(3, "", 17, "demand.json")));
        Assert.NotEqual(AssetKey.ForCase(AssetKeyKind.Demand, rec), AssetKey.ForCase(AssetKeyKind.Demand, rec with { Index = 18 }));
        Assert.NotEqual(AssetKey.ForClass("class:A"), AssetKey.ForGroup("class:A"));
        Assert.Equal(AssetKey.ForClass("class:A"), AssetKey.ForClass("class:A"));
        Assert.Same(AssetKey.Home, AssetKey.Home);
        Assert.Equal(AssetKey.ForOverlay(new OverlayRef("sub-1")), AssetKey.ForOverlay(new OverlayRef("sub-1")));

        // Exactly one reference, of the sort the kind takes.
        foreach (AssetKeyKind k in new[] { AssetKeyKind.Home, AssetKeyKind.Class, AssetKeyKind.Group, AssetKeyKind.Demand, AssetKeyKind.OverlayRecord })
            Assert.Throws<ArgumentException>(() => AssetKey.ForModel(k, ModelRef.Bus(1)));
        foreach (AssetKeyKind k in Enum.GetValues<AssetKeyKind>().Where(k => k != AssetKeyKind.Demand))
            Assert.Throws<ArgumentException>(() => AssetKey.ForCase(k, rec));
        Assert.Throws<ArgumentNullException>(() => AssetKey.ForModel(AssetKeyKind.Generator, null!));
        Assert.ThrowsAny<ArgumentException>(() => AssetKey.ForClass(""));
        Assert.Equal(AssetKeyKind.Demand, AssetKey.ForCase(AssetKeyKind.Demand, rec).Kind);
        Assert.Null(AssetKey.ForCase(AssetKeyKind.Demand, rec).Model);
        Assert.Null(a.Case);
        Assert.Null(a.Overlay);
        Assert.Null(a.Section);
    }

    [Fact]
    public void AssetKey_display_texts_are_exact()
    {
        Assert.Equal("Generator #5 bus 12 'Drax 1'", AssetKey.ForModel(AssetKeyKind.Generator, ModelRef.Unit(5, 12, "Drax 1")).Display);
        Assert.Equal("Substation bus 400", AssetKey.Substation(400).Display);
        Assert.Equal("Circuit #7 12->13 'L1'", AssetKey.ForModel(AssetKeyKind.Circuit, ModelRef.Branch(7, 12, 13, "L1")).Display);
        Assert.Equal("Boundary #2 'B6'", AssetKey.ForModel(AssetKeyKind.Boundary, ModelRef.Ordinal(2, "B6")).Display);
        Assert.Equal("Demand demand.json #17", AssetKey.ForCase(AssetKeyKind.Demand, new CaseRef(3, "", 17, "demand.json")).Display);
        Assert.Equal("Demand x.network.json demands #0", AssetKey.ForCase(AssetKeyKind.Demand, new CaseRef(1, "/demands", 0, "x.network.json")).Display);
        Assert.Equal("Class class:GeneratorFleet", AssetKey.ForClass("class:GeneratorFleet").Display);
        Assert.Equal("Home home", AssetKey.Home.Display);
        Assert.Equal("OverlayRecord sub-1", AssetKey.ForOverlay(new OverlayRef("sub-1")).ToString());
        Assert.Equal("/17", new CaseRef(3, "", 17, "demand.json").Pointer);
        Assert.Equal("/generators/4", new CaseRef(1, "/generators", 4, "a.network.json").Pointer);
    }

    [Fact]
    public void A_model_reference_is_confirmed_by_the_AssetLookup_rules()
    {
        GridModel m = JsonGridLoader.LoadDirectory(EditorTestSupport.CaseDir("uk"));
        Generator g = m.Generators[3];
        Branch b = m.Branches[2];
        ModelRef unit = ModelRef.Unit(3, g.Bus, g.Name);
        Assert.Equal(3, unit.FindGenerator(m)!.Value.Index);
        Assert.Null(ModelRef.Unit(3, g.Bus + 100000, g.Name).FindGenerator(m));         // another bus: not this unit
        Assert.Equal(2, ModelRef.Branch(2, b.From, b.To, b.Name).FindBranch(m)!.Value.Index);
        Assert.Null(ModelRef.Branch(2, b.To + 100000, b.To, b.Name).FindBranch(m));
        Assert.True(ModelRef.Bus(m.Buses[0].Id).HasBus(m));
        Assert.False(ModelRef.Bus(int.MinValue).HasBus(m));
        // The tree's keys are made by the same factories.
        Assert.Equal(AssetKey.ForModel(AssetKeyKind.Circuit, ModelRef.Branch(2, b.From, b.To, b.Name)), AssetKey.Branch(m, 2));
    }

    [Theory]
    [InlineData("uk")]
    [InlineData("gb-synthetic")]
    public void Every_node_of_the_explorer_tree_carries_its_asset_key_and_is_found_again_by_it(string relative)
    {
        // The navigator's nodes are keyed by AssetKey (B1): the landing page, each class and group by its section id,
        // each asset by the model reference its page confirms (a bus id; a unit's or branch's index, bus or ends and name;
        // an OLTC record's position, transformer and controlled bus). The tree is otherwise the A3 tree.
        GridModel m = JsonGridLoader.LoadDirectory(EditorTestSupport.CaseDir(relative));
        GridSim.Ui.Assets.Navigation.AssetTree t = GridSim.Ui.Assets.Navigation.AssetTree.Build(m);
        var all = t.PreOrder().ToList();
        Assert.Equal(all.Count, all.Select(n => n.Key).Distinct().Count());
        foreach (GridSim.Ui.Assets.Navigation.AssetTreeNode n in all)
        {
            Assert.Same(n, t.NodeFor(n.Key));
            GridSim.Ui.Assets.Navigation.AssetPageKey p = n.Page;
            AssetKey expected = n.Role switch
            {
                GridSim.Ui.Assets.Navigation.AssetNodeRole.Home => AssetKey.Home,
                GridSim.Ui.Assets.Navigation.AssetNodeRole.Class => AssetKey.ForClass(n.Id),
                GridSim.Ui.Assets.Navigation.AssetNodeRole.Group => AssetKey.ForGroup(n.Id),
                _ when n.Id.StartsWith("oltc:", StringComparison.Ordinal) => AssetKey.TapChanger(m, int.Parse(n.Id[5..], Inv)),
                _ => p.Kind switch
                {
                    GridSim.Ui.Assets.Navigation.AssetPageKind.Substation => AssetKey.Substation(p.BusA),
                    GridSim.Ui.Assets.Navigation.AssetPageKind.Generator => AssetKey.ForModel(AssetKeyKind.Generator, ModelRef.Unit(p.Index, p.BusA, p.Name)),
                    GridSim.Ui.Assets.Navigation.AssetPageKind.Interconnector => AssetKey.ForModel(AssetKeyKind.Interconnector, ModelRef.Unit(p.Index, p.BusA, p.Name)),
                    GridSim.Ui.Assets.Navigation.AssetPageKind.Circuit => AssetKey.ForModel(AssetKeyKind.Circuit, ModelRef.Branch(p.Index, p.BusA, p.BusB, p.Name)),
                    GridSim.Ui.Assets.Navigation.AssetPageKind.Transformer => AssetKey.ForModel(AssetKeyKind.Transformer, ModelRef.Branch(p.Index, p.BusA, p.BusB, p.Name)),
                    _ => throw new InvalidOperationException(n.ToString()),
                },
            };
            Assert.Equal(expected, n.Key);
        }
        Assert.Null(t.NodeFor(AssetKey.Substation(int.MinValue)));
        Assert.Null(t.NodeFor((AssetKey?)null));
    }

    // ================================================================ the map, pinned against the loader (v1)

    // The loader's own read of one v1 file (the DTOs it builds the model from), or an empty list when the file is absent.
    private static List<T> Read<T>(string dir, string file, System.Text.Json.Serialization.Metadata.JsonTypeInfo<List<T>> info)
    {
        string path = Path.Combine(dir, file);
        return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllBytes(path), info)! : new List<T>();
    }

    /// <summary>The counts one pin saw, for the evidence log.</summary>
    private sealed record PinCounts(int Buses, int Generators, int OutOfService, int Branches, int Oltcs, int Boundaries,
        int Protections, int Classes, int Bindings, int Demands);

    /// <summary>
    /// Pin the map of <paramref name="model"/> (loaded from v1 folder <paramref name="dir"/>) both ways and against the
    /// loader's DTOs: every element has the record of its file at its position, every such record names that element, and
    /// the DTO of that record builds exactly the element's values.
    /// </summary>
    private static PinCounts PinV1(string dir, GridModel model)
    {
        CaseSession s = CaseSession.OpenV1(dir, CaseSessionTests.Unlimited());
        ModelRecordMap map = ModelRecordMap.Build(model, s);
        JsonGridLoaderJsonContext j = JsonGridLoader.Json;
        List<BusDto> buses = Read(dir, "buses.json", j.ListBusDto);
        List<GenDto> gens = Read(dir, "generation.json", j.ListGenDto);
        List<DemandDto> demands = Read(dir, "demand.json", j.ListDemandDto);
        List<LineDto> lines = Read(dir, "transmission.json", j.ListLineDto);
        List<TransformerDto> txs = Read(dir, "transformer.json", j.ListTransformerDto);
        List<OltcDto> oltcs = Read(dir, "onloadtapchanger.json", j.ListOltcDto);
        List<BoundaryDto> boundaries = Read(dir, "boundaries.json", j.ListBoundaryDto);
        List<ProtectionInstanceDto> protections = Read(dir, "protections.json", j.ListProtectionInstanceDto);
        List<AssetClassDto> classes = Read(dir, "asset-classes.json", j.ListAssetClassDto);
        List<AssetBindingDto> bindings = Read(dir, "asset-bindings.json", j.ListAssetBindingDto);

        void Is(CaseRef r, string file, int index, AssetKeyKind kind, int element)
        {
            Assert.Equal(file, r.FileName);
            Assert.Equal("", r.ArrayPointer);
            Assert.Equal(index, r.Index);
            Assert.Equal(file, s.Documents[r.Document].FileName);
            Assert.Equal((kind, element), map.ElementOf(r));   // and back
        }

        // ---- buses ----
        Assert.Equal(buses.Count, model.Buses.Count);
        Assert.Equal(buses.Count, map.BusCount);
        for (int i = 0; i < buses.Count; i++)
        {
            Is(map.BusRecord(i), "buses.json", i, AssetKeyKind.Substation, i);
            Assert.Equal(buses[i].Id, model.Buses[i].Id);
            Assert.Equal(Bits(buses[i].BaseKv!.Value), Bits(model.Buses[i].BaseKv));
            Assert.Equal(buses[i].Name, model.Buses[i].Name);
        }

        // ---- generators: the whole fleet, out-of-service units included; the in-service list through it ----
        Assert.Equal(gens.Count, model.AllGenerators.Count);
        Assert.Equal(gens.Count, map.GeneratorCount);
        int inService = 0;
        for (int k = 0; k < gens.Count; k++)
        {
            Is(map.FleetRecord(k), "generation.json", k, AssetKeyKind.OutOfServiceUnit, k);
            Generator g = model.AllGenerators[k];
            Assert.Equal(gens[k].Bus, g.Bus);
            Assert.Equal(gens[k].Name, g.Name);
            Assert.Equal(gens[k].Type, g.Type);
            Assert.Equal(gens[k].InService, g.InService);
            Assert.Equal(Bits(gens[k].CapacityMva), Bits(g.MBase));
            if (!g.InService) continue;
            Assert.Same(g, model.Generators[inService]);
            Assert.Equal(k, map.FleetIndexOf(inService));
            Assert.Equal(map.FleetRecord(k), map.GeneratorRecord(inService));
            inService++;
        }
        Assert.Equal(model.Generators.Count, inService);

        // ---- branches: every line, then every transformer, with the loader's per-circuit equivalent ----
        Assert.Equal(lines.Count + txs.Count, model.Branches.Count);
        Assert.Equal(model.Branches.Count, map.BranchCount);
        for (int i = 0; i < lines.Count; i++)
        {
            Is(map.BranchRecord(i), "transmission.json", i, AssetKeyKind.Circuit, i);
            LineDto l = lines[i];
            Branch b = model.Branches[i];
            Assert.Equal(l.IsTransformer, b.IsTransformer);   // a line record may say "isTransformer": true
            Assert.Equal((l.From, l.To, l.Name), (b.From, b.To, b.Name));
            Assert.Equal(Bits(l.RPu / l.Circuits), Bits(b.R));
            Assert.Equal(Bits(l.XPu / l.Circuits), Bits(b.X));
            Assert.Equal(Bits(l.BPu * l.Circuits), Bits(b.B));
            Assert.Equal(Bits(l.RatingMva * l.Circuits), Bits(b.RateAMva));
        }
        for (int t = 0; t < txs.Count; t++)
        {
            int i = lines.Count + t;
            Is(map.BranchRecord(i), "transformer.json", t, AssetKeyKind.Circuit, i);
            TransformerDto x = txs[t];
            Branch b = model.Branches[i];
            Assert.True(b.IsTransformer);
            Assert.Equal((x.From, x.To, x.Name), (b.From, b.To, b.Name));
            Assert.Equal(Bits(x.RPu), Bits(b.R));
            Assert.Equal(Bits(x.XPu), Bits(b.X));
            Assert.Equal(Bits(x.RatingMva), Bits(b.RateAMva));
        }

        // ---- tap changers, boundaries, protections, classes, bindings: in record order ----
        Assert.Equal(oltcs.Count, map.OltcCount);
        for (int k = 0; k < oltcs.Count; k++)
        {
            Is(map.OltcRecord(k), "onloadtapchanger.json", k, AssetKeyKind.TapChanger, k);
            Assert.Equal((oltcs[k].Transformer, oltcs[k].ControlsBus), (model.Oltcs[k].Transformer, model.Oltcs[k].ControlsBus));
            Assert.Equal(Bits(oltcs[k].TargetVpu), Bits(model.Oltcs[k].TargetVpu));
        }
        Assert.Equal(boundaries.Count, map.BoundaryCount);
        for (int k = 0; k < boundaries.Count; k++)
        {
            Is(map.BoundaryRecord(k), "boundaries.json", k, AssetKeyKind.Boundary, k);
            Assert.Equal(boundaries[k].Name, model.Boundaries[k].Name);
            Assert.Equal(Bits(boundaries[k].LimitMw), Bits(model.Boundaries[k].LimitMw));
        }
        var modelProtections = ModelRecordMap.ProtectionsOf(model);
        Assert.Equal(protections.Count, map.ProtectionCount);
        Assert.Equal(protections.Count, modelProtections.Count);
        for (int k = 0; k < protections.Count; k++)
        {
            Is(map.ProtectionRecord(k), "protections.json", k, AssetKeyKind.Protection, k);
            Assert.Equal((protections[k].Name, protections[k].Class, protections[k].MonitorBus),
                (modelProtections[k].Name, modelProtections[k].ClassName, modelProtections[k].MonitorBus));
        }
        var modelClasses = ModelRecordMap.ClassesOf(model);
        Assert.Equal(classes.Count, map.AssetClassCount);
        Assert.Equal(classes.Count, modelClasses.Count);
        for (int k = 0; k < classes.Count; k++)
        {
            Is(map.AssetClassRecord(k)!, "asset-classes.json", k, AssetKeyKind.AssetClass, k);
            Assert.Equal(classes[k].Name, modelClasses[k].Name);
        }
        Assert.Null(map.AssetClassRecord(classes.Count));
        var modelBindings = ModelRecordMap.BindingsOf(model);
        Assert.Equal(bindings.Count, map.BindingCount);
        Assert.Equal(bindings.Count, modelBindings.Count);
        for (int k = 0; k < bindings.Count; k++)
        {
            Is(map.BindingRecord(k)!, "asset-bindings.json", k, AssetKeyKind.AssetBinding, k);
            Assert.Equal((bindings[k].MatchType, bindings[k].Class), (modelBindings[k].MatchType, modelBindings[k].ClassName));
        }

        // ---- demand: every record, its bus; summed per bus in record order it is the model's Pd, bit for bit ----
        Assert.Equal(demands.Count, map.Demands.Count);
        var pd = new Dictionary<int, double>();
        for (int k = 0; k < demands.Count; k++)
        {
            DemandRecord d = map.Demands[k];
            Is(d.Record, "demand.json", k, AssetKeyKind.Demand, k);
            Assert.Equal(k, d.Ordinal);
            Assert.Equal(demands[k].Bus, d.Bus);
            Assert.Equal(demands[k].Name, d.Name);
            pd[d.Bus] = pd.GetValueOrDefault(d.Bus) + demands[k].PMw;
        }
        foreach (Bus b in model.Buses) Assert.Equal(Bits(pd.GetValueOrDefault(b.Id)), Bits(b.Pd));

        // ---- nothing else maps: system.json and nested records are no element ----
        Assert.Null(map.ElementOf(new CaseRef(0, "", -1, "system.json")));
        return new PinCounts(buses.Count, gens.Count, gens.Count(g => !g.InService), model.Branches.Count, oltcs.Count, boundaries.Count,
            protections.Count, classes.Count, bindings.Count, demands.Count);
    }

    [Theory]
    [MemberData(nameof(EditorTestSupport.BundledCases), MemberType = typeof(EditorTestSupport))]
    public void ModelRecordMap_equals_the_loader_on_every_bundled_case(string relative)
    {
        string dir = EditorTestSupport.CaseDir(relative);
        // The model as the app loads it (dispatched) and as the editor's pre-flight loads it (not dispatched): the same
        // records build both, so the map is the same.
        GridModel dispatched = JsonGridLoader.LoadDirectory(dir);
        GridModel raw = JsonGridLoader.LoadDirectory(dir, dispatch: false);
        PinCounts c = PinV1(dir, raw);
        Assert.Equal(c, PinV1(dir, dispatched));
        Assert.True(c.Buses > 0 && c.Generators > 0 && c.Branches > 0, "a case with no buses, units or branches proves nothing");
        _out.WriteLine($"{relative}: {c}");
    }

    // ---- the tagged copy: the loader itself says which record built which element ----

    // Write every array file of a v1 case into a fresh folder after a change to each record (comments are dropped: the
    // copy is read by the loader, never compared byte for byte).
    private static string TaggedCopy(string sourceDir, Action<string, JsonArray> change)
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-assets-b1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var opts = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        foreach (string f in Directory.EnumerateFiles(sourceDir, "*.json"))
        {
            string name = Path.GetFileName(f);
            JsonNode node = JsonNode.Parse(File.ReadAllText(f), documentOptions: opts)!;
            if (node is JsonArray a) change(name, a);
            File.WriteAllText(Path.Combine(dir, name), node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        return dir;
    }

    [Theory]
    [InlineData("uk")]
    [InlineData("gb-synthetic")]
    public void A_tagged_copy_with_out_of_service_units_maps_each_element_to_the_record_that_built_it(string relative)
    {
        // Each unit and demand record is renamed after its record position, each line carries it as its length and each
        // transformer as its emergency rating (fields the loader passes through to the element unchanged); every third
        // non-slack unit is taken out of service. Then the element built from record k says "k" itself.
        string dir = TaggedCopy(EditorTestSupport.CaseDir(relative), (file, a) =>
        {
            for (int k = 0; k < a.Count; k++)
            {
                JsonObject o = a[k]!.AsObject();
                switch (file)
                {
                    case "generation.json":
                        o["name"] = "rec-g-" + k.ToString(Inv);
                        bool slack = o.TryGetPropertyValue("role", out JsonNode? role) && string.Equals((string?)role, "slack", StringComparison.OrdinalIgnoreCase);
                        if (!slack && k % 3 == 1) o["inService"] = false;
                        break;
                    case "demand.json":
                        o["name"] = "rec-d-" + k.ToString(Inv);
                        break;
                    case "transmission.json":
                        o["lengthKm"] = k + 1;
                        break;
                    case "transformer.json":
                        o["ratingCMva"] = 100000 + k;
                        break;
                }
            }
        });
        try
        {
            GridModel m = JsonGridLoader.LoadDirectory(dir);
            Assert.Contains(m.AllGenerators, g => !g.InService);
            PinCounts c = PinV1(dir, m);
            Assert.True(c.OutOfService > 0);
            ModelRecordMap map = ModelRecordMap.Build(m, CaseSession.OpenV1(dir, CaseSessionTests.Unlimited()));
            for (int k = 0; k < m.AllGenerators.Count; k++)
                Assert.Equal("rec-g-" + map.FleetRecord(k).Index.ToString(Inv), m.AllGenerators[k].Name);
            for (int i = 0; i < m.Generators.Count; i++)
                Assert.Equal("rec-g-" + map.GeneratorRecord(i).Index.ToString(Inv), m.Generators[i].Name);
            for (int i = 0; i < m.Branches.Count; i++)
            {
                CaseRef r = map.BranchRecord(i);
                if (m.Branches[i].IsTransformer) Assert.Equal(100000 + r.Index, m.Branches[i].RateCMva);
                else Assert.Equal(r.Index + 1, m.Branches[i].LengthKm);
            }
            foreach (DemandRecord d in map.Demands) Assert.Equal("rec-d-" + d.Record.Index.ToString(Inv), d.Name);
            _out.WriteLine($"{relative} tagged: {c}");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_case_changed_on_disk_after_the_load_is_refused_naming_the_disagreement()
    {
        GridModel m = JsonGridLoader.LoadDirectory(EditorTestSupport.CaseDir("uk"));
        // A unit renamed and one demand record pointed at a bus that does not exist: two problems, both named.
        string dir = TaggedCopy(EditorTestSupport.CaseDir("uk"), (file, a) =>
        {
            if (file == "generation.json") a[0]!["name"] = "renamed after the load";
        });
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => ModelRecordMap.Build(m, CaseSession.OpenV1(dir, CaseSessionTests.Unlimited())));
            Assert.StartsWith("The case file does not build the running model (1 problem(s)): generation.json #0: unit 'renamed after the load'", ex.Message, StringComparison.Ordinal);
            // The index then has no case and says why (PROPERTIES is not shown, never a record for the wrong unit).
            AssetIndex ix = AssetIndex.Build(m, AssetCaseSource.V1(dir), CaseSessionTests.Unlimited());
            Assert.False(ix.HasCase);
            Assert.Null(ix.Map);
            Assert.Equal("PROPERTIES is not shown: " + ex.Message, ix.CaseNote);
        }
        finally { Directory.Delete(dir, recursive: true); }
        // A model of another case: every count disagrees.
        GridModel other = JsonGridLoader.LoadDirectory(EditorTestSupport.CaseDir("gb-synthetic"));
        var ex2 = Assert.Throws<InvalidDataException>(() => ModelRecordMap.Build(other, CaseSession.OpenV1(EditorTestSupport.CaseDir("uk"), CaseSessionTests.Unlimited())));
        Assert.Contains("buses: the case file holds", ex2.Message, StringComparison.Ordinal);
    }

    // ================================================================ the map, pinned against the loader (v2)

    private static string SchemasV2 => Path.Combine(EditorTestSupport.SchemasDir, "v2");

    /// <summary>
    /// Pin the map of a v2 case: each element maps to the element record of its network file at its position in the
    /// order the v2 loader reads them (the manifest's files in order, each file's array in order; branches: every file's
    /// lines, then every file's transformers), both directions, and the record's identity fields equal the element's.
    /// </summary>
    private static (int Files, PinCounts Counts) PinV2(string manifest, GridModel m)
    {
        CaseSession s = CaseSession.OpenV2(manifest, CaseSessionTests.Unlimited());
        ModelRecordMap map = ModelRecordMap.Build(m, s);
        var opts = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        // Each network document's arrays, read independently of the editing engine.
        var docs = new List<(int Id, string File, JsonElement Root)>();
        foreach (CaseDocument d in s.Documents.Where(d => d.SchemaFile == CaseSession.V2NetworkSchema))
        {
            using JsonDocument jd = JsonDocument.Parse(File.ReadAllBytes(d.Path), opts);
            docs.Add((d.Id, d.FileName, jd.RootElement.Clone()));
        }
        List<(CaseRef Ref, JsonElement E)> All(string role)
        {
            var list = new List<(CaseRef, JsonElement)>();
            foreach (var (id, file, root) in docs)
                if (root.TryGetProperty(role, out JsonElement a))
                {
                    int i = 0;
                    foreach (JsonElement e in a.EnumerateArray()) list.Add((new CaseRef(id, "/" + role, i++, file), e));
                }
            return list;
        }
        static string? Name(JsonElement e) => e.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;

        var buses = All("buses");
        Assert.Equal(buses.Count, m.Buses.Count);
        for (int i = 0; i < buses.Count; i++)
        {
            Assert.Equal(buses[i].Ref, map.BusRecord(i));
            Assert.Equal((AssetKeyKind.Substation, i), map.ElementOf(buses[i].Ref));
            Assert.Equal(buses[i].E.GetProperty("id").GetInt32(), m.Buses[i].Id);
        }
        var gens = All("generators");
        Assert.Equal(gens.Count, m.AllGenerators.Count);
        for (int k = 0; k < gens.Count; k++)
        {
            Assert.Equal(gens[k].Ref, map.FleetRecord(k));
            Assert.Equal((AssetKeyKind.OutOfServiceUnit, k), map.ElementOf(gens[k].Ref));
            Assert.Equal((gens[k].E.GetProperty("bus").GetInt32(), Name(gens[k].E)), (m.AllGenerators[k].Bus, m.AllGenerators[k].Name));
        }
        var branches = All("lines").Concat(All("transformers")).ToList();
        int lineCount = All("lines").Count;
        Assert.Equal(branches.Count, m.Branches.Count);
        for (int i = 0; i < branches.Count; i++)
        {
            Assert.Equal(branches[i].Ref, map.BranchRecord(i));
            Assert.Equal((AssetKeyKind.Circuit, i), map.ElementOf(branches[i].Ref));
            JsonElement e = branches[i].E;
            bool flagged = e.TryGetProperty("isTransformer", out JsonElement f) && f.GetBoolean();
            Assert.Equal((e.GetProperty("from").GetInt32(), e.GetProperty("to").GetInt32(), Name(e), i >= lineCount || flagged),
                (m.Branches[i].From, m.Branches[i].To, m.Branches[i].Name, m.Branches[i].IsTransformer));
        }
        var oltcs = All("oltcs");
        Assert.Equal(oltcs.Count, m.Oltcs.Count);
        for (int k = 0; k < oltcs.Count; k++)
        {
            Assert.Equal(oltcs[k].Ref, map.OltcRecord(k));
            Assert.Equal(oltcs[k].E.GetProperty("transformer").GetString(), m.Oltcs[k].Transformer);
        }
        var bounds = All("boundaries");
        Assert.Equal(bounds.Count, m.Boundaries.Count);
        for (int k = 0; k < bounds.Count; k++)
        {
            Assert.Equal(bounds[k].Ref, map.BoundaryRecord(k));
            Assert.Equal(Name(bounds[k].E), m.Boundaries[k].Name);
        }
        var prots = All("protections");
        var modelProtections = ModelRecordMap.ProtectionsOf(m);
        Assert.Equal(prots.Count, modelProtections.Count);
        for (int k = 0; k < prots.Count; k++)
        {
            Assert.Equal(prots[k].Ref, map.ProtectionRecord(k));
            Assert.Equal(Name(prots[k].E), modelProtections[k].Name);
        }
        var demands = All("demands");
        Assert.Equal(demands.Count, map.Demands.Count);
        for (int k = 0; k < demands.Count; k++)
        {
            Assert.Equal(demands[k].Ref, map.Demands[k].Record);
            Assert.Equal(demands[k].E.GetProperty("bus").GetInt32(), map.Demands[k].Bus);
            Assert.Equal((AssetKeyKind.Demand, k), map.ElementOf(demands[k].Ref));
        }
        // v2 classes and bindings are manifest templates, not records.
        Assert.Equal(0, map.AssetClassCount);
        Assert.Equal(0, map.BindingCount);
        return (docs.Count, new PinCounts(buses.Count, gens.Count, m.AllGenerators.Count(g => !g.InService), branches.Count, oltcs.Count,
            bounds.Count, prots.Count, 0, 0, demands.Count));
    }

    [Theory]
    [MemberData(nameof(EditorTestSupport.BundledCases), MemberType = typeof(EditorTestSupport))]
    public void ModelRecordMap_equals_the_v2_loader_on_the_v2_projection_of_every_bundled_case(string relative)
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-assets-b1-v2-" + Guid.NewGuid().ToString("N"));
        try
        {
            V2CaseWriteResult w = V2CaseWriter.Write(JsonGridLoader.LoadDirectory(EditorTestSupport.CaseDir(relative), dispatch: false),
                Path.Combine(dir, "case.case.json"));
            GridModel m = V2CaseLoader.Load(w.ManifestPath, SchemasV2);
            (int files, PinCounts c) = PinV2(w.ManifestPath, m);
            Assert.Equal(1, files);
            _out.WriteLine($"{relative} v2: {c}");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ModelRecordMap_follows_the_v2_loader_across_two_network_files()
    {
        // The v2 projection of the reduced GB case, its network split in two: the first half of every element array in
        // one file, the rest in the other. The loader reads the files in the manifest's order, each array in order, so
        // bus i lives in the first file when i is in its first half, and the lines of BOTH files come before any
        // transformer.
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-assets-b1-split-" + Guid.NewGuid().ToString("N"));
        try
        {
            V2CaseWriteResult w = V2CaseWriter.Write(JsonGridLoader.LoadDirectory(EditorTestSupport.CaseDir("uk"), dispatch: false),
                Path.Combine(dir, "split.case.json"));
            JsonObject net = JsonNode.Parse(File.ReadAllText(w.NetworkPath))!.AsObject();
            var a = new JsonObject { ["schema"] = "gridsim-network/2" };
            var b = new JsonObject { ["schema"] = "gridsim-network/2" };
            var firstHalf = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach ((string key, JsonNode? value) in net.ToList())
            {
                if (value is not JsonArray arr) continue;
                int half = arr.Count / 2;
                firstHalf[key] = half;
                a[key] = new JsonArray(arr.Take(half).Select(n => n!.DeepClone()).ToArray());
                b[key] = new JsonArray(arr.Skip(half).Select(n => n!.DeepClone()).ToArray());
            }
            File.WriteAllText(Path.Combine(dir, "a.network.json"), a.ToJsonString());
            File.WriteAllText(Path.Combine(dir, "b.network.json"), b.ToJsonString());
            JsonObject manifest = JsonNode.Parse(File.ReadAllText(w.ManifestPath))!.AsObject();
            manifest["network"] = new JsonArray("a.network.json", "b.network.json");
            File.WriteAllText(w.ManifestPath, manifest.ToJsonString());
            File.Delete(w.NetworkPath);

            GridModel m = V2CaseLoader.Load(w.ManifestPath, SchemasV2);
            (int files, PinCounts c) = PinV2(w.ManifestPath, m);
            Assert.Equal(2, files);
            ModelRecordMap map = ModelRecordMap.Build(m, CaseSession.OpenV2(w.ManifestPath, CaseSessionTests.Unlimited()));
            Assert.Equal("a.network.json", map.BusRecord(0).FileName);
            Assert.Equal("b.network.json", map.BusRecord(firstHalf["buses"]).FileName);
            Assert.Equal(0, map.BusRecord(firstHalf["buses"]).Index);
            int lines = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "a.network.json")))!["lines"]!.AsArray().Count
                + JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "b.network.json")))!["lines"]!.AsArray().Count;
            Assert.Equal(("b.network.json", "/lines"), (map.BranchRecord(lines - 1).FileName, map.BranchRecord(lines - 1).ArrayPointer));
            // The writer puts a transformer it cannot write as a transformer element among the lines (isTransformer);
            // when it wrote transformer elements too, the first of them follows the lines of BOTH files.
            if (lines < m.Branches.Count)
                Assert.Equal(("a.network.json", "/transformers", 0), (map.BranchRecord(lines).FileName, map.BranchRecord(lines).ArrayPointer, map.BranchRecord(lines).Index));
            else Assert.Equal(m.Branches.Count, lines);
            _out.WriteLine($"uk v2 split: {c}");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    public static TheoryData<string> V2FixtureManifests()
    {
        var data = new TheoryData<string>();
        foreach (string f in Directory.EnumerateFiles(Path.Combine(EditorTestSupport.RepoRoot(), "tests", "GridSim.Tests", "fixtures", "v2", "cases"), "*.case.json")
                     .OrderBy(f => f, StringComparer.Ordinal))
            data.Add(Path.GetFileName(f));
        return data;
    }

    [Theory]
    [MemberData(nameof(V2FixtureManifests))]
    public void ModelRecordMap_equals_the_v2_loader_on_every_v2_fixture_the_loader_accepts(string manifest)
    {
        string fixtures = Path.Combine(EditorTestSupport.RepoRoot(), "tests", "GridSim.Tests", "fixtures", "v2");
        string path = Path.Combine(fixtures, "cases", manifest);
        GridModel m;
        try { m = V2CaseLoader.Load(path, SchemasV2, dataRoot: fixtures); }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            // A fixture the loader refuses builds no model, so there is nothing to map (and the app never runs it).
            _out.WriteLine($"{manifest}: refused by the loader, no model to map ({ex.GetType().Name})");
            return;
        }
        (int files, PinCounts c) = PinV2(path, m);
        _out.WriteLine($"{manifest}: {files} network file(s), {c}");
    }
}
