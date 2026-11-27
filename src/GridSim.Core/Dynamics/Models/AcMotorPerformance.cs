using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// WP20 (RC5): the WECC single-phase air-conditioner performance model, the CMLD motor D (WECC MVWG, "WECC Composite Load
/// Model Specification", January 2015, re-issued April 2021, section "Single-phase Air Conditioner Performance-based Model";
/// public on wecc.org). The compressors are algebraic. On the motor's MVA base (its share of the far-end power over
/// <see cref="LoadingFactor"/>), with Po and Q'o set at initialisation:
/// <code>
///   run, V &gt;= Vbrk:          P = Po + Kp1 (V - Vbrk)^Np1     Q = Q'o + Kq1 (V - Vbrk)^Nq1
///   run, V'stall &lt; V &lt; Vbrk: P = Po + Kp2 (Vbrk - V)^Np2     Q = Q'o + Kq2 (Vbrk - V)^Nq2
///   run, V &lt;= V'stall, or stalled:  P = Gstall V^2,  Q = Bstall V^2,  Gstall + j Bstall = 1 / (Rstall - j Xstall)
/// </code>
/// The WECC values are Vbrk 0.86, Kp1 0, Kq1 6, Nq1 2, Kp2 12, Np2 3.2, Kq2 11, Nq2 2.5 (<see cref="Wecc"/>); the PSS/E
/// CMLD record carries them as data, so they are parameters here. Initialisation (the specification's): Q'o = Po tan(acos
/// CompPF) - Kq1 (1 - Vbrk)^Nq1. Po is the motor's power at its initial voltage V0, the loading factor on its MVA base,
/// less the run-curve term at V0 (Po = LF exactly with the WECC Kp1 = 0 when V0 is above Vbrk), so the motor draws its share
/// at V0. V'stall is where the run curve below Vbrk meets the stall curve, found exactly by bisection (the specification's
/// 0.01 pu scan is indicative; PowerWorld uses 1e-4 pu).
/// <para>The motor is two parts: part A (1 - <see cref="RestartFraction"/>) cannot restart, part B
/// (<see cref="RestartFraction"/>) restarts once the voltage stays above <see cref="RestartVoltage"/> for
/// <see cref="RestartSeconds"/>. Both stall when the voltage stays below <see cref="StallVoltage"/> for
/// <see cref="StallSeconds"/>. Protection (the specification's text and Figures 9 and 10, as the WP20 spec states them):</para>
/// <list type="bullet">
/// <item>Thermal relay, per part: d theta/dt = (I^2 Rstall - theta) / Tth, I the part's current on the motor base; the part's
/// fraction not tripped falls linearly from 1 at theta = Th1t to 0 at theta = Th2t and never rises again. Tth = 0 turns it off.</item>
/// <item>Under-voltage relay: the fraction Fuvr trips, for the rest of the run, once the voltage stays below Vtr1 for Ttr1 or
/// below Vtr2 for Ttr2.</item>
/// <item>Contactors: the connected fraction falls linearly from 1 at Vc1off to 0 at Vc2off on the way down. On the way up the
/// part that dropped out reconnects linearly from Vc2on (none) to Vc1on (all).</item>
/// </list>
/// <para>Each timer counts the whole steps on which its condition holds at the step's voltage, and acts at the end of the
/// step on which that count times the step reaches its delay (<see cref="StepTimer"/>). Not modelled: the frequency terms (df), the voltage measurement lag Tv, the
/// inverse-time stall characteristic (Tstall &lt; 0) and point-on-wave stall.</para>
/// </summary>
public sealed record AcMotorParameters
{
    /// <summary>The loading factor LFm: the motor's MVA base is its share of the power over it.</summary>
    public double LoadingFactor { get; init; } = 1.0;

    /// <summary>CompPF, the compressor power factor at 1 pu.</summary>
    public double PowerFactor { get; init; } = 0.97;

    /// <summary>Vbrk, the breakdown voltage (pu).</summary>
    public double BreakdownVoltage { get; init; } = 0.86;

    /// <summary>Run-curve coefficients and exponents above (1) and below (2) Vbrk.</summary>
    public double Kp1 { get; init; }

    /// <inheritdoc cref="Kp1"/>
    public double Np1 { get; init; } = 1.0;

    /// <inheritdoc cref="Kp1"/>
    public double Kq1 { get; init; } = 6.0;

    /// <inheritdoc cref="Kp1"/>
    public double Nq1 { get; init; } = 2.0;

    /// <inheritdoc cref="Kp1"/>
    public double Kp2 { get; init; } = 12.0;

