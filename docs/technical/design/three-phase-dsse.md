# Unbalanced three-phase distribution state estimation (DSSE)

> **Status (verified 1.95.091):** Partly implemented and experimental: the per-phase network model, three-phase backward/forward sweep, node-voltage three-phase WLS (with an optional WLAV reweighting loop), branch-current DSSE, sequence-network fault study, transformer connection model, pseudo-measurements and a strict JSON feeder loader exist, reachable from the `dist3ph` CLI verb. The four-wire neutral model, Carson line construction, three-phase Y-bus, converter-loss and virtual-reference-bus devices, the sparse-l1 `ExtendedWlav` estimator and the embedded IEEE 13/34/123 feeders are not implemented.
> **Implemented in:** `src/GridSim.Core/Distribution/MultiPhaseModel.cs` (`MultiPhaseModel`, `PhaseBus`, `PhaseBranch`, `PhaseLoad`, `PhaseImpedance`, `Phase`), `ThreePhaseSweep.cs` (`ThreePhaseSweep`, `IMultiPhaseSolver`, `MultiPhaseResult`, `ThreePhaseSweepOptions`), `ThreePhaseWlsEstimator.cs` (`ThreePhaseWlsEstimator`, `ThreePhaseVMag`, `ThreePhaseCurrent`, `ThreePhaseWlsResult`), `BranchCurrentDsse.cs` (`BranchCurrentDsse`, `BranchCurrentEstimate`), `SequenceFaultStudy.cs`, `SymmetricalComponents.cs`, `TransformerConnectionModel.cs`, `DistributionPseudoMeasurements.cs`, `MultiPhaseJsonLoader.cs` (`MultiPhaseFeeder`, `MultiPhaseMeasurementSet`).
> **Design-to-code name map:**
>
> | Design name | Implemented as |
> |---|---|
> | `Phase.cs` (`[Flags] enum Phase`, `PhaseSet`) | `Phase` enum in `MultiPhaseModel.cs`; no `PhaseSet`; the sweep treats every bus and branch as three-phase |
> | `PhaseImpedance.cs` with `FromCarson`, Kron reduction | `PhaseImpedance` in `MultiPhaseModel.cs` (3x3 matrix, `Diagonal`, `Invert`); no Carson or Kron code, line codes are loaded as matrices |
> | `MultiPhaseYBus.cs`, `PhaseSource`, four-wire neutral model | not implemented; `PhaseBus.IsSource` and `SourceV` carry the source |
> | `ConverterLossModel.cs`, `VirtualReferenceBus.cs` | not implemented |
> | `MultiPhaseResult.cs`, `IMultiPhaseSolver.cs` | both in `ThreePhaseSweep.cs` |
> | `ThreePhaseMeasurement.cs` (`PhaseMeasurement`, `PhaseMeasurementKind`) | `ThreePhaseVMag` and `ThreePhaseCurrent` in `ThreePhaseWlsEstimator.cs`; file form is `MultiPhaseMeasurementSet` |
> | `ThreePhaseMeasurementModel.cs`, `IThreePhaseStep.cs` | not implemented as types; the model is inside `ThreePhaseWlsEstimator.Estimate` |
> | `ExtendedWlav.cs` (sparse-l1 LP) | not implemented; `ThreePhaseWlsEstimator.Estimate(..., robust: true)` runs an IRLS WLAV loop |
> | `DsseEstimationResult.cs` | `ThreePhaseWlsResult` and `BranchCurrentEstimate` (no shared type) |
> | `FaultAnalysis.cs` additive `SingleLineToGround`, `LineLine`, `LineLineGround` | not added; `FaultAnalysis` keeps `FaultType.SingleLineToGround` reported as not representable; the unbalanced faults are `SequenceFaultStudy` in `Distribution/` (not `Protection/`) |
> | `PowerFlow/BackwardForwardSweep.cs` extension | `ThreePhaseSweep` is a separate class; `PowerFlow/BackwardForwardSweep.cs` is unchanged |
> | `data/distribution/ieee13.json`, `ieee34.json`, `ieee123.json` | not present; `data/dist3ph/` holds the IEEE 4-node feeder cases only |
>
> This is a design record. The current behaviour is documented in the technical manual chapter [18. Real-time, distribution and dynamic estimation](<../0018 Real-time, distribution and dynamic estimation.md>) (section 18.5).

Implementation-ready design for the unbalanced, per-phase distribution
state-estimation subsystem: the four-wire phase-impedance network model, a
three-phase backward/forward-sweep power flow, two distribution estimation
paths (node-voltage WLS and branch-current DSSE), an Extended-WLAV robust
estimator, sequence-network fault support, and the pseudo-measurement layer that
makes an under-metered feeder observable.

This is a design an implementer follows; it contains no C#. Equations are exact;
data structures are named to fit the existing code. The whole subsystem is a
**separate model path** that leaves the positive-sequence engine
(`GridModel`, `YBus`, `NewtonRaphsonPowerFlow`, `WlsStateEstimator`) untouched.

Audience: an engineer extending `src/GridSim.Core`. Read
`UnderstandingRestrictions.md` and `RestrictionReview.Reviewed` (`src/GridSim.Core/Diagnostics/RestrictionReview.cs`) first - the
no-forward-inference rule governs every seam here.

---

## 1. Purpose and scope

