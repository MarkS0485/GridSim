namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// WP3b (RC5): a governor that also reads the machine's electrical power. The RMS kernel passes the start-of-step air-gap
/// power Re(E'' conj(I)) (equal to the terminal power with no armature resistance), converted to the machine base, through
/// <see cref="Step(double, double, double)"/>; the plain <see cref="IRmsGovernor.Step"/> is refused when
/// <see cref="NeedsElectricalPower"/> is true.
/// </summary>
public interface IRmsElectricalPowerGovernor : IRmsGovernor
{
    /// <summary>True when a step reads the electrical power (GGOV1 with Rselect 1 or a supervisory load controller).</summary>
    bool NeedsElectricalPower { get; }

    /// <summary>One step with speed deviation <paramref name="speedDeviationPu"/> and electrical power
    /// <paramref name="electricalPowerPu"/> (machine base), both held across the step; returns the mechanical power.</summary>
    double Step(double speedDeviationPu, double electricalPowerPu, double dtSeconds);
}

/// <summary>
/// WP3b (RC5): the RMS kernel's hook for governors that read the electrical power. The kernel tests the mechanical-power
/// delegate's target for this interface once per run; a hook that implements it is handed each machine's start-of-step
/// electrical power (system base) before it is asked for that machine's mechanical power. No historical hook implements it,
/// so every existing run takes exactly its old path.
/// </summary>
internal interface IRmsElectricalPowerFeed
{
    void SetElectricalPower(int machine, double electricalPowerSystemPu);
}

/// <summary>
/// A first-order lag 1/(1 + sT) whose rate is limited to [<c>closing</c>, <c>opening</c>] and whose state is held in
/// [min, max] (non-windup): the exact solution over a step with the input held, as <see cref="Ieeeg1"/>'s valve uses it
/// (a rate-limited stretch at the limit rate, then the exact lag from the point where the rate falls inside the limits).
/// T at or below <see cref="ControlBlocks.InstantThresholdSeconds"/> is the pure rate limiter.
/// </summary>
internal static class RateLimitedLag
{
    internal static double Step(double x, double u, double t, double opening, double closing, double min, double max, double dt)
    {
        double next;
        if (t <= ControlBlocks.InstantThresholdSeconds)
            next = Math.Clamp(u, x + closing * dt, x + opening * dt);
        else
        {
            double rate = (u - x) / t;
            if (rate > opening)
            {
                double x1 = u - opening * t;
                double t1 = (x1 - x) / opening;
                next = t1 >= dt ? x + opening * dt : LagFrom(x1, u, t, dt - t1);
            }
            else if (rate < closing)
            {
                double x1 = u - closing * t;
                double t1 = (x1 - x) / closing;
                next = t1 >= dt ? x + closing * dt : LagFrom(x1, u, t, dt - t1);
            }
            else next = LagFrom(x, u, t, dt);
        }
        return Math.Clamp(next, min, max);
    }

    private static double LagFrom(double x, double u, double t, double seconds)
    {
        double a = DetMath.Exp(-seconds / t);
        return a * x + (1.0 - a) * u;
    }
}

