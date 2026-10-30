using GridSim.Core.Assets;

namespace GridSim.Core.Dynamics.SmallSignal;

/// <summary>
/// A linearised small-signal block for one inverter, read from its authored <see cref="AssetClass"/>.
/// These are the linear archetypes of the nonlinear time-domain controllers - the nonlinear code
/// (<see cref="GridFormingInverter"/>, <see cref="GridFollowingInverter"/>) is UNTOUCHED; the
/// correspondence is documented per term. Deadband, arming, SoC clamps and trips are nonlinearities
/// dropped about an armed / unsaturated / untripped operating point, which is exactly the pre-trip
/// small-signal (in)stability question.
/// </summary>
/// <param name="Bus">Terminal bus id the block injects at.</param>
/// <param name="StateKinds">Kind tag per internal state (real vs synthetic separation on the wire).</param>
/// <param name="Aself">Internal dynamics d(x)/dt = Aself x (before network coupling).</param>
/// <param name="FreqInputRow">Per-state coefficient on the measured bus frequency deviation d(f) (Hz).</param>
/// <param name="RocofInputRow">Per-state coefficient on the measured bus df/dt (Hz/s) - the synthetic-inertia leg.</param>
/// <param name="PowerOutputRow">Per-state contribution to injected active power d(P) at the bus (MW).</param>
/// <param name="SyntheticInertiaMwsPerHz">The GFM df/dt (synthetic-inertia) coefficient, surfaced so the
/// caller can assert it is NEVER summed into a machine H/E. 0 for a pure-droop GFM or a GFL block.</param>
public sealed record InverterLinearBlock(
    int Bus,
    StateKind[] StateKinds,
    double[,] Aself,
    double[] FreqInputRow,
    double[] RocofInputRow,
    double[] PowerOutputRow,
    double SyntheticInertiaMwsPerHz)
{
    /// <summary>Number of internal states this block adds.</summary>
    public int Order => StateKinds.Length;
}

/// <summary>
/// Builds <see cref="InverterLinearBlock"/>s from authored asset classes. Both archetypes floor the
/// actuator time constant with <c>max(1e-3, actuateTauS)</c>, mirroring the code's
/// <c>Math.Max(dt, actuateTauS)</c> guard, so a zero-tau class never divides by zero.
/// </summary>
public static class LinearInverterModel
{
    // N345 (CF3, October 2026): the fall-back value of each class parameter this code reads when the class does not
    // set it, named so the v2 kind specs (schemas/v2/kinds) cite it by symbol instead of quoting the source line.
    // Each constant is the literal it replaces, character for character, so no number moves.
    internal const double DefaultDampingMwPerHz = 0.0;   // AssetParams.DampingMwPerHz
    internal const double DefaultDetectionLatencyS = 0.0;   // AssetParams.DetectionLatencyS
    internal const double DefaultSynInertiaMwsPerHz = 0.0;   // AssetParams.SynInertiaMwsPerHz

    private const double TauFloor = 1e-3;

