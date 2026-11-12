# NIST Secure Software Development Framework (SSDF) mapping

**Assurance statement. Status as at GridSim 2.1.010 (reviewed 2026-11-01; RC-1 released 2026-10-31 as 2.1.000).** This document maps GridSim's development and release
practice to the NIST Secure Software Development Framework (SP 800-218, version 1.1), practice group by practice
group. It is a companion to `caf-mapping.md`: where that document maps the running system to the Cyber Assessment
Framework, this one maps the process that produces the system. It exists because CAF v4.0 principle A4.b, Secure
Software Development and Support, asks a supplier to evidence a secure development lifecycle and to attest to the
authenticity and integrity of the software it ships. The SSDF is the recognised yardstick for that lifecycle, so it
is the appendix against which A4.b is read.

Scope and evidence bar are inherited from `caf-mapping.md`: the unit of assessment is GridSim itself (the engine,
the CLI and the hosted services), not the deploying party's environment; and no practice is marked Achieved unless a
named passing test or a build artefact evidences it. The status vocabulary is the same four values: Achieved,
Partially achieved, Not achieved, Out of scope (deploying-party).

---

## 1. Why the SSDF fits GridSim

The SSDF is outcome-based and does not prescribe a toolchain. That suits a codebase whose central discipline is
already a security property: the forward-inference guard bounds what the software can ever reveal, the tests are
constitutional (never loosened to make a change pass), and every result is bit-reproducible. Those three facts do
most of the SSDF's work on their own. The remaining practices are release hygiene, dependency handling and build
integrity, most of which already exist in the repository and are named below.

A single deliberate deviation is recorded up front so the rest of the mapping reads honestly: continuous integration
runs when the release workflow calls it on a release tag, or by manual dispatch, not on every push; CodeQL runs by
manual dispatch only. This is a conscious, risk-accepted choice. The SSDF asks for the outcome (every change is built
and checked deterministically). The compensating control the owner has chosen is that every build the owner makes for
use is a production build carrying the compliance attestation, checked by the local test suite, with CI run by hand as
the witness of a release; as designed, `release.yml` also calls `ci.yml` as a reusable workflow (job `ci`) and every
publishing job needs it. The matrix covers Linux and Windows in the normal, AVX2-off (which also holds the non-FMA
axis), hardware-intrinsics-off and forced 256-bit modes, a forced AVX-512 leg on each (reported NOT CHECKED and
skipped on a runner without AVX-512), and macOS on Apple silicon and Linux and Windows on ARM64 in the normal and
hardware-intrinsics-off modes; no leg is advisory.

At RC-1 (2.1.000, 2026-10-31) the compensating control was only half in place. The workflow refused the RC tag form
then (it accepts it since 2.1.008, but no release has been made through it since), so RC-1 was built, signed and
published by hand from the owner's build machine after the local suite passed
(GridSim.Tests 10,291 passed and 31 skipped by the RAM gate on an 8 GB machine, GridSim.App.Tests 310 passed;
`RC1.md`); the CI runs started by hand after publication all failed, the last on one desktop-app layout test on one
macOS leg. The RC-1 binaries report `release build  yes` because the owner injected the SBOM hash in the local build,
so the flag does not show that the matrix passed. This is stated again under PO.3, PO.4, PS.2 and PW.6, and in
section 7.

---

## 2. Prepare the Organization (PO)

### 2.1 PO.1 Define security requirements for software development

**Status: Achieved.**

The governing security requirement is codified, not merely written: `UnderstandingRestrictions.md` is the policy,
and its five invariants are the acceptance criteria every change must meet. `CONTRIBUTING.md` states the criteria for
every slice (machine-epsilon bit-identity, constitutional tests, fail-loud, zero-warning build). The requirement
that the engine cannot perform forward inference on live grid data is enforced by `ForwardInferenceGuard` and pinned
by `RestrictionInvariantTests`.

### 2.2 PO.2 Implement roles and responsibilities

**Status: Partially achieved.** The restriction-review register (`RestrictionReview.Reviewed`) is a code-resident,
test-enforced record of who reviewed the one seam that matters. A broader named security owner and review cadence are
documented in the companion `security-governance.md`. Staff-level role definition beyond the project is
out of scope (deploying-party).

### 2.3 PO.3 Implement supporting toolchains

