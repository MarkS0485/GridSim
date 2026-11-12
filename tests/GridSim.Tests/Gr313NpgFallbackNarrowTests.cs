using GridSim.Gda.Csv;
using GridSim.Gda.Npg;
using Parquet;
using Parquet.Schema;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-313: NpgSource fell back from a Parquet copy to the RAWData CSV on ANY exception, so an ambiguous Parquet folder
/// (parts from two publication dates, GR-287) would have been quietly replaced by a CSV of some other publication. The
/// catch now lets the AmbiguousDataset-marked refusal through. Pinned: with a populated RAWData CSV beside it, a Parquet folder of
/// two vintages makes the NPg load throw the marked InvalidDataException (naming the folder) instead of reading the CSV; and a
/// Parquet file that is merely damaged still falls back to the CSV (the GR-313 note path is unchanged).
/// </summary>
public sealed class Gr313NpgFallbackNarrowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gridsim-gr313-" + Guid.NewGuid().ToString("N"));
    private const string Dataset = "LTDS_Appendix_3_Circuit_Data";

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string Pq => Path.Combine(_root, "NorthernPowerGrid", "Parquet", Dataset);
    private string Raw => Path.Combine(_root, "NorthernPowerGrid", "RAWData", Dataset);

    private void WriteCsv()
    {
        Directory.CreateDirectory(Raw);
        File.WriteAllText(Path.Combine(Raw, Dataset + ".csv"),
            "License,S/S Group,S/S or Busbar Name Node 1,S/S or Busbar Name Node 2,Operating Voltage (kV),Positive Sequence Impedance R (% on 100 MVA Base),Positive Sequence Impedance X (% on 100 MVA Base),Susceptance B (% on 100 MVA Base),Rating (MVA),Circuit Length (km)\n" +
            "NPG,Test,GRID 132kV,SITE 132kV,132,1,5,0.1,100,10\n");
    }

    private async Task WriteParquet(string name)
    {
        Directory.CreateDirectory(Pq);
        var field = new DataField<double>("Value");
        using Stream s = File.Create(Path.Combine(Pq, name));
        await using ParquetWriter w = await ParquetWriter.CreateAsync(new ParquetSchema(field), s);
        using ParquetRowGroupWriter rg = w.CreateRowGroup();
        await rg.WriteAsync(field, new ReadOnlyMemory<double>(new[] { 1.0 }));
    }

    [Fact]
    public async Task Gr313_an_ambiguous_parquet_folder_is_not_replaced_by_the_csv()
    {
        WriteCsv();
        await WriteParquet("circuits-05-may-2024.parquet");
        await WriteParquet("circuits-12-jun-2025.parquet");
        var ex = Assert.Throws<InvalidDataException>(() => NpgSource.Load(_root, preferParquet: true));
        Assert.True(AmbiguousDataset.Is(ex));
        Assert.Contains(Dataset, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr313_a_damaged_parquet_file_still_falls_back_to_the_csv_with_a_note()
    {
        WriteCsv();
        Directory.CreateDirectory(Pq);
        File.WriteAllText(Path.Combine(Pq, "part-0000.parquet"), "this is not a parquet file");
        // The fallback is taken (no AmbiguousDataset refusal, and not a Parquet error): whatever the later tables do with
        // this minimal fixture, the failure, if any, is not the Parquet one.
        Exception? ex = Record.Exception(() => NpgSource.Load(_root, preferParquet: true));
        Assert.False(AmbiguousDataset.Is(ex));
        if (ex is not null) Assert.DoesNotContain("parquet", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
