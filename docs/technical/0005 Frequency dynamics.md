# 5. Frequency dynamics

GridSim models system frequency with one centre-of-inertia (COI) swing equation. A power imbalance `deltaP` moves
frequency at a rate set by the stored kinetic energy `E` of the synchronous fleet; load damping, governor droop,
recorded response services, an optional fast-response device, an optional grid-forming virtual former and an
optional authored inverter fleet add power terms that arrest the fall. This chapter specifies the equation as
implemented, how `E` is built from the generator list, the integration method and step, every `DynamicsConfig`
field, the response-service delivery curves, the grid-forming kernel, the distributed multi-domain model, the
inertia inversion, the GB limit classifiers and the NESO trace path. It states which constants are fitted, which are
not, and which numbers each of them can and cannot move.

The code is under `src/GridSim.Core/Dynamics/`, `src/GridSim.Core/Frequency/` and `src/GridSim.Core/Simulation/LiveSim.cs`.
The user-facing verbs (`freq`, `replay`, `trip`, `transient`, `domains`) are in
[manual ch. 14, Frequency studies](<../manual/0014 Frequency studies.md>). Where the inertia numbers of a real fleet come
from is in [ch. 13, Asset-dynamics data grounding](<0013 Asset-dynamics data grounding.md>); how the cascade model
was blind-validated is in [ch. 14, Blind cross-event validation of the asset-dynamics cascade](<0014 Cascade validation and blind prediction.md>);
the detailed machine, governor and exciter models are in
[ch. 24, Standard dynamic-model library](<0024 Standard dynamic-model library.md>). None of those is repeated here.

## Model

### The swing equation

Per integration step, with `df = f - f0` and every power in MW (negative `deltaP` is a loss of infeed):

```
rocof      = f0 / (2 * E) * pImbalance                                   Hz/s
pImbalance = pEvent + pStanding + pGov + pTsgb + pServices - d * df
f(next)    = f + rocof * dt                                              explicit Euler
```

| Term | Meaning | Definition | Unit |
|---|---|---|---|
| `f0` | Nominal frequency | `DynamicsConfig.NominalHz` | Hz |
| `E` | Stored kinetic energy of the system | `max(1.0, systemInertiaMwS + DeviceRotationalInertiaMwS)` | MW.s |
| `pEvent` | Imposed imbalance | sum of `FrequencyEvent.DeltaPMw` over every event with `AtSeconds <= t + 1e-6 * dt` (the tolerance absorbs the rounding of `t = k * dt`, as the RMS stage times do); steps are cumulative and permanent | MW |
| `pStanding` | Pre-event balance | 0 unless `InitialFrequencyHz` is set; then `-(pGov0 + sum(pSvc0) - d*df0)` | MW |
| `pGov` | Governor droop response | first-order lag toward `-(df/f0)/DroopPu * Presp` | MW |
| `pTsgb` | Device fast response | first-order lag toward the clamped, dead-banded fast-response target | MW |
| `pServices` | Recorded DC/DM/DR | sum of one lagged state per `SystemResponseService` | MW |
| `d * df` | Load damping | `d = DampingPctPerHz / 100 * baseDemandMw` | MW |

`Presp = ResponsiveFraction * baseDemandMw` is the capacity the droop term is scaled by. It is not a limit: the
governor target is `-(df / f0) / DroopPu * Presp`, a straight line through zero with no dead-band and no clamp, so
it equals `Presp` at `|df| = f0 * DroopPu` (2.5 Hz at the defaults) and keeps growing past it (a 5 Hz deviation
gives `2 * Presp`, 15,800 MW on the default case). A loss (`pEvent < 0`) drives `df` negative; the droop target and
the load-relief term `-d*df` are then positive and oppose the loss.

The lagged terms are advanced before `rocof` is evaluated in the same step, using the frequency at the start of
the step:

```
pGovTarget = -(df / f0) / DroopPu * Presp
pGov      += (pGovTarget - pGov) / max(dt, GovernorTimeConstantS) * dt

ffrTarget  = 0                                                   if FastResponseMw <= 0 or |df| <= FastResponseDeadbandHz
           = clamp(-(FastResponseMw / 0.5) * df, -FastResponseMw, +FastResponseMw)    otherwise
pTsgb     += (ffrTarget - pTsgb) / max(dt, FastResponseTimeConstantS) * dt

for each service s:
    pSvc[s] += (curve_s(df) - pSvc[s]) / max(dt, T_s) * dt        curve_s is defined under "Response services"
pServices   = sum of pSvc[s]
```

`0.5` in the fast-response gain is the full-delivery deviation in Hz, `SwingKernel.DeviceFullDeliveryHz`: the device
reaches `FastResponseMw` at 0.5 Hz deviation. Neither term has a knee. The governor is a straight line through zero
with no dead-band and no cap. The fast response is zero inside `FastResponseDeadbandHz`, a straight line outside it,
and clamped to `+/-FastResponseMw`; it is the only one of the two with a dead-band and a clamp.

At the step where an event first applies, every lagged state is still 0 and `df` is 0, so `pImbalance = pEvent`
exactly and the first RoCoF is `f0/(2E) * pEvent` (see "What sets the initial RoCoF and the nadir").

The load-damping term is physical: load falls as frequency falls, so `-d*df` is a positive power when `df < 0`.

### Inertia E

`E` is the sum of `H * MBase` over the generators that count as rotating mass, in MW.s. `H` is the inertia constant
in seconds on the machine's own base `MBase` (MVA), so `H * MBase` is that machine's stored energy. A generator whose
type the shared technology map (`PoleMap.ForType`) knows as converter plant (storage, battery or bess, wind, solar,
tidal, interconnector, hvdc, converter) contributes nothing, whatever `H` its data carries: such an `H` is a synthetic
inertia figure, and virtual inertia is not real mass. A unit of unknown or blank type keeps its `H`.

```
GridModel.SystemInertiaMwS  = sum over rotating-mass generators of (g.H * g.MBase)  MW.s   (fleet order)
GridModel.SystemInertiaGvaS = SystemInertiaMwS / 1000                                  GVA.s
systemInertiaMwS            = GridModel.SystemInertiaGvaS * 1000                       MW.s
```

(`src/GridSim.Core/Model/Model.cs`.) `Generator.H` defaults to 0 and `Generator.MBase` to 100, so a plant with
`H = 0` adds nothing, whatever its rating. The lumped and distributed callers that start from a `GridModel` pass
`SystemInertiaGvaS * 1000.0`; the divide and multiply by 1000 are part of the computation and every such route uses
the same pair. `trip` and `screen` read `SystemInertiaMwS` directly and take out the tripped unit's own share,
`GridModel.RotatingInertiaMwS(g)` (`H * MBase` for rotating mass, exactly 0 for a known converter technology), because a
tripped unit stops spinning. Every route therefore agrees on what inertia is.

Where `H` comes from for a GDA-built model is the representative value per fuel class in `GridSim.Gda/FuelMap.cs`:

| Class | H (s) | Class | H (s) |
|---|---|---|---|
| nuclear | 5.0 | biomass | 4.0 |
| ccgt | 5.0 | gas | 4.0 |
| ocgt | 4.0 | coal | 4.0 |
| pumped-storage | 3.0 | hydro | 3.0 |
| chp | 2.5 | recip | 1.0 |
| wind, solar, tidal, storage, interconnector, other | 0.0 | | |

The floor and the substitutions that guard `E`:

- **Batch and live integrators.** `E = max(1.0, system + device)`. A system with no inertia is treated as 1 MW.s,
  which is a numerical guard, not a physical claim. The floor does not apply under grid-forming; see below.
- **Device inertia.** `DeviceRotationalInertiaMwS` is real stored energy `(1/2)*J*omega^2` of an added device and is
  summed into `E`, so it cuts RoCoF exactly as the same MW.s of system inertia would. The device's fast response is
  a separate power injection and never changes `E`. The two device terms are never merged.
- **Live fallback.** `LiveSim` replaces `E` when the fleet carries no usable `H`. If `E < LiveSolveSettings.MinInertiaMwS`
  (100 MW.s), it uses `LiveSolveSettings.FallbackInertiaH * sum(MBase)` over the generators (`|Pg|` for a generator
  with `MBase <= 0`), with `FallbackInertiaH = 4.0` s; if that is still below 100 MW.s it uses
  `FallbackInertiaH * baseDemandMw`. The substitution is suppressed when `Forming != None`, because a low real
  inertia is then the case under study. The batch simulator and the `trip` verb have no fallback.
- **Virtual inertia.** `VirtualInertiaMwS` (`Ev`) is a control state. It enters only the grid-forming denominator and
  is never added to `E`.

### Integration method and step

Every COI path is a fixed-step explicit (forward) Euler integration of the lagged states and of `f`. There is no
adaptive step, no RK scheme and no iteration.

- **Batch** (`FrequencyDynamics.Simulate`): step `StepSeconds` (0.02 s, the cadence the NESO upsampler produces, so
  simulated and recorded traces share a timeline) for `round(DurationSeconds / StepSeconds)` steps, `k = 0..steps`
  inclusive, so `steps + 1` points. `t = k * dt`. `Points[k]` holds the frequency at the start of step `k` and the
  RoCoF computed in it. `SettleHz` is the frequency after the final update, one step past the last point.
- **Live** (`FrequencyIntegrator.Step(dtSeconds, eventMw)`): the caller's step is split into equal sub-steps:

  ```
  nSub = clamp(ceil(dt / min(dt, MaxInternalDt)), 1, MaxSubSteps)       MaxInternalDt = 0.02 s, MaxSubSteps = 4000
  h    = dt / nSub
  ```

  A step of 20 ms or less is one sub-step. A step above 80 s hits the 4000-sub-step cap, so the sub-step grows
  above 20 ms in proportion (a 1200 s step uses `h = 0.3 s`). The governor, fast-response and damping states are
  advanced per sub-step with `h` in place of `dt`. The droop equilibrium does not depend on `h`, so a clamped step
  settles where a fine sweep settles.
- **Grid-forming** (`Forming != None`): the load-damping term is integrated implicitly and the step is sub-divided
  by `FormingKernel.SubStepCount`; see the grid-forming section.
- **Distributed** (two or more swing masses): semi-implicit (symplectic) Euler on the same `StepSeconds`; see the
  distributed section.

Trajectory timestamps are synthetic, anchored at 2000-01-01T00:00Z. No simulator in this chapter reads the clock.

### Batch simulator and live integrators

