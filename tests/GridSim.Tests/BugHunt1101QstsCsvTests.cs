using System.Text.Json;
using GridSim.Core.Qsts;
using Xunit;

namespace GridSim.Tests;

/// <summary>Static bug hunt of 2026-11-01: #162, the QSTS CSV header decision and file-line error messages.</summary>
public sealed partial class BugHunt1101WebTests
{
    private static double[] QstsCsv(string content, string? columnJson, bool? header = null)
    {
        string path = Path.Combine(Path.GetTempPath(), "gridsim-bh1101-" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(path, content);
        try
        {
            JsonElement? column = null;
            if (columnJson is not null) { using JsonDocument d = JsonDocument.Parse(columnJson); column = d.RootElement.Clone(); }
            return QstsFile.ReadCsvColumn(path, column, "profile 'p'", header);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void B162_a_headerless_file_with_a_timestamp_first_column_keeps_its_first_row()
    {
        // The bug: the first cell is a timestamp, so the old rule took row 1 as a header and dropped 0.83.
        const string csv = "2026-01-01 00:00,0.83\n2026-01-01 00:15,0.80\n2026-01-01 00:30,0.78\n";
        Assert.Equal(new[] { 0.83, 0.80, 0.78 }, QstsCsv(csv, "1"));
    }

    [Fact]
    public void B162_a_real_header_is_still_recognised_and_a_declaration_wins()
    {
        const string withHeader = "time,load\n00:00,0.6\n00:15,0.7\n";
        Assert.Equal(new[] { 0.6, 0.7 }, QstsCsv(withHeader, "1"));
        Assert.Equal(new[] { 0.6, 0.7 }, QstsCsv(withHeader, "\"load\""));
        Assert.Equal(new[] { 0.6, 0.7 }, QstsCsv(withHeader, "1", header: true));
        // A header whose cell for the column is numeric ("2026" as a column name) needs the declaration.
        const string numericName = "site,2026\na,0.5\nb,0.4\n";
        Assert.Equal(new[] { 0.5, 0.4 }, QstsCsv(numericName, "1", header: true));
        // header false with a named column is refused by name.
        var ex = Assert.Throws<InvalidDataException>(() => QstsCsv(withHeader, "\"load\"", header: false));
        Assert.Contains("needs a header row", ex.Message, StringComparison.Ordinal);
        // header false on a file that has one: the header cell is reported, by its file line.
        var bad = Assert.Throws<InvalidDataException>(() => QstsCsv(withHeader, "1", header: false));
        Assert.Contains("line 1: column 1 is not a finite number", bad.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void B162_errors_name_the_file_line_counting_comments_and_blank_lines()
    {
        const string csv = "# a comment\n\nload\n0.5\n# another\nnot-a-number\n";
        var ex = Assert.Throws<InvalidDataException>(() => QstsCsv(csv, null));
        Assert.Contains("line 6: column 0 is not a finite number", ex.Message, StringComparison.Ordinal);
    }
}
