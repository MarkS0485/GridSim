# NIS Regulations / Operators of Essential Services -- Gap Assessment

**Status:** assessment as at GridSim 1.95.296, against the NCSC Cyber Assessment Framework v4.0 (released
6 August 2025). Types are named as they are in the code at this release. The seams described in Section 7 are now
built: the types, namespaces and tests named there exist unless the text marks an item as a deploying-party action.
Three of them are primitives that no product path applies yet (the `RoleGuard` authorisation gate, the
`DistrustedSources` control and the reconnect backoff schedule); they are marked Partial below.
Per-control CAF statuses are kept in `caf-mapping.md`; where this document and the CAF mapping cover the same control,
the CAF mapping row is the status of record.

**Governing constraint:** everything below respects `UnderstandingRestrictions.md` and
the review gate `RestrictionReview.Reviewed` (`src/GridSim.Core/Diagnostics/RestrictionReview.cs`). GridSim is a historical state estimator -- a *filter, not a
predictor*. No obligation, hook, feature or seam introduced by this document may create a code path
that reads present public telemetry or emits a future-dated claim about the real GB grid. The
`ForwardInferenceGuard` 169 h floor is treated here not only as a safety invariant but as a
first-class *security control* that shrinks the assessed attack surface (Section 6).

**Audience:** GridSim maintainers, and the security/assurance function of any Operator of Essential
Services (OES) considering GridSim as an in-boundary component.

---

## 1. Purpose and scope

This document assesses GridSim against the obligations that the UK Network and
Information Systems Regulations 2018 (the NIS Regulations) place on Operators of Essential Services
in the **downstream electricity** subsector, and identifies what the current design already
satisfies, what remains, and how to close the gaps without breaching the no-forward-inference
invariant or the zero-warning build gate.

In scope:

- The obligations that apply when GridSim is deployed as a component *inside* an OES boundary.
- A mapping of the current codebase (data-boundary guard, tamper-evident audit trail, FDIA screen)
  onto the Ofgem-adapted NCSC Cyber Assessment Framework (CAF) contributing outcomes.
- The specific argument that the 169 h horizon guard reduces the operational-visibility attack
  surface -- a property unusual enough to be worth stating precisely.
- An honest, per-outcome status table and the built seams for: incident reporting hooks, access
  control, supply-chain assurance, and availability SLOs.
- A validation and test plan that keeps the assurance claims falsifiable (machine-epsilon
  cross-checks, determinism, invariant tests) and inside the zero-warning gate.

Out of scope: certifying GridSim *as* an OES (it is not one -- see Section 3); the OES's wider
organisational NIS compliance (governance, HR, physical security) except where GridSim must expose a
seam to feed it; and any change to the estimator numerics.

---

## 2. Regulatory context

### 2.1 The NIS Regulations 2018 and Operators of Essential Services

The NIS Regulations 2018 transpose the EU NIS Directive into UK law and survive post-EU-exit as
retained domestic law. They impose duties on two population classes: Operators of Essential Services
(OES) and Relevant Digital Service Providers. Electricity falls in the OES population. The core
duties on an OES are:

- **Regulation 10 (security duty):** take appropriate and proportionate technical and organisational
  measures to manage risks to the security of the network and information systems on which the
  essential service relies, and to prevent and minimise the impact of incidents.
- **Regulation 11 (incident notification):** notify the designated competent authority of any
  incident which has a significant impact on the continuity of the essential service, without undue
  delay and no later than 72 hours after becoming aware of it.
- **Regulations 15-18 (enforcement / inspection):** the competent authority may inspect, require
  information, issue enforcement notices and levy penalties.

### 2.2 Competent authority and the Ofgem-adapted CAF

For downstream electricity in Great Britain the designated competent authority is the
Gas and Electricity Markets Authority, exercised through **Ofgem**, working alongside the NCSC as
the national technical authority and the Department for Energy Security and Net Zero. Ofgem
implements the Regulation 10 security duty through an **energy-sector adaptation of the NCSC Cyber
Assessment Framework (CAF)** -- an outcome-based profile with per-outcome indicators of good
practice, assessed against a target profile appropriate to the operator's importance. This assessment targets
**CAF v4.0 (released 6 August 2025)**, which carries 41 contributing outcomes (39 in v3.x) across the same four
Objectives and fourteen Principles. The CAF is organised as four Objectives and fourteen Principles:

| Objective | Principles |
|---|---|
| **A -- Managing security risk** | A1 Governance, A2 Risk management, A3 Asset management, A4 Supply chain |
| **B -- Protecting against cyber attack** | B1 Service protection policies, B2 Identity and access control, B3 Data security, B4 System security, B5 Resilient networks and systems, B6 Staff awareness |
| **C -- Detecting cyber security events** | C1 Security monitoring, C2 Proactive security event discovery |
| **D -- Minimising the impact** | D1 Response and recovery planning, D2 Lessons learned |

This assessment maps GridSim's technical capabilities to the CAF *contributing outcomes* that a
software component can meaningfully evidence. Organisational outcomes (A1 governance, B6 staff
awareness) are the OES's responsibility and are noted as out-of-component.

The v4.0 deltas that matter to a software component are: the new outcome **A4.b Secure software development and
support** (met through the release provenance and the compliance attestation, section 7.3); expanded **monitoring and threat hunting**
under C1/C2 (met through per-security-event audit entries and the `SecurityEventMonitor` aggregation, section 7.1); and
a strengthened **D1** that requires tested response plans extending to suppliers (section 7.1). v4.0 also weaves
AI-related risk through several outcomes; GridSim is a deterministic, non-AI and non-ML state estimator (its numerics
are bit-reproducible and contain no model inference), so those AI-specific risks do not arise in the engine. The one
adjacent concern, a future dynamics-into-estimation predictor, is held off by the `RestrictionReview` gate (Section
5.4).

### 2.3 What "component inside an OES" changes

GridSim is a .NET 10 library plus CLI plus web host. It is not an operator under licence and does not
itself deliver an essential service. When an OES embeds GridSim, GridSim becomes:

- a **network and information system** the OES must secure under Regulation 10, and
- a **supplier / third-party component** the OES must assure under CAF Principle A4 (supply chain).

The obligation therefore lands on the OES; GridSim's job is to be *assurable* -- to expose the
evidence, controls and seams the OES needs to meet A3, A4, B2, B3, C1 and D1 for the part of its
estate that is GridSim. This document specifies that assurable surface.

---

## 3. Where GridSim sits in an OES

### 3.1 Deployment posture and the primary trust boundary