    /// <summary>
    /// FAITHFUL reduction of the grid-forming law <c>p = -synInertia*(df/dt) - damping*(f-f0)</c> through
    /// the first-order actuator lag (<see cref="GridFormingInverter"/> lines 84-85): a SINGLE first-order
    /// actuator state with df and df/dt FEEDTHROUGH into the terminal-bus power balance. The GFM forms no
    /// internal angle (it reacts to the MEASURED bus frequency), and it reduces GRACEFULLY to pure droop at
    /// <c>synInertia = 0</c> (the code's default) - there is no virtual-mass division, so no singularity.
    /// The df/dt (synthetic-inertia) coefficient is tagged and returned separately; it is NEVER folded into
    /// a machine's H or EMF.
    /// </summary>
    /// <param name="cls">The grid-forming asset class (its SynInertia, Damping and ActuateTau parameters).</param>
    /// <param name="bus">The terminal bus.</param>
    /// <param name="ratedMw">Accepted for symmetry with <see cref="ForGfl"/> and NOT used (R085): the class's damping
    /// and synthetic-inertia coefficients are authored in absolute MW/Hz and MW.s/Hz, so they need no rating. Scaling
    /// them by the rating would change every grid-forming eigenvalue and belongs to the deferred frequency / inertia
    /// pass, so it is recorded there rather than done here.</param>
    /// <param name="f0">Accepted for symmetry and NOT used: the law acts on the frequency DEVIATION df and its rate,
    /// which carry no nominal frequency.</param>
    public static InverterLinearBlock ForGfm(AssetClass cls, int bus, double ratedMw, double f0)
    {
        double synInertia = cls.Param(AssetParams.SynInertiaMwsPerHz, DefaultSynInertiaMwsPerHz);   // MW.s/Hz (df/dt leg)
        double damping = cls.Param(AssetParams.DampingMwPerHz, DefaultDampingMwPerHz);          // MW/Hz (droop leg)
        double tau = Math.Max(TauFloor, cls.Param(AssetParams.ActuateTauS, ConverterDefaults.SmallSignalGfmActuateTauSeconds));
        _ = ratedMw;   // R085: unused by design (absolute MW/Hz coefficients); see the parameter notes
        _ = f0;        // unused: the law acts on deviations only

        // Single actuator state p_lag: p_lag' = (target - p_lag)/tau, target = -synInertia*rocof - damping*df.
        var aself = new double[,] { { -1.0 / tau } };
        var freqIn = new[] { -damping / tau };        // df leg
        var rocofIn = new[] { -synInertia / tau };    // df/dt leg (synthetic inertia)
        var powerOut = new[] { 1.0 };                 // p_lag IS the injected MW
        return new InverterLinearBlock(bus,
            new[] { StateKind.DroopActuatorLag }, aself, freqIn, rocofIn, powerOut, synInertia);
    }

