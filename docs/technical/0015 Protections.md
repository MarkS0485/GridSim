# 15. Protections

This chapter specifies the protection device layer on the asset-dynamics seam: free-standing protective devices
(RoCoF and loss-of-mains, under- and over-frequency, df/dt, vector shift, under- and over-voltage, thermal overload,
low-frequency demand disconnection and topology auto-reclosers) that run inside the same fleet as the inverter
controllers. A device reads a locally sampled quantity, compares it with a threshold, and when the threshold has been
crossed for a dwell time it acts on one of the channels the asset seam already carries: lost infeed, demand shed, or an
opened branch. The code is in `src/GridSim.Core/Assets/Protection/`.

The layer models generic protective behaviour, not a site's relay scheme. Site relay settings are unpublished, so every
authored setting is generic and carries the provenance rules described in
[ch. 8, Provenance and invariants](<0008 Provenance and invariants.md>); the grounding of the two shipped device
classes is recorded in [ch. 13, Asset dynamics data grounding](<0013 Asset-dynamics data grounding.md>). The
passive relay overlay that grades a solved network state (`protection-check`) is
[ch. 17, Protection assessment](<0017 Protection assessment (a passive relay overlay).md>). Short-circuit currents are chapter 25
([Short-circuit, IEC 60909](<0025 IEC 60909 short-circuit duty study.md>)) and inverse-time grading is chapter 26
([Overcurrent coordination](<0026 Overcurrent coordination (IDMT grading).md>)); neither is repeated here. The frequency
integrator that the layer plugs into is specified in
[ch. 5, Frequency dynamics](<0005 Frequency dynamics.md>). The authoring recipe for a new class is in
[ch. 29, Extending GridSim](<../manual/0029 Extending GridSim.md>).

## Model

### Where a protection sits

A protection is an `IActiveAssetDynamics` (`src/GridSim.Core/Assets/IAssetDynamics.cs`): one stateful object per
placed device, advanced once per fast sub-step by `AssetDynamicsFleet`. The fleet holds the inverter controllers and
the protection controllers in one array. Each step the fleet sums the `AssetActuation` of every member:

```
AssetActuation(ActivePowerMw, TrippedNow, TrippedInfeedMw, TrippedInertiaMwS)

fleet net swing power   pAssetFleet   = sum of ActivePowerMw over the fleet
fleet trip deficit      tripDeficit   = running sum of TrippedInfeedMw over every step with TrippedNow
swing imbalance         pImbalance    = pBase + pAssetFleet - tripDeficit
RoCoF                   rocof         = f0 / (2 E) * pImbalance
```

`pBase` is the imbalance before the fleet (event, governor, fast response, damping, the standing imbalance
`pStanding` and the system-response services), `E` is `max(1, inertia + DeviceRotationalInertiaMwS)` in MW.s and `f0`
the nominal frequency. In forming mode the predictor is `f0 / (2 * eForming) * (pForcingBase - dampTotal * df)`, with
the forming inertia and damping. A protection controller never reports inertia: `TrippedInertiaMwS` is always `0`.

### Stage

One definite-time stage is a `ProtectionStage` (`ProtectionElement.cs`):

| Field | Meaning |
|---|---|
| `Quantity` | `Frequency`, `Rocof`, `Voltage`, `LoadingFraction` or `PhaseShift`. |
| `Sense` | `Above` (operate when the value exceeds the threshold) or `Below` (operate when it falls under). |
| `Threshold` | The pickup, in the unit of the quantity. |
| `DwellSeconds` | The definite time the condition must hold. |
| `Action` | `TripInfeed`, `ShedDemand`, `InjectStep`, `OpenBranch` or `RestoreBranch`. |
| `ActionMw` | The MW the action moves. `0` means use the placed device's `capacityMw`. |
| `LocalUpliftFactor` | Multiplies a RoCoF reading (sub-cycle local RoCoF is larger than the COI value). |
| `AutoRestoreDelayS` | Reset delay after operating. `+infinity` (the default) latches. |
| `ArmDelayS` | The stage cannot operate while the clock is below this. |
| `TargetBranch` | The branch an `OpenBranch` action opens. |

`ArmDelayS` is populated only by code that builds a stage directly. No class parameter sets it, and a class that
carries an `armDelayS` key is refused at load as an unknown key, so an author cannot believe it took effect.

### Quantities and units

The controller reads these from `AssetStepContext` (`LocalHz`, `LocalRocofHzPerS`, `LocalPhaseDeg`,
`LocalVoltagePu`, `LoadingFraction`, `DtSeconds`, `ClockSeconds`). Per update:

```
push (ClockSeconds, LocalHz, LocalRocofHzPerS, LocalPhaseDeg) into the delay history
(fMeas, rocofMeas) = history sample read at  ClockSeconds - detectionLatencyS
phaseStep          = phase(newest sample) - phase(sample read at  ClockSeconds - max(DtSeconds, 0.02))

value(Frequency)       = fMeas                          Hz
value(Rocof)           = |rocofMeas| * LocalUpliftFactor   Hz/s
value(Voltage)         = LocalVoltagePu                 pu of nominal
value(LoadingFraction) = LoadingFraction                1.0 = 100 % of branch rating
value(PhaseShift)      = |phaseStep|                    degrees over the window
```

