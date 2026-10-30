# Design: EMT time-domain solver (Dommel companion model)

> **Status (verified 1.95.091):** Implemented: the single-phase companion engine with ideal switches, faults and critical damping adjustment, Bergeron lines, constant-impedance loads, off-nominal taps and series-capacitor arms, and the three-phase (abc) engine with mutual coupling and average-value grid-following and grid-forming converters (`ThreePhaseConverter`). Not implemented: converter and machine models in the single-phase scalar engine, a public real-valued factorisation seam, and a three-phase build from `GridModel`.
> **Implemented in:** `src/GridSim.Core/Dynamics/Emt/EmtEngine.cs` (`EmtEngine`), `EmtNetwork.cs` (`EmtNetwork`, `FromGridModel`), `EmtElement.cs` (`SeriesBranch`, `SeriesCapacitorBranch`, `ShuntElement`, `VoltageSource`, `CurrentSource`, `SwitchElement`, `BergeronLine`), `Companion.cs` (`EmtCompanionStamp`), `EmtEvent.cs`, `EmtUnits.cs`, `EmtTypes.cs`, `ThreePhaseEmt.cs` (`ThreePhaseEmtEngine`, `ThreePhaseNetwork`, `ThreePhaseBranch`, `ThreePhaseFault`, `ThreePhaseConverter`); CLI verb `emt`.
> **Design-to-code name map:**
>
> | Design name | Implemented as |
> |---|---|
> | `IRealLinearFactorization` (future public seam) | not implemented; `EmtEngine` calls the internal `Numerics/SparseLu.Factor` and `SparseLuFactorization.Solve` directly |
> | converter and machine models behind the `CurrentSource` seam (scalar engine) | not implemented there; average-value converters exist only in `ThreePhaseEmtEngine` (`ThreePhaseConverter`) |
> | `EmtCompanionStamp` | in `Companion.cs` |
> | `EmtLineTests`, `EmtRlcAnalyticTests`, `EmtSteadyStateTests`, `EmtSwitchingTests` | present under those names in `tests/GridSim.Tests/` |
>
> This is a design record. The current behaviour is documented in the technical manual chapter [21. Electromagnetic transient](<../0021 Electromagnetic transient (EMT) simulation.md>).

## Governing constraint

An EMT run is **Solving**, not inference, under [`UnderstandingRestrictions.md`](../../../UnderstandingRestrictions.md):
it integrates the network's governing differential equations forward in time about the **model**, and its
output is a **model trajectory** - never a claim about the real grid's present or future. It therefore:

- sits in the `Dynamics/` seam beside the swing/harmonic forward-simulation tools (ch. 5, ch. 19), **not**
  in `Estimation/`;
- does **not** engage the `RestrictionReview` gate (`Diagnostics/RestrictionReview`), which fires only when a
  `Dynamics/` process model is fused into the estimator's predict step. An EMT solver never touches the
  estimator, so it is unconditionally permitted as forward simulation;
- seeds only from guard-permitted data if any dated input is used (the 169 h `ForwardInferenceGuard`, ch. 8),
  and labels every output a model trajectory;
- carries `ProvenanceTag.CuratedPlaceholder` + the literal `[VERIFY]` token on any assumed parameter. The
  passive R/L/C/source/switch elements implemented here are grounded in the model's per-unit data; future
  converter/machine control internals (vendor-proprietary, absent from the lake) will be placeholders.

## Method

The network is solved by Dommel's nodal companion-model method. Each reactive element is discretised with the
trapezoidal rule into an equivalent conductance `G_eq` in parallel with a history current source `I_hist`.
Assembling these gives a real nodal system

```
G * v(t) = i_history(t) + i_sources(t)
```

which is solved for the instantaneous node voltages every fixed timestep `dt`. `G` is **constant between
switching events**, so it is factored once and back-substituted each step - the amortization that makes
microsecond stepping affordable.

### Companion models (per-unit; `omega0 = 2*pi*FrequencyHz`, `L = X/omega0`, `C = B/omega0`)

All fold to the single form `I_hist = HistCoeffV*v_prev + HistCoeffI*i_prev` (see `Companion`):

| Element | Trapezoidal | Backward Euler (CDA half-step) |
|---|---|---|
| Series R-L | `G = 1/(R+2L/dt)`, `I_hist = G*v_prev + alpha*i_prev`, `alpha = (2L/dt-R)/(2L/dt+R)` | `G = 1/(R+L/dt)`, `I_hist = i_prev/(1+R*dt/L)` |
| Capacitor | `G = 2C/dt`, `I_hist = -G*v_prev - i_prev` | `G = C/dt`, `I_hist = -G*v_prev` |
| Resistor / switch | `G = 1/R`, no history | same |

A **voltage source** is stamped as its Norton equivalent when Thevenin (`G = 1/Rs`, injection `e(t)/Rs`) or
as a Dirichlet node when ideal (eliminated from the unknown set, like the slack bus - never a penalty
conductance). A **switch/fault** is a conductance toggling between `1/Ron` and `1/Roff`; it stays permanently
in the matrix pattern (open = a tiny conductance), so the fill-reducing ordering is computed once and only a
numeric re-factor is needed on an event.

### Rest initialisation

A source already on at t=0 requires the history to reflect the rest state (`i_L=0`, `v_C=0`) or the source
appears to ramp 0->V over the first step (half the initial `di/dt` - the standard trapezoidal step-from-rest
artefact). `EmtEngine.InitializeRest` performs an IC solve (capacitors hold their nodes at 0, inductors open,
sources at emf(0), resistors carry the resulting DC) and seeds each reactive branch's history from it.

### Critical Damping Adjustment (CDA)

