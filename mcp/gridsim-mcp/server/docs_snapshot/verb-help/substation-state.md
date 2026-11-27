### substation-state

`gridsim substation-state <case.json> [--open <switch>,...] [--close <switch>,...] [--unspecified refuse|normal] [--numbering node-rank|compact] [--emit <projected.m>] [--base-mva <MVA>] [--sequence <steps.json>]`

Node-breaker topology processing (RC4 R4-11 part A, Core `GridSim.Core.Model.Substations`). `<case.json>` is a native
`gridsim-substations/1` file: `substations` (each with `voltageLevels`, `bays`, connectivity `nodes` and `switches`, a
switch being a `breaker`, `disconnector`, `earthSwitch` or `loadBreak` with `from`, `to` and `normalOpen`, the words of
the asset overlay's substation records) and the equipment on the nodes: `lines`, `transformers`, `loads`, `generators`
and `shunts`, and optionally `transformers3w` (three-winding transformers: `id`, the `hv`, `mv` and `lv` nodes, the pair
impedances `r12Pu`, `x12Pu`, `r13Pu`, `x13Pu`, `r23Pu`, `x23Pu` on the model base, the star-point magnetising `bPu`, and
optional three-entry lists `ratio`, `phaseShiftDeg` and `ratedMva`; each is projected as a star bus and three branches with
the PSS/E reader's star impedances), and optionally `notes` (an array of strings: provenance, such as the switches an
[scl-import](#scl-import) with `--assume-closed` took as closed; no calculation reads them). `<case.json>` may instead be a CGMES exchange, a `.zip` or a folder of EQ, TP, SSH and SV profile files, which
Core's `CgmesSubstationReader` reads into the same node-breaker model (the inverse of [cgmes-export](#cgmes-export)); the
SSH switch states are then the starting states, `--open` and `--close` override them, and a switch the SSH does not state is
handled by `--unspecified`. CGMES carries no system base, so the model is read on 100 MVA (noted) unless `--base-mva` gives
one; what the reader mapped only in part or defaulted is printed as `cgmes note:` lines and written to a `cgmes_notes` table. A
CGMES object the node-breaker model has no place for (a three-winding transformer, a series compensator, ...) is refused
by name, exit 1. A `.json` input behaves exactly as before. The verb applies the switch states, joins the nodes that closed switches connect into electrical buses and
prints the resulting bus-branch model: the bus groups, the bus ids, which terminal sits on which bus, what became of every
piece of equipment and the demand and generation accounting. The input is never altered. Nothing else in GridSim changes:
the existing bus-branch model, solvers, importers and exporters behave exactly as before; this verb produces an ordinary
case beside them.

A group of nodes becomes a bus when two or more equipment terminals sit on it and no closed earth switch earths it. A
group with one terminal is isolated (an open breaker cuts it off) and its equipment is left out of the bus-branch model
with the reason recorded; a branch with an isolated end is left out whole; a closed earth switch earths its group. A
load or generator that is not connected is listed with its MW, and the served plus not-served demand always equals the
total. Bus ids are stable: with `--numbering node-rank` (default) a bus id is 1 plus the ordinal rank, among all nodes of
the file, of the lowest node of its group, so splitting one group never renumbers another; `compact` numbers the buses
1 to n in group order. Every ordering is by ordinal id, never by hash order, so the same file and states give the same
buses and bits on every run.

`--sequence <steps.json>` runs Core's `SwitchingScenario` from the starting state the other flags give. The file is
`{"schema": "gridsim-switching-sequence/1", "steps": [{"action": "open", "switch": "CPL", "note": "optional"}, ...]}`, an `action` being
`open` or `close`, read strictly (an unknown key, an action other than those two or a switch the model does not have exits 1). The report
of the starting state is printed as before, then one line per step with the bus and node-group counts, the demand served and the
generation connected, and the node groups the step removed and added. Every step is projected afresh from the node-breaker graph and the
cumulative switch states, so opening a switch and reclosing it returns the starting buses and bus ids; asking for the state a switch
is already in is recorded as `(already in that state)`, not refused. The result adds the summary member `sequence_steps` and the tables
`sequence` (one row per step, step 0 being the start) and `sequence_buses` (the buses of every step). Each step's projected case is
admitted by the licence's bus limit as the starting one is. `--sequence` cannot be combined with `--emit` (exit 2): `--emit` writes the one
case of the starting state, and a step's case is written by naming its switch states in `--open` and `--close`. The overlay projection
of a v2 case's substation records (`SubstationOverlayProjection`) is not reached from this verb.

Every switch must have a state: a switch named in neither `--open` nor `--close` is refused (a closed switch is never
inferred) unless `--unspecified normal` declares that unnamed switches take their `normalOpen` state. The topology is
refused, with every problem named, when a connectivity node is dangling (no switch and no equipment terminal, or held by
one switch only), when a switch joins two nodes of different nominal voltage, when a line joins two voltage levels, or
when an id is repeated or a reference does not resolve. A solve on the projected case goes through the existing verbs:
`--emit` writes the projected case to a NEW MATPOWER file (never over an existing file; a name in use gets a numbered
suffix; never the input), then `gridsim pf projected.m` solves it. Exit 0 done; 1 the file cannot be read or its
topology or states are refused; 2 usage.

| Flag | Default | Meaning |
|---|---|---|
| `--open` | none | Comma list of switch ids to open. |
| `--close` | none | Comma list of switch ids to close. |
| `--unspecified` | `refuse` | `refuse` or `normal`: what to do with a switch named in neither list. |
| `--numbering` | `node-rank` | `node-rank` (stable ids) or `compact` (1 to n). |
| `--emit` | none | Write the projected case as a new MATPOWER `.m` file. |
| `--base-mva` | 100 | CGMES input only: the system base the model is read on. |
| `--sequence` | none | A `gridsim-switching-sequence/1` file: run the ordered open and close steps from the starting state and report each. Not with `--emit`. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary (bus, group and branch counts, the demand and generation accounting, the
grouping) and the tables `buses`, `node_groups`, `terminals` (the terminal to bus map), `equipment` (with dispositions)
and `switches`.
