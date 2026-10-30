# 18. Real-time, distribution and dynamic estimation

This chapter specifies the estimation code that sits beside the static transmission WLS estimator of
[ch. 6, State estimation](<0006 State estimation.md>): the real-time estimator behind `gridsim serve`, the
recursive (Kalman-family) network-state filters it can run, the three-phase unbalanced distribution stack behind
`gridsim dist3ph`, and the pointers to the code that other chapters own (robust estimation, synchrophasors, CIM,
telemetry codecs, the false-data screen). The real-time and dynamic code lives in `src/GridSim.Core/Simulation/` and
`src/GridSim.Core/Estimation/Dse/`; the distribution code lives in `src/GridSim.Core/Distribution/`.

Everything here obeys [`UnderstandingRestrictions.md`](../../UnderstandingRestrictions.md). State estimation is a
filter: it resolves the present or the past from observations and never infers forward. Three mechanisms enforce it
in code, and every section below depends on them: the 169 hour horizon guard on every dated input, the review gate
that a recursive filter must pass before it can run, and the output guard that refuses to emit a state stamped newer
than the horizon.

The user chapters are manual ch. 21, [State estimation](<../manual/0021 State estimation.md>), for `serve`, and
manual ch. 7, [Three-phase distribution feeders](<../manual/0007 Three-phase distribution feeders.md>), for
`dist3ph`. Flags are
in the [`serve`](<../manual/0019 CLI reference.md#serve>) and [`dist3ph`](<../manual/0019 CLI reference.md#dist3ph>) sections of
the CLI reference. The three-phase design record is [design/three-phase-dsse.md](design/three-phase-dsse.md).

## Model

### The restriction frame

`ForwardInferenceGuard` (`src/GridSim.Core/Diagnostics/ForwardInferenceGuard.cs`) holds one constant,
`HorizonHours = 169.0` (seven days plus one hour). `Horizon(now) = now - 169 h`. A stamp is accepted when
`stamp <= Horizon(now)` and rejected, with `ForwardInferenceException`, when `stamp > Horizon(now)`: a stamp exactly on
the horizon passes. Recent and future stamps are refused alike, so the engine can only lag real time.

`RestrictionReview` (`src/GridSim.Core/Diagnostics/RestrictionReview.cs`) is the review gate. Its `Reviewed` table is
the canonical log of the estimation methods that fuse a process model (a predict step) into estimation. It holds four
entries: `Ekf`, `Ukf`, `EnKf` and `FrequencyRocofKf`. Each filter constructor calls
`RestrictionReview.AssertPermitted(MethodName, usesDynamicsModel: true)`; a method that is not in the table throws
`RestrictionViolationException` before it runs. A static, measurement-only estimator (WLS, the three-phase WLS, the
branch-current estimator) needs no entry.

`RestrictionReview.AssertOutputHistorical(stamp, now, what)` is the output guard. Both streaming engines call it on
every frame before it is emitted; a frame whose state time is newer than `Horizon(now)` throws
`RestrictionViolationException`. A filter integrates its process model one step ahead internally, but the state it
emits is the filtered present, stamped at the window time.

### The streaming contract

Both engines take a stream of `MeasurementSet` windows (`gridsim-measurements/1`, see
[ch. 10](<0010 File and wire formats.md>)) and a `RealtimeEstimatorOptions`
(`src/GridSim.Core/Simulation/RealtimeEstimatorEngine.cs`). For each window, in stream order:

```
1. if window.TakenUtc is null            -> throw ForwardInferenceException (an undated window is refused)
2. if TakenUtc > Horizon(options.UtcNow) -> rejectedFresh += 1; skip the window (never estimated)
3. estimate the window                   (static WLS, or one recursive filter step)
4. AssertOutputHistorical(TakenUtc)      then emit the frame, Sequence = 0, 1, 2, ... over emitted frames
```

`options.UtcNow` null means the wall clock. The engine returns a `RealtimeRunSummary`: `WindowsProcessed`,
`WindowsConverged`, `WindowsRejectedFresh`, `WindowsFailed`, and the first and last state time. A fresh window is
counted and skipped, never fatal, so a resident process survives a briefly-fresh window and still never estimates one.

An undated window is the one protocol violation that stops the run: with no stamp it could pass the horizon test as
`DateTime.MinValue`.

### The measurement model the filters share

All four estimators of `Estimation/` (WLS, EKF, UKF, EnKF) build the same `MeasurementModel`
(`src/GridSim.Core/Estimation/MeasurementModel.cs`), so a filter estimate is directly comparable with a WLS estimate.
The state is

```
x = [ Va at every non-slack bus (radians) ; Vm at every bus (pu) ]        s = (n - slackCount) + n
```

in model bus order; for case9 (`n = 9`, one slack) `s = 8 + 9 = 17`. The measurement vector is `z`, converted to
per-unit once (powers divided by `BaseMva`, angles to radians), with per-unit standard deviations `sigma_r`. The
measurement function `h(x)` and its Jacobian `H = dh/dx` for the eight measurement kinds (`VmPu`, `VaDeg`, `PInjMw`,
`QInjMvar`, `PFlowMw`, `QFlowMvar`, `IMagPu`, `IAngDeg`) are specified in ch. 6 and are not repeated. The injection
equations that matter most are

```
th_ik = Va_i - Va_k
P_i = Vm_i * sum_k Vm_k * (G_ik * cos(th_ik) + B_ik * sin(th_ik))
Q_i = Vm_i * sum_k Vm_k * (G_ik * sin(th_ik) - B_ik * cos(th_ik))
```

For the two angle kinds `EvaluateH` folds `h` toward `z` (`h = z - IEEERemainder(z - h, 2 pi)`), so every residual
`z - h` lies in `[-pi, pi]`. All three filters evaluate `h` through `EvaluateH`, so they inherit the fold.

### Process model

A recursive filter predicts the state across the gap between two windows through an `IProcessModel`
(`src/GridSim.Core/Estimation/Dse/IProcessModel.cs`):

```
Predict(x, dt, F, Q):   x <- f(x)      (in place)      F = df/dx (s x s)      Q = process noise covariance (s x s)
```

The default is `RandomWalkProcessModel` (persistence, the forecasting-aided model of Debs and Larson):

```
x_k = x_(k-1) + w,   w ~ N(0, Q)
F = I
Q = diag( qa * dt  for each angle state ,  qv * dt  for each Vm state )
qa = angleProcessStd^2 = 0.05^2 = 0.0025 rad^2 per second
qv = vmProcessStd^2    = 0.01^2 = 0.0001 pu^2 per second
```

The model makes no claim about the real grid; it only sets how far the filter trusts the previous estimate as a prior
for the present one. `dt` is the elapsed time in seconds. `dt = 0` (two windows at one instant) is a pure update with
`Q = 0`. A negative or non-finite `dt` throws `ArgumentOutOfRangeException`. The first window has no predecessor and
uses `dt = 1 s`. The seam is where a `Dynamics/` model could be fused into estimation; no such model is registered in
the review gate, so the only process model that runs is the random walk.

### Extended Kalman filter

`KalmanStateEstimator` (`src/GridSim.Core/Estimation/Dse/KalmanStateEstimator.cs`, method name `Ekf`) carries the state
`x` and covariance `P` across windows and corrects in information form, so it reuses the WLS Jacobian and forms no
`m x m` inverse:

```
initialise (first window):   x0 = [ slack reference angle for each angle state ; 1.0 for each Vm state ],
                             P0 = initialStateVariance * I,     initialStateVariance = 1.0
predict:                     F, Q from the process model;   x- = f(x);   P- = F P F^T + Q
linearise at x-:             h = h(x-);   H = dh/dx at x-;   W = diag(1 / sigma_r^2)
information gain:            G   = H^T W H           (accumulated row by row over the non-zero columns of H)
                             rhs = H^T W (z - h)
update:                      Y = inv(P-) + G;    P = inv(Y);    x = x- + P * rhs
emit:                        Vm, Va (degrees); per-bus sigma = sqrt(P_cc); angle sigma converted to degrees
```

Two `s x s` inverses (`inv(P-)` and `inv(Y)`) are formed per window by Gauss-Jordan elimination with partial pivoting
(`DseLinAlg.Invert`). A pivot at or below `NumericTolerances.PivotFloor(max|A|)` throws `InvalidOperationException`
("singular matrix in DSE covariance inverse"). The step is transactional: the prior is read from the committed state
(or a fresh initial state on the first window), `Predict` runs on a copy, and the state, the covariance, the state size,
the default process model and the initialised flag are committed together only after the last operation that can
throw. A step that throws, with any `IProcessModel`, leaves the filter exactly as it was, and a throw on the first
window leaves it uninitialised.

### Unscented Kalman filter

`UnscentedKalmanStateEstimator` (`UnscentedKalmanStateEstimator.cs`, method name `Ukf`) propagates `2s + 1` sigma points
through the exact `h` and needs no Jacobian:

```
predict:   P- = F P F^T + Q                      (x- = f(x), persistence leaves x unchanged)
lambda = alpha^2 (s + kappa) - s                 c = s + lambda
wm[0] = lambda / c                               wc[0] = lambda / c + (1 - alpha^2 + beta)
wm[i] = wc[i] = 1 / (2 c)                        for i = 1 .. 2s
L = chol(P-)                                     (Cholesky; throws if a pivot is not strictly positive)
sigma[0] = x-;   sigma[1+i] = x- + sqrt(c) L[:,i];   sigma[1+s+i] = x- - sqrt(c) L[:,i]
z_p = h(sigma[p]);          zbar = sum_p wm[p] z_p
S   = sum_p wc[p] (z_p - zbar)(z_p - zbar)^T + R          R = diag(sigma_r^2)
Pxz = sum_p wc[p] (sigma[p] - x-)(z_p - zbar)^T
K   = Pxz inv(S);    x = x- + K (z - zbar);    P = P- - K S K^T
```

With the defaults `alpha = 1e-3`, `beta = 2`, `kappa = 0` and case9's `s = 17`: `lambda = -16.999983`,
`c = 1.7e-5`, there are 35 sigma points, `wm[0] = -999,999.0`, `wc[0] = -999,996.0` and every other weight is
`29,411.76`. The first sigma-point spread is `sqrt(c) = 0.0041231` times the Cholesky factor of `P-`.

### Ensemble Kalman filter

`EnsembleKalmanStateEstimator` (`EnsembleKalmanStateEstimator.cs`, method name `EnKf`) carries `N` members and
estimates `P` from their spread:

```
initialise:   member e = baseState + initialStateStd * g,   g ~ N(0,1) per state      baseState as for the EKF
predict:      for each member: Predict(member, dt, F, Q);  member += L g,   L L^T = Q
              (diagonal Q: L = diag(sqrt(Q_ii)), the random-walk case; correlated Q: L from
               DseLinAlg.CholeskySemiDefinite, one factorisation per distinct Q)
hbar = mean_e h(member_e);   xbar = mean_e member_e
Pyy = sum_e (h_e - hbar)(h_e - hbar)^T / (N - 1) + R          Pxy = sum_e (member_e - xbar)(h_e - hbar)^T / (N - 1)
K = Pxy inv(Pyy)
update:       member_e += K (z + sigma_r * g - h_e)           (perturbed observations, fresh g per entry)
emit:         the ensemble mean; per-state sigma = sqrt( sum_e (member_e - mean)^2 / (N - 1) )
```

The Gaussian draws are Box-Muller on a `System.Random`, in a fixed order, so a run repeats for a fixed seed. Each window
has its own stream (N264, October 2026): window `k`, counting committed windows from 0, is seeded with
`WindowSeed(seed, k)`, the seed itself for `k = 0` and the top 31 bits of the `k`-th SplitMix64 output of the seed
otherwise. A window that fails commits nothing, so the next window draws exactly what it would have drawn had the failed
window never arrived. A diagonal `Q_ii` that is negative, NaN or infinite is refused, naming the process model. A
correlated `Q` must be exactly symmetric and finite; its factor is computed column by column with a zero column where a
pivot is within `t = 1e-12 x max|Q_ij|` (everything below must be within `t` too) and a pivot below `-t` refused
(`InvalidOperationException` naming the process model; the engine counts the window failed). The draws are `s` Gaussians
in state order, the same count and order as the diagonal path, so a diagonal `Q` gives the diagonal path's bits.

**Transactional steps.** Every filter (EKF, UKF, EnKF) reads its prior from the committed state, runs `Predict` on a
copy, and commits state, covariance, state size, default process model and the initialised flag together after the last
operation that can throw. A step that fails leaves the filter exactly as it was. The EnKF has nothing random to roll
back: its stream for a window is derived from the seed and the committed window count, so after a failed window the
next window draws exactly the noise it would have drawn had the failed one never arrived. `serve --method enkf` runs the defaults: `N = 80`, `initialStateStd = 0.5`, seed `20260713`.

### Frequency and RoCoF filter

`FrequencyRocofFilter` (`FrequencyRocofFilter.cs`, method name `FrequencyRocofKf`) tracks system frequency and its rate
of change as two filter states from noisy frequency samples. Estimating RoCoF as a state is more reliable than
differentiating a PLL frequency; an erroneous PLL RoCoF mis-tripped 6,200 MW of solar in the 2016 Blue Cut event, the
figure the filter's source records.

```
state  [f, r]  (Hz, Hz/s)        F = [[1, dt],[0, 1]]        H = [1, 0]
predict:  f- = f + r dt;   r- = r
          P00 = P00 + dt (P10 + P01) + dt^2 P11 + qf dt        P01 = P01 + dt P11
          P10 = P10 + dt P11                                   P11 = P11 + qr dt
update:   S = P00 + sigmaHz^2;   k0 = P00 / S;   k1 = P10 / S;   innov = fMeasured - f-
          f = f- + k0 innov;   r = r- + k1 innov
          P00 = (1 - k0) P00;  P01 = (1 - k0) P01;  P10 = P10 - k1 P00;  P11 = P11 - k1 P01
```

The four covariance assignments are simultaneous: every right-hand side uses the values before the update.

It has no feed and no verb. It is Core API, marked experimental in its XML documentation.

### What `Converged` means for a filter

A Kalman update is one step, not an iteration, so `DseEstimate.Converged` carries no iteration verdict. For the EKF and
the UKF (and, with the same finiteness test, the EnKF) it is true when every state and every variance is finite.
`DseEstimate.Finite` is the same value under its accurate name. It does not certify the estimate against the
measurements. The `serve` line for a filter window reads `OK` or `non-finite`.

### Robust estimation and bad data

These are specified in ch. 6 and are listed here only as the map from this chapter's real-time estimator:

- `EstimationMethod.SchweppeGm` (Huber IRLS on the leverage-adjusted, hat-matrix normalised residual) takes its
  leverage from `IWlsStep.NormalizedResiduals`. `ProjectionStatistics` (masking-immune leverage) is not wired into it.
  The design record is [design/robust-gm-leverage.md](design/robust-gm-leverage.md).
- `WlsStateEstimator` removes bad data by sequential largest normalised residual. `HypothesisTestingBadData` is not
  called by it. `HtiBadDataEstimation` is the alternative loop (`estimate --hti`): it runs the estimator with removal
  off, calls `HypothesisTestingBadData.Identify` while chi-square fails, removes every identified row (largest test
  statistic first, inside the removal cap) and re-estimates. It is dense and refused above
  `DenseEstimationLimits.StateCovarianceMaxState` (2,000 states).
- `estimate --method` accepts `wls`, `huber`, `lav`, `lav-lp`, `lav-irls`, `schweppe-gm` (`gm`), `qr`, `constrained` and
  `feasible` (`constrained-physics`).
- `NoiseFloorAnalysis` calibrates declared sigmas per source tag and kind from pooled normalised residuals; the verb
  is `estimate-noise-floor`.
- `QrWlsStep` and `Numerics/HouseholderQr` solve without forming the normal equations. `IMagPu` and `IAngDeg` are the
  current-magnitude and current-angle kinds; the linear PMU estimator takes correlated 2 x 2 block covariances.
- `serve` sets `ComputeStateCovariance = true` on its WLS engine, so each emitted `EstimationResult` carries the
  per-bus 1-sigma bounds `diag(G^-1)` when the state size is at or below 2,000.

### The three-phase model

`MultiPhaseModel` (`src/GridSim.Core/Distribution/MultiPhaseModel.cs`) is a separate, radial, per-phase model. It
leaves the positive-sequence engine untouched. It holds:

| Part | Type | Content |
|---|---|---|
| Bus | `PhaseBus(Id, IsSource, SourceV)` | Exactly one source bus; `SourceV` is three complex per-unit voltages, default `BalancedNominal()` = `1 at 0, -120, +120 degrees`. |
| Branch | `PhaseBranch(From, To, Z, Phases, ShuntY, RegulatorRatio)` | A 3 x 3 series `PhaseImpedance` `Z` (per-unit); the phases it carries; an optional 3 x 3 shunt `ShuntY` (half at each end, the pi section); an optional per-phase ideal regulator ratio at the From end. |
| Load | `PhaseLoad(Bus, SPerPhase, Model, Connection)` | Complex power per phase at nominal voltage, per-unit of `BaseMva / 3`; model `ConstantPower`, `ConstantCurrent` or `ConstantImpedance`; connection `Wye` or `Delta`. |
| Capacitor | `PhaseCapacitor(Bus, QPerPhase)` | Wye bank; `QPerPhase[p]` per-unit at 1 pu, so susceptance `B = QPerPhase[p]`. |
| Base | `BaseMva` | Three-phase MVA base. |

Voltages are per-unit of each bus's line-to-neutral base `kV_LL / sqrt(3)`; per-phase power is per-unit of
`BaseMva / 3`. The constructor refuses a model with no or several source buses, a load or capacitor on an unknown
bus, a branch with no phase, a non-zero impedance or shunt entry on a phase the branch does not carry, and a
regulator ratio that is not a finite number above 0.

Element definitions as the sweep solves them (`From` is the end nearer the source):

```
regulator ratio             a_p = 1 + stepPu * tap_p                      (PhaseBranch.StepRegulatorRatio)
branch, From to To          ideal regulator (V_out = a .* V_from, I_from = a .* I_out), then pi section:
                            half of ShuntY at the regulator output, series Z, half of ShuntY at the To end
closed switch               a branch with Z = 0
wye constant power          I = conj(S / V)
wye constant current        I = (|S| / 1) at angle (angle(V) - angle(S))
wye constant impedance      I = V conj(S) / 1^2
delta element (a-b,b-c,c-a) same three laws on the line-to-line voltage with nominal magnitude sqrt(3);
                            line currents  Ia = Iab - Ica,  Ib = Ibc - Iab,  Ic = Ica - Ibc
capacitor                   I = j B V
```

A bus has the phases of the branch that feeds it; a phase it does not have is exactly zero.

`SymmetricalComponents` (`SymmetricalComponents.cs`) is the Fortescue transform with `a = 1 at 120 degrees`:

```
V0 = (Va + Vb + Vc) / 3        V1 = (Va + a Vb + a^2 Vc) / 3        V2 = (Va + a^2 Vb + a Vc) / 3
Va = V0 + V1 + V2              Vb = V0 + a^2 V1 + a V2              Vc = V0 + a V1 + a^2 V2
voltage unbalance factor  VUF % = 100 * |V2| / |V1|     (0 when |V1| = 0)
```

`TransformerConnectionModel` (`TransformerConnectionModel.cs`) transforms an HV phase triple to the LV side in the
sequence domain. Grounded-wye/grounded-wye divides every sequence by the turns ratio. Delta/grounded-wye (ANSI Dyn1
step-down) sets `V0 = 0`, rotates `V1` by -30 degrees and `V2` by +30 degrees, then divides by the ratio. A turns
ratio at or below 0 means 1. The loader and the sweep do not call this class; it is Core API with tests.

### The `gridsim-dist3ph/1` feeder file

`MultiPhaseJsonLoader` (`MultiPhaseJsonLoader.cs`) is a strict `System.Text.Json` reader in the style of
`JsonGridLoader`. The v1 file is the authoring format in physical units; its v2 form is below. The `"schema"`
member must be exactly `"gridsim-dist3ph/1"`; member names are case-sensitive. Refused, naming the file and the JSON
path: an unknown, duplicate or missing member, a wrong JSON type, a non-finite number, a matrix that is not 3 x 3, a
duplicate id, an unknown bus or line-code reference, a self-loop, a line, switch or regulator joining two kV levels,
a negative self resistance, a self susceptance below 0, an asymmetric susceptance matrix, and a network that is not
one radial tree from the single source. Messages read like
`feeder.json: $.lines[0].to: unknown bus 9 (not in $.buses).` JSON comments are allowed; trailing commas are not.

```json
{
  "schema": "gridsim-dist3ph/1",
  "name": "three-bus example",
  "description": "where the data came from (cite it here)",
  "baseMva": 6.0,
  "buses": [
    { "id": 1, "baseKvLL": 12.47, "source": { "magnitudePu": [1.0, 1.0, 1.0], "angleDeg": [0, -120, 120] } },
    { "id": 2, "baseKvLL": 12.47 },
    { "id": 3, "baseKvLL": 4.16 }
  ],
  "lineCodes": [
    { "id": "oh", "unit": "ohm/mile",
      "r": [[0.4576, 0.1560, 0.1535], [0.1560, 0.4666, 0.1580], [0.1535, 0.1580, 0.4615]],
      "x": [[1.0780, 0.5017, 0.3849], [0.5017, 1.0482, 0.4236], [0.3849, 0.4236, 1.0651]] }
  ],
  "lines": [ { "id": "l12", "from": 1, "to": 2, "code": "oh", "length": 2000, "lengthUnit": "ft" } ],
  "transformers": [
    { "id": "t23", "from": 2, "to": 3, "connection": "gy-gy", "kva": 6000,
      "kvLLFrom": 12.47, "kvLLTo": 4.16, "rPercent": 1.0, "xPercent": 6.0 }
  ],
  "loads": [
    { "id": "ld3", "bus": 3, "connection": "wye", "model": "constant-power",
      "kw": [1275, 1800, 2375], "kvar": [790.17, 871.78, 780.62] }
  ]
}
```

Members and their conversions:

- `source` (on exactly one bus): optional `magnitudePu` and `angleDeg`, three values each; the default is the balanced
  1.0 pu set built exactly as `MultiPhaseModel.BalancedNominal()`.
- Units: line codes in `ohm/mile` or `ohm/km`; lengths in `ft`, `mile`, `m` or `km`; loads and capacitors in kW and
  kvar per phase at nominal voltage.
- Per-unit bases: impedance base `kV_LL^2 / baseMva` ohm, current base `baseMva / (sqrt(3) kV_LL)` A. A line's matrix is
  `(r + jx) * miles / Zbase`; a line code's `b` (3 x 3 symmetric, with `bUnit` `uS/mile` or `uS/km`, both or neither)
  becomes the shunt `b * miles * Zbase`. A load's per-phase power is `kW / (baseMva * 1000 / 3)`.
- `gy-gy` transformer at rated ratio: its rated kV must equal both bus bases exactly, and the per-unit ideal
  transformer is then 1:1 with all sequences passing, so it loads as the diagonal `PhaseBranch`
  `z = (rPercent + j xPercent) / 100 * (baseMva * 1000 / kva)`. `d-gy` (1.95.251): a delta / grounded-wye step-down
  at rated ratio, `PhaseBranch.DeltaWyeStepDown`, the same z on the wye side and, in per-unit of each side's rated
  line-to-neutral base, Kersting's generalised matrices reduced to `V_lv = (1/sqrt 3) [[1,0,-1],[-1,1,0],[0,-1,1]] V_hv
  - z I_lv` and `I_hv = (1/sqrt 3) [[1,-1,0],[0,1,-1],[-1,0,1]] I_lv` (the transpose: lossless; no zero sequence on the
  delta side). 1.95.252: with the delta on the lower-voltage side it is a step-up, `PhaseBranch.DeltaWyeStepUp`,
  `V_hv = (1/sqrt 3) [[1,-1,0],[0,1,-1],[-1,0,1]] V_lv - z I_hv` and the transpose for the currents (the wye leads by 30
  degrees). Validated against the IEEE 4 Node Test Feeder's published D-GrY step-down and step-up results
  (Dist3phDeltaWyeTransformerTests). 1.95.253: `y-d` (ungrounded wye / delta) and `d-d` (delta / delta) with equal
  winding impedances, `PhaseBranch.WyeDeltaStepDown` / `WyeDeltaStepUp` / `DeltaDelta`: in sequence terms Y-D is the
  D-GrY matrices of the same direction (the floating wye neutral sits at the mean of the phase voltages, which reduces
  exactly to them) and D-D is `I - J/3` (no shift, no zero sequence) for both voltages and currents. Their secondary is
  three-wire (`HasDeltaSecondary`): the sweep refuses wye loads, capacitors, line charging and regulators fed from it, the
  loader a `gy-gy` transformer. Validated against the published Y-D and D-D results (Dist3phDeltaSecondaryTransformerTests:
  61 of 72 values within half a printed digit; 11 residuals pinned, one D-D and ten Y-D against the IEEE Y-D column, up
  to 1.03 V, where the published Y-D node 2 table disagrees with the IEEE's own Y-D line currents; the earlier 1.26 to
  1.86 V low phase a pattern belonged to the grounded Gr Y-D column). 1.95.254: off-nominal ratios, rated kV within 20 % of the bus bases: per-unit ideal ratio t = (base_from / rated_from)
  (rated_to / base_to) (`PhaseBranch.TransformerRatio` scaling the shift matrices; for `gy-gy` the `RegulatorRatio`
  element on every phase), impedance moved to the To base by (rated_to / base_to)^2; proved by per-unit invariance
  (Dist3phOffNominalTransformerTests). Any other connection, a `d-gy` or `y-d` with equal rated voltages, a rated kV more than 20 % from its bus base and a transformer with `rPercent = xPercent = 0` are
  refused with a message; nothing is approximated. Two open banks complete the set: `oy-od` (open wye / open delta, the
  two primaries on From-side phases A and B to earth; the step-down connection only, a step-up is refused,
  `PhaseBranch.OpenWyeOpenDelta`) and `od-od` (open delta / open delta, `PhaseBranch.OpenDeltaOpenDelta`). `y-d`, `d-d`,
  `oy-od` and `od-od` take an optional `windings` array of per-winding `{rPercent, xPercent}` on the bank's kVA (three,
  `ab`, `bc`, `ca`, for `y-d` and `d-d`; two, `ab`, `bc`, for the open banks) in place of the bank's `rPercent` and
  `xPercent`, for unequal winding impedances (`TransformerWindings`); giving both is refused, and `windings` on any
  other connection is refused. Equal windings give exactly the bank-impedance arithmetic.
- Lines, transformers, switches and regulators share one branch id space (the duplicate-id message still says "lines
  and transformers"); capacitors and loads each have their own. `MultiPhaseModel.Branches` holds lines, then transformers, then closed
  switches, then regulators.
- Optional sections: `lineCodes`, `lines`, `transformers`, `switches`, `regulators`, `capacitors`, `loads`. The radial
  check still needs `n - 1` branches (lines, transformers, closed switches and regulators).
- `phases` on a line, switch or regulator is `abc` (default), `ab`, `ac`, `bc`, `a`, `b` or `c`; a lateral's line code
  must be zero on the phases it does not carry and have a non-zero self impedance on each it does.
- `switches`: `from`, `to`, `closed`. An open switch is recorded in `MultiPhaseFeeder.OpenSwitchIds` and is not a
  branch, so a normally-open tie never closes a loop.
- `regulators`: `from` (nearer the source), `to` (the output bus), `tapStepPercent` (above 0) and `taps` (one integer
  per phase, 0 on a phase the regulator does not have). Ratio `1 + tapStepPercent / 100 * tap`; it must stay above 0.
- `capacitors`: `bus` and `kvar` per phase (not negative, at least one non-zero).
- `loads`: `connection` `wye` or `delta` (delta entries are a-b, b-c, c-a), `model` `constant-power`,
  `constant-current` or `constant-impedance`. `zip` is refused with a message: write up to three loads on one bus,
  one of each model.
- A bus may carry a display `name` (the IEEE 123 feeder names its regulator output nodes RG1 to RG4).
- `converters` (optional): active-front-end units for the three-phase harmonic study only (`harmonics --afe feeder`):
  `id`, `bus`, `kva` (above 0), `xPercent` (above 0) and optional `rPercent` (0 or more, default 0), the internal
  impedance on the unit's own rating. No default impedance exists: every value comes from the file. Converter ids are
  their own id space.

**The v2 form, `gridsim-dist3ph/2`.** `MultiPhaseJsonLoader.ParseV2` reads the JSON v2 form of the same feeder
(`schemas/v2/dist3ph.schema.json`): the solver's per-unit model written exactly, every per-unit number (series and shunt
matrices, regulator and transformer ratios, source voltages, load powers, capacitor ratings) as the double the model
holds, with `baseMva` and each bus's kV base beside it, a provenance default and optional per-element entries
(`bus:<id>`, `branch:<id>`, `capacitor:<id>`, `load:<id>`, `converter:<id>`; a `CuratedPlaceholder` note must carry
`[VERIFY]`), and unknown members refused. Converters are written as `ratingMva`, `xOnRating` and optional `rOnRating`
(per unit on the unit's own rating). The bundled schema checks the structure (every error at once, with JSON
pointers); then the v1 reader's own checks and messages apply (sizes, finite numbers, unique ids, references, one
source, one radial tree, the three-wire rule, phases). `MultiPhaseV2Writer` followed by `ParseV2` gives back the same
model bit for bit, and its text is byte-stable. `dist3ph <feeder> to-v2 --to <out.json>` writes it and proves the
round trip. `MultiPhaseJsonLoader.LoadAny` picks the reader by the file's tag.

The estimators read a `gridsim-dist3ph-measurements/1` file with the same strictness: `voltageMagnitudes` (`bus`,
`phase` a, b or c, `valuePu`, `sigmaPu`), `currents` (`from`, `to`, `phase`, `realPu`, `imagPu`, `sigmaPu`; either
orientation of a branch for `wls` and `wlav`; for `branch-current` either orientation of a pure series branch, a
reversed one being re-keyed to the model orientation with its phasor negated, while a reversed current on a branch with a
regulator or line charging is refused with exit 1, because the current into its To end is not minus the current into its
From end), and an optional `pseudoVoltage` (`valuePu`, `sigmaPu`) that adds a nominal-voltage
pseudo-measurement on every phase of every non-source bus without a voltage measurement
(`DistributionPseudoMeasurements.NominalVoltageMagnitudes`).

### The adapter from a positive-sequence case

`MultiPhaseGridModelAdapter.FromGridModel` (`MultiPhaseGridModelAdapter.cs`) lifts a radial `GridModel` into a balanced
`MultiPhaseFeeder`, so `dist3ph` accepts a MATPOWER `.m` file or a JSON case folder. In per-unit on the three-phase
base a positive-sequence quantity is the per-phase quantity, so the data mapping is exact (the pinned agreement with
Newton is `1e-9` pu):

```
branch r + jx            -> diagonal 3 x 3 series impedance (r + jx on every phase, no mutual coupling)
branch charging B        -> diagonal shunt jB, half at each end (the same pi model)
bus load Pd + jQd        -> per-phase (Pd + jQd) / BaseMva on every phase
ZIP load                 -> constant impedance (Ap Pd + j Aq Qd), constant current (Bp Pd + j Bq Qd),
                            constant power (Cp Pd + j Cq Qd): the same power at every voltage
generator at a PQ bus    -> a constant-power load of minus its Pg + jQg
bus shunt Bs > 0         -> a capacitor bank of Bs / BaseMva per phase
slack bus                -> the source at its voltage setpoint (else Vm) and angle VaDeg, -120 and +120 on b and c
```

Refused, naming the cause (exit 1 at the CLI): a PV bus, an off-nominal tap or a phase shift, a shunt conductance or a
shunt reactor, a bus without a base kV, anything other than exactly one slack, and a network that is not one radial
tree. Out-of-service branches are left out. The sweep on the lift of case33bw agrees with the Newton power flow
to `1e-9` pu (pinned by `MultiPhaseGridModelAdapterTests`).

## Algorithm

### The `serve` verb

`RunServe` (`src/GridSim.Cli/Program.cs`) loads the case (a built-in name, a `.m` file or a JSON case folder), reads `*.json` files from `--measurements-dir` in ordinal file-name order (`--max-windows N`, at least 1, takes the
first N), and loads each with `MeasurementSet.Load(file, now)`. `now` is the real clock, or `--utc-now` (an ISO 8601
instant at or before the real clock; a later one is `REJECTED`, exit 2). A window newer than the horizon at load is
counted as rejected-fresh; an undated window stops the run (`REJECTED`, exit 2). It builds
`RealtimeEstimatorOptions { UtcNow = now, Estimation = { ComputeStateCovariance = true } }` and prints

```
serving <method> estimator over N window(s) from <dir>
horizon guard: now=<now>, newest processable=<now - 169 h>
  #<sequence> <state time> <OK | status | non-finite> [J=<J> chi2=<PASS|FAIL>] max-sigma |V|=<E3> pu angle=<E3> deg
done: processed P, converged C, rejected-fresh F, failed X
estimated|filtered state at window #N (<time>), <method>:
  | bus | |V| pu | sigma |V| pu | angle deg | sigma angle deg |
```

Each converged (`wls`) or finite (`ekf`, `ukf`, `enkf`) window line ends with the largest sigma of `|V|` and of the angle
(` sigma=n/a` when the WLS covariance was not computed, above 2,000 states). After `done:` the last usable window's state
is printed, one row per bus.

Exit codes: 0 when at least one window converged and none failed; 3 when any window failed, or when no window
converged (the reason is on stderr: none of the estimated windows converged, all windows are newer than the horizon,
or every window failed); 2 for a missing argument, a `--method` other than `wls`, `ekf`, `ukf` or `enkf`, a bad or
later-than-real `--utc-now`, a `--max-windows` below 1, a filter size refusal, or a `ForwardInferenceException`; 1 when
the case does not load or the directory holds no `*.json`. With `--audit-log` the run is recorded
([ch. 31](<0031 Audit log and diagnostics.md>)).

### Static engine (`--method wls`)

`RealtimeEstimatorEngine` runs `WlsStateEstimator` on each window. Defaults (`RealtimeEstimatorOptions`):
`WarmStartFromPrior = true`, `ContinueOnError = true`. The estimator options are `EstimationOptions` defaults
(tolerance `1e-8`, 30 iterations, chi-square confidence 0.95; see ch. 6) with `ComputeStateCovariance = true`.

```
for each admitted window:
    opts = (prior exists and WarmStartFromPrior) ? Estimation with { WarmStart = prior, FlatStart = false } : Estimation
    result = WlsStateEstimator.Estimate(model, window, opts)
    InvalidDataException or NotSupportedException  -> failed += 1; skip   (rethrown when ContinueOnError is false)
    processed += 1
    if result.Converged:  converged += 1;  prior = result.ToPowerFlowResult(model)
    else if not ContinueOnError:  throw InvalidOperationException
    emit frame
```

A non-converged result is emitted with its status and never becomes the warm-start prior. The line prints
`OK` for a converged window, otherwise the status name, then `J = ObjectiveJ` with two decimals and the chi-square
verdict.

### Recursive engine (`--method ekf|ukf|enkf`)

`RealtimeDseEngine` (`src/GridSim.Core/Simulation/RealtimeDseEngine.cs`) binds one filter's `Step(window, dt)`:
`new RealtimeDseEngine(model)` is the EKF, `RealtimeDseEngine.Unscented(model)` the UKF and
`RealtimeDseEngine.Ensemble(model)` the EnKF. Per window, after the guard of the streaming contract:

```
dt = (previous processed window exists) ? (TakenUtc - previous TakenUtc).TotalSeconds : 1.0
if dt < 0:  InvalidDataException (windows must arrive in time order)
estimate = filter.Step(window, dt)              dt = 0 passes through as a pure update
InvalidDataException or InvalidOperationException -> failed += 1; skip   (rethrown when ContinueOnError is false)
processed += 1; if estimate.Converged: converged += 1
AssertOutputHistorical(TakenUtc); emit DseFrame
```

The two innovation-form filters invert a dense `m x m` matrix each window. Their factories refuse a case with
`s > DenseEstimationLimits.RecursiveFilterMaxState` (2,500) at construction, and so do the UKF and EnKF constructors
themselves, with the same limit and exception as the EKF's own constructor (the factory checks first, so the `serve`
message is the factory's). Each window of the engine refuses `m > DenseEstimationLimits.InnovationFilterMaxMeasurements`
(2,500) measurements; that per-window check is the engine's, not the filters' `Step`, so a Core caller stepping a UKF or
an EnKF directly has no `m` limit. Every refusal is a `NotSupportedException`, which `serve` reports as
`error: serve --method <method>: <reason>` with exit 2. The `m` refusal names the window and says to use `--method ekf`
(information form, no `m x m` inverse) or `wls`. The cost per window is `O(s^3)` for the EKF. For the UKF it is `O(s^3)` plus `O((2s + 1)(m^2 + s m))` for the
sigma-point covariances, plus `O(m^3)`, `O(s m^2)` and `O(s^2 m)` for the inverse, the gain and the update. For the
EnKF it is `O(N m^2)` plus `O(N s m)` plus `O(m^3)` plus `O(s m^2)` for the gain, which leads when `s` exceeds `N`.

### The three-phase sweep

`ThreePhaseSweep` (`ThreePhaseSweep.cs`) is the matrix generalisation of the positive-sequence backward/forward sweep.

```
tree:     BFS from the source; every non-source bus gets one parent branch.
          NonRadial (Converged = false, Iterations = 0) when a branch names an unknown bus, a bus is not reached,
          or branchCount != busCount - 1.
phases:   source = abc; a bus has the phases of its parent branch. A branch may not carry a phase its upstream bus
          lacks; a regulator's From bus must be the one nearer the source; a load or capacitor may draw only on
          phases its bus has. Each violation throws ArgumentException naming the element.
start:    every bus at the source voltage, absent phases zero.
repeat (at most MaxIterations):
  backward, leaves to source, per bus (reverse BFS order):
      acc_p = (V_p == 0) ? 0 : conj(S_p / V_p)                         constant-power wye loads
      acc  += constant-current, constant-impedance and delta load currents, capacitor currents j B V,
              the To-end half of the feeding branch's shunt
      for each child branch: acc += i_branch                           (a plain line)
                             or  acc += a .* (i_branch + (ShuntY / 2) V_out),  V_out = a .* V      (regulator, shunt)
      i_parent_branch = acc
  forward, source to leaves:
      V_child_p = V_in_p - (Z i_branch)_p      for each phase the branch carries,  V_in = a .* V_parent with a regulator
  maxDelta = largest |V_new - V_old| over every bus and phase
  NaN or Inf -> status NonFinite;   maxDelta < Tolerance -> Converged
```

`MultiPhaseResult` carries `Status` (`Converged`, `MaxIterations`, `NonFinite`, `NonRadial`), `Iterations`,
`MaxDelta`, `V` (per bus, three complex), `BusPhases`, `BranchCurrents` (the current through each branch's series
impedance toward its To bus, after any regulator), `VoltageUnbalancePercent` and `PositiveSequence()`. On a balanced
diagonal model the sweep is three decoupled positive-sequence sweeps.

### Sequence-network fault study

`SequenceFaultStudy` (`SequenceFaultStudy.cs`) reduces each branch to its transposed-equivalent sequence impedances and
accumulates them along the unique path from the source to the fault bus. The source is an infinite bus (zero source
impedance) at the prefault `E1 = 1.0 pu`.

```
zSelf = (z00 + z11 + z22) / 3          zMutual = (z01 + z02 + z10 + z12 + z20 + z21) / 6
z1 = z2 = zSelf - zMutual              z0 = zSelf + 2 zMutual
Z0, Z1, Z2 = sums of the branch z0, z1, z2 on the path source -> fault bus

three-phase        I = |E1 / (Z1 + Zf)|
single line-ground I0 = I1 = I2 = E1 / (Z0 + Z1 + Z2 + 3 Zf);          fault current |3 I1|
line-to-line       I1 = E1 / (Z1 + Z2 + Zf);  I2 = -I1;  I0 = 0;      fault current sqrt(3) |I1|
double line-ground Zg = Z0 + 3 Zg_ground;  Zpar = Z2 Zg / (Z2 + Zg);  I1 = E1 / (Z1 + Zpar);
                   I0 = -I1 Z2 / (Z2 + Zg);                            ground current |3 I0|
```

Shunt elements (capacitors, line charging and loads of every kind) are neglected, and a closed switch adds zero
impedance. A single- or two-phase branch or a regulator on the path to the fault is refused, naming the branch;
elsewhere in the feeder they do not enter the study. A fault at the source bus has a zero loop impedance and is
refused. A network with parallel or loop branches in the source's component is refused, because the path sum would
misstate the current. Every fault type checks its own loop before dividing: three-phase `Z1 + Zf`, line-to-line
`Z1 + Z2 + Zf`, double line-to-ground both `Z2 + Z0 + 3 Zg` and `Z1 + Z2 || Zg`, single line-to-ground
`Z0 + Z1 + Z2 + 3 Zf`; a zero loop (a bolted fault at the source or at a bus joined to it only by closed switches) and a
non-finite current are refused, naming the type (`ArgumentException`).

### Node-voltage WLS estimator

`ThreePhaseWlsEstimator.Estimate` (`ThreePhaseWlsEstimator.cs`) is a Gauss-Newton estimator. The state is the
rectangular voltage of every phase of every non-source bus, `s = 6 * nonSourceBuses` reals; the source voltage is the
fixed reference. The start is every bus at the source voltage.

```
voltage magnitude row:  h = sqrt(Vre^2 + Vim^2),   dh/dVre = Vre / h,   dh/dVim = Vim / h
current phasor rows:    I_p = sum_q Y_pq (V_from,q - V_to,q),    Y = Z^-1 (the branch's 3 x 3 series admittance)
                        two real rows per phasor (real and imaginary), linear in the state
weight:                 w = reweight / sigma^2          (sigma per rectangular component for a current)
iterate:                G = sum w h h^T,   rhs = sum w h (z - h(x)),   dx = G^-1 rhs,   x += dx
                        converged when max|dx| < tolerance        (defaults 1e-10 and 50 iterations)
```

`G` is inverted by `DseLinAlg.Invert`; a singular `G` (an unobservable set) ends the run with `Converged = false`. The
gain is dense, so a state above `ThreePhaseWlsEstimator.MaxState` = `DenseEstimationLimits.RecursiveFilterMaxState`
(2,500 states, 416 non-source buses) is refused with `NotSupportedException` before anything `s x s` is allocated.
A current measurement authored against the reversed endpoints is re-keyed onto the model orientation and negated, since
a `PhaseBranch` is a pure series element; a measurement that names no branch in either orientation throws
`ArgumentException`. A source-bus magnitude row has no state column and is left out of both the fit and
`ObjectiveJ`, so no estimate could reduce it.

With `robust: true` the Gauss-Newton solve sits inside a weighted-least-absolute-value IRLS loop of at most
`maxRobustIterations` (default 8; `--robust-passes`) outer passes. After each converged pass every row's reweight becomes
`1 / max(|standardised residual|, robustFloor)` with `robustFloor` default `1e-3` (`--robust-floor`); a current row's
standardised residual is the modulus of its complex residual over `sigma`, and both of its real rows share one weight.
The loop stops when the largest weight change is below `IrlsWeightTolerance` (`1e-3`). When the last allowed pass still
moves a weight by that much, `ThreePhaseWlsResult.IrlsCapReached` is true; `Converged` is not changed, and the CLI prints
`WARNING: the robust re-weighting (IRLS) reached its cap of N pass(es) before the weights settled; ...` (exit stays 0,
result field `irls_cap_reached`). `ObjectiveJ` is the plain weighted sum of squared residuals at the final state,
without the reweights.

`ThreePhaseWlsResult.Residuals` lists one `ThreePhaseMeasurementResidual` per measurement in the fit (kind, buses as
authored, phase, measured, estimate, sigma, `Standardised = |m - e| / sigma`): magnitudes, then pseudo-measurements,
then currents, a reversed current as authored, source-bus magnitudes left out as in `J`. The sum of `Standardised^2` is
`ObjectiveJ`.

The estimator models loads and capacitors by leaving them out (it has no injection measurements). It refuses, by name
(`RequireSupportedElements`), laterals, line charging, regulators and closed switches: none can be written as
`Z^-1 (V_from - V_to)`.

### Branch-current estimator

`BranchCurrentDsse.Estimate` (`BranchCurrentDsse.cs`, the Baran-Kelley formulation) takes the per-phase branch currents
as the state. It WLS-combines the redundant current phasors per branch and phase and recovers the voltages in one
forward sweep. There is no iteration and no tolerance.

```
combine:  I_(branch,phase) = sum(w I_measured) / sum(w),     w = 1 / sigma^2
sweep:    V_child = V_out - Z i_series                (v1 line: V_out = V_parent, i_series = I_from)
          with a regulator:  V_out = a .* V_parent;  i_series = I_from ./ a
          with line charging:  i_series = i_series - (ShuntY / 2) V_out
          a lateral drops only on its phases; the other phases are exactly zero
          a branch whose From end is downstream (a plain branch, no regulator or shunt, facing the source):
                             V_child = V_parent + Z I_from
```

A measured current is the current into the branch at its From bus. `Converged` is true only when the model has exactly
`n - 1` branches, each joining two known buses, every bus is reached from the source, every phase each tree branch
carries has a current phasor with positive weight, and every value is finite. A branch phase without one is listed in
`BranchCurrentEstimate.UnobservableBranchPhases` (`1-2 b`) and `Converged` is false (CLI exit 3, the first 20 listed).
A current named against the reversed endpoints of a pure series branch is re-keyed and negated (exact); across a
regulator or line charging it is refused, naming the branch, and a current on no branch in either orientation, a
regulator or a shunt that faces the source, and a current on a phase the branch does not carry throw
`ArgumentException`. Loads and capacitors do not enter the formulation: the measured currents
already carry what they draw.

### The `dist3ph` verb

`RunDist3ph` (`src/GridSim.Cli/Program.cs`) takes `<feeder.json|case.m|case-folder> [mode]`, with mode `sweep`
(default), `fault`, `estimate`, `harmonics` or `to-v2`. A feeder file of either version is read by
`MultiPhaseJsonLoader.LoadAny` (by its `schema` tag). Each mode accepts only its own flags; another mode's flag exits 2.

| Mode | Flags | Study | Prints |
|---|---|---|---|
| `sweep` | `--tol` (1e-10), `--max-iter` (200) | `ThreePhaseSweep` | per bus and phase `\|V\|` in pu and in line-to-neutral volts, angle, VUF on the first phase of each three-phase bus |
| `fault` | `--bus`, `--type all\|3ph\|slg\|ll\|llg`, `--zf` (0) | `SequenceFaultStudy` | Thevenin `Z0`, `Z1`, `Z2`, the current base at the bus, and each fault current in pu and in amps |
| `estimate` | `--measurements`, `--method wls\|wlav\|branch-current`, `--tol` (1e-10), `--max-iter` (50), `--robust-passes` (8), `--robust-floor` (0.001) | `ThreePhaseWlsEstimator` or `BranchCurrentDsse` | iterations and `J` (WLS, WLAV), the voltage table of `sweep`, then (converged `wls` or `wlav`) the residual table `\| measurement \| phase \| measured pu \| estimate pu \| r/sigma \|` |
| `harmonics` | `--bus`, `--spectrum`, `--current`, `--afe feeder` | `ThreePhaseHarmonicPenetration` | per harmonic order the phase and sequence voltages at the injection bus and the worst bus |
| `to-v2` | `--to <out.json>` | `MultiPhaseV2Writer` | writes the `gridsim-dist3ph/2` form, reads it back and requires the same model bit for bit (exit 3 naming the first difference) |

For `--type llg` the `--zf` value is the ground impedance. `--robust-passes` and `--robust-floor` apply to `wlav` only
(exit 2 otherwise). `harmonics` injects a balanced source from the harmonic source library, rotated `h x 120 degrees`
per phase; it is specified in [ch. 19](<0019 Harmonics.md>). With `--afe feeder` each unit of the feeder file's
`converters` section is stamped into `Y(h)`: on each phase `R + j h X` between the phase and the converter's floating
star point (positive and negative sequence, no zero sequence), on the unit's own rating; a feeder with no `converters`
section exits 1, and any other value of the flag exits 2. Exit codes:
0 success; 1 input error (an unreadable or invalid feeder or measurement file, a bus the feeder does not have, the
source bus for `fault` or `harmonics`, a study that refuses the feeder's elements); 2 usage error; 3 the solve or the
estimate did not converge (the status goes to stderr and no voltages are printed). `estimate --method branch-current`
refuses `--tol`, `--max-iter`, `--robust-passes` and `--robust-floor` (exit 2) and refuses a measurement file that
also holds voltage magnitudes or `pseudoVoltage` (exit 1), because it would ignore them. A feeder above the WLS state
limit exits 1.

### Element support by study

| Element | Sweep | Fault study | WLS / WLAV | Branch-current | Harmonics |
|---|---|---|---|---|---|
| Three-phase line | solved | path impedance | modelled | modelled | `R + j h X` |
| Single- or two-phase lateral | solved | refused on the path, ignored off it | refused | modelled | refused |
| Line charging | pi section | neglected | refused | modelled | `G + j h B` |
| Step regulator (fixed tap) | solved | refused on the path, ignored off it | refused | modelled | refused |
| Closed switch | zero impedance | zero impedance | refused | modelled | one node |
| Capacitor | solved | neglected | accepted, not in the model | not in the formulation | `G + j h B` |
| Constant-current, constant-impedance, delta load | solved | neglected | accepted, not in the model | not in the formulation | not modelled |
| Transformer other than `gy-gy` (`d-gy`, `y-d`, `d-d`, `oy-od`, `od-od`) | solved | refused on the path, ignored off it | refused | refused | refused |
| Converter unit (`converters`) | not read | not read | not read | not read | stamped with `--afe feeder` |

### Other code in this estate

- Synchrophasors. `C37118Decoder`, `C37118ConfigParser`, `PhasorDataConcentrator`, `PmuMeasurementSetMapper`, the
  `LinearPmuStateEstimator` (`Estimation/`) and the `HybridMultiRateScheduler` (`Simulation/`), and the `pmu-ingest`
  verb, are specified in [ch. 33, C37.118 synchrophasor ingest](<0033 C37.118 synchrophasor ingest.md>). The design
  record is [design/c37118-synchrophasor.md](design/c37118-synchrophasor.md).
- CIM and CGMES. `IO/Cim/` (`CimImporter`, `CimGridModelBridge`, `CimExporter`) is specified in
  [ch. 32, CIM/CGMES import and export](<0032 CIM and CGMES import and export.md>).
- Telemetry codecs. `IO/Telemetry/` holds frame codecs for IEC 60870-5-104, DNP3, IEC 61850 GOOSE and Modbus, and
  object-model decoders for IEC-104 and Modbus. The IEC-104 ASDU decoder handles type ids 1, 3, 9, 11, 13, 15, 30, 31,
  34, 35, 36 and 37, and types 5 / 32 (step position) and 7 / 33 (bitstring), which decode to their own point types and
  never become estimator inputs; every command type decodes to an empty object list and is counted as unsupported. DNP3 (the 10-byte link header) and GOOSE are framing only. The live path
  built on these (the `interpose` verb, the correction pipeline, the crosswalk and the quarantine sink) is
  [ch. 20, The RTU/SCADA interposer](<0020 The RTU and SCADA interposer.md>).
- False-data screen and audit. `Estimation/Security/FdiaScreen` is reachable as `estimate --fdia-screen`; the audit
  log (`Diagnostics/AuditLog`, hash-chained, with `EstimationAuditTrail`) through `--audit-log`. See ch. 6 and
  [ch. 31, Audit log and diagnostics](<0031 Audit log and diagnostics.md>). The assurance notes are
  [caf-mapping](../assurance/caf-mapping.md), [nis-oes-gap-assessment](../assurance/nis-oes-gap-assessment.md) and
  [rbac-sbom-secrets-notes](../assurance/rbac-sbom-secrets-notes.md).
- Solver breadth. `Analysis/ContinuationPowerFlow` (loadability margin) and `Analysis/AcOptimalPowerFlow` (DC dispatch
  with an AC loss loop) are in
  [ch. 29](<0029 OPF, CPF, taps, distribution factors and loss allocation.md>).
- Web host. `RealtimeEstimatorHostedService` (`src/GridSim.Web/Realtime/`) is enabled by the configuration key
  `Realtime:Enabled`, reads a case from `Realtime:Case` (default `case9`) and windows from `Realtime:WindowsDir`
  through `ReplayDirectoryWindowFeed`, and runs `RealtimeEstimatorEngine` once over the feed at start-up. The latest
  frame, the run summary and the skipped input files are read from `GET /api/realtime`, which answers 404 when the
  service is off.

## Parameters and defaults

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| Horizon | 169 | hours | A window stamped after `now - 169 h` is skipped | `ForwardInferenceGuard.HorizonHours` |
| `ContinueOnError` | true | - | A malformed window is counted failed and skipped; a non-converged window is emitted with its status and not used as the warm-start prior. With false, either stops the run | `RealtimeEstimatorOptions.ContinueOnError` |
| `WarmStartFromPrior` | true | - | Static engine seeds each window from the last converged estimate | `RealtimeEstimatorOptions.WarmStartFromPrior` |
| First-window `dt` | 1.0 | s | Elapsed time assumed before the first window | `RealtimeDseEngine.Run` |
| Angle process std | 0.05 | rad per sqrt(s) | Random-walk angle noise, `qa = 0.0025` rad^2 per s | `RandomWalkProcessModel(angleProcessStdPerSqrtSec)` |
| Vm process std | 0.01 | pu per sqrt(s) | Random-walk magnitude noise, `qv = 0.0001` pu^2 per s | `RandomWalkProcessModel(vmProcessStdPerSqrtSec)` |
| EKF/UKF initial variance | 1.0 | per-unit squared | Diffuse prior on the diagonal of `P0` | `initialStateVariance` |
| UKF `alpha` | 1e-3 | - | Sigma-point spread | `UnscentedKalmanStateEstimator(alpha)` |
| UKF `beta` | 2.0 | - | Prior-distribution term of `wc[0]` | `UnscentedKalmanStateEstimator(beta)` |
| UKF `kappa` | 0.0 | - | Secondary scaling | `UnscentedKalmanStateEstimator(kappa)` |
| EnKF members | 80 | - | Ensemble size `N` | `EnsembleKalmanStateEstimator(ensembleSize)` |
| EnKF initial std | 0.5 | rad or pu | Spread of the initial members about the flat start | `initialStateStd` |
| EnKF seed | 20260713 | - | Seed from which each window's `System.Random` stream is derived (`WindowSeed`) | `EnsembleKalmanStateEstimator(seed)` |
| Recursive state cap | 2,500 | states | Largest `s` the EKF, UKF and EnKF constructors and the three `serve` engines accept | `DenseEstimationLimits.RecursiveFilterMaxState` |
| Innovation cap | 2,500 | measurements | Largest `m` per window for UKF and EnKF, checked by the streaming engine | `DenseEstimationLimits.InnovationFilterMaxMeasurements` |
| Covariance cap | 2,000 | states | Largest `s` for the WLS `diag(G^-1)` bounds and for HTI | `DenseEstimationLimits.StateCovarianceMaxState` |
| Gauss-Jordan pivot floor | `1e-12 * max\|A\|` | matrix units | A pivot at or below it is singular | `NumericTolerances.RelativePivotFloor`, `PivotFloor` |
| Frequency filter start | 50.0 Hz, var 1.0, RoCoF var 1.0 | Hz, Hz^2, (Hz/s)^2 | Initial state and covariance | `FrequencyRocofFilter(initialFreqHz, initialFreqVar, initialRocofVar)` |
| Frequency process std | 0.001 and 0.05 | Hz and Hz/s per sqrt(s) | `qf` and `qr` of the two states | `freqProcessStd`, `rocofProcessStd` |
| Sweep tolerance | 1e-10 | pu | Largest per-phase voltage change to stop | `ThreePhaseSweepOptions.Tolerance` |
| Sweep iterations | 200 | sweeps | Cap | `ThreePhaseSweepOptions.MaxIterations` |
| Three-phase WLS tolerance | 1e-10 | per-unit | Largest state update to stop | `ThreePhaseWlsEstimator.Estimate(tolerance)` |
| Three-phase WLS iterations | 50 | iterations | Cap per Gauss-Newton pass | `maxIterations` |
| WLAV outer passes | 8 | passes | IRLS cap | `maxRobustIterations` |
| WLAV weight floor | 1e-3 | standardised residuals | Floor in `1 / max(r, floor)` | `robustFloor` |
| WLAV stop | 1e-3 | weight change | Largest weight change to stop | `ThreePhaseWlsEstimator` |
| Pseudo-voltage value | 1.0 | pu | Library default of the nominal pseudo-measurement | `NominalVoltageMagnitudes(nominalPu)` |
| Pseudo-voltage sigma | 0.05 | pu | Library default; the file gives its own | `NominalVoltageMagnitudes(sigma)` |
| Delta nominal voltage | sqrt(3) | pu of line-to-neutral base | Nominal of a delta load | `ThreePhaseSweep.DeltaNominal` |

## Invariants

| Invariant | Pinned by |
|---|---|
| A window newer than the horizon is counted in `WindowsRejectedFresh` and never estimated, by both engines | `RealtimeEstimatorEngineTests.A_window_newer_than_the_horizon_is_skipped_never_estimated`, `RealtimeDseEngineTests.A_fresh_window_in_the_stream_is_skipped`, `RestrictionInvariantTests.Inv4_A_realtime_window_stamped_fresh_is_refused` |
| An undated window is refused, not skipped | `RealtimeEstimatorEngineTests.An_undated_window_is_refused`, `RealtimeDseEngineTests.An_undated_window_is_refused` |
| The horizon is one constant, and the input and output guards share its boundary | `RestrictionInvariantTests.Inv1_The_horizon_is_a_single_knob`, `Inv5_The_output_guard_boundary_matches_the_input_horizon`, `Inv5_A_future_dated_output_is_rejected_mechanically`, `Inv5_A_present_past_stamped_output_is_permitted` |
| A dynamics-fused method not in the review table cannot run; every reviewed entry certifies present-or-past output; the frequency filter is registered | `RestrictionInvariantTests.Inv5_A_dynamics_fused_method_without_review_is_rejected`, `Inv5_Every_reviewed_entry_certifies_present_past_output_and_carries_a_note`, `FrequencyRocofFilterTests.Is_registered_in_the_review_gate` |
| A static estimator needs no review entry | `RestrictionInvariantTests.Inv5_A_static_measurement_only_method_is_permitted_without_review` |
| Streaming is deterministic: two runs over one stream give identical frames | `RealtimeEstimatorEngineTests.Streaming_is_deterministic`, `RealtimeDseEngineTests.Streaming_dse_is_deterministic`, `KalmanStateEstimatorTests.Ekf_is_deterministic`, `Ukf_is_deterministic`, `EnsembleKalmanStateEstimatorTests.Is_deterministic_for_a_fixed_seed` |
| Each window of a historical stream recovers the truth; the filters track a static truth and tighten as information accumulates; the UKF agrees with the EKF | `RealtimeEstimatorEngineTests.Streams_historical_windows_and_recovers_truth_each_time`, `RealtimeDseEngineTests.Tracks_the_truth_over_a_historical_stream`, `KalmanStateEstimatorTests.Ekf_tracks_a_static_truth_to_close_tolerance`, `Confidence_tightens_as_information_accumulates`, `Ukf_tracks_a_static_truth_and_agrees_with_ekf`, `EnsembleKalmanStateEstimatorTests.Ensemble_mean_converges_toward_the_truth` |
| The engine reproduces direct filter stepping to the bit for the UKF and the EnKF, and refuses an oversized window | `R062EstimationWiringTests.Ukf_engine_frames_are_bit_identical_to_stepping_the_filter_directly`, `Enkf_engine_frames_are_bit_identical_to_stepping_the_filter_directly`, `Innovation_form_filters_refuse_an_oversized_window_loudly`, `The_ekf_engine_still_reports_its_method`; `Cli_wires_hti_fdia_noise_floor_and_the_new_serve_methods` is a source-text check |
| Equal timestamps step with `dt = 0` (zero process noise is pinned by the process-model tests); the dt coercion row is a source-text check; an out-of-order window is refused (failed, or fatal without `ContinueOnError`); the engine does not coerce `dt` | `RealtimeDseTimeOrderTests.An_equal_timestamp_is_processed_with_no_extra_process_noise`, `An_out_of_order_window_is_counted_failed_and_skipped_by_default`, `An_out_of_order_window_fails_the_run_when_errors_are_not_tolerated`, `The_engine_no_longer_coerces_a_non_positive_step_to_one_second`, `EstimationRobustnessFixTests.Random_walk_with_zero_dt_adds_no_process_noise`, `Random_walk_with_a_positive_dt_is_unchanged`, `Frequency_filter_with_zero_dt_is_a_pure_update` |
| The dense kernels (Gauss-Jordan inverse, identity congruence, buffer reuse) are bit-identical to the reference loops; the EKF fails loud above its state cap | `KalmanDenseKernelBitIdentityTests.Gauss_jordan_inverse_is_bit_identical_to_the_original_full_width_loop`, `Gauss_jordan_inverse_still_fails_loud_on_a_singular_matrix`, `Identity_congruence_fast_path_is_bit_identical_including_signed_zeros`, `Congruence_takes_the_general_loop_for_non_identity_or_non_finite_input`, `InvertInto_and_CongruenceInto_overwrite_dirty_buffers_to_the_same_bits`, `Ekf_fails_loud_above_the_dense_filter_state_limit` |
| The frequency filter recovers a RoCoF ramp where differencing diverges | `FrequencyRocofFilterTests.Tracks_a_frequency_ramp_and_recovers_rocof`, `Kalman_recovers_rocof_where_naive_differentiation_diverges` |
| The resident output is readable (the REST row is a source-text check) | `RealtimeEstimatorOutputTests.Latest_frame_and_skipped_files_are_readable_after_a_run`, `Rest_api_exposes_the_realtime_output_and_the_service_is_resolvable` |
| The balanced sweep equals an independent scalar fixed point; the positive-sequence collapse of a balanced solution is phase a; VUF is 0 when balanced; unbalanced load splits the phases | `DistributionThreePhaseSweepTests.Balanced_two_bus_matches_the_independent_scalar_fixed_point`, `Positive_sequence_collapse_of_a_balanced_solution_is_phase_a`, `Voltage_unbalance_factor_is_zero_when_balanced_and_positive_when_not`, `Unbalanced_load_splits_the_phase_voltages`, `Voltage_drops_along_a_radial_feeder` |
| Each element has its closed-form solution; an absent phase is exactly zero; a model without the optional parameters solves bit for bit as before | `MultiPhaseElementsTests` (regulator, lateral, constant-impedance, constant-current, delta, capacitor and shunt, switch), `MultiPhaseElementsTests.A_v1_model_is_unchanged_by_the_new_optional_parameters`, `MultiPhaseIeee123Tests.The_solve_is_deterministic_bit_for_bit` |
| The sweep on the IEEE 4-node feeders matches the published voltages to the last printed digit | `MultiPhaseJsonLoaderTests.Ieee4_gyGy_stepdown_matches_the_published_node_voltages_to_rounding` |
| The sweep on the IEEE 123-node feeder matches the published voltages and currents | `MultiPhaseIeee123Tests.The_voltage_profile_matches_the_published_results`, `The_branch_currents_match_the_published_results`, `The_regulator_output_nodes_match_the_published_values_to_rounding`, `The_capacitors_and_the_delta_loads_draw_what_the_published_results_report`, `Every_published_phase_is_modelled_and_no_other` |
| The loader builds the hand-built per-unit model, gives volts and amps, and refuses malformed input naming the path | `MultiPhaseJsonLoaderTests.Loader_builds_exactly_the_hand_built_per_unit_model`, `Bases_give_volts_and_amps`, `Malformed_input_is_refused_naming_the_path`, `Malformed_measurements_are_refused_naming_the_path`, `A_loop_or_an_island_is_refused`, `Not_json_names_the_file` |
| A radial case lifts to within `1e-9` pu of Newton and what the sweep cannot model is refused | `MultiPhaseGridModelAdapterTests.The_balanced_lift_of_case33bw_solves_to_the_newton_voltages`, `A_zip_load_is_lifted_exactly`, `A_capacitor_and_line_charging_are_lifted_exactly`, `What_the_sweep_cannot_model_is_refused`, `MultiPhaseCliModesTests.A_radial_matpower_case_is_lifted_and_swept` |
| The sequence fault currents equal the closed forms on a diagonal line; the sequence reduction separates `z0` from `z1`; `AllFaults` agrees with the single studies | `SequenceFaultStudyTests` (all eight) |
| The Fortescue transform round-trips to machine precision; a balanced set has only a positive sequence | `SymmetricalComponentsTests.Phase_sequence_round_trip_is_machine_precision`, `A_balanced_set_has_only_a_positive_sequence`, `Positive_sequence_helper_agrees_with_the_transform` |
| The transformer connection model scales by the ratio, and the delta shifts the positive sequence by -30 degrees and blocks zero sequence | `TransformerConnectionModelTests.WyeG_WyeG_scales_by_turns_ratio_without_phase_shift`, `Delta_WyeG_lags_positive_sequence_by_30_degrees_and_blocks_zero_sequence` |
| The three-phase WLS recovers the sweep truth (voltage error below `1e-8` pu, `J` below `1e-10`); WLAV resists a gross measurement; reversed currents give the same estimate; an unmatched current fails loud; the source row is out of `J` | `DistributionThreePhaseWlsTests.Recovers_the_sweep_truth_to_machine_precision`, `Robust_wlav_mode_is_more_resistant_to_a_gross_measurement`, `DistributionEstimatorFixTests.Reversed_endpoint_currents_give_the_same_estimate_as_model_oriented_ones`, `A_current_matching_no_branch_fails_loud`, `Source_bus_magnitude_residuals_are_not_counted_in_the_objective` |
| The branch-current estimator reproduces the sweep from exact branch currents, on the 4-node and 123-node feeders, and flags a meshed or parallel network | `MultiPhaseCliModesTests.The_branch_current_estimator_reproduces_the_sweep_from_its_exact_branch_currents`, `N167ExtendedElementSolversTests.The_branch_current_estimator_reproduces_the_ieee123_sweep_from_its_exact_from_end_currents`, `The_branch_current_estimator_from_the_published_ieee123_currents_gives_the_published_voltages`, `DistributionEstimatorFixTests.Branch_current_dsse_flags_a_meshed_network`, `Branch_current_dsse_flags_parallel_branches`, `Branch_current_dsse_still_converges_on_a_radial_feeder` |
| Each solver accepts the element kinds it models and refuses the rest by name | `N167ExtendedElementSolversTests.The_fault_study_refuses_a_lateral_or_a_regulator_on_the_path_naming_the_branch`, `The_wls_estimator_refuses_laterals_line_charging_regulators_and_switches_by_name`, `The_branch_current_estimator_refuses_a_reversed_regulator_and_a_current_on_an_absent_phase`, `The_harmonic_penetration_refuses_laterals_and_regulators_by_name`, `MultiPhaseCliModesTests.The_branch_current_estimator_refuses_a_file_with_voltage_measurements_it_would_ignore` |
| The fault study neglects shunts and loads; the WLS accepts capacitors and the load kinds; a v1 feeder's branch-current estimate is unchanged to the bit | `N167ExtendedElementSolversTests.The_fault_study_neglects_shunts_and_loads_and_adds_nothing_for_a_switch_or_an_off_path_lateral`, `The_wls_estimator_accepts_capacitors_and_zip_and_delta_loads_and_recovers_the_sweep`, `A_v1_feeder_estimate_is_the_pre_n167_forward_sweep_to_the_bit` |
| Pseudo-measurements are generated for unmetered buses only | `DistributionPseudoMeasurementsTests.Generates_nominal_voltage_pseudos_for_unmetered_buses_only` |

## Worked example

Timings, stamps and the `now` of each run differ by machine and run; every output block below is pasted from
`docs/_captures/realtime/`.

### A stream through `serve`

Four case9 windows, one minute apart, are in `results/realtime/windows` (git-ignored; the recipe follows). Each is a `gridsim-measurements/1` file of
63 measurements sampled from the solved case with `gridsim estimate case9 --synth --seed <20260714..20260717>` and
re-stamped `2024-03-01T12:00:00Z` to `12:03:00Z`. The state size is `s = 8 + 9 = 17`, so each window has `63 - 17 = 46`
degrees of freedom and the 95 percent chi-square threshold is 62.83.

```
$ gridsim serve case9 --measurements-dir results/realtime/windows --method wls
serving wls estimator over 4 window(s) from results/realtime/windows
horizon guard: now=2026-10-04T18:39:00.7522631Z, newest processable=2026-09-27T17:39:00.7522631Z
  #0 2024-03-01T12:00:00.0000000Z OK J=47.35 chi2=PASS
  #1 2024-03-01T12:01:00.0000000Z OK J=61.17 chi2=PASS
  #2 2024-03-01T12:02:00.0000000Z OK J=44.61 chi2=PASS
  #3 2024-03-01T12:03:00.0000000Z OK J=53.41 chi2=PASS
done: processed 4, converged 4, rejected-fresh 0, failed 0
[exit 0]
```

The horizon line is `now - 169 h`: `2026-10-04T18:39 - 7 d - 1 h = 2026-09-27T17:39`. Every window is older, so all four
are processed. `J` is the weighted residual sum of squares of each window's fit; each is below 62.83, so each passes.
Window 1 (`J = 61.17`) is the closest to the threshold. The window states are independent: the static engine
warm-starts from the previous converged state but carries no covariance.

The same stream through the three filters prints a status line per window and no `J`, because a filter step has no
chi-square test:

```
$ gridsim serve case9 --measurements-dir results/realtime/windows --method ekf
serving ekf estimator over 4 window(s) from results/realtime/windows
horizon guard: now=2026-10-04T18:39:01.8747937Z, newest processable=2026-09-27T17:39:01.8747937Z
  #0 2024-03-01T12:00:00.0000000Z OK
  #1 2024-03-01T12:01:00.0000000Z OK
  #2 2024-03-01T12:02:00.0000000Z OK
  #3 2024-03-01T12:03:00.0000000Z OK
done: processed 4, converged 4, rejected-fresh 0, failed 0
[exit 0]
```

`--method ukf` and `--method enkf` print the same shape (`docs/_captures/realtime/serve-ukf.txt` and
`serve-enkf.txt`). For the EKF the process noise between window 0 and window 1 is `dt = 60 s`: `qa dt = 0.0025 * 60 = 0.15`
rad^2 on every angle state and `qv dt = 0.0001 * 60 = 0.006` pu^2 on every magnitude state; window 0 uses `dt = 1 s`,
so `0.0025` and `0.0001`. `serve` prints status only; the filtered state and its per-bus sigma are in the
`DseFrame`, which a Core caller or a test reads.

The guard cuts the stream at the horizon. With `--utc-now 2024-03-08T13:01:00Z` the horizon is exactly
`2024-03-01T12:01:00Z`; window 1 is on the boundary and passes, windows 2 and 3 are later and are skipped:

```
$ gridsim serve case9 --measurements-dir results/realtime/windows --utc-now 2024-03-08T13:01:00Z
serving wls estimator over 4 window(s) from results/realtime/windows
horizon guard: now=2024-03-08T13:01:00.0000000Z, newest processable=2024-03-01T12:01:00.0000000Z
  #0 2024-03-01T12:00:00.0000000Z OK J=47.35 chi2=PASS
  #1 2024-03-01T12:01:00.0000000Z OK J=61.17 chi2=PASS
done: processed 2, converged 2, rejected-fresh 2, failed 0
[exit 0]
```

With `--utc-now 2024-03-01T12:00:00Z` every window is newer than the horizon, and the run fails loud:

```
$ gridsim serve case9 --measurements-dir results/realtime/windows --method ekf --utc-now 2024-03-01T12:00:00Z
serving ekf estimator over 4 window(s) from results/realtime/windows
horizon guard: now=2024-03-01T12:00:00.0000000Z, newest processable=2024-02-23T11:00:00.0000000Z
done: processed 0, converged 0, rejected-fresh 4, failed 0
error: no window produced a usable estimate: all 4 window(s) are newer than the 169 h horizon.
[exit 3]
```

A case above the state cap is refused at construction. case1354pegase has `s = 1354 + 1353 = 2707`:

```
$ gridsim serve pegase/case1354pegase.m --measurements-dir results/realtime/windows --method ekf
...
serving ekf estimator over 4 window(s) from results/realtime/windows
horizon guard: now=2026-10-04T18:39:08.4645915Z, newest processable=2026-09-27T17:39:08.4645915Z
error: serve --method ekf: The Kalman filter is dense (s x s covariance inverted every window) and is limited to 2500 states; this case has 2707 (1354 buses). Use the snapshot WLS estimator, which switches to the sparse backend at this size.
[exit 2]
```

### The UKF weights for case9

From the UKF equations with `alpha = 1e-3`, `beta = 2`, `kappa = 0`, `s = 17`:

```
lambda = 1e-6 * 17 - 17 = -16.999983         c = 17 + lambda = 1.7e-5         sigma points = 2 * 17 + 1 = 35
wm[0] = -16.999983 / 1.7e-5 = -999,999.0
wc[0] = -999,999.0 + (1 - 1e-6 + 2) = -999,996.0
wm[i] = wc[i] = 1 / (2 * 1.7e-5) = 29,411.76          i = 1 .. 34
```

The weights sum to one (`wm[0] + 34 * 29,411.76 = 1`), and the 34 outer points sit `sqrt(c) = 0.0041231` of a
Cholesky column from the mean.

### The IEEE 4-node feeder: sweep and validation

`data/dist3ph/` holds the IEEE 4 Node Test Feeder (IEEE PES Distribution System Analysis Subcommittee, W. H. Kersting),
grounded-wye / grounded-wye step-down, with the balanced and the unbalanced load: an infinite 12.47 kV source at node 1,
a 2000 ft overhead line 1-2, a 6000 kVA 12.47/4.16 kV transformer 2-3 (`1.0 + j6.0` percent), a 2500 ft line 3-4 and a
wye constant-power load at node 4. The line code is the published conductor geometry reduced by Kersting's modified
Carson equations and Kron reduction (computed offline, not by a routine in GridSim; phase-a self impedance `0.4576 + j1.0780` ohm per mile); line
capacitance is neglected. The step-up case (12.47/24.9 kV) is bundled for the D-GrY, Y-D and D-D connections
(`ieee4-dGy-stepup-*`, `ieee4-yD-stepup-*`, `ieee4-dD-stepup-*`); only the GrY-GrY step-up is not bundled.

```
$ gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json
THREE-PHASE DISTRIBUTION (sweep) - IEEE 4-node test feeder, grounded-wye / grounded-wye step-down, unbalanced load   [EXPERIMENTAL]
  file: data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json
  4 buses, 3 branches, 1 loads, base 6 MVA (three-phase)
  sweep CONVERGED in 39 iteration(s), max voltage change 7.191E-011 pu (tolerance 1.000E-010)
  | bus | kV LL | phase | |V| pu | |V| V (LN) | angle deg | VUF % |
  |-----|-------|-------|--------|------------|-----------|-------|
  | 1 | 12.47 | a | 1.000000 | 7199.56 | 0.000 | 0.0000 |
  | 1 | 12.47 | b | 1.000000 | 7199.56 | -120.000 |  |
  | 1 | 12.47 | c | 1.000000 | 7199.56 | 120.000 |  |
  | 2 | 12.47 | a | 0.995022 | 7163.72 | -0.140 | 0.3049 |
  | 2 | 12.47 | b | 0.987628 | 7110.48 | -120.185 |  |
  | 2 | 12.47 | c | 0.983675 | 7082.03 | 119.265 |  |
  | 3 | 4.16 | a | 0.959910 | 2305.49 | -2.258 | 1.9708 |
  | 3 | 4.16 | b | 0.938745 | 2254.66 | -123.625 |  |
  | 3 | 4.16 | c | 0.917157 | 2202.81 | 114.788 |  |
  | 4 | 4.16 | a | 0.905567 | 2174.97 | -4.123 | 6.3762 |
  | 4 | 4.16 | b | 0.803496 | 1929.82 | -126.798 |  |
  | 4 | 4.16 | c | 0.763044 | 1832.66 | 102.845 |  |
[exit 0]
```

The balanced-load capture is `docs/_captures/realtime/dist3ph-sweep-balanced.txt`. The two cases against the published
node voltages (the comparison is half the last printed digit: 0.5 V and 0.05 degrees):

| Case | Node | Published `\|V\|` (V) a / b / c | GridSim `\|V\|` (V) | Published angle (deg) | GridSim angle (deg) |
|---|---|---|---|---|---|
| balanced | 2 | 7107 / 7140 / 7121 | 7106.55 / 7139.71 / 7120.77 | -0.3 / -120.3 / 119.6 | -0.339 / -120.344 / 119.629 |
| balanced | 3 | 2247 / 2269 / 2256 | 2247.41 / 2268.51 / 2255.86 | -3.7 / -123.5 / 116.4 | -3.694 / -123.476 / 116.395 |
| balanced | 4 | 1918 / 2061 / 1981 | 1917.77 / 2061.26 / 1980.77 | -9.1 / -128.3 / 110.9 | -9.073 / -128.315 / 110.856 |
| unbalanced | 2 | 7164 / 7110 / 7082 | 7163.72 / 7110.48 / 7082.03 | -0.1 / -120.2 / 119.3 | -0.140 / -120.185 / 119.265 |
| unbalanced | 3 | 2305 / 2255 / 2203 | 2305.49 / 2254.66 / 2202.81 | -2.3 / -123.6 / 114.8 | -2.258 / -123.625 / 114.788 |
| unbalanced | 4 | 2175 / 1930 / 1833 | 2174.97 / 1929.82 / 1832.66 | -4.1 / -126.8 / 102.8 | -4.123 / -126.798 / 102.845 |

The largest differences are 0.49 V (unbalanced node 3 phase a; balanced node 3 phase b) and 0.045 degrees (unbalanced
node 4 phase c), both inside the printed rounding. The unbalance factor at the load bus is 2.3654 percent for the
balanced-load case and 6.3762 percent for the unbalanced one, from the `VUF %` column.

### The fault study, checked by hand

The fault study on node 4 of the same feeder:

```
$ gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json fault --bus 4
THREE-PHASE DISTRIBUTION (fault) - IEEE 4-node test feeder, grounded-wye / grounded-wye step-down, unbalanced load   [EXPERIMENTAL]
  file: data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json
  4 buses, 3 branches, 1 loads, base 6 MVA (three-phase)
  fault at bus 4 (4.16 kV LL), Zf = 0 pu (resistive), prefault E1 = 1.0 pu, infinite source;
  each branch reduced to its transposed-equivalent sequence impedances (z1 = z2 = zs - zm, z0 = zs + 2 zm).
  Thevenin Z0 = 0.148286 + j0.406336 pu   Z1 = 0.064718 + j0.172093 pu   Z2 = 0.064718 + j0.172093 pu
  current base at this bus 832.717 A
  | fault | current pu | current A |
  |-------|------------|-----------|
  | 3ph | 5.438923 | 4529.08 |
  | slg | 3.748789 | 3121.68 |
  | ll | 4.710245 | 3922.30 |
  | llg (ground) | 2.860006 | 2381.57 |
[exit 0]
```

Hand calculation from the line code (`zSelf = 0.46188 + j1.06375`, `zMutual = 0.15581 + j0.43675` ohm per mile):

```
z1 = zSelf - zMutual = 0.30607 + j0.62700 ohm/mile          z0 = zSelf + 2 zMutual = 0.77351 + j1.93726 ohm/mile
line 1-2:  2000 ft = 0.37879 mile on Zbase = 12.47^2 / 6 = 25.918 ohm:   z1 = 0.004473 + j0.009164 pu
           z0 = 0.011305 + j0.028314 pu
transformer 2-3:  (1.0 + j6.0) / 100 * (6 * 1000 / 6000) = 0.01 + j0.06 pu (z0 = z1)
line 3-4:  2500 ft = 0.47348 mile on Zbase = 4.16^2 / 6 = 2.8843 ohm:    z1 = 0.050245 + j0.102929 pu
           z0 = 0.126981 + j0.318022 pu
Z1 = Z2 = 0.064718 + j0.172093 pu        Z0 = 0.148286 + j0.406336 pu          matches the printed values
3ph: 1 / |Z1| = 5.438923                 slg: 3 / |Z0 + 2 Z1| = 3.748789      ll: sqrt(3) / |2 Z1| = 4.710245
llg: Zpar = Z2 Z0 / (Z2 + Z0);  I1 = 1 / (Z1 + Zpar);  I0 = -I1 Z2 / (Z2 + Z0);  |3 I0| = 2.860006
current base 6e6 / (sqrt(3) * 4160) = 832.717 A;  3ph in amps 5.438923 * 832.717 = 4529.08 A
```

A fault at the source is refused:

```
$ gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json fault --bus 1
THREE-PHASE DISTRIBUTION (fault) - IEEE 4-node test feeder, grounded-wye / grounded-wye step-down, unbalanced load   [EXPERIMENTAL]
  file: data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json
  4 buses, 3 branches, 1 loads, base 6 MVA (three-phase)
error: bus 1 is the source (an infinite bus with zero impedance); a fault there has no finite current in this model.
[exit 1]
```

### Estimation on the 4-node feeder

`data/dist3ph/ieee4-gyGy-stepdown-unbalanced.measurements.json` is synthetic: the sweep solution rounded to six
decimals, with voltage magnitudes at nodes 2 to 4 and current phasors on all three branches, `sigma = 0.001` pu.

```
$ gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json estimate --measurements data/dist3ph/ieee4-gyGy-stepdown-unbalanced.measurements.json
THREE-PHASE DISTRIBUTION (estimate) - IEEE 4-node test feeder, grounded-wye / grounded-wye step-down, unbalanced load   [EXPERIMENTAL]
  file: data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json
  4 buses, 3 branches, 1 loads, base 6 MVA (three-phase)
  measurements: 9 voltage magnitude(s), 9 current phasor(s), 0 nominal-voltage pseudo-measurement(s); method wls
  estimate CONVERGED in 4 iteration(s), objective J = 7.057361E-007
  | bus | kV LL | phase | |V| pu | |V| V (LN) | angle deg | VUF % |
  |-----|-------|-------|--------|------------|-----------|-------|
  | 1 | 12.47 | a | 1.000000 | 7199.56 | 0.000 | 0.0000 |
  ...
  | 4 | 4.16 | a | 0.905567 | 2174.97 | -4.123 | 6.3762 |
  | 4 | 4.16 | b | 0.803496 | 1929.82 | -126.798 |  |
  | 4 | 4.16 | c | 0.763044 | 1832.66 | 102.845 |  |
  ...
[exit 0]
```

The estimate equals the sweep to every printed digit. `J = 7.06e-7` is the rounding of the six-decimal measurements.
The state has `s = 6 * 3 = 18` reals and there are `9 + 18 = 27` measurement rows (the current phasors are two real
rows each), so the gain matrix is `18 x 18`.

The branch-current estimator needs current phasors only. With the same file minus its `voltageMagnitudes` member (saved
as `results/realtime/ieee4-currents.json`):

```
$ gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json estimate --measurements results/realtime/ieee4-currents.json --method branch-current
THREE-PHASE DISTRIBUTION (estimate) - IEEE 4-node test feeder, grounded-wye / grounded-wye step-down, unbalanced load   [EXPERIMENTAL]
  file: data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json
  4 buses, 3 branches, 1 loads, base 6 MVA (three-phase)
  measurements: 9 current phasor(s); method branch-current
  estimate CONVERGED (one forward sweep over 3 measured branch(es))
  | bus | kV LL | phase | |V| pu | |V| V (LN) | angle deg | VUF % |
  |-----|-------|-------|--------|------------|-----------|-------|
  | 1 | 12.47 | a | 1.000000 | 7199.56 | 0.000 | 0.0000 |
  ...
  | 4 | 4.16 | a | 0.905568 | 2174.97 | -4.123 | 6.3762 |
  | 4 | 4.16 | b | 0.803496 | 1929.82 | -126.798 |  |
  | 4 | 4.16 | c | 0.763044 | 1832.66 | 102.845 |  |
[exit 0]
```

Node 4 phase a reads `0.905568` here against `0.905567` from the WLS: the six-decimal rounding of the currents shows
through a single sweep, which has no voltage rows to average it out. With the full file the branch-current estimator is
refused, naming the cause:

```
$ gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json estimate --measurements data/dist3ph/ieee4-gyGy-stepdown-unbalanced.measurements.json --method branch-current
THREE-PHASE DISTRIBUTION (estimate) - IEEE 4-node test feeder, grounded-wye / grounded-wye step-down, unbalanced load   [EXPERIMENTAL]
  file: data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json
  4 buses, 3 branches, 1 loads, base 6 MVA (three-phase)
error: dist3ph estimate --method branch-current uses current phasors only; data/dist3ph/ieee4-gyGy-stepdown-unbalanced.measurements.json also has 9 voltage magnitude(s), which it would ignore.
[exit 1]
```

A gross error separates WLS from WLAV. `results/realtime/ieee4-gross.json` is the bundled file with the node 4 phase c
magnitude changed from `0.763044` to `0.90` pu. The plain WLS follows it (node 4 phase c reads `0.769559`, node 4 phase a
`0.904287`, against the true `0.763044` and `0.905567`); the WLAV reweighting loop recovers the truth to the printed
digits:

```
$ gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json estimate --measurements results/realtime/ieee4-gross.json --method wlav
THREE-PHASE DISTRIBUTION (estimate) - IEEE 4-node test feeder, grounded-wye / grounded-wye step-down, unbalanced load   [EXPERIMENTAL]
  file: data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json
  4 buses, 3 branches, 1 loads, base 6 MVA (three-phase)
  measurements: 9 voltage magnitude(s), 9 current phasor(s), 0 nominal-voltage pseudo-measurement(s); method wlav
  estimate CONVERGED in 24 iteration(s), objective J = 1.875696E+004
  | bus | kV LL | phase | |V| pu | |V| V (LN) | angle deg | VUF % |
  |-----|-------|-------|--------|------------|-----------|-------|
  | 1 | 12.47 | a | 1.000000 | 7199.56 | 0.000 | 0.0000 |
  ...
  | 4 | 4.16 | a | 0.905567 | 2174.97 | -4.123 | 6.3762 |
  | 4 | 4.16 | b | 0.803496 | 1929.82 | -126.798 |  |
  | 4 | 4.16 | c | 0.763044 | 1832.66 | 102.845 |  |
[exit 0]
```

The WLAV `J = 18,756.96` is the plain weighted sum of squares at the WLAV state, dominated by the discarded row:
`((0.90 - 0.763044) / 0.001)^2 = 18,757`. The plain WLS reports the smaller `J = 17,864.74` because it moves the state
toward the bad row. The plain run is `docs/_captures/realtime/dist3ph-estimate-wls-gross.txt`.

An unobservable set does not produce an estimate. With only the bus 2 voltages and the 1-2 branch currents
(`results/realtime/ieee4-thin.json`), the gain matrix is singular:

```
$ gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json estimate --measurements results/realtime/ieee4-thin.json
THREE-PHASE DISTRIBUTION (estimate) - IEEE 4-node test feeder, grounded-wye / grounded-wye step-down, unbalanced load   [EXPERIMENTAL]
  file: data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json
  4 buses, 3 branches, 1 loads, base 6 MVA (three-phase)
  measurements: 3 voltage magnitude(s), 3 current phasor(s), 0 nominal-voltage pseudo-measurement(s); method wls
dist3ph: the three-phase estimator did not converge after 0 iteration(s) (final max state update Infinity); the measurement set may be unobservable. No estimate reported.
[exit 3]
```

Pseudo-measurements supply magnitudes only. They do not fix the angles of buses 3 and 4, so adding
`"pseudoVoltage": { "valuePu": 1.0, "sigmaPu": 0.05 }` to the thin set above leaves it unobservable. They help when the
currents are present and the voltage meters are not: all nine current phasors, the bus 2 magnitudes and that
pseudo-measurement for the two unmetered buses (`results/realtime/ieee4-pseudo.json`) converge in three iterations with
`J = 46.36`, and node 4 phase c reads `0.763047` against the true `0.763044`
(`docs/_captures/realtime/dist3ph-estimate-pseudo.txt`).

### The IEEE 123-node feeder

`data/dist3ph/ieee123.json` is the IEEE 123 Node Test Feeder (IEEE PES Distribution System Analysis Subcommittee;
data and published results in `docs/reference/models/S289-feeder123.zip`): configurations 1 to 12 with their printed
impedance and susceptance, 118 line segments, 85 spot loads (wye and delta; constant power, current and impedance), 4
capacitor banks, 4 regulators on the published taps (RG1 7/7/7, RG2 -1, RG3 0/-1, RG4 8/1/5, each at 0.625 percent per
step) with their output nodes RG1 to RG4, and the switches (closed ones as zero-impedance branches; 54-94 and 151-300
open). The substation transformer is not modelled: the published results start at node 150 = 1.0000 pu. XFM-1 and node
610 serve no load and are omitted.

```
$ gridsim dist3ph data/dist3ph/ieee123.json
THREE-PHASE DISTRIBUTION (sweep) - IEEE 123 Node Test Feeder   [EXPERIMENTAL]
  file: data/dist3ph/ieee123.json
  128 buses, 127 branches, 85 loads, base 5 MVA (three-phase)
  4 regulator(s), 5 closed switch(es), 2 open switch(es) (not branches), 4 capacitor bank(s), 60 single- or two-phase branch(es)
  sweep CONVERGED in 10 iteration(s), max voltage change 1.167E-011 pu (tolerance 1.000E-010)
  | bus | kV LL | phase | |V| pu | |V| V (LN) | angle deg | VUF % |
  |-----|-------|-------|--------|------------|-----------|-------|
  | 1 | 4.16 | a | 1.031303 | 2476.96 | -0.636 | 0.2534 |
  | 1 | 4.16 | b | 1.041294 | 2500.96 | -120.310 |  |
  | 1 | 4.16 | c | 1.034887 | 2485.57 | 119.622 |  |
  | 2 | 4.16 | a | - | - | - | |
  | 2 | 4.16 | b | 1.041073 | 2500.42 | -120.314 |  |
  | 2 | 4.16 | c | - | - | - | |
  ...
[exit 0]
```

Node 2 is a single-phase lateral on phase b, so phases a and c print `-` and have no result row, and the unbalance factor
is printed for three-phase buses only. The published voltage profile and branch currents are transcribed in
`data/dist3ph/ieee123.published-voltages.csv` and `ieee123.published-currents.csv`; `MultiPhaseIeee123Tests` compares
every value:

| Quantity | Compared | Pinned bound | Largest difference now | Where |
|---|---|---|---|---|
| Node voltage magnitude | 268 node-phases | 6e-4 pu | 5.2e-4 pu | node 95 phase a |
| Node voltage angle | 268 node-phases | 0.06 degrees | 0.053 degrees | node 110 phase a |
| Branch current magnitude | 265 branch-phases | 0.1 A | inside the bound | - |
| Branch current angle | 265 branch-phases | 0.1 degrees | inside the bound | - |

The voltage maxima (5.2e-4 pu is the test comment's figure; the 6-decimal capture gives 5.25e-4) are recomputed from a `dist3ph` run against the published CSV (268 of 268 phases matched). The
currents agree to the printed rounding (amps to 2 decimals), which checks the load models, capacitors, line charging
and regulators directly. The regulator output nodes match the published values to rounding: with the source at 1.0000 pu and RG1 on tap
7, node RG1 reads `1 + 0.00625 * 7 = 1.04375` pu on all three phases.

The sequence study refuses a fault anywhere on this feeder, because RG1 sits at its head:

```
$ gridsim dist3ph data/dist3ph/ieee123.json fault --bus 1
THREE-PHASE DISTRIBUTION (fault) - IEEE 123 Node Test Feeder   [EXPERIMENTAL]
...
dist3ph fault: the sequence fault study cannot model the path to bus 1: branch 150-9001 has a step-voltage regulator, whose off-nominal (per-phase) ratio the series sum of sequence impedances does not hold. Use the three-phase sweep.
[exit 1]
```

### A positive-sequence case lifted into three phases

`gridsim dist3ph data/matpower/dist/case33bw.m` lifts the radial MATPOWER case through
`MultiPhaseGridModelAdapter` and sweeps it. Every phase carries the positive-sequence data, so the three phases are
identical and the unbalance factor is 0:

```
$ gridsim dist3ph data/matpower/dist/case33bw.m
THREE-PHASE DISTRIBUTION (sweep) - case33bw   [EXPERIMENTAL]
  file: data/matpower/dist/case33bw.m
  33 buses, 32 branches, 32 loads, base 10 MVA (three-phase)
  sweep CONVERGED in 9 iteration(s), max voltage change 9.431E-011 pu (tolerance 1.000E-010)
  | bus | kV LL | phase | |V| pu | |V| V (LN) | angle deg | VUF % |
  |-----|-------|-------|--------|------------|-----------|-------|
  | 1 | 12.66 | a | 1.000000 | 7309.25 | 0.000 | 0.0000 |
  | 1 | 12.66 | b | 1.000000 | 7309.25 | -120.000 |  |
  | 1 | 12.66 | c | 1.000000 | 7309.25 | 120.000 |  |
  | 2 | 12.66 | a | 0.997032 | 7287.56 | 0.014 | 0.0000 |
  | 2 | 12.66 | b | 0.997032 | 7287.56 | -119.986 |  |
  | 2 | 12.66 | c | 0.997032 | 7287.56 | 120.014 |  |
  ...
```

(The capture, `docs/_captures/realtime/dist3ph-lift-case33bw.txt`, holds the full table and the loader notes.)

## Source map

| File | Type | Responsibility |
|---|---|---|
| `src/GridSim.Core/Simulation/RealtimeEstimatorEngine.cs` | `RealtimeEstimatorEngine`, `RealtimeEstimatorOptions`, `RealtimeEstimateFrame`, `RealtimeRunSummary` | Static streaming estimator: guard, warm start, resilience, output guard |
| `src/GridSim.Core/Simulation/RealtimeDseEngine.cs` | `RealtimeDseEngine`, `DseFrame` | Recursive streaming estimator: guard, time order, size guards, filter binding |
| `src/GridSim.Core/Estimation/Dse/KalmanStateEstimator.cs` | `KalmanStateEstimator`, `DseEstimate`, `DseLinAlg` | EKF in information form; dense inverse, congruence, Cholesky |
| `src/GridSim.Core/Estimation/Dse/UnscentedKalmanStateEstimator.cs` | `UnscentedKalmanStateEstimator` | UKF |
| `src/GridSim.Core/Estimation/Dse/EnsembleKalmanStateEstimator.cs` | `EnsembleKalmanStateEstimator` | EnKF with perturbed observations |
| `src/GridSim.Core/Estimation/Dse/IProcessModel.cs`, `RandomWalkProcessModel.cs` | `IProcessModel`, `RandomWalkProcessModel` | Predict-step seam; persistence model |
| `src/GridSim.Core/Estimation/Dse/FrequencyRocofFilter.cs` | `FrequencyRocofFilter`, `FrequencyRocofEstimate` | Two-state frequency and RoCoF filter |
| `src/GridSim.Core/Estimation/DenseEstimationLimits.cs` | `DenseEstimationLimits` | The 2,000 and 2,500 caps |
| `src/GridSim.Core/Diagnostics/ForwardInferenceGuard.cs`, `RestrictionReview.cs` | `ForwardInferenceGuard`, `RestrictionReview` | Horizon, review table, output guard |
| `src/GridSim.Core/Distribution/MultiPhaseModel.cs` | `MultiPhaseModel`, `PhaseBus`, `PhaseBranch`, `PhaseLoad`, `PhaseCapacitor`, `PhaseImpedance`, `Phase`, `MultiPhaseElement` | Per-phase network model and element accounting |
| `src/GridSim.Core/Distribution/ThreePhaseSweep.cs` | `ThreePhaseSweep`, `MultiPhaseResult`, `ThreePhaseSweepOptions`, `IMultiPhaseSolver` | Backward/forward sweep |
| `src/GridSim.Core/Distribution/ThreePhaseWlsEstimator.cs` | `ThreePhaseWlsEstimator`, `ThreePhaseVMag`, `ThreePhaseCurrent`, `ThreePhaseWlsResult` | Node-voltage WLS and WLAV |
| `src/GridSim.Core/Distribution/BranchCurrentDsse.cs` | `BranchCurrentDsse`, `BranchCurrentEstimate` | Branch-current estimator |
| `src/GridSim.Core/Distribution/SequenceFaultStudy.cs` | `SequenceFaultStudy`, `SlgFaultResult` | Sequence-network fault currents |
| `src/GridSim.Core/Distribution/SymmetricalComponents.cs`, `TransformerConnectionModel.cs` | `SymmetricalComponents`, `TransformerConnectionModel` | Fortescue transform; sequence-domain transformer connection |
| `src/GridSim.Core/Distribution/DistributionPseudoMeasurements.cs` | `DistributionPseudoMeasurements` | Nominal-voltage pseudo-measurements |
| `src/GridSim.Core/Distribution/MultiPhaseJsonLoader.cs` | `MultiPhaseJsonLoader`, `MultiPhaseFeeder`, `MultiPhaseMeasurementSet` | `gridsim-dist3ph/1` and measurements file |
| `src/GridSim.Core/Distribution/MultiPhaseGridModelAdapter.cs` | `MultiPhaseGridModelAdapter` | Balanced lift of a positive-sequence case |
| `src/GridSim.Cli/Program.cs` | `RunServe`, `RunDist3ph` | The two verbs |
| `src/GridSim.Web/Realtime/` | `RealtimeEstimatorHostedService`, `ReplayDirectoryWindowFeed` | Web host of the static engine |
| `data/dist3ph/` | `ieee4-gyGy-stepdown-*.json`, `ieee123.json`, published CSVs | Bundled feeders and published reference values |

## Limits

- `serve` prints the last usable window's state only, one row per bus, with no flag to suppress it on a large case.
- `serve` reads a directory once, in file-name order, and exits; the Web service runs the static engine once at start-up
  and does not watch the directory. Neither runs the recursive filters in a resident loop.
- The only process model that runs is the random walk; no `Dynamics/` model is registered in the review gate.
- The EKF, UKF and EnKF are dense. They refuse a case above 2,500 states, and the UKF and EnKF a window above 2,500
  measurements; gb-full (7,077 states) is out of reach, and the WLS engine is the option at that size.
- `FrequencyRocofFilter` is experimental: it has no frequency feed, no CLI or replay surface, and its tuning is not
  validated against a recorded event.
- `RealtimeDseEngine` fails a window that arrives earlier than the previous processed window; there is no reordering
  buffer.
- The three-phase stack is radial: the sweep, the fault study and both estimators refuse a meshed network.
- The sweep has no distributed loads, no regulator control (taps are given, with no line-drop compensator or band), no
  switch impedance and no magnetising branch; the step-up open-wye / open-delta bank is refused. Only the sweep models
  the transformers other than `gy-gy`. The 13- and 34-node feeders are not in `docs/reference`.
- The fault study takes an infinite source at 1.0 pu and neglects every shunt element.
- `ThreePhaseWlsEstimator` forms and inverts a dense `6 x (non-source buses)` gain matrix every iteration and refuses
  a feeder above 416 non-source buses.
- The IEEE 123-node voltages differ from the published values by up to 5.2e-4 pu because the published run gives its
  switches an impedance the data does not state (node 149 is 1.0436 at -0.02 degrees behind RG1 at 1.0437 at 0.00, with
  only a switch between them) [VERIFY]. Most of the residual downstream of node 86 disappears if segment 86-87 takes
  configuration 5 instead of the data's 6; the data is used as published.
- IEC 61850 GOOSE and DNP3 are framing only.
- `dist3ph` is experimental.

## See also

- [21. State estimation](<../manual/0021 State estimation.md>) (running `serve`) and
  [Three-phase distribution feeders](<../manual/0007 Three-phase distribution feeders.md>) (running `dist3ph`).
- [`serve`](<../manual/0019 CLI reference.md#serve>), [`dist3ph`](<../manual/0019 CLI reference.md#dist3ph>) and
  [`estimate`](<../manual/0019 CLI reference.md#estimate>) in the CLI reference.
- [6. State estimation](<0006 State estimation.md>), which owns `h(x)`, WLS, robust methods and bad data.
- [19. Harmonics](<0019 Harmonics.md>), which owns the three-phase harmonic penetration model.
- [20. The RTU/SCADA interposer](<0020 The RTU and SCADA interposer.md>), the live path that writes the windows `serve`
  reads.
- [32. CIM/CGMES import and export](<0032 CIM and CGMES import and export.md>) and
  [33. C37.118 synchrophasor ingest](<0033 C37.118 synchrophasor ingest.md>).
- [8. Provenance and invariants](<0008 Provenance and invariants.md>), and the design records under
  [design/](design/three-phase-dsse.md).
