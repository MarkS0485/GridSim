using System.Numerics;

namespace GridSim.Core.Distribution.Meshed;

/// <summary>
/// RC4 R4-15: the North American 120/240 V split-phase service in the phase frame. Wires l1 and l2 are the two 120 V lines,
/// n the centre-tap neutral. The secondary is a centre-tapped winding: l1 to the tap is half the winding and the tap to l2 the
/// other half (series-aiding), so with the tap at neutral l1 and l2 are 180 degrees apart.
/// </summary>
public static class SplitPhase
{
    /// <summary>The wire names of a split-phase node.</summary>
    public static readonly IReadOnlyList<string> Wires = ["l1", "l2", "n"];

    /// <summary>An ideal split-phase source: l1 = +V/2, l2 = -V/2 (V the full winding voltage, for example 240), with the neutral
    /// wire n fixed at 0 V (a solidly earthed centre tap). With <paramref name="seriesZ"/> the two lines are reached through
    /// that impedance each.</summary>
    public static FrameSource IdealSource(string node, double volts, Complex? seriesZ = null)
        => new(node, ["l1", "l2", "n"], [new Complex(volts / 2.0, 0), new Complex(-volts / 2.0, 0), Complex.Zero],
            seriesZ is { } z ? [z, z, Complex.Zero] : null);

    /// <summary>A centre-tap distribution transformer as one single-core unit with three windings: the primary between
    /// <paramref name="primaryWire"/> and <paramref name="primaryReturn"/> at <paramref name="primaryNode"/> (rated
    /// <paramref name="primaryVolts"/>), and the secondary halves l1-n and n-l2 at <paramref name="secondaryNode"/> (each
    /// <paramref name="secondaryVolts"/>/2). Leakage impedances are on each winding's own side: <paramref name="zPrimary"/>
    /// and <paramref name="zHalf"/> for each secondary half. Magnetising current is neglected.</summary>
    public static FrameTransformerUnit CentreTapUnit(string name, string primaryNode, string primaryWire, string? primaryReturn,
        double primaryVolts, string secondaryNode, double secondaryVolts, Complex zPrimary, Complex zHalf)
    {
        if (!(primaryVolts > 0 && secondaryVolts > 0 && double.IsFinite(primaryVolts) && double.IsFinite(secondaryVolts)))
            throw new ArgumentException("rated voltages must be finite and above 0.");
        return new FrameTransformerUnit(name,
        [
            new FrameWinding(primaryNode, primaryWire, primaryReturn, primaryVolts, zPrimary),
            new FrameWinding(secondaryNode, "l1", "n", secondaryVolts / 2.0, zHalf),
            new FrameWinding(secondaryNode, "n", "l2", secondaryVolts / 2.0, zHalf),
        ]);
    }
}
