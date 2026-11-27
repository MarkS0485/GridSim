### acopf

`gridsim acopf <case> [--loss-tol 1e-3] [--max-iter 20] [--top 12] [--costs case|<file.json>] [--prices]`

DC dispatch with an AC loss loop (`AcOptimalPowerFlow`): NOT a full AC optimal power flow. It runs the DC-OPF dispatch
inside an outer loop that inflates demand by the AC losses, re-dispatches and re-solves the AC power flow until the
losses change by less than `--loss-tol` MW, so the dispatch pays for its own losses. Bus voltages, reactive power and
AC branch flows are neither optimised nor limited. The header says so: `ACOPF (DC dispatch + AC loss loop) - <case>
(<n> buses)  [not a full AC-OPF: no voltage, reactive-power or AC branch-flow limits]`. Prints the iterations, losses,
cost and dispatch. Exit 3 if the run does not converge, exit 1 if the solve throws.

A run that does not converge says which of three causes stopped it: `the AC power flow at the DC-OPF dispatch of loss
pass <k> ... did not converge` (with the solver status, iterations, largest mismatch and the lowest voltage where it
stopped; the DC dispatch ignores voltages and reactive power, so it can be a point the network cannot carry within the
units' reactive limits), `the losses settled after <n> pass(es), but the DC-OPF dispatch of the final pass is
infeasible` (run `dcopf` to see why), or `the losses did not settle within <n> pass(es)`. A run whose first DC dispatch
is infeasible in a way the loss loop cannot repair stops there, before any AC solve, with `the DC-OPF dispatch of loss
pass 0 (passes count from 0) is infeasible ..., and no later pass can be feasible` (exit 3). The loop only ever raises
demand, so this is decided only where it is proved: demand above the units' total Pmax, an island with demand and no
unit, or a security LP that is Infeasible while every unit may produce nothing (Pmin 0 or below), and only when no
branch resistance or bus shunt conductance is negative. Demand below the units' total Pmin, an LP that stalled, or line
limits with a unit whose Pmin is above 0 still run the full loop. When every unit is priced the same (a case with no
fuel types, such as every MATPOWER file), the first message adds that the dispatch is one arbitrary optimum of a
degenerate LP and that `--costs case` dispatches on the case's own cost curves. `acopf case118` and `acopf case300`
stop at pass 0 (the flat 50 GBP/MWh dispatch puts 25 and 50 units at Pmax, and the AC solve with reactive limits stops
with lowest voltages 0.7966 and 0.7806 pu); with `--costs case` `acopf case118` converges in 4 passes (losses
132.504 MW). `acopf gb-synthetic` converges in 6 passes (losses 1,235.507 MW, 675,277 GBP/h; 4 passes and 1,385.085 MW before the N240 tie-break chose a different optimal vertex of its degenerate DC dispatch) and its final pass leaves
out 5 rated lines that no dispatch can relieve (MID SGT 400/275 1 and 2, LDN SGT 400/275 1, YRK G8P4 132/33, MID G2P1S3
33/11). Since N316 (October 2026) the name is the shipped folder, whose branch order differs from the in-memory builder
it used to run; the folder stops at the same vertex and prints the same figures (measured at 1.95.259; `acopf
data/gb-synthetic` prints the same output byte for byte). When the final DC pass left out rated lines that no dispatch can relieve (see [`dcopf`](#dcopf)), the run
names them first (`the final DC pass left out <n> rated line(s) no dispatch can bring within rating (still overloaded;
...)`, up to `--top` of them).

| Flag | Default | Meaning |
|---|---|---|
| `--loss-tol` | 1e-3 | Loss convergence tolerance (MW); must be above 0. |
| `--max-iter` | 20 | Outer-loop cap; at least 1. |
| `--top` | 12 | Generators to list. |
| `--costs` | off | Every DC pass dispatches on the generators' own cost curves, exactly as [`dcopf --costs`](#dcopf); the reported cost is then the sum of the curves at the final dispatch, in the case's cost units. |
| `--prices` | off | Also print the nodal prices (LMPs, with their energy and congestion parts) and line shadow prices of the final DC pass, as [`dcopf --prices`](#dcopf) prints them. They are lossless DC prices at the loss-inflated demand that pass dispatched: the loss loop only scales demand, so there is no marginal-loss component, and voltage and reactive power do not enter them. The dispatch is the same with or without the flag. The result file gains the `prices` and `shadow_prices` tables and `prices_source`. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).
