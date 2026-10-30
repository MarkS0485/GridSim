namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// The classic transient-droop (dashpot) hydro turbine-governor with the ideal water column - the standard
/// hydro-plant model, alongside the steam <see cref="Tgov1"/> / <see cref="Ieeeg1"/>. Two features distinguish
/// a hydro governor: the <b>transient droop</b> (a washout of gate position that temporarily opposes gate
/// motion, so the slow water column is not over-driven), and the <b>non-minimum-phase water column</b>, whose
/// power initially moves the <i>wrong way</i> when the gate opens (water inertia). Structure:
/// <code>
///   err  = Pref - dw
///   c    = (err - Rt*washout_{Tr}(g)) / Rp        (permanent droop Rp, transient droop Rt over Tr)
///   g    = lag_{Tg, [gmin,gmax]}(c)                (gate servo)
///   Pmech = (1 - sTw)/(1 + 0.5 sTw) * g            (ideal water column)
/// </code>
/// All quantities are per-unit. The water column is realised exactly as <c>Pmech = 3*lag_{0.5Tw}(g) - 2*g</c>
/// (the partial-fraction form of <c>(1 - sTw)/(1 + 0.5sTw)</c>), so a gate opening of <c>dg</c> makes the
/// power first drop by <c>2*dg</c> before recovering to <c>+dg</c> - the water-hammer inverse response, exact
/// to machine epsilon. With <c>dw = 0</c> and <c>Pref = Rp*Pmech0</c> the model is stationary, and the steady
/// (permanent) droop is <c>dPmech/dw = -1/Rp</c>. The dynamics are the exact-discretised <see cref="Washout"/>
/// and <see cref="FirstOrderLag"/> blocks.
/// </summary>
public sealed class HydroGovernor : IRmsGovernor
{
    private readonly double _rp, _rt;
    private readonly Washout _transientDroop;
    private readonly FirstOrderLag _gate;
    private readonly FirstOrderLag _waterLag;   // lag with time 0.5*Tw, gain 3 (the water-column partial fraction)
    private readonly double _tg;                // gate-servo time constant Tg (s), kept to pick the step scheme
    private double _pref;

    /// <param name="permanentDroopPu">Permanent droop Rp (pu); must be &gt; 0.</param>
    /// <param name="transientDroopPu">Transient droop Rt (pu), &gt;= 0.</param>
    /// <param name="resetTimeSeconds">Transient-droop (dashpot) reset time Tr (s); must be &gt; 0.</param>
    /// <param name="gateTimeSeconds">Gate-servo time constant Tg (s), 0 or above; 0 makes the gate instantaneous. A negative
    /// value is refused (bug hunt 2026-10-29, finding 26: the doc used to promise that a negative value was instant too, but the lag has refused negatives since GR-557).</param>
    /// <param name="waterTimeSeconds">Water starting time Tw (s); must be &gt; 0.</param>
    /// <param name="gateMinPu">Gate lower limit (pu).</param>
    /// <param name="gateMaxPu">Gate upper limit (pu).</param>
    public HydroGovernor(double permanentDroopPu, double transientDroopPu, double resetTimeSeconds,
        double gateTimeSeconds, double waterTimeSeconds,
        double gateMinPu = 0.0, double gateMaxPu = double.PositiveInfinity)
    {
        if (permanentDroopPu <= 0.0) throw new System.ArgumentOutOfRangeException(nameof(permanentDroopPu), "Rp must be > 0.");
        if (waterTimeSeconds <= 0.0) throw new System.ArgumentOutOfRangeException(nameof(waterTimeSeconds), "Tw must be > 0.");
        _rp = permanentDroopPu;
        _rt = transientDroopPu;
        _transientDroop = new Washout(resetTimeSeconds);
        if (!(gateTimeSeconds >= 0.0) || !double.IsFinite(gateTimeSeconds))
            throw new System.ArgumentOutOfRangeException(nameof(gateTimeSeconds), gateTimeSeconds, "Tg must be a finite number of seconds, 0 or above; 0 makes the block instantaneous (a negative \"disabled\" marker is not accepted).");
        if (!(transientDroopPu >= 0.0) || !double.IsFinite(transientDroopPu))
            throw new System.ArgumentOutOfRangeException(nameof(transientDroopPu), transientDroopPu, "Rt must be a finite number >= 0.");
        _tg = gateTimeSeconds;
        _gate = new FirstOrderLag(gain: 1.0, timeConstantSeconds: gateTimeSeconds, min: gateMinPu, max: gateMaxPu);
        _waterLag = new FirstOrderLag(gain: 3.0, timeConstantSeconds: 0.5 * waterTimeSeconds);
    }

    /// <summary>Gate position (pu) after the last step.</summary>
    public double GatePosition => _gate.Output;

