### restore

`gridsim restore <case> --order <b1,b2,...> [--vmin 0.90] [--vmax 1.10] [--qtol 1.0]`

Restoration (black-start) sequence check (`RestorationSequencer.Evaluate`). The branches named in `--order` start open,
whatever their stored state, and are energised one per step in that order; every other branch keeps its state in the
case. Each step re-solves the live (slack-connected) network with generator reactive limits enforced and the live
on-load tap changers regulating, then reports the lowest and highest bus voltage, the buses outside [`--vmin`,
`--vmax`], and the generator buses whose reactive output is within `--qtol` Mvar of a limit (the machine can absorb or
supply no more, which is what makes an energisation step unsafe, for example a Ferranti over-voltage on a lightly
loaded line). A step is feasible when it solves with no band breach and no generator at a limit.

A branch is named by its name in the case, or by `#k`, its 1-based row in the case's branch table (MATPOWER files carry
no branch names). Exit 0 when every step is feasible; 3 when any step does not solve or is not feasible (the table is
still printed, with the first unsafe step named); 1 for a branch that is not in the case, a branch named twice, a `#k`
outside the table or one that is also another branch's name, or a load error; 2 without `--order`, for an empty entry in
the list, or for a bad flag value.

| Flag | Default | Meaning |
|---|---|---|
| `--order` | - (required) | The energisation order: branch names or `#k` rows, comma-separated, no empty entries. |
| `--vmin` / `--vmax` | 0.90 / 1.10 | The voltage band checked at each step (pu; the statutory band of `VoltageLimits`). `--vmin` above 0, `--vmax` above `--vmin`. |
| `--qtol` | 1.0 | A generator bus whose reactive output is within this many Mvar of its summed Qmin or Qmax counts as at its limit; 0 or more. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): one row per step, with null voltages for a step that did not solve.
