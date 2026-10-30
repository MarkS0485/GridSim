using System;
using System.Collections.Generic;

namespace GridSim.Core.Dynamics.Models.Wecc;

/// <summary>The measured and external inputs of a REEC model for one step (machine base, per unit).</summary>
/// <param name="Vt">Terminal voltage magnitude (raw, unfiltered).</param>
/// <param name="Pe">Electrical active power output.</param>
/// <param name="Qgen">Reactive power output (exporting positive).</param>
/// <param name="Pref">Active power reference (constant, or from the plant controller / torque controller).</param>
/// <param name="Qext">Reactive reference (constant, or from the plant controller).</param>
/// <param name="Wg">Generator speed (pu) for PFlag = 1; 1 without wind mechanics.</param>
public readonly record struct ReecInputs(double Vt, double Pe, double Qgen, double Pref, double Qext, double Wg)
{
    /// <summary>REEC_D: the terminal voltage phasor (network frame), for the local current compensation; unused by
    /// REEC_A/B/C.</summary>
    public System.Numerics.Complex VtPhasor { get; init; }

    /// <summary>REEC_D: the terminal current phasor (machine base, network frame, out of the unit), for the local
    /// current compensation; unused by REEC_A/B/C.</summary>
    public System.Numerics.Complex It { get; init; }
}

/// <summary>What a REEC model computes from its states for one step: the current commands and the block inputs the
/// state advance needs.</summary>
public readonly record struct ReecOutputs(
    double Ipcmd, double Iqcmd, double Pord, double Ipmax, double Ipmin, double Iqmax,
    double Qlim, double QErr, double VErr, double Psel, double S4Target, bool Dip, double Iqinj)
{
    /// <summary>REEC_D: the converter is blocked (every current limit forced to zero); always false for REEC_A/B/C.</summary>
    public bool Blocked { get; init; }

    /// <summary>REEC_D: the reactive current lower limit (Iqmin = -Iqmax, or Iqmax when Iqmax is negative); REEC_A/B/C
    /// use -Iqmax and leave this 0.</summary>
    public double Iqmin { get; init; }

    /// <summary>REEC_D: Iqcmd_bl, the reactive command before the Iqmax/Iqmin limits (what Thld holds).</summary>
    public double IqcmdBl { get; init; }
}

/// <summary>An electrical control model as the composed WECC unit drives it (REEC_A/B/C in <see cref="Reec"/>, REEC_D in
/// <see cref="Reecd"/>): the same four calls per step, in the same order.</summary>
public interface IReecModel
{
    /// <summary>The WECC model name.</summary>
    string Name { get; }
    /// <summary>The Pref that holds the initial operating point.</summary>
    double PrefInitial { get; }
    /// <summary>The Qext that holds the initial operating point.</summary>
    double QextInitial { get; }
    /// <summary>True while the voltage-dip logic is active.</summary>
    bool InDip { get; }
    /// <summary>The power order (pu).</summary>
    double Pord { get; }
    /// <summary>Decide the dip (and, REEC_D, blocking) state for the step at time <paramref name="t"/>.</summary>
    void UpdateDipState(double t, double vt);
    /// <summary>The commands and block inputs from the current states (pure).</summary>
    ReecOutputs Evaluate(in ReecInputs u, double t, double dt);
    /// <summary>Advance every state one step.</summary>
    void Advance(in ReecInputs u, in ReecOutputs o, double dt);
    /// <summary>The equilibrium residuals.</summary>
    void Residuals(in ReecInputs u, in ReecOutputs o, List<(string Name, double Value)> into);
}

