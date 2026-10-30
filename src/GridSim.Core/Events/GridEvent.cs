using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;

namespace GridSim.Core.Events;

/// <summary>A power step within an event: negative MW = a generation loss/deficit; positive = restoration.</summary>
public sealed record PowerStep(double AtSeconds, double DeltaPMw, string What, ProvenanceTag Provenance);

/// <summary>
/// D-DOC-034 (October 2026, docs-session finding): what a recorded action IS, which decides whether a counterfactual
/// replays it. <see cref="Unspecified"/> is the value a catalog entry gets when it does not say; the catalog loader
/// refuses it (fail loud), because guessing either way changes every counterfactual of the event.
/// </summary>
public enum RecordedActionKind
{
    /// <summary>
    /// Not stated. Refused by <see cref="EventCatalog.Validate"/> (so every catalogue file and every built-in must say),
    /// but an event built in code without a kind keeps the pre-DOC-5 meaning in <see cref="GridEvent.ExogenousLossEvents"/>:
    /// the action is left out of the counterfactual, as every action was before (API compatibility; the constitutional
    /// EvidenceReportTests build their LFDD this way).
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// Automatic low-frequency demand disconnection (LFDD): an ENDOGENOUS consequence of the frequency itself (relays
    /// operate because the frequency fell through their stage). A counterfactual never replays it - the device under
    /// test is meant to make it unnecessary, so feeding it back in would assume the answer.
    /// </summary>
    DemandDisconnection,

    /// <summary>
    /// An instruction from the system operator that replaces the lost energy whatever the nadir did - for example the
    /// balancing-mechanism recovery ramp after a loss (gb-2023-12-22, BOALF). The lost infeed has to be replaced with or
    /// without the device, so a counterfactual KEEPS it, exactly as it keeps the losses themselves.
    /// </summary>
    OperatorInstruction,
}

/// <summary>
/// A protective/operator action the record shows during the event - most importantly low-frequency demand
/// disconnection (LFDD). It enters the swing model as a <b>positive</b> deltaP (load removed = deficit reduced),
/// so feeding the RECORDED action in is replay, not prediction: we are told what happened and reproduce it.
/// <see cref="Kind"/> (D-DOC-034) says whether a counterfactual replays it too (see <see cref="RecordedActionKind"/>).
/// </summary>
public sealed record RecordedAction(
    double AtSeconds, double DeltaPMw, string What, ProvenanceTag Provenance,
    RecordedActionKind Kind = RecordedActionKind.Unspecified);

/// <summary>
/// A frequency-response service the system HELD at the event - volumes from the procurement record
/// (e.g. EAC auction cleared quantities for the delivery block), delivery shape from the published
/// service terms: DC ramps to 5% at 0.2 Hz deviation then to full at 0.5 Hz, within ~1 s; DM ramps
/// 0.1->0.2 Hz within ~1 s; DR ramps 0.015->0.2 Hz over ~10 s. The knee pair carries the low-gain
/// segment (DC: 0.2 Hz / 0.05); knee at the deadband with fraction 0 is a plain ramp-from-deadband
/// (DM/DR); knee <= 0 is a droop line through zero. It enters the swing model as a proportional
/// responder - replay of what the real system had, not prediction. Kept in BOTH the baseline replay
/// and counterfactuals: stripping it from a counterfactual would credit the device with response the
/// system already owned.
/// </summary>
public sealed record ResponseService(
    double Mw, double DeadbandHz, double FullDeliveryDeviationHz, double TimeConstantS,
    string What, ProvenanceTag Provenance,
    double KneeDeviationHz = 0.0, double KneeFraction = 0.0);

