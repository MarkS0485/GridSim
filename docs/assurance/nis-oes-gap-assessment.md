# NIS Regulations / Operators of Essential Services -- Gap Assessment

**Status:** assessment as at GridSim 2.1.010 (reviewed 2026-11-01, first against 2.1.005 and again after the 2.1.008 to
2.1.010 fixes; release candidate RC-1 was released on 2026-10-31 as 2.1.000), against the NCSC Cyber Assessment
Framework v4.0 (released 6 August 2025). Types are named as they are in the code at this release. The seams described in
Section 7 are now built: the types, namespaces and tests named there exist unless the text marks an item as a
deploying-party action. Three of them are marked Partial below: the `RoleGuard` authorisation gate, which the web
host enforces on its REST and WebSocket solve surface since 2026-11-01 but not on the dashboard's own sessions, and the
`DistrustedSources` control, wired into `estimate`, `serve` and the web host since 2026-11-01 and held Partial until
the release test run confirms it, and the supervised bridge reconnect, which the web host and the desktop app apply
since 2026-11-01 only when switched on (off by default) and which a release test run has not yet confirmed. The
supply-chain and secure-development rows are also Partial at this release, because RC-1 was built, signed and published
from the owner's build machine rather than by the gated release workflow (Section 7.3, and `caf-mapping.md` sections 4.4
and 4.5); since 2.1.008 the code's control inventory records those two controls as Partially achieved too. Per-control
CAF statuses are kept in `caf-mapping.md`; where this document and the CAF mapping cover the same control, the CAF
mapping row is the status of record.

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
support** (partly met: the compliance attestation is built, but the release provenance the workflow is written to
produce did not exist for RC-1, section 7.3); expanded **monitoring and threat hunting**
under C1/C2 (met through per-security-event audit entries and the `SecurityEventMonitor` aggregation, section 7.1); and
a strengthened **D1** that requires tested response plans extending to suppliers (section 7.1). v4.0 also weaves
AI-related risk through several outcomes; GridSim is a deterministic, non-AI and non-ML state estimator (its numerics
are bit-reproducible and contain no model inference), so those AI-specific risks do not arise in the engine. The one
adjacent concern, a future dynamics-into-estimation predictor, is held off by the `RestrictionReview` gate (Section
5.4).

### 2.3 What "component inside an OES" changes