The delay history is `MeasurementDelay` (`src/GridSim.Core/Assets/GridFollowingInverter.cs`), the same buffer the
inverter controllers use. It is a ring of 512 samples, 10.24 s at the 20 ms sub-step. `Read(now, latency)` returns the
newest stored sample whose time stamp is at or before `now - latency + 1e-12`, and the oldest stored sample when none
qualifies; an empty history reads nominal frequency, zero RoCoF and zero phase. A device therefore sees the start of
the run, not the present, until `latency` seconds of history exist. Once the 512 samples are full, a latency longer
than the span they hold is refused with an error (both the protection device and the inverter controller check it on
every update), rather than being shortened to that span. Only frequency and RoCoF are delayed. Voltage and
loading are taken from the step context as supplied, and the phase window is not delayed. `PhaseStep` reads zero until
a stored sample at least one window old exists, so the first update of a run sees no phase step.

The RoCoF in the context is the integrator's predicted RoCoF: `f0 / (2 E) * pBase` (non-forming), computed before the fleet acts
(`AssetAwareFrequencyIntegrator.Step`, `AssetFrequencyDynamics.Simulate`). It excludes the fleet's own contribution,
which is what lets a device be evaluated without an algebraic loop. The phase is the accumulated angle against nominal,
`phaseDeg += 360 * (f - f0) * h` in degrees, so a `PhaseShift` stage reads the angle moved in the last
`max(DtSeconds, 0.02)` seconds.

### Pickup and dwell: `ProtectionEvaluator`

`ProtectionEvaluator` (`ProtectionEvaluator.cs`) is the quantity-agnostic edge detector for one stage. It receives the
already selected and scaled value. As coded:

```
Step(ctx, value):
    if operated:
        if ClockSeconds >= restoreAt:            # only reachable with a finite AutoRestoreDelayS
            operated = false;  dwell = 0;  restoreAt = +inf
            return Restored
        return None                              # latched, or waiting out the reclose delay
    if ClockSeconds < ArmDelayS:  return None
    over  = (Sense == Above) ? value > Threshold : value < Threshold      # strict comparison
    if over:  n = n + 1;  dwell = carry + n * DtSeconds                     # whole sub-steps counted
    else:     n = 0;  carry = 0;  dwell = 0                                   # any miss resets the dwell
    if over and DwellReached(dwell, DwellSeconds):
        operated  = true
        restoreAt = (AutoRestoreDelayS is +inf) ? +inf : ClockSeconds + AutoRestoreDelayS
        return Fired                             # exactly once per operation
    return None
```

Consequences that follow directly from the code:

- The comparison is strict. A value equal to the threshold does not pick up.
- The dwell counts whole over-threshold sub-steps and includes the step on which the stage fires: the elapsed time is
  the count times `DtSeconds` (a sub-step of a different length banks the steps counted so far into `carry` first), not a
  repeated sum. `DwellReached(elapsed, dwell)` is `elapsed >= dwell`, or, for a finite dwell,
  `dwell - elapsed <= 2^-49 * dwell` (`DwellRelativeSlack`), so a representation error in the step cannot add a step;
  an infinite dwell never times out. A stage with `DwellSeconds = 0` fires on the first update whose value is over the
  threshold. A positive dwell is met on the first update at which the counted time reaches it, so the operating time is
  quantised to the sub-step (20 ms at the default `DynamicsConfig.StepSeconds`): a 0.2 s dwell at 20 ms is met on the
  tenth update. The inverter controllers' confirm times are counted the same way (`ConfirmTimer`).
- A single update below the threshold clears the accumulated dwell. A spike shorter than the dwell never operates.
- `Fired` is emitted once. While the stage is operated it returns `None`, whatever the value does.
- After `Restored` the dwell restarts from zero, so a condition that persists through a reclose operates the stage
  again after its dwell.
- `ProtectionAction.RestoreBranch` is refused: the `ProtectionEvaluator` constructor throws `NotSupportedException`
  for a stage with that action. Nothing acts on a restore request, so a stage built with it would operate and change
  nothing. Reclosing is expressed as an `OpenBranch` stage with a finite `AutoRestoreDelayS`. The controller's own
  `RestoreBranch` case also throws, so the action cannot become a silent no-op if the construction guard is removed.

### Building the stages: `ProtectionController`

`ProtectionController` (`ProtectionController.cs`) builds one stage for each protection parameter the class sets and
drives one evaluator per stage. A parameter the class omits builds no stage. The stages are built in this fixed order:

| # | Class key (stage built when set) | Quantity | Sense | Threshold | Dwell key | Action |
|---|---|---|---|---|---|---|
| 1 | `rocofTripHzPerS` | `Rocof` | `Above` | `rocofTripHzPerS` | `rocofTripConfirmS` | trip |
| 2 | `dfDtTripHzPerS` | `Rocof` | `Above` | `dfDtTripHzPerS` | `dfDtTripDwellS` | trip |
| 3 | `ufTripHz` | `Frequency` | `Below` | `ufTripHz` | `ufTripDwellS` | trip |
| 4 | `ofTripHz` | `Frequency` | `Above` | `ofTripHz` | `ofTripDwellS` | trip |
| 5 | `uvTripPu` | `Voltage` | `Below` | `uvTripPu` | `uvTripDwellS` | trip |
| 6 | `lvrtVoltagePu`, only when greater than 0 | `Voltage` | `Below` | `lvrtVoltagePu` | none (0 s) | trip |
| 7 | `ovTripPu` | `Voltage` | `Above` | `ovTripPu` | `ovTripDwellS` | trip |
| 8 | `overloadTripFraction` | `LoadingFraction` | `Above` | `overloadTripFraction` | `overloadTripDwellS` | trip |
| 9 | `vectorShiftTripDeg`, only when finite | `PhaseShift` | `Above` | `vectorShiftTripDeg` | none (0 s) | trip |
| 10 | `lfddStageHz` | `Frequency` | `Below` | `lfddStageHz` | `lfddDwellS` | `ShedDemand`, `ActionMw = lfddBlockMw` |

