using GridSim.Core.Assets;
using GridSim.Core.Dynamics.Forming;

namespace GridSim.Core.Dynamics;

/// <summary>
/// The stateful, incremental centre-of-inertia integrator WITH the authored per-asset fleet - the
/// running-clock twin of <see cref="AssetFrequencyDynamics.Simulate"/> and the asset-aware counterpart
/// of <see cref="FrequencyIntegrator"/>. It owns the fleet and samples each substep's local frequency
/// from the COI at the previous substep's RoCoF/phase. With an empty fleet it is bit-for-bit
/// <see cref="FrequencyIntegrator"/> (the term is appended last as <c>0.0</c>) - so <see cref="Simulation.LiveSim"/>
/// only builds this when asset dynamics is enabled AND something is bound; otherwise it keeps the
/// untouched <see cref="FrequencyIntegrator"/> path.
///
/// <para>R080 (October 2026, stream B1): the responders (governor, TSGB device, recorded services, equilibrium start)
/// are the one swing kernel's, <see cref="SwingResponders"/>, the same copy every engine of the family uses. Before
/// R080 this integrator ignored the recorded services and the pre-event frequency and floored the demand at 1 MW.</para>
///
/// <para>R044 (October 2026, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04): optional SECONDARY RESTORATION of
/// the standing change the fleet imposes (the cumulative infeed its trips removed, net of the demand its protection
/// devices hold shed), see the constructor's <c>secondaryRestorationTauS</c>. The live loop turns it on with the same
/// 300 s time constant it applies to manual trips; the default (none) leaves every other caller, and the batch twin,
/// exactly as before.</para>
/// </summary>
public sealed class AssetAwareFrequencyIntegrator : IFrequencyIntegrator
{
    private readonly SwingResponders _resp;
    private readonly AssetDynamicsFleet _fleet;
    private readonly double _f0;
    private readonly double _e;
    private readonly double _eForming;
    private double _phaseDeg;
    private double _clockS;   // accumulated wall-clock seconds - drives controller detection/compute latency
    private Func<int, double>? _voltageOf, _loadingOf;   // per-bus network state from the last solve (for U/O-voltage + thermal protections)

    public double FrequencyHz { get; private set; }
    public double RocofHzPerS { get; private set; }
    public double NetFleetMw { get; private set; }
    public double TrippedDeficitMw { get; private set; }

    // R044: the secondary-restoration time constant (s; +infinity = none, the default) and the restoration state: the MW
    // that redispatch has so far put back against the fleet's standing deficit. Exactly +0.0 until something trips or a
    // protection device holds a block, and never added to the swing while it is +0.0.
    private readonly double _restorationTauS;
    private double _restoredMw;

    /// <summary>R044: the MW secondary restoration has put back so far against the fleet's standing deficit (trips net of
    /// held protection blocks): positive while it redispatches up for lost infeed, negative while it redispatches down for
    /// shed demand. Always 0 when restoration is off.</summary>
    public double RestoredMw => _restoredMw;

