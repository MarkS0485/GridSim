# NCSC Cyber Assessment Framework (CAF) mapping

**Assurance statement. Status as at GridSim 2.1.010 (reviewed 2026-11-01, first against 2.1.005 and again after the
2.1.008 to 2.1.010 fixes; release candidate RC-1 was released on 2026-10-31 as 2.1.000).** This document maps the
GridSim state estimator and the services that host it to the four objectives of the NCSC Cyber Assessment Framework v4.0
(released 6 August 2025), principle by principle. v4.0 carries 41 contributing outcomes (39 in v3.x) across the same
four objectives and fourteen principles; the new outcomes relevant to a software component are A4.b Secure software
development and support, and the expanded monitoring and threat-hunting expectations under C1/C2 and the tested,
supplier-aware response expectations under D1. Each outcome names the controls that exist in the code at this release,
the test or build artefact that evidences them, and the gaps that remain.

No outcome is marked "Achieved" unless a control is built and a named test or artefact evidences it. Any control
still owned by the deploying party is named as such with its design, so the boundary is concrete.

The headline position at this release: most GridSim-owned controls are Achieved with a named test; three are Partially
achieved: B2 role-based access control, which the web host enforces on its REST and WebSocket solve surface since
2026-11-01 but not on the dashboard's own sessions; the D1 distrusted-source control, wired into the estimator
paths since 2026-11-01 and held at Partially achieved until the release test run confirms it; and the B5 supervised
reconnect of a lost bridge stream, which the web host and the desktop app drive since 2026-11-01 but only when it is
switched on (off by default) and which a release test run has not yet confirmed (section 9). The compliance attestation (A4.b, section 4.5)
reports this honestly: it reads COMPLIANT only for a release build whose every in-scope control is recorded Achieved,
so at this release even a release build reads NOT COMPLIANT until every Partially achieved control is closed.

Three more outcomes are recorded Partially achieved at this release because the published release did not go through
the release controls this document describes: the software-inventory half of A3, A4 supply chain and A4.b secure
software development (sections 4.3 to 4.5, gap register items 10 to 13). RC-1 was built, signed and published from
the owner's build machine. At RC-1 the tag-driven release workflow (`release.yml`) refused the RC tag form, so it
stopped in its first step on both RC tags; no published file carries the signed build-provenance attestation, none was
built behind the CI matrix gate, and the "release build" flag a binary reports was set by a local build, not by the
workflow. Since 2.1.008 the workflow accepts a release-candidate tag, but no release has yet been made through it.
Since 2.1.008 the code inventory (`ComplianceManifest`) agrees with this document: it records `supply-chain` and
`secure-sdlc-attestation` as Partially achieved, naming that gap, so five in-scope controls are recorded Partially
achieved (`rbac`, `distrusted-source`, `resilience`, `supply-chain`, `secure-sdlc-attestation`) and the count recorded
Achieved is two lower than at 2.1.005. No overstated verdict reached a user: an RC-1 command line binary (2.1.000,
built before that change) reports `release build  yes` and the verdict `NOT COMPLIANT`, listing the three controls
that were open then.

Full CAF compliance *as* an Operator of Essential Services is not achievable by a component (A1 governance at org level, B1 policy, B6
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
(`gridsim interpose`), and the web host (`GridSim.Web`, including its resident estimator). The release artefacts that
carry them are assessed as well. RC-1 (2.1.000) shipped the command line tool for Windows x64 and Linux x64, the
Windows WPF desktop app (`GridSim.Wpf`), the cross-platform Avalonia desktop app (`GridSim.App.Desktop`, Windows x64
and Linux x64), the `GridSim.Core` NuGet package, one CycloneDX SBOM and `SHA256SUMS.txt` (`RC1.md`). No web host
image or web host build was published at RC-1, so a deployed web host is built by its deployer. The desktop apps are
presentation hosts: they hold no estimator of their own, apply `ResilienceOptions` to every bridge client they create
(section 5.4) and offer the AUDIT LOG viewer. It does **not** assess an
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
table for the deployed services; it does not cover the Avalonia desktop app and
its case-folder inputs such as `geography.json`, or the hand-built release path (gap register item 18).

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

**Status: Achieved for data assets; Partially achieved for the software inventory of the published release (the
published SBOM covers the command line tool only; the workflow now builds five and stamps each into its artefact, but
no release has been made through it; gap register item 11).**

Data-asset provenance is a first-class, enforced concept: every emitted series carries a `ProvenanceTag` (`Recorded`,
`DerivedFromRecorded`, `CuratedPlaceholder`, `Synthetic`, `Counterfactual`, `Interpolated`, `UnvalidatedAtScale`), and
`ProvenanceTrust` refuses an output tag (`Counterfactual`, `UnvalidatedAtScale`) offered as an input, at the replay entry
and at the case editor's save gate, and refuses a computed tag raised above its inputs, so a tag only moves down through
a computation. No code can check that a `Recorded` tag in a hand-written file is backed by a record, and the
forward-inference guard checks time stamps only and never reads a tag; a correct `Recorded` tag on an authored input
is a rule for authors and reviewers. Knowing the trust class of every datum is the CAF A3 outcome for information
assets. `AssetProvenanceTests` pins the tags on asset data.

Software-asset inventory: the release workflow is written to build five CycloneDX software bills of materials
(SBOMs), all Release files: the CLI, the web host, GridSim.Core (the NuGet package's own), the Avalonia desktop app and
the WPF desktop app. Since 2026-11-02 (GR-439) the Release also carries an SBOM of the built web image (Syft: the Ubuntu base's packages and the ASP.NET runtime, checked for both), and the CLI and desktop SBOMs list the .NET runtime they bundle (the WPF app's also the Windows Desktop runtime) as framework components, added before the SBOMs are hashed. Each shipped artefact is built with the SHA-256 of its own SBOM injected (`-p:GridSimSbomSha256`):
the CLI, each desktop app (job `desktop`, which checks that the single-file bundle carries the hash) and the web image
with theirs, and the NuGet package with the GridSim.Core SBOM's hash (`sbom-sha256: core_sha256` in the `nuget` job;
`WorkflowReleaseGateTests` and `SlsaSupplyChainPinTests` pin that wiring). The software-inventory gap (gap register
item 11) is therefore closed in the workflow but not yet exercised by a release: the workflow did not produce RC-1
(section 4.4) and no release has been made through it. The RC-1 Release carries one CycloneDX SBOM,
`gridsim-2.1.000.sbom.json`, for the command line tool, made on the owner's build machine; its SHA-256 is stamped into
the CLI (`gridsim compliance verify` reports it). The two desktop apps and the NuGet package were published with no
SBOM of their own, so the Avalonia app's packages (Avalonia, SkiaSharp and HarfBuzzSharp with their native libraries,
CommunityToolkit.Mvvm) are inventoried, for the published RC-1, only in `THIRD-PARTY-NOTICES.md` and the lock files,
not in a published SBOM.
`THIRD-PARTY-NOTICES.md` names each shipped package with its licence, including the Avalonia app's packages and its
bundled fonts (Inter, Barlow, Cascadia Mono), and `ThirdPartyNoticesTests` checks it against every package in the
lock files. The restore graph is recorded: per-project `packages.lock.json` lock files
(`RestorePackagesWithLockFile=true` in `Directory.Build.props`) record the resolved dependency set. Since 2.1.008 the
.NET SDK is pinned by `global.json` to 10.0.401 (the SDK RC-1 was built with) with `rollForward` `disable`, so a
machine without exactly that SDK refuses to build rather than building with another; every workflow installs the SDK
from that file (`global-json-file: global.json`), and the container image builds on the `sdk:10.0.401` image. Restores
on GitHub Actions run in locked mode (`RestoreLockedMode` is true in `Directory.Build.props` when `GITHUB_ACTIONS` is
true) and the container image restores with `--locked-mode`, so there a restore that would change a lock file fails
instead of rewriting it. A restore on the owner's machine is not in locked mode; the SDK pin removes the cause of drift
seen before 2.1.008 (a newer SDK rewriting the implicit SDK package entries), and the command line tool's lock file
now covers its three release runtimes, so a release publish no longer rewrites it. In the workflow the SBOM job
restores on its own rather than reusing the publishing job's restore; with locked mode both resolve exactly the
committed lock files, so the SBOM describes the graph the shipped build used. Evidence for the pin and locked mode:
`BugHunt1101WebTests` (`B161_*`). `BuildProvenance`
(`GridSim.Core.Diagnostics.SupplyChain`) exposes the version, commit, toolchain (the .NET SDK version stamped at build
time), SBOM hash and deterministic build hash at runtime, so an emitted estimate or audit entry can be correlated with
the build that produced it. The build hash is an identity label over those metadata strings, not an integrity proof:
no byte of the shipped files is hashed into it (section 4.5). Evidence: `SupplyChainManifestTests`,
`ThirdPartyNoticesTests`; the published `gridsim-2.1.000.sbom.json` (SHA-256 `fcf0cb79...6ce32`, listed in `RC1.md`).

### 4.4 A4 Supply chain

