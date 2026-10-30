using System;
using System.Collections.Generic;
using System.Numerics;

namespace GridSim.Core.Dynamics.Models.Wecc;

/// <summary>
/// DER_A, the WECC aggregated distributed energy resource model, after P. Pourbeik, "Proposal for DER_A model", memo to the
/// WECC REMTF (10/11/16, revised to 6/19/19; public on wecc.org), Fig. 2 (block diagram), Fig. 3 and the Appendix (partial
/// voltage tripping, Vrfrac logic), Table 1 (the 48 parameters, in this order). Per unit on the unit's MBASE.
/// </summary>
public sealed record DeraParameters
{
    public double Trv { get; init; } = 0.02;
    /// <summary>Frequency transducer time constant (s), at least 0.02 (Table 1).</summary>
    public double Trf { get; init; } = 0.02;
    public double Dbd1 { get; init; } = -99.0;
    public double Dbd2 { get; init; } = 99.0;
    public double Kqv { get; init; }
    /// <summary>Voltage reference of the proportional voltage control (pu); 0 = the initial terminal voltage.</summary>
    public double Vref0 { get; init; }
    public double Tp { get; init; } = 0.02;
    public double Tiq { get; init; } = 0.02;
    public double Ddn { get; init; } = 20.0;
    public double Dup { get; init; }
    public double Fdbd1 { get; init; } = -0.0006;
    public double Fdbd2 { get; init; } = 0.0006;
    public double Femax { get; init; } = 99.0;
    public double Femin { get; init; } = -99.0;
    public double Pmax { get; init; } = 1.0;
    public double Pmin { get; init; }
    public double DPmax { get; init; } = 99.0;
    public double DPmin { get; init; } = -99.0;
    public double Tpord { get; init; } = 0.02;
    public double Kpg { get; init; } = 0.1;
    public double Kig { get; init; } = 10.0;
    public double Imax { get; init; } = 1.2;
    public double Vl0 { get; init; } = 0.44;
    public double Vl1 { get; init; } = 0.49;
    public double Vh0 { get; init; } = 1.2;
    public double Vh1 { get; init; } = 1.15;
    public double Tvl0 { get; init; } = 0.16;
    public double Tvl1 { get; init; } = 0.16;
    public double Tvh0 { get; init; } = 0.16;
    public double Tvh1 { get; init; } = 0.16;
    /// <summary>Fraction of the tripped devices that recover when the voltage returns inside (vl1, vh1).</summary>
    public double Vrfrac { get; init; }
    public double Fl { get; init; } = 59.3 / 60.0;
    public double Fh { get; init; } = 60.5 / 60.0;
    public double Tfl { get; init; } = 0.16;
    public double Tfh { get; init; } = 0.16;
    public double Tg { get; init; } = 0.02;
    public double Rrpwr { get; init; } = 2.0;
    public double Tv { get; init; } = 0.02;
    /// <summary>Voltage at or below which the frequency trip sees nominal frequency (pu).</summary>
    public double Vpr { get; init; } = 0.8;
    /// <summary>0 = constant Q, 1 = constant power factor.</summary>
    public int Pflag { get; init; } = 1;
    /// <summary>0 = Q priority, 1 = P priority.</summary>
    public int Pqflag { get; init; }
    public int FreqFlag { get; init; }
    public int FtripFlag { get; init; }
    public int VtripFlag { get; init; }
    /// <summary>0 = storage (Ipmin = -Ipmax), any other value = generator (Ipmin = 0).</summary>
    public double TypeFlag { get; init; } = 1.0;
    /// <summary>Source reactance of the network interface (pu, &gt; 0); see <see cref="DerAState"/>.</summary>
    public double Xe { get; init; } = 0.25;
    public double Iqh1 { get; init; } = 1.0;
    public double Iql1 { get; init; } = -1.0;

    /// <summary>True for a storage device (typeflag = 0).</summary>
    public bool IsStorage => TypeFlag == 0.0;

