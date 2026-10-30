# 14. Cascade validation and blind prediction

This chapter specifies how GridSim tests whether its frequency models predict a recorded event or only reproduce
it, and what the tests establish for the asset-dynamics cascade: embedded generation that trips on its own
rate-of-change-of-frequency (RoCoF) protection, feeds the lost infeed back into the swing equation and deepens the
fall. The protocol freezes every unmeasurable constant, feeds the model only recorded facts about the event, and
compares the model nadir with the recorded nadir. The tests are
`tests/GridSim.Tests/BlindPredictionTests.cs` (base swing model, two recorded GB events),
`tests/GridSim.Tests/Asset2019EventTests.cs` (the cascade on 9 August 2019),
`tests/GridSim.Tests/AssetEnvelopeTests.cs` (the sweep envelope) and
`tests/GridSim.Tests/AssetValidationTests.cs` (the back-test gate). The swing model is specified in
[ch. 5, Frequency dynamics](<0005 Frequency dynamics.md>), the asset fleet and its data in
[ch. 13, Asset-dynamics data grounding](<0013 Asset-dynamics data grounding.md>), and the replay gate and
evidence dossier in [ch. 7, The evidence engine](<0007 The evidence engine.md>).

The outcome, stated once. The cascade mechanism is reproduced: with every catalogue loss except the embedded-generation
trips fed in (the Hornsea and Little Barford losses, the later 100, 210 and 187 MW trips, and the recorded LFDD shed),
a legacy embedded fleet trips on its own relay setting. The cascade depth is not predicted: the nadir
is a function of the unmeasured embedded-trip volume and moves 2.7297 Hz across 500 to 4000 MW. The cascade cannot be
blind-tested on GB data because 9 August 2019 is the only recorded GB embedded-generation cascade. Its standing is
mechanism and envelope tool, tagged `ProvenanceTag.UnvalidatedAtScale`: use it for counterfactuals, sensitivity
sweeps and screening, and treat a single cascade nadir as a scenario outcome. The base swing model is the part that
is cross-checked on two recorded events: it matches 2019 as a replay (0.0410 Hz) and misses 2023 as a forward
estimate (0.2955 Hz too deep). That result belongs to the base model, not to the asset framework.

## Model

### What is predicted and what is held out

| Item | Treatment |
|---|---|
| Nadir (Hz), initial RoCoF (Hz/s), nadir time (s) | Predicted. Compared with the recorded outcome of the event (`RecordedOutcome`). |
| Droop, damping, responsive fraction, governor time constant | Frozen. One `DynamicsConfig` for every event (table below). Never fitted to an event. |
| Pre-event inertia, demand, loss steps, recorded actions | Recorded inputs, different per event. Facts, not knobs. |
| Procured response services (DC, DM, DR volumes and curves) | Recorded input where the event declares them (`GridEvent.ResponseServices`). |
| Pre-event frequency | Recorded input (`GridEvent.PreEventFrequencyHz`); the run starts in equilibrium there. |
| Recorded low-frequency demand disconnection (LFDD) | Recorded action. Feeding it in makes the run a replay (see below). |
| Embedded-generation trip volume | For the 2019 base model, the four Ofgem-derived embedded losses are fed as recorded steps (150, 390, 200 and 550 MW, 1290 MW in total). The volume that a relay cascade produces is unmeasured, and is swept in the cascade model. |
| Relay settings, inverter control loops | Unmeasured. Class parameters tagged `[VERIFY]`; swept. |

The single mapping from an event to a config is `GridEvent.ApplyRecordedServices`
(`src/GridSim.Core/Events/GridEvent.cs`). It sets `SystemResponseServices` from `ResponseServices`, sets
`InitialFrequencyHz` from `PreEventFrequencyHz`, and, when `ResponseServicesComplete` is true, sets
`ResponsiveFraction` to 0 so the generic governor does not double-count a response the record already lists. The
replay baseline, the scenario sweep, the fleet comparison, the evidence dossier and these tests all build their
config through it.

### Replay and forward estimate

A run is a replay when its inputs include an action that is itself a consequence of the frequency fall. 2019 feeds
the recorded LFDD shed (+892 MW at 76 s) and the full recorded loss sequence, so the run confirms the integrator,
not predictive power. A run is a forward estimate when no recorded action beyond the exogenous losses and the
balancing-mechanism recovery is fed in and nothing is fitted to the nadir. 2023 is the forward estimate: its inputs
are the two metered losses, the measured DC, DM and DR volumes, the measured pre-event frequency and the
Balancing Mechanism (BM) recovery ramp derived from the acceptance record. Only a forward estimate counts as
evidence about prediction. The 2023 inputs are measured, but they were chosen with the trace in view (the pre-event
frequency is read off it and the BM ramp matches its recovery through 49.5 Hz), so it is a forward estimate in the
protocol's sense, not a held-out one.

