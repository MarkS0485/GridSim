using System;
using System.IO;
using System.Linq;
using GridSim.Gda;
using GridSim.Gda.Ltds;
using GridSim.Gda.Npg;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N202 (October 2026, Ruling R1 follow-up, Mark delegated 2026-10-04): the NPg full-lake build stopped converging once
/// the N184 join placed 86 demand rows. Two changes, both NPg only:
/// <list type="number">
/// <item>NPg takes UKPN's infeed rule (<see cref="LtdsDataset.InfeedPerGspGroupPerIsland"/>, N194), as the ruling says.
/// On its own that was not enough (full lake: still not converged, mismatch 4.63E-001 -> 1.84E-001).</item>
/// <item>The cause of the unsolvable island: NPg's transformer table names a winding "Harrogate GT2B" where the circuit
/// table names its busbars "Harrogate GT2B 132kV" and "Harrogate GT2B 33kV", so keyed by (name, voltage) 308 windings on
/// the full lake named no busbar and their transformers were built floating (Harrogate's and Wormald Green's 132/33 kV
/// grid transformers among them). A winding whose key names no node now joins its site's circuit busbar by site name,
/// the N184 rule (nearest voltage within 5 %, unique).</item>
/// </list>
/// The NGED and UKPN replicas are byte-identical (measured on the full lake; NGED never sets the flag, and UKPN does not
/// read through NpgSource).
/// </summary>
public sealed class N202NpgInfeedAndWindingJoinTests
{
    /// <summary>A minimal NPg RAWData lake: a grid transformer whose table name omits the voltage its busbars carry, a
    /// 33/11 kV unit spelt "Brig 33/11kV T2" against a busbar "Brig T2", and a winding whose site has two busbars at the
    /// same voltage (a tie, so it must stay on its own key).</summary>
    private static string Fixture()
    {
        string root = Path.Combine(Path.GetTempPath(), "gridsim-n202-npg-" + Guid.NewGuid().ToString("N"));
        string raw = Path.Combine(root, "NorthernPowerGrid", "RAWData");
        void Write(string dataset, string csv)
        {
            Directory.CreateDirectory(Path.Combine(raw, dataset));
            File.WriteAllText(Path.Combine(raw, dataset, dataset + ".csv"), csv);
        }
        Write("LTDS_Appendix_3_Circuit_Data",
            "License,S/S Group,S/S or Busbar Name Node 1,S/S or Busbar Name Node 2,Operating Voltage (kV),Positive Sequence Impedance R (% on 100 MVA Base),Positive Sequence Impedance X (% on 100 MVA Base),Susceptance B (% on 100 MVA Base),Rating (MVA),Circuit Length (km)\n" +
            "NPG,Test,GRID 132kV,SITE GT1 132kV,132,1,5,0.1,100,10\n" +
            "NPG,Test,SITE GT1 33kV,LOAD 33kV,33,1,5,0.1,40,5\n" +
            "NPG,Test,LOAD 33kV,Brig T1,33,1,5,0.1,40,5\n" +
            "NPG,Test,LOAD 33kV,Brig T2,33,1,5,0.1,40,5\n" +
            "NPG,Test,LOAD 33kV,AMB T1 33kV,33,1,5,0.1,40,5\n" +
            "NPG,Test,LOAD 33kV,AMB T1 33 kV,33,1,5,0.1,40,5\n");
        Write("LTDS_Appendix_4_Transformer_Data",
            "License,S/S Group,S/S or Busbar Name Node,Primary Voltage (kV),Secondary Voltage (kV),Vector Group,Positive Sequence Impedance R (% on 100 MVA Base),Positive Sequence Impedance X (% on 100 MVA Base),Zero Sequence Reactance X (% on 100 MVA Base),Minimum Tap %,Maximum Tap %,Transformer Rating (MVA)\n" +
            "NPG,Test,SITE GT1,132,33,Yd11,0.5,20,18,-10,10,60\n" +
            "NPG,Test,Brig 33/11kV T2,33,11.5,Dyn11,1,40,36,-10,10,20\n" +
            "NPG,Test,Amb T1,33,11.5,Dyn11,1,40,36,-10,10,20\n");
        Write("LTDS_Appendix_5_Load_Information",
            "S/S Group,S/S or Busbar Name,Voltage Level (kV),2024/25 Net (Observed) Maximum Load (MVA),2024/25 Net (Observed) Maximum Load (PF),Firm Capacity (MVA)\n" +
            "Test,LOAD 33kV,33,20,0.95,30\n");
        return root;
    }

