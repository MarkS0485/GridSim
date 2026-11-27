namespace GridSim.Core.Dynamics.Models.Iec61400;

// RC5 WP23: the discrete blocks the IEC 61400 modules are built from. Every block advances one fixed step dt with its inputs
// held over the step (a zero-order hold), so a run is a pure function of its inputs and step and repeats bit for bit.
//  - A first-order lag T dy/dt = u - y is advanced exactly under the hold: y' = u + (y - u) exp(-dt/T). Written this way an
//    equilibrium (y = u) stays exactly where it is. T at or below ControlBlocks.InstantThresholdSeconds is the pass-through y' = u.
//  - A rate limit acts on the change over the step: the step's change is clamped to [rateDown dt, rateUp dt]. When the change is
//    inside the band the lag's own result is kept, so an unlimited step is the plain lag bit for bit.
//  - An absolute limit clamps the state itself (non-windup): the output leaves the limit the step the input turns back. Dynawo's
//    AbsLimRateLimFirstOrderAntiWindup uses a finite anti-windup gain instead; clamping is the limit of a large gain.
//  - A ramp limiter (Dynawo RampLimiter with its integration step tS = dt) moves its output toward the input by at most the
//    rate times dt per step.
//  - A PI controller is advanced by forward Euler: the output of a step is the integral state at the start of the step plus the
//    proportional term, and the integral then takes ki e dt, clamped (non-windup) and held while frozen.

/// <summary>RC5 WP23: shared discrete block arithmetic (see the file comment).</summary>
internal static class Iec61400Blocks
{
    /// <summary>The exact zero-order-hold step of the lag <c>T dy/dt = u - y</c>.</summary>
    internal static double Lag(double y, double u, double t, double dt)
        => t <= ControlBlocks.InstantThresholdSeconds ? u : u + (y - u) * DetMath.Exp(-dt / t);

    /// <summary>The lag, then the change limited to [<paramref name="rateDown"/> dt, <paramref name="rateUp"/> dt], then the
    /// state clamped to [<paramref name="min"/>, <paramref name="max"/>].</summary>
    internal static double RateLimitedLag(double y, double u, double t, double dt, double rateDown, double rateUp, double min, double max)
    {
        double target = Lag(y, u, t, dt);
        double change = target - y;
        double next = target;
        if (change > rateUp * dt) next = y + rateUp * dt;
        else if (change < rateDown * dt) next = y + rateDown * dt;
        return Math.Clamp(next, min, max);
    }

    /// <summary>The ramp limiter: the output moves toward <paramref name="u"/> by at most the rate times dt.</summary>
    internal static double Ramp(double y, double u, double dt, double rateDown, double rateUp)
    {
        double change = u - y;
        if (change > rateUp * dt) return y + rateUp * dt;
        if (change < rateDown * dt) return y + rateDown * dt;
        return u;
    }

    /// <summary>The dead zone: 0 inside [<paramref name="lower"/>, <paramref name="upper"/>], else the excess beyond the edge.</summary>
    internal static double DeadZone(double x, double lower, double upper)
        => x > upper ? x - upper : x < lower ? x - lower : 0.0;

    /// <summary>The angle <paramref name="theta"/> moved by whole turns to within half a turn of <paramref name="near"/>.</summary>
    internal static double Unwrap(double theta, double near)
    {
        double d = theta - near;
        double turns = Math.Round(d / (2.0 * Math.PI), MidpointRounding.ToEven);
        return theta - turns * (2.0 * Math.PI);
    }

    /// <summary>The largest whole number of steps of <paramref name="dt"/> that does not pass <paramref name="seconds"/>, with a
    /// 4-ulp guard so a duration that is a whole number of steps up to rounding counts as that number (a timer counted in whole
    /// steps; "time in the state exceeds T" is then "steps in the state exceed this").</summary>
    internal static long StepsWithin(double seconds, double dt)
    {
        const double Guard = 4.0 * 1.1102230246251565e-16;
        double r = seconds / dt;
        return (long)Math.Floor(r + Guard * r);
    }
}

/// <summary>RC5 WP23: the generator system (WindGenType4IEC; Dynawo GenSystem4). States: the active and reactive currents in the
/// IEC frame (unit pu; the reactive current is positive when the unit absorbs reactive power, as Dynawo's RefFrameRotation
/// gives: the injected current is (ip + j iq) e^(j theta_pll), so Q = -U iq).</summary>
internal sealed class Iec61400GenSystemModule
{
    private readonly Iec61400GenSystem _p;