"Trip" in the table is `TripInfeed` for an ordinary class and `OpenBranch` for a recloser class (below). Stages 1 and 2
multiply the RoCoF by `localRocofUpliftFactor` (default `1.0`); no other stage uses the uplift. A confirm or dwell
key has no default: a class that sets a stage's threshold key without its confirm or dwell key is refused when the
controller is built (`InvalidDataException` naming every missing key; write `0` for an instantaneous stage). Stages 6 and
9 have no dwell key and are instantaneous. Stage 10 is always a demand shed, whether or not the class
is a recloser, and it never auto-restores.

The MW each stage moves is `ActionMw` when positive, else the placed device's `capacityMw`. For an infeed trip and an
unset `lfddBlockMw` that is `capacityMw`.

What each action does when its stage fires (`ProtectionController.Update`):

| Action | Effect on the model |
|---|---|
| `TripInfeed` | If the device has not already tripped: `TrippedNow = true` and `TrippedInfeedMw = mw` for this step, and `Tripped` latches. The fleet adds `mw` to `tripDeficit`, which enters the swing as lost infeed for the rest of the run. A device whose class builds several trip stages (RoCoF, df/dt, UF, ...) loses its infeed once: the second and later stages that fire find `Tripped` already set and add nothing. |
| `ShedDemand` | `shed += mw * demandScale` (`mw = lfddBlockMw`; the block is a share of the demand now running, so it is exactly `mw` unless the live loop scaled the demand, N297; the stage remembers it so a reclose reconnects the same MW). The controller returns `ActivePowerMw = shed`, a positive swing-power term: demand removed reduces the deficit. `Tripped` is not set; a demand shed operates but is not a tripped unit. |
| `InjectStep` | `shed += ActionMw`, a signed step on the same channel. No class parameter builds this action; it exists for code that builds stages directly. |
| `OpenBranch` | `Tripped` latches and the target branch's hold count is incremented. The branch is reported through `OpenBranches`. Nothing is added to the swing at this point; the topology layer acts on the branch (below). |
| `RestoreBranch` | Refused (see above). |

When a finite-delay stage returns `Restored`, `ShedDemand` and `InjectStep` subtract what they added, and
`OpenBranch` releases that stage's hold. The controller counts holds per branch: several stages (RoCoF, UF, ...) can
open the same branch, each with its own reclose delay, and the branch stays open until the last holding stage has
reclosed. `Tripped` then equals "at least one branch is still held open". The shipped LFDD stage latches, so `Restored`
is not produced for it by the controller.

`Reset()` clears every evaluator, the delay history, the shed total, `Tripped` and the held branches.

### Reduction gate

A controller whose stages never operate returns `AssetActuation(0, false, 0, 0)`. The fleet then adds `0.0` to
`pAssetFleet` and `0.0` to `tripDeficit`, and the swing computes `pBase + 0.0 - 0.0`, which equals `pBase` bit for bit.
A case with no `protections.json`, or with devices that never operate, therefore reproduces the lumped trajectory
exactly.

## Placement and files

A protection has two parts. The **class** describes the device; the **placement** says where it is and what it acts
on. Both files are optional, and both bundles are null-gated, so a case without them is untouched.

### The class: `asset-classes.json`

A device class is an entry of `kind: "protection"` (the loader also accepts `relay` and `protective`) in the same
`asset-classes.json` that holds the inverter classes. The entry is `name`, `kind`, `mode`, a flat `params` bag of
class keys to numbers, and a `provenance` map from key to `{tag, note}`. The `protections` classes of the bundled
`data/gb-synthetic/asset-classes.json` are these two:

| Class | `params` | Provenance |
|---|---|---|
| `prot-lom-legacy` | `rocofTripHzPerS 0.125`, `rocofTripConfirmS 0.0`, `localRocofUpliftFactor 1.16` | `rocofTripHzPerS`: `Recorded` (legacy GB loss-of-mains setting, GDA `rocof_events` relay_legacy grade). `localRocofUpliftFactor`: `DerivedFromRecorded` (sub-cycle local over COI RoCoF, 1.16). |
| `prot-lfdd-stage1` | `lfddStageHz 48.8`, `lfddDwellS 0.0`, `lfddBlockMw 632.0` | `lfddStageHz`: `Recorded` (GB Grid Code first LFDD stage, CC.A.5.5.1 Table CC.A.5.5.1a). `lfddBlockMw`: `DerivedFromRecorded` (GDA `lfdd_risk` 632 MW coincident block, 2019-08-09). |

Class loading is strict (`AssetClassSupport`, `src/GridSim.Core/Assets/AssetClassLibrary.cs`). A protection class
accepts exactly the keys in the parameter table below; any other key, a misspelling included, is refused at load with
every problem listed. Class names are matched ignoring case. Every `CuratedPlaceholder` provenance note must carry the
literal `[VERIFY]` token or the library is refused when a case is loaded (`JsonGridLoader.BuildLibrary` calls
`AssetClassLibrary.ValidateOrThrow`; a library built in code is checked only if the caller invokes it). The meaning of each tag is defined once, in chapter 8.

### The placement: `protections.json`

`protections.json` (schema `schemas/v1/protections.schema.json`, `ProtectionInstanceDto`) is an array of placed
devices:

| Member | Required | Meaning |
|---|---|---|
| `name` | no | Device name. |
| `class` | yes | A class of kind protection in `asset-classes.json`. In a v2 network the member is `template`. |
| `monitorBus` | yes | The bus whose solved voltage and loading the device reads. Must be a bus of the case. |
| `capacityMw` | no, default `0`, schema minimum `0`; a negative or non-finite value is refused when the placement is validated (D-DOC-094) | MW the device trips (infeed) or sheds (demand when `lfddBlockMw` is unset). |
| `targetBranch` | recloser only | The branch name the recloser opens. |

The shipped placement for `data/gb-synthetic`:

```
embedded-dg-lom  class prot-lom-legacy   monitorBus 1  capacityMw 800
system-lfdd-1    class prot-lfdd-stage1  monitorBus 1  capacityMw 632
```

The loader (`JsonGridLoader.BuildProtections`) returns a `ProtectionModel` of the class library plus the placed
`ProtectionInstance` records, or null when the file has no instances. It refuses a file with instances but no classes,
an unknown class, and a class that is not of kind protection. After the model is built, `ProtectionModel.ValidateAgainst`
checks the placement against the network and throws one `InvalidDataException` listing every problem:

- a class missing from the library, or a class that is not of kind protection;
- a `monitorBus` that is not a bus of the model (it would read 1.0 pu and zero loading, so its voltage and overload
  stages could never operate);
- a recloser (a class that sets `autoRecloseDelayS`) with no `targetBranch`, or a `targetBranch` that names no branch;
- a `targetBranch` on a device whose class is not a recloser.

The `ProtectionController` constructor repeats the recloser check: a recloser class built with no target throws
`InvalidDataException`, so a device that cannot do what its class says never exists.

`AssetDynamicsFleet.Build` creates one `ProtectionController` per placed instance and adds it to the fleet after the
inverter controllers. The loaded placement is also what `GridModel.CopyWith` and the island, inertia and cascade
transforms carry through.

### Recloser classes

A class that sets `autoRecloseDelayS` is a recloser. Every trip stage in it (rows 1 to 9 of the stage table) becomes an
`OpenBranch` stage on the placement's `targetBranch` with `AutoRestoreDelayS = autoRecloseDelayS`. The class's LFDD
stage, if it has one, stays a demand shed.

## Integration with the frequency integrator and LiveSim

`LiveSim` runs the asset-aware integrator when its `DynamicsConfig` sets `EnableAssetDynamics` and the case carries an
asset-dynamics bundle, a protections bundle, or both (`LiveSim.HasAuthoredBundle`). The desktop app, the web session
and the scheduled-cycle runner build that configuration with one shared rule, `LiveSim.AuthoredBundleConfig(model)`,
so a case with only `protections.json` runs its protections there as it does under `trip`. (Before D-DOC-079 those three
tested the asset-dynamics bundle only, and such a case ran with its protections idle. No bundled case changes: the one
bundled protections bundle, gb-synthetic's, ships with an asset-dynamics bundle.)

### Tier A: frequency and injection domain

RoCoF/loss-of-mains, under- and over-frequency, df/dt and LFDD plug into the fleet and need nothing from the network.
The integrators call `AssetDynamicsFleet.StepCoi(f, rocofPredict, phaseDeg, h, clock, ...)` and fold `pAssetFleet`
and `tripDeficit` into the imbalance as in the model section.

- `AssetFrequencyDynamics.Simulate` (the scripted run of the `trip` verb) calls it once per outer step of
  `DynamicsConfig.StepSeconds` (default 0.02 s), with no further sub-division of the fleet step.
- `AssetAwareFrequencyIntegrator` (the running clock of `LiveSim`), in non-forming mode, sub-steps each tick at
  `h <= 20 ms` (`MaxInternalDt`) and calls it once per sub-step, with at most 4000 sub-steps per tick.
- In forming mode both call it once per step and sub-divide the swing by `FormingKernel.SubStepCount`.

LFDD is closed loop. The stage fires when the simulated frequency falls under `lfddStageHz`, and the shed
`+lfddBlockMw` enters `pAssetFleet` on the update that fires, so it lowers the fall from that sample onward. It replaces a scripted LFDD step; it must not be run
alongside one, or the demand is removed twice.

The fleet trip deficit and the LFDD shed persist until the fleet is reset (`AssetDynamicsFleet.Reset`, called by the
integrator constructors and by `AssetFrequencyDynamics.Simulate`). In the scripted runs they stay applied for the run.
In `LiveSim` the live deficit (manual trips through `TripInfeed` and the blackout term) decays with the 300 s
secondary-restoration constant `SecondaryTauS`, and the integrator restores the fleet's standing deficit (its trips net
of held protection injection, and its sheds) over the same constant (`secondaryRestorationTauS`): the redispatch `R`
follows the deficit as a first-order lag, integrated exactly per sub-step. The tripped units stay tripped; what
recovers is the balance.

### Tier B: voltage and loading

