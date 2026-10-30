using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.Model;
using GridSim.Gda;
using GridSim.Gda.Ltds;
using GridSim.Gda.Npg;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N205 (CF2, October 2026; Ruling R1): the NGED canonical key names a SITE; where a site's demand sits on several buses
/// (its primaries of different voltages), generation joins by site AND voltage, never the last-listed bus; a voltage
/// that picks no bus or several leaves the row unplaced with a warning. "S.Stn" no longer leaves a stray "s" in the key.
/// N211: NPg's two spellings of the Harrogate group word (KNAREBOROUGH / KNARESBOROUGH) are read as one canonical
/// name, the alias kept for carves.
/// </summary>
public sealed partial class Cf2OpenCodeFindingsTests
{
    [Theory]
    [InlineData("Wigston Magna 33 11 kV S Stn", 11.0)]
    [InlineData("Wigston Magna 11", 11.0)]
    [InlineData("Courthouse Green 11", 11.0)]
    [InlineData("Courthouse Green 6.6", 6.6)]
    [InlineData("Cox Street 11 6.6 kV S Stn", 6.6)]
    [InlineData("St. Austell 33/11kV S.Stn", 11.0)]
    [InlineData("Pyle 33kV", 33.0)]
    [InlineData("66/11kV", 11.0)]
    public void N205_the_voltage_a_primary_name_carries_is_its_lowest_voltage_token(string name, double kv)
        => Assert.Equal(kv, GdaModelBuilder.CanonicalPrimaryVoltageKv(name));

    [Theory]
    [InlineData("Harbury S Stn")]
    [InlineData("Dillotford Avenue (T2)")]
    [InlineData("LoadSub")]
    public void N205_a_name_with_no_voltage_token_carries_none(string name)
        => Assert.Null(GdaModelBuilder.CanonicalPrimaryVoltageKv(name));

    [Fact]
    public void N205_s_dot_stn_is_a_station_word_and_every_n184_key_is_unchanged()
    {
        Assert.Equal("staustell", GdaModelBuilder.CanonicalPrimaryKey("St. Austell 33/11kV S.Stn"));
        Assert.Equal("staustell", GdaModelBuilder.CanonicalPrimaryKey("St Austell 11"));
        // The N184 rows, re-checked here against the widened station-word list.
        Assert.Equal("wigstonmagna", GdaModelBuilder.CanonicalPrimaryKey("Wigston Magna 33 11 kV S Stn"));
        Assert.Equal("courthousegreen", GdaModelBuilder.CanonicalPrimaryKey("Courthouse Green 6.6"));
        Assert.Equal("abhamsgp", GdaModelBuilder.CanonicalPrimaryKey("Abham S.G.P."));
        Assert.Equal("dillotfordavenuet2", GdaModelBuilder.CanonicalPrimaryKey("Dillotford Avenue (T2)"));
    }

    /// <summary>One NGED-shaped GSP: 132 kV GSP, a 33 kV primary busbar, and two primaries at one site, Courthouse Green
    /// 33/11 kV (node A11, 5 MW) and 33/6.6 kV (node B66, 3 MW), the 6.6 kV row listed LAST (the old rule's winner).</summary>
    private static LtdsDataset N205Dataset(params LtdsGeneration[] gen) => new(
        "test", ImpedanceConvention.OhmicReferredToGsp,
        new[]
        {
            new LtdsNode("NGED", "GSP_1", "Test GSP 132kV", "BSP1", "Test 132kV", "Energised"),
            new LtdsNode("NGED", "P33", "Courthouse Green 33kV", "BSP1", "Test 132kV", "Energised"),
            new LtdsNode("NGED", "A11", "Courthouse Green 11kV", "BSP1", "Test 132kV", "Energised"),
            new LtdsNode("NGED", "B66", "Courthouse Green 6.6kV", "BSP1", "Test 132kV", "Energised"),
        },
        Array.Empty<LtdsCircuit>(),
        new[]
        {
            new LtdsTransformer2("NGED", "Test 132kV", "GSP_1", 132, "P33", 33, "Energised", "Dyn11", 2, 50, 45, -10, 10, 90, 100),
            new LtdsTransformer2("NGED", "Test 132kV", "P33", 33, "A11", 11, "Energised", "Dyn11", 2, 50, 45, -10, 10, 30, 30),
            new LtdsTransformer2("NGED", "Test 132kV", "P33", 33, "B66", 6.6, "Energised", "Dyn11", 2, 50, 45, -10, 10, 20, 20),
        },
        Array.Empty<LtdsTransformer3>(),
        new[]
        {
            new LtdsDemand("Test 132kV", "BSP1", "Courthouse Green 11", "A11", 11, 5, 0.95, 10),
            new LtdsDemand("Test 132kV", "BSP1", "Courthouse Green 6.6", "B66", 6.6, 3, 0.95, 10),
        },
        gen)
    {
        CanonicalPrimaryJoin = true,
    };