### Equations as implemented

The base model is the single-mass swing equation of `FrequencyDynamics.Simulate`
(`src/GridSim.Core/Dynamics/FrequencyDynamics.cs`), integrated by explicit Euler at `StepSeconds` (0.02 s):

```
E       = max(1, SystemInertia + DeviceRotationalInertia)             MW.s
D       = DampingPctPerHz / 100 * DemandMw                            MW/Hz
PResp   = ResponsiveFraction * DemandMw                               MW
pEvent  = sum of DeltaPMw over events with AtSeconds <= t + 1e-6 dt   MW
pGov   += (-(df/f0)/DroopPu * PResp - pGov) / GovernorTimeConstantS * dt
pSvc_s += (target_s(df) - pSvc_s) / max(dt, tau_s) * dt               for each recorded service s
pImb    = pEvent + pStanding + pGov + pTsgb + sum(pSvc_s) - D * df    df = f - f0
rocof   = f0 / (2 * E) * pImb
f      += rocof * dt
```

`target_s(df)` is `SwingKernel.FastResponseTarget` (`src/GridSim.Core/Dynamics/SwingKernel.cs`): zero for
`|df| <= DeadbandHz`; with `a = |df|`, below the knee `Mw * KneeFraction * (a - Deadband) / (Knee - Deadband)`,
above it `Mw * (KneeFraction + (1 - KneeFraction) * (a - Knee) / (Full - Knee))`, capped at `Mw` and signed
`-sign(df)`. A knee at or below zero gives the line through zero `-(Mw / Full) * df`, clamped to `+/- Mw`.

When `InitialFrequencyHz` is set the run starts in equilibrium at that value:

```
df0       = fInit - f0
pGov(0)   = -(df0/f0)/DroopPu * PResp
pSvc_s(0) = target_s(df0)
pStanding = -(pGov(0) + sum(pSvc_s(0)) - D * df0)         so rocof(0) = 0 before the first loss
```

The initial RoCoF depends on inertia and the first losses only, and has no free parameter:

```
rocof0 = f0 * dP_early / (2 * E)        dP_early = sum of |DeltaPMw| over losses with AtSeconds <= 1.0
```

The cascade adds two terms to the imbalance sum, appended last (`AssetFrequencyDynamics.Simulate`,
`src/GridSim.Core/Dynamics/AssetFrequencyDynamics.cs`): the fleet's net swing power and the cumulative lost
dispatched generation of tripped units. A grid-following embedded unit trips when the delayed local RoCoF, scaled by
the local uplift, exceeds the relay threshold for the confirmation time
(`GridFollowingInverter.Update`, `src/GridSim.Core/Assets/GridFollowingInverter.cs`):

```
overRocof = |rocofMeas| * localRocofUpliftFactor > rocofTripHzPerS
n         = overRocof ? n + 1 : 0                         (ConfirmTimer: whole sub-steps counted)
rocofTrip = overRocof and DwellReached(n * dt, rocofTripConfirmS)
```

`DwellReached` is the protection layer's rule ([ch. 15](<0015 Protections.md>)): the counted time reaches the confirm
time, or is within `2^-49` of it relative, so a representation error in `dt` cannot add a step.

Once tripped the unit delivers 0 MW and reports its lost infeed `_pgMw` to the swing sum. The effective COI
threshold is `rocofTripHzPerS / localRocofUpliftFactor`: 0.125 / 1.16 = 0.10776 Hz/s for the legacy class used in
the 2019 test.

The envelope (`AssetEnvelope.Sweep`, `src/GridSim.Core/Assets/AssetEnvelope.cs`) runs the 2^k corners of k swept
class parameters (k at most 12) and reports `NadirMinHz`, `NadirMaxHz`, `NadirMedianHz` (element `n/2` of the sorted
nadirs), the RoCoF range, a one-at-a-time tornado with the other parameters at their midpoint, and
`CascadeIsPossible = any corner trips and any corner does not`.

### Acceptance rules

