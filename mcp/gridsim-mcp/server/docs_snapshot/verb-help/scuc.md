### scuc

`gridsim scuc <case> [--uc-data <file.json>] [--periods N] [--no-line-limits] [--node-cap 100000] [--gap 1e-6] [--top 12]`

Security-constrained unit commitment (`UnitCommitment`): which units run in each period, and at what output, at least
total cost. It is a mixed-integer linear program solved by `MilpBranchAndBound`, an LP-based branch-and-bound over the
sparse interior point LP, both in GridSim's own numerics (no external solver). Per unit and period: on/off and start
decisions (whole), output between Pmin and Pmax while on, minimum up and down times, ramp limits (a start may reach
max(ramp, Pmin) in its first period), start-up and no-load costs; per period: the demand balance (the case's bus loads
times the period's scale) and a spinning reserve requirement (the committed units' headroom Pmax - P); per rated branch
and period: the DC flow (PTDF times generation plus the flow the loads drive, as `dcpf` solves it) within RATE_A; and for
each listed outage, every rated branch's post-outage DC flow (by the line outage distribution factors) within RATE_A.

Costs: a unit's `marginalCost` from the data file, else the case's cost curve (a `.m` file's `mpc.gencost`: linear and
piecewise-linear curves are used exactly; a quadratic curve is cut into 4 chords between Pmin and Pmax, exact at the
break points and slightly above the curve between them, and a note says so), else the fuel-type cost of `dcopf`. A
polynomial curve's constant term is paid in every period the unit is on. The start-up cost is the data file's, else the
gencost STARTUP column, else 0. Without a data file every unit starts on (so nothing starts) and the run is one period
of the case's own load.

The data file (`gridsim-uc/1`, every key checked, every top-level key but `format` optional):

```
{ "format": "gridsim-uc/1", "periods": 24, "periodHours": 1,
  "demandScale": [0.7, 0.65, ...], "reserveMw": [100, 100, ...],
  "units": [ { "unit": 0, "marginalCost": 12.5, "noLoadCost": 100, "startupCost": 500, "minUp": 3, "minDown": 2,
               "rampUpMw": 80, "rampDownMw": 80, "initiallyOn": false, "initialPeriods": 5, "initialMw": 0,
               "mustRun": false },
             { "name": "G2", "startupCost": 200 } ],
  "contingencies": [ 4, "Line 7-8" ] }
```

A unit is named by `unit` (its index among the in-service generators, from 0) or `name` (unique); a contingency by a
branch index (from 0) or a unique branch name. `initialPeriods` is how long the unit has been in its initial state (a
unit inside its initial minimum time is held there); `initialMw` ramp-limits the first period. A unit whose Pmin is
below 0 (storage charging, an exporting link) is always on.

Prints the problem size, the total cost with its energy, no-load and start-up parts, each period's demand, generation,
headroom, reserve, system price and units on, the largest units' schedules (`1` on, `*` started, `0` off) and the binding
branch limits (intact, or after an outage). The system price is the balance row's dual in the LP with the commitment
fixed at the answer (the usual pricing of a commitment), per MWh.

Exit 0 optimal (within the gap). Exit 1 for input the commitment cannot build exactly, with the reason: a malformed data
file, a committable unit with no finite Pmax, several DC islands, an outage that islands the network, per-period arrays
of the wrong length, or more than 200,000 columns. Exit 3 when no optimum is proven: infeasible (no schedule meets the
demand, reserve and limits), the node cap reached (the best schedule found so far is printed, labelled `NOT PROVEN
OPTIMAL`, with the best bound), or a node relaxation that ended without an answer (the node is named).

| Flag | Default | Meaning |
|---|---|---|
| `--uc-data` | none | The unit commitment data file (`gridsim-uc/1`). |
| `--periods` | the file's, else 1 | The number of periods (must agree with the file's per-period arrays). |
| `--no-line-limits` | off | Leave the branch ratings out (a copper plate); listed outages are still secured. |
| `--node-cap` | 100000 | Branch-and-bound nodes before stopping; at least 1. |
| `--gap` | 1e-6 | Relative optimality gap. |
| `--top` | 12 | Units and binding limits to list. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary (`status`, `optimal`, `total_cost`, `energy_cost`, `no_load_cost`,
`startup_cost`, `bound`, `nodes`, `lp_iterations`, `rows`, `columns`, `integer_columns`, `line_limits`, `contingencies`,
`uc_data`) and the tables `periods` (`period,demand_mw,generation_mw,headroom_mw,reserve_mw,system_price_per_mwh`),
`schedule` (`unit,bus,name,period,on,started,output_mw`) and `binding` (`period,branch,name,contingency,flow_mw,rate_mw`;
contingency -1 is the intact network). A run with no schedule writes no result file.
