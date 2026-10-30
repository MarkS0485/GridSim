using System.Collections.Generic;

namespace GridSim.Core.Dynamics.Distributed;

/// <summary>
/// The spatial outputs of the distributed inertia model, carried alongside the COI-collapsed
/// <see cref="GridSim.Core.Dynamics.FrequencyResponse"/>: the per-domain frequency trajectories, the
/// speed-domain dominance report, a per-bus event-RoCoF map, and (when the propagation overlay is on)
/// the wave-speed / arrival-time report. Always <c>null</c> for <see cref="LumpedScalarInertiaModel"/>.
/// </summary>
/// <param name="EventPlacements">R060 (October 2026, stream B1): where each event landed, in the order the events were
/// given. On the multi-domain path each event is shared among the swing-mass domains; on the degenerate single-domain and
/// forming paths, which delegate to the lumped model, each event acts wholly on the one domain and its rule says that a
/// given location was not used.</param>
/// <param name="Coupling">R025 (October 2026, stream B1): where the inter-domain synchronising stiffness came from, on the
/// multi-domain path (null on the delegating paths, which have no coupling). When <see cref="CouplingReport.IsHeuristic"/>
/// is true the per-domain traces rest on the uniform target-mode stiffness, not on the network.</param>
public sealed record DistributedResult(
    IReadOnlyList<DomainTrace> Domains,
    DominanceReport Dominance,
    IReadOnlyDictionary<int, double> PerBusEventRocofHzPerS,
    PropagationReport? Propagation,
    IReadOnlyList<EventPlacement>? EventPlacements = null,
    CouplingReport? Coupling = null);

/// <summary>
/// R060 (October 2026 review, stream B1): how one frequency event was placed on the speed domains of the distributed
/// model: the fraction of the step each swing-mass domain took at the instant it happened (adding to one), and the rule
/// that decided it, in plain English, so a report can say why the per-domain nadirs and RoCoFs look as they do.
/// </summary>
public sealed record EventPlacement(
    double AtSeconds,
    double DeltaPMw,
    int? AtBus,
    IReadOnlyList<int> DomainIds,
    IReadOnlyList<double> Shares,
    string Rule);

/// <summary>
/// The propagation overlay's output: the wave speed used (km/s, clamped to the literature band), the
/// largest inter-domain delay, and each swing-mass domain's arrival time - the moment its local frequency
/// first departs from nominal after the event (~ event time + tau from the trip domain). Unvalidated at scale.
/// </summary>
/// <param name="UnlocatedDomainIds">R240 (October 2026, stream B1): the swing-mass domains none of whose machine buses has
/// a layout position (a bus at exactly (0, 0) counts as having none); they get zero first-order delay to and from every
/// other domain. Empty when every domain is located.</param>
public sealed record PropagationReport(
    double WaveSpeedKmPerS,
    double MaxDelaySeconds,
    IReadOnlyList<double> DomainArrivalSeconds,
    IReadOnlyList<int>? UnlocatedDomainIds = null);