    /// <inheritdoc cref="Kp1"/>
    public double Np2 { get; init; } = 3.2;

    /// <inheritdoc cref="Kp1"/>
    public double Kq2 { get; init; } = 11.0;

    /// <inheritdoc cref="Kp1"/>
    public double Nq2 { get; init; } = 2.5;

    /// <summary>Vstall (pu) and Tstall (s): below Vstall for Tstall the motor stalls.</summary>
    public double StallVoltage { get; init; } = 0.6;

    /// <inheritdoc cref="StallVoltage"/>
    public double StallSeconds { get; init; } = 9999.0;

    /// <summary>Rstall and Xstall, the stalled impedance (pu on the motor base).</summary>
    public double StallR { get; init; } = 0.124;

    /// <inheritdoc cref="StallR"/>
    public double StallX { get; init; } = 0.114;

    /// <summary>Frst, the restartable fraction; Vrst (pu) and Trst (s), the restart condition.</summary>
    public double RestartFraction { get; init; }

    /// <inheritdoc cref="RestartFraction"/>
    public double RestartVoltage { get; init; } = 0.9;

    /// <inheritdoc cref="RestartFraction"/>
    public double RestartSeconds { get; init; } = 9999.0;

    /// <summary>Fuvr, the fraction under the under-voltage relay; the two stages (Vtr1, Ttr1) and (Vtr2, Ttr2).</summary>
    public double UvFraction { get; init; }

    /// <inheritdoc cref="UvFraction"/>
    public double Vtr1 { get; init; }

    /// <inheritdoc cref="UvFraction"/>
    public double Ttr1 { get; init; } = 9999.0;

    /// <inheritdoc cref="UvFraction"/>
    public double Vtr2 { get; init; }

    /// <inheritdoc cref="UvFraction"/>
    public double Ttr2 { get; init; } = 9999.0;

    /// <summary>The contactor voltages (pu): tripping starts at Vc1off and is complete at Vc2off; reconnection starts at
    /// Vc2on and is complete at Vc1on. All 0 (the default) keeps every contactor closed.</summary>
    public double Vc1off { get; init; }

    /// <inheritdoc cref="Vc1off"/>
    public double Vc2off { get; init; }

    /// <inheritdoc cref="Vc1off"/>
    public double Vc1on { get; init; }

    /// <inheritdoc cref="Vc1off"/>
    public double Vc2on { get; init; }

    /// <summary>Tth (s), the thermal time constant (0 turns the thermal relay off), and the trip start and completion
    /// temperatures Th1t and Th2t (pu).</summary>
    public double ThermalSeconds { get; init; }

    /// <inheritdoc cref="ThermalSeconds"/>
    public double Th1t { get; init; } = 1.3;

    /// <inheritdoc cref="ThermalSeconds"/>
    public double Th2t { get; init; } = 4.3;

    /// <summary>The WECC run-curve values (Vbrk 0.86, Kp1 0, Kq1 6, Nq1 2, Kp2 12, Np2 3.2, Kq2 11, Nq2 2.5) with the given
    /// stall data, protection off.</summary>
    public static AcMotorParameters Wecc(double loadingFactor, double powerFactor, double stallVoltage, double stallSeconds,
        double stallR, double stallX) => new()
    {
        LoadingFactor = loadingFactor, PowerFactor = powerFactor, StallVoltage = stallVoltage, StallSeconds = stallSeconds,
        StallR = stallR, StallX = stallX,
    };

