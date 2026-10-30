using GridSim.Core.Model;

namespace GridSim.Core.Presentation;

/// <summary>
/// THE one table of per-fuel carbon-intensity figures in GridSim (October 2026, R145 / R146; ruled by main under
/// Ruling R1, CF4). Every carbon figure the app paints or computes - the MACHINE tab's stack total, the MARKET tab's
/// grid-intensity read and the experimental carbon tracing - reads ONE figure per fuel from here, so the same dispatch
/// gives the same carbon in every view, and no other file carries a per-fuel carbon literal.
///
/// <para><b>Source.</b> NESO (National Energy System Operator), "Carbon Intensity Regional Forecast Methodology",
/// issue May 2024, Table 1 "Fuel Type / Carbon Intensity gCO2/kWh", p.3; the same figures are
/// served by the NESO Carbon Intensity API at <c>https://api.carbonintensity.org.uk/intensity/factors</c> (read
/// 2026-10-11): Biomass 120, Coal 937, Gas (Combined Cycle) 394, Gas (Open Cycle) 651, Hydro 0, Nuclear 0, Oil 935,
/// Other 300, Solar 0, Wind 0, Pumped Storage 0. The methodology states (p.1) that the factors cover "CO2 emissions
/// related to electricity generation only" and "are based on the output-weighted average efficiency of generation in
/// GB and DUKES CO2 emission factors for fuels" (its refs [3], Energy Policy 102 (2017) pp.463-475,
/// doi 10.1016/j.enpol.2016.12.037, and [4], DUKES). That is the DIRECT-COMBUSTION intensity of the electricity generated: the fuel's DUKES combustion factor
/// divided by the GB fleet's measured efficiency, per MWh generated, with no upstream (mining, gas supply, plant
/// build) emissions.</para>
///
/// <para><b>Why this set.</b> (1) It is the one per-fuel set the GB system operator publishes for GB plant, so the
/// MARKET tab's indicative read is consistent with the measured NESO intensity it is replaced by whenever a lake tick
/// carries one. (2) It is a direct-combustion set (the basis main's Q8 judgement asked for). (3) It is the set the
/// MARKET tab already used (coal 937, CCGT 394, OCGT 651, biomass 120, other 300), which the review identified as the
/// only one of the three old sets with a traceable origin; the MACHINE (0.35 / 0.49 / 0.95) and tracing (0.36 / 0.44 /
/// 0.91 / 0.40 / 0.34 / 0.20 / 0.65) sets had none.</para>
///
/// <para><b>Mapping of GridSim's kinds onto the published fuel types</b> (choices recorded here, Ruling R1):</para>
/// <list type="bullet">
/// <item>Coal (and lignite) = Coal 937; CCGT = Gas (Combined Cycle) 394; OCGT = Gas (Open Cycle) 651.</item>
/// <item>Gas of unstated cycle = Gas (Combined Cycle): the GB gas fleet's output is overwhelmingly combined cycle, and
/// an unstated gas unit in GB data is a CCGT far more often than a peaker.</item>
/// <item>Gas CHP = Gas (Combined Cycle): the table has no CHP row, and the electricity-side efficiency of GB gas CHP is
/// closer to a CCGT's than to an open cycle's (the heat output is not counted against the electricity here).</item>
/// <item>Oil and reciprocating engines (diesel, oil engines) = Oil 935.</item>
/// <item>Biomass = Biomass 120. <b>The one entry that is not direct combustion</b>: the methodology's footnote i says it
/// is "consumption-based" accounting (120 +/- 120 gCO2/kWh, supply-chain emissions), because the stack CO2 of biomass
/// is biogenic and counted as zero by UK carbon accounting. It is kept as published so the whole table stays one
/// published set; it is named (<see cref="BiomassTPerMwh"/>) so a caller that wants pure stack CO2 can see it.</item>
/// <item>Other (and any unclassified fossil fuel) = Other 300.</item>
/// <item>Nuclear, wind, solar, hydro, tidal, pumped storage, batteries, hydrogen: 0 (no combustion).</item>
/// <item>Interconnectors and grid infeeds: 0. Their combustion happens outside the modelled network, so under direct
/// accounting it is not this network's. NESO's Table 1 does print approximate consumption-based import figures
/// (French ~53, Dutch ~474, Belgian ~179, Irish ~458 gCO2/kWh, marked "~" because the forecast weights each link by
/// the exporting country's live mix); they are not used here because they are consumption-based, not direct, and a
/// GridSim interconnector or GSP infeed does not carry its source country. A caller wanting consumption-based totals
/// applies them itself. The methodology PDF is docs/reference/carbon/S325-neso-ci-regional-methodology-v2.pdf.
/// (CF4-F2, N350.)</item>
/// </list>
///
/// <para><b>Units.</b> The table is held in t CO2 / MWh (the form the stack total and the tracing multiply MW by);
/// the kg CO2 / MWh form (numerically gCO2/kWh, as NESO publishes it) is DERIVED as 1000 x t. Each derived value is the
/// published whole number exactly: for a decimal t = d / 1000 the nearest double times 1000 lies within 1000 x half an
/// ulp of t, which is always under half an ulp of d (the ulp ratio is at most 1000 / 1024), so it rounds to d.
/// <c>Cf4CarbonTableTests</c> pins every one.</para>
///
/// <para><b>What moved</b> (R145 / R146, before -> after, t/MWh unless kg): MACHINE CCGT / Gas / CHP 0.35 -> 0.394,
/// OCGT 0.49 -> 0.651, coal 0.95 -> 0.937, biomass 0 -> 0.12, Other 0 -> 0.3; MARKET unknown fossil 450 -> 300 kg;
/// tracing coal 0.91 -> 0.937, OCGT 0.44 -> 0.651, CCGT 0.36 -> 0.394, CHP 0.34 -> 0.394, generic gas 0.40 -> 0.394,
/// oil / recip 0.65 -> 0.935, imports / infeed 0.20 -> 0, biomass 0.12 and unknown 0.30 unchanged. The old per-view
/// constant names below are kept ONLY as names (each is now the one figure of its fuel, not a separate literal),
/// because tests outside this change refer to them by name.</para>
/// </summary>
public static class CarbonIntensityTable
{
    /// <summary>The kg / t ratio used to derive the kg CO2 / MWh form (exact).</summary>
    public const double KgPerTonne = 1000.0;