`FrequencyDynamics.Simulate(systemInertiaMwS, baseDemandMw, events, config)` returns a `FrequencyResponse`. The
stateful integrators run the same equation one `Step` at a time for the live view
(`src/GridSim.Core/Simulation/LiveSim.cs`). Every centre-of-inertia engine (`FrequencyDynamics.Simulate`,
`AssetFrequencyDynamics.Simulate`, `FrequencyIntegrator`, `AssetAwareFrequencyIntegrator`, `FleetDynamics.Simulate`,
`MultiDeviceFrequencyIntegrator`, `MultiDomainSwing.Simulate`) takes its responder states and the order their powers
are added from one kernel, `SwingResponders` (`src/GridSim.Core/Dynamics/SwingResponders.cs`): the governor droop lag,
the TSGB device lag, each recorded service's lag and the equilibrium start at a pre-event frequency. Each engine keeps
only its own time loop, swing step and trace, so the live integrators reproduce the batch trace bit for bit when
stepped at the batch step under the same event power.

| Capability | `FrequencyDynamics.Simulate` | `FrequencyIntegrator` | `AssetAwareFrequencyIntegrator` |
|---|---|---|---|
| Step | `StepSeconds`, fixed | caller `dt`, split to at most 20 ms (at most 4,000 sub-steps) | same as `FrequencyIntegrator` |
| Governor, damping, device FFR | yes | yes | yes |
| `SystemResponseServices` | yes | yes | yes |
| `InitialFrequencyHz` (equilibrium start) | yes | yes | yes |
| Base demand | as given | as given | as given |
| Grid-forming | yes | yes | yes |
| `pAssetFleet` | `AssetFrequencyDynamics.Simulate` | no | yes |

Every engine refuses a negative or non-finite demand and a non-finite or non-positive `NominalHz` or
`InitialFrequencyHz` (`ArgumentOutOfRangeException`). A live `Step` refuses a step that is not finite and positive, or
an event power that is not finite (`SwingKernel.RequireLiveStep`). `LiveSim` floors its own demand at 1 MW before it
builds an integrator. `MultiDomainSwing` refuses a grid-forming config (`MultiDeviceFrequencyIntegrator` runs the forming step
since N291).

`IFrequencyIntegrator` (`FrequencyHz`, `RocofHzPerS`, `Step`) is the contract `LiveSim` drives. `LiveSim` builds the
asset-aware integrator only when `EnableAssetDynamics` is on and the model binds an asset-dynamics bundle or
protections; otherwise it builds the plain `FrequencyIntegrator`. `AssetFrequencyDynamics.Simulate` is the batch
twin of the asset-aware integrator.

`FrequencyResponse`:

| Field | Meaning |
|---|---|
| `InitialRocofHzPerS` | The largest-magnitude RoCoF over all steps (the post-event spike), signed. |
| `NadirHz`, `NadirSeconds` | The lowest frequency and the time `t` of the point that holds it. |
| `SettleHz` | The frequency after the final update. It is the value at the end of the window, not an equilibrium. |
| `Points` | The trajectory as `FrequencyPoint(Time, Hz, RocofHzPerS)`. |

`SwingKernel` (`GovernorTarget`, `FastResponseTarget`, `SwingInertia`, `SortedEvents`, `ActiveEventMw`,
`BatchStepCount`, `LiveSubStepCount`) holds the targets and step rules every engine shares; `SwingResponders` calls
`GovernorTarget` for the governor and `FastResponseTarget` for the services, and `FleetUnitBank`
(`src/GridSim.Core/Fleet/FleetUnitBank.cs`) holds the fleet unit loop (deadband, lag, stagger arming) of the two fleet
engines. A fleet unit re-arms its stagger once the frequency has stayed inside its deadband for that unit's own
stagger, so every later excursion meets the detection latency again. Decision (GR-843, October 2026): the arming and
re-arming times are compared with the clock to a millionth of a step, the rule the RMS and EMT event times use, so a
stagger meant to land on the step grid (0.1 s + 0.3 s against 20 x 0.02 s) arms on that step, not one step late.

The model-based entry points (`LumpedScalarInertiaModel`, `DistributedInertiaModel`, `AssetFrequencyDynamics`,
`RocofScreening.Screen`) resolve their config through `SwingKernel.ConfigForModel`: no config gives
`DynamicsConfig { NominalHz = model.FrequencyHz }`, and a config whose `NominalHz` is not exactly the model's
frequency is refused (`ArgumentException`).

### LiveSim: how an event reaches the integrator

Each live tick calls `_freqSim.Step(dtSeconds, ambient + tripDeficit)`:

```
ambient      = (u - 0.5) * 2 * AmbientNoiseFraction * baseDemandMw         u from a System.Random seeded with LiveSimSeed
tripDeficit *= exp(-dtSeconds / 300)           (SecondaryTauS = 300 s); set to 0 when |tripDeficit| < TripDeficitZeroMw (0.5 MW)
```

`LiveSolveSettings.AmbientNoiseFraction` is 0.0008 and `LiveSolveSettings.LiveSimSeed` is 20260605, so a live
session is reproducible. `LiveSim.TripInfeed(lostMw)` subtracts `|lostMw|` from the deficit (negative = loss) under a
lock and folds it in at the next step; trips accumulate. A reclosed or islanded topology contributes the net lost
load minus lost generation of the blacked-out islands as a one-off imbalance. With asset dynamics on, the
asset-aware integrator restores the fleet's trips and protection sheds with the same 300 s time constant
(`secondaryRestorationTauS: SecondaryTauS`): the tripped units stay tripped (`AssetTrippedMw` still reports them), and
what recovers is the balance as the rest of the system is redispatched. The live loop runs at the case's own nominal
frequency and grades against `FrequencyBands.ForModel(model)`. The `transient` verb integrates frequency through this
same path at one step per slice.

### Response services

`SystemResponseService(Mw, DeadbandHz, FullDeliveryDeviationHz, TimeConstantS, KneeDeviationHz = 0, KneeFraction = 0)`
is one recorded frequency-response service as the swing model integrates it. Each is an independent first-order
responder: the target is the delivery curve at the present `df`, the state lags it with `max(dt, TimeConstantS)`,
and the states sum into `pServices`. These model the response the real system held, not a device. The replay
keeps them where it strips device terms and the counterfactuals keep them too, so a device is credited only for what
it adds to reality.

The delivery curve (`SwingKernel.FastResponseTarget`, full form), with `a = |df|`:

```
a <= DeadbandHz                           target = 0
KneeDeviationHz <= 0                      target = -sign(df) * min(Mw * a / FullDeliveryDeviationHz, Mw)       line through zero
a <= KneeDeviationHz                      mag = Mw * KneeFraction * (a - DeadbandHz) / max(1e-9, KneeDeviationHz - DeadbandHz)
a >  KneeDeviationHz                      mag = Mw * (KneeFraction + (1 - KneeFraction) * (a - KneeDeviationHz) / max(1e-9, FullDeliveryDeviationHz - KneeDeviationHz))
otherwise                                 target = -sign(df) * min(mag, Mw)
```

The curve is symmetric: a negative `df` (under-frequency) gives positive injected power, a positive `df` gives the
mirror. With the knee at the dead-band and a fraction of 0 the curve is a ramp from the dead-band to full output
(the DM and DR shape).

`GbResponseServices.Typical(scale = 1.0)` (`src/GridSim.Core/Dynamics/GbResponseServices.cs`) is the GB arrest layer
at grounded volumes:

| Service | Mw | Dead-band (Hz) | Knee (Hz, fraction) | Full (Hz) | Time constant (s) | Delivery at selected deviations |
|---|---|---|---|---|---|---|
| DC, Dynamic Containment | 950 | 0.015 | 0.2, 0.05 | 0.5 | 0.5 | 21.82 MW at 0.1 Hz, 47.5 MW at 0.2 Hz, 498.75 MW at 0.35 Hz, 950 MW at 0.5 Hz |
| DM, Dynamic Moderation | 520 | 0.1 | 0.1, 0.0 | 0.2 | 2.0 | 0 at 0.1 Hz, 260 MW at 0.15 Hz, 520 MW at 0.2 Hz |
| DR, Dynamic Regulation | 490 | 0.015 | 0.015, 0.0 | 0.2 | 10.0 | 225.14 MW at 0.1 Hz, 245 MW at 0.1075 Hz, 490 MW at 0.2 Hz |

The curve parameters and time constants are literals in `GbResponseServices.Typical`. The only test that
asserts any of them is `GbResponseServicesTests.Grounded_volumes_match_the_eac_clears`: `Mw` of all three services and
`FullDeliveryDeviationHz` of DC and DM. The dead-bands, knees, knee fractions, DR full-delivery deviation and the
time constants are pinned by no test.

The volumes are the NESO EAC auction clears held each direction (DC 944 to 993 MW, DM 515 to 547 MW, DR 480 to
500 MW; the constants are the nominal 950, 520 and 490). The curve shapes are the NESO published service terms. The
three time constants are literature-typical and carry `[VERIFY]` (an unconfirmed figure; see
[ch. 8, Provenance and invariants](<0008 Provenance and invariants.md>)). A per-event replay uses the volumes the
system held at that event, not `Typical`. `GridEvent.ApplyRecordedServices` copies an event's services into the
config, sets `InitialFrequencyHz` to the recorded pre-event frequency and, when the event marks its service list
complete, sets `ResponsiveFraction = 0` so the generic governor does not double-count the recorded response.

**Equilibrium start.** With `InitialFrequencyHz` set, `Simulate` starts at that frequency. It pre-loads the governor and
each service state to their static targets at `df0 = InitialFrequencyHz - f0`, and sets the constant `pStanding` so
that the pre-event derivative is exactly 0. Under grid-forming the standing term balances `d + FormingDampingMwPerHz`,
the damping the forming step integrates against. Device terms start undeployed: a counterfactual device begins at zero.

### Grid-forming formation

`Forming` selects how the frequency is built (`FormingMode`: `None`, `Vsm`, `Droop`). `None` runs the explicit
Euler COI loop above verbatim. Under `Vsm` or `Droop` the grid frequency is the frequency of a virtual former locked
to the real synchronous mass. The unified swing is

```
(2 * (E + Ev) / f0) * df/dt = pForcing - Dtot * (f - f0)

E      = systemInertiaMwS + DeviceRotationalInertiaMwS                  real stored energy, no floor
Ev     = VirtualInertiaMwS                                              virtual control inertia, MW.s
Dtot   = d + FormingDampingMwPerHz                                      load damping + former damping Dv
pForcing = pEvent + pStanding + pGov + pTsgb + pServices + FormingSetpointMw
         (asset-aware paths append  + pAssetFleet - tripDeficit)
```

