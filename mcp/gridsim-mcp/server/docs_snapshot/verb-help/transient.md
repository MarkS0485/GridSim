### transient

`gridsim transient <case|--cases a,b,c|--cases suite> [flags]`

Headless demanding time-domain run: a simulated duration at a fine slice, integrating swing-equation frequency each
slice. Writes `timeseries.csv`, sampled solve-math exports (`exports/slice-*.json`), `summary.md` and
`run-metadata.json`. `--cases suite` (or `all`) runs case9, case14, case30, case57, case118, case300 and
`pegase/case1354pegase.m`. The global `--forming` (with `--forming-inertia` and `--forming-damping`) and
`--asset-dynamics` switches are honoured: they reach the live frequency integrator, and the run metadata names them.
`--asset-dynamics` on a case with no asset bundle exits 1 for that case. The frequency is integrated at the case's own
nominal frequency (case9 and the two-area case at 60 Hz, every GB and MATPOWER-file case at 50 Hz unless
`--frequency-hz` says otherwise), so a 60 Hz run's `freq_hz` column and nadir sit about 60 Hz. With
`--asset-dynamics`, inverter Loss-of-Mains trips book the dispatch actually running, and both those trips and the
protection devices' demand sheds are restored by secondary redispatch over about 5 minutes, as a `--trip` loss is.

| Flag | Default | Meaning |
|---|---|---|
| `<case>` | `case9` | Case name, or a `.m` file under `data/matpower/`. |
| `--cases` | - | Comma list, or `suite` / `all`. |
| `--minutes` (alias `--duration`) | 60 | Simulated duration in minutes; above 0. |
| `--dt-ms` | 1.0 | Slice length, milliseconds; above 0. |
| `--log-every` | 1 | Write every N-th slice to the CSV; at least 1. |
| `--export-every` | 0 (off) | Full solve-math export every N-th slice (slice 1 and the trip window always export). |
| `--export-jac` | off | Include the Jacobian in exports. |
| `--trip` | 0 (none) | Infeed MW to trip mid-run. |
| `--trip-at` | 0 | Trip time (seconds). Exit 2 if past the run end: the contingency would never fire, or without a `--trip` above 0 (nothing to time). |
| `--trip-window` | 2.0 | Seconds each side of the trip always logged and exported. Exit 2 without a `--trip` above 0. |
| `--solver` | `auto` | `dense`, `sparse`, `bfs` / `sweep` or `auto` (any other value exits 2). `auto` sends a radial network to the sweep, uses sparse above 1,500 buses (the `LiveSim` per-tick threshold) and dense otherwise. |
| `--dyn-data` | - | A machine dynamic data file (`gridsim-dynamics/2`, as for [`trip`](#trip)): each generator it binds takes the file's inertia H for the live swing (the only per-machine number it integrates); a stderr line says so, and the run metadata and summary header add `dyn-data=<file> (inertia H only)`. One case only: with `--cases` it exits 2. A missing or invalid file exits 1, before the run. |
| `--out` | `results/transient-<case>-<stamp>` | Output directory (batch runs use `results/transient-<stamp>/<case>`). |

Exit 1 if no case loaded at all, if some cases of a batch failed to load, for `--asset-dynamics` on a case with no
asset bundle, or for a `--dyn-data` file that is missing or invalid; exit 2 for a bad `--dt-ms`, `--minutes`
or `--solver`, a `--trip-at` past the run end, `--trip-at` or `--trip-window` without a `--trip` above 0, or `--dyn-data` with `--cases`; exit 3 if any slice failed to converge (frequency and RoCoF from
unconverged states are not trusted).
