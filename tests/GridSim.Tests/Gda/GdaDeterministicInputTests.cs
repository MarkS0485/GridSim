using GridSim.Gda;
using GridSim.Gda.Csv;
using GridSim.Gda.Ukpn;
using Parquet;
using Parquet.Schema;
using Xunit;

namespace GridSim.Tests.Gda;

/// <summary>
/// R297: a directory dataset never picks an arbitrary csv (filesystem enumeration order), and Parquet parts
/// with differing columns stay row-aligned. R300: the UKPN demand column is discovered by vintage, and a table
/// with no demand column (or no demand at all) fails loud instead of building a zero-load replica.
/// </summary>
public class GdaDeterministicInputTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gridsim-gda-{Guid.NewGuid():N}");

    public GdaDeterministicInputTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string Dir(string name)
    {
        string d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    // ---- R297: csv choice -------------------------------------------------------------------------

    [Fact]
    public void Two_undated_csvs_in_a_dataset_folder_are_ambiguous_and_fail_loud()
    {
        string d = Dir("undated");
        File.WriteAllText(Path.Combine(d, "circuits-a.csv"), "x\n1\n");
        File.WriteAllText(Path.Combine(d, "circuits-b.csv"), "x\n2\n");
        var ex = Assert.Throws<InvalidDataException>(() => DataSourceTable.Open(d));
        Assert.Contains("circuits-a.csv", ex.Message);
        Assert.Contains("circuits-b.csv", ex.Message);
    }

    [Fact]
    public void The_csv_with_the_strictly_newest_date_in_its_name_is_chosen()
    {
        string d = Dir("dated");
        File.WriteAllText(Path.Combine(d, "ltds-1-January-2024.csv"), "x\n2024\n");
        File.WriteAllText(Path.Combine(d, "ltds-1-January-2025.csv"), "x\n2025\n");
        using DataSourceTable t = DataSourceTable.Open(d);
        Assert.Equal(2025.0, t.Rows().Single().Double("x"));
    }

    [Fact]
    public void A_single_csv_is_used_as_before()
    {
        string d = Dir("single");
        File.WriteAllText(Path.Combine(d, "anything.csv"), "x\n7\n");
        using DataSourceTable t = DataSourceTable.Open(d);
        Assert.Equal(7.0, t.Rows().Single().Double("x"));
    }

    // ---- R297: parquet parts -------------------------------------------------------------------------

    private static async Task WritePart(string path, string[] names, double[] b)
    {
        var name = new DataField<string>("Name");
        if (b.Length == 0)
        {
            var schemaA = new ParquetSchema(name);
            using Stream s = File.Create(path);
            await using ParquetWriter w = await ParquetWriter.CreateAsync(schemaA, s);
            using ParquetRowGroupWriter rg = w.CreateRowGroup();
            await rg.WriteAsync(name, names);
            return;
        }
        var bField = new DataField<double>("B");
        var schema = new ParquetSchema(name, bField);
        using (Stream s = File.Create(path))
        {
            await using ParquetWriter w = await ParquetWriter.CreateAsync(schema, s);
            using ParquetRowGroupWriter rg = w.CreateRowGroup();
            await rg.WriteAsync(name, names);
            await rg.WriteAsync(bField, new ReadOnlyMemory<double>(b));
        }
    }

    [Fact]
    public async Task A_column_missing_from_a_middle_part_does_not_shift_later_rows()
    {
        string d = Dir("parts");
        await WritePart(Path.Combine(d, "part-0.parquet"), new[] { "r0" }, new double[] { 10.0 });
        await WritePart(Path.Combine(d, "part-1.parquet"), new[] { "r1" }, Array.Empty<double>());   // no B column
        await WritePart(Path.Combine(d, "part-2.parquet"), new[] { "r2" }, new double[] { 30.0 });

        using DataSourceTable t = DataSourceTable.Open(d);
        var rows = t.Rows().ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal("r2", rows[2]["Name"]);
        Assert.Equal(10.0, rows[0].Double("B"));
        Assert.False(rows[1].Has("B"));           // the middle part has no B: null, not the next part's value
        Assert.Equal(30.0, rows[2].Double("B"));   // before R297 this 30 slid up onto row 1
    }

    // ---- R297 + R300: UKPN ---------------------------------------------------------------------------

    private string UkpnRoot(string demandHeader, string demandRow, string? extraSubdirDuplicate = null)
    {
        string root = Dir("ukpn-" + Guid.NewGuid().ToString("N"));
        string raw = Path.Combine(root, "UkPowerNetworks", "RAWData");
        Directory.CreateDirectory(raw);
        File.WriteAllText(Path.Combine(raw, "ltds-table-1-circuit-data.csv"),
            "LicenceArea,GridSupplyPoint,FromNode,ToNode,FromSubstation,ToSubstation,OperatingVoltage (kV),LineName," +
            "Positive_Sequence_Impedance_R_Percent,Positive_Sequence_Impedance_X_Percent,Susceptance_B_Percent," +
            "Rating_Amps_Winter,Rating_Amps_Summer,Circuit_Length (km)\n" +
            "EPN,GSP1,N1,N2,SUBA,SUBB,33,L1,1,5,0,500,400,10\n");
        File.WriteAllText(Path.Combine(raw, "ltds-table-2a-transformer-2w.csv"), "HV_Node,LV_Node\n");
        File.WriteAllText(Path.Combine(raw, "ltds-table-3a-load-data-observed.csv"), demandHeader + "\n" + demandRow + "\n");
        if (extraSubdirDuplicate is not null)
        {
            string sub = Path.Combine(raw, extraSubdirDuplicate);
            Directory.CreateDirectory(sub);
            File.Copy(Path.Combine(raw, "ltds-table-1-circuit-data.csv"), Path.Combine(sub, "ltds-table-1-circuit-data.csv"));
        }
        return root;
    }

    [Fact]
    public void Ukpn_reads_the_newest_demand_vintage()
    {
        // A 2025/26 table. Before R300 the hard-coded 24_25 column read 0 everywhere and demand vanished.
        string root = UkpnRoot(
            "Substation,GridSupplyPoint,Maximum_Demand_24_25_MW,Maximum_Demand_25_26_MW,Maximum_Demand_25_26_PF,Firm_Capacity_MW",
            "SUBB,GSP1,9.0,12.5,0.95,20");
        LtdsDataset ds = UkpnSource.Load(root);
        LtdsDemandAssert(ds, 12.5, 0.95);
    }

    private static void LtdsDemandAssert(LtdsDataset ds, double mw, double pf)
    {
        var d = Assert.Single(ds.Demand);
        Assert.Equal(mw, d.MaxDemandMw);
        Assert.Equal(pf, d.PowerFactor);
    }

    [Fact]
    public void Ukpn_with_no_demand_column_fails_loud()
    {
        string root = UkpnRoot("Substation,GridSupplyPoint,Peak_MW", "SUBB,GSP1,12.5");
        var ex = Assert.Throws<InvalidDataException>(() => UkpnSource.Load(root));
        Assert.Contains("Maximum_Demand_YY_YY_MW", ex.Message);
    }

    [Fact]
    public void Ukpn_with_a_demand_column_but_no_demand_fails_loud()
    {
        string root = UkpnRoot("Substation,GridSupplyPoint,Maximum_Demand_25_26_MW", "SUBB,GSP1,0");
        var ex = Assert.Throws<InvalidDataException>(() => UkpnSource.Load(root));
        Assert.Contains("no positive demand", ex.Message);
    }

    [Fact]
    public void Ukpn_with_two_equally_good_file_matches_fails_loud()
    {
        string root = UkpnRoot(
            "Substation,GridSupplyPoint,Maximum_Demand_25_26_MW", "SUBB,GSP1,12.5", extraSubdirDuplicate: "2025");
        var ex = Assert.Throws<InvalidDataException>(() => UkpnSource.Load(root));
        Assert.Contains("ltds-table-1-circuit-data.csv", ex.Message);
    }
}
