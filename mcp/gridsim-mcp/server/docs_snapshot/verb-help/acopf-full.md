### acopf-full

`gridsim acopf-full <case> [--max-iter 150] [--top 12] [--cost-file <file.json>]`

The full AC optimal power flow (`AcOpfInteriorPoint`): a primal-dual interior point method, MATPOWER's MIPS with its
defaults, starting point and stopping conditions. It chooses every bus voltage (magnitude and angle) and every unit's
active and reactive output to minimise the units' polynomial cost curves, subject to the AC power balance at every bus,
each rated branch's MVA limit (RATE_A) at both ends, each bus's own voltage limits (else 0.94 .. 1.06 pu) and each unit's
P and Q limits, with every slack angle fixed. Transformer taps and phase shifts stay at their case values. The costs are
the case's own curves (a `.m` file's `mpc.gencost`), or `--cost-file` (the cost file format of [`dcopf --costs`](#dcopf)).
Unlike [`acopf`](#acopf) it optimises voltages and reactive power and enforces the AC limits.

Prints the iterations, the objective (in the case's cost units per hour), generation, load and losses, the largest units'
P and Q, the highest nodal prices (the multipliers of the active and reactive power balance at each bus, cost units per
MWh and per MVArh) with each bus's voltage, and the binding constraints with their shadow prices (`Vmin`/`Vmax` per pu,
`Pmin`/`Pmax`/`Qmin`/`Qmax` per MW or MVAr, `Sf`/`St` per MVA of the branch's from or to end, each per hour). On
`case9`, `case14`, `case30`, `case57`, `case118`, `case300`, `case1354pegase` and `case2869pegase` the objective rounds to
the figure MATPOWER's `runopf` prints (5296.69, 8081.53, 576.89, 41737.79, 129660.70, 719725.11, 74069.35 and 133999.29).

Exit 0 converged. Exit 1 for a case the method refuses, with the reason: a unit with no cost curve (give `--cost-file`)
or a piecewise-linear one, a dispatchable load (Pmin below 0 with Pmax 0), a voltage-dependent load, crossed or
non-numeric limits, a zero-impedance branch, no slack bus, or more than one island. Exit 3 when it does not converge
(`not converged: no convergence within <n> iteration(s)`, or `numerical failure: ...` for a singular Newton system, a step
below 1e-8 or a barrier parameter out of range); the iteration log is printed on stderr and no dispatch is shown, because
the point it stopped at is not an optimum. Branch angle difference limits are not enforced (the MATPOWER reader notes
them when a file has them).

| Flag | Default | Meaning |
|---|---|---|
| `--max-iter` | 150 | Iteration cap (MIPS `max_it`); at least 1. |
| `--top` | 12 | Units, buses and binding constraints to list. |
| `--cost-file` | the case's curves | A cost file (JSON, as `dcopf --costs <file.json>`) giving every unit's curve. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary (`objective_per_h`, `total_generation_mw`, `total_load_mw`, `losses_mw`,
`iterations`, `cost_source`) and the tables `buses` (`bus_id,vm_pu,va_deg,lmp_p_per_mwh,lmp_q_per_mvarh,mu_vmin,mu_vmax`),
`generators` (`unit,bus,pg_mw,qg_mvar,mu_pmin,mu_pmax,mu_qmin,mu_qmax`), `branches`
(`branch,from,to,pf_mw,qf_mvar,pt_mw,qt_mvar,rate_mva,mu_sf,mu_st`), `binding` (`kind,element,shadow_price`) and
`iterations` (the log). A run that does not converge writes no result file.
