namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// ESST4B - the IEEE 421.5-2005 type ST4B static excitation system (PSS/E ESST4B): a potential- and/or compound-source
/// controlled rectifier exciter with an outer voltage PI regulator and an inner field-voltage PI regulator. Structure
/// (all per-unit on the machine base; VT and IT the terminal voltage and current phasors, IFD the field current in the
/// non-reciprocal Xad base):
/// <code>
///   VE   = | KP e^(j THETAP) VT + j (KI + KP e^(j THETAP) XL) IT |      (the power source voltage)
///   IN   = KC IFD / VE,   VB = min(VE FEX(IN), VBMAX)                    (FEX the IEEE 421.5 rectifier regulation)
///   VR'  = PI_{KPR, KIR, [VRMIN, VRMAX]}(Verr)                          (Verr = Vref - Vt + Vs)
///   VR   = lag_{TA}(VR')
///   VM   = PI_{KPM, KIM, [VMMIN, VMMAX]}(VR - KG Efd)        (solved with the step's own Efd = VM VB)
///   Efd  = VM VB
/// </code>
/// The outer PI regulator is a <see cref="PiController"/> (non-windup limits on the integral and on the output) and the
/// lag a <see cref="FirstOrderLag"/>, with the inputs held across the step. The inner loop's algebraic loop through
/// KG Efd = KG VB VM is solved exactly each step (linear in VM, then clamped, the integral held inside the same limits):
/// closing it through the start-of-step field instead is a one-step-delay loop of gain KPM KG VB, which diverges once
/// that passes 1, as ordinary data do. The PSS/E data have no VGMAX
/// (the 2005 limit on KG Efd), so none is applied; the under- and over-excitation limiter gates are not modelled.
///
/// <para>Initialisation needs the terminal quantities, so <see cref="Initialize"/> only records the field and the
/// first <see cref="Step"/> sets every state from that step's measurements (the undisturbed network at t = 0, as the
/// kernel guarantees): VB0 from VT, IT and IFD = Efd0, then VM0 = Efd0 / VB0, and with KIM above 0 the inner error 0 so
/// VR0 = KG Efd0 (KIM = 0: VR0 = KG Efd0 + VM0 / KPM). The outer integrator then holds VR0 with zero voltage error when
/// KIR is above 0; KIR = 0 needs KIM above 0 and holds the error KG Efd0 / KPR. KIR = KIM = 0 would need the
/// measurements to find the steady error before the first step, so it is refused. Any state outside its limits, or a VB0
/// that is not above 0, fails loud.</para>
/// </summary>
public sealed class Esst4b
{
    private readonly double _kpr, _kir, _kpm, _kim, _kg, _kc, _vbMax;
    private readonly double _kpRe, _kpIm, _ki, _xl;
    private readonly double _vrMin, _vrMax, _vmMin, _vmMax;
    private readonly PiController _outer;
    private readonly FirstOrderLag _lag;
    private double _xi;   // the inner regulator's integral state, held in [VMMIN, VMMAX]
    private double _efd;
    private bool _pending;

