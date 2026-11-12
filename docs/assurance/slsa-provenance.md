# SLSA build provenance mapping

**Assurance statement. Status as at GridSim 2.1.010 (reviewed 2026-11-01; RC-1 released 2026-10-31 as 2.1.000).** This document maps GridSim's build and release integrity to
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
  bit-identity contract and falls back to the exact CPU solver, but it is still shipped native code. The Avalonia
  desktop app adds Avalonia, SkiaSharp and HarfBuzzSharp with their native Skia and HarfBuzz libraries. The provenance
  and signing described below are meant to cover the binaries that contain this code; section 2 states what exists
  for RC-1.

So the prize is a signed, verifiable statement of how each released artefact was built, over exactly the surface that
has one.

---

## 2. The provenance record

GridSim has two provenance records, and they do different jobs.

**The signed release provenance (integrity and origin), as written.** The release workflow (`release.yml`, job
`release`) runs `actions/attest-build-provenance` over every file it puts on the GitHub Release (the three CLI archives,
the three desktop archives `gridsim-wpf-<version>-win-x64.zip`, `gridsim-app-<version>-win-x64.zip` and
`gridsim-app-<version>-linux-x64.tar.gz`, the five CycloneDX SBOMs - CLI, web host, GridSim.Core, Avalonia app and WPF
app - the web image's own SBOM (GR-439) and `SHA256SUMS.txt`), and job `web-image-push` attests the pushed web image in the registry. Each
attestation is a SLSA build-provenance statement naming the workflow run and commit, signed through Sigstore with the
workflow's GitHub OIDC identity and stored with the repository; a downloaded file is checked with
`gh attestation verify <file> -R <owner>/GridSim`. Since 2026-11-01 the package workflow (`publish.yml`) also attests
the NuGet package, over the signed bytes it pushes, after signing and before the push (`SlsaSupplyChainPinTests`). Since
2.1.008 the workflow also accepts a release-candidate tag (`vMAJOR.MINOR.PATCH-<label>`): the numeric part must equal
the source `<Version>`, the Release is marked a pre-release, its files pass the same gates and are attested, and no
NuGet package or container image is pushed for a candidate (`BugHunt1101WebTests`, `B195_*`).

**What exists for RC-1.** The workflow did not build RC-1. At RC-1 it accepted only a `vMAJOR.MINOR.PATCH` tag, so it
stopped in its first step on `v2.0.195-rc1.0` and on `v2.1.000-RC-1`; the RC-1 Release (2.1.000, 2026-10-31) was built,
signed and uploaded by hand from the owner's build machine. No release has been made through the workflow since. No RC-1
file has a provenance attestation. What a downloader can check is: the Authenticode signatures on the three Windows
executables and the author signature on the NuGet package (one self-signed certificate, checked by its thumbprint), and
`SHA256SUMS.txt` with the SHA-256 values listed in `RC1.md`. The checksums are uploaded by the same hand as the files
they cover, so for the Linux files, which are not signed, they show only that a download matches what was uploaded.

**The embedded build identity (correlation).** `GridSim.Core.Diagnostics.SupplyChain.BuildProvenance` carries:

- `Version` (the single `<Version>` in `Directory.Build.props`, `2.1.NNN` on the 2.1 line; the RC label is carried
  only in the tag and the release title),
- `Commit` (the commit SHA stamped at build),
- `Toolchain` (the .NET SDK version stamped into the assembly at build time as the `GridSimBuildSdk` metadata, not the
  runtime the binary happens to run on; RC-1 was built with SDK 10.0.401, and since 2.1.008 `global.json` pins that
  SDK with no roll-forward, so every build uses exactly 10.0.401 or refuses to build),
- `SbomSha256` (the SHA-256 of the CycloneDX SBOM, injected as the `GridSimSbomSha256` assembly metadata; `unset` when
  none was given),
- `SourceTree` (since 2.1.008: `clean` or `dirty` from `git status --porcelain` at build time, stamped as the
  `GridSimSourceTree` metadata only when an SBOM hash is given, otherwise `unknown`; not part of the build hash),
- `BuildHash` (a deterministic SHA-256 over `version|commit|toolchain|sbom`),
- `IsReleaseBuild` (since 2.1.008 true only when the SBOM hash is exactly 64 hexadecimal digits, the commit is known
  and the source tree was clean; `NotReleaseReason` names the first condition that failed; `BugHunt1101WebTests`,
  `B176_*`).

In the workflow, the five SBOMs are generated first and the CLI, the two desktop apps, the web image (build argument
`SBOM_SHA256`) and the NuGet package are each built with the hash of their own SBOM (`-p:GridSimSbomSha256=<hash>`;
GridSim.Core carries the hash of the GridSim.Core SBOM); each CLI binary must then report `release build  yes` and
exactly that SBOM hash from `gridsim compliance verify`, each desktop bundle must contain its hash (the job has no
display, so it searches the published file instead of running it), or the release stops. In
the workflow every restore runs in locked mode (`RestoreLockedMode` on GitHub Actions; `--locked-mode` in the image
build), so the SBOM job and the publishing jobs resolve exactly the committed lock files, and the web-image job passes
the tree state into the image build (`SOURCE_TREE`). For RC-1 (2.1.000, before these checks) the owner produced the
CLI SBOM, injected its hash in the local build and checked both CLI builds after signing (`RC1.md`); the desktop apps
and the package have no SBOM of their own. The workflow's per-artefact SBOMs have not been exercised by a release.

The build hash is an **identity label**, not an integrity proof. It is computed from four metadata strings, not from
the bytes of any shipped file, so a modified binary that keeps the same metadata reports the same hash. It correlates
an estimate or an audit entry with the release it came from; it does not prove that a given binary is that release.
Since 2026-11-01 the release workflow has a reproducible-build check (job `reproducible`, B7): it rebuilds the
linux-x64 CLI on a fresh runner from a fresh checkout with the cli job's exact command and SBOM hash, and compares
every file of the shipped archive with the rebuild by SHA-256, failing on any difference (`SlsaSupplyChainPinTests`).
It covers the Linux CLI only (the Windows binary is signed after the build, with a timestamp in the signature, and
the macOS build runs on a separate image) and it has never run because no release has been made through the workflow.
It is one of the jobs the Release job waits for (`needs: [ verify, ci, cli, web-image, sbom, reproducible, desktop ]`,
pinned by `WorkflowReleaseGateTests`), so a non-reproducible Linux CLI stops the release. The determinism tests compare numerical results, not assembly bytes.

`ComplianceManifest` (schema tag `gridsim-compliance/1`) lists each in-scope control with its principle, description,
evidencing test and status (maintained by hand in source), and carries its own `Hash`. `ComplianceAttestation` binds
the manifest hash to the build identity and reports a verdict, `IsCompliant`, which needs both a release build and
every in-scope control recorded Achieved; a build that is not a release build reports `NOT ATTESTED` and
`gridsim compliance verify` exits 1. At this release five of the 18 in-scope controls are recorded Partially achieved
(`caf-mapping.md` section 9), including, since 2.1.008, `supply-chain` and `secure-sdlc-attestation`, whose
descriptions name the gap this document records: no published release has yet been made through `release.yml`. A
release build therefore reads `NOT COMPLIANT`. A running binary reports all of this, with the source-tree state and
the reason a build is not a release build, through `gridsim compliance verify` and the web `/compliance` endpoint.

---

## 3. SLSA Build track, level by level

### 3.1 Build L1, provenance exists and describes the build

**Status: Partially achieved for RC-1.** The running binary records its version, commit and build SDK
(`BuildProvenance`), the CLI SBOM describes what is in the CLI, and `SHA256SUMS.txt` fixes the artefact digests. No
provenance statement in the SLSA format exists for any RC-1 file, the published SBOM covers the CLI only, and how each
file was built is recorded only in `RC1.md`. The workflow, had it run, would produce L1 provenance for the Release
files, including the desktop archives, with five SBOMs.

### 3.2 Build L2, signed provenance from a hosted build platform

**Status: Not achieved for RC-1** (RC-1 was built on the owner's machine, not a hosted build platform, and carries no
signed provenance). Written into the workflow, not exercised:

- Hosted build platform: GitHub Actions (`release.yml`), on hosted runners.
- Build-platform-signed provenance: `actions/attest-build-provenance` signs a SLSA provenance statement for every
  Release file (CLI and desktop archives, the five SBOMs, `SHA256SUMS.txt`) and the pushed image with the workflow's OIDC identity through Sigstore (jobs `release` and
  `web-image-push`, permissions `id-token: write` and `attestations: write`). Pinned by `WorkflowReleaseGateTests`.
