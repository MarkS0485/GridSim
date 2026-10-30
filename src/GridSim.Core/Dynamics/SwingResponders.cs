using GridSim.Core.Dynamics.Forming;

namespace GridSim.Core.Dynamics;

/// <summary>
/// THE one swing kernel of the centre-of-inertia frequency family (October 2026 review, R080; stream B1; Ruling R1: Mark
/// delegated EE calls 2026-10-04). It owns every responder state of the swing equation and the order in which their
/// powers are added, so the seven engines of the family compute the same physics from one copy:
/// <list type="bullet">
/// <item><see cref="FrequencyDynamics.Simulate"/> (batch, lumped);</item>
/// <item><see cref="AssetFrequencyDynamics.Simulate"/> (batch, lumped, plus the authored asset fleet);</item>
/// <item><see cref="FrequencyIntegrator"/> (live, lumped);</item>
/// <item><see cref="AssetAwareFrequencyIntegrator"/> (live, lumped, plus the authored asset fleet);</item>
/// <item><see cref="Fleet.FleetDynamics.Simulate"/> (batch, a fleet of devices);</item>
/// <item><see cref="MultiDeviceFrequencyIntegrator"/> (live, a fleet of devices);</item>
/// <item><see cref="Distributed.MultiDomainSwing.Simulate"/> (batch, the distributed model's per-domain swing).</item>
/// </list>
///
/// <para>What it holds, per run: the governor droop lag (GB Grid Code 5 % droop through a first-order lag), the TSGB
/// device's fast frequency response (a capped droop with a deadband, through its own lag), the recorded system response
/// services (Dynamic Containment / Moderation / Regulation on their published delivery curves, each through its own lag),
/// the standing imbalance of an equilibrium start at a recorded pre-event frequency, and the load self-damping. The swing
/// itself (df/dt = f0 / (2E) * deltaP, Kundur, Power System Stability and Control, 1994, section 3.9 and section 11.1;
/// or the grid-forming semi-implicit step of <see cref="FormingKernel"/>) is integrated by each engine, because the
/// engines legitimately differ there (a fixed batch trace, a live clock, one mass or several).</para>
///
/// <para>Why it exists (the R080 finding): the five hand-copied loops had drifted. The two live integrators ignored the
/// recorded services and the pre-event frequency (<see cref="DynamicsConfig.SystemResponseServices"/>,
/// <see cref="DynamicsConfig.InitialFrequencyHz"/>) although their documentation said "the per-step physics are
/// identical to Simulate"; the multi-device integrator ignored the grid-forming fields; and the live integrators floored
/// the demand at 1 MW while the batch ones did not (the R163 demand-floor inconsistency). All of them now take every
/// one of those terms from here, so they cannot drift again. Engines that cannot model a feature refuse it loudly instead
/// of ignoring it. (The fleet engines refused grid-forming from R263 / R080 until N291, stream B5, routed them through the
/// forming step with the fleet's summed fast response in the device slot.)</para>
///
/// <para>Bit-identity (Mark's determinism rule): every state update and every sum below is the expression the engines
/// used, operand for operand and in the same order (the B1 notes and <c>B1SwingKernelTests</c> carry the verbatim old
/// loops and compare them bit for bit). Where an engine used to leave a term out (the live integrators had no standing
/// imbalance and no services), the kernel adds an exact +0.0 in its place: x + (+0.0) is x for every x except -0.0, and
/// no sum here can be -0.0 because the governor state is never -0.0 (it starts at +0.0 and an update that cancels
/// exactly gives +0.0 in round-to-nearest), so those engines are bit-identical too when the terms are absent.</para>
/// </summary>
public sealed class SwingResponders
{
    private readonly double _f0;
    private readonly double _droopPu;
    private readonly double _responsiveMw;
    private readonly double _governorTimeConstantS;

    // The TSGB device's fast frequency response (zero in the fleet engines, whose units carry the device response).
    private readonly double _deviceMw;
    private readonly double _deviceGainMwPerHz;
    private readonly double _deviceDeadbandHz;
    private readonly double _deviceTimeConstantS;

    // The recorded system response services, one first-order lag state each.
    private readonly IReadOnlyList<SystemResponseService> _services;
    private readonly double[] _serviceMw;

    /// <summary>The nominal frequency f0 (Hz) of the run: the reference every deviation is taken from.</summary>
    public double NominalHz => _f0;

    /// <summary>Load self-damping D (MW per Hz) = DampingPctPerHz / 100 * demand.</summary>
    public double DampingMwPerHz { get; }

    /// <summary>Droop-responsive capacity (MW) = ResponsiveFraction * demand.</summary>
    public double ResponsiveMw => _responsiveMw;

