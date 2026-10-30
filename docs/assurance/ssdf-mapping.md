# NIST Secure Software Development Framework (SSDF) mapping

**Assurance statement. Status as at GridSim 1.95.296.** This document maps GridSim's development and release
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
manual dispatch only. This is a conscious, risk-accepted choice, not a gap. The SSDF asks for the outcome (every
change is built and checked deterministically), and for what ships that outcome is met at the release: `release.yml`
calls `ci.yml` as a reusable workflow (job `ci`) and every publishing job needs it, so a release exists only after the
full matrix passed (Linux and Windows in the normal, AVX2-off, hardware-intrinsics-off, forced AVX-512 and FMA-off
modes, and macOS on Apple silicon in the normal and hardware-intrinsics-off modes; no leg is advisory). Between
releases, day-to-day verification is the local batch run. A local build is never a release build: it carries no SBOM
hash and its compliance attestation reads `NOT ATTESTED`. This is the compensating control, and it is stated again
under PO.3 and PW.6.

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

**Status: Achieved.** The toolchain is fixed and declared: a single `net10.0` target, a single `<Version>` in
`Directory.Build.props`, .NET analysers at `AnalysisMode=Recommended` with `TreatWarningsAsErrors=true`, CodeQL
(`codeql.yml`, run by hand), Dependabot over NuGet, Docker and GitHub Actions, and the CI, release and publish
workflows (`ci.yml`, `release.yml`, `publish.yml`), pinned by `WorkflowContractTests` and `WorkflowReleaseGateTests`.
GitHub Actions are referenced by version tag, not pinned to a commit SHA. The CI-cadence deviation in section 1
applies here and is compensated by the release gate on the full CI matrix.

### 2.4 PO.4 Define and use criteria for software security checks

**Status: Achieved.** The criteria are mechanical and non-negotiable: a nullable or analyser warning is a build
failure; a NuGet audit warning is a build failure; and the full test suite must be green on every leg of the CI matrix
(every OS and SIMD mode) before anything is built for shipping, because every publishing job of the release needs the
CI job. The compliance verdict is not itself a release gate: the release does not stop when a control is recorded
Partially achieved, the shipped binary reports `NOT COMPLIANT` and lists it instead. What the release does check is
that each CLI binary reports `release build  yes` with the SBOM hash it was built against (section 4.4). A status is
upgraded only when the build is warning-free and the evidencing tests pass.

### 2.5 PO.5 Implement and maintain secure development environments

**Status: Out of scope (deploying-party) for the developer workstation; Partially achieved for the build
environment.** Release builds run on GitHub-hosted runners, gated on the full CI matrix, and every Release file gets
a signed build-provenance attestation naming the workflow run and commit, so a download's origin can be checked
without trusting the machine it was fetched from. There is no reproducible-build check: no step rebuilds and compares
assembly bytes (the determinism tests cited in `nasa-std-7009-credibility.md` compare numerical results, not
binaries), and GitHub Actions are referenced by mutable version tags.

---

## 3. Protect the Software (PS)

### 3.1 PS.1 Protect all forms of code from unauthorised access and tampering

**Status: Partially achieved.** The source is held in a single `main` branch with no alternative branches carried.
The licensing path embeds only an ECDSA P-256 public key, so a binary can verify a licence but cannot mint one, and
the release signing private key is held in repository secrets only (the `signing/` folder holds the public
certificate). Repository access control itself is a platform concern (deploying-party / GitHub account).

### 3.2 PS.2 Provide a mechanism for verifying software release integrity

**Status: Achieved for the GitHub Release files.** The integrity mechanisms are:

- A signed SLSA build-provenance attestation (`actions/attest-build-provenance`, Sigstore) over every file on the
  GitHub Release (the CLI archives, the SBOMs and `SHA256SUMS.txt`), checked with
  `gh attestation verify <file> -R <owner>/GridSim`.