    internal void Check(string who)
    {
        LibraryChecks.Positive(who, "motor D LFm", LoadingFactor);
        if (!(PowerFactor > 0.0 && PowerFactor <= 1.0)) throw LoadChecks.OutOfRange(nameof(PowerFactor), PowerFactor, $"{who}: motor D CompPF must lie in (0, 1].");
        LibraryChecks.Positive(who, "motor D Vbrk", BreakdownVoltage);
        foreach ((string n, double x) in new[] { ("Kp1", Kp1), ("Kq1", Kq1), ("Kp2", Kp2), ("Kq2", Kq2) }) LoadChecks.Finite(who, "motor D " + n, x);
        foreach ((string n, double x) in new[] { ("Np1", Np1), ("Nq1", Nq1), ("Np2", Np2), ("Nq2", Nq2) }) LibraryChecks.Positive(who, "motor D " + n, x);
        LibraryChecks.NonNegative(who, "motor D Vstall", StallVoltage);
        LibraryChecks.NonNegative(who, "motor D Tstall", StallSeconds);
        LibraryChecks.NonNegative(who, "motor D Rstall", StallR);
        LoadChecks.Finite(who, "motor D Xstall", StallX);
        if (!(StallR * StallR + StallX * StallX > 0.0)) throw LoadChecks.OutOfRange(nameof(StallR), StallR, $"{who}: the motor D stall impedance must not be 0.");
        foreach ((string n, double x) in new[] { ("Frst", RestartFraction), ("Fuvr", UvFraction) })
            if (!(x >= 0.0 && x <= 1.0)) throw LoadChecks.OutOfRange(n, x, $"{who}: motor D {n} must lie in [0, 1].");
        foreach ((string n, double x) in new[] { ("Vrst", RestartVoltage), ("Trst", RestartSeconds), ("Vtr1", Vtr1), ("Ttr1", Ttr1), ("Vtr2", Vtr2), ("Ttr2", Ttr2),
                     ("Vc1off", Vc1off), ("Vc2off", Vc2off), ("Vc1on", Vc1on), ("Vc2on", Vc2on), ("Tth", ThermalSeconds) })
            LibraryChecks.NonNegative(who, "motor D " + n, x);
        if (Vc2off > Vc1off) throw LoadChecks.OutOfRange(nameof(Vc2off), Vc2off, $"{who}: motor D Vc2off must not be above Vc1off.");
        if (Vc2on > Vc1on) throw LoadChecks.OutOfRange(nameof(Vc2on), Vc2on, $"{who}: motor D Vc2on must not be above Vc1on.");
        if (Vc1off > 0.0)
        {
            // Contactors in use. At a steady voltage v with Vc2on < v < Vc1off, a step drops part of the motor (v below Vc1off)
            // and the next reconnects some of it (v above Vc2on), so the contactors chatter on alternate steps. Reconnection
            // must therefore start no lower than dropping starts: Vc2on >= Vc1off (then Vc1on >= Vc2on >= Vc1off and
            // Vc2on >= Vc1off >= Vc2off follow). A zero reconnection voltage would reconnect at any voltage.
            if (!(Vc1on > 0.0 && Vc2on > 0.0))
                throw LoadChecks.OutOfRange(nameof(Vc2on), Vc2on, $"{who}: motor D Vc1on and Vc2on must be above 0 when the contactors are in use (Vc1off above 0).");
            if (Vc1on < Vc1off) throw LoadChecks.OutOfRange(nameof(Vc1on), Vc1on, $"{who}: motor D Vc1on must not be below Vc1off.");
            if (Vc2on < Vc2off) throw LoadChecks.OutOfRange(nameof(Vc2on), Vc2on, $"{who}: motor D Vc2on must not be below Vc2off.");
            if (Vc2on < Vc1off) throw LoadChecks.OutOfRange(nameof(Vc2on), Vc2on, $"{who}: motor D Vc2on must not be below Vc1off when the contactors are in use (a steady voltage between them would drop and reconnect part of the motor on alternate steps).");
        }
        if (ThermalSeconds > 0.0 && !(Th1t >= 0.0 && Th2t > Th1t && double.IsFinite(Th2t)))
            throw LoadChecks.OutOfRange(nameof(Th2t), Th2t, $"{who}: motor D needs 0 <= Th1t < Th2t when Tth is above 0.");
    }

    /// <summary>The stall admittance Gstall + j Bstall on the motor base (Q = Bstall V^2 consumed).</summary>
    internal (double G, double B) StallAdmittance()
    {
        double d = StallR * StallR + StallX * StallX;
        return (StallR / d, StallX / d);
    }

    private static double Pw(double x, double e) => e == 1.0 ? x : e == 2.0 ? x * x : DetMath.Pow(x, e);

    /// <summary>The run-curve P and Q terms (without Po and Q'o) at <paramref name="v"/>.</summary>
    internal (double P, double Q) RunTerms(double v)
    {
        double vb = BreakdownVoltage;
        if (v >= vb)
        {
            double d = v - vb;
            return (Kp1 == 0.0 ? 0.0 : Kp1 * Pw(d, Np1), Kq1 == 0.0 ? 0.0 : Kq1 * Pw(d, Nq1));
        }
        double u = vb - v;
        return (Kp2 == 0.0 ? 0.0 : Kp2 * Pw(u, Np2), Kq2 == 0.0 ? 0.0 : Kq2 * Pw(u, Nq2));
    }