/// <summary>
/// REEC_A / REEC_B / REEC_C (PSS/E REECA1 / REECB1 / REECCU1), the renewable energy electrical controls, after EPRI
/// 3002006525 Figs. D-1, D-2, D-3, Fig. 3-2 (state transitions), Fig. 3-4 (storage) and App. B (current limit logic).
/// Per unit on the unit's MBASE; reactive current exporting positive.
/// <code>
///   s0 Vt_filt = lag(Vt, Trv)                    s1 Pe_filt = lag(Pe, Tp)        vp = max(Vt_filt, 0.01)
///   Voltage_dip = Vt &lt; Vdip or Vt &gt; Vup     (raw Vt, DECISIONS D6)
///   Qsel = PfFlag ? tan(pf0) Pe_filt : Qext;     Qlim = clamp(Qsel, Qmin, Qmax)
///   VFlag=1: Vsel = clamp(Kqp (Qlim - Qgen) + s2, Vmin, Vmax), s2' = Kqi (Qlim - Qgen) in [Vmin, Vmax]
///   VFlag=0: Vsel = clamp(Vref1 + Qlim, Vmin, Vmax)     (REEC_B/C: no Vref1, DECISIONS D8)
///   QFlag=1: Iqv_path = clamp(Kvp (Vsel - Vt_filt) + s3, Iqmin, Iqmax), s3' = Kvi (Vsel - Vt_filt) in [Iqmin, Iqmax]
///   QFlag=0: Iqv_path = s4,  s4 = lag(Qlim / vp, Tiq)
///   Iqv = clamp(Kqv db(Vref0 - Vt_filt, dbd1, dbd2), Iql1, Iqh1)
///   Iqinj: 0 normal; Iqv in a dip; after the dip (REEC_A) Iqfrz for Thld s (Thld &gt; 0) or Iqv for -Thld s (Thld &lt; 0)
///   Iqcmd = clamp(Iqv_path + Iqinj, -Iqmax, Iqmax)
///   prl = Pref rate limited to [dPmin, dPmax];  Psel = PFlag ? prl Wg : prl;  s5 Pord = lag(Psel, Tpord) in [Pmin, Pmax]
///   Ipcmd = clamp(Pord / vp, Ipmin, Ipmax)
///   current limit (App. B): Q priority Iqmax = min(VDL1, Imax), Ipmax = min(VDL2, sqrt(Imax^2 - Iqcmd^2));
///                           P priority Ipmax = min(VDL2, Imax), Iqmax = min(VDL1, sqrt(Imax^2 - Ipcmd^2));
///                           Ipmin = 0 (REEC_C: -Ipmax); REEC_A Thld2 holds Ipmax at its dip value after the dip.
///   REEC_C: SOC = SOCini - (1/T) integral(Pe dt); SOC &lt;= SOCmin -> Ipmax = 0; SOC &gt;= SOCmax -> Ipmin = 0.
///   s2, s3, s4, s5 are frozen while Voltage_dip = 1.
/// </code>
/// </summary>
public sealed class Reec : IReecModel
{
    /// <summary>Floor (pu) on Vt_filt when it divides a power into a current (the 0.01 of the diagrams).</summary>
    public const double VoltageFloorPu = 0.01;

    private readonly ReecParameters _p;
    private readonly double _vref0, _tanPf;
    private double _vf, _pf, _s2, _s3, _s4, _pord, _prl, _soc;
    private bool _wasDip;
    private HoldTimer _thld, _thld2;
    private double _ipmaxLast, _ipmaxHeld;

