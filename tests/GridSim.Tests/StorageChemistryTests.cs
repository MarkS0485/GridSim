using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Storage;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The chemistry-first battery model (<see cref="CellModel"/>, <see cref="PackModel"/>, <see cref="AgeingModel"/>,
/// <see cref="BatteryChemistryLibrary"/>). The oracles are physical: rate capability emerges from the equivalent
/// circuit (delivered charge falls with rate), the RC pairs relax on their time constants, energy balances,
/// heat scales with I^2, chemistries differ where they should (pulse vs sustained power, cold), rainflow matches
/// the ASTM E1049 worked example, and calendar fade integrates consistently through time.
/// </summary>
public class StorageChemistryTests
{
    private static double DeliveredAhToCutoff(ChemistryParameters chem, double cRate)
    {
        var cell = new CellModel(chem, chem.SocMax);
        double i = cRate * chem.CapacityAh, ah = 0, dt = 1.0;
        for (int k = 0; k < 400_000; k++)
        {
            if (cell.TerminalVoltage(i) <= chem.VMin || cell.Soc <= 0.0) break;
            cell.Step(i, dt);
            cell.TemperatureK = 298.15;          // isothermal: isolate the electrical rate effect
            ah += i * dt / 3600.0;
        }
        return ah;
    }

    public static IEnumerable<object[]> Chemistries => BatteryChemistryLibrary.All.Select(c => new object[] { c.Name });

    [Theory]
    [MemberData(nameof(Chemistries))]
    public void Delivered_charge_falls_with_discharge_rate(string name)
    {
        ChemistryParameters chem = BatteryChemistryLibrary.ByName(name);
        double slow = DeliveredAhToCutoff(chem, 0.05);
        double fast = DeliveredAhToCutoff(chem, chem.MaxDischargeCRate);
        Assert.True(fast < slow, $"{name}: {fast:F1} Ah at max rate vs {slow:F1} Ah at C/20");
        Assert.True(slow > 0.85 * chem.CapacityAh * chem.SocMax, $"{name}: slow discharge delivers {slow:F1} Ah");
    }

    [Fact]
    public void Rc_pairs_relax_on_their_time_constants()
    {
        ChemistryParameters chem = BatteryChemistryLibrary.Nmc;
        var cell = new CellModel(chem, 0.5);
        for (int k = 0; k < 600; k++) { cell.Step(64.0, 1.0); cell.TemperatureK = 298.15; }
        double v1 = cell.V1, v2 = cell.V2;
        cell.Step(0.0, chem.Tau1);                      // one fast time constant at rest
        Assert.Equal(v1 * Math.Exp(-1.0), cell.V1, 12);
        Assert.Equal(v2 * Math.Exp(-chem.Tau1 / chem.Tau2), cell.V2, 12);
    }

    [Fact]
    public void Energy_balance_closes_over_a_discharge()
    {
        // OCV x current = terminal power + dissipated heat + change of the RC capacitors' stored energy.
        ChemistryParameters chem = BatteryChemistryLibrary.Lfp with { EntropicCoefficient = LookupTable.Constant(0.0) };
        var cell = new CellModel(chem, 0.9);
        double i = 140.0, dt = 0.1, eOcv = 0, eOut = 0, heat = 0;
        double C1 = chem.Tau1 / chem.R1, C2 = chem.Tau2 / chem.R2;
        for (int k = 0; k < 18_000; k++)
        {
            double ocvStart = cell.Ocv, vt = cell.TerminalVoltage(i);
            double v1a = cell.V1, v2a = cell.V2;
            heat += cell.Step(i, dt);
            cell.TemperatureK = 298.15;
            double ocvEnd = cell.Ocv;
            eOcv += 0.5 * (ocvStart + ocvEnd) * i * dt;
            double vtEnd = ocvEnd - i * cell.R0 - cell.V1 - cell.V2;
            eOut += 0.5 * (vt + vtEnd) * i * dt;
            _ = v1a + v2a;
        }
        double stored = 0.5 * C1 * cell.V1 * cell.V1 + 0.5 * C2 * cell.V2 * cell.V2;
        double residual = eOcv - (eOut + heat + stored);
        Assert.True(Math.Abs(residual) < 1e-3 * eOcv, $"residual {residual:E2} J of {eOcv:E3} J");
    }

    [Fact]
    public void Cell_heat_scales_with_current_squared()
    {
        ChemistryParameters chem = BatteryChemistryLibrary.Nmc with
        {
            EntropicCoefficient = LookupTable.Constant(0.0), PassiveCoolingWPerK = 0.0,
            Auxiliary = new AuxiliaryModel(HvacCop: 0),
        };
        double Rise(double cRate)
        {
            var pack = new PackModel(chem, 100, 1, 0.8, ambientC: 25, trackAgeing: false);
            double v = pack.OpenCircuitVoltage;
            for (int k = 0; k < 60; k++) pack.Step(cRate * chem.CapacityAh * v, 1.0);
            return pack.TemperatureC - 25.0;
        }
        double ratio = Rise(2.0) / Rise(1.0);
        Assert.InRange(ratio, 3.6, 4.2);                // I^2: 4x, less a little because V (and so I at P) shifts
    }

    [Fact]
    public void Pulse_power_exceeds_sustained_power_and_nmc_out_powers_lfp_on_both()
    {
        PackModel Build(ChemistryParameters c) => PackModel.Sized(c, energyWh: 1e6, dcVoltage: 1000, initialSoc: 0.8);
        PackModel lfp = Build(BatteryChemistryLibrary.Lfp), nmc = Build(BatteryChemistryLibrary.Nmc);
        (double lfpPulse, _) = lfp.StateOfPower(10);
        (double lfpHalfHour, _) = lfp.StateOfPower(1800);
        (double nmcPulse, _) = nmc.StateOfPower(10);
        (double nmcHalfHour, _) = nmc.StateOfPower(1800);
        Assert.True(lfpPulse > lfpHalfHour && nmcPulse > nmcHalfHour);
        Assert.True(nmcPulse > 1.5 * lfpPulse, $"pulse: NMC {nmcPulse / 1e6:F2} MW vs LFP {lfpPulse / 1e6:F2} MW");
        Assert.True(nmcHalfHour > lfpHalfHour, $"30 min: NMC {nmcHalfHour / 1e6:F2} MW vs LFP {lfpHalfHour / 1e6:F2} MW");
        // A 30-minute delivery can never exceed the energy above the SoC floor.
        Assert.True(nmcHalfHour * 0.5 <= nmc.AvailableEnergyWh * 1.02);
    }

    [Fact]
    public void Sodium_ion_keeps_more_power_in_the_cold_than_nmc()
    {
        double ColdFraction(ChemistryParameters c)
        {
            PackModel warm = PackModel.Sized(c, 1e6, 1000, 0.8, ambientC: 25);
            PackModel cold = PackModel.Sized(c, 1e6, 1000, 0.8, ambientC: -10);
            return cold.StateOfPower(10).DischargeW / warm.StateOfPower(10).DischargeW;
        }
        Assert.True(ColdFraction(BatteryChemistryLibrary.SodiumIon) > ColdFraction(BatteryChemistryLibrary.Nmc));
    }

    [Fact]
    public void Lead_acid_loses_capacity_at_rate_far_more_than_lfp()
    {
        double LeadRatio = DeliveredAhToCutoff(BatteryChemistryLibrary.LeadAcid, 0.3) / DeliveredAhToCutoff(BatteryChemistryLibrary.LeadAcid, 0.05);
        double LfpRatio = DeliveredAhToCutoff(BatteryChemistryLibrary.Lfp, 1.0) / DeliveredAhToCutoff(BatteryChemistryLibrary.Lfp, 0.05);
        Assert.True(LeadRatio < LfpRatio - 0.05, $"lead-acid {LeadRatio:F3} vs LFP {LfpRatio:F3}");
    }

    [Fact]
    public void Charging_is_blocked_below_the_minimum_charge_temperature()
    {
        PackModel pack = PackModel.Sized(BatteryChemistryLibrary.Nmc, 1e6, 1000, 0.5, ambientC: -5);
        PackStepResult r = pack.Step(-200_000, 1.0);
        Assert.True(r.Limited);
        Assert.Equal("too cold to charge", r.LimitReason);
        Assert.Equal(0.0, r.DcPowerW, 6);
    }

    [Fact]
    public void Flow_battery_pumps_draw_a_share_of_the_power_moved()
    {
        PackModel vrfb = PackModel.Sized(BatteryChemistryLibrary.VanadiumFlow, 1e6, 700, 0.5, ratedPowerW: 250_000);
        PackStepResult r = vrfb.Step(200_000, 1.0);
        Assert.False(r.Limited);
        double pumps = r.AuxiliaryPowerW - 0.003 * 250_000;
        Assert.Equal(0.04 * 200_000, pumps, 0);
    }

    [Fact]
    public void Rainflow_matches_the_astm_e1049_example()
    {
        // ASTM E1049-85 Fig. 6: ranges 3 (0.5), 4 (1.5), 6 (0.5), 8 (1.0), 9 (0.5).
        List<(double Depth, double Count)> cycles = Rainflow.Count(new double[] { -2, 1, -3, 5, -1, 3, -4, 4, -2 });
        var byRange = cycles.GroupBy(c => c.Depth).ToDictionary(g => g.Key, g => g.Sum(c => c.Count));
        Assert.Equal(0.5, byRange[3]);
        Assert.Equal(1.5, byRange[4]);
        Assert.Equal(0.5, byRange[6]);
        Assert.Equal(1.0, byRange[8]);
        Assert.Equal(0.5, byRange[9]);
    }

    [Fact]
    public void Calendar_fade_integrates_consistently_and_accelerates_with_heat()
    {
        ChemistryParameters chem = BatteryChemistryLibrary.Lfp;
        double Fade(double tempC, int steps)
        {
            var cell = new CellModel(chem, 0.5, tempC);
            var ageing = new AgeingModel(chem.Ageing);
            double dt = 365.0 * 86400.0 / steps;
            for (int k = 0; k < steps; k++) ageing.Advance(cell, 0.0, dt);
            return ageing.CalendarFade;
        }
        Assert.Equal(Fade(25, 1), Fade(25, 365), 10);                                      // step-size independent
        Assert.Equal(chem.Ageing.CalendarFadePerSqrtDay * Math.Sqrt(365.0), Fade(25, 1), 12); // k sqrt(days) at 25 C, 50 %
        Assert.True(Fade(40, 12) > 2.0 * Fade(25, 12));                                     // Arrhenius
    }

    [Fact]
    public void Presets_are_curated_placeholders_with_verify_notes()
    {
        Assert.All(BatteryChemistryLibrary.All, c =>
        {
            Assert.Equal(GridSim.Core.Diagnostics.ProvenanceTag.CuratedPlaceholder, c.Provenance);
            Assert.Contains("[VERIFY]", c.ProvenanceNote);
        });
    }

    /// <summary>CodeQL cs/loss-of-precision (2026-10-31): the pack's cell count (series x parallel) is an int product,
    /// so a pack whose count passes int.MaxValue must be refused, never built with a wrapped count. Pins both sides of
    /// the gate: the largest legal count builds and reports the exact product; one cell more throws.</summary>
    [Fact]
    public void Pack_cell_count_above_int_max_is_refused_not_wrapped()
    {
        ChemistryParameters chem = BatteryChemistryLibrary.All.First();
        // 2^31 - 1 = 2147483647 = 7 x 306783378 + 1, so 1 x int.MaxValue is the exact edge on one side ...
        var edge = new PackModel(chem, 1, int.MaxValue, chem.SocMax, trackAgeing: false);
        Assert.Equal(int.MaxValue, (long)edge.SeriesCells * edge.ParallelStrings);
        // ... and 2 x 2^30 = 2^31 is the first count that would wrap to int.MinValue.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new PackModel(chem, 2, 1 << 30, chem.SocMax, trackAgeing: false));
        Assert.Equal("parallelStrings", ex.ParamName);
        Assert.Contains("must fit in a 32-bit integer", ex.Message, StringComparison.Ordinal);
        // A product far past the limit (where int multiplication would wrap to a small positive count) is refused too.
        Assert.Throws<ArgumentOutOfRangeException>(() => new PackModel(chem, 65_536, 65_537, chem.SocMax, trackAgeing: false));
    }
}
