namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// IEEEG1 - the IEEE type 1 steam turbine-governor (IEEE Committee Report, "Dynamic Models for Steam and Hydro Turbines
/// in Power System Studies", IEEE Trans. PAS-92, 1973, Fig. 1 and 5; the PSS/E IEEEG1 model), the standard steam-plant
/// model above the compact <see cref="Tgov1"/>. It has the governor phase-shaping lead-lag, a rate- and
/// position-limited valve, and the four-stage steam train (chest, reheater, crossover, second crossover) whose power
/// fractions tap the high-, intermediate- and low-pressure stages of a high-pressure (HP) shaft and, for a
/// cross-compound unit, a low-pressure (LP) shaft. A direct wiring of the common control blocks:
/// <code>
///   e      = -(1/R) * dw                                     (droop on speed deviation, K = 1/R)
///   s1     = leadlag_{T2/T1}(e)                              (governor phase shaping)
///   dPgv/dt = clamp((Pref + s1 - Pgv) / T3, Uc, Uo),          (valve servo: rate limited ...)
///   Pgv in [Pmin, Pmax]                                       (... and position limited, non-windup)
///   y4..y7 = cascade of lags T4, T5, T6, T7 from Pgv          (steam chest -> reheat -> crossover -> second crossover)
///   PmechHP = K1*y4 + K3*y5 + K5*y6 + K7*y7                   (HP shaft: the value Step returns)
///   PmechLP = K2*y4 + K4*y5 + K6*y6 + K8*y7                   (LP shaft: LowPressureShaftPowerPu)
/// </code>
/// All quantities are per-unit on the machine base. The eight fractions are fractions of the TOTAL turbine power and
/// must sum to 1 (the 1973 report's definition; checked in the constructor). With <c>Pref</c> at the valve power at
/// synchronous speed, <c>dw = 0</c> is an exact fixed point: the valve holds Pgv = Pmech0, the HP shaft carries
/// (K1 + K3 + K5 + K7) * Pmech0 and the LP shaft the rest. A tandem-compound unit (one shaft, the usual case and the
/// default K1 = 1) puts every stage on one machine: its shaft power is the sum of the two outputs, which is Pmech0 at
/// equilibrium. The steady droop of the total is <c>dPmech/dw = -1/R</c> in the unlimited region. It collapses onto a
/// single-stage governor when <c>K1 = 1</c> (all power from the chest stage) and <c>T2 = T1</c> (no phase shaping).
/// The dynamics are the exact-discretised <see cref="FirstOrderLag"/> and <see cref="LeadLag"/> blocks, plus the
/// exact piecewise solution of the rate-limited valve servo (below).
///
/// <para><b>R083 (October 2026 review, stream B3; Ruling R1: Mark delegated EE calls 2026-10-04).</b> Before, the model
/// had no valve opening / closing rate limits (Uo / Uc) and no LP-shaft fractions (K2 / K4 / K6 / K8), so IEEEG1 data
/// whose response is rate-limited (most published steam data: Uo about 0.1 pu/s, Uc about -1 pu/s) could not be
/// reproduced, and a data set with LP fractions silently lost that share of the power. Both are now in. With the new
/// arguments at their defaults (Uo = +infinity, Uc = -infinity, K2 = K4 = K6 = K8 = 0) every expression of the old model
/// runs unchanged, so every existing result is bit for bit what it was. A governor lag T1 of zero with no lead
/// (T1 = T2 = 0, common in PSS/E data) is the pure gain the transfer function then is, instead of an exception.</para>
/// </summary>
public sealed class Ieeeg1
{
    /// <summary>Tolerance on the sum of the eight stage fractions (they must sum to 1): absorbs the rounding of data
    /// written in decimal (0.3 + 0.3 + 0.2 + 0.2 is not exactly 1 in binary), never a real modelling difference.</summary>
    public const double FractionSumTolerance = 1e-6;

    private readonly double _rDroop;
    private readonly LeadLag? _governor;   // null: T1 = T2 = 0, the pure gain (no phase shaping)
    private readonly FirstOrderLag _valve;
    private readonly FirstOrderLag _chest, _reheat, _crossover, _lp;
    private readonly double _k1, _k3, _k5, _k7;
    private readonly double _k2, _k4, _k6, _k8;
    private readonly double _t3;
    private readonly double _uo, _uc;       // valve opening (> 0) and closing (< 0) rate limits, pu/s
    private readonly bool _rateLimited;     // true when either rate limit is finite
    private double _pgvRateLimited;         // the valve state when rate-limited (the FirstOrderLag carries it otherwise)
    private double _pref;