    /// <param name="kpr">Outer regulator proportional gain KPR.</param>
    /// <param name="kir">Outer regulator integral gain KIR (1/s), 0 or above.</param>
    /// <param name="vrMin">Outer regulator lower limit VRMIN.</param>
    /// <param name="vrMax">Outer regulator upper limit VRMAX.</param>
    /// <param name="ta">Regulator lag TA (s), 0 or above.</param>
    /// <param name="kpm">Inner regulator proportional gain KPM.</param>
    /// <param name="kim">Inner regulator integral gain KIM (1/s), 0 or above.</param>
    /// <param name="vmMin">Inner regulator lower limit VMMIN.</param>
    /// <param name="vmMax">Inner regulator upper limit VMMAX.</param>
    /// <param name="kg">Field voltage feedback gain KG, 0 or above.</param>
    /// <param name="kp">Potential circuit gain KP, 0 or above.</param>
    /// <param name="ki">Compound circuit gain KI, 0 or above.</param>
    /// <param name="vbMax">Power source voltage ceiling VBMAX, above 0.</param>
    /// <param name="kc">Rectifier loading factor KC, 0 or above.</param>
    /// <param name="xl">Potential source reactance XL (pu, machine base), 0 or above.</param>
    /// <param name="thetapDeg">Potential circuit phase angle THETAP (degrees).</param>
    public Esst4b(double kpr, double kir, double vrMin, double vrMax, double ta, double kpm, double kim, double vmMin, double vmMax,
        double kg, double kp, double ki, double vbMax, double kc, double xl, double thetapDeg)
    {
        foreach ((double v, string n) in new[] { (kir, "KIR"), (kim, "KIM"), (kg, "KG"), (kp, "KP"), (ki, "KI"), (kc, "KC"), (xl, "XL"), (ta, "TA") })
            if (!(v >= 0) || !double.IsFinite(v)) throw new ArgumentOutOfRangeException(n, v, $"ESST4B: {n} must be a finite number, 0 or above.");
        foreach ((double v, string n) in new[] { (kpr, "KPR"), (kpm, "KPM"), (thetapDeg, "THETAP") })
            if (!double.IsFinite(v)) throw new ArgumentOutOfRangeException(n, v, $"ESST4B: {n} must be finite.");
        if (!(vbMax > 0) || double.IsNaN(vbMax)) throw new ArgumentOutOfRangeException(nameof(vbMax), vbMax, "ESST4B: VBMAX must be above 0.");
        if (double.IsNaN(vrMin) || double.IsNaN(vrMax) || !(vrMax >= vrMin)) throw new ArgumentException($"ESST4B needs VRMAX >= VRMIN (got {vrMin}, {vrMax}).", nameof(vrMax));
        if (double.IsNaN(vmMin) || double.IsNaN(vmMax) || !(vmMax >= vmMin)) throw new ArgumentException($"ESST4B needs VMMAX >= VMMIN (got {vmMin}, {vmMax}).", nameof(vmMax));
        if (kp == 0 && ki == 0) throw new ArgumentException("ESST4B: KP and KI are both 0, so the power source gives no voltage.", nameof(kp));
        if (kir == 0 && kim == 0) throw new ArgumentException("ESST4B with KIR = KIM = 0 has a steady voltage error that depends on the terminal quantities; GridSim needs an integral gain in at least one regulator.", nameof(kir));
        if (kir == 0 && kpr == 0) throw new ArgumentException("ESST4B: KPR and KIR are both 0, so the outer regulator does nothing.", nameof(kpr));
        _kpr = kpr; _kir = kir; _kpm = kpm; _kim = kim; _kg = kg; _kc = kc; _vbMax = vbMax; _ki = ki; _xl = xl;
        double th = thetapDeg * Math.PI / 180.0;
        (double s, double c) = DetMath.SinCos(th);
        _kpRe = kp * c; _kpIm = kp * s;
        _vrMin = vrMin; _vrMax = vrMax; _vmMin = vmMin; _vmMax = vmMax;
        _outer = new PiController(kpr, kir, vrMin, vrMax);
        _lag = new FirstOrderLag(1.0, ta);
    }

    /// <summary>Field voltage Efd (pu) after the last step.</summary>
    public double FieldVoltage => _efd;

    /// <summary>The power source voltage VB the last step used (pu), for checks.</summary>
    public double LastVb { get; private set; }

    /// <summary>VE = |KP e^(j THETAP) VT + j (KI + KP e^(j THETAP) XL) IT| (machine base).</summary>
    public double SourceVoltage(double vtRe, double vtIm, double itRe, double itIm)
    {
        // a = KPc VT;  b = j (KI + KPc XL) IT, with KPc XL = (kpRe XL) + j (kpIm XL).
        double ar = _kpRe * vtRe - _kpIm * vtIm, ai = _kpRe * vtIm + _kpIm * vtRe;
        double gr = _ki + _kpRe * _xl, gi = _kpIm * _xl;          // KI + KPc XL
        double jr = -gi, ji = gr;                                 // j (KI + KPc XL)
        double br = jr * itRe - ji * itIm, bi = jr * itIm + ji * itRe;
        double re = ar + br, im = ai + bi;
        return Math.Sqrt(re * re + im * im);
    }

