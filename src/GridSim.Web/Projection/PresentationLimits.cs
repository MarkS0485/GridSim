namespace GridSim.Web.Projection;

/// <summary>
/// The web presentation layer's named display thresholds and narrative constants (R203-R208, October 2026). Each one
/// used to be a bare literal, several repeated across files (the 2,000 MW pseudo-rating in the frame projector and the
/// SCADA service, the 1,500-bus heavy-model gate in the projector and the session, 50 Hz / 3,000 rpm everywhere).
/// <para>Every value here is exactly the literal it replaced (named, not changed). Where a value is a physical
/// standard Core already names, the code references Core instead (<c>FrequencyLimits.NominalHz</c>,
/// <c>ScadaThresholds.VoltageBandLowPu</c>, <c>MechanicalThresholds.MinSyncMachineMw</c>,
/// <c>EnergyEconomicsConstants.HomeKwhPerDay</c>, <c>CredibleLossBasis.FictionLossMw</c>).</para>
/// <para>N023: the constants the WPF app shares (same names) now live once in
/// <c>GridSim.Core.Presentation.PresentationConstants</c>; the names here refer to that copy so existing web callers
/// are unchanged, and the WPF twins refer to it too. The rest are web-only.</para>
/// Pure presentation constants: they decide how a solved frame is drawn and narrated, never what is solved.
/// </summary>
public static class PresentationLimits
{
    // ---------------- network map and SCADA ----------------

    /// <summary>A branch with no thermal rating (RateA = 0) is drawn and loaded against this assumed MW figure, so an
    /// unrated circuit still shows a loading fraction (|P| / 2,000 MW, capped at 1). A display assumption only: no
    /// verdict is drawn from it.</summary>
    public const double UnratedBranchNominalMw = GridSim.Core.Presentation.PresentationConstants.UnratedBranchNominalMw;

    /// <summary>Above this many buses the heavy per-frame experimental models (Bialek tracing, carbon tracing) and the
    /// on-demand harmonics study are skipped, so a large network's per-frame cost stays bounded.</summary>
    public const int HeavyModelBusLimit = GridSim.Core.Presentation.PresentationConstants.HeavyModelBusLimit;

    /// <summary>Signed mode-shape amplitude a machine must exceed (either sign) to be listed on one side of the
    /// dominant inter-area mode's "(these) vs (those)" split; machines nearer zero sit on the node and are left out.</summary>
    public const double ModeShapeSplitAmplitude = GridSim.Core.Presentation.PresentationConstants.ModeShapeSplitAmplitude;

    // ---------------- rotating-machine narrative ----------------

    /// <summary>A two-pole synchronous machine's speed (rpm) at nominal frequency: 60 x 50 Hz / 1 pole pair. The
    /// "grid as one machine" rpm read-outs scale it by f / f_nominal.</summary>
    public const double TwoPoleSyncRpm = GridSim.Core.Presentation.PresentationConstants.TwoPoleSyncRpm;

    /// <summary>Droop-implied surplus or deficit (MW) beyond which THE MACHINE narrates "too much push" / "not enough
    /// push" instead of "in balance".</summary>
    public const double TorqueNarrativeBandMw = GridSim.Core.Presentation.PresentationConstants.TorqueNarrativeBandMw;

    /// <summary>Small weight (MW) added to every bus's demand in the demand-weighted average voltage, so buses with no
    /// load still count and an all-zero-demand case does not divide by zero.</summary>
    public const double VoltageWeightEpsilonMw = GridSim.Core.Presentation.PresentationConstants.VoltageWeightEpsilonMw;

    /// <summary>OLTC deadband (pu) assumed for the regulation-stress dial when the model has no OLTCs at all (the
    /// dial then reads zero anyway, as there are no taps to average).</summary>
    public const double DefaultOltcDeadbandPu = GridSim.Core.Presentation.PresentationConstants.DefaultOltcDeadbandPu;

    /// <summary>A tap within this fraction of one tap step of its travel limit counts as AT LIMIT.</summary>
    public const double TapAtLimitStepFraction = GridSim.Core.Presentation.PresentationConstants.TapAtLimitStepFraction;

    /// <summary>The nominal kV tiers THE MACHINE groups buses into for its voltage layers.</summary>
    public static readonly IReadOnlyList<int> KvTiers = new[] { 400, 275, 132, 66, 33, 25, 22, 11 };

    /// <summary>Relative distance from a tier (|kV - tier| / tier) within which a bus snaps onto it; a bus further from
    /// every tier keeps its own rounded kV.</summary>
    public const double KvTierSnapTolerance = 0.2;

    /// <summary>The narrative grid battery's state of charge (%) at the start of a run and after a timeline break.</summary>
    public const double BessInitialSocPct = GridSim.Core.Presentation.PresentationConstants.BessInitialSocPct;

    // ---------------- HUMAN SCALE anchors ----------------

    /// <summary>Radius (m) of a large steam turbine's last-stage LP blade tip, for the blade-tip speed read-out.</summary>
    public const double LpBladeTipRadiusM = GridSim.Core.Presentation.PresentationConstants.LpBladeTipRadiusM;

