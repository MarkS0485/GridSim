using System;
using System.Collections.Generic;
using GridSim.Core.Dynamics.Plugins;
using GridSim.Core.Numerics;

[assembly: GridSimModelPlugin(2)]

namespace GridSim.ModelPlugin.Contract2Sample;

/// <summary>SAMPLE_GENCLS: the classical machine as a contract 2 machine model, a constant EMF E' behind the transient
/// reactance X'd (a Norton source: Y = 1 / (j X'd), I_N = E' Y), with the swing equation by forward Euler on the step's start
/// values. Parameters on the machine base: H (s), D (pu power per pu speed), XDP (pu), F0 (Hz, the synchronous frequency).</summary>
public sealed class SampleGenclsFactory : IUserModelFactory
{
    public string Id => "SAMPLE_GENCLS";

    public UserModelRole Role => UserModelRole.Machine;

    public IReadOnlyList<PluginParameter> Parameters { get; } = new[]
    {
        new PluginParameter("H", "s", 3.0, 1e-6, null),
        new PluginParameter("D", "pu", 0.0, null, null),
        new PluginParameter("XDP", "pu", 0.3, 1e-6, null),
        new PluginParameter("F0", "Hz", 50.0, 1.0, null),
    };

    public object Create(IReadOnlyList<double> parameters) => new SampleGencls(parameters);
}

internal sealed class SampleGencls : IUserMachine
{
    private readonly double _h, _d, _xdp, _ws;
    private double _x, _hs, _er, _ei, _emag, _delta, _w, _pm, _pe;

    internal SampleGencls(IReadOnlyList<double> p)
    {
        _h = p[0];
        _d = p[1];
        _xdp = p[2];
        _ws = 2.0 * Math.PI * p[3];
        _x = _xdp;
    }

    // Y = 1 / (j x) = -j / x (system base).
    public (double Re, double Im) NortonAdmittancePu => (0.0, -1.0 / _x);

    public void Initialise(in MachineInit init)
    {
        if (!(init.MbasePu > 0.0)) throw new ArgumentOutOfRangeException(nameof(init), "SAMPLE_GENCLS: the unit has no rating (MBASE).");
        _x = _xdp / init.MbasePu;
        _hs = _h * init.MbasePu;
        double v2 = init.V0Re * init.V0Re + init.V0Im * init.V0Im;
        if (!(v2 > 0.0)) throw new ArgumentOutOfRangeException(nameof(init), "SAMPLE_GENCLS: the start voltage is zero.");
        // The terminal current I = conj(S / V) = (P - jQ)(Vr + jVi) / |V|^2, then E' = V + j x I.
        double ir = (init.P0 * init.V0Re + init.Q0 * init.V0Im) / v2;
        double ii = (init.P0 * init.V0Im - init.Q0 * init.V0Re) / v2;
        _er = init.V0Re - _x * ii;
        _ei = init.V0Im + _x * ir;
        _emag = Math.Sqrt(_er * _er + _ei * _ei);
        _delta = DetMath.Atan2(_ei, _er);
        _w = 0.0;
        _pm = init.P0;
        _pe = init.P0;
    }

    // I_N = E' Y = (Er + j Ei)(-j / x) = Ei / x - j Er / x.
    public (double Re, double Im) NortonCurrentPu => (_ei / _x, -_er / _x);

    public void Step(double dt, in MachineStepInputs inputs)
    {
        // The terminal current I = (E' - V) / (j x); the electrical power Re(E' conj I).
        double ir = (_ei - inputs.VIm) / _x;
        double ii = -(_er - inputs.VRe) / _x;
        _pe = _er * ir + _ei * ii;
        double accel = (_pm - _pe - _d * _w) / (2.0 * _hs);
        _delta += _ws * _w * dt;
        _w += accel * dt;
        (double s, double c) = DetMath.SinCos(_delta);
        _er = _emag * c;
        _ei = _emag * s;
    }

    public double SpeedPu => _w;

    public double AngleRad => _delta;

    public double FieldCurrentPu => 0.0;

    public double ElectricalPowerPu => _pe;
}

/// <summary>SAMPLE_UV27: an under-voltage relay written as the built-in unit 27 stage is evaluated: beyond pickup while
/// V &lt; PICKUP; it picks up at the first step beyond and stays so, a step not beyond resets it, and it operates at the first
/// step k with (k - k_pickup) h + h 1e-6 &gt;= DELAY (whole steps, never early). The trip carries CODE.</summary>
public sealed class SampleUv27Factory : IUserModelFactory
{
    public string Id => "SAMPLE_UV27";

    public UserModelRole Role => UserModelRole.Relay;

    public IReadOnlyList<PluginParameter> Parameters { get; } = new[]
    {
        new PluginParameter("PICKUP", "pu", 0.8, 0.0, null),
        new PluginParameter("DELAY", "s", 0.05, 0.0, null),
        new PluginParameter("CODE", "1", 27.0, 0.0, 1000.0),
    };

    public object Create(IReadOnlyList<double> parameters) => new SampleUv27(parameters, latch: false);
}

/// <summary>SAMPLE_LATCH: SAMPLE_UV27 with a sealed-in pickup: once V has been below PICKUP at one step, the timer runs on
/// whatever the voltage does after, and the relay trips DELAY later.</summary>
public sealed class SampleLatchFactory : IUserModelFactory
{
    public string Id => "SAMPLE_LATCH";

    public UserModelRole Role => UserModelRole.Relay;

    public IReadOnlyList<PluginParameter> Parameters { get; } = new[]
    {
        new PluginParameter("PICKUP", "pu", 0.8, 0.0, null),
        new PluginParameter("DELAY", "s", 0.3, 0.0, null),
        new PluginParameter("CODE", "1", 86.0, 0.0, 1000.0),
    };

    public object Create(IReadOnlyList<double> parameters) => new SampleUv27(parameters, latch: true);
}

internal sealed class SampleUv27 : IUserRelay
{
    private readonly double _pickup, _delay;
    private readonly int _code;
    private readonly bool _latch;
    private int _kp = -1;

    internal SampleUv27(IReadOnlyList<double> p, bool latch)
    {
        _pickup = p[0];
        _delay = p[1];
        _code = (int)p[2];
        _latch = latch;
    }

    public void Initialise() => _kp = -1;

    public RelayDecision Evaluate(in RelayInputs inputs)
    {
        bool beyond = inputs.VoltagePu < _pickup;
        if (!beyond && !(_latch && _kp >= 0))
        {
            _kp = -1;
            return RelayDecision.None;
        }
        if (_kp < 0) _kp = inputs.StepIndex;
        double elapsed = (inputs.StepIndex - _kp) * inputs.StepSeconds;
        return elapsed + inputs.StepSeconds * 1e-6 >= _delay ? RelayDecision.TripWith(_code) : RelayDecision.None;
    }
}
