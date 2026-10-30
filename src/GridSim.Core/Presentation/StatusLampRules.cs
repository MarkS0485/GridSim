using GridSim.Core.Dynamics;

namespace GridSim.Core.Presentation;

/// <summary>
/// The status-lamp and interconnector display rules the web and WPF shells used to apply differently (Q10, ruled
/// 2026-10-02 evening, best judgement). Each rule lives here once and both shells call it:
/// <list type="bullet">
/// <item><b>FREQUENCY EVENT</b>: strictly <c>|f - 50| &gt; 0.2 Hz</c> (the WPF rule). 49.8 Hz and 50.2 Hz are the
/// edges of the GB operational band (<see cref="FrequencyLimits.OperationalLowHz"/> /
/// <see cref="FrequencyLimits.OperationalHighHz"/>), so a frequency ON the edge is still inside the operational
/// limit and is not an event. The web used <c>&gt;=</c>.</item>
/// <item><b>VOLTAGE ALERT</b>: on the minimum and maximum bus voltage (the WPF rule): any bus outside the SCADA
/// alarm band is an alert. The web used the network average, which hides a single bus out of band.</item>
/// <item><b>Idle interconnector</b>: one shared cut-off, <see cref="IdleInterconnectorMw"/> = 0.5 MW (the web
/// value; the smaller threshold hides less). WPF used 5 MW.</item>
/// </list>
/// Presentation only: these read numbers Core already produced and add no analysis.
/// </summary>
public static class StatusLampRules
{
    /// <summary>Half-width (Hz) of the frequency band around nominal before the lamp reads FREQUENCY EVENT (strictly
    /// greater than this). The GB operational +/-0.2 Hz band written as a deviation. Kept as the literal 0.2 rather
    /// than <c>NominalHz - OperationalLowHz</c>, which is 0.20000000000000284 in binary floating point.</summary>
    public const double FrequencyEventBandHz = 0.2;

    /// <summary>An interconnector moving less than this (MW, either direction) is idle: not listed as an active
    /// link, not an infeed that can be lost, badged neither importing nor exporting. At or above it, it is active.</summary>
    public const double IdleInterconnectorMw = 0.5;

    /// <summary>
    /// True when the frequency is a FREQUENCY EVENT on the deviation test: strictly more than 0.2 Hz away from
    /// nominal, i.e. strictly outside the closed operational band [49.8, 50.2] Hz. Fails loud on a non-finite
    /// frequency.
    /// <para>Why it compares against the band edges instead of computing <c>|f - 50| &gt; 0.2</c>: in binary floating
    /// point <c>50 - 49.8</c> is 0.20000000000000284 and <c>50.2 - 50</c> is 0.20000000000000284, both GREATER than the
    /// double 0.2, so the subtraction form fires on the edges themselves and could never express the ruling's reason
    /// ("0.2 Hz is inside the operational limit"). Doubles near 50 are spaced about 7.1e-15 apart, so no frequency
    /// gives a difference exactly equal to the double 0.2, and the old web <c>&gt;=</c> and WPF <c>&gt;</c> forms were
    /// in fact identical. Comparing f directly with the literal edges 49.8 and 50.2 is the exact real-number meaning
    /// of the strict rule: 49.8 and 50.2 are in band, 49.79 and 50.21 are events.</para>
    /// </summary>
    public static bool IsFrequencyDeviationEvent(double frequencyHz)
    {
        if (!double.IsFinite(frequencyHz))
            throw new ArgumentOutOfRangeException(nameof(frequencyHz), frequencyHz, "Frequency must be finite.");
        return frequencyHz < FrequencyLimits.OperationalLowHz || frequencyHz > FrequencyLimits.OperationalHighHz;
    }

    /// <summary>
    /// N273 (October 2026 review, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04): the same strict rule against
    /// the operational band of the frame's OWN system (<paramref name="bands"/>, normally
    /// <see cref="FrequencyBands.ForNominal"/> at the model's nominal), so a 60 Hz case is not a FREQUENCY EVENT for ever.
    /// With <see cref="FrequencyBands.Gb"/> the edges are the same constants as above, so every 50 Hz verdict is
    /// unchanged. Fails loud on a non-finite frequency.
    /// </summary>
    public static bool IsFrequencyDeviationEvent(double frequencyHz, FrequencyBands bands)
    {
        ArgumentNullException.ThrowIfNull(bands);
        if (!double.IsFinite(frequencyHz))
            throw new ArgumentOutOfRangeException(nameof(frequencyHz), frequencyHz, "Frequency must be finite.");
        return frequencyHz < bands.OperationalLowHz || frequencyHz > bands.OperationalHighHz;
    }

