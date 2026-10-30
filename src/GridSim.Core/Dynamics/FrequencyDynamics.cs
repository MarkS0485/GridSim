using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Dynamics.Forming;
using GridSim.Core.Frequency;

namespace GridSim.Core.Dynamics;

/// <summary>
/// Settings for the centre-of-inertia frequency model.
///
/// PROVENANCE / CALIBRATION STATUS (see <c>FrequencyCalibrationTests</c>):
/// <list type="bullet">
/// <item><see cref="DroopPu"/> = 5% is the GB Grid Code governor droop  -  a firm,
///   documented figure, not a fitted one.</item>
/// <item><see cref="DampingPctPerHz"/> (~1%/Hz), <see cref="ResponsiveFraction"/>
///   (~20% headroom) and <see cref="GovernorTimeConstantS"/> (~8 s) are
///   literature-typical GB values, NOT yet fitted to a measured trip. They set the
///   nadir and the recovery shape (the initial RoCoF depends only on inertia and the
///   loss, so it is unaffected by them). Their uncertainty is roughly a factor of two.</item>
/// </list>
/// The bundled NESO sample (<c>data/neso/frequency-sample.csv</c>) is <b>ambient</b>
/// normal-operation data (band ~+/-0.1 Hz, max |RoCoF| ~0.01 Hz/s)  -  it contains no
/// clean trip step, so it cannot pin the governor/damping constants. It IS enough to
/// confirm the swing-equation RoCoF scale (the calibration test drives an ambient-sized
/// imbalance through <see cref="FrequencyDynamics.Simulate"/> and matches the measured
/// ambient RoCoF). Calibrating the nadir constants against a real loss-of-infeed trace
/// (a genuine dip to a nadir and recovery) is PENDING; do not treat the nadir/settle
/// values as validated until that trace is in hand.
/// </summary>
public sealed record DynamicsConfig
{
    /// <summary>
    /// The nominal frequency f0 (Hz) of the run: the start point, the reference every deviation is taken from, and the
    /// f0 in RoCoF = f0 * deltaP / (2E). R196 remainder (D2 nominal frequency, October 2026; Ruling R1: Mark delegated EE
    /// calls 2026-10-04): the default is the GB nominal (<see cref="FrequencyLimits.NominalHz"/>, the same 50.0 the
    /// literal gave), because this centre-of-inertia model is calibrated for GB (5 % droop, the GB response services,
    /// the GB statutory bands it is scored against) and most callers have no model. A caller studying a specific
    /// <see cref="GridSim.Core.Model.GridModel"/> sets it to that model's <see cref="GridSim.Core.Model.GridModel.FrequencyHz"/>; the CLI's
    /// <c>trip</c> verb does (a 60 Hz case's RoCoF is 1.2 times the 50 Hz figure for the same loss and inertia).
    /// The live loop (LiveSim, the web and desktop shells) keeps the GB nominal: its demand curve, bands and displays
    /// are GB (an open item in the D2 notes).
    /// </summary>
    public double NominalHz { get; init; } = FrequencyLimits.NominalHz;
    public double DroopPu { get; init; } = 0.05;             // 5% governor droop (GB Grid Code  -  documented, not fitted)
    public double DampingPctPerHz { get; init; } = 1.0;      // load self-damping, % of demand per Hz (literature-typical, UNFITTED)
    public double ResponsiveFraction { get; init; } = 0.20;  // share of generation that responds / headroom (literature-typical, UNFITTED)
    public double GovernorTimeConstantS { get; init; } = 8.0; // primary-response lag, s (literature-typical, UNFITTED)
    public double StepSeconds { get; init; } = 0.02;         // 20 ms  -  matches the NESO upsampler
    public double DurationSeconds { get; init; } = 30.0;

    // ---- TSGB (Twin Scroll Grid Balancer) device ----
    /// <summary>
    /// Real rotational spinning energy the device adds to the system E = (1/2)J*omega^2 (MW.s)  -  directly cuts RoCoF via
    /// <c>RoCoF = f0*deltaP/(2E)</c>. This is INTRINSIC inertia (actual stored kinetic energy), NOT df/dt-proportional
    /// "synthetic" emulation  -  the two are never blurred. It is summed into E; it must NEVER be merged with the
    /// fast-response term (<see cref="FastResponseMw"/>), which is a separate power injection, not stored energy.
    /// </summary>
    public double DeviceRotationalInertiaMwS { get; init; } = 0.0;

