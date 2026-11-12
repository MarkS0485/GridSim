using GridSim.Gda.Ltds;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-289: the LTDS readers looked every column up by name, and a column that does not exist reads as blank or 0 on every
/// row, so a renamed column (R1 to "R1 (ohm)") built a model of zero-impedance branches with no error. Each reader now
/// checks its columns exist (CsvTable.RequireColumns) before the row loop and refuses by name, listing the headers found.
/// Pinned: a renamed column is refused for the circuits and the demand reader, naming the column; a table with every
/// column is read as before; a header-only table is still an empty list.
/// </summary>
public sealed class Gr289LtdsRequireColumnsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-gr289-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string Write(string name, string text)
    {
        Directory.CreateDirectory(_dir);
        string p = Path.Combine(_dir, name);
        File.WriteAllText(p, text);
        return p;
    }

    private const string CircuitHeader =
        "Licence,GSP Group,BSP Group,Node 1,Node 2,Circuit Status,Operating Voltage (kV),Circuit Code,R1,X1,B1,R0,X0,Winter,Intermediate Cool,Intermediate Warm,Summer,Circuit Length (km),Equipment\n";

    [Fact]
    public void Gr289_a_circuits_table_with_every_column_is_read_as_before()
    {
        string p = Write("c.csv", CircuitHeader + "NGED,G1,B1,N1,N2,Existing,132,C1,0.1,0.2,0.3,0.4,0.5,100,0,0,90,5.5,OHL\n");
        LtdsCircuit c = Assert.Single(LtdsReader.Circuits(p));
        Assert.Equal(0.1, c.R1Ohm);
    }

    [Fact]
    public void Gr289_a_renamed_circuits_column_is_refused_naming_it_and_the_headers_found()
    {
        string p = Write("c.csv", CircuitHeader.Replace(",R1,", ",R1 (ohm),") + "NGED,G1,B1,N1,N2,Existing,132,C1,0.1,0.2,0.3,0.4,0.5,100,0,0,90,5.5,OHL\n");
        var ex = Assert.Throws<InvalidDataException>(() => LtdsReader.Circuits(p));
        Assert.Contains("'R1'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("LTDS circuits table", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'R1 (ohm)'", ex.Message, StringComparison.Ordinal);   // the headers found
    }

    [Fact]
    public void Gr289_a_demand_table_without_the_max_demand_column_is_refused()
    {
        string p = Write("d.csv", "Licence,GSP Group,BSP Group,Primary Substation,Node,Status,Operating Voltage,Firm Capacity of Substation\n" +
                                  "NGED,G1,B1,Prim,N1,Existing,11,20\n");
        var ex = Assert.Throws<InvalidDataException>(() => LtdsReader.Demand(p));
        Assert.Contains("'Current Year Max Demand'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr289_a_header_only_circuits_table_is_an_empty_list_not_a_refusal()
    {
        string p = Write("c.csv", CircuitHeader);
        Assert.Empty(LtdsReader.Circuits(p));
    }
}
