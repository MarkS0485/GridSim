using System.Numerics;
using GridSim.Core.Diagnostics;

namespace GridSim.Core.Harmonics;

/// <summary>One line of a harmonic current spectrum: the order, its magnitude (percent of the source's
/// fundamental current), and its phase (degrees).</summary>
public sealed record HarmonicComponent(int Order, double MagnitudePercent, double PhaseDeg = 0.0);

/// <summary>
/// A harmonic current source at a bus - the ONE assumed input to the harmonic engine, built as a swappable,
/// provenance-tagged, measurement-ready seam. Its <see cref="Spectrum"/> gives the per-order injection as a
/// percent of <see cref="FundamentalCurrentPu"/> (the reference current); the physics (Y(h), the penetration
/// solve) is fully grounded and does not depend on where the spectrum came from. A default library spectrum
/// is <see cref="ProvenanceTag.CuratedPlaceholder"/> (assumed-typical, [VERIFY]); a measured spectrum loads
/// identically tagged <see cref="ProvenanceTag.Recorded"/> - the same solver, the tag the only difference.
/// </summary>
public sealed record HarmonicSource(
    int Bus,
    string SourceType,
    double FundamentalCurrentPu,
    IReadOnlyList<HarmonicComponent> Spectrum,
    ProvenanceTag Provenance = ProvenanceTag.CuratedPlaceholder,
    string? AssetId = null,
    string? Name = null)
{
    /// <summary>The complex injection current (pu) this source contributes at harmonic order
    /// <paramref name="order"/>, or 0 if its spectrum carries no such order.</summary>
    public Complex InjectionAt(int order)
    {
        foreach (HarmonicComponent c in Spectrum)
            if (c.Order == order)
                return DetMath.FromPolar(
                    (c.MagnitudePercent / 100.0) * FundamentalCurrentPu, c.PhaseDeg * Math.PI / 180.0);
        return Complex.Zero;
    }
}

/// <summary>
/// Typed default harmonic-source spectra - the library that lets a study run before any measured data exists.
/// Every spectrum here is <b>assumed-typical, literature-idealised</b>, so it is tagged
/// <see cref="ProvenanceTag.CuratedPlaceholder"/> and carries a <c>[VERIFY]</c> in its type name: a real
/// converter's spectrum depends on its control, loading and the local voltage, none of which these encode.
/// They exist so the grounded engine has an input to run on; a measured spectrum replaces one by swapping the
/// tag, not the code path.
/// </summary>
public static class HarmonicSourceLibrary
{
    /// <summary>Characteristic-harmonic spectrum I_h = I_1 / h for the given orders (the classic idealised
    /// converter shape), as percent components.</summary>
    private static IReadOnlyList<HarmonicComponent> Characteristic(params int[] orders)
        => orders.Select(h => new HarmonicComponent(h, 100.0 / h)).ToArray();

    /// <summary>Six-pulse rectifier / drive: characteristic orders 6k±1, magnitude ~1/h. [VERIFY]</summary>
    public static IReadOnlyList<HarmonicComponent> SixPulse => Characteristic(5, 7, 11, 13, 17, 19, 23, 25);

    /// <summary>Twelve-pulse converter: the 5th/7th cancel, leaving 12k±1. [VERIFY]</summary>
    public static IReadOnlyList<HarmonicComponent> TwelvePulse => Characteristic(11, 13, 23, 25, 35, 37);

    /// <summary>PWM voltage-source inverter / PV inverter: low characteristic distortion (~few % THD). [VERIFY]</summary>
    public static IReadOnlyList<HarmonicComponent> PvInverter => new[]
    {
        new HarmonicComponent(3, 2.0), new HarmonicComponent(5, 4.0), new HarmonicComponent(7, 3.0),
        new HarmonicComponent(11, 2.0), new HarmonicComponent(13, 1.5),
    };

    /// <summary>Single-phase EV charger: strong low-order odd incl. 3rd. [VERIFY]</summary>
    public static IReadOnlyList<HarmonicComponent> EvCharger => new[]
    {
        new HarmonicComponent(3, 12.0), new HarmonicComponent(5, 8.0), new HarmonicComponent(7, 5.0),
        new HarmonicComponent(9, 3.0), new HarmonicComponent(11, 2.0),
    };