The defining architectural fact is that GridSim's inputs **lag real time by at least 169 hours or
are replay**. This is enforced at every dated ingestion seam by
`GridSim.Core.Diagnostics.ForwardInferenceGuard.RejectFuture` (see
`src/GridSim.Core/Diagnostics/ForwardInferenceGuard.cs`), which throws
`ForwardInferenceException` for any stamp newer than `UtcNow - 169 h`. The seams that call it include:

- `MeasurementSet.Load` (`src/GridSim.Core/Estimation/MeasurementSet.cs`) -- the estimator's own input,
- `GdaTimeSeries.Load`, `GdaReplayLoop.Replay` and `PresolvedReplay` (the GDA data-lake bridge and replay),
- `NesoFrequencyCsv.Read`, `EventCatalog.Validate`,
- `GridBridgeClient.HandleState` (the network wire seam),
- `RealtimeEstimatorEngine.Run` and `RealtimeDseEngine` (`src/GridSim.Core/Simulation/`), which refuse an undated
  window as a protocol violation and record-and-skip any window newer than the horizon,
- the RTU interposer (`InterposePipeline`, `RtuMeasurementSetMapper`) and the synchrophasor mappers
  (`PmuMeasurementSetMapper`, `PmuLiveQuarantine`).

The full list is in `caf-mapping.md` section 3.1.

Consequently GridSim, as embedded, is a system that processes **week-old or replayed** data. This is
the pivot for the whole assessment: it changes the *impact* half of every NIS risk calculation
(Section 6).

### 3.2 Trust boundaries a deploying OES must draw

```
  +---------------------------- OES security boundary ----------------------------+
  |                                                                               |
  |   [Archived / >=169h data]  --guard-->  GridSim.Core estimator  --> AuditLog  |
  |          ^                                   |                        |        |
  |          |                                   v                        v        |
  |   GDA bridge / MeasurementSet         EstimationResult          hash-chained   |
  |   NESO CSV / EventCatalog             + FdiaScreenReport        evidence trail |
  |          ^                                                                     |
  |   GridBridgeClient (network wire) <==== transport TLS: deploying-party ====> |
  |                                                                               |
  +-------------------------------------------------------------------------------+
```

The estimator core is deterministic and offline-capable; the exposed attack surface is the
ingestion seams, the `GridBridgeClient` wire (an outbound client), and the web host's `/api` and `/ws/sim`
endpoints. In the production configuration the web host runs replay-only, which leaves `/api` and `/ws/sim`
unmapped (the shipped exposure control). Incident reporting is built and wired (Section 7.1); the authorisation gate
is built but no endpoint enforces it yet (Section 7.2). The bridge client refuses an unencrypted `ws://` endpoint
unless explicitly allowed; transport security on the bridge wire is otherwise a deploying-party choice.

---

## 4. CAF objective-by-objective mapping

Legend: **Met** (design satisfies the outcome as a component), **Partial** (a seam exists but
work remains), **Gap** (no component support yet), **OES** (organisational -- out of component).

| CAF outcome | What it asks of a component | GridSim status | Evidence / seam |
|---|---|---|---|
| A2 Risk management | Understand and document security risks to the system; hold a threat-actor view | Met | `UnderstandingRestrictions.md`, this document, and `risk-register.md` (threat-actor view and the horizon-as-security argument) |
| A3 Asset management | Know what data and dependencies exist | Met | `ProvenanceTag` taxonomy; CycloneDX SBOMs; `packages.lock.json` lock files; `BuildProvenance` (7.3) |
| A4 Supply chain | Assure third-party components and dependencies | Met (in-component) | SBOMs injected into the shipped builds, lock files, build provenance, signed build-provenance attestation of every Release file, vulnerable-package gate, CodeQL (by hand), Dependabot, release signing with a self-signed pinned certificate (7.3) |
| A4.b Secure software development and support (new in v4.0) | Run a secure-development framework; attest software authenticity and integrity | Met (mechanism); verdict NOT COMPLIANT at this release | NIST SSDF / Microsoft SDL mapping; download integrity from the provenance attestation, `SHA256SUMS.txt` and Authenticode; `ComplianceManifest` + `ComplianceAttestation` bound to the build identity, compliant only for a release build with every in-scope control recorded Achieved (7.3) |
| B2 Identity and access control | Authenticate and authorise access to the system and data | Partial | Web host sign-in and SSO consumer, replay-only gate (the shipped exposure control); the `RoleGuard` authorisation gate is built and tested but no hosting surface enforces it yet (7.2); bridge mutual-TLS remains a deploying-party transport choice |
| B3 Data security | Protect data at rest and in transit; ensure integrity | Met (in-component) | Audit chain persisted and anchored, with an optional signed anchor (output integrity); key ring encrypted at rest (DPAPI, or a certificate); `ISecretStore` with a secrets-directory backend; `ws://` refused unless allowed; bridge transport TLS is deploying-party |
| B4 System security | Reduce the attack surface; validate inputs | Met (core) | Guard + `MeasurementSet.Validate` + fail-loud numerics + resource limits (`ResilienceOptions`, applied to every bridge client) |
| B5 Resilient networks and systems | Maintain availability under adverse conditions | Partial (in-component) | `RealtimeEstimatorEngine` continue-on-error resilience; session cap; `ResilienceOptions` limits; `ServiceLevelObjective` + `HealthProbe`, an SLO breach recorded as a security event; no host drives the reconnect backoff schedule (7.4) |
| C1 Security monitoring | Collect and analyse data to detect events | Met | `FdiaScreen`, chi-square/LNR bad-data, persisted, anchored audit trail with one entry per security event at the audited detection sites, optional signed anchor; JSON-lines forwarding (`--audit-forward`, `Audit:ForwardPath`) (7.1) |
| C2 Proactive event discovery | Actively search for indicators not caught by monitoring; threat hunting | Met | FDIA physical-plausibility screen; `SecurityEventMonitor` aggregation and burst escalation, attached by `--incident-log` / `Audit:IncidentLogPath` (7.1) |
| D1 Response and recovery | Respond to and recover from incidents; tested plans extending to suppliers | Met (in-component); distrusted-source containment Partial | `IncidentRouter` + `IIncidentSink`, wired through `--incident-log` / `Audit:IncidentLogPath`; `DistrustedSources` (primitive only); `incident-response.md` (tabletop; GDA-lake suppliers in scope) (7.1) |
| D2 Lessons learned | Use incidents to improve | Met (in-component) | Restriction-review loop; post-incident template in `incident-response.md`; component feeds the org loop via the audit trail |
| A1 Governance, B1 policy, B6 awareness | Organisational governance and culture | OES | Out of component (in-component governance in `security-governance.md`) |

