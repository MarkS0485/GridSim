using System.Globalization;

namespace GridSim.Core.Dynamics;

/// <summary>
/// N273 (October 2026 review, stream B2; D2 open item O1; Ruling R1: Mark delegated EE calls 2026-10-04): the frequency
/// band set a study or a live display grades a frequency against, for ONE system nominal frequency. The GB set
/// (<see cref="Gb"/>) holds exactly the <see cref="FrequencyLimits"/> constants; <see cref="ForNominal"/> gives the same
/// GB bands about another nominal.
///
/// <para><b>Why offsets in hertz about the nominal (the decision, benchmarked).</b> The live loop, the web dashboard and
/// the desktop used the absolute GB edges (49.8 / 50.2 operational, 49.5 / 50.5 statutory, 48.8 LFDD, 49.9 / 50.1 SCADA)
/// for every case, so case9 (60 Hz since D2) sat 10 Hz "above statutory" for ever. The question is how a non-50 Hz system's
/// bands should be expressed. How the reference tools and codes express them:</para>
/// <list type="bullet">
/// <item>PSS/E: one base frequency per case (the BASFRQ option, 50 or 60 Hz); dynamic models carry speed and frequency
///   as a per-unit DEVIATION from it, and its frequency-relay and load-shedding models take pickups in Hz for the system
///   they protect [VERIFY model names and units against the PSS/E Model Library].</item>
/// <item>PowerFactory: the nominal frequency is a project / grid setting (50 or 60 Hz); RMS results report frequency in
///   per unit of it and the deviation in Hz, and relay frequency elements are set in Hz [VERIFY against the PowerFactory
///   User Manual, "Nominal frequency" and the frequency-relay element].</item>
/// <item>ENTSO-E System Operation Guideline (Commission Regulation (EU) 2017/1485, Annex III, Table 1): the standard
///   frequency range, maximum instantaneous and maximum steady-state frequency DEVIATIONS are given in mHz about the
///   50 Hz nominal (e.g. +/-50 mHz, 800 mHz, 200 mHz for Continental Europe) [VERIFY table number].</item>
/// <item>NERC (60 Hz): frequency response is measured in MW per 0.1 Hz of deviation (BAL-003-2) and UFLS first stages
///   are set in Hz per region (PRC-006); deviations, again, in Hz about 60 Hz [VERIFY standard versions].</item>
/// <item>GB itself: ESQCR 2002 (SI 2002/2665) regulation 27(3)(a), "a variation not exceeding 1 per cent above or
///   below the declared frequency", with 27(2) declaring 50 Hz (+/-1 % of 50 Hz, the statutory 49.5 / 50.5 Hz; confirmed
///   on legislation.gov.uk 2026-10-11, N292); the operational +/-0.2 Hz and the 48.8 Hz LFDD first stage (Grid Code OC6.6) are written in Hz.</item>
/// </list>
/// <para>So the common form is a band in HERTZ ABOUT THE SYSTEM'S NOMINAL. A proportional rescale (x 1.2 at 60 Hz) has no
/// support beyond ESQCR's one percentage, and would move the operational and LFDD edges, which every code writes in Hz.
/// GridSim's live loop is a GB tool (GB demand curve, GB SCADA bands, GB services), so its bands are the GB ones,
/// expressed as offsets about the model's nominal: case9 at 60 Hz is graded against 59.8 / 60.2, 59.5 / 60.5, 58.8 and
/// 59.9 / 60.1 Hz. A per-model band set read from case data (a system's own grid code) would need a model field and a
/// schema change; it is recorded as a follow-up in the B2 notes. Every 50 Hz case gets <see cref="Gb"/> itself, so every
/// 50 Hz output is byte-identical.</para>
///
/// <para>Exactness: for any GB edge e (all between 25 and 100 Hz) the difference e - 50 is exact (Sterbenz: two doubles
/// within a factor of two subtract exactly), and at 50 Hz the edge is returned as the constant itself, never recomputed.</para>
/// </summary>
public sealed class FrequencyBands
{
    /// <summary>System nominal frequency (Hz).</summary>
    public double NominalHz { get; }
    /// <summary>Operational band low edge (Hz). GB: 49.8.</summary>
    public double OperationalLowHz { get; }
    /// <summary>Operational band high edge (Hz). GB: 50.2.</summary>
    public double OperationalHighHz { get; }
    /// <summary>Statutory low limit (Hz). GB: 49.5.</summary>
    public double StatutoryLowHz { get; }
    /// <summary>Statutory high limit (Hz). GB: 50.5.</summary>
    public double StatutoryHighHz { get; }
    /// <summary>Low-frequency demand disconnection first stage (Hz). GB: 48.8.</summary>
    public double LfddFirstStageHz { get; }

