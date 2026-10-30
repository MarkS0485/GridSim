using System;
using System.Collections.Generic;
using System.Numerics;

namespace GridSim.Core.Dynamics.Models.Wecc;

/// <summary>
/// REGFM_A1: the droop-controlled grid-forming inverter model approved by the WECC MVS on 27 September 2023 (W. Du et al.,
/// "Model Specification of Droop-Controlled, Grid-Forming Inverters (REGFM_A1)", PNNL-35110, September 2023: Fig. 1-5,
/// Table 1, Eq. 1-9). Per unit on the inverter rating (MBASE). GridSim reads a <c>'REGFM_A1'</c> record (the PSS/E record
/// order is not public; DECISIONS D30) with the 22 data values of Table 1 in its order, the three set points that the power
/// flow initialises (Vref, Qref, Pref) left out: XL mq kpv kiv Emax Emin mp Pmax Pmin kppmax kipmax omega0 TPf TQf TVf Qmax
/// Qmin VFlag QVFlag kpqmax kiqmax ImaxF.
/// </summary>
public sealed record RegfmA1Parameters
{
    /// <summary>Coupling reactance (pu).</summary>
    public double XL { get; init; } = 0.15;
    /// <summary>Q-V droop gain (pu).</summary>
    public double Mq { get; init; } = 0.05;
    /// <summary>Voltage controller proportional gain (pu).</summary>
    public double Kpv { get; init; }
    /// <summary>Voltage controller integral gain (pu/s).</summary>
    public double Kiv { get; init; } = 5.86;
    /// <summary>Limits on the internal voltage magnitude (pu).</summary>
    public double Emax { get; init; } = 1.15;
    public double Emin { get; init; }
    /// <summary>P-f droop gain (pu).</summary>
    public double Mp { get; init; } = 0.01;
    /// <summary>Active power limits (pu); Pmin negative for a store.</summary>
    public double Pmax { get; init; } = 0.9;
    public double Pmin { get; init; }
    /// <summary>Overload mitigation (Pmax / Pmin) controller gains (pu, pu/s).</summary>
    public double Kppmax { get; init; } = 0.01;
    public double Kipmax { get; init; } = 0.1;
    /// <summary>Rated angular frequency (rad/s); must equal 2 pi times the case's nominal frequency.</summary>
    public double Omega0 { get; init; } = 2.0 * Math.PI * 60.0;
    /// <summary>Measurement low-pass filters for P, Q and V (s).</summary>
    public double TPf { get; init; } = 0.01;
    public double TQf { get; init; } = 0.01;
    public double TVf { get; init; } = 0.01;
    /// <summary>Reactive power limits (pu).</summary>
    public double Qmax { get; init; } = 0.44;
    public double Qmin { get; init; } = -0.44;
    /// <summary>1: the PI regulates the terminal voltage through Edroop; 0: the droop sets Edroop directly.</summary>
    public int VFlag { get; init; } = 1;
    /// <summary>1: a plant controller would move Vref (Qref = 0 at the start); 0: it would move Qref (Qref = Q0).</summary>
    public int QVFlag { get; init; } = 1;
    /// <summary>Qmax / Qmin controller gains (pu, pu/s).</summary>
    public double Kpqmax { get; init; } = 3.0;
    public double Kiqmax { get; init; } = 20.0;
    /// <summary>Maximum transient output current (pu).</summary>
    public double ImaxF { get; init; } = 2.0;

