# SLSA build provenance mapping

**Assurance statement. Status as at GridSim 1.95.296.** This document maps GridSim's build and release integrity to
the SLSA Build track (SLSA is `Supply-chain Levels for Software Artifacts`, version 1.0). It is the evidence behind the
authenticity-and-integrity obligation of CAF v4.0 principle A4.b, and a companion to `ssdf-mapping.md` (which maps
the development lifecycle) and `caf-mapping.md` (which maps the running system). Scope, status vocabulary and the
evidence bar are inherited from `caf-mapping.md`.

---

## 1. What provenance is for here, and what it is not

A common first reaction is that GridSim has nothing for a supply-chain regime to protect, because the core has no
third-party dependencies. That is the wrong frame. The SLSA Build track is not about dependencies; it is about build
integrity and provenance, the question of whether a shipped artefact is provably the one built from this exact source
by this builder, tamper-evident and reproducible. Dependency hygiene is a separate concern and is covered under
`ssdf-mapping.md` (PW.4, RV.1).

Two facts set the scope precisely:

- **The numerical core is dependency-free, and that is a stated strength.** `GridSim.Core` depends on no third-party
  package. Its effective software bill of materials is one line, the .NET runtime. That is a minimal-attack-surface
  property most projects cannot claim, and the provenance record states it rather than hiding it.
- **The shipped product does carry a real third-party and native surface, and that is what provenance covers.** The
  data lake uses `Parquet.Net`; the optional GPU backend uses `ILGPU` and native interop into the AMD ROCm/HIP stack
  (`amdhip64`, `hipsolver`, `rocsparse`, `rocblas`) with a CUDA driver probe. The GPU backend is outside the
  bit-identity contract and falls back to the exact CPU solver, but it is still shipped native code, and the
  provenance and signing described below cover the binaries that contain it.

So the prize is a signed, verifiable statement of how each released artefact was built, over exactly the surface that
has one.

---

## 2. The provenance record

GridSim has two provenance records, and they do different jobs.

**The signed release provenance (integrity and origin).** The release workflow (`release.yml`, job `release`) runs
`actions/attest-build-provenance` over every file on the GitHub Release: the three CLI archives, the two CycloneDX
SBOMs and `SHA256SUMS.txt`. Each attestation is a SLSA build-provenance statement naming the workflow run and commit,
signed through Sigstore with the workflow's GitHub OIDC identity and stored with the repository. A downloaded file is
checked with `gh attestation verify <file> -R <owner>/GridSim`. This, together with `SHA256SUMS.txt` and the
Authenticode signature on the Windows binary, is what proves a download is the file the release built. The NuGet
package and the container image are not covered by this attestation step (the package is signed, section 3.2; the
image is neither signed nor attested).

**The embedded build identity (correlation).** `GridSim.Core.Diagnostics.SupplyChain.BuildProvenance` carries:

- `Version` (the single `1.95.NNN` from `Directory.Build.props`),
- `Commit` (the full commit SHA stamped at build),
- `Toolchain` (the .NET SDK version stamped into the assembly at build time as the `GridSimBuildSdk` metadata, not the
  runtime the binary happens to run on),
- `SbomSha256` (the SHA-256 of the release CycloneDX SBOM, injected at release as the `GridSimSbomSha256` assembly
  metadata; `unset` for a local build),
- `BuildHash` (a deterministic SHA-256 over `version|commit|toolchain|sbom`),
- `IsReleaseBuild` (true when an SBOM hash was injected).

The release generates the SBOMs first and builds the CLI, the web image (build argument `SBOM_SHA256`) and the NuGet
package (GridSim.Core carries the CLI SBOM's hash) with `-p:GridSimSbomSha256=<hash>`; each CLI binary must then report
`release build  yes` and exactly that SBOM hash from `gridsim compliance verify`, or the release stops.

The build hash is an **identity label**, not an integrity proof. It is computed from four metadata strings, not from
the bytes of any shipped file, so a modified binary that keeps the same metadata reports the same hash. It correlates
an estimate or an audit entry with the release it came from; it does not prove that a given binary is that release.
There is no reproducible-build check: the determinism tests compare numerical results, not assembly bytes, so
"rebuild and compare" is not a provided or evidenced verification route.

`ComplianceManifest` (schema tag `gridsim-compliance/1`) lists each in-scope control with its principle, description,
evidencing test and status (maintained by hand in source), and carries its own `Hash`. `ComplianceAttestation` binds
the manifest hash to the build identity and reports a verdict, `IsCompliant`, which needs both a release build and
every in-scope control recorded Achieved; a local build reports `NOT ATTESTED` and `gridsim compliance verify` exits 1.
At this release three controls are recorded Partially achieved (`caf-mapping.md` section 9), so a release build reads
`NOT COMPLIANT`. A running binary reports all of this through `gridsim compliance verify` and the web `/compliance`
endpoint.

---

## 3. SLSA Build track, level by level

### 3.1 Build L1, provenance exists and describes the build

**Status: Achieved.** The signed build-provenance attestation records which workflow run built each Release file from
which commit; `BuildProvenance` records the version, commit and build SDK in the running binary; the per-application
CycloneDX SBOMs describe what is in it; `SHA256SUMS.txt` fixes the artefact digests. The attestations are stored with
the repository and retrieved by `gh attestation verify`; the build identity is also queryable from the running
binary.

### 3.2 Build L2, signed provenance from a hosted build platform

**Status: Achieved for the files on the GitHub Release; the NuGet package and the container image are not covered by
the provenance attestation.**

- Hosted build platform: the public release is built by GitHub Actions (`release.yml`), on hosted runners.
- Build-platform-signed provenance: `actions/attest-build-provenance` signs a SLSA provenance statement for every
  Release file with the workflow's OIDC identity through Sigstore (job `release`, permissions `id-token: write` and
  `attestations: write`). Pinned by `WorkflowReleaseGateTests`.