    /// <param name="systemInertiaMwS">Real rotating energy E (MW.s).</param>
    /// <param name="baseDemandMw">Demand that scales damping and governor capacity (MW).</param>
    /// <param name="fleet">The authored asset fleet (reset here).</param>
    /// <param name="config">Swing settings; null = defaults.</param>
    /// <param name="secondaryRestorationTauS">R044 (stream B2): the time constant (s) of secondary restoration of the
    /// fleet's standing deficit. +infinity (the default) = none, the batch-identical behaviour. A finite value R makes the
    /// redispatch follow the standing deficit X = (cumulative tripped infeed) - (held protection injection) as a first-order
    /// lag, dR/dt = (X - R) / tau, integrated exactly per sub-step (R += (X - R) * (1 - exp(-h / tau))), and adds R to the
    /// imbalance: so a trip or an LFDD shed is restored over the time constant exactly as LiveSim restores a manual trip
    /// (its _tripDeficitMw decays by exp(-dt / tau), the same superposition of decaying steps). Refused when it is NaN, zero
    /// or negative.</param>
    public AssetAwareFrequencyIntegrator(
        double systemInertiaMwS, double baseDemandMw, AssetDynamicsFleet fleet, DynamicsConfig? config = null,
        double secondaryRestorationTauS = double.PositiveInfinity)
    {
        if (!(secondaryRestorationTauS > 0.0))
            throw new ArgumentOutOfRangeException(nameof(secondaryRestorationTauS), secondaryRestorationTauS,
                "The secondary-restoration time constant must be a number of seconds above zero (+infinity for none).");
        _restorationTauS = secondaryRestorationTauS;
        DynamicsConfig cfg = config ?? new DynamicsConfig();
        _fleet = fleet;
        _fleet.Reset();
        _resp = new SwingResponders(cfg, baseDemandMw);
        _f0 = _resp.NominalHz;
        (_e, _eForming) = SwingKernel.SwingInertia(systemInertiaMwS + cfg.DeviceRotationalInertiaMwS, cfg);
        FrequencyHz = _resp.StartFrequencyHz;
    }

    public AssetDynamicsFleet Fleet => _fleet;

    /// <summary>
    /// Supply the per-bus voltage (p.u.) and per-bus loading fraction from the latest network solve, so
    /// under/over-voltage and thermal-overload protections observe real values. Held constant across the
    /// fast frequency sub-steps within a tick (the solve is much slower than the swing step). Both suppliers null is
    /// the pure-COI behaviour (no network state). GR-625: give both or neither; one alone used to fabricate the other
    /// (voltage 1.0 pu or loading 0.0) as if it had been measured, the placeholder D-DOC-038 removed elsewhere.
    /// </summary>
    public void SetNetworkState(Func<int, double>? voltageOf, Func<int, double>? loadingOf)
    {
        if ((voltageOf is null) != (loadingOf is null))
            throw new ArgumentException(
                "SetNetworkState needs both the voltage and the loading supplier, or neither (no network state); one alone " +
                "would stand a fabricated value in for the other.", voltageOf is null ? nameof(voltageOf) : nameof(loadingOf));
        _voltageOf = voltageOf;
        _loadingOf = loadingOf;
    }

