using System;
using System.Collections.Generic;
using System.Numerics;

namespace GridSim.Core.Dynamics.Models.Wecc;

/// <summary>
/// REEC_D: the renewable energy electrical control model of the WECC REMWG memo Rev. 27b (Pourbeik, 2/16/23, section 3.1,
/// Figure 3 and Table 3; approved by the WECC MVS in August 2020): REEC_A with ten-point VDLq / VDLp tables (Iqmin = Iqmax
/// when Iqmax is negative; Ipmin = -Ke Ipmax), local current or reactive-droop compensation (rc, Xc, Kc, Tr1, Vcmpflag),
/// an always-active reactive injection arm with the Iqcmd_bl hold (Thld), the Ipcmd/Ipmax hold (Thld2), converter blocking
/// (vblkl, vblkh, Tblk_delay), qvmax/qvmin on the incoming reference, Paux, the dip flag from Vt_filt, and the linked
/// non-windup PI limits of the memo's page 10-11 pseudo code. Per unit on MBASE; reactive current exporting positive.
///
/// <para>GridSim reads a <c>'REEC_D'</c> record (no public source gives the PSS/E record order; DECISIONS D30): six flags
/// PfFlag VFlag QFlag PFlag PqFlag Vcmpflag, then the 78 values in the order of the EPRI REGC_B/REEC_D test protocol's
/// Table 5 (EPRI memo to WECC REMTF, 8/17/2020): mvab Vdip Vup Trv dbd1 dbd2 Kqv Iqh1 Iql1 Vref0 Iqfrz Thld Thld2 Tp qvmax
/// qvmin Vmax Vmin Kqp Kqi Kvp Kvi Vref1 Tiq dPmax dPmin Pmax Pmin Imax Tpord rc Xc Tr1 Kc Ke vq1 iq1 .. vq10 iq10 vp1 ip1 ..
/// vp10 ip10 vblkl vblkh Tblk_delay.</para>
/// </summary>
public sealed record ReecdParameters
{
    public int PfFlag { get; init; }
    public int VFlag { get; init; } = 1;
    public int QFlag { get; init; }
    /// <summary>1 = the power reference is multiplied by the generator speed (wind); only 0 is accepted with no wind model.</summary>
    public int PFlag { get; init; }
    /// <summary>0 = reactive current priority, 1 = active current priority.</summary>
    public int PqFlag { get; init; }
    /// <summary>1 = current compensation |Vt - (rc + j Xc) It|, 0 = reactive droop Vt + Kc Qgen.</summary>
    public int VcmpFlag { get; init; }
    /// <summary>PSLF model MVA base: 0 or the unit's MBASE (GridSim works on MBASE).</summary>
    public double Mvab { get; init; }
    public double Vdip { get; init; } = 0.9;
    public double Vup { get; init; } = 1.1;
    public double Trv { get; init; } = 0.02;
    public double Dbd1 { get; init; } = -0.05;
    public double Dbd2 { get; init; } = 0.05;
    public double Kqv { get; init; } = 2.0;
    public double Iqh1 { get; init; } = 1.05;
    public double Iql1 { get; init; } = -1.05;
    /// <summary>0 = the initial terminal voltage (PSS/E convention, DECISIONS D9).</summary>
    public double Vref0 { get; init; }
    public double Iqfrz { get; init; }
    /// <summary>&gt; 0: Iqcmd_bl held at its last dip value for Thld s; &lt; 0: held at Iqfrz for -Thld s; 0: no hold.</summary>
    public double Thld { get; init; }
    /// <summary>Time (s) Ipcmd and Ipmax stay at their last dip values after a dip.</summary>
    public double Thld2 { get; init; }
    public double Tp { get; init; } = 0.05;
    /// <summary>Upper limit of the incoming reactive (or voltage) reference, applied whatever the flags.</summary>
    public double Qvmax { get; init; } = 1.0;
    public double Qvmin { get; init; } = -1.0;
    public double Vmax { get; init; } = 1.1;
    public double Vmin { get; init; } = 0.9;
    public double Kqp { get; init; }
    public double Kqi { get; init; } = 0.1;
    public double Kvp { get; init; }
    public double Kvi { get; init; } = 40.0;
    public double Vref1 { get; init; }
    public double Tiq { get; init; } = 0.02;
    public double DPmax { get; init; } = 99.0;
    public double DPmin { get; init; } = -99.0;
    public double Pmax { get; init; } = 1.0;
    public double Pmin { get; init; }
    public double Imax { get; init; } = 1.3;
    public double Tpord { get; init; } = 0.02;
    public double Rc { get; init; }
    public double Xc { get; init; }
    public double Tr1 { get; init; } = 0.02;
    public double Kc { get; init; }
    /// <summary>Ipmin = -Ke Ipmax: 0 a generator, (0, 1] a store.</summary>
    public double Ke { get; init; }
    /// <summary>VDLq: ten (voltage, reactive current limit) points; the currents may be negative.</summary>
    public IReadOnlyList<double> Vq { get; init; } = new[] { 0.0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 1.5 };
    public IReadOnlyList<double> Iq { get; init; } = new[] { 1.3, 1.3, 1.3, 1.3, 1.3, 1.3, 1.3, 1.3, 1.3, 1.3 };
    /// <summary>VDLp: ten (voltage, active current limit) points; the currents must be 0 or positive.</summary>
    public IReadOnlyList<double> Vp { get; init; } = new[] { 0.0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 1.5 };
    public IReadOnlyList<double> Ip { get; init; } = new[] { 1.3, 1.3, 1.3, 1.3, 1.3, 1.3, 1.3, 1.3, 1.3, 1.3 };
    /// <summary>Vt_filt at or below which the converter blocks.</summary>
    public double Vblkl { get; init; } = -1.0;
    /// <summary>Vt_filt at or above which the converter blocks.</summary>
    public double Vblkh { get; init; } = 99.0;
    /// <summary>Delay (s) after the voltage returns inside (vblkl, vblkh) before the limits are released.</summary>
    public double TblkDelay { get; init; }

