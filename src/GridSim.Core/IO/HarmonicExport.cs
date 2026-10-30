using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Diagnostics;
using GridSim.Core.Harmonics;
using GridSim.Core.Model;

namespace GridSim.Core.IO;

/// <summary>
/// Serialises a harmonic study to a single JSON document: run metadata, the sources (with their provenance),
/// the per-order per-bus harmonic voltages, the distortion indices, and (optionally) the driving-point
/// resonance scan and the compliance verdicts. Schema <c>gridsim-harmonic-export/1</c>, mirrored from
/// <see cref="EstimateExport"/>/<see cref="SolveExport"/> (additive blocks; named float literals allowed).
/// A headline <c>provenance</c> field states honestly whether the study rests on measured or assumed spectra.
/// </summary>
public static class HarmonicExport
{
    public const string Schema = "gridsim-harmonic-export/1";

    // Phase 0.1 (source-generated JSON, so the core can run under NativeAOT): exactly the options this file has always
    // used, now bound to the source-generated HarmonicExportJsonContext (below) so no runtime reflection is needed.
    // Enums are still by name, through the source-generated per-enum JsonStringEnumConverter<TEnum> (UseStringEnumConverter
    // on the context) in place of the reflection-based JsonStringEnumConverter: both write the member name and both
    // still accept an integer on read.
    // Internal, not private, so JsonSourceGenEquivalenceTests reads and writes through this very instance.
    internal static readonly HarmonicExportJsonContext Json = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    });

    /// <param name="fundamentalVm">N244 (October 2026): the solved fundamental voltage magnitude |V_1| (pu) of every
    /// bus, aligned with <see cref="HarmonicPenetrationResult.BusIds"/>: the denominator the THD in
    /// <paramref name="indices"/> was formed with (<see cref="HarmonicIndices.SolvedFundamentalVm"/>). Written as the
    /// additive <c>indices.fundamentalVm</c> list so an independent checker (tools/crossval/crossval_harmonics.py) can
    /// form THD over the same |V_1|; it divided by a nominal 1.0 pu and reported false THD mismatches. Null (an older
    /// caller) writes nothing, so the document is byte-identical to before.</param>
    public static void WriteJson(
        string path, GridModel model, HarmonicSourceSet sources, HarmonicPenetrationResult penetration,
        HarmonicIndicesResult indices, SolveExportMeta meta,
        ImpedanceScanResult? scan = null, IReadOnlyList<BusComplianceResult>? compliance = null,
        HarmonicModelOptions? modelOptions = null, FrequencySweepResult? sweep = null,
        IReadOnlyList<double>? fundamentalVm = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, ToJson(model, sources, penetration, indices, meta, scan, compliance, modelOptions, sweep, fundamentalVm));
    }

    /// <summary>
    /// The export's headline provenance for a set of harmonic sources. N359 (CF6, technical ch. 8 "trust only moves
    /// down"): the headline may not be stronger than the weakest source, so it is
    /// <list type="bullet">
    /// <item><see cref="ProvenanceTag.Recorded"/> only when EVERY source is Recorded;</item>
    /// <item><see cref="ProvenanceTag.DerivedFromRecorded"/> when every source is Recorded or DerivedFromRecorded and at
    /// least one is DerivedFromRecorded (before N359 this case read Recorded: a raise of the derived sources' trust);</item>
    /// <item><see cref="ProvenanceTag.CuratedPlaceholder"/> otherwise (any assumed source, or no source at all), exactly as
    /// before.</item>
    /// </list>
    /// The result is then checked by <see cref="ProvenanceTrust.RequireNotRaised"/> against every source's tag, so a
    /// later change that raised the headline would fail loud (<see cref="InvalidDataException"/>), and a source carrying
    /// an output tag (Counterfactual, UnvalidatedAtScale) is refused as an input. Every set with no DerivedFromRecorded
    /// source among otherwise measured ones gets the same headline as before.
    /// </summary>
    public static ProvenanceTag HeadlineProvenance(IReadOnlyList<HarmonicSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0) return ProvenanceTag.CuratedPlaceholder;   // no source: nothing measured (as before)
        bool allMeasured = sources.All(s => s.Provenance is ProvenanceTag.Recorded or ProvenanceTag.DerivedFromRecorded);
        ProvenanceTag headline = !allMeasured
            ? ProvenanceTag.CuratedPlaceholder
            : sources.All(s => s.Provenance == ProvenanceTag.Recorded) ? ProvenanceTag.Recorded : ProvenanceTag.DerivedFromRecorded;
        ProvenanceTrust.RequireNotRaised(sources.Select(s => s.Provenance), headline, "the harmonic export headline");
        return headline;
    }

    /// <param name="fundamentalVm">N244: see <see cref="WriteJson"/>.</param>
    public static string ToJson(
        GridModel model, HarmonicSourceSet sources, HarmonicPenetrationResult penetration,
        HarmonicIndicesResult indices, SolveExportMeta meta,
        ImpedanceScanResult? scan = null, IReadOnlyList<BusComplianceResult>? compliance = null,
        HarmonicModelOptions? modelOptions = null, FrequencySweepResult? sweep = null,
        IReadOnlyList<double>? fundamentalVm = null)
    {
        // N244: the |V_1| list must line up with the study's buses, or the checker would divide by the wrong bus.
        if (fundamentalVm is not null && fundamentalVm.Count != penetration.BusIds.Count)
            throw new ArgumentException(
                $"fundamentalVm has {fundamentalVm.Count} entries but the harmonic study has {penetration.BusIds.Count} buses.",
                nameof(fundamentalVm));

        // Headline provenance: measured only if EVERY source is measured; otherwise the study is assumed.
        // N359 (CF6): the headline is now checked downgrade-only (see HeadlineProvenance).
        ProvenanceTag headline = HeadlineProvenance(sources.Sources);

        // Phase 0.1: named types (below) in place of the anonymous objects: same members, same order, same types, so
        // the bytes are unchanged; the source-generated context can describe a named type (NativeAOT). This list was a
        // List<object> of anonymous rows (written by their runtime type); it is now a list of that row type.
        var voltage = new List<VoltageRow>();
        for (int oi = 0; oi < penetration.Orders.Count; oi++)
            for (int bi = 0; bi < penetration.BusIds.Count; bi++)
            {
                System.Numerics.Complex v = penetration.Voltage[oi][bi];
                if (v == System.Numerics.Complex.Zero) continue;   // sparse: only nonzero harmonic voltages
                voltage.Add(new VoltageRow
                {
                    order = penetration.Orders[oi],
                    bus = penetration.BusIds[bi],
                    vmPu = v.Magnitude,
                    vaDeg = DetMath.Phase(v) * 180.0 / Math.PI,
                });
            }

        var doc = new HarmonicDoc
        {
            schema = Schema,
            meta = meta,
            provenance = headline,          // honest headline: measured (Recorded / DerivedFromRecorded, N359) vs assumed (CuratedPlaceholder)
            model = new ModelRow
            {
                name = model.Name,
                baseMva = model.BaseMva,
                busCount = model.BusCount,
                buses = model.Buses.Select(b => new BusRow { id = b.Id, baseKv = b.BaseKv, name = b.Name }),
            },
            orders = penetration.Orders,
            // The harmonic model the study ran (additive, optional). Records every input beyond the case
            // network that Y(h) used - the machine source reactances and the per-bus loads - so an independent
            // checker (tools/crossval/crossval_harmonics.py) can rebuild the same Y(h) without GridSim code.
            harmonicModel = modelOptions is null ? null : new HarmonicModelRow
            {
                includeGeneratorSources = modelOptions.IncludeGeneratorSources,
                includeLoadShunts = modelOptions.IncludeLoadShunts,
                skinCoefficient = modelOptions.SkinCoefficient,
                cableSkinCoefficient = modelOptions.CableSkinCoefficient,
                longLineCorrection = modelOptions.LongLineCorrection,
                balancedHarmonicSequences = modelOptions.BalancedHarmonicSequences,
                generatorSources = !modelOptions.IncludeGeneratorSources ? null : model.Generators
                    .Where(g => !g.PowerFlowOnly && !g.IsConverterInterfaced)
                    .Select(g => new GeneratorSourceRow
                    {
                        bus = g.Bus,
                        name = g.Name,
                        xSystemPu = FrequencyDependentYBus.GeneratorSourceReactanceSystemPu(
                            g, model.BaseMva, modelOptions.GeneratorFallbackXdPu),
                    }),
                loads = !modelOptions.IncludeLoadShunts ? null : model.Buses
                    .Where(b => b.Pd != 0 || b.Qd != 0)
                    .Select(b => new LoadRow { bus = b.Id, pdMw = b.Pd, qdMvar = b.Qd }),
                // N348 (CF4, October 2026): the active-front-end internal impedance (N149), when the study stamped it, with
                // every stamped unit's impedance on the system base, so an independent checker rebuilds the same Y(h)
                // (the shunt R + j*h*X at each unit's bus). Omitted when the option is not set: the document is then
                // byte-identical to before.
                activeFrontEnd = ActiveFrontEndBlock(model, modelOptions),
            },
            sources = sources.Sources.Select(s => new SourceRow
            {
                bus = s.Bus,
                sourceType = s.SourceType,
                provenance = s.Provenance,
                fundamentalCurrentPu = s.FundamentalCurrentPu,
                spectrum = s.Spectrum.Select(c => new SpectrumRow { order = c.Order, magnitudePercent = c.MagnitudePercent, phaseDeg = c.PhaseDeg }),
            }),
            voltage = voltage,
            indices = new IndicesRow
            {
                worstBusId = indices.WorstBusId,
                worstThdPct = indices.WorstThdPct,
                thd = indices.BusIds.Select((id, i) => new ThdRow { bus = id, thdPct = indices.ThdVoltagePct[i] }),
                // N244 (additive; omitted when the caller gives none): the THD denominator |V_1| per bus.
                fundamentalVm = fundamentalVm is null ? null
                    : penetration.BusIds.Select((id, i) => new FundamentalVmRow { bus = id, vmPu = fundamentalVm[i] }),
            },
            // Additive optional blocks (readers of /1 ignore unknown fields).
            resonance = scan is null ? null : new ResonanceRow
            {
                bus = scan.BusId,
                order = scan.ResonanceOrder,
                zMag = scan.PeakMagnitude,
                sharpness = HarmonicIndices.DampingSharpness(scan),
            },
            // The fine driving-point sweep's refined peak (R063; additive, optional).
            resonanceSweep = sweep is null ? null : new ResonanceSweepRow
            {
                bus = sweep.BusId,
                peakOrder = sweep.PeakOrder,
                peakHz = sweep.PeakHz,
                zMag = sweep.PeakMagnitude,
                sequence = sweep.PeakSequence,
                minOrder = sweep.Orders[0],
                maxOrder = sweep.Orders[sweep.Orders.Count - 1],
                gridPoints = sweep.Orders.Count,
            },
            compliance = compliance?.Select(c => new ComplianceRow
            {
                bus = c.BusId,
                baseKv = c.BaseKv,
                thdPct = c.ThdPct,
                thdLimitPct = c.ThdLimitPct,
                worstIhdPct = c.WorstIhdPct,
                worstIhdOrder = c.WorstIhdOrder,
                ihdLimitPct = c.IhdLimitPct,
                compliant = c.Compliant,
                voltageLevelKnown = c.VoltageLevelKnown,
            }),
        };
        return JsonSerializer.Serialize(doc, Json.HarmonicDoc);
    }

    /// <summary>N348 (CF4): the <c>harmonicModel.activeFrontEnd</c> block, or null when the study did not stamp the
    /// active-front-end impedance. Each unit is exactly what <see cref="FrequencyDependentYBus"/> stamped: the units of
    /// <see cref="FrequencyDependentYBus.IsActiveFrontEndPlant"/>, their impedance from
    /// <see cref="FrequencyDependentYBus.ActiveFrontEndImpedanceSystemPu"/>, in model order. The option values are
    /// checked by the same rule the stamp uses, so an export never records an impedance the study would refuse.</summary>
    /// <remarks>Phase 0.1: typed (it returned object? and built List&lt;object&gt; of anonymous rows); the same
    /// members in the same order, so the same bytes.</remarks>
    private static ActiveFrontEndRow? ActiveFrontEndBlock(GridModel model, HarmonicModelOptions modelOptions)
    {
        if (modelOptions.ActiveFrontEndReactancePu is not double xAfe) return null;
        double rAfe = modelOptions.ActiveFrontEndResistancePu;
        FrequencyDependentYBus.ValidateActiveFrontEndImpedance(rAfe, xAfe);
        var units = new List<AfeUnitRow>();
        foreach (Generator g in model.Generators)
        {
            if (!FrequencyDependentYBus.IsActiveFrontEndPlant(g)) continue;
            (double r, double x) = FrequencyDependentYBus.ActiveFrontEndImpedanceSystemPu(g, model.BaseMva, rAfe, xAfe);
            units.Add(new AfeUnitRow { bus = g.Bus, name = g.Name, rSystemPu = r, xSystemPu = x });
        }
        return new ActiveFrontEndRow { reactancePu = xAfe, resistancePu = rAfe, units = units };
    }

    // ---- Phase 0.1: the document as named types ----------------------------------------------------------------------
    // These replace the anonymous objects ToJson used to build, so the source-generated context can describe them
    // (NativeAOT). The property names ARE the JSON member names (these options apply no naming policy), so they are
    // lower camelCase on purpose, spelt exactly as the anonymous members were, declared in the same order and with the
    // same CLR types (enums by name; null members omitted, WhenWritingNull). JsonSourceGenEquivalenceTests holds the
    // bytes equal to the old reflection-built document.

    /// <summary>The whole harmonic study document.</summary>
    internal sealed class HarmonicDoc
    {
        public required string schema { get; init; }
        public required SolveExportMeta meta { get; init; }
        public required ProvenanceTag provenance { get; init; }
        public required ModelRow model { get; init; }
        public required IReadOnlyList<int> orders { get; init; }
        public required HarmonicModelRow? harmonicModel { get; init; }
        public required IEnumerable<SourceRow> sources { get; init; }
        public required List<VoltageRow> voltage { get; init; }
        public required IndicesRow indices { get; init; }
        public required ResonanceRow? resonance { get; init; }
        public required ResonanceSweepRow? resonanceSweep { get; init; }
        public required IEnumerable<ComplianceRow>? compliance { get; init; }
    }

    /// <summary>One nonzero harmonic voltage: { order, bus, vmPu, vaDeg }.</summary>
    internal sealed class VoltageRow
    {
        public required int order { get; init; }
        public required int bus { get; init; }
        public required double vmPu { get; init; }
        public required double vaDeg { get; init; }
    }

    /// <summary>{ name, baseMva, busCount, buses }.</summary>
    internal sealed class ModelRow
    {
        public required string name { get; init; }
        public required double baseMva { get; init; }
        public required int busCount { get; init; }
        public required IEnumerable<BusRow> buses { get; init; }
    }

    /// <summary>{ id, baseKv, name }.</summary>
    internal sealed class BusRow
    {
        public required int id { get; init; }
        public required double baseKv { get; init; }
        public required string? name { get; init; }
    }

    /// <summary>The harmonic model the study ran.</summary>
    internal sealed class HarmonicModelRow
    {
        public required bool includeGeneratorSources { get; init; }
        public required bool includeLoadShunts { get; init; }
        public required double skinCoefficient { get; init; }
        public required double cableSkinCoefficient { get; init; }
        public required bool longLineCorrection { get; init; }
        public required bool balancedHarmonicSequences { get; init; }
        public required IEnumerable<GeneratorSourceRow>? generatorSources { get; init; }
        public required IEnumerable<LoadRow>? loads { get; init; }
        public required ActiveFrontEndRow? activeFrontEnd { get; init; }
    }

    /// <summary>One machine source reactance: { bus, name, xSystemPu }.</summary>
    internal sealed class GeneratorSourceRow
    {
        public required int bus { get; init; }
        public required string? name { get; init; }
        public required double xSystemPu { get; init; }
    }

    /// <summary>One load shunt: { bus, pdMw, qdMvar }.</summary>
    internal sealed class LoadRow
    {
        public required int bus { get; init; }
        public required double pdMw { get; init; }
        public required double qdMvar { get; init; }
    }

    /// <summary>The active-front-end block: { reactancePu, resistancePu, units }.</summary>
    internal sealed class ActiveFrontEndRow
    {
        public required double reactancePu { get; init; }
        public required double resistancePu { get; init; }
        public required List<AfeUnitRow> units { get; init; }
    }

    /// <summary>One stamped active-front-end unit: { bus, name, rSystemPu, xSystemPu }.</summary>
    internal sealed class AfeUnitRow
    {
        public required int bus { get; init; }
        public required string? name { get; init; }
        public required double rSystemPu { get; init; }
        public required double xSystemPu { get; init; }
    }

    /// <summary>One harmonic source.</summary>
    internal sealed class SourceRow
    {
        public required int bus { get; init; }
        public required string sourceType { get; init; }
        public required ProvenanceTag provenance { get; init; }
        public required double fundamentalCurrentPu { get; init; }
        public required IEnumerable<SpectrumRow> spectrum { get; init; }
    }

    /// <summary>One spectrum component: { order, magnitudePercent, phaseDeg }.</summary>
    internal sealed class SpectrumRow
    {
        public required int order { get; init; }
        public required double magnitudePercent { get; init; }
        public required double phaseDeg { get; init; }
    }

    /// <summary>The THD indices.</summary>
    internal sealed class IndicesRow
    {
        public required int worstBusId { get; init; }
        public required double worstThdPct { get; init; }
        public required IEnumerable<ThdRow> thd { get; init; }
        public required IEnumerable<FundamentalVmRow>? fundamentalVm { get; init; }
    }

    /// <summary>{ bus, thdPct }.</summary>
    internal sealed class ThdRow
    {
        public required int bus { get; init; }
        public required double thdPct { get; init; }
    }

    /// <summary>{ bus, vmPu }.</summary>
    internal sealed class FundamentalVmRow
    {
        public required int bus { get; init; }
        public required double vmPu { get; init; }
    }

    /// <summary>The impedance-scan resonance.</summary>
    internal sealed class ResonanceRow
    {
        public required int bus { get; init; }
        public required int order { get; init; }
        public required double zMag { get; init; }
        public required double sharpness { get; init; }
    }

    /// <summary>The fine driving-point sweep's refined peak.</summary>
    internal sealed class ResonanceSweepRow
    {
        public required int bus { get; init; }
        public required double peakOrder { get; init; }
        public required double peakHz { get; init; }
        public required double zMag { get; init; }
        public required HarmonicSequenceKind sequence { get; init; }
        public required double minOrder { get; init; }
        public required double maxOrder { get; init; }
        public required int gridPoints { get; init; }
    }

    /// <summary>One bus compliance result.</summary>
    internal sealed class ComplianceRow
    {
        public required int bus { get; init; }
        public required double baseKv { get; init; }
        public required double thdPct { get; init; }
        public required double thdLimitPct { get; init; }
        public required double worstIhdPct { get; init; }
        public required int worstIhdOrder { get; init; }
        public required double ihdLimitPct { get; init; }
        public required bool compliant { get; init; }
        public required bool voltageLevelKnown { get; init; }
    }
}

/// <summary>
/// Phase 0.1: the source-generated JSON metadata for the harmonic study export document and every type it reaches, so it is read and
/// written without runtime reflection (NativeAOT). Metadata mode only: the bytes come from the same converters the
/// reflection path used, never from a separately generated fast path. Enums by name, as before.
/// The runtime options (naming, key case, null and number handling, indentation) are passed in by the call site.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, UseStringEnumConverter = true)]
[JsonSerializable(typeof(HarmonicExport.HarmonicDoc))]
internal sealed partial class HarmonicExportJsonContext : JsonSerializerContext;
