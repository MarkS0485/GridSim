# 23. Contingency and security analysis (N-k)

This chapter specifies the contingency layer: how a branch outage is applied and classified, how the N-1, N-2 and N-1-1
screens enumerate and rank outages, how the corrective redispatch of N-1-1 works, how violations are netted against the base case, how `stress`
and `chain` step the operating point, how the frequency verbs `trip` and `screen` judge a generator loss, and how
`cycle` schedules continuous N-1 inside a time window. The outage engine is
`src/GridSim.Core/Analysis/ContingencyAnalysis.cs`; the frequency screen is
`src/GridSim.Core/Dynamics/RocofScreening.cs`; the scheduler is in `src/GridSim.Core/Scheduling/`; the verbs that drive
them are in `src/GridSim.Cli/Program.cs`. The layer adds no new numerics. Every verdict comes from the Newton-Raphson
solve of [ch. 3, Power flow](<0003 Power flow.md>), so the bit-identity of that chapter carries over: the dense and
sparse engines agree to the last bit, and a parallel screen returns the same converged and secure verdict for every outage as
a sequential one. What this chapter adds is orchestration,
and the invariants that keep the orchestration deterministic.

## Model

### What an outage is

An outage is a set of branch indices. `ContingencyAnalysis.WithoutMany` applies it by rebuilding the branch list:

```
skip      = HashSet(indices)
branches' = [ branch_j  for j = 0 .. n-1  if j not in skip ]          model order kept
model'    = model.CopyWith(branches: branches')
```

`CopyWith` keeps the buses, generators, OLTC records, boundaries, asset dynamics and protections. The buses and
generators are shared with the original model, which is immutable, so there is no defensive copy. A branch
that was already out of service takes part like any other: its index is in the list and its removal changes nothing.
The label of an outage is built from the branches:

```
Label(b)   = (b.Name if non-empty else "<From>-<To>") + (" (tx)" if b.IsTransformer else "")
LabelMany  = Label(b_1) + " + " + Label(b_2) + ...                    N-2, N-k
N-1-1 row  = Label(first) + " => " + Label(second)
```

The outage engine removes branches, and since F20 also generators and buses (`ScreenElementOutage`, below). A generator
loss is also a frequency study (`trip`, `screen`), and a common-mode group is a branch set passed to `ScreenOutage`,
which takes any list of indices.

### Islanding

