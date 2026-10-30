using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Impedance;
using GridSim.Core.Model;

namespace GridSim.Core.IO;

/// <summary>
/// Serialises an impedance-based stability scan to a single JSON document (schema
/// <c>gridsim-impedance-stability/1</c>): run metadata, the converter model + operating point, the per-
/// frequency grid/converter impedance and return ratio, the Nyquist verdict, the SCR sweep and the critical
/// SCR boundary, and the Workstream-C modal cross-check. Additive JSON with a headline <c>provenance</c>,
/// mirrored from <see cref="HarmonicExport"/> (named float literals allowed; unknown fields ignored by
/// readers). The sidecar (<c>crossval_impedance.py</c>) re-derives the grid impedance and the encirclement
/// count independently and diffs.
/// </summary>
public static class ImpedanceStabilityExport
{
    public const string Schema = "gridsim-impedance-stability/1";

    // Phase 0.1 (source-generated JSON, so the core can run under NativeAOT): exactly the options this file has always
    // used, now bound to the source-generated ImpedanceStabilityExportJsonContext (below) so no runtime reflection is needed.
    // Enums are still by name, through the source-generated per-enum JsonStringEnumConverter<TEnum> (UseStringEnumConverter
    // on the context) in place of the reflection-based JsonStringEnumConverter: both write the member name and both
    // still accept an integer on read.
    // Internal, not private, so JsonSourceGenEquivalenceTests reads and writes through this very instance.
    internal static readonly ImpedanceStabilityExportJsonContext Json = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    });

    public static void WriteJson(string path, GridModel model, ImpedanceStabilityResult result, SolveExportMeta meta)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, ToJson(model, result, meta));
    }

    public static string ToJson(GridModel model, ImpedanceStabilityResult r, SolveExportMeta meta)
    {
        // Phase 0.1: named types (below) in place of the anonymous objects: same members, same order, same types, so
        // the bytes are unchanged; the source-generated context can describe a named type (NativeAOT).
        var doc = new ImpedanceDoc
        {
            schema = Schema,
            meta = meta,
            provenance = r.Provenance,   // Recorded (measured inner-loop params) vs CuratedPlaceholder [VERIFY]
            model = new ModelRow { name = model.Name, baseMva = model.BaseMva, busCount = model.BusCount },
            inverterBus = r.InverterBus,
            mode = r.Mode,
            modelDetail = r.Detail,
            criterion = r.Criterion,
            operatingPoint = new OperatingPointRow
            {
                vd0Pu = r.OperatingPoint.Vd0Pu,
                vq0Pu = r.OperatingPoint.Vq0Pu,
                id0Pu = r.OperatingPoint.Id0Pu,
                iq0Pu = r.OperatingPoint.Iq0Pu,
                pPu = r.OperatingPoint.PPu,
                qPu = r.OperatingPoint.QPu,
                vd0Clamped = r.OperatingPoint.Vd0WasClamped,
            },
            converterParams = r.Params is null ? null : new ConverterParamsRow
            {
                pllKp = r.Params.PllKp,
                pllKi = r.Params.PllKi,
                currentLoopWciRadS = r.Params.CurrentLoopWci,
                idealPll = r.Params.IdealPll,
                scr = r.Params.Scr,
                xOverR = r.Params.XOverR,
                pRatedPu = r.Params.PRatedPu,
                f0Hz = r.Params.F0Hz,
                canonical = r.Params.Canonical,
            },
            frequenciesHz = r.FrequenciesHz,
            gridImpedance = r.Frequency.Select(s => new ComplexSample { fHz = s.FHz, re = s.ZGrid.Real, im = s.ZGrid.Imaginary }),
            // D-DOC-175 (October 2026, docs-session finding): the converter's small-signal ADMITTANCE Y_conv (per unit on
            // the system base; the return ratio is L = Z_grid * Y_conv) under its own name. It was written only under the
            // key converterImpedance, which names the wrong quantity (an impedance is 1 / Y). converterAdmittance is the
            // key to read; converterImpedance still carries the same numbers for compatibility with readers of earlier
            // exports, is DEPRECATED, and goes at the next schema version (gridsim-impedance-stability/2). Both are the
            // same values, bit for bit.
            converterAdmittance = r.Frequency.Select(s => new ComplexSample { fHz = s.FHz, re = s.YConv.Real, im = s.YConv.Imaginary }),
            converterImpedance = r.Frequency.Select(s => new ComplexSample { fHz = s.FHz, re = s.YConv.Real, im = s.YConv.Imaginary }),
            minorLoop = r.Frequency.Select(s => new MinorLoopRow
            {
                fHz = s.FHz,
                lRe = s.L.Real, lIm = s.L.Imaginary,
                eig1Re = s.Eig1.Real, eig1Im = s.Eig1.Imaginary,
                eig2Re = s.Eig2.Real, eig2Im = s.Eig2.Imaginary,
            }),
            nyquist = new NyquistRow
            {
                encirclements = r.Nyquist.Encirclements,
                unstableModeCount = r.Nyquist.UnstableModeCount,
                stable = r.Nyquist.Stable,
                gainMarginDb = r.Nyquist.GainMarginDb,
                phaseMarginDeg = r.Nyquist.PhaseMarginDeg,
                criticalFreqHz = r.Nyquist.CriticalFreqHz,
                minDistToMinusOne = r.Nyquist.MinDistToMinusOne,
            },
            scrSweep = r.ScrSweep.Select(s => new ScrSweepRow
            {
                scr = s.Scr, encirclements = s.Encirclements, stable = s.Stable, minDistToMinusOne = s.MinDistToMinusOne,
            }),
            boundary = new BoundaryRow { criticalScr = r.CriticalScr, found = r.BoundaryFound },
            crossval = new CrossvalRow { rhpEigenvalueCountModal = r.RhpEigenvalueCountFromModal, agreement = r.AgreesWithModal },
        };
        return JsonSerializer.Serialize(doc, Json.ImpedanceDoc);
    }

    // ---- Phase 0.1: the document as named types ----------------------------------------------------------------------
    // These replace the anonymous objects ToJson used to build, so the source-generated context can describe them
    // (NativeAOT). The property names ARE the JSON member names (these options apply no naming policy), so they are
    // lower camelCase on purpose, spelt exactly as the anonymous members were, declared in the same order and with the
    // same CLR types (the type decides how a value is written: enums by name, numbers by their CLR type).
    // JsonSourceGenEquivalenceTests holds the bytes equal to the old reflection-built document.

    /// <summary>The whole impedance-stability document.</summary>
    internal sealed class ImpedanceDoc
    {
        public required string schema { get; init; }
        public required SolveExportMeta meta { get; init; }
        public required ProvenanceTag provenance { get; init; }
        public required ModelRow model { get; init; }
        public required int inverterBus { get; init; }
        public required GridSim.Core.Assets.AssetKind mode { get; init; }
        public required ImpedanceModelDetail modelDetail { get; init; }
        public required StabilityCriterion criterion { get; init; }
        public required OperatingPointRow operatingPoint { get; init; }
        public required ConverterParamsRow? converterParams { get; init; }
        public required IReadOnlyList<double> frequenciesHz { get; init; }
        public required IEnumerable<ComplexSample> gridImpedance { get; init; }
        public required IEnumerable<ComplexSample> converterAdmittance { get; init; }
        public required IEnumerable<ComplexSample> converterImpedance { get; init; }
        public required IEnumerable<MinorLoopRow> minorLoop { get; init; }
        public required NyquistRow nyquist { get; init; }
        public required IEnumerable<ScrSweepRow> scrSweep { get; init; }
        public required BoundaryRow boundary { get; init; }
        public required CrossvalRow crossval { get; init; }
    }

    /// <summary>{ name, baseMva, busCount }.</summary>
    internal sealed class ModelRow
    {
        public required string name { get; init; }
        public required double baseMva { get; init; }
        public required int busCount { get; init; }
    }

    /// <summary>The converter operating point.</summary>
    internal sealed class OperatingPointRow
    {
        public required double vd0Pu { get; init; }
        public required double vq0Pu { get; init; }
        public required double id0Pu { get; init; }
        public required double iq0Pu { get; init; }
        public required double pPu { get; init; }
        public required double qPu { get; init; }
        public required bool vd0Clamped { get; init; }
    }

    /// <summary>The re-derivation parameters (null, so omitted, for a non-GFL or real-network run).</summary>
    internal sealed class ConverterParamsRow
    {
        public required double pllKp { get; init; }
        public required double pllKi { get; init; }
        public required double currentLoopWciRadS { get; init; }
        public required bool idealPll { get; init; }
        public required double scr { get; init; }
        public required double xOverR { get; init; }
        public required double pRatedPu { get; init; }
        public required double f0Hz { get; init; }
        public required bool canonical { get; init; }
    }

    /// <summary>One complex value at one frequency: { fHz, re, im }.</summary>
    internal sealed class ComplexSample
    {
        public required double fHz { get; init; }
        public required double re { get; init; }
        public required double im { get; init; }
    }

    /// <summary>One minor-loop sample: the return ratio and its two eigenvalues.</summary>
    internal sealed class MinorLoopRow
    {
        public required double fHz { get; init; }
        public required double lRe { get; init; }
        public required double lIm { get; init; }
        public required double eig1Re { get; init; }
        public required double eig1Im { get; init; }
        public required double eig2Re { get; init; }
        public required double eig2Im { get; init; }
    }

    /// <summary>The Nyquist verdict.</summary>
    internal sealed class NyquistRow
    {
        public required int encirclements { get; init; }
        public required int unstableModeCount { get; init; }
        public required bool stable { get; init; }
        public required double gainMarginDb { get; init; }
        public required double phaseMarginDeg { get; init; }
        public required double criticalFreqHz { get; init; }
        public required double minDistToMinusOne { get; init; }
    }

    /// <summary>One SCR sweep point.</summary>
    internal sealed class ScrSweepRow
    {
        public required double scr { get; init; }
        public required int encirclements { get; init; }
        public required bool stable { get; init; }
        public required double minDistToMinusOne { get; init; }
    }

    /// <summary>{ criticalScr, found }.</summary>
    internal sealed class BoundaryRow
    {
        public required double criticalScr { get; init; }
        public required bool found { get; init; }
    }

    /// <summary>{ rhpEigenvalueCountModal, agreement } (each omitted when null).</summary>
    internal sealed class CrossvalRow
    {
        public required int? rhpEigenvalueCountModal { get; init; }
        public required bool? agreement { get; init; }
    }
}

/// <summary>
/// Phase 0.1: the source-generated JSON metadata for the impedance-stability export document and every type it reaches, so it is read and
/// written without runtime reflection (NativeAOT). Metadata mode only: the bytes come from the same converters the
/// reflection path used, never from a separately generated fast path. Enums by name, as before.
/// The runtime options (naming, key case, null and number handling, indentation) are passed in by the call site.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ImpedanceStabilityExport.ImpedanceDoc))]
internal sealed partial class ImpedanceStabilityExportJsonContext : JsonSerializerContext;