    /// <summary>Initialise at the operating point: terminal voltage <paramref name="v0"/>, active power
    /// <paramref name="p0"/>, exported reactive power <paramref name="q0"/> (machine base), the REGC model's equilibrium
    /// current commands and the generator speed <paramref name="wg0"/>. Sets every state so the start is stationary and
    /// works out the external references that hold it there (<see cref="PrefInitial"/>, <see cref="QextInitial"/>);
    /// refuses a point the model cannot hold (inside a dip, a reference outside its limits, a current command above
    /// the current limit), naming the quantity.</summary>
    public Reec(ReecParameters p, double v0, double p0, double q0, double ipcmd0, double iqcmd0, double wg0 = 1.0)
    {
        ArgumentNullException.ThrowIfNull(p);
        IReadOnlyList<string> bad = p.Check();
        if (bad.Count > 0) throw new ArgumentException($"{p.Name} parameters: " + string.Join("; ", bad), nameof(p));
        _p = p;
        string m = p.Name;
        if (v0 < p.Vdip || v0 > p.Vup)
            throw new ArgumentOutOfRangeException(nameof(v0), v0, $"{m}: the initial terminal voltage {WeccCheck.N(v0)} pu is outside [Vdip, Vup] = [{WeccCheck.N(p.Vdip)}, {WeccCheck.N(p.Vup)}], "
                + "so the unit would start inside the voltage-dip logic; no equilibrium.");
        _vref0 = p.Vref0 == 0.0 ? v0 : p.Vref0;   // PSS/E: Vref0 = 0 means the initial terminal voltage (DECISIONS D9)
        _vf = v0;
        _pf = p0;
        if (p.PfFlag == 1)
        {
            if (p0 == 0.0) throw new ArgumentOutOfRangeException(nameof(p0), p0, $"{m}: constant power factor control (PfFlag = 1) needs a non-zero initial active power to define the power factor.");
            _tanPf = q0 / p0;   // tan(pfaref) taken from the solved P and Q (PSS/E initialises pfaref this way)
        }
        double vp = Math.Max(v0, VoltageFloorPu);

        // Reactive path: the Qlim the start needs, and the external reference that gives it.
        double qlimNeeded;
        if (p.QFlag == 0) qlimNeeded = iqcmd0 * vp;                       // s4 = Qlim / vp must equal Iqcmd0
        else if (p.VFlag == 1) qlimNeeded = q0;                           // the Q PI input Qlim - Qgen must be 0
        else qlimNeeded = v0 - (p.Variant == ReecVariant.A ? p.Vref1 : 0.0);   // Vsel = (Vref1 +) Qlim must equal Vt_filt
        double qsel = p.PfFlag == 1 ? _tanPf * p0 : qlimNeeded;
        if (qsel < p.Qmin || qsel > p.Qmax)
            throw new ArgumentOutOfRangeException(nameof(q0), qsel, $"{m}: the reactive reference the start needs ({WeccCheck.N(qsel)} pu) is outside [Qmin, Qmax] = [{WeccCheck.N(p.Qmin)}, {WeccCheck.N(p.Qmax)}].");
        QextInitial = p.PfFlag == 1 ? qlimNeeded : qsel;   // with PfFlag = 1 Qext is unused; report what Q control would need
        if (p.QFlag == 1)
        {
            if (v0 < p.Vmin || v0 > p.Vmax)
                throw new ArgumentOutOfRangeException(nameof(v0), v0, $"{m}: the initial terminal voltage {WeccCheck.N(v0)} pu is outside [Vmin, Vmax] = [{WeccCheck.N(p.Vmin)}, {WeccCheck.N(p.Vmax)}], the voltage reference limits.");
            _s2 = v0;            // the Q PI's output is the voltage reference Vsel = Vt_filt
            _s3 = iqcmd0;        // the V PI's integral carries the whole command (Iqinj = 0 at the start)
        }
        else
        {
            _s2 = Math.Clamp(v0, p.Vmin, p.Vmax);   // unused on this path; parked inside its limits
            _s4 = iqcmd0;
        }

        // Active path.
        _pord = p0;
        if (p0 < p.Pmin || p0 > p.Pmax)
            throw new ArgumentOutOfRangeException(nameof(p0), p0, $"{m}: the initial active power {WeccCheck.N(p0)} pu is outside [Pmin, Pmax] = [{WeccCheck.N(p.Pmin)}, {WeccCheck.N(p.Pmax)}].");
        double wg = p.PFlag == 1 ? wg0 : 1.0;
        _prl = p.PFlag == 1 ? p0 / wg : p0;
        PrefInitial = _prl;
        _soc = p.SocIni;
        if (p.Variant == ReecVariant.C)
        {
            if (_soc <= p.SocMin && p0 > 0.0) throw new ArgumentOutOfRangeException(nameof(p0), p0, $"{m}: the store starts at SOC = {WeccCheck.N(_soc)}, at or below SOCmin, while discharging.");
            if (_soc >= p.SocMax && p0 < 0.0) throw new ArgumentOutOfRangeException(nameof(p0), p0, $"{m}: the store starts at SOC = {WeccCheck.N(_soc)}, at or above SOCmax, while charging.");
        }

        // The commands this start gives must be the REGC model's equilibrium commands, inside the current limits.
        ReecOutputs o = Evaluate(new ReecInputs(v0, p0, q0, PrefInitial, QextInitial, wg0), 0.0, 0.0);
        _ipmaxLast = o.Ipmax;
        var r = new List<(string, double)>();
        Residuals(new ReecInputs(v0, p0, q0, PrefInitial, QextInitial, wg0), o, r);
        r.Add(($"{m} Ipcmd vs the REGC_A equilibrium Ip", o.Ipcmd - ipcmd0));
        r.Add(($"{m} Iqcmd vs the REGC_A equilibrium Iq", o.Iqcmd - iqcmd0));
        WeccEquilibrium.Require(m, r);
    }

