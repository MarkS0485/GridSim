using GridSim.Gda;
using GridSim.Gda.Neso;
using Xunit;

namespace GridSim.Tests.Gda;

/// <summary>
/// F-038: the connected fleet counted a register row that looks connected (a "Connected" or "Built" status) but has
/// 0 MW connected at its future-inclusive cumulative figure. In the NESO TEC and Embedded registers "Cumulative Total
/// Capacity (MW)" is the running total after the row's increase takes effect on "MW Effective From", so it is never
/// a connected figure. The connected fleet now counts "MW Connected" only; whole-register mode is unchanged.
/// Tiny synthetic registers shaped like the real rows (no real data).
/// </summary>
public class F038ConnectedCapacityTests : IDisposable
{
    private readonly string _root;

    private const string TecHeader =
        "Project Name,Customer Name,Connection Site,Stage,MW Connected,MW Increase / Decrease,Cumulative Total Capacity (MW),MW Effective From,Project Status,Agreement Type,HOST TO,Plant Type,Project ID,Project Number,Gate\n";
    private const string EmbHeader =
        "Project Name,Customer Name,Connection Site,Stage,MW Connected,MW Increase / Decrease,Cumulative Total Capacity (MW),MW Effective From,Project Status,HOST TO,Plant Type,Project ID,Project Number,Gate\n";

    public F038ConnectedCapacityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gridsim-f038-" + Guid.NewGuid().ToString("N"));
        string neso = Path.Combine(_root, "NESO", "RAWData");

        // TEC register:
        //  * A connected CCGT, 800 MW connected (counted in both modes).
        //  * Stage 1 of a two-stage station: 1333 MW connected today.
        //  * Stage 2 of the same station: says "Connected" (as a register status could), 0 MW connected, a future
        //    48 MW increase taking the cumulative to 1881 MW. Before the fix the connected fleet added 1881 MW here.
        string tec = Path.Combine(neso, "Transmission Entry Capacity (TEC) register");
        Directory.CreateDirectory(tec);
        File.WriteAllText(Path.Combine(tec, "tec-register-test.csv"), TecHeader +
            "A CCGT,SSE,Keadby,,800,0,800,,Built,Direct Connection,NGET,CCGT,1,1,\n" +
            "Two Stage,Co,Site B,1,1333,500,1833,2027-10-30,Built,Direct Connection,NGET,CCGT,2,2,\n" +
            "Two Stage,Co,Site B,2,0,48,1881,2034-10-30,Connected,Direct Connection,NGET,CCGT,2,3,\n");

        // Embedded register: a Built storage project with 0 MW connected and 49.95 MW cumulative (the Camilla Farm
        // shape), beside a Built solar farm with 40 MW connected.
        string emb = Path.Combine(neso, "Embedded Register");
        Directory.CreateDirectory(emb);
        File.WriteAllText(Path.Combine(emb, "embedded-register-test.csv"), EmbHeader +
            "Solar Farm,Dev,Local,,40,0,40,,Built,NGET,Solar PV,9,9,\n" +
            "Storage Zero,Dev,Glenniston GSP,,0,49.95,49.95,2022-02-15,Built,NGET,Energy Storage System,10,10,\n");
    }

    [Fact]
    public void F038_connected_fleet_counts_mw_connected_only()
    {
        NationalFleet f = NationalFleetBuilder.Build(new GdaPaths(_root));

        // 800 + 1333 + 40 = 2173 MW from 3 units. Before the fix: 800 + 1333 + 1881 + 40 + 49.95 = 4103.95 MW from 5.
        Assert.Equal(3, f.Units.Count);
        Assert.Equal(800.0 + 1333.0 + 40.0, f.TotalCapacityMw);
        Assert.Single(f.Units, u => u.Name == "Two Stage");
        Assert.Equal(1333.0, f.Units.Single(u => u.Name == "Two Stage").CapacityMw);
        Assert.DoesNotContain(f.Units, u => u.Name == "Storage Zero");
    }

    [Fact]
    public void F038_whole_register_mode_keeps_the_planning_figure()
    {
        NationalFleet f = NationalFleetBuilder.Build(new GdaPaths(_root), connectedOnly: false);

        // Unchanged by F-038. Corrected 2026-11-01 for bug #209 (2.1.016), which this test predates by hours: the two
        // "Two Stage" rows are stages 1 and 2 of ONE project (Project ID 2), and whole-register mode now counts a
        // multi-stage project once, at its latest stage's cumulative total (1881 MW). The old expectation (5 units,
        // 1333 + 1881) was the per-stage double count #209 removed. The fixture is kept: the connected-fleet test above
        // needs both stages.
        Assert.Equal(4, f.Units.Count);
        Assert.Equal(800.0 + 1881.0 + 40.0 + 49.95, f.TotalCapacityMw);
        Assert.Single(f.Units, u => u.Name == "Two Stage");
        Assert.Equal(1881.0, f.Units.Single(u => u.Name == "Two Stage").CapacityMw);
    }

    [Theory]
    [InlineData("Built", 1333.0, 1833.0, 1333.0, 1333.0)]
    [InlineData("Connected", 0.0, 1881.0, 1881.0, 0.0)]   // the F-038 row: planning figure kept, connected figure 0
    [InlineData("Built", 0.0, 0.0, 0.0, 0.0)]             // a closed station still listed
    [InlineData("Scoping", 0.0, 400.0, 400.0, 0.0)]
    [InlineData("Built", -5.0, 10.0, 10.0, 0.0)]          // a negative MW Connected is never connected capacity
    public void F038_tec_entry_connected_and_planning_capacity(string status, double mwConnected, double cumulative,
        double planning, double connected)
    {
        var t = new TecEntry("P", "C", "Site", "", mwConnected, cumulative, status, "NGET", "CCGT", "1");
        Assert.Equal(planning, t.CapacityMw);
        Assert.Equal(connected, t.ConnectedCapacityMw);
    }

    [Theory]
    [InlineData("Built", 40.0, 60.0, 40.0, 40.0)]
    [InlineData("Built", 0.0, 49.95, 49.95, 0.0)]          // Camilla Farm shape
    [InlineData("Connected", 0.0, 25.0, 25.0, 0.0)]
    public void F038_embedded_entry_connected_and_planning_capacity(string status, double mwConnected, double cumulative,
        double planning, double connected)
    {
        var e = new EmbeddedEntry("P", "Site", mwConnected, cumulative, status, "NGET", "Energy Storage System");
        Assert.Equal(planning, e.CapacityMw);
        Assert.Equal(connected, e.ConnectedCapacityMw);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }
}