    // ---- The table (t CO2 / MWh), NESO Carbon Intensity Regional Forecast Methodology, May 2024, Table 1 ----

    /// <summary>Coal (and lignite): NESO Table 1 "Coal" 937 gCO2/kWh.</summary>
    public const double CoalTPerMwh = 0.937;
    /// <summary>Combined-cycle gas turbine: NESO Table 1 "Gas (Combined Cycle)" 394 gCO2/kWh.</summary>
    public const double CcgtTPerMwh = 0.394;
    /// <summary>Open-cycle gas turbine: NESO Table 1 "Gas (Open Cycle)" 651 gCO2/kWh.</summary>
    public const double OcgtTPerMwh = 0.651;
    /// <summary>Oil, diesel and reciprocating engines: NESO Table 1 "Oil" 935 gCO2/kWh.</summary>
    public const double OilTPerMwh = 0.935;
    /// <summary>Biomass, biogas, landfill gas, energy from waste: NESO Table 1 "Biomass" 120 gCO2/kWh. Consumption-based
    /// (supply chain), NOT stack CO2: the methodology's footnote i (see the class summary).</summary>
    public const double BiomassTPerMwh = 0.120;
    /// <summary>The "Other" bucket and any unclassified fossil fuel: NESO Table 1 "Other" 300 gCO2/kWh.</summary>
    public const double OtherTPerMwh = 0.300;
    /// <summary>Gas of unstated cycle: the combined-cycle figure (see the class summary for why).</summary>
    public const double GasUnstatedTPerMwh = CcgtTPerMwh;
    /// <summary>Gas combined heat and power: the combined-cycle figure (see the class summary for why).</summary>
    public const double ChpTPerMwh = CcgtTPerMwh;
    /// <summary>Non-combusting plant (nuclear, wind, solar, hydro, tidal, storage, hydrogen): NESO Table 1 0.</summary>
    public const double ZeroTPerMwh = 0.0;
    /// <summary>Interconnectors and grid infeeds: 0 under direct accounting (combustion outside the network).</summary>
    public const double ImportTPerMwh = 0.0;

    // ---- The derived kg CO2 / MWh form (= gCO2/kWh as published) ----

