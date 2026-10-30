using GridSim.Gda;
using Xunit;

namespace GridSim.Tests.Gda;

public class FuelMapTests
{
    [Theory]
    [InlineData("Nuclear", "nuclear", true)]
    [InlineData("CCGT", "ccgt", true)]
    [InlineData("Wind Offshore", "wind-offshore", false)]
    [InlineData("Solar Photovoltaic", "solar", false)]
    [InlineData("Battery Storage", "storage", false)]
    [InlineData("Pumped Storage", "pumped-storage", true)]
    [InlineData("Pump Storage", "pumped-storage", true)]        // NESO TEC register spelling (Dinorwig et al.)
    [InlineData("Interconnector", "interconnector", false)]
    [InlineData("", "other", false)]
    public void Classifies_fuel_type_and_inertia(string raw, string type, bool hasInertia)
    {
        FuelMap.Kind k = FuelMap.Classify(raw);
        Assert.Equal(type, k.Type);
        Assert.Equal(hasInertia, k.H > 0);
    }

    [Theory]
    [InlineData("Coventry 132kV", 132)]
    [InlineData("Exeter 132 kV", 132)]
    [InlineData("Feckenham 66kV", 66)]
    [InlineData("Cardiff East / Aberthaw", 132)]   // no number -> default 132
    [InlineData("Some 33kV Primary", 33)]
    public void Parses_gsp_reference_voltage(string group, int expected)
    {
        Assert.Equal(expected, (int)GdaModelBuilder.ParseGspKv(group));
    }
}