    internal Iec61400GenSystemModule(Iec61400GenSystem p, double ip0, double iq0)
    {
        _p = p;
        Ip = ip0;
        Iq = iq0;
    }

    internal double Ip { get; private set; }

    internal double Iq { get; private set; }

    /// <summary>One step toward the commands within the limits from the current limiter.</summary>
    internal void Step(double ipCmd, double iqCmd, double ipMax, double iqMin, double iqMax, double dt)
    {
        // Dynawo GenSystem4: the active current has a rise-rate limit only and an upper limit only (its -999 placeholders).
        Ip = Iec61400Blocks.RateLimitedLag(Ip, ipCmd, _p.Tg, dt, double.NegativeInfinity, _p.Dipmax, double.NegativeInfinity, ipMax);
        Iq = Iec61400Blocks.RateLimitedLag(Iq, iqCmd, _p.Tg, dt, _p.Diqmin, _p.Diqmax, iqMin, Math.Max(iqMin, iqMax));
    }
}

/// <summary>RC5 WP23: the current limiter (WindContCurrLimIEC; Dynawo CurrentLimiter2020 with BaseCurrentLimiter). Pure function
/// of its inputs.</summary>
internal static class Iec61400CurrentLimiterModule
{
    /// <summary>The limits for one step (IEC frame, unit pu).</summary>
    /// <param name="p">The limiter data.</param>
    /// <param name="uf">The filtered terminal voltage.</param>
    /// <param name="omegaGen">The generator speed (1 for Type 4A).</param>
    /// <param name="fFrt">The fault state 0 (normal), 1 (fault) or 2 (post-fault).</param>
    /// <param name="ipCmd">The active current command.</param>
    /// <param name="iqCmd">The reactive current command.</param>
    internal static (double IpMax, double IqMin, double IqMax) Limits(Iec61400CurrentLimit p, double uf, double omegaGen, int fFrt,
        double ipCmd, double iqCmd)
    {
        double iMax = fFrt == 1 ? p.Imaxdip : p.Imax;
        double ipTab = p.Ipvdl.Evaluate(uf);
        double iqTab = p.Iqvdl.Evaluate(uf);
        double w = p.Mdfslim ? omegaGen : 1.0;
        // Reactive priority applies only during and after a fault (Dynawo: the switch index is fFrt times Mqpri).
        bool qPriority = p.Mqpri && fFrt != 0;
        double iqMax, ipMax;
        if (!qPriority)
        {
            double a = Math.Min(ipCmd / w, ipTab);
            iqMax = Math.Min(Math.Sqrt(Math.Max(0.0, iMax * iMax - a * a)), iqTab);
            ipMax = w * ipTab;
        }
        else
        {
            double b = Math.Min(iqTab, Math.Abs(iqCmd));
            iqMax = iqTab;
            ipMax = w * Math.Min(ipTab, Math.Sqrt(Math.Max(0.0, iMax * iMax - b * b)));
        }
        // The capacitive limit: no capacitive current above upqumax, with slope kpqu (Dynawo max2/greater/switch3).
        double m = Math.Max(-iqMax, p.Kpqu * (uf - p.Upqumax));
        double iqMin = iqMax > m ? m : iqMax;
        return (ipMax, iqMin, iqMax);
    }
}

/// <summary>RC5 WP23: the active power control (WindContPType4aIEC / 4bIEC; Dynawo PControl4A2020 / PControl4B2020 with
/// BasePControl4).</summary>
internal sealed class Iec61400PControlModule
{
    private readonly Iec61400PControl _p;
    private readonly bool _typeB;
    private readonly double _pRef;

    internal Iec61400PControlModule(Iec61400PControl p, bool typeB, double p0)
    {
        _p = p;
        _typeB = typeB;
        _pRef = p0;
        RefState = p0;
        Order = p0;
        Aero = p0;
    }

    /// <summary>Type 4A: the lagged reference; Type 4B: the ramp-limited reference.</summary>
    internal double RefState { get; private set; }

    /// <summary>The power order (the state of the order lag).</summary>
    internal double Order { get; private set; }