    /// <summary>Coal, kg CO2 / MWh (derived: 937).</summary>
    public const double CoalKgPerMwh = CoalTPerMwh * KgPerTonne;
    /// <summary>CCGT, kg CO2 / MWh (derived: 394).</summary>
    public const double CcgtKgPerMwh = CcgtTPerMwh * KgPerTonne;
    /// <summary>OCGT, kg CO2 / MWh (derived: 651).</summary>
    public const double OcgtKgPerMwh = OcgtTPerMwh * KgPerTonne;
    /// <summary>Oil / reciprocating engines, kg CO2 / MWh (derived: 935).</summary>
    public const double OilKgPerMwh = OilTPerMwh * KgPerTonne;
    /// <summary>Biomass, kg CO2 / MWh (derived: 120).</summary>
    public const double BiomassKgPerMwh = BiomassTPerMwh * KgPerTonne;
    /// <summary>Other / unclassified fossil, kg CO2 / MWh (derived: 300).</summary>
    public const double OtherKgPerMwh = OtherTPerMwh * KgPerTonne;
    /// <summary>Gas of unstated cycle, kg CO2 / MWh (derived: 394).</summary>
    public const double GasUnstatedKgPerMwh = GasUnstatedTPerMwh * KgPerTonne;
    /// <summary>Gas CHP, kg CO2 / MWh (derived: 394).</summary>
    public const double ChpKgPerMwh = ChpTPerMwh * KgPerTonne;

    /// <summary>The one figure (t CO2 / MWh) for a fuel kind of Core's taxonomy (<see cref="FuelTypes"/>). Every
    /// consumer reads its figure through this (or through <see cref="KgPerMwh"/>). An unknown enum value throws.</summary>
    public static double TPerMwh(FuelKind kind) => kind switch
    {
        FuelKind.Nuclear or FuelKind.WindOnshore or FuelKind.WindOffshore or FuelKind.Solar or FuelKind.Hydro
            or FuelKind.Tidal or FuelKind.PumpedStorage or FuelKind.Storage or FuelKind.Hydrogen => ZeroTPerMwh,
        FuelKind.Coal => CoalTPerMwh,
        FuelKind.Ccgt => CcgtTPerMwh,
        FuelKind.Ocgt => OcgtTPerMwh,
        FuelKind.Gas => GasUnstatedTPerMwh,
        FuelKind.Chp => ChpTPerMwh,
        FuelKind.Biomass => BiomassTPerMwh,
        FuelKind.Recip => OilTPerMwh,
        FuelKind.Interconnector or FuelKind.GridInfeed => ImportTPerMwh,
        FuelKind.Other => OtherTPerMwh,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown fuel kind."),
    };

    /// <summary>The same figure in kg CO2 / MWh, read from the derived constants (never a separate number).</summary>
    public static double KgPerMwh(FuelKind kind) => kind switch
    {
        FuelKind.Coal => CoalKgPerMwh,
        FuelKind.Ccgt => CcgtKgPerMwh,
        FuelKind.Ocgt => OcgtKgPerMwh,
        FuelKind.Gas => GasUnstatedKgPerMwh,
        FuelKind.Chp => ChpKgPerMwh,
        FuelKind.Biomass => BiomassKgPerMwh,
        FuelKind.Recip => OilKgPerMwh,
        FuelKind.Other => OtherKgPerMwh,
        _ => TPerMwh(kind) * KgPerTonne,   // the zero rows (0 x 1000 = 0) and the unknown-kind refusal
    };

    // ---- Old per-view names (R145 / R146): names only, each the one figure of its fuel ----
    // Kept because tests written before the unification refer to them by name; none is a separate literal.

