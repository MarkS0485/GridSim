using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Forming;
using GridSim.Core.Frequency;

namespace GridSim.Core.Fleet;

/// <summary>
/// A fleet frequency solve: the excursion, the peak fast-response the fleet ever delivered, and - the
/// decision-relevant figure - the fast-response online <b>at the nadir instant</b>, when the fall must be
/// arrested. Identical units that stay engaged all reach full output during a sustained excursion (so peak is
/// additive), but a <i>staggered</i> fleet has fewer units online at the early nadir - that gap is the emergent
/// non-additivity, and it shows up as a deeper nadir than an aggregate device of the same total budget.
/// </summary>
public sealed record FleetResult(
    FrequencyResponse Response,
    double PeakFleetFfrMw,
    double FfrAtNadirMw,
    double TotalInertiaMwS,
    double TotalFfrCapMw);

/// <summary>
/// The batch (pre-computed-trace) analogue of <see cref="FrequencyDynamics.Simulate"/> for a fleet of devices.
/// Identical governor/damping/swing physics, but the device term is the sum of N independently deadbanded,
/// lagged and onset-staggered unit outputs. With a single unit at zero stagger it is byte-identical to
/// <c>Simulate</c> with that device - the fleet generalises, it doesn't fork, the physics.
/// </summary>
public static class FleetDynamics
{
    /// <summary>
    /// Run the fleet after <paramref name="events"/>: the base system inertia plus the units' rotational inertia swing as
    /// one centre-of-inertia mass, and the units' fast response is the sum of their independently deadbanded, lagged and
    /// staggered outputs.
    ///
    /// <para><b>Grid-forming (N291, October 2026 review, stream B5; Ruling R1: Mark delegated EE calls 2026-10-04).</b>
    /// Under <see cref="DynamicsConfig.Forming"/> = Vsm or Droop the fleet runs the SAME grid-forming step as
    /// <see cref="FrequencyDynamics.Simulate"/> (<see cref="FormingKernel"/>): the frequency is formed by the config's
    /// aggregate virtual former (virtual inertia Ev = <see cref="DynamicsConfig.VirtualInertiaMwS"/>, damping
    /// Dv = <see cref="DynamicsConfig.FormingDampingMwPerHz"/>, setpoint Pset) over the real rotating mass, which here is
    /// the base inertia plus the fleet's N-additive unit inertia, and the units' summed fast response sits in the device
    /// slot of the forcing. The model is the power-frequency channel of a grid-forming converter (see
    /// <see cref="FormingMode"/>): Vsm is a second-order virtual synchronous machine,
    /// (2 (E + Ev) / f0) df/dt = P - (D + Dv)(f - f0), the swing part of a VSM / synchronverter (the virtual-synchronous-
    /// machine grid-forming model of the WECC / PSS/E REGFM family, REGFM_B1 [VERIFY model name]); Droop is the
    /// first-order P-f droop former (Ev = 0: the power-frequency loop of the droop-based REGFM_A1 model without its power
    /// measurement filter [VERIFY model name]). A droop former WITH a first-order power filter of time constant tau_f is
    /// the Vsm form with 2 Hv = tau_f / m_p and Dv = 1 / m_p (D'Arco and Suul, "Equivalence of virtual synchronous
    /// machines and frequency-droops for converter-based microgrids", IEEE Trans. Smart Grid 5(1), 2014, pp. 394-395).
    /// The voltage and current-limit channels of such a converter are not part of a centre-of-inertia frequency model
    /// (they are in the RMS and EMT converter models).</para>
    ///
    /// <para>Before N291 a forming config was refused here (R263), because this engine had only the floored swing. Now it
    /// runs, and a one-unit, zero-stagger forming fleet is bit for bit <see cref="FrequencyDynamics.Simulate"/> with that
    /// device (the same operands in the same order: the trace point is the start-of-step frequency, the reported RoCoF
    /// the first sub-step's, the fleet's fast response at the nadir the first sub-step's output; the peak fleet response
    /// is taken over every sub-step). With Forming = None every number is the old one: the non-forming loop below is the
    /// pre-N291 loop unchanged.</para>
    /// </summary>
    public static FleetResult Simulate(
        double baseInertiaMwS, double baseDemandMw, IEnumerable<FrequencyEvent> events,
        FleetSpec fleet, DynamicsConfig? config = null)
    {
        DynamicsConfig cfg = (config ?? new DynamicsConfig()) with
        {
            DeviceRotationalInertiaMwS = 0.0,
            FastResponseMw = 0.0,
        };
        // R080 (October 2026, stream B1): the governor, the recorded system response services (they survive the device
        // strip above: they are what the REAL system held, not device terms) and the equilibrium start are the one swing
        // kernel's (SwingResponders), and the unit loop is the one shared with the live twin (FleetUnitBank). Fleet units
        // start undeployed (they are the counterfactual). Same operands in the same order as the old inline loop.
        var resp = new SwingResponders(cfg, baseDemandMw);
        var bank = new FleetUnitBank(fleet);
        double f0 = resp.NominalHz;
        // Inertia N-additive. eForming is the unfloored forming denominator E_real + Ev (it fails loud when nothing forms
        // the frequency); without forming it equals the floored E and is not used.
        (double e, double eForming) = SwingKernel.SwingInertia(baseInertiaMwS + fleet.TotalInertiaMwS, cfg);
        double dt = cfg.StepSeconds;
        int steps = SwingKernel.BatchStepCount(cfg);
        FrequencyEvent[] ev = SwingKernel.SortedEvents(events);

        var baseTime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var points = new List<FrequencyPoint>(steps + 1);

        double f = resp.StartFrequencyHz;
        double peakRocof = 0, nadir = f, nadirT = 0, peakFleetFfr = 0, ffrAtNadir = 0;

        for (int k = 0; k <= steps; k++)
        {
            double t = k * dt;
            double df = f - f0;
            double pEvent = SwingKernel.ActiveEventMw(ev, t, dt);

            if (!resp.Forming)
            {
                resp.Advance(df, dt);                    // governor + recorded services (the device slot is zero)
                double pFleet = bank.Advance(df, t, dt); // the units, each on its own deadband / lag / stagger
                if (Math.Abs(pFleet) > Math.Abs(peakFleetFfr)) peakFleetFfr = pFleet;

                double rocof = f0 / (2.0 * e) * resp.ImbalanceMw(pEvent, pFleet, df);

                points.Add(new FrequencyPoint(baseTime.AddSeconds(t), f, rocof));
                if (f < nadir) { nadir = f; nadirT = t; ffrAtNadir = pFleet; }
                if (Math.Abs(rocof) > Math.Abs(peakRocof)) peakRocof = rocof;

                f += rocof * dt;
            }
            else
            {
                // N291: grid-forming FORMATION, the forming branch of FrequencyDynamics.Simulate with the fleet in the
                // device slot. The (f - f0) damping (load + former) is integrated implicitly (A-stable); the governor, the
                // services and the units stay explicit in the forcing, so the outer step is sub-divided by
                // FormingKernel.SubStepCount to keep fast, high-gain responders stable at small E + Ev. The units' clock
                // advances with the sub-steps (t + sub * h; sub-step 0 is t itself, the clock of the non-forming loop).
                double fStart = f;
                int nSub = FormingKernel.SubStepCount(dt, f0, eForming);
                double h = dt / nSub;
                double rocofReported = 0.0, pFleetReported = 0.0;
                for (int sub = 0; sub < nSub; sub++)
                {
                    double dfSub = f - f0;
                    resp.Advance(dfSub, h);
                    double pFleet = bank.Advance(dfSub, t + sub * h, h);
                    if (Math.Abs(pFleet) > Math.Abs(peakFleetFfr)) peakFleetFfr = pFleet;   // peak over every sub-step
                    (double fNext, double rocof) = FormingKernel.SemiImplicitStep(
                        f, f0, eForming, resp.FormingForcingMw(pEvent, pFleet), resp.FormingDampingTotalMwPerHz, h);
                    if (sub == 0) { rocofReported = rocof; pFleetReported = pFleet; }   // start-of-step, the None convention
                    f = fNext;
                }

                points.Add(new FrequencyPoint(baseTime.AddSeconds(t), fStart, rocofReported));
                if (fStart < nadir) { nadir = fStart; nadirT = t; ffrAtNadir = pFleetReported; }
                if (Math.Abs(rocofReported) > Math.Abs(peakRocof)) peakRocof = rocofReported;
            }
        }

        var response = new FrequencyResponse(peakRocof, nadir, nadirT, f, points);
        return new FleetResult(response, peakFleetFfr, ffrAtNadir, fleet.TotalInertiaMwS, fleet.TotalFfrCapMw);
    }
}