    /// <summary>Set the reference and pre-load every state so <c>dw = 0</c> holds mechanical power
    /// <paramref name="mechanicalPowerPu"/> as an exact equilibrium (gate = Pmech0, transient droop washed out).</summary>
    public void Initialize(double mechanicalPowerPu)
    {
        // R252 (October 2026, stream B3): the gate sits at g = Pmech0 at equilibrium, so Pmech0 must lie inside
        // [gmin, gmax]; out of limits is refused instead of clamped silently.
        ControlBlocks.RequireEquilibriumWithinLimits("hydro governor", "gate position g", mechanicalPowerPu,
            _gate.Min, _gate.Max, nameof(mechanicalPowerPu));
        _pref = _rp * mechanicalPowerPu;                 // steady gate = err/Rp = Pmech0
        _transientDroop.Initialize(mechanicalPowerPu);   // washout of the gate -> 0
        _gate.Initialize(mechanicalPowerPu);
        _waterLag.Initialize(3.0 * mechanicalPowerPu);   // gain-3 lag: steady output 3*Pmech0 so Pmech = 3Pmech0 - 2Pmech0
    }

    /// <summary>R246 (October 2026, stream B3): change the power reference to <paramref name="referencePu"/> (the steady
    /// gate, and so the steady mechanical power, it asks for at rated speed) without touching any state. The internal
    /// reference is <c>Rp * referencePu</c>, the same expression <see cref="Initialize"/> uses, so setting the initial
    /// power again is an exact no-op. A step then travels through the gate servo, the transient droop and the water
    /// column (with its inverse response) instead of jumping. Finite values only.</summary>
    public void SetReference(double referencePu)
    {
        if (!double.IsFinite(referencePu))
            throw new System.ArgumentOutOfRangeException(nameof(referencePu), referencePu, "Hydro governor: the power reference must be a finite number.");
        _pref = _rp * referencePu;
    }

    /// <summary>Advance one step of <paramref name="dtSeconds"/> with speed deviation
    /// <paramref name="speedDeviationPu"/> (pu, held across the step) and return the mechanical power Pmech (pu).</summary>
    public double Step(double speedDeviationPu, double dtSeconds)
    {
        double err = _pref - speedDeviationPu;
        double g;
        if (_tg <= ControlBlocks.InstantThresholdSeconds)
        {
            // Instant gate (Tg <= the instant threshold, documented as "the gate instantaneous"). The gate command
            // c depends on the gate itself through the transient-droop washout, which passes a gate change straight
            // through: an algebraic loop. Evaluating it explicitly (washout of the PREVIOUS gate) multiplies a gate
            // error by -Rt/Rp every step, -10 for Rt = 0.4, Rp = 0.04: the gate diverged or banged limit to limit.
            // The loop is linear, so solve it exactly with the washout state xL held across the step:
            //   g = (err - Rt (g - xL)) / Rp   =>   g = (err + Rt xL) / (Rp + Rt),
            // then apply the gate limits as the servo block does.
            g = (err + _rt * _transientDroop.State) / (_rp + _rt);
            _gate.Initialize(g);                         // sets the output, clamped to [gmin, gmax]
            g = _gate.Output;
        }
        else
        {
            // Finite Tg: the explicit evaluation is kept (it is the validated discretisation for every pinned case),
            // but it is only meaningful while one step does not flip the sign of a gate perturbation. The step map
            // of a perturbation is dg' = (a - (1 - a) Rt/Rp) dg with a = exp(-dt/Tg); a negative factor is a
            // numerical oscillation the physics does not have, and below -1 it diverges. Refuse it, naming the
            // smallest gate time this step and droop data support, rather than return a ringing or runaway gate.
            double a = DetMath.Exp(-dtSeconds / _tg);
            double factor = a - (1.0 - a) * _rt / _rp;
            if (!(factor >= 0.0))
            {
                double minTg = dtSeconds / DetMath.Log(1.0 + _rt / _rp);   // D1: DetMath, as everywhere in Core
                throw new System.ArgumentOutOfRangeException(nameof(dtSeconds), dtSeconds, string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"Hydro governor: the gate time constant Tg = {_tg:R} s is too short for a {dtSeconds:R} s step with " +
                    $"Rt/Rp = {_rt / _rp:R}; the transient-droop loop would oscillate step to step (factor {factor:R}). " +
                    $"Use Tg >= {minTg:G4} s, a smaller step, or Tg = 0 (an instant gate, solved exactly)."));
            }
            double c = (err - _rt * _transientDroop.OutputFor(_gate.Output)) / _rp;
            g = _gate.Step(c, dtSeconds);
        }
        _transientDroop.Step(g, dtSeconds);              // advance the dashpot washout on the new gate
        double lag = _waterLag.Step(g, dtSeconds);       // 3/(1+0.5Tw s) of the gate
        return lag - 2.0 * g;                            // (1 - sTw)/(1 + 0.5sTw) * g
    }
}
