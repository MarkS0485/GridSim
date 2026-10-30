# 3. Power flow

This chapter specifies the steady-state AC power flow: the three engines that implement `IPowerFlowSolver`, the
equations they solve, the order in which they perform arithmetic, and the rules that decide when a result is
reported as converged. The engines are the dense Newton-Raphson
(`src/GridSim.Core/PowerFlow/NewtonRaphsonPowerFlow.cs`), the sparse Newton-Raphson
(`src/GridSim.Core/PowerFlow/SparseNewtonRaphsonPowerFlow.cs`) and the backward/forward sweep for radial
feeders (`src/GridSim.Core/PowerFlow/BackwardForwardSweep.cs`). Two outer loops wrap them: the generator
reactive-limit loop (inside each Newton engine) and the on-load tap-changer loop
(`src/GridSim.Core/PowerFlow/SteadyStateSolver.cs`). Three opt-in wrappers also implement `IPowerFlowSolver` and
are reached through the `pf` verb: `DistributedSlackPowerFlow` (`pf --distributed-slack`), and `OltcLoopPowerFlow`
and `SwitchedShuntPowerFlow` (`pf --switched-shunts`). This chapter specifies the engines; the wrappers drive them.

The contract that governs the design is bit-identity. For every case in the parity suite (listed under Invariants),
the dense and sparse Newton engines return the same IEEE-754 bit pattern for every voltage, angle, injection, flow,
loss and mismatch, the same iteration count, the same status and the same slack-reactive note. They achieve it by
sharing one Newton step (one linear solve, one damped update) and by assembling every quantity with the same
products and sums in the same order; the section "Dense and sparse: how bit-identity is achieved" lists each
mechanism. The sweep solves the same physical model but is a different algorithm and is not bit-identical to
Newton. GPU backends replace only the linear solve and sit outside the contract
([ch. 4, Sparse and GPU numerics](<0004 Sparse and GPU numerics.md>)).

Fields, units and the Y-bus stamp are defined in
[ch. 2, Data model and per-unit conventions](<0002 Data model and per-unit conventions.md>) and are not repeated here. This chapter uses
`BaseMva`, `Bus`, `Generator`, `Branch`, `Oltc` and `ZipLoad` as defined there.

## Model

### Bus types and unknowns

Every bus has a type (`BusType` in `src/GridSim.Core/Model/Model.cs`) that decides which quantities are fixed
and which are solved.

| Type | Fixed | Solved | Equations contributed |
|---|---|---|---|
| `Slack` | `Vm`, `Va` | net P and Q (result only) | none |
| `Pv` | P (net injection), `Vm` | `Va`, Q (result only) | one: P balance |
| `Pq` | P and Q (net injection) | `Vm`, `Va` | two: P and Q balance |

A model may carry several `Slack` buses; each is held at its own setpoint and contributes no equation. A bus typed
`Pv` that has no generator on it is solved as `Pq` (`NewtonShell.BuildBusSpec`): nothing can hold its voltage.
During the reactive-limit loop a `Pv` bus can become `Pq` (the "effective type"); the original type is kept as
`Type0`.

With `n` buses, `npvpq` non-slack buses and `npq` effective-`Pq` buses, the system has `m = npvpq + npq`
equations and unknowns.

### Per-unit formulation

All network quantities are per-unit on the system base `GridModel.BaseMva`; per-unit conventions, the branch
pi-model with tap and phase shift, and the bus-shunt stamp `(Gs + jBs)/BaseMva` are specified in
[ch. 2, Data model and per-unit conventions](<0002 Data model and per-unit conventions.md>). The Y-bus is `Y = G + jB`. Only in-service
branches are stamped. Both Newton engines take the stamp from `AdmittanceStamp.Branch`
(`src/GridSim.Core/PowerFlow/AdmittanceStamp.cs`), so a tap ratio at or below zero means 1 and the phase shift is
always applied.

### Bus specification and start point

`NewtonShell.BuildBusSpec` (`src/GridSim.Core/PowerFlow/NewtonShell.cs`) aggregates generators and loads onto
buses, in model order, and builds the start point:

```
Pspec[i] = (sum of Pg at bus i - Pd[i]) / BaseMva
Qspec[i] = (sum of Qg at bus i - Qd[i]) / BaseMva
QMinBus[i], QMaxBus[i] = sums of Qmin, Qmax over the generators at bus i
Vg[i]    = Vg of the LAST generator listed at bus i
```

The last-listed rule is `VoltageSetpoint.ForBus` (`src/GridSim.Core/PowerFlow/VoltageSetpoint.cs`); the sweep uses
the same function.

Start values:

| Quantity | Value |
|---|---|
| `Vm`, bus is `Slack` or `Pv` and has a generator | `Vg[i]` |
| `Vm`, `Slack` bus with no generator | stored `Vm` (its set point), whatever `FlatStart` says; refused when it is not above 0 (GR-046) |
| `Vm`, any other bus | `1.0` when `FlatStart`, else the bus's stored `Vm` |
| `Va`, `Slack` bus | stored `VaDeg` converted to radians, whatever `FlatStart` says |
| `Va`, any other bus | `0` when `FlatStart`, else stored `VaDeg` in radians |

`Pspec` and `Qspec` hold the nominal load (the load at 1.0 pu voltage). A voltage-dependent load is applied as a
deviation from nominal inside the iteration, as described next.

### Mismatch equations

Injections at the present state use `th_ik = Va_i - Va_k`:

```
P_i = Vm_i * sum_k Vm_k * (G_ik * cos(th_ik) + B_ik * sin(th_ik))
Q_i = Vm_i * sum_k Vm_k * (G_ik * sin(th_ik) - B_ik * cos(th_ik))
```

The mismatch vector stacks the real-power residual of every non-slack bus (ascending bus order), then the
reactive-power residual of every `Pq` bus (ascending bus order). It is spec minus calculated, in per-unit on
`BaseMva`:

```
f = [ Pspec_i - P_i - dP_i(Vm_i)   for each non-slack bus i     (npvpq rows)
      Qspec_i - Q_i - dQ_i(Vm_i)   for each Pq bus i            (npq rows)  ]
```

`dP_i` and `dQ_i` are the ZIP deviation terms below and are zero for a constant-power bus. A `Pv` bus has no Q
row; the slack has no row at all. `NewtonRaphsonPowerFlow.ComputeInjections` and
`SparseNewtonRaphsonPowerFlow.ComputeInjections` are `internal` so that the state estimator and the estimate
projection evaluate `h(x)` through the same code.

### ZIP load

A bus whose `Zip` is a `ZipLoad` with `VariesWithVoltage` (any of `Ap`, `Bp`, `Aq`, `Bq` non-zero) draws

```
Pload(V) = Pd * (Ap*V^2 + Bp*V + Cp)          Qload(V) = Qd * (Aq*V^2 + Bq*V + Cq)
```

with `V = Vm` in per-unit. `ZipLoad` defaults are `Ap = Bp = Aq = Bq = 0`, `Cp = Cq = 1` (constant power). The
solver does not require each coefficient triple to sum to 1; the JSON loader enforces that and fails loud
(`ZipLoadWiringTests`). The mismatch deviation and Jacobian terms (`NewtonShell.ZipDeviationPu`,
`NewtonShell.ZipJacobianDiagPu`) are:

```
dP_i = Pd_i * (PMultiplier(Vm_i) - 1) / BaseMva        dQ_i = Qd_i * (QMultiplier(Vm_i) - 1) / BaseMva
N_ii += Pd_i * (2*Ap*Vm_i + Bp) / BaseMva              (Pq buses only)
L_ii += Qd_i * (2*Aq*Vm_i + Bq) / BaseMva              (Pq buses only)
```

`N_ii` and `L_ii` are the diagonal entries of the Jacobian blocks below; only `Pq` buses have a voltage column,
so a `Pv` bus gets the mismatch deviation but no Jacobian term. `BuildBusSpec` registers a bus's ZIP only when
`VariesWithVoltage` is true, and sets `HasZip` only when at least one bus does; with no such bus the ZIP code is
skipped and the constant-power path is untouched.

Everything that reads a load after the solve uses `LoadAtVoltage` (`src/GridSim.Core/PowerFlow/LoadAtVoltage.cs`):
a constant-power bus returns its stored `Pd`/`Qd` untouched, a ZIP bus returns `Pd*PMultiplier(V)` and
`Qd*QMultiplier(V)`. The reactive-limit check, the slack diagnostic and the sweep use it, so every consumer
measures the same load.

## Algorithm

### Newton iteration (one reactive-limit pass)

Each pass runs `RunNewton` (one in each engine) on the current effective bus types:

```
number the unknowns and equations: NewtonShell.EquationPositions
for iter = 0, 1, 2, ...:
    Cancellation.ThrowIfCancellationRequested()
    evaluate P, Q at (Vm, Va)
    build f (above)
    maxMis = max over k of |f[k]|
    if maxMis < Tolerance:  converged; stop              (before any factorisation)
    if iter = MaxIterations:  not converged; stop        (the cap: no further step)
    build J
    solve J * dx = f      (NewtonShell.SolveStep); if no finite step exists: stop, not converged
    apply the damped update (NewtonShell.ApplyStep)
```

Four facts follow from this order.

- The convergence test runs before the linear solve, so a start point that already satisfies the tolerance costs
  zero factorisations and reports `Iterations = 0`.
