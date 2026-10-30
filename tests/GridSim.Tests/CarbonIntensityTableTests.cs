using GridSim.Core.Physics.Experimental;
using GridSim.Core.Presentation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Pins R145/R146: every per-fuel carbon figure is read from the ONE table, <see cref="CarbonIntensityTable"/>.
/// The MACHINE-tab stack rates, the MARKET-tab grid intensities and the carbon-tracing factors all resolve to
/// named constants there (values unchanged; the three sets still disagree, which the table documents).
/// </summary>
public class CarbonIntensityTableTests
{
    [Fact]
    public void Machine_tab_stack_rates_come_from_the_table()
    {
        Assert.Equal(CarbonIntensityTable.StackGasTPerMwh, CarbonFactors.CombustionTonnesPerMwh("CCGT"));
        Assert.Equal(CarbonIntensityTable.StackGasTPerMwh, CarbonFactors.CombustionTonnesPerMwh("Gas"));
        Assert.Equal(CarbonIntensityTable.StackGasTPerMwh, CarbonFactors.CombustionTonnesPerMwh("CHP"));
        Assert.Equal(CarbonIntensityTable.StackOcgtTPerMwh, CarbonFactors.CombustionTonnesPerMwh("OCGT"));
        Assert.Equal(CarbonIntensityTable.StackCoalTPerMwh, CarbonFactors.CombustionTonnesPerMwh("Coal"));
    }

    [Fact]
    public void Market_tab_grid_intensities_come_from_the_table()
    {
        Assert.Equal(CarbonIntensityTable.GridCoalKgPerMwh, CarbonFactors.IntensityKgPerMwh("Coal", true));
        Assert.Equal(CarbonIntensityTable.GridGasKgPerMwh, CarbonFactors.IntensityKgPerMwh("CCGT", true));
        Assert.Equal(CarbonIntensityTable.GridOcgtKgPerMwh, CarbonFactors.IntensityKgPerMwh("OCGT", true));
        Assert.Equal(CarbonIntensityTable.GridChpKgPerMwh, CarbonFactors.IntensityKgPerMwh("CHP", true));
        Assert.Equal(CarbonIntensityTable.GridBiomassKgPerMwh, CarbonFactors.IntensityKgPerMwh("Biomass", false));
        Assert.Equal(CarbonIntensityTable.GridOtherKgPerMwh, CarbonFactors.IntensityKgPerMwh("Other", false));
        Assert.Equal(CarbonIntensityTable.GridUnknownFossilKgPerMwh, CarbonFactors.IntensityKgPerMwh("mystery", true));
    }

    [Fact]
    public void Carbon_tracing_factors_come_from_the_table()
    {
        Assert.Equal(CarbonIntensityTable.TraceCoalTPerMwh, FuelCarbon.IntensityTPerMwh("lignite"));
        Assert.Equal(CarbonIntensityTable.TraceOcgtTPerMwh, FuelCarbon.IntensityTPerMwh("OCGT"));
        Assert.Equal(CarbonIntensityTable.TraceCcgtTPerMwh, FuelCarbon.IntensityTPerMwh("CCGT"));
        Assert.Equal(CarbonIntensityTable.TraceChpTPerMwh, FuelCarbon.IntensityTPerMwh("CHP"));
        Assert.Equal(CarbonIntensityTable.TraceBiomassTPerMwh, FuelCarbon.IntensityTPerMwh("biogas"));
        Assert.Equal(CarbonIntensityTable.TraceImportTPerMwh, FuelCarbon.IntensityTPerMwh("HVDC import"));
        Assert.Equal(CarbonIntensityTable.TraceOilTPerMwh, FuelCarbon.IntensityTPerMwh("diesel"));
        Assert.Equal(CarbonIntensityTable.TraceGenericGasTPerMwh, FuelCarbon.IntensityTPerMwh("gas"));
        Assert.Equal(CarbonIntensityTable.TraceUnknownTPerMwh, FuelCarbon.DefaultTPerMwh);
        Assert.Equal(CarbonIntensityTable.TraceUnknownTPerMwh, FuelCarbon.IntensityTPerMwh("unobtainium"));
    }
}
