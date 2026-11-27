using System.Numerics;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// WP3b (RC5): the load torque of an induction motor load, normalised so that it equals the initial electrical torque at
/// the initial speed w0. <see cref="PowerLaw"/> is PSS/E CIM5's T = T0 (1 + dw)^D (and the CMLD motor's T0 w^etrq);
/// <see cref="Polynomial"/> is PSS/E CIMW's T = T0 (A w^2 + B w + C0 + D w^E) with C0 = 1 - A w0^2 - B w0 - D w0^E.
/// </summary>
public sealed record InductionMotorTorque
{
    private InductionMotorTorque(bool polynomial, double a, double b, double d, double e)
    {
        IsPolynomial = polynomial; A = a; B = b; D = d; E = e;
    }

    /// <summary>True for the CIMW polynomial, false for the power law.</summary>
    public bool IsPolynomial { get; }

    /// <summary>The CIMW coefficients A, B, D and exponent E (the power law keeps its exponent in <see cref="D"/>).</summary>
    public double A { get; }

    /// <inheritdoc cref="A"/>
    public double B { get; }

    /// <summary>The CIMW coefficient D, or the power law's exponent.</summary>
    public double D { get; }

    /// <inheritdoc cref="A"/>
    public double E { get; }

    /// <summary>T = T0 w^exponent (CIM5's D; 0 is a constant torque).</summary>
    public static InductionMotorTorque PowerLaw(double exponent)
    {
        LoadChecks.Finite("motor load torque", "exponent", exponent);
        return new InductionMotorTorque(false, 0.0, 0.0, exponent, 0.0);
    }

    /// <summary>T = T0 (A w^2 + B w + C0 + D w^E), C0 taking the rest at the initial speed (CIMW).</summary>
    public static InductionMotorTorque Polynomial(double a, double b, double d, double e)
    {
        LoadChecks.Finite("motor load torque", "A", a);
        LoadChecks.Finite("motor load torque", "B", b);
        LoadChecks.Finite("motor load torque", "D", d);
        LoadChecks.Finite("motor load torque", "E", e);
        return new InductionMotorTorque(true, a, b, d, e);
    }

    /// <summary>The torque at speed <paramref name="w"/> per unit of the torque at <paramref name="w0"/>.</summary>
    internal double Shape(double w, double w0)
    {
        if (!IsPolynomial) return D == 0.0 ? 1.0 : DetMath.Pow(w / w0, D);
        double c0 = 1.0 - A * w0 * w0 - B * w0 - D * (E == 0.0 ? 1.0 : DetMath.Pow(w0, E));
        return A * w * w + B * w + c0 + D * (E == 0.0 ? 1.0 : DetMath.Pow(w, E));
    }
}

/// <summary>
/// WP3b (RC5): a third-order (single-cage, transient) induction motor on its own MVA base: stator resistance Ra, the
/// open-circuit reactance X = Xa + Xm, the transient reactance X' = Xa + Xm X1 / (Xm + X1), the transient open-circuit time
/// constant T'o = (X1 + Xm) / (ws R1), the inertia H and the load torque. In the synchronous frame, with the motor current
/// I = (V - E') / (Ra + jX') drawn from the bus:
/// <code>
///   dE'/dt = -(E' - j (X - X') I) / T'o - j s ws E',   s = 1 - w
///   2H dw/dt = Te - TL(w),   Te = Re(E' conj(I))
/// </code>
/// the model of PSS/E CIM5 and CIMW with a single cage (R2 = X2 = 0), and of the WECC CMLD motor with X'' = X'. In steady
/// state it is exactly the equivalent circuit Ra + jXa in series with jXm in parallel with R1/s + jX1.
/// </summary>
/// <param name="Ra">Stator resistance (pu, motor base).</param>
/// <param name="X">Open-circuit (synchronous) reactance Xa + Xm (pu).</param>
/// <param name="Xp">Transient reactance X' (pu), below X.</param>
/// <param name="TpoSeconds">Transient open-circuit time constant T'o (s).</param>
/// <param name="H">Inertia constant (s, motor base).</param>
/// <param name="Torque">The load torque.</param>
public sealed record InductionMotorParameters(double Ra, double X, double Xp, double TpoSeconds, double H, InductionMotorTorque Torque)
{
    /// <summary>The motor from its equivalent circuit (PSS/E CIM5 and CIMW, single cage) at nominal frequency
    /// <paramref name="nominalHz"/>.</summary>
    public static InductionMotorParameters FromCircuit(double ra, double xa, double xm, double r1, double x1, double h,
        InductionMotorTorque torque, double nominalHz)
    {
        const string who = "induction motor";
        LibraryChecks.NonNegative(who, "RA", ra);
        LibraryChecks.NonNegative(who, "XA", xa);
        LibraryChecks.Positive(who, "XM", xm);
        LibraryChecks.Positive(who, "R1", r1);
        LibraryChecks.Positive(who, "X1", x1);
        LibraryChecks.Positive(who, "nominal frequency", nominalHz);
        double ws = 2.0 * Math.PI * nominalHz;
        return new InductionMotorParameters(ra, xa + xm, xa + xm * x1 / (xm + x1), (x1 + xm) / (ws * r1), h, torque);
    }

