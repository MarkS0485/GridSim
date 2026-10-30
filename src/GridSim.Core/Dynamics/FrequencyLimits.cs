using GridSim.Core.Frequency;

namespace GridSim.Core.Dynamics;

/// <summary>How a measured frequency trace sat against the GB limits over its whole length.</summary>
public sealed record FrequencyTraceReport(
    int Samples,
    double MinHz,
    double MaxHz,
    int SamplesBelowOperational,
    int SamplesBelowStatutory,
    bool LfddReached,
    double MaxAbsRocofHzPerS,
    bool RocofLimitExceeded,
    // ---- dual-threshold RoCoF reporting (added; defaulted so the historical 8-arg ctor still compiles) ----
    // RocofLimitExceeded (above) is retained and tracks the LEGACY 0.125 Hz/s setting for back-compat.
    bool RocofLimitExceededLegacy = false,
    bool RocofLimitExceededModern = false,
    bool RocofLimitExceededActive = false,
    double RocofLimitAppliedHzPerS = FrequencyLimits.RocofLimitModernHzPerS,
    // ---- over-frequency (R081, October 2026 review, stream B1; defaulted so the older constructors still compile) ----
    int SamplesAboveOperational = 0,
    int SamplesAboveStatutory = 0);

/// <summary>How a frequency excursion sits against the GB limits  -  the headline numbers plus flags.</summary>
/// <param name="InitialRocofHzPerS">The response's PEAK RoCoF (Hz/s, sign kept), copied from
/// <see cref="FrequencyResponse.InitialRocofHzPerS"/>; every RoCoF flag is scored on its magnitude. N141: the name says
/// "initial" for historical reasons (see FrequencyResponse).</param>
public sealed record FrequencyAssessment(
    double InitialRocofHzPerS,
    double NadirHz,
    double SettleHz,
    bool RocofExceedsLimit,
    bool BelowOperational,
    bool BelowStatutory,
    bool LfddAtRisk,
    string Summary,
    // ---- dual-threshold RoCoF reporting (added; defaulted so the historical 8-arg ctor still compiles) ----
    // RocofExceedsLimit (above) is retained and tracks the LEGACY 0.125 Hz/s setting for back-compat.
    bool RocofExceedsLegacyLimit = false,
    bool RocofExceedsModernLimit = false,
    bool RocofExceedsActiveLimit = false,
    double RocofLimitAppliedHzPerS = FrequencyLimits.RocofLimitModernHzPerS,
    // ---- over-frequency (R081, October 2026 review, stream B1; defaulted so the older constructors still compile) ----
    double PeakHz = double.NaN,
    bool AboveOperational = false,
    bool AboveStatutory = false);

/// <summary>
/// The GB frequency operating limits, and a classifier that scores a
/// <see cref="FrequencyResponse"/> against them. These are the thresholds the system is
/// secured to: statutory +/-0.5 Hz, the tighter operational +/-0.2 Hz band, the
/// low-frequency demand-disconnection (LFDD) first stage, and the RoCoF protection
/// setting that the largest credible loss must stay under.
///
/// RoCoF is dual-threshold and configurable. The <b>legacy</b> 0.125 Hz/s figure is the
/// pre-ALoMCP loss-of-mains relay setting; GB relays were retuned to the <b>modern</b>
/// 1.0 Hz/s by ~2022. <see cref="ActiveRocofLimitHzPerS"/> is the settable limit the active
/// assessment is scored against (defaults to modern 1.0), while assessments also report the
/// legacy and modern breaches independently so both can be surfaced.
/// </summary>
public static class FrequencyLimits
{
    public const double NominalHz = 50.0;
    /// <summary>Operational band low edge (NESO aims to hold +/-0.2 Hz).</summary>
    public const double OperationalLowHz = 49.8;
    /// <summary>Operational band high edge (+0.2 Hz). R081 (October 2026, stream B1): assessed on the run's peak (and on
    /// every trace sample); before, it was declared and never read, so an over-frequency excursion was scored "within limits".</summary>
    public const double OperationalHighHz = 50.2;
    /// <summary>Statutory limit  -  frequency must stay within +/-0.5 Hz of nominal (ESQCR 2002, regulation 27(3)(a): 1 % either
    /// side of the 50 Hz declared under 27(2); confirmed on legislation.gov.uk 2026-10-11, N292).</summary>
    public const double StatutoryLowHz = 49.5;
    /// <summary>Statutory high edge (+0.5 Hz). R081: assessed on the run's peak (see <see cref="OperationalHighHz"/>).</summary>
    public const double StatutoryHighHz = 50.5;
    /// <summary>Low-frequency demand disconnection first stage  -  load is shed to arrest the fall.</summary>
    public const double LfddFirstStageHz = 48.8;

