namespace GridSim.Core.Dynamics;

/// <summary>
/// The GB frequency-response arrest layer at real, data-grounded held volumes - the Dynamic Containment /
/// Moderation / Regulation the system actually procures. Volumes are NESO EAC auction clears
/// (`enduring_auction_capability_eac_auction_results`, materialised in the GDA lake; see
/// <c>docs/technical/0013 Asset-dynamics data grounding.md</c>): DC ≈ 950-1000 MW, DM ≈ 500-550 MW,
/// DR ≈ 480-500 MW each direction. The delivery-curve SHAPE (DC full at 0.5 Hz with a 5% knee at 0.2 Hz;
/// DM/DR full at 0.2 Hz, ramp-from-deadband) is NESO published service spec. Time constants are
/// literature-typical ([VERIFY]). Use for a realistic "typical GB" arrest layer in scenarios/envelopes;
/// per-event replays should still use the volumes the system actually held at that event.
/// </summary>
public static class GbResponseServices
{
    /// <summary>Dynamic Containment held MW each direction (EAC clears ~944-993; nominal 950).</summary>
    public const double DynamicContainmentMw = 950.0;
    /// <summary>Dynamic Moderation held MW each direction (EAC clears ~515-547; nominal 520).</summary>
    public const double DynamicModerationMw = 520.0;
    /// <summary>Dynamic Regulation held MW each direction (EAC clears 480-500; nominal 490).</summary>
    public const double DynamicRegulationMw = 490.0;

    /// <summary>
    /// The grounded DC + DM + DR arrest layer as three <see cref="SystemResponseService"/> responders, on
    /// their published delivery curves. Scale each volume by <paramref name="scale"/> (e.g. to model a
    /// lighter/heavier procurement). DC: 5% by 0.2 Hz, full by 0.5 Hz. DM: ramp 0.1->0.2 Hz. DR: ramp
    /// 0.015->0.2 Hz.
    /// </summary>
    public static IReadOnlyList<SystemResponseService> Typical(double scale = 1.0) =>
    [
        // DC: fast containment - low-gain 5% segment to 0.2 Hz, steep to full at 0.5 Hz.
        new SystemResponseService(
            Mw: DynamicContainmentMw * scale, DeadbandHz: 0.015, FullDeliveryDeviationHz: 0.5,
            TimeConstantS: 0.5, KneeDeviationHz: 0.2, KneeFraction: 0.05),
        // DM: ramp from a 0.1 Hz deadband to full at 0.2 Hz (knee at the deadband, fraction 0).
        new SystemResponseService(
            Mw: DynamicModerationMw * scale, DeadbandHz: 0.1, FullDeliveryDeviationHz: 0.2,
            TimeConstantS: 2.0, KneeDeviationHz: 0.1, KneeFraction: 0.0),
        // DR: continuous regulation - ramp from a 0.015 Hz deadband to full at 0.2 Hz.
        new SystemResponseService(
            Mw: DynamicRegulationMw * scale, DeadbandHz: 0.015, FullDeliveryDeviationHz: 0.2,
            TimeConstantS: 10.0, KneeDeviationHz: 0.015, KneeFraction: 0.0),
    ];
}