    /// <summary>Type 4B: the aerodynamic power (the tpaero lag); for Type 4A it stays at the initial power and is not read.</summary>
    internal double Aero { get; private set; }

    /// <summary>The active current command for the present order.</summary>
    internal static double Command(double order, double uf) => order / Math.Max(uf, 0.01);

    /// <summary>One step. <paramref name="u"/> is the unfiltered terminal voltage, <paramref name="uf"/> the filtered one.</summary>
    internal void Step(double u, double uf, double ipMax, double omegaGen, double dt)
    {
        RefState = _typeB
            ? Iec61400Blocks.Ramp(RefState, _pRef, dt, _p.Dprefmin, _p.Dprefmax)
            : Iec61400Blocks.RateLimitedLag(RefState, _pRef, _p.Tpwtref, dt, _p.Dprefmin, _p.Dprefmax, double.NegativeInfinity, double.PositiveInfinity);
        double x = _p.Mpuscale && u < _p.Updip ? RefState * u : RefState;
        if (_typeB) Aero = Iec61400Blocks.Lag(Aero, x, _p.Tpaero, dt);
        double input = _typeB ? x * omegaGen : x;
        Order = Iec61400Blocks.RateLimitedLag(Order, input, _p.Tpord, dt, double.NegativeInfinity, _p.Dpmax, double.NegativeInfinity, ipMax * u);
    }
}

/// <summary>RC5 WP23: the two-mass drive train of Type 4B (WindMechIEC; Dynawo Mechanical). The shaft torque is
/// <c>cdrt (w_wtr - w_gen) + kdrt theta</c> with <c>d theta/dt = w_wtr - w_gen</c> (Dynawo's PI block: gain 1 on the twist, no
/// angle base factor); <c>2 Hwtr dw_wtr/dt = P_aero / w_wtr - T</c>, <c>2 Hgen dw_gen/dt = T - P_ag / w_gen</c>. Advanced by
/// the semi-implicit (symplectic) Euler step: the speeds from the start-of-step torque, then the twist from the new speeds. For
/// the undamped linear system this map has its eigenvalues on the unit circle, so a free oscillation neither grows nor decays;
/// it is stable for <c>dt wn &lt; 2</c> with <c>wn^2 = kdrt (1/(2 Hwtr) + 1/(2 Hgen))</c>, which the unit checks.</summary>
internal sealed class Iec61400MechanicalModule
{
    private readonly Iec61400Mechanical _p;

    internal Iec61400MechanicalModule(Iec61400Mechanical p, double torque0)
    {
        _p = p;
        OmegaWtr = 1.0;
        OmegaGen = 1.0;
        Twist = torque0 / p.Kdrt;
    }

    internal double OmegaWtr { get; private set; }

    internal double OmegaGen { get; private set; }

    /// <summary>The integrated twist (the PI integrator state).</summary>
    internal double Twist { get; private set; }

    /// <summary>The undamped natural frequency (rad/s) of the linearised two-mass system.</summary>
    internal double NaturalFrequency => Math.Sqrt(_p.Kdrt * (1.0 / (2.0 * _p.Hwtr) + 1.0 / (2.0 * _p.Hgen)));

    /// <summary>The damping rate <c>cdrt (1/(2 Hwtr) + 1/(2 Hgen))</c> (1/s) of the relative speed.</summary>
    internal double DampingRate => _p.Cdrt * (1.0 / (2.0 * _p.Hwtr) + 1.0 / (2.0 * _p.Hgen));

    internal void Step(double pAero, double pAg, double dt)
    {
        double torque = _p.Cdrt * (OmegaWtr - OmegaGen) + _p.Kdrt * Twist;
        double wtr = OmegaWtr + dt * (pAero / OmegaWtr - torque) / (2.0 * _p.Hwtr);
        double gen = OmegaGen + dt * (torque - pAg / OmegaGen) / (2.0 * _p.Hgen);
        OmegaWtr = wtr;
        OmegaGen = gen;
        Twist += dt * (wtr - gen);
    }
}

/// <summary>RC5 WP23: the phase-locked loop (Dynawo PLL). The voltage tests use the voltage of the previous step (Dynawo's
/// FixedBooleanDelay of one integration step).</summary>
internal sealed class Iec61400PllModule
{
    private readonly Iec61400Pll _p;
    private bool _belowFilter;   // previous step's voltage below upll1: the filtered angle is used
    private bool _belowFreeze;   // previous step's voltage below upll2: the filtered angle is frozen

