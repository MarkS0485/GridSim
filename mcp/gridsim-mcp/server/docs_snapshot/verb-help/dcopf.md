### dcopf

`gridsim dcopf <case> [--no-limits] [--top 12] [--costs case|<file.json>] [--prices]`

DC optimal power flow: the cheapest dispatch that meets demand, with line limits enforced unless `--no-limits`. Prints
total cost (GBP/h), the dispatch by output and any binding line limits. The dispatch is feasible only when generation
can meet demand and, with limits on, the security-constrained LP finishes Optimal. An infeasible, unbounded or
iteration-limited LP prints `INFEASIBLE` and exits 3 (a verdict failure). Exit 1 if the case does not load or the
solve throws. On a connected case the `INFEASIBLE` line says why: `no dispatch within the units' output limits keeps
every rated line within its rating (security LP Infeasible)` (`--no-limits` drops the line limits), `the security LP
stopped without proving an optimum (status <s>)`, or `generation capability cannot meet demand (<x> MW of in-service
capability for <y> MW of demand)` when the merit order itself falls short.

**Lines no dispatch can relieve.** Before the LP runs, every rated line's DC flow is bounded over every unit anywhere
in its output range (the balance row dropped, so a superset of every dispatch the LP could choose). A line whose whole
range lies above its rating (or below minus its rating) by more than 1e-6 MW cannot be brought within its rating by any
dispatch: it is left out of the LP, as the corrective redispatch leaves out what no generator can move. Each one is
printed whatever the outcome, `<n> rated line(s) cannot be brought within rating by any dispatch; left out of the
security LP and still overloaded:` with its rating and the range of flows any dispatch gives it, and the summary says
`secure for every rated line except the <n> listed above`; they also appear among the binding lines with their flow
over the rating. The result file gains an `unrelievable_lines` table and `unrelievable_line_count` only then.
`dcopf gb-synthetic` leaves out 2 lines, the meshed supergrid transformers `MID SGT 400/275 1` (rating 1,000 MW, any
dispatch 1,409.0 .. 1,446.0 MW) and `LDN SGT 400/275 1` (rating 1,000 MW, any dispatch 1,258.1 .. 1,757.7 MW), and
dispatches the rest at 602,945 GBP/h (2.7 s wall time for the whole run on the reference machine).

A case with several DC islands (`gb-full` has two) is dispatched island by island: each island meets its own demand
from its own generation, against its own reference bus. The output lists every island (reference bus, buses, demand,
generation, verdict and LP status) before the dispatch. An island with no generator is listed as `NO GENERATION: load
cannot be supplied` when it carries load, and the run is then `INFEASIBLE`. With `--no-limits` the islands are
dispatched by merit order. `dcopf gb-full` dispatches the 3,536-bus main island (reference bus 194, 53,279.4 MW) and the
3-bus island (reference bus 6774, 5.6 MW), leaves out 3 lines (`COPP C1-_COPP T1 132kV`, rating 109 MW, any dispatch
-164.4 .. -125.7 MW; the parallel pair `ENW-ENW 132kV#23` and `#24`, rating 150 MW, 168.8 MW whatever the dispatch) and
dispatches 53,285 MW at 312,021 GBP/h; the run took 2 min 29 s of wall time on the reference machine. With `--no-limits` the same case dispatches both islands
by merit order at 53,560 GBP/h (4.9 s).

| Flag | Default | Meaning |
|---|---|---|
| `--no-limits` | off | Ignore branch ratings (unconstrained merit order). |
| `--top` | 12 | Generators, binding lines, and (with `--prices`) buses to list. |
| `--costs` | off | Dispatch on each generator's own cost curve instead of the fuel-type merit order. `case` uses the case's MATPOWER `mpc.gencost`; any other value is a `gridsim-costs/1` JSON cost file laid over the case (below). Every in-service unit must end up with a curve, else exit 1. The built-in IEEE names read their MATPOWER files and so carry the files' curves (`dcopf case9 --costs case` and `dcopf case9.m --costs case` give the same dispatch). |
| `--prices` | off | Also report the nodal prices (LMPs) and the binding lines' shadow prices, from the optimum's dual values. The dispatch is the same bits with or without it. |