    public IReadOnlyList<string> Check()
    {
        var p = new List<string>();
        const string m = "DER_A";
        WeccCheck.AtLeast(p, m, "Trv", Trv, 0.0);
        WeccCheck.AtLeast(p, m, "Trf", Trf, 0.02);   // Table 1: must be >= 0.02 s
        WeccCheck.Finite(p, m, "dbd1", Dbd1);
        WeccCheck.Finite(p, m, "dbd2", Dbd2);
        if (Dbd1 > 0.0) p.Add($"{m} dbd1 = {WeccCheck.N(Dbd1)}: must be 0 or negative");
        if (Dbd2 < 0.0) p.Add($"{m} dbd2 = {WeccCheck.N(Dbd2)}: must be 0 or positive");
        WeccCheck.Finite(p, m, "Kqv", Kqv);
        WeccCheck.AtLeast(p, m, "Vref0", Vref0, 0.0);
        WeccCheck.AtLeast(p, m, "Tp", Tp, 0.0);
        WeccCheck.AtLeast(p, m, "Tiq", Tiq, 0.0);
        WeccCheck.AtLeast(p, m, "Ddn", Ddn, 0.0);
        WeccCheck.AtLeast(p, m, "Dup", Dup, 0.0);
        WeccCheck.Finite(p, m, "fdbd1", Fdbd1);
        WeccCheck.Finite(p, m, "fdbd2", Fdbd2);
        if (Fdbd1 > 0.0) p.Add($"{m} fdbd1 = {WeccCheck.N(Fdbd1)}: must be 0 or negative");
        if (Fdbd2 < 0.0) p.Add($"{m} fdbd2 = {WeccCheck.N(Fdbd2)}: must be 0 or positive");
        WeccCheck.AtLeast(p, m, "femax", Femax, 0.0);
        WeccCheck.Finite(p, m, "femin", Femin);
        if (Femin > 0.0) p.Add($"{m} femin = {WeccCheck.N(Femin)}: must be 0 or negative");
        WeccCheck.Finite(p, m, "Pmax", Pmax);
        WeccCheck.Finite(p, m, "Pmin", Pmin);
        WeccCheck.Ordered(p, m, "Pmin", Pmin, "Pmax", Pmax);
        WeccCheck.Above(p, m, "dPmax", DPmax, 0.0);
        if (!double.IsFinite(DPmin) || !(DPmin < 0.0)) p.Add($"{m} dPmin = {WeccCheck.N(DPmin)}: must be a finite number below 0");
        WeccCheck.AtLeast(p, m, "Tpord", Tpord, 0.0);
        WeccCheck.Finite(p, m, "Kpg", Kpg);
        WeccCheck.Finite(p, m, "Kig", Kig);
        if (Tpord == 0.0 && Tp == 0.0 && !(Kig > 0.0)) p.Add($"{m} Kig = {WeccCheck.N(Kig)}: with Tpord = Tp = 0 the power loop needs Kig above 0 (memo item g)");
        WeccCheck.Above(p, m, "Imax", Imax, 0.0);
        foreach ((string n, double v) in new[] { ("vl0", Vl0), ("vl1", Vl1), ("vh0", Vh0), ("vh1", Vh1) }) WeccCheck.Finite(p, m, n, v);
        WeccCheck.Ordered(p, m, "vl0", Vl0, "vl1", Vl1);
        WeccCheck.StrictlyOrdered(p, m, "vl1", Vl1, "vh1", Vh1);
        WeccCheck.Ordered(p, m, "vh1", Vh1, "vh0", Vh0);
        foreach ((string n, double v) in new[] { ("tvl0", Tvl0), ("tvl1", Tvl1), ("tvh0", Tvh0), ("tvh1", Tvh1), ("tfl", Tfl), ("tfh", Tfh) }) WeccCheck.AtLeast(p, m, n, v, 0.0);
        if (!double.IsFinite(Vrfrac) || Vrfrac < 0.0 || Vrfrac > 1.0) p.Add($"{m} Vrfrac = {WeccCheck.N(Vrfrac)}: must be in [0, 1]");
        WeccCheck.Finite(p, m, "fl", Fl);
        WeccCheck.Finite(p, m, "fh", Fh);
        WeccCheck.StrictlyOrdered(p, m, "fl", Fl, "fh", Fh);
        if (FtripFlag == 1 && double.IsFinite(Tfl) && double.IsFinite(Trf) && !(Tfl > Trf)) p.Add($"{m} tfl = {WeccCheck.N(Tfl)}: must exceed Trf = {WeccCheck.N(Trf)} (Table 1)");
        WeccCheck.AtLeast(p, m, "Tg", Tg, 0.0);
        WeccCheck.Above(p, m, "rrpwr", Rrpwr, 0.0);
        WeccCheck.AtLeast(p, m, "Tv", Tv, 0.0);
        WeccCheck.Finite(p, m, "Vpr", Vpr);
        WeccCheck.Flag(p, m, "Pflag", Pflag, 0, 1);
        WeccCheck.Flag(p, m, "Pqflag", Pqflag, 0, 1);
        WeccCheck.Flag(p, m, "Freq_flag", FreqFlag, 0, 1);
        WeccCheck.Flag(p, m, "Ftripflag", FtripFlag, 0, 1);
        WeccCheck.Flag(p, m, "Vtripflag", VtripFlag, 0, 1);
        WeccCheck.Finite(p, m, "typeflag", TypeFlag);
        WeccCheck.Above(p, m, "Xe", Xe, 0.0);
        WeccCheck.Finite(p, m, "Iqh1", Iqh1);
        WeccCheck.Finite(p, m, "Iql1", Iql1);
        WeccCheck.Ordered(p, m, "Iql1", Iql1, "Iqh1", Iqh1);
        return p;
    }
}