    internal Iec61400PllModule(Iec61400Pll p, double u0, double angle0)
    {
        _p = p;
        Filtered = angle0;
        _belowFilter = u0 < p.Upll1;
        _belowFreeze = u0 < p.Upll2;
    }

    /// <summary>The filtered angle (rad).</summary>
    internal double Filtered { get; private set; }

    /// <summary>True when the output is the filtered angle (else it is the terminal angle at once).</summary>
    internal bool UsesFiltered => _belowFilter;

    internal void Step(double u, double angle, double dt)
    {
        if (!_belowFreeze) Filtered = Iec61400Blocks.Lag(Filtered, Iec61400Blocks.Unwrap(angle, Filtered), _p.Tpll, dt);
        _belowFilter = u < _p.Upll1;
        _belowFreeze = u < _p.Upll2;
    }
}

/// <summary>RC5 WP23: the reactive power control with its fault ride-through states (WindContQIEC; Dynawo QControl2020 with
/// BaseQControl and DelayFlag). Output: the reactive current command in the IEC frame (positive absorbing).</summary>
internal sealed class Iec61400QControlModule
{
    private readonly Iec61400QControl _p;
    private readonly double _xRef;       // the reference: voltage (mode 0) or reactive power (modes 1, 2)
    private readonly double _tanPhi;     // modes 3, 4
    private long _stepsSinceFault = -1;  // whole steps since the fault state ended (-1: no fault yet)
    private bool _hadFault;

    /// <param name="p">The control data.</param>
    /// <param name="u0">The initial terminal voltage (pu).</param>
    /// <param name="p0">The initial active power (unit pu, generating positive).</param>
    /// <param name="q0">The initial reactive power (unit pu, exporting positive).</param>
    internal Iec61400QControlModule(Iec61400QControl p, double u0, double p0, double q0)
    {
        _p = p;
        double drop0 = DroppedVoltage(p, u0, p0, q0);
        double iqExport0 = q0 / u0;
        Uss = u0;
        switch (p.Mode)
        {
            case Iec61400QControlMode.Voltage:
                _xRef = drop0 - p.Uref0;
                Require("the voltage reference xref + uref0 (the dropped voltage)", drop0, p.Umin, p.Umax);
                Require("the voltage controller output (the exporting reactive current)", iqExport0, p.Iqmin, p.Iqmax);
                VoltageIntegral = iqExport0;
                break;
            case Iec61400QControlMode.ReactivePower:
            case Iec61400QControlMode.PowerFactor:
                if (p.Mode == Iec61400QControlMode.PowerFactor)
                {
                    if (p0 == 0.0) throw new ArgumentException("IEC 61400 power factor control needs a non-zero initial active power.", nameof(p0));
                    _tanPhi = q0 / Math.Abs(p0);
                }
                else _xRef = q0;
                Require("the reactive power reference", q0, p.Qmin, p.Qmax);
                Require("the reactive power controller output (the dropped voltage)", drop0, p.Umin, p.Umax);
                Require("the voltage controller output (the exporting reactive current)", iqExport0, p.Iqmin, p.Iqmax);
                QIntegral = drop0;
                VoltageIntegral = iqExport0;
                break;
            default:
                if (p.Mode == Iec61400QControlMode.OpenLoopPowerFactor)
                {
                    if (p0 == 0.0) throw new ArgumentException("IEC 61400 open-loop power factor control needs a non-zero initial active power.", nameof(p0));
                    _tanPhi = q0 / Math.Abs(p0);
                }
                else _xRef = q0;
                Require("the reactive power order", q0, p.Qmin, p.Qmax);
                Require("the open-loop reactive current", iqExport0, p.Iqmin, p.Iqmax);
                QOrder = p.Mode == Iec61400QControlMode.OpenLoopPowerFactor ? _tanPhi * Math.Abs(p0) : q0;
                OpenLoopCurrent = iqExport0;
                break;
        }
        if (u0 < p.Uqdip || u0 > p.Uqrise)
            throw new ArgumentException($"IEC 61400: the initial voltage {Num(u0)} pu is outside the normal band [uqdip {Num(p.Uqdip)}, uqrise {Num(p.Uqrise)}], "
                + "so the unit would start in its fault state; there is no quiet start.", nameof(u0));
    }