| Rule | Value | Where |
|---|---|---|
| 2019 replay residual | `|model - recorded| < 0.10` Hz | `Base_model_reproduces_2019_when_fed_the_recorded_facts` |
| 2019 recorded nadir pinned | 48.8 Hz to 1 decimal place | same test |
| 2023 forward residual | `recorded - model` in `[0.15, 0.45]` Hz (positive means the model is too deep) | `Base_model_forward_estimates_2023_to_within_a_few_tenths_Hz` |
| 2023 recorded nadir pinned | 49.275 Hz to 3 decimal places | same test |
| Parameter-free initial RoCoF | in `[0.10, 0.20]` Hz/s for both events | `Parameter_free_initial_rocof_is_physically_consistent_for_both_events` |
| No second GB cascade | 2019 has a loss containing "distributed generation"; 2023 has none | `The_cascade_has_no_second_gb_event_to_blind_predict` |
| Cascade mechanism | a legacy fleet of 1290 MW trips (`TrippedCount > 0`, `TotalTrippedMw > 0`) | `The_cascade_mechanism_emerges_from_first_principles` |
| Envelope width | nadir at 500 MW minus nadir at 2500 MW exceeds 1.0 Hz | `The_recorded_nadir_lies_WITHIN_the_embedded_trip_envelope_not_at_a_predicted_point` |
| Envelope brackets the record | 48.787 Hz (the recorded 76 s minimum, ch. 13) lies between the 2500 MW and 500 MW nadirs | same test |
| Ride-through counterfactual | a 10 Hz/s fleet at 1290 MW trips 0 units | `No_dg_fleet_means_no_cascade_and_a_shallower_fall` |
| Sweep brackets trip and no trip | `CascadeIsPossible`, `NadirMinHz < NadirMaxHz`, 2 samples for k = 1, tag `UnvalidatedAtScale` | `AssetEnvelopeTests.Sweeping_the_trip_threshold_brackets_cascade_and_no_cascade` |
| Back-test gate | `|nadir err| <= 0.05` Hz and `|RoCoF err| <= 0.05` Hz/s and `|nadir time err| <= 2.0` s | `AssetValidation.Compare`, `ValidationTolerance.Default` |

The 2019 band is a replay check and the 2023 band is a documented miss: the test fails if the miss shrinks below
0.15 Hz or grows above 0.45 Hz, so a change to the base arrest model shows up in either direction.

`AssetValidation.Compare` (`src/GridSim.Core/Assets/AssetValidation.cs`) scores a simulated `FrequencyResponse`
against a `RecordedFrequencyTrace`. The recorded RoCoF is the peak first-difference slope over the trace points;
the recorded nadir time is the time of the minimum point. The RoCoF is compared on one basis: when the trace is coarser
than the simulation step, the simulation is read on the trace's own window (`ReplayValidator.MaxWindowedRocof`).
The gate is applied in two places. The credibility gate (`ReplayValidator.Validate`, used by `gridsim replay-event`)
runs `Compare` whenever a recorded trace is attached, reports it as `ReplayResult.BackTest`, and caps a Reproduced
verdict at Marginal when the back-test fails. `AssetValidation.BlindPredict(frozen, shared, blindEvent, recorded)` is
the blind step of the protocol: it replays the blind event with the shared configuration and back-tests it, and throws
`InvalidOperationException`, naming every difference, when the configuration changes any constant held in the
`FrozenParameterSet` captured at the anchor (`FrozenParameterSet.Differences`), or when the blind event is the anchor
event itself.