    /// <summary>
    /// N041 (B4, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04): the RoCoF (Hz/s) above which the lamp
    /// reads FREQUENCY EVENT in BOTH shells: the GB loss-of-mains RoCoF protection setting, 1 Hz/s
    /// (<see cref="FrequencyLimits.RocofLimitModernHzPerS"/>). Sources: EREC G99 Issue 2 (docs/reference S124)
    /// s.10.6.7.1 Table 10.1, LoM (RoCoF) 1 Hz/s with a 0.5 s time delay; Ofgem's DC0079 decision (S109, 15 December
    /// 2017): "RoCoF settings changed to 1Hz/s with a definite time delay of 500ms", applied retrospectively to existing
    /// plant by the Accelerated Loss of Mains Change Programme (DC0079 retrospective application, S107). A RoCoF above it
    /// is the rate at which embedded generation's LoM relays start to trip, which is an event an operator must see.
    /// Before: the web lamp fired at RoCoF &gt;= 0.125 Hz/s (the legacy pre-ALoMCP setting, which no GB relay is meant to
    /// use any more, so ordinary post-trip slopes lit the lamp) and the desktop lamp had no RoCoF test at all.
    /// </summary>
    public const double RocofEventHzPerS = FrequencyLimits.RocofLimitModernHzPerS;

    /// <summary>
    /// N041: true when the RoCoF magnitude breaches <see cref="RocofEventHzPerS"/> (strictly above it, by more than the
    /// shared <see cref="FrequencyLimits.RocofBreachToleranceHzPerS"/>: the one breach test every assessment uses, so a
    /// RoCoF of exactly 1 Hz/s is not an event, as a relay set to 1 Hz/s does not operate on it). Fails loud on a
    /// non-finite RoCoF.
    /// </summary>
    public static bool IsRocofEvent(double rocofHzPerS)
    {
        if (!double.IsFinite(rocofHzPerS))
            throw new ArgumentOutOfRangeException(nameof(rocofHzPerS), rocofHzPerS, "RoCoF must be finite.");
        return FrequencyLimits.BreachesRocof(rocofHzPerS, RocofEventHzPerS);
    }

    /// <summary>True when any bus is outside the SCADA voltage alarm band, judged on the minimum and maximum bus
    /// voltage (band edges inclusive, i.e. exactly 0.94 or 1.06 pu is in band). Fails loud on non-finite input or
    /// a minimum above the maximum.</summary>
    public static bool IsVoltageAlert(double minBusVoltagePu, double maxBusVoltagePu)
    {
        if (!double.IsFinite(minBusVoltagePu) || !double.IsFinite(maxBusVoltagePu))
            throw new ArgumentOutOfRangeException(nameof(minBusVoltagePu), $"min {minBusVoltagePu} / max {maxBusVoltagePu}",
                "Bus voltages must be finite.");
        if (minBusVoltagePu > maxBusVoltagePu)
            throw new ArgumentException($"Minimum bus voltage {minBusVoltagePu} pu is above the maximum {maxBusVoltagePu} pu.");
        return minBusVoltagePu < ScadaThresholds.VoltageBandLowPu || maxBusVoltagePu > ScadaThresholds.VoltageBandHighPu;
    }

    /// <summary>True when an interconnector flow (MW, import positive) is idle: |flow| below
    /// <see cref="IdleInterconnectorMw"/>. Fails loud on a non-finite flow.</summary>
    public static bool IsIdleInterconnector(double flowMw)
    {
        if (!double.IsFinite(flowMw))
            throw new ArgumentOutOfRangeException(nameof(flowMw), flowMw, "Interconnector flow must be finite.");
        return Math.Abs(flowMw) < IdleInterconnectorMw;
    }
}