    /// <summary>Deprecated alias for <see cref="DeviceRotationalInertiaMwS"/>. The term is intrinsic (1/2)J*omega^2, not synthetic.</summary>
    [Obsolete("Renamed to DeviceRotationalInertiaMwS: this is real (1/2)J*omega^2 rotational energy, not df/dt synthetic inertia.")]
    public double SyntheticInertiaMwS
    {
        get => DeviceRotationalInertiaMwS;
        init => DeviceRotationalInertiaMwS = value;
    }
    /// <summary>Peak fast-frequency-response power the device can inject (MW)  -  arrests the nadir.</summary>
    public double FastResponseMw { get; init; } = 0.0;
    /// <summary>How quickly the fast response ramps in (s)  -  much faster than a governor.</summary>
    public double FastResponseTimeConstantS { get; init; } = 0.5;
    /// <summary>Frequency deadband before the fast response engages (Hz).</summary>
    public double FastResponseDeadbandHz { get; init; } = 0.015;

    // ---- Inertia model selection (see IInertiaModel / InertiaModelSelector) ----
    /// <summary>
    /// Which inertia/frequency model to run. Defaults to <see cref="InertiaModel.LumpedScalar"/>
    /// (the validated single-scalar COI model), so every existing caller and test is unchanged.
    /// Set to <see cref="InertiaModel.Distributed"/> to opt into the experimental multi-domain model.
    /// </summary>
    public InertiaModel InertiaModel { get; init; } = InertiaModel.LumpedScalar;

    /// <summary>Settings for the distributed model when selected (null => its degenerate defaults).</summary>
    public DistributedConfig? Distributed { get; init; } = null;

    // ---- Per-asset dynamics (authored inverter/plant classes) ----
    /// <summary>
    /// Opt into the authored per-asset dynamics fleet (the <c>pAssetFleet</c> term). Default OFF, so
    /// every existing caller and test is byte-for-byte unchanged. When on, active asset classes bound
    /// to the model contribute a swing-power term and can trip on RoCoF/vector-shift/under-voltage.
    /// With nothing bound (an empty fleet) the term is identically zero - the reduction gate.
    /// </summary>
    public bool EnableAssetDynamics { get; init; } = false;

    /// <summary>
    /// Opt into the bound PASSIVE plant (delayed OLTC taps, line/transformer thermal derate) in the live loop
    /// (R058, October 2026). Default OFF, so every existing caller and test is bit-for-bit unchanged, the same
    /// convention as <see cref="EnableAssetDynamics"/>. Independent of that flag: passive plant shapes the network
    /// (taps, ratings) on the steady-state tick and never adds a swing-power term. With nothing bound (an empty
    /// passive fleet) the loop is bit-identical to OFF - the reduction gate.
    /// </summary>
    public bool EnablePassivePlant { get; init; } = false;

    // ---- Grid-forming frequency FORMATION (opt-in; None = byte-identical to today) ----
    /// <summary>
    /// Grid-forming FORMATION mode. Default <see cref="FormingMode.None"/> runs the existing floored COI
    /// swing verbatim (byte-identical). Under Vsm/Droop the grid frequency is built on a virtual former:
    /// the denominator is <c>E_real + E_virtual</c> with NO Math.Max floor (see <see cref="FormingMode"/>).
    /// </summary>
    public FormingMode Forming { get; init; } = FormingMode.None;

    /// <summary>
    /// Virtual control inertia Ev = Hv*Sv (MW.s). A CONTROL state, NEVER real (1/2)J*omega^2 - it is kept
    /// in its own export column and never merged into the real inertia budget (E stays a pure function of
    /// system + device rotational inertia). It enters ONLY the forming RoCoF denominator E_real + Ev.
    /// Vsm only: under <see cref="FormingMode.Droop"/> it must be 0 (a pure droop former has no virtual inertia;
    /// a non-zero value is refused by name, N311).
    /// </summary>
    public double VirtualInertiaMwS { get; init; } = 0.0;

    /// <summary>Virtual former damping Dv (MW/Hz), added to load damping in the forming swing.</summary>
    public double FormingDampingMwPerHz { get; init; } = 0.0;

    /// <summary>Former deviation setpoint Pset (MW) - a forcing term, usually 0.</summary>
    public double FormingSetpointMw { get; init; } = 0.0;

