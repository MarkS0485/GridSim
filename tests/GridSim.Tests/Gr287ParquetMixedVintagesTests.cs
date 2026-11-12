using GridSim.Gda.Csv;
using Parquet;
using Parquet.Schema;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-287: DataSourceTable.Open concatenated every Parquet part under a directory, so a folder holding two dated
/// publications (for example a 2024 and a 2025 vintage) was read as one table with every record twice. It now refuses a
/// directory whose Parquet part names carry two or more different publication dates, naming the files and dates.
/// Pinned: mixed dated parts are refused; parts all carrying one date, undated parts and a single file are still read in
/// full, row for row, as before.
/// </summary>
public sealed class Gr287ParquetMixedVintagesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gridsim-gr287-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private async Task Part(string name, params double[] values)
    {
        Directory.CreateDirectory(_root);
        var field = new DataField<double>("Value");
        using Stream s = File.Create(Path.Combine(_root, name));
        await using ParquetWriter w = await ParquetWriter.CreateAsync(new ParquetSchema(field), s);
        using ParquetRowGroupWriter rg = w.CreateRowGroup();
        await rg.WriteAsync(field, new ReadOnlyMemory<double>(values));
    }

    private static List<double> ReadAll(string path)
    {
        using DataSourceTable t = DataSourceTable.Open(path);
        return t.Rows().Select(r => r.Double("Value")).ToList();
    }

    [Fact]
    public async Task Gr287_parts_from_two_publication_dates_are_refused_naming_both_files()
    {
        await Part("ltds-table-1-05-may-2024.parquet", 1.0, 2.0);
        await Part("ltds-table-1-12-jun-2025.parquet", 3.0);
        var ex = Assert.Throws<InvalidDataException>(() => DataSourceTable.Open(_root));
        Assert.Contains("ltds-table-1-05-may-2024.parquet (2024-05-05)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ltds-table-1-12-jun-2025.parquet (2025-06-12)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("different publication", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gr287_parts_that_share_one_date_are_still_read_together_in_name_order()
    {
        await Part("a-05-may-2024-part-0.parquet", 1.0, 2.0);
        await Part("a-05-may-2024-part-1.parquet", 3.0);
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, ReadAll(_root));
    }

    [Fact]
    public async Task Gr287_undated_parts_are_still_read_together_in_name_order()
    {
        await Part("part-0000.parquet", 1.0);
        await Part("part-0001.parquet", 2.0, 3.0);
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, ReadAll(_root));
    }

    [Fact]
    public async Task Gr287_pointing_at_one_dated_file_of_a_mixed_folder_still_works()
    {
        await Part("ltds-table-1-05-may-2024.parquet", 1.0, 2.0);
        await Part("ltds-table-1-12-jun-2025.parquet", 3.0);
        Assert.Equal(new[] { 3.0 }, ReadAll(Path.Combine(_root, "ltds-table-1-12-jun-2025.parquet")));
    }
}
