using System;
using System.Collections.Generic;
using GridSim.Core.Diagnostics;

namespace GridSim.Core.Storage;

/// <summary>
/// A piecewise-linear table <c>y(x)</c> on ascending <c>x</c>, clamped at the ends - the open-circuit-voltage
/// curve, a resistance-vs-SoC curve.
/// </summary>
public sealed class LookupTable
{
    private readonly double[] _x, _y;

    public LookupTable(double[] x, double[] y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        if (x.Length != y.Length || x.Length < 2) throw new ArgumentException("A table needs at least two matching points.");
        for (int i = 1; i < x.Length; i++)
            if (!(x[i] > x[i - 1])) throw new ArgumentException("Table x must be strictly ascending.");
        _x = (double[])x.Clone(); _y = (double[])y.Clone();
    }

    public IReadOnlyList<double> X => _x;
    public IReadOnlyList<double> Y => _y;

    public double this[double x]
    {
        get
        {
            if (x <= _x[0]) return _y[0];
            if (x >= _x[^1]) return _y[^1];
            int i = Array.BinarySearch(_x, x);
            if (i >= 0) return _y[i];
            i = ~i;
            double f = (x - _x[i - 1]) / (_x[i] - _x[i - 1]);
            return _y[i - 1] + f * (_y[i] - _y[i - 1]);
        }
    }

    /// <summary>A constant table.</summary>
    public static LookupTable Constant(double y) => new(new[] { 0.0, 1.0 }, new[] { y, y });
}

/// <summary>
/// Auxiliary (balance-of-plant) electrical load of a storage system: thermal management for lithium systems
/// (HVAC removing heat at a coefficient of performance), electrolyte pumps for flow batteries, plus a constant
/// standby draw. Powers are per-unit of the pack's rated power so a preset scales with the system.
/// </summary>
/// <param name="StandbyFraction">Constant draw as a fraction of rated power (controls, BMS, idle fans).</param>
/// <param name="HvacCop">Cooling coefficient of performance: electrical power = heat removed / COP. 0 = no HVAC.</param>
/// <param name="HvacSetpointC">Cell temperature the HVAC holds the pack at or below (°C).</param>
/// <param name="HvacCapacityFraction">Maximum heat the HVAC removes, as a fraction of rated power.</param>
/// <param name="PumpFraction">Pump power while operating, as a fraction of the power being moved (flow batteries).</param>
public sealed record AuxiliaryModel(
    double StandbyFraction = 0.002, double HvacCop = 3.0, double HvacSetpointC = 30.0,
    double HvacCapacityFraction = 0.05, double PumpFraction = 0.0);

/// <summary>
/// Semi-empirical ageing coefficients: calendar fade <c>k_cal * Arrhenius(T) * (1 + a_SoC (SoC - 0.5)) * sqrt(days)</c>
/// and cycle fade of 20 % over <see cref="CyclesTo80Percent"/> equivalent full cycles at the reference condition,
/// scaled by temperature (Arrhenius), C-rate and depth of discharge (<c>DoD^DodExponent</c> per rainflow cycle).
/// Resistance grows with fade: <c>R = R_new (1 + ResistanceGrowthPerFade * fade)</c>.
/// </summary>
public sealed record AgeingParameters(
    double CalendarFadePerSqrtDay = 0.0012, double CalendarActivationEnergy = 50_000, double CalendarSocSensitivity = 0.6,
    double CyclesTo80Percent = 6000, double CycleActivationEnergy = 30_000, double CRateSensitivity = 0.3,
    double DodExponent = 1.3, double ResistanceGrowthPerFade = 2.5);

