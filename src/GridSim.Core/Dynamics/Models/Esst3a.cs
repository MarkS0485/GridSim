using System.Numerics;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// WP19 (RC5): a library exciter that reads the terminal voltage and current phasors (a compound source), not only |Vt|.
/// <see cref="RmsExciter"/> passes them on the step that supplies the terminal quantities, the current on the machine base.
/// </summary>
internal interface IRmsPhasorExciterModel : IRmsExciterModel
{
    /// <summary>One step as <see cref="IRmsExciterModel.Step"/>, with the terminal voltage <paramref name="terminalVoltagePu"/>
    /// and the machine current <paramref name="terminalCurrentMachinePu"/> (machine base), both held across the step.</summary>
    double Step(double voltageErrorPu, double stabiliserPu, FieldControlInputs inputs, Complex terminalVoltagePu,
        Complex terminalCurrentMachinePu, double dtSeconds);
}

/// <summary>
/// ESST3A, the IEEE 421.5-2005 type ST3A compound-source static excitation system (PSS/E ESST3A, data TR VIMAX VIMIN KM TC
/// TB KA TA VRMAX VRMIN KG KP KI VBMAX KC XL VGMAX THETAP TM VMMAX VMMIN). Structure (pu on the machine base; VT and IT the
/// terminal voltage and current phasors, IFD the field current in the non-reciprocal Xad base):
/// <code>
///   Vc   = lag_{TR}(Vt)                                             (TR = 0: Vc = Vt)
///   x    = leadlag_{TC/TB}(clamp(Vref - Vc + Vs, VIMIN, VIMAX))    (TB = TC = 0 bypasses it)
///   VR   = lag_{KA, TA, [VRMIN, VRMAX]}(x)                         (non-windup)
///   VG   = min(KG Efd, VGMAX)
///   VM   = lag_{KM, TM, [VMMIN, VMMAX]}(VR - VG)                   (the inner field-voltage loop, non-windup)
///   VE   = | KP e^(j THETAP) VT + j (KI + KP e^(j THETAP) XL) IT |  (the compound power source)
///   VB   = min(VE FEX(KC IFD / VE), VBMAX)
///   Efd  = VB VM
/// </code>
/// FEX is <see cref="AcExciter.Fex"/>. The inner loop closes through Efd = VB VM. With VB held across the step and VG below
/// VGMAX at the start of the step, VM obeys TM dVM/dt = KM VR - (1 + KM KG VB) VM, a linear lag whose exact zero-order
/// hold is used (pole (1 + KM KG VB) / TM, target KM VR / (1 + KM KG VB)), so the loop is stable at any step. With VG at
/// VGMAX at the start of the step, VG is held there and VM is the plain lag. TM must be above 0.
/// <para>Initialisation needs the terminal quantities, so <see cref="Initialize"/> records the field and the first step
/// sets every state from that step's measurements (the undisturbed network at t = 0): VB0 from VT, IT and IFD = Efd0, then
/// VM0 = Efd0 / VB0, VG0 = min(KG Efd0, VGMAX), VR0 = VM0 / KM + VG0 and the regulator input VR0 / KA. Because VR0 depends
/// on VB0, <see cref="EquilibriumErrorPu"/> is 0 and the model holds the input it needs as a constant offset added to the
/// error, fixed on the first step (it stands where PSS/E's VREF would carry VR0 / KA). A reference step still moves the
/// error one for one. Any state outside its limits, a VB0 that is not above 0, or an input outside VIMIN..VIMAX fails loud.</para>
/// <para>Not modelled: the UEL input and the OEL/SCL limiters.</para>
/// </summary>
public sealed class Esst3a : IRmsPhasorExciterModel
{
    private const string Who = "ESST3A";
    private readonly VoltageTransducer _tr;
    private readonly double _viMin, _viMax, _km, _ka, _kg, _kc, _vbMax, _vgMax, _tm, _vmMin, _vmMax;
    private readonly double _kpRe, _kpIm, _ki, _xl;
    private readonly LeadLag? _shaping;
    private readonly FirstOrderLag _regulator;
    private double _vm, _efd, _bias;
    private bool _pending;

    /// <summary>The ESST3A data in PSS/E order.</summary>
    public Esst3a(double tr, double viMax, double viMin, double km, double tc, double tb, double ka, double ta, double vrMax, double vrMin,
        double kg, double kp, double ki, double vbMax, double kc, double xl, double vgMax, double thetapDeg, double tm, double vmMax, double vmMin)
    {
        LibraryChecks.Positive(Who, "KM", km);
        LibraryChecks.Positive(Who, "KA", ka);
        LibraryChecks.NonNegative(Who, "TA", ta);
        LibraryChecks.Positive(Who, "TM", tm);
        LibraryChecks.NonNegative(Who, "KG", kg);
        LibraryChecks.NonNegative(Who, "KC", kc);
        LibraryChecks.Positive(Who, "VBMAX", vbMax);
        foreach ((double v, string n) in new[] { (kp, "KP"), (ki, "KI"), (xl, "XL"), (thetapDeg, "THETAP") })
            if (!double.IsFinite(v)) throw new ArgumentOutOfRangeException(n, v, $"{Who}: {n} must be finite.");
        if (double.IsNaN(vgMax)) throw new ArgumentOutOfRangeException(nameof(vgMax), vgMax, $"{Who}: VGMAX must be a number.");
        LibraryChecks.Limits(Who, "VIMIN", viMin, "VIMAX", viMax);
        LibraryChecks.Limits(Who, "VRMIN", vrMin, "VRMAX", vrMax);
        LibraryChecks.Limits(Who, "VMMIN", vmMin, "VMMAX", vmMax);
        _tr = new VoltageTransducer(tr, Who);
        _viMin = viMin; _viMax = viMax; _km = km; _ka = ka; _kg = kg; _kc = kc; _vbMax = vbMax; _vgMax = vgMax; _tm = tm;
        _vmMin = vmMin; _vmMax = vmMax; _ki = ki; _xl = xl;
        (double s, double c) = DetMath.SinCos(thetapDeg * Math.PI / 180.0);
        _kpRe = kp * c; _kpIm = kp * s;
        _shaping = LibraryChecks.Stage(Who, tc, "TC", tb, "TB");
        _regulator = new FirstOrderLag(ka, ta, vrMin, vrMax);
    }

