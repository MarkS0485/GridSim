# Component risk register

**Assurance statement. Status as at GridSim 1.95.296.** This is the component-level risk register for the
GridSim state estimator and its services, mapping to CAF Objective A2 (Risk management). It is the
consolidated register for the *conventional* risks that `caf-mapping.md` section 4.2 cites. The
dominant risk - turning public data into live operational timing by forward inference - is not in this table,
because it is mitigated to structural impossibility by the `ForwardInferenceGuard` and is covered in
`caf-mapping.md` section 3 and `nis-oes-gap-assessment.md` section 6. This register covers the ordinary
risks that remain once that catastrophic risk has been designed out.

GridSim is assessed against CAF v4.0 (released 6 August 2025) and the UK NIS Regulations 2018, as a software
component embedded inside a deploying OES in downstream electricity, under the Ofgem Downstream Gas and
Electricity (DGE) CAF overlay. Competent authority: Ofgem, with DESNZ and the NCSC. Per-control statuses of
record are in `caf-mapping.md` section 8; the gap register there (section 9) and the honest gap table in
`nis-oes-gap-assessment.md` section 8 are the remediation plans this register points at.

Governing constraint: no treatment in this register may weaken the guard or create a code path that reads
present telemetry or emits a future-dated grid claim. See `UnderstandingRestrictions.md`.

---

## 1. Scoring

Likelihood and impact are each scored Low / Medium / High. Impact is scored *after* the horizon guard has
bounded the blast radius: because a total compromise of GridSim yields only week-stale reconstruction of
already-public history, the confidentiality impact of most risks is capped well below where it would sit for
a live real-time state estimator. This is stated so an assessor sees that the low impact scores are earned by
a control, not assumed.

---

## 2. Understanding attacker methods and motivations

CAF v4.0 strengthens A2 with an explicit expectation that the operator understands who would attack the
system and why. The honest answer for GridSim shapes every row below.

**Who would target a historical grid estimator.** The plausible actors are the same ones who target the
downstream electricity subsector: a state-aligned actor seeking to understand or pre-position against GB grid
operations, a capable criminal actor seeking disruption or extortion, and an insider or a compromised
supplier with access to the deploying OES estate. A hacktivist seeking visible disruption is a lower-tier
but real actor.

**What they want from a state estimator, and why GridSim denies it.** The high-value asset in a conventional
real-time state estimator is *timing*: a current, actionable picture of when inertia is low, margins are
thin and the system is weakest, which is knowledge of when a disturbance does the most damage. That asset is
the motivation for most state-estimator hardening. GridSim removes it by construction: the freshest datum the
engine can hold is 169 hours old, by which point unit commitment, outage windows, market positions and
constraint patterns have all rolled over, so the output space sits outside any operationally actionable
envelope (`caf-mapping.md` section 3.2, `nis-oes-gap-assessment.md` section 6). An attacker who fully owns a
GridSim instance obtains a week-stale reconstruction, not a live weapon.

**What that leaves.** Having designed out the high-value timing asset, the residual motivations are ordinary:
disrupting the availability of a resident service, tampering with the historical evidence record to
discredit it, poisoning a measurement source to bias a reconstruction, harvesting a leaked secret to pivot
elsewhere in the OES estate, and exploiting a dependency CVE. These are the conventional risks this register
treats. They matter, but none of them restores the forward-inference asset the guard has removed.

---

## 3. Risk register