    private static LtdsGeneration N205Gen(string primary, double kv, double mw) =>
        new("Test 132kV S Stn", "BSP1", primary, kv, mw, "Solar", "Connected");

    [Fact]
    public void N205_generation_at_a_two_voltage_site_joins_the_bus_of_its_own_voltage()
    {
        GdaBuildResult r = GdaModelBuilder.Build(N205Dataset(
            N205Gen("Courthouse Green 33 11 kV S Stn", 11, 4),       // name says 11 kV
            N205Gen("Courthouse Green 11 6.6 kV S Stn", 6.6, 2),     // name says 6.6 kV (its lowest)
            N205Gen("Courthouse Green S Stn", 11, 1)));              // no voltage in the name: its connection voltage
        int bus11 = r.Model.Buses.Single(b => b.Pd == 5.0).Id;
        int bus66 = r.Model.Buses.Single(b => b.Pd == 3.0).Id;
        Assert.NotEqual(bus11, bus66);
        Assert.Equal(bus11, r.Model.Generators.Single(g => g.Pmax == 4.0).Bus);
        Assert.Equal(bus66, r.Model.Generators.Single(g => g.Pmax == 2.0).Bus);
        Assert.Equal(bus11, r.Model.Generators.Single(g => g.Pmax == 1.0).Bus);
        Assert.Equal(3, r.Diagnostics.GenJoinedBySiteVoltage);
        Assert.Equal(0, r.Diagnostics.GenAmbiguousSiteVoltage);
        Assert.Equal(0, r.Diagnostics.UnmatchedGen);
        Assert.Equal(1, r.Diagnostics.PrimariesOnSeveralBuses);
        Assert.Contains(r.Diagnostics.Notes, n => n ==
            "Demand: 1 primary name(s) have demand rows on more than one bus within one GSP group; generation at those sites joins the bus of its own voltage (N205).");
        Assert.Contains(r.Diagnostics.Notes, n => n ==
            "Generation join: 3 row(s) at a site with demand on several buses joined by site and voltage (N205).");
    }

    [Fact]
    public void N205_a_voltage_that_picks_no_bus_leaves_the_row_unplaced_with_a_warning()
    {
        GdaBuildResult r = GdaModelBuilder.Build(N205Dataset(N205Gen("Courthouse Green S Stn", 33, 7.5)));
        Assert.Empty(r.Model.Generators);
        Assert.Equal(1, r.Diagnostics.UnmatchedGen);
        Assert.Equal(1, r.Diagnostics.GenAmbiguousSiteVoltage);
        Assert.Equal(7.5, r.Diagnostics.GenAmbiguousSiteVoltageMw);
        Assert.Contains(r.Diagnostics.Notes, n => n ==
            "WARNING generation join: 1 row(s), 7.5 MW export capacity, are at a site whose demand sits on several buses and " +
            "their voltage picks none or several of them (N205); left unplaced: 'Courthouse Green S Stn' (Test 132kV S Stn; 33 kV).");
    }

