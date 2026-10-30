namespace GridSim.Core.Presentation;

/// <summary>
/// The SCADA console's register-building and alarm thresholds, shared by the WPF <c>ScadaVm</c> and the web
/// <c>ScadaService</c> so the two consoles raise the same alarms off the same solved frame. Previously each
/// twin carried its own copies of these cutoffs (275 kV EHV, 30 heaviest corridors, the 0.94-1.06 voltage
/// band, the 90%/70% corridor loading steps, the 49.9-50.1 Hz alarm band) and could drift. R139 / R149 (B4): the
/// frequency alarm band is the NORMAL +/-0.1 Hz band of <see cref="GridSim.Core.Dynamics.FrequencyLimits"/>, not the
/// +/-0.2 Hz operational band it used to be called.
/// </summary>
public static class ScadaThresholds
{
    /// <summary>EHV telemetry cutoff (kV): buses at or above this are registered as voltage points.</summary>
    public const double EhvCutoffKv = 275.0;

    /// <summary>How many of the heaviest-loaded corridors (by solved loading) carry P/Q telemetry points.</summary>
    public const int TopCorridors = 30;

    // ---- bus voltage alarm band (pu) - a generous operational band, not the wider statutory one ----
    /// <summary>Below this per-unit voltage a bus point alarms.</summary>
    public const double VoltageBandLowPu = GridSim.Core.Analysis.VoltageLimits.OperationalLowPu;   // N018: the one band (0.94)
    /// <summary>Above this per-unit voltage a bus point alarms.</summary>
    public const double VoltageBandHighPu = GridSim.Core.Analysis.VoltageLimits.OperationalHighPu;  // N018: the one band (1.06)

    // ---- corridor loading steps (% of rating) ----
    /// <summary>P2 alarm: a corridor loaded above this percentage of its rating.</summary>
    public const double CorridorAlarmPct = 90.0;
    /// <summary>P3 watch: a corridor loaded above this percentage (but below the alarm step).</summary>
    public const double CorridorWatchPct = 70.0;

    // ---- system frequency alarm band (Hz): the NORMAL +/-0.1 Hz band, tighter than the +/-0.2 Hz operational band ----
    // R139 / R149 (B4, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04): these were described as "the system
    // frequency operational band" while FrequencyLimits defines the operational band as 49.8-50.2 Hz, so one frequency
    // was "within operational limits" in the assessments and alarming on the consoles. The alarm band is deliberately
    // tighter: it is the NORMAL band (FrequencyLimits.NormalBandLowHz / HighHz, the frequency-quality band NESO measures,
    // sources there), so the consoles flag a drift long before the operational limit (the FREQUENCY EVENT lamp,
    // StatusLampRules) or the statutory limit. The constants now REFERENCE that one definition; the names are historical
    // (every caller and test spells them), the values are unchanged.
    /// <summary>Below this frequency the system-frequency point alarms: <see cref="GridSim.Core.Dynamics.FrequencyLimits.NormalBandLowHz"/>
    /// (49.9 Hz), the NORMAL band, not the operational limit. Historical name.</summary>
    public const double FreqOperationalLowHz = GridSim.Core.Dynamics.FrequencyLimits.NormalBandLowHz;
    /// <summary>Above this frequency the system-frequency point alarms: <see cref="GridSim.Core.Dynamics.FrequencyLimits.NormalBandHighHz"/>
    /// (50.1 Hz). Historical name.</summary>
    public const double FreqOperationalHighHz = GridSim.Core.Dynamics.FrequencyLimits.NormalBandHighHz;

    // N273 (October 2026 review, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04): the two consoles alarm on the
    // band about the frame's OWN nominal (the model's FrequencyHz), the GB +/-0.1 Hz moved with the nominal
    // (FrequencyBands.GbEdgeAbout: the constants themselves at 50 Hz). Before, a 60 Hz case alarmed on every frame.

    /// <summary>Low edge (Hz) of the SCADA frequency alarm band for a system at <paramref name="nominalHz"/>:
    /// <see cref="FreqOperationalLowHz"/> at 50 Hz, the same -0.1 Hz about any other nominal.</summary>
    public static double FreqAlarmLowHz(double nominalHz) => GridSim.Core.Dynamics.FrequencyBands.GbEdgeAbout(FreqOperationalLowHz, nominalHz);

    /// <summary>High edge (Hz) of the SCADA frequency alarm band for a system at <paramref name="nominalHz"/>.</summary>
    public static double FreqAlarmHighHz(double nominalHz) => GridSim.Core.Dynamics.FrequencyBands.GbEdgeAbout(FreqOperationalHighHz, nominalHz);

    /// <summary>True when <paramref name="frequencyHz"/> is outside the SCADA alarm band about <paramref name="nominalHz"/>
    /// (edges inclusive, in band). At 50 Hz this is exactly the old test against the two constants.</summary>
    public static bool IsFrequencyAlarm(double frequencyHz, double nominalHz) =>
        frequencyHz < FreqAlarmLowHz(nominalHz) || frequencyHz > FreqAlarmHighHz(nominalHz);

    /// <summary>The alarm band as the consoles print it: "49.9-50.1" at 50 Hz, "59.9-60.1" at 60 Hz.</summary>
    public static string FreqAlarmBandText(double nominalHz) =>
        GridSim.Core.Dynamics.FrequencyBands.Label(FreqAlarmLowHz(nominalHz)) + "-" + GridSim.Core.Dynamics.FrequencyBands.Label(FreqAlarmHighHz(nominalHz));

    // ---- event-log priorities (D-DOC-147, October 2026) ----
    // One rule for both consoles. The web raised a frequency excursion as P1 and the desktop as P2. Ruling (best
    // judgement, Mark delegated EE calls 2026-10-04): P2. The alarm band is the tight 49.9-50.1 Hz NORMAL band, well
    // inside the +/-0.2 Hz operational limit (StatusLampRules.FrequencyEventBandHz, the FREQUENCY EVENT lamp) and the
    // +/-0.5 Hz statutory limit; GB frequency leaves 49.9-50.1 Hz many times a day in normal operation, so treating it
    // as top priority beside a bus outside its voltage band would bury the real P1s. It is the same priority as a
    // corridor above 90 % of rating: act soon, not now. The web code's own comment already called it "P2".

    /// <summary>P1 (red): a bus voltage outside the alarm band <see cref="VoltageBandLowPu"/>..<see cref="VoltageBandHighPu"/>.</summary>
    public const string PriorityVoltageOutOfBand = "P1";

    /// <summary>P2 (amber): the system frequency outside <see cref="FreqOperationalLowHz"/>..<see cref="FreqOperationalHighHz"/>.</summary>
    public const string PriorityFrequencyOutOfBand = "P2";

    /// <summary>P2 (amber): a corridor loaded above <see cref="CorridorAlarmPct"/> of its rating.</summary>
    public const string PriorityCorridorAlarm = "P2";

    /// <summary>P3 (muted): information - a corridor above <see cref="CorridorWatchPct"/> (watch), a tap movement, a
    /// condition clearing or returning to band.</summary>
    public const string PriorityInformation = "P3";
}
