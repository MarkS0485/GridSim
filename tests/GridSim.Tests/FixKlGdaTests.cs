using GridSim.Gda.Npg;
using Xunit;

namespace GridSim.Tests;

/// <summary>APP-27: the NPg reader now makes the column check the LTDS reader makes (GR-289) for the voltage columns it needs.</summary>
public sealed class FixKlGdaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gridsim-fixkl-npg-" + Guid.NewGuid().ToString("N"));
    private const string Dataset = "LTDS_Appendix_3_Circuit_Data";

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private void WriteCircuits(string voltageHeader)
    {
        string raw = Path.Combine(_root, "NorthernPowerGrid", "RAWData", Dataset);
        Directory.CreateDirectory(raw);
        File.WriteAllText(Path.Combine(raw, Dataset + ".csv"),
            "License,S/S Group,S/S or Busbar Name Node 1,S/S or Busbar Name Node 2," + voltageHeader + ",Rating (MVA),Circuit Length (km)\n" +
            "NPG,Test,GRID 132kV,SITE 132kV,132,100,10\n");
    }

    [Fact]
    public void App27_an_npg_circuits_table_with_a_renamed_voltage_column_is_refused_naming_the_column()
    {
        WriteCircuits("Operating Voltage kV");
        var ex = Assert.Throws<InvalidDataException>(() => NpgSource.Load(_root, preferParquet: false));
        Assert.Contains("'Operating Voltage (kV)'", ex.Message, StringComparison.Ordinal);
        Assert.Contains(Dataset, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void App27_the_column_check_passes_when_the_voltage_column_is_present()
    {
        WriteCircuits("operating voltage (kv)");   // any case
        // The later tables of this minimal fixture may still be missing; the failure, if any, is not the column check.
        Exception? ex = Record.Exception(() => NpgSource.Load(_root, preferParquet: false));
        if (ex is not null) Assert.DoesNotContain("Operating Voltage (kV)", ex.Message, StringComparison.Ordinal);
    }
}
