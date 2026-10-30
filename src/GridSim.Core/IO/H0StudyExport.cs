using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Diagnostics;
using GridSim.Core.Scenarios;

namespace GridSim.Core.IO;

/// <summary>Provenance for an H=0 counterfactual study export.</summary>
public sealed record H0StudyMeta(
    string Event, string GitCommit, string TimestampUtc, string Machine,
    string GfmClass, string GflClass, ProvenanceTag Provenance = ProvenanceTag.Counterfactual);

/// <summary>
/// Serialises an <see cref="H0StudyResult"/> to schema <c>gridsim-h0-study/1</c>: the passed baseline
/// gate, the untransformed per-generator inertia table (so a sidecar can recompute E for any scale),
/// the sweep axes, and every cell. The real-vs-synthetic separation is visible in the wire format and
/// testable: <c>systemInertiaGvaS</c> / <c>deviceRotationalInertiaMwS</c> are REAL (1/2)Jw^2 that enter
/// E; the grid-forming fleet's virtual inertia <c>virtualInertiaMwS</c> (Ev) is a SEPARATE column that
/// enters only <c>effectiveFormingInertiaMwS</c> (E_real + Ev, NO floor), never the real budget. RoCoF
/// and nadir fields are JSON <c>null</c> (an explicit "undefined" sentinel) on a forming-dependent cell
/// rather than a huge finite E-floor artifact.
/// </summary>
public static class H0StudyExport
{
    public const string Schema = "gridsim-h0-study/1";

