# Robust GM estimation: leverage down-weighting, Schweppe-Huber, high-breakdown LTS/LMS, and HTI

> **Status (verified 1.95.091):** Partly implemented: Schweppe-Huber GM (`EstimationMethod.SchweppeGm`), the projection-statistics routine (internal, not wired into `SchweppeGm`) and hypothesis-testing identification (`HypothesisTestingBadData`, `HtiBadDataEstimation`, remove-only) exist. High-breakdown LTS and LMS, the `LeverageSource` option and leverage diagnostics in the result and export are not implemented. HTI correct-in-place exists since October 2026 (N061) as `HtiAction.CorrectThenReverify`, opt-in (`estimate --hti --hti-correct`); removal stays the default.
> **Implemented in:** `src/GridSim.Core/Estimation/WlsStateEstimator.cs` (`EstimationMethod.SchweppeGm`, `EstimationOptions.HuberK`), `src/GridSim.Core/Estimation/WlsStep.cs` (`IWlsStep.NormalizedResiduals`), `src/GridSim.Core/Estimation/BadDataAnalysis.cs`, `src/GridSim.Core/Estimation/ProjectionStatistics.cs` (`ProjectionStatistics`, internal), `src/GridSim.Core/Estimation/HypothesisTestingBadData.cs` (`HypothesisTestingBadData.Identify`, `HtiIdentified`), `src/GridSim.Core/Estimation/HtiBadDataEstimation.cs` (`HtiBadDataEstimation`, `HtiEstimationResult`, `HtiRemoval`, `HtiStopReason`). CLI: `estimate --method schweppe-gm` (alias `gm`) and `estimate --hti`.
> **Design-to-code name map:**
>
> | Design name | Implemented as |
> |---|---|
> | `Estimation/RobustGm.cs` | the `SchweppeGm` branch of the IRLS loop in `WlsStateEstimator.cs` |
> | `IWlsStep.LeverageDiagonal`, `HatDiagonal` | not implemented; leverage is taken from `IWlsStep.NormalizedResiduals` |
> | `MeasurementResidual.Leverage`, `leverage` field in `gridsim-estimate-export/1` | not implemented |
> | `EstimationOptions.Leverage`, `LeverageSource`, `LeverageFallbackToHat`, `SchweppeCutoff` | not implemented; `ProjectionStatistics` exists but no option selects it |
> | `Estimation/HypothesisTestingIdentification.cs` (`HypothesisTestingIdentification`) | `HypothesisTestingBadData.cs` (`HypothesisTestingBadData.Identify`) and `HtiBadDataEstimation.cs` |
> | `BadDataId`, `BadDataCorrection`, `HtiMaxSuspects`, `EstimationResult.Corrected` | not implemented. `HtiAction { Remove, CorrectThenReverify }` exists (N061) as a parameter of `HtiBadDataEstimation.Estimate`, default `Remove`; corrections are reported in `HtiEstimationResult.Corrected` (`HtiCorrection`), not on `EstimationResult` |
> | `Estimation/HighBreakdownDriver.cs`, `EstimationMethod.Lts` / `Lms`, `LtsSubsetCount`, `LtsCoverage`, `LtsSeed` | not implemented |
> | `RobustGmLeverageTests.cs` | `tests/GridSim.Tests/SchweppeGmTests.cs`, `ProjectionStatisticsTests.cs` |
> | `HtiTests.cs` | `tests/GridSim.Tests/HypothesisTestingBadDataTests.cs`, `R062EstimationWiringTests.cs` |
> | `HighBreakdownTests.cs` | not present |
>
> This is a design record. The current behaviour is documented in the technical manual chapter [6. State estimation](<../0006 State estimation.md>).

## Implementation status (October 2026)

| Item (section) | Status | Where it lives / what differs from this spec |
|---|---|---|
| Schweppe-Huber GM with hat-diagonal leverage (3.1, 4) | Shipped | `EstimationMethod.SchweppeGm` in `WlsStateEstimator`; leverage taken from `IWlsStep.NormalizedResiduals` (no separate `LeverageDiagonal`). `EstimationOptions.HuberK` exists. CLI: `estimate --method schweppe-gm` (alias `gm`), not `schweppegm`. |
| Projection statistics (3.2) | Code exists, not wired | `Estimation/ProjectionStatistics.cs` (internal). There is no `LeverageSource` option and `SchweppeGm` does not use it (R259). |
| HTI (6) | Shipped, remove-only | `HypothesisTestingBadData.Identify` (suspect set, joint error estimate, per-suspect test) and the loop `HtiBadDataEstimation` (R062), which replaces sequential LNR when asked. CLI: `estimate --hti`. Correct-in-place (`HtiAction.CorrectThenReverify`, N061): opt-in with `--hti-correct`, corrections in `hti-corrections.csv`; a row identified again after its correction is removed; corrections plus removals capped at `MaxBadDataRemovals`. Not built: the `BadDataId` option, the correlation-based suspect-set expansion, `HtiMaxSuspects`, and a `Corrected` list on `EstimationResult`. HTI is dense (refused above 2,000 states) and refuses the robust methods. |
| High-breakdown LTS / LMS (5) | NOT IMPLEMENTED | There is no `Lts` or `Lms` `EstimationMethod`, no `HighBreakdownDriver`, and no `LtsSubsetCount` / `LtsCoverage` / `LtsSeed` options. Everything in section 5, and the LTS parts of sections 7, 8, 10 and 11, is still only a proposal. |
| Leverage diagnostics (7.3, 7.5, 7.6) | Not implemented | No `IWlsStep.LeverageDiagonal`, no `MeasurementResidual.Leverage`, no `leverage` field in `gridsim-estimate-export/1`. |

