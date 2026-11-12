using GridSim.Gda;
using GridSim.Gda.Neso;
using Xunit;

namespace GridSim.Tests.Gda;

/// <summary>
/// Bug #209: whole-register mode (connectedOnly false) added every stage row of a multi-stage project at its own
/// cumulative MW, so a project was counted once per stage (Springwell: 400 + 800 + 1600 = 2800 MW against its 1600 MW
/// ultimate). It now counts each project (Project ID) once, at the cumulative figure of its LATEST stage: latest
/// "MW Effective From" first (blank = already in effect, sorts first), then highest Stage. A tie on both, a malformed date
/// or a malformed stage throws. Single-row projects keep CapacityMw (pinned by NationalFleetEmbeddedTests).
/// Tiny synthetic registers shaped like the real rows (no real data).
/// </summary>
public class Bug209WholeRegisterStageTests : IDisposable
{
    private const string TecHeader =
        "Project Name,Customer Name,Connection Site,Stage,MW Connected,MW Increase / Decrease,Cumulative Total Capacity (MW),MW Effective From,Project Status,Agreement Type,HOST TO,Plant Type,Project ID,Project Number,Gate\n";
    private const string EmbHeader =
        "Project Name,Customer Name,Connection Site,Stage,MW Connected,MW Increase / Decrease,Cumulative Total Capacity (MW),MW Effective From,Project Status,HOST TO,Plant Type,Project ID,Project Number,Gate\n";

    private readonly List<string> _roots = new();

    /// <summary>A lake root holding the given TEC rows and (optionally) embedded rows.</summary>
    private GdaPaths Lake(string tecRows, string? embRows = null)
    {
        string root = Path.Combine(Path.GetTempPath(), "gridsim-bug209-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        string tec = Path.Combine(root, "NESO", "RAWData", "Transmission Entry Capacity (TEC) register");
        Directory.CreateDirectory(tec);
        File.WriteAllText(Path.Combine(tec, "tec-register-test.csv"), TecHeader + tecRows);
        if (embRows is not null)
        {
            string emb = Path.Combine(root, "NESO", "RAWData", "Embedded Register");
            Directory.CreateDirectory(emb);
            File.WriteAllText(Path.Combine(emb, "embedded-register-test.csv"), EmbHeader + embRows);
        }
        return new GdaPaths(root);
    }

    [Fact]
    public void Bug209_a_three_stage_project_counts_once_at_its_last_stage_cumulative()
    {
        // The Springwell shape: three Scoping stages, cumulative 400 / 800 / 1600 MW.
        GdaPaths lake = Lake(
            "Springwell,Co,Site,1.00,0,400,400,2029-10-30,Scoping,Direct Connection,NGET,Solar,P1,1,\n" +
            "Springwell,Co,Site,2.00,0,400,800,2030-10-30,Scoping,Direct Connection,NGET,Solar,P1,2,\n" +
            "Springwell,Co,Site,3.00,0,800,1600,2033-06-01,Scoping,Direct Connection,NGET,Solar,P1,3,\n");
        NationalFleet f = NationalFleetBuilder.Build(lake, connectedOnly: false);

        Assert.Single(f.Units);
        Assert.Equal(1600.0, f.TotalCapacityMw);   // before the fix: 2800 MW from 3 units
    }

    [Fact]
    public void Bug209_partly_connected_later_stage_counts_the_project_ultimate_not_its_connected_part()
    {
        // The Thurrock shape: stage 1 Built, 300 MW connected; stage 2 commissioning, 75 MW connected so far, cumulative 750.
        GdaPaths lake = Lake(
            "Thurrock,Co,Site,1.00,300,0,300,,Built,Direct Connection,NGET,OCGT,P2,1,\n" +
            "Thurrock,Co,Site,2.00,75,375,750,2026-06-30,Under Construction/Commissioning,Direct Connection,NGET,OCGT,P2,2,\n");

        NationalFleet whole = NationalFleetBuilder.Build(lake, connectedOnly: false);
        Assert.Single(whole.Units);
        Assert.Equal(750.0, whole.TotalCapacityMw);   // before the fix: 300 + 75 = 375 MW (each row's connected MW)

        // The connected fleet is untouched by #209: each stage's connected MW, 300 + 75.
        NationalFleet connected = NationalFleetBuilder.Build(lake);
        Assert.Equal(300.0 + 75.0, connected.TotalCapacityMw);
    }

    [Fact]
    public void Bug209_effective_date_decides_before_stage_number_and_a_blank_date_is_earliest()
    {
        // Stage 3 is dated earlier than stage 2, so stage 2 (2035) is the latest; stage 1 has no date (in effect now).
        GdaPaths lake = Lake(
            "Odd Order,Co,Site,1.00,100,0,100,,Built,Direct Connection,NGET,CCGT,P3,1,\n" +
            "Odd Order,Co,Site,3.00,0,50,150,2030-01-01,Scoping,Direct Connection,NGET,CCGT,P3,3,\n" +
            "Odd Order,Co,Site,2.00,0,70,220,2035-01-01,Scoping,Direct Connection,NGET,CCGT,P3,2,\n");
        NationalFleet f = NationalFleetBuilder.Build(lake, connectedOnly: false);

        Assert.Single(f.Units);
        Assert.Equal(220.0, f.TotalCapacityMw);
    }

    [Fact]
    public void Bug209_same_date_and_stage_twice_is_ambiguous_and_throws()
    {
        GdaPaths lake = Lake(
            "Twin,Co,Site,2.00,0,100,300,2030-01-01,Scoping,Direct Connection,NGET,CCGT,P4,1,\n" +
            "Twin,Co,Site,2.00,0,200,400,2030-01-01,Scoping,Direct Connection,NGET,CCGT,P4,2,\n");
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => NationalFleetBuilder.Build(lake, connectedOnly: false));
        Assert.Contains("ambiguous", ex.Message);
        Assert.Contains("Twin", ex.Message);
    }

