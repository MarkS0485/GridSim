# 11. Contingency analysis

Contingency analysis asks what the grid does when something is lost or the demand grows. GridSim answers with nine
verbs. Three take branches out of the network one at a time, in pairs, or in sequence with a corrective redispatch,
re-solve the power flow after each outage and classify the result (`nminus1`, `nminus2`, `n-1-1`). `nminus1-sweep` runs the
single-branch screen in parallel and at scale. `trip` and `screen` lose generators and judge the frequency response
against the GB limits, with no power flow. `stress` scales the load until the power flow stops converging. `chain` solves
a sequence of operating points, each warm-started from the last. `cycle` runs the N-1 screen continuously inside the
real-time analysis cycle. Battery storage in a `chain` run is specified in
[ch. 27, Battery energy storage](<../technical/0027 Battery energy storage.md>). The desktop app's right-click trip is described in
[ch. 8, The desktop app](<0008 The desktop app.md>).

## Before you start

`gridsim` means the installed command, or `dotnet run --project src/GridSim.Cli -c Release --` from a checkout
([ch. 1, Getting started](<0001 Getting started.md>)). The network a verb runs on differs between verbs, and the
difference matters.

Every verb in this chapter takes the network the same way: a positional case or `--dir <folder>`
([ch. 19, Case arguments](<0019 CLI reference.md#case-arguments>)). The positional case is a built-in name (`case9`,
`case14`, `case30`, `case57`, `case118`, `case300`, `case145`, `case24`, `cigre`, `uk`, `gb-full`, `gb-synthetic`), a
MATPOWER `.m` file (a path, a path under `data/matpower` such as `pegase/case1354pegase.m`, or a bare file name such as
`case33bw.m`, which is found in the `data/matpower` subfolders), or a JSON case directory, meaning a folder with a
`system.json` ([ch. 2, Loading networks](<0002 Loading networks.md>)). `--dir <folder>` names a JSON case
directory. Giving both is refused with exit 2 unless they name the same folder, and a `--dir` folder that does not exist
exits 1 with `error: <verb>: case folder not found: ...`.

- **`trip`, `screen`, `nminus1`, `nminus2` and `n-1-1`** default to the reduced GB model (`data/uk`, 20 buses, 24
  branches). `trip` also takes a generator name: `trip <case> <gen>`, or `trip <gen>` on the default model. A single
  free token is read as a case when it names one, and as a generator name otherwise.
- **`nminus1-sweep`, `stress` and `chain`** default to `case118`. `nminus1-sweep` and `stress` also take `--all` for
  every registered case; `--all` with a case or `--dir` exits 2.
- **`cycle`** defaults to `data/uk`.

A user network above the installed licence tier's bus limit is refused with exit 2
([ch. 4, Licensing and activation](<0004 Licensing and activation.md>)). The bundled networks are never counted: `gb-full`
(3,539 buses), `gb-synthetic`, `uk` and the folders under `data/matpower`, `data/intl` and `data/dist3ph` load at every
tier.

The Newton-Raphson engines behind these verbs are bit-identical between runs, so a screen run twice gives the same
table to the last digit. The worker count changes throughput only: a parallel screen returns the same converged and
secure verdict for every outage as a sequential one. Every captured block in this chapter carries the timings and
stamps of the run that produced it; they differ by machine and run.

`nminus1`, `nminus2`, `n-1-1`, `nminus1-sweep`, `stress` and `chain` solve with one option set: 100 Newton iterations,
generator reactive limits enforced, and the on-load tap changer loop, so every post-outage state is the tap-regulated
steady state that `gridsim <case> --once` reports ([ch. 19, Solver selection](<0019 CLI reference.md#solver-selection>)).

## Worked example

Screen every single branch outage on the reduced GB model:

```bash
gridsim nminus1
```

The flags of every verb are in [ch. 19, CLI reference](<0019 CLI reference.md#nminus1>).

```
$ gridsim nminus1
GridSim  -  N-1 branch screening: GB reduced (10-zone)
==========================================================================
Base case: secure (no violation before any outage).
Outage                          Status        MaxLoad  WorstBnd  Vmin   Vmax
--------------------------------------------------------------------------
L1-2 N-S Scotland               ISLANDED      islanded
L2-3 B6 boundary                ISLANDED      islanded
T1 GSP N Scotland (tx)          ISLANDED      islanded
T2 GSP S Scotland (tx)          ISLANDED      islanded
T3 GSP N England (tx)           ISLANDED      islanded
T4 GSP Yorkshire (tx)           ISLANDED      islanded
T5 GSP N Wales & Mersey (tx)    ISLANDED      islanded
T6 GSP Midlands (tx)            ISLANDED      islanded
T7 GSP East Anglia (tx)         ISLANDED      islanded
T8 GSP London & SE (tx)         ISLANDED      islanded
T9 GSP South (tx)               ISLANDED      islanded
T10 GSP SW & S Wales (tx)       ISLANDED      islanded
L3-4 N England-Yorks            secure           85%      68%  0.998  1.040
L3-5 N England-N Wales          secure           85%      68%  0.998  1.040
L4-6 Yorks-Midlands             secure           85%      68%  0.998  1.040
L5-6 N Wales-Midlands           secure           85%      68%  0.998  1.040
L4-7 Yorks-E Anglia             secure           85%      68%  0.998  1.040
L6-7 Midlands-E Anglia          secure           85%      68%  0.998  1.040
L6-8 Midlands-London            secure           85%      68%  0.998  1.040
L7-8 E Anglia-London            secure           85%      68%  0.998  1.040
L6-9 Midlands-South             secure           85%      68%  0.998  1.040
L8-9 London-South               secure           85%      68%  0.998  1.040
L9-10 South-SW                  secure           85%      68%  0.998  1.040
L5-10 N Wales-SW                secure           85%      68%  0.998  1.040
--------------------------------------------------------------------------
24 outages screened  12 secure  0 with violations  12 islanded
Verdict: NOT SECURE, 12 insecure outage(s) (exit 3).
[exit 3]
```

The model has 24 branches, so 24 outages were screened. The base case, solved first with the same options, has no
violation. Twelve outages leave the network connected and inside its limits (`secure`). Twelve leave a part of the
network cut off, and the power flow cannot solve it (`ISLANDED`): the two north-south lines `L1-2` and `L2-3` that carry
Scotland, and the ten transformers `T1` to `T10`, each the only feed to one grid supply point. The twelve secure rows are
the meshed 400 kV lines. Their `MaxLoad` of 85% and `WorstBnd` of 68% are the loading of the `T6 GSP Midlands`
transformer (84.53% in the `L3-4` row) and the worst boundary loading (67.79%), which none of these ten line outages
moves by a whole percent, so the columns repeat from row to row. `Vmin` 0.998 pu is the voltage after the tap changers
have acted, the same as `gridsim uk --once` reports. The model is not N-1 secure, because twelve outages island, so the
verdict says so and the run exits 3.

## Reading the output

The report has a header, the base-case line, a table with one row per outage, a footer and the verdict.

**Header.** `GridSim  -  N-1 branch screening: <model name>`, then a line of `=`.

**Base case.** The intact network is solved first, with the same options as the outages, and its violations are printed
once: `Base case: secure (no violation before any outage).` or `Base case: N pre-existing violation(s), reported once
here and not counted against any outage: <list>`. A base case that does not converge stops the run with exit 3 and
`error: <verb>: the base case of <model> did not converge (...)`.

**Outage.** The branch name, cut to 29 characters followed by `...` when it is longer than 30. A transformer carries the suffix
` (tx)`. A branch with no name is shown as `<from>-<to>` bus numbers. In `nminus2` the label joins two branches with
` + `; in `n-1-1` it reads `first => second`.

**Status.** One of four values.

- `secure`: the power flow converged and no limit is violated.
- `base only`: the power flow converged and the outage adds no violation of its own, but the state still carries the
  violations the base case already had. It counts as secure for the outage; the base case itself fails the verdict.
- `VIOLATIONxN`: the power flow converged and the outage causes `N` violations that the base case did not have. A
  violation is new when its element was not violating in the base case: a branch above 100% of its rating (`RateA`),
  a boundary above its transfer limit, or a bus below 0.94 pu or above 1.06 pu. One violation is counted for each new
  branch, one for each new boundary, one if any bus newly falls below 0.94 pu and one if any newly rises above 1.06 pu.
  The table prints the count, not the list. Each bus is judged against its own limits where the case gives them
  (MATPOWER VMIN / VMAX, JSON `vminPu` / `vmaxPu`) and against 0.94 / 1.06 pu on any side it leaves unset;
  `--global-vlimits` judges every bus against the band instead. Every MATPOWER file carries VMIN and VMAX, so the
  choice changes verdicts on the bundled MATPOWER cases; see [ch. 19](<0019 CLI reference.md#nminus1>).
- `ISLANDED`: no converged state, and the outage split the network (the post-outage network has more connected parts
  than the case). The row prints the reason in place of the numbers: `islanded`, or `solver error: <type>: <message>`
  when the solver threw. The screens elect no slack bus for a separated part, so a part cut off from the slack bus has
  no solution, whether or not it holds generation; a part that provably cannot solve is reported without running the
  solver on it.
- `NO SOLUTION`: no converged state on a network that is still connected (a voltage collapse, for example `case14`
  branch `1-2` with reactive limits on); the reason reads `no solution`. The count line adds `N not solved`.

**MaxLoad.** The highest branch loading in the post-outage state, in whole percent of `RateA`: the larger of the two
end flows over the rating. A branch with no rating loads 0%, so a case whose branches carry no ratings never shows
a thermal violation. `case14`, `case57`, `case118` and `case300` carry no ratings; `case9`, `case30`, `case24` and `cigre`
rate every branch, and `case145` rates 58 of its 453.

**WorstBnd.** The highest boundary loading, in whole percent of the boundary's transfer limit. A boundary with limit 0
has no limit authored: its flow is not reported as loading and it is never over limit. A case with no boundaries shows
0%.

**Vmin, Vmax.** The lowest and highest bus voltage magnitude in pu, to three decimals.

**Footer.** `N outages screened  S secure  V with violations  I islanded`, with `M not solved` added when there are any,
then, when some secure rows are `base only`, a line counting them.

**Order.** `nminus1` lists islanded outages first, then converged outages by violation count, largest first; ties keep
model order. `nminus2` and `n-1-1` list worst first by stress score (see [nminus2](#nminus2---two-branches-lost-together)).

**Verdict and exit code.** The last line is the verdict, and the exit code carries it: `Verdict: SECURE (exit 0).` when
the base case and every outage are secure, `Verdict: NOT SECURE, ... (exit 3).` when the base case has a violation or
any outage violates, islands or does not solve. A script can read the exit code.

A network that is already outside the voltage band before any outage reports that violation once, in the base-case
line, and not on every outage. The all-island Irish model is an example:

```bash
gridsim nminus1 --dir data/intl/ireland-all-island
```

```
$ gridsim nminus1 --dir data/intl/ireland-all-island
GridSim  -  N-1 branch screening: Ireland all-island (EirGrid + SONI) - open transmission model
==========================================================================
Base case: 1 pre-existing violation(s), reported once here and not counted against any outage: overvoltage 1.126 p.u.
Outage                          Status        MaxLoad  WorstBnd  Vmin   Vmax
--------------------------------------------------------------------------
L3105 6062-6067                 ISLANDED      islanded
L1437 6063-6077                 ISLANDED      islanded
L14631 6069-6083                ISLANDED      islanded
TX16082 7033-7034 (tx)          ISLANDED      islanded
L10420 6067-6068                VIOLATIONx1      23%       0%  1.020  1.159
L10419 6068-6069                VIOLATIONx1      24%       0%  1.020  1.161
L10404 6075-6076                VIOLATIONx1      30%       0%  1.019  1.167
L13030 6071-7197                VIOLATIONx1      26%       0%  1.020  1.200
...
TX15942 6079-6080 (tx)          base only        22%       0%  1.020  1.125
TX15943 6085-6086 (tx)          base only        23%       0%  1.020  1.173
TX15944 6090-6091 (tx)          base only        23%       0%  1.020  1.174
--------------------------------------------------------------------------
82 outages screened  70 secure  8 with violations  4 islanded
(68 of the secure outages are 'base only': they add no violation, but the base case's own violations remain in their state)
Verdict: NOT SECURE, 1 base-case violation(s) and 12 insecure outage(s) (exit 3).
[exit 3]
```

The base case already reaches 1.126 pu (`gridsim --dir data/intl/ireland-all-island --once` reports `Voltage range:
1.020 - 1.126 p.u. (OUT OF BAND)`), and that is printed once. Four outages island. Eight push a bus that was inside
the band above 1.06 pu, so they violate. Seventy are secure for the outage; 68 of them are `base only`, because the
buses that were already high stay high. `base only` does not say how far an already-high bus moves: `TX15943` takes the
highest voltage from 1.126 to 1.173 pu and is still `base only`, so read `Vmax` on those rows.

## Variations

### nminus2 - two branches lost together

```bash
gridsim nminus2 [--dir <folder>] [--candidates N] [--exhaustive] [--top N]
```

`n-2` is an alias; the flags are in [ch. 19](<0019 CLI reference.md#nminus2>).

A full pairwise sweep is C(n,2) solves. On the reduced GB model that is 276 solves. gb-full has 5,918 branches, which
makes 17,508,403 pairs, so `nminus2` screens N-1 first, ranks the single outages that solve by how hard they stress the
system, keeps the `--candidates` most stressed, and solves only the unordered pairs among them. The score is the branch
loading plus the boundary loading plus a penalty for any voltage outside 0.94 to 1.06 pu. A single outage that islands
or does not solve is not ranked: it already fails at N-1, so it is left out of the pairing and listed under the table.
`--candidates` is held between 2 and the number of single outages that solve. `--exhaustive` solves every pair and
ignores `--candidates`. `--top` limits the rows printed; every pair is still screened and counted. The base case, the
netting, the statuses and the verdict are as for `nminus1`; the outages left out of the pairing also count against the
verdict.

```
$ gridsim nminus2 --exhaustive --top 216
GridSim  -  N-2 branch screening: GB reduced (10-zone)  [exhaustive (every pair)]
==========================================================================
Base case: secure (no violation before any outage).
Outage                          Status        MaxLoad  WorstBnd  Vmin   Vmax
--------------------------------------------------------------------------
L1-2 N-S Scotland + L2-3 B6 b...  ISLANDED      islanded
...
T7 GSP East Anglia (tx) + T10...  ISLANDED      islanded
T8 GSP London & SE (tx) + T9 ...  ISLANDED      islanded
T8 GSP London & SE (tx) + T10...  ISLANDED      islanded
T9 GSP South (tx) + T10 GSP S...  ISLANDED      islanded
L7-8 E Anglia-London + L8-9 L...  VIOLATIONx1     112%      68%  0.998  1.040
L6-8 Midlands-London + L8-9 L...  VIOLATIONx1     112%      68%  0.998  1.040
L6-8 Midlands-London + L7-8 E...  VIOLATIONx1     111%      68%  0.998  1.040
L3-4 N England-Yorks + L5-6 N...  secure           87%      68%  0.998  1.040
--------------------------------------------------------------------------
(showing worst 216 of 276)
276 outages screened  61 secure  3 with violations  212 islanded
Verdict: NOT SECURE, 215 insecure outage(s) (exit 3).
[exit 3]
```

Of the 276 pairs, 212 island and 64 converge. Three converged pairs violate (the loss of two of the London lines
overloads the third at 112%) and 61 are secure. The table is the same as for `nminus1`, worst first, with the pair named
by both branches and `(showing worst N of M)` above the footer when `--top` cut the list.

On a network with radial branches the single outages that island are kept out of the candidate set, so every pair is
made of outages that solve on their own. The Irish model has four islanding outages among 82 branches; with
`--candidates 6` the six candidates are the six most-stressed outages that solve, and the four islanding ones are listed
under the table:

```bash
gridsim nminus2 --dir data/intl/ireland-all-island --candidates 6
```

```
$ gridsim nminus2 --dir data/intl/ireland-all-island --candidates 6
GridSim  -  N-2 branch screening: Ireland all-island (EirGrid + SONI) - open transmission model  [top-6 candidate pairs]
==========================================================================
Base case: 1 pre-existing violation(s), reported once here and not counted against any outage: overvoltage 1.126 p.u.
Screening N-1 first, then pairing the 6 most-stressed single outages that solve.
Outage                          Status        MaxLoad  WorstBnd  Vmin   Vmax
--------------------------------------------------------------------------
L13030 6071-7197 + L3100 6076...  ISLANDED      islanded
L13030 6071-7197 + L10404 607...  ISLANDED      islanded
L3100 6076-8427 + L10404 6075...  ISLANDED      islanded
L10418 6069-8427 + L13479 608...  VIOLATIONx1      46%       0%  1.020  1.324
L13030 6071-7197 + L13479 608...  VIOLATIONx1      45%       0%  1.020  1.313
L10418 6069-8427 + TX15944 60...  VIOLATIONx1      30%       0%  1.020  1.269
L13479 6083-7691 + L3100 6076...  VIOLATIONx1      40%       0%  1.018  1.256
L13479 6083-7691 + L10404 607...  VIOLATIONx1      40%       0%  1.019  1.253
L13030 6071-7197 + TX15944 60...  VIOLATIONx1      27%       0%  1.020  1.259
L13479 6083-7691 + TX15944 60...  base only        35%       0%  1.020  1.238
L10418 6069-8427 + L3100 6076...  VIOLATIONx1      36%       0%  1.018  1.225
L10418 6069-8427 + L10404 607...  VIOLATIONx1      35%       0%  1.019  1.223
L3100 6076-8427 + TX15944 609...  VIOLATIONx1      33%       0%  1.018  1.223
TX15944 6090-6091 (tx) + L104...  VIOLATIONx1      33%       0%  1.019  1.220
L10418 6069-8427 + L13030 607...  VIOLATIONx1      21%       0%  0.838  1.020
--------------------------------------------------------------------------
15 outages screened  1 secure  11 with violations  3 islanded
(1 of the secure outages are 'base only': they add no violation, but the base case's own violations remain in their state)
4 single outage(s) left out of the pairing because they already fail at N-1 (islanded or no solution):
  L3105 6062-6067                 islanded
  L1437 6063-6077                 islanded
  L14631 6069-6083                islanded
  TX16082 7033-7034 (tx)          islanded
Verdict: NOT SECURE, 1 base-case violation(s) and 18 insecure outage(s) (exit 3).
[exit 3]
```

All fifteen pairs are new information: three island together although each member solves alone, eleven violate and
one adds nothing to the base case's overvoltage. The verdict counts the 14 insecure pairs and the 4 outages left out
of the pairing.

### n-1-1 - lose, redispatch, lose again

```bash
gridsim n-1-1 [--dir <folder>] [--candidates N] [--exhaustive] [--top N]
```

`nminus1minus1` is an alias; the flags are in [ch. 19](<0019 CLI reference.md#n-1-1>).

N-1-1 is not N-2. In an N-2 both branches are lost in one event and the order does not
matter. In an N-1-1 the first branch trips, the operator redispatches generation, and then the second trips, so the pairs
are ordered: `A => B` and `B => A` are different scenarios and both are screened. The candidate set is chosen as for
`nminus2`, and every ordered pair of distinct candidates is solved: `c(c-1)` solves for `c` candidates.

The corrective action is what an operator does after the first loss. It acts only when the first outage leaves a rated
line or a limited boundary over its limit in the DC model of the network; with nothing over its limit the dispatch is
unchanged and the pair is exactly the N-2 pair. When something is over its limit it moves the fewest MW of generation
(raises some units, lowers others, within each unit's limits) that bring every rated line and every limited boundary
back inside its limit, and it keeps each DC island's total generation, so a first outage that splits the network is
redispatched island by island. When no such redispatch exists the dispatch is left as it was and the second outage is
screened on the unchanged operating point. The redispatch is computed once per first outage and reused for every
second. When several redispatches move the same fewest MW, a fixed tie-break picks one (the simplex takes the
lowest-index entering column among those whose reduced costs agree within 1e-10 relative), so the plan does not depend on the order of floating-point
sums; on `case145`, pglib case73 and `case30_as` it chooses between whole-unit moves of equal total.

```bash
gridsim n-1-1 --exhaustive --top 430
```

```
$ gridsim n-1-1 --exhaustive --top 430
GridSim  -  N-1-1 sequential screening: GB reduced (10-zone)  [exhaustive (every ordered pair)]
==========================================================================
Base case: secure (no violation before any outage).
Lose one element; if a line or boundary is then over its limit, redispatch the fewest MW that relieve it
(DC model, island by island); then lose a second.
Outage (first => second)        Status        MaxLoad  WorstBnd  Vmin   Vmax
--------------------------------------------------------------------------
L1-2 N-S Scotland => L2-3 B6 ...  ISLANDED      islanded
L1-2 N-S Scotland => L3-4 N E...  ISLANDED      islanded
...
T10 GSP SW & S Wales (tx) => ...  ISLANDED      islanded
T10 GSP SW & S Wales (tx) => ...  ISLANDED      islanded
T10 GSP SW & S Wales (tx) => ...  ISLANDED      islanded
L7-8 E Anglia-London => L8-9 ...  VIOLATIONx1     112%      68%  0.998  1.040
L8-9 London-South => L7-8 E A...  VIOLATIONx1     112%      68%  0.998  1.040
L6-8 Midlands-London => L8-9 ...  VIOLATIONx1     112%      68%  0.998  1.040
L8-9 London-South => L6-8 Mid...  VIOLATIONx1     112%      68%  0.998  1.040
L6-8 Midlands-London => L7-8 ...  VIOLATIONx1     111%      68%  0.998  1.040
L7-8 E Anglia-London => L6-8 ...  VIOLATIONx1     111%      68%  0.998  1.040
--------------------------------------------------------------------------
(showing worst 430 of 552)
552 outages screened  122 secure  6 with violations  424 islanded
Verdict: NOT SECURE, 430 insecure outage(s) (exit 3).
[exit 3]
```

All 424 islanded pairs come first (212 pairs, each in both orders). No meshed first outage leaves anything over its
limit, so no redispatch is made and each converged pair is the N-2 pair in both orders: 122 secure and 6 violating
(the three London pairs of the N-2 table, each in both orders).

### trip - lose one generator

```bash
gridsim trip [<case>] [<gen>] [--dir <folder>] [--export <csv>]
```

The flags are in [ch. 19](<0019 CLI reference.md#trip>). `trip` removes one generator's output and its stored rotational energy, integrates the centre-of-inertia swing
equation, and reports the frequency response. It runs no power flow.

```
$ gridsim trip
GridSim  -  contingency on GB reduced (10-zone)
================================================================
Tripped:        Hinkley Point Nuclear  (2880 MW lost, nuclear)
System inertia: 171.7 -> 158.9 GVAs after the trip
Peak RoCoF:     -0.453 Hz/s  (largest |df/dt| over the run)
Nadir:          48.340 Hz  (t+6.9 s)
Settling:       49.144 Hz

50.05 Hz |***+==-:,,..           ....,,,::::-----=========------------::::| 48.34 Hz (nadir)

...at HALF the inertia (79.5 GVAs): RoCoF -0.906 Hz/s, nadir 47.879 Hz
[exit 0]
```

- `Tripped` is the unit and the MW it was producing (`Pg`), with its fuel type.
- `System inertia` is the rotating mass's stored energy, the sum of `H x MBase` over the synchronous machines (a
  converter-type unit adds nothing whatever its `H`), in GVAs, before and after the unit leaves.
- `Peak RoCoF` is the largest rate of change of frequency in the trace, in Hz/s. It is negative for a loss of
  generation. For a single trip without `--forming` or `--asset-dynamics` it is the slope just after the trip.
- `Nadir` is the lowest frequency, with the time after the trip at which it occurs.
- `Settling` is the frequency at the end of the 30 s window. It is not a steady-state value: the governors are still
  recovering at 30 s.
- The line of characters is a trace of up to 64 columns, labelled with 50.05 Hz (the top of the scale) and its nadir, drawn on a
  nine-level ramp `" .,:-=+*#"`.
- The half-inertia line repeats the trip at half the remaining inertia, always on the lumped model, to show how much of
  the response is inertia.
- After it, `trip` assesses the system frequency protection on the run's trajectory: `81U` against the minimum, `81O`
  against the maximum and `81R` against the peak |df/dt|, at the `protection-check` defaults (47.5 Hz, 52.0 Hz, 1.0
  Hz/s). Each line says `holds`, `WOULD OPERATE` or `not evaluated`; `78` (vector shift) is not evaluated, and on a case
  that is not 50 Hz neither are `81U` and `81O`. The lines are information and do not change the exit code.

`<gen>` must come directly after `trip`. It is a case-insensitive substring of a generator name, and the first
generator whose name contains it is tripped. With no name, `trip` takes the unit with the highest dispatch among units that are not on the slack bus and
have an inertia constant above zero. A name directly after `trip` that matches nothing exits 2 and lists the first 20 names; it never falls
back to another unit:

```
$ gridsim trip nosuchunit
error: trip 'nosuchunit' matched no generator in GB reduced (10-zone). Available: North Scotland Hydro, North Scotland Onshore Wind, Torness Nuclear, South Scotland Onshore Wind, Moyle, Heysham Nuclear, North England CCGT, North Sea Link, Drax Biomass, Yorkshire CCGT, Mersey Offshore Wind, North Wales CCGT, EWIC, Midlands CCGT (marginal), East Anglia Offshore Wind, Sizewell B Nuclear, Viking Link, IFA, ElecLink, NemoLink
[exit 2]
```

A case with no eligible unit also exits 2. `--export <csv>` writes the trajectory as `dtm,f` rows, which
`gridsim replay <csv>` plays back. `trip` is the one verb that integrates the distributed multi-domain inertia model
(`--inertia-model distributed`); it also honours `--asset-dynamics` and `--forming` (see
[Global switches](<0019 CLI reference.md#global-switches>)). The run is at the case's own nominal frequency, so the 60 Hz
`case9` starts at 60 Hz. `--gb-services[=<scale>]` adds the GB Dynamic Containment, Moderation and Regulation layer at
the typical volumes, `--band[=<fraction>]` adds the nadir's band over the unfitted swing constants, `--envelope` (with
`--asset-dynamics`) sweeps asset-class control parameters around the trip, and `--dyn-data <file>` takes the machines'
inertia from a dynamic data file; all are in [ch. 19, trip](<0019 CLI reference.md#trip>) and
[ch. 14](<0014 Frequency studies.md>).

On another network:

```bash
gridsim trip --dir data/intl/ireland-all-island
```

```
$ gridsim trip --dir data/intl/ireland-all-island
GridSim  -  contingency on Ireland all-island (EirGrid + SONI) - open transmission model
================================================================
Tripped:        Aghada  (336 MW lost, gas)
System inertia: 27.7 -> 23.4 GVAs after the trip
Peak RoCoF:     -0.358 Hz/s  (largest |df/dt| over the run)
Nadir:          47.641 Hz  (t+12.5 s)
Settling:       48.534 Hz

50.05 Hz |****++==--::,,,....                       .........,,,,,,,,,,,,,| 47.64 Hz (nadir)

...at HALF the inertia (11.7 GVAs): RoCoF -0.717 Hz/s, nadir 46.998 Hz
[exit 0]
```

### screen - rank every credible loss against the frequency limits

```bash
gridsim screen [--dir <folder>] [--top N] [--csv <path>]
```

The flags are in [ch. 19](<0019 CLI reference.md#screen>). `screen` runs the `trip` calculation for every generator with a positive dispatch and ranks the losses by the size of
the initial RoCoF, largest first. It runs no power flow. The inertia is the rotating-mass total, as for `trip`, and a
case with no rotating machine carrying `H` above 0 is refused with exit 1 (the RoCoF of a loss would be the artefact of
a 1 MWs floor). The screen runs at the case's own nominal frequency; on a case that is not 50 Hz a `note:` line says
so and the statutory and LFDD bands are taken about that nominal.

```
$ gridsim screen --top 6
GridSim  -  N-1 RoCoF screening: GB reduced (10-zone)
==========================================================================
System inertia 171.7 GVAs  demand 39.50 GW  limits: RoCoF 0.125 Hz/s, statutory 49.5 Hz
--------------------------------------------------------------------------
Lost infeed                       MW   Inertia(rem)   RoCoF   Nadir   Verdict
--------------------------------------------------------------------------
Midlands CCGT (marginal)         5625    126.7 GVAs  -1.110   46.49  LFDD RISK
East Anglia Offshore Wind        4000    171.7 GVAs  -0.582   47.76  LFDD RISK
Hinkley Point Nuclear            2880    158.9 GVAs  -0.453   48.34  LFDD RISK
Yorkshire CCGT                   2400    151.7 GVAs  -0.396   48.59  LFDD RISK
Heysham Nuclear                  2160    162.1 GVAs  -0.333   48.76  LFDD RISK
South Scotland Onshore Wind      2000    171.7 GVAs  -0.291   48.88  <49.5 Hz
--------------------------------------------------------------------------
28 credible single losses screened  19 breach a limit  worst RoCoF -1.110 Hz/s (Midlands CCGT (marginal))
binding case needs >= 1125.0 GVAs spinning for 0.125 Hz/s (has 126.7, headroom -998.3 GVAs)
Verdict: NOT SECURE, 19 of 28 single loss(es) breach a limit (exit 3).
[exit 3]
```

- `Lost infeed` and `MW` are the generator and its dispatch.
- `Inertia(rem)` is the fleet inertia without that unit, in GVAs.
- `RoCoF` and `Nadir` are as for `trip`.
- `Verdict` is the first that applies: `LFDD RISK` when the nadir is at or below 48.8 Hz, the first stage of low
  frequency demand disconnection; `<49.5 Hz` when the nadir is below the statutory floor; `>0.125 Hz/s` when the RoCoF
  magnitude exceeds 0.125 Hz/s; otherwise `ok`.
- The footer gives the number of losses screened, how many breach a limit, and the worst RoCoF. The last line is the
  spinning inertia the binding case would need to hold 0.125 Hz/s, and the headroom against what the system has. A
  negative headroom means the case cannot hold it.
- The verdict line carries the exit code: exit 3 when any loss breaches a limit, exit 0 (`Verdict: SECURE`) when every
  loss is `ok`.

The header prints the system inertia, the demand and the limits. Every loss is screened and counted whatever `--top`
shows. `--csv` writes every loss with the columns `name,lost_mw,remaining_inertia_gva_s,peak_rocof_hz_per_s,nadir_hz,rocof_breach,statutory_breach,lfdd_risk` (the RoCoF
column is the peak over the run).
The RoCoF limit in the verdict and the CSV is the legacy 0.125 Hz/s relay setting; the technical chapter
gives the modern 1.0 Hz/s setting alongside it.

### Generator and busbar outages, post-fault ratings and the batched sweep

`nminus1` takes four more choices; each is in [ch. 19](<0019 CLI reference.md#nminus1>).

```bash
gridsim nminus1 uk --outage-types branch,gen,bus
gridsim nminus1 uk --outage-types gen --outage-dispatch droop
gridsim nminus1 case9 --post-fault-rating B
gridsim nminus1 case118 --batched-lanes 0
```

- `--outage-types` adds each in-service generating unit (`gen`) and each bus with every branch, unit and load on it
  (`bus`) to the branch outages. The load and generation each outage takes are printed under its row and are reported,
  not judged. When an outage takes the reference bus or its last unit, the reference moves to the largest remaining unit
  in the same connected part, and the row says so.
- `--outage-dispatch` decides who picks up the power a `gen` or `bus` outage takes away: `reference` (the default, the
  reference bus takes it all), `droop` (the remaining synchronous units in proportion to their MBase) or `inertia` (in
  proportion to H x MBase), each unit within its limits.
- `--post-fault-rating B` or `C` judges every outage state against each branch's short-term or emergency rating (rating A
  where a branch has none); the base case stays on rating A.
- `--batched-lanes N` solves the branch outages with the batched contingency sweep on N lanes (0 for one per
  processor). Every number is bit-identical to the default screen; only the speed differs, and a `note:` line on
  standard error says the batched sweep ran.

### nminus1-sweep - the branch sweep, in parallel

```bash
gridsim nminus1-sweep <case|--all> [--parallel N] [--sample N] [--losses] [--global-vlimits] [--out <path>]
```

The flags are in [ch. 19](<0019 CLI reference.md#nminus1-sweep>). The sweep screens every branch of the named case (or of every registered case with `--all`) on a pool of worker threads
and prints one row per case instead of one row per outage. Use `nminus1` for the per-outage detail and the sweep for
coverage and throughput.

```
$ gridsim nminus1-sweep case14 --parallel 2
    GridSim  -  <date> UTC
    ...
    run:     nminus1-sweep  cases=1  parallel=2  sample=full  vlimits=per-bus

| Case | Buses | Branches | Screened | Secure | Violations | Islanded | Not solved | Solves/s | Wall s | Base viol |
|------|------:|---------:|---------:|-------:|-----------:|---------:|-----------:|---------:|-------:|----------:|
| case14 | 14 | 20 | 20 | 16 | 3 | 0 | 1 | 526 | 0.0 | 1 |
[n-1] case14: 14 buses, screening 20 outages on 2 threads...
[n-1] done -> results\nminus1-sweep.md
[n-1] verdict: 1 of 1 case(s) not N-1 secure (exit 3).
[exit 3]
```

The columns are the case, bus and branch counts, outages screened, and the secure, violating, islanded and `Not solved`
counts (`Islanded` counts only the outages that split the network, `Not solved` the connected outages with no solution),
then the throughput in solves per second, the wall time in seconds, and `Base viol`, the number of violations the base case
already has. Those are netted out of every outage as in `nminus1`, so case14's base overvoltage (bus 8 held at its
1.090 pu set-point) is counted once, in `Base viol`, and not on every outage; the counts are those of `nminus1 case14`.
The run exits 3 because case14 is not N-1 secure (a base-case violation, 3 violating outages and 1 that does not solve);
it exits 0 only when every case is secure. `--parallel` sets the worker threads (0 means every logical core).

`--sample N` screens N evenly spaced branches of a large case. The row is marked `(sampled)` and a line on stderr says
so; a sample is never silent:

```
$ gridsim nminus1-sweep case118 --sample 20 --parallel 2
...
| Case | Buses | Branches | Screened | Secure | Violations | Islanded | Not solved | Solves/s | Wall s | Base viol |
|------|------:|---------:|---------:|-------:|-----------:|---------:|-----------:|---------:|-------:|----------:|
| case118 (sampled) | 118 | 186 | 20 | 18 | 1 | 1 | 0 | 211 | 0.1 | 0 |
[n-1] case118: SAMPLING 20/186 branches (--sample 20)
[n-1] case118: 118 buses, screening 20 outages on 2 threads...
[n-1] done -> results\nminus1-sweep.md
[n-1] verdict: 1 of 1 case(s) not N-1 secure (exit 3).
[exit 3]
```

case118 carries no branch ratings, so only voltage violations can appear in its sweep.

`--losses` adds the loss-contingency sweep after each case: the change in total system losses for each outage, with the
20 outages that raise losses most:

```
$ gridsim nminus1-sweep case118 --losses --parallel 4
...
| Case | Buses | Branches | Screened | Secure | Violations | Islanded | Not solved | Solves/s | Wall s | Base viol |
|------|------:|---------:|---------:|-------:|-----------:|---------:|-----------:|---------:|-------:|----------:|
| case118 | 118 | 186 | 186 | 164 | 13 | 9 | 0 | 799 | 0.2 | 0 |

### Loss contingency - case118 (base losses: 132.48 MW)
| Outage | Delta MW | Post MW | Loading % |
|--------|--------:|--------:|----------:|
| 8-5 (tx) | +66.66 | 199.14 | 0.0 |
| 38-37 (tx) | +36.91 | 169.39 | 0.0 |
| 38-65 | +35.00 | 167.48 | 0.0 |
| 26-30 | +33.10 | 165.58 | 0.0 |
| 30-17 (tx) | +17.11 | 149.59 | 0.0 |
| 64-65 | +14.59 | 147.07 | 0.0 |
| 89-92 | +14.02 | 146.50 | 0.0 |
...
| 88-89 | +4.58 | 137.06 | 0.0 |
| 42-49 | +4.44 | 136.92 | 0.0 |
| 42-49 | +4.44 | 136.92 | 0.0 |
| (9 non-converged) | | | |

[n-1] case118: 118 buses, screening 186 outages on 4 threads...
[n-1] case118: running loss contingency sweep...
[n-1] done -> results\nminus1-sweep.md
[n-1] verdict: 1 of 1 case(s) not N-1 secure (exit 3).
[exit 3]
```

The `Loading %` column reads 0.0 because case118 has no ratings. The `42-49` branch appears twice because two parallel
branches carry the same name. The base and outage solves of the loss sweep use the same options as the screen, with
reactive limits on.

The engine follows the shared dense / sparse rule ([ch. 19, Solver selection](<0019 CLI reference.md#solver-selection>)). On
the CPU the two engines give the same bits, so only the speed differs. A large case is screened one sample at a time:

```
$ gridsim nminus1-sweep gb-synthetic --sample 24 --parallel 4
...
| Case | Buses | Branches | Screened | Secure | Violations | Islanded | Not solved | Solves/s | Wall s | Base viol |
|------|------:|---------:|---------:|-------:|-----------:|---------:|-----------:|---------:|-------:|----------:|
| gb-synthetic (sampled) | 2227 | 2268 | 24 | 0 | 0 | 24 | 0 | 5 | 4.9 | 27 |
[n-1] gb-synthetic: SAMPLING 24/2268 branches (--sample 24)
[n-1] gb-synthetic: 2227 buses, screening 24 outages on 4 threads...
[n-1] done -> results\nminus1-sweep.md
[n-1] verdict: 1 of 1 case(s) not N-1 secure (exit 3).
[exit 3]
```

The rows go to `results/nminus1-sweep.md` under the current directory, with a `.csv` sibling and
`nminus1-sweep.run-metadata.json` beside them (the metadata is named after the report); `--out` moves them. The CSV
ends in a `base_violations` column. A case whose
base case does not converge gets a `BASE FAIL` row and counts as not secure. A case that fails to load is reported as a
`LOAD FAIL` row and the others run; the verb exits 1 if none loaded, and also exits 1 after a partial failure (an input
error outranks the security verdict). The sweep, `stress` and `chain` honour `--gpu`; `nminus1`, `nminus2`, `n-1-1` and
`cycle` run on the CPU.

### stress - load to collapse

```bash
gridsim stress <case|--all> [--from F] [--to F] [--step F] [--parallel N] [--out <path>]
```

The flags are in [ch. 19](<0019 CLI reference.md#stress>). `stress` scales the case to each load factor from `--from` to `--to`, solves every factor independently, and reports
the highest factor that converged before the first failure. At each factor every bus load is multiplied by the factor,
and so is every generator except the slack unit, which carries the balance.

```
$ gridsim stress case14
    GridSim  -  2026-10-19 17:39:26 UTC
    code:    git v1.95.294-168-g786a2162  |  GridSim.Core v1.95.462.0  |  build Release
    runtime: .NET 10.0.12  X64  GC=Server
    machine: LAPTOP  Microsoft Windows 10.0.26300
    cpu:     Intel(R) Core(TM) i3-N305  -  8 logical cores  -  8 GB RAM
    simd:    Avx256  (4 doubles/step)  FMA=True
    backend: cpu
    run:     stress  cases=1  load 0.5->2 step 0.05  parallel=8

| Case | Buses | Max loadability (x base) | Vmin at collapse | Notes |
|------|------:|-------------------------:|-----------------:|-------|
| case14 | 14 | 1.75 | 0.714 |  |
[stress] case14: 31 load points on 8 threads...
[stress] done -> results\stress.md
[exit 0]
```

`Max loadability` is the highest converged factor before the first that failed, as a multiple of the base load. `Vmin at
collapse` is the lowest bus voltage at that last converged factor; it is not the voltage at the failing point. The sweep
resolution is `--step`: with 0.05 the answer for case14 is 1.75, and a finer sweep finds the real limit between 1.75 and
1.80:

```
$ gridsim stress case14 --from 1.7 --to 1.8 --step 0.01
...
| Case | Buses | Max loadability (x base) | Vmin at collapse | Notes |
|------|------:|-------------------------:|-----------------:|-------|
| case14 | 14 | 1.77 | 0.669 |  |
[stress] case14: 11 load points on 8 threads...
[stress] done -> results\stress.md
[exit 0]
```

A case that converges at every factor is noted `did not collapse in range`; widen `--to`:

```
$ gridsim stress case118
...
| Case | Buses | Max loadability (x base) | Vmin at collapse | Notes |
|------|------:|-------------------------:|-----------------:|-------|
| case118 | 118 | 2.00 | 0.797 | did not collapse in range |
[stress] case118: 31 load points on 8 threads...
[stress] done -> results\stress.md
[exit 0]
```

Generator reactive limits are enforced and the tap-changer loop runs at every load point (the contingency option set,
see [Before you start](#before-you-start)), so the collapse found is reactive exhaustion where it occurs. A factor counts as
collapsed when Newton-Raphson fails to converge within 100 iterations, which is the solver giving up, not a proof that
no solution exists. `--step` must be positive; `0` exits 2:

```
$ gridsim stress case14 --step 0
error: stress --step must be positive (got 0); the load sweep would never terminate.
[exit 2]
```

### chain - consecutive warm-started solves

```bash
gridsim chain <case> [--steps N] [--mode fixed|ramp|daily] [--ramp-pct P] [--out <path>]
```

The flags are in [ch. 19](<0019 CLI reference.md#chain>). `chain` solves one operating point after another and starts each from the voltages of the previous converged solve. The
`--mode` sets the load factor of each step: `fixed` keeps 1.000; `ramp` adds `--ramp-pct` percent per step
(`1 + ramp/100 x step`); `daily` follows the 24-hour demand curve at `24 x step / steps` hours.

```
$ gridsim chain case118 --mode ramp --steps 6 --ramp-pct 10
...
| Step | Load x | Warm-start iters | Mismatch | Vmin | Vmax |
|-----:|-------:|-----------------:|---------:|-----:|-----:|
| 0 | 1.000 | 6 | 2.85E-014 | 0.943 | 1.050 |
| 1 | 1.100 | 6 | 5.70E-014 | 0.943 | 1.050 |
| 2 | 1.200 | 6 | 1.14E-013 | 0.941 | 1.050 |
| 3 | 1.300 | 8 | 1.05E-009 | 0.933 | 1.050 |
| 4 | 1.400 | 10 | 2.66E-010 | 0.919 | 1.050 |
| 5 | 1.500 | 9 | 5.70E-014 | 0.904 | 1.050 |

Cold (flat-start) iters: 8.  Max warm-start iters over 5 warm-started step(s) (steps 1-5; step 0 starts from the case): 10.  
[chain] done -> results\chain-case118.md
[exit 0]
```

- `Load x` is the load factor of the step.
- `Warm-start iters` is the Newton iteration count, summed over the reactive-limit passes. Step 0 starts from the
  voltages stored in the case, not from a previous solve, so it is not warm.
- `Mismatch` is the largest power mismatch at the end of the solve, in pu.
- `Vmin`, `Vmax` are the extreme bus voltages of the step.
- The last line gives the cold (flat-start) iteration count for comparison and the largest iteration count over the
  warm-started steps (1 onwards; step 0 is left out). In `fixed` mode it adds
  `Warm start converges in <=1 iter with no drift - as expected.` when that maximum is 1 or below.

In `fixed` mode every step after the first re-solves the same point. A case that switches no generator to its reactive
limit converges in 0 iterations, because the convergence test runs before the first linear solve:

```
$ gridsim chain case30 --steps 5
...
| Step | Load x | Warm-start iters | Mismatch | Vmin | Vmax |
|-----:|-------:|-----------------:|---------:|-----:|-----:|
| 0 | 1.000 | 3 | 9.57E-010 | 0.961 | 1.000 |
| 1 | 1.000 | 0 | 9.57E-010 | 0.961 | 1.000 |
| 2 | 1.000 | 0 | 9.57E-010 | 0.961 | 1.000 |
| 3 | 1.000 | 0 | 9.57E-010 | 0.961 | 1.000 |
| 4 | 1.000 | 0 | 9.57E-010 | 0.961 | 1.000 |

Cold (flat-start) iters: 3.  Max warm-start iters over 4 warm-started step(s) (steps 1-4; step 0 starts from the case): 0.  Warm start converges in <=1 iter with no drift - as expected.
[chain] done -> results\chain-case30.md
[exit 0]
```

A case whose generators hit reactive limits, such as case118 or case300, needs 4 or 5 iterations per step even in
`fixed` mode, because a generator bus always restarts at its set-point voltage and the limit switching is found again
on each solve. A step that does not converge shows `NaN` voltages, makes the run exit 3, and does not seed the next step.
The iteration count of a failed step can exceed the cap of 100 because it sums the reactive-limit passes:

```
$ gridsim chain case14 --mode ramp --steps 12 --ramp-pct 20
...
| Step | Load x | Warm-start iters | Mismatch | Vmin | Vmax |
|-----:|-------:|-----------------:|---------:|-----:|-----:|
| 0 | 1.000 | 2 | 1.32E-010 | 1.010 | 1.090 |
| 1 | 1.200 | 8 | 6.47E-012 | 1.006 | 1.090 |
| 2 | 1.400 | 6 | 2.48E-009 | 0.954 | 1.060 |
| 3 | 1.600 | 7 | 9.25E-011 | 0.854 | 1.060 |
| 4 | 1.800 | 104 | 3.39E-002 | NaN | NaN |
| 5 | 2.000 | 104 | 4.16E-001 | NaN | NaN |
| 6 | 2.200 | 104 | 6.49E-001 | NaN | NaN |
| 7 | 2.400 | 104 | 7.41E-001 | NaN | NaN |
| 8 | 2.600 | 104 | 9.63E-001 | NaN | NaN |
| 9 | 2.800 | 104 | 1.30E+000 | NaN | NaN |
| 10 | 3.000 | 104 | 1.49E+000 | NaN | NaN |
| 11 | 3.200 | 105 | 1.68E+000 | NaN | NaN |

Cold (flat-start) iters: 4.  Max warm-start iters over 11 warm-started step(s) (steps 1-11; step 0 starts from the case): 105.  
[chain] done -> results\chain-case14.md
[exit 3]
```

An unknown `--mode` exits 2, and a case that does not load exits 1:

```
$ gridsim chain case14 --mode bogus
error: --mode expected 'fixed', 'ramp' or 'daily' but got 'bogus'
[exit 2]
```

The report goes to `results/chain-<case>.md`, with a `.csv` sibling and `chain-<case>.run-metadata.json`. Adding `--bess` sites turns
`chain` into a battery storage time series; that study is specified in
[ch. 27, Battery energy storage](<../technical/0027 Battery energy storage.md>).

### cycle - the continuous N-1 and analysis cycle

```bash
gridsim cycle <case|--dir <folder>> [--cycles N] [--window-ms MS] [--workers N] [--shards N] [--hold-steady]
```

The flags are in [ch. 19](<0019 CLI reference.md#cycle>). `cycle` is the real-time analysis engine in a loop. Each cycle does one single-threaded base solve of the live
simulator, then fans every analysis out across a worker pool against that solved state. Work comes in two tiers. The
High tier runs to completion every cycle: the operator watch list, N-1 on the hot branches (those loaded at or above
`--hot-loading`, default 80% of rating), and the harmonic penetration study. The Low tier is best effort: the rest of
the branches in `--shards` rotating N-1 shards, the RoCoF screen and the physics audit. `--jobs` selects which analyses
run (`n-1`, `harmonics`, `rocof`, `physics`, `watch`).

```
$ gridsim cycle case118 --cycles 6 --hold-steady
GridSim  -  prioritised parallel analysis cycle: IEEE 118-bus
============================================================================================
case 118 buses / 186 branches   engine Newton-Raphson (dense)
window 1000 ms   workers 7   budget 7.0 core-s/cycle   jobs 13 (3 High / 10 Low)
--------------------------------------------------------------------------------------------
cycle |  base ms | wall ms |  HP   | LP done/adm/def | util% | backlog | note
--------------------------------------------------------------------------------------------
    0 |     76.6 |     252 |  3/3  |  10/10 /0     |   16% |       0 | n-1 shards 8/8
    1 |     18.4 |     141 |  3/3  |  10/10 /0     |   11% |       0 | n-1 shards 8/8
    2 |     17.7 |     199 |  3/3  |  10/10 /0     |   13% |       0 | n-1 shards 8/8
    3 |     22.0 |     185 |  3/3  |  10/10 /0     |   13% |       0 | n-1 shards 8/8
    4 |     16.2 |     130 |  3/3  |  10/10 /0     |    9% |       0 | n-1 shards 8/8
    5 |      7.5 |     115 |  3/3  |  10/10 /0     |    8% |       0 | n-1 shards 8/8
--------------------------------------------------------------------------------------------
6 cycles   avg util 12%   HP met every cycle: YES   N-1 coverage 8/8 shards
[exit 0]
```

The header gives the case and the engine of the base solve, then the window per cycle, the worker count, the compute
budget (`workers x window`, in core-seconds) and the job count. The columns are:

- `cycle`: the cycle number.
- `base ms`: the base solve, on one thread (76.6 ms in the first cycle of this capture against 7.5 to 22.0 ms later).
- `wall ms`: the wall time of the fan-out.
- `HP`: High jobs completed over High jobs scheduled.
- `LP done/adm/def`: Low jobs completed, admitted this cycle, and deferred to a later one.
- `util%`: the share of the compute budget spent.
- `backlog`: the Low jobs still owed a run in the present rotation round.
- `note`: `n-1 shards K/N` counts the shards completed at least once; `HP MISSED` marks a cycle where a High job did not complete.

`case118` has 13 jobs: 3 High (watch list, hot N-1, harmonics) and 10 Low (8 N-1 shards, RoCoF, physics). With
7 workers and a 1,000 ms window the budget is 7,000 core-ms; no cycle spends more than 46% of it, so every Low job is
admitted and every shard completes in the first cycle. `--hold-steady` keeps the operating point constant and warm-starts the base
solve; without it the base solve follows the GB demand curve at one simulated minute per cycle.

Shrink the window and the budget and the scheduler shows its rotation:

```bash
gridsim cycle case118 --cycles 8 --hold-steady --window-ms 150 --workers 2
```

```
$ gridsim cycle case118 --cycles 8 --hold-steady --window-ms 150 --workers 2
GridSim  -  prioritised parallel analysis cycle: IEEE 118-bus
============================================================================================
case 118 buses / 186 branches   engine Newton-Raphson (dense)
window 150 ms   workers 2   budget 0.3 core-s/cycle   jobs 13 (3 High / 10 Low)
--------------------------------------------------------------------------------------------
cycle |  base ms | wall ms |  HP   | LP done/adm/def | util% | backlog | note
--------------------------------------------------------------------------------------------
    0 |     84.8 |     173 |  3/3  |   0/1  /9     |   54% |      10 | n-1 shards 0/8
    1 |     17.0 |      81 |  3/3  |   2/2  /8     |   49% |       8 | n-1 shards 2/8
    2 |     18.1 |      83 |  3/3  |   2/2  /8     |   52% |       8 | n-1 shards 4/8
    3 |     16.0 |      90 |  3/3  |   2/2  /8     |   53% |       8 | n-1 shards 6/8
    4 |     26.9 |      77 |  3/3  |   4/4  /6     |   45% |       6 | n-1 shards 8/8
    5 |      7.5 |     127 |  3/3  |   3/3  /7     |   78% |       7 | n-1 shards 8/8
    6 |      6.8 |      99 |  3/3  |   3/3  /7     |   50% |       7 | n-1 shards 8/8
    7 |      6.5 |     154 |  3/3  |   6/6  /4     |   88% |       4 | n-1 shards 8/8
--------------------------------------------------------------------------------------------
8 cycles   avg util 59%   HP met every cycle: YES   N-1 coverage 8/8 shards
[exit 0]
```

The budget is 2 workers x 150 ms = 300 core-ms. Each cycle admits as many Low jobs as fit and at least one. A shard cut
off by the window keeps the outages it has screened and resumes from the next one in the following cycle, so the
coverage count climbs (0, 0, 2, 2, 4, 4, 6, 6 shards) although five of the eight cycles complete no shard. High work is never
cut off, so a cycle that overruns shows `util%` above 100 and still meets `HP 3/3`. The run exits 1 if any High job
is ever missed. It exits 2 for an unknown case name, a positional case that disagrees with `--dir`, or `--pipeline`, and
exits 1 for a missing or unloadable case file or folder.

## Pitfalls

A positional case and `--dir` that name different networks are refused, because the verb cannot know which one was
meant:

```
$ gridsim nminus1 case14 --dir data/uk
error: nminus1: a positional case 'case14' and --dir 'data/uk' were both given and name different networks. Give one of them.
[exit 2]
```

- **`trip` with one free token.** `gridsim trip case14` loads case14 and trips its largest eligible unit; case14 has no
  inertia data, so it exits 2 with `found no eligible generator`. `gridsim trip Torness` trips Torness on the default
  model. Write `gridsim trip <case> <gen>` to name both.
- **A missing `--dir` folder exits 1** with `error: <verb>: case folder not found: '<folder>' (full path ...)`, before
  any report file is opened. A `--dir` with no value exits 2.
- **`ISLANDED` and `NO SOLUTION` are different findings.** `ISLANDED` means the outage split the network; `NO SOLUTION`
  means a connected network with no solution. A `solver error:` row is a solver exception, not a verdict.
- **`base only` is not violation-free.** A violation the base case already has is printed once and not counted against
  any outage, whether the outage leaves it as it was or makes it worse; read `Vmin` and `Vmax` on `base only` rows (the
  Irish model's `TX15943` row takes the highest voltage from 1.126 to 1.173 pu). The verdict still fails, because the
  base case violates.
- **A non-zero exit is the verdict, not a crash.** `nminus1`, `nminus2`, `n-1-1`, `nminus1-sweep` and `screen` exit 3
  when anything is not secure. A script that only wants the table can ignore exit 3; one that gates on security reads it.
- **`stress --from` above `--to` is refused** with exit 2, because the load range is empty.
- **The `chain` footer `Warm start converges in <=1 iter with no drift - as expected.` is absent on a case with
  reactive-limit switching** (case118, case300), where every warm step takes 4 to 5 iterations. Its absence there does not
  mean a fault: check that the mismatch column does not drift.
- **Each report has its own metadata.** `nminus1-sweep`, `stress` and `chain` write `<report>.run-metadata.json` next to
  their report, so reports sharing a folder keep their own; a re-run with the same `--out` replaces both.
- **`screen` and `trip` nadir and settling values are model output, not a forecast.** The governor, damping and
  responsive-capacity constants are literature-typical values that have not been fitted to a measured trip. The initial
  RoCoF depends only on the inertia and the lost infeed.

## Limits

- A bare `.m` file name that matches two files in the `data/matpower` subfolders is refused with exit 2 and a list of
  the candidates; give the relative path. No two shipped files share a name today.
- A violation present in the base case is netted by element: a pre-existing violation that an outage makes worse is not
  counted against that outage.
- The screens do not elect a slack bus for an island that holds generation, so every split reads `ISLANDED`.
- The N-1-1 corrective action works in the DC model (reactance only, 1 pu voltages): it holds rated lines and limited
  boundaries, not voltages, and minimises the MW moved, not the cost. The slack unit's room to move is measured from its
  DC-balanced output (its stated output plus its island's imbalance, which the slack absorbs), not from its stated
  output; the DC model is lossless, so AC losses are not in that figure.
- `nminus1 --outage-types gen,bus` adds single generating units and busbar sections (a bus with everything on it);
  `nminus2`, `n-1-1` and the sweep screen branches only, and there is no common-mode outage.
- `stress` scales load and dispatch uniformly and treats a failed Newton-Raphson solve as collapse; it does not trace the
  nose of the PV curve.
- `screen` and `trip` use the legacy 0.125 Hz/s RoCoF limit in their verdicts, and the governor, damping and responsive
  capacity constants behind the nadir are not fitted to a measured trip.
- `cycle --pipeline` exits 2 because the pipelined backlog drain is not implemented (R096).

## See also

- [Contingency and security analysis (N-k)](<../technical/0023 Contingency and security analysis (N-k).md>) - the outage
  application, classification, pruning, redispatch, warm start, scheduler and invariants behind these verbs
- [ch. 19, CLI reference](<0019 CLI reference.md#trip>) - `trip`; also
  [screen](<0019 CLI reference.md#screen>), [nminus1](<0019 CLI reference.md#nminus1>), [nminus2](<0019 CLI reference.md#nminus2>),
  [n-1-1](<0019 CLI reference.md#n-1-1>), [nminus1-sweep](<0019 CLI reference.md#nminus1-sweep>),
  [stress](<0019 CLI reference.md#stress>), [chain](<0019 CLI reference.md#chain>) and [cycle](<0019 CLI reference.md#cycle>)
- [ch. 5, Running power flows](<0005 Running power flows.md>) - the solve each outage repeats, and the base report
- [ch. 14, Frequency studies](<0014 Frequency studies.md>) - `freq`, `transient` and the live frequency view
- [ch. 2, Loading networks](<0002 Loading networks.md>) - case names and case directories
- [ch. 8, The desktop app](<0008 The desktop app.md>) - the right-click trip and the CYCLE tab
