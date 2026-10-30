using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Web.Projection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R053: the NESO RP1 correlated-corridor detection matched link names with exact ordinal equality against the
/// short ids "IFA", "ElecLink", "NSL", "Viking", so it never matched gb-full's model names and the North Sea
/// corridor never fired on any model. Membership is now by canonical id (Core <see cref="InterconnectorCorridors"/>).
/// </summary>
public class InterconnectorCorridorTests
{
    private static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");

    [Theory]
    // gb-full generation.json names (raw and after the web DedupeWords pass)
    [InlineData("IFA Interconnector interconnector", "IFA")]
    [InlineData("IFA Interconnector", "IFA")]
    [InlineData("ElecLink interconnector", "ElecLink")]
    [InlineData("NS Link interconnector", "NSL")]
    [InlineData("Viking Link Denmark Interconnector interconnector", "Viking")]
    [InlineData("Viking Link Denmark Interconnector", "Viking")]
    // uk / gb-synthetic names
    [InlineData("IFA", "IFA")]
    [InlineData("ElecLink", "ElecLink")]
    [InlineData("North Sea Link", "NSL")]
    [InlineData("Viking Link", "Viking")]
    // bridge raw block ids (BridgeProtocol.RawInterconnectorsBlock.Rows)
    [InlineData("NSL", "NSL")]
    [InlineData("Viking", "Viking")]
    public void Corridor_members_are_recognised_under_every_source_spelling(string name, string member)
        => Assert.Equal(member, InterconnectorCorridors.CanonicalMember(name));

