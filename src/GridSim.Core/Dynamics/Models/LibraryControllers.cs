namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// What one step of a library exciter (<see cref="IRmsExciterModel"/>) or stabiliser (<see cref="IRmsStabiliser"/>)
/// reads: the start-of-step values the RMS kernel measures, held across the step. A value the kernel did not supply on
/// this path is NaN; a model that needs it refuses the step rather than read NaN.
/// </summary>
/// <param name="SpeedDeviationPu">Rotor speed deviation (pu of synchronous speed).</param>
/// <param name="FieldCurrentPu">Field current IFD in the non-reciprocal Xad base (equal to Efd at every equilibrium).</param>
/// <param name="TerminalVoltagePu">Terminal voltage magnitude |Vt| (pu).</param>
/// <param name="ElectricalPowerPu">Electrical power Re(Vt conj(It)) on the machine base (pu).</param>
public readonly record struct FieldControlInputs(double SpeedDeviationPu, double FieldCurrentPu, double TerminalVoltagePu, double ElectricalPowerPu);

/// <summary>
/// An excitation-system model of the standard library that <see cref="RmsExciter.FromModel"/> wraps (ESST1A, EXST1, SCRX,
/// ESDC1A). The wrapper routes the stabiliser output and the measurements; the model owns its blocks.
/// </summary>
public interface IRmsExciterModel
{
    /// <summary>The PSS/E model name, for reports and errors.</summary>
    string Kind { get; }

    /// <summary>True when a step reads the terminal voltage (a bus-fed ceiling, a voltage transducer lag); the kernel
    /// then supplies the terminal phasors.</summary>
    bool NeedsTerminalQuantities { get; }

    /// <summary>Pre-load the states at the equilibrium with field <paramref name="fieldVoltagePu"/> (field current equal
    /// to it). A state the limits cannot hold fails loud. A check that needs the terminal voltage runs on the first step.</summary>
    void Initialize(double fieldVoltagePu);

    /// <summary>The steady regulator error Vref - Vt that holds field <paramref name="fieldVoltagePu"/>.</summary>
    double EquilibriumErrorPu(double fieldVoltagePu);

    /// <summary>One step: regulator error <paramref name="voltageErrorPu"/> (Vref - Vt), stabiliser output
    /// <paramref name="stabiliserPu"/> (added where the model's diagram adds it) and the measurements; returns Efd.</summary>
    double Step(double voltageErrorPu, double stabiliserPu, FieldControlInputs inputs, double dtSeconds);
}

/// <summary>
/// A power-system stabiliser of the standard library (IEEEST, PSS2A, PSS2B) that <see cref="RmsExciter"/> adds to the
/// AVR. Its states are set on the first step from that step's inputs (the undisturbed operating point at t = 0), so a
/// stabiliser on electrical power starts with every washout at rest and an output of exactly 0.
/// </summary>
public interface IRmsStabiliser
{
    /// <summary>The PSS/E model name.</summary>
    string Kind { get; }

    /// <summary>True when a step reads the electrical power or the terminal voltage.</summary>
    bool NeedsTerminalQuantities { get; }

    /// <summary>RC5 (2.1.122): true when the first step sets a state from a network measurement: an input on electrical
    /// power or terminal voltage (its washouts and filters start at that step's value), or an output logic that reads the
    /// first step's power. A memoryless check such as the IEEEST voltage cut-off reads the terminal voltage on every step
    /// but sets no state, so it does not count, and an input on rotor speed reads a state that no network change moves at
    /// t = 0. The kernel refuses a stabiliser for which this is true when a disturbance is in force at t = 0. The default
    /// (a stabiliser that does not say) is <see cref="NeedsTerminalQuantities"/>.</summary>
    bool ReadsNetworkInputsAtStart => NeedsTerminalQuantities;

    /// <summary>Reset: the next step sets every state from its inputs.</summary>
    void Initialize();

    /// <summary>One step on the start-of-step inputs; returns the stabiliser output Vs (pu).</summary>
    double Step(FieldControlInputs inputs, double dtSeconds);
}

/// <summary>
/// The voltage transducer lag 1/(1 + sTR) of the library exciters: the regulator compares Vref with the filtered
/// terminal voltage Vc. The kernel passes Vref - Vt and |Vt|, so the error is (Vref - Vt) + (Vt - Vc). TR = 0 passes the
/// kernel's error through untouched. The lag starts at the first measured |Vt|, so a flat start stays flat.
/// </summary>
internal sealed class VoltageTransducer
{
    private readonly FirstOrderLag? _lag;
    private readonly string _who;
    private bool _pending = true;

    internal VoltageTransducer(double trSeconds, string who)
    {
        if (!(trSeconds >= 0) || !double.IsFinite(trSeconds))
            throw new ArgumentOutOfRangeException(nameof(trSeconds), trSeconds, $"{who}: TR must be a finite number of seconds, 0 or above.");
        _lag = trSeconds > 0 ? new FirstOrderLag(1.0, trSeconds) : null;
        _who = who;
    }

    /// <summary>True when TR is above 0 (the step then needs |Vt|).</summary>
    internal bool Active => _lag is not null;

    internal void Initialize() => _pending = true;

    internal double Error(double voltageErrorPu, double terminalVoltagePu, double dtSeconds)
    {
        if (_lag is null) return voltageErrorPu;
        if (!double.IsFinite(terminalVoltagePu))
            throw new InvalidOperationException($"{_who}: the voltage transducer (TR above 0) needs the terminal voltage; the caller must use the step that supplies it.");
        if (_pending) { _lag.Initialize(terminalVoltagePu); _pending = false; }
        double vc = _lag.Step(terminalVoltagePu, dtSeconds);
        return voltageErrorPu + (terminalVoltagePu - vc);
    }
}

/// <summary>Shared checks of the library models.</summary>
internal static class LibraryChecks
{
    internal static void NonNegative(string who, string name, double v)
    {
        if (!(v >= 0) || !double.IsFinite(v)) throw new ArgumentOutOfRangeException(name, v, $"{who}: {name} must be a finite number, 0 or above.");
    }

    internal static void Positive(string who, string name, double v)
    {
        if (!(v > 0) || !double.IsFinite(v)) throw new ArgumentOutOfRangeException(name, v, $"{who}: {name} must be a finite number above 0.");
    }

    internal static void Limits(string who, string minName, double min, string maxName, double max)
    {
        if (double.IsNaN(min) || double.IsNaN(max) || !(max >= min))
            throw new ArgumentException($"{who}: {maxName} must be at least {minName} (got {minName} = {min}, {maxName} = {max}).", maxName);
    }

    /// <summary>A lead-lag stage (1 + s lead)/(1 + s lag): both 0 bypass it (null, exactly unity); a lead with no lag is an
    /// improper pure derivative and is refused naming the parameters.</summary>
    internal static LeadLag? Stage(string who, double lead, string leadName, double lag, string lagName)
    {
        NonNegative(who, leadName, lead);
        NonNegative(who, lagName, lag);
        if (lag == 0.0)
        {
            if (lead == 0.0) return null;
            throw new ArgumentOutOfRangeException(lagName, lag, $"{who}: {lagName} = 0 with {leadName} = {lead} is a pure derivative (improper); set both to 0 to bypass the stage.");
        }
        return new LeadLag(lead, lag);
    }
}
