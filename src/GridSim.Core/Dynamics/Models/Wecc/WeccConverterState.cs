using System;
using System.Collections.Generic;
using System.Numerics;

namespace GridSim.Core.Dynamics.Models.Wecc;

/// <summary>
/// One converter-interfaced unit driven by its WECC model chain (<see cref="WeccPlant"/>: REGC_A with an optional
/// REEC_A/B/C and an optional REPC_A plant controller) in the RMS engine. The models work per unit on the unit's MBASE;
/// the network sees system per unit, so currents are scaled by the unit's rating (<see cref="RmsConverterUnit.RatingPu"/>
/// = MBASE / system base).
///
/// <para><b>Each step</b> (the kernel's sequence): the current is iterated with the network to the algebraic fixed
/// point (<see cref="TrialCurrent"/>: REGC_A's LVACM and HVRCM at the trial voltage, states held), committed at the
/// converged voltage (<see cref="Commit"/>, which also measures the unit's P and Q), then <see cref="Step"/> runs the
/// dip state machine, takes the plant controller's Pref and Qext from its start-of-step states and measurements, evaluates
/// the REEC commands, advances REGC_A with those commands held, then advances the REEC and REPC states and the frequency
/// measurement. Without a REEC the current commands stay at their initial values; without a REPC the REEC's Pref and Qext
/// stay at the values that hold the initial point.</para>
///
/// <para><b>Plant controller measurements.</b> The regulated bus voltage and the flow-branch current come from the
/// retained converter nodes the network solves at the start of the step (<see cref="RmsConverterUnit.WeccMeasurement"/>);
/// with no branch the plant output is the unit's own P, Q and current. The frequency is the backward difference of the
/// regulated bus voltage angle over the step, <c>1 + wrap(theta_k - theta_k-1) / (2 pi f0 dt)</c>, through a first-order lag
/// of <see cref="RepcaParameters.FrequencyFilterSeconds"/> (default 0.02 s, DECISIONS D15); the controller reads the filter state at
/// the start of the step.</para>
///
/// <para><b>Initialisation</b> from the unit's solved operating point (P0, Q0, V0) and, for a plant controller, the solved
/// voltages of its measurement buses; every model checks its own start (<see cref="Regca"/>, <see cref="Reec"/>,
/// <see cref="Repca"/>) and the composed unit then requires every REGC_A state residual to be zero for the commands the
/// controls give at t = 0 (<see cref="WeccEquilibrium"/>).</para>
///
/// <para>The current is injected along the terminal voltage angle (REGC_A has no PLL dynamics); when the terminal
/// voltage collapses below <see cref="RmsConverterState.PllLockMinVoltagePu"/> the last angle is held, as the legacy
/// converter does.</para>
/// </summary>
public sealed class WeccConverterState : IRmsNetworkSolvedConverterModel
{
    private readonly RmsConverterUnit _u;
    private readonly WeccPlant _plant;
    private readonly Regca? _regc;
    private readonly Regcb? _regcb;      // REGC_B (voltage source) in place of REGC_A
    private readonly Reec? _reec;
    private readonly Reecd? _reecd;      // REEC_D in place of REEC_A/B/C
    private readonly IReecModel? _ctl;   // whichever electrical control the unit has
    private readonly Complex _ysys;      // REGC_B: the source's Norton admittance on the bus (system pu); 0 for REGC_A
    private bool _blocked;               // REEC_D blocking, linked to REGC_B (memo: the output current is zero at once)
    private bool _frameHeld;             // REGC_B: no consistent terminal frame at the last network solution (D36); hold _angle
    private readonly Repca? _repc;
    private readonly WeccMeasurement? _meas;
    private readonly int _nodeReg = -1, _nodeFrom = -1, _nodeTo = -1;
    private readonly double _r;           // MBASE / system base
    private readonly double _ws;          // 2 pi f0 (rad/s)
    private readonly double _ipcmd0, _iqcmd0, _pref, _qext;
    private double _angle, _pe, _qe, _vmCommitted;
    private Complex _iCommitted;
    private double _freq = 1.0, _freqAngle;
    // Wind mechanics (null without): drive train, and for a type 3 turbine the pitch and torque controllers.
    private readonly DriveTrain? _drive;
    private readonly PitchController? _pitch;
    private readonly TorqueController? _torque;
    private readonly double _pm0, _plantPref0;