/// <summary>
/// GGOV1, the GE general-purpose governor-turbine model (PSS/E GGOV1; PSS/E 33 model library data sheet 7.8, public copy at
/// pdfcoffee.com/models-pdf-free.html; the block structure as OpenIPSL's PSS/E-verified GGOV1, github.com/OpenIPSL/OpenIPSL,
/// Electrical/Controls/PSSE/TG). Per unit on the turbine base (Trate MW when given, else the machine base):
/// <code>
///   Pef   = lag_{Tpelec}(Pelec)
///   fb    = Pef (Rselect 1) | valve stroke (-1) | governor output fsr (-2) | 0 (isochronous, 0)
///   xMW   = integral Kimw (Pmwset - Pef), limited to +-1.1 R          (supervisory load controller; Kimw 0: none)
///   err   = clamp(Pref + xMW - R fb - dw, minerr, maxerr)
///   fsrn  = Kpgov err + xI + Kdgov s/(1 + s Tdgov) err,  xI = integral Kigov err in [Vmin, Vmax]
///   fsrt  = min(1, Kpload e + xL),  e = Ldref/Kturb + Wfnl - TEXM,  dxL/dt = clamp(Kiload e, Rdown, Rup), xL at most 1
///   fsra  = fsr(previous step) + Ka dt (Aset - s/(1 + s Ta) dw)       (acceleration limiter, as PSS/E with DELT = the step)
///   fsr   = clamp(min(fsrn, fsrt, fsra), Vmin, Vmax)
///   valve = lag_{Tact}(fsr), rate in [Rclose, Ropen], position in [Vmin, Vmax]
///   wf    = valve (1 + dw) (Flag 1) | valve (Flag 0)
///   Pmech = leadlag_{Tc/Tb}(Kturb (wf - Wfnl)) - Dm (1 + dw)
///   TEXM  = lag_{Tfload}(leadlag_{Tsa/Tsb}(wf))
/// </code>
/// <para>At rest (dw = 0, Pelec = Pmech0) every state sits at the equilibrium of Pmech0: the turbine holds Pmech0 + Dm, the
/// valve and fsr sit at fsr0 = (Pmech0 + Dm)/Kturb + Wfnl (inside [Vmin, Vmax], else refused), xI = fsr0, Pref = R fb0 and
/// Pmwset = Pelec0. Pref, Pmwset and the electrical-power lag are set on the first step from the measured power (as WP3a's
/// stabilisers set their washouts), so a machine at equilibrium starts with err = 0 exactly. The load limiter starts with
/// xL = fsr0 and must not bind there (Ldref/Kturb + Wfnl at or above fsr0, else refused); the acceleration limiter starts
/// at rest.</para>
/// <para>Not modelled, refused by name: a transport delay Teng above 0, a speed deadband db other than 0 (the public sources
/// place it differently) and a negative Dm (PSS/E's speed-dependent fuel limit). The load limiter is left out when Kpload
/// and Kiload are both 0, and the acceleration limiter when Ka or Ta is 0 (PSS/E: "may be set to zero if the limiter is not
/// active"). Rup and Rdown limit the rate of the load-limit integrator. The acceleration limiter follows PSS/E's discrete
/// form, so it depends on the step.</para>
/// </summary>
public sealed class Ggov1 : IRmsElectricalPowerGovernor
{
    private readonly int _rselect, _flag;
    private readonly double _r, _maxErr, _minErr, _kp, _ki, _kd, _vMin, _vMax, _tAct, _kturb, _wfnl;
    private readonly double _kpLoad, _kiLoad, _ldref, _dm, _ropen, _rclose, _kimw, _aset, _ka, _rup, _rdown, _toTurbine;
    private readonly FirstOrderLag _pelecLag;
    private readonly Washout? _derivative;
    private readonly double _derivativeGain;
    private readonly LeadLag? _turbine;
    private readonly LeadLag? _temperature;
    private readonly FirstOrderLag _texm;
    private readonly Washout? _acceleration;
    private readonly double _ta;
    private readonly bool _loadLimiter, _accelerationLimiter;
    private double _xI, _xMw, _xLoad, _valve, _fsr, _pref, _pmwset;
    private bool _pending;

