using GridSim.Core.Dynamics;

namespace GridSim.Core.Presentation;

/// <summary>
/// R148 + N297 (B4, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04): the ONE set of rules the trip and TSGB
/// what-if surfaces use (web TRIP / DEVICE tab, web GEN &amp; INERTIA TSGB card, WPF trip compare), so the same loss gives
/// the same "required device" and the same grades on every tab and shell, at the case's own nominal frequency.
/// <para><b>R148, before:</b> the web TRIP / DEVICE tab sized the device to 0.5 Hz/s and 49.5 Hz with literals, while the
/// web TSGB card and the WPF compare sized it to 0.125 Hz/s (<see cref="FrequencyLimits.RocofLimitHzPerS"/>) and
/// <see cref="FrequencyLimits.StatutoryLowHz"/>, so one loss gave two different answers on two tabs of one dashboard; the
/// trip tab also hard-coded its 48.8 / 49.5 / 1.0 / 0.5 grade cuts and reference lines.</para>
/// <para><b>Decision (Ruling R1).</b> The device is sized to the targets the other two surfaces and the CLI (`trip`,
/// `gda` TSGB lines) already use: RoCoF at most <see cref="FrequencyLimits.RocofLimitHzPerS"/> (0.125 Hz/s, the
/// legacy loss-of-mains setting, the strictest RoCoF any GB relay was ever set to: the conservative design target this
/// app's inertia thesis, the dynamic MIF and the RP1 tab all quote) and the nadir held at the statutory low edge (ESQCR
/// 2002 regulation 27, 49.5 Hz at 50 Hz). The 0.5 Hz/s figure had no source. The grade cuts are named here: a RoCoF
/// above <see cref="FrequencyLimits.RocofLimitModernHzPerS"/> (1 Hz/s, the post-ALoMCP LoM setting, EREC G99 Issue 2
/// Table 10.1) is an alarm, above <see cref="RocofWarnHzPerS"/> a warning; a nadir at or below the LFDD first stage is an
/// alarm, below the statutory edge a warning.</para>
/// <para><b>N297, before:</b> these surfaces ran their swing at the GB 50 Hz and graded against the absolute GB edges for
/// every case, so on case9 (60 Hz since D2) they disagreed with CLI `trip`. Now the study runs at the case's nominal
/// (<see cref="StudyConfig"/>) and grades against the GB bands about it (<see cref="FrequencyBands"/>, B2's convention).
/// At 50 Hz every number and text is the one the WPF compare and the TSGB card printed before.</para>
/// </summary>
public static class TripWhatIf
{
    /// <summary>R148: the RoCoF (Hz/s) the TSGB device is sized to hold: <see cref="FrequencyLimits.RocofLimitHzPerS"/>
    /// (0.125 Hz/s). A relay setting in Hz/s, so it does not move with the nominal.</summary>
    public const double SizingMaxRocofHzPerS = FrequencyLimits.RocofLimitHzPerS;

    /// <summary>R148: the RoCoF magnitude (Hz/s) above which a what-if read-out is graded as a warning (amber). A
    /// presentation step halfway between the legacy and modern settings; above <see cref="FrequencyLimits.RocofLimitModernHzPerS"/>
    /// it is an alarm (red). It was a literal in two places.</summary>
    public const double RocofWarnHzPerS = 0.5;

    /// <summary>R148 / N297: the nadir (Hz) the device is sized to hold: the statutory low edge of the system's bands.</summary>
    public static double SizingMinNadirHz(FrequencyBands bands)
    {
        ArgumentNullException.ThrowIfNull(bands);
        return bands.StatutoryLowHz;
    }