    internal void Check()
    {
        const string who = "induction motor";
        LibraryChecks.NonNegative(who, "Ra", Ra);
        LibraryChecks.Positive(who, "X'", Xp);
        LibraryChecks.Positive(who, "T'o", TpoSeconds);
        LibraryChecks.Positive(who, "H", H);
        if (!(X > Xp) || !double.IsFinite(X)) throw new ArgumentOutOfRangeException(nameof(X), X, $"{who}: X must be above X' ({ControlBlocks.Num(Xp)}).");
        ArgumentNullException.ThrowIfNull(Torque);
    }

    /// <summary>The steady-state impedance Ra + jX' + j(X - X')/(1 + j s ws T'o) at slip <paramref name="slip"/> (pu).</summary>
    public Complex Impedance(double slip, double nominalHz)
    {
        double b = slip * 2.0 * Math.PI * nominalHz * TpoSeconds;
        return new Complex(Ra, Xp) + new Complex(0.0, X - Xp) / new Complex(1.0, b);
    }

    /// <summary>The steady-state electrical torque (pu) at slip <paramref name="slip"/> and terminal voltage
    /// <paramref name="v"/>: the power across the air gap, |V|^2 (Re(1/Z) - Ra |1/Z|^2).</summary>
    public double ElectricalTorque(double slip, double v, double nominalHz)
    {
        Complex y = Complex.One / Impedance(slip, nominalHz);
        double y2 = y.Real * y.Real + y.Imaginary * y.Imaginary;
        return v * v * (y.Real - Ra * y2);
    }

    /// <summary>The steady-state input power |V|^2 Re(1/Z) (pu) at slip <paramref name="slip"/>.</summary>
    public double InputPower(double slip, double v, double nominalHz)
    {
        Complex y = Complex.One / Impedance(slip, nominalHz);
        return v * v * y.Real;
    }

    /// <summary>The slip of the largest electrical torque on (0, 1] (golden-section search, a fixed 200 steps, so every run
    /// takes the same arithmetic). The torque rises to it and falls beyond; slips below it are the stable branch.</summary>
    public double BreakdownSlip(double nominalHz)
    {
        double lo = 0.0, hi = 1.0;
        double g = (Math.Sqrt(5.0) - 1.0) / 2.0;
        double x1 = hi - g * (hi - lo), x2 = lo + g * (hi - lo);
        double f1 = ElectricalTorque(x1, 1.0, nominalHz), f2 = ElectricalTorque(x2, 1.0, nominalHz);
        for (int k = 0; k < 200; k++)
        {
            if (f1 < f2) { lo = x1; x1 = x2; f1 = f2; x2 = lo + g * (hi - lo); f2 = ElectricalTorque(x2, 1.0, nominalHz); }
            else { hi = x2; x2 = x1; f2 = f1; x1 = hi - g * (hi - lo); f1 = ElectricalTorque(x1, 1.0, nominalHz); }
        }
        return 0.5 * (lo + hi);
    }

    /// <summary>The slip on the stable branch at which the motor draws <paramref name="power"/> (pu, motor base) at voltage
    /// <paramref name="v"/>, by bisection on (0, breakdown slip] (a fixed 200 halvings); NaN when even the breakdown slip
    /// draws less (the motor cannot carry the load and stalls).</summary>
    public double SlipFor(double power, double v, double nominalHz)
    {
        double hi = BreakdownSlip(nominalHz);
        if (!(InputPower(hi, v, nominalHz) >= power)) return double.NaN;
        double lo = 0.0;
        for (int k = 0; k < 200; k++)
        {
            double mid = 0.5 * (lo + hi);
            if (mid <= lo || mid >= hi) break;
            if (InputPower(mid, v, nominalHz) < power) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }
}

/// <summary>
/// The running state of one third-order motor (<see cref="InductionMotorParameters"/>), advanced by classical RK4 over a
/// step with the terminal voltage held (in equal sub-steps when the step is beyond RK4's stability bound for the rotor EMF,
/// <see cref="SubSteps"/>). The speed is held at 0 once the motor stalls (the load does not drive it backwards).
/// </summary>
internal sealed class InductionMotorState
{
    private readonly InductionMotorParameters _m;
    private readonly double _ws, _w0, _t0;
    private readonly Complex _z;
    private Complex _e;
    private double _w;