- Release gate: the release calls the CI workflow as a reusable workflow and every publishing job needs it; the Release
  job also waits for the reproducible-build check and the desktop job.

Signed artefacts exist for RC-1 independently of the platform: the three Windows executables (`gridsim.exe`,
`GridSim.Wpf.exe`, `GridSim.App.Desktop.exe`) carry an Authenticode signature and the NuGet package an author
signature, each RFC 3161 timestamped, from the self-signed certificate `signing/gridsim-signing.cer`. A consumer's
machine does not trust it unless the certificate is imported; the check is that the signer thumbprint matches. The
Linux binaries, `SHA256SUMS.txt` and the SBOM are not signed. The workflow signs the Windows CLI, the Windows WPF and
Avalonia executables (job `desktop`, the same signing step as the CLI) and the NuGet package, and no Linux or macOS
file. Its signing reads repository secrets that are not set, so a workflow-built release would be unsigned, with a warning, unless the repository variable
`GRIDSIM_ALLOW_UNSIGNED_RELEASE` is set to `false` (then it fails in its first job).

Qualifiers: since 2026-11-01 every GitHub Action in every workflow is pinned to a full commit SHA (the release tag
it was taken from is in a comment), so a moved tag cannot change a build step; the CycloneDX SBOM tool is installed at
a named version. The embedded build hash is not part of the integrity argument (section 2).

