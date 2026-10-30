using GridSim.Core.Dynamics.Forming;

namespace GridSim.Core.Dynamics;

/// <summary>
/// A <b>stateful, incremental</b> centre-of-inertia swing-equation integrator - the running-clock
/// twin of <see cref="FrequencyDynamics.Simulate"/>. Where <c>Simulate</c> pre-computes a fixed
/// trace, this advances one <see cref="Step"/> at a time by an arbitrary deltat, so the live dashboard
/// can drive system frequency at whatever time-step the user chooses (1 ms -> 20 min) and watch a
/// real RoCoF/nadir/recovery play out instead of a canned animation.
///
/// <para>The per-step physics are identical to <c>Simulate</c>, because both take them from the one swing kernel,
/// <see cref="SwingResponders"/> (October 2026 review, R080, stream B1): <c>df/dt = f0/(2E)*deltaP</c> with governor
/// droop (lagged), load damping, the optional TSGB fast response, the recorded system response services
/// (<see cref="DynamicsConfig.SystemResponseServices"/>) and an equilibrium start at the recorded pre-event frequency
/// (<see cref="DynamicsConfig.InitialFrequencyHz"/>). Before R080 this integrator silently ignored the services and the
/// pre-event frequency, and floored the demand at 1 MW where Simulate did not; the demand is now used as given (the
/// live loop floors it itself, so no live result moves). Large deltat is integrated in bounded internal sub-steps so
/// explicit Euler stays stable - a 20-minute step doesn't blow up, it just settles.</para>
/// </summary>
public sealed class FrequencyIntegrator : IFrequencyIntegrator
{
    private readonly SwingResponders _resp;
    private readonly double _f0;
    private readonly double _e;        // spinning energy E = H*S (+ device), MW.s, floored
    private readonly double _eForming; // forming denominator E_real + E_virtual, MW.s (unfloored)

    public double FrequencyHz { get; private set; }
    public double RocofHzPerS { get; private set; }

    public FrequencyIntegrator(double systemInertiaMwS, double baseDemandMw, DynamicsConfig? config = null)
    {
        DynamicsConfig cfg = config ?? new DynamicsConfig();
        _resp = new SwingResponders(cfg, baseDemandMw);
        _f0 = _resp.NominalHz;
        (_e, _eForming) = SwingKernel.SwingInertia(systemInertiaMwS + cfg.DeviceRotationalInertiaMwS, cfg);
        FrequencyHz = _resp.StartFrequencyHz;
    }

    /// <summary>
    /// Advance the frequency by <paramref name="dtSeconds"/> under a net power imbalance
    /// <paramref name="eventMw"/> (MW; <b>negative = deficit</b>, e.g. a loss of infeed). Governor,
    /// damping, fast-response and service state carry over between calls. Sub-steps internally for stability.
    /// </summary>
    public void Step(double dtSeconds, double eventMw)
    {
        // N291 (October 2026, stream B2): a NaN, infinite, zero or negative step (or a non-finite event power) is refused
        // here; it used to be dropped (zero / negative) or to turn the frequency NaN for good (NaN / infinity).
        SwingKernel.RequireLiveStep(dtSeconds, eventMw, nameof(FrequencyIntegrator));
        int nSub = SwingKernel.LiveSubStepCount(dtSeconds, _resp.Forming, _f0, _eForming);
        double h = dtSeconds / nSub;
        // Bug hunt 2026-10-29, finding 18: the batch twin's explicit-Euler damping bound, at this call's sub-step.
        SwingKernel.RequireLiveDampingStable(h, _f0, _resp.DampingMwPerHz, _e, _resp.Forming, nameof(FrequencyIntegrator));
        double f = FrequencyHz;

        for (int s = 0; s < nSub; s++)
        {
            double df = f - _f0;
            _resp.Advance(df, h);

            if (!_resp.Forming)
            {
                double rocof = _f0 / (2.0 * _e) * _resp.ImbalanceMw(eventMw, df);
                f += rocof * h;
                RocofHzPerS = rocof;
            }
            else
            {
                (double fNext, double rocof) = FormingKernel.SemiImplicitStep(
                    f, _f0, _eForming, _resp.FormingForcingMw(eventMw), _resp.FormingDampingTotalMwPerHz, h);
                f = fNext;
                RocofHzPerS = rocof;
            }
        }
        SwingKernel.RequireFiniteLiveState(f, RocofHzPerS, nameof(FrequencyIntegrator));   // finding 18: fail loud, never publish NaN
        FrequencyHz = f;
    }
}