- `Iterations` counts completed Newton steps, at most `MaxIterations`. The mismatch is evaluated after every step,
  the last included, so a run that reaches the tolerance on its `MaxIterations`-th step is converged, and a run that
  hits the cap reports `MaxIterations` with `MaxMismatch` taken at the state it returns.
- `f` is spec minus calculated, so `J` is the plain derivative of the calculated injections and the update is
  `x = x + dx`.
- The tolerance is absolute, per-unit on `BaseMva`, over the P rows of every non-slack bus and the Q rows of every
  `Pq` bus. The Q of a `Pv` bus and everything at the slack are not tested.

### Unknown ordering

`NewtonShell.EquationPositions` numbers the unknowns once per pass, and both engines use the result:

```
position m0, m0+1, ...  : theta of every non-slack bus, ascending bus order      (npvpq entries)
then                    : Vm of every effective-Pq bus, ascending bus order      (npq entries)
```

Equation row `r` pairs with unknown column `r`: P-balance of a bus pairs with that bus's angle, Q-balance with its
magnitude. The dense engine also keeps explicit `pvpq` and `pq` index lists for its assembly and throws
`InvalidOperationException` if the system size `EquationPositions` returns differs from `npvpq + npq`. The check
compares the count only, not the order of the entries.

### Jacobian

The Jacobian has the standard polar block structure:

```
        theta (npvpq)   Vm (npq)
J = [      H               N      ]   P rows (npvpq)
    [      M               L      ]   Q rows (npq)

H = dP/dtheta    N = dP/dVm    M = dQ/dtheta    L = dQ/dVm
```

Entries, with `G`, `B` taken from `Y[i,j]` and `theta = Va_i - Va_j`:

| Block | i = j (diagonal) | i != j |
|---|---|---|
| H | `-Q_i - B_ii*Vm_i*Vm_i` | `Vm_i*Vm_j*(G_ij*sin(theta) - B_ij*cos(theta))` |
| N | `P_i/Vm_i + G_ii*Vm_i`, plus ZIP term | `Vm_i*(G_ij*cos(theta) + B_ij*sin(theta))` |
| M | `P_i - G_ii*Vm_i*Vm_i` | `-Vm_i*Vm_j*(G_ij*cos(theta) + B_ij*sin(theta))` |
| L | `Q_i/Vm_i - B_ii*Vm_i`, plus ZIP term | `Vm_i*(G_ij*sin(theta) - B_ij*cos(theta))` |

Diagonals use the injections already evaluated at this iteration. Both engines use `DetMath.SinCos` for the
trigonometry: the deterministic C# port of fdlibm, whose two halves are bit-identical to its own `Sin` and `Cos`
(`FdlibmStrictMathVectorTests.SinCos_halves_are_bit_identical_to_Sin_and_Cos`). The platform's `Math.SinCos` calls the
operating system's C library, which rounds differently on Windows, Linux and macOS, and
`DeterministicMathGuardTests` fails the test run if a numerical path calls it.

The sparse engine builds the Jacobian pattern once per pass (`SparseNewtonRaphsonPowerFlow.BuildJacobianStructure`):
for every Y entry `(i, j)` it adds the four cells H, N, M, L whose row and column both exist in the reduced
system, with placeholder value 1.0 so that no cell is dropped, and records a slot map from each Y entry to its
four cells. Each iteration `RefillJacobian` clears the values and overwrites every slot in place by walking Y row
by row; the pattern is a structural superset and never changes inside a pass, even when a block is exactly zero
at some iterate. The ZIP diagonal terms are added in place after the refill.

The dense engine writes the same values into a reused `double[m,m]` (`BuildJacobianInto`) and copies it cell for
cell into the sparse engine's pattern (`CopyIntoPattern`). Any dense cell outside the pattern must be exactly
zero; if it is not, `CopyIntoPattern` throws `InvalidOperationException`, so a pattern or assembly disagreement
fails loud instead of dropping a term.

### The canonical Newton step

`NewtonShell.SolveStep` is the one linear solve both Newton engines use on the CPU.

1. **Direct sparse LU.** `CpuSparseLuSolver.Solve` factors `J` and solves. The factorisation
   (`src/GridSim.Core/Numerics/SparseLu.cs`, `SparseLuFactorization.cs`) is Gilbert-Peierls left-looking LU
   with partial pivoting over a reverse Cuthill-McKee (RCM) column ordering.
   - CSR is converted to CSC; explicit zeros are dropped.
   - RCM works on the symmetrised pattern with duplicate neighbours removed. Each connected component starts from
     a minimum-degree node, a node's unvisited neighbours are queued in ascending degree, and the visiting order
     is reversed at the end. The ordering depends only on the pattern.
   - For each column in RCM order the factorisation does one sparse triangular solve (pattern found by
     depth-first reach) and picks the pivot as the largest `|x[i]|` among rows that are still unpivoted (strict `>`, so the
     first maximum in reach order wins). There is no diagonal preference.
   - The matrix is singular when no pivot candidate exists or when the best candidate satisfies
     `|pivot| <= NumericTolerances.RelativePivotFloor * max|J|` with `RelativePivotFloor = 1e-12`. A NaN in `J`
     throws `ArgumentException` from the factorisation.
   - `CpuSparseLuSolver` caches the RCM ordering and recomputes it only when the CSR structure changes (compared
     by content on every call), so inside a pass only the numeric factorisation repeats; a reactive-limit
     bus-type switch changes the pattern and triggers a new ordering.
   - All arithmetic in the factorisation and the solve is scalar double-precision multiply, subtract and divide.
     No fused multiply-add and no SIMD kernel runs on this path.
2. **GPU retry.** When the engine was built with a non-CPU sparse backend and that backend returns `null`
   (breakdown, stagnation or non-convergence), `SolveStep` retries on the engine's own `CpuSparseLuSolver`
   (`directFallback`) before falling back further.
3. **ILU(0)/BiCGSTAB fallback.** When the LU returns `null`, `SolveStep` factors ILU(0) of `J` and runs
   preconditioned BiCGSTAB with relative residual tolerance `min(1e-10, Tolerance * 1e-3)` and iteration cap
   `4m + 200` (`NumericTolerances.DefaultKrylovMaxIterations`). The convergence test is `||r|| / ||b|| < tol`, and
   BiCGSTAB starts from zero. A tiny ILU(0) pivot is pushed out to `1e-12 * max|row|` (the preconditioner only
   steers the iteration; the residual test against the true `J` decides success). If BiCGSTAB fails,
   `SolveStep` returns `false`.
4. **Finite-step guard.** Any NaN or infinite component in `dx` makes `SolveStep` return `false`. A non-finite
   correction is never applied. `SolveStep` also returns `maxAbs`, the largest `|dx[k]|`, for the damping rule.

When `SolveStep` returns `false` the pass stops with `converged = false` and the engine reports non-convergence;
nothing is thrown. A dense engine built with a GPU dense solver (`RocmDenseLinearSolver`, `GpuDenseLinearSolver`)
calls that solver's `SolveInPlace` as given, applies the same finite-step guard, and has no CPU retry. The CPU
`DenseLuSolver` passed to the dense engine is replaced by the canonical step: `NewtonRaphsonPowerFlow` treats
`null` or `DenseLuSolver` as "use the canonical CPU path".

### Step damping and voltage floor

`NewtonShell.ApplyStep` applies the correction:

```
damp = maxAbs > MaxUndampedStep ? MaxUndampedStep / maxAbs : 1.0          MaxUndampedStep = 0.4
for i = 0 .. n-1, ascending:
    if pPos[i] >= 0:  Va[i] += damp * dx[pPos[i]]
    if qPos[i] >= 0:  Vm[i] += damp * dx[qPos[i]];  if Vm[i] < VmFloorPu: Vm[i] = VmFloorPu     VmFloorPu = 0.2
```

If the largest correction component exceeds 0.4 (radians for an angle, per-unit for a magnitude) the whole step
is scaled so that component equals 0.4 and the direction is preserved. `Vm` of a `Pq` bus is floored at 0.2 pu
after the update. The floor protects against an overshooting flat-start step on a stiff network that would
otherwise reach zero or negative magnitude, where the polar equations degenerate (the Jacobian divides by
`Vm`). Near a solution every step is below 0.4, `damp` is exactly 1.0 and the floor is inactive, so the converged
point is the one undamped Newton reaches, and quadratic convergence is intact. Both engines, and a GPU dense
backend, apply the same rule.

### Convergence and failure reporting

The engines fail loud:

- A result is `Converged` only if the mismatch test passed and every solved value is finite. After the loop,
  `NewtonShell.AssembleResult` checks `MaxMismatch`, `Vm`, `Va` and the injections for NaN or infinity; any
  non-finite value gives status `NonFinite` even if a stale mismatch read below the tolerance.
- A NaN mismatch cannot satisfy `maxMis < Tolerance`, because `Math.Max` propagates NaN. A refused step stops the
  pass at once instead of iterating on garbage to the iteration cap.
- A Newton step that cannot be solved, or the iteration cap, gives `converged = false` and status `MaxIterations`
  (or `NonFinite` if the mismatch or state is non-finite).
- `MaxQLimitPasses < 1` with `EnforceQLimits` on throws `ArgumentOutOfRangeException`.
- Studies that start from a solved state call `ConvergenceContract.RequireConverged`
  (`src/GridSim.Core/PowerFlow/ConvergenceContract.cs`), which throws `NonConvergedStateException` naming the
  study, the status, the mismatch and the iteration count. Fault analysis, protection assessment and cascade,
  loss-contingency sweeps, small-signal assembly, subtransient initialisation and the RMS transient studies use
  it.