GridSim is a .NET 10 library plus CLI plus web host, with two desktop apps (WPF on Windows, Avalonia on Windows and
Linux) shipped at RC-1. It is not an operator under licence and does not
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
| A3 Asset management | Know what data and dependencies exist | Met for data; Partial for software | `ProvenanceTag` taxonomy; `packages.lock.json` lock files; `BuildProvenance`; one CycloneDX SBOM on the RC-1 Release, for the CLI only (the desktop apps and the NuGet package have none); the workflow is written to publish five, each stamped into its artefact, and has not yet been run (7.3) |
| A4 Supply chain | Assure third-party components and dependencies | Partial | Lock files, build provenance, NuGet audit as a build error, CodeQL `security-extended` (by hand), Dependabot, the three RC-1 Windows executables and the NuGet package signed with a self-signed pinned certificate; the workflow's SBOM injection, provenance attestation, vulnerable-package gate, CI gate and reproducible-build check did not run for RC-1 (7.3) |
| A4.b Secure software development and support (new in v4.0) | Run a secure-development framework; attest software authenticity and integrity | Partial; verdict NOT COMPLIANT at this release | NIST SSDF / Microsoft SDL mapping; download integrity from Authenticode, the NuGet signature and `SHA256SUMS.txt` (no provenance attestation exists for RC-1); `ComplianceManifest` + `ComplianceAttestation` bound to the build identity, compliant only for a release build with every in-scope control recorded Achieved, where since 2.1.008 a release build needs a 64-hex SBOM hash, a known commit and a clean source tree (a local build of a clean tree can still meet that); support period, CVSS-based remediation target and advisory process stated in `SECURITY.md` (2026-11-01), not yet exercised by a release (7.3) |
| B2 Identity and access control | Authenticate and authorise access to the system and data | Partial | Web host sign-in and SSO consumer, replay-only gate (the shipped exposure control), `/ws/sim` origin check (`GridSim:AllowedOrigins`) behind the `AllowedHosts` host list, and browser security headers with HSTS (since 2.1.008); `pmu-ingest` UDP and `interpose` listen on loopback by default; the `RoleGuard` authorisation gate is enforced by the web host on `POST /api/solve` and every `/ws/sim` control since 2026-11-01, each denial audited when the host has an audit log, but the dashboard's own sessions are not role-gated (7.2); bridge mutual-TLS remains a deploying-party transport choice |
| B3 Data security | Protect data at rest and in transit; ensure integrity | Met (in-component) | Audit chain persisted and anchored, with an optional signed anchor (output integrity); key ring encrypted at rest (DPAPI, or a certificate); `ISecretStore` with a secrets-directory backend; `ws://` refused unless allowed; bridge transport TLS is deploying-party |
| B4 System security | Reduce the attack surface; validate inputs | Met (core) | Guard + `MeasurementSet.Validate` + fail-loud numerics + resource limits (`ResilienceOptions`, applied to every bridge client; C37.118 non-DATA frame cap; usage-log rotation) |
| B5 Resilient networks and systems | Maintain availability under adverse conditions | Partial (in-component) | `RealtimeEstimatorEngine` continue-on-error resilience; session cap; `ResilienceOptions` limits; `ServiceLevelObjective` + `HealthProbe`, an SLO breach recorded as a security event; `BridgeReconnectSupervisor` drives the reconnect backoff schedule in the web host and the desktop app, off by default and not yet confirmed by a release test run (7.4) |
| C1 Security monitoring | Collect and analyse data to detect events | Met | `FdiaScreen`, chi-square/LNR bad-data, persisted, anchored audit trail with one entry per security event at the audited detection sites, optional signed anchor; JSON-lines forwarding (`--audit-forward`, `Audit:ForwardPath`) (7.1) |
| C2 Proactive event discovery | Actively search for indicators not caught by monitoring; threat hunting | Met | FDIA physical-plausibility screen; `SecurityEventMonitor` aggregation and burst escalation, attached by `--incident-log` / `Audit:IncidentLogPath` (7.1) |
| D1 Response and recovery | Respond to and recover from incidents; tested plans extending to suppliers | Met (in-component); distrusted-source containment Partial; plan unexercised | `IncidentRouter` + `IIncidentSink`, wired through `--incident-log` / `Audit:IncidentLogPath`; `DistrustedSources` (applied by `--distrust` / `Realtime:DistrustedSources` since 2026-11-01, held Partial); `incident-response.md` (GDA-lake suppliers in scope; the CVE tabletop script `tabletop-cve-exercise.md` is written, and no tabletop has been held as at 2026-11-01) (7.1) |
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
never silently rejects. This is a genuine C1/C2 contributing outcome, with both sub-classes now covered. Where several
meters measure one quantity, the margin uses the tightest meter, for bus quantities and, since 2.1.010, for the flow
on a single line too (parallel circuits keep the widest circuit's bound), so a loose second meter cannot widen the
margin enough to hide a rating breach (`BugHunt1101WebTests`, `B189_*`).

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
  chain. A failure to write an incident never stops the run; the CLI reports it at the end and exits 1, and the web host
  reports it on stderr when the resident run ends or fails.

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
unmapped -- the shipped exposure control. Since 2026-11-01 the web host enforces the role-based access-control gate on
its REST and WebSocket solve surface and audits each denial when it has an audit log. B2 stays Partial for two reasons:
the Blazor dashboard creates its own sessions with no role check, and a web host without the resident estimator has no
audit log, so its denials are refused but not audited.

**Built** (`src/GridSim.Core/Security/`, namespace `GridSim.Core.Security`):

- `OperationRole` (`Viewer`, `Analyst`, `Operator`, `Administrator`), `IdentityContext` (principal id, role,
  correlation id, taken from the cookie or SSO claims), `RoleGuard.Require(IdentityContext, OperationRole)` throwing
  `AccessDeniedException`, and an audited overload that writes a `SecurityEventKind.AccessDenied` entry for each deny.
- Enforced by the web host (`GridSim.Web.Site.AccessControl`, since 2026-11-01): `POST /api/solve` needs Analyst,
  `GET /api/topology` Viewer, and every `/ws/sim` control frame (start, play, pause, step, seek, speed, trip) Operator.
  A signed-in caller holds the highest band among its `GridSim.<role>` role claims (the prefix keeps an estate-wide role
  for another application from granting control of GridSim); any other caller holds `GridSim:AnonymousRole`, which
  defaults to Operator on an instance that answers only loopback names (the documented single-user use) and to Viewer
  on any other (fail-safe), and stops start-up when it is not a band name. A denial is refused before the action runs
  (403, or an error frame with the session left as it was) and written as one `SecurityEventKind.AccessDenied` entry
  through the host's audit writer, so the forwarder carries it (it raises no incident on its own: it is graded Low
  and has no burst threshold); a host with no audit log still
  refuses. The resident estimator has no control surface on the web host (it is configured and runs once at start-up),
  so there is nothing there to authorise. Still open: the dashboard's sessions, and auditing on a host without the
  resident estimator.
