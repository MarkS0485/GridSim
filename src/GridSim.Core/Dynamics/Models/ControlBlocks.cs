namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// The common block-diagram integrator: the standard control-system transfer-function blocks that every
/// IEEE excitation / turbine-governor / PSS standard model is wired from. This is the foundation of the
/// standard dynamic-model library (P0-1) - a machine model, an AVR and a governor are each a small wiring
/// of these primitives, so validating the blocks once, hard, de-risks every model built on them.
///
/// The linear dynamic blocks (<see cref="FirstOrderLag"/>, <see cref="LeadLag"/>, <see cref="Washout"/>)
/// integrate by the <b>exact zero-order-hold</b> discretisation of their pole, not by an Euler/RK step: for
/// an input held constant across the step the state advance <c>x <- a*x + (1-a)*K*u</c> with
/// <c>a = exp(-dt/T)</c> is the closed-form solution of the block's ODE, so a step response reproduces the
/// analytic <c>K(1-e^{-t/T})</c> to machine epsilon at every sample and is unconditionally A-stable at any
/// step. The pure <see cref="Integrator"/> (a marginally-stable pole at the origin) advances by the
/// forward-Euler rule <c>x <- x + K*u*dt</c>, which is likewise exact for a held input (a ramp). Every block
/// is a pure function of (state, input, dt) with no shared or static state, so a study is bit-for-bit
/// reproducible run to run, matching the whole-app determinism contract (technical manual ch. 11).
///
/// Two structural degeneracies are exact by construction and are pinned as reduction identities (the same
/// discipline used across GridSim - collapse the general model onto a known one and demand field-for-field
/// agreement): a <see cref="LeadLag"/> with equal numerator and denominator time constants is an exact
/// pass-through, and a <see cref="FirstOrderLag"/> with zero time constant is an exact gain.
/// </summary>
public static class ControlBlocks
{
    /// <summary>The largest time constant treated as instantaneous (seconds); at or below it a lag/lead-lag
    /// collapses to its algebraic gain rather than integrating an ill-conditioned pole.</summary>
    internal const double InstantThresholdSeconds = 1e-12;

    /// <summary>
    /// R252 (October 2026 review, stream B3; Ruling R1: Mark delegated EE calls 2026-10-04): the fail-loud gate every
    /// standard model's <c>Initialize</c> runs before it pre-loads a limited block. A model documents that
    /// <c>Initialize(x)</c> makes <c>x</c> an EXACT equilibrium. The limited blocks (<see cref="FirstOrderLag"/>,
    /// <see cref="Integrator"/>) clamp their preset state to their limits, while the blocks downstream of them are
    /// preset with the unclamped value, so a requested equilibrium outside the limits used to give a start that is not
    /// an equilibrium at all, with no error (a motoring unit with a negative mechanical power against the default
    /// valve floor of 0, or a field voltage above the exciter ceiling): the run moved from t = 0 with nothing to say
    /// why. PSS/E refuses such a case at initialisation (an initial condition outside a model's limits is reported by
    /// the dynamic initialisation, not run), and GridSim now does the same: the requested value must be finite and
    /// inside [min, max] (both inclusive, so a unit starting exactly at a limit is still an equilibrium), else this
    /// throws, naming the model, the quantity, the value and the limits. A value inside the limits is untouched, so
    /// every in-limits initialisation is bit for bit what it was.
    /// </summary>
    /// <param name="model">The model name for the message ("TGOV1", "SEXS", ...).</param>
    /// <param name="quantity">The limited quantity in words ("valve position Pgv", "field voltage Efd", ...).</param>
    /// <param name="value">The value the equilibrium needs that quantity to take.</param>
    /// <param name="min">The block's lower limit.</param>
    /// <param name="max">The block's upper limit.</param>
    /// <param name="paramName">The Initialize argument the value derives from (for the exception).</param>
    internal static void RequireEquilibriumWithinLimits(string model, string quantity, double value, double min, double max,
        string paramName)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(paramName, value,
                $"{model}: the initial {quantity} = {Num(value)} is not a finite number, so there is no equilibrium to start from.");
        if (!(value >= min && value <= max))
            throw new ArgumentOutOfRangeException(paramName, value,
                $"{model}: the requested equilibrium needs {quantity} = {Num(value)} pu, outside the limits [{Num(min)}, {Num(max)}]. "
                + "Starting there would clamp the state and the run would not begin at equilibrium (R252); widen the "
                + "limits or start from an operating point the model can hold.");
    }

    /// <summary>A number for a message: round-trip digits, invariant culture, so an unlimited side reads "Infinity" /
    /// "-Infinity" in plain ASCII on every machine (the current culture may print the infinity sign).</summary>
    internal static string Num(double x) => x.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// First-order lag <c>K / (1 + sT)</c> with optional non-windup output limits - the workhorse block (an