    /// <summary>GGOV1 in its PSS/E order (ICONs Rselect Flag, then CONs R Tpelec maxerr minerr Kpgov Kigov Kdgov Tdgov Vmax Vmin
    /// Tact Kturb Wfnl Tb Tc Teng Tfload Kpload Kiload Ldref Dm Ropen Rclose Kimw Aset Ka Ta Trate db Tsa Tsb Rup Rdown).
    /// <paramref name="machineBaseMva"/> converts between the machine base the RMS study uses and the turbine base Trate
    /// (when Trate is above 0).</summary>
    public Ggov1(int rselect, int flag, double r, double tpelec, double maxErr, double minErr, double kpgov, double kigov,
        double kdgov, double tdgov, double vMax, double vMin, double tact, double kturb, double wfnl, double tb, double tc,
        double teng, double tfload, double kpload, double kiload, double ldref, double dm, double ropen, double rclose,
        double kimw, double aset, double ka, double ta, double trate, double db, double tsa, double tsb, double rup,
        double rdown, double machineBaseMva = 0.0)
    {
        const string who = "GGOV1";
        if (rselect is not (1 or 0 or -1 or -2))
            throw new ArgumentOutOfRangeException(nameof(rselect), rselect, "GGOV1: Rselect must be 1 (electrical power), 0 (isochronous), -1 (fuel valve stroke) or -2 (governor output).");
        if (flag is not (0 or 1))
            throw new ArgumentOutOfRangeException(nameof(flag), flag, "GGOV1: Flag must be 0 (fuel flow independent of speed) or 1 (proportional to speed).");
        if (rselect != 0) LibraryChecks.Positive(who, "R", r);
        else LibraryChecks.NonNegative(who, "R", r);
        LibraryChecks.NonNegative(who, "Tpelec", tpelec);
        LibraryChecks.Limits(who, "minerr", minErr, "maxerr", maxErr);
        if (!(minErr <= 0.0 && maxErr >= 0.0))
            throw new ArgumentOutOfRangeException(nameof(maxErr), maxErr, "GGOV1: the speed error limits must hold 0 (minerr at or below 0, maxerr at or above 0), or the governor has no equilibrium.");
        LibraryChecks.NonNegative(who, "Kpgov", kpgov);
        LibraryChecks.NonNegative(who, "Kigov", kigov);
        LibraryChecks.NonNegative(who, "Kdgov", kdgov);
        LibraryChecks.NonNegative(who, "Tdgov", tdgov);
        if (kdgov > 0 && tdgov == 0) throw new ArgumentOutOfRangeException(nameof(tdgov), tdgov, "GGOV1: Kdgov above 0 with Tdgov = 0 is a pure derivative; give Tdgov above 0.");
        LibraryChecks.Limits(who, "Vmin", vMin, "Vmax", vMax);
        LibraryChecks.NonNegative(who, "Tact", tact);
        LibraryChecks.Positive(who, "Kturb", kturb);
        if (!double.IsFinite(wfnl)) throw new ArgumentOutOfRangeException(nameof(wfnl), wfnl, "GGOV1: Wfnl must be finite.");
        if (teng != 0.0)
            throw new ArgumentOutOfRangeException(nameof(teng), teng, "GGOV1: a transport delay Teng above 0 (diesel engines) is not modelled; GridSim's GGOV1 needs Teng = 0.");
        if (db != 0.0)
            throw new ArgumentOutOfRangeException(nameof(db), db, "GGOV1: a speed deadband db other than 0 is not modelled (the public sources place it differently in the diagram).");
        if (!(dm >= 0.0) || !double.IsFinite(dm))
            throw new ArgumentOutOfRangeException(nameof(dm), dm, "GGOV1: Dm must be 0 or above; a negative Dm (PSS/E's speed-dependent maximum fuel flow) is not modelled.");
        LibraryChecks.NonNegative(who, "Tfload", tfload);
        LibraryChecks.NonNegative(who, "Kpload", kpload);
        LibraryChecks.NonNegative(who, "Kiload", kiload);
        if (!double.IsFinite(ldref)) throw new ArgumentOutOfRangeException(nameof(ldref), ldref, "GGOV1: Ldref must be finite.");
        if (!(ropen > 0.0)) throw new ArgumentOutOfRangeException(nameof(ropen), ropen, "GGOV1: Ropen must be above 0 pu/s.");
        if (!(rclose < 0.0)) throw new ArgumentOutOfRangeException(nameof(rclose), rclose, "GGOV1: Rclose must be below 0 pu/s.");
        LibraryChecks.NonNegative(who, "Kimw", kimw);
        if (!double.IsFinite(aset)) throw new ArgumentOutOfRangeException(nameof(aset), aset, "GGOV1: Aset must be finite.");
        LibraryChecks.NonNegative(who, "Ka", ka);
        LibraryChecks.NonNegative(who, "Ta", ta);
        LibraryChecks.NonNegative(who, "Trate", trate);
        if (!(rup > 0.0)) throw new ArgumentOutOfRangeException(nameof(rup), rup, "GGOV1: Rup must be above 0.");
        if (!(rdown < 0.0)) throw new ArgumentOutOfRangeException(nameof(rdown), rdown, "GGOV1: Rdown must be below 0.");
        if (trate > 0.0 && !(machineBaseMva > 0.0 && double.IsFinite(machineBaseMva)))
            throw new ArgumentOutOfRangeException(nameof(machineBaseMva), machineBaseMva, "GGOV1: with Trate above 0 the machine base (MVA) must be given to convert to the turbine base.");

        _rselect = rselect; _flag = flag; _r = r; _maxErr = maxErr; _minErr = minErr; _kp = kpgov; _ki = kigov; _kd = kdgov;
        _vMin = vMin; _vMax = vMax; _tAct = tact; _kturb = kturb; _wfnl = wfnl; _kpLoad = kpload; _kiLoad = kiload; _ldref = ldref;
        _dm = dm; _ropen = ropen; _rclose = rclose; _kimw = kimw; _aset = aset; _ka = ka; _ta = ta; _rup = rup; _rdown = rdown;
        _toTurbine = trate > 0.0 ? machineBaseMva / trate : 1.0;
        _pelecLag = new FirstOrderLag(1.0, tpelec);
        _derivative = kdgov > 0.0 ? new Washout(tdgov) : null;
        _derivativeGain = kdgov > 0.0 ? kdgov / tdgov : 0.0;
        _turbine = LibraryChecks.Stage(who, tc, "Tc", tb, "Tb");
        _temperature = LibraryChecks.Stage(who, tsa, "Tsa", tsb, "Tsb");
        _texm = new FirstOrderLag(1.0, tfload);
        _loadLimiter = kpload > 0.0 || kiload > 0.0;
        _accelerationLimiter = ka > 0.0 && ta > 0.0;
        _acceleration = _accelerationLimiter ? new Washout(ta) : null;
    }

