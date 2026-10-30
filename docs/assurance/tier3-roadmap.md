# Assurance roadmap (further frameworks)

**Status as at GridSim 1.95.295.** This note records the assurance frameworks that are candidates for later work,
each with where it would attach to the existing evidence and why it is parked rather than pursued now. It keeps the
forward view honest: the assurance set deliberately covers the Cyber Assessment Framework (`caf-mapping.md`), the NIS
Regulations (`nis-oes-gap-assessment.md`), the secure-development lifecycle (`ssdf-mapping.md`), build provenance
(`slsa-provenance.md`), model credibility (`nasa-std-7009-credibility.md`) and Cyber Essentials
(`cyber-essentials-note.md`). What follows is everything deliberately left for later, so the decision not to do it
now is a recorded choice rather than an omission.

---

## 1. Near-term candidates

### 1.1 ISO/IEC 27001 Annex A control mapping (self-assessed)

A mapping of GridSim to the Annex A control set, as a self-assessed gap map rather than a certification. The Cyber
Assessment Framework assumes an information-security management system underneath it, and Annex A is the recognised
control catalogue for that backbone. Parked because certification is an organisational undertaking disproportionate
to a software component at this stage, and because the controls GridSim actually owns are already evidenced under the
CAF mapping; a 27001 cross-reference would mostly re-index existing rows.

### 1.2 CIM / CGMES conformance statement

A formal conformance statement for the Common Information Model and CGMES import and export. This is the lightest
item on the list, because the mapping and the tests already exist (`docs/technical/0032` and
`docs/technical/design/cim-cgmes-mapping.md`). The work is to state conformance against the profile formally, not to
build anything. Parked only behind the current effort.

---

## 2. Conditional candidates (only if the deployment shape changes)

### 2.1 IEC 62351 (security for power-system communications)

Security for the telemetry protocols GridSim already frames (IEC 61850 GOOSE, IEC 60870-5-104, Modbus, DNP3,
C37.118). The protocol handling exists; a security layer over it does not. Parked because it is only warranted if a
live-communications deployment is ever in scope, which stands in tension with the replay-only posture and the
forward-inference guard. Recorded so that, if that posture ever changes, the requirement is already named.

### 2.2 FAIR data principles

Findable, Accessible, Interoperable, Reusable, applied to the planned per-asset profile library. This is a data-asset
framework, not a security one, and it attaches to that library when it is built, not to the engine. Parked until the
profile library exists, since there is nothing to apply it to before then.

---

## 3. Deliberately deprioritised

Named so the decision is explicit:

- **IEC 61508 (functional safety).** Overkill unless GridSim ever drives control actions, which the architecture and
  the forward-inference guard preclude. Not pursued.
- **NERC CIP.** A North American reliability standard; the jurisdiction is wrong for a GB-focused tool. Not pursued.
- **IEC 62443 in full.** IEC 62443-4-2, named as the secure-product benchmark in the CAF mapping (section 12), is
  sufficient for a component; no separate 62443 cross-walk document is kept; the full 62443 suite is a system-and-operator programme, disproportionate here.
- **External ISO/IEC 27001 certification.** The self-assessed posture of the whole assurance set is a deliberate
  choice; external certification is an organisational decision for later, not a technical gap.

---

## 4. How this list is kept honest

An item moves off this roadmap only when it is either done (and gets its own document in the assurance set) or
reclassified with a stated reason. The guiding rule is the same as the rest of the set: nothing is claimed that an
artefact or a passing test does not evidence, and a decision not to pursue a framework is recorded as a decision, not
left as a silent gap.