    /// <summary>Old MACHINE-tab name; now <see cref="CcgtTPerMwh"/>.</summary>
    public const double StackGasTPerMwh = CcgtTPerMwh;
    /// <summary>Old MACHINE-tab name; now <see cref="OcgtTPerMwh"/>.</summary>
    public const double StackOcgtTPerMwh = OcgtTPerMwh;
    /// <summary>Old MACHINE-tab name; now <see cref="CoalTPerMwh"/>.</summary>
    public const double StackCoalTPerMwh = CoalTPerMwh;
    /// <summary>Old MARKET-tab name; now <see cref="CoalKgPerMwh"/>.</summary>
    public const double GridCoalKgPerMwh = CoalKgPerMwh;
    /// <summary>Old MARKET-tab name; now <see cref="CcgtKgPerMwh"/>.</summary>
    public const double GridGasKgPerMwh = CcgtKgPerMwh;
    /// <summary>Old MARKET-tab name; now <see cref="OcgtKgPerMwh"/>.</summary>
    public const double GridOcgtKgPerMwh = OcgtKgPerMwh;
    /// <summary>Old MARKET-tab name; now <see cref="ChpKgPerMwh"/>.</summary>
    public const double GridChpKgPerMwh = ChpKgPerMwh;
    /// <summary>Old MARKET-tab name; now <see cref="BiomassKgPerMwh"/>.</summary>
    public const double GridBiomassKgPerMwh = BiomassKgPerMwh;
    /// <summary>Old MARKET-tab name; now <see cref="OtherKgPerMwh"/>.</summary>
    public const double GridOtherKgPerMwh = OtherKgPerMwh;
    /// <summary>Old MARKET-tab name for the unclassified-fossil fallback (was 450, unsourced); now
    /// <see cref="OtherKgPerMwh"/>, NESO's figure for unclassified generation.</summary>
    public const double GridUnknownFossilKgPerMwh = OtherKgPerMwh;
    /// <summary>Old tracing name; now <see cref="CoalTPerMwh"/>.</summary>
    public const double TraceCoalTPerMwh = CoalTPerMwh;
    /// <summary>Old tracing name; now <see cref="OcgtTPerMwh"/>.</summary>
    public const double TraceOcgtTPerMwh = OcgtTPerMwh;
    /// <summary>Old tracing name; now <see cref="CcgtTPerMwh"/>.</summary>
    public const double TraceCcgtTPerMwh = CcgtTPerMwh;
    /// <summary>Old tracing name; now <see cref="ChpTPerMwh"/>.</summary>
    public const double TraceChpTPerMwh = ChpTPerMwh;
    /// <summary>Old tracing name; now <see cref="BiomassTPerMwh"/>.</summary>
    public const double TraceBiomassTPerMwh = BiomassTPerMwh;
    /// <summary>Old tracing name; now <see cref="ImportTPerMwh"/>.</summary>
    public const double TraceImportTPerMwh = ImportTPerMwh;
    /// <summary>Old tracing name; now <see cref="OilTPerMwh"/>.</summary>
    public const double TraceOilTPerMwh = OilTPerMwh;
    /// <summary>Old tracing name; now <see cref="GasUnstatedTPerMwh"/>.</summary>
    public const double TraceGenericGasTPerMwh = GasUnstatedTPerMwh;
    /// <summary>Old tracing name; now <see cref="OtherTPerMwh"/>.</summary>
    public const double TraceUnknownTPerMwh = OtherTPerMwh;
}

/// <summary>
/// The per-fuel carbon factors the presentation layer paints, shared by both the WPF view-models and the web
/// session projections so the twins can never drift. Both forms read the ONE figure per fuel in
/// <see cref="CarbonIntensityTable"/> (R145 / R146, CF4): the MACHINE tab's t/MWh and the MARKET tab's kg/MWh of the
/// same fuel are the same number in two units.
/// Keys are fuel <i>names</i> (from the app-local <c>Fuels</c> taxonomy, the names <see cref="FuelTaxonomy"/> paints)
/// rather than the app-local <c>FuelInfo</c> struct, because that struct is defined separately in each UI project.
/// </summary>
public static class CarbonFactors
{
    /// <summary>
    /// The fuel kind behind a display name, or null for a name the table does not know. Only the names the shells
    /// paint (and "Gas", "Oil") are recognised; anything else is the caller's fall-back.
    /// </summary>
    private static FuelKind? KindOfName(string fuelName) => fuelName switch
    {
        "Coal" => FuelKind.Coal,
        "CCGT" => FuelKind.Ccgt,
        "Gas" => FuelKind.Gas,
        "OCGT" => FuelKind.Ocgt,
        "CHP" => FuelKind.Chp,
        "Biomass" => FuelKind.Biomass,
        "Oil" or "Reciprocating engine" => FuelKind.Recip,
        "Other" => FuelKind.Other,
        _ => null,   // nuclear / wind / solar / hydro / storage / interconnectors and unknown names
    };

    /// <summary>
    /// Carbon intensity (t CO2 / MWh) per fuel for the MACHINE tab's tCO2/h total: the table's figure for the
    /// combusting fuels and "Other"; 0 for every other name (nuclear, renewables, storage, interconnectors), so a
    /// flat blend over total generation cannot over-count.
    /// </summary>
    public static double CombustionTonnesPerMwh(string fuelName)
        => KindOfName(fuelName) is FuelKind k ? CarbonIntensityTable.TPerMwh(k) : 0.0;

    /// <summary>
    /// The same figure in kg CO2 / MWh for the MARKET tab's gCO2/kWh estimate. <paramref name="isFossil"/> only
    /// matters for a fuel name not in the table: an unrecognised fossil fuel takes the table's "Other" figure (NESO's
    /// unclassified generation; it was an unsourced 450 before CF4), everything else 0.
    /// </summary>
    public static double IntensityKgPerMwh(string fuelName, bool isFossil)
        => KindOfName(fuelName) is FuelKind k ? CarbonIntensityTable.KgPerMwh(k)
            : isFossil ? CarbonIntensityTable.OtherKgPerMwh : 0.0;
}
