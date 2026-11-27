using GridSim.Core.Diagnostics;

namespace GridSim.Core.Lines.Cables;

/// <summary>How the metallic layers (sheath and armour) of the cables are bonded.</summary>
public enum CableBonding
{
    /// <summary>Solidly bonded and earthed at both ends: the metallic layers are at earth potential and carry current (Kron elimination).</summary>
    BothEnds,
    /// <summary>Bonded at one point only: the metallic layers carry no current (dropped from the series path).</summary>
    SinglePoint,
    /// <summary>Ideal cross-bonding: each metallic layer takes every cable position for a third of a major section, then is earthed (rotation average, then Kron elimination). Needs exactly three cables with the same metallic layers (a sheath, and an armour on all or none); their dimensions and materials may differ.</summary>
    CrossBonded,
}

/// <summary>The earth-return formula for buried cables.</summary>
public enum CableEarthMethod
{
    /// <summary>Wedepohl and Wilcox's closed form (valid while |m_e| times the largest of the distance and the depth sum is small).</summary>
    Wedepohl,
    /// <summary>The exact Pollaczek integral, by deterministic quadrature.</summary>
    Pollaczek,
}

/// <summary>
/// One conductor of a cable (core, sheath or armour), radii in metres. A core with <see cref="InnerRadiusM"/> 0 is a solid
/// round conductor; any other conductor is a tube. <see cref="ResistivityOhmM"/> is the resistivity at the stated
/// temperature (a stranded core is entered with the resistivity that gives its DC resistance on the stated radius).
/// </summary>
public sealed record CableConductor(double InnerRadiusM, double OuterRadiusM, double ResistivityOhmM, double RelativePermeability = 1.0);

/// <summary>
/// The insulation outside one conductor. It runs from that conductor's outer radius to the next conductor's inner radius,
/// or, for the outermost layer, to the cable's outer radius. Semiconducting screens are part of the insulation: enter the
/// permittivity already corrected for them, if a correction is wanted. <see cref="LossTangent"/> gives the dielectric
/// conductance G = w C tan(delta).
/// </summary>
public sealed record CableInsulation(double RelativePermittivity, double LossTangent = 0.0, double RelativePermeability = 1.0);

/// <summary>
/// One single-core cable: its centre at horizontal position <see cref="XM"/> and depth <see cref="DepthM"/> below the
/// surface, the conductors from the inside out (core first, then sheath, then armour; one to three), one insulation layer
/// outside each conductor, and the outer radius over the last insulation layer.
/// </summary>
public sealed record SingleCoreCable(
    string Name,
    double XM,
    double DepthM,
    IReadOnlyList<CableConductor> Conductors,
    IReadOnlyList<CableInsulation> Insulation,
    double OuterRadiusM)
{
    /// <summary>The name of conductor <paramref name="layer"/>: core, sheath, armour.</summary>
    public string ConductorName(int layer) => Name + "." + (layer switch { 0 => "core", 1 => "sheath", _ => "armour" });

    /// <summary>The outer radius of insulation layer <paramref name="layer"/>.</summary>
    public double InsulationOuterRadius(int layer) => layer + 1 < Conductors.Count ? Conductors[layer + 1].InnerRadiusM : OuterRadiusM;
}

