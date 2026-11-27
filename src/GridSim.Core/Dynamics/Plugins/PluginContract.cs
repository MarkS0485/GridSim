namespace GridSim.Core.Dynamics.Plugins;

/// <summary>
/// Marks an assembly as a GridSim model plug-in written against contract version <see cref="ContractVersion"/>. The loader
/// reads it from the assembly's metadata before anything is loaded; an assembly without it is refused.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class GridSimModelPluginAttribute(int contractVersion) : Attribute
{
    /// <summary>The contract version the plug-in was written against.</summary>
    public int ContractVersion { get; } = contractVersion;
}

/// <summary>The plug-in contract this build implements.</summary>
public static class PluginContract
{
    /// <summary>Contract version 1: exciter, governor, stabiliser, injection and load models. Results that record a contract
    /// version for the plug-in route as a whole name this one, as they always have.</summary>
    public const int Version = 1;

    /// <summary>RC5 WP32: contract version 2, which adds the machine (<see cref="IUserMachine"/>) and relay
    /// (<see cref="IUserRelay"/>) roles. Every contract 1 interface is unchanged in it, so a contract 1 plug-in loads exactly as
    /// before.</summary>
    public const int Version2 = 2;

    /// <summary>True for a contract version this build implements (1 and 2).</summary>
    public static bool IsSupported(int version) => version is Version or Version2;

    /// <summary>True when <paramref name="role"/> needs contract 2 (machine and relay).</summary>
    public static bool NeedsVersion2(UserModelRole role) => role is UserModelRole.Machine or UserModelRole.Relay;

    /// <summary>RC5 wp32b: true for the five contract 1 roles (exciter, governor, stabiliser, injection, load).</summary>
    internal static bool IsContract1Role(UserModelRole role) => role is UserModelRole.Exciter or UserModelRole.Governor
        or UserModelRole.Stabiliser or UserModelRole.Injection or UserModelRole.Load;

    /// <summary>RC5 wp32b: the role a contract 1 model is held with when its factory gives a number that is no contract 1 role
    /// (it matches no role, so every check refuses it as v2.1.105-RC-5 did).</summary>
    internal const UserModelRole NoContract1Role = (UserModelRole)(-1);

    /// <summary>The label every result that used a plug-in carries.</summary>
    public const string OutsideLabel = "OUTSIDE THE VERIFIED CONTRACT";
}

/// <summary>Where a user model plugs into the dynamics engine.</summary>
public enum UserModelRole
{
    /// <summary>Field controller: regulator error and speed deviation in, field voltage out (machine base).</summary>
    Exciter,
    /// <summary>Turbine-governor: speed deviation in, mechanical power out (machine base).</summary>
    Governor,
    /// <summary>Stabiliser: speed deviation, terminal voltage and electrical power in, stabilising signal out.</summary>
    Stabiliser,
    /// <summary>A generator-bound current source (system base currents).</summary>
    Injection,
    /// <summary>A bus load model (system base currents, drawn).</summary>
    Load,
    /// <summary>RC5 WP32 (contract 2): a generator-bound machine, a Norton source with a constant admittance
    /// (<see cref="IUserMachine"/>).</summary>
    Machine,
    /// <summary>RC5 WP32 (contract 2): a unit-bound relay that trips its unit (<see cref="IUserRelay"/>).</summary>
    Relay,
}

/// <summary>A parameter a plug-in model declares: name, unit text, default and optional inclusive bounds.</summary>
public sealed record PluginParameter(string Name, string Unit, double Default, double? Min, double? Max);

/// <summary>
/// One model of a plug-in. A plug-in assembly holds one public class with a public parameterless constructor per model.
/// </summary>
public interface IUserModelFactory
{
    /// <summary>The model id: 1 to 16 of A-Z, 0-9 and underscore; unique across the loaded plug-ins and the shipped library.</summary>
    string Id { get; }

    /// <summary>The role.</summary>
    UserModelRole Role { get; }

    /// <summary>The parameters, in the order <see cref="Create"/> takes them and reports list them.</summary>
    IReadOnlyList<PluginParameter> Parameters { get; }

    /// <summary>A fresh instance (one per machine per run) implementing the role's interface below.</summary>
    object Create(IReadOnlyList<double> parameters);
}

/// <summary>An exciter plug-in (machine base). The same contract as the kernel's field controller.</summary>
public interface IUserExciter
{
    /// <summary>Preset every state at the equilibrium that holds <paramref name="efd0"/>; throw when it cannot.</summary>
    void Initialize(double efd0);

    /// <summary>The steady regulator error that holds <paramref name="efd0"/>. Must not change the instance's states.</summary>
    double EquilibriumErrorPu(double efd0);

    /// <summary>One step with the regulator error and speed deviation held; returns the field voltage.</summary>
    double Step(double verr, double speedDev, double dt);
}