    /// <summary>
    /// Grid-following block: a 2nd-order SRF-PLL frequency estimator feeding a droop leg through the
    /// actuator lag. The time-domain GFL carries a transport DELAY (<c>detectionLatencyS</c>), not PLL
    /// gains, so this is a DOCUMENTED ARCHETYPE SUBSTITUTION (like a Pade of the delay), not a
    /// linearisation of the exact code - the PLL bandwidth comes from the optional
    /// <see cref="AssetParams.PllBandwidthHz"/>, defaulting to <c>1/(2*pi*detectionLatencyS)</c>. States:
    /// [PllSpeed = f_est, PllFrequencyRate = its rate, DroopActuatorLag] (R085: the rate was labelled PllAngle).
    ///
    /// <para>R138 (October 2026 review; stream B5; Ruling R1: Mark delegated EE calls 2026-10-04): the bandwidth and the
    /// damping follow one rule shared with the impedance and EMT models. Bandwidth: the class's
    /// <see cref="AssetParams.PllBandwidthHz"/> when authored; else the delay stand-in 1/(2 pi latency) when the class gives
    /// a usable detection latency; else the one PLL default <see cref="ConverterDefaults.PllBandwidthHz"/> (20 Hz; was a
    /// separate 5 Hz). Damping: the class's <see cref="AssetParams.PllDampingRatio"/> when authored (above 0; it used to be
    /// ignored here while the impedance model read it); else the critically damped
    /// <see cref="ConverterDefaults.SmallSignalDelayEstimatorDampingRatio"/> for the delay stand-in (a delay has no
    /// overshoot); else the one PLL default <see cref="ConverterDefaults.PllDampingRatio"/> (0.707; was 1.0 for every
    /// case). A class with a latency and no PLL parameters, which is every bundled grid-following class, takes exactly the
    /// old bandwidth and damping, so its block is bit-identical.</para>
    /// </summary>
    /// <param name="cls">The grid-following asset class (its DroopGain, ActuateTau, DetectionLatency and optional
    /// PllBandwidth parameters).</param>
    /// <param name="bus">The terminal bus.</param>
    /// <param name="ratedMw">The unit's rating (MW): the per-MW droop gain is scaled by it.</param>
    /// <param name="f0">Accepted for symmetry and NOT used (R085): the PLL estimator archetype filters the frequency
    /// DEVIATION df, whose transfer function (wn^2 / (s^2 + 2 zeta wn s + wn^2)) does not involve the nominal
    /// frequency.</param>
    public static InverterLinearBlock ForGfl(AssetClass cls, int bus, double ratedMw, double f0)
    {
        // GR-567: the per-MW droop is scaled by the rating, so a zero, negative or non-finite rating used to give a block
        // with no droop at all (Math.Max(0, rating)) and the eigenvalues of a converter that does nothing, without a word.
        // It is refused instead. (A record with no rating takes Generator.MBase's default of 100 MVA, which this cannot
        // tell from an authored 100; that stays as documented in technical ch. 36.)
        if (!(double.IsFinite(ratedMw) && ratedMw > 0.0))
            throw new ArgumentOutOfRangeException(nameof(ratedMw), ratedMw,
                $"small-signal grid-following block at bus {bus}: the unit rating (MBase) must be a finite positive MVA; " +
                "the droop gain is per MW of rating, so a zero or negative rating would silently give no droop.");
        double droopGain = cls.Param(AssetParams.DroopGainMwPerHzPerMw, ConverterDefaults.SmallSignalGflDroopMwPerHzPerMw) * ratedMw; // MW/Hz
        double tau = Math.Max(TauFloor, cls.Param(AssetParams.ActuateTauS, ConverterDefaults.SmallSignalGflActuateTauSeconds));
        double detect = cls.Param(AssetParams.DetectionLatencyS, DefaultDetectionLatencyS);
        // R138: the delay stand-in applies only when the class authors no PLL bandwidth and gives a usable latency.
        bool delayStandIn = !cls.Params.ContainsKey(AssetParams.PllBandwidthHz)
            && detect > ConverterDefaults.SmallSignalLatencyFloorSeconds;
        double bwHz = cls.Param(AssetParams.PllBandwidthHz, delayStandIn
            ? 1.0 / (2.0 * Math.PI * detect)
            : ConverterDefaults.PllBandwidthHz);
        double wn = 2.0 * Math.PI * Math.Max(ConverterDefaults.SmallSignalPllBandwidthFloorHz, bwHz);
        // R138: the class's authored PLL damping when it gives one (above 0), else critically damped for the delay
        // stand-in, else the one PLL default.
        double authoredZeta = cls.Param(AssetParams.PllDampingRatio, 0.0);
        double zeta = authoredZeta > 0.0 ? authoredZeta
            : delayStandIn ? ConverterDefaults.SmallSignalDelayEstimatorDampingRatio
            : ConverterDefaults.PllDampingRatio;
        _ = f0;   // R085: unused by design (the estimator acts on the deviation df); see the parameter notes

        // f_est/df = wn^2 / (s^2 + 2 zeta wn s + wn^2), controllable canonical form:
        //   s1' = s2                                  (s1 = f_est)
        //   s2' = -wn^2 s1 - 2 zeta wn s2 + wn^2 df
        //   p_lag' = -(droopGain/tau) s1 - p_lag/tau
        var aself = new double[,]
        {
            { 0.0,          1.0,             0.0     },
            { -wn * wn,     -2.0 * zeta * wn, 0.0     },
            { -droopGain / tau, 0.0,          -1.0 / tau },
        };
        var freqIn = new[] { 0.0, wn * wn, 0.0 };  // df drives the estimator's s2'
        var rocofIn = new[] { 0.0, 0.0, 0.0 };     // rocof leg folded through the PLL (archetype)
        var powerOut = new[] { 0.0, 0.0, 1.0 };    // p_lag IS the injected MW
        return new InverterLinearBlock(bus,
            new[] { StateKind.PllSpeed, StateKind.PllFrequencyRate, StateKind.DroopActuatorLag },   // R085: s2 = d(f_est)/dt, not an angle
            aself, freqIn, rocofIn, powerOut, SyntheticInertiaMwsPerHz: 0.0);
    }
}
