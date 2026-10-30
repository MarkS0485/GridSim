using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Diagnostics;

namespace GridSim.Core.Storage;

/// <summary>
/// Preset cell parameter sets for the storage chemistries a grid study meets. Each is a <b>representative</b>
/// cell for its class - typical published open-circuit-voltage shapes, impedances, rate and temperature limits,
/// thermal mass and life - not any one product. They are therefore tagged
/// <see cref="ProvenanceTag.CuratedPlaceholder"/> with a <c>[VERIFY]</c> note: fit for comparing chemistries and
/// exercising the models, to be replaced by a specific cell's datasheet (or fitted test data) before a result
/// is quoted for a real asset. Values per cell; packs are built with <see cref="PackModel"/>.
/// </summary>
public static class BatteryChemistryLibrary
{
    private static readonly double[] SocGrid = { 0.0, 0.05, 0.10, 0.20, 0.30, 0.40, 0.50, 0.60, 0.70, 0.80, 0.90, 0.95, 1.0 };

    private static LookupTable Ocv(params double[] v) => new(SocGrid, v);

    /// <summary>Lithium iron phosphate (LFP / graphite), large prismatic ESS cell (~280 Ah). A very flat OCV
    /// plateau (SoC is hard to read from voltage), low impedance per Ah, the longest cycle life of the lithium
    /// chemistries, good thermal stability. Rate-limited to ~0.5-1C in ESS duty.</summary>
    public static ChemistryParameters Lfp { get; } = new()
    {
        Name = "LFP",
        CapacityAh = 280,
        Ocv = Ocv(2.50, 3.00, 3.17, 3.22, 3.25, 3.27, 3.29, 3.30, 3.31, 3.33, 3.34, 3.36, 3.45),
        EntropicCoefficient = LookupTable.Constant(-0.05e-3),
        R0 = new LookupTable(new[] { 0.0, 0.1, 0.5, 0.9, 1.0 }, new[] { 0.45e-3, 0.30e-3, 0.25e-3, 0.27e-3, 0.35e-3 }),
        R1 = 0.12e-3, Tau1 = 10, R2 = 0.18e-3, Tau2 = 400,
        ResistanceActivationEnergy = 32_000,
        CoulombicEfficiency = 0.995, SelfDischargePerMonth = 0.03,
        VMin = 2.5, VMax = 3.65, MaxDischargeCRate = 1.0, MaxChargeCRate = 0.5,
        SocMin = 0.05, SocMax = 0.95, TDerateC = 45, TMaxC = 55, TMinChargeC = 0,
        MassKg = 5.4, SpecificHeat = 1100, PassiveCoolingWPerK = 0.08,
        Ageing = new AgeingParameters(CalendarFadePerSqrtDay: 0.0010, CyclesTo80Percent: 6000, CRateSensitivity: 0.25),
        Auxiliary = new AuxiliaryModel(StandbyFraction: 0.002, HvacCop: 3.0, HvacSetpointC: 30, HvacCapacityFraction: 0.05),
        ProvenanceNote = "[VERIFY] representative LFP 280 Ah ESS cell - typical published OCV/impedance/life; replace with a datasheet",
    };

    /// <summary>Lithium nickel manganese cobalt oxide (NMC / graphite), ~64 Ah ESS/automotive-derived cell.
    /// Higher and sloped OCV (SoC readable from voltage), higher energy density and power capability than LFP,
    /// shorter cycle life, more temperature-sensitive.</summary>
    public static ChemistryParameters Nmc { get; } = new()
    {
        Name = "NMC",
        CapacityAh = 64,
        Ocv = Ocv(3.00, 3.45, 3.55, 3.62, 3.66, 3.72, 3.78, 3.86, 3.94, 4.02, 4.10, 4.14, 4.20),
        EntropicCoefficient = LookupTable.Constant(-0.10e-3),
        R0 = new LookupTable(new[] { 0.0, 0.1, 0.5, 0.9, 1.0 }, new[] { 1.6e-3, 1.1e-3, 0.95e-3, 1.0e-3, 1.2e-3 }),
        R1 = 0.55e-3, Tau1 = 8, R2 = 0.75e-3, Tau2 = 300,
        ResistanceActivationEnergy = 35_000,
        CoulombicEfficiency = 0.998, SelfDischargePerMonth = 0.02,
        VMin = 3.0, VMax = 4.2, MaxDischargeCRate = 2.0, MaxChargeCRate = 1.0,
        SocMin = 0.05, SocMax = 0.95, TDerateC = 40, TMaxC = 50, TMinChargeC = 5,
        MassKg = 0.9, SpecificHeat = 1000, PassiveCoolingWPerK = 0.02,
        Ageing = new AgeingParameters(CalendarFadePerSqrtDay: 0.0016, CalendarActivationEnergy: 55_000,
            CyclesTo80Percent: 3500, CRateSensitivity: 0.35),
        Auxiliary = new AuxiliaryModel(StandbyFraction: 0.002, HvacCop: 3.0, HvacSetpointC: 28, HvacCapacityFraction: 0.06),
        ProvenanceNote = "[VERIFY] representative NMC 64 Ah cell - typical published OCV/impedance/life; replace with a datasheet",
    };

