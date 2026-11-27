namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// The IEEE dc commutator exciters EXDC2 (IEEE 1981 type DC2) and IEEEX1 (IEEE 1979 type 1), PSS/E data TR KA TA TB TC
/// VRMAX VRMIN KE TE KF TF1 SWITCH E1 SE(E1) E2 SE(E2) for both. Structure (pu on the machine base):
/// <code>
///   Vc   = lag_{TR}(Vt)                                   (TR = 0: Vc = Vt)
///   VF   = sKF/(1 + sTF1) * Efd                           (rate feedback; KF = 0: none)
///   x    = leadlag_{TC/TB}(Vref - Vc + Vs - VF)           (TB = TC = 0 bypasses it)
///   VR   = lag_{KA, TA}(x), held in [Vt VRMIN, Vt VRMAX]  (EXDC2: the limits scale with the terminal voltage)
///                               or [VRMIN, VRMAX]         (IEEEX1: constant limits)
///   TE dEfd/dt = VR - (KE + SE(Efd)) Efd                  (SE(Efd) Efd = B (Efd - A)^2 above A)
/// </code>
/// The DC2 regulator is fed from the machine terminals, so its ceiling and floor are VRMAX and VRMIN times the terminal
/// voltage; that is the one difference from IEEEX1, which has fixed limits. The limit is non-windup: the regulator state is
/// held at the limit, so it leaves it on the first step the input reverses. SE is the IEEE quadratic through (E1, SE(E1))
/// and (E2, SE(E2)), fitted by <see cref="AcExciter.SaturationFit"/>; both SE 0 is no saturation. The exciter advances by
/// the exact zero-order hold of its start-of-step linearised pole (SE frozen) and the rate feedback reads the start-of-step
/// Efd, as <see cref="Esdc1a"/> does. IEEEX1 has the same arithmetic as <see cref="Esdc1a"/>, step for step.
/// <para>Initialisation: VR0 = (KE + SE(Efd0)) Efd0, so the regulator error is VR0 / KA. IEEEX1 refuses a VR0 outside
/// [VRMIN, VRMAX] at once; EXDC2 needs the terminal voltage for its limits, so the first step checks VR0 against
/// [Vt VRMIN, Vt VRMAX] at the undisturbed operating point and refuses it outside.</para>
/// </summary>
public sealed class Exdc2 : IRmsExciterModel
{
    private readonly bool _scaled;
    private readonly string _who;
    private readonly VoltageTransducer _tr;
    private readonly double _ka, _ta, _ke, _te, _kf, _tf, _satA, _satB, _vrMax, _vrMin;
    private readonly LeadLag? _shaping;
    private readonly Washout? _feedback;
    private double _vr, _efd;
    private bool _checkPending;

    private Exdc2(bool scaled, double tr, double ka, double ta, double tb, double tc, double vrMax, double vrMin, double ke, double te,
        double kf, double tf1, double e1, double se1, double e2, double se2)
    {
        _scaled = scaled;
        _who = scaled ? "EXDC2" : "IEEEX1";
        LibraryChecks.Positive(_who, "KA", ka);
        LibraryChecks.NonNegative(_who, "TA", ta);
        LibraryChecks.Positive(_who, "TE", te);
        LibraryChecks.NonNegative(_who, "KF", kf);
        LibraryChecks.NonNegative(_who, "TF1", tf1);
        if (kf > 0 && tf1 == 0) throw new ArgumentOutOfRangeException(nameof(tf1), tf1, $"{_who}: KF above 0 with TF1 = 0 is a pure derivative of Efd; give TF1 above 0.");
        if (!double.IsFinite(ke)) throw new ArgumentOutOfRangeException(nameof(ke), ke, $"{_who}: KE must be finite.");
        if (!double.IsFinite(vrMax) || !double.IsFinite(vrMin)) throw new ArgumentOutOfRangeException(nameof(vrMax), vrMax, $"{_who}: VRMAX and VRMIN must be finite.");
        LibraryChecks.Limits(_who, "VRMIN", vrMin, "VRMAX", vrMax);
        (_satA, _satB) = AcExciter.SaturationFit(_who, e1, se1, e2, se2);
        _tr = new VoltageTransducer(tr, _who);
        _ka = ka; _ta = ta; _ke = ke; _te = te; _kf = kf; _tf = tf1; _vrMax = vrMax; _vrMin = vrMin;
        _shaping = LibraryChecks.Stage(_who, tc, "TC", tb, "TB");
        _feedback = kf > 0 ? new Washout(tf1) : null;
    }

