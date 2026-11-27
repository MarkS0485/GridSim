namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// ESAC8B, the IEEE 421.5-2005 type AC8B excitation system with a PID voltage regulator (PSS/E ESAC8B, data TR KP KI KD
/// TD KA TA VRMAX VRMIN TE KE E1 SE(E1) E2 SE(E2)). Structure (pu on the machine base; IFD the field current in the
/// non-reciprocal Xad base):
/// <code>
///   Vc   = lag_{TR}(Vt)                                         (TR = 0: Vc = Vt)
///   e    = Vref - Vc + Vs
///   u    = KP e + KI/s e + s KD/(1 + s TD) e                    (the PID; KD = 0 or TD = 0 drops the derivative)
///   VR   = lag_{KA, TA, [VRMIN, VRMAX]}(u)                      (non-windup)
///   TE dVE/dt = VR - (KE + SE(VE)) VE - KDexc IFD
///   Efd  = VE FEX(KC IFD / VE)                                  (KC = 0: Efd = VE)
/// </code>
/// The PSS/E record has no KC and no exciter demagnetising factor, so a DYR record gives KC = KDexc = 0 and Efd = VE; a
/// gridsim-dynamics/2 file may give both (ac8bKc, ac8bKdExc), and then the rectifier regulation FEX and the inverse used at
/// initialisation are <see cref="AcExciter"/>'s, shared, not copied. With KC above 0, VE at or below 0 gives Efd = 0 (a diode
/// rectifier). SE is the IEEE quadratic through (E1, SE(E1)) and (E2, SE(E2)) (<see cref="AcExciter.SaturationFit"/>).
/// <para>Discretisation, each step on the held error: the integral is the exact zero-order hold of KI/s (a ramp,
/// x += KI e dt), the derivative is (KD / TD) times the exact-hold washout of TD, the regulator lag is the exact hold
/// of its pole, and the exciter is the exact hold of its start-of-step linearised pole (SE frozen, KDexc IFD held) driven
/// by the new VR, the operator splitting of <see cref="AcExciter"/> and <see cref="Esdc1a"/>.</para>
/// <para>Initialisation: VE0 from Efd0 and IFD0 = Efd0, VR0 = (KE + SE(VE0)) VE0 + KDexc Efd0, which must lie in
/// [VRMIN, VRMAX]. With KI above 0 the integral holds VR0 / KA and the error is 0; with KI = 0 the error is VR0 / (KA KP)
/// (KP must then be above 0).</para>
/// <para>Not modelled: the 2016 PID output limits, VFEMAX and VEMIN, and the UEL/OEL inputs.</para>
/// </summary>
public sealed class Esac8b : IRmsExciterModel
{
    private readonly VoltageTransducer _tr;
    private readonly double _kp, _ki, _kd, _td, _ka, _te, _ke, _kc, _kdExc, _satA, _satB;
    private readonly FirstOrderLag _regulator;
    private readonly Washout? _derivative;
    private double _integral, _ve, _pid;

    /// <summary>The ESAC8B data in PSS/E order, then the file-only rectifier loading factor KC and demagnetising factor
    /// KDexc (both 0 for a PSS/E record).</summary>
    public Esac8b(double tr, double kp, double ki, double kd, double td, double ka, double ta, double vrMax, double vrMin,
        double te, double ke, double e1, double se1, double e2, double se2, double kc = 0.0, double kdExc = 0.0)
    {
        const string who = "ESAC8B";
        LibraryChecks.NonNegative(who, "KP", kp);
        LibraryChecks.NonNegative(who, "KI", ki);
        LibraryChecks.NonNegative(who, "KD", kd);
        LibraryChecks.NonNegative(who, "TD", td);
        LibraryChecks.Positive(who, "KA", ka);
        LibraryChecks.NonNegative(who, "TA", ta);
        LibraryChecks.Positive(who, "TE", te);
        LibraryChecks.NonNegative(who, "KC", kc);
        LibraryChecks.NonNegative(who, "KDexc", kdExc);
        if (!double.IsFinite(ke)) throw new ArgumentOutOfRangeException(nameof(ke), ke, "ESAC8B: KE must be finite.");
        if (kp == 0 && ki == 0) throw new ArgumentException("ESAC8B: KP and KI are both 0, so the regulator has no steady gain.", nameof(kp));
        if (kd > 0 && td == 0) throw new ArgumentOutOfRangeException(nameof(td), td, "ESAC8B: KD above 0 with TD = 0 is a pure derivative of the error; give TD above 0.");
        LibraryChecks.Limits(who, "VRMIN", vrMin, "VRMAX", vrMax);
        (_satA, _satB) = AcExciter.SaturationFit(who, e1, se1, e2, se2);
        _tr = new VoltageTransducer(tr, who);
        _kp = kp; _ki = ki; _kd = kd; _td = td; _ka = ka; _te = te; _ke = ke; _kc = kc; _kdExc = kdExc;
        _regulator = new FirstOrderLag(ka, ta, vrMin, vrMax);
        _derivative = kd > 0 ? new Washout(td) : null;
    }

