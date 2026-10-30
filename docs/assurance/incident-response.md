# Incident-response runbook

**Assurance statement. Status as at GridSim 1.95.296.** This is the incident-response and recovery runbook
for the GridSim state estimator and its services, mapping to CAF Objective D1 (Response and recovery
planning) and D2 (Lessons learned). It is the runbook that `caf-mapping.md` section 7.1 cites.
CAF v4.0 (released 6 August 2025) strengthens D1: response plans must be tested, and their scope must extend
to suppliers. This runbook is written to that bar. Under the UK NIS Regulations 2018, Regulation 11 requires
an OES to notify the competent authority of any incident with a significant impact on the continuity of the
essential service without undue delay and no later than 72 hours after becoming aware of it.

GridSim is assessed as a software component embedded inside a deploying OES in downstream electricity, under
the Ofgem Downstream Gas and Electricity (DGE) CAF overlay. The competent authority is Ofgem, with DESNZ and
the NCSC. The Regulation 11 duty lands on the OES; GridSim's job is to detect candidate incidents, raise them
with enough structured detail for the OES to assess significance, and give the OES the forensic tools to
contain and recover - without ever leaking a grid claim newer than the horizon. Per-control statuses of
record are in `caf-mapping.md` section 8.

Governing constraint: no step in this runbook reads present telemetry or emits a future-dated grid claim.
Recovery re-runs history; it never re-runs the present. See `UnderstandingRestrictions.md`.

---

## 1. Detection sources

An incident begins at one of these signals. Each is a control already in the code. When the run has an audit log
(`--audit-log` on the CLI, `Audit:LogPath` on the web host), each detection below except the audit-verify failure is
also written as its own `action=security` audit entry at the detection site:

- **`FdiaScreen` SuspectedFdia.** A chi-square-consistent estimate whose state breaks a hard physical law or
  leaves the operational envelope by more than three sigma raises `FdiaScreenReport.SuspectedFdia` - a
  candidate false-data-injection incident (`estimate --fdia-screen`; a `SuspectedFdia` entry).
- **`EstimationStatus.Infeasible`.** The `FeasibleWlsStep` Ring C gate has rejected a converged state as
  physically impossible. A burst of these on a feed is a data-integrity or poisoning signal (`estimate`, each
  `serve` window and each web resident-estimator window; an `InfeasibleState` entry).
- **AuditLog verify failure.** `AuditLog.Verify` / `AuditLogFile.Verify` returning false, or
  `gridsim audit verify` naming a broken entry (or, with `--pubkey`, a missing or bad anchor signature), is a
  tamper or corruption signal on the evidence record. An audited run refuses to start on such a log (CLI exit 1,
  web start-up failure) and reports it on stderr; the event is not written into that same log, whose chain
  cannot be trusted, so the operator must route that stderr report to the SOC.
- **A `ForwardInferenceException` burst.** A rising rate of rejected too-fresh data at an ingestion seam
  indicates either a clock problem, a misrouted live feed, or an attempt to push sub-horizon data through
  the guard (a `HorizonViolation` entry per refusal; `interpose` writes a `LiveDataQuarantined` entry per
  quarantined window).
- **An output-guard refusal.** `serve` refusing an estimate stamped newer than the horizon (a
  `RestrictionRefused` entry).
- **An availability SLO breach.** A breach of a service-level objective (`ServiceLevelObjective` /
  `HealthProbe`) - converged/processed falling below the objective, window latency or rejected-fresh
  fraction rising - is an availability incident (B5/D1). It is recorded as an `AvailabilityDegraded` entry at
  the end of a `serve` run and of the web resident estimator's run; the `/health` endpoint answers 503 but
  raises nothing itself.

Two kinds have no product detection site at this release: an access denial (`RoleGuard` is not called by any
host) and a distrusted source or dependency alert (no product path produces them).

These are the same signals listed in `nis-oes-gap-assessment.md` section 7.1 and `caf-mapping.md` section 6.

---

## 2. Raising an incident

A detection becomes an incident through the incident channel (built and wired; `nis-oes-gap-assessment.md`
section 7.1). It is switched on with `--incident-log <file>` on the CLI or `Audit:IncidentLogPath` on the web host,
both of which need the audit log:

- the detection is first recorded as an audit entry with its `SecurityEventKind` and `SecuritySeverity`
  (`AuditRunRecorder.RecordSecurityEvent`);
