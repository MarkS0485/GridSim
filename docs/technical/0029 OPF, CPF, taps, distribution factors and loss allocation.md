# 29. OPF, CPF, taps, distribution factors and loss allocation

This chapter specifies the eight steady-state analyses that sit on top of the power flow: the DC power flow
(`DcPowerFlow`), the DC optimal power flow (`DcOptimalPowerFlow`), the loss-aware optimal power flow
(`AcOptimalPowerFlow`), the load-multiplier search for the voltage-collapse limit (`ContinuationPowerFlow`), the
coordinated tap optimiser (`TapOptimizer`), the DC distribution factors (`DistributionFactors`: PTDF and LODF) and the loss
decomposition (`LossAllocation`). All live in `src/GridSim.Core/Analysis/`. They reach the command line as `dcpf`, `dcopf`,
`acopf`, `cpf`, `taps`, `ptdf` and `lodf` (the distribution factors) and `losses`. The chapter states each formulation as
implemented, says plainly where a name promises more than the code does (`acopf` is a DC problem in a loss loop; `cpf` is a
bisection, not a predictor-corrector continuation), and records the exact case-loading, result-file and exit behaviour of
each verb. Usage is in
[ch. 6 of the user manual](<../manual/0006 OPF, CPF, taps, distribution factors and losses.md>).

The AC solves inside `acopf`, `cpf` and `taps` use the Newton-Raphson engines of
[ch. 3, Power flow](<0003 Power flow.md>), chosen by `SolverSelector.CreateNewton` (sparse at the default threshold of 0,
never the sweep). The two engines are bit-identical on the CPU, so the same model gives the same bits from either. The DC solves use the sparse LU of
[ch. 4, Sparse and GPU numerics](<0004 Sparse and GPU numerics.md>). Per-unit conventions and the branch records are in
[ch. 2, Data model and per-unit conventions](<0002 Data model and per-unit conventions.md>).

## Model

### The DC network model

`DistributionFactors.Build` (`src/GridSim.Core/Analysis/DistributionFactors.cs`) forms the DC model every other DC analysis
uses. A branch takes part when it is in service, its series reactance `X` is not zero and both end buses exist in the model.
Resistance, line charging, tap ratio and phase shift are ignored, and every voltage magnitude is 1 pu:

```
b_l       = 1 / X_l                                    (per-unit susceptance of branch l; X in pu on BaseMva)
B'[i,i]   = sum of b_l over participating branches at bus i
B'[i,j]   = - sum of b_l over participating branches joining i and j       (i != j)
every island reference bus: its row and column are removed from B'
theta     = B'^-1 * (P / BaseMva)                      (reference angles are 0; P is the net injection in MW)
f_l       = b_l * (theta_from(l) - theta_to(l)) * BaseMva                  (MW, positive from -> to)
```

Parallel branches add. B' is stamped in branch order into a `CsrMatrix` and factored once by `SparseLu.Factor` (reverse
Cuthill-McKee ordering, partial pivoting; [ch. 3](<0003 Power flow.md>)). If the factorisation finds B' singular even with
one reference per island, `Build` throws `InvalidOperationException` (`DC susceptance is singular even with one reference bus per
island ...`). `DcFlows` is one solve of the equation above for any injection set; an injection at a reference bus or at an unknown
bus id is dropped, which is the reference absorbing it.

**Islands.** `FindIslands` joins buses by union-find over exactly the participating branches. Each component is a DC island. If an
in-service branch with `X = 0` is the only link between two components, `FindIslands` throws `InvalidOperationException`, because
the reactance-only model cannot represent an infinite susceptance and splitting there would invent an island. Islands are numbered by
their first bus in model order. The reference of each island is chosen as follows:

| Island | Reference bus |
|---|---|
| Holds the overall slack: `slackBusId` if given and present, else the first `Slack` bus, else bus index 0 | That slack |
| Any other island with a `Slack` bus | The first `Slack` bus in model order |
| Any other island with generators | The bus holding the largest single `|Pg|` (first in model order among ties) |
| An island with no generator (dead island) | Its first bus, so that B' stays non-singular |

The overall slack's reference is first in the reference list and becomes `DistributionFactors.SlackBusId`. With one island nothing
changes from the single-slack formulation: the same reference, the same matrix, the same factorisation. The rule for the other
islands is the AC island rule of `IslandPartition` ([ch. 3](<0003 Power flow.md>)), with one difference: the DC rule counts an
island as having a generator when any generator sits on it, even one at `Pg = 0`, while the AC pre-pass blacks out an island
whose generators all have `Pg = 0`. Such an island is a live island to the DC model and a dead one to the AC solve. `DcIsland`
records the reference bus id, the bus ids, whether the island has any generator, and its total `Pd`.

### DC power flow

