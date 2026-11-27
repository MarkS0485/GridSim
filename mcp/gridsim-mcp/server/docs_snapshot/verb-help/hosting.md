### hosting

`gridsim hosting <case> [--study <file.json>] [--method snapshot|qsts] [--resource pv|load] [--buses all|<ids>] [--max-mw X] [--tolerance-mw T] [--snapshots d:r,...] [--resource-profile NAME] [--pv-function unity|pf|volt-var|volt-watt] [--mva-per-mw 1] [--load-pf 1] [--reverse-branch auto|none|NAME] [--reverse-allowance-mw 0] [--allowed-violation-s 0] [--tap-increase-pct P] [--criteria list] [--threads N] [--top 20]`

Hosting capacity (`HostingCapacity`): the largest PV (or, with `--resource load`, added demand such as electric vehicle
charging or heat pumps) at each candidate bus before a limit is broken, found by bisection on the size in MW. Each
candidate is studied on its own, with the resource added at that bus only.

Methods:

- `snapshot` (the default): one regulated power flow per snapshot, each a demand multiplier (on every bus with demand)
  and a resource multiplier (PV availability or the added load's level). Default snapshots: for PV, minimum demand
  `0.3:1` and full demand `1:1`; for load, `1:1`. Tap changers and capacitor banks of the study (or the case's OLTCs)
  settle instantly (every delay is taken as zero).
- `qsts`: the whole time series of `--study` (a `gridsim-qsts/1` file, see [`qsts`](#qsts)) with the resource added,
  driven by `--resource-profile` (a profile of the study; constant 1 when not given). A bus or branch may be outside its
  limits for `--allowed-violation-s` seconds before it counts, and `--tap-increase-pct` adds the tap-operation
  criterion: the run may make at most that percentage more tap operations than the base run.

Constraints (all by default; `--criteria` takes a comma list of `overvoltage`, `undervoltage`, `thermal`,
`reverse-power`, `tap-operations`, `non-convergence`): a bus above or below the study's voltage limits (default 0.94 and
1.06 pu), a rated branch above the loading limit (default 100 % of RATE_A), active power flowing back through the
`--reverse-branch` (the protection proxy: feeder protection and the source tap changer are set for forward flow;
`auto`, the default, takes the first named branch leaving the slack bus; `none` turns it off), more tap operations than
allowed, and a power flow that does not converge. A limit the base case (no added resource) already breaks counts only
where the resource makes it strictly worse, so an existing problem does not mask the capacity and is not blamed on the
resource; such limits are listed.

Search: `--max-mw` (default twice the case demand, at least 1 MW) is tried first; if it passes, the capacity is reported
as above it. Otherwise the bracket [0, max] is halved (mid = lo + (hi - lo) / 2) until it is at most `--tolerance-mw`
wide (default max / 1024). The reported capacity is the passing end, the "fails at" size the failing end, and the
limiting constraints are those broken at the failing end; for a limit that grows steadily with the size, the true
capacity lies between the two. The same sizes are evaluated in the same order on every run; `--threads` studies several
candidates at once (each with its own engine) without changing any number.

Prints the method, search, limits and any base-case violations, then one row per candidate (lowest capacity first):
the capacity, the failing size and the limiting constraint with where and how far.

Exit 0 when the study ran (a location limited within the range is a result). Exit 1 when the study is refused (every
problem is listed) or the base case does not solve. Exit 2 for a usage error (`--method qsts` without `--study`, a bad
flag value).

| Flag | Default | Meaning |
|---|---|---|
| `--study` | none | The `gridsim-qsts/1` study: the time series (qsts method), and the controls and limits (both methods). |
| `--method` | snapshot | `snapshot` or `qsts`. |
| `--resource` | pv | `pv` or `load`. |
| `--buses` | all | Candidate buses: `all` (every PQ bus) or a comma list of bus ids. |
| `--max-mw` | 2 x demand | Top of the search (MW). |
| `--tolerance-mw` | max / 1024 | Width of the final bracket (MW). |
| `--snapshots` | see above | Snapshot conditions as `demand:resource` pairs. |
| `--resource-profile` | constant 1 | The study profile driving the resource (qsts method only). |
| `--pv-function` | unity | The hosted PV's inverter function: `unity`, `pf`, `volt-var` (IEEE 1547-2018 Category B) or `volt-watt`. |
| `--mva-per-mw` | 1 | The hosted PV's apparent power rating per MW (at least 1). |
| `--load-pf` | 1 | The hosted load's power factor (lagging). |
| `--reverse-branch` | auto | The reverse-power branch: `auto`, `none` or a branch name. |
| `--reverse-allowance-mw` | 0 | Reverse power allowed before it counts (MW). |
| `--allowed-violation-s` | 0 | Time a bus or branch may spend outside its limits (qsts method, s). |
| `--tap-increase-pct` | off | Tap operations allowed above the base run (qsts method, %). |
| `--criteria` | all | The constraints judged (comma list). |
| `--threads` | 1 | Candidates studied at once. |
| `--top` | 20 | Rows to list. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary (`case`, `buses`, `engine`, `resource`, `method`, `max_mw`, `tolerance_mw`,
`candidates`, `evaluations`, `min_hosting_mw`, `base_violations`) and the tables `locations` (`bus_id,hosting_mw,
failing_mw,limited,limiting,detail,evaluations`; `failing_mw` is Infinity where the maximum passed), `limiting`
(`bus_id,constraint,element,value,detail`: every constraint broken at the failing size) and `base_violations`
(`constraint,element,value,detail`).