**Status: Partially achieved.** The toolchain is declared: a single `net10.0` target, a single `<Version>` in
`Directory.Build.props`, .NET analysers at `AnalysisMode=Recommended` with `TreatWarningsAsErrors=true`, CodeQL with
the `security-extended` suite (`codeql.yml`, run by hand), Dependabot over NuGet, Docker and GitHub Actions, build
telemetry switched off on every build (`AvaloniaTelemetryOptOutTests`), and the CI, release and publish workflows
(`ci.yml`, `release.yml`, `publish.yml`), pinned by `WorkflowContractTests` and `WorkflowReleaseGateTests`. Since
2.1.008 the SDK is fixed: `global.json` pins 10.0.401 (the SDK RC-1 was built with) with `rollForward` `disable`, so
any machine without exactly that SDK refuses to build; every workflow installs it from that file
(`global-json-file: global.json`), and the container image builds on `sdk:10.0.401`; CI restores NuGet packages in
locked mode (`BugHunt1101WebTests`, `B161_*`). Since 2026-11-01 every GitHub Action in every workflow is pinned to a
full commit SHA, with its release tag in a comment, and the CycloneDX SBOM tool is installed at a named version.
Still not fixed: the release workflow, which the CI-cadence deviation in section 1 relies on, did not build
RC-1 and has built no release since.

### 2.4 PO.4 Define and use criteria for software security checks

**Status: Partially achieved.** The criteria are mechanical: a nullable or analyser warning is a build failure; a
NuGet audit warning is a build failure; and, in the release workflow, the full test suite must be green on every leg
of the CI matrix before anything is built for shipping. The first two applied to RC-1; the third did not, because
RC-1 was not built by the workflow and no CI run passed before (or after) its publication. The compliance verdict is not itself a release gate: the release does not stop when a control is recorded
Partially achieved, the shipped binary reports `NOT COMPLIANT` and lists it instead. What the release workflow checks is
that each CLI binary reports `release build  yes` with the SBOM hash it was built against (section 4.4), that each
desktop bundle carries the hash of its own SBOM, and that the Linux CLI rebuilds byte for byte (job `reproducible`,
which the Release job waits for); for RC-1 the owner made the CLI check by hand after signing (`RC1.md`). A status is upgraded only when the build is warning-free
and the evidencing tests pass.

### 2.5 PO.5 Implement and maintain secure development environments

**Status: Partially achieved for the build environment; the developer workstation is the owner's.** The release
workflow is written to build on GitHub-hosted runners, gated on the full CI matrix, with a signed build-provenance
attestation on every Release file. RC-1 was built instead on the owner's development machine (Windows 11, SDK
10.0.401), so for the published release the development and build environments are one machine and no attestation
records the build. Since 2026-11-01 the release workflow rebuilds the linux-x64 CLI independently and compares it
byte for byte with the shipped archive (job `reproducible`); it has not run yet, covers that archive only, and the
Release job waits for it, so a non-reproducible Linux CLI stops the release. Since the same date the workflow also
builds the WPF and Avalonia desktop apps (job `desktop`; GA-5, an owner decision), each with its own SBOM hash, signed
on Windows with the same pinned certificate, with no macOS desktop build; this too has not run. (The determinism tests cited in `nasa-std-7009-credibility.md` compare numerical results, not binaries).

---

## 3. Protect the Software (PS)

### 3.1 PS.1 Protect all forms of code from unauthorised access and tampering

**Status: Partially achieved.** The source is held in a single `main` branch with no alternative branches carried.
Development pushes go to a private repository (`GridSim-dev`); the public `GridSim` repository carries the same `main`
and is where CI and releases run. Every commit on `main` is signed with the owner's key; release tags are lightweight
and unsigned. Since 2026-11-01 the public repository has a ruleset on `main` (no deletion, no force push;
administrators may bypass), secret scanning with push protection, Dependabot alerts and private vulnerability
reporting (`tools/github/apply-public-settings.ps1`). The
history was reset at RC 1.0 (2026-10-30), so the repository holds no commit history from before then;
the CHANGELOG keeps the per-version record. The licensing path embeds only an ECDSA P-256 public key, so a binary can
verify a licence but cannot mint one (the licence key pair was replaced at 2.0.167). The release signing private key
is held by the owner off the repository (the `signing/` folder holds only the public certificate); the RC-1 signatures
were made on the owner's build machine, and the repository secrets the release workflow reads are not set. Repository
access control itself is a platform concern (GitHub account).

### 3.2 PS.2 Provide a mechanism for verifying software release integrity

**Status: Partially achieved.** For RC-1 the integrity mechanisms are:

- The three Windows executables (`gridsim.exe`, `GridSim.Wpf.exe`, `GridSim.App.Desktop.exe`) carry an Authenticode
  signature and the `GridSim.Core` NuGet package an author signature, each with an RFC 3161 timestamp, from one
  self-signed certificate (`signing/gridsim-signing.cer`); a consumer checks the signer thumbprint, or imports the
  published certificate to validate the signature.
