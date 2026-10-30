# Security governance

**Assurance statement. Status as at GridSim 1.95.296.** This document is the security governance artefact
for the GridSim state estimator and the services that host it, mapping to CAF Objective A1 (Governance). It
names the accountable security owner, states the review cadence, and records the change-control discipline
that governs the security-critical values in the codebase. It is the broader governance artefact that
`caf-mapping.md` section 4.1 cites; that section remains the status of record for the A1
outcome.

GridSim is mapped to the NCSC Cyber Assessment Framework v4.0 (released 6 August 2025) and assessed as a
software component embedded inside a deploying Operator of Essential Services (OES) in the downstream
electricity subsector. The competent authority is Ofgem, working alongside the Department for Energy
Security and Net Zero and the NCSC, and the assessment is read against the Ofgem Downstream Gas and
Electricity (DGE) CAF overlay. The obligation to operate a security governance function lands on the OES;
this document specifies the governance GridSim itself runs, so that the OES can rely on it and evidence it.

Governing constraint: everything here is subordinate to `UnderstandingRestrictions.md` and the single
load-bearing control, the `ForwardInferenceGuard` 169-hour data boundary. Governance exists to keep that
control, and the controls that surround it, from being weakened quietly. See `caf-mapping.md` sections 3
and 8.

---

## 1. Accountable security owner

**Named owner.** Mark Shirley, Chief Technology Officer, Twin Scroll Grid Balancer (TSGB), is the accountable
owner for the security posture of the estimator and its services: for accepting residual risk, for deciding
when a control's status changes, and for the change control of the security-critical values in section 4. The
security and licensing contact of record is `gdatooling@proton.me`. The project also publishes an RFC 9116
`.well-known/security.txt` for vulnerability disclosure.

When GridSim is embedded in an Operator of Essential Services, the OES names its own accountable owner inside
its security governance (CAF A1), and that owner, not the component owner above, carries the Regulation 10
duty for the part of the estate that is GridSim. This document records the owner of the component's own
security decisions; it does not transfer the OES's duty.

**What the owner owns.** The owner is the single point of sign-off for:

- a change to `ForwardInferenceGuard.HorizonHours` or to the ingestion-seam list (section 4.1);
- a new or amended entry in the `RestrictionReview.Reviewed` register (section 4.2);
- a control moving status in the `caf-mapping.md` section 8 traceability matrix (Achieved, Partially
  achieved, Not achieved, Out of scope);
- acceptance of a residual risk in `risk-register.md`, including the feasible-landing FDIA residual.

**Separation from the guard.** The owner's authority is governance, not a bypass. No owner decision can make
the engine process sub-horizon data, because the guard consults no role, reads no configuration flag and has
no privileged override (`caf-mapping.md` section 3.2). The owner can decide to widen the horizon only by a
source change that must still pass `RestrictionInvariantTests`; there is no runtime lever.

---

## 2. Review cadence

The security posture is reviewed on two triggers, so that review is tied to real change rather than to a
calendar alone:

- **Per release.** Every GridSim release (1.95.NNN) is a review point. The `caf-mapping.md` header carries
  the release it describes, and the owner confirms that every section-8 row still reflects the code and the
  tests at that release. A downgrade (a dependency CVE, a persistence bug that breaks an audit guarantee) is
  recorded with the same honesty as an upgrade: the affected row returns to Partially achieved with a gap
  entry, never left overstated.
- **Whenever a control changes status.** Independently of the release cadence, when a control in the
  traceability matrix changes status, the change is reviewed and the matrix row, this document and the
  relevant assurance doc are updated together. A status is upgraded to Achieved only when the test or
  artefact that evidences it is named and green (`caf-mapping.md` section 2.2 and 10.5).

Between triggers the invariant tests (section 4) fail the test run if a pinned security-critical value is
changed. The suite runs locally and in the release gate (CI runs when the release calls it on a tag, or by
hand, not on every push), so such a change is caught at the latest when a release is cut; it is not stopped
at the moment it is pushed.

---

## 3. CHANGELOG security-labelling discipline

The CHANGELOG is a user-facing record kept in the Keep a Changelog form (Added, Changed, Deprecated,
Removed, Fixed, Security). A security-relevant change is flagged under the **Security** heading of its
release entry, so that an OES tracking GridSim can read its own security-relevant delta from one place
without reading the commit history.

A change is security-relevant, and therefore belongs under Security, when it:

- changes `ForwardInferenceGuard` behaviour, the horizon value, or the set of dated ingestion seams;
- changes the audit trail, its persistence, its anchoring, or what is recorded per event;
- changes authentication, the replay-only gate, or role-based access control (built, not yet enforced by any host);
- changes secrets handling, key-ring protection, or the dependency surface in a way that bears on
  vulnerability exposure;