    /// <summary>Every problem with these values (empty when usable).</summary>
    public IReadOnlyList<string> Check()
    {
        var p = new List<string>();
        const string m = "REGFM_A1";
        WeccCheck.Above(p, m, "XL", XL, 0.0);
        WeccCheck.AtLeast(p, m, "mq", Mq, 0.0);
        WeccCheck.AtLeast(p, m, "kpv", Kpv, 0.0);
        WeccCheck.AtLeast(p, m, "kiv", Kiv, 0.0);
        WeccCheck.Finite(p, m, "Emax", Emax);
        WeccCheck.AtLeast(p, m, "Emin", Emin, 0.0);
        WeccCheck.StrictlyOrdered(p, m, "Emin", Emin, "Emax", Emax);
        WeccCheck.Above(p, m, "mp", Mp, 0.0);
        WeccCheck.Finite(p, m, "Pmax", Pmax);
        WeccCheck.Finite(p, m, "Pmin", Pmin);
        WeccCheck.StrictlyOrdered(p, m, "Pmin", Pmin, "Pmax", Pmax);
        WeccCheck.AtLeast(p, m, "kppmax", Kppmax, 0.0);
        WeccCheck.AtLeast(p, m, "kipmax", Kipmax, 0.0);
        WeccCheck.Above(p, m, "omega0", Omega0, 0.0);
        WeccCheck.AtLeast(p, m, "TPf", TPf, 0.0);
        WeccCheck.AtLeast(p, m, "TQf", TQf, 0.0);
        WeccCheck.AtLeast(p, m, "TVf", TVf, 0.0);
        WeccCheck.Finite(p, m, "Qmax", Qmax);
        WeccCheck.Finite(p, m, "Qmin", Qmin);
        WeccCheck.StrictlyOrdered(p, m, "Qmin", Qmin, "Qmax", Qmax);
        WeccCheck.Flag(p, m, "VFlag", VFlag, 0, 1);
        WeccCheck.Flag(p, m, "QVFlag", QVFlag, 0, 1);
        WeccCheck.AtLeast(p, m, "kpqmax", Kpqmax, 0.0);
        WeccCheck.AtLeast(p, m, "kiqmax", Kiqmax, 0.0);
        WeccCheck.Above(p, m, "ImaxF", ImaxF, 0.0);
        if (VFlag == 1 && Kpv == 0.0 && Kiv == 0.0) p.Add($"{m} VFlag = 1 needs kpv or kiv above 0 (the voltage controller would do nothing)");
        return p;
    }
}

/// <summary>
/// REGFM_A1 in the RMS engine (see <see cref="RegfmA1Parameters"/>). A voltage source E at angle delta behind j XL, stamped
/// as its Norton admittance on the bus (spec Eq. 8, 9). Per unit on MBASE; the network sees system per unit.
/// <code>
///   Pinv = lag(P, TPf), Qinv = lag(Q, TQf), Vinv = lag(|V|, TVf)                     (Eq. 4-6, P and Q at the terminal)
///   dPmax = min(0, kppmax (Pmax - Pinv) + xPmax),  xPmax' = kipmax (Pmax - Pinv), xPmax &lt;= 0
///   dPmin = max(0, kppmax (Pmin - Pinv) + xPmin),  xPmin' = kipmax (Pmin - Pinv), xPmin &gt;= 0
///   d omega = omega0 (mp (Pref - Pinv) + dPmax + dPmin)     (rad/s);   delta' = d omega;   omega = omega0 + d omega
///   dQmax, dQmin likewise with kpqmax, kiqmax on Qinv
///   Vcmd = Vref + mq (Qref - Qinv) + dQmax + dQmin
///   VFlag 1: Edroop = clamp(kpv (Vcmd - Vinv) + xV, Emin, Emax), xV' = kiv (Vcmd - Vinv) in [Emin, Emax]
///   VFlag 0: Edroop = clamp(Vcmd, Emin, Emax)
///   network: I = (Edroop e^{j delta} - V) / (j XL); |I| &gt; ImaxF -&gt; I = ImaxF I / |I|   (Fig. 4: same angle phi)
/// </code>
/// Initialisation: I0 from the solved P0, Q0, V0; E0 = V0 + j XL I0, delta0 = angle(E0); Pref = P0; Qref = 0 (QVFlag 1) or
/// Q0 (QVFlag 0); Vref so that Vcmd = V0 (VFlag 1) or |E0| (VFlag 0); xV = |E0|. Refused, naming the quantity: P0 outside
/// [Pmin, Pmax], Q0 outside [Qmin, Qmax], |E0| outside [Emin, Emax], |I0| at or above ImaxF.
/// Integration (the WECC rule, <see cref="WeccBlocks"/>): lags by exact ZOH, integrators and delta by forward Euler, all
/// from the start-of-step measurements; E is constant over a step except through the algebraic current limit. The spec
/// says nothing of freezing the controls while the current limit acts, so nothing is frozen (DECISIONS D33).
/// </summary>
public sealed class RegfmA1State : IRmsConverterModel
{
    private readonly RmsConverterUnit _u;
    private readonly RegfmA1Parameters _p;
    private readonly double _r;
    private readonly Complex _zl, _ysys;
    private readonly double _pref, _qref, _vref;
    private double _pinv, _qinv, _vinv, _xpmax, _xpmin, _xqmax, _xqmin, _xv, _delta;
    private double _pe, _qe, _angle;
    private Complex _iTerm;
    private bool _limited;

