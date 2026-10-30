using System.Text;
using GridSim.Core.Model;
using GridSim.Gda;
using GridSim.Gda.Ltds;
using GridSim.Gda.Ukpn;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N194 (October 2026): the UKPN replica built from the full lake did not converge (sparse NR 60 iterations,
/// mismatch 1.66, V 0.266 to 1.302 pu). The diagnosis found six reader / builder defects, each fixed for UKPN only
/// (NGED and NPg build byte-identically; their own tests are untouched). These tests pin each fix on small CSV lakes
/// (the same reader path the full CSVs take) or on hand-built datasets:
/// <list type="number">
/// <item>Transformer impedances are percent on 100 MVA, not on the unit's rating.</item>
/// <item>The three-winding table (LTDS Table 2b) is read.</item>
/// <item>A node code used for different substations in different licence areas is kept apart per area; a boundary
/// site published by two areas stays one node.</item>
/// <item>A substation's demand is shared over the transformer LV nodes that feed it (busbar sections).</item>
/// <item>A grid board's demand row, the sum of the primaries behind it, carries no load (join-only), and generation
/// naming that board still joins it.</item>
/// <item>GSP infeeds are placed per GSP group per island, at every section of the GSP site.</item>
/// </list>
/// </summary>
public sealed class N194UkpnReplicaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gridsim-n194-{Guid.NewGuid():N}");

    public N194UkpnReplicaTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private const string Epn = "Eastern Power Networks (EPN)";
    private const string Lpn = "London Power Networks (LPN)";

    private const string CircuitHeader =
        "GridSupplyPoint,FromSubstation,FromNode,ToNode,ToSubstation,LineName,OperatingVoltage (kV)," +
        "Positive_Sequence_Impedance_R_Percent,Positive_Sequence_Impedance_X_Percent,Susceptance_B_ Percent," +
        "Rating_Amps_Summer,Rating_Amps_Winter,Circuit_Length (km),LicenceArea";

    private const string Tx2Header =
        "GridSupplyPoint,HV_Node,HV_Substation,Voltage_HV (kV),LV_Node,LV_Substation,Voltage_LV (kV),Vector_Group," +
        "Positive_Sequence_Impedance_R_Percent,Positive_Sequence_Impedance_X_Percent,Zero_Sequence_Impedance_X_Percent," +
        "Tap_Range_Max_Percent,Tap_Range_Min_Percent,Transformer_Rating_MVA_Winter,Transformer_Rating_MVA_Summer,LicenceArea";

    private const string Tx3Header =
        "GridSupplyPoint,HV_Node,HV_Substation,Voltage_HV (kV),LV_Node_1,LV_Substation_1,Voltage_LV_1 (kV)," +
        "LV_Node_2,LV_Substation_2,Voltage_LV_2 (kV),Vector_Group_HV,Vector_Group_LV1,Vector_Group_LV2," +
        "Positive_Sequence_Impedance_R_HV_LV1_Percent,Positive_Sequence_Impedance_X_HV_LV1_Percent," +
        "Positive_Sequence_Impedance_X_LV1_LV2_Percent,Positive_Sequence_Impedance_X_HV_LV2_Percent," +
        "Tap_Range_Min_Percent_HV,Tap_Range_Max_Percent_HV,Transformer_Rating_MVA_Winter_HV,Transformer_Rating_MVA_Summer_HV," +
        "Transformer_Rating_MVA_Winter_LV1,Transformer_Rating_MVA_Winter_LV2,LicenceArea";

    private const string DemandHeader = "Substation,GridSupplyPoint,Maximum_Demand_24_25_MW,Maximum_Demand_24_25_PF,Firm_Capacity_MW";
    private const string GenHeader = "Substation,GridSupplyPoint,Connection_Voltage_kV,InstalledCapacity_MVA,FuelType,Connected_Accepted";

    /// <summary>Write a UKPN RAWData lake from header + row text; a null table is not written at all.</summary>
    private string Lake(string[] circuits, string[] tx2, string[] demand, string[]? tx3 = null, string[]? gen = null)
    {
        string root = Path.Combine(_root, "lake-" + Guid.NewGuid().ToString("N"));
        string raw = Path.Combine(root, "UkPowerNetworks", "RAWData");
        Directory.CreateDirectory(raw);
        void W(string name, string header, string[] rows) =>
            File.WriteAllText(Path.Combine(raw, name), header + "\n" + string.Concat(rows.Select(r => r + "\n")), Encoding.UTF8);
        W("ltds-table-1-circuit-data.csv", CircuitHeader, circuits);
        W("ltds-table-2a-transformer-2w.csv", Tx2Header, tx2);
        W("ltds-table-3a-load-data-observed.csv", DemandHeader, demand);
        if (tx3 is not null) W("ltds-table-2b-transformer-data-3w.csv", Tx3Header, tx3);
        if (gen is not null) W("ltds-table-5-generation.csv", GenHeader, gen);
        return root;
    }

    // ---- 1. transformer impedance base ---------------------------------------------------------------------------

    [Fact]
    public void Transformer_percent_is_on_100_mva_not_on_the_unit_rating()
    {
        // The first row of the real Table 2a: Coldharbour Farm, 15 MVA, X 112.3978 %, R 7.7796 %.
        string root = Lake(
            new[] { $"Amersham,Grid,G1,G2,Grid B,L1,33,1,5,0,500,600,10,{Epn}" },
            new[] { $"Amersham,G2,Grid B,33,P1,Coldharbour 11kV,11.5,Dyn11,7.7796,112.3978,101.158,110,90,15,12,{Epn}" },
            new[] { "Coldharbour 11kV,Amersham,4.2,0.96,14" });
        LtdsDataset ds = UkpnSource.Load(root);
        Assert.False(ds.TransformerPercentOnRating);
        Branch tx = GdaModelBuilder.Build(ds, new GdaBuildOptions()).Model.Branches.Single(b => b.IsTransformer);
        // Exactly the builder's percent-on-100-MVA formula: raw / 100 * (BaseMva / 100).
        Assert.Equal(112.3978 / 100.0 * (100.0 / 100.0), tx.X);
        Assert.Equal(7.7796 / 100.0 * (100.0 / 100.0), tx.R);
        // Before N194 it was read as percent on its 15 MVA rating: 112.3978 / 100 * 100 / 15 = 7.49 pu, a 100 %
        // reactance on its own rating that no real 33/11 kV unit has.
        Assert.NotEqual(112.3978 / 100.0 * (100.0 / 15.0), tx.X);
    }

    // ---- 2. three-winding table ----------------------------------------------------------------------------------

    [Fact]
    public void Three_winding_units_are_read_from_table_2b_and_built_as_a_star()
    {
        // Carnaby St's first unit from the real Table 2b (66 / 11.3 / 21.8 kV).
        string root = Lake(
            new[] { $"Lodge Road,Lodge Rd B 66kV,LODB61,CARN61,Carnaby St 66 kV,L1,66,0.56,1.29,1.36,200,250,1,{Lpn}" },
            Array.Empty<string>(),
            new[] { "Carnaby St 11 kV,Lodge Road,5,0.97,20" },
            tx3: new[]
            {
                "Lodge Road,CARN61,Carnaby St 66 kV,66,CARN52,Carnaby St 11 kV,11.3,CARN80,Carnaby St 22 kV,21.799999," +
                $"D,Z,YN,2.2085,46.911,154.1305,106.4655,82,109,29,22,18,10,{Lpn}",
            });
        LtdsDataset ds = UkpnSource.Load(root);
        LtdsTransformer3 t = Assert.Single(ds.Transformers3);
        Assert.Equal(("CARN61", 66.0, "CARN52", 11.3, "CARN80", 21.799999), (t.Node1, t.Kv1, t.Node2, t.Kv2, t.Node3, t.Kv3));
        Assert.Equal(2.2085, t.R1Ohm);
        Assert.Equal(46.911, t.X1HvLvOhm);        // X HV-LV1
        Assert.Equal(154.1305, t.X1LvTertOhm);    // X LV1-LV2
        Assert.Equal(106.4655, t.X1HvTertOhm);    // X HV-LV2
        Assert.Equal(-18.0, t.MinTap);            // 82 % of nominal, N195 convention
        Assert.Equal(9.0, t.MaxTap);
        Assert.Equal(29.0, t.NominalRatingMva);
        Assert.Equal(22.0, t.EmergencyRatingMva);
        Assert.Equal("DZYN", t.VectorGroup);

        GdaBuildResult r = GdaModelBuilder.Build(ds, new GdaBuildOptions());
        Assert.Equal(1, r.Diagnostics.Transformers3);
        Assert.Equal(1, r.Diagnostics.StarNodes);
        Assert.Equal(3, r.Model.Branches.Count(b => (b.Name ?? "").StartsWith("TX3 ", StringComparison.Ordinal)));
        Assert.Equal(5.0, r.Diagnostics.TotalDemandMw);   // the demand is on the LV1 node, reached through the star
    }

    [Fact]
    public void A_lake_without_table_2b_still_loads_with_no_three_winding_units()
    {
        string root = Lake(
            new[] { $"Amersham,Grid,G1,G2,Sub B,L1,33,1,5,0,500,600,10,{Epn}" },
            Array.Empty<string>(),
            new[] { "Sub B,Amersham,4.2,0.96,14" });
        Assert.Empty(UkpnSource.Load(root).Transformers3);
    }

    // ---- 3. node codes per licence area --------------------------------------------------------------------------

    [Fact]
    public void A_code_naming_different_substations_in_two_areas_is_kept_apart_and_a_shared_boundary_site_is_not()
    {
        string root = Lake(
            new[]
            {
                // BROG31 is Brogborough (EPN) and Bromley (LPN) on the real lake: different places.
                $"Sundon,Brogborough Primary 33kV,BROG31,SUND31,Sundon 33kV,L1,33,1,2,0,500,600,3,{Epn}",
                $"Hurst 132kV,Bromley Grid 33kV,BROG31,HURS31,Hurst 33kV,L2,33,1,2,0,500,600,3,{Lpn}",
                // BARG11 is Barking in both areas (site word and GSP word agree): one boundary node.
                $"Barking 132kV,Barking 132kV GIS,BARG11,RIPP11,Ripple Rd 132kV,L3,132,0.1,0.5,0,900,1000,2,{Epn}",
                $"Barking 132kV GIS,Barking Grid 132kV,BARG11,BARK11,Barking Town 132kV,L4,132,0.1,0.5,0,900,1000,2,{Lpn}",
            },
            Array.Empty<string>(),
            new[] { "Sundon 33kV,Sundon,10,0.95,20", "Hurst 33kV,Hurst 132kV,10,0.95,20" });
        LtdsDataset ds = UkpnSource.Load(root);
        var codes = ds.Nodes.Select(n => n.Code).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("BROG31@EPN", codes);
        Assert.Contains("BROG31@LPN", codes);
        Assert.DoesNotContain("BROG31", codes);
        Assert.Contains("BARG11", codes);                     // the boundary site stays one node
        Assert.DoesNotContain("BARG11@EPN", codes);
        Assert.Contains("SUND31", codes);                     // a code used in one area only is untouched
        Assert.Equal("Brogborough Primary 33kV", ds.Nodes.Single(n => n.Code == "BROG31@EPN").Name);
        Assert.Equal("Bromley Grid 33kV", ds.Nodes.Single(n => n.Code == "BROG31@LPN").Name);
        Assert.Contains(ds.SourceNotes, n => n.StartsWith("UKPN: 1 node code(s) name different substations", StringComparison.Ordinal));

        // Built, the two BROG31 substations are no longer one bus: two separate islands.
        GdaBuildResult r = GdaModelBuilder.Build(ds, new GdaBuildOptions());
        Assert.Equal(3, r.Diagnostics.Islands);   // Brogborough-Sundon, Bromley-Hurst, Barking (three 132 kV nodes)
    }

    // ---- 4 and 5. demand sharing and aggregate rows --------------------------------------------------------------

    /// <summary>
    /// A grid site Bravo (132/33 kV from GSP Alpha) whose 33 kV board feeds primary Papa through a 33 kV circuit.
    /// Papa has two transformers (10 and 30 MVA) onto two unconnected 11 kV sections. Demand rows: the grid board
    /// (40 MW, the sum of what is behind it) and Papa (20 MW). Generation: 10 MVA of solar at the grid board.
    /// </summary>
    private string GridAndPrimaryLake() => Lake(
        new[]
        {
            $"Alpha,Alpha 132kV,A11,A12,Alpha 132kV,L0,132,0,0.1,0,900,1000,0.1,{Epn}",
            $"Alpha,Bravo Grid 33kV,B31,P31,Papa 33kV,L1,33,1,2,0,500,600,3,{Epn}",
        },
        new[]
        {
            $"Alpha,A11,Alpha 132kV,132,B31,Bravo Grid 33kV,33,YNd1,0.5,25,20,110,90,60,50,{Epn}",
            $"Alpha,P31,Papa 33kV,33,P51,Papa 11kV,11,Dyn11,1,100,90,110,90,10,8,{Epn}",
            $"Alpha,P31,Papa 33kV,33,P52,Papa 11kV,11,Dyn11,1,100,90,110,90,30,25,{Epn}",
        },
        new[] { "Bravo Grid 33kV,Alpha,40,0.95,60", "Papa 11kV,Alpha,20,0.96,30" },
        gen: new[] { "Bravo Grid 33kV,Alpha,33,10,Solar,Connected" });

    [Fact]
    public void A_substations_demand_is_shared_over_its_transformer_sections_by_rating()
    {
        LtdsDataset ds = UkpnSource.Load(GridAndPrimaryLake());
        var papa = ds.Demand.Where(d => d.PrimarySubstation == "Papa 11kV").ToList();
        Assert.Equal(2, papa.Count);
        Assert.Equal(new[] { "P51", "P52" }, papa.Select(d => d.Node).ToArray());   // node-code order
        // Exactly MW x weight / total: 10 / 40 and 30 / 40 of 20 MW.
        Assert.Equal(20.0 * (10.0 / 40.0), papa[0].MaxDemandMw);
        Assert.Equal(20.0 * (30.0 / 40.0), papa[1].MaxDemandMw);
        Assert.Equal(30.0 * (10.0 / 40.0), papa[0].FirmCapacityMva);
        Assert.All(papa, d => Assert.Equal(0.96, d.PowerFactor));
        Assert.All(papa, d => Assert.False(d.JoinOnly));
    }

    [Fact]
    public void A_single_feeder_on_the_old_node_leaves_the_row_untouched()
    {
        string root = Lake(
            new[] { $"Amersham,Grid,G1,G2,Grid B,L1,33,1,5,0,500,600,10,{Epn}" },
            new[] { $"Amersham,G2,Grid B,33,P1,Prim 11kV,11,Dyn11,1,100,90,110,90,15,12,{Epn}" },
            new[] { "Prim 11kV,Amersham,4.2,0.96,14" });
        LtdsDemand d = Assert.Single(UkpnSource.Load(root).Demand);
        Assert.Equal(("P1", 11.0, 4.2, 14.0, false), (d.Node, d.Kv, d.MaxDemandMw, d.FirmCapacityMva, d.JoinOnly));
    }

    [Fact]
    public void A_row_on_a_node_no_transformer_feeds_moves_to_the_one_feeder_node()
    {
        // Townmead-style: the substation's lowest-voltage node is a cable end (P1X, 11 kV, from the circuit table,
        // seen first), while its transformer lands on P51. The row moves to P51 with its demand unchanged.
        string root = Lake(
            new[]
            {
                $"Amersham,Prim 11kV,P1X,P51,Prim 11kV,L9,11,4,7,0,300,350,0.5,{Epn}",
                $"Amersham,Grid,G1,G2,Grid B,L1,33,1,5,0,500,600,10,{Epn}",
            },
            new[] { $"Amersham,G2,Grid B,33,P51,Prim 11kV,11,Dyn11,1,100,90,110,90,15,12,{Epn}" },
            new[] { "Prim 11kV,Amersham,4.2,0.96,14" });
        LtdsDemand d = Assert.Single(UkpnSource.Load(root).Demand);
        Assert.Equal("P51", d.Node);
        Assert.Equal(4.2, d.MaxDemandMw);
    }

    [Fact]
    public void A_grid_boards_row_carries_no_load_but_generation_at_the_board_still_joins_it()
    {
        LtdsDataset ds = UkpnSource.Load(GridAndPrimaryLake());
        LtdsDemand grid = ds.Demand.Single(d => d.PrimarySubstation == "Bravo Grid 33kV");
        Assert.True(grid.JoinOnly);
        Assert.Equal("B31", grid.Node);
        Assert.Contains(ds.SourceNotes, n => n.StartsWith("UKPN: 1 demand row(s), 40.0 MW, carry no load", StringComparison.Ordinal));

        GdaBuildResult r = GdaModelBuilder.Build(ds, new GdaBuildOptions());
        // Only Papa's 20 MW is load (two shared points); the grid board's 40 MW is not counted again.
        Assert.Equal(20.0 * (10.0 / 40.0) + 20.0 * (30.0 / 40.0), r.Diagnostics.TotalDemandMw);
        Assert.Equal(2, r.Diagnostics.DemandPoints);
        // The solar at the grid board joins the board's bus.
        Generator solar = Assert.Single(r.Model.Generators, g => (g.Name ?? "").StartsWith("Bravo Grid 33kV", StringComparison.Ordinal));
        Assert.Equal("Bravo Grid 33kV", r.Model.Buses[solar.Bus].Name);
        Assert.Equal(0, r.Diagnostics.UnmatchedGen);
    }

    [Fact]
    public void Builder_join_only_row_adds_no_load_and_registers_the_join()
    {
        LtdsDataset ds = SmallDataset(new[]
        {
            new LtdsDemand("Alpha", "", "Board", "B", 33, MaxDemandMw: 50, PowerFactor: 0.95, FirmCapacityMva: 60, JoinOnly: true),
            new LtdsDemand("Alpha", "", "Prim", "C", 33, MaxDemandMw: 7, PowerFactor: 0.95, FirmCapacityMva: 9),
        }, new[] { new LtdsGeneration("Alpha", "", "Board", 33, 4, "Solar", "Connected") }, flag: false);
        GdaBuildResult r = GdaModelBuilder.Build(ds, new GdaBuildOptions());
        Assert.Equal(7.0, r.Diagnostics.TotalDemandMw);
        Assert.Equal(1, r.Diagnostics.DemandPoints);
        Generator g = Assert.Single(r.Model.Generators);
        Assert.Equal("Bravo", r.Model.Buses[g.Bus].Name);   // node B's bus
    }

    // ---- 6. infeeds per GSP group per island, at the GSP site ----------------------------------------------------

    /// <summary>
    /// One island, GSP group "Alpha" on every element: node A "Alpha GIS 132kV" (the GSP site, one branch), node M
    /// "Mike 132kV" (three branches, the busiest), nodes B and C at 33 kV. A second site section A2 "Alpha GIS
    /// 132kV" hangs off C through its own transformer.
    /// </summary>
    private static LtdsDataset SmallDataset(LtdsDemand[] demand, LtdsGeneration[] gen, bool flag) => new(
        "Test/N194", ImpedanceConvention.PercentOn100Mva,
        Nodes: new[]
        {
            new LtdsNode("UKPN", "A", "Alpha GIS 132kV", "", "Alpha", "Energised"),
            new LtdsNode("UKPN", "M", "Mike 132kV", "", "Alpha", "Energised"),
            new LtdsNode("UKPN", "B", "Bravo", "", "Alpha", "Energised"),
            new LtdsNode("UKPN", "C", "Charlie", "", "Alpha", "Energised"),
            new LtdsNode("UKPN", "A2", "Alpha GIS 132kV", "", "Alpha", "Energised"),
        },
        Circuits: new[]
        {
            new LtdsCircuit("UKPN", "Alpha", "", "A", "M", "Energised", 132, "C1", 0.1, 1, 0, 0, 0, 500, 500, 1, "Circuit"),
            new LtdsCircuit("UKPN", "Alpha", "", "B", "C", "Energised", 33, "C2", 1, 2, 0, 0, 0, 50, 50, 1, "Circuit"),
        },
        Transformers2: new[]
        {
            new LtdsTransformer2("UKPN", "Alpha", "M", 132, "B", 33, "Energised", "YNd1", 0.5, 20, 18, -10, 10, 90, 100),
            new LtdsTransformer2("UKPN", "Alpha", "M", 132, "C", 33, "Energised", "YNd1", 0.5, 20, 18, -10, 10, 90, 100),
            new LtdsTransformer2("UKPN", "Alpha", "A2", 132, "C", 33, "Energised", "YNd1", 0.5, 20, 18, -10, 10, 90, 100),
        },
        Transformers3: Array.Empty<LtdsTransformer3>(),
        Demand: demand,
        Generation: gen,
        TransformerPercentOnRating: false,
        InfeedPerGspGroupPerIsland: flag);

    private static string[] SlackNames(GdaBuildResult r) =>
        r.Model.Buses.Where(b => b.Type == BusType.Slack).Select(b => b.Name ?? "").OrderBy(s => s, StringComparer.Ordinal).ToArray();

    [Fact]
    public void Without_the_flag_one_infeed_goes_to_the_busiest_node_of_the_group_as_before()
    {
        GdaBuildResult r = GdaModelBuilder.Build(SmallDataset(
            new[] { new LtdsDemand("Alpha", "", "Charlie", "C", 33, 10, 0.95, 20) }, Array.Empty<LtdsGeneration>(), flag: false),
            new GdaBuildOptions());
        Assert.Equal(new[] { "Mike 132kV" }, SlackNames(r));
    }

    [Fact]
    public void With_the_flag_every_section_of_the_gsp_site_is_an_infeed()
    {
        GdaBuildResult r = GdaModelBuilder.Build(SmallDataset(
            new[] { new LtdsDemand("Alpha", "", "Charlie", "C", 33, 10, 0.95, 20) }, Array.Empty<LtdsGeneration>(), flag: true),
            new GdaBuildOptions());
        // Both Alpha GIS 132kV sections (A and A2), not the busier Mike 132kV.
        Assert.Equal(new[] { "Alpha GIS 132kV", "Alpha GIS 132kV" }, SlackNames(r));
    }

    [Fact]
    public void With_the_flag_a_gsp_site_reached_first_through_another_groups_circuit_still_gets_its_infeed()
    {
        // Chessington-style: node K "Kilo 132kV" is the site of GSP group "Kilo", but the only element touching it
        // is a circuit published under group "Alpha", so its bus carries the tag "Alpha". Group "Kilo" exists
        // elsewhere (node K2's island). Under the flag, K is a Kilo site and gets its own infeed.
        var ds = new LtdsDataset(
            "Test/N194b", ImpedanceConvention.PercentOn100Mva,
            Nodes: new[]
            {
                new LtdsNode("UKPN", "A", "Alpha 132kV", "", "Alpha", "Energised"),
                new LtdsNode("UKPN", "K", "Kilo 132kV", "", "Kilo", "Energised"),
                new LtdsNode("UKPN", "K2", "Kilo 132kV", "", "Kilo", "Energised"),
                new LtdsNode("UKPN", "L", "Lima 132kV", "", "Kilo", "Energised"),
            },
            Circuits: new[]
            {
                new LtdsCircuit("UKPN", "Alpha", "", "A", "K", "Energised", 132, "C1", 0.1, 1, 0, 0, 0, 500, 500, 1, "Circuit"),
                new LtdsCircuit("UKPN", "Kilo", "", "K2", "L", "Energised", 132, "C2", 0.1, 1, 0, 0, 0, 500, 500, 1, "Circuit"),
            },
            Transformers2: Array.Empty<LtdsTransformer2>(),
            Transformers3: Array.Empty<LtdsTransformer3>(),
            Demand: new[] { new LtdsDemand("Alpha", "", "Alpha", "A", 132, 5, 0.95, 10) },
            Generation: Array.Empty<LtdsGeneration>(),
            TransformerPercentOnRating: false,
            InfeedPerGspGroupPerIsland: true);
        GdaBuildResult r = GdaModelBuilder.Build(ds, new GdaBuildOptions());
        Assert.Equal(new[] { "Alpha 132kV", "Kilo 132kV", "Kilo 132kV" }, SlackNames(r));
    }

    [Fact]
    public void The_full_ukpn_dataset_flags_are_set_by_the_reader()
    {
        LtdsDataset ds = UkpnSource.Load(GridAndPrimaryLake());
        Assert.True(ds.InfeedPerGspGroupPerIsland);
        Assert.False(ds.TransformerPercentOnRating);
    }
}