- a single event whose baseline severity is Significant or above (`SuspectedFdia`, `RestrictionRefused`,
  `DistrustedSource`, `AvailabilityDegraded`, `DependencyAlert`) raises one incident (`SecurityIncidentSink`), and
  a burst of events is escalated to one latched incident (`SecurityEventMonitor`; by default 20
  `HorizonViolation`, 3 `SuspectedFdia`, 1 `AuditChainBroken`), both through `IncidentRouter`;
- an `IncidentReport` is constructed, carrying its category, severity, a summary, structured detail, and the
  `AuditHash` of the audit entry that evidences it;
- it is appended as one JSON line to the incident file (`JsonLinesIncidentSink`), which the OES routes to its
  security operations centre (SOC), ticketing or notification process; an OES may instead supply its own
  `IIncidentSink` in code.

Incidents are not themselves written into the audit chain; each one names, by hash, the audit entry that
evidences it, so the incident and the trail are linked. A failure to write an incident never stops the run; the
CLI reports it at the end and exits 1 (the web host does not report it at this release).

Restriction constraint on raising: an `IncidentReport` carries `RaisedUtc` as run metadata and, if present, a
`StateTimeUtc` that is a historical grid instant guard-checked on construction and never newer than the
horizon. A data-integrity report may quote the offending *historical* measurement, never a reconstructed
present or future state. A horizon-violation report carries the rejected stamp and the horizon, not the
payload.

---

## 3. Significance assessment against Regulation 11

On receiving an incident, the OES assesses whether it has a *significant impact on the continuity of the
essential service*, which is the Regulation 11 test and the start of the 72-hour clock. The
`IncidentReport.Severity` carries GridSim's own first classification (the `Significant` band is the one that
maps to a Regulation 11 assessment), but the significance decision is the OES's, made against its own
thresholds and the Ofgem DGE criteria.

The horizon guard shapes this assessment in GridSim's favour and this must be stated to the assessor
honestly: because GridSim processes only week-stale or replayed data, a compromise of GridSim does not of
itself interrupt the live essential service. A GridSim incident is therefore most often an incident about the
*integrity of the historical evidence record* or the *availability of an analysis service*, not an
interruption of live grid operation. That does not remove the Regulation 11 duty - a tampered evidence
record or a poisoned reconstruction can still be significant in the OES's wider context - but it frames the
assessment accurately rather than overstating the impact.

---

## 4. Isolation and containment

Once an incident is confirmed, contain it before recovering:

- **Isolate the affected service.** Stop the resident service (`serve`, the web host, or the interposer) that
  is emitting the suspect output, or fail it over if a redundant instance is available. Because the estimator
  is deterministic and stateless per call, stopping a service loses no state that cannot be re-derived
  (section 5).
- **Quarantine a poisoned measurement source.** Where a specific feed is implicated, withhold that source's
  data from the affected runs (remove it from the input set or the window directory) and re-run. The
  distrusted-source control (`DistrustedSources`, `caf-mapping.md` section 7.1), which forces a distrusted
  source's rows to pseudo-measurement treatment rather than trusted observations, is built as a primitive but
  no CLI verb or host setting applies it at this release, and distrusted rows would still count towards the
  chi-square redundancy, so it is not yet an operator action. Live telemetry captured by the RTU interposer is already
  written to `QuarantineLiveStore`, which refuses any path inside the evidence lake, so live data cannot
  contaminate the historical record; `PmuLiveQuarantine` sorts synchrophasor frames by the guard's horizon.
  Quarantine is the containment action for a suspected false-data-injection or poisoning incident.
- **Preserve the evidence.** Before any remediation, run `gridsim audit verify` and capture the result (with
  `--pubkey <public.pem>` where the anchor is signed). A broken chain localises tampering to a sequence range,
  the anchor shows whether entries were removed from the end, and a bad or missing anchor signature shows a
  joint rewrite of log and anchor by someone without the private key. Where entries were forwarded
  (`--audit-forward`, `Audit:ForwardPath`), compare the forwarded copy with the local log. The audit files, the
  forwarded copy, the incident file and the quarantine store are the forensic record and must be preserved, not
  overwritten.

---

## 5. Recovery using deterministic replay

Recovery relies on a property that is built and tested, not assumed: GridSim is bit-reproducible. An estimate
is bit-identical from `(model, measurement set, options)`, and a replay is bit-identical run to run, on both
the dense and the sparse backend, with no wall clock in the estimation path (`EstimatorDeterminismTests`,
`GdaReplayDeterminismTests`, `EstimationClockIndependenceTests`, `Doc16NumericsDeterminismTests`).

The recovery procedure for a suspected-tamper or suspected-corruption incident:

1. re-run the affected window from the preserved inputs;
2. compare the re-derived state against the audited one bit for bit;
3. treat a divergence as the incident signal - if the replay matches, the audited record is intact; if it
   diverges, the inputs or the record were altered, and the divergence localises what changed.