**Status: Partially achieved (low intrinsic exposure; the lock files, notices and signing are in place, but the
published RC-1 release did not pass through the release workflow's gates; gap register item 10).**

GridSim.Core is deliberately close to the base class library - the measurement model, solvers and guard use
`System.Numerics`, `System.Security.Cryptography`, `System.Text.Json` and little else, so the third-party attack
surface is small by construction. The Avalonia desktop app adds the largest third-party surface the product ships
(Avalonia 11.3.22, SkiaSharp 2.88.9 and HarfBuzzSharp 8.3.1.1 with their native libraries, CommunityToolkit.Mvvm
8.4.2), admitted with exact versions in committed lock files. The controls that keep the surface small and known:

- the SBOM (A3) and `THIRD-PARTY-NOTICES.md`;
- per-project `packages.lock.json` lock files (`RestorePackagesWithLockFile=true` in `Directory.Build.props`) that
  record the restore graph, enforced by a locked-mode restore on GitHub Actions and in the container image build, with
  the SDK pinned by `global.json` (since 2.1.008; a local restore is not locked, section 4.3), and `BuildProvenance`
  (`GridSim.Core.Diagnostics.SupplyChain`) exposing version, commit, toolchain, SBOM hash and build hash at runtime;
- NuGet audit warnings fail the build (through `TreatWarningsAsErrors`), on every build including the owner's local
  builds;
- CodeQL analysis of the C# with the `security-extended` query suite (`codeql.yml`, started by hand only; the runs on
  2026-10-31 passed) and Dependabot over NuGet, Docker and GitHub Actions (monthly, merged by hand; Dependabot security
  updates are enabled on the public repository). Every GitHub Action in every workflow is pinned to a full commit
  SHA, with the release tag it was taken from in a comment, and the CycloneDX SBOM tool is installed at a named
  version (since 2026-11-01);
- build telemetry is off on every build: `Directory.Build.targets` replaces the Avalonia build-services statistics
  target with an empty one and every building workflow sets `AVALONIA_TELEMETRY_OPTOUT=1`
  (`AvaloniaTelemetryOptOutTests`), so no build reports to a third party;
- release signing with one self-signed certificate (`signing/gridsim-signing.cer`, thumbprint
  `86DD43B731D2B8A5133972F7B7C91C0FDA8EB721`). At RC-1 the three Windows executables (`gridsim.exe`,
  `GridSim.Wpf.exe`, `GridSim.App.Desktop.exe`) carry an Authenticode signature (SHA-256, RFC 3161 timestamp) and the
  NuGet package an author signature, all made on the owner's build machine (`RC1.md`). The Linux binaries,
  `SHA256SUMS.txt` and the SBOM are not signed. A consumer checks the signer thumbprint; Windows reports the signature
  as not trusted because the certificate is self-signed. The self-signed certificate is an accepted residual (owner
  decision, 2026-11-01): a CA-issued code-signing certificate is not planned, and the published certificate and its
  pinned thumbprint are the trust anchor (`slsa-provenance.md` section 5, item 5).

What the release workflow adds, as written. `release.yml` generates the five SBOMs first, builds the CLI, the web
image, the two desktop apps and the NuGet package, each with the SHA-256 of its own SBOM injected
(`-p:GridSimSbomSha256=<hash>`, the `SBOM_SHA256` build argument for the image), requires each CLI binary to report
`release build  yes` with exactly that hash and each desktop bundle to carry its hash, runs a vulnerable-package gate
(`dotnet list package --vulnerable --include-transitive`), puts a signed SLSA build-provenance attestation
(`actions/attest-build-provenance`, Sigstore) over every Release file and over the pushed image (and, in
`publish.yml`, over the NuGet package after signing and before the push), and signs the Windows CLI, the Windows WPF
and Avalonia executables and the NuGet package with the pinned certificate, trusting exactly that certificate in its
verify steps. Since 2026-11-01 (GA-5, an owner decision) its ship scope covers the desktop apps as RC-1 shipped them:
job `desktop` builds the WPF app (`GridSim.Wpf`) for win-x64 and the Avalonia app (`GridSim.App.Desktop`) for win-x64
and linux-x64, each a self-contained single file, archived as `gridsim-wpf-<version>-win-x64.zip`,
`gridsim-app-<version>-win-x64.zip` and `gridsim-app-<version>-linux-x64.tar.gz` and put on the Release page, where
every file is attested. There is no macOS desktop build and no smoke run of the desktop apps (they need a display).
Job `reproducible` rebuilds the linux-x64 CLI on a fresh runner and compares it byte for byte with the shipped archive;
the Release job waits for it (`needs: [ verify, ci, cli, web-image, sbom, reproducible, desktop ]`), so a
non-reproducible Linux CLI stops the release. Since 2.1.008 it accepts a release-candidate tag (`vMAJOR.MINOR.PATCH-<label>`, for example `v2.1.000-RC-1`):
the numeric part must still equal the source `<Version>`, the GitHub Release is marked a pre-release, and no NuGet
package or container image is pushed for a candidate; the candidate's Release files still pass every gate and get the
attestation. The web-image job passes the source-tree state into the image build (`SOURCE_TREE`), so the image can
attest a release build only from a clean tree (section 4.5). These steps are pinned by `WorkflowReleaseGateTests`,
`WorkflowContractTests` and `BugHunt1101WebTests` (`B195_*`, `B176_*`).

What happened at RC-1. None of those workflow steps ran for a published file. At RC-1 the workflow's first step
accepted only a `vMAJOR.MINOR.PATCH` tag, so the pushes of `v2.0.195-rc1.0` (2026-10-30) and `v2.1.000-RC-1`
(2026-10-31) stopped it within seconds, and the package workflow failed with it (the tag form is accepted since
2.1.008). Its ship scope now covers the desktop apps as RC-1 shipped them (2026-11-01) but still differs from RC-1 in
the CLI: it builds an osx-arm64 CLI as well and names the CLI archives `gridsim-<version>-<rid>`, where RC-1 used
`gridsim-cli-<version>-<rid>` and had no macOS CLI. The Release was assembled by hand from the owner's build: no file carries a
provenance attestation (`gh attestation verify` has nothing to check), no web image or web SBOM was published, the
NuGet package is attached to the Release rather than pushed to the package feed, and `SHA256SUMS.txt` comes from the
same hand-made upload as the files it lists. The signing secrets the workflow reads are not set on the repository, so
the workflow would publish unsigned, with a warning, if it ran; the RC-1 signatures exist because the owner signed on
the build machine.

Evidence: `SupplyChainManifestTests` (lock-file pin and build provenance), `WorkflowReleaseGateTests`,
`WorkflowContractTests`, `ThirdPartyNoticesTests`, `AvaloniaTelemetryOptOutTests`, `BugHunt1101WebTests` (`B161_*`
SDK pin and locked restore, `B195_*` candidate tags); `RC1.md` (the RC-1 file list, SHA-256 values and signing
statement).

**Continuous integration cadence (a deliberate, risk-accepted deviation).** CI runs when the release workflow calls it
on a release tag, or by hand, and since 2026-11-01 (GR-382) on every push to main and weekly on the public repository,
where Actions minutes are free (its jobs are guarded to that repository, so pushes to the private development
repository start nothing); CodeQL runs by hand only. Development commits are therefore checked by CI only when they
reach the public repository. CAF is outcome-based, and the
outcome CAF asks for is every shipped change built and checked deterministically. The compensating control the owner
has chosen is twofold: every build the owner makes for use is a production build carrying the compliance attestation,
checked by the local test suite, and CI is run by hand as the witness of a release. As designed, the witness is also a
gate: `release.yml` calls `ci.yml` (job `ci`) and every publishing job needs it. The matrix covers Linux and Windows
in the normal, AVX2-off (which also holds the non-FMA axis: on .NET 10 it turns FMA off, and its preflight requires
`Fma=False`), hardware-intrinsics-off and forced 256-bit modes; a forced AVX-512 leg on each, reported NOT CHECKED
and skipped when the runner's CPU has no AVX-512; and macOS on Apple silicon and Linux and Windows on ARM64 in the
normal and hardware-intrinsics-off modes. No leg is advisory. At RC-1 the witness did not hold: RC-1 was published at
13:00 UTC on 2026-10-31 after the local suite (GridSim.Tests 10,291 passed and 31 skipped by the RAM gate on an 8 GB
machine, GridSim.App.Tests 310 passed; `RC1.md`), and the three CI runs started by hand later that day all failed. The
last of them (on 2.1.002) failed one desktop-app layout test on the macOS hardware-intrinsics-off leg, every other leg
passing; no CI run on `main` has passed since the history reset at RC 1.0. That test now waits for the rows to be shown
before it measures them (a harness timing change, no assertion changed), and passes locally, but no CI run has been
made since, so the witness is still not shown green. The deviation is recorded and risk-accepted
in [`security-governance.md`](security-governance.md); the failing witness is gap register item 12.

### 4.5 A4.b Secure software development and support (new outcome in v4.0)

**Status: Partially achieved. The attestation mechanism is built and reports honestly (its verdict reads NOT COMPLIANT
at this release), but the integrity half of the indicator rests on workflow steps that did not run for RC-1, and the
"release build" flag, now checked for form and a clean tree, can still be set by a local build (gap register items 10,
12 and 13).**

v4.0 adds A4.b, which asks a supplier to run an established secure-development framework, to know the composition and
origin of its software and monitor its components for vulnerabilities, to secure its development, test and production
environments and its source and repositories, to develop against a threat model, and - the load-bearing new indicator
- to **attest to the authenticity and integrity of software, including updates and patches**. Against these:

- **Established secure-development framework.** The development process maps to NIST SSDF (SP 800-218) and the
  Microsoft SDL; the mapping is in [`ssdf-mapping.md`](ssdf-mapping.md).
- **Known composition and origin, monitored for vulnerabilities.** The lock files, `THIRD-PARTY-NOTICES.md`, NuGet
  audit as a build error, CodeQL and Dependabot (A3/A4). The published RC-1 SBOM covers the command line tool only; the
workflow is written to publish five (section 4.3), not yet exercised by a release.
- **Secured dev/test/prod and source/repos; threat-informed development.** The zero-warning analyser gate
  (section 10.5), the restriction review discipline (section 3), and the threat model in technical ch. 35 and
  [`risk-register.md`](risk-register.md). Every commit on `main` is signed with the owner's key; release tags are
  lightweight and unsigned. Development pushes go to a private repository (`GridSim-dev`); the public `GridSim`
  repository is where CI and releases run. Since 2026-11-01 the public repository has a ruleset on `main` that blocks
  deletion and force pushes (repository administrators may bypass it; pull requests are not required, since `main`
  is pushed to directly), secret scanning with push protection, Dependabot alerts and private vulnerability
  reporting, applied and read back by `tools/github/apply-public-settings.ps1`. RC-1 was
  built on the owner's own machine, so the development environment and the release build environment are one machine
  (gap register item 13).
- **Authenticity and integrity attestation.** Two separate things meet this indicator, and they must not be confused.
  - *Integrity and origin of a download* come from outside the binary. For RC-1 they are the Authenticode signatures
    on the three Windows executables and the author signature on the NuGet package (self-signed certificate, checked
    by thumbprint), and `SHA256SUMS.txt` with the SHA-256 values in `RC1.md`. The Linux archives and binaries are
    covered only by those checksums, which are published beside them by the same hand. The signed build-provenance
    attestation the workflow is written to produce (`gh attestation verify <file> -R <owner>/GridSim`) does not exist
    for any RC-1 file.
  - *What a running binary states about itself* is the compliance attestation. `ComplianceManifest`
    (`GridSim.Core.Diagnostics.SupplyChain`) is the machine-readable control inventory, each control bound to its
    evidencing test and given a status that is maintained by hand in source (an inventory, not a measurement).
    `ComplianceAttestation` binds the manifest hash to the build identity (version, commit, SDK, SBOM hash and the
    build hash over them). The build hash is an identity label, not an integrity proof: it covers no artefact bytes,
    so a modified binary that keeps the same metadata reports the same hash. `IsCompliant` needs BOTH a release build
    AND every in-scope control recorded Achieved. Since 2.1.008 a build is a release build only when all three hold
    (`BuildProvenance.ReleaseRefusal`): the injected SBOM hash (`-p:GridSimSbomSha256`) is exactly 64 hexadecimal
    digits, the commit is known, and the source tree was clean. The build stamps the tree state
    (`GridSimSourceTree`, from `git status --porcelain`, by the `StampGridSimSourceTree` target in
    `Directory.Build.targets`, which runs only when an SBOM hash is given); a dirty or unrecorded tree builds normally
    but reports `release build  no` with the reason (`NotReleaseReason`). The source-tree state is not part of the
    build hash. What the check cannot do: it does not prove the hash is that of a real SBOM, or that the build was made
    by the workflow, so a local build of a clean, committed tree given any 64-hex value still reports
    `release build  yes`. The design premise that the flag implies a green CI matrix therefore holds only for a build
    made by the workflow; the RC-1 binaries (2.1.000, before this check) carry the flag from a local build. A build
    that is not a release build reports `NOT ATTESTED (not a release build...)` and `gridsim compliance verify` exits
    1 for it; the report shows the source-tree state, says why a build is not a release build, and lists each in-scope
    control not recorded Achieved. The web `/compliance` endpoint (anonymous, always mapped) returns the same fields
    (including `sourceTree` and `notReleaseReason`) plus a `notAchieved` list. At this release five controls are
    recorded Partially achieved in the manifest (`rbac`, `distrusted-source`, `resilience`, and since 2.1.008
    `supply-chain` and `secure-sdlc-attestation`, whose descriptions now name the gap: no published release has yet
    been made through `release.yml`; section 9): 13 of the 18 in-scope controls are recorded Achieved (15 of 18 at
    2.1.005), so even a release build reads `NOT COMPLIANT` until they are closed. Evidence for the release-build
    rule and the manifest statuses: `BugHunt1101WebTests` (`B176_*`, `B196_*`), `ComplianceAttestationTests`.
- **Support, including updates and patches.** [`SECURITY.md`](../../SECURITY.md) (owner policy, 2026-11-01)
  states the support period (the latest release; earlier releases are not patched, and a .NET runtime fix arrives in
  a new GridSim build because the builds are self-contained), private reporting through GitHub private vulnerability
  reporting or `gdatooling@proton.me` (the RFC 9116 `security.txt` is unchanged), an objective CVSS v4.0 and v3.1
  base-score assessment recorded in the advisory, a remediation target of 48 hours from the recorded assessment for
  a base score of 7.0 or above (remediation meaning a fixed release and the published advisory), and the update
  channel: the security advisory, with a CVE requested through GitHub, and an automatic notice to subscribers
  (`security-notice.yml` posts a Discussions announcement on publication; GitHub notifies Watch subscribers of the
  release and the announcement). Security fixes follow the slice discipline (fix, test, CHANGELOG `Security` bullet
  naming the advisory id, version bump). The runbook is `incident-response.md` section 9. No release has yet been
  made under this policy (gap register item 14).

  This rests on the build provenance (A4) and the SLSA build-track provenance mapped in
  [`slsa-provenance.md`](slsa-provenance.md). Evidence: `ComplianceAttestationTests` (which also pins exactly which
  controls the manifest records as not Achieved), `SupplyChainManifestTests`, `WorkflowReleaseGateTests`; `RC1.md`.

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
- the production configuration runs replay-only (B2);
- since 2.1.008 the web host answers only the host names in `AllowedHosts` (default the loopback names `localhost`,
  `127.0.0.1` and `[::1]`; Compose `GRIDSIM_ALLOWED_HOSTS`): any other `Host` gets a 400 naming the setting, from the
  first middleware after the exception handler, before any page, API call or WebSocket runs (`Site.HostAllowList`). A
  malformed value stops start-up; `AllowedHosts=*` restores the old behaviour with a start-up warning. The systemd and
  nginx examples set `AllowedHosts=gridsim.example.com`;
- since 2.1.008 every web answer carries (by default) `Content-Security-Policy: frame-ancestors 'none'`,
  `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff` and `Referrer-Policy: strict-origin-when-cross-origin`
  (`Site.SecurityHeaders`; `GridSim:FrameAncestors` names sites allowed to frame the pages), and outside Development the
  host sends HSTS (`max-age=31536000`, no sub-domains, no preload) on HTTPS requests, never for the loopback names;
  `GridSim:Hsts=false` leaves HSTS to the reverse proxy. Evidence: `BugHunt1101WebTests` (`B170_*`, `B179_*`).

Not in the baseline: the Content-Security-Policy sets only `frame-ancestors`; there is no script or resource policy
(`script-src`, `default-src`), so the headers stop framing and content-type sniffing but not injected script. The nginx
example adds no security headers of its own and relies on the application's.