    /// <param name="unit">The unit (its <see cref="RmsConverterUnit.Wecc"/> chain must be set).</param>
    /// <param name="nodeBuses">The network's converter-node bus order (<see cref="RmsConverterUnit.NodeBuses"/>), where
    /// the plant controller's measurement buses are found; may be empty for a unit with no plant controller measuring
    /// another bus.</param>
    public WeccConverterState(RmsConverterUnit unit, IReadOnlyList<int>? nodeBuses = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        _plant = unit.Wecc ?? throw new ArgumentException("The unit carries no WECC model chain (RmsConverterUnit.Wecc is null).", nameof(unit));
        if (_plant.Der is not null) throw new ArgumentException("A DER_A unit is driven by DerAState, not WeccConverterState.", nameof(unit));
        IReadOnlyList<string> bad = _plant.Check();
        if (bad.Count > 0) throw new ArgumentException($"WECC unit '{unit.Name}': " + string.Join("; ", bad), nameof(unit));
        _u = unit;
        _r = unit.RatingPu;
        if (!(_r > 0.0) || !double.IsFinite(_r)) throw new ArgumentOutOfRangeException(nameof(unit), _r, $"WECC unit '{unit.Name}': the rating (MBASE / system base) must be a finite number above 0.");
        _ws = 2.0 * Math.PI * unit.Parameters.NominalHz;
        double v0 = unit.V0.Magnitude;
        double p0 = unit.P0 / _r, q0 = unit.Q0 / _r;
        _meas = unit.WeccMeasurement;
        if (_plant.Repc is not null && _meas is null)
            _meas = new WeccMeasurement(0, 0, 0, Complex.Zero, Complex.Zero, Complex.Zero, Complex.Zero, Complex.Zero);   // own terminal, own output
        if (_meas is not null)
        {
            int Node(int bus)
            {
                int i = nodeBuses is null ? -1 : IndexOf(nodeBuses, bus);
                if (i < 0) throw new ArgumentException($"WECC unit '{unit.Name}': its plant controller measures bus {bus}, which is not a retained converter node of the network.", nameof(nodeBuses));
                return i;
            }
            if (_meas.RegulatedBus != 0) _nodeReg = Node(_meas.RegulatedBus);
            if (_meas.HasBranch) { _nodeFrom = Node(_meas.FromBus); _nodeTo = Node(_meas.ToBus); }
        }
        try
        {
            if (_plant.RegcB is { } pb)
            {
                _regcb = new Regcb(pb, unit.V0, p0, q0);
                _ysys = unit.NortonAdmittance;
                _ipcmd0 = _regcb.Ip;
                _iqcmd0 = _regcb.IqExport;
            }
            else
            {
                _regc = new Regca(_plant.Regc!, v0, p0, q0);
                _ipcmd0 = _regc.Ip;
                _iqcmd0 = _regc.IqExport;
            }
            double ipcmd = _ipcmd0, iqcmd = _iqcmd0;
            _iCommitted = Complex.Conjugate(new Complex(unit.P0, unit.Q0) / unit.V0);
            // Wind: the initial speed is the torque controller's speed reference at the initial power (type 3), else 1 pu.
            WindParameters? wind = _plant.Wind;
            double omega0 = 1.0;
            if (wind is { Torque: { } tq })
            {
                double mbase = unit.RatingPu * unit.BaseMva;
                if (tq.Trate > 0.0 && Math.Abs(tq.Trate - mbase) > 1e-9 * mbase)
                    throw new ArgumentException($"WTGTRQ_A TRATE = {WeccCheck.N(tq.Trate)} MW differs from the unit's MBASE {WeccCheck.N(mbase)} MVA; the scaling between "
                        + "the turbine and machine bases is not publicly specified, so GridSim requires them equal (or TRATE = 0).");
                omega0 = WeccBlocks.Table(tq.P, tq.Spd, p0);
            }
            _pm0 = p0;
            _plantPref0 = p0;
            if (_plant.Reec is { } rp) { _reec = new Reec(rp, v0, p0, q0, _ipcmd0, _iqcmd0, omega0); _ctl = _reec; }
            else if (_plant.ReecD is { } rd) { _reecd = new Reecd(rd, unit.V0, _iCommitted / _r, p0, q0, _ipcmd0, _iqcmd0); _ctl = _reecd; }
            if (_ctl is not null)
            {
                _pref = _ctl.PrefInitial;
                _qext = _ctl.QextInitial;
                double qext = _qext, pref = _pref;
                if (_plant.Repc is { } pp)
                {
                    // A type 3 turbine's plant controller gives the torque controller its power reference (P0 at the start);
                    // otherwise it gives the REEC model its Pref.
                    double repcPref0 = wind?.IsType3 == true ? p0 : _pref;
                    Complex vreg0 = _meas!.RegulatedBus != 0 ? _meas.RegulatedV0 : unit.V0;
                    RepcInputs u0 = Measure(vreg0, _meas.HasBranch ? _meas.FromV0 : Complex.Zero, _meas.HasBranch ? _meas.ToV0 : Complex.Zero, pp, p0, q0, _iCommitted, 1.0);
                    _repc = new Repca(pp, u0, _qext, repcPref0);
                    double repcPref;
                    (qext, repcPref) = _repc.Outputs(u0);
                    var rr = new List<(string, double)> { ("REPC_A Qext vs the REEC's initial Qext", qext - _qext), ("REPC_A Pref vs its initial value", repcPref - repcPref0) };
                    WeccEquilibrium.Require("REPC_A", rr);
                    if (wind?.IsType3 != true) pref = repcPref;
                    _freqAngle = DetMath.Phase(vreg0);
                }
                if (wind is not null)
                {
                    Reec reecA = _reec!;   // WeccPlant.Check: wind mechanics need REEC_A
                    var wr = new List<(string, double)>();
                    _drive = new DriveTrain(wind.DriveTrain, omega0, p0);
                    double[] d = _drive.Derivatives(p0, p0);
                    for (int i = 0; i < d.Length; i++) wr.Add(($"WTGT_A state {i} derivative", d[i]));
                    if (wind.IsType3)
                    {
                        _torque = new TorqueController(wind.Torque!, p0, reecA.PrefInitial);
                        _pitch = new PitchController(wind.Pitch!, wind.Aero!.Theta0);
                        wr.Add(("WTGAR_A Pm(theta0) - P0", Aerodynamics.MechanicalPower(wind.Aero, p0, _pitch.Theta) - p0));
                        _torque.Residuals(p0, p0, omega0, wr);
                        wr.Add(("WTGTRQ_A torque vs the REEC's initial Pref", _torque.Torque(p0, omega0) - reecA.PrefInitial));
                        ReecOutputs oi = reecA.Evaluate(new ReecInputs(v0, p0, q0, reecA.PrefInitial, qext, omega0), 0.0, 0.0);
                        _pitch.Residuals(omega0, _torque.SpeedReference, oi.Pord, oi.Psel, wr);
                    }
                    WeccEquilibrium.Require(wind.Chain, wr);
                }
                ReecOutputs o = _ctl.Evaluate(new ReecInputs(v0, p0, q0, pref, qext, omega0) { VtPhasor = unit.V0, It = _iCommitted / _r }, 0.0, 0.0);
                ipcmd = o.Ipcmd; iqcmd = o.Iqcmd;
            }
            var r = new List<(string, double)>();
            if (_regcb is not null) _regcb.Residuals(ipcmd, iqcmd, unit.V0, r);
            else _regc!.Residuals(ipcmd, iqcmd, v0, r);
            WeccEquilibrium.Require(_plant.Chain, r);
            if (_regcb is not null) _regcb.Settle(ipcmd, iqcmd, unit.V0);
            else _regc!.Settle(ipcmd, iqcmd);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException($"WECC unit '{unit.Name}' at bus {unit.Bus} ({_plant.Chain}{(_plant.Source.Length > 0 ? ", " + _plant.Source : "")}): {ex.Message}", ex);
        }
        _angle = DetMath.Phase(unit.V0);
        _vmCommitted = unit.V0.Magnitude;
        _pe = p0; _qe = q0;
    }