    public RegfmA1State(RmsConverterUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        WeccPlant plant = unit.Wecc ?? throw new ArgumentException("The unit carries no WECC model chain.", nameof(unit));
        _p = plant.Gfm ?? throw new ArgumentException("The unit's chain has no REGFM_A1 model.", nameof(unit));
        IReadOnlyList<string> bad = plant.Check();
        if (bad.Count > 0) throw new ArgumentException($"REGFM_A1 unit '{unit.Name}': " + string.Join("; ", bad), nameof(unit));
        _u = unit;
        _r = unit.RatingPu;
        RegfmA1Parameters p = _p;
        string where = $"REGFM_A1 unit '{unit.Name}' at bus {unit.Bus}";
        double ws = 2.0 * Math.PI * unit.Parameters.NominalHz;
        if (Math.Abs(p.Omega0 - ws) > 1e-3 * ws)
            throw new InvalidOperationException($"{where}: omega0 = {WeccCheck.N(p.Omega0)} rad/s differs from 2 pi times the case's nominal frequency ({WeccCheck.N(ws)} rad/s) by more than 0.1 %.");
        _zl = new Complex(0.0, p.XL);
        _ysys = unit.NortonAdmittance;
        Complex v0 = unit.V0;
        double p0 = unit.P0 / _r, q0 = unit.Q0 / _r;
        Complex i0 = Complex.Conjugate(new Complex(p0, q0) / v0);
        Complex e0 = v0 + _zl * i0;
        double em = e0.Magnitude;
        var issues = new List<string>();
        if (p0 < p.Pmin || p0 > p.Pmax) issues.Add($"the initial active power {WeccCheck.N(p0)} pu is outside [Pmin, Pmax] = [{WeccCheck.N(p.Pmin)}, {WeccCheck.N(p.Pmax)}]");
        if (q0 < p.Qmin || q0 > p.Qmax) issues.Add($"the initial reactive power {WeccCheck.N(q0)} pu is outside [Qmin, Qmax] = [{WeccCheck.N(p.Qmin)}, {WeccCheck.N(p.Qmax)}]");
        if (em < p.Emin || em > p.Emax) issues.Add($"the initial internal voltage {WeccCheck.N(em)} pu is outside [Emin, Emax] = [{WeccCheck.N(p.Emin)}, {WeccCheck.N(p.Emax)}]");
        if (!(i0.Magnitude < p.ImaxF)) issues.Add($"the initial current {WeccCheck.N(i0.Magnitude)} pu is not below ImaxF = {WeccCheck.N(p.ImaxF)}");
        if (issues.Count > 0) throw new InvalidOperationException($"{where}: " + string.Join("; ", issues) + "; no equilibrium.");
        _pinv = p0; _qinv = q0; _vinv = v0.Magnitude;
        _pref = p0;
        _qref = p.QVFlag == 1 ? 0.0 : q0;
        _vref = (p.VFlag == 1 ? v0.Magnitude : em) - p.Mq * (_qref - q0);
        _xv = em;
        _delta = DetMath.Phase(e0);
        _angle = DetMath.Phase(v0);
        _pe = p0; _qe = q0;
        _iTerm = i0 * _r;
        // Residual gate: every state derivative zero and the source reproducing the solved current.
        var res = new List<(string, double)>
        {
            ("REGFM_A1 d omega (rad/s)", DeltaOmega()),
            ("REGFM_A1 Edroop - |E0|", Edroop() - em),
            ("REGFM_A1 xV' (Vcmd - Vinv)", p.VFlag == 1 ? Vcmd() - _vinv : 0.0),
        };
        Complex it = TerminalCurrent(v0, out _) / _r - i0;
        res.Add(("REGFM_A1 terminal current Re (I - I0)", it.Real));
        res.Add(("REGFM_A1 terminal current Im (I - I0)", it.Imaginary));
        try { WeccEquilibrium.Require("REGFM_A1", res); }
        catch (InvalidOperationException ex) { throw new InvalidOperationException($"{where}: {ex.Message}", ex); }
    }

    public RmsConverterUnit Unit => _u;
    public RegfmA1Parameters Parameters => _p;

    private double DeltaOmega()
    {
        RegfmA1Parameters p = _p;
        double dPmax = Math.Min(0.0, p.Kppmax * (p.Pmax - _pinv) + _xpmax);
        double dPmin = Math.Max(0.0, p.Kppmax * (p.Pmin - _pinv) + _xpmin);
        return p.Omega0 * (p.Mp * (_pref - _pinv) + dPmax + dPmin);
    }