    // ---- Recorded system response services ----
    /// <summary>
    /// Frequency-response services the REAL system held at the event (e.g. Dynamic Containment /
    /// Moderation / Regulation, volumes from the EAC auction record). Each is integrated as an
    /// independent first-order proportional responder. These are recorded operational facts, NOT
    /// device terms: the replay baseline strips the device but keeps these, and counterfactuals
    /// keep them too (the device is additional to what the system actually had, otherwise the
    /// comparison credits the device with the system's own response).
    /// </summary>
    public IReadOnlyList<SystemResponseService> SystemResponseServices { get; init; } = [];

    /// <summary>
    /// The recorded pre-event frequency (Hz). Null => start at <see cref="NominalHz"/> with all response
    /// states at zero (the historical behaviour). When set, the simulation starts IN EQUILIBRIUM at this
    /// frequency: governor and service lag states are pre-loaded to their static targets at the offset,
    /// and a constant standing-imbalance term balances them so df/dt(0) = 0 - the pre-event deficit the
    /// balancing mechanism was carrying. Matters because services respond to ABSOLUTE frequency: a trip
    /// landing on a system already 0.12 Hz low meets partially-deployed response with less headroom, and
    /// its steepest fall runs deeper into the delivery curves than a replay from nominal can reach
    /// (gb-2023-12-22). The device terms are NOT pre-loaded: a counterfactual device starts undeployed.
    /// </summary>
    public double? InitialFrequencyHz { get; init; } = null;
}

/// <summary>
/// One recorded frequency-response service as the swing model integrates it: the published delivery
/// curve - zero inside the deadband, a low-gain segment to <see cref="KneeFraction"/>*<see cref="Mw"/>
/// at <see cref="KneeDeviationHz"/>, the steep segment to full output at
/// <see cref="FullDeliveryDeviationHz"/>, capped beyond - first-order-lagged by
/// <see cref="TimeConstantS"/>. DC: deadband 0.015, knee (0.2, 5%), full at 0.5 Hz. DM/DR have no
/// low-gain segment: knee at the deadband with fraction 0 gives their ramp-from-deadband shape
/// (DM 0.1->0.2 Hz, DR 0.015->0.2 Hz). Knee <= 0 falls back to a droop line through zero.
/// </summary>
public sealed record SystemResponseService(
    double Mw, double DeadbandHz, double FullDeliveryDeviationHz, double TimeConstantS,
    double KneeDeviationHz = 0.0, double KneeFraction = 0.0);

/// <summary>A power step at a moment in time (negative MW = a generation loss / deficit).</summary>
/// <param name="AtSeconds">When the step happens (s from the start of the run).</param>
/// <param name="DeltaPMw">The step (MW; negative = a deficit, a loss of infeed).</param>
/// <param name="AtBus">Where it happens: the id of the bus the lost (or added) power was injected at, or null when the
/// study does not say. R060 (October 2026 review, stream B1; Ruling R1: Mark delegated EE calls 2026-10-04): only the
/// distributed inertia model reads it, to land the step on the speed domain(s) that actually feel it first (see
/// <see cref="DistributedInertiaModel"/>); the centre-of-inertia engines have no location by construction and
/// ignore it, so a located event gives them exactly the numbers an unlocated one does.</param>
public sealed record FrequencyEvent(double AtSeconds, double DeltaPMw, int? AtBus = null);

