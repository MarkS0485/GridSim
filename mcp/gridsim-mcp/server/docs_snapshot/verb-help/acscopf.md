### acscopf

`gridsim acscopf <case> [--mode preventive|corrective] [--ramp-mw <MW> | --ramp-file <file.json>] [--contingencies all|<i,j,...>] [--outages <file.json> | --n2 pairs|<file.json> [--max-outages 5000]] [--post-rating A|B|C] [--objective cost|losses] [--max-rounds 20] [--max-iter 150] [--out <file.csv|file.json>]`

AC security-constrained optimal power flow (`AcScopf`). The base case and each secured branch outage are network
blocks of one stacked AC-OPF, solved by the MIPS interior point method of `acopf-full`. Preventive: every unit not at a
slack bus keeps its base-case output after the outage. Corrective: each such unit may move by at most its ramp
(`--ramp-mw` for every unit, or `--ramp-file`, `{"format": "gridsim-opf-ramps/1", "rampMw": [...]}` with one ramp per
in-service unit). Slack-bus units take the change in losses; reactive output and voltages are free within their limits
in every block (post-outage voltage control is allowed). The objective is the base case's. Post-outage branch limits use
the rating rule of [`scopf`](#scopf).

Outages enter by constraint generation: after each solve every listed outage not yet in the problem is screened with an
AC Newton power flow (the base-case dispatch, the base-case voltages as set-points, reactive limits on, the slack taking
the losses); every outage that does not converge, breaks a post-outage rating by more than 1e-6 MVA or a voltage limit
by more than 1e-6 pu is added, in index order, and the problem is solved again from scratch, until none is added. An
outage that islands the network is listed and not secured. Then every secured outage is screened again at the final
dispatch (in corrective mode with its block's unit outputs). Its block let the unit voltages move after the outage and
the screen holds them, so one can still fail; the run then ends `SecuredOutageFailsScreen`, naming those outages.

Generator and N-2 outages: `--outages <file.json>` reads a list of outages, each removing branches, units or both:

```
{ "format": "gridsim-scopf-outages/1",
  "outages": [ { "branches": [3, 4], "name": "double circuit" }, { "generators": [1] }, { "branches": [2], "generators": [0] } ] }
```

Branches are indices into the case's branch list and units into its in-service unit list, both from 0. In a block the
removed units' P and Q are held at 0 and have no coupling row. Their output is shared by governor droop: each remaining
synchronous unit with a dispatch range takes a fixed share in proportion to its effective MVA base (its MBASE, or the
system base when it has none), held there (preventive) or within its ramp
of it (corrective), and the units at the reference bus take their own share and the change in losses. Each unit's limits
hold after the outage, so it keeps room for its share. An outage of every unit at a slack bus moves the reference to the
bus of the largest remaining unit; taking out every unit is refused. Power-flow-only units and converter-interfaced
units (wind, solar, battery) take no share, as in the contingency screen's governor-droop mode. The screen of an outage that removes a unit shares
the output the same way, and fails when a unit's share takes it outside its range or the reference bus's P or Q leaves
its units' range.
`--n2 pairs` makes every pair of the `--contingencies` branches (or of every in-service branch) in index order; more than
5000 pairs are refused unless `--max-outages` raises the cap. `--n2 <file.json>` reads an outage file whose every entry
is a pair of branches. With either flag the outage numbers in the output are positions in the list, and the result
table gains an `outage` / `label` row for each.

Prints each round (outages in the problem, iterations, objective, outages added), the secured, screened-ok and islanding
outages, the base-case optimum, each secured block's dispatch and binding limits, and the binding ramp limits. Exit 0
optimal. Exit 1 for input it refuses (a bad outage list, outage file or ramp file, an outage of every unit, a pair
count above the cap, the reasons of `acopf-full`). Exit 2 for `--outages` with `--n2` or `--contingencies`, or
`--max-outages` without `--n2`. Exit 3 when a solve
does not converge, the round cap is reached or a secured outage fails its screen at the final dispatch; no dispatch is
printed.

Result files: `--json <file.json>` or `--csv <file.csv>` write the result to a file through the shared writer ([Result files](#result-files)): the `summary` rows as summary values and every row of the result table as the table `rows`; a run that is not optimal writes nothing. `--out` is unchanged: it still names its own result table, not a shared-writer file.

| Flag | Default | Meaning |
|---|---|---|
| `--mode` | preventive | `preventive` or `corrective`. |
| `--ramp-mw` | none | Corrective: the ramp (MW) of every unit. |
| `--ramp-file` | none | Corrective: a `gridsim-opf-ramps/1` file with one ramp per in-service unit. |
| `--contingencies` | all | `all`, or a comma list of branch indices (from 0). With `--n2 pairs`, the branches to pair. |
| `--outages` | none | A `gridsim-scopf-outages/1` file of outages (branches, units or both) in place of `--contingencies`. |
| `--n2` | none | `pairs` (every pair of the listed branches) or a `gridsim-scopf-outages/1` file of branch pairs. |
| `--max-outages` | 5000 | With `--n2`: the most pairs allowed. |
| `--post-rating` | A | The rating after an outage: A, B or C (A where B or C is not given). |
| `--objective` | cost | `cost` or `losses` of the base case. |
| `--max-rounds` | 20 | The most solves constraint generation may take; at least 1. |
| `--max-iter` | 150 | Iteration cap of each interior point solve; at least 1. |
| `--out` | none | Write the result table as CSV, or as JSON for a `.json` file. |