`pForcing` holds every term not proportional to `(f - f0)`; the proportional damping is integrated implicitly
(`FormingKernel.SemiImplicitStep`, A-stable on that term):

```
gain      = f0 / (2 * Eform)                          Eform = E + Ev
rocof     = gain * (pForcing - Dtot * (f - f0))       the start-of-step value, which is what is reported
a         = h * gain
f(next)   = (f + a * (pForcing + Dtot * f0)) / (1 + a * Dtot)
```

The governor, fast response and services stay explicit inside `pForcing`, so fast high-gain responders at small
`Eform` need a smaller step:

```
hMax   = min(MaxInternalDt, MaxStepGain / gain)             MaxInternalDt = 0.02 s, MaxStepGain = 0.02
nSub   = clamp(ceil(dt / min(dt, hMax)), 1, MaxSubSteps)    MaxSubSteps = 4000
h      = dt / nSub
```

`FormingKernel.EffectiveFormingInertiaMwS` returns `E + Ev` and throws `InvalidOperationException` when it is not
greater than 0 (zero real and zero virtual inertia form nothing); there is no `Math.Max(1, E)` floor on this path.
The kernels run `Vsm` and `Droop` through the same equation. A droop former is a pure P-f droop with `Ev = 0`, and
`SwingKernel.SwingInertia` enforces it: `Forming = Droop` with any `VirtualInertiaMwS` other than 0 (NaN included) throws
`ArgumentException`, which names `Vsm` as the form that carries inertia. Every engine of the swing family takes its
forming denominator through that one gate. The CLI rejects `--forming vsm` without
`--forming-inertia` greater than 0 (exit 2); `--forming-inertia` is given in GVA.s and converted to MW.s, and
`--forming-damping` is in MW/Hz.

`FormingAggregate.FromFleet` re-interprets a grid-forming asset class as a former: `Ev = sum(SynInertiaMwsPerHz * f0 / 2)`
and `Dv = sum(DampingMwPerHz)` over the grid-forming units. The re-interpretation is exact: moving the injection
`-SynInertia * df/dt` to the left of `(2E/f0) df/dt = P` gives `2*(E + SynInertia*f0/2)/f0`. It idealises away the
state-of-charge and headroom cap that the fleet-injection path enforces. The H=0 counterfactual study uses it.

### Per-asset fleet term: `pAssetFleet`

`AssetFrequencyDynamics.Simulate` is the batch loop with one extra pair of terms appended last in the sum, so that
an empty or passive fleet adds `0.0` and leaves the trajectory bit-identical:

```
pBase      = pEvent + pStanding + pGov + pTsgb + pServices - d * df
rocofPred  = f0 / (2 * E) * pBase                                       predictor
pImbalance = pBase + pAssetFleet - assetTripDeficit
rocof      = f0 / (2 * E) * pImbalance
```

`AssetDynamicsFleet.StepCoi` advances every bound controller against the COI frequency, the predicted RoCoF and the
accumulated phase `phaseDeg += 360 * df * dt`. `pAssetFleet` is the sum of the controllers' active power. A
grid-forming unit (`GridFormingInverter`) delivers

```
target = clamp(-SynInertiaMwsPerHz * rocof - DampingMwPerHz * dfActive, -capDown, +capUp)       dfActive = df outside its dead-band, else 0
p     += (target - p) / max(dt, actuateTau) * dt
```

so its synthetic-inertia term lowers the initial RoCoF as a power injection while `E` is untouched. A grid-following
unit trips on local RoCoF held past a confirmation time, on vector shift or on low voltage; its lost dispatched `Pg`
joins `assetTripDeficit`, which is cumulative and never decays inside the integrator. The live asset-aware
integrator is given the per-bus voltage and loading of the last solve for the voltage and thermal protections. Under
grid-forming the predictor uses the unfloored `Eform`.

`EnableAssetDynamics` defaults off. The asset-control parameters are mostly curated placeholders. In
`data/gb-synthetic/asset-classes.json` each class has a `_default` provenance of `CuratedPlaceholder` whose note
carries `[VERIFY]`, and a parameter without its own entry takes that default. The explicit entries are:

| Class | Parameter | Tag |
|---|---|---|
| `gfl-wind-2024` | `rocofTripHzPerS` (1.0), `deadbandHz` (0.015) | `Recorded` |
| `gfl-solar-2024` | `rocofTripHzPerS` (1.0) | `Recorded` |
| `legacy-dg-old-lom` | `rocofTripHzPerS` (0.125) | `Recorded` |
| `dg-alomcp` | `rocofTripHzPerS` (1.0) | `Recorded` |
| `legacy-dg-old-lom` | `vectorShiftTripDeg` (6.0) | `CuratedPlaceholder`, `[VERIFY]` |
| `gfm-bess-2024`, `gfm-vsm-study-2024` | `synInertiaMwsPerHz` | `CuratedPlaceholder`, `[VERIFY]` |
| `prot-lom-legacy` | `rocofTripHzPerS` (0.125) | `Recorded` |
| `prot-lom-legacy` | `localRocofUpliftFactor` (1.16) | `DerivedFromRecorded` |
| `prot-lfdd-stage1` | `lfddStageHz` (48.8) | `Recorded` |
| `prot-lfdd-stage1` | `lfddBlockMw` (632) | `DerivedFromRecorded` |

Every other parameter of the six inverter classes (droop gains, latencies, actuator time constants, headrooms,
slew rates, LVRT thresholds, the GFM inertia and damping gains, the `gfm-bess-2024` and `gfm-vsm-study-2024` RoCoF
trips, the `deadbandHz` of every class except `gfl-wind-2024`) is `CuratedPlaceholder` with `[VERIFY]`. The two
protection classes carry no `_default`: a parameter of theirs with no entry (`rocofTripConfirmS`, `lfddDwellS`)
takes the library's own fallback, `CuratedPlaceholder` with a `[VERIFY] unspecified provenance` note. A claim about a real fleet is
therefore an `AssetEnvelope` sweep tagged `UnvalidatedAtScale`, checked against a recorded trace by
`AssetValidation`, never a point curve. How classes are authored is in
[manual ch. 29, Extending GridSim](<../manual/0029 Extending GridSim.md>).

### The distributed multi-domain model

`src/GridSim.Core/Dynamics/Distributed/` resolves the swing into one mass per speed domain, coupled across the
network. It is opt-in (`InertiaModel.Distributed`) and its outputs carry the `UnvalidatedAtScale` provenance tag
([ch. 8, Provenance and invariants](<0008 Provenance and invariants.md>)).

**Speed domains.** `DomainAssignment.Assign(model, config)` groups the generators by `(poles, kind)`. `PoleMap.ForType`
maps the type string; rpm is `120 * f / poles` at the model's own frequency `f` (the speeds below are at 50 Hz; a 60 Hz
two-pole unit is 3600 rpm):

| Type | Poles, speed | Kind |
|---|---|---|
| nuclear, coal, biomass, ccgt, ocgt, chp, gas | 2 pole, 3000 rpm | `Synchronous` |
| recip | 4 pole, 1500 rpm | `Synchronous` |
| hydro, pumped-storage, pumped | 8 pole, 750 rpm | `Synchronous` |
| storage, battery, bess | none | `SyntheticInertia` |
| wind, solar, converter, interconnector, tidal, blank, unknown | none | `Pll` |

The mapping is a modelling assumption: GDA supplies only the type label. A machine with `H > 0` whose type is
unknown or blank becomes a 2-pole synchronous unit. A named converter technology (wind, wind-onshore, wind-offshore,
onshore, offshore, solar, pv, tidal, interconnector, hvdc, converter: `PoleMap.MachineSpeed.IsKnownType`) never becomes
a swing mass, whatever its `H`.
`DistributedConfig.SingleDomain` places every machine in one 2-pole synchronous domain. Domains are numbered in
ascending `(poles, kind)` order. Each domain aggregates:

```
E_d    = sum(H * MBase)                       MW.s
S_d    = sum(MBase)                           MVA
GVad_d = sum(MBase * C)                       C = clamp(|Pg| / MBase, 0, 1); 0 when MBase <= 0
```

`C` is a dispatch-derived proxy for the availability and coupling coefficient. A domain is a swing mass
(`SpeedDomain.IsSwingMass`) only when it is `Synchronous` with `E_d > 0`; the others count in dominance only.

**Dominance.** `DominanceReport` orders the domains by `GVad` descending and computes

```
deltaGVa = (GVad_1 - GVad_2) / GVad_2
```

A dominant domain exists when `deltaGVa >= DominanceThreshold`. When the runner-up or the only domain has `GVad = 0`,
`deltaGVa` is `+infinity` and the top domain is dominant. The default threshold of 0.10 is an engineering-judgement
value anchored on data: over 103,433 GB samples (GDA `Derived/speed_domains`, 2014 to 2026) the dominance gap has a
10th percentile of 0.106, so 0.10 flags the bottom decile of conditions (it fires on 9.4 % of samples; a threshold
of 0.08 fires on 7.5 % and 0.12 on 11.4 %). `DominanceThreshold` is `init`-settable.

**The swing.** `MultiDomainSwing.Simulate` integrates one equation per swing mass `i` over `n` masses:

```
fCoi        = sum(E_i * f_i) / sum(E_i)            dfCoi = fCoi - f0
pResponse   = pStanding + pGov + pTsgb + pServices - d * dfCoi            all keyed off the COI, exactly as the lumped model
share_i     = E_i / sum(E_j)
tie_i       = sum over j != i of K_ij * (delta_i - delta_j(t - tau_ij))   MW
fi          = eventShare_i * pEvent + share_i * pResponse - tie_i          eventShare from the event placement (below)
rocof_i     = f0 / (2 * E_i) * fi
f_i        += rocof_i * dt                          frequency first
delta_i    += 2 * pi * (f_i - f0) * dt              then angle, with the updated frequency
```

`delta` is in rad and `K` in MW/rad. The inertia of the device (`DeviceRotationalInertiaMwS`) is folded into the
masses in proportion so the total equals the lumped `E`. The governor, fast response, services and standing term
use the same responder kernel as the lumped loop, evaluated on `dfCoi`.

**Where an event lands.** `FrequencyEvent(AtSeconds, DeltaPMw, AtBus)` carries an optional bus; only the distributed
model reads it (the COI engines give identical numbers either way). `DistributedInertiaModel` places every event on the
swing masses as a vector of shares (`EventPlacement` in `DistributedResult.EventPlacements`, each with its rule in
words), and the swing adds `share_i * pEvent` to domain `i`:

- At a bus that hosts swing-mass machines: wholly on the domain with the most inertia at that bus.
- At any other bus: shared by synchronising coupling to the impact bus (`DomainCoupling.ImpactShares`: a Kron
  reduction onto the machine buses plus the impact bus, shares proportional to `BaseMva * Im(Y_red[impact, g])` summed
  per domain), the distribution of a power impact by synchronising power coefficients.
- With no bus: wholly on the domain of the single largest unit (the largest `MBase` among the swing-mass members; the
  first in domain, then member order, on a tie), the credible-loss home.

An unknown bus is refused (`ArgumentException`), and a bus with no path to any machine fails loud
(`InvalidOperationException`). `trip --inertia-model distributed` places the loss at the tripped unit's bus and prints
a `Loss placed:` line.

**Coupling.** `DomainCoupling.SynchronisingMatrix` Kron-reduces the bus admittance matrix onto the generator buses
(`KronReduction`) and reads the cross-domain synchronising stiffness

```
K_ij = BaseMva * Im(Y_reduced[a,b])        flat 1 pu voltage; summed over bus pairs a in domain i, b in domain j; entries <= 0 ignored
```

Each generator bus belongs to the domain holding the most inertia there. Buses with no admittance path to any kept
generator bus (an isolated bus, an island with no swing-mass machine) are left out of the reduction; they cannot change
a reduced value. `DomainCoupling.Synchronising` returns the matrix with a `CouplingReport`: its `CouplingSource`
(`Kron`, `UniformNoBranchReactance`, `UniformReductionFailed`, `UniformNegligibleCoupling`, `NotNeeded`), the reason,
the count of buses dropped and the count of cross-domain pairs whose reduced coefficient was zero or negative (left out
of `K`). `DistributedResult.Coupling` carries it; the divergence report and `trip --inertia-model distributed` (a
`Coupling:` line) say so only when the coupling is not network-derived. If no branch has `|X| > 1e-12`, if the
reduction fails, or if the summed stiffness is not above `1e-9`, the model uses the uniform stiffness

```
K_uniform = omega^2 / (pi * f0 * sum(1 / E_d))        omega = 2 * pi * TargetInterAreaHz       f0 = GridModel.FrequencyHz
```

which places the single inter-area mode at `TargetInterAreaHz`. `K` is symmetric.

**Propagation (off by default).** `PropagationDelay.ComputeWithLocation` returns `tau = 0` unless `EnablePropagation` is
true, there are at least two masses, and the buses carry map coordinates. A bus at exactly `(0, 0)` has no position: it
is left out of its domain's centroid, and a domain with no located member gets zero first-order delay (a supplied `tau`
still applies) and is listed in `PropagationReport.UnlocatedDomainIds`. Then:

```
centroid_d = mean of (Bus.X, Bus.Y) over the domain's located generator buses; distance scales X by 660 km and Y by 1020 km
density    = sum(E_d) / maxCentroidDistanceKm                                         MW.s per km
v          = clamp(1000 * sqrt(200 / max(density, 1e-6)), MinWaveSpeedKmPerS, MaxWaveSpeedKmPerS)     km/s
tau_ij     = distance_ij / v       unless ProvidedTauSeconds holds the pair of representative buses (each domain's highest-inertia bus)
```

In the swing loop, tie `i-j` reads the far domain's angle `round(tau / dt)` steps back from a per-step angle history. The
delay is `0` for a negative, NaN or infinite `tau`, and is clamped to the run length. `tau = 0` reproduces the
undelayed model exactly. The model asserts only that the wave speed lies in the band of `MinWaveSpeedKmPerS = 500` to
`MaxWaveSpeedKmPerS = 1500` km/s; the absolute value is not validated. The arrival time of a domain is the first step
time at which `|f_i - fStart|` exceeds 1 mHz, where `fStart = InitialFrequencyHz ?? f0`; -1 means it never did.

