using GridSim.Core.Dynamics.Emt.Nodal;

namespace GridSim.Core.Dynamics.Emt.Nonlinear;

/// <summary>Parameters of a single-phase two-winding transformer in the T model, secondary referred to the primary side
/// (so the network on the secondary side is also in primary-referred volts, amps and ohms).</summary>
/// <param name="R1">Primary winding resistance, ohm (not negative).</param>
/// <param name="Ll1">Primary leakage inductance, henry (positive).</param>
/// <param name="R2">Secondary winding resistance referred to the primary, ohm (not negative).</param>
/// <param name="Ll2">Secondary leakage inductance referred to the primary, henry (positive).</param>
/// <param name="CoreLossOhm">A linear core-loss resistance across the magnetising branch, ohm; 0 for none.</param>
/// <param name="Magnetising">The magnetising current-flux curve (x = Wb, y = A), monotone and odd.</param>
/// <param name="ResidualFlux">Initial magnetising flux linkage, Wb (single-valued curve: see <see cref="SaturableInductor"/>).</param>
public sealed record SaturableTransformerData(double R1, double Ll1, double R2, double Ll2, double CoreLossOhm,
    PiecewiseCurve Magnetising, double ResidualFlux = 0.0);

/// <summary>The elements a <see cref="SaturableTransformer"/> added, for probing.</summary>
/// <param name="MagnetisingNode">The internal node name between the leakage arms.</param>
/// <param name="Primary">The primary series R-L element.</param>
/// <param name="Secondary">The secondary series R-L element.</param>
/// <param name="Core">The saturable magnetising inductor.</param>
public sealed record SaturableTransformerParts(string MagnetisingNode, NodalBranch Primary, NodalBranch Secondary, SaturableInductor Core);

/// <summary>
/// A saturable single-phase transformer as a composition of existing elements (so the Newton iteration is the only new
/// machinery): primary series R-L, a saturable magnetising inductor (and optional core-loss resistor) at an internal node,
/// then the secondary series R-L. No ideal transformer is needed because the secondary is referred to the primary. Three
/// phases are three of these (a three single-phase bank with grounded neutrals; there is no limb-to-limb core coupling and
/// no zero-sequence flux path, which the manual states as not modelled).
/// </summary>
public static class SaturableTransformer
{
    /// <summary>Add a transformer named <paramref name="name"/> between the node names <paramref name="primaryNode"/> and
    /// <paramref name="secondaryNode"/>, plus an internal node <c>name.m</c>.</summary>
    public static SaturableTransformerParts AddTo(NodalNetwork net, string name, string primaryNode, string secondaryNode, SaturableTransformerData d)
    {
        ArgumentNullException.ThrowIfNull(net);
        ArgumentNullException.ThrowIfNull(d);
        if (!(d.R1 >= 0) || !(d.R2 >= 0) || !(d.Ll1 > 0) || !(d.Ll2 > 0) || !(d.CoreLossOhm >= 0))
            throw new ArgumentException($"{name}: winding resistances and the core-loss resistance must not be negative and the leakage inductances must be positive.");
        int p = net.Node(primaryNode), s = net.Node(secondaryNode);
        string mName = name + ".m";
        int m = net.Node(mName);
        NodalBranch prim = net.Add(NodalBranch.SeriesRl(name + ".primary", p, m, d.R1, d.Ll1));
        SaturableInductor core = net.Add(new SaturableInductor(name + ".core", m, NodalNetwork.Ground, d.Magnetising, d.ResidualFlux));
        if (d.CoreLossOhm > 0) net.Add(NodalBranch.Resistor(name + ".coreloss", m, NodalNetwork.Ground, d.CoreLossOhm));
        NodalBranch sec = net.Add(NodalBranch.SeriesRl(name + ".secondary", m, s, d.R2, d.Ll2));
        return new SaturableTransformerParts(mName, prim, sec, core);
    }
}
