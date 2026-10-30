using GridSim.Gda.Csv;
using Parquet;
using Parquet.Schema;
using Xunit;

namespace GridSim.Tests.Gda;

/// <summary>
/// Round-trips a small Parquet file through the unified table abstraction  -  the
/// reader that lets the converter ingest the parquet-format GDA datasets.
/// </summary>
public class ParquetReaderTests
{
    [Fact]
    public async Task Reads_parquet_rows_by_column_name()
    {
        string path = Path.Combine(Path.GetTempPath(), "gridsim-pq-" + Guid.NewGuid().ToString("N") + ".parquet");
        var name = new DataField<string>("Busbar Name");
        var kv = new DataField<double>("Operating Voltage (kV)");
        var n = new DataField<int>("Count");
        var schema = new ParquetSchema(name, kv, n);

        try
        {
            using (Stream s = File.Create(path))
            {
                await using ParquetWriter w = await ParquetWriter.CreateAsync(schema, s);
                using ParquetRowGroupWriter rg = w.CreateRowGroup();
                await rg.WriteAsync(name, new[] { "ALPHA", "BETA" });
                await rg.WriteAsync(kv, new ReadOnlyMemory<double>(new[] { 132.0, 33.0 }));
                await rg.WriteAsync(n, new ReadOnlyMemory<int>(new[] { 7, 9 }));
            }

            using DataSourceTable t = DataSourceTable.Open(path);
            Assert.Contains("Busbar Name", t.Headers);
            var rows = t.Rows().ToList();
            Assert.Equal(2, rows.Count);
            Assert.Equal("ALPHA", rows[0]["Busbar Name"]);
            Assert.Equal(132.0, rows[0].Double("Operating Voltage (kV)"), 6);
            Assert.Equal(9.0, rows[1].Double("Count"), 6);
            Assert.False(rows[0].Has("Nonexistent"));
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