The remaining sections expand the core **Met** cells (Section 5), the distinctive horizon argument
(Section 6), and the seams that close each formerly-open cell (Section 7), now built.

---

## 5. What the current design already satisfies

### 5.1 The data-boundary guard (B4 System security, and the impact-reduction spine)

**Control.** `ForwardInferenceGuard` rejects any dated input newer than `UtcNow - HorizonHours`,
with `HorizonHours = 169.0`. The predicate is exact:

```
accept(stamp)  <=>  stamp <= UtcNow - 169 h        (UTC, DST-safe by the extra hour)
```

`RejectFuture` normalises `DateTimeKind.Unspecified` to UTC before comparison, so a naive timestamp
cannot slip through as local time. `AssertHistorical` applies the same test across a whole series and
throws on the first violation.

**Input validation.** `MeasurementSet.Validate` rejects non-finite values,
non-positive or non-finite sigmas, unknown bus ids, and unresolvable / ambiguous branch references
*before* any value can reach the gain matrix -- "a NaN or a zero-weight measurement caught here is
one that cannot silently poison the gain matrix later." `MeasurementSet.Load` additionally rejects an
unknown wire schema. Together these are the CAF B4 "input validation / reduce attack surface"
outcome for the estimator's front door.

**Fail-loud numerics.** A singular gain is reported as `EstimationStatus.Unobservable`, never as
NaN; a non-finite iterate becomes `EstimationStatus.NonFinite`; constrained/feasible methods above
their dense limit throw `NotSupportedException` rather than silently degrade. Failure is always
observable, which is what a monitored system needs.

### 5.2 The tamper-evident audit trail (B3 Data security, C1 monitoring, D1 recovery evidence)

`GridSim.Core.Diagnostics.AuditLog` is an append-only, hash-chained log. Each
`AuditLogEntry` records `Sequence`, `RunTimestampUtc` (wall-clock run metadata -- *not* a grid
claim), an optional `StateTimeUtc` (the present/past grid instant described), a `ProvenanceTag`
trust class, a human `Summary`, `PrevHash` and `Hash`, where:

```
Hash_i = SHA256( seq | runUtc(o) | stateUtc(o)|"-" | tag | summary | Hash_{i-1} )
Hash_0 chains to the constant "GENESIS".
```

`AuditLog.Verify` recomputes the chain and returns false if any entry was altered, reordered, inserted or deleted.
`AuditLogWriter` persists the chain append-only (`gridsim-audit/1`, flushed on every append, one writer per log), and
an anchor file (entry count and head hash) is checked on every open, so removal of entries from the end is caught too.
`--audit-log` records `estimate`, `crossval`, `interpose` and `serve` runs, and the web host records its resident
estimator through `Audit:LogPath`; `gridsim audit verify` checks a log. This is the evidence trail CAF Objective C and
D1 require: what was emitted, when, for which grid instant, and with what provenance -- tamper-evident by
construction. The reporting, signing and forwarding are now built and wired (7.1): `AuditRunRecorder.RecordSecurityEvent`
writes one entry per security event with its reason at the detection sites of `estimate`, `serve`, `interpose` and
the web resident estimator; `SignedAuditAnchor` adds an optional detached ECDSA P-256 signature over the anchor
(`--audit-sign-key` / `--audit-verify-key`, `Audit:AnchorSigningKeyPath` / `Audit:AnchorVerificationKeyPath`,
`gridsim audit verify --pubkey`), so a joint rewrite of log and anchor is detectable by anyone who lacks the private
key (against a host that holds the key, only when the key is kept off the host); and `--audit-forward` /
`Audit:ForwardPath` appends every entry as a JSON line to a second file, which the OES points at a WORM mount or a
SIEM collector.

### 5.3 The FDIA screen (C1 / C2 detection)

`GridSim.Core.Estimation.Security.FdiaScreen` is a second detection layer for
residual-cloaked False Data Injection. The threat model is exact: an attack vector `a = H c` lies in
the range of the measurement Jacobian `H`, so it shifts the estimate by `c` while leaving every
residual `r = z - h(x_hat)` unchanged (Liu-Ning-Reiter). Classical chi-square + largest-normalised-
residual detection is provably blind to it:

```
J(x_hat) = sum_i ( (z_i - h_i(x_hat)) / sigma_i )^2       (WLS objective)
gain G   = H^T W H,   W = diag(1/sigma_i^2)                (per WlsStateEstimator)
attack a = H c  =>  r unchanged  =>  chi-square PASS, LNR silent.
```

`FdiaScreen.Screen` reuses GridSim's physics-conformance audit (`PhysicsLaws.Audit`) and operational
envelope check (`PhysicsFeasibility.Check`) as an **independent validator the attacker must also
satisfy**: a chi-square-consistent estimate whose state nonetheless breaks a hard physical law or
leaves the voltage/thermal/generator-Q envelope by more than three standard deviations of its own uncertainty is
flagged `SuspectedFdia` in `FdiaScreenReport` (a breach inside that margin is listed but not counted, so a true state
sitting on a limit does not raise a false alarm). `FdiaCloakedAttackTests` builds a genuinely cloaked attack and
proves both halves: the residual is unchanged and the screen flags the state.
The feasible-landing sub-class (an attack that lands on another *feasible, in-limit* operating point) is now covered:
`FdiaScreen.Screen` takes `protectedAnchors`, and a feasible, in-limit cloaked attack that disturbs a PMU-anchored
quantity is flagged (`FdiaScreenReport.AnchorInconsistent`, pinned by `FdiaProtectedAnchorTests`), because the attacker
cannot both cloak against the WLS residual and match an independently measured PMU anchor. The screen **reports**,
never silently rejects. This is a genuine C1/C2 contributing outcome, with both sub-classes now covered.

### 5.4 The Dynamics -> Estimation review gate (defence against capability creep)

`RestrictionReview.AssertPermitted` fails loud if any estimation method that fuses
a dynamics process model runs without an explicit review entry. This is not a NIS control per se, but
it is the mechanism that stops a future feature from quietly converting the estimator into a
predictor -- i.e. it protects the very property that Section 6 relies on for its security argument.
It belongs in the assessment as a *change-control* assurance (supports A2 and D2).

---

## 6. How the 169 h horizon guard reduces the operational-visibility attack surface

This is the argument that distinguishes GridSim from a conventional EMS/SCADA state estimator, and
it is worth stating with precision because it changes the risk maths an OES applies to the component.