    private double Vcmd()
    {
        RegfmA1Parameters p = _p;
        double dQmax = Math.Min(0.0, p.Kpqmax * (p.Qmax - _qinv) + _xqmax);
        double dQmin = Math.Max(0.0, p.Kpqmax * (p.Qmin - _qinv) + _xqmin);
        return _vref + p.Mq * (_qref - _qinv) + dQmax + dQmin;
    }

    private double Edroop()
    {
        RegfmA1Parameters p = _p;
        return p.VFlag == 1
            ? Math.Clamp(p.Kpv * (Vcmd() - _vinv) + _xv, p.Emin, p.Emax)
            : Math.Clamp(Vcmd(), p.Emin, p.Emax);
    }

    /// <summary>The internal voltage phasor the droop control commands (network frame, pu).</summary>
    public Complex DroopSource => DetMath.FromPolar(Edroop(), _delta);

    /// <summary>The internal frequency deviation (rad/s) and the internal angle (rad).</summary>
    public double DeltaOmegaRadPerSecond => DeltaOmega();
    public double AngleRad => _delta;

    /// <summary>The internal frequency (pu of nominal).</summary>
    public double FrequencyPu => 1.0 + DeltaOmega() / _p.Omega0;

    /// <summary>True while the fault current limit acts (at the last commit).</summary>
    public bool CurrentLimited => _limited;

    /// <summary>Filtered P, Q (machine base) and V the controls see.</summary>
    public (double P, double Q, double V) Filtered => (_pinv, _qinv, _vinv);

    // Terminal current (system pu) at terminal voltage v: the source behind j XL, limited to ImaxF in magnitude.
    private Complex TerminalCurrent(Complex v, out bool limited)
    {
        Complex i = (DroopSource - v) / _zl;   // machine base
        limited = i.Magnitude > _p.ImaxF;
        if (limited) i *= _p.ImaxF / i.Magnitude;
        return i * _r;
    }

    public Complex TrialCurrent(Complex v) => TerminalCurrent(v, out _) + _ysys * v;

    public Complex Commit(Complex v)
    {
        Complex it = TerminalCurrent(v, out bool limited);
        _limited = limited;
        _iTerm = it;
        Complex s = v * Complex.Conjugate(it);
        _pe = s.Real / _r; _qe = s.Imaginary / _r;
        double vm = v.Magnitude;
        if (vm > RmsConverterState.PllLockMinVoltagePu) _angle = DetMath.Phase(v);
        Complex rel = it * Complex.Conjugate(DetMath.FromPolar(1.0, _angle));
        ActiveCurrent = rel.Real;
        ReactiveCurrent = -rel.Imaginary;
        return it + _ysys * v;
    }

    public Complex TerminalCurrent(Complex injection, Complex v) => injection - _ysys * v;

    public void Step(double t, Complex v, Complex[] nodeVoltages, double dt)
    {
        RegfmA1Parameters p = _p;
        double dw = DeltaOmega();
        double vcmd = Vcmd();
        double ePmax = p.Pmax - _pinv, ePmin = p.Pmin - _pinv, eQmax = p.Qmax - _qinv, eQmin = p.Qmin - _qinv;
        double eV = vcmd - _vinv;
        _delta += dw * dt;
        _xpmax = Math.Min(0.0, _xpmax + p.Kipmax * ePmax * dt);
        _xpmin = Math.Max(0.0, _xpmin + p.Kipmax * ePmin * dt);
        _xqmax = Math.Min(0.0, _xqmax + p.Kiqmax * eQmax * dt);
        _xqmin = Math.Max(0.0, _xqmin + p.Kiqmax * eQmin * dt);
        if (p.VFlag == 1) _xv = Math.Clamp(_xv + p.Kiv * eV * dt, p.Emin, p.Emax);
        _pinv = WeccBlocks.Lag(_pinv, _pe, p.TPf, dt);
        _qinv = WeccBlocks.Lag(_qinv, _qe, p.TQf, dt);
        _vinv = WeccBlocks.Lag(_vinv, v.Magnitude, p.TVf, dt);
    }

    public double ActiveCurrent { get; private set; }
    public double ReactiveCurrent { get; private set; }
    public bool InDip => _limited;
}
