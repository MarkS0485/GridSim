namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// TGOV1 - the standard steam turbine-governor, the simplest of the IEEE governor family and the first
/// entry in GridSim's standard turbine-governor library. It maps rotor speed deviation to mechanical power
/// and is a direct wiring of the common control blocks: a droop gain <c>1/R</c>, a valve/steam-bowl lag
/// <c>1/(1+sT1)</c> with non-windup position limits <c>[Vmin, Vmax]</c>, a reheat lead-lag
/// <c>(1+sT2)/(1+sT3)</c>, and a turbine-damping term <c>Dt*dw</c>:
/// <code>
///   valveCmd = Pref - dw/R
///   Pgv      = lag_{T1, [Vmin,Vmax]}(valveCmd)          (mechanical valve/gate position)
///   Pt       = leadlag_{T2/T3}(Pgv)                       (turbine power after reheat shaping)
///   Pmech    = Pt - Dt*dw
/// </code>
/// All quantities are per-unit on the machine base. With <c>Pref</c> set to the mechanical power at
/// synchronous speed, <c>dw = 0</c> is an exact fixed point (the undisturbed run is stationary, matching
/// the classical-model initialisation), and the steady droop is <c>dPmech/dw = -1/R - Dt</c> in the
/// unlimited region. The model owns no numerics of its own beyond this arithmetic - the dynamics are the
/// exact-discretised <see cref="FirstOrderLag"/> and <see cref="LeadLag"/> blocks.
/// </summary>
public sealed class Tgov1 : IRmsGovernor
{
    private readonly double _rDroop;
    private readonly double _dt;
    private readonly FirstOrderLag _valve;
    private readonly LeadLag _reheat;
    private double _pref;

    /// <param name="droopPu">Permanent droop R (pu); must be &gt; 0.</param>
    /// <param name="valveTimeSeconds">Governor/valve lag T1 (s), 0 or above; 0 makes the valve instantaneous. A negative value
    /// is refused (bug hunt 2026-10-29, finding 26: the doc used to promise that a negative value was instant too, but the lag has refused negatives since GR-557).</param>
    /// <param name="reheatLeadSeconds">Turbine lead time constant T2 (s), &gt;= 0.</param>
    /// <param name="reheatLagSeconds">Turbine lag time constant T3 (s), &gt; 0.</param>
    /// <param name="turbineDampingPu">Turbine damping Dt (pu).</param>
    /// <param name="valveMinPu">Valve/gate lower position limit Vmin (pu).</param>
    /// <param name="valveMaxPu">Valve/gate upper position limit Vmax (pu).</param>
    public Tgov1(double droopPu, double valveTimeSeconds, double reheatLeadSeconds, double reheatLagSeconds,
        double turbineDampingPu = 0.0, double valveMinPu = 0.0, double valveMaxPu = double.PositiveInfinity)
    {
        if (droopPu <= 0.0) throw new ArgumentOutOfRangeException(nameof(droopPu), "Droop R must be > 0.");
        _rDroop = droopPu;
        _dt = turbineDampingPu;
        if (!(valveTimeSeconds >= 0.0) || !double.IsFinite(valveTimeSeconds))
            throw new ArgumentOutOfRangeException(nameof(valveTimeSeconds), valveTimeSeconds, "T1 must be a finite number of seconds, 0 or above; 0 makes the block instantaneous (a negative \"disabled\" marker is not accepted).");
        _valve = new FirstOrderLag(gain: 1.0, timeConstantSeconds: valveTimeSeconds, min: valveMinPu, max: valveMaxPu);
        _reheat = new LeadLag(reheatLeadSeconds, reheatLagSeconds);
    }

    /// <summary>Mechanical valve/gate position Pgv (pu) after the last step.</summary>
    public double ValvePosition => _valve.Output;

    /// <summary>
    /// Set the load reference and pre-load every state so that <c>dw = 0</c> holds mechanical power
    /// <paramref name="mechanicalPowerPu"/> as an exact equilibrium.
    /// <para>R252 (October 2026, stream B3): at equilibrium the valve sits at Pgv = Pmech0, so Pmech0 must lie inside
    /// [Vmin, Vmax]; otherwise this throws <see cref="ArgumentOutOfRangeException"/> (the valve used to be clamped
    /// silently while the reheat stage was preset at the unclamped value, a start that is not an equilibrium). A
    /// motoring or pumping unit (Pmech0 &lt; 0) therefore needs a valve floor below its operating point.</para>
    /// </summary>
    public void Initialize(double mechanicalPowerPu)
    {
        ControlBlocks.RequireEquilibriumWithinLimits("TGOV1", "valve position Pgv", mechanicalPowerPu,
            _valve.Min, _valve.Max, nameof(mechanicalPowerPu));
        _pref = mechanicalPowerPu;
        _valve.Initialize(mechanicalPowerPu);
        _reheat.Initialize(mechanicalPowerPu);
    }

    /// <summary>
    /// R246 (October 2026, stream B3): change the load reference Pref to <paramref name="referencePu"/> WITHOUT touching
    /// any state. Call it after <see cref="Initialize"/> to apply a set-point step: the valve and reheat states stay at
    /// the initial equilibrium and the mechanical power then moves to the new reference through the valve lag T1 and the
    /// reheat lead-lag T2 / T3, as the block diagram says (PSS/E TGOV1: the reference enters the summing junction ahead
    /// of the valve lag). Re-initialising at the new reference instead (what GovernedMultiMachine used to do) preset
    /// every state there, so the power jumped to it on the first step with no lag at all. The value is not limited here
    /// (a reference above Vmax simply saturates the valve), but it must be finite.
    /// </summary>
    public void SetReference(double referencePu)
    {
        if (!double.IsFinite(referencePu))
            throw new ArgumentOutOfRangeException(nameof(referencePu), referencePu, "TGOV1: the load reference must be a finite number.");
        _pref = referencePu;
    }

    /// <summary>
    /// Advance one step of <paramref name="dtSeconds"/> with speed deviation <paramref name="speedDeviationPu"/>
    /// (pu, held across the step) and return the mechanical power output Pmech (pu).
    /// </summary>
    public double Step(double speedDeviationPu, double dtSeconds)
    {
        double valveCmd = _pref - speedDeviationPu / _rDroop;
        double pgv = _valve.Step(valveCmd, dtSeconds);
        double pt = _reheat.Step(pgv, dtSeconds);
        return pt - _dt * speedDeviationPu;
    }
}
