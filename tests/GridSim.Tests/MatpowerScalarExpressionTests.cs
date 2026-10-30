using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// A MATPOWER scalar such as <c>mpc.baseMVA</c> is evaluated as a whole expression, exactly like a matrix entry.
/// The bundled case533mt_hi writes <c>mpc.baseMVA = 50/3;</c>; reading only the leading "50" put every MW and MVAr
/// of that case on a base three times too large, silently.
/// </summary>
public class MatpowerScalarExpressionTests
{
    // A two-bus case whose base is given by the expression under test.
    private static string Case(string baseMva) => @"function mpc = scalar
mpc.baseMVA = " + baseMva + @";
mpc.bus = [ 1 3 0 0 0 0 1 1 0 11 1 1.1 0.9; 2 1 1 0.5 0 0 1 1 0 11 1 1.1 0.9; ];
mpc.gen = [ 1 0 0 100 -100 1 100 1 100 0; ];
mpc.branch = [ 1 2 0.01 0.1 0 0 0 0 0 0 1 -360 360; ];";

    [Theory]
    [InlineData("100", 100.0)]
    [InlineData("50/3", 50.0 / 3.0)]
    [InlineData("50 / 3", 50.0 / 3.0)]
    [InlineData("2*sqrt(4)", 4.0)]
    [InlineData("1e2", 100.0)]
    public void BaseMva_expression_is_evaluated_in_full(string rhs, double expected)
    {
        GridModel m = MatpowerParser.Parse(Case(rhs));
        // Exact: the evaluator performs the same IEEE operations as the C# literal.
        Assert.Equal(expected, m.BaseMva);
    }

    [Theory]
    [InlineData("100abc")]
    [InlineData("50/")]
    [InlineData("50)3")]
    public void BaseMva_with_trailing_text_is_refused_not_truncated(string rhs)
    {
        Assert.Throws<FormatException>(() => MatpowerParser.Parse(Case(rhs)));
    }

    [Fact]
    public void Bundled_case533mt_hi_loads_on_its_own_50_over_3_MVA_base()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "data", "matpower", "dist", "case533mt_hi.m");
        Assert.True(File.Exists(path), $"bundled case missing: {path}");
        GridModel m = MatpowerParser.ParseFile(path);
        Assert.Equal(50.0 / 3.0, m.BaseMva);
    }
}
