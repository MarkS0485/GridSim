### pv-contingency

`gridsim pv-contingency <case> [--branches <idx,idx,...>] [--step 0.25] [--min-step 1e-3] [--max-steps 200] [--parallel N] [--top 20]`

Maximum loadability (the nose of the PV curve) under each single-branch outage: the `cpf` walk (load multiplier stepped
up and bisected, every point solved with the contingency study solver and options) run on the base case and on the outage
copy of each branch in index order (every in-service branch, or the `--branches` list in the order given). An outage that
splits the network is classified from the topology first and printed `ISLANDED` (no solve); an outage state that does
not converge at multiplier 1 is `NO SOLUTION` (its multiplier is then the highest lower one that does, as `cpf` prints for a base case that does not solve, with a negative margin). A row's `bracketed` flag is the walk's own statement: false means the
multiplier is only a lower bound. The items are independent and each has its own solver, so `--parallel` returns the same
rows with the same bits as the serial run. Exit 3 when the base case does not converge or any outage is `NO SOLUTION`.

| Flag | Default | Meaning |
|---|---|---|
| `--branches` | all in service | Comma list of 0-based branch indices to outage; an out-of-service or repeated branch exits 2. |
| `--step` | 0.25 | Initial multiplier step, as `cpf`. |
| `--min-step` | 1e-3 | Bisection stops below this step; in (0, `--step`]. |
| `--max-steps` | 200 | Cap on solves per walk; at least 1. |
| `--parallel` | 1 | Outages walked at once; 0 means one per processor. |
| `--top` | 20 | Outages to list on the console, lowest loadability first (the result file has every row). |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary and an `outages` table.