/// AVR forward gain, a governor valve/steam bowl, a measurement transducer). The state is the output
/// <c>x</c>; its ODE is <c>T dx/dt = K u - x</c>, advanced by the exact zero-order hold. When limits are
/// set the state is clamped to <c>[min, max]</c> each step - a non-windup position limit, so the output
/// leaves the limit the instant the input reverses, with no wound-up integral to unload. A time constant
/// at or below <see cref="ControlBlocks.InstantThresholdSeconds"/> makes the block the pure gain
/// <c>y = K u</c>.
/// </summary>
public sealed class FirstOrderLag
{
    private readonly double _k;
    private readonly double _t;
    private readonly double _min;
    private readonly double _max;
    private double _x;

    /// <param name="gain">Steady-state (DC) gain K.</param>
    /// <param name="timeConstantSeconds">Lag time constant T (s), 0 or above; 0 (up to <see cref="ControlBlocks.InstantThresholdSeconds"/>)
    /// makes the block a pure gain. A negative or non-finite value is refused (GR-557).</param>
    /// <param name="min">Non-windup lower output limit (default none).</param>
    /// <param name="max">Non-windup upper output limit (default none).</param>
    public FirstOrderLag(double gain, double timeConstantSeconds,
        double min = double.NegativeInfinity, double max = double.PositiveInfinity)
    {
        if (max < min) throw new ArgumentException("max must be >= min.", nameof(max));
        // GR-557: a negative or NaN time constant used to fall into the "instant" branch (a NaN in no branch at all once
        // compared), silently; 0 (and anything up to InstantThresholdSeconds) is the deliberate instant lag.
        if (!(timeConstantSeconds >= 0) || double.IsInfinity(timeConstantSeconds))
            throw new ArgumentOutOfRangeException(nameof(timeConstantSeconds), timeConstantSeconds, "A lag time constant must be a finite number of seconds, 0 or above.");
        if (!double.IsFinite(gain))
            throw new ArgumentOutOfRangeException(nameof(gain), gain, "A lag gain must be a finite number.");
        _k = gain;
        _t = timeConstantSeconds;
        _min = min;
        _max = max;
    }

    /// <summary>Current output.</summary>
    public double Output => _x;

    /// <summary>The lower output limit (negative infinity when none).</summary>
    public double Min => _min;

    /// <summary>The upper output limit (positive infinity when none).</summary>
    public double Max => _max;

    /// <summary>Preset the state to a known output (an equilibrium initialisation), clamped to the limits. The block is
    /// a primitive and keeps clamping; the standard models check their value against <see cref="Min"/> /
    /// <see cref="Max"/> first and refuse an out-of-limits equilibrium (R252,
    /// <see cref="ControlBlocks.RequireEquilibriumWithinLimits"/>).</summary>
    public void Initialize(double output) => _x = Math.Clamp(output, _min, _max);