- `SHA256SUMS.txt` is produced over every release asset and attached to the GitHub Release.
- The Windows CLI binary is Authenticode-signed and the `GridSim.Core` NuGet package is signed with the same
  certificate (`release.yml`, `publish.yml`), each with an RFC-3161 timestamp. The certificate is self-signed
  (`signing/README.md`); the release's verify steps trust exactly that certificate, pinned by its thumbprint, and a
  consumer must import the published certificate to validate the signature. The Linux and macOS archives,
  `SHA256SUMS.txt` and the container image are not signed, and a release built without the signing secret is
  unsigned (with a warning).

The embedded `BuildProvenance` record (version, full commit SHA, build SDK, SBOM SHA-256 and a deterministic
`BuildHash` over them) and `ComplianceAttestation` are reported by `gridsim compliance verify`. They identify a build;
they do not verify its integrity, because the build hash covers no artefact bytes. The NuGet package and the
container image carry no provenance attestation. The provenance and attestation mapping is detailed in
`slsa-provenance.md`.

### 3.3 PS.3 Archive and protect each software release

**Status: Partially achieved.** Each tagged release carries its CLI archives (the Windows binary signed when the
signing secret is configured), SHA256SUMS, per-application CycloneDX SBOMs and their provenance attestations on the
GitHub Release. The CHANGELOG is an append-only, per-version record (`1.95.NNN`), enforced by
`VersionContractTests`. Residual: no tagged release has yet exercised the workflow on GitHub, so the archive is a
written control until the first release runs (a flagged owner action, tracked in `caf-mapping.md` gap register).

---

## 4. Produce Well-Secured Software (PW)

### 4.1 PW.1 / PW.2 Design to meet security requirements; review the design

**Status: Achieved for the load-bearing design.** The design decisions that carry the security case, the
forward-inference boundary, the fail-loud status doctrine (no NaN reported as a plausible state), the physics
acceptance gate and the independent FDIA screen, are documented in `caf-mapping.md` sections 3 to 6 and reviewed
through the restriction-review discipline.

### 4.2 PW.4 Reuse existing, well-secured software

**Status: Achieved.** `GridSim.Core` deliberately depends on no third-party package; it uses `System.Numerics`,
`System.Security.Cryptography` and `System.Text.Json` from the base class library. The shipped product adds only
`Parquet.Net` (data lake) and `ILGPU` with native ROCm/HIP interop (the optional GPU backend, outside the
bit-identity contract and falling back to the exact CPU solver). Every shipped package is named with its licence in
`THIRD-PARTY-NOTICES.md` and pinned by `ThirdPartyNoticesTests`. The small, declared dependency set is the point:
the reuse that exists is well-secured and minimal.

### 4.3 PW.5 Create source code adhering to secure coding practices

**Status: Achieved.** `Nullable=enable`, the full analyser set and warnings-as-errors make an entire class of
null-dereference and disposal defects impossible to ship. Input validation is fail-loud (`MeasurementSet.Validate`
rejects malformed rows with `InvalidDataException` rather than producing a quietly wrong state).

### 4.4 PW.6 Configure the build process to improve executable security

**Status: Achieved.** `ContinuousIntegrationBuild` normalises paths; `packages.lock.json` per project (via
`RestorePackagesWithLockFile`) pins the restore graph so the SBOM is stable; the release generates the SBOMs first and
injects their SHA-256 as assembly metadata (`-p:GridSimSbomSha256=<hash>`) into the CLI, the web image and the NuGet
package, and the .NET SDK version is stamped as `GridSimBuildSdk`, both folded into the build hash (an identity
label); each CLI binary must report `release build  yes` and exactly that SBOM hash, or the release stops. There is no
reproducible-build check. The CI-cadence deviation of section 1 is the one qualifier, compensated by the release gate
on the full CI matrix.

### 4.5 PW.7 Review and analyse human-readable code

**Status: Achieved (automated) / Partially achieved (manual).** CodeQL runs the C# security-and-quality query pack
when started by hand (`codeql.yml`, manual dispatch only; it is not part of the release gate); the analyser set runs
on every build. The structured manual security assessment of the October 2026 programme is kept in local records that
are excluded from version control, so it is evidenced off-repository: no clone or release contains it, and an
assessor must ask the owner for it. A standing cadence is named in `security-governance.md`.

