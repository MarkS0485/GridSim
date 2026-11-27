### cgmes-export

`gridsim cgmes-export <substations.json> --dir <folder> [--cgmes-version 2.4.15|3.0] [--open <switch>,...] [--close <switch>,...] [--unspecified refuse|normal] [--sv none|solve]`

CGMES per-profile export of a native node-breaker file (RC4 R4-11 part B; Core `GridSim.Core.IO.Cim.Profiles`).
`<substations.json>` is a `gridsim-substations/1` file (see [substation-state](#substation-state)). The verb writes separate
documents into `--dir`: EQ (substations, voltage levels, bays, connectivity nodes, switches, terminals, lines,
two-winding transformers with ratio and linear phase tap changers, loads, synchronous machines with their voltage control,
shunts and permanent apparent-power limits), TP (the TopologicalNodes the closed switches make), SSH (switch states,
set-points, terminal flags) and, with `--sv solve`, SV (the Newton-Raphson voltages of the projected case). Files are
`<case>_EQ.xml`, `_TP.xml`, `_SSH.xml` and `_SV.xml`; an existing file is never overwritten (a numbered suffix is used).
`--cgmes-version 2.4.15` (default) writes the CIM16 namespace with `rdf:ID="_mRID"`; `3.0` writes CIM100 with `urn:uuid:`
identifiers. Impedances are written in ohms and siemens, set-points in kV and MW (CGMES units); the reader converts them
back to the same bits wherever a double exists that does so, and counts the values that have none. GridSim's own
identities travel in `IdentifiedObject.description` as `gridsim-id=<id>`, and the model name, base and frequency in the
EQ `md:Model.description`, so reading the documents back gives an identical model.

Every class and data item the files do not carry is listed (the structured omissions record: diagram layout, geographical
location, short circuit and dynamics profiles, ratedS and machine type, other limit kinds, ...): each with its profile and
the reason; nothing is left out silently. The CGMES specifications used are ENTSO-E's free CGMES 2.4.15 and CGMES 3.0
(see the CGMES Library at https://www.entsoe.eu/data/cim/cim-for-grid-models-exchange/ and
https://github.com/entsoe/application-profiles-library); no schema validator was run against the output (see
[cgmes-conform](#cgmes-conform) for what is checked). Exit 0 done; 1 the file cannot be read, its topology or states are
refused, or a file cannot be written; 2 usage.

| Flag | Default | Meaning |
|---|---|---|
| `--dir` | required | Folder the documents are written to (created if needed). |
| `--cgmes-version` | `2.4.15` | `2.4.15` or `3.0`. |
| `--open` | none | Comma list of switch ids to open (written as the SSH state). |
| `--close` | none | Comma list of switch ids to close. |
| `--unspecified` | `normal` | `normal`: a switch named in neither list takes its `normalOpen` state; `refuse`: refuse it. |
| `--sv` | `none` | `solve` writes an SV document from a Newton-Raphson solve of the projected case. |
| `--profiles` | none | A comma list of `dl`, `gl`, `sc` and `dy`: the bus-branch export with these profiles (below). |
| `--geo` | none | With `--profiles gl`: the `gridsim-geo/1` sidecar holding the bus sites and line routes. |
| `--dyr` | none | With `--profiles dy`: the PSS/E DYR file whose records are written as the DY profile. |
| `--model` | none | `bus-branch`, with `--cgmes-version 3.0`: a bus-branch case written as CGMES 3.0 EQ, TP, SSH and SV documents (below). |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary and the tables `files` and `omissions`.

**Diagram, geographical, short-circuit and dynamics profiles.**
`gridsim cgmes-export <case> --dir <folder> --profiles dl,gl,sc,dy [--geo <gridsim-geo.json>] [--dyr <file.dyr>]`

With `--profiles` the input is a bus-branch case (any case file the CLI reads). It is written as `<case>_EQ.xml`, the same
bytes `convert` writes to a `.xml` file (one CGMES 2.4.15 document with the equipment, topology, set-points and starting
voltages), and each named profile as its own document with an `md:FullModel` header:

- `dl`, `<case>_DL.xml`: one Diagram (orientation negative, y growing downwards as the single-line diagram draws it) and,
  per bus, a DiagramObject on its TopologicalNode with one point at the bus's layout position. Refused when every bus is
  at (0, 0).
- `gl`, `<case>_GL.xml`: WGS 84 (`urn:ogc:def:crs:EPSG::4326`, x = longitude, y = latitude) Locations from the `--geo`
  sidecar. A bus-branch case has no Substation to carry a location, so each line whose two buses have sites gets a
  Location running from its from site through its route to its to site; a site that no such line touches is refused.
- `sc`, `<case>_SC.xml`: the short-circuit data: line and transformer zero-sequence impedances in ohms, transformer
  winding connections (D, Y, Yn) with the neutral reactance, and machine X''d, X2 and zero-sequence impedance (per unit on
  the machine base). Each ohm value is the double GridSim's reader converts back to the model's bits; any that has none
  is counted in a note.
- `dy`, `<case>_DY.xml`: the `--dyr` records of GENROU, GENSAL, GENCLS, SEXS, SCRX and TGOV1 as their IEC 61970-302
  classes, linked to their machines. The case must be a PSS/E `.raw` file (the DYR names machines by bus and ID). Any
  other model is refused by name.

`--profiles` on its own writes CGMES 2.4.15 only, and refuses `--cgmes-version 3.0` and the node-breaker options. The gridsim-geo/1
sidecar is strict JSON:
`{"format":"gridsim-geo/1","crs":"EPSG:4326","sites":[{"bus":1,"latDeg":51.5072,"lonDeg":-0.1276}],"lines":[{"branch":2,"points":[[51.51,-0.12]]}]}`
(`branch` is the 1-based branch number, `points` the route between the two end sites). Reading the profiles back is a
library step (`CgmesDiagramLayout`, `CgmesGeographicalLocation`, `CgmesShortCircuitProfile`, `CgmesDynamicsProfile`;
see [technical ch. 32](<../technical/0032 CIM and CGMES import and export.md>)). Exit 0 done; 1 the case, the sidecar or
the DYR cannot be read or a profile is refused; 2 usage.

**CGMES 3.0 bus-branch documents.**
`gridsim cgmes-export <case> --dir <folder> --model bus-branch --cgmes-version 3.0 [--profiles dl,gl,sc,dy] [--geo <gridsim-geo.json>] [--dyr <file.dyr>]`

With `--model bus-branch` a bus-branch case (any case file the CLI reads) is written as four CGMES 3.0 documents,
`<case>_EQ.xml`, `_TP.xml`, `_SSH.xml` and `_SV.xml`, each with an `md:FullModel` header naming its CGMES 3.0 profile and
the models it depends on. The values are the ones the 2.4.15 document holds (the same writer, the same exact-value search),
split by profile as the CGMES 3.0 RDFS places each attribute: equipment, terminals, limits and tap changers in EQ;
TopologicalNodes in TP; p and q, targets, steps, `Equipment.inService` and terminal flags in SSH; starting voltages in SV.
Every IdentifiedObject carries its `IdentifiedObject.mRID`; objects are named `urn:uuid:` identifiers. Each bus gets a
Substation and a VoltageLevel, which CGMES 3.0 needs as the TopologicalNode's container. GridSim's CIM reader rebuilds the
same model from the four documents as from the 2.4.15 one. Not written: the machine inertia (in 3.0 a dynamics-profile
attribute; use `--profiles dy`), `GeneratingUnit.normalPF` (the case holds none: distributed-slack factors are a solve option, not model data) and the SV flows;
`SynchronousMachine.type` and `operatingMode` are written as `generator`. Each omission is listed. With `--profiles` the
named profiles are written in CGMES 3.0 too and refer to the 3.0 objects; the short-circuit profile then holds the
machine r0, x0, r2 and x2 in ohms (on the machine's rated voltage and MVA), as CGMES 3.0 does. `--model bus-branch`
without `--cgmes-version 3.0` is a usage error (exit 2); the 2.4.15 routes above are unchanged.