    /// <summary>A band set; fails loud unless every value is finite and LFDD &lt; statutory low &lt; operational low &lt;
    /// nominal &lt; operational high &lt; statutory high (ArgumentException naming the values).</summary>
    public FrequencyBands(double nominalHz, double operationalLowHz, double operationalHighHz,
        double statutoryLowHz, double statutoryHighHz, double lfddFirstStageHz)
    {
        double[] all = { nominalHz, operationalLowHz, operationalHighHz, statutoryLowHz, statutoryHighHz, lfddFirstStageHz };
        bool ordered = lfddFirstStageHz < statutoryLowHz && statutoryLowHz < operationalLowHz && operationalLowHz < nominalHz
                       && nominalHz < operationalHighHz && operationalHighHz < statutoryHighHz;
        if (!all.All(double.IsFinite) || !(lfddFirstStageHz > 0.0) || !ordered)
            throw new ArgumentException(
                "FrequencyBands: every edge must be a finite frequency above 0 Hz, in the order LFDD < statutory low < operational low "
                + $"< nominal < operational high < statutory high (got LFDD {lfddFirstStageHz:R}, statutory {statutoryLowHz:R} / "
                + $"{statutoryHighHz:R}, operational {operationalLowHz:R} / {operationalHighHz:R}, nominal {nominalHz:R} Hz).");
        NominalHz = nominalHz;
        OperationalLowHz = operationalLowHz;
        OperationalHighHz = operationalHighHz;
        StatutoryLowHz = statutoryLowHz;
        StatutoryHighHz = statutoryHighHz;
        LfddFirstStageHz = lfddFirstStageHz;
    }

    /// <summary>The GB band set at 50 Hz: exactly the <see cref="FrequencyLimits"/> constants.</summary>
    public static FrequencyBands Gb { get; } = new(
        FrequencyLimits.NominalHz,
        FrequencyLimits.OperationalLowHz, FrequencyLimits.OperationalHighHz,
        FrequencyLimits.StatutoryLowHz, FrequencyLimits.StatutoryHighHz,
        FrequencyLimits.LfddFirstStageHz);

    /// <summary>True when this is the GB 50 Hz set (the constants themselves).</summary>
    public bool IsGb => ReferenceEquals(this, Gb);

    /// <summary>
    /// The GB bands about <paramref name="nominalHz"/>: <see cref="Gb"/> itself at exactly 50 Hz, otherwise each GB edge
    /// moved by the same number of hertz as the nominal (see the class remarks for why hertz, not percent). Fails loud on
    /// a nominal that is not a finite frequency above 1.2 Hz (below that the 48.8 Hz LFDD offset of -1.2 Hz would not be a
    /// frequency).
    /// </summary>
    public static FrequencyBands ForNominal(double nominalHz)
    {
        if (!double.IsFinite(nominalHz) || !(nominalHz > FrequencyLimits.NominalHz - FrequencyLimits.LfddFirstStageHz))
            throw new ArgumentOutOfRangeException(nameof(nominalHz), nominalHz,
                $"A system nominal frequency must be a finite number of Hz above {FrequencyLimits.NominalHz - FrequencyLimits.LfddFirstStageHz:R}.");
        if (nominalHz == FrequencyLimits.NominalHz) return Gb;
        return new FrequencyBands(nominalHz,
            GbEdgeAbout(FrequencyLimits.OperationalLowHz, nominalHz), GbEdgeAbout(FrequencyLimits.OperationalHighHz, nominalHz),
            GbEdgeAbout(FrequencyLimits.StatutoryLowHz, nominalHz), GbEdgeAbout(FrequencyLimits.StatutoryHighHz, nominalHz),
            GbEdgeAbout(FrequencyLimits.LfddFirstStageHz, nominalHz));
    }

    /// <summary>The band set for a model: <see cref="ForNominal"/> at the model's <see cref="GridSim.Core.Model.GridModel.FrequencyHz"/>.</summary>
    public static FrequencyBands ForModel(GridSim.Core.Model.GridModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return ForNominal(model.FrequencyHz);
    }

    /// <summary>
    /// A GB edge (Hz, written for 50 Hz) moved to <paramref name="nominalHz"/>: the edge itself at exactly 50 Hz (bit for
    /// bit), else nominal + (edge - 50), the same deviation in hertz. Also used for the presentation-only GB bands that are
    /// not limits (the SCADA alarm band, the sparkline ranges).
    /// </summary>
    public static double GbEdgeAbout(double gbEdgeHz, double nominalHz)
        => nominalHz == FrequencyLimits.NominalHz ? gbEdgeHz : nominalHz + (gbEdgeHz - FrequencyLimits.NominalHz);

    /// <summary>A band edge as printed in summaries and labels: invariant culture, at least one decimal, at most three
    /// ("49.5", "50.2", "59.8"), so the GB texts read exactly as they always did.</summary>
    public static string Label(double hz) => hz.ToString("0.0##", CultureInfo.InvariantCulture);
}
