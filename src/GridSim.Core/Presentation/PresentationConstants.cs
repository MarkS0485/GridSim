namespace GridSim.Core.Presentation;

/// <summary>
/// N023: the display thresholds and narrative constants the web dashboard (<c>GridSim.Web</c>
/// <c>Projection/PresentationLimits</c>) and the desktop app (<c>GridSim.Wpf</c> view models and renderer) share. Before
/// N023 each surface held its own copy under the same name, kept equal only by <c>PresentationConstantsAntiDriftTests</c>
/// reading both sources; now this is the one copy and both surfaces refer to it.
///
/// <para>Every value is exactly the literal both copies carried (moved, not changed); PresentationConstantsTests pins
/// each one. Pure presentation: these decide how a solved frame is drawn and narrated, never what is solved.</para>
///
/// <para>Constants only one surface uses stay with that surface. Values that already had a Core home are not repeated
/// here: the NESO RP1 purchased-time ladder is <see cref="FrequencyRiskPresenter.GradeNormalS"/> /
/// <see cref="FrequencyRiskPresenter.GradeAdvisoryS"/>, the frequency-event band and idle-interconnector cut-off are in
/// <see cref="StatusLampRules"/>.</para>
/// </summary>
public static class PresentationConstants
{
    // ---------------- network map and experimental models ----------------

    /// <summary>A branch with no thermal rating (RateA = 0) is drawn and loaded against this assumed MW figure, so an
    /// unrated circuit still shows a loading fraction (|P| / 2,000 MW, capped at 1). A display assumption only: no
    /// verdict is drawn from it.</summary>
    public const double UnratedBranchNominalMw = 2000.0;

    /// <summary>Above this many buses the heavy per-frame experimental models (Bialek tracing, carbon tracing) and the
    /// on-demand harmonics study are skipped, so a large network's per-frame cost stays bounded.</summary>
    public const int HeavyModelBusLimit = 1500;

    /// <summary>Signed mode-shape amplitude a machine must exceed (either sign) to be listed on one side of the
    /// dominant inter-area mode's "(these) vs (those)" split; machines nearer zero sit on the node and are left out.</summary>
    public const double ModeShapeSplitAmplitude = 0.05;

    // ---------------- rotating-machine narrative ----------------

    /// <summary>A two-pole synchronous machine's speed (rpm) at nominal frequency: 60 x 50 Hz / 1 pole pair. The
    /// "grid as one machine" rpm read-outs scale it by f / f_nominal.</summary>
    public const double TwoPoleSyncRpm = 3000.0;

    /// <summary>N273 (October 2026 review, stream B2): a two-pole machine's synchronous speed (rpm) at
    /// <paramref name="nominalHz"/>: 60 x f / 1 pole pair, so 3,000 rpm at 50 Hz (60.0 x 50.0 is exactly 3000.0, the
    /// constant above) and 3,600 rpm at 60 Hz. The live read-outs scale it by f / f_nominal of the frame's own model.</summary>
    public static double TwoPoleSyncRpmAt(double nominalHz) => 60.0 * nominalHz;

    /// <summary>Droop-implied surplus or deficit (MW) beyond which THE MACHINE narrates "too much push" / "not enough
    /// push" instead of "in balance".</summary>
    public const double TorqueNarrativeBandMw = 200.0;

    /// <summary>Small weight (MW) added to every bus's demand in the demand-weighted average voltage, so buses with no
    /// load still count and an all-zero-demand case does not divide by zero.</summary>
    public const double VoltageWeightEpsilonMw = 1e-6;

    /// <summary>OLTC deadband (pu) assumed for the regulation-stress dial when the model has no OLTCs at all (the
    /// dial then reads zero anyway, as there are no taps to average).</summary>
    public const double DefaultOltcDeadbandPu = 0.01;

    /// <summary>A tap within this fraction of one tap step of its travel limit counts as AT LIMIT.</summary>
    public const double TapAtLimitStepFraction = 0.5;

    /// <summary>The narrative grid battery's state of charge (%) at the start of a run and after a timeline break.</summary>
    public const double BessInitialSocPct = 50.0;

    // ---------------- HUMAN SCALE anchors ----------------

    /// <summary>Radius (m) of a large steam turbine's last-stage LP blade tip, for the blade-tip speed read-out.</summary>
    public const double LpBladeTipRadiusM = 0.9;

    /// <summary>Speed of sound in air (m/s) at about 20 C, for the blade-tip Mach number.</summary>
    public const double SpeedOfSoundMs = 343.0;

    /// <summary>|RoCoF| (Hz/s) below which the stored rotor energy reads COASTING rather than DELIVERING or SOAKING UP.</summary>
    public const double RegenDeadbandHzPerS = 1e-4;

    // ---------------- GENERATION & INERTIA ----------------

    /// <summary>Synchronous share of generation below which the web GEN &amp; INERTIA note reads converter-dominated and
    /// the desktop GENERATION tab shows LOW INERTIA REGIME (the desktop name is <c>LowInertiaSyncFraction</c>).</summary>
    public const double SyncShareModerateFraction = 0.40;

    // ---------------- MECHANICAL ----------------

    /// <summary>Unit ramp (MW/min, either way) inside which the ramp read-out is coloured neutral (holding).</summary>
    public const double RampDeadbandMwPerMin = 0.5;

    /// <summary>Machine stress (% of its capability) at or above which its risk reads LIMIT (the web works in %).</summary>
    public const double StressLimitPct = 95.0;

    /// <summary>Machine stress (%) at or above which its risk reads NEAR (the web works in %).</summary>
    public const double StressNearPct = 85.0;

    /// <summary><see cref="StressLimitPct"/> as a fraction of capability (the desktop works in fractions). Written as
    /// the literal 0.95, which is the double nearest 95 / 100, so it equals <c>StressLimitPct / 100.0</c> exactly
    /// (pinned by a test).</summary>
    public const double StressLimitFraction = 0.95;

    /// <summary><see cref="StressNearPct"/> as a fraction (0.85 = the double nearest 85 / 100; pinned by a test).</summary>
    public const double StressNearFraction = 0.85;
}