    internal InductionMotorState(InductionMotorParameters m, double nominalHz, Complex e0, double w0, double t0)
    {
        _m = m; _ws = 2.0 * Math.PI * nominalHz; _z = new Complex(m.Ra, m.Xp); _e = e0; _w = w0; _w0 = w0; _t0 = t0;
    }

    internal Complex Emf => _e;

    internal double Speed => _w;

    /// <summary>The motor's Norton admittance 1/(Ra + jX') on its own base.</summary>
    internal Complex Admittance => Complex.One / _z;

    /// <summary>The current drawn at <paramref name="v"/> (motor base current, the voltage in pu).</summary>
    internal Complex Current(Complex v) => (v - _e) / _z;

    private (Complex DE, double DW) Rates(Complex e, double w, Complex v)
    {
        Complex i = (v - e) / _z;
        Complex de = -(e - new Complex(0.0, _m.X - _m.Xp) * i) / _m.TpoSeconds - new Complex(0.0, (1.0 - w) * _ws) * e;
        double te = e.Real * i.Real + e.Imaginary * i.Imaginary;
        double tl = _t0 * _m.Torque.Shape(Math.Max(w, 0.0), _w0);
        return (de, (te - tl) / (2.0 * _m.H));
    }

    /// <summary>DYN-03: the bound on |lambda dt| the step keeps. Classical RK4 is stable for |lambda dt| up to about 2.62 in
    /// every direction of the left half plane (2.83 on the imaginary axis, 2.79 on the real axis); 2.5 keeps a margin.</summary>
    internal const double Rk4StabilityBound = 2.5;

    /// <summary>DYN-03: the most RK4 sub-steps one step may take (a step that needs more is refused).</summary>
    internal const int MaxSubSteps = 100_000;

    /// <summary>DYN-03: the number of equal RK4 sub-steps that keep the rotor EMF equation inside the stability bound at
    /// the state at the start of the step. The equation dE'/dt = -(E' - j(X - X') I) / T'o - j (1 - w) ws E' with
    /// I = (V - E') / (Ra + jX') is linear in E' with the coefficient lambda = -(1 + j(X - X') / (Ra + jX')) / T'o
    /// - j (1 - w) ws; |lambda| is bounded by the sum of the two terms' magnitudes, the decay |1 + j(X - X') / (Ra + jX')| / T'o
    /// and the rotation |1 - w| ws. At stall (w = 0) the step bound is 2.5 / (decay + ws): about 7.3 ms at 50 Hz and 6.1 ms
    /// at 60 Hz for a slow motor (X 3, X' 0.2, Ra 0.01, T'o 0.5 s: decay 30 1/s), but 4.7 ms at 60 Hz for a fast one such
    /// as the WECC CMLD motor A (Ls 1.8, Lp 0.12, Ra 0.04, Tpo 0.095 s: decay about 150 1/s), so at the 5 ms default such a
    /// stalled motor is taken in two sub-steps. A running motor (slip a few %) allows tens of ms.
    /// A step inside the bound is one RK4 step exactly as before (bit for bit); a longer one is split into n equal
    /// sub-steps with the voltage held (deterministic: n depends only on the state and dt). The speed is clamped at 0
    /// after each sub-step, as after a step.</summary>
    internal int SubSteps(double dt)
    {
        double decay = (Complex.One + new Complex(0.0, _m.X - _m.Xp) / _z).Magnitude / _m.TpoSeconds;
        double lambda = decay + Math.Abs(1.0 - _w) * _ws;
        double need = lambda * dt / Rk4StabilityBound;
        if (!(need <= MaxSubSteps))
            throw new InvalidOperationException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"induction motor: a step of {dt} s needs more than {MaxSubSteps} RK4 sub-steps for a stable rotor EMF (|lambda| {lambda:G6} 1/s); use a shorter step."));
        return need <= 1.0 ? 1 : (int)Math.Ceiling(need);
    }

    internal void Advance(Complex v, double dt)
    {
        int n = SubSteps(dt);
        if (n == 1) { Rk4(v, dt); return; }
        double h = dt / n;
        for (int s = 0; s < n; s++) Rk4(v, h);
    }

    private void Rk4(Complex v, double dt)
    {
        (Complex k1e, double k1w) = Rates(_e, _w, v);
        (Complex k2e, double k2w) = Rates(_e + 0.5 * dt * k1e, _w + 0.5 * dt * k1w, v);
        (Complex k3e, double k3w) = Rates(_e + 0.5 * dt * k2e, _w + 0.5 * dt * k2w, v);
        (Complex k4e, double k4w) = Rates(_e + dt * k3e, _w + dt * k3w, v);
        _e += dt / 6.0 * (k1e + 2.0 * k2e + 2.0 * k3e + k4e);
        _w += dt / 6.0 * (k1w + 2.0 * k2w + 2.0 * k3w + k4w);
        if (_w < 0.0) _w = 0.0;
    }

    /// <summary>Initialise <paramref name="m"/> drawing <paramref name="power"/> (pu, motor base) at <paramref name="v0"/>:
    /// the stable-branch slip, the internal EMF E' = V - (Ra + jX') I and the load torque at the initial speed.</summary>
    internal static (Complex E0, double W0, double T0, Complex I0) Equilibrium(InductionMotorParameters m, double power, Complex v0,
        double nominalHz, string who)
    {
        double vm = v0.Magnitude;
        double s = m.SlipFor(power, vm, nominalHz);
        if (!double.IsFinite(s))
            throw new InvalidOperationException($"{who}: the motor cannot draw {ControlBlocks.Num(power)} pu on its base at {ControlBlocks.Num(vm)} pu: "
                + "even at its breakdown slip it draws less, so it would stall at the solved operating point; check the motor base and the reactances.");
        Complex i0 = v0 / m.Impedance(s, nominalHz);
        Complex e0 = v0 - new Complex(m.Ra, m.Xp) * i0;
        double te = e0.Real * i0.Real + e0.Imaginary * i0.Imaginary;
        double w0 = 1.0 - s;
        return (e0, w0, te, i0);
    }
}