    /// <summary>True when the run is grid-forming (<see cref="DynamicsConfig.Forming"/> is not None).</summary>
    public bool Forming { get; }

    /// <summary>The damping the forming swing integrates implicitly: load damping plus the virtual former's damping Dv
    /// (MW per Hz). Also the damping the equilibrium start balances on a forming run.</summary>
    public double FormingDampingTotalMwPerHz { get; }

    /// <summary>The former's deviation setpoint Pset (MW), a forcing term of the forming swing.</summary>
    public double FormingSetpointMw { get; }

    /// <summary>The frequency the run starts at (Hz): the recorded pre-event frequency when one is set, else f0.</summary>
    public double StartFrequencyHz { get; }

    /// <summary>The standing imbalance (MW) of an equilibrium start: it cancels the pre-loaded responders and the damping
    /// at the start frequency, so df/dt(0) = 0. Exactly +0.0 when the run starts at nominal.</summary>
    public double StandingMw { get; }

    /// <summary>Governor droop output (MW), the state of its first-order lag.</summary>
    public double GovernorMw { get; private set; }

    /// <summary>The TSGB device's fast-response output (MW), the state of its first-order lag.</summary>
    public double DeviceMw { get; private set; }

    /// <summary>The recorded services' total output (MW): the sum, left to right in list order from +0.0, of the
    /// service lag states after the latest <see cref="Advance"/> (or of the pre-loaded states before the first).</summary>
    public double ServicesMw { get; private set; }

    /// <summary>
    /// Build the responders for one run of <paramref name="cfg"/> at <paramref name="demandMw"/>.
    /// Fails loud (ArgumentOutOfRangeException) on a demand that is negative or not finite (it would turn the load
    /// damping and the governor's responsive capacity negative, a power system that amplifies its own excursions), on a
    /// nominal frequency that is not a finite positive number, and on a pre-event frequency that is not.
    /// </summary>
    public SwingResponders(DynamicsConfig cfg, double demandMw)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        // R163 demand-floor inconsistency, unified (R080): the live integrators floored the demand at 1 MW, the batch
        // engines used the raw figure. The raw figure is the right one: demand is never a divisor here (it scales the
        // damping and the responsive capacity), so a floor only invents 0.01 MW/Hz of damping and 0.2 MW of governor
        // response for a system with no load. The one live caller (LiveSim) floors the demand itself before it gets
        // here, so no live result moves. A negative or non-finite demand is refused instead of being floored away.
        if (!double.IsFinite(demandMw) || demandMw < 0.0)
            throw new ArgumentOutOfRangeException(nameof(demandMw), demandMw,
                "The demand that scales the load damping and the governor's responsive capacity must be a finite number of MW, zero or above.");
        if (!double.IsFinite(cfg.NominalHz) || !(cfg.NominalHz > 0.0))
            throw new ArgumentOutOfRangeException(nameof(cfg), cfg.NominalHz,
                "DynamicsConfig.NominalHz (the run's nominal frequency) must be a finite number of Hz above zero.");
        // Every other numeric knob, once, here (every swing engine builds its responders through this constructor).
        // They used to be taken as given: a NaN step gave a zero-step "result", a NaN droop or damping a NaN
        // trajectory, a negative time constant an unstable lag.
        static void Check(double v, bool positive, string name)
        {
            if (!double.IsFinite(v) || (positive ? !(v > 0.0) : v < 0.0))
                throw new ArgumentOutOfRangeException(nameof(cfg), v,
                    $"DynamicsConfig.{name} must be a finite number{(positive ? " above zero" : ", zero or above")}.");
        }
        Check(cfg.DroopPu, positive: true, nameof(cfg.DroopPu));
        Check(cfg.DampingPctPerHz, positive: false, nameof(cfg.DampingPctPerHz));
        Check(cfg.ResponsiveFraction, positive: false, nameof(cfg.ResponsiveFraction));
        Check(cfg.GovernorTimeConstantS, positive: false, nameof(cfg.GovernorTimeConstantS));
        Check(cfg.StepSeconds, positive: true, nameof(cfg.StepSeconds));
        Check(cfg.DurationSeconds, positive: false, nameof(cfg.DurationSeconds));
        Check(cfg.DeviceRotationalInertiaMwS, positive: false, nameof(cfg.DeviceRotationalInertiaMwS));
        Check(cfg.FastResponseMw, positive: false, nameof(cfg.FastResponseMw));
        Check(cfg.FastResponseTimeConstantS, positive: false, nameof(cfg.FastResponseTimeConstantS));
        Check(cfg.FastResponseDeadbandHz, positive: false, nameof(cfg.FastResponseDeadbandHz));
        Check(cfg.FormingDampingMwPerHz, positive: false, nameof(cfg.FormingDampingMwPerHz));