    /// <summary>Po and Q'o for a motor at initial voltage <paramref name="v0"/> (motor base): Po = LF - P term at V0, Q'o =
    /// Po tan(acos CompPF) - Kq1 (1 - Vbrk)^Nq1.</summary>
    internal (double Po, double Qo) Initial(double v0)
    {
        double po = LoadingFactor - RunTerms(v0).P;
        double tan = Math.Sqrt(1.0 - PowerFactor * PowerFactor) / PowerFactor;
        double at1 = 1.0 - BreakdownVoltage;
        double q1 = Kq1 == 0.0 ? 0.0 : at1 >= 0.0 ? Kq1 * Pw(at1, Nq1) : 0.0;
        return (po, po * tan - q1);
    }

    /// <summary>V'stall: where Po + Kp2 (Vbrk - V)^Np2 = Gstall V^2 on (0, Vbrk), by bisection (a fixed 200 halvings, so
    /// every run takes the same arithmetic); 0 when the stall curve stays below the run curve up to Vbrk.</summary>
    internal double StallBreakVoltage(double po)
    {
        (double g, _) = StallAdmittance();
        double vb = BreakdownVoltage;
        double F(double v) => po + RunTerms(v).P - g * v * v;
        if (F(vb) > 0.0) return 0.0;
        double lo = 0.0, hi = vb;
        for (int k = 0; k < 200; k++)
        {
            double mid = 0.5 * (lo + hi);
            if (mid <= lo || mid >= hi) break;
            if (F(mid) > 0.0) lo = mid; else hi = mid;
        }
        return hi;
    }
}

/// <summary>The running motor D (<see cref="AcMotorParameters"/>): powers on the motor base, scaled by the caller.</summary>
internal sealed class AcMotorState
{
    private readonly AcMotorParameters _p;
    private readonly double _po, _qo, _vStallBreak, _g, _b;
    private readonly FirstOrderLag? _thA, _thB;
    private long _nStall, _nRestart, _nUv1, _nUv2;   // whole steps each timer's condition has held
    private double _cLow = 1.0;

    internal AcMotorState(AcMotorParameters p, double v0, string who)
    {
        _p = p;
        (_po, _qo) = p.Initial(v0);
        (_g, _b) = p.StallAdmittance();
        _vStallBreak = p.StallBreakVoltage(_po);
        if (!(v0 > p.StallVoltage) || !(v0 > _vStallBreak))
            throw new InvalidOperationException($"{who}: the far-end voltage {ControlBlocks.Num(v0)} pu is not above motor D's stall voltage "
                + $"(Vstall {ControlBlocks.Num(p.StallVoltage)}, V'stall {ControlBlocks.Num(_vStallBreak)}), so it would start stalled.");
        if (p.UvFraction > 0.0 && (v0 < p.Vtr1 || v0 < p.Vtr2))
            throw new InvalidOperationException($"{who}: the far-end voltage {ControlBlocks.Num(v0)} pu is below motor D's under-voltage relay setting "
                + $"(Vtr1 {ControlBlocks.Num(p.Vtr1)}, Vtr2 {ControlBlocks.Num(p.Vtr2)}), so its fraction Fuvr would trip with no disturbance.");
        if (v0 < p.Vc1off)
            throw new InvalidOperationException($"{who}: the far-end voltage {ControlBlocks.Num(v0)} pu is below motor D's contactor setting Vc1off "
                + $"{ControlBlocks.Num(p.Vc1off)}, so the contactors would drop with no disturbance.");
        if (p.ThermalSeconds > 0.0)
        {
            (double pr, double qr) = Run(v0);
            double u0 = (pr * pr + qr * qr) / (v0 * v0) * p.StallR;
            if (!(u0 < p.Th1t))
                throw new InvalidOperationException($"{who}: motor D's running temperature {ControlBlocks.Num(u0)} pu is not below Th1t, so the thermal relay would trip at rest.");
            _thA = new FirstOrderLag(1.0, p.ThermalSeconds);
            _thB = new FirstOrderLag(1.0, p.ThermalSeconds);
            _thA.Initialize(u0);
            _thB.Initialize(u0);
        }
        ContactorFraction = 1.0;
    }

    /// <summary>Po and Q'o (motor base).</summary>
    internal (double Po, double Qo) Initial => (_po, _qo);

    /// <summary>V'stall (pu).</summary>
    internal double StallBreakVoltage => _vStallBreak;

    /// <summary>True once part A (non-restarting) or part B (restarting) is stalled.</summary>
    internal bool StalledA { get; private set; }

    /// <inheritdoc cref="StalledA"/>
    internal bool StalledB { get; private set; }

    /// <summary>True once the under-voltage relay has tripped its fraction.</summary>
    internal bool UvTripped { get; private set; }

    /// <summary>The fraction the contactors keep connected.</summary>
    internal double ContactorFraction { get; private set; }

    /// <summary>Each part's fraction not tripped by its thermal relay.</summary>
    internal double ThermalFractionA { get; private set; } = 1.0;

