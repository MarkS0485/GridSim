namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// GAST - the standard simple gas-turbine governor (PSS/E GAST; CGMES GovGAST), in GridSim's turbine-governor library
/// beside <see cref="Tgov1"/>. Data R, T1, T2, T3, AT, KT, VMAX, VMIN, DTURB (the PSS/E order). The block diagram, as
/// published for PSS/E GAST and reproduced by NEPLAN's turbine-governor reference and the ANDES GAST model:
/// <code>
///   pd     = Pref - dw/R                           (load reference less the droop on the speed deviation)
///   limit  = AT + KT*(AT - x3)                     (exhaust temperature load limit)
///   u      = min(pd, limit)                        (low value gate)
///   x1     = lag_{T1, [VMIN, VMAX]}(u)             (fuel valve, non-windup position limits)
///   x2     = lag_{T2}(x1)                          (turbine power)
///   x3     = lag_{T3}(x2)                          (exhaust temperature measurement)
///   Pmech  = x2 - DTURB*dw
/// </code>
/// All quantities are per-unit on the machine base. Each step the gate reads the exhaust temperature state x3 of the
/// START of the step (the measurement the limit sees while the step's input is held), then the three lags advance in
/// signal order with the exact zero-order hold of <see cref="FirstOrderLag"/>, each seeing its upstream block's new
/// output (the same sequencing as <see cref="Tgov1"/>). With Pref set to the initial mechanical power, dw = 0 is an exact
/// equilibrium provided the temperature limit is not binding there: at equilibrium x1 = x2 = x3 = Pmech0, so the limit
/// is AT + KT*(AT - Pmech0), which is at least Pmech0 exactly when Pmech0 &lt;= AT (KT &gt;= 0). In steady state on the
/// temperature limit x = AT + KT*(AT - x), so the turbine settles at AT whatever KT: AT is the most power the ambient
/// temperature allows, and Pmech0 above it cannot be an equilibrium (refused, as R252 refuses any start outside a limit).
/// </summary>
public sealed class Gast : IRmsGovernor
{
    private readonly double _r;
    private readonly double _at;
    private readonly double _kt;
    private readonly double _dturb;
    private readonly FirstOrderLag _valve;
    private readonly FirstOrderLag _turbine;
    private readonly FirstOrderLag _exhaust;
    private double _pref;

    /// <param name="droopPu">Permanent droop R (pu), above 0.</param>
    /// <param name="valveTimeSeconds">Governor mechanism (fuel valve) time constant T1 (s), 0 or above.</param>
    /// <param name="turbineTimeSeconds">Turbine power time constant T2 (s), 0 or above.</param>
    /// <param name="exhaustTimeSeconds">Exhaust temperature time constant T3 (s), 0 or above.</param>
    /// <param name="ambientLimitPu">Ambient temperature load limit AT (pu), finite.</param>
    /// <param name="temperatureGainPu">Temperature limiter gain KT, 0 or above.</param>
    /// <param name="valveMinPu">Minimum valve (turbine power) VMIN (pu).</param>
    /// <param name="valveMaxPu">Maximum valve (turbine power) VMAX (pu), at or above VMIN.</param>
    /// <param name="turbineDampingPu">Turbine damping DTURB (pu), finite.</param>
    public Gast(double droopPu, double valveTimeSeconds, double turbineTimeSeconds, double exhaustTimeSeconds,
        double ambientLimitPu, double temperatureGainPu, double valveMinPu = 0.0, double valveMaxPu = double.PositiveInfinity,
        double turbineDampingPu = 0.0)
    {
        if (!(droopPu > 0.0) || !double.IsFinite(droopPu)) throw new ArgumentOutOfRangeException(nameof(droopPu), droopPu, "GAST: the droop R must be a finite number above 0.");
        if (!double.IsFinite(ambientLimitPu)) throw new ArgumentOutOfRangeException(nameof(ambientLimitPu), ambientLimitPu, "GAST: the ambient temperature load limit AT must be finite.");
        if (!(temperatureGainPu >= 0.0) || !double.IsFinite(temperatureGainPu)) throw new ArgumentOutOfRangeException(nameof(temperatureGainPu), temperatureGainPu, "GAST: the temperature limiter gain KT must be a finite number, 0 or above.");
        if (!double.IsFinite(turbineDampingPu)) throw new ArgumentOutOfRangeException(nameof(turbineDampingPu), turbineDampingPu, "GAST: the turbine damping DTURB must be finite.");
        if (double.IsNaN(valveMinPu) || double.IsNaN(valveMaxPu) || !(valveMaxPu >= valveMinPu))
            throw new ArgumentException($"GAST needs VMAX >= VMIN (got VMIN = {valveMinPu}, VMAX = {valveMaxPu}).", nameof(valveMaxPu));
        _r = droopPu;
        _at = ambientLimitPu;
        _kt = temperatureGainPu;
        _dturb = turbineDampingPu;
        _valve = new FirstOrderLag(1.0, valveTimeSeconds, valveMinPu, valveMaxPu);
        _turbine = new FirstOrderLag(1.0, turbineTimeSeconds);
        _exhaust = new FirstOrderLag(1.0, exhaustTimeSeconds);
    }