    /// <summary>Speed of sound in air (m/s) at about 20 C, for the blade-tip Mach number.</summary>
    public const double SpeedOfSoundMs = GridSim.Core.Presentation.PresentationConstants.SpeedOfSoundMs;

    /// <summary>|RoCoF| (Hz/s) below which the stored rotor energy reads COASTING rather than DELIVERING or SOAKING UP.</summary>
    public const double RegenDeadbandHzPerS = GridSim.Core.Presentation.PresentationConstants.RegenDeadbandHzPerS;

    // ---------------- GENERATION & INERTIA, OPERATOR ----------------

    /// <summary>Synchronous share of generation at or above which the GEN &amp; INERTIA note reads "healthy".</summary>
    public const double SyncShareHealthyFraction = 0.55;

    /// <summary>Synchronous share at or above which the note reads "moderate" (below it: converter-dominated). The WPF
    /// GENERATION tab's LOW INERTIA REGIME cut is the same 0.4.</summary>
    public const double SyncShareModerateFraction = GridSim.Core.Presentation.PresentationConstants.SyncShareModerateFraction;

    /// <summary>An interconnector moving less than this (MW, either way) is idle and left off the link lists. Q10
    /// (2026-10): the ONE shared cut-off now lives in Core (<c>StatusLampRules.IdleInterconnectorMw</c>, 0.5 MW) and
    /// the WPF dashboard uses it too; this name is kept so existing web callers and tests read the same value.</summary>
    public const double IdleInterconnectorMw = GridSim.Core.Presentation.StatusLampRules.IdleInterconnectorMw;

    // ---------------- NESO RP1 LFDD traffic light ----------------

    /// <summary>Purchased time (s) to the 49.2 Hz working set-point at or above which the LFDD light is green
    /// (grade NORMAL). Q1 (2026-10): the ladder now lives in Core (<c>FrequencyRiskPresenter</c>); this is its
    /// <c>GradeNormalS</c> under the old web name.</summary>
    public const double PurchasedTimeGreenS = GridSim.Core.Presentation.FrequencyRiskPresenter.GradeNormalS;

    /// <summary>Purchased time (s) at or above which the LFDD light is amber (below it: red, grade WARNING). The Core
    /// ladder's <c>GradeAdvisoryS</c> under the old web name.</summary>
    public const double PurchasedTimeAmberS = GridSim.Core.Presentation.FrequencyRiskPresenter.GradeAdvisoryS;

    // ---------------- MARKET colour bands ----------------

    /// <summary>Imbalance price (GBP/MWh) above which it is coloured red.</summary>
    public const double ImbalancePriceHighGbpPerMwh = 150.0;

    /// <summary>Imbalance price (GBP/MWh) above which it is coloured amber.</summary>
    public const double ImbalancePriceWatchGbpPerMwh = 80.0;

    /// <summary>Grid carbon intensity (gCO2/kWh) below which it is coloured green.</summary>
    public const double CarbonLowGPerKwh = 100.0;

    /// <summary>Grid carbon intensity (gCO2/kWh) below which it is coloured amber (at or above: red).</summary>
    public const double CarbonWatchGPerKwh = 250.0;

    /// <summary>Balancing spend (GBP) above which it is coloured red.</summary>
    public const double BalancingSpendHighGbp = 1_000_000.0;

    /// <summary>Balancing spend (GBP) above which it is coloured amber.</summary>
    public const double BalancingSpendWatchGbp = 250_000.0;

    // ---------------- MECHANICAL ----------------

    /// <summary>Unit ramp (MW/min, either way) inside which the ramp read-out is coloured neutral (holding).</summary>
    public const double RampDeadbandMwPerMin = GridSim.Core.Presentation.PresentationConstants.RampDeadbandMwPerMin;

    /// <summary>Machine stress (% of its capability) at or above which its risk reads LIMIT.</summary>
    public const double StressLimitPct = GridSim.Core.Presentation.PresentationConstants.StressLimitPct;

    /// <summary>Machine stress (%) at or above which its risk reads NEAR.</summary>
    public const double StressNearPct = GridSim.Core.Presentation.PresentationConstants.StressNearPct;

    // ---------------- session driver ----------------

    /// <summary>Default playback rate (frames per second) of a pure RAW replay, faster than 1x so the half-hourly
    /// settlement channels step often.</summary>
    public const double RawDefaultFps = 8.0;

    /// <summary>Slowest playback rate (frames per second) a session accepts.</summary>
    public const double MinFps = 0.1;

    /// <summary>Fastest playback rate (frames per second) a session accepts (the transport bar's top speed).</summary>
    public const double MaxFps = 60.0;

    /// <summary>Time-out of the shared HTTP client that fetches bridge metadata.</summary>
    public static readonly TimeSpan MetaFetchTimeout = TimeSpan.FromSeconds(100);

    /// <summary>How many days before the forward-inference horizon a live bridge replay starts, so the published
    /// settlement data it needs is already in.</summary>
    public const int SettlementLagMarginDays = 2;

    /// <summary>Angular speed (rad/s) below which no torque is derived (T = P / omega would blow up).</summary>
    public const double MinOmegaRadPerS = 1e-9;

    /// <summary>Most worker threads the background analysis cycle takes by default (it never starves the web server).</summary>
    public const int DefaultCycleWorkerCap = 4;
}
