using System;

namespace GridSim.Core.Storage;

/// <summary>
/// The equivalent-circuit state of one (representative) cell: state of charge, the two RC polarisation
/// voltages, temperature and accumulated ageing. Current is positive on discharge.
/// <code>
///   V_t   = OCV(SoC, T) - I R0(SoC, T) - V1 - V2
///   V_k'  = -V_k / tau_k + I R_k / tau_k          (k = 1, 2; exact zero-order hold per step)
///   SoC'  = -I_eff / (3600 Q)                      (I_eff: Peukert on discharge, coulombic efficiency on charge)
/// </code>
/// Resistances scale with temperature (Arrhenius) and with ageing (resistance growth); capacity with fade.
/// </summary>
public sealed class CellModel
{
    /// <summary>Length (days) of the "month" a chemistry's self-discharge rate is quoted per (R198).</summary>
    public const double SelfDischargeMonthDays = 30.0;

    public ChemistryParameters Chemistry { get; }

    /// <summary>State of charge (0..1, of the current - faded - capacity).</summary>
    public double Soc { get; private set; }
    public double V1 { get; private set; }
    public double V2 { get; private set; }
    /// <summary>Cell temperature (K).</summary>
    public double TemperatureK { get; internal set; }
    /// <summary>Capacity fade so far (fraction of rated capacity lost).</summary>
    public double CapacityFade { get; internal set; }

    public CellModel(ChemistryParameters chemistry, double soc, double temperatureC = 25.0)
    {
        Chemistry = chemistry ?? throw new ArgumentNullException(nameof(chemistry));
        Soc = Math.Clamp(soc, 0.0, 1.0);
        TemperatureK = temperatureC + ChemistryParameters.Kelvin0;
    }

    /// <summary>A copy with identical state (for look-ahead queries).</summary>
    public CellModel Clone() => new(Chemistry, Soc)
    {
        V1 = V1, V2 = V2, TemperatureK = TemperatureK, CapacityFade = CapacityFade,
    };

    public double TemperatureC => TemperatureK - ChemistryParameters.Kelvin0;

    /// <summary>Present capacity (Ah) after fade.</summary>
    public double CapacityAh => Chemistry.CapacityAh * (1.0 - CapacityFade);

    private double AgeingResistanceFactor => 1.0 + Chemistry.Ageing.ResistanceGrowthPerFade * CapacityFade;

    /// <summary>Open-circuit voltage at the present SoC and temperature.</summary>
    public double Ocv => Chemistry.Ocv[Soc] + Chemistry.EntropicCoefficient[Soc] * (TemperatureK - ChemistryParameters.TRefK);

    /// <summary>Present series resistance (Ω).</summary>
    public double R0 => Chemistry.R0[Soc] * Chemistry.ResistanceFactor(TemperatureK) * AgeingResistanceFactor;
    public double R1 => Chemistry.R1 * Chemistry.ResistanceFactor(TemperatureK) * AgeingResistanceFactor;
    public double R2 => Chemistry.R2 * Chemistry.ResistanceFactor(TemperatureK) * AgeingResistanceFactor;

    /// <summary>The Thevenin source behind R0 at the start of a step: <c>E = OCV - V1 - V2</c>.</summary>
    public double InternalEmf => Ocv - V1 - V2;

    /// <summary>Terminal voltage for a current <paramref name="current"/> (A) at the present state.</summary>
    public double TerminalVoltage(double current) => InternalEmf - current * R0;

    /// <summary>
    /// Advance the electrochemical state by <paramref name="dt"/> seconds at constant <paramref name="current"/>
    /// (A, discharge positive). Returns the heat generated (J): irreversible <c>I^2 R0 + V1^2/R1 + V2^2/R2</c>
    /// plus reversible entropic <c>I T dU/dT</c>. Temperature is advanced by the pack's thermal model.
    /// </summary>
    public double Step(double current, double dt)
    {
        ChemistryParameters c = Chemistry;
        double r0 = R0, r1 = R1, r2 = R2;

        // Heat over the step (trapezoid on the RC voltages).
        double a1 = DetMath.Exp(-dt / c.Tau1), a2 = DetMath.Exp(-dt / c.Tau2);
        double v1n = V1 * a1 + current * r1 * (1.0 - a1);
        double v2n = V2 * a2 + current * r2 * (1.0 - a2);
        double qIrr = current * current * r0 * dt
            + 0.5 * (V1 * V1 + v1n * v1n) / r1 * dt
            + 0.5 * (V2 * V2 + v2n * v2n) / r2 * dt;
        double qRev = current * TemperatureK * c.EntropicCoefficient[Soc] * dt;
        V1 = v1n; V2 = v2n;

        // Charge throughput.
        double iEff = current;
        if (current > 0 && c.PeukertExponent > 1.0)
        {
            double cRate = current / c.CapacityAh;
            if (cRate > c.PeukertReferenceCRate)
                iEff = current * DetMath.Pow(cRate / c.PeukertReferenceCRate, c.PeukertExponent - 1.0);
        }
        else if (current < 0)
        {
            iEff = current * c.CoulombicEfficiency;
        }
        double selfDischarge = c.SelfDischargePerMonth / (SelfDischargeMonthDays * AgeingModel.SecondsPerDay) * dt;
        Soc = Math.Clamp(Soc - iEff * dt / (3600.0 * CapacityAh) - selfDischarge, 0.0, 1.0);
        return qIrr + qRev;
    }
}
