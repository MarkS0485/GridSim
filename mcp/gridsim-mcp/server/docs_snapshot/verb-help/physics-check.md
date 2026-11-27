### physics-check

`gridsim physics-check <case|case.m|case-dir> [--tol MVA]`

Audit one solved state against the physical laws (energy balance, Ohm, Joule, KCL, complex power, and the rest of the
registry) and print one row per law. The gate is the hard laws (1, 2, 3, 6, 8 in `PhysicsCheckRegistry.HardGateLaws`)
AND a converged solve: `GATE: PASS` means the power flow converged and the state is physically admissible. The header
prints `converged=<true|false>` and the tolerance. A solve that did not converge prints its laws for diagnosis, then
`GATE: FAIL  -  the power flow did not converge (status ..., max mismatch ... pu after N iterations)` and exits 3. A
converged state that fails the gate prints `GATE: FAIL  -  physically-impossible state. Worst: law N <name>: residual
R <unit> above the tolerance T <unit> (<detail>)` and exits 1. Exit 1 also on a load error, 2 without a case or for a
bad `--tol`.

The reactive balance (law 8) is checked at every bus the solve ended as PQ, including a PV bus pinned at a reactive
limit (checked against that limit) and a PV bus with no generator. Law 9 (synchronism) reads `NotApplicable` with
`NOT SCORED` in its detail: a power flow has no trajectory.

| Flag | Default | Meaning |
|---|---|---|
| `--tol` | BaseMva x 1e-8 | Residual tolerance (MVA), one absolute line for every law. The default is the power-flow convergence tolerance (1e-8 pu) on the case's own base: 1e-6 MVA on a 100 MVA case, 1e-4 MVA on the 10,000 MVA `uk`. A negative value exits 2. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).
