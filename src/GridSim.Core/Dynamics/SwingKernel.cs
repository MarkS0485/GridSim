using System.Runtime.CompilerServices;
using GridSim.Core.Dynamics.Forming;

namespace GridSim.Core.Dynamics;

/// <summary>
/// The shared per-step primitives of the swing model, factored out so <see cref="FrequencyDynamics"/>, the
/// stateful <see cref="FrequencyIntegrator"/>, and the fleet kernel all compute governor and fast-response
/// targets identically - a single source of truth for the terms that must stay consistent across the family.
/// Both are called from per-substep x per-unit inner loops (<see cref="MultiDeviceFrequencyIntegrator.Step"/>,
/// the sweep verbs run those loops thousands of times per contingency), so they inline aggressively - the
/// same convention as the dense LU's row update (<c>DenseLuSolver</c>, through <c>SimdKernels.SubtractScaledRow</c>).
/// </summary>
public static class SwingKernel
{
    // ---- Shared constants of the swing family (October 2026 review, R159 / R163, H hygiene stream). ----
    // Each of these used to be written out as a bare literal in every integrator of the family (the lumped
    // FrequencyDynamics, the stateful FrequencyIntegrator, the asset-aware and multi-device integrators, the fleet
    // kernel, the distributed MultiDomainSwing, RoCoF screening and the inertia estimator). The COI-equals-lumped
    // invariant (the distributed model's centre of inertia reproduces the lumped trajectory) depends on every copy
    // being the same number, so there is now one copy, here. A C# const is folded into the IL as the same 8-byte
    // literal the source used to spell out, so every caller computes exactly the same bits as before.

    /// <summary>Frequency deviation (Hz) at which the TSGB device's fast frequency response reaches its full output:
    /// the device's droop gain is <c>FastResponseMw / DeviceFullDeliveryHz</c>. 0.5 Hz is the statutory band edge,
    /// which is where the device is sized to be fully deployed (the same point as Dynamic Containment's full delivery).
    /// Recorded system response services carry their own full-delivery point
    /// (<see cref="SystemResponseService.FullDeliveryDeviationHz"/>) and do not use this.</summary>
    public const double DeviceFullDeliveryHz = 0.5;

    /// <summary>Floor (MW.s) on the system kinetic energy E in the floored (non-forming) COI swing,
    /// <c>RoCoF = f0 * deltaP / (2 E)</c>: it stops a fleet with no inertia from dividing by zero. The grid-forming
    /// branch deliberately has NO floor (it fails loud when nothing forms; see FormingKernel), so this applies only to
    /// the floored swing, RoCoF screening and the inertia estimator's forward check.</summary>
    public const double MinInertiaMwS = 1.0;

    /// <summary>Floor (MW) on the demand RoCoF screening uses (a screen of a case with no load still needs a figure).
    /// R163 / R080 (October 2026, stream B1): the live integrators used to floor their demand at this value while the batch
    /// engines used the raw figure; the swing family is now unified on the raw figure (<see cref="SwingResponders"/>,
    /// which refuses a negative or non-finite demand), so this floor is a screening policy only.</summary>
    public const double MinDemandMw = 1.0;

    /// <summary>
    /// R242 (October 2026 review, H hygiene stream): the explicit-Euler increment of the first-order governor lag,
    /// <c>(target - pGov) / max(step, T) * step</c>. Every other responder lag in the swing family (the TSGB fast
    /// response, the recorded services) already divided by <c>max(step, T)</c>; the governor alone divided by T, so
    /// T = 0 gave Infinity / NaN, T below step / 2 overshot and T below the step diverged. With the guard a lag shorter
    /// than the step is deadbeat (the output reaches its target in one step), the same convention as the other lags.
    ///
    /// <para>Bit-identity: for every T at or above the step, <c>Math.Max(step, T)</c> returns T itself, so the increment
    /// is computed with exactly the operations and operands of the old inline expression (subtract, divide by T, multiply
    /// by the step) and the caller's <c>pGov += increment</c> is the old statement. Every default and bundled value (T = 8 s,
    /// the scenario corners 5.6 / 11.2 s, the fitter's box from 0.5 s) is far above any step the family uses.</para>
    ///
    /// <para>Fail loud: a negative or NaN time constant is not a lag; it throws <see cref="ArgumentOutOfRangeException"/>
    /// naming the value (before, a negative T grew the governor output without bound and NaN poisoned the run).</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double GovernorLagIncrement(double pGovTarget, double pGov, double governorTimeConstantS, double stepSeconds)
    {
        if (!(governorTimeConstantS >= 0.0))
            ThrowBadGovernorTimeConstant(governorTimeConstantS);
        return (pGovTarget - pGov) / Math.Max(stepSeconds, governorTimeConstantS) * stepSeconds;
    }