`DcPowerFlow.Solve` (the `dcpf` verb) is the DC model above used as a power flow, not a second DC model. It forms the net
injection of each bus as the sum of in-service `Pg` minus `Pd` (accumulated in model order), and calls
`DistributionFactors.DcSolve`, which builds the same reduced right-hand side as `DcFlows` (`mw / BaseMva`, every island
reference's entry dropped), solves `B' theta = P` with the one factorisation, and forms the flows
`b_l (theta_from - theta_to) BaseMva` exactly as `DcFlows` does (the two share the right-hand-side and flow code, so they
give the same bits). Angles are returned in degrees, every island reference at 0. Each island's reference injects
`-(sum of the other buses' net injections in the island)`: the DC model is lossless, so that is the whole imbalance.
Loading is `|flow| / RateAMva x 100`, 0 for an unrated branch. The result (`DcPowerFlowResult`) holds the bus ids, angles
and net injections, the branch flows and loadings, and one `DcIslandBalance` per island (reference bus, bus count, load,
scheduled generation, reference injection, whether the island has a generator).

`RunDcpf` (`src/GridSim.Cli/Program.cs`) prints one line per island, the number of rated branches above
`ContingencyAnalysis.OverloadThresholdPercent` (100) of `RateA`, and the `--top` branches (12 by default, 0 prints none) with
the largest absolute flow, ties by branch index. It accepts `--top`, `--out`, `--json` and `--csv`. Exit 1 when the case does
not load or `DistributionFactors.Build` throws `InvalidOperationException`. Exit 2 for a negative `--top`. Exit 3 when an
island carries load and has no generator: the report is printed, then `dcpf: N island(s) carry load but have no generator, so
the DC solution has no physical meaning there.` goes to standard error. An overload is reported, not an error. The result
tables are `branches` (`index, name, from, to, flow_mw, rate_mva, loading_pct`), `buses` (`bus_id, va_deg, p_inj_mw`) and
`islands`; `--out <file>.csv` writes the branch table to the file and the bus table to `<stem>-buses.csv`, as the verb did
before the shared result writer.

### PTDF

Column `k` of the PTDF matrix is the DC flow response to one per-unit injected at bus `k` and withdrawn at the reference of `k`'s own
island:

```
rhs       = e_k                                         (1.0 at the reduced index of bus k, 0 elsewhere)
theta     = B'^-1 * rhs
PTDF[l,k] = b_l * (theta_from(l) - theta_to(l))         (dimensionless: MW per MW)
```

`PtdfColumn(busId)` returns an all-zero column when the bus is a reference bus (the slack column is identically zero) or is not in the
model. Every branch of another island has a zero entry. One column is one solve; `Ptdf()` builds the whole matrix and is memory-heavy
beyond small cases. Flow under any injection is `f = f0 + PTDF * P`, where `f0` is the flow of the fixed loads.

### LODF

For an outage of branch `k` the code solves one transfer problem, a unit injected at the from bus of `k` and withdrawn at its to bus
(a reference end contributes nothing to the right-hand side):

```
theta     = B'^-1 * (e_from(k) - e_to(k))
Phi[l,k]  = b_l * (theta_from(l) - theta_to(l))         (flow on l per unit transferred across k)
LODF[l,k] = Phi[l,k] / (1 - Phi[k,k])          LODF[k,k] = -1
f_l(after outage of k) = f_l + LODF[l,k] * f_k
```

`Phi[k,k]` is the share of the transfer that the branch carries itself; it is exactly 1 when the branch is a bridge (no other path).
When `|1 - Phi[k,k]| < DistributionFactors.IslandingSensitivityTolerance` (1e-9, absolute) the outage islands its network:
`OutageIslands(k)` returns true and `LodfColumn(k)` returns an all-NaN column. Both read the same expression, so they cannot disagree.
For an out-of-service branch `LodfColumn` returns zeros and `OutageIslands` returns false.

### The DC-OPF problem

`DcOptimalPowerFlow.Solve(model, costPerGen, enforceLineLimits)` (`src/GridSim.Core/Analysis/DcOptimalPowerFlow.cs`) minimises a linear
generation cost; the overload `Solve(model, DcOpfOptions)` adds cost curves and prices (see Cost data). With `g` generators, `L` rated branches (`RateAMva > 0`, in branch order) and bus `bus_i` for generator `i`:

```
variables    Pg_i  (i = 1..g)           s_l  (l = 1..L)         dispatch in MW; one slack per rated line
minimise     sum_i cost_i * Pg_i
subject to   sum_i Pg_i = D                                      D = sum over buses of Pd
             sum_i PTDF[l, bus_i] * Pg_i + s_l = RateA_l - f0_l  one row per rated line l kept by the screen below
             Pmin_i <= Pg_i <= Pmax_i                            Pmax_i = 1e6 MW when infinite
             0 <= s_l <= 2 * RateA_l
f0_l   = DC flow on line l with every Pg = 0 and the loads at -Pd           (FlowsFrom)
flow_l = f0_l + sum_i PTDF[l, bus_i] * Pg_i = RateA_l - s_l      so -RateA_l <= flow_l <= +RateA_l on every kept line
```

**Unrelievable lines.** Before the LP is built, `ScreenUnrelievable` computes for each rated line the range of DC flows that
any dispatch with every unit anywhere in `[Pmin, Pmax]` could give it (the balance row dropped, so the range covers every dispatch
the LP could choose): `flowMin = f0_l + sum_i min(a_i Pmin_i, a_i Pmax_i)` and `flowMax = f0_l + sum_i max(a_i Pmin_i, a_i Pmax_i)`
with `a_i = PTDF[l, bus_i]`. A line with `flowMin > RateA_l + UnrelievableMarginMw` or `flowMax < -RateA_l - UnrelievableMarginMw`
(`UnrelievableMarginMw` = 1e-6 MW) cannot be brought within its rating by any dispatch. It is left out of the LP, which is then
solved over the remaining lines, and it is reported in `DcOpfResult.UnrelievableLines` (branch index, name, rating, and the
reachable minimum and maximum flow), in branch order. When no line is left out the LP is exactly the one described above. A line
left out stays over its rating at the dispatch returned, so an `Optimal` LP gives `Feasible = true` (and `dcopf` exit 0, `acopf`
`Converged`) while those lines are still overloaded. `dcopf` lists them under
`<n> rated line(s) cannot be brought within rating by any dispatch; left out of the security LP and still overloaded:`, adds
`secure for every rated line except the <n> listed above (no dispatch can relieve them)` to the summary, and writes an
`unrelievable_lines` table and `unrelievable_line_count` to the result file; `acopf` lists the lines its final DC pass left out.
Nothing in the exit code tells such a run from one with no unrelievable line: a script must read the listing or the result file.

The balance row has no loss term. `Pmin` is used as given, so a unit with a negative `Pmin` (storage charging, an exporting
interconnector) can be dispatched below zero. A `Pmin` that is NaN or infinite, a `Pmax` that is NaN, or a `Pmin` above `Pmax` throws
`InvalidDataException`. The generator `Pg` field is not read, except by the island reference election.

**Merit order first.** Before any LP the solver builds a merit-order dispatch: start every unit at `Pmin`, then fill units in ascending
cost, ties in generator index order (`OrderBy` is stable), each to `Pmax`, until the unserved remainder is at most
`MeritOrderFillStopMw` (1e-9). The dispatch is feasible when `D - sum Pmin >= -BalanceToleranceMw` (1e-6),
`sum (Pmax - Pmin) + BalanceToleranceMw >= D - sum Pmin`, and the fill leaves a remainder of at most `BalanceToleranceMw`. This is the
dispatch of `--no-limits`.

**When the LP runs.** Only when `enforceLineLimits` is true, the merit order is feasible and at least one branch has `RateAMva > 0`. Its
answer replaces the merit order. A result is feasible only when the LP status is `Optimal` (over the lines the screen kept). For any other status (`Infeasible`,
`Unbounded`, `IterationLimit`) the result carries the merit-order dispatch with `Feasible = false`, `NetworkConstrained = true`, the
rated lines whose merit-order flow is within `BindingToleranceMw` (1e-3 MW) of the rating listed as binding, and the status in
`DcOpfResult.SecurityLpStatus`. With no LP run the status is null.

**Result.** `TotalCost = sum Pg_i * cost_i`. `BindingLines` lists every rated line with `|flow| >= RateA - BindingToleranceMw` at the final
dispatch, computed from the dispatch through `DcFlows`. `NetworkConstrained` is true when that list is not empty (or the LP failed).
Because a dispatch of equal-cost units is a vertex of the LP, a case whose units all cost the same has many optimal dispatches. The
DC-OPF LPs price with a near-tie rule (`BoundedSimplex.DcOpfPricingTieMargin`, relative `1e-10`): among the improving columns whose
reduced cost is within `1e-10 x S` of the best score `S`, the lowest-index column enters. A degenerate dispatch is therefore the
lowest-index optimal vertex, and a last-bit change in the factors cannot pick a different one. The contingency corrective redispatch
and the storage LP scheduler pass the same margin (`BoundedSimplex.NearTiePricingMargin`).

**Islands.** When `FindIslands` returns more than one island, `SolveIslands` dispatches each energised island as its own model: its
buses, the branches with both ends in it and its generators, all in model order, with its reference bus marked `Slack` if it is not
already. Each is solved by the connected-case code above and the answers are merged back to the full model's generator and branch
indices. No power crosses between islands. An island with no generator is reported and, if it carries load (more than
`BalanceToleranceMw`), makes the result infeasible; it is never dropped. A branch joining two islands is out of service by
construction. The result's `Islands` list holds, per island: reference bus, bus count, load, generation, whether it has generation,
its own verdict and its own LP status. The overall `SecurityLpStatus` is null when no island ran an LP, `Optimal` when every island that
did reached it, and otherwise the first non-optimal status in island order. A generator on a bus the model does not have, or a
`costPerGen` array of the wrong length, throws in the island path.

### Cost data

There are two cost paths. The default path prices each unit by its fuel type. The curve path (`--costs`) prices each unit
by its own cost curve.

**Fuel-type path.** The cost of unit `i` is `costPerGen[i]` when the caller supplies an array aligned to `model.Generators`
(no CLI verb does), and otherwise `FuelCostPerMwh(Generator.Type)`. The unit's marginal cost is linear in output, with no
fixed, quadratic or piecewise term. A MATPOWER case has an empty `Type` on every unit, so every unit costs 50 GBP/MWh
(`DefaultCostPerMwh`). `FuelCostPerMwh` classifies the type with Core's one fuel taxonomy, `GridSim.Core.Model.FuelTypes`
(the economics reader), and returns the cost of the kind:

| Kind (`FuelKind`) | GBP/MWh |
|---|---|
| empty type, or no keyword matches (`Other`) | 50 (`DefaultCostPerMwh`) |
| `WindOnshore`, `WindOffshore`, `Solar`, `Tidal` | 1 |
| `Hydro` | 5 |
| `Nuclear` | 10 |
| `Storage`, `PumpedStorage`, `Hydrogen` | 30 |
| `Coal` | 45 |
| `Biomass` | 55 |
| `Interconnector`, `GridInfeed` | 60 |
| `Ocgt` | 95 |
| `Recip` | 120 |
| `Ccgt`, `Chp`, `Gas` | 65 |

`FuelTypes` lower-cases the type and splits it into words at every character that is not a letter or digit. A keyword
matches when each of its words is the start of consecutive words of the type, and the first matching keyword in list order
decides, so `hydro` matches `Hydroelectric` but `coal` does not match `Charcoal` (kind `Other`, 50). The list, in order, with
the kind each keyword selects (`FuelTypes.Keywords`):

| Order | Keywords | Kind |
|---|---|---|
| 1 | `nuclear` | `Nuclear` |
| 2 | `pumped`, `pump storage` | `PumpedStorage` |
| 3 | `ccgt`, `combined cycle` | `Ccgt` |
| 4 | `ocgt`, `open cycle`, `gas turbine`, `peaker` | `Ocgt` |
| 5 | `chp`, `cogen`, `combined heat` | `Chp` |
| 6 | `coal`, `lignite` | `Coal` |
| 7 | `biomass`, `biogas`, `anaerobic`, `landfill`, `waste` | `Biomass` |
| 8 | `hydrogen`, `fuel cell`, `electrolys`, `electrolyz` | `Hydrogen` |
| 9 | `hydro` | `Hydro` |
| 10 | `tidal`, `wave`, `marine` | `Tidal` |
| 11 | `offshore` | `WindOffshore` |
| 12 | `wind`, `onshore` | `WindOnshore` |
| 13 | `solar`, `pv`, `photovolt` | `Solar` |
| 14 | `storage`, `battery`, `bess` | `Storage` |
| 15 | `interconnector`, `hvdc`, `import` | `Interconnector` |
| 16 | `grid`, `infeed` | `GridInfeed` |
| 17 | `gas` | `Gas` |
| 18 | `reciprocating`, `recip`, `diesel`, `engine`, `oil` | `Recip` |

Each reader of the taxonomy skips the keywords it does not read (`FuelKeyword.Readers`); the economics reader reads every
keyword except a bare `combined`. Because the order is fixed, `hydrogen` (30) is tested before `hydro` (5), `pumped storage
hydro` is tested as pumped (30), `gas turbine` is `Ocgt` (95), `Gas Reciprocating` is `Gas` (65) and the bundled `data/uk` and
`data/gb-synthetic` plant typed `onshore` and `offshore` costs 1. The tests `FuelTypesTaxonomyTests.Every_bundled_type_reads_the_same_in_every_reader`,
`A_gas_turbine_is_ocgt_to_every_reader`, `Hydrogen_and_pumped_storage_are_no_longer_priced_as_hydro`,
`Keywords_match_whole_words_by_their_start` and `The_substring_false_positives_read_as_other_in_every_reader` pin the costs and
the rule.

**Curve path.** With `DcOpfOptions.UseGeneratorCosts` (the CLI's `--costs`), the cost of unit `i` is its `Generator.Cost`
curve, and every in-service unit must carry one. `--costs case` uses the curves the MATPOWER parser read from `mpc.gencost`
(`MatpowerParser`, carried in `Generator.Cost`; a case with a unit that has no row exits 1 with `[dcopf] --costs case: K of N
in-service generator(s) carry no cost curve (the case has no mpc.gencost for them); give a cost file instead.`).
`--costs <file.json>` overlays a `gridsim-costs/1` file (`GeneratorCostFile.Apply`): each entry names a unit by 1-based
`index` in file order or by exact `name`, and gives a `polynomial` (`coefficients`, highest order first) or `piecewiseLinear`
(`points`, `[MW, cost per hour]`) model. An unknown key, a unit named twice, a missing file and an invalid curve exit 1 as
`[dcopf] --costs: <message>`. A unit the file does not name keeps the case's own curve, if any. A MATPOWER case file without
the flag reads its `gencost` and ignores it, with a `note:` on standard error.

`GeneratorCostCurve.From` accepts a convex quadratic `a P^2 + b P + c` (`a >= 0`) and a convex piecewise-linear curve through at
least two points with strictly increasing MW and non-decreasing slopes (the first and last segments extend in a straight
line outside the points). A polynomial with more than three coefficients is accepted only when every term above the
quadratic is exactly 0. A cubic term, a negative quadratic coefficient, a non-convex piecewise curve and a non-finite number
are refused with `InvalidDataException`, naming the unit.

The curve LP has the constraints of the security LP above (the balance row and one thermal row per rated line,
`DcOpfCosted`). A polynomial unit is one column in `[Pmin, Pmax]` with its linear coefficient as cost; a piecewise unit is one
column per segment, costed at the segment slope, which a convex curve fills in order. With no quadratic term the structured
simplex solves it. With a quadratic term the simplex gives a feasible vertex and `DcOpfQuadratic` (a primal active-set
method for convex quadratic programs, plain IEEE arithmetic in index order) takes it to the optimum. The total cost is the sum of
the curves including their constants. With line limits off the problem has the balance row only.

**Prices.** With `DcOpfOptions.ComputePrices` (`--prices`), `y0` is the dual of the balance row and `y_r` that of thermal
row `r` (each the change in optimal cost per unit increase of the row's right-hand side). The price at bus `k` is

```
LMP_k = y0 + sum_r y_r PTDF[r, k]
```

`y0` is the energy component (the price at the island's reference bus) and the sum is the congestion component. The DC model
is lossless, so there is no loss component. A line's shadow price is the cost saved per MW of extra rating (`-y_r` when held
at `+RateA`, `+y_r` at `-RateA`). At a degenerate optimum the duals need not be unique; the reported ones are those of the
optimal basis or active set found. On a multi-island case each island is priced at its own marginal unit. `prices` works on the
fuel-type path too (`dcopf uk --prices`).

`DcOpfResult` carries `Prices` (null when not asked for or when no optimum with duals was reached) and `SolverFailure`, the
reason the curve solver stopped without an optimum, which the CLI prints as a `solver: ... (status ...)` line.

The CLI labels a curve run `[cheapest secure dispatch on generator cost curves: case gencost|<file name>]`, prints money as
`cost units` (a curve is in the case's own currency), shows each unit's marginal cost instead of the fuel price
(`marginal 24.0442 cost units/MWh`), and labels the mode `(network-constrained)` or `(no line limit binds)`.
`AcOptimalPowerFlow.Solve` takes the same options (`useGeneratorCosts`, `computePrices`); a cost vector together with either is
refused with `ArgumentException`.

The JSON loader sets `Pmin = pMinMw` (0 when the file does not give it) and `Pmax = pMaxMw` (`capacityMva` when not given)
for each generator, and `RateAMva = ratingMva x circuits` for a line (with `X` and `R` divided, and `B` multiplied, by the
circuit count). So in a JSON case a generator cannot be dispatched below zero unless the file allows it, and the `uk` line
`L2-3 B6 boundary` is rated 2 x 2,200 = 4,400 MVA.

### Preventive security-constrained DC-OPF

`DcScopf.Solve(model, ScopfOptions)` finds the cheapest dispatch that keeps every rated branch within RateA before any
outage and within its post-outage rating after each listed single-branch outage, with no action after the outage
(preventive). The CLI verb is `scopf` (manual chapter 19).

The LP is written on bus angles, so it stays sparse: columns are the dispatch P_g in [Pmin, Pmax] at a linear cost, an
angle psi_i = base MVA x theta_i per bus (the reference bus fixed at 0) and a flow f_l per branch (within +/- RateA
when rated, free otherwise). Rows are each bus's balance (its units' output, minus the flows leaving, plus the flows
arriving, equals its demand) and each branch's flow, f_l - (psi_from - psi_to) / X_l = 0. After the outage of branch
k, branch l carries f_l + LODF(l, k) f_k; a secured pair adds a column q within +/- the post-outage rating and the row
q - f_l - LODF(l, k) f_k = 0. The LP is solved by `SparseBoundedSimplex` (two-phase, so an infeasible security set is
proved infeasible and reported, never dispatched).

- **Outages.** `Contingencies` null secures every in-service branch whose outage leaves the network connected; the
  outages that island it are listed in `IslandingOutages` and not secured. A listed islanding outage, an unknown or
  out-of-service branch and a duplicate are refused. One connected network only.
- **Post-outage rating.** `PostContingencyRating` is RateA (default), RateB or RateC; RateB and RateC fall back to
  RateA on a branch where they are 0, as in the AC contingency analysis. The pre-outage state uses RateA.
- **Constraint generation** (default). The LP starts with no post-outage row. After each solve every pair (outage,
  rated branch) is checked with the LODF; the pairs over their rating by more than `ViolationToleranceMw` (1e-6 MW)
  are added in outage then branch order and the LP is solved again. It stops when none is broken; the answer is then
  optimal for the LP holding every pair. `MaxRounds` (50) caps it; reaching the cap is status `RoundLimit`, with no
  dispatch. `ConstraintGeneration = false` puts every pair in the LP from the start.
- **Prices.** A bus's price is the dual of its balance row: the change in optimal cost per extra MW of demand there,
  including the congestion of binding post-outage limits.
- **Result.** Status, total cost, demand, dispatch, base flows, prices, the binding limits (pre-outage with
  contingency -1, and post-outage), the secured and islanding outages, the rounds, the post-outage rows in the last LP
  and the LP size. Every loop runs in a fixed order, so a repeat solve gives the same bits.

**Validation** (`DcScopfTests`). A three-bus case against hand calculation (dispatch 120 / 30 MW, cost 2100, prices
10 / 30 / 30, the binding limit of line 1-2 after the outage of line 1-3, and 1800 with RateC 135); the Wood and
Wollenberg six-bus network and case30 against an independent formulation (one angle vector and flow set per outage
state with the branch removed, no LODF or PTDF, solved by the interior point method) to 1e-8 relative; the plain
DC-OPF when nothing is secured; every secured post-outage flow re-solved on the network with the branch removed;
constraint generation against the full LP; repeat solves bit for bit; infeasibility and the round cap reported.

### AC-OPF as implemented

`AcOptimalPowerFlow.Solve` is **not** an optimal power flow in the AC equations. It is the DC-OPF above, called repeatedly on loads
scaled up by the losses of the previous pass, with an AC power flow in between that does nothing except measure those losses.
Neither bus voltages, nor reactive power, nor AC branch loading, nor generator reactive limits appear as constraints or as results. The
CLI header says so: `ACOPF (DC dispatch + AC loss loop) - <case> (<n> buses)  [not a full AC-OPF: no voltage, reactive-power or AC branch-flow limits]`.
A real AC-OPF with voltage and reactive limits is not implemented (see Limits).

```
D       = sum of Pd over buses
losses  = 0
for iter = 0 .. maxIterations - 1:
    scale = (D + losses) / D                      (1.0 when D <= 0)
    dc    = DcOptimalPowerFlow.Solve(ScaleLoads(model, scale), options)   (loads Pd and Qd scaled; fuel-type or cost-curve costs)
    if iter == 0 and StaysInfeasible(dc):  return Converged = false, Iterations = 0, Failure = DcDispatchInfeasible, StoppedAtInfeasibleFirstPass
    ac    = Newton(model with each generator's Pg := dc.Dispatch[i].Pg)                  (the UNscaled model)
    if not ac.Converged:  return Converged = false, Iterations = iter, Losses = NaN, Cost = NaN, Failure = AcPowerFlowNotConverged
    newLosses = sum over buses of ac.PInjMw                                              (net injections sum to the loss)
    settled   = | newLosses - losses | < lossToleranceMw
    losses    = newLosses
    if settled:  iter = iter + 1;  break
cost = sum Pg_i * cost_i of the last dc            (the dc total cost with cost curves)
Converged = settled AND dc.Feasible
Failure   = None | IterationCap (not settled) | DcDispatchInfeasible (settled, last dc infeasible)
```

`options` is the fuel-type default, or `UseGeneratorCosts` for `--costs`, with `ComputePrices` for `--prices`. The prices returned are
those of the last DC pass: the lossless DC prices at the loss-inflated demand, with no marginal-loss component.

**The pass-0 stop.** An infeasible DC dispatch at pass 0 ends the run when no later pass can be feasible (`StaysInfeasible`).
Every later pass scales demand by `s >= 1`, which holds because the loss estimate is non-negative when every branch has
`R >= 0` and every bus has `Gs >= 0` (checked; otherwise the loop runs on). The proved cases are: (a) an island with demand and
no unit; (b) island demand above its total `Pmax` plus `BalanceToleranceMw`; (c) the security LP proved `Infeasible` while every
unit has `Pmin <= 0 <= Pmax`, because the feasible values of `s` then form an interval that contains 0, so `s = 1` infeasible
means every `s >= 1` is infeasible. Demand below the units' total `Pmin`, an LP that stalled or was unbounded, and (c) with a
unit whose `Pmin` is above 0 do not stop the run. A multi-island network stops when any one island is proved.

**Islands.** A network of several DC islands runs the same loop per island, because the DC-OPF dispatches each island on its own
balance and no power crosses between islands: island k keeps its own `losses_k` (the sum of `ac.PInjMw` over its buses), its
buses' `Pd` and `Qd` are scaled by `(D_k + losses_k) / D_k` (1.0 when `D_k <= 0`), the loop has settled when every island's
estimate moved by less than `lossToleranceMw`, and the reported loss is the sum over all buses. No loss cover moves between
islands, so each island's slack produces its dispatch to within the tolerance. A connected network runs the single-ratio loop
above and its results are the same bits as before the per-island form existed
(`S4SolverConvergenceTests.N070_a_connected_network_keeps_its_loss_loop_to_the_bit`);
`N070_each_island_pays_for_its_own_losses` pins the per-island behaviour.

Details that follow from the code:

- The AC power flow is a bare `SolverSelector.CreateNewton(model)` solve with default `PowerFlowOptions`
  ([ch. 3](<0003 Power flow.md>)): tolerance 1e-8 pu, generator reactive limits enforced, no OLTC loop. The slack bus ignores its
  generators' dispatched `Pg`, so the slack picks up whatever the other dispatches leave.
- Each pass dispatches `D + losses_previous`. The new loss is `sum Pg_dispatch - D + (P_slack_actual - Pg_slack_dispatch)`, so
  `newLosses - losses = P_slack_actual - Pg_slack_dispatch`, where `Pg_slack_dispatch` is the dispatch of the generators on the slack
  bus. The loop therefore stops when the slack bus's actual output equals the dispatch the LP gave its generators, to within
  `lossToleranceMw`. At that fixed point the generation covers the demand and the losses exactly.
- The first pass always runs with `losses = 0`, so a case with any loss needs at least two passes to settle unless its first measured
  loss is below the tolerance (the lossless `tri3` settles in one).
- `Iterations` is the number of completed passes; on an AC failure it is the index of the failing pass, and it is 0 for a pass-0
  stop. `Converged` is false for an unsettled loop, for an AC failure and for a final DC-OPF that is infeasible.
  `AcOpfResult.Failure` (`AcOpfFailure`: `None`, `IterationCap`, `DcDispatchInfeasible`, `AcPowerFlowNotConverged`) names the
  cause, `AcFailure` (`AcOpfAcSolveFailure`: pass, status, iterations, largest mismatch in pu, lowest voltage in pu) describes a
  failed AC solve, and `StoppedAtInfeasibleFirstPass` marks the pass-0 stop. No number of a run depends on them.
- The CLI prints one of four messages after the header, all on standard error with exit 3:
  1. `acopf: the AC power flow at the DC-OPF dispatch of loss pass P (passes count from 0) did not converge: <status> after N
     iteration(s), largest mismatch M pu, lowest voltage V pu where it stopped. ...` With every unit priced the same and no
     `--costs`, a second line says the dispatch is one arbitrary optimum of a degenerate LP and names `--costs case`.
  2. `acopf: the DC-OPF dispatch of loss pass 0 (passes count from 0) is infeasible ... so the loop stopped there; 'gridsim dcopf'
     on this case shows why.`
  3. `acopf: the losses settled after N pass(es), but the DC-OPF dispatch of the final pass is infeasible ...`
  4. `acopf: the losses did not settle within N pass(es) (--max-iter M, --loss-tol T MW).`

  `N111_case118_acopf_fails_at_pass_0_on_the_ac_solve_and_says_so`, `N111_the_iteration_cap_is_reported_as_the_cap`,
  `N111_gb_synthetic_settles_on_an_infeasible_dc_dispatch` and `N111_cli_acopf_names_the_failed_ac_solve_and_the_flat_price` in
  `S4SolverDataFollowUpsTests` pin the causes and the text, and `N173AcopfEarlyStopTests` pins the pass-0 stop.
- `TotalLossMw` is the loss of the AC solve of the last pass. `TotalCostPerHour` prices the dispatch of the last DC solve, which
  covers `D + losses_previous`.
- A DC-OPF that throws `InvalidOperationException` (for example a singular DC model) reaches the CLI as exit 1. A Newton solve cannot
  throw on a singular Jacobian: the step reports non-convergence.

### Continuation power flow

`ContinuationPowerFlow.MaxLoadability(model, step, minStep, maxSteps)` searches for the largest uniform load multiplier at which the AC
power flow converges. **It is a bisection on the multiplier, not a predictor-corrector continuation.** There is no tangent
predictor, no arc-length or voltage parameterisation, and no tracking of the solution through the nose. The nose is located by
the failure of Newton-Raphson to converge.

```
parameter    lambda scales every bus:  Pd := lambda * Pd,  Qd := lambda * Qd     (ScaleLoads; generators are unchanged)
corrector    r = Newton(ScaleLoads(model, lambda))      from the case's stored voltages

lambda = 1;  lastGood = 0;  solves = 0
while step >= minStep and solves < maxSteps:
    solves = solves + 1
    if Newton converges at lambda:   lastGood = lambda;  lambda = lambda + step
    else:                            lambda = lambda - step;  step = step / 2;  lambda = lambda + step
return lastGood                                           (0 when no solve converged)
```

The overload `MaxLoadability(model, step, minStep, maxSteps)` solves each point with the engine's default `PowerFlowOptions` and no
OLTC loop. The overloads that take an `IPowerFlowSolver` and `PowerFlowOptions` (`MaxLoadability`, `TracePvCurve`,
`MaxLoadabilityWithEnd`) solve each point with those. The `cpf` verb passes
`ContingencyAnalysis.WithOltcLoop(backend.NewtonSolver(m))` and `ContingencyAnalysis.StudyOptions` (100 iterations, generator
reactive limits on, 1e-8 pu), so a `cpf` point is solved exactly as a `stress` load point is: the shared Newton engine on the
selected backend, wrapped in the on-load tap changer loop (skipped when the model has no OLTC).

Step control: the step is fixed while solves succeed and halves on each failure, so the sequence of steps is `step / 2^k`. After a failure
the next trial is `lambda_failed - step_old/2`, that is `lastGood + step_new` when the failed point was `lastGood + step_old`. The next
success then advances by `step_new` and retries the point that has just failed, so each refinement costs one repeated solve. `maxSteps`
counts every solve, failures included.

Nose detection: a solve whose status is not `Converged` (iteration cap, non-finite state, or a singular Jacobian, which the canonical
Newton step reports as non-convergence) counts as past the nose. Each solve starts from the stored case voltages, not from the previous
solution, so a failure can occur before the true nose. The slack bus supplies the whole increase, because generator `Pg` does not move;
the power flow places no limit on the slack's active power, on any other generator's active power, or on a branch rating. Generator
reactive limits are enforced by both option sets, and a bus that hits one becomes PQ.

**Bracket.** The loop ends when `step < minStep` and, if at least one failure occurred, it ended on a failure. The last failed point is
`lastGood + step_prev` where `step_prev = 2 * step_final`, and `minStep <= step_prev < 2 * minStep`. So the first failing multiplier lies
above the returned value by a width in `[minStep, 2 minStep)`. The CLI prints `nose bracketed to <minStep>`, the lower end of that
range. `MaxLoadabilityWithEnd` also returns how the walk ended (`LoadabilityWalkEnd`: whether the step fell below `minStep`, the
solve count, the final step and the lowest failed multiplier, NaN when none failed). The CLI prints `nose bracketed to <minStep>` only
when the walk ended on the step. When `maxSteps` ran out first it prints `NOT bracketed: --max-steps <n> ran out with every solve
converged, so lambda is only a lower bound` (no solve failed) or `NOT bracketed: --max-steps <n> ran out first; the nose lies between
this lambda and <lowest failed lambda>`, writes `cpf: --max-steps <n> ran out before the nose was bracketed to --min-step <m>; ...
Exit 3.` to standard error, and exits 3. The result file carries `nose_bracketed`, `solves` and `lowest_failed_lambda`.

**A failing base case.** If the solve at `lambda = 1` fails, the search steps down to `1 - step/2`, then `1 - 3 step/4`, and so on towards
`1 - step`; the first of those that converges becomes `lastGood`. The return value is then below 1 and positive. The CLI prints the
lambda and the negative margin, writes `cpf: the base case (lambda = 1) did not converge; only a lighter load did (largest converged
lambda <lambda>, margin <m>%), so the case as given has no loadability margin. Exit 3.` to standard error, and exits 3. When the return
value is not positive (every solve failed) it prints no margin, writes `cpf: the base case (lambda = 1) did not converge; no
loadability margin.` and exits 3.

**PV curves.** `ContinuationPowerFlow.TracePvCurve` runs the same loop (`MaxLoadability` and it share one private
`Run`, the trace passing a recorder that only copies each converged result) and returns `PvCurve(MaxLambda, BusIds,
Points)`: `MaxLambda` is bit for bit `MaxLoadability`'s answer, and `Points` holds each converged `lambda` with a copy of
every bus voltage magnitude, in the order solved. The converged multipliers rise strictly (a failed step is backed off
and halved, so the next point tried is still above the last converged one). `DcPowerFlowAndPvCurveTests` pins
`MaxLambda` (`The_pv_trace_max_lambda_is_bit_identical_to_MaxLoadability`) and the first point
(`The_first_pv_point_is_the_base_case_solve_bit_for_bit`).

`cpf --pv-out <file.csv>` writes one long CSV with the header `case,lambda,bus_id,vm_pu` and one row per converged point and
bus, in the order solved, with numbers in round-trip form and invariant culture. A name that does not end in `.csv` exits 2.
The file is written before the report, and `[cpf] PV curves: N converged point(s) x M bus(es) -> <full path>` goes to standard
error. `cpf case9` gives 12 points of 9 buses, 108 rows. `stress --pv-out` writes the same layout with a `load_factor` column
in place of `lambda` for the voltages of its own grid of re-dispatched load points, up to its reported max loadability; the two
curves answer different questions, as the two verbs do.

### Tap optimiser

`TapOptimizer.Flatten(model, targetVpu, options, optimizer)` finds one set of tap ratios for all on-load tap changers that minimises the
squared voltage deviation, by re-solving the nonlinear power flow for each candidate.

```
objective(taps) = sum over EVERY bus i of (Vm_i - target)^2           Vm from Newton(model with the taps), default options, fixed taps
```

The objective counts every bus of the solved state, the 400 kV generator buses as well as the transformer outlets, and uses no OLTC
loop (the taps are the variables). A power flow that does not converge returns `NonConvergedPenalty` (1e9).

**Mapping.** Every `Oltc` record must name exactly one branch (ordinal comparison) and control a bus the model has
(`TapOptimizer.OltcProblems`); otherwise `Flatten` throws `ArgumentException` naming every problem, before any solve. The record's
`ControlsBus`, `TargetVpu` and `DeadbandPu` do not enter the objective; after the search each OLTC's controlled-bus voltage is checked
against its own target and deadband (`TapSchedule.Regulation`, one `OltcRegulationCheck` per OLTC).

**Search.** With the base state solved and at least one matched OLTC:

```
seed     = TapRatio of each matched branch (1.0 when TapRatio <= 0)
Eval(t)  = penalty                      if any tap leaves [MinTap, MaxTap]; no solve is run
                                         penalty = sum over violating taps of (TapBoundPenalty + TapBoundPenaltyPerPu * excess_pu)
         = objective(t)                 otherwise
t*       = NelderMead.Minimize(Eval, seed, InitialStep = InitialTapStepPu)        continuous taps
snapped  = for each tap:  clamp(MinTap + Round((t* - MinTap) / step) * step, MinTap, MaxTap)     step = StepPu, or 0.0125 when StepPu <= 0
after    = objective(snapped)                                                       re-solved as it would be applied
```

`Math.Round` is round-half-to-even. The Nelder-Mead minimiser (`src/GridSim.Core/Numerics/NelderMead.cs`) is the classic one with reflection 1,
expansion 2, contraction 0.5 and shrink 0.5. The initial simplex is the seed plus `InitialStep * |seed|` along each axis (0.02 for a seed
of 1.0). It stops when the simplex spread is at most `XTolerance` (1e-8) and the objective spread at most `FTolerance` (1e-12), or after
`MaxIterations` (2000). The minimiser's verdict is kept in `TapSchedule.OptimizerConverged` and `OptimizerIterations`.

**Result.** If the snapped schedule fails to solve, or `after >= before`, the schedule is the current taps on every row, with the
objective unchanged. Otherwise the taps are `snapped`. `TapSchedule.Improved` is `Solved && after < before - ImprovementEpsilon`
(1e-9). When no `Oltc` is matched, `Taps` is empty, `before == after` and `Improved` is false. If the base case does not converge,
`Solved` is false and `Taps` is empty.

### Loss allocation

`LossAllocation.Allocate(model, result)` decomposes a solved `PowerFlowResult` and runs no solver. For each entry of
`result.LineFlows`, in branch order (the list holds a zero-flow placeholder for an out-of-service branch, so it is index-aligned with
`model.Branches`):

```
PLoss_l = PFromMw + PToMw          QLoss_l = QFromMvar + QToMvar                (ch. 3, LineFlow)
IsTransformer_l = Branches[l].IsTransformer
TotalPLossMw = sum PLoss_l         TotalQLossMvar = sum QLoss_l
ShuntPMw = sum over buses of Gs_i * Vm_i^2                                      (MW; Gs is MW at 1 pu)
ReconciliationResidualMw = result.TotalLossMw - TotalPLossMw - ShuntPMw
```

`result.TotalLossMw` is the sum of the net bus injections, so it also holds the real power the bus shunt conductances draw,
which flows in no branch. That power is `ShuntPMw` (the `losses` verb prints it as `Bus shunt conductance` when the case has
any, and writes `shunt_p_mw`), so the residual is rounding: on case300, whose 17 conductive bus shunts total 1.30 MW at 1 pu,
`ShuntPMw` is 1.21 MW. With no conductive shunt `ShuntPMw` is exactly 0 and the residual is the plain `TotalLossMw -
TotalPLossMw`. `S4SolverConvergenceTests.N053_case300_losses_reconcile_once_the_shunt_conductance_is_counted` and
`N053_without_conductive_shunts_the_residual_is_unchanged_to_the_bit` pin both.

Zones are the model's boundaries. `Boundary.RequireResolvedBranchNames` first throws unless every boundary branch name names
exactly one branch of the model. A branch whose non-empty `Name` appears in a boundary's branch list belongs to that boundary, and
when a name appears in several boundaries the first boundary wins (`TryAdd`). Every other branch, including every branch with no name,
belongs to `Other`. Each zone's row is the sum of its branches' `PLoss` and `QLoss` and their count (out-of-service branches included).
Zone rows are sorted by real loss, descending, then `Other` is appended when it holds any branch. The allocation is by branch and zone
only; it does not apportion losses to generators or loads.

The `losses` verb loads its case through the shared loader (`SingleCasePositional`, then `LoadCaseArgument`) and solves the base case
with a bare Newton engine chosen by the shared rule (`backend.NewtonSolver`: sparse at every size on the CPU, since
`SolverSelector.DefaultSparseThreshold` = 0; the dense and sparse engines are bit-identical on the CPU), with default options. It does not use the radial test and runs no OLTC loop,
so its total differs from the `gridsim <case> --once` report on a case with tap changers.

## Algorithm

### The bounded-variable simplex

`BoundedSimplex.Solve(a, b, c, lo, hi, tol, maxIter)` (`src/GridSim.Core/Numerics/BoundedSimplex.cs`) is the dense reference solver for

```
minimise c.x   subject to   A x = b,   lo <= x <= hi          (any hi may be +inf)
```

It is a two-phase primal simplex that handles bounds natively: a nonbasic variable rests at a finite bound (the lower in preference, else
the upper, else 0 for a free variable), and the ratio test includes the entering variable's own opposite bound, so a step can be a bound flip
with no basis change.

1. **Set-up.** One artificial variable per row. Each structural variable starts at its resting bound; a row whose initial residual is negative is
   negated, so every artificial starts at a non-negative value and the artificials form an identity basis.
2. **Phase I** minimises the sum of the artificials. If the sum is above `PhaseOneFeasibilityTolerance` (1e-7, absolute) the status is `Infeasible`.
3. **Phase II** pins every artificial to 0 and minimises `c`.
4. **Pricing.** Dantzig: the nonbasic variable with the most negative reduced cost for an increase from a lower bound (or the most positive
   for a decrease from an upper bound) beyond `tol` (1e-9), the first index winning a tie. A free nonbasic variable is priced in both
   directions.
5. **Ratio test.** The step is the smallest of each basic variable's distance to its bound divided by its rate, and the entering variable's own
   bound gap. A candidate replaces the current leaving row only when its step is smaller by more than `RatioTieMargin` (1e-12), so among ties
   the first row wins. An unbounded step gives `Unbounded`.
6. **Basis inverse.** Updated by eta (product-form) row operations. A chosen pivot below `EtaPivotFloor` (1e-12) is refused (the LP throws,
   naming the pivot) before the basis changes. The ratio test admits only rows with |alpha| above the tolerance (1e-9), so this cannot
   happen at the default tolerance, and a tolerance that is not positive and finite throws `ArgumentOutOfRangeException`
   (`SimplexPivotFloorTests`). A refactorisation (Gauss-Jordan with partial pivoting, first strict maximum) runs every `RefactorInterval` (64)
   iterations, bound flips included. A refactorisation pivot below `RefactorPivotClamp` (1e-14) is clamped to that magnitude with its sign.
7. **Limit.** Each phase stops after `DefaultMaxIterations` (20000) iterations with status `IterationLimit`.

The statuses are `LpStatus.Optimal`, `Infeasible`, `Unbounded` and `IterationLimit`. The constants steer the pivot path, so changing any of them
can move the reported optimum in its last digits or choose another optimal vertex of a degenerate LP.

### The structured security simplex

`DcOpfSecuritySimplex` (`src/GridSim.Core/Analysis/DcOpfSecuritySimplex.cs`) runs the same algorithm as `BoundedSimplex.Solve` on the DC-OPF's LP without
building its matrices, and returns the dense solver's answer bit for bit. It differs only in how the matrices are stored:

- **No constraint matrix.** A dispatch column is a 1 in the balance row plus one PTDF entry per rated line. Generators on one bus share one column, so the
  PTDF block is held once per distinct generator bus, for the rated lines only, row-major (`L` x `distinct buses`). A slack or artificial column is a
  single +/-1 and is never stored. `BuildSecurityLp` builds this problem; it throws `InvalidOperationException` when the block would exceed
  `Array.MaxLength` cells.
- **Basis inverse in `DcOpfHybridColumns`.** A column with one non-zero is stored as that value, and a column becomes dense only when a row operation
  gives it a second. The Gauss-Jordan working matrix of each refactorisation is held the same way.
- **Grouped pricing.** One reduced cost is computed per (PTDF column, objective coefficient) group of generators, because generators with an identical
  column and cost have bit-identical reduced costs.

The only operations skipped are multiplications by an exact zero and the additions of their zero results, which can change at most the sign of a zero.
No tolerance, floor or iteration cap is changed: every constant is read from `BoundedSimplex`. The dense path stays callable as
`DcOptimalPowerFlow.SolveDenseReference` for the parity tests. The source comments record that the dense LP on `gb-full` (5,876 rated lines, 5,318
generators) needs 1.9 GB of arrays, against 73 MB for the structured PTDF block.

## Parameters and defaults

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| Unbounded Pmax | `1e6` | MW | Stand-in for an infinite `Pmax` in the LP bounds. | `DcOptimalPowerFlow.UnboundedPmaxMw` |
| Balance tolerance | `1e-6` | MW | Feasibility margin of the merit order (demand versus the dispatchable range) and of a dead island's load. | `DcOptimalPowerFlow.BalanceToleranceMw` |
| Merit-order stop | `1e-9` | MW | The merit-order fill stops at or below this unserved remainder. | `DcOptimalPowerFlow.MeritOrderFillStopMw` |
| Binding tolerance | `1e-3` | MW | A rated line with `abs(flow) >= RateA - this` is reported binding. | `DcOptimalPowerFlow.BindingToleranceMw` |
| Default cost | `50` | GBP/MWh | Cost of an empty or unrecognised fuel type. | `DcOptimalPowerFlow.DefaultCostPerMwh` |
| Islanding tolerance | `1e-9` | - | A branch with `abs(1 - Phi_kk)` below this is a bridge. | `DistributionFactors.IslandingSensitivityTolerance` |
| LP tolerance | `1e-9` | - | Reduced-cost and ratio-test threshold. | `BoundedSimplex.DefaultTolerance` |
| LP iteration cap | `20000` | iterations per phase | Stops a stalled phase with `IterationLimit`. | `BoundedSimplex.DefaultMaxIterations` |
| Phase I tolerance | `1e-7` | - | Largest sum of artificials accepted as feasible (absolute). | `BoundedSimplex.PhaseOneFeasibilityTolerance` |
| Refactor interval | `64` | iterations | Simplex iterations (basis changes and bound flips) between refactorisations of the basis inverse. | `BoundedSimplex.RefactorInterval` |
| Ratio tie margin | `1e-12` | - | A leaving-row candidate must beat the current step by more than this. | `BoundedSimplex.RatioTieMargin` |
| Eta pivot floor | `1e-12` | - | A chosen pivot below this is refused (unreachable at the default tolerance). | `BoundedSimplex.EtaPivotFloor` |
| Refactor pivot clamp | `1e-14` | - | Smallest pivot magnitude kept in a refactorisation. | `BoundedSimplex.RefactorPivotClamp` |
| AC-OPF loss tolerance | `1e-3` | MW | Loss change that counts as settled. CLI `--loss-tol`, above 0. | `AcOptimalPowerFlow.Solve` parameter `lossToleranceMw` |
| AC-OPF pass limit | `20` | passes | Outer-loop cap. CLI `--max-iter`, at least 1. | `AcOptimalPowerFlow.Solve` parameter `maxIterations` |
| CPF initial step | `0.25` | multiplier | First increment of lambda. CLI `--step`, above 0. | `ContinuationPowerFlow.MaxLoadability` parameter `step` |
| CPF minimum step | `1e-3` | multiplier | The search ends when the step is below this. CLI `--min-step`, in (0, `--step`]. | `ContinuationPowerFlow.MaxLoadability` parameter `minStep` |
| CPF solve limit | `200` | power flows | Cap on solves, failures included. CLI `--max-steps`, at least 1. | `ContinuationPowerFlow.MaxLoadability` parameter `maxSteps` |
| Tap target | `1.0` | pu | Voltage the objective aims at. CLI `--target`, above 0. | `TapOptimizer.Flatten` parameter `targetVpu` |
| Tap improvement threshold | `1e-9` | pu^2 | The objective must fall by more than this to count as improved. | `TapOptimizer.ImprovementEpsilon` |
| Tap initial step | `0.02` | relative to the seed | Initial Nelder-Mead spread, 0.02 pu at a seed of 1.0. | `TapOptimizer.InitialTapStepPu` |
| Non-converged penalty | `1e9` | pu^2 | Objective of a tap vector whose power flow fails. | `TapOptimizer.NonConvergedPenalty` |
| Tap bound penalty | `1e6` plus `1e6` per pu | pu^2 | Added when a tap leaves its range; no solve is run. | `TapOptimizer.TapBoundPenalty`, `TapBoundPenaltyPerPu` |
| Default tap step | `0.0125` | pu per position | Snap step when an OLTC's `StepPu` is 0 or less. | `Oltc.DefaultStepPu` |
| Nelder-Mead limits | `2000`, `1e-8`, `1e-12` | iterations, x spread, f spread | Search stop. | `NelderMead.Options.MaxIterations`, `XTolerance`, `FTolerance` |
| PTDF and LODF display floor | `1e-4` | MW/MW | Factors at or below this absolute value are not printed by `ptdf` and `lodf`. | `CliDefaults.SensitivityDisplayFloor` |
| Dispatch display floor | `1e-3` | MW | A unit at or below this output is not printed by `dcopf` and `acopf`. | `CliDefaults.DcopfDispatchDisplayFloorMw` |
| Rows listed | `12` | rows | `--top` of `dcopf`, `acopf`, `ptdf`, `lodf` and `dcpf`. | `Program.cs` (`FlagInt(a, "--top", 12)`) |
| Sparse threshold | `0` | buses | Above this `acopf`, `cpf`, `taps` and `losses` use the sparse Newton engine (so every CPU solve is sparse). | `SolverSelector.DefaultSparseThreshold` |
| Overload threshold | `100` | % of `RateA` | A branch above this loading is counted by `dcpf`. | `ContingencyAnalysis.OverloadThresholdPercent` |

All constants of `DcOptimalPowerFlow`, `DistributionFactors` and `TapOptimizer` listed above are pinned to the literals they replaced
by `Ws13CoreConstantsAntiDriftTests`.

## Case loading and exit behaviour of the verbs

The eight verbs load their case in three places, with the same rule for what a case argument may be.

| Verb | How the case is chosen | A load failure | No case given |
|---|---|---|---|
| `dcopf`, `ptdf`, `lodf` | First free argument after the verb: a folder holding `system.json` (`Gated.Dir`), else a name ending `.m` (`Gated.Matpower`), else `LoadNamed` | `error loading case '<arg>': <reason>`, exit 1 | `usage: ...`, exit 2 |
| `acopf`, `cpf`, `taps`, `dcpf` | The same rule, through `LoadAnalysisCase` | `error loading case '<arg>': <reason>`, exit 1 | `usage: ...`, exit 2 |
| `losses` | `SingleCasePositional` (default `case9`), then `LoadCaseArgument`: the same three forms, and an existing folder without `system.json` is loaded so the loader says what is wrong with it | An unknown name: `error: losses: unknown case '<arg>' (built-in names: ...)`, exit 2. A missing `.m` file or an unloadable folder: `error: <reason>`, exit 1. A second case argument: exit 2 | Solves `case9` |

The free argument is the first token after the verb that is neither a `--flag` nor the value a flag consumes. A `.m` name is
searched as given, then under `data/matpower`, then by file name through its subfolders (`dcopf case33bw.m` loads
`data/matpower/dist/case33bw.m`). `LoadNamed` accepts `case9`/`ieee9`, `case14`, `case30`, `case57`, `case118`, `case300`,
`case145`, `case24`/`rts`, `cigre`, `uk`/`gb`/`gb-reduced`, `gb-full`, `gb-synthetic`/`synthetic`, `gb-reduced-29`,
`gb-network-2224`, `pypsa-gb-reduced`, a name ending `.m`, and a name ending `.json` (a JSON v2 case manifest,
`Gated.V2Case`). `pypsa-gb-zonal` is refused (`Gated.NotAcSolvable`: a transport model with no power flow, exit 1). Any other
name throws `ArgumentException` listing `BuiltInCases.Names`. A licence refusal (a user network above the tier's bus limit)
exits 2 from inside `Gated`.

**Flags.** The result-writer flags `--json`, `--csv` and `--out` are taken off the argument list first (`CliOutput.TakeFlags`;
all eight verbs are writer verbs). The process-wide gate then refuses a flag that no verb knows (exit 2). `VerbFlags.Check`
then refuses a flag that the verb's own section of `0019 CLI reference.md` does not name, with
`error: <verb> does not take <flag> (it is not one of <verb>'s flags, so it would be silently ignored). ...` and exit 2, before
the verb runs and with nothing on standard output. `--dir` is refused for every verb in this chapter this way, including
`losses`. `--costs` and `--prices` are refused outside `dcopf` and `acopf` with
`error: --costs applies to dcopf and acopf only (got '<verb>').`

| Verb | Exit 0 | Exit 1 | Exit 2 | Exit 3 |
|---|---|---|---|---|
| `dcopf` | A feasible dispatch | A load failure; an exception from the solve; a `--costs` problem | No case; a bad flag value; an unknown flag or a flag of another verb | An infeasible dispatch |
| `acopf` | Converged | A load failure; an exception from the solve; a `--costs` problem | As `dcopf` | One of the four failures (AC solve, pass-0 stop, infeasible final dispatch, pass limit) |
| `cpf` | A bracketed nose | A load failure | As `dcopf`; `--pv-out` not ending in `.csv` | The base case (`lambda = 1`) did not converge; `--max-steps` ran out before the nose was bracketed (`NOT bracketed: ...`, `lambda` a lower bound) |
| `taps` | A schedule, improved or not | No OLTC in the case; an OLTC that names no branch, several branches or a missing controlled bus; a load failure | As `dcopf` | The base case did not converge; the tap search stopped at its iteration cap |
| `ptdf` | Factors printed (no rows when none exceeds the floor) | A load failure; `Build` failed | No case; `--bus` missing, negative or not a bus of the case; a bad flag value | - |
| `lodf` | Factors printed, or a bridge outage | A load failure; `Build` failed | No case; `--branch` missing or outside `0..N-1`; a bad flag value | - |
| `dcpf` | The DC model solved | A load failure; the DC model cannot be formed | No case; a negative `--top`; a bad or foreign flag | An island carries load and has no generator |
| `losses` | The tables | A missing `.m` file; an unloadable folder | An unknown name; a second case argument; a foreign flag | The base case did not converge (`error: base case did not converge.`) |

`DcOptimalPowerFlow` throws `InvalidDataException` for `Pmin` above `Pmax` or a non-finite `Pmin`. On every path, with or
without `--costs` and `--prices`, both verbs catch it (and `InvalidOperationException`) and exit 1 as `[dcopf] <message>` or
`[acopf] <message>`. Output that reaches the console before a failure stays: `acopf`, `cpf` and
`taps` print their header line to standard output and then the failure message to standard error. `ptdf` and `lodf` load the
case and factor it before they test `--bus` or `--branch`, so a bad case exits 1 ahead of a missing flag.

**`dcopf` infeasible messages.** A case with several islands prints the island list and then
`INFEASIBLE: at least one island has no dispatch that meets its demand (within its line limits, when they are enforced); see the island list.`
A connected case prints one of three messages: `INFEASIBLE: no dispatch within the units' output limits keeps every rated line
within its rating (security LP Infeasible); '--no-limits' drops the line limits.` when the security LP is `Infeasible`;
`INFEASIBLE: the security LP stopped without proving an optimum (status X); no secure dispatch is reported.` for another LP
status; and `INFEASIBLE: generation capability cannot meet demand (N MW of in-service capability for M MW of demand).` only when
no LP ran. With `--costs` a failed solve adds `solver: <reason> (status X)`. The `--costs` result keys carry no currency (a curve is
in the case's own currency). A branch with no name is printed as `from-to`.

**`taps` output.** When the schedule leaves an OLTC's controlled bus outside that OLTC's own `target +/- deadband`, `taps` lists
each such OLTC (its local regulator would retap). A search cut off at its iteration cap exits 3 with the iteration count.

## Invariants

| Invariant | Pinned by |
|---|---|
| Without line limits the dispatch is a merit order: the cheapest unit is loaded to `Pmax`, the next takes the rest, and generation equals demand. | `DcOptimalPowerFlowTests.Unconstrained_dispatch_follows_merit_order` |
| A binding line forces energy onto a dearer unit, the balance is preserved, the result is flagged `NetworkConstrained`, and the cheap unit backs off. | `DcOptimalPowerFlowTests.A_binding_line_forces_redispatch_onto_a_costlier_generator` |
| `Pmin` is used as given: a unit with a negative `Pmin` dispatches below zero; `Pmin` above `Pmax`, and a non-finite `Pmin`, are rejected. | `AnalysisWs6Tests.Dc_opf_can_dispatch_storage_charging_below_zero`, `Dc_opf_rejects_pmin_above_pmax`, `Dc_opf_rejects_a_non_finite_pmin` |
| No LP status is reported when no LP ran. | `ConvergenceContractTests.Dc_opf_reports_its_security_lp_outcome` |
| The structured security simplex returns the dense solver's dispatch, binding set and status bit for bit on the bundled cases, reproduces results recorded from the original dense code, and matches `BoundedSimplex` on hundreds of randomised degenerate LPs through many refactorisations. | `DcOpfSecuritySimplexParityTests.Structured_security_dispatch_is_bit_identical_to_the_dense_reference`, `Structured_security_dispatch_reproduces_the_recorded_original_results`, `Structured_simplex_matches_the_dense_simplex_on_randomised_degenerate_lps`, `Structured_simplex_matches_the_dense_simplex_through_many_refactorisations`, `Hybrid_columns_track_a_dense_matrix_through_swaps_scalings_and_eliminations`, `Hybrid_columns_fail_loud_on_misuse`, `Structured_simplex_rejects_an_inconsistent_problem` |
| `BoundedSimplex` reaches the textbook optimum, honours finite upper and non-zero lower bounds, and reports infeasible and unbounded programs, and prices a free variable in both directions. | `BoundedSimplexTests` (five tests), `Wave3NumericsTests.BoundedSimplex_optimises_over_a_free_variable` |
| A pivot below the floor is refused by name before the basis changes, the default tolerance never chooses one, and both solvers check the pivot before the step. | `SimplexPivotFloorTests.A_tolerance_below_the_floor_that_chooses_a_tiny_pivot_is_refused_by_name`, `The_default_tolerance_never_chooses_a_pivot_below_the_floor`, `Both_solvers_check_the_pivot_before_the_step_and_no_longer_refactorise_around_it` |
| A connected network has one island referenced at the slack; each island of a split network takes its own reference by the island rule; a PTDF column withdraws at its island's reference and leaves other islands untouched. | `DcIslandTests.A_connected_network_has_one_island_referenced_at_the_slack`, `Each_island_gets_its_own_reference_by_the_AC_island_rule`, `Ptdf_withdraws_at_the_bus_islands_reference_and_leaves_other_islands_untouched` |
| A zero-reactance branch that alone joins two islands fails loud. | `DcIslandTests.A_zero_reactance_branch_that_alone_joins_two_islands_fails_loud` |
| `dcopf` dispatches each island on its own balance, solves each as the island alone, reports a binding line with its full-model index, reports an island without generation (infeasible when it has load, harmless when it has none), and refuses a mismatched cost vector. | `DcIslandTests.Dcopf_dispatches_each_island_on_its_own_balance`, `Each_island_is_solved_exactly_as_the_island_alone`, `A_binding_line_in_a_second_island_is_reported_with_its_full_model_index`, `An_island_with_no_generation_is_reported_and_its_load_makes_the_dispatch_infeasible`, `An_island_with_no_generation_and_no_load_is_reported_but_harmless`, `A_cost_vector_that_does_not_match_the_generators_fails_loud_with_islands` |
| `gb-full` has two DC islands, each with its own slack and generation. | `DcIslandTests.Gb_full_has_two_DC_islands_each_with_its_own_slack_and_generation` |
| PTDF equals the analytic values on an equal-reactance triangle, and the slack column is zero. | `DistributionFactorsTests.PTDF_matches_the_analytic_triangle_and_the_slack_column_is_zero` |
| The LODF diagonal is -1, and the post-outage flow equals the flow of the network with the branch removed. | `DistributionFactorsTests.LODF_diagonal_is_minus_one_and_satisfies_the_re_solve_identity` |
| A bridge outage is flagged and its LODF column is NaN. | `DistributionFactorsTests.A_radial_bridge_outage_is_flagged_as_islanding` |
| The DC power flow's branch flows are the bits `DcFlows` gives, every bus and island balances, the angles reproduce the flows, and each island of a multi-island case is reported. | `DcPowerFlowAndPvCurveTests.Dc_power_flow_branch_flows_are_bit_identical_to_DcFlows`, `Dc_power_flow_balances_every_bus_and_island`, `Dc_power_flow_angles_reproduce_the_flows`, `Dc_power_flow_reports_each_island_of_a_multi_island_case` |
| The PV trace's `MaxLambda` is `MaxLoadability`'s answer bit for bit, and its first point is the base-case solve. | `DcPowerFlowAndPvCurveTests.The_pv_trace_max_lambda_is_bit_identical_to_MaxLoadability`, `The_first_pv_point_is_the_base_case_solve_bit_for_bit` |
| One taxonomy prices every fuel string: the cost of each bundled type, a gas turbine as `Ocgt`, hydrogen and pumped storage not as `Hydro`, whole-word matching with word-start keywords, splitting at every non-alphanumeric character. | `FuelTypesTaxonomyTests.Every_bundled_type_reads_the_same_in_every_reader`, `A_gas_turbine_is_ocgt_to_every_reader`, `Hydrogen_and_pumped_storage_are_no_longer_priced_as_hydro`, `Keywords_match_whole_words_by_their_start`, `The_substring_false_positives_read_as_other_in_every_reader`, `Words_split_at_every_non_alphanumeric_character` |
| Cost curves: case9 quadratic costs reach the closed-form equal-marginal-cost dispatch, a convex piecewise curve fills its segments in order, prices match the published values, a binding line separates the prices, a unit with no curve is refused, demand beyond the fleet is infeasible with no prices, islands are priced each at its own marginal unit, and the cost file overlays by index and by name. | `GeneratorCostsAndPricesTests.Case9_quadratic_costs_reach_the_closed_form_equal_marginal_cost_dispatch`, `A_convex_piecewise_linear_cost_fills_its_segments_in_order_and_prices_at_the_marginal_slope`, `Pjm_5_bus_dispatch_matches_the_published_dc_opf_and_its_prices_match_highs`, `A_binding_line_separates_the_prices_on_a_hand_case`, `A_unit_with_no_curve_is_refused_when_curves_are_in_use`, `Demand_beyond_the_fleet_is_infeasible_with_curves_and_reports_no_prices`, `Two_islands_with_curves_are_priced_each_at_its_own_marginal_unit`, `The_cost_file_overlays_the_case_by_index_and_by_name_and_round_trips` |
| `acopf` without cost curves is unchanged, with them reports the curve cost, and its final-pass prices are the loss-inflated DC prices; a cost vector with prices is refused. | `GeneratorCostsAndPricesTests.Acopf_without_cost_curves_is_unchanged_and_with_them_reports_the_curve_cost`, `AcOpfPricesTests.Pjm_5_bus_final_pass_prices_are_the_loss_inflated_dc_prices`, `Prices_and_a_cost_vector_are_refused_together` |
| `--costs` and `--prices` are known flags, documented, and refused outside `dcopf` and `acopf`. | `GeneratorCostsAndPricesTests.Dcopf_costs_and_prices_flags_are_known_documented_and_refused_elsewhere` |
| The AC-OPF converges on case14 to a dispatch whose total equals demand plus the reported losses to within 0.5 MW. | `AcOptimalPowerFlowTests.Converges_to_a_loss_aware_ac_feasible_dispatch` |
| A loop that cannot settle is not reported converged. | `ConvergenceContractStudyTests.Ac_opf_that_cannot_settle_is_not_converged` |
| Each island pays for its own losses, and a connected network keeps its loss loop to the bit. | `S4SolverConvergenceTests.N070_each_island_pays_for_its_own_losses`, `N070_a_connected_network_keeps_its_loss_loop_to_the_bit` |
| The `acopf` failure causes are told apart (AC solve at pass 0, iteration cap, infeasible final dispatch), a converged run reports no failure, and the CLI names the cause. | `S4SolverDataFollowUpsTests.N111_case118_acopf_fails_at_pass_0_on_the_ac_solve_and_says_so`, `N111_the_iteration_cap_is_reported_as_the_cap`, `N111_gb_synthetic_settles_on_an_infeasible_dc_dispatch`, `N111_a_converged_run_reports_no_failure_and_keeps_its_numbers`, `N111_cli_acopf_names_the_failed_ac_solve_and_the_flat_price` |
| `dcopf` tells a line-limit infeasibility from a shortfall of capability. | `S4SolverDataFollowUpsTests.N111_cli_dcopf_tells_line_limit_infeasibility_from_short_capability` |
| An infeasible pass 0 that no later pass can cure stops the loop at pass 0, and a case where more demand could cure it keeps the full loop. | `N173AcopfEarlyStopTests.Demand_above_total_capability_stops_at_pass_0`, `Line_limits_proved_infeasible_stop_at_pass_0_when_every_unit_may_produce_nothing`, `Line_limits_with_a_unit_whose_pmin_is_above_zero_keep_the_full_loop`, `Demand_below_total_pmin_keeps_the_full_loop`, `Gb_synthetic_stops_at_pass_0_with_the_same_verdict`, `Cli_acopf_gb_synthetic_names_the_pass_0_stop_and_exits_3`, `Feasible_runs_are_untouched` |
| The load search finds a multiplier above 1 on case9, and does not converge far past it. | `ContinuationPowerFlowTests.Finds_a_loadability_margin_beyond_the_base_case` |
| The search reaches the nose without an exception. | `ConvergenceContractStudyTests.Continuation_reaches_the_nose_without_an_exception` |
| The tap schedule improves the objective, stays inside the OLTC band, lands on the step grid, and lowers the worst voltage deviation when applied. | `TapOptimizerTests.Flattens_the_profile_across_both_transformers` |
| A case with no OLTC returns an empty, unimproved schedule with equal objectives. | `TapOptimizerTests.No_oltcs_returns_the_unchanged_state` |
| Branch losses sum to the solver's total (residual below 1e-6 MW), zone rows plus `Other` sum to the total, and no branch loss is negative beyond -1e-6 MW. | `LossAllocationTests.Branch_losses_sum_to_total`, `Zone_losses_plus_other_equal_total`, `Every_branch_loss_is_non_negative` |
| The bus shunt draw closes the case300 residual, and without conductive shunts the residual is unchanged to the bit. | `S4SolverConvergenceTests.N053_case300_losses_reconcile_once_the_shunt_conductance_is_counted`, `N053_without_conductive_shunts_the_residual_is_unchanged_to_the_bit` |
| `losses` loads its case through the shared loader (unknown name exit 2, missing `.m` exit 1, second case argument exit 2). | `CliContingencyVerdictTests.Losses_loads_its_case_through_the_shared_loader` (a source-text test: no test runs `losses` on an unknown name) |
| The CLI result files carry the printed bits and every row, `dcpf --out` writes the same files byte for byte, a run with no result writes no file, and a failing verdict is still written. | `CliResultFilesProcessTests.Dcpf_out_csv_still_writes_the_f18_files_byte_for_byte`, `Json_numbers_are_the_exact_bits_the_console_prints_in_round_trip_form`, `Json_and_csv_carry_the_same_bits_and_every_row_not_just_the_printed_top`, `A_run_that_stops_before_its_result_writes_no_file_and_says_so`, `A_verdict_that_fails_is_still_written_with_its_exit_code` |
| A flag of another verb exits 2 naming it with nothing on standard output, a documented flag is accepted, and every flag a verb's code reads is in its accepted set. | `CliS4CliAndLoaderProcessTests.N103_a_flag_of_another_verb_exits_2` (`ptdf --branch`), `VerbFlagsTests.A_flag_of_another_verb_is_refused`, `A_documented_flag_is_accepted`, `Every_flag_a_verbs_code_reads_is_accepted_for_it` |
| The built binary starts each verb on case9 and exits 0 with the verb's header: `ptdf`, `lodf`, `dcopf`, `acopf`, `cpf` and `losses` (`PTDF - `, `LODF - `, `DC-OPF - `, `ACOPF (DC dispatch + AC loss loop) - `, `max loadability`, `Total losses:`), and `taps case9` exits 1 with `no on-load tap changers`. | `CliProcessSmokeTests.Verb_runs_on_case9_and_exits_0`, `Taps_on_a_case_without_tap_changers_is_an_input_error_exit_1` |

What is pinned about the CLI text and exit codes is what the rows above name: the header lines of six verbs and the `taps`
failure (`CliProcessSmokeTests`), the `acopf` and `dcopf` failure messages and the pass-0 message, the cost per fuel kind
(`FuelTypesTaxonomyTests`), and the exit 2 of a foreign flag. No test pins the console text of `dcpf`, the `cpf` and `taps`
failure messages, the exit codes of `losses` on bad input, the tie-break of equal-cost dispatch, or the half-even rounding of
the tap snap (the tap test checks only that every tap lies on the grid).

## Worked example

Every number below is from a run in `docs/_captures/opf/`.

### PTDF by hand: the three-bus case

`docs/_captures/opf/tri3/` has buses 1 (slack, generator G1), 2 (generator G2) and 3 (load 100 MW), and three lines of `X = 0.1` pu: `L13` (branch 0),
`L23` (branch 1) and `L12` (branch 2). Each `b = 1 / 0.1 = 10`. Removing the slack row and column (bus 1) leaves the buses 2 and 3:

```
B' = [ 20  -10 ]        B'^-1 = (1/300) * [ 20  10 ]
     [-10   20 ]                          [ 10  20 ]
```

An injection of 1 at bus 2 is `rhs = [1, 0]`, so `theta = [20/300, 10/300] = [0.066667, 0.033333]` and `theta_1 = 0`:

```
L12 (1->2):  10 * (0 - 0.066667)         = -0.66667
L13 (1->3):  10 * (0 - 0.033333)         = -0.33333
L23 (2->3):  10 * (0.066667 - 0.033333)  = +0.33333
```

The run agrees to the printed four decimals:

```
$ gridsim ptdf docs/_captures/opf/tri3 --bus 2
PTDF - Three-bus worked example (3 buses): +1 MW injected at bus 2 (withdrawn at slack 1) flows on:
    branch 2    1->2           PTDF -0.6667  (-66.7%)
    branch 0    1->3           PTDF -0.3333  (-33.3%)
    branch 1    2->3           PTDF  0.3333  ( 33.3%)
[exit 0]
```

For bus 3, `rhs = [0, 1]` gives `theta = [10/300, 20/300]` and the factors `L13 = -0.66667`, `L23 = -0.33333`, `L12 = -0.33333`:

```
$ gridsim ptdf docs/_captures/opf/tri3 --bus 3
PTDF - Three-bus worked example (3 buses): +1 MW injected at bus 3 (withdrawn at slack 1) flows on:
    branch 0    1->3           PTDF -0.6667  (-66.7%)
    branch 1    2->3           PTDF -0.3333  (-33.3%)
    branch 2    1->2           PTDF -0.3333  (-33.3%)
[exit 0]
```

A ring check on case9: bus 5 reaches the slack through bus 4, either directly (`4->5`, `X = 0.092`) or round the ring 5-6-7-8-9-4. The ring reactance is
0.17 + 0.1008 + 0.072 + 0.161 + 0.085 = 0.5888, and a DC flow divides inversely with reactance, so the direct line carries `0.5888 / (0.5888 + 0.092) = 0.86486`
and the ring the other 0.13514. Branches 3 (`3->6`) and 6 (`8->2`) are dead-end generator connections and carry nothing.

```
$ gridsim ptdf case9 --bus 5
PTDF - IEEE 9-bus (WSCC) (9 buses): +1 MW injected at bus 5 (withdrawn at slack 1) flows on:
    branch 0    1->4           PTDF -1.0000  (-100.0%)
    branch 1    4->5           PTDF -0.8649  (-86.5%)
    branch 7    8->9           PTDF  0.1351  ( 13.5%)
    branch 4    6->7           PTDF  0.1351  ( 13.5%)
    branch 8    9->4           PTDF  0.1351  ( 13.5%)
    branch 2    5->6           PTDF  0.1351  ( 13.5%)
    branch 5    7->8           PTDF  0.1351  ( 13.5%)
[exit 0]
```

### LODF by hand

Outage of `L13` (branch 0). The transfer problem is +1 at bus 1 (the slack, dropped) and -1 at bus 3: `rhs = [0, -1]`, `theta = [-10/300, -20/300]`. Then
`Phi[L13,L13] = 10 * (0 + 0.066667) = 2/3`, `Phi[L23,L13] = 10 * (-0.033333 + 0.066667) = 1/3` and `Phi[L12,L13] = 10 * (0 + 0.033333) = 1/3`. With
`1 - Phi_kk = 1/3`, `LODF[L23] = LODF[L12] = (1/3) / (1/3) = 1.0`: the whole flow of `L13` moves onto the path 1->2->3.

```
$ gridsim lodf docs/_captures/opf/tri3 --branch 0
LODF - Three-bus worked example (3 buses): outage of branch 0 (1->3) redistributes onto:
    branch 1    2->3           LODF  1.0000  (100.0% of the lost flow)
    branch 2    1->2           LODF  1.0000  (100.0% of the lost flow)
[exit 0]
```

### DC power flow by hand

On `tri3` the case is balanced when it loads: the `ocgt` unit on bus 2 schedules 10 MW and the slack unit 92 MW (the 100 MW demand
plus a 2 % allowance, less the 10 MW). Bus 1 is the reference, so only buses 2 and 3 enter the right-hand side:
`P / BaseMva = [10, -100] / 100 = [0.1, -1.0]`. With the `B'^-1` above,

```
theta_2 = (20 x 0.1 + 10 x (-1.0)) / 300 = -0.026667 rad = -1.5279 deg
theta_3 = (10 x 0.1 + 20 x (-1.0)) / 300 = -0.063333 rad = -3.6287 deg
L13 (1->3):  10 x (0 + 0.063333)          x 100 = 63.333 MW      63.333 / 60  = 105.6 %
L23 (2->3):  10 x (-0.026667 + 0.063333)  x 100 = 36.667 MW      36.667 / 200 =  18.3 %
L12 (1->2):  10 x (0 + 0.026667)          x 100 = 26.667 MW      26.667 / 200 =  13.3 %
```

The reference injects `-(10 - 100) = 90` MW, against the 92 MW the slack unit schedules, and the island's scheduled
generation is 102 MW. The run agrees; angles and injections are in the result file (`--csv`), the console shows the flows:

```
$ gridsim dcpf docs/_captures/opf/tri3
DC power flow - Three-bus worked example (3 buses, 3 branches)  [linear: series reactance only; R, charging, shunts, taps and phase shifts ignored; |V| = 1 pu; lossless]
  island ref bus 1          3 buses  demand      100.0 MW  scheduled generation      102.0 MW  reference injects       90.0 MW
  1 of 3 rated branch(es) above 100% of RateA
  largest flows:
      branch 0     L13                   1 -> 3           63.33 MW    105.6% of 60 MVA
      branch 1     L23                   2 -> 3           36.67 MW     18.3% of 200 MVA
      branch 2     L12                   1 -> 2           26.67 MW     13.3% of 200 MVA
[exit 0]
```

### DC-OPF by hand

The dispatch problem on `tri3`: G1 (nuclear, 10 GBP/MWh, 0 to 200 MW) at the slack bus, G2 (`ocgt`, 95 GBP/MWh, 0 to 200 MW) at bus 2, load 100 MW at bus 3,
`L13` rated 60 MVA and `L23`, `L12` rated 200 MVA. G1 is on the reference bus, so its PTDF column is zero. The flows from the load alone, `f0 = -100 x PTDF(bus 3)`,
are `L13 = +66.667`, `L23 = +33.333` and `L12 = +33.333`. The LP rows, in the order the code builds them (balance, then the rated lines in branch order), are:

```
balance   P1 + P2                     = 100
L13       -(1/3) P2 + s13             = 60 - 66.667  = -6.667
L23       +(1/3) P2 + s23             = 200 - 33.333 = 166.667
L12       -(2/3) P2 + s12             = 200 - 33.333 = 166.667
bounds    0 <= P1, P2 <= 200          0 <= s13, s23, s12 <= 120, 400, 400
minimise  10 P1 + 95 P2
```

The `L13` row gives `s13 = -6.667 + P2 / 3 >= 0`, so `P2 >= 20`. G2 is dearer, so the optimum takes the smallest `P2`, which is 20, and `P1 = 80`. The slacks follow: `s13 = 0`
(the line is at its limit), `s23 = 160` (flow `200 - 160 = 40`) and `s12 = 180` (flow 20). The cost is 80 x 10 + 20 x 95 = 2,700 GBP/h. Without the line row the same
LP gives `P1 = 100`, cost 1,000 GBP/h, and a flow of 66.667 MW on a 60 MVA line. The run agrees on the dispatch, the cost and the binding line (`60.0 MW / 60 MVA`):

```
$ gridsim dcopf docs/_captures/opf/tri3
DC-OPF - Three-bus worked example (3 buses)  [cheapest secure dispatch]
  demand 100 MW  generation 100 MW  total cost 2,700 GBP/h  (network-constrained redispatch applied)
  dispatch (by output):
      gen bus 1          80.0 MW  @    10 GBP/MWh
      gen bus 2          20.0 MW  @    95 GBP/MWh
  binding line limits:
      branch 0    L13              60.0 MW / 60 MVA
[exit 0]
```

```
$ gridsim dcopf docs/_captures/opf/tri3 --no-limits
DC-OPF - Three-bus worked example (3 buses)  [cheapest secure dispatch]
  demand 100 MW  generation 100 MW  total cost 1,000 GBP/h  (unconstrained merit order)
  dispatch (by output):
      gen bus 1         100.0 MW  @    10 GBP/MWh
[exit 0]
```

Relaxing the `L13` rating by 1 MW lets `P2` fall by 3 MW, which saves 3 x (95 - 10) = 255 GBP/h.

With equal costs the optimum is not unique. `case9` has three units at 50 GBP/MWh and demand 315 MW. The merit order (`--no-limits`) fills them in index order, 250 MW and
then 65 MW, and the LP lands on a different vertex of the same cost, 270 MW and 45 MW. Both cost 315 x 50 = 15,750 GBP/h:

```
$ gridsim dcopf case9 --no-limits
DC-OPF - IEEE 9-bus (WSCC) (9 buses)  [cheapest secure dispatch]
  demand 315 MW  generation 315 MW  total cost 15,750 GBP/h  (unconstrained merit order)
  dispatch (by output):
      gen bus 1         250.0 MW  @    50 GBP/MWh
      gen bus 2          55.0 MW  @    50 GBP/MWh
      gen bus 3          10.0 MW  @    50 GBP/MWh
[exit 0]
```

```
$ gridsim dcopf case9
DC-OPF - IEEE 9-bus (WSCC) (9 buses)  [cheapest secure dispatch]
  demand 315 MW  generation 315 MW  total cost 15,750 GBP/h  (unconstrained merit order)
  dispatch (by output):
      gen bus 3         270.0 MW  @    50 GBP/MWh
      gen bus 2          35.0 MW  @    50 GBP/MWh
      gen bus 1          10.0 MW  @    50 GBP/MWh
[exit 0]
```

### DC-OPF on cost curves and prices

`docs/_captures/opf/tri3-costs.json` gives G1 the curve `0.05 P^2 + 10 P` and G2 the curve `95 P`:

```
{
  "format": "gridsim-costs/1",
  "generators": [
    { "index": 1, "model": "polynomial", "coefficients": [0.05, 10, 0] },
    { "index": 2, "model": "polynomial", "coefficients": [0, 95, 0] }
  ]
}
```

The `L13` row still forces `P2 >= 20`. G1's marginal cost at `P1 = 80` is `10 + 0.1 x 80 = 18`, below G2's 95, so the
optimum is the same dispatch, 80 and 20 MW, and the cost is `10 x 80 + 0.05 x 80^2 + 95 x 20 = 1,120 + 1,900 = 3,020`. The
balance dual is G1's marginal cost, `y0 = 18`, and the energy component of every price. G2 sits strictly inside its bounds,
so its reduced cost is 0: `95 = y0 + y x PTDF[L13, 2] = 18 + y x (-1/3)`, where `y` is the dual of the `L13` row, so `y = -231`
(the line's shadow price is 231 per MW of extra rating). The prices follow from `LMP_k = y0 + y x PTDF[L13, k]`: bus 1 is 18,
bus 2 is `18 + (-231) x (-1/3) = 95` and bus 3 is `18 + (-231) x (-2/3) = 172`.

```
$ gridsim dcopf docs/_captures/opf/tri3 --costs docs/_captures/opf/tri3-costs.json --prices
DC-OPF - Three-bus worked example (3 buses)  [cheapest secure dispatch on generator cost curves: tri3-costs.json]
  demand 100 MW  generation 100 MW  total cost 3,020.00 cost units/h  (network-constrained)
  dispatch (by output):
      gen bus 1         80.00 MW  marginal   18.0000 cost units/MWh
      gen bus 2         20.00 MW  marginal   95.0000 cost units/MWh
  binding line limits:
      branch 0    L13              60.0 MW / 60 MVA
  nodal prices (LMP, cost units/MWh, lossless DC): 3 bus(es), highest 172.0000 at bus 3, lowest 18.0000 at bus 1
      bus 3      LMP   172.0000  (energy 18.0000, congestion 154.0000)
      bus 2      LMP    95.0000  (energy 18.0000, congestion 77.0000)
      bus 1      LMP    18.0000  (energy 18.0000, congestion 0.0000)
  line shadow prices (cost units/MWh per MW of extra rating):
      branch 0    L13              60.0 MW / 60 MVA  shadow price 231.0000
[exit 0]
```

### AC-OPF fixed point

`acopf case9` settles in 5 passes at 11.615 MW of losses with 270.0 MW on bus 3, 46.6 MW on bus 2 and 10.0 MW on bus 1. Generation is 270.0 + 46.6 + 10.0 = 326.6 MW, which equals demand plus losses
(315 + 11.615 = 326.615 MW), and the cost is 326.615 x 50 = 16,331 GBP/h (the table prints 16,331).
The case's own base dispatch (72.3, 163 and 85 MW) loses 4.64 MW (`gridsim losses case9`); this one loses 11.615 MW. The three units cost the same,
so the LP is indifferent between the two dispatches, and the loss enters the choice only through the demand the loop inflates.

```
$ gridsim acopf case9
ACOPF (DC dispatch + AC loss loop) - IEEE 9-bus (WSCC) (9 buses)  [not a full AC-OPF: no voltage, reactive-power or AC branch-flow limits]
  converged in 5 iteration(s)  losses 11.615 MW  cost 16,331 GBP/h
      gen bus 3         270.0 MW  @    50 GBP/MWh
      gen bus 2          46.6 MW  @    50 GBP/MWh
      gen bus 1          10.0 MW  @    50 GBP/MWh
[exit 0]
```

The lossless `tri3` settles in one pass, since the first measured loss is already below `--loss-tol`:

```
$ gridsim acopf docs/_captures/opf/tri3
ACOPF (DC dispatch + AC loss loop) - Three-bus worked example (3 buses)  [not a full AC-OPF: no voltage, reactive-power or AC branch-flow limits]
  converged in 1 iteration(s)  losses -0.000 MW  cost 2,700 GBP/h
      gen bus 1          80.0 MW  @    10 GBP/MWh
      gen bus 2          20.0 MW  @    95 GBP/MWh
[exit 0]
```

### The CPF search, step by step

`cpf case9` returns 2.373046875. Replaying the loop with Newton-Raphson converging at every multiplier up to a limit anywhere from 2.373046875 to just below 2.375 gives these 20 solves (the move of `cpf` to the study options and the OLTC loop left the printed lambda the same bits on every bundled case):

```
solve  lambda         step          converged
  1    1.0000000000   0.2500000000  yes
  2    1.2500000000   0.2500000000  yes
  3    1.5000000000   0.2500000000  yes
  4    1.7500000000   0.2500000000  yes
  5    2.0000000000   0.2500000000  yes
  6    2.2500000000   0.2500000000  yes
  7    2.5000000000   0.2500000000  no
  8    2.3750000000   0.1250000000  no
  9    2.3125000000   0.0625000000  yes
 10    2.3750000000   0.0625000000  no
 11    2.3437500000   0.0312500000  yes
 12    2.3750000000   0.0312500000  no
 13    2.3593750000   0.0156250000  yes
 14    2.3750000000   0.0156250000  no
 15    2.3671875000   0.0078125000  yes
 16    2.3750000000   0.0078125000  no
 17    2.3710937500   0.0039062500  yes
 18    2.3750000000   0.0039062500  no
 19    2.3730468750   0.0019531250  yes
 20    2.3750000000   0.0019531250  no
```

After solve 20 the step halves to 0.0009765625, which is below `minStep` (0.001), and the loop ends with `lastGood = 2.373046875`. The repeated trial at 2.375 on every other row is the
retry of the point that failed one refinement earlier. The width of the bracket is 2.375 - 2.373046875 = 0.001953125, which is `2 x 0.0009765625`, inside `[0.001, 0.002)`. The replay reproduces
the run's behaviour at every cap: `--max-steps 5` returns 2.0 (solve 5), `--max-steps 18` returns 2.37109375 (solve 17), and 19 or more returns 2.373046875. With `--step 0.5
--min-step 0.01` the same loop ends after 13 solves at 2.359375:

```
$ gridsim cpf case9
CPF - IEEE 9-bus (WSCC) (9 buses)  [load scaled uniformly, slack absorbs the balance]
  max loadability lambda = 2.373046875  (base demand 315.0 MW -> 747.5 MW at the last converged point; nose bracketed to 0.001)
  margin 137.30% above base load
[exit 0]
```

```
$ gridsim cpf case9 --step 0.5 --min-step 0.01
CPF - IEEE 9-bus (WSCC) (9 buses)  [load scaled uniformly, slack absorbs the balance]
  max loadability lambda = 2.359375  (base demand 315.0 MW -> 743.2 MW at the last converged point; nose bracketed to 0.01)
  margin 135.94% above base load
[exit 0]
```

```
$ gridsim cpf case9 --max-steps 5
CPF - IEEE 9-bus (WSCC) (9 buses)  [load scaled uniformly, slack absorbs the balance]
  max loadability lambda = 2  (base demand 315.0 MW -> 630.0 MW at the last converged point; NOT bracketed: --max-steps 5 ran out with every solve converged, so lambda is only a lower bound)
  margin 100.00% above base load
cpf: --max-steps 5 ran out before the nose was bracketed to --min-step 0.001; the lambda above is a lower bound, not the loadability limit. Raise --max-steps (or --min-step). Exit 3.
[exit 3]
```

All five solves converge, so the bracket clause reads `NOT bracketed: --max-steps 5 ran out with every solve converged, so
lambda is only a lower bound`, the `--max-steps` message goes to standard error and the run exits 3.

### The tap objective

`taps uk` lowers the objective from 2.789433E-002 to 1.610133E-002:

```
$ gridsim taps uk
TAP SCHEDULE - GB reduced (10-zone) (10 OLTCs)  [minimise sum of squared deviation from 1.000 pu]
  objective 2.789433E-002 -> 1.610133E-002  (improved)
      T1 GSP N Scotland          1.0000 ->   1.0375
      T2 GSP S Scotland          1.0000 ->   1.0375
      T3 GSP N England           1.0000 ->   1.0375
      T4 GSP Yorkshire           1.0000 ->   1.0375
      T5 GSP N Wales & Mersey    1.0000 ->   1.0375
      T6 GSP Midlands            1.0000 ->   1.0250
      T7 GSP East Anglia         1.0000 ->   1.0375
      T8 GSP London & SE         1.0000 ->   1.0250
      T9 GSP South               1.0000 ->   1.0375
      T10 GSP SW & S Wales       1.0000 ->   1.0375
[exit 0]
```

The starting objective is the sum over the 20 bus voltages of a power flow with the taps at their case values and no OLTC loop, which `gridsim pf uk --top 20` prints. The ten 400 kV
buses are generator buses held at 1.040 pu, and the ten GSP buses sit between 1.0274 and 1.0392 pu. The sum of (V - 1)^2 over those 20 values is 2.789433E-002, the `before`
value. The 400 kV buses alone contribute 10 x 0.04^2 = 0.016, and no tap moves them. The `after` value, 1.610133E-002, is 0.000101 above that floor, so the schedule has brought the
ten GSP buses to an rms deviation of 0.0032 pu from 1.000 pu. A higher ratio on the 400 kV side lowers the GSP voltage. Eight taps sit at 1.0375 = 1.0 + 3 x 0.0125 and two at 1.0250
= 1.0 + 2 x 0.0125, all on the grid `0.9 + k x 0.0125`.

## Source map

| File | Type | Responsibility |
|---|---|---|
| `src/GridSim.Core/Analysis/DcOptimalPowerFlow.cs` | `DcOptimalPowerFlow`, `DcOpfOptions` | Merit order, security LP build, island dispatch, fuel cost table, price assembly, result records. |
| `src/GridSim.Core/Analysis/DcOpfCosted.cs` | `DcOpfCosted` | The cost-curve LP on a connected network and its nodal and line prices. |
| `src/GridSim.Core/Analysis/DcOpfQuadratic.cs` | `DcOpfQuadratic` | The primal active-set solver for quadratic cost terms. |
| `src/GridSim.Core/Analysis/GeneratorCostCurve.cs` | `GeneratorCostCurve` | The optimisable curve form and the refusals. |
| `src/GridSim.Core/Analysis/DcPowerFlow.cs` | `DcPowerFlow`, `DcPowerFlowResult`, `DcIslandBalance` | The DC power flow over `DistributionFactors`. |
| `src/GridSim.Core/Model/FuelTypes.cs` | `FuelTypes`, `FuelKind`, `FuelKeyword` | The ordered keyword list and the matching rule behind `FuelCostPerMwh`. |
| `src/GridSim.Core/IO/Matpower/MatpowerParser.cs` | `MatpowerParser` | Reads `mpc.gencost` into `Generator.Cost`. |
| `src/GridSim.Core/IO/Json/GeneratorCostFile.cs` | `GeneratorCostFile` | The `gridsim-costs/1` file and its overlay. |
| `src/GridSim.Core/Analysis/DcScopf.cs` | `DcScopf`, `ScopfOptions`, `ScopfResult` | The preventive security-constrained DC-OPF on the sparse simplex, with constraint generation. |
| `src/GridSim.Core/Analysis/DcOpfSecuritySimplex.cs` | `DcOpfSecuritySimplex` | The structured security LP solver, bit-identical to the dense reference. |
| `src/GridSim.Core/Analysis/DcOpfHybridColumns.cs` | `DcOpfHybridColumns` | Column storage (single or dense) for the basis inverse and the Gauss-Jordan matrix. |
| `src/GridSim.Core/Numerics/BoundedSimplex.cs` | `BoundedSimplex`, `LpStatus` | The dense two-phase bounded-variable simplex and its constants. |
| `src/GridSim.Core/Analysis/DistributionFactors.cs` | `DistributionFactors`, `DcIsland` | DC model, islands, PTDF, LODF, DC flows. |
| `src/GridSim.Core/Analysis/AcOptimalPowerFlow.cs` | `AcOptimalPowerFlow`, `AcOpfResult` | The loss loop around the DC-OPF. |
| `src/GridSim.Core/Analysis/ContinuationPowerFlow.cs` | `ContinuationPowerFlow`, `PvCurve` | Load scaling, the multiplier bisection and the PV trace. |
| `src/GridSim.Core/Analysis/TapOptimizer.cs` | `TapOptimizer`, `TapSchedule`, `TapSetting` | Tap objective, penalties, snap-to-grid, result. |
| `src/GridSim.Core/Numerics/NelderMead.cs` | `NelderMead` | Derivative-free simplex search used by `TapOptimizer`. |
| `src/GridSim.Core/Analysis/LossAllocation.cs` | `LossAllocation`, `BranchLoss`, `ZoneLoss` | Per-branch and per-zone loss decomposition. |
| `src/GridSim.Core/IO/Json/JsonGridLoader.cs` | `JsonGridLoader` | `Pmin`, `Pmax`, `RateAMva` and circuit handling of a JSON case. |
| `src/GridSim.Core/PowerFlow/SolverSelector.cs` | `SolverSelector` | `CreateNewton`, used by `acopf`, `cpf` and `taps`. |
| `src/GridSim.Cli/Program.cs` | `RunDcpf`, `RunDcOpf`, `RunAcOpf`, `RunCpf`, `RunTaps`, `RunDistributionFactors`, `RunLossAllocation`, `ApplyCostSource`, `WritePvCurveCsv`, `LoadAnalysisCase`, `LoadCaseArgument`, `SingleCasePositional`, `PositionalArg` | The verbs, their case loading, output and exit codes. |
| `src/GridSim.Cli/VerbFlags.cs` | `VerbFlags` | The per-verb accepted flags, read from chapter 19. |
| `src/GridSim.Cli/CliResultWriter.cs` | `CliOutput`, `CliResult` | `--json`, `--csv` and `--out`. |
| `src/GridSim.Cli/CliDefaults.cs` | `CliDefaults` | The display floors. |
| `tests/GridSim.Tests/DcOptimalPowerFlowTests.cs`, `AnalysisWs6Tests.cs`, `ConvergenceContractTests.cs` | | DC-OPF dispatch, `Pmin`, LP status. |
| `tests/GridSim.Tests/DcPowerFlowAndPvCurveTests.cs`, `SimplexPivotFloorTests.cs` | | The DC power flow, the PV trace, the pivot floor. |
| `tests/GridSim.Tests/FuelTypesTaxonomyTests.cs`, `GeneratorCostsAndPricesTests.cs`, `AcOpfPricesTests.cs` | | The cost per fuel kind, cost curves and prices. |
| `tests/GridSim.Tests/N173AcopfEarlyStopTests.cs`, `S4SolverDataFollowUpsTests.cs`, `S4SolverConvergenceTests.cs` | | The `acopf` and `dcopf` failure causes and text, the per-island loss loop, the shunt residual. |
| `tests/GridSim.Tests/CliProcessSmokeTests.cs`, `CliS4CliAndLoaderProcessTests.cs`, `VerbFlagsTests.cs`, `CliResultFilesProcessTests.cs` | | The built binary's headers and exit codes, flag refusal, result files. |
| `tests/GridSim.Tests/DcOpfSecuritySimplexParityTests.cs`, `BoundedSimplexTests.cs` | | Structured versus dense simplex; LP behaviour. |
| `tests/GridSim.Tests/DistributionFactorsTests.cs`, `DcIslandTests.cs` | | PTDF, LODF, islands, island dispatch. |
| `tests/GridSim.Tests/DcScopfTests.cs` | | The preventive SCOPF against hand calculation and an independent formulation. |
| `tests/GridSim.Tests/AcOptimalPowerFlowTests.cs`, `ContinuationPowerFlowTests.cs`, `ConvergenceContractStudyTests.cs` | | Loss loop and load search. |
| `tests/GridSim.Tests/TapOptimizerTests.cs`, `LossAllocationTests.cs`, `Ws13CoreConstantsAntiDriftTests.cs` | | Tap schedule, loss decomposition, constants. |

## Limits

- `acopf` solves the DC problem and adds a loss loop; it does not solve the full AC equations. Voltage, reactive power and AC branch limits are not constraints, and the AC state is
  used only to measure the losses.
- Without `--costs` the cost of a unit is linear and comes only from its fuel-type string. With `--costs` a curve is a polynomial up to quadratic with a non-negative quadratic term or a convex
  piecewise-linear curve; a cubic term, a concave curve, a non-convex piecewise curve and a unit without a curve are refused. Start-up and shut-down costs are carried and not optimised. The
  prices are lossless DC prices with no marginal-loss component, and at a degenerate optimum they are those of the basis found.
- The DC-OPF leaves out of its LP a rated line that no dispatch can relieve and reports it, so a feasible result (`dcopf` exit 0, `acopf` `Converged`) can leave such a line over its rating; only the listing and the result file's `unrelievable_lines` say so.
- `dcpf` is linear and lossless: the reference bus of each island absorbs the imbalance, and a rating is reported, not enforced.
- `cpf` is a bisection on a uniform load multiplier with no predictor, no corrector and no parameterisation through the nose; each solve restarts from the stored voltages, so a failure can
  precede the true nose.
- `cpf` scales loads only and takes the increase on the slack; it checks no generator active-power limit, no branch rating and no slack limit.
- `taps` minimises the deviation of every bus from one target; each OLTC's own target and deadband are checked after the search, not optimised.
- The preventive SCOPF (`DcScopf`) takes linear costs only (an explicit cost per unit or the fuel-type cost), secures
  single-branch outages only (no generator outage, no corrective action) and needs one connected network. A post-outage limit left out of the final LP holds to within 1e-6 MW.
- `ptdf`, `lodf`, `dcpf` and the DC-OPF use series reactance only; resistance, charging, tap ratios and phase shifts are ignored.
- The loss decomposition is by branch and zone only; it does not allocate losses to generators or loads.

## See also

- [ch. 6, OPF, CPF, taps, distribution factors and losses](<../manual/0006 OPF, CPF, taps, distribution factors and losses.md>) - using the verbs
- [ch. 3, Power flow](<0003 Power flow.md>) - the Newton engines, options, `LineFlow` and the OLTC loop
- [ch. 23, Contingency and security analysis](<0023 Contingency and security analysis (N-k).md>) - the redispatch that reuses the DC-OPF, and the AC screens that `lodf` approximates
- [ch. 2, Data model and per-unit conventions](<0002 Data model and per-unit conventions.md>) - branch, generator and OLTC records
- [ch. 4, Sparse and GPU numerics](<0004 Sparse and GPU numerics.md>) - the sparse LU behind B'
- [ch. 19, CLI reference](<../manual/0019 CLI reference.md#dcpf>) - the `dcpf` flags and exit codes
- [ch. 19, CLI reference](<../manual/0019 CLI reference.md#dcopf>) - the `dcopf` flags and exit codes
- [ch. 19, CLI reference](<../manual/0019 CLI reference.md#acopf>) - the `acopf` flags
- [ch. 19, CLI reference](<../manual/0019 CLI reference.md#cpf>) - the `cpf` flags
- [ch. 19, CLI reference](<../manual/0019 CLI reference.md#taps>) - the `taps` flags
- [ch. 19, CLI reference](<../manual/0019 CLI reference.md#ptdf>) - the `ptdf` flags
- [ch. 19, CLI reference](<../manual/0019 CLI reference.md#lodf>) - the `lodf` flags
- [ch. 19, CLI reference](<../manual/0019 CLI reference.md#losses>) - the `losses` verb