`Voltage` and `LoadingFraction` stages read `AssetStepContext.LocalVoltagePu` and `LoadingFraction`. The pure lumped
model solves no network, so the context holds `NoNetworkVoltagePu` and `NoNetworkLoadingFraction`, both NaN ("not
known"). Every comparison with NaN is false, so no voltage or loading stage operates on a path that solves no network,
whatever its setting; frequency, RoCoF and phase stages are unaffected. `StepCoi` builds the shared default context
when both of its optional suppliers `voltageOf` and `loadingOf` are null, which keeps the lumped path bit-identical.
`trip` with asset dynamics prints a note when assets carry such stages: they are not evaluated on that run.

`LiveSim` supplies the quantities after every power-flow solve through
`AssetAwareFrequencyIntegrator.SetNetworkState(voltageOf, loadingOf)`:

```
voltageOf(bus) = Vm of the bus in the solved result          (1.0 if the bus is absent)
loadingOf(bus) = max over the in-service branches incident to the bus of  LoadingPercent / 100
                                                              (0.0 when no branch is incident)
```

The values are held constant across the fast sub-steps of the tick, because the solve is much slower than the swing
step. (The fleet's former per-domain `Step(sampler, ...)` entry, which had no production caller and built its context
without voltage or loading, was removed in R074; `StepCoi` is the one fleet step.)

### Topology: recloser, islanding and blackout

Each tick `LiveSim` does the following in order:

1. `aware.Fleet.OpenBranches()` from the previous tick gives the set of branches held open.
2. If the set is not empty, `TopologyOps.Apply(model, openBranches)` marks those branches `InService = false` and runs
   `IslandPartition.Prepare`. If it is empty, the model solves unchanged.
3. The solve runs on the adjusted model. `YBus.Build`, the sparse `SparseNewtonRaphsonPowerFlow.BuildY`,
   `BackwardForwardSweep` and `NewtonShell.ComputeLineFlows` skip an out-of-service branch. With every branch in
   service the admittance accumulation order is unchanged, so the solve is bit-identical to a model with no topology
   feature.
4. The island report's net blackout `netBlackout = LostLoadMw - LostGenMw` enters the swing as a one-off perturbation of
   the live deficit: `_tripDeficitMw += netBlackout - lastBlackoutNetMw`. Load lost is surplus, so frequency rises. When
   every branch has reclosed, the stored `lastBlackoutNetMw` is subtracted and cleared: the load coming back is a new
   step of `-netBlackout`, applied in full.

The live deficit is a superposition of step imbalances, each decaying with `SecondaryTauS` (300 s). The decay of the
outage step stands for the rest of the system redispatching down to the smaller load, so at a reclose `t` seconds later
the system is short by the redispatched amount, `netBlackout * (1 - exp(-t / 300))`: frequency dips (cold load pickup)
and recovers as the reclose step decays in turn. A reclose within seconds leaves almost nothing; one after a long outage
leaves nearly the whole block. This was reviewed under D-DOC-080 (a docs-session finding that read the reclose as an
undo with a residual of the wrong sign) and kept as the intended behaviour; no number changed.

`IslandPartition.Prepare` is a union-find over in-service branches. It groups the buses into connected components and
treats each island as follows:

| Island has | Action |
|---|---|
| a slack bus | Kept as it is. |
| generation but no slack | The bus carrying the largest single generator (by `abs(Pg)`) is promoted to slack. |
| no generation | Blacked out: its buses, branches and generators are dropped, and its demand and generation are added to `LostLoadMw` and `LostGenMw`. |

A model that is one component containing a slack bus is returned untouched. The reclose reverses the topology on the
next tick, where the controller's hold count reaches zero and the branch leaves `OpenBranches`.

## Parameters and defaults

Class keys of a protection (`AssetParams`, `src/GridSim.Core/Assets/AssetClassLibrary.cs`; the accepted set is
`AssetClassSupport.AcceptedKeys(AssetKind.Protection)`). A key omitted from a class builds no stage or takes the default shown; a dwell key omitted while its threshold key is set is refused.

| Key | Unit | When omitted | Meaning |
|---|---|---|---|
| `rocofTripHzPerS` | Hz/s | no stage | RoCoF/loss-of-mains pickup on `abs(RoCoF) * uplift`. |
| `rocofTripConfirmS` | s | refused when `rocofTripHzPerS` is set | RoCoF dwell. |
| `localRocofUpliftFactor` | ratio | 1.0 | Scales the RoCoF reading of stages 1 and 2. |
| `dfDtTripHzPerS` | Hz/s | no stage | df/dt pickup. |
| `dfDtTripDwellS` | s | refused when `dfDtTripHzPerS` is set | df/dt dwell. |
| `ufTripHz` | Hz | no stage | Under-frequency pickup. |
| `ufTripDwellS` | s | refused when `ufTripHz` is set | Under-frequency dwell. |
| `ofTripHz` | Hz | no stage | Over-frequency pickup. |
| `ofTripDwellS` | s | refused when `ofTripHz` is set | Over-frequency dwell. |
| `uvTripPu` | pu | no stage | Under-voltage pickup. |
| `uvTripDwellS` | s | refused when `uvTripPu` is set | Under-voltage dwell. |
| `ovTripPu` | pu | no stage | Over-voltage pickup. |
| `ovTripDwellS` | s | refused when `ovTripPu` is set | Over-voltage dwell. |
| `lvrtVoltagePu` | pu | no stage | Low-voltage trip, instantaneous. A value of 0 or less builds no stage. |
| `overloadTripFraction` | fraction of rating | no stage | Thermal-overload pickup on the incident-branch loading. |
| `overloadTripDwellS` | s | refused when `overloadTripFraction` is set | Overload dwell. |
| `vectorShiftTripDeg` | deg | no stage | Vector-shift pickup on the phase moved over the window. A non-finite value builds no stage. |
| `detectionLatencyS` | s | 0 | Relay latency applied to the frequency and RoCoF readings. |
| `lfddStageHz` | Hz | no stage | LFDD pickup (frequency below). |
| `lfddDwellS` | s | refused when `lfddStageHz` is set | LFDD dwell. |
| `lfddBlockMw` | MW | placed `capacityMw` | Demand shed by the stage. |
| `autoRecloseDelayS` | s | not a recloser | Makes the class a recloser; the reset delay of its `OpenBranch` stages. |
| `settingsNominalHz` | Hz | 50 | The nominal the frequency settings are written for; on a system at another nominal they move by the difference. Load refuses a `ufTripHz` or `lfddStageHz` that is not strictly below it, an `ofTripHz` that is not strictly above it, and any of them more than 10 Hz from it on its own side (`AssetClassSupport.MaxFrequencySettingDistanceHz`). |

Fixed values in the code:

| Value | Setting | Source |
|---|---|---|
| Phase window | `max(DtSeconds, 0.02)` s | `ProtectionController.Update` |
| Delay history | 512 samples | `MeasurementDelay.Capacity` |
| Read tolerance | `1e-12` s on the latency cut-off | `MeasurementDelay.Read` |
| Sub-step | at most 0.02 s | `AssetAwareFrequencyIntegrator.MaxInternalDt` |
| Sub-steps per tick | at most 4000 | `AssetAwareFrequencyIntegrator.MaxSubSteps` |
| Live trip-deficit decay | 300 s | `LiveSim.SecondaryTauS` |

This layer ships no pickup defaults of its own: a stage exists only for a key the class sets, and the two bundled
classes above are the whole shipped set. The ENA G99 Issue 2 Table 10.1 interface-protection settings (27 at 0.80 pu,
59 at 1.10 pu, 81U at 47.5 Hz, 81O at 52.0 Hz, 81R at 1.0 Hz/s with 0.5 s) are the pickups of the passive relay overlay,
`ProtectionSettings` in `src/GridSim.Core/Protection/ProtectionTypes.cs`, and are specified in chapter 17. A class in this
layer reproduces them by setting the keys above to the same numbers.

## Invariants

| Invariant | Pinned by |
|---|---|
| No `protections.json` gives a trajectory byte-identical to the lumped baseline. | `ProtectionTests.Reduction_gate_no_protections_is_byte_identical` |
| A device whose threshold is never reached gives a byte-identical trajectory. | `ProtectionTests.A_protection_that_never_operates_is_byte_identical` |
| A RoCoF stage trips at least the placed `capacityMw` (the test asserts a lower bound) and deepens the nadir. | `ProtectionTests.Rocof_lom_protection_trips_and_deepens_the_nadir` |
| An under-frequency stage operates when the frequency falls through its pickup. | `ProtectionTests.Under_frequency_protection_trips_when_frequency_falls_through_its_stage` |
| Dynamic LFDD sheds demand, raises the nadir, and is not counted as a trip. | `ProtectionTests.Dynamic_lfdd_sheds_demand_and_arrests_the_fall` |
| A stage fires only after its dwell, and a spike shorter than the dwell does not fire. | `ProtectionEvaluatorTests.Fires_only_after_the_dwell_is_satisfied`, `A_sub_dwell_spike_does_not_fire` |
| A finite reclose delay resets the stage. | `ProtectionEvaluatorTests.Auto_reclose_restores_after_the_delay` |
| A stage is blocked until its arm delay has passed. | `ProtectionEvaluatorTests.Arm_delay_blocks_operation_until_armed` |
| An under-voltage stage trips on a fed low voltage and stays quiet at nominal; over-voltage and overload trip on their fed quantities. | `ProtectionVoltageTests.Under_voltage_protection_trips_when_fed_a_low_voltage_and_is_inert_at_nominal`, `Over_voltage_and_thermal_overload_trip_on_their_fed_quantities` |
| An under-voltage stage is inert in the pure lumped model (the test covers under-voltage only; over-voltage and overload rest on the strict comparison above). | `ProtectionVoltageTests.Voltage_protection_is_inert_in_the_pure_lumped_model` |
| A recloser opens its branch on its condition without an infeed trip, holds it through the delay, and recloses. | `ProtectionRecloserTests.Recloser_opens_its_branch_on_under_frequency_and_recloses_after_the_delay` |
| Opening a branch partitions the model and books the lost load. | `ProtectionRecloserTests.TopologyOps_applies_an_open_branch_and_partitions` |
| No open branch is a no-op. | `ProtectionRecloserTests.No_open_branches_is_a_no_op`, `IslandPartitionTests.All_in_service_is_a_no_op` |
| An island with generation gets a slack; a dead island is blacked out with its load booked; the adjusted model solves. | `IslandPartitionTests.Islanding_a_generating_region_elects_a_slack`, `Islanding_a_dead_region_blacks_it_out_and_books_the_lost_load`, `The_adjusted_islanded_model_solves` |
| A recloser with no target branch fails loud; an invalid placement names its problem (each test row has one problem); a valid one passes. | `ProtectionWs6Tests.A_recloser_with_no_target_branch_fails_loud`, `An_invalid_placement_fails_loud`, `A_valid_placement_passes` |
| The shipped placement validates, and the loader calls `ValidateAgainst`. | `FollowUpWiringTests` |
| A `RestoreBranch` stage is refused. | `AssetClassSupportTests.A_restore_branch_protection_stage_is_refused_rather_than_silently_doing_nothing` |
| An `armDelayS` key, or any key no protection reads, is refused. | `AssetClassSupportTests.A_key_no_consumer_of_the_kind_reads_is_refused` |
| The accepted keys equal the v2 kind specs less the refused keys; the bundled classes load. | `AssetClassSupportTests.The_accepted_keys_match_the_v2_kind_specs_less_the_refused_keys`, `The_bundled_classes_load_and_no_longer_carry_the_unread_ramp_key` |
| A `CuratedPlaceholder` note without `[VERIFY]` is refused. | `ProtectionTests.Protection_provenance_verify_rule_is_enforced` |

## Worked example

The run is the `trip` verb on the bundled `data/gb-synthetic` case with `--asset-dynamics`. `trip` removes the largest
non-slack synchronous unit at t = 1.0 s: Hinkley Point Nuclear, 2880 MW. The case places two devices on bus 1:
`embedded-dg-lom` (RoCoF 0.125 Hz/s, uplift 1.16, 800 MW) and `system-lfdd-1` (48.8 Hz, 632 MW block). The runs
are deterministic and the captured output carries no timing stamps.

To isolate the two devices, the runs below use copies of the case under `results/protections/`: `prot-only` is
`data/gb-synthetic` with `asset-bindings.json` removed, so only the protections act and no inverter responds;
`lom-only` and `lfdd-only` are that copy with one placement line deleted from `protections.json`.

```
$ gridsim trip results/protections/prot-only --asset-dynamics
Asset dynamics: on  (8 class(es), 2 protection(s), 1 unit(s) tripped, 800 MW lost to LoM)
GridSim  -  contingency on GB synthetic
================================================================
Tripped:        Hinkley Point Nuclear  (2880 MW lost, nuclear)
System inertia: 171.7 -> 158.9 GVAs after the trip
Peak RoCoF:     -0.579 Hz/s  (largest |df/dt| over the run)
Nadir:          48.186 Hz  (t+6.3 s)
Settling:       49.076 Hz

50.05 Hz |***+=-:,,..           ...,,,::::-----============-----------::::| 48.19 Hz (nadir)

...at HALF the inertia (79.5 GVAs): RoCoF -0.906 Hz/s, nadir 47.872 Hz
[exit 0]
```

The header line reports the library's 8 classes (the file defines six inverter classes besides the two protections),
2 placed protections, and 1 unit tripped: the LoM device. The LFDD device sheds demand, so it is not a tripped unit.
The last line is a baseline without the fleet and does not change between runs. Its nadir equals the LoM-only nadir
(47.872 Hz) to three decimals by coincidence; the two runs have different inertia and deficit.

What each device did, from the four captures in `docs/_captures/protections/`:

| Case | Capture | Peak RoCoF (Hz/s) | Nadir (Hz) | Nadir time | Settling (Hz) |
|---|---|---|---|---|---|
| No asset dynamics | `trip-no-assets.txt` | -0.453 | 48.334 | t+6.9 s | 49.142 |
| LoM only | `trip-lom-only.txt` | -0.579 | 47.872 | t+6.9 s | 48.903 |
| LFDD only | `trip-lfdd-only.txt` | -0.453 | 48.592 | t+5.8 s | 49.307 |
| LoM and LFDD | `trip-prot-only.txt` | -0.579 | 48.186 | t+6.3 s | 49.076 |
| Full case: protections plus inverter fleet | `trip-asset-dynamics.txt` | -0.576 | 49.052 | t+5.5 s | 49.525 |

The numbers follow from the equations:

- Without the fleet the first post-event RoCoF is `50 * 2880 / (2 * 158900) = 0.453 Hz/s` (`E` is the 158.9 GVA.s after
  the trip, 158900 MW.s).
- The LoM stage reads `0.453 * 1.16 = 0.526 Hz/s`, above its 0.125 Hz/s pickup, with a 0 s dwell. It fires on the first
  fleet update after the event, at t = 1.0 s, and adds 800 MW to the deficit. The new imbalance is 3680 MW and
  `50 * 3680 / (2 * 158900) = 0.579 Hz/s`, which is the captured peak.
- With LoM and LFDD both placed, the exported trajectory (`--export`) first reads under 48.8 Hz at the sub-step at
  t = 3.36 s (48.79468 Hz; the step before reads 48.80311 Hz). The LFDD stage fires on that sub-step. The following
  step falls 0.00641 Hz where the LoM-only run falls 0.00840 Hz, a difference of 0.00199 Hz per 20 ms step, against
  the predicted `50 * 632 / (2 * 158900) * 0.02 = 0.0019887 Hz`: the 632 MW block, entering as a positive swing term.
  The exported CSV is rounded to 5 decimals.
- LFDD alone (first RoCoF 0.453 Hz/s) crosses 48.8 Hz later, at t = 4.24 s, and holds the nadir at 48.592 Hz.
- With the LoM trip alone the nadir is 47.872 Hz; adding the LFDD block lifts it to 48.186 Hz.
- The full case adds the inverter fleet bound by `asset-bindings.json`. Its nadir is 49.052 Hz, above the 48.8 Hz LFDD
  stage, so LFDD does not operate in that run; the LoM device still trips (1 unit, 800 MW in the header).

To reproduce the full-case row, run `gridsim trip data/gb-synthetic --asset-dynamics`.

## Source map

| File | Type or member | Responsibility |
|---|---|---|
| `src/GridSim.Core/Assets/Protection/ProtectionElement.cs` | `MonitoredQuantity`, `ThresholdSense`, `ProtectionAction`, `ProtectionStage` | The vocabulary: what a stage watches, how it compares, what it does. |
| `src/GridSim.Core/Assets/Protection/ProtectionEvaluator.cs` | `ProtectionEvaluator`, `ProtectionEdge` | The dwell, arm and reclose state machine of one stage; refuses `RestoreBranch`. |
| `src/GridSim.Core/Assets/Protection/ProtectionController.cs` | `ProtectionController` | Builds stages from a class, reads the delayed measurements, applies actions, tracks held branches. |
| `src/GridSim.Core/Assets/Protection/ProtectionModel.cs` | `ProtectionInstance`, `ProtectionModel.ValidateAgainst` | Placement record and the placement-against-network check. |
| `src/GridSim.Core/Assets/AssetDynamicsFleet.cs` | `AssetDynamicsFleet.Build`, `StepCoi`, `OpenBranches` | Creates controllers per placement, steps the fleet, aggregates swing power, deficit and open branches. |
| `src/GridSim.Core/Assets/GridFollowingInverter.cs` | `MeasurementDelay` | The 512-sample delay history shared with the inverter controllers. |
| `src/GridSim.Core/Assets/IAssetDynamics.cs` | `AssetStepContext`, `AssetActuation`, `IActiveAssetDynamics` | The seam. |
| `src/GridSim.Core/Assets/AssetClassLibrary.cs` | `AssetClass`, `AssetClassLibrary`, `AssetClassSupport`, `AssetParams` | Class parsing, accepted keys, `[VERIFY]` rule. |
| `src/GridSim.Core/IO/Json/ProtectionDto.cs`, `JsonGridLoader.cs`, `V2CaseLoader.cs` | `ProtectionInstanceDto`, `BuildProtections`, `MapProtection` | Reading `protections.json` (v1) and the protections member (v2). |
| `src/GridSim.Core/Dynamics/AssetFrequencyDynamics.cs` | `AssetFrequencyDynamics.Simulate` | Scripted lumped run with the fleet (the `trip` verb). |
| `src/GridSim.Core/Dynamics/AssetAwareFrequencyIntegrator.cs` | `AssetAwareFrequencyIntegrator`, `SetNetworkState` | Running-clock integrator with the fleet and the network feed. |
| `src/GridSim.Core/Simulation/LiveSim.cs` | `LiveSim.Step`, `BuildVoltageOf`, `BuildLoadingOf` | Network feed, topology application, blackout perturbation, trip-deficit decay. |
| `src/GridSim.Core/PowerFlow/TopologyOps.cs` | `TopologyOps.Apply` | Marks opened branches out of service and partitions. |
| `src/GridSim.Core/PowerFlow/IslandPartition.cs` | `IslandPartition.Prepare`, `IslandReport` | Islands, slack election, blackout. |
| `src/GridSim.Core/Model/Model.cs` | `Branch.InService`, `GridModel.Protections` | The model fields. |
| `src/GridSim.Core/PowerFlow/YBus.cs`, `SparseNewtonRaphsonPowerFlow.cs`, `BackwardForwardSweep.cs`, `NewtonShell.cs` | admittance and flow assembly | Skip out-of-service branches. |
| `src/GridSim.Cli/Program.cs` | `trip --asset-dynamics` | The verb that runs a protection event on a case. |
| `tests/GridSim.Tests/` | `ProtectionTests`, `ProtectionEvaluatorTests`, `ProtectionVoltageTests`, `ProtectionRecloserTests`, `ProtectionWs6Tests`, `IslandPartitionTests`, `AssetClassSupportTests`, `FollowUpWiringTests` | The pins listed under Invariants. |

## Limits

- Every stage is definite-time; inverse-time characteristics are not part of this layer.
- Frequency and RoCoF stages read the single lumped centre-of-inertia signal, so an island created by a recloser has no
  frequency of its own and `monitorBus` selects only the bus for the voltage and loading readings.
- Voltage and thermal stages operate only under `LiveSim` with a solved network; the scripted `AssetFrequencyDynamics`
  run (`trip`, `h0-study`, asset sweeps) and the fleet's sampler path feed NaN voltage and loading, so those stages are
  not evaluated there.
- In the scripted runs a tripped infeed stays in the fleet deficit and an LFDD shed stays in `pAssetFleet` until the
  fleet is reset; only `LiveSim` restores them by secondary response.
- A class holds one LFDD stage (`lfddStageHz`), so a multi-stage scheme needs one class and one placed device per stage.
- Reconnection is a single step of the whole blacked-out block: there is no staged restoration, no cold-load
  inrush above the pre-outage demand, and no resynchronising delay for generation in the dead island.
- `ProtectionStage.ArmDelayS` and `ProtectionAction.InjectStep` cannot be set from a class file.
- The recloser, auto-reclose, measured-voltage, thermal-loading and fault-level settings have no recorded source; they are
  `CuratedPlaceholder` with the `[VERIFY]` token.
- The case writer does not write `protections.json`; a written case needs the file copied across by hand.

## See also

- [Ch. 5, Frequency dynamics](<0005 Frequency dynamics.md>): the swing equation and integrators the fleet plugs into.
- [Ch. 13, Asset dynamics data grounding](<0013 Asset-dynamics data grounding.md>): the grounding ledger of the shipped classes.
- [Ch. 17, Protection assessment](<0017 Protection assessment (a passive relay overlay).md>): the passive relay overlay and the G99 pickups.
- [Ch. 25, Short-circuit, IEC 60909](<0025 IEC 60909 short-circuit duty study.md>) and
  [ch. 26, Overcurrent coordination](<0026 Overcurrent coordination (IDMT grading).md>).
- [Ch. 8, Provenance and invariants](<0008 Provenance and invariants.md>): the tags and the `[VERIFY]` rule.
- [Ch. 29, Extending GridSim](<../manual/0029 Extending GridSim.md>): authoring a protection class.
- [Ch. 14, Frequency studies](<../manual/0014 Frequency studies.md>) and the `trip` section of
  [ch. 19, CLI reference](<../manual/0019 CLI reference.md#trip>).
