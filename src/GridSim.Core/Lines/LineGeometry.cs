using GridSim.Core.Diagnostics;

namespace GridSim.Core.Lines;

/// <summary>The earth-return method used for the primitive impedance matrix.</summary>
public enum EarthReturnMethod
{
    /// <summary>Perfect ground: image conductors only (infinite conductivity).</summary>
    PerfectGround,
    /// <summary>The exact earth-return integral (Pollaczek/Carson) by deterministic quadrature.</summary>
    EarthIntegral,
}

/// <summary>An overhead line cross-section and its study conditions. <see cref="Validate"/> refuses an unusable geometry.</summary>
public sealed record LineGeometry(
    IReadOnlyList<ConductorData> Conductors,
    double FrequencyHz,
    double EarthResistivityOhmM,
    EarthReturnMethod Method,
    bool Transposed)
{
    /// <summary>Refuse names, heights, radii, coincident conductors, frequency or resistivity that make no physical sense.</summary>
    public void Validate()
    {
        static Exception R(string m) => DomainRefusal.Mark(new ArgumentException(m));
        if (Conductors is null || Conductors.Count == 0) throw R("The geometry has no conductors.");
        if (!(double.IsFinite(FrequencyHz) && FrequencyHz > 0)) throw R("The frequency must be finite and positive.");
        if (Method == EarthReturnMethod.EarthIntegral && !(double.IsFinite(EarthResistivityOhmM) && EarthResistivityOhmM > 0))
            throw R("The earth resistivity must be finite and positive.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in Conductors)
        {
            if (string.IsNullOrEmpty(c.Name) || !names.Add(c.Name)) throw R("Conductor names must be non-empty and unique.");
            if (!double.IsFinite(c.XM)) throw R($"Conductor {c.Name}: x is not finite.");
            if (!(double.IsFinite(c.HeightM) && c.HeightM > 0)) throw R($"Conductor {c.Name}: the height above ground must be positive.");
            if (!(double.IsFinite(c.ResistanceOhmPerKm) && c.ResistanceOhmPerKm >= 0)) throw R($"Conductor {c.Name}: the resistance must be finite and not negative.");
            if (!(double.IsFinite(c.GmrM) && c.GmrM > 0)) throw R($"Conductor {c.Name}: the GMR must be positive.");
            if (!(double.IsFinite(c.OuterRadiusM) && c.OuterRadiusM > 0)) throw R($"Conductor {c.Name}: the radius must be positive.");
            if (c.GmrM > c.OuterRadiusM * 1.0000001) throw R($"Conductor {c.Name}: the GMR cannot exceed the outer radius.");
            if (c.HeightM <= c.OuterRadiusM) throw R($"Conductor {c.Name}: the conductor must lie wholly above ground.");
        }
        for (int i = 0; i < Conductors.Count; i++)
            for (int k = i + 1; k < Conductors.Count; k++)
            {
                var a = Conductors[i]; var b = Conductors[k];
                double dx = a.XM - b.XM, dy = a.HeightM - b.HeightM;
                if (Math.Sqrt(dx * dx + dy * dy) <= a.OuterRadiusM + b.OuterRadiusM)
                    throw R($"Conductors {a.Name} and {b.Name} coincide or overlap.");
            }
        if (!Conductors.Any(c => c.Role == ConductorRole.Phase)) throw R("The geometry has no phase conductor.");
    }
}
