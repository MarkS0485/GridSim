# 6. OPF, CPF, taps, distribution factors and losses

A power flow ([ch. 5](<0005 Running power flows.md>)) answers one question: given these set-points and these loads,
what are the voltages? Nine verbs build on it and answer the questions an operator asks next. `dcpf` solves the linear
DC power flow of the case's own dispatch: bus angles, branch flows and loading. `dcopf` finds the cheapest dispatch that
meets demand within the line ratings. `acopf` repeats that dispatch until it also pays for the network losses. `acopf-full` solves the full AC optimal power
flow: the cheapest dispatch of active and reactive power with the voltages, the units' reactive limits and the branch MVA
ratings as constraints. `cpf` finds
how far the load can grow before the power flow stops solving. `taps` chooses the transformer tap settings that flatten the
voltage profile. `ptdf` and `lodf` give the linear sensitivities of branch flows to an injection and to a line outage, from
one factorisation. `losses` splits the losses of the solved base case by branch and by zone. All nine are headless, print a
short report, exit with a code a script can test, and can write the full result to a file (see
[Result files](#result-files)).

## Before you start

- Run every command from the repository root. `gridsim` means `dotnet run --project src/GridSim.Cli -c Release --` when
  GridSim is not installed ([ch. 1](<0001 Getting started.md>)).
- Each verb takes one case: a built-in name (for example `case9`, `case14`, `case30`, `case118`, `case300`, `uk`,
  `gb-full`, `gb-synthetic`; the full list is printed by the unknown-name error shown under [Pitfalls](#pitfalls)), a
  MATPOWER `.m` path, a case folder that holds a `system.json`, or a JSON v2 case manifest `.json`. All nine verbs accept
  every form. `pypsa-gb-zonal` is a transport model with no power flow and is refused. [Ch. 2](<0002 Loading networks.md>)
  describes the formats.
- No verb in this chapter accepts `--dir`: it exits 2 and names the flag. Pass the folder as the case argument.
- A flag that belongs to another verb is refused the same way (exit 2). A verb accepts the flags that its own section of the
  [CLI reference](<0019 CLI reference.md>) names.
- The worked examples use a three-bus case that ships with the documentation captures,
  `docs/_captures/opf/tri3/`. It is small enough to check every number by hand. Buses 1 and 2 carry one generator each,
  bus 3 carries a 100 MW load, and the three lines have the same reactance. Generator 1 is a nuclear unit (10 GBP/MWh,
  200 MW) on the slack bus, generator 2 is an `ocgt` unit (95 GBP/MWh, 200 MW), and line `L13` is rated 60 MVA against
  200 MVA for the others. The files are the ordinary JSON case files of
  [ch. 2](<0002 Loading networks.md>).
- Timings and other run-dependent stamps in the captured output differ by machine and run. The numbers in these
  examples (dispatch, factors, losses) do not.

## Worked example

### dcpf

`dcpf` solves the DC power flow of a case as it is dispatched. It chooses nothing: each bus injects its scheduled
generation minus its load, the island's reference bus picks up the imbalance, and the verb reports the branch flows and
each branch's loading against its rating.

```bash
gridsim dcpf docs/_captures/opf/tri3
```

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

The case files give the generators no output, so the case is balanced when it loads: the `ocgt` unit takes 10 MW (5 % of its
200 MW capacity) and the slack unit 92 MW, which is the 100 MW demand plus a 2 % allowance, less the 10 MW. Scheduled
generation is 102 MW. The DC model is lossless, so the reference bus (bus 1) injects exactly what balances the other buses:
the other buses net 10 - 100 = -90 MW (10 MW at bus 2, -100 MW at bus 3), so bus 1 injects 90 MW, not the 92 MW scheduled. The `ptdf` factors of this case on `L13`
are -1/3 for an injection at bus 2 and -2/3 for one at bus 3 (derived in
[ch. 29](<../technical/0029 OPF, CPF, taps, distribution factors and loss allocation.md>)), so the flow on `L13` is
10 x (-1/3) + (-100) x (-2/3) = 63.33 MW. That is 105.6 % of its 60 MVA rating, which is the one overload the summary line
counts. `L23` carries 36.67 MW and `L12` 26.67 MW. The `dcopf` run below dispatches 80 MW and 20 MW instead and holds
`L13` at 60 MW.

### dcopf

`dcopf` solves the DC optimal power flow: the cheapest generation that meets demand with every rated line inside its
limit.

```bash
gridsim dcopf docs/_captures/opf/tri3
```

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

Unit 1 is cheap (10 GBP/MWh) and sits on the slack bus. Dispatching it alone would carry 66.7 MW over `L13`, 6.7 MW above
its rating. The solver holds `L13` at exactly 60 MW and takes the other 20 MW from the dearer unit 2. The cost is
80 x 10 + 20 x 95 = 2,700 GBP/h. Without the line limit the same case is a plain merit order:

```bash
gridsim dcopf docs/_captures/opf/tri3 --no-limits
```

```
$ gridsim dcopf docs/_captures/opf/tri3 --no-limits
DC-OPF - Three-bus worked example (3 buses)  [cheapest secure dispatch]
  demand 100 MW  generation 100 MW  total cost 1,000 GBP/h  (unconstrained merit order)
  dispatch (by output):
      gen bus 1         100.0 MW  @    10 GBP/MWh
[exit 0]
```

The line limit costs 1,700 GBP/h here. The derivation of the 20 MW is in the worked example of
[ch. 29](<../technical/0029 OPF, CPF, taps, distribution factors and loss allocation.md>).

### acopf

`acopf` takes the same dispatch problem and makes it pay for the real-power losses of the network.

```bash
gridsim acopf case9
```

```
$ gridsim acopf case9
ACOPF (DC dispatch + AC loss loop) - IEEE 9-bus (WSCC) (9 buses)  [not a full AC-OPF: no voltage, reactive-power or AC branch-flow limits]
  converged in 5 iteration(s)  losses 11.615 MW  cost 16,331 GBP/h
      gen bus 3         270.0 MW  @    50 GBP/MWh
      gen bus 2          46.6 MW  @    50 GBP/MWh
      gen bus 1          10.0 MW  @    50 GBP/MWh
[exit 0]
```

The loop converged in 5 passes. The losses of 11.615 MW are those of the AC power flow at the final dispatch. The
dispatch totals 270.0 + 46.6 + 10.0 = 326.6 MW, which is the 315 MW demand plus the 11.6 MW of losses, and the cost is
326.615 x 50 = 16,331 GBP/h. `acopf` is the DC problem in a loop. It does not optimise voltages or reactive power (see
[Limits](#limits)).

### acopf-full

`acopf-full` solves the AC optimal power flow itself: it chooses every bus voltage and every unit's active and reactive
output at once, to minimise the units' own cost curves (the case's `mpc.gencost`) within the voltage limits, the units'
P and Q limits and the branch MVA ratings.

```bash
gridsim acopf-full case9
```

```
$ gridsim acopf-full case9
AC-OPF (full AC, primal-dual interior point) - IEEE 9-bus (WSCC) (9 buses, 3 units)
  converged in 11 iteration(s)  objective 5296.69 cost units/h  generation 318.307 MW  load 315.000 MW  losses 3.307 MW
  dispatch (largest 3 of 3 units):
      unit 1    bus 2          134.32 MW       0.05 MVAr
      unit 2    bus 3           94.19 MW     -22.62 MVAr
      unit 0    bus 1           89.80 MW      12.94 MVAr
  nodal prices (cost units/MWh; reactive per MVArh): highest 24.9985 at bus 9, lowest 24.0345 at bus 2
      bus 9      LMP    24.9985  reactive    0.1116  V 1.0717 pu    -4.616 deg
      bus 5      LMP    24.9985  reactive    0.0266  V 1.0844 pu    -3.982 deg
      bus 4      LMP    24.7559  reactive    0.0044  V 1.0942 pu    -2.463 deg
      bus 1      LMP    24.7557  reactive    0.0000  V 1.1000 pu     0.000 deg
      bus 7      LMP    24.2539  reactive    0.0355  V 1.0895 pu    -1.197 deg
      bus 6      LMP    24.0759  reactive    0.0000  V 1.1000 pu     0.602 deg
      bus 3      LMP    24.0759  reactive   -0.0000  V 1.0866 pu     3.249 deg
      bus 8      LMP    24.0345  reactive    0.0000  V 1.1000 pu     0.905 deg
      bus 2      LMP    24.0345  reactive   -0.0000  V 1.0974 pu     4.893 deg
  binding constraints: 3
      Vmax bus 8                            shadow price 77.4572
      Vmax bus 6                            shadow price 75.3287
      Vmax bus 1                            shadow price 8.3844
[exit 0]
```

The objective, 5296.69 per hour, and the dispatch 89.80, 134.32 and 94.19 MW are the figures MATPOWER's `runopf` prints for
the same case. The losses are 3.307 MW, against 11.615 MW at the `acopf` dispatch above: with voltages free, the method
raises them to their 1.1 pu limit at buses 1, 6 and 8 (the three binding constraints), which cuts the losses. A unit
inside its limits is priced at its own marginal cost: unit 1 at bus 2 has the curve 0.085 P^2 + 1.2 P + 600, and
2 x 0.085 x 134.32 + 1.2 = 24.03 per MWh, the nodal price at bus 2.

### cpf

`cpf` raises every load together and reports the largest multiplier at which the AC power flow still converges.

```bash
gridsim cpf case9
```

```
$ gridsim cpf case9
CPF - IEEE 9-bus (WSCC) (9 buses)  [load scaled uniformly, slack absorbs the balance]
  max loadability lambda = 2.373046875  (base demand 315.0 MW -> 747.5 MW at the last converged point; nose bracketed to 0.001)
  margin 137.30% above base load
[exit 0]
```

The case loads can be multiplied by 2.373046875 before the power flow stops solving. At that point the 315.0 MW of base
demand has become 747.5 MW, a margin of 137.30 % above the base load. The slack bus supplies every extra megawatt.

### taps

`taps` looks for one coordinated set of tap ratios across every on-load tap changer (OLTC). The built-in IEEE cases have
none, so use `uk`, the reduced GB model, which has ten.

```bash
gridsim taps uk
```

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

The objective is the sum over every bus of the squared voltage deviation from the target (1.000 pu by default). It falls
from 2.789433E-002 to 1.610133E-002. Eight transformers move up by three steps of 0.0125, to 1.0375, and two (T6 and T8)
move up two steps, to 1.0250. A higher tap ratio lowers the voltage on the transformer's low-voltage side. At flat taps the
ten GSP buses sit between 1.0274 and 1.0392 pu (`gridsim pf uk --top 20`). The ten 400 kV buses are generator buses held at
1.040 pu, 0.04 pu from the target, so they contribute 10 x 0.04^2 = 0.016 to the objective whatever the taps do. The
schedule's 1.610133E-002 is 0.000101 above that floor.

### ptdf

`ptdf` gives the power transfer distribution factors of one injection bus: the share of an extra megawatt injected there
and withdrawn at the slack that flows on each branch.

```bash
gridsim ptdf docs/_captures/opf/tri3 --bus 2
```

```
$ gridsim ptdf docs/_captures/opf/tri3 --bus 2
PTDF - Three-bus worked example (3 buses): +1 MW injected at bus 2 (withdrawn at slack 1) flows on:
    branch 2    1->2           PTDF -0.6667  (-66.7%)
    branch 0    1->3           PTDF -0.3333  (-33.3%)
    branch 1    2->3           PTDF  0.3333  ( 33.3%)
[exit 0]
```

One extra megawatt at bus 2 leaves two thirds of itself on the short path to the slack (line `L12`, branch 2) and one third
on the long path through bus 3 (`L23` then `L13`). The signs follow each branch's `from->to` direction: the 2/3 flows from
bus 2 towards bus 1, so branch `1->2` reads -0.6667. On the nine-bus case, a larger network, the same verb gives:

```bash
gridsim ptdf case9 --bus 5
```

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

Bus 5 reaches the slack (bus 1) through bus 4. The direct line `4->5` carries 0.8649 of the megawatt, and the other
0.1351 goes round the ring 5-6-7-8-9-4. Branch 0 (`1->4`) carries all of it, so its factor is -1.0000.

### lodf

`lodf` gives the line outage distribution factors: when one branch is lost, how its flow lands on the others.

```bash
gridsim lodf docs/_captures/opf/tri3 --branch 0
```

```
$ gridsim lodf docs/_captures/opf/tri3 --branch 0
LODF - Three-bus worked example (3 buses): outage of branch 0 (1->3) redistributes onto:
    branch 1    2->3           LODF  1.0000  (100.0% of the lost flow)
    branch 2    1->2           LODF  1.0000  (100.0% of the lost flow)
[exit 0]
```

Branch 0 is `L13`. After its outage all of its flow takes the only remaining path, 1->2 then 2->3, so branches 1 and 2 each
take 1.0000 of it (100.0 % of the lost flow). In the dispatch above `L13` carries 60 MW, `L23` 40 MW and `L12` 20 MW, so
after the outage `L23` carries 40 + 1.0 x 60 = 100 MW and `L12` carries 20 + 1.0 x 60 = 80 MW.

### losses

`losses` solves the base case and splits its losses by branch and by zone.

```bash
gridsim losses case9
```

```
$ gridsim losses case9
Loss allocation - IEEE 9-bus (WSCC) (9 buses, 9 branches)
  Total losses: 4.64 MW  (reactive: -92.16 MVAr)
  Reconciliation residual: 2.13E-014 MW

  Zone losses:
  | Zone | P Loss MW | Q Loss MVAr | Branches |
  |------|----------:|------------:|---------:|
  | Other | 4.64 | -92.16 | 9 |

  Top 20 branch losses:
  | Branch | From | To | P Loss MW | Q Loss MVAr | Loading % | Type |
  |--------|-----:|---:|----------:|------------:|----------:|------|
  |  | 8 | 9 | 2.300 | -19.694 | 34.8 | line |
  |  | 5 | 6 | 1.354 | -31.531 | 42.3 | line |
  |  | 7 | 8 | 0.475 | -11.502 | 30.7 | line |
  |  | 9 | 4 | 0.258 | -15.794 | 22.5 | line |
  |  | 4 | 5 | 0.166 | -15.513 | 13.9 | line |
  |  | 6 | 7 | 0.088 | -21.176 | 22.8 | line |
  |  | 1 | 4 | 0.000 | 3.123 | 30.6 | line |
  |  | 3 | 6 | 0.000 | 4.096 | 28.8 | line |
  |  | 8 | 2 | 0.000 | 15.832 | 65.3 | line |
[exit 0]
```

The first line names the case, its bus count and its branch count. The nine branches lose 4.64 MW and produce 92.16 MVAr
(the reactive loss is negative because the lines' charging exceeds their series consumption). The residual line is the
check that the branch losses and the bus shunt draw add up to the solver's total.

## Reading the output

### dcpf

```
DC power flow - <case> (<buses> buses, <branches> branches)  [linear: series reactance only; ...; lossless]
  island ref bus R   N buses  demand D MW  scheduled generation G MW  reference injects X MW
  K of M rated branch(es) above 100% of RateA
  largest flows:
      branch I   name   from -> to   F MW   L% of R MVA
```

- **Island lines.** One line per DC island. `ref bus` is the island's reference (angle 0), chosen as the slack when the
  island holds it. `demand` is the sum of the island's loads. `scheduled generation` is the sum of the in-service `Pg`,
  the reference bus's units included. `reference injects` is what the reference bus must inject for the island to balance
  in a lossless model: minus the sum of every other bus's net injection. An island with load and no generator also prints
  `NO GENERATION: load cannot be supplied`.
- **Overload count.** `M` is the number of branches with a rating (`RateA` above 0) and `K` how many carry more than 100 %
  of it. Overloads are reported, not treated as an error.
- **Flow rows.** The `--top` branches (12 by default) with the largest absolute flow, ties by branch index. The name is
  the branch name, or `from-to` for an unnamed branch (a MATPOWER branch). The flow is in MW with two decimals, positive
  from the `from` bus to the `to` bus, and the loading is `|flow| / RateA x 100`, or `unrated` when `RateA` is 0. `--top 0`
  prints no rows.
- **Not on the console.** The bus angles (degrees, reference at 0) and the net injections are in the result file
  ([Result files](#result-files)).
- **Exit codes.** 0 when the model solves. 1 when the case does not load or the DC model cannot be formed. 2 when no case
  is given or a flag value is wrong (`--top` below 0). 3 when an island carries load but has no generator: the report is
  printed first, then `dcpf: N island(s) carry load but have no generator, so the DC solution has no physical meaning there.`
  on standard error.

### dcopf

```
DC-OPF - <case> (<buses> buses)  [cheapest secure dispatch]
  demand D MW  generation G MW  total cost C GBP/h  (<mode>)
  dispatch (by output):
      gen bus B   P MW  @ c GBP/MWh
  binding line limits:
      branch I  name   F MW / R MVA
```

- **Summary line.** `demand` is the sum of the bus loads (`Pd`). `generation` is the sum of the dispatch. `total cost` is
  the sum of dispatch times cost per megawatt-hour, in GBP/h. The three are printed as whole numbers with thousands
  separators.
- **Mode.** `(unconstrained merit order)` means no rated line ends up at its limit. It does not mean the line limits
  were ignored: with limits on, at least one rated branch and a merit order that meets demand, the limit LP runs, and the
  dispatch printed is its answer. `(network-constrained redispatch applied)` means at least one line is held at its limit. With `--no-limits`
  the label is always `(unconstrained merit order)` and the dispatch is a pure merit order.
- **Dispatch rows.** One row per generator unit, largest output first, limited to `--top` rows (12 by default). A unit
  below 0.001 MW is not listed. Two units on one bus are two rows (bus 3 appears twice in `gridsim dcopf uk`). `gen bus` is
  the bus number, and `@ c GBP/MWh` is the unit's cost per megawatt-hour (see [Pitfalls](#pitfalls) for where it comes from).
- **Binding line limits.** Printed only when a rated branch carries a flow within 0.001 MW of its rating. The row gives
  the branch index (0-based, in the order of the case's branch list), its name (blank for a MATPOWER branch), the DC flow
  in MW and the rating in MVA. In `uk` the rating of `L2-3 B6 boundary` is 4,400 MVA because the case stores 2,200 MVA per
  circuit and the line has two circuits. `--top` limits this list as well as the dispatch rows.
- **Islands.** A case that is electrically split (`gb-full`) prints one line per DC island before the summary: the
  island's reference bus, its bus count, its demand, its generation, a verdict (`feasible`, `INFEASIBLE`,
  `NO GENERATION: load cannot be supplied` for a dead island with load, or `no generation (no load)` for a dead island
  without) and, when the limit LP ran, its status (`Optimal`, `Infeasible`, `Unbounded` or `IterationLimit`).
- **Lines no dispatch can relieve.** A rated line whose flow stays above its rating whatever the units do is left out of
  the limit LP and named before the summary: `N rated line(s) cannot be brought within rating by any dispatch; left out of
  the security LP and still overloaded:`, then one row per line with its rating and the range of flows any dispatch could
  give it. The summary is then preceded by `secure for every rated line except the N listed above (no dispatch can
  relieve them)`. A case with no such line prints neither.
- **Exit codes.** 0 for a feasible dispatch. 3 when the dispatch is infeasible: the case loaded and the LP ran, and the
  answer is that no secure dispatch exists. 1 when the case does not load, when the solve rejects the data (for example a
  generator whose `Pmin` is above its `Pmax`) or when `--costs` cannot be applied. 2 when no case is given, the case name
  is unknown, a flag is wrong or a flag belongs to another verb. An infeasible run prints `INFEASIBLE` and no dispatch,
  and a requested result file is still written with the island list ([Pitfalls](#pitfalls) lists the messages).

### acopf

```
ACOPF (DC dispatch + AC loss loop) - <case> (<buses> buses)  [not a full AC-OPF: no voltage, reactive-power or AC branch-flow limits]
  converged in N iteration(s)  losses L MW  cost C GBP/h
      gen bus B   P MW  @ c GBP/MWh
```

- `N` is the number of passes of the loss loop. Each pass solves the DC problem on loads scaled up by the losses found
  so far, then runs an AC power flow at that dispatch. The loop stops when the losses change by less than `--loss-tol`
  MW between passes (0.001 MW by default), or after `--max-iter` passes (20 by default).
- `losses` is the AC real-power loss at the final dispatch, in MW. `cost` is the sum of dispatch times cost of the final
  pass, so it includes the energy that covers the losses.
- The dispatch rows are those of `dcopf`, from the final pass.
- When the final DC pass left out lines that no dispatch can relieve, a line under the header names them (`the final DC
  pass left out N rated line(s) no dispatch can bring within rating (still overloaded; 'gridsim dcopf' lists them):`),
  with up to `--top` rows.
- Exit 0 when converged, 3 when the run did not converge (one of four messages, listed under [Pitfalls](#pitfalls)),
  1 when the case does not load, the solve rejects the data or `--costs` cannot be applied, 2 for an unknown case name or
  a bad or foreign flag.
- With `--costs` the cost is in `cost units/h` and each row shows the `marginal` cost, as for `dcopf`
  ([Variations](#variations)).

### acopf-full

```
AC-OPF (full AC, primal-dual interior point) - <case> (<buses> buses, <units> units)
  converged in N iteration(s)  objective F cost units/h  generation G MW  load L MW  losses X MW
  dispatch (largest K of U units):
      unit I    bus B    P MW    Q MVAr
  nodal prices (cost units/MWh; reactive per MVArh): highest H at bus B, lowest W at bus B
      bus B      LMP P  reactive Q  V v pu  a deg
  binding constraints: C
      KIND element    shadow price S
```

- `N` is the number of interior point iterations (MIPS's conditions: feasibility, gradient, complementarity and cost change
  all below 1e-6). `F` is the sum of the units' cost curves at the optimum, in the case's own cost units.
- The units are listed largest first, up to `--top`; P and Q are the optimal outputs.
- `LMP` is the nodal price of active power at the bus (the change in the optimal cost per extra MW of load there);
  `reactive` is the price of reactive power. `V` and the angle are the optimal voltage.
- A binding constraint is one whose multiplier is not zero at the optimum: `Vmin`/`Vmax` (per pu), `Pmin`/`Pmax`/`Qmin`/
  `Qmax` (per MW or MVAr), `Sf`/`St` (per MVA at the branch's from or to end), each per hour.
- Exit 0 when converged; 3 when not (`not converged` or `numerical failure`, with the iteration log on stderr and no
  dispatch, because the point is not an optimum); 1 when the case does not load or is refused (a unit without a cost
  curve, a piecewise-linear cost, a dispatchable load, a voltage-dependent load, crossed limits, a zero-impedance branch, no
  slack bus, more than one island); 2 for an unknown case name or a bad or foreign flag.

### cpf

```
CPF - <case> (<buses> buses)  [load scaled uniformly, slack absorbs the balance]
  max loadability lambda = X  (base demand D MW -> E MW at the last converged point; nose bracketed to M)
  margin P% above base load
```

- `lambda` is the largest load multiplier for which the AC power flow converged, printed to full precision. Every bus's
  real and reactive demand is multiplied by it. Generator set-points do not change, so the slack bus supplies the whole
  increase.
- `base demand -> ...` is the total real demand before and after scaling.
- `nose bracketed to M` repeats the `--min-step` value (0.001 by default), and is printed only when the search ended on
  its step; when `--max-steps` ran out first the clause reads `NOT bracketed: ...` instead (see
  [Pitfalls](#pitfalls)). The search ends when its step has fallen
  below `--min-step`. The last multiplier that failed to converge then lies above `lambda` by a step between
  `--min-step` and twice `--min-step`: for case9 it is 2.375 against a `lambda` of 2.373046875
  ([how `cpf` searches, ch. 29](<../technical/0029 OPF, CPF, taps, distribution factors and loss allocation.md>).
- `margin` is (`lambda` - 1) x 100, in percent.
- Exit 0 when the nose was bracketed at a lambda of 1 or more. Exit 3 when the base case (lambda = 1) did not converge
  (with no converged point at all: `cpf: the base case (lambda = 1) did not converge; no loadability margin.`; with only a
  lighter load converging, the negative margin is printed and then the message in [Pitfalls](#pitfalls)), and when
  `--max-steps` ran out before the nose was bracketed. Exit 1 when the case does not load, 2 for a bad flag value
  (`--step` not above 0, `--min-step` outside (0, `--step`], `--max-steps` below 1, `--pv-out` not ending in `.csv`).
- Every point is solved as `stress` solves its load points: 100 Newton iterations, generator reactive limits on, and the
  on-load tap changer loop when the case has OLTCs.
- `--pv-out <file.csv>` also writes the PV curves ([Variations](#variations)).

### taps

```
TAP SCHEDULE - <case> (<n> OLTCs)  [minimise sum of squared deviation from T pu]
  objective BEFORE -> AFTER  (improved | current taps already best)
      <transformer>   current -> recommended
```

- The objective is the sum over every bus of (voltage - target) squared, with the voltage in pu of the bus's nominal
  voltage, from a power flow with the taps held fixed. The 400 kV buses count as well as the transformer outlets.
- `current` is the transformer's present tap ratio. `recommended` is the optimiser's tap, on the OLTC's step grid
  (0.0125 pu per step in `uk`) and within its minimum and maximum (0.9 to 1.1 in `uk`).
- `(improved)` appears when the objective falls by more than 1e-9. `(current taps already best)` appears when it does not,
  and then `recommended` equals `current` on every row.
- Exit 0 for either outcome, 1 when the case has no OLTC, 3 when the base case does not converge (`taps: the base case did
  not converge; no schedule.`).

### ptdf

```
PTDF - <case> (<buses> buses): +1 MW injected at bus B (withdrawn at slack S) flows on:
    branch I   from->to   PTDF f  (p%)
```

- A factor is the change in a branch's DC flow, in MW, for one more megawatt injected at the bus and withdrawn at the
  slack. The sign follows the branch's `from->to` direction.
- Branches are listed by absolute factor, largest first, with `--top` rows (12 by default). A factor with an absolute
  value of 1e-4 or below is not listed.
- The branch index is the 0-based position of the branch in the case. For a JSON case it is the lines of
  `transmission.json` followed by the transformers of `transformer.json`; for a `.m` file it is the position among the
  branches the loader keeps (in service, with both end buses present), counted from 0.
- A case split into several DC islands withdraws the injection at the reference of the bus's own island, and the header
  says so: `withdrawn at its island's reference R; N DC islands`.
- Exit 0 on success, 1 when the factors cannot be built, 2 when `--bus` is missing, not an integer or negative.

### lodf

```
LODF - <case> (<buses> buses): outage of branch I (from->to) redistributes onto:
    branch J   from->to   LODF f  (p% of the lost flow)
```

- A factor is the flow that lands on branch J, per megawatt that the outaged branch carried before the outage, in the
  direction of J's own `from->to`. After the outage the flow on J is its old flow plus the factor times the old flow on
  the outaged branch. The outaged branch itself is not listed (its own factor is -1).
- The ordering, the 1e-4 floor, `--top` and the branch numbering are those of `ptdf`.
- A branch whose loss splits the network prints `outage of branch I (from->to) ISLANDS the system (bridge line); LODF
  undefined.` and exits 0. The line has no factors because the flow has nowhere to go.
- Exit 2 when `--branch` is missing or outside `0..N-1` (the usage line prints the valid range), 1 when the factors
  cannot be built.

### losses

```
Loss allocation - <case> (<buses> buses, <branches> branches)
  Total losses: P MW  (reactive: Q MVAr)
  Bus shunt conductance: S MW  (not a branch loss; in the injection total)
  Reconciliation residual: E MW
```

- The first line names the case, its bus count and its branch count.
- `Total losses` is the real loss of the solved base case in MW and, in brackets, the reactive loss in MVAr. It is the
  sum of every branch's end-flow losses.
- `Bus shunt conductance` is printed only when the case has a bus with a conductive shunt (`Gs` not 0), for example
  `case300` (1.21 MW). It is the real power those shunts draw, which flows in no branch and is part of the solver's
  injection total.
- `Reconciliation residual` is the solver's reported total loss minus the branch losses minus the bus shunt draw, in MW.
  It is rounding noise (2.13E-014 MW on case9, -4.16E-012 MW on case300).
- `Zone losses` appears when the case defines boundaries. Each zone is a named boundary; its row sums the branches
  the boundary lists, and every boundary branch name must name exactly one branch of the case. A branch listed in two
  boundaries counts in the first. Every other branch falls in `Other`. Rows are sorted by real loss, with `Other` last.
- `Top 20 branch losses` lists the 20 branches with the largest real loss: name (blank for a MATPOWER branch), from bus,
  to bus, real loss in MW, reactive loss in MVAr, loading in percent of rating (0.0 for an unrated branch), and `tx` for a
  branch flagged as a transformer, otherwise `line`.
- Exit 0 on success. 1 when a `.m` file is missing or a case folder does not load. 2 for an unknown case name, a second
  case argument or a flag the verb does not take. 3 when the base case does not converge.

## Variations

### dcpf

**A larger case, fewer rows.** `--top` limits the flow rows. On `uk` one island holds all 20 buses and no branch is above its
rating:

```bash
gridsim dcpf uk --top 3
```

```
$ gridsim dcpf uk --top 3
DC power flow - GB reduced (10-zone) (20 buses, 24 branches)  [linear: series reactance only; R, charging, shunts, taps and phase shifts ignored; |V| = 1 pu; lossless]
  island ref bus 6         20 buses  demand   39,500.0 MW  scheduled generation   40,290.0 MW  reference injects    4,835.0 MW
  0 of 24 rated branch(es) above 100% of RateA
  largest flows:
      branch 21    T8 GSP London & SE      8 -> 108       8500.00 MW     77.3% of 11,000 MVA
      branch 19    T6 GSP Midlands       6 -> 106       6500.00 MW     81.2% of 8,000 MVA
      branch 16    T3 GSP N England      3 -> 103       5500.00 MW     78.6% of 7,000 MVA
[exit 0]
```

The reference bus (bus 6) injects 4,835.0 MW, the amount that balances the other 19 buses in a lossless model. Results go to files with `--csv`, `--json` or `--out` ([Result files](#result-files)).

### dcopf

**A line limit that bites on a real case.** The reduced GB model holds the B6 boundary line at its rating:

```bash
gridsim dcopf uk --top 3
```

```
$ gridsim dcopf uk --top 3
DC-OPF - GB reduced (10-zone) (20 buses)  [cheapest secure dispatch]
  demand 39,500 MW  generation 39,500 MW  total cost 705,500 GBP/h  (network-constrained redispatch applied)
  dispatch (by output):
      gen bus 7       8,000.0 MW  @     1 GBP/MWh
      gen bus 2       4,000.0 MW  @     1 GBP/MWh
      gen bus 9       4,000.0 MW  @     1 GBP/MWh
  binding line limits:
      branch 1    L2-3 B6 boundary  4,400.0 MW / 4,400 MVA
[exit 0]
```

**Equal costs.** A MATPOWER case has no fuel types, so by default every unit costs 50 GBP/MWh and every dispatch of 315 MW
costs the same. The solver returns one of many optimal dispatches, chosen by its pivoting rules:

```bash
gridsim dcopf case9
gridsim dcopf case9 --no-limits
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

`--no-limits` fills units in index order (250 MW from bus 1 first), which is the merit order of equal costs. With limits
on, the same cost comes from a different vertex of the LP (270 MW from bus 3).

**A MATPOWER file.** The file's own `Pmin` and `Pmax` are honoured. Its `gencost` table is read, and without `--costs case`
it is not used:

```bash
gridsim dcopf data/matpower/lib/case9.m
```

```
$ gridsim dcopf data/matpower/lib/case9.m
DC-OPF - case9 (9 buses)  [cheapest secure dispatch]
  demand 315 MW  generation 315 MW  total cost 15,750 GBP/h  (unconstrained merit order)
  dispatch (by output):
      gen bus 3         270.0 MW  @    50 GBP/MWh
      gen bus 2          35.0 MW  @    50 GBP/MWh
      gen bus 1          10.0 MW  @    50 GBP/MWh
note: case9.m: nominal frequency not given (MATPOWER files carry none); assumed 50 Hz - pass the real value if this is, for example, a 60 Hz system (gridsim: --frequency-hz 60).
note: case9.m: read mpc.gencost (3 row(s)); the costs are carried on the generators and used by dcopf / acopf when asked (--costs case); without it the OPF uses the fuel-type merit order.
[exit 0]
```

The two `note:` lines come from the MATPOWER reader on standard error (the capture lists standard error after standard output; a
terminal shows them when the case loads). That file prices its three units at
150 + 5 P + 0.11 P^2, 600 + 1.2 P + 0.085 P^2 and 335 + 1 P + 0.1225 P^2 (its startup costs 1500, 2000 and 3000 are
separate columns). The default run ignores those and prices every unit at 50 GBP/MWh. The `Pmin` of 10 MW is honoured, which
is why bus 1 appears.

**Cost curves and prices.** `--costs case` dispatches on the case's own cost curves, and `--prices` adds the nodal prices
(locational marginal prices, LMPs) and the line shadow prices:

```bash
gridsim dcopf data/matpower/lib/case9.m --costs case --prices --top 3
```

```
$ gridsim dcopf data/matpower/lib/case9.m --costs case --prices --top 3
DC-OPF - case9 (9 buses)  [cheapest secure dispatch on generator cost curves: case gencost]
  demand 315 MW  generation 315 MW  total cost 5,216.03 cost units/h  (no line limit binds)
  dispatch (by output):
      gen bus 2        134.38 MW  marginal   24.0442 cost units/MWh
      gen bus 3         94.06 MW  marginal   24.0442 cost units/MWh
      gen bus 1         86.56 MW  marginal   24.0442 cost units/MWh
  nodal prices (LMP, cost units/MWh, lossless DC): 9 bus(es), highest 24.0442 at bus 1, lowest 24.0442 at bus 9
      bus 1      LMP    24.0442  (energy 24.0442, congestion 0.0000)
      bus 2      LMP    24.0442  (energy 24.0442, congestion 0.0000)
      bus 3      LMP    24.0442  (energy 24.0442, congestion 0.0000)
note: case9.m: nominal frequency not given (MATPOWER files carry none); assumed 50 Hz - pass the real value if this is, for example, a 60 Hz system (gridsim: --frequency-hz 60).
note: case9.m: read mpc.gencost (3 row(s)); the costs are carried on the generators and used by dcopf / acopf when asked (--costs case); without it the OPF uses the fuel-type merit order.
[exit 0]
```

The figures are in `cost units`, because a MATPOWER cost curve is in the case's own currency. Every unit runs at the same
marginal cost, 24.0442 per MWh: 5 + 0.22 x 86.56 = 1.2 + 0.17 x 134.38 = 1 + 0.245 x 94.06 = 24.0442, and the three outputs
add up to the 315 MW demand. No line limit binds, so every bus has the same price and the congestion component is 0.
`--costs case` exits 1 when a unit has no curve, which is the case for every built-in case:

```bash
gridsim dcopf case9 --costs case
```

```
$ gridsim dcopf case9 --costs case
DC-OPF - IEEE 9-bus (WSCC) (9 buses)  [cheapest secure dispatch on generator cost curves: case gencost]
  demand 315 MW  generation 315 MW  total cost 5,216.03 cost units/h  (no line limit binds)
  dispatch (by output):
      gen bus 2        134.38 MW  marginal   24.0442 cost units/MWh
      gen bus 3         94.06 MW  marginal   24.0442 cost units/MWh
      gen bus 1         86.56 MW  marginal   24.0442 cost units/MWh
[exit 0]
```

**A cost file.** `--costs <file.json>` lays the curves of a `gridsim-costs/1` file over the case (a unit the file does not
name keeps the case's own curve, if any). A curve is a polynomial up to quadratic, `coefficients` highest order first, or a
convex piecewise-linear `points` list of `[MW, cost per hour]`. The file `docs/_captures/opf/tri3-costs.json` gives the
nuclear unit 0.05 P^2 + 10 P and the `ocgt` unit 95 P:

```
{
  "format": "gridsim-costs/1",
  "generators": [
    { "index": 1, "model": "polynomial", "coefficients": [0.05, 10, 0] },
    { "index": 2, "model": "polynomial", "coefficients": [0, 95, 0] }
  ]
}
```

```bash
gridsim dcopf docs/_captures/opf/tri3 --costs docs/_captures/opf/tri3-costs.json --prices
```

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

The unit on bus 1 runs at 80 MW, where its marginal cost is 10 + 0.1 x 80 = 18, and the cost is
10 x 80 + 0.05 x 80^2 + 95 x 20 = 3,020. Line `L13` is at its limit, so the prices separate: the price at bus 2 is
18 + 231/3 = 95 and at bus 3 is 18 + 2 x 231/3 = 172, where 231 is the shadow price of `L13` (the cost saved per MW of extra
rating) and 1/3 and 2/3 are the `ptdf` factors of `L13` for buses 2 and 3.

**Prices on a fuel-priced case.** `--prices` without `--costs` prices the fuel-type dispatch. On `uk` the B6 boundary line
is held at its rating and its shadow price is 55 GBP/MWh per MW of extra rating. The prices run from 5 GBP/MWh (bus 102) to
60 GBP/MWh (buses 3, 103 and 4, the three listed):

```bash
gridsim dcopf uk --prices --top 3
```

```
$ gridsim dcopf uk --prices --top 3
DC-OPF - GB reduced (10-zone) (20 buses)  [cheapest secure dispatch]
  demand 39,500 MW  generation 39,500 MW  total cost 705,500 GBP/h  (network-constrained redispatch applied)
  dispatch (by output):
      gen bus 7       8,000.0 MW  @     1 GBP/MWh
      gen bus 2       4,000.0 MW  @     1 GBP/MWh
      gen bus 9       4,000.0 MW  @     1 GBP/MWh
  binding line limits:
      branch 1    L2-3 B6 boundary  4,400.0 MW / 4,400 MVA
  nodal prices (LMP, GBP/MWh, lossless DC): 20 bus(es), highest 60.0000 at bus 3, lowest 5.0000 at bus 102
      bus 3      LMP    60.0000  (energy 60.0000, congestion 0.0000)
      bus 103    LMP    60.0000  (energy 60.0000, congestion 0.0000)
      bus 4      LMP    60.0000  (energy 60.0000, congestion 0.0000)
  line shadow prices (GBP/MWh per MW of extra rating):
      branch 1    L2-3 B6 boundary  4,400.0 MW / 4,400 MVA  shadow price 55.0000
[exit 0]
```

**Several islands.** `gb-full` splits into two DC islands, and each is dispatched on its own with its own balance and
reference bus. Some of its rated circuits carry more than their rating whatever the units do, so `gridsim dcopf
gb-full --top 5` names them as lines no dispatch can relieve, leaves them out of the limit LP, and dispatches the rest
securely (`secure for every rated line except the N listed above`).

With `--no-limits` it dispatches both islands by merit order:

```bash
gridsim dcopf gb-full --no-limits --top 3
```

```
$ gridsim dcopf gb-full --no-limits --top 3
DC-OPF - GB full (3539-bus, 5918-branch) (3539 buses)  [cheapest secure dispatch]
  2 DC islands, each dispatched on its own (its own balance, its own reference bus):
      island ref bus 194     3536 buses  demand   53,279.4 MW  generation   53,279.4 MW  feasible
      island ref bus 6774       3 buses  demand        5.6 MW  generation        5.6 MW  feasible
  demand 53,285 MW  generation 53,285 MW  total cost 53,560 GBP/h  (unconstrained merit order)
  dispatch (by output):
      gen bus 171     4,100.0 MW  @     1 GBP/MWh
      gen bus 175     1,320.0 MW  @     1 GBP/MWh
      gen bus 75      1,200.0 MW  @     1 GBP/MWh
[exit 0]
```

### acopf

**The cost of the losses on a real case.** `uk` dispatches 39,500 MW for 705,500 GBP/h in `dcopf`. The loss loop settles at
86.735 MW of losses and a cost of 710,366 GBP/h, which is 4,866 GBP/h more:

```bash
gridsim acopf uk --top 4
```

```
$ gridsim acopf uk --top 4
ACOPF (DC dispatch + AC loss loop) - GB reduced (10-zone) (20 buses)  [not a full AC-OPF: no voltage, reactive-power or AC branch-flow limits]
  converged in 4 iteration(s)  losses 86.735 MW  cost 710,366 GBP/h
      gen bus 7       8,000.0 MW  @     1 GBP/MWh
      gen bus 2       4,000.0 MW  @     1 GBP/MWh
      gen bus 9       4,000.0 MW  @     1 GBP/MWh
      gen bus 10      3,200.0 MW  @    10 GBP/MWh
[exit 0]
```

**A tighter or looser loop.** `--loss-tol` sets the settling tolerance and `--max-iter` the pass limit. A tolerance of
1e-9 MW takes case9 from 5 passes to 10 and leaves the losses at 11.615 MW:

```bash
gridsim acopf case9 --loss-tol 1e-9
```

```
$ gridsim acopf case9 --loss-tol 1e-9
ACOPF (DC dispatch + AC loss loop) - IEEE 9-bus (WSCC) (9 buses)  [not a full AC-OPF: no voltage, reactive-power or AC branch-flow limits]
  converged in 10 iteration(s)  losses 11.615 MW  cost 16,331 GBP/h
      gen bus 3         270.0 MW  @    50 GBP/MWh
      gen bus 2          46.6 MW  @    50 GBP/MWh
      gen bus 1          10.0 MW  @    50 GBP/MWh
[exit 0]
```

A pass limit that is too low exits 3 and says which limit stopped the loop:

```bash
gridsim acopf case9 --max-iter 2
```

```
$ gridsim acopf case9 --max-iter 2
ACOPF (DC dispatch + AC loss loop) - IEEE 9-bus (WSCC) (9 buses)  [not a full AC-OPF: no voltage, reactive-power or AC branch-flow limits]
acopf: the losses did not settle within 2 pass(es) (--max-iter 2, --loss-tol 0.001 MW).
[exit 3]
```

**Cost curves.** `--costs case|<file.json>` and `--prices` work as for `dcopf`. Every pass of the loop dispatches on the
cost curves, and the prices are those of the final pass, at the demand inflated by the losses:

```bash
gridsim acopf data/matpower/lib/case9.m --costs case
```

```
$ gridsim acopf data/matpower/lib/case9.m --costs case
ACOPF (DC dispatch + AC loss loop) - case9 (9 buses)  [not a full AC-OPF: no voltage, reactive-power or AC branch-flow limits]
  converged in 4 iteration(s)  losses 3.906 MW  cost 5,310.47 cost units/h (generator cost curves)
      gen bus 2        135.96 MW  marginal   24.3134 cost units/MWh
      gen bus 3         95.16 MW  marginal   24.3134 cost units/MWh
      gen bus 1         87.79 MW  marginal   24.3134 cost units/MWh
note: case9.m: nominal frequency not given (MATPOWER files carry none); assumed 50 Hz - pass the real value if this is, for example, a 60 Hz system (gridsim: --frequency-hz 60).
note: case9.m: read mpc.gencost (3 row(s)); the costs are carried on the generators and used by dcopf / acopf when asked (--costs case); without it the OPF uses the fuel-type merit order.
[exit 0]
```

### cpf

**A coarser search.** `--step` is the first increment of the multiplier and `--min-step` the width at which the search
stops. A larger pair gives fewer power flows and a looser answer:

```bash
gridsim cpf case9 --step 0.5 --min-step 0.01
```

```
$ gridsim cpf case9 --step 0.5 --min-step 0.01
CPF - IEEE 9-bus (WSCC) (9 buses)  [load scaled uniformly, slack absorbs the balance]
  max loadability lambda = 2.359375  (base demand 315.0 MW -> 743.2 MW at the last converged point; nose bracketed to 0.01)
  margin 135.94% above base load
[exit 0]
```

The default run reaches 2.373046875 in 20 power flows; this one reaches 2.359375 in 13.

**Other cases.** `gridsim cpf case14` gives lambda 1.759765625 (margin 75.98 %), and `gridsim cpf uk` gives 6.052734375.
The `uk` figure is not a credible load limit: it scales 39,500 MW of demand to 239,083 MW, and the power flow places no limit
on the active power of the slack bus that supplies the increase.

**PV curves.** `--pv-out <file.csv>` writes the voltage of every bus at every converged multiplier, from the same search
(the file name must end in `.csv`):

```bash
gridsim cpf case9 --pv-out docs/_captures/opf/results/cpf-case9-pv.csv
```

```
$ gridsim cpf case9 --pv-out docs/_captures/opf/results/cpf-case9-pv.csv
CPF - IEEE 9-bus (WSCC) (9 buses)  [load scaled uniformly, slack absorbs the balance]
  max loadability lambda = 2.373046875  (base demand 315.0 MW -> 747.5 MW at the last converged point; nose bracketed to 0.001)
  margin 137.30% above base load
[cpf] PV curves: 12 converged point(s) x 9 bus(es) -> docs\_captures\opf\results\cpf-case9-pv.csv
[exit 0]
```

The file has one row per converged point and bus, in the order solved, so 12 points of 9 buses are 108 rows after the
header. The multipliers are 1, 1.25, 1.5, 1.75, 2, 2.25, 2.3125, 2.34375, 2.359375, 2.3671875, 2.37109375 and 2.373046875.
The last point, the nose, ends the file:

```
case,lambda,bus_id,vm_pu
IEEE 9-bus (WSCC),2.373046875,1,1.04
IEEE 9-bus (WSCC),2.373046875,2,1.025
IEEE 9-bus (WSCC),2.373046875,3,1.025
IEEE 9-bus (WSCC),2.373046875,4,0.815959715265056
IEEE 9-bus (WSCC),2.373046875,5,0.7436204438661285
IEEE 9-bus (WSCC),2.373046875,6,0.9347902211592166
IEEE 9-bus (WSCC),2.373046875,7,0.8673162934674681
IEEE 9-bus (WSCC),2.373046875,8,0.9116579115173994
IEEE 9-bus (WSCC),2.373046875,9,0.6793645141757912
```

At the last point the lowest voltage is 0.6794 pu, at bus 9. `stress --pv-out` writes the same layout, with a `load_factor` column in place of
`lambda`, for the load points of `stress` ([ch. 11](<0011 Contingency analysis.md>)).

### taps

**A different target.** `--target` sets the voltage the schedule aims at:

```bash
gridsim taps uk --target 1.02
```

```
$ gridsim taps uk --target 1.02
TAP SCHEDULE - GB reduced (10-zone) (10 OLTCs)  [minimise sum of squared deviation from 1.020 pu]
  objective 6.164149E-003 -> 4.133679E-003  (improved)
      T1 GSP N Scotland          1.0000 ->   1.0250
      T2 GSP S Scotland          1.0000 ->   1.0125
      T3 GSP N England           1.0000 ->   1.0125
      T4 GSP Yorkshire           1.0000 ->   1.0125
      T5 GSP N Wales & Mersey    1.0000 ->   1.0125
      T6 GSP Midlands            1.0000 ->   1.0125
      T7 GSP East Anglia         1.0000 ->   1.0125
      T8 GSP London & SE         1.0000 ->   1.0125
      T9 GSP South               1.0000 ->   1.0125
      T10 GSP SW & S Wales       1.0000 ->   1.0125
  10 OLTC(s) would leave this schedule: the bus each controls is outside its own target +/- deadband, so its local regulator would retap:
      T1 GSP N Scotland        bus 101     1.0138 pu  (own target 1.0000 +/- 0.0100)
      T2 GSP S Scotland        bus 102     1.0240 pu  (own target 1.0000 +/- 0.0100)
      T3 GSP N England         bus 103     1.0191 pu  (own target 1.0000 +/- 0.0100)
      T4 GSP Yorkshire         bus 104     1.0207 pu  (own target 1.0000 +/- 0.0100)
      T5 GSP N Wales & Mersey  bus 105     1.0221 pu  (own target 1.0000 +/- 0.0100)
      T6 GSP Midlands          bus 106     1.0176 pu  (own target 1.0000 +/- 0.0100)
      T7 GSP East Anglia       bus 107     1.0229 pu  (own target 1.0000 +/- 0.0100)
      T8 GSP London & SE       bus 108     1.0144 pu  (own target 1.0000 +/- 0.0100)
      T9 GSP South             bus 109     1.0221 pu  (own target 1.0000 +/- 0.0100)
      T10 GSP SW & S Wales     bus 110     1.0248 pu  (own target 1.0000 +/- 0.0100)
[exit 0]
```

A case folder works as the case argument, and `gridsim taps data/uk` gives the same schedule as `gridsim taps uk`.

### ptdf

**A larger network.** On `uk` the slack is bus 6, so an injection at bus 8 is withdrawn at bus 6:

```bash
gridsim ptdf uk --bus 8 --top 5
```

```
$ gridsim ptdf uk --bus 8 --top 5
PTDF - GB reduced (10-zone) (20 buses): +1 MW injected at bus 8 (withdrawn at slack 6) flows on:
    branch 8    6->8           PTDF -0.4539  (-45.4%)
    branch 9    7->8           PTDF -0.2867  (-28.7%)
    branch 11   8->9           PTDF  0.2594  ( 25.9%)
    branch 10   6->9           PTDF -0.2038  (-20.4%)
    branch 7    6->7           PTDF -0.1985  (-19.9%)
[exit 0]
```

### lodf

**A line whose loss splits the network.** Branch 0 of `uk` is the only link between North Scotland (bus 1) and the
rest of the network:

```bash
gridsim lodf uk --branch 0
```

```
$ gridsim lodf uk --branch 0
LODF - GB reduced (10-zone): outage of branch 0 (1->2) ISLANDS the system (bridge line); LODF undefined.
[exit 0]
```

On case9, branches 0, 3 and 6 are the three generator connections, and each is a bridge. Branch 1 (`4->5`) sits on the
ring 4-5-6-7-8-9, so its outage sends all of its flow the other way round:

```bash
gridsim lodf case9 --branch 1
```

```
$ gridsim lodf case9 --branch 1
LODF - IEEE 9-bus (WSCC) (9 buses): outage of branch 1 (4->5) redistributes onto:
    branch 4    6->7           LODF -1.0000  (-100.0% of the lost flow)
    branch 7    8->9           LODF -1.0000  (-100.0% of the lost flow)
    branch 8    9->4           LODF -1.0000  (-100.0% of the lost flow)
    branch 5    7->8           LODF -1.0000  (-100.0% of the lost flow)
    branch 2    5->6           LODF -1.0000  (-100.0% of the lost flow)
[exit 0]
```

Each ring branch has factor -1.0000: the sign is negative because each of those branches points against the detour.

### losses

**Zones.** `uk` defines four boundaries, so the zone table appears:

```bash
gridsim losses uk
```

```
$ gridsim losses uk
Loss allocation - GB reduced (10-zone) (20 buses, 24 branches)
  Total losses: 53.68 MW  (reactive: -5974.15 MVAr)
  Reconciliation residual: 3.81E-012 MW

  Zone losses:
  | Zone | P Loss MW | Q Loss MVAr | Branches |
  |------|----------:|------------:|---------:|
  | B6 Scotland-England (SCOTEX) | 7.53 | -789.99 | 1 |
  | SW Peninsula & S Wales (export) | 3.94 | -1150.33 | 2 |
  | B4 Upper North (Scotland) | 2.24 | -626.56 | 1 |
  | B7/B8 North-Midlands | 0.68 | -1453.40 | 3 |
  | Other | 39.29 | -1953.86 | 17 |

  Top 20 branch losses:
  | Branch | From | To | P Loss MW | Q Loss MVAr | Loading % | Type |
  |--------|-----:|---:|----------:|------------:|----------:|------|
  | T8 GSP London & SE | 8 | 108 | 12.367 | 363.723 | 80.6 | tx |
  | L2-3 B6 boundary | 2 | 3 | 7.529 | -789.991 | 54.7 | line |
  | T6 GSP Midlands | 6 | 106 | 7.188 | 211.403 | 84.5 | tx |
  | T3 GSP N England | 3 | 103 | 5.131 | 150.912 | 81.6 | tx |
  | T4 GSP Yorkshire | 4 | 104 | 3.425 | 100.731 | 77.8 | tx |
  | L9-10 South-SW | 9 | 10 | 2.719 | -513.611 | 49.0 | line |
  | L1-2 N-S Scotland | 1 | 2 | 2.240 | -626.558 | 40.5 | line |
  | T5 GSP N Wales & Mersey | 5 | 105 | 2.066 | 60.763 | 72.5 | tx |
  | T9 GSP South | 9 | 109 | 2.066 | 60.763 | 72.5 | tx |
  | L7-8 E Anglia-London | 7 | 8 | 2.007 | -358.491 | 52.7 | line |
  | T7 GSP East Anglia | 7 | 107 | 1.516 | 44.579 | 77.6 | tx |
  | L5-10 N Wales-SW | 5 | 10 | 1.224 | -636.721 | 30.6 | line |
  | L8-9 London-South | 8 | 9 | 0.998 | -314.496 | 37.3 | line |
  | T2 GSP S Scotland | 2 | 102 | 0.813 | 23.921 | 75.8 | tx |
  | T10 GSP SW & S Wales | 10 | 110 | 0.485 | 14.264 | 70.3 | tx |
  | L5-6 N Wales-Midlands | 5 | 6 | 0.457 | -482.149 | 22.4 | line |
  | L6-8 Midlands-London | 6 | 8 | 0.432 | -428.315 | 23.2 | line |
  | L6-7 Midlands-E Anglia | 6 | 7 | 0.373 | -428.911 | 20.9 | line |
  | L3-4 N England-Yorks | 3 | 4 | 0.270 | -429.936 | 18.8 | line |
  | L4-6 Yorks-Midlands | 4 | 6 | 0.193 | -430.714 | 16.2 | line |
[exit 0]
```

The total of 53.68 MW is the raw Newton solve. `gridsim uk --once` reports 55.57 MW for the same case because the report also
regulates the on-load tap changers (3 OLTC passes, [ch. 5](<0005 Running power flows.md>)); `losses` does not.

**A case folder.** `losses` takes the same case forms as the other verbs. The three-bus folder has no boundary, so every
branch is in `Other`, and `L13` is shown at 105.7 % of its rating:

```bash
gridsim losses docs/_captures/opf/tri3
```

```
$ gridsim losses docs/_captures/opf/tri3
Loss allocation - Three-bus worked example (3 buses, 3 branches)
  Total losses: 0.00 MW  (reactive: 6.08 MVAr)
  Reconciliation residual: 1.42E-014 MW

  Zone losses:
  | Zone | P Loss MW | Q Loss MVAr | Branches |
  |------|----------:|------------:|---------:|
  | Other | 0.00 | 6.08 | 3 |

  Top 20 branch losses:
  | Branch | From | To | P Loss MW | Q Loss MVAr | Loading % | Type |
  |--------|-----:|---:|----------:|------------:|----------:|------|
  | L13 | 1 | 3 | 0.000 | 4.020 | 105.7 | line |
  | L23 | 2 | 3 | 0.000 | 1.350 | 18.4 | line |
  | L12 | 1 | 2 | 0.000 | 0.712 | 13.3 | line |
[exit 0]
```

**Shunt conductance.** `gridsim losses case300` has 17 buses with a conductive shunt. It prints `Bus shunt conductance: 1.21 MW`
between the totals (408.33 MW of branch losses) and a residual of -4.16E-012 MW.

### Result files

Each verb in this chapter takes `--json <file.json>`, `--csv <file.csv>` and `--out <file>`, which picks the format from the
extension. The console report is the same with or without them. A result file holds more than the console: every
generator, branch, bus or factor, where the console lists the top rows, with numbers at full round-trip precision.

- `--csv` writes the verb's main table to the named file, every other table beside it as `<stem>-<table>.csv`, and the scalar
  summary as `<stem>-summary.csv`. `dcopf` writes `dispatch` (main), `binding_lines` and `islands`, plus `prices` and
  `shadow_prices` with `--prices`. `acopf` writes `dispatch` and the same price tables. `dcpf` writes `branches` (main),
  `buses` (angle in degrees and net injection) and `islands`. `ptdf` and `lodf` write `factors`, `taps` writes `taps`,
  `losses` writes `branches` and `zones`, and `cpf` writes the summary only.
- `--json` writes the same tables and summary as one document with the schema tag `gridsim-cli-result/1`, the arguments and
  the exit code.
- A run that stops before it has a result (a case that does not load, a solve that does not converge) writes no file and says
  so on standard error. A finished result whose verdict is a failure, such as an infeasible `dcopf`, is written with its exit
  code.

```bash
gridsim dcopf docs/_captures/opf/tri3 --csv docs/_captures/opf/results/dcopf-tri3.csv
```

```
$ gridsim dcopf docs/_captures/opf/tri3 --csv docs/_captures/opf/results/dcopf-tri3.csv
DC-OPF - Three-bus worked example (3 buses)  [cheapest secure dispatch]
  demand 100 MW  generation 100 MW  total cost 2,700 GBP/h  (network-constrained redispatch applied)
  dispatch (by output):
      gen bus 1          80.0 MW  @    10 GBP/MWh
      gen bus 2          20.0 MW  @    95 GBP/MWh
  binding line limits:
      branch 0    L13              60.0 MW / 60 MVA
[dcopf] wrote docs\_captures\opf\results\dcopf-tri3.csv, docs\_captures\opf\results\dcopf-tri3-binding_lines.csv, docs\_captures\opf\results\dcopf-tri3-islands.csv, docs\_captures\opf\results\dcopf-tri3-summary.csv
[exit 0]
```

The report is unchanged, and standard error names the four files. The main table is the full dispatch, and the summary
holds the scalars at full precision:

```
gen_bus,pg_mw,cost_gbp_per_mwh
1,80.00000000000003,10
2,19.99999999999997,95
```

```
key,value
verb,dcopf
exit_code,0
case,Three-bus worked example
buses,3
line_limits_enforced,true
feasible,true
total_load_mw,100
total_generation_mw,100
total_cost_gbp_per_h,2699.9999999999973
network_constrained,true
```

`dcpf` writes the angles that the console omits:

```
bus_id,va_deg,p_inj_mw
1,0,90
2,-1.5278874536821956,10
3,-3.6287327024952134,-100
```

The angle of bus 3 is -3.6287 degrees: (10 x 0.1 + 20 x (-1.0)) / 300 = -0.063333 rad.

## Pitfalls

- **Where the cost per megawatt-hour comes from.** Without `--costs`, `dcopf` and `acopf` price a unit from its fuel `type`
  string. The string is lower-cased and split into words, a keyword matches the start of a word, and the first keyword in a
  fixed list decides, so `hydro` matches `Hydroelectric` but `coal` does not match `Charcoal`. The prices are: wind
  (onshore or offshore), solar, `pv`, tidal, wave 1; hydro 5; nuclear 10; storage, battery, `bess`, pumped storage,
  hydrogen 30; coal, lignite 45; biomass and waste 55; interconnector, `hvdc`, import, grid, infeed 60; `ccgt`, `chp`,
  gas 65; `ocgt`, gas turbine 95; reciprocating engine, diesel, oil 120; anything else, including an empty type, 50
  GBP/MWh. Every unit of a MATPOWER case has an empty type, so every unit costs 50. The keyword order and the exact rule
  are in [ch. 29](<../technical/0029 OPF, CPF, taps, distribution factors and loss allocation.md>). `--costs` replaces
  the whole table with cost curves.
- **`dcopf` has four infeasible messages, and they name the cause.** On a case with one DC island,
  `INFEASIBLE: no dispatch within the units' output limits keeps every rated line within its rating (security LP
  Infeasible); '--no-limits' drops the line limits.` says the line limits are the cause, and `--no-limits` shows the merit
  order that meets the demand without them. `the security LP stopped without proving an optimum (status X); no secure
  dispatch is reported.` is printed when the limit LP hit its iteration limit or reported unbounded, and, only when no
  limit LP ran, `generation capability cannot meet demand (N MW of in-service capability for M MW of demand).` A case
  with several islands prints the island list and `at least one island has no dispatch that meets its demand ... see the
  island list.` All exit 3. A line that no dispatch can relieve does not make the LP infeasible: it is named and left
  out (see [Reading the output](#reading-the-output)).
- **`acopf` has four failure messages, all exit 3.** The header line is printed first, then one of these on standard error:

  1. `acopf: the AC power flow at the DC-OPF dispatch of loss pass P (passes count from 0) did not converge: ...` gives the
     solver status, the iterations, the largest mismatch and the lowest voltage where it stopped. The DC dispatch ignores
     voltages and reactive power, so it can be a point the network cannot carry. When every unit is priced the same, a second
     line says that the dispatch is one arbitrary optimum and suggests `--costs case`:

     ```bash
     gridsim acopf case118 --top 3
     ```

     ```
$ gridsim acopf case118 --top 3
ACOPF (DC dispatch + AC loss loop) - IEEE 118-bus (118 buses)  [not a full AC-OPF: no voltage, reactive-power or AC branch-flow limits]
acopf: the AC power flow at the DC-OPF dispatch of loss pass 0 (passes count from 0) did not converge: MaxIterations after 38 iteration(s), largest mismatch 6.210E-001 pu, lowest voltage 0.7966 pu where it stopped. The DC dispatch is chosen without voltages or reactive power, so it can be a point the network cannot carry within the units' reactive limits: the case's physics at that dispatch, not a solver fault.
acopf: every unit is priced the same (50 GBP/MWh: the case gives no fuel types), so that dispatch is one arbitrary optimum of a degenerate LP; '--costs case' dispatches on the case's own cost curves (a .m file's mpc.gencost).
[exit 3]
```

  2. `acopf: the DC-OPF dispatch of loss pass 0 (passes count from 0) is infeasible (no dispatch meets the demand within
     the units' limits and the rated lines' limits), and no later pass can be feasible: the loss loop only raises demand,
     which cannot cure this infeasibility, so the loop stopped there; 'gridsim dcopf' on this case shows why.` is printed
     when pass 0 is infeasible, because no later pass can cure it.

  3. `acopf: the losses settled after N pass(es), but the DC-OPF dispatch of the final pass is infeasible ...` when the
     loop settles and the last dispatch is not feasible.
  4. `acopf: the losses did not settle within N pass(es) (--max-iter M, --loss-tol T MW).` when the pass limit is the cause
     (the `--max-iter 2` run under [Variations](#variations)).

  `gridsim dcopf <case>` on the same case shows whether the dispatch itself is feasible.
- **`cpf` on a case that does not solve as given exits 3.** If the base case does not converge, the search backs off
  below lambda = 1 and prints the first multiplier that does, with a negative margin, then says on standard error `cpf:
  the base case (lambda = 1) did not converge; only a lighter load did (largest converged lambda ..., margin ...%), so the
  case as given has no loadability margin. Exit 3.` `gridsim cpf docs/_captures/opf/overloaded-case9.m` is case9 with its
  loads multiplied by 2.5:

  ```
$ gridsim cpf docs/_captures/opf/overloaded-case9.m
CPF - overloaded-case9 (9 buses)  [load scaled uniformly, slack absorbs the balance]
  max loadability lambda = 0.94921875  (base demand 787.5 MW -> 747.5 MW at the last converged point; nose bracketed to 0.001)
  margin -5.08% above base load
note: overloaded-case9.m: nominal frequency not given (MATPOWER files carry none); assumed 50 Hz - pass the real value if this is, for example, a 60 Hz system (gridsim: --frequency-hz 60).
note: overloaded-case9.m: read mpc.gencost (3 row(s)); the costs are carried on the generators and used by dcopf / acopf when asked (--costs case); without it the OPF uses the fuel-type merit order.
cpf: the base case (lambda = 1) did not converge; only a lighter load did (largest converged lambda 0.94921875, margin -5.08%), so the case as given has no loadability margin. Exit 3.
[exit 3]
```

  The two `note:` lines come from the MATPOWER reader on standard error: the file gives no nominal frequency, so 50 Hz is
  assumed, and its `gencost` table was read. The case does not solve as given (`gridsim docs/_captures/opf/overloaded-case9.m --once`
  prints `DID NOT CONVERGE`). A negative margin means the base case is beyond its limit.
- **`cpf --max-steps` can stop before the nose.** The line then reads `NOT bracketed: --max-steps N ran out ...` in
  place of `nose bracketed to ...`, standard error says the lambda is a lower bound, and the run exits 3. With
  `--max-steps 5` the search stops at lambda 2.0, which is 0.373 below the real limit:

  ```
$ gridsim cpf case9 --max-steps 5
CPF - IEEE 9-bus (WSCC) (9 buses)  [load scaled uniformly, slack absorbs the balance]
  max loadability lambda = 2  (base demand 315.0 MW -> 630.0 MW at the last converged point; NOT bracketed: --max-steps 5 ran out with every solve converged, so lambda is only a lower bound)
  margin 100.00% above base load
cpf: --max-steps 5 ran out before the nose was bracketed to --min-step 0.001; the lambda above is a lower bound, not the loadability limit. Raise --max-steps (or --min-step). Exit 3.
[exit 3]
```


  The verb does not print how many power flows it ran (the result file of `--json`, `--csv` or `--out` carries it as `solves`), so keep `--max-steps` well above what the search needs. The default of 200 is ten times the 20 solves case9 takes.
- **`ptdf` prints an empty list for the slack bus.** An injection at the slack is withdrawn at the slack, so no branch
  carries anything, and the run exits 0. A bus that is not in the case exits 2 with `error: ptdf --bus 99: case 'IEEE
  9-bus (WSCC)' has no bus 99.`:

  ```
$ gridsim ptdf case9 --bus 1
PTDF - IEEE 9-bus (WSCC) (9 buses): +1 MW injected at bus 1 (withdrawn at slack 1) flows on:
[exit 0]
```

- **`ptdf` and `lodf` without `--bus` or `--branch` exit 2.** The verb loads and factors the case first, then checks the
  flag:

  ```
$ gridsim ptdf case9
usage: gridsim ptdf <case> --bus <busId>
[exit 2]
```

  ```
$ gridsim lodf case9
usage: gridsim lodf <case> --branch <0..8>
[exit 2]
```

  The two usage lines differ: `lodf` shows the valid branch range for the case.
- **`taps` needs on-load tap changers.** A case with none exits 1. The built-in IEEE cases and the bundled MATPOWER cases
  have none, because a MATPOWER transformer carries a fixed off-nominal ratio and no tap limits:

  ```
$ gridsim taps case9
taps: case 'IEEE 9-bus (WSCC)' has no on-load tap changers to schedule.
[exit 1]
```

- **`taps` chooses the objective, not the OLTC.** The target is the `--target` value for every bus. After the schedule a
  block lists every OLTC whose controlled bus would sit outside its own target and deadband, so its local regulator would
  retap away from the schedule. An OLTC whose transformer name matches no branch, or several, or whose controlled bus is
  unknown, is refused by name (exit 1), and a tap search that stops at its iteration cap exits 3. The `before` objective is a power flow with the taps fixed at the case's
  ratios, so it differs from the voltages in `gridsim uk --once`, whose OLTC loop has already moved taps.
- **`--dir` is refused.** None of these verbs takes `--dir`, so it exits 2 and names the flag instead of running on a
  different case. `losses` is covered too, even though its case argument is optional:

  ```
$ gridsim dcopf --dir data/uk
error: dcopf does not take --dir (it is not one of dcopf's flags, so it would be silently ignored). Run 'gridsim dcopf --help' for the flags dcopf takes, or see docs/manual/0019 CLI reference.md.
[exit 2]
```

  ```
$ gridsim losses --dir data/uk
error: losses does not take --dir (it is not one of losses's flags, so it would be silently ignored). Run 'gridsim losses --help' for the flags losses takes, or see docs/manual/0019 CLI reference.md.
[exit 2]
```

  Pass the folder as the case argument.
- **A flag that belongs to another verb is refused.** Each verb accepts the flags its section of the
  [CLI reference](<0019 CLI reference.md>) names. Any other flag exits 2 and names the flag and the verb:

  ```
$ gridsim taps uk --step 3
error: taps does not take --step (it is not one of taps's flags, so it would be silently ignored). Run 'gridsim taps --help' for the flags taps takes, or see docs/manual/0019 CLI reference.md.
[exit 2]
```

  ```
$ gridsim losses uk --top 3
error: losses does not take --top (it is not one of losses's flags, so it would be silently ignored). Run 'gridsim losses --help' for the flags losses takes, or see docs/manual/0019 CLI reference.md.
[exit 2]
```

  `--costs` and `--prices` belong to `dcopf` and `acopf` only:

  ```
$ gridsim ptdf case9 --bus 1 --costs case
error: --costs applies to dcopf and acopf only (got 'ptdf').
[exit 2]
```
- **An unknown case name exits 2; a missing file exits 1.** `dcopf`, `acopf`, `cpf`, `taps`, `ptdf`, `lodf` and `dcpf`
  print `error loading case '<arg>': <reason>`, and `losses` prints `error: losses: <reason>`:

  ```
$ gridsim dcopf nosuchcase
error loading case 'nosuchcase': unknown case 'nosuchcase' (built-in names: case118, case14, case145, case24, case30, case300, case57, case9, cigre, gb, gb-full, gb-network-2224, gb-reduced, gb-reduced-29, gb-synthetic, ieee9, pypsa-gb-reduced, pypsa-gb-zonal, rts, synthetic, uk; or a case folder, or a MATPOWER .m file, or a JSON v2 case manifest .json)
[exit 2]
```

  ```
$ gridsim losses nosuchcase
error: losses: unknown case 'nosuchcase' (built-in names: case118, case14, case145, case24, case30, case300, case57, case9, cigre, gb, gb-full, gb-network-2224, gb-reduced, gb-reduced-29, gb-synthetic, ieee9, pypsa-gb-reduced, pypsa-gb-zonal, rts, synthetic, uk; or a case folder, or a MATPOWER .m file, or a JSON v2 case manifest .json)
[exit 2]
```

  ```
$ gridsim losses nosuch.m
error: MATPOWER case file not found: 'nosuch.m' (full path nosuch.m).
[exit 1]
```

  A second case argument to `losses` exits 2.
- **A generator with `Pmin` above `Pmax` exits 1** in `dcopf` and `acopf`, with `[dcopf] DC-OPF: generator 0 ('' at bus 1)
  has Pmin 300 MW above Pmax 250 MW.` (`[acopf] ...` for `acopf`).
- **A case over the licence limit exits 2** in every verb that loads a user network ([ch. 4](<0004 Licensing and activation.md>)).

## Limits

- `acopf` solves the DC problem and adds a loss loop. It does not solve an AC optimal power flow: bus voltages, reactive
  power and AC branch limits are not constraints, and the AC state it computes is used only to measure the losses and is not
  reported. `acopf-full` is the AC optimal power flow.
- `acopf-full` optimises polynomial cost curves (any order) and refuses piecewise-linear ones; it keeps transformer taps and
  phase shifts at their case values, models loads as constant power, solves one island, and does not enforce branch angle
  difference limits.
- Without `--costs`, `dcopf` and `acopf` price units by a fixed fuel-type table, linear in output. With `--costs` a curve is a
  polynomial up to quadratic with a non-negative quadratic term, or a convex piecewise-linear curve; a cubic term, a
  concave curve and a unit without a curve are refused. Start-up and shut-down costs are carried, not optimised. Prices are
  lossless DC prices: energy plus congestion, no marginal-loss component.
- `dcpf` is linear: it uses series reactance only, schedules the generation the case holds and lets the reference bus of each
  island absorb the imbalance, so it is not a loss-aware solution and it does not enforce a rating.
- `cpf` is a bisection on a uniform load multiplier, not a continuation method: it has no predictor, no corrector and no
  parameterisation that passes the nose. Newton-Raphson restarts from the case's stored voltages at every multiplier, and
  non-convergence is taken to mean the nose is passed.
- `cpf` scales the loads only, in a fixed direction, and takes the whole increase on the slack bus. Generator active-power
  limits, branch ratings and a limit on the slack are not checked.
- `taps` pairs an OLTC with a branch by name; a name that matches no branch, or several, is refused.
- `ptdf` and `lodf` use the series reactance only. Resistance, charging, tap ratios and phase shifts are ignored, and every
  voltage is 1 pu.
- `losses` allocates losses to branches and zones, not to generators or loads.

## See also

- [ch. 29, OPF, CPF, taps, distribution factors and loss allocation](<../technical/0029 OPF, CPF, taps, distribution factors and loss allocation.md>) - the formulation, the LP, the search and the invariants
- [ch. 5, Running power flows](<0005 Running power flows.md>) - the power flow every AC verb here starts from
- [ch. 11, Contingency analysis](<0011 Contingency analysis.md>) - `trip`, `screen`, `nminus1`, `stress` and `chain`, which re-solve the network after a loss instead of using the linear factors of `lodf`
- [ch. 2, Loading networks](<0002 Loading networks.md>) - case names, MATPOWER files and JSON folders
- [ch. 19, CLI reference](<0019 CLI reference.md#dcpf>) - every `dcpf` flag and exit code
- [ch. 19, CLI reference](<0019 CLI reference.md#dcopf>) - every `dcopf` flag and exit code
- [ch. 19, CLI reference](<0019 CLI reference.md#acopf>) - every `acopf` flag
- [ch. 19, CLI reference](<0019 CLI reference.md#cpf>) - every `cpf` flag
- [ch. 19, CLI reference](<0019 CLI reference.md#taps>) - every `taps` flag
- [ch. 19, CLI reference](<0019 CLI reference.md#ptdf>) - the `ptdf` flags
- [ch. 19, CLI reference](<0019 CLI reference.md#lodf>) - the `lodf` flags
- [ch. 19, CLI reference](<0019 CLI reference.md#losses>) - the `losses` verb
- [ch. 30, Troubleshooting](<0030 Troubleshooting.md>) - what to do when a power flow does not converge