        _f0 = cfg.NominalHz;
        _droopPu = cfg.DroopPu;
        _governorTimeConstantS = cfg.GovernorTimeConstantS;
        DampingMwPerHz = cfg.DampingPctPerHz / 100.0 * demandMw;   // MW per Hz
        _responsiveMw = cfg.ResponsiveFraction * demandMw;          // MW of droop-responsive capacity

        _deviceMw = cfg.FastResponseMw;
        _deviceGainMwPerHz = cfg.FastResponseMw / SwingKernel.DeviceFullDeliveryHz;   // full TSGB response at 0.5 Hz
        _deviceDeadbandHz = cfg.FastResponseDeadbandHz;
        _deviceTimeConstantS = cfg.FastResponseTimeConstantS;

        _services = cfg.SystemResponseServices;
        // GR-566: each delivery curve must be in order: deadband >= 0, full delivery beyond the deadband, a knee (when
        // given) between the two, and a knee fraction from 0 to 1. A misordered curve used to integrate as a step through
        // the 1e-9 denominator floor in FastResponseTarget, with no message.
        for (int s = 0; s < _services.Count; s++)
        {
            SystemResponseService svc = _services[s];
            // Without a knee the curve is the line through zero (full output at FullDeliveryDeviationHz, zero inside
            // the deadband), so a deadband EQUAL to the full-delivery point is a valid static (step) service: the
            // recorded 2019 static service is 198 MW triggered at 0.3 Hz with full delivery at 0.3 Hz. That form has
            // no 1e-9 floor; it only needs full > 0 and full at or beyond the deadband. The strict deadband < full
            // applies to the knee form, whose segments divide by (knee - deadband) and (full - knee).
            bool kneeForm = svc.KneeDeviationHz > 0;
            bool ok = double.IsFinite(svc.Mw) && svc.Mw >= 0
                && double.IsFinite(svc.DeadbandHz) && svc.DeadbandHz >= 0
                && double.IsFinite(svc.FullDeliveryDeviationHz) && svc.FullDeliveryDeviationHz > 0
                && (kneeForm ? svc.FullDeliveryDeviationHz > svc.DeadbandHz : svc.FullDeliveryDeviationHz >= svc.DeadbandHz)
                && (!kneeForm
                    || (svc.KneeDeviationHz >= svc.DeadbandHz && svc.KneeDeviationHz <= svc.FullDeliveryDeviationHz
                        && svc.KneeFraction >= 0 && svc.KneeFraction <= 1));
            if (!ok)
                throw new ArgumentException(
                    $"Response service {s}: the delivery curve is not in order (Mw {svc.Mw}, deadband {svc.DeadbandHz} Hz, knee " +
                    $"{svc.KneeDeviationHz} Hz at {svc.KneeFraction}, full at {svc.FullDeliveryDeviationHz} Hz); it needs " +
                    "full > 0 and deadband <= full (a step when equal) without a knee, or 0 <= deadband <= knee <= full, " +
                    "deadband < full and a knee fraction from 0 to 1 with one.", nameof(cfg));
        }
        _serviceMw = new double[_services.Count];

        Forming = cfg.Forming != FormingMode.None;
        FormingDampingTotalMwPerHz = DampingMwPerHz + cfg.FormingDampingMwPerHz;
        FormingSetpointMw = cfg.FormingSetpointMw;

