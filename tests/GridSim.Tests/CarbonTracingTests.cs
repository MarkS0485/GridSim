using GridSim.Core.Model;
using GridSim.Core.Physics.Experimental;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Carbon tracing - the fuel/CO2 each load actually consumes, from generation tracing weighted by
/// per-fuel intensity. Pins the emission-factor endpoints (zero-carbon source -> 0 gCO2/kWh; coal ->
/// 937, the NESO figure since the 2026-10 end pass; was ~910), the fuel classifier, and conservation (load CO2 sums to what the generators emitted).
/// </summary>
public class CarbonTracingTests
{
    // 3-bus radial: one generator at bus 1 (fuel set by the caller) feeds loads at 2 and 3.
    private static GridModel Radial(string fuel) => new()
    {
        BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack),
            new Bus(2, BusType.Pq, Pd: 40),
            new Bus(3, BusType.Pq, Pd: 30),
        },
        Generators = new[] { new Generator(1, Pg: 0, Type: fuel) },
        Branches = new[] { new Branch(1, 2, 0.01, 0.05), new Branch(2, 3, 0.01, 0.05) },
    };

    [Theory]
    [InlineData("nuclear", 0.0)]
    [InlineData("wind offshore", 0.0)]
    // Moved 2026-10 (END-PASS CF4 row 3, end pass; NESO "Carbon Intensity Regional Forecast Methodology", May 2024,
    // Table 1 (direct combustion), the one per-fuel table of R145 / R146, Ruling R1 and Mark's go on the end-pass moves
    // 2026-10-11): tracing now reads the one sourced table instead of its own unsourced set. Before -> after: coal 910 ->
    // 937, ccgt 360 -> 394 gCO2/kWh (observed: Actual 937 and 394, 1.95.273-275 runs); nuclear and wind 0 unchanged;
    // tolerance (0 dp) unchanged.
    [InlineData("coal", 937.0)]
    [InlineData("ccgt", 394.0)]
    public void A_single_fuel_source_gives_every_load_that_fuels_intensity(string fuel, double expectedGPerKwh)
    {
        GridModel m = Radial(fuel);
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged);

        CarbonTracing.CarbonTraceResult c = CarbonTracing.Trace(m, r);
        Assert.True(c.Traced);
        foreach (CarbonTracing.LoadCarbon lc in c.Loads)
            Assert.Equal(expectedGPerKwh, lc.GramsPerKwh, 0); // gCO2/kWh, whole number
        Assert.Equal(expectedGPerKwh, c.SystemGramsPerKwh, 0);
    }

    [Fact]
    public void Load_carbon_conserves_against_generator_emissions()
    {
        GridModel m = Radial("coal");
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        CarbonTracing.CarbonTraceResult c = CarbonTracing.Trace(m, r);

        // Sum of load CO2 == the traced generation to loads * intensity. For a single coal source that is
        // (total load MW) * 0.937 tCO2/MWh (losses are not delivered to load, so not counted).
        // Moved 2026-10 (END-PASS CF4 row 4, end pass; NESO Carbon Intensity methodology May 2024 Table 1, coal 937
        // gCO2/kWh, R145 / R146, Ruling R1 and Mark's go on the end-pass moves 2026-10-11): coal is the NESO figure.
        // Before -> after: 70 x 0.91 = 63.7 -> 70 x 0.937 = 65.59 tCO2/h (observed: Actual 65.58999999751137, 1.95.273-275
        // runs); the conservation it tests and the tolerance (1 dp) unchanged.
        double totalLoadMw = 70.0;
        double expectedTph = totalLoadMw * 0.937;
        Assert.Equal(expectedTph, c.TotalTonnesPerHour, 1);
    }

    [Fact]
    public void The_fuel_classifier_orders_specific_fuels_before_generic_gas()
    {
        // Moved 2026-10 (END-PASS CF4 row 5, end pass; NESO Carbon Intensity methodology May 2024 Table 1, direct
        // combustion, R145 / R146, Ruling R1 and Mark's go on the end-pass moves 2026-10-11): the classifier now reads the
        // one sourced table (generic gas takes the CCGT figure; interconnectors 0 under direct accounting). Before -> after:
        // Coal 0.91 -> 0.937 (observed: Actual 0.93700000000000006, 1.95.273-275 runs); "CCGT gas" 0.36 -> 0.394; OCGT 0.44
        // -> 0.651; "gas" 0.40 -> 0.394; "interconnector" 0.20 -> 0.0 (these four not reached in the failing run; the same
        // calls give exactly these values in Cf4CarbonTableTests.The_moved_figures_are_exact, green in those runs). With
        // CCGT and generic gas now one figure, the value no longer shows the order this test is named for, so the
        // classification itself is asserted as well (derived from FuelTypes.Keywords: "ccgt" precedes "gas").
        Assert.Equal(0.0, FuelCarbon.IntensityTPerMwh("nuclear"));
        Assert.Equal(0.937, FuelCarbon.IntensityTPerMwh("Coal"));
        Assert.Equal(0.394, FuelCarbon.IntensityTPerMwh("CCGT gas"));   // CCGT before generic gas
        Assert.Equal(FuelKind.Ccgt, FuelTypes.Classify("CCGT gas", FuelReader.Economics));   // the order itself
        Assert.Equal(0.651, FuelCarbon.IntensityTPerMwh("OCGT"));
        Assert.Equal(0.394, FuelCarbon.IntensityTPerMwh("gas"));        // generic gas
        Assert.Equal(0.0, FuelCarbon.IntensityTPerMwh("wind-offshore"));
        Assert.Equal(0.0, FuelCarbon.IntensityTPerMwh("interconnector"));
        Assert.Equal(FuelCarbon.DefaultTPerMwh, FuelCarbon.IntensityTPerMwh(""));   // empty -> fallback
    }
}
