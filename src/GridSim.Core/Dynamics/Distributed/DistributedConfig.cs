using System.Collections.Generic;

namespace GridSim.Core.Dynamics.Distributed;

/// <summary>
/// Settings for the experimental distributed / multi-domain inertia model.
///
/// The DEGENERATE configuration - <see cref="SingleDomain"/> = true and
/// <see cref="EnablePropagation"/> = false - is the reduction limit that must reproduce the
/// lumped scalar model to machine epsilon (the T0 reduction gate). Everything the distributed
/// model does spatially is switched off there, leaving one coherent COI mass.
/// </summary>
public sealed record DistributedConfig
{
    /// <summary>Force a single coherent speed domain (the lumped degenerate limit - used by T0).</summary>
    public bool SingleDomain { get; init; } = false;

    /// <summary>
    /// Apply finite-velocity propagation delay to the inter-domain coupling (the experimental,
    /// unvalidated-at-scale overlay). OFF by default: the propagation retardation perturbs the centre of
    /// inertia (real physics), so the machine-eps COI invariant only holds with it off. Turn it on to study
    /// the spatial wave. Off => all tau = 0.
    /// </summary>
    public bool EnablePropagation { get; init; } = false;

    /// <summary>
    /// Dominance threshold deltaGVa above which one speed domain is "dominant" (below it the fleet is too
    /// evenly spread - the LOS fragmentation mode a lumped total can't see). 0.10 is an engineering-judgement
    /// value, NOT a theory-derived or fitted constant - so treat it as intuitive, not published. It is,
    /// however, empirically anchored: over 103,433 GB samples (GDA Derived/speed_domains, 2014-2026) the
    /// dominance gap has P10 ~ 0.106, so 0.10 flags almost exactly the bottom decile of gap conditions and
    /// fires ~9.4% of the time (sensitivity: 0.08 -> 7.5%, 0.10 -> 9.4%, 0.12 -> 11.4%). It is `init`-settable
    /// for calibration against an operational event read.
    /// </summary>
    public double DominanceThreshold { get; init; } = 0.10;

    /// <summary>Electromechanical wave-speed clamp band (km/s), from the propagation literature.</summary>
    public double MinWaveSpeedKmPerS { get; init; } = 500.0;
    public double MaxWaveSpeedKmPerS { get; init; } = 1500.0;

    /// <summary>
    /// Target inter-area electromechanical mode frequency (Hz) used to set the MVP inter-domain
    /// synchronising stiffness. Real GB inter-area modes sit ~0.3-0.7 Hz. Refined to Kron-reduced
    /// synchronising coefficients in M2.
    /// </summary>
    public double TargetInterAreaHz { get; init; } = 0.5;

    /// <summary>
    /// Optional externally-provided per-corridor propagation delay tau (seconds), keyed by (fromBus, toBus)
    /// - e.g. GDA's `propagation_delays` lake product streamed over the GDA<->GridSim bridge. When a domain
    /// pair's representative buses are found here, that measured/derived tau is used instead of GridSim's
    /// first-order model. Null => always use the first-order model.
    /// </summary>
    public IReadOnlyDictionary<(int From, int To), double>? ProvidedTauSeconds { get; init; } = null;

    /// <summary>The exact degenerate configuration that must reduce to the lumped model.</summary>
    public static DistributedConfig ReductionLimit => new() { SingleDomain = true, EnablePropagation = false };
}
