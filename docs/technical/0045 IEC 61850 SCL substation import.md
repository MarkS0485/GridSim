# IEC 61850 SCL substation import

GridSim reads the substation section of an IEC 61850 SCL file (System Configuration description Language: SSD, SCD, ICD
and the other SCL file kinds) into its node-breaker model. The code is in `src/GridSim.Core/IO/Scl/` (`SclReader`,
`SclParams`, `SclImporter`), the verb is `gridsim scl-import`
([user manual ch. 19](<../manual/0019 CLI reference.md#scl-import>)), and the tests are
`tests/GridSim.Tests/Rc5Wp30SclImportTests.cs`.

The verb and the Core types are new. No existing output changes.

## Sources

- The element and attribute names are those of the public SCL XML schema, version 2007 revision B release 4
  (`SCL_Substation.xsd`, `SCL_Enums.xsd`), published by the IEC as code components of IEC 61850-6:2009/AMD1:2018 and free
  to download under the IEC code component licence (www.iec.ch/CCv1).
- The schema lists the conducting-equipment type codes (`tPredefinedCommonConductingEquipmentEnum`: CBR, DIS, VTR, CTR,
  GEN, CAP, REA, ...) but documents none of them. The meanings used here are the names OpenSCD shows for the codes in its
  conducting-equipment dialog (Apache-2.0). The convention that a terminal with `cNodeName="grounded"` joins a switch to
  earth is also OpenSCD's.
- No IEC 61850 standard text was used. The communication and application layers (GOOSE, sampled values, MMS) need
  IEC 61850-8-1 and 9-2 and are not part of this import.

## What is mapped

| SCL | Node-breaker model |
|---|---|
| `Substation` | a substation, id = its name |
| `VoltageLevel` with `Voltage` | a voltage level, id = substation/level, nominal kV from the value, unit V, multiplier `''`, `k` or `M` |
| `Bay` | a bay, id = substation/level/bay |
| `ConnectivityNode` | a node, id = its `pathName` |
| `CBR` (Circuit Breaker), two terminals | a breaker |
| `DIS` (Disconnector), two terminals | a disconnector |
| `DIS`, one terminal on a node and one grounded | an earth switch |
| `LIN` (Power Overhead Line), `CAB` (Power Cable) | a line: one piece with two terminals, or two one-terminal pieces named together in the params file |
| `GEN` (Generator) | a generator |
| `IFL` (Infeeding Line) | an external grid: a generator marked slack |
| `CTR` (Current Transformer), `VTR` (Voltage Transformer) | a measurement point, kept for later binding to protection devices; no part of the topology |
| `PowerTransformer` with two windings | a two-winding transformer |
| `PowerTransformer` with three windings | a three-winding transformer |

An equipment id is its path: substation/level/bay/name, or for a power transformer the path of what holds it plus its
name. A switch id is its path too. A current transformer with two terminals sits in series between two nodes with no
impedance, so the two nodes are one electrical point: the node with the higher `pathName` is merged into the other, and
the merge is listed. A current transformer whose two nodes are in different voltage levels is refused: it has no ratio,
so it cannot join them.

The model's lists follow the SCL file's order, so the params file's order never changes the result. Loads, which SCL does
not hold, are put in the SCL file's order of the node each one names, then by load id.

## What is not mapped

- Every other type code (CAP, REA, MOT, GIL, SAR and the rest, and the extension codes `E...`) is refused by name, and the
  whole import with it. Leaving a capacitor bank or a motor out would change the network, so nothing is dropped silently.
- Equipment held by an element the import does not read (the schema lets a `Function` or `SubFunction` under a
  substation, voltage level or bay hold conducting equipment) is refused by name, with the element that holds it.
- A switch with no terminal, or with one terminal only, is refused: its place in the topology is unknown.
- A top-level `Line` or `Process` section that holds equipment is refused; one that holds none is listed.
- Elements of the substation section that the import does not read, and that hold no equipment, are listed as NOT
  IMPORTED with their count, by element name: `Function`, `LNode`, `GeneralEquipment`, `SubEquipment`, `EqFunction`, `TapChanger`, `NeutralPoint`,
  `Private`, `Text` and any other. Only the outermost unread element is counted.
- The `Communication`, `IED` and `DataTypeTemplates` sections are read past and counted (sub-networks, connected access
  points, IEDs, logical nodes, sampled-value control blocks, template entries). GOOSE control blocks are listed by name
  (IED/logical device/name) and nothing more.
- A winding's grounded terminal (its neutral earthing) is noted and not modelled: the power flow is balanced.
- A connectivity node no terminal and no load uses is left out and noted.
- There is no SCL export.

## Switch states

The SCL substation section holds no switch state, so every switch needs its `normalOpen` in the params file. A switch
without one is refused, each named: taking it as closed would be a guess, as the node-breaker model's own policy for an
unstated switch also refuses. With `--assume-closed` (Core: `SclImporter.Import(doc, params, assumeClosed: true)`) such a
switch is taken as closed, listed in the report, and recorded in the written file's `notes`, so the assumption travels
with the file. The states an IED reports at run time are not read.

## The params file: `gridsim-scl-params/1`

SCL holds no impedances or set points. They come from a strict JSON file keyed by SCL path (an unknown member, a repeated
member, a wrong type or a missing required value is refused, naming the JSON path):

```json
{
  "format": "gridsim-scl-params/1",
  "lines": [ { "ends": ["S1/V132/FDR/L1", "S2/V132/L1/L1"], "rPu": 0.01, "xPu": 0.1, "bPu": 0.02, "rateAMva": 150 } ],
  "transformers": [ { "path": "S3/HV/T1", "hv": "W1", "lv": "W2", "rPu": 0.002, "xPu": 0.08, "tapRatio": 1.025 } ],
  "transformers3w": [ { "path": "S3/T2", "hv": "W1", "mv": "W2", "lv": "W3",
                        "r12Pu": 0.001, "x12Pu": 0.1, "r13Pu": 0.002, "x13Pu": 0.2, "r23Pu": 0.0015, "x23Pu": 0.12 } ],
  "generators": [ { "path": "S1/V132/GEN/G1", "pMw": 20, "vPu": 1.01 } ],
  "infeeds": [ { "path": "S2/V132/INF/GRID", "vPu": 1.0 } ],
  "loads": [ { "id": "LD1", "node": "S1/V132/BB1/BB1", "pMw": 40, "qMvar": 10 } ],
  "switches": [ { "path": "S1/V132/FDR/QC9", "normalOpen": true } ]
}
```

- `name`, `baseMva` (100) and `frequencyHz` are optional. Without a name the model takes the SCL `Header` id.
- `lines`: `ends` names one LIN or CAB with two terminals, or two with one terminal each. `rPu`, `xPu` and `bPu` are
  required, per unit on the model base; `rateAMva` and `id` (default: the first end's path) are optional.
- `transformers` and `transformers3w`: the windings are named by their `TransformerWinding` name. `rPu` and `xPu` (the pair
  values `r12Pu` to `x23Pu` for three windings) are required; the rest are optional with the defaults of
  `gridsim-substations/1`.
- `generators` need `pMw` and `vPu`; `infeeds` need `vPu` (`pMw` defaults to 0).
- `loads` sit at a node `pathName`. SCL has no load equipment code.
- Values are range-checked when the file is read, naming the JSON path: `vPu` and `tapRatio` (and each three-winding
  `ratio`) above 0; a line's or transformer's `rPu` and `xPu` (each pair for three windings) not both 0; `rateAMva` and
  `ratedMva` at least 0; `qMinMvar` not above `qMaxMvar`; `pMinMw` not above `pMaxMw`.
- Every LIN, CAB, GEN, IFL and power transformer of the file needs an entry, and every entry must name something in the
  file; anything missing or unknown is refused by name.

After mapping, the node-breaker model's own consistency check runs (duplicate ids, dangling nodes, a line between voltage
levels, ...), and any problem refuses the import.

## Safe reading

An SCL file is untrusted XML. The reader:

- caps the file size with the same limit as every other input file;
- refuses a file with a DTD (`<!DOCTYPE>`), so no entity is declared or expanded and nothing external is fetched (no XXE,
  no entity-expansion bomb); no XML resolver is set;
- refuses elements nested deeper than 64 levels, checked in a first pass before any tree is built;
- refuses a root that is not `SCL` in the SCL namespace.

## Verification

The tests use hand-made SCL files written for them: a double-busbar substation with a coupler, a feeder bay
(disconnector, breaker, current transformer, disconnector, earth switch, line) and a generator, with a second substation
holding the far end of the line and an infeed; and a substation with a two-winding and a three-winding transformer. The
expected nodes, switches, line, generators and loads are written out by hand; the projection gives two buses with every
switch closed and three with the coupler open; the imported file solves bit for bit like the same network written
directly as `gridsim-substations/1`. The public sample `valid2007B4.scd` from OpenSCD (Apache-2.0, in
`tests/GridSim.Tests/fixtures/openscd-scl/` with its attribution) is read, its element counts are pinned (checked with an
independent XML parser), and its import is refused because three of its switches have no terminal and one has only a
grounded one, and its earth switch has no stated state.

The import has not been tried on an export from a vendor's SCL tool.