    /// <summary>Advance the frequency by <paramref name="dtSeconds"/> under net imbalance <paramref name="eventMw"/> (negative = deficit).</summary>
    public void Step(double dtSeconds, double eventMw)
    {
        // N291 (October 2026, stream B2): a NaN, infinite, zero or negative step (or a non-finite event power) is refused
        // here; it used to be dropped (zero / negative) or to turn the frequency NaN for good (NaN / infinity).
        SwingKernel.RequireLiveStep(dtSeconds, eventMw, nameof(AssetAwareFrequencyIntegrator));
        int nSub = SwingKernel.LiveSubStepCount(dtSeconds, _resp.Forming, _f0, _eForming);
        double h = dtSeconds / nSub;
        // Bug hunt 2026-10-29, finding 18: the batch twin's explicit-Euler damping bound, at this call's sub-step.
        SwingKernel.RequireLiveDampingStable(h, _f0, _resp.DampingMwPerHz, _e, _resp.Forming, nameof(AssetAwareFrequencyIntegrator));
        double f = FrequencyHz;
        // R044: the exact first-order-lag gain over one sub-step, 1 - exp(-h / tau) (as -expm1 for accuracy at small
        // h / tau); 0 when restoration is off, which leaves _restoredMw at +0.0 for good.
        bool restoring = !double.IsPositiveInfinity(_restorationTauS);
        double restoreGain = restoring ? -DetMath.Expm1(-h / _restorationTauS) : 0.0;

        for (int s = 0; s < nSub; s++)
        {
            double df = f - _f0;
            _resp.Advance(df, h);

            if (!_resp.Forming)
            {
                double pBase = _resp.ImbalanceMw(eventMw, df);
                double pAssetFleet = 0.0, tripDeficit = 0.0, protectionMw = 0.0;
                if (!_fleet.IsEmpty)
                {
                    double rocofPredict = _f0 / (2.0 * _e) * pBase;
                    // Pass the ACCUMULATED clock (not 0): the controllers' detection/compute latency and
                    // vector-shift all key off ClockSeconds - a frozen clock silently disables them in the
                    // live twin (they still trip on RoCoF, so the bug is selective and silent).
                    AssetFleetStep fs = _fleet.StepCoi(f, rocofPredict, _phaseDeg, h, _clockS, _voltageOf, _loadingOf);
                    pAssetFleet = fs.NetActivePowerMw;
                    tripDeficit = fs.TrippedDeficitMw;
                    protectionMw = fs.ProtectionInjectionMw;
                    NetFleetMw = pAssetFleet;
                    TrippedDeficitMw = tripDeficit;
                }

                double pImbalance = pBase + pAssetFleet - tripDeficit;
                // R044: secondary restoration put back so far (added only once it is non-zero, so a run with nothing
                // tripped or shed, and every run without restoration, adds exactly the terms it always did).
                if (_restoredMw != 0.0) pImbalance += _restoredMw;
                if (restoring) AdvanceRestoration(tripDeficit - protectionMw, restoreGain);
                double rocof = _f0 / (2.0 * _e) * pImbalance;
                _phaseDeg += 360.0 * df * h;
                _clockS += h;
                f += rocof * h;
                RocofHzPerS = rocof;
            }
            else
            {
                // Forming: the fleet predictor RoCoF is recomputed with the UNFLOORED forming denominator
                // (eForming) so grid-following assets trip on the true formed RoCoF, not the Math.Max(1,E) floor.
                double pForcingBase = _resp.FormingForcingMw(eventMw);
                double dampTotal = _resp.FormingDampingTotalMwPerHz;
                double pAssetFleet = 0.0, tripDeficit = 0.0, protectionMw = 0.0;
                if (!_fleet.IsEmpty)
                {
                    double rocofPredict = _f0 / (2.0 * _eForming) * (pForcingBase - dampTotal * df);
                    AssetFleetStep fs = _fleet.StepCoi(f, rocofPredict, _phaseDeg, h, _clockS, _voltageOf, _loadingOf);
                    pAssetFleet = fs.NetActivePowerMw;
                    tripDeficit = fs.TrippedDeficitMw;
                    protectionMw = fs.ProtectionInjectionMw;
                    NetFleetMw = pAssetFleet;
                    TrippedDeficitMw = tripDeficit;
                }

                double pForcing = pForcingBase + pAssetFleet - tripDeficit;
                if (_restoredMw != 0.0) pForcing += _restoredMw;   // R044, as in the non-forming branch
                if (restoring) AdvanceRestoration(tripDeficit - protectionMw, restoreGain);
                (double fNext, double rocof) = FormingKernel.SemiImplicitStep(f, _f0, _eForming, pForcing, dampTotal, h);
                _phaseDeg += 360.0 * df * h;
                _clockS += h;
                f = fNext;
                RocofHzPerS = rocof;
            }
        }
        SwingKernel.RequireFiniteLiveState(f, RocofHzPerS, nameof(AssetAwareFrequencyIntegrator));   // finding 18: fail loud, never publish NaN
        FrequencyHz = f;
    }

    /// <summary>
    /// R044: one sub-step of secondary restoration towards the standing deficit <paramref name="standingDeficitMw"/>
    /// (cumulative tripped infeed minus held protection injection): R += (X - R) * gain, the exact zero-order-hold step of
    /// dR/dt = (X - R) / tau. Applied AFTER this sub-step's imbalance is formed, so the restoration acts from the next
    /// sub-step on (the same order LiveSim gives a manual trip). With X = +0.0 and R = +0.0 the update adds +0.0 * gain =
    /// +0.0, so R stays exactly +0.0 until something trips or is shed.
    /// </summary>
    private void AdvanceRestoration(double standingDeficitMw, double gain) =>
        _restoredMw += (standingDeficitMw - _restoredMw) * gain;
}