/// <summary>The shape of a frequency excursion: the headline numbers plus the full trajectory.</summary>
/// <param name="InitialRocofHzPerS">PEAK RoCoF (Hz/s) of the run, sign kept: the step value with the largest magnitude over the whole
/// trajectory (for a single step loss that is the step straight after the event, so it equals the "initial" RoCoF;
/// with staged losses, recorded services or a pre-event offset it can come later). N141 (October 2026): the name says
/// "initial" for historical reasons; the value has always been the peak, and the name is kept because it is public API
/// and the key of the frequency / H0 export JSON that the crossval sidecars read (renaming it would break those
/// contracts without moving a number). Read it as "peak RoCoF" everywhere.</param>
/// <param name="NadirHz">Lowest frequency (Hz) on the trajectory.</param>
/// <param name="NadirSeconds">Time (s) of the nadir.</param>
/// <param name="SettleHz">Frequency (Hz) at the end of the run, taken AFTER the last step's update: the time loop records
/// the point at t = k dt and then advances f by one step, so SettleHz is the frequency at Duration + dt, one step past
/// <c>Points[^1]</c> (whose Hz is the frequency at Duration). Every engine of the swing family (the forming branch,
/// AssetFrequencyDynamics, MultiDomainSwing and the per-domain <see cref="Distributed.DomainTrace"/>) uses the same
/// convention, and pinned results depend on it, so it is kept and stated here (bug hunt 2026-10-29, finding 11). A caller
/// that compares the settle value with the trace should read <c>Points[^1].Hz</c> for the frequency at Duration.</param>
/// <param name="Points">The trajectory on the run's step.</param>
public sealed record FrequencyResponse(
    double InitialRocofHzPerS,
    double NadirHz,
    double NadirSeconds,
    double SettleHz,
    IReadOnlyList<FrequencyPoint> Points)
{
    // Final batch (2026-10-10): a diverged run's NaN headline numbers are stored as the one canonical NaN, so their bytes
    // do not depend on how the JIT ordered the operands (see DetMath.CanonicalNaN). Finite and infinite values pass bit
    // for bit.
    /// <summary>Peak RoCoF (Hz/s), see the record's parameter notes; a NaN is stored canonical.</summary>
    public double InitialRocofHzPerS { get; init => field = Numerics.DetMath.CanonicalNaN(value); } = Numerics.DetMath.CanonicalNaN(InitialRocofHzPerS);

    /// <summary>Lowest frequency (Hz) on the trajectory; a NaN is stored canonical.</summary>
    public double NadirHz { get; init => field = Numerics.DetMath.CanonicalNaN(value); } = Numerics.DetMath.CanonicalNaN(NadirHz);

    /// <summary>Time (s) of the nadir; a NaN is stored canonical.</summary>
    public double NadirSeconds { get; init => field = Numerics.DetMath.CanonicalNaN(value); } = Numerics.DetMath.CanonicalNaN(NadirSeconds);

    /// <summary>Frequency (Hz) one step after the last trace point (see the record's parameter notes); a NaN is stored canonical.</summary>
    public double SettleHz { get; init => field = Numerics.DetMath.CanonicalNaN(value); } = Numerics.DetMath.CanonicalNaN(SettleHz);

    /// <summary>
    /// Highest frequency (Hz) on the trajectory: the over-frequency twin of <see cref="NadirHz"/>, over the same points
    /// (R081, October 2026 review, stream B1). A loss of demand, of an exporting interconnector or any positive event
    /// drives the frequency UP; before R081 nothing measured that, so an over-frequency excursion was scored "within
    /// limits". Computed from <see cref="Points"/> on each read (a record field would change equality); with no points it
    /// is the nadir (the start), as the nadir is.
    /// </summary>
    public double PeakHz
    {
        get
        {
            if (Points.Count == 0) return NadirHz;
            double peak = Points[0].Hz;
            for (int i = 1; i < Points.Count; i++) if (Points[i].Hz > peak) peak = Points[i].Hz;
            return peak;
        }
    }
}

/// <summary>
/// A single-mass (centre-of-inertia) swing-equation model of system frequency:
///
///   df/dt = f / (2E)  deltaP ,   E =  HS  (the spinning kinetic energy, MWs)
///
/// After a generation loss deltaP, frequency falls at a rate set entirely by the
/// inertia E  -  that's the RoCoF. Governor droop (ramping in over a few seconds) and
/// load damping then arrest the fall, producing a nadir and a settling point. Halve
/// the inertia and the RoCoF doubles and the nadir deepens  -  the whole "98 vs 260
/// GVAs" argument, runnable. Output is on the same 20 ms timeline as the NESO data,
/// so it can be replayed/visualised alongside the real trace.
/// </summary>
public static class FrequencyDynamics
{
    /// <summary>Time (s) at which a design loss is applied in the single-event studies (N-1 RoCoF screening, the TSGB
    /// sizer, the CLI / desktop / web trip views): one second of steady pre-event trace, then the step. October 2026
    /// review, R171 / R173: it was the literal 1.0 in each of them; one shared name, same value, so no number moves.</summary>
    public const double DesignEventSeconds = 1.0;

