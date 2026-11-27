using System.Numerics;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// WP6 (RC5): the initialised STATCOM1 (<see cref="Statcom1Data"/>) of one device: the power-flow reactive current, the
/// voltage reference and the integrator's initial value. Refuses a power-flow current beyond the limits.
/// </summary>
internal sealed class Statcom1Plan : RmsDevicePlan
{
    internal Statcom1Plan(Statcom1Data data, string name, double baseMva, double q0Pu, Complex v0)
    {
        ArgumentNullException.ThrowIfNull(data);
        data.Validate($"STATCOM1 '{name}'");
        Data = data;
        Name = name;
        MbasePu = data.MbaseMva / baseMva;
        V0 = Math.Max(v0.Magnitude, DeviceCurrents.MinVoltagePu);
        Iq0 = q0Pu / (V0 * MbasePu);
        if (!double.IsFinite(Iq0) || !(Iq0 <= data.ImaxCap && Iq0 >= -data.ImaxInd))
            throw new ArgumentException($"STATCOM1 '{name}': the power flow's {ControlBlocks.Num(q0Pu * baseMva)} Mvar needs a reactive current of "
                + $"{ControlBlocks.Num(Iq0)} pu of MBASE, outside its limits [-{ControlBlocks.Num(data.ImaxInd)}, {ControlBlocks.Num(data.ImaxCap)}].");
        if (data.Ki == 0)
        {
            // A proportional controller: the integrator holds 0 and the reference takes the offset.
            Vref = V0 + data.Xs * Iq0 + Iq0 / data.Kp;
            Integral0 = 0.0;
        }
        else
        {
            Vref = V0 + data.Xs * Iq0;
            Integral0 = Iq0 - data.Kp * (Vref - V0 - data.Xs * Iq0);
        }
    }

    internal Statcom1Data Data { get; }
    internal string Name { get; }
    internal double MbasePu { get; }
    internal double V0 { get; }
    internal double Iq0 { get; }
    internal double Vref { get; }
    internal double Integral0 { get; }

    internal override IRmsConverterModel CreateModel(IReadOnlyList<int> nodeBuses) => new Statcom1State(this);
}

/// <summary>
/// WP6 (RC5): the run state of STATCOM1. The integrator (forward Euler, exact for a held input, non-windup) and the current
/// lag (exact zero-order hold, non-windup) advance from the start-of-step voltage and current; within a step the device is a
/// phase-locked reactive current source <c>-j Iq (MBASE / Sb) V / |V|</c>. With Tc = 0 the current is algebraic in the
/// trial voltage: <c>Iq = clip((Kp (Vref - |V|) + xi) / (1 + Kp Xs))</c>.
/// </summary>
internal sealed class Statcom1State : IRmsConverterModel, IRmsDeviceStateRecorder
{
    private static readonly string[] States = { "iq_pu", "integrator_pu", "q_pu" };

    private readonly Statcom1Plan _p;
    private readonly Integrator _xi;
    private readonly FirstOrderLag? _lag;
    private double _ip, _iq;
    private readonly List<double[]> _samples = new();

    internal Statcom1State(Statcom1Plan plan)
    {
        _p = plan;
        Statcom1Data d = plan.Data;
        _xi = new Integrator(d.Ki, -d.ImaxInd, d.ImaxCap);
        _xi.Initialize(plan.Integral0);
        if (d.Tc > ControlBlocks.InstantThresholdSeconds)
        {
            _lag = new FirstOrderLag(1.0, d.Tc, -d.ImaxInd, d.ImaxCap);
            _lag.Initialize(plan.Iq0);
        }
    }

    /// <summary>The reactive current (pu of MBASE, capacitive positive) at a trial voltage magnitude.</summary>
    internal double Iq(double vm)
    {
        if (_lag is not null) return _lag.Output;
        Statcom1Data d = _p.Data;
        return Math.Clamp((d.Kp * (_p.Vref - vm) + _xi.Output) / (1.0 + d.Kp * d.Xs), -d.ImaxInd, d.ImaxCap);
    }

    public Complex TrialCurrent(Complex v)
    {
        double vm = v.Magnitude;
        if (!(vm > DeviceCurrents.MinVoltagePu)) return Complex.Zero;
        return new Complex(0.0, -Iq(vm) * _p.MbasePu) * (v / vm);
    }

    public Complex Commit(Complex v)
    {
        Complex i = TrialCurrent(v);
        (_ip, _iq) = DeviceCurrents.Split(i, v);
        double iq = Iq(v.Magnitude);
        _samples.Add(new[] { iq, _xi.Output, v.Magnitude * iq * _p.MbasePu });
        return i;
    }

    public Complex TerminalCurrent(Complex injection, Complex v) => injection;

    public void Step(double t, Complex v, Complex[] nodeVoltages, double dt)
    {
        Statcom1Data d = _p.Data;
        double vm = v.Magnitude;
        double iq = Iq(vm);
        double e = _p.Vref - vm - d.Xs * iq;
        if (d.Ki != 0) _xi.Step(e, dt);
        if (_lag is not null) _lag.Step(d.Kp * e + _xi.Output, dt);
    }

    public double ActiveCurrent => _ip;

    public double ReactiveCurrent => _iq;

    public bool InDip => false;

    public RmsDeviceTrace? Trace => new(_p.Name, _p.Data.Model, States, _samples);
}
