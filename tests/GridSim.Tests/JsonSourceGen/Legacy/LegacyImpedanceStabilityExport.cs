using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Impedance;
using GridSim.Core.Model;
using GridSim.Core.IO;

namespace GridSim.Tests.JsonSourceGen.Legacy;

// Phase 0.1 equivalence oracle: a verbatim, test-only copy of GridSim.Core.IO.ImpedanceStabilityExport as it stood before source-generated JSON
// (GridSim 2.0.48), still serialising its anonymous objects through runtime reflection. Only the class name (LegacyImpedanceStabilityExport)
// and the namespace changed.
// JsonSourceGenEquivalenceTests requires the production exporter to write exactly the bytes this copy writes. Never
// edit it to follow the production code: it is the old behaviour, frozen.

internal static class LegacyImpedanceStabilityExport
{
    public const string Schema = "gridsim-impedance-stability/1";

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void WriteJson(string path, GridModel model, ImpedanceStabilityResult result, SolveExportMeta meta)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, ToJson(model, result, meta));
    }

    public static string ToJson(GridModel model, ImpedanceStabilityResult r, SolveExportMeta meta)
    {
        var doc = new
        {
            schema = Schema,
            meta,
            provenance = r.Provenance,   // Recorded (measured inner-loop params) vs CuratedPlaceholder [VERIFY]
            model = new { name = model.Name, baseMva = model.BaseMva, busCount = model.BusCount },
            inverterBus = r.InverterBus,
            mode = r.Mode,
            modelDetail = r.Detail,
            criterion = r.Criterion,
            operatingPoint = new
            {
                vd0Pu = r.OperatingPoint.Vd0Pu,
                vq0Pu = r.OperatingPoint.Vq0Pu,
                id0Pu = r.OperatingPoint.Id0Pu,
                iq0Pu = r.OperatingPoint.Iq0Pu,
                pPu = r.OperatingPoint.PPu,
                qPu = r.OperatingPoint.QPu,
                vd0Clamped = r.OperatingPoint.Vd0WasClamped,
            },
            converterParams = r.Params is null ? null : new
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
            gridImpedance = r.Frequency.Select(s => new { fHz = s.FHz, re = s.ZGrid.Real, im = s.ZGrid.Imaginary }),
            // D-DOC-175 (October 2026, docs-session finding): the converter's small-signal ADMITTANCE Y_conv (per unit on
            // the system base; the return ratio is L = Z_grid * Y_conv) under its own name. It was written only under the
            // key converterImpedance, which names the wrong quantity (an impedance is 1 / Y). converterAdmittance is the
            // key to read; converterImpedance still carries the same numbers for compatibility with readers of earlier
            // exports, is DEPRECATED, and goes at the next schema version (gridsim-impedance-stability/2). Both are the
            // same values, bit for bit.
            converterAdmittance = r.Frequency.Select(s => new { fHz = s.FHz, re = s.YConv.Real, im = s.YConv.Imaginary }),
            converterImpedance = r.Frequency.Select(s => new { fHz = s.FHz, re = s.YConv.Real, im = s.YConv.Imaginary }),
            minorLoop = r.Frequency.Select(s => new
            {
                fHz = s.FHz,
                lRe = s.L.Real, lIm = s.L.Imaginary,
                eig1Re = s.Eig1.Real, eig1Im = s.Eig1.Imaginary,
                eig2Re = s.Eig2.Real, eig2Im = s.Eig2.Imaginary,
            }),
            nyquist = new
            {
                encirclements = r.Nyquist.Encirclements,
                unstableModeCount = r.Nyquist.UnstableModeCount,
                stable = r.Nyquist.Stable,
                gainMarginDb = r.Nyquist.GainMarginDb,
                phaseMarginDeg = r.Nyquist.PhaseMarginDeg,
                criticalFreqHz = r.Nyquist.CriticalFreqHz,
                minDistToMinusOne = r.Nyquist.MinDistToMinusOne,
            },
            scrSweep = r.ScrSweep.Select(s => new
            {
                scr = s.Scr, encirclements = s.Encirclements, stable = s.Stable, minDistToMinusOne = s.MinDistToMinusOne,
            }),
            boundary = new { criticalScr = r.CriticalScr, found = r.BoundaryFound },
            crossval = new { rhpEigenvalueCountModal = r.RhpEigenvalueCountFromModal, agreement = r.AgreesWithModal },
        };
        return JsonSerializer.Serialize(doc, Opts);
    }
}
