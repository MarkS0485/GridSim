# Solve export - `--enableExport`

Dump **everything a single power-flow solve computes** to one JSON document: the run metadata, the
full network inputs, the solver's internal trace (admittance matrix, per-unit bus specification,
every iteration's mismatch/correction/Jacobian, and the reactive-limit switching), and the converged
state with all line flows. Intended for validation, cross-checking against other tools (MATPOWER,
pandapower), regression fixtures, and teaching.

## Usage

```bash
# Dense/sparse/BFS is auto-selected exactly like a normal solve; one traced solve is written.
gridsim case118 --enableExport                      # -> ./exports/case118-solve.json
gridsim case9  --enableExport=out/case9.json        # explicit path
gridsim data/matpower/dist/case33bw.m --enableExport   # radial -> BFS engine
gridsim case300 --enableExport --export-jacobian     # force the Jacobian past the size gate
gridsim <case> --gpu --enableExport                  # trace a GPU-backend solve
```

| Flag | Meaning |
|---|---|
| `--enableExport[=PATH]` | Run one traced solve and write JSON. Default path `exports/<case>-solve.json`. |
| `--export-jacobian` | Capture the dense Jacobian every iteration even for large systems (off by default: it is O(n^2) and dwarfs the file; captured automatically for `n <= 300`). |

The engine is picked the same way as any solve (radial -> backward-forward sweep, large -> sparse,
else dense; `--gpu` selects the accelerator backend). Zero overhead when the flag is absent - the
recorder is only attached when exporting.

## Schema (`gridsim-solve-export/1`)

```jsonc
{
  "schema": "gridsim-solve-export/1",
  "meta":   { "Case", "Backend", "GitCommit", "TimestampUtc", "ElapsedMs", "Machine" },
  "model": {                       // the full network as solved
    "name", "baseMva", "frequencyHz", "busCount",   // frequencyHz since October 2026 (R177)
    "buses":      [ { "id", "type", "baseKv", "pd", "qd", "gs", "bs", "vm" } ],
    "generators": [ { "bus", "pg", "qg", "vg", "qmin", "qmax", "pmax", "mBase", "h", "type" } ],
    "branches":   [ { "from", "to", "r", "x", "b", "tap", "phaseShiftDeg", "rateAMva", "name", "isTransformer" } ]
  },
  "solve": {
    "solver": "dense-newton | sparse-newton | bfs",
    "n", "converged", "iterations", "maxMismatch", "jacobianOmittedForSize", "slackQNote",
    "ybus":    [ { "I", "J", "G", "B" } ],   // non-zero admittance entries (empty for BFS - no Y-bus is formed)
    "busSpec": [ { "index", "id", "type", "vmInit", "vaInitDeg", "pspec", "qspec", "qd", "vg", "qmin", "qmax" } ],
    "qLimitEvents": [ { "busIndex", "busId", "pinnedAt", "pass" } ],
    "passes": [ {                            // one per reactive-limit outer pass
      "pass", "effectiveTypes", "pvpqIndices", "pqIndices",
      "iterations": [ {
        "iteration", "maxMismatch",
        "mismatch":   [ ... ],   // ordered [P over pvpqIndices, then Q over pqIndices]
        "correction": [ ... ],   // same ordering (raw Newton step; BFS records per-bus |deltaV|)
        "jacobian":   [ [ ... ] ]  // present for dense within the size gate; null otherwise
      } ]
    } ]
  },
  "result": {
    "totalLossMw", "totalLossMvar",
    "buses":     [ { "id", "vm", "vaDeg", "pInjMw", "qInjMvar" } ],
    "lineFlows": [ { "from", "to", "pFromMw", "qFromMvar", "pToMw", "qToMvar", "pLossMw", "qLossMvar", "name", "ratingMva", "loadingPercent" } ]
  }
}
```

### Reading the per-iteration vectors

`mismatch[k]` and `correction[k]` share an ordering defined by the pass: the first `pvpqIndices.Length`
entries are the **P** equations (over the non-slack buses), the remainder are the **Q** equations
(over the PQ buses). So `pvpqIndices[k]` is the bus index of `mismatch[k]` for the P block, and
`pqIndices[k - pvpqIndices.Length]` for the Q block. The `jacobian` (when present) is the square
matrix in that same row/column order, captured *before* the linear solve factorises it.

### Notes

- **BFS**: forms no admittance matrix (it sweeps branch-by-branch), so `ybus` is empty - the branch
  impedances are in `model.branches`. Each `iteration` records the per-bus voltage change `|deltaV|` as
  `mismatch` (the quantity BFS drives to zero); `correction` is empty.
- **Determinism**: given a deterministic solve, the JSON is byte-for-byte reproducible apart from the `meta` block (the
  tests assert this for a fixed meta). A CLI export's `meta` records when, on which machine and how long the run took
  (the UTC time, the elapsed milliseconds, the machine name and OS), so two CLI runs differ there and only there.
- The recorder captures *observationally* - attaching it does not change the solution (regression
  test: dense and sparse produce bit-identical voltages with and without a trace).
