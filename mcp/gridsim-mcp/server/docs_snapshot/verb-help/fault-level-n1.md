### fault-level-n1

`gridsim fault-level-n1 <case> [--buses <id,id,...>] [--branches <idx,idx,...>] [--objective max|min] [--parallel N] [--top 20]`

Three-phase fault level under N-1: for the intact network and then each single-branch outage in index order, the outage
copy is built and the IEC 60909 initial symmetrical current Ik'' is evaluated at every bus (or the `--buses` list). Each
fault is restricted to the fault bus's own island, so an outage that splits the network leaves a bus in an island with no
source with no value in that state (counted in `states_without_source`, never a number). Per bus the report gives the
intact value, the largest and smallest Ik'' over the states in which the bus has a source and the state that sets each
(`intact` or `branch <idx> <name>`; a tie names the first state). The outage states are independent, so `--parallel`
returns the same bits as the serial run. Needs a base kV at every fault bus, as `iec60909`.

| Flag | Default | Meaning |
|---|---|---|
| `--buses` | every bus | Comma list of fault bus ids. |
| `--branches` | all in service | Comma list of 0-based branch indices to outage. |
| `--objective` | max | `max` (voltage factor c_max) or `min` (c_min), as `iec60909`. |
| `--parallel` | 1 | States evaluated at once; 0 means one per processor. |
| `--top` | 20 | Buses to list on the console, largest maximum first (the result file has every bus). |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary and a `buses` table.