    // Kept out of line so the inlined hot path stays small.
    private static void ThrowBadGovernorTimeConstant(double governorTimeConstantS) =>
        throw new ArgumentOutOfRangeException(nameof(governorTimeConstantS),
            $"The governor time constant (DynamicsConfig.GovernorTimeConstantS) must be zero or positive seconds (got {governorTimeConstantS}).");

    // ---- The engine-side helpers of the one swing kernel (R080, stream B1). The responder states live in
    // SwingResponders; these are the small pieces every engine of the family also repeated: the inertia it divides by,
    // the event power at a time, the batch step count and the live sub-step count. Each is the expression the engines
    // spelled out, operand for operand, so moving them here changes no bit. ----

    /// <summary>
    /// The swing's inertia (MW.s) for one run: the floored real inertia <c>max(MinInertiaMwS, E_real)</c> for the
    /// non-forming COI swing, and the forming denominator <c>E_real + E_virtual</c> with NO floor (it fails loud when
    /// nothing forms; <see cref="FormingKernel.EffectiveFormingInertiaMwS"/>) for a grid-forming run, else the
    /// floored value again. <paramref name="realInertiaMwS"/> is the real rotating energy the engine swings: system plus
    /// device rotational inertia in the lumped engines, base plus the fleet units' inertia in the fleet engines.
    /// </summary>
    public static (double Floored, double Forming) SwingInertia(double realInertiaMwS, DynamicsConfig cfg)
    {
        RefuseDroopWithVirtualInertia(cfg);
        double e = Math.Max(MinInertiaMwS, realInertiaMwS);
        double eForming = cfg.Forming != FormingMode.None
            ? FormingKernel.EffectiveFormingInertiaMwS(realInertiaMwS, cfg.VirtualInertiaMwS)
            : e;
        return (e, eForming);
    }

    /// <summary>
    /// N311 (October 2026, stream LF4; Ruling R1: Mark delegated EE calls 2026-10-04, ruled by main): a
    /// <see cref="FormingMode.Droop"/> former is a pure first-order P-f droop (the power-frequency loop of the droop-based
    /// REGFM_A1 model without its measurement filter) and carries NO virtual inertia, by definition. Before N311 the
    /// swing used <see cref="DynamicsConfig.VirtualInertiaMwS"/> for Droop exactly as for Vsm, so a droop run with a
    /// non-zero virtual inertia was really a VSM run under the droop label. Such a config is now refused, naming the
    /// field and the mode, instead of being run as something it is not. A droop former WITH inertia-like behaviour
    /// (a droop with a power filter) is the Vsm form (D'Arco and Suul 2014), so the caller is pointed there. Every
    /// engine of the swing family obtains its forming denominator through <see cref="SwingInertia"/>, so this one gate
    /// covers them all. Zero (+0.0 or -0.0) is the only accepted value; NaN is refused too (it is not zero).
    /// </summary>
    public static void RefuseDroopWithVirtualInertia(DynamicsConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (cfg.Forming == FormingMode.Droop && !(cfg.VirtualInertiaMwS == 0.0))
            throw new ArgumentException(
                $"DynamicsConfig.VirtualInertiaMwS = {cfg.VirtualInertiaMwS:R} MW.s with Forming = Droop: a droop former is a pure "
                + "P-f droop and carries no virtual inertia (N311). Set VirtualInertiaMwS = 0 for droop, or use Forming = Vsm "
                + "(virtual inertia plus damping) if the former should carry inertia.", nameof(cfg));
    }