    /// <summary>Every problem with these values (empty when usable).</summary>
    public IReadOnlyList<string> Check()
    {
        var p = new List<string>();
        const string m = "REEC_D";
        WeccCheck.Flag(p, m, "PfFlag", PfFlag, 0, 1);
        WeccCheck.Flag(p, m, "VFlag", VFlag, 0, 1);
        WeccCheck.Flag(p, m, "QFlag", QFlag, 0, 1);
        WeccCheck.Flag(p, m, "PFlag", PFlag, 0, 1);
        WeccCheck.Flag(p, m, "PqFlag", PqFlag, 0, 1);
        WeccCheck.Flag(p, m, "Vcmpflag", VcmpFlag, 0, 1);
        WeccCheck.AtLeast(p, m, "mvab", Mvab, 0.0);
        WeccCheck.Finite(p, m, "Vdip", Vdip);
        WeccCheck.Finite(p, m, "Vup", Vup);
        WeccCheck.StrictlyOrdered(p, m, "Vdip", Vdip, "Vup", Vup);
        WeccCheck.AtLeast(p, m, "Trv", Trv, 0.0);
        WeccCheck.Finite(p, m, "dbd1", Dbd1);
        WeccCheck.Finite(p, m, "dbd2", Dbd2);
        if (double.IsFinite(Dbd1) && Dbd1 > 0.0) p.Add($"{m} dbd1 = {WeccCheck.N(Dbd1)}: the lower deadband threshold must be 0 or negative");
        if (double.IsFinite(Dbd2) && Dbd2 < 0.0) p.Add($"{m} dbd2 = {WeccCheck.N(Dbd2)}: the upper deadband threshold must be 0 or positive");
        WeccCheck.Finite(p, m, "Kqv", Kqv);
        WeccCheck.Finite(p, m, "Iqh1", Iqh1);
        WeccCheck.Finite(p, m, "Iql1", Iql1);
        WeccCheck.Ordered(p, m, "Iql1", Iql1, "Iqh1", Iqh1);
        WeccCheck.AtLeast(p, m, "Vref0", Vref0, 0.0);
        WeccCheck.Finite(p, m, "Iqfrz", Iqfrz);
        WeccCheck.Finite(p, m, "Thld", Thld);
        WeccCheck.AtLeast(p, m, "Thld2", Thld2, 0.0);
        WeccCheck.AtLeast(p, m, "Tp", Tp, 0.0);
        WeccCheck.Finite(p, m, "qvmax", Qvmax);
        WeccCheck.Finite(p, m, "qvmin", Qvmin);
        WeccCheck.Ordered(p, m, "qvmin", Qvmin, "qvmax", Qvmax);
        WeccCheck.Finite(p, m, "Vmax", Vmax);
        WeccCheck.Finite(p, m, "Vmin", Vmin);
        WeccCheck.Ordered(p, m, "Vmin", Vmin, "Vmax", Vmax);
        WeccCheck.Finite(p, m, "Kqp", Kqp);
        WeccCheck.Finite(p, m, "Kqi", Kqi);
        WeccCheck.Finite(p, m, "Kvp", Kvp);
        WeccCheck.Finite(p, m, "Kvi", Kvi);
        WeccCheck.Finite(p, m, "Vref1", Vref1);
        WeccCheck.AtLeast(p, m, "Tiq", Tiq, 0.0);
        WeccCheck.Above(p, m, "dPmax", DPmax, 0.0);
        if (!double.IsFinite(DPmin) || !(DPmin < 0.0)) p.Add($"{m} dPmin = {WeccCheck.N(DPmin)}: must be a finite number below 0");
        WeccCheck.Finite(p, m, "Pmax", Pmax);
        WeccCheck.Finite(p, m, "Pmin", Pmin);
        WeccCheck.Ordered(p, m, "Pmin", Pmin, "Pmax", Pmax);
        WeccCheck.Above(p, m, "Imax", Imax, 0.0);
        WeccCheck.AtLeast(p, m, "Tpord", Tpord, 0.0);
        WeccCheck.AtLeast(p, m, "rc", Rc, 0.0);
        WeccCheck.Finite(p, m, "Xc", Xc);
        WeccCheck.AtLeast(p, m, "Tr1", Tr1, 0.0);
        WeccCheck.Finite(p, m, "Kc", Kc);
        if (!double.IsFinite(Ke) || Ke < 0.0 || Ke > 1.0) p.Add($"{m} Ke = {WeccCheck.N(Ke)}: must be in [0, 1] (the memo: Ke cannot be negative or greater than 1)");
        if (Vq.Count != 10 || Iq.Count != 10 || Vp.Count != 10 || Ip.Count != 10) p.Add($"{m}: VDLq and VDLp have exactly ten (voltage, current) points each");
        else
        {
            WeccCheck.Table(p, m, "vq", Vq, "iq", Iq);
            WeccCheck.Table(p, m, "vp", Vp, "ip", Ip);
            for (int i = 0; i < 10; i++)
                if (double.IsFinite(Ip[i]) && Ip[i] < 0.0) p.Add($"{m} ip{i + 1} = {WeccCheck.N(Ip[i])}: the VDLp currents must be 0 or positive (the memo)");
        }
        WeccCheck.Finite(p, m, "vblkl", Vblkl);
        WeccCheck.Finite(p, m, "vblkh", Vblkh);
        WeccCheck.StrictlyOrdered(p, m, "vblkl", Vblkl, "vblkh", Vblkh);
        WeccCheck.AtLeast(p, m, "Tblk_delay", TblkDelay, 0.0);
        return p;
    }
}