The replay gate used by `gridsim replay-event` (`ReplayValidator`, `src/GridSim.Core/Replay/ReplayValidator.cs`)
applies a looser rule to the same events: Reproduced needs `|RoCoF| relative error <= 0.15` and
`|nadir error| <= 0.15` Hz; Marginal needs both within twice that; otherwise Failed. A per-sample RMSE above 0.05 Hz,
or a failed back-test (above), caps a Reproduced verdict at Marginal. Only a Reproduced baseline unlocks counterfactuals. See
[ch. 7](<0007 The evidence engine.md>) and the [`replay-event` section of the CLI reference](<../manual/0019 CLI reference.md#replay-event>).

### Event qualification

An event is a valid step-loss test only if its recorded RoCoF obeys step physics at the recorded inertia. The check
is the closure ratio, the inertia implied by the recorded RoCoF over the recorded inertia, with the implied inertia
`E = f0 * dP / (2 * |rocof|)` (`InertiaEstimator.EstimateInertiaMwS`, `src/GridSim.Core/Dynamics/InertiaEstimator.cs`).

| Event | Implied inertia / recorded inertia | Qualifies |
|---|---|---|
| 2019-08-09 | 223.7 / 215.0 GVA.s = 1.04 (replay capture); the GDA report's swing-equation estimate gives 267.8 / 215 = 1.25 | yes |
| 2023-12-22 | 193.6 / 161.0 GVA.s = 1.20 (replay capture; the recorded RoCoF is a 1 s window figure, inverted after scaling by the replay's instantaneous / windowed ratio 1.266) | yes |
| 2026-05-31 (992 MW) | 19.32, from the GDA report of that event | no |

The 2026-05-31 report records a peak RoCoF of +0.007 Hz/s. Step physics at its recorded 183 GVA.s gives
`50 * 992 / (2 * 183000) = 0.1355` Hz/s, so the recorded fall is 19 times slower than a step: a gradual imbalance.
The event is excluded from step-loss prediction. No code computes a closure ratio or applies a qualification threshold:
the qualification is a judgement from the ratio, and a ratio near 1 (1.04, 1.20) qualifies while 19.32 does not. The
figure 19.32 is the report's own; the rounded inputs above give 19.4. The figures for this event come from the GDA analysis report, which is
not part of this repository; the catalogue carries no entry for it.

## Parameters and defaults

Frozen constants, the same for every event (`DynamicsConfig`, `src/GridSim.Core/Dynamics/FrequencyDynamics.cs`;
`BlindPredictionTests.SharedConfig` changes only the duration):

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| `DroopPu` | 0.05 | pu | Governor droop. GB Grid Code figure, not fitted | `DynamicsConfig.DroopPu` |
| `DampingPctPerHz` | 1.0 | % of demand per Hz | Load self-damping. Literature value, unfitted | `DynamicsConfig.DampingPctPerHz` |
| `ResponsiveFraction` | 0.20 | fraction of demand | Governor-responsive capacity. Unfitted. Forced to 0 when an event's service list is complete | `DynamicsConfig.ResponsiveFraction` |
| `GovernorTimeConstantS` | 8.0 | s | Primary-response lag. Unfitted | `DynamicsConfig.GovernorTimeConstantS` |
| `StepSeconds` | 0.02 | s | Integration step | `DynamicsConfig.StepSeconds` |
| `DurationSeconds` | 150 | s | Window used by the blind tests | `BlindPredictionTests.SharedConfig` |
| `NominalHz` | 50.0 | Hz | `f0` | `DynamicsConfig.NominalHz` |

Recorded inputs per event (`EventCatalog.Builtin`, `src/GridSim.Core/Events/EventCatalog.cs`):

| Input | 2019-08-09 | 2023-12-22 |
|---|---|---|
| `PreEventInertiaMwS` | 215,000 (NESO recorded outturn) | 161,000 (NESO recorded outturn) |
| `PreEventDemandMw` | 21,100 (WLS-estimated transmission-net demand) | 22,400 (WLS-estimated transmission-net demand) |
| Losses | nine steps, 2768 MW in total, including the embedded-generation trips (Ofgem 2.4.x) | IFA -1000 MW at 0.5 s, Cottam -345 MW at 9.5 s (settlement-metered) |
| Recorded actions | LFDD +892 MW at 76 s | BM recovery ramp, twelve steps from 19 s to 129 s, +904 MW in total |
| `ResponseServices` | none | DCL 873 MW, DML 150 MW, DRL 203 MW (EAC auction 70), 1226 MW in total |
| `ResponseServicesComplete` | false (`ResponsiveFraction` stays 0.20) | true (`ResponsiveFraction` becomes 0) |
| `PreEventFrequencyHz` | nominal | 49.879 |
| Recorded nadir | 48.8 Hz | 49.275 Hz |
| Recorded initial RoCoF | -0.17 Hz/s (instantaneous basis) | -0.102 Hz/s (1 s window, `RocofMeasurementWindowS` = 1.0) |

The 2019 catalogue entry is also shipped as a measured catalogue, `data/events/gb-2019-08-09-estimated.json`, with a
demand of 21,147.2 MW. The tests use the built-in entry.

Cascade fleet parameters used by `Asset2019EventTests` (`AssetTestHelpers.GflClass`, `AssetParams` in
`src/GridSim.Core/Assets/AssetClassLibrary.cs`):

| Parameter | Value | Unit | Meaning |
|---|---|---|---|
| `rocofTripHzPerS` | 0.125 (legacy), 10.0 (modern, rides through) | Hz/s | Loss-of-mains relay setting; 0.125 is the pre-ALoMCP setting |
| `localRocofUpliftFactor` | 1.16 | ratio | Sub-cycle local RoCoF over the 1 s COI value |
| `rocofTripConfirmS` | 0.0 in the tests | s | Trip on the first sample over threshold. The code fallback is 0.5 |
| `headroomUpFraction`, `headroomDownFraction` | 0.0 | fraction | The embedded unit contributes no frequency response |
| Fleet size | 500, 1290, 2500, 4000 | MW | Unmeasured embedded-trip volume; swept. 1290 MW is the catalogue's embedded total (150 + 390 + 200 + 550) |
| Synchronous plant | 20,000 MW dispatched, `MBase` 43,000 MVA, `H` 5.0 s | | `E = 5.0 * 43,000 = 215,000` MW.s |
| Demand | 21,100 | MW | Same as the catalogue entry |

The cascade runs feed every catalogue loss whose description does not contain "distributed generation" (-737 MW at
0.5 s, -244 MW at 1.0 s, -100 MW at 40 s, -210 MW at 58 s, -187 MW at 90 s), plus the recorded LFDD action (+892 MW at
76 s). By the definition above that makes them replays. The shed lies after the cascade nadirs (13.5 to 13.7 s) and
does not change them: without it the 1290 MW run still bottoms at 48.2291 Hz at 13.62 s.

## Results

All numbers below were produced by the code the tests call: the base-model values by the baseline swing export of
`replay-event` (`baseline-frequency.json`, a 120 s window for 2019 and 159 s for 2023), the cascade values by `Asset2019EventTests.RunWithEmbeddedFleet` at its 120 s window with the
fleet sizes shown. Timings, durations and stamps in
captured output differ by machine and run.

### Base model, two recorded events

| Event | Run type | Recorded nadir | Model nadir | Residual (recorded - model) | Model nadir time | Initial RoCoF (model) |
|---|---|---|---|---|---|---|
| 2019-08-09 | replay | 48.800 Hz | 48.8410 Hz | -0.0410 Hz (model 0.0410 Hz too shallow) | 76.00 s | -0.1710 Hz/s |
| 2023-12-22 | forward estimate | 49.275 Hz | 48.9795 Hz | +0.2955 Hz (model too deep) | 49.00 s | -0.1553 Hz/s (instantaneous peak; the replay gate scores the 1 s window figure, -0.123) |

Parameter-free RoCoF, `f0 * dP_early / (2 * E)`: 2019 `50 * 1131 / (2 * 215000) = 0.1315` Hz/s; 2023
`50 * 1000 / (2 * 161000) = 0.1553` Hz/s. Both lie inside the pinned band 0.10 to 0.20 Hz/s and next to the recorded
figures (2019 -0.17 instantaneous and -0.151 on a 1 s window; 2023 -0.102 on a 1 s window). One-second averaging lowers the peak of
a sharp step.

What the numbers show:

- 2019 is a replay. The 0.0410 Hz agreement confirms the integrator at 215 GVA.s with the recorded losses, response
  services and shed. The first fall is arrested at 49.2339 Hz at 16.72 s; the model recovers to 49.3204 Hz at 40 s,
  falls with the later losses to 48.8569 Hz at 75 s and 48.8425 Hz at 75.9 s, and reaches its minimum, 48.8410 Hz, at
  76.0 s, when the final embedded-generation loss and the +892 MW shed land together; the shed lifts it from there. The
  recorded minimum of 48.8 Hz at 75.9 s belongs to the same 76 s event.
- 2023 is the forward number. The model under-arrests by 0.2955 Hz. The model minimum falls at 49.00 s against a
  recorded minimum at 20 s (13:10:11 against an onset of 13:09:51), and the recorded nadir lies outside the model's
  own declared-uncertainty corner envelope. The residual comes from the base arrest model (damping and the service
  time constants), because the 2023 event carries no embedded cascade.

### The replay gate on the same events

Output of `gridsim replay-event gb-2019-08-09` (`docs/_captures/events/replay-event-gb-2019-08-09.txt`):

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
...
[exit 0]
```

Output of `gridsim replay-event gb-2023-12-22 --trace data/neso/gb-2023-12-22-frequency.csv`
(`docs/_captures/events/replay-event-gb-2023-12-22-trace.txt`):

```
$ gridsim replay-event gb-2023-12-22 --trace data/neso/gb-2023-12-22-frequency.csv

=== Replay: GB 22 Dec 2023 excursion (IFA + Cottam, settlement-measured) ===
  when (UTC):        2023-12-22T13:09:51.0000000Z   provenance: DerivedFromRecorded (event tagged Recorded; the weakest of its parts)
  pre-event inertia: 161.0 GVA.s   demand: 22.4 GW   total loss: 1345 MW
  inferred inertia from recorded RoCoF: 193.6 GVA.s (cross-check vs 161.0)
    (recorded RoCoF is a 1 s windowed figure; inverted after scaling by the replay's instantaneous / windowed ratio 1.266)

  RoCoF:  sim  -0.123   recorded  -0.102 Hz/s   (20 % error)
  Nadir:  sim   48.98   recorded   49.27 Hz   (0.30 Hz error)
  Nadir envelope (declared-uncertainty corners): [48.65, 49.20] Hz - recorded OUTSIDE (a constant the corners don't cover - e.g. the inertia basis - is off)
  RMSE vs trace: 0.281 Hz over 7926 sample(s)
  back-test vs trace (gate 0.05 Hz / 0.05 Hz/s / 2.0 s): FAIL  nadir 0.295 Hz, RoCoF sim -0.123 vs rec -0.102 Hz/s, nadir time sim 49.0 s vs rec 20.5 s (28.5 s)
  assessment: BELOW STATUTORY (49.5 Hz)

  VERDICT: MARGINAL  - NOT reproduced; do not run counterfactuals until the baseline matches (check [VERIFY] figures / tolerances).
...
[exit 3]
```

With the 2019 trace attached (`gridsim replay-event gb-2019-08-09 --trace data/neso/gb-2019-08-09-frequency.csv`,
`docs/_captures/events/replay-event-gb-2019-08-09-trace.txt`) the per-sample RMSE is 0.099 Hz, the back-test fails on
the nadir (0.054 Hz against the 0.05 Hz gate), and the verdict drops to MARGINAL (exit 3), because the headline
metrics match and the trace shape does not. The nadir match of 0.04 Hz is a
match of one number, not of the trajectory. The 2023 verdict is MARGINAL because both metrics miss the Reproduced
tolerance: the RoCoF error is 20 % (limit 15 %) and the nadir error 0.2955 Hz (limit 0.15 Hz). Both lie inside twice
the tolerance, the nadir by 0.0045 Hz.

### Cascade on 9 August 2019

The non-embedded losses and the recorded shed (listed above) are fed at 215 GVA.s into a model whose embedded fleet is a legacy class
(0.125 Hz/s, uplift 1.16). The COI RoCoF reaches `50 * 737 / (2 * 215000) = 0.0857` Hz/s after the Hornsea step and
`50 * 981 / (2 * 215000) = 0.1141` Hz/s after the Little Barford step at 1.0 s, which exceeds the effective threshold
0.10776 Hz/s, so the fleet trips at that point on its own protection.

| Embedded fleet | Nadir | Nadir time | Peak RoCoF | Units tripped |
|---|---|---|---|---|
| none tripped (modern class, 10 Hz/s, 1290 MW) | 49.2350 Hz | 13.40 s | -0.1128 Hz/s | 0 |
| legacy, 500 MW | 48.8452 Hz | 13.54 s | -0.1709 Hz/s | 1 (500.0 MW) |
| legacy, 1290 MW | 48.2291 Hz | 13.62 s | -0.2628 Hz/s | 1 (1290.0 MW) |
| legacy, 2500 MW | 47.2854 Hz | 13.68 s | -0.4035 Hz/s | 1 (2500.0 MW) |
| legacy, 4000 MW | 46.1155 Hz | 13.70 s | -0.5779 Hz/s | 1 (4000.0 MW) |

The nadir spans 48.8452 - 46.1155 = 2.7297 Hz across the fleet sizes. The recorded 48.787 Hz (the minimum at 76 s, 15:53:49Z, ch. 13; the
catalogue rounds it to 48.8 Hz) lies inside the 500 to 2500 MW band `[47.2854, 48.8452]`, and a fleet of 574.621 MW
gives a first-dip nadir of exactly 48.7870 Hz (bisection on fleet size). The two numbers are at different times: the
cascade nadirs are the first dip at about 13.6 s, while 48.787 Hz is the 76 s minimum. The fleet size is selected by
the recorded number (see the outcome above). The no-cascade run bottoms at 49.2350 Hz, 0.448 Hz above the recorded nadir, which is the
depth the embedded trips add.

## Why the cascade cannot be blind-tested on GB data

A blind test needs a second event in which embedded generation tripped on RoCoF. The GB record has none.

1. 9 August 2019 is the only major GB embedded-generation RoCoF cascade. The catalogue records its trips as losses
   (150 MW vector-shift trip at 0.1 s, Ofgem 2.4.2; 390 MW at 3 s, the midpoint of the reported 350 to 430 MW; 200 MW
   at 65 s; 550 MW at 76 s; Ofgem 2.4.6, 2.4.11, 2.4.13).
2. The Accelerated Loss-of-Mains Change Programme (ALoMCP) raised embedded relays from 0.125 Hz/s to 1.0 Hz/s
   (`FrequencyLimits.RocofLimitModernHzPerS`, `src/GridSim.Core/Dynamics/FrequencyLimits.cs`) with a 500 ms
   definite-time delay, to remove the mechanism.
3. Every later recorded GB event carries no embedded trips. `The_cascade_has_no_second_gb_event_to_blind_predict`
   pins it for 2023.

The real-fleet
deliverable is the sweep envelope (`AssetEnvelope.Sweep`), whose `AssetEnvelopeResult` carries
`ProvenanceTag.UnvalidatedAtScale` by default. Only new measured evidence changes this: per-feeder embedded trip
telemetry for a real event, or a future recorded GB cascade.

## Invariants

| Invariant | Test |
|---|---|
| One config for all events: per-event differences enter only through `ApplyRecordedServices` | `BlindPredictionTests.Replay`; `ResponseServiceTests.ApplyRecordedServices_maps_and_completeness_zeroes_the_governor` |
| 2019 replay residual below 0.10 Hz; recorded nadir 48.8 | `BlindPredictionTests.Base_model_reproduces_2019_when_fed_the_recorded_facts` |
| 2023 forward residual in 0.15 to 0.45 Hz, model too deep; recorded nadir 49.275 | `BlindPredictionTests.Base_model_forward_estimates_2023_to_within_a_few_tenths_Hz` |
| Parameter-free initial RoCoF in 0.10 to 0.20 Hz/s for both events | `BlindPredictionTests.Parameter_free_initial_rocof_is_physically_consistent_for_both_events` |
| 2019 carries embedded-generation losses, 2023 carries none | `BlindPredictionTests.The_cascade_has_no_second_gb_event_to_blind_predict` |
| The cascade emerges from the relay setting with no scripted embedded steps | `Asset2019EventTests.The_cascade_mechanism_emerges_from_first_principles` |
| The nadir band is wider than 1.0 Hz and brackets 48.787 Hz | `Asset2019EventTests.The_recorded_nadir_lies_WITHIN_the_embedded_trip_envelope_not_at_a_predicted_point` |
| A ride-through fleet produces no trips | `Asset2019EventTests.No_dg_fleet_means_no_cascade_and_a_shallower_fall`; `InverterTripCascadeTests.A_fleet_below_its_limit_does_not_trip` |
| A lower relay threshold trips and deepens the nadir | `InverterTripCascadeTests.A_low_rocof_threshold_trips_and_deepens_the_nadir` |
| Local RoCoF uplift trips a relay the COI value would not | `InverterTripCascadeTests.Local_rocof_uplift_makes_a_relay_trip_that_the_coi_value_would_not` |
| The sweep brackets trip and no trip, is tagged `UnvalidatedAtScale`, ranks the trip threshold first | `AssetEnvelopeTests.Sweeping_the_trip_threshold_brackets_cascade_and_no_cascade` |
| A ride-through-only range reports no cascade | `AssetEnvelopeTests.A_ride_through_only_range_reports_no_cascade` |
| An empty or unmatched fleet reproduces the baseline bit for bit | `AssetDynamicsReductionTests.Flag_off_is_bit_identical_to_the_lumped_baseline`, `Enabled_but_nothing_bound_reduces_to_baseline`, `Enabled_with_only_unmatched_assets_reduces_to_baseline` |
| The back-test gate passes a matching trace and fails one shifted by 0.3 Hz | `AssetValidationTests.A_matching_simulation_passes_the_gate`, `A_perturbed_recording_fails_the_gate` |
| Initial RoCoF reproduces the recorded 2019 figure: -0.151 Hz/s at 267.8 GVA.s, -0.188 Hz/s at 215 GVA.s | `AssetValidationTests.Recorded_2019_08_09_initial_rocof_is_reproduced_at_the_reported_inertia` |
| The replay gate reproduces a self-consistent event, fails a corrupted one, marks a slightly off one Marginal and strips any device | `ReplayValidatorTests` |

## Worked example

Question: does the legacy embedded fleet trip in the 2019 replay, and how much does the answer depend on its size?

1. Take the 2019 catalogue entry: `E = 215,000` MW.s, demand 21,100 MW, `ApplyRecordedServices` leaves
   `ResponsiveFraction` at 0.20 because the entry declares no services.
2. Remove the embedded-generation losses and keep the rest (Hornsea -737 MW at 0.5 s, Little Barford -244 MW at 1.0 s,
   the later GT trips and the +892 MW shed at 76 s).
3. Bind a legacy class (`rocofTripHzPerS` 0.125, uplift 1.16, no headroom) to a 1290 MW unit with `H = 0`, so the
   system inertia stays 215,000 MW.s.
4. At 1.0 s the COI RoCoF is `50 * (737 + 244) / (2 * 215000) = 0.1141` Hz/s. The relay sees
   `0.1141 * 1.16 = 0.1324` Hz/s against 0.125, and trips.
5. The lost 1290 MW enters the swing sum. The peak RoCoF becomes -0.2628 Hz/s
   (`50 * (981 + 1290) / 430000 = 0.2641` before damping and governor response) and the nadir is 48.2291 Hz at
   13.62 s.
6. Repeat at 500, 2500 and 4000 MW: 48.8452, 47.2854 and 46.1155 Hz. The same trip, the same loss sequence, a
   2.7297 Hz spread. The recorded 48.787 Hz selects 574.621 MW.
7. Replace the class by a modern one (10 Hz/s). No unit trips and the nadir is 49.2350 Hz.

Steps 4 and 7 show the mechanism; steps 5 and 6 show how the depth depends on the fleet size.

## Source map

| File | Type | Responsibility |
|---|---|---|
| `tests/GridSim.Tests/BlindPredictionTests.cs` | tests | Base-model cross-event protocol: shared config, 2019 replay, 2023 forward estimate, parameter-free RoCoF, no second cascade |
| `tests/GridSim.Tests/Asset2019EventTests.cs` | tests | Cascade mechanism, nadir envelope and ride-through counterfactual on 2019 |
| `tests/GridSim.Tests/AssetEnvelopeTests.cs` | tests | Sweep envelope, tornado ranking, `UnvalidatedAtScale` tag |
| `tests/GridSim.Tests/AssetValidationTests.cs` | tests | Back-test gate and 2019 RoCoF anchor |
| `tests/GridSim.Tests/InverterTripCascadeTests.cs` | tests | Relay trip, nadir deepening, local RoCoF uplift |
| `tests/GridSim.Tests/AssetDynamicsReductionTests.cs` | tests | Empty-fleet reduction to the baseline |
| `src/GridSim.Core/Events/EventCatalog.cs` | `EventCatalog` | The built-in recorded events and their provenance tags |
| `src/GridSim.Core/Events/GridEvent.cs` | `GridEvent`, `ResponseService`, `RecordedOutcome` | Recorded inputs and outcome; `ApplyRecordedServices` |
| `src/GridSim.Core/Dynamics/FrequencyDynamics.cs` | `DynamicsConfig`, `FrequencyDynamics` | Frozen constants and the swing integrator |
| `src/GridSim.Core/Dynamics/SwingKernel.cs` | `SwingKernel` | Governor and service delivery curves |
| `src/GridSim.Core/Dynamics/AssetFrequencyDynamics.cs` | `AssetFrequencyDynamics`, `AssetFrequencyResult` | Swing solve with the asset fleet and the trip deficit |
| `src/GridSim.Core/Assets/GridFollowingInverter.cs` | `GridFollowingInverter` | Relay trip rule with local RoCoF uplift |
| `src/GridSim.Core/Assets/AssetEnvelope.cs` | `AssetEnvelope`, `AssetEnvelopeResult` | Corner sweep, tornado, `CascadeIsPossible`, provenance tag |
| `src/GridSim.Core/Assets/AssetValidation.cs` | `AssetValidation`, `ValidationTolerance`, `FrozenParameterSet` | Back-test gate against a recorded trace; the enforced freeze and blind prediction |
| `src/GridSim.Core/Replay/ReplayValidator.cs` | `ReplayValidator`, `ReplayTolerance` | Replay verdict and corner envelope used by `gridsim replay-event` |
| `src/GridSim.Core/Dynamics/InertiaEstimator.cs` | `InertiaEstimator` | Inertia implied by a recorded RoCoF (closure ratio) |

## Limits

- The freeze is enforced only on the blind step: `AssetValidation.BlindPredict` refuses a configuration that changed a
  frozen constant, but nothing stops a caller from fitting constants and freezing the result as a new anchor, and no
  CLI verb runs a blind prediction.
- The 0.05 Hz, 0.05 Hz/s and 2.0 s back-test runs in the credibility gate only when a recorded trace is attached to the
  event; it can only demote a Reproduced verdict, never promote one.
- The 2023 forward estimate misses the recorded nadir by 0.2955 Hz and the nadir time by 29 s, and ends MARGINAL in the
  replay gate; the base arrest model (damping and service time constants) is not fitted to a recorded trip.
- The embedded-trip volume and every inverter control and relay parameter are unmeasured; the cascade result is a
  band, `UnvalidatedAtScale`.
- The vector-shift trip is a frequency-offset proxy and not a phase-jump detector.
- The cascade runs on the single-bus lumped model; per-node validation against the four-node PMU data awaits the
  distributed model carrying the asset fleet.
- The 2026-05-31 closure figures come from a GDA report outside this repository and cannot be reproduced from it.

## See also

- [Ch. 5, Frequency dynamics](<0005 Frequency dynamics.md>)
- [Ch. 7, The evidence engine](<0007 The evidence engine.md>)
- [Ch. 13, Asset-dynamics data grounding](<0013 Asset-dynamics data grounding.md>)
- [Ch. 15, Protections](<0015 Protections.md>)
- [Replaying recorded events (manual)](<../manual/0018 Replaying recorded events.md>)
- [`replay-event` in the CLI reference](<../manual/0019 CLI reference.md#replay-event>)