    private static int IndexOf(IReadOnlyList<int> list, int value)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == value) return i;
        return -1;
    }

    public RmsConverterUnit Unit => _u;
    public WeccPlant Plant => _plant;
    /// <summary>The REGC_A model (throws for a REGC_B unit: use <see cref="RegcB"/>).</summary>
    public Regca Regc => _regc ?? throw new InvalidOperationException("This unit's generator/converter model is REGC_B (RegcB).");
    /// <summary>The REGC_B model, or null for a REGC_A unit.</summary>
    public Regcb? RegcB => _regcb;
    public Reec? Control => _reec;
    /// <summary>The REEC_D model, or null.</summary>
    public Reecd? ControlD => _reecd;
    /// <summary>REEC_D: true while the converter is blocked (as applied to the network solve of the next step).</summary>
    public bool Blocked => _blocked;
    public Repca? PlantController => _repc;

    public double ActiveCurrent { get; private set; }
    public double ReactiveCurrent { get; private set; }
    public bool InDip => _ctl?.InDip ?? false;

    /// <summary>Active and reactive power (machine base) measured at the last commit.</summary>
    public (double P, double Q) MeasuredPower => (_pe, _qe);

    /// <summary>The plant controller's filtered frequency measurement (pu); 1 without a plant controller.</summary>
    public double MeasuredFrequency => _freq;

    /// <summary>The drive train (WTGT_A), or null.</summary>
    public DriveTrain? Drive => _drive;

    /// <summary>The pitch controller (WTGPT_A), or null.</summary>
    public PitchController? Pitch => _pitch;

    /// <summary>The torque controller (WTGTRQ_A), or null.</summary>
    public TorqueController? Torque => _torque;

    private Complex Injection(Complex v, out double ip, out double iq, out double angle)
    {
        double vm = v.Magnitude;
        angle = vm > RmsConverterState.PllLockMinVoltagePu && !_frameHeld ? DetMath.Phase(v) : _angle;
        if (_regcb is not null)
        {
            // Voltage source in the terminal-voltage frame (D36, replacing the start-of-step hold of D32): the terminal current
            // (E e^{j angle} - V) / Z, limited on the same frame axis (or zero while blocked), plus the Norton part y V. The frame
            // is the terminal angle itself unless the last network solution found no consistent frame (then it is held).
            Complex it = _regcb.TerminalCurrent(v, angle, angle, _blocked, out _);
            Complex rel = it * Complex.Conjugate(DetMath.FromPolar(1.0, angle));
            ip = rel.Real; iq = -rel.Imaginary;
            return it * _r + _ysys * v;
        }
        (ip, iq) = _regc!.Output(vm);
        return new Complex(ip * _r, -iq * _r) * DetMath.FromPolar(1.0, angle);
    }

    public Complex TrialCurrent(Complex v) => Injection(v, out _, out _, out _);

    /// <summary>REGC_B units solve their network interface exactly (D36); REGC_A units take the historical iteration.</summary>
    public bool SolvesWithNetwork => _regcb is not null;

    /// <summary>Both generator models can solve their interface exactly; for REGC_A the kernel uses it only to rescue a step
    /// whose substitution hit the cap (D38).</summary>
    public bool CanSolveWithNetwork => true;

    /// <summary>REGC_B: the injection that satisfies <c>V = a + zdd I(V)</c> (<see cref="Regcb.SolveWithNetwork"/>); records
    /// whether the terminal frame had to be held, which the next trial and the commit follow.</summary>
    public Complex NetworkSolvedCurrent(Complex a, Complex zdd)
    {
        if (_regcb is null)
        {
            // REGC_A (a current source: no Norton admittance), D38.
            CurrentSourceNetworkSolver.Solution c = _regc!.SolveWithNetwork(a, Complex.One, zdd * _r, _angle, _vmCommitted);
            _frameHeld = c.FrameHeld;
            return c.Current * _r;
        }
        RegcbNetworkSolution s = _regcb.SolveWithNetwork(a, 1.0 - zdd * _ysys, zdd * _r, _angle, _blocked, _iCommitted / _r);
        _frameHeld = s.FrameHeld;
        return s.Current * _r + _ysys * s.Voltage;
    }

    public Complex Commit(Complex v)
    {
        Complex i = Injection(v, out double ip, out double iq, out double angle);
        _angle = angle;
        _vmCommitted = v.Magnitude;
        ActiveCurrent = ip * _r;
        ReactiveCurrent = iq * _r;
        Complex term = _regcb is null ? i : i - _ysys * v;   // the terminal current (the injection itself for REGC_A)
        Complex s = v * Complex.Conjugate(term);
        _pe = s.Real / _r;
        _qe = s.Imaginary / _r;
        _iCommitted = term;
        return i;
    }

    public Complex TerminalCurrent(Complex injection, Complex v) => _regcb is null ? injection : injection - _ysys * v;

    // The plant controller's inputs (machine base for P and Q): the regulated voltage, the plant output (the branch flow at
    // BUS1, or the unit's own) and the compensated voltage |Vreg - (Rc + jXc) I| with I on the machine base.
    private RepcInputs Measure(Complex vreg, Complex vFrom, Complex vTo, RepcaParameters pp, double pUnit, double qUnit, Complex iUnit, double freq)
    {
        double p = pUnit, q = qUnit;
        Complex i = iUnit;   // system per unit
        if (_meas is { HasBranch: true } m)
        {
            i = m.Y11 * vFrom + m.Y12 * vTo;
            Complex s = vFrom * Complex.Conjugate(i);
            p = s.Real / _r; q = s.Imaginary / _r;
        }
        double vcomp = (vreg - new Complex(pp.Rc, pp.Xc) * (i / _r)).Magnitude;
        return new RepcInputs(vreg.Magnitude, vcomp, p, q, freq);
    }

    public void Step(double t, Complex v, Complex[] nodeVoltages, double dt)
    {
        double vm = v.Magnitude;
        if (_regcb is null) _frameHeld = false;   // REGC_A: a rescued step's frame choice does not carry into the next step (D38)
        if (_ctl is null)
        {
            if (_regcb is not null) _regcb.Step(_ipcmd0, _iqcmd0, v, _angle, dt);
            else _regc!.Step(_ipcmd0, _iqcmd0, vm, dt);
            return;
        }
        _ctl.UpdateDipState(t, vm);
        double pref = _pref, qext = _qext;
        RepcInputs ru = default;
        Complex vreg = v;
        if (_repc is not null)
        {
            vreg = _nodeReg >= 0 ? nodeVoltages[_nodeReg] : v;
            ru = Measure(vreg, _nodeFrom >= 0 ? nodeVoltages[_nodeFrom] : Complex.Zero, _nodeTo >= 0 ? nodeVoltages[_nodeTo] : Complex.Zero,
                _repc.Parameters, _pe, _qe, _iCommitted, _freq);
            (qext, pref) = _repc.Outputs(ru);
        }
        // Wind (start-of-step values throughout): speeds from the drive train; a type 3 turbine's REEC Pref is the torque
        // controller's torque, whose own power reference is the plant controller's Pref (or P0 without one).
        double wg = _drive?.GeneratorSpeed ?? 1.0, wt = _drive?.TurbineSpeed ?? 1.0;
        double plantPref = _repc is not null ? pref : _plantPref0;
        double wref = _torque?.SpeedReference ?? 1.0;
        double reecPref = _torque is not null ? _torque.Torque(plantPref, wg) : pref;
        var u = new ReecInputs(vm, _pe, _qe, reecPref, qext, wg) { VtPhasor = v, It = _iCommitted / _r };
        ReecOutputs o = _ctl.Evaluate(u, t, dt);
        double pm = _pitch is not null ? Aerodynamics.MechanicalPower(_plant.Wind!.Aero!, _pm0, _pitch.Theta) : _pm0;
        if (_regcb is not null) _regcb.Step(o.Ipcmd, o.Iqcmd, v, _angle, dt);
        else _regc!.Step(o.Ipcmd, o.Iqcmd, vm, dt);
        _ctl.Advance(u, o, dt);
        _blocked = o.Blocked && _regcb is not null;
        _torque?.Advance(_pe, plantPref, wg, o.Dip, dt);
        _pitch?.Advance(wt, wref, o.Pord, o.Psel, dt);
        _drive?.Step(pm, _pe, dt);
        if (_repc is not null)
        {
            _repc.Advance(ru, dt);
            // Frequency: the angle step of the regulated voltage over this step, wrapped into (-pi, pi]; a collapsed
            // voltage (no angle) holds the last angle and reads nominal.
            double fInst = 1.0;
            if (vreg.Magnitude > RmsConverterState.PllLockMinVoltagePu)
            {
                double th = DetMath.Phase(vreg);
                double d = th - _freqAngle;
                if (d > Math.PI) d -= 2.0 * Math.PI;
                else if (d <= -Math.PI) d += 2.0 * Math.PI;
                fInst = 1.0 + d / (_ws * dt);
                _freqAngle = th;
            }
            _freq = WeccBlocks.Lag(_freq, fInst, _repc.Parameters.FrequencyFilterSeconds, dt);
        }
    }
}