/// <summary>
/// REEC_D (see <see cref="ReecdParameters"/>). The equations, beyond REEC_A (<see cref="Reec"/>):
/// <code>
///   s0 Vt_filt = lag(Vt, Trv);  Voltage_dip = Vt_filt &lt; Vdip or Vt_filt &gt; Vup     (from Vt_filt, memo item 6)
///   s6 Vcomp   = lag(Vcmpflag ? |Vt - (rc + j Xc) It| : Vt + Kc Qgen, Tr1)
///   Qsel = PfFlag ? tan(pfaref) Pe_filt : Qext;  Qlim = clamp(Qsel, qvmin, qvmax)
///   Vsel = clamp(VFlag ? Kqp (Qlim - Qgen) + s2 : Vref1 + Qlim, Vmin, Vmax)
///   QFlag 1: clamp(Kvp (Vsel - Vcomp) + s3, Iqmin, Iqmax);  QFlag 0: s4 = lag(Qlim / vp, Tiq)
///   Iqinj = clamp(Kqv db(Vref0 - Vt_filt), Iql1, Iqh1)       always active (memo item 2c)
///   Iqcmd_bl = Q path + Iqinj, held after a dip for |Thld| s (Thld &gt; 0: its last dip value; &lt; 0: Iqfrz)
///   Iqcmd = clamp(Iqcmd_bl, Iqmin, Iqmax);  Iqmin = Iqmax &lt; 0 ? Iqmax : -Iqmax
///   Ipcmd = clamp((Pord + Paux) / vp, -Ke Ipmax, Ipmax); Ipcmd and Ipmax held at their last dip values for Thld2 s
///   blocked (Vt_filt &lt;= vblkl or &gt;= vblkh, and Tblk_delay after it returns): every limit 0, so Ipcmd = Iqcmd = 0
///   linked non-windup: s3 at a limit with its derivative pushing out stops, and stops s2 too when s2 pushes the same way
/// </code>
/// Paux is 0 (GridSim has no external auxiliary-control models). The baseload flag of the North American tools is not a
/// REEC_D parameter (it belongs to the generator record) and is not modelled (baseload 0).
/// </summary>
public sealed class Reecd : IReecModel
{
    private readonly ReecdParameters _p;
    private readonly double _vref0, _tanPf;
    private double _vf, _pf, _s2, _s3, _s4, _pord, _prl, _vc;
    private bool _wasDip, _wasBlk;
    private HoldTimer _thld, _thld2, _blk;
    private double _iqblLast, _ipcmdLast, _ipmaxLast, _iqblHeld, _ipcmdHeld, _ipmaxHeld;