    /// <summary>EXDC2: the regulator limits are VRMAX and VRMIN times the terminal voltage. Values in PSS/E order (SWITCH is
    /// not a parameter here).</summary>
    public static Exdc2 Exdc2Model(double tr, double ka, double ta, double tb, double tc, double vrMax, double vrMin, double ke, double te,
        double kf, double tf1, double e1, double se1, double e2, double se2)
        => new(true, tr, ka, ta, tb, tc, vrMax, vrMin, ke, te, kf, tf1, e1, se1, e2, se2);

    /// <summary>IEEEX1: the regulator limits are VRMAX and VRMIN. Values in PSS/E order (SWITCH is not a parameter here).</summary>
    public static Exdc2 Ieeex1(double tr, double ka, double ta, double tb, double tc, double vrMax, double vrMin, double ke, double te,
        double kf, double tf1, double e1, double se1, double e2, double se2)
        => new(false, tr, ka, ta, tb, tc, vrMax, vrMin, ke, te, kf, tf1, e1, se1, e2, se2);

    /// <inheritdoc/>
    public string Kind => _who;

    /// <summary>EXDC2 reads the terminal voltage for its limits; IEEEX1 only when TR is above 0.</summary>
    public bool NeedsTerminalQuantities => _scaled || _tr.Active;

    /// <summary>True for EXDC2 (limits scaled by the terminal voltage), false for IEEEX1.</summary>
    internal bool ScaledLimits => _scaled;

    /// <summary>The regulator output VR after the last step.</summary>
    public double RegulatorOutput => _vr;

    private double SaturationProduct(double efd) => efd > _satA ? _satB * (efd - _satA) * (efd - _satA) : 0.0;

    private double RegulatorAtRest(double efd) => _ke * efd + SaturationProduct(efd);

    /// <inheritdoc/>
    public double EquilibriumErrorPu(double fieldVoltagePu) => RegulatorAtRest(fieldVoltagePu) / _ka;

    /// <inheritdoc/>
    public void Initialize(double fieldVoltagePu)
    {
        if (!double.IsFinite(fieldVoltagePu)) throw new ArgumentOutOfRangeException(nameof(fieldVoltagePu), fieldVoltagePu, $"{_who}: the initial field is not finite.");
        double vr = RegulatorAtRest(fieldVoltagePu);
        if (!_scaled)
            ControlBlocks.RequireEquilibriumWithinLimits(_who, "regulator output VR = (KE + SE) Efd", vr, _vrMin, _vrMax, nameof(fieldVoltagePu));
        _shaping?.Initialize(vr / _ka);
        _feedback?.Initialize(fieldVoltagePu);
        _tr.Initialize();
        _vr = vr;
        _efd = fieldVoltagePu;
        _checkPending = _scaled;
    }

    /// <inheritdoc/>
    public double Step(double voltageErrorPu, double stabiliserPu, FieldControlInputs inputs, double dtSeconds)
    {
        double lo = _vrMin, hi = _vrMax;
        if (_scaled)
        {
            double vt = inputs.TerminalVoltagePu;
            if (!double.IsFinite(vt))
                throw new InvalidOperationException($"{_who}: its regulator limits scale with the terminal voltage; the caller must use the step that supplies it.");
            lo = vt * _vrMin; hi = vt * _vrMax;
            if (_checkPending)
            {
                ControlBlocks.RequireEquilibriumWithinLimits(_who, "regulator output VR = (KE + SE) Efd against Vt VRMIN .. Vt VRMAX", _vr, lo, hi, nameof(inputs));
                _checkPending = false;
            }
        }
        double e = _tr.Error(voltageErrorPu, inputs.TerminalVoltagePu, dtSeconds);
        double vf = _feedback is null ? 0.0 : (_kf / _tf) * _feedback.OutputFor(_efd);
        double u = e + stabiliserPu - vf;
        double x = _shaping is null ? u : _shaping.Step(u, dtSeconds);
        // The regulator lag KA / (1 + s TA), exact zero-order hold, state held in [lo, hi] (non-windup), as FirstOrderLag.
        double target = _ka * x;
        if (_ta <= ControlBlocks.InstantThresholdSeconds) _vr = target;
        else
        {
            double a = DetMath.Exp(-dtSeconds / _ta);
            _vr = a * _vr + (1.0 - a) * target;
        }
        _vr = Math.Clamp(_vr, lo, hi);
        double se = _efd > 0 ? SaturationProduct(_efd) / _efd : 0.0;
        double pole = _ke + se;
        if (Math.Abs(pole) <= Ieeet1.ExciterPoleAtOriginTolerance) _efd += _vr / _te * dtSeconds;
        else
        {
            double a = DetMath.Exp(-dtSeconds * pole / _te);
            _efd = a * _efd + (1.0 - a) * _vr / pole;
        }
        _feedback?.Step(_efd, dtSeconds);
        return _efd;
    }
}