    /// <param name="droopPu">Permanent droop R (pu); must be &gt; 0 (the PSS/E gain K is 1/R).</param>
    /// <param name="governorLeadSeconds">Governor lead T2 (s), &gt;= 0.</param>
    /// <param name="governorLagSeconds">Governor lag T1 (s), &gt; 0; or 0 together with a zero lead (pure gain).</param>
    /// <param name="valveTimeSeconds">Valve/gate servo time constant T3 (s), 0 or above; 0 makes it instantaneous. A negative
    /// value is refused (bug hunt 2026-10-29, finding 26: the doc used to promise that a negative value was instant too, but the lag has refused negatives since GR-557).</param>
    /// <param name="chestSeconds">Steam-chest time constant T4 (s).</param>
    /// <param name="reheatSeconds">Reheater time constant T5 (s).</param>
    /// <param name="crossoverSeconds">Crossover time constant T6 (s).</param>
    /// <param name="lpSeconds">Second crossover/LP time constant T7 (s).</param>
    /// <param name="k1">HP-shaft fraction of the chest stage K1.</param>
    /// <param name="k3">HP-shaft fraction of the reheat stage K3.</param>
    /// <param name="k5">HP-shaft fraction of the crossover stage K5.</param>
    /// <param name="k7">HP-shaft fraction of the second crossover stage K7.</param>
    /// <param name="valveMinPu">Valve lower position limit Pmin (pu).</param>
    /// <param name="valveMaxPu">Valve upper position limit Pmax (pu).</param>
    /// <param name="k2">R083: LP-shaft fraction of the chest stage K2 (default 0).</param>
    /// <param name="k4">R083: LP-shaft fraction of the reheat stage K4 (default 0).</param>
    /// <param name="k6">R083: LP-shaft fraction of the crossover stage K6 (default 0).</param>
    /// <param name="k8">R083: LP-shaft fraction of the second crossover stage K8 (default 0).</param>
    /// <param name="valveOpeningRatePuPerS">R083: maximum valve opening rate Uo (pu/s, &gt; 0; default unlimited).</param>
    /// <param name="valveClosingRatePuPerS">R083: maximum valve closing rate Uc (pu/s, &lt; 0; default unlimited).</param>
    public Ieeeg1(double droopPu, double governorLeadSeconds, double governorLagSeconds, double valveTimeSeconds,
        double chestSeconds, double reheatSeconds, double crossoverSeconds, double lpSeconds,
        double k1 = 1.0, double k3 = 0.0, double k5 = 0.0, double k7 = 0.0,
        double valveMinPu = 0.0, double valveMaxPu = double.PositiveInfinity,
        double k2 = 0.0, double k4 = 0.0, double k6 = 0.0, double k8 = 0.0,
        double valveOpeningRatePuPerS = double.PositiveInfinity, double valveClosingRatePuPerS = double.NegativeInfinity)
    {
        if (droopPu <= 0.0) throw new System.ArgumentOutOfRangeException(nameof(droopPu), "Droop R must be > 0.");
        _rDroop = droopPu;

        // T1 = T2 = 0 (no phase shaping) is the pure gain K; a lead with no lag is improper and stays refused by LeadLag.
        bool noShaping = governorLagSeconds <= ControlBlocks.InstantThresholdSeconds
            && governorLeadSeconds >= 0.0 && governorLeadSeconds <= ControlBlocks.InstantThresholdSeconds;
        _governor = noShaping ? null : new LeadLag(governorLeadSeconds, governorLagSeconds);

        if (!(valveTimeSeconds >= 0.0) || !double.IsFinite(valveTimeSeconds))
            throw new System.ArgumentOutOfRangeException(nameof(valveTimeSeconds), valveTimeSeconds, "T3 must be a finite number of seconds, 0 or above; 0 makes the block instantaneous (a negative \"disabled\" marker is not accepted).");
        _valve = new FirstOrderLag(gain: 1.0, timeConstantSeconds: valveTimeSeconds, min: valveMinPu, max: valveMaxPu);
        _t3 = valveTimeSeconds;
        _chest = new FirstOrderLag(1.0, chestSeconds);
        _reheat = new FirstOrderLag(1.0, reheatSeconds);
        _crossover = new FirstOrderLag(1.0, crossoverSeconds);
        _lp = new FirstOrderLag(1.0, lpSeconds);

        // R083: the stage fractions are fractions of the total turbine power: each in [0, 1] and all eight summing to 1.
        double[] ks = { k1, k2, k3, k4, k5, k6, k7, k8 };
        string[] names = { nameof(k1), nameof(k2), nameof(k3), nameof(k4), nameof(k5), nameof(k6), nameof(k7), nameof(k8) };
        double sum = 0.0;
        for (int i = 0; i < ks.Length; i++)
        {
            if (!(ks[i] >= 0.0 && ks[i] <= 1.0))
                throw new System.ArgumentOutOfRangeException(names[i], ks[i], $"IEEEG1: the stage fraction {names[i].ToUpperInvariant()} must lie in [0, 1].");
            sum += ks[i];
        }
        if (!(System.Math.Abs(sum - 1.0) <= FractionSumTolerance))
            throw new System.ArgumentException($"IEEEG1: the stage fractions K1..K8 are fractions of the total turbine power and must "
                + $"sum to 1 (got {ControlBlocks.Num(sum)}).", nameof(k1));
        _k1 = k1; _k3 = k3; _k5 = k5; _k7 = k7;
        _k2 = k2; _k4 = k4; _k6 = k6; _k8 = k8;

        // R083: valve rate limits. NaN is refused; Uo must open (> 0) and Uc must close (< 0).
        if (!(valveOpeningRatePuPerS > 0.0))
            throw new System.ArgumentOutOfRangeException(nameof(valveOpeningRatePuPerS), valveOpeningRatePuPerS,
                "IEEEG1: the valve opening rate limit Uo must be > 0 pu/s (positive infinity for none).");
        if (!(valveClosingRatePuPerS < 0.0))
            throw new System.ArgumentOutOfRangeException(nameof(valveClosingRatePuPerS), valveClosingRatePuPerS,
                "IEEEG1: the valve closing rate limit Uc must be < 0 pu/s (negative infinity for none).");
        _uo = valveOpeningRatePuPerS;
        _uc = valveClosingRatePuPerS;
        _rateLimited = !double.IsInfinity(_uo) || !double.IsInfinity(_uc);
    }

