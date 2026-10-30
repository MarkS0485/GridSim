using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.Events;
using GridSim.Core.Fleet;
using GridSim.Core.Replay;
using GridSim.Core.Scenarios;

namespace GridSim.Core.Evidence;

/// <summary>
/// One column of the evidence dossier: recorded, simulated-no-device, or counterfactual-with-device. The device's
/// <b>rotational inertia and fast response are kept in separate labelled fields</b> - intrinsic (1/2)J*omega^2 is never
/// blurred with the FFR power injection.
/// </summary>
public sealed record EvidenceColumn(
    string Label,
    ProvenanceTag Provenance,
    double RocofHzPerS,
    double NadirHz,
    double? SettleHz,
    bool LfddAtRisk,
    double DeviceRotationalInertiaGvaS,
    double DeviceFfrGw);

/// <summary>Single aggregate device vs a staggered fleet of the same total budget (the non-additivity, in one place).</summary>
public sealed record FleetComparison(
    double InertiaGvaS, int Units, double StaggerSeconds,
    double AggregateNadirHz, double FleetNadirHz,
    double AggregateFfrAtNadirMw, double FleetFfrAtNadirMw);

/// <summary>What to assemble the dossier from: the event, the device under test, and the sweep/fleet/cost settings.</summary>
public sealed record EvidenceSpec(
    GridEvent Event,
    DeviceConfig Device,
    IReadOnlyList<double> InertiaAxisGvaS,
    double CounterfactualInertiaGvaS,
    int FleetUnits,
    double FleetStaggerSeconds,
    AvoidedCostInputs AvoidedCost)
{
    public static EvidenceSpec Default(GridEvent ev, DeviceConfig device) => new(
        ev, device,
        SweepAxes.GbInertiaTrajectoryGvaS,
        CounterfactualInertiaGvaS: ev.PreEventInertiaMwS / 1000.0,
        // ~10 s onset spread: GB primary response is fully delivered by 10 s and a diverse FFR/DSR fleet
        // staggers across that window; a sub-second default finishes before the nadir and hides the
        // non-additivity the dossier is meant to show.
        FleetUnits: 100, FleetStaggerSeconds: 10.0,
        AvoidedCost: new AvoidedCostInputs());
}

