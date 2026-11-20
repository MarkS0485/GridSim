using GridSim.Core.Dynamics.Emt.Nodal;

namespace GridSim.Core.Dynamics.Emt.PowerElectronics;

// RC4 R4-17 (EMT depth B). The power-electronics elements run on the R4-16 nodal solver (new files; the incumbent EmtEngine and
// every R4-16 result are untouched). A valve is a two-terminal conductance of 1/Ron (on) or 1/Roff (off), the same two-state
// rule as the R4-16 NodalSwitch and the incumbent SwitchElement (defaults EmtConstants.DefaultSwitchOnResistancePu /
// DefaultSwitchOffResistancePu in per unit; in SI the caller states ohms). Its state is decided by the circuit, not by a
// scheduled event.

/// <summary>What decides when a <see cref="NodalValve"/> conducts.</summary>
public enum ValveKind
{
    /// <summary>Ideal diode: turns on when the forward voltage becomes positive, off when the forward current falls to zero.</summary>
    Diode,
    /// <summary>Thyristor: turns on when the forward voltage is positive AND the gate signal is high; off when the current falls to zero
    /// (a gate pulse alone does not hold it on). Forward is anode to cathode.</summary>
    Thyristor,
    /// <summary>A gated switch with an antiparallel diode (IGBT, MOSFET): on whenever the gate is high (in either direction: the
    /// transistor forward, the diode backward); with the gate low it conducts only through the antiparallel diode, that is, backward
    /// (cathode to anode) when the voltage reverses. Forward is the transistor direction, anode (collector) to cathode (emitter).</summary>
    GatedDiode,
}

/// <summary>
/// A self-switching power-electronic valve between an anode node and a cathode node. Positive current is anode to cathode.
/// <para><b>Switching rule (stated, deterministic).</b> A valve decides its state at the START of each step from the LAST
/// ACCEPTED voltage and current (the values at the end of the previous step) and the gate signal evaluated at the END time of the
/// step being taken. A device therefore changes state at the first step boundary after its condition is met: a switching instant
/// is quantised UP to the step grid, with an error of at most one step (and, for a current zero, at most one step of current
/// overshoot). There is no interpolation back to the exact instant. Every decision is a comparison of doubles produced in a fixed
/// order, so a run is bit-identical on every machine and at every thread count.</para>
/// <para>No reverse recovery, forward voltage drop or turn-on delay is modelled (an on valve is its <see cref="Ron"/>).</para>
/// </summary>
public sealed class NodalValve : NodalElement
{
    private readonly int _a, _k;
    private readonly Func<double, bool>? _gate;
    private bool _on, _byGate;
    private double _vak;
    private long _transitions;

    /// <summary>Create the valve. <paramref name="gate"/> is required for a thyristor and a gated diode (it may return a pulse train) and
    /// ignored for a diode.</summary>
    public NodalValve(string name, int anode, int cathode, ValveKind kind, double ron, double roff, Func<double, bool>? gate = null, bool initiallyOn = false) : base(name)
    {
        if (anode == cathode) throw new ArgumentException($"{name}: both terminals are the same node.");
        if (!(ron > 0) || !(roff > ron) || !double.IsFinite(roff)) throw new ArgumentException($"{name}: need 0 < Ron < Roff, both finite.");
        if (kind != ValveKind.Diode && gate is null) throw new ArgumentException($"{name}: a {kind} needs a gate function.");
        _a = anode; _k = cathode; Kind = kind; Ron = ron; Roff = roff; _gate = gate; InitiallyOn = initiallyOn;
        Reset();
    }

    /// <summary>The valve kind.</summary>
    public ValveKind Kind { get; }
    /// <summary>On resistance, ohms.</summary>
    public double Ron { get; }
    /// <summary>Off resistance, ohms.</summary>
    public double Roff { get; }
    /// <summary>The state the run starts in.</summary>
    public bool InitiallyOn { get; }
    /// <summary>The present state.</summary>
    public bool On => _on;
    /// <summary>Number of state changes since the start of the run.</summary>
    public long Transitions => _transitions;

    internal override bool HasSwitchLogic => true;

    internal override bool SwitchLogic(double t)
    {
        double i = _vak / (_on ? Ron : Roff);
        bool g = _gate is not null && _gate(t);
        bool next, byGate = false;
        switch (Kind)
        {
            case ValveKind.Diode: next = _on ? i > 0.0 : _vak > 0.0; break;
            case ValveKind.Thyristor: next = _on ? i > 0.0 : (_vak > 0.0 && g); break;
            default:
                // Gate high: on. Gate low: a valve that was on BY GATE turns off at once (its complementary valve takes the pole; holding it on
                // through its antiparallel diode would short the DC link for a step); one that is conducting through its diode stays on while the
                // current is backward; an off valve turns on through its diode when the voltage reverses.
                if (g) { next = true; byGate = true; }
                else if (_on) next = !_byGate && i < 0.0;
                else next = _vak < 0.0;
                break;
        }
        _byGate = byGate;
        if (next == _on) return false;
        _on = next;
        _transitions++;
        return true;
    }

    internal override void Configure(double dt, IntegrationRule rule) { }
    internal override void Reset() { _on = InitiallyOn; _byGate = false; _vak = 0.0; _transitions = 0; }
    internal override void StampMatrix(NodalStamper s) => s.AddConductance(_a, _k, 1.0 / (_on ? Ron : Roff));
    internal override void Accept(double t, double[] v) => _vak = V(v, _a) - V(v, _k);

    /// <summary>The current anode to cathode, amps.</summary>
    internal override double ProbeCurrent(int channel) => _vak / (_on ? Ron : Roff);

    /// <summary>1 when the valve is on, else 0.</summary>
    internal override double ProbeState() => _on ? 1.0 : 0.0;
}

/// <summary>Which model class a converter result came from. The class is named in <see cref="PowerElectronicsResult"/>.</summary>
public enum ConverterModelClass
{
    /// <summary>Individual valves switching: harmonics, device stress and commutation are represented.</summary>
    Switched,
    /// <summary>Average-value model: the converter is its fundamental (modulated) voltage source; no switching ripple.</summary>
    AverageValue,
}
