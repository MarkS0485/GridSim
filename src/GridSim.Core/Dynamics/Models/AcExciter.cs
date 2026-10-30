namespace GridSim.Core.Dynamics.Models;

/// <summary>Which alternator-rectifier excitation system an <see cref="AcExciter"/> is.</summary>
public enum AcExciterVariant
{
    /// <summary>IEEE 1981 type AC1 (PSS/E EXAC1): the regulator lag carries the VR limits; the exciter voltage VE has
    /// no floor.</summary>
    Exac1,
    /// <summary>IEEE 421.5-2005 type AC1A (ESAC1A): the regulator lag carries VAMIN..VAMAX, its output is then held in
    /// VRMIN..VRMAX, and the exciter voltage VE has a floor of 0.</summary>
    Esac1a,
}

/// <summary>
/// The alternator-rectifier excitation systems of the IEEE AC1 family (EXAC1, ESAC1A): a field-controlled alternator
/// whose output is rectified by non-controlled diodes, so the field voltage falls as the field current loads the
/// rectifier. Structure (all per-unit; IFD the machine's field current in the non-reciprocal Xad base, equal to Efd in
/// steady state):
/// <code>
///   VFE  = VE*(KE + SE(VE)) + KD*IFD                     (exciter field demand: self-excitation, saturation, armature reaction)
///   VF   = sKF/(1+sTF) * VFE                             (rate feedback)
///   x    = leadlag_{TC/TB}(Verr - VF)                    (Verr = Vref - Vt + Vs)
///   EXAC1:  VR = lag_{KA, TA, [VRMIN, VRMAX]}(x)
///   ESAC1A: VA = lag_{KA, TA, [VAMIN, VAMAX]}(x),  VR = clamp(VA, VRMIN, VRMAX)
///   TE dVE/dt = VR - VFE                                 (ESAC1A: VE &gt;= 0)
///   IN   = KC*IFD / VE,  Efd = VE * FEX(IN)
///   FEX  = 1 (IN &lt;= 0), 1 - 0.577 IN (IN &lt;= 0.433), sqrt(0.75 - IN^2) (IN &lt;= 0.75), 1.732 (1 - IN) (IN &lt;= 1), 0 above
/// </code>
/// SE is the quadratic saturation through (E1, SE1) and (E2, SE2): VE*SE(VE) = B*(VE - A)^2 above A, else 0, with
/// A and B from the two points (no saturation when SE1 = SE2 = 0). The FEX constants and the quadratic form are the
/// IEEE 421.5 ones (its rectifier regulation annex). Each step reads VFE at the start-of-step VE (as <see cref="Ieeet1"/>
/// reads its feedback), advances the lead-lag and the regulator with the exact zero-order hold, then the exciter with the
/// exact zero-order hold of its start-of-step linearised pole (SE frozen, KD*IFD held), the same operator splitting
/// <see cref="Ieeet1"/> uses; the field current is held across the step like every other input.
/// </summary>
public sealed class AcExciter
{
    private readonly AcExciterVariant _variant;
    private readonly double _ka, _te, _kf, _tf, _kc, _kd, _ke;
    private readonly double _vrMin, _vrMax;
    private readonly double _satA, _satB;
    private readonly LeadLag? _leadLag;
    private readonly FirstOrderLag _regulator;
    private readonly Washout _feedback;
    private double _ve;

