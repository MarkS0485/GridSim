using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Diagnostics;
using GridSim.Core.Scenarios;
using GridSim.Core.IO;

using Events = GridSim.Core.Events;

namespace GridSim.Tests.JsonSourceGen.Legacy;

// Phase 0.1 equivalence oracle: a verbatim, test-only copy of GridSim.Core.IO.H0StudyExport as it stood before source-generated JSON
// (GridSim 2.0.48), still serialising its anonymous objects through runtime reflection. Only the class name (LegacyH0StudyExport)
// and the namespace changed (a using alias stands in for the names Core resolves through its own namespace).
// JsonSourceGenEquivalenceTests requires the production exporter to write exactly the bytes this copy writes. Never
// edit it to follow the production code: it is the old behaviour, frozen.

internal static class LegacyH0StudyExport
{
    public const string Schema = "gridsim-h0-study/1";

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // No WhenWritingNull: the NaN->null sentinel fields must be PRESENT-as-null, not dropped.
        Converters = { new JsonStringEnumConverter() },
    };

    public static void WriteJson(string path, H0StudyResult result, H0StudyMeta meta)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, ToJson(result, meta));
    }

    public static string ToJson(H0StudyResult result, H0StudyMeta meta)
    {
        var doc = new
        {
            schema = Schema,
            meta,
            baseline = new { verdict = result.BaselineVerdict.ToString() },
            system = new
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
            swing = new
            {
                demandMw = result.DemandMw,
                config = new
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
                services = result.BaseConfigResolved.SystemResponseServices.Select(s => new
                {
                    mw = s.Mw,
                    deadbandHz = s.DeadbandHz,
                    fullDeliveryDeviationHz = s.FullDeliveryDeviationHz,
                    timeConstantS = s.TimeConstantS,
                    kneeDeviationHz = s.KneeDeviationHz,
                    kneeFraction = s.KneeFraction,
                }),
                events = result.Losses.OrderBy(e => e.AtSeconds).Select(e => new { atSeconds = e.AtSeconds, deltaPMw = e.DeltaPMw }),
                // R178 (October 2026 review, H hygiene stream): the factors are ScenarioCorners' own constants (they were
                // copied here as literals, the same values, so no number in the file moves), and each corner now also
                // carries the cap Apply puts on the scaled responsive fraction: 1.0 for the best corner, null (no cap)
                // for the nominal and worst corners, exactly as ScenarioCorners.Apply does. A sidecar that re-integrates
                // from these factors without the cap diverged whenever the base fraction times 1.5 exceeded 1.
                cornerFactors = new
                {
                    best = new
                    {
                        damping = ScenarioCorners.BestDampingScale,
                        responsive = ScenarioCorners.BestResponsiveScale,
                        governor = ScenarioCorners.BestGovernorTauScale,
                        responsiveCap = (double?)ScenarioCorners.MaxResponsiveFraction,
                    },
                    nominal = new { damping = 1.0, responsive = 1.0, governor = 1.0, responsiveCap = (double?)null },
                    worst = new
                    {
                        damping = ScenarioCorners.WorstDampingScale,
                        responsive = ScenarioCorners.WorstResponsiveScale,
                        governor = ScenarioCorners.WorstGovernorTauScale,
                        responsiveCap = (double?)null,
                    },
                },
            },
            axes = new
            {
                syncInertiaScales = result.SyncInertiaScales,
                gfmFractions = result.GfmFractions,
                corners = result.Corners.Select(c => c.ToString()),
            },
            inertiaTable = result.InertiaTable.Select(g => new
            {
                name = g.Name,
                bus = g.Bus,
                type = g.Type,
                hMwsPerMva = g.HMwsPerMva,
                mBaseMva = g.MBaseMva,
            }),
            cells = result.Cells.Select(c => new
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
                fleet = new
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
        return JsonSerializer.Serialize(doc, Opts);
    }

    /// <summary>NaN -> null (an explicit JSON sentinel for an undefined/un-formable quantity), else the value.</summary>
    private static object? Nn(double v) => double.IsNaN(v) ? null : v;
}
