using GridSim.Core.Dynamics.Forming;
using GridSim.Core.Fleet;

namespace GridSim.Core.Dynamics;

// Internal use (F stream, R079, Ruling R1): the live twin of FleetDynamics, pinned equal to it by B1; users reach the fleet through `gridsim fleet` (the batch twin). No front door of its own.
/// <summary>
/// The stateful, live twin of <see cref="Fleet.FleetDynamics"/> - the multi-device analogue of
/// <see cref="FrequencyIntegrator"/>. A base system inertia plus N <see cref="FleetUnit"/>s, each with its own
/// FFR state, deadband, lag and onset stagger, all seeing one system frequency. Intrinsic inertia is summed into
/// E (N-additive real (1/2)J*omega^2); the fast response is the <b>sum of independently-clamped, independently-staggered
/// unit outputs</b>, which is strictly less than Nx a single unit at full tilt - the emergent non-additivity.
///
/// <para>R080 (October 2026, stream B1): the governor, the recorded system response services and the equilibrium start
/// are the one swing kernel's (<see cref="SwingResponders"/>) and the unit loop is the one shared with the batch twin
/// (<see cref="FleetUnitBank"/>). Before R080 this integrator ignored the recorded services, the pre-event frequency and
/// every grid-forming field, and floored the demand at 1 MW; R080 then refused grid-forming here, as its batch twin
/// refused it (R263), instead of silently running the non-forming swing.</para>
///
/// <para><b>Grid-forming (N291, October 2026 review, stream B5; Ruling R1: Mark delegated EE calls 2026-10-04).</b> Under
/// <see cref="DynamicsConfig.Forming"/> = Vsm or Droop this integrator now runs the grid-forming step of its single-mass
/// twin <see cref="FrequencyIntegrator"/> (<see cref="FormingKernel"/>, the config's aggregate virtual former over the base
/// plus unit inertia) with the fleet's summed fast response in the device slot of the forcing, exactly as the batch twin
/// <see cref="Fleet.FleetDynamics.Simulate"/> does (the model and its sources are documented there). A one-unit,
/// zero-stagger forming fleet is bit for bit <see cref="FrequencyIntegrator"/> with that device; with Forming = None every
/// number is the old one (the forming flag is false, the forming denominator equals the floored E, so the sub-step count
/// and the swing step are the pre-N291 ones).</para>
///
/// <para>Internal use (N314, LF5, October 2026; F stream R079, Ruling R1): the live twin of FleetDynamics, pinned equal to
/// it by B1; users reach the fleet through <c>gridsim fleet</c> (the batch twin). No front door of its own, so the type is
/// <c>internal</c> (its tests reach it through InternalsVisibleTo); nothing outside Core used it.</para>
/// </summary>
internal sealed class MultiDeviceFrequencyIntegrator
{
    private readonly SwingResponders _resp;
    private readonly FleetUnitBank _bank;
    private readonly double _f0, _e;
    private readonly double _eForming;   // forming denominator E_real + E_virtual, MW.s (unfloored); equals _e when not forming
    private double _clock;

    public double FrequencyHz { get; private set; }
    public double RocofHzPerS { get; private set; }

    public MultiDeviceFrequencyIntegrator(
        double baseInertiaMwS, double baseDemandMw, FleetSpec fleet, DynamicsConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(fleet);
        // The units carry the device inertia/FFR - zero the single-device slots so nothing is double-counted.
        DynamicsConfig cfg = (config ?? new DynamicsConfig()) with { DeviceRotationalInertiaMwS = 0.0, FastResponseMw = 0.0 };
        _resp = new SwingResponders(cfg, baseDemandMw);
        _bank = new FleetUnitBank(fleet);
        _f0 = _resp.NominalHz;
        (_e, _eForming) = SwingKernel.SwingInertia(baseInertiaMwS + fleet.TotalInertiaMwS, cfg); // inertia N-additive
        FrequencyHz = _resp.StartFrequencyHz;
    }

    /// <summary>Total spinning energy E (MW.s), base + all units' rotational inertia.</summary>
    public double SystemInertiaMwS => _e;

    /// <summary>The fast-response power the whole fleet is delivering right now (MW).</summary>
    public double FleetFfrMw => _bank.TotalMw;

    /// <summary>Advance the fleet by <paramref name="dtSeconds"/> under net imbalance <paramref name="eventMw"/> (MW; negative = deficit).</summary>
    public void Step(double dtSeconds, double eventMw)
    {
        // N291 (October 2026, stream B2): a NaN, infinite, zero or negative step (or a non-finite event power) is refused
        // here; it used to be dropped (zero / negative) or to turn the frequency NaN for good (NaN / infinity).
        SwingKernel.RequireLiveStep(dtSeconds, eventMw, nameof(MultiDeviceFrequencyIntegrator));
        int nSub = SwingKernel.LiveSubStepCount(dtSeconds, _resp.Forming, _f0, _eForming);
        double h = dtSeconds / nSub;
        // Bug hunt 2026-10-29, finding 18: the batch twin's explicit-Euler damping bound, at this call's sub-step.
        SwingKernel.RequireLiveDampingStable(h, _f0, _resp.DampingMwPerHz, _e, _resp.Forming, nameof(MultiDeviceFrequencyIntegrator));
        double f = FrequencyHz;

        for (int s = 0; s < nSub; s++)
        {
            double df = f - _f0;

            _resp.Advance(df, h);                          // governor + recorded services (the device slot is zero)
            double pFleet = _bank.Advance(df, _clock, h);   // the units, each on its own deadband / lag / stagger

            if (!_resp.Forming)
            {
                double rocof = _f0 / (2.0 * _e) * _resp.ImbalanceMw(eventMw, pFleet, df);
                f += rocof * h;
                RocofHzPerS = rocof;
            }
            else
            {
                // N291: the forming step of FrequencyIntegrator, the fleet in the device slot of the forcing.
                (double fNext, double rocof) = FormingKernel.SemiImplicitStep(
                    f, _f0, _eForming, _resp.FormingForcingMw(eventMw, pFleet), _resp.FormingDampingTotalMwPerHz, h);
                f = fNext;
                RocofHzPerS = rocof;
            }
            _clock += h;
        }
        SwingKernel.RequireFiniteLiveState(f, RocofHzPerS, nameof(MultiDeviceFrequencyIntegrator));   // finding 18: fail loud, never publish NaN
        FrequencyHz = f;
    }
}
