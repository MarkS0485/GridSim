using System;
using GridSim.Core.Model;
using GridSim.Core.Physics.Experimental;
using GridSim.Core.Presentation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R145 / R146 (CF4, October 2026; ruled by main under Ruling R1): ONE per-fuel carbon table, the NESO Carbon Intensity
/// Regional Forecast Methodology (May 2024) Table 1 figures, direct combustion (biomass the published consumption-based
/// figure, named), held in t CO2 / MWh with the kg CO2 / MWh form derived as 1000 x t. The MACHINE tab, the MARKET tab
/// and carbon tracing all read it, so the same fuel has the same figure in every view.
/// </summary>
public sealed class Cf4CarbonTableTests
{
    private static void SameBits(double expected, double actual) =>
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));

    [Theory]
    [InlineData(FuelKind.Coal, 0.937, 937.0)]
    [InlineData(FuelKind.Ccgt, 0.394, 394.0)]
    [InlineData(FuelKind.Ocgt, 0.651, 651.0)]
    [InlineData(FuelKind.Gas, 0.394, 394.0)]
    [InlineData(FuelKind.Chp, 0.394, 394.0)]
    [InlineData(FuelKind.Recip, 0.935, 935.0)]
    [InlineData(FuelKind.Biomass, 0.120, 120.0)]
    [InlineData(FuelKind.Other, 0.300, 300.0)]
    [InlineData(FuelKind.Nuclear, 0.0, 0.0)]
    [InlineData(FuelKind.WindOnshore, 0.0, 0.0)]
    [InlineData(FuelKind.WindOffshore, 0.0, 0.0)]
    [InlineData(FuelKind.Solar, 0.0, 0.0)]
    [InlineData(FuelKind.Hydro, 0.0, 0.0)]
    [InlineData(FuelKind.Tidal, 0.0, 0.0)]
    [InlineData(FuelKind.PumpedStorage, 0.0, 0.0)]
    [InlineData(FuelKind.Storage, 0.0, 0.0)]
    [InlineData(FuelKind.Hydrogen, 0.0, 0.0)]
    [InlineData(FuelKind.Interconnector, 0.0, 0.0)]
    [InlineData(FuelKind.GridInfeed, 0.0, 0.0)]
    public void The_table_holds_the_published_figures_and_the_kg_form_is_the_published_whole_number(
        FuelKind kind, double tPerMwh, double kgPerMwh)
    {
        // Bit for bit: the t literal is the decimal the source publishes / 1000, and 1000 x it rounds to the published
        // whole number exactly (the argument is on CarbonIntensityTable).
        SameBits(tPerMwh, CarbonIntensityTable.TPerMwh(kind));
        SameBits(kgPerMwh, CarbonIntensityTable.KgPerMwh(kind));
        SameBits(CarbonIntensityTable.TPerMwh(kind) * CarbonIntensityTable.KgPerTonne, CarbonIntensityTable.KgPerMwh(kind));
    }

    [Fact]
    public void Every_kind_has_a_figure()
    {
        foreach (FuelKind k in Enum.GetValues<FuelKind>())
        {
            Assert.True(double.IsFinite(CarbonIntensityTable.TPerMwh(k)) && CarbonIntensityTable.TPerMwh(k) >= 0, k.ToString());
            Assert.True(double.IsFinite(CarbonIntensityTable.KgPerMwh(k)), k.ToString());
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => CarbonIntensityTable.TPerMwh((FuelKind)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => CarbonIntensityTable.KgPerMwh((FuelKind)999));
    }

    [Theory]
    [InlineData("Coal", true)]
    [InlineData("CCGT", true)]
    [InlineData("Gas", true)]
    [InlineData("OCGT", true)]
    [InlineData("CHP", true)]
    [InlineData("Reciprocating engine", true)]
    [InlineData("Biomass", false)]
    [InlineData("Other", false)]
    [InlineData("Nuclear", false)]
    [InlineData("Wind (offshore)", false)]
    [InlineData("Solar", false)]
    [InlineData("Interconnector", false)]
    public void The_machine_and_market_tabs_read_the_same_figure_in_two_units(string name, bool isFossil)
    {
        // The MACHINE tab's t/MWh x 1000 is the MARKET tab's kg/MWh for every painted name, with the fossil flag the
        // shells give that name (FuelTaxonomy's category).
        SameBits(CarbonFactors.CombustionTonnesPerMwh(name) * CarbonIntensityTable.KgPerTonne,
            CarbonFactors.IntensityKgPerMwh(name, isFossil));
    }

    [Theory]
    [InlineData("coal", "Coal")]
    [InlineData("CCGT", "CCGT")]
    [InlineData("OCGT", "OCGT")]
    [InlineData("gas", "Gas")]
    [InlineData("CHP", "CHP")]
    [InlineData("biomass", "Biomass")]
    [InlineData("nuclear", "Nuclear")]
    [InlineData("wind offshore", "Wind (offshore)")]
    [InlineData("interconnector", "Interconnector")]
    public void Carbon_tracing_reads_the_same_figure_as_the_machine_tab(string type, string displayName)
        => SameBits(CarbonFactors.CombustionTonnesPerMwh(displayName), FuelCarbon.IntensityTPerMwh(type));

    [Fact]
    public void The_moved_figures_are_exact()
    {
        // R145 / R146: before -> after, exactly as recorded on CarbonIntensityTable.
        SameBits(0.394, CarbonFactors.CombustionTonnesPerMwh("CCGT"));   // was 0.35
        SameBits(0.651, CarbonFactors.CombustionTonnesPerMwh("OCGT"));   // was 0.49
        SameBits(0.937, CarbonFactors.CombustionTonnesPerMwh("Coal"));   // was 0.95
        SameBits(0.120, CarbonFactors.CombustionTonnesPerMwh("Biomass")); // was 0
        SameBits(300.0, CarbonFactors.IntensityKgPerMwh("mystery", isFossil: true));   // was 450
        SameBits(0.0, CarbonFactors.IntensityKgPerMwh("mystery", isFossil: false));
        SameBits(0.937, FuelCarbon.IntensityTPerMwh("Coal"));        // was 0.91
        SameBits(0.651, FuelCarbon.IntensityTPerMwh("OCGT"));        // was 0.44
        SameBits(0.394, FuelCarbon.IntensityTPerMwh("CCGT gas"));    // was 0.36
        SameBits(0.394, FuelCarbon.IntensityTPerMwh("gas"));         // was 0.40
        SameBits(0.0, FuelCarbon.IntensityTPerMwh("interconnector")); // was 0.20
        SameBits(0.935, FuelCarbon.IntensityTPerMwh("diesel"));      // was 0.65
        SameBits(0.300, FuelCarbon.IntensityTPerMwh(""));            // unchanged (Other)
        SameBits(0.300, FuelCarbon.DefaultTPerMwh);
    }

    [Fact]
    public void The_old_per_view_names_are_the_one_figure_of_their_fuel()
    {
        SameBits(CarbonIntensityTable.CcgtTPerMwh, CarbonIntensityTable.StackGasTPerMwh);
        SameBits(CarbonIntensityTable.OcgtTPerMwh, CarbonIntensityTable.TraceOcgtTPerMwh);
        SameBits(CarbonIntensityTable.OcgtKgPerMwh, CarbonIntensityTable.GridOcgtKgPerMwh);
        SameBits(CarbonIntensityTable.CoalTPerMwh, CarbonIntensityTable.StackCoalTPerMwh);
        SameBits(CarbonIntensityTable.CoalTPerMwh, CarbonIntensityTable.TraceCoalTPerMwh);
        SameBits(CarbonIntensityTable.CoalKgPerMwh, CarbonIntensityTable.GridCoalKgPerMwh);
        SameBits(CarbonIntensityTable.OtherKgPerMwh, CarbonIntensityTable.GridUnknownFossilKgPerMwh);
        SameBits(CarbonIntensityTable.OtherTPerMwh, CarbonIntensityTable.TraceUnknownTPerMwh);
    }

    [Fact]
    public void No_other_core_file_carries_a_per_fuel_carbon_literal()
    {
        // The carbon tracer must hold no figure of its own: its source reads the table only.
        string root = RepoRoot();
        string tracing = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src", "GridSim.Core", "Physics",
            "Experimental", "CarbonTracing.cs"));
        string body = tracing[tracing.IndexOf("public static class FuelCarbon", StringComparison.Ordinal)..];
        foreach (string literal in new[] { "0.91", "0.44", "0.36", "0.40", "0.34", "0.20", "0.65", "0.12" })
            Assert.DoesNotContain(literal, body);
    }

    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root (GridSim.slnx) not found");
    }
}