- `SHA256SUMS.txt` on the GitHub Release, and the SHA-256 of every archive and executable in `RC1.md`. The Linux files
  are not signed, and the checksums are uploaded by the same hand as the files, so for Linux they show only that a
  download matches the upload.

The signed SLSA build-provenance attestation (`actions/attest-build-provenance`, Sigstore;
`gh attestation verify <file> -R <owner>/GridSim`) that `release.yml` is written to put on every Release file (the CLI
and desktop archives, the five SBOMs and `SHA256SUMS.txt`) and the web image does not exist for any RC-1 file, because
the workflow did not build RC-1. `release.yml` also signs the Windows WPF and Avalonia executables, as well as the
Windows CLI; `publish.yml` signs and attests the NuGet package.

The embedded `BuildProvenance` record (version, commit SHA, build SDK, SBOM SHA-256 and a deterministic `BuildHash`
over them) and `ComplianceAttestation` are reported by `gridsim compliance verify`. They identify a build; they do not
verify its integrity, because the build hash covers no artefact bytes. The provenance and attestation mapping is
detailed in `slsa-provenance.md`.

### 3.3 PS.3 Archive and protect each software release

**Status: Partially achieved.** The RC-1 GitHub Release (tag `v2.1.000-RC-1`) keeps its eight files: the two CLI
archives, the WPF and Avalonia app archives, the NuGet package, the CLI's CycloneDX SBOM and `SHA256SUMS.txt`; the
earlier RC 1.0 Release (tag `v2.0.195-rc1.0`) is kept beside it. The CHANGELOG is an append-only, per-version record
(`2.1.NNN` on the current line, after `2.0.N` and `1.95.NNN`), enforced by `VersionContractTests`. Residuals: no SBOM
for the desktop apps or the package (the workflow is written to publish five SBOMs, one per shipped application, but
has not been run), no provenance attestation, unsigned lightweight tags, and no commit history before the RC 1.0 reset
in the repository.

---

## 4. Produce Well-Secured Software (PW)

### 4.1 PW.1 / PW.2 Design to meet security requirements; review the design

**Status: Achieved for the load-bearing design.** The design decisions that carry the security case, the
forward-inference boundary, the fail-loud status doctrine (no NaN reported as a plausible state), the physics
acceptance gate and the independent FDIA screen, are documented in `caf-mapping.md` sections 3 to 6 and reviewed
through the restriction-review discipline.

### 4.2 PW.4 Reuse existing, well-secured software

**Status: Achieved.** `GridSim.Core` deliberately depends on no third-party package; it uses `System.Numerics`,
`System.Security.Cryptography` and `System.Text.Json` from the base class library. The command line tool adds
`Parquet.Net` (data lake) and `ILGPU` with native ROCm/HIP interop (the optional GPU backend, outside the
bit-identity contract and falling back to the exact CPU solver). The Avalonia desktop app adds Avalonia 11.3.22,
SkiaSharp 2.88.9 and HarfBuzzSharp 8.3.1.1 (with their native libraries) and CommunityToolkit.Mvvm 8.4.2, admitted
with exact versions in committed lock files. Every shipped package is named with its licence in
`THIRD-PARTY-NOTICES.md` and pinned by `ThirdPartyNoticesTests`. The small, declared dependency set is the point:
the reuse that exists is well-secured and minimal.

### 4.3 PW.5 Create source code adhering to secure coding practices

**Status: Achieved.** `Nullable=enable`, the full analyser set and warnings-as-errors make an entire class of
null-dereference and disposal defects impossible to ship. Input validation is fail-loud (`MeasurementSet.Validate`
rejects malformed rows with `InvalidDataException` rather than producing a quietly wrong state).

### 4.4 PW.6 Configure the build process to improve executable security

