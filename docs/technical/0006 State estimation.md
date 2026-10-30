# 6. State estimation

The state estimator fits a network state to redundant, noisy telemetry. Given measurements `z = h(x) + e`, it finds the
state `x^` that minimises the weighted sum of squared residuals `J(x) = sum_i (z_i - h_i(x))^2 / sigma_i^2`. A power flow
([ch. 3](<0003 Power flow.md>)) needs a complete, exactly determined P/Q specification at every bus. The estimator
takes whatever measurements exist, wherever they are, and uses redundancy to average the noise out. It tests whether the
fit is consistent with the declared sigmas, finds and removes gross errors, and reports honestly when the measurements
cannot determine the state.

This chapter specifies everything in `src/GridSim.Core/Estimation/`: the state vector and measurement model (`h(x)` and
its Jacobian for all eight measurement kinds), the Gauss-Newton loop with trust-capped steps, the estimation
methods (WLS, Huber, exact LAV and LAV-IRLS, Schweppe-GM, QR, Constrained, Feasible), the dense and sparse step backends, observability
analysis, the chi-square consistency test, bad-data identification by largest normalised residual (LNR) and by
hypothesis testing (HTI), the false-data-injection screen, noise-floor analysis, and the measured-injection prior, the
phase-angle feed and the synthetic sampler. The recursive filters in `Estimation/Dse/` get one paragraph and a pointer to
[ch. 18](<0018 Real-time, distribution and dynamic estimation.md>). The Feasible method's physical-law audit is
specified in [ch. 16](<0016 Physics conformance and the constrained estimator.md>); this chapter specifies its solve. The CLI surface is the
`estimate` verb: see the user chapter [21. State estimation](<../manual/0021 State estimation.md>) and
[the `estimate` section of the CLI reference](<../manual/0019 CLI reference.md#estimate>). The design record for the
leverage-aware robust estimator is [design/robust-gm-leverage.md](design/robust-gm-leverage.md).

## Model

### State vector

The state is `x = [Va at every non-slack bus (radians); Vm at every bus (pu)]` in model bus order
(`MeasurementModel`, `src/GridSim.Core/Estimation/MeasurementModel.cs`). Slack buses keep their reference angle
throughout, so

```
StateSize s = (n - slackCount) + n
angle column of bus i  : AngleColumn(i)  = rank of i among non-slack buses, or -1 for a slack
Vm column of bus i     : VmColumn(i)     = AngleCount + i            (AngleCount = n - slackCount)
```

These are the conventions of the Newton power flow, so an estimate is directly comparable with a solver result.
`StateBusIndex(col, out isAngle)` maps a column back to its bus.

### Measurements

A `Measurement` (`Measurement.cs`) is `(Kind, Value, Sigma, Bus, From, To, End, Circuit, Source)`. Bus measurements
address the bus by id, not by solver index. Branch measurements address the branch by its `(From, To)` endpoints
in the orientation stored in the model, plus `Circuit` to separate parallel circuits: the branch name, or the ordinal
form `"#k"` where `k` is the model branch index. `End` selects the metered terminal. `Source` is a free tag
(default `"scada"`); four tags carry meaning in `estimate`. The estimator reads three (the third, `prior-solved`, below): `virtual-zero-injection` (hard
equality for the Constrained and Feasible methods) and `removal-backfill-prior` (never removable).
`pseudo-voltage-schedule` is read by the CLI's `--prior solve` step (`ApplyVoltagePrior`), which replaces those rows and
writes the solved profile back as `prior-solved`; the estimator reads `prior-solved` only to leave those rows out of the
chi-square test (their residuals out of J, the rows out of the degrees of freedom, since they add
no independent evidence; see the measured-injection prior below).

Values travel in engineering units and are converted to per-unit once, when `MeasurementModel` is constructed. Every
equation below is in per-unit, and `w_i = 1 / sigma_i^2` uses the per-unit sigma.

| Kind | `h(x)` | Conversion at ingestion (value and sigma) |
|---|---|---|
| `VmPu` | `Vm[bus]` | none |
| `VaDeg` | `Va[bus]` | x pi/180 (radians) |
| `PInjMw` | net real injection at the bus | / `BaseMva` |
| `QInjMvar` | net reactive injection at the bus | / `BaseMva` |
| `PFlowMw` | real flow into the branch at the metered end | / `BaseMva` |
| `QFlowMvar` | reactive flow into the branch at the metered end | / `BaseMva` |
| `IMagPu` | `sqrt(P^2 + Q^2) / Vm_end` at the metered end | none |
| `IAngDeg` | `Va_end - atan2(Q, P)` at the metered end | x pi/180 (radians) |

The equations as implemented (`EvaluateH`, `EvaluateJacobian`). Injections sum over the electrical neighbourhood of bus
`i`, which is `i` itself and every bus joined to it by an in-service branch:

```
th_ik = Va_i - Va_k
P_i = Vm_i * sum_k Vm_k * (G_ik * cos(th_ik) + B_ik * sin(th_ik))
Q_i = Vm_i * sum_k Vm_k * (G_ik * sin(th_ik) - B_ik * cos(th_ik))
```

A flow at end `a` of a branch towards end `b` uses the branch pi-model stamps of `AdmittanceStamp.Branch` (tap ratio and
phase shift included): `y_aa = g_aa + j b_aa` is `yff` for the from end or `ytt` for the to end, and
`y_ab = g_ab + j b_ab` is `yft` or `ytf`:

```
th = Va_a - Va_b
P = g_aa * Vm_a^2 + Vm_a * Vm_b * (g_ab * cos(th) + b_ab * sin(th))
Q = -b_aa * Vm_a^2 + Vm_a * Vm_b * (g_ab * sin(th) - b_ab * cos(th))
```

A branch that is out of service is stamped with zero admittance, so its flow rows predict zero. Injection rows already
exclude it.

Jacobian partials, with `k != i` for the mutual terms and the diagonal terms using `G_ii`, `B_ii` and the evaluated
`P_i`, `Q_i`:

```
injection P_i :  dP/dVa_k = Vm_i Vm_k (G_ik sin th_ik - B_ik cos th_ik)
                 dP/dVa_i = -Q_i - B_ii Vm_i^2
                 dP/dVm_k = Vm_i (G_ik cos th_ik + B_ik sin th_ik)
                 dP/dVm_i = P_i / Vm_i + G_ii Vm_i
injection Q_i :  dQ/dVa_k = -Vm_i Vm_k (G_ik cos th_ik + B_ik sin th_ik)
                 dQ/dVa_i = P_i - G_ii Vm_i^2
                 dQ/dVm_k = Vm_i (G_ik sin th_ik - B_ik cos th_ik)
                 dQ/dVm_i = Q_i / Vm_i - B_ii Vm_i
flow P        :  dP/dVa_a = Vm_a Vm_b (-g_ab sin th + b_ab cos th),   dP/dVa_b = -dP/dVa_a
                 dP/dVm_a = 2 g_aa Vm_a + Vm_b (g_ab cos th + b_ab sin th)
                 dP/dVm_b = Vm_a (g_ab cos th + b_ab sin th)
flow Q        :  dQ/dVa_a = Vm_a Vm_b (g_ab cos th + b_ab sin th),    dQ/dVa_b = -dQ/dVa_a
                 dQ/dVm_a = -2 b_aa Vm_a + Vm_b (g_ab sin th - b_ab cos th)
                 dQ/dVm_b = Vm_a (g_ab sin th - b_ab cos th)
Vm row        :  1 in the Vm column of the bus
Va row        :  1 in the angle column of the bus (no entry at a slack)
|I| row       :  d|I|/dx = (P dP/dx + Q dQ/dx) / (S Vm_a) - (S / Vm_a^2) dVm_a/dx,  S = sqrt(P^2 + Q^2)
                 empty row when S <= ZeroFlowMagnitudePu (1e-12)
ang(I) row    :  d/dx = dVa_a/dx - (P dQ/dx - Q dP/dx) / (P^2 + Q^2)
                 only the dVa_a term when P^2 + Q^2 <= ZeroFlowMagnitudeSquaredPu (1e-24)
```

Four properties are load-bearing.

- **One evaluation path.** `MeasurementModel.EvaluateJacobian<TSink>` writes every partial into an `IJacobianSink`
  (`JacobianSinks.cs`): `DenseJacobianSink` fills a dense array, `SparseJacobianRows` collects per-row lists. The dense and
  sparse backends call the same method, so their Jacobians cannot drift apart. Each (row, column) is emitted at most once.
- **Neighbourhood injections.** `ComputeInjectionsSparse` sums only over the neighbour list, at O(nnz) cost instead of
  O(n^2). The skipped terms are exact structural zeros and adding `0.0` is exact in IEEE 754, so the injections equal
  `NewtonRaphsonPowerFlow.ComputeInjections` to the bit.
- **Angle rows are linear and wrapped.** `VaDeg` has a single constant Jacobian entry. For both angle kinds
  `EvaluateH` folds `h` toward `z` (`h = z - IEEERemainder(z - h, 2 pi)`), so every residual `z - h` lies in
  `[-pi, pi]`; the Jacobian is unchanged. A `VaDeg` row on a slack bus has no state column: it is structurally empty,
  a constant residual, legal and uninformative.
- **Structural pattern.** `StructuralColumns(row)` lists every column a row can ever write, independent of the
  operating point: a `VmPu` row one column; a `VaDeg` row its angle column (none at a slack); an injection row the
  angle and Vm columns of the whole neighbourhood; a flow or current row the angle and Vm columns of its two ends,
  four at most. The sparse backend builds its gain pattern from this once per pass.

### Measurement set

`MeasurementSet` (`MeasurementSet.cs`) is the versioned input document: schema `gridsim-measurements/1`, camelCase
JSON, enums by name, fields `schema`, `case`, `baseMva` (default 100), optional `takenUtc`, `note` and `measurements`.
`Load` rejects any other schema and checks `takenUtc` against the historical horizon (`ForwardInferenceGuard`, a 169 hour
minimum age; see [ch. 8](<0008 Provenance and invariants.md>)). `Validate(model)` throws one
`InvalidDataException` that names every offender: a non-finite value, a sigma that is not finite and positive, an
unknown bus id, a branch reference that resolves to no branch or to several parallel circuits without a `Circuit`, and a
`baseMva` that differs from the model's by more than `BaseMvaMatchToleranceMva` (1e-9 MVA). A NaN or zero-weight row
therefore cannot reach the gain matrix. `WlsStateEstimator.Estimate` calls `Validate` first.

Branch resolution (`TryResolveBranch`) matches the stored orientation only. An ordinal `"#k"` circuit must name a branch
whose endpoints equal `(From, To)`. Otherwise the branch must be the unique `(From, To)` match, or the unique match with
that name.

### Start point

`InitialState` builds `(Vm, Va)`:

| Option | Vm | Va |
|---|---|---|
| default (`FlatStart` true, no `WarmStart`) | 1.0 | slack reference angle (the first slack bus's `VaDeg`) |
| `FlatStart` false | the bus's stored `Vm` | the bus's stored `VaDeg` |
| `WarmStart` (a `PowerFlowResult`) | the warm state's `Vm`, matched by bus id | the warm state's angle, matched by bus id |

A bus missing from the warm state gets `Vm = 1` and the reference angle. A slack bus always keeps its own fixed angle.
The start state is also cloned as the backfill prior.

## Algorithm

### The estimation loop

`WlsStateEstimator.Estimate` owns the loop: start point, trust-capped damping, convergence, chi-square consistency and
bad-data removal. Everything that depends on how the normal equations are represented lives behind the internal
`IWlsStep` seam (`WlsStep.cs`):

| `IWlsStep` member | Responsibility |
|---|---|
| `Begin(model, mm, w)` | bind to a measurement set and weights; allocate buffers and do structural work, once per bad-data pass |
| `RebindWeights(w)` | new weights for the same measurement set (the IRLS outer loop); throws before `Begin` or on a weight array of the wrong length |
| `GateObservability(vm, va)` | the observability verdict at the start state |
| `TrySolve(vm, va, dx)` | the undamped Gauss-Newton correction; false when the system is singular |
| `NormalizedResiduals(vm, va, r)` | residual-covariance diagonals: normalised residuals and criticality flags |

The loop, as implemented:

```
x = start state;  prior = copy of x;  active = all measurements;  removed = []
for pass = 0, 1, 2, ...:
    mm = MeasurementModel(model, active);   w_i = 1 / sigma_i^2;   wEff = copy of w (robust methods) or w
    step.Begin(model, mm, wEff)
    obs = step.GateObservability(x)
    if not obs.Observable:  status = Unobservable;  stop
    for outer = 0 .. maxOuter - 1:                 (maxOuter = 50 for Huber and Schweppe-GM, 10 for LAV-IRLS; 1 otherwise; Lav runs its own LP loop)
        if outer > 0:  step.RebindWeights(wEff)
        for inner = 0 .. MaxIterations - 1:
            if not step.TrySolve(x, dx):  status = Infeasible (Feasible method with an observable set) or Unobservable;  break
            apply the trust-capped step (below);  record it in the trace
            if any state is not finite:  status = NonFinite;  break
            if maxUpdate < Tolerance and (scale = 1 or max|dx| < Tolerance):  status = Converged;  break
        if status != Converged or method is not robust:  break
        re-derive wEff from the converged residuals (IRLS, below);  if the largest change < 1e-3:  break
    if status != Converged:  stop
    if DetectBadData is false or mChi - s <= 0:  stop (chi-square not run)   (mChi: rows other than prior-solved)
    if method is robust:  J against the declared weights, chi-square test, stop (no removal loop)
    J = sum_i w_i (z_i - h_i)^2;  threshold = chi-square(mChi - s, ChiSquareConfidence);  passed = J <= threshold
    if passed and LeanResiduals:  stop
    (rN, critical) = step.NormalizedResiduals(x, r)
    if passed:  stop
    worst = argmax rN over rows that are not critical, not backfill rows, finite, and above the threshold (3.0)
    if there is none or removed.Count >= MaxBadDataRemovals:  stop (failure is reported, not force-fitted)
    remove the worst row (optionally backfill it), record it with this pass number;  continue with the next pass
```

`Iterations` counts every Gauss-Newton step across all inner loops, outer passes and bad-data passes, and counts the
step that met the tolerance. `EstimationStatus` is one of `Converged`, `MaxIterations`, `NonFinite`, `Unobservable` or,
for the Feasible method only, `Infeasible`. `Converged` is true for the first value only. A singular system
mid-iteration, after the gate passed at the start point, means the state wandered somewhere the measurements no longer
pin down: it is reported as `Unobservable`, never as NaN. For the Feasible method the gate is run again after the
failure; if it still passes, the singularity comes from the constraint block and the result is `Infeasible`.

If the observability gate fails on a bad-data pass after the first, the result keeps the chi-square values of the
previous pass. Read `Status` before reading the chi-square fields.

### Gauss-Newton step and trust cap

Each iteration linearises `h` at the current state and solves for the correction:

```
r  = z - h(x)                                  (per-unit residual)
H  = dh/dx at x                                (m x s)
W  = diag(w_i),  w_i = 1 / sigma_i^2
G  = H^T W H                                   (gain matrix, s x s)
g  = H^T W r
G dx = g                                       (normal equations; DenseWlsStep, SparseWlsStep)
```

`QrWlsStep` solves the same least-squares problem without forming `G`:

```
A = sqrt(W) H,   b = sqrt(W) r,   dx = argmin || A dx - b ||_2     (Householder QR, Numerics/HouseholderQr)
```

Forming `G` squares the condition number of `A`, so QR is the choice when tight PMU or virtual weights sit beside loose
pseudo-measurements.

`ApplyStep` then scales the whole correction by one factor `t` and updates the state:

```
t = min(1,  min over columns c of  cap_c / |dx_c| )      cap_c = MaxAngleStepRad (0.2 rad) for angles, MaxVmStepPu (0.1 pu) for Vm
x <- x + t * dx
```

The direction is preserved. The caps do not bind near the optimum, so converged answers are unaffected; they stop a
stressed start from overshooting a voltage magnitude through zero. `maxUpdate = max_c |t dx_c|` and
`EstimationTrace` records `maxUpdate` and `t` per iteration and pass (`EstimationOptions.Trace`; the export carries it as
an additive block).

For the Feasible method `ApplyStep` adds the interior guard. For every Vm column whose scaled step `t dx_c` is negative:

```
gap = Vm_bus - VmFloor
if gap <= 0:                          t = 0  (freeze; stop scanning)
else if -t dx_c > FractionToBoundary * gap:   t = min(t, FractionToBoundary * gap / (-dx_c))
after the update:  Vm_i = max(Vm_i, VmFloor) for every bus
```

Convergence is `maxUpdate < Tolerance` (1e-8) and, whenever the guard or cap has scaled the step (`t < 1`), also
`max_c |dx_c| < Tolerance`. A step that the guard shrinks to a small size or to zero therefore cannot pass for
convergence.

### Methods and step backends

`EstimationOptions.Method` selects one of nine `EstimationMethod` members; every one has a CLI name
(`estimate --method`), and any other value fails loud with the accepted list.

| `EstimationMethod` | CLI `--method` | Step backend | Loop |
|---|---|---|---|
| `Wls` (default) | `wls` | `CanonicalWlsStep` on the CPU: the `SparseWlsStep` solve at every size, with the dense observability report when `n <= SparseBusThreshold`; `DenseWlsStep` only with a non-CPU `ILinearSolver` | Gauss-Newton, then chi-square and LNR removal |
| `Huber` | `huber` | as `Wls` | IRLS outer loop, no removal |
| `Lav` (alias `LavLp`) | `lav` (alias `lav-lp`) | `LavLinearProgram` (dense basis); refused above `DenseEstimationLimits.StateCovarianceMaxState` states | successive linear programming, exact LAV optimum, no removal |
| `LavIrls` | `lav-irls` | as `Wls` | IRLS outer loop, approximate LAV, no removal |
| `SchweppeGm` | `schweppe-gm` (alias `gm`) | as `Wls` | IRLS on the normalised residual, no removal |
| `Qr` | `qr` | `QrWlsStep` (dense) | as `Wls`; throws `NotSupportedException` above 3,000 buses |
| `Constrained` | `constrained` | `ConstrainedDenseWlsStep` (dense KKT) | as `Wls`; throws above 3,000 buses |
| `Feasible` | `feasible` (alias `constrained-physics`) | `FeasibleWlsStep` (dense KKT with active set) | as `Constrained`, plus interior guard and acceptance gate; throws above 3,000 buses |

`Feasible` validates `EstimationOptions.Feasibility` (`FeasibilityConstraints.Validate`) before solving and fails loud on
an empty envelope.

#### The canonical CPU step (`CanonicalWlsStep`)

On the CPU (the estimator's linear solver is the default `DenseLuSolver`) every `Wls`, `Huber`, `LavIrls` and
`SchweppeGm` estimate is solved by `CanonicalWlsStep`: its Gauss-Newton solve and its normalised residuals are the
`SparseWlsStep` arithmetic (below) at every size, so an estimate does not depend on which side of any bus-count
threshold the case falls. `SparseBusThreshold` (`SolverSelector.DenseSparseCrossoverBuses`, 200 buses) now selects only
the observability report: at or below it the start-point gate is the dense gain-rank proof of `DenseWlsStep`, above it
the sparse diagnosis. The gate decides only whether the estimate runs and what the report names; it does no arithmetic
of the estimate. `CanonicalWlsParityTests.The_estimate_is_bit_identical_across_the_dense_sparse_threshold` pins the
estimate across the threshold for each of these methods.

#### Dense normal equations (`DenseWlsStep`)

Solves an estimate only when the estimator is given a non-CPU `ILinearSolver` (a GPU dense backend); on the CPU it
serves as the dense observability gate of `CanonicalWlsStep`, and its normal-equation fill is shared by the
Constrained and QR steps. It evaluates `h` and `H`, forms `G` and `g` in one pass over the rows (`W` is diagonal; only
the upper triangle is accumulated, then mirrored; `AddScaledRow` with `fastFma: false`, a separate multiply and add per
lane), and solves through the injected `ILinearSolver` (dense LU with partial pivoting). A collapsed pivot returns
false. Its observability gate is the full analysis: island sweep plus gain rank proof.

#### Sparse fixed-pattern gain (`SparseWlsStep`)

The solve of every CPU `Wls` and robust estimate (through `CanonicalWlsStep`). `H` is held as per-row sparse lists. The pattern of `G` is structural: it depends on which
rows exist, not on the operating point. `Begin` therefore does all structural work once per bad-data pass:

1. `StructuralColumns` gives each row's distinct, sorted column list.
2. The pattern of `G = H^T W H` is built once as the union of each row's column-pair outer product. `G` is symmetric, so
   the CSR arrays double as the CSC arrays.
3. The fill-reducing ordering (`SparseLu.Ordering`, reverse Cuthill-McKee) is computed once.
4. Each row gets a slot map: the value-array index of every column pair, by binary search once.

Each iteration clears and refills values (`G[c_i, c_j] += w * h_ci * h_cj` over both triangles, `g` likewise) and
refactors numerically with `SparseLu.FactorPrepared` (Gilbert-Peierls); a null factorisation returns false. A Debug
build asserts that the numeric Jacobian never writes a column outside the structural pattern.

Observability depends on the state size. At or below `DenseEstimationLimits.RankProofMaxState` (2,500 states)
`ObservabilityAnalysis.AnalyzeSparse` densifies the gain at the start point and runs the same rank proof as the dense
backend, so free directions are named. Above it only the island sweep runs (`AnalyzeTopology`),
`ObservabilityReport.Rank` is -1 (not computed), and numerical singularity surfaces as a null factorisation.

#### Dense versus sparse: what is bit-identical

On the CPU a `Wls` or robust estimate takes the same sparse arithmetic at every size, so a case just below and a case
just above `SparseBusThreshold` come from the same arithmetic; `CanonicalWlsParityTests` pins that. The dense step
(`DenseWlsStep`, used to solve an estimate only with a non-CPU linear solver) and the sparse step are still not
bit-identical to each other: the dense backend factors with dense LU, the sparse backend with Gilbert-Peierls under an
RCM ordering, so the operation order differs. Both fills use a separate multiply and add (no fused multiply-add).

What is pinned:

- Agreement within tolerance. `SparseEstimatorTests.Sparse_and_dense_backends_agree_on_a_noisy_set` estimates case118
  with the default threshold and with the threshold forced below the case's size (on the CPU both now take the sparse
  solve and differ only in the observability report; the bit-for-bit pin is `CanonicalWlsParityTests`) and asserts
  `|dVm| < 1e-8` pu, `|dVa| < 1e-7` degrees, `J` within `1e-6` relative, the same
  chi-square verdict, the same rank, and `Sparse_backend_identifies_and_removes_the_same_gross_error` asserts the same
  removed row on case14.
- Run to run: `WlsEstimatorTests.Estimation_is_deterministic` runs two estimates in one process on the noise-free case14
  set and asserts equal iteration counts and equal bits of every `Vm` and `VaDeg`.
- Bit-for-bit against a reference: the neighbourhood injections against the solver injections; the estimate projection
  (`ToPowerFlowResult`) against the dense form; the sparse residual covariance against the full-solve loop, serial and
  parallel and at every SIMD tier; the gain-rank elimination against the original scalar loop. The Constrained and
  Feasible gain and KKT accumulations, the opt-in state-covariance gain and the observability gate of the Constrained
  method use the non-fused `AddScaledRow` kernel, and so does the residual covariance of Constrained, Feasible and QR
  (`DenseWlsStep.FormNormalEquations`, `fastFma: false`).
- Across SIMD tiers: `D1CrossTierTests.Dense_WLS_gain_is_bit_identical_at_every_tier_and_has_no_fused_multiply_add`
  pins the dense gain at every tier, and `Dot_is_bit_identical_at_every_tier_and_equals_the_defined_order` pins the
  dot product the dense residual covariance uses (one defined summation order, no fused multiply-add).

### Exact LAV (`Lav`, `LavLp`)

`Lav` minimises the least-absolute-values objective `sum_i |z_i - h_i(x)| / sigma_i` exactly, by successive linear
programming (Abur and Celik's formulation). At each Gauss-Newton iterate the correction minimises
`sum_i |r_i - H_i dx| / sigma_i` exactly (`LavLinearProgram`, an L1 vertex-descent simplex warm-started from the
previous iterate's basis). The step is a proximal (Levenberg-Marquardt style) successive linear program: the LP minimises
`sum_i |r_i - H_i dx| / sigma_i + mu sum_j |dx_j|`, the proximal term being `s` extra rows `dx_j = 0` of weight `mu`, so the
problem stays an L1 fit; `mu = 0` is the plain LAV step. The step is scaled by the same trust caps as every method's,
and then judged by the ratio `rho` of the actual decrease of the true LAV objective `sum_i |z_i - h_i(x)| / sigma_i` to the
decrease the linearised objective (without the proximal term) predicts at the capped step:

```
rho >= LavLpAcceptRatio (0.1) and the trial objective finite:   accept;  if rho > LavLpExpandRatio (0.75):  mu = mu / LavLpProximalGrowth (4),
                                                                and mu returns to 0 below LavLpProximalStart (1e-3) x max_i w_i max|H_i|
otherwise:   mu = (mu == 0 ? LavLpProximalStart x max_i w_i max|H_i| : mu x LavLpProximalGrowth);  solve the LP again
```

`mu` carries from one iterate to the next. A raw step below the tolerance is returned as is and the loop converges on
it. A predicted decrease at or below `LavLpStationaryRelativeDecrease` (1e-12) times `1 + objective` is stationary with
`mu = 0` (the state is the LAV optimum of this linearisation, `dx = 0`); with `mu > 0` the LP is retried once at `mu = 0`
before concluding. At the LAV optimum `dx = 0` is optimal for every `mu`, so the proximal term never moves the converged
state, and the ratio test keeps the iterate from cycling between two vertices of the linearisation. An LP that is not
optimal, or a predicted decrease that no `mu` realises within `LavLpMaxRetries` (40) retries, ends the estimate as
`MaxIterations`, never `Converged`.
`LavLp` is an alias: it is normalised to `Lav` on entry and runs the identical code path, bit for bit.

The optimum interpolates some rows exactly and leaves the gross errors with their whole error. Its objective is not the
weighted sum of squares and is not chi-square distributed: the result still fills `ObjectiveJ` and the chi-square fields
against the declared sigmas (for comparability), reports the LAV objective in `EstimationResult.LavObjective`, and sets
`ChiSquareIsVerdict` false; the CLI prints the LAV objective with no PASS or FAIL. There is no IRLS loop and no bad-data
removal.

### IRLS robust estimators (Huber, LAV-IRLS, Schweppe-GM)

The robust methods reuse the `Wls` step inside an iteratively reweighted outer loop of at most
`IrlsMaxOuterIterationsMEstimator` (50) passes for Huber and Schweppe-GM, and `IrlsMaxOuterIterations` (10) for
LAV-IRLS. After an inner solve converges, the weights are re-derived from the residuals at
the solution and the Gauss-Newton loop runs again from the current state. With `r~_i = |z_i - h_i| / sigma_i`, `k =
HuberK`, and `w_i` the declared weight:

```
Huber:        f_i = 1 if r~_i <= k else k / r~_i
LAV-IRLS:     f_i = 1 / max(r~_i, LavResidualFloor)                 LavResidualFloor = 1e-3
Schweppe-GM:  f_i = 1 if row i is critical, or rN_i is not finite, or rN_i <= k;  else k / rN_i
wEff_i = w_i * f_i
```

`HuberK` is 1.345. `rN_i` is the normalised residual of the bad-data section below, `|r_i| / sqrt(Omega_ii)`, which
divides by `sigma_i sqrt(1 - K_ii)` instead of `sigma_i`. It inflates the residual of a high-leverage measurement (a
radial GSP's sole injection meter) and defeats the masking that leaves that row's raw residual small. The leverage
`K_ii` and `Omega` are always computed with the declared weights `1/sigma^2`: before evaluating the normalised
residuals the loop calls `RebindWeights(w)`, and the next outer pass rebinds `wEff` for the solve. The fixed point of
a persistent gross error is therefore Huber's `f = k sigma / |r|`. The derivation is in
[design/robust-gm-leverage.md](design/robust-gm-leverage.md). Critical rows keep their declared weight, because
down-weighting a sole observer loses observability.

The loop stops when `max_i |wEff_new_i - wEff_old_i| / w_i < IrlsWeightTolerance` (1e-3). If it runs out of outer
passes with the weights still moving, the state is the solve under the second-last weights, not a fixed point;
`EstimationResult.IrlsCapReached` is then true. The status stays `Converged`, because a truncated IRLS is still a
converged Gauss-Newton solve, so read the flag alongside the status. `LavIrls` is approximate: the LAV optimum
interpolates some rows exactly, their weights hit the `1 / LavResidualFloor` ceiling and the active set flips between
passes, so it usually runs to the cap with `IrlsCapReached` set; `Lav` gives the exact optimum.

Between outer passes only the weights change, so the loop calls `RebindWeights`, not `Begin`. The sparse backend keeps
its gain pattern, RCM ordering and slot map, and the dense backends keep their buffers; every per-iteration buffer is
cleared and refilled before use, so this equals a fresh `Begin`. The Constrained and Feasible backends derive a
pseudo-weight from the weights and re-run `Begin`; the robust methods never select them.

Outliers are down-weighted, never removed, and the removal loop is skipped. The chi-square statistic is computed against
the original sigmas, `J = sum_i w_i (z_i - h_i)^2` with the declared `w_i`, so it stays comparable across methods; the
reported `ObjectiveJ` is that same quantity. The residual table of a robust estimate carries NaN normalised residuals
and no criticality flags, because the covariance stage does not run for it.

### Equality-constrained KKT (`Constrained`)

`ConstrainedDenseWlsStep` promotes every row whose `Source` is `virtual-zero-injection` (`ConstraintSource`) from a soft
pseudo-measurement to a hard equality. A pure switching node has no device attached, so its zero net injection is a
network fact. With `G_f`, `g_f` the normal equations of the free rows and `C` the constraint Jacobian rows, the
Hachtel augmented system is

```
[ G_f   alpha C^T ] [ dx          ]   [ g_f         ]
[ alpha C    0    ] [ lambda/alpha ] = [ alpha r_c    ]          r_c = z_c - h_c at the constraint rows
```

solved by the same partial-pivot LU (the matrix is indefinite). The free block is accumulated over the upper triangle
with the non-fused kernel and mirrored.

**Row equilibration.** With standard sigmas the free gain has entries of 1e6 to 1e8, while a constraint row is of order 1
to 100, so an unscaled constraint pivot falls under the relative pivot floor and the system looks singular. Every
constraint row, its `C^T` column and its right-hand side are multiplied by an exact power of two:

```
e     = ILogB(max over c of G_f[c, c])
alpha = 2^(e >> 1)  =  2^floor(e / 2)            (sqrt(max diag G_f) / alpha lies in [1, 2))
alpha = 1 when no diagonal entry is positive and finite
```

(`ConstraintRowScale`, shared by Constrained and Feasible). `dx` is the same vector mathematically; only the multipliers
are divided by `alpha`. Because `alpha` is a power of two, the scaling is exact in binary floating point, and the
exponent is integer arithmetic, identical on every CPU. With no constraint rows nothing is scaled, and a run without
constraint rows is unchanged.

**Gate and covariance.** The observability gate and the residual-covariance stage both treat a constraint row as an
ordinary row of weight `HardConstraintWeightScale * max_free(w)` (`NumericTolerances.HardConstraintWeightScale` = 1e4),
one value for both, so the rank test runs on the matrix whose covariance is reported. Rank deficiency of that gain is
rank deficiency of `[H_f; C]`. The covariance treats the hard equality as a very heavy row, which is an approximation of
the equality. Constraint rows are marked critical outright with a NaN normalised residual, so a network fact is never
removable telemetry.

`Begin` fails loud with `InvalidOperationException` when a free row has a non-positive or non-finite weight, or when
every row is a constraint (no telemetry to estimate from). The backend is dense only; above
`DenseEstimationLimits.DenseMethodMaxBuses` (3,000 buses) the estimator throws `NotSupportedException` rather than run
unconstrained; `estimate` reports the message and exits 2. This limit is independent of `SparseBusThreshold`.

### Physics-feasible method (`Feasible`)

`Feasible` is the Constrained KKT plus three additions, configured by `EstimationOptions.Feasibility`
(`FeasibilityConstraints`, `FeasibilityConstraints.cs`).

**Interior guard.** In `ApplyStep`, as given above: a step that would drive a voltage magnitude toward `VmFloor` (1e-6
pu) closes at most `FractionToBoundary` (0.995) of the gap, and `Vm` is clamped at the floor afterwards. The voltage band
`VmMin` to `VmMax` (0.9 to 1.1 pu, `PerBusVm` overrides) is not enforced during iteration; it is audited after
convergence.

**Active set.** Each iteration `TrySolve` linearises every inequality that is violated or inside its band and
appends it to the constraint block, row-scaled by the same `alpha`. A row that is no longer violated drops out at the
next iteration. With no equality and no active inequality the system is the free normal equations, so the method adds no
cost on a state that is already feasible.

- **Generator reactive capability** (`EnforceGeneratorQ`, on). Candidates are non-slack buses with at least one finite
  bound on the summed `Qmin`, `Qmax` of their generators. With `Qg = Qinj + Qd` (pu) and the Jacobian row from
  `MeasurementModel.InjectionQRow`:

  ```
  band      = max(GeneratorQBandFloorPu, GeneratorQHysteresisFraction * scale)
              scale = Qmax - Qmin when both finite;  |finite bound| when only one is;  0 when neither
              (GeneratorQBandFloorPu = 1e-6 pu, GeneratorQHysteresisFraction = 1e-3;
               NaN or inverted limits (Qmin > Qmax) throw ArgumentException)
  upper row active when  Qg > Qmax + 1e-6   or   (held on the previous solve and Qg > Qmax - band)
  lower row active when  Qg < Qmin - 1e-6   or   (held on the previous solve and Qg < Qmin + band)
  held (upper) = active on the previous solve AND its multiplier lambda > 0
  held (lower) = active on the previous solve AND its multiplier lambda < 0
  right-hand side = (bound - Qd) - Qinj(x)               full pull onto the bound
  ```

  The multiplier sign is exact because `alpha` is a positive power of two. A row whose multiplier says the data pulls
  `Qg` back inside is released. This rule, not an "active anywhere in the band" clamp, is used because a generator often
  sits at its reactive limit and the iterate approaches the limit from inside; a clamp would freeze `Qg` where it
  entered the band. The active-set memory is cleared in every `Begin`, so no memory crosses a bad-data pass.

- **Across-branch angle difference** (always on). One candidate per unordered bus pair (parallel and reversed circuits
  give identical rows). With `d = Va_f - Va_t` and `L = AngleDiffMaxRad` (pi/2):

  ```
  trigger = L * (1 - AngleHysteresisFraction)            AngleHysteresisFraction = 1e-3
  d >  trigger :  row [+1 at from column, -1 at to column],  rhs = min(0, L - d)
  d < -trigger :  same row,                                  rhs = max(0, -L - d)
  ```

  The clamp lets the row hold or pull back a difference, never push an under-limit difference onto the limit.

- **Branch thermal rating** (`EnforceBranchRatings`, off by default). The limit `|S| <= RateA` is nonlinear, so it is
  linearised through `S^2 = P^2 + Q^2`. Candidates are in-service branches with a finite positive `RateAMva`. Only the
  more loaded end of a branch is constrained, because the two ends differ by the loss and their rows are nearly
  collinear:

  ```
  band    = RateA_pu^2 * ThermalHysteresisFraction          ThermalHysteresisFraction = 1e-3
  active  = S^2 > RateA_pu^2 - band       (at the more loaded end)
  row     = 2 P dP/dx + 2 Q dQ/dx          (from MeasurementModel.BranchFlowRow)
  rhs     = min(0, RateA_pu^2 - S^2)       (hold or pull down, never push up)
  ```

  Parallel circuits between the same two buses form one group, and each iteration only the most over-rated circuit
  of the group (largest `S^2 / RateA^2`) contributes a row. Two identical rows would make the KKT singular.

**Acceptance gate.** After convergence, with `AuditGate` on (default), the estimate is projected with `ToPowerFlowResult`
and audited:

1. `PhysicsLaws.AuditAgainst(model, projected, ZeroInjectionSpecification(mm), AuditTolMva)` against the hard-gate laws
   (`PhysicsCheckRegistry.HardGateLaws` = laws 1, 2, 3, 6 and 8; `AuditTolMva` = 1e-4). Laws 1, 2 and 3 are identities of
   the projected state. Kirchhoff's current law (laws 6 and 8) is checked against the injections the estimate is
   physically bound to: the hard zero-injection rows, P and Q separately, at exactly those buses. It is not checked
   against the case file's static `Pg - Pd`, and not against telemetered injections, whose residuals belong to the
   chi-square test. With no zero-injection row, laws 6 and 8 report `NotApplicable`. Two zero-injection rows for the same
   bus and component with different values throw `ArgumentException`.
2. `PhysicsFeasibility.Check(model, projected, Feasibility)` against the voltage band, generator reactive capability
   (slack exempt, when `EnforceGeneratorQ`) and thermal ratings.

A hard-law breach sets `Status = Infeasible` and `Converged = false`. A limit breach does so only when
`RejectOnLimitViolation` is set (default off); otherwise it is reported on `EstimationResult.LimitViolations`.
`EstimationResult.Physics` carries the audit. The laws and the audit engine are specified in
[ch. 16](<0016 Physics conformance and the constrained estimator.md>).

`FeasibilityConstraints.Validate` requires `VmFloor` positive and finite, `VmFloor < VmMin < VmMax`, every `PerBusVm`
band to have `0 < min < max`, and `AngleDiffMaxRad` in `(0, pi]`; it throws `ArgumentException` otherwise.

Observability and residual covariance delegate to `ConstrainedDenseWlsStep`. The size limit is the same 3,000 buses.

### Observability

`ObservabilityAnalysis` (`ObservabilityAnalysis.cs`) runs two checks and produces an `ObservabilityReport(Observable,
Rank, StateSize, UnobservableBuses, UnobservableStates, UnmeasuredIslandBuses)`.

- **Island sweep** (every backend). Connected components of the graph of in-service branches, the same branches
  `YBus.Build` stamps. A component that no measurement touches (`MeasurementModel.TouchedBuses`) is unobservable by
  construction, and its bus ids go to `UnmeasuredIslandBuses`. A bus with no in-service branch is its own component.
  Above the rank-proof size this sweep is the only gate.
- **Gain rank** (`Analyze`; dense, QR, Constrained and Feasible backends, and the sparse backend up to 2,500 states).
  Pivoted row-echelon elimination of `G` with partial pivoting and a relative pivot floor:

  ```
  floor = max|G_ij| * NumericTolerances.RelativePivotFloor           RelativePivotFloor = 1e-12
  a column with no pivot above the floor is a free direction;  rank = number of pivots
  ```

  Free columns are named `"Va@bus N"` or `"Vm@bus N"` in `UnobservableStates`, with the affected bus ids in
  `UnobservableBuses`. The elimination uses the bit-exact `SimdKernels.SubtractScaledRow` (separate multiply and
  subtract per lane, never fused) and stops each row update at the pivot row's last nonzero column. The skipped updates
  subtract `factor * (+-0)`, which can only flip the sign of a zero, and a zero's sign never reaches the pivot search (it
  uses `|a|`) or a factor (zero factors are skipped), so the rank and the dead columns equal the original full-width
  loop's.

`Observable = (Rank == StateSize) and no unmeasured island`. When false the run ends with
`EstimationStatus.Unobservable` and the reported state is the state the gate ran at (the start point on the first
pass), not an estimate. On the sparse backend above
2,500 states `Rank = -1`.

### Chi-square consistency test

With redundancy, `dof = mChi - s > 0` (`mChi` counts the rows other than prior-solved ones), the converged `J(x^)` is compared with the chi-square quantile at
`ChiSquareConfidence` (0.95): `ChiSquarePassed = (J <= threshold)`. `ChiSquare.Quantile` (`ChiSquare.cs`) is dependency
free. It uses the Wilson-Hilferty cube approximation and Acklam's rational approximation for the standard-normal quantile:

```
t         = 2 / (9 k)
cube      = 1 - t + z_p * sqrt(t)
chi2(k,p) = k * cube^3
z_p       = NormalQuantile(p)        (Acklam; relative error < 1.15e-9; two tail pieces for p < 0.02425 and p > 1 - 0.02425,
                                      a central rational fit between them)
Quantile returns NaN when k <= 0 or p is not in (0, 1)
k = 1:      chi2(1,p) = NormalQuantile((1 + p) / 2)^2        (exact form; GR-539)
k = 2:      chi2(2,p) = -2 ln(1 - p)                         (exact form, deterministic log; GR-539)
```

The cube approximation is within 1 percent for `k >= 3` at `p = 0.95` (0.51 percent at `k = 3`, checked against the
exact quantile by numerical integration), which is what a detection gate needs; the test
`BadDataTests.Chi_square_quantiles_match_table_values_within_one_percent` asserts a relative error below 0.01 against
table values at `p = 0.95` for `k = 10`, 50 and 100, and a NaN for `k = 0`. At `k = 1` and 2, where the cube
approximation is 2.5 and 0.9 percent low at `p = 0.95`, the exact closed forms above are used instead
(`BadDataTests.Chi_square_quantiles_at_one_and_two_dof_are_exact`). With `dof <= 0`
the threshold is NaN and the verdict is vacuous: a gross error is undetectable and `DegreesOfFreedom` says so.

`EstimationResult.ChiSquareTest` (`ChiSquareOutcome`) says whether the test ran: `NotRun` for an unobservable,
non-converged or non-finite estimate, for `DetectBadData = false`, and for `dof <= 0`; otherwise `Passed` or `Failed`.
`ChiSquarePassed` is true whenever the test did not run, and `ChiSquareThreshold` is then NaN. Read `ChiSquareTest`, not
`ChiSquarePassed`, to learn whether a verdict exists. When the test passes and `LeanResiduals` is set, the m
residual-covariance forms are skipped, and the residual table carries NaN normalised residuals.

### Bad data: largest normalised residual with critical protection

When chi-square fails, `BadDataAnalysis` (`BadDataAnalysis.cs`) computes, at the converged state, the residual-covariance
diagonal and the normalised residual of every row:

```
Omega_ii = sigma_i^2 - h_i^T G^-1 h_i                  (h_i = row i of H, per-unit; G at the declared weights)
rN_i     = |r_i| / sqrt(Omega_ii)
critical_i :  Omega_ii <= sigma_i^2 * CriticalResidualVarianceFraction          (1e-8)
leverage:     K_ii = w_i * h_i^T G^-1 h_i = 1 - Omega_ii / sigma_i^2
```

A critical row is the only observation of some state direction: its residual is structurally zero and removing it would
make the system unobservable. It is flagged (`IsCritical`, `rN = NaN`) and never removed, so a gross error on a critical
measurement is undetectable and stays flagged (`BadDataTests.A_gross_error_on_a_critical_measurement_is_undetectable_and_stays_flagged`).

The dense path factors `G` once (LU with partial pivoting, the shared relative pivot floor) and back-substitutes one
right-hand side per row, at O(s^3) + m O(s^2). It throws on a near-singular gain; callers reach it only at a converged,
observable estimate. The sparse path reuses one Gilbert-Peierls factorisation for all rows through
`SparseLuFactorization.InverseQuadraticForm`, which evaluates `h_i^T G^-1 h_i` doing only the work that can change it
and is bit-identical to the full solve:

1. the row is scattered straight into pivot-step space in a reused workspace;
2. the forward sweep starts at the row's first nonzero slot and skips columns whose multiplier is exactly zero;
3. the back sweep stops at the smallest slot the row reads and does not propagate an exactly zero value;
4. eight rows are solved per pass over `L` and `U` in a lane-interleaved workspace, one vector multiply and one vector
   subtract per factor entry, never fused, so each lane rounds as the scalar update does at every SIMD width; rows are
   grouped by their back-sweep stop (this affects speed only);
5. the blocks run in parallel, each writing only its own rows, so the result does not depend on thread count or
   scheduling.

A factor with a non-finite entry takes the full reference arithmetic for every row. On gb-full (3,539 buses, 34,289
synthetic measurements, Release, the i3 reference machine) this stage takes 5.3 to 5.7 s, 18.5 to 20.4 s on one thread, and a default
estimate takes 9.4 s and allocates 515 MB. A fill-reducing ordering or selected inversion would be faster but changes the
rounding of every normalised residual.

**Identification.** Among rows that are not critical, not a backfill row, and have a finite `rN` above
`NormalizedResidualThreshold` (3.0), the largest `rN` is removed, recorded in `Removed` (a `BadDataRemoval` with the row,
its `rN` and the zero-based pass number), and the estimate is recomputed warm from the current state. The loop stops when
chi-square passes, when no row is identifiable, or after `MaxBadDataRemovals` (20) removals. A failure with nothing
removable is reported (`ChiSquarePassed = false`), not force-fitted.

**Prior backfill** (`BackfillRemovedFromPrior`, off by default). When the loop removes a real-power row (`PInjMw` or
`PFlowMw`) a pseudo-measurement replaces it, evaluated from the start state (the warm start, or the measured-injection
solved prior under `--prior solve`):

```
value = h_prior(x_start) * BaseMva
sigma = max(BackfillSigmaMultiplier * sigma_removed, BackfillRelativeSigmaFloor * |value|)       (3.0 and 0.05)
Source = "removal-backfill-prior"  (WlsStateEstimator.BackfillSource); never removable
```

Without it, removing a radial region's sole informative constraint lets the state run away from the remaining
measurements. Reactive kinds are deliberately not backfilled: in the GB measurement layer the Q rows are themselves
pseudo-measurements and the solved prior embeds the measured Q, so backfilling a defective Q row re-injects its own
defect and churns the loop to its cap (`BadDataTests.Reactive_removals_are_not_backfilled`). The option is opt-in
because it changes what "removed" means: excluded becomes replaced by the prior.

### Hypothesis-testing identification (HTI)

LNR removes one row at a time and can point at a good row when several errors interact, because their residuals smear.
`HypothesisTestingBadData.Identify` (`HypothesisTestingBadData.cs`) forms a joint estimate of the errors over a suspect
set and applies a test per suspect. It is dense and runs on a converged estimate. With `G` and `h_i` at the estimate:

```
suspects S :  rows that are not critical (Omega_ii > sigma_i^2 * 1e-8) and have rN_i > suspectThreshold
S_SS[a,b]     = delta_ab - h_a^T G^-1 h_b * w_b           (I - H G^-1 H^T W, restricted to S)
Omega_SS[a,b] = delta_ab * sigma_a^2 - h_a^T G^-1 h_b
e^            = S_SS^-1 r_S                                (jointly estimated gross errors, per-unit)
Cov           = S_SS^-1 Omega_SS S_SS^-T
t_a           = |e^_a| / sqrt((Cov)_aa)                    (0 when the variance is not positive)
identified    :  t_a > testThreshold
```

`suspectThreshold` is `NormalizedResidualThreshold` (3.0) and `testThreshold` is 3.0. A suspect below the test threshold
is exonerated even though its raw `rN` crossed the suspect threshold. With `hardConstraints` (passed by
`HtiBadDataEstimation` when the method is Constrained or Feasible) every `virtual-zero-injection` row carries the
constrained backend's pseudo-weight `w_c = HardConstraintWeightScale (1e4) * max w_free` in `G`, `W` and `Omega`
(`sigma_c^2 = 1 / w_c`), exactly as `ConstrainedDenseWlsStep.NormalizedResiduals` forms it for LNR, and is excluded
from the suspect set outright, so a hard constraint is never identified. Without it (plain WLS) such a row is an
ordinary soft row at its declared sigma. An identified row is returned as an
`HtiIdentified(Measurement, EstimatedError, TestStatistic)`.

`HtiBadDataEstimation.Estimate` (`HtiBadDataEstimation.cs`) wraps the estimator in the HTI loop:

1. Estimate with `MaxBadDataRemovals = 0`, so chi-square still runs but LNR removes nothing.
2. If the estimate is not converged or chi-square did not run, stop (`ChiSquareNotRun`); if it passed, stop
   (`ChiSquarePassed`); if `MaxBadDataRemovals` HTI removals were already made, stop (`RemovalCapReached`).
3. Otherwise run `Identify`. If nothing is identified, stop (`NothingIdentified`).
4. Remove the identified rows by reference, largest `t` first, up to the remaining cap, and re-estimate from the
   original start point.

Every stop is reported in `HtiEstimationResult.StopReason`; none is silent. The final `Estimate` has an empty `Removed`
list; the HTI removals are in `HtiEstimationResult.Removed` (`HtiRemoval`: the verdict and the 0-based pass). HTI is
refused with `ArgumentException` or `NotSupportedException` when `DetectBadData` is false, with a robust method, with
`BackfillRemovedFromPrior`, with a non-finite or non-positive `testThreshold`, and above
`HtiBadDataEstimation.MaxState` (2,000 states, `DenseEstimationLimits.StateCovarianceMaxState`); nothing falls back to
LNR. `estimate --hti` selects it; the CLI exits 2 on a refusal. A suspect block that is singular is not a refusal:
`Identify` throws `InvalidOperationException` ("singular matrix in DSE covariance inverse"); the CLI reports that the
bad data could not be identified on this set and exits 3.

### False-data-injection screen

Model-based bad-data detection is provably bypassable: an attack `a = H c` lies in the range of the Jacobian, leaves every
residual unchanged, and moves the estimate by `c` without raising chi-square (Liu, Ning and Reiter).
`FdiaScreen.Screen` (`Security/FdiaScreen.cs`) catches the sub-class of such attacks that drives the state into a
physically implausible region. It requires a converged estimate whose chi-square test ran, and throws
`InvalidOperationException` otherwise, because a non-converged estimate is not a state and a test that never ran is not
a pass. It then projects the estimate and evaluates:

```
tol         = physicsTolMva ?? 0.1 * BaseMva
spec        = ZeroInjectionSpecification(residual-table rows tagged virtual-zero-injection)
impossible  = not PhysicsLaws.AuditAgainst(model, projected, spec, tol).Gate(HardGateLaws)
limits      = PhysicsFeasibility.Check(model, projected, constraints)          (default FeasibilityConstraints)
significant = { v in limits : |v.Value - v.Limit| > k * u(v) }                 (k = significanceSigmas, default 3)
anchorBad   = { a in protectedAnchors : |z_a - h_a(x_hat)| > k * sigma_a }   (empty when no anchors are given)
consistent  = ChiSquareTest == Passed
suspected   = consistent and (impossible or significant.Count > 0 or anchorBad.Count > 0)
```

A protected anchor is a trusted (PMU-anchored) measurement passed as `protectedAnchors`. A feasible-landing attack passes
chi-square and the envelope, but it cannot move an anchor it did not forge, so the state it produces no longer reproduces
that anchor. The anchor residual is evaluated through the shared `MeasurementModel`, and an anchor with a non-finite value
or a sigma that is not a finite number above 0 is refused with `ArgumentException`, naming it. A clean screen's summary
reads "chi-square PASSED and the state is physically plausible and anchor-consistent - no FDIA indicator."

KCL (laws 6 and 8) is scored only against the injections the estimate is physically bound to, its hard zero-injection
rows, exactly as the Feasible gate does; buses those rows do not name are unconstrained. Laws 1, 2 and 3 are identities
of the projected state and are gated as before. Scoring KCL against the case file's `Pg - Pd` (the plain `Audit`) would
read every estimate of real telemetry whose injection differs from the case dispatch by more than `tol` at one bus as
`impossible`, and so as `suspected`.

`u(v)` is an upper bound on the standard deviation of the estimated quantity, from the declared sigma of the telemetry
in the residual table: the covariance of an estimated measured quantity is `H G^-1 H^T = R - Omega` with `Omega`
positive semi-definite, so its standard deviation is at most the measurement's sigma. For `VmBand` it is the `VmPu`
sigma at the bus; for `GenQ` the `QInjMvar` sigma at the bus (`Qg = Qinj + Qload`, `Qload` fixed by the state); for
`Rating`, per end, `(|P| sigma_P + |Q| sigma_Q) / |S|` from that end's flow meters (a first-order bound on the standard
deviation of `|S|` for any correlation), the larger of the two ends because the rating is checked on the more-loaded
end. A quantity with no meter (or a branch end without both P and Q) has `u = 0`, no margin. Where several rows measure one
bus quantity the smallest sigma is used, since each one bounds the estimate's standard deviation (GR-077; it used to be
the largest, so a loose pseudo row at the bus could hide a breach); where branch rows share a `(From, To, end)` key,
which parallel circuits do, the largest is used, since the rows may measure different circuits. All breaches are returned in `LimitViolations`, the counted ones in
`SignificantLimitViolations`.With `k = 0` the clean synthetic case30 and case118 would read `suspected` with
exit 3, which is why the margin exists (see manual ch. 21).

The tolerance is 0.1 of the base MVA because an estimate of noisy telemetry never satisfies power balance to machine
precision; the tolerance is 10 MVA on a 100 MVA base. The screen reports and never silently rejects, and it does
not catch an attack that lands on another feasible, in-limit operating point. `estimate --fdia-screen` runs it, refuses
`--no-bad-data` (exit 2), writes `fdia-screen.json` (`gridsim-fdia-screen/1`) and exits 3 when an attack is suspected or
the screen has no verdict.

### Noise-floor analysis

`NoiseFloorAnalysis.Characterise` (`NoiseFloorAnalysis.cs`) decides whether the declared sigmas match the noise the feeds
carry. It reads the normalised residuals `rN_i` the estimator produces, skips non-finite ones, and groups them by
`(Source, Kind)`. Because `rN` is an absolute value, a correctly declared class is half-normal, so each statistic is
divided by its half-normal reference:

```
kMean   = mean(rN) / 0.7978845608028654           (E|N(0,1)| = sqrt(2/pi))
kStd    = sampleStd(rN) / 0.6028102749890869      (std|N(0,1)| = sqrt(1 - 2/pi))
kRobust = median(rN) / 0.6744897501960817         (median|N(0,1)|)
bias    = kMean - kRobust
SuggestedSigmaScale = kRobust if > 0, else kStd if > 0, else 1
Miscalibrated = (count >= minSamples) and (|kRobust - 1| > tol or |bias| > tol)
```

`tol` is `DefaultMiscalibrationTolerance` (0.25) and `minSamples` is `DefaultMinSamplesPerClass` (8); a smaller class is
reported but never flagged. `k` is the ratio of true noise to declared sigma. Classes sort worst first
(`|kRobust - 1|` descending, then by count). A single estimate rarely gives a class enough samples; run it over a corpus
of many timestamps. `estimate-noise-floor` pools the residual tables of many `estimate-export.json` files and exits 1
when no residual is finite, which is the case for a robust-method or `--lean` run.

### Measured-injection prior

`MeasuredInjectionPrior` (`MeasuredInjectionPrior.cs`) turns a measurement set into a measured-injection power flow
solved by the Newton engine. Its solution is the Gauss-Newton warm start and the source of voltage priors. Per-bus
injections come from the `PInjMw` and `QInjMvar` rows (duplicates summed, unmeasured buses zero):

| Bus | Model |
|---|---|
| slack | keeps its reference role and generator; `Pd = Qd = 0`; absorbs the imbalance |
| measured `P >= PvThresholdMw` (50 MW) | PV at `PvSetpointPu` (1.01 pu), `Pd = 0`, `Qd = -Q`, reactive range `+-QCapabilityRatio * max(|P|, MinQCapabilityBaseMva)` (0.6, 100 MVA) |
| everything else | PQ with `Pd = -P`, `Qd = -Q` |

The solve runs with `EnforceQLimits` and at most `PriorSolveMaxIterations` (100) iterations. `ApplyVoltagePrior` then
replaces each `VmPu` row whose source is `pseudo-voltage-schedule` with the solved voltage at sigma
`DefaultVoltagePriorSigmaPu` (0.02 pu), retagged `prior-solved`. `estimate --prior solve` runs this and warm-starts from it.

### Phase-angle feed

`PhaseAngleFeed` (`PhaseAngleFeed.cs`) turns per-bus phase-angle observations into `VaDeg` rows. Bus angles are the
weakest-observed directions in a P-rich, VAr-blind measurement layer: they are pinned only indirectly through the
P-injection and P-flow rows, and show up as the near-singular gain pivots that make the trust cap bind. A genuine per-bus
angle adds a direct 1.0 pivot on that bus's angle column and sharpens the angle covariance.

A per-bus absolute angle is `phi_i(t) = theta_sys(t) + delta_i`, with `theta_sys` the system common-mode phase and
`delta_i` the bus's spatial spread. The estimator solves `delta_i` against a fixed slack, so re-referencing an absolute
feed to the slack cancels `theta_sys` exactly. Genuine per-bus differences improve the estimate
(`PhaseAngleFeedTests.Genuine_per_bus_angles_sharpen_the_estimate` asserts the angle RMSE falls below half the baseline). A
single system angle repeated on every bus cancels against the slack and adds nothing, so the GDA `PHASEANGLE_INFERED`
series is used only as a reference frame and a common-mode source, never as a per-bus row. A real feed carries angles
against its own reference; the estimator does not solve a per-feed offset.

`ToMeasurements` conditions each observation `(Bus, AngleDeg, Utc, FreqHz, RocofHzPerS)` by mode. Every mode except
`Raw` first projects to a common instant `T` (default: the latest observation):

```
theta(T) = theta(t) + 360 * (f - f0) * dt + 180 * rocof * dt^2          f0 = NominalHz (50), dt = T - t in seconds, degrees
AutoReference    :  z = theta_i(T) - theta_datum(T) + anchor
AlreadyReferenced:  z = theta_i(T) - CommonModeDeg
Raw              :  z = theta_i as given
```

`AutoReference` (default) re-references through a datum bus: `DatumBus` if set, else the slack when it is among the
observations, else the first observation's bus; the datum must be present. A slack datum anchors at the slack's own
angle. A non-slack datum needs `DatumAnchorDeg`, its present-state angle in the slack frame (for example from a solved
prior), and its own row is then not emitted. Without it the call throws, because the case file's static angle would shift
every row. `DatumAnchorDeg` given with a slack datum, in another mode, or non-finite also throws. Slack-bus rows are
dropped. Each row has `Source = "phaseangle f=<Hz>Hz@<utc>"` and sigma `SigmaDeg` (2.0 degrees). `estimate --phaseangle
BUS:ANGLE:UTC:FREQ ...` with `--phaseangle-ref`, `--phaseangle-sigma`, `--phaseangle-at` and `--phaseangle-from-gda`
exposes it, and injected rows carry the same `TakenUtc` horizon guard as every dated input. The GDA lake authors no
genuine per-bus angle yet, so the override is the injection path.

### Synthetic sampler

`SyntheticMeasurements.Sample` (`SyntheticMeasurements.cs`) draws `z = h(x_true) + N(0, sigma) * NoiseScale` from a
solved power-flow truth. It is the estimator's test fixture and the `estimate --synth` feed. It is deterministic: one
`System.Random(Seed)`, buses and branches walked in model order, and Box-Muller draws
`z = sqrt(-2 ln u1) cos(2 pi u2)` with `u1 = 1 - NextDouble()`. `NoiseScale = 0` draws nothing from the generator, giving
exact values under the declared sigmas, which is the machine-precision recovery fixture. It emits `VmPu`, `PInjMw`,
`QInjMvar` per covered bus and `PFlowMw`, `QFlowMvar` at both ends per covered branch; it never emits `VaDeg`, `IMagPu`
or `IAngDeg`. Coverage is a deterministic stride: item `i` is covered when `floor((i+1) * coverage) > floor(i * coverage)`.
Parallel branches are keyed by the ordinal circuit `"#k"`, because real cases carry duplicate names on parallel
branches. The set carries `TakenUtc` 2024-03-01T12:00Z; the sampler never reads the clock.

### Results and export

`EstimationResult` carries `Status`, `Converged`, `Iterations`, `MaxStateUpdate` (final `||dx||inf`), `BusIds`, `Vm`,
`VaDeg` (degrees, slack-referenced), `ObjectiveJ`, `DegreesOfFreedom`, `ChiSquareThreshold`, `ChiSquarePassed`,
`ChiSquareTest`, `Residuals` (per row: predicted value and residual in the row's engineering unit, normalised residual or
NaN, criticality), `Removed`, `Observability` and `IrlsCapReached`. The Feasible method adds `Physics` and
`LimitViolations`. `Converged` is true only for `Status == Converged`; every other status, `Infeasible` included, means the
estimate is not to be used. The residual unit factor is 1 for `VmPu` and `IMagPu`, `180/pi` for the angle kinds and
`BaseMva` for the power kinds.

With `ComputeStateCovariance` (off by default) a converged estimate of at most `CovarianceStateLimit` = 2,000 states also
carries `StateVmSigma` (pu) and `StateVaSigmaDeg` (degrees, slack 0): `sqrt(max(diag(G^-1), 0))` per state, with `G` formed
densely at the declared weights and one LU plus `s` back-substitutions. Above the limit both stay null.

`ToPowerFlowResult(model)` projects the estimate into the solved-state shape the rest of GridSim consumes: injections,
losses and flows recomputed at `x^` through the same assembly path as the power-flow solvers, with the sparse
admittance and the nonzero cells only. It is bit-identical to the dense form. `EstimateExport` writes
`gridsim-estimate-export/1` ([ch. 10](<0010 File and wire formats.md>)), and `estimate --audit-log` appends the run
to a tamper-evident audit log with the result line `EstimationAuditTrail.Outcome` builds, the one source of that text, which `EstimationAuditTrail.Record` also writes for embedding code (`method`, `status`, `observable`, `J`, `chi2` as `PASS`, `FAIL`,
`not-run` (the test did not run) or `no-verdict` (a LAV estimate, followed by `lav_objective=<value>`), `dof`, `removed`,
`measurements`, then `irls_cap_reached=true` when the IRLS iteration cap was reached, plus the HTI and FDIA outcomes; see
[ch. 21](<../manual/0021 State estimation.md>)).
`estimate` exits 0 when the estimate converged and is observable, and 3 otherwise.

### Recursive estimators (`Estimation/Dse/`)

`Dse/` holds the Kalman-family trackers (`KalmanStateEstimator`, `UnscentedKalmanStateEstimator`,
`EnsembleKalmanStateEstimator`), the `IProcessModel` seam with `RandomWalkProcessModel`, and `FrequencyRocofFilter`.
They carry the previous window's state and covariance forward instead of solving each window from scratch, and are
specified in [ch. 18](<0018 Real-time, distribution and dynamic estimation.md>). Their size caps are in
`DenseEstimationLimits` (below). `LinearPmuStateEstimator` is a single-solve linear estimator on rectangular voltages for
PMU phasors; `gridsim pmu-estimate` runs it on a `gridsim-measurements/1` set through `PmuMeasurementSetAdapter`
([ch. 33](<0033 C37.118 synchrophasor ingest.md>)). The adapter pairs each bus's `VmPu` and `VaDeg` rows and each
from-end `IMagPu` and `IAngDeg` pair into a rectangular phasor, with the first-order (linearised) polar-to-rectangular
error covariance.

### GDA-side sigma ladder

Real GB measurement sets are authored in the GDA data lake by two builders (the GDA repository,
`v1/NetworkModel/build_gsp_measurements.py` and `build_estimator_measurements.py`) and consumed here through
`MeasurementSet.Load`. The per-GSP generation, PN fallback, unallocated bucket and group-demand sigmas are specified in
[ch. 9](<0009 GDA integration.md>). The estimator-measurement builder composes a per-bus
`PInjMw` from metered generation, interconnector flow and a scaled folded demand, and the sigma is the root sum of
squares of the parts. The source tag on each row names its composition.

| Component | Sigma | Source tag |
|---|---|---|
| Interconnector flow | `max(5 MW, 2 % * |MW|)` | `ic` |
| Demand pseudo, national-scaled | `max(5 MW, 25 % * |P_load|)` | `pseudo-demand` |
| Demand pseudo, group-anchored | `max(5 MW, 15 % * |P_load|)` | `pseudo-demand-group` |
| `QInjMvar` demand pseudo | `max(10 MVAr, 30 % * |Q_load|)` | `pseudo-demand` |
| Switched-compensation widening | adds `(range / sqrt(12))^2` in quadrature | `...+comp-range` |
| Zero-centred reactive pseudo (metered bus, no demand row) | `max(50, 0.3 * |P|)` | `pseudo-reactive` |
| Virtual zero injection (pure switching nodes) | 10 MW (P), 20 MVAr (Q) | `virtual-zero-injection` |
| Slack voltage reference | 1.02 pu, sigma 0.005 | `reference` |
| Voltage-schedule prior at every other bus | 1.01 pu, sigma 0.03 | `pseudo-voltage-schedule` |

A group-anchored pseudo scales the bus's folded demand to its PES group's measured net offtake, so only the within-group
shape is prior, while a national-scaled pseudo rests on one national scalar. The comp-range widening declares real
uncertainty: the state of a switched MSC, reactor or SVC is unobservable from settlement data, so its Q pseudo carries the
device range as extra sigma (the standard deviation of a uniform distribution of that range). The reactive layer is
entirely pseudo, which is why prior backfill excludes Q kinds and why the `virtual-zero-injection` rows are the ones the
Constrained method promotes to hard equalities. The sets are horizon-guarded on the GDA side and again by
`MeasurementSet.Load` ([ch. 8](<0008 Provenance and invariants.md>)).

## Parameters and defaults

`EstimationOptions` (`src/GridSim.Core/Estimation/WlsStateEstimator.cs`):

| Name | Default | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| Tolerance | 1e-8 | rad or pu | convergence threshold on `max |dx|` | `EstimationOptions.Tolerance` |
| Max iterations | 30 | iterations | inner Gauss-Newton cap per solve | `EstimationOptions.MaxIterations` |
| Flat start | true | flag | start at `Vm = 1`, slack angle | `EstimationOptions.FlatStart` |
| Warm start | none | state | start from a solved or estimated state, matched by bus id | `EstimationOptions.WarmStart` |
| Detect bad data | true | flag | run chi-square and the removal loop | `EstimationOptions.DetectBadData` |
| LNR threshold | 3.0 | sigma | a row is removable only above this `rN` | `EstimationOptions.NormalizedResidualThreshold` |
| Max removals | 20 | rows | cap on bad-data removals | `EstimationOptions.MaxBadDataRemovals` |
| Chi-square confidence | 0.95 | probability | confidence of the consistency test | `EstimationOptions.ChiSquareConfidence` |
| Angle step cap | 0.2 | rad | trust cap on one step's angle components | `EstimationOptions.MaxAngleStepRad` |
| Vm step cap | 0.1 | pu | trust cap on one step's Vm components | `EstimationOptions.MaxVmStepPu` |
| Sparse threshold | 200 | buses | dense observability report at or below this, sparse diagnosis above; the estimate itself is the same arithmetic either side | `EstimationOptions.SparseBusThreshold` = `SolverSelector.DenseSparseCrossoverBuses` |
| Method | `Wls` | enum | estimation method | `EstimationOptions.Method` |
| Feasibility | see below | record | envelope of the Feasible method | `EstimationOptions.Feasibility` |
| Lean residuals | false | flag | skip the covariance stage when chi-square passes | `EstimationOptions.LeanResiduals` |
| Trace | none | object | record per-iteration `maxUpdate` and trust scale | `EstimationOptions.Trace` |
| Backfill | false | flag | replace removed real-power rows by prior pseudo-rows | `EstimationOptions.BackfillRemovedFromPrior` |
| State covariance | false | flag | per-bus 1-sigma bounds from `diag(G^-1)` | `EstimationOptions.ComputeStateCovariance` |
| Huber k | 1.345 | sigma | Huber and Schweppe-GM transition | `EstimationOptions.HuberK` |

Loop and numerical constants:

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| IRLS outer cap (M-estimators) | 50 | passes | outer reweighting passes of Huber and Schweppe-GM | `WlsStateEstimator.IrlsMaxOuterIterationsMEstimator` |
| IRLS outer cap (LAV-IRLS) | 10 | passes | outer reweighting passes of LAV-IRLS | `WlsStateEstimator.IrlsMaxOuterIterations` |
| Exact LAV retries | 40 | LPs | proximal-weight retries of one correction | `WlsStateEstimator.LavLpMaxRetries` |
| Exact LAV accept ratio | 0.1 | ratio | accept a step when actual / predicted decrease reaches this | `WlsStateEstimator.LavLpAcceptRatio` |
| Exact LAV expand ratio | 0.75 | ratio | above this the proximal weight shrinks | `WlsStateEstimator.LavLpExpandRatio` |
| Exact LAV proximal growth | 4 | x | factor the proximal weight grows or shrinks by | `WlsStateEstimator.LavLpProximalGrowth` |
| Exact LAV proximal start | 1e-3 | x | first nonzero proximal weight, as a fraction of `max_i w_i max|H_i|` | `WlsStateEstimator.LavLpProximalStart` |
| Exact LAV stationary decrease | 1e-12 | relative | a predicted decrease at or below this times `1 + objective` is stationary | `WlsStateEstimator.LavLpStationaryRelativeDecrease` |
| LAV residual floor | 1e-3 | sigma | floor on `r~` in `1/r~` | `WlsStateEstimator.LavResidualFloor` |
| IRLS weight tolerance | 1e-3 | relative | stop when no weight factor moves more | `WlsStateEstimator.IrlsWeightTolerance` |
| Backfill sigma multiple | 3.0 | x | minimum multiple of the removed row's sigma | `WlsStateEstimator.BackfillSigmaMultiplier` |
| Backfill sigma floor | 0.05 | fraction | minimum fraction of the backfilled value | `WlsStateEstimator.BackfillRelativeSigmaFloor` |
| Critical fraction | 1e-8 | fraction | critical when `Omega_ii <= sigma_i^2 * this` | `NumericTolerances.CriticalResidualVarianceFraction` |
| Pivot floor | 1e-12 | relative | relative pivot floor of the rank proof and the LU | `NumericTolerances.RelativePivotFloor` |
| Constraint weight scale | 1e4 | x | constraint pseudo-weight over the largest free weight | `NumericTolerances.HardConstraintWeightScale` |
| Zero-flow magnitude | 1e-12 | pu | `|I|` row left empty at or below this `|S|` | `MeasurementModel.ZeroFlowMagnitudePu` |
| Zero-flow magnitude squared | 1e-24 | pu^2 | angle-of-current row has only the `Va` term at or below this | `MeasurementModel.ZeroFlowMagnitudeSquaredPu` |
| Base MVA tolerance | 1e-9 | MVA | set versus model base mismatch | `MeasurementSet.BaseMvaMatchToleranceMva` |
| Horizon | 169 | hours | minimum age of a dated input | `ForwardInferenceGuard.HorizonHours` |

`FeasibilityConstraints` and the Feasible active set (`FeasibilityConstraints.cs`, `FeasibleWlsStep.cs`):

| Name | Default | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| Vm floor | 1e-6 | pu | the state never crosses it | `FeasibilityConstraints.VmFloor` |
| Vm band | 0.9 to 1.1 | pu | audited after convergence | `VmMin`, `VmMax` |
| Per-bus band | none | pu | overrides the global band | `PerBusVm` |
| Angle limit | pi/2 | rad | across-branch angle difference | `AngleDiffMaxRad` |
| Enforce generator Q | true | flag | reactive capability rows | `EnforceGeneratorQ` |
| Enforce branch ratings | false | flag | thermal rows | `EnforceBranchRatings` |
| Fraction to boundary | 0.995 | fraction | share of the gap to the floor one step may close | `FractionToBoundary` |
| Audit gate | true | flag | physical-law audit after convergence | `AuditGate` |
| Reject on limit violation | false | flag | a limit breach also sets `Infeasible` | `RejectOnLimitViolation` |
| Audit tolerance | 1e-4 | MVA | tolerance of the hard-law audit | `AuditTolMva` |
| Thermal hysteresis | 1e-3 | fraction of rating^2 | band of an active thermal row | `FeasibleWlsStep.ThermalHysteresisFraction` |
| Angle hysteresis | 1e-3 | fraction of limit | band of an active angle row | `FeasibleWlsStep.AngleHysteresisFraction` |
| Generator Q hysteresis | 1e-3 | fraction of range | band of a reactive row | `FeasibleWlsStep.GeneratorQHysteresisFraction` |
| Generator Q band floor | 1e-6 | pu | narrowest reactive band | `FeasibleWlsStep.GeneratorQBandFloorPu` |
| Generator Q violation tolerance | 1e-6 | pu | a bound is violated this far past it | local constant `qTolPu` in `FeasibleWlsStep.TrySolve` |

Size limits (`DenseEstimationLimits`, `DenseEstimationLimits.cs`):

| Name | Value | Unit | Applies to | Source symbol |
|---|---|---|---|---|
| Dense method cap | 3,000 | buses | `Constrained`, `Feasible`, `Qr` throw above it | `DenseMethodMaxBuses` |
| Rank proof cap | 2,500 | states | sparse-backend rank proof; above it `Rank = -1` | `RankProofMaxState` |
| Covariance cap | 2,000 | states | `ComputeStateCovariance`; HTI | `StateCovarianceMaxState`, `HtiBadDataEstimation.MaxState` |
| Recursive filter cap | 2,500 | states | EKF construction | `RecursiveFilterMaxState` |
| Innovation filter cap | 2,500 | measurements per window | UKF and EnKF windows | `InnovationFilterMaxMeasurements` |

Other defaults:

| Name | Default | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| HTI test threshold | 3.0 | sigma | `t` above it identifies a row | `HtiBadDataEstimation.Estimate(testThreshold)` |
| FDIA physics tolerance | `0.1 * BaseMva` | MVA | hard-law audit tolerance in the screen | `FdiaScreen.Screen(physicsTolMva)` |
| Noise-floor tolerance | 0.25 | ratio | miscalibration band on `k` and `bias` | `NoiseFloorAnalysis.DefaultMiscalibrationTolerance` |
| Noise-floor minimum samples | 8 | samples | a smaller class is never flagged | `NoiseFloorAnalysis.DefaultMinSamplesPerClass` |
| Prior PV threshold | 50 | MW | measured injection at or above it becomes PV | `MeasuredInjectionPrior.PvThresholdMw` |
| Prior PV setpoint | 1.01 | pu | voltage of a measured generating site | `MeasuredInjectionPrior.PvSetpointPu` |
| Prior Q range | 0.6 x max(abs P, 100 MVA) | MVAr | reactive range of a measured PV site | `QCapabilityRatio`, `MinQCapabilityBaseMva` |
| Prior solve iterations | 100 | iterations | Newton cap of the prior solve | `MeasuredInjectionPrior.PriorSolveMaxIterations` |
| Prior voltage sigma | 0.02 | pu | sigma of a solved voltage prior row | `MeasuredInjectionPrior.DefaultVoltagePriorSigmaPu` |
| Phase-angle sigma | 2.0 | degrees | sigma of a `VaDeg` row | `PhaseAngleOptions.SigmaDeg` |
| Nominal frequency | 50 | Hz | time-projection reference | `PhaseAngleOptions.NominalHz` |
| Synthetic seed | 20260713 | integer | sampler seed | `SyntheticMeasurementOptions.Seed` |
| Synthetic sigmas | 0.004 / 1.0 / 1.6 | pu / MW (and MVAr) / MW (and MVAr) | Vm, injection P and Q, flow P and Q | `VmSigmaPu`, `InjSigmaMw`, `FlowSigmaMw` |
| Synthetic noise scale | 1.0 | x | 0 draws nothing | `SyntheticMeasurementOptions.NoiseScale` |
| Synthetic coverage | 1.0 / 1.0 / 1.0 | fraction | Vm, injection, flow | `VmCoverage`, `InjCoverage`, `FlowCoverage` |

## Invariants

| Invariant | Pinned by |
|---|---|
| Exact full measurement sets recover the truth to machine precision (case9, case14); a minimal observable set still does | `WlsEstimatorTests.Exact_full_measurement_set_recovers_case9_truth_to_machine_precision`, `Exact_full_measurement_set_recovers_case14_truth_to_machine_precision`, `Minimal_observable_set_still_recovers_truth` |
| The measurement Jacobian equals central finite differences, including the current rows | `WlsEstimatorTests.Measurement_jacobian_matches_central_finite_differences`, `CurrentMagnitudeMeasurementTests.Current_magnitude_jacobian_matches_central_finite_differences` |
| Neighbourhood injections equal the solver injections to the bit | `SparseEstimatorTests.Neighbour_injection_evaluation_is_bit_identical_to_the_solver_path` |
| A warm start at the truth converges at once; the slack angle never moves | `WlsEstimatorTests.Warm_start_from_truth_converges_immediately`, `Slack_angle_stays_exactly_at_the_model_reference` |
| An undeterminable set reports `Unobservable`, never NaN; an isolated unmeasured island is named | `WlsEstimatorTests.An_undeterminable_set_reports_unobservable_not_nan`, `ObservabilityTests.An_unmeasured_island_is_reported_by_bus_id`, `EstimationRobustnessFixTests.An_island_isolated_by_an_open_branch_is_reported_unmeasured` |
| The same input on the same machine gives the same estimate | `WlsEstimatorTests.Estimation_is_deterministic` |
| Dense and sparse WLS agree within tolerance and remove the same row | `SparseEstimatorTests.Sparse_and_dense_backends_agree_on_a_noisy_set`, `Sparse_backend_identifies_and_removes_the_same_gross_error` |
| The sparse backend re-derives its structure when the measurement set changes | `SparseWlsStructureReusePinTests.Sparse_backend_re_derives_structure_when_the_measurement_set_changes` |
| The sparse residual covariance equals the full-solve loop bit for bit, serial and parallel, at every SIMD tier, with critical rows | `ResidualCovarianceBitIdentityTests` |
| The gain-rank elimination equals the original loop on estimator gains and rank-deficient matrices | `ObservabilityRankBitIdentityTests` |
| The sparse backend names the same free direction as the dense one | `SparseObservabilityDiagnosisTests.Sparse_backend_names_the_free_direction_and_matches_the_dense_diagnosis` |
| The estimate projection equals the dense form to the bit and builds no dense admittance | `EstimationProjectionTests` |
| Dense methods fail loud above the shared bus limit; the limit values are fixed | `DenseEstimationLimitsTests.Dense_methods_fail_loud_above_the_shared_bus_limit`, `The_limits_keep_their_pre_review_values` |
| Every `EstimationMethod` has a CLI name | `EstimateMethodSelectionTests.Every_estimation_method_has_a_cli_name` |
| QR matches WLS on well-conditioned data | `QrEstimatorTests.Qr_method_recovers_truth_and_matches_wls` |
| The chi-square quantile matches table values within 1 percent | `BadDataTests.Chi_square_quantiles_match_table_values_within_one_percent` |
| A clean set passes with no removals; a single gross error is identified and removed | `BadDataTests.A_clean_set_passes_chi_square_with_no_removals`, `A_single_gross_error_is_identified_and_removed` |
| Critical measurements are flagged and never removed | `BadDataTests.Critical_measurements_are_flagged_and_never_removed` |
| Backfill replaces a removed real-power row when opted in and never a reactive row | `BadDataTests.A_removed_measurement_is_backfilled_from_the_prior_when_opted_in`, `Reactive_removals_are_not_backfilled` |
| The residual covariance matches an independent solve | `BadDataCovarianceDiagTests.Residual_covariance_matches_an_independent_solve_path` |
| Huber matches WLS on clean data and shrugs off several gross errors; LAV follows the majority | `SparseEstimatorTests.Huber_matches_wls_on_clean_data_and_shrugs_off_multiple_gross_errors`, `Lav_follows_the_trustworthy_majority` |
| Schweppe-GM reduces to WLS on clean data, resists a gross error, and converges to the Huber fixed point on declared-weight leverage | `SchweppeGmTests`, `EstimationRobustnessFixTests.SchweppeGm_converges_to_the_huber_fixed_point_on_declared_weight_leverage` |
| `lav` and `lav-lp` give bit-identical results; exact LAV converges with no IRLS cap, reports its objective and no chi-square verdict, and its objective is no larger than the IRLS approximation; `lav-irls` keeps the IRLS behaviour | `Cf6LavExactTests.Lav_and_its_alias_lav_lp_give_bit_identical_results`, `Lav_is_now_the_exact_form_with_no_irls_cap`, `A_lav_estimate_reports_its_objective_and_no_chi_square_verdict`, `The_exact_lav_objective_is_no_larger_than_the_irls_approximation`, `Lav_irls_keeps_the_old_behaviour_on_the_q13_set_cap_reached_status_converged` |
| A truncated IRLS reports `IrlsCapReached` without changing the status; WLS never reports it | `Q13WsThirteenDecisionTests.Lav_with_a_gross_outlier_runs_to_the_cap_and_says_so_without_changing_its_status`, `Huber_on_an_exact_set_settles_at_once_and_reports_no_cap`, `Plain_wls_never_reports_an_irls_cap` |
| Constrained honours zero injections exactly | `SparseEstimatorTests.Constrained_method_honours_zero_injections_exactly` |
| The constraint scale is `sqrt(max diag G)` rounded down to a power of two; hard zero-injection rows converge on case57 and case118; a case with one hard row and a case with none are unchanged | `ConstrainedKktEquilibrationTests` |
| Feasible keeps every Vm positive, matches Constrained on clean data to 9 decimals, and reports limit breaches without demoting by default | `FeasibleEstimationTests.Feasible_keeps_every_voltage_strictly_positive`, `Feasible_matches_Constrained_on_clean_data`, `Operational_limits_are_reported_but_do_not_demote_by_default` |
| Feasible holds generator Q, angle differences and thermal ratings at solve time | `FeasibleEstimationTests.Ring_B_holds_generator_reactive_at_its_capability_at_solve_time`, `Ring_B_holds_across_branch_angle_differences_under_the_cap`, `Ring_B_holds_a_branch_at_its_thermal_rating_under_the_limit` |
| The acceptance gate demotes a state that breaks a hard law, and KCL is audited against zero-injection rows only | `FeasibleEstimationTests.The_acceptance_gate_demotes_to_Infeasible_when_a_hard_law_fails`, `FeasibleKclGateTests` |
| Feasible equilibration makes one active angle row solvable and synthetic telemetry solvable at the first iteration; an angle difference inside the band is held, not pushed onto the limit | `FeasibleKktEquilibrationTests` |
| The generator Q band is a fraction of the range with a floor; inverted limits fail loud; case118 with noise converges in 8 iterations at J 877.8811650453016; noise-free data is fitted exactly with nothing removed; the case14 angle cap at 0.7 of the WLS spread converges in 68 iterations at J 72.38263564898884 and holds the cap | `FeasibleGeneratorQHysteresisTests` |
| Parallel circuits give one angle row and one thermal row per group; a degenerate constraint block with observable measurements is `Infeasible`, not `Unobservable` | `EstimationRobustnessFixTests.Feasible_step_enforces_one_angle_row_across_parallel_circuits`, `Feasible_step_enforces_one_thermal_row_per_parallel_group`, `A_degenerate_constraint_block_with_observable_measurements_is_infeasible_not_unobservable` |
| An invalid feasibility envelope fails loud before solving | `FeasibilityEnvelopeValidationTests` |
| HTI identifies a gross error and exonerates clean rows; on the case9 exact set it equals plain WLS in iterations, `J`, `Vm` and `VaDeg` bit for bit; it respects the cap and refuses what it cannot mean | `HypothesisTestingBadDataTests.Identifies_a_gross_error_and_exonerates_clean_measurements`, `R062EstimationWiringTests.Hti_on_a_clean_set_removes_nothing_and_is_bit_identical_to_plain_wls`, `Hti_identifies_and_removes_a_gross_error_and_the_final_fit_passes`, `Hti_respects_the_removal_cap_and_says_so`, `Hti_refuses_what_it_cannot_mean` |
| The FDIA screen raises no flag on a clean estimate, flags a consistent but out-of-band state, and, over 100 noisy case9 seeds of which at least 90 converge, flags at most 2 percent of the converged estimates | `FdiaScreenTests` (the clean noisy case30 set at the default seed is flagged `suspected`: see ch. 21) |
| The noise-floor analysis recovers per-class sigma scaling, never flags small classes, skips non-finite residuals, and reads exported residuals back exactly | `NoiseFloorAnalysisTests`, `R062EstimationWiringTests.Export_residuals_read_back_exactly_and_give_the_same_noise_floor_report` |
| Phase-angle: parsing, time projection, common-mode cancellation, datum anchoring, genuine angles sharpen the estimate | `PhaseAngleFeedTests`, `PhaseAngleAnchorTests` |
| The sampler is deterministic per seed, exact at zero noise, strides coverage evenly, and produces a valid horizon-safe set | `SyntheticMeasurementTests` |
| The measurement set round-trips through JSON, rejects a wrong schema and a future stamp, and names every offender | `EstimationMeasurementTests` |
| The export carries the versioned schema, the model, the fit, the residuals and the removals, and serialises NaN | `EstimateExportContractTests` |

## Worked example

`gridsim estimate case14 --synth` samples a measurement set from case14's own solved power flow (seed 20260713, noise
scale 1, all coverages 1) and estimates it. The captured output carries no timings, and the estimator is
deterministic, so the figures below reproduce on any machine that runs the same CPU tier.

```
$ gridsim estimate case14 --synth

=== WLS state estimate: case14 ===
  status: Converged   iterations: 5   max |dx|: 2.72E-009
  measurements: 122   states: 27   redundancy (dof): 95
  observability: observable (rank 27/27)
  consistency: J = 80.01 vs chi2(95%) = 118.75 -> PASS
  vs synthetic truth: max |dVm| = 1.330E-003 pu   max |dVa| = 2.248E-001 deg

  -> results\estimate-case14
[exit 0]
```

Recount the quantities from the model.

- **State size.** case14 has 14 buses and 1 slack, so `s = (14 - 1) + 14 = 27`. The gain rank is 27 of 27, so the set is
  observable.
- **Measurements.** The sampler emits 14 `VmPu`, 14 `PInjMw` and 14 `QInjMvar` rows, and four flow rows (`PFlowMw` and
  `QFlowMvar` at both ends) for each of the 20 branches: `14 + 28 + 80 = 122`.
- **Degrees of freedom.** `dof = m - s = 122 - 27 = 95`.
- **Convergence.** The last correction is `2.72E-009 < Tolerance = 1e-8`, reached on the fifth Gauss-Newton step.

The chi-square threshold at 95 percent for `k = 95` follows from the equations above:

```
p   = 0.95
z_p = NormalQuantile(0.95) = 1.644853625133699          (p lies in the central region [0.02425, 0.97575])
t   = 2 / (9 * 95)         = 2 / 855 = 0.0023391812865497
sqrt(t)                    = 0.04836508334066744
cube = 1 - t + z_p * sqrt(t) = 1 - 0.0023391812865497 + 0.07955348266279032 = 1.0772143013762405
chi2(95, 0.95) = 95 * cube^3 = 95 * 1.2499894045368987 = 118.74899343100537   -> printed 118.75
```

`J = 80.01 <= 118.75`, so `ChiSquarePassed` is true with a margin of 38.74, no row is tested for removal, and the
estimate stands. The same arithmetic gives the other captured thresholds: case118 has `m = 1098`, `s = 235` and
`dof = 863`, so `chi2(863, 0.95) = 932.4531178584928`, printed as 932.45 against `J = 877.80`, which also passes
(capture `case118-synth.txt`).

WLS, QR, Constrained and Feasible agree to the printed digits on clean data; the robust methods differ by the amounts in
the table. The captures below are the same set under each method
(`J`, iterations summed over IRLS passes, and the worst error against truth):

| `--method` | Iterations | `J` | max `|dVm|` (pu) | max `|dVa|` (deg) |
|---|---|---|---|---|
| `wls` | 5 | 80.01 | 1.330E-003 | 2.248E-001 |
| `qr` | 5 | 80.01 | 1.330E-003 | 2.248E-001 |
| `constrained` | 5 | 80.01 | 1.330E-003 | 2.248E-001 |
| `feasible` | 6 | 80.01 | 1.330E-003 | 2.248E-001 |
| `huber` | 13 | 80.18 | 1.218E-003 | 2.220E-001 |
| `lav` | 32 | 86.75 | 1.114E-003 | 2.064E-001 |
| `schweppe-gm` | 29 | 80.88 | 1.601E-003 | 2.148E-001 |

WLS, QR and Constrained print identical figures here because case14 has no hard zero-injection row in the synthetic set,
so Constrained solves the free normal equations; Feasible takes one extra iteration, ends with a tighter last correction
(`5.46E-011` against `2.72E-009`), and reports the same `J`. The robust methods report `J` against the declared sigmas,
which is why their `J` is larger than the WLS minimum: they minimise a different objective.

The bad-data loop is exercised by scaling the sampled noise to 1.6 times the declared sigmas, so chi-square must fail:

```
$ gridsim estimate case14 --synth --noise 1.6

=== WLS state estimate: case14 ===
  status: Converged   iterations: 25   max |dx|: 2.78E-011
  measurements: 122   states: 27   redundancy (dof): 89
  observability: observable (rank 27/27)
  consistency: J = 137.92 vs chi2(95%) = 112.02 -> FAIL
  removed bad data (pass 0): PFlowMw @ branch 2->5 (from)  rN = 3.6
  removed bad data (pass 1): QFlowMvar @ branch 2->3 (to)  rN = 3.6
  removed bad data (pass 2): VmPu @ bus 14  rN = 3.2
  removed bad data (pass 3): QInjMvar @ bus 12  rN = 3.3
  removed bad data (pass 4): QInjMvar @ bus 13  rN = 3.2
  removed bad data (pass 5): PInjMw @ bus 1  rN = 3.0
  vs synthetic truth: max |dVm| = 5.456E-003 pu   max |dVa| = 3.968E-001 deg

  -> results\estimate-case14
[exit 0]
```

Six rows had `rN > 3.0` and were removed one per pass, so the final fit has `dof = 122 - 6 - 27 = 89`, and
`chi2(89, 0.95) = 112.02`. The remaining rows all have `rN <= 3.0`, so nothing is identifiable, and the loop stops with
the failure reported (`FAIL`) instead of force-fitting. Scaling every sigma by 1.6 is a miscalibration, not six gross
errors, and the loop's refusal to keep removing rows is what shows it. `--hti` on the same set identifies the same six rows
jointly:

```
$ gridsim estimate case14 --synth --noise 1.6 --hti

=== WLS state estimate: case14 ===
  status: Converged   iterations: 5   max |dx|: 4.45E-009
  measurements: 122   states: 27   redundancy (dof): 89
  observability: observable (rank 27/27)
  consistency: J = 137.92 vs chi2(95%) = 112.02 -> FAIL
  removed bad data (HTI pass 0): PFlowMw @ branch 2->5 (from)  t = 3.6  estimated error = -0.0589804 pu_base_mva (-5.89804 MW)
  removed bad data (HTI pass 0): QFlowMvar @ branch 2->3 (to)  t = 3.6  estimated error = -0.0596507 pu_base_mva (-5.96507 Mvar)
  removed bad data (HTI pass 0): VmPu @ bus 14  t = 3.3  estimated error = -0.0148523 pu
  removed bad data (HTI pass 0): QInjMvar @ bus 12  t = 3.3  estimated error = 0.0490311 pu_base_mva (4.90311 Mvar)
  removed bad data (HTI pass 1): QInjMvar @ bus 13  t = 3.2  estimated error = -0.0596492 pu_base_mva (-5.96492 Mvar)
  removed bad data (HTI pass 2): PInjMw @ bus 1  t = 3.0  estimated error = -0.0484199 pu_base_mva (-4.84199 MW)
  bad-data method: HTI, 4 pass(es), 20 iteration(s) in all, stopped: NothingIdentified
  vs synthetic truth: max |dVm| = 5.456E-003 pu   max |dVa| = 3.968E-001 deg

  -> results\estimate-case14
[exit 0]
```

HTI removes four rows in its first pass, one in each of the next two, and stops on the fourth pass with
`NothingIdentified`. It reaches the same final state as LNR here (identical `J`, dof and truth error), and the
`Iterations` field counts only the final estimate, while the summary line counts 20 across passes. The CLI exits 0
because the estimate is converged and observable; a failed chi-square verdict is a finding in the output, not an exit code.

Finally, the noise floor of a clean run. Two commands, the second reading the export the first wrote:

```
$ gridsim estimate case14 --synth --out results/nf-case14

=== WLS state estimate: case14 ===
  status: Converged   iterations: 5   max |dx|: 2.72E-009
  measurements: 122   states: 27   redundancy (dof): 95
  observability: observable (rank 27/27)
  consistency: J = 80.01 vs chi2(95%) = 118.75 -> PASS
  vs synthetic truth: max |dVm| = 1.330E-003 pu   max |dVa| = 2.248E-001 deg

  -> results\nf-case14
[exit 0]
$ gridsim estimate-noise-floor results/nf-case14 --out results/nf-floor

=== Measurement noise floor: 1 estimate(s), 122 residual(s) (0 NaN skipped) ===
  source                   kind         count  k_robust    k_std     bias  verdict
  synthetic                QInjMvar        14     1.183    0.933   -0.174  ok
  synthetic                PFlowMw         40     1.076    0.799   -0.119  ok
  synthetic                QFlowMvar       40     1.048    0.887   -0.060  ok
  synthetic                VmPu            14     0.984    0.849   -0.187  ok
  synthetic                PInjMw          14     1.011    0.871   -0.006  ok
  0 of 5 class(es) miscalibrated (tolerance 0.25, k = true noise / declared sigma)

  -> results\nf-floor
[exit 0]
```

Every class has `|k_robust - 1| <= 0.25` and `|bias| <= 0.25`, so none is flagged; the synthetic noise is drawn at the
declared sigma, so `k` is 1 within the sampling spread of 14 to 40 samples per class. `QInjMvar` is the furthest from 1
at 1.183.

## Source map

All files are under `src/GridSim.Core/Estimation/` unless stated.

| File | Type or members | Responsibility |
|---|---|---|
| `Measurement.cs` | `Measurement`, `MeasurementKind`, `BranchEnd` | the telemetered value and the eight kinds |
| `MeasurementSet.cs` | `MeasurementSet` | `gridsim-measurements/1`, load, validate, branch resolution |
| `MeasurementModel.cs` | `MeasurementModel` | per-unit conversion, `h(x)`, Jacobian, structural columns, `InjectionQRow`, `BranchFlowRow` |
| `JacobianSinks.cs` | `IJacobianSink`, `DenseJacobianSink`, `SparseJacobianRows` | the one Jacobian path feeding both backends |
| `WlsStateEstimator.cs` | `WlsStateEstimator`, `EstimationOptions`, `EstimationMethod` | the loop, `ApplyStep`, IRLS, chi-square, removal, backfill, Feasible gate |
| `WlsStep.cs` | `IWlsStep`, `DenseWlsStep`, `SparseWlsStep`, `ConstrainedDenseWlsStep`, `QrWlsStep` | step backends |
| `FeasibleWlsStep.cs` | `FeasibleWlsStep` | KKT with active set, hysteresis bands, row equilibration |
| `FeasibilityConstraints.cs` | `FeasibilityConstraints` | the Feasible envelope and its validation |
| `ObservabilityAnalysis.cs` | `ObservabilityAnalysis`, `ObservabilityReport` | island sweep and gain rank |
| `ChiSquare.cs` | `ChiSquare` | Wilson-Hilferty quantile with Acklam normal quantile (exact forms at 1 and 2 dof) |
| `BadDataAnalysis.cs` | `BadDataAnalysis` | `Omega`, normalised residuals, criticality, state covariance |
| `HypothesisTestingBadData.cs` | `HypothesisTestingBadData`, `HtiIdentified` | joint suspect test |
| `HtiBadDataEstimation.cs` | `HtiBadDataEstimation`, `HtiEstimationResult`, `HtiRemoval`, `HtiStopReason` | the HTI loop |
| `Security/FdiaScreen.cs` | `FdiaScreen`, `FdiaScreenReport` | false-data-injection screen |
| `NoiseFloorAnalysis.cs` | `NoiseFloorAnalysis`, `NoiseFloorClass`, `NoiseFloorReport` | per-class sigma calibration |
| `EstimationResult.cs` | `EstimationResult`, `EstimationStatus`, `ChiSquareOutcome`, `MeasurementResidual`, `BadDataRemoval` | result, statuses, projection to a power-flow result |
| `EstimationTrace.cs` | `EstimationTrace`, `EstimationIteration` | per-iteration diagnostics |
| `EstimationAuditTrail.cs` | `EstimationAuditTrail` | tamper-evident record of an estimate |
| `DenseEstimationLimits.cs` | `DenseEstimationLimits` | the size caps |
| `MeasuredInjectionPrior.cs` | `MeasuredInjectionPrior` | measured-injection power flow and voltage prior |
| `PhaseAngleFeed.cs` | `PhaseAngleFeed`, `PhaseAngleOptions`, `PhaseAngleObs` | phase-angle rows |
| `SyntheticMeasurements.cs` | `SyntheticMeasurements`, `SyntheticMeasurementOptions` | deterministic sampler |
| `ProjectionStatistics.cs` | `ProjectionStatistics` | projection statistics (internal, not used by an estimator) |
| `LinearPmuStateEstimator.cs` | `LinearPmuStateEstimator` | single-solve linear PMU estimator (`pmu-estimate`) |
| `PmuMeasurementSetAdapter.cs` | `PmuMeasurementSetAdapter`, `PmuPhasorSet` | polar set rows to rectangular phasors for `pmu-estimate` |
| `LavLinearProgram.cs` | `LavLinearProgram` | the exact LAV linear program of each iterate |
| `Dse/` | Kalman, unscented and ensemble estimators, `IProcessModel`, `FrequencyRocofFilter` | recursive estimation, see ch. 18 |
| `src/GridSim.Core/Numerics/NumericTolerances.cs` | `NumericTolerances` | pivot floor, critical fraction, constraint weight scale |
| `src/GridSim.Core/Numerics/SimdKernels.cs` | `SimdKernels` | bit-exact row and dot kernels |
| `src/GridSim.Cli/Program.cs` | `RunEstimate`, `RunEstimateNoiseFloor` | the `estimate` and `estimate-noise-floor` verbs |

## Limits

- The dense step (`DenseWlsStep` with a non-CPU linear solver) and the sparse step are not bit-identical to each other;
  on the CPU a `Wls` or robust estimate always takes the sparse step, so this applies only to a GPU estimate.
- Above 2,500 states the sparse backend runs the island sweep only, reports `Rank = -1`, and detects numerical
  singularity through the factorisation.
- `Constrained`, `Feasible` and `Qr` are dense and throw `NotSupportedException` above 3,000 buses; HTI and the state
  covariance stop at 2,000 states.
- The Constrained method's residual covariance treats a hard equality as a row of very large weight, which is an
  approximation of the equality.
- The angle-difference and thermal active rows clamp their right-hand side to hold the current value inside the band, so
  a row that enters its band from below is held below the limit. `FeasibleKktEquilibrationTests` pins that hold for an
  angle-difference row; no test exercises it for a thermal row.
- `ProjectionStatistics` is not wired into `SchweppeGm`, which takes its leverage from the hat-matrix normalised residual (R259).
- The exact LAV uses a dense basis inverse and is refused above `DenseEstimationLimits.StateCovarianceMaxState` states; `lav-irls` is an approximation of the LAV optimum.
- Prior backfill replaces real-power rows only, so a removed reactive row leaves its region less observed.
- The estimator does not solve a per-feed phase-angle offset; an absolute feed must be re-referenced before it is authored.

## See also

- [3. Power flow](<0003 Power flow.md>) - the Newton engine whose injections and stamps the measurement model reuses
- [4. Sparse and GPU numerics](<0004 Sparse and GPU numerics.md>) - CSR and CSC, Gilbert-Peierls LU, the ordering
- [8. Provenance and invariants](<0008 Provenance and invariants.md>) - the horizon guard and the bit-identity table
- [9. GDA integration](<0009 GDA integration.md>) - the measurement products and the estimated-state lake feed
- [10. File and wire formats](<0010 File and wire formats.md>) - `gridsim-estimate-export/1`
- [11. Validation](<0011 Validation.md>) - cross-validation against pandapower and the independent optimality check
- [16. Physics conformance](<0016 Physics conformance and the constrained estimator.md>) - the audit engine behind the Feasible acceptance gate
- [18. Real-time, distribution and dynamic estimation](<0018 Real-time, distribution and dynamic estimation.md>) - the recursive filters and the serving engine
- [Robust GM leverage design record](design/robust-gm-leverage.md)
- User manual: [21. State estimation](<../manual/0021 State estimation.md>), [22. Estimating GB history](<../manual/0022 Estimating GB history.md>), [`estimate`](<../manual/0019 CLI reference.md#estimate>), [`estimate-noise-floor`](<../manual/0019 CLI reference.md#estimate-noise-floor>)
