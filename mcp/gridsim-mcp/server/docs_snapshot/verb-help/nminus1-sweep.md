### nminus1-sweep

`gridsim nminus1-sweep <case|--dir <path>|--all> [--parallel N] [--sample N] [--losses] [--global-vlimits] [--out <path>]`

Parallel N-1 branch sweep with throughput reporting, over one case or the whole case registry (`--all` is the embedded
classics plus every `.m` under `data/matpower/`). Writes Markdown, CSV and `<report>.run-metadata.json` beside the
report ([Output files](#output-files)). Each case is screened
with the contingency option set and the tap-changer loop on the shared dense / sparse rule
([Solver selection](#solver-selection)). The base case is solved first; its violation count is the last column, `Base
viol` (CSV `base_violations`, after `commit`), and those violations are netted out of every outage as for
[nminus1](#nminus1). `Islanded` counts the outages that split the network and `Not solved` the connected outages with
no solution (CSV `islanded` and `not_solved`, the last column). A base case that does not converge gives a `BASE FAIL`
row. Bus voltages are judged as [nminus1](#nminus1) judges them: against each bus's own limits where the case gives
them (the default), or against the operational band with `--global-vlimits`, so the sweep and `nminus1` give the same
verdicts on the same case (the header line records `vlimits=per-bus` or `vlimits=global`). Before October 2026 the
sweep always judged the band, so on a case with per-bus limits it disagreed with `nminus1`. Exit 1 if no case loaded or after a partial load failure; otherwise exit 3 when any case is not N-1 secure (a
base-case violation, an outage that violates, islands or does not solve, or a `BASE FAIL`), and 0 when every case is.

| Flag | Default | Meaning |
|---|---|---|
| `<case>` / `--dir` | `case118` | Case name, `.m` file or case folder, or a JSON case directory ([Case arguments](#case-arguments)). |
| `--all` | off | Sweep the whole registry (exit 2 with a case or `--dir`). |
| `--parallel` | 0 (all cores) | Worker threads; a negative value is refused. |
| `--sample` | 0 (full) | Screen an evenly spaced sample of N branches (logged, flagged `(sampled)`). |
| `--losses` | off | Also run the loss-contingency sweep and append the 20 outages that raise network losses most. |
| `--global-vlimits` | off | Judge every bus against the operational band 0.94-1.06 pu, ignoring the case's per-bus limits (see [nminus1](#nminus1)). `--bus-vlimits` is accepted as a no-op alias of the default. |
| `--bus-vlimits` | on | The default (each bus against its own limits), accepted as a no-op so existing scripts keep working; with `--global-vlimits` it exits 2. |
| `--out` | `results/nminus1-sweep.md` | Report path (CSV alongside). |
