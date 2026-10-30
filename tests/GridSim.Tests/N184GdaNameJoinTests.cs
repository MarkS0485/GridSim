using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.Analysis;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Core.Synthetic;
using GridSim.Gda;
using GridSim.Gda.Ltds;
using GridSim.Gda.Npg;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N184 (Ruling R1, Mark delegated 2026-10-04): GDA name joins. NGED generation joins its demand primaries on the
/// canonical primary key; NPg demand joins a node by site name and nearest voltage within 5 % when its exact
/// (name, voltage) key names none.
/// </summary>
public sealed class N184GdaNameJoinTests
{
    private static string Root() => CliProcess.Root();

    // =============================================================================================================
    // N184: canonical primary key (NGED generation) and the NPg demand site join
    // =============================================================================================================

    [Theory]
    [InlineData("Wigston Magna 33 11 kV S Stn", "wigstonmagna")]
    [InlineData("Wigston Magna 11", "wigstonmagna")]
    [InlineData("Courthouse Green 11", "courthousegreen")]
    [InlineData("Courthouse Green 6.6", "courthousegreen")]
    [InlineData("Harbury 11 kV S Stn", "harbury")]
    [InlineData("Pyle 33kV", "pyle")]
    [InlineData("Cox Street 6.6", "coxstreet")]
    [InlineData("Abham S.G.P.", "abhamsgp")]
    [InlineData("Dillotford Avenue (T2)", "dillotfordavenuet2")]
    [InlineData("LoadSub", "loadsub")]
    [InlineData("Grid", "")]
    [InlineData("66/11kV", "")]
    public void N184_canonical_primary_key(string name, string key)
        => Assert.Equal(key, GdaModelBuilder.CanonicalPrimaryKey(name));

    /// <summary>A one-GSP NGED-shaped dataset whose demand table calls the primary "Erin Road 11" and whose generation
    /// table calls it "Erin Road 33 11 kV S Stn" (the two vocabularies of the real tables).</summary>
    private static LtdsDataset NgedShaped(bool canonical) => new(
        "test", ImpedanceConvention.OhmicReferredToGsp,
        new[]
        {
            new LtdsNode("NGED", "GSP_1", "Test GSP 132kV", "BSP1", "Test 132kV", "Energised"),
            new LtdsNode("NGED", "P33", "Erin Road 33kV", "BSP1", "Test 132kV", "Energised"),
        },
        Array.Empty<LtdsCircuit>(),
        new[] { new LtdsTransformer2("NGED", "Test 132kV", "GSP_1", 132, "P33", 33, "Energised", "Dyn11", 2, 50, 45, -10, 10, 90, 100) },
        Array.Empty<LtdsTransformer3>(),
        new[] { new LtdsDemand("Test 132kV", "BSP1", "Erin Road 11", "P33", 33, 20, 0.95, 30) },
        new[] { new LtdsGeneration("Test 132kV S Stn", "BSP1", "Erin Road 33 11 kV S Stn", 33, 10, "Solar", "Connected") })
    {
        CanonicalPrimaryJoin = canonical,
    };

    [Fact]
    public void N184_the_canonical_key_places_ngeds_differently_spelt_generation_and_the_old_key_does_not()
    {
        GdaBuildResult legacy = GdaModelBuilder.Build(NgedShaped(canonical: false));
        Assert.DoesNotContain(legacy.Model.Generators, g => (g.Name ?? "").StartsWith("Erin Road", StringComparison.Ordinal));
        Assert.Equal(1, legacy.Diagnostics.UnmatchedGen);

        GdaBuildResult canon = GdaModelBuilder.Build(NgedShaped(canonical: true));
        Generator solar = Assert.Single(canon.Model.Generators, g => (g.Name ?? "").StartsWith("Erin Road 33 11 kV S Stn", StringComparison.Ordinal));
        Assert.Equal(10.0, solar.Pmax);
        Assert.Equal(0, canon.Diagnostics.UnmatchedGen);
        Assert.Contains(canon.Diagnostics.Notes, n => n.StartsWith("Generation join key: canonical primary name", StringComparison.Ordinal));
        Assert.DoesNotContain(legacy.Diagnostics.Notes, n => n.StartsWith("Generation join key:", StringComparison.Ordinal));
    }

