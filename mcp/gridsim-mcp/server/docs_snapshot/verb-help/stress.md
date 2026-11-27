### stress

`gridsim stress <case|--dir <path>|--all> [--from F] [--to F] [--step F] [--parallel N] [--out <path>] [--pv-out <file.csv>]`

Load-to-collapse: scale the operating point until the flow stops converging and report max loadability. Each load
point solves with the contingency option set and the tap-changer loop ([Solver selection](#solver-selection));
Q-limits are enforced so collapse is real reactive exhaustion. Writes Markdown, CSV and `<report>.run-metadata.json`
beside the report ([Output files](#output-files)). Exit 1 if
no case loaded or after a partial load failure; exit 2 for a bad `--step` or `--from` above `--to`; exit 3 when any case
has no converged load point at all (its row reads `<none>` and `NO LOAD POINT CONVERGED`), unless exit 1 already applies.
A load point whose solve fails numerically (a singular or non-finite Jacobian, typically past the nose) counts as not
converged and is counted in the Notes column and on stderr; any other exception stops the run.

Max loadability is the highest load factor that converged before the first one that did not. The rule is the shared
loadability loop the [`cpf`](#cpf) verb also runs (`ContinuationPowerFlow.ScanGrid` here, on this verb's fixed grid of
factors, every point solved in parallel). The two verbs answer different questions on purpose: `stress` re-dispatches
the generators at each load factor (`GridScenario.AtLoad`) and steps through a fixed grid, while `cpf` lets the slack
absorb the extra load and bisects to the nose.

| Flag | Default | Meaning |
|---|---|---|
| `<case>` / `--dir` | `case118` | Case name, `.m` file or case folder, or a JSON case directory (`--all` sweeps the registry; exit 2 with a case or `--dir`). |
| `--from` / `--to` / `--step` | 0.5 / 2.0 / 0.05 | Load-multiplier sweep. `--step` must be positive and `--from` must not be above `--to` (exit 2 otherwise). |
| `--parallel` | 0 (all cores) | Load points solved in parallel; a negative value is refused. |
| `--out` | `results/stress.md` | Report path (CSV alongside). |
| `--pv-out` | - | Also write the PV curves: for each case, every bus voltage at each load factor from `--from` up to the reported max loadability (the converged factors before the first failure), as `case,load_factor,bus_id,vm_pu`. A name not ending in `.csv` exits 2. |
