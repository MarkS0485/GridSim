# Component risk register

**Assurance statement. Status as at GridSim 2.1.010 (reviewed 2026-11-01; RC-1 released 2026-10-31 as 2.1.000).** This is the component-level risk register for the
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
| R1 | **Dependency CVE (A4).** A known-vulnerable NuGet dependency, direct or transitive, ships in a service. | Medium | Medium | Small dependency surface by construction (Core is close to the base class library); `dotnet list package --vulnerable` gate in CI and the release; NuGet audit warnings are build errors; CodeQL (`codeql.yml`); Dependabot monthly; CycloneDX SBOMs and `THIRD-PARTY-NOTICES.md` (`ThirdPartyNoticesTests`, `WorkflowContractTests`). | Accepted with residual: `packages.lock.json` lock files record the restore graph; NuGet audit fails every build, including the owner's local release build; the release workflow is written to build five SBOMs and inject each artefact's own SBOM hash into its build (the CLI, the web image, both desktop apps and the NuGet package), attest every Release file, rebuild the Linux CLI byte for byte and wait for the full CI matrix, but it did not build RC-1 (it refused the RC tag form then; accepted since 2.1.008, with no release made through it yet); since 2.1.008 the SDK is pinned by `global.json` (10.0.401, no roll-forward) and restores on GitHub Actions and in the image build run in locked mode, so a lock-file change fails there instead of drifting (`BugHunt1101WebTests`, `B161_*`). Residual: CI and CodeQL run by hand only, not on every push (a risk-accepted cadence), and no CI run has passed on `main` since the history reset, before or after RC-1 was published; a restore on the owner's machine is not locked; the published RC-1 SBOM covers the CLI only (the workflow's five SBOMs have not been exercised by a release); the builds are self-contained, so a .NET runtime security fix reaches a deployer only in a new GridSim build; GitHub Actions are pinned to full commit SHAs (since 2026-11-01); a `DependencyAlert` incident category exists but no product path raises it, so a flagged dependency is handled through the vulnerable-package gate, Dependabot and the OES's own process. `caf-mapping.md` 4.4, 9 items 3, 10 to 13; `nis-oes-gap-assessment.md` 8 rows 3, 3b and 8. | GridSim maintainer |
| R2 | **Secret leakage (B3).** A feed credential, key-ring certificate password, or licence key leaks through configuration, an environment dump, or the audit trail. | Medium | Medium | Core holds no secrets on its path; secrets live only at the edges, in environment variables or configuration, never committed; only an ECDSA P-256 public key is embedded, so a binary can verify but not mint a licence; `AuditEvent` records inputs by SHA-256 and sanitises actor user and machine names, and the writers are documented never to record secrets. | Accepted with residual: `ISecretStore` (`EnvironmentSecretStore`, `DirectorySecretStore`, `CompositeSecretStore`) resolves the key-ring certificate password, from a one-file-per-secret directory first when `Secrets:Directory` is set (Docker / Kubernetes / systemd secrets), so it need not be in configuration or the environment; `NoSecretInAuditTests` enforces that no secret appears in any audit summary. Residual: no OS-keychain or vault backend ships (a deployer can supply their own `ISecretStore` in code). `caf-mapping.md` 5.3, 9.5; `rbac-sbom-secrets-notes.md` 3. | GridSim maintainer |
| R3 | **Denial of service of the resident services (B4/B5).** A flood of sessions, oversized frames, or an unbounded bridge feed exhausts a resident service (`serve`, the web host, the interposer). | Medium | Low | Session cap (`GridSim:MaxSessions`, default 64, `SessionLimitException`); `/ws/sim` control frame over 16 KiB closed (`MessageTooBig`); Modbus frame-length cap; audit-log viewer read cap; `GridBridgeClient` times out a query after 60 s and fails pending queries on fault (`BridgeClientRobustnessTests`); `ContinueOnError` keeps the resident loop up past a bad window. | Accepted with residual: `ResilienceOptions` (bridge message cap, in-flight query cap) is applied to every bridge client; since 2.1.008 a live C37.118 receive stops after 1024 frames that are not DATA frames instead of buffering them, `interpose` drops a client silent for `--idle-seconds` (default 120) so it cannot hold the single slot, `pmu-ingest` UDP and `interpose` listen on loopback by default, and the web usage log is rotated at `Analytics:MaxLogBytes` (default 16 MiB) so start-up no longer reads an ever-growing file (`BugHunt1101WebTests`, `B177_*`, `B174_*`, `B173_*`, `B188_*`); `ServiceLevelObjective` + `HealthProbe` record an SLO breach at the end of a `serve` / resident run as an `AvailabilityDegraded` security event (an incident with `--incident-log` / `Audit:IncidentLogPath`); a failing window is skipped and the loop carries on. Residual: there is no degraded-mode fallback to a last-good estimate; the bounded reconnect backoff schedule is driven by `BridgeReconnectSupervisor` in the web host and the desktop app since 2026-11-01, but it is off by default (`GridSim:BridgeReconnect`, `BridgeAutoReconnect`), not yet confirmed by a release test run, and the bridge client has no keep-alive; request rate limiting belongs at the reverse proxy. Impact is Low because a downed historical estimator loses no live operational function. `caf-mapping.md` 5.4, 5.5, 9.4; `nis-oes-gap-assessment.md` 7.4, 8 row 4. | GridSim maintainer + OES |
| R4 | **Audit-log tampering at rest (C1).** An actor with disk access alters, reorders, inserts, deletes or rewrites audit entries to hide activity. | Low | Medium | Hash-chained, append-only `AuditLog`/`AuditLogWriter`/`AuditLogFile`; SHA-256 chain from genesis makes any edit, reorder, insertion or deletion tamper-evident; `AuditAnchorFile` (entry count and head hash) checked on every open catches end-truncation and whole-log rewrite; exclusive writer lock fails loud; `gridsim audit verify` localises a break (`AuditLogTests`, `AuditLogDeletionTests`, `AuditLogFileTests`, `Doc3AuditAnchorTests`). | Accepted residual, honestly bounded: the chain is tamper-evident, not tamper-proof - an actor who can rewrite both the log and its anchor can recompute a consistent pair. Two optional controls narrow this: `SignedAuditAnchor` (`--audit-sign-key` / `--audit-verify-key`, `Audit:AnchorSigningKeyPath` / `Audit:AnchorVerificationKeyPath`, `gridsim audit verify --pubkey`) detects a joint rewrite by anyone who lacks the private key, and `--audit-forward` / `Audit:ForwardPath` keeps an independent JSON-lines copy the OES can place on a WORM mount or SIEM. Residual: a host that holds the private key does not protect against an attacker with that host's privileges; the strong form keeps the private key off the host (`SignedAuditAnchorTests`, `AuditWiringTests`). `caf-mapping.md` 6.1, 6.2, 9.2. | GridSim maintainer + OES |
| R5 | **False-data injection landing on a feasible point (C1, accepted residual).** A perfectly crafted FDIA that lands on another feasible, in-limit operating point passes chi-square, the largest-normalised-residual test, the physics-conformance audit and the operational-envelope screen, biasing the reconstruction undetected. | Low | Low | `FdiaScreen` catches the out-of-envelope and physically-impossible sub-classes as an independent validator the attacker must also satisfy (`FdiaCloakedAttackTests`, `FdiaScreenTests`); the `FeasibleWlsStep` Ring C gate rejects a physically-impossible converged state as `EstimationStatus.Infeasible`; chi-square and largest-normalised-residual bad-data detection; `ProvenanceTag`/`ProvenanceTrust` label every series; the guard caps the value of any bias to a historical reconstruction. | Reduced: `FdiaScreen.Screen` takes PMU-anchored `protectedAnchors` and flags a feasible, in-limit cloaked attack that disturbs an anchored quantity (`FdiaScreenReport.AnchorInconsistent`, `FdiaProtectedAnchorTests`). Since 2.1.010 the screen's significance margin on a branch flow uses the tightest meter on a single line, as for bus quantities, so a loose second meter cannot widen it enough to hide a rating breach (`BugHunt1101WebTests`, `B189_*`). Residual: an attack that leaves every anchored quantity undisturbed, or a deployment with no PMU anchors, is still not caught by this path. Impact is Low because the guard caps the payoff to biasing a week-stale reconstruction, never a live view. `caf-mapping.md` 6.3, 9.8; `nis-oes-gap-assessment.md` 5.3. | GridSim maintainer |
| R6 | **Misconfiguration disabling a control.** A deployment leaves the solve surface exposed (replay-only off), stores keys unencrypted, points the audit anchor beside the log, or accepts an unencrypted bridge URI, silently removing a control. | Medium | Medium | Fail-loud configuration where it exists: a protection setting that cannot be honoured stops start-up (`KeyRingProtection`); `Sso:IsAuthority=true` stops start-up; replay-only leaves `/api` and `/ws/sim` unmapped in the production configuration; trusted-proxy allow-list; secure-configuration baseline in `docs/developer/deploy/` and the technical chapters; documented systemd and container hardening. | Accepted with remediation: `GridBridgeClient` refuses an unencrypted `ws://` URI unless explicitly allowed, and on the web host a `ws://` bridge endpoint stops start-up unless `GridSim:BridgeAllowUnencrypted=true` (`BridgeEndpointSettingTests`); an audit setting given without the audit log stops start-up or is a usage error. Remaining soft spots: the compose file omits `read_only`/`cap_drop`/`no-new-privileges`; on Linux without a certificate the keys are stored unencrypted with a start-up warning; a deployment reached under a public name must set `AllowedHosts` (until it does, every request answers 400 naming the setting, which fails loud), and `AllowedHosts=*` removes the DNS-rebinding protection with only a start-up warning (since 2.1.008 the host list, framing, content-type and referrer headers and HSTS are on by default; `BugHunt1101WebTests`, `B170_*`, `B179_*`); and the RBAC gate (`RoleGuard`/`OperationRole`/`IdentityContext`) is enforced by the web host on `POST /api/solve` and every `/ws/sim` control (since 2026-11-01; an anonymous caller holds Viewer on an instance that answers any non-loopback name, and a `GridSim:AnonymousRole` that is not a band name stops start-up), but not on the dashboard's own sessions, so turning replay-only off still lets an anonymous visitor run live dashboard sessions; `GridSim:AnonymousRole` set above Viewer on such an instance is a start-up warning only. The guard remains independent of every such setting, so no misconfiguration grants sub-horizon data. `caf-mapping.md` 5.1, 5.2, 5.3, 9 items 1, 4 and 15 (closed at 2.1.008). | GridSim maintainer + OES |
| R7 | **Tampered or substituted release (A4/A4.b).** A deployer runs a GridSim binary that is not the one the owner built: altered in transit or at the download host, or built from a modified tree, or built on a compromised build machine. | Low | High | Authenticode on the three Windows executables and an author signature on the NuGet package (self-signed certificate, checked by thumbprint); `SHA256SUMS.txt` and the SHA-256 values in `RC1.md`; commits signed with the owner's key; `gridsim compliance verify` and `/compliance` name the version, commit, SBOM hash and build identity. | Accepted with residual: the build identity is a label, not an integrity proof, and "release build" (since 2.1.008: a 64-hex SBOM hash, a known commit and a clean source tree) can still be met by a local build of a clean tree, so a binary's own report cannot show it is the released one; RC-1 was built on the owner's development machine and carries no build-provenance attestation; the Linux files rest on checksums published by the same hand; release tags are unsigned; the certificate is self-signed (an accepted residual, owner decision 2026-11-01). Impact is High: this is the one risk the horizon guard does not bound, because a substituted binary need not contain the guard at all and can do whatever its host account can. Download verification (signer thumbprint, checksums) is the deployer's step. `caf-mapping.md` 4.4, 4.5, 9 items 10 and 13; `slsa-provenance.md` 5. | GridSim maintainer + OES |

---

## 4. How this register honours the restriction invariant

Every treatment above is additive to the forward-inference guard and independent of it. No control added to
close a conventional risk (RBAC, a secret store, a signed anchor, a resilience loop) consults the horizon or
can relax it, and none needs fresher-than-permitted data to function. The low impact scores of R1 to R6 are a
direct consequence of the guard having already removed the high-value timing asset (section 2); R7 is scored High
because a substituted binary sits outside the guard. If the guard were ever weakened, every other impact score in this
table would rise, which is why its enforcement tests are
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
