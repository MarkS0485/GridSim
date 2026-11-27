### nminus2

`gridsim nminus2 [<case>] [--dir <path>] [--candidates N] [--exhaustive] [--top N] [--global-vlimits]` (alias `n-2`)

N-2 branch screening: two elements lost at once. A full pairwise sweep is C(n,2), so by default it screens N-1 first,
ranks the single outages that solve by how hard they stress the system, keeps the `--candidates` most-stressed, and
pairs only those (fewer when fewer solve). The single outages that do not solve (islanded or no solution) are left out
of the pairing and listed under the table (`N single outage(s) left out of the pairing because they already fail at
N-1`). Results are printed worst first. Default model `data/uk`. Exit codes as for [nminus1](#nminus1).

| Flag | Default | Meaning |
|---|---|---|
| `<case>` / `--dir` | `data/uk` | Case to load, or a JSON case directory ([Case arguments](#case-arguments)). |
| `--candidates` | 20 | How many of the most-stressed single outages to pair (ignored with `--exhaustive`). |
| `--exhaustive` | off | Enumerate every pair; use only on small cases. |
| `--top` | 20 | Rows to print (all pairs are still screened and counted). |
| `--post-fault-rating` | A | Judge the outage states against rating A, B or C (see [nminus1](#nminus1)). |
| `--ras` | off | Run the remedial action schemes of a `gridsim-ras/1` file inside each pair (see [nminus1](#nminus1)). |
| `--global-vlimits` | off | Judge every bus against the operational band 0.94-1.06 pu, ignoring the case's per-bus limits (see [nminus1](#nminus1)). `--bus-vlimits` is accepted as a no-op alias of the default. |
| `--bus-vlimits` | on | The default (each bus against its own limits), accepted as a no-op so existing scripts keep working; with `--global-vlimits` it exits 2. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).