The Newton engines do not partition the network. `ScreenOutageCore` hands the reduced model straight to the solver, so
a part of the network left without a path to the slack bus has a singular Jacobian, so no Newton step can be solved
([ch. 3](<0003 Power flow.md#the-canonical-newton-step>)) and the engine returns `Converged = false`. It does not
matter whether the separated part holds generation: no slack is elected for it. A non-converged result is classified by
topology: when the outage split the network (the reduced model has more connected parts than the case,
`ContingencyResult.Islanded`), its single violation is `islanded` and the verdict `ISLANDED`; when the network is still
connected, the violation is `no solution` and the verdict `NO SOLUTION` (a voltage collapse, for example). The count lines
add `N not solved` for the second kind. A solver exception is recorded with
its cause, `solver error: <ExceptionType>: <message>`, and `Converged = false`. An `OperationCanceledException` is
rethrown: a cancelled sweep ends as cancelled and never reports its unscreened outages as islanded.

Since N232 (October 2026) the screen does not solve an outage it can prove unsolvable. With a built-in Newton engine
(dense or sparse, bare or in the OLTC loop), `ContingencyAnalysis.ProvablyUnsolvablePart` looks for a connected part
with no reference bus whose real-power balance can never be met: either its demand exceeds its generation by more than
`n tol + 1e-6` pu while every branch R and bus Gs in it is non-negative (its losses cannot be negative), or it is
lossless (every R and Gs zero, a bus cut off on a radial spur being the usual case) and its generation and demand differ
by more than that. The Newton convergence test needs every P mismatch of the part below `tol`, and the P injections of a
part add up to its losses, so neither case can converge; the screen returns the non-converged result at once
(`islanded` when the outage split the network, as before). It used to solve such an outage anyway: the direct LU
reported the singular Jacobian and the ILU0 / BiCGSTAB fallback ran to its `4m + 200` iteration cap, about 6 s per
outage on gb-synthetic. Every other outage is solved exactly as before, so its result is unchanged to the bit
(`Lf2ContainedTests.N232_*` compare every field of every single outage of case9 to case118 against the full solve). A
part the proof does not cover is still solved: one with a reference bus, a voltage-dependent load, a lossy part with a
surplus, or a balanced one. On case14, branch 7-8 cuts off the synchronous condenser at bus 8 (no real power either
way), and the main network still solves, so that outage stays `base only`. A caller's own `IPowerFlowSolver` is always
called.

The screens start from the voltages stored in the case, not from the base-case solution (`PowerFlowOptions.FlatStart` is
false, so the start is the stored `Vm` and `VaDeg`, with generator buses at their set-points). Through the API a null
solver is the raw Newton engine and null options are the `PowerFlowOptions` defaults, as they always were. The CLI verbs
pass the contingency study set instead (see [Solver, options and parallelism per verb](#solver-options-and-parallelism-per-verb)):
`ContingencyAnalysis.StudyOptions` and an engine wrapped in the OLTC loop (`OltcLoopPowerFlow`,
`src/GridSim.Core/PowerFlow/OltcLoopPowerFlow.cs`), so a post-outage state is the tap-regulated steady state. The wrapper
is `SteadyStateSolver.Solve(model, options, 25)` around the given engine; with no OLTC records it returns
`engine.Solve(model, options)` unchanged, bit for bit.

### Classification

The classification reads the converged state of the reduced model:

```
for each LineFlow f of the solved reduced model:
    maxLoad = max(maxLoad, f.LoadingPercent)
    if f.LoadingPercent > 100.0:  violation "branch <f.Name> <f.LoadingPercent:F0>%"
        LoadingPercent = max(|S_from|, |S_to|) / RateAMva * 100        0 when RateAMva = 0

for each boundary b of the model:
    flow       = sum over bb in b.Branches of  bb.Direction * PFromMw(bb.Branch)     branches matched by name
    loading    = |flow| / b.LimitMw * 100            if b.LimitMw > 0, else 0
    overloaded = b.LimitMw > 0 and |flow| > b.LimitMw   ->  violation "boundary <b.Name> <loading:F0>%"
    b.LimitMw negative or not finite:  InvalidDataException
    worstBoundary = max loading over boundaries

minVm, maxVm = min and max of Vm over all buses
if minVm < 0.94:  violation "undervoltage <minVm:F3> p.u."
if maxVm > 1.06:  violation "overvoltage <maxVm:F3> p.u."

Secure = Converged and no violations
```

Per-bus limits (`PowerFlowOptions.PerBusVoltageLimits`): when it is set and any bus carries `VminPu` / `VmaxPu`, the two
voltage lines above become a per-bus test, each bus against `VoltageLimits.OperationalLowFor(bus)` /
`OperationalHighFor(bus)` (its own limit, else 0.94 / 1.06 on that side); the summary text keeps its form with the lowest
voltage among the buses below their own limit and the highest among those above theirs, and the per-bus `Details` (what
netting compares) follow the same test. `MinVm`, `MaxVm` and the stress score are unchanged. The API default is off; the
CLI verbs `nminus1`, `nminus2`, `n-1-1` and `nminus1-sweep` set it by default (every MATPOWER file carries VMIN and VMAX,
so this moves verdicts on the bundled MATPOWER cases), and `--global-vlimits` judges every bus against the band instead
(`--bus-vlimits` is accepted as a no-op alias).

**Post-fault ratings.** `PowerFlowOptions.PostFaultRating` (`RateA`, the default; `RateB`; `RateC`; CLI
`--post-fault-rating A|B|C` on `nminus1`, `nminus2` and `n-1-1`) holds every outage state to the short-term or emergency
rating (RateA where that rating is 0); the base case stays on RateA.

**Generator and bus outages.** `nminus1 --outage-types branch,gen,bus` (`OutageKind`: `Branch`, the default;
`Generator`, one in-service unit, NETS SQSS paragraph 5.1.2; `Bus`, a bus with every branch, unit and load on it, the
bus-branch stand-in for a busbar section, paragraph 5.1.5) adds unit and bus outages. Each result records the load and
generation the outage took and, when the outage took the reference bus or its only unit, that the reference moved to the
largest remaining unit in the same part. `--outage-dispatch` (`OutageDispatch`) decides who picks up the lost
generation: `ReferenceBus` (the default, the PSS/E "disabled" and PowerFactory "as dispatched" mode), `GovernorDroop` (the
remaining synchronous units in proportion to `MBase`, the model carrying no per-unit droop) or `Inertia` (in proportion to
`H x MBase`), each unit within `[Pmin, Pmax]` and the rest left to the reference.

**Batched sweep.** `nminus1 --batched-lanes N` solves the branch outages with the batched contingency sweep
(`ContingencyAnalysis.Batched.cs`, N lanes, 0 for one per processor); every number is bit-identical to the default
screen and a `note:` line on stderr says the batched sweep ran.

`ContingencyAnalysis.OverloadThresholdPercent` is 100.0. The band is `VoltageLimits.OperationalLowPu` 0.94 and
`OperationalHighPu` 1.06 (`src/GridSim.Core/Analysis/VoltageLimits.cs`), the table shared with the SCADA alarm band and
the watch list. A boundary branch is looked up by name in the flows of the reduced model, so a removed branch contributes
no flow; two branches that share a name resolve to the later one. A boundary whose `LimitMw` is 0 has no limit authored:
its flow is evaluated but its loading is 0 and it is never over limit. A non-converged result carries zeros in every
numeric field and the single violation `islanded` or `no solution`. Loading is the larger end flow over the rating in
force (`RateAMva`, or the post-fault rating for an outage state),
so a branch with no rating loads 0% and never violates. `case14`, `case57`, `case118` and `case300` carry no ratings, and so do
the `data/matpower/dist` feeders `case18`, `case22`, `case33bw`, `case69` and `case85`. `case9`, `case30`, `case24`, `cigre`,
`case533mt_hi` and every `pglib_opf_*` case rate every branch; `case145` rates 58 of 453, `case1354pegase` 1,432 of 1,991,
`case2869pegase` 2,743 of 4,582 and `case9241pegase` 6,295 of 16,049. The count in `VIOLATIONxN` is
the length of the violation list: one entry per overloaded branch, one per overloaded boundary, at most one undervoltage
and at most one overvoltage, however many buses breach.

Alongside the text list, every converged result carries `Details`, one `ContingencyViolation(Kind, Element, Value, Name)`
per violating element: `BranchOverload` keyed by the branch's index in the ORIGINAL model (the reduced model's branch `j`
is mapped back through the kept-index list, so an N-2 overload is keyed like the base case's), `BoundaryOverload` keyed by
the boundary's index in `model.Boundaries`, and `Undervoltage` / `Overvoltage` keyed by bus id, one per bus outside the
band. A non-converged result has none.

### Netting against the base case

`ScreenBaseCase(model, solver, options)` classifies the intact network (no branch removed) with the same solver and
options, labelled `base case`. `NetAgainstBase(outage, baseCase)` then removes the base case's own violations:

```
if not baseCase.Converged:  InvalidOperationException          nothing to net against
if not outage.Converged:    return outage (flag set)            an islanding is never netted away
baseKeys  = { (Kind, Element) of baseCase.Details }
fresh     = outage.Details whose key is not in baseKeys        in order
pre       = outage.Details whose key is in baseKeys
Violations            = Texts(fresh)        PreExistingViolations = Texts(pre)        NettedAgainstBase = true
Texts(d)  = one "branch <Name> <Value:F0>%" per branch entry, one "boundary <Name> <Value:F0>%" per boundary entry,
            then "undervoltage <min Value:F3> p.u." if any, then "overvoltage <max Value:F3> p.u." if any
```

The numeric fields are not changed. With a base case that has no violations, `Texts(fresh)` is the raw list exactly
(`ContingencyCorrectnessTests.Netting_against_a_clean_base_returns_the_raw_violations_exactly`, on case300). The key is
the element, not the size of its violation: a bus already above 1.06 pu that an outage pushes higher stays pre-existing.
The CLI prints the base case once, nets every outage, marks a netted secure row that still carries base-case violations
`base only`, and fails the verdict when the base case has any violation. Before October 2026 (D03) the violations were
absolute, so a network already outside the band reported that violation on every outage (case14: 0 of 20 outages
secure; Ireland: 2 of 82).

### Stress score

`StressScore` is the ranking key of the pruned N-2 and N-1-1 candidate sets and the ordering key of the N-2 and N-1-1
tables:

```
StressScore(c) = +Infinity                                                       if not c.Converged
               = c.MaxBranchLoadingPercent + c.WorstBoundaryLoadingPercent
                 + 1000 * max(0, 0.94 - c.MinVm) + 1000 * max(0, c.MaxVm - 1.06)  otherwise
```

Higher is worse. The weight `ContingencyAnalysis.StressVoltageWeightPerPu` is 1000 score points per pu outside the
band, so a 0.01 pu excursion costs as much as 10 points of loading. The converged score keys on raw loading, not on
violations, so an outage that is heavily loaded but legal ranks above a relaxed one: that is the outage worth pairing,
because a second loss on a stressed network is where cascades begin. Every non-converged outage scores `+Infinity` and
ties with every other, so in a table they come first, in enumeration order. The candidate ranking does not use them (see
[N-2](#n-2-pruning-and-enumeration-counts)).

### Frequency screens: `trip` and `screen`

`trip` and `screen` run no power flow. They read the generators and the demand of the loaded model and integrate the
centre-of-inertia swing equation of `FrequencyDynamics.Simulate`
(`src/GridSim.Core/Dynamics/FrequencyDynamics.cs`, specified in
[ch. 5, Frequency dynamics](<0005 Frequency dynamics.md>)). With the default `DynamicsConfig` the terms are:

```
E       = max(1, sum(H*MBase) - H*MBase of the lost unit)        MW.s
D       = DampingPctPerHz / 100 * demand                          MW per Hz      demand = sum of bus Pd
Presp   = ResponsiveFraction * demand                             MW
dt = 0.02 s;  steps = round(30 / dt) = 1500;  samples k = 0 .. 1500;  t = k*dt
    df      = f - f0                                              f0 = 50 Hz, f starts at f0
    pEvent  = -Pg of the lost unit   once t >= 1.0 s, else 0
    pGovT   = -(df / f0) / DroopPu * Presp
    pGov   += (pGovT - pGov) / GovernorTimeConstantS * dt
    pImb    = pEvent + pGov - D*df
    rocof   = f0 / (2*E) * pImb
    f      += rocof * dt
InitialRoCoF = the rocof sample of largest magnitude        Nadir = lowest f        Settling = f after the last step
```

The governor and device terms start at zero, so the first sample after the event is `rocof = -f0 * Pg / (2*E)`. For a
single loss the peak magnitude is that sample, because the response only reduces the imbalance afterwards. `trip` removes the lost unit's
`H*MBase` from the fleet energy and its `Pg` from the supply, then runs the equation once, and runs it again with `E`
halved on the lumped model. With `--inertia-model distributed` the first run is the multi-domain model on the post-trip
network; with `--asset-dynamics` and an authored fleet it is `AssetFrequencyDynamics.Simulate`. The unit is the first
generator whose name contains the argument (case-insensitive), or with no argument the generator with the highest `Pg`
among those not on the slack bus whose `H` is above zero.

`screen` (`RocofScreening.Screen`) runs the same integration for every generator with `Pg > 0`, each with its own `H*MBase`
removed, and sorts by `|InitialRoCoF|`, largest first. The verdict follows the first rule that holds, from the
flags of `FrequencyLimits.Assess`:

```
LFDD RISK     nadir <= 48.8 Hz        FrequencyLimits.LfddFirstStageHz
<49.5 Hz      nadir <  49.5 Hz        FrequencyLimits.StatutoryLowHz
>0.125 Hz/s   |rocof| > 0.125 + 1e-9  FrequencyLimits.RocofLimitLegacyHzPerS
ok            otherwise
```

The footer's inertia floor inverts the swing equation for the binding (first-ranked) loss, using
`InertiaEstimator.EstimateInertiaGvaS`:

```
floor_GVAs = f0 * LostMw / (2 * 0.125) / 1000          headroom = Inertia(rem) - floor
```

`FrequencyLimits` also defines the modern relay setting, `RocofLimitModernHzPerS` 1.0 Hz/s, and the settable
`ActiveRocofLimitHzPerS` (default 1.0). `RocofScreeningResult` carries the legacy, modern and active flags; the CLI verdict
and the CSV column `rocof_breach` use the legacy flag.

## Algorithm

### Solver, options and parallelism per verb

The contingency study set (October 2026, D04) is `ContingencyAnalysis.StudyOptions`: `MaxIterations` 100
(`StudyMaxIterations`), `EnforceQLimits` true, every other field at its default (tolerance `1e-8`, 12 Q passes, one-way
switching, no flat start), solved through `ContingencyAnalysis.WithOltcLoop(engine)` (the OLTC loop, at most 25 passes).
The grounds: NETS SQSS v2.10 (8 April 2025) section 6.2 requires the post-fault steady-state voltages "without exceeding
the available reactive capability of generation" and judges them "after the operation of Grid Supply Transformer
tap-changers"; 100 iterations is the cap of the default case solve.

| Verb | Engine | Power-flow options | Parallelism |
|---|---|---|---|
| `nminus1` | `WithOltcLoop(backend.NewtonSolver(model))`: the shared rule on the CPU or the `--gpu` backend (GR-213; on the CPU exactly `ContingencyAnalysis.CreateStudySolver`). `--batched-lanes` branch outages run on the CPU batched solver, with a note when a GPU is selected | study set | none: `ScreenBranches` runs the outages in sequence |
| `nminus2` | as `nminus1`, one engine per worker | study set | `Parallel.For` over the N-1 pre-screen and over the pairs, `Environment.ProcessorCount` threads |
| `n-1-1` | as `nminus2`; the redispatch is a small DC LP | study set | pre-screen and pair solves parallel; the LPs sequential |
| `nminus1-sweep` | `WithOltcLoop(backend.NewtonSolver(m))`: the shared rule on the CPU or the `--gpu` backend | study set | `Parallel.For` over branches, `--parallel` threads (0 = logical cores) |
| `stress` | `WithOltcLoop(backend.NewtonSolver(model))` | study set | `Parallel.For` over load factors, `--parallel` threads |
| `chain` | `WithOltcLoop(backend.NewtonSolver(model))` | study set | none: each step needs the previous |
| `cycle` base solve | `LiveSim`: sparse above 1,500 buses; with the OLTC loop on a dense model | `LiveSim` options | one thread |
| `cycle` jobs | `SolverSelector.Resolve(model)` once: radial sweep, sparse above 200 buses, else dense | defaults | `--workers` threads across jobs, one thread inside each job |
| `trip`, `screen` | no power flow | - | none |

Before D04: `nminus1`, `nminus2` and `n-1-1` used the defaults (30 iterations), the sweep 100 iterations with Q-limits
off, `stress` and `chain` 100 with Q-limits on, and none of them ran the OLTC loop. `chain --bess` (the storage time
series) joined the set in N084 (October 2026): `StudyOptions` (the same values its own default already had) and the OLTC
loop, so on GB reduced its step voltages now match plain `chain` (step 0 of a one-site storage day: Vmin
1.0313969238603367 -> 0.9977947297199731 pu); on a case without tap changers it is unchanged to the bit. The shared rule of `SolverSelector` is 200 buses (`DefaultSparseThreshold`); the sweep's
own `m.BusCount > 1000` literal was removed in 1.95.105. The threshold only changes speed and memory: on the CPU the dense
and sparse engines return the same bits ([ch. 3](<0003 Power flow.md#dense-and-sparse-how-bit-identity-is-achieved>)),
pinned for the sweep under the study set and the old sweep set on case57, case118 and case300
(`ContingencyCorrectnessTests.Sweep_dense_and_sparse_engines_are_bit_identical_under_the_study_set_and_the_old_sweep_set`).
The sweep, `stress` and `chain` take the linear-solver backend selected by `--gpu`; the screens and the cycle jobs always
run on the CPU.

### N-1

`ScreenBranches` outages branch `i` for every `i` in turn and returns the results in branch order. The CLI table sorts
them: islanded first, then by violation count, largest first, stable on model order. `ScreenBranchesParallel`
(`Func<IPowerFlowSolver>` factory, `maxDegreeOfParallelism`, optional `branchIndices`, `CancellationToken`) runs the same
screen with `Parallel.For` over the index list. A worker takes its solver from a `ThreadLocal`, writes its result to
`results[k]`, and the list returns in the order of the index list whatever the completion order. The sweep's `--sample N`
builds the index list as `{ k * nb / N : k = 0 .. N-1 }`, distinct values, so 20 of 186 branches are indices 0, 9, 18, ...

### N-2: pruning and enumeration counts

`ScreenNMinusK(model, k, ...)` is the general screen. `k < 1` throws, `k > n` returns an empty list, and `k = 1` is
`ScreenBranchesParallel`. For `k >= 2`:

```
candidates = all branches                                               if exhaustive (excluded = none)
otherwise:
  solvable   = the branches whose single outage converged, in index order
  excluded   = the single outages that did not converge, in index order
  candidates = the first min(max(candidateCount, k), |solvable|) of
               solvable.OrderByDescending(StressScore of the single outage)   stable: ties in index order
combos     = every k-subset of candidates, from Combinations() in ascending candidate order
results[c] = ScreenOutageCore(model, combos[c])                         Parallel.For, fixed slot c
return (results.OrderByDescending(StressScore), excluded)               NkScreenReport; stable: ties in enumeration order
```

`ScreenNMinusKReport` returns both lists; `ScreenNMinusK` and `ScreenNMinus2` return the first.

`ScreenNMinus2` is `k = 2`: unordered pairs, because a simultaneous double outage has no order. The cost:

| Screen | Power flows solved |
|---|---|
| N-1 | `n` |
| N-2 pruned, `c` candidates, `s` single outages that solve | `n` pre-screen + `C(min(c, s), 2)` |
| N-2 exhaustive | `C(n, 2)` |
| N-1-1 pruned | `n` pre-screen + `m` corrective plans + `m(m-1)` ordered pairs, `m = min(c, s)` |
| N-1-1 exhaustive | `n` corrective plans + `n(n-1)` ordered pairs |

A corrective plan is one DC flow evaluation, and an LP only when something is over its limit (see below). For the 24
branches of the reduced GB model (12 single outages solve): N-1 is 24 solves; N-2 with 5 candidates is 24 + 10 = 34; with
the default 20 candidates 24 + 66 = 90; exhaustive 276; N-1-1 with 5 candidates is 24 + 20 solves; exhaustive 552 solves,
with an LP for 2 of the 24 first outages (`T2` and `T10`). The pruned pairwise cost is bounded by `c`, whatever `n`; only
the pre-screen grows with the network. On gb-full, 5,918 branches give 17,508,403 unordered pairs, which the prune
replaces by at most 5,918 + 190 solves.

A single outage that does not solve already fails at N-1, so pairing it adds no information; it is reported apart
(`ExcludedSingleOutages`) and the CLI lists it under the table and counts it against the verdict. Before October 2026
(D02) the ranking included them: every `+Infinity` outage tied and kept index order, so the candidate set was first the
islanding outages, and on GB reduced with 5 candidates all 10 pairs islanded. When fewer than `k` single outages solve,
there is no combination to screen. With pruning the pair count above becomes `C(min(c, s), 2)` for `s` solvable single
outages (GB reduced: `s` = 12, so 20 candidates give 66 pairs, not 190).

### N-1-1: lose, redispatch, lose again

```
candidates = as N-2 with k = 2 (solvable single outages only; excluded reported apart)
for each first outage A in candidates, in order:
    postFirst  = WithoutMany(model, [A])
    corrected  = correctiveAction(postFirst)                  default RedispatchToRelieve
    withA[A]   = Dispatcher.Clone(model, generators: corrected.Generators)       full topology, new dispatch
pairs = every ordered (A, B), A != B, over candidate positions
result(A, B) = ScreenOutageCore(withA[A], [A, B])  relabelled "Label(A) => Label(B)"     Parallel.For
return results.OrderByDescending(StressScore)
```

The redispatch is decided after the first loss, so `A => B` and `B => A` are separate scenarios. The corrective
dispatch is computed once per first outage, sequentially, and reused for every second. `correctiveAction` is a public
delegate (`Func<GridModel, GridModel>`), so a caller can model a different operator policy or none; the CLI always passes
the default.

**The corrective redispatch** (`RedispatchToRelieve(model)` = `PlanCorrectiveRedispatch(model).Model`; October 2026,
D01 and N069) is what an operator does after the first loss, in the DC model:

```
df       = DistributionFactors.Build(model)       throws -> outcome NoDcModel, model unchanged
F        = DC branch flows of the PRESENT dispatch (each island's reference absorbs its imbalance)
monitor  = every in-service line with RateAMva > 0 (limit RateA, read as MW), in branch order,
           then every boundary with LimitMw > 0 (flow = sum of Direction * F of its branches, matched by name,
           a later branch of the same name winning; the screen refuses a model whose boundary names do not
           each name exactly one branch, N138), in boundary order
over     = monitored elements with |flow| > limit + 1e-3 MW           (RedispatchLimitToleranceMw)
if over is empty:  outcome NotNeeded, the same model object, 0 MW moved
active   = over
repeat:
    LP over up_i in [0, max(0, Pmax_i - Pg_i)], dn_i in [0, max(0, Pg_i - Pmin_i)], s_e in [0, 2 L_e]:
        minimise    sum_i (up_i + dn_i)                                   MW moved
        subject to  sum over island k of (up_i - dn_i) = 0                one row per DC island holding a generator
                    sum_i a_e,i (up_i - dn_i) + s_e = L_e - F_e           one row per active element e that some
                                                                          generator can move (a_e,i not all zero)
        a_e,i = sum over e's branches of weight * PTDF(branch, bus of generator i)
    not Optimal:  outcome Infeasible, model unchanged
    Pg' = Pg + up - dn;  F' = DC flows of Pg'
    add every monitored element not yet active with |flow'| > limit + 1e-3 MW;  none added: stop
outcome Applied: every generator's Pg set to Pg'; MovedMw = sum_i |Pg'_i - Pg_i|
```

The flow row holds `F_e + sum a (up - dn) = L_e - s_e` inside `[-L_e, +L_e]`. Constraint rows are generated lazily, so
the LP starts small; each round adds at least one element, so the loop ends. An element no generator can influence
(every sensitivity exactly zero, for example a line in an island with no generator) cannot be relieved by redispatch and
is left out of the LP. Keeping each island's total generation leaves each island's balancing machine where it was. The
reference unit's bounds are measured from its DC-balanced output, not its stated `Pg` (N083, October 2026): for island k,
the first generator in model order at the island's reference bus is taken to produce its stated `Pg` plus
`imbalance_k = (island demand) - (sum of the island's stated Pg)`, which is what the reference absorbs in the DC flows
(the rule MATPOWER's `pfsoln` uses for the slack unit's output), so its room is `[0, Pmax - Pg_op]` up and
`[0, Pg_op - Pmin]` down. Only the bounds use `Pg_op`: the adopted dispatch is the stated `Pg` plus the LP's change, so a
unit the LP leaves alone keeps its stated `Pg` to the bit, and the reference unit's own movement changes no flow, because
its bus is its island's reference. Before N083 the stated `Pg` was used, which could offer the slack room it did not have
(a slack stated at 100 MW that really produces 0 MW was allowed to fall 100 MW) or deny it room it had (a slack stated
at 0 MW that really supplies the load could not fall at all). On every bundled case the redispatch is unchanged to the
bit (the reference unit's bounds never bind there). `Pmin` is used as given; a non-finite `Pg` or `Pmin`, or `Pmin` above `Pmax`, throws
`InvalidDataException`. The LP is the dense `BoundedSimplex` (`src/GridSim.Core/Numerics/BoundedSimplex.cs`, tolerance
1e-9, 20,000 iterations); every step is sequential and pure, so the plan is deterministic.

`PTDF(l, k)` is the DC sensitivity of line `l` to a unit injection at bus `k`, withdrawn at the reference bus of `k`'s own
island (`DistributionFactors`, `src/GridSim.Core/Analysis/DistributionFactors.cs`). It uses only the series reactance
(`b_l = 1/X_l`), so resistance, charging, taps and phase shifts are ignored and every voltage is 1 pu. Each island's
reference is its first `Slack` bus, else the bus of its largest generator by `|Pg|`, else its first bus (the rule of
`IslandPartition`, [ch. 3](<0003 Power flow.md#islands>)). An in-service zero-reactance branch that is the only link
between two islands makes `Build` throw (outcome `NoDcModel`).

On the reduced GB model no meshed first outage leaves anything over its limit, so every such pair is the N-2 pair. Of the
24 first outages only `T2 GSP S Scotland` (DC flow on `L2-3 B6 boundary` over its rating; 250 MW moved) and
`T10 GSP SW & S Wales` (the `SW Peninsula & S Wales (export)` boundary over its limit; 510 MW moved) are redispatched,
and both island at the AC solve that follows.

**What it replaced.** Before D01 the default corrective action was a full security-constrained DC-OPF
(`DcOptimalPowerFlow.Solve(model, enforceLineLimits: true)`, specified in
[ch. 29](<0029 OPF, CPF, taps, distribution factors and loss allocation.md>)): it re-dispatched every generator at the
indicative fuel costs, held only rated lines, ran whether or not anything was overloaded, and left a network of more
than one DC island unchanged. On GB reduced it moved 31,810 to 32,380 MW after every meshed first outage and carried
`B4 Upper North (Scotland)` from 48.435617734804836 % of its 3,200 MW limit to 115.62311773480394 % before any second
outage, so every converged exhaustive N-1-1 pair violated (0 secure, 128 violating, 424 islanded; now 122 / 6 / 424).

**What it constrains.** Generator limits, each island's generation total, the thermal limit of every rated line and the
transfer limit of every limited boundary, in the DC model. It does not constrain bus voltages or losses, and it minimises
the MW moved, not the cost.

### Stress: load to collapse

```
factors  f_0 = from;  f_{k+1} = f_k + step  while f <= to + 1e-9;   each rounded to 4 decimals
for each factor f_i, independently and in parallel:
    m = GridScenario.AtLoad(base, f_i)
    r = WithOltcLoop(NewtonSolver(base)).Solve(m, StudyOptions)       fresh engine per point; 100 iterations, Q-limits on
    converged[i] = r.Converged   (an exception counts as not converged)
    vmin[i]      = min(r.Vm)     if converged, else NaN
lastOk = the largest i for which converged[0 .. i] are all true         the contiguous converged prefix
Max loadability = f_lastOk  ("<none>" if lastOk < 0);  Vmin at collapse = vmin[lastOk]
Note "did not collapse in range" when lastOk = count - 1
```

`GridScenario.AtLoad` (`src/GridSim.Core/Simulation/GridScenario.cs`) scales the model:

```
Pd_i' = f * Pd_i,  Qd_i' = f * Qd_i                      every bus
Pg_g' = f * Pg_g                                         every generator not on the first slack bus
slack plant Pg = max(0, 1.02 * sum(Pd') - sum(other Pg'))        Dispatcher.Balance, lossAllowance 0.02
```

The slack unit's `Pg` is a reported figure: the solver has no active-power equation at the slack bus, which supplies
whatever the network needs. Every factor starts from the voltages stored in the case, with no warm start, so the result
does not depend on the order of the factors. A factor after the first failure is ignored even if it converges: the answer
is the end of the contiguous prefix. A failure means Newton-Raphson did not converge in 100 iterations, which is the
solver giving up and not a proof that no solution exists. The reactive-limit loop makes the collapse that of reactive
exhaustion at the generators. `--step <= 0` exits 2 before any file is opened, because the factor loop would not
terminate.

### Chain: warm-start mechanics

```
solver = WithOltcLoop(NewtonSolver(base));  opt = StudyOptions               100 iterations, Q-limits on, OLTC loop
cold = solver.Solve(base, opt with { FlatStart = true }).Iterations
prev = none
for s = 0 .. steps-1:
    factor_s = 1                                    mode fixed
             = 1 + rampPct / 100 * s                mode ramp
             = DailyFactor((24 * s / steps) mod 24) mode daily
    m = GridScenario.AtLoad(base, factor_s)
    if prev != none:  m = GridScenario.WithSolvedVoltages(m, prev)
    r = solver.Solve(m, opt)
    if r.Converged: prev = r    else: exit code 3 (the failed state never seeds the next step)
```

`WithSolvedVoltages` copies `Vm` and `VaDeg` of each bus from the previous result by bus id; the model is otherwise
unchanged. Each step scales the base model by its own factor and does not compound. The daily curve is

```
DailyFactor(h) = 0.60 + 0.40 * (0.5 - 0.5 * cos(2*pi * ((h - 4 + 24) mod 24) / 24))        trough 0.60 at 04:00, peak 1.00 at 16:00
```

Step 0 is not warm: it starts from the voltages stored in the case. The mechanics of the iteration count follow from ch. 3.
The convergence test runs before the first linear solve, so a start that already meets `1e-8` pu reports 0 iterations. In
`fixed` mode the model of step `s >= 1` is the model of step 0 with the solved voltages, so on a case with no
reactive-limit switch the first mismatch is below tolerance and the step costs 0 iterations. A generator bus always
restarts at its set-point voltage (`VoltageSetpoint` in the start table of ch. 3), and the PV-to-PQ switching is found again on
every solve, so a case that switches a generator to its limit pays for the passes again: case118 and case300 need 5 and 4
iterations per fixed step with Q-limits on, and a measured 0 with Q-limits off. `ChainAndScenarioTests` asserts at most one
iteration for the Q-limits-off form, on case14 and case118 only.
A diverged step reports an iteration count that sums the reactive-limit passes and can exceed the cap of 100. The footer
`Warm start converges in <=1 iter with no drift - as expected.` is written only when `mode == fixed` and the maximum
iteration count over every step, step 0 included, is at most 1. With `--bess` the verb runs the storage time series
(`StorageCli`) instead; see [ch. 27, Battery energy storage](<0027 Battery energy storage.md>).

### Cycle: the prioritised parallel analysis cycle

`CycleRunner` (`src/GridSim.Core/Scheduling/CycleRunner.cs`) drives one cycle per call: `LiveSim.Step(tick, 1.0 min)`
makes the base solve on one thread and produces an immutable `SimFrame`; `CycleScheduler.RunCycle` fans the analysis jobs
out against that frame. Cycles run in sequence, because the base solve is single-core and the scheduler's rotation is
stateful. `ResidentCycleService` drives the same scheduler off the caller's thread for the desktop and web hosts; it keeps
only the newest offered frame, so intermediate frames coalesce.

**Jobs** (`AnalysisJobLibrary.Build`; keys `watch`, `n-1`, `harmonics`, `rocof`, `physics`):

| Id | Tier | Work | Seed cost |
|---|---|---|---|
| `watch` | High | `WatchList.Assess` on the frame | 2 ms |
| `n-1:hot` | High | N-1 on the branches whose frame loading is at or above `HotBranchLoadingPercent`; none if none are | 50 ms |
| `harmonics` | High | `HarmonicPenetration.Solve`, a 6-pulse source at 0.5 pu at the largest-demand bus; skipped above `HarmonicsBusLimit` buses | 40 ms |
| `n-1:lp:k` | Low | shard `k` of the branch list | 5 ms per branch |
| `rocof` | Low | `RocofScreening.Screen` on the frame's model | 20 ms |
| `physics` | Low | `PhysicsLaws.Audit` of the solved state | 5 ms |

Every job runs on one worker thread; the re-solving jobs pass `maxDegreeOfParallelism: 1`, because the scheduler owns the
parallelism across jobs and nested fan-out is avoided. The budget of a cycle is `Workers * WindowMs` core-milliseconds
(`CycleBudget`): 7 workers and a 1,000 ms window give 7,000 core-ms.

**Admission and execution** (`CycleScheduler.RunCycle`):

```
reserved = sum over High jobs of Estimate(job);   running = reserved
for job in the rotating Low pool, front to back:                  most-owed first
    est = Estimate(job)
    if running + est > budget.TotalCoreMs and admitted.Count > 0:  break          at least one Low job is admitted
    admitted.Add(job);  running += est
High jobs: all run, deadline ignored, token None
Low jobs : run under a deadline token that fires WindowMs after the cycle starts
Drain    : min(Workers, jobs) workers pull jobs through an atomic cursor (Interlocked.Increment)
Estimate(job) = EWMA of its measured cost if observed, else SeedCoreMs
Observe(id, ms): ewma = ms if first, else CostEwmaAlpha * ms + (1 - CostEwmaAlpha) * ewma        a Cancelled run is not observed
```

A Low worker stops pulling new jobs once the token fires. A running Low job ends when it honours the token: the solver
checks `PowerFlowOptions.Cancellation` at each Newton iteration, and the shard checks it between outages. The outcome of a
job is `Completed`, `Cancelled` (started, aborted at the deadline), `Deferred` (never started) or `Faulted` (threw; the
message is kept and the cycle continues). `HighTierMet` is `HighCompleted == HighTotal`, and the CLI exits 1 if any cycle
misses it. A High job runs to completion even when it overruns the window, so `util%` can pass 100.

**Rotation.** After the cycle, `Completed` and `Faulted` Low jobs move to the back of the pool; `Cancelled` and `Deferred`
jobs keep their place at the front, in their relative order. `BacklogDepth = |Low| - |moved to the back|`. Every Low job is
therefore served in turn and none starves.

**Shards and resume.** The branch list is split into `NMinus1Shards` chunks of `ceil(total / shards)`, one job per chunk
(fewer shards than asked if the chunks run out first). A chunk entry is a `BranchKey(From, To, Occurrence)`: the end buses
and the occurrence number among branches with the same ends, in model order. The key is resolved to a branch index in the
current frame on every run, so a frame that has lost the branches of a dead island does not shift the outages onto the wrong
branches; a key absent from the frame is skipped. A shard screens its outages one at a time and keeps `ShardProgress` (the
next position and the results so far). On cancellation the progress is kept and the next cycle resumes from the next outage;
when the chunk finishes, the shard returns all its results and resets. A fault resets the progress. A lock on the
progress makes the one-run-at-a-time rule a guarantee. A resumed shard's outages were each screened against the frame that
was current when that outage was reached.

## Parallelism and determinism

The parallel paths give the same answer as a sequential run, by these mechanisms.

1. **One engine per worker.** `ScreenBranchesParallel`, `ScreenNMinusK` and `ScreenNMinus1Minus1` take solvers from a
   `ThreadLocal<IPowerFlowSolver>` built by the factory. The engines and `SteadyStateSolver` are single-instance-per-thread
   objects ([ch. 3](<0003 Power flow.md#oltc-outer-loop>)).
2. **Immutable inputs.** `GridModel` is immutable and `WithoutMany` builds a new model, so no worker writes anything another reads.
   The cycle's fan-out never mutates the frame.
3. **Fixed result slots.** Worker `k` writes `results[k]`, never to a shared accumulator, so the completion order cannot reorder
   or race the output.
4. **Stable ordering on a pure key.** The candidate set and the final tables use LINQ `OrderByDescending` on `StressScore`,
   which is stable, so ties fall back to the fixed enumeration order.
5. **No cross-solve state that depends on the thread.** The one cache an engine keeps across solves is the RCM ordering
   of `CpuSparseLuSolver`, a pure function of the Jacobian pattern, so which thread solved which outage cannot change a bit.
6. **Sequential redispatch.** The corrective LP of each first outage runs in a sequential loop, and is deterministic.

The worker count, `--parallel` and `--workers` change throughput only. In `cycle` the verdict (secure or not) of a completed outage is the
same whatever the worker count (`Results_are_independent_of_worker_count`); which outages complete in which cycle depends on
the measured costs and the deadline, so the cycle table is not reproducible run to run. Timings, `solves/s` and wall times
are never reproducible.

## Parameters and defaults

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| Overload threshold | 100.0 | % of `RateA` | A branch above this is a violation | `ContingencyAnalysis.OverloadThresholdPercent` |
| Operational voltage band | 0.94, 1.06 | pu | Outside this is a violation and a stress penalty | `VoltageLimits.OperationalLowPu`, `OperationalHighPu` |
| Stress voltage weight | 1000.0 | score points per pu | Weight of a voltage excursion in `StressScore` | `ContingencyAnalysis.StressVoltageWeightPerPu` |
| Candidate count | 20 | branches | Size of the critical set for N-2 and N-1-1 | `ContingencyAnalysis.DefaultCandidateCount`; CLI `--candidates` |
| Candidate bound | at least `k` (N-2) or 2 (N-1-1), at most the solvable count | branches | Bounds on the candidate count | `Math.Min(Math.Max(candidateCount, minimum), solvable.Count)` in `RankCandidates` |
| Contingency study set | tolerance 1e-8, 100 iterations, Q-limits on, 12 Q passes, OLTC loop (25 passes) | pu, - | `nminus1`, `nminus2`, `n-1-1`, `nminus1-sweep`, `stress`, `chain` | `ContingencyAnalysis.StudyOptions`, `StudyMaxIterations`, `WithOltcLoop` |
| Redispatch limit tolerance | 1e-3 | MW | A monitored line or boundary is over its limit for the corrective redispatch only beyond this | `ContingencyAnalysis.RedispatchLimitToleranceMw` (= `DcOptimalPowerFlow.BindingToleranceMw`) |
| Shared sparse threshold | 200 | buses | Screens, sweep, `stress`, `chain`, cycle jobs | `SolverSelector.DefaultSparseThreshold` |
| Stress sweep | from 0.5, to 2.0, step 0.05 | x base load | Load-factor range; step must be positive | `--from`, `--to`, `--step` |
| Chain | 50 steps, mode `fixed`, ramp 0.5 | steps, -, % per step | Defaults | `--steps`, `--mode`, `--ramp-pct` |
| Daily curve | 0.60 to 1.00, trough 04:00 | x base load | Chain `daily` mode | `LoadProfile.DailyFactor` |
| Frequency model | droop 0.05, damping 1.0, responsive 0.20, governor 8 s | pu, %/Hz, of demand, s | Swing terms of `trip` and `screen` | `DynamicsConfig.DroopPu`, `DampingPctPerHz`, `ResponsiveFraction`, `GovernorTimeConstantS` |
| Frequency window | 0.02 s step, 30 s, event at 1.0 s | s | Integration grid | `DynamicsConfig.StepSeconds`, `DurationSeconds`; `RunTrip` |
| RoCoF limits | legacy 0.125, modern 1.0 (active) | Hz/s | `screen` verdict uses the legacy limit | `FrequencyLimits.RocofLimitLegacyHzPerS`, `RocofLimitModernHzPerS`, `ActiveRocofLimitHzPerS` |
| Frequency floors | statutory 49.5, LFDD stage 1 48.8 | Hz | `screen` verdict | `FrequencyLimits.StatutoryLowHz`, `LfddFirstStageHz` |
| Cycle window | 1000 | ms | Wall-clock deadline of Low work | `CycleOptions.WindowMs`; `--window-ms` |
| Cycle workers | logical cores - 1 (at least 1) | threads | Fan-out width | `CycleOptions.Workers`; `--workers` |
| Cycle shards | 8 | shards | Low-tier N-1 shard count | `CycleOptions.NMinus1Shards`; `--shards` |
| Hot loading | 80.0 | % of rating | A branch at or above is in the High-tier N-1 | `CycleOptions.HotBranchLoadingPercent`; `--hot-loading` |
| Cost smoothing | 0.3 | - | EWMA factor of the cost model | `CycleOptions.CostEwmaAlpha` |
| Harmonics gate | 1500 | buses | The harmonics job is skipped above this | `CycleOptions.HarmonicsBusLimit` |
| Harmonics source | 0.5 | pu | Fundamental current of the 6-pulse source | `AnalysisJobLibrary.HarmonicsSourceCurrentPu` |
| Seed costs | 2, 50, 40, 5 per branch, 20, 5 | core-ms | watch, hot N-1, harmonics, shard, RoCoF, physics | `AnalysisJobLibrary.Seed*` |
| DC-OPF constants | 1e6, 1e-6, 1e-9, 1e-3, 50 | MW, MW, MW, MW, GBP/MWh | Unbounded Pmax, balance tolerance, merit-order stop, binding tolerance, default cost | `DcOptimalPowerFlow.UnboundedPmaxMw`, `BalanceToleranceMw`, `MeritOrderFillStopMw`, `BindingToleranceMw`, `DefaultCostPerMwh` |
| Simplex | tolerance 1e-9, 20,000 iterations, refactor every 64 | -, iterations | LP solver | `BoundedSimplex.DefaultTolerance`, `DefaultMaxIterations`, `RefactorInterval` |
| Bridge test | 1e-9 | - | `abs(1 - Phi_kk)` below this marks a bridge, in the LODF column |  `DistributionFactors.IslandingSensitivityTolerance` |

## Timings

The numbers below were measured on the development machine: Intel Core `i3-N305`, 8 logical cores, 7.7 GB of RAM, Windows 11,
.NET 10.0.12, Release build. The first table is the median of 7 timed runs after one warm-up run, called through the API
from a console project (default GC settings), screening with the CLI's own options (`nminus1`, `nminus2`, `n-1-1`).

| Case | Buses / branches | One solve | N-1 sequential | N-1 parallel | N-2, 20 candidates | N-1-1, 20 candidates |
|---|---|---|---|---|---|---|
| GB reduced (`data/uk`) | 20 / 24 | 0.303 ms | 19.71 ms | 5.51 ms | 59.27 ms | 44.66 ms |
| Ireland (`data/intl/ireland-all-island`) | 60 / 82 | 2.472 ms | 188.92 ms | 47.75 ms | 336.11 ms | 941.54 ms |
| case118 | 118 / 186 | 8.517 ms | 1312.40 ms | 281.20 ms | 760.40 ms | 3380.91 ms |

A second run of the same method on the same machine gave, for GB reduced, 0.447 ms for one solve, 25.23 ms for N-1 sequential,
7.27 ms parallel, 62.40 ms for N-2 and 118.66 ms for N-1-1 (the table shows 44.66 ms), and for the sweep 127.0 ms dense and
50.6 ms sparse on case57, 263.9 and 80.9 ms on case118, 7849.2 and 2030.8 ms on case300. Run-to-run spread on this machine therefore
reaches 2.7x in the shortest N-1-1 cell; the ratios between the columns hold.

The LP of one redispatch takes 0.43 ms on the GB reduced model and 0.71 ms on the Irish model. case118 has no rated lines,
so its redispatch is the merit order alone (0.03 ms). The N-2 and N-1-1 counts at 20 candidates are 190 and 380 pairs in all
three cases.

The sweep's engine split, measured the same way with the sweep's options of the time (100 iterations, Q-limits off; before the study set), parallel over 8
threads, median of 5 after one warm-up run, with the dense and sparse results compared bit for bit in the measuring code (identical in all three
cases; `DefaultEngineRoutingTests` pins the agreement for five case300 outages under default options, and no test pins it under the
sweep's options):

| Case | Branches | Dense | Sparse |
|---|---|---|---|
| case57 | 80 | 92.0 ms | 37.4 ms |
| case118 | 186 | 262.0 ms | 67.3 ms |
| case300 | 411 | 6314.0 ms | 2240.6 ms |

The sparse engine is faster at every size measured here, including the sizes below the sweep's 1,000-bus threshold and the shared
200-bus threshold. The thresholds do not mark a measured crossover on this machine. gb-full is a bundled network
(`BundledNetworks.DataFolders`, `JsonCaseIds`), so it loads at every licence tier and the free-tier bus limit never applies to it.
Throughput in the sweep captures
(`docs/_captures/contingency/`): case14, 20 outages on 2 threads, 321 solves/s; case118, 186 outages on 4 threads,
315 solves/s; gb-synthetic, 24 sampled outages on 4 threads, 2 solves/s (2,227 buses; 23 of the 24 outages islanded).

## Invariants

| Invariant | Test |
|---|---|
| The `k = 1` core reproduces the N-1 screen field for field, exactly | `ContingencyTests.Single_outage_via_general_core_matches_the_N1_screen_exactly` |
| `ScreenNMinusK(k = 1)` returns one result per branch | `ContingencyTests.NMinusK_with_k1_is_the_plain_N1_screen` (asserts the count only) |
| On case118 the parallel N-1 equals the sequential N-1 position by position in outage label, `Converged` and `Secure` | `ChainAndScenarioTests.ScreenBranchesParallel_matches_sequential` |
| A branch-index sample screens exactly those branches | `ChainAndScenarioTests.ScreenBranchesParallel_sample_screens_only_requested_indices` |
| Radial GSP transformers island; a meshed line outage is secure; the screen mixes both | `ContingencyTests.Radial_gsp_transformer_outage_islands_its_demand`, `Meshed_line_outage_stays_secure`, `Reports_a_mix_of_secure_and_islanded` |
| The exhaustive N-2 screens `C(n, 2)` pairs, each labelled with two branches | `ContingencyTests.Exhaustive_N2_screens_every_unordered_pair` |
| The pruned N-2 screens `C(c, 2)` pairs | `ContingencyTests.Pruned_N2_keeps_only_the_candidate_pairs` |
| N-2 results are worst first by stress score, and some pair islands | `ContingencyTests.N2_is_at_least_as_severe_as_N1_and_is_ordered_worst_first` |
| Two exhaustive parallel runs on the reduced GB model agree in outage label, `Converged`, `MaxBranchLoadingPercent`, `MinVm` and `MaxVm` for N-2 and N-1-1 | `ContingencyTests.N2_enumeration_is_deterministic_despite_the_parallel_solve`, `N1_1_is_deterministic_despite_the_parallel_solve` |
| The exhaustive N-1-1 screens `n(n-1)` ordered pairs, labelled `first => second` | `ContingencyTests.Exhaustive_N1_1_screens_every_ordered_pair` |
| N-1-1 with an identity corrective action equals the N-2 pair | `ContingencyTests.N1_1_with_no_corrective_action_collapses_to_the_N2_pair` |
| On case300, `ScreenOutage` and `ScreenBranchesParallel` for outages 0, 7, 50, 120 and 300 match an explicit dense engine bit for bit, and so does the whole loss sweep | `DefaultEngineRoutingTests.Contingency_screens_default_engine_matches_the_dense_engine_bit_for_bit`, `Loss_contingency_default_engine_matches_the_dense_engine_bit_for_bit` |
| The stress weight, overload threshold and candidate count do not drift | `Ws13CoreConstantsAntiDriftTests` (`StressVoltageWeightPerPu`, `OverloadThresholdPercent`, `DefaultCandidateCount`, the DC-OPF constants) |
| The structured security LP equals the dense reference bit for bit | `DcOpfSecuritySimplexParityTests.Structured_security_dispatch_is_bit_identical_to_the_dense_reference` |
| Each DC island is dispatched on its own balance and reference; a dead island is reported and infeasible | `DcIslandTests.Each_island_gets_its_own_reference_by_the_AC_island_rule`, `Dcopf_dispatches_each_island_on_its_own_balance`, `An_island_with_no_generation_is_reported_and_its_load_makes_the_dispatch_infeasible` |
| A binding line limit forces redispatch onto a costlier generator; the unconstrained dispatch is merit order | `DcOptimalPowerFlowTests.A_binding_line_forces_redispatch_onto_a_costlier_generator`, `Unconstrained_dispatch_follows_merit_order` |
| The legacy and modern RoCoF flags are distinct and reported together | `RocofThresholdTests.Legacy_and_modern_thresholds_are_distinct`, `Screening_reports_dual_threshold_breach_flags` |
| The RoCoF screen ranks the worst loss first | `InertiaAndLimitsTests.Rocof_screening_ranks_worst_loss_first` |
| `WithSolvedVoltages` copies the solved state onto the buses | `ChainAndScenarioTests.WithSolvedVoltages_copies_solved_state_onto_buses` |
| On case14 and case118 with Q-limits off, a warm-started re-solve takes at most one iteration and agrees with the first solve to 8 decimal places in `Vm` and 6 in `VaDeg` | `ChainAndScenarioTests.Warm_started_resolve_converges_in_one_iteration_without_drift` |
| The budget is workers times window; High is always admitted; Low is gated by the budget | `CycleSchedulerTests.Budget_is_workers_times_window_core_ms`, `High_always_admitted_low_gated_by_budget` |
| The Low tier rotates to full coverage with no starvation | `CycleSchedulerTests.Low_tier_rotates_to_full_coverage_without_starvation` |
| On case118, the `Secure` verdict of each N-1 outage in one cycle is the same with 1 and 8 workers (keyed by outage label); the fan-out never mutates the frame; a faulting job is isolated | `CycleSchedulerTests.Results_are_independent_of_worker_count`, `Fan_out_never_mutates_the_shared_frame`, `Faulting_job_is_isolated_and_the_cycle_continues` |
| A cancelled shard resumes where it stopped and completes with results identical to an uninterrupted screen | `ResumableShardTests.A_cancelled_shard_resumes_where_it_stopped_and_completes_with_identical_results` |
| A cancelled run is not learned as the job's cost | `ResumableShardTests.A_cancelled_run_is_not_learned_as_the_jobs_cost` |
| Shard outages follow branch identity, and parallel twins keep distinct identities | `ResumableShardTests.Shard_outages_follow_branch_identity_when_the_frame_has_lost_branches`, `Parallel_twins_keep_distinct_identities` |
| The default cancellation token is bit-identical; a cancelled token aborts the solve; a Low job honours the deadline and a High job does not | `SolverCancellationTests.Default_token_is_bit_for_bit_identical`, `Cancelled_token_aborts_the_solve`, `Low_job_that_honours_the_deadline_is_cancelled_high_is_not` |
| The study set is 100 iterations, Q-limits on, default tolerance and Q passes; the OLTC wrapper is the `SteadyStateSolver` to the bit and the raw engine on a model without OLTCs | `ContingencyCorrectnessTests.Study_options_are_the_one_documented_set`, `The_oltc_loop_wrapper_is_exactly_the_steady_state_solver`, `The_oltc_loop_is_bit_identical_to_the_raw_engine_on_a_model_without_oltcs` |
| Under the study set the sequential and parallel screens agree field for field (case14, case118, GB reduced) | `ContingencyCorrectnessTests.The_sequential_and_parallel_screens_classify_every_outage_alike_under_the_study_set` |
| The sweep's dense and sparse engines are bit-identical under the study set and the old sweep set (case57, case118, case300) | `ContingencyCorrectnessTests.Sweep_dense_and_sparse_engines_are_bit_identical_under_the_study_set_and_the_old_sweep_set` |
| Netting: a clean base returns the raw text exactly; keys are elements (original branch index, boundary index, bus id); an islanding is never netted; an unsolved base throws; case14 0/19/1 -> 16/3/1, Ireland -> 70/8/4 | `ContingencyCorrectnessTests.Netting_*`, `Branch_violations_are_keyed_by_the_original_branch_index_in_a_double_outage`, `Case14_base_overvoltage_is_reported_once_not_on_every_outage`, `Ireland_netted_counts` |
| The pruned N-2 and N-1-1 pair only single outages that solve and return the others apart; an exhaustive screen excludes nothing | `ContingencyCorrectnessTests.Pruned_N2_pairs_only_outages_that_solve_and_reports_the_rest_separately`, `The_candidate_count_is_capped_by_the_outages_that_solve`, `An_exhaustive_screen_excludes_nothing`, `Pruned_N1_1_also_reports_its_failing_single_outages_separately` |
| The corrective redispatch: no action on a secure post-outage network (GB reduced, B4 unchanged to the bit); fewest MW against a rated line and a boundary; a lazily added limit is enforced; island by island; an uninfluenceable line does not block it; deterministic; bad generator ranges throw; exhaustive N-1-1 on GB reduced 122/6/424 and equal to the N-2 pairs where no action is taken | `ContingencyCorrectnessTests.Redispatch_*`, `B4_stays_at_its_pre_outage_loading_before_the_second_outage`, `A_limit_the_redispatch_would_break_is_added_and_enforced`, `An_islanded_network_is_redispatched_island_by_island`, `Gb_reduced_exhaustive_N1_1_counts_before_and_after`, `When_no_redispatch_is_needed_N1_1_pairs_equal_the_N2_pairs_bit_for_bit` |
| `CycleRunner` throws `NotSupportedException` for the pipelined option; the CLI refusal text is present in `Program.cs` | `BatchedAndCycleOptionTests.Cycle_runner_refuses_the_unimplemented_pipeline_option`, `Cli_cycle_pipeline_flag_fails_loud` (searches the source text; it does not run the CLI) |

The tests live in `tests/GridSim.Tests/`. The CLI refusals (an unknown generator, `stress --step 0`, an unknown `chain --mode`) are
pinned only by the captured runs in `docs/_captures/contingency/`; the CLI wiring of the contingency verbs (study set, netting,
verdict exit codes, `losses` case loading) is pinned by source scans in `CliContingencyVerdictTests`.

## Worked example

The reduced GB model has 20 buses and 24 branches. Its base case has no violation, so netting changes nothing here.

**N-1.** `gridsim nminus1` screens 24 outages: 12 island and 12 converge, and the verdict is NOT SECURE (exit 3). The 12
islanding outages score `+Infinity`. A converged outage, `L3-4 N England-Yorks`, read through the API with the default
raw engine, has `MaxBranchLoadingPercent` 84.53066528907604 and `WorstBoundaryLoadingPercent` 67.78656090591737 (the
table shows 85% and 68%), and its voltages 1.0274172838264433 to 1.04 pu are inside the band, so its score is
84.53066528907604 + 67.78656090591737 = 152.3172261949934. The CLI solves with the study set and the tap-changer loop, so
its table shows that outage at 0.998 to 1.040 pu, the regulated voltages of `gridsim uk --once`.

**N-2, pruned.** The candidates are the five most-stressed of the 12 single outages that solve; the 12 islanding
outages are listed apart and count against the verdict. `C(5, 2) = 10` pairs are solved after the 24 of the pre-screen,
and every pair is secure:

```
$ gridsim nminus2 --candidates 5
GridSim  -  N-2 branch screening: GB reduced (10-zone)  [top-5 candidate pairs]
==========================================================================
Base case: secure (no violation before any outage).
Screening N-1 first, then pairing the 5 most-stressed single outages that solve.
Outage                          Status        MaxLoad  WorstBnd  Vmin   Vmax
--------------------------------------------------------------------------
L6-9 Midlands-South + L6-7 Mi...  secure           85%      68%  0.998  1.040
L6-9 Midlands-South + L3-5 N ...  secure           85%      68%  0.998  1.040
L3-5 N England-N Wales + L6-7...  secure           85%      68%  0.998  1.040
L6-9 Midlands-South + L4-7 Yo...  secure           85%      68%  0.998  1.040
L6-7 Midlands-E Anglia + L4-7...  secure           85%      68%  0.998  1.040
L8-9 London-South + L6-7 Midl...  secure           85%      68%  0.998  1.040
L8-9 London-South + L4-7 York...  secure           85%      68%  0.998  1.040
L3-5 N England-N Wales + L4-7...  secure           85%      68%  0.998  1.040
L8-9 London-South + L3-5 N En...  secure           85%      68%  0.998  1.040
L6-9 Midlands-South + L8-9 Lo...  secure           85%      68%  0.998  1.040
--------------------------------------------------------------------------
10 outages screened  10 secure  0 with violations  0 islanded
12 single outage(s) left out of the pairing because they already fail at N-1 (islanded or no solution):
  L1-2 N-S Scotland               islanded
  L2-3 B6 boundary                islanded
  T1 GSP N Scotland (tx)          islanded
  ...
  T10 GSP SW & S Wales (tx)       islanded
Verdict: NOT SECURE, 12 insecure outage(s) (exit 3).
[exit 3]
```

Before October 2026 (D02) the 12 islanding outages tied at `+Infinity` and took the first five places in model order
(`L1-2`, `L2-3`, `T1`, `T2`, `T3`), so all 10 pairs islanded.

**N-2, exhaustive.** All `C(24, 2) = 276` pairs are solved: 212 island, 64 converge, 3 of those violate and 61 are secure. The worst converged
pair is `L7-8 E Anglia-London + L8-9 London-South`; through the API (raw engine) its `MaxBranchLoadingPercent` is 111.60680970729109 and
`WorstBoundaryLoadingPercent` 67.7918394586839, for a score of 179.39864916597497; its one violation is `L6-8 Midlands-London`
at 112% (original branch index 8 in its `Details`).

```
$ gridsim nminus2 --exhaustive --top 216
GridSim  -  N-2 branch screening: GB reduced (10-zone)  [exhaustive (every pair)]
==========================================================================
Base case: secure (no violation before any outage).
Outage                          Status        MaxLoad  WorstBnd  Vmin   Vmax
--------------------------------------------------------------------------
L1-2 N-S Scotland + L2-3 B6 b...  ISLANDED      islanded
...
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

**N-1-1, exhaustive.** The same 24 branches give `24 * 23 = 552` ordered pairs. No meshed first outage leaves a line or
boundary over its limit in the DC model, so no redispatch is made and each such pair is the N-2 pair; only `T2` and `T10`
are redispatched (250 and 510 MW moved), and both island. Of the 552, 424 island (212 pairs in each order) and 128
converge: 122 secure and 6 violating (the three London pairs, in both orders):

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
...
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

Before October 2026 (D01) every meshed first outage was followed by a full economic re-dispatch that moved the
`B4 Upper North (Scotland)` flow from 1549.9 MW (48.435617734804836 % of its 3,200 MW limit) to 3,699.9 MW
(115.62311773480394 %) before any second outage, so all 128 converged pairs violated (0 secure, 128 violating); the worst,
`L4-7 Yorks-E Anglia => L6-7 Midlands-E Anglia`, read 168.51865571221708 % on `L7-8` and 115.62311773480364 % on B4. Now
B4 on the corrected full topology after `L3-4` is the base case's 48.435617734804836 % to the last bit
(`ContingencyCorrectnessTests.B4_stays_at_its_pre_outage_loading_before_the_second_outage`).

**Frequency.** `gridsim trip` loses `Hinkley Point Nuclear` at 2,880 MW. The fleet holds 171.7 GVAs; the unit takes
`H*MBase` with it and leaves 158.9 GVAs, `E = 158,900 MW.s`. The first sample after the event at t = 1.0 s is
`rocof = -50 * 2880 / (2 * 158900) = -0.4531 Hz/s`, which the capture prints as `-0.453`. At half the remaining inertia
(79,450 MW.s) it is -0.9062, printed `-0.906` (capture `trip-default`). In `screen` the binding loss is `Midlands CCGT (marginal)` at
5,625 MW, with 126.7 GVAs remaining, and holding 0.125 Hz/s needs `50 * 5625 / (2 * 0.125) / 1000 = 1125` GVAs, so the headroom is
126.7 - 1125.0 = -998.3 GVAs (capture `screen-gbreduced`).

## Source map

| File | Type or function | Responsibility |
|---|---|---|
| `src/GridSim.Core/Analysis/ContingencyAnalysis.cs` | `ContingencyAnalysis`, `ContingencyResult`, `ContingencyViolation`, `NkScreenReport`, `CorrectiveRedispatch` | Outage application, classification, the study set, base-case netting, N-1, N-2, N-k, N-1-1, `StressScore`, `PlanCorrectiveRedispatch` / `RedispatchToRelieve` |
| `src/GridSim.Core/PowerFlow/OltcLoopPowerFlow.cs` | `OltcLoopPowerFlow` | The OLTC loop of `SteadyStateSolver` as an `IPowerFlowSolver`, for the contingency study set |
| `src/GridSim.Core/Analysis/BoundaryAnalysis.cs` | `BoundaryAnalysis.Evaluate` | Boundary flow and loading against its limit |
| `src/GridSim.Core/Analysis/VoltageLimits.cs` | `VoltageLimits` | The operational voltage band |
| `src/GridSim.Core/Analysis/LossContingency.cs` | `LossContingency` | Change in system losses per outage, for `nminus1-sweep --losses` |
| `src/GridSim.Core/Analysis/DcOptimalPowerFlow.cs` | `DcOptimalPowerFlow` | The `dcopf` dispatch (no longer the N-1-1 corrective action); `BindingToleranceMw`, `UnboundedPmaxMw` reused by the redispatch |
| `src/GridSim.Core/Analysis/DcOpfSecuritySimplex.cs` | `DcOpfSecuritySimplex` | The security LP without dense matrices |
| `src/GridSim.Core/Analysis/DistributionFactors.cs` | `DistributionFactors` | PTDF and LODF, DC islands and their references |
| `src/GridSim.Core/Numerics/BoundedSimplex.cs` | `BoundedSimplex`, `LpStatus` | The dense reference simplex and its constants |
| `src/GridSim.Core/PowerFlow/SolverSelector.cs` | `SolverSelector` | The shared dense, sparse and sweep choice |
| `src/GridSim.Core/Simulation/GridScenario.cs` | `GridScenario.AtLoad`, `WithSolvedVoltages` | Load scaling and warm start for `stress` and `chain` |
| `src/GridSim.Core/Simulation/LoadProfile.cs` | `LoadProfile.DailyFactor` | The daily demand curve |
| `src/GridSim.Core/Dynamics/RocofScreening.cs` | `RocofScreening` | Per-generator loss screen |
| `src/GridSim.Core/Dynamics/FrequencyDynamics.cs` | `FrequencyDynamics`, `DynamicsConfig` | The swing integration |
| `src/GridSim.Core/Dynamics/FrequencyLimits.cs` | `FrequencyLimits` | Frequency and RoCoF limits and the assessment flags |
| `src/GridSim.Core/Dynamics/InertiaEstimator.cs` | `InertiaEstimator` | The inertia floor of `screen` |
| `src/GridSim.Core/Scheduling/CycleScheduler.cs` | `CycleScheduler` | Admission, execution, classification and rotation of jobs |
| `src/GridSim.Core/Scheduling/AnalysisJobLibrary.cs` | `AnalysisJobLibrary` | The jobs, shards, `BranchKey`, resumable shards |
| `src/GridSim.Core/Scheduling/CycleRunner.cs` | `CycleRunner` | Base solve then fan-out, one cycle per call |
| `src/GridSim.Core/Scheduling/ResidentCycleService.cs` | `ResidentCycleService` | The same scheduler off the caller's thread with frame coalescing |
| `src/GridSim.Core/Scheduling/CycleBudget.cs`, `CycleOptions.cs`, `JobCostModel.cs`, `JobResult.cs` | budget, options, cost EWMA, outcomes | Compute budget, defaults, cost learning, job outcomes |
| `src/GridSim.Cli/Program.cs` | `SelectCase`, `LoadCaseArgument`, `SelectBatchCases`, `ContingencyBaseCase`, `PrintContingencyBaseCase`, `ContingencyVerdict`, `ContingencyStatus`, `PrintExcludedSingles`, `RunNMinus1`, `RunNMinus2`, `RunNMinus1Minus1`, `PrintNkTable`, `RunScreen`, `RunLossAllocation`, `RunTrip`, `RunNMinus1Sweep`, `RunStress`, `RunChain`, `RunCycle`, `LoadNamed` | Verb parsing, case loading, tables, report files |
| `src/GridSim.Cli/SolverBackend.cs` | `SolverBackend` | `DenseSolver`, `SparseSolver`, `NewtonSolver` on the selected backend |

## Limits

- Every verb here selects its network through `SelectCase`: a positional case (built-in name, `.m` file, JSON case directory) or `--dir`.
  Both given is refused with exit 2 unless they name the same folder; a missing `--dir` folder exits 1 before any report file is opened.
  A bare `.m` name is searched for through the `data/matpower` subfolders (`Gated.ResolveMatpower`); a name that matches two files exits 2.
- No test compares every field of a parallel screen bitwise with a sequential one or across worker counts; the worker-count and
  parallel-versus-sequential tests compare verdict flags only.
- The `nminus1-sweep` gb-full timings (one outage in 122 ms on the sparse engine against 9,801 ms on the dense one, 12 minutes against 16 hours
  for a sequential N-1 of 5,918 outages) are the CHANGELOG figures of the engine-routing change, which names no machine, and are not re-measured here.
- Netting is by element: a pre-existing violation that an outage makes worse is not counted against that outage (the row says `base only`).
- No slack is elected for an island that holds generation, so every split reads `ISLANDED`.
- The screens start from the stored voltages, not from the base-case solution. Through the API the defaults are still the raw engine and the `PowerFlowOptions` defaults; only the CLI passes the study set.
- The pruned N-2 and N-1-1 never pair a single outage that fails at N-1; such outages are reported apart, not combined with a second loss.
- The N-1-1 corrective redispatch works in the DC model: it holds rated lines and limited boundaries, not voltages or losses, minimises MW moved rather than cost, and measures the slack unit's room from its lossless DC-balanced output (AC losses are not in it). Its LP is degenerate by construction; it prices with the near-tie rule (`BoundedSimplex.NearTiePricingMargin`, relative 1e-10), so it moves the lowest-index units among equal choices.
- `nminus1` screens branch, generator and bus outages; `nminus2`, `n-1-1` and the sweep screen branches only, and there is no common-mode outage.
- `stress` scales load and dispatch uniformly, and takes the end of the contiguous converged prefix as collapse; `--from` above `--to` is refused with exit 2.
- `screen` and `trip` take the nadir and settling values from governor, damping and responsive-capacity constants that are literature-typical and not fitted to a measured trip, and the `screen` verdict uses the legacy 0.125 Hz/s RoCoF limit.
- The shared 200-bus engine threshold is not a measured crossover: the sparse engine is faster at every size measured here (the sweep's own 1,000-bus literal was removed in 1.95.105).
- A resumed `cycle` shard mixes frames, because each outage is screened against the frame current when it is reached.
- `cycle --pipeline` is refused because the pipelined backlog drain does not exist (R096).

## See also

- [Contingency analysis (user manual)](<../manual/0011 Contingency analysis.md>) - the verbs, their output and their pitfalls
- [Power flow](<0003 Power flow.md>) - the solve every outage repeats, the dense and sparse bit-identity, islands and the start rules
- [Frequency dynamics](<0005 Frequency dynamics.md>) - the swing model behind `trip` and `screen`
- [Architecture](<0001 Architecture.md>) - the cycle engine in the system
- [Validation](<0011 Validation.md>) - the machine-epsilon agreement this layer inherits
- [Sparse and GPU numerics](<0004 Sparse and GPU numerics.md>) - the linear solvers under the Newton step
