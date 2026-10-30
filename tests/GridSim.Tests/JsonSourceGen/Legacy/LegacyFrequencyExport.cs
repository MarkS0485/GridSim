using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.IO;

namespace GridSim.Tests.JsonSourceGen.Legacy;

// Phase 0.1 equivalence oracle: a verbatim, test-only copy of GridSim.Core.IO.FrequencyExport as it stood before source-generated JSON
// (GridSim 2.0.48), still serialising its anonymous objects through runtime reflection. Only the class name (LegacyFrequencyExport)
// and the namespace changed.
// JsonSourceGenEquivalenceTests requires the production exporter to write exactly the bytes this copy writes. Never
// edit it to follow the production code: it is the old behaviour, frozen.

internal static class LegacyFrequencyExport
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, // uniform camelCase across meta + body
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The classic (non-forming) discrete recurrence - the exact byte sequence pre-forming exports carried.</summary>
    private static readonly string[] BaseRecurrence =
    [
        "df = f - f0",
        "pEvent(t) = sum of deltaPMw over events with atSeconds <= t + 1e-6*dt",
        "pGovTarget = -(df/f0)/droopPu * responsiveFraction*demand ;  pGov += (pGovTarget-pGov)/governorTau * dt",
        "ffrTarget = clamp(-(FFR/0.5)*df, -FFR, +FFR) if |df|>deadband else 0 ;  pFfr += (ffrTarget-pFfr)/max(dt,ffrTau) * dt",
        "pImbalance = pEvent + pGov + pFfr - damping*df",
        "rocof = f0/(2E) * pImbalance ;  f += rocof*dt",
    ];

    /// <summary>The two extra recurrence lines appended ONLY under a grid-forming run.</summary>
    private static readonly string[] FormingRecurrence =
    [
        "forming: rocof = f0/(2*(Ereal+Ev)) * (pForcing - (damp+Dv)*df)",
        "forming advance (semi-implicit, A-stable): f_{n+1} = (f + a*(pForcing + Dtot*f0))/(1 + a*Dtot),  a = dt*f0/(2*(Ereal+Ev))",
    ];

    public static void WriteJson(
        string path, double systemInertiaMwS, double demandMw, IReadOnlyList<FrequencyEvent> events,
        DynamicsConfig cfg, FrequencyResponse response, FrequencyExportMeta meta)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, ToJson(systemInertiaMwS, demandMw, events, cfg, response, meta));
    }

    public static string ToJson(
        double systemInertiaMwS, double demandMw, IReadOnlyList<FrequencyEvent> events,
        DynamicsConfig cfg, FrequencyResponse response, FrequencyExportMeta meta)
    {
        // Re-derive E exactly as FrequencyDynamics does, so the export is self-checking. This is the REAL
        // denominator and is a pure function of system + device rotational inertia - Ev NEVER enters it.
        double effectiveE = Math.Max(SwingKernel.MinInertiaMwS, systemInertiaMwS + cfg.DeviceRotationalInertiaMwS);   // GR-648: the swing's own floor by name
        bool forming = cfg.Forming != FormingMode.None;

        // Forming block: present ONLY under a forming run (WhenWritingNull drops it, so Forming==None
        // exports are byte-identical). effectiveFormingInertiaMwS carries NO Math.Max floor, matching
        // FormingKernel.EffectiveFormingInertiaMwS - the low-E denominator the crossval sidecar must read.
        object? formingBlock = forming
            ? new
            {
                mode = cfg.Forming.ToString(),
                virtualFormingInertiaMwS = cfg.VirtualInertiaMwS,           // Ev = Hv*Sv - a CONTROL param, NOT real energy
                formingDampingMwPerHz = cfg.FormingDampingMwPerHz,          // Dv
                formingSetpointMw = cfg.FormingSetpointMw,
                effectiveFormingInertiaMwS = systemInertiaMwS + cfg.DeviceRotationalInertiaMwS + cfg.VirtualInertiaMwS, // NO floor
                note = "Ev is virtual (1/2)Jw^2-equivalent CONTROL inertia, kept separate from real inertia; never summed into it.",
            }
            : null;

        // The forming recurrence strings are appended ONLY under a forming run, so Forming==None keeps the
        // exact original recurrence array (byte-identical for existing golden/consumer exports).
        string[] recurrence = forming
            ? [.. BaseRecurrence, .. FormingRecurrence]
            : BaseRecurrence;

        var doc = new
        {
            schema = "gridsim-frequency-export/1",
            meta,
            // The inertia budget, kept split: intrinsic (1/2)J*omega^2 (system + device) is what enters E; the device's
            // fast response is a SEPARATE power injection and is deliberately NOT part of E.
            inertia = new
            {
                systemInertiaMwS,
                deviceRotationalInertiaMwS = cfg.DeviceRotationalInertiaMwS, // real (1/2)J*omega^2, NOT df/dt synthetic
                effectiveInertiaMwS = effectiveE,                            // = the E in RoCoF = f0*deltaP/(2E)
                note = "E excludes fast response; FFR is a power injection (see fastResponse), never stored energy.",
            },
            forming = formingBlock,
            fastResponse = new
            {
                fastResponseMw = cfg.FastResponseMw,
                timeConstantS = cfg.FastResponseTimeConstantS,
                deadbandHz = cfg.FastResponseDeadbandHz,
                note = "Separate labelled column - a power injection that arrests the nadir, distinct from inertia.",
            },
            demandMw,
            config = new
            {
                nominalHz = cfg.NominalHz,
                droopPu = cfg.DroopPu,
                dampingPctPerHz = cfg.DampingPctPerHz,
                responsiveFraction = cfg.ResponsiveFraction,
                governorTimeConstantS = cfg.GovernorTimeConstantS,
                stepSeconds = cfg.StepSeconds,
                durationSeconds = cfg.DurationSeconds,
                // R056: an equilibrium (offset) pre-event start, e.g. a recorded event that began at 49.95 Hz. Null
                // (the nominal start) is dropped by WhenWritingNull, so nominal-start exports are byte-identical.
                initialFrequencyHz = cfg.InitialFrequencyHz,
            },
            // R056: the recorded response services the simulation delivered (replay-event and scenario apply an
            // event's recorded services). Present ONLY when there are any (null is dropped), so service-free exports
            // are byte-identical. Without this block an independent re-integration silently used different physics.
            systemResponseServices = cfg.SystemResponseServices.Count == 0 ? null : cfg.SystemResponseServices.Select(s => new
            {
                mw = s.Mw,
                deadbandHz = s.DeadbandHz,
                fullDeliveryDeviationHz = s.FullDeliveryDeviationHz,
                timeConstantS = s.TimeConstantS,
                kneeDeviationHz = s.KneeDeviationHz,
                kneeFraction = s.KneeFraction,
            }).ToList(),
            events = events.OrderBy(e => e.AtSeconds).Select(e => new { atSeconds = e.AtSeconds, deltaPMw = e.DeltaPMw }),
            // The exact discrete recurrence, so RoCoF/nadir are hand-checkable from the numbers above.
            recurrence,
            headline = new
            {
                // N141: the PEAK RoCoF of the run (largest-magnitude step value, sign kept). The key keeps its historical
                // name because the crossval sidecar (tools/crossval/crossval_freq.py) and other readers use it.
                initialRocofHzPerS = response.InitialRocofHzPerS,
                nadirHz = response.NadirHz,
                nadirSeconds = response.NadirSeconds,
                settleHz = response.SettleHz,
            },
            points = (response.Points.Count > 0 ? response.Points
                    // GR-649: a response with no samples has nothing to export; it used to be written as a valid-looking
                    // document with headline values and an empty points array.
                    : throw new InvalidDataException("The frequency response has no samples; there is nothing to export."))
                .Select(p => new
            {
                t = (p.Time - response.Points[0].Time).TotalSeconds,
                hz = p.Hz,
                rocofHzPerS = p.RocofHzPerS,
            }),
        };
        return JsonSerializer.Serialize(doc, Opts);
    }
}
