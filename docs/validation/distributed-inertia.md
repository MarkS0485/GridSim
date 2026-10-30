# Distributed / multi-domain inertia model

*Branch of origin: `inertia2-distributed`. Status: experimental, opt-in, additive. The lumped
scalar model remains the default and is untouched.*

## 1. Why

Almost every frequency solver - GridSim's default path included - models system inertia as a
**single lumped centre-of-inertia (COI) scalar**: `E = sum(H*MBase)`, one swing equation
`df/dt = f0/(2E)*deltaP` (`FrequencyDynamics.Simulate`). That is exact for the questions it answers, but
it erases *where* inertia sits, and it scores an evenly-spread fleet as adequate - when an even
spread is itself a fragmentation failure mode.

The TSGB "speed domain" thesis (corpus LOS sec. 2.7 / sec. 5.5) says the real fleet groups into **speed
domains** by mechanical speed / pole ratio; each domain has an apparent-power stiffness
`GVad = sum(S_i*C_i)`; a domain is **dominant** when `deltaGVa = (GVad1 - GVad2)/GVad2 >= 0.10` (an
engineering-judgement threshold, not a fitted constant - but empirically ~the 10th percentile of the GB
dominance gap, firing ~9.4% of the time over 103k samples; `init`-settable for calibration); and a
disturbance does not reach the whole system at once - it propagates between domains at a finite
electromechanical wave speed. None of that is visible to a scalar. This model makes it visible, as a
runtime-selectable alternative that reduces *exactly* to the lumped model in its degenerate limit.

## 2. The non-negotiables

- **Lumped stays the default and is untouched.** Distributed is purely additive. Every pre-existing
  test stays green with zero behaviour change; the four `rocof = f0/(2*E)*deltaP` lines are not edited.
- **Runtime switch**, mirroring the `SolverKind`/`SolverBackend` seam: an `enum InertiaModel {
  LumpedScalar, Distributed }` resolved by `InertiaModelSelector`, exposed on the API, CLI, WPF and
  Web.
- **GDA provides only data already present or streamed** (per-machine `H`, `Bus`, `MBase`, `Type`,
  `Branch.R/X`, `Bus.X/Y`, and the precomputed lake products). GridSim derives pole/speed, domains,
  distances, propagation, and integration.
- **Every validation tier is a fail-loud gate.** Where there is no GB-scale ground truth, the result
  is a bounded, mechanism-attributed **divergence report** tagged `unvalidated-at-scale` - never an
  accuracy claim against a reference that does not exist. Machine-epsilon is **relocated** (to
  aggregates and reductions where an exact oracle exists), not fabricated.

## 3. The selectable seam

New under `src/GridSim.Core/`, mirroring `PowerFlow/SolverSelector.cs`:

| File | Role |
|---|---|
| `Abstractions/IInertiaModel.cs` | The seam: `Simulate(...)` -> `FrequencyResponse`; `CoiInertiaGvaS(model)`; `DistributedResult? LastDistributedResult`. |
| `Dynamics/InertiaModel.cs` | `enum InertiaModel { LumpedScalar, Distributed }`. |
| `Dynamics/InertiaModelSelector.cs` | `Resolve` / `Create` / `Label`, one-to-one with `SolverSelector`. |
| `Dynamics/LumpedScalarInertiaModel.cs` | Delegates verbatim to `FrequencyDynamics.Simulate`; returns `model.SystemInertiaGvaS`. **Identical old behaviour.** |
| `Dynamics/Distributed/DistributedInertiaModel.cs` | The multi-mass model; returns a COI-collapsed `FrequencyResponse` and stashes a `DistributedResult`. |

`DynamicsConfig` gains `InertiaModel InertiaModel { get; init; } = InertiaModel.LumpedScalar;` and a
`DistributedConfig? Distributed`. Consumers opt in by routing through `InertiaModelSelector` instead
of calling `FrequencyDynamics.Simulate` directly.

## 4. The numerical model

One **coupled multi-mass swing kernel** spanning a spectrum, with LumpedScalar as its exact one-mass
degenerate limit.

- **Domain assignment** (`Distributed/PoleMap.cs`, `DomainAssignment.cs`, `SpeedDomain.cs`,
  `DominanceReport.cs`). `PoleMap` is a GridSim-owned `Generator.Type -> (poles, rpm, DomainKind)`
  lookup: nuclear/coal/CCGT -> 2-pole/3000 rpm synchronous; large hydro/pumped -> multi-pole
  synchronous; wind/solar/storage/interconnector -> `Pll`/virtual (no real pole). `DomainAssignment`
  partitions generators into domains, computes each domain's aggregate inertia `H_d = sum H_i` and
  `GVad = sum(S*C)`, and reports `deltaGVa` + dominance.
- **The kernel** (`Distributed/MultiDomainSwing.cs`). Each domain is a coherent mass linked by
  Kron-reduced synchronising power. It keeps the COI equal to the lumped result by keying the
  aggregate forcing off the inertia-weighted COI and using **antisymmetric tie power** (sum = 0), and
  it integrates with **symplectic (semi-implicit) Euler** - forward Euler blows up under the stiff
  Kron coupling. The single-domain / reduction limit delegates to `FrequencyDynamics` for the exact
  reduction.