**Status: Partially achieved.** `ContinuousIntegrationBuild` normalises paths; `packages.lock.json` per project (via
`RestorePackagesWithLockFile`) records the restore graph. Since 2.1.008 the SDK is pinned (`global.json`, 10.0.401, no
roll-forward) and restores on GitHub Actions (`RestoreLockedMode`) and in the container image build (`--locked-mode`)
run in locked mode, so there a restore that would change a lock file fails instead of rewriting it; the command line
tool's lock file covers its three release runtimes, so a release publish no longer rewrites it. A restore on the
owner's machine is not locked. The SBOM's SHA-256 is injected as assembly metadata (`-p:GridSimSbomSha256=<hash>`) and
the .NET SDK version is stamped as `GridSimBuildSdk`, both folded into the build hash (an identity label). In the
workflow, the five SBOMs come first, each artefact is built with the hash of its own SBOM (the NuGet package with the
GridSim.Core SBOM's), and each CLI binary must report `release build  yes` and exactly that SBOM hash, or the release
stops; each desktop bundle must contain its hash; for RC-1 the owner did this by hand for the CLI. Since 2.1.008 the release-build flag needs a 64-hex SBOM hash, a
known commit and a clean source tree, recorded in the build as `GridSimSourceTree` (`BuildProvenance.ReleaseRefusal`;
`BugHunt1101WebTests`, `B176_*`); it still does not prove the hash is a real SBOM's or that the workflow built the
binary, so a local build of a clean tree can set it. The reproducible-build check in the workflow (linux-x64 CLI
only, a gate on the Release job, not yet run) is the route that would show a binary is the released one.

### 4.5 PW.7 Review and analyse human-readable code

**Status: Achieved (automated) / Partially achieved (manual).** CodeQL runs the C# `security-extended` query suite
when started by hand (`codeql.yml`, manual dispatch only; it is not part of the release gate; the runs on 2026-10-31
passed and its findings led to fixes in 2.1.001); the analyser set runs on every build. The structured manual security assessment of the October 2026 programme is kept in local records that
are excluded from version control, so it is evidenced off-repository: no clone or release contains it, and an
assessor must ask the owner for it. A standing cadence is named in `security-governance.md`.

### 4.6 PW.8 Test executable code to identify vulnerabilities

**Status: Achieved.** The suite exercises the security controls directly: `ForwardInferenceGuardTests`,
`RestrictionInvariantTests`, the `AuditLog*` tamper-evidence tests, `FdiaScreenTests`, `FdiaCloakedAttackTests`, the
web security tests and the determinism tests. A vulnerable-package gate (`dotnet list package --vulnerable
--include-transitive`) runs in CI and in the release workflow, and NuGet audit fails every build, including the
owner's local release build. The breadth of the suite is inventoried in
`nasa-std-7009-credibility.md`.

### 4.7 PW.9 Configure software to have secure settings by default

**Status: Achieved.** The production configuration runs replay-only (`GridSim:ReplayOnly`), which leaves the solve
surface unmapped; the container runs as a non-root user bound to localhost; the web host trusts forwarded headers
only from an allow-listed proxy. Since 2.1.008 the web host answers only the loopback host names unless `AllowedHosts`
names others, and sends framing, content-type, referrer and (outside Development, on HTTPS) HSTS headers by default;
`pmu-ingest --live udp://:port` and `interpose` listen on 127.0.0.1 by default, and a listener reachable from other
machines needs a named sender or client, or an explicit opt-out flag (`BugHunt1101WebTests`, `B170_*`, `B179_*`,
`B173_*`, `B174_*`). Defaults are the safe choice, and relaxing them is an explicit configuration act.

---

## 5. Respond to Vulnerabilities (RV)

### 5.1 RV.1 Identify and confirm vulnerabilities on an ongoing basis

**Status: Partially achieved.** Dependabot watches NuGet, Docker and GitHub Actions (monthly, with Dependabot
security updates enabled on the public repository); NuGet audit and the vulnerable-package gate fail the build on a
known-vulnerable dependency; CodeQL scans the C# when run by hand. A security contact is published
(`gdatooling@proton.me`, RFC 9116 `security.txt`). Since 2026-11-01 [`SECURITY.md`](../../SECURITY.md) states the
disclosure and remediation policy: private reporting through GitHub private vulnerability reporting, an objective
CVSS v4.0 and v3.1 base-score assessment recorded in the advisory, a 48-hour remediation target from that assessment
for a base score of 7.0 or above, support for the latest release only, and the advisory channel (a published
repository advisory, a CVE requested through GitHub, and an automatic Discussions notice to Watch subscribers). No
release has yet been made under it.

### 5.2 RV.2 Assess, prioritise and remediate vulnerabilities

**Status: Partially achieved.** Remediation runs through the normal slice discipline (fix, test, CHANGELOG `Security`
bullet, version bump). A consolidated conventional-risk register with likelihood and impact treatment is held in the
companion `risk-register.md`.

### 5.3 RV.3 Analyse vulnerabilities to identify root causes

**Status: Partially achieved.** The constitutional-tests doctrine is a root-cause control: a defect that broke a test
cannot be papered over, it must be fixed with a recorded justification, which forces the root cause into the record.
A general post-incident review template is folded into `incident-response.md`.

---

## 6. Practice-to-evidence matrix

| SSDF practice | GridSim control | Evidence | Status |
|---|---|---|---|
| PO.1 | `UnderstandingRestrictions.md` invariants; `CONTRIBUTING.md` criteria | `RestrictionInvariantTests` | Achieved |
| PO.3 | Declared toolchain, SDK pinned by `global.json` (10.0.401, no roll-forward) since 2.1.008, analysers, CodeQL `security-extended` (by hand), Dependabot, telemetry off, CI / release / publish workflows | `WorkflowContractTests`, `WorkflowReleaseGateTests`, `AvaloniaTelemetryOptOutTests`, `BugHunt1101WebTests` (`B161_*`) | Partially achieved (the release workflow did not build RC-1 and has built no release since; Actions SHA-pinned since 2026-11-01) |
| PO.4 | Warnings-as-errors; NuGet audit as an error; release gated on the full CI matrix (as written); release-build and SBOM-hash check on each CLI binary, SBOM-hash check on each desktop bundle | CI build; `WorkflowReleaseGateTests`; `ComplianceAttestationTests`; `RC1.md` | Partially achieved (the CI gate did not apply to RC-1) |
| PO.5 | Hosted release build gated on CI and provenance attestation, as written; RC-1 built on the owner's machine; reproducible-build check for the linux-x64 CLI written and a gate on the Release job, not yet run; desktop apps in the ship scope as written | `WorkflowReleaseGateTests`; `RC1.md` | Partially achieved |
| PS.2 | Authenticode on the three Windows executables and NuGet signing (self-signed, pinned thumbprint; the workflow signs the same three and the package); SHA256SUMS; provenance attestation written into the workflow but absent for RC-1 | `RC1.md`; `WorkflowReleaseGateTests`; `WorkflowContractTests` | Partially achieved (Linux files rest on checksums alone) |
| PS.3 | RC-1 and RC 1.0 Releases with SHA256SUMS and the CLI SBOM; append-only CHANGELOG | `VersionContractTests` | Partially achieved (no published SBOM for the desktop apps or package, five written into the workflow and not yet run; no attestation; unsigned tags) |
| PW.4 | Minimal declared dependency set; `THIRD-PARTY-NOTICES.md` | `ThirdPartyNoticesTests` | Achieved |
| PW.5 | Nullable, analysers, warnings-as-errors, fail-loud validation | CI build; `MeasurementSet` tests | Achieved |
| PW.6 | Lock files, restored in locked mode on CI and in the image build; SDK pinned; SBOM hash injected and checked; build SDK and source-tree state stamped; release build needs a 64-hex SBOM hash, a known commit and a clean tree (since 2.1.008) | `SupplyChainManifestTests`; `WorkflowReleaseGateTests`; `BugHunt1101WebTests` (`B161_*`, `B176_*`) | Partially achieved (a local restore is not locked; a local build of a clean tree can still set the release-build flag; the reproducible-build check is written and gates the release but has not run) |
| PW.8 | Security-control tests; vulnerable-package gate | the named security tests | Achieved |
| RV.1 | Dependabot; NuGet audit and the vulnerable-package gate; CodeQL (by hand); security contact | CI build; `codeql.yml` | Partially achieved (disclosure policy and support period stated in `SECURITY.md`; CodeQL by hand; policy not yet exercised by a release) |

---

## 7. The one recorded deviation

Continuous integration runs when the release workflow calls it on a release tag, or by manual dispatch; CodeQL runs by
manual dispatch only. Neither runs on every push. This is a deliberate, risk-accepted deviation from the common SSDF
implementation pattern, taken because the CAF and the SSDF are both outcome-based. The outcome is every shipped change
built and checked deterministically, and the compensating control is twofold: the owner's every build for use is a
production build carrying the compliance attestation, checked by the local suite, and CI is run by hand as the witness
of a release (and, in the release workflow, as its gate). A build that is not a release build (since 2.1.008: no
64-hex SBOM hash, an unknown commit, or a dirty or unrecorded source tree) reports `NOT ATTESTED`, so it cannot be
mistaken for a release.

For RC-1 the witness half failed: the release workflow refused the RC tag form then and did not run, and the CI runs
started by hand after publication did not pass. Neither has changed in effect since: the workflow accepts the tag form
from 2.1.008 but has built no release, and no CI run has been made since the 2026-10-31 failures. A reader who expects CI-on-every-push should read this section as a documented
compensating control with a recorded failure at RC-1, not as an omission and not as a met control. The release's
authenticity and integrity evidence (the signatures, `SHA256SUMS.txt`, and the provenance attestation the workflow
would add) is the subject of `slsa-provenance.md` and is cited under CAF v4.0 A4.b.