    /// <summary>Mechanical valve/gate position Pgv (pu) after the last step.</summary>
    public double ValvePosition => _rateLimited ? _pgvRateLimited : _valve.Output;

    /// <summary>R083: the LP-shaft output <c>K2*y4 + K4*y5 + K6*y6 + K8*y7</c> (pu) after the last step (0 with the
    /// default fractions). For a tandem-compound unit (one shaft) the machine's mechanical power is the sum of
    /// <see cref="Step"/>'s return value and this.</summary>
    public double LowPressureShaftPowerPu { get; private set; }

    /// <summary>R083: the sum K1 + K3 + K5 + K7, the share of the total power on the HP shaft (<see cref="Step"/>).</summary>
    public double HighPressureShaftFraction => _k1 + _k3 + _k5 + _k7;

    /// <summary>Set the load reference and pre-load every state so <c>dw = 0</c> holds valve (total turbine) power
    /// <paramref name="mechanicalPowerPu"/> as an exact equilibrium: Pgv = Pmech0 and every steam stage at Pmech0. The
    /// HP shaft then carries (K1 + K3 + K5 + K7) * Pmech0, the LP shaft the rest; with the default K1 = 1 all of it.</summary>
    public void Initialize(double mechanicalPowerPu)
    {
        // R252 (October 2026, stream B3): the valve sits at Pgv = Pmech0 at equilibrium (the stage fractions sum to 1),
        // so Pmech0 must lie inside [Pmin, Pmax]; out of limits is refused instead of clamped silently.
        ControlBlocks.RequireEquilibriumWithinLimits("IEEEG1", "valve position Pgv", mechanicalPowerPu,
            _valve.Min, _valve.Max, nameof(mechanicalPowerPu));
        _pref = mechanicalPowerPu;
        _governor?.Initialize(0.0);
        _valve.Initialize(mechanicalPowerPu);
        _pgvRateLimited = mechanicalPowerPu;
        _chest.Initialize(mechanicalPowerPu);
        _reheat.Initialize(mechanicalPowerPu);
        _crossover.Initialize(mechanicalPowerPu);
        _lp.Initialize(mechanicalPowerPu);
        LowPressureShaftPowerPu = _k2 * mechanicalPowerPu + _k4 * mechanicalPowerPu + _k6 * mechanicalPowerPu + _k8 * mechanicalPowerPu;
    }