    /// <summary>The fault state: 0 normal, 1 fault, 2 post-fault.</summary>
    internal int FrtState { get; private set; }

    /// <summary>The reactive power PI integral (a voltage reference, modes 1 and 3).</summary>
    internal double QIntegral { get; private set; }

    /// <summary>The voltage PI integral (an exporting reactive current, modes 0, 1 and 3).</summary>
    internal double VoltageIntegral { get; private set; }

    /// <summary>The reactive power order (modes 2 and 4).</summary>
    internal double QOrder { get; private set; }

    /// <summary>The open-loop exporting reactive current, held during a fault (modes 2 and 4).</summary>
    internal double OpenLoopCurrent { get; private set; }

    /// <summary>The steady-state voltage (the tuss lag).</summary>
    internal double Uss { get; private set; }

    /// <summary>The voltage the controller regulates: <c>|U + (rdroop + j xdroop)(P - jQ)/U|</c> (Dynawo VDrop, fed with the
    /// generating P and Q). With no drop impedance it is <paramref name="u"/> itself.</summary>
    internal static double DroppedVoltage(Iec61400QControl p, double u, double pg, double qg)
    {
        if (p.Rdroop == 0.0 && p.Xdroop == 0.0) return u;
        double re = u + (p.Rdroop * pg + p.Xdroop * qg) / u;
        double im = (p.Xdroop * pg - p.Rdroop * qg) / u;
        return Math.Sqrt(re * re + im * im);
    }

    /// <summary>One step from the filtered voltage, active and reactive power (unit pu, generating and exporting positive).
    /// Returns the reactive current command (IEC frame, positive absorbing).</summary>
    internal double Step(double uf, double pf, double qf, double dt)
    {
        // The fault state (Dynawo QControl2020 switch5 and DelayFlag): fault while uf < uqdip or uf > uqrise; after a dip,
        // post-fault until the whole steps since its end (0 at the first step after it) reach tpost (StepTimer: ceil(tpost/dt)
        // with a 4-ulp guard), so tpost = 0 gives no post-fault state and tpost = 10 dt gives ten steps of it.
        bool dip = uf < _p.Uqdip;
        if (dip) { _hadFault = true; _stepsSinceFault = -1; }
        else if (_hadFault) _stepsSinceFault++;
        int state = dip ? 1 : _hadFault && !StepTimer.Reached(_stepsSinceFault, _p.Tpost, dt) ? 2 : 0;
        if (uf > _p.Uqrise) state = 1;
        FrtState = state;
        bool frozen = state >= 1;
        double uDen = Math.Max(uf, 0.01);

        double xRef = _p.Mode is Iec61400QControlMode.PowerFactor or Iec61400QControlMode.OpenLoopPowerFactor ? _tanPhi * Math.Abs(pf) : _xRef;
        double iqBase;
        if (_p.Mode is Iec61400QControlMode.OpenLoopReactivePower or Iec61400QControlMode.OpenLoopPowerFactor)
        {
            if (!frozen)
            {
                QOrder = Math.Clamp(Iec61400Blocks.Lag(QOrder, xRef, _p.Tqord, dt), _p.Qmin, _p.Qmax);
                OpenLoopCurrent = Math.Clamp(QOrder / uDen, _p.Iqmin, _p.Iqmax);
            }
            iqBase = -OpenLoopCurrent;
        }
        else
        {
            double uRef;
            if (_p.Mode == Iec61400QControlMode.Voltage) uRef = Math.Clamp(xRef + _p.Uref0, _p.Umin, _p.Umax);
            else
            {
                double eQ = Math.Clamp(xRef, _p.Qmin, _p.Qmax) - qf;
                double qOut = QIntegral + _p.Kpq * eQ;
                if (!frozen) QIntegral = Math.Clamp(QIntegral + _p.Kiq * eQ * dt, _p.Umin, _p.Umax);
                uRef = Math.Clamp(qOut, _p.Umin, _p.Umax);
            }
            double eU = uRef - DroppedVoltage(_p, uf, pf, qf);
            double prop = frozen ? _p.Kpufrt * eU : Math.Clamp(_p.Kpu * eU, _p.Qmin / uDen, _p.Qmax / uDen);
            double iqExport = VoltageIntegral + prop;
            if (!frozen) VoltageIntegral = Math.Clamp(VoltageIntegral + _p.Kiu * eU * dt, _p.Iqmin, _p.Iqmax);
            iqBase = -iqExport;
        }

        // The voltage change from the steady-state voltage (Dynawo's Derivative block with k = T = tuss: y = u - x, the state
        // before this step's advance), through the dead band, scaled by kqv.
        double iqv = _p.Kqv * Iec61400Blocks.DeadZone(uf - Uss, _p.Udb1, _p.Udb2);
        Uss = Iec61400Blocks.Lag(Uss, uf, _p.Tuss, dt);

        if (state == 0) return iqBase;
        double fault = _p.FrtMode == 0 ? iqv : iqBase + iqv;
        double cmd = state == 1 || _p.FrtMode != 2 ? fault : iqBase + _p.Iqpost;
        return Math.Clamp(cmd, _p.Iqmin, _p.Iqh1);
    }

