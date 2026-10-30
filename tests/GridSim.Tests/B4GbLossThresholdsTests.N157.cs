using GridSim.Core.Model;
using GridSim.Core.Presentation;
using GridSim.Gda;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N157: NESO "Bioenergy" (1,149 lake rows) read as Other with H 0, and NGED "... (not CHP)" (149 rows) read as CHP. The
/// taxonomy now reads "bioenergy" as biomass, and a keyword directly after "not" / "non" does not match there.
/// </summary>
public sealed partial class B4GbLossThresholdsTests
{
    [Theory]
    [InlineData("Bioenergy", FuelKind.Biomass)]
    [InlineData("bioenergy CHP", FuelKind.Chp)]                       // a real CHP still reads as CHP (chp is earlier)
    [InlineData("Biomass & energy crops (not CHP)", FuelKind.Biomass)]
    [InlineData("Waste incineration (not CHP)", FuelKind.Biomass)]
    [InlineData("Non-CHP biomass", FuelKind.Biomass)]
    [InlineData("Small CHP", FuelKind.Chp)]
    [InlineData("CHP (Combined Heat and Power)", FuelKind.Chp)]
    [InlineData("not", FuelKind.Other)]
    public void N157_bioenergy_reads_as_biomass_and_a_negated_keyword_does_not_match(string text, FuelKind expected)
    {
        foreach (FuelReader reader in new[] { FuelReader.Gda, FuelReader.Presentation, FuelReader.Economics })
            Assert.Equal(expected, FuelTypes.Classify(text, reader));
    }

    [Fact]
    public void N157_the_importer_and_the_shells_give_biomass_inertia_and_synchronous_status()
    {
        foreach (string s in new[] { "Bioenergy", "Biomass & energy crops (not CHP)", "Waste incineration (not CHP)" })
        {
            Assert.Equal("biomass", FuelMap.Classify(s).Type);
            Assert.Equal(4.0, FuelMap.Classify(s).H);
            Assert.Equal("Biomass", FuelTaxonomy.Classify(s).Name);
            Assert.True(FuelTaxonomy.Classify(s).Synchronous);
        }
        Assert.Equal(new[] { "not", "non" }, FuelTypes.NegatingWords);
    }
}