    public ReecParameters Parameters => _p;

    /// <summary>The WECC model name (REEC_A, REEC_B or REEC_C).</summary>
    public string Name => _p.Name;

    /// <summary>The Pref that holds the initial operating point (P0, or P0 / speed with PFlag = 1).</summary>
    public double PrefInitial { get; }

    /// <summary>The Qext that holds the initial operating point (Q0 for Q control, V0 - Vref1 for local voltage control).</summary>
    public double QextInitial { get; }

    /// <summary>The voltage reference of the dip injection (Vref0, or the initial voltage when Vref0 = 0).</summary>
    public double Vref0 => _vref0;

    /// <summary>True while the voltage-dip logic is active.</summary>
    public bool InDip => _wasDip;

    /// <summary>The power order Pord (pu), the s5 state.</summary>
    public double Pord => _pord;

    /// <summary>The filtered terminal voltage Vt_filt (pu), the s0 state.</summary>
    public double FilteredVoltage => _vf;

    /// <summary>REEC_C: the state of charge (fraction); NaN for REEC_A / REEC_B.</summary>
    public double StateOfCharge => _p.Variant == ReecVariant.C ? _soc : double.NaN;

    /// <summary>The voltage-dip state machine: decide the dip flag at time <paramref name="t"/> from the raw terminal
    /// voltage and arm the post-dip timers (REEC_A Thld, Thld2) on the step the dip ends.</summary>
    public void UpdateDipState(double t, double vt)
    {
        bool dip = vt < _p.Vdip || vt > _p.Vup;
        if (!dip && _wasDip && _p.Variant == ReecVariant.A)
        {
            if (_p.Thld != 0.0) _thld.Arm(t, Math.Abs(_p.Thld));
            if (_p.Thld2 > 0.0) { _thld2.Arm(t, _p.Thld2); _ipmaxHeld = _ipmaxLast; }
        }
        if (dip) { _thld.Clear(); _thld2.Clear(); }
        _wasDip = dip;
    }