    /// <summary>Initialise at the operating point (terminal voltage phasor <paramref name="v0"/>, terminal current
    /// <paramref name="i0"/> on the machine base, P0, Q0) for the REGC model's equilibrium commands; every state is set so
    /// the start is stationary, and a point the model cannot hold is refused naming the quantity.</summary>
    public Reecd(ReecdParameters p, Complex v0, Complex i0, double p0, double q0, double ipcmd0, double iqcmd0)
    {
        ArgumentNullException.ThrowIfNull(p);
        IReadOnlyList<string> bad = p.Check();
        if (bad.Count > 0) throw new ArgumentException("REEC_D parameters: " + string.Join("; ", bad), nameof(p));
        _p = p;
        const string m = "REEC_D";
        double vm = v0.Magnitude;
        if (vm < p.Vdip || vm > p.Vup)
            throw new ArgumentOutOfRangeException(nameof(v0), vm, $"{m}: the initial terminal voltage {WeccCheck.N(vm)} pu is outside [Vdip, Vup] = [{WeccCheck.N(p.Vdip)}, {WeccCheck.N(p.Vup)}]; no equilibrium.");
        if (vm <= p.Vblkl || vm >= p.Vblkh)
            throw new ArgumentOutOfRangeException(nameof(v0), vm, $"{m}: the initial terminal voltage {WeccCheck.N(vm)} pu is inside the blocking range (at or below vblkl = {WeccCheck.N(p.Vblkl)} or at or above vblkh = {WeccCheck.N(p.Vblkh)}).");
        if (p.PFlag != 0) throw new ArgumentException($"{m}: PFlag = 1 multiplies the power reference by the generator speed; GridSim couples the wind mechanics to REEC_A only, so REEC_D needs PFlag = 0.");
        _vref0 = p.Vref0 == 0.0 ? vm : p.Vref0;
        _vf = vm; _pf = p0;
        _vc = Compensated(v0, i0, q0);
        if (p.PfFlag == 1)
        {
            if (p0 == 0.0) throw new ArgumentOutOfRangeException(nameof(p0), p0, $"{m}: constant power factor control (PfFlag = 1) needs a non-zero initial active power.");
            _tanPf = q0 / p0;
        }
        double vp = Math.Max(vm, Reec.VoltageFloorPu);
        double iqinj0 = Math.Clamp(p.Kqv * WeccBlocks.Deadband(_vref0 - vm, p.Dbd1, p.Dbd2), p.Iql1, p.Iqh1);
        double qpath0 = iqcmd0 - iqinj0;   // what the Q / V path must give at the start
        double qlimNeeded;
        if (p.QFlag == 0) qlimNeeded = qpath0 * vp;
        else if (p.VFlag == 1) qlimNeeded = q0;
        else qlimNeeded = _vc - p.Vref1;
        double qsel = p.PfFlag == 1 ? _tanPf * p0 : qlimNeeded;
        if (qsel < p.Qvmin || qsel > p.Qvmax)
            throw new ArgumentOutOfRangeException(nameof(q0), qsel, $"{m}: the reference the start needs ({WeccCheck.N(qsel)} pu) is outside [qvmin, qvmax] = [{WeccCheck.N(p.Qvmin)}, {WeccCheck.N(p.Qvmax)}].");
        QextInitial = p.PfFlag == 1 ? qlimNeeded : qsel;
        if (p.QFlag == 1)
        {
            if (_vc < p.Vmin || _vc > p.Vmax)
                throw new ArgumentOutOfRangeException(nameof(v0), _vc, $"{m}: the initial compensated voltage {WeccCheck.N(_vc)} pu is outside [Vmin, Vmax] = [{WeccCheck.N(p.Vmin)}, {WeccCheck.N(p.Vmax)}].");
            _s2 = _vc;
            _s3 = qpath0;
        }
        else
        {
            _s2 = Math.Clamp(_vc, p.Vmin, p.Vmax);
            _s4 = qpath0;
        }
        if (p0 < p.Pmin || p0 > p.Pmax)
            throw new ArgumentOutOfRangeException(nameof(p0), p0, $"{m}: the initial active power {WeccCheck.N(p0)} pu is outside [Pmin, Pmax] = [{WeccCheck.N(p.Pmin)}, {WeccCheck.N(p.Pmax)}].");
        _pord = p0;   // Paux = 0
        _prl = p0;
        PrefInitial = p0;
        var u0 = new ReecInputs(vm, p0, q0, PrefInitial, QextInitial, 1.0) { VtPhasor = v0, It = i0 };
        ReecOutputs o = Evaluate(u0, 0.0, 0.0);
        _iqblLast = o.IqcmdBl; _ipcmdLast = o.Ipcmd; _ipmaxLast = o.Ipmax;
        var r = new List<(string, double)>();
        Residuals(u0, o, r);
        r.Add(($"{m} Ipcmd vs the REGC equilibrium Ip", o.Ipcmd - ipcmd0));
        r.Add(($"{m} Iqcmd vs the REGC equilibrium Iq", o.Iqcmd - iqcmd0));
        WeccEquilibrium.Require(m, r);
    }