- adds, removes or changes a detection control (`FdiaScreen`, the physics-conformance audit) or an incident
  or quarantine control.

The discipline is deliberately narrow in wording: the Security bullet says what changed for the user, in
plain impersonal terms, and carries no people, no review identifiers, no branch or test-run narrative and no
`review-progress/` paths. Those belong in the commit message and the local review records, not in the
user-facing log. A test change that evidences a security control is recorded as a brief "Tests: ..." bullet.

---

## 4. Change control of security-critical values

Three values in the codebase are security-critical: changing them changes the attack surface or the blast
radius of a compromise. Each is governed, not merely documented, so that a quiet change is impossible.

### 4.1 The 169-hour horizon and the ingestion-seam list

`ForwardInferenceGuard.HorizonHours = 169.0` is the most dangerous single value in the system to change: it
is the floor that removes the high-value timing asset an attacker would otherwise obtain (`caf-mapping.md`
section 3.2). The ingestion-seam list (`caf-mapping.md` section 3.1) is security-critical for the same
reason: a dated input that bypasses a seam bypasses the guard.

Change control:

- the horizon and the seam list are enumerated in the technical seam table and pinned by
  `ForwardInferenceGuardTests`, `GdaReplayLoopTests` and the cross-seam `RestrictionInvariantTests`;
- widening or removing the horizon is a source change that fails `RestrictionInvariantTests`. A new dated
  ingester that does not call the guard is not detected by any test: the invariant tests cover the seams that
  exist, so adding a seam is a review-discipline item, checked against the seam table;
- any such change follows the `UnderstandingRestrictions.md` review discipline, is signed off by the owner
  (section 1), is flagged under the CHANGELOG Security heading (section 3), and updates the `caf-mapping.md`
  section 3.1 seam list in the same change.

The horizon widening from 25 hours to 169 hours is the worked example of this discipline functioning: it is
recorded in the `ForwardInferenceGuard` remarks with its owner-decision rationale and date.

### 4.2 The RestrictionReview register

The `RestrictionReview.Reviewed` register is the code-resident, test-enforced control that governs the most
dangerous seam in the system: the fusion of dynamics into estimation, which is the one place the engine
could slip from a filter toward a predictor (`caf-mapping.md` section 3.2, invariant #5).

Change control: a change to the security posture of that seam cannot merge without a reviewed entry in the
register, because `RestrictionInvariantTests` fails without one. The register is the governance record; there
is no separate document to keep in step with it. A new entry is an owner decision and is reviewed at the next
review point.

### 4.3 How the restriction invariant governs the seam

The restriction invariant is the spine of this governance, not an add-on. It governs the most dangerous seam
by construction: `RestrictionReview.AssertPermitted` fails loud if any estimation method that fuses a
dynamics process model runs without an explicit, reviewed entry. This converts a design intention ("do not
turn the estimator into a predictor") into a build-enforced gate that a future feature cannot cross without
the owner's reviewed sign-off. Governance here is therefore executable: the test suite, not a policy
document alone, is what refuses an unreviewed change.

### 4.4 Continuous-integration cadence (a risk-accepted deviation)

The repository is private and keeps the free GitHub Actions allowance, so by owner decision (October 2026) the CI
workflow does not run on every push or pull request, and CodeQL runs only when started by hand. CI runs when the
release workflow calls it on a release tag, and every publishing job of the release needs it, so nothing ships unless
the full matrix passed (Linux and Windows in every SIMD mode, including forced AVX-512 and FMA off, and macOS on
Apple silicon; no leg is advisory). Day-to-day verification is the local batch run. The residual accepted is that a
defect can sit on `main` between releases until the next local batch run or release finds it; it cannot reach a
release. The choice is reviewed at each release review point (section 2). `caf-mapping.md` section 4.4 and
`ssdf-mapping.md` section 7 cite this record.

---

## 5. How this honours the restriction invariant

Governance may only ever strengthen the forward-inference invariant, never provide a lever to weaken it. No
governance decision, owner sign-off or review outcome can grant access to sub-horizon data, because the guard
is independent of every role and has no bypass. The owner can tighten a control or accept a residual risk;
the owner cannot extend the engine's reach forward. If a proposed governance change would require
fresher-than-permitted data to be useful, the change is wrong, not the horizon.

---

## 6. Cross-references

- `caf-mapping.md` - the per-principle CAF mapping and the section 8 traceability matrix (the status of
  record for every control named here); section 3 for the guard; section 13 for the mapping's own change
  control.
- `risk-register.md` - the component-level risk register this governance accepts residual risk against.
- `incident-response.md` - the incident-response runbook and the post-incident review loop (D2) that feeds
  lessons back into this governance.
- `nis-oes-gap-assessment.md` - the NIS/OES gap assessment and the deploying-OES boundary.
- `UnderstandingRestrictions.md` - the governing no-forward-inference document.