    /// <summary>Advance one step of <paramref name="dtSeconds"/> with input <paramref name="input"/> held
    /// across the step, and return the new output.</summary>
    public double Step(double input, double dtSeconds)
    {
        double target = _k * input;
        if (_t <= ControlBlocks.InstantThresholdSeconds)
            _x = target;
        else
        {
            double a = DetMath.Exp(-dtSeconds / _t);
            _x = a * _x + (1.0 - a) * target;
        }
        _x = Math.Clamp(_x, _min, _max);
        return _x;
    }
}

/// <summary>
/// Lead-lag compensator <c>(1 + sTa) / (1 + sTb)</c>, unity DC gain - the phase-shaping block of PSS and
/// AVR transient-gain-reduction stages. Realised as a direct feedthrough plus an internal lag:
/// <c>y = c*u + xL</c> where <c>c = Ta/Tb</c> and <c>xL</c> is the lag <c>(1-c)/(1+sTb)</c> of the input,
/// integrated by the same exact zero-order hold as <see cref="FirstOrderLag"/>. The denominator time
/// constant Tb must be positive. When <c>Ta == Tb</c> the feedthrough coefficient is exactly 1 and the
/// internal-lag gain exactly 0, so the block is a bit-exact pass-through (<c>y = u</c>) - a reduction
/// identity, not an approximation.
/// </summary>
public sealed class LeadLag
{
    private readonly double _c;      // Ta/Tb feedthrough
    private readonly double _tb;
    private double _xL;              // internal lag state, gain (1-c), time Tb

    /// <param name="leadSeconds">Numerator (lead) time constant Ta (s), &gt;= 0.</param>
    /// <param name="lagSeconds">Denominator (lag) time constant Tb (s), &gt; 0.</param>
    public LeadLag(double leadSeconds, double lagSeconds)
    {
        if (leadSeconds < 0.0) throw new ArgumentOutOfRangeException(nameof(leadSeconds), "Lead time constant must be >= 0.");
        if (lagSeconds <= 0.0) throw new ArgumentOutOfRangeException(nameof(lagSeconds), "Lag time constant must be > 0.");
        _c = leadSeconds / lagSeconds;
        _tb = lagSeconds;
    }

    /// <summary>Current output for a given input (feedthrough plus internal-lag state).</summary>
    public double OutputFor(double input) => _c * input + _xL;

    /// <summary>Preset the internal state so a held input <paramref name="input"/> is in equilibrium
    /// (output equals the input, since the DC gain is unity).</summary>
    public void Initialize(double input) => _xL = (1.0 - _c) * input;

    /// <summary>Advance one step with <paramref name="input"/> held across it, and return the new output.</summary>
    public double Step(double input, double dtSeconds)
    {
        double a = DetMath.Exp(-dtSeconds / _tb);
        _xL = a * _xL + (1.0 - a) * (1.0 - _c) * input;
        return _c * input + _xL;
    }
}

/// <summary>
/// Washout (high-pass) <c>sT / (1 + sT)</c> - the derivative-with-memory block at the front of every PSS,
/// so a sustained (DC) input produces no steady output and only the change is passed. Realised as
/// <c>y = u - xL</c> where <c>xL</c> is the unity-gain lag of the input, integrated by the exact
/// zero-order hold. In steady state <c>xL -> u</c> so the output decays to exactly zero. The time constant
/// must be positive.
/// </summary>
public sealed class Washout
{
    private readonly double _t;
    private double _xL;

    /// <param name="timeConstantSeconds">Washout time constant T (s), &gt; 0.</param>
    public Washout(double timeConstantSeconds)
    {
        if (timeConstantSeconds <= 0.0) throw new ArgumentOutOfRangeException(nameof(timeConstantSeconds), "Washout time constant must be > 0.");
        _t = timeConstantSeconds;
    }

    /// <summary>Current output for a given input.</summary>
    public double OutputFor(double input) => input - _xL;

