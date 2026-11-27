namespace GridSim.Core.Dynamics.Models;

/// <summary>Which static potential-source exciter an <see cref="St1Exciter"/> is.</summary>
public enum St1Variant
{
    /// <summary>PSS/E EXST1, the IEEE 1981 type ST1: one lead-lag, the regulator with no limit of its own, the output
    /// limits only.</summary>
    Exst1,
    /// <summary>PSS/E ESST1A, the IEEE 421.5-2005 type ST1A: two lead-lags, the regulator limited to VAMIN..VAMAX
    /// (non-windup), the field current limiter, and the stabiliser input at the summing junction or after the
    /// regulator (VOS).</summary>
    Esst1a,
}

/// <summary>
/// The potential-source controlled-rectifier exciters of the IEEE ST1 family (PSS/E EXST1 and ESST1A). The rectifier is
/// fed from the terminal voltage, so the ceiling moves with it. Structure (pu on the machine base; Vt the terminal
/// voltage magnitude, IFD the field current in the non-reciprocal Xad base):
/// <code>
///   Vc   = lag_{TR}(Vt)                                       (TR = 0: Vc = Vt)
///   VF   = sKF/(1 + sTF) * Efd                                (rate feedback; KF = 0: none)
///   u    = clamp(Vref - Vc + Vs[VOS 1] - VF, VIMIN, VIMAX)    (input limiter)
///   x    = leadlag_{TC/TB}(u), then leadlag_{TC1/TB1} (ESST1A)
///   VA   = lag_{KA, TA}(x)                                    (ESST1A: VAMIN..VAMAX, non-windup; EXST1: no limit)
///   v    = VA + Vs[VOS 2] - max(0, KLR (IFD - ILR))           (field current limiter; EXST1 has none)
///   Efd  = clamp(v, Vt VRMIN, Vt VRMAX - KC IFD)              (the lower bound wins if the two cross)
/// </code>
/// The under-excitation limiter input (ESST1A UEL) is not modelled, since GridSim has no UEL model to drive it. Every
/// block is the library's exact zero-order hold; the rate feedback reads the start-of-step Efd (as <see cref="Ieeet1"/>
/// does). Steady state: VA0 = Efd0 + max(0, KLR (Efd0 - ILR)), so the regulator error is VA0 / KA. <see cref="Initialize"/>
/// refuses a VA0 outside VAMIN..VAMAX or an error outside VIMIN..VIMAX; the output limits need Vt, so the first step
/// checks Efd0 against them.
/// </summary>
public sealed class St1Exciter : IRmsExciterModel
{
    private readonly St1Variant _variant;
    private readonly VoltageTransducer _tr;
    private readonly double _viMin, _viMax, _ka, _vrMin, _vrMax, _kc, _kf, _tf, _klr, _ilr;
    private readonly int _vos;
    private readonly LeadLag? _ll1, _ll2;
    private readonly FirstOrderLag _regulator;
    private readonly Washout? _feedback;
    private double _efd;
    private bool _pending;

    private St1Exciter(St1Variant variant, double tr, double viMin, double viMax, double tc, double tb, double tc1, double tb1,
        double ka, double ta, double vaMin, double vaMax, double vrMin, double vrMax, double kc, double kf, double tf,
        double klr, double ilr, int vos)
    {
        string who = variant == St1Variant.Exst1 ? "EXST1" : "ESST1A";
        LibraryChecks.Positive(who, "KA", ka);
        LibraryChecks.NonNegative(who, "TA", ta);
        LibraryChecks.NonNegative(who, "KC", kc);
        LibraryChecks.NonNegative(who, "KF", kf);
        LibraryChecks.NonNegative(who, "TF", tf);
        LibraryChecks.NonNegative(who, "KLR", klr);
        if (!double.IsFinite(ilr)) throw new ArgumentOutOfRangeException(nameof(ilr), ilr, $"{who}: ILR must be finite.");
        if (kf > 0 && tf == 0) throw new ArgumentOutOfRangeException(nameof(tf), tf, $"{who}: KF above 0 with TF = 0 is a pure derivative of Efd; give TF above 0.");
        LibraryChecks.Limits(who, "VIMIN", viMin, "VIMAX", viMax);
        LibraryChecks.Limits(who, "VAMIN", vaMin, "VAMAX", vaMax);
        LibraryChecks.Limits(who, "VRMIN", vrMin, "VRMAX", vrMax);
        if (vos is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(vos), vos, $"{who}: VOS must be 1 (stabiliser at the summing junction) or 2 (after the regulator).");
        _variant = variant;
        _tr = new VoltageTransducer(tr, who);
        _viMin = viMin; _viMax = viMax; _ka = ka; _vrMin = vrMin; _vrMax = vrMax; _kc = kc; _kf = kf; _tf = tf; _klr = klr; _ilr = ilr; _vos = vos;
        _ll1 = LibraryChecks.Stage(who, tc, "TC", tb, "TB");
        _ll2 = LibraryChecks.Stage(who, tc1, "TC1", tb1, "TB1");
        _regulator = new FirstOrderLag(ka, ta, vaMin, vaMax);
        _feedback = kf > 0 ? new Washout(tf) : null;
    }

