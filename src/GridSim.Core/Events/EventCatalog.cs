using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Diagnostics;

namespace GridSim.Core.Events;

/// <summary>
/// Loads/saves a catalog of <see cref="GridEvent"/>s (schema <c>gridsim-event-catalog/1</c>) under
/// <c>data/events/</c>. <b>Every dated field passes through <see cref="ForwardInferenceGuard"/> on load</b>, so a
/// catalog can never smuggle a future "event" into the pipeline. Ships three built-ins (R095: this doc said two):
/// <c>gb-2019-08-09</c> (sourced from the Ofgem investigation report; the measured operating point additionally ships
/// as <c>data/events/gb-2019-08-09-estimated.json</c>), <c>gb-2023-12-22</c>, and <c>gb-largest-loss-1320</c>, a
/// <see cref="ProvenanceTag.CuratedPlaceholder"/> screening event whose unsourced numbers stay tagged and flagged
/// <c>[VERIFY]</c>.
/// </summary>
public static class EventCatalog
{
    /// <summary>
    /// N135 (B4, October 2026): the title of the built-in <c>gb-largest-loss-1320</c> screening event. 1320 MW is the
    /// NETS SQSS v2.10 Normal Infeed Loss Risk (docs/reference S036, chapter 11), NOT the largest credible loss (that is
    /// the Infrequent Infeed Loss Risk, 1800 MW). The old title called it the GB largest credible loss
    /// screening event (a single 1320 MW infeed trip). The id keeps its historical name so no script, capture or citation breaks.
    /// </summary>
    public const string NormalInfeedLossScreeningTitle =
        "GB normal infeed loss risk screening (SQSS): single 1320 MW infeed trip [VERIFY]";