    /// <summary>True for Rselect 1 or a supervisory load controller (Kimw above 0).</summary>
    public bool NeedsElectricalPower => _rselect == 1 || _kimw > 0.0;

    /// <summary>The valve stroke after the last step (turbine base).</summary>
    public double ValveStroke => _valve;

    /// <summary>The governor output fsr (the low-value select, limited) after the last step.</summary>
    public double GovernorOutput => _fsr;

    /// <summary>The fuel stroke that holds <paramref name="mechanicalPowerPu"/> (machine base) at rated speed.</summary>
    public double EquilibriumStroke(double mechanicalPowerPu) => (mechanicalPowerPu * _toTurbine + _dm) / _kturb + _wfnl;

    /// <inheritdoc/>
    public void Initialize(double mechanicalPowerPu)
    {
        double pm = mechanicalPowerPu * _toTurbine;
        double turb = pm + _dm;
        double fsr0 = turb / _kturb + _wfnl;
        ControlBlocks.RequireEquilibriumWithinLimits("GGOV1", "fuel stroke fsr = valve", fsr0, _vMin, _vMax, nameof(mechanicalPowerPu));
        if (_loadLimiter)
        {
            double margin = _ldref / _kturb + _wfnl - fsr0;
            if (!(margin >= 0.0))
                throw new ArgumentOutOfRangeException(nameof(mechanicalPowerPu), mechanicalPowerPu,
                    $"GGOV1: the load limiter binds at the operating point (Ldref/Kturb + Wfnl = {ControlBlocks.Num(_ldref / _kturb + _wfnl)} is below the fuel stroke {ControlBlocks.Num(fsr0)}), so the start is not an equilibrium; raise Ldref.");
        }
        _xI = fsr0;
        _xMw = 0.0;
        _xLoad = Math.Min(fsr0, 1.0);
        _valve = fsr0;
        _fsr = fsr0;
        _derivative?.Initialize(0.0);
        _acceleration?.Initialize(0.0);
        _turbine?.Initialize(_kturb * (fsr0 - _wfnl));
        _temperature?.Initialize(fsr0);
        _texm.Initialize(fsr0);
        _pelecLag.Initialize(pm);
        _pref = _rselect switch { 1 => _r * pm, -1 or -2 => _r * fsr0, _ => 0.0 };
        _pmwset = pm;
        _pending = true;
    }

    /// <summary>Change the speed/load reference Pref (turbine base) without touching a state.</summary>
    public void SetReference(double referencePu)
    {
        if (!double.IsFinite(referencePu)) throw new ArgumentOutOfRangeException(nameof(referencePu), referencePu, "GGOV1: the reference must be finite.");
        _pref = referencePu;
        _pending = false;
    }

    /// <inheritdoc/>
    public double Step(double speedDeviationPu, double dtSeconds)
    {
        if (NeedsElectricalPower)
            throw new InvalidOperationException("GGOV1 with Rselect 1 or a supervisory load controller reads the electrical power; the caller must use the step that supplies it.");
        return Core(speedDeviationPu, double.NaN, dtSeconds);
    }