    /// <summary>Domestic inverter-driven heat pump (single-phase VSD compressor, diode-bridge + DC-link front
    /// end): low-order odd harmonics dominated by the 3rd/5th/7th, ~15-20% THD_i at typical load. STRONGLY
    /// load-dependent - emissions rise sharply at low compressor speed (part-load), which coincides with mild
    /// weather; units with active PFC emit far less. This single curve cannot encode that, hence [VERIFY]. The
    /// per-bus injection is scaled by the measured HP fundamental (GDA heatpump_region_demand), so the SITING
    /// and AMOUNT are grounded even though the SHAPE here is assumed-typical. [VERIFY]</summary>
    public static IReadOnlyList<HarmonicComponent> HeatPumpInverter => new[]
    {
        new HarmonicComponent(3, 12.0), new HarmonicComponent(5, 9.0), new HarmonicComponent(7, 6.0),
        new HarmonicComponent(9, 4.0), new HarmonicComponent(11, 3.0), new HarmonicComponent(13, 2.0),
    };

    /// <summary>Arc furnace: broadband incl. even and low-order harmonics (interharmonics not modelled here). [VERIFY]</summary>
    public static IReadOnlyList<HarmonicComponent> ArcFurnace => new[]
    {
        new HarmonicComponent(2, 5.0), new HarmonicComponent(3, 8.0), new HarmonicComponent(4, 3.0),
        new HarmonicComponent(5, 6.0), new HarmonicComponent(7, 4.0),
    };

    /// <summary>Saturated transformer / inrush: dominant even and 3rd harmonics. [VERIFY]</summary>
    public static IReadOnlyList<HarmonicComponent> SaturatedTransformer => new[]
    {
        new HarmonicComponent(2, 15.0), new HarmonicComponent(3, 8.0), new HarmonicComponent(4, 4.0),
        new HarmonicComponent(5, 3.0),
    };

    // ---- S3 G02 (October 2026): the G5 Issue 5 emission assumptions, added ALONGSIDE the idealised spectra ----
    // Source: ENA Engineering Recommendation G5 Issue 5 (2020), docs/reference id S139, Annex B, "Table B.3 - Harmonic
    // current emission assumptions for three-phase equipment types" (listed as Table B.1 in the document's list of
    // tables), pp.99-100: Ih / I1 in per unit for orders 2 to 50, here as percent (x 100), orders printed as 0.000
    // omitted. G5 uses these profiles for its Stage 1C / 2A assessments of each technology. They are still typical
    // profiles, not a measured converter, so Make() tags them CuratedPlaceholder like the rest of the library; what the
    // grounding adds is a citable source for the SHAPE. The idealised "6-pulse" / "12-pulse" spectra above are NOT
    // replaced: a study names "6-pulse-g5", "12-pulse-g5" or "afe-g5". Since N131 (Ruling R1) "6-pulse-g5" is also the
    // default synthetic source (DefaultSyntheticSourceType below); "6-pulse" keeps its 1/h meaning.

    /// <summary>G5 Issue 5 Table B.3, six-pulse three-phase converter (THDI printed as 0.3441, which these rounded
    /// entries reproduce to 0.3441).</summary>
    public static IReadOnlyList<HarmonicComponent> SixPulseG5 => new[]
    {
        new HarmonicComponent(5, 31.4), new HarmonicComponent(7, 10.9), new HarmonicComponent(11, 7.0),
        new HarmonicComponent(13, 3.9), new HarmonicComponent(17, 2.7), new HarmonicComponent(19, 1.9),
        new HarmonicComponent(23, 1.0), new HarmonicComponent(25, 1.0), new HarmonicComponent(29, 0.6),
        new HarmonicComponent(31, 0.7), new HarmonicComponent(35, 0.6), new HarmonicComponent(37, 0.5),
        new HarmonicComponent(41, 0.4), new HarmonicComponent(43, 0.4), new HarmonicComponent(47, 0.3),
        new HarmonicComponent(49, 0.2),
    };

    /// <summary>G5 Issue 5 Table B.3, twelve-pulse three-phase converter (THDI printed as 0.0596; these rounded
    /// entries give 0.0596).</summary>
    public static IReadOnlyList<HarmonicComponent> TwelvePulseG5 => new[]
    {
        new HarmonicComponent(5, 0.4), new HarmonicComponent(7, 0.2), new HarmonicComponent(11, 4.8),
        new HarmonicComponent(13, 3.2), new HarmonicComponent(17, 0.1), new HarmonicComponent(23, 1.0),
        new HarmonicComponent(25, 0.8), new HarmonicComponent(35, 0.4), new HarmonicComponent(37, 0.4),
        new HarmonicComponent(47, 0.2),
    };