    // Phase 0.1 (source-generated JSON, so the core can run under NativeAOT): exactly the options this file has always
    // used. Enums are still by name, now through the source-generated per-enum JsonStringEnumConverter<TEnum>
    // (UseStringEnumConverter on EventCatalogJsonContext) in place of the reflection-based JsonStringEnumConverter;
    // both write the member name and both still accept an integer on read.
    // Internal, not private, so JsonSourceGenEquivalenceTests reads and writes through this very instance.
    internal static readonly EventCatalogJsonContext Json = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    });

    // Internal rather than private (Phase 0.1) only so the source-generated context, a separate class, can reach it.
    internal sealed record CatalogDoc(string Schema, IReadOnlyList<GridEvent> Events);

    /// <summary>The one schema id this loader reads, and the one <see cref="Save"/> writes.</summary>
    public const string SchemaId = "gridsim-event-catalog/1";

    /// <summary>Load a catalog file, enforcing the historical horizon on every event timestamp.</summary>
    public static IReadOnlyList<GridEvent> Load(string path, DateTime? utcNow = null)
    {
        CatalogDoc? doc = JsonSerializer.Deserialize(File.ReadAllText(path), Json.CatalogDoc)
            ?? throw new InvalidDataException($"Not a GridSim event catalog: {path}");
        // System.Text.Json binds a positional record's missing parameters to null, so any non-catalog
        // JSON (e.g. {} or an unrelated file) deserializes to a non-null doc with null Schema/Events.
        // Validate the shape and fail with the designed InvalidDataException, not a later NullReference.
        if (doc.Schema is null || doc.Events is null)
            throw new InvalidDataException($"Not a GridSim event catalog (missing schema/events): {path}");
        // D-DOC-036 (October 2026, docs-session finding): the schema id was checked for presence only, so a file
        // declaring any other schema (another GridSim format, or a future gridsim-event-catalog/2 with different
        // meanings) was read as version 1. The id must be exactly SchemaId (ordinal, case-sensitive, as every
        // GridSim schema id is compared).
        if (!string.Equals(doc.Schema, SchemaId, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Not a GridSim event catalog: {path} declares schema '{doc.Schema}', but this loader reads '{SchemaId}' only.");
        return Validate(doc.Events, utcNow);
    }

    /// <summary>Find one event by id across a catalog file (falls back to the built-ins).</summary>
    public static GridEvent Get(string id, string? catalogPath = null, DateTime? utcNow = null)
    {
        IReadOnlyList<GridEvent> events = catalogPath is not null ? Load(catalogPath, utcNow) : Builtin(utcNow);
        return events.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException(
                $"Event '{id}' not found. Available: {string.Join(", ", events.Select(e => e.Id))}");
    }

    public static void Save(string path, IReadOnlyList<GridEvent> events)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new CatalogDoc(SchemaId, events), Json.CatalogDoc));
    }

    /// <summary>
    /// Enforce the no-forward-inference horizon on every event's timestamp, (N317) that a recorded demand effect is
    /// usable (<see cref="GridEvent.DemandEffectProblem"/>), and (D-DOC-034) that every recorded action
    /// says what kind it is: a <see cref="RecordedActionKind.Unspecified"/> action is refused with an
    /// <see cref="InvalidDataException"/> naming the event and the action, because whether a counterfactual replays it
    /// cannot be guessed (see <see cref="GridEvent.ExogenousLossEvents"/>).
    /// </summary>
    public static IReadOnlyList<GridEvent> Validate(IReadOnlyList<GridEvent> events, DateTime? utcNow = null)
    {
        // Ids are matched without regard to case (Get), so two ids that differ only in case are one id: the second
        // could never be selected.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (GridEvent e in events)
        {
            if (string.IsNullOrWhiteSpace(e.Id))
                throw new InvalidDataException("An event in the catalog has no id.");
            if (!seen.Add(e.Id))
                throw new InvalidDataException($"Event id '{e.Id}' appears more than once in the catalog (ids are compared without regard to case).");
            ForwardInferenceGuard.RejectFuture(e.WhenUtc, utcNow, $"event '{e.Id}'");
            // The numbers a replay is built from: a NaN loss time sorted first and was applied at t = 0 (or never), a
            // null Losses list failed later on a null, and zero or negative system inertia or demand loaded silently.
            if (e.Losses is null)
                throw new InvalidDataException($"Event '{e.Id}' has no losses list.");
            if (e.Outcome is null)
                throw new InvalidDataException($"Event '{e.Id}' has no recorded outcome.");
            if (!(double.IsFinite(e.PreEventInertiaMwS) && e.PreEventInertiaMwS > 0))
                throw new InvalidDataException($"Event '{e.Id}': preEventInertiaMwS {e.PreEventInertiaMwS} must be a finite number above 0.");
            if (!(double.IsFinite(e.PreEventDemandMw) && e.PreEventDemandMw > 0))
                throw new InvalidDataException($"Event '{e.Id}': preEventDemandMw {e.PreEventDemandMw} must be a finite number above 0.");
            foreach (PowerStep l in e.Losses)
                if (!(double.IsFinite(l.AtSeconds) && l.AtSeconds >= 0) || !double.IsFinite(l.DeltaPMw))
                    throw new InvalidDataException($"Event '{e.Id}': the loss '{l.What}' at {l.AtSeconds} s of {l.DeltaPMw} MW needs a finite time of 0 s or later and a finite MW.");
            // A catalog that omits the list is a malformed event, not "no actions": refuse it by name rather than
            // fail later on a null.
            if (e.RecordedActions is null)
                throw new InvalidDataException($"Event '{e.Id}' has no recordedActions list (write [] when the record shows none).");
            // N317 (LF6, October 2026): a recorded demand effect that cannot be applied is refused at load, naming the
            // event, rather than when a replay first runs.
            if (e.DemandEffectProblem() is { } demandEffectProblem)
                throw new InvalidDataException(demandEffectProblem);
            foreach (RecordedAction a in e.RecordedActions)
                if (!(double.IsFinite(a.AtSeconds) && a.AtSeconds >= 0) || !double.IsFinite(a.DeltaPMw))
                    throw new InvalidDataException($"Event '{e.Id}': the recorded action '{a.What}' at {a.AtSeconds} s of {a.DeltaPMw} MW needs a finite time of 0 s or later and a finite MW.");
                else if (a.Kind == RecordedActionKind.Unspecified)
                    throw new InvalidDataException(
                        $"Event '{e.Id}': the recorded action at {a.AtSeconds} s ('{a.What}') has no kind. Set \"kind\" to " +
                        $"\"{nameof(RecordedActionKind.DemandDisconnection)}\" (automatic LFDD: never replayed in a counterfactual) " +
                        $"or \"{nameof(RecordedActionKind.OperatorInstruction)}\" (replaces lost energy whatever the nadir: kept " +
                        "in a counterfactual).");
        }
        return events;
    }

    /// <summary>
    /// The built-in events: <c>gb-2019-08-09</c> sourced from the Ofgem investigation report (loss sequence,
    /// LFDD, outcome), and the <c>gb-largest-loss-1320</c> screening case whose figures remain
    /// curated-placeholder ([VERIFY]) until sourced. The 1-second NESO trace path stays data-gated.
    /// </summary>
    public static IReadOnlyList<GridEvent> Builtin(DateTime? utcNow = null)
    {
        var events = new List<GridEvent>
        {
            // ---- 9 August 2019 GB disturbance (Hornsea + Little Barford) ----
            // Loss sequence and outcome sourced from the Ofgem investigation report "9 August 2019 power
            // outage report" (Jan 2020), paras 2.4.1-2.4.16. The operating point here keeps round curated
            // figures; the MEASURED operating point (NESO recorded outturn inertia 215 GVA.s + the WLS
            // estimator's transmission-net demand 21.1 GW) ships in data/events/gb-2019-08-09-estimated.json
            // (built by GDA NetworkModel/build_event_catalog.py) and reproduces the record: use --catalog.
            new(
                Id: "gb-2019-08-09",
                Title: "GB 9 Aug 2019 disturbance (Hornsea + Little Barford)",
                WhenUtc: new DateTime(2019, 8, 9, 15, 52, 33, DateTimeKind.Utc), // Ofgem 2.4.1 (16:52:33 BST)
                PreEventInertiaMwS: 215_000,   // NESO recorded outturn for the half-hour (GDA inertia_all_srcs)
                PreEventDemandMw: 21_100,      // WLS-estimated transmission-net demand for the period (GDA estimated_state)
                Losses: new List<PowerStep>
                {
                    new(0.1, -150, "distributed generation vector-shift trip (Ofgem 2.4.2)", ProvenanceTag.Recorded),
                    new(0.5, -737, "Hornsea 1 deload 799->62 MW (Ofgem 2.4.3)", ProvenanceTag.Recorded),
                    new(1.0, -244, "Little Barford steam turbine trip (Ofgem 2.4.4)", ProvenanceTag.Recorded),
                    // Moved 2026-10 (LF6 N317, Ruling R1 (Mark delegated EE calls 2026-10-04); source National Grid ESO
                    // interim LFDD report of 16 August 2019, docs/reference S192, Table 1 "Timeline of Events", PDF p.11;
                    // ESO technical report appendices S191 Appendix I Table 1, PDF p.62): at 3.0 s -> 1.0 s. Both ESO tables
                    // put this loss at 16:52:34, the SAME second as the Little Barford steam-turbine trip above (the "~500MW
                    // increase of transformer loadings ... [1481MW of cumulative infeed loss]" row; Ofgem 2.4.5-2.4.7: about
                    // 1,130 MW within about 1 s of the fault, then the RoCoF trips, about 1,500 MW cumulative). 1.0 s is also
                    // the first instant the model's RoCoF exceeds the 0.125 Hz/s legacy setting (Hornsea + vector shift alone
                    // give 50 x 887 / (2 x 215,000) = 0.103 Hz/s; with Little Barford 0.132 Hz/s). The 3.0 s had no cited
                    // source (Ofgem 2.4.6 gives no time).
                    // It matters once the recorded response is modelled: at 3.0 s the fast services and the demand effect
                    // were already acting, and the peak RoCoF fell to -0.138 Hz/s against the recorded -0.17. Same MW, same
                    // list position, so RocofSettingLossMw (1,521 MW in the 5 s window) and TotalLossMw are bit-identical.
                    new(1.0, -390, "distributed generation RoCoF-protection trip, 350-430 MW est. (Ofgem 2.4.6; ESO timeline 16:52:34)", ProvenanceTag.DerivedFromRecorded),
                    new(40.0, -100, "unattributed net reduction over 30 s (Ofgem 2.4.9)", ProvenanceTag.Recorded),
                    new(58.0, -210, "Little Barford GT1a steam-pressure trip (Ofgem 2.4.10)", ProvenanceTag.Recorded),
                    new(65.0, -200, "distributed generation tripped at 49 Hz (Ofgem 2.4.11)", ProvenanceTag.Recorded),
                    new(76.0, -550, "additional distributed generation lost at LFDD instant (Ofgem 2.4.13)", ProvenanceTag.DerivedFromRecorded),
                    new(90.0, -187, "Little Barford GT1b manual trip (Ofgem 2.4.15)", ProvenanceTag.Recorded),
                },
                RecordedActions: new List<RecordedAction>
                {
                    new(76.0, +892, "LFDD stage 1: 892 MW net demand disconnected, 1.15 M customers (Ofgem 2.4.12)", ProvenanceTag.Recorded,
                        RecordedActionKind.DemandDisconnection),
                },
                Outcome: new RecordedOutcome(
                    InitialRocofHzPerS: -0.17,  // E3C interim trace; Ofgem 2.4.6 records only ">0.125 Hz/s"
                    NadirHz: 48.8,              // LFDD trigger floor reached at ~76 s (Ofgem 2.4.12)
                    SettleHz: 50.0,             // within 5 minutes (Ofgem 2.4.16)
                    LfddMwShed: 892,            // net demand disconnected (Ofgem 2.4.12)
                    NesoTraceCsvPath: null,     // data-gated: drop the real 1-sec trace here to enable RMSE
                    Provenance: ProvenanceTag.Recorded,
                    Notes: "Arrest 49.1 Hz at 25 s, plateau 49.2 Hz at 45 s, below 48.8 Hz at ~76 s -> LFDD, 50 Hz within 5 min (Ofgem 2.4.8-2.4.16).",
                    // N237 (LF4): the recorded nadir is the LFDD instant, 16:53:49.398, 75.9 s after the fault at
                    // 16:52:33.490 (National Grid ESO, "Interim Report into the Low Frequency Demand Disconnection (LFDD)
                    // following Generator Trips and Frequency Excursion on 9 Aug 2019", 16 August 2019, Table 1 "Timeline of
                    // Events", docs/reference S192; Ofgem 2.4.12 gives no time). The first arrest (49.1 Hz) came earlier:
                    // 16:52:58, 24.5 s, in the same table (Ofgem 2.4.8: 25 s; the ESO report's "Response and Reserve"
                    // text says "approximately 15 seconds").
                    NadirSeconds: 75.9),
                Provenance: ProvenanceTag.Recorded,
                Notes: "Credibility-gate target event, sourced from the Ofgem investigation report. The curated " +
                       "operating point (215 GVA*s / 21.1 GW) mirrors the measured catalog data/events/gb-2019-08-09-estimated.json.",
                // ---- N317 (October 2026, stream LF6; Ruling R1, Mark delegated EE calls 2026-10-04): the response the
                // system HAD, from the record, instead of the generic governor (20 % of demand responsive, 5 % droop,
                // 8 s lag) and 1 %/Hz load damping. Before, the replay arrested the fall at 48.82 Hz at 14.06 s, deeper and
                // earlier than the real first arrest (49.1 Hz at about 15-25 s), and "matched" the recorded 48.8 Hz nadir
                // there instead of at the LFDD instant (75.9 s) where it happened (N237). With what follows the replay
                // nadir is the LFDD instant again (see LF6 notes for every moved number).
                //
                // Volumes: what each service DELIVERED, National Grid ESO interim report of 16 August 2019 (docs/reference
                // S192), Table 3 "Operational Response Performance Data for SP34 09/08/2019", PDF p.15, column "Low
                // Frequency Delivered": 266 + 231 + 165 + 198 + 200 + 30 = 1,090 MW. The holdings beside them (same table
                // and Table 4, PDF p.18; final technical report S189 Table 3, PDF p.21) were 1,022 MW primary
                // (284 + 280 + 227 + 200 + 31) and the static 49.7 Hz secondary [285]. A replay reproduces what happened, so
                // it takes the delivered figure, the only per-service MW of delivery the record gives (S189 Table 5,
                // PDF p.25, gives validated percentages of the holdings instead: 89 % primary, 88 % secondary). The ESO's
                // own timeline agrees in size: "at least 650MW" delivered by 16:52:44 and "900MW" by 16:53:04 (S192
                // Table 1, PDF p.11).
                //
                // Delivery shapes, from the service terms:
                //   * dynamic (mandatory BM response, FFR, EFR): a droop line reaching full output at a 0.5 Hz fall (FFR
                //     FAQs v1.3, S236 PDF p.9: "All response levels are calculated for a 0.5Hz deviation"), outside a
                //     +/-0.015 Hz deadband (S236 PDF p.4; Grid Code CC.6.3.7(c)(iii), quoted in S211 PDF p.5);
                //   * static: full output once the frequency crosses its trigger, 49.6 Hz "delivered within 1 second" or
                //     49.7 Hz "delivered within 30 seconds" (S192 Tables 3 and 4). Written as deadband = full-delivery
                //     deviation = 50 - trigger with no knee, which the kernel's curve turns into a step at the trigger.
                // Lags: the kernel's first-order lag with the SAME MEAN DELAY as a linear release over the documented time,
                // T = (time to full) / 2. Primary response is "released increasingly with time over the period 0 to 10
                // seconds" (Grid Code definition, S211 PDF p.6), so T = 5 s; secondary is "fully available by 30
                // seconds" (same page), T = 15 s; the 49.6 Hz static services deliver "within 1 second", T = 0.5 s; EFR
                // must "achieve full output in one second" (National Grid EFCC report Jan-Jun 2016, S213 PDF p.9), T = 0.5 s.
                // The dynamic BM and NBM figures are primary and secondary together (Table 3 "Primary [Secondary]"); they
                // take the primary lag, the faster of the two, because most of their delivery is in the first 10 s (650 MW
                // of the 1,090 MW by 10.5 s, Table 1).
                ResponseServices: new List<ResponseService>
                {
                    new(266, 0.015, 0.5, 5.0, "Dynamic primary/secondary, BM (mandatory response): 266 MW delivered of 284 [325] held (ESO S192 Table 3)", ProvenanceTag.Recorded),
                    new(231, 0.015, 0.5, 5.0, "Dynamic primary/secondary, NBM (FFR): 231 MW delivered of 280 [270] held (ESO S192 Table 3)", ProvenanceTag.Recorded),
                    new(165, 0.015, 0.5, 0.5, "Enhanced Frequency Response, NBM: 165 MW delivered of 227 held (ESO S192 Table 3)", ProvenanceTag.Recorded),
                    new(198, 0.3, 0.3, 15.0, "Static, triggered at 49.7 Hz within 30 s, NBM secondary: 198 MW delivered of [285] held (ESO S192 Table 3)", ProvenanceTag.Recorded),
                    new(200, 0.4, 0.4, 0.5, "Static, triggered at 49.6 Hz within 1 s, BM: 200 MW delivered of 200 held (ESO S192 Table 3)", ProvenanceTag.Recorded),
                    new(30, 0.4, 0.4, 0.5, "Static, triggered at 49.6 Hz within 1 s, NBM: 30 MW delivered of 31 held (ESO S192 Table 3)", ProvenanceTag.Recorded),
                },
                // The ESO's tables are the whole low-frequency response the system held in SP34 (S189 Table 3, "Lower
                // Frequency response held"; the mandatory BM response IS the governor response that was held, and plant
                // not instructed into frequency-sensitive mode does not respond to a fall). So the generic governor term
                // is zeroed, exactly as for gb-2023-12-22: keeping it would add about 4 GW of response nobody held.
                ResponseServicesComplete: true,
                // The demand effect, ESO S192 Tables 3 and 4 footnote (PDF p.15 and p.18): "For 29 GW demand, this effect
                // is approximately 350MW at 49.5 Hz", i.e. 700 MW per Hz. 29 GW is the event's own national demand (ESO
                // technical report S189 section 5.1, PDF p.26: "national transmission demand at the time of the event
                // (29GW)"), so the figure is the event's, in MW per Hz, and is carried as such: on this event's
                // 21,100 MW transmission-net demand (embedded generation netted off; the demand effect belongs to the gross
                // demand) it is 3.3175... %/Hz, not the generic 1 %/Hz (211 MW/Hz).
                DemandEffect: new RecordedDemandEffect(700.0,
                    "Demand effect: about 350 MW at 49.5 Hz for the event's 29 GW demand (ESO S192 Tables 3 and 4 footnote)",
                    ProvenanceTag.Recorded)),

            // ---- 22 December 2023 GB excursion (IFA + Cottam, settlement-measured) ----
            // The ENTIRE loss inventory is measured from settlement metering - no secondary
            // narrative is cited (an earlier draft cited "the FRCR 2025 independent review"; that
            // document does not in fact discuss this event - always verify a citation against the
            // document itself). Outcome figures are read off NESO's public 1-second trace
            // (data/neso/gb-2023-12-22-frequency.csv): first fall onset 13:09:51.x, second at
            // ~13:10:00, minimum 49.275 Hz at 13:10:11, below 49.5 Hz for ~59 s.
            //   * IFA (Sellindge landing) -1,000 MW: metered flow 1,988 -> 999 MW across the trip,
            //     and the event period's 1,322 MW average is exactly the time-weighted value of a
            //     1,000 MW step ~10 min into the half-hour (GDA Derived/gsp_ic_flows).
            //   * Cottam CCGT (T_CDCL-1) -345 MW: B1610 metered 172.5 MWh/hh (345 MW) -> 55.7 -> 0;
            //     55.7/172.5 = 32% of the period delivered = trip at ~13:09:4x (raw B1610 quantity
            //     is MWh per half-hour - multiply by 2 for MW).
            // Pre-event context, also settlement-measured: North Sea Link (Blyth landing) ran back
            // ~292 MW at ~13:02:50 (time-weighted), and a ~213 MW scheduled flow swing landed
            // exactly on the 13:00 boundary (KINO) - together they explain why the trip landed on a
            // system already sagged to 49.879 Hz with the BM carrying a ~200 MW residual, which is
            // what PreEventFrequencyHz + the equilibrium start represent. Operating point measured:
            // NESO recorded outturn inertia 161 GVA*s; WLS-estimated transmission-net demand
            // 22,400 MW (Derived/estimated_state, 48/48 chi-square on the day).
            new(
                Id: "gb-2023-12-22",
                Title: "GB 22 Dec 2023 excursion (IFA + Cottam, settlement-measured)",
                WhenUtc: new DateTime(2023, 12, 22, 13, 9, 51, DateTimeKind.Utc), // first-fall onset, NESO 1-s trace
                PreEventInertiaMwS: 161_000,   // NESO recorded outturn for the half-hour (GDA inertia_all_srcs)
                PreEventDemandMw: 22_400,      // WLS-estimated transmission-net demand for the period
                Losses: new List<PowerStep>
                {
                    new(0.5, -1000, "IFA trip (settlement-measured: Sellindge 1,988->999 MW, time-weighting exact)", ProvenanceTag.DerivedFromRecorded),
                    new(9.5, -345, "Cottam CCGT trip (settlement-measured: T_CDCL-1 345 MW->0, 32% of period delivered; assigned to the trace's second fall)", ProvenanceTag.DerivedFromRecorded),
                },
                // The balancing mechanism's recovery, straight from the BOALF record (Elexon
                // bid-offer acceptance level segments, GDA DataSources/Elexon/boalf): the net
                // instructed level across all BM units is FLAT before the trip, starts ramping at
                // 13:10:10 (+19 s, right at the nadir) at ~150 MW/min, accelerates to ~750 MW/min at
                // +79 s, and lands +904 MW by +129 s - matching the trace's recovery through 49.5 Hz
                // at +59 s. Steps below are the 10-second increments of that interpolated profile
                // (instructed = delivered is the declared assumption; DC/DM/DR deliver via their own
                // automatic mechanism, so there is no double-count with the response services).
                RecordedActions: new List<RecordedAction>
                {
                    new(19, +25.6, "BM recovery ramp (BOALF net instructed-level delta)", ProvenanceTag.DerivedFromRecorded, RecordedActionKind.OperatorInstruction),
                    new(29, +25.3, "BM recovery ramp", ProvenanceTag.DerivedFromRecorded, RecordedActionKind.OperatorInstruction),
                    new(39, +25.3, "BM recovery ramp", ProvenanceTag.DerivedFromRecorded, RecordedActionKind.OperatorInstruction),
                    new(49, +25.3, "BM recovery ramp", ProvenanceTag.DerivedFromRecorded, RecordedActionKind.OperatorInstruction),
                    new(59, +25.3, "BM recovery ramp", ProvenanceTag.DerivedFromRecorded, RecordedActionKind.OperatorInstruction),
                    new(69, +25.3, "BM recovery ramp", ProvenanceTag.DerivedFromRecorded, RecordedActionKind.OperatorInstruction),
                    new(79, +125.3, "BM recovery ramp (fast phase)", ProvenanceTag.DerivedFromRecorded, RecordedActionKind.OperatorInstruction),
                    new(89, +125.3, "BM recovery ramp", ProvenanceTag.DerivedFromRecorded, RecordedActionKind.OperatorInstruction),
                    new(99, +125.3, "BM recovery ramp", ProvenanceTag.DerivedFromRecorded, RecordedActionKind.OperatorInstruction),
                    new(109, +125.3, "BM recovery ramp", ProvenanceTag.DerivedFromRecorded, RecordedActionKind.OperatorInstruction),
                    new(119, +125.3, "BM recovery ramp", ProvenanceTag.DerivedFromRecorded, RecordedActionKind.OperatorInstruction),
                    new(129, +125.3, "BM recovery ramp (net +904 MW by 13:12:00)", ProvenanceTag.DerivedFromRecorded, RecordedActionKind.OperatorInstruction),
                },
                Outcome: new RecordedOutcome(
                    InitialRocofHzPerS: -0.102, // max 1-second delta on the NESO public trace
                    NadirHz: 49.275,            // trace minimum, 13:10:11
                    SettleHz: 50.0,
                    LfddMwShed: 0,
                    NesoTraceCsvPath: null,     // trace committed at data/neso/gb-2023-12-22-frequency.csv; attach via --trace
                    Provenance: ProvenanceTag.Recorded,
                    Notes: "Below 49.5 Hz for ~59 s; no LFDD. Two-stage fall on the trace (13:09:51 arrest ~49.65, second fall 13:10:00) matching the two metered losses.",
                    RocofMeasurementWindowS: 1.0), // the recorded RoCoF is a 1-s-window figure - score like for like
                Provenance: ProvenanceTag.Recorded,
                Notes: "Second sourced event, measured end to end: losses from settlement metering (gsp_ic_flows + " +
                       "B1610), outcome from NESO's public 1-second trace, operating point measured (recorded inertia " +
                       "+ WLS-estimated demand + trace pre-event frequency), response services measured (EAC cleared " +
                       "volumes), BM recovery measured (BOALF).",
                // The low-frequency response the system HELD in the 11:00-15:00 UTC EAC delivery block:
                // cleared volumes from NESO's published auction results summary (GDA
                // DataSources/NESO/enduring_auction_capability_eac_auction_results, auctionID 70 -
                // use the results-SUMMARY clearedVolume, not a sum over the buy-orders file, whose
                // rolling weekly archive copies overlap and double-count). Volumes are the procurement
                // record; delivery shapes are the published service terms (DC-L full at 49.5 Hz within
                // ~1 s; DM-L full at 0.2 Hz deviation within ~1 s; DR-L full at 0.2 Hz over ~10 s).
                // By Dec 2023 THIS, not governor droop, is what arrests a GB excursion - without it the
                // replay bottoms at ~49.0 Hz vs the recorded 49.275. Fully delivered at the nadir the
                // 1,226 MW held matches the trace's own arithmetic: at 49.275 Hz with df/dt = 0, the
                // balance needs ~1,221 MW beyond load damping.
                ResponseServices: new List<ResponseService>
                {
                    // Published delivery curves: DC 5% by 0.2 Hz then full by 0.5 Hz; DM ramps
                    // 0.1->0.2 Hz; DR ramps 0.015->0.2 Hz over ~10 s.
                    new(873, 0.015, 0.5, 0.3, "Dynamic Containment low (DCL, EAC auction 70 cleared)", ProvenanceTag.Recorded,
                        KneeDeviationHz: 0.2, KneeFraction: 0.05),
                    new(150, 0.1, 0.2, 0.3, "Dynamic Moderation low (DML, EAC auction 70 cleared)", ProvenanceTag.Recorded,
                        KneeDeviationHz: 0.1, KneeFraction: 0.0),
                    new(203, 0.015, 0.2, 4.0, "Dynamic Regulation low (DRL, EAC auction 70 cleared)", ProvenanceTag.Recorded,
                        KneeDeviationHz: 0.015, KneeFraction: 0.0),
                },
                // From EAC go-live (first auction delivered 2 Nov 2023 - auctionID 1 in the same lake
                // dataset) DC/DM/DR daily auctions are the SOLE response procurement route; legacy
                // monthly FFR ended Oct 2023. The list above is therefore the complete response
                // picture, and the generic conventional-plant governor term must not be added on top.
                ResponseServicesComplete: true,
                // The trip landed on a system already low: the trace is flat at 49.879-49.881 Hz for
                // at least six seconds before the fall. The replay starts in equilibrium there -
                // response partially deployed per the published curves, the residual standing
                // imbalance carried by the BM (whose recovery ramp is the recorded actions above).
                PreEventFrequencyHz: 49.879),

            // ---- Generic secured-loss screening event (a single 1320 MW infeed trip) ----
            // N135 (B4, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04; source NETS SQSS v2.10, 08 April 2025,
            // docs/reference S036, chapter 11 definitions "Normal Infeed Loss Risk" and "Infrequent Infeed Loss Risk").
            // 1320 MW is the NORMAL infeed loss risk; the largest secured infeed loss is the INFREQUENT infeed loss risk,
            // 1800 MW (the figure NESO's FRCR 2025, S052 s.4.1, calls the future largest loss). The title used to say
            // "largest-credible-loss", which 1320 MW is not, so it now names what it is. The step size, the id (quoted by
            // the manual, the tests and users' scripts) and every number are unchanged; only the title text moves (here
            // and in data/events/gb-events.json, which D-DOC-179 keeps field-for-field equal to this list).
            // Decision (Ruling R1): the event stays at 1320 MW rather than moving to 1800 MW. A normal infeed loss is the
            // more frequent secured event and the one this placeholder outcome was written against; the infrequent
            // 1800 MW case can be authored in a catalog (--catalog) without changing a built-in every capture quotes.
            new(
                Id: "gb-largest-loss-1320",
                Title: NormalInfeedLossScreeningTitle,
                WhenUtc: new DateTime(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc), // [VERIFY] nominal, not a specific event
                PreEventInertiaMwS: 200_000,   // [VERIFY] placeholder
                PreEventDemandMw: 35_000,      // [VERIFY] placeholder
                Losses: new List<PowerStep>
                {
                    // S3 G02 (October 2026): 1320 MW is the NETS SQSS "Normal Infeed Loss Risk" from 1 April 2014
                    // (NETS SQSS Version 2.10, 08 April 2025, docs/reference S036, chapter 11 definitions, PDF p.66;
                    // the Infrequent Infeed Loss Risk is 1800 MW from the same date, PDF p.61). The value is
                    // unchanged; the step is now tagged Recorded with that citation. The rest of this screening
                    // event (inertia, demand, outcome) stays CuratedPlaceholder [VERIFY].
                    new(1.0, -1320, "SQSS normal infeed loss risk, 1320 MW (NETS SQSS v2.10 ch.11)", ProvenanceTag.Recorded),
                },
                RecordedActions: new List<RecordedAction>(),
                Outcome: new RecordedOutcome(
                    // [VERIFY] derived, not sourced: -dP f0 / (2 Ek) = -1320 x 50 / (2 x 200000) = -0.165 Hz/s, so it is
                    // only as good as the placeholder 200,000 MW.s inertia above.
                    InitialRocofHzPerS: -0.165,
                    // [VERIFY] 49.2 Hz is the FRCR 2025 secured-event limit (NESO FRCR 2025, docs/reference S052,
                    // s.7.1: "keep resulting frequency deviations within 49.2 Hz and 50.5 Hz"), i.e. a policy floor,
                    // not the outcome of a recorded event; it stays a placeholder outcome.
                    NadirHz: 49.2,
                    SettleHz: 49.8,
                    LfddMwShed: 0,
                    NesoTraceCsvPath: null,
                    Provenance: ProvenanceTag.CuratedPlaceholder,
                    Notes: "Nominal secured-loss screening case, not a specific recorded event."),
                Provenance: ProvenanceTag.CuratedPlaceholder,
                Notes: "Design-basis screening loss. Figures [VERIFY]."),
        };
        return Validate(events, utcNow);
    }
}

/// <summary>
/// Phase 0.1: the source-generated JSON metadata for the event catalog document and every type it reaches, so a
/// catalog loads and saves without runtime reflection (NativeAOT). Metadata mode only (the bytes come from the same
/// converters the reflection path used); enums by name. The runtime options are passed in by <see cref="EventCatalog"/>.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, UseStringEnumConverter = true)]
[JsonSerializable(typeof(EventCatalog.CatalogDoc))]
internal sealed partial class EventCatalogJsonContext : JsonSerializerContext;