Because recovery re-derives a past state from history, it never risks presenting fresher-than-permitted data:
degradation and recovery both operate at or behind the 169-hour horizon, which is already the normal state of
the system.

---

## 6. The 72-hour notification timeline

The Regulation 11 clock starts when the OES becomes aware of a significant incident. The runbook timeline:

- **T0 - detection.** A detection source (section 1) raises an `IncidentReport` to the SOC (section 2).
- **Within hours - triage and containment.** The OES triages, GridSim isolates and quarantines (section 4),
  and `gridsim audit verify` preserves the evidence.
- **As soon as significance is established - the clock is running.** When the OES assesses the incident as
  significant (section 3), the Regulation 11 duty is live.
- **No later than T0 + 72 hours - notify the competent authority.** The OES notifies Ofgem, without undue
  delay and within 72 hours of becoming aware. GridSim's contribution to the notification is the structured
  `IncidentReport` detail and the `AuditHash` that ties the incident to a tamper-evident entry, so the
  notification can be substantiated.
- **After notification - recovery and review.** Recovery (section 5) proceeds; the post-incident review
  (section 7) follows.

GridSim does not notify Ofgem itself; it equips the OES to notify within the window.

---

## 7. Post-incident review template (D2)

Every incident closes with a short post-incident review, so that the response plan and the controls improve
(CAF D2). The template:

- **Summary.** What happened, the detection source, the category and severity, and the `AuditHash` of the
  evidencing entry.
- **Timeline.** T0 to closure, including when significance was established and, if applicable, when Ofgem was
  notified.
- **Impact.** What was actually affected (evidence-record integrity, service availability, a reconstruction's
  accuracy), stated against the horizon-bounded blast radius, not overstated.
- **Containment and recovery.** The isolation and quarantine actions taken, and the replay comparison result
  (match or divergence, and where).
- **Root cause.** The underlying fault - a dependency CVE, a misconfiguration, a poisoned feed, a tamper
  attempt.
- **Lessons learned.** What to change: a new or amended control, a test to pin it, a governance or cadence
  change (feeding `security-governance.md`), and any risk-register update (`risk-register.md`).
- **Actions.** Each lesson becomes a tracked remediation with an owner, consistent with the gap registers in
  `caf-mapping.md` section 9 and `nis-oes-gap-assessment.md` section 8.

The existing restriction-review loop (`RestrictionReview.Reviewed`) is the worked example of a lessons-learned
loop functioning for the one seam that matters; this template generalises it to any incident.

---

## 8. Testing the plan, and supplier scope (v4.0)

CAF v4.0 requires that the plan is exercised and that its scope extends to suppliers.

- **The plan is to be exercised.** A tabletop exercise is planned, walking a `SuspectedFdia` and an
  audit-verify-failure scenario end to end - detection, raising, significance assessment, quarantine, replay
  recovery, the 72-hour timeline, and the post-incident review - so the runbook is tested rather than only
  written. The exercise result and its date are to be recorded against this document at the next review point.
- **Supplier and upstream incidents are in scope.** The GDA data lake is GridSim's primary upstream supplier,
  and an incident originating there (a poisoned or tampered upstream feed, an integrity failure in the lake)
  is in scope for this runbook per v4.0. Containment uses the same steps (section 4): a poisoned upstream
  source's data is withheld from the affected runs (the distrusted-source control, once wired, will do this in
  the estimator), and the guard already refuses any sub-horizon upstream datum regardless of its source. A
  dependency-supplier incident (a CVE flagged by the OES scanner, the vulnerable-package gate or Dependabot) is
  raised by the OES through its own process: the incident wiring would raise a `DependencyAlert` event, but no
  product path produces one at this release (`caf-mapping.md` section 7.1, `nis-oes-gap-assessment.md`
  section 7.3).

---

## 9. Cross-references

- `caf-mapping.md` - section 6 (detection controls: audit trail, `FdiaScreen`); section 7 (Objective D,
  including the distrusted-source control, built but not yet wired); section 8 traceability matrix; section 9
  gap register.
- `nis-oes-gap-assessment.md` - section 7.1 (the incident-reporting seam and Regulation 11); section 6 (why
  the horizon bounds the impact); section 8 honest gap table.
- `risk-register.md` - the risks this runbook handles when they materialise, and their residual treatments.
- `security-governance.md` - the owner, the review cadence, and the CHANGELOG Security discipline that
  records a control change arising from a lesson learned.
- `UnderstandingRestrictions.md` - the governing no-forward-inference document.
