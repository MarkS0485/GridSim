using GridSim.Core.Assets;
using GridSim.Core.Model;

namespace GridSim.Core.Simulation;

/// <summary>
/// Model-level inertia/composition transforms for the H=0 counterfactual harness, beside
/// <see cref="GridScenario.AtLoad"/> and sharing its immutable-copy idiom (every property is copied,
/// including <see cref="GridModel.AssetDynamics"/> / <see cref="GridModel.Protections"/>). Unlike
/// <c>AtLoad</c> these do NOT re-dispatch: they change inertia and binding metadata only, never Pg/Pd,
/// so the swing energy E and the converter fleet composition move but dispatch does not.
///
/// <para>The two transforms are the study's two independent axes and the codebase's real-vs-synthetic
/// separation is load-bearing here: <see cref="ScaleSynchronousInertia"/> scales REAL (1/2)Jw^2 that
/// enters E; <see cref="SetGfmFraction"/> changes how much of the CONVERTER fleet is grid-forming
/// (which becomes virtual df/dt control inertia in the forming denominator, never real E). Neither
/// touches the other's quantity: scaling never rewrites a binding, and setting the GFM fraction never
/// rewrites an <see cref="Generator.H"/>.</para>
/// </summary>
public static class GridInertiaTransforms
{
    /// <summary>
    /// Scale the SYNCHRONOUS rotational inertia constant H of every rotating unit by
    /// <paramref name="scale"/> in [0,1]. Converter-connected units already at <c>H==0</c>
    /// (wind/solar/storage/interconnector) are untouched (0*scale==0). <c>scale==1</c> is the identity
    /// (a byte-identical copy of the generators); <c>scale==0</c> is a genuine H=0 grid
    /// (<c>sum(H*MBase)==0</c>, so <see cref="GridModel.SystemInertiaGvaS"/>==0). This is REAL
    /// (1/2)Jw^2 energy in E - never synthetic. Inline <see cref="Generator.DynamicsClass"/> tags are
    /// preserved, so this composes AFTER <see cref="SetGfmFraction"/> without disturbing the fleet.
    /// </summary>
    public static GridModel ScaleSynchronousInertia(GridModel m, double scale)
    {
        if (!(scale >= 0.0 && scale <= 1.0))   // NaN-safe: a NaN scale set every synchronous H to NaN
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Synchronous-inertia scale must be in [0,1].");

        var gens = m.Generators.Select(g => g.H == 0.0 ? g : g with { H = g.H * scale }).ToList();
        return With(m, gens);
    }

    /// <summary>
    /// Set the grid-forming fraction of the CONVERTER fleet. The converter set is every unit with
    /// <c>H==0</c> that the model's bindings resolve to an INVERTER class (grid-following OR
    /// grid-forming) - so synchronous plant (H&gt;0) and unbound converters (e.g. interconnectors that
    /// no rule matches) are left exactly as they are. Ordered deterministically by DESCENDING |Pg|,
    /// then Bus, then Name, the largest converter units up to <paramref name="gfmFraction"/> of total
    /// converter MW are tagged (inline <see cref="Generator.DynamicsClass"/>, which wins over rules per
    /// <see cref="AssetBindingSet.Resolve"/>) with <paramref name="gfmClass"/>; the remainder with
    /// <paramref name="gflClass"/>. <c>gfmFraction</c> in [0,1]; 0 =&gt; all GFL, 1 =&gt; all GFM.
    ///
    /// <para>MUST be applied to the UNSCALED base model: keying the converter set off <c>H==0</c> is
    /// only correct while synchronous machines still carry their real H (after
    /// <see cref="ScaleSynchronousInertia"/> to 0 every machine would read as a converter). Applying
    /// GFM-fraction first then scaling keeps the fraction scale-invariant and never retags a
    /// former-synchronous unit as a trippable inverter.</para>
    /// </summary>
    public static GridModel SetGfmFraction(GridModel m, double gfmFraction, string gfmClass, string gflClass)
        => SetGfmFraction(m, gfmFraction, gfmClass, gflClass, out _);