---

*Original specification follows. It contains equations, data-structure and file layout, the
seams to extend, and the validation plan. No C# is written here.*

Governing constraint: everything below is a **measurement-only static filter** (no `Dynamics/`
process model, no forward step, no clock read). Under `UnderstandingRestrictions.md` sec. 3 and
the review gate (`RestrictionReview.Reviewed` (`src/GridSim.Core/Diagnostics/RestrictionReview.cs`)), such estimators are filters, not predictors: they need no
entry and are unconditionally permitted. Section 9 discharges this obligation explicitly and
states the invariants the suite must assert. Read `UnderstandingRestrictions.md` and the review
log before implementing.

---

## 1. Purpose and scope

The estimator today (`src/GridSim.Core/Estimation/WlsStateEstimator.cs`) carries two robust
methods behind the `IWlsStep` seam - Huber and LAV, both IRLS outer loops that reweight from the
**raw standardized residual** `r~_i = |z_i - h_i(x)| / sigma_i` - and a **sequential
largest-normalised-residual (LNR)** bad-data loop (`Estimation/BadDataAnalysis.cs`). Both have a
known blind spot: a gross error sitting on a **high-leverage** measurement at a **low-redundancy**
bus. The error pulls the fitted state toward itself (leverage), which keeps its own raw residual
small (masking / smearing), so `k / r~_i` never triggers in the Huber loop and the LNR test never
promotes it to "worst". `tools/crossval/se_residual_check.py --leverage` already exposes exactly
this quantity (the hat-matrix diagonal `K_ii`) as the independent arbiter of criticality; this
design brings that leverage into the estimator's decision logic.

This document specifies four additions, in increasing order of assurance and cost:

1. **Leverage (hat-matrix diagonal) down-weighting** inside the IRLS loop, so high-leverage rows
   are judged against a **leverage-adjusted** residual instead of the raw one (sec. 3, sec. 4).
2. A **Schweppe-Huber Generalized-M (GM)** weight function that formalises (1) with a
   leverage-scaled Huber threshold (sec. 4).
3. **High-breakdown LTS / LMS** methods as new `EstimationMethod` options that reuse the
   `IWlsStep` seam for their elemental/subset solves, used as the 50%-breakdown initial fit that
   feeds the GM reweighting (sec. 5).
4. **Hypothesis-Testing Identification (HTI)** for multiple interacting bad data, replacing the
   single-shot sequential LNR loop with a joint suspect-set error estimate and per-suspect
   hypothesis test (sec. 6).

Every addition is a drop-in behind existing seams; the estimator loop's ownership boundary (start
point, damping, convergence, chi-square) is unchanged. Companion reference doc:
`docs/technical/06-state-estimation.md`.

---

## 2. The failure this fixes (worked mechanism)

Let `H = dh/dx` at the estimate, `W = diag(1/sigma_i^2)` the measurement weights, and
`G = H^T W H` the gain (all as formed in `DenseWlsStep.FormNormalEquations`). The **hat (influence)
matrix** is

```
K = H G^-1 H^T W                         (m x m)
```

and its diagonal `K_ii` in [0, 1] is the **leverage** of measurement `i`. The residual of a
converged WLS fit relates to the true measurement error `e` by

```
r = z - h(x^) = S e,     S = I - K,     Cov(r) = Omega = S W^-1 = W^-1 - H G^-1 H^T
```

so the residual-covariance diagonal is `Omega_ii = sigma_i^2 (1 - K_ii)` and the normalised
residual already computed in `BadDataAnalysis.NormalizedResiduals` is

```
r^N_i = |r_i| / sqrt(Omega_ii) = |r_i| / (sigma_i * sqrt(1 - K_ii)).
```

**Key identity (the whole design leans on it).** `BadDataAnalysis` already computes
`hGh = h_i^T G^-1 h_i` and `Omega_ii = sigma_i^2 - hGh`. Therefore the leverage is recoverable for
free from quantities already in hand:

```
K_ii = 1 - Omega_ii / sigma_i^2 = w_i * (h_i^T G^-1 h_i)        (w_i = 1/sigma_i^2)
```

This is bit-for-bit the quantity `se_residual_check.py` computes as
`K[r] = hrow @ lu.solve(hrow) * W[r]` - so the independent validator is already the arbiter (sec.
10).