    /// <summary>Fuel valve position x1 (pu) after the last step.</summary>
    public double ValvePosition => _valve.Output;

    /// <summary>Exhaust temperature state x3 (pu) after the last step.</summary>
    public double ExhaustState => _exhaust.Output;

    /// <summary>The temperature load limit AT + KT*(AT - x3) the gate will see on the next step (pu).</summary>
    public double TemperatureLimit => _at + _kt * (_at - _exhaust.Output);

    /// <summary>
    /// Set the load reference to <paramref name="mechanicalPowerPu"/> and pre-load every state at the equilibrium that
    /// holds it at rated speed. Throws <see cref="ArgumentOutOfRangeException"/> when that power is outside the valve
    /// limits or above AT (the temperature limit would bind, so the start would not be an equilibrium).
    /// </summary>
    public void Initialize(double mechanicalPowerPu)
    {
        ControlBlocks.RequireEquilibriumWithinLimits("GAST", "valve position", mechanicalPowerPu, _valve.Min, _valve.Max, nameof(mechanicalPowerPu));
        if (mechanicalPowerPu > _at)
            throw new ArgumentOutOfRangeException(nameof(mechanicalPowerPu), mechanicalPowerPu,
                $"GAST: the initial mechanical power {mechanicalPowerPu} pu is above the ambient temperature load limit AT = {_at} pu, so the temperature limit binds and the unit cannot start at equilibrium.");
        _pref = mechanicalPowerPu;
        _valve.Initialize(mechanicalPowerPu);
        _turbine.Initialize(mechanicalPowerPu);
        _exhaust.Initialize(mechanicalPowerPu);
    }

    /// <summary>Change the load reference Pref WITHOUT touching any state (as <see cref="Tgov1.SetReference"/>).</summary>
    public void SetReference(double referencePu)
    {
        if (!double.IsFinite(referencePu))
            throw new ArgumentOutOfRangeException(nameof(referencePu), referencePu, "GAST: the load reference must be a finite number.");
        _pref = referencePu;
    }

    /// <summary>Advance one step of <paramref name="dtSeconds"/> with speed deviation <paramref name="speedDeviationPu"/>
    /// (pu, held across the step) and return the mechanical power (pu, machine base).</summary>
    public double Step(double speedDeviationPu, double dtSeconds)
    {
        double pd = _pref - speedDeviationPu / _r;
        double limit = _at + _kt * (_at - _exhaust.Output);
        double u = Math.Min(pd, limit);
        double x1 = _valve.Step(u, dtSeconds);
        double x2 = _turbine.Step(x1, dtSeconds);
        _exhaust.Step(x2, dtSeconds);
        return x2 - _dturb * speedDeviationPu;
    }
}