    public ReecdParameters Parameters => _p;
    public string Name => "REEC_D";
    public double PrefInitial { get; }
    public double QextInitial { get; }
    public bool InDip => _wasDip;
    public double Pord => _pord;

    /// <summary>True while the converter is blocked (including the release delay).</summary>
    public bool Blocked { get; private set; }

    /// <summary>The filtered terminal voltage (s0) and the compensated voltage (s6).</summary>
    public double FilteredVoltage => _vf;
    public double CompensatedVoltage => _vc;

    private double Compensated(Complex vt, Complex it, double qgen) => _p.VcmpFlag == 1
        ? (vt - new Complex(_p.Rc, _p.Xc) * it).Magnitude
        : vt.Magnitude + _p.Kc * qgen;

    /// <summary>The dip and blocking state machine for the step at time <paramref name="t"/>, from the start-of-step Vt_filt
    /// (the raw <paramref name="vt"/> is not used: REEC_D decides both from Vt_filt). Arms Thld / Thld2 on the step a dip ends
    /// and Tblk_delay on the step blocking ends.</summary>
    public void UpdateDipState(double t, double vt)
    {
        ReecdParameters p = _p;
        bool dip = _vf < p.Vdip || _vf > p.Vup;
        if (!dip && _wasDip)
        {
            if (p.Thld != 0.0) { _thld.Arm(t, Math.Abs(p.Thld)); _iqblHeld = p.Thld > 0.0 ? _iqblLast : p.Iqfrz; }
            if (p.Thld2 > 0.0) { _thld2.Arm(t, p.Thld2); _ipcmdHeld = _ipcmdLast; _ipmaxHeld = _ipmaxLast; }
        }
        if (dip) { _thld.Clear(); _thld2.Clear(); }
        _wasDip = dip;
        bool blk = _vf <= p.Vblkl || _vf >= p.Vblkh;
        if (!blk && _wasBlk && p.TblkDelay > 0.0) _blk.Arm(t, p.TblkDelay);
        if (blk) _blk.Clear();
        _wasBlk = blk;
        _blkNow = blk;
    }
    private bool _blkNow;

