namespace GridSim.Core.PowerFlow.Equivalents;

/// <summary>The kind of static network equivalent <see cref="NetworkReduction.Reduce"/> builds.</summary>
public enum EquivalentMethod
{
    /// <summary>The Ward equivalent: the external network folded into boundary branches, shunts and constant-power
    /// injections (Ward 1949).</summary>
    Ward,
    /// <summary>The extended Ward equivalent: Ward plus one voltage-controlled fictitious source per boundary bus that
    /// stands in for the reactive support of the external PV buses (Deckmann et al. 1980).</summary>
    ExtendedWard,
    /// <summary>The REI equivalent: the external injections of a group moved to one fictitious bus through a
    /// zero-power-balance network, then Ward (Dimo 1975; Tinney and Powell 1977).</summary>
    Rei,
    /// <summary>RC5 WP31: the short-circuit equivalent, every IEC 60909 sequence network Kron-reduced onto the boundary
    /// (<see cref="ShortCircuitEquivalent.Reduce"/>; not built by <see cref="NetworkReduction.Reduce"/>).</summary>
    ShortCircuit,
    /// <summary>RC5 WP31: the coherency-based dynamic equivalent, each group of coherent machines aggregated into one
    /// classical machine (<see cref="GridSim.Core.Dynamics.Equivalents.InertialAggregation.Build"/>; not built by
    /// <see cref="NetworkReduction.Reduce"/>).</summary>
    Dynamic,
}

/// <summary>Which external injections the REI equivalent aggregates.</summary>
public enum ReiGrouping
{
    /// <summary>One group: every in-service external generator. The external loads become Ward injections.</summary>
    Generators,
    /// <summary>Two groups: every in-service external generator (a PV bus) and every external load (a PQ bus).</summary>
    GeneratorsAndLoads,
}

/// <summary>
/// What to reduce: the buses to keep (<paramref name="InternalBusIds"/>, the internal and boundary buses together; the
/// boundary is computed) and the equivalent to build. Every other bus is external and is replaced by the equivalent.
/// </summary>
public sealed record ReductionSpec(IReadOnlyList<int> InternalBusIds, EquivalentMethod Method, ReiGrouping Grouping = ReiGrouping.Generators);