    /// <param name="variant">EXAC1 or ESAC1A.</param>
    /// <param name="leadSeconds">Lead time constant TC (s), 0 or above.</param>
    /// <param name="lagSeconds">Lag time constant TB (s), 0 or above; TB = TC = 0 bypasses the lead-lag.</param>
    /// <param name="regulatorGain">Regulator gain KA, above 0.</param>
    /// <param name="regulatorTimeSeconds">Regulator time constant TA (s), 0 or above.</param>
    /// <param name="regulatorMinPu">The regulator lag's lower limit: VRMIN for EXAC1, VAMIN for ESAC1A.</param>
    /// <param name="regulatorMaxPu">The regulator lag's upper limit: VRMAX for EXAC1, VAMAX for ESAC1A.</param>
    /// <param name="outputMinPu">ESAC1A VRMIN (ignored, must be negative infinity, for EXAC1).</param>
    /// <param name="outputMaxPu">ESAC1A VRMAX (ignored, must be positive infinity, for EXAC1).</param>
    /// <param name="exciterTimeSeconds">Exciter time constant TE (s), above 0.</param>
    /// <param name="feedbackGain">Rate feedback gain KF, 0 or above.</param>
    /// <param name="feedbackTimeSeconds">Rate feedback time constant TF (s), above 0.</param>
    /// <param name="rectifierKc">Rectifier loading factor KC, 0 or above.</param>
    /// <param name="demagnetisingKd">Demagnetising factor KD, 0 or above.</param>
    /// <param name="exciterConstant">Exciter constant KE.</param>
    /// <param name="satE1">Saturation point E1 (pu).</param>
    /// <param name="satSe1">SE(E1), 0 or above.</param>
    /// <param name="satE2">Saturation point E2 (pu).</param>
    /// <param name="satSe2">SE(E2), 0 or above.</param>
    public AcExciter(AcExciterVariant variant, double leadSeconds, double lagSeconds, double regulatorGain, double regulatorTimeSeconds,
        double regulatorMinPu, double regulatorMaxPu, double outputMinPu, double outputMaxPu, double exciterTimeSeconds,
        double feedbackGain, double feedbackTimeSeconds, double rectifierKc, double demagnetisingKd, double exciterConstant,
        double satE1, double satSe1, double satE2, double satSe2)
    {
        string who = variant == AcExciterVariant.Exac1 ? "EXAC1" : "ESAC1A";
        if (!(regulatorGain > 0) || !double.IsFinite(regulatorGain)) throw new ArgumentOutOfRangeException(nameof(regulatorGain), regulatorGain, $"{who}: KA must be a finite number above 0.");
        if (!(exciterTimeSeconds > 0) || !double.IsFinite(exciterTimeSeconds)) throw new ArgumentOutOfRangeException(nameof(exciterTimeSeconds), exciterTimeSeconds, $"{who}: TE must be a finite number above 0.");
        if (!(feedbackTimeSeconds > 0) || !double.IsFinite(feedbackTimeSeconds)) throw new ArgumentOutOfRangeException(nameof(feedbackTimeSeconds), feedbackTimeSeconds, $"{who}: TF must be a finite number above 0.");
        foreach ((double v, string n) in new[] { (feedbackGain, "KF"), (rectifierKc, "KC"), (demagnetisingKd, "KD"), (satSe1, "SE(E1)"), (satSe2, "SE(E2)"), (leadSeconds, "TC"), (lagSeconds, "TB") })
            if (!(v >= 0) || !double.IsFinite(v)) throw new ArgumentOutOfRangeException(n, v, $"{who}: {n} must be a finite number, 0 or above.");
        if (!double.IsFinite(exciterConstant)) throw new ArgumentOutOfRangeException(nameof(exciterConstant), exciterConstant, $"{who}: KE must be finite.");
        if (double.IsNaN(regulatorMinPu) || double.IsNaN(regulatorMaxPu) || !(regulatorMaxPu >= regulatorMinPu))
            throw new ArgumentException($"{who}: the regulator limits must satisfy max >= min (got {regulatorMinPu}, {regulatorMaxPu}).", nameof(regulatorMaxPu));
        if (variant == AcExciterVariant.Exac1)
        {
            if (!double.IsNegativeInfinity(outputMinPu) || !double.IsPositiveInfinity(outputMaxPu))
                throw new ArgumentException("EXAC1 carries its VR limits on the regulator lag; it has no separate output limits.", nameof(outputMinPu));
        }
        else if (double.IsNaN(outputMinPu) || double.IsNaN(outputMaxPu) || !(outputMaxPu >= outputMinPu))
            throw new ArgumentException($"ESAC1A needs VRMAX >= VRMIN (got {outputMinPu}, {outputMaxPu}).", nameof(outputMaxPu));
        if (lagSeconds == 0 && leadSeconds != 0)
            throw new ArgumentException($"{who}: a lead TC of {leadSeconds} s with no lag TB is not a proper lead-lag; TB = TC = 0 bypasses it.", nameof(lagSeconds));

        (_satA, _satB) = SaturationFit(who, satE1, satSe1, satE2, satSe2);
        _variant = variant;
        _ka = regulatorGain; _te = exciterTimeSeconds; _kf = feedbackGain; _tf = feedbackTimeSeconds;
        _kc = rectifierKc; _kd = demagnetisingKd; _ke = exciterConstant;
        _vrMin = outputMinPu; _vrMax = outputMaxPu;
        _leadLag = lagSeconds == 0 ? null : new LeadLag(leadSeconds, lagSeconds);
        _regulator = new FirstOrderLag(regulatorGain, regulatorTimeSeconds, regulatorMinPu, regulatorMaxPu);
        _feedback = new Washout(feedbackTimeSeconds);
    }

