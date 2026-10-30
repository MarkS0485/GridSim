using System;
using System.Linq;
using GridSim.Core.Diagnostics;

namespace GridSim.Core.Dynamics.Distributed;

/// <summary>
/// A bounded, mechanism-attributed comparison of the distributed model against the lumped model at a scale
/// where no reference exists (T5). It quantifies the centre-of-inertia delta (which stays ~0 without the
/// propagation overlay - the machine-eps invariant) and the per-domain spatial spread (the information a
/// lumped scalar cannot show), attributes them to a named mechanism, and carries the
/// <see cref="ProvenanceTag.UnvalidatedAtScale"/> tag. It asserts SELF-CONSISTENCY, not accuracy: nothing
/// here claims the distributed result is closer to reality - that awaits PMU/DFR field data or peer review.
/// </summary>
public sealed record DivergenceReport(
    double LumpedNadirHz,
    double DistributedCoiNadirHz,
    double CoiNadirDeltaHz,
    double PerDomainNadirSpreadHz,
    double PerDomainRocofSpreadHzPerS,
    string Mechanism,
    ProvenanceTag Provenance)
{
    /// <summary>Build the report from a lumped response, the distributed COI response, and the spatial detail.</summary>
    public static DivergenceReport Compute(
        FrequencyResponse lumped, FrequencyResponse distributedCoi, DistributedResult dist)
    {
        double coiDelta = Math.Abs(lumped.NadirHz - distributedCoi.NadirHz);

        double nadirSpread = 0.0, rocofSpread = 0.0;
        if (dist.Domains.Count > 0)
        {
            nadirSpread = dist.Domains.Max(d => d.NadirHz) - dist.Domains.Min(d => d.NadirHz);
            rocofSpread = dist.Domains.Max(d => Math.Abs(d.InitialRocofHzPerS))
                        - dist.Domains.Min(d => Math.Abs(d.InitialRocofHzPerS));
        }

        string mechanism = dist.Propagation is not null
            ? "inter-domain synchronising coupling + finite-velocity propagation delay redistribute the disturbance spatially"
            : "inter-domain synchronising coupling redistributes the disturbance across speed domains";
        // R025 (October 2026, stream B1): say so when that coupling is the uniform heuristic, not derived from the network.
        // Before, the text read "synchronising coupling" either way, so a heuristic run looked network-derived.
        if (dist.Coupling is { IsHeuristic: true } c)
            mechanism += $"; NOTE the coupling is NOT network-derived: {c.Reason}";

        return new DivergenceReport(
            lumped.NadirHz, distributedCoi.NadirHz, coiDelta, nadirSpread, rocofSpread,
            mechanism, ProvenanceTag.UnvalidatedAtScale);
    }
}
