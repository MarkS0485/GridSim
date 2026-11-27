### taps

`gridsim taps <case> [--target 1.0]`

Coordinated tap schedule (`TapOptimizer.Flatten`): one set of tap ratios across every OLTC that minimises the sum of
squared bus-voltage deviations from `--target`, rounded to each OLTC's step grid. Prints the objective before and after
and each transformer's current and recommended tap.

Every OLTC record must name exactly one branch of the case and control a bus the case has; otherwise the run exits 1
before any solve, naming each bad record (an OLTC that matched no branch used to be dropped from the schedule without a
word). The schedule minimises every bus's deviation from the one `--target`, so it can leave the bus an OLTC controls
outside that OLTC's own target plus or minus its deadband, where its local regulator would move the tap away from the
schedule: such OLTCs are listed after the schedule (`<n> OLTC(s) would leave this schedule: ...`, with the controlled
bus, its voltage and the OLTC's own target and deadband); nothing is printed when every OLTC accepts the schedule. The
tap search (downhill simplex, at most 2,000 iterations) must meet its convergence test: when it stops at the cap the
schedule found is still printed, stderr says it is not a proven optimum, and the run exits 3 (it used to exit 0). Exit
1 when the case has no OLTCs or an OLTC record does not resolve, exit 3 when the base case does not converge or the
search does not converge. The result file adds `optimizer_converged`, `optimizer_iterations` and a `regulation` table
(`transformer,controls_bus,vm_pu,target_pu,deadband_pu,within_deadband`).

| Flag | Default | Meaning |
|---|---|---|
| `--target` | 1.0 | Target bus voltage (pu); above 0. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).