/// <summary>
/// WP3b (RC5): a third-order induction motor load (PSS/E CIM5 or CIMW with a single cage; ANDES / PowerWorld's
/// third-order motor). The motor takes the whole <see cref="DynamicLoadData.PMw"/> on <paramref name="MotorMva"/> of
/// rating; its reactive power at the operating point follows from its circuit, and the difference from
/// <see cref="DynamicLoadData.QMvar"/> is a constant shunt at the bus (PSS/E's "admittance of initial condition Mvar
/// difference"). The motor's Norton admittance and that shunt are stamped in the network; the motor injects E'/(Ra + jX').
/// </summary>
/// <param name="Motor">The motor (on its own base).</param>
/// <param name="MotorMva">The motor MVA base (PSS/E MBASE, or PMULT x the MW load when MBASE is 0).</param>
public sealed record InductionMotorLoadData(int BusId, double PMw, double QMvar, InductionMotorParameters Motor, double MotorMva)
    : DynamicLoadData(BusId, PMw, QMvar)
{
    /// <inheritdoc/>
    public override string Model => "third-order induction motor";

    internal override DynamicLoadPlan Plan(Complex v0, double baseMva, double nominalHz)
    {
        string who = Label;
        ArgumentNullException.ThrowIfNull(Motor);
        Motor.Check();
        LibraryChecks.Positive(who, "MotorMva", MotorMva);
        LibraryChecks.Positive(who, "PMw", PMw);
        LoadChecks.Finite(who, "QMvar", QMvar);
        double k = MotorMva / baseMva;   // motor base -> system base
        (Complex e0, double w0, double t0, Complex i0) = InductionMotorState.Equilibrium(Motor, PMw / MotorMva, v0, nominalHz, who);
        double v2 = v0.Magnitude * v0.Magnitude;
        double qMotor = (v0 * Complex.Conjugate(i0)).Imaginary * k;
        Complex yComp = new Complex(0.0, -(QMvar / baseMva - qMotor)) / v2;
        Complex yMotor = Complex.One / new Complex(Motor.Ra, Motor.Xp) * k;
        return new Plan_(Motor, nominalHz, e0, w0, t0, k, yMotor, yComp);
    }

    private sealed class Plan_(InductionMotorParameters m, double hz, Complex e0, double w0, double t0, double k, Complex yMotor, Complex yComp)
        : DynamicLoadPlan
    {
        internal override Complex NortonAdmittance => yMotor + yComp;

        internal override IRmsConverterModel CreateModel() => new State(new InductionMotorState(m, hz, e0, w0, t0), k, yMotor, yComp);
    }

    private sealed class State(InductionMotorState motor, double k, Complex yMotor, Complex yComp) : DynamicLoadState(yMotor + yComp)
    {
        public InductionMotorState Motor => motor;

        protected override Complex Drawn(Complex v) => motor.Current(v) * k + yComp * v;

        protected override void Advance(Complex v, double dt) => motor.Advance(v, dt);
    }
}