    /// <summary>
    /// R139 / R149 (B4, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04): the NORMAL frequency band, low edge
    /// (Hz), +/-0.1 Hz about 50 Hz. It is NOT a limit and NOT the operational band (that is
    /// <see cref="OperationalLowHz"/> / <see cref="OperationalHighHz"/>, +/-0.2 Hz): it is the band GB frequency is held in
    /// most of the time, the frequency-quality measure NESO uses and the one the SCADA consoles alarm on. Sources on disk:
    /// NESO FRCR 2025 supplementary report (docs/reference S055) p.16, "the probability of frequency at and above 49.9 Hz
    /// is approximately 90%"; NESO pre-fault frequency control modelling webinar (S050), the share of time outside
    /// 49.9 / 50.1 Hz ("Target: 5% below 49.9 Hz"); Grid Code Issue 6 Revision 16 (S076) ECP.A.5.8.9, the Target
    /// Frequency "normal control point within the range of 49.9Hz to 50.1Hz". Before, the SCADA thresholds carried 49.9 /
    /// 50.1 as their own "operational band", so one frequency was inside the operational limits on one view and alarming
    /// on another under the same name. Same values: no number moves.
    /// </summary>
    public const double NormalBandLowHz = 49.9;

    /// <summary>R139 / R149: the NORMAL frequency band, high edge (Hz), +0.1 Hz. See <see cref="NormalBandLowHz"/>.</summary>
    public const double NormalBandHighHz = 50.1;

    /// <summary>Legacy RoCoF loss-of-mains relay setting (Hz/s)  -  the pre-ALoMCP GB value.</summary>
    public const double RocofLimitLegacyHzPerS = 0.125;
    /// <summary>Modern RoCoF loss-of-mains relay setting (Hz/s)  -  GB relays retuned to 1.0 Hz/s by ~2022.</summary>
    public const double RocofLimitModernHzPerS = 1.0;

    /// <summary>
    /// Back-compatible alias for the historical RoCoF protection constant. Kept at the
    /// <see cref="RocofLimitLegacyHzPerS"/> value (0.125 Hz/s) so existing callers and their
    /// expectations don't change. Use <see cref="ActiveRocofLimitHzPerS"/> for the current
    /// (modern) setting.
    /// </summary>
    public const double RocofLimitHzPerS = RocofLimitLegacyHzPerS;

    /// <summary>
    /// The RoCoF limit (Hz/s) the "active" assessment is scored against when the caller does not pass one: the modern
    /// 1.0 Hz/s relay setting. To screen against legacy relays (or any bespoke protection setting) pass the limit
    /// explicitly: every assessment has an overload that takes it (<see cref="Assess(FrequencyResponse, double)"/>,
    /// <see cref="AssessTrace(IReadOnlyList{FrequencySample}, double)"/>, <c>RocofScreening.Screen(..., double, ...)</c>).
    /// This does not affect the retained legacy/modern-specific flags, which are always reported.
    ///
    /// <para>R243 (October 2026 review, H hygiene stream): this used to be a settable process-wide static, so any caller
    /// (a web session, a desktop setting, a test) that changed it changed the verdict of every concurrent and later study
    /// in the process: the same input could score differently depending on what ran before. Nothing in the code base
    /// ever set it, so removing the setter changes no result; the value is the same 1.0 Hz/s it always defaulted to.</para>
    /// </summary>
    public static double ActiveRocofLimitHzPerS => RocofLimitModernHzPerS;

    /// <summary>Tolerance (Hz/s) on the RoCoF breach test, so a RoCoF that equals a limit up to round-off is not a breach
    /// (October 2026 review, H hygiene stream: named, value unchanged).</summary>
    public const double RocofBreachToleranceHzPerS = 1e-9;

    /// <summary>True if <paramref name="rocofHzPerS"/> exceeds <paramref name="limitHzPerS"/> by more than
    /// <see cref="RocofBreachToleranceHzPerS"/>.</summary>
    public static bool BreachesRocof(double rocofHzPerS, double limitHzPerS) =>
        Math.Abs(rocofHzPerS) > limitHzPerS + RocofBreachToleranceHzPerS;

