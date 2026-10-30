using GridSim.Gda.Ukpn;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N067 (October 2026): on the full GDA lake the UKPN RAWData CSVs are header-only stubs (the rows were converted to
/// Parquet), so `gda --dno ukpn` refused with "no positive demand on any known substation", which pointed at the
/// demand column. The refusal is unchanged (same exception type, same opening text); it now adds the data-row count
/// per table and, when a table had none, says the rows may live in the Parquet copy that the reader does not read.
/// </summary>
public sealed class N067UkpnHeaderOnlyStubTests
{
    [Fact]
    public void Header_only_tables_are_named_in_the_refusal()
    {
        string root = Path.Combine(Path.GetTempPath(), "gridsim-n067-" + Guid.NewGuid().ToString("N"));
        string raw = Path.Combine(root, "UkPowerNetworks", "RAWData");
        try
        {
            Directory.CreateDirectory(raw);
            Directory.CreateDirectory(Path.Combine(root, "UkPowerNetworks", "Parquet"));
            File.WriteAllText(Path.Combine(raw, "ukpn-ltds-table-1-circuit-data.csv"),
                "GridSupplyPoint,FromSubstation,FromNode,ToSubstation,ToNode,OperatingVoltage (kV)\n");
            File.WriteAllText(Path.Combine(raw, "ukpn-ltds-table-2a-transformer-2w.csv"),
                "GridSupplyPoint,HV_Substation,HV_Node,LV_Substation,LV_Node\n");
            File.WriteAllText(Path.Combine(raw, "ukpn-ltds-table-3a-load-data-observed.csv"),
                "GridSupplyPoint,Substation,Season,Maximum_Demand_24_25_MW,Maximum_Demand_24_25_PF\n");

            var ex = Assert.Throws<InvalidDataException>(() => UkpnSource.Load(root));
            Assert.StartsWith("UKPN ukpn-ltds-table-3a-load-data-observed.csv: column Maximum_Demand_24_25_MW gave no positive demand on any known substation",
                ex.Message, StringComparison.Ordinal);
            Assert.Contains("(data rows read: circuits 0, transformers 0, demand 0)", ex.Message, StringComparison.Ordinal);
            Assert.Contains("had no data rows (header only)", ex.Message, StringComparison.Ordinal);
            Assert.Contains("which the UKPN reader does not read", ex.Message, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }
}