    /// <inheritdoc/>
    public double Step(double speedDeviationPu, double electricalPowerPu, double dtSeconds)
    {
        if (NeedsElectricalPower && !double.IsFinite(electricalPowerPu))
            throw new InvalidOperationException("GGOV1: the electrical power is not a finite number.");
        return Core(speedDeviationPu, electricalPowerPu, dtSeconds);
    }

    private double Core(double dw, double pelecMachine, double dt)
    {
        double pe = pelecMachine * _toTurbine;
        if (_pending)
        {
            // The undisturbed point: the transducer, the droop reference and the supervisory set point from the measured power.
            if (NeedsElectricalPower)
            {
                _pelecLag.Initialize(pe);
                if (_rselect == 1) _pref = _r * pe;
                _pmwset = pe;
            }
            _pending = false;
        }
        double pef = NeedsElectricalPower ? _pelecLag.Step(pe, dt) : _pelecLag.Output;
        double fb = _rselect switch { 1 => pef, -1 => _valve, -2 => _fsr, _ => 0.0 };
        if (_kimw > 0.0) _xMw = Math.Clamp(_xMw + _kimw * (_pmwset - pef) * dt, -1.1 * _r, 1.1 * _r);
        double err = Math.Clamp(_pref + _xMw - _r * fb - dw, _minErr, _maxErr);
        double derivative = _derivative is null ? 0.0 : _derivativeGain * _derivative.Step(err, dt);
        _xI = Math.Clamp(_xI + _ki * err * dt, _vMin, _vMax);
        double fsr = _kp * err + _xI + derivative;
        if (_loadLimiter)
        {
            double e = _ldref / _kturb + _wfnl - _texm.Output;
            _xLoad = Math.Min(_xLoad + Math.Clamp(_kiLoad * e, _rdown, _rup) * dt, 1.0);
            fsr = Math.Min(fsr, Math.Min(_kpLoad * e + _xLoad, 1.0));
        }
        if (_accelerationLimiter)
        {
            double accel = _acceleration!.Step(dw, dt) / _ta;
            fsr = Math.Min(fsr, _fsr + _ka * dt * (_aset - accel));
        }
        _fsr = Math.Clamp(fsr, _vMin, _vMax);
        _valve = RateLimitedLag.Step(_valve, _fsr, _tAct, _ropen, _rclose, _vMin, _vMax, dt);
        double wf = _flag == 1 ? _valve * (1.0 + dw) : _valve;
        double drive = _kturb * (wf - _wfnl);
        double turb = _turbine is null ? drive : _turbine.Step(drive, dt);
        double temp = _temperature is null ? wf : _temperature.Step(wf, dt);
        _texm.Step(temp, dt);
        double pmech = turb - _dm * (1.0 + dw);
        return pmech / _toTurbine;
    }
}

/// <summary>WP3b (RC5): GGOV1 parameters (see <see cref="Ggov1"/>'s constructor, PSS/E order; per unit on the turbine
/// base Trate when given, else the machine base, whose MVA <see cref="MachineBaseMva"/> converts between them).</summary>
public sealed record Ggov1Data(
    int Rselect, int Flag, double R, double Tpelec, double MaxErr, double MinErr, double Kpgov, double Kigov, double Kdgov,
    double Tdgov, double Vmax, double Vmin, double Tact, double Kturb, double Wfnl, double Tb, double Tc, double Teng,
    double Tfload, double Kpload, double Kiload, double Ldref, double Dm, double Ropen, double Rclose, double Kimw,
    double Aset, double Ka, double Ta, double Trate, double Db, double Tsa, double Tsb, double Rup, double Rdown,
    double MachineBaseMva = 0.0) : GovernorData
{
    /// <inheritdoc/>
    public override string Model => "GGOV1";

    /// <inheritdoc/>
    public override IRmsGovernor Create() => new Ggov1(Rselect, Flag, R, Tpelec, MaxErr, MinErr, Kpgov, Kigov, Kdgov, Tdgov,
        Vmax, Vmin, Tact, Kturb, Wfnl, Tb, Tc, Teng, Tfload, Kpload, Kiload, Ldref, Dm, Ropen, Rclose, Kimw, Aset, Ka, Ta,
        Trate, Db, Tsa, Tsb, Rup, Rdown, MachineBaseMva);
}