- The `--once` report prints `DID NOT CONVERGE` and the process exits with code 3.

### Dense and sparse: how bit-identity is achieved

The two engines keep their own assembly (dense arrays versus CSR walks). Every difference between them is removed
by one of these mechanisms, each verified in code:

1. **One equation numbering.** Both call `NewtonShell.EquationPositions`.
2. **One Jacobian sparsity pattern.** The dense engine builds the sparse engine's Y pattern
   (`SparseNewtonRaphsonPowerFlow.BuildY`) and its Jacobian pattern (`BuildJacobianStructure`), then copies its
   values into it. The pattern fixes the RCM ordering and so the rounding of the LU.
3. **One linear solver.** Both call `NewtonShell.SolveStep` with a `CpuSparseLuSolver`; the dense engine's own
   dense LU is not used on the CPU path.
4. **One update rule.** Both call `NewtonShell.ApplyStep`; bus order and arithmetic are part of the contract.
5. **One mismatch, Q-limit, result and line-flow code.** `BuildBusSpec`, `ApplyQLimits`, `DiagnoseSlackQ`,
   `AssembleResult` and `ComputeLineFlows` are shared pure functions.
6. **Exact assembly arithmetic.**
   - Y accumulation order. `YBus.Build` (dense) adds branch stamps in model order, then bus shunts, into cells
     that start at +0.0. `ComplexCsr.Builder` (sparse) stable-sorts each row by (column, insertion index) and sums
     duplicate cells in insertion order from +0.0, so each cell sums in the same order and a first stamp of `-0.0`
     becomes `+0.0` in both. The sort key is unique, so the result does not depend on the sort algorithm.
   - Injection sums. The dense loop runs `k` ascending over all buses; the sparse loop runs over the non-zero Y
     entries of the row, in ascending column order. A dense term with `G = B = 0` adds `+0.0` or `-0.0`, which
     leaves the partial sum unchanged, so the sums agree to the bit.
   - Jacobian entries are the same expressions with the same operand order in `BuildJacobianInto` and
     `RefillJacobian`. The ZIP terms are added to the diagonal after the base value in both.
   - Trigonometry is `DetMath.SinCos` in both.
7. **No fused multiply-add on the CPU Newton path.** `NewtonShell.SolveStep`, `CpuSparseLuSolver` and `SparseLu`
   call neither `Math.FusedMultiplyAdd` nor a `SimdKernels` routine, and the .NET JIT does not contract `a*b + c`.
   Two things outside that path do use vector code. `DenseLuSolver` (`DenseLuKernels.cs`, `DenseLuSolver.cs`) calls
   `SimdKernels.SubtractScaledRow`, the bit-exact non-FMA kernel; the Newton engines replace `DenseLuSolver` with the
   canonical step, so it is off the Newton path. The estimator's block evaluator in `SparseLuFactorization.cs` uses
   vector code and is also not on the Newton path.

Parity is pinned by exact bit comparison (`DenseSparseParityTests`) of `Converged`, `Status`, `Iterations`,
`MaxMismatch`, `BusIds`, `Vm`, `VaDeg`, `PInjMw`, `QInjMvar`, `TotalLossMw`, `TotalLossMvar`, `SlackQNote` and,
for every branch, `From`, `To`, `PFromMw`, `QFromMvar`, `PToMw`, `QToMvar` and `LoadingPercent`, or of identical
exceptions, on the eight built-in cases, 24 named `.m` files and three JSON folders the test lists, under four
option sets: Q limits on, Q limits off, flat start with Q limits on, and Q limits on with revert (`gb-full` runs
the first set only). The per-branch `PLossMw`, `QLossMvar`, `Name` and `RatingMva` are not compared by that test; `PLossMw` and
`QLossMvar` are the sums of the compared end flows, formed by the shared `ComputeLineFlows`. Cross-CPU equality
(AVX-512, AVX2, non-FMA) follows from mechanism 7. It was observed by the determinism probe
(`review-progress/2026-10/probes/determinism-probe/`) and by running the suite under `DOTNET_EnableAVX2=0` and
`DOTNET_EnableHWIntrinsic=0`; no automated test compares tiers (see
[ch. 8, Provenance and invariants](<0008 Provenance and invariants.md>)).

### Reactive-limit outer loop

With `EnforceQLimits` on (the default) each Newton engine runs up to `MaxQLimitPasses` passes. After each pass:

1. If the pass did not converge, stop. No bus type changes: the Q values of an unconverged iterate are not a
   state. If `EnforceQLimits` is off the engine runs exactly one pass and stops.
2. `NewtonShell.ApplyQLimits` examines every bus whose original type is `Pv`, in ascending bus order, using the
   one solved state. All violating buses switch in the same pass.
3. If no bus switched, stop; the converged result stands. Otherwise run the next pass on the new types, starting
   from the solved voltages (a reverted bus restarts at `Vm = Vg`). The unknown numbering changes, so the
   Jacobian pattern and the RCM ordering are rebuilt.

**Forward rule.** For a bus that is still `Pv`, the generators' reactive output is
`qGen = Q_i * BaseMva + QloadAt(V)` in MVAr, where the load is the load actually drawn at the solved voltage
(`LoadAtVoltage.QMvar`; exactly `Qd` for a constant-power bus):

```
qGen > QMaxBus + QLimitTolMva :  type = Pq,  Qspec = (QMaxBus - Qd) / BaseMva,  pinned = Max
qGen < QMinBus - QLimitTolMva :  type = Pq,  Qspec = (QMinBus - Qd) / BaseMva,  pinned = Min
```

`QLimitTolMva = 1e-3` MVA. `QMinBus` and `QMaxBus` are the sums over the bus's generators; a generator's
`Qmin`/`Qmax` default to minus and plus infinity, so an unlimited machine never pins.

**Revert rule** (opt-in, `RevertQLimits`, default off). A pinned `Pq` bus returns to `Pv` when its solved voltage
shows the limit is not binding:

```
pinned Max and Vm > Vg + RevertVoltTolPu   -> revert
pinned Min and Vm < Vg - RevertVoltTolPu   -> revert        RevertVoltTolPu = 1e-4 pu
```

A reverted bus gets `Vm = Vg`, `pinned = None`, `revertCount + 1`. Injecting more reactive power raises the local
voltage, so a bus pinned at `Qmax` that sits above its setpoint is injecting more than it needs; a bus that wants
more reactive power while pinned at `Qmax` is correctly limited and stays `Pq`. A bus reverts at most once per
solve (`revertCount < 1`). A bus that re-violates after reverting is pinned again by the forward rule and stays
pinned, so the loop terminates and the final state respects the limits. With the option off, a switched bus never
returns to `Pv`.

**Pass cap.** The loop stops at `MaxQLimitPasses` (default 12). If the last permitted pass switched a bus, the
switched types have never been solved. The engine then solves once more on the final types so the reported state
matches them, adds those iterations to `Iterations`, and reports the result not converged (status
`MaxIterations`), whatever that last solve achieves. `NewtonShell.QLimitPasses` refuses a cap below 1.

**Slack.** In the engines the slack bus is never limited and never reassigned; a distributed slack is available only
through the opt-in `DistributedSlackPowerFlow` wrapper.
`NewtonShell.DiagnoseSlackQ` runs after every Newton solve and sets `SlackQNote` on the engine instance when the
slack's reactive injection, with the load drawn at the solved voltage, lies outside the aggregate limits of the
machines on it (skipped when `QMin = QMax = 0`): `Slack bus {index}: reactive output {x} MVAr exceeds aggregate
Qmax {y} MVAr.` or `... below aggregate Qmin ...`. The index is the zero-based solver index, not the external bus
id. The note is diagnostic only; it does not change the solution or the status.

### OLTC outer loop

`SteadyStateSolver.Solve(model, options, maxOltcPasses)` wraps any `IPowerFlowSolver`. With no `Oltc` in the
model it runs the plain power flow and reports `TapsSettled = true`. Otherwise:

```
result = solve(model)
for pass = 0 .. maxOltcPasses - 1:
    if not result.Converged: break                    (TapsSettled stays false)
    for each Oltc o, in model order, except a held one (controlled bus PV or slack):
        v = solved Vm of o.ControlsBus;  tap = present TapRatio of o's transformer
        newTap = tap
        if v < Target - Deadband:  newTap = raise-controlled-bus step
        if v > Target + Deadband:  newTap = lower-controlled-bus step
        if |newTap - tap| > TapChangeEpsilon: record the new ratio
        else if v is outside Target +/- Deadband: o is pinned at MinTap or MaxTap; record its transformer name
    if no tap changed:
        TapsAtLimit = the pinned transformer names (model order)
        TapsSettled = (TapsAtLimit is empty); break
    model = Dispatcher.Clone(model, branches with the new ratios)
    result = solve(model);  OltcPasses = pass + 1
```

The two step expressions are `TapDown = Max(MinTap, tap - StepPu)` and `TapUp = Min(MaxTap, tap + StepPu)`. Which
one raises the controlled bus depends on the end of the transformer it is on. The tap sits on the From end
(MATPOWER convention), so `V_to ~ V_from / a`:

| Controlled bus | Raises the bus | Lowers the bus |
|---|---|---|
| To end, or a remote bus beyond it | `TapDown` | `TapUp` |
| From end (`ControlsBus == From` and `!= To`) | `TapUp` | `TapDown` |

Properties:

- One step per transformer per pass; all OLTCs in a pass read the same solved voltages. Two records that resolve to
  the same transformer (possible through the name-only fallback) do not both step it: the first record in model order
  that moves it has the pass, and `GridModel.Validate` warns about the shared transformer (GR-503; it used to move two
  steps). The cost is one full power flow per step of total tap travel.
- A tap never moves on a non-converged (or NaN) solve; the loop stops and returns that result, whose `Converged`
  says so.
- `TapsSettled` is `true` when a pass moved no tap and no tap is pinned at a limit with its bus out of band, or the
  model has no OLTC. It is `false` when the loop stopped on a non-converged solve, ended its `maxOltcPasses` (default
  `DefaultMaxOltcPasses = 25`) with taps still moving, or stopped with a tap pinned at `MinTap` or `MaxTap` while its
  controlled bus is outside the deadband.
- `TapsAtLimit` names (by `Oltc.Transformer`, in model order) the transformers pinned that way on the final converged
  pass. It is empty when every OLTC reached its band, when the model has no OLTC, and when the loop stopped for another
  reason; non-empty always means `TapsSettled` is `false`. The `--once` report and the live dashboard turn it into the
  `Taps NOT SETTLED` line (up to ten names); the line is a report and does not change the exit code.
- LiveSim runs no tap passes on the sparse (large case) and sweep (radial) engines, to keep each tick fast. Decision
  (GR-352, October 2026): such a frame carries `SimFrame.TapsRegulated = false`, with `TapsSettled` equal to the
  solve's convergence and no `TapsAtLimit`, instead of the solver's unrun-loop `false`, which read as a tap out of range
  on every frame.
- `RegulatedModel` is the model actually solved, with the final tap ratios. The loop solves on internal clones,
  so post-solve consumers (the live dashboard, SCADA) read `RegulatedModel`, never the model they passed in.
- `OltcPasses` counts the re-solves of the last call. `result.Iterations` is that of the final solve only.
- A tap already at `MinTap` or `MaxTap` does not move. When its bus is outside the deadband it is named in
  `TapsAtLimit` and the loop is not settled; when the bus is inside the deadband it is settled.
- An `Oltc` whose `Transformer` name matches no branch, or whose `ControlsBus` matches no bus, is skipped.
- Which transformer a record drives: the first branch carrying the record's `Transformer` name AND having
  its `ControlsBus` at the From or To end (`SteadyStateSolver.MapOltcRecordsToBranches`). Only when no same-named
  branch touches the controlled bus (a remote controlled bus) does the record fall back to the first branch of the
  name. The passive plant (`PassivePlantFleet`) uses the same matcher, so the two always agree.
- A record whose controlled bus is generator-regulated (PV or slack in the model) holds its tap, as the
  passive plant does: the machine's AVR fixes that voltage, so no tap position moves it; regulating it would run the
  tap one step per pass to its limit. `TapsHeldAtGeneratorBus` counts the held records of the last call. On gb-full
  71 of the 1,383 records aim at PV buses held at 1.02 pu against a 1.0 +/- 0.01 pu target; those taps hold at 1.0,
  the loop settles in 4 passes and the gb-full solve's losses are 523.1288528336736 MW (pinned by
  `GbReferenceCrossCheckTests`). The CHANGELOG entry for the change records 936 taps moving and a largest voltage
  change of 0.00019445173554877293 pu (bus 5764) against a run that stepped those taps to 1.0999999999999996; no test
  pins those figures. The other bundled cases with OLTCs (gb-synthetic, uk) are unaffected.
- "Generator-regulated" is judged on the bus types each converged solve ENDED with (the Newton engines report them as
  `PowerFlowResult.SolvedBusTypes`), the model's types only before the first solve or after a non-converged one. A PV
  bus whose units the reactive-limit loop pinned at Qmin / Qmax solved as PQ, so its tap changer regulates it (a 2-bus
  test case: the bus sits at 0.9457323728577838 pu with the tap held, and the tap moves to 0.95 and the bus to 1.0 pu
  once it regulates). `TapsHeldAtGeneratorBus` counts the held records of the last converged pass.
- The instance holds per-solve state and is not thread-safe. A concurrent `Solve` on one instance is detected by an
  interlocked guard and throws `InvalidOperationException`. Parallel N-1 sweeps use one instance per thread.

`SteadyStateSolver()` with no engine argument picks the Newton engine per model at each `Solve` by
`SolverSelector.CreateNewton`; the tap passes change taps, never the bus count, so one pick serves the whole
loop.

### Backward/forward sweep

`BackwardForwardSweep` solves radial feeders, where the high R/X ratios make transmission Newton a poor fit. It
needs exactly one source: it uses the first `Slack` bus and throws `InvalidDataException` when the model has none.
A forced run on a model with several `Slack` buses sweeps the first one only; the others are treated as ordinary
buses with their generation as constant injection.

**Setup.** Per bus: `sLoad = (Pd + jQd)/BaseMva - (Pg + jQg)/BaseMva` (power drawn), shunt admittance
`yShunt = (Gs + jBs)/BaseMva`. Per in-service branch: series admittance `y = 1/(R + jX)`, half charging
`bsh = jB/2`, tap `tap = a * exp(j*shift)` with `a <= 0` meaning 1 (the `AdmittanceStamp` rule). Charging joins the
end shunts: `yShunt[from] += bsh/|tap|^2`, `yShunt[to] += bsh`, which is the shunt part of `Yff`, so the converged
voltages solve the same pi-plus-tap model as Newton. A breadth-first tree from the source orders the buses; every
in-service branch left out of that tree is a link (breakpoint).

**Start.** The source holds `|V| = VoltageSetpoint` of its generators (else its stored `Vm`; 1.0 if that is not
positive) at its stored `VaDeg`. Other buses start from the source voltage under `FlatStart`; otherwise they warm
start from their stored `Vm` and `VaDeg`, and a stored magnitude that is not positive and finite, or a non-finite
angle, throws `InvalidDataException`.

**One sweep.**