### 6.1 The asset a normal RTSE holds, and the asset GridSim holds

A live grid-operator RTSE is a high-value target because it holds a **current, actionable picture of
the real system**: present inertia, present margins, present topology and constraint patterns.
Compromise of that estimator, or exfiltration of its state, hands an adversary *timing* -- knowledge
of when the system is weakest, which is knowledge of when a disturbance does the most damage. That
is precisely the "forward visibility / inference of unpublished system conditions" harm forbidden by
`UnderstandingRestrictions.md`.

GridSim structurally cannot hold that asset. Every estimate it emits is stamped at a grid instant at
least 169 h in the past (or is model replay). By the guard's own documented rationale, a week-lagged
state carries no operationally actionable information: unit commitment, outage windows, market
positions and constraint patterns have all rolled over. The high-value asset -- a fresh operational
picture -- is *absent from the system by construction*.

### 6.2 The security consequence, stated as attack-surface reduction

Frame each threat as (surface) x (value of the asset behind it). The horizon guard drives the second
factor to near zero for the confidentiality threats that make a live RTSE dangerous:

- **Exfiltration of estimator output.** An adversary who reads every `EstimationResult` and every
  `AuditLogEntry` GridSim ever produced obtains a week-lagged reconstruction of already-public
  history. There is no present-state or future-state secret to steal, because none is ever computed.
  The classic "steal the operator's current picture" objective has no target here.
- **Model/data poisoning for anticipatory advantage.** An attacker who tampers with inputs to bias
  the estimate still only biases a *historical reconstruction*; the output cannot be repurposed as a
  nowcast because the guard refuses any input recent enough to make it one. The FDIA screen (5.3)
  still applies to protect reconstruction integrity, but the *strategic* payoff -- shaping the
  operator's live view -- is unavailable.
- **Timing / anticipation.** The entire class of "learn when the grid is weak, then act" attacks
  depends on the estimate being about the present or future. The guard's second-order effect
  ("reduced operational constraint space") means GridSim's whole output space sits outside the
  envelope an operational reading could attach to.

The residual confidentiality risk that *does* remain is ordinary: the historical states and audit
log are still data the OES should protect (B3) and access-control (B2), because provenance and
integrity matter for evidence. But the catastrophic CNI-timing confidentiality risk that motivates
most RTSE hardening is **designed out**, not merely mitigated.

### 6.3 Why this must be defended, not just claimed

The security value in 6.2 is entirely contingent on the guard remaining universal and on no seam
fusing dynamics into estimation to produce a predictor. Therefore the assessment treats two existing
mechanisms as **security controls, not merely safety invariants**:

1. `ForwardInferenceGuard` at every dated seam (invariant #1), pinned by `ForwardInferenceGuardTests`,
   `GdaReplayLoopTests` and `RestrictionInvariantTests`.
2. `RestrictionReview.AssertPermitted` gate (invariant #5), pinned by `RestrictionInvariantTests`.

Any regression in either is a *security* regression: it re-opens the operational-visibility attack
surface that the whole argument closes. The validation plan (Section 9) treats a breach of these
tests as release-blocking on security grounds, not only restriction grounds.

---

## 7. The seams that close the gaps (now built)

Each item below was specified as an implementable seam in earlier editions and is now built: the types (named to the
existing `Diagnostics` / `Estimation` / `Security` conventions -- records for value bundles, `sealed` classes for
stateful services, enums by name), their namespace, the restriction constraint each honours, and the test that
evidences it. No seam weakens the guard or emits future-dated state.

### 7.1 Incident reporting hooks (CAF C1, D1; NIS Regulation 11)

**Obligation.** An OES must notify Ofgem of a significant-impact incident within 72 hours. A
component embedded in the OES must be able to *raise* an internal signal the OES's reporting process
can act on, with enough structured detail to assess significance -- and must do so without leaking a
grid claim newer than the horizon.

**What it meets.** GridSim *detects* candidate events (`FdiaScreenReport.SuspectedFdia`,
`EstimationStatus.Infeasible`, an ingestion `ForwardInferenceException`, an output-guard refusal, a quarantined live
window, a breached service-level objective), records each as its own audit entry at the detection site (section 5.2),
and now surfaces them as structured incidents. A log that fails `AuditLog` verification is refused before the run
starts (CLI exit 1, web start-up failure) and reported on stderr; that event is not written into the same log, whose
chain cannot be trusted.

**Built** (`src/GridSim.Core/Diagnostics/Incident/`, namespace `GridSim.Core.Diagnostics.Incident`):

- `SecurityEventKind` and `SecuritySeverity` (in `GridSim.Core.Diagnostics`) type and grade each event;
  `SecuritySeverity.Significant` is the NIS-relevant threshold the OES maps to a Regulation 11 assessment.
- `IncidentReport` carries a deterministic content-derived id, the raise-time run metadata, an optional
  `StateTimeUtc` (the historical grid instant, horizon-checked at construction so it can never be newer than the
  horizon), a severity, a summary and the audit hash that links the incident to the tamper-evident entry evidencing it.
- `IIncidentSink` -- GridSim ships `NullIncidentSink`, `BufferedIncidentSink` and `JsonLinesIncidentSink` (one JSON
  line per incident, flushed to disk, the file the OES points its SOC, ticketing or notification process at); an OES
  may supply its own implementation in code.
- `IncidentRouter` maps detections to `IncidentReport`s via a documented policy (`IncidentPolicy.Classify`, with
  per-kind severity and burst thresholds: cumulative counts of events of a kind in the run, not rates over a time
  window), off wall-clock so the resident loop stays deterministic.
- `SecurityIncidentSink` (an `IAuditSink`) raises one incident for each single security event whose baseline severity
  is Significant or above (`SuspectedFdia`, `RestrictionRefused`, `DistrustedSource`, `AvailabilityDegraded`,
  `DependencyAlert`).
- `SecurityEventMonitor` (an `IAuditSink`) tallies security-event kinds and escalates a burst to one latched incident
  (default thresholds 20 `HorizonViolation`, 3 `SuspectedFdia`, 1 `AuditChainBroken`), meeting the v4.0
  threat-hunting expectation.
- **Wiring.** `--incident-log <file>` (CLI) or `Audit:IncidentLogPath` (web) attaches both sinks to the product audit
  log through `IncidentWiring` and writes the incidents to `<file>`; it needs `--audit-log` / `Audit:LogPath`. Each
  incident names, by hash, the audit entry that evidences it; incidents are not themselves written into the audit
  chain. A failure to write an incident never stops the run; the CLI reports it at the end and exits 1 (the web host does not).

**Restriction constraint.** An incident's detail for a data-integrity event may quote the offending *historical*
measurement, never a reconstructed present/future state; `StateTimeUtc` is guard-checked on construction. A horizon
violation carries the rejected stamp and the horizon, not the payload.

**Test.** `IncidentRouterTests`: a `SuspectedFdia` detection produces exactly one `Significant` incident carrying the
matching audit hash; a horizon-violation incident never carries a stamp inside the horizon; determinism (same window
stream -> identical incident sequence). `SecurityEventMonitorTests` pins the burst escalation. `AuditWiringTests` pins
the product wiring: a significant event raises one incident, a routine one raises none, a burst is escalated once, and
the incident file holds one JSON line per incident. The plan these feed is
exercised as a tabletop and extends to suppliers (the GDA data lake), per [`incident-response.md`](incident-response.md).

### 7.2 Access control (CAF B2 Identity and access control; B3 in transit)

**Obligation.** Authenticate and authorise access to the system and its data. The exposed surface is
the `GridBridgeClient` wire (`HandleState` is a guard seam but trusts its transport) and, in the web host, the
`/api` and `/ws/sim` endpoints and session access to estimates.

**What it meets.** The web host authenticates with a sign-in cookie and a single sign-on consumer (single-use,
audience-checked tickets), and the production configuration runs replay-only, which leaves `/api` and `/ws/sim`
unmapped -- the shipped exposure control. The role-based access-control gate is built and tested as a primitive, but
no hosting surface enforces it at this release, so B2 is Partial: nothing is authorised by role and no denial is
audited until it is wired.

**Built** (`src/GridSim.Core/Security/`, namespace `GridSim.Core.Security`):

- `OperationRole` (`Viewer`, `Analyst`, `Operator`, `Administrator`), `IdentityContext` (principal id, role,
  correlation id, taken from the cookie or SSO claims), `RoleGuard.Require(IdentityContext, OperationRole)` throwing
  `AccessDeniedException`, and an audited overload that writes a `SecurityEventKind.AccessDenied` entry for each deny
  (so that, once wired, B2 decisions are C1-observable and D1-recoverable).
- This is the authorisation *mechanism* intended for the solve surface when that surface is exposed; at this release no
  endpoint, CLI verb or hosted service calls it, so it does not authorise `/api` or `/ws/sim` (existing constitutional
  tests reach the solve surface without a role, and the shipped exposure control there is the replay-only gate).
  Closing the gap means wiring it into those surfaces with the host's audit writer.
- Transport: `GridBridgeClient` refuses an unencrypted `ws://` endpoint unless explicitly allowed (B3 transport); on
  the web host a `ws://` bridge endpoint stops start-up unless `GridSim:BridgeAllowUnencrypted=true` (desktop:
  `BridgeAllowUnencrypted`). Carrying mutual TLS or a signed-token scheme on the bridge wire is a deploying-party
  transport choice.

**Restriction constraint.** Access control does not relax the guard: even an authorised Administrator cannot submit
sub-horizon data, because the guard consults no role and has no bypass, and it caps what any caller can learn
(week-stale reconstruction only) regardless of role. B2 and the horizon invariant are independent gates.

**Test.** `RoleGuardTests`: each role against each operation; a deny is audited; an authorised caller submitting a
sub-horizon window is still rejected by the guard, and no role reaches sub-169h data. These test the primitive, not a
product path. `BridgeEndpointSettingTests` pins the `ws://` opt-in setting.

### 7.3 Supply-chain assurance (CAF A3 Asset management, A4 Supply chain)

**Obligation.** The OES must assure the provenance and integrity of third-party components. As the
component, GridSim must be able to enumerate and attest its own dependencies and build.

**What it meets.** Besides its *data* provenance model (`ProvenanceTag`), GridSim's release workflow builds two
CycloneDX SBOMs (CLI and web host) and attaches them to each GitHub Release; CI and the release fail on a
known-vulnerable package; Dependabot runs and CodeQL is run by hand; the Windows CLI binary and the NuGet package are
signed when the signing secret is configured (with a self-signed certificate that the verify steps trust only by its
pinned thumbprint; when the secret is absent the release is unsigned, with a warning). Every file on the GitHub Release
gets a signed build-provenance attestation. The lock files, build provenance and runtime accessor are now built;
running the workflow on a GitHub tag so the artefacts are published remains a deploying-maintainer action, and the
CI-at-release cadence is a risk-accepted deviation (see `caf-mapping.md` section 4.4).

**Built** (namespace `GridSim.Core.Diagnostics.SupplyChain`):

- The CycloneDX **SBOMs** enumerate every NuGet dependency with its version from the .NET restore graph, attached to
  each release (not committed, where a dependency update would leave a committed copy stale). Per-project
  `packages.lock.json` lock files (`RestorePackagesWithLockFile=true` in `Directory.Build.props`) record the restore
  graph. No CI or release restore runs in locked mode, so a restore that resolves differently rewrites a lock file
  rather than failing, and the SBOM job restores on its own; the SBOM matches the shipped build while both resolve the
  committed lock files, and nothing yet makes a divergence fail the release.
- `BuildProvenance` exposes version, commit, toolchain (the .NET SDK version stamped at build time), SBOM hash and
  deterministic build hash at runtime, so an emitted `EstimationResult` / `AuditLogEntry` correlates to the build that
  produced it. The release generates the SBOMs first and builds the CLI, the web image and the NuGet package with the
  SBOM hash injected (`-p:GridSimSbomSha256=<hash>`); each CLI binary must report `release build  yes` and that exact
  hash from `gridsim compliance verify`, or the release stops. The build hash is an identity label over those metadata
  strings, not an integrity proof: it covers no artefact bytes.
- **Integrity of a download.** The signed SLSA build-provenance attestation over every Release file
  (`actions/attest-build-provenance`, Sigstore; `gh attestation verify <file> -R <owner>/GridSim`), `SHA256SUMS.txt`,
  and the Authenticode signature on the Windows binary.
- **A4.b secure-SDLC attestation (new v4.0 outcome).** `ComplianceManifest` is the machine-readable control inventory,
  each control bound to its evidencing test, with statuses maintained by hand in source (an inventory, not a
  measurement); `ComplianceAttestation` binds the manifest hash to the build identity. `IsCompliant` needs both a
  release build (SBOM hash injected, which the release does only after the full CI matrix passed) and every in-scope
  control recorded Achieved. A local or development build reports `NOT ATTESTED` and `gridsim compliance verify` exits
  1; the report and the web `/compliance` endpoint list each control not recorded Achieved. At this release `rbac`,
  `distrusted-source` and `resilience` are recorded Partially achieved, so even a release build reads `NOT COMPLIANT`.
  The development process maps to NIST SSDF (SP 800-218) and the Microsoft SDL (`ssdf-mapping.md`), and to the SLSA
  build track (`slsa-provenance.md`).
- A `DependencyAlert` security event would be raised as an incident (7.1), but no product path produces one at this
  release; known-vulnerable dependencies are caught by the vulnerable-package gate and Dependabot instead.

**Restriction constraint.** None directly; supply-chain artefacts describe software, not grid data. The generator runs
inside the zero-warning build (Section 9) and makes no network call at runtime.

**Test.** `SupplyChainManifestTests`: the build hash is deterministic and changes with the SBOM hash, and a local build
carries no injected SBOM hash and is not a release build. `ComplianceAttestationTests`: a local build never attests
compliance, the verdict needs a release build and every in-scope control Achieved, exactly the controls with a named
gap are not Achieved, and every named evidence test class exists. `WorkflowReleaseGateTests`: the SBOM hash is
injected into every shipped build and checked, every publishing job waits for the CI matrix, Release files get a
provenance attestation, and signing trusts only the pinned certificate. The release step that checks each binary's
reported SBOM hash is the evidence that a shipped binary carries it. `WorkflowContractTests` and
`ThirdPartyNoticesTests` pin the release workflow's SBOM and signing steps and the completeness of
`THIRD-PARTY-NOTICES.md`.

### 7.4 Availability SLOs and resilience (CAF B5 Resilient networks and systems)

**Obligation.** Maintain the availability of the service under adverse conditions, to a stated
objective, and evidence it.

**What it meets.** `RealtimeEstimatorEngine` has meaningful resilience: `ContinueOnError`
records-and-skips a malformed window or a non-converged estimate rather than tearing down the loop,
and `RealtimeRunSummary` reports processed/converged/rejected-fresh/failed counts. The web host caps concurrent
sessions (`GridSim:MaxSessions`) and the container health check probes it. The explicit service-level objective, the
in-application health probe and the resource limits are now built and applied; the bounded reconnect schedule is built
but no host drives it yet, so B5 is Partial.

**Built** (`GridSim.Core.Net` and `src/GridSim.Core/Simulation/Health/`, namespace `GridSim.Core.Simulation.Health`):

- `ResilienceOptions` (`GridSim.Core.Net`): the resource limits (bridge message cap, in-flight-query cap, measurement
  cap) and the `ws://` transport policy, applied by `ApplyTo` to every bridge client the web and desktop hosts create;
  and a bounded reconnect backoff schedule (`BackoffDelay`). **Gap:** no host drives the schedule, so there is no
  supervised auto-reconnect loop yet.
- `ServiceLevelObjective` (for example converged/processed >= 0.99) and `HealthProbe`, which folds the engine's
  `RealtimeRunSummary` into a `HealthReport`. The counters are cumulative over the whole run, not a rolling window, so a
  long healthy run can mask a recent run of failures.
- An SLO breach at the end of a `gridsim serve` run (with `--audit-log`) or of the web resident estimator's run (with
  `Audit:LogPath`) is recorded as an `AvailabilityDegraded` security event, which becomes an incident when
  `--incident-log` / `Audit:IncidentLogPath` is set (7.1). The `/health` endpoint itself raises nothing; it answers 503
  on a breach. There is no degraded-mode fallback to a last-good estimate: a failing window is skipped and counted, and
  the resident loop carries on (`RealtimeEstimatorOptions.ContinueOnError`). Capacity SLOs by scale come from the benchmark corpus (`docs/developer/benchmarks/benchmark-results.md`): the
  estimator's dense/sparse crossover and per-window solve time bound the achievable window latency.

**Restriction constraint.** Availability of a *historical* estimator is a resilience property of an
offline/replay pipeline; nothing here streams live data. Latency is measured against window *arrival*, not against real
time, so meeting a tight latency SLO can never imply nowcasting, and staleness is already the normal state (>=169h) so
the degraded fallback never risks fresher-than-permitted data.

**Test.** `HealthProbeTests`: a low converged fraction, a high rejected-fresh fraction or a worst-window latency beyond
the objective breaches it, and the verdict is deterministic for the same input. `AuditWiringTests` pins that a security
event is recorded as its own entry and that a significant one raises one incident (generic over the event kind; there
is no SLO-specific incident test). `ResilienceOptionsTests` pins the backoff schedule and the resource caps;
`BridgeEndpointSettingTests` pins the `ws://` opt-in setting.

---

## 8. Honest gap table

Ranked by NIS/CAF materiality for a component embedded in an OES. "Owner" distinguishes what GridSim
must build from what the deploying OES must supply.

| # | CAF outcome | Obligation | Current status | Residual gap | Owner | Priority |
|---|---|---|---|---|---|---|
| 1 | B2 Identity & access control | Authenticate/authorise access to system and data | **Partial** -- web host sign-in, SSO, replay-only (shipped exposure control); `RoleGuard` authorisation gate built and tested as a primitive, enforced by no hosting surface (7.2) | Wire the gate into the solve and resident-estimator surfaces; bridge mTLS is a deployment choice | GridSim + OES | open |
| 2 | D1 / C1 incident response | Regulation 11 significant-incident notification within 72 h | **Met (in-component)** -- `IncidentRouter` + `IIncidentSink`, wired by `--incident-log` / `Audit:IncidentLogPath`, severity threshold, tabletop and GDA-lake suppliers in scope (7.1); distrusted-source containment is a primitive only | OES sets the incident log, routes it to its reporting process and files the Regulation 11 report; GridSim to wire `DistrustedSources` | GridSim (hook), OES (report) | closed in-component except containment |
| 3 | A4 Supply chain | Assure third-party components | **Met (in-component)** -- SBOMs injected into the shipped builds, lock files, `BuildProvenance`, signed provenance attestation, vulnerable-package gate, signing with a self-signed pinned certificate (7.3) | Run the first tagged release to publish the artefacts; Actions are referenced by version tag, not SHA | GridSim | closed in-component |
| 3b | A4.b Secure software development and support (new in v4.0) | Attest software authenticity and integrity | **Met (mechanism)** -- provenance attestation, `SHA256SUMS.txt` and Authenticode for download integrity; `ComplianceManifest` + `ComplianceAttestation` bound to the build identity (7.3) | The verdict reads NOT COMPLIANT until rows 1, 2 (containment) and 4 are closed | GridSim | open (verdict) |
| 4 | B5 Resilience | Availability to a stated SLO, evidenced | **Partial (in-component)** -- resilient loop, session cap, `ResilienceOptions` limits on every bridge client, `ServiceLevelObjective` + `HealthProbe` with an SLO breach recorded as a security event (7.4) | No host drives the reconnect backoff schedule; edge rate limiting at the reverse proxy | GridSim + OES | open |
| 5 | C1 / C2 detection | Monitor for and discover security events | **Met** -- FDIA (both sub-classes) + bad-data + per-security-event audit at the audited detection sites, optional signed anchor, JSON-lines forwarding + `SecurityEventMonitor` (7.1) | No product detection site yet for `AccessDenied`, `DistrustedSource`, `DependencyAlert` | GridSim | closed |
| 6 | B3 Data security (transit) | Protect data in transit; ensure integrity | **Met (in-component)** -- output integrity via the anchored audit trail (optionally signed); key ring encrypted at rest; `ws://` refused unless allowed; `ISecretStore` with a secrets-directory backend | Bridge transport TLS is deploying-party; no OS-keychain or vault backend ships | GridSim + OES | closed in-component |
| 7 | B4 System security | Reduce attack surface; validate inputs | **Met (core)** -- guard + validation + fail-loud + resource limits | Maintain | GridSim | maintain |
| 8 | A3 Asset management | Know data and dependency inventory | **Met** -- data provenance strong; software inventory, lock files and build provenance (7.3) | Run the first tagged release to publish the SBOMs | GridSim | closed in-component |
| 9 | A2 Risk management | Documented component risk assessment and threat-actor view | **Met** -- restriction docs, this document and `risk-register.md` | -- | GridSim + OES | closed in-component |
| 10 | Confidentiality of operational picture | Protect a live operational view | **Met by design** -- 169 h guard removes the asset (Section 6) | Maintain guard universality as a security control | GridSim | maintain |
| 11 | A1/B1/B6 governance & culture | Organisational security management | **OES** -- in-component governance in `security-governance.md` | Out of component | OES | n/a |

Three cells are stated as strengths rather than gaps and must be represented that way to an assessor:
row 7 (B4 core), row 10 (the horizon guard's design-out of the operational-visibility asset), and the position that
every GridSim-owned control is Met with a named test except the three open rows above (B2 role enforcement, D1
distrusted-source containment, B5 reconnect), each a built primitive that no product path applies yet. Row 10 is
contingent on the Section 9 tests staying green. Full CAF compliance *as* an Operator of Essential Services is not
achievable by a component (row 11, host OS, perimeter and physical security are the deploying party's); full
*in-component* compliance is the aim, and the compliance attestation (7.3) reads NOT COMPLIANT until those three rows
are closed.

---

## 9. Validation and test plan

The assurance claims in this document are only credible if they are falsifiable in the build. This
section specifies the checks; all are additive to the existing suite and all must pass inside the
**zero-warning gate** (`dotnet build -warnaserror` / `TreatWarningsAsErrors`), which is a release
condition -- a new assurance type that trips an analyser warning does not ship.

### 9.1 Restriction-as-security invariant tests (release-blocking)

Extend `RestrictionInvariantTests` (and keep `ForwardInferenceGuardTests`, `GdaReplayLoopTests`
green) so that every seam introduced in Section 7 is proven not to weaken the guard:

- **Guard universality:** every dated seam rejects a stamp of `UtcNow - 169 h + 1 s`. Parametrise over all seams so a
  newly added ingester cannot forget the guard.
- **Access control is additive to the guard:** the authorisation gate never relaxes the guard. An authorised caller
  submitting sub-horizon data is still rejected, and no role reaches sub-169h data (`RoleGuardTests`, 7.2).
- **No future-dated emission:** no `IncidentReport`, `HealthReport`, `EstimationResult` or
  `AuditLogEntry` carries a `StateTimeUtc` newer than its newest input (invariant #2), asserted by a
  property test over random guard-permitted window streams.
- **DSE gate intact:** `RestrictionReview.AssertPermitted` still fails loud for an unregistered
  dynamics-fused method; the code table `RestrictionReview.Reviewed` is the register.

A failure of any of these is treated as a **security** regression (Section 6.3), not merely a
restriction one.

### 9.2 Machine-epsilon cross-checks against an independent reference

The estimator's correctness underpins the FDIA screen and every downstream assurance claim, so it is
pinned against an independent implementation to machine precision, exactly as the existing solver
validation does (`docs/validation/solver-validation-report.md`, `tools/crossval/crossval_estimation.py`):

- **Reference:** an independent WLS state estimator (e.g. PYPOWER/pandapower/MATPOWER-style) run on
  the same standard cases (IEEE 14/30/57/118/300) with identical measurement sets and sigmas.
- **Tolerance:** agreement of the converged state `(Vm, VaDeg)` and objective `J(x_hat)` to a tight
  bound, so a numeric drift that could mask or fake an FDIA-screen verdict is caught. The measured agreement against
  pandapower is reported in technical ch. 11 (5.6e-12 pu).
- **Assurance linkage:** because `FdiaScreen` reuses `PhysicsLaws.Audit` on the *projected* state
  (`EstimationResult.ToPowerFlowResult`), the same cross-check validates the input to the security
  screen. A green cross-check is the evidence that the C1/C2 detector is operating on a correct state.

### 9.3 Determinism

Every assurance surface must be deterministic so that audit and incident evidence is reproducible:

- Same measurement-window stream + same options -> identical `EstimationResult` sequence, identical
  `AuditLog` hash chain, identical `IncidentReport` sequence, identical `HealthReport` verdicts.
- The horizon guard is exercised with an injected `UtcNow` (the `RealtimeEstimatorOptions.UtcNow`
  seam already exists) so the boundary is fixed and tests never depend on the wall clock.
- In place: `EstimatorDeterminismTests` (estimates bit-identical run to run on each backend),
  `GdaReplayDeterminismTests` (replay bit-identical run to run), `EstimationClockIndependenceTests` (no estimation code
  reads the clock) and `RealtimeEstimatorEngineTests` (a deterministic stream).
- Golden-file tests over `AuditLog.Verify` and a fixed incident stream detect any nondeterminism in
  hashing, ordering, or JSON serialisation of the new records.

### 9.4 Supply-chain and build-gate checks

- SBOM: built from the restore graph by the release workflow, so it cannot go stale relative to the release it
  describes (7.3); the `packages.lock.json` lock files pin the graph itself.
- The release injects the SBOM hash into every shipped build and stops unless each CLI binary reports exactly that
  hash as a release build (`WorkflowReleaseGateTests` pins the steps); a local build carries none and never attests
  compliance (`SupplyChainManifestTests`, `ComplianceAttestationTests`).
- Zero-warning gate: all analysers/nullable/style warnings are errors; the assurance code must
  compile clean under the same `-warnaserror` the rest of GridSim uses.

### 9.5 Traceability matrix (kept in-repo)

The matrix mapping each CAF outcome to the type and the tests that evidence it is `caf-mapping.md` section 8, so an
Ofgem/NCSC assessor can walk from an outcome to a passing, deterministic test. It is updated with each seam.

---

## 10. Restriction-invariant conformance summary

Every element introduced or referenced here honours `UnderstandingRestrictions.md`:

- **Invariant #1 (no sub-horizon ingest):** the guard remains universal; the authorisation gate is additive and never
  sits around the guard, so no role reaches sub-horizon data (7.2, 9.1).
- **Invariant #2 (no future-dated output):** incident, health, and audit records carry `RunTimestamp`
  as run metadata and `StateTimeUtc` as a historical grid instant only; property-tested (9.1).
- **Invariant #3 (solver trajectories labelled model output):** untouched -- this document adds no
  solver path.
- **Invariant #4 (real-time on replay/permitted-age only):** the SLO/health work measures latency
  against window arrival, not real time, so meeting it can never imply nowcasting (7.4).
- **Invariant #5 (Dynamics -> Estimation gated):** `RestrictionReview` gate unchanged and re-pinned
  as a security control (6.3, 9.1).

The distinctive contribution to the security posture is that the 169 h guard is not just a safety
rule; it removes the highest-value confidentiality asset an attacker would target in a state
estimator, and this document elevates its enforcement tests to release-blocking security tests.

---

## 11. Delivery summary

Each seam in Section 7 is built, with its test, inside the zero-warning gate, and none alters the estimator numerics
or the guard floor. Items 1, 4 (reconnect) and 6 (distrusted source) are primitives that no product path applies yet:

1. **B2 authorisation gate + B3 transport (7.2).** `OperationRole`, `IdentityContext`, `RoleGuard`,
   `AccessDeniedException`; `SecurityEventKind.AccessDenied` on deny (gate not yet enforced by any host); `ws://`
   refused unless `GridSim:BridgeAllowUnencrypted=true`. Tests: `RoleGuardTests`, `BridgeEndpointSettingTests`.
2. **Incident hooks (7.1).** `IIncidentSink`, `IncidentRouter`, `IncidentPolicy`, `SecurityIncidentSink`,
   `SecurityEventMonitor`, `JsonLinesIncidentSink`; Regulation 11 severity mapping; wired by `--incident-log` /
   `Audit:IncidentLogPath`. Tests: `IncidentRouterTests`, `SecurityEventMonitorTests`, `AuditWiringTests`.
3. **SBOM, lock files, build provenance and A4.b attestation (7.3).** `packages.lock.json`, `BuildProvenance`, SBOM
   hash injected into every shipped build, signed provenance attestation, `ComplianceManifest` +
   `ComplianceAttestation`. Tests: `SupplyChainManifestTests`, `ComplianceAttestationTests`, `WorkflowReleaseGateTests`.
4. **Availability SLOs and resilience (7.4).** `ResilienceOptions` (limits applied; reconnect schedule not driven),
   `ServiceLevelObjective`, `HealthProbe`, `AvailabilityDegraded` recorded on an SLO breach. Tests: `HealthProbeTests`,
   `ResilienceOptionsTests`, `AuditWiringTests`.
5. **Per-event audit, signed anchor and secrets (C1/B3).** `AuditRunRecorder.RecordSecurityEvent` /
   `SecurityAudit.Record`, `JsonLinesAuditSink` forwarding, `SignedAuditAnchor` wired through the CLI flags and web
   settings, `ISecretStore` with `DirectorySecretStore` (`Secrets:Directory`). Tests: `SecurityAuditTests`,
   `SignedAuditAnchorTests`, `AuditWiringTests`, `SecretStoreTests`, `DirectorySecretStoreTests`,
   `NoSecretInAuditTests`.
6. **Governance, risk and incident documents (A1/A2/D1/D2).** `security-governance.md`, `risk-register.md`,
   `incident-response.md`; distrusted-source control (`DistrustedSources`, `DistrustedSourceTests`; primitive only).

The remaining GridSim code items are the three primitives above. The deploying-party actions are: run the first
tagged release; point `--audit-forward` / `Audit:ForwardPath` at a WORM mount or SIEM collector and `--incident-log` /
`Audit:IncidentLogPath` at the reporting process; keep the anchor-signing private key off the host where the threat
model needs it; provide a Linux key-ring certificate; apply edge rate limiting; exercise the response plan as a
tabletop.

---

## 12. References

- `UnderstandingRestrictions.md` -- the governing no-forward-inference document (the one rule, the
  five testable invariants, the 169 h rationale).
- `src/GridSim.Core/Diagnostics/RestrictionReview.cs` -- the Dynamics -> Estimation review gate (invariant #5).
- `src/GridSim.Core/Diagnostics/ForwardInferenceGuard.cs` -- the 169 h horizon guard.
- `src/GridSim.Core/Diagnostics/AuditLog.cs` -- the tamper-evident, hash-chained audit trail.
- `src/GridSim.Core/Diagnostics/Provenance.cs` -- the `ProvenanceTag` trust taxonomy.
- `src/GridSim.Core/Diagnostics/RestrictionReview.cs` -- the code-resident DSE review gate.
- `src/GridSim.Core/Estimation/Security/FdiaScreen.cs` -- the residual-cloaked FDIA screen.
- `src/GridSim.Core/Estimation/WlsStateEstimator.cs`, `MeasurementModel.cs`, `MeasurementSet.cs`,
  `EstimationResult.cs` -- the estimator, measurement equation, input, and result contract.
- `src/GridSim.Core/Simulation/RealtimeEstimatorEngine.cs` -- the resident, guard-safe streaming loop.
- `docs/validation/solver-validation-report.md`, `tools/crossval/crossval_estimation.py`,
  `docs/developer/benchmarks/benchmark-results.md` -- the machine-epsilon cross-check and capacity evidence base.
- NIS Regulations 2018 (Regulations 10, 11, 15-18); NCSC Cyber Assessment Framework (Objectives
  A-D); Ofgem energy-sector CAF profile for downstream electricity.