- Transport: `GridBridgeClient` refuses an unencrypted `ws://` endpoint unless explicitly allowed (B3 transport); on
  the web host a `ws://` bridge endpoint stops start-up unless `GridSim:BridgeAllowUnencrypted=true` (desktop:
  `BridgeAllowUnencrypted`). Carrying mutual TLS or a signed-token scheme on the bridge wire is a deploying-party
  transport choice.
- Exposure (since 2.1.008): the web host answers only the host names in `AllowedHosts` (default the loopback names),
  so a DNS-rebinding page gets a 400 before `/ws/sim` is reached, and every answer carries
  `Content-Security-Policy: frame-ancestors 'none'`, `X-Frame-Options`, `X-Content-Type-Options` and
  `Referrer-Policy`, with HSTS on HTTPS outside Development (the CSP covers framing only). `pmu-ingest --live
  udp://:port` and `interpose` listen on 127.0.0.1 by default; a listener reachable from other machines needs a named
  sender (`--live-udp-source`) or client (`--allow-client`), or an explicit opt-out flag, and `interpose` drops a
  client silent for `--idle-seconds` (default 120).

**Restriction constraint.** Access control does not relax the guard: even an authorised Administrator cannot submit
sub-horizon data, because the guard consults no role and has no bypass, and it caps what any caller can learn
(week-stale reconstruction only) regardless of role. B2 and the horizon invariant are independent gates.

**Test.** `RoleGuardTests`: each role against each operation; a deny is audited; an authorised caller submitting a
sub-horizon window is still rejected by the guard, and no role reaches sub-169h data. `CafB2RoleGateWebTests` tests
the product path through the real web host: the band of each surface, an allowed band passes, each denied band on each
surface is refused with no side effect, exactly one `AccessDenied` entry per denial (while the resident estimator's run
holds the log and after it has closed it), a host with no audit log still refuses, and the default anonymous role. `BridgeEndpointSettingTests` pins the `ws://` opt-in setting. `BugHunt1101WebTests` pins the host list,
the headers and the listener defaults (`B170_*`, `B179_*`, `B173_*`, `B174_*`).

### 7.3 Supply-chain assurance (CAF A3 Asset management, A4 Supply chain)

**Obligation.** The OES must assure the provenance and integrity of third-party components. As the
component, GridSim must be able to enumerate and attest its own dependencies and build.

