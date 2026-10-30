using GridSim.Core.Dynamics;

namespace GridSim.Core.Fleet;

/// <summary>
/// The fast-response states of a fleet of devices, shared by the batch <see cref="FleetDynamics.Simulate"/> and the live
/// <see cref="MultiDeviceFrequencyIntegrator"/> (October 2026 review, R080, stream B1: one copy of the unit loop instead
/// of two). Each unit has its own deadband, first-order lag and onset stagger, and all of them see one system frequency;
/// the fleet's fast response is the sum of the independently clamped, independently staggered unit outputs, which is
/// why fleet FFR is non-additive.
///
/// <para>Arming: a unit's <see cref="FleetUnit.StaggerSeconds"/> is a RESPONSE LATENCY measured from the moment the
/// frequency leaves that unit's deadband, not from absolute t = 0 (anchoring it to t = 0 made stagger a no-op whenever
/// units armed before the loss landed).</para>
/// </summary>
public sealed class FleetUnitBank
{
    private readonly IReadOnlyList<FleetUnit> _units;
    private readonly double[] _ffrMw;
    // When each unit's response starts (s on the caller's clock); +infinity = not armed.
    private readonly double[] _armAtS;
    // When the frequency last came back inside each unit's deadband (s on the caller's clock); +infinity = it is outside
    // now (R253 re-arming).
    private readonly double[] _insideSinceS;

    /// <summary>A bank for <paramref name="fleet"/>, every unit undeployed and not armed.</summary>
    public FleetUnitBank(FleetSpec fleet)
    {
        ArgumentNullException.ThrowIfNull(fleet);
        _units = fleet.Units;
        _ffrMw = new double[_units.Count];
        _armAtS = new double[_units.Count];
        _insideSinceS = new double[_units.Count];
        for (int u = 0; u < _units.Count; u++)
        {
            _armAtS[u] = double.PositiveInfinity;        // not yet armed
            _insideSinceS[u] = double.PositiveInfinity;  // set on the first step inside the deadband
        }
    }

    /// <summary>The fast response the whole fleet is delivering now (MW): the unit states added left to right from +0.0.</summary>
    public double TotalMw { get { double s = 0; foreach (double p in _ffrMw) s += p; return s; } }

    /// <summary>
    /// Advance every unit by one (sub-)step of <paramref name="stepSeconds"/> at the frequency deviation
    /// <paramref name="df"/> (Hz) and the caller's clock <paramref name="clockS"/> (s), and return the fleet's summed
    /// output (MW, the unit states after the update, left to right from +0.0).
    /// </summary>
    public double Advance(double df, double clockS, double stepSeconds)
    {
        double pFleet = 0;
        // GR-843 (October 2026): the arming and re-arming times are compared with the clock to a millionth of a step,
        // the same rule as the RMS and EMT event times. A unit armed at clock + stagger is a sum of two doubles, so a
        // stagger meant to land on the step grid (0.1 + 0.3 against 20 * 0.02 = 0.4) could round just above it and arm
        // one step late. A millionth of a step can never reach the previous grid point, so a stagger that is not on the
        // grid arms on the same step as before; a zero-stagger unit arms on its crossing step exactly as before.
        double timeTol = 1e-6 * stepSeconds;
        for (int u = 0; u < _units.Count; u++)
        {
            FleetUnit unit = _units[u];
            if (Math.Abs(df) > unit.FastResponseDeadbandHz)
            {
                // Outside the deadband. Arm the unit the first time frequency leaves ITS deadband; its output then lags
                // that crossing by StaggerSeconds (detection / dispatch / comms latency). With stagger 0 this arms on the
                // crossing step, so a one-unit zero-stagger fleet is byte-identical to the lumped model with that device.
                _insideSinceS[u] = double.PositiveInfinity;
                if (double.IsPositiveInfinity(_armAtS[u]))
                    _armAtS[u] = clockS + unit.StaggerSeconds;
            }
            else
            {
                // Inside the deadband. R253 (October 2026 review, stream B1; Ruling R1: Mark delegated EE calls
                // 2026-10-04): the unit RE-ARMS once the frequency has stayed inside its deadband for its own latency
                // (StaggerSeconds), so the next excursion meets the detection delay again. Before, a unit armed on the
                // first excursion stayed armed for good, so in a continuously running live twin every later event met an
                // already-armed fleet and the stagger was a no-op: the defect the event-relative arming was meant to fix.
                // GB fast-response services act continuously on the measured frequency (NESO Dynamic Containment service
                // terms: a static delivery curve, no one-shot arming), so the latency belongs to every excursion; the hold
                // equal to the latency is the symmetric detection rule (the same time to see the frequency come back as to
                // see it leave), which also stops chatter at the deadband edge from re-imposing the delay every step.
                // Bit-identity: inside the deadband the target is exactly 0.0 armed or not (FastResponseTarget returns 0.0
                // for |df| at or below the deadband), so disarming changes nothing until the frequency leaves again; a
                // zero-stagger unit re-arms on that same step (clock + 0 <= clock), so it is unchanged on every input; a
                // staggered unit changes only when the frequency re-enters its deadband for at least its stagger and then
                // leaves again (never in a single-loss study, which settles outside the deadband).
                if (double.IsPositiveInfinity(_insideSinceS[u])) _insideSinceS[u] = clockS;
                if (!double.IsPositiveInfinity(_armAtS[u]) && clockS - _insideSinceS[u] >= unit.StaggerSeconds - timeTol)
                    _armAtS[u] = double.PositiveInfinity;
            }
            double target = clockS >= _armAtS[u] - timeTol
                ? SwingKernel.FastResponseTarget(df, unit.FastResponseMw, unit.FastResponseDeadbandHz)
                : 0.0;
            _ffrMw[u] += (target - _ffrMw[u]) / Math.Max(stepSeconds, unit.FastResponseTimeConstantS) * stepSeconds;
            pFleet += _ffrMw[u];
        }
        return pFleet;
    }
}
