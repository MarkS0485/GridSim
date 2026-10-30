using System.Globalization;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Harmonics;

/// <summary>
/// The fundamental current a converter source's G5 profile is scaled by (N036): the profile gives each harmonic as a
/// fraction of the converter's fundamental current I1, and this says which I1.
/// </summary>
public enum ConverterHarmonicLevel
{
    /// <summary>
    /// I1 = the unit's RATED current: <c>MBase / BaseMva</c> pu on the system base at 1 pu voltage. This is the basis
    /// G5 Issue 5 assesses converter plant on (Stage 1C / 2A use the equipment's rated power, S139 s.7.4.1.1 eq. (10)
    /// p.50 and s.8.2.1.1 p.61), and it is the conservative choice: every in-service converter injects its full-output
    /// emission whatever it is dispatched at. The default.
    /// </summary>
    Rated,

    /// <summary>
    /// I1 = the unit's DISPATCHED current at 1 pu voltage: <c>sqrt(P^2 + Q^2) / BaseMva</c> pu. With a solved operating
    /// point (the <c>operatingPoint</c> argument of <see cref="ConverterHarmonicSources.FromModel"/>), P and Q are the
    /// unit's SOLVED output (<see cref="SolvedGeneration.UnitOutput"/>: the slack's P and every PV unit's Q come
    /// from the solve; N150, 1.95.243); without one, the generator's scheduled Pg and Qg. A unit at zero output injects
    /// nothing and is listed as not modelled. Not a G5 basis: G5 profiles are
    /// typical full-load shapes, and a converter's spectrum at part load is not simply the full-load shape scaled
    /// down ([VERIFY]; no document on disk gives part-load profiles).
    /// </summary>
    Dispatched,
}

/// <summary>
/// The per-converter harmonic sources of a model (N036): one <see cref="HarmonicSource"/> per in-service
/// converter-interfaced generator, plus what the rule left out and why. Nothing is dropped without a line here.
/// </summary>
/// <param name="Sources">One source per modelled unit, in <see cref="GridModel.Generators"/> order.</param>
/// <param name="OmittedComponents">Profile components NOT placed in a source, each named
/// ("unit U at bus B (profile) order h at m %: reason"). In the balanced study these are the triplen (zero-sequence)
/// orders, which the positive-sequence network cannot carry (R029).</param>
/// <param name="UnitsNotModelled">Converter units that became no source, each named with the reason (a power-flow-only
/// device; zero output at the dispatched level).</param>
public sealed record ConverterHarmonicSourceSet(
    IReadOnlyList<HarmonicSource> Sources,
    IReadOnlyList<string> OmittedComponents,
    IReadOnlyList<string> UnitsNotModelled);

