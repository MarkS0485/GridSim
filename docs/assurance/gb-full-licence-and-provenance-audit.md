# gb-full licence and provenance audit

**Status as at GridSim 2.0.171.** This page audits where the data of the bundled full GB case (`data/gb-full`) comes
from and what that means for publishing it. It is a document only: no data is removed or changed, and no gb-full
number moves. The rule it applies is the public / private boundary: a public GB model is exactly the published Long
Term Development Statement (LTDS) data and the other published sources it cites, and an element that cannot be traced
to a published source cannot appear in a public export (`gridsim convert --public`, which keeps only what a
`gridsim-provenance/1` manifest traces to published sources).

## What the case is

`data/gb-full` is a GridSim case folder: 3,539 buses, 3,298 lines, 2,620 transformers, 1,958 demand rows, 5,318
generation rows, 1,383 on-load tap changers, 5 boundaries and the asset-dynamics files. Its README records that it was
assembled outside GridSim from the NESO transmission network data, the distribution network operators' LTDS tables and
the NESO registers, and committed as a pinned reference model: every gb-full number in the tests and the docs is
computed from these files.

## What the files record about their sources

| File | Per-element source recorded? | Note |
|---|---|---|
| `buses.json` | No | id, name, base kV, type, starting voltage, shunt, layout coordinates |
| `transmission.json` | No | name, ends, impedance, rating, circuits (579 names carry a `spine:` prefix) |
| `transformer.json` | No | name, ends, impedance, ratio, shift, rating |
| `generation.json` | No | name, bus, type, capacity, output, set-point, limits, inertia, flags |
| `demand.json` | No | name, bus, P, Q |
| `onloadtapchanger.json` | No | transformer, controlled bus, target, tap range, step, deadband |
| `boundaries.json` | No | name, MW limit, branches |
| `asset-classes.json` | Yes, per parameter | each class carries `provenance` tags (`Recorded` with the source named, or `CuratedPlaceholder` with `[VERIFY]`) |

So, apart from the asset classes, no element of gb-full says which published document it comes from. The licence each
source dataset is published under is not recorded in the repository either. **[VERIFY: the publisher, dataset, version
and licence of each source the case was built from, recorded per dataset before any of it is published.]**

## Elements that are not published data

These element classes are products of the assembly step, not values published by a network operator, so no manifest
could cite a published source for them:

| Element class | Count | Why it is not published data |
|---|---:|---|
| Synthesised supergrid transformers (`SGT ...`) | 1,207 of 2,620 transformers | Built to couple the distribution networks to the transmission spine; sized from demand, all with r = 0, many sharing one reactance family (data/gb-full/README.md) |
| National slack unit (`National slack @ Melksham`) | 1 | A power-flow-only balancing unit of 266,425 MVA at bus 194, not a real machine |
| Grid-supply-point voltage-control units (`GSP volt ctrl @ ...`) | 1,071 | Power-flow-only units with no output that hold voltages at grid supply points; together with the slack, 838,265 MVA of modelling capacity |
| Bus layout coordinates | 3,539 buses | Normalised plot positions (x from 0.154 to 0.961), with no source |
| Tap-changer settings | 1,383 | Every tap changer has the same generic settings (target 1.0 pu, range 0.9 to 1.1, step 0.0125, deadband 0.01 pu), not per-unit published settings |
| Parallel supergrid transformer copies (`#2`) | 47 | Each adds a second bank at one of 7 GSP groups (3,840 MVA in all); whether these are real or a double count is unverified (data/gb-full/README.md) |

The rest of the case (lines, the other 1,413 transformers, demand, the real generating units, boundaries) is described
as derived from published data, but without per-element citations it cannot yet be shown to be exactly that data: the
assembly step may have joined, filtered, renamed, aggregated or defaulted values on the way.

## Consequence for a public export

- `gridsim convert gb-full <output> --public` exits 1 and writes nothing, because gb-full has no provenance manifest.
- A manifest cannot make gb-full public as it stands. The gate would strip the 1,207 synthesised supergrid transformers
  (which disconnects the distribution networks from the transmission spine, leaving islands with no slack), and the
  national slack unit (leaving the slack bus with no generator); either alone makes the gate refuse the whole model.
- A public GB model therefore has to be built from the published datasets alone, with each element citing the
  dataset row it came from and each modelling device (a slack, a voltage-control equivalent, a coupling transformer)
  either taken from a published source or left out. That is a modelling decision, not a data fix.

## Limits

- This audit reads the case files and their README; it does not open the source datasets, so it cannot say which
  individual values match a published row.
- Licence terms are not stated here because they are not recorded in the repository; stating them from memory would be
  unsourced.