- Signed artefacts: the Windows CLI binary is Authenticode-signed and the NuGet package is signed with the same
  certificate, each RFC-3161 timestamped. The certificate is self-signed (`signing/README.md`); the release's verify
  steps trust exactly that certificate, pinned by thumbprint (the public `.cer` is imported into the runner's
  `LocalMachine\Root` for `signtool verify /pa`, and the NuGet verify uses `--certificate-fingerprint`). A consumer's
  machine does not trust it unless the certificate is imported. The Linux and macOS archives, `SHA256SUMS.txt` and
  the container image are not signed; the archives and `SHA256SUMS.txt` are covered by the provenance attestation.
  When the signing secret is not configured, signing is skipped with a warning and the release is unsigned.
  Pinned by `WorkflowContractTests` and `WorkflowReleaseGateTests`.
- Release gate: the release calls the CI workflow as a reusable workflow and every publishing job needs it, so
  nothing is built for shipping until the full test matrix has passed on every OS and SIMD mode.

Qualifiers: GitHub Actions are referenced by mutable version tags, not pinned to commit SHAs, so the build steps
themselves are trusted by tag. The embedded build hash is not part of the integrity argument (section 2).

### 3.3 Build L3, hardened builds with unforgeable provenance

**Status: Not achieved (recorded residual).** Strong build isolation and provenance that is unforgeable even by the
tenant's own workflow steps are not claimed, and there is no reproducible-build check that could serve as a partial
substitute. Recorded as a residual, not asserted.

---

## 4. Level-to-evidence matrix

| SLSA Build requirement | GridSim control | Evidence | Status |
|---|---|---|---|
| L1 provenance exists | Signed build-provenance attestation of every Release file; `BuildProvenance` (version, commit, build SDK, SBOM hash, build hash) in the binary | `WorkflowReleaseGateTests`, `SupplyChainManifestTests` | Achieved |
| L1 provenance distributed | Attestations stored with the repository (`gh attestation verify`); SBOMs + SHA256SUMS on the Release; `gridsim compliance verify` | `WorkflowReleaseGateTests`, `WorkflowContractTests`, `ComplianceAttestationTests` | Achieved |
| L2 hosted build platform | GitHub Actions release build, gated on the full CI matrix (Actions referenced by version tag, not SHA) | `release.yml`, `WorkflowReleaseGateTests` | Achieved (witnessed at first tagged release) |
| L2 build-platform-signed provenance | `actions/attest-build-provenance` (Sigstore, OIDC identity) over the CLI archives, SBOMs and `SHA256SUMS.txt` | `WorkflowReleaseGateTests` | Achieved for Release files (NuGet package and container image not attested) |
| L2 signed artefacts | Authenticode (Windows CLI) + NuGet signing, RFC-3161 timestamp, self-signed certificate trusted by pinned thumbprint; skipped with a warning when the secret is absent | `WorkflowContractTests`, `WorkflowReleaseGateTests` | Partially achieved (two artefacts signed; self-signed certificate) |
| L3 hardened, isolated, unforgeable | none claimed | none | Not achieved (residual) |

---

## 5. Honest residuals

1. **No tagged release has yet run the workflow on GitHub**, so the signing, SBOM and attestation steps are written
   and pinned by tests, and are exercised at the first public release. This is tracked as the flagged owner action in
   `caf-mapping.md`.
2. **The NuGet package and the container image carry no provenance attestation**, and the image is not signed. The
   package is signed (self-signed certificate); the image's identity is its registry tag and the SBOM hash built into
   its assemblies.
3. **The signing certificate is self-signed.** A consumer's machine does not trust the Authenticode or NuGet
   signature unless the published certificate (`signing/gridsim-signing.cer`, thumbprint
   `86DD43B731D2B8A5133972F7B7C91C0FDA8EB721`) is imported; without the signing secret a release is unsigned.
4. **GitHub Actions are referenced by version tag, not commit SHA.**
5. **No reproducible-build check.** The build hash is an identity label, not an integrity proof, and no step rebuilds
   and compares bytes.
6. **No SLSA L3 build isolation.** Recorded as a residual.

The guiding rule is the same as the rest of the assurance set: a control is marked Achieved only where an artefact or
a passing test evidences it, and a substitute is named as a substitute, never dressed up as the thing it stands in
for.