/// <summary>
/// DER_A dynamics (see <see cref="DeraParameters"/>), all states advanced from start-of-step inputs (<see cref="WeccBlocks"/>):
/// <code>
///   s0 Vt_filt = lag(Vt, Trv);  vp = max(Vt_filt, 0.01);  Iqv = clamp(Kqv db(Vref0 - Vt_filt, dbd1, dbd2), Iql1, Iqh1)
///   s1 = lag(Pord, Tp)   (the power feedback is the power order, memo item g)
///   s2 = lag((Pflag ? tan(pf0) s1 : Qref) / vp, Tiq);   Iqcmd = clamp(s2 + Iqv, -Iqmax, Iqmax)
///   s5 Frq_filt = lag(f, Trf);  df = db(1 - Frq_filt, fdbd1, fdbd2);  droop = Ddn min(df, 0) + Dup max(df, 0)
///   s6: PI(Kpg, Kig) on clamp(Pref - s1 + droop, femin, femax) in [Pmin, Pmax];  input = Freq_flag ? PI out : Pref
///   s7 = that input rate limited to [dPmin, dPmax];  s8 Pord = lag(s7, Tpord) in [Pmin, Pmax];  Ipcmd = clamp(Pord / vp, Ipmin, Ipmax)
///   current limit: Q priority Iqmax = Imax, Ipmax = sqrt(Imax^2 - Iqcmd^2); P priority Ipmax = Imax, Iqmax = sqrt(Imax^2 - Ipcmd^2);
///                  Ipmin = -Ipmax for storage (typeflag = 0), else 0
///   s4 = lag(Vrfrac multiplier of Vt_filt, Tv);  m = Vtripflag ? s4 : 1;  m = 0 once the frequency trip has acted
///   s3 Iq = lag(m Iqcmd, Tg);  s9 Ip = lag(m Ipcmd, Tg), rise limited to rrpwr while Ip &gt;= 0 (and the charging rise to
///   -rrpwr while Ip &lt;= 0, memo footnote 2)
/// </code>
/// <para><b>Partial voltage tripping</b> is the memo's Appendix pseudo code, on Vt_filt (memo note 10), with the low side
/// (vl0, vl1, tvl0, tvl1, Vmin) and the same logic on the high side (vh1, vh0, tvh1, tvh0, Vmax); the two multipliers are
/// multiplied (DECISIONS D26). A timer "exceeds" its setting when the time since it started is above the setting by more
/// than a millionth of a step (the engine's event-time convention). <b>Frequency tripping</b> (Ftripflag): the filtered
/// frequency, read as nominal while Vt_filt &lt;= Vpr, below fl for more than tfl or above fh for more than tfh trips the
/// whole model for the rest of the run (its current commands become 0 and the Tg lags take its current to 0).</para>
/// <para><b>Network interface.</b> The memo places a voltage source behind Xe at the terminal as a numerical device; with
/// its EMF recomputed from the model's currents it injects the model's current at the solved voltage. GridSim solves the
/// current-source interface to its algebraic fixed point (1e-10 pu) every step, so the injected current is exactly the
/// model's current and Xe has no effect; it is range-checked and named in the DYR notes (DECISIONS D24).</para>
/// <para><b>Frequency</b> is the backward difference of the terminal voltage angle over the step, <c>1 + wrap(dtheta) /
/// (2 pi f0 dt)</c>, into the Trf transducer lag. <b>Initialisation</b> from (P0, Q0, V0): Pref = P0 and every P state at
/// P0, Qref = Q0 (or tan(pf0) = Q0 / P0), s2 = Q0 / V0, the multiplier at 1; refused unless vl1 &lt; V0 &lt; vh1 (memo note 9),
/// V0 - Vref0 is inside the deadband when Kqv is not zero (memo note 4a: GridSim refuses rather than move Vref0), P0 is
/// inside [Pmin, Pmax] and the initial currents inside the current limit; then every state residual must be under 1e-9.</para>
/// </summary>
public sealed class DerAState : IRmsConverterModel
{
    private readonly RmsConverterUnit _u;
    private readonly DeraParameters _p;
    private readonly double _r, _ws, _vref0, _tanPf, _qref, _pref;
    private double _s0, _s1, _s2, _s3, _s4, _s5, _s6, _s7, _s8, _s9;
    private double _angle, _freqAngle;
    // Partial voltage tripping (low and high side) and frequency tripping.
    private bool _run1, _run2, _cnt1, _cnt2, _run1h, _run2h, _cnt1h, _cnt2h, _runFl, _runFh, _tripped;
    private double _start1, _start2, _start1h, _start2h, _startFl, _startFh, _vmin, _vmax;

