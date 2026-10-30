using System.IO;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-663: a MATPOWER row shorter than the standard columns the parser reads, or holding a NaN, is refused by matrix,
/// row and column (it used to be padded silently with defaults).
/// </summary>
public sealed class MatpowerRowChecksTests
{
    private const string Gen = "1 0 0 300 -300 1 100 1 250 10";
    private const string Branch = "1 2 0.01 0.1 0.02 250 250 250 0 0 1 -360 360";

    private static string Case(string gen = Gen, string branch = Branch) =>
        "function mpc = rowcheck\n" +
        "mpc.version = '2';\n" +
        "mpc.baseMVA = 100;\n" +
        "mpc.bus = [\n" +
        "1 3 0 0 0 0 1 1 0 230 1 1.1 0.9;\n" +
        "2 1 50 10 0 0 1 1 0 230 1 1.1 0.9;\n" +
        "];\n" +
        "mpc.gen = [\n" + gen + ";\n];\n" +
        "mpc.branch = [\n" + branch + ";\n];\n";

    [Fact]
    public void The_complete_case_parses()
    {
        GridModel m = MatpowerParser.Parse(Case(), "rowcheck");
        Assert.Equal(2, m.BusCount);
        Assert.Single(m.Branches);
    }

    [Fact]
    public void A_gen_row_short_of_the_standard_columns_is_refused()
    {
        var ex = Assert.Throws<InvalidDataException>(() => MatpowerParser.Parse(Case(gen: "1 0 0 300 -300 1 100 1 250"), "rowcheck"));
        Assert.Contains("mpc.gen row 1 has 9 number(s)", ex.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_branch_row_short_of_BR_STATUS_is_refused()
    {
        var ex = Assert.Throws<InvalidDataException>(() => MatpowerParser.Parse(Case(branch: "1 2 0.01 0.1 0.02 250 250 250 0 0"), "rowcheck"));
        Assert.Contains("mpc.branch row 1 has 10 number(s)", ex.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_matrix_not_closed_before_the_next_one_is_refused()
    {
        // GR-647: the gen matrix loses its ']' so its first ']' is the branch matrix's; the two used to read as one.
        string text = Case().Replace(Gen + ";\n];", Gen + ";\n", System.StringComparison.Ordinal);
        Assert.NotEqual(Case(), text);
        var ex = Assert.Throws<InvalidDataException>(() => MatpowerParser.Parse(text, "rowcheck"));
        Assert.Contains("is not closed before the next '['", ex.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_NaN_in_a_branch_row_is_refused_by_column()
    {
        var ex = Assert.Throws<InvalidDataException>(() => MatpowerParser.Parse(Case(branch: "1 2 NaN 0.1 0.02 250 250 250 0 0 1 -360 360"), "rowcheck"));
        Assert.Contains("mpc.branch row 1 column 3 is NaN", ex.Message, System.StringComparison.Ordinal);
    }
}