    // Phase 0.1 (source-generated JSON, so the core can run under NativeAOT): exactly the options this file has always
    // used, now bound to the source-generated H0StudyExportJsonContext (below) so no runtime reflection is needed.
    // Enums are still by name, through the source-generated per-enum JsonStringEnumConverter<TEnum> (UseStringEnumConverter
    // on the context) in place of the reflection-based JsonStringEnumConverter: both write the member name and both
    // still accept an integer on read.
    // Internal, not private, so JsonSourceGenEquivalenceTests reads and writes through this very instance.
    internal static readonly H0StudyExportJsonContext Json = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // No WhenWritingNull: the NaN->null sentinel fields must be PRESENT-as-null, not dropped.
    });

    public static void WriteJson(string path, H0StudyResult result, H0StudyMeta meta)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, ToJson(result, meta));
    }

    public static string ToJson(H0StudyResult result, H0StudyMeta meta)
    {
        // Phase 0.1: named types (below) in place of the anonymous objects: same members, same order, same types, so
        // the bytes are unchanged; the source-generated context can describe a named type (NativeAOT).
        var doc = new H0Doc
        {
            schema = Schema,
            meta = meta,
            baseline = new BaselineRow { verdict = result.BaselineVerdict.ToString() },
            system = new SystemRow
            {
                // R287 (B4, October 2026): "largestLossMw" is the historical key and keeps its documented value, the
                // |sum of every exogenous loss| (so existing readers see no change); it is NOT the largest loss. The
                // honest names follow: the same total, the RoCoF-setting loss (first 5 s) that is now the analytic dP
                // of bareInertiaRocofHzPerS, and the largest single loss step. Additive: schema id unchanged.
                largestLossMw = result.TotalExogenousLossMw,
                totalExogenousLossMw = result.TotalExogenousLossMw,
                rocofSettingLossMw = result.RocofSettingLossMw,
                rocofSettingWindowS = Events.GridEvent.RocofSettingWindowSeconds,
                largestSingleLossMw = result.LargestSingleLossMw,
                lfddFirstStageHz = result.LfddThresholdHz,
                nominalHz = result.NominalHz,
            },
            invariantNote =
                "systemInertiaGvaS is REAL (1/2)Jw^2 summed into E; virtualInertiaMwS (Ev) is the grid-forming " +
                "fleet's virtual control inertia and enters ONLY effectiveFormingInertiaMwS (E_real+Ev), never E.",
            idealizationNote =
                "The grid-forming fleet is represented as an ideal Ev/Dv former (Workstream A forming path). This " +
                "idealises away its SoC/headroom cap (the fleet-injection path would enforce it) - a documented MVP " +
                "simplification. RoCoF is reported on the COI basis; null RoCoF/nadir = un-formable (forming-dependent) cell.",
            // Everything a sidecar needs to independently RE-INTEGRATE the fleet-free forming swing and
            // reproduce each cell's bareFormedRocofHzPerS / bareFormedNadirHz (the forming ODE decisively,
            // not just closed-form arithmetic). Corner factors match Scenarios.ScenarioCorners.Apply.
            swing = new SwingRow
            {
                demandMw = result.DemandMw,
                config = new ConfigRow
                {
                    nominalHz = result.BaseConfigResolved.NominalHz,
                    droopPu = result.BaseConfigResolved.DroopPu,
                    dampingPctPerHz = result.BaseConfigResolved.DampingPctPerHz,
                    responsiveFraction = result.BaseConfigResolved.ResponsiveFraction,
                    governorTimeConstantS = result.BaseConfigResolved.GovernorTimeConstantS,
                    fastResponseMw = result.BaseConfigResolved.FastResponseMw,
                    fastResponseTimeConstantS = result.BaseConfigResolved.FastResponseTimeConstantS,
                    fastResponseDeadbandHz = result.BaseConfigResolved.FastResponseDeadbandHz,
                    stepSeconds = result.BaseConfigResolved.StepSeconds,
                    durationSeconds = result.BaseConfigResolved.DurationSeconds,
                    initialFrequencyHz = result.BaseConfigResolved.InitialFrequencyHz,
                },
                services = result.BaseConfigResolved.SystemResponseServices.Select(s => new ServiceRow
                {
                    mw = s.Mw,
                    deadbandHz = s.DeadbandHz,
                    fullDeliveryDeviationHz = s.FullDeliveryDeviationHz,
                    timeConstantS = s.TimeConstantS,
                    kneeDeviationHz = s.KneeDeviationHz,
                    kneeFraction = s.KneeFraction,
                }),
                events = result.Losses.OrderBy(e => e.AtSeconds).Select(e => new EventRow { atSeconds = e.AtSeconds, deltaPMw = e.DeltaPMw }),
                // R178 (October 2026 review, H hygiene stream): the factors are ScenarioCorners' own constants (they were
                // copied here as literals, the same values, so no number in the file moves), and each corner now also
                // carries the cap Apply puts on the scaled responsive fraction: 1.0 for the best corner, null (no cap)
                // for the nominal and worst corners, exactly as ScenarioCorners.Apply does. A sidecar that re-integrates
                // from these factors without the cap diverged whenever the base fraction times 1.5 exceeded 1.
                cornerFactors = new CornerFactorsRow
                {
                    best = new CornerRow
                    {
                        damping = ScenarioCorners.BestDampingScale,
                        responsive = ScenarioCorners.BestResponsiveScale,
                        governor = ScenarioCorners.BestGovernorTauScale,
                        responsiveCap = (double?)ScenarioCorners.MaxResponsiveFraction,
                    },
                    nominal = new CornerRow { damping = 1.0, responsive = 1.0, governor = 1.0, responsiveCap = (double?)null },
                    worst = new CornerRow
                    {
                        damping = ScenarioCorners.WorstDampingScale,
                        responsive = ScenarioCorners.WorstResponsiveScale,
                        governor = ScenarioCorners.WorstGovernorTauScale,
                        responsiveCap = (double?)null,
                    },
                },
            },
            axes = new AxesRow
            {
                syncInertiaScales = result.SyncInertiaScales,
                gfmFractions = result.GfmFractions,
                corners = result.Corners.Select(c => c.ToString()),
            },
            inertiaTable = result.InertiaTable.Select(g => new InertiaRow
            {
                name = g.Name,
                bus = g.Bus,
                type = g.Type,
                hMwsPerMva = g.HMwsPerMva,
                mBaseMva = g.MBaseMva,
            }),
            cells = result.Cells.Select(c => new CellRow
            {
                syncInertiaScale = c.SyncInertiaScale,
                gfmFraction = c.GfmFraction,                      // the requested axis value
                realisedGfmFraction = c.RealisedGfmFraction,      // R289: the share whole units realised (additive field)
                corner = c.Corner.ToString(),
                systemInertiaGvaS = c.SystemInertiaGvaS,
                deviceRotationalInertiaMwS = 0.0,                 // no device in this study; kept separate, always 0
                virtualInertiaMwS = c.VirtualInertiaMwS,          // Ev - virtual, never in real E
                formingDampingMwPerHz = c.FormingDampingMwPerHz,  // Dv
                effectiveFormingInertiaMwS = c.EffectiveFormingInertiaMwS, // E_real + Ev, NO floor
                bareInertiaRocofHzPerS = Nn(c.BareInertiaRocofHzPerS),
                bareFormedRocofHzPerS = Nn(c.BareFormedRocofHzPerS),
                bareFormedNadirHz = Nn(c.BareFormedNadirHz),
                rocofHzPerS = Nn(c.RocofHzPerS),
                nadirHz = Nn(c.NadirHz),
                nadirSeconds = Nn(c.NadirSeconds),
                settleHz = Nn(c.SettleHz),
                fleet = new FleetRow
                {
                    trippedCount = c.TrippedCount,
                    totalTrippedMw = c.TotalTrippedMw,
                    netFleetMwAtNadir = Nn(c.NetFleetMwAtNadir),
                },
                formingDependent = c.FormingDependent,
                formedBy = c.FormedBy,
                lfddAtRisk = c.LfddAtRisk,
                cascadeLossOfMains = c.CascadeLossOfMains,
                assessment = c.Assessment,
            }),
            recurrence = new[]
            {
                "E_real = sum(H*scale*MBase) over synchronous units (converters carry H=0)",
                "RoCoF0(bare) = f0*dP/(2*E_real) ; NaN (null) when E_real == 0",
                "Ev = sum(synInertiaMwsPerHz * f0/2) over the grid-forming fleet ; Dv = sum(dampingMwPerHz)",
                "forming denominator = E_real + Ev (NO Math.Max floor); nothing forms => forming-dependent (null RoCoF)",
                "fleet: grid-following units inject FFR and trip on RoCoF -> Loss-of-Mains deficit fed back into the swing",
            },
        };
        return JsonSerializer.Serialize(doc, Json.H0Doc);
    }

    /// <summary>NaN -> null (an explicit JSON sentinel for an undefined/un-formable quantity), else the value.</summary>
    /// <remarks>Phase 0.1: typed double? (it was object?, a boxed double or null). Both write the same: null, or the
    /// number through the same double converter.</remarks>
    private static double? Nn(double v) => double.IsNaN(v) ? null : v;

    // ---- Phase 0.1: the document as named types ----------------------------------------------------------------------
    // These replace the anonymous objects ToJson used to build, so the source-generated context can describe them
    // (NativeAOT). The options apply camelCase, which leaves these already-camelCase names unchanged, so the JSON names
    // are spelt exactly as the anonymous members were; declared in the same order and with the same CLR types. No
    // WhenWritingNull here: a null is written as null, as before. JsonSourceGenEquivalenceTests holds the bytes equal to
    // the old reflection-built document.

    /// <summary>The whole <c>gridsim-h0-study/1</c> document.</summary>
    internal sealed class H0Doc
    {
        public required string schema { get; init; }
        public required H0StudyMeta meta { get; init; }
        public required BaselineRow baseline { get; init; }
        public required SystemRow system { get; init; }
        public required string invariantNote { get; init; }
        public required string idealizationNote { get; init; }
        public required SwingRow swing { get; init; }
        public required AxesRow axes { get; init; }
        public required IEnumerable<InertiaRow> inertiaTable { get; init; }
        public required IEnumerable<CellRow> cells { get; init; }
        public required string[] recurrence { get; init; }
    }

    /// <summary>{ verdict }.</summary>
    internal sealed class BaselineRow
    {
        public required string verdict { get; init; }
    }

    /// <summary>The system-level loss figures.</summary>
    internal sealed class SystemRow
    {
        public required double largestLossMw { get; init; }
        public required double totalExogenousLossMw { get; init; }
        public required double rocofSettingLossMw { get; init; }
        public required double rocofSettingWindowS { get; init; }
        public required double largestSingleLossMw { get; init; }
        public required double lfddFirstStageHz { get; init; }
        public required double nominalHz { get; init; }
    }

    /// <summary>Everything a sidecar needs to re-integrate the forming swing.</summary>
    internal sealed class SwingRow
    {
        public required double demandMw { get; init; }
        public required ConfigRow config { get; init; }
        public required IEnumerable<ServiceRow> services { get; init; }
        public required IEnumerable<EventRow> events { get; init; }
        public required CornerFactorsRow cornerFactors { get; init; }
    }

    /// <summary>The resolved base dynamics configuration.</summary>
    internal sealed class ConfigRow
    {
        public required double nominalHz { get; init; }
        public required double droopPu { get; init; }
        public required double dampingPctPerHz { get; init; }
        public required double responsiveFraction { get; init; }
        public required double governorTimeConstantS { get; init; }
        public required double fastResponseMw { get; init; }
        public required double fastResponseTimeConstantS { get; init; }
        public required double fastResponseDeadbandHz { get; init; }
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

    /// <summary>One loss: { atSeconds, deltaPMw }.</summary>
    internal sealed class EventRow
    {
        public required double atSeconds { get; init; }
        public required double deltaPMw { get; init; }
    }

    /// <summary>{ best, nominal, worst }.</summary>
    internal sealed class CornerFactorsRow
    {
        public required CornerRow best { get; init; }
        public required CornerRow nominal { get; init; }
        public required CornerRow worst { get; init; }
    }

    /// <summary>One corner's scale factors and responsive-fraction cap (null = no cap).</summary>
    internal sealed class CornerRow
    {
        public required double damping { get; init; }
        public required double responsive { get; init; }
        public required double governor { get; init; }
        public required double? responsiveCap { get; init; }
    }

    /// <summary>The sweep axes.</summary>
    internal sealed class AxesRow
    {
        public required IReadOnlyList<double> syncInertiaScales { get; init; }
        public required IReadOnlyList<double> gfmFractions { get; init; }
        public required IEnumerable<string> corners { get; init; }
    }

    /// <summary>One generator of the untransformed inertia table.</summary>
    internal sealed class InertiaRow
    {
        public required string name { get; init; }
        public required int bus { get; init; }
        public required string type { get; init; }
        public required double hMwsPerMva { get; init; }
        public required double mBaseMva { get; init; }
    }

    /// <summary>One sweep cell.</summary>
    internal sealed class CellRow
    {
        public required double syncInertiaScale { get; init; }
        public required double gfmFraction { get; init; }
        public required double realisedGfmFraction { get; init; }
        public required string corner { get; init; }
        public required double systemInertiaGvaS { get; init; }
        public required double deviceRotationalInertiaMwS { get; init; }
        public required double virtualInertiaMwS { get; init; }
        public required double formingDampingMwPerHz { get; init; }
        public required double effectiveFormingInertiaMwS { get; init; }
        public required double? bareInertiaRocofHzPerS { get; init; }
        public required double? bareFormedRocofHzPerS { get; init; }
        public required double? bareFormedNadirHz { get; init; }
        public required double? rocofHzPerS { get; init; }
        public required double? nadirHz { get; init; }
        public required double? nadirSeconds { get; init; }
        public required double? settleHz { get; init; }
        public required FleetRow fleet { get; init; }
        public required bool formingDependent { get; init; }
        public required string formedBy { get; init; }
        public required bool lfddAtRisk { get; init; }
        public required bool cascadeLossOfMains { get; init; }
        public required string assessment { get; init; }
    }

    /// <summary>The cell's fleet outcome.</summary>
    internal sealed class FleetRow
    {
        public required int trippedCount { get; init; }
        public required double totalTrippedMw { get; init; }
        public required double? netFleetMwAtNadir { get; init; }
    }
}

/// <summary>
/// Phase 0.1: the source-generated JSON metadata for the H=0 study export document and every type it reaches, so it is read and
/// written without runtime reflection (NativeAOT). Metadata mode only: the bytes come from the same converters the
/// reflection path used, never from a separately generated fast path. Enums by name, as before.
/// The runtime options (naming, key case, null and number handling, indentation) are passed in by the call site.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, UseStringEnumConverter = true)]
[JsonSerializable(typeof(H0StudyExport.H0Doc))]
internal sealed partial class H0StudyExportJsonContext : JsonSerializerContext;