/// <summary>
/// The unified evidence dossier (schema <c>gridsim-evidence/1</c>): recorded vs simulated vs counterfactual,
/// the inertia risk curve, single-vs-fleet, and the avoided cost from the replayed event. Everything carries a
/// <see cref="ProvenanceTag"/>; intrinsic inertia and FFR are never merged; and the whole thing is <b>gated</b> -
/// if the no-device baseline does not reproduce the record, the counterfactual/risk-curve/avoided-cost sections
/// are withheld and the report says so, rather than presenting an ungrounded claim.
/// </summary>
public sealed record EvidenceReport(
    string EventId,
    string EventTitle,
    DateTime WhenUtc,
    ProvenanceTag EventProvenance,
    ReplayVerdict BaselineVerdict,
    bool CounterfactualUnlocked,
    IReadOnlyList<EvidenceColumn> Columns,
    InertiaRiskCurve? RiskCurve,
    FleetComparison? Fleet,
    AvoidedCostResult? AvoidedCost,
    string Summary)
{
    // Phase 0.1 (source-generated JSON, so the core can run under NativeAOT): exactly the options this file has always
    // used, now bound to the source-generated EvidenceReportJsonContext (below) so no runtime reflection is needed.
    // Enums are still by name, through the source-generated per-enum JsonStringEnumConverter<TEnum> (UseStringEnumConverter
    // on the context) in place of the reflection-based JsonStringEnumConverter: both write the member name and both
    // still accept an integer on read.
    // Internal, not private, so JsonSourceGenEquivalenceTests reads and writes through this very instance.
    internal static readonly EvidenceReportJsonContext Json = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    });

    // Phase 0.1: the envelope is the named EvidenceDocument record (end of file) in place of the anonymous
    // { schema, report } object: same two members, same order, and camelCase still gives "schema" and "report".
    public string ToJson() => JsonSerializer.Serialize(new EvidenceDocument("gridsim-evidence/1", this), Json.EvidenceDocument);

    public void WriteJson(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, ToJson());
    }

    /// <summary>Assemble the dossier for an event + device, reproducing reality first and gating on it.</summary>
    public static EvidenceReport Build(EvidenceSpec spec, DynamicsConfig? config = null)
    {
        DynamicsConfig cfg = config ?? new DynamicsConfig();
        GridEvent ev = spec.Event;
        // Every column - baseline, counterfactual, risk curve, fleet - carries the event's RECORDED
        // response services; the device is evaluated on top of what the real system already held.
        cfg = ev.ApplyRecordedServices(cfg);
        // ...and every column must span the WHOLE recorded loss sequence. ReplayValidator extends
        // its own window internally, but the counterfactual and fleet sims below run on this cfg
        // directly; left at the 30 s default they replay a truncated event against a full-window
        // baseline, which can falsely unlock the avoided-cost claim (gb-2019-08-09 only sheds LFDD
        // via losses staged out to 90 s). Match the baseline's window here, once.
        IReadOnlyList<FrequencyEvent> lossEvents = ev.ExogenousLossEvents();
        cfg = ReplayValidator.ExtendToCover(cfg, lossEvents);   // the ONE post-event tail rule (R176)
        double lfddHz = FrequencyLimits.LfddFirstStageHz;

        // ---- credibility gate: recorded vs simulated (no device) ----
        ReplayResult baseline = ReplayValidator.Validate(ev, cfg);
        bool unlocked = baseline.Verdict == ReplayVerdict.Reproduced;
        // CF6 (main's ruling on N359 / CF6-F3, Ruling R1): a placeholder or synthetic event is a legitimate study input,
        // but nothing computed from it may carry more trust than it has. The event's effective tag is the weakest of its
        // parts; the dossier's event tag, the simulated column and the avoided cost inherit it (downgrade only). The
        // recorded column keeps the outcome's own tag; the counterfactual column stays Counterfactual (an output tag).
        ProvenanceTag effective = ReplayValidator.EffectiveProvenance(ev);

        var columns = new List<EvidenceColumn>
        {
            new("recorded", ev.Outcome.Provenance,
                ev.Outcome.InitialRocofHzPerS, ev.Outcome.NadirHz, ev.Outcome.SettleHz,
                ev.Outcome.NadirHz <= lfddHz, 0, 0),
            // D-DOC-030 (October 2026, docs-session finding): the RoCoF beside the recorded figure is the gate's own
            // same-basis figure (ReplayValidator.RocofOnRecordBasis), not the instantaneous peak. For an instantaneous
            // record the two are the same number, bit for bit; for gb-2023-12-22 (a 1 s windowed record) the column now
            // shows the -0.123 Hz/s the gate scored, not the -0.155 Hz/s instantaneous peak.
            // CF6: DerivedFromRecorded only when the event is (a recorded event's simulation is derived from the record);
            // a placeholder or synthetic event's simulation takes the event's weaker tag.
            new("simulated (no device)", ProvenanceTrust.Weakest(new[] { ProvenanceTag.DerivedFromRecorded, effective }, $"event '{ev.Id}' simulated column"),
                baseline.SimulatedRocofHzPerS, baseline.Simulated.NadirHz, baseline.Simulated.SettleHz,
                baseline.Simulated.NadirHz <= lfddHz, 0, 0),
        };

        InertiaRiskCurve? riskCurve = null;
        FleetComparison? fleet = null;
        AvoidedCostResult? avoided = null;

        if (unlocked)
        {
            // ---- counterfactual column: the device at the headline inertia level ----
            double eMwS = spec.CounterfactualInertiaGvaS * 1000.0;
            DynamicsConfig deviceCfg = spec.Device.Apply(cfg);
            FrequencyResponse cf = FrequencyDynamics.Simulate(eMwS, ev.PreEventDemandMw, ev.ExogenousLossEvents(), deviceCfg);
            bool cfLfdd = cf.NadirHz <= lfddHz;
            // D-DOC-030: on the record's measurement basis too, so the three RoCoF figures in the table are one quantity.
            columns.Add(new EvidenceColumn(
                $"counterfactual (device @ {spec.CounterfactualInertiaGvaS:F0} GVA.s)", ProvenanceTag.Counterfactual,
                ReplayValidator.RocofOnRecordBasis(ev.Outcome, cf), cf.NadirHz, cf.SettleHz, cfLfdd,
                spec.Device.RotationalInertiaMwS / 1000.0, spec.Device.FastResponseMw / 1000.0));

            // ---- inertia risk curve (P2) ----
            var scenarioSpec = new ScenarioSpec(ev,
                new SweepAxes(spec.InertiaAxisGvaS, new[] { DeviceConfig.None, spec.Device }, new[] { Corner.Best, Corner.Worst }),
                cfg, ReplayTolerance.Default);
            riskCurve = ScenarioEngine.Run(scenarioSpec).RiskCurve;

            // ---- single-vs-fleet (P3) at the headline inertia ----
            // Thread the caller's physics config through so an API caller with custom dynamics gets a fleet
            // section consistent with the counterfactual/risk-curve columns (which already use cfg). The device
            // is modelled via the FleetSpec - FleetDynamics zeroes cfg's device terms - so pass the base cfg,
            // not deviceCfg (which would add the device inertia/FFR a second time).
            // D-DOC-031 (October 2026, docs-session finding): the units carry the DEVICE's own response lag and dead-band.
            // FleetSpec.Uniform used to be called without them, so every fleet unit took Uniform's defaults (0.5 s, 0.015 Hz)
            // whatever the device said - data/devices/tsgb-fleet.json declares 0.05 s, so its fleet section modelled a unit
            // ten times slower than its counterfactual column. A device built from --device-inertia / --device-ffr carries
            // DeviceConfig's defaults, which equal Uniform's (0.5 s, 0.015 Hz), so that dossier is bit-identical.
            FleetResult agg = FleetDynamics.Simulate(eMwS, ev.PreEventDemandMw, ev.ExogenousLossEvents(),
                FleetSpec.Uniform(1, spec.Device.RotationalInertiaMwS, spec.Device.FastResponseMw,
                    ffrTauS: spec.Device.FastResponseTimeConstantS, deadbandHz: spec.Device.FastResponseDeadbandHz, maxStaggerS: 0), cfg);
            FleetResult flt = FleetDynamics.Simulate(eMwS, ev.PreEventDemandMw, ev.ExogenousLossEvents(),
                FleetSpec.Uniform(spec.FleetUnits, spec.Device.RotationalInertiaMwS, spec.Device.FastResponseMw,
                    ffrTauS: spec.Device.FastResponseTimeConstantS, deadbandHz: spec.Device.FastResponseDeadbandHz,
                    maxStaggerS: spec.FleetStaggerSeconds), cfg);
            fleet = new FleetComparison(
                spec.CounterfactualInertiaGvaS, spec.FleetUnits, spec.FleetStaggerSeconds,
                agg.Response.NadirHz, flt.Response.NadirHz, agg.FfrAtNadirMw, flt.FfrAtNadirMw);

            // ---- avoided cost: only if the device keeps the nadir above LFDD AND the record shed load ----
            if (!cfLfdd && ev.Outcome.LfddMwShed > 0)
            {
                avoided = Evidence.AvoidedCost.FromLfddAvoided(ev.Outcome.LfddMwShed, spec.AvoidedCost);
                // CF6: the shed MW come from the event, so the cost can be no more trusted than the event either.
                avoided = avoided with
                {
                    Provenance = ProvenanceTrust.Weakest(new[] { avoided.Provenance, effective }, $"event '{ev.Id}' avoided cost"),
                };
            }
        }

        // N237 (LF4): the baseline's nadir-timing statement travels with its verdict, so a dossier unlocked on the nadir
        // value alone says so (gb-2019-08-09: the simulated nadir is the first arrest at about 14 s, the recorded one the
        // LFDD dip at 75.9 s). No recorded nadir time, or times within tolerance: the text is exactly as before.
        // N317 (LF6, October 2026): with the recorded response modelled, the 2019 replay's nadir is the LFDD instant
        // again (76.0 s against the recorded 75.9 s), so its dossier carries no timing statement; the rule is unchanged.
        string baselineText = $"Baseline {baseline.Verdict}" + (baseline.NadirTimingNote is { } tn ? $" [{tn}]" : "");
        // D-DOC-135 (DOC-17): the avoided cost reads "GBP <n>" (plain ASCII) rather than the pound sign.
        string summary = unlocked
            ? $"{baselineText}; counterfactual device {(columns[^1].LfddAtRisk ? "does NOT" : "DOES")} keep the nadir above LFDD" +
              (avoided is { } ac ? FormattableString.Invariant($"; avoided cost GBP {ac.AvoidedCostGbp:N0} ({ac.Provenance})") : "; no avoided cost claimed")
            : $"{baselineText} - counterfactual withheld. Reproduce the record first ([VERIFY] the event figures/tolerances).";

        return new EvidenceReport(
            // CF6: the event's effective tag (the weakest of its parts), never stronger than what the dossier rests on.
            ev.Id, ev.Title, ev.WhenUtc, effective, baseline.Verdict, unlocked,
            columns, riskCurve, fleet, avoided, summary);
    }
}

/// <summary>
/// Phase 0.1: the evidence dossier's JSON envelope, <c>{ "schema": "gridsim-evidence/1", "report": { ... } }</c>. It
/// was an anonymous object; it is a named record so the source-generated context can describe it (NativeAOT). The
/// member names and order are those of the anonymous object (camelCase turns Schema into "schema", Report into
/// "report").
/// </summary>
internal sealed record EvidenceDocument(string Schema, EvidenceReport Report);

/// <summary>
/// Phase 0.1: the source-generated JSON metadata for the evidence dossier and every type it reaches, so it is read and
/// written without runtime reflection (NativeAOT). Metadata mode only: the bytes come from the same converters the
/// reflection path used, never from a separately generated fast path. Enums by name, as before.
/// The runtime options (naming, key case, null and number handling, indentation) are passed in by the call site.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, UseStringEnumConverter = true)]
[JsonSerializable(typeof(EvidenceDocument))]
internal sealed partial class EvidenceReportJsonContext : JsonSerializerContext;
