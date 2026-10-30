namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// A per-machine turbine-governor for the RMS transient-stability study (R061, governor part; October 2026 review,
/// stream B3). The study calls <see cref="Initialize"/> once per run with the machine's initial mechanical power (pu on
/// the MACHINE base), then <see cref="Step"/> once per integration step with the machine's start-of-step rotor speed
/// deviation (pu), and holds the returned mechanical power (pu, machine base) across the step's swing integration.
/// <see cref="Tgov1"/> and <see cref="HydroGovernor"/> implement it directly; <see cref="Ieeeg1"/> through
/// <see cref="Ieeeg1Data"/> (a tandem-compound unit: both shafts' power on the one machine).
/// </summary>
public interface IRmsGovernor
{
    /// <summary>Pre-load every state at the equilibrium that holds <paramref name="mechanicalPowerPu"/> (machine base)
    /// at rated speed. Must fail loud when that equilibrium lies outside the governor's limits (R252).</summary>
    void Initialize(double mechanicalPowerPu);

    /// <summary>Advance one step of <paramref name="dtSeconds"/> with speed deviation <paramref name="speedDeviationPu"/>
    /// (held across the step) and return the mechanical power (pu, machine base) to hold across it.</summary>
    double Step(double speedDeviationPu, double dtSeconds);
}

/// <summary>
/// The parameters of one machine's turbine-governor: immutable data (machine base, seconds), carried on
/// <see cref="SubtransientInit.MachineData.Governor"/> and turned into a FRESH stateful governor for every run by
/// <see cref="Create"/>, so a critical-clearing-time search that runs the study many times never leaks state from one
/// trial into the next. The three standard models are <see cref="Tgov1Data"/>, <see cref="Ieeeg1Data"/> and
/// <see cref="HydroGovernorData"/>. Their parameter names follow the model classes' constructors exactly.
/// </summary>
public abstract record GovernorData
{
    /// <summary>The model name, as the v2 governor kind spells it ("TGOV1", "IEEEG1", "HYGOV", "GAST").</summary>
    public abstract string Model { get; }

    /// <summary>A new governor with these parameters (constructor checks run here, so bad data fails loud).</summary>
    public abstract IRmsGovernor Create();
}

/// <summary>TGOV1 parameters (see <see cref="Tgov1"/>'s constructor; per-unit on the machine base).</summary>
public sealed record Tgov1Data(
    double DroopPu, double ValveTimeSeconds, double ReheatLeadSeconds, double ReheatLagSeconds,
    double TurbineDampingPu = 0.0, double ValveMinPu = 0.0, double ValveMaxPu = double.PositiveInfinity) : GovernorData
{
    /// <inheritdoc/>
    public override string Model => "TGOV1";

    /// <inheritdoc/>
    public override IRmsGovernor Create() => new Tgov1(DroopPu, ValveTimeSeconds, ReheatLeadSeconds, ReheatLagSeconds,
        TurbineDampingPu, ValveMinPu, ValveMaxPu);
}

/// <summary>IEEEG1 parameters (see <see cref="Ieeeg1"/>'s constructor; per-unit on the machine base). The RMS study
/// treats the unit as TANDEM-COMPOUND: one shaft carries both the HP and the LP output (the eight fractions sum to 1, so
/// the shaft holds the initial power at equilibrium). GridSim does not model cross-compound pairs (two machines on one
/// governor); their LP machine would need its own entry.</summary>
public sealed record Ieeeg1Data(
    double DroopPu, double GovernorLeadSeconds, double GovernorLagSeconds, double ValveTimeSeconds,
    double ChestSeconds, double ReheatSeconds, double CrossoverSeconds, double LpSeconds,
    double K1 = 1.0, double K3 = 0.0, double K5 = 0.0, double K7 = 0.0,
    double ValveMinPu = 0.0, double ValveMaxPu = double.PositiveInfinity,
    double K2 = 0.0, double K4 = 0.0, double K6 = 0.0, double K8 = 0.0,
    double ValveOpeningRatePuPerS = double.PositiveInfinity, double ValveClosingRatePuPerS = double.NegativeInfinity) : GovernorData
{
    /// <inheritdoc/>
    public override string Model => "IEEEG1";

    /// <inheritdoc/>
    public override IRmsGovernor Create() => new Tandem(new Ieeeg1(DroopPu, GovernorLeadSeconds, GovernorLagSeconds,
        ValveTimeSeconds, ChestSeconds, ReheatSeconds, CrossoverSeconds, LpSeconds, K1, K3, K5, K7, ValveMinPu, ValveMaxPu,
        K2, K4, K6, K8, ValveOpeningRatePuPerS, ValveClosingRatePuPerS));

    /// <summary>One shaft: the HP output plus the LP output of the same step. With the default fractions the LP output
    /// is exactly +0.0, so the shaft power is the HP output itself.</summary>
    private sealed class Tandem(Ieeeg1 g) : IRmsGovernor
    {
        public void Initialize(double mechanicalPowerPu) => g.Initialize(mechanicalPowerPu);

        public double Step(double speedDeviationPu, double dtSeconds)
        {
            double hp = g.Step(speedDeviationPu, dtSeconds);
            return hp + g.LowPressureShaftPowerPu;
        }
    }
}

/// <summary>GAST parameters (see <see cref="Gast"/>'s constructor; per-unit on the machine base): the simple gas-turbine
/// governor with its exhaust temperature load limit.</summary>
public sealed record GastData(
    double DroopPu, double ValveTimeSeconds, double TurbineTimeSeconds, double ExhaustTimeSeconds,
    double AmbientLimitPu, double TemperatureGainPu, double ValveMinPu = 0.0, double ValveMaxPu = double.PositiveInfinity,
    double TurbineDampingPu = 0.0) : GovernorData
{
    /// <inheritdoc/>
    public override string Model => "GAST";

    /// <inheritdoc/>
    public override IRmsGovernor Create() => new Gast(DroopPu, ValveTimeSeconds, TurbineTimeSeconds, ExhaustTimeSeconds,
        AmbientLimitPu, TemperatureGainPu, ValveMinPu, ValveMaxPu, TurbineDampingPu);
}

/// <summary>Hydro governor parameters (see <see cref="HydroGovernor"/>'s constructor: permanent and transient droop, the
/// dashpot reset time, the gate servo and the ideal water column; per-unit on the machine base). The v2 governor kind
/// names this model "HYGOV"; it is GridSim's classic transient-droop governor with the ideal (inelastic, lossless) water
/// column, which is the PSS/E HYGOV structure without HYGOV's turbine gain At, no-load flow qNL, turbine damping and
/// gate-velocity limit (those are not modelled; a data set that relies on them is approximated, see the notes).</summary>
public sealed record HydroGovernorData(
    double PermanentDroopPu, double TransientDroopPu, double ResetTimeSeconds, double GateTimeSeconds,
    double WaterTimeSeconds, double GateMinPu = 0.0, double GateMaxPu = double.PositiveInfinity) : GovernorData
{
    /// <inheritdoc/>
    public override string Model => "HYGOV";

    /// <inheritdoc/>
    public override IRmsGovernor Create() => new HydroGovernor(PermanentDroopPu, TransientDroopPu, ResetTimeSeconds,
        GateTimeSeconds, WaterTimeSeconds, GateMinPu, GateMaxPu);
}
