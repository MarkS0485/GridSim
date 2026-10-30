# GridSim & GDA Licence

**Licence version 1.0 - 24 July 2026**

Copyright (C) 2026 Mark Shirley (MarkS0485). All rights reserved.

This licence covers **GridSim** (the power-grid simulator: GridSim.Core, GridSim.Gda,
and the CLI, Web, WPF and GPU front-ends) and **GDA** (the Grid Data Archive ingestion
and network-model tooling) - together **"the Software"**. From version 1.3.0 the
Software is **source-available, proprietary** software. It is not open source.
Releases published before version 1.3.0 under the GNU GPL v3.0-or-later remain
available under that licence (see section 9).

**Intent (non-binding).** The aim of this licence is to be fair, not extractive.
Licensing is measured on buses and nothing else - never per generator, per demand
point or per study.

## Summary (non-binding - the numbered sections govern)

| Use | Bus limit |
|---|---|
| Personal, non-commercial | 1,500 |
| Academic research & teaching (attribution required in publications) | 1,500 - larger on request |
| Commercial: Code Licence | None |
| Commercial: Deployment Licence | None |
| TSO / ESO / DNO (site-wide, subject to regulatory approval) | None |

Commercial and Network Operator terms are available on application (see Contact).

## 1. Definitions

- **"Bus"** - one electrical busbar (node) in a network model loaded into or built by
  the Software. Bus limits apply **per network model**, not cumulatively across models.
- **"Bundled Networks"** - network models distributed with the Software itself (the
  embedded IEEE and CIGRE test cases, the bundled synthetic GB network, the international
  starter maps under `data/intl` and the distribution test feeders under `data/dist3ph`). Bundled
  Networks do **not** count toward any Bus limit.
- **"Licensor"** - the copyright holder named above. **"You"** - the licensee.
- **"Network Operator"** - a transmission or distribution system operator (TSO, ESO,
  DNO) or equivalent licensed grid operator.
- **"Major Version"** - the first component of the version number (the "1" in 1.3.0).

## 2. The licensing metric

Buses are the only licensed quantity. No tier of this licence limits generators,
demand points, lines, transformers, storage, study runs, scenarios or compute.
Where a tier carries a Bus limit, You may not technically circumvent it (for
example by patching the Software to misreport or ignore bus counts).

## 3. Personal Licence

Individuals may use, build and modify the Software for personal, non-commercial
purposes, with network models of up to **1,500 Buses**. You may not redistribute
the Software or modified versions of it, and may not use it to provide services,
paid or unpaid, to third parties.

## 4. Academic Licence

Recognised academic and research institutions may use, build and modify the
Software for research and teaching, with network models of up to **1,500 Buses**.
Larger limits are available on request - contact the Licensor (see Contact) and
access will be arranged case by case. Any published research produced with the
Software must carry attribution (section 8). Commercial consultancy is not covered by
this tier, even when performed within an academic institution.

## 5. Commercial Code Licence

Available on application. It grants You the right to use,
modify and incorporate the Software into your own products and services and to
distribute it in compiled form as part of them, with **no Bus limit**. Conditions:

- Your product's manuals and user documentation must state that it incorporates
  GridSim and/or GDA (section 8).
- You may not redistribute or resell the Software's source code itself, nor ship a
  substantially unmodified copy of the Software as a competing standalone product.
- The licence covers **every release within the licensed Major Version** (the
  current Major Version is 1; the current release is 1.3.0). Your rights to the
  releases You have licensed are perpetual. A new Major Version requires a new licence.

## 6. Commercial Deployment Licence

A supported, turnkey deployment of the Software on approved hardware, governed by
a signed agreement with the Licensor, with **no Bus limit**. Terms are available on
application (see Contact).

## 7. Network Operators

The Licensor may, at their discretion and subject to any required regulatory
approval, grant Network Operators a **site-wide licence**. The
intent: grids will not be held hostage to this tool, and operators' public and
commercial teams should work from the same tooling, so that results, planning and
contingency analysis are unified. Contact the Licensor to arrange.

## 8. Attribution

- **Academic:** publications reporting results produced with the Software must
  cite it by name ("GridSim" and/or "GDA"), with version and project URL.
- **Commercial Code Licence:** manuals and user documentation of products
  incorporating the Software must include a reference to GridSim and/or GDA.
- Notices, credits and copyright statements in the Software may not be removed.

## 9. Prior GPL releases

Versions of GridSim released before 1.3.0 were published under the GNU GPL
v3.0-or-later. Those grants are not - and cannot be - revoked: copies already
distributed under the GPL remain governed by it. From version 1.3.0 onward, no
new release is offered under the GPL.

## 10. General restrictions

Except as a tier expressly allows, You may not redistribute, sublicense, rent,
sell or host the Software for third parties. You may not misrepresent its origin
or use the Licensor's names or marks to endorse derived work without permission.

## 11. Third-party components

Third-party components keep their own licences and are not covered by this
licence - including the MATPOWER case files (see
`data/matpower/LICENSE-MATPOWER.txt`), package dependencies (NuGet, PyPI, npm),
and public datasets ingested by GDA, which remain governed by their publishers'
data licences (e.g. OGL, CC-BY).

## 12. Contributions

By submitting a contribution to the Software You retain your copyright and grant
the Licensor a perpetual, irrevocable, worldwide, royalty-free licence to use,
modify, distribute and relicense it under any terms.

## 13. No warranty

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NON-INFRINGEMENT. Simulation outputs are engineering estimates and are not a
substitute for professional judgement; the Software is not certified for
real-time operational control of energised networks.

## 14. Liability

To the extent permitted by law, the Licensor's total liability under this licence
is capped at the greater of the fees You paid in the preceding 12 months or GBP 100,
and the Licensor is not liable for indirect or consequential loss. Nothing in
this licence excludes liability that cannot be excluded under the law of England
and Wales, including for death or personal injury caused by negligence, or fraud.

## 15. Termination

This licence terminates automatically if You materially breach it and do not cure
the breach within 30 days of notice. Sections 9 and 11-16 survive termination.

## 16. General

This document is the entire agreement for the Personal and Academic tiers; Commercial
Deployment Licences are governed by their signed agreements, which prevail on conflict. If a
provision is unenforceable, the remainder stands. This licence is governed by the
law of England and Wales, under the exclusive jurisdiction of its courts.

## Contact

Contact us: **gdatooling@proton.me**