Transmission state estimation (GridSim's `WlsStateEstimator`) assumes a
balanced, transposed, positive-sequence network: one complex voltage per bus,
one pi-model 2x2 stamp per branch. Distribution feeders break every one of those
assumptions:

- **Unbalanced.** Phase loads differ; single- and two-phase laterals exist; the
  three phase voltages are genuinely distinct, not one phasor times `1<0/-120/+120`.
- **Untransposed.** Overhead lines and cables carry real mutual coupling between
  phases (and to the neutral/earth). The series impedance is a full 3x3 (or 4x4)
  matrix, not a scalar.
- **High R/X.** Newton-Raphson conditions badly; the sweep methods are standard.
- **Radically under-metered.** A feeder may have telemetry only at the primary
  substation, plus sparse AMI. Observability comes from pseudo-measurements.
- **Increasingly inverter-dominated.** DER changes injections and, in islanded
  operation, removes the transmission slack that anchors the reference.

This subsystem adds:

1. A full per-phase **3x3 phase-impedance** network model (self/mutual,
   untransposed, missing phases) with **four-wire neutral-inclusive** (LV) form.
2. **Sequence networks** (zero / positive / negative) derived from the phase
   model, enabling `Protection/FaultAnalysis.cs` to represent **single-line-to-
   ground** (and line-line, line-line-ground) faults it currently declines.
3. A **three-phase backward/forward-sweep** power flow extending
   `PowerFlow/BackwardForwardSweep.cs`.
4. A three-phase **node-voltage WLS** estimator and a distinct **branch-current
   DSSE** estimator that cross-validate each other.
5. A **balanced virtual-bus** reference for islanded / high-DER unbalanced grids.
6. **Converter-loss** modelling for DER/inverter injections.
7. An **Extended-WLAV** (sparse-l1) robust estimator.
8. **Pseudo-measurement generation** (load allocation + AMI/profile priors).
9. Validation to **machine epsilon** against an independent in-house reference,
   cross-checked to **OpenDSS** and **pandapower-3ph** on the IEEE 13/34/123
   unbalanced test feeders.

Out of scope here: harmonics, electromagnetic transients, time-series/quasi-
static simulation, and any recursive dynamic estimator (those are the gated
`Dynamics/`->`Estimation/` seam, not this).

---

## 2. Restriction posture: DSSE is a filter, not a predictor

Every estimator specified here is a **static, measurement-only filter**: given
`z = h(x) + e` over a fixed network, resolve the present/past state `x`. None
integrates a process model forward. The review gate (`RestrictionReview`) already
permits these static estimators:

> Static, measurement-only estimators (WLS, Huber, LAV, constrained, feasible,
> QR, linear-PMU, **branch-current, EWLAV**) are filters, not predictors - they
> need no entry and are unconditionally permitted.

So the two new estimation methods named in this design - **branch-current** and
**EWLAV** - are *already* on the permitted list. **No new `RestrictionReview`
entry is required** for anything in this document, because nothing here consumes
a `Dynamics/` process model. If a future revision fuses a load-dynamics process
model into a recursive three-phase filter, that - and only that - trips
invariant #5 and must be reviewed and logged first.

The rules that DO bind this subsystem:

- **Invariant #1 (the 169h floor).** Every new dated input seam calls
  `ForwardInferenceGuard.RejectFuture` before touching data:
  `MultiPhaseMeasurementSet.Load`, and any AMI / smart-meter / load-profile
  ingester feeding the pseudo-measurement layer. A three-phase measurement set
  is a dated ingester exactly like `MeasurementSet` (Section 16.4).
- **Invariant #2 (no future-dated output).** The estimators stamp their result
  at the measurement window time. There is no code path that emits a state
  dated later than its newest input.
- **Solving vs forward inference.** The three-phase power flow (Section 10) is
  *solving* - a statement about the model. Seeded from guard-permitted GB data
  it is reconstruction; it never nowcasts the real feeder's unpublished present
  or future. Pseudo-measurements (Section 16) are **historical/allocation
  priors, not forecasts**: they derive from load-research profiles, nameplate
  allocation, or AMI reads that are themselves >=169h old or replayed. The
  generator never reads the wall clock (deterministic `TakenUtc`), mirroring
  `SyntheticMeasurements`.
- **Balanced virtual bus** (Section 11) is a numerical reference device inside
  the model; it invents no observation of reality.

A dedicated `ThreePhaseRestrictionInvariantTests` fixture asserts the guard
fires on a fresh three-phase set and on a fresh AMI pseudo-source, next to the
machine-epsilon checks.

---

## 3. Design principles and non-regression of the positive-sequence path

1. **Separate model path.** The unbalanced world lives in a new namespace
   `GridSim.Core.Distribution` under `src/GridSim.Core/Distribution/`. The
   positive-sequence types (`GridModel`, `Bus`, `Branch`, `YBus`,
   `NewtonRaphsonPowerFlow`, `BackwardForwardSweep`, `WlsStateEstimator`,
   `MeasurementModel`) are **not modified** except for two purely additive
   things: (a) the `Protection/FaultAnalysis.cs` gains a new `SingleLineToGround`
   entry point (the existing `ThreePhase` method is byte-for-byte unchanged), and
   (b) `Generator`/`Branch` gain optional, defaulted sequence/grounding fields
   that every existing case leaves at their defaults (Section 9.4). No existing
   test can change behaviour.

2. **Reuse the conventions, not the code.** The three-phase types mirror the
   existing style - `sealed record` data, `Complex` from `System.Numerics`,
   `internal` implementation behind public seams, `PowerFlowResult`-shaped
   results, deterministic date-literal seeds - so a reader of the transmission
   code is immediately at home. Where the physics forces a different choice
   (rectangular state, 3x3 stamps) the divergence is called out.

3. **Bridges both ways, tested to machine epsilon.**
   - `GridModel.ToBalancedThreePhase()` lifts a positive-sequence case into a
     perfectly balanced `MultiPhaseModel` (phase impedance = diag with the
     positive-sequence value, zero mutuals). Running the three-phase BFS on it
     and collapsing back must reproduce the positive-sequence `BackwardForward
     Sweep`/Newton result **to machine epsilon**. This is the master non-
     regression and correctness gate (Section 18.3).
   - `MultiPhaseModel.ToPositiveSequence()` projects an unbalanced model onto its
     positive-sequence single-line equivalent (average self minus average mutual
     -> `z1`), so all existing positive-sequence tooling (contingency, boundary,
     export) still runs on distribution cases at reduced fidelity.

4. **Fail loud, never silently degrade.** Same doctrine as the rest of the
   engine: an unsupported connection, a singular grounding configuration, a
   feeder too meshed for the sweep, or a non-finite result is reported as a
   typed failure status, never as a NaN dressed up as converged.

---

## 4. File and namespace layout

All new code under `src/GridSim.Core/Distribution/` in namespace
`GridSim.Core.Distribution`, except the fault additions in
`GridSim.Core.Protection`.

```
src/GridSim.Core/Distribution/
  Phase.cs                     [Flags] enum Phase; PhaseSet helpers; phase masks
  PhaseImpedance.cs            NxN complex matrix value type; Carson; Kron reduction
  SymmetricalComponents.cs     A / A^-1 transform; z_abc <-> z_012
  MultiPhaseModel.cs           MultiPhaseModel, PhaseBus, PhaseBranch, PhaseLoad,
                               PhaseSource, WindingConnection, LoadConnection enums
  ConverterLossModel.cs        quadratic converter-loss record + evaluation
  VirtualReferenceBus.cs       balanced virtual-bus reference device
  MultiPhaseYBus.cs            3n x 3n (or 4-wire) sparse admittance; sequence Ybus
  MultiPhaseResult.cs          MultiPhaseResult (per-phase Vm/Va, flows, losses, status)
  IMultiPhaseSolver.cs         solver seam (parallel to IPowerFlowSolver)
  ThreePhaseSweep.cs           three-phase BFS (extends the BackwardForwardSweep idea)
  ThreePhaseMeasurement.cs     PhaseMeasurement, PhaseMeasurementKind, PhaseMeasurementSet
  ThreePhaseMeasurementModel.cs  rectangular-state h(x) and Jacobian
  IThreePhaseStep.cs           estimation-step seam (parallel to IWlsStep)
  ThreePhaseWlsEstimator.cs    node-voltage 3-phase WLS + loop
  BranchCurrentDsse.cs         distinct branch-current formulation + estimator
  ExtendedWlav.cs              sparse-l1 (LP) robust estimator
  DistributionPseudoMeasurements.cs  load-allocation + AMI/profile priors
  DsseEstimationResult.cs      result type shared by the three estimators

src/GridSim.Core/Protection/
  FaultAnalysis.cs             + SingleLineToGround / LineLine / LineLineGround (additive)
  SequenceFaultStudy.cs        sequence-network Thevenin build (new file)

data/distribution/            embedded IEEE feeders (Section 18.1)
  ieee13.json  ieee34.json  ieee123.json
```

Public surface: `MultiPhaseModel`, `PhaseBus/Branch/Load/Source`, `Phase`,
`ThreePhaseSweep`, `ThreePhaseWlsEstimator`, `BranchCurrentDsse`,
`ExtendedWlav`, `DsseEstimationResult`, `MultiPhaseResult`,
`DistributionPseudoMeasurements`, the fault entry points. Implementation
(step backends, LP kernels, Carson helpers) stays `internal` with
`InternalsVisibleTo("GridSim.Tests")` (already granted in the csproj).

---

## 5. Phasing and the primitive/phase-impedance model

### 5.1 Phase identity and masks

```
[Flags] enum Phase { None = 0, A = 1, B = 2, C = 4, N = 8 }
```

A `PhaseBus`, `PhaseBranch`, `PhaseLoad` and `PhaseSource` each carry a `Phase
Present` mask. A single-phase lateral is `A`; a two-phase is `A | C`; a full
three-phase is `A | B | C`; the four-wire LV form adds `N`. All per-phase arrays
and matrices are indexed by the ordered present phases (a stable ascending order
A,B,C,N). `PhaseSet` provides `Count`, `IndexOf(Phase)`, and the ordered
enumeration so a 2x2 submatrix on `{A,C}` is handled uniformly with a 3x3 on
`{A,B,C}`.

**Missing phases** are first-class: a branch's series matrix is sized to
`Present.Count`, and a bus's state carries only its present phases. A load or
source on a phase the bus does not present is a validation Error.

### 5.2 The primitive impedance matrix (modified Carson)

For an overhead segment with conductors indexed by `{a,b,c,n,...}` the primitive
series impedance (ohms per unit length, per Kersting's modified Carson's
equations at line frequency `f`, earth resistivity `rho`) is:

```
Self  (conductor i):
  z_ii = (r_i + r_earth) + j * x_factor * ( ln(1/GMR_i) + earth_term )
Mutual (i != j):
  z_ij =         r_earth  + j * x_factor * ( ln(1/D_ij)  + earth_term )

with, at 50 Hz (SI, ohms/km) :
  r_earth   = pi^2 * f * 1e-4                      (earth return resistance)
  x_factor  = 4 * pi * f * 1e-4                    (= omega * mu0 / (2 pi))
  earth_term= 0.5 * ln( rho / f ) + C_earth        (Carson earth-return const)
```

`GMR_i` is the conductor geometric mean radius; `D_ij` the centre-to-centre
distance from geometry. `PhaseImpedance.FromCarson(conductors, geometry, f, rho)`
computes the primitive `z_prim` (an `N x N` complex matrix) and, from the
potential-coefficient matrix `P` (same geometry, image charges), the primitive
shunt `y_prim = j * omega * C_prim` with `C_prim = 2*pi*eps0 * P^-1`.

Because published feeders (IEEE 13/34/123, OpenDSS `LineGeometry`) most often
provide the **already-reduced** phase matrices directly, the primary authoring
path is to supply `z_abc` and `y_abc` per segment as data; `FromCarson` is the
constructive path for cases given as geometry + conductor tables. Both must
agree to machine epsilon on a shared fixture (Section 18.5).

### 5.3 Kron reduction of the neutral

The four-wire primitive partitions into phase block `p` and neutral block `n`:

```
        [ z_pp  z_pn ]
z_prim= [ z_np  z_nn ]

Kron reduction (grounded neutral folded to earth):
  z_abc = z_pp - z_pn * z_nn^-1 * z_np        (3x3 phase impedance)
```

`PhaseImpedance.KronReduce(neutralPhases)` performs the block solve (dense
complex LU on the small `z_nn`). The **3-wire model** uses `z_abc`; the **4-wire
model** keeps `z_prim` un-reduced (Section 6). A `PhaseBranch` records which form
it is in (`NeutralModel = Reduced | Explicit`).

### 5.4 Per-unit conversion

Distribution data is in physical units (ohms, siemens, kV, kVA). The model
carries a per-segment length and converts to per-unit on the segment's voltage
base and the system `BaseMva`, exactly analogous to the transmission per-unit
convention (technical ch. 2). Transformer segments define a voltage-base change;
the per-unit `z_abc` is referred to the segment's own base. A
`MultiPhaseModel.Validate()` pass checks base continuity across each transformer.

---

## 6. Four-wire neutral-inclusive admittance (LV-LSE) with neutral coupling

For LV feeders (and MV cases where neutral/earth current matters) the neutral is
kept **explicit** rather than Kron-folded. This is the "LV low-voltage
state-estimation" (LV-LSE) form: the state carries the neutral conductor
voltage, and neutral-return / earthing currents are observable.

- A four-wire `PhaseBranch` has `Present = A|B|C|N`, a `4x4` series `z_prim`, a
  `4x4` shunt `y_prim`, and `NeutralModel = Explicit`.
- The neutral bus voltage is a genuine state at each four-wire node (it is NOT
  assumed zero); multi-grounded neutrals are modelled by a shunt earthing
  admittance `y_g` from the neutral node to the ideal ground (the reference for
  all phase voltages). `PhaseBus.NeutralGroundingSiemens` gives `y_g` (0 =
  ungrounded, infinite = solidly bonded to the reference).
- **Neutral coupling** enters through the off-diagonal `z_prim[a..c, n]` /
  `y_prim[..., n]` terms exactly as for any other conductor; nothing special is
  needed in the sweep or the Y-bus once N is treated as a fourth phase index.

The 4-wire Y-bus is `4-present x 4-present` per node. Grounding admittances stamp
the neutral diagonal. Kron reduction (Section 5.3) is exactly the operation that
maps a solved four-wire result onto the three-wire equivalent, so the two forms
are cross-checked: solving the explicit four-wire model and Kron-reducing the
voltages must match the reduced-model solve to machine epsilon on a common
grounded case (Section 18.5).

The estimator treats the neutral phase identically to A/B/C: a four-wire node
contributes `2 * Present.Count` rectangular state components. Voltage/current
measurements can address the neutral (`Phase.N`) - a neutral-current meter is a
strong LV-LSE observation of unbalance.

---

## 7. The multi-phase network model

```
MultiPhaseModel (sealed class, parallels GridModel):
  double BaseMva
  double FrequencyHz          (default 50)
  IReadOnlyList<PhaseBus>     Buses
  IReadOnlyList<PhaseBranch>  Branches
  IReadOnlyList<PhaseLoad>    Loads
  IReadOnlyList<PhaseSource>  Sources         (slack / DER / grid-forming)
  VirtualReferenceBus?        VirtualReference (Section 11; null on grid-tied)
  string Name
  IReadOnlyList<GridDiagnostic> Validate()    (same diagnostic type as GridModel)
```

```
PhaseBus (sealed record):
  int Id
  Phase Present
  double BaseKv                    (line-to-line nominal)
  Complex NeutralGroundingSiemens  (per-unit; 4-wire only)
  double[] VmInitPu, VaInitDeg     (per present phase; flat-start default)

PhaseBranch (sealed record):
  int From, To
  Phase Present
  NeutralModelKind NeutralModel    (Reduced | Explicit)
  PhaseImpedance Zseries           (present x present, per-unit)
  PhaseImpedance Yshunt            (present x present, per-unit; split half/half)
  BranchKind Kind                  (Line | Cable | Transformer | Switch | Regulator)
  WindingConnection FromConn, ToConn   (transformer only; else Wye by default)
  double TapRatio                  (regulators/transformers; per phase optional)
  double[]? PhaseTapRatio          (independent per-phase regulator taps)
  string? Name
  bool InService

PhaseLoad (sealed record):
  int Bus
  LoadConnection Connection        (Wye | Delta)
  Phase Phases
  LoadModel Model                  (ConstantPower | ConstantCurrent | ConstantImpedance | ZIP)
  double[] Pkw, Qkvar              (per phase, or per phase-pair for Delta)
  ZipCoefficients? Zip

PhaseSource (sealed record):
  int Bus
  SourceRole Role                  (Slack | GridForming | GridFollowingPV | GridFollowingPQ)
  Phase Phases
  double[] Vsetpu, Vsetdeg         (per phase; Slack/GridForming)
  double[] Pkw, Qkvar              (GridFollowing)
  ConverterLossModel? Converter    (Section 12)
  Complex SequenceZ1, SequenceZ2, SequenceZ0   (machine/inverter sequence
                                                 impedances for fault studies)
  Complex NeutralGroundingZPu      (source neutral earthing; 0 = solidly grounded)
```

`WindingConnection { Wye, WyeGrounded, Delta }` and `LoadConnection { Wye,
Delta }` capture the connection algebra that drives both the sweep transformer
matrices (Section 10.4) and the zero-sequence network (Section 8.3).

`Validate()` reuses `ValidationSeverity`/`GridDiagnostic` and reports: a load or
source on an absent phase (Error), no slack and no virtual reference (Error), a
non-finite matrix entry (Error), a transformer base discontinuity (Error), an
unsupported connection pair (Error), a delta load on a bus missing a required
phase (Error), and an ungrounded four-wire island (Warning - it will float).

---

## 8. Multi-phase Y-bus and the sequence networks

### 8.1 Phase-domain Y-bus

`MultiPhaseYBus.BuildSparse(model)` produces a sparse per-phase admittance keyed
by `(busIndex, phase)` compound indices, mirroring `YBus.BuildSparse`. Each
in-service branch stamps its 2x2 block-of-blocks:

```
For a line/cable segment with series admittance Ys = Zseries^-1 (present x present)
and half shunt Bsh = 0.5 * Yshunt:
  Y[f,f] += Ys + Bsh
  Y[t,t] += Ys + Bsh
  Y[f,t] += -Ys
  Y[t,f] += -Ys
```

where each entry is itself a `present x present` complex matrix. For missing
phases the submatrices are sized to the branch's present set and scattered into
the matching phase slots of the two buses. Transformers stamp their connection-
dependent nodal blocks (Section 10.4 gives the primitive per-connection
admittance `Yprim` used identically here). Bus shunts and neutral-grounding
admittances land on the diagonal. Determinism: stamps are visited in model order
with a fixed accumulation order so each cell is bit-reproducible (same doctrine
as `YBus.BuildSparse`).

### 8.2 Symmetrical-component transform

```
a = exp(j*2*pi/3)
      [ 1   1    1  ]              1     [ 1   1    1  ]
A =   [ 1   a^2  a  ]     A^-1 = ----- * [ 1   a    a^2]
      [ 1   a    a^2]              3     [ 1   a^2  a  ]

Sequence impedance of a 3x3 phase matrix:
  z_012 = A^-1 * z_abc * A
```

`SymmetricalComponents` provides `A`, `Ainv`, `ToSequence(z_abc)` and
`ToPhase(z_012)`. For a **transposed** line `z_abc` has equal diagonals and equal
off-diagonals, so `z_012 = diag(z0, z1, z2)` exactly with

```
z0 = z_self + 2*z_mutual
z1 = z2 = z_self - z_mutual
```

For an **untransposed** line `z_012` is full (inter-sequence coupling); the
sequence networks are then coupled. The fault study (Section 9) uses the exact
`z_012` blocks; the common decoupled approximation (diagonal only) is available
behind a flag for comparison to hand calculations, and the design records that
the exact coupled form is the default so untransposed feeders are not silently
approximated.

### 8.3 Sequence Y-bus / Z-bus

`SequenceFaultStudy.Build(model)` assembles three nodal networks:

- **Positive (1) and negative (2).** For passive plant `Y1 = Y2`; they are the
  balanced single-phase network already produced by the positive-sequence path,
  reusable directly. Sources contribute `1/Z1` and `1/Z2` shunts (machine or
  inverter sequence impedance from `PhaseSource`).
- **Zero (0).** Assembled from the zero-sequence line impedance `z0` and, per
  transformer, the connection-dependent zero-sequence coupling:
  - `WyeGrounded - WyeGrounded`: passes zero-sequence through (series `z0`).
  - `WyeGrounded - Delta`: zero-sequence sees the grounded-wye winding to ground
    but is blocked to the delta side (shunt on the grounded side, open across).
  - `Delta - Delta` / `Wye(ungrounded)`: blocks zero-sequence (open).
  - A grounded source neutral adds `3 * Zn` in the zero-sequence path
    (`3 * NeutralGroundingZPu`).

The zero-sequence network topology is therefore genuinely different from the
positive-sequence one - which is exactly why single-line-to-ground currents
cannot be obtained from the positive-sequence model alone, and why the current
`FaultAnalysis` reports SLG as not-representable.

---

## 9. Single-line-to-ground faults via sequence networks

This is the additive extension to `Protection/FaultAnalysis.cs` that lets the
protection overlay operate on earth faults.

### 9.1 Thevenin sequence impedances at the fault bus

For fault bus `k`, solve one column of each sequence Z-bus (one complex linear
solve per sequence, reusing the dense `SolveComplex` already in
`FaultAnalysis.cs`):

```
Z1_kk = e_k^T * Y1^-1 * e_k
Z2_kk = e_k^T * Y2^-1 * e_k
Z0_kk = e_k^T * Y0^-1 * e_k
```

with prefault (positive-sequence) voltage `E_k = V0_k` at the fault bus taken
from the prefault solved state (the same prefault-voltage source the existing
`ThreePhase` uses).

### 9.2 Fault-type connection of the sequence networks

```
Single-line-to-ground (phase a), fault impedance Zf:
  I_a0 = I_a1 = I_a2 = E_k / (Z0_kk + Z1_kk + Z2_kk + 3*Zf)
  Phase currents:  I_abc = A * [I_a0; I_a1; I_a2]
  (=> I_b = I_c = 0 for a pure a-g fault, the physical signature)

Line-to-line (b-c), impedance Zf:
  I_a1 = -I_a2 = E_k / (Z1_kk + Z2_kk + Zf) ;  I_a0 = 0

Double line-to-ground (b-c-g):
  standard parallel combination of Z2 and (Z0 + 3*Zf) fed from Z1.
```

Faulted sequence voltages `V_012(i) = V0_012(i) - Z_seq[:,k] * I_seq` per network,
then per-bus phase voltages `V_abc = A * V_012`; faulted phase branch currents
follow from the phase-domain stamps (Section 8.1) at the faulted voltages,
driving the protection elements exactly as `ThreePhase` does today.

### 9.3 Result and API

```
FaultAnalysis.SingleLineToGround(MultiPhaseModel model, MultiPhaseResult prefault,
    int faultBusId, Phase faultedPhase = Phase.A, double faultReactancePu = 0.0)
    -> MultiPhaseFaultResult
FaultAnalysis.LineLine(...)         FaultAnalysis.LineLineGround(...)
```

`MultiPhaseFaultResult` carries the faulted `MultiPhaseResult`, the per-phase
fault current (pu and kA when `BaseKv` known), and the sequence currents for
provenance. The existing balanced `ThreePhase(GridModel, ...)` overload is
untouched; a new `ThreePhase(MultiPhaseModel, ...)` overload is the three-phase
sibling (three-phase-to-ground = the balanced limit, a consistency check:
sequence-network SLG with all three phases faulted must equal the direct
phase-domain three-phase fault to machine epsilon).

### 9.4 Additive model fields

`Generator` and `Branch` in `Model/Model.cs` gain optional, defaulted fields so a
positive-sequence case can still be fault-studied for SLG by supplying sequence
data without a full phase model:

- `Generator`: `SequenceZ2Pu`, `SequenceZ0Pu`, `NeutralGroundingZPu` (defaults:
  `Z2 = Z1` synthesised, `Z0` open, `Zn = 0`).
- `Branch`: `WindingConnection FromConn, ToConn` (default `WyeGrounded`),
  `SequenceZ0Pu` (default: `3*Z1` overhead rule-of-thumb, provenance-noted).

Every existing case leaves these at defaults, so no positive-sequence test moves.

---

## 10. Three-phase backward/forward-sweep power flow

`ThreePhaseSweep : IMultiPhaseSolver` extends the algorithm in
`PowerFlow/BackwardForwardSweep.cs` from scalars to `present x present` complex
matrices/vectors. It is the reference truth for the estimators and the seed for
warm starts.

### 10.1 State and structure

Voltages and currents become per-bus complex phase vectors `V[bus] in C^{present}`,
`I[bus] in C^{present}`. Series impedances are the `PhaseImpedance` matrices. The
spanning-tree / breakpoint machinery, PV-bus reactive correction, and weak-mesh
compensation from the scalar sweep carry over unchanged in structure; only the
per-edge algebra becomes matricial. The `MaxCompensatedLoops` gate and the
"disconnected / too meshed -> non-radial report" fallbacks are preserved.

### 10.2 Backward sweep (leaves -> source)

For each bus in reverse tree order, accumulate the nodal injection current (load
+ shunt + DER) plus the accumulated child-branch currents, transformed across
each child branch's connection matrix:

```
I_series[k] = sum over children c ( T_i(conn_k) * I_series[c] ) + I_node[bus]
```

`I_node[bus]` is the per-phase current the bus draws (Section 10.5). `T_i` is the
branch current-transformation matrix (identity for a plain line; connection-
dependent for transformers/regulators, Section 10.4).

### 10.3 Forward sweep (source -> leaves)

```
V[child] = T_v(conn_k) * V[parent] - Zseries_k * I_series[k]
```

`T_v` is the branch voltage-transformation matrix (identity for a plain line;
the ideal-transformer ratio/connection for transformers). Convergence is on
`max over (bus,phase) |V - V_prev| < Tolerance`, matching the scalar sweep's
`maxDelta` contract, with the same `NonFinite` guard and status doctrine.

### 10.4 Transformer / regulator connection matrices

Each supported connection contributes a 6x6 primitive `[Yprim]` (equivalently
the `(T_v, T_i, Zseries)` triple) per Kersting's generalized matrices
`[a],[b],[c],[d],[A],[B]`:

```
V_abc(from) = a * V_abc(to) + b * I_abc(to)
I_abc(from) = c * V_abc(to) + d * I_abc(to)
V_abc(to)   = A * V_abc(from) - B * I_abc(to)
```

Connections supported at v1: `WyeGrounded-WyeGrounded`, `Delta-WyeGrounded`
(the ubiquitous distribution step-down, with its 30-degree/`1/sqrt3` shift),
`WyeGrounded-Delta`, `Delta-Delta`, and the step/voltage regulator (per-phase
tap on a wye or closed-delta). Each connection's `[a..d,A,B]` matrices are
tabulated constants times the per-unit turns ratio; the design lists them
explicitly in the implementation notes and each is unit-tested against a
single-transformer hand calculation.

### 10.5 Load and source current models

```
Wye constant-power (phase p):   I_p = conj( S_p / V_p )
Wye constant-impedance:         I_p = V_p / Z_p          (Z_p from nameplate S,V)
Wye constant-current:           I_p = |I_p0| at fixed pf, angle tracks V_p
Delta constant-power (pair pq): I_pq = conj( S_pq / (V_p - V_q) ),
                                nodal current = I_pq - I_(previous pair into p)
ZIP: convex combination of the three above by (Zip.z, Zip.i, Zip.p) with z+i+p=1
```

DER `GridFollowingPQ` sources inject `-I` of the constant-power form at their
setpoint (converter loss applied, Section 12); `GridFollowingPV` sources hold
per-phase (or positive-sequence) voltage magnitude via the reactive correction
already in the scalar sweep, generalized per phase.

### 10.6 Result

`MultiPhaseResult` mirrors `PowerFlowResult`: per-`(bus,phase)` `Vm`/`VaDeg`,
per-phase net injections, per-branch per-phase flows and losses, total loss, and
a `MultiPhaseStatus { Converged, MaxIterations, NonFinite, NonRadial }`. A
`ToPositiveSequence()` collapses it to a `PowerFlowResult` for the existing UI /
export plumbing (positive-sequence component of the solved phase voltages).

---

## 11. Balanced virtual-bus reference for islanded / high-DER grids

A grid-tied feeder inherits its angle/voltage reference from the transmission
slack (the primary substation `PhaseSource` with `Role = Slack`). An **islanded**
microgrid (grid-forming inverters, no transmission connection) has no such
anchor, and its unbalance means there is no single "correct" reference phasor.

`VirtualReferenceBus` provides a **balanced virtual bus**: an internal,
perfectly-balanced positive-sequence source `E * [1, a^2, a]` behind a small
virtual series impedance `Zv`, attached to a nominated physical bus (usually the
dominant grid-forming unit's terminal). It:

1. **Fixes the angle datum.** The virtual bus's phase-a angle is 0 by
   definition, so the sweep and the estimator have a well-defined reference even
   when every physical source is grid-forming and imbalanced.
2. **Absorbs residual imbalance** during the solve (it is the slack of the
   islanded network), while `Zv` keeps its influence local and physically
   interpretable rather than clamping the nearby physical bus to perfect balance.
3. **Grounds the sequence decomposition.** Because it is balanced, `V_012` at the
   virtual bus is pure positive-sequence, giving the fault study a clean
   reference and the estimator a stable positive-sequence datum.

Design points:
- `Zv` is a design parameter (default: a few percent on the unit's base) chosen
  large enough not to distort the local unbalance yet small enough to anchor the
  solve; the sensitivity is documented and tested.
- In the estimator, the virtual bus's three voltages are **fixed reference
  states** (removed from the free state, exactly as slack angles are removed in
  the transmission estimator), so it adds a datum, not free unknowns.
- It is a **model device, not an observation**: it asserts nothing about the real
  grid and therefore raises no restriction concern. It appears in provenance as a
  synthetic reference so no consumer mistakes it for a metered bus.

Selection rule: if `Sources` contains a `Slack`, the virtual bus is off by
default; if the model is islanded (no `Slack`), a `VirtualReferenceBus` is
required or `Validate()` reports "no reference (Error)".

---

## 12. Converter-loss modelling

Inverter-interfaced DER (and any power-electronic converter) dissipates a
current-dependent loss that a naive constant-power injection ignores, biasing
both the power flow and the estimated DER output. Model it as the standard
quadratic in converter current magnitude:

```
P_loss = a0 + a1 * |I_conv| + a2 * |I_conv|^2
```

```
ConverterLossModel (sealed record):
  double A0Kw     (no-load / switching loss)
  double A1       (conduction, linear, kW per pu-amp)
  double A2       (resistive, quadratic, kW per pu-amp^2)
  double Evaluate(double iConvMagPu)     -> P_loss (kW)
```

Placement:
- **Power flow.** In the source current model (Section 10.5), the AC-side real
  injection is reduced by `P_loss` (the loss appears on the DC/prime-mover side);
  `|I_conv|` is the AC current the sweep already computes, so the correction is a
  fixed-point term evaluated each sweep - it converges with the outer iteration.
- **Estimation.** When a measurement observes the DC-side or nameplate power and
  the state carries AC-side voltage/current, the loss enters `h(x)` as the same
  quadratic; its Jacobian contribution is `dP_loss/d|I| = a1 + 2*a2*|I|` chained
  through `d|I|/dx`. This keeps an estimated DER injection physically consistent
  with its metered AC current instead of over-crediting output by the loss.

The loss model is optional per source (null = ideal converter); coefficients are
authored from datasheet efficiency curves and provenance-noted. A converter-loss
consistency test checks that the power-flow AC/DC balance closes to machine
epsilon for a chosen `(a0,a1,a2)`.

---

## 13. The three-phase node-voltage WLS estimator

`ThreePhaseWlsEstimator` is the direct three-phase analogue of
`WlsStateEstimator`, and its structure deliberately mirrors it (loop owns start
point, damping, convergence, chi-square, bad-data removal; representation lives
behind `IThreePhaseStep`). Two deliberate divergences from the transmission
estimator, both standard DSSE practice:

### 13.1 Rectangular state

The state is **rectangular** voltage per present phase:

```
x = [ Vre(bus,phase), Vim(bus,phase) ]  over all buses and present phases,
    minus the reference bus/virtual-bus phases (fixed datum)
```

Rectangular coordinates are chosen because (a) current and power measurement
Jacobians are lower-order and better-conditioned on stiff, high-R/X feeders, (b)
there is no per-phase angle-wrap or reference-degeneracy pathology, and (c) the
branch-current path (Section 14) shares the rectangular convention so the two
estimators compare directly. The divergence from the transmission estimator's
polar `(Va, Vm)` state is intentional and documented; the balanced-case bridge
(Section 18.3) reconciles the two to machine epsilon.

The reference datum (slack `PhaseSource` phases, or the `VirtualReferenceBus`
phases) is fixed and removed from the free state, exactly as slack angles are
removed in the transmission `MeasurementModel`.

### 13.2 Measurement kinds and h(x)

```
PhaseMeasurementKind:
  VmPu        (per bus,phase)            h = sqrt(Vre^2 + Vim^2)
  VaRad       (per bus,phase; PMU)       h = atan2(Vim, Vre) - ref
  VreVim      (PMU rectangular pair)     h = Vre ; h = Vim        (linear)
  PInjKw      (per bus,phase)            h = Re( V_p * conj( sum_k Y_pk V_k ) )
  QInjKvar    (per bus,phase)            h = Im( V_p * conj( sum_k Y_pk V_k ) )
  PFlowKw     (per branch end,phase)     h = Re( V_p * conj( I_flow_p ) )
  QFlowKvar   (per branch end,phase)     h = Im( V_p * conj( I_flow_p ) )
  IMagPu      (per branch end,phase)     h = |I_flow_p|
  IReIm       (PMU branch current pair)  h = Re/Im( I_flow_p )    (linear)
  IneutralPu  (four-wire neutral current)                        (LV-LSE)
```

`I_flow` uses the phase-domain branch stamps from Section 8.1. Injection sums run
over the bus's electrical neighbourhood only (the same O(nnz) sparse-injection
discipline as `MeasurementModel.ComputeInjectionsSparse`), and there is a single
Jacobian evaluation path writing into a dense or sparse sink, mirroring
`EvaluateJacobian<TSink>` so the dense and sparse backends cannot drift.

### 13.3 Jacobians (rectangular)

With `E_p = Vre_p + j Vim_p` and nodal current `Ii_p = sum_k Y_pk E_k` (each `Y_pk`
a scalar phase-to-phase admittance from the block matrix), the exact partials
are the standard rectangular DSSE forms, e.g. for the linear/near-linear kinds:

```
VreVim:   dVre/dVre = 1, all else 0                        (constant, linear)
VmPu:     d|V|/dVre = Vre/|V| ,  d|V|/dVim = Vim/|V|
Pinj_p:   dP/dVre_p , dP/dVim_p , dP/dVre_k , dP/dVim_k
          from d/dx of Re( E_p * conj(sum_k Y_pk E_k) )    (bilinear -> linear-in-x partials)
```

Because `P/Q` injections and flows are **bilinear** in the rectangular state, the
partials are affine in `x`; `|V|` and `|I|` add the only genuinely nonlinear rows.
This is why rectangular DSSE converges in very few Gauss-Newton steps and why the
gain is nearly constant - exploited in the branch-current path (Section 14) and
noted as a conditioning advantage here.

### 13.4 Loop, damping, observability, bad data

Reused wholesale from the transmission estimator's doctrine:
- Gauss-Newton on the normal equations `G = H^T W H`, `G dx = H^T W (z - h)`,
  with `W = diag(1/sigma^2)`.
- Trust-region damping on `||dx||` (rectangular components share one cap; the
  angle/Vm split is not meaningful in rectangular coordinates, so a single
  per-component cap is used).
- Observability by gain rank (dense) / unmeasured-island sweep (sparse), reusing
  the `ObservabilityReport` type and doctrine (Rank = -1 on the sparse backend).
- Chi-square consistency (`ChiSquare.Quantile`, reused unchanged) and largest-
  normalised-residual bad-data removal with critical-measurement protection.
- Zero-injection buses (no load, no source) are exact equality constraints via
  the same Hachtel/KKT mechanism as `ConstrainedDenseWlsStep`
  (`Source == "virtual-zero-injection"`), which is heavily used on feeders (every
  branchless junction is a zero-injection node).

`DsseEstimationResult` mirrors `EstimationResult` (status, converged, iterations,
per-phase `Vm`/`VaDeg`, objective, dof, chi-square verdict, residual table,
removals, observability), plus a `ToMultiPhaseResult(model)` projector analogous
to `EstimationResult.ToPowerFlowResult` (pure evaluation, no solve - so
no-forward-inference holds).

---

## 14. Branch-current DSSE (distinct estimation path)

A structurally different estimator that cross-validates the node-voltage WLS -
the classic **branch-current-based** DSSE (Baran/Kelley), which is the
distribution workhorse because its gain is nearly constant on radial feeders.

### 14.1 State

```
x = [ Ire(branch,phase), Iim(branch,phase) ]  over all branches and present phases
```

i.e. the complex series current of every branch per phase (rectangular). For a
radial feeder with `b` branches and 3 phases the state is `6b`; bus voltages are
**recovered** from the branch currents by a forward sweep from the reference,
not carried as state.

### 14.2 Measurement equations

- **Branch current (magnitude or PMU rectangular).** Linear in the state -
  `h = Ire` / `Iim` or `|I|` directly. This is the natural, best-conditioned
  distribution measurement and makes `H` almost constant.
- **Branch power flow.** `S = V * conj(I)`; with `V` obtained from the current
  state by the forward sweep, `P/Q` flow rows are bilinear, evaluated at the swept
  voltages.
- **Injection.** A bus injection is the (connection-resolved) sum of the incident
  branch currents times the bus voltage: `S_inj = V_bus * conj( sum incident I )`.
- **Voltage magnitude.** `|V_bus|`, with `V_bus` from the forward sweep - the only
  rows coupling distant branches, and the reason a small number of voltage
  measurements is needed for the magnitude datum.

### 14.3 Why it is a distinct path, not a reformulation

The Jacobian topology is fundamentally different (branch-indexed, near-constant),
the gain factorisation can be reused across iterations, and the two estimators
have different numerical failure modes - so agreement between the node-voltage
WLS and the branch-current DSSE on the same measurement set is a strong
cross-check (Section 18.4), the same philosophy as GridSim's dense/sparse and
Newton/BFS cross-validation. `BranchCurrentDsse` is a separate public estimator
(not an `IThreePhaseStep`), returning the same `DsseEstimationResult` so callers
and tests treat the three estimators uniformly.

### 14.4 Restriction note

Branch-current DSSE is explicitly on the permitted whitelist in
the review gate (`RestrictionReview`): branch-current estimators are filters, not predictors.
It is measurement-only, emits a present/past state, and needs no review entry.

---

## 15. Extended-WLAV robust estimator (sparse-l1)

`ExtendedWlav` implements the **weighted least-absolute-value** estimate as an
exact linear program, "extended" to carry equality and inequality constraints -
the robust sibling of the approximate IRLS-LAV already in the transmission
estimator.

### 15.1 Formulation

WLAV minimises the weighted l1 norm of residuals:

```
min_x  sum_i  w_i * | z_i - h_i(x) |
```

Linearised at the operating point (`H` = Jacobian, `r = z - h(x_bar)`,
`dx = x - x_bar`), each Gauss-Newton step is the LP:

```
min   sum_i w_i (u_i + v_i)
s.t.  H dx - u + v = r
      u, v >= 0                          (u,v = pos/neg residual parts)
[extended]  C dx = d_c                   (zero-injection equalities)
            x_lo <= x_bar + dx <= x_hi   (state box, when Feasible-style guard on)
```

Solved by a **sparse primal-dual interior-point** method (or the equivalent
sparse simplex on the dual), reusing the CSR/CSC + Gilbert-Peierls sparse LU
already in `GridSim.Core.Numerics` for the KKT/normal-step factorisation. The
outer Gauss-Newton loop re-linearises until `||dx||` meets tolerance.

### 15.2 Why WLAV, and why "extended"

- **Robustness by construction.** The l1 estimate is insensitive to a bounded
  number of gross errors without an explicit detection-and-removal loop: a single
  bad measurement in a redundant set is fit with zero weight at the LP vertex.
  This is valuable on distribution data where pseudo-measurements and AMI reads
  carry heavy-tailed errors that break the Gaussian assumption behind WLS.
- **Extended** = the same estimate honours **hard zero-injection equalities**
  (ubiquitous on feeders) and optional **state boxes** (voltage admissibility),
  reusing the constrained/feasible doctrine rather than the huge-weight trick.

### 15.3 Determinism and restriction

The LP uses a fixed pivot/ordering rule and a fixed starting point, so the vertex
selected is deterministic and byte-reproducible (a determinism test pins it). On
clean Gaussian data WLAV agrees with the node-voltage WLS to a documented
tolerance (they minimise different norms, so not to machine epsilon, but close);
on data with one injected gross error, WLAV's estimate is unmoved while WLS
requires its removal loop - the discriminating test. EWLAV is on the permitted
whitelist; it is measurement-only and needs no review entry.

---

## 16. Pseudo-measurement generation

Feeders are under-determined by real telemetry alone. `DistributionPseudo
Measurements` makes a case observable by generating priors - the distribution
analogue of `SyntheticMeasurements` + `MeasuredInjectionPrior`.

### 16.1 Sources of pseudo-measurements

1. **Nameplate / kVA allocation.** Distribute a measured feeder-head load onto
   downstream buses in proportion to connected transformer kVA (or contract
   capacity), per phase. Sigma is large (typically 30-50% of value) - these are
   weak priors, not observations.
2. **Load-research / AMI profile priors.** Where historical smart-meter or
   load-research class profiles exist, the expected per-phase demand at a bus and
   time-of-day is a pseudo-measurement with a tighter (profile-derived) sigma.
3. **Zero-injection equalities.** Every branchless junction / pure connection
   node injects exactly zero on every present phase - authored as
   `Source == "virtual-zero-injection"` rows the constrained/EWLAV paths promote
   to hard equalities (Sections 13.4, 15).
4. **Voltage-schedule prior.** A flat or regulator-scheduled voltage magnitude at
   unmetered buses at a loose sigma, the distribution version of the transmission
   `pseudo-voltage-schedule` prior.

### 16.2 Sigma model and provenance

Each pseudo-measurement carries a `Source` tag naming its composition
(`pseudo-alloc-kva`, `pseudo-ami-profile`, `virtual-zero-injection`,
`pseudo-voltage-schedule`), with sigmas combined root-sum-square, exactly the GDA
sigma-model convention documented for the transmission estimator. This lets the
bad-data / robust logic treat weak priors appropriately (never removing a
zero-injection equality; down-weighting a 40%-sigma allocation before a 2%-sigma
metered flow).

### 16.3 Determinism

Same discipline as `SyntheticMeasurements`: one seeded RNG (date-literal default
seed), buses/branches walked in model order, Box-Muller Gaussians, `NoiseScale =
0` draws nothing (exact-recovery fixture). The generator **never reads the wall
clock**; `TakenUtc` is an explicit, safely-historical parameter.

### 16.4 Restriction: pseudo-measurements are historical priors, not forecasts

This is the load-bearing restriction point for the pseudo-measurement layer:

- A pseudo-measurement encodes what a load *typically was* (allocation, historical
  profile), not what the feeder *will do* or is *secretly doing now*. It is a
  prior over the past/present state the estimator is reconstructing, never a
  forward projection of the real grid.
- Any real AMI / profile input is a **dated ingester** and passes
  `ForwardInferenceGuard.RejectFuture` at load: profiles must be >=169h old or
  replayed. `MultiPhaseMeasurementSet.Load` horizon-checks its `TakenUtc` like
  `MeasurementSet.Load` does.
- The generated set's timestamp is the reconstruction window's, so invariant #2
  (no future-dated output) holds through the estimate.

---

## 17. Seams to extend (summary)

New seams, each a clean parallel to an existing one - no existing interface is
widened:

| New seam | Parallels | Purpose |
|---|---|---|
| `IMultiPhaseSolver` (`Solve -> MultiPhaseResult`) | `IPowerFlowSolver` | three-phase power flow |
| `ThreePhaseSweep` | `BackwardForwardSweep` | the v1 solver behind that seam |
| `IThreePhaseStep` (`Begin`/`GateObservability`/`TrySolve`/`NormalizedResiduals`) | `IWlsStep` | dense/sparse three-phase WLS backends |
| `ThreePhaseEstimationMethod { Wls, BranchCurrent, Ewlav }` | `EstimationMethod` | method selection; all three whitelisted |
| `PhaseMeasurementKind` | `MeasurementKind` | per-phase measurement vocabulary |
| `MultiPhaseYBus` / `SequenceFaultStudy` | `YBus` | phase and sequence admittances |
| `DistributionPseudoMeasurements` | `SyntheticMeasurements` + `MeasuredInjectionPrior` | observability priors |

Method selection: rather than overloading the transmission `EstimationMethod`
enum (which is bound to the positive-sequence `WlsStateEstimator`), the
distribution estimators take a sibling `ThreePhaseEstimationMethod`. The names
`Wls`, `BranchCurrent`, `Ewlav` map onto the permitted whitelist entries, and a
comment in the enum cross-references `RestrictionReview` so the
whitelist coverage is discoverable at the call site. `Huber`/`Lav`/`Constrained`/
`Feasible` are available on the node-voltage path by reusing the IRLS/KKT/box
mechanisms behind `IThreePhaseStep`.

The `RestrictionReview.AssertPermitted` call is present at the head of every
estimator (as it is for the transmission estimator); because none of these
methods `usesDynamicsModel`, the gate passes them unconditionally, and
`RestrictionInvariantTests` continues to assert the code table and the log agree.

---

## 18. Validation and test plan

The gate for merge: the full existing suite stays green (no positive-sequence
regression) and the new suite below passes, under `TreatWarningsAsErrors`.

### 18.1 Reference feeders

Embed the IEEE 13-, 34-, and 123-bus **unbalanced** test feeders as
`data/distribution/ieee{13,34,123}.json` (a faithful transcription of the IEEE
PES / OpenDSS feeder data: line geometries or reduced phase matrices, spot and
distributed loads, regulators, transformers, capacitors, phasing). Add them as
`EmbeddedResource` with stable logical names
(`GridSim.Core.cases.dist.ieee13.json`, ...), exactly like the MATPOWER `case*.m`
resources. A `DistributionCases` factory (parallel to `IeeeCases`) materialises
them into `MultiPhaseModel`.

### 18.2 Machine-epsilon vs an independent in-house reference

"Machine epsilon" is asserted against a **second, structurally-different in-house
solver**, not against OpenDSS (whose reported precision is limited). The
independent reference is a direct **nodal-admittance (Y-bus) fixed-point / full
3n-dimension Newton** three-phase power flow (a different algorithm from the
BFS): its converged phase voltages must agree with `ThreePhaseSweep` to `< 1e-9`
pu on all three feeders. This is the same doctrine as the transmission
Newton-vs-BFS and dense-vs-sparse cross-checks.

### 18.3 Non-regression / balanced-bridge (the master gate)

- `GridModel.ToBalancedThreePhase()` on each MATPOWER case, solved by
  `ThreePhaseSweep`, collapsed via `ToPositiveSequence()`, must reproduce the
  positive-sequence `BackwardForwardSweep` (radial cases) and Newton result to
  `< 1e-10` pu. This proves the three-phase machinery is correct AND that the
  positive-sequence path is untouched.
- Every pre-existing test (power flow, transmission estimation, fault `ThreePhase`,
  protections) is asserted byte-identical - the additive model fields default to
  no-ops.

### 18.4 Estimator correctness and cross-agreement

- **Exact recovery.** A full, exact (`NoiseScale = 0`) three-phase measurement
  set recovers the `ThreePhaseSweep` truth to `< 1e-10` for the node-voltage WLS
  and the branch-current DSSE on all three feeders (mirrors
  `WlsEstimatorTests.AssertRecoversTruth`).
- **Cross-estimator.** Node-voltage WLS, branch-current DSSE, and EWLAV agree on
  clean noisy data to a documented tolerance (WLS==BCDSSE to ~1e-8; EWLAV close,
  different norm).
- **Bad data.** With one injected gross error, WLS's removal loop identifies and
  removes it; EWLAV's estimate is unmoved without removal. Both restore the truth
  within the clean-data band.
- **Observability.** A feeder stripped to below observability reports
  `Unobservable` with the free directions named; adding the pseudo-measurement
  layer restores observability - a direct test of Section 16.

### 18.5 Model-layer checks

- **Carson vs supplied matrices.** `PhaseImpedance.FromCarson` on a geometry with
  a published reduced matrix matches the supplied `z_abc` to machine epsilon.
- **Kron consistency.** Four-wire explicit solve, Kron-reduced, equals the reduced
  three-wire solve on a common grounded case (Section 6).
- **Sequence identities.** For a transposed line, `z_012 = diag(z0,z1,z2)` to
  machine epsilon; `A * A^-1 = I`.
- **Fault checks.** SLG at a bus: `I_a0 = I_a1 = I_a2` and `I_b = I_c = 0` for a
  pure a-g fault (to machine epsilon); a symmetric three-phase-to-ground SLG-limit
  equals the direct phase-domain three-phase fault; magnitudes cross-checked to an
  OpenDSS `FaultStudy` to its reported precision.
- **Converter loss.** AC/DC balance closes to machine epsilon for a chosen
  `(a0,a1,a2)`; a zero-loss model reproduces the ideal-source solve exactly.

### 18.6 Cross-check to OpenDSS / pandapower-3ph

Under `tools/crossval/` (parallel to the existing pandapower cross-validation),
scripts export each feeder solve and estimate and compare against OpenDSS
(`Solve`, `Export Voltages`) and `pandapower` three-phase (`pandapower.pf.
runpp_3ph`, `pandapower.estimation` where applicable). Agreement is asserted to
each tool's published precision (typically 1e-4..1e-6 pu), with the in-house
machine-epsilon check (18.2) carrying the tight bound. Differences are documented,
not silently tolerated.

### 18.7 Determinism

Byte-identical results across repeated runs and (CI) across platforms: the
pseudo-measurement RNG is seeded, the sweep and estimator visit model order, the
sparse orderings are deterministic, and the EWLAV LP uses a fixed pivot rule. A
determinism fixture hashes the solved/estimated arrays and pins the hash.

### 18.8 Restriction invariants

`ThreePhaseRestrictionInvariantTests`: `MultiPhaseMeasurementSet.Load` rejects a
`TakenUtc` newer than `UtcNow - 169h`; an AMI pseudo-source rejects a fresh
stamp; the estimators emit a state stamped no later than their newest input; and
`RestrictionReview` needs (and gains) no new dynamics-fused entry for any method
here.

---

## 19. Build-gate and coding-standard conformance

The repo builds pedantically: `TreatWarningsAsErrors=true`,
`EnableNETAnalyzers`, `AnalysisMode=Recommended`, `Nullable=enable` (root
`Directory.Build.props`). The new code must land at **zero warnings**:

- Nullable-clean: no `!` dammit operators without justification; seams return
  non-null or typed-empty (`Array.Empty<T>()`), matching the existing style.
- `sealed` on every concrete class not designed for extension (all of them);
  `internal` implementation with `InternalsVisibleTo("GridSim.Tests")`.
- No unused parameters/usings/locals; `static` where the analyser asks; culture-
  invariant parsing/formatting for the feeder JSON loader (`CultureInfo.
  InvariantCulture`, as `MeasurementSet.TryResolveBranch` already does).
- Deterministic APIs: any `Random` is seeded from an explicit option, never the
  default clock-seeded ctor (an analyser/determinism concern both).
- Complex/matrix value types (`PhaseImpedance`) implement value equality and
  guard against non-finite entries at construction, consistent with the model's
  finite-input validation doctrine.
- XML doc comments on every public member (the codebase documents densely; the
  analyser set and the house style both expect it).

CI runs `dotnet build -warnaserror` and the full `dotnet test`; both must be
green before the positive-sequence path is considered non-regressed.

---

## 20. Restriction invariants honoured (checklist)

| Invariant (`UnderstandingRestrictions.md`) | How this design honours it |
|---|---|
| #1 169h floor at every dated ingester | `MultiPhaseMeasurementSet.Load` and every AMI/profile source call `ForwardInferenceGuard.RejectFuture` |
| #2 no future-dated output | estimators stamp at the window time; `ToMultiPhaseResult`/`ToPowerFlowResult` are pure evaluation |
| #3 solver forward trajectories are model output | the three-phase power flow is solving; seeded only from guard-permitted data; never presented as a feeder forecast |
| #4 real-time on replay/permitted-age only | DSSE estimates present/past from permitted-age or replayed three-phase sets, never live feeder telemetry |
| #5 Dynamics->Estimation fusion is gated | nothing here consumes a `Dynamics/` process model; all methods are static filters; no new review entry needed (branch-current, EWLAV already whitelisted) |

The pseudo-measurement layer is the subtlety: it is explicitly a **historical/
allocation prior over the reconstructed state**, not a forecast, and it is
horizon-guarded like every other dated input. The balanced virtual bus is a model
device that asserts nothing about reality.

---

## 21. Implementation phasing

A suggested build order that keeps the suite green at each step:

1. **Model + Y-bus** (Sections 5-8.1): `Phase`, `PhaseImpedance` (+ Carson/Kron),
   `MultiPhaseModel` and records, `MultiPhaseYBus`, `Validate`, the balanced-
   bridge `ToBalancedThreePhase`/`ToPositiveSequence`. Gate: 18.3, 18.5.
2. **Three-phase power flow** (Section 10): `ThreePhaseSweep`,
   `MultiPhaseResult`, the independent Newton reference. Gate: 18.2, 18.3.
3. **Sequence networks + faults** (Sections 8.2-8.3, 9): `SymmetricalComponents`,
   `SequenceFaultStudy`, `FaultAnalysis.SingleLineToGround` and additive fields.
   Gate: 18.5 fault checks.
4. **Node-voltage WLS** (Section 13): measurement model, `IThreePhaseStep` dense
   backend, loop, chi-square/bad-data reuse. Gate: 18.4 exact recovery.
5. **Pseudo-measurements** (Section 16) + observability tests. Gate: 18.4
   observability, 18.8.
6. **Branch-current DSSE** (Section 14) and cross-agreement. Gate: 18.4 cross.
7. **EWLAV** (Section 15) sparse-l1. Gate: 18.4 bad-data, 18.7 determinism.
8. **Converter loss + virtual bus + four-wire LV** (Sections 6, 11, 12) as the
   islanded/DER-heavy hardening pass. Gate: 18.5 Kron/converter, islanded solves.
9. **Sparse backends + scaling** (sparse `IThreePhaseStep`, sparse EWLAV) for
   large feeders. Gate: 18.7 determinism at scale.
10. **Cross-val scripts** (Section 18.6) and the technical-manual chapter.

---

## 22. Open questions and future work

- **Time-series / quasi-static** three-phase estimation over a day of AMI reads
  (state carried bus-to-bus as a warm start) - still a static filter per instant,
  but the chaining deserves its own restriction note (it must stay a sequence of
  present-state reconstructions, never a forecast).
- **Regulator/OLTC discrete-tap estimation** (mixed-integer) - deferred; v1
  treats taps as known inputs.
- **Meter placement / observability design** tooling on the pseudo-measurement
  layer.
- **Harmonic / unbalanced-source** extensions to the sequence model.
- A **GDA distribution feed** (LV/MV network models + AMI in the data lake),
  analogous to the transmission GDA bridge, once such data is authored and
  horizon-guarded on the GDA side.

## See also

- [State estimation](<../0006 State estimation.md>) - the positive-sequence estimator this parallels
- [Power flow](<../0003 Power flow.md>) - Newton/BFS engines and the pi-model stamps reused per phase
- [Protections](<../0015 Protections.md>) and [Protection assessment](<../0017 Protection assessment (a passive relay overlay).md>) - the overlay the SLG fault feeds
- [Provenance and invariants](<../0008 Provenance and invariants.md>) - the horizon guard on dated inputs
- `UnderstandingRestrictions.md`, `RestrictionReview.Reviewed` (`src/GridSim.Core/Diagnostics/RestrictionReview.cs`) - the governing rule and the DSE gate
