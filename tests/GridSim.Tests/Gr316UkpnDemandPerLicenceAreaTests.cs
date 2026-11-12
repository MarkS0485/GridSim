using System.Text;
using GridSim.Gda;
using GridSim.Gda.Ltds;
using GridSim.Gda.Ukpn;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-316 (bug #271): UKPN LTDS Table 3a demand was keyed by substation name alone and the row's LicenceArea was never
/// read, so one name used in two licence areas (two different substations) collapsed to the larger row and the other
/// area's load was silently dropped from the replica. Rows are now keyed by (licence area, name). Pinned: two areas
/// sharing a name both keep their load, each on its own area's node, with a note; a demand row with a substation name
/// but a blank licence area is refused, naming it; a name used in one area only is keyed and placed exactly as before.
/// </summary>
public sealed class Gr316UkpnDemandPerLicenceAreaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gridsim-gr316-{Guid.NewGuid():N}");

    public Gr316UkpnDemandPerLicenceAreaTests() => Directory.CreateDirectory(_root);

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

    private const string DemandHeader =
        "Substation,GridSupplyPoint,Maximum_Demand_24_25_MW,Maximum_Demand_24_25_PF,Firm_Capacity_MW,LicenceArea";

    // Two separate areas, each a 33 kV circuit and one 33/11 kV transformer onto a primary named "Coldharbour 11kV"
    // (node P1 in EPN, Q1 in LPN), plus an EPN-only primary "Amber 11kV" on node P2.
    private string Lake(string[] demand)
    {
        string root = Path.Combine(_root, "lake-" + Guid.NewGuid().ToString("N"));
        string raw = Path.Combine(root, "UkPowerNetworks", "RAWData");
        Directory.CreateDirectory(raw);
        void W(string name, string header, string[] rows) =>
            File.WriteAllText(Path.Combine(raw, name), header + "\n" + string.Concat(rows.Select(r => r + "\n")), Encoding.UTF8);
        W("ltds-table-1-circuit-data.csv", CircuitHeader, new[]
        {
            $"Amersham,Grid,G1,G2,Grid B,L1,33,1,5,0,500,600,10,{Epn}",
            $"Bankside,Grid L,H1,H2,Grid M,L2,33,1,5,0,500,600,10,{Lpn}",
        });
        W("ltds-table-2a-transformer-2w.csv", Tx2Header, new[]
        {
            $"Amersham,G2,Grid B,33,P1,Coldharbour 11kV,11.5,Dyn11,7.7796,112.3978,101.158,110,90,15,12,{Epn}",
            $"Amersham,G2,Grid B,33,P2,Amber 11kV,11.5,Dyn11,7.7796,112.3978,101.158,110,90,15,12,{Epn}",
            $"Bankside,H2,Grid M,33,Q1,Coldharbour 11kV,11,Dyn11,7.7796,112.3978,101.158,110,90,15,12,{Lpn}",
        });
        W("ltds-table-3a-load-data-observed.csv", DemandHeader, demand);
        return root;
    }

    [Fact]
    public void Gr316_two_licence_areas_sharing_a_substation_name_both_keep_their_load()
    {
        LtdsDataset ds = UkpnSource.Load(Lake(new[]
        {
            $"Coldharbour 11kV,Amersham,4.2,0.96,14,{Epn}",
            $"Coldharbour 11kV,Bankside,6.5,0.95,20,{Lpn}",
            $"Amber 11kV,Amersham,3.0,0.95,10,{Epn}",
        }));

        List<LtdsDemand> cold = ds.Demand.Where(d => d.PrimarySubstation == "Coldharbour 11kV" && !d.JoinOnly).ToList();
        Assert.Equal(2, cold.Count);   // before GR-316: one row, 6.5 MW, the EPN 4.2 MW dropped
        LtdsDemand epn = Assert.Single(cold, d => d.Node == "P1");
        LtdsDemand lpn = Assert.Single(cold, d => d.Node == "Q1");
        Assert.Equal(4.2, epn.MaxDemandMw);
        Assert.Equal(11.5, epn.Kv);
        Assert.Equal(6.5, lpn.MaxDemandMw);
        Assert.Equal(11.0, lpn.Kv);

        // The whole table is in the replica: 4.2 + 6.5 + 3.0 MW.
        Assert.Equal(4.2 + 6.5 + 3.0, ds.Demand.Where(d => !d.JoinOnly).Sum(d => d.MaxDemandMw));
        string note = Assert.Single(ds.SourceNotes, n => n.Contains("GR-316", StringComparison.Ordinal));
        Assert.Contains("1 demand substation name(s) are published in more than one licence area", note, StringComparison.Ordinal);
        Assert.Contains("'Coldharbour 11kV'", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr316_a_name_used_in_one_area_only_is_placed_as_before()
    {
        LtdsDataset ds = UkpnSource.Load(Lake(new[]
        {
            $"Amber 11kV,Amersham,3.0,0.95,10,{Epn}",
            $"Amber 11kV,Amersham,3.4,0.95,10,{Epn}",   // a second season: the worst one counts
        }));
        LtdsDemand d = Assert.Single(ds.Demand);
        Assert.Equal(("Amber 11kV", "P2", 11.5, 3.4, false), (d.PrimarySubstation, d.Node, d.Kv, d.MaxDemandMw, d.JoinOnly));
        Assert.DoesNotContain(ds.SourceNotes, n => n.Contains("GR-316", StringComparison.Ordinal));
    }

    [Fact]
    public void Gr316_a_demand_row_with_no_licence_area_is_refused_by_name()
    {
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => UkpnSource.Load(Lake(new[]
        {
            $"Amber 11kV,Amersham,3.0,0.95,10,{Epn}",
            "Coldharbour 11kV,Amersham,4.2,0.96,14,",
        })));
        Assert.Contains("demand row 2 (substation 'Coldharbour 11kV') has no LicenceArea", ex.Message, StringComparison.Ordinal);
    }
}