/// <summary>
/// N036 (October 2026): the Core rule that turns every converter-interfaced generator of a model into a harmonic
/// current source, so a study need not rely on one synthetic six-pulse source at the largest-demand bus (the CLI and
/// web default, which is unchanged and stays the default; this rule is opt-in and additive).
///
/// <para><b>Which units.</b> Every in-service generator with <see cref="Generator.IsConverterInterfaced"/> true
/// (battery, PV, type-4 wind, tidal, HVDC / interconnector: <see cref="ConverterPlant"/>, or an explicit
/// <c>converterInterfaced</c>), except power-flow-only devices (<see cref="Generator.PowerFlowOnly"/>: a slack balancer
/// or control unit is not physical plant). Synchronous machines inject no characteristic harmonic current in this
/// engine; they are the harmonic SINKS (<see cref="HarmonicModelOptions.IncludeGeneratorSources"/>).</para>
///
/// <para><b>Which spectrum.</b> The technology profiles of ENA EREC G5 Issue 5 (2020), docs/reference S139, Annex B
/// Table B.3 "Harmonic current emission assumptions for three-phase equipment types" pp.99-100 (listed as Table B.1 in
/// the document's list of tables), already in <see cref="HarmonicSourceLibrary"/>:
/// <list type="bullet">
/// <item>Self-commutated converters -> <c>afe-g5</c> (active front end). S139 Annex B pp.96-97: an active-front-end
/// converter replaces the diode bridge with self-commutated IGBT modules, can transfer power both ways and produce
/// reactive power. That is the topology of grid-connected battery, PV, full-converter wind and VSC HVDC plant, which
/// is why the fault studies already treat these units as current sources. G5 Stage 1C / 2A list exactly three
/// three-phase technologies (six-pulse, active front end, twelve-pulse: s.7.4 p.47, s.8.2 p.59); a
/// converter-interfaced generator is none of the rectifier types, so active front end is the only one that fits.</item>
/// <item>Line-commutated converters -> <c>12-pulse-g5</c>. A unit marked as feeding no fault current
/// (<see cref="Generator.ConverterFaultCurrentPu"/> exactly 0, the model's own encoding of a line-commutated HVDC link)
/// is a thyristor converter (S139 Annex B Figure B.2 p.96). That a line-commutated HVDC station is twelve-pulse is
/// [VERIFY]: G5 does not say so (it is the usual HVDC arrangement), and a real station's AC filters remove most of the
/// 11th / 13th, so this profile overstates such a station.</item>
/// </list>
/// The profiles are typical, not measured, so every source keeps the library's
/// <see cref="Diagnostics.ProvenanceTag.CuratedPlaceholder"/> tag.</para>
///
/// <para><b>At what level.</b> I1 per <see cref="ConverterHarmonicLevel"/> (rated by default, the G5 assessment basis).
/// Each component is the profile's Ih/I1 times I1, at phase 0.</para>
///
/// <para><b>How several units add.</b> All sources are at phase 0, so units at one bus, and their effects at every
/// other bus, add linearly. G5 Issue 5 s.6.3.3 p.36: the summation exponents of Table 16 may be used only on a declared
/// operational and locational diversity, "without which linear addition shall be employed" (exponent 1.0 means the
/// sources are in phase). No diversity is declared in a model, so linear addition is the G5 rule.</para>
///
/// <para><b>Orders.</b> Table B.3 runs from order 2 to 50 and the active-front-end profile has even orders, which the
/// default odd 3-49 study would not analyse (N075: they would be dropped). <see cref="StudyOptions"/> is
/// <see cref="HarmonicModelOptions.Study"/> with orders 2 to 50 (<see cref="StudyOrders"/>). The study is balanced, so
/// the triplen orders (3, 6, 9, ...: zero sequence) of the active-front-end profile cannot be carried by the
/// positive-sequence network (R029); with <c>balanced: true</c> they are left out of the sources and each is listed in
/// <see cref="ConverterHarmonicSourceSet.OmittedComponents"/>. A balanced three-phase converter with a delta or
/// unearthed-star converter-transformer winding passes no zero-sequence current anyway, but nothing on disk states the
/// winding of a given unit ([VERIFY]); use the three-phase study for triplens.</para>
///
/// <para><b>Internal impedance (N149).</b> S139 s.3.29 p.17 notes that active-front-end converters "may be classified as
/// resonant plant due to their internal impedance". By default this engine stamps no converter impedance in Y(h) (a
/// converter is an ideal current source, as in the fault studies). With
/// <see cref="HarmonicModelOptions.ActiveFrontEndReactancePu"/> set, every unit of the active-front-end class (the
/// <see cref="SelfCommutatedProfile"/> units, <see cref="FrequencyDependentYBus.IsActiveFrontEndPlant"/>) becomes a
/// Norton equivalent: its source in parallel with that impedance to ground; line-commutated units are unchanged. No
/// document on disk gives the value, so it has no default [VERIFY].</para>
/// </summary>
public static class ConverterHarmonicSources
{
    /// <summary>The library profile for a self-commutated (IGBT, active-front-end) converter: G5 Table B.3.</summary>
    public const string SelfCommutatedProfile = "afe-g5";

    /// <summary>The library profile for a line-commutated (thyristor) converter: G5 Table B.3 twelve-pulse [VERIFY].</summary>
    public const string LineCommutatedProfile = "12-pulse-g5";

    /// <summary>The orders G5 Table B.3 covers, 2 to 50: the order set a per-converter study analyses.</summary>
    public static IReadOnlyList<int> StudyOrders { get; } = Enumerable.Range(2, 49).ToArray();   // 2, 3, ..., 50

    /// <summary>The physical study model (<see cref="HarmonicModelOptions.Study"/>: machines, load damping, balanced
    /// sequences) over <see cref="StudyOrders"/>. Everything but the order set is the default study's.</summary>
    public static HarmonicModelOptions StudyOptions { get; } = HarmonicModelOptions.Study with { Orders = StudyOrders };

    /// <summary>
    /// The library profile name for one generator under this rule, or null when the unit injects nothing here (not
    /// converter-interfaced, or a power-flow-only device). The caller passes in-service units only
    /// (<see cref="GridModel.Generators"/>).
    /// </summary>
    public static string? ProfileFor(Generator g)
    {
        ArgumentNullException.ThrowIfNull(g);
        if (!g.IsConverterInterfaced || g.PowerFlowOnly) return null;
        // ConverterFaultCurrentPu == 0 is the model's encoding of a line-commutated converter (Generator: "0 = none
        // (LCC HVDC)"); every other converter is self-commutated.
        return g.ConverterFaultCurrentPu is double k && k == 0.0 ? LineCommutatedProfile : SelfCommutatedProfile;
    }