The transport and resource-limits gaps are closed: `GridBridgeClient` refuses an unencrypted `ws://` endpoint unless
explicitly allowed (B3 transport); on the web host a `ws://` bridge endpoint stops start-up unless
`GridSim:BridgeAllowUnencrypted=true` (the desktop app's setting is `BridgeAllowUnencrypted`), for a bridge on a
private network or loopback. `ResilienceOptions` (`GridSim.Core.Net`) carries the resource-limits baseline (B4) and
is applied to every bridge client. Evidence: `BridgeEndpointSettingTests`, `ResilienceOptionsTests`. Setting `read_only`, `cap_drop` and `no-new-privileges` on the compose file remains a deploying-party hardening
step, documented in `docs/developer/deploy/`.

### 5.2 B2 Identity and access control

**Status: Partially achieved.** Authentication and exposure controls are in place, and the shipped exposure control in
production is the replay-only gate. Since 2026-11-01 the web host enforces the role-based access-control gate on its
REST and WebSocket solve surface (`POST /api/solve`, `GET /api/topology`, every `/ws/sim` control frame) and writes
each denial to its audit log when it has one. Two gaps keep it Partially achieved, and the manifest records `rbac` so:
the Blazor dashboard's own sessions are not role-gated, and a web host without the resident estimator has no audit log
(`Audit:LogPath` is refused there), so its denials are refused but not audited.

What exists at this release:

- **Authentication in the web host.** Cookie sign-in (a challenge answers 401 and a forbid 403, never a redirect)
  and a single sign-on consumer (`/sso/bridge`, `/sso/land`) that accepts single-use, audience-checked tickets and
  refuses to act as an authority (`Sso:IsAuthority=true` stops start-up). Pinned by `WebSecurityDoc2Tests`.
- **The replay-only gate.** `GridSim:ReplayOnly` (on in the production configuration) leaves `/api` and `/ws/sim`
  unmapped, answers 404 for other dashboards, and makes `SessionManager.Create` refuse
  (`ReplayOnlyRefusedException`). On a production instance the solve surface does not exist, so there is nothing to
  authorise. Pinned by `WebSecurityDoc2Tests` and `WebHostDoc2Tests`.
- **Exposure by design.** `serve` binds no network port; `GridBridgeClient` is an outbound client; the anonymous-visitor
  rule keeps every page usable without signing in; sign-in shows "signed in as" and, since 2026-11-01, carries the
  caller's `GridSim.<role>` role claims to the role gate below.
- **Cross-site WebSocket check.** A browser upgrade to `/ws/sim` must come from the request's own host or an origin
  listed in `GridSim:AllowedOrigins`; any other origin is refused with 403, so a page elsewhere cannot drive or read
  sessions through a visitor's browser. The check trusts the request's `Host` header; since 2.1.008 that header has
  already passed the `AllowedHosts` list (B1), so a DNS-rebinding page, which arrives under the attacker's host name,
  gets a 400 before the socket is reached. With `AllowedHosts=*` an origin equal to the host is accepted only for a
  loopback host, and otherwise only when listed in `GridSim:AllowedOrigins`. Evidence: `BugHunt1101WebTests`
  (`B170_*`).
- **Network listeners bound to loopback by default (since 2.1.008).** `pmu-ingest --live udp://:port` listens on
  127.0.0.1 (it listened on every interface); a listener on another interface needs `--live-udp-source <pmu-ip>` or,
  on purpose, `--live-udp-any-sender`, or the run stops before listening (exit 2). `interpose` listens on 127.0.0.1
  when no address is given; a listener reachable from other machines needs `--allow-client <ip>[,<ip>...]` or, on
  purpose, `--allow-any-client` (exit 2 otherwise), and a client silent for `--idle-seconds` (default 120) is dropped
  so it cannot hold the single slot (`InterposeAccessPolicy`). Evidence: `BugHunt1101WebTests` (`B173_*`, `B174_*`),
  `Cf5LiveC37118Tests`.

Role-based access-control gate (`GridSim.Core.Security`), enforced by the web host since 2026-11-01
(`Site.AccessControl`):

- `OperationRole` enum: `Viewer`, `Analyst`, `Operator`, `Administrator` - least-privilege bands.
- `IdentityContext` (record): the authenticated principal id, role, and a correlation id, taken from the signed-in
  cookie or SSO claims at the service boundary.
- `RoleGuard.Require(IdentityContext, OperationRole)` - a fail-loud gate, mirroring
  `RestrictionReview.AssertPermitted` in spirit (throws a typed `AccessDeniedException`), at the *service* boundary,
  never inside the inner solver loop where it would cost per-iteration overhead and pollute the numerics.
- **Where the host calls it, and the band each surface needs.** `POST /api/solve` needs `Analyst` (an on-demand
  analysis); `GET /api/topology` needs `Viewer` (a read, so every caller holds it; it passes the gate so the band is
  stated in one place); every `/ws/sim` control frame (`start`, `play`, `pause`, `step`, `seek`, `speed`, `trip`)
  needs `Operator` (a start selects the stream source, live, replay or bridge, and runs a session; the others drive
  it). The gate runs before the action: a refused solve answers 403 before the body or the case is looked at; a refused
  control frame gets an error frame and leaves the connection's session exactly as it was (a refused `start` neither
  disposes the current session nor creates one). Opening a `results/` or `gda-out/` model by id on the dashboard needs
  `Operator` too (operation `dashboard.private`, decided on the interactive pass: one audit entry, no session built;
  the prerender of a private id does nothing); a `/ws/sim` `start` already
  needs it for every case. The resident estimator has no control surface on the web host (it
  is configured by `Realtime:*` and runs once at start-up; `GET /api/realtime` only reads it), so there is nothing
  there to authorise. The read-only calls (`/api/cases`, `/api/realtime`, `/api/openapi.json`), the pages,
  `/compliance` and `/health` are not gated.
- **Who the caller is.** A signed-in caller (the estate cookie or the SSO consumer) holds the highest band among its
  role claims whose value is `GridSim.Viewer`, `GridSim.Analyst`, `GridSim.Operator` or `GridSim.Administrator`; the
  `GridSim.` prefix keeps an estate-wide role given for another application from granting control of GridSim. Any
  other caller is anonymous and holds `GridSim:AnonymousRole`; a signed-in caller never holds less.
- **The default, decided on purpose.** `GridSim:AnonymousRole` unset gives an anonymous caller `Operator` when the
  instance answers only loopback names (the default `AllowedHosts`: the documented single-user `dotnet run` use keeps
  working), and `Viewer` when it answers any other name or `*` (fail-safe: an unknown caller may read but not solve or
  drive sessions). A value that is not one of the four band names stops start-up naming the setting. A replay-only
  instance maps neither `/api` nor `/ws/sim`, so nothing changes there.
- **Denials are audited (C1).** Each denial writes one `SecurityEventKind.AccessDenied` entry (severity Low; the reason
  names the operation, the required and actual bands, the subject and the request id) through
  `AuditRunRecorder.RecordSecurityEvent`, the path the resident estimator's own security events take, so
  `Audit:ForwardPath` carries it (section 6.1); it raises no incident on its own (`IncidentPolicy` grades it Low and
  sets no burst threshold for it). The resident estimator's run closes the log when
  it finishes; a later denial reopens the same log against its anchor, with the same sinks and keys, for that one entry
  (`AuditRunRecorder.AppendSecurityEvent`). A failure to record is reported on standard error and never replaces the
  refusal. A host with no audit log still refuses.
- The gate is *additive to* the forward-inference guard, never a replacement. The two are independent: the guard
  consults no role and has no bypass, so even an Administrator cannot obtain sub-169h data, and the guard caps what any
  caller can learn (week-stale reconstruction only) regardless of role.

Evidence: `RoleGuardTests` (each role against each operation; deny is audited; an invariant that no role reaches
sub-169h data); `CafB2RoleGateWebTests` (through the real host: the band of each surface, an allowed band passes, each
denied band on each surface is refused with a 403 or an error frame and no side effect, exactly one `AccessDenied`
entry per denial while the run holds the log and after it has closed it, a host with no audit log still refuses, the
default anonymous role and the setting's fail-loud parsing, the role-claim mapping). The remaining gaps: the Blazor
dashboard's sessions are not role-gated, and a host without the resident estimator cannot audit its denials
(section 9).

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
- **Licensing:** only an ECDSA P-256 public key is embedded, so a binary can verify a licence but cannot mint one. The
  bus-limit exemption for a bundled network is decided by content, not by name: since 2.1.008 the shipped files are
  hashed afresh on every load and checked both before and after the model is read (`BundledNetworks.BundledAcrossLoad`;
  the earlier cache keyed on size and write time is gone), a replay counts as bundled only when its model has the
  shipped network's bus ids and branch ends (`BundledNetworks.SameTopology`), and web bridge sessions pass the licence
  gate when the server's topology arrives; since 2.1.010 the desktop apps gate a replay and a GDA bridge topology the
  same way (`BundledNetworks.IsShippedTopology`). Evidence: `BugHunt1101WebTests` (`B171_*`, `B172_*`, `B178_*`,
  `B198_*`). The release signing private key is not in the repository: it is held by the owner, outside the repository,
  and RC-1 was signed on the owner's build machine; the repository secrets the release workflow is written to read are
  not set. The `signing/` folder holds the public certificate. The licence key is signed, not encrypted (it is
  inspectable, not a confidential secret), so it needs no secret store.
- **Secrets management (Achieved):** `ISecretStore` (`GridSim.Core.Security`), with `EnvironmentSecretStore`,
  `DirectorySecretStore`, `CompositeSecretStore` and `NullSecretStore`, is BCL-only (no new third-party dependency).
  `DirectorySecretStore` reads one file per secret, the form Docker and Compose secrets (`/run/secrets`), a Kubernetes
  secret volume and systemd credentials (`$CREDENTIALS_DIRECTORY`) take. The web setting `Secrets:Directory` puts it in
  front of the environment for the key-ring certificate password, so that password need not be in configuration or
  the environment. In the environment a secret set under both the ASP.NET spelling and the older doubled spelling is
  refused, naming both variables, and stops web start-up (ambiguous configuration can hide a stale secret). No
  OS-keychain or vault backend ships; a deployer can supply their own `ISecretStore` in code.
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
  a quietly-wrong state. Case validation runs schema patterns on a linear-time matcher with no time limit, so whether a
  case validates no longer depends on machine load. The desktop map refuses a case's `geography.json` whole (a wrong
  format or projection, a malformed point layout, a ring of fewer than three points, or written x/y that disagree with
  the declared projection by more than 1e-4) and never draws it in part.
- **Build hardening (Achieved):** `TreatWarningsAsErrors=true`, `EnableNETAnalyzers=true`, `AnalysisLevel=latest`,
  `AnalysisMode=Recommended`, `Nullable=enable` in `Directory.Build.props` (section 10.5). A whole class of latent
  defects cannot ship.