    /// <summary>Assess a response against the current <see cref="ActiveRocofLimitHzPerS"/>.</summary>
    public static FrequencyAssessment Assess(FrequencyResponse r) => Assess(r, ActiveRocofLimitHzPerS);

    /// <summary>
    /// Assess a response, scoring the "active" RoCoF flag against <paramref name="activeRocofLimitHzPerS"/>.
    /// The legacy (0.125 Hz/s) and modern (1.0 Hz/s) flags are always reported too. The retained
    /// <see cref="FrequencyAssessment.RocofExceedsLimit"/> tracks the legacy setting for back-compat.
    /// </summary>
    public static FrequencyAssessment Assess(FrequencyResponse r, double activeRocofLimitHzPerS)
        => Assess(r, activeRocofLimitHzPerS, FrequencyBands.Gb);

    /// <summary>
    /// N291 / N273 (October 2026 review, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04): assess a response
    /// against the bands of ITS system (<paramref name="bands"/>, e.g. <see cref="FrequencyBands.ForNominal"/> at the
    /// model's nominal), not the fixed GB edges. The band flags and the summary's band numbers come from
    /// <paramref name="bands"/>; the RoCoF settings are GB relay settings in Hz/s and do not depend on the nominal. Before,
    /// the flags compared with the absolute GB edges, so a 60 Hz response was "above statutory" from its first point. With
    /// <see cref="FrequencyBands.Gb"/> (what every older overload passes) the result is exactly the old one, summary text
    /// included (the band labels print as "49.5", "50.5", "49.8", "50.2").
    /// </summary>
    public static FrequencyAssessment Assess(FrequencyResponse r, double activeRocofLimitHzPerS, FrequencyBands bands)
    {
        ArgumentNullException.ThrowIfNull(bands);
        ArgumentNullException.ThrowIfNull(r);
        // A non-finite nadir or RoCoF is no response to grade: every "below the band" comparison is false for NaN, so it
        // used to be graded within limits.
        if (!double.IsFinite(r.NadirHz) || !double.IsFinite(r.InitialRocofHzPerS))
            throw new ArgumentException(System.FormattableString.Invariant(
                $"FrequencyLimits.Assess: the response is not finite (nadir {r.NadirHz:R} Hz, RoCoF {r.InitialRocofHzPerS:R} Hz/s); it cannot be graded."), nameof(r));
        double absRocof = Math.Abs(r.InitialRocofHzPerS);
        bool rocofLegacy = BreachesRocof(absRocof, RocofLimitLegacyHzPerS);
        bool rocofModern = BreachesRocof(absRocof, RocofLimitModernHzPerS);
        bool rocofActive = BreachesRocof(absRocof, activeRocofLimitHzPerS);
        bool rocof = rocofLegacy; // retained flag: legacy semantics, unchanged behaviour
        bool operational = r.NadirHz < bands.OperationalLowHz;
        bool statutory = r.NadirHz < bands.StatutoryLowHz;
        bool lfdd = r.NadirHz <= bands.LfddFirstStageHz;
        // R081 (October 2026 review, stream B1; Ruling R1: Mark delegated EE calls 2026-10-04): the high side, on the
        // run's peak, with the same strict comparison as the low side (a peak exactly on a band edge is inside it). A
        // loss of demand or of an exporting interconnector drives the frequency up; it used to score "within limits".
        double peak = r.PeakHz;
        bool aboveOperational = peak > bands.OperationalHighHz;
        bool aboveStatutory = peak > bands.StatutoryHighHz;

        // The summary names the worst breach. The statutory band outranks a RoCoF breach on either side, as it always
        // did below; the operational band ranks last on either side. A run that stays at or below 50.2 Hz (every
        // under-frequency study) gets exactly the text it always got.
        string summary =
            lfdd ? "LFDD RISK  -  demand disconnection"
            : statutory ? $"BELOW STATUTORY ({FrequencyBands.Label(bands.StatutoryLowHz)} Hz)"
            : aboveStatutory ? $"ABOVE STATUTORY ({FrequencyBands.Label(bands.StatutoryHighHz)} Hz)"
            // GR-543: the RoCoF limits are formatted invariantly, as the band labels are, so the summary is the same
            // text on every machine locale.
            : rocofActive ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"RoCoF OVER {activeRocofLimitHzPerS:0.###} Hz/s")
            : rocofLegacy ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"RoCoF OVER legacy {RocofLimitLegacyHzPerS:0.###} Hz/s")
            : operational ? $"below operational ({FrequencyBands.Label(bands.OperationalLowHz)} Hz)"
            : aboveOperational ? $"above operational ({FrequencyBands.Label(bands.OperationalHighHz)} Hz)"
            : "within limits";

        return new FrequencyAssessment(
            r.InitialRocofHzPerS, r.NadirHz, r.SettleHz, rocof, operational, statutory, lfdd, summary,
            rocofLegacy, rocofModern, rocofActive, activeRocofLimitHzPerS,
            PeakHz: peak, AboveOperational: aboveOperational, AboveStatutory: aboveStatutory);
    }

    /// <summary>
    /// Scan a measured frequency trace (e.g. NESO 1-second samples) against the GB limits:
    /// the excursion extremes, how many samples sat below the operational / statutory floors,
    /// whether LFDD was reached, and the worst sample-to-sample RoCoF versus the RoCoF
    /// protection setting. This is the breach detection the RoCoF work runs over real data.
    /// The active RoCoF flag is scored against <see cref="ActiveRocofLimitHzPerS"/>.
    /// </summary>
    public static FrequencyTraceReport AssessTrace(IReadOnlyList<FrequencySample> samples) =>
        AssessTrace(samples, ActiveRocofLimitHzPerS);

    /// <summary>
    /// As <see cref="AssessTrace(IReadOnlyList{FrequencySample})"/>, scoring the "active" RoCoF flag
    /// against <paramref name="activeRocofLimitHzPerS"/>. Legacy (0.125) and modern (1.0) flags are
    /// also reported; the retained <see cref="FrequencyTraceReport.RocofLimitExceeded"/> tracks legacy.
    /// </summary>
    public static FrequencyTraceReport AssessTrace(IReadOnlyList<FrequencySample> samples, double activeRocofLimitHzPerS)
        => AssessTrace(samples, activeRocofLimitHzPerS, FrequencyBands.Gb);

    /// <summary>
    /// N291 / N273 (stream B2): as <see cref="AssessTrace(IReadOnlyList{FrequencySample}, double)"/>, counting the
    /// samples against <paramref name="bands"/> (the trace's own system) instead of the fixed GB edges. With
    /// <see cref="FrequencyBands.Gb"/> the report is exactly the old one.
    /// </summary>
    public static FrequencyTraceReport AssessTrace(IReadOnlyList<FrequencySample> samples, double activeRocofLimitHzPerS, FrequencyBands bands)
    {
        ArgumentNullException.ThrowIfNull(bands);
        if (samples.Count == 0)
            return new FrequencyTraceReport(0, 0, 0, 0, 0, false, 0, false,
                false, false, false, activeRocofLimitHzPerS);

        double min = double.MaxValue, max = double.MinValue, maxRocof = 0;
        int belowOper = 0, belowStat = 0;
        int aboveOper = 0, aboveStat = 0;   // R081: the high side, same strict comparison as the low side
        bool lfdd = false;

        for (int i = 0; i < samples.Count; i++)
        {
            double f = samples[i].Hz;
            if (f < min) min = f;
            if (f > max) max = f;
            if (f < bands.OperationalLowHz) belowOper++;
            if (f < bands.StatutoryLowHz) belowStat++;
            if (f > bands.OperationalHighHz) aboveOper++;
            if (f > bands.StatutoryHighHz) aboveStat++;
            if (f <= bands.LfddFirstStageHz) lfdd = true;

            if (i > 0)
            {
                double dt = (samples[i].Time - samples[i - 1].Time).TotalSeconds;
                RocofUpsampler.RequireNotBackwards(samples[i - 1].Time, samples[i].Time);   // GR-572
                if (dt > 1e-6)
                {
                    double r = Math.Abs((samples[i].Hz - samples[i - 1].Hz) / dt);
                    if (r > maxRocof) maxRocof = r;
                }
            }
        }

        bool legacy = BreachesRocof(maxRocof, RocofLimitLegacyHzPerS);
        bool modern = BreachesRocof(maxRocof, RocofLimitModernHzPerS);
        bool active = BreachesRocof(maxRocof, activeRocofLimitHzPerS);

        return new FrequencyTraceReport(
            samples.Count, min, max, belowOper, belowStat, lfdd,
            maxRocof, legacy, // retained flag: legacy semantics, unchanged behaviour
            legacy, modern, active, activeRocofLimitHzPerS,
            SamplesAboveOperational: aboveOper, SamplesAboveStatutory: aboveStat);
    }
}