    /// <summary>
    /// N297: the dynamics config a what-if study of a system at <paramref name="nominalHz"/> runs with: the defaults at
    /// that nominal (the swing's RoCoF = f0 dP / 2E). At exactly 50 Hz it is a default config, field for field the one
    /// these surfaces used before, so every 50 Hz trajectory is bit-identical. Fails loud on a nominal that is not a
    /// finite frequency above 0 Hz.
    /// </summary>
    public static DynamicsConfig StudyConfig(double nominalHz)
    {
        if (!double.IsFinite(nominalHz) || !(nominalHz > 0.0))
            throw new ArgumentOutOfRangeException(nameof(nominalHz), nominalHz, "A system nominal frequency must be a finite number of Hz above 0.");
        return nominalHz == FrequencyLimits.NominalHz ? new DynamicsConfig() : new DynamicsConfig { NominalHz = nominalHz };
    }

    /// <summary>The grade of one what-if read-out. Each shell maps it to its own style (web css "alarm" / "warnv").</summary>
    public enum Level { Normal, Warning, Alarm }

    /// <summary>R148 / N297: grade a nadir against <paramref name="bands"/>: at or below the LFDD first stage an alarm (the
    /// strict test of <see cref="FrequencyLimits.Assess(FrequencyResponse, double, FrequencyBands)"/>), below the
    /// statutory low edge a warning. Before (web): "&lt; 48.8" alarm (the web trip tab used a strict test), "&lt; 49.5"
    /// warning.</summary>
    public static Level GradeNadir(double nadirHz, FrequencyBands bands)
    {
        ArgumentNullException.ThrowIfNull(bands);
        return nadirHz <= bands.LfddFirstStageHz ? Level.Alarm : nadirHz < bands.StatutoryLowHz ? Level.Warning : Level.Normal;
    }

    /// <summary>R148: grade a RoCoF magnitude: above <see cref="FrequencyLimits.RocofLimitModernHzPerS"/> an alarm, above
    /// <see cref="RocofWarnHzPerS"/> a warning (the cuts both web surfaces used, now named).</summary>
    public static Level GradeRocof(double rocofHzPerS)
    {
        double ro = Math.Abs(rocofHzPerS);
        return ro > FrequencyLimits.RocofLimitModernHzPerS ? Level.Alarm : ro > RocofWarnHzPerS ? Level.Warning : Level.Normal;
    }

    /// <summary>The web css class of a grade: "alarm", "warnv" or "" (the classes both web surfaces used).</summary>
    public static string CssClass(Level level) => level switch
    {
        Level.Alarm => "alarm",
        Level.Warning => "warnv",
        _ => "",
    };

    /// <summary>One reference line of a frequency strip chart: its frequency, its label and its kind.</summary>
    public readonly record struct ReferenceLine(double Hz, string Label, string Kind);

    /// <summary>
    /// N297: the reference lines a what-if strip chart draws for a system with <paramref name="bands"/>: nominal,
    /// operational low, statutory low and LFDD first stage (kinds "nom", "oper", "stat", "lfdd"), labelled with
    /// <see cref="FrequencyBands.Label"/>. For <see cref="FrequencyBands.Gb"/> these are 50.0 / 49.8 / 49.5 / 48.8, the
    /// lines both charts drew as literals.
    /// </summary>
    public static IReadOnlyList<ReferenceLine> ReferenceLines(FrequencyBands bands)
    {
        ArgumentNullException.ThrowIfNull(bands);
        return new[]
        {
            new ReferenceLine(bands.NominalHz, FrequencyBands.Label(bands.NominalHz), "nom"),
            new ReferenceLine(bands.OperationalLowHz, FrequencyBands.Label(bands.OperationalLowHz), "oper"),
            new ReferenceLine(bands.StatutoryLowHz, FrequencyBands.Label(bands.StatutoryLowHz), "stat"),
            new ReferenceLine(bands.LfddFirstStageHz, FrequencyBands.Label(bands.LfddFirstStageHz), "lfdd"),
        };
    }

    /// <summary>N297: the sizing targets as the surfaces print them: "&lt;=0.125 Hz/s, &gt;=49.5 Hz" at 50 Hz (the WPF
    /// compare's text), the statutory edge about the nominal otherwise.</summary>
    public static string SizingTargetsText(FrequencyBands bands) =>
        $"<={SizingMaxRocofHzPerS.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} Hz/s, " +
        $">={FrequencyBands.Label(SizingMinNadirHz(bands))} Hz";
}