    /// <summary>
    /// <see cref="SetGfmFraction(GridModel, double, string, string)"/>, also returning the fraction it REALISED.
    /// <para>R289 (B4, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04). Converter units are whole units, so
    /// the greedy fill (largest first, until the tagged MW reaches the target) can overshoot the request by up to one
    /// unit's MW: on the H0StudyEngineTests fleet (wind 2,000 / 1,500, storage 800 / 600 MW) a 0.25 request tags the
    /// 2,000 MW unit, a realised 2000 / 4900 = 0.408. The study used to record only the requested value, so plotted axes
    /// were mislabelled (the physics, Ev / Dv, was always computed from the real tagging). The tagging rule is kept (so no
    /// computed number moves) and the realised share is returned: tagged GFM |Pg| / total converter |Pg|, summed in the
    /// same order as the fill; 0 when the converter fleet has no MW (nothing to share), whatever was tagged.</para>
    /// </summary>
    public static GridModel SetGfmFraction(GridModel m, double gfmFraction, string gfmClass, string gflClass,
        out double realisedGfmFraction)
    {
        if (!(gfmFraction >= 0.0 && gfmFraction <= 1.0))   // NaN-safe: a NaN fraction tagged every converter GFM
            throw new ArgumentOutOfRangeException(nameof(gfmFraction), gfmFraction, "GFM fraction must be in [0,1].");
        if (m.AssetDynamics is not { } spec)
            throw new InvalidOperationException(
                "SetGfmFraction requires an asset-class library (model.AssetDynamics is null) - ship asset-classes.json + asset-bindings.json.");

        // Validate both class names up front (fail loud, same style as AssetClassLibrary.Get).
        AssetClass gfm = spec.Library.Get(gfmClass);
        _ = spec.Library.Get(gflClass);
        if (gfm.Kind != AssetKind.GridFormingInverter)
            throw new InvalidDataException($"GFM class '{gfmClass}' is kind {gfm.Kind}, not a grid-forming inverter.");

        // The converter set: H==0 AND resolves (via the BASE bindings) to an inverter class.
        var converters = m.Generators
            .Select((g, i) => (g, i))
            .Where(t => t.g.H == 0.0 && IsInverter(spec, t.g))
            .OrderByDescending(t => Math.Abs(t.g.Pg))
            .ThenBy(t => t.g.Bus)
            .ThenBy(t => t.g.Name, StringComparer.Ordinal)
            .Select(t => t.i)
            .ToList();

        double totalConverterMw = converters.Sum(i => Math.Abs(m.Generators[i].Pg));
        double targetGfmMw = gfmFraction * totalConverterMw;

        var gfmIndices = new HashSet<int>();
        double acc = 0.0;
        foreach (int i in converters)
        {
            if (acc >= targetGfmMw) break;
            gfmIndices.Add(i);
            acc += Math.Abs(m.Generators[i].Pg);
        }
        // A positive fraction with zero-MW converters (or rounding) still tags at least the leading unit.
        if (gfmFraction > 0.0 && gfmIndices.Count == 0 && converters.Count > 0) gfmIndices.Add(converters[0]);

        // R289: the share actually tagged, in MW of the converter fleet (the fill order above, so acc is reproduced
        // exactly when the leading-unit rule did not fire; recomputed from the tagged set either way).
        double taggedMw = 0.0;
        foreach (int i in converters) if (gfmIndices.Contains(i)) taggedMw += Math.Abs(m.Generators[i].Pg);
        realisedGfmFraction = totalConverterMw > 0.0 ? taggedMw / totalConverterMw : 0.0;

        var converterSet = new HashSet<int>(converters);
        var gens = m.Generators.Select((g, i) =>
            converterSet.Contains(i)
                ? g with { DynamicsClass = gfmIndices.Contains(i) ? gfmClass : gflClass }
                : g).ToList();

        return With(m, gens);
    }

    private static bool IsInverter(AssetDynamicsModel spec, Generator g)
    {
        string? cn = spec.Bindings.Resolve(g.DynamicsClass, g.Type);
        if (cn is null) return false;
        AssetKind kind = spec.Library.Get(cn).Kind;
        return kind is AssetKind.GridFollowingInverter or AssetKind.GridFormingInverter;
    }

    /// <summary>Immutable copy of <paramref name="m"/> with a new generator list; every other property preserved.
    /// D-DOC-206 (October 2026): through <see cref="GridModel.CopyWith"/>. The list given is built from
    /// <see cref="GridModel.Generators"/> (the in-service units), and the hand-written copy used it as the whole fleet, so
    /// every out-of-service unit was dropped. CopyWith appends them again (kept, never solved), and the copy's
    /// <see cref="GridModel.Generators"/> is exactly the list given, so every engine reads what it read before.</summary>
    internal static GridModel With(GridModel m, IReadOnlyList<Generator> generators) => m.CopyWith(generators: generators);
}