    /// <inheritdoc/>
    public string Kind => "ESAC8B";

    /// <summary>True when TR is above 0.</summary>
    public bool NeedsTerminalQuantities => _tr.Active;

    /// <summary>The PID output u after the last step (before the regulator lag).</summary>
    public double PidOutput => _pid;

    /// <summary>The regulator output VR after the last step.</summary>
    public double RegulatorOutput => _regulator.Output;

    /// <summary>The exciter alternator voltage VE after the last step.</summary>
    public double ExciterVoltage => _ve;

    private double SaturationProduct(double ve) => ve > _satA ? _satB * (ve - _satA) * (ve - _satA) : 0.0;

    private double SolveVe(double efd) => _kc > 0 ? AcExciter.SolveVe(Kind, _kc, efd, efd) : efd;

    private double RegulatorAtRest(double efd)
    {
        double ve = SolveVe(efd);
        return _ke * ve + SaturationProduct(ve) + _kdExc * efd;
    }

    /// <inheritdoc/>
    public double EquilibriumErrorPu(double fieldVoltagePu) => _ki > 0 ? 0.0 : RegulatorAtRest(fieldVoltagePu) / (_ka * _kp);

    /// <inheritdoc/>
    public void Initialize(double fieldVoltagePu)
    {
        if (!double.IsFinite(fieldVoltagePu)) throw new ArgumentOutOfRangeException(nameof(fieldVoltagePu), fieldVoltagePu, "ESAC8B: the initial field is not finite.");
        double ve = SolveVe(fieldVoltagePu);
        double vr = _ke * ve + SaturationProduct(ve) + _kdExc * fieldVoltagePu;
        ControlBlocks.RequireEquilibriumWithinLimits(Kind, "regulator output VR = (KE + SE) VE + KDexc IFD", vr, _regulator.Min, _regulator.Max, nameof(fieldVoltagePu));
        double e0 = EquilibriumErrorPu(fieldVoltagePu);
        _pid = vr / _ka;
        _integral = _ki > 0 ? _pid : 0.0;
        _derivative?.Initialize(e0);
        _regulator.Initialize(vr);
        _tr.Initialize();
        _ve = ve;
    }

    /// <inheritdoc/>
    public double Step(double voltageErrorPu, double stabiliserPu, FieldControlInputs inputs, double dtSeconds)
    {
        if ((_kc > 0 || _kdExc > 0) && !double.IsFinite(inputs.FieldCurrentPu))
            throw new InvalidOperationException("ESAC8B with KC or KDexc above 0 reads the machine's field current; the caller must use the step that supplies it.");
        double ifd = _kc > 0 || _kdExc > 0 ? inputs.FieldCurrentPu : 0.0;
        double e = _tr.Error(voltageErrorPu, inputs.TerminalVoltagePu, dtSeconds) + stabiliserPu;
        _integral += _ki * e * dtSeconds;
        double d = _derivative is null ? 0.0 : (_kd / _td) * _derivative.Step(e, dtSeconds);
        _pid = _kp * e + _integral + d;
        double vr = _regulator.Step(_pid, dtSeconds);

        double se = _ve > 0 ? SaturationProduct(_ve) / _ve : 0.0;
        double pole = _ke + se;
        double drive = vr - _kdExc * ifd;
        if (Math.Abs(pole) <= Ieeet1.ExciterPoleAtOriginTolerance) _ve += drive / _te * dtSeconds;
        else
        {
            double a = DetMath.Exp(-dtSeconds * pole / _te);
            _ve = a * _ve + (1.0 - a) * drive / pole;
        }
        if (_kc == 0) return _ve;
        return _ve > 0 ? _ve * AcExciter.Fex(_kc * ifd / _ve) : 0.0;
    }
}