    /// <summary>PSS/E EXST1 (data TR VIMAX VIMIN TC TB KA TA VRMAX VRMIN KC KF TF).</summary>
    public static St1Exciter Exst1(double tr, double viMax, double viMin, double tc, double tb, double ka, double ta,
        double vrMax, double vrMin, double kc, double kf, double tf)
        => new(St1Variant.Exst1, tr, viMin, viMax, tc, tb, 0.0, 0.0, ka, ta, double.NegativeInfinity, double.PositiveInfinity,
            vrMin, vrMax, kc, kf, tf, 0.0, 0.0, 1);

    /// <summary>PSS/E ESST1A (ICONs UEL VOS, data TR VIMAX VIMIN TC TB TC1 TB1 KA TA VAMAX VAMIN VRMAX VRMIN KC KF TF KLR
    /// ILR). <paramref name="vos"/> 1 adds the stabiliser at the summing junction, 2 after the regulator.</summary>
    public static St1Exciter Esst1a(double tr, double viMax, double viMin, double tc, double tb, double tc1, double tb1,
        double ka, double ta, double vaMax, double vaMin, double vrMax, double vrMin, double kc, double kf, double tf,
        double klr, double ilr, int vos = 1)
        => new(St1Variant.Esst1a, tr, viMin, viMax, tc, tb, tc1, tb1, ka, ta, vaMin, vaMax, vrMin, vrMax, kc, kf, tf, klr, ilr, vos);

    /// <inheritdoc/>
    public string Kind => _variant == St1Variant.Exst1 ? "EXST1" : "ESST1A";

    /// <summary>Always true: the ceiling is Vt VRMAX - KC IFD.</summary>
    public bool NeedsTerminalQuantities => true;

    /// <summary>True when TR is above 0: the voltage transducer starts at the first step's terminal voltage.</summary>
    internal bool TransducerActive => _tr.Active;

    /// <summary>The regulator output VA after the last step.</summary>
    public double RegulatorOutput => _regulator.Output;

    private double FieldCurrentLimit(double ifd)
    {
        double over = _klr * (ifd - _ilr);
        return over > 0.0 ? over : 0.0;
    }

    /// <inheritdoc/>
    public double EquilibriumErrorPu(double fieldVoltagePu) => (fieldVoltagePu + FieldCurrentLimit(fieldVoltagePu)) / _ka;

    /// <inheritdoc/>
    public void Initialize(double fieldVoltagePu)
    {
        if (!double.IsFinite(fieldVoltagePu)) throw new ArgumentOutOfRangeException(nameof(fieldVoltagePu), fieldVoltagePu, $"{Kind}: the initial field is not finite.");
        double va = fieldVoltagePu + FieldCurrentLimit(fieldVoltagePu);
        ControlBlocks.RequireEquilibriumWithinLimits(Kind, "regulator output VA", va, _regulator.Min, _regulator.Max, nameof(fieldVoltagePu));
        double x = va / _ka;
        ControlBlocks.RequireEquilibriumWithinLimits(Kind, "regulator input (the input limiter)", x, _viMin, _viMax, nameof(fieldVoltagePu));
        _ll1?.Initialize(x);
        _ll2?.Initialize(x);
        _regulator.Initialize(va);
        _feedback?.Initialize(fieldVoltagePu);
        _tr.Initialize();
        _efd = fieldVoltagePu;
        _pending = true;
    }

    /// <inheritdoc/>
    public double Step(double voltageErrorPu, double stabiliserPu, FieldControlInputs inputs, double dtSeconds)
    {
        double vt = inputs.TerminalVoltagePu, ifd = inputs.FieldCurrentPu;
        if (!double.IsFinite(vt) || !double.IsFinite(ifd))
            throw new InvalidOperationException($"{Kind} reads the terminal voltage and the field current; the caller must use the step that supplies them.");
        if (_pending)
        {
            ControlBlocks.RequireEquilibriumWithinLimits(Kind, "field voltage Efd (between Vt VRMIN and Vt VRMAX - KC IFD)", _efd,
                vt * _vrMin, vt * _vrMax - _kc * _efd, "fieldVoltagePu");
            _pending = false;
        }
        double e = _tr.Error(voltageErrorPu, vt, dtSeconds);
        double vf = _feedback is null ? 0.0 : (_kf / _tf) * _feedback.OutputFor(_efd);
        double u = Math.Clamp(e + (_vos == 1 ? stabiliserPu : 0.0) - vf, _viMin, _viMax);
        double x = _ll1 is null ? u : _ll1.Step(u, dtSeconds);
        x = _ll2 is null ? x : _ll2.Step(x, dtSeconds);
        double va = _regulator.Step(x, dtSeconds);
        double v = va + (_vos == 2 ? stabiliserPu : 0.0) - FieldCurrentLimit(ifd);
        double upper = vt * _vrMax - _kc * ifd, lower = vt * _vrMin;
        double efd = Math.Max(lower, Math.Min(v, upper));
        _feedback?.Step(efd, dtSeconds);
        _efd = efd;
        return efd;
    }
}
