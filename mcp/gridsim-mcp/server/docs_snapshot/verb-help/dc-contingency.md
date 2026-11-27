### dc-contingency

`gridsim dc-contingency <case> [--branches <idx,idx,...>] [--post-rating A|B|C] [--top 20]`

DC N-1 screen: one factorisation of B', the base DC flows (in-service generation minus demand, each DC island's reference
absorbing its imbalance, as `dcpf`), then for each outage `flow_after = flow_before + LODF * flow_outaged` in MW. It reports DC MW and never
claims AC MVA. Limits follow the RC3 effective-rating rule: the intact state against RateA, an outage state against RateB
or RateC when `--post-rating` asks for it and the branch has one, else RateA (the rating source, `A`, `B`, `C` or
`fallback A`, is in the violations table). A branch with no RateA is not monitored (counted in `unrated_branches`). A
bridge outage is `ISLANDED` (the LODF is undefined; threshold 1e-9) and no algebra is done. A row reads `SECURE`,
`VIOLATIONxN` or `ISLANDED`; limits are absolute, so a violation the base case already has appears on the base row and on
every outage row. Exit 0 when the base case and every outage are `SECURE`; exit 3 (`NOT SECURE`) when any violates or
islands; exit 1 when the DC model cannot be formed.

| Flag | Default | Meaning |
|---|---|---|
| `--branches` | all in service | Comma list of 0-based branch indices to outage; an out-of-service or repeated branch exits 2. |
| `--post-rating` | A | `A`, `B` or `C`: the rating an outage state is judged against. |
| `--top` | 20 | Rows to list on the console (the result file has every row). |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary, an `outages` table (the base case is outage -1) and a `violations` table.
