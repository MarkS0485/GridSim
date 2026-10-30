# 5. Running power flows

A power flow takes a network, a set of generator set-points and a set of loads, and solves for the voltage
magnitude and angle at every bus. Everything else GridSim reports (branch flows, losses, loading, protection
verdicts) is derived from that one solved state. `gridsim <case>` is the shortest route to it: load a case, solve it,
and either print an engineering report (`--once`), animate a day of demand (the default), or write the full
mathematics of the solve to JSON (`--enableExport`). This chapter covers those three forms, how to read the report,
which engine solves the case, and the recorded time-domain form (`transient`). The analysis verbs, each with its own
chapter, start from the same solve.

## Worked example

Solve the IEEE 14-bus case once:

```bash
gridsim case14 --once
```

```
$ gridsim case14 --once
GridSim  -  IEEE 14-bus
14 buses  20 branches  5 generators  base 100 MVA  50 Hz
========================================================================
Converged (2 NR iters, 0 OLTC passes, max mismatch 1.32E-010 p.u.)
Generator output 0.27 GW  Demand 0.26 GW  Losses 13.4 MW  Inertia not in the data (no generator H)

Bus  Name                            kV    Vm(pu)  Va(deg)    P(MW)   Q(MVAr)
------------------------------------------------------------------------------
  1                                   -   1.060     0.00     232.4     -16.5
  2                                   -   1.045    -4.98      18.3      30.9
  3                                   -   1.010   -12.73     -94.2       6.1
  4                                   -   1.018   -10.31     -47.8       3.9
  5                                   -   1.020    -8.77      -7.6      -1.6
  6                                   -   1.070   -14.22     -11.2       5.2
  7                                   -   1.062   -13.36       0.0       0.0
  8                                   -   1.090   -13.36       0.0      17.6
  9                                   -   1.056   -14.94     -29.5     -16.6
 10                                   -   1.051   -15.10      -9.0      -5.8
 11                                   -   1.057   -14.79      -3.5      -1.8
 12                                   -   1.055   -15.08      -6.1      -1.6
 13                                   -   1.050   -15.16     -13.5      -5.8
 14                                   -   1.036   -16.03     -14.9      -5.0
kV '-': base voltage not in the data for 14 of 14 bus(es) (the case gives 0).

Voltage range: 1.010 - 1.090 p.u. (OUT OF BAND)
Total losses: 13.39 MW, 8.94 MVAr
Protection simulation: no relay would operate on this state (generic settings).
[exit 0]
```

The case converged in 2 Newton iterations, the system loses 13.4 MW, and the highest voltage (1.090 p.u. at bus 8)
is above the 1.06 p.u. operating band. The exit code is 0 because the power flow converged. The timings, stamps and
machine names in the captured output on this page differ by machine and run; the numbers of a solve do not.

## Reading the output

The report has nine parts, always in this order. A part is omitted only where stated.

**Header.** The case name; then bus, branch and generator counts, the system base in MVA, and the nominal
frequency. The line of `=` characters closes the header.

**Convergence line.** `Converged (2 NR iters, 0 OLTC passes, max mismatch 1.32E-010 p.u.)` reports three numbers.