    /// <summary>
    /// The batch lumped swing: one centre-of-inertia mass of <paramref name="systemInertiaMwS"/> (plus the device's real
    /// rotational inertia) after the given events, traced on the config's step. R080 (October 2026, stream B1): every
    /// responder state and every sum comes from the one swing kernel, <see cref="SwingResponders"/>; this method keeps only
    /// the time loop, the swing step and the trace. The arithmetic is the method's own old loop operand for operand (the
    /// kernel was cut out of it), so every result is bit-identical.
    /// </summary>
    public static FrequencyResponse Simulate(
        double systemInertiaMwS, double baseDemandMw, IEnumerable<FrequencyEvent> events, DynamicsConfig? config = null)
    {
        var cfg = config ?? new DynamicsConfig();
        var resp = new SwingResponders(cfg, baseDemandMw);
        double f0 = resp.NominalHz;
        // + device real (1/2)J*omega^2 inertia; floored for the COI swing, unfloored E_real + E_virtual when forming.
        (double e, double eForming) = SwingKernel.SwingInertia(systemInertiaMwS + cfg.DeviceRotationalInertiaMwS, cfg);
        double dt = cfg.StepSeconds;
        int steps = SwingKernel.BatchStepCount(cfg);
        FrequencyEvent[] ev = SwingKernel.SortedEvents(events);

        var baseTime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var points = new List<FrequencyPoint>(steps + 1);

        double f = resp.StartFrequencyHz;
        double nadir = f, nadirT = 0;
        double peakRocof = 0;

        // The non-forming swing advances the load damping -D*df explicitly (f += rocof*dt). Explicit Euler on that term
        // alone is stable only while dt*f0*D/(2E) < 2; above it every step multiplies a frequency error by more than
        // one in magnitude, the trajectory overflows to Infinity and NaN, and nothing downstream noticed: nadir and peak
        // kept the first huge values and the run read as a result. Refuse it up front, naming the bound.
        if (!resp.Forming)
        {
            double dampingGain = dt * f0 * resp.DampingMwPerHz / (2.0 * e);
            if (!(dampingGain < 2.0))
                throw new InvalidOperationException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"Frequency swing: inertia {e:R} MW.s is too small to integrate with a {dt:R} s step and {resp.DampingMwPerHz:R} MW/Hz " +
                    $"of load damping (dt*f0*D/(2E) = {dampingGain:R}, stable only below 2; E must exceed {dt * f0 * resp.DampingMwPerHz / 4.0:R} MW.s). " +
                    $"Use a shorter step, or treat a system with this little rotating mass as having no frequency inertia."));
        }

        for (int k = 0; k <= steps; k++)
        {
            double t = k * dt;
            double df = f - f0;
            double pEvent = SwingKernel.ActiveEventMw(ev, t, dt);

            if (!resp.Forming)
            {
                // Governor, TSGB device and recorded services advance at the outer step; then the floored COI swing.
                resp.Advance(df, dt);
                double rocof = f0 / (2.0 * e) * resp.ImbalanceMw(pEvent, df);

                // A non-finite value means the integration failed (a responder or an event drove it out of range):
                // fail loud rather than return a trajectory whose nadir and peak skipped the NaNs.
                if (!double.IsFinite(f) || !double.IsFinite(rocof))
                    throw new InvalidOperationException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"Frequency swing diverged at t = {t:R} s (f = {f:R} Hz, RoCoF = {rocof:R} Hz/s); no result."));
                points.Add(new FrequencyPoint(baseTime.AddSeconds(t), f, rocof));
                if (f < nadir) { nadir = f; nadirT = t; }
                if (Math.Abs(rocof) > Math.Abs(peakRocof)) peakRocof = rocof; // the post-event spike

                f += rocof * dt;
            }
            else
            {
                // Grid-forming FORMATION: the (f-f0) damping is integrated implicitly (A-stable); the
                // proportional responders stay explicit in the forcing, so the outer step is sub-divided by
                // FormingKernel.SubStepCount to keep fast, high-gain responders stable at small E+Ev.
                double fStart = f;
                int nSub = FormingKernel.SubStepCount(dt, f0, eForming);
                double h = dt / nSub;
                double rocofReported = 0.0;
                for (int sub = 0; sub < nSub; sub++)
                {
                    double dfSub = f - f0;
                    resp.Advance(dfSub, h);
                    (double fNext, double rocof) = FormingKernel.SemiImplicitStep(
                        f, f0, eForming, resp.FormingForcingMw(pEvent), resp.FormingDampingTotalMwPerHz, h);
                    if (sub == 0) rocofReported = rocof;   // start-of-step RoCoF, matching the None convention
                    f = fNext;
                }

                if (!double.IsFinite(fStart) || !double.IsFinite(rocofReported))
                    throw new InvalidOperationException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"Grid-forming frequency swing diverged at t = {t:R} s (f = {fStart:R} Hz); no result."));
                points.Add(new FrequencyPoint(baseTime.AddSeconds(t), fStart, rocofReported));
                if (fStart < nadir) { nadir = fStart; nadirT = t; }
                if (Math.Abs(rocofReported) > Math.Abs(peakRocof)) peakRocof = rocofReported;
            }
        }

        return new FrequencyResponse(peakRocof, nadir, nadirT, f, points);
    }
}