**Outputs.** `DistributedInertiaModel.Simulate` returns the COI-collapsed `FrequencyResponse` so every scalar
consumer works with either model, and leaves the spatial detail in `LastDistributedResult`: one `DomainTrace` per
swing mass, the `DominanceReport`, a per-bus event-RoCoF map (each generator bus gets its domain's peak RoCoF) and,
when propagation is on, a `PropagationReport` (wave speed, largest delay, per-domain arrival times).

**Delegation.** Two cases never enter `MultiDomainSwing` and call the lumped `FrequencyDynamics.Simulate` with
`SystemInertiaGvaS * 1000`:

- Any run with `Forming != None`, whatever the domain count (the multi-domain swing has no virtual former).
- Any model with at most one swing mass, including `SingleDomain = true`. The result is bitwise the lumped result.

**Validation status.** The distributed model passes its reduction gates (below). It has no field validation: the
replay test that scores the distributed COI against a recorded event uses a synthetic, self-consistent event produced
by the lumped model, and the per-domain spread and propagation need PMU or DFR data that the repository does not hold.
`DivergenceReport` states the divergence between the lumped and distributed results, names its mechanism and tags it
`UnvalidatedAtScale`; it makes no accuracy claim.

### The inertia-model seam

`IInertiaModel` (`src/GridSim.Core/Abstractions/IInertiaModel.cs`) is the swappable frequency model, parallel to
`IPowerFlowSolver`:

- `Simulate(model, baseDemandMw, events, config)` returns the COI-collapsed `FrequencyResponse`;
- `CoiInertiaGvaS(model)` returns the aggregate every scalar readout uses (`SystemInertiaGvaS` for both models);
- `LastDistributedResult` is the spatial detail of the last solve, `null` for the lumped model.

`InertiaModelSelector.Create` resolves `DynamicsConfig.InertiaModel` (`LumpedScalar`, the default, or `Distributed`).
There is no shape-based auto-selection: the model is an explicit choice. `LumpedScalarInertiaModel.Simulate` is
`FrequencyDynamics.Simulate(model.SystemInertiaGvaS * 1000, ...)`.

Under `--inertia-model distributed` only the `trip` verb integrates the distributed model, on the post-trip network;
the half-inertia comparison it prints runs the lumped model. `transient` reports the speed-domain split on stderr and
integrates the lumped live model; `domains` reports the split from `DomainAssignment` and integrates nothing; `evidence`,
`scenario`, `h0-study`, `fleet` and `replay-event` run the lumped model and print a warning. `--forming` with
`--inertia-model distributed` runs the lumped forming integrator and prints a warning. The `trip` verb applies
`--asset-dynamics` before the distributed model: with asset dynamics on and a bundle or protections bound, the lumped
asset loop runs.

### Inverting the swing equation

`InertiaEstimator` recovers inertia from an observed excursion:

```
E = f0 * |deltaP| / (2 * |RoCoF|)           MW.s
```

- `EstimateInertiaMwS(deltaPMw, rocofHzPerS, nominalHz = 50)` ignores both signs and returns `+infinity` when
  `|RoCoF| < 1e-9`;
- `EstimateInertiaGvaS` divides by 1000;
- `EstimateHSeconds(deltaPMw, rocofHzPerS, baseMva)` returns `E / baseMva`, or NaN for `baseMva <= 0`;
- `PredictRocof(inertiaMwS, deltaPMw)` is the forward check, with `E` floored at 1 MW.s.

The estimate is clean because the first RoCoF sample precedes governor and fast-response action: at that instant the
only imbalance is `deltaP`.

### GB limits and assessment

`FrequencyLimits` (`src/GridSim.Core/Dynamics/FrequencyLimits.cs`):

| Constant | Value | Meaning |
|---|---|---|
| `OperationalLowHz`, `OperationalHighHz` | 49.8, 50.2 | NESO operational band |
| `StatutoryLowHz`, `StatutoryHighHz` | 49.5, 50.5 | statutory band |
| `LfddFirstStageHz` | 48.8 | first stage of low-frequency demand disconnection |
| `RocofLimitLegacyHzPerS` | 0.125 | the pre-ALoMCP loss-of-mains relay setting |
| `RocofLimitModernHzPerS` | 1.0 | the GB relay setting after retuning |
| `RocofLimitHzPerS` | 0.125 | back-compatible alias, pinned to legacy |
| `ActiveRocofLimitHzPerS` | 1.0 | read-only; the limit the "active" flag scores against when the caller passes none |

`BreachesRocof(r, limit)` is `|r| > limit + 1e-9`. An assessment reports the legacy and modern breaches
independently plus an active flag. The retained `RocofExceedsLimit` and `RocofLimitExceeded` fields keep legacy
semantics.

- `Assess(FrequencyResponse, activeLimit, bands)` returns the headline RoCoF, nadir, settle and peak
  (`FrequencyResponse.PeakHz`), the flags `BelowOperational` (nadir below the operational low edge), `BelowStatutory`
  (below the statutory low edge), `LfddAtRisk` (nadir at or below the LFDD edge), `AboveOperational` and
  `AboveStatutory` (peak strictly above the high edges), and a one-line summary from the ladder LFDD, below statutory,
  above statutory, active RoCoF, legacy RoCoF, below operational, above operational, within limits.
- `AssessTrace(samples, activeLimit, bands)` scans a measured trace: extremes, the sample counts below the operational
  and statutory floors and above the operational and statutory ceilings (`SamplesAboveOperational`,
  `SamplesAboveStatutory`), whether the LFDD edge was reached, and the worst sample-to-sample `|df/dt|` against the
  three thresholds.

The edges come from a `FrequencyBands` set (`src/GridSim.Core/Dynamics/FrequencyBands.cs`). `FrequencyBands.Gb` holds
exactly the constants above; `FrequencyBands.ForNominal(f0)` (and `ForModel(model)`) moves every GB edge by the same
number of hertz as the nominal, so a 60 Hz case is graded against 59.8 / 60.2, 59.5 / 60.5 and 58.8 Hz. At exactly
50 Hz it returns `Gb` itself. The overloads without `bands` use `Gb`. The RoCoF thresholds are relay settings in Hz/s and
do not move with the nominal. `NormalBandLowHz` and `NormalBandHighHz` (49.9, 50.1) are the normal band the SCADA view
alarms on.

### The NESO trace path

`FrequencySample(Time, Hz)` is one recorded reading and `FrequencyPoint(Time, Hz, RocofHzPerS)` is a point on the fine
timeline (`src/GridSim.Core/Frequency/FrequencyTypes.cs`).

`NesoFrequencyCsv.Read` streams a NESO `dtm,f` CSV one row at a time, so a month of 1 s data (2.7 million rows) reads
without loading into memory. Blank lines and one header line are skipped; any other line with no comma, an unparseable
timestamp or an unparseable number is refused with `InvalidDataException`, naming the file and the line number.
Timestamps land in UTC: an offset-carrying stamp is converted and a naive stamp is assumed UTC. The replay validator
compares recorded times with the UTC event instant by raw ticks, so a local-time stamp would drift by the offset in
BST and could demote a `Reproduced` baseline to `Marginal`. Every row passes `ForwardInferenceGuard.RejectFuture`, so the
reader cannot ingest a sample newer than the historical horizon.

`RocofUpsampler`:

- `Upsample(samples, subSamplesPerInterval = 50)` takes each pair of consecutive samples with `dt > 0`, computes
  `RoCoF = (f2 - f1) / dt` and emits `sub` points `f(t) = f1 + RoCoF * t` at `dt / sub` spacing (50 per 1 s interval, so
  20 ms). A closing point repeats the last sample with RoCoF 0.
- `Rocof(samples)` is the per-interval RoCoF series, stamped at the later sample.
- `Summarize(samples)` returns `FrequencyStats` (count, min, mean, max, worst `|RoCoF|` and when).
- All three (and `FrequencyLimits.AssessTrace`) refuse a sample stamped before its predecessor (`InvalidDataException`,
  GR-572); a repeated time stamp has no interval and is skipped.

`ReplayController` is the playback cursor over the fine timeline (`SlicesPerSecond` clamped to 0.25 to 2000, 50 is real
time). The CLI `freq` and `replay` verbs and `ReplayValidator`'s per-sample RMSE use this path
([ch. 7, The evidence engine](<0007 The evidence engine.md>)).

## Parameters and defaults

`DynamicsConfig` (`src/GridSim.Core/Dynamics/FrequencyDynamics.cs`) is an immutable record. Status is one of:
**firm** (a documented figure), **UNFITTED** (a literature-typical value not fitted to a measured trip; uncertainty is
a factor of two), **structural** (a mode or a physical quantity of the study, not a calibration), **opt-in** (default off).

| Field | Default | Unit | Status | Meaning |
|---|---|---|---|---|
| `NominalHz` | 50.0 | Hz | structural | Nominal frequency `f0`. |
| `DroopPu` | 0.05 | pu | firm | Governor droop, 5 %, the GB Grid Code figure. |
| `DampingPctPerHz` | 1.0 | % of demand per Hz | UNFITTED | Load self-damping; `d = value/100 * baseDemandMw`. |
| `ResponsiveFraction` | 0.20 | fraction of demand | UNFITTED | Share of demand carrying droop headroom; `Presp`. |
| `GovernorTimeConstantS` | 8.0 | s | UNFITTED | Primary-response lag. |
| `StepSeconds` | 0.02 | s | structural | Batch step; equals the NESO upsampler cadence. |
| `DurationSeconds` | 30.0 | s | structural | Batch window; the replay validator extends it. |
| `DeviceRotationalInertiaMwS` | 0.0 | MW.s | structural | Device `(1/2)*J*omega^2`, summed into `E`. |
| `FastResponseMw` | 0.0 | MW | structural | Device fast-response capacity; 0 disables it. |
| `FastResponseTimeConstantS` | 0.5 | s | structural | Device fast-response lag. |
| `FastResponseDeadbandHz` | 0.015 | Hz | structural | Device fast-response dead-band. |
| `InertiaModel` | `LumpedScalar` | enum | opt-in | `LumpedScalar` or `Distributed`. |
| `Distributed` | `null` | `DistributedConfig?` | opt-in | Distributed settings; `null` uses the defaults below. |
| `EnableAssetDynamics` | `false` | bool | opt-in | The `pAssetFleet` term and its trips. |
| `EnablePassivePlant` | `false` | bool | opt-in | Delayed OLTC taps and thermal derate in the live loop; adds no swing power. |
| `Forming` | `None` | enum | opt-in | `None`, `Vsm`, `Droop`. |
| `VirtualInertiaMwS` | 0.0 | MW.s | opt-in | Virtual former inertia `Ev`; forming denominator only. |
| `FormingDampingMwPerHz` | 0.0 | MW/Hz | opt-in | Former damping `Dv`, added to `d`. |
| `FormingSetpointMw` | 0.0 | MW | opt-in | Former deviation setpoint, a forcing term. |
| `SystemResponseServices` | empty | list | structural | Recorded DC, DM, DR the system held. |
| `InitialFrequencyHz` | `null` | Hz | structural | Pre-event frequency; `null` starts at `NominalHz` with all lag states 0. |

`SyntheticInertiaMwS` is an `[Obsolete]` alias that forwards to `DeviceRotationalInertiaMwS`.

`DistributedConfig` (`src/GridSim.Core/Dynamics/Distributed/DistributedConfig.cs`):

| Field | Default | Unit | Meaning |
|---|---|---|---|
| `SingleDomain` | `false` | bool | One coherent domain; with `EnablePropagation = false` this is `DistributedConfig.ReductionLimit`. |
| `EnablePropagation` | `false` | bool | Apply the inter-domain delay `tau`. |
| `DominanceThreshold` | 0.10 | ratio | `deltaGVa` at or above which a domain is dominant. |
| `MinWaveSpeedKmPerS` | 500.0 | km/s | Lower clamp of the wave speed. |
| `MaxWaveSpeedKmPerS` | 1500.0 | km/s | Upper clamp of the wave speed. |
| `TargetInterAreaHz` | 0.5 | Hz | Inter-area mode of the uniform fallback stiffness. |
| `ProvidedTauSeconds` | `null` | s | External `(fromBus, toBus)` delay map; `null` uses the first-order model. |

Constants in the swing loops and settings:

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| Inertia floor | 1.0 | MW.s | Floor on `E` outside grid-forming | `Math.Max(1.0, ...)` in the integrators |
| Device and curve full delivery | 0.5 | Hz | Deviation at which device fast response is full | `SwingKernel.DeviceFullDeliveryHz` |
| Largest sub-step | 0.02 | s | Live and forming sub-step ceiling | `FormingKernel.MaxInternalDt` (the swing kernel's sub-stepping uses it too) |
| Sub-step cap | 4000 | steps | Per-call cap | `MaxSubSteps` |
| Forming step gain | 0.02 | - | Cap on `h * f0 / (2 * Eform)` | `FormingKernel.MaxStepGain` |
| Live inertia floor | 100.0 | MW.s | Below this the live fallback applies | `LiveSolveSettings.MinInertiaMwS` |
| Live fallback H | 4.0 | s | H assumed on the fleet MVA | `LiveSolveSettings.FallbackInertiaH` |
| Ambient jitter | 0.0008 | fraction of demand | Live load-imbalance amplitude | `LiveSolveSettings.AmbientNoiseFraction` |
| Live RNG seed | 20260605 | - | Seed of the ambient jitter | `LiveSolveSettings.LiveSimSeed` |
| Trip recovery | 300 | s | Live trip-deficit decay time constant | `LiveSim.SecondaryTauS` |
| Trip clear | 0.5 | MW | Live deficit below this is zeroed | `LiveSolveSettings.TripDeficitZeroMw` |
| Arrival threshold | 1e-3 | Hz | Departure that marks a wave arrival | local constant in `MultiDomainSwing` |
| GB map spans | 660, 1020 | km | Scale of `Bus.X`, `Bus.Y` | `PropagationDelay.SpanXKm`, `SpanYKm` |
| Wave-speed reference | 1000 at 200 | km/s at MW.s per km | Inertia-density reference | `PropagationDelay.RefWaveSpeedKmPerS`, `RefInertiaDensityMwSPerKm` |

Fitted versus unfitted. `DroopPu` is firm. `DampingPctPerHz`, `ResponsiveFraction` and `GovernorTimeConstantS` are
UNFITTED: the bundled NESO sample (`data/neso/frequency-sample.csv`) is ambient normal-operation data, spanning
49.921 to 50.106 Hz with no trip in it, so it cannot pin them. They set the nadir depth, the swing
period and the recovery shape. The best and worst corners of the scenario engine bracket exactly these three.
`FrequencyConstantsFitter` fits them by least squares over the `FrequencyDynamics.Simulate` trajectory to a measured
loss-of-infeed trace with the lost infeed and the inertia known; `gridsim fit-frequency <trace.csv>` runs it
([CLI reference](<../manual/0019 CLI reference.md#fit-frequency>)). Damping alone is well identified from one trace; the
three together are weakly identified. No bundled trace contains a trip, so the shipped defaults remain unfitted.

## What sets the initial RoCoF and the nadir

**Initial RoCoF depends only on `E` and `deltaP`.** At the step where an event first applies the lagged states are
0 (or, with `InitialFrequencyHz`, cancel against `pStanding`) and `df*d` is 0 (or cancelled), so

```
rocof(0) = f0 / (2 * E) * deltaP
```

`DroopPu`, `DampingPctPerHz`, `ResponsiveFraction`, `GovernorTimeConstantS`, the device fast response and every
response service leave it unchanged. For the four governor and damping constants this follows from the equation
(each multiplies a state or a `df` that is 0 at the event step) and no test varies them. The tests pin the rest:
the formula to 3 decimals (`FrequencyDynamicsTests.Initial_rocof_matches_the_swing_equation`,
`FrequencyCalibrationTests.Representative_trip_rocof_is_physically_sensible`) and 4 decimals
(`FrequencyIntegratorTests.A_generation_loss_drops_the_frequency_with_the_expected_initial_rocof`); the device fast
response to 12 decimals (`DynamicsConfigInvariantTests.Fast_response_does_not_reduce_inertia`); the `Typical`
services to 6 decimals (`GbResponseServicesTests.The_arrest_layer_raises_the_nadir`). Three things do move it,
each through the denominator or the numerator and none through a response term: the size of `deltaP`; `E`, including
`DeviceRotationalInertiaMwS` and, under grid-forming, `Ev`; and, with `EnableAssetDynamics`, a grid-forming
unit's synthetic-inertia power. `InitialRocofHzPerS` is the largest-magnitude RoCoF over the run; for a single
step event it is the RoCoF of the event step.

**The nadir is shaped by the constants.** The inertia sets how quickly the first swing falls. The response then
builds with the governor lag. The default configuration (no dead-bands, no services, no device, no clamp reached) is
linear, so a step loss has a closed-form solution. With `x = df` and `pG` the governor state:

```
M * dx/dt  = deltaP - d*x + pG            T * dpG/dt = -K*x - pG
```

Eliminating `pG` gives the second-order system:

```
M  = 2 * E / f0                                                          MW.s per Hz
K  = Presp / (DroopPu * f0)                                              governor gain, MW per Hz
(M*s + d) * (1 + s*T) + K = 0     ->     M*T*s^2 + (M + d*T)*s + (d + K) = 0       T = GovernorTimeConstantS
omega_n = sqrt((d + K) / (M*T))        zeta = (M + d*T) / (2 * M * T * omega_n)
equilibrium |df| = |deltaP| / (d + K)                                    reached only if the swing has damped out
```

With `x(0) = 0` and `dx/dt(0) = deltaP / M`, and for `zeta < 1`, the step response is

```
alpha = (M + d*T) / (2*M*T)           omega_d = sqrt(omega_n^2 - alpha^2)
A     = deltaP / (d + K)              (the equilibrium offset, negative for a loss)
x(t)  = A + exp(-alpha*t) * (-A*cos(omega_d*t) + C*sin(omega_d*t))        C = (deltaP/M - alpha*A) / omega_d
```

Setting `dx/dt = 0` gives `tan(omega_d*t) = -omega_d / (1/T - alpha)`, so the first extremum (the nadir) is at
`t* = (pi - atan(omega_d / (1/T - alpha))) / omega_d` after the event, and later extrema follow at
`pi / omega_d` intervals. The worked example evaluates this for the default case and compares it with the run.

Raising `Presp` or lowering `DroopPu` raises `K` and lifts the nadir; raising `T` slows the arrest and deepens it;
`d` adds load relief in proportion to `df` with no lag (on the default case 395 of the 3,555 MW/Hz static stiffness, 11 %). Response services and the device fast response add a dead-banded term to the
numerator that acts within a second or two and lifts the nadir without changing the initial RoCoF. Halving `E` doubles
the initial RoCoF and deepens the nadir (the half-inertia lines of the `trip` capture below).

## Determinism

The COI frequency paths are written to give identical results on every run and every CPU, by construction. The
sections below separate what is bitwise by design, what is tested bitwise, what is not bitwise, and what no test
covers.

- **No clock, no random source.** The simulators read no clock (timestamps are anchored at 2000-01-01T00:00Z) and have
  no random source. The one random source in the area is the `LiveSim` ambient jitter, which uses a `System.Random`
  seeded with the constant `LiveSimSeed`.
- **Scalar arithmetic only.** The COI paths use IEEE double multiply, add, subtract, divide, `Math.Clamp`, `Math.Max`,
  `Math.Min`, `Math.Abs`, `Math.Sign`, `Math.Round` and `Math.Ceiling`. They call no `SimdKernels` routine, no
  `Math.FusedMultiplyAdd` and no parallel loop, so no instruction-set tier can change a result. The multi-domain loop
  adds `Math.Sqrt` in the propagation model, which is correctly rounded. `DomainCoupling` uses scalar
  `System.Numerics.Complex` arithmetic in the Kron reduction. The transcendental calls are `Math.Exp` in the
  `LiveSim` trip decay, which is the platform runtime's function, and `DetMath.Expm1` (deterministic) in the live
  asset-aware integrator's restoration.
- **Fixed order of evaluation.** Events are stably sorted by `AtSeconds` and summed in that order; domains are
  ordered by `(poles, kind)`; members keep generator order; the tie sum runs `j` ascending; services sum in list order.
- **Bitwise, and tested with exact equality.** Flag-off and empty-fleet paths add `0.0` (`x + 0.0 - 0.0 == x`)
  and the trajectories are compared point by point (`AssetDynamicsReductionTests`); the `None` path never reads the
  forming fields (`FormingFrequencyTests.None_path_ignores_the_forming_params_byte_identically`); the single-mass
  distributed model delegates to the lumped loop (`DistributedReductionTests`); routing through `IInertiaModel` is
  the direct call (`InertiaModelSeamTests`). `FrequencyExportTests.Export_is_deterministic` compares two exports built
  in the same process.
- **Not bitwise.** The multi-mass COI equals the lumped trajectory to rounding: the tests allow `1e-6` Hz on every
  trajectory point and, in `DistributedInvariantTests.Coi_trajectory_equals_the_lumped_model_to_machine_epsilon`,
  on the nadir, the settle value and the initial RoCoF. The single zero-latency asset unit matches a fleet unit to
  6 decimals (`AssetDynamicsReductionTests.Single_zero_latency_grid_following_unit_reproduces_a_fleet_unit`).
- **Untested.** No automated test compares two process runs, CPU tiers, operating systems or machines for these
  paths. The cross-CPU and cross-machine claim rests on the arithmetic above, not on a test
  ([ch. 8, Provenance and invariants](<0008 Provenance and invariants.md>)).

## Invariants

| Invariant | Pinned by |
|---|---|
| No event leaves frequency at nominal and RoCoF 0 | `FrequencyDynamicsTests.No_event_stays_at_nominal`, `FrequencyIntegratorTests.No_event_holds_at_nominal` |
| Initial RoCoF equals `f0 * deltaP / (2E)` | `FrequencyDynamicsTests.Initial_rocof_matches_the_swing_equation` (3 decimals), `FrequencyIntegratorTests.A_generation_loss_drops_the_frequency_with_the_expected_initial_rocof` (4 decimals), `FrequencyCalibrationTests.Representative_trip_rocof_is_physically_sensible` (3 decimals) |
| Lower inertia steepens RoCoF and deepens the nadir; frequency recovers above the nadir | `FrequencyDynamicsTests.Lower_inertia_steepens_rocof_and_deepens_nadir`, `FrequencyDynamicsTests.Frequency_recovers_above_the_nadir` |
| A surplus drives frequency up | `FrequencyDynamicsTests.Generation_loss_drives_frequency_down_load_gain_up` |
| `NominalHz`, `DroopPu`, `DampingPctPerHz`, `ResponsiveFraction` and `GovernorTimeConstantS` keep their documented defaults (9 decimals) | `FrequencyCalibrationTests.Documented_defaults_are_unchanged` |
| The ambient sample is inside the operational band with max `|RoCoF|` below 0.05 Hz/s | `FrequencyCalibrationTests.Measured_trace_is_ambient_not_a_trip` |
| A round trip of the swing equation at the ambient RoCoF: the imbalance implied by the sample (`2E * RoCoF / f0`, checked to lie in 1 to 500 MW) fed to `Simulate` returns that RoCoF (3 decimals). It is self-consistency of one equation, not independent validation | `FrequencyCalibrationTests.Model_reproduces_the_ambient_rocof_scale` |
| Device rotational inertia is pure stored energy: adding it equals adding the same MW.s of system inertia | `DynamicsConfigInvariantTests.Device_inertia_is_pure_half_J_omega_squared` (initial RoCoF, nadir and settle value, 12 decimals) |
| Fast response leaves the initial RoCoF unchanged (12 decimals) and lifts the nadir by more than 1e-4 Hz; zero device settings equal no device (9 decimals) | `DynamicsConfigInvariantTests.Fast_response_does_not_reduce_inertia`, `TsgbDynamicsTests.Fast_response_lifts_the_nadir`, `TsgbDynamicsTests.Defaults_match_no_device` |
| The obsolete alias equals the canonical field | `DynamicsConfigInvariantTests.Renamed_field_and_obsolete_alias_are_identical` |
| Live and batch integrators agree; large and 20-minute steps stay finite and settle where fine stepping settles | `FrequencyIntegratorTests.Matches_the_batch_simulate_trajectory_for_a_trip`, `Nadir_matches_the_batch_simulate`, `Large_step_stays_finite_and_matches_substepped_result`, `The_clamped_20_minute_step_still_settles_where_fine_stepping_does` |
| A sub-dead-band deficit never engages fast response; state carries across a changed `dt` | `FrequencyIntegratorTests.A_sub_deadband_deficit_never_engages_fast_response`, `A_mid_run_dt_change_carries_state_and_settles_the_same` |
| Service curves: full delivery at the declared deviation, the DC knee shape, the DM ramp, knee <= 0 falls back to the line | `ResponseServiceTests.Kernel_general_form_full_delivery_at_declared_deviation`, `Kernel_knee_form_matches_the_published_dc_curve` |
| An empty service list is the identity; services raise the nadir; the fleet kernel integrates services identically to the scalar model | `ResponseServiceTests.Empty_service_list_is_the_identity`, `Services_arrest_the_nadir`, `Fleet_integrates_services_identically_to_the_scalar_model` |
| `Typical` carries the volumes (exact), the DC and DM full-delivery deviations (exact), raises the nadir, leaves the initial RoCoF unchanged (6 decimals) and scales the DC volume | `GbResponseServicesTests.Grounded_volumes_match_the_eac_clears`, `The_arrest_layer_raises_the_nadir`, `Scale_reduces_the_held_volume` |
| A complete recorded service list zeroes the generic governor | `ResponseServiceTests.ApplyRecordedServices_maps_and_completeness_zeroes_the_governor` |
| `Forming = None` ignores every forming field, bit for bit | `FormingFrequencyTests.None_path_ignores_the_forming_params_byte_identically` |
| Forming with `Ev = 0`, `Dv = 0` reduces to the COI swing with O(dt) continuity | `FormingFrequencyTests.Forming_reduces_to_the_coi_swing_with_O_dt_continuity` |
| An islanded former gives a bounded first-order frequency; nothing forming fails loud; virtual inertia never enters the real denominator; sub-steps grow as inertia shrinks | `FormingFrequencyTests.Islanded_former_gives_a_real_bounded_first_order_frequency`, `Grid_forming_with_nothing_forming_the_frequency_fails_loud`, `Virtual_inertia_never_leaks_into_the_real_denominator`, `Effective_forming_inertia_has_no_floor_and_fails_loud_at_zero`, `Substep_count_grows_as_inertia_shrinks`, `Small_inertia_with_a_fast_high_gain_service_and_ffr_stays_bounded` |
| Asset dynamics off, or on with nothing bound, is bit-identical to the baseline, also under forming | `AssetDynamicsReductionTests.Flag_off_is_bit_identical_to_the_lumped_baseline`, `Enabled_but_nothing_bound_reduces_to_baseline`, `Enabled_with_only_unmatched_assets_reduces_to_baseline`, `Forming_flag_reduces_to_the_lumped_forming_baseline` |
| A grid-forming asset's synthetic inertia lowers the initial RoCoF without changing `E` | `GridFormingInertiaTests.Synthetic_inertia_lowers_initial_rocof_without_touching_E` |
| The lumped seam is bit-for-bit the direct call; the default is lumped | `InertiaModelSeamTests.Routing_through_the_seam_is_bit_for_bit_identical_to_calling_FrequencyDynamics`, `Default_dynamics_config_selects_the_lumped_scalar_model` |
| T0: the reduction limit and a naturally single-domain case are identical to the lumped model | `DistributedReductionTests.Reduction_limit_config_is_identical_to_the_lumped_model`, `Single_domain_case_reduces_naturally_without_forcing` |
| T1: the multi-domain COI equals the lumped trajectory to 1e-6 Hz, with and without services and with physical coupling (the first test's name says machine epsilon; its assertion is `< 1e-6` Hz) | `DistributedInvariantTests.Coi_trajectory_equals_the_lumped_model_to_machine_epsilon`, `ResponseServiceTests.Distributed_coi_with_services_equals_the_lumped_model`, `CanonicalDynamicsTests.Coi_still_equals_lumped_with_physical_coupling` |
| Per-domain frequencies diverge while the COI is preserved; the divergence is bounded, attributed and tagged `UnvalidatedAtScale` | `DistributedInvariantTests.Per_domain_frequencies_diverge_from_the_coi`, `DistributedScaleDivergenceTests.Scale_divergence_is_bounded_mechanism_attributed_and_unvalidated` |
| Coupling: a single line gives `BaseMva / X`; stiffer coupling reduces divergence; the inter-domain mode matches the small-signal formula | `CanonicalDynamicsTests.Kron_coupling_of_a_single_line_equals_base_over_reactance`, `Stiffer_coupling_reduces_inter_domain_divergence`, `Inter_domain_mode_frequency_matches_the_small_signal_formula` |
| Dominance reproduces the LOS worked example (`deltaGVa` 0.333) | `DistributedQualitativeTests.Dominance_reproduces_the_los_worked_example`, `DistributedInvariantTests.Dominance_report_flags_the_larger_domain` |
| Propagation: distant domains respond later; wave speed within 500 to 1500 km/s; off by default; a provided `tau` overrides the model | `DistributedQualitativeTests.Propagation_makes_distant_domains_respond_later`, `Wave_speed_is_in_the_literature_band`, `Propagation_is_off_by_default_and_leaves_no_report`, `Provided_tau_map_overrides_the_first_order_model` |
| The distributed COI reproduces a (synthetic, self-consistent) recorded event within the replay tolerance | `DistributedReplayTests.Distributed_coi_reproduces_a_recorded_event_within_replay_tolerance` |
| The inertia inversion round-trips the simulator | `InertiaAndLimitsTests.Inertia_estimate_round_trips_the_simulator`, `Predict_and_estimate_are_consistent`, `Estimate_h_seconds_on_a_base` |
| Limit classifiers and trace assessment | `InertiaAndLimitsTests.Mild_trip_stays_within_limits`, `Severe_low_inertia_trip_breaches_rocof`, `Trace_assessment_flags_a_dip_and_high_rocof`, `Steady_trace_reports_no_breaches` |
| Case9 `H` and `MBase` and the system total are the published dynamic data | `Case9InertiaTests.Case9_system_inertia_matches_published_total`, `Case9_each_generator_stored_energy_matches_published_values`, `Case9_generator_H_and_MBase_are_the_published_dynamic_data` |
| The frequency export is deterministic | `FrequencyExportTests.Export_is_deterministic` |

## Worked example

The `trip` verb trips the non-slack unit with `H > 0` and the largest dispatched `Pg` (not the largest rating) of the default case (`data/uk`, the GB reduced
10-zone model) at t = 1 s and runs the lumped swing with the default `DynamicsConfig` for 30 s.

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

(Captured in `docs/_captures/frequency/trip.txt`. The output has no timings; the captures of other chapters that
carry timings differ by machine and run.)

**Inertia, from `data/uk/generation.json`.** Thirteen generators carry `inertiaH > 0`; every other generator has
`H = 0`. `MBase` is `capacityMva`:

| Generator | H (s) | MBase (MVA) | H * MBase (MW.s) |
|---|---|---|---|
| North Scotland Hydro | 3.0 | 1300 | 3,900 |
| Torness Nuclear | 4.0 | 1250 | 5,000 |
| Heysham Nuclear | 4.0 | 2400 | 9,600 |
| North England CCGT | 5.0 | 2000 | 10,000 |
| Drax Biomass | 4.0 | 2600 | 10,400 |
| Yorkshire CCGT | 5.0 | 4000 | 20,000 |
| North Wales CCGT | 5.0 | 3000 | 15,000 |
| Midlands CCGT (marginal) | 5.0 | 9000 | 45,000 |
| Sizewell B Nuclear | 4.0 | 1250 | 5,000 |
| London CCGT | 5.0 | 2000 | 10,000 |
| South CCGT | 5.0 | 3000 | 15,000 |
| Hinkley Point Nuclear | 4.0 | 3200 | 12,800 |
| Pembroke CCGT | 5.0 | 2000 | 10,000 |
| Total | | | 171,700 |

The total, 171,700 MW.s, is the 171.7 GVA.s of the first line. The tripped unit stops spinning:
`E = 171,700 - 12,800 = 158,900 MW.s`, the 158.9 of the capture. The lost infeed is the unit's dispatch,
`Pg = 0.90 * 3200 = 2,880 MW`, so `deltaP = -2,880 MW`.

**Initial RoCoF by hand.** At the event step every lagged state is 0 and `df = 0`, so

```
rocof = f0 / (2 * E) * deltaP = 50 * (-2880) / (2 * 158900) = -144000 / 317800 = -0.45312 Hz/s
```

which is the `-0.453` of the capture. The exported trajectory (`gridsim trip --export <file>`, 20 ms rows, five decimals)
agrees: frequency is 50.00000 Hz at t = 1.00 s and 49.99094 Hz at 1.02 s, and `50 - 0.45312 * 0.02 = 49.99094`.
Inverting the equation with the exact RoCoF gives `E = 50 * 2880 / (2 * 0.4531152) = 158,900 MW.s` (`InertiaEstimator`).

**Half inertia.** The verb reruns with `E / 2 = 79,450 MW.s` (79.45 GVA.s, printed as 79.5):
`50 * 2880 / (2 * 79450) = 0.90623 Hz/s`, the `-0.906` of the capture, exactly twice the first value.

**What the nadir depends on.** The default config has no services and no device. The case's base demand is the sum
of bus loads, 39,500 MW, so

```
d    = 1.0 / 100 * 39500                  = 395 MW/Hz
Presp = 0.20 * 39500                      = 7,900 MW
K    = 7900 / (0.05 * 50)                 = 3,160 MW/Hz
M    = 2 * 158900 / 50                    = 6,356 MW.s/Hz
```

The static droop equilibrium is `|df| = 2880 / (3160 + 395) = 0.8101 Hz`, that is 49.190 Hz. The linearised
characteristic equation `s^2 + 0.18715 s + 0.06991 = 0` (`(M + d*T)/(M*T)` and `(d + K)/(M*T)` with `T = 8 s`) gives
`omega_n = 0.2644 rad/s`, `zeta = 0.354`, and a damped half-period of `pi / (omega_n * sqrt(1 - zeta^2)) = 12.70 s`.

The default run is linear (no dead-band engaged, no service, no clamp), so the step response has the closed form
given under "What sets the initial RoCoF and the nadir". Its coefficients for this case are

```
alpha   = (M + d*T) / (2*M*T) = 9,516 / 101,696      = 0.093573 1/s
omega_d = sqrt(0.264413^2 - 0.093573^2)                = 0.247302 rad/s
A       = -2880 / 3555                                  = -0.810127 Hz
C       = (-2880 / 6356 - 0.093573 * (-0.810127)) / omega_d = -1.525701 Hz
x(t)    = -0.810127 + exp(-0.093573 t) * (0.810127 cos(0.247302 t) - 1.525701 sin(0.247302 t))
```

The nadir is at `t* = (pi - atan(0.247302 / (0.125 - 0.093573))) / 0.247302 = 6.8629 s` after the event, which is
7.8629 s on the run's clock (the event is at 1 s), and `f = 50 + x(t*) = 48.33980 Hz`. The first crest is
`pi / omega_d = 12.7035 s` later, at 19.5663 s after the event (20.5663 s on the run's clock), at 49.44882 Hz.

The exported trajectory gives a trough of 48.34017 Hz at t = 7.86 s (the capture's `t+6.9 s` after the trip) and a
first crest of 49.44867 Hz at t = 20.54 s. The run is above the closed form by 0.00037 Hz at the nadir and below it
by 0.00015 Hz at the crest. The difference is the discretisation error of the explicit 20 ms Euler step (an
independent 20 ms Euler recomputation of the two-state loop gives 48.34017 Hz at 7.86 s and 49.44867 Hz at 20.54 s),
and the reported times are grid points. The run's crest-to-trough spacing is 12.68 s against 12.7035 s from the
closed form, a difference of 0.024 s. The trajectory then falls back toward 49.190 Hz. The reported `Settling` of
49.144 Hz is the frequency one step after the 30 s window and is still below that equilibrium: it is the end of the
window, not the steady state. The nadir depends on `T`, `K` and `d`, which are the UNFITTED constants.

**Grid-forming.** The same trip with a virtual former of 20 GVA.s and 100 MW/Hz of damping:

```
$ gridsim trip --forming vsm --forming-inertia 20 --forming-damping 100
GridSim  -  contingency on GB reduced (10-zone)
================================================================
Tripped:        Hinkley Point Nuclear  (2880 MW lost, nuclear)
System inertia: 171.7 -> 158.9 GVAs after the trip
Peak RoCoF:     -0.402 Hz/s  (largest |df/dt| over the run)
Nadir:          48.480 Hz  (t+7.2 s)
Settling:       49.215 Hz

50.05 Hz |***+==-:,,..             ....,,,:::::-----------------------::::| 48.48 Hz (nadir)

...at HALF the inertia (79.5 GVAs): RoCoF -0.724 Hz/s, nadir 48.147 Hz
[exit 0]
```

The denominator is `Eform = 158,900 + 20,000 = 178,900 MW.s`: `50 * 2880 / (2 * 178900) = 0.40246`, the `-0.402`
shown. The half-inertia line is `Eform = 79,450 + 20,000 = 99,450` and `0.72398`, the `-0.724` shown. The printed system
inertia line is the real stored energy and does not include `Ev`.

**Distributed.** `gridsim trip --inertia-model distributed` runs the multi-domain swing on the post-trip network:

```
$ gridsim trip --inertia-model distributed
Inertia model:  distributed  (2 speed domain(s), dGVa 0.37)
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

The COI-collapsed numbers equal the lumped run to the printed precision (invariant T1). The `2` in the `Inertia model:`
line counts swing masses (`dr.Domains.Count`): the 2-pole and the 8-pole synchronous domains. `gridsim domains` counts
every speed domain, including the `Pll` domain, so it prints 3. `dGVa 0.37` is the dominance gap of the post-trip
fleet. The speed-domain table of the intact fleet:

```
$ gridsim domains
GridSim  -  SPEED DOMAINS on GB reduced (10-zone)
==================================================================
3 speed domains  *  deltaGVa 56 %  *  dominant = Domain 1

Domain   Speed         Kind               Mach      GVA.s   GVad GVA
Domain 1 2p/3000rpm    Synchronous          12     167.80      24.34  *
Domain 0 inverter      Pll                  16       0.00      15.61
Domain 2 8p/750rpm     Synchronous           1       3.90       0.65

GVad = apparent-power stiffness sum(S*C); GVA.s = real spinning inertia. A domain is dominant
when deltaGVa >= 10% (LOS sec. 5.5). Distributed model: spatial spread & propagation are unvalidated at scale.
[exit 0]
```

The 12 two-pole machines hold `167.80` GVA.s and the one hydro machine `3.90`, which sum to 171.7. The dominance gap is
`(24.34 - 15.61) / 15.61 = 0.559`, shown as 56 %, which is above the 0.10 threshold, so Domain 1 is dominant. The
`Pll` domain has no inertia and is not a swing mass, but its stiffness `GVad` counts in the dominance ratio.

**The NESO anchor.** `gridsim freq` summarises the bundled ambient sample and shows the upsampler:

```
$ gridsim freq
NESO frequency  -  frequency-sample.csv
================================================================
Samples      300  (1 s cadence)
Frequency    min 49.921 Hz  mean 49.991 Hz  max 50.106 Hz
Max |RoCoF|  0.0080 Hz/s  at 2024-08-01 00:00:02

vs GB limits:
  RoCoF 0.125 Hz/s: within  (worst 0.0080 Hz/s)
  operational 49.8 Hz: held    statutory 49.5 Hz: held    LFDD 48.8 Hz: clear

Upsample demo  -  1 s frames expanded to 20 ms by RoCoF interpolation:
  time            Hz         RoCoF (Hz/s)
  00:00:00.000   50.10500     0.0010
  00:00:00.020   50.10502     0.0010
  00:00:00.040   50.10504     0.0010
  00:00:00.060   50.10506     0.0010
  00:00:00.080   50.10508     0.0010
  00:00:00.100   50.10510     0.0010
  00:00:00.120   50.10512     0.0010
  00:00:00.140   50.10514     0.0010
[exit 0]
```

The interval RoCoF is 0.0010 Hz/s, so each 20 ms row adds `0.0010 * 0.02 = 0.00002` Hz, as shown. The worst ambient
RoCoF, 0.0080 Hz/s, is 6.4 % of the 0.125 Hz/s legacy limit. Inverting the swing equation at the representative
200 GVA.s of `FrequencyCalibrationTests` gives an implied imbalance of `2 * 200000 * 0.0080 / 50 = 64 MW` (63.6 to
64.4 MW across the rounding of the displayed 0.0080), against 1,320 MW for the largest normal infeed loss in that
test. `FrequencyCalibrationTests.Model_reproduces_the_ambient_rocof_scale` derives the imbalance from the measured
RoCoF with this inversion and then asserts that `Simulate` returns the same RoCoF to 3 decimals, so it is a round trip
of one equation. Its only independent check is that the implied imbalance lies in 1 to 500 MW. It does not constrain
the nadir constants.

## Source map

| File | Type | Responsibility |
|---|---|---|
| `src/GridSim.Core/Dynamics/FrequencyDynamics.cs` | `DynamicsConfig`, `SystemResponseService`, `FrequencyEvent`, `FrequencyResponse`, `FrequencyDynamics` | Config, batch swing loop, equilibrium start. |
| `src/GridSim.Core/Dynamics/FrequencyIntegrator.cs` | `FrequencyIntegrator` | Live stateful integrator, sub-stepping. |
| `src/GridSim.Core/Dynamics/AssetAwareFrequencyIntegrator.cs`, `AssetFrequencyDynamics.cs` | `AssetAwareFrequencyIntegrator`, `AssetFrequencyDynamics` | Live and batch loops with the `pAssetFleet` term. |
| `src/GridSim.Core/Dynamics/IFrequencyIntegrator.cs` | `IFrequencyIntegrator` | Contract `LiveSim` drives. |
| `src/GridSim.Core/Dynamics/SwingKernel.cs` | `SwingKernel` | Governor and delivery-curve targets. |
| `src/GridSim.Core/Dynamics/GbResponseServices.cs` | `GbResponseServices` | GB DC, DM, DR volumes and curve parameters. |
| `src/GridSim.Core/Dynamics/Forming/FormingKernel.cs`, `FormingAggregate.cs`, `src/GridSim.Core/Dynamics/FormingMode.cs` | `FormingKernel`, `FormingAggregate`, `FormingMode` | Semi-implicit forming step, sub-step count, fleet aggregate. |
| `src/GridSim.Core/Dynamics/InertiaModel.cs`, `InertiaModelSelector.cs`, `LumpedScalarInertiaModel.cs`, `src/GridSim.Core/Abstractions/IInertiaModel.cs` | `InertiaModel`, `InertiaModelSelector`, `LumpedScalarInertiaModel`, `IInertiaModel` | The model seam. |
| `src/GridSim.Core/Dynamics/Distributed/DomainAssignment.cs`, `PoleMap.cs`, `SpeedDomain.cs`, `DominanceReport.cs` | `DomainAssignment`, `PoleMap`, `SpeedDomain`, `DominanceReport` | Speed domains and dominance. |
| `src/GridSim.Core/Dynamics/Distributed/MultiDomainSwing.cs`, `DomainCoupling.cs`, `PropagationDelay.cs` | `MultiDomainSwing`, `DomainCoupling`, `PropagationDelay` | Multi-mass swing, Kron coupling, propagation. |
| `src/GridSim.Core/Dynamics/Distributed/DistributedInertiaModel.cs`, `DistributedConfig.cs`, `DistributedResult.cs`, `DomainTrace.cs`, `DivergenceReport.cs` | `DistributedInertiaModel`, `DistributedConfig`, `DistributedResult`, `DomainTrace`, `DivergenceReport` | Distributed model entry, settings, outputs. |
| `src/GridSim.Core/Dynamics/InertiaEstimator.cs` | `InertiaEstimator` | Inertia inversion. |
| `src/GridSim.Core/Dynamics/FrequencyLimits.cs` | `FrequencyLimits`, `FrequencyAssessment`, `FrequencyTraceReport` | GB limits and classifiers. |
| `src/GridSim.Core/Dynamics/FrequencyConstantsFitter.cs` | `FrequencyConstantsFitter` | Fitting tool for the nadir constants. |
| `src/GridSim.Core/Assets/AssetDynamicsFleet.cs`, `GridFormingInverter.cs`, `GridFollowingInverter.cs` | `AssetDynamicsFleet`, `GridFormingInverter`, `GridFollowingInverter` | The fleet behind `pAssetFleet`. |
| `src/GridSim.Core/Model/Model.cs` | `Generator`, `GridModel.SystemInertiaGvaS` | `H`, `MBase`, and the inertia sum. |
| `src/GridSim.Core/Simulation/LiveSim.cs`, `LiveSolveSettings.cs` | `LiveSim`, `LiveSolveSettings` | Live frequency loop, trip decay, inertia fallback. |
| `src/GridSim.Core/Frequency/NesoFrequencyCsv.cs`, `RocofUpsampler.cs`, `FrequencyTypes.cs`, `ReplayController.cs` | `NesoFrequencyCsv`, `RocofUpsampler`, `FrequencySample`, `FrequencyPoint`, `ReplayController` | NESO trace path and playback. |
| `src/GridSim.Gda/FuelMap.cs` | `FuelMap` | Representative `H` per fuel class. |
| `src/GridSim.Cli/Program.cs` | `RunTrip`, `RunDomains`, `PrintFrequency`, `RunReplay`, `RunTransientBatch`, `RunTransient` | The verbs that drive this chapter. |

## Limits

- The nadir constants `DampingPctPerHz`, `ResponsiveFraction` and `GovernorTimeConstantS` are not fitted to any
  measured loss-of-infeed trace, and `FrequencyConstantsFitter` has no production caller (R079).
- The no-device baseline of the recorded 2023-12-22 event reproduces only as `Marginal` (captured: RoCoF
  -0.123 against -0.102 Hz/s recorded, nadir 48.98 against 49.27 Hz), so `gridsim h0-study` refuses to run
  counterfactuals on it (exit 3).
- A bus genuinely at map position `(0, 0)` is read as unlocated by `PropagationDelay`.
- When the Kron reduction fails, or finds no usable coupling, `DomainCoupling` falls back to the uniform 0.5 Hz
  stiffness; the fallback and its reason are carried on the distributed result as a `CouplingReport`. The reduction
  works on the sparse admittance matrix.
- The distributed model has no virtual former and no authored-asset term: a forming run delegates to the lumped
  model, and the per-domain asset sampling path is not wired in (R074).
- The distributed model has no field validation: spatial spread and propagation await PMU or DFR data.
- The lumped model has one frequency for the whole system: no inter-area swing, no local frequency, no network
  propagation unless the distributed model is selected.

## See also

- [Frequency studies](<../manual/0014 Frequency studies.md>) - the `freq`, `replay`, `trip`, `transient` and `domains` verbs.
- [CLI reference: trip](<../manual/0019 CLI reference.md#trip>), [domains](<../manual/0019 CLI reference.md#domains>), [transient](<../manual/0019 CLI reference.md#transient>), [freq](<../manual/0019 CLI reference.md#freq>), [replay](<../manual/0019 CLI reference.md#replay>).
- [Replaying recorded events](<../manual/0018 Replaying recorded events.md>) - the credibility gate built on `Simulate`.
- [Asset-dynamics data grounding](<0013 Asset-dynamics data grounding.md>) - where inertia and response volumes come from.
- [Cascade validation and blind prediction](<0014 Cascade validation and blind prediction.md>).
- [Standard dynamic-model library](<0024 Standard dynamic-model library.md>) - machine, governor and exciter models.
- [Power flow](<0003 Power flow.md>) - the solver whose model feeds `SystemInertiaGvaS`.
- [The evidence engine](<0007 The evidence engine.md>) - replay validation, scenarios and fleets built on `Simulate`.
- [Provenance and invariants](<0008 Provenance and invariants.md>) - `UnvalidatedAtScale`, `[VERIFY]` and the horizon guard on the NESO reader.
- [GDA integration](<0009 GDA integration.md>) - the bridge, including the `propagation` query.
