using System;
using System.Collections.Generic;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.Plugins;

[assembly: GridSimModelPlugin(1)]

namespace GridSim.ModelPlugin.Sample;

/// <summary>SAMPLE_TGOV1: the TGOV1 equations written with the public ControlBlocks operators, operation for operation as
/// GridSim.Core.Dynamics.Models.Tgov1 (valveCmd = Pref - dw/R; valve lag with limits; reheat lead-lag; Pm = Pt - Dt dw).</summary>
public sealed class SampleTgov1Factory : IUserModelFactory
{
    public string Id => "SAMPLE_TGOV1";

    public UserModelRole Role => UserModelRole.Governor;

    public IReadOnlyList<PluginParameter> Parameters { get; } = new[]
    {
        new PluginParameter("R", "pu", 0.05, 1e-6, null),
        new PluginParameter("T1", "s", 0.5, 0.0, null),
        new PluginParameter("T2", "s", 1.0, 0.0, null),
        new PluginParameter("T3", "s", 5.0, 1e-6, null),
        new PluginParameter("Dt", "pu", 0.0, null, null),
        new PluginParameter("Vmin", "pu", 0.0, null, null),
        new PluginParameter("Vmax", "pu", 1.2, null, null),
    };

    public object Create(IReadOnlyList<double> parameters) => new SampleTgov1(parameters);
}

internal sealed class SampleTgov1 : IUserGovernor
{
    private readonly double _r;
    private readonly double _damping;
    private readonly FirstOrderLag _valve;
    private readonly LeadLag _reheat;
    private double _pref;

    internal SampleTgov1(IReadOnlyList<double> p)
    {
        _r = p[0];
        _damping = p[4];
        _valve = new FirstOrderLag(gain: 1.0, timeConstantSeconds: p[1], min: p[5], max: p[6]);
        _reheat = new LeadLag(p[2], p[3]);
    }

    public void Initialize(double pm0)
    {
        if (!(pm0 >= _valve.Min && pm0 <= _valve.Max))
            throw new ArgumentOutOfRangeException(nameof(pm0), pm0, "SAMPLE_TGOV1: the valve position at equilibrium is outside [Vmin, Vmax].");
        _pref = pm0;
        _valve.Initialize(pm0);
        _reheat.Initialize(pm0);
    }

    public double Step(double speedDev, double dt)
    {
        double valveCmd = _pref - speedDev / _r;
        double pgv = _valve.Step(valveCmd, dt);
        double pt = _reheat.Step(pgv, dt);
        return pt - _damping * speedDev;
    }
}

/// <summary>SAMPLE_SEXS: the SEXS equations (lead-lag shaping, then a gain-lag with field limits) as
/// GridSim.Core.Dynamics.Models.Sexs writes them.</summary>
public sealed class SampleSexsFactory : IUserModelFactory
{
    public string Id => "SAMPLE_SEXS";

    public UserModelRole Role => UserModelRole.Exciter;

    public IReadOnlyList<PluginParameter> Parameters { get; } = new[]
    {
        new PluginParameter("K", "pu", 100.0, 1e-6, null),
        new PluginParameter("Ta", "s", 0.1, 0.0, null),
        new PluginParameter("Tb", "s", 1.0, 1e-6, null),
        new PluginParameter("Te", "s", 0.05, 0.0, null),
        new PluginParameter("Emin", "pu", -5.0, null, null),
        new PluginParameter("Emax", "pu", 5.0, null, null),
    };

    public object Create(IReadOnlyList<double> parameters) => new SampleSexs(parameters);
}

internal sealed class SampleSexs : IUserExciter
{
    private readonly double _k;
    private readonly LeadLag _shaping;
    private readonly FirstOrderLag _exciter;

    internal SampleSexs(IReadOnlyList<double> p)
    {
        _k = p[0];
        _shaping = new LeadLag(p[1], p[2]);
        _exciter = new FirstOrderLag(p[0], p[3], p[4], p[5]);
    }

    public void Initialize(double efd0)
    {
        if (!(efd0 >= _exciter.Min && efd0 <= _exciter.Max))
            throw new ArgumentOutOfRangeException(nameof(efd0), efd0, "SAMPLE_SEXS: the field voltage at equilibrium is outside [Emin, Emax].");
        double verr = efd0 / _k;
        _shaping.Initialize(verr);
        _exciter.Initialize(efd0);
    }

    public double EquilibriumErrorPu(double efd0) => efd0 / _k;

    public double Step(double verr, double speedDev, double dt)
    {
        double v1 = _shaping.Step(verr, dt);
        return _exciter.Step(v1, dt);
    }
}

/// <summary>SAMPLE_PQ: a constant-power current source, I = conj(S0 / v), with S0 the power-flow export at the start.</summary>
public sealed class SamplePqFactory : IUserModelFactory
{
    public string Id => "SAMPLE_PQ";

    public UserModelRole Role => UserModelRole.Injection;

    public IReadOnlyList<PluginParameter> Parameters { get; } = Array.Empty<PluginParameter>();

    public object Create(IReadOnlyList<double> parameters) => new SamplePq();
}

internal sealed class SamplePq : IUserInjection
{
    private double _p, _q, _ip, _iq;

    public (double Re, double Im) NortonAdmittance => (0.0, 0.0);

    public void Initialize(in InjectionStart s)
    {
        _p = s.P0;
        _q = s.Q0;
    }

    public (double Re, double Im) Current(double vRe, double vIm)
    {
        // conj(S / v) = conj(S) / conj(v) = (P - jQ)(vr + j vi) / |v|^2.
        double v2 = vRe * vRe + vIm * vIm;
        if (!(v2 > 0.0)) return (0.0, 0.0);
        return ((_p * vRe + _q * vIm) / v2, (_p * vIm - _q * vRe) / v2);
    }

    public void Commit(double vRe, double vIm)
    {
        (double ir, double ii) = Current(vRe, vIm);
        double vm = Math.Sqrt(vRe * vRe + vIm * vIm);
        _ip = vm > 0.0 ? (ir * vRe + ii * vIm) / vm : 0.0;
        _iq = vm > 0.0 ? (ir * vIm - ii * vRe) / vm : 0.0;
    }

    public void Step(double t, double vRe, double vIm, double dt)
    {
    }

    public double ActiveCurrent => _ip;

    public double ReactiveCurrent => _iq;

    public bool InDip => false;
}

/// <summary>BAD_STATIC: passes the metadata lint but shares a static counter between instances, so two instances stepped side
/// by side differ at once; the replay check refuses it.</summary>
public sealed class BadStaticFactory : IUserModelFactory
{
    public string Id => "BAD_STATIC";

    public UserModelRole Role => UserModelRole.Governor;

    public IReadOnlyList<PluginParameter> Parameters { get; } = Array.Empty<PluginParameter>();

    public object Create(IReadOnlyList<double> parameters) => new BadStatic();
}

internal sealed class BadStatic : IUserGovernor
{
    private static long s_count;
    private double _pm0;

    public void Initialize(double pm0) => _pm0 = pm0;

    public double Step(double speedDev, double dt)
    {
        s_count++;
        return _pm0 + 1e-9 * s_count;
    }
}