    public ReecOutputs Evaluate(in ReecInputs u, double t, double dt)
    {
        ReecdParameters p = _p;
        bool dip = _wasDip;
        bool blocked = _blkNow || _blk.Active(t, dt);
        double vp = Math.Max(_vf, Reec.VoltageFloorPu);
        double qsel = p.PfFlag == 1 ? _tanPf * _pf : u.Qext;
        double qlim = Math.Clamp(qsel, p.Qvmin, p.Qvmax);
        double qerr = qlim - u.Qgen;
        double vsel = Math.Clamp(p.VFlag == 1 ? p.Kqp * qerr + _s2 : p.Vref1 + qlim, p.Vmin, p.Vmax);
        double verr = vsel - _vc;
        double iqinj = Math.Clamp(p.Kqv * WeccBlocks.Deadband(_vref0 - _vf, p.Dbd1, p.Dbd2), p.Iql1, p.Iqh1);
        double vdlq = WeccBlocks.Table(p.Vq, p.Iq, _vf);
        double vdlp = WeccBlocks.Table(p.Vp, p.Ip, _vf);
        bool hold1 = !dip && _thld.Active(t, dt);
        bool hold2 = !dip && _thld2.Active(t, dt);
        double ipRaw = _pord / vp;   // (Pord + Paux) / vp, Paux = 0

        double iqmax, iqmin, ipmax, ipmin, iqcmd, ipcmd, iqbl;
        if (p.PqFlag == 0)
        {
            iqmax = Math.Min(vdlq, p.Imax);
            iqmin = iqmax < 0.0 ? iqmax : -iqmax;
            iqbl = hold1 ? _iqblHeld : QPath(iqmin, iqmax, verr) + iqinj;
            iqcmd = Math.Clamp(iqbl, iqmin, iqmax);
            ipmax = hold2 ? _ipmaxHeld : Math.Min(vdlp, Math.Sqrt(Math.Max(0.0, p.Imax * p.Imax - iqcmd * iqcmd)));
            ipmin = -p.Ke * ipmax;
            ipcmd = hold2 ? _ipcmdHeld : Math.Clamp(ipRaw, ipmin, ipmax);
        }
        else
        {
            ipmax = hold2 ? _ipmaxHeld : Math.Min(vdlp, p.Imax);
            ipmin = -p.Ke * ipmax;
            ipcmd = hold2 ? _ipcmdHeld : Math.Clamp(ipRaw, ipmin, ipmax);
            iqmax = Math.Min(vdlq, Math.Sqrt(Math.Max(0.0, p.Imax * p.Imax - ipcmd * ipcmd)));
            iqmin = iqmax < 0.0 ? iqmax : -iqmax;
            iqbl = hold1 ? _iqblHeld : QPath(iqmin, iqmax, verr) + iqinj;
            iqcmd = Math.Clamp(iqbl, iqmin, iqmax);
        }
        if (blocked) { iqmax = iqmin = ipmax = ipmin = 0.0; iqcmd = ipcmd = 0.0; }
        return new ReecOutputs(ipcmd, iqcmd, _pord, ipmax, ipmin, iqmax, qlim, qerr, verr, _prl, qlim / vp, dip, iqinj)
            { Blocked = blocked, Iqmin = iqmin, IqcmdBl = iqbl };
    }