- **Resource limits (Achieved in-component):** the web host caps concurrent sessions (`GridSim:MaxSessions`, default
  64, `SessionLimitException`) and the sessions one client holds (`GridSim:MaxSessionsPerClient`, default 16,
  `ClientSessionLimitException`), closes a `/ws/sim` control frame over 16 KiB (`MessageTooBig`), and the Modbus
  transport caps the frame length; the audit-log viewer caps what it reads. `GridBridgeClient` now caps the
  single-message size (default 8 MiB) and the in-flight-query count (default 256), and `MeasurementSet.Load` caps the
  measurement count, all carried by `ResilienceOptions` (`GridSim.Core.Net`), which the web and desktop hosts apply to
  every bridge client they create. Since 2.1.008 a live C37.118 receive stops (exit 1) when a device sends more than
  1024 frames that are not DATA frames (`C37118LiveOptions.MaxNonDataFrames`), instead of buffering them until the
  timeout, and the web usage log is rotated once it passes `Analytics:MaxLogBytes` (default 16 MiB; the old file kept
  as `usage-<UTC stamp>.jsonl`, the visit count carried in `usage.jsonl.visits`), so start-up no longer reads an
  ever-growing file. Evidence: `ResilienceOptionsTests`, `BridgeEndpointSettingTests`, `BugHunt1101WebTests`
  (`B177_*`, `B188_*`). The solve REST calls are capped per instance (`GridSim:MaxConcurrentSolves`, 503 when full;
  `GridSim:SolveTimeoutSeconds`, 504) and each `/ws/sim` socket has a token bucket on its `step` and `start` frames
  (`GridSim:WsControlBurst`, `GridSim:WsControlRatePerSecond`), settings read and checked at start-up
  (`GA7E3Batch2WebTests`). None of these is per client or per address: request rate limiting per client at the network
  edge remains a deploying-party reverse-proxy concern.

### 5.5 B5 Resilient networks and systems

**Status: Partially achieved in-component (supervised reconnect built and wired, off by default, not yet confirmed by a release test run); not applicable to the library.**

The library is deterministic and stateless per call (section 10.3), so it has no availability state to lose. The
services do. What exists: `GridBridgeClient` fails every pending query when the stream closes or faults (the `Faulted`
event) and times out a query after 60 s (`QueryTimeout`), so no awaiter is orphaned
(`BridgeClientRobustnessTests`); the real-time estimator continues past a failed window by default
(`ContinueOnError`); the container health check probes the web host.

What is built:

- `ResilienceOptions` (`GridSim.Core.Net`): the resource limits (max in-flight queries, max message bytes) and the
  transport policy (`ws://` refused unless allowed), applied by `ApplyTo` to every bridge
  client the web and desktop hosts create; and a bounded exponential reconnect backoff schedule
  (`ResilienceOptions.BackoffDelay`).
- `BridgeReconnectSupervisor` (`GridSim.Core.Net`, since 2026-11-01) drives that schedule. On a lost bridge stream it
  waits `BackoffDelay(n)` and reconnects through the host's own connect path, up to
  `ResilienceOptions.MaxReconnectAttempts` (0, the default, means no bound), then reports one give-up. A horizon
  rejection (`ForwardInferenceException`) is never retried; a successful connection resets the count. The web host
  wires it into each bridge session (`GridSim:BridgeReconnect=true`, `GridSim:BridgeReconnectMaxAttempts`, read in
  `SessionManager`; a malformed value stops start-up) and the Avalonia desktop app into its bridge session
  (`BridgeAutoReconnect`, `BridgeReconnectMaxAttempts` in `AppSettings`). **It is off by default in both**: with the
  settings unset, a dropped bridge stream fails its pending queries (the `Faulted` event) and is not reconnected. Each
  attempt and the give-up go to the host's log (web: standard error and the session's Warning or Error; desktop: the
  event log, the status pill and an alarm on give-up), not to the security audit. The legacy WPF window is not wired.
  The bridge client has no keep-alive or stall detection, so a silent stall is not seen as a loss. Evidence:
  `BridgeReconnectSupervisorTests`, `BridgeReconnectHostWiringTests`. The `resilience` manifest status stays Partially
  achieved: the control is off by default and a release test run has not yet confirmed it.
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
estimator through `Audit:LogPath`. Since 2.1.008 the web resident estimator's entry names exactly the window files it
estimated (input `windows-consumed`: each file's name and SHA-256, hashed before and after its load, a file that
changed during the read skipped and named), instead of a hash of the whole folder taken at start-up
(`ReplayDirectoryWindowFeed.ConsumedInput`; evidence `BugHunt1101WebTests`, `B175_*`). `gridsim audit verify` and
`gridsim audit show` check and display a log, and the web and desktop apps have an AUDIT LOG viewer.

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
| (g) an access denial | an `AccessDenied` entry from the web host for each request its role gate refuses (`POST /api/solve`, `/ws/sim` control frames; B2), when the host has an audit log (that is, with the resident estimator); a host without one refuses but records nothing. The CLI has no role gate. |
| (h) a distrusted source or a dependency alert | a `DistrustedSource` entry once per named source per run when `--distrust` or `Realtime:DistrustedSources` is set (since 2026-11-01); nothing produces a `DependencyAlert` event at this release. |
| (i) a broken audit chain | refused, not logged in the same file: a log that does not verify stops the run before it starts (CLI exit 1, web start-up failure) and is reported on stderr. The `AuditChainBroken` event is deliberately not written into the log whose chain cannot be trusted. |

**Per-security-event audit (wired).** `SecurityAudit.Record` (`GridSim.Core.Diagnostics`), reached through
`AuditRunRecorder.RecordSecurityEvent`, writes one audit entry per security event with its reason (`action=security`),
typed by `SecurityEventKind` and graded by `SecuritySeverity` (the kind's baseline by default). This needs `--audit-log`
on the CLI or `Audit:LogPath` on the web host; without a log nothing is recorded. A failure to record an event is
loud (stderr, exit 1).

**Forwarding.** `--audit-forward <file>` (CLI) or `Audit:ForwardPath` (web) attaches `JsonLinesAuditSink`, which
appends every entry, all fields including both hashes, as one JSON line to a second file: the input for a SIEM
collector or a copy on a write-once mount. Sinks are notified by `AuditLogWriter` after the durable append, so a
forwarder failure never fails the append; the CLI reports it at the end of the run and exits 1, and the web host
reports a failed forward, incident write or anchor update on stderr when the resident run ends or fails. Each of these
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
  strong form keeps the private key off the host and gives the host only the public key. The off-host form is
  served by `gridsim audit sign <log> --key <private.pem> [--anchor f] [--sig f]`: at a checkpoint the key holder
  verifies the whole log against its anchor and signs that anchor, and a log that does not verify is never signed
  (`TAMPERED`, exit 3). The signature line is a documented ECDSA P-256 / SHA-256 signature
  (`gridsim-audit-anchor-sig/2`, over the entry count, head hash, first-entry hash and signing time; a `/1` line still
  verifies, with a warning, and is replaced at the next signing), so a deployer may also sign with their own tooling. A host running with only
  `--audit-verify-key` refuses its next run until the anchor its last run wrote has been signed. Evidence:
  `SignedAuditAnchorTests`, `AuditWiringTests`. The anchor
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
measures it. A true state sitting on a limit therefore does not raise a false alarm from noise. Where several
telemetry rows measure one quantity, the screen keeps the smallest sigma (failing towards a flag): for a bus quantity,
and since 2.1.010 for a branch flow on a single line between two buses too, so a loose second meter (a
pseudo-measurement) no longer widens the margin enough to hide a rating breach. Only where parallel circuits join the
same two buses (a breach names its buses, not its circuit) is the widest circuit's bound kept (gap register item 16,
closed; `BugHunt1101WebTests`, `B189_*`). The screen refuses to
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
  changes a value). `estimate --distrust`, `serve --distrust` and the web host's `Realtime:DistrustedSources` apply it (since 2026-11-01): each run writes one `DistrustedSource` security event per named source (severity Significant, so each raises an incident when an incident log is set), and the distrusted rows are left out of the chi-square J and its degrees of freedom while staying in the fit. With no source named every number is bit-identical to a run without the option. The HTI
  estimator and the recursive filters (`ekf`, `ukf`, `enkf`) refuse it, and a row with no source tag is refused while
  a list is set. The status is held at Partially achieved until the release test run confirms the wiring (the
  manifest status is pinned by `ComplianceAttestationTests`). Evidence: `DistrustedSourceTests`,
  `DistrustedSourceRedundancyTests`, `DistrustedSourceSettingTests`.