    [Fact]
    public void N184_only_the_nged_loader_asks_for_the_canonical_key()
    {
        // The flag defaults off, so NPg, UKPN and every caller-built dataset join exactly as before.
        Assert.False(NgedShaped(canonical: false).CanonicalPrimaryJoin);
        string src = File.ReadAllText(Path.Combine(Root(), "src", "GridSim.Gda", "GdaModelBuilder.cs"));
        Assert.Contains("CanonicalPrimaryJoin = true", src, StringComparison.Ordinal);
        Assert.DoesNotContain("CanonicalPrimaryJoin", File.ReadAllText(Path.Combine(Root(), "src", "GridSim.Gda", "Npg", "NpgSource.cs")), StringComparison.Ordinal);
        Assert.DoesNotContain("CanonicalPrimaryJoin", File.ReadAllText(Path.Combine(Root(), "src", "GridSim.Gda", "Ukpn", "UkpnSource.cs")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Annfield 66/20kV", "ANNFIELD")]
    [InlineData("Blyth 66kV", "BLYTH")]
    [InlineData("  whitby   T2 ", "WHITBY T2")]
    [InlineData("Potter House 11 KV", "POTTER HOUSE")]
    [InlineData("Consett", "CONSETT")]
    public void N184_npg_site_name(string name, string site) => Assert.Equal(site, NpgSource.SiteName(name));

    /// <summary>Minimal NPg RAWData CSVs: one 66 kV circuit, one 66/11.5 kV transformer at WHITBY, and loads.</summary>
    private static string NpgFixture()
    {
        string root = Path.Combine(Path.GetTempPath(), "gridsim-r1-npg-" + Guid.NewGuid().ToString("N"));
        string raw = Path.Combine(root, "NorthernPowerGrid", "RAWData");
        void Write(string dataset, string csv)
        {
            Directory.CreateDirectory(Path.Combine(raw, dataset));
            File.WriteAllText(Path.Combine(raw, dataset, dataset + ".csv"), csv);
        }
        Write("LTDS_Appendix_3_Circuit_Data",
            "License,S/S Group,S/S or Busbar Name Node 1,S/S or Busbar Name Node 2,Operating Voltage (kV),Positive Sequence Impedance R (% on 100 MVA Base),Positive Sequence Impedance X (% on 100 MVA Base),Susceptance B (% on 100 MVA Base),Rating (MVA),Circuit Length (km)\n" +
            "NPG,Test,GRID A,WHITBY,66,1,5,0.1,60,10\n");
        Write("LTDS_Appendix_4_Transformer_Data",
            "License,S/S Group,S/S or Busbar Name Node,Primary Voltage (kV),Secondary Voltage (kV),Vector Group,Positive Sequence Impedance R (% on 100 MVA Base),Positive Sequence Impedance X (% on 100 MVA Base),Zero Sequence Reactance X (% on 100 MVA Base),Minimum Tap %,Maximum Tap %,Transformer Rating (MVA)\n" +
            "NPG,Test,WHITBY,66,11.5,Dyn11,1,20,18,-10,10,20\n");
        Write("LTDS_Appendix_5_Load_Information",
            "S/S Group,S/S or Busbar Name,Voltage Level (kV),2024/25 Net (Observed) Maximum Load (MVA),2024/25 Net (Observed) Maximum Load (PF),Firm Capacity (MVA)\n" +
            "Test,WHITBY,66,5,0.95,10\n" +          // exact key WHITBY|66: unchanged
            "Test,Whitby T2,11,4,0.95,10\n" +       // site WHITBY, 11 vs 11.5 kV (4.5 %): joins WHITBY|11.5
            "Test,Whitby 66kV,66,3,0.95,10\n" +     // site WHITBY, exact voltage: joins WHITBY|66
            "Test,Whitby,33,2,0.95,10\n" +          // no node within 5 % of 33 kV: keeps its own key (outside)
            "Test,Nowhere,11,1,0.95,10\n");         // unknown site: keeps its own key (outside)
        return root;
    }

    [Fact]
    public void N184_npg_demand_joins_by_site_name_and_nearest_voltage_within_five_percent()
    {
        string root = NpgFixture();
        try
        {
            LtdsDataset ds = NpgSource.Load(root, preferParquet: false);
            Assert.Equal(new[] { "WHITBY|66", "WHITBY|11.5", "WHITBY|66", "WHITBY|33", "NOWHERE|11" }, ds.Demand.Select(d => d.Node));
            // The published name and voltage are kept on the row; only the node it joins changes.
            Assert.Equal("Whitby T2", ds.Demand[1].PrimarySubstation);
            Assert.Equal(11.0, ds.Demand[1].Kv);
            Assert.Equal(0.05, NpgSource.SiteJoinVoltageTolerance);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