    /// <inheritdoc/>
    public string Kind => Who;

    /// <summary>Always true: the power source reads the terminal voltage and current.</summary>
    public bool NeedsTerminalQuantities => true;

    /// <summary>The inner loop output VM after the last step.</summary>
    public double InnerOutput => _vm;

    /// <summary>The power source voltage VB of the last step.</summary>
    public double LastVb { get; private set; }

    /// <summary>The compound source voltage VE = |KP e^(j THETAP) VT + j (KI + KP e^(j THETAP) XL) IT| (machine base).</summary>
    public double SourceVoltage(Complex vt, Complex it)
    {
        Complex kpc = new(_kpRe, _kpIm);
        Complex v = kpc * vt + Complex.ImaginaryOne * (_ki + kpc * _xl) * it;
        return Math.Sqrt(v.Real * v.Real + v.Imaginary * v.Imaginary);
    }

    private double Vb(Complex vt, Complex it, double ifd)
    {
        double ve = SourceVoltage(vt, it);
        double vb = ve > 0 ? ve * AcExciter.Fex(_kc * ifd / ve) : 0.0;
        return Math.Min(vb, _vbMax);
    }

    /// <summary>0: the model carries its steady input itself (see the class notes).</summary>
    public double EquilibriumErrorPu(double fieldVoltagePu) => 0.0;

    /// <inheritdoc/>
    public void Initialize(double fieldVoltagePu)
    {
        if (!double.IsFinite(fieldVoltagePu)) throw new ArgumentOutOfRangeException(nameof(fieldVoltagePu), fieldVoltagePu, $"{Who}: the initial field is not finite.");
        _efd = fieldVoltagePu;
        _tr.Initialize();
        _pending = true;
    }

    /// <summary>Refused: ESST3A needs the terminal phasors; the RMS kernel calls the phasor step.</summary>
    public double Step(double voltageErrorPu, double stabiliserPu, FieldControlInputs inputs, double dtSeconds)
        => throw new InvalidOperationException($"{Who}: its power source reads the terminal voltage and current phasors; the caller must use the step that supplies them.");

    /// <inheritdoc/>
    public double Step(double voltageErrorPu, double stabiliserPu, FieldControlInputs inputs, Complex terminalVoltagePu,
        Complex terminalCurrentMachinePu, double dtSeconds)
    {
        if (!double.IsFinite(inputs.FieldCurrentPu) || !double.IsFinite(inputs.TerminalVoltagePu))
            throw new InvalidOperationException($"{Who}: it reads the field current and the terminal quantities; the caller must use the step that supplies them.");
        double vb = Vb(terminalVoltagePu, terminalCurrentMachinePu, inputs.FieldCurrentPu);
        if (_pending)
        {
            if (!(vb > 0)) throw new InvalidOperationException($"{Who}: the power source gives VB = {ControlBlocks.Num(vb)} pu at the operating point, so no field can be held.");
            double vm0 = _efd / vb;
            ControlBlocks.RequireEquilibriumWithinLimits(Who, "inner loop output VM = Efd / VB", vm0, _vmMin, _vmMax, nameof(inputs));
            double vg0 = Math.Min(_kg * _efd, _vgMax);
            double vr0 = vm0 / _km + vg0;
            ControlBlocks.RequireEquilibriumWithinLimits(Who, "regulator output VR = VM / KM + VG", vr0, _regulator.Min, _regulator.Max, nameof(inputs));
            double x0 = vr0 / _ka;
            ControlBlocks.RequireEquilibriumWithinLimits(Who, "regulator input VR / KA", x0, _viMin, _viMax, nameof(inputs));
            double u0 = voltageErrorPu + stabiliserPu;
            _bias = x0 - u0;
            _shaping?.Initialize(x0);
            _regulator.Initialize(vr0);
            _vm = vm0;
            _pending = false;
        }
        double e = _tr.Error(voltageErrorPu, inputs.TerminalVoltagePu, dtSeconds);
        double u = Math.Clamp(e + stabiliserPu + _bias, _viMin, _viMax);
        double x = _shaping is null ? u : _shaping.Step(u, dtSeconds);
        double vr = _regulator.Step(x, dtSeconds);
        double vgStart = _kg * _efd;
        double pole, target;
        if (vgStart < _vgMax)
        {
            double g = 1.0 + _km * _kg * vb;
            pole = g / _tm;
            target = _km * vr / g;
        }
        else
        {
            pole = 1.0 / _tm;
            target = _km * (vr - _vgMax);
        }
        double a = DetMath.Exp(-dtSeconds * pole);
        _vm = Math.Clamp(a * _vm + (1.0 - a) * target, _vmMin, _vmMax);
        LastVb = vb;
        _efd = vb * _vm;
        return _efd;
    }
}