/// <summary>
/// N317 (October 2026, stream LF6; Ruling R1, Mark delegated EE calls 2026-10-04): the demand effect the record gives for
/// the event, as MW of demand reduction per Hz of frequency fall. Demand varies with frequency (synchronous motors draw
/// slightly less when the frequency is low), and a system operator can state how much for the system as it was: National
/// Grid ESO's 9 August 2019 report (docs/reference S192, Tables 3 and 4 footnote) gives "approximately 350MW at 49.5 Hz"
/// for the event's 29 GW demand, 700 MW/Hz. It is load damping (physics), so it is not a response service: it acts
/// instantly, on both sides of nominal, and is kept in counterfactuals like the losses are.
/// <para>When an event carries one, <see cref="GridEvent.ApplyRecordedServices"/> sets
/// <see cref="DynamicsConfig.DampingPctPerHz"/> so that the swing model's damping, DampingPctPerHz / 100 x
/// <see cref="GridEvent.PreEventDemandMw"/>, is this figure (MW per Hz). The record's MW per Hz is kept as MW per Hz rather
/// than as a percentage, because the percentage depends on which demand it is a percentage OF: the ESO figure is for the
/// gross national demand (29 GW), the event's PreEventDemandMw is transmission-net (21.1 GW). Every other consumer of the
/// damping (the declared-uncertainty corners, the exports the crossval sidecars read) sees the ordinary DampingPctPerHz
/// field, so they need no change.</para>
/// </summary>
/// <param name="MwPerHz">MW of demand reduction per Hz of frequency fall: a finite number above 0.</param>
/// <param name="What">Where the figure comes from (report, table, quotation).</param>
/// <param name="Provenance">How the figure was obtained.</param>
public sealed record RecordedDemandEffect(double MwPerHz, string What, ProvenanceTag Provenance);

/// <summary>What the record says actually happened - the headline metrics a replay must reproduce.</summary>
public sealed record RecordedOutcome(
    double InitialRocofHzPerS,   // recorded/derived RoCoF (Hz/s; magnitude compared, sign informational)
    double NadirHz,
    double? SettleHz,
    double LfddMwShed,           // total demand disconnected (MW); 0 if none
    string? NesoTraceCsvPath,    // optional 1-second NESO trace for per-sample RMSE (data-gated)
    ProvenanceTag Provenance,
    string? Notes = null,
    // The measurement basis of InitialRocofHzPerS: 0 (default) = an instantaneous/instrument
    // figure, compared against the sim's 20 ms-resolution maximum; > 0 = the recorded figure is
    // a windowed average over this many seconds (e.g. 1.0 for a max 1-second delta derived from
    // the public 1 Hz trace), and the sim is scored on the SAME window - a sharp single-step
    // loss reads very differently at 20 ms and 1 s, so bases must match for a fair verdict.
    double RocofMeasurementWindowS = 0.0,
    // N237 (October 2026, stream LF4): WHEN the recorded nadir happened, in seconds on the event's own clock (the clock
    // the losses' AtSeconds use; 0 = the initiating fault). Null = the record gives no time. Reporting only: the
    // credibility gate still scores the nadir VALUE, but when this is set and the simulated nadir falls more than
    // ReplayTolerance.NadirTimeToleranceS away from it, the replay and the evidence dossier say so in words
    // (ReplayResult.NadirTimingNote), so a verdict reached on the value alone is never presented as a reproduced
    // trajectory. Omitted from the catalog JSON when null.
    double? NadirSeconds = null);