**What it meets.** Besides its *data* provenance model (`ProvenanceTag`), GridSim records its restore graph in lock
files, names every shipped package and bundled font with its licence in `THIRD-PARTY-NOTICES.md`, fails every build on a
NuGet audit warning, runs Dependabot (with Dependabot security updates on the public repository) and runs CodeQL
(`security-extended`) by hand. The release workflow is written to build five CycloneDX SBOMs, all Release files (CLI, web host, GridSim.Core for the
NuGet package, and the Avalonia and WPF desktop apps), inject each artefact's own SBOM hash into its build (the NuGet
package carries the GridSim.Core SBOM's hash), gate on the full CI matrix, a vulnerable-package check and a byte-for-byte
rebuild of the Linux CLI, attest every Release file and the web image, and sign the Windows CLI, the Windows WPF and
Avalonia executables and the NuGet package. Since 2026-11-01 (GA-5, an owner decision) its ship scope includes the
desktop apps as RC-1 shipped them: the WPF app for win-x64 and the Avalonia app for win-x64 and linux-x64, with no
macOS desktop build. All of this is written, not yet run: no release has been made through the workflow. RC-1 did not go through that workflow: it
stopped in its first step on the RC tag form (accepted since 2.1.008), and the Release was built, signed and published
by hand from the owner's build machine. No release has been made through the workflow since. RC-1 therefore carries one
SBOM (the CLI's, its hash stamped into the CLI), signatures on the three Windows executables and the NuGet package
(self-signed certificate, checked by thumbprint) and a hand-made `SHA256SUMS.txt`, but no provenance attestation, no web
image and no SBOM for the desktop apps or the package (the workflow's SBOM coverage is closed in the workflow and not yet exercised by a release). CI and CodeQL run by hand only, a risk-accepted cadence (see
`caf-mapping.md` section 4.4); the CI runs started by hand after RC-1 was published did not pass.

**Built** (namespace `GridSim.Core.Diagnostics.SupplyChain`):

- The CycloneDX **SBOM** enumerates every NuGet dependency with its version from the .NET restore graph, attached to
  the release (not committed, where a dependency update would leave a committed copy stale). Per-project
  `packages.lock.json` lock files (`RestorePackagesWithLockFile=true` in `Directory.Build.props`) record the restore
  graph. Since 2.1.008 `global.json` pins the SDK to 10.0.401 with no roll-forward, every workflow and the container
  image build use exactly that SDK, and restores on GitHub Actions and in the image build run in locked mode, so there
  a restore that would change a lock file fails; the SBOM job and the publishing jobs therefore resolve the same
  committed lock files (`BugHunt1101WebTests`, `B161_*`). A restore on the owner's machine is not in locked mode.
- `BuildProvenance` exposes version, commit, toolchain (the .NET SDK version stamped at build time), SBOM hash and
  deterministic build hash at runtime, so an emitted `EstimationResult` / `AuditLogEntry` correlates to the build that
  produced it. The build hash is an identity label over those metadata strings, not an integrity proof: it covers no
  artefact bytes.
- **Integrity of a download.** For RC-1: the Authenticode signatures on the Windows executables, the NuGet author
  signature and `SHA256SUMS.txt` (with the SHA-256 values in `RC1.md`). The Linux files rest on the checksums alone.
  The signed SLSA build-provenance attestation (`actions/attest-build-provenance`, Sigstore;
  `gh attestation verify <file> -R <owner>/GridSim`) is written into the workflow and does not exist for any RC-1 file.
- **A4.b secure-SDLC attestation (new v4.0 outcome).** `ComplianceManifest` is the machine-readable control inventory,
  each control bound to its evidencing test, with statuses maintained by hand in source (an inventory, not a
  measurement); `ComplianceAttestation` binds the manifest hash to the build identity. `IsCompliant` needs both a
  release build and every in-scope control recorded Achieved. Since 2.1.008 a release build needs an injected SBOM
  hash (`-p:GridSimSbomSha256`) of exactly 64 hexadecimal digits, a known commit and a source tree recorded clean at
  build time (`BugHunt1101WebTests`, `B176_*`). That proves form and a clean tree, not that the workflow built the
  binary, so the flag implies a green CI matrix only when the workflow set it; the RC-1 binaries (2.1.000, before the
  check) got it from a local build. A build that is not a release build reports `NOT ATTESTED` and
  `gridsim compliance verify` exits 1; the report and the web `/compliance` endpoint show the source-tree state, say
  why a build is not a release build, and list each control not recorded Achieved. At this release `rbac`,
  `distrusted-source`, `resilience` and, since 2.1.008, `supply-chain` and `secure-sdlc-attestation` are recorded
  Partially achieved (13 of 18 in-scope controls Achieved), so even a release build reads `NOT COMPLIANT`; the
  manifest and this assessment now agree. The development process maps to NIST SSDF (SP 800-218) and the Microsoft SDL
  (`ssdf-mapping.md`), and to the SLSA build track (`slsa-provenance.md`).
- A `DependencyAlert` security event would be raised as an incident (7.1), but no product path produces one at this
  release; known-vulnerable dependencies are caught by NuGet audit in the build, the workflow's vulnerable-package gate
  when CI runs, and Dependabot.

**Restriction constraint.** None directly; supply-chain artefacts describe software, not grid data. The generator runs
inside the zero-warning build (Section 9) and makes no network call at runtime.

**Test.** `SupplyChainManifestTests`: the build hash is deterministic and changes with the SBOM hash, and a local build
carries no injected SBOM hash and is not a release build. `ComplianceAttestationTests`: a local build never attests
compliance, the verdict needs a release build and every in-scope control Achieved, exactly the controls with a named
gap are not Achieved, and every named evidence test class exists. `WorkflowReleaseGateTests`: the SBOM hash is
injected into every shipped build and checked, every publishing job waits for the CI matrix, Release files get a
provenance attestation, and signing trusts only the pinned certificate; these tests read the workflow files and show
what it would do, not that it ran. For RC-1 the evidence that a shipped CLI binary carries its SBOM hash is the
owner's post-signing check recorded in `RC1.md` (both CLI builds report `release build  yes` with the SBOM hash). `WorkflowContractTests` and
`ThirdPartyNoticesTests` pin the release workflow's SBOM and signing steps and the completeness of
`THIRD-PARTY-NOTICES.md`.

### 7.4 Availability SLOs and resilience (CAF B5 Resilient networks and systems)

**Obligation.** Maintain the availability of the service under adverse conditions, to a stated
objective, and evidence it.

**What it meets.** `RealtimeEstimatorEngine` has meaningful resilience: `ContinueOnError`
records-and-skips a malformed window or a non-converged estimate rather than tearing down the loop,
and `RealtimeRunSummary` reports processed/converged/rejected-fresh/failed counts. The web host caps concurrent
sessions (`GridSim:MaxSessions`) and the container health check probes it. The explicit service-level objective, the
in-application health probe and the resource limits are now built and applied; the bounded reconnect schedule is driven
since 2026-11-01 by `BridgeReconnectSupervisor` in the web host and the desktop app, but it is off by default and not yet
confirmed by a release test run, so B5 is Partial.

**Built** (`GridSim.Core.Net` and `src/GridSim.Core/Simulation/Health/`, namespace `GridSim.Core.Simulation.Health`):

- `ResilienceOptions` (`GridSim.Core.Net`): the resource limits (bridge message cap, in-flight-query cap, measurement
  cap) and the `ws://` transport policy, applied by `ApplyTo` to every bridge client the web and desktop hosts create;
  and a bounded reconnect backoff schedule (`BackoffDelay`).
- `BridgeReconnectSupervisor` (`GridSim.Core.Net`) drives that schedule: on a lost bridge stream it waits
  `BackoffDelay(n)` and reconnects through the host's own connect path, up to `MaxReconnectAttempts` (0 means no
  bound), then reports one give-up. A horizon rejection (`ForwardInferenceException`) is never retried, and a success
  resets the count. The web host applies it with `GridSim:BridgeReconnect=true` and `GridSim:BridgeReconnectMaxAttempts`
  (a malformed value stops start-up); the desktop app with `BridgeAutoReconnect` and `BridgeReconnectMaxAttempts`.
  **Gap:** both are off by default, a release test run has not yet confirmed them, the legacy WPF window is not wired,
  the bridge client has no keep-alive, and each attempt and the give-up go to the host's log (web: standard error and
  the session's Warning or Error; desktop: event log, status pill, alarm on give-up), not to the security audit.
  Tests: `BridgeReconnectSupervisorTests`, `BridgeReconnectHostWiringTests`.
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
| 1 | B2 Identity & access control | Authenticate/authorise access to system and data | **Partial** -- web host sign-in, SSO, replay-only (shipped exposure control), `/ws/sim` origin check behind the `AllowedHosts` host list, browser security headers and HSTS (since 2.1.008), loopback-default listeners for `pmu-ingest` UDP and `interpose`; `RoleGuard` authorisation gate enforced by the web host on `POST /api/solve` and every `/ws/sim` control since 2026-11-01, denials audited when the host has an audit log (7.2) | Gate the dashboard's own sessions; give a host without the resident estimator an audit log for its denials; bridge mTLS is a deployment choice | GridSim + OES | open |
| 2 | D1 / C1 incident response | Regulation 11 significant-incident notification within 72 h | **Met (in-component)** -- `IncidentRouter` + `IIncidentSink`, wired by `--incident-log` / `Audit:IncidentLogPath`, severity threshold, GDA-lake suppliers in scope (7.1); distrusted-source containment wired (`--distrust`, `Realtime:DistrustedSources`, 2026-11-01), held Partial until the release test run; the runbook tabletop has not been held | OES sets the incident log, routes it to its reporting process and files the Regulation 11 report; GridSim to confirm the distrust wiring in the release test run and hold and record the tabletop | GridSim (hook), OES (report) | closed in-component except containment and the exercise |
| 3 | A4 Supply chain | Assure third-party components | **Partial** -- lock files, `BuildProvenance`, NuGet audit as a build error, Dependabot, CodeQL by hand, signing of the RC-1 Windows executables and NuGet package with a self-signed pinned certificate; RC-1 was published by hand, so the workflow's SBOM injection, provenance attestation, vulnerable-package gate and CI gate did not run (7.3) | Build the next release through the release workflow (it accepts the RC tag form since 2.1.008; since 2026-11-01 its ship scope includes the desktop apps, written and not yet run; its signing secrets are still to settle); a green CI run before publication. Done 2026-11-01: every Action pinned to a full commit SHA. Done at 2.1.008: SDK pinned by `global.json`, locked-mode restore on CI and in the image build | GridSim | open |
| 3b | A4.b Secure software development and support (new in v4.0) | Attest software authenticity and integrity; support updates | **Partial** -- Authenticode, the NuGet signature and `SHA256SUMS.txt` for download integrity (no provenance attestation for RC-1); `ComplianceManifest` + `ComplianceAttestation` bound to the build identity; since 2.1.008 a release build needs a 64-hex SBOM hash, a known commit and a clean tree, and the manifest records `supply-chain` and `secure-sdlc-attestation` Partially achieved (7.3) | The verdict reads NOT COMPLIANT until rows 1, 2 (containment), 3 and 4 are closed; a release build environment separate from development (a local build of a clean tree can still set the release-build flag); signed tags on the public repository (its `main` ruleset, secret scanning with push protection and private vulnerability reporting are on since 2026-11-01; the support period and advisory process are stated in `SECURITY.md`) | GridSim | open |
| 4 | B5 Resilience | Availability to a stated SLO, evidenced | **Partial (in-component)** -- resilient loop, session cap, `ResilienceOptions` limits on every bridge client, `ServiceLevelObjective` + `HealthProbe` with an SLO breach recorded as a security event (7.4) | Supervised reconnect is off by default and not yet confirmed by a release test run (the legacy WPF window is not wired; no bridge keep-alive); edge rate limiting at the reverse proxy | GridSim + OES | open |
| 5 | C1 / C2 detection | Monitor for and discover security events | **Met** -- FDIA (both sub-classes) + bad-data + per-security-event audit at the audited detection sites, optional signed anchor, JSON-lines forwarding + `SecurityEventMonitor` (7.1) | `AccessDenied` is written by the web host's role gate since 2026-11-01; no product detection site yet for `DistrustedSource`, `DependencyAlert` | GridSim | closed |
| 6 | B3 Data security (transit) | Protect data in transit; ensure integrity | **Met (in-component)** -- output integrity via the anchored audit trail (optionally signed); key ring encrypted at rest; `ws://` refused unless allowed; `ISecretStore` with a secrets-directory backend | Bridge transport TLS is deploying-party; no OS-keychain or vault backend ships | GridSim + OES | closed in-component |
| 7 | B4 System security | Reduce attack surface; validate inputs | **Met (core)** -- guard + validation + fail-loud + resource limits (including, since 2.1.008, the C37.118 non-DATA frame cap and usage-log rotation) | Maintain | GridSim | maintain |
| 8 | A3 Asset management | Know data and dependency inventory | **Met for data; Partial for software** -- data provenance strong; lock files, notices and build provenance; one SBOM, for the CLI (7.3) | An SBOM for each shipped application, stamped into it: closed in the workflow (2026-11-01: five SBOMs, each desktop app built with its own SBOM's hash, the package stamped with the GridSim.Core SBOM's hash), not yet exercised by a release, so the published RC-1 still has one SBOM | GridSim | open |
| 9 | A2 Risk management | Documented component risk assessment and threat-actor view | **Met** -- restriction docs, this document and `risk-register.md` | -- | GridSim + OES | closed in-component |
| 10 | Confidentiality of operational picture | Protect a live operational view | **Met by design** -- 169 h guard removes the asset (Section 6) | Maintain guard universality as a security control | GridSim | maintain |
| 11 | A1/B1/B6 governance & culture | Organisational security management | **OES** -- in-component governance in `security-governance.md` | Out of component | OES | n/a |