    /// <summary>The internal lag state xL (the output is input - xL). Read by a model that solves an algebraic loop
    /// through the washout exactly, with the state held across the step.</summary>
    public double State => _xL;

    /// <summary>Preset the internal state so a held input is in equilibrium (output zero).</summary>
    public void Initialize(double input) => _xL = input;

    /// <summary>Advance one step with <paramref name="input"/> held across it, and return the new output.</summary>
    public double Step(double input, double dtSeconds)
    {
        double a = DetMath.Exp(-dtSeconds / _t);
        _xL = a * _xL + (1.0 - a) * input;
        return input - _xL;
    }
}

/// <summary>
/// Pure integrator <c>K / s</c> with optional non-windup limits - the integral path of a PI regulator, an
/// over/under-excitation timer, a valve position. The state is the output; <c>dx/dt = K u</c>, advanced by
/// forward Euler (<c>x <- x + K u dt</c>), which is exact for an input held across the step (a ramp).
/// Clamping the state itself to <c>[min, max]</c> makes the limit non-windup: the output cannot integrate
/// past the limit, so it releases immediately when the input reverses.
/// </summary>
public sealed class Integrator
{
    private readonly double _k;
    private readonly double _min;
    private readonly double _max;
    private double _x;

    /// <param name="gain">Integrator gain K (s^-1).</param>
    /// <param name="min">Non-windup lower limit (default none).</param>
    /// <param name="max">Non-windup upper limit (default none).</param>
    public Integrator(double gain, double min = double.NegativeInfinity, double max = double.PositiveInfinity)
    {
        if (max < min) throw new ArgumentException("max must be >= min.", nameof(max));
        _k = gain;
        _min = min;
        _max = max;
    }

    /// <summary>Current output.</summary>
    public double Output => _x;

    /// <summary>Preset the state to a known output, clamped to the limits.</summary>
    public void Initialize(double output) => _x = Math.Clamp(output, _min, _max);

    /// <summary>Advance one step with <paramref name="input"/> held across it, and return the new output.</summary>
    public double Step(double input, double dtSeconds)
    {
        _x = Math.Clamp(_x + _k * input * dtSeconds, _min, _max);
        return _x;
    }
}

/// <summary>
/// Proportional-integral regulator <c>Kp + Ki/s</c> with a non-windup limit on the integral state - the
/// core of a bus-voltage or governor load regulator. The proportional path is a direct feedthrough; the
/// integral path is an <see cref="Integrator"/> whose non-windup limits stop the integral winding up while
/// the plant is against a limit. The integral limits default to the (optional) overall output limits.
/// </summary>
public sealed class PiController
{
    private readonly double _kp;
    private readonly Integrator _integral;
    private readonly double _outMin;
    private readonly double _outMax;

    /// <param name="proportionalGain">Kp.</param>
    /// <param name="integralGain">Ki (s^-1).</param>
    /// <param name="min">Non-windup lower limit on the integral state and the output (default none).</param>
    /// <param name="max">Non-windup upper limit on the integral state and the output (default none).</param>
    public PiController(double proportionalGain, double integralGain,
        double min = double.NegativeInfinity, double max = double.PositiveInfinity)
    {
        if (max < min) throw new ArgumentException("max must be >= min.", nameof(max));
        _kp = proportionalGain;
        _outMin = min;
        _outMax = max;
        _integral = new Integrator(integralGain, min, max);
    }

    /// <summary>Preset the integral state so a held input <paramref name="input"/> yields output
    /// <paramref name="output"/> in equilibrium.</summary>
    public void Initialize(double input, double output) => _integral.Initialize(output - _kp * input);

    /// <summary>Advance one step with <paramref name="input"/> held across it, and return the output.</summary>
    public double Step(double input, double dtSeconds)
        => Math.Clamp(_kp * input + _integral.Step(input, dtSeconds), _outMin, _outMax);
}