### 4.6 PW.8 Test executable code to identify vulnerabilities

**Status: Achieved.** The suite exercises the security controls directly: `ForwardInferenceGuardTests`,
`RestrictionInvariantTests`, the `AuditLog*` tamper-evidence tests, `FdiaScreenTests`, `FdiaCloakedAttackTests`, the
web security tests and the determinism tests. A vulnerable-package gate (`dotnet list package --vulnerable
--include-transitive`) runs in CI and in the release. The breadth of the suite is inventoried in
`nasa-std-7009-credibility.md`.

### 4.7 PW.9 Configure software to have secure settings by default

**Status: Achieved.** The production configuration runs replay-only (`GridSim:ReplayOnly`), which leaves the solve
surface unmapped; the container runs as a non-root user bound to localhost; the web host trusts forwarded headers
only from an allow-listed proxy. Defaults are the safe choice, and relaxing them is an explicit configuration act.

---

## 5. Respond to Vulnerabilities (RV)

### 5.1 RV.1 Identify and confirm vulnerabilities on an ongoing basis

**Status: Achieved.** Dependabot watches NuGet, Docker and GitHub Actions; the vulnerable-package gate fails the
build on a known-vulnerable dependency; CodeQL scans the C# when run by hand. Because the dependency set is tiny and declared, the
surface to watch is small by construction.

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
| PO.3 | Declared toolchain, analysers, CodeQL (by hand), Dependabot, CI / release / publish workflows | `WorkflowContractTests`, `WorkflowReleaseGateTests` | Achieved |
| PO.4 | Warnings-as-errors; release gated on the full CI matrix; release-build and SBOM-hash check on each CLI binary | CI build; `WorkflowReleaseGateTests`; `ComplianceAttestationTests` | Achieved |
| PO.5 | Hosted release build gated on CI; provenance attestation; no reproducible-build check | `WorkflowReleaseGateTests` | Partially achieved |
| PS.2 | Signed provenance attestation over every Release file; SHA256SUMS; Authenticode + NuGet signing (self-signed, pinned thumbprint) | `WorkflowReleaseGateTests`; `WorkflowContractTests` | Achieved (Release files; NuGet package and image not attested) |
| PS.3 | Release assets with provenance attestations, SBOMs, append-only CHANGELOG | `VersionContractTests` | Partially achieved (no tagged release yet) |
| PW.4 | Minimal declared dependency set; `THIRD-PARTY-NOTICES.md` | `ThirdPartyNoticesTests` | Achieved |
| PW.5 | Nullable, analysers, warnings-as-errors, fail-loud validation | CI build; `MeasurementSet` tests | Achieved |
| PW.6 | Lock files; SBOM hash injected into every shipped build and checked; build SDK stamped | `SupplyChainManifestTests`; `WorkflowReleaseGateTests` | Achieved |
| PW.8 | Security-control tests; vulnerable-package gate | the named security tests | Achieved |
| RV.1 | Dependabot; vulnerable-package gate; CodeQL (by hand) | CI build; `codeql.yml` | Achieved |

---

## 7. The one recorded deviation

Continuous integration runs when the release workflow calls it on a release tag, or by manual dispatch; CodeQL runs by
manual dispatch only. Neither runs on every push. This is a deliberate, risk-accepted deviation from the common SSDF
implementation pattern, taken because the CAF and the SSDF are both outcome-based and the outcome, every shipped
change built and checked deterministically, is met at the release: every publishing job of `release.yml` needs the
full CI matrix (job `ci`), so nothing ships unless every OS and SIMD-mode leg passed. Between releases, day-to-day
verification is the local batch run; a local build carries no SBOM hash and its attestation reads `NOT ATTESTED`, so
it can never be mistaken for a release. A reader who expects CI-on-every-push should read this as a documented
compensating control, not an omission. The release's authenticity and integrity evidence (the signed provenance
attestation, `SHA256SUMS.txt` and the signatures) is the subject of `slsa-provenance.md` and is cited under CAF v4.0
A4.b.
