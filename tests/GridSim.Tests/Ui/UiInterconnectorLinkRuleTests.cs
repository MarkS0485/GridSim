using System.Globalization;
using System.Text.Json;
using GridSim.Core.Analysis;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.Presentation;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Assets.Pages;
using GridSim.Ui.Dashboard;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests.Ui;

/// <summary>
/// Bug #28 (2026-10-24): the one interconnector LINK rule, <see cref="FuelTaxonomy.IsInterconnectorLink"/>. Every place
/// that lists, counts or totals links used to test the fuel CATEGORY, which also holds the grid infeed (the slack
/// balancer and the voltage-control units at a model boundary). On gb-full that listed 1,072 grid infeeds as links beside
/// the 12 real ones. These tests pin the corrected rule: the truth table, the bundled cases that carry grid infeeds
/// (counted here from the raw JSON type strings, independently of the code), the landing cards, the INTERCONNECTORS page,
/// the explorer tree (each grid infeed under the substation bus it feeds) and the GENERATION tab's import-links panel.
/// They also pin what did NOT change: the grid infeed's category (imported energy in the fuel mix, carbon and THE
/// MACHINE's IMPORTS group) and the generator pages' membership (plant: neither links nor grid infeeds).
/// </summary>
public sealed class UiInterconnectorLinkRuleTests(ITestOutputHelper output)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");

    // ---------------------------------------------------------------------------------------------------------------
    // The rule itself
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("interconnector", true)]
    [InlineData("Interconnector", true)]
    [InlineData("hvdc", true)]
    [InlineData("import", true)]
    [InlineData("grid", false)]
    [InlineData("infeed", false)]
    [InlineData("Grid infeed", false)]
    [InlineData("ccgt", false)]
    [InlineData("other", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_interconnector_kind_is_a_link(string? type, bool link)
    {
        Assert.Equal(link, FuelTaxonomy.IsInterconnectorLink(type));
        Assert.Equal(link, AssetIndex.IsLink(new Generator(1, 0, Type: type ?? "")));
    }

    [Theory]
    [InlineData("interconnector", false, false)]
    [InlineData("grid", true, false)]
    [InlineData("infeed", true, false)]
    [InlineData("ccgt", false, true)]
    [InlineData("other", false, true)]
    [InlineData("", false, true)]
    public void Plant_is_neither_a_link_nor_a_grid_infeed(string type, bool infeed, bool plant)
    {
        Assert.Equal(infeed, FuelTaxonomy.IsGridInfeed(type));
        Generator g = new(1, 0, Type: type);
        Assert.Equal(plant, AssetIndex.IsPlant(g));
        // Plant is exactly the old membership of the generator pages: outside the imported-energy category.
        Assert.Equal(FuelTaxonomy.Classify(type).Category != FuelCategory.Interconnector, AssetIndex.IsPlant(g));
    }

    [Fact]
    public void A_grid_infeed_keeps_its_imported_energy_category()
    {
        // The energy bucket is deliberately unchanged: only the LINK lists moved.
        FuelDescriptor d = FuelTaxonomy.Classify("grid");
        Assert.Equal("Grid infeed", d.Name);
        Assert.Equal(FuelCategory.Interconnector, d.Category);
        Assert.False(d.Synchronous);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The bundled cases that carry grid infeeds
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The raw type string of every in-service generation row, read straight from the case's JSON.</summary>
    private static List<string> RawTypes(string name)
    {
        string file = Path.Combine(DataDir, name.Replace('/', Path.DirectorySeparatorChar), "generation.json");
        Assert.True(File.Exists(file), $"bundled case not found: {file}");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
        var types = new List<string>();
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (row.TryGetProperty("inService", out JsonElement svc) && svc.ValueKind == JsonValueKind.False) continue;
            types.Add(row.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "" : "");
        }
        return types;
    }

    private static SimFrame FrameOf(GridModel m) => new(m, new PowerFlowResult
    {
        Converged = false,
        Iterations = 0,
        MaxMismatch = double.NaN,
        BusIds = Array.Empty<int>(),
        Vm = Array.Empty<double>(),
        VaDeg = Array.Empty<double>(),
        PInjMw = Array.Empty<double>(),
        QInjMvar = Array.Empty<double>(),
        TotalLossMw = 0,
        TotalLossMvar = 0,
        LineFlows = Array.Empty<LineFlow>(),
    }, Hour: 0, Tick: 0, FrequencyHz: 50, GenerationMw: 0, DemandMw: 0);

    /// <summary>gb-full: exactly 12 links (its 12 "interconnector" rows) and 1,072 grid infeeds that are not links.</summary>
    [Theory]
    [InlineData("gb-full", 12, 1072)]
    [InlineData("intl/france", -1, -1)]
    [InlineData("intl/continental-europe", -1, -1)]
    public void The_explorer_the_landing_card_and_the_page_list_only_real_links(string name, int wantLinks, int wantInfeeds)
    {
        // Counted from the raw JSON, independently of the taxonomy: a link is a row typed "interconnector" / "hvdc" /
        // "import" (the three interconnector keywords); a grid infeed is a row typed "grid" / "infeed".
        List<string> raw = RawTypes(name);
        int links = raw.Count(t => t.ToLowerInvariant() is "interconnector" or "hvdc" or "import");
        int infeeds = raw.Count(t => t.ToLowerInvariant() is "grid" or "infeed");
        if (wantLinks >= 0) Assert.Equal(wantLinks, links);
        if (wantInfeeds >= 0) Assert.Equal(wantInfeeds, infeeds);
        Assert.True(infeeds > 0, $"{name}: a case with no grid infeed proves nothing here");

        GridModel m = JsonGridLoader.LoadDirectory(Path.Combine(DataDir, name.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Equal(raw.Count, m.Generators.Count);

        // The explorer tree.
        AssetTree t = AssetTree.Build(m);
        AssetTreeNode gens = t.Roots.Single(r => r.Page.Kind == AssetPageKind.GeneratorFleet);
        AssetTreeNode ics = t.Roots.Single(r => r.Page.Kind == AssetPageKind.InterconnectorFleet);
        int plant = m.Generators.Count - links - infeeds;
        Assert.Equal(links.ToString(Inv), ics.Detail);
        Assert.Equal(plant.ToString(Inv), gens.Detail);

        // Each grid infeed is one unit node, directly under the substation bus it feeds, opening its generator page.

        List<AssetTreeNode> infeedNodes = t.PreOrder().Where(n => n.Kind == "grid infeed unit").ToList();
        Assert.Equal(infeeds, infeedNodes.Count);
        foreach (AssetTreeNode n in infeedNodes)
        {
            Assert.Equal(AssetNodeRole.Unit, n.Role);
            Assert.Equal(AssetPageKind.Generator, n.Page.Kind);
            Assert.NotNull(n.Parent);
            Assert.Equal(AssetPageKind.Substation, n.Parent!.Page.Kind);
            Assert.Equal(n.Page.BusA, n.Parent.Page.BusA);
            Assert.True(FuelTaxonomy.IsGridInfeed(m.Generators[n.Page.Index].Type));
        }

        // The landing page's INTERCONNECTORS and GENERATORS cards.
        SimFrame f = FrameOf(m);
        var x = new AssetExplorerModel();
        x.Update(f, "00:00");
        var home = Assert.IsType<AssetHomeModel<AssetClassCardModel>>(x.Home);
        Assert.Equal(links.ToString(Inv), home.Classes.Single(c => c.Title == "INTERCONNECTORS").Count);
        Assert.Equal(plant.ToString(Inv), home.Classes.Single(c => c.Title == "GENERATORS").Count);

        // The INTERCONNECTORS page: one row per link, none a grid infeed; its capacity is the links' own.
        var page = new InterconnectorFleetModel<LinkFleetRowModel>(x);
        page.Refresh(f);
        Assert.Equal(links, page.Links.Count);
        double linkCap = m.Generators.Where(g => g.Type.ToLowerInvariant() is "interconnector" or "hvdc" or "import").Sum(g => g.Pmax);
        Assert.Equal((linkCap / 1000.0).ToString("F1", Inv), page.TotalCap);
        double allCap = m.Generators.Where(g => FuelTaxonomy.Classify(g.Type).Category == FuelCategory.Interconnector).Sum(g => g.Pmax);
        output.WriteLine($"{name}: links {links}, grid infeeds {infeeds}; INTERCONNECTORS capacity {page.TotalCap} GW " +
                         $"(the old category rule read {(allCap / 1000.0).ToString("F1", Inv)} GW over {links + infeeds} rows)");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The GENERATION tab's import-links panel and net import
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_grid_infeed_is_not_an_import_link_and_not_in_the_net_import()
    {
        // A busy grid infeed (150 MW), one importing link and one CCGT. Before bug #28 the panel listed the
        // infeed as a link and added its output to the net import.
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[]
            {
                new Bus(1, BusType.Slack, BaseKv: 400),
                new Bus(2, BusType.Pq, Pd: 400, BaseKv: 400),
                new Bus(3, BusType.Pv, BaseKv: 400),
                new Bus(4, BusType.Pv, BaseKv: 400),
            },
            Branches = new[]
            {
                new Branch(1, 2, 0.01, 0.1, RateAMva: 1000),
                new Branch(3, 2, 0.01, 0.1, RateAMva: 1000),
                new Branch(4, 2, 0.01, 0.1, RateAMva: 1000),
            },
            Generators = new[]
            {
                new Generator(1, 150, Name: "Grid infeed @ boundary", Type: "grid", Pmax: 5000, PowerFlowOnly: true),
                new Generator(3, 150, Name: "Link A", Type: "interconnector", Pmax: 1000),
                new Generator(4, 100, Name: "CCGT B", Type: "ccgt", Pmax: 500),
            },
        };
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged);
        var f = new SimFrame(m, r, 12, 1, 50, 400, 400);
        Assert.False(StatusLampRules.IsIdleInterconnector(f.Model.Generators[0].Pg));   // the infeed is visibly busy

        var dash = new DashboardModel();
        dash.Update(f, Array.Empty<BoundaryFlow>());
        Assert.Equal(new[] { "Link A" }, dash.ImportLinks.Select(l => l.Name));
        Assert.Equal("+0.15 GW net import", dash.NetImport);
    }
}