    public DerAState(RmsConverterUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        _p = unit.Wecc?.Der ?? throw new ArgumentException("The unit carries no DER_A model.", nameof(unit));
        IReadOnlyList<string> bad = unit.Wecc.Check();
        if (bad.Count > 0) throw new ArgumentException($"WECC unit '{unit.Name}': " + string.Join("; ", bad), nameof(unit));
        _u = unit;
        _r = unit.RatingPu;
        if (!(_r > 0.0) || !double.IsFinite(_r)) throw new ArgumentOutOfRangeException(nameof(unit), _r, $"DER_A unit '{unit.Name}': the rating (MBASE / system base) must be a finite number above 0.");
        _ws = 2.0 * Math.PI * unit.Parameters.NominalHz;
        double v0 = unit.V0.Magnitude, p0 = unit.P0 / _r, q0 = unit.Q0 / _r;
        string who = $"DER_A unit '{unit.Name}' at bus {unit.Bus}{(unit.Wecc!.Source.Length > 0 ? " (" + unit.Wecc.Source + ")" : "")}";
        if (!(v0 > _p.Vl1 && v0 < _p.Vh1))
            throw new InvalidOperationException($"{who}: the initial terminal voltage {WeccCheck.N(v0)} pu must be above vl1 = {WeccCheck.N(_p.Vl1)} and below vh1 = {WeccCheck.N(_p.Vh1)} (memo note 9); otherwise the model starts partly tripped.");
        _vref0 = _p.Vref0 == 0.0 ? v0 : _p.Vref0;
        if (_p.Kqv != 0.0 && (_vref0 - v0 < _p.Dbd1 || _vref0 - v0 > _p.Dbd2))
            throw new InvalidOperationException($"{who}: Vref0 - V0 = {WeccCheck.N(_vref0 - v0)} pu is outside the deadband [{WeccCheck.N(_p.Dbd1)}, {WeccCheck.N(_p.Dbd2)}] with Kqv = {WeccCheck.N(_p.Kqv)}, "
                + "so the voltage control would act at the start. The memo has the software move Vref0 to V0 with a warning; GridSim refuses instead: give Vref0 = 0 (the initial voltage) or a value inside the deadband.");
        if (_p.Pflag == 1)
        {
            if (p0 == 0.0) throw new InvalidOperationException($"{who}: constant power factor control (Pflag = 1) needs a non-zero initial active power.");
            _tanPf = q0 / p0;
        }
        if (p0 < _p.Pmin || p0 > _p.Pmax)
            throw new InvalidOperationException($"{who}: the initial active power {WeccCheck.N(p0)} pu is outside [Pmin, Pmax] = [{WeccCheck.N(_p.Pmin)}, {WeccCheck.N(_p.Pmax)}].");
        double vp = Math.Max(v0, Reec.VoltageFloorPu);
        _qref = q0;
        _pref = p0;
        _s0 = v0; _s1 = p0; _s2 = q0 / vp; _s4 = 1.0; _s5 = 1.0; _s6 = p0; _s7 = p0; _s8 = p0;
        (double ipcmd, double iqcmd, double ipmax, double ipmin, double iqmax) = Commands(_s0);
        _s3 = iqcmd; _s9 = ipcmd;
        var r = new List<(string, double)>();
        if (Math.Abs(q0 / vp) > iqmax + WeccEquilibrium.Tolerance) r.Add(("DER_A initial Iq outside the current limit", Math.Abs(q0 / vp) - iqmax));
        if (p0 / vp > ipmax + WeccEquilibrium.Tolerance || p0 / vp < ipmin - WeccEquilibrium.Tolerance) r.Add(("DER_A initial Ip outside the current limit", p0 / vp));
        Residuals(v0, 1.0, r);
        r.Add(("DER_A Ip vs P0 / V0", _s9 - p0 / vp));
        r.Add(("DER_A Iq vs Q0 / V0", _s3 - q0 / vp));
        try { WeccEquilibrium.Require("DER_A", r); }
        catch (InvalidOperationException ex) { throw new InvalidOperationException($"{who}: {ex.Message}", ex); }
        _angle = DetMath.Phase(unit.V0);
        _freqAngle = _angle;
        _vmin = v0; _vmax = v0;
    }