    private double QPath(double iqmin, double iqmax, double verr) =>
        _p.QFlag == 1 ? Math.Clamp(_p.Kvp * verr + Math.Clamp(_s3, iqmin, iqmax), iqmin, iqmax) : _s4;

    public void Advance(in ReecInputs u, in ReecOutputs o, double dt)
    {
        ReecdParameters p = _p;
        if (o.Dip) { _iqblLast = o.IqcmdBl; _ipcmdLast = o.Ipcmd; _ipmaxLast = o.Ipmax; }
        if (!o.Dip)
        {
            double ds2 = p.VFlag == 1 ? p.Kqi * o.QErr : 0.0;
            if (p.QFlag == 1)
            {
                // Memo p. 10-11: the integrator is first clamped to its limits, its derivative stops at a limit it pushes
                // against, and then the Q PI (s2) stops too when it pushes the same way (the linked non-windup).
                double s3 = Math.Clamp(_s3, o.Iqmin, o.Iqmax);
                double ds3 = p.Kvi * o.VErr;
                if (s3 >= o.Iqmax && ds3 > 0.0) { ds3 = 0.0; if (ds2 > 0.0) ds2 = 0.0; }
                else if (s3 <= o.Iqmin && ds3 < 0.0) { ds3 = 0.0; if (ds2 < 0.0) ds2 = 0.0; }
                _s3 = Math.Clamp(s3 + ds3 * dt, o.Iqmin, o.Iqmax);
            }
            else _s4 = WeccBlocks.Lag(_s4, o.S4Target, p.Tiq, dt);
            if (p.VFlag == 1) _s2 = Math.Clamp(_s2 + ds2 * dt, p.Vmin, p.Vmax);
            _pord = Math.Clamp(WeccBlocks.Lag(_pord, o.Psel, p.Tpord, dt), p.Pmin, p.Pmax);
        }
        _prl = WeccBlocks.RateLimit(_prl, u.Pref, p.DPmax, p.DPmin, dt);
        _vf = WeccBlocks.Lag(_vf, u.Vt, p.Trv, dt);
        _pf = WeccBlocks.Lag(_pf, u.Pe, p.Tp, dt);
        _vc = WeccBlocks.Lag(_vc, Compensated(u.VtPhasor, u.It, u.Qgen), p.Tr1, dt);
    }

    public void Residuals(in ReecInputs u, in ReecOutputs o, List<(string Name, double Value)> into)
    {
        const string m = "REEC_D";
        into.Add(($"{m} s0 Vt_filt (Vt - Vt_filt)", u.Vt - _vf));
        into.Add(($"{m} s1 Pe_filt (Pe - Pe_filt)", u.Pe - _pf));
        into.Add(($"{m} s6 Vcomp (input - s6)", Compensated(u.VtPhasor, u.It, u.Qgen) - _vc));
        if (_p.VFlag == 1) into.Add(($"{m} s2 Q PI input (Qlim - Qgen)", o.QErr));
        if (_p.QFlag == 1) into.Add(($"{m} s3 V PI input (Vsel - Vcomp)", o.VErr));
        else into.Add(($"{m} s4 (Qlim / Vt_filt - s4)", o.S4Target - _s4));
        into.Add(($"{m} s5 Pord (Psel - Pord)", o.Psel - _pord));
        into.Add(($"{m} Pref rate limiter (Pref - Pref_rl)", u.Pref - _prl));
    }
}