    [Fact]
    public void N205_a_one_bus_site_joins_exactly_as_before()
    {
        // Only the 11 kV primary has demand: one bus, so the voltage is not consulted (a 6.6 kV name still joins it).
        LtdsDataset one = N205Dataset(N205Gen("Courthouse Green 11 6.6 kV S Stn", 6.6, 2)) with
        {
            Demand = new[] { new LtdsDemand("Test 132kV", "BSP1", "Courthouse Green 11", "A11", 11, 5, 0.95, 10) },
        };
        GdaBuildResult r = GdaModelBuilder.Build(one);
        Assert.Equal(r.Model.Buses.Single(b => b.Pd == 5.0).Id, Assert.Single(r.Model.Generators).Bus);
        Assert.Equal(0, r.Diagnostics.GenJoinedBySiteVoltage);
        Assert.DoesNotContain(r.Diagnostics.Notes, n => n.Contains("N205", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------------------------------------ N211

    [Theory]
    [InlineData("HARROGATE/KNAREBOROUGH", "HARROGATE/KNARESBOROUGH")]
    [InlineData("Harrogate/Knareborough", "Harrogate/Knaresborough")]
    [InlineData("harrogate knareborough 132kv", "harrogate knaresborough 132kv")]
    [InlineData("HARROGATE/KNARESBOROUGH", "HARROGATE/KNARESBOROUGH")]
    [InlineData("KNAREBOROUGHS", "KNAREBOROUGHS")]          // whole words only
    [InlineData("Tees Valley", "Tees Valley")]
    public void N211_the_alias_word_reads_as_the_canonical_spelling(string published, string canonical)
        => Assert.Equal(canonical, GdaModelBuilder.ApplyGroupWordAliases(published, NpgSource.GroupWordAliases));

    [Fact]
    public void N211_the_alias_table_is_the_one_npg_spelling_pair()
    {
        Assert.Equal(new Dictionary<string, string> { ["KNAREBOROUGH"] = "KNARESBOROUGH" }, NpgSource.GroupWordAliases);
        Assert.Equal("anything", GdaModelBuilder.ApplyGroupWordAliases("anything", new Dictionary<string, string>()));
    }

    /// <summary>Minimal NPg RAWData: the circuit table spells the group KNARESBOROUGH, the transformer and load tables
    /// KNAREBOROUGH (as the published tables do).</summary>
    private static string N211Fixture()
    {
        string root = Path.Combine(Path.GetTempPath(), "gridsim-n211-npg-" + Guid.NewGuid().ToString("N"));
        string raw = Path.Combine(root, "NorthernPowerGrid", "RAWData");
        void Write(string dataset, string csv)
        {
            Directory.CreateDirectory(Path.Combine(raw, dataset));
            File.WriteAllText(Path.Combine(raw, dataset, dataset + ".csv"), csv);
        }
        Write("LTDS_Appendix_3_Circuit_Data",
            "License,S/S Group,S/S or Busbar Name Node 1,S/S or Busbar Name Node 2,Operating Voltage (kV),Positive Sequence Impedance R (% on 100 MVA Base),Positive Sequence Impedance X (% on 100 MVA Base),Susceptance B (% on 100 MVA Base),Rating (MVA),Circuit Length (km)\n" +
            "NPG,HARROGATE/KNARESBOROUGH,GRID A,WHITBY,66,1,5,0.1,60,10\n");
        Write("LTDS_Appendix_4_Transformer_Data",
            "License,S/S Group,S/S or Busbar Name Node,Primary Voltage (kV),Secondary Voltage (kV),Vector Group,Positive Sequence Impedance R (% on 100 MVA Base),Positive Sequence Impedance X (% on 100 MVA Base),Zero Sequence Reactance X (% on 100 MVA Base),Minimum Tap %,Maximum Tap %,Transformer Rating (MVA)\n" +
            "NPG,HARROGATE/KNAREBOROUGH,WHITBY,66,11.5,Dyn11,1,20,18,-10,10,20\n");
        Write("LTDS_Appendix_5_Load_Information",
            "S/S Group,S/S or Busbar Name,Voltage Level (kV),2024/25 Net (Observed) Maximum Load (MVA),2024/25 Net (Observed) Maximum Load (PF),Firm Capacity (MVA)\n" +
            "HARROGATE/KNAREBOROUGH,WHITBY,66,5,0.95,10\n");
        return root;
    }

    [Fact]
    public void N211_npg_reads_one_group_name_and_a_carve_with_either_spelling_selects_it()
    {
        string root = N211Fixture();
        try
        {
            LtdsDataset ds = NpgSource.Load(root, preferParquet: false);
            Assert.Equal(new[] { "HARROGATE/KNARESBOROUGH" }, GdaModelBuilder.GspGroups(ds));
            Assert.All(ds.Transformers2, t => Assert.Equal("HARROGATE/KNARESBOROUGH", t.GspGroup));
            Assert.All(ds.Demand, d => Assert.Equal("HARROGATE/KNARESBOROUGH", d.GspGroup));
            Assert.Contains(ds.SourceNotes, n => n ==
                "S/S Group: 2 cell(s) spell a group word two ways and were read in the canonical spelling " +
                "('HARROGATE/KNAREBOROUGH' -> 'HARROGATE/KNARESBOROUGH'; N211).");
            Assert.True(GdaModelBuilder.IsKnownGspGroup(ds, "Harrogate/Knareborough"));
            Assert.True(GdaModelBuilder.IsKnownGspGroup(ds, "Harrogate/Knaresborough"));

            GdaBuildResult byAlias = GdaModelBuilder.Build(ds, new GdaBuildOptions { GspGroup = "Harrogate/Knareborough" });
            GdaBuildResult byName = GdaModelBuilder.Build(ds, new GdaBuildOptions { GspGroup = "Harrogate/Knaresborough" });
            Assert.True(byName.Diagnostics.Buses > 0);
            Assert.Equal(byName.Diagnostics.Buses, byAlias.Diagnostics.Buses);
            Assert.Equal(byName.Diagnostics.TotalDemandMw, byAlias.Diagnostics.TotalDemandMw);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