    public RmsConverterUnit Unit => _u;
    public DeraParameters Parameters => _p;

    public double ActiveCurrent { get; private set; }
    public double ReactiveCurrent { get; private set; }

    /// <summary>DER_A has no voltage-dip freeze; "in dip" is reported while the partial-trip multiplier is below 1 or the
    /// model has tripped, so a trace shows the ride-through state.</summary>
    public bool InDip => _tripped || _s4 < 1.0 - 1e-12 || Multiplier(_s0) < 1.0;

    /// <summary>True once the frequency trip has acted.</summary>
    public bool FrequencyTripped => _tripped;

    /// <summary>The voltage-trip multiplier state s4.</summary>
    public double TripMultiplier => _s4;

    /// <summary>The filtered frequency s5 (pu).</summary>
    public double FilteredFrequency => _s5;

    /// <summary>The power order Pord (s8).</summary>
    public double PowerOrder => _s8;

    // The current commands and limits from the current states (Vt_filt given).
    private (double Ipcmd, double Iqcmd, double Ipmax, double Ipmin, double Iqmax) Commands(double vf)
    {
        DeraParameters p = _p;
        double vp = Math.Max(vf, Reec.VoltageFloorPu);
        double iqv = Math.Clamp(p.Kqv * WeccBlocks.Deadband(_vref0 - vf, p.Dbd1, p.Dbd2), p.Iql1, p.Iqh1);
        double ipRaw = _s8 / vp;
        double iqmax, ipmax, iqcmd, ipcmd, ipmin;
        if (p.Pqflag == 0)
        {
            iqmax = p.Imax;
            iqcmd = Math.Clamp(_s2 + iqv, -iqmax, iqmax);
            ipmax = Math.Sqrt(Math.Max(0.0, p.Imax * p.Imax - iqcmd * iqcmd));
            ipmin = p.IsStorage ? -ipmax : 0.0;
            ipcmd = Math.Clamp(ipRaw, ipmin, ipmax);
        }
        else
        {
            ipmax = p.Imax;
            ipmin = p.IsStorage ? -ipmax : 0.0;
            ipcmd = Math.Clamp(ipRaw, ipmin, ipmax);
            iqmax = Math.Sqrt(Math.Max(0.0, p.Imax * p.Imax - ipcmd * ipcmd));
            iqcmd = Math.Clamp(_s2 + iqv, -iqmax, iqmax);
        }
        return (ipcmd, iqcmd, ipmax, ipmin, iqmax);
    }

    // The active power path: the PI error and output, and the rate limiter's input.
    private (double E, double PiOut, double RlIn) PowerPath()
    {
        DeraParameters p = _p;
        double df = WeccBlocks.Deadband(1.0 - _s5, p.Fdbd1, p.Fdbd2);
        double droop = p.Ddn * Math.Min(df, 0.0) + p.Dup * Math.Max(df, 0.0);
        double e = Math.Clamp(_pref - _s1 + droop, p.Femin, p.Femax);
        double pi = Math.Clamp(p.Kpg * e + _s6, p.Pmin, p.Pmax);
        return (e, pi, p.FreqFlag == 1 ? pi : _pref);
    }

