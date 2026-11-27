using System.Numerics;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// WP3b (RC5): a dynamic load model attached to a bus of an RMS study. It takes <see cref="PMw"/> + j<see cref="QMvar"/> of
/// the bus's load (the part of <c>Bus.Pd</c>, <c>Bus.Qd</c> the RMS network would otherwise hold as a constant admittance at
/// the solved voltage); the rest of the bus load stays a constant admittance. Attach a list of them to a study through
/// <see cref="RmsLoadedMachineData"/>. The three models are <see cref="InductionMotorLoadData"/> (third-order motor, PSS/E
/// CIM5 and CIMW single cage), <see cref="ZipRecoveryLoadData"/> (static ZIP with exponential recovery) and
/// <see cref="CompositeLoadData"/> (a documented subset of the WECC composite load CMLD).
/// </summary>
/// <param name="BusId">The bus the load is on.</param>
/// <param name="PMw">The active power the model takes at the solved voltage (MW, consumed positive); above 0.</param>
/// <param name="QMvar">The reactive power it takes (Mvar, consumed positive).</param>
public abstract record DynamicLoadData(int BusId, double PMw, double QMvar)
{
    /// <summary>The model name, for traces and errors.</summary>
    public abstract string Model { get; }

    /// <summary>The trace name; null names the load by model and bus.</summary>
    public string? Name { get; init; }

    /// <summary>Initialise at the solved bus voltage <paramref name="v0"/> (system pu): the plan holds the initial states
    /// and the Norton admittance; every run takes fresh states from it.</summary>
    internal abstract DynamicLoadPlan Plan(Complex v0, double baseMva, double nominalHz);

    internal string Label => Name ?? $"{Model} load @ bus {BusId}";
}

/// <summary>
/// WP3b (RC5): the machine data of an RMS study (aligned to <c>model.Generators</c>, exactly as the list it wraps) with the
/// dynamic loads to attach. Pass it as the <c>data</c> argument of any RMS study entry point (Study, StudyEvents, the CCT
/// searches); <see cref="SubtransientInit.FromSolvedCase"/> reads the loads from it. With no loads, a study given the plain
/// list is exactly the historical study.
/// </summary>
public sealed class RmsLoadedMachineData : IReadOnlyList<SubtransientInit.MachineData>
{
    private readonly IReadOnlyList<SubtransientInit.MachineData> _machines;

    public RmsLoadedMachineData(IReadOnlyList<SubtransientInit.MachineData> machines, IReadOnlyList<DynamicLoadData> loads)
    {
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(loads);
        for (int i = 0; i < loads.Count; i++)
            if (loads[i] is null) throw new ArgumentException($"loads[{i}] is null.", nameof(loads));
        _machines = machines;
        Loads = loads.ToArray();
    }

    /// <summary>
    /// WP6 (RC5): the machine data with dynamic loads and network device models (<see cref="RmsNetworkDeviceData"/>: an SVC,
    /// a STATCOM, an LCC or a VSC link of the case, each bound by name). The devices join the network after the dynamic
    /// loads, in list order. With an empty device list the study is the one the two-argument constructor gives.
    /// </summary>
    public RmsLoadedMachineData(IReadOnlyList<SubtransientInit.MachineData> machines, IReadOnlyList<DynamicLoadData> loads,
        IReadOnlyList<RmsNetworkDeviceData> devices) : this(machines, loads)
    {
        ArgumentNullException.ThrowIfNull(devices);
        for (int i = 0; i < devices.Count; i++)
            if (devices[i] is null) throw new ArgumentException($"devices[{i}] is null.", nameof(devices));
        Devices = devices.ToArray();
    }

    /// <summary>The dynamic loads, in the order they join the network.</summary>
    public IReadOnlyList<DynamicLoadData> Loads { get; }

    /// <summary>WP6 (RC5): the network device models, in the order they join the network (empty by default: every HVDC link,
    /// FACTS device and compensator is left out of the RMS network, as it always has been).</summary>
    public IReadOnlyList<RmsNetworkDeviceData> Devices { get; } = Array.Empty<RmsNetworkDeviceData>();

    public SubtransientInit.MachineData this[int index] => _machines[index];

    public int Count => _machines.Count;

    public IEnumerator<SubtransientInit.MachineData> GetEnumerator() => _machines.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>The initialised form of a dynamic load: its Norton admittance at the bus (system pu, stamped in the network) and
/// a factory of fresh run states.</summary>
internal abstract class DynamicLoadPlan
{
    internal abstract Complex NortonAdmittance { get; }

    internal abstract IRmsConverterModel CreateModel();
}

/// <summary>
/// The kernel side of a dynamic load (<see cref="IRmsConverterModel"/>): its bus is a converter node, its Norton admittance
/// <c>Y</c> is in the network, and it injects <c>Y V - I(V)</c>, where <c>I(V)</c> is the current the load draws. The kernel's
/// converter traces carry it as an injection, so its P and Q there are the load's consumption with the sign reversed.
/// </summary>
internal abstract class DynamicLoadState : IRmsConverterModel
{
    private readonly Complex _y;
    private double _ip, _iq;

    protected DynamicLoadState(Complex nortonAdmittance) => _y = nortonAdmittance;

    /// <summary>The current the load draws at <paramref name="v"/> (system pu), its states held.</summary>
    protected abstract Complex Drawn(Complex v);

    /// <summary>Advance the states one step with the terminal voltage <paramref name="v"/> held.</summary>
    protected abstract void Advance(Complex v, double dt);

    public Complex TrialCurrent(Complex v) => _y * v - Drawn(v);

    public Complex Commit(Complex v)
    {
        Complex inj = TrialCurrent(v);
        Complex terminal = inj - _y * v;
        double mag = v.Magnitude;
        if (mag > 0.0)
        {
            Complex unit = v / mag;
            Complex rel = terminal * Complex.Conjugate(unit);
            _ip = rel.Real;
            _iq = -rel.Imaginary;
        }
        else { _ip = 0.0; _iq = 0.0; }
        return inj;
    }

    public Complex TerminalCurrent(Complex injection, Complex v) => injection - _y * v;

    /// <summary>Advance with the step's start time <paramref name="t"/> (a model with timed parts, such as the composite
    /// load's DER_A, reads it); the default ignores it.</summary>
    protected virtual void Advance(double t, Complex v, double dt) => Advance(v, dt);

    public void Step(double t, Complex v, Complex[] nodeVoltages, double dt) => Advance(t, v, dt);

    public double ActiveCurrent => _ip;

    public double ReactiveCurrent => _iq;

    public bool InDip => false;
}

/// <summary>Shared checks and helpers of the dynamic loads.</summary>
internal static class LoadChecks
{
    /// <summary>The refusal of a record value (named by its property).</summary>
    internal static ArgumentOutOfRangeException OutOfRange(string name, double value, string message) => new(name, value, message);

    internal static void Finite(string who, string name, double v)
    {
        if (!double.IsFinite(v)) throw new ArgumentOutOfRangeException(name, v, $"{who}: {name} must be a finite number.");
    }

    /// <summary>The low-voltage rule of the voltage-dependent terms: at or above <paramref name="vLow"/> the factor is 1 and
    /// the term is evaluated at |V|; below it the term is evaluated at <paramref name="vLow"/> and scaled by (|V|/vLow)^2, a
    /// constant impedance (as PSS/E's PQBRAK treats constant-power load), so the current falls to 0 with the voltage.</summary>
    internal static (double Evaluate, double Scale) LowVoltage(double v, double vLow)
        => v >= vLow ? (v, 1.0) : (vLow, (v / vLow) * (v / vLow));
}
