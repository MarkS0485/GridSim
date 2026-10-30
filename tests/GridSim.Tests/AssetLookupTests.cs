using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Presentation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R055: the WPF asset explorer and GENERATION import-links panel keyed units and branches by NAME, collapsing
/// gb-full's namesakes. The shared Core <see cref="AssetLookup"/> gives index identity, confirmed by the element's
/// own fields. (The WPF pages themselves are view code the test project cannot reference; the identity rule they
/// now call is tested here.)
/// </summary>
public class AssetLookupTests
{
    private static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");

    private static GridModel TwoNamesakes() => new()
    {
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 275), new Bus(2, BusType.Pq, Pd: 300, BaseKv: 275) },
        Branches = new[]
        {
            new Branch(1, 2, 0.01, 0.1, Name: "Dup"),
            new Branch(1, 2, 0.02, 0.2, Name: "Dup"),
        },
        Generators = new[]
        {
            new Generator(2, 125.1, Type: "interconnector", Name: "Auchencrosh"),
            new Generator(2, 220.2, Type: "interconnector", Name: "Auchencrosh"),
            new Generator(1, 0, Type: "CCGT", Name: "Slack unit"),
        },
    };

    [Fact]
    public void Generator_lookup_returns_the_unit_at_its_own_index_not_the_first_namesake()
    {
        GridModel m = TwoNamesakes();
        (Generator Unit, int Index)? second = AssetLookup.FindGenerator(m, 1, 2, "Auchencrosh");
        Assert.NotNull(second);
        Assert.Equal(1, second.Value.Index);
        Assert.Equal(220.2, second.Value.Unit.Pg);   // name lookup (FirstOrDefault) returned 125.1
    }

    [Fact]
    public void Generator_lookup_refuses_to_guess_between_namesakes_when_the_index_is_stale()
    {
        GridModel m = TwoNamesakes();
        // Index 5 does not exist; two units share bus 2 + "Auchencrosh", so the answer is "not found", not the first.
        Assert.Null(AssetLookup.FindGenerator(m, 5, 2, "Auchencrosh"));
        // A unique bus + name is found even from a stale index (the model was reordered).
        Assert.Equal(2, AssetLookup.FindGenerator(m, 0, 1, "Slack unit")!.Value.Index);
        // A unit that is gone is null.
        Assert.Null(AssetLookup.FindGenerator(m, 0, 9, "Nowhere"));
    }

    [Fact]
    public void Branch_flow_is_read_by_index_so_namesakes_show_their_own_flow()
    {
        GridModel m = TwoNamesakes();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);

        LineFlow? a = AssetLookup.FlowAt(r, 0);
        LineFlow? b = AssetLookup.FlowAt(r, 1);
        Assert.NotNull(a);
        Assert.NotNull(b);
        // The parallel circuits have different impedances, so they carry different flows; matching by name
        // gave the first circuit's flow for both.
        Assert.NotEqual(a.PFromMw, b.PFromMw);
        Assert.Same(r.LineFlows[1], b);
        Assert.Null(AssetLookup.FlowAt(r, 2));
        Assert.Null(AssetLookup.FlowAt(r, -1));

        Assert.Equal(1, AssetLookup.FindBranch(m, 1, 1, 2, "Dup")!.Value.Index);
        Assert.Null(AssetLookup.FindBranch(m, 7, 1, 2, "Dup"));   // stale index + ambiguous ends/name
    }

    [Fact]
    public void Gb_full_has_the_namesakes_the_finding_describes()
    {
        GridModel m = JsonGridLoader.LoadDirectory(Path.Combine(DataDir, "gb-full"));
        var links = m.Generators.Select((g, i) => (g, i))
            .Where(x => x.g.Name == "Auchencrosh (interconnector CCT) interconnector").ToList();
        Assert.Equal(2, links.Count);
        // Each resolves to itself by index: two distinct rows, two distinct outputs.
        foreach ((Generator g, int i) in links)
            Assert.Equal(i, AssetLookup.FindGenerator(m, i, g.Bus, g.Name)!.Value.Index);
        Assert.NotEqual(links[0].g.Pg, links[1].g.Pg);
    }
}