**Why plain Huber / LNR miss the high-leverage outlier.** When `K_ii -> 1` (a measurement that is
the near-sole observer of some state direction - a radial GSP's single injection meter), the fit
is dragged onto that measurement, so `r_i` (hence `r~_i = |r_i|/sigma_i`) stays *small* even when
the reading is grossly wrong. The Huber factor `k / r~_i` therefore stays at 1 (no down-weight) and
LNR's `r~`-driven ranking never selects it. The cure is to judge the measurement against
`sqrt(1 - K_ii)` - the leverage-adjusted residual `r^N_i` - which *inflates* precisely at high
leverage and defeats the masking. That single substitution is the core of the Schweppe-Huber GM
weight (sec. 4).

---

## 3. Two leverage models

The estimator needs a per-measurement leverage weight `a_i` in (0, 1], with `a_i -> 1` for a
well-redundant measurement and `a_i -> 0` for a leverage point. Two sources are offered, selected
by `EstimationOptions.Leverage` (sec. 7). They trade cost against masking-immunity.

### 3.1 Hat-matrix diagonal (`LeverageSource.HatDiagonal`) - cheap, reuses existing machinery

```
a_i = sqrt(1 - K_ii) = sqrt(Omega_ii) / sigma_i,     K_ii in [0, 1]
```

This is **already computed** by `step.NormalizedResiduals` (via `Omega_ii`); no new gain solve is
needed. It is the default. Caveat: `K_ii` is derived from the *reweighted* gain, so under a single
gross error it is only mildly corrupted, but under **multiple interacting** errors the hat diagonal
can itself be masked. For that regime use 3.2 or the LTS initialiser (sec. 5).

Boundary cases:
- A **critical** measurement has `Omega_ii <= sigma_i^2 * 1e-8` (the `CriticalFloor` in
  `BadDataAnalysis`), i.e. `K_ii ~ 1`, `a_i ~ 0`. A critical row is the sole observer of a state
  direction; it **cannot** be down-weighted without losing observability. The reweight must clamp
  its factor to 1 (leave it at its declared weight) and rely on HTI/constraints, never IRLS, to
  vet it. Concretely: when `NormalizedResiduals` returns `critical[i] == true` (NaN normalised
  residual), the GM factor for `i` is 1.
- `sum_i K_ii = trace(K) = s` (state size). This is a cheap, exact determinism/sanity invariant
  the test suite asserts (sec. 10.4).

### 3.2 Projection statistics (`LeverageSource.ProjectionStatistic`) - masking-immune, dense-only

The hat diagonal measures leverage against the *possibly-contaminated* fit. **Projection
statistics** (Mili, Cheniae, Rousseeuw) measure how outlying each Jacobian row `h_i` (a point in
state space `R^s`) is among all rows, using a high-breakdown location/scale, so they are immune to
the masking that corrupts `K_ii`. Algorithm, computed once at the linearisation point from the
Jacobian pattern (no operating-point dependence beyond `H(x)`):

```
Points:      p_i = h_i (row i of H), i = 1..m, in R^s
For each candidate direction u (unit vector):
    project:  t_j = p_j . u  for all j
    center:   med = median_j(t_j)
    scale:    MAD = 1.4826 * median_j |t_j - med|
    PS_i(u) = |t_i - med| / MAD
PS_i = max over directions u of PS_i(u)
```

Directions `u` are taken as the normalised vectors from each point to the coordinate-wise (L1)
median of the point cloud - the standard `m`-direction approximation - giving `O(m^2 s)` cost.
The leverage weight is the Schweppe cutoff form:

```
a_i = min( 1, (d / PS_i)^2 ),     d = sqrt(chi-square_{nu, 0.975})
```

where `nu` is the count of nonzero entries of `h_i` (its structural column count from
`MeasurementModel.StructuralColumns`) and `chi-square_{nu, p}` uses the existing
`Estimation/ChiSquare.cs` quantile. A row with normal leverage has `PS_i <= d`, hence `a_i = 1`
(no penalty); a leverage point has `PS_i >> d`, hence `a_i` small.

**Scaling doctrine (matches the codebase's dense/sparse split).** `O(m^2 s)` PS does not scale to
gb-full. Projection statistics are therefore computed on the **dense backend only** (state size
`<= SolverSelector` dense threshold, the same gate `Constrained`/`Feasible` use). Above it,
`ProjectionStatistic` selection **fails loud** with `NotSupportedException` (the established
"dense only, fail loud rather than silently degrade" convention), or - if
`EstimationOptions.LeverageFallbackToHat` is set - silently falls back to `HatDiagonal`, whose PS
approximation the sparse Gilbert-Peierls factorisation already supports for free. Default: fail
loud.

---

## 4. The Schweppe-Huber GM weight function

### 4.1 Objective and weight

The Schweppe-Huber Generalized-M estimator minimises

```
J(x) = sum_i  a_i^2 * sigma_i^2 * rho( r_Si ),      r_Si = r_i / (sigma_i * a_i)
```

with `rho` the Huber loss and `a_i` the leverage weight (sec. 3). `r_Si` is the
**leverage-adjusted standardized (Schweppe) residual**: the raw standardized residual divided by
the leverage weight. Differentiating and casting as IRLS (the same reduction the current loop
uses), the effective diagonal WLS weight on measurement `i` becomes

```
W_i,eff = q(r_Si) * w_i,     w_i = 1/sigma_i^2,     q(r) = psi(r)/r

Huber psi:    q(r_Si) = 1                 if |r_Si| <= k
              q(r_Si) = k / |r_Si|        if |r_Si| >  k        (k = 1.345 default)
```

**This is a one-line change to the existing IRLS block.** Compare the current reweight in
`WlsStateEstimator.Estimate` (the `EstimationMethod.Huber` arm):

```
current  Huber:   r~_i  = |z_i - h_i| / sigma_i             factor = r~_i <= k ? 1 : k/r~_i
new  SchweppeGm:  r_Si  = |z_i - h_i| / (sigma_i * a_i)     factor = r_Si <= k ? 1 : k/r_Si
```

When `a_i = sqrt(1 - K_ii)` (sec. 3.1), `r_Si` equals the normalised residual `r^N_i` exactly, so
the implementation can obtain `r_Si` directly from `step.NormalizedResiduals(vm, va, residualPu)`
- **no new numerics** - and apply the Huber `q()` to it. When `a_i` comes from projection
statistics (sec. 3.2), `a_i` is computed once (masking-immune) and held constant across IRLS
passes; `r_Si` is then `r~_i / a_i`.

### 4.2 Integration into the existing IRLS loop

The change is localized to the reweight block of `WlsStateEstimator.Estimate` (currently lines
~261-279) and the `maxOuter` / `wEff` gating (lines 214, 235, 259, 289):

1. Extend the "is a robust IRLS method" predicate to include `SchweppeGm` wherever
   `Method is Huber or Lav` appears (weights cloned into `wEff`, `maxOuter = 10`, chi-square
   against original sigmas, removal loop skipped by default - or handed to HTI, sec. 6).
2. In the reweight, for `SchweppeGm`:
   - If `Leverage == HatDiagonal`: call `step.NormalizedResiduals(vm, va, residualPu)` (which the
     loop already computes for the removal path) to get `normalized[i]` and `critical[i]`.
     `r_Si = normalized[i]`. For `critical[i]`, set `factor = 1`.
   - If `Leverage == ProjectionStatistic`: `r_Si = (|z_i - h_i| / sigma_i) / a_i`, with `a_i` from
     the once-computed PS vector.
   - `factor = r_Si <= HuberK ? 1 : HuberK / r_Si`, then `wEff[i] = w[i] * factor`, and the
     existing `maxFactorChange < 1e-3` convergence test on the outer loop is unchanged.
3. Expose the Huber `k` (hardcoded `huberK = 1.345`) as `EstimationOptions.HuberK` so Huber and
   SchweppeGm share it; default preserves today's value exactly.

**Reduces-to invariants** (tested, sec. 10.4):
- On clean data (all `r_Si <= k`), every `factor == 1`, so `W_eff == W` and **SchweppeGm is
  bit-for-bit WLS** - it must reproduce the machine-precision recovery fixture
  (`NoiseScale = 0`) exactly.
- With `a_i == 1` forced (leverage disabled), SchweppeGm is **bit-for-bit the existing Huber**.
- SchweppeGm never removes a measurement; like Huber/LAV it down-weights smoothly and reports
  chi-square against the original sigmas for comparability.

---

## 5. High-breakdown LTS / LMS as `EstimationMethod` options

M-estimators (Huber, LAV, SHGM) have a breakdown point of at most `1/(m)` in the presence of
leverage points - a single bad leverage point can still break them if it is not caught. **LTS**
(Least Trimmed Squares) and **LMS** (Least Median of Squares) have a ~50% breakdown point and are
used as the **robust initial fit** that seeds the SHGM reweighting (the classical two-stage
high-breakdown SE pipeline: high-breakdown initialiser -> statistically-efficient GM cleanup).

### 5.1 Objectives

```
LMS:  minimise  median_i ( r_i^2 / sigma_i^2 )
LTS:  minimise  sum_{i=1..h} ( r^2 / sigma^2 )_(i)    (h smallest squared standardized residuals)
```

where `h = floor((m + s + 1) / 2)` for maximum breakdown (tunable via
`EstimationOptions.LtsCoverage`, the trimming fraction). Both are combinatorial; the practical
driver is resampling plus concentration:

### 5.2 The resampling / C-step driver

```
LtsDriver(model, mm, opt):
  best_obj = +inf; best_state = null
  for trial in 1..LtsSubsetCount:
      draw an OBSERVABLE elemental subset E of measurement rows (|E| = s), seeded RNG
      solve the exactly-determined subset via an IWlsStep over the sub-set  -> x_E
      (LTS only) run C concentration steps:
          repeat up to LtsCStepCap:
              rank all m standardized residuals at x_E; keep the h smallest -> subset E'
              re-solve WLS over E' via IWlsStep                              -> x_E'
              stop when the h-subset stops changing
      obj = LMS or LTS objective at the final subset state
      if obj < best_obj: best_obj = obj; best_state = x_E'
  scale_hat = 1.4826 * (1 + 5/(m - s)) * sqrt(best_obj)      (robust residual scale)
  return best_state, scale_hat
```

Key points:
- The subset solve **reuses the `IWlsStep` seam**: an elemental subset is an exactly-determined
  observable measurement set, so `DenseWlsStep`/`SparseWlsStep` solve it unchanged. The driver is a
  new sibling class (`HighBreakdownDriver`), not a new step backend - it *drives* the step, it does
  not replace it.
- **Observable elemental subsets only.** A random `s`-row draw is usually unobservable; the draw
  must be rejected and re-drawn (bounded retries) until `step.GateObservability` passes, or built
  greedily to cover all state directions. This reuses the existing observability gate.
- `LtsSubsetCount` defaults to a fixed count (e.g. 200) sized for gb-spine; determinism requires a
  **seeded RNG** and strictly model-order subset enumeration (sec. 10.4), mirroring
  `SyntheticMeasurements` (seed default `20260713`).

### 5.3 The two-stage pipeline (recommended default)

`--method lts` (or `lms`) runs the driver, then hands `best_state` as the **warm start** and
`scale_hat` as the **residual scale** to a `SchweppeGm` reweighting pass. The GM stage recovers
statistical efficiency (LTS/LMS alone are inefficient) while the LTS stage guarantees the GM starts
from an uncontaminated fit - so masking cannot corrupt the leverage weights. This is the highest-
assurance measurement-only configuration and the one to reach for on the weakest GB periods (PN-
fallback days, radial GSPs).

Scaling doctrine: the resampling driver is **dense-first**. For gb-full it is gated exactly like
`Constrained`/`Feasible` (fail loud above the dense threshold) until a sparse elemental-solve path
is proven; state that limit in the exception message, per house convention.

---

## 6. HTI: Hypothesis-Testing Identification for multiple interacting bad data

The sequential LNR loop in `WlsStateEstimator` removes one measurement per pass (the single largest
`r^N`), re-estimating between removals. It is correct for a single gross error and for
well-separated errors, but for **multiple interacting** (conforming) errors it can smear the
residuals so the largest `r^N` points at a *good* measurement - misidentification. **HTI** replaces
the single-shot ranking with a joint estimate of the errors over a **suspect set**, then a
per-suspect hypothesis test. It is offered as `EstimationOptions.BadDataId = Hti` and slots into the
post-convergence identification stage (replacing the LNR block at
`WlsStateEstimator.Estimate` ~lines 320-357).

### 6.1 Equations

At the converged estimate, using the linearised residual model `r = S e`, `S = I - K`,
`Omega = S W^-1 = W^-1 - H G^-1 H^T`:

1. **Suspect set.** `Sset = { i : r^N_i > tau }` (`tau = HtiSuspectThreshold`, default 3.0),
   optionally expanded to include any measurement strongly correlated with a suspect
   (`|Omega_ij| / sqrt(Omega_ii Omega_jj) > rho_min` for some suspect `j`) so interacting partners
   are not left out. Cap `|Sset| <= HtiMaxSuspects` (fail-soft: if exceeded, fall back to LNR and
   report it).
2. **Joint error estimate.** Assume only suspects carry error (`e_j = 0` for `j` not in `Sset`).
   Then `r_Sset = S_SS * e_Sset`, where `S_SS` is the principal `|Sset| x |Sset|` submatrix of `S`.
   Solve

   ```
   e^_Sset = (S_SS)^-1 * r_Sset
   Cov(e^_Sset) = (S_SS)^-1 * Omega_SS * (S_SS)^-T
   ```

   Both `S_SS` and `Omega_SS` are built from `|Sset|` columns of `H G^-1 H^T`, i.e. `|Sset|`
   back-substitutions against the gain factorisation **already formed** by
   `NormalizedResiduals` - cheap when `|Sset|` is small. (`S_SS = I - (H G^-1 H^T W)_SS`,
   `Omega_SS = (W^-1)_SS - (H G^-1 H^T)_SS`.)
3. **Per-suspect hypothesis test.** For each `i` in `Sset`:

   ```
   t_i = |e^_i| / sqrt( Cov(e^_Sset)_ii )
   ```

   `t_i > gamma` (`gamma = HtiTestThreshold`, default 3.0, a standard-normal significance level)
   declares measurement `i` **bad**. Measurements with `t_i <= gamma` are **exonerated** even
   though their raw `r^N` exceeded `tau` - this is precisely the interacting-error case LNR gets
   wrong.

### 6.2 Correction vs removal (low-redundancy advantage)

For each identified-bad suspect, HTI can either **remove** it (as LNR does) or **correct** it in
place: `z_i <- z_i - e^_i`, keeping the row. Correction preserves observability - decisive at low
redundancy, where removing a radial GSP's sole injection is exactly the runaway the prior-backfill
option (`BackfillRemovedFromPrior`) treats from the other side. Recommended policy:
`HtiAction = CorrectThenReverify` (default): correct all identified suspects, re-estimate, re-run
the chi-square test; if it now passes, done; if not, iterate (capped at `MaxBadDataRemovals`),
falling back to removal for any suspect whose correction fails to clear. `HtiAction = Remove`
reproduces removal semantics for callers that want excluded-not-corrected behaviour. Corrections
and removals are both recorded in `EstimationResult.Removed` / a new
`EstimationResult.Corrected` list (sec. 7) with the estimated error `e^_i` and its `t_i`.

### 6.3 Interaction with SHGM and constraints

- HTI runs **after** the (optionally SHGM-reweighted) fit converges, on the current weights.
- Critical measurements (`Omega_ii ~ 0`) cannot enter `Sset` (their `r^N` is NaN) - a network fact
  is never removable, same doctrine as today.
- `virtual-zero-injection` constraint rows and `removal-backfill-prior` pseudos are excluded from
  `Sset` (they are not removable telemetry), matching the existing LNR guards.

---

## 7. Code seams and data structures

All names below fit the existing style (PascalCase enum members and options, `<summary>` on every
public member, `internal` for new numeric machinery).

### 7.1 `EstimationMethod` (extend `Estimation/WlsStateEstimator.cs`)

Add, after `Feasible`:

- `SchweppeGm` - Schweppe-Huber Generalized-M: Huber IRLS on the **leverage-adjusted** standardized
  residual `r_Si = r_i / (sigma_i * a_i)`, rejecting high-leverage bad data the plain Huber/LNR
  loop masks. CLI: `--method schweppegm` (the estimator lowercases the enum name via
  `ToString().ToLowerInvariant()`, see the `--method` error path).
- `Lts` - Least Trimmed Squares initial fit (resampling + C-steps) feeding a `SchweppeGm` cleanup
  pass; ~50% breakdown. Dense-only, fail-loud above threshold.
- `Lms` - Least Median of Squares initial fit (resampling), same pipeline as `Lts` with the median
  objective.

Each gets a full `<summary>` in the enum, matching the existing Huber/LAV prose.

### 7.2 `EstimationOptions` (extend the same record)

New `init` properties, each documented:

```
LeverageSource Leverage        = LeverageSource.HatDiagonal   // sec. 3
bool   LeverageFallbackToHat   = false                        // sec. 3.2 (else fail loud)
double HuberK                  = 1.345                        // shared Huber/SHGM threshold
double SchweppeCutoff          = 2.5                          // 'd' scale for PS weights, sec. 3.2
BadDataIdentification BadDataId = BadDataIdentification.SequentialLnr   // vs Hti, sec. 6
double HtiSuspectThreshold     = 3.0                          // tau
double HtiTestThreshold        = 3.0                          // gamma
int    HtiMaxSuspects          = 30                           // cap; fall back to LNR beyond
HtiAction HtiAction            = HtiAction.CorrectThenReverify
int    LtsSubsetCount          = 200                          // resampling trials
double LtsCoverage             = 0.5                          // trimming fraction -> h
int    LtsCStepCap             = 20
int    LtsSeed                 = 20260713                     // determinism, matches SyntheticMeasurements
```

New supporting enums in `Estimation/` (their own small file or beside `EstimationMethod`):
`LeverageSource { None, HatDiagonal, ProjectionStatistic }`,
`BadDataIdentification { SequentialLnr, Hti }`,
`HtiAction { Remove, CorrectThenReverify }`.

### 7.3 `IWlsStep` extension (`Estimation/WlsStep.cs`)

Add one method to the seam so leverage is a first-class product of the representation that owns the
gain:

```
double[] LeverageDiagonal(double[] vm, double[] va);   // returns K_ii, i = 0..m-1, in [0,1]
```

- `DenseWlsStep` / `ConstrainedDenseWlsStep` / `FeasibleWlsStep`: `K_ii = 1 - Omega_ii/sigma_i^2`,
  reusing the exact `BadDataAnalysis` back-substitution path (form the gain, factor once, one solve
  per row) - it is the `NormalizedResiduals` computation minus the residual division, so factor the
  shared code rather than duplicating it.
- `SparseWlsStep`: same, reusing the single Gilbert-Peierls factorisation it already builds for
  `NormalizedResiduals` (`FactorPrepared` + per-row `Solve`). No new O(s^3) cost.

Because `HatDiagonal` leverage is *exactly* `1 - Omega_ii/sigma_i^2`, an implementation may instead
derive `a_i` straight from the existing `NormalizedResiduals` return (the loop already calls it),
and `LeverageDiagonal` becomes an optional convenience. Choose one and keep the dense/sparse math on
a single code path (the codebase's standing rule that "dense and sparse math cannot drift apart").

### 7.4 New files (all `internal`, namespace `GridSim.Core.Estimation`)

- `Estimation/RobustGm.cs` - the Schweppe-Huber weight function `q(r_Si)` and the leverage-weight
  assembly (`HatDiagonal` from `Omega`, `ProjectionStatistic` from `ProjectionStatistics`). Pure
  functions; the IRLS loop calls in.
- `Estimation/ProjectionStatistics.cs` - `PS_i` computation (sec. 3.2), dense-gated, ChiSquare
  cutoff. Cross-checked against `se_residual_check.py` leverage on small cases.
- `Estimation/HighBreakdownDriver.cs` - the LTS/LMS resampling + C-step driver (sec. 5), seeded RNG,
  observable-subset draw via `step.GateObservability`, returns `(state, robustScale)`.
- `Estimation/HypothesisTestingIdentification.cs` - HTI suspect set, joint error estimate, per-
  suspect test, correct/remove policy (sec. 6). Consumes `S_SS`/`Omega_SS` built from the gain
  factorisation the step exposes.

### 7.5 `EstimationResult` additions (`Estimation/EstimationResult.cs`)

- `MeasurementResidual`: add `double Leverage = double.NaN` (the `K_ii`), a new trailing optional
  record parameter (back-compatible), populated when any leverage-aware method ran. Surfaces in
  `gridsim-estimate-export/1` alongside the residual for the validator to check.
- `EstimationResult`: add `IReadOnlyList<BadDataCorrection> Corrected` (init, default empty) for HTI
  in-place corrections, where `BadDataCorrection(Measurement, double EstimatedError, double TestStat,
  int Pass)` mirrors `BadDataRemoval`. `Removed` keeps its meaning (excluded rows), so both the LNR
  and HTI paths report through typed lists.

### 7.6 Export / crossval surface

Extend `gridsim-estimate-export/1` (additive, versioned) so each residual carries `leverage`
(`K_ii`) and the document carries the identification method and any HTI corrections. This lets
`tools/crossval/se_residual_check.py --leverage` diff the estimator's leverage against its own
independent recomputation - the arbiter already exists (sec. 10).

---

## 8. Namespace / file layout summary

```
src/GridSim.Core/Estimation/
  WlsStateEstimator.cs      (extend: enum members, options fields, IRLS reweight arm, HTI hookup)
  WlsStep.cs                (extend IWlsStep + 4 backends: LeverageDiagonal)
  BadDataAnalysis.cs        (factor out the K_ii / Omega core shared with LeverageDiagonal)
  EstimationResult.cs       (add MeasurementResidual.Leverage, BadDataCorrection, Corrected)
  RobustGm.cs               (new: Schweppe-Huber q(), leverage-weight assembly)
  ProjectionStatistics.cs   (new: PS_i, dense-gated)
  HighBreakdownDriver.cs    (new: LTS/LMS resampling + C-steps)
  HypothesisTestingIdentification.cs  (new: HTI)
docs/technical/
  06-state-estimation.md    (extend: new methods, leverage, HTI - the reference doc)
tools/crossval/
  se_residual_check.py      (already computes leverage K and omega; extend to diff exported K and
                             to validate an HTI-corrected export)
tests/GridSim.Tests/
  RobustGmLeverageTests.cs, HighBreakdownTests.cs, HtiTests.cs, RestrictionInvariantTests.cs (add)
```

No public API beyond the `Estimation` namespace changes; the CLI `estimate --method ...` surface
extends by three names and the new options, all defaulted so existing runs are byte-for-byte
unchanged.

---

## 9. Restriction-invariant compliance

Every method here is a **static, measurement-only filter**. Per the review gate (`RestrictionReview`):
"Static, measurement-only estimators (WLS, Huber, LAV, constrained, feasible, QR, linear-PMU,
branch-current, EWLAV) are filters, not predictors - they need no entry and are unconditionally
permitted." `SchweppeGm`, `Lts`, `Lms`, and HTI join that list. Concretely, each is compliant
because:

1. **No `Dynamics/` process model.** None integrates a state-transition model; there is no predict
   step. `RestrictionReview.AssertPermitted(method, usesDynamicsModel: false)` returns immediately.
   These methods therefore get **no** `RestrictionReviewEntry` and **no** row in
   `RestrictionReview.Reviewed` - adding one would be wrong (that register is only for
   `Dynamics/`->`Estimation/` fusion, invariant #5).
2. **Filter, not predictor (no forward component).** Leverage, PS, LTS resampling, and HTI all
   operate on a **single measurement snapshot** already resolved at the estimate. They read no
   clock and emit nothing stamped later than the newest input. The estimate is the filtered
   present/past, exactly as WLS/Huber today.
3. **Guard-limited inputs unchanged.** Inputs still arrive through `MeasurementSet.Load`, which
   applies `ForwardInferenceGuard` (>= 169 h old or replay). No new ingestion seam is introduced;
   the LTS driver's RNG draws from the *existing* measurement rows, not from any external or live
   feed.
4. **No dynamics fusion smuggled in.** The LTS warm start is a *measurement subset fit*, not a
   process-model prediction; the SHGM cleanup consumes it as an initial `x`, identical in kind to
   today's `WarmStart` seeding. Nothing crosses the `Dynamics/`->`Estimation/` seam.

**Invariants the suite must assert** (add to `RestrictionInvariantTests`):

- I1. `RestrictionReview.AssertPermitted("SchweppeGm"/"Lts"/"Lms", usesDynamicsModel: false)` does
  not throw, and none of these names appears in `RestrictionReview.Reviewed` (they are not
  dynamics-fused, so they must **not** be registered).
- I2. Running any new method on a set whose `TakenUtc` is newer than `UtcNow - 169h` is rejected by
  the guard before estimation (reuse the existing `ForwardInferenceGuard` seam test).
- I3. No new-method output carries a timestamp later than its newest input (the general
  no-forward-inference output invariant).

---

## 10. Validation and test plan

The estimator's validation doctrine (see `docs/technical/11-validation.md`): machine-epsilon
cross-checks against an **independent reference**, and bit-for-bit determinism. Every item below
follows it.

### 10.1 The headline fixture: reject a high-leverage outlier LNR/Huber miss

Construct a small network (e.g. a 5-bus radial spur off a meshed core) with a **low-redundancy**
bus whose single injection meter has leverage `K_ii` near 1 (verify via `se_residual_check.py
--leverage`). Inject a gross error into that meter, sized so its **raw** residual stays below the
`3-sigma` LNR/Huber trigger because the fit follows it (masking). Assert:

- Plain `--method wls` + LNR: the bad meter is **not** removed (or the wrong meter is), chi-square
  passes or a good measurement is flagged - the documented failure.
- Plain `--method huber`: `factor` for the bad meter stays ~1 (not down-weighted).
- `--method schweppegm`: the leverage-adjusted `r_Si = r^N_i` exceeds `k`, `factor = k/r_Si << 1`,
  the bad meter is down-weighted, and the recovered state matches the clean-data WLS state within a
  set tolerance. This is the **must-pass** demonstration of the whole design.
- With `Leverage = ProjectionStatistic`: same rejection, and additionally survives a *second*
  interacting error where `HatDiagonal` leverage is itself masked (the masking-immunity claim).

### 10.2 HTI vs sequential LNR on interacting bad data

Two conforming errors on adjacent/parallel meters constructed so their residuals smear and the
largest `r^N` points at a **good** measurement (the classic LNR misidentification). Assert:

- `BadDataId = SequentialLnr`: removes the good measurement first (misidentification), needing
  extra passes or leaving chi-square failing.
- `BadDataId = Hti`: both true errors land in `Sset`, `t_i > gamma` for exactly the two bad meters
  and `t_i <= gamma` for the exonerated good one; `CorrectThenReverify` corrects both and
  chi-square then passes. Report `EstimatedError` vs the injected error within tolerance.

### 10.3 Independent-reference cross-checks (machine epsilon)

- **Leverage honesty.** `se_residual_check.py --leverage` recomputes `K_ii` from an independently
  built Ybus / pi-model (no GridSim math) and diffs it against the exported `leverage`; require
  `max |K_indep - K_exported|` below the validator tolerance. The script's criticality arbitration
  (`K > 1 - 1e-6`) already exists; extend it to diff the leverage field per residual.
- **SHGM reduces to WLS.** On a clean synthetic set (`--synth`, `NoiseScale = 0`), `--method
  schweppegm` must produce a state **bit-for-bit** identical to `--method wls` (all factors 1), and
  the independent Gauss-Newton optimality step from `se_residual_check.py` must be negligible.
- **PS self-check.** On small cases, cross-check `PS_i` and the projection directions against a
  reference NumPy recomputation (an added `--projstat` mode in the crossval script, mirroring the
  existing `--fd` analytic-vs-finite-difference self-check).

### 10.4 Determinism and structural invariants

- **Trace identity.** `sum_i K_ii == StateSize` to machine tolerance on every fixture (exact hat-
  trace property) - a cheap always-on assertion.
- **LTS/LMS determinism.** Two runs with the same `LtsSeed` produce bit-identical subsets,
  objective, and state; subset enumeration walks measurements in strict model order; tie-breaks are
  deterministic (lowest index wins). Mirrors the `SyntheticMeasurements` determinism doctrine.
- **HTI determinism.** Suspect-set construction and the joint solve are deterministic (fixed
  ordering, deterministic factorisation); repeated runs are identical.
- **Huber equivalence.** `SchweppeGm` with `Leverage = None` (all `a_i = 1`) equals `--method
  huber` bit-for-bit - guards against accidental behaviour change to the existing method.

### 10.5 Cross-validation vs an external estimator (optional, where feasible)

Where `pandapower.estimation` (the existing `crossval_estimation.py` reference) offers a comparable
robust/GM path, cross-check the down-weighted final state on a shared case. Absent a matching GM
implementation, the independent optimality + leverage checks in 10.3 are the primary gate (they
need no second estimator, only an independent `h`/`K` recomputation).

---

## 11. Zero-warning build gate and implementation checklist

The build ships at 0 warnings; keep it there:

- Every new public member (`EstimationMethod` values, `EstimationOptions` properties, enums,
  `BadDataCorrection`, `MeasurementResidual.Leverage`) carries an XML `<summary>` in the
  established prose style.
- New numeric types are `internal sealed`; nullable-reference discipline uses the `= null!`
  Begin-then-bind pattern already in the step backends.
- No unused fields, no unreachable arms; the `switch` over `EstimationMethod` in the reweight and in
  the method-selection block stays exhaustive (the new members are handled or explicitly fall
  through to WLS behaviour).
- Fail-loud, never silent-degrade: `ProjectionStatistic` and `Lts`/`Lms` above the dense threshold
  throw `NotSupportedException` with a message naming the limit (the `--method constrained` pattern),
  unless the documented fallback flag is set.
- Determinism: seeded RNG only, no `DateTime.Now`, no unordered-collection iteration in any path
  that affects the result.

Implementation order (each step independently shippable and testable):

1. `IWlsStep.LeverageDiagonal` + `MeasurementResidual.Leverage` + export/crossval diff (no method
   behaviour change yet; pure diagnostics, validates the leverage plumbing against
   `se_residual_check.py`).
2. `SchweppeGm` with `HatDiagonal` leverage (the one-line IRLS substitution + reduces-to tests) -
   delivers the headline fixture (sec. 10.1).
3. `ProjectionStatistics` + `LeverageSource.ProjectionStatistic` (masking-immunity).
4. `HypothesisTestingIdentification` + `BadDataId = Hti` (sec. 10.2).
5. `HighBreakdownDriver` + `Lts`/`Lms` two-stage pipeline (sec. 5).

---

## 12. Open questions / follow-ups

- **Sparse projection statistics and sparse LTS.** Both are dense-gated here. A sparse PS
  approximation (subsampled directions) and a sparse elemental solve would lift the gb-full limit;
  out of scope for the first landing, called out in the fail-loud messages.
- **HTI suspect-set expansion policy.** The correlation-based expansion (`rho_min`) is a tunable;
  the initial default should be validated against a battery of multiple-interacting-error cases
  before it is trusted on GB history.
- **GM efficiency tuning.** `HuberK = 1.345` (95% Gaussian efficiency) and `SchweppeCutoff` are the
  standard starting points; a GB-specific tuning study (against the sigma model in
  `docs/technical/06-state-estimation.md` sec. "GDA-side sigma model") is a later calibration task.
- **Interaction with the feasibility gate.** SHGM/LTS states should also pass the Ring C physics
  acceptance gate when combined with `Feasible`; the composition of robust weighting with the KKT
  constraint backend is a follow-up (both currently sit behind `IWlsStep`, so it is a matter of
  wiring, not new math).

---

## 13. See also

- `UnderstandingRestrictions.md` - the no-forward-inference rule (sec. 3: filter, not predictor).
- `RestrictionReview.Reviewed` (`src/GridSim.Core/Diagnostics/RestrictionReview.cs`) - the review register; measurement-only methods need no entry.
- `docs/technical/06-state-estimation.md` - the WLS estimator, IRLS Huber/LAV, LNR bad-data loop,
  observability, the GDA sigma model (the reference this design extends).
- `src/GridSim.Core/Estimation/WlsStateEstimator.cs` - the IRLS block (Huber/LAV) and the LNR
  removal loop this design modifies.
- `src/GridSim.Core/Estimation/BadDataAnalysis.cs` - the `Omega_ii` / normalised-residual /
  criticality core that the leverage `K_ii` and HTI submatrices reuse.
- `src/GridSim.Core/Estimation/WlsStep.cs` - the `IWlsStep` seam the new methods extend.
- `tools/crossval/se_residual_check.py` - the independent leverage/criticality arbiter (`--leverage`).
