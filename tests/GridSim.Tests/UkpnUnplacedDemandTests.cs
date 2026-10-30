using System.Text;
using GridSim.Gda;
using GridSim.Gda.Ukpn;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// A UKPN demand row whose substation names no node in the circuit or transformer tables cannot be placed. It used to
/// be skipped with no count, so its load vanished while the build reported full demand (on the real lake, 11
/// substations and 146.3 MW). Such rows are now counted, summed at their worst season and reported in the source notes.
/// </summary>
public sealed class UkpnUnplacedDemandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gridsim-ukpn-unplaced-{Guid.NewGuid():N}");

    public UkpnUnplacedDemandTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private const string Epn = "Eastern Power Networks (EPN)";

    private const string CircuitHeader =
        "GridSupplyPoint,FromSubstation,FromNode,ToNode,ToSubstation,LineName,OperatingVoltage (kV)," +
        "Positive_Sequence_Impedance_R_Percent,Positive_Sequence_Impedance_X_Percent,Susceptance_B_ Percent," +
        "Rating_Amps_Summer,Rating_Amps_Winter,Circuit_Length (km),LicenceArea";

    private const string Tx2Header =
        "GridSupplyPoint,HV_Node,HV_Substation,Voltage_HV (kV),LV_Node,LV_Substation,Voltage_LV (kV),Vector_Group," +
        "Positive_Sequence_Impedance_R_Percent,Positive_Sequence_Impedance_X_Percent,Zero_Sequence_Impedance_X_Percent," +
        "Tap_Range_Max_Percent,Tap_Range_Min_Percent,Transformer_Rating_MVA_Winter,Transformer_Rating_MVA_Summer,LicenceArea";

    private const string DemandHeader = "Substation,GridSupplyPoint,Maximum_Demand_24_25_MW,Maximum_Demand_24_25_PF,Firm_Capacity_MW";

    private string Lake(string[] demand)
    {
        string root = Path.Combine(_root, "lake-" + Guid.NewGuid().ToString("N"));
        string raw = Path.Combine(root, "UkPowerNetworks", "RAWData");
        Directory.CreateDirectory(raw);
        void W(string name, string header, string[] rows) =>
            File.WriteAllText(Path.Combine(raw, name), header + "\n" + string.Concat(rows.Select(r => r + "\n")), Encoding.UTF8);
        W("ltds-table-1-circuit-data.csv", CircuitHeader, new[] { $"Amersham,Grid,G1,G2,Grid B,L1,33,1,5,0,500,600,10,{Epn}" });
        W("ltds-table-2a-transformer-2w.csv", Tx2Header,
            new[] { $"Amersham,G2,Grid B,33,P1,Coldharbour 11kV,11.5,Dyn11,7.7796,112.3978,101.158,110,90,15,12,{Epn}" });
        W("ltds-table-3a-load-data-observed.csv", DemandHeader, demand);
        return root;
    }

    [Fact]
    public void Demand_on_a_substation_no_table_names_is_reported_with_its_megawatts()
    {
        // Two seasons for the unplaced substation: the worst (19.8 MW) is the one that counts, as for a placed row.
        LtdsDataset ds = UkpnSource.Load(Lake(new[]
        {
            "Coldharbour 11kV,Amersham,4.2,0.96,14",
            "St Johns Wood 22kV,Amersham,12.5,0.95,20",
            "St Johns Wood 22kV,Amersham,19.8,0.95,20",
        }));
        string note = Assert.Single(ds.SourceNotes, n => n.Contains("name no node", StringComparison.Ordinal));
        Assert.Contains("1 demand substation(s), 19.8 MW", note, StringComparison.Ordinal);
        Assert.Contains("'St Johns Wood 22kV' 19.8 MW", note, StringComparison.Ordinal);
        Assert.StartsWith("WARNING", note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lake_whose_demand_all_places_has_no_unplaced_note()
    {
        LtdsDataset ds = UkpnSource.Load(Lake(new[] { "Coldharbour 11kV,Amersham,4.2,0.96,14" }));
        Assert.DoesNotContain(ds.SourceNotes, n => n.Contains("name no node", StringComparison.Ordinal));
    }

    [Fact]
    public void A_demand_row_with_no_substation_name_is_reported()
    {
        LtdsDataset ds = UkpnSource.Load(Lake(new[] { "Coldharbour 11kV,Amersham,4.2,0.96,14", ",Amersham,3.5,0.95,10" }));
        Assert.Contains(ds.SourceNotes, n => n.Contains("1 demand row(s), 3.5 MW, have no substation name", StringComparison.Ordinal));
    }
}