    /// <summary>Lithium titanate anode (LTO), ~30 Ah. Low voltage, very high rate capability (fast frequency
    /// response duty), very long cycle life, wide temperature window, low energy density.</summary>
    public static ChemistryParameters Lto { get; } = new()
    {
        Name = "LTO",
        CapacityAh = 30,
        Ocv = Ocv(1.50, 2.00, 2.15, 2.22, 2.25, 2.28, 2.30, 2.33, 2.36, 2.40, 2.46, 2.52, 2.80),
        R0 = LookupTable.Constant(0.8e-3),
        R1 = 0.25e-3, Tau1 = 5, R2 = 0.3e-3, Tau2 = 150,
        ResistanceActivationEnergy = 25_000,
        CoulombicEfficiency = 0.999, SelfDischargePerMonth = 0.01,
        VMin = 1.5, VMax = 2.8, MaxDischargeCRate = 6.0, MaxChargeCRate = 6.0,
        SocMin = 0.03, SocMax = 0.97, TDerateC = 50, TMaxC = 60, TMinChargeC = -20,
        MassKg = 1.0, SpecificHeat = 1000, PassiveCoolingWPerK = 0.03,
        Ageing = new AgeingParameters(CalendarFadePerSqrtDay: 0.0005, CyclesTo80Percent: 20000, CRateSensitivity: 0.05),
        Auxiliary = new AuxiliaryModel(StandbyFraction: 0.002, HvacCop: 3.0, HvacSetpointC: 35, HvacCapacityFraction: 0.08),
        ProvenanceNote = "[VERIFY] representative LTO 30 Ah cell - typical published OCV/impedance/life; replace with a datasheet",
    };

    /// <summary>Sodium-ion (layered oxide / hard carbon), ~160 Ah. Sloped OCV, LFP-like energy density, better
    /// low-temperature behaviour (lower activation energy), can be stored at 0 V; cycle life still maturing.</summary>
    public static ChemistryParameters SodiumIon { get; } = new()
    {
        Name = "Na-ion",
        CapacityAh = 160,
        Ocv = Ocv(2.00, 2.50, 2.70, 2.85, 2.95, 3.03, 3.10, 3.18, 3.27, 3.38, 3.52, 3.62, 3.90),
        R0 = new LookupTable(new[] { 0.0, 0.1, 0.5, 0.9, 1.0 }, new[] { 0.9e-3, 0.65e-3, 0.55e-3, 0.6e-3, 0.75e-3 }),
        R1 = 0.3e-3, Tau1 = 8, R2 = 0.4e-3, Tau2 = 300,
        ResistanceActivationEnergy = 20_000,
        CoulombicEfficiency = 0.99, SelfDischargePerMonth = 0.03,
        VMin = 2.0, VMax = 3.95, MaxDischargeCRate = 2.0, MaxChargeCRate = 1.0,
        SocMin = 0.03, SocMax = 0.97, TDerateC = 45, TMaxC = 55, TMinChargeC = -10,
        MassKg = 3.0, SpecificHeat = 1050, PassiveCoolingWPerK = 0.06,
        Ageing = new AgeingParameters(CalendarFadePerSqrtDay: 0.0014, CyclesTo80Percent: 4000, CRateSensitivity: 0.3),
        Auxiliary = new AuxiliaryModel(StandbyFraction: 0.002, HvacCop: 3.0, HvacSetpointC: 30, HvacCapacityFraction: 0.05),
        ProvenanceNote = "[VERIFY] representative Na-ion 160 Ah cell - early-market published figures; replace with a datasheet",
    };