**Cost curves (`--costs`).** Polynomial curves up to quadratic (MATPOWER MODEL 2 with NCOST 1-3; a longer row is
accepted only when the terms above the quadratic are exactly zero) and convex piecewise-linear curves (MODEL 1) are
optimised exactly, as MATPOWER's own DC OPF accepts them; a cubic or higher term, a negative quadratic coefficient or a
non-convex piecewise curve is refused (exit 1), naming the generator. Linear and piecewise-linear costs are solved by
the same structured simplex as the fuel-type dispatch (a piecewise curve becomes one LP column per segment); any
quadratic term is finished by an exact active-set quadratic program started at the simplex's vertex. The total cost is
the sum of the curves at the dispatch (constant terms included, as MATPOWER reports its objective) and each unit's
figure is its marginal cost there; the units are the case's own currency, printed as `cost units` (MATPOWER's cost
figures are in $/h by convention). Outside a piecewise curve's points its first and last segments are extended in a straight line. Startup
and shutdown costs are read and carried but play no part (one period, no commitment). The cost file:

```json
{ "format": "gridsim-costs/1",
  "generators": [
    { "index": 1, "model": "polynomial", "coefficients": [0.11, 5, 150] },
    { "name": "G2", "model": "piecewiseLinear", "points": [[0, 0], [100, 2500], [200, 6000]], "startup": 0 } ] }
```

`index` is 1-based in the case's generator list in file order (out-of-service units included: the order of MATPOWER's
`mpc.gen` rows or of `generation.json`); `name` must name exactly one unit; give one of the two. Coefficients are
highest order first, as in MATPOWER. Every key is checked: an unknown key, a unit named twice, a bad curve or a missing
format tag is refused (exit 1) naming the entry. A unit the file does not name keeps the case's own curve, if any.

**Prices (`--prices`).** With y0 the balance row's dual and y_r the dual of rated line r's thermal row, the price at
bus k is `LMP = y0 + sum_r y_r PTDF[r, k]`: y0 is the energy component (the price at the island's reference bus) and
the sum the congestion component; the DC model is lossless, so there is no loss component. A binding line's shadow
price is the cost saved per MW of extra rating. Each island is priced on its own; a dead island's buses get no price.
At a degenerate optimum the duals are not unique and those of the optimal basis found are reported. **Degenerate dispatch (N240).** When several dispatches are equally cheap (every MATPOWER case on the fuel-type merit order prices all its units at 50 GBP/MWh) the LP has many optimal vertices. The simplex breaks ties between entering columns whose reduced costs are equal to within 1e-10 (relative) by taking the lowest index, so the dispatch reported does not depend on the last bits of the PTDF sums (before October 2026, a change in summation order could move pglib case588's dispatch by up to 251.7 MW at the same cost). Every LP whose path meets no such near-tie gives the same bits as before. The console prints
the price range, the `--top` dearest buses and every binding line's shadow price; `prices: not available` when the
dispatch is infeasible. Validated against the published PJM 5-bus example (Li and Bo, 2010;
`data/matpower/nesta/pglib_opf_case5_pjm.m`, whose header records the paper's dispatch 40 / 170 / 323.49 / 0 / 466.51
MW: matched to the printed 0.01 MW) and against the HiGHS solver on pglib case3_lmbd, case5_pjm, case30_as and MATPOWER
case9 / case30 (dispatch, cost and every LMP within 1e-6). `gridsim dcopf pglib_opf_case5_pjm.m --costs case --prices`
prints a total cost of 17,479.90, LMPs 16.9774 (bus 1), 26.3845 (bus 2), 30.0000 (bus 3), 39.9427 (bus 4, the
reference: its LMP is the energy component) and 10.0000 (bus 5), and branch 5 (bus 4 - bus 5) binding at -240.0 MW
with a shadow price of 62.3220 per MW of rating.

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)). Without `--costs` the money is GBP and the keys say so (`total_cost_gbp_per_h`, dispatch
column `cost_gbp_per_mwh`). With `--costs` the money is the case's own cost units, so the keys carry no currency:
`total_cost_per_h` and the dispatch column `marginal_cost_per_mwh` (each unit's marginal cost), and the summary adds
`cost_source` and `cost_units` (`cost units`); before October 2026 the keys said GBP whatever the curves' currency. The
same rule applies to `acopf --costs`. A branch with no name (every MATPOWER branch) is shown in the console as its end
buses, `from-to`. With `--prices` two tables are added, `prices`
(`bus_id,lmp_per_mwh,energy_per_mwh,congestion_per_mwh`) and `shadow_prices`
(`branch,name,flow_mw,rate_mw,shadow_price_per_mwh`).