    /// <summary>G5 Issue 5 Table B.3, active-front-end three-phase converter (THDI printed as 0.0381; the
    /// three-decimal entries give 0.0386, the difference being the rounding of the many 0.001 entries).</summary>
    public static IReadOnlyList<HarmonicComponent> ActiveFrontEndG5 => new[]
    {
        new HarmonicComponent(2, 0.4), new HarmonicComponent(3, 0.5), new HarmonicComponent(4, 0.2),
        new HarmonicComponent(5, 3.4), new HarmonicComponent(6, 0.2), new HarmonicComponent(7, 1.5),
        new HarmonicComponent(8, 0.1), new HarmonicComponent(9, 0.1), new HarmonicComponent(10, 0.1),
        new HarmonicComponent(11, 0.4), new HarmonicComponent(12, 0.1), new HarmonicComponent(13, 0.4),
        new HarmonicComponent(15, 0.1), new HarmonicComponent(17, 0.1), new HarmonicComponent(19, 0.2),
        new HarmonicComponent(21, 0.1), new HarmonicComponent(22, 0.1), new HarmonicComponent(23, 0.2),
        new HarmonicComponent(25, 0.2), new HarmonicComponent(26, 0.1), new HarmonicComponent(27, 0.1),
        new HarmonicComponent(28, 0.1), new HarmonicComponent(29, 0.1), new HarmonicComponent(31, 0.1),
        new HarmonicComponent(35, 0.1), new HarmonicComponent(37, 0.1),
    };

    // ---- N131 (Ruling R1, Mark delegated 2026-10-04): the DEFAULT synthetic source ---------------------------------
    // Every study that injects a synthetic source because the user named none (the CLI `harmonics` verb without
    // --spectra / --converter-sources, `experimental harmonic-kcl`, the cycle's harmonics job and the web HARMONICS tab's
    // default choice) used the idealised 1/h six-pulse spectrum. It understates the cited G5 Issue 5 Table B.3 six-pulse
    // profile (5th 20 % vs 31.4 %, 7th 14.3 % vs 10.9 %, THDI 29.0 % over orders 5-25 vs 34.4 %), and PowerFactory / ETAP
    // ship typical measured spectra, not 1/h. The ruling: the cited G5 profile is the default; the idealised 1/h stays
    // available by name. The library NAMES keep their meaning ("6-pulse" is still the 1/h spectrum, so a spectra file
    // that names it is unchanged); only which name the defaults pick moves.

    /// <summary>The library type every default synthetic harmonic source uses (N131): the G5 Issue 5 Table B.3
    /// six-pulse profile, <c>6-pulse-g5</c>.</summary>
    public const string DefaultSyntheticSourceType = "6-pulse-g5";

    /// <summary>The library name of the idealised 1/h six-pulse spectrum when chosen in place of the default (N131):
    /// <c>ideal</c>, the same spectrum as <c>6-pulse</c> (<see cref="SixPulse"/>).</summary>
    public const string IdealSixPulseType = "ideal";

    /// <summary>The known source types keyed by name (for library lookup by a case/asset class).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<HarmonicComponent>> ByType { get; } =
        new Dictionary<string, IReadOnlyList<HarmonicComponent>>(StringComparer.OrdinalIgnoreCase)
        {
            ["6-pulse"] = SixPulse,
            ["12-pulse"] = TwelvePulse,
            ["pv-inverter"] = PvInverter,
            ["ev-charger"] = EvCharger,
            ["heat-pump-inverter"] = HeatPumpInverter,
            ["arc-furnace"] = ArcFurnace,
            ["saturated-transformer"] = SaturatedTransformer,
            // S3 G02: the G5 Issue 5 Table B.3 profiles (additive; see above).
            ["6-pulse-g5"] = SixPulseG5,
            ["12-pulse-g5"] = TwelvePulseG5,
            ["afe-g5"] = ActiveFrontEndG5,
            // N131: the idealised 1/h six-pulse spectrum by its ruled name (identical to "6-pulse"), for a study that
            // wants it in place of the G5 default.
            [IdealSixPulseType] = SixPulse,
        };

    /// <summary>Build a source of the given library type at a bus (assumed-typical, CuratedPlaceholder).</summary>
    public static HarmonicSource Make(string sourceType, int bus, double fundamentalCurrentPu, string? name = null)
    {
        if (!ByType.TryGetValue(sourceType, out IReadOnlyList<HarmonicComponent>? spectrum))
            throw new ArgumentException($"unknown harmonic source type '{sourceType}' (known: {string.Join(", ", ByType.Keys)})");
        return new HarmonicSource(bus, sourceType, fundamentalCurrentPu, spectrum, ProvenanceTag.CuratedPlaceholder, Name: name);
    }
}
