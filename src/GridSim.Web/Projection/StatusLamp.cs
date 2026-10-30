using GridSim.Core.Dynamics;
using GridSim.Core.Presentation;

namespace GridSim.Web.Projection;

/// <summary>
/// The control-room status lamp shown in the dashboard header, derived from one projected frame. Pulled out of
/// <c>Dashboard.razor</c> (R051, October 2026) so the priority order is a pure, unit-testable function instead of a
/// private property buried in a component.
/// <para>Priority, highest first:</para>
/// <list type="number">
/// <item><b>SOLVE FAULT</b> - there is no frame at all.</item>
/// <item><b>NOT CONVERGED</b> - the power flow stopped without converging (R051). Its flows and voltages are not a
/// valid operating point, so no lower-priority verdict (least of all SYSTEM NORMAL) may be drawn from them. This
/// used to be missing: the lamp never read <see cref="HeadlineDto.Converged"/>, so a Newton solve that hit its
/// iteration cap with voltages still inside the band showed SYSTEM NORMAL.</item>
/// <item><b>FREQUENCY EVENT</b> - frequency strictly more than 0.2 Hz from nominal (Q10, 2026-10: the shared Core rule
/// <see cref="StatusLampRules.IsFrequencyDeviationEvent"/>; the web used to fire ON the band edge too), or RoCoF above
/// the GB loss-of-mains setting, 1 Hz/s (N041, B4 2026-10: <see cref="StatusLampRules.IsRocofEvent"/>; it was "at or
/// above the legacy 0.125 Hz/s").</item>
/// <item><b>VOLTAGE ALERT</b> - any bus outside the SCADA alarm band, judged on the lowest and highest bus voltage (Q10,
/// 2026-10: the shared Core rule <see cref="StatusLampRules.IsVoltageAlert"/>; the web used to test the average,
/// which hides a single bus out of band).</item>
/// <item><b>CORRIDOR ALERT</b> - any boundary over its limit.</item>
/// <item><b>SYSTEM NORMAL</b> - none of the above.</item>
/// </list>
/// Presentation only: it reads numbers Core already produced and adds no analysis of its own.
/// </summary>
public static class StatusLamp
{
    /// <summary>Half-width (Hz) of the frequency band around nominal before the lamp reads FREQUENCY EVENT. This is
    /// the GB operational +/-0.2 Hz band (<see cref="FrequencyLimits.OperationalLowHz"/> = 49.8 Hz,
    /// <see cref="FrequencyLimits.OperationalHighHz"/> = 50.2 Hz) written as a deviation. It is kept as the literal
    /// 0.2 rather than computed as <c>NominalHz - OperationalLowHz</c>, because that subtraction is
    /// 0.20000000000000284 in binary floating point and would move the lamp's edge by a few ulps (R203: named, value
    /// unchanged). A test pins it equal to the Core band to within that rounding. Q10 (2026-10): it is now the shared
    /// Core value <see cref="StatusLampRules.FrequencyEventBandHz"/>, kept under this name for existing callers.</summary>
    public const double FrequencyEventBandHz = StatusLampRules.FrequencyEventBandHz;

    /// <summary>The lamp's CSS class and text when there is no frame at all.</summary>
    public static readonly (string Cls, string Text) SolveFault = ("lamp-fault", "SOLVE FAULT");

    /// <summary>The lamp's CSS class and text when the solve did not converge (R051).</summary>
    public static readonly (string Cls, string Text) NotConverged = ("lamp-unconverged", "NOT CONVERGED");

    /// <summary>Work out the lamp for a frame. <paramref name="headline"/> is null when no frame has arrived;
    /// <paramref name="anyBoundaryOverloaded"/> is whether any OPERATOR boundary is over its limit.</summary>
    public static (string Cls, string Text) Evaluate(HeadlineDto? headline, bool anyBoundaryOverloaded)
        => Evaluate(headline, anyBoundaryOverloaded, FrequencyBands.Gb);

    /// <summary>N273 (October 2026, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04): as above, with the
    /// FREQUENCY EVENT test made against <paramref name="bands"/>, the operational band of the session's own case (the
    /// GB +/-0.2 Hz about its nominal). With <see cref="FrequencyBands.Gb"/> it is exactly the rule above.</summary>
    public static (string Cls, string Text) Evaluate(HeadlineDto? headline, bool anyBoundaryOverloaded, FrequencyBands bands)
    {
        ArgumentNullException.ThrowIfNull(bands);
        if (headline is null) return SolveFault;

        // Non-convergence outranks everything that is read off the solved state (R051). Fail loud: the numbers
        // below come from an unconverged iterate and must not be graded as if they were an operating point.
        if (!headline.Converged) return NotConverged;

        // Frequency: the shared strict deviation test (Q10), or a RoCoF above the GB loss-of-mains setting.
        // N041 (B4, October 2026; Ruling R1; source EREC G99 Issue 2 Table 10.1 and Ofgem's DC0079 decision, see
        // StatusLampRules.RocofEventHzPerS): the shared RoCoF rule, 1 Hz/s, the same in the desktop lamp. Before:
        // |RoCoF| >= 0.125 Hz/s (the legacy pre-ALoMCP setting), web only.
        if (StatusLampRules.IsFrequencyDeviationEvent(headline.FrequencyHz, bands)
            || StatusLampRules.IsRocofEvent(headline.RocofHzPerS))
            return ("lamp-freq", "FREQUENCY EVENT");
        // Voltage: the shared min / max bus voltage test (Q10).
        if (StatusLampRules.IsVoltageAlert(headline.MinVoltagePu, headline.MaxVoltagePu))
            return ("lamp-volt", "VOLTAGE ALERT");
        if (anyBoundaryOverloaded) return ("lamp-corridor", "CORRIDOR ALERT");
        return ("lamp-ok", "SYSTEM NORMAL");
    }
}
