using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;

namespace GridSim.Core.IO;

/// <summary>Provenance for a frequency (swing-solve) export - the analogue of <see cref="SolveExportMeta"/>.</summary>
public sealed record FrequencyExportMeta(
    string Event, string GitCommit, string TimestampUtc, double ElapsedMs, string Machine,
    ProvenanceTag Provenance = ProvenanceTag.Synthetic);

/// <summary>
/// The swing-solve analogue of <see cref="SolveExport"/>: serialises <b>everything a frequency solve used and
/// produced</b> so a reviewer can hand-check <c>RoCoF = f0*deltaP/(2E)</c> the way they hand-check the Jacobian
/// today. It records the run metadata, the split inertia budget (system (1/2)J*omega^2 + device (1/2)J*omega^2 -> the E that enters
/// the swing equation), the fast-response power as a <b>separate</b> injection (never folded into E), every
/// event, the full <see cref="DynamicsConfig"/>, the discrete recurrence in plain text, the headline metrics,
/// and the whole trajectory. Deterministic given a deterministic solve.
/// </summary>
public static class FrequencyExport
{
    // Phase 0.1 (source-generated JSON, so the core can run under NativeAOT): exactly the options this file has always
    // used, now bound to the source-generated FrequencyExportJsonContext (below) so no runtime reflection is needed.
    // Enums are still by name, through the source-generated per-enum JsonStringEnumConverter<TEnum> (UseStringEnumConverter
    // on the context) in place of the reflection-based JsonStringEnumConverter: both write the member name and both
    // still accept an integer on read.
    // Internal, not private, so JsonSourceGenEquivalenceTests reads and writes through this very instance.
    internal static readonly FrequencyExportJsonContext Json = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, // uniform camelCase across meta + body
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    });

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
        // Phase 0.1: a named type (FormingRow, below) in place of the anonymous object; the reflection path wrote the
        // runtime type behind the "object?" declaration, which is exactly this type's members.
        FormingRow? formingBlock = forming
            ? new FormingRow
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

        // Phase 0.1: named types (below) in place of the anonymous objects: same members, same order, same types, so
        // the bytes are unchanged; the source-generated context can describe a named type (NativeAOT).
        var doc = new FrequencyDoc
        {
            schema = "gridsim-frequency-export/1",
            meta = meta,
            // The inertia budget, kept split: intrinsic (1/2)J*omega^2 (system + device) is what enters E; the device's
            // fast response is a SEPARATE power injection and is deliberately NOT part of E.
            inertia = new InertiaRow
            {
                systemInertiaMwS = systemInertiaMwS,
                deviceRotationalInertiaMwS = cfg.DeviceRotationalInertiaMwS, // real (1/2)J*omega^2, NOT df/dt synthetic
                effectiveInertiaMwS = effectiveE,                            // = the E in RoCoF = f0*deltaP/(2E)
                note = "E excludes fast response; FFR is a power injection (see fastResponse), never stored energy.",
            },
            forming = formingBlock,
            fastResponse = new FastResponseRow
            {
                fastResponseMw = cfg.FastResponseMw,
                timeConstantS = cfg.FastResponseTimeConstantS,
                deadbandHz = cfg.FastResponseDeadbandHz,
                note = "Separate labelled column - a power injection that arrests the nadir, distinct from inertia.",
            },
            demandMw = demandMw,
            config = new ConfigRow
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
            systemResponseServices = cfg.SystemResponseServices.Count == 0 ? null : cfg.SystemResponseServices.Select(s => new ServiceRow
            {
                mw = s.Mw,
                deadbandHz = s.DeadbandHz,
                fullDeliveryDeviationHz = s.FullDeliveryDeviationHz,
                timeConstantS = s.TimeConstantS,
                kneeDeviationHz = s.KneeDeviationHz,
                kneeFraction = s.KneeFraction,
            }).ToList(),
            events = events.OrderBy(e => e.AtSeconds).Select(e => new EventRow { atSeconds = e.AtSeconds, deltaPMw = e.DeltaPMw }),
            // The exact discrete recurrence, so RoCoF/nadir are hand-checkable from the numbers above.
            recurrence = recurrence,
            headline = new HeadlineRow
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
                .Select(p => new PointRow
            {
                t = (p.Time - response.Points[0].Time).TotalSeconds,
                hz = p.Hz,
                rocofHzPerS = p.RocofHzPerS,
            }),
        };
        return JsonSerializer.Serialize(doc, Json.FrequencyDoc);
    }

    // ---- Phase 0.1: the document as named types ----------------------------------------------------------------------
    // These replace the anonymous objects ToJson used to build, so the source-generated context can describe them
    // (NativeAOT). The options apply camelCase, which leaves these already-camelCase names unchanged, so the JSON names
    // are spelt exactly as the anonymous members were; declared in the same order and with the same CLR types.
    // JsonSourceGenEquivalenceTests holds the bytes equal to the old reflection-built document.

    /// <summary>The whole <c>gridsim-frequency-export/1</c> document.</summary>
    internal sealed class FrequencyDoc
    {
        public required string schema { get; init; }
        public required FrequencyExportMeta meta { get; init; }
        public required InertiaRow inertia { get; init; }
        public required FormingRow? forming { get; init; }
        public required FastResponseRow fastResponse { get; init; }
        public required double demandMw { get; init; }
        public required ConfigRow config { get; init; }
        public required List<ServiceRow>? systemResponseServices { get; init; }
        public required IEnumerable<EventRow> events { get; init; }
        public required string[] recurrence { get; init; }
        public required HeadlineRow headline { get; init; }
        public required IEnumerable<PointRow> points { get; init; }
    }

    /// <summary>The split inertia budget.</summary>
    internal sealed class InertiaRow
    {
        public required double systemInertiaMwS { get; init; }
        public required double deviceRotationalInertiaMwS { get; init; }
        public required double effectiveInertiaMwS { get; init; }
        public required string note { get; init; }
    }

    /// <summary>The forming block (null, so omitted, unless the run is a forming run).</summary>
    internal sealed class FormingRow
    {
        public required string mode { get; init; }
        public required double virtualFormingInertiaMwS { get; init; }
        public required double formingDampingMwPerHz { get; init; }
        public required double formingSetpointMw { get; init; }
        public required double effectiveFormingInertiaMwS { get; init; }
        public required string note { get; init; }
    }

    /// <summary>The fast-response injection, a separate labelled column.</summary>
    internal sealed class FastResponseRow
    {
        public required double fastResponseMw { get; init; }
        public required double timeConstantS { get; init; }
        public required double deadbandHz { get; init; }
        public required string note { get; init; }
    }

    /// <summary>The dynamics configuration the run used.</summary>
    internal sealed class ConfigRow
    {
        public required double nominalHz { get; init; }
        public required double droopPu { get; init; }
        public required double dampingPctPerHz { get; init; }
        public required double responsiveFraction { get; init; }
        public required double governorTimeConstantS { get; init; }
        public required double stepSeconds { get; init; }
        public required double durationSeconds { get; init; }
        public required double? initialFrequencyHz { get; init; }
    }

    /// <summary>One recorded response service.</summary>
    internal sealed class ServiceRow
    {
        public required double mw { get; init; }
        public required double deadbandHz { get; init; }
        public required double fullDeliveryDeviationHz { get; init; }
        public required double timeConstantS { get; init; }
        public required double kneeDeviationHz { get; init; }
        public required double kneeFraction { get; init; }
    }

    /// <summary>One event: { atSeconds, deltaPMw }.</summary>
    internal sealed class EventRow
    {
        public required double atSeconds { get; init; }
        public required double deltaPMw { get; init; }
    }

    /// <summary>The headline metrics.</summary>
    internal sealed class HeadlineRow
    {
        public required double initialRocofHzPerS { get; init; }
        public required double nadirHz { get; init; }
        public required double nadirSeconds { get; init; }
        public required double settleHz { get; init; }
    }

    /// <summary>One trajectory point: { t, hz, rocofHzPerS }.</summary>
    internal sealed class PointRow
    {
        public required double t { get; init; }
        public required double hz { get; init; }
        public required double rocofHzPerS { get; init; }
    }
}

/// <summary>
/// Phase 0.1: the source-generated JSON metadata for the frequency (swing-solve) export document and every type it reaches, so it is read and
/// written without runtime reflection (NativeAOT). Metadata mode only: the bytes come from the same converters the
/// reflection path used, never from a separately generated fast path. Enums by name, as before.
/// The runtime options (naming, key case, null and number handling, indentation) are passed in by the call site.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, UseStringEnumConverter = true)]
[JsonSerializable(typeof(FrequencyExport.FrequencyDoc))]
internal sealed partial class FrequencyExportJsonContext : JsonSerializerContext;