Trapezoidal integration is only marginally stable (poles on the j*omega axis), so a switching discontinuity excites
a spurious `2*dt` (Nyquist) oscillation - most visibly a capacitor's current alternating +/- after a voltage
step. On the event step the engine takes **two backward-Euler `dt/2` half-steps** (BE is L-stable; `alpha = 0`),
annihilating the mode, then resumes trapezoidal. A whole-run backward-Euler fallback (`--rule be`, O(dt)
accuracy) is available if CDA is undesirable.

## Numerics & reuse

- Companion assembly mirrors `PowerFlow/AdmittanceStamp` and `Harmonics/FrequencyDependentYBus` (a per-parameter
  matrix rebuild): `EmtCompanionStamp` produces the companion; `Numerics/Sparse.CsrMatrix.Builder` assembles `G`.
- Factor-once/solve-many uses the internal `Numerics/SparseLu.Factor` -> `SparseLuFactorization.Solve` (RCM
  ordering, Gilbert-Peierls LU). `EmtEngine` lives in `GridSim.Core`, so it calls these directly. A future
  public `IRealLinearFactorization` seam (mirroring `IComplexLinearFactorization`) would let the GPU backend
  drop in and decouple external callers - deferred, not needed for the current cut.
- The batch loop runs under `Simulation/GcScope.LowLatency` with per-step buffers pre-allocated.

## Files

`src/GridSim.Core/Dynamics/Emt/`: `EmtUnits`, `Companion` (`EmtCompanionStamp`), `EmtElement`
(`SeriesBranch`/`ShuntElement`/`VoltageSource`/`CurrentSource`/`SwitchElement`), `EmtEvent` (`SwitchEvent`),
`EmtNetwork` (+`FromGridModel`, `EmtCase`, `EmtBuildOptions`), `EmtTypes` (`EmtPoint`/`EmtResult`/`EmtRunStats`/
`IntegrationRule`/`EmtProbe`/`EmtOptions`), `EmtEngine`. CLI: the `emt` verb in `GridSim.Cli/Program.cs`.

## Travelling-wave (Bergeron) lines

A lossless distributed line is a surge-impedance conductance `Yc = 1/Zc` to ground at each end plus a history
current source carrying the wave that left the **other** end `N = round(tau/dt)` steps ago - the ends couple
only through the delay buffers, so the stamp is diagonal-only. From a branch's per-unit X, B: `Zc = sqrt(X/B)`,
`tau = sqrt(X*B)/omega0` (`EmtUnits.SurgeParams`). `BergeronLine` in `EmtEngine`; per-line ring buffers advance one
slot per full step (frozen during a CDA half-step). `FromGridModel(UseTravellingWaveLines: true)` builds
charged non-transformer branches as lines (lossless - series R dropped in that mode).

## Constant-impedance loads

`FromGridModel(IncludeLoads: true, default)` models each bus's `Pd/Qd` as a constant-impedance shunt at 1 pu:
`Y = (Pd - jQd)/BaseMva` -> a conductance plus an inductor (Qd>0) or capacitor (Qd<0). This damps the
otherwise near-lossless network to a physical settling.

## Three-phase (abc) with mutual coupling - `ThreePhaseEmtEngine`

Each bus is three phase nodes (`3*bus + p`). A `ThreePhaseBranch` (symmetric self/mutual R,L) stamps the **3x3
companion** of its matrices - `Y = (R + (2/dt)L)^-1`, `alpha = Y*((2/dt)L - R)`, history `I_hist = Y*v_prev +
alpha*i_prev` per phase - so inter-phase mutual coupling is exact (the difference from three independent circuits).
Ideal three-phase sources (120 deg-apart Dirichlet nodes), per-phase shunts/loads, and shunt faults
(`ThreePhaseFault`, e.g. single-line-to-ground) are supported; faults toggle a conductance in a fixed pattern.
Reuses the same nodal reduction + `SparseLu` factor-once path as the scalar engine.

## Validation (acceptance gates)

- **`EmtRlcAnalyticTests`** - closed-form series-RLC step response (over-, critically-, under-damped) to
  < 1e-5 against an independent oracle; error is O(dt^2); byte-deterministic.
- **`EmtSteadyStateTests`** - a DC-energised RL network relaxes to the resistive DC solution.
- **`EmtSwitchingTests`** - closing onto an L-C branch gives a TRV peak about 2 x V; CDA cuts the post-event
  numerical roughness by > 2x on a capacitor-switching case.
- **`EmtLineTests`** - a matched Bergeron line absorbs its wave with no reflection; an open-ended line doubles
  the far-end voltage at tau and the reflection returns at 2tau.
- **`ThreePhaseEmtTests`** - a balanced coupled line matches the analytic **positive-sequence phasor** to
  < 5e-3 with zero-sequence approx. 0 (mutual coupling handled); a single-line-to-ground fault collapses the faulted
  phase, leaves the healthy phases energised, recovers on clearing, and creates a zero-sequence voltage.

## Limitations / remaining future phase

- **Converters/machines** slot in behind the `CurrentSource`/Norton seam with a `dq` interface and
  `CuratedPlaceholder [VERIFY]` parameters - the one substantial phase not yet built.
- Off-nominal taps are modelled as an ideal From-side transformer and X < 0 as a series R-C arm (R078, see
  technical chapter 21.7); a phase-shifting transformer is refused by name (it needs a three-phase build).
  Bergeron lines are lossless (series R dropped); a lossy line lumps R at the ends. `FromGridModel` three-phase
  needs per-phase sequence data (absent from the lake -> would be `[VERIFY]`), so the three-phase engine is
  driven directly for now, not from `FromGridModel`.