/// <summary>A governor plug-in (machine base).</summary>
public interface IUserGovernor
{
    /// <summary>Preset every state at the equilibrium that holds <paramref name="pm0"/> at rated speed.</summary>
    void Initialize(double pm0);

    /// <summary>One step with the speed deviation held; returns the mechanical power.</summary>
    double Step(double speedDev, double dt);
}

/// <summary>A stabiliser plug-in.</summary>
public interface IUserStabiliser
{
    /// <summary>True when a step reads the terminal voltage or the electrical power.</summary>
    bool NeedsTerminalQuantities { get; }

    /// <summary>Reset: the next step sets every state from its inputs.</summary>
    void Initialize();

    /// <summary>One step on the start-of-step inputs; returns the stabilising signal.</summary>
    double Step(double speedDev, double vt, double pe, double dt);
}

/// <summary>The operating point an injection or load model starts at (system per unit, network frame).</summary>
/// <param name="P0">Active power exported (injection) or drawn (load), system pu.</param>
/// <param name="Q0">Reactive power exported (injection) or drawn (load), system pu.</param>
/// <param name="V0Re">Real part of the solved terminal voltage.</param>
/// <param name="V0Im">Imaginary part of the solved terminal voltage.</param>
/// <param name="RatingPu">The unit's rating on the system base (MBASE / Sb for an injection, |S| / Sb for a load).</param>
public readonly record struct InjectionStart(double P0, double Q0, double V0Re, double V0Im, double RatingPu);

/// <summary>
/// A generator-bound current source (system per unit). The kernel calls <see cref="Current"/> many times per step inside its
/// network fixed point with the states held, <see cref="Commit"/> once at the converged voltage, and <see cref="Step"/> once
/// per step to advance the states from the start-of-step voltage.
/// </summary>
public interface IUserInjection
{
    /// <summary>A constant admittance stamped in the network at the bus to condition the kernel's fixed point; (0, 0) is a
    /// pure current source. It does not change the net current: the kernel injects <c>Current(v) + Y v</c> on top of the
    /// stamp <c>Y</c>, so the network always sees exactly <see cref="Current"/> (for a load, minus it).</summary>
    (double Re, double Im) NortonAdmittance { get; }

    /// <summary>Initialise so that the model reproduces P0 + jQ0 at V0 exactly.</summary>
    void Initialize(in InjectionStart s);

    /// <summary>The net current the unit injects at trial voltage v (system pu, network frame), states held: pure in v (the
    /// same states and v give the same bits).</summary>
    (double Re, double Im) Current(double vRe, double vIm);

    /// <summary>The converged voltage of the step.</summary>
    void Commit(double vRe, double vIm);

    /// <summary>Advance the states once per step from the start-of-step voltage.</summary>
    void Step(double t, double vRe, double vIm, double dt);

    /// <summary>Active current at the last commit (for traces).</summary>
    double ActiveCurrent { get; }

    /// <summary>Reactive current at the last commit, exporting positive (for traces).</summary>
    double ReactiveCurrent { get; }

    /// <summary>True while a voltage-dip logic of the model is active.</summary>
    bool InDip { get; }
}

/// <summary>A bus load plug-in: the shape of <see cref="IUserInjection"/>, but <see cref="IUserInjection.Current"/> is the
/// current the load DRAWS (the kernel injects Y v minus it, as for the coded dynamic loads).</summary>
public interface IUserLoad : IUserInjection
{
}

/// <summary>RC5 WP32 (contract 2): the operating point a machine model starts at (system per unit, network frame).</summary>
/// <param name="P0">Active power the unit exports in the power flow, system pu.</param>
/// <param name="Q0">Reactive power the unit exports in the power flow, system pu.</param>
/// <param name="V0Re">Real part of the solved terminal voltage.</param>
/// <param name="V0Im">Imaginary part of the solved terminal voltage.</param>
/// <param name="MbasePu">The unit's MBASE on the system base (MBASE / Sb).</param>
public readonly record struct MachineInit(double P0, double Q0, double V0Re, double V0Im, double MbasePu);

/// <summary>RC5 WP32 (contract 2): what a machine model reads for one step: the time at the start of the step and the terminal
/// voltage of that step's converged network solve (system pu, network frame).</summary>
public readonly record struct MachineStepInputs(double TimeSeconds, double VRe, double VIm);

/// <summary>
/// RC5 WP32 (contract 2): a generator-bound machine model, a Norton source with a constant admittance on the system base. The
/// kernel stamps <see cref="NortonAdmittancePu"/> once and injects <see cref="NortonCurrentPu"/> at the unit's bus, so the
/// current the unit exports is <c>I_N - Y v</c>. It reaches the network by the same route as an injection model (a unit on its
/// own bus, iterated with the network at each step), not by the built-in machines' route: the built-in machines' rotor swing is
/// integrated by the kernel itself (RK4 with the network re-solved at each sub-step), which no plug-in can join. A machine
/// model therefore owns its own rotor, field and mechanical power, takes no bound exciter or governor, and is not judged in the
/// synchronism verdict. Saturation or any other change of the machine's admittance must be carried in the Norton current.
/// </summary>
public interface IUserMachine
{
    /// <summary>The constant Norton admittance on the system base; (0, 0) is a pure current source.</summary>
    (double Re, double Im) NortonAdmittancePu { get; }

