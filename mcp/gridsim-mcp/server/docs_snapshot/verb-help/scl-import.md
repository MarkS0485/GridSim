### scl-import

`gridsim scl-import <file.scd> --params <params.json> --out <case.substations.json> [--assume-closed]`

Imports the substation section of an IEC 61850 SCL file (an SSD, SCD or any other SCL file; Core `GridSim.Core.IO.Scl`)
into the node-breaker model of [substation-state](#substation-state) and writes it as a `gridsim-substations/1` file. SCL
describes topology and equipment, not electrical values, so impedances and set points come from a `gridsim-scl-params/1`
file keyed by SCL path. See [technical ch. 45](<../technical/0045 IEC 61850 SCL substation import.md>).

What is imported: each `ConnectivityNode` becomes a node (its id is the `pathName`); a `CBR` becomes a breaker and a `DIS`
a disconnector, or an earth switch when one terminal is grounded; `LIN` and `CAB` become lines (one piece with two
terminals, or the two one-terminal halves a file holds for a line between substations); `GEN` becomes a generator and
`IFL` an external grid (a slack generator); a `PowerTransformer` with two windings becomes a transformer and with three a
three-winding transformer. `CTR` and `VTR` are kept as measurement points and play no part in the topology; a current
transformer with two terminals sits in series, so its two nodes are merged into one. Loads are not in SCL: they come from
the params file, at a node's `pathName`. Every other type code is refused by name, and so is the whole import: leaving out
a capacitor or a motor would change the network. The SCL substation section holds no switch state, so every switch needs
its state (`normalOpen`) in the params file: a switch without one is refused, each named. With `--assume-closed` such a
switch is taken as closed instead, listed in the report and recorded in the written file's `notes`. A current transformer
whose two terminals are in different voltage levels is refused (it has no ratio to join them).

The Communication, IED and DataTypeTemplates sections are read past and counted. GOOSE control blocks are listed by name
only. Elements of the substation section that are not read (`Function`, `LNode`, `GeneralEquipment`, `SubEquipment`,
`TapChanger`, `Private`, ...) are listed as NOT IMPORTED with their count. Any such element that holds conducting
equipment or a power transformer (a `Function` or `SubFunction` may) is refused, as is a top-level `Line` or `Process`
section that holds equipment: that equipment would otherwise be lost. The params file's values are range-checked: `vPu`
and `tapRatio` above 0, a branch impedance not exactly 0, ratings at least 0, `qMinMvar` not above `qMaxMvar` and
`pMinMw` not above `pMaxMw`.

The file is untrusted XML: a file with a DTD (so any entity, internal or external) is refused, elements may nest at most
64 deep, and the file size is capped like every other input file.

The report (mapped, measurement points, NOT IMPORTED, read past, GOOSE control blocks, notes) is printed, with the bus and
branch count of the projection at the imported switch states. The output is never an existing file. Not on the shared
`--json`/`--csv` writer. Exit 0 imported; 1 the SCL file or the params file is refused or unreadable, or the output cannot
be written; 2 usage (a missing flag, or an output that already exists).

| Flag | Default | Meaning |
|---|---|---|
| `--params` | required | The `gridsim-scl-params/1` file: lines, transformers, three-winding transformers, generators, infeeds, loads and switch states. |
| `--out` | required | The `gridsim-substations/1` file to write (never an existing one). |
| `--assume-closed` | off | Take a switch with no state in the params file as closed instead of refusing it; each is listed and recorded in the output file's `notes`. |