1. Node currents: `I_i = conj((S_draw_i + ZIPdeviation_i - j*qExtra_i) / V_i) + yShunt_i * V_i`; when `|V_i|` falls
   below `SafetyFloor = 1e-6` the division uses the real number `SafetyFloor` instead of `V_i` (the bus angle is not
   kept for that bus's load current). The ZIP deviation is `(Pd*(PMultiplier(|V|) - 1) + j*Qd*(QMultiplier(|V|) - 1))/BaseMva`,
   the same deviation Newton subtracts. Breakpoint currents are added.
2. Backward (leaves to source): a bus's accumulated current is its node current plus each child's branch current,
   referred through the ideal transformer: `J_child / conj(tap)` when the bus is the tapped From end,
   `conj(tap) * J_child` when it is the To end.
3. Forward (source to leaves): child on the To side `V_to = V_from/tap - Z*J`; child on the tapped From side
   `V_from = tap*V_to - |tap|^2 * Z * J`, with `Z = 1/y`.
4. `maxDelta` is the largest per-bus `|V - V_previous|`.

**Weakly meshed feeders.** A loop matrix `M[p,q] = z_p*delta_pq - (V_from_p/tap_p - V_to_p)`, the closure voltage
caused by a unit current at link `q` with loads off and the source grounded, is built once by superposition (one
linear sweep per link). Each iteration computes the closure residual `V_from/tap - V_to - z*I` of each link and
adds `M^-1 * residual` to the link currents, solving the small complex system by Gauss-Jordan with partial
pivoting. A pivot of magnitude below `LoopPivotFloor = 1e-14` pu is skipped and its correction left at zero; the
residual test then keeps that loop open and the solve reports not converged. At most `MaxCompensatedLoops = 6`
links are accepted.

**Regulating generators.** A non-source `Pv` bus with at least one generator holds `|V|` at its setpoint
(`VoltageSetpoint`, last generator wins) by adjusting `qExtra` each sweep:

```
err   = Vset - |V|
qWant = (Qnom + qExtra) + err * |V| / Xth          Xth = max(|Im(source-to-bus path impedance)|, SafetyFloor)
qWant clamped to [Qmin, Qmax] / BaseMva (unlimited when EnforceQLimits is off);  qExtra = qWant - Qnom
```

A clamped bus is treated as a `Pq` bus at its limit. The clamp is continuous: a saturated bus leaves its limit as
soon as the voltage allows, which is the converged state of the Newton loop with `RevertQLimits` on. A `Pv` bus
with no generator is a `Pq` bus.

**Convergence** needs all of: `maxDelta < Tolerance`; every unclamped regulating bus within
`ctrlTol = max(Tolerance, MinControlTolPu)` of its setpoint (`MinControlTolPu = 1e-6`); every link residual below
`ctrlTol`. The cap is `MaxIterations`; `Cancellation` is checked each sweep; `MaxQLimitPasses` is only validated.
`Iterations` is the number of sweeps performed.

**Result.** The flows use the same pi-plus-tap stamps as Newton. `MaxMismatch` is the largest real-power mismatch
per non-slack bus (and reactive mismatch for buses that are not regulating) in per-unit, using the load drawn at
the solved voltage; for a non-converged run it is the larger of that mismatch and the last `maxDelta`. A
disconnected network (not every bus reached) or more than six links returns status `NonFinite`,
`Converged = false`, `MaxMismatch = infinity`, `Vm = 1` for every bus, zero injections, and one zero-flow
`LineFlow` per branch so the list stays index-aligned with `Branches`. The non-finite guard of Newton applies to
a swept result as well.

### Islands

The Newton engines do not partition the network. A split network needs `IslandPartition.Prepare`
(`src/GridSim.Core/PowerFlow/IslandPartition.cs`), which `TopologyOps.Apply` runs after marking opened branches
out of service; `LiveSim` calls it for recloser-opened branches. `Prepare` unions buses over in-service branches
(union-find) and handles each component:

| Component | Treatment |
|---|---|
| Has a `Slack` bus | Kept unchanged. |
| No slack, at least one bus with generation (`sum |Pg| > 0`) | The bus carrying the largest single `|Pg|` is promoted to `Slack`; ties go to the lowest bus index. |
| No slack and no such generation | Blacked out: its buses, branches and generators are dropped, and `LostLoadMw` and `LostGenMw` record what was lost. |

A model with one component and a slack is returned unchanged, so a fully connected model is untouched and solves
bit-identically. The `IslandReport` carries `Islands`, `EnergizedIslands`, `BlackedOutIslands`,
`ElectedSlackBuses`, `BlackedOutBuses`, `LostLoadMw` and `LostGenMw`. Kron reduction
(`src/GridSim.Core/PowerFlow/KronReduction.cs`) is not part of the power-flow solve; the transient initialisation
and domain coupling use it to eliminate non-generator buses.

## Engine selection

`SolverSelector` (`src/GridSim.Core/PowerFlow/SolverSelector.cs`) is the single rule for the dense, sparse or sweep
choice.

| Test, in order | Engine |
|---|---|
| `LooksRadial`: exactly one `Slack` bus, in-service branch count `== BusCount - 1`, and every bus reachable over the in-service branches | `DistributionSweep` |
| `BusCount > sparseThreshold` (`DefaultSparseThreshold = 0`) | `SparseNewton` |
| otherwise | `DenseNewton` |

With the default threshold of 0 every model with at least one bus is above it, so every meshed network, and every
radial one that the first test does not take, runs on the sparse engine. The threshold is strict: a caller that passes
200 gets dense for 200 buses and sparse for 201. A caller can pass its own threshold, and the dense engine is built
explicitly through `Create(SolverKind.DenseNewton, ...)` where a tool offers the choice.

`LooksRadial` counts only in-service branches, so a feeder with an open normally-open tie still routes to the sweep. It
tests connectivity with a breadth-first search from the first bus over the in-service branches: a model with `n - 1`
in-service branches that leaves a bus unreached (one island holds a loop while another bus hangs free), and a model with
an in-service branch that names a bus the model does not contain, are not radial and go to Newton, which reports its own
failure. A meshed transmission case fails the first test from `case9` upward. `ResolveNewton` is the same rule without
the radial test, for callers that always run Newton; `CreateNewton` builds that engine on the CPU;
`Create(kind, model, sparseThreshold)` resolves `Auto` and returns the engine, the resolved kind and a display label
(`Backward-forward sweep`, `Newton-Raphson (sparse)`, `Newton-Raphson (dense)`); `Auto` is never returned. The two
Newton engines are bit-identical on the CPU, so the threshold changes speed and memory, never a number. Weak-mesh
compensation in the sweep is reachable only by forcing the sweep (`gridsim <case> --bfs`, or
`SolverKind.DistributionSweep`), because the radial test requires exactly `n - 1` in-service branches.

The threshold of 0 comes from a re-bench by the S4 solver stream (median wall time per solve, Release, warm; dense / sparse
in ms, results bit-identical on every case): case9 0.247 / 0.209, case14 0.293 / 0.195, case30 0.806 / 0.547, case57
1.681 / 0.821, case118 7.954 / 2.477, case145 13.816 / 4.898, case300 80.815 / 7.348, case588_sdet 465.008 / 37.734,
case1354pegase 1852.764 / 59.151, gb-synthetic 3582.322 / 28.455. Sparse is faster at every size. The machine is not
recorded in the comment on `SolverSelector.DefaultSparseThreshold`.

`SolverSelector.DenseSparseCrossoverBuses` (200) is the crossover that three users keep: the GPU routing in
`SolverBackend.SparseThreshold` (on a GPU the dense engine is the device's dense LU, outside the CPU bit-identity
contract), the solve export (`SolverBackend.ExportSolver`, because only the dense engine records the Jacobian in the
trace) and the state estimator.

Callers of the rule:

- **CLI, with the radial test.** `SolverBackend.Solver(m)` (`Resolve` at `SolverBackend.SparseThreshold`, then the
  backend) serves the verbs that call it. `PickSolver` in `Program.cs`, which serves the `--once` report and the
  command-line live loop, tests `LooksRadial` itself (or takes `--bfs`) and otherwise calls `SolverBackend.Pick(m)`.
  `--bfs` forces the sweep in the bare, case and directory forms only. The solve export (`--enableExport`) uses
  `SolverBackend.ExportSolver(m)`: radial to the sweep, then dense up to 200 buses and sparse above, the 200 being
  `DenseSparseCrossoverBuses`; `--bfs` forces the sweep there too.
- **CLI, Newton only.** `SolverBackend.NewtonSolver(m)` (`ResolveNewton` at `SolverBackend.SparseThreshold`) serves the
  verbs that never want the sweep, for example `chain` and `losses`; `Pick(m)` is `SteadyStateSolver` on
  `ResolveNewton`, and serves `synth` and the `case9` report, which never routes to the sweep.
  `nminus1-sweep` builds its Newton engine per worker the same way.
- **CLI, own rule.** `gda --solve` takes `--dense` or `--sparse` when given and otherwise `backend.ResolveNewton(m)` on
  the whole model's bus count. `bench` builds each engine explicitly. `CliEngineChoiceScanTests` scans the CLI source and
  fails on any local dense-or-sparse threshold that does not carry a stated routing exemption.
- **Analyses and services.** With no solver supplied, `ContingencyAnalysis`, `LossContingency`,
  `AcOptimalPowerFlow`, `ContinuationPowerFlow`, `TapOptimizer` and `RestorationSequencer` use
  `CreateNewton(model)`; the protection cascade does the same; the parameterless `SteadyStateSolver()` (REST
  `/solve`, viz export, bench) picks per model at each solve with `CreateNewton`, so it never returns the sweep;
  the cycle engine's N-1 workers (`CycleRunner`, `ResidentCycleService`) use `Resolve` with default options.
- **State estimator.** It chooses its step backend with `EstimationOptions.SparseBusThreshold`, default
  `SolverSelector.DenseSparseCrossoverBuses` (200). Its dense and sparse backends are not bit-identical to each
  other ([ch. 6, State estimation](<0006 State estimation.md>)).

### Live simulation

`LiveSim` and `GdaReplayLoop` call `SolverSelector.Create` with `sparseThreshold: LiveSolveSettings.SparseThresholdBuses`
(1500; `src/GridSim.Core/Simulation/LiveSolveSettings.cs`). Up to 1500 buses the live solve runs the dense engine where
the shared rule would run sparse; that changes the engine only, not a number. When the resolved engine is `SparseNewton`, the live solve runs with
`MaxIterations = LiveSolveSettings.SparseMaxIterations` (40) and `EnforceQLimits = false`, and with no OLTC loop;
a swept feeder also runs no OLTC loop; a dense live model runs default options with `OltcPasses` =
`DefaultMaxOltcPasses` (25). Above 1500 buses the live base solve and the cycle's N-1 workers therefore solve
with different options (Limits).

## Results and status codes

`PowerFlowResult` (`src/GridSim.Core/PowerFlow/PowerFlowResult.cs`):

| Field | Meaning |
|---|---|
| `Status` | `Converged`, `MaxIterations` or `NonFinite` (below). Defaults to `Converged` for presolved states that carry only the boolean. |
| `Converged` | Both engines set it `true` only when `Status == Converged`; the type does not enforce this. Every other status means the state is untrustworthy. |
| `Iterations` | Newton steps summed over all reactive-limit passes (sweeps for the sweep engine). |
| `MaxMismatch` | The final `max|f|` in per-unit (sweep: see above). |
| `BusIds` | Result index to external bus id; the only way to map the arrays back to the model. |
| `Vm`, `VaDeg` | Solved voltage, pu and degrees. |
| `PInjMw`, `QInjMvar` | Net injection (generation minus demand) per bus. |
| `TotalLossMw`, `TotalLossMvar` | Sum of the net injections, which equals the network loss (it is negative in `TotalLossMvar` when line charging exceeds series reactive loss). |
| `LineFlows` | One `LineFlow` per branch, index-aligned with `Model.Branches`; an out-of-service branch has a zero-flow placeholder. |

| Status | Arises when |
|---|---|
| `Converged` | `max|f| < Tolerance` at a check, every solved value finite, and (Newton) the reactive-limit loop ended without a pending switch. |
| `MaxIterations` | The iteration cap was reached; or a step could not be solved and the state is finite; or the reactive-limit loop hit `MaxQLimitPasses` with a bus still switching. |
| `NonFinite` | `MaxMismatch`, `Vm`, `Va` or an injection is NaN or infinite; the sweep returns it for a topology it cannot solve. |

`LineFlow(From, To, PFromMw, QFromMvar, PToMw, QToMvar, PLossMw, QLossMvar, Name, RatingMva, LoadingPercent)`
(`NewtonShell.ComputeLineFlows`):

```
S_f = V_f * conj(Yff*V_f + Yft*V_t) * BaseMva         S_t = V_t * conj(Ytf*V_f + Ytt*V_t) * BaseMva
PLossMw = PFromMw + PToMw        QLossMvar = QFromMvar + QToMvar
LoadingPercent = max(|S_f|, |S_t|) / RateAMva * 100        (0 when RateAMva = 0)
```

`PFrom`/`QFrom` and `PTo`/`QTo` are the power flowing into the branch at each end, so a through-flow is positive
at one end and negative at the other. `From` and `To` are external bus ids. Charging makes `QLoss` negative on a
lightly loaded line.

## Parameters and defaults

`PowerFlowOptions` (`src/GridSim.Core/PowerFlow/PowerFlowOptions.cs`):

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| Convergence tolerance | `1e-8` | pu on `BaseMva` | Bound on `max|f|`; strict `<`. | `PowerFlowOptions.Tolerance` |
| Iteration cap | `30` | steps per pass | Newton iterations per reactive-limit pass; the mismatch is evaluated after the last allowed step, so a solve that reaches the tolerance on step 30 is converged. | `PowerFlowOptions.MaxIterations` |
| Flat start | `false` | - | Start voltages at 1.0 pu and angles at 0 (slack angle and generator-bus magnitudes excepted). | `PowerFlowOptions.FlatStart` |
| Enforce Q limits | `true` | - | Run the PV to PQ outer loop. | `PowerFlowOptions.EnforceQLimits` |
| Q-limit pass cap | `12` | passes | Outer-loop cap; below 1 throws when `EnforceQLimits` is on. | `PowerFlowOptions.MaxQLimitPasses` |
| Revert Q limits | `false` | - | Opt-in PQ to PV revert (MATPOWER `enforce_q_lims` style). | `PowerFlowOptions.RevertQLimits` |
| Trace | `null` | - | Opt-in `SolveTrace` capture of Y, spec, per-step mismatch, correction, Jacobian and Q-limit events; `null` has no overhead. | `PowerFlowOptions.Trace` |
| Cancellation | `None` | - | Checked at the top of every Newton iteration and every sweep; `None` never throws, so results are bit-identical with or without it. | `PowerFlowOptions.Cancellation` |

Constants:

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| Q-limit slack | `1e-3` | MVA | Margin when comparing generator Q to the aggregate limits. | `NewtonShell.QLimitTolMva` |
| Revert deadband | `1e-4` | pu | Voltage margin for a PQ to PV revert. | `NewtonShell.RevertVoltTolPu` |
| Largest undamped step | `0.4` | rad or pu | Cap on the largest correction component. | `NewtonShell.MaxUndampedStep` |
| Voltage floor | `0.2` | pu | Floor on `Vm` of a PQ bus after an update. | `NewtonShell.VmFloorPu` |
| Singular-pivot floor | `1e-12` x `max|A_ij|` | relative | A pivot at or below it is singular. | `NumericTolerances.RelativePivotFloor` |
| BiCGSTAB tolerance | `min(1e-10, Tolerance*1e-3)` | relative residual | Fallback solve convergence. | `NewtonShell.SolveStep` |
| BiCGSTAB cap | `4m + 200` | iterations | Fallback iteration cap. | `NumericTolerances.DefaultKrylovMaxIterations(m)` (a method) |
| OLTC pass cap | `25` | re-solves | Outer-loop cap. | `SteadyStateSolver.DefaultMaxOltcPasses` |
| Tap move epsilon | `1e-12` | ratio | A smaller move is no move. | `SteadyStateSolver.TapChangeEpsilon` |
| OLTC defaults | `TargetVpu 1.0`, `MinTap 0.9`, `MaxTap 1.1`, `StepPu 0.0125`, `DeadbandPu 0.01` | pu | Defaults of the `Oltc` record. | `Oltc` in `Model.cs` |
| Dense/sparse threshold | `0` | buses | `BusCount >` this selects sparse, so every CPU Newton solve is sparse. | `SolverSelector.DefaultSparseThreshold` |
| Dense/sparse crossover | `200` | buses | Kept by the GPU routing, the solve export and the state estimator. | `SolverSelector.DenseSparseCrossoverBuses` |
| Live threshold | `1500` | buses | Same rule in `LiveSim` and `GdaReplayLoop`. | `LiveSolveSettings.SparseThresholdBuses` |
| Live sparse iteration cap | `40` | steps | Sparse live solve, Q limits off. | `LiveSolveSettings.SparseMaxIterations` |
| Sweep loop cap | `6` | links | Largest compensated loop count. | `BackwardForwardSweep.MaxCompensatedLoops` |
| Sweep voltage floor | `1e-6` | pu | Floor on `|V|` in the current division and on path reactance. | `BackwardForwardSweep.SafetyFloor` |
| Sweep control tolerance floor | `1e-6` | pu | Regulation and loop tolerance is `max(Tolerance, this)`. | `BackwardForwardSweep.MinControlTolPu` |
| Sweep loop pivot floor | `1e-14` | pu (absolute) | Pivot of the small loop solve. | `BackwardForwardSweep.LoopPivotFloor` |
| Jacobian capture gate | `300` | buses | Export includes the Jacobian up to this size unless forced (dense engine only). | `SolveTrace.JacobianMaxN` |
| ZIP defaults | `Ap=Bp=Aq=Bq=0`, `Cp=Cq=1` | - | Constant power. | `ZipLoad` in `Model.cs` |

## Invariants

| Invariant | Test |
|---|---|
| Dense and sparse results are bit-identical on the eight built-in cases, 24 named `.m` files and three JSON folders the test lists, under four option sets (not listed: `gb/GBnetwork.m`, `gb/GBreducednetwork.m`, the `data/intl` maps and `data/dist3ph`; `case9241pegase` is left out for memory) | `DenseSparseParityTests.Built_in_case_is_bit_identical_dense_vs_sparse`, `Matpower_file_is_bit_identical_dense_vs_sparse`, `Json_case_is_bit_identical_dense_vs_sparse` |
| `MaxUndampedStep`, `VmFloorPu`, `RevertVoltTolPu` and the sweep's `SafetyFloor` equal their values, and the floor's effect is isolated | `PowerFlowStepConstantsPinTests` |
| A cached RCM ordering gives the same bits as a fresh one, across structure changes | `DenseSparseParityTests.Repeated_solves_on_one_sparse_instance_stay_bit_identical` |
| A non-finite input stops both engines on the first iteration with status `NonFinite`, identically | `DenseSparseParityTests.A_non_finite_input_stops_both_engines_at_once_with_the_same_failure` |
| A matrix singular to working precision returns `null` from the sparse LU at any scale, and the sparse and dense LU reject the same matrix | `NumericTolerancesTests.Sparse_LU_returns_null_for_a_matrix_singular_to_working_precision_at_any_scale`, `Sparse_and_dense_LU_reject_the_same_near_singular_matrix`, `The_shared_pivot_floor_is_relative_1e_minus_12` |
| Reaching the iteration cap reports `MaxIterations`, never a stale `Converged`; a solve that converges on the last allowed step is converged; dense and sparse still agree to the bit on a capped solve | `PowerFlowTests.Hitting_the_iteration_cap_reports_MaxIterations_not_converged`, `IterationCapTests.A_solve_that_converges_on_the_last_allowed_step_is_converged`, `Dense_and_sparse_still_agree_to_the_bit_on_a_capped_non_converged_solve` |
| Case 9 converges to the published voltages and losses; generation balances load plus losses; case 9 from a flat start converges to the published bus 9 voltage (3 decimals) | `PowerFlowTests.Case9_converges`, `Case9_bus_voltages_match_published_solution`, `Case9_slack_and_losses_match_known_values`, `Generation_balances_load_plus_losses`, `Line_losses_sum_to_total_losses`, `Flat_start_reaches_the_same_solution` |
| A PV bus holds its voltage inside its limits; at a limit it switches to PQ and pins Q; disabling enforcement lets Q exceed the limit | `QLimitTests.Pv_bus_holds_voltage_when_within_q_limits`, `Pv_bus_switches_to_pq_and_pins_q_at_the_limit`, `Disabling_enforcement_lets_q_exceed_the_limit` |
| Revert direction, deadband, off-by-default, genuinely binding limit and one revert per bus | `QLimitRevertTests.Forward_pin_switches_PV_to_PQ_at_Qmax`, `Pinned_bus_does_not_revert_when_option_is_off`, `Bus_pinned_at_Qmax_reverts_to_PV_when_voltage_overshoots_setpoint`, `Bus_pinned_at_Qmin_reverts_when_voltage_undershoots_setpoint`, `Pinned_bus_stays_PQ_when_the_limit_is_genuinely_binding`, `A_bus_reverts_at_most_once_no_oscillation` |
| The slack-reactive note fires on a breached aggregate limit and is quiet otherwise | `QLimitRevertTests.DiagnoseSlackQ_flags_a_breached_aggregate_limit_and_is_quiet_otherwise` |
| A Q-limit loop that hits its pass cap with a switch pending is not converged; a cap below 1 is refused | `ConvergenceContractTests.Q_limit_loop_that_hits_its_pass_cap_with_a_switch_pending_is_not_converged`, `A_q_limit_pass_cap_below_one_is_refused` |
| `QLimitTolMva` equals the physics-feasibility generator Q slack | `Ws13CoreConstantsAntiDriftTests.Physics_and_power_flow_constants_equal_the_literals_they_replaced` |
| A PV bus with no generator solves exactly as PQ on both engines | `PowerFlowPhysicsConsistencyTests.A_pv_bus_with_no_generator_solves_exactly_as_pq_on_both_engines` |
| The last generator at a bus sets the voltage setpoint, for Newton and the sweep alike | `PowerFlowPhysicsConsistencyTests.Voltage_setpoint_rule_is_last_generator_at_the_bus`, `Newton_and_sweep_hold_the_slack_at_the_same_setpoint_with_two_units`, `Newton_and_sweep_hold_a_pv_bus_at_the_same_setpoint_with_two_units` |
| Studies refuse a non-converged state and name the study | `ConvergenceContractTests.RequireConverged_names_the_study_and_the_state`, `Fault_analysis_refuses_a_non_converged_prefault`, `Protection_cascade_and_assessment_refuse_a_non_converged_state`, `Loss_contingency_refuses_a_non_converged_base_case` |
| OLTC loop settles and says so; never steps on a non-converged state; reports a hit pass cap as unsettled; no OLTC is settled | `ConvergenceContractTests.Oltc_loop_settles_and_says_so`, `Oltc_loop_never_steps_a_tap_on_a_non_converged_state`, `Oltc_loop_that_hits_its_pass_cap_reports_unsettled`, `A_model_without_oltcs_is_trivially_settled` |
| Tap direction by controlled end, and stop at the correct limit | `OltcFromSideTests` (all five tests) |
| A tap pinned at its limit with its bus out of band is not settled and is named in `TapsAtLimit`; a tap that runs to its limit during the loop is not settled; a tap with room to move settles in band and names nothing | `OltcTapAtLimitTests.A_tap_pinned_at_its_limit_with_the_bus_out_of_band_is_not_settled`, `A_tap_that_runs_to_its_limit_during_the_loop_is_not_settled`, `A_tap_with_room_to_move_settles_in_band_and_names_nothing` |
| The report totals use generator output (net injection plus load drawn, per generator bus), which balances demand plus losses and is not the positive-injection sum; a result from another model is refused | `SolvedGenerationTests.Generator_output_balances_demand_plus_losses`, `Case14_generator_output_is_not_the_positive_injection_sum`, `A_result_from_another_model_is_refused`, `CliDocsProgrammeFindingsTests.The_report_totals_use_generator_output_not_positive_injections` |
| The source contains the exit paths for a missing case file or folder (exit 1), a non-converged `case9` report (exit 3) and an explicit `--ticks` below 1 (a source-text check of `Program.cs` and `Gated.cs`) | `CliDocsProgrammeFindingsTests.A_missing_case_file_or_folder_exits_1_not_with_a_crash`, `A_non_converged_case9_report_keeps_exit_code_3`, `An_explicit_ticks_of_zero_or_less_is_refused` |
| The live dashboard zone table pairs only real lower-voltage neighbours and reproduces the GB table exactly | `LiveZoneTableTests` (all eight tests) |
| The OLTC loop is bit-for-bit the original loop; the pass cap and epsilon constants are fixed | `OltcLoopEquivalencePinTests.Refactored_loop_matches_the_original_bit_for_bit`, `Ws13CoreConstantsAntiDriftTests.Physics_and_power_flow_constants_equal_the_literals_they_replaced` |
| A constant-power default is bit-for-bit the unchanged solver; the solved load equals the ZIP polynomial at the solved voltage; dense and sparse agree on a ZIP case | `ZipLoadTests.Constant_power_default_is_bit_for_bit_the_unchanged_solver`, `The_solved_load_equals_the_zip_polynomial_at_the_solved_voltage`, `Dense_and_sparse_agree_on_a_zip_solution` |
| The sweep draws a ZIP load like Newton; the Q-limit check measures the load actually drawn; the physics audit of an exactly solved ZIP case holds | `PowerFlowPhysicsConsistencyTests.Sweep_solves_a_zip_load_like_newton`, `Q_limit_check_measures_the_generator_against_the_zip_load_actually_drawn`, `Physics_audit_of_an_exactly_solved_zip_case_holds` |
| ZIP input validation fails loud (missing key, misspelt key, triple not summing to 1) | `ZipLoadWiringTests` |
| A default `Cancellation` changes no bit; a cancelled token aborts the solve; the sweep honours it | `SolverCancellationTests.Default_token_is_bit_for_bit_identical`, `Cancelled_token_aborts_the_solve`, `PowerFlowPhysicsConsistencyTests.Sweep_honours_cancellation` |
| The default threshold is 0 and routes every size to sparse; the 200-bus crossover keeps its rule for the estimator and the GPU; `CreateNewton` builds the resolved engine; the Newton rule never picks the sweep | `SolverRoutingTests.Newton_rule_switches_to_sparse_past_the_shared_threshold`, `The_old_crossover_keeps_its_rule_for_the_estimator_and_the_gpu`, `CreateNewton_builds_the_resolved_engine`, `Newton_rule_never_picks_the_sweep_even_for_a_radial_network` |
| No CLI verb chooses dense or sparse outside the shared selector, and the scan itself catches every local-threshold form and honours only reasoned exemptions | `SolverRoutingTests.No_local_dense_sparse_threshold_remains_in_the_cli`, `CliEngineChoiceScanTests.No_cli_verb_chooses_dense_or_sparse_outside_the_shared_selector`, `The_scan_catches_every_form_the_old_guard_missed`, `The_scan_honours_only_reasoned_exemptions` |
| `LooksRadial` is true for a tree and false for meshed case9, ignores an open tie, and tests connectivity (a disconnected model with `n - 1` in-service branches, an open branch as the only link, and a branch to an unknown bus are not radial) | `SolverSelectorTests.LooksRadial_is_true_for_a_tree_and_false_for_meshed_case9`, `Resolve_routes_radial_to_sweep_meshed_to_dense_and_large_to_sparse`, `PowerFlowPhysicsConsistencyTests.LooksRadial_ignores_an_open_tie`, `LooksRadialConnectivityTests` (all four tests) |
| The sweep matches dense Newton on `cigre_mv` to 1e-3 pu, solves `case33bw` to the published solution, and declines a meshed case | `DistributionSolverTests.Bfs_matches_dense_solver_on_cigre_mv`, `Bfs_solves_case33bw_to_published_solution`, `Bfs_reports_nonconvergence_on_a_meshed_transmission_case` |
| The sweep keeps tap phase shift, the slack angle, warm and flat start agreement, and index-aligned `LineFlows` on failure | `PowerFlowPhysicsConsistencyTests.Sweep_keeps_the_phase_shift_of_a_tap_zero_branch`, `Sweep_holds_the_slack_at_its_own_angle`, `Sweep_warm_and_flat_starts_reach_the_same_solution`, `Sweep_failure_keeps_line_flows_index_aligned_with_branches` |
| Island pre-pass: no-op when fully connected, elects a slack for a generating island, blacks out a dead island, and the adjusted model solves | `IslandPartitionTests` (all four tests) |
| The trace captures Y, spec, iterations and Jacobian; the Jacobian is size-gated; the export is deterministic | `SolveExportTests.Dense_trace_captures_ybus_spec_iterations_and_jacobian`, `Jacobian_is_gated_by_size_and_forceable`, `Export_is_deterministic` |

## Worked example

Both traces come from `--enableExport`, which solves once on the raw engine (no OLTC loop) with
`MaxIterations = 100` and `EnforceQLimits = true` and writes the schema `gridsim-solve-export/1`, with one entry
per Newton step in `solve.passes[].iterations[]`. The run below is `gridsim case14 --once`
(`docs/_captures/power-flows/case14-once.txt`), excerpt:

```
$ gridsim case14 --once
GridSim  -  IEEE 14-bus
14 buses  20 branches  5 generators  base 100 MVA  50 Hz
========================================================================
Converged (2 NR iters, 0 OLTC passes, max mismatch 1.32E-010 p.u.)
Generator output 0.27 GW  Demand 0.26 GW  Losses 13.4 MW  Inertia not in the data (no generator H)
```

### case14, dense engine, case-file start

Slack bus 1; PV buses 2, 3, 6, 8; PQ buses 4, 5, 7, 9 to 14. So `npvpq = 13`, `npq = 9`, `m = 22`, and the Jacobian
is 22 by 22. Rows 0 to 12 are the P equations of buses 2 to 14 and rows 13 to 21 the Q equations of buses 4, 5,
7, 9, 10, 11, 12, 13, 14. One reactive-limit pass; no bus switched.

| Newton step | `max|f|` before the step (pu) | Row of the maximum | Largest correction |
|---|---|---|---|
| 0 | `4.218284e-02` | Q of bus 4 | `Vm` of bus 4: `-1.326882e-03` pu |
| 1 | `5.669102e-05` | Q of bus 4 | `Vm` of bus 4: `-2.264672e-06` pu |
| convergence test | `1.315749e-10` | - | none; `1.3157e-10 < 1e-8`, the loop stops |

`Iterations = 2`, `MaxMismatch = 1.3157486905557292e-10`. Convergence is quadratic: `5.669e-5 / (4.218e-2)^2 = 0.032`
and `1.3157e-10 / (5.669e-5)^2 = 0.041`. The largest corrections are far below `MaxUndampedStep = 0.4`, so
`damp = 1.0` on both steps. At the converged point bus 4 injects `P = -47.80000000074846` MW against a spec of
`-47.8` and `Q = 3.900000013157487` MVAr against `3.9` (the spec is positive because the case stores a reactive
load of `-3.9` MVAr); the Q difference `1.3157487e-8` MVAr is exactly `1.3157e-10` pu on 100 MVA. The slack
injects `P = 232.39327235325797` MW and `Q = -16.549300542088847` MVAr; total loss is `13.393272357432002` MW and
`8.937543870467078` MVAr. Check by hand: slack plus the 40 MW unit minus the 259 MW demand is 13.393 MW. The
export's `slackQNote` reads `Slack bus 0: reactive output -16.55 MVAr below aggregate Qmin 0.00 MVAr.` because
the case stores `Qmin = 0` for the slack machine; the slack is never limited, so the note is diagnostic only.

### case118, dense engine, reactive-limit passes

`gridsim case118 --once --enableExport` (`docs/_captures/power-flows/case118-export.txt`) prints
`[export] dense-newton: converged=True iters=6 maxMis=2.85E-014`. The trace splits into two passes:

| Pass | System size `m` | Step | `max|f|` before the step (pu) |
|---|---|---|---|
| 0 | 181 (117 P rows, 64 Q rows) | 0 | `1.296780e+00` |
| 0 | | 1 | `2.418409e-02` |
| 0 | | 2 | `9.538510e-06` |
| 1 | 187 (117 P rows, 70 Q rows) | 0 | `3.542236e-01` |
| 1 | | 1 | `3.306692e-03` |
| 1 | | 2 | `3.255106e-07` |

After pass 0 converged (the next evaluation was below `1e-8`), `ApplyQLimits` switched six PV buses to PQ in one
call: buses 19, 32, 34, 92 and 105 pinned at `Qmin`, bus 103 pinned at `Qmax`. That is `m = 181 + 6 = 187` in pass
1, whose start mismatch is `0.354` pu because the pinned Q values differ from the pass-0 solution. Pass 1
converged with `max|f| = 2.851406207206033e-14`, no bus switched, and the loop stopped after two of the twelve
permitted passes. `Iterations = 3 + 3 = 6`; total loss is `132.48074930437622` MW and `-644.0822370596604` MVAr
(line charging exceeds series reactive loss).

## Source map

| File | Type or function | Responsibility |
|---|---|---|
| `src/GridSim.Core/PowerFlow/NewtonRaphsonPowerFlow.cs` | `NewtonRaphsonPowerFlow` | Dense assembly (`ComputeInjections`, `BuildJacobianInto`, `CopyIntoPattern`), pass loop, reactive-limit loop. |
| `src/GridSim.Core/PowerFlow/SparseNewtonRaphsonPowerFlow.cs` | `SparseNewtonRaphsonPowerFlow` | CSR Y (`BuildY`), Jacobian pattern, slot map and refill, pass loop, reactive-limit loop. |
| `src/GridSim.Core/PowerFlow/NewtonShell.cs` | `NewtonShell` | Shared pure functions: bus spec, equation numbering, `SolveStep`, `ApplyStep`, ZIP terms, `ApplyQLimits`, `DiagnoseSlackQ`, `AssembleResult`, `ComputeLineFlows`; the damping, floor and Q-limit constants. |
| `src/GridSim.Core/PowerFlow/CpuSparseLuSolver.cs` | `CpuSparseLuSolver` | Canonical CPU linear solve with cached RCM ordering. |
| `src/GridSim.Core/Numerics/SparseLu.cs`, `SparseLuFactorization.cs` | `SparseLu`, `SparseLuFactorization` | RCM ordering and Gilbert-Peierls LU with partial pivoting. |
| `src/GridSim.Core/Numerics/Sparse.cs` | `CsrMatrix`, `ComplexCsr`, `Ilu0`, `BiCgStab` | Sparse structures, fallback preconditioner and Krylov solver. |
| `src/GridSim.Core/Numerics/NumericTolerances.cs` | `NumericTolerances` | Singular-pivot floor, Krylov cap and tolerance. |
| `src/GridSim.Core/PowerFlow/BackwardForwardSweep.cs` | `BackwardForwardSweep` | Radial and weakly meshed sweep, PV regulation, loop compensation. |
| `src/GridSim.Core/PowerFlow/SteadyStateSolver.cs` | `SteadyStateSolver` | OLTC outer loop, `RegulatedModel`, `TapsSettled`, `OltcPasses`. |
| `src/GridSim.Core/PowerFlow/SolverSelector.cs` | `SolverSelector`, `SolverKind` | Engine choice and construction. |
| `src/GridSim.Core/PowerFlow/DistributedSlackPowerFlow.cs` | `DistributedSlackPowerFlow` | Opt-in wrapper that shares the slack by participation factors (`pf --distributed-slack`). |
| `src/GridSim.Core/PowerFlow/OltcLoopPowerFlow.cs`, `SwitchedShuntPowerFlow.cs` | `OltcLoopPowerFlow`, `SwitchedShuntPowerFlow` | Opt-in wrappers for discrete switched shunts (`pf --switched-shunts`). |
| `src/GridSim.Core/PowerFlow/PowerFlowOptions.cs` | `PowerFlowOptions` | Solve options. |
| `src/GridSim.Core/PowerFlow/PowerFlowResult.cs` | `PowerFlowResult`, `PowerFlowStatus`, `LineFlow` | Result and status. |
| `src/GridSim.Core/PowerFlow/ConvergenceContract.cs` | `ConvergenceContract`, `NonConvergedStateException` | Refusal of a non-converged state by dependent studies. |
| `src/GridSim.Core/PowerFlow/YBus.cs`, `AdmittanceStamp.cs` | `YBus`, `AdmittanceStamp` | Dense Y-bus and the shared branch stamp. |
| `src/GridSim.Core/PowerFlow/LoadAtVoltage.cs` | `LoadAtVoltage` | Load drawn at a voltage. |
| `src/GridSim.Core/PowerFlow/VoltageSetpoint.cs` | `VoltageSetpoint` | Last-generator-wins setpoint. |
| `src/GridSim.Core/PowerFlow/IslandPartition.cs`, `TopologyOps.cs` | `IslandPartition`, `TopologyOps`, `IslandReport` | Island election and blackout. |
| `src/GridSim.Core/PowerFlow/KronReduction.cs` | `KronReduction` | Star-mesh reduction for dynamics; not part of the solve. |
| `src/GridSim.Core/PowerFlow/DenseLuSolver.cs` | `DenseLuSolver` | Dense LU kept as a seam value; the dense engine replaces it with the canonical step. |
| `src/GridSim.Core/Simulation/LiveSolveSettings.cs` | `LiveSolveSettings` | Live threshold and options. |
| `src/GridSim.Core/Diagnostics/SolveTrace.cs`, `src/GridSim.Core/IO/SolveExport.cs` | `SolveTrace`, `SolveExport` | Opt-in capture and the JSON export. |
| `src/GridSim.Cli/SolverBackend.cs` | `SolverBackend` | CLI engine and GPU backend selection. |

## Limits

- In the engines the slack bus is never limited or reassigned; the distributed slack is a separate opt-in wrapper.
  `SlackQNote` reports a breach and changes nothing.
- The Newton engines do not detect islands. A network with a slack-less island must go through
  `TopologyOps`/`IslandPartition`, which only `LiveSim` calls; the CLI and the analyses solve the model they are
  given.
- GPU backends are outside bit-identity. The CPU dense and sparse engines are pinned; a GPU solve is an independent
  cross-check.
- Cross-CPU equality (AVX-512, AVX2, non-FMA) holds by construction: the Newton path calls no tier-dispatched kernel
  and no fused multiply-add. The trigonometry is `DetMath` (fdlibm), not the platform libm, so the operating system
  does not enter either. No single test compares two machines; the CI workflow runs the same exact-value pins on
  Windows, Linux and macOS and in each SIMD mode, and every leg must pass.
- Weak-mesh loop compensation in the sweep has no test: the five tie lines of `case33bw` are out of service in the
  file, so the solved network is a tree, and `cigre_mv` is radial. The sweep's agreement with Newton is tested to `1e-3` pu on `cigre_mv` only, and the two
  algorithms are not bit-identical.
- The 200-bus crossover that the GPU routing, the solve export and the state estimator keep was measured on
  2026-07-14 against the dense engine's earlier own LU; it has not been re-measured with the shared sparse-LU step.
- The bench record behind the threshold of 0 does not name its machine.
- No bundled case carries a ZIP load (`ZipLoadWiringTests.No_bundled_json_case_carries_a_zip_load`), so the ZIP
  paths are exercised by synthetic cases only.
- Above 1500 buses the live base solve (40 iterations, no Q limits, no OLTC loop) and the cycle's N-1 workers
  (default options) solve with different options (R147).

## See also

- [ch. 2, Data model and per-unit conventions](<0002 Data model and per-unit conventions.md>) - the fields the solve consumes, per-unit conventions and the Y-bus stamp.
- [ch. 4, Sparse and GPU numerics](<0004 Sparse and GPU numerics.md>) - the sparse LU, ILU(0)/BiCGSTAB and the GPU backends behind each Newton step.
- [ch. 8, Provenance and invariants](<0008 Provenance and invariants.md>) - the bit-identity contract and its evidence across CPU tiers.
- [ch. 6, State estimation](<0006 State estimation.md>) - the estimator that reuses `ComputeInjections` for its measurement model.
- [ch. 1, Architecture](<0001 Architecture.md>) - where solves sit in the data flow.
- [Manual ch. 5, Running power flows](<../manual/0005 Running power flows.md>) - driving these engines from the CLI.
- [Manual ch. 30, Troubleshooting](<../manual/0030 Troubleshooting.md>) - reading non-convergence.
