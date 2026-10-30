namespace GridSim.Core.Presentation;

/// <summary>
/// The shared scalar assumptions behind the "THE MACHINE" presentation - the whole GB grid told as one
/// rotating machine. These are the numbers that were previously copy-pasted between the WPF <c>MachineVm</c>
/// and the web <c>MachineService</c> and so drifted apart. Both surfaces now read them from here so the
/// torque story, the banked-divergence regen valuation and the leaning BESS behave identically across twins.
///
/// Pure presentation constants: they set how the sim is narrated to the operator/layman, not the physics the
/// power-flow solves. Kept at the exact values both twins already used (no behaviour change on extraction).
/// </summary>
public static class EnergyEconomicsConstants
{
    // ---- the nominal grid battery: 500 MW / 1 GWh ----
    /// <summary>BESS power rating (MW) - the most it can charge or discharge.</summary>
    public const double BessCapMw = 500.0;
    /// <summary>BESS energy capacity (MWh) - a full charge-to-empty swing.</summary>
    public const double BessCapMwh = 1000.0;

    // ---- how the BESS leans against the frequency divergence ----
    /// <summary>Fraction of the instantaneous surplus/deficit MW the BESS answers (it charges off over-spin).</summary>
    public const double BessResponseFraction = 0.25;
    /// <summary>State-of-charge (%) above which the BESS stops charging (leaves headroom).</summary>
    public const double BessSocChargeCeilingPct = 95.0;
    /// <summary>State-of-charge (%) below which the BESS stops discharging (keeps a reserve).</summary>
    public const double BessSocDischargeFloorPct = 5.0;
    /// <summary>Power (MW) below which the BESS reads as STANDBY rather than CHARGING/DISCHARGING.</summary>
    public const double BessModeDeadbandMw = 10.0;

    // ---- the torque story ----
    /// <summary>Governor droop (4%): a frequency deviation of 4% of 50 Hz implies 100% of demand as surplus/deficit.</summary>
    public const double GovernorDroopFraction = 0.04;
    /// <summary>Stress = 100% when the torque divergence reaches this fraction (5%) of the commanded torque.</summary>
    public const double StressBand = 0.05;

    // ---- potential-missed-regen valuation (both stated as assumptions in the view) ----
    /// <summary>Indicative wholesale price (GBP/MWh) applied to the harvestable over-spin.</summary>
    public const double RegenPricePerMwh = 80.0;
    /// <summary>A home's daily draw (kWh), for the layman "homes powered for a day" figure.</summary>
    public const double HomeKwhPerDay = 8.0;
}