Three cells are stated as strengths rather than gaps and must be represented that way to an assessor:
row 7 (B4 core), row 10 (the horizon guard's design-out of the operational-visibility asset), and the position that
every GridSim-owned product control is Met with a named test except three: B2 role enforcement (enforced on the web
host's REST and WebSocket solve surface, not on the dashboard's own sessions), D1 distrusted-source containment (wired
since 2026-11-01, held until the release test run) and the B5 supervised reconnect (applied by the web host and the desktop app since 2026-11-01, off by default, not yet confirmed by a release test run). Rows 3, 3b and 8 are open at 2.1.010 for a
different reason: the published release did not go through the release controls (Section 7.3). Row 10 is
contingent on the Section 9 tests staying green. Full CAF compliance *as* an Operator of Essential Services is not
achievable by a component (row 11, host OS, perimeter and physical security are the deploying party's); full
*in-component* compliance is the aim, and the compliance attestation (7.3) reads NOT COMPLIANT until the three
primitives are applied and, since 2.1.008, until a release is made through the release workflow (the manifest records
`supply-chain` and `secure-sdlc-attestation` Partially achieved for that reason).

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

- SBOM: built from the restore graph at release, so it cannot go stale relative to the release it describes (7.3);
  the `packages.lock.json` lock files record the graph itself, enforced by a locked-mode restore on GitHub Actions and
  in the image build, with the SDK pinned by `global.json` (since 2.1.008; `BugHunt1101WebTests`, `B161_*`).
- The release workflow injects the SBOM hash into every shipped build and stops unless each CLI binary reports exactly
  that hash as a release build (`WorkflowReleaseGateTests` pins the steps); a build given no SBOM hash, a malformed
  one, an unknown commit or a dirty or unrecorded tree never attests compliance (`SupplyChainManifestTests`,
  `ComplianceAttestationTests`, `BugHunt1101WebTests` `B176_*`). For RC-1 the workflow did not run; the owner
  injected the hash in the local build and checked each CLI binary after signing (`RC1.md`).
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
or the guard floor. Item 4 (reconnect) is applied by the web host and the desktop app since 2026-11-01 but off by default; item 6 (distrusted source) is wired since
2026-11-01 and held until the release test run;
item 1 is enforced by the web host's REST and WebSocket solve surface but not by the dashboard's own sessions:

1. **B2 authorisation gate + B3 transport (7.2).** `OperationRole`, `IdentityContext`, `RoleGuard`,
   `AccessDeniedException`; `SecurityEventKind.AccessDenied` on deny; enforced by the web host on `POST /api/solve` and
   every `/ws/sim` control since 2026-11-01 (not on the dashboard's own sessions); `ws://` refused unless
   `GridSim:BridgeAllowUnencrypted=true`. Tests: `RoleGuardTests`, `CafB2RoleGateWebTests`, `BridgeEndpointSettingTests`.
2. **Incident hooks (7.1).** `IIncidentSink`, `IncidentRouter`, `IncidentPolicy`, `SecurityIncidentSink`,
   `SecurityEventMonitor`, `JsonLinesIncidentSink`; Regulation 11 severity mapping; wired by `--incident-log` /
   `Audit:IncidentLogPath`. Tests: `IncidentRouterTests`, `SecurityEventMonitorTests`, `AuditWiringTests`.
3. **SBOM, lock files, build provenance and A4.b attestation (7.3).** `packages.lock.json`, `BuildProvenance`,
   `ComplianceManifest` + `ComplianceAttestation`; in the release workflow, SBOM hash injection into every shipped
   build and a signed provenance attestation (neither ran for RC-1). Tests: `SupplyChainManifestTests`,
   `ComplianceAttestationTests`, `WorkflowReleaseGateTests`.
4. **Availability SLOs and resilience (7.4).** `ResilienceOptions` (limits applied; reconnect schedule driven by
   `BridgeReconnectSupervisor`, off by default),
   `ServiceLevelObjective`, `HealthProbe`, `AvailabilityDegraded` recorded on an SLO breach. Tests: `HealthProbeTests`,
   `ResilienceOptionsTests`, `AuditWiringTests`.
5. **Per-event audit, signed anchor and secrets (C1/B3).** `AuditRunRecorder.RecordSecurityEvent` /
   `SecurityAudit.Record`, `JsonLinesAuditSink` forwarding, `SignedAuditAnchor` wired through the CLI flags and web
   settings, `ISecretStore` with `DirectorySecretStore` (`Secrets:Directory`). Tests: `SecurityAuditTests`,
   `SignedAuditAnchorTests`, `AuditWiringTests`, `SecretStoreTests`, `DirectorySecretStoreTests`,
   `NoSecretInAuditTests`.
6. **Governance, risk and incident documents (A1/A2/D1/D2).** `security-governance.md`, `risk-register.md`,
   `incident-response.md`; distrusted-source control (`DistrustedSources`, `DistrustedSourceSetting`; `DistrustedSourceTests`, `DistrustedSourceRedundancyTests`, `DistrustedSourceSettingTests`).

The remaining GridSim code items are the three partly applied controls above. The remaining GridSim process items (2.1.010) are:
build the next release through the release workflow (it accepts the RC tag form since 2.1.008; its ship scope includes
the desktop apps and five SBOMs since 2026-11-01, written and not yet run; the signing secrets are still to settle); a
green CI run on the release commit before publication; a release build environment separate from development; signed tags on the public repository;
and the runbook tabletop (its CVE script is written). Done 2026-11-01: a `main` ruleset, secret scanning with push
protection and private vulnerability reporting on the public repository, and the support period, remediation target
and advisory process stated in `SECURITY.md`.
Closed at 2.1.008: browser security headers and host filtering on the web host, the SDK pin and locked-mode restore,
and the format and clean-tree check on the release-build flag. The deploying-party actions are: point `--audit-forward` / `Audit:ForwardPath` at a WORM mount or SIEM collector and `--incident-log` /
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