    /// <summary>
    /// Build the per-converter sources of <paramref name="model"/> (see the class remarks for the rule). Fails loud
    /// (<see cref="ArgumentException"/>) on a non-finite or non-positive system base, on a converter whose rating
    /// (<see cref="ConverterHarmonicLevel.Rated"/>: <see cref="Generator.MBase"/>) is non-finite or not positive, and on a
    /// non-finite Pg or Qg (<see cref="ConverterHarmonicLevel.Dispatched"/>): a source current built from such a value
    /// would be meaningless. A model with no converter-interfaced unit gives an empty source list (the caller decides
    /// whether that is an error).
    /// </summary>
    /// <param name="model">The network; its in-service generators are classified.</param>
    /// <param name="level">Which fundamental current scales each profile.</param>
    /// <param name="balanced">True for the balanced (positive-sequence) study, <see cref="StudyOptions"/>: triplen orders
    /// are left out and listed. False keeps every Table B.3 component (for a study that carries triplens).</param>
    /// <param name="operatingPoint">The converged solve of <paramref name="model"/> the dispatched level reads each unit's
    /// output from (N150, 1.95.243: a generator's Pg / Qg are its schedule; the solve sets the slack's P and every PV
    /// unit's Q, the D-DOC-054 rule). Null keeps the scheduled values. Ignored at the rated level. A non-converged result
    /// throws <see cref="NonConvergedStateException"/>.</param>
    public static ConverterHarmonicSourceSet FromModel(
        GridModel model, ConverterHarmonicLevel level = ConverterHarmonicLevel.Rated, bool balanced = true,
        PowerFlowResult? operatingPoint = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!double.IsFinite(model.BaseMva) || model.BaseMva <= 0.0)
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"Converter harmonic sources: the model's BaseMva ({model.BaseMva}) must be finite and positive."), nameof(model));
        if (!Enum.IsDefined(level))
            throw new ArgumentOutOfRangeException(nameof(level), level, "unknown converter harmonic level");

        var sources = new List<HarmonicSource>();
        var omitted = new List<string>();
        var notModelled = new List<string>();
        for (int k = 0; k < model.Generators.Count; k++)
        {
            Generator g = model.Generators[k];
            if (!g.IsConverterInterfaced) continue;   // a synchronous machine: a harmonic sink, not a source
            string unit = Describe(g, k);
            string? profile = ProfileFor(g);
            if (profile is null)
            {
                notModelled.Add($"{unit}: a power-flow-only device, not physical plant");
                continue;
            }

            double i1;
            if (level == ConverterHarmonicLevel.Rated)
            {
                if (!double.IsFinite(g.MBase) || g.MBase <= 0.0)
                    throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                        $"Converter harmonic sources: {unit} has rating MBase = {g.MBase} MVA; the rated level needs a finite, positive rating."), nameof(model));
                i1 = g.MBase / model.BaseMva;
            }
            else
            {
                (double p, double q) = operatingPoint is null ? (g.Pg, g.Qg) : SolvedGeneration.UnitOutput(model, operatingPoint, k);
                if (!double.IsFinite(p) || !double.IsFinite(q))
                    throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                        $"Converter harmonic sources: {unit} has {(operatingPoint is null ? "Pg" : "solved P")} = {p} MW, {(operatingPoint is null ? "Qg" : "solved Q")} = {q} MVAr; the dispatched level needs finite values."), nameof(model));
                i1 = Math.Sqrt(p * p + q * q) / model.BaseMva;
                if (i1 == 0.0)
                {
                    notModelled.Add($"{unit}: zero output at this operating point (dispatched level)");
                    continue;
                }
            }

            // The profile, minus the components the balanced study cannot carry (each one listed, never dropped
            // silently).
            HarmonicSource full = HarmonicSourceLibrary.Make(profile, g.Bus, i1, g.Name);
            IReadOnlyList<HarmonicComponent> kept = full.Spectrum;
            if (balanced)
            {
                var keep = new List<HarmonicComponent>(full.Spectrum.Count);
                foreach (HarmonicComponent c in full.Spectrum)
                {
                    if (HarmonicSequence.IsTriplen(c.Order) && c.MagnitudePercent != 0.0)
                        omitted.Add(string.Create(CultureInfo.InvariantCulture,
                            $"{unit} ({profile}) order {c.Order} at {c.MagnitudePercent} %: a triplen (zero sequence), not carried by the balanced study"));
                    else
                        keep.Add(c);
                }
                kept = keep;
            }
            sources.Add(full with { Spectrum = kept });
        }
        return new ConverterHarmonicSourceSet(sources, omitted, notModelled);
    }

    /// <summary>"unit k 'name' (type) at bus B": how a generator is named in the lists (k is its index in
    /// <see cref="GridModel.Generators"/>, so two unnamed units at one bus stay distinguishable).</summary>
    private static string Describe(Generator g, int k)
    {
        string name = string.IsNullOrWhiteSpace(g.Name) ? "" : $" '{g.Name}'";
        string type = string.IsNullOrWhiteSpace(g.Type) ? "" : $" ({g.Type})";
        return string.Create(CultureInfo.InvariantCulture, $"unit {k}{name}{type} at bus {g.Bus}");
    }
}
