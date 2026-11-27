# Cyber Essentials note

This is a short self-assessed note mapping GridSim to the
five technical controls of the NCSC Cyber Essentials scheme. It is deliberately brief, because Cyber Essentials is a
scheme about an organisation's operational IT environment (the boundary firewalls, the user accounts, the patched
estate), not about a software component. Most of its five controls are therefore the deploying party's
responsibility, the same boundary that `caf-mapping.md` section 1.3 draws for the Cyber Assessment Framework. This
note states the engine-side contribution to each control and marks the rest deploying-party, so the boundary is
explicit rather than silently assumed.

Status vocabulary follows the rest of the set: Achieved, Partially achieved, Not achieved, Out of scope
(deploying-party).

---

## 1. Firewalls

**Status: Out of scope (deploying-party), with one engine-side contribution.** Boundary and host firewalls are the
deploying party's. The engine-side contribution is that the services minimise what must be exposed at all: `serve`
binds no network port, `GridBridgeClient` is an outbound client, and the container binds its port to localhost with
TLS terminated at the reverse proxy. There is less to firewall by design.

## 2. Secure configuration

**Status: Partially achieved (engine-side).** GridSim ships secure-by-default settings: the production configuration
runs replay-only (`GridSim:ReplayOnly`), which leaves the solve surface unmapped; the container runs as a non-root user;
the web host trusts forwarded headers only from an allow-listed proxy (`TrustedProxies`). The documented
secure-configuration baseline lives in `docs/developer/deploy/` and technical chapter 35. Since 2.1.008 the web host
answers only the host names in `AllowedHosts` (default the loopback names), sends `Content-Security-Policy:
frame-ancestors 'none'`, `X-Frame-Options`, `X-Content-Type-Options` and `Referrer-Policy` on every answer and HSTS on
HTTPS outside Development, and `pmu-ingest` UDP and `interpose` listen on loopback by default. The
default Content-Security-Policy covered framing until 2.1.140; since then it restricts scripts, styles, images, fonts and
connections to the app's own origin (styles keep `'unsafe-inline'` for inline style attributes). A `script-src 'self'`
policy is also available as an option (`GridSim:ScriptCsp`, `report-only` or `enforce`), and the nginx example adds no
headers of its own. `compose.yml` runs the web container
with a read-only root file system, no Linux capabilities and `no-new-privileges`. Host
OS hardening remains deploying-party.

## 3. User access control

**Status: Partially achieved (engine-side), maturing under the assurance effort.** The web host authenticates
(cookie sign-in and a single sign-on consumer), and the production configuration's replay-only gate leaves the solve
surface unmapped. A role-based access-control gate (`OperationRole`, `IdentityContext`, `RoleGuard`) is built and tested, and the web host
enforces it on `POST /api/solve`, every `/ws/sim` control frame and the dashboard's live sessions and actions, writing each denial to its audit log when `Audit:LogPath` is set. It also limits each client
to 600 requests a minute on `/api` and `/console` by default. Account provisioning, directory services and privileged-account management at the
organisation level are deploying-party.

## 4. Malware protection

**Status: Out of scope (deploying-party), with one engine-side contribution.** Endpoint malware protection is the
deploying party's. The engine-side contribution is release integrity. At RC-1 the three Windows executables carry
an Authenticode signature and the NuGet package an author signature, from a self-signed certificate whose thumbprint a
deployer checks (or which a deployer imports to validate the signature), and `SHA256SUMS.txt` and the SHA-256 values
in `RC1.md` are published. The Linux files are not signed and rest on the checksums alone, and no RC-1 file carries the
build-provenance attestation the release workflow adds; RC-3 and RC-4 were built and published by that workflow
(see `slsa-provenance.md`). The signatures let a
Windows deployer confirm a download is the signed file before trusting it, which reduces the chance of running a
tampered or substituted binary. The embedded build identity (`BuildProvenance` / `ComplianceAttestation`) names the build but is
not an integrity check: it covers no file bytes.

## 5. Security update management

**Status: Partially achieved (engine-side).** Dependency vulnerabilities are watched by Dependabot alerts (dependency updates are made by hand) and a known-vulnerable
dependency fails the build (NuGet audit on every build, and the vulnerable-package gate in CI). Each fix follows the
slice discipline (fix, test, CHANGELOG `Security` bullet, version bump), so the update trail is in the record. The
builds are self-contained and carry the .NET 10 runtime, so a .NET security update reaches a deployer only through a
new GridSim build. `SECURITY.md` (2026-11-01) states the support period (the latest release), a 48-hour remediation
target for a vulnerability objectively assessed at a CVSS base score of 7.0 or above, and the update notice (a
published advisory and an automatic Discussions announcement to Watch subscribers). Patching of the host operating
system is deploying-party.

---

## 6. Where this could go next

This note is a self-assessment, consistent with the assurance set's chosen posture. If independent validation is ever
wanted, **Cyber Essentials Plus** (the externally assessed tier) is the natural candidate, because it is lightweight,
UK-focused and sits cleanly beneath the Cyber Assessment Framework work already done. The first public release
candidate shipped on 2026-10-31; it would be pursued, if at all, once the software is deployed in a real environment,
since several of its controls are exercised only there. It is recorded here as a candidate for external assessment, not as work in progress.
