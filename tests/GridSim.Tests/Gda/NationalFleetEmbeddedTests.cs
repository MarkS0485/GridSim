using GridSim.Gda;
using GridSim.Gda.Neso;
using Xunit;

namespace GridSim.Tests.Gda;

/// <summary>
/// R016 (October 2026 review): the Embedded Register must obey the same connected-only
/// rule as the TEC and Interconnector registers, and a connected project must count its
/// connected MW, not its future cumulative MW. Tiny synthetic registers, no real data.
/// </summary>
public class NationalFleetEmbeddedTests : IDisposable
{
    private readonly string _root;

    public NationalFleetEmbeddedTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gridsim-fleet-emb-" + Guid.NewGuid().ToString("N"));
        string neso = Path.Combine(_root, "NESO", "RAWData");

        // One connected 1000 MW TEC unit so the fleet is never empty.
        string tec = Path.Combine(neso, "Transmission Entry Capacity (TEC) register");
        Directory.CreateDirectory(tec);
        File.WriteAllText(Path.Combine(tec, "tec-register-test.csv"),
            "Project Name,Customer Name,Connection Site,Stage,MW Connected,MW Increase / Decrease,Cumulative Total Capacity (MW),MW Effective From,Project Status,Agreement Type,HOST TO,Plant Type,Project ID,Project Number,Gate\n" +
            "Big CCGT,SSE,Keadby,Built,1000,0,1000,2018,Connected,BEGA,NGET,CCGT,1,1,G1\n");

        // Embedded register:
        //  * Built solar: 40 MW connected today, 60 MW cumulative after a future increase.
        //  * Scoping wind: nothing connected, 300 MW cumulative (a project, not a plant).
        //  * Awaiting-consents CCGT: nothing connected, 500 MW cumulative, synchronous.
        string emb = Path.Combine(neso, "Embedded Register");
        Directory.CreateDirectory(emb);
        File.WriteAllText(Path.Combine(emb, "embedded-register-test.csv"),
            "Project Name,Customer Name,Connection Site,Stage,MW Connected,MW Increase / Decrease,Cumulative Total Capacity (MW),MW Effective From,Project Status,HOST TO,Plant Type,Project ID,Project Number,Gate\n" +
            "Solar Farm,Lightsource,Local,Built,40,20,60,2030,Built,NGET,Solar PV,9,9,G1\n" +
            "Wind Project,Dev,Local,Scoping,0,300,300,2031,Scoping,NGET,Wind Onshore,10,10,G2\n" +
            "Gas Project,Dev,Local,Consents,0,500,500,2032,Awaiting Consents,NGET,CCGT,11,11,G2\n");
    }

    [Fact]
    public void Connected_fleet_excludes_unbuilt_embedded_projects_and_counts_connected_mw()
    {
        NationalFleet f = NationalFleetBuilder.Build(new GdaPaths(_root));

        // 1000 (TEC CCGT) + 40 (solar, connected MW only) = 1040 MW. Before the R016 fix
        // this was 1000 + 60 + 300 + 500 = 1860 MW with 4 units and 7.5 GVAs.
        Assert.Equal(1040.0, f.TotalCapacityMw);
        Assert.Equal(2, f.Units.Count);
        Assert.DoesNotContain(f.Units, u => u.Name == "Wind Project");
        Assert.DoesNotContain(f.Units, u => u.Name == "Gas Project");
        Assert.Equal(40.0, f.Units.Single(u => u.Name == "Solar Farm").CapacityMw);
        // Inertia counts only the TEC CCGT: H = 5 s on 1000 MW = 5 GVAs. The unbuilt
        // 500 MW embedded CCGT would have added 2.5 GVAs.
        Assert.Equal(5.0, f.InertiaGvaS);
    }

    [Fact]
    public void Whole_register_mode_still_includes_every_project()
    {
        NationalFleet f = NationalFleetBuilder.Build(new GdaPaths(_root), connectedOnly: false);

        // 1000 + 40 (built solar: connected MW preferred, as TecEntry) + 300 + 500 = 1840 MW
        // (was 1860 MW: the solar counted its future 60 MW cumulative figure).
        Assert.Equal(1840.0, f.TotalCapacityMw);
        Assert.Equal(4, f.Units.Count);
    }

    [Theory]
    [InlineData("Built", 40, 60, true, 40)]
    [InlineData("Connected", 0, 25, true, 25)]           // connected status, MW column blank: cumulative fallback
    [InlineData("Under Construction", 10, 50, true, 10)] // partly energised: connected MW counts
    [InlineData("Scoping", 0, 300, false, 300)]
    [InlineData("Awaiting Consents", 0, 500, false, 500)]
    public void Embedded_entry_connection_and_capacity_rules(string status, double mwConnected, double cumulative, bool connected, double capacity)
    {
        var e = new EmbeddedEntry("P", "Site", mwConnected, cumulative, status, "NGET", "Solar PV");
        Assert.Equal(connected, e.IsConnected);
        Assert.Equal(capacity, e.CapacityMw);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }
}