| Ref | Risk | Likelihood | Impact | Existing controls | Residual treatment | Owner |
|---|---|---|---|---|---|---|
| R1 | **Dependency CVE (A4).** A known-vulnerable NuGet dependency, direct or transitive, ships in a service. | Medium | Medium | Small dependency surface by construction (Core is close to the base class library); `dotnet list package --vulnerable` gate in CI and the release; NuGet audit warnings are build errors; CodeQL (`codeql.yml`); Dependabot monthly; CycloneDX SBOMs and `THIRD-PARTY-NOTICES.md` (`ThirdPartyNoticesTests`, `WorkflowContractTests`). | Accepted with residual: `packages.lock.json` lock files pin the restore graph; the release injects the SBOM hash into every shipped build and attaches a signed build-provenance attestation to every Release file; every publishing job waits for the full CI matrix. Residual: CI runs at the release or by hand and CodeQL by hand only, not on every push (a risk-accepted cadence); GitHub Actions are referenced by version tag, not SHA; a `DependencyAlert` incident category exists but no product path raises it, so a flagged dependency is handled through the vulnerable-package gate, Dependabot and the OES's own process. `caf-mapping.md` 4.4, 9 item 3; `nis-oes-gap-assessment.md` 8 row 3. | GridSim maintainer |
| R2 | **Secret leakage (B3).** A feed credential, key-ring certificate password, or licence key leaks through configuration, an environment dump, or the audit trail. | Medium | Medium | Core holds no secrets on its path; secrets live only at the edges, in environment variables or configuration, never committed; only an ECDSA P-256 public key is embedded, so a binary can verify but not mint a licence; `AuditEvent` records inputs by SHA-256 and sanitises actor user and machine names, and the writers are documented never to record secrets. | Accepted with residual: `ISecretStore` (`EnvironmentSecretStore`, `DirectorySecretStore`, `CompositeSecretStore`) resolves the key-ring certificate password, from a one-file-per-secret directory first when `Secrets:Directory` is set (Docker / Kubernetes / systemd secrets), so it need not be in configuration or the environment; `NoSecretInAuditTests` enforces that no secret appears in any audit summary. Residual: no OS-keychain or vault backend ships (a deployer can supply their own `ISecretStore` in code). `caf-mapping.md` 5.3, 9.5; `rbac-sbom-secrets-notes.md` 3. | GridSim maintainer |
| R3 | **Denial of service of the resident services (B4/B5).** A flood of sessions, oversized frames, or an unbounded bridge feed exhausts a resident service (`serve`, the web host, the interposer). | Medium | Low | Session cap (`GridSim:MaxSessions`, default 64, `SessionLimitException`); `/ws/sim` control frame over 16 KiB closed (`MessageTooBig`); Modbus frame-length cap; audit-log viewer read cap; `GridBridgeClient` times out a query after 60 s and fails pending queries on fault (`BridgeClientRobustnessTests`); `ContinueOnError` keeps the resident loop up past a bad window. | Accepted with residual: `ResilienceOptions` (bridge message cap, in-flight query cap) is applied to every bridge client; `ServiceLevelObjective` + `HealthProbe` record an SLO breach at the end of a `serve` / resident run as an `AvailabilityDegraded` security event (an incident with `--incident-log` / `Audit:IncidentLogPath`); a failing window is skipped and the loop carries on. Residual: there is no degraded-mode fallback to a last-good estimate; no host drives the bounded reconnect backoff schedule, so there is no supervised reconnect; request rate limiting belongs at the reverse proxy. Impact is Low because a downed historical estimator loses no live operational function. `caf-mapping.md` 5.4, 5.5, 9.4; `nis-oes-gap-assessment.md` 7.4, 8 row 4. | GridSim maintainer + OES |
| R4 | **Audit-log tampering at rest (C1).** An actor with disk access alters, reorders, inserts, deletes or rewrites audit entries to hide activity. | Low | Medium | Hash-chained, append-only `AuditLog`/`AuditLogWriter`/`AuditLogFile`; SHA-256 chain from genesis makes any edit, reorder, insertion or deletion tamper-evident; `AuditAnchorFile` (entry count and head hash) checked on every open catches end-truncation and whole-log rewrite; exclusive writer lock fails loud; `gridsim audit verify` localises a break (`AuditLogTests`, `AuditLogDeletionTests`, `AuditLogFileTests`, `Doc3AuditAnchorTests`). | Accepted residual, honestly bounded: the chain is tamper-evident, not tamper-proof - an actor who can rewrite both the log and its anchor can recompute a consistent pair. Two optional controls narrow this: `SignedAuditAnchor` (`--audit-sign-key` / `--audit-verify-key`, `Audit:AnchorSigningKeyPath` / `Audit:AnchorVerificationKeyPath`, `gridsim audit verify --pubkey`) detects a joint rewrite by anyone who lacks the private key, and `--audit-forward` / `Audit:ForwardPath` keeps an independent JSON-lines copy the OES can place on a WORM mount or SIEM. Residual: a host that holds the private key does not protect against an attacker with that host's privileges; the strong form keeps the private key off the host (`SignedAuditAnchorTests`, `AuditWiringTests`). `caf-mapping.md` 6.1, 6.2, 9.2. | GridSim maintainer + OES |
| R5 | **False-data injection landing on a feasible point (C1, accepted residual).** A perfectly crafted FDIA that lands on another feasible, in-limit operating point passes chi-square, the largest-normalised-residual test, the physics-conformance audit and the operational-envelope screen, biasing the reconstruction undetected. | Low | Low | `FdiaScreen` catches the out-of-envelope and physically-impossible sub-classes as an independent validator the attacker must also satisfy (`FdiaCloakedAttackTests`, `FdiaScreenTests`); the `FeasibleWlsStep` Ring C gate rejects a physically-impossible converged state as `EstimationStatus.Infeasible`; chi-square and largest-normalised-residual bad-data detection; `ProvenanceTag`/`ProvenanceTrust` label every series; the guard caps the value of any bias to a historical reconstruction. | Reduced: `FdiaScreen.Screen` takes PMU-anchored `protectedAnchors` and flags a feasible, in-limit cloaked attack that disturbs an anchored quantity (`FdiaScreenReport.AnchorInconsistent`, `FdiaProtectedAnchorTests`). Residual: an attack that leaves every anchored quantity undisturbed, or a deployment with no PMU anchors, is still not caught by this path. Impact is Low because the guard caps the payoff to biasing a week-stale reconstruction, never a live view. `caf-mapping.md` 6.3, 9.8; `nis-oes-gap-assessment.md` 5.3. | GridSim maintainer |
| R6 | **Misconfiguration disabling a control.** A deployment leaves the solve surface exposed (replay-only off), stores keys unencrypted, points the audit anchor beside the log, or accepts an unencrypted bridge URI, silently removing a control. | Medium | Medium | Fail-loud configuration where it exists: a protection setting that cannot be honoured stops start-up (`KeyRingProtection`); `Sso:IsAuthority=true` stops start-up; replay-only leaves `/api` and `/ws/sim` unmapped in the production configuration; trusted-proxy allow-list; secure-configuration baseline in `docs/developer/deploy/` and the technical chapters; documented systemd and container hardening. | Accepted with remediation: `GridBridgeClient` refuses an unencrypted `ws://` URI unless explicitly allowed, and on the web host a `ws://` bridge endpoint stops start-up unless `GridSim:BridgeAllowUnencrypted=true` (`BridgeEndpointSettingTests`); an audit setting given without the audit log stops start-up or is a usage error. Remaining soft spots: the compose file omits `read_only`/`cap_drop`/`no-new-privileges`; on Linux without a certificate the keys are stored unencrypted with a start-up warning; and the RBAC gate (`RoleGuard`/`OperationRole`/`IdentityContext`) is built but not enforced by any host, so turning replay-only off exposes the solve surface with no role check. The guard remains independent of every such setting, so no misconfiguration grants sub-horizon data. `caf-mapping.md` 5.1, 5.2, 5.3, 9.1, 9.4. | GridSim maintainer + OES |

---

## 4. How this register honours the restriction invariant

Every treatment above is additive to the forward-inference guard and independent of it. No control added to
close a conventional risk (RBAC, a secret store, a signed anchor, a resilience loop) consults the horizon or
can relax it, and none needs fresher-than-permitted data to function. The low impact scores throughout are a
direct consequence of the guard having already removed the high-value timing asset (section 2); if the guard
were ever weakened, every impact score in this table would rise, which is why its enforcement tests are
treated as release-blocking security tests (`nis-oes-gap-assessment.md` section 6.3).

---

## 5. Cross-references

- `caf-mapping.md` - the per-principle mapping; section 8 traceability matrix (status of record); section 9
  gap register and remediation plan.
- `nis-oes-gap-assessment.md` - the NIS/OES gap assessment; section 6 (the horizon argument); section 8
  honest gap table.
- `security-governance.md` - the governance that accepts residual risk against this register (A1) and owns
  the review cadence.
- `incident-response.md` - the runbook that handles a risk when it materialises (D1).
- `UnderstandingRestrictions.md` - the governing no-forward-inference document.