    /// <summary>"EXAC1" or "ESAC1A".</summary>
    public string Kind => _variant == AcExciterVariant.Exac1 ? "EXAC1" : "ESAC1A";

    /// <summary>The exciter alternator output voltage VE (pu) after the last step.</summary>
    public double ExciterVoltage => _ve;

    /// <summary>
    /// The quadratic saturation through (E1, SE1) and (E2, SE2): VE*SE(VE) = B (VE - A)^2 for VE above A. From
    /// SE(E) = B (E - A)^2 / E at both points, (E1 - A) / (E2 - A) = sqrt(SE1 E1 / (SE2 E2)) = r, so
    /// A = (E1 - r E2) / (1 - r) and B = SE2 E2 / (E2 - A)^2. Both SE 0: no saturation (A = 0, B = 0). One of them 0, a
    /// point at or below 0, equal points, or a curve that does not rise (r not below 1 with E1 below E2) is refused.
    /// </summary>
    internal static (double A, double B) SaturationFit(string who, double e1, double se1, double e2, double se2)
    {
        if (se1 == 0 && se2 == 0) return (0, 0);
        if (!(se1 > 0) || !(se2 > 0) || !(e1 > 0) || !(e2 > 0) || !double.IsFinite(e1) || !double.IsFinite(e2))
            throw new ArgumentException($"{who}: the saturation points need E1, E2, SE(E1) and SE(E2) all above 0 (or both SE 0 for none); got E1 {e1}, SE1 {se1}, E2 {e2}, SE2 {se2}.");
        if (!(e2 > e1)) throw new ArgumentException($"{who}: the saturation points need E2 above E1 (got E1 {e1}, E2 {e2}).");
        double r = Math.Sqrt(se1 * e1 / (se2 * e2));
        if (!(r < 1)) throw new ArgumentException($"{who}: E1*SE(E1) must be below E2*SE(E2) for a rising saturation curve (got {e1 * se1} and {e2 * se2}).");
        double a = (e1 - r * e2) / (1 - r);
        double d = e2 - a;
        return (a, se2 * e2 / (d * d));
    }

    /// <summary>VE*SE(VE): B (VE - A)^2 above A, else 0.</summary>
    internal double SaturationProduct(double ve) => ve > _satA ? _satB * (ve - _satA) * (ve - _satA) : 0.0;

    /// <summary>The IEEE 421.5 rectifier regulation function FEX(IN).</summary>
    public static double Fex(double iN)
    {
        if (iN <= 0) return 1.0;
        if (iN <= 0.433) return 1.0 - 0.577 * iN;
        if (iN <= 0.75) return Math.Sqrt(0.75 - iN * iN);
        if (iN <= 1.0) return 1.732 * (1.0 - iN);
        return 0.0;
    }

    /// <summary>The field voltage VE*FEX(KC*IFD/VE) (0 when VE is not above 0).</summary>
    private double FieldVoltage(double ve, double ifd) => ve > 0 ? ve * Fex(_kc * ifd / ve) : 0.0;

    /// <summary>The exciter field demand VFE = VE (KE + SE(VE)) + KD IFD.</summary>
    private double Vfe(double ve, double ifd) => ve * _ke + SaturationProduct(ve) + _kd * ifd;

