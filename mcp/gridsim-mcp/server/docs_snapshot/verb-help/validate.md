### validate

`gridsim validate <file.json|folder>... [--schemas <dir>]`

Check GridSim JSON v2 documents with the Phase 1 validator, choosing the check by each document's `"schema"` tag:
`gridsim-case/2` against `case.schema.json` (and, when the manifest is valid, each network file it lists, read relative
to it: a missing one is an error on the manifest, an existing one is checked as a network file); `gridsim-network/2`
against `network.schema.json`; `gridsim-library/2` files all together (schema, kind specs, unique `id@rev`, controller
references resolving within the set); `gridsim-kind/2` against `kind.schema.json`; `gridsim-dynamics/2` (F23, machine
dynamic data for `stability --rms --dyn-data`) against `dynamics.schema.json` and then its templates as one library
(the binding to a case's generators is checked when a study loads it); `gridsim-dist3ph/2` (N339, the three-phase
feeder in its v2 form) against `dist3ph.schema.json` and then with the feeder reader itself (ids, references, the
branch kind rules, one radial tree, phases; its first refusal is reported). A folder is searched for `*.json`
files. A document with no tag (a v1 case file, for example) or an unknown tag, and a file that is not JSON, are
reported as invalid. Library imports in a manifest are not resolved (they name files relative to a data root a lone
manifest does not know); pass the library files to check them. See
[technical ch. 28](<../technical/0028 The v2 JSON schema.md>).

Each document prints `OK <path> (<tag>)` or `FAIL <path> (<tag>): N error(s)` followed by every error as
`<pointer>: <message>`, then a summary line.

| Flag | Default | Meaning |
|---|---|---|
| `--schemas` | `schemas/v2` beside the program, else the nearest above it or above the current folder | The folder holding the v2 schemas (with the kind specs in `kinds/`). |

Exit 0 when every document is valid; 3 when any is not; 1 when a path does not exist, no `.json` file is found or the
schemas cannot be found or loaded; 2 for a usage error.

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).