/// <summary>A buried single-core cable system and its study conditions. <see cref="Validate"/> refuses an unusable one.</summary>
public sealed record CableGeometry(
    IReadOnlyList<SingleCoreCable> Cables,
    double FrequencyHz,
    double EarthResistivityOhmM,
    CableEarthMethod Earth,
    CableBonding Bonding,
    bool Transposed)
{
    /// <summary>Refuse names, radii, layer order, materials, positions, frequency or resistivity that make no physical sense, and a bonding the system cannot have.</summary>
    public void Validate()
    {
        static Exception R(string m) => DomainRefusal.Mark(new ArgumentException(m));
        static bool Pos(double v) => double.IsFinite(v) && v > 0;
        if (Cables is null || Cables.Count == 0) throw R("The cable system has no cables.");
        if (!Pos(FrequencyHz)) throw R("The frequency must be finite and positive.");
        if (!Pos(EarthResistivityOhmM)) throw R("The earth resistivity must be finite and positive.");
        if (!Enum.IsDefined(Earth)) throw R("The earth-return method is not known.");
        if (!Enum.IsDefined(Bonding)) throw R("The bonding is not known.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in Cables)
        {
            if (c is null || string.IsNullOrEmpty(c.Name) || !names.Add(c.Name)) throw R("Cable names must be non-empty and unique.");
            if (!double.IsFinite(c.XM)) throw R($"Cable {c.Name}: x is not finite.");
            if (!Pos(c.DepthM)) throw R($"Cable {c.Name}: the depth must be positive.");
            if (c.Conductors is null || c.Conductors.Count is < 1 or > 3)
                throw R($"Cable {c.Name}: a single-core cable has one to three conductors (core, sheath, armour).");
            if (c.Insulation is null || c.Insulation.Count != c.Conductors.Count)
                throw R($"Cable {c.Name}: there must be one insulation layer outside each conductor.");
            for (int k = 0; k < c.Conductors.Count; k++)
            {
                var m = c.Conductors[k];
                string what = c.ConductorName(k);
                if (m is null) throw R($"{what}: missing.");
                if (!(double.IsFinite(m.InnerRadiusM) && m.InnerRadiusM >= 0)) throw R($"{what}: the inner radius must be finite and not negative.");
                if (k > 0 && !(m.InnerRadiusM > 0)) throw R($"{what}: a sheath or armour is a tube; its inner radius must be positive.");
                if (!(Pos(m.OuterRadiusM) && m.OuterRadiusM > m.InnerRadiusM)) throw R($"{what}: the outer radius must exceed the inner radius.");
                if (!Pos(m.ResistivityOhmM)) throw R($"{what}: the resistivity must be finite and positive.");
                if (!Pos(m.RelativePermeability)) throw R($"{what}: the relative permeability must be finite and positive.");
                if (k > 0 && !(m.InnerRadiusM > c.Conductors[k - 1].OuterRadiusM))
                    throw R($"{what}: it must lie outside the previous conductor with insulation between them.");
                var ins = c.Insulation[k];
                if (ins is null) throw R($"{what}: the insulation outside it is missing.");
                if (!(double.IsFinite(ins.RelativePermittivity) && ins.RelativePermittivity >= 1.0))
                    throw R($"{what}: the relative permittivity of the insulation outside it must be finite and at least 1.");
                if (!(double.IsFinite(ins.LossTangent) && ins.LossTangent >= 0)) throw R($"{what}: the loss tangent must be finite and not negative.");
                if (!Pos(ins.RelativePermeability)) throw R($"{what}: the insulation's relative permeability must be finite and positive.");
            }
            if (!(Pos(c.OuterRadiusM) && c.OuterRadiusM > c.Conductors[c.Conductors.Count - 1].OuterRadiusM))
                throw R($"Cable {c.Name}: the outer radius must exceed the outermost conductor's (an outer insulation layer is required).");
            if (c.DepthM <= c.OuterRadiusM) throw R($"Cable {c.Name}: the cable must lie wholly below the surface.");
        }
        for (int i = 0; i < Cables.Count; i++)
            for (int k = i + 1; k < Cables.Count; k++)
            {
                var a = Cables[i]; var b = Cables[k];
                double dx = a.XM - b.XM, dy = a.DepthM - b.DepthM;
                if (Math.Sqrt(dx * dx + dy * dy) <= a.OuterRadiusM + b.OuterRadiusM)
                    throw R($"Cables {a.Name} and {b.Name} coincide or overlap.");
            }
        if (Bonding == CableBonding.CrossBonded)
        {
            if (Cables.Count != 3) throw R("Cross-bonding needs exactly three cables.");
            int n = Cables[0].Conductors.Count;
            if (n < 2 || Cables.Any(c => c.Conductors.Count != n))
                throw R("Cross-bonding needs every cable to have the same metallic layers (a sheath, and an armour on all or none).");
        }
    }
}