/// <summary>
/// A real GB frequency event, described from the record: its pre-event system state, the loss(es) that
/// occurred, the protective actions the record shows, and the <see cref="RecordedOutcome"/> the engine must
/// reproduce <b>before any counterfactual is allowed</b> (the credibility gate). Every number not taken from a
/// primary source is tagged <see cref="ProvenanceTag.CuratedPlaceholder"/> and flagged <c>[VERIFY]</c> in Notes.
/// </summary>
public sealed record GridEvent(
    string Id,
    string Title,
    DateTime WhenUtc,
    double PreEventInertiaMwS,
    double PreEventDemandMw,
    IReadOnlyList<PowerStep> Losses,
    IReadOnlyList<RecordedAction> RecordedActions,
    RecordedOutcome Outcome,
    ProvenanceTag Provenance,
    string? Notes = null,
    // Response services the system held at the event (null/empty: the generic governor model stands in
    // for them, as for the screening event). Post-2021 GB events need these: by Dec 2023 the arrest is
    // dominated by sub-second battery Dynamic Containment, which an 8 s governor lag cannot represent.
    // N317 (LF6, October 2026): the 2019 event carries them too (it used to be the example of an event
    // the generic governor described; it was not: the governor arrested it at 48.82 Hz at 14 s, deeper and
    // earlier than the record's 49.1 Hz first arrest, see EventCatalog).
    IReadOnlyList<ResponseService>? ResponseServices = null,
    // True when ResponseServices enumerates ALL frequency response the system held, so the generic
    // governor term (DynamicsConfig.ResponsiveFraction, a conventional-plant-era assumption) must be
    // ZEROED - keeping it would double-count response the record already lists. True is correct for
    // GB events after EAC go-live (Nov 2023), when the DC/DM/DR daily auctions became the sole
    // frequency-response procurement route (legacy monthly FFR ended Oct 2023). Load damping is
    // physics, not procurement, and always stays. N317 (LF6): also true for 9 August 2019, whose
    // ESO tables list the whole low-frequency response held in the settlement period.
    bool ResponseServicesComplete = false,
    // The recorded frequency the trip landed on (Hz; null = nominal). Services respond to ABSOLUTE
    // frequency, so an offset start changes both the pre-deployed response (headroom consumed) and
    // how deep into the delivery curves the first seconds run. The sim starts in equilibrium at this
    // value (states pre-loaded, standing imbalance balanced) - see DynamicsConfig.InitialFrequencyHz.
    double? PreEventFrequencyHz = null,
    // N317 (LF6, October 2026): the demand effect the record gives (MW per Hz), which replaces the
    // config's generic load damping for this event (see RecordedDemandEffect). Null = the config's
    // DampingPctPerHz is kept, as before. Omitted from the catalog JSON when null.
    RecordedDemandEffect? DemandEffect = null)
{
    /// <summary>
    /// N317: why <see cref="DemandEffect"/> cannot be applied, or null when it can (or the event has none). It must be a
    /// finite number of MW per Hz above 0, and the event's <see cref="PreEventDemandMw"/> a finite number above 0 (the
    /// damping is carried as a percentage of that demand, so a zero or non-finite demand cannot carry it). The catalog
    /// loader and <see cref="ApplyRecordedServices"/> both refuse the event with this text.
    /// </summary>
    public string? DemandEffectProblem()
    {
        if (DemandEffect is not { } de) return null;
        if (!double.IsFinite(de.MwPerHz) || !(de.MwPerHz > 0.0))
            return $"Event '{Id}': demandEffect.mwPerHz = {de.MwPerHz:R} must be a finite number of MW per Hz above 0.";
        if (!double.IsFinite(PreEventDemandMw) || !(PreEventDemandMw > 0.0))
            return $"Event '{Id}': a demandEffect needs a preEventDemandMw that is a finite number of MW above 0 (got {PreEventDemandMw:R}), " +
                   "because the swing model carries the damping as a percentage of that demand.";
        return null;
    }

    /// <summary>Total generation lost at/after t=0 over the WHOLE staged sequence (MW, magnitude). R287 (B4, October
    /// 2026): this used to say "the deltaP the RoCoF is set by", which it is not for a staged event (gb-2019-08-09
    /// loses 2,768 MW over 90 s but only 1,521 MW in the first 5 s); the RoCoF-setting loss is
    /// <see cref="RocofSettingLossMw"/>.</summary>
    public double TotalLossMw => Losses.Sum(l => Math.Abs(l.DeltaPMw));

    /// <summary>
    /// R287 (B4, October 2026): the window (seconds of event time, from t = 0) whose losses set the initial RoCoF. The
    /// ONE rule, used by the replay's inertia cross-check (<c>ReplayValidator</c>, where it was an inline 5.0 s
    /// constant) and by the H=0 study's analytic RoCoF. 5 s covers the near-instant losses and the fast Loss-of-Mains
    /// cascades they set off (9 August 2019: the 0.1 / 0.5 / 1.0 / 3.0 s steps, Ofgem report paras 2.4.2-2.4.6), and
    /// leaves out the steps tens of seconds later (the 58 s and 90 s gas-turbine trips) that cannot shape the first
    /// slope.
    /// </summary>
    public const double RocofSettingWindowSeconds = 5.0;

    /// <summary>
    /// R287: the loss that sets the initial RoCoF (MW, magnitude): every loss step at or before
    /// <see cref="RocofSettingWindowSeconds"/>, summed in list order; when no loss falls in the window, the whole
    /// <see cref="TotalLossMw"/> (so an event whose first loss is late still has a deltaP). A method, not a property,
    /// so the catalog writer never serialises it. Exactly the expression ReplayValidator used inline before.
    /// </summary>
    public double RocofSettingLossMw()
    {
        double inWindow = Losses
            .Where(l => l.AtSeconds <= RocofSettingWindowSeconds)
            .Sum(l => Math.Abs(l.DeltaPMw));
        return inWindow <= 0 ? TotalLossMw : inWindow;
    }

    /// <summary>
    /// R287: the SIGNED loss that sets the initial RoCoF (MW; negative = deficit), for the swing equation
    /// f0 dP / (2 E): the signed sum of the loss steps at or before <see cref="RocofSettingWindowSeconds"/>, in time
    /// order (<see cref="LossEvents"/>); when no loss falls in the window, the signed sum of every loss.
    /// </summary>
    public double RocofSettingSignedLossMw()
    {
        IReadOnlyList<FrequencyEvent> losses = LossEvents();
        double inWindow = losses.Where(l => l.AtSeconds <= RocofSettingWindowSeconds).Sum(l => l.DeltaPMw);
        bool any = losses.Any(l => l.AtSeconds <= RocofSettingWindowSeconds && l.DeltaPMw != 0.0);
        return any ? inWindow : losses.Sum(l => l.DeltaPMw);
    }

    /// <summary>R287: the largest SINGLE loss step of the event (MW, magnitude); 0 when the event has no loss.</summary>
    public double LargestSingleLossMw() => Losses.Count == 0 ? 0.0 : Losses.Max(l => Math.Abs(l.DeltaPMw));

    /// <summary>
    /// Losses + recorded actions as swing-equation <see cref="FrequencyEvent"/>s, ordered in time. This is the
    /// replay input: a recorded loss (-deltaP) and a recorded LFDD shed (+deltaP) are both facts we reproduce.
    /// </summary>
    public IReadOnlyList<FrequencyEvent> ToFrequencyEvents()
    {
        var list = new List<FrequencyEvent>(Losses.Count + RecordedActions.Count);
        foreach (PowerStep l in Losses) list.Add(new FrequencyEvent(l.AtSeconds, l.DeltaPMw));
        foreach (RecordedAction a in RecordedActions) list.Add(new FrequencyEvent(a.AtSeconds, a.DeltaPMw));
        // GR-842: the rule at equal times is losses first, then actions, each in catalogue order. It rests on OrderBy
        // being a stable sort (a documented LINQ guarantee); GridEventOrderingTests pins it, so an unstable sort here
        // fails a test instead of moving a replay's nadir.
        return list.OrderBy(e => e.AtSeconds).ToList();
    }

    /// <summary>
    /// The EXOGENOUS steps: every loss plus every recorded action that does not depend on the frequency. Counterfactuals
    /// inject these plus a device - the device is meant to <b>replace</b> the recorded LFDD, so feeding the LFDD back in
    /// would be wrong (it is an endogenous consequence, not a cause). The baseline replay uses
    /// <see cref="ToFrequencyEvents"/>; a counterfactual uses this.
    ///
    /// <para>D-DOC-034 (October 2026, docs-session finding): this used to drop EVERY recorded action, not only the LFDD.
    /// gb-2023-12-22's recorded actions are the balancing-mechanism recovery ramp (+904 MW by +129 s, BOALF), which
    /// replaces the lost infeed whatever the device does, so dropping it made every counterfactual of that event replay a
    /// system that never recovered. Now only <see cref="RecordedActionKind.DemandDisconnection"/> actions are dropped;
    /// <see cref="RecordedActionKind.OperatorInstruction"/> actions are kept; an <see cref="RecordedActionKind.Unspecified"/>
    /// action (only possible from code: the catalogue loader refuses it) is left out, the pre-DOC-5 behaviour. For an event
    /// whose only actions are LFDD (gb-2019-08-09, the screening event) the list is the same elements in the same order as
    /// before, so every number is bit-identical.</para>
    /// </summary>
    public IReadOnlyList<FrequencyEvent> ExogenousLossEvents()
    {
        var list = new List<FrequencyEvent>(Losses.Count + RecordedActions.Count);
        foreach (PowerStep l in Losses) list.Add(new FrequencyEvent(l.AtSeconds, l.DeltaPMw));
        foreach (RecordedAction a in RecordedActions)
        {
            // DemandDisconnection (endogenous: the counterfactual decides for itself whether demand is disconnected) and
            // Unspecified (code-built, the old behaviour) are left out; an operator instruction is kept.
            if (a.Kind == RecordedActionKind.OperatorInstruction)
                list.Add(new FrequencyEvent(a.AtSeconds, a.DeltaPMw));
        }
        // Losses first, then actions, then a stable sort by time: the same order ToFrequencyEvents gives.
        return list.OrderBy(e => e.AtSeconds).ToList();
    }

    /// <summary>
    /// The recorded losses alone, as swing-equation steps ordered in time (no recorded action of any kind): what
    /// <see cref="ExogenousLossEvents"/> returned before D-DOC-034. For a figure that is about the LOSS itself - the
    /// H=0 study's dP and its bare-inertia RoCoF - where the operator's later recovery must not be netted off.
    /// </summary>
    public IReadOnlyList<FrequencyEvent> LossEvents() =>
        Losses.Select(l => new FrequencyEvent(l.AtSeconds, l.DeltaPMw)).OrderBy(e => e.AtSeconds).ToList();

    /// <summary>
    /// Stamps this event's recorded operating state - response services, pre-event frequency and (N317) the
    /// recorded demand effect - into a dynamics config: the single place the Events -> Dynamics mapping happens,
    /// so the replay baseline, the scenario sweep, the fleet comparison and the evidence dossier all integrate the
    /// SAME recorded system. Idempotent (overwrites); an event with none of them returns the config with an
    /// empty list, a nominal start and its own damping. A demand effect that cannot be applied
    /// (<see cref="DemandEffectProblem"/>) throws <see cref="InvalidDataException"/>.
    /// </summary>
    public DynamicsConfig ApplyRecordedServices(DynamicsConfig cfg)
    {
        DynamicsConfig applied = cfg with
        {
            SystemResponseServices = (ResponseServices ?? [])
                .Select(s => new SystemResponseService(
                    s.Mw, s.DeadbandHz, s.FullDeliveryDeviationHz, s.TimeConstantS,
                    s.KneeDeviationHz, s.KneeFraction))
                .ToList(),
            InitialFrequencyHz = PreEventFrequencyHz,
        };
        // A complete service list IS the system's frequency response - the generic governor term
        // would double-count it (see the ResponseServicesComplete parameter note).
        if (ResponseServicesComplete) applied = applied with { ResponsiveFraction = 0.0 };
        // N317 (LF6, October 2026): the recorded demand effect replaces the generic load damping. The swing kernel computes
        // the damping as DampingPctPerHz / 100 x demand (SwingResponders), and every engine of an event runs at the event's
        // PreEventDemandMw, so DampingPctPerHz = MwPerHz / PreEventDemandMw x 100 gives back the recorded MW per Hz
        // (gb-2019-08-09: 700 / 21,100 x 100 = 3.3175355450236967 %/Hz, and 3.3175355450236967 / 100 x 21,100 = 700.0
        // exactly in IEEE double). An event without one keeps the config's damping, so nothing else moves.
        if (DemandEffect is { } de)
        {
            if (DemandEffectProblem() is { } problem) throw new InvalidDataException(problem);
            applied = applied with { DampingPctPerHz = de.MwPerHz / PreEventDemandMw * 100.0 };
        }
        return applied;
    }
}
