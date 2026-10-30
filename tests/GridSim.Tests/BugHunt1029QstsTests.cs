using System.Text.Json;
using GridSim.Core.Qsts;
using Xunit;

namespace GridSim.Tests;

/// <summary>Static bug hunt of 2026-10-29 (GridSim 2.0.183), QSTS file findings. See BugHunt1029DynamicsTests.cs.</summary>
public sealed partial class BugHunt1029Tests
{
    private static double[] BHReadCsv(string csv, string columnJson)
    {
        string path = Path.Combine(Path.GetTempPath(), "gridsim-bh-" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(path, csv);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(columnJson);
            return QstsFile.ReadCsvColumn(path, doc.RootElement.Clone(), "profile 'p'");
        }
        finally { File.Delete(path); }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // BH79: a negative CSV column index is refused by name.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void BH79_a_negative_csv_column_index_is_refused_by_name()
    {
        var ex = Assert.Throws<InvalidDataException>(() => BHReadCsv("a,b\n1,2\n3,4\n", "-1"));
        Assert.Contains("column index -1 is negative", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // BH80: a quoted CSV header with a comma in it no longer shifts the columns.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void BH80_a_quoted_header_holding_a_comma_is_one_column()
    {
        // Three columns: time, "load, kW" and pv. Splitting on every comma made four header fields, so "pv" was column 3
        // of the header but column 2 of the data.
        const string csv = "time,\"load, kW\",pv\n0,10,1\n1,20,2\n";
        Assert.Equal(new[] { 1.0, 2.0 }, BHReadCsv(csv, "\"pv\""));
        Assert.Equal(new[] { 10.0, 20.0 }, BHReadCsv(csv, "\"load, kW\""));
        Assert.Equal(new[] { 10.0, 20.0 }, BHReadCsv(csv, "1"));
    }

    [Theory]
    [InlineData("a,b,c", new[] { "a", "b", "c" })]
    [InlineData(" a , b ,c ", new[] { "a", "b", "c" })]
    [InlineData("a,b,", new[] { "a", "b", "" })]
    [InlineData(",a", new[] { "", "a" })]
    [InlineData("\"x, y\",\"say \"\"hi\"\"\",z", new[] { "x, y", "say \"hi\"", "z" })]
    public void BH80_csv_split_keeps_the_plain_comma_rule_and_honours_quotes(string line, string[] expected)
        => Assert.Equal(expected, QstsFile.SplitCsvLine(line, "t"));

    [Theory]
    [InlineData("\"never closed,1")]
    [InlineData("\"closed\"junk,1")]
    public void BH80_a_malformed_quoted_field_is_refused(string line)
        => Assert.Throws<InvalidDataException>(() => QstsFile.SplitCsvLine(line, "t"));
}
