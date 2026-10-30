# NCSC Cyber Assessment Framework (CAF) mapping

**Assurance statement. Status as at GridSim 1.95.296.** This document maps the GridSim state estimator and the
services that host it to the four objectives of the NCSC Cyber Assessment Framework v4.0 (released 6 August 2025),
principle by principle. v4.0 carries 41 contributing outcomes (39 in v3.x) across the same four objectives and
fourteen principles; the new outcomes relevant to a software component are A4.b Secure software development and
support, and the expanded monitoring and threat-hunting expectations under C1/C2 and the tested, supplier-aware
response expectations under D1. Each outcome names the controls that exist in the code at this release, the test or
build artefact that evidences them, and the gaps that remain.

No outcome is marked "Achieved" unless a control is built and a named test or artefact evidences it. Any control
still owned by the deploying party is named as such with its design, so the boundary is concrete.

The headline position at this release: most GridSim-owned controls are Achieved with a named test; three are Partially
achieved because the control exists but no product path applies it yet (B2 role-based access control, the D1
distrusted-source control, and the B5 reconnect schedule; section 9). The compliance attestation (A4.b, section 4.5)
reports this honestly: it reads COMPLIANT only for a release build whose every in-scope control is recorded Achieved,
so at this release even a release build reads NOT COMPLIANT until those three are closed. Full CAF compliance *as*
an Operator of Essential Services is not achievable by a component (A1 governance at org level, B1 policy, B6
awareness, host OS, perimeter and physical security are the deploying party's); what is aimed at here is full
*in-component* compliance, with the remaining in-component gaps and the deploying-party boundary stated throughout.

Governing constraint: everything here is subordinate to `UnderstandingRestrictions.md`. GridSim is a
**historical state estimator** - a filter over data that lags real time by at least 169 hours, or
over replay. The single most important security property of this system is not a firewall; it is
that the engine is *structurally incapable* of forward inference on live UK grid data. That property
is a CAF control in its own right (it bounds the blast radius of every other failure) and it is the
lens through which every control below is read.

---

## 1. Purpose and scope

### 1.1 What this maps

The unit of assessment is the GridSim estimation core (`GridSim.Core`: the WLS, robust and constrained estimators
and their ingestion seams) and the services that host it: the `gridsim serve` resident estimator, the RTU interposer
(`gridsim interpose`), and the web host (`GridSim.Web`, including its resident estimator). It does **not** assess an
operator's EMS - GridSim is not deployed inside a control room and, by the restriction invariant, cannot be. The
relevant regulatory framing is therefore "a critical-infrastructure-adjacent evidence and analysis tool handling
sensitive (if lagged) national grid data", not "an Operator of Essential Services running live SCADA". The CAF is
used here as the assurance yardstick because it is the UK framework for critical-infrastructure cyber assurance, and
because it forces the honest question CAF is designed to force: *can you evidence the outcome, or only assert it?*

### 1.2 What "the estimator and its services" means concretely

- **Estimator**: `WlsStateEstimator.Estimate(GridModel, MeasurementSet, EstimationOptions)` and its step backends
  (`DenseWlsStep`, `SparseWlsStep`, `ConstrainedDenseWlsStep`, `FeasibleWlsStep`), the `MeasurementModel` (the
  `z = h(x) + e` equation), `EstimationResult` and `EstimationStatus`.
- **Data boundary**: `ForwardInferenceGuard` at every dated ingestion seam (listed in full in section 3.1).
- **Security layer**: `Estimation.Security.FdiaScreen`; the audit trail (`Diagnostics.AuditLog`, `AuditLogWriter`,
  `AuditLogFile`, `AuditAnchor`, `AuditAnchorFile`, `AuditEvent`, `AuditRunRecorder`); `Diagnostics.ProvenanceTag`;
  `Diagnostics.RestrictionReview`; and the physics-conformance audit (`Physics.PhysicsLaws`,
  `Physics.PhysicsFeasibility`, `Physics.PhysicsCheckRegistry`).
- **Web host controls**: cookie sign-in, the single sign-on (SSO) consumer, the replay-only gate, the trusted-proxy
  list, the session cap and the encrypted data-protection key ring (section 5).

### 1.3 Out of scope (named so the boundary is explicit)

Host OS hardening, network perimeter, TLS termination, physical security of the machine GridSim runs on, and the
security of the upstream GDA data lake are the deploying party's responsibility and are covered by their own CAF
assessment. This document covers only what GridSim itself can and does enforce. The deployment guidance GridSim ships
for those layers is listed under B1.

---

## 2. How to read this document

### 2.1 CAF status vocabulary

Each contributing outcome is marked with a status drawn from the CAF's own three-band scale, plus an
honest fourth for the many outcomes that a library-plus-tool simply does not own:

- **Achieved** - an implemented control substantially meets the CAF indicators of good practice, and
  there is a test or artefact that evidences it.
- **Partially achieved** - a control exists but has a named, material gap (listed in section 9).
- **Not achieved** - the outcome is in scope and there is no control yet (a gap with a remediation
  entry).
- **Out of scope / deploying-party** - the outcome belongs to the host environment, not the engine.

### 2.2 Evidence convention

Every "Achieved" row points at either (a) a type and a passing test in `GridSim.Tests`, or (b) a
build-gate artefact. Assertion without evidence is treated as "Partially achieved" at best. This is
the same doctrine the estimator applies to its own numerics: a singular gain is reported as
`Unobservable`, never papered over as a plausible-looking state - see `EstimationStatus`.

### 2.3 The restriction invariant is the spine

Read section 3 before the objective sections. `ForwardInferenceGuard` is not merely a B3/B4 data
control; it is the design decision that makes the entire consequence-space of a GridSim compromise
bounded. An attacker who fully owns a GridSim instance still cannot obtain live or forward operational
visibility of the GB grid, because the guard refuses the timestamp before any code path touches the
datum. That containment is what the CAF calls "minimising impact by design" and it is invoked
repeatedly below.

### 2.4 Why the v4.0 AI-specific risks do not arise in the engine

v4.0 weaves AI-related risk through several outcomes. GridSim is a deterministic, non-AI and non-ML state estimator:
its numerics are bit-reproducible (section 10.3) and contain no model inference, no learned weights and no trained
component, so the AI-specific CAF v4.0 risks (training-data poisoning, model drift, opaque inference, adversarial
examples against a model) do not arise in the engine. The one adjacent concern - a future dynamics-into-estimation
predictor - is held off by the `RestrictionReview` gate (section 3.2 and invariant #5): any such fusion must be
reviewed and must still emit a filtered present/past estimate, or it fails loud. This is stated honestly rather than
claimed as exemption: the risk is designed out and gated, not merely absent today.

---

## 3. Foundational control: the forward-inference data boundary

`ForwardInferenceGuard` (`GridSim.Core.Diagnostics`) is the primary data-boundary control and the
single most load-bearing security property in the codebase. It is documented in full in
`UnderstandingRestrictions.md`; this section states its CAF role.

### 3.1 What it does, mechanically

```
ForwardInferenceGuard.HorizonHours = 169.0                       // 7 days + 1 hour
ForwardInferenceGuard.Horizon(utcNow) = utcNow - 169h
ForwardInferenceGuard.RejectFuture(stampUtc, utcNow, what) ->    // throws ForwardInferenceException
    if stampUtc (normalised to UTC) > utcNow - 169h then throw
ForwardInferenceGuard.AssertHistorical(stampsUtc, ...) ->        // first-violation-throws over a series
ForwardInferenceGuard.RequireNotAfterRealClock(utcNow, ...)  ->  // a supplied "now" may not run ahead of the real clock
ForwardInferenceGuard.RequireDated(...)                      ->  // an undated input is refused, not assumed old
```

Every dated input passes through the guard at its ingestion seam. At this release the seams are:

- **Files and the data lake**: `GdaTimeSeries.Load`, `NesoFrequencyCsv.Read`, `EventCatalog.Validate`,
  `MeasurementSet.Load`, `HarmonicSourceSet`.
- **Replay**: `GdaReplayLoop.Replay` and `PresolvedReplay` (the web host's replay path goes through it via
  `CaseCatalog`).
- **Streaming**: `GridBridgeClient` (each `state` tick, before a `SimFrame` is built), the web host's
  replay-directory window feed (through `MeasurementSet.Load`) and its bridge session start.
- **Real-time engines**: `RealtimeEstimatorEngine` and `RealtimeDseEngine`.
- **RTU interposer**: `InterposePipeline` and `RtuMeasurementSetMapper` (the IEC 60870-5-104 and Modbus paths both
  enter here).
- **Synchrophasors**: `PmuMeasurementSetMapper`, the `pmu-ingest` C37.118 data frames, and `PmuLiveQuarantine`
  (which sorts frames by the guard's horizon instead of throwing).
- **CLI clock inputs**: `--utc-now`, the register as-of date and the phase-angle override.
- **Dynamics to estimation**: `RestrictionReview` (section 3.2).

The seams are enumerated in the seam table of technical ch. 31 and pinned by `ForwardInferenceGuardTests`,
`GdaReplayLoopTests` and the cross-seam `RestrictionInvariantTests`. `GridBridgeClient` checks each tick against the
exact 169 h horizon with no clock-skew allowance (an earlier 15-minute allowance accepted frames 168 h 45 min old,
weaker than every other ingester, and was removed).

### 3.2 Why it is a security control, not just a policy

- **It bounds impact (CAF Objective D by construction).** The harm is *timing* -
  knowing when inertia is low and recovery margins are thin. The guard makes that harm unreachable:
  the freshest datum the engine can hold is a week old, by which point unit commitment, outage
  windows, market positions and constraint patterns have all rolled over, so the output space sits
  outside any operationally actionable envelope. A total compromise of GridSim yields week-stale
  reconstruction, not a live weapon.
- **It is enforced by construction, not by trust (CAF B4).** There is no configuration flag, no
  per-source override, no privileged bypass. Widening or removing the horizon is a source change that
  fails `RestrictionInvariantTests`, so it cannot be done quietly.
- **It is the reason "real-time" is safe here (CAF A2 risk framing).** Real-time in GridSim is an
  *engineering* property (streaming ingest, resident service, sub-cycle cadence, resilience) applied
  to permitted-age or replay data. It is never live sight. The estimator is a filter over now/then,
  never a predictor of next - see `UnderstandingRestrictions.md` section 3 and the
  `RestrictionReview` gate for the one seam (Dynamics -> Estimation fusion) where that could slip.

### 3.3 The invariant list this control must keep satisfying

The five testable invariants of `UnderstandingRestrictions.md` are the acceptance criteria for this
control. Restated as CAF-relevant assertions:

1. No ingester accepts a timestamp newer than `UtcNow - 169h` (universal, non-bypassable).
2. No public-data code path emits an output stamped later than its newest input.
3. Solver forward trajectories are labelled model output (`ProvenanceTag.Synthetic` /
   `Counterfactual`), seeded only from guard-permitted data, never presented as a grid forecast.
4. Real-time features are proven on replay or permitted-age streams, estimating now/then.
5. Any `Dynamics/` -> `Estimation/` fusion is gated by `RestrictionReview.AssertPermitted`.

An invariant that is not tested is a hope; these live in the suite beside the machine-epsilon solver
checks (section 10).

---

## 4. Objective A - Managing security risk

CAF principles A1 Governance, A2 Risk management, A3 Asset management, A4 Supply chain.

### 4.1 A1 Governance

**Status: Achieved in-component; organisation-level governance is deploying-party.**

The governance of the one risk that matters most for this system - forward inference - is codified, not merely
documented. `UnderstandingRestrictions.md` is the governing policy; `RestrictionReview.Reviewed` is its code-resident,
test-enforced register (there is no separate document to keep in step with it). A change to the security posture of
the estimator's most dangerous seam (fusing dynamics into estimation) cannot merge without a reviewed entry in that
register, because `RestrictionInvariantTests` fails without one.

The broader security governance artefact exists: [`security-governance.md`](security-governance.md) names the
security owner, the risk-acceptance record and the review cadence for the *other* controls, with a CHANGELOG
discipline that flags security-relevant changes. The in-component governance gap is closed. What remains is the
org-level governance an Operator of Essential Services must run (board accountability, estate-wide risk ownership),
which a component cannot provide and which is the deploying party's.

### 4.2 A2 Risk management

**Status: Achieved.**

The dominant risk (turning public data into operational timing) is identified, analysed, and mitigated to the point of
structural impossibility (section 3). The residual-cloaked FDIA risk is identified and now closed for the
feasible-landing sub-class as well (`FdiaScreen` with PMU-anchored protected measurements, section 6.3). The
physics-conformance layer (`PhysicsLaws`, `PhysicsFeasibility`) is an independent-validator risk control: a state that
passes chi-square but breaks conservation of energy, Ohm's law, Joule heating, KCL or complex-power balance
(`PhysicsCheckRegistry.HardGateLaws = {1,2,3,6,8}`) is flagged or rejected. Technical ch. 35 carries a threat-model
table for the deployed services.

The consolidated risk register for the *conventional* risks (dependency CVEs, secret leakage, denial of service of the
resident services, log tampering at rest), each with a likelihood and impact treatment, exists at
[`risk-register.md`](risk-register.md), cross-referenced to the gap register in section 9.

**Understanding attacker methods and motivations (A2, drawn out in v4.0).** v4.0 asks an operator to hold an explicit
threat-actor view, not merely a risk list. [`risk-register.md`](risk-register.md) carries that view (who would target a
grid state estimator, and why), and it is where the design rationale for the 169 h horizon is stated as a
security decision: the high-value asset a live RTSE holds is *timing* (knowing when inertia is low and margins thin),
and the guard removes that asset by refusing any datum fresher than a week, so the motive that drives the most
dangerous attacker class has no target here (section 6, and the same argument in `nis-oes-gap-assessment.md` section 6).

### 4.3 A3 Asset management

**Status: Achieved.**

Data-asset provenance is a first-class, enforced concept: every emitted series carries a `ProvenanceTag` (`Recorded`,
`DerivedFromRecorded`, `CuratedPlaceholder`, `Synthetic`, `Counterfactual`, `Interpolated`, `UnvalidatedAtScale`), and
`ProvenanceTrust` refuses an output tag (`Counterfactual`, `UnvalidatedAtScale`) offered as an input, at the replay entry
and at the case editor's save gate, and refuses a computed tag raised above its inputs, so a tag only moves down through
a computation. No code can check that a `Recorded` tag in a hand-written file is backed by a record, and the
forward-inference guard checks time stamps only and never reads a tag; a correct `Recorded` tag on an authored input
is a rule for authors and reviewers. Knowing the trust class of every datum is the CAF A3 outcome for information
assets. `AssetProvenanceTests` pins the tags on asset data.

Software-asset inventory: the release workflow builds two CycloneDX software bills of materials (SBOMs), one for the
CLI and one for the web host, and attaches them to the GitHub Release next to the artefacts they describe.
`THIRD-PARTY-NOTICES.md` names each shipped package with its licence (`ThirdPartyNoticesTests`). The restore graph is
now recorded: per-project `packages.lock.json` lock files (`RestorePackagesWithLockFile=true` in
`Directory.Build.props`) record the resolved dependency set. No CI or release restore runs in locked mode
(`RestoreLockedMode` or `--locked-mode`), so a restore that resolves differently rewrites a lock file rather than
failing, and the SBOM job restores on its own rather than reusing the publishing job's restore; the SBOM therefore
describes the graph its own restore resolved, which matches the shipped build while both resolve the committed lock
files. `BuildProvenance`
(`GridSim.Core.Diagnostics.SupplyChain`) exposes the version, commit, toolchain (the .NET SDK version stamped at build
time), SBOM hash and deterministic build hash at runtime, so an emitted estimate or audit entry can be correlated with
the build that produced it. The build hash is an identity label over those metadata strings, not an integrity proof:
no byte of the shipped files is hashed into it (section 4.5). Running the workflow on a GitHub tag (so the SBOMs exist
as published artefacts rather than as a built control) is a deploying-maintainer action at the first tagged release,
not a code gap. Evidence: `SupplyChainManifestTests`, `ThirdPartyNoticesTests`.

### 4.4 A4 Supply chain

**Status: Achieved in-component (low intrinsic exposure; lock files and build provenance now pin the graph and the
build).**

GridSim.Core is deliberately close to the base class library - the measurement model, solvers and guard use
`System.Numerics`, `System.Security.Cryptography`, `System.Text.Json` and little else, so the third-party attack
surface is small by construction. The controls that keep it small:

- the per-release SBOMs (A3) and `THIRD-PARTY-NOTICES.md`;
- per-project `packages.lock.json` lock files (`RestorePackagesWithLockFile=true` in `Directory.Build.props`) that pin
  the restore graph, and `BuildProvenance` (`GridSim.Core.Diagnostics.SupplyChain`) exposing version, commit,
  toolchain, SBOM hash and build hash at runtime. The release workflow generates the SBOMs first and builds the CLI,
  the web image and the NuGet package with the SBOM's SHA-256 injected (`-p:GridSimSbomSha256=<hash>`, the
  `SBOM_SHA256` build argument for the image); each published CLI binary must then report `release build  yes` and
  exactly that SBOM hash from `gridsim compliance verify`, or the release stops;
- a signed SLSA build-provenance attestation (`actions/attest-build-provenance`, Sigstore) over every file on the
  GitHub Release (the CLI archives, the SBOMs and `SHA256SUMS.txt`), checked with
  `gh attestation verify <file> -R <owner>/GridSim`;
- a vulnerable-package gate (`dotnet list package --vulnerable --include-transitive`) in CI and in the release;
- NuGet audit warnings fail the build (through `TreatWarningsAsErrors`);
- CodeQL analysis of the C# (`codeql.yml`, started by hand only) and Dependabot over NuGet, Docker and GitHub Actions
  (monthly). GitHub Actions are referenced by version tag, not pinned to a commit SHA;
- release signing: the Windows CLI binary is Authenticode-signed and the NuGet package is signed with one self-signed
  certificate (`signing/README.md`). The verify steps trust exactly that certificate: the committed public `.cer` is
  imported into the runner's `LocalMachine\Root`, `signtool verify /pa` must pass and the signer thumbprint must equal
  the pinned one; the NuGet verify appends the certificate to the runner SDK's code-signing root bundle and passes
  `--certificate-fingerprint`. The Linux and macOS archives, `SHA256SUMS.txt` and the container image are not signed.
  When the signing secret is not configured, signing is skipped with a warning and the release is unsigned
  (`release.yml`, `publish.yml`, pinned by `WorkflowContractTests`).

Evidence: `SupplyChainManifestTests` (lock-file pin and build provenance), `WorkflowReleaseGateTests`, plus the
existing `WorkflowContractTests` and `ThirdPartyNoticesTests`.

**Continuous integration cadence (a deliberate, risk-accepted deviation).** CI runs when the release workflow calls it
on a release tag, or by hand; CodeQL runs by hand only. Neither runs on every push. CAF is outcome-based, and the
outcome CAF asks for - every shipped change built and checked deterministically - is met at the release: `release.yml`
calls `ci.yml` as a reusable workflow (job `ci`) and every publishing job needs it, so a release exists only after the
full matrix passed (Linux and Windows in the normal, AVX2-off, hardware-intrinsics-off, forced AVX-512 and FMA-off
modes, and macOS on Apple silicon in the normal and hardware-intrinsics-off modes; no leg is advisory). Between
releases, day-to-day verification is the local batch run. The deviation is recorded and risk-accepted in
[`security-governance.md`](security-governance.md). The remaining deploying-maintainer action is to run the tagged
release so the SBOM, provenance and signing steps execute on GitHub, and to run CodeQL by hand before it.

### 4.5 A4.b Secure software development and support (new outcome in v4.0)

**Status: Achieved (the attestation mechanism); its verdict honestly reads NOT COMPLIANT at this release (below).**

v4.0 adds A4.b, which asks a supplier to run an established secure-development framework, to know the composition and
origin of its software and monitor its components for vulnerabilities, to secure its development, test and production
environments and its source and repositories, to develop against a threat model, and - the load-bearing new indicator
- to **attest to the authenticity and integrity of software, including updates and patches**. GridSim meets these:

- **Established secure-development framework.** The development process maps to NIST SSDF (SP 800-218) and the
  Microsoft SDL; the mapping is in [`ssdf-mapping.md`](ssdf-mapping.md).
- **Known composition and origin, monitored for vulnerabilities.** The SBOMs, lock files, `THIRD-PARTY-NOTICES.md`,
  the vulnerable-package gate, CodeQL and Dependabot (A3/A4).
- **Secured dev/test/prod and source/repos; threat-informed development.** The zero-warning analyser gate
  (section 10.5), the restriction review discipline (section 3), and the threat model in technical ch. 35 and
  [`risk-register.md`](risk-register.md).
- **Authenticity and integrity attestation.** Two separate things meet this indicator, and they must not be confused.
  - *Integrity and origin of a download* come from outside the binary: the signed SLSA build-provenance attestation
    over every Release file (`gh attestation verify <file> -R <owner>/GridSim`), `SHA256SUMS.txt`, and the Authenticode
    signature of the Windows binary (section 4.4). These bind the published bytes to the workflow run and commit that
    built them.
  - *What a running binary states about itself* is the compliance attestation. `ComplianceManifest`
    (`GridSim.Core.Diagnostics.SupplyChain`) is the machine-readable control inventory, each control bound to its
    evidencing test and given a status that is maintained by hand in source (an inventory, not a measurement).
    `ComplianceAttestation` binds the manifest hash to the build identity (version, commit, SDK, SBOM hash and the
    build hash over them). The build hash is an identity label, not an integrity proof: it covers no artefact bytes,
    so a modified binary that keeps the same metadata reports the same hash. `IsCompliant` needs BOTH a release build
    (the SBOM hash was injected by the release workflow, which builds only after the full test matrix passed) AND every
    in-scope control recorded Achieved. A local or development build always reports
    `NOT ATTESTED (not a release build...)` and `gridsim compliance verify` exits 1 for it; the report lists each
    in-scope control not recorded Achieved. The web `/compliance` endpoint returns the same fields plus a
    `notAchieved` list. At this release three controls are recorded Partially achieved (`rbac`, `distrusted-source`,
    `resilience`; section 9), so even a release build reads `NOT COMPLIANT` until they are closed.

  This rests on the build provenance (A4) and the SLSA build-track provenance mapped in
  [`slsa-provenance.md`](slsa-provenance.md). Evidence: `ComplianceAttestationTests` (which also pins exactly which
  controls are not yet Achieved), `SupplyChainManifestTests`, `WorkflowReleaseGateTests`.

---

## 5. Objective B - Protecting against cyber attack

CAF principles B1 policies/processes, B2 identity and access control, B3 data security, B4 system
security, B5 resilience, B6 staff awareness.

### 5.1 B1 Service protection policies and processes

**Status: Achieved in-component.**

The restriction invariant and its review gate (section 3) are a codified, enforced protection process for the
highest-value asset. The 0-warning build gate (section 10.5) is a codified code-hygiene process. A secure-configuration
baseline for the services is documented in `docs/developer/deploy/` and technical ch. 35:

- the container runs as a non-root user and publishes its port on `127.0.0.1` only; the systemd unit binds
  `127.0.0.1:5540` and sets `NoNewPrivileges`, `ProtectSystem=strict` and `PrivateTmp`;
- TLS terminates at the reverse proxy (the nginx example), not in the application;
- the web host trusts forwarded headers only from an allow-listed proxy (`TrustedProxies`);
- the production configuration runs replay-only (B2).

The transport and resource-limits gaps are closed: `GridBridgeClient` refuses an unencrypted `ws://` endpoint unless
explicitly allowed (B3 transport); on the web host a `ws://` bridge endpoint stops start-up unless
`GridSim:BridgeAllowUnencrypted=true` (the desktop app's setting is `BridgeAllowUnencrypted`), for a bridge on a
private network or loopback. `ResilienceOptions` (`GridSim.Core.Net`) carries the resource-limits baseline (B4) and
is applied to every bridge client. Evidence: `BridgeEndpointSettingTests`, `ResilienceOptionsTests`. Setting `read_only`, `cap_drop` and `no-new-privileges` on the compose file remains a deploying-party hardening
step, documented in `docs/developer/deploy/`.

### 5.2 B2 Identity and access control

**Status: Partially achieved.** Authentication and exposure controls are in place, and the shipped exposure control in
production is the replay-only gate. The role-based access-control gate is built and tested as a primitive only: no
hosting surface (no endpoint, CLI verb or hosted service) calls it at this release, so no request is ever authorised by
role and no access denial is ever written to the audit trail. The manifest records `rbac` as Partially achieved for
that reason.

What exists at this release:

- **Authentication in the web host.** Cookie sign-in (a challenge answers 401 and a forbid 403, never a redirect)
  and a single sign-on consumer (`/sso/bridge`, `/sso/land`) that accepts single-use, audience-checked tickets and
  refuses to act as an authority (`Sso:IsAuthority=true` stops start-up). Pinned by `WebSecurityDoc2Tests`.
- **The replay-only gate.** `GridSim:ReplayOnly` (on in the production configuration) leaves `/api` and `/ws/sim`
  unmapped, answers 404 for other dashboards, and makes `SessionManager.Create` refuse
  (`ReplayOnlyRefusedException`). On a production instance the solve surface does not exist, so there is nothing to
  authorise. Pinned by `WebSecurityDoc2Tests` and `WebHostDoc2Tests`.
- **Exposure by design.** `serve` binds no network port; `GridBridgeClient` is an outbound client; the anonymous-visitor
  rule keeps every page usable without signing in, and sign-in only shows "signed in as".

Role-based access-control gate (built and tested as a primitive, in `GridSim.Core.Security`). It is the mechanism
intended to authorise the solve surface when that surface is exposed; at this release no hosting surface calls it, so
it does not authorise `/api` or `/ws/sim` (the shipped exposure control there is the replay-only gate, and existing
constitutional tests reach the solve surface without a role):

- `OperationRole` enum: `Viewer`, `Analyst`, `Operator`, `Administrator` - least-privilege bands.
- `IdentityContext` (record): the authenticated principal id, role, and a correlation id, taken from the signed-in
  cookie or SSO claims at the service boundary.
- `RoleGuard.Require(IdentityContext, OperationRole)` - a fail-loud gate, mirroring
  `RestrictionReview.AssertPermitted` in spirit (throws a typed `AccessDeniedException`), intended for the *service*
  boundary, never inside the inner solver loop where it would cost per-iteration overhead and pollute the numerics.
- The audited overload writes a `SecurityEventKind.AccessDenied` audit entry for each deny (section 6.1), tying B2 to
  C1; because no hosting surface calls the gate, no product path writes that event today.
- The gate is *additive to* the forward-inference guard, never a replacement. The two are independent: the guard
  consults no role and has no bypass, so even an Administrator cannot obtain sub-169h data, and the guard caps what any
  caller can learn (week-stale reconstruction only) regardless of role.

Evidence: `RoleGuardTests` (each role against each operation; deny is audited; an invariant that no role reaches
sub-169h data). The gap: wire the gate, with the host's audit writer, into the solve and resident-estimator control
surfaces (section 9).

### 5.3 B3 Data security

**Status: Achieved in-component.** Strong on the data boundary; secrets management and the no-secret-in-audit
invariant are now built and tested.

- **Data-boundary integrity (Achieved):** the forward-inference guard is the definitive B3 control on *what data may
  enter*. It is the reason the sensitivity of GridSim's data holdings is bounded.
- **Provenance/trust labelling (Achieved):** `ProvenanceTag` on every series.
- **Output integrity / tamper-evidence (Achieved):** the hash-chained audit trail, persisted and anchored, makes
  emitted-estimate records tamper-evident; with the optional detached signature over the anchor (section 6.2) a joint
  rewrite of the log and its anchor is detectable by anyone who lacks the private key.
- **Data-protection keys at rest (Achieved in-component):** the web host's key ring is encrypted at rest with DPAPI on
  Windows by default, and with a PKCS#12 certificate elsewhere when `Auth:KeyCertificatePath` is set (with
  certificate rotation); the certificate password is resolved through `ISecretStore` (`KeyRingProtection`) rather than
  read from plain configuration; a protection setting that cannot be honoured stops start-up (pinned by
  `WebSecurityDoc2Tests`). On Linux without a certificate the keys are stored unencrypted with a start-up warning;
  providing that certificate is a deploying-party step.
- **Licensing:** only an ECDSA P-256 public key is embedded, so a binary can verify a licence but cannot mint one.
  The release signing key is held in repository secrets only; the `signing/` folder holds the public certificate. The
  licence key is signed, not encrypted (it is inspectable, not a confidential secret), so it needs no secret store.
- **Secrets management (Achieved):** `ISecretStore` (`GridSim.Core.Security`), with `EnvironmentSecretStore`,
  `DirectorySecretStore`, `CompositeSecretStore` and `NullSecretStore`, is BCL-only (no new third-party dependency).
  `DirectorySecretStore` reads one file per secret, the form Docker and Compose secrets (`/run/secrets`), a Kubernetes
  secret volume and systemd credentials (`$CREDENTIALS_DIRECTORY`) take. The web setting `Secrets:Directory` puts it in
  front of the environment for the key-ring certificate password, so that password need not be in configuration or
  the environment. No OS-keychain or vault backend ships; a deployer can supply their own `ISecretStore` in code.
  Evidence: `SecretStoreTests`, `DirectorySecretStoreTests`, `NoSecretInAuditTests`.
- **No secret in the audit trail (Achieved):** audit summaries are plaintext and hashed, so they must stay
  secret-free. `AuditEvent` records inputs by SHA-256 and sanitises the actor's user and machine names, and a test now
  enforces that no secret appears in any audit summary (`NoSecretInAuditTests`).
- **Audit log at rest (Achieved):** the audit chain is persisted append-only with a separate anchor, and
  `SignedAuditAnchor` adds an optional detached ECDSA P-256 signature over the anchor (`--audit-sign-key`,
  `Audit:AnchorSigningKeyPath`; section 6.2), so a rewrite of log and anchor together is detectable by anyone who
  lacks the private key. Every entry can also be forwarded as a JSON line to a second file (`--audit-forward`,
  `Audit:ForwardPath`), which a deploying party points at a SIEM collector or a write-once mount (section 6.1).

### 5.4 B4 System security

**Status: Achieved in-component.**

- **Enforced-by-construction invariants (Achieved):** the guard, the review gate, and the physics acceptance gate
  (`FeasibleWlsStep` Ring C, which rejects a physically-impossible converged state as `EstimationStatus.Infeasible`)
  are all fail-loud, non-bypassable system controls. The estimator never emits NaN in place of an honest
  `Unobservable`/`NonFinite`/`Infeasible` status - a robustness property that doubles as a security property (no
  silent degradation an attacker can hide behind).
- **Input validation (Achieved for measurements):** `MeasurementSet.Validate(model)` runs before estimation; branch
  measurements resolve against real topology; malformed rows fail loud (`InvalidDataException`) rather than producing
  a quietly-wrong state.
- **Build hardening (Achieved):** `TreatWarningsAsErrors=true`, `EnableNETAnalyzers=true`, `AnalysisLevel=latest`,
  `AnalysisMode=Recommended`, `Nullable=enable` in `Directory.Build.props` (section 10.5). A whole class of latent
  defects cannot ship.
- **Resource limits (Achieved in-component):** the web host caps concurrent sessions (`GridSim:MaxSessions`, default
  64, `SessionLimitException`), closes a `/ws/sim` control frame over 16 KiB (`MessageTooBig`), and the Modbus
  transport caps the frame length; the audit-log viewer caps what it reads. `GridBridgeClient` now caps the
  single-message size (default 8 MiB) and the in-flight-query count (default 256), and `MeasurementSet.Load` caps the
  measurement count, all carried by `ResilienceOptions` (`GridSim.Core.Net`), which the web and desktop hosts apply to
  every bridge client they create. Evidence: `ResilienceOptionsTests`, `BridgeEndpointSettingTests`.
  Request rate limiting at the network edge remains a deploying-party reverse-proxy concern.

### 5.5 B5 Resilient networks and systems

**Status: Partially achieved in-component (no host drives the reconnect schedule); not applicable to the library.**

The library is deterministic and stateless per call (section 10.3), so it has no availability state to lose. The
services do. What exists: `GridBridgeClient` fails every pending query when the stream closes or faults (the `Faulted`
event) and times out a query after 60 s (`QueryTimeout`), so no awaiter is orphaned
(`BridgeClientRobustnessTests`); the real-time estimator continues past a failed window by default
(`ContinueOnError`); the container health check probes the web host.

What is built:

- `ResilienceOptions` (`GridSim.Core.Net`): the resource limits (max in-flight queries, max message bytes) and the
  transport policy (`ws://` refused unless allowed), applied by `ApplyTo` to every bridge
  client the web and desktop hosts create; and a bounded exponential reconnect backoff schedule
  (`ResilienceOptions.BackoffDelay`). **The gap:** no host drives that schedule at this release, so there is no
  supervised auto-reconnect loop; a dropped bridge stream fails its pending queries (the `Faulted` event) and is not
  reconnected automatically.
- `ServiceLevelObjective` + `HealthProbe` (`GridSim.Core.Simulation.Health`) fold the engine's `RealtimeRunSummary`
  into a `HealthReport`. At the end of a `gridsim serve` run with `--audit-log`, and of the web resident estimator's
  run with `Audit:LogPath`, an SLO breach is recorded as an `AvailabilityDegraded` security event, which becomes an
  incident when `--incident-log` / `Audit:IncidentLogPath` is set (section 7.1). The `/health` endpoint itself raises
  nothing: it answers 503 on a breach so an orchestrator can act.
- There is no degraded-mode fallback: no engine or host keeps or serves a last-good estimate with a stale tag. A
  window that fails is skipped and counted, and the resident loop carries on with the next one
  (`RealtimeEstimatorOptions.ContinueOnError`, true by default); the SLO verdict above reports a run that fails too often.
- Determinism is the recovery ally: because a replay is bit-reproducible (section 10.3), a crashed service recovers
  by re-running the same window and getting the identical state - no divergent recovered state to reconcile.

Evidence: `ResilienceOptionsTests`, `BridgeEndpointSettingTests`, `HealthProbeTests` (the SLO verdict),
`AuditWiringTests` (a security event recorded and raised as an incident), plus the existing
`BridgeClientRobustnessTests`.

### 5.6 B6 Staff awareness and training

**Status: Out of scope / deploying-party**, with one engine-side contribution: the codified restriction doctrine
(`UnderstandingRestrictions.md` and the `RestrictionReview.Reviewed` register) *is* the security-culture artefact for
anyone extending GridSim. The doc's "if you add a data source, it goes through the guard; if you connect dynamics to
the estimator, stop and check this document first" is the training. `CONTRIBUTING.md` states the acceptance criteria
every change must meet.

---

## 6. Objective C - Detecting cyber security events

CAF principles C1 security monitoring, C2 proactive security event discovery. This objective is where
GridSim's built controls are strongest after the guard.

### 6.1 C1 Security monitoring - the hash-chained audit trail

**Status: Achieved.** A tamper-evident, persisted, anchored trail (with an optional signed anchor), with one audit entry
per security event at the detection sites of the audited product paths (v4.0 expands the monitoring expectation, and
C2 adds threat hunting; both are met here and in section 6.4). The event kinds that have no product detection site yet
are listed below.

`AuditLog` (`GridSim.Core.Diagnostics`) is the evidence-trail control CAF C1 asks for. Each `AuditLogEntry` records
what was emitted and chains to its predecessor:

```
AuditLogEntry(
    Sequence,            // 0-based position
    RunTimestampUtc,     // wall-clock of the write (run metadata, NOT a grid claim)
    StateTimeUtc,        // the present/past grid instant the record describes (nullable)
    Provenance,          // ProvenanceTag - trust class of the output
    Summary,             // what ran, on what, with what outcome
    PrevHash,            // hash of the previous entry ("GENESIS" for the first)
    Hash)                // SHA-256 over (Sequence|RunTs|StateTs|Provenance|Summary|PrevHash)

AuditLog.Append(runTs, stateTs, provenance, summary) -> AuditLogEntry   // chains to current tail
AuditLog.Verify() / Verify(entries) -> bool                             // re-derives every hash + chain
```

Timestamps are normalised to UTC before hashing, and a missing state time hashes as `-`, so the same record always
hashes the same way.

**Persistence and verification.** `AuditLog` itself is in memory; `AuditLogWriter` appends the same chain to a file
(`gridsim-audit/1`, one JSON line per entry, UTC timestamps, flushed to disk on every append), with one writer per log
enforced by an exclusive lock that fails loud. Opening verifies the existing file and refuses a damaged one.
`AuditLogFile.Verify` / `Load` recompute the chain from the file and name the first broken entry and why (edit,
deletion, insertion, reordering, truncation, hand reformatting). `AuditEvent.Record` is the product-path API (who /
what / when / input SHA-256 / outcome).

**Anchoring.** After each run the writer's anchor (entry count and head hash, `AuditAnchor`) is written to an anchor
file (`AuditAnchorFile`, `gridsim-audit-anchor/1`; by default `<log>.anchor`), and every later open checks the log
against it, so truncation from the end and a whole-log rewrite are caught. The anchor can be kept away from the log
(`--audit-anchor <file>`, `Audit:AnchorPath`); a log that has entries but no anchor file records
`audit-anchor=missing` in its next entry.

**Who writes it.** `--audit-log <file>` on `estimate`, `crossval`, `interpose` and `serve`; the web host's resident
estimator through `Audit:LogPath`. `gridsim audit verify` and `gridsim audit show` check and display a log, and the web
and desktop apps have an AUDIT LOG viewer.

CAF-relevant properties:

- **Tamper-evident.** Altering, reordering, inserting or deleting any entry breaks the SHA-256 chain from that point
  on; deleting from the end is caught by the anchor. Pinned by `AuditLogTests`, `AuditLogDeletionTests`,
  `AuditLogFileTests` and `Doc3AuditAnchorTests`.
- **Provenance-bound.** Each record carries the emitted output's `ProvenanceTag`, so the log answers not just "what
  happened" but "how trustworthy was the datum" - the two questions C1 wants coupled.
- **Restriction-aware by design.** The record separates `RunTimestampUtc` (wall clock, metadata) from `StateTimeUtc`
  (the grid instant, present/past only). The log can never be read as a future-dated grid claim because the field
  that carries a grid instant is definitionally the filtered present/past.

**What is logged today.** One entry per run (plus a start entry for `interpose`), carrying the run's outcome, and one
`action=security` entry per security event at each detection site listed below
(`AuditRunRecorder.RecordSecurityEvent`):

| Security-relevant event | Recorded today |
|---|---|
| (a) an emitted estimate | `estimate` records the outcome (method, status, observability, J, chi-square verdict, removed measurements); `serve`, the web host and `interpose` record aggregate counts; `crossval` records its verdict. Counterfactual and export verbs are not audited. |
| (b) a rejected too-fresh datum (`ForwardInferenceException`) | a `HorizonViolation` entry: `estimate` when its input is refused by the horizon guard; `serve` when `--utc-now` is refused, for each window refused as too fresh at load, for windows the engine refused (one entry naming the count) and when the run is stopped by the guard; the web resident estimator when it is stopped by the guard and for windows the engine refused. `interpose` writes one `LiveDataQuarantined` entry per window held as a quarantine capture. |
| (c) a refused output or dynamics fusion (`RestrictionViolationException`) | `serve` writes a `RestrictionRefused` entry when the output guard refuses an estimate; the web host records the exception type in the run's outcome; `estimate` records only the exit code. |
| (d) a suspected FDIA (`FdiaScreen`) | a `SuspectedFdia` entry from `estimate` when `--fdia-screen` is given and the screen reads suspected. |
| (e) an `Infeasible` / `Unobservable` estimate | an `InfeasibleState` entry from `estimate`, from each infeasible `serve` window (WLS method) and from each infeasible web resident-estimator window; `Unobservable` is in the outcome and the counts only. |
| (f) a breached service-level objective | an `AvailabilityDegraded` entry at the end of a `serve` run and of the web resident estimator's run (B5). |
| (g) an access denial | not recorded by any product path: the `RoleGuard` audited overload writes an `AccessDenied` entry, but no hosting surface calls the gate (B2). |
| (h) a distrusted source or a dependency alert | not recorded: no product path applies `DistrustedSources` and nothing produces a `DependencyAlert` event at this release. |
| (i) a broken audit chain | refused, not logged in the same file: a log that does not verify stops the run before it starts (CLI exit 1, web start-up failure) and is reported on stderr. The `AuditChainBroken` event is deliberately not written into the log whose chain cannot be trusted. |

**Per-security-event audit (wired).** `SecurityAudit.Record` (`GridSim.Core.Diagnostics`), reached through
`AuditRunRecorder.RecordSecurityEvent`, writes one audit entry per security event with its reason (`action=security`),
typed by `SecurityEventKind` and graded by `SecuritySeverity` (the kind's baseline by default). This needs `--audit-log`
on the CLI or `Audit:LogPath` on the web host; without a log nothing is recorded. A failure to record an event is
loud (stderr, exit 1).

**Forwarding.** `--audit-forward <file>` (CLI) or `Audit:ForwardPath` (web) attaches `JsonLinesAuditSink`, which
appends every entry, all fields including both hashes, as one JSON line to a second file: the input for a SIEM
collector or a copy on a write-once mount. Sinks are notified by `AuditLogWriter` after the durable append, so a
forwarder failure never fails the append; the CLI reports it at the end of the run and exits 1 (the web host does not report it at this release). Each of these
settings needs `--audit-log` / `Audit:LogPath`, or the CLI refuses the command line (usage error) and the web host
refuses to start. Evidence: `SecurityAuditTests`, `AuditWiringTests`.

### 6.2 Honest limits of the audit trail

The hash chain is tamper-**evident**. The former "unsigned anchor" limit is closed by an optional signed anchor:

- **Signed anchor.** `SignedAuditAnchor` (`GridSim.Core.Diagnostics`) adds a detached ECDSA P-256 signature over the
  `AuditAnchor`, so a joint rewrite of the log and its anchor is detectable rather than recomputable into a consistent
  pair. It is wired into the product paths: `--audit-sign-key <PEM P-256 private key>` (web
  `Audit:AnchorSigningKeyPath`) signs the anchor after every entry (`<log>.anchor.sig`); `--audit-verify-key <PEM public
  key>` (web `Audit:AnchorVerificationKeyPath`) requires the signature to exist and verify before the run starts (CLI
  exit 1, web start-up failure otherwise); `gridsim audit verify <log> --pubkey <pem> [--anchor f] [--sig f]` checks it
  offline (`TAMPERED`, exit 3, on a missing or bad signature); `gridsim audit keygen <private.pem> <public.pem>` makes
  a key pair. There is no embedded public key: the verifier is the public key the deployer supplies. **The trust
  model, stated honestly:** a host that holds the private key (the `--audit-sign-key` form) only protects against an
  attacker who does not have that key; an attacker with the host's privileges can re-sign a rewritten anchor. The
  strong form keeps the private key off the host and gives the host only the public key. GridSim ships no separate
  offline signing verb: the signature line is a documented ECDSA P-256 / SHA-256 signature over the exact anchor line
  (`gridsim-audit-anchor-sig/1`), so the off-host form signs each new anchor with the deployer's own tooling, and a
  host running with only `--audit-verify-key` refuses its next run until the anchor its last run wrote has been
  signed. Evidence: `SignedAuditAnchorTests`, `AuditWiringTests`. The anchor
  may still be kept where the log's writer cannot change it (`--audit-anchor`, `Audit:AnchorPath`, write-once media)
  as defence in depth.
- **Write-once or remote copy.** `--audit-forward` / `Audit:ForwardPath` appends every entry to a second file
  (section 6.1); pointing that file at a WORM mount or a SIEM collector is a deploying-party choice.
- **Time source integrity (residual, deploying-party).** `RunTimestampUtc` trusts the host clock, so a deploying party
  should record a clock-source / skew note; the `Sequence` field, not wall-clock order, is the ordering authority for
  every security decision.

### 6.3 C1 - second-layer intrusion detection: FdiaScreen

**Status: Achieved.** The out-of-envelope sub-class and the feasible-landing sub-class are both covered now that the
screen takes PMU-anchored protected measurements.

`FdiaScreen.Screen(model, EstimationResult, FeasibilityConstraints?, physicsTolMva?, significanceSigmas = 3)` is a
detection control for residual-cloaked False Data Injection. The threat is real and provable: an attacker who replaces
the measurements z = h(x) with z' = h(x + c) (to first order, adds a = H c, in the range of the measurement Jacobian)
leaves the residual unchanged, passes chi-square and the largest-normalised-residual test, and shifts the estimated
state by c completely undetected (Liu-Ning-Reiter). Conventional bad-data detection is *provably bypassable*, so a
second, independent detector is required - and GridSim already has an independent validator the attacker must also
satisfy: the physics-conformance audit and the operational envelope.

```
FdiaScreenReport(
    ChiSquarePassed,              // did conventional bad-data detection accept the estimate?
    PhysicallyImpossible,         // hard law breached (PhysicsCheckRegistry.HardGateLaws)
    LimitViolations,              // every voltage-band / thermal / generator-Q breach
    SuspectedFdia,                // ChiSquarePassed AND (PhysicallyImpossible OR any significant breach)
    Summary,
    SignificantLimitViolations)   // the breaches larger than significanceSigmas x the estimate's uncertainty
```

A limit breach counts only when it exceeds k standard deviations of the estimated quantity (k = 3 by default, the same
three-sigma rule the bad-data tests use), with the standard deviation bounded by the sigma of the telemetry that
measures it. A true state sitting on a limit therefore does not raise a false alarm from noise. The screen refuses to
screen an estimate that did not converge, or whose chi-square test did not run, rather than reading "not run" as a
pass.

Two paths raise the flag:

- **Out of envelope.** A perfectly cloaked AC attack produces a self-consistent state, so its projected power flow
  satisfies every conservation law by construction; it is caught when that state lies significantly outside the
  operational envelope. `FdiaCloakedAttackTests` builds exactly this attack (z' = h(x + c) with one bus driven to
  1.20 pu), proves the cloak (the estimate recovers x + c with J at round-off and chi-square passing), and asserts
  the screen flags it.
- **Physically impossible.** An estimate whose projection is not self-consistent breaks a hard law and is flagged
  through `PhysicallyImpossible`.

**The feasible-landing sub-class (now closed):** a perfectly crafted attack that lands on another *feasible, in-limit*
operating point leaves the envelope and physics checks satisfied, so it stood as an accepted residual risk.
GridSim ingests synchrophasors (C37.118 `pmu-ingest`, `LinearPmuStateEstimator`), and `FdiaScreen.Screen` now takes
`protectedAnchors`: a feasible, in-limit cloaked attack that disturbs a PMU-anchored quantity is flagged
(`FdiaScreenReport.AnchorInconsistent`), because the attacker cannot both cloak against the WLS residual and match an
independently measured PMU anchor. This closes the feasible-landing residual that earlier editions accepted. The screen
**reports**; it never silently rejects. The `FeasibleWlsStep` acceptance gate (Ring C) is the enforcing sibling: where
`FdiaScreen` reports, the feasible method *rejects* a physically-impossible converged state as `Infeasible`.

Evidence: `FdiaProtectedAnchorTests` (the feasible-landing attack flagged through `AnchorInconsistent`),
`FdiaCloakedAttackTests` (the cloaked attack; a chi-square failure reported as "conventional bad-data detection
applies", not as a cloaked attack; refusal when chi-square did not run) and `FdiaScreenTests` (clean estimate not
flagged; out-of-band state flagged; a breach inside three sigma listed but not counted, one beyond it counted; clean
case118 telemetry not flagged; false-positive rate at most 2% over 100 noisy seeds).

### 6.4 C2 Proactive security event discovery

**Status: Achieved.** The physics-conformance audit is a proactive-discovery control: it finds implausible
states nobody asked it to look for. Determinism and replay (section 10.3) are a forensic discovery tool - re-running a
suspect window reproduces the exact state for investigation. v4.0 adds an explicit threat-hunting expectation, and the
aggregation that meets it is built: `SecurityEventMonitor` (an `IAuditSink` in `GridSim.Core.Diagnostics.Incident`)
tallies security-event kinds and escalates a burst to one latched incident, deterministically (default thresholds:
20 `HorizonViolation`, 3 `SuspectedFdia`, 1 `AuditChainBroken` events). It is attached to the product audit log with
`--incident-log <file>` (CLI) or `Audit:IncidentLogPath` (web), beside the single-event incident sink (section 7.1).
It consumes the audit trail; it does not replace it. Evidence: `SecurityEventMonitorTests`, `AuditWiringTests`.

---

## 7. Objective D - Minimising the impact of cyber security incidents

CAF principles D1 response and recovery planning, D2 lessons learned.

### 7.1 D1 Response and recovery planning

**Status: Achieved in-component, unusually strong on the "impact" half; the distrusted-source containment control is
Partially achieved (below).**

The defining D1 property is already true by construction: **the maximum impact of a full GridSim compromise is bounded
to week-stale reconstruction** (section 3.2). An adversary who owns the process, the disk, and the network still
cannot obtain live or forward operational visibility of the GB grid, because the forward-inference guard refuses the
timestamp before any code runs. CAF D1 asks you to minimise impact; GridSim minimised it in the architecture, not the
incident playbook.

Recovery is aided by two built properties:

- **Deterministic estimate and replay (section 10.3):** recovery from a crash or a suspected-tamper incident is
  "re-run the window and compare". An estimate is bit-reproducible from `(model, measurement set, options)` and a
  replay is bit-reproducible run to run, so a recovered state can be checked against the audited one bit for bit - a
  divergence *is* the incident signal.
- **Tamper-evident trail (section 6):** `gridsim audit verify` is the first forensic step; a broken chain localises
  tampering to a sequence range, and the anchor shows whether entries were removed from the end.

Live telemetry captured by the RTU interposer is written to a quarantine store (`QuarantineLiveStore`) that refuses
any path inside the evidence lake, so live data cannot contaminate the historical record.

The incident-response runbook exists at [`incident-response.md`](incident-response.md) (who is called, how the service
is isolated, how a poisoned measurement source is quarantined), and the reporting and containment controls are built:

- **Incident hooks (C1/D1; NIS Reg 11).** `IncidentReport`, `IIncidentSink` (`NullIncidentSink`,
  `BufferedIncidentSink`), `IncidentRouter` and `IncidentPolicy` (`GridSim.Core.Diagnostics.Incident`) surface a
  detection as a structured incident with a deterministic content-derived id; `StateTimeUtc` is horizon-checked at
  construction, so no incident can carry a future-dated grid claim. `SecuritySeverity.Significant` is the Regulation 11
  threshold. Wired by `--incident-log <file>` (CLI) or `Audit:IncidentLogPath` (web), which need the audit log:
  `IncidentWiring` attaches a `SecurityIncidentSink`, which raises one incident for each single event whose baseline
  severity is Significant or above (`SuspectedFdia`, `RestrictionRefused`, `DistrustedSource`, `AvailabilityDegraded`,
  `DependencyAlert`), and the `SecurityEventMonitor` for bursts (section 6.4). Incidents are written as JSON lines to
  that file. Each incident carries the hash of the audit entry that evidences it; incidents are not themselves written
  into the audit chain. Evidence: `IncidentRouterTests`, `AuditWiringTests`.
- **Distrusted-source control (D1 containment), Partially achieved.** `DistrustedSources` (`GridSim.Core.Estimation`)
  marks a measurement source distrusted and forces its rows to pseudo-measurement treatment (it inflates sigma, never
  changes a value). It is a primitive only: no product path (CLI verb or host setting) applies it at this release, and
  distrusted rows still count towards the chi-square redundancy. Until it is wired, the runbook's containment step is
  to withhold the source's data from the run. Evidence: `DistrustedSourceTests`.

**v4.0 D1 (tested plans, suppliers in scope).** v4.0 strengthens D1: a response plan must be exercised, and its scope
must extend to suppliers. The plan in [`incident-response.md`](incident-response.md) is to be exercised as a tabletop,
and upstream and supplier incidents - chiefly the GDA data lake GridSim draws from - are in scope: a poisoned or
compromised upstream source is handled by quarantine (`QuarantineLiveStore`) and, once it is wired into a product
path, by the distrusted-source control above. A `DependencyAlert` event would be raised as an incident by the incident
wiring, but nothing produces one at this release: dependency advisories surface through the vulnerable-package gate
and Dependabot (section 4.4), outside the running product.

### 7.2 D2 Lessons learned

**Status: Achieved in-component.** The restriction review register (`RestrictionReview.Reviewed`) and its tests are a
working lessons-learned loop for the one seam that matters. The horizon widening from 25 h to 169 h (recorded in the
`ForwardInferenceGuard` remarks with its owner-decision rationale and date) is a documented example of the loop
functioning. The general post-incident review template is folded into [`incident-response.md`](incident-response.md).
Running the organisation-wide lessons-learned process is the deploying party's.

---

## 8. Control-to-type traceability matrix

Every claim above resolves to a named type or artefact and its evidencing test. "Achieved in-component" marks a
control that is complete in GridSim with a named deploying-party step beside it; "Out of scope / deploying-party"
marks an outcome the host environment or operator organisation owns.

| CAF principle | Control | Type / artefact | Status | Evidence |
|---|---|---|---|---|
| Foundational / D1 | Forward-inference data boundary | `Diagnostics.ForwardInferenceGuard` at every dated seam (3.1) | Achieved | `ForwardInferenceGuardTests`, `RestrictionInvariantTests`, `GdaReplayLoopTests` |
| A1 / A2 | Dynamics->Estimation review gate | `Diagnostics.RestrictionReview` + `RestrictionReview.Reviewed` register | Achieved | `RestrictionInvariantTests` |
| A2 / B4 / C1 | Physics-conformance audit | `Physics.PhysicsLaws`, `Physics.PhysicsCheckRegistry.HardGateLaws` | Achieved | `PhysicsAuditTests`, `FeasibleKclGateTests` |
| A3 / B3 | Data-provenance labelling | `Diagnostics.ProvenanceTag` | Achieved | `AssetProvenanceTests` |
| A3 / A4 | SBOMs, dependency audit, code scanning | Two CycloneDX SBOMs attached to each GitHub Release (`release.yml`); vulnerable-package gate in CI and the release; NuGet audit warnings are build errors; CodeQL (`codeql.yml`, by hand only); Dependabot; `THIRD-PARTY-NOTICES.md` | Achieved in-component (running the workflow on a GitHub tag is a deploying-maintainer action; CI runs on a release tag through `release.yml` or by hand, CodeQL by hand only, as a risk-accepted deviation, section 4.4) | `ThirdPartyNoticesTests`, `WorkflowContractTests` |
| A4 | Lock files, build provenance and release provenance | `packages.lock.json` (`RestorePackagesWithLockFile=true`); `Diagnostics.SupplyChain.BuildProvenance` (version/commit/build SDK/SBOM-hash/build-hash, an identity label); SBOM hash injected into the CLI, web image and NuGet builds and checked on each CLI binary; signed build-provenance attestation over every Release file | Achieved | `SupplyChainManifestTests`, `WorkflowReleaseGateTests`, `WorkflowContractTests` |
| A4 | Release signing | Authenticode for the Windows CLI (`release.yml`), NuGet package signing (`publish.yml`), with one self-signed certificate trusted for verification only by its pinned thumbprint; Linux / macOS archives, `SHA256SUMS.txt` and the container image are not signed; signing skipped with a warning when the secret is absent | Achieved in-component (signing secret exercised at the first tagged release, a deploying-maintainer action; a CA-issued certificate is not yet in place) | `WorkflowContractTests`, `WorkflowReleaseGateTests` |
| A4.b | Secure-SDLC attestation | `Diagnostics.SupplyChain.ComplianceManifest` (hand-maintained inventory) + `ComplianceAttestation` (manifest hash bound to the build identity; `IsCompliant` only for a release build with every in-scope control recorded Achieved; a local build is NOT ATTESTED and `compliance verify` exits 1) | Achieved (the mechanism; the verdict reads NOT COMPLIANT at this release while three controls are Partially achieved) | `ComplianceAttestationTests` |
| B1 | Secure-configuration baseline | `docs/developer/deploy/`, technical ch. 35, `Site.TrustedProxies`, non-root container, systemd hardening | Achieved in-component (bridge `ws://` refused unless `GridSim:BridgeAllowUnencrypted=true`; compose hardening is deploying-party) | `Cf2OpenCodeFindingsTests` (trusted proxies), `BridgeEndpointSettingTests` |
| B2 | Authentication and exposure | Cookie sign-in, SSO consumer (`Sso.EstateSso`), replay-only gate (`GridSim:ReplayOnly`) | Achieved (as compensating controls) | `WebSecurityDoc2Tests`, `WebHostDoc2Tests` |
| B2 | Role-based access-control gate | `Security.OperationRole`, `IdentityContext`, `RoleGuard.Require`, `AccessDeniedException`; the audited overload writes `SecurityEventKind.AccessDenied` (primitive only: no hosting surface calls it; replay-only remains the production control) | Partially achieved | `RoleGuardTests` |
| B3 | Data-protection keys at rest | `Site.KeyRingProtection` (DPAPI / PKCS#12; certificate password resolved through `ISecretStore`) | Achieved in-component (Linux without a certificate is a deploying-party step) | `WebSecurityDoc2Tests`, `Cf3OpenCodeFindingsTests` |
| B3 | Secrets management | `Security.ISecretStore` (`EnvironmentSecretStore`, `DirectorySecretStore`, `CompositeSecretStore`, `NullSecretStore`), BCL-only; web `Secrets:Directory` | Achieved (no OS-keychain or vault backend ships) | `SecretStoreTests`, `DirectorySecretStoreTests`, `NoSecretInAuditTests` |
| B4 | Feasibility acceptance gate | `Estimation.FeasibleWlsStep` (Ring C) -> `EstimationStatus.Infeasible` | Achieved | `FeasibleEstimationTests`, `FeasibleKclGateTests` |
| B4 | Fail-loud status doctrine | `Estimation.EstimationStatus` (`Unobservable`/`NonFinite`/`Infeasible`) | Achieved | `ObservabilityTests`, `DenseEstimationLimitsTests`, `EstimationRobustnessFixTests` |
| B4 | Build hardening gate | `Directory.Build.props` (warnings-as-errors, analysers, nullable) | Achieved | CI build |
| B4 | Resource limits | `GridSim:MaxSessions` (64), `/ws/sim` 16 KiB frame cap, Modbus frame cap; `Net.ResilienceOptions` bridge message cap (8 MiB) and in-flight-query cap (256); `MeasurementSet.Load` measurement cap | Achieved in-component (edge rate limiting is deploying-party) | `WebHostDoc2Tests`, `ResilienceOptionsTests` |
| B5 | Service resilience / health | `Net.ResilienceOptions` (limits and transport policy applied to every bridge client; bounded `BackoffDelay` schedule not driven by any host); `Simulation.Health.ServiceLevelObjective` + `HealthProbe`; an SLO breach at the end of a `serve` / resident run is an `AvailabilityDegraded` security event | Partially achieved (no supervised reconnect) | `ResilienceOptionsTests`, `BridgeEndpointSettingTests`, `HealthProbeTests`, `AuditWiringTests`, `BridgeClientRobustnessTests` |
| C1 | Hash-chained audit | `Diagnostics.AuditLog`, `AuditLogEntry` | Achieved | `AuditLogTests`, `AuditLogDeletionTests` |
| C1 | Audit persistence | `Diagnostics.AuditLogWriter`, `AuditLogFile`, `AuditEvent`, `AuditRunRecorder` (`gridsim-audit/1`); `--audit-log` on `estimate`, `crossval`, `interpose`, `serve`; web host `Audit:LogPath`; `gridsim audit verify` / `show` | Achieved | `AuditLogFileTests`, `AuditRunRecorderTests`, `EstimationAuditTrailTests` |
| C1 | Audit anchoring | `Diagnostics.AuditAnchor`, `AuditAnchorFile` (`gridsim-audit-anchor/1`), checked on every open; `SignedAuditAnchor` (detached ECDSA P-256 over the anchor) via `--audit-sign-key` / `--audit-verify-key`, `Audit:AnchorSigningKeyPath` / `Audit:AnchorVerificationKeyPath`, `audit verify --pubkey`, `audit keygen`; `--audit-anchor`, `Audit:AnchorPath` | Achieved (protection against a key-holding host needs the private key off the host, section 6.2) | `AuditLogFileTests`, `Doc3AuditAnchorTests`, `SignedAuditAnchorTests`, `AuditWiringTests` |
| C1 | Per-event security logging | `Diagnostics.SecurityAudit.Record` via `AuditRunRecorder.RecordSecurityEvent` at the detection sites of `estimate`, `serve`, `interpose` and the web resident estimator; forwarded by `JsonLinesAuditSink` (`--audit-forward`, `Audit:ForwardPath`) | Achieved (no product site yet for `AccessDenied`, `DistrustedSource`, `DependencyAlert`; `AuditChainBroken` refused and reported on stderr, not logged, section 6.1) | `SecurityAuditTests`, `AuditWiringTests` |
| C1 / C2 | Residual-cloaked FDIA detection | `Estimation.Security.FdiaScreen`, `FdiaScreenReport` (incl. `AnchorInconsistent` with `protectedAnchors`) | Achieved (out-of-envelope and feasible-landing sub-classes) | `FdiaProtectedAnchorTests`, `FdiaCloakedAttackTests`, `FdiaScreenTests` |
| C2 | Security-event aggregation / threat hunting | `Diagnostics.Incident.SecurityEventMonitor` (an `IAuditSink`), attached by `--incident-log` / `Audit:IncidentLogPath` | Achieved | `SecurityEventMonitorTests`, `AuditWiringTests` |
| D1 | Deterministic estimate and replay for forensics | `WlsStateEstimator`, `GdaReplayLoop`; no clock in estimation code | Achieved | `EstimatorDeterminismTests`, `GdaReplayDeterminismTests`, `EstimationClockIndependenceTests`, `Doc16NumericsDeterminismTests` |
| D1 | Live-data quarantine | `QuarantineLiveStore` (refuses the evidence lake) | Achieved | `QuarantineLiveStoreTests` |
| D1 | Incident hooks | `Diagnostics.Incident.IncidentReport` / `IIncidentSink` / `IncidentRouter` / `IncidentPolicy`; `SecurityIncidentSink`, `JsonLinesIncidentSink` and `IncidentWiring` attached by `--incident-log` / `Audit:IncidentLogPath`; [`incident-response.md`](incident-response.md) | Achieved | `IncidentRouterTests`, `AuditWiringTests` |
| D1 | Distrusted-source containment | `Estimation.DistrustedSources` (primitive only: no product path applies it; distrusted rows still count as chi-square redundancy) | Partially achieved | `DistrustedSourceTests` |
| A1 / A2 / D1 / D2 | Governance, risk and incident documents | [`security-governance.md`](security-governance.md), [`risk-register.md`](risk-register.md), [`incident-response.md`](incident-response.md) | Achieved (org-level governance remains deploying-party) | the documents, cross-referenced from sections 4.1, 4.2, 7.1 |
| Out of scope / deploying-party | Host OS, perimeter, physical security, org-level A1 governance, B6 awareness | the deploying party's environment and organisation | Out of scope / deploying-party | the deploying party's own CAF assessment |

---

## 9. Gap register

Three in-component gaps remain open; each is recorded Partially achieved in `ComplianceManifest`, so the compliance
attestation reads NOT COMPLIANT until it is closed (section 4.5). The other entries are kept as a record of what closed
them.

1. **RBAC (B2) - open (Partially achieved).** The access-control gate `OperationRole`, `IdentityContext`,
   `RoleGuard.Require(...)`, `AccessDeniedException` (`GridSim.Core.Security`) is built and tested, never inside the
   solver loop, and its audited overload writes a `SecurityEventKind.AccessDenied` entry for each deny. It is a
   primitive only: no endpoint, CLI verb or hosted service calls it, so nothing is authorised by role and no denial is
   ever audited. The replay-only gate remains the production exposure control. Independent of `ForwardInferenceGuard`;
   no role reaches sub-169h data. To close: enforce it, with the host's audit writer, on the solve and
   resident-estimator control surfaces. Evidence: `RoleGuardTests`.
2. **Per-event audit and signed anchor (C1) - closed.** `AuditRunRecorder.RecordSecurityEvent` writes one entry per
   security event at the detection sites listed in section 6.1, forwarded by `--audit-forward` / `Audit:ForwardPath`;
   `SignedAuditAnchor` is wired through `--audit-sign-key` / `--audit-verify-key`, the matching web settings and
   `audit verify --pubkey`, so a joint rewrite of log and anchor is detectable by anyone who lacks the private key
   (fully so only when the private key is off the host). `AccessDenied`, `DistrustedSource` and `DependencyAlert` have
   no product detection site yet (items 1 and 9). Evidence: `SecurityAuditTests`, `SignedAuditAnchorTests`,
   `AuditWiringTests`.
3. **Supply chain (A3/A4) - closed in-component.** `packages.lock.json` lock files pin the restore graph;
   `BuildProvenance` exposes version/commit/build SDK/SBOM-hash/build-hash (an identity label); the release injects
   the SBOM hash into every shipped build, checks it on each CLI binary, and attaches a signed build-provenance
   attestation to every Release file. Running the workflow on a GitHub tag is a deploying-maintainer action, and the
   CI-at-release cadence is a risk-accepted deviation (section 4.4). Actions are referenced by version tag, not
   pinned by SHA. Evidence: `SupplyChainManifestTests`, `WorkflowReleaseGateTests`.
4. **Service resilience (B5) - open (Partially achieved); resource limits (B4) - closed in-component.**
   `ResilienceOptions` (bridge message cap, in-flight-query cap; `ws://` refused unless allowed) is
   applied to every bridge client, and an SLO breach at the end of a `serve` / resident run is an
   `AvailabilityDegraded` security event. The open part: no host drives the bounded reconnect backoff schedule, so
   there is no supervised auto-reconnect. Edge rate limiting is deploying-party. Evidence: `ResilienceOptionsTests`,
   `BridgeEndpointSettingTests`, `HealthProbeTests`, `AuditWiringTests`.
5. **Secrets management (B3) - closed.** `ISecretStore` (`EnvironmentSecretStore`, `DirectorySecretStore`,
   `CompositeSecretStore`, `NullSecretStore`), BCL-only; the key-ring certificate password resolved through it, from a
   secrets directory first when `Secrets:Directory` is set; no secret in any audit summary. No OS-keychain or vault
   backend ships. Evidence: `SecretStoreTests`, `DirectorySecretStoreTests`, `NoSecretInAuditTests`.
6. **Security-event aggregation / threat hunting (C2) - closed.** `SecurityEventMonitor` (an `IAuditSink`) tallies
   security-event kinds and escalates a burst to one latched incident; attached by `--incident-log` /
   `Audit:IncidentLogPath`. Evidence: `SecurityEventMonitorTests`, `AuditWiringTests`.
7. **Governance, risk and incident documents (A1/A2/D1/D2) - closed in-component.**
   [`security-governance.md`](security-governance.md), [`risk-register.md`](risk-register.md) and
   [`incident-response.md`](incident-response.md) exist and are cross-referenced; the incident hooks
   (`IncidentRouter`, `IIncidentSink`) are built and wired through `--incident-log` / `Audit:IncidentLogPath`.
   Evidence: `IncidentRouterTests`, `AuditWiringTests`.
8. **Feasible-landing FDIA (C1) - closed.** `FdiaScreen.Screen` takes `protectedAnchors` and flags a feasible,
   in-limit cloaked attack that disturbs a PMU-anchored quantity (`FdiaScreenReport.AnchorInconsistent`). Evidence:
   `FdiaProtectedAnchorTests`.
9. **Distrusted-source containment (D1) - open (Partially achieved).** `DistrustedSources` marks a source distrusted
   and forces its rows to pseudo-measurement treatment, but no product path applies it, and distrusted rows still
   count towards the chi-square redundancy. To close: a CLI / host setting that applies it, with a
   `DistrustedSource` security event, and redundancy that excludes the distrusted rows. Evidence:
   `DistrustedSourceTests`.

**Remaining deploying-party actions (not code gaps):** run the first tagged release so the SBOM, provenance and
signing steps execute on GitHub, and run CodeQL by hand before it; provide a PKCS#12 certificate on Linux so the key
ring is encrypted at rest; point `--audit-forward` / `Audit:ForwardPath` at a WORM mount or SIEM collector; keep the
anchor-signing private key off the host where the threat model needs it; set `read_only`, `cap_drop` and `no-new-privileges` on the compose file; apply edge rate
limiting at the reverse proxy; exercise the incident-response plan as a tabletop.

**Numerics-roadmap note (explicitly not a compliance gap).** On the CPU every WLS and robust estimate is solved by one
canonical step (`CanonicalWlsStep`, the sparse arithmetic) at every size, so the estimate is bit-identical across the
old 200-bus dense / sparse threshold (`CanonicalWlsParityTests`, section 10.4). The dense step that a GPU linear solver
uses still agrees with the sparse step to solver tolerance rather than to the bit; GPU arithmetic is outside the
bit-identity contract. Each backend is deterministic run to run and across the SIMD/CPU tiers (the forensic property
that matters), and the estimator is cross-validated to pandapower at 5.6e-12 pu. It bears on no CAF or NIS outcome and
is recorded here only so it is not mistaken for one.

---

## 10. Validation and test inventory

The assurance controls are held to the same evidence bar as the numerics. An assurance claim without a test is
"Partially achieved" at best. This section lists the tests that evidence each claim at this release, and names the
ones still planned.

### 10.1 Restriction-invariant tests (the spine)

- `ForwardInferenceGuardTests`, `GdaReplayLoopTests` and `RestrictionInvariantTests`: dated seams refuse a stamp
  inside the horizon and accept one outside it; a future-dated series is rejected at ingestion; an unregistered
  dynamics-fused method is refused by `RestrictionReview.AssertPermitted`, with `RestrictionReview.Reviewed` as the
  register.

### 10.2 Security-control unit tests

- `AuditLogTests` and `AuditLogDeletionTests` (in memory): a clean log verifies and chains from genesis; an edited
  entry, a reordering, and a deleted first or middle entry are detected; deleting entries from the end leaves a
  consistent chain, which is the documented limit the anchor closes.
- `AuditLogFileTests` (persistence): file round trip equals the in-memory chain hash for hash; each tamper kind (edit,
  edit with recomputed hash, middle and first deletion, reorder, blank line, truncation through a line, hand
  reformatting, non-UTC timestamp, CR LF, byte-order mark, extra field) is reported at the right entry index; an empty
  log verifies; a torn last line (crash mid-write) is refused on reopen; tail truncation and whole-log rewrite pass the
  chain alone but are caught by the anchor; two threads appending to one writer keep a gap-free chain; a second writer
  fails loud. `Doc3AuditAnchorTests` pins the anchor file and the `serve` and web host trails;
  `AuditRunRecorderTests` and `EstimationAuditTrailTests` pin what a run records.
- `FdiaProtectedAnchorTests`, `FdiaCloakedAttackTests` and `FdiaScreenTests`: as listed in 6.3.
- `WebSecurityDoc2Tests`, `WebHostDoc2Tests`: replay-only gate, SSO tickets, 401 / 403 answers, key-ring protection
  modes, session cap.
- `BridgeClientRobustnessTests`, `ResilienceOptionsTests`, `BridgeEndpointSettingTests`, `HealthProbeTests`: query
  timeout and fault propagation, the resource limits and backoff schedule, the `ws://` opt-in setting, and the SLO
  verdict. `AuditWiringTests`: a security event is its own forwarded entry, a significant event raises one incident and
  a burst is escalated once, and the signed anchor is written, checked and refused when rewritten or missing.
- `WorkflowReleaseGateTests`: the release calls the CI matrix and every publishing job waits for it, the matrix covers
  macOS, forced AVX-512 and FMA off with no advisory leg, the SBOM hash is injected and checked, Release files get a
  provenance attestation, signing trusts only the pinned certificate, and the build SDK is stamped.
- `RoleGuardTests` (role x operation matrix; deny is audited; no role reaches sub-horizon data), `SecurityAuditTests`
  (one entry per security event with its reason), `SignedAuditAnchorTests` (joint rewrite detected),
  `SecurityEventMonitorTests` (burst escalation), `IncidentRouterTests` (one incident per detection, no future-dated
  claim), `DistrustedSourceTests`, `SecretStoreTests`, `NoSecretInAuditTests`, `SupplyChainManifestTests` and
  `ComplianceAttestationTests`: the controls built this release.

### 10.3 Determinism and replay (forensic-integrity requirement)

Determinism is a D1 forensic control, so it is tested, not assumed:

- **Bit-reproducible estimate.** `EstimatorDeterminismTests`: two runs of the same estimate give bit-identical
  `Vm`, `VaDeg`, `ObjectiveJ`, iteration count, chi-square verdict and every residual, on the dense and on the sparse
  backend, on case14 and case118. `Doc16NumericsDeterminismTests` pins J, Vm and Va bit-identical across the SIMD
  tiers; `RealtimeEstimatorEngineTests` pins a deterministic stream.
- **Bit-reproducible replay.** `GdaReplayDeterminismTests`: replaying the same 24-step window twice, warm starts
  included, gives bit-identical states, losses and mismatches on the dense and on the sparse power-flow engine. This
  is the property recovery relies on (5.5, 7.1).
- **No wall clock in estimation.** `EstimationClockIndependenceTests` reads the compiled `GridSim.Core` and fails if
  any method in a `GridSim.Core.Estimation` namespace calls a clock source (`DateTime.Now` / `UtcNow` / `Today`,
  `DateTimeOffset.Now` / `UtcNow`, `Environment.TickCount`, `Stopwatch`, `TimeProvider`); a positive control confirms
  the scan does see the guard's own clock read. The clock appears only in the guard's horizon check and in audit-log
  run metadata.

### 10.4 Correctness of the estimate against independent references

The estimator's correctness underwrites every downstream security claim (a wrong state makes `FdiaScreen` and the
physics audit meaningless), so it is cross-checked:

- **Self-consistency (recovery).** A measurement set generated from a converged power flow, with zero noise and full
  redundancy, is recovered to better than 1e-10 pu in Vm with J below 1e-16 (`WlsEstimatorTests`, and the sparse
  equivalent in `SparseEstimatorTests`).
- **Injection agreement.** The estimator's measurement model and the power flow's injection routine agree to the bit
  (`SparseEstimatorTests`: neighbour injection evaluation is bit-identical to the solver path).
- **Physics audit of the estimate.** The projected power flow of an estimate is audited against the same laws the
  solvers satisfy (`FeasibleKclGateTests`, `WlsEstimatorTests`).
- **Independent external reference.** `tools/crossval/crossval_estimation.py` re-solves the estimation problem in
  pandapower and compares every bus; the measured agreement is reported in technical ch. 11 (5.6e-12 pu), giving an
  oracle outside GridSim's own code.
- **Backend equivalence.** On the CPU every WLS and robust estimate takes `CanonicalWlsStep` (the `SparseWlsStep`
  arithmetic) at every size; `SparseBusThreshold` (200) now selects only the observability report, so a case just
  below and just above it gives bit-identical estimates (`CanonicalWlsParityTests`), the machine-epsilon standard the
  power flow meets (`DenseSparseParityTests`). The dense step (`DenseWlsStep`) solves an estimate only with a GPU
  linear solver, and agrees with the sparse step to solver tolerance (`SparseEstimatorTests`: Vm to 1e-8 pu, Va to
  1e-7, J to a relative 1e-6 on a noisy set); this is recorded in section 9 as a numerics note, not a compliance gap.

### 10.5 The 0-warning build gate

`Directory.Build.props` sets `TreatWarningsAsErrors=true`, `EnableNETAnalyzers=true`, `AnalysisLevel=latest`,
`AnalysisMode=Recommended`, `Nullable=enable`. The CAF-relevant consequences:

- A nullable-reference or analyser warning *is a build failure*, so an entire class of null-deref and disposal defects
  cannot ship - a B4 system-security control enforced mechanically.
- Every assurance type (RBAC, `IAuditSink`, `ISecretStore`, `ComplianceAttestation`) is held to the same bar: it must
  compile clean under the analyser set or it does not merge. The gate extends to every control in section 9; security
  code gets no warnings exemption.
- A status in this file is upgraded only when the build is warning-free and the tests that evidence it are green.

---

## 11. How this honours the restriction invariants

Every control in this mapping is designed so that it can only ever *strengthen* the forward-inference
invariant, never provide a lever to weaken it:

- **Access control is independent of the guard.** No role - not even `Administrator` - can request sub-169h data,
  because the guard consults no role and has no bypass. Access control narrows who may run the filter; it never
  widens what the filter may see.
- **The audit trail is structurally present/past.** `AuditLogEntry` separates `RunTimestampUtc`
  (metadata) from `StateTimeUtc` (a grid instant that is definitionally the filtered present/past), so
  the evidence trail can never itself become a future-dated grid claim.
- **FDIA detection and the physics audit are pure evaluation.** `FdiaScreen` and the Ring C gate call
  `ToPowerFlowResult` / `PhysicsLaws.Audit`, which *evaluate* `h(x^)` at the already-estimated state -
  no solve, no forward step, so they cannot manufacture a forward claim (the code comment on the Ring C
  gate makes this explicit: "pure evaluation, no solver call, so no-forward-inference holds").
- **Determinism and replay operate on guard-permitted or replay data only.** The forensic recovery story
  re-runs history; it never re-runs the present. A recovered state is a re-derived past state.
- **The Dynamics->Estimation seam stays gated.** Any future dynamic state estimation control that a security
  requirement tempts toward (a predictive filter "for resilience") is still bound by
  `RestrictionReview.AssertPermitted`: it must emit a filtered-present estimate and be reviewed, or it
  fails loud. No assurance requirement overrides invariant #5.

The mapping's guiding rule: a security control may *reduce* what GridSim does, never *extend its
reach forward*. If a proposed control needs fresher-than-permitted data to function, the control is
wrong, not the horizon.

---

## 12. Cross-framework mapping

The NCSC Cyber Assessment Framework v4.0 is the primary obligation for a critical-infrastructure-adjacent component in
the GB electricity subsector. The companion documents are supporting cross-walks that show the same controls from the
viewpoint of other frameworks; they are not additional certifications being pursued. NIST CSF 2.0 is the shared
vocabulary the cross-walks below are read in (no separate CSF document is kept), and IEC 62443-4-2 is the
secure-product benchmark (no separate 62443 document is kept).

- [`ssdf-mapping.md`](ssdf-mapping.md) - NIST SP 800-218 (the Secure Software Development Framework): the
  secure-development practices that evidence A4.b (section 4.5).
- [`slsa-provenance.md`](slsa-provenance.md) - the SLSA Build track: how the build hash, provenance and attestation
  (A4, A4.b) sit against the SLSA levels.
- [`nasa-std-7009-credibility.md`](nasa-std-7009-credibility.md) - NASA-STD-7009 model-and-simulation credibility: the
  credibility of the estimator as a model, underwriting the correctness claims in section 10.4.
- [`cyber-essentials-note.md`](cyber-essentials-note.md) - the Cyber Essentials control themes and where the component
  meets them.
- [`tier3-roadmap.md`](tier3-roadmap.md) - candidate further frameworks (ISO 27001, CIM-CGMES, IEC 62351, FAIR),
  recorded as a roadmap rather than committed obligations.

## 13. Maintenance and change control of this mapping

- This file is reviewed whenever a control in section 8 changes status, and at every release; the version in the
  header is the release it describes.
- A control may be marked "Achieved" only when the test or artefact that evidences it is green and named here.
- Downgrades are as important as upgrades: if a dependency introduces a CVE (A4) or a persistence bug
  breaks the audit trail's guarantees (C1), the affected row returns to "Partially achieved" with a
  gap entry, honestly, the same way the estimator reports `Unobservable` rather than a plausible lie.
- Changes to `ForwardInferenceGuard.HorizonHours`, `RestrictionReview.Reviewed`, or the ingestion-seam
  list are security-critical and follow the `UnderstandingRestrictions.md` review discipline in
  addition to updating this file.
