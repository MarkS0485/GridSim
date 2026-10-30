using GridSim.Gda;
using GridSim.Gda.Neso;
using Xunit;

namespace GridSim.Tests.Gda;

/// <summary>
/// National-fleet aggregation on a tiny synthetic NESO register set (no real data).
/// </summary>
public class NationalFleetTests : IDisposable
{
    private readonly string _root;

    public NationalFleetTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gridsim-fleet-" + Guid.NewGuid().ToString("N"));
        string neso = Path.Combine(_root, "NESO", "RAWData");

        string tec = Path.Combine(neso, "Transmission Entry Capacity (TEC) register");
        Directory.CreateDirectory(tec);
        File.WriteAllText(Path.Combine(tec, "tec-register-test.csv"),
            "Project Name,Customer Name,Connection Site,Stage,MW Connected,MW Increase / Decrease,Cumulative Total Capacity (MW),MW Effective From,Project Status,Agreement Type,HOST TO,Plant Type,Project ID,Project Number,Gate\n" +
            "Big Nuclear,EDF,Sizewell,Built,1200,0,1200,2020,Connected,BEGA,NGET,Nuclear,1,1,G1\n" +
            "A CCGT,SSE,Keadby,Built,800,0,800,2018,Connected,BEGA,NGET,CCGT,2,2,G1\n" +
            "Big Wind,Orsted,Hornsea,Built,500,0,500,2021,Connected,BEGA,NGET,Wind Offshore,3,3,G1\n" +
            "A Battery,Zenobe,Capenhurst,Built,100,0,100,2023,Connected,BEGA,NGET,Battery Storage,4,4,G1\n");

        string ic = Path.Combine(neso, "Interconnector Register");
        Directory.CreateDirectory(ic);
        File.WriteAllText(Path.Combine(ic, "interconnector-register-test.csv"),
            "Project Name,Customer Name,Connection Site,Stage,MW Import - Current,MW Export - Current,MW Import - Increase / Decrease,MW Export - Increase / Decrease,MW Import - Total,MW Export - Total,MW Effective From,Project Status,HOST TO,Project Number,Gate\n" +
            "IFA,NG,Sellindge,Built,2000,2000,0,0,2000,2000,1986,Connected,NGET,1,G1\n");

        string emb = Path.Combine(neso, "Embedded Register");
        Directory.CreateDirectory(emb);
        File.WriteAllText(Path.Combine(emb, "embedded-register-test.csv"),
            "Project Name,Customer Name,Connection Site,Stage,MW Connected,MW Increase / Decrease,Cumulative Total Capacity (MW),MW Effective From,Project Status,HOST TO,Plant Type,Project ID,Project Number,Gate\n" +
            "Solar Farm,Lightsource,Local,Built,50,0,50,2022,Connected,NGET,Solar PV,9,9,G1\n");
    }

    [Fact]
    public void Aggregates_capacity_split_and_inertia()
    {
        NationalFleet f = NationalFleetBuilder.Build(new GdaPaths(_root));

        // 1200 + 800 + 500 + 100 + 2000 (IC) + 50 (solar) = 4650 MW
        Assert.Equal(4650, f.TotalCapacityMw, 0);
        // synchronous = nuclear + ccgt = 2000 MW
        Assert.Equal(2000, f.SynchronousCapacityMw, 0);
        // inertia = (5*1200 + 5*800) / 1000 = 10 GVAs
        Assert.Equal(10.0, f.InertiaGvaS, 3);
        // largest single unit is the 2000 MW interconnector
        Assert.Equal(2000, f.Largest.CapacityMw, 0);
        Assert.Contains(f.Categories, c => c.Type == "wind-offshore");
        Assert.Contains(f.Categories, c => c.Type == "storage");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }
}
