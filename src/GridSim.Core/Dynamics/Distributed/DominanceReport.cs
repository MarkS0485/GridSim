using System.Collections.Generic;

namespace GridSim.Core.Dynamics.Distributed;

/// <summary>
/// The speed-domain dominance read-out (LOS sec. 5.5). Domains ordered by <c>GVad</c>, the dominance
/// gap <c>deltaGVa = (GVad1 - GVad2) / GVad2</c>, and whether a dominant domain exists
/// (<c>deltaGVa >= threshold</c>, ~0.10). A large deltaGVa means one domain can carry waveform authority;
/// a small one means inertia is too evenly distributed (the LOS failure mode a lumped total can't see).
/// </summary>
public sealed record DominanceReport(
    IReadOnlyList<SpeedDomain> DomainsByGvadDesc,
    double DeltaGVa,
    double Threshold,
    int? DominantDomainId)
{
    /// <summary>True when a dominant speed domain exists (deltaGVa >= threshold).</summary>
    public bool HasDominant => DominantDomainId is not null;
}
