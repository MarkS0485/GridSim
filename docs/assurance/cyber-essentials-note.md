# Cyber Essentials note

**Assurance statement. Status as at GridSim 1.95.296.** This is a short self-assessed note mapping GridSim to the
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
runs replay-only (`GridSim:ReplayOnly`), which leaves the solve surface unmapped; the container runs as a non-root
user; the web host trusts forwarded headers only from an allow-listed proxy (`TrustedProxies`). The documented
secure-configuration baseline lives in `docs/developer/deploy/` and technical chapter 35. Host OS hardening remains
deploying-party.

## 3. User access control

**Status: Partially achieved (engine-side), maturing under the assurance effort.** The web host authenticates
(cookie sign-in and a single sign-on consumer), and the production configuration's replay-only gate leaves the solve
surface unmapped. A role-based access-control gate (`OperationRole`, `IdentityContext`, `RoleGuard`, with an audited
overload that records each denial) is built and tested, but no hosting surface enforces it yet, so no request is
authorised by role and no denial is written to the audit trail at this release. Account provisioning, directory services and privileged-account management at the
organisation level are deploying-party.

## 4. Malware protection

**Status: Out of scope (deploying-party), with one engine-side contribution.** Endpoint malware protection is the
deploying party's. The engine-side contribution is release integrity: every file on the GitHub Release carries a
signed build-provenance attestation (`gh attestation verify <file> -R <owner>/GridSim`), SHA256SUMS is published,
and, when the signing secret is configured, the Windows CLI is Authenticode-signed and the NuGet package is signed
(with a self-signed certificate a deployer must import to validate; see `slsa-provenance.md`). These let a deployer
confirm a download is the released file before trusting it, which reduces the chance of running a tampered or
substituted binary. The embedded build identity (`BuildProvenance` / `ComplianceAttestation`) names the build but is
not an integrity check: it covers no file bytes.

## 5. Security update management

**Status: Partially achieved (engine-side).** Dependency updates are watched by Dependabot and a known-vulnerable
dependency fails the build (the vulnerable-package gate). Each fix follows the slice discipline (fix, test, CHANGELOG
`Security` bullet, version bump), so the update trail is in the record. Patching of the host operating system and
runtime is deploying-party.

---

## 6. Where this could go next

This note is a self-assessment, consistent with the assurance set's chosen posture. If independent validation is ever
wanted, **Cyber Essentials Plus** (the externally assessed tier) is the natural candidate, because it is lightweight,
UK-focused and sits cleanly beneath the Cyber Assessment Framework work already done. It would be pursued, if at all,
after the first public release, since several of its controls are exercised only once the software is deployed in a
real environment. It is recorded here as a candidate for external assessment, not as work in progress.