    /// <summary>The Vrfrac multiplier (memo Appendix) at filtered voltage <paramref name="vf"/> for the current timer and
    /// counter states, low side times high side (pure).</summary>
    public double Multiplier(double vf)
    {
        DeraParameters p = _p;
        double low;
        double vmin = Math.Max(_vmin, p.Vl0);
        double dl = p.Vl1 - p.Vl0;
        if (vf <= p.Vl0 || _cnt2) low = 0.0;
        else if (vf <= p.Vl1 && !_cnt1) low = dl > 0.0 ? (vf - p.Vl0) / dl : 0.0;
        else if (vf <= p.Vl1) low = dl > 0.0 ? ((vmin - p.Vl0) + p.Vrfrac * (vf - vmin)) / dl : 0.0;
        else if (!_cnt1) low = 1.0;
        else low = dl > 0.0 ? p.Vrfrac * ((p.Vl1 - vmin) / dl) + ((vmin - p.Vl0) / dl) : p.Vrfrac;
        double high;
        double vmax = Math.Min(_vmax, p.Vh0);
        double dh = p.Vh0 - p.Vh1;
        if (vf >= p.Vh0 || _cnt2h) high = 0.0;
        else if (vf >= p.Vh1 && !_cnt1h) high = dh > 0.0 ? (p.Vh0 - vf) / dh : 0.0;
        else if (vf >= p.Vh1) high = dh > 0.0 ? ((p.Vh0 - vmax) + p.Vrfrac * (vmax - vf)) / dh : 0.0;
        else if (!_cnt1h) high = 1.0;
        else high = dh > 0.0 ? p.Vrfrac * ((vmax - p.Vh1) / dh) + ((p.Vh0 - vmax) / dh) : p.Vrfrac;
        return low * high;
    }

    // The memo's timer, counter and Vmin/Vmax updates at time t (after the multiplier has been read for the step).
    private void UpdateVoltageTrip(double t, double vf, double dt)
    {
        DeraParameters p = _p;
        double eps = dt * MultiMachineSubtransient.StageTimeToleranceSteps;
        if (vf < p.Vl1 && !_run1) { _run1 = true; _start1 = t; } else if (vf > p.Vl1 && _run1) _run1 = false;
        if (vf < p.Vl0 && !_run2) { _run2 = true; _start2 = t; } else if (vf > p.Vl0 && _run2) _run2 = false;
        if (vf > p.Vh1 && !_run1h) { _run1h = true; _start1h = t; } else if (vf < p.Vh1 && _run1h) _run1h = false;
        if (vf > p.Vh0 && !_run2h) { _run2h = true; _start2h = t; } else if (vf < p.Vh0 && _run2h) _run2h = false;
        if (_vmin <= p.Vl0) _vmin = p.Vl0;
        if (_vmax >= p.Vh0) _vmax = p.Vh0;
        if (!_cnt1 && _run1 && t - _start1 > p.Tvl1 + eps) { _cnt1 = true; _vmin = vf; }
        if (!_cnt2 && _run2 && t - _start2 > p.Tvl0 + eps) _cnt2 = true;
        if (!_cnt1h && _run1h && t - _start1h > p.Tvh1 + eps) { _cnt1h = true; _vmax = vf; }
        if (!_cnt2h && _run2h && t - _start2h > p.Tvh0 + eps) _cnt2h = true;
    }

    private void UpdateFrequencyTrip(double t, double dt)
    {
        DeraParameters p = _p;
        if (p.FtripFlag != 1 || _tripped) return;
        double eps = dt * MultiMachineSubtransient.StageTimeToleranceSteps;
        double f = _s0 <= p.Vpr ? 1.0 : _s5;
        if (f < p.Fl && !_runFl) { _runFl = true; _startFl = t; } else if (f >= p.Fl && _runFl) _runFl = false;
        if (f > p.Fh && !_runFh) { _runFh = true; _startFh = t; } else if (f <= p.Fh && _runFh) _runFh = false;
        if ((_runFl && t - _startFl > p.Tfl + eps) || (_runFh && t - _startFh > p.Tfh + eps)) _tripped = true;
    }

    private Complex Injection(Complex v, out double angle)
    {
        double vm = v.Magnitude;
        angle = vm > RmsConverterState.PllLockMinVoltagePu ? DetMath.Phase(v) : _angle;
        return new Complex(_s9 * _r, -_s3 * _r) * DetMath.FromPolar(1.0, angle);
    }

    public Complex TrialCurrent(Complex v) => Injection(v, out _);

