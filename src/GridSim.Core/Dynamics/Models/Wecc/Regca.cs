using System;
using System.Collections.Generic;
using System.Numerics;

namespace GridSim.Core.Dynamics.Models.Wecc;

/// <summary>
/// REGC_A (PSS/E REGCA1), the renewable energy generator/converter model, after EPRI 3002006525 Fig. D-4. All
/// quantities are per unit on the unit's own MVA base (MBASE). Reactive current is held EXPORT-positive
/// (DECISIONS D2): the diagram's <c>-1/(1+sTg)</c> block gives the network-convention <c>Iq = -Iq_export</c>.
/// <code>
///   states  Ip  = lag(Ipcmd, Tg), rise limited to Rrpwr, upper limit LVPL(Vf)          (s1, non-windup)
///           Iqx = lag(Iqcmd, Tg), export fall limited to Iqrmax when Q0 &gt; 0,
///                                 export rise limited to -Iqrmin when Q0 &lt; 0          (s0)
///           Vf  = lag(|Vt|, Tfltr)                                                       (s2)
///   network Ip_out  = Ip * LVACM(|Vt|)            LVACM = 0 below Lvpnt0, 1 above Lvpnt1, linear between
///           Iqx_out = max(Iqx - Khv max(|Vt| - Volim, 0), Iolim)                         (HVRCM, DECISIONS D3)
///   LVPL(Vf) = 0 at or below Zerox, Lvpl1 (Vf - Zerox)/(Brkpt - Zerox) between, no limit at or above Brkpt
/// </code>
/// The two network blocks (LVACM, HVRCM) are algebraic in the terminal voltage and are evaluated inside the
/// converter / network fixed point; the three states advance once per step from the start-of-step voltage with the
/// current commands held (see <see cref="WeccBlocks"/>).
/// </summary>
public sealed class Regca
{
    private readonly RegcaParameters _p;
    private readonly bool _limitExportFall, _limitExportRise;
    private double _ip, _iq, _vf;

    /// <summary>Initialise at the solved operating point: terminal voltage <paramref name="v0"/>, active power
    /// <paramref name="p0"/> and exported reactive power <paramref name="q0"/> (machine base). Throws, naming the
    /// quantity, when that point is not an equilibrium of the model (LVACM gain zero with power flowing, the active
    /// current above the LVPL limit, the voltage above Volim with HVRCM active, the reactive current below Iolim).</summary>
    public Regca(RegcaParameters p, double v0, double p0, double q0)
    {
        ArgumentNullException.ThrowIfNull(p);
        IReadOnlyList<string> bad = p.Check();
        if (bad.Count > 0) throw new ArgumentException("REGC_A parameters: " + string.Join("; ", bad), nameof(p));
        if (!(v0 > 0.0) || !double.IsFinite(v0)) throw new ArgumentOutOfRangeException(nameof(v0), v0, "REGC_A: the initial terminal voltage must be a finite number above 0.");
        _p = p;
        double ip0 = p0 / v0, iq0 = q0 / v0;
        double g = Lvacm(v0);
        if (g <= 0.0 && ip0 != 0.0)
            throw new ArgumentOutOfRangeException(nameof(v0), v0, $"REGC_A: the initial terminal voltage {WeccCheck.N(v0)} pu is at or below Lvpnt0 = {WeccCheck.N(p.Lvpnt0)}, "
                + "where the low-voltage active current management passes no active current, but the unit carries active power; no equilibrium.");
        _ip = g > 0.0 ? ip0 / g : 0.0;   // the lag state the LVACM gain scales down to the solved current
        _iq = iq0;
        _vf = v0;
        if (_ip > Lvpl(v0))
            throw new ArgumentOutOfRangeException(nameof(p0), p0, $"REGC_A: the initial active current {WeccCheck.N(_ip)} pu is above the LVPL limit {WeccCheck.N(Lvpl(v0))} pu at {WeccCheck.N(v0)} pu.");
        if (v0 > p.Volim && p.Khv > 0.0)
            throw new ArgumentOutOfRangeException(nameof(v0), v0, $"REGC_A: the initial terminal voltage {WeccCheck.N(v0)} pu is above Volim = {WeccCheck.N(p.Volim)} pu with Khv > 0, "
                + "so the high-voltage reactive current management would already be acting; no equilibrium of the command path.");
        if (iq0 < p.Iolim)
            throw new ArgumentOutOfRangeException(nameof(q0), q0, $"REGC_A: the initial reactive current {WeccCheck.N(iq0)} pu is below Iolim = {WeccCheck.N(p.Iolim)} pu.");
        _limitExportFall = q0 > 0.0;   // Iqrmax acts when the initial reactive output is positive (EPRI Fig. D-4 box)
        _limitExportRise = q0 < 0.0;   // Iqrmin acts when it is negative
    }

    public RegcaParameters Parameters => _p;

    /// <summary>The active current lag state Ip (pu), before LVACM.</summary>
    public double Ip => _ip;

    /// <summary>The exported reactive current lag state (pu), before HVRCM.</summary>
    public double IqExport => _iq;