/// <summary>
/// One battery chemistry, at <b>cell</b> level: an equivalent-circuit model (open-circuit voltage, series
/// resistance and two RC polarisation pairs), its temperature dependence, efficiency losses, operating limits,
/// thermal mass, ageing coefficients and balance-of-plant. Everything the grid ultimately sees - how much power
/// a battery can deliver and for how long - follows from these: voltage sag under sustained current reaches the
/// cut-off sooner at high rate (rate capability), resistance rises in the cold (Arrhenius), and so on.
/// Currents: positive = discharge. Temperatures in °C at the interface, kelvin internally.
/// </summary>
public sealed record ChemistryParameters
{
    public required string Name { get; init; }
    /// <summary>Rated cell capacity (Ah) at the reference rate and 25 °C.</summary>
    public required double CapacityAh { get; init; }
    /// <summary>Open-circuit voltage vs SoC at 25 °C (V).</summary>
    public required LookupTable Ocv { get; init; }
    /// <summary>Entropic coefficient dOCV/dT (V/K) vs SoC - the reversible heat <c>I T dU/dT</c>.</summary>
    public LookupTable EntropicCoefficient { get; init; } = LookupTable.Constant(0.0);
    /// <summary>Series (ohmic) resistance vs SoC at 25 °C (Ω).</summary>
    public required LookupTable R0 { get; init; }
    /// <summary>Fast polarisation pair (Ω, s).</summary>
    public required double R1 { get; init; }
    public required double Tau1 { get; init; }
    /// <summary>Slow (diffusion) polarisation pair (Ω, s).</summary>
    public required double R2 { get; init; }
    public required double Tau2 { get; init; }
    /// <summary>Arrhenius activation energy for all resistances (J/mol): <c>R(T) = R25 exp(Ea/Rg (1/T - 1/298.15))</c>.</summary>
    public double ResistanceActivationEnergy { get; init; } = 30_000;
    /// <summary>Coulombic efficiency on charge (fraction of charge stored).</summary>
    public double CoulombicEfficiency { get; init; } = 0.998;
    /// <summary>Self-discharge (fraction of capacity per 30 days).</summary>
    public double SelfDischargePerMonth { get; init; } = 0.02;
    /// <summary>Peukert exponent (1 = none). Discharge above <see cref="PeukertReferenceCRate"/> depletes charge
    /// faster by <c>(C / Cref)^(k-1)</c> - the lead-acid rate effect not captured by resistance alone.</summary>
    public double PeukertExponent { get; init; } = 1.0;
    public double PeukertReferenceCRate { get; init; } = 0.1;
    public required double VMin { get; init; }
    public required double VMax { get; init; }
    /// <summary>Continuous current limits as C-rates (discharge, charge).</summary>
    public required double MaxDischargeCRate { get; init; }
    public required double MaxChargeCRate { get; init; }
    /// <summary>Usable SoC window the BMS enforces.</summary>
    public double SocMin { get; init; } = 0.05;
    public double SocMax { get; init; } = 0.95;
    /// <summary>Temperature above which power derates linearly to zero at <see cref="TMaxC"/> (°C).</summary>
    public double TDerateC { get; init; } = 45;
    public double TMaxC { get; init; } = 55;
    /// <summary>Temperature below which charging is not allowed (lithium plating) (°C).</summary>
    public double TMinChargeC { get; init; } = 0;
    /// <summary>Cell mass (kg) and specific heat (J/kg/K).</summary>
    public required double MassKg { get; init; }
    public double SpecificHeat { get; init; } = 1000;
    /// <summary>Heat transfer to ambient per cell (W/K) without active cooling.</summary>
    public double PassiveCoolingWPerK { get; init; } = 0.05;
    public AgeingParameters Ageing { get; init; } = new();
    public AuxiliaryModel Auxiliary { get; init; } = new();
    /// <summary>Where the numbers come from. Presets are <see cref="ProvenanceTag.CuratedPlaceholder"/> with a
    /// <c>[VERIFY]</c> note until replaced by a specific cell's datasheet.</summary>
    public ProvenanceTag Provenance { get; init; } = ProvenanceTag.CuratedPlaceholder;
    public string ProvenanceNote { get; init; } = "[VERIFY] unspecified";

    /// <summary>Nominal cell voltage (OCV at 50 % SoC, 25 °C).</summary>
    public double NominalVoltage => Ocv[0.5];

    /// <summary>Nominal cell energy (Wh) - capacity times the mean OCV over the SoC window.</summary>
    public double NominalEnergyWh
    {
        get
        {
            const int n = 100;
            double s = 0;
            for (int i = 0; i < n; i++) s += Ocv[SocMin + (SocMax - SocMin) * (i + 0.5) / n];
            return CapacityAh * (SocMax - SocMin) * s / n;
        }
    }

    internal const double GasConstant = 8.314462618;
    internal const double Kelvin0 = 273.15;
    internal const double TRefK = 298.15;

    /// <summary>Arrhenius resistance multiplier at <paramref name="tK"/> (1 at 25 °C).</summary>
    public double ResistanceFactor(double tK) =>
        DetMath.Exp(ResistanceActivationEnergy / GasConstant * (1.0 / tK - 1.0 / TRefK));
}