    /// <summary>The commands and block inputs at time <paramref name="t"/> from the current states (pure: no state
    /// changes). Call <see cref="UpdateDipState"/> for the step first.</summary>
    public ReecOutputs Evaluate(in ReecInputs u, double t, double dt)
    {
        ReecParameters p = _p;
        bool dip = _wasDip;
        double vp = Math.Max(_vf, VoltageFloorPu);

        // Reactive reference and the voltage reference.
        double qsel = p.PfFlag == 1 ? _tanPf * _pf : u.Qext;
        double qlim = Math.Clamp(qsel, p.Qmin, p.Qmax);
        double qerr = qlim - u.Qgen;
        double vsel = p.VFlag == 1
            ? Math.Clamp(p.Kqp * qerr + _s2, p.Vmin, p.Vmax)
            : qlim + (p.Variant == ReecVariant.A ? p.Vref1 : 0.0);
        vsel = Math.Clamp(vsel, p.Vmin, p.Vmax);
        double verr = vsel - _vf;

        // The dip injection and its post-dip states.
        double iqv = Math.Clamp(p.Kqv * WeccBlocks.Deadband(_vref0 - _vf, p.Dbd1, p.Dbd2), p.Iql1, p.Iqh1);
        double iqinj = dip ? iqv
            : _thld.Active(t, dt) ? (p.Thld > 0.0 ? p.Iqfrz : iqv)
            : 0.0;

        // Active path before the limit.
        double pord = _pord;
        double ipRaw = pord / vp;
        double vdl1 = p.HasVdl ? WeccBlocks.Table(p.Vq, p.Iq, _vf) : double.PositiveInfinity;
        double vdl2 = p.HasVdl ? WeccBlocks.Table(p.Vp, p.Ip, _vf) : double.PositiveInfinity;
        bool hold2 = !dip && _thld2.Active(t, dt);

        double iqmax, ipmax, iqcmd, ipcmd, ipmin;
        if (p.PqFlag == 0)
        {
            iqmax = Math.Min(vdl1, p.Imax);
            iqcmd = Math.Clamp(QPath(p, qlim, verr, vp, iqmax) + iqinj, -iqmax, iqmax);
            ipmax = hold2 ? _ipmaxHeld : Math.Min(vdl2, Math.Sqrt(Math.Max(0.0, p.Imax * p.Imax - iqcmd * iqcmd)));
            ipmin = IpMin(p, ref ipmax);
            ipcmd = Math.Clamp(ipRaw, ipmin, ipmax);
        }
        else
        {
            ipmax = hold2 ? _ipmaxHeld : Math.Min(vdl2, p.Imax);
            ipmin = IpMin(p, ref ipmax);
            ipcmd = Math.Clamp(ipRaw, ipmin, ipmax);
            iqmax = Math.Min(vdl1, Math.Sqrt(Math.Max(0.0, p.Imax * p.Imax - ipcmd * ipcmd)));
            iqcmd = Math.Clamp(QPath(p, qlim, verr, vp, iqmax) + iqinj, -iqmax, iqmax);
        }
        double psel = p.PFlag == 1 ? _prl * u.Wg : _prl;
        return new ReecOutputs(ipcmd, iqcmd, pord, ipmax, ipmin, iqmax, qlim, qerr, verr, psel, qlim / vp, dip, iqinj);
    }

    // The reactive current the Q/V path asks for (before Iqinj), with the V PI's output limited to [-Iqmax, Iqmax].
    private double QPath(ReecParameters p, double qlim, double verr, double vp, double iqmax) =>
        p.QFlag == 1 ? Math.Clamp(p.Kvp * verr + _s3, -iqmax, iqmax) : _s4;

    // Ipmin, and the state-of-charge cut-offs of REEC_C (which may also force Ipmax to 0).
    private double IpMin(ReecParameters p, ref double ipmax)
    {
        if (p.Variant != ReecVariant.C) return 0.0;
        if (_soc <= p.SocMin) ipmax = 0.0;
        double ipmin = -ipmax;
        if (_soc >= p.SocMax) ipmin = 0.0;
        return ipmin;
    }

