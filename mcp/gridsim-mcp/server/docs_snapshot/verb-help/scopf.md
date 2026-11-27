### scopf

`gridsim scopf <case> [--contingencies all|<i,j,...>] [--post-rating A|B|C] [--linear-costs case|<file.json>] [--full-lp] [--max-rounds 50] [--top 12]`

Preventive security-constrained DC optimal power flow (`DcScopf`): the cheapest dispatch that keeps every rated branch
within RATE_A in the intact network and within its post-outage rating after each secured single-branch outage, with no
action after the outage. It is a sparse linear program on bus angles (each bus's balance and each branch's DC flow), and
each secured (outage, branch) pair adds the post-outage flow through the line outage distribution factors, solved by
GridSim's own sparse two-phase simplex. By default the pairs enter the LP only once the dispatch breaks them (constraint
generation; each round re-solves with the broken limits added, until none is broken by more than 1e-6 MW); `--full-lp`
puts every pair in from the start and gives the same optimal cost. The DC model is that of `dcopf`, `ptdf` and `lodf`
(series reactance with tap ratios, phase shifts and bus shunt conductance, as MATPOWER `makeBdc`). See technical chapter 29 for the formulation and its validation.

Outages: `all` (the default) secures every in-service branch whose outage leaves the network connected; outages that
would island it are listed and not secured. A comma list of branch indices (from 0) secures those; a listed outage that
islands the network, an unknown or out-of-service branch, or a duplicate is refused (exit 1). Costs: the fuel-type cost of
`dcopf` (GBP/MWh), or with `--linear-costs` each unit's own curve (`case` for a `.m` file's `mpc.gencost`, or a
`gridsim-costs/1` file), which must be linear: a quadratic or piecewise-linear curve is refused (exit 1), not
approximated. A curve's constant term is added to the total cost.

Prints the outages secured and not secured, the LP size and solves, the total cost, the range of nodal prices (each bus's
balance dual: the cost of one more MW of demand there), the largest units' dispatch and the binding limits (intact, or
after the outage of a branch, against the rating used).

Exit 0 optimal. Exit 1 for input the SCOPF cannot build: several DC islands, a refused outage, a cost curve that is not
linear, a unit with no cost curve under `--linear-costs case`, invalid generator limits. Exit 3 when there is no secure dispatch:
the security set is infeasible, the round cap is reached, or an LP solve failed (the cause is printed); no dispatch is
printed and no result file is written.

| Flag | Default | Meaning |
|---|---|---|
| `--contingencies` | all | `all`, or a comma list of branch indices (from 0) to secure. |
| `--post-rating` | A | The rating after an outage: A, B (short-term) or C (emergency); A on a branch where B or C is not given. The intact network always uses A. |
| `--linear-costs` | fuel-type costs | `case` or a `gridsim-costs/1` file: each unit's cost curve, which must be linear. |
| `--full-lp` | off | Every post-outage limit in the LP from the start (no constraint generation). |
| `--max-rounds` | 50 | The most LP solves constraint generation may take; at least 1. |
| `--top` | 12 | Units, binding limits and islanding outages to list. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary (`status`, `optimal`, `total_cost_per_h`, `cost_units`, `demand_mw`,
`post_fault_rating`, `outages_secured`, `islanding_outages`, `rounds`, `post_outage_rows`, `rows`, `columns`,
`lp_iterations`, `full_lp`) and the tables `dispatch` (`unit,bus,name,p_mw,marginal_cost_per_mwh`), `prices`
(`bus_id,lmp_per_mwh`), `binding` (`branch,name,contingency,flow_mw,rate_mw`; contingency -1 is the intact network) and
`islanding_outages` (`branch`). A run with no secure dispatch writes no result file.