- **Physical coupling** (`PowerFlow/KronReduction.cs`, `Distributed/DomainCoupling.cs`). The
  inter-domain synchronising coefficients are Kron-reduced (star-mesh elimination) from the `YBus` -
  physically derived, not fitted - so the inter-area mode frequency is a property of the network, not
  a knob.
- **Propagation overlay** (`Distributed/PropagationDelay.cs`, **off by default**). Finite wave speed
  enters as a retardation `delta_j(t - tau_ij)` on the coupling, read from per-mass angle ring buffers.
  `tau = physical distance / wave speed`, with the wave speed derived from local inertia density and
  **clamped to the published 500-1500 km/s band**. It is off by default because the retardation
  perturbs the COI (real physics), so the machine-eps COI invariant only holds with it off.

**Why LumpedScalar is the exact limit.** Summing the per-mass swing equations, the internal coupling
telescopes and the COI obeys `(2E/omega_s)*domega/dt = deltaP` with `E = sum H_i*MBase_i` - identical to today's
scalar ODE. Set #domains = 1, tau = 0, coupling -> inf => machine-eps reduction.

## 5. COI projection - every existing consumer keeps working

`DistributedInertiaModel.Simulate` returns the inertia-weighted COI collapse
`f_COI = sum H_i f_i / sum H_i`, equal to the lumped response to machine-eps when domains don't diverge. So
`StabilityMetrics`, `InertiaEstimator`, `RocofScreening`, `TsgbSizer`, RP1, MECHANICAL, the Web
`FrameProjector`, and the bridge protocol read `CoiInertiaGvaS` (== `SystemInertiaGvaS`) unchanged.
The new per-domain / per-bus outputs live on `Distributed/DistributedResult.cs` (`DomainTrace[]`,
per-bus event RoCoF map, `DominanceReport`, wave-speed / delay matrix) for the spatial views.
`SystemInertiaGvaS` is retained verbatim - the distributed model *projects onto* it, never redefines
it.

## 6. Validation - the fail-loud gates

All in `tests/GridSim.Tests`, all pass-gates (484 tests green as of this writing).

| Tier | File | Asserts |
|---|---|---|
| T0 | `DistributedReductionTests` | The degenerate `{1 domain, no propagation, coupling -> inf}` config == `FrequencyDynamics.Simulate` to machine-eps. |
| T1 | `DistributedInvariantTests` | `sum H_i f_i / sum H_i == lumped f` to machine-eps; energy balance; identical settled steady state. |
| T2 | `CanonicalDynamicsTests`, `KronReductionTests` | Kron-reduced coupling and inter-area mode frequency behave canonically. |
| T3 | `DistributedQualitativeTests` | Dominance reproduces the LOS sec. 5.5 worked example (`deltaGVa ~ 0.333`); distant domains respond later; wave speed  in  500-1500 km/s; an externally-provided tau map overrides the first-order model. |
| T4 | `DistributedReplayTests` | The COI-projected distributed response reproduces recorded GB events within `ReplayValidator` tolerance. |
| T5 | `DistributedScaleDivergenceTests` | gb-full lumped-vs-distributed divergence is **bounded and mechanism-attributed**, emitted as a `DivergenceReport` tagged `ProvenanceTag.UnvalidatedAtScale`. No accuracy assertion. |
| T6 | `ClassicalMultiMachineTests` | The per-machine classical backbone (sec. 6a) against **exact analytic references**: SMIB mode frequency, equal-area critical clearing time, RK4 energy boundedness, COI conservation, and WSCC-9 solve->init->fixed-point. |

Machine-eps is **retained** for the T0 reduction and the T1 COI invariant; **relocated** to bands for
T3 (published), T4 (recorded), and T5 (bounded, no reference), and to closed-form physics for T6.

### 6a. The classical multi-machine backbone

Alongside the GB-scale domain-reduced kernel, GridSim carries the **per-generator classical
transient-stability model** (`ClassicalMultiMachine` + `ClassicalInit`) for the small canonical cases.
Each machine is a constant `E'angle delta` behind its transient reactance `X'd`, swinging over the network
Kron-reduced to the generator internal nodes (`Pe_i = sum_j |E'_i||E'_j|(G_ij cos + B_ij sin)`), integrated
with **classical RK4** for first-swing accuracy. `ClassicalInit.FromSolvedCase` builds the reduced inputs
from a solved power flow by the standard preparation: loads -> constant-impedance shunts, generator
internal nodes behind `X'd`, Kron reduction, `E' = V_t + jX'd*I_t`, and `Pm = Pe(delta0)` so an undisturbed
case is a fixed point.

