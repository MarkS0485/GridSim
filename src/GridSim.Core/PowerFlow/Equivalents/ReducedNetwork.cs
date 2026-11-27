using System.Numerics;
using GridSim.Core.Model;

namespace GridSim.Core.PowerFlow.Equivalents;

/// <summary>The kind of an element <see cref="NetworkReduction.Reduce"/> added to the reduced case.</summary>
public enum EquivalentElementKind
{
    /// <summary>An equivalent branch "EQ:i-j" between two boundary buses; <see cref="EquivalentElement.Value"/> is its
    /// series admittance y (pu).</summary>
    Branch,
    /// <summary>An equivalent shunt at a boundary bus, the row sum of the equivalent admittance (pu, added to Gs + jBs).</summary>
    Shunt,
    /// <summary>A constant-power injection at a boundary bus, the external currents transferred to it (pu, taken off
    /// Pd + jQd).</summary>
    Injection,
    /// <summary>An extended-Ward source "XW:b": a fictitious PV bus joined to boundary bus b by the admittance d_b (pu).</summary>
    ExtendedWardSource,
    /// <summary>An REI bus "REI:gen" or "REI:load"; <see cref="EquivalentElement.Value"/> is the aggregated injection
    /// S_R (pu).</summary>
    ReiBus,
}

/// <summary>One element of an equivalent: its kind, name, the buses it touches (for a branch or source, from and to)
/// and its value in pu on the system base.</summary>
public sealed record EquivalentElement(EquivalentElementKind Kind, string Name, IReadOnlyList<int> BusIds, Complex Value);

/// <summary>
/// The result of <see cref="NetworkReduction.Reduce"/>: the reduced case (an ordinary <see cref="GridModel"/>, so every
/// study runs on it), the boundary buses in model order, the external buses removed, the elements added and notes on
/// what was removed or not carried.
/// </summary>
public sealed record ReducedNetwork(
    GridModel Model,
    IReadOnlyList<int> BoundaryBusIds,
    IReadOnlyList<int> ExternalBusIds,
    IReadOnlyList<EquivalentElement> Elements,
    IReadOnlyList<string> Notes);