    [Fact]
    public void A_winding_that_names_no_busbar_joins_its_sites_busbar_and_only_then()
    {
        string root = Fixture();
        try
        {
            LtdsDataset ds = NpgSource.Load(root, preferParquet: false);
            LtdsTransformer2 gt = ds.Transformers2.Single(t => t.Node1.StartsWith("SITE", StringComparison.Ordinal));
            // Before N202: "SITE GT1|132" and "SITE GT1|33", nodes on nothing but this transformer.
            Assert.Equal("SITE GT1 132KV|132", gt.Node1);
            Assert.Equal("SITE GT1 33KV|33", gt.Node2);
            // " T2" is part of the unit's name: it joins Brig T2, never Brig T1. Its 11.5 kV winding has no busbar.
            LtdsTransformer2 brig = ds.Transformers2.Single(t => t.Node1.StartsWith("BRIG", StringComparison.Ordinal));
            Assert.Equal("BRIG T2|33", brig.Node1);
            Assert.Equal("BRIG 33/11KV T2|11.5", brig.Node2);
            // Two busbars of site AMB T1 at 33 kV ("AMB T1 33KV" and "AMB T1 33 KV"): a tie, so the winding keeps its key.
            LtdsTransformer2 amb = ds.Transformers2.Single(t => t.Node1.StartsWith("AMB", StringComparison.Ordinal));
            Assert.Equal("AMB T1|33", amb.Node1);
            // The reader says what it did: 3 windings joined (GT1 both sides, Brig's 33 kV side).
            Assert.Single(ds.SourceNotes);
            Assert.StartsWith("Transformer windings: 3 named no busbar by (name, voltage)", ds.SourceNotes[0], StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void The_grid_transformer_now_connects_the_33_kV_load_to_the_132_kV_grid()
    {
        string root = Fixture();
        try
        {
            LtdsDataset ds = NpgSource.Load(root, preferParquet: false);
            GdaBuildResult r = GdaModelBuilder.Build(ds);
            // GRID, SITE GT1 132kV, SITE GT1 33kV, LOAD, Brig T1, Brig T2, Brig's 11.5 kV node, AMB T1 33KV, AMB T1 33 KV,
            // and the AMB T1 transformer's own two nodes: the AMB unit floats (its tie), everything else is one island.
            int loadBus = r.Model.Buses.Single(b => b.Name == "LOAD 33kV").Id;
            int gridBus = r.Model.Buses.Single(b => b.Name == "GRID 132kV").Id;
            Assert.Equal(Island(r, loadBus), Island(r, gridBus));
            Assert.Equal(2, r.Diagnostics.Islands);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static int Island(GdaBuildResult r, int bus)
    {
        // Union-find over the built branches: the representative of the bus's connected component.
        var parent = r.Model.Buses.ToDictionary(b => b.Id, b => b.Id);
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        foreach (var br in r.Model.Branches) { int a = Find(br.From), b = Find(br.To); if (a != b) parent[a] = b; }
        return Find(bus);
    }

    [Fact]
    public void Npg_and_ukpn_take_the_per_island_infeed_rule_and_nged_does_not()
    {
        string root = Fixture();
        try
        {
            Assert.True(NpgSource.Load(root, preferParquet: false).InfeedPerGspGroupPerIsland);
        }
        finally { Directory.Delete(root, recursive: true); }
        // NGED's loader builds its dataset with the default (false); source pin, since NGED needs the lake.
        string builder = File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Gda", "GdaModelBuilder.cs"));
        int nged = builder.IndexOf("public static LtdsDataset LoadNged(", StringComparison.Ordinal);
        int end = builder.IndexOf("};", nged, StringComparison.Ordinal);
        Assert.DoesNotContain("InfeedPerGspGroupPerIsland", builder[nged..end], StringComparison.Ordinal);
        string ukpn = File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Gda", "Ukpn", "UkpnSource.cs"));
        Assert.Contains("InfeedPerGspGroupPerIsland: true", ukpn, StringComparison.Ordinal);
    }
}