    /// <summary>
    /// All-vanadium redox flow battery, per stack cell with energy set by the electrolyte (here a 1000 Ah
    /// equivalent tank per cell string). OCV from the Nernst equation <c>E = 1.40 + (2RT/F) ln(SoC/(1-SoC))</c>;
    /// energy and power are decoupled (tank vs stack), SoC window ~15-85 %, electrolyte pumps are a real
    /// parasitic load, cycle life is essentially unlimited, and the large electrolyte mass makes it thermally slow.
    /// </summary>
    public static ChemistryParameters VanadiumFlow { get; } = new()
    {
        Name = "VRFB",
        CapacityAh = 1000,
        Ocv = NernstOcv(1.40),
        R0 = LookupTable.Constant(1.2e-3),
        R1 = 0.3e-3, Tau1 = 2, R2 = 0.2e-3, Tau2 = 60,
        ResistanceActivationEnergy = 15_000,
        CoulombicEfficiency = 0.97, SelfDischargePerMonth = 0.02,
        VMin = 1.10, VMax = 1.65, MaxDischargeCRate = 0.25, MaxChargeCRate = 0.25,
        SocMin = 0.15, SocMax = 0.85, TDerateC = 38, TMaxC = 45, TMinChargeC = 5,
        MassKg = 45, SpecificHeat = 3000, PassiveCoolingWPerK = 0.4,
        Ageing = new AgeingParameters(CalendarFadePerSqrtDay: 0.0002, CyclesTo80Percent: 20000, CRateSensitivity: 0.0),
        Auxiliary = new AuxiliaryModel(StandbyFraction: 0.003, HvacCop: 0, PumpFraction: 0.04),
        ProvenanceNote = "[VERIFY] representative VRFB stack cell with a 1000 Ah electrolyte share - Nernst OCV, typical ASR; replace with vendor data",
    };

    /// <summary>Valve-regulated lead-acid, 2 V / ~1000 Ah cell. Near-linear OCV, strong rate effect (Peukert),
    /// low coulombic efficiency, short cycle life at deep DoD, heavy.</summary>
    public static ChemistryParameters LeadAcid { get; } = new()
    {
        Name = "Lead-acid",
        CapacityAh = 1000,
        Ocv = new LookupTable(new[] { 0.0, 1.0 }, new[] { 1.95, 2.13 }),
        R0 = new LookupTable(new[] { 0.0, 0.2, 1.0 }, new[] { 0.6e-3, 0.35e-3, 0.3e-3 }),
        R1 = 0.2e-3, Tau1 = 30, R2 = 0.3e-3, Tau2 = 1200,
        ResistanceActivationEnergy = 25_000,
        CoulombicEfficiency = 0.90, SelfDischargePerMonth = 0.03,
        PeukertExponent = 1.15, PeukertReferenceCRate = 0.1,
        VMin = 1.75, VMax = 2.40, MaxDischargeCRate = 0.3, MaxChargeCRate = 0.2,
        SocMin = 0.2, SocMax = 0.98, TDerateC = 40, TMaxC = 50, TMinChargeC = -10,
        MassKg = 60, SpecificHeat = 800, PassiveCoolingWPerK = 0.5,
        Ageing = new AgeingParameters(CalendarFadePerSqrtDay: 0.0025, CyclesTo80Percent: 1200, CRateSensitivity: 0.3, DodExponent: 1.6),
        Auxiliary = new AuxiliaryModel(StandbyFraction: 0.001, HvacCop: 0),
        ProvenanceNote = "[VERIFY] representative 2 V VRLA 1000 Ah cell - typical published curves; replace with a datasheet",
    };

    /// <summary>All presets.</summary>
    public static IReadOnlyList<ChemistryParameters> All { get; } = new[] { Lfp, Nmc, Lto, SodiumIon, VanadiumFlow, LeadAcid };

    /// <summary>A preset by name (case-insensitive: lfp, nmc, lto, na-ion / sodium-ion, vrfb / flow, lead-acid).</summary>
    public static ChemistryParameters ByName(string name)
    {
        string n = (name ?? "").Trim().ToLowerInvariant();
        return n switch
        {
            "sodium-ion" or "naion" or "na-ion" or "sib" => SodiumIon,
            "flow" or "vanadium" or "vrfb" => VanadiumFlow,
            "lead" or "lead-acid" or "vrla" => LeadAcid,
            _ => All.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                 ?? throw new ArgumentException($"unknown chemistry '{name}' (lfp, nmc, lto, na-ion, vrfb, lead-acid)", nameof(name)),
        };
    }

    private static LookupTable NernstOcv(double e0)
    {
        const double twoRtOverF = 2.0 * ChemistryParameters.GasConstant * ChemistryParameters.TRefK / 96485.332;
        double[] soc = { 0.0, 0.02, 0.05, 0.10, 0.15, 0.25, 0.35, 0.50, 0.65, 0.75, 0.85, 0.90, 0.95, 0.98, 1.0 };
        double[] v = soc.Select(s => e0 + twoRtOverF * DetMath.Log(Math.Clamp(s, 0.01, 0.99) / (1.0 - Math.Clamp(s, 0.01, 0.99)))).ToArray();
        return new LookupTable(soc, v);
    }
}