    private static void Require(string what, double value, double min, double max)
    {
        if (!(value >= min && value <= max))
            throw new ArgumentException($"IEC 61400: the initial operating point needs {what} = {Num(value)}, outside [{Num(min)}, {Num(max)}]; "
                + "the run would not start at equilibrium.", nameof(value));
    }

    private static string Num(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>RC5 WP23: the grid protection (WindProtectionIEC; Dynawo GridProtection2020). Each condition counts the whole steps
/// it has held (0 at the step it starts); the unit trips at the step where the count exceeds the whole steps that fit in the
/// table's allowed time for the present level. A trip is latched.</summary>
internal sealed class Iec61400ProtectionModule
{
    private readonly Iec61400Protection _p;
    private readonly long[] _count = { -1, -1, -1, -1 };

    internal Iec61400ProtectionModule(Iec61400Protection p, double u0)
    {
        _p = p;
        Voltage = u0;
        FrequencyDeviation = 0.0;
        if (u0 >= p.Uover || u0 <= p.Uunder || 1.0 >= p.Fover || 1.0 <= p.Funder)
            throw new ArgumentException($"IEC 61400: the initial voltage {u0.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} pu or the nominal frequency "
                + "is already beyond a protection threshold; there is no quiet start.", nameof(u0));
    }

    /// <summary>The filtered protection voltage.</summary>
    internal double Voltage { get; private set; }

    /// <summary>The filtered frequency deviation (pu of nominal; the measured frequency is 1 plus this).</summary>
    internal double FrequencyDeviation { get; private set; }

    /// <summary>The rate-limited raw frequency deviation (the state before the filter).</summary>
    internal double LimitedDeviation { get; private set; }

    /// <summary>The cause of the trip, or null while connected.</summary>
    internal string? Trip { get; private set; }

    /// <summary>One step from the terminal voltage magnitude and the raw frequency deviation over the last step.</summary>
    internal void Step(double u, double rawDeviation, double dt)
    {
        Voltage = Iec61400Blocks.Lag(Voltage, u, _p.Tufilt, dt);
        LimitedDeviation = Iec61400Blocks.Ramp(LimitedDeviation, rawDeviation, dt, -_p.Dfmax, _p.Dfmax);
        FrequencyDeviation = Iec61400Blocks.Lag(FrequencyDeviation, LimitedDeviation, _p.Tffilt, dt);
        if (Trip is not null) return;
        double f = 1.0 + FrequencyDeviation;
        Check(0, Voltage >= _p.Uover, _p.TUover, Voltage, "over-voltage", dt);
        Check(1, Voltage <= _p.Uunder, _p.TUunder, Voltage, "under-voltage", dt);
        Check(2, f >= _p.Fover, _p.TFover, f, "over-frequency", dt);
        Check(3, f <= _p.Funder, _p.TFunder, f, "under-frequency", dt);
    }

    private void Check(int i, bool on, Iec61400Table table, double level, string what, double dt)
    {
        if (!on) { _count[i] = -1; return; }
        _count[i]++;
        if (Trip is null && _count[i] > Iec61400Blocks.StepsWithin(table.Evaluate(level), dt))
            Trip = $"{what} protection at level {level.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} after {_count[i].ToString(System.Globalization.CultureInfo.InvariantCulture)} steps";
    }
}