    /// <summary>The filtered terminal voltage of the LVPL (pu).</summary>
    public double FilteredVoltage => _vf;

    /// <summary>The LVACM gain at terminal voltage <paramref name="v"/>.</summary>
    public double Lvacm(double v)
    {
        if (v >= _p.Lvpnt1) return 1.0;
        if (v <= _p.Lvpnt0) return 0.0;
        return (v - _p.Lvpnt0) / (_p.Lvpnt1 - _p.Lvpnt0);
    }

    /// <summary>The LVPL upper limit on Ip at filtered voltage <paramref name="vf"/> (positive infinity: no limit).</summary>
    public double Lvpl(double vf)
    {
        if (_p.Lvplsw == 0 || vf >= _p.Brkpt) return double.PositiveInfinity;
        if (vf <= _p.Zerox) return 0.0;
        return _p.Lvpl1 * (vf - _p.Zerox) / (_p.Brkpt - _p.Zerox);
    }

    /// <summary>The currents the unit injects at terminal voltage magnitude <paramref name="v"/> (machine base):
    /// active <c>Ip LVACM(v)</c> and exported reactive <c>max(Iqx - Khv max(v - Volim, 0), Iolim)</c>.</summary>
    public (double Ip, double IqExport) Output(double v)
    {
        double iq = _iq - _p.Khv * Math.Max(v - _p.Volim, 0.0);
        return (_ip * Lvacm(v), Math.Max(iq, _p.Iolim));
    }

    /// <summary>The exact network-interface solution against the Thevenin view of the node (DECISIONS D38;
    /// <see cref="CurrentSourceNetworkSolver"/>): the injected frame current <c>Ip LVACM(x) - j HVRCM(x)</c> is affine in
    /// the terminal magnitude x between Lvpnt0, Lvpnt1, Volim and the point where HVRCM reaches Iolim.</summary>
    internal CurrentSourceNetworkSolver.Solution SolveWithNetwork(Complex a, Complex c1, Complex c2, double heldAngle, double previousMagnitude)
    {
        var breaks = new List<double> { _p.Lvpnt0, _p.Lvpnt1, _p.Volim };
        if (_p.Khv > 0.0) breaks.Add(_p.Volim + (_iq - _p.Iolim) / _p.Khv);
        return CurrentSourceNetworkSolver.Solve(x => { (double ip, double iq) = Output(x); return new Complex(ip, -iq); },
            breaks, a, c1, c2, heldAngle, previousMagnitude);
    }

    /// <summary>Advance the three states one step of <paramref name="dt"/> with the commands and the terminal voltage
    /// magnitude held. The LVPL limit is the one at the start-of-step filtered voltage.</summary>
    public void Step(double ipcmd, double iqcmd, double v, double dt)
    {
        double lvpl = Lvpl(_vf);
        double ip = WeccBlocks.Lag(_ip, ipcmd, _p.Tg, dt);
        if (ip > _ip) ip = Math.Min(ip, _ip + _p.Rrpwr * dt);   // rise limit (falls are not limited)
        _ip = Math.Min(ip, lvpl);                                // non-windup LVPL upper limit

        double iq = WeccBlocks.Lag(_iq, iqcmd, _p.Tg, dt);
        // The network-convention Iq = -Iqx: its upward limit Iqrmax bounds the FALL of the exported current, its
        // downward limit Iqrmin bounds the RISE; each only on the side the initial Q selects.
        double rateUp = _limitExportRise ? -_p.Iqrmin : double.PositiveInfinity;
        double rateDown = _limitExportFall ? -_p.Iqrmax : double.NegativeInfinity;
        _iq = WeccBlocks.RateLimit(_iq, iq, rateUp, rateDown, dt);

        _vf = WeccBlocks.Lag(_vf, v, _p.Tfltr, dt);
    }

    /// <summary>Set the two current states to the commands the controls give at t = 0, once the composed unit has checked
    /// they agree with this model's own equilibrium to <see cref="WeccEquilibrium.Tolerance"/>: the closed-form starts of
    /// REGC_A (P0 / V0) and of the control (for example Qlim / Vt_filt) can differ in the last bit, and this removes that
    /// rounding so the commands and the states are the same numbers from the first step.</summary>
    internal void Settle(double ipcmd, double iqcmd)
    {
        _ip = Math.Min(ipcmd, Lvpl(_vf));
        _iq = iqcmd;
    }

    /// <summary>The equilibrium residual of each state for held commands and voltage (input minus state for each lag):
    /// all zero at a stationary point.</summary>
    public void Residuals(double ipcmd, double iqcmd, double v, List<(string Name, double Value)> into)
    {
        into.Add(("REGC_A Ip (Ipcmd - Ip)", Math.Min(ipcmd, Lvpl(_vf)) - _ip));
        into.Add(("REGC_A Iq (Iqcmd - Iq)", iqcmd - _iq));
        into.Add(("REGC_A Vf (|Vt| - Vf)", v - _vf));
    }
}