    /// <summary>
    /// N275 (October 2026, stream B1; D2 open item O4; Ruling R1: Mark delegated EE calls 2026-10-04): the config a
    /// MODEL-BASED frequency entry point runs with. Every engine that takes a <see cref="GridSim.Core.Model.GridModel"/>
    /// (the inertia-model seam, the distributed model, the asset-aware lumped solve, the model-level RoCoF screen) studies
    /// THAT model, so it runs at the model's nominal frequency: RoCoF = f0 * deltaP / (2E) is 1.2 times larger at 60 Hz
    /// than at 50 Hz for the same loss and inertia, and every deviation is taken from f0.
    /// <list type="bullet">
    /// <item>No config: the defaults at the model's nominal (<c>new DynamicsConfig { NominalHz = model.FrequencyHz }</c>).
    ///   For a 50 Hz model this is the default config bit for bit (the default nominal is the same 50.0).</item>
    /// <item>A config whose <see cref="DynamicsConfig.NominalHz"/> is not exactly the model's: refused
    ///   (<see cref="ArgumentException"/>, naming both), because it would integrate the model at another system's
    ///   frequency. Before N275 it was run without a word (a 60 Hz case at the GB 50 Hz).</item>
    /// <item>A config at the model's nominal: used as given.</item>
    /// </list>
    /// </summary>
    public static DynamicsConfig ConfigForModel(GridSim.Core.Model.GridModel model, DynamicsConfig? config, string entryPoint)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (config is null) return new DynamicsConfig { NominalHz = model.FrequencyHz };
        if (!config.NominalHz.Equals(model.FrequencyHz))
            throw new ArgumentException(
                $"{entryPoint}: the config's nominal frequency ({config.NominalHz:R} Hz) is not the model's ({model.FrequencyHz:R} Hz, "
                + $"'{model.Name}'). A model-based study runs at the model's own nominal: set DynamicsConfig.NominalHz = "
                + "model.FrequencyHz (or pass no config).", nameof(config));
        return config;
    }

    /// <summary>The events in time order, the order every batch engine sums them in (a stable sort: equal times keep
    /// their given order).</summary>
    public static FrequencyEvent[] SortedEvents(IEnumerable<FrequencyEvent> events) => events.OrderBy(x => x.AtSeconds).ToArray();

    /// <summary>The summed power (MW) of every event that has happened by <paramref name="t"/>, added left to right from
    /// +0.0 in <paramref name="sortedEvents"/> order. An event has happened when
    /// <c>AtSeconds &lt;= t + dt * MultiMachineSubtransient.StageTimeToleranceSteps</c> (see <see cref="EventReached"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ActiveEventMw(FrequencyEvent[] sortedEvents, double t, double dt)
    {
        double pEvent = 0;
        foreach (FrequencyEvent x in sortedEvents) if (EventReached(x.AtSeconds, t, dt)) pEvent += x.DeltaPMw;
        return pEvent;
    }

    /// <summary>
    /// GR-540: whether an event at <paramref name="atSeconds"/> has happened by step time <paramref name="t"/> of a run
    /// stepping at <paramref name="dt"/>. The step time is formed as <c>k * dt</c>, which can round just below a time
    /// that is meant to be on the grid (an event at 0.7 s with a 0.1 s step: 7 * 0.1 is 0.7000000000000001, but other
    /// products round the other way), and a strict <c>AtSeconds &lt;= t</c> then fired the event one whole step late.
    /// The frequency family now uses the same "reached" rule as the RMS stage and trip times
    /// (<see cref="Models.MultiMachineSubtransient.StageTimeToleranceSteps"/>, one millionth of a step): far below any
    /// meaningful time, far above the rounding of <c>k * dt</c>. A NaN time is never reached (the comparison is false).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool EventReached(double atSeconds, double t, double dt)
        => atSeconds <= t + dt * Models.MultiMachineSubtransient.StageTimeToleranceSteps;

    /// <summary>The number of steps a batch run takes after its start point: round(DurationSeconds / StepSeconds); the
    /// trace holds that many plus one points (t = 0 included).</summary>
    public static int BatchStepCount(DynamicsConfig cfg) => (int)Math.Round(cfg.DurationSeconds / cfg.StepSeconds);

    /// <summary>
    /// The number of internal sub-steps a live integrator takes for one call of <paramref name="dtSeconds"/>: under
    /// grid-forming, <see cref="FormingKernel.SubStepCount"/> (bounded by the 20 ms step and the small-inertia
    /// gain limit); otherwise ceil(dt / min(dt, 20 ms)), clamped to [1, 4000], so a 20-minute step settles instead of
    /// blowing up and the per-call cost stays bounded. The 20 ms / 4000 bounds are FormingKernel's constants (the same
    /// literals the live integrators each declared).
    /// </summary>
    public static int LiveSubStepCount(double dtSeconds, bool forming, double f0, double eForming)
        => forming
            ? FormingKernel.SubStepCount(dtSeconds, f0, eForming)
            : Math.Clamp((int)Math.Ceiling(dtSeconds / Math.Min(dtSeconds, FormingKernel.MaxInternalDt)), 1, FormingKernel.MaxSubSteps);

    /// <summary>
    /// N291 (October 2026 review, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04): the step guard every live
    /// integrator (<see cref="FrequencyIntegrator"/>, <see cref="AssetAwareFrequencyIntegrator"/>,
    /// <see cref="MultiDeviceFrequencyIntegrator"/>) runs at the top of its <c>Step</c>.
    /// <para>Before, each one began with <c>if (dtSeconds &lt;= 0) return;</c>. A NaN step passes that test (every
    /// comparison with NaN is false), so it reached <see cref="LiveSubStepCount"/>, whose <c>(int)Math.Ceiling(NaN)</c> is
    /// an unspecified integer in C#, and the swing then integrated NaN-length sub-steps: the frequency became NaN and
    /// every later step stayed NaN, with no error anywhere. An infinite step gave the same through Infinity / 4000. A zero
    /// or negative step was dropped without a word, so a caller whose clock ran backwards (or a step computed as zero by
    /// mistake) saw a frozen frequency and no sign of the fault.</para>
    /// <para>Now: a step that is not a finite number of seconds above zero is refused
    /// (<see cref="ArgumentOutOfRangeException"/>, naming the engine and the value), and so is a non-finite event power
    /// (a NaN imbalance poisons the frequency the same way). Every caller in the code base passes a finite positive step
    /// (LiveSim's <c>stepMinutes * 60</c>, refused there first with its own message; the web transport ignores a
    /// non-positive pick; the tests step at 1 ms to 20 min), so no result moves: for a finite positive step the old
    /// guard fell through to exactly the code that still runs.</para>
    /// </summary>
    public static void RequireLiveStep(double dtSeconds, double eventMw, string engine)
    {
        if (!double.IsFinite(dtSeconds) || !(dtSeconds > 0.0))
            throw new ArgumentOutOfRangeException(nameof(dtSeconds), dtSeconds,
                $"{engine}.Step: the step must be a finite number of seconds above zero (got {dtSeconds:R} s). A NaN or "
                + "infinite step would make the frequency NaN for the rest of the run; a zero or negative step is not a step.");
        if (!double.IsFinite(eventMw))
            throw new ArgumentOutOfRangeException(nameof(eventMw), eventMw,
                $"{engine}.Step: the event power must be a finite number of MW (got {eventMw:R} MW); a non-finite imbalance "
                + "would make the frequency NaN for the rest of the run.");
    }

    /// <summary>
    /// Bug hunt 2026-10-29, finding 18: the explicit-Euler damping bound of the non-forming swing, checked by every live
    /// integrator before it integrates a call's sub-steps. The batch twin (<see cref="FrequencyDynamics.Simulate"/>)
    /// refuses a run with <c>h*f0*D/(2E) &gt;= 2</c>, because each step then multiplies a frequency error by more than one
    /// in magnitude and the trajectory runs off to Infinity and NaN; the live integrators had no such check and simply
    /// returned the diverging frequency. The live sub-step <paramref name="subStepSeconds"/> is what the bound is judged
    /// at (it is at most <see cref="FormingKernel.MaxInternalDt"/> unless the sub-step cap binds). A forming swing
    /// integrates its damping implicitly and is not bounded by this, so it passes unchecked. For every stable run this
    /// check reads only, so no result moves.
    /// </summary>
    public static void RequireLiveDampingStable(double subStepSeconds, double f0, double dampingMwPerHz, double e, bool forming, string engine)
    {
        if (forming) return;
        double dampingGain = subStepSeconds * f0 * dampingMwPerHz / (2.0 * e);
        if (!(dampingGain < 2.0))
            throw new InvalidOperationException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{engine}.Step: inertia {e:R} MW.s is too small to integrate with a {subStepSeconds:R} s sub-step and {dampingMwPerHz:R} MW/Hz of load damping (h*f0*D/(2E) = {dampingGain:R}, stable only below 2; E must exceed {subStepSeconds * f0 * dampingMwPerHz / 4.0:R} MW.s). Use a shorter step, or treat a system with this little rotating mass as having no frequency inertia."));
    }

    /// <summary>
    /// Bug hunt 2026-10-29, finding 18: the non-finite guard of the live integrators, run at the end of a call before the
    /// new frequency is published. The batch twin throws the moment <c>f</c> or the RoCoF goes non-finite; the live
    /// integrators used to publish Infinity and then NaN as the system frequency with no error. The responder states of
    /// the failed call have advanced, so an integrator that has thrown here must not be stepped again.
    /// </summary>
    public static void RequireFiniteLiveState(double frequencyHz, double rocofHzPerS, string engine)
    {
        if (!double.IsFinite(frequencyHz) || !double.IsFinite(rocofHzPerS))
            throw new InvalidOperationException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{engine}.Step: the frequency swing diverged (f = {frequencyHz:R} Hz, RoCoF = {rocofHzPerS:R} Hz/s); no result. The integrator must not be stepped again."));
    }

    /// <summary>Governor droop target (MW): -(deltaf/f0)/droop * responsive capacity.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double GovernorTarget(double df, double f0, double droopPu, double responsiveMw)
        => -(df / f0) / droopPu * responsiveMw;

    /// <summary>
    /// Fast-frequency-response target (MW) for one unit: a near-instant droop on deltaf, capped at +/-<paramref name="ffrMw"/>,
    /// zero inside the deadband. Full output at <see cref="DeviceFullDeliveryHz"/> (0.5 Hz) deviation
    /// (gain = ffrMw / DeviceFullDeliveryHz), matching the device model.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double FastResponseTarget(double df, double ffrMw, double deadbandHz)
        => FastResponseTarget(df, ffrMw, deadbandHz, DeviceFullDeliveryHz);

    /// <summary>
    /// The general form: a droop line through zero reaching full output at <paramref name="fullDeliveryHz"/>
    /// deviation, zero inside the deadband, capped at +/-<paramref name="ffrMw"/>. The device model is the
    /// 0.5 Hz special case; recorded system response services (DC full at 0.5 Hz, DM/DR full at 0.2 Hz per
    /// the published NESO service terms) use their own delivery gradients.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double FastResponseTarget(double df, double ffrMw, double deadbandHz, double fullDeliveryHz)
        => ffrMw > 0 && Math.Abs(df) > deadbandHz
            ? Math.Clamp(-(ffrMw / fullDeliveryHz) * df, -ffrMw, ffrMw)
            : 0.0;

    /// <summary>
    /// The full published-curve form: zero inside the deadband, then a low-gain segment rising to
    /// <paramref name="kneeFraction"/>*<paramref name="ffrMw"/> at <paramref name="kneeDeviationHz"/>,
    /// then the steep segment to full output at <paramref name="fullDeliveryHz"/>, capped beyond.
    /// This is Dynamic Containment's shape (5% by 0.2 Hz, 100% by 0.5 Hz). With
    /// <paramref name="kneeDeviationHz"/> equal to the deadband and <paramref name="kneeFraction"/> 0 it
    /// is a ramp from the deadband to full - Dynamic Moderation/Regulation's shape. With
    /// <paramref name="kneeDeviationHz"/> <= 0 it falls back to the line-through-zero form above.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double FastResponseTarget(
        double df, double ffrMw, double deadbandHz, double fullDeliveryHz,
        double kneeDeviationHz, double kneeFraction)
    {
        if (ffrMw <= 0) return 0.0;
        double adf = Math.Abs(df);
        if (adf <= deadbandHz) return 0.0;
        if (kneeDeviationHz <= 0.0)
            return Math.Clamp(-(ffrMw / fullDeliveryHz) * df, -ffrMw, ffrMw);

        double mag = adf <= kneeDeviationHz
            ? ffrMw * kneeFraction * (adf - deadbandHz) / Math.Max(1e-9, kneeDeviationHz - deadbandHz)
            : ffrMw * (kneeFraction + (1.0 - kneeFraction)
                * (adf - kneeDeviationHz) / Math.Max(1e-9, fullDeliveryHz - kneeDeviationHz));
        return -Math.Sign(df) * Math.Min(mag, ffrMw);
    }
}
