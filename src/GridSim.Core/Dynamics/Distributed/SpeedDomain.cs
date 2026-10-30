using System.Collections.Generic;

namespace GridSim.Core.Dynamics.Distributed;

/// <summary>
/// A speed domain (LOS sec. 5.5): a group of machines sharing a mechanical speed / pole ratio, treated
/// as one coherent inertial body during a transient. Synchronous domains (E &gt; 0) are the swing
/// masses; virtual (inverter) domains carry apparent-power stiffness <see cref="Gvad"/> but no
/// rotational inertia, so they contribute to dominance accounting but not to the electromechanical swing.
/// </summary>
public sealed record SpeedDomain(
    int Id,
    int Poles,
    double Rpm,
    DomainKind Kind,
    IReadOnlyList<int> MemberGenIndices,
    double AggregateInertiaMwS,   // E_d = sum H_i*MBase_i over members (MW.s; 0 for virtual)
    double RatedMva,              // S_d = sum MBase_i (MVA)
    double Gvad,                  // sum (S_i * C_i) - LOS apparent-power stiffness
    double CouplingC)             // representative availability/coupling C in [0,1]
{
    /// <summary>True if this domain participates in the electromechanical swing (synchronous, E &gt; 0).</summary>
    public bool IsSwingMass => Kind == DomainKind.Synchronous && AggregateInertiaMwS > 0.0;

    /// <summary>Aggregate inertia in GVA.s.</summary>
    public double AggregateInertiaGvaS => AggregateInertiaMwS / 1000.0;
}
