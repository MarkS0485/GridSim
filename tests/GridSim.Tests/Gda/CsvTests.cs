using GridSim.Gda.Csv;
using Xunit;

namespace GridSim.Tests.Gda;

public class CsvTests
{
    private static CsvTable From(string text) => CsvTable.Create(new StringReader(text));

    [Fact]
    public void Reads_header_and_rows_by_name()
    {
        using CsvTable t = From("a,b,c\n1,2,3\n4,5,6\n");
        var rows = t.Rows().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("2", rows[0]["b"]);
        // GR-791: CsvRow.Int was dead code and is removed; the cell is checked through Double instead.
        Assert.Equal(6.0, rows[1].Double("c"));
    }

    [Fact]
    public void Strips_bom_and_tab_polluted_headers()
    {
        using CsvTable t = From("\"Licence\t\",GSP Group\nX,Y\n");
        Assert.True(t.HasColumn("Licence"));
        Assert.True(t.HasColumn("GSP Group"));
        Assert.Equal("X", t.Rows().First()["Licence"]);
    }

    [Fact]
    public void Handles_quoted_commas_and_escaped_quotes()
    {
        using CsvTable t = From("name,val\n\"Smith, John\",\"a \"\"quote\"\" here\"\n");
        CsvRow r = t.Rows().First();
        Assert.Equal("Smith, John", r["name"]);
        Assert.Equal("a \"quote\" here", r["val"]);
    }

    [Fact]
    public void Handles_embedded_newline_in_quoted_field()
    {
        using CsvTable t = From("a,b\n\"line1\nline2\",x\n");
        CsvRow r = t.Rows().First();
        Assert.Equal("line1\nline2", r["a"]);
        Assert.Equal("x", r["b"]);
    }

    [Fact]
    public void Tolerates_ragged_rows_and_blank_lines()
    {
        using CsvTable t = From("a,b,c\n1,2\n\n3,4,5\n");
        var rows = t.Rows().ToList();
        Assert.Equal(2, rows.Count);            // blank line skipped
        Assert.Equal(string.Empty, rows[0]["c"]); // missing trailing cell -> empty
        // GR-791: CsvRow.Int was dead code and is removed; the cell is checked through Double instead.
        Assert.Equal(5.0, rows[1].Double("c"));
    }

    [Fact]
    public void Double_tolerates_thousands_and_units()
    {
        using CsvTable t = From("x\n\"1,234.5\"\n");
        Assert.Equal(1234.5, t.Rows().First().Double("x"), 3);
    }

    [Theory]
    [InlineData("0,5")]       // a decimal comma used to read as 5
    [InlineData("1,23")]
    [InlineData("12,3456")]
    [InlineData("(5)")]       // accounting negative used to read as -5
    [InlineData("5-")]        // trailing sign used to read as -5
    public void A_number_in_a_guessed_form_is_refused_by_column(string cell)
    {
        // GR-379: the reader used to strip every comma and parse with NumberStyles.Any; these forms are now refused.
        using CsvTable t = From($"mw\n\"{cell}\"\n");
        var ex = Assert.Throws<InvalidDataException>(() => t.Rows().First().Double("mw"));
        Assert.Contains("'mw'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("12,345,678", 12345678.0)]
    [InlineData("-1,234.5", -1234.5)]
    [InlineData("45%", 45.0)]
    [InlineData("1.5e3", 1500.0)]
    public void Plain_and_thousands_grouped_numbers_still_read(string cell, double expected)
    {
        // GR-379: the accepted forms are unchanged.
        using CsvTable t = From($"mw\n\"{cell}\"\n");
        Assert.Equal(expected, t.Rows().First().Double("mw"));
    }

    [Fact]
    public void Text_that_is_not_a_number_still_takes_the_fallback()
    {
        // GR-379: "N/A" is no value, not a refused number, as before.
        using CsvTable t = From("mw\nN/A\n");
        Assert.Equal(-1.0, t.Rows().First().Double("mw", -1.0));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void A_non_finite_numeric_cell_is_refused_by_column(string cell)
    {
        // GR-805: 'NaN' and 'Infinity' parse as doubles but are not numbers; they used to pass every "x <= 0" gate.
        using CsvTable t = From($"mw\n{cell}\n");
        var ex = Assert.Throws<InvalidDataException>(() => t.Rows().First().Double("mw"));
        Assert.Contains("'mw'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blank_numeric_cell_takes_the_fallback()
    {
        using CsvTable t = From("mw,x\n,1\n");
        Assert.Equal(-1.0, t.Rows().First().Double("mw", -1.0));
    }

    [Fact]
    public void A_duplicated_header_is_refused_when_it_is_read()
    {
        // GR-793: "Winter" and "Winter " normalise alike; the second used to be unreachable and a blank first read as 0.
        using CsvTable t = From("name,Winter,Winter \nA,,120\n");
        CsvRow r = t.Rows().First();
        Assert.Equal("A", r["name"]);   // an unambiguous column still reads
        var ex = Assert.Throws<InvalidDataException>(() => r.Double("Winter"));
        Assert.Contains("'Winter'", ex.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => t.HasColumn("winter"));
    }

    [Fact]
    public void Duplicated_blank_headers_that_are_never_read_do_not_block_a_table()
    {
        // GR-793: a padded export's blank trailing headers are duplicates too, but nobody asks for them.
        using CsvTable t = From("a,b,,\n1,2,,\n");
        Assert.Equal("2", t.Rows().First()["b"]);
    }

    [Fact]
    public void A_row_with_non_blank_extra_cells_is_refused()
    {
        // GR-811: an unquoted comma in a value shifts the rest of the row; it used to be accepted silently.
        using CsvTable t = From("name,status,kv\nSmith, Jones St,Live,11\n");
        var ex = Assert.Throws<InvalidDataException>(() => t.Rows().ToList());
        Assert.Contains("record 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_with_only_blank_extra_cells_is_accepted()
    {
        // GR-811: a trailing comma (or several) carries no value and shifts nothing.
        using CsvTable t = From("a,b\n1,2,\n3,4, ,\n");
        var rows = t.Rows().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("4", rows[1]["b"]);
    }
}