    /// <summary>Preset every state so that the machine exports P0 + jQ0 at V0 exactly (the kernel checks it).</summary>
    void Initialise(in MachineInit init);

    /// <summary>The Norton source current from the present states (system pu, network frame). It must not change the states:
    /// the same states give the same bits.</summary>
    (double Re, double Im) NortonCurrentPu { get; }

    /// <summary>Advance the states once by <paramref name="dt"/> from the start-of-step inputs.</summary>
    void Step(double dt, in MachineStepInputs inputs);

    /// <summary>Speed deviation, pu of synchronous speed.</summary>
    double SpeedPu { get; }

    /// <summary>Rotor angle in the network frame, rad.</summary>
    double AngleRad { get; }

    /// <summary>Field current, pu on the machine base (0 for a model without one).</summary>
    double FieldCurrentPu { get; }

    /// <summary>Electrical power, system pu.</summary>
    double ElectricalPowerPu { get; }
}

/// <summary>RC5 WP32 (contract 2): what a relay reads at one step, from that step's converged network solve. A relay bound to a
/// unit reads its machine; a relay bound to a branch (RC5 wp32b) reads one end of the branch, and a branch has no machine, so
/// its <see cref="FrequencyHz"/>, <see cref="FrequencyRateHzPerSecond"/> and <see cref="SpeedPu"/> are NaN (not a number:
/// nothing is measured, and nothing is put in its place).</summary>
/// <param name="StepIndex">The step number k (0 at t = 0). Timers count whole steps from it.</param>
/// <param name="TimeSeconds">The time of the step start, k h.</param>
/// <param name="StepSeconds">The fixed step h.</param>
/// <param name="VoltagePu">The magnitude of the unit's bus voltage, pu (a branch relay: of the bus at its measuring end).</param>
/// <param name="FrequencyHz">The machine's electrical frequency F0 (1 + w), Hz (the quantity the built-in 81 stages read); NaN
/// for a branch relay.</param>
/// <param name="FrequencyRateHzPerSecond">F0 dw/dt from the swing equation with the step's mechanical and electrical power (the
/// quantity the built-in 81R stage reads); NaN for a branch relay.</param>
/// <param name="ActivePowerPu">The unit's electrical active power, system pu (a branch relay: the active power flowing into the
/// branch at its measuring end).</param>
/// <param name="ReactivePowerPu">The unit's reactive power at its bus, system pu (a branch relay: the reactive power flowing into
/// the branch at its measuring end).</param>
/// <param name="SpeedPu">The machine's speed deviation w, pu; NaN for a branch relay.</param>
/// <param name="CurrentPu">RC5 wp32b: the magnitude of the current, system pu: the machine's current into the network (a unit
/// relay), or the current flowing into the branch at its measuring end (a branch relay).</param>
public readonly record struct RelayInputs(int StepIndex, double TimeSeconds, double StepSeconds, double VoltagePu, double FrequencyHz,
    double FrequencyRateHzPerSecond, double ActivePowerPu, double ReactivePowerPu, double SpeedPu, double CurrentPu = double.NaN);

/// <summary>RC5 WP32 (contract 2): a relay's decision at one step: no action, or a trip with the relay's own reason code.</summary>
public readonly record struct RelayDecision(bool Trip, int ReasonCode)
{
    /// <summary>No action.</summary>
    public static RelayDecision None => default;

    /// <summary>Trip the unit, giving <paramref name="reasonCode"/> (recorded with the trip).</summary>
    public static RelayDecision TripWith(int reasonCode) => new(true, reasonCode);
}

/// <summary>
/// RC5 WP32 (contract 2): a unit-bound relay. The kernel calls <see cref="Evaluate"/> once per step, at the step start, from that
/// step's converged network solve; a trip disconnects the unit from the next step on by the built-in unit relays' trip path
/// (network rebuilt without the machine, mechanical power 0, field held, not judged). A unit trips once and stays out.
/// RC5 wp32b: the same relay may instead be bound to a branch; its trip then opens the branch at both ends from the next step
/// on (the network rebuilt without it, as a scheduled branch trip), and the branch stays out.
/// </summary>
public interface IUserRelay
{
    /// <summary>Reset every state before the run.</summary>
    void Initialise();

    /// <summary>One step: the decision from this step's inputs.</summary>
    RelayDecision Evaluate(in RelayInputs inputs);
}
