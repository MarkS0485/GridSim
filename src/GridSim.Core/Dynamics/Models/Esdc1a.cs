namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// ESDC1A, the IEEE 421.5-2005 type DC1A field-controlled dc commutator exciter (PSS/E ESDC1A, data TR KA TA TB TC VRMAX
/// VRMIN KE TE KF TF1 SWITCH E1 SE(E1) E2 SE(E2)). Structure (pu on the machine base):
/// <code>
///   Vc   = lag_{TR}(Vt)                                   (TR = 0: Vc = Vt)
///   VF   = sKF/(1 + sTF1) * Efd                           (rate feedback; KF = 0: none)
///   x    = leadlag_{TC/TB}(Vref - Vc + Vs - VF)           (TB = TC = 0 bypasses it)
///   VR   = lag_{KA, TA, [VRMIN, VRMAX]}(x)                (non-windup)
///   TE dEfd/dt = VR - (KE + SE(Efd)) Efd                  (SE(Efd) Efd = B (Efd - A)^2 above A)
/// </code>
/// SE is the IEEE 421.5 quadratic saturation through (E1, SE(E1)) and (E2, SE(E2)), fitted as for the AC exciters
/// (<see cref="AcExciter"/>); both SE 0 is no saturation. The exciter advances by the exact zero-order hold of its
/// start-of-step linearised pole (SE frozen), as <see cref="Ieeet1"/> does; the rate feedback reads the start-of-step Efd.
/// Steady state: VR0 = (KE + SE(Efd0)) Efd0, so the regulator error is VR0 / KA. A VR0 outside VRMIN..VRMAX is refused.
/// </summary>
public sealed class Esdc1a : IRmsExciterModel
{
    private readonly VoltageTransducer _tr;
    private readonly double _ka, _ke, _te, _kf, _tf, _satA, _satB;
    private readonly LeadLag? _shaping;
    private readonly FirstOrderLag _regulator;
    private readonly Washout? _feedback;
    private double _efd;

    /// <summary>The DC1A data in PSS/E order (SWITCH is not a parameter here).</summary>
    public Esdc1a(double tr, double ka, double ta, double tb, double tc, double vrMax, double vrMin, double ke, double te,
        double kf, double tf1, double e1, double se1, double e2, double se2)
    {
        LibraryChecks.Positive("ESDC1A", "KA", ka);
        LibraryChecks.NonNegative("ESDC1A", "TA", ta);
        LibraryChecks.Positive("ESDC1A", "TE", te);
        LibraryChecks.NonNegative("ESDC1A", "KF", kf);
        LibraryChecks.NonNegative("ESDC1A", "TF1", tf1);
        if (kf > 0 && tf1 == 0) throw new ArgumentOutOfRangeException(nameof(tf1), tf1, "ESDC1A: KF above 0 with TF1 = 0 is a pure derivative of Efd; give TF1 above 0.");
        if (!double.IsFinite(ke)) throw new ArgumentOutOfRangeException(nameof(ke), ke, "ESDC1A: KE must be finite.");
        LibraryChecks.Limits("ESDC1A", "VRMIN", vrMin, "VRMAX", vrMax);
        (_satA, _satB) = AcExciter.SaturationFit("ESDC1A", e1, se1, e2, se2);
        _tr = new VoltageTransducer(tr, "ESDC1A");
        _ka = ka; _ke = ke; _te = te; _kf = kf; _tf = tf1;
        _shaping = LibraryChecks.Stage("ESDC1A", tc, "TC", tb, "TB");
        _regulator = new FirstOrderLag(ka, ta, vrMin, vrMax);
        _feedback = kf > 0 ? new Washout(tf1) : null;
    }

    /// <inheritdoc/>
    public string Kind => "ESDC1A";

    /// <summary>True when TR is above 0.</summary>
    public bool NeedsTerminalQuantities => _tr.Active;

    /// <summary>The regulator output VR after the last step.</summary>
    public double RegulatorOutput => _regulator.Output;

    private double SaturationProduct(double efd) => efd > _satA ? _satB * (efd - _satA) * (efd - _satA) : 0.0;

    private double RegulatorAtRest(double efd) => _ke * efd + SaturationProduct(efd);

    /// <inheritdoc/>
    public double EquilibriumErrorPu(double fieldVoltagePu) => RegulatorAtRest(fieldVoltagePu) / _ka;

    /// <inheritdoc/>
    public void Initialize(double fieldVoltagePu)
    {
        if (!double.IsFinite(fieldVoltagePu)) throw new ArgumentOutOfRangeException(nameof(fieldVoltagePu), fieldVoltagePu, "ESDC1A: the initial field is not finite.");
        double vr = RegulatorAtRest(fieldVoltagePu);
        ControlBlocks.RequireEquilibriumWithinLimits("ESDC1A", "regulator output VR = (KE + SE) Efd", vr, _regulator.Min, _regulator.Max, nameof(fieldVoltagePu));
        _shaping?.Initialize(vr / _ka);
        _regulator.Initialize(vr);
        _feedback?.Initialize(fieldVoltagePu);
        _tr.Initialize();
        _efd = fieldVoltagePu;
    }

    /// <inheritdoc/>
    public double Step(double voltageErrorPu, double stabiliserPu, FieldControlInputs inputs, double dtSeconds)
    {
        double e = _tr.Error(voltageErrorPu, inputs.TerminalVoltagePu, dtSeconds);
        double vf = _feedback is null ? 0.0 : (_kf / _tf) * _feedback.OutputFor(_efd);
        double u = e + stabiliserPu - vf;
        double x = _shaping is null ? u : _shaping.Step(u, dtSeconds);
        double vr = _regulator.Step(x, dtSeconds);
        double se = _efd > 0 ? SaturationProduct(_efd) / _efd : 0.0;
        double pole = _ke + se;
        if (Math.Abs(pole) <= Ieeet1.ExciterPoleAtOriginTolerance) _efd += vr / _te * dtSeconds;
        else
        {
            double a = DetMath.Exp(-dtSeconds * pole / _te);
            _efd = a * _efd + (1.0 - a) * vr / pole;
        }
        _feedback?.Step(_efd, dtSeconds);
        return _efd;
    }
}