### 3.3 Build L3, hardened builds with unforgeable provenance

**Status: Not achieved (recorded residual).** Strong build isolation and provenance that is unforgeable even by the
tenant's own workflow steps are not claimed. The reproducible-build check written into the workflow (section 2)
would serve as a partial substitute for the Linux CLI once a release runs through it. Recorded as a residual, not
asserted.

---

## 4. Level-to-evidence matrix

| SLSA Build requirement | GridSim control | Evidence | Status |
|---|---|---|---|
| L1 provenance exists | `BuildProvenance` (version, commit, build SDK, SBOM hash, build hash) in the binary; CLI SBOM; `SHA256SUMS.txt`; in the workflow, five SBOMs each stamped into its artefact and a signed attestation of every Release file | `SupplyChainManifestTests`, `WorkflowReleaseGateTests`; `RC1.md` | Partially achieved (no SLSA-format provenance for RC-1; published SBOM for the CLI only) |
| L1 provenance distributed | SBOM and SHA256SUMS on the Release; `gridsim compliance verify`; in the workflow, attestations stored with the repository | `WorkflowContractTests`, `ComplianceAttestationTests`; `RC1.md` | Partially achieved |
| L2 hosted build platform | GitHub Actions release build, gated on the full CI matrix (every Action pinned to a commit SHA since 2026-11-01); SDK pinned by `global.json`, locked-mode restore | `release.yml`, `WorkflowReleaseGateTests`, `BugHunt1101WebTests` (`B161_*`, `B195_*`) | Not achieved for RC-1 (the workflow refused the RC tag form then; RC-1 was built on the owner's machine; the tag form is accepted since 2.1.008, but no release has been made through it) |
| L2 build-platform-signed provenance | `actions/attest-build-provenance` (Sigstore, OIDC identity) over the Release files and the image | `WorkflowReleaseGateTests` | Not achieved for RC-1 (no attestation exists) |
| L2 signed artefacts | Authenticode on the three Windows executables and the NuGet author signature, RFC 3161 timestamp, self-signed certificate checked by thumbprint (the workflow signs the same three executables and the package) | `RC1.md`; `WorkflowContractTests`, `WorkflowReleaseGateTests` | Partially achieved (Windows executables and package signed; Linux files, checksums and SBOM unsigned; self-signed certificate, an accepted residual) |
| L3 hardened, isolated, unforgeable | none claimed | none | Not achieved (residual) |

---

## 5. Honest residuals

1. **No release has been built by the workflow.** At RC-1 it refused the RC tag form (accepted since 2.1.008). Since
   2026-11-01 (GA-5, an owner decision) its ship scope matches what RC-1 shipped for the desktop apps (WPF for win-x64,
   Avalonia for win-x64 and linux-x64, each with its own SBOM and the same signing step as the CLI); it still differs
   from RC-1 in the CLI (it builds osx-arm64 as well and names the archives `gridsim-<version>-<rid>`, where RC-1 used
   `gridsim-cli-<version>-<rid>` with no macOS CLI), in the NuGet package (pushed to the feed, not attached to the
   Release) and in carrying five SBOMs where RC-1 has one. The signing, SBOM-injection, attestation and CI-gate steps are written
   and pinned by tests that read the workflow file, and have never executed for a published file (`caf-mapping.md`
   section 9, item 10).
2. **RC-1 was built on the owner's development machine**, so the build environment is not separated from development
   and is recorded only in `RC1.md` (SDK 10.0.401, Windows 11 build 26300).
3. **"Release build" proves form and a clean tree, not origin (narrowed at 2.1.008).** A release build now needs a
   64-hex `GridSimSbomSha256`, a known commit and a clean source tree, recorded in the build. The check does not prove
   that the hash is that of a real SBOM or that the workflow built the binary, so a local build of a clean, committed
   tree given any 64-hex value still reports `release build  yes`; only the provenance attestation (residual 1) ties a
   file to the workflow run. A local restore is not in locked mode (CI and the image build are).
4. **No published NuGet package carries a provenance attestation**, and no web image was published at RC-1. Since
   2026-11-01 `publish.yml` attests the package it pushes (after signing, before the push), so the next package
   published through the workflow carries one; none has been published through it yet.
5. **The signing certificate is self-signed: accepted residual (owner decision, 2026-11-01).** A consumer's machine
   does not trust the Authenticode or NuGet signature unless the published certificate
   (`signing/gridsim-signing.cer`, thumbprint `86DD43B731D2B8A5133972F7B7C91C0FDA8EB721`) is imported; the Linux
   files are not signed. The owner has accepted this: a CA-issued code-signing certificate is not planned, and the
   published certificate with its pinned thumbprint is the trust anchor a consumer checks. It is recorded as an
   accepted residual, not an open gap; the Linux files remain covered only by the checksums.
6. **GitHub Actions were referenced by version tag, not commit SHA - closed 2026-11-01.** Every `uses:` line in every
   workflow now names a full commit SHA with its release tag in a comment; Dependabot's github-actions ecosystem
   proposes SHA bumps. Recorded here so the numbering of the residuals stays stable.
7. **Reproducible-build check written, not yet exercised (narrowed 2026-11-01).** The build hash is an identity
   label, not an integrity proof. The workflow's `reproducible` job rebuilds the linux-x64 CLI independently and
   compares it byte for byte with the shipped archive; it covers that one archive and has never run (no release through
   the workflow). Since 2026-11-01 it gates the Release job: `reproducible` is in its `needs` list, which a test pins, so
   a non-reproducible Linux CLI stops the release.
8. **No SLSA L3 build isolation.** Recorded as a residual.

The guiding rule is the same as the rest of the assurance set: a control is marked Achieved only where an artefact or
a passing test evidences it, and a substitute is named as a substitute, never dressed up as the thing it stands in
for.