    /// <summary>R246 (October 2026, stream B3): change the load reference Pref to <paramref name="referencePu"/> without
    /// touching any state, so a set-point step after <see cref="Initialize"/> travels through the valve (T3) and the
    /// steam train (T4..T7) instead of jumping. Finite values only; not limited (the valve limits act downstream).</summary>
    public void SetReference(double referencePu)
    {
        if (!double.IsFinite(referencePu))
            throw new System.ArgumentOutOfRangeException(nameof(referencePu), referencePu, "IEEEG1: the load reference must be a finite number.");
        _pref = referencePu;
    }

    /// <summary>Advance one step of <paramref name="dtSeconds"/> with speed deviation
    /// <paramref name="speedDeviationPu"/> (pu, held across the step) and return the HP-shaft mechanical power (pu):
    /// the whole mechanical power with the default fractions. The LP-shaft power of the same step is
    /// <see cref="LowPressureShaftPowerPu"/>.</summary>
    public double Step(double speedDeviationPu, double dtSeconds)
    {
        double e = -speedDeviationPu / _rDroop;
        double s1 = _governor is null ? e : _governor.Step(e, dtSeconds);
        double pgv = _rateLimited ? RateLimitedValveStep(_pref + s1, dtSeconds) : _valve.Step(_pref + s1, dtSeconds);
        double y4 = _chest.Step(pgv, dtSeconds);
        double y5 = _reheat.Step(y4, dtSeconds);
        double y6 = _crossover.Step(y5, dtSeconds);
        double y7 = _lp.Step(y6, dtSeconds);
        LowPressureShaftPowerPu = _k2 * y4 + _k4 * y5 + _k6 * y6 + _k8 * y7;
        return _k1 * y4 + _k3 * y5 + _k5 * y6 + _k7 * y7;
    }

    /// <summary>
    /// R083: one step of the rate- and position-limited valve servo <c>dx/dt = clamp((u - x) / T3, Uc, Uo)</c> with the
    /// command u held across the step, solved EXACTLY (no Euler error):
    /// <list type="bullet">
    /// <item>if the start-of-step rate (u - x) / T3 lies within [Uc, Uo], it stays there for the whole step (the rate of
    /// a first-order lag only falls in magnitude as x approaches u), so the step is the exact zero-order hold
    /// <c>x' = a x + (1 - a) u</c>, <c>a = exp(-dt / T3)</c>, the same expression <see cref="FirstOrderLag"/> uses;</item>
    /// <item>if the rate starts above Uo, x ramps at Uo until the lag's own rate falls to Uo, at x1 = u - Uo T3, after
    /// t1 = (x1 - x) / Uo; if t1 is beyond the step the whole step is the ramp, else the ramp to x1 and then the exact
    /// lag for the remaining dt - t1 (symmetrically below Uc);</item>
    /// <item>with T3 at or below the instant threshold the servo has no lag: x moves straight to u, as far as the rate
    /// limits allow in one step.</item>
    /// </list>
    /// The result is then clamped to [Pmin, Pmax] (the non-windup position limit, as in the unlimited path).
    /// </summary>
    private double RateLimitedValveStep(double u, double dt)
    {
        double x = _pgvRateLimited;
        double next;
        if (_t3 <= ControlBlocks.InstantThresholdSeconds)
        {
            next = System.Math.Clamp(u, x + _uc * dt, x + _uo * dt);
        }
        else
        {
            double rate = (u - x) / _t3;
            if (rate > _uo)
            {
                double x1 = u - _uo * _t3;
                double t1 = (x1 - x) / _uo;
                next = t1 >= dt ? x + _uo * dt : LagFrom(x1, u, dt - t1);
            }
            else if (rate < _uc)
            {
                double x1 = u - _uc * _t3;
                double t1 = (x1 - x) / _uc;
                next = t1 >= dt ? x + _uc * dt : LagFrom(x1, u, dt - t1);
            }
            else
            {
                next = LagFrom(x, u, dt);
            }
        }
        _pgvRateLimited = System.Math.Clamp(next, _valve.Min, _valve.Max);
        return _pgvRateLimited;
    }

    /// <summary>The exact zero-order-hold lag step from <paramref name="x"/> toward <paramref name="u"/> over
    /// <paramref name="seconds"/>, the same expression as <see cref="FirstOrderLag.Step"/>.</summary>
    private double LagFrom(double x, double u, double seconds)
    {
        double a = DetMath.Exp(-seconds / _t3);
        return a * x + (1.0 - a) * u;
    }
}