    /// <inheritdoc cref="ThermalFractionA"/>
    internal double ThermalFractionB { get; private set; } = 1.0;

    /// <summary>The temperatures of parts A and B (pu; 0 with the relay off).</summary>
    internal double TemperatureA => _thA?.Output ?? 0.0;

    /// <inheritdoc cref="TemperatureA"/>
    internal double TemperatureB => _thB?.Output ?? 0.0;

    /// <summary>A running part's P and Q at <paramref name="v"/> (motor base): the run curve, or the stall curve at or
    /// below V'stall.</summary>
    internal (double P, double Q) Run(double v)
    {
        if (v <= _vStallBreak) return Stalled(v);
        (double tp, double tq) = _p.RunTerms(v);
        return (_po + tp, _qo + tq);
    }

    internal (double P, double Q) Stalled(double v) => (_g * v * v, _b * v * v);

    /// <summary>The motor's P and Q at <paramref name="v"/> on its base, every state held.</summary>
    internal (double P, double Q) Power(double v)
    {
        (double pa, double qa) = StalledA ? Stalled(v) : Run(v);
        (double pb, double qb) = StalledB ? Stalled(v) : Run(v);
        double fa = (1.0 - _p.RestartFraction) * ThermalFractionA, fb = _p.RestartFraction * ThermalFractionB;
        double on = (UvTripped ? 1.0 - _p.UvFraction : 1.0) * ContactorFraction;
        return (on * (fa * pa + fb * pb), on * (fa * qa + fb * qb));
    }

    private static double Clip01(double x) => x <= 0.0 ? 0.0 : x >= 1.0 ? 1.0 : x;

    /// <summary>The contactor's down curve at <paramref name="v"/>: 1 at or above Vc1off, 0 at or below Vc2off.</summary>
    private double Down(double v) => v >= _p.Vc1off ? 1.0 : v <= _p.Vc2off ? 0.0 : (v - _p.Vc2off) / (_p.Vc1off - _p.Vc2off);

    /// <summary>The contactor's up curve: 0 at or below Vc2on, 1 at or above Vc1on.</summary>
    private double Up(double v) => v >= _p.Vc1on ? 1.0 : v <= _p.Vc2on ? 0.0 : Clip01((v - _p.Vc2on) / (_p.Vc1on - _p.Vc2on));

    /// <summary>Advance one step of <paramref name="dt"/> at the held voltage <paramref name="v"/>: the thermal states, then
    /// the stall, restart and under-voltage timers, then the contactors.</summary>
    internal void Advance(double v, double dt)
    {
        if (_thA is not null && _thB is not null)
        {
            double v2 = v * v;
            (double pa, double qa) = StalledA ? Stalled(v) : Run(v);
            (double pb, double qb) = StalledB ? Stalled(v) : Run(v);
            double ua = v2 > 0.0 ? (pa * pa + qa * qa) / v2 * _p.StallR : 0.0, ub = v2 > 0.0 ? (pb * pb + qb * qb) / v2 * _p.StallR : 0.0;
            double ta = _thA.Step(ua, dt), tb = _thB.Step(ub, dt);
            double span = _p.Th2t - _p.Th1t;
            ThermalFractionA = Math.Min(ThermalFractionA, Clip01((_p.Th2t - ta) / span));
            ThermalFractionB = Math.Min(ThermalFractionB, Clip01((_p.Th2t - tb) / span));
        }
        if (v < _p.StallVoltage)
        {
            if (StepTimer.Reached(++_nStall, _p.StallSeconds, dt)) { StalledA = true; StalledB = true; }
        }
        else _nStall = 0;
        if (StalledB && v > _p.RestartVoltage)
        {
            if (StepTimer.Reached(++_nRestart, _p.RestartSeconds, dt)) { StalledB = false; _nRestart = 0; }
        }
        else _nRestart = 0;
        if (v < _p.Vtr1) { if (StepTimer.Reached(++_nUv1, _p.Ttr1, dt)) UvTripped = true; } else _nUv1 = 0;
        if (v < _p.Vtr2) { if (StepTimer.Reached(++_nUv2, _p.Ttr2, dt)) UvTripped = true; } else _nUv2 = 0;
        double down = Down(v);
        if (down < ContactorFraction) ContactorFraction = down;
        else
        {
            double target = _cLow + (1.0 - _cLow) * Up(v);
            if (target > ContactorFraction) ContactorFraction = target;
        }
        _cLow = ContactorFraction >= 1.0 ? 1.0 : Math.Min(_cLow, ContactorFraction);
    }
}