    [Theory]
    [InlineData("IFA2")]
    [InlineData("IFA2 Interconnector interconnector")]
    [InlineData("Nemo Link interconnector")]
    [InlineData("NemoLink")]
    [InlineData("BritNed")]
    [InlineData("Auchencrosh (interconnector CCT) interconnector")]
    [InlineData("East West Interconnector interconnector")]
    [InlineData("Greenlink")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_links_are_not_corridor_members(string? name)
    {
        Assert.Null(InterconnectorCorridors.CanonicalMember(name));
        Assert.Null(InterconnectorCorridors.CorridorOf(name));
    }

    [Fact]
    public void Corridor_names_and_members_are_unchanged()
    {
        // The table moved from two private copies (web + WPF) into Core; its content is pinned here.
        Assert.Equal(2, InterconnectorCorridors.All.Count);
        Assert.Equal("Sellindge (IFA+ElecLink)", InterconnectorCorridors.All[0].Name);
        Assert.Equal(new[] { "IFA", "ElecLink" }, InterconnectorCorridors.All[0].Members);
        Assert.Equal("North Sea (NSL+Viking)", InterconnectorCorridors.All[1].Name);
        Assert.Equal(new[] { "NSL", "Viking" }, InterconnectorCorridors.All[1].Members);
    }

    /// <summary>The finding's failure scenario on gb-full names: IFA importing 994 MW, ElecLink 500 MW.</summary>
    private static GridModel GbFullNamedLinks(double ifa, double elec, double nsl, double viking) => new()
    {
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 400), new Bus(2, BusType.Pq, Pd: 4000, BaseKv: 400) },
        Branches = new[] { new Branch(1, 2, 0.001, 0.01) },
        Generators = new[]
        {
            new Generator(1, 1500, MBase: 1800, H: 5, Type: "CCGT", Name: "Big CCGT"),
            new Generator(2, ifa, Type: "interconnector", Name: "IFA Interconnector interconnector"),
            new Generator(2, elec, Type: "interconnector", Name: "ElecLink interconnector"),
            new Generator(2, nsl, Type: "interconnector", Name: "NS Link interconnector"),
            new Generator(2, viking, Type: "interconnector", Name: "Viking Link Denmark Interconnector interconnector"),
        },
    };

    [Fact]
    public void Web_rp1_flags_gb_full_named_links_and_sums_the_sellindge_group()
    {
        GridModel m = GbFullNamedLinks(ifa: 994, elec: 500, nsl: 700, viking: -300);
        var f = new SimFrame(m, new NewtonRaphsonPowerFlow().Solve(m), Hour: 0, Tick: 0, FrequencyHz: 50,
            GenerationMw: m.Generators.Sum(g => g.Pg), DemandMw: 4000);
        Rp1Dto rp1 = TabProjections.Rp1(f, new FuelBreakdown(f.Model));

        // Before the fix: CorrGroup was ("", 0) and no row was correlated.
        Assert.Equal("Sellindge (IFA+ElecLink)", rp1.CorrelatedGroup.Name);
        Assert.Equal(1494.0, rp1.CorrelatedGroup.Mw);                          // 994 + 500, exact
        Assert.Equal(new[] { "IFA Interconnector", "ElecLink interconnector" },
            rp1.CorrelatedGroup.Members.Select(r => r.Name).ToArray());
        // North Sea: 700 MW importing NSL + an EXPORTING Viking (-300) = 700 MW, below Sellindge.
        Assert.All(rp1.Interconnectors, r => Assert.True(r.Correlated));
        Assert.Equal("North Sea (NSL+Viking)",
            rp1.Interconnectors.Single(r => r.Name == "NS Link interconnector").CorridorName);
    }

    [Fact]
    public void Web_rp1_north_sea_group_now_fires_on_model_names()
    {
        GridModel m = GbFullNamedLinks(ifa: 100, elec: 0, nsl: 1400, viking: 1400);
        var f = new SimFrame(m, new NewtonRaphsonPowerFlow().Solve(m), Hour: 0, Tick: 0, FrequencyHz: 50,
            GenerationMw: m.Generators.Sum(g => g.Pg), DemandMw: 4000);
        Rp1Dto rp1 = TabProjections.Rp1(f, new FuelBreakdown(f.Model));
        Assert.Equal("North Sea (NSL+Viking)", rp1.CorrelatedGroup.Name);
        Assert.Equal(2800.0, rp1.CorrelatedGroup.Mw);
    }

    [Fact]
    public void Web_rp1_raw_short_ids_give_the_same_group_as_before()
    {
        // The raw path (bridge short ids) already matched; it must be bit-identical after the change.
        GridModel m = GbFullNamedLinks(0, 0, 0, 0);
        var f = new SimFrame(m, new NewtonRaphsonPowerFlow().Solve(m), Hour: 0, Tick: 0, FrequencyHz: 50,
            GenerationMw: 1500, DemandMw: 4000);
        var raw = new List<(string Name, double Mw)> { ("IFA", 994.25), ("ElecLink", 500.5), ("NSL", -10), ("Viking", 30) };
        Rp1Dto rp1 = TabProjections.Rp1(f, new FuelBreakdown(f.Model), raw);
        Assert.Equal("Sellindge (IFA+ElecLink)", rp1.CorrelatedGroup.Name);
        Assert.Equal(994.25 + 500.5, rp1.CorrelatedGroup.Mw);
    }

    [Fact]
    public void Gb_full_rp1_correlated_group_matches_an_independent_sum()
    {
        GridModel model = JsonGridLoader.LoadDirectory(Path.Combine(DataDir, "gb-full"));
        var solver = new SteadyStateSolver();
        PowerFlowResult r = solver.Solve(model);
        GridModel solved = solver.RegulatedModel;
        var f = new SimFrame(solved, r, Hour: 0, Tick: 0, FrequencyHz: 50,
            GenerationMw: solved.Generators.Sum(g => g.Pg), DemandMw: solved.Buses.Sum(b => b.Pd));
        Rp1Dto rp1 = TabProjections.Rp1(f, new FuelBreakdown(f.Model));

        // Independent: positive live import of the named gb-full units, by exact name.
        double Pos(string n) => Math.Max(0, solved.Generators.Single(g => g.Name == n).Pg);
        double sellindge = Pos("IFA Interconnector interconnector") + Pos("ElecLink interconnector");
        double northSea = Pos("NS Link interconnector") + Pos("Viking Link Denmark Interconnector interconnector");
        double expected = Math.Max(sellindge, northSea);
        Assert.True(expected > 0, "gb-full has importing corridor links at its solved start point");
        Assert.Equal(expected, rp1.CorrelatedGroup.Mw);
    }
}