**v4.0 D1 (tested plans, suppliers in scope).** v4.0 strengthens D1: a response plan must be exercised, and its scope
must extend to suppliers. The plan in [`incident-response.md`](incident-response.md) is to be exercised as a tabletop;
no exercise has been held or recorded as at 2.1.010, so the plan is written but untested (gap register item 17). Upstream
and supplier incidents - chiefly the GDA data lake GridSim draws from - are in scope: a poisoned or
compromised upstream source is handled by quarantine (`QuarantineLiveStore`) and by the distrusted-source control
above (`--distrust`, `Realtime:DistrustedSources`). A `DependencyAlert` event would be raised as an incident by the incident
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
| A3 / A4 | SBOMs, dependency audit, code scanning | CycloneDX SBOMs (the workflow is written to attach five - CLI, web host, GridSim.Core, Avalonia app, WPF app - each stamped into its artefact; RC-1 carries one, for the CLI, made on the owner's build machine); vulnerable-package gate in CI and the release workflow; NuGet audit warnings are build errors on every build; CodeQL `security-extended` (`codeql.yml`, by hand only); Dependabot; `THIRD-PARTY-NOTICES.md`; build telemetry off | Partially achieved (the desktop apps and the NuGet package were published with no SBOM and no release has been made through the workflow that now builds one for each; CI and CodeQL by hand only, a risk-accepted deviation, section 4.4) | `ThirdPartyNoticesTests`, `WorkflowContractTests`, `AvaloniaTelemetryOptOutTests` |
| A4 | Lock files, build provenance and release provenance | `packages.lock.json` (`RestorePackagesWithLockFile=true`; locked-mode restore on GitHub Actions and in the image build, SDK pinned to 10.0.401 by `global.json`, since 2.1.008; a local restore is not locked); `Diagnostics.SupplyChain.BuildProvenance` (version/commit/build SDK/SBOM-hash/build-hash, an identity label); in `release.yml`, each artefact's own SBOM hash injected into the CLI, web image, desktop app and NuGet builds and checked on each CLI binary and desktop bundle, the linux-x64 CLI rebuilt byte for byte as a release gate, and a signed build-provenance attestation over every Release file and the image (the package is attested in `publish.yml`) | Partially achieved (the workflow did not run for RC-1: no published file is attested and no release passed the CI gate; it accepts a candidate tag since 2.1.008 but no release has been made through it, section 4.4) | `SupplyChainManifestTests`, `WorkflowReleaseGateTests`, `WorkflowContractTests`, `BugHunt1101WebTests` (`B161_*`, `B195_*`) |
| A4 | Release signing | One self-signed certificate checked by its pinned thumbprint. RC-1: Authenticode on the three Windows executables and an author signature on the NuGet package, made on the owner's build machine; Linux binaries, `SHA256SUMS.txt` and the SBOM not signed. In the workflow: the Windows CLI, the Windows WPF and Avalonia executables and the NuGet package, skipped with a warning when the signing secret is absent (it is not set; the private key is held by the owner outside the repository) | Achieved in-component for the Windows executables and the package (self-signed certificate: accepted residual, owner decision 2026-11-01) | `RC1.md`; `WorkflowContractTests`, `WorkflowReleaseGateTests` |
| A4.b | Secure-SDLC attestation | `Diagnostics.SupplyChain.ComplianceManifest` (hand-maintained inventory) + `ComplianceAttestation` (manifest hash bound to the build identity; `IsCompliant` only for a release build with every in-scope control recorded Achieved; since 2.1.008 a release build needs a 64-hex SBOM hash, a known commit and a clean source tree, and any other build is NOT ATTESTED and `compliance verify` exits 1) | Partially achieved (the verdict reads NOT COMPLIANT while five controls are Partially achieved, 13 of 18 Achieved; a local build of a clean tree given any 64-hex value still reports a release build; download integrity rests on workflow steps that did not run for RC-1; the manifest records `supply-chain` and `secure-sdlc-attestation` Partially achieved since 2.1.008) | `ComplianceAttestationTests`, `BugHunt1101WebTests` (`B176_*`, `B196_*`) |
| B1 | Secure-configuration baseline | `docs/developer/deploy/`, technical ch. 35, `Site.TrustedProxies`, non-root container, systemd hardening | Achieved in-component (bridge `ws://` refused unless `GridSim:BridgeAllowUnencrypted=true`; `AllowedHosts` host list and security headers with HSTS since 2.1.008, the CSP covering framing only; compose hardening is deploying-party) | `Cf2OpenCodeFindingsTests` (trusted proxies), `BridgeEndpointSettingTests`, `BugHunt1101WebTests` (`B170_*`, `B179_*`) |
| B2 | Authentication and exposure | Cookie sign-in, SSO consumer (`Sso.EstateSso`), replay-only gate (`GridSim:ReplayOnly`), `/ws/sim` origin check (`GridSim:AllowedOrigins`) behind the `AllowedHosts` list; `pmu-ingest` UDP and `interpose` listeners on loopback by default with sender and client rules | Achieved (as compensating controls) | `WebSecurityDoc2Tests`, `WebHostDoc2Tests`, `BugHunt1101WebTests` (`B170_*`, `B173_*`, `B174_*`) |
| B2 | Role-based access-control gate | `Security.OperationRole`, `IdentityContext`, `RoleGuard.Require`, `AccessDeniedException`; enforced by the web host (`Site.AccessControl`) on `POST /api/solve` (Analyst), `GET /api/topology` (Viewer) and every `/ws/sim` control (Operator), from `GridSim.<role>` role claims or `GridSim:AnonymousRole` (default Operator on a loopback-only instance, Viewer otherwise); each denial a `SecurityEventKind.AccessDenied` audit entry when the host has an audit log; replay-only remains the production control | Partially achieved (the dashboard's own sessions are not role-gated; a host without the resident estimator has no audit log, so its denials are not audited) | `RoleGuardTests`, `CafB2RoleGateWebTests` |
| B3 | Data-protection keys at rest | `Site.KeyRingProtection` (DPAPI / PKCS#12; certificate password resolved through `ISecretStore`) | Achieved in-component (Linux without a certificate is a deploying-party step) | `WebSecurityDoc2Tests`, `Cf3OpenCodeFindingsTests` |
| B3 | Secrets management | `Security.ISecretStore` (`EnvironmentSecretStore`, `DirectorySecretStore`, `CompositeSecretStore`, `NullSecretStore`), BCL-only; web `Secrets:Directory` | Achieved (no OS-keychain or vault backend ships) | `SecretStoreTests`, `DirectorySecretStoreTests`, `NoSecretInAuditTests` |
| B4 | Feasibility acceptance gate | `Estimation.FeasibleWlsStep` (Ring C) -> `EstimationStatus.Infeasible` | Achieved | `FeasibleEstimationTests`, `FeasibleKclGateTests` |
| B4 | Fail-loud status doctrine | `Estimation.EstimationStatus` (`Unobservable`/`NonFinite`/`Infeasible`) | Achieved | `ObservabilityTests`, `DenseEstimationLimitsTests`, `EstimationRobustnessFixTests` |
| B4 | Build hardening gate | `Directory.Build.props` (warnings-as-errors, analysers, nullable) | Achieved | CI build |
| B4 | Resource limits | `GridSim:MaxSessions` (64), `GridSim:MaxSessionsPerClient` (16, per signed-in subject or remote address), `GridSim:MaxConcurrentSolves` (8, 503 beyond) and `GridSim:SolveTimeoutSeconds` (120, 504 after) on `/api/solve` and `/api/topology`, a per-socket token bucket on `/ws/sim` `step` and `start` frames (`GridSim:WsControlBurst` 20, `GridSim:WsControlRatePerSecond` 5), `/ws/sim` 16 KiB frame cap, Modbus frame cap; `Net.ResilienceOptions` bridge message cap (8 MiB) and in-flight-query cap (256); `MeasurementSet.Load` measurement cap; C37.118 non-DATA frame cap (1024); usage-log rotation (`Analytics:MaxLogBytes`, 16 MiB) | Achieved in-component (edge rate limiting is deploying-party) | `WebHostDoc2Tests`, `ResilienceOptionsTests`, `BugHunt1101WebTests` (`B177_*`, `B188_*`), `GA7E3Batch2WebTests`, `GA7E4WebTests` |
| B5 | Service resilience / health | `Net.ResilienceOptions` (limits and transport policy applied to every bridge client; bounded `BackoffDelay` schedule driven by `BridgeReconnectSupervisor` in the web host and the desktop app, off by default); `Simulation.Health.ServiceLevelObjective` + `HealthProbe`; an SLO breach at the end of a `serve` / resident run is an `AvailabilityDegraded` security event | Partially achieved (supervised reconnect off by default; not yet confirmed by a release test run) | `ResilienceOptionsTests`, `BridgeEndpointSettingTests`, `HealthProbeTests`, `AuditWiringTests`, `BridgeClientRobustnessTests`, `BridgeReconnectSupervisorTests`, `BridgeReconnectHostWiringTests` |
| C1 | Hash-chained audit | `Diagnostics.AuditLog`, `AuditLogEntry` | Achieved | `AuditLogTests`, `AuditLogDeletionTests` |
| C1 | Audit persistence | `Diagnostics.AuditLogWriter`, `AuditLogFile`, `AuditEvent`, `AuditRunRecorder` (`gridsim-audit/1`); `--audit-log` on `estimate`, `crossval`, `interpose`, `serve`; web host `Audit:LogPath`; `gridsim audit verify` / `show` | Achieved | `AuditLogFileTests`, `AuditRunRecorderTests`, `EstimationAuditTrailTests` |
| C1 | Audit anchoring | `Diagnostics.AuditAnchor`, `AuditAnchorFile` (`gridsim-audit-anchor/1`), checked on every open; `SignedAuditAnchor` (detached ECDSA P-256 over the anchor) via `--audit-sign-key` / `--audit-verify-key`, `Audit:AnchorSigningKeyPath` / `Audit:AnchorVerificationKeyPath`, `audit verify --pubkey`, `audit keygen`; `--audit-anchor`, `Audit:AnchorPath` | Achieved (protection against a key-holding host needs the private key off the host, section 6.2) | `AuditLogFileTests`, `Doc3AuditAnchorTests`, `SignedAuditAnchorTests`, `AuditWiringTests` |
| C1 | Per-event security logging | `Diagnostics.SecurityAudit.Record` via `AuditRunRecorder.RecordSecurityEvent` at the detection sites of `estimate`, `serve`, `interpose` and the web resident estimator; forwarded by `JsonLinesAuditSink` (`--audit-forward`, `Audit:ForwardPath`) | Achieved (`AccessDenied` from the web host's role gate since 2026-11-01; no product site yet for `DistrustedSource`, `DependencyAlert`; `AuditChainBroken` refused and reported on stderr, not logged, section 6.1) | `SecurityAuditTests`, `AuditWiringTests` |
| C1 / C2 | Residual-cloaked FDIA detection | `Estimation.Security.FdiaScreen`, `FdiaScreenReport` (incl. `AnchorInconsistent` with `protectedAnchors`) | Achieved (out-of-envelope and feasible-landing sub-classes; tightest-meter bound for bus and single-line branch quantities since 2.1.010) | `FdiaProtectedAnchorTests`, `FdiaCloakedAttackTests`, `FdiaScreenTests`, `BugHunt1101WebTests` (`B189_*`) |
| C2 | Security-event aggregation / threat hunting | `Diagnostics.Incident.SecurityEventMonitor` (an `IAuditSink`), attached by `--incident-log` / `Audit:IncidentLogPath` | Achieved | `SecurityEventMonitorTests`, `AuditWiringTests` |
| D1 | Deterministic estimate and replay for forensics | `WlsStateEstimator`, `GdaReplayLoop`; no clock in estimation code | Achieved | `EstimatorDeterminismTests`, `GdaReplayDeterminismTests`, `EstimationClockIndependenceTests`, `Doc16NumericsDeterminismTests` |
| D1 | Live-data quarantine | `QuarantineLiveStore` (refuses the evidence lake) | Achieved | `QuarantineLiveStoreTests` |
| D1 | Incident hooks | `Diagnostics.Incident.IncidentReport` / `IIncidentSink` / `IncidentRouter` / `IncidentPolicy`; `SecurityIncidentSink`, `JsonLinesIncidentSink` and `IncidentWiring` attached by `--incident-log` / `Audit:IncidentLogPath`; [`incident-response.md`](incident-response.md) | Achieved | `IncidentRouterTests`, `AuditWiringTests` |
| D1 | Distrusted-source containment | `Estimation.DistrustedSources` applied by `estimate --distrust`, `serve --distrust` and `Realtime:DistrustedSources`; one `DistrustedSource` event per source per run; distrusted rows excluded from the chi-square J and degrees of freedom (`DistrustedSourceSetting`, `WlsStateEstimator`) | Partially achieved (held until the release test run) | `DistrustedSourceTests`, `DistrustedSourceRedundancyTests`, `DistrustedSourceSettingTests` |
| A1 / A2 / D1 / D2 | Governance, risk and incident documents | [`security-governance.md`](security-governance.md), [`risk-register.md`](risk-register.md), [`incident-response.md`](incident-response.md) | Achieved (org-level governance remains deploying-party) | the documents, cross-referenced from sections 4.1, 4.2, 7.1 |
| Out of scope / deploying-party | Host OS, perimeter, physical security, org-level A1 governance, B6 awareness | the deploying party's environment and organisation | Out of scope / deploying-party | the deploying party's own CAF assessment |

---

## 9. Gap register

Three in-component code gaps remain open (items 1, 4 and 9); each is recorded Partially achieved in
`ComplianceManifest`, so the compliance attestation reads NOT COMPLIANT until it is closed (section 4.5). Items 10 to
18 were opened at the 2.1.005 review: they concern the release process, the source repository and a few code paths,
and items 10 to 12 are why A3, A4 and A4.b read Partially achieved. Since 2.1.008 the manifest agrees, recording
`supply-chain` and `secure-sdlc-attestation` as Partially achieved with the release-path gap named (item 10). The
2.1.008 to 2.1.010 fixes closed items 15 and 16 and narrowed items 10 and 13. The other entries are kept as a record of
what closed them.

1. **RBAC (B2) - open (Partially achieved), narrowed 2026-11-01.** The access-control gate `OperationRole`,
   `IdentityContext`, `RoleGuard.Require(...)`, `AccessDeniedException` (`GridSim.Core.Security`) is built and tested,
   never inside the solver loop. Since 2026-11-01 the web host enforces it (`Site.AccessControl`, section 5.2):
   `POST /api/solve` needs Analyst, `GET /api/topology` Viewer, every `/ws/sim` control frame Operator; the caller's
   band comes from `GridSim.<role>` role claims or `GridSim:AnonymousRole` (default Operator on an instance that answers
   only loopback names, Viewer otherwise); a denial is refused before the action runs and written as one
   `SecurityEventKind.AccessDenied` entry through the host's audit writer (`AuditRunRecorder.RecordSecurityEvent`, or
   `AuditRunRecorder.AppendSecurityEvent` once the resident estimator's run has closed the log). The resident estimator
   has no control surface on the web host to gate. The replay-only gate remains the production exposure control.
   Independent of `ForwardInferenceGuard`; no role reaches sub-169h data. Still open: the Blazor dashboard creates its
   own sessions with no role check, and a host without the resident estimator has no audit log (`Audit:LogPath` is
   refused there), so its denials are refused but not audited. To close: gate the dashboard's session start and
   controls, and give a host without the resident estimator an audit log for its security events. Evidence:
   `RoleGuardTests`, `CafB2RoleGateWebTests`.
2. **Per-event audit and signed anchor (C1) - closed.** `AuditRunRecorder.RecordSecurityEvent` writes one entry per
   security event at the detection sites listed in section 6.1, forwarded by `--audit-forward` / `Audit:ForwardPath`;
   `SignedAuditAnchor` is wired through `--audit-sign-key` / `--audit-verify-key`, the matching web settings and
   `audit verify --pubkey`, so a joint rewrite of log and anchor is detectable by anyone who lacks the private key
   (fully so only when the private key is off the host). Since 2026-11-01 the web host's role gate writes
   `AccessDenied` (item 1) and the distrust setting writes `DistrustedSource` (item 9); `DependencyAlert` has no product detection site yet. Evidence: `SecurityAuditTests`, `SignedAuditAnchorTests`,
   `AuditWiringTests`.
3. **Supply chain (A3/A4) - reopened at 2.1.005; see items 10 and 11.** `packages.lock.json` lock files record the
   restore graph; `BuildProvenance` exposes version/commit/build SDK/SBOM-hash/build-hash (an identity label); the
   release workflow is written to inject each artefact's own SBOM hash into every shipped build, check it on each CLI
   binary and desktop bundle, rebuild the Linux CLI byte for byte as a release gate, and attach a signed
   build-provenance attestation to every Release file and the image. Those workflow steps did not run for
   either published release candidate. Since 2.1.008 the SDK is pinned by `global.json` and CI restores in locked
   mode (item 13). Since 2026-11-01 every Action is pinned to a full commit SHA. Evidence: `SupplyChainManifestTests`,
   `WorkflowReleaseGateTests`, `BugHunt1101WebTests` (`B161_*`).
4. **Service resilience (B5) - open (Partially achieved); resource limits (B4) - closed in-component.**
   `ResilienceOptions` (bridge message cap, in-flight-query cap; `ws://` refused unless allowed) is
   applied to every bridge client, and an SLO breach at the end of a `serve` / resident run is an
   `AvailabilityDegraded` security event. Since 2026-11-01 `BridgeReconnectSupervisor` drives the bounded reconnect
   backoff schedule in the web host (`GridSim:BridgeReconnect`) and the desktop app (`BridgeAutoReconnect`). The open
   part: both are off by default, a release test run has not yet confirmed them, the legacy WPF window is not wired,
   the bridge client has no keep-alive, and the attempts go to the host log, not the security audit. Edge rate limiting
   is deploying-party. Evidence: `ResilienceOptionsTests`, `BridgeEndpointSettingTests`, `HealthProbeTests`,
   `AuditWiringTests`, `BridgeReconnectSupervisorTests`, `BridgeReconnectHostWiringTests`.
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
9. **Distrusted-source containment (D1) - open (Partially achieved), wired 2026-11-01.** The asked-for wiring now
   exists: `estimate --distrust`, `serve --distrust` and the web host's `Realtime:DistrustedSources` apply it (since 2026-11-01): each run writes one `DistrustedSource` security event per named source (severity Significant, so each raises an incident when an incident log is set), and the distrusted rows are left out of the chi-square J and its degrees of freedom while staying in the fit. With no source named, every number is bit-identical (`DistrustedSourceRedundancyTests`, exact
   comparison on case14 and case30). Still open: the wiring has not been through the release test run, so the
   manifest keeps `distrusted-source` at Partially achieved; HTI and the recursive filters refuse the option.
   Evidence: `DistrustedSourceTests`, `DistrustedSourceRedundancyTests`, `DistrustedSourceSettingTests`.
10. **Release path bypasses the release gates (A4/A4.b) - open, narrowed at 2.1.008 (Partially achieved).** At RC-1
    `release.yml` accepted only a `vMAJOR.MINOR.PATCH` tag, so it stopped in its first step on `v2.0.195-rc1.0` and
    `v2.1.000-RC-1`. RC-1 was built, signed and published by hand from the owner's build machine: no published file
    has a provenance attestation, none was built behind the CI matrix gate, no web image or web SBOM exists, and the
    binaries' "release build" flag came from a local build. Done since: the workflow accepts a release-candidate tag
    (the numeric part must equal `<Version>`; marked a pre-release; no NuGet package or image pushed for a candidate;
    `BugHunt1101WebTests`, `B195_*`), and the manifest records `supply-chain` and `secure-sdlc-attestation` as Partially
    achieved, naming this gap (`B196_*`, `ComplianceAttestationTests`). Still open: no release has been made through
    the workflow, its ship scope (GA-5, 2026-11-01: the desktop apps as RC-1 shipped them, five SBOMs, the independent
    Linux CLI rebuild as a release gate) is written but has not been run, it still differs from RC-1 in the CLI (it
    builds osx-arm64 as well and names archives `gridsim-<version>-<rid>`, RC-1 used `gridsim-cli-<version>-<rid>` with
    no macOS CLI) and does not attach the NuGet package to the Release (the package goes to the feed), and the signing
    secrets it reads are not set, so it would publish unsigned. To close: build and publish the next release through the workflow, with the ship
    scope decided and the signing secrets set (or an unsigned release chosen on purpose), and confirm its attestation
    with `gh attestation verify`.
11. **SBOM coverage (A3) - closed in the workflow 2026-11-01, not yet exercised by a release.** The published RC-1
    SBOM describes the command line tool only; the WPF and Avalonia desktop apps and the NuGet package were published
    without one. Done: the release workflow builds five SBOMs, all Release files (CLI, web host, GridSim.Core for the
    NuGet package, Avalonia app, WPF app); each desktop app is built by job `desktop` with its own SBOM's hash
    injected and the bundle checked for it, and the NuGet package is stamped with the GridSim.Core SBOM's hash
    (`SlsaSupplyChainPinTests`, `WorkflowReleaseGateTests`, `WorkflowContractTests`). Still open: no release has been
    made through the workflow, so the published RC-1 still has one SBOM. To close: a release made through the
    workflow, its five SBOMs and their stamped hashes confirmed.
12. **CI witness not green (A4) - open.** No CI run on `main` has passed since the history reset. The three runs
    started by hand on 2026-10-31 all came after RC-1 was published and all failed; the last (on 2.1.002) failed one
    desktop-app layout test on the macOS hardware-intrinsics-off leg. That test now waits for the rows to be shown
    before measuring them (harness timing only, no assertion changed) and passes locally; no CI run has been made
    since, so the matrix is still not shown green. The forced AVX-512 legs are reported NOT CHECKED
    and skipped on a runner without AVX-512, and RC-1's local suite skipped 31 large-case tests by the RAM gate, so the
    AVX-512 tier and the largest cases were not exercised for RC-1. To close: a green matrix on the release commit,
    recorded before publication, and the large-case run on a machine that holds it.
13. **Build environment and source repository (A4.b) - open, narrowed at 2.1.008.** Done since: a release build needs
    a 64-hex `GridSimSbomSha256`, a known commit and a clean source tree, recorded in the build
    (`BuildProvenance.ReleaseRefusal`, `GridSimSourceTree`; `BugHunt1101WebTests`, `B176_*`); the SDK is pinned to
    10.0.401 by `global.json` with no roll-forward, every workflow and the image use it, and restores on GitHub Actions
    and in the image build run in locked mode (`B161_*`). Still open: the release build that was published ran on the
    owner's development machine; the release-build check proves form and a clean tree, not that the hash is a real
    SBOM's or that the workflow built the binary, so a local build of a clean tree can still set the flag; a local
    restore is not locked; release tags are lightweight and unsigned (commits are signed). Done 2026-11-01: the public
    repository has a ruleset on `main` (no deletion, no force push; administrators may bypass), secret scanning with
    push protection, Dependabot alerts and private vulnerability reporting (`tools/github/apply-public-settings.ps1`,
    which reads each setting back). To close: releases built only by the workflow (or on a clean, recorded build
    machine), and signed tags.
14. **Support and update policy (A4.b) - closed 2026-11-01 (owner policy).** [`SECURITY.md`](../../SECURITY.md)
    states the support period (latest release only), how to report (GitHub private vulnerability reporting, or
    `gdatooling@proton.me`), the objective CVSS v4.0 and v3.1 base-score assessment recorded in the advisory, the
    remediation target (48 hours from the recorded assessment for a base score of 7.0 or above, meaning a fixed
    release and the published advisory), how fixes ship (advisory, CVE requested through GitHub, CHANGELOG
    `Security` entry) and how runtime fixes are delivered (a new self-contained build). Deployers learn of a fix
    through the automatic notice: `.github/workflows/security-notice.yml` posts a Discussions announcement when a
    release naming a published advisory is published, and GitHub notifies Watch subscribers of the release and the
    announcement. The statement lives in the repository root (GitHub shows it as the security policy) rather than
    inside the release archives. No release has yet been made under it, so the 48-hour target is stated, not yet
    evidenced; the runbook is `incident-response.md` section 9.
15. **Web host browser hardening (B1/B2) - closed at 2.1.008.** The host answers only the names in `AllowedHosts`
    (default the loopback names; any other `Host` gets a 400 naming the setting before any page, API call or WebSocket
    runs), which stops a DNS-rebinding page reaching `/ws/sim`; every answer carries `Content-Security-Policy:
    frame-ancestors 'none'`, `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff` and `Referrer-Policy:
    strict-origin-when-cross-origin` by default, and outside Development HSTS is sent on HTTPS requests; the systemd
    and nginx examples set the public name. Evidence: `BugHunt1101WebTests` (`B170_*`, `B179_*`), `Site.HostAllowList`,
    `Site.SecurityHeaders`. Left as stated limits, not gaps: the CSP covers framing only (no script policy), and
    `AllowedHosts=*` restores the old behaviour with a start-up warning.
16. **FDIA margin rule for branch flows (C1) - closed at 2.1.010.** `FdiaScreen` now bounds a branch-flow breach by
    the tightest meter on the line, as it does for bus meters; only parallel circuits between the same two buses keep
    the widest circuit's bound, because a breach names its buses, not its circuit. The estimate itself is unchanged;
    only the screen's significance margin moves. Evidence: `BugHunt1101WebTests` (`B189_*`), `FdiaScreenTests`.
17. **Incident plan not exercised (D1) - open.** The script for the first tabletop exercise, a mock CVE scored 7.5
    (CVSS v3.1) taken from private report to published fix, automatic notice and the deploying OES's Regulation 11
    decision, is written with its record form ([`tabletop-cve-exercise.md`](tabletop-cve-exercise.md)). Neither it
    nor the `SuspectedFdia` / audit-verify-failure exercise named in [`incident-response.md`](incident-response.md)
    section 8 has been held. To close: hold the exercise, fill in its record and commit it.
18. **Threat model not refreshed (A2/A4.b) - closed 2026-11-01.** Technical ch. 35 now models the Avalonia desktop
    app, its case-folder input `geography.json` and the release path (building, signing, source and distribution),
    each with its threat, control and residual ("Threat model: the desktop app, case geography and the release
    path"). Residuals it names: no file-size cap on `geography.json` or on case files (a very large file can exhaust
    memory), the hand-built RC-1 with no attestation, the self-signed certificate (accepted), and unsigned tags.

**Remaining owner actions:** items 10 to 13 and 17 are actions on the release process, the repository and the
runbook rather than on the product code; items 15 and 16, the two code changes, are closed.

**Remaining deploying-party actions (not code gaps):** provide a PKCS#12 certificate on Linux so the key
ring is encrypted at rest; point `--audit-forward` / `Audit:ForwardPath` at a WORM mount or SIEM collector; keep the
anchor-signing private key off the host where the threat model needs it; set `read_only`, `cap_drop` and
`no-new-privileges` on the compose file; set `AllowedHosts` to the public name; apply edge rate limiting at the
reverse proxy, and a script Content-Security-Policy there if the threat model needs one;
build the web host itself, since no web host build was published at RC-1; exercise its own incident-response plan.

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
  macOS, forced AVX-512 and the non-FMA axis (the AVX2-off leg, whose preflight requires `Fma=False`) with no advisory
  leg, the SBOM hash is injected and checked, Release files and the web image get a provenance attestation, signing
  trusts only the pinned certificate, and the build SDK is stamped. These tests read the workflow files; they show
  what the workflow would do, not that it ran (section 4.4).
- `RoleGuardTests` (role x operation matrix; deny is audited; no role reaches sub-horizon data),
  `CafB2RoleGateWebTests` (the web host's role gate on each surface, one audited `AccessDenied` per denial, no side
  effect on a denial, the default anonymous role), `SecurityAuditTests`
  (one entry per security event with its reason), `SignedAuditAnchorTests` (joint rewrite detected),
  `SecurityEventMonitorTests` (burst escalation), `IncidentRouterTests` (one incident per detection, no future-dated
  claim), `DistrustedSourceTests`, `SecretStoreTests`, `NoSecretInAuditTests`, `SupplyChainManifestTests` and
  `ComplianceAttestationTests`: the controls built this release.
- `BugHunt1101WebTests` (the 2.1.008 to 2.1.010 fixes, one test group per fix): `B161_*` SDK pin and locked restore;
  `B176_*` release-build rule (64-hex SBOM hash, known commit, clean tree); `B195_*` candidate tags; `B196_*` manifest
  statuses; `B170_*` host allow-list; `B179_*` security headers and HSTS; `B173_*` and `B177_*` C37.118 UDP sender rule
  and non-DATA frame cap; `B174_*` `interpose` client rule and idle drop; `B171_*`, `B172_*`, `B178_*` and `B198_*`
  licence gating by content and topology; `B175_*` resident-estimator audit inputs; `B188_*` usage-log rotation;
  `B189_*` FDIA branch bound. The workflow tests read the workflow files, as above.

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