    private double Vb(double vtRe, double vtIm, double itRe, double itIm, double ifd)
    {
        double ve = SourceVoltage(vtRe, vtIm, itRe, itIm);
        double vb = ve > 0 ? ve * AcExciter.Fex(_kc * ifd / ve) : 0.0;
        return Math.Min(vb, _vbMax);
    }

    /// <summary>The steady voltage error Vref - Vt that holds field <paramref name="efd"/>: 0 with KIR above 0 (integral
    /// action), KG Efd / KPR with KIR = 0 (then KIM is above 0, so the inner error is 0).</summary>
    public double EquilibriumErrorPu(double efd) => _kir > 0 ? 0.0 : _kg * efd / _kpr;

    /// <summary>Record the initial field; the states are set on the first <see cref="Step"/> from its measurements.</summary>
    public void Initialize(double efd)
    {
        if (!(efd > 0) || !double.IsFinite(efd)) throw new ArgumentOutOfRangeException(nameof(efd), efd, $"ESST4B: a controlled rectifier exciter gives a positive field voltage; the initial field Efd = {efd} pu cannot be held.");
        _efd = efd;
        _pending = true;
    }

    private void Settle(double vtRe, double vtIm, double itRe, double itIm)
    {
        double vb = Vb(vtRe, vtIm, itRe, itIm, _efd);
        if (!(vb > 0)) throw new InvalidOperationException($"ESST4B: the power source voltage VB is {vb} pu at the initial operating point, so no field can be held.");
        double vm = _efd / vb;
        ControlBlocks.RequireEquilibriumWithinLimits("ESST4B", "inner regulator output VM = Efd / VB", vm, _vmMin, _vmMax, "efd");
        double u2 = _kim > 0 ? 0.0 : vm / _kpm;
        double vr = _kg * _efd + u2;
        ControlBlocks.RequireEquilibriumWithinLimits("ESST4B", "outer regulator output VR", vr, _vrMin, _vrMax, "efd");
        _xi = vm - _kpm * u2;
        _lag.Initialize(vr);
        _outer.Initialize(EquilibriumErrorPu(_efd), vr);
        _pending = false;
    }

    /// <summary>Advance one step with voltage error <paramref name="voltageErrorPu"/>, terminal voltage and current phasors
    /// (current on the machine base) and field current <paramref name="fieldCurrentPu"/>, all held across the step;
    /// return Efd. The first step after <see cref="Initialize"/> sets the states from these measurements first.</summary>
    public double Step(double voltageErrorPu, double vtRe, double vtIm, double itRe, double itIm, double fieldCurrentPu, double dtSeconds)
    {
        if (_pending) Settle(vtRe, vtIm, itRe, itIm);
        double vb = Vb(vtRe, vtIm, itRe, itIm, fieldCurrentPu);
        LastVb = vb;
        double vrp = _outer.Step(voltageErrorPu, dtSeconds);
        double vr = _lag.Step(vrp, dtSeconds);
        // Inner loop, exact with the step's VB: VM = KPM u + xi', xi' = xi + KIM u dt, u = VR - KG VB VM, so
        // VM (1 + KG VB (KPM + KIM dt)) = (KPM + KIM dt) VR + xi; on a limit the output is the limit and the integral
        // advances on the error at that output, never past the limits.
        double g = _kpm + _kim * dtSeconds;
        double vm = Math.Clamp((g * vr + _xi) / (1.0 + _kg * vb * g), _vmMin, _vmMax);
        double u = vr - _kg * vb * vm;
        _xi = Math.Clamp(_xi + _kim * u * dtSeconds, _vmMin, _vmMax);
        _efd = vm * vb;
        return _efd;
    }
}
