using GridSim.Core.Presentation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Pins the shared <c>GridSim.Core.Presentation</c> presenters to the agreed constants. These are the SINGLE
/// source of truth both the WPF view-models and the web projections read (Phase 0 anti-drift extraction), so a
/// change here is a change to BOTH twins at once - this suite makes any such change deliberate and visible.
/// </summary>
public sealed class CorePresenterConstantsTests
{
    // ---- EnergyEconomicsConstants (THE MACHINE) ----
    [Fact]
    public void Bess_IsFiveHundredMw_OneGwh()
    {
        Assert.Equal(500.0, EnergyEconomicsConstants.BessCapMw);
        Assert.Equal(1000.0, EnergyEconomicsConstants.BessCapMwh);
    }

    [Fact]
    public void RegenPrice_IsEightyPoundsPerMwh()
        => Assert.Equal(80.0, EnergyEconomicsConstants.RegenPricePerMwh);

    [Fact]
    public void GovernorDroop_IsFourPercent()
        => Assert.Equal(0.04, EnergyEconomicsConstants.GovernorDroopFraction);

    // ---- CredibleLossBasis (NESO RP1 / OPERATOR) ----
    [Fact]
    public void DesignBasis_IsThreeThousandSixHundredMw_DoggerBank()
    {
        Assert.Equal(3600.0, CredibleLossBasis.DesignBasisLossMw);
        Assert.Equal("Dogger Bank A+B+C", CredibleLossBasis.DesignBasisSource);
    }

    [Fact]
    public void Fiction_IsEighteenHundredMw()
        => Assert.Equal(1800.0, CredibleLossBasis.FictionLossMw);

    // ---- ScadaThresholds (SCADA console) ----
    [Fact]
    public void ScadaThresholds_AreTheAgreedCutoffs()
    {
        Assert.Equal(275.0, ScadaThresholds.EhvCutoffKv);
        Assert.Equal(90.0, ScadaThresholds.CorridorAlarmPct);
        Assert.Equal(70.0, ScadaThresholds.CorridorWatchPct);
        Assert.Equal(0.94, ScadaThresholds.VoltageBandLowPu);
        Assert.Equal(1.06, ScadaThresholds.VoltageBandHighPu);
        Assert.Equal(49.9, ScadaThresholds.FreqOperationalLowHz);
        Assert.Equal(50.1, ScadaThresholds.FreqOperationalHighHz);
    }

    // ---- MechanicalThresholds (MECHANICAL) ----
    [Fact]
    public void MechanicalListing_StartsAtTenMw()
        => Assert.Equal(10.0, MechanicalThresholds.MinSyncMachineMw);

    // ---- CarbonFactors: grid intensity (kg CO2 / MWh) ----
    [Theory]
    [InlineData("Coal", 937.0)]
    [InlineData("CCGT", 394.0)]
    [InlineData("Gas", 394.0)]
    [InlineData("OCGT", 651.0)]
    [InlineData("Biomass", 120.0)]
    public void CarbonIntensity_TableEntries(string fuel, double kgPerMwh)
        => Assert.Equal(kgPerMwh, CarbonFactors.IntensityKgPerMwh(fuel, isFossil: true));

    [Fact]
    public void CarbonIntensity_UnknownFossilFallsBackTo450_NonFossilToZero()
    {
        // Moved 2026-10 (END-PASS CF4 row 2, end pass; NESO "Carbon Intensity Regional Forecast Methodology", May 2024,
        // Table 1, "Other" 300 gCO2/kWh, R145 / R146, Ruling R1 and Mark's go on the end-pass moves 2026-10-11): the 450
        // had no source; an unclassified fossil plant now takes the NESO "Other" figure. The name keeps "450" so existing
        // references resolve. Before -> after: 450.0 -> 300.0 kg/MWh (observed: Expected 450, Actual 300, 1.95.273-275
        // runs); the non-fossil 0 unchanged.
        Assert.Equal(300.0, CarbonFactors.IntensityKgPerMwh("mystery", isFossil: true));
        Assert.Equal(0.0, CarbonFactors.IntensityKgPerMwh("Wind (onshore)", isFossil: false));
    }

    // ---- CarbonFactors: whole-plant combustion (t CO2 / MWh) ----
    // Moved 2026-10 (END-PASS CF4 row 1, end pass; NESO "Carbon Intensity Regional Forecast Methodology", May 2024,
    // Table 1 (direct combustion: CCGT 394, OCGT 651, coal 937 gCO2/kWh), R145 / R146, Ruling R1 and Mark's go on the
    // end-pass moves 2026-10-11): the MACHINE view reads the one sourced table instead of its own unsourced set. Before ->
    // after: CCGT 0.35 -> 0.394, OCGT 0.49 -> 0.651, Coal 0.95 -> 0.937 t/MWh (observed: Actual 0.39400000000000002,
    // 0.65100000000000002, 0.93700000000000006, 1.95.273-275 runs).
    [Theory]
    [InlineData("CCGT", 0.394)]
    [InlineData("OCGT", 0.651)]
    [InlineData("Coal", 0.937)]
    public void CombustionRate_TableEntries(string fuel, double tonnesPerMwh)
        => Assert.Equal(tonnesPerMwh, CarbonFactors.CombustionTonnesPerMwh(fuel));

    [Fact]
    public void CombustionRate_NonThermalIsZero()
    {
        Assert.Equal(0.0, CarbonFactors.CombustionTonnesPerMwh("Nuclear"));
        Assert.Equal(0.0, CarbonFactors.CombustionTonnesPerMwh("Wind (offshore)"));
    }
}
