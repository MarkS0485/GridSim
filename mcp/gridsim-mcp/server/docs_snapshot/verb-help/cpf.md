### cpf

`gridsim cpf <case> [--step 0.25] [--min-step 1e-3] [--max-steps 200] [--pv-out <file.csv>]`

Maximum loadability by stepping and bisection (`ContinuationPowerFlow.MaxLoadability`). Despite the verb's name it is
not a predictor-corrector continuation power flow (no tangent predictor, no parameterised corrector, no tracing round
the nose): it scales every bus's P and Q demand by a multiplier lambda, with the slack absorbing the extra generation,
solves an ordinary power flow at each lambda, steps lambda up while it converges and, on a failure, backs off and halves
the step. The answer is the largest lambda at which the Newton power flow still converges, a slightly conservative
estimate of the voltage-collapse nose (Newton fails as the Jacobian becomes singular). Prints the largest converged
lambda (round-trip precision) and the margin above base load. The line ends `nose bracketed to <min-step>` only when
the walk ended because its step fell below `--min-step`. When `--max-steps` runs out first it says `NOT bracketed:
--max-steps <n> ran out first; the nose lies between this lambda and <the smallest lambda that failed>` (or, when every
solve converged, that lambda is only a lower bound), the run explains on stderr and exits 3; before October 2026 it
printed `nose bracketed` either way and exited 0. Exit 3 when no lambda converges. A case whose base load (lambda 1) does not converge but whose lower loads do prints
the lambda below 1 and the negative margin (how far the load must fall before the case solves), then says on stderr
that the base case did not converge, and exits 3: the case as given has no loadability margin (`cpf data/intl/france`:
lambda 0.986328125, margin -1.37%, exit 3). This answers a
different question from `stress`, which re-dispatches generation at each load factor on a fixed grid of factors; the
two definitions are deliberately distinct. What counts as a converged point is the same for both: every point is
solved with the contingency option set and the tap-changer loop ([Solver selection](#solver-selection)), on the
selected backend. `cpf case9` prints `max loadability lambda = 2.373046875` and `cpf uk` `6.052734375`.

| Flag | Default | Meaning |
|---|---|---|
| `--step` | 0.25 | Initial lambda step; above 0. |
| `--min-step` | 1e-3 | Bisection stops below this step (the nose is bracketed to it); in (0, `--step`]. |
| `--max-steps` | 200 | Cap on power-flow solves; at least 1. Running out before the nose is bracketed to `--min-step` exits 3 (the lambda printed is then a lower bound). |
| `--pv-out` | - | Also write the PV curves: every converged point's voltage at every bus, one row each, as `case,lambda,bus_id,vm_pu`, in the order solved (lambda rises strictly). The points come from the same loop, so the printed lambda is the same. A name not ending in `.csv` exits 2. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)); the summary carries `nose_bracketed`, `solves` and `lowest_failed_lambda` (null when
every solve converged).