    /// <summary>
    /// The exciter voltage VE that gives field voltage <paramref name="efd"/> at field current <paramref name="ifd"/>:
    /// the inverse of Efd = VE FEX(KC IFD / VE) branch by branch (VE = Efd + 0.577 KC IFD on the first,
    /// sqrt((Efd^2 + (KC IFD)^2) / 0.75) on the second, Efd / 1.732 + KC IFD on the third), taking the first branch
    /// whose IN it reproduces. Fails loud when none does (a field the rectifier cannot deliver).
    /// </summary>
    internal double SolveVe(double efd, double ifd)
    {
        if (!(efd > 0)) throw new ArgumentOutOfRangeException(nameof(efd), efd, $"{Kind}: a diode rectifier gives a positive field voltage; the initial field Efd = {efd} pu cannot be held.");
        double k = _kc * ifd;
        if (k <= 0) return efd;
        double v1 = efd + 0.577 * k;
        if (k / v1 <= 0.433) return v1;
        double v2 = Math.Sqrt((efd * efd + k * k) / 0.75);
        double in2 = k / v2;
        if (in2 > 0.433 && in2 <= 0.75) return v2;
        double v3 = efd / 1.732 + k;
        double in3 = k / v3;
        if (in3 > 0.75 && in3 <= 1.0) return v3;
        throw new InvalidOperationException($"{Kind}: no exciter voltage gives Efd = {efd} pu at field current {ifd} pu with KC = {_kc} (the rectifier regulation curve is not invertible there).");
    }

    /// <summary>The regulator output VR the exciter needs at the equilibrium with field <paramref name="efd"/> (field
    /// current equal to the field voltage, as at a machine's steady state).</summary>
    public double EquilibriumRegulatorOutput(double efd) => Vfe(SolveVe(efd, efd), efd);

    /// <summary>The steady regulator error Vref - Vt that holds field <paramref name="efd"/>: VR / KA (rate feedback
    /// washed out, lead-lag at unity DC gain).</summary>
    public double EquilibriumErrorPu(double efd) => EquilibriumRegulatorOutput(efd) / _ka;

    /// <summary>Pre-load every state at the equilibrium with field voltage <paramref name="efd"/> and field current
    /// equal to it. Fails loud when VR (and for ESAC1A VA) falls outside its limits, or VE outside its floor.</summary>
    public void Initialize(double efd)
    {
        double ve = SolveVe(efd, efd);
        double vfe = Vfe(ve, efd);
        ControlBlocks.RequireEquilibriumWithinLimits(Kind, "regulator output", vfe, _regulator.Min, _regulator.Max, nameof(efd));
        if (_variant == AcExciterVariant.Esac1a)
            ControlBlocks.RequireEquilibriumWithinLimits(Kind, "VR", vfe, _vrMin, _vrMax, nameof(efd));
        _ve = ve;
        double x = vfe / _ka;
        _leadLag?.Initialize(x);
        _regulator.Initialize(vfe);
        _feedback.Initialize(vfe);
    }

    /// <summary>Advance one step of <paramref name="dtSeconds"/> with regulator error <paramref name="voltageErrorPu"/>
    /// (Vref - Vt + Vs) and field current <paramref name="fieldCurrentPu"/>, both held across the step; return Efd.</summary>
    public double Step(double voltageErrorPu, double fieldCurrentPu, double dtSeconds)
    {
        double vfe0 = Vfe(_ve, fieldCurrentPu);
        double vf = (_kf / _tf) * _feedback.OutputFor(vfe0);
        double u = voltageErrorPu - vf;
        double x = _leadLag is null ? u : _leadLag.Step(u, dtSeconds);
        double vr = _regulator.Step(x, dtSeconds);
        if (_variant == AcExciterVariant.Esac1a) vr = Math.Clamp(vr, _vrMin, _vrMax);

        // Exciter: TE dVE/dt = VR - KD IFD - VE (KE + SE(VE)), SE frozen at the start of the step -> exact ZOH.
        double se = _ve > 0 ? SaturationProduct(_ve) / _ve : 0.0;
        double pole = _ke + se;
        double drive = vr - _kd * fieldCurrentPu;
        if (Math.Abs(pole) <= Ieeet1.ExciterPoleAtOriginTolerance) _ve += drive / _te * dtSeconds;
        else
        {
            double a = DetMath.Exp(-dtSeconds * pole / _te);
            _ve = a * _ve + (1.0 - a) * drive / pole;
        }
        if (_variant == AcExciterVariant.Esac1a && _ve < 0) _ve = 0.0;

        _feedback.Step(Vfe(_ve, fieldCurrentPu), dtSeconds);
        return FieldVoltage(_ve, fieldCurrentPu);
    }
}