        StartFrequencyHz = _f0;
        if (cfg.InitialFrequencyHz is { } fInit)
        {
            if (!double.IsFinite(fInit) || !(fInit > 0.0))
                throw new ArgumentOutOfRangeException(nameof(cfg), fInit,
                    "DynamicsConfig.InitialFrequencyHz (the recorded pre-event frequency) must be a finite number of Hz above zero.");
            // Equilibrium start at the recorded pre-event frequency: pre-load the governor and service states to their
            // static targets at the offset, and carry the standing imbalance that was balancing them (df/dt(0) = 0 by
            // construction). Device terms start undeployed (a counterfactual device is additional to the system).
            StartFrequencyHz = fInit;
            double df0 = fInit - _f0;
            GovernorMw = SwingKernel.GovernorTarget(df0, _f0, _droopPu, _responsiveMw);
            double servicesSum = 0;
            for (int s = 0; s < _serviceMw.Length; s++)
            {
                SystemResponseService svc = _services[s];
                _serviceMw[s] = SwingKernel.FastResponseTarget(
                    df0, svc.Mw, svc.DeadbandHz, svc.FullDeliveryDeviationHz, svc.KneeDeviationHz, svc.KneeFraction);
                servicesSum += _serviceMw[s];
            }
            ServicesMw = servicesSum;
            // The standing imbalance must cancel the SAME damping the swing integrates against, so df/dt(0) = 0: the
            // forming branch integrates against D + Dv, so Dv is included on a forming run.
            double dEquilibrium = Forming ? FormingDampingTotalMwPerHz : DampingMwPerHz;
            StandingMw = -(GovernorMw + ServicesMw - dEquilibrium * df0);
        }
    }

    /// <summary>
    /// Advance every responder by one (sub-)step of <paramref name="stepSeconds"/> at the frequency deviation
    /// <paramref name="df"/> (Hz, f - f0): the governor droop lag, the TSGB device lag and each recorded service's lag,
    /// explicit Euler, each on its own state (the order of the three updates does not matter: none reads another).
    /// </summary>
    public void Advance(double df, double stepSeconds)
    {
        double pGovTarget = SwingKernel.GovernorTarget(df, _f0, _droopPu, _responsiveMw);
        GovernorMw += SwingKernel.GovernorLagIncrement(pGovTarget, GovernorMw, _governorTimeConstantS, stepSeconds);   // R242: guarded lag

        // TSGB fast frequency response: near-instant droop on deltaf, capped, outside a deadband.
        double ffrTarget = 0;
        if (_deviceMw > 0 && Math.Abs(df) > _deviceDeadbandHz)
            ffrTarget = Math.Clamp(-_deviceGainMwPerHz * df, -_deviceMw, _deviceMw);
        DeviceMw += (ffrTarget - DeviceMw) / Math.Max(stepSeconds, _deviceTimeConstantS) * stepSeconds;

        // Recorded system response services (DC / DM / DR the real system held), each on its own published delivery
        // curve and time constant.
        double pServices = 0;
        for (int s = 0; s < _serviceMw.Length; s++)
        {
            SystemResponseService svc = _services[s];
            double target = SwingKernel.FastResponseTarget(
                df, svc.Mw, svc.DeadbandHz, svc.FullDeliveryDeviationHz, svc.KneeDeviationHz, svc.KneeFraction);
            _serviceMw[s] += (target - _serviceMw[s]) / Math.Max(stepSeconds, svc.TimeConstantS) * stepSeconds;
            pServices += _serviceMw[s];
        }
        ServicesMw = pServices;
    }

    /// <summary>The non-forming swing's net imbalance (MW): event + standing + governor + device + services - D * df,
    /// added left to right in exactly that order.</summary>
    public double ImbalanceMw(double eventMw, double df) => ImbalanceMw(eventMw, DeviceMw, df);

    /// <summary>As <see cref="ImbalanceMw(double, double)"/> with <paramref name="deviceMw"/> in the device's place: the
    /// fleet engines put their units' summed fast response there (their own device slot is zeroed), so a one-unit,
    /// zero-stagger fleet adds the same numbers in the same order as the lumped model with that device.</summary>
    public double ImbalanceMw(double eventMw, double deviceMw, double df)
        => eventMw + StandingMw + GovernorMw + deviceMw + ServicesMw - DampingMwPerHz * df;

    /// <summary>The forming swing's forcing (MW): every imbalance term NOT proportional to (f - f0), that is
    /// event + standing + governor + device + services + Pset, left to right. The damping is integrated implicitly by
    /// <see cref="FormingKernel.SemiImplicitStep"/> with <see cref="FormingDampingTotalMwPerHz"/>.</summary>
    public double FormingForcingMw(double eventMw) => FormingForcingMw(eventMw, DeviceMw);

    /// <summary>As <see cref="FormingForcingMw(double)"/> with <paramref name="deviceMw"/> in the device's place: the fleet
    /// engines put their units' summed fast response there (their own device slot is zeroed), exactly as
    /// <see cref="ImbalanceMw(double, double, double)"/> does for the non-forming swing (N291, October 2026, stream B5), so
    /// a one-unit, zero-stagger forming fleet adds the same numbers in the same order as the lumped forming model with
    /// that device. The one-argument form is this one with the kernel's own device state, operand for operand.</summary>
    public double FormingForcingMw(double eventMw, double deviceMw)
        => eventMw + StandingMw + GovernorMw + deviceMw + ServicesMw + FormingSetpointMw;

    /// <summary>The aggregate response (MW) the distributed swing shares across its domains:
    /// standing + governor + device + services - D * df (no event term: the event lands on its own domain).</summary>
    public double CoiResponseMw(double df) => StandingMw + GovernorMw + DeviceMw + ServicesMw - DampingMwPerHz * df;
}