- `NR iters` is the total number of Newton iterations over every generator-reactive-limit pass (see
  [How the solve works](#how-the-solve-works)). On the sweep engine it is the number of sweep iterations; the label
  does not change. A pass may use up to 30 iterations, and a solve that reaches the tolerance on its 30th step is
  converged.
- `OLTC passes` is the number of times the on-load tap-changer loop re-solved the network. It is 0 when the case
  has no tap changers, or when none needed to move. The case `uk` has tap changers and reports 3 (see the
  `gridsim uk --once` block below, under Boundary table).
- `max mismatch` is the largest remaining power mismatch at the final iteration, in per-unit on the system base.
  A Newton solve is converged when this is below `1e-8` p.u. (`PowerFlowOptions.Tolerance`). The sweep tests the
  change in voltage between sweeps instead, and prints this mismatch as a separate figure.

A solve that does not converge prints `DID NOT CONVERGE (N iters, max mismatch M p.u.)` instead, still prints the
tables below it, and exits 3 (see [Pitfalls](#pitfalls)).

When the case has tap changers and the tap loop did not finish, one more line follows the convergence line. It reads
`Taps NOT SETTLED: N tap changer(s) at the end of their range with the controlled bus still outside its deadband: <names>.`
and names up to ten transformers whose tap sits at its limit with the bus out of band, or it reads
`Taps NOT SETTLED: the tap changer loop stopped after N pass(es) with taps still moving; ...` when the loop reached its
pass cap. The line is a report: the exit code stays 0 because the power flow converged. It is absent when the taps
settled, as in every run on this page.

**Totals line.** `Generator output 0.27 GW  Demand 0.26 GW  Losses 13.4 MW  Inertia not in the data (no generator H)`.

- `Generator output` is the generators' total real output at the solved state: at every bus that holds a generator,
  the net injection plus the load drawn there, summed once per bus. It includes the slack machine's solved output, so
  it balances against `Demand` plus `Losses` (case14: 0.27 GW against 0.26 GW and 13.4 MW). On a run that did not
  converge it reads `n/a (not converged)`.
- `Demand` is the sum of the stated real loads (`Pd`) in the model. It does not move with a voltage-dependent
  (ZIP) load model; the bus table shows the effect.
- `Losses` is the total real-power loss in MW: the sum of the bus net injections, which equals the loss in the
  in-service branches plus the loss in the bus shunts.
- `Inertia` is the stored kinetic energy of the generator fleet in GVAs, summed from the generators' `h` and MVA
  base, when any generator carries `h`. A MATPOWER case has no inertia column, so its line reads
  `Inertia not in the data (no generator H)` instead of a figure. The case `uk` prints `171.7 GVAs` and the built-in
  `case9` prints `7.5 GVAs`.

**Bus table.** One row per bus, in model order.

| Column | Meaning |
|---|---|
| `Bus` | Bus number from the case. |
| `Name` | Bus name, cut to 28 characters. Blank when the case gives none (every MATPOWER case). |
| `kV` | Nominal voltage, rounded to a whole kV. A bus whose data gives no base voltage (a MATPOWER `baseKV` of 0, as in case14) prints `-`. The per-unit results are unaffected. |
| `Vm(pu)` | Solved voltage magnitude in per-unit of the bus nominal voltage. |
| `Va(deg)` | Solved voltage angle in degrees, the slack bus holds its stored angle (0.00 in case14). |
| `P(MW)` | Net real-power injection: generation minus load. Positive means the bus supplies the network; negative means it draws from it. |
| `Q(MVAr)` | Net reactive-power injection, same sign convention. A `-0.0` is a small negative value that rounds to zero. |

When any bus shows `-` in the `kV` column, the line `kV '-': base voltage not in the data for 14 of 14 bus(es) (the
case gives 0).` follows the table and counts them.

The slack bus (bus 1 in case14) carries the whole system imbalance: its `P` is whatever the other buses leave over
plus the losses.

**Boundary table.** Printed only when the case defines boundaries (the `uk` case defines four). Each row gives the
net flow across the boundary in MW, its limit in MW and the loading as a percentage, and appends `OVERLOAD` when the
loading exceeds 100 %.

**Most-loaded branches.** Printed only when at least one branch has a rating. The five branches with the highest
loading, each with the larger of its two end flows in MW and the loading in percent of rating. MATPOWER cases such as
case14 and case118 carry no ratings, so this block does not appear for them.

```
$ gridsim uk --once
...
Boundary                          Flow(MW)  Limit(MW)  Loading
----------------------------------------------------------------
B4 Upper North (Scotland)           1550      3200    48.4% 
B6 Scotland-England (SCOTEX)        2322      6600    35.2% 
B7/B8 North-Midlands                1247     12000    10.4% 
SW Peninsula & S Wales (expor...      3051      4500    67.8% 

Most-loaded branches:
  T6 GSP Midlands                 6508 MW    84.6%
  T3 GSP N England                5505 MW    81.7%
  T8 GSP London & SE              8513 MW    80.7%
  T4 GSP Yorkshire                4504 MW    77.8%
  T7 GSP East Anglia              3002 MW    77.7%

Voltage range: 0.998 - 1.040 p.u. (in band)
Total losses: 55.57 MW, -5918.65 MVAr
Protection simulation: 1 event(s) suggested  -  e.g. on Moyle, relay 32 (Reverse power) would likely operate (Moyle is absorbing 150.0 MW (reverse/motoring past the 5 MW pickup)). Run `protection-check` for the full list.
[exit 0]
```

**Voltage verdict.** `Voltage range: 1.010 - 1.090 p.u. (OUT OF BAND)` gives the lowest and highest bus voltage. The
verdict is `in band` when the lowest voltage is at least 0.94 p.u. and the highest is at most 1.06 p.u.
(`VoltageLimits.OperationalLowPu` and `OperationalHighPu`, in `src/GridSim.Core/Analysis/VoltageLimits.cs`), and
`OUT OF BAND` otherwise. The verdict is a report on the solved state. It does not change the exit code. It applies that one global band to every bus; the per-bus voltage limits a case may
carry (MATPOWER `VMIN` and `VMAX`) are not read here. The contingency verbs `nminus1`, `nminus2` and `n-1-1` judge
buses against the per-bus limits by default (see [ch. 11, Contingency analysis](<0011 Contingency analysis.md>)).
case14 reads
`OUT OF BAND` because bus 8 (1.090 p.u., the generator set-point), bus 6 (1.070 p.u., the set-point) and bus 7
(1.062 p.u.) exceed 1.06 p.u.

**Total losses.** `Total losses: 13.39 MW, 8.94 MVAr` gives real and reactive loss over all branches and bus
shunts to two decimals. Reactive loss is negative when line charging produces more reactive power than the series reactances
absorb, as in case118 (`-644.08 MVAr`) and `uk` (`-5918.65 MVAr`).

**Protection line.** The last line runs a passive relay assessment on the solved state with generic relay
settings. It never alters the solve. Three forms exist.

- `no relay would operate on this state (generic settings).` No modelled relay picks up.
- `N event(s) suggested - e.g. on <location>, relay <device> (<function>) would likely operate (<basis>).` The
  first operating relay is named and the line points to `protection-check` for the full list (see
  [ch. 19, CLI reference](<0019 CLI reference.md#protection-check>)).
- `skipped - the power flow did not converge, so there is no state to assess.` Protection is assessed only on a
  converged state.

## Variations

### Live view

```bash
gridsim case14
gridsim
```

With neither `--once` nor `--ticks`, the CLI enters the live loop and re-solves until a key is pressed. `gridsim`
with no case loads the reduced GB model (`data/uk`). The simulated clock starts at 08:00 and advances 20 minutes per
tick. Each tick scales every load, and every generator not on the slack bus, by a daily demand factor that falls to 0.60
at 04:00 and peaks at 1.00 at 16:00. The first generator on the slack bus is then reset to 1.02 times the demand less
the other generation (a 2 % loss allowance, `Dispatcher.Balance`), and the case is re-solved. Frequency reads a constant 50.00 Hz because every tick is a
steady-state solve; frequency dynamics are in `transient` and the dashboards.

The dashboard shows frequency, demand, generation, losses, the voltage range with its band verdict, the convergence
state with the iteration and OLTC-pass counts, the `Taps NOT SETTLED` line when it applies, the most-loaded boundary,
and a zone table. The zone table (`LiveZoneTable`, `src/GridSim.Core/Presentation/LiveZoneTable.cs`) works on any case:

- Its rows are the buses at 200 kV and above when the case has any, otherwise the buses at the highest voltage
  present, in bus-number order, at most 24 rows; a closing line says how many more buses were left out.
- A row's lower-voltage partner is the bus joined to it by an in-service branch at a lower, known voltage. The GB
  model's convention (partner number is the bus number plus 100) is used first when that bus really is such a
  neighbour, so `data/uk` reproduces the GB table exactly (`LiveZoneTableTests`). With no partner, or several, the lower-voltage column shows
  `-` and a line under the table counts the rows without one. A bus is never paired with an unrelated bus.
- A case with no bus voltages at all (case14) has no rows and the line `Zone table: bus voltages (kV) are not in the
  data for this case.` replaces them.

### Headless ticks

`--ticks N`, with N at least 1, runs the same loop for N ticks, prints one line per tick and exits. It never waits for
a key, so use it in scripts and CI. The interactive loop is what you get by leaving `--ticks` out.

```bash
gridsim case14 --ticks 6
```

```
$ gridsim case14 --ticks 6
t=08:00 tick  0  load  0.18GW  gen  0.19GW  loss    6MW  V 1.010-1.090  ok (3 NR)
t=08:20 tick  1  load  0.19GW  gen  0.19GW  loss    7MW  V 1.010-1.090  ok (3 NR)
t=08:39 tick  2  load  0.19GW  gen  0.20GW  loss    7MW  V 1.010-1.090  ok (3 NR)
t=09:00 tick  3  load  0.19GW  gen  0.20GW  loss    7MW  V 1.010-1.090  ok (3 NR)
t=09:20 tick  4  load  0.20GW  gen  0.21GW  loss    8MW  V 1.010-1.090  ok (3 NR)
t=09:39 tick  5  load  0.20GW  gen  0.21GW  loss    8MW  V 1.010-1.090  ok (3 NR)
[exit 0]
```

Each line gives the clock time (the minute is computed as the fractional hour times 60 and truncated, which gives
39.999999999999964 for 08:40, so that step prints as 08:39), the tick number, total load and generator output in GW, losses in MW, the
voltage range, and the convergence verdict `ok` or `DIV` with the Newton iteration count. 72 ticks cover a simulated
day. A tick that reads `DIV` has not converged. `--ticks` with a value that is not an integer exits 2, and so does
`--ticks 0` or a negative count, because 0 would otherwise mean the interactive loop:

```
$ gridsim case14 --ticks 0
error: --ticks expected a positive integer (leave --ticks out to run interactively) but got '0'
[exit 2]
```

The GB model gives the same format at transmission scale:

```
$ gridsim --ticks 4
t=08:00 tick  0  load 27.65GW  gen 27.68GW  loss   27MW  V 0.998-1.040  ok (3 NR)
t=08:20 tick  1  load 28.26GW  gen 28.29GW  loss   28MW  V 0.999-1.040  ok (3 NR)
t=08:39 tick  2  load 28.90GW  gen 28.93GW  loss   30MW  V 0.999-1.040  ok (3 NR)
t=09:00 tick  3  load 29.56GW  gen 29.59GW  loss   31MW  V 0.999-1.040  ok (3 NR)
[exit 0]
```

`case9` is the one case that never loops: without `--enableExport` it prints the detailed report and exits with the
report's own code, 0 when the solve converged and 3 when it did not. `--ticks` does not change that. The built-in
`case9` carries the published machine data, so its totals line reads `Inertia 7.5 GVAs`:

```
$ gridsim case9
GridSim  -  IEEE 9-bus (WSCC)
9 buses  9 branches  3 generators  base 100 MVA  60 Hz
========================================================================
Converged (4 NR iters, 0 OLTC passes, max mismatch 1.65E-014 p.u.)
Generator output 0.32 GW  Demand 0.32 GW  Losses 4.6 MW  Inertia 7.5 GVAs
...
Voltage range: 0.996 - 1.040 p.u. (in band)
Total losses: 4.64 MW, -92.16 MVAr
Protection simulation: no relay would operate on this state (generic settings).
[exit 0]
```

### A larger case

`gridsim case118 --once` prints the same report for 118 buses and 186 branches. The first lines are:

```
$ gridsim case118 --once
GridSim  -  IEEE 118-bus
118 buses  186 branches  54 generators  base 100 MVA  50 Hz
========================================================================
Converged (6 NR iters, 0 OLTC passes, max mismatch 2.85E-014 p.u.)
Generator output 4.37 GW  Demand 4.24 GW  Losses 132.5 MW  Inertia not in the data (no generator H)
...
Voltage range: 0.943 - 1.050 p.u. (in band)
Total losses: 132.48 MW, -644.08 MVAr
Protection simulation: no relay would operate on this state (generic settings).
[exit 0]
```

case118 needs 6 Newton iterations against 2 for case14 because it runs two generator reactive-limit passes of 3
iterations each, where case14 runs one pass of 2 (see [How the solve works](#how-the-solve-works)). Every meshed case,
case300 as much as case14, is solved by the sparse engine on the CPU, and the report has the same layout.

### Choosing the engine

The engine is chosen automatically from the network (see [How the solve works](#how-the-solve-works)). Nothing in the
report names it. Two switches change the choice or the arithmetic.

`--bfs` forces the backward/forward sweep, the engine built for radial distribution feeders. A radial feeder (one slack bus, exactly buses - 1 in-service branches, and every bus connected) is routed
to it without the flag, and `gridsim data/matpower/dist/case33bw.m --once` shows a 33-bus feeder solved that way. A bare
file name such as `case33bw.m` is searched for under the subfolders of `data/matpower`, so `gridsim case33bw.m --once`
solves the same feeder; a name that matches two files exits 2 and lists them. The
sweep accepts a feeder with up to 6 independent loops and declines a meshed transmission grid. On a declined network
the run reports non-convergence and exits 3. Loading any `.m` file prints `note:` lines on standard error, for
example `skipped 5 out-of-service branch(es)` for `case33bw.m`:

```
$ gridsim case14 --once --bfs
GridSim  -  IEEE 14-bus
14 buses  20 branches  5 generators  base 100 MVA  50 Hz
========================================================================
DID NOT CONVERGE (0 iters, max mismatch Infinity p.u.)
Generator output n/a (not converged)  Demand 0.26 GW  Losses 0.0 MW  Inertia not in the data (no generator H)

...
kV '-': base voltage not in the data for 14 of 14 bus(es) (the case gives 0).

Voltage range: 1.000 - 1.000 p.u. (in band)
Total losses: 0.00 MW, 0.00 MVAr
Protection simulation: skipped - the power flow did not converge, so there is no state to assess.
[exit 3]
```

The bus table of a declined solve holds the flat start (1.000 p.u., 0 degrees, zero injections), not a result, and
`Generator output` reads `n/a (not converged)`.
case14 has 7 independent loops, one over the sweep's limit of 6.

`--gpu` moves the linear solve inside each Newton step to a graphics device; `--cpu` is the default and stays on the
processor. On a GPU backend the dense-or-sparse choice keeps a crossover of 200 buses (dense up to 200, sparse above),
because the dense engine there is the device's own LU; the CPU is always sparse. `--gpu` tries ROCm, then OpenCL, then CUDA. `--gpu=rocm`, `--gpu=opencl` and `--gpu=cuda` name one
backend; `--gpu=auto` and `--gpu=cpu` are also accepted, and any other value exits 2. With `--gpu` (or `--gpu=auto`),
when no device is present, or an OpenCL device has no double-precision support, the run prints a warning on stderr,
falls back to the CPU, and the report is identical. A named vendor (`--gpu=rocm`, `--gpu=opencl`, `--gpu=cuda`) with no
usable device is refused with exit 2 instead, because the run asked for that device; `bench` and `gpucheck` are the
exceptions and report the absent vendor in their output:

```
$ gridsim case14 --once --gpu
GridSim  -  IEEE 14-bus
14 buses  20 branches  5 generators  base 100 MVA  50 Hz
========================================================================
Converged (2 NR iters, 0 OLTC passes, max mismatch 1.32E-010 p.u.)
Generator output 0.27 GW  Demand 0.26 GW  Losses 13.4 MW  Inertia not in the data (no generator H)
...
Protection simulation: no relay would operate on this state (generic settings).
[gpu] no usable gpu device found - falling back to the CPU backend.
[exit 0]
```

The `[gpu]` line goes to stderr, so it arrives after the report in the capture; on a terminal it appears first.

Check what a machine offers before relying on `--gpu`. `gpucheck` probes the CPU, ROCm, OpenCL and CUDA in turn, runs
a case9 dense solve and a case9 sparse solve on each present backend, and prints a `FAIL` line naming any failing
call. A machine with no usable GPU prints this:

```
$ gridsim gpucheck
GridSim GPU preflight
=====================
CPU cores: 8   OS: Microsoft Windows 10.0.26300
commit: v1.95.294-168-g786a2162

[CPU] cpu   (vendor=Cpu)
  CPU dense: build solver... solve case9... PASS  (4 it, 76.4 ms, |mis|=1.6E-014)
  CPU sparse: build solver... solve case9... PASS  (4 it, 3.5 ms, |mis|=1.6E-014)

[ROCm] cpu (rocm requested but unavailable)   (vendor=Cpu)
[ROCm] raw probe: ROCm unavailable: HIP runtime library (amdhip64) not found
[ROCm]   not present - skipped.

[OpenCL] cpu (opencl requested but unavailable)   (vendor=Cpu)
[OpenCL] raw probe: Intel(R) UHD Graphics [OpenCL] rejected: no usable FP64 (cl_khr_fp64) - GridSim needs double precision
[OpenCL]   not present - skipped.

[CUDA] cpu (cuda requested but unavailable)   (vendor=Cpu)
[CUDA] raw probe: no CUDA device available (ILGPU saw: [CPU, OpenCL]; driver probe: libcuda not found by .NET)
[CUDA]   not present - skipped.

Preflight done. A FAIL line names the failing call; if the process died mid-line, that call
segfaulted (a P/Invoke signature mismatch) - send that last line and I'll fix the binding.
gpucheck: OK - every probe that ran passed (absent backends are skipped, not failed).
[gpu] no usable rocm device found - falling back to the CPU backend.
[gpu] no usable opencl device found - falling back to the CPU backend.
[gpu] no usable cuda device found - falling back to the CPU backend.
[exit 0]
```

The solve times (76.4 ms and 3.5 ms here) and the commit differ on every run and machine. Read it as follows. A backend with `PASS` lines solved case9 and is usable. A `not present - skipped.` line, with the
`raw probe` line above it giving the reason, means the backend is unavailable and `--gpu` falls back past it. Every
run directory and solve export records the backend actually used, for example `cpu (gpu requested but unavailable)`. GPU
arithmetic is outside the bit-identity guarantee that holds on the CPU (see
[How the solve works](#how-the-solve-works)). The `gpucheck` section of the CLI reference is
[ch. 19, CLI reference](<0019 CLI reference.md#gpucheck>); the verb is also covered in
[ch. 27, Validation tools](<0027 Validation tools.md>).

### Solve export

`--enableExport` writes one fully traced solve to JSON instead of printing the report:

```bash
gridsim case118 --once --enableExport
```

The run prints one line, `[export] dense-newton: converged=True iters=6 maxMis=2.85E-014`, followed by `->` and the
full path of the file written (the capture shows it repo-relative):

```
$ gridsim case118 --once --enableExport
[export] dense-newton: converged=True iters=6 maxMis=2.85E-014  ->  results\exports\case118-solve.json
[exit 0]
```
 The solver name is `dense-newton`, `sparse-newton` or `bfs`, according to the
engine used; a sweep that declines the network leaves it empty (`[export] : converged=False`). `--enableExport` wins over `--once`: the report is not printed. The file goes to
`results/exports/<case>-solve.json` under the current directory unless `--enableExport=<path>` names another location; missing
directories are created.

The export runs one solve on the raw engine with the iteration cap raised to 100, generator Q-limits on, and no OLTC
loop, so its result matches the report only for a case without tap changers. The document has the schema
`gridsim-solve-export/1` and these top-level sections.

| Section | Contents |
|---|---|
| `schema` | The string `gridsim-solve-export/1`. |
| `meta` | `Case`, `Backend` (`cpu` or the GPU used), `GitCommit`, `TimestampUtc`, `ElapsedMs` and `Machine`. |
| `model` | The network as solved: buses (with `zip`), generators, branches, OLTCs and boundaries. |
| `solve` | The trace: `solver`, `n`, `converged`, `iterations`, `maxMismatch`, `jacobianOmittedForSize`, the Y-bus as `ybus` triplets, the per-unit `busSpec`, `qLimitEvents`, `slackQNote` when the slack breaches its
aggregate reactive limit, and `passes`. Each pass lists its `effectiveTypes`, `pvpqIndices` and `pqIndices` and, for every Newton iteration, the `maxMismatch`, `mismatch` vector, `correction` vector and (dense engine) `jacobian`. |
| `result` | `totalLossMw`, `totalLossMvar`, per-bus `vm`, `vaDeg`, `pInjMw`, `qInjMvar`, and per-branch `lineFlows` with loading. |
| `physics` | A physics-law audit of the solved state: `gatePass`, the `hardGateLaws` that gate it and each law's `status` and residual. |

For case118 the `solve.passes` array has 2 entries (two Q-limit passes, 3 Newton iterations each, 6 in total) and the
file is 4.7 MB, mostly Jacobians. Only the dense engine records the Jacobian, and only up to 300 buses unless
`--export-jacobian` is given. The sparse engine records no Jacobian and leaves `jacobianOmittedForSize` false; the
flag is set only by the dense engine on a model above 300 buses, which the CLI reaches only through `transient` on
a model of 301 to 1,500 buses, because the export routes every model above 200 buses to the sparse engine (the
export keeps a 200-bus crossover so that small models still record their Jacobian). The desktop app replays
this document and `crossval` diffs it against pandapower; the field reference is in
[ch. 31, Exports and formats - a field guide](<0031 Exports and formats.md>).

### Recorded runs with `transient`

`transient` is the headless time-domain form. It drives the live simulator (warm-started from the last converged
slice) for a simulated duration at a fine time slice, integrates the swing equation for frequency every slice, and
records everything to a run directory.

```bash
gridsim transient case118 --minutes 1 --dt-ms 100 --trip 1000 --trip-at 20
```

```
$ gridsim transient case118 --minutes 1 --dt-ms 100 --trip 1000 --trip-at 20
[transient] case118: done in 7.0s  nadir 45.152Hz  maxRoCoF -1.153Hz/s  exports 42  -> results\transient-case118-20261019-183811
[transient] case118: 600 slices @ 100ms (1 min) on Newton-Raphson (dense)...
[exit 0]
```

The first line is the closing summary: the wall time, the frequency nadir, the worst rate of change of frequency
(RoCoF), the export count and the run directory. The wall time and the run-directory stamp differ on every run. This run trips 1000 MW of infeed 20 s in. case118
carries no inertia data, so the live simulator substitutes an inertia constant of 4 s on the generator MVA
(`LiveSolveSettings.FallbackInertiaH`); read the frequency numbers of such a case as a demonstration, not a
prediction.

The run directory is created under `results/` in the current directory as `results/transient-<case>-<stamp>/`:

```
results/transient-case118-20261004-193728/
|-- run-metadata.json      what ran: command, machine, runtime, git commit, backend
|-- summary.md             nadir, worst RoCoF, non-converged slices, export count
|-- timeseries.csv         one row per logged slice, after a `#` header block
`-- exports/
    |-- slice-000000001.json
    `-- slice-000000180.json ... slice-000000220.json
```

`timeseries.csv` logs every slice at the default `--log-every 1`; a larger `--log-every N` logs the first slice, the
last, every slice within `--trip-window` of the trip (default 2 s) and every Nth. It has the columns `tick`, `t_seconds`, `freq_hz`, `rocof_hz_s`, `gen_mw`, `demand_mw`, `iters`,
`max_mismatch`, `vmin_pu`, `vmax_pu`, `solve_ms` and `converged`. The `exports/` folder holds a full solve export
(`gridsim-solve-export/1`) for the first slice, for every slice within the trip window and for every `--export-every N`
slice. This run wrote 42 files: slice 1 and the 41 slices from 180 to 220 around the trip. `transient` exits 3 if any slice did not
converge, 2 for a `--trip-at` past the end of the run, and 1 if no case loaded. The flags are in
[ch. 19, CLI reference](<0019 CLI reference.md#transient>).

`replay-event`, `scenario`, `fleet`, `evidence`, `gda-replay` and `estimate` write the same run-directory
pattern with a `run-metadata.json`, so any result traces to the commit and backend that produced it. Run directories
that hold solve exports (`exports/slice-*.json` or `state-*.json`), which `transient` and `gda-replay` write, in a
`results/` folder in the desktop app's directory or up to seven levels above it, are discovered automatically and can
be stepped through frame by frame ([ch. 8, The desktop app](<0008 The desktop app.md>)).

## How the solve works

**Engine choice.** The shared rule is `SolverSelector` (`src/GridSim.Core/PowerFlow/SolverSelector.cs`). It has two
forms. `Resolve` applies the radial test first and then the bus-count threshold. `ResolveNewton` and `CreateNewton`
apply the bus-count threshold only and never return the sweep.

| Network | `Resolve` | `ResolveNewton` and `CreateNewton` |
|---|---|---|
| One slack bus, exactly buses - 1 in-service branches, and every bus connected over them (a tree) | Backward/forward sweep | Sparse Newton-Raphson |
| Anything else | Sparse Newton-Raphson | Sparse Newton-Raphson |

The bus-count threshold (`SolverSelector.DefaultSparseThreshold`) is 0, so every CPU Newton solve is sparse. A caller
can pass its own threshold, and the dense engine is chosen explicitly where a tool offers it.

Only in-service branches count towards the radial test, so a feeder with an open tie is still routed to the sweep. The
test also checks connectivity: a network with buses - 1 in-service branches that does not join every bus (one island
holds a loop while another bus hangs free), or that has a branch naming a bus the model does not contain, is not
radial and goes to Newton, which reports its own failure. Which path applies which form:

- The radial test applies to the `gridsim <case>` report (`--once`), the live loop of the command line, the solve
  export (`--enableExport`), the CLI verbs that call `SolverBackend.Solver`, the live simulator (`LiveSim`, which
  `transient` uses) and the cycle workers (`CycleRunner`, `ResidentCycleService`). `--bfs` forces the sweep on the
  bare, case and directory forms.
- The Newton-only form, with no sweep, applies to the contingency screens, AC-OPF, the continuation power flow, the
  tap optimiser, restoration, the REST `/solve` endpoint, the visualiser export and the CLI verbs that call
  `SolverBackend.NewtonSolver`.
- Some paths keep a rule of their own: the solve export (`--enableExport`) and the `crossval` export keep a 200-bus
  crossover (only the dense engine records the Jacobian), a GPU backend keeps it too (its dense engine is the device
  LU), the state estimator keeps it for its own step choice, the live simulator switches at 1,500 buses, `gda --solve`
  takes `--dense` or `--sparse`, and `bench` builds each engine explicitly. The list is in the
  [solver selection](<0019 CLI reference.md#solver-selection>) section of the CLI reference.

The `SolverSelector` source comment records `bench` figures (median wall time per solve, Release, warm, results
bit-identical on every case) with sparse faster at every size, from case9 (0.247 ms dense, 0.209 ms sparse) to gb-synthetic (3,582 ms dense,
28.5 ms sparse); hence the threshold of 0. The two engines give the same answer to the last bit on the CPU, so the
threshold changes speed and memory, never a result.

**Live loops use a different threshold.** The live simulator (`LiveSim`) behind the desktop and web live views, the
`transient` verb and the `cycle` base solve switches to sparse above 1,500 buses (`LiveSolveSettings.SparseThresholdBuses`),
because a dense solve each frame is too slow for animation past that size. On a model it routes to sparse it caps
Newton at 40 iterations, turns generator Q-limits off, and skips the OLTC loop. The live loop of the command line,
`gridsim <case>` and `--ticks`, is not `LiveSim`: it uses the shared rule (sparse on the CPU, with the radial test)
with Q-limits and the OLTC loop on.

**Dense and sparse agree to the last bit.** On the CPU the two Newton engines give identical voltages, angles, flows,
losses, mismatches and iteration counts, bit for bit. The choice of engine therefore changes speed and memory, never
an answer. A GPU backend changes only the linear solve inside each Newton step and sits outside that guarantee. The
CPU sparse solve is a direct sparse LU with reverse Cuthill-McKee ordering and partial pivoting; an ILU(0)-
preconditioned BiCGSTAB solver runs only if the factorisation is singular, and a GPU sparse solve that gives up is
retried on the exact CPU LU first.

**Newton-Raphson.** Each iteration builds the power mismatch at every bus, solves the Jacobian system for a
correction, and updates angles and the voltages of load buses. The solve stops when the largest mismatch is below
`1e-8` p.u. or after 30 iterations (`PowerFlowOptions.Tolerance`, `PowerFlowOptions.MaxIterations`). The mismatch is
evaluated after every step, the 30th included, so a solve that reaches the tolerance on its 30th step is converged and
one that has not reached it then is reported as not converged, with the mismatch of the state it returns. A solve that
produces a NaN or infinity is also reported as not converged. The equations, damping and Jacobian layout are in
[ch. 3, Power flow](<../technical/0003 Power flow.md>).

**Generator Q-limits.** Generator buses start as voltage-controlled (PV). After each converged pass the solver
checks each PV generator's reactive output against its limits with a tolerance of 1e-3 MVAr. A generator past a limit
is pinned at that limit and its bus becomes a load bus (PQ); the network is re-solved. This repeats for at most 12
passes (`PowerFlowOptions.MaxQLimitPasses`), and a bus switched this way stays switched. If the 12th pass still
switched a bus, the solver solves once more and reports the result as not converged. `NR iters` in the report is the
sum over all passes.

**The OLTC loop.** Around whichever engine runs, the steady-state solver regulates on-load tap changers: solve; for
each tap changer whose controlled bus is outside its voltage deadband, step the tap one increment; re-solve; stop
when a pass moves no tap. It runs at most 25 passes (`SteadyStateSolver.DefaultMaxOltcPasses`). It never steps a tap on
a solve that did not converge. A tap changer whose controlled bus is held by a generator (a PV or slack bus, judged on
the bus types the converged solve ended with) holds its tap, because the machine's voltage regulator fixes that bus;
`SteadyStateSolver.TapsHeldAtGeneratorBus` counts them. The loop is settled when a pass moves no tap and no tap sits at
its limit with its bus still outside the deadband (`SteadyStateSolver.TapsSettled`); the report prints the
`Taps NOT SETTLED` line otherwise (see [Reading the output](#reading-the-output)), naming the transformers from
`SteadyStateSolver.TapsAtLimit` or the pass cap. A pass count of 25 means the cap was reached after a tap moved on the last pass, so the final state is unverified.

**Voltage-dependent loads.** A bus whose model carries a ZIP load draws `P(V) = Pd * (ap*V^2 + bp*V + cp)` and the
same form for Q, evaluated at the solved voltage and included in both the mismatch and the Jacobian. A bus without
one, or with a constant-power ZIP, draws its stated load. How to supply a ZIP model is in
[ch. 2, Loading networks](<0002 Loading networks.md>).

## Pitfalls

- **`DID NOT CONVERGE` and exit 3.** The solver reached its iteration cap, hit a NaN, or declined the network (the
  sweep on a meshed grid). The `--once` report, the `case9` report, `--enableExport` (which still writes the export
  and says so on stderr), `--ticks N` (which prints `DIV` on each non-converged tick) and `transient` all exit 3 so a
  script can test for it. The
  tables of a non-converged report are not a solution: do not read voltages from them. Check the case for an
  unsupplied island, a generator set-point the network cannot support, or a load beyond what the network can carry;
  see [ch. 30, Troubleshooting](<0030 Troubleshooting.md>).
- **`OUT OF BAND` is a report, not an error.** The solve converged and the exit code is 0. The verdict compares the
  extreme voltages with the 0.94 to 1.06 p.u. operating band. Standard test cases routinely sit outside it, as
  case14 does.
- **A fallback to CPU is silent in the report.** With `--gpu` and no usable device the only sign is the `[gpu]` line
  on stderr. A script that discards stderr cannot tell. Use `gpucheck` to find out what a machine offers.
- **The live view needs a key press to stop.** `gridsim <case>` with no `--once` and no `--ticks` loops until a key
  is pressed, and a process with redirected input never receives one. In scripts always pass `--once` or `--ticks N`.
- **`Generator output` on a failed run is `n/a (not converged)`.** The figure is read off the solved injections, so it
  is withheld when the state is not a solution. The bus table of such a run is not a result either.
- **`Taps NOT SETTLED` on an exit 0 run.** The power flow converged, but a tap changer ran out of range with its bus
  still outside the deadband, or the tap loop hit its pass cap. The voltages are the converged state of the final taps,
  not a regulated steady state; widen the tap range or check the controlled bus.
- **`-` in the `kV` column.** The case gives no base voltage for the bus (a MATPOWER `baseKV` of 0). It is a label, not
  a failure.
- **A missing `.m` file or `--dir` folder.** The run prints a one-line `error:` naming the path and its full path and
  exits 1, before any solve:

  ```
  $ gridsim nosuch.m --once
  error: MATPOWER case file not found: 'nosuch.m' (full path nosuch.m).
  [exit 1]
  ```

  (The capture shows the full path made repo-relative; on a terminal it is absolute.)
- **A case over the licence limit is refused.** A user network above the installed tier's bus limit exits 2. The
  built-in cases and the shipped files of the bundled folders (`data/matpower`, `data/uk`, `data/gb-synthetic`,
  `data/gb-full`, `data/intl` and `data/dist3ph`, checked against `data/bundled.sha256`) are never counted; see
  [ch. 4, Licensing and activation](<0004 Licensing and activation.md>).
- **A mistyped flag or value exits 2.** Unknown flags, a malformed `--ticks`, `--ticks 0` or a negative count, and an
  unknown `--gpu` vendor stop the run with a message naming the flag, before any solve.

## Limits

- The report does not name the engine that solved the case; only the export's `solve.solver` field does.
- The sweep engine accepts at most 6 independent loops.
- The 200-bus crossover that the GPU routing, the solve export and the state estimator keep comes from a bench dated
  2026-07-14, run before the engines shared the sparse-LU step, and has not been re-measured.

## See also

- [ch. 3, Power flow](<../technical/0003 Power flow.md>) - the equations, damping, Jacobian and invariants
- [ch. 2, Loading networks](<0002 Loading networks.md>) - case selection, MATPOWER files, JSON folders, ZIP loads
- [ch. 19, CLI reference](<0019 CLI reference.md#running-a-case-no-verb>) - the bare run, its flags and exit codes
- [ch. 19, CLI reference](<0019 CLI reference.md#global-switches>) - `--once`, `--ticks`, `--gpu`, `--bfs` and the export switches
- [ch. 19, CLI reference](<0019 CLI reference.md#transient>) - every `transient` flag
- [ch. 19, CLI reference](<0019 CLI reference.md#gpucheck>) - the GPU preflight
- [ch. 31, Exports and formats - a field guide](<0031 Exports and formats.md>) - the solve export and run directories in full
- [ch. 11, Contingency analysis](<0011 Contingency analysis.md>) - trip, screen, nminus1, stress, chain
- [ch. 27, Validation tools](<0027 Validation tools.md>) - crossval, bench and gpucheck
- [ch. 8, The desktop app](<0008 The desktop app.md>) - the same solves, drawn live; replaying run directories