    public Complex Commit(Complex v)
    {
        Complex i = Injection(v, out double angle);
        _angle = angle;
        ActiveCurrent = _s9 * _r;
        ReactiveCurrent = _s3 * _r;
        return i;
    }

    public Complex TerminalCurrent(Complex injection, Complex v) => injection;

    public void Step(double t, Complex v, Complex[] nodeVoltages, double dt)
    {
        DeraParameters p = _p;
        double vm = v.Magnitude;
        // Frequency: the terminal angle step over this step, wrapped; a collapsed terminal reads nominal.
        double fInst = 1.0;
        if (vm > RmsConverterState.PllLockMinVoltagePu)
        {
            double th = DetMath.Phase(v);
            double d = th - _freqAngle;
            if (d > Math.PI) d -= 2.0 * Math.PI;
            else if (d <= -Math.PI) d += 2.0 * Math.PI;
            fInst = 1.0 + d / (_ws * dt);
            _freqAngle = th;
        }
        // Everything below reads the start-of-step states.
        (double ipcmd, double iqcmd, _, _, double iqmax) = Commands(_s0);
        (double e, _, double rlIn) = PowerPath();
        double vp = Math.Max(_s0, Reec.VoltageFloorPu);
        double qsel = p.Pflag == 1 ? _tanPf * _s1 : _qref;
        double mult = Multiplier(_s0);
        double m = _tripped ? 0.0 : (p.VtripFlag == 1 ? _s4 : 1.0);
        UpdateVoltageTrip(t, _s0, dt);
        UpdateFrequencyTrip(t, dt);

        // Advance (simultaneous: every target above is from the start-of-step states).
        double ipNext = WeccBlocks.Lag(_s9, m * ipcmd, p.Tg, dt);
        double dIp = ipNext - _s9;
        if (dIp > p.Rrpwr * dt && _s9 >= 0.0) ipNext = _s9 + p.Rrpwr * dt;
        else if (dIp < -p.Rrpwr * dt && _s9 <= 0.0) ipNext = _s9 - p.Rrpwr * dt;
        _s9 = ipNext;
        _s3 = WeccBlocks.Lag(_s3, m * iqcmd, p.Tg, dt);
        _s4 = WeccBlocks.Lag(_s4, mult, p.Tv, dt);
        _s2 = WeccBlocks.Lag(_s2, qsel / vp, p.Tiq, dt);
        _s6 = WeccBlocks.Integrate(_s6, p.Kig, e, dt, p.Pmin, p.Pmax);
        double s8 = Math.Clamp(WeccBlocks.Lag(_s8, _s7, p.Tpord, dt), p.Pmin, p.Pmax);
        _s7 = WeccBlocks.RateLimit(_s7, rlIn, p.DPmax, p.DPmin, dt);
        _s1 = WeccBlocks.Lag(_s1, _s8, p.Tp, dt);
        _s8 = s8;
        _s5 = WeccBlocks.Lag(_s5, fInst, p.Trf, dt);
        _s0 = WeccBlocks.Lag(_s0, vm, p.Trv, dt);
        _ = iqmax;
    }

    /// <summary>The equilibrium residual of each state for terminal voltage <paramref name="vt"/> and frequency
    /// <paramref name="f"/>.</summary>
    public void Residuals(double vt, double f, List<(string Name, double Value)> into)
    {
        (double ipcmd, double iqcmd, _, _, _) = Commands(_s0);
        (double e, _, double rlIn) = PowerPath();
        double vp = Math.Max(_s0, Reec.VoltageFloorPu);
        double qsel = _p.Pflag == 1 ? _tanPf * _s1 : _qref;
        into.Add(("DER_A s0 (Vt - Vt_filt)", vt - _s0));
        into.Add(("DER_A s1 (Pord - s1)", _s8 - _s1));
        into.Add(("DER_A s2 (Qref / Vt_filt - s2)", qsel / vp - _s2));
        into.Add(("DER_A s3 (Iqcmd - Iq)", iqcmd - _s3));
        into.Add(("DER_A s4 (multiplier - s4)", Multiplier(_s0) - _s4));
        into.Add(("DER_A s5 (f - Frq_filt)", f - _s5));
        into.Add(("DER_A s6 PI input", e));
        into.Add(("DER_A s7 (input - Pref_rl)", rlIn - _s7));
        into.Add(("DER_A s8 (Pref_rl - Pord)", _s7 - _s8));
        into.Add(("DER_A s9 (Ipcmd - Ip)", ipcmd - _s9));
    }
}
