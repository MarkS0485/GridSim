# 7. The evidence engine

The evidence engine turns a recorded GB frequency event into a counterfactual claim that can be audited. It has four
stages, and each stage refuses to run unless the one before it passed. The event model describes a real event from
the record. The replay validator reproduces that record with no device and issues a verdict. The scenario, fleet and
H0 engines then ask what a device (or a changed grid) would have done, and only a `Reproduced` verdict lets them run.
The evidence report assembles the dossier and computes the avoided cost with every term exposed. This chapter specifies
the event and catalogue schema, trace ingestion, the validator's metrics, tolerances and verdict rules, the scenario
corners and counterfactual trajectory, fleet additivity, the avoided-cost formula with each constant and its provenance
tag, how the forward-inference guard applies, determinism, and the tests that pin each rule.

The code is in `src/GridSim.Core/Events/`, `Replay/`, `Scenarios/`, `Fleet/`, `Evidence/` and `Frequency/`, and in the
`replay-event`, `scenario`, `h0-study`, `fleet` and `evidence` verbs of `src/GridSim.Cli/Program.cs`. The swing
equation, `DynamicsConfig`, the response-service curves and `FrequencyLimits` belong to
[ch. 5, Frequency dynamics](<0005 Frequency dynamics.md>) and are used here as defined there. `ProvenanceTag` and
the forward-inference rule belong to [ch. 8, Provenance and invariants](<0008 Provenance and invariants.md>). The
Loss-of-Mains cascade that the H0 study uses is validated in
[ch. 14, Cascade validation and blind prediction](<0014 Cascade validation and blind prediction.md>). Flags and
exit codes per verb are in [ch. 19, `replay-event`](<../manual/0019 CLI reference.md#replay-event>); the task chapters are
[ch. 18, Replaying recorded events](<../manual/0018 Replaying recorded events.md>) and
[ch. 20, Counterfactuals](<../manual/0020 Counterfactuals.md>).

## Model

### The gated pipeline

```
GridEvent (record)
   |  ToFrequencyEvents()  = losses + recorded actions
   v
ReplayValidator.Validate  -- no device, recorded services, window covers the whole sequence
   |  verdict: Reproduced | Marginal | Failed
   |
   +-- Reproduced only ----------------------------+
   |                                               |
   v                                               v
ScenarioEngine / H0StudyEngine                EvidenceReport.Build
FleetDynamics (CLI `fleet` verb)              (columns, risk curve, fleet, avoided cost)
   |  ExogenousLossEvents() = losses only
   v
counterfactual trajectory = FrequencyDynamics.Simulate / FleetDynamics.Simulate
```

Every consumer runs the gate itself:

| Consumer | On a verdict short of `Reproduced` | Exit code |
|---|---|---|
| `replay-event` | prints the verdict and `NOT reproduced` | 3 |
| `ScenarioEngine.Run`, `H0StudyEngine.Run` | throw `BaselineNotReproducedException` (the message contains "Reproduce reality first." followed by the baseline summary); the verbs print `GATE FAILED: ...` | 3 |
| `fleet` verb | prints `GATE FAILED: baseline verdict is X, not Reproduced - ...` | 3 |
| `EvidenceReport.Build` | returns a report with `CounterfactualUnlocked = false`, only the `recorded` and `simulated (no device)` columns, and no risk curve, fleet or cost section; the `evidence` verb still writes it | 3 |

There is no flag that overrides the gate. `Marginal` locks the counterfactual exactly as `Failed` does.

### Event model

`GridEvent` (`src/GridSim.Core/Events/GridEvent.cs`) describes a real event from the record. Times inside an event are
seconds from the event onset (`WhenUtc`). Inertia is in MW.s, so the 215 GVA.s that NESO publishes is `215000`.

| Field (JSON key) | Type | Unit | Meaning |
|---|---|---|---|
| `Id` (`id`) | string | - | Event identifier. `EventCatalog.Get` matches it case-insensitively and returns the first match. |
| `Title` (`title`) | string | - | Display title. |
| `WhenUtc` (`whenUtc`) | `DateTime` | UTC | Event onset. Passes the forward-inference guard on every load. |
| `PreEventInertiaMwS` (`preEventInertiaMwS`) | double | MW.s | System inertia before the event; the `E` of the baseline replay. |
| `PreEventDemandMw` (`preEventDemandMw`) | double | MW | Transmission-net demand before the event; sets load damping and the governor's responsive capacity. |
| `Losses` (`losses`) | `PowerStep[]` | - | The exogenous generation losses. |
| `RecordedActions` (`recordedActions`) | `RecordedAction[]` | - | Protective or operator actions the record shows (LFDD, balancing-mechanism recovery). |
| `Outcome` (`outcome`) | `RecordedOutcome` | - | The headline metrics a replay must reproduce. |
| `Provenance` (`provenance`) | `ProvenanceTag` | - | Event-level tag. |
| `Notes` (`notes`) | string, optional | - | Source citations; unsourced figures carry `[VERIFY]`. |
| `ResponseServices` (`responseServices`) | `ResponseService[]`, optional | - | Frequency-response services the system held. `null` is an empty list. |
| `ResponseServicesComplete` (`responseServicesComplete`) | bool, default `false` | - | `true` declares that the list enumerates all frequency response the system held. |
| `PreEventFrequencyHz` (`preEventFrequencyHz`) | double, optional | Hz | Recorded frequency the trip landed on. `null` starts the replay at `NominalHz`. |

`PowerStep` (`losses[]`): `AtSeconds` (s), `DeltaPMw` (MW; negative is a loss, positive a restoration), `What`
(string citing the source, or carrying `[VERIFY]`), `Provenance`. `RecordedAction` (`recordedActions[]`) has the same
four fields. A recorded action enters the swing model as its `DeltaPMw` with its sign unchanged, so a load shed is
positive: load removed reduces the deficit. Feeding a recorded action in is replay, not prediction.

`RecordedOutcome` (`outcome`):

| Field | Type | Unit | Meaning |
|---|---|---|---|
| `InitialRocofHzPerS` | double | Hz/s | Recorded RoCoF. Magnitudes are compared; the sign is informational. |
| `NadirHz` | double | Hz | Recorded minimum frequency. |
| `SettleHz` | double, optional | Hz | Recorded settling frequency. Reported, never scored. |
| `LfddMwShed` | double | MW | Total demand disconnected by LFDD; `0` if none. The avoided-cost calculation reads it. |
| `NesoTraceCsvPath` | string, optional | path | A 1-second NESO trace. When it names an existing file the validator adds the per-sample RMSE. |
| `Provenance` | `ProvenanceTag` | - | Tag of the outcome figures. |
| `Notes` | string, optional | - | Source details. |
| `RocofMeasurementWindowS` | double, default `0` | s | Measurement basis of `InitialRocofHzPerS`. `0` is an instantaneous or instrument figure, scored against the simulator's 20 ms peak. A positive value is a windowed figure (for example `1.0`, the maximum 1-second delta on the public 1 Hz trace), and the simulator is scored on the same grid. |

`ResponseService` (`responseServices[]`): `Mw` (MW), `DeadbandHz` (Hz, deviation from nominal),
`FullDeliveryDeviationHz` (Hz), `TimeConstantS` (s), `What`, `Provenance`, `KneeDeviationHz` (Hz, default `0`) and
`KneeFraction` (fraction of `Mw`, default `0`). The knee pair carries a service's low-gain segment: Dynamic Containment
is knee `(0.2 Hz, 0.05)`, so it delivers 5 % of its volume by 0.2 Hz and the rest by `FullDeliveryDeviationHz`. A knee at
the deadband with fraction `0` is a plain ramp from the deadband (Dynamic Moderation and Regulation). A knee at or below
zero is a droop line through zero. The curve itself, `SwingKernel.FastResponseTarget`, is specified in ch. 5.

`GridEvent.ApplyRecordedServices(cfg)` is the single mapping from event to dynamics. It returns `cfg` with:

```
SystemResponseServices = ResponseServices as SystemResponseService list   (empty if null)
InitialFrequencyHz     = PreEventFrequencyHz
ResponsiveFraction     = 0.0   if ResponseServicesComplete, else unchanged
```

The validator, the scenario engine, the H0 engine, the evidence report and the `fleet` verb all call it, so every path
integrates the same recorded operating state. `ResponseServicesComplete = true` zeroes the generic governor term
(`DynamicsConfig.ResponsiveFraction`), because a complete list already is the system's response and keeping the governor
would count it twice. Load damping is physics, not procurement, and always stays. A start at `PreEventFrequencyHz` is an
equilibrium start: the governor and service lag states are pre-loaded to their static targets at that offset and a
constant standing imbalance cancels them, so `df/dt` is zero at `t = 0` (ch. 5).

`TotalLossMw` is the sum of `|DeltaPMw|` over `Losses`.

### Two projections of one event

| Projection | Contents | Used by |
|---|---|---|
| `ToFrequencyEvents()` | losses and recorded actions, ordered by time (`OrderBy`, stable) | the baseline replay, `fleet`'s gate, the exported baseline trajectory |
| `ExogenousLossEvents()` | the losses only, ordered by time | every counterfactual: scenario cells, the risk curve, the dossier's counterfactual column, fleet runs, H0 cells |

The counterfactual drops every recorded action, not only LFDD. A recorded action is an endogenous consequence of the
disturbance (a relay or an operator reacted), and a device is meant to replace it; feeding it back would count the
action twice.

### Catalogue schema

`EventCatalog` (`src/GridSim.Core/Events/EventCatalog.cs`) reads and writes JSON with schema id
`gridsim-event-catalog/1`:

```
{ "schema": "gridsim-event-catalog/1", "events": [ <GridEvent>, ... ] }
```

Serialisation is camelCase keys, enums as strings (`"Recorded"`), `null` members omitted on write, indented. Reading is
case-insensitive on keys. `EventCatalog.Load` throws `InvalidDataException` when the document deserialises to `null` or when
`schema` or `events` is missing, and a document that is not JSON throws `JsonException`; the verbs exit 1 on both. It checks that `schema` is present, not its value. `EventCatalog.Save` always writes
the id above. `replay-event --dump-catalog <path>` writes the built-in catalogue and exits.

Every event passes `ForwardInferenceGuard.RejectFuture(WhenUtc, utcNow, "event '<id>'")` in `EventCatalog.Validate`,
which `Builtin`, `Load` and `Get` all run. A catalogue therefore cannot introduce an event newer than the horizon.

### Built-in events

| Id | `WhenUtc` | `E` (GVA.s) | Demand (MW) | Steps | Outcome (RoCoF, nadir, LFDD shed) | Provenance | Verdict without a trace |
|---|---|---|---|---|---|---|---|
| `gb-2019-08-09` | 2019-08-09 15:52:33 | 215 | 21,100 | 9 losses (2,768 MW in total, last at 90 s), 1 LFDD action (+892 MW at 76 s) | -0.17 Hz/s, 48.8 Hz, 892 MW | `Recorded` (two steps `DerivedFromRecorded`) | `Reproduced` |
| `gb-2023-12-22` | 2023-12-22 13:09:51 | 161 | 22,400 | 2 losses (1,000 MW at 0.5 s, 345 MW at 9.5 s), 12 balancing-mechanism recovery steps (+904 MW in total, 19 s to 129 s), 3 services (873 + 150 + 203 = 1,226 MW, complete), pre-event 49.879 Hz | -0.102 Hz/s (1.0 s window), 49.275 Hz, 0 MW | `Recorded` (losses and BM steps `DerivedFromRecorded`) | `Marginal` |
| `gb-largest-loss-1320` | 2020-01-01 12:00:00 | 200 | 35,000 | 1 loss (1,320 MW at 1.0 s) | -0.165 Hz/s, 49.2 Hz, 0 MW | `CuratedPlaceholder`, `[VERIFY]` | `Reproduced` |

The 2019 loss sequence and outcome are sourced paragraph by paragraph to the Ofgem investigation report (paragraphs
2.4.1 to 2.4.16), and the operating point is NESO's recorded outturn inertia and the state estimator's transmission-net
demand. The 2023 loss inventory is settlement-metered, its outcome is read off NESO's public 1-second trace, and its
services are the cleared volumes of EAC auction 70. `gb-largest-loss-1320` is a design-basis screening case; its
"recorded" figures are placeholders that agree with the model to the gate tolerance (nadir 49.2 Hz against a simulated 49.24 Hz, RoCoF -0.165 Hz/s against -0.1650), so the gate reproduces it and its tag stays
`CuratedPlaceholder` (the gate tests consistency with the record, not that the record is real). Its effective provenance
(the weakest of its parts) is `CuratedPlaceholder`, so `replay-event` refuses it (only an event sourced from a record is
replayed as a reproduction); the study verbs (`scenario`, `fleet`, `evidence`, `h0-study`) run it and their outputs carry
the weaker tag ([ch. 8](<0008 Provenance and invariants.md>)). The 2019 event also ships
as `data/events/gb-2019-08-09-estimated.json`, with the measured demand 21,147.2 MW and onset `15:52:00Z`, and is loaded
with `--catalog`.

### Trace ingestion

`NesoFrequencyCsv.Read(path)` (`src/GridSim.Core/Frequency/NesoFrequencyCsv.cs`) streams a NESO system-frequency CSV one
row at a time, so a month of 1-second data (2.7 million rows) reads without being held in memory. The bundled traces
are `data/neso/gb-2019-08-09-frequency.csv` and `data/neso/gb-2023-12-22-frequency.csv` (601 samples each), and
`data/neso/frequency-sample.csv`. A row is `timestamp,hz`:

```
dtm,f
2019-08-09 15:50:00,50.037
2019-08-09 15:50:01,50.035
```

- The row is split at its first comma. A line with no comma, a comma in column 0, a timestamp that does not parse or a
  value that does not parse is skipped without a message. That is how the header and blank lines are dropped.
- The timestamp is parsed with the invariant culture and `AssumeUniversal | AdjustToUniversal`: a naive stamp is UTC and
  a stamp with `Z` or an offset is converted to UTC. Machine-local time never enters.
- The value is parsed with `NumberStyles.Float` and the invariant culture.
- Every accepted row passes `ForwardInferenceGuard.RejectFuture(t, what: "NESO frequency sample")`.

The row type is `FrequencySample(Time, Hz)`. `RocofUpsampler` (`src/GridSim.Core/Frequency/RocofUpsampler.cs`) refines the
trace:

```
for each consecutive pair (prev, cur) with dt = cur.Time - prev.Time > 0:
    rocof = (cur.Hz - prev.Hz) / dt
    step  = dt / sub
    for k = 0 .. sub-1:   emit  Time = prev.Time + k*step,  Hz = prev.Hz + rocof * (k*step),  RoCoF = rocof
a pair with dt = 0 is dropped (prev := cur); a pair with dt < 0 (out of time order) is refused (GR-572)
after the last pair emit the last sample with RoCoF = 0
```

`N` ordered samples give `(N - 1) * sub + 1` points. The validator passes `sub = round(1 / StepSeconds)`, which is 50 and a
20 ms grid for the default step and a 1-second trace. `RocofUpsampler.Rocof` yields the per-interval slope paired with the
later sample's time, and `Summarize` returns the count, minimum, maximum, mean and the largest `|slope|` with its time.

The word "window" names three different things in this engine:

| Window | Where | Meaning |
|---|---|---|
| `RocofMeasurementWindowS` | `RecordedOutcome` | The basis of the recorded RoCoF figure and of the simulated statistic it is scored against. |
| 5 s inertia window | `ReplayValidator.Validate` | Only losses at `AtSeconds <= 5` count when the recorded RoCoF is inverted for inertia. |
| `PostEventTailSeconds` | `ReplayValidator` | 30 s of simulation after the last event. |

The 2023 outcome is a windowed figure and the bundled trace reproduces it: the largest 1-second delta on
`gb-2023-12-22-frequency.csv` is -0.102 Hz at 13:09:53 and its minimum is 49.275 Hz at 13:10:11. On the 2019 trace the
largest 1-second delta is -0.151 Hz at 15:52:35 and the minimum is 48.787 Hz at 15:53:49, 76 s after onset; the 2019
outcome carries the instrument figure -0.17 Hz/s with window `0`.

## Algorithm

### The replay validator

`ReplayValidator.Validate(ev, config, tol)` (`src/GridSim.Core/Replay/ReplayValidator.cs`) runs these steps.

1. **Strip the device.** `config` (default `new DynamicsConfig()`) is copied with `DeviceRotationalInertiaMwS = 0` and
   `FastResponseMw = 0`. A caller cannot put a device into the baseline.
2. **Cover the whole sequence.** `ExtendToCover` sets
   `DurationSeconds = max(DurationSeconds, max(event.AtSeconds) + PostEventTailSeconds)`, with `PostEventTailSeconds = 30`.
   It takes the maximum event time, so the list need not be sorted, and it never shortens a window. For 2019 that is
   `max(30, 90 + 30) = 120 s`. A window clipped at the 30 s default would replay a different event from the one recorded.
   The scenario engine, the H0 engine and the evidence report call the same helper.
3. **Stamp the recorded state.** `ev.ApplyRecordedServices(cfg)` (above). The recorded services stay in the baseline: they
   are facts about the system, not device terms.
4. **Simulate.** `FrequencyDynamics.Simulate(PreEventInertiaMwS, PreEventDemandMw, ToFrequencyEvents(), cfg)`, a forward
   Euler step of `StepSeconds = 0.02` s. The step in use is, with `E = max(1, E0)`, `d = DampingPctPerHz/100 * demand`
   and `pResp = ResponsiveFraction * demand`:

   ```
   pEvent(t)  = sum of DeltaPMw over events with AtSeconds <= t + 1e-6 dt
   pGovTarget = -(df / f0) / DroopPu * pResp          pGov += (pGovTarget - pGov) / GovernorTimeConstantS * dt
   pServices  = sum of lagged service outputs (curve of df, lag TimeConstantS)
   rocof      = f0 / (2 E) * (pEvent + pStanding + pGov + pServices - d * df)
   f         += rocof * dt                             df = f - f0
   ```

   The response of the whole run is `InitialRocofHzPerS` (the largest-magnitude `rocof` over all steps, signed), `NadirHz`
   (the minimum of the start-of-step `f`), `NadirSeconds` and `SettleHz` (the frequency after the last update).
5. **RoCoF on the recorded basis.**

   ```
   simRocof = Outcome.RocofMeasurementWindowS > 0 ? MaxWindowedRocof(sim, w) : sim.InitialRocofHzPerS
   ```

   `MaxWindowedRocof` reproduces what a logger sampling every `w` seconds would have read. With `dt` the step and
   `stride = max(1, round(w / dt))` (50 for `w = 1`), it takes each of the `stride` sub-second phases `p`, finds the signed
   slope `(f[i + stride] - f[i]) / (stride * dt)` of largest magnitude over `i = p, p + stride, ...`, sorts the per-phase
   maxima by magnitude and returns the element at index `count / 2`, the median. The logger's phase against the event is
   unknown, so the median over phases is the unbiased single figure. A continuous sliding-window maximum is not used: it
   always finds the worst-aligned window, a steeper reading than any sampled trace can contain.
6. **Errors.**

   ```
   rocofRelError = |rec| < 1e-9  ?  |simRocof|  :  ||simRocof| - |rec|| / |rec|
   nadirError    = |sim.NadirHz - Outcome.NadirHz|                     (Hz)
   ```

7. **Inertia cross-check.** The recorded RoCoF is inverted for the inertia it implies, using only the losses that set it:

   ```
   dP = sum |DeltaPMw| over Losses with AtSeconds <= 5         (TotalLossMw if that sum is 0)
   rec' = rec                                                   (instantaneous record, window 0)
   rec' = rec * |sim.InitialRocofHzPerS| / |simRocof|           (windowed record, window > 0)
   InferredInertiaMwS = NominalHz * dP / (2 * |rec'|)          (+infinity when |rec'| < 1e-9)
   ```

   A windowed record averages the fall over a window in which the response services are already delivering, so it is
   shallower than the instantaneous slope the inversion needs. It is scaled by the replay's own ratio of its instantaneous
   peak to its same-window figure, which assumes the real system's ratio is the replay's. When either replay figure is
   below `1e-9` in magnitude there is no ratio, and `InferredInertiaMwS` is NaN. For a windowed record `InertiaCrossCheckNote` states
   the ratio used or why no figure was inferred; it is null for an instantaneous record.

   Recorded actions never enter `dP`. The 5 s window covers the near-instant losses and the fast loss-of-mains cascades; it
   excludes a staged sequence's late steps (the 58 s and 90 s trips on 9 August 2019 do not set the initial slope). The
   result is reported beside the event's stated inertia and is not scored.
8. **Declared-uncertainty envelope.** The same device-free replay runs at `ScenarioCorners.Apply(cfg, Best)` and
   `(cfg, Worst)`. Their nadirs are `NadirBestCornerHz` and `NadirWorstCornerHz`, and
   `RecordedNadirInEnvelope` is true when the recorded nadir lies within `[min, max]` of the two, widened by `1e-9` Hz.
   The envelope is reported and never gates: the verdict comes from the nominal run only. A recorded nadir inside the
   bracket is consistent with the model within its own error bars. One outside points at a quantity the corners do not
   scale, which is inertia.
9. **Per-sample RMSE** (data-gated, below).
10. **Verdict.**

   ```
   rocofOk       = rocofRelError <= RocofRelTol              (0.15)
   nadirOk       = nadirError    <= NadirAbsToleranceHz      (0.15 Hz)
   rocofMarginal = rocofRelError <= 2 * RocofRelTol          (0.30)
   nadirMarginal = nadirError    <= 2 * NadirAbsToleranceHz  (0.30 Hz)

   verdict = Reproduced   if rocofOk and nadirOk
           = Marginal     else if rocofMarginal and nadirMarginal
           = Failed       otherwise
   if verdict == Reproduced and rmse is present and 0 < rmse and rmse > RmseWarnHz (0.05 Hz):  verdict = Marginal
   if verdict == Reproduced and a trace is attached and backTest.Passed is false:              verdict = Marginal
   ```

   All comparisons are inclusive, so an error exactly on a tolerance passes. The back-test is
   `AssetValidation.Compare` against the attached trace on the simulation clock: nadir within 0.05 Hz, RoCoF within
   0.05 Hz/s on the trace's own basis and nadir time within 2.0 s (`ValidationTolerance.Default`), reported as
   `ReplayResult.BackTest` and in the summary. The RMSE and back-test rules only demote `Reproduced` to `Marginal`; they
   never produce `Failed` and never promote. When the record gives the nadir time and the simulated nadir is more than
   `ReplayTolerance.NadirTimeToleranceS` (2.0 s) from it, the summary also carries a `[nadir time NOT reproduced: ...]`
   note (`ReplayResult.NadirTimingNote`); the note does not change the verdict. The score also records a
   `FrequencyLimits.Assess` of the simulated response and a one-line summary.

The metrics the validator computes are therefore RoCoF (relative error, on the declared basis), nadir (absolute error),
the per-sample RMSE and the back-test when a trace is attached, the nadir-time gap when the record gives the time, the
inferred inertia and the nadir envelope. The headline tolerances score the nadir value, not its time; the nadir time
enters the verdict only through the back-test, and the settling frequency and the LFDD instant are not compared.

**Per-sample RMSE.** When `Outcome.NesoTraceCsvPath` names an existing file (`replay-event --trace <csv>` overrides the
catalogue's path and exits 2 if the file is missing), the validator computes:

```
recorded   = RocofUpsampler.Upsample(NesoFrequencyCsv.Read(path), round(1 / StepSeconds))
anchor     = index of the recorded point nearest WhenUtc
firstAt    = AtSeconds of the first event in ToFrequencyEvents()
simEvent   = sim.Points[0].Time + firstAt
for each sim point p with tOff = p.Time - simEvent >= 0:
    j    = index of the recorded point nearest recorded[anchor].Time + tOff
    diff = p.Hz - recorded[j].Hz
RMSE = sqrt( sum(diff^2) / n )          (null when n = 0, when either series is empty, or when no file is attached)
```

The sim's first event instant is aligned to the trace sample nearest `WhenUtc`, and every simulated point from that
instant to the end of the extended window is scored, as long as its aligned recorded time lies on the trace (within half
a step of the last sample). Samples past the end of the trace are counted (`TraceSamplesPastEnd`), left out of the RMSE
and reported in `TraceNote`; an event instant outside the trace gives no RMSE and a note. A trace that covers the whole
window takes neither branch, so its RMSE is unchanged bit for bit. `scenario`, `fleet`, `evidence` and `h0-study` read
`--trace` as `replay-event` does (`AttachTraceFlag` in `src/GridSim.Cli/Program.cs` attaches the file to the event before
the gate runs), so their gate adds the same RMSE check; each exits 2 when the file does not exist.

### The scenario engine

`ScenarioEngine.Run(spec)` (`src/GridSim.Core/Scenarios/ScenarioEngine.cs`) sweeps inertia x devices x corners.

```csharp
ReplayResult baseline = ReplayValidator.Validate(spec.Event, spec.BaseConfig, spec.Tolerance);
if (baseline.Verdict != ReplayVerdict.Reproduced)
    throw new BaselineNotReproducedException(baseline);
```

**What is varied.** The three axes of `SweepAxes`:

- *Inertia.* `SweepAxes.GbInertiaTrajectoryGvaS = {260, 155, 120, 102, 50}` GVA.s. The axis value replaces the event's
  `PreEventInertiaMwS`: a cell simulates `E = inertiaGvaS * 1000` MW.s. The five points are verified against the TSGB
  corpus (14 July 2026): 260 is the 2017 average outturn (259.85), 155 the 2025 average outturn, 120 the typical current
  outturn, 102 the FRCR 2025 proposed minimum floor, and 50 sits in the recorded 2021-2023 yearly minima band of 44 to 52
  GVA.s. `--inertia` replaces the list.
- *Devices.* `DeviceConfig(Name, RotationalInertiaMwS, FastResponseMw, FastResponseTimeConstantS = 0.5,
  FastResponseDeadbandHz = 0.015)`. `DeviceConfig.Apply` is the single point where a device enters dynamics: its real
  `(1/2) J omega^2` energy goes to `DeviceRotationalInertiaMwS` (added to `E`) and its fast response to `FastResponseMw`
  (a separate power injection). They are never merged into one "equivalent inertia". The default sweep is
  `{DeviceConfig.None, device}`. A device loads from JSON with the keys `name`, `rotationalInertiaMwS`, `fastResponseMw`,
  `fastResponseTimeConstantS`, `fastResponseDeadbandHz` (`data/devices/tsgb-fleet.json` is an example).
- *Corners.* `ScenarioCorners.Apply` scales the three documented-but-unfitted constants:

  | Corner | `DampingPctPerHz` | `ResponsiveFraction` | `GovernorTimeConstantS` |
  |---|---|---|---|
  | `Best` | x1.5 | x1.5, capped at 1.0 | x0.7 |
  | `Nominal` | x1 | x1 | x1 |
  | `Worst` | x0.6 | x0.6 | x1.4 |

  The multipliers are `BestDampingScale`, `BestResponsiveScale`, `BestGovernorTauScale`, `WorstDampingScale`,
  `WorstResponsiveScale` and `WorstGovernorTauScale`. They are engineering brackets, not fitted to any record, and they
  are asymmetric on purpose: the pessimistic corner pulls harder than the optimistic one pushes. The default corners are
  `{Best, Worst}`; `--corners` also accepts `nominal`. Recorded services are not scaled.

**How the counterfactual trajectory is computed.** Before the loop the engine builds one base configuration:

```
base = ExtendToCover( event.ApplyRecordedServices(spec.BaseConfig), ExogenousLossEvents() )
```

For each (inertia, device, corner) cell:

```
cfg  = ScenarioCorners.Apply( device.Apply(base), corner )
resp = FrequencyDynamics.Simulate( inertiaGvaS * 1000, PreEventDemandMw, ExogenousLossEvents(), cfg )
cell = ( RocofHzPerS = resp.InitialRocofHzPerS, NadirHz = resp.NadirHz, SettleHz = resp.SettleHz,
         LfddAtRisk = resp.NadirHz <= 48.8,  Assessment = FrequencyLimits.Assess(resp).Summary )
```

So a counterfactual is the recorded loss sequence, the recorded response services and the recorded pre-event frequency,
at the swept inertia, with the device added on top and without the recorded LFDD or any other recorded action. A cell
count is `|inertia| x |devices| x |corners|` (20 for the defaults). `FrequencyLimits.LfddFirstStageHz = 48.8` Hz is the
LFDD threshold, and the comparison is `<=`.

The risk curve is a second, nominal-corner pass. The device under test is the first device on the axis with positive
inertia or fast response (`DeviceConfig.None` if there is none). At each inertia level it simulates with no device and
with the device and records `RiskPoint(InertiaGvaS, RocofNoDevice, NadirNoDevice, LfddNoDevice, RocofWithDevice,
NadirWithDevice, LfddWithDevice, LfddAvoidedByDevice)`, where `LfddAvoidedByDevice = LfddNoDevice && !LfddWithDevice`.
The risk curve ignores `--corners`.

**Corners and RoCoF.** `FrequencyDynamics` reports the largest-magnitude RoCoF of the whole run. When the loss is a
single step the peak is at the loss instant, before damping or the governor act, so it depends on `E` alone and the
corners leave it unchanged. When the peak falls later in a staged sequence the corner scalings reach it. The 2019 baseline
peaks at `t = 1.0 s`, at the two 1.0 s loss steps, with a peak RoCoF of -0.1710 Hz/s at nominal.

### Fleet additivity

`FleetDynamics.Simulate(baseInertiaMwS, baseDemandMw, events, fleet, config)`
(`src/GridSim.Core/Fleet/FleetDynamics.cs`) is the fleet analogue of `FrequencyDynamics.Simulate`. It uses the same
governor, damping and swing terms with the same arithmetic order, zeroes the config's device terms (the fleet is the
device) and keeps the recorded services and the equilibrium start. The device term is the sum of `N` independently
deadbanded, lagged, onset-staggered unit outputs. `FleetUnit(RotationalInertiaMwS, FastResponseMw,
FastResponseTimeConstantS = 0.5, FastResponseDeadbandHz = 0.015, StaggerSeconds = 0)`.

```
E       = max(1, baseInertiaMwS + sum(unit.RotationalInertiaMwS))            (inertia is N-additive)
per step, per unit u:
    if u is not armed and |df| > u.FastResponseDeadbandHz:  armAt[u] = t + u.StaggerSeconds
    target = (t >= armAt[u]) ? FastResponseTarget(df, u.FastResponseMw, u.FastResponseDeadbandHz) : 0
    pFfr[u] += (target - pFfr[u]) / max(dt, u.FastResponseTimeConstantS) * dt
pFleet = sum(pFfr[u])
rocof  = f0 / (2 E) * (pEvent + pStanding + pGov + pFleet + pServices - d * df)
```

Under `DynamicsConfig.Forming` = `Vsm` or `Droop` the fleet runs the same grid-forming step as
`FrequencyDynamics.Simulate` (`FormingKernel`): the frequency is formed by the config's virtual former (`Ev =
VirtualInertiaMwS`, `Dv = FormingDampingMwPerHz`) over the real rotating mass (base inertia plus the fleet's unit
inertia), with the fleet's summed fast response in the device slot of the forcing. A one-unit, zero-stagger forming
fleet is bit for bit `FrequencyDynamics.Simulate` with that device; with `Forming = None` the non-forming loop above
runs. `scenario` and `fleet` take the `--forming` switches; the other event verbs refuse them (exit 2).

`FastResponseTarget(df, mw, db)` is a droop line `-(mw / 0.5) * df`, clamped to `+/- mw`, and zero inside the deadband:
full output at 0.5 Hz deviation. A unit arms the first time frequency leaves its own deadband (and re-arms its stagger
once the frequency has stayed inside the deadband for that stagger), and its output then lags
that crossing by its stagger. Stagger is a response latency measured from the crossing, not from simulation time zero.

`FleetSpec.Uniform(n, totalInertiaMwS, totalFfrMw, ffrTauS = 0.5, deadbandHz = 0.015, maxStaggerS = 0)` builds
`n = max(1, n)` identical units, each with `total / n` of both budgets, and unit `i` staggered by
`maxStaggerS * i / (n - 1)` (0 for `n = 1`).

The two budgets differ by mechanism. Stored energy sums, so the RoCoF at the loss instant is the same for any split of
the same total. Fast response does not add: identical units that all stay engaged reach full output only when the deviation
reaches 0.5 Hz (on 2019 the nadir deviation is 0.47 Hz and the peak fleet response 1,410.8 MW of the 1,500 MW budget), and a staggered fleet has fewer units
delivering at an early nadir. On the 1,320 MW screening event the deviation stays below 0.5 Hz and the 1,500 MW budget
peaks at 1,050 MW. `FleetResult` therefore carries `PeakFleetFfrMw` (the largest-magnitude fleet output over the run) and
`FfrAtNadirMw` (the fleet output at the step of the nadir, the decision-relevant figure). Whether a gap opens depends on
when the nadir falls relative to the stagger window: if the nadir comes after the last unit has armed and settled, the two
figures coincide (worked example). With one unit and zero stagger the solve reproduces `FrequencyDynamics.Simulate`
with that device; the pinned agreement is 12 decimal places.

### The H0 study

`H0StudyEngine.Run` (`src/GridSim.Core/Scenarios/H0StudyEngine.cs`) is the gated, fleet-routed twin of the scenario sweep
on a reconstructed grid. It runs the same gate (the validator reads only the scalar swing, so `EnableAssetDynamics = true` in the base config affects the sweep cells and not the gate), then sweeps `GfmFractions` x
`SyncInertiaScales` x `Corners` (defaults `{0, 0.25, 0.5, 0.75, 1}` x `{1, 0.5, 0.25, 0}` x `{Best, Worst}`, 40 cells) over
the case model's authored asset fleet. Per fraction, `GridInertiaTransforms.SetGfmFraction` tags the converter fleet
grid-forming or grid-following and `FormingAggregate.FromFleet` gives the virtual former's `Ev` and `Dv`; the grid-forming
units are excluded from the active fleet so their contribution is counted once. Per scale,
`ScaleSynchronousInertia` scales real inertia. For each cell:

```
eReal       = scaled system inertia (MW.s), real (1/2) J omega^2 only
bareRocof   = f0 * dP / (2 * eReal)       (dP = signed sum of the losses in the first 5 s, of every loss
                                           if none falls there; NaN when eReal = 0)
formable    = eReal > 0 or Ev > 0
eForm       = eReal + Ev                                            (no floor)
formedBy    = "GridForming" if Ev > 0, else "Synchronous" if eReal > 0, else "None"
```

`f0` is `BaseConfig.NominalHz`; it is not checked against the model's `FrequencyHz`. A cell with `formable = false` is
`FormingDependent`: its RoCoF, nadir and settle are `NaN`, and it is not simulated. `LfddAtRisk` and `CascadeLossOfMains`
are asserted true and `TrippedCount` and `TotalTrippedMw` are the whole grid-following fleet's count and infeed, by
assumption rather than from a simulated cascade, and the run still exits 0 because such a cell is data. Any other cell is simulated with
`Forming = Vsm`, `VirtualInertiaMwS = Ev` and `FormingDampingMwPerHz = Dv` when `Ev > 0`, through
`FrequencyDynamics.Simulate` (the fleet-free reference) and `AssetFrequencyDynamics.Simulate` (the fleet-coupled result),
with `LfddAtRisk = nadir <= 48.8` and `CascadeLossOfMains = TrippedCount > 0 && LfddAtRisk`. Virtual inertia `Ev` is a
control state and never enters the real inertia budget; the two stay in separate fields.

### The evidence dossier

`EvidenceReport.Build(spec, config)` (`src/GridSim.Core/Evidence/EvidenceReport.cs`) assembles the umbrella dossier. It is
serialised as `{ "schema": "gridsim-evidence/1", "report": { ... } }` with camelCase keys, string enums and `null`
members omitted. `EvidenceSpec.Default(ev, device)` supplies the axis `GbInertiaTrajectoryGvaS`,
`CounterfactualInertiaGvaS = PreEventInertiaMwS / 1000`, `FleetUnits = 100`, `FleetStaggerSeconds = 10` and
`new AvoidedCostInputs()`.

```
cfg      = ExtendToCover( event.ApplyRecordedServices(config ?? default), ExogenousLossEvents() )
baseline = ReplayValidator.Validate(event, cfg)
unlocked = (baseline.Verdict == Reproduced)
```

Columns (`EvidenceColumn(Label, Provenance, RocofHzPerS, NadirHz, SettleHz, LfddAtRisk, DeviceRotationalInertiaGvaS,
DeviceFfrGw)`), with the device's rotational inertia and fast response as separate labelled fields:

| Column | Source | Provenance | Present |
|---|---|---|---|
| `recorded` | the outcome; `LfddAtRisk = NadirHz <= 48.8` | the outcome's own tag | always |
| `simulated (no device)` | `baseline.SimulatedRocofHzPerS` (the RoCoF on the record's own basis, the figure the gate scored; for a 1 s windowed record such as `gb-2023-12-22` it is the windowed figure, not the instantaneous peak), and `baseline.Simulated` nadir and settle | the weaker of `DerivedFromRecorded` and the event's effective tag (so a placeholder or synthetic event's column takes the event's weaker tag) | always |
| `counterfactual (device @ N GVA.s)` | `Simulate(CounterfactualInertiaGvaS * 1000, demand, ExogenousLossEvents(), device.Apply(cfg))` | `Counterfactual` | only when unlocked |

When unlocked the report also holds:

- **Risk curve.** `ScenarioEngine.Run` on `{None, device}` x `{Best, Worst}` over `InertiaAxisGvaS`; the report keeps
  `RiskCurve`. The engine re-runs the gate.
- **Fleet comparison.** Two `FleetDynamics.Simulate` runs at the headline inertia with the base `cfg` (not the device
  config, which would count the device twice): one `FleetSpec.Uniform(1, deviceInertia, deviceFfr, maxStaggerS: 0)` and one
  `FleetSpec.Uniform(FleetUnits, deviceInertia, deviceFfr, maxStaggerS: FleetStaggerSeconds)`. The report holds
  `FleetComparison(InertiaGvaS, Units, StaggerSeconds, AggregateNadirHz, FleetNadirHz, AggregateFfrAtNadirMw,
  FleetFfrAtNadirMw)`. The 10 s default stagger reflects that GB primary response is fully delivered by 10 s and a diverse
  fleet staggers across that window; a sub-second stagger completes before the nadir and hides the effect.
- **Avoided cost**, below.

The summary names the verdict, whether the device keeps the nadir above LFDD, and either the avoided cost with its
provenance tag or `no avoided cost claimed`. A locked dossier's summary says the counterfactual is withheld.

### Avoided cost

`AvoidedCost.FromLfddAvoided(lfddMwShedAvoided, inputs)` (`src/GridSim.Core/Evidence/AvoidedCost.cs`):

```
AvoidedCostGbp = max(0, LfddMwShed) x VollPerMwh x LfddDurationHours
```

`EvidenceReport.Build` calls it only when both conditions hold:

```
counterfactual nadir > 48.8 Hz        (the device keeps the nadir above the LFDD first stage; `!cfLfdd`)
Outcome.LfddMwShed > 0                (the record shed load)
```

Otherwise the shed would still have happened, or there was nothing to avoid, and no cost is claimed. The MW term is the
recorded shed (`RecordedOutcome.LfddMwShed`), not a figure the engine produces. `AvoidedCostResult` carries all four
terms: `LfddMwShedAvoided`, `VollPerMwh`, `DurationHours`, `AvoidedCostGbp`, the `Provenance` tag and a `Basis` string.

| Term | Value | Unit | Provenance | Source symbol |
|---|---|---|---|---|
| MW shed | the event's recorded value (892 for 2019) | MW | the outcome's tag (`Recorded`, Ofgem 2.4.12) | `RecordedOutcome.LfddMwShed` |
| Value of lost load | `6000` | GBP/MWh | `CuratedPlaceholder` (`AvoidedCost.VollBaseProvenance`), with or without `--voll-source` | `AvoidedCostInputs.VollPerMwh`, `CliDefaults.DefaultVollGbpPerMwh` |
| Outage duration | `0.5` | h | `CuratedPlaceholder` | `AvoidedCostInputs.LfddDurationHours`, `CliDefaults.DefaultLfddDurationH` |
| Cost tag | the weaker of the VoLL and duration tags (`AvoidedCost.Weakest`): `CuratedPlaceholder` | - | policy input | `AvoidedCostResult.Provenance` |

VoLL and the duration are policy inputs, not physics. Each input carries its own tag (`VollProvenance`,
`DurationProvenance`), the cost takes the weaker of the two, and the evidence output prints both on a
`VoLL [..]   duration [..]` line beside the cost. `AvoidedCost.FromLfddAvoided` refuses a VoLL tag raised above
`VollBaseProvenance` (`ProvenanceTrust.RequireNotRaised`). `--voll-source <id>` cites a reference in
`docs/reference/voll` that states a GB VoLL (`VollSources`): `S324` (Ofgem BSC P305 decision, 2015-04-02: GBP 3,000 and
6,000 per MWh), `S322` (London Economics, 2013-07: GBP 16,940) or `S323` (DECC EMR consultation annex C, 2013-07:
GBP 16,940). `--voll` must equal a figure the cited reference states, otherwise the run exits 2 naming the figures it
does state. A valid citation is printed (`VoLL source: <id>, <title>: <where>`) and recorded as `VollSourceId`; it does
not raise the tag, because a policy figure is not a record of the event.

### The forward-inference guard here

`ForwardInferenceGuard.RejectFuture(stamp, utcNow, what)` (`src/GridSim.Core/Diagnostics/ForwardInferenceGuard.cs`) throws
`ForwardInferenceException` when `stamp > utcNow - 169 h`, with `HorizonHours = 169` (7 days plus 1 hour). A stamp exactly
169 h old passes. `Unspecified` stamps are read as UTC. The engine applies it at two boundaries:

| Boundary | Check |
|---|---|
| Catalogue | `WhenUtc` of every event, in `Builtin`, `Load` and `Get` |
| Trace | the timestamp of every NESO row, as `NesoFrequencyCsv.Read` yields it |

Nothing here forecasts, and nothing reads a recent or future record, so the engine can only lag reality. The five verbs
catch `ForwardInferenceException` around the catalogue load, print `REJECTED (forward inference): <message>` to stderr and
exit 2. There is no switch to disable it. The rationale for the horizon is in ch. 8.

### Determinism

The path from event to dossier is single-threaded double-precision arithmetic: forward Euler, sums over a time-ordered
event array, `Math.Clamp`, `Math.Min`, `Math.Sqrt` (correctly rounded) and sorts. It calls no random source, no
`Parallel`, no `Math.FusedMultiplyAdd` and no transcendental function on the non-forming path, and the only clock read is
`DateTime.UtcNow` in the guard and in the export metadata. `OrderBy` is stable, so events at the same instant keep their
order. A given event, catalogue and flag set therefore gives the same numbers on every run. The printed values shown in
this chapter are reproducible with the loop in step 4 of the validator.

## Parameters and defaults

Gate and window:

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| RoCoF tolerance | `0.15` | fraction of `|rec|` | Reproduced bound; threshold set in code, no external derivation | `ReplayTolerance.RocofRelTol` |
| Nadir tolerance | `0.15` | Hz | Reproduced bound | `ReplayTolerance.NadirAbsToleranceHz` |
| RMSE warning | `0.05` | Hz | Demotes `Reproduced` to `Marginal` | `ReplayTolerance.RmseWarnHz` |
| Marginal band | `2` | x tolerance | Upper edge of `Marginal` (0.30 and 0.30 Hz) | literal in `ReplayValidator.Validate` |
| Post-event tail | `30` | s | Window runs this far past the last event | `ReplayValidator.PostEventTailSeconds` |
| Inertia-inference window | `5` | s | Losses at or before this time set the inferred inertia | `rocofWindowSeconds` in `ReplayValidator.Validate` |
| Zero-RoCoF guard | `1e-9` | Hz/s | Below it the relative error becomes `|simRocof|` and the inferred inertia infinite | literal in `Validate`, `InertiaEstimator` |
| Envelope slack | `1e-9` | Hz | Widening of the nadir bracket | `ReplayResult.RecordedNadirInEnvelope` |
| Integration step | `0.02` | s | Forward Euler step, the 20 ms upsampler grid | `DynamicsConfig.StepSeconds` |
| Base duration | `30` | s | Before extension | `DynamicsConfig.DurationSeconds` |
| LFDD first stage | `48.8` | Hz | `LfddAtRisk` is `nadir <= 48.8` | `FrequencyLimits.LfddFirstStageHz` |
| Forward horizon | `169` | h | Newest datum the engine may process | `ForwardInferenceGuard.HorizonHours` |

Corner and base dynamics (the constants in the first four rows are literature-typical and unfitted; droop is the Grid Code
figure; see ch. 5):

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| Load damping | `1.0` | % of demand per Hz | Unfitted | `DynamicsConfig.DampingPctPerHz` |
| Responsive fraction | `0.20` | of demand | Unfitted | `DynamicsConfig.ResponsiveFraction` |
| Governor time constant | `8.0` | s | Unfitted | `DynamicsConfig.GovernorTimeConstantS` |
| Governor droop | `0.05` | pu | Documented | `DynamicsConfig.DroopPu` |
| Best corner | damping x1.5, responsive x1.5 (cap 1.0), tau x0.7 | - | Engineering bracket | `ScenarioCorners.Best*Scale` |
| Worst corner | damping x0.6, responsive x0.6, tau x1.4 | - | Engineering bracket | `ScenarioCorners.Worst*Scale` |
| Inertia axis | `260, 155, 120, 102, 50` | GVA.s | GB trajectory, verified 2026-07-14 | `SweepAxes.GbInertiaTrajectoryGvaS` |
| Device (flags) | `30` GVA.s inertia, `1.0` GW fast response | - | Placeholder TSGB device | `CliDefaults.DefaultDeviceInertiaGvaS`, `DefaultDeviceFfrGw` |
| Device fast-response lag, deadband | `0.5` s, `0.015` Hz | - | Device defaults | `DeviceConfig` |

Fleet, dossier and cost:

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| Fleet inertia budget | `40` | GVA.s | `fleet` verb, split over the units | `CliDefaults.DefaultFleetInertiaGvaS` |
| Fleet FFR budget | `1.5` | GW | `fleet` verb | `CliDefaults.DefaultFleetFfrGw` |
| Fleet units | `1,100` (`fleet`), `100` (`evidence`) | units | Counts compared | `fleet --units`, `EvidenceSpec.FleetUnits` |
| Stagger | `10` | s | Onset spread over `[0, 10]` | `CliDefaults.DefaultFleetStaggerS`, `EvidenceSpec.FleetStaggerSeconds` |
| Fleet FFR lag, deadband | `0.5` s, `0.015` Hz | - | Per-unit | `CliDefaults.DefaultFfrTimeConstantS`, `DefaultFfrDeadbandHz`, `FleetSpec.Uniform` |
| Full-delivery deviation | `0.5` | Hz | Device and fleet FFR reach full output here | `SwingKernel.FastResponseTarget` |
| VoLL | `6000` | GBP/MWh | Placeholder | `CliDefaults.DefaultVollGbpPerMwh` |
| Duration | `0.5` | h | Placeholder | `CliDefaults.DefaultLfddDurationH` |
| Counterfactual inertia | event `PreEventInertiaMwS / 1000` | GVA.s | Headline column | `EvidenceSpec.CounterfactualInertiaGvaS` |

Schema ids: `gridsim-event-catalog/1`, `gridsim-evidence/1`. Wire formats are in
[ch. 10, File and wire formats](<0010 File and wire formats.md>).

## Invariants

| Invariant | Pinned by |
|---|---|
| An event whose record equals the model's own output is `Reproduced`, with RoCoF and nadir errors below `1e-6` and the inferred inertia equal to the stated inertia to the nearest MW.s (precision 0) | `ReplayValidatorTests.A_self_consistent_event_is_reproduced` |
| A grossly wrong record is `Failed` | `ReplayValidatorTests.A_mismatched_event_fails` |
| RoCoF 16.7 % and nadir 0.2 Hz off is `Marginal` | `ReplayValidatorTests.A_slightly_off_event_is_marginal_not_reproduced_or_failed` |
| The baseline is device-free whatever the config carries (equal to 12 decimals) | `ReplayValidatorTests.The_baseline_is_always_device_free` |
| The Best-corner nadir is at or above the nominal nadir and the Worst-corner nadir at or below it | `ResponseServiceTests.Replay_reports_the_corner_envelope_and_verdict_stays_nominal` |
| A windowed record (1.0 s) scores a smaller simulated RoCoF magnitude than the same record as an instantaneous figure | `ResponseServiceTests.Replay_scores_a_windowed_record_against_the_sampled_grid_not_the_sliding_max` |
| `ApplyRecordedServices` maps the service count, the first service's `Mw` (873) and the second's `FullDeliveryDeviationHz` (0.2); a complete list gives `ResponsiveFraction` 0, an incomplete one keeps the default, a null list gives an empty one | `ResponseServiceTests.ApplyRecordedServices_maps_and_completeness_zeroes_the_governor` |
| The 2023 event carries the three EAC services and 1,226 MW | `ResponseServiceTests.Catalogue_dec23_carries_the_measured_service_set` |
| An empty service list changes nothing; the fleet integrates services as the scalar model does | `ResponseServiceTests.Empty_service_list_is_the_identity`, `Fleet_integrates_services_identically_to_the_scalar_model` |
| The window is `max(Duration, last event + 30 s)`, the maximum over an unsorted list, never shorter; the tail is 30 s | `Ws13CoreConstantsAntiDriftTests.ExtendToCover_reproduces_the_old_window_rule_exactly`, `Scenario_replay_and_scheduling_constants_equal_the_literals_they_replaced` |
| The six corner multipliers hold their values | `Ws13CoreConstantsAntiDriftTests.Scenario_replay_and_scheduling_constants_equal_the_literals_they_replaced` |
| The scenario gate throws unless `Reproduced` | `ScenarioEngineTests.The_gate_throws_when_the_baseline_is_not_reproduced`, `H0StudyEngineTests.The_gate_throws_when_the_baseline_is_not_reproduced` |
| Cells and risk points replay the full loss window | `ScenarioEngineTests.Risk_curve_no_device_uses_the_full_loss_window` |
| The nadir worsens monotonically as inertia falls; the device lifts the nadir and cuts RoCoF at every level | `ScenarioEngineTests.The_risk_curve_worsens_monotonically_as_inertia_falls`, `The_device_lifts_the_nadir_and_cuts_rocof_at_every_level` |
| The matrix has `|inertia| x |devices| x |corners|` cells | `ScenarioEngineTests.The_matrix_has_a_cell_per_inertia_times_device_times_corner` |
| Corners change the nadir and, for a single-step loss, not the RoCoF | `ScenarioEngineTests.Corners_bracket_the_nadir_but_not_the_initial_rocof` |
| Inertia is N-additive and equals the closed form `f0 / (2 E) * deltaP` | `FleetDynamicsTests.Intrinsic_inertia_is_N_additive` |
| A staggered fleet delivers less FFR at the nadir and has a deeper nadir, also when the loss lands after the stagger window | `FleetDynamicsTests.Fast_response_is_non_additive_when_staggered`, `Stagger_is_relative_to_the_event_not_absolute_time` |
| One unit at zero stagger equals `FrequencyDynamics.Simulate` (12 decimals, trajectory spot-checked) | `FleetDynamicsTests.One_unit_zero_stagger_equals_Simulate` |
| A sub-deadband event never arms a unit | `FleetDynamicsTests.A_sub_deadband_event_never_arms_the_fleet_ffr` |
| The integrator sums the inertia budget | `FleetDynamicsTests.Integrator_sums_the_inertia_budget` |
| Dossier columns are provenance-labelled and keep device inertia and FFR in separate fields | `EvidenceReportTests.Columns_are_provenance_labelled_and_never_merge_inertia_with_ffr` |
| Avoided cost is exactly shed x VoLL x duration and carries the placeholder tag | `EvidenceReportTests.Avoided_cost_arithmetic_is_exact_and_grounded_in_the_recorded_shed` |
| `Marginal` and `Failed` both withhold the counterfactual, risk curve, fleet and cost | `EvidenceReportTests.The_counterfactual_is_withheld_when_the_baseline_fails`, `A_marginal_baseline_withholds_the_counterfactual` |
| The default fleet stagger is 10 s over 100 units | `EvidenceReportTests.The_default_spec_uses_the_ten_second_onset_spread` |
| The counterfactual replays the full loss window and a zero device on 2019 claims no avoided cost | `EvidenceReportTests.Counterfactual_replays_full_window_and_does_not_falsely_claim_avoided_cost` |
| The report serialises with schema `gridsim-evidence/1` | `EvidenceReportTests.Report_serialises_with_the_schema_tag` |
| The catalogue round-trips through JSON; a future event is rejected on load; actions merge with losses in time order; `Get` is case-insensitive and an unknown id throws | `EventCatalogTests.Builtins_round_trip_through_json`, `A_future_dated_event_is_rejected_on_load`, `ToFrequencyEvents_merges_losses_and_actions_in_time_order`, `Get_is_case_insensitive_and_unknown_ids_throw` |
| The horizon boundary is exactly `HorizonHours`; the exact horizon instant passes and one tick inside throws; `Unspecified` is UTC; a `Local` stamp compares by true instant | `ForwardInferenceGuardTests.The_horizon_boundary_is_exactly_HorizonHours`, `The_exact_horizon_instant_passes_and_one_tick_inside_throws`, `Unspecified_kind_is_treated_as_utc`, `A_local_kind_stamp_is_compared_by_true_instant_not_naive_ticks` |
| With no converter fleet the H0 cell equals the scalar swing to 9 decimals (RoCoF and nadir); virtual inertia never joins real inertia; at H=0 on the Best corner all-GFL is forming-dependent with a cascade and all-GFM holds (finite RoCoF below 2 Hz/s, nadir above 48.8 Hz, no trips) | `H0StudyEngineTests.With_no_converter_fleet_the_cell_equals_the_scalar_swing_to_machine_epsilon`, `Virtual_former_inertia_is_never_added_to_real_system_inertia`, `At_H0_all_GFL_is_forming_dependent_and_cascades_while_all_GFM_holds` |

No automated test pins the verdict being independent of the envelope (the envelope test compares `Validate` with a second
call of itself), the median-over-phases rule of the windowed RoCoF (the windowed test checks only that the figure is
smaller), the start frequency in `ApplyRecordedServices`, the Worst corner of the H0 all-GFM cell (at scale 0.00 and
GFM fraction 1.00 the 2019 H0 table shows `48.80 CASC`), the RMSE alignment or its demotion rule, the NESO CSV time-zone handling or its per-row guard,
the verb exit codes, or the two-condition avoided-cost rule at its boundary.

## Worked example

The event is `gb-2019-08-09`. The captures below come from the Release build; timings and stamps differ by machine and
run. The unrounded values below are taken from the runs' own files: the `headline` block of
`baseline-frequency.json`, `risk-curve.csv`, the `columns` of `evidence.json` and `fleet.csv`.

### The gate

```
$ gridsim replay-event gb-2019-08-09

=== Replay: GB 9 Aug 2019 disturbance (Hornsea + Little Barford) ===
  when (UTC):        2019-08-09T15:52:33.0000000Z   provenance: DerivedFromRecorded (event tagged Recorded; the weakest of its parts)
  pre-event inertia: 215.0 GVA.s   demand: 21.1 GW   total loss: 2768 MW
  inferred inertia from recorded RoCoF: 223.7 GVA.s (cross-check vs 215.0)

  RoCoF:  sim  -0.171   recorded  -0.170 Hz/s   (1 % error)
  Nadir:  sim   48.84   recorded   48.80 Hz   (0.04 Hz error)
  Nadir envelope (declared-uncertainty corners): [48.20, 49.17] Hz - recorded INSIDE (consistent within the model's own error bars)
  RMSE vs trace: n/a (no 1-sec NESO trace attached - data-gated)
  assessment: BELOW STATUTORY (49.5 Hz)

  VERDICT: REPRODUCED  - baseline reproduces the record; counterfactuals unlocked.

  -> results\replay-gb-2019-08-09
[exit 0]
```

**Total loss.** `150 + 737 + 244 + 390 + 100 + 210 + 200 + 550 + 187 = 2768` MW, as printed.

**RoCoF.** The loss steps accumulate to `-150` MW at 0.1 s, `-887` at 0.5 s, `-1131` at 1.0 s and `-1521` at 3.0 s, and the
window is 120 s. The gain is `f0 / (2E) = 50 / (2 x 215,000) = 1.162791e-4` Hz/s per MW. At the first step the RoCoF is
`-150 x 1.162791e-4 = -0.017442` Hz/s, and with no response at 3.0 s it would be `-1521 x 1.162791e-4 = -0.176860`. The
simulated peak in the `headline` block is `-0.17097330951595094` Hz/s. Against the record:

```
rocofRelError = |0.170973 - 0.170| / 0.170 = 0.0057   (0.57 %, printed "1 %")  <= 0.15
```

**Nadir.** The simulated minimum is 48.84096 Hz at `t = 76 s` (`nadirHz` 48.84096136852519, `nadirSeconds` 76).
`nadirError = |48.84096 - 48.80| = 0.0410` Hz, within 0.15 Hz.

**Inertia cross-check.** The losses at or before 5 s are `150 + 737 + 244 + 390 = 1521` MW, so
`E = 50 x 1521 / (2 x 0.17) = 223,676` MW.s, which is the printed 223.7 GVA.s against the stated 215.0.

**Envelope.** The `Best` replay (damping 1.5 %/Hz, responsive fraction 0.30, governor lag 5.6 s) and the `Worst` replay
(0.6 %/Hz, 0.12, 11.2 s) bracket the nadir at the printed `[48.20, 49.17]`. The record, 48.80 Hz, lies inside.

**Verdict.** RoCoF and nadir are both inside tolerance, no trace is attached, so the verdict is `Reproduced`.

### The gate with a trace

Attaching the bundled 1-second trace turns on the RMSE check.

```
$ gridsim replay-event gb-2019-08-09 --trace data/neso/gb-2019-08-09-frequency.csv

=== Replay: GB 9 Aug 2019 disturbance (Hornsea + Little Barford) ===
  when (UTC):        2019-08-09T15:52:33.0000000Z   provenance: DerivedFromRecorded (event tagged Recorded; the weakest of its parts)
  pre-event inertia: 215.0 GVA.s   demand: 21.1 GW   total loss: 2768 MW
  inferred inertia from recorded RoCoF: 223.7 GVA.s (cross-check vs 215.0)

  RoCoF:  sim  -0.171   recorded  -0.170 Hz/s   (1 % error)
  Nadir:  sim   48.84   recorded   48.80 Hz   (0.04 Hz error)
  Nadir envelope (declared-uncertainty corners): [48.20, 49.17] Hz - recorded INSIDE (consistent within the model's own error bars)
  RMSE vs trace: 0.099 Hz over 5996 sample(s)
  back-test vs trace (gate 0.05 Hz / 0.05 Hz/s / 2.0 s): FAIL  nadir 0.054 Hz, RoCoF sim -0.154 vs rec -0.151 Hz/s, nadir time sim 76.0 s vs rec 76.1 s (0.1 s)
  assessment: BELOW STATUTORY (49.5 Hz)

  VERDICT: MARGINAL  - NOT reproduced; do not run counterfactuals until the baseline matches (check [VERIFY] figures / tolerances).

  -> results\replay-gb-2019-08-09
[exit 3]
```

The trace has 601 samples from 15:50:00, so the event sample 15:52:33 is index 153 (49.995 Hz, then 49.921 Hz). The first
loss is at 0.1 s, so the simulated points run from `t = 0.1 s` to 120 s: 5,996 points at 20 ms. Each is compared with the
upsampled trace point nearest `15:52:33 + (t - 0.1)`, and the root mean square of the differences prints as 0.099 Hz.
That is above `RmseWarnHz = 0.05`, and the back-test fails on the nadir (0.054 Hz against its 0.05 Hz gate), so
`Reproduced` is demoted to `Marginal` and the verb exits 3. The headline metrics match; the trajectory does not. The
back-test puts the simulated nadir at 76.0 s and the recorded minimum, 48.787 Hz on the trace, at 76.1 s (15:53:49).

### Scenario, and the counterfactual column

```
$ gridsim scenario --event gb-2019-08-09

=== Counterfactual scenario: GB 9 Aug 2019 disturbance (Hornsea + Little Barford) ===
  baseline gate: Reproduced   device: tsgb (30.0 GVA.s inertia + 1.00 GW FFR)
  LFDD first-stage threshold: 48.8 Hz  (nominal corner)

   inertia | RoCoF no-dev    nadir  LFDD |  RoCoF +dev    nadir  LFDD | avoided
  ------------------------------------------------------------------------------------
     260GV |     -0.142Hz/s  47.69Hz   YES |    -0.125Hz/s  49.09Hz    no | *** YES ***
     155GV |     -0.234Hz/s  47.62Hz   YES |    -0.190Hz/s  49.05Hz    no | *** YES ***
     120GV |     -0.298Hz/s  47.61Hz   YES |    -0.231Hz/s  49.04Hz    no | *** YES ***
     102GV |     -0.347Hz/s  47.60Hz   YES |    -0.259Hz/s  49.04Hz    no | *** YES ***
      50GV |     -0.660Hz/s  47.60Hz   YES |    -0.398Hz/s  49.03Hz    no | *** YES ***

  device avoids LFDD at 5/5 inertia levels.

  -> results\scenario-gb-2019-08-09
[exit 0]
```

The no-device column drops the recorded LFDD, so it shows what the exogenous losses alone do at each inertia: 47.69 Hz
at 260 GVA.s (47.690 in `risk-curve.csv`) and 47.60 Hz at 50 GVA.s. The device adds `30,000` MW.s to `E` and a 1,000 MW fast
response.

### Evidence

```
$ gridsim evidence --event gb-2019-08-09 --out results/evidence-gb-2019-08-09

=== Evidence dossier: GB 9 Aug 2019 disturbance (Hornsea + Little Barford) ===
  when: 2019-08-09T15:52:33.0000000Z   event provenance: DerivedFromRecorded   baseline gate: Reproduced

  column                                          RoCoF    nadir  LFDD   dev inertia  dev FFR
  --------------------------------------------------------------------------------------------
  recorded                                    -0.170Hz/s  48.80Hz   YES        0.0GVAs  0.00GW   [Recorded]
  simulated (no device)                       -0.171Hz/s  48.84Hz    no        0.0GVAs  0.00GW   [DerivedFromRecorded]
  counterfactual (device @ 215 GVA.s)         -0.147Hz/s  49.07Hz    no       30.0GVAs  1.00GW   [Counterfactual]

  single-vs-fleet @ 215 GVA.s: aggregate nadir 49.07 Hz vs 100-unit fleet 49.07 Hz (FFR@nadir 1000 vs 1000 MW).

  AVOIDED COST: 892 MW shed x 0.50 h x GBP 6,000/MWh = GBP 2,676,000  [CuratedPlaceholder]
                (recorded LFDD energy avoided (MW shed x duration h) x VoLL (GBP/MWh) - grounded in the record, not a marketing figure)
                VoLL [CuratedPlaceholder]   duration [CuratedPlaceholder]

  Baseline Reproduced; counterfactual device DOES keep the nadir above LFDD; avoided cost GBP 2,676,000 (CuratedPlaceholder)

  -> results\evidence-gb-2019-08-09
[exit 0]
```

**Counterfactual column.** `E = 215,000 + 30,000 = 245,000` MW.s, so the gain is `50 / 490,000 = 1.020408e-4` Hz/s per MW.
The column's RoCoF is `-0.14661442346289758` Hz/s (printed -0.147) and its nadir 49.06952 Hz (`nadirHz`
49.06951716271372), which is above 48.8 Hz by 0.26952 Hz.

**Avoided cost.** Both conditions hold (`49.06952 > 48.8` and `892 > 0`):

```
AvoidedCostGbp = max(0, 892) x 6000 x 0.5 = 2,676,000
```

The tag is `CuratedPlaceholder`: the VoLL and the duration are both policy placeholders. With `--voll 16940 --duration 1
--voll-source S322` the same event gives `892 x 16940 x 1 = 15,110,480`, still tagged `CuratedPlaceholder`, with the
citation printed (`docs/_captures/events/evidence-gb-2019-08-09-voll.txt`).

### Fleet additivity

```
$ gridsim fleet --event gb-2019-08-09

=== Fleet emergent behaviour: GB 9 Aug 2019 disturbance (Hornsea + Little Barford) ===
  system inertia: 215 GVA.s   device budget: 40 GVA.s inertia + 1.50 GW FFR   stagger: 10.00 s
  (same TOTAL budget split N ways - intrinsic inertia N-additive, FFR non-additive)

   units |       RoCoF |    nadir |  FFR @ nadir |   peak FFR | LFDD
  --------------------------------------------------------------------------
       1 |   -0.139Hz/s |  49.53Hz |      1411MW |    1411MW | no
     100 |   -0.145Hz/s |  49.53Hz |      1411MW |    1411MW | no

  RoCoF differs across unit counts (-0.139 vs -0.145 Hz/s): the inertia total is the same in both (it is N-additive), so the difference is the timing of the fast response, not the inertia.
  At the nadir the 100-unit fleet has the same 1411 MW online as 1 aggregate unit (100 %): on this event the staggered fleet is fully delivered by the nadir, so the split costs nothing here.
  Consequence: the same nadir, 49.53 Hz for the 100-unit fleet and 49.53 Hz for 1 unit(s).

  -> results\fleet-gb-2019-08-09
[exit 0]
```

The two rows share one stored-energy term, `E = 215,000 + 40,000 = 255,000` MW.s. They differ in peak RoCoF (-0.1394 and
-0.1448 Hz/s in `fleet.csv`) because the run reports the largest RoCoF over 120 s and the staggered fleet is still
arming across `[0, 10]` s when the early loss steps land. At the nadir (49.530 Hz in both rows) the fleet delivers the
same 1,410.5 MW as the single unit, and `peak FFR` is 1,410.8 MW in both, so the nadirs are equal: for this event the
non-additivity does not reach the nadir.

### A locked gate

`gb-2023-12-22` is `Marginal` and so unlocks nothing (`docs/_captures/events/scenario-gb-2023-12-22.txt`):

```
$ gridsim scenario --event gb-2023-12-22
GATE FAILED: Refusing to run counterfactuals: the no-device baseline is 'Marginal', not 'Reproduced'. Reproduce reality first. Marginal: RoCoF sim -0.123 vs rec -0.102 Hz/s (20 % err); nadir sim 48.98 vs rec 49.27 Hz (0.30 Hz err); RMSE n/a (no trace)
Fix the event's [VERIFY] figures (or tolerances) until 'replay-event' reports Reproduced, then re-run.
[exit 3]
```

The `evidence` verb on the same event writes a two-column dossier and the line `Baseline Marginal - counterfactual
withheld.` (`docs/_captures/events/evidence-gb-2023-12-22.txt`). The windowed-RoCoF basis is visible here: the simulated
figure scored is the median over the 1-second grid, -0.123 Hz/s, against a 20 ms peak of -0.155 Hz/s.

## Source map

| File | Type | Responsibility |
|---|---|---|
| `src/GridSim.Core/Events/GridEvent.cs` | `GridEvent`, `PowerStep`, `RecordedAction`, `ResponseService`, `RecordedOutcome` | The event model, the two projections and `ApplyRecordedServices` |
| `src/GridSim.Core/Events/EventCatalog.cs` | `EventCatalog` | Catalogue JSON (`gridsim-event-catalog/1`), the three built-ins, the horizon check on load |
| `src/GridSim.Core/Frequency/NesoFrequencyCsv.cs` | `NesoFrequencyCsv` | Streaming CSV reader with UTC normalisation and the per-row horizon guard |
| `src/GridSim.Core/Frequency/RocofUpsampler.cs` | `RocofUpsampler` | Linear refinement to 20 ms, per-interval RoCoF, trace summary |
| `src/GridSim.Core/Frequency/FrequencyTypes.cs` | `FrequencySample`, `FrequencyPoint`, `FrequencyStats` | Trace row, fine-grid point, summary |
| `src/GridSim.Core/Replay/ReplayValidator.cs` | `ReplayValidator`, `ReplayTolerance`, `ReplayResult`, `ReplayVerdict` | The gate: window extension, metrics, windowed RoCoF, RMSE, envelope, verdict |
| `src/GridSim.Core/Scenarios/ScenarioSpec.cs` | `ScenarioSpec`, `SweepAxes`, `DeviceConfig`, `ScenarioCorners`, `Corner`, `BaselineNotReproducedException` | Sweep definition, device entry point, corner scalings, the gate exception |
| `src/GridSim.Core/Scenarios/ScenarioEngine.cs` | `ScenarioEngine`, `ScenarioMatrix`, `ScenarioCell`, `InertiaRiskCurve`, `RiskPoint` | The gated sweep and the risk curve |
| `src/GridSim.Core/Scenarios/H0StudySpec.cs`, `H0StudyEngine.cs` | `H0StudySpec`, `H0StudyEngine`, `H0StudyCell`, `H0StudyResult` | The gated H=0 sweep over a reconstructed grid |
| `src/GridSim.Core/Fleet/FleetSpec.cs`, `FleetDynamics.cs` | `FleetUnit`, `FleetSpec`, `FleetDynamics`, `FleetResult` | N-unit device term, staggered arming, peak and at-nadir FFR |
| `src/GridSim.Core/Evidence/EvidenceReport.cs` | `EvidenceReport`, `EvidenceSpec`, `EvidenceColumn`, `FleetComparison` | The dossier, its gate and its JSON |
| `src/GridSim.Core/Evidence/AvoidedCost.cs` | `AvoidedCost`, `AvoidedCostInputs`, `AvoidedCostResult` | The avoided-cost formula and its terms |
| `src/GridSim.Core/Diagnostics/ForwardInferenceGuard.cs` | `ForwardInferenceGuard` | The 169 h horizon |
| `src/GridSim.Core/Dynamics/FrequencyDynamics.cs`, `SwingKernel.cs`, `InertiaEstimator.cs`, `FrequencyLimits.cs` | (ch. 5) | Swing integration, response curves, inertia inversion, LFDD threshold |
| `src/GridSim.Cli/Program.cs` | `RunReplayEvent`, `RunScenario`, `RunH0Study`, `RunFleet`, `RunEvidence` | The verbs, their gates, exit codes and output files |
| `src/GridSim.Cli/CliDefaults.cs` | `CliDefaults` | Device, fleet and cost defaults |

## Limits

- The validator's verdict scores the nadir value only. When the record gives the nadir time (`RecordedOutcome.NadirSeconds`) and the simulated nadir is more than `ReplayTolerance.NadirTimeToleranceS` (2.0 s) away from it, the summary carries a `[nadir time NOT reproduced: ...]` note (`ReplayResult.NadirTimingNote`) and the verdict is unchanged.
- Corners leave the RoCoF unchanged only when the peak falls at the first loss; on a staged sequence such as `gb-2019-08-09` the peak is at 3.0 s and moves with the corner.
- Without `--trace` the gate of `scenario`, `fleet`, `evidence` and `h0-study` runs without the RMSE check, so `gb-2019-08-09` is `Reproduced` there; with `--trace` it is `Marginal`, as under `replay-event --trace`, and the study verbs withhold its counterfactual.
- The inertia cross-check inverts a windowed recorded RoCoF after scaling it by the replay's own instantaneous / windowed ratio (1.266 for `gb-2023-12-22`), so for that event it reads 193.6 GVA.s against a stated 161.0.
- The dossier's fleet section builds its units with the default 0.5 s lag and 0.015 Hz deadband and ignores the device's own `FastResponseTimeConstantS` and `FastResponseDeadbandHz`.
- The VoLL and the outage duration are policy placeholders; a cited VoLL source is checked against the figures it states but does not make the cost `Recorded`.
- A counterfactual drops every recorded action, including the balancing-mechanism recovery of `gb-2023-12-22`, not only LFDD.
- `replay-event --trace` with a trace holding a timestamp newer than the horizon ends with an unhandled `ForwardInferenceException`, not the exit-2 refusal the catalogue path gives.
- `NesoFrequencyCsv` skips unparsable lines silently, and `RocofUpsampler` assumes a regular 1-second cadence (a gap is interpolated into the same number of points).
- `EventCatalog.Load` checks that `schema` and `events` exist but not the schema id, and does not reject duplicate ids or an empty loss list.
- The gate tests agreement with the record, not that the record is real: `gb-largest-loss-1320` passes the gate with placeholder figures (and `replay-event` refuses it on its provenance).
- The tolerances (0.15, 0.15 Hz, 0.05 Hz) and the corner multipliers are engineering choices with no external derivation.
- Equality of these paths across CPU tiers follows from scalar arithmetic and is not compared by an automated test.

## See also

- [ch. 5, Frequency dynamics](<0005 Frequency dynamics.md>) - the swing model, `DynamicsConfig`, response-service curves, `InertiaEstimator`, `FrequencyLimits`
- [ch. 8, Provenance and invariants](<0008 Provenance and invariants.md>) - `ProvenanceTag`, `[VERIFY]` and the horizon guard
- [ch. 10, File and wire formats](<0010 File and wire formats.md>) - `gridsim-event-catalog/1` and `gridsim-evidence/1`
- [ch. 14, Cascade validation and blind prediction](<0014 Cascade validation and blind prediction.md>) - the Loss-of-Mains cascade used by the H0 study
- User manual: [ch. 18, Replaying recorded events](<../manual/0018 Replaying recorded events.md>), [ch. 20, Counterfactuals](<../manual/0020 Counterfactuals.md>)
- CLI reference: [`replay-event`](<../manual/0019 CLI reference.md#replay-event>), [`scenario`](<../manual/0019 CLI reference.md#scenario>), [`h0-study`](<../manual/0019 CLI reference.md#h0-study>), [`fleet`](<../manual/0019 CLI reference.md#fleet>), [`evidence`](<../manual/0019 CLI reference.md#evidence>)
