using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Diagnostics;
using GridSim.Core.Harmonics;
using GridSim.Core.Model;
using GridSim.Core.IO;

using DetMath = GridSim.Core.Numerics.DetMath;

namespace GridSim.Tests.JsonSourceGen.Legacy;

// Phase 0.1 equivalence oracle: a verbatim, test-only copy of GridSim.Core.IO.HarmonicExport as it stood before source-generated JSON
// (GridSim 2.0.48), still serialising its anonymous objects through runtime reflection. Only the class name (LegacyHarmonicExport)
// and the namespace changed (a using alias stands in for the names Core resolves through its own namespace).
// JsonSourceGenEquivalenceTests requires the production exporter to write exactly the bytes this copy writes. Never
// edit it to follow the production code: it is the old behaviour, frozen.

internal static class LegacyHarmonicExport
{
    public const string Schema = "gridsim-harmonic-export/1";

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

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

        var voltage = new List<object>();
        for (int oi = 0; oi < penetration.Orders.Count; oi++)
            for (int bi = 0; bi < penetration.BusIds.Count; bi++)
            {
                System.Numerics.Complex v = penetration.Voltage[oi][bi];
                if (v == System.Numerics.Complex.Zero) continue;   // sparse: only nonzero harmonic voltages
                voltage.Add(new
                {
                    order = penetration.Orders[oi],
                    bus = penetration.BusIds[bi],
                    vmPu = v.Magnitude,
                    vaDeg = DetMath.Phase(v) * 180.0 / Math.PI,
                });
            }

        var doc = new
        {
            schema = Schema,
            meta,
            provenance = headline,          // honest headline: measured (Recorded / DerivedFromRecorded, N359) vs assumed (CuratedPlaceholder)
            model = new
            {
                name = model.Name,
                baseMva = model.BaseMva,
                busCount = model.BusCount,
                buses = model.Buses.Select(b => new { id = b.Id, baseKv = b.BaseKv, name = b.Name }),
            },
            orders = penetration.Orders,
            // The harmonic model the study ran (additive, optional). Records every input beyond the case
            // network that Y(h) used - the machine source reactances and the per-bus loads - so an independent
            // checker (tools/crossval/crossval_harmonics.py) can rebuild the same Y(h) without GridSim code.
            harmonicModel = modelOptions is null ? null : new
            {
                includeGeneratorSources = modelOptions.IncludeGeneratorSources,
                includeLoadShunts = modelOptions.IncludeLoadShunts,
                skinCoefficient = modelOptions.SkinCoefficient,
                cableSkinCoefficient = modelOptions.CableSkinCoefficient,
                longLineCorrection = modelOptions.LongLineCorrection,
                balancedHarmonicSequences = modelOptions.BalancedHarmonicSequences,
                generatorSources = !modelOptions.IncludeGeneratorSources ? null : model.Generators
                    .Where(g => !g.PowerFlowOnly && !g.IsConverterInterfaced)
                    .Select(g => new
                    {
                        bus = g.Bus,
                        name = g.Name,
                        xSystemPu = FrequencyDependentYBus.GeneratorSourceReactanceSystemPu(
                            g, model.BaseMva, modelOptions.GeneratorFallbackXdPu),
                    }),
                loads = !modelOptions.IncludeLoadShunts ? null : model.Buses
                    .Where(b => b.Pd != 0 || b.Qd != 0)
                    .Select(b => new { bus = b.Id, pdMw = b.Pd, qdMvar = b.Qd }),
                // N348 (CF4, October 2026): the active-front-end internal impedance (N149), when the study stamped it, with
                // every stamped unit's impedance on the system base, so an independent checker rebuilds the same Y(h)
                // (the shunt R + j*h*X at each unit's bus). Omitted when the option is not set: the document is then
                // byte-identical to before.
                activeFrontEnd = ActiveFrontEndBlock(model, modelOptions),
            },
            sources = sources.Sources.Select(s => new
            {
                bus = s.Bus,
                sourceType = s.SourceType,
                provenance = s.Provenance,
                fundamentalCurrentPu = s.FundamentalCurrentPu,
                spectrum = s.Spectrum.Select(c => new { order = c.Order, magnitudePercent = c.MagnitudePercent, phaseDeg = c.PhaseDeg }),
            }),
            voltage,
            indices = new
            {
                worstBusId = indices.WorstBusId,
                worstThdPct = indices.WorstThdPct,
                thd = indices.BusIds.Select((id, i) => new { bus = id, thdPct = indices.ThdVoltagePct[i] }),
                // N244 (additive; omitted when the caller gives none): the THD denominator |V_1| per bus.
                fundamentalVm = fundamentalVm is null ? null
                    : penetration.BusIds.Select((id, i) => new { bus = id, vmPu = fundamentalVm[i] }),
            },
            // Additive optional blocks (readers of /1 ignore unknown fields).
            resonance = scan is null ? null : new
            {
                bus = scan.BusId,
                order = scan.ResonanceOrder,
                zMag = scan.PeakMagnitude,
                sharpness = HarmonicIndices.DampingSharpness(scan),
            },
            // The fine driving-point sweep's refined peak (R063; additive, optional).
            resonanceSweep = sweep is null ? null : new
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
            compliance = compliance?.Select(c => new
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
        return JsonSerializer.Serialize(doc, Opts);
    }

    /// <summary>N348 (CF4): the <c>harmonicModel.activeFrontEnd</c> block, or null when the study did not stamp the
    /// active-front-end impedance. Each unit is exactly what <see cref="FrequencyDependentYBus"/> stamped: the units of
    /// <see cref="FrequencyDependentYBus.IsActiveFrontEndPlant"/>, their impedance from
    /// <see cref="FrequencyDependentYBus.ActiveFrontEndImpedanceSystemPu"/>, in model order. The option values are
    /// checked by the same rule the stamp uses, so an export never records an impedance the study would refuse.</summary>
    private static object? ActiveFrontEndBlock(GridModel model, HarmonicModelOptions modelOptions)
    {
        if (modelOptions.ActiveFrontEndReactancePu is not double xAfe) return null;
        double rAfe = modelOptions.ActiveFrontEndResistancePu;
        FrequencyDependentYBus.ValidateActiveFrontEndImpedance(rAfe, xAfe);
        var units = new List<object>();
        foreach (Generator g in model.Generators)
        {
            if (!FrequencyDependentYBus.IsActiveFrontEndPlant(g)) continue;
            (double r, double x) = FrequencyDependentYBus.ActiveFrontEndImpedanceSystemPu(g, model.BaseMva, rAfe, xAfe);
            units.Add(new { bus = g.Bus, name = g.Name, rSystemPu = r, xSystemPu = x });
        }
        return new { reactancePu = xAfe, resistancePu = rAfe, units };
    }
}