It is validated against **exact, published, self-contained references** - no fabrication, no reliance on
a reference that doesn't exist: the single-machine-infinite-bus small-signal mode frequency
`omega_n = sqrt(omega_s*P_s/2H)` (to <1%), the **equal-area critical clearing time** (stable just below it, unstable
just above), undamped RK4 energy/amplitude boundedness over ~28 cycles, two-machine COI conservation, and
a WSCC-9 (Anderson-Fouad `X'd`) end-to-end check that the solved pre-fault point is a fixed point and a
rotor-angle bump oscillates while holding synchronism. It is additionally **cross-validated against an
independent integrator**: `gridsim stability case9 --export <json>` writes the reduced system and its RK4
trajectory, and `tools/crossval/crossval_classical.py` re-integrates the identical ODE with scipy's RK45
- the two full 3-machine coupled trajectories agree to **~3.7x10^-^8 rad** (machine epsilon), validating
the coupling numerics the analytic SMIB/2-machine limits don't exercise. This backbone is reserved for
the small canonical cases; the domain-reduced kernel remains the GB-scale path.

## 7. GDA as the data provider

**GDA precomputes the heavy, slow-changing physics; GridSim looks it up and owns the dynamics.** Two
lake products (GDA manual sec. 38.11) feed the model:

- **`propagation_delays`** - a network-static, single-file product of corridor delays: real physical
  and electrical distance, real local inertia, and a modelled clamped wave speed -> tau per bus pair.
  Served over the bridge as `op=propagation`. The loop is closed end-to-end: `GridBridgeClient.
  QueryPropagation(at)` requests the corridors and projects them (`PropagationData.TauByBusPair`) into
  the `(fromBus, toBus) -> tau` map that drops straight into `DistributedConfig.ProvidedTauSeconds`; where
  a domain pair's representative buses are found there, that tau is used instead of the first-order model,
  falling back to the model elsewhere (`PropagationDelay.Compute`). Corridors whose lake row carries no
  tau are omitted, and the honest provenance note (real distances/inertia; modelled wave speed) rides
  along on `PropagationData.Provenance`.
- **`speed_domains`** - the fleet decomposition precomputed from GridWatch alone (per-domain GVad +
  inertia + `deltaGVa` + the `is_too_evenly_spread` flag), a direct lake read the SPEED DOMAINS tabs can
  display for any past instant without recomputing.

The provenance split is honest and stated per column: real distances and inertia; **modelled and
unvalidated-at-scale** wave speed and delay. The lake product and GridSim's own first-order model use
the identical `clamp(1000*sqrt(200/density), 500, 1500)` relation, so they agree by construction.

## 8. Using it

- **API:** `new DynamicsConfig { InertiaModel = InertiaModel.Distributed, Distributed = new
  DistributedConfig { /* EnablePropagation, ProvidedTauSeconds, ... */ } }`, then route through
  `InertiaModelSelector`. Default `LumpedScalar`.
- **CLI:** the global `--inertia-model=lumped|distributed` switch (wired into `trip`, reported on
  `transient`), plus the dedicated `gridsim domains` verb that prints the decomposition. See
  [`docs/manual/0019 CLI reference.md`](<../manual/0019 CLI reference.md#domains>).
- **WPF:** the **SPEED DOMAINS** tab (`Views/DomainsVm.cs` + `DomainsView.xaml`), inserted before
  ASSETS - GVad and spinning-inertia bar charts, dominance against the 10% threshold, provenance
  badges. Degrades gracefully to a single-COI-domain summary when the lumped model is active.
- **Web:** the **SPEED DOMAINS** tab in GridSim.Web (`Components/Tabs/DomainsTab.razor`, fed by
  `TabProjections.Domains` -> `FrameEnvelope.Domains`).

## 9. Honest limits

The propagation overlay is **unvalidatable at scale**: there is no GB-wide PMU/DFR corpus to check an
absolute electromechanical wave speed against, so the model asserts only the band, never a
scale-accurate figure, and the overlay is off by default. Domain assignment from `type -> poles` is
many-to-one and inverters have no real pole - a conservative, documented default, provenance-tagged.
The per-machine classical multimachine model **and its RK4 integrator are now built and validated**
(sec. 6a) for the canonical cases. Still **deferred, not faked**: pinning the full published IEEE-39 / A&F
first-swing rotor-angle *curves* (needs the exact exciter/governor/clearing sequence), a scipy
multimachine sidecar, and importing the open dynamic cases the research catalogue surfaced (ANDES
IEEE-39 / Kundur, Texas A&M ACTIVSg `.dyr`) as additional cross-checks. The GB-scale path remains the
domain-reduced multi-area COI, which is what the T0-T5 gates validate. When the distributed and lumped
results diverge at GB scale, that divergence is reported and bounded, never presented as "more
accurate".

## Related

- `docs/validation/neso-rp1-and-mechanical.md` - the situational-awareness tab template this follows.
- [`docs/manual/0019 CLI reference.md`](<../manual/0019 CLI reference.md>) - the `--inertia-model` switch and the `domains` verb.
- `docs/validation/solver-validation-report.md` - the machine-epsilon validation discipline this relocates.
- GDA manual sec. 38.11 and Chapter 66 - the `propagation_delays` / `speed_domains` lake products and the
  `op=propagation` bridge channel.