    [Theory]
    [InlineData("30/06/2026", "1.00", "not a yyyy-MM-dd date")]
    [InlineData("2026-06-30", "two", "not a number")]
    public void Bug209_a_malformed_date_or_stage_in_a_multi_stage_project_throws(string date, string stage, string expected)
    {
        GdaPaths lake = Lake(
            "Bad,Co,Site,1.00,0,100,100,2025-01-01,Scoping,Direct Connection,NGET,CCGT,P5,1,\n" +
            $"Bad,Co,Site,{stage},0,100,200,{date},Scoping,Direct Connection,NGET,CCGT,P5,2,\n");
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => NationalFleetBuilder.Build(lake, connectedOnly: false));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Bug209_embedded_multi_stage_projects_count_once_and_order_follows_first_appearance()
    {
        GdaPaths lake = Lake(
            "Solo,Co,Site,,500,0,500,,Built,Direct Connection,NGET,CCGT,T1,1,\n",
            "Wind A,Co,Local,1.00,0,25,25,2027-01-01,Scoping,NGET,Wind Onshore,E1,1,\n" +
            "Solar B,Co,Local,,40,20,60,2030-01-01,Built,NGET,Solar PV,E2,2,\n" +
            "Wind A,Co,Local,2.00,0,33,58,2029-01-01,Scoping,NGET,Wind Onshore,E1,3,\n");
        NationalFleet f = NationalFleetBuilder.Build(lake, connectedOnly: false);

        // Solo (single row) 500; Wind A once at 58 (was 25 + 58); Solar B single row keeps CapacityMw = 40 (connected
        // preferred, as NationalFleetEmbeddedTests pins). Order: TEC first, then embedded in first-appearance order.
        Assert.Equal(new[] { "Solo", "Wind A", "Solar B" }, f.Units.Select(u => u.Name).ToArray());
        Assert.Equal(new[] { 500.0, 58.0, 40.0 }, f.Units.Select(u => u.CapacityMw).ToArray());
        Assert.Equal(500.0 + 58.0 + 40.0, f.TotalCapacityMw);
    }

    [Fact]
    public void Bug209_rows_with_a_blank_project_id_are_never_merged()
    {
        GdaPaths lake = Lake(
            "Anon,Co,Site,1.00,0,100,100,2030-01-01,Scoping,Direct Connection,NGET,CCGT,,1,\n" +
            "Anon,Co,Site,2.00,0,100,200,2031-01-01,Scoping,Direct Connection,NGET,CCGT,,2,\n");
        NationalFleet f = NationalFleetBuilder.Build(lake, connectedOnly: false);
        Assert.Equal(2, f.Units.Count);
        Assert.Equal(100.0 + 200.0, f.TotalCapacityMw);
    }

    public void Dispose()
    {
        foreach (string r in _roots)
            try { Directory.Delete(r, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }
}