    /// <summary>Advance every state one step of <paramref name="dt"/> from the start-of-step inputs and the outputs
    /// <see cref="Evaluate"/> gave for them.</summary>
    public void Advance(in ReecInputs u, in ReecOutputs o, double dt)
    {
        ReecParameters p = _p;
        _ipmaxLast = o.Ipmax;
        if (!o.Dip)
        {
            if (p.VFlag == 1) _s2 = WeccBlocks.Integrate(_s2, p.Kqi, o.QErr, dt, p.Vmin, p.Vmax);
            if (p.QFlag == 1) _s3 = WeccBlocks.Integrate(_s3, p.Kvi, o.VErr, dt, -o.Iqmax, o.Iqmax);
            else _s4 = WeccBlocks.Lag(_s4, o.S4Target, p.Tiq, dt);
            _pord = Math.Clamp(WeccBlocks.Lag(_pord, o.Psel, p.Tpord, dt), p.Pmin, p.Pmax);
        }
        // Bug hunt 2026-10-29, finding 29 (decided, documented): with PFlag = 1 the rate limit acts on Pref and the speed
        // multiplies afterwards (Psel = _prl * Wg), while one reading of the EPRI 3002006525 REEC_A diagram multiplies
        // Pref by speed first and rate-limits the product. The two differ only while dPmax / dPmin binds AND the speed
        // moves (type 3 wind in a fast event); with the usual +-99 pu/s limits they are the same. Kept, as the pinned
        // WECC results depend on it [VERIFY against the EPRI report figure].
        _prl = WeccBlocks.RateLimit(_prl, u.Pref, p.DPmax, p.DPmin, dt);
        if (p.Variant == ReecVariant.C) _soc -= u.Pe / p.T * dt;
        _vf = WeccBlocks.Lag(_vf, u.Vt, p.Trv, dt);
        _pf = WeccBlocks.Lag(_pf, u.Pe, p.Tp, dt);
    }

    /// <summary>The equilibrium residual of each state for the given inputs and outputs: lag input minus state,
    /// integrator input. The state of charge is not listed: an energy store delivering power is genuinely not at
    /// rest, and its integral feeds nothing back until it reaches a limit.</summary>
    public void Residuals(in ReecInputs u, in ReecOutputs o, List<(string Name, double Value)> into)
    {
        string m = _p.Name;
        into.Add(($"{m} s0 Vt_filt (Vt - Vt_filt)", u.Vt - _vf));
        into.Add(($"{m} s1 Pe_filt (Pe - Pe_filt)", u.Pe - _pf));
        if (_p.VFlag == 1) into.Add(($"{m} s2 Q PI input (Qlim - Qgen)", o.QErr));
        if (_p.QFlag == 1) into.Add(($"{m} s3 V PI input (Vsel - Vt_filt)", o.VErr));
        else into.Add(($"{m} s4 (Qlim / Vt_filt - s4)", o.S4Target - _s4));
        into.Add(($"{m} s5 Pord (Psel - Pord)", o.Psel - _pord));
        into.Add(($"{m} Pref rate limiter (Pref - Pref_rl)", u.Pref - _prl));
    }
}

/// <summary>The fail-loud equilibrium gate every WECC model runs at initialisation.</summary>
public static class WeccEquilibrium
{
    /// <summary>The largest residual (absolute, per unit or per unit per second) accepted at t = 0. The models are
    /// initialised in closed form, so a consistent start is exact to rounding (1e-15); 1e-9 leaves room for the
    /// rounding of a long chain while catching any genuine inconsistency, which is of the order of the data.</summary>
    public const double Tolerance = 1e-9;

    /// <summary>Throw, listing every residual above <see cref="Tolerance"/>, when the start is not stationary.</summary>
    public static void Require(string model, IReadOnlyList<(string Name, double Value)> residuals)
    {
        var bad = new List<string>();
        foreach ((string name, double value) in residuals)
            if (!(Math.Abs(value) <= Tolerance)) bad.Add($"{name} = {WeccCheck.N(value)}");
        if (bad.Count > 0)
            throw new InvalidOperationException($"{model}: the initial state is not an equilibrium (residuals above {WeccCheck.N(Tolerance)}): "
                + string.Join("; ", bad) + ". The operating point cannot be held by this model and data.");
    }
}
