# 20. Counterfactuals

A counterfactual replays a recorded grid event with something changed and reports what the frequency would have done:
a different system inertia, a device on the system, a device split across many units, or a grid whose synchronous
inertia has been scaled toward zero. Four verbs do it. `scenario` sweeps inertia, device and model corner. `fleet`
splits one device budget across N staggered units. `evidence` assembles the one-page dossier, with an avoided-cost
figure. `h0-study` removes real synchronous inertia while converters are made grid-forming. All four run on the same
swing model as [`replay-event`](<0018 Replaying recorded events.md>), on the same recorded events. `scenario`,
`fleet` and `h0-study` run only on an event whose no-device baseline is `Reproduced`, and `evidence` withholds every
counterfactual section otherwise. Read ch. 18 first, because the gate it defines decides
which events these verbs accept.

## Before you start

- **The gate.** Each verb first replays the event with no device and requires the verdict `Reproduced`. There is no
  flag that bypasses it. A baseline that does not reproduce reality gives a counterfactual nothing to stand on. Of the
  built-in events, `gb-2019-08-09` reproduces and is the grounded counterfactual baseline. Each verb takes
  `--trace <neso.csv>` as `replay-event` does; without it the gate is the headline gate, and with it the gate adds the
  RMSE and back-test checks of ch. 18, under which the 2019 event is `Marginal` (RMSE 0.099 Hz against the 0.05 Hz
  bar). `gb-largest-loss-1320` reproduces against placeholder figures and is for exploration: the verbs take it as a
  design-basis input, print an `input provenance: [CuratedPlaceholder]` line, add `provenance=CuratedPlaceholder` to the
  run description, and every figure they write inherits that tag. `gb-2023-12-22` is `Marginal`, so every verb refuses
  it (see [The gate](#the-gate)).
- **The device.** Every verb models a device as two separate terms: real rotational inertia, the stored energy
  `(1/2) J omega^2`, and fast frequency response, a power injection. They are never merged into one "equivalent
  inertia" number, and every output keeps them in separate fields.
- **The default device is a placeholder.** Without `--device`, `scenario` and `evidence` build a device named `tsgb` from
  30 GVA.s of inertia and 1.0 GW of fast response. `fleet` splits a budget of 40 GVA.s and 1.5 GW. Results on these
  defaults are illustrative. Supply your device's figures for anything you quote.
- **What stays in the system.** The recorded response services of the event (Dynamic Containment and the others, see
  ch. 18) are in every counterfactual. A device is evaluated on top of what the real system held. The recorded losses
  are replayed exactly. A recorded action of kind `DemandDisconnection`, such as the 892 MW of load disconnection on
  9 August 2019, is left out, because the device is meant to make that disconnection unnecessary. A recorded action of
  kind `OperatorInstruction`, such as the balancing-mechanism recovery of 22 December 2023, is kept, because the lost
  energy is replaced with or without a device.
- **Where output goes.** Without `--out`, a verb writes `results/<verb>-<id>` under the current directory. The
  examples pass `--out results/...` to name it.
- **Output text.** Money prints as `GBP <n>`. Elapsed times and the stamps in file headers differ by machine and run.

## Worked example

Sweep the 9 August 2019 event across the GB inertia trajectory with the default device:

```bash
gridsim scenario --event gb-2019-08-09 --out results/scenario-gb-2019-08-09
```

```
$ gridsim scenario --event gb-2019-08-09 --out results/scenario-gb-2019-08-09

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

At every inertia level from 260 GVA.s down to 50 GVA.s the recorded sequence drives the nominal no-device system to or
below the 48.8 Hz line where load disconnection starts, and the device holds it above. The device avoids LFDD at all
five levels. Each row is one nominal-corner pair of runs. The exit code is 0.

## Reading the output

**Header.** `baseline gate: Reproduced` is the gate verdict. `device: tsgb (30.0 GVA.s inertia + 1.00 GW FFR)` names
the device with its two terms side by side. `LFDD first-stage threshold: 48.8 Hz` is the nadir at or below which a
run is flagged `YES`. It is the first stage of GB low-frequency demand disconnection.

**The risk curve.** One row per inertia level, always at the nominal corner.

| Column | Meaning |
|---|---|
| `inertia` | The system inertia of the row in GVA.s, printed with a `GV` suffix. It replaces the event's recorded pre-event inertia. |
| `RoCoF no-dev`, `nadir`, `LFDD` | The replay of the event's losses with no device. RoCoF is the largest absolute rate over the window in Hz/s, the nadir is in Hz, and `LFDD` is `YES` when the nadir is at or below 48.8 Hz. |
| `RoCoF +dev`, `nadir`, `LFDD` | The same replay with the device. |
| `avoided` | `*** YES ***` when the no-device run reaches the LFDD line and the device run does not. Otherwise `-`. |

The last line, `device avoids LFDD at N/M inertia levels`, counts the `avoided` rows.

Each row replays the event's exogenous losses only. The recorded LFDD action is never replayed in a counterfactual, so
for an event with recorded load disconnection a no-device row can be deeper than the baseline replay of ch. 18. At 215 GVA.s the 2019 baseline bottoms at 48.84 Hz
with the recorded 892 MW shed in the sequence, while the no-device sweep row at the same inertia reaches 47.65 Hz
(see [the device-file run](#your-own-device)).

### Run directory

```
results/scenario-gb-2019-08-09/
|-- run-metadata.json              what ran, on which commit, backend and machine
|-- matrix.csv                     every cell of the sweep
|-- risk-curve.csv                 the paired curve behind the console table
|-- summary.md                     gate verdict, LFDD threshold and the risk curve as a table
`-- counterfactual-frequency.json  the full swing export of the hardest cell
```

`matrix.csv` has one row per inertia level, device (`none` and the device under test) and corner, so five levels, two
devices and two corners give 20 rows. After a block of `#` header lines (the run metadata) it starts like this:

```
# results/scenario-gb-2019-08-09/matrix.csv
inertia_gvas,device,corner,rocof_hz_s,nadir_hz,settle_hz,lfdd_at_risk,assessment
260,none,Best,-0.1407,48.418,48.417,True,"LFDD RISK  -  demand disconnection"
260,none,Worst,-0.1435,46.458,46.458,True,"LFDD RISK  -  demand disconnection"
260,tsgb,Best,-0.1238,49.363,49.363,False,"BELOW STATUTORY (49.5 Hz)"
260,tsgb,Worst,-0.1260,48.672,48.672,True,"LFDD RISK  -  demand disconnection"
155,none,Best,-0.2299,48.403,48.403,True,"LFDD RISK  -  demand disconnection"
```

The columns are `inertia_gvas`, `device`, `corner`, `rocof_hz_s`, `nadir_hz`, `settle_hz` (the frequency at the end of
the window), `lfdd_at_risk` and `assessment` (the frequency-limit classification of ch. 18). `risk-curve.csv` holds
the console table, with RoCoF at four decimals and nadir at three:

```
# results/scenario-gb-2019-08-09/risk-curve.csv
inertia_gvas,rocof_no_device,nadir_no_device,lfdd_no_device,rocof_with_device,nadir_with_device,lfdd_with_device,lfdd_avoided_by_device
260,-0.1422,47.690,True,-0.1251,49.088,False,True
155,-0.2341,47.618,True,-0.1905,49.048,False,True
120,-0.2983,47.607,True,-0.2307,49.039,False,True
102,-0.3473,47.605,True,-0.2589,49.036,False,True
50,-0.6595,47.603,True,-0.3983,49.032,False,True
```

Its columns are `inertia_gvas`, `rocof_no_device`, `nadir_no_device`, `lfdd_no_device`, `rocof_with_device`,
`nadir_with_device`, `lfdd_with_device` and `lfdd_avoided_by_device`. `counterfactual-frequency.json` (schema
`gridsim-frequency-export/1`) is the trajectory of the device at the lowest swept inertia, on the `Worst` corner when `--corners` lists it and
otherwise on the first listed corner, so a reviewer can hand-check the hardest number. It is labelled `Counterfactual` in its metadata.

### Exit codes

The four verbs share these.

| Code | Meaning |
|---|---|
| 0 | the verb ran. For `evidence`, the counterfactual was unlocked. For `h0-study`, a cell that reads `NaN` is data and still exits 0. |
| 1 | the event id is not in the catalogue, the catalogue could not be read, a `--trace` row that is not a data row, an output tag on an event part, or (`h0-study`) the case could not be loaded or has no converter fleet |
| 2 | no `--event`, an unknown flag, a malformed flag value, an unreadable `--device` file, a `--trace` file that does not exist, a `--voll-source` that is not a VoLL reference or a `--voll` that reference does not state (`evidence`), or an event rejected by the horizon guard |
| 3 | the gate failed: the baseline is `Marginal` or `Failed` |

## Variations

### The gate

A baseline short of `Reproduced` stops `scenario`, `fleet` and `h0-study` with `GATE FAILED` on standard error and exit
code 3. The 2023 event is `Marginal`:

```bash
gridsim scenario --event gb-2023-12-22 --out results/scenario-gb-2023-12-22
```

```
$ gridsim scenario --event gb-2023-12-22 --out results/scenario-gb-2023-12-22
GATE FAILED: Refusing to run counterfactuals: the no-device baseline is 'Marginal', not 'Reproduced'. Reproduce reality first. Marginal: RoCoF sim -0.123 vs rec -0.102 Hz/s (20 % err); nadir sim 48.98 vs rec 49.27 Hz (0.30 Hz err); RMSE n/a (no trace)
Fix the event's [VERIFY] figures (or tolerances) until 'replay-event' reports Reproduced, then re-run.
[exit 3]
```

`fleet` prints the same second line after its own first line:

```
$ gridsim fleet --event gb-2023-12-22 --out results/fleet-gb-2023-12-22
GATE FAILED: baseline verdict is Marginal, not Reproduced - Marginal: RoCoF sim -0.123 vs rec -0.102 Hz/s (20 % err); nadir sim 48.98 vs rec 49.27 Hz (0.30 Hz err); RMSE n/a (no trace)
Fix the event's [VERIFY] figures (or tolerances) until 'replay-event' reports Reproduced, then re-run.
[exit 3]
```

`evidence` does not stop. It always writes a report, withholds every counterfactual section, and exits 3 (see
[The evidence dossier](#the-evidence-dossier)). No run directory is written by a refused `scenario` or `fleet`. The
second line of the message tells you to fix the event's `[VERIFY]` figures. For an event with placeholder figures that
is the remedy. For `gb-2023-12-22`, whose figures are sourced, the verdict is a finding about the model, documented in
the [2023 dossier](../validation/evidence-gb-2023-12-22.md), and the figures are not to be edited to clear the gate.

### Your own device

Give a device as a JSON file (camelCase) or build one from flags. The file:

```json
{
  "name": "my-device",
  "rotationalInertiaMwS": 30000,
  "fastResponseMw": 1000,
  "fastResponseTimeConstantS": 0.5,
  "fastResponseDeadbandHz": 0.015
}
```

`rotationalInertiaMwS` is in MW.s (30,000 is 30 GVA.s), `fastResponseMw` in MW, the time constant in seconds and the
deadband in Hz. The last two default to the values shown when omitted. Other keys are ignored. The flag form is
`--device-inertia <GVA.s> --device-ffr <GW>`.

The repository ships `data/devices/tsgb-fleet.json`: 13.7 GVA.s, 1.14 GW and a 0.05 s time constant, the proposed
123-unit fleet of the TSGB corpus. Its own `_provenance` block records that every figure is a design or aspirational
number, so a result on it is a counterfactual about a proposed device and never a measurement. A run with it, at three
inertia levels and all three corners:

```bash
gridsim scenario --event gb-2019-08-09 --device data/devices/tsgb-fleet.json --inertia 215,120,50 --corners best,nominal,worst --out results/scenario-tsgb-fleet
```

```
$ gridsim scenario --event gb-2019-08-09 --device data/devices/tsgb-fleet.json --inertia 215,120,50 --corners best,nominal,worst --out results/scenario-tsgb-fleet

=== Counterfactual scenario: GB 9 Aug 2019 disturbance (Hornsea + Little Barford) ===
  baseline gate: Reproduced   device: tsgb-fleet-1.14GW (13.7 GVA.s inertia + 1.14 GW FFR)
  LFDD first-stage threshold: 48.8 Hz  (nominal corner)

   inertia | RoCoF no-dev    nadir  LFDD |  RoCoF +dev    nadir  LFDD | avoided
  ------------------------------------------------------------------------------------
     215GV |     -0.171Hz/s  47.65Hz   YES |    -0.149Hz/s  49.26Hz    no | *** YES ***
     120GV |     -0.298Hz/s  47.61Hz   YES |    -0.238Hz/s  49.24Hz    no | *** YES ***
      50GV |     -0.660Hz/s  47.60Hz   YES |    -0.420Hz/s  49.23Hz    no | *** YES ***

  device avoids LFDD at 3/3 inertia levels.

  -> results\scenario-tsgb-fleet
[exit 0]
```

The device name in the header is the `name` from the file. The risk curve is nominal-corner whatever `--corners` says,
so the console table is the same for `--corners best,worst`; the corners change only the rows of `matrix.csv`, which
for this run has 18 rows (three levels, two devices, three corners).

### Corners

A corner is the model's own error bar. The model has three constants that are documented but not fitted to a record:
load damping, the responsive fraction of generation and the governor time constant. `Best` and `Worst` scale them in
opposite directions, and `Nominal` leaves them as they are.

| Corner | Load damping | Responsive fraction | Governor time constant |
|---|---|---|---|
| `Best` | x 1.5 | x 1.5, capped at 1.0 | x 0.7 |
| `Nominal` | x 1.0 | x 1.0 | x 1.0 |
| `Worst` | x 0.6 | x 0.6 | x 1.4 |

The multipliers are engineering brackets. They are not fitted to any event,
and they are deliberately asymmetric, because a security study must not understate a slow or weak response. The
brackets are wide. At 260 GVA.s the no-device nadir is 48.418 Hz at `Best` and 46.458 Hz at `Worst` (see the
`matrix.csv` excerpt above), while the nominal risk-curve row is 47.69 Hz.

Corners do not change the RoCoF of the first step, which is set by the inertia and the loss alone. The RoCoF these
verbs print is the largest rate over the whole window, so for a staged event it can fall on a later step where damping
and governor response have acted, and then it differs between corners. At 260 GVA.s the 2019 no-device RoCoF is
-0.1407 Hz/s at `Best` and -0.1435 Hz/s at `Worst`. For a single-step loss it is identical across corners.

`scenario` takes `--corners best,worst` by default. `h0-study` takes the same flag. `fleet` and `evidence` run at the
nominal corner and have no corners flag.

### A fleet of devices

`fleet` answers a question about one device budget: does it matter whether the budget is one aggregate unit or many
units? The same total inertia and fast response are split into N identical units. Each unit arms when the system
frequency leaves its own deadband and then responds after a latency that is spread evenly from 0 to `--stagger` seconds
across the units. One unit has zero stagger. The intrinsic inertia of the units adds, because stored energy adds. The
fast response does not add at the moment it counts, because a unit that is still waiting to arm delivers nothing.

```bash
gridsim fleet --event gb-largest-loss-1320 --units 1,10,100 --inertia 60 --out results/fleet-gb-largest-loss-1320
```

```
$ gridsim fleet --event gb-largest-loss-1320 --units 1,10,100 --inertia 60 --out results/fleet-gb-largest-loss-1320

=== Fleet emergent behaviour: GB normal infeed loss risk screening (SQSS): single 1320 MW infeed trip [VERIFY] ===
  input provenance: [CuratedPlaceholder]  (event 'gb-largest-loss-1320' is effectively CuratedPlaceholder, the weakest of its parts): a design-basis study, not a reproduction of history; every figure below inherits it and none is Recorded.
  system inertia: 60 GVA.s   device budget: 40 GVA.s inertia + 1.50 GW FFR   stagger: 10.00 s
  (same TOTAL budget split N ways - intrinsic inertia N-additive, FFR non-additive)

   units |       RoCoF |    nadir |  FFR @ nadir |   peak FFR | LFDD
  --------------------------------------------------------------------------
       1 |   -0.330Hz/s |  49.64Hz |      1008MW |    1050MW | no
      10 |   -0.330Hz/s |  49.30Hz |       544MW |     879MW | no
     100 |   -0.330Hz/s |  49.28Hz |       516MW |     906MW | no

  RoCoF the same across unit counts (-0.330 vs -0.330 Hz/s): intrinsic inertia is N-additive.
  At the nadir the 100-unit staggered fleet has 516 MW online vs 1008 MW for 1 aggregate unit (51 %): FFR is non-additive when it counts.
  Consequence: fleet nadir 49.28 Hz vs 1-unit 49.64 Hz (0.36 Hz deeper).

  -> results\fleet-gb-largest-loss-1320
[exit 0]
```

| Column | Meaning |
|---|---|
| `units` | The unit count. The first count in `--units` is the comparison baseline. |
| `RoCoF` | The largest absolute RoCoF over the window, in Hz/s. |
| `nadir` | The frequency minimum in Hz. |
| `FFR @ nadir` | The fast response the fleet is delivering at the instant of the nadir, in MW. This is the decision-relevant figure. |
| `peak FFR` | The largest fast response the fleet delivers at any instant, in MW. |
| `LFDD` | `YES` when the nadir is at or below 48.8 Hz. |

The header line gives the system inertia (`--inertia`, which defaults to the event's pre-event inertia; here 60), the
device budget and the stagger. Here the total response is 1,500 MW, but at the nadir the aggregate unit delivers 1,008
MW and the 100-unit fleet 516 MW, and the nadir is 0.36 Hz deeper (49.28 against 49.64 Hz). The RoCoF is identical
across unit counts because this event is a single step. The three lines under the table compare the first and last
rows at the precision the table prints, so the words always agree with the digits beside them: the RoCoF is the same
or differs (and why), the fast response at the nadir is lower for the fleet or the same, and the nadir is deeper,
shallower or the same. The default 2019 event shows the other branches:

```
$ gridsim fleet --event gb-2019-08-09 --out results/fleet-gb-2019-08-09

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

The 100-unit fleet delivers 1,411 MW at the nadir, the same as the aggregate unit: every unit is already delivering at
the nadir, so the staggering has played out by then and the two nadirs are equal (49.53 Hz). The RoCoF differs
(-0.139 and -0.145 Hz/s), because the 2019 sequence has later loss steps and the staggered fleet is delivering less
response when they land; the inertia total is the same in both rows. With `--stagger 0` the two RoCoF values are equal,
so the difference comes from the stagger. The non-additivity is a property of the event as well as the device, which is the reason to run the
comparison on the event you care about.

`fleet.csv` in the run directory holds the table:

```
# results/fleet-gb-largest-loss-1320/fleet.csv
units,rocof_hz_s,nadir_hz,ffr_at_nadir_mw,peak_fleet_ffr_mw,lfdd_at_risk
1,-0.3300,49.640,1008.4,1049.8,False
10,-0.3300,49.302,543.8,878.5,False
100,-0.3300,49.283,515.7,905.7,False
```

The run directory also holds `run-metadata.json`. `fleet` writes no `summary.md` and no JSON export. The columns are
`units`, `rocof_hz_s`, `nadir_hz`, `ffr_at_nadir_mw`, `peak_fleet_ffr_mw` and `lfdd_at_risk`.

`fleet` takes the global `--forming`, `--forming-inertia` and `--forming-damping` switches: an aggregate virtual former
over the real inertia, with the units adding real inertia and fast response as before. The header then gains a
`grid-forming:` line naming the former, its virtual inertia and its damping, and the run description records them.
Without `--forming` the output is unchanged.

### The H=0 study

`h0-study` asks how a recorded loss would have played out if the real synchronous inertia of the grid fell toward
zero while a growing fraction of the converter fleet became grid-forming. It runs on a case directory that ships a
converter fleet (default `data/gb-synthetic`) and routes each cell through the per-asset dynamics, so grid-following
converters can trip into a Loss-of-Mains cascade. It uses the case's own inertia, scaled by the synchronous-inertia scale, the rows of
the table (the case `gb-synthetic` carries 171.7 GVA.s at scale 1.00), and the event's losses and recorded services. The gate still
validates the event first, on the event's own inertia.

```bash
gridsim h0-study --event gb-2019-08-09 --out results/h0-study-gb-2019-08-09
```

```
$ gridsim h0-study --event gb-2019-08-09 --out results/h0-study-gb-2019-08-09

=== H=0 counterfactual: GB 9 Aug 2019 disturbance (Hornsea + Little Barford) ===
  baseline gate: Reproduced   dP=1521 MW (first 5 s; 2768 MW over the whole event)   LFDD stage: 48.8 Hz
  GFM class: gfm-vsm-study-2024   GFL class: gfl-wind-2024   (Ev is virtual control inertia, NEVER in real E)
  GFM fraction realised by whole units: 0.25 -> 0.392, 0.50 -> 0.588, 0.75 -> 0.833

  corner Best: cell = nadir Hz / verdict  (HELD=formed & clear of LFDD, LFDD=below stage, CASC=Loss-of-Mains cascade, NaN=un-formable)
   scale\gfm |       0.00       0.25       0.50       0.75       1.00
  -------------------------------------------------------------------
        1.00 | 49.59 HELD 49.10 HELD 48.80 CASC 48.80 CASC 49.06 HELD
        0.50 | 49.57 HELD 49.10 HELD 48.80 CASC 48.80 CASC 49.06 HELD
        0.25 | 38.46 CASC 49.10 HELD 48.80 CASC 48.80 CASC 49.06 HELD
        0.00 |   -    NaN 42.62 CASC 44.65 CASC 47.07 CASC 49.06 HELD
...
  11/40 cells HELD (formed, clear of LFDD); 2 forming-dependent (H=0, no grid-forming source).
  Note: forming-dependent cells are expected DATA, not an error - the run still exits 0.

  -> results\h0-study-gb-2019-08-09
[exit 0]
```

Each cell is a nadir in Hz and a verdict. `HELD` means a frequency was formed and stayed above the LFDD line with no
cascade. `LFDD` means the nadir reached 48.8 Hz. `CASC` means grid-following converters tripped into a Loss-of-Mains
cascade. `NaN` means nothing forms the frequency, which is real synchronous inertia of zero and no grid-forming source,
so no RoCoF or nadir exists. Rows are the synchronous-inertia scale and columns are the grid-forming fraction. A
`NaN` cell is expected data, and the run exits 0. The virtual inertia of the grid-forming fleet is a control quantity.
It is held in its own column and is never added to the real inertia. The full table, the file layout (`h0-study.json`,
`matrix.csv`, `summary.md`, `run-metadata.json`) and the physics are in [ch. 14](<0014 Frequency studies.md>).
The `--corners` flag works as for `scenario`.

## The evidence dossier

`evidence` puts the whole argument on one page: what was recorded, what the model reproduces with no device, what it
says a device would have done, the inertia risk curve, the single-versus-fleet comparison and the avoided cost. Run it
on the 2019 event:

```bash
gridsim evidence --event gb-2019-08-09 --out results/evidence-gb-2019-08-09
```

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

**Header.** The event time, the event's effective provenance (the weakest tag of its parts, see ch. 18) and the
baseline gate verdict. An event below `DerivedFromRecorded` adds the `input provenance:` line.

**Columns.** Three rows, each with its RoCoF, nadir, an LFDD flag, the device's inertia and fast response in separate
fields, and a provenance tag in brackets.

| Row | What it is |
|---|---|
| `recorded` | The recorded outcome of the event. Its `LFDD` flag is `YES` when the recorded nadir is at or below 48.8 Hz. |
| `simulated (no device)` | The baseline replay of ch. 18, including the recorded LFDD action. Tag `DerivedFromRecorded`, or the event's weaker effective tag. |
| `counterfactual (device @ N GVA.s)` | The device at `--cf-inertia`, which defaults to the event's pre-event inertia, on the exogenous losses only, at the nominal corner. Tag `Counterfactual`. Present only when the gate passes. |

For this event the counterfactual column reads -0.147 Hz/s and 49.07 Hz against the baseline's -0.171 Hz/s and 48.84 Hz.
The `simulated` row reads `LFDD no` although the recorded row reads `YES`. The baseline nadir of 48.84 Hz sits four
hundredths above the 48.8 Hz line that the record crossed, and 0.04 Hz is the gate's measured nadir accuracy. It is
not a claim that load disconnection would not have happened. The baseline replays the recorded 892 MW disconnection;
the counterfactual does not, which is why the no-device sweep rows of `scenario` are deeper than this row.

The RoCoF in the `simulated` and `counterfactual` columns is on the recorded figure's basis, the same figure the gate
scores: the 20 ms maximum for an instrument record, and the 1-second median-over-phase slope for a windowed record such
as the 2023 event (-0.123 Hz/s, the verdict line's figure).

**Single-versus-fleet line.** The device split into `--units` units (default 100) over `--stagger` seconds (default 10)
against one aggregate unit, at the counterfactual inertia, with the nadir and the fast response at the nadir of each.
It uses the device's inertia and fast-response budget with the device's own `fastResponseTimeConstantS` and
`fastResponseDeadbandHz`, not the fleet verb's 40 GVA.s and 1.5 GW budget. On this event both nadirs are 49.07 Hz
and both deliver 1,000 MW at the nadir, which is the late-nadir case described above.

**Avoided cost.** See [Avoided cost, VoLL and provenance](#avoided-cost-voll-and-provenance).

**Summary line.** One sentence that states the verdict, whether the device keeps the nadir above the LFDD line, and the
avoided cost or `no avoided cost claimed`. When the record gives its nadir time and the baseline misses it by more than
2.0 s, the verdict carries the `[nadir time NOT reproduced: ...]` statement of ch. 18.

### Run directory

```
results/evidence-gb-2019-08-09/
|-- run-metadata.json   what ran
|-- evidence.json       the full report, schema gridsim-evidence/1
`-- summary.md          the same as a markdown table, with the gate verdict
```

`evidence.json` wraps the report as `{ "schema": "gridsim-evidence/1", "report": { ... } }`. The report holds
`eventId`, `eventTitle`, `whenUtc`, `eventProvenance`, `baselineVerdict`, `counterfactualUnlocked`, the `columns`
(each with `label`, `provenance`, `rocofHzPerS`, `nadirHz`, `settleHz`, `lfddAtRisk`, `deviceRotationalInertiaGvaS` and
`deviceFfrGw`), the `riskCurve` (one point per inertia level with the paired figures of `risk-curve.csv`), `fleet`,
`avoidedCost` and `summary`. The last part of the file for the run above:

```
# results/evidence-gb-2019-08-09/evidence.json
    "fleet": {
      "inertiaGvaS": 215,
      "units": 100,
      "staggerSeconds": 10,
      "aggregateNadirHz": 49.06951716271372,
      "fleetNadirHz": 49.06953264157351,
      "aggregateFfrAtNadirMw": 999.9999999999986,
      "fleetFfrAtNadirMw": 999.9999999999993
    },
    "avoidedCost": {
      "lfddMwShedAvoided": 892,
      "vollPerMwh": 6000,
      "durationHours": 0.5,
      "avoidedCostGbp": 2676000,
      "provenance": "CuratedPlaceholder",
      "basis": "recorded LFDD energy avoided (MW shed x duration h) x VoLL (GBP/MWh) - grounded in the record, not a marketing figure",
      "vollProvenance": "CuratedPlaceholder",
      "durationProvenance": "CuratedPlaceholder"
    },
    "summary": "Baseline Reproduced; counterfactual device DOES keep the nadir above LFDD; avoided cost GBP 2,676,000 (CuratedPlaceholder)"
  }
}
```

The field layout is in [ch. 31](<0031 Exports and formats.md>). When the gate fails the file has no counterfactual
column, no `riskCurve`, no `fleet` and no `avoidedCost`, so it cannot carry an ungated claim. `summary.md` repeats the
column table and the summary line.

### When the gate fails

```bash
gridsim evidence --event gb-2023-12-22 --out results/evidence-gb-2023-12-22
```

```
$ gridsim evidence --event gb-2023-12-22 --out results/evidence-gb-2023-12-22

=== Evidence dossier: GB 22 Dec 2023 excursion (IFA + Cottam, settlement-measured) ===
  when: 2023-12-22T13:09:51.0000000Z   event provenance: DerivedFromRecorded   baseline gate: Marginal

  column                                          RoCoF    nadir  LFDD   dev inertia  dev FFR
  --------------------------------------------------------------------------------------------
  recorded                                    -0.102Hz/s  49.27Hz    no        0.0GVAs  0.00GW   [Recorded]
  simulated (no device)                       -0.123Hz/s  48.98Hz    no        0.0GVAs  0.00GW   [DerivedFromRecorded]

  Baseline Marginal - counterfactual withheld. Reproduce the record first ([VERIFY] the event figures/tolerances).

  -> results\evidence-gb-2023-12-22
[exit 3]
```

The recorded and simulated rows still print, so the reader sees how far the baseline is from the record. The
counterfactual, fleet and cost sections are absent, the summary says so, and the exit code is 3.

## Avoided cost, VoLL and provenance

**What it is.** The avoided cost is the cost of the load that the recorded event disconnected, valued at the value of
lost load (VoLL) over an assumed outage duration:

```
avoided cost (GBP) = max(0, recorded LFDD MW shed) x duration (h) x VoLL (GBP/MWh)
```

For the 2019 event, 892 MW x 0.50 h x GBP 6,000/MWh = GBP 2,676,000. The shed figure comes from the event's recorded
outcome, not from the simulation. The cost is claimed only when both conditions hold: the counterfactual device keeps
the nadir above 48.8 Hz, and the record shows load actually shed (`lfddMwShed` above zero). Otherwise the dossier says
`no avoided cost claimed`. An event with no recorded shed never produces a cost:

```
$ gridsim evidence --event gb-largest-loss-1320 --out results/evidence-gb-largest-loss-1320

=== Evidence dossier: GB normal infeed loss risk screening (SQSS): single 1320 MW infeed trip [VERIFY] ===
  when: 2020-01-01T12:00:00.0000000Z   event provenance: CuratedPlaceholder   baseline gate: Reproduced
  input provenance: [CuratedPlaceholder]  (event 'gb-largest-loss-1320' is effectively CuratedPlaceholder, the weakest of its parts): a design-basis study, not a reproduction of history; every figure below inherits it and none is Recorded.

  column                                          RoCoF    nadir  LFDD   dev inertia  dev FFR
  --------------------------------------------------------------------------------------------
  recorded                                    -0.165Hz/s  49.20Hz    no        0.0GVAs  0.00GW   [CuratedPlaceholder]
  simulated (no device)                       -0.165Hz/s  49.24Hz    no        0.0GVAs  0.00GW   [CuratedPlaceholder]
  counterfactual (device @ 200 GVA.s)         -0.143Hz/s  49.62Hz    no       30.0GVAs  1.00GW   [Counterfactual]

  single-vs-fleet @ 200 GVA.s: aggregate nadir 49.62 Hz vs 100-unit fleet 49.48 Hz (FFR@nadir 751 vs 568 MW).

  Baseline Reproduced; counterfactual device DOES keep the nadir above LFDD; no avoided cost claimed
...
```

The claim is what the record cost, and that a device would have kept the nadir clear of the threshold that caused it.
It is not a forecast. The nadir test is a nominal-corner test on one counterfactual column. At 260 GVA.s the same
default device on the `Worst` corner reaches 48.672 Hz and `lfdd_at_risk` is `True` (the `matrix.csv` excerpt of the
worked example), so the avoided-cost claim holds for the nominal model and not for every corner.

**VoLL is a policy number, not physics.** The default is GBP 6,000/MWh, the administrative cash-out VoLL from
1 November 2018 (Ofgem's BSC P305 decision, reference S324). Set `--voll` to the figure you intend to quote, and
`--duration` (default 0.5 h) to the restoration time you assume. `--voll-source` names the reference that states the
figure, and is checked: it must be one of the references in `docs/reference/voll` that state a GB VoLL, and `--voll`
must equal a figure that reference states. Anything else exits 2 naming what the reference does state.

| `--voll-source` | Reference | Figures (GBP/MWh) |
|---|---|---|
| `S324` | Ofgem BSC P305 decision (2 April 2015) | 3,000 (from 5 November 2015), 6,000 (from 1 November 2018) |
| `S322` | London Economics, The Value of Lost Load for Electricity in GB (July 2013) | 16,940 (headline weighted average, domestic and SME, peak winter workday) |
| `S323` | DECC EMR consultation annex C, reliability standard methodology (July 2013) | 16,940 (the London Economics figure) |

A run with the higher figure and its source:

```bash
gridsim evidence --event gb-2019-08-09 --device data/devices/tsgb-fleet.json --voll 16940 --voll-source S322 --out results/evidence-gb-2019-08-09-cited
```

The avoided-cost block then shows the cost at 16,940 GBP/MWh, a line `VoLL [CuratedPlaceholder]   duration
[CuratedPlaceholder]` with the tag of each input, and `VoLL source: S322, <title>: <where>`. A citation records where
the figure is stated; it does not make the figure a record of the event, so the tag stays `CuratedPlaceholder`.

**What the tags mean.** Every row and figure in these outputs carries one tag. Each means one thing.

| Tag | Meaning here |
|---|---|
| `Recorded` | Taken directly from an archived record: the recorded outcome of a sourced event such as 2019. |
| `DerivedFromRecorded` | Computed from recorded data. The `simulated (no device)` column. |
| `CuratedPlaceholder` | Stands in for data that has not been sourced, and is always accompanied by `[VERIFY]` in the text. The VoLL and the duration of the avoided cost (cited or not), and every figure of `gb-largest-loss-1320`, including its `recorded` row. |
| `Counterfactual` | A result of what a device would have done. It is never an input to another replay. |

The tag on the avoided-cost line is the weakest of the VoLL's tag, the duration's tag and the event's effective tag,
and the line under it shows the VoLL and duration tags separately. Both inputs are policy figures, so the cost is
always `CuratedPlaceholder`: quote it with its VoLL source and the duration you assumed.

## Pitfalls

- **`GATE FAILED` and exit 3.** The baseline of the event is `Marginal` or `Failed`. Run `gridsim replay-event <id>` to
  see why. No counterfactual verb accepts the event, and `--device` or any other flag does not change that.
- **A flag for another verb exits 2.** Each counterfactual verb accepts only its own flags (listed in
  [ch. 19](<0019 CLI reference.md#scenario>)): `fleet --device-ffr 5` prints
  `error: fleet does not use --device-ffr (it belongs to another verb, so it would be silently ignored). fleet accepts: ...`,
  because `fleet` reads `--total-ffr`; `evidence --corners best` is refused because `evidence` runs at the nominal
  corner. `--forming` is refused by every verb here except `scenario` and `fleet`, and `--asset-dynamics` by all of
  them.
  `--device <file>` together with `--device-inertia` or `--device-ffr` exits 2, because the file would win.
- **`--units` is a list for `fleet` and a single count for `evidence`.** `fleet --units 1,10,100` compares three
  cases. `evidence --units 100` sets the one fleet in the dossier.
- **The risk-curve and avoided-cost flags are nominal-corner results.** Look at the `Worst` rows of `matrix.csv` before
  you rely on a device at a given inertia.
- **The avoided cost is always `CuratedPlaceholder`.** Quote it with the `--voll-source` reference and the duration
  you assumed.
- **`--voll-source` takes a reference id, not free text.** `S322`, `S323` or `S324`, with a `--voll` that reference
  states; anything else exits 2.
- **A `--device` file that is missing or malformed exits 2**, with `error: --device '<path>': <reason>`.
- **A mistyped list or number exits 2** with a message naming the flag, for example `error: --inertia/list expected a
  comma-separated list of numbers but got 'abc'`.
- **The default `--out` is under `results/` in the current directory.** Pass `--out` for any run you want to keep.

## Limits

- A counterfactual replays every recorded loss at its recorded time. Losses that were themselves consequences of the
  frequency fall, such as distributed-generation trips on RoCoF and at 49 Hz, are not removed when a device holds the
  frequency up, so a device cannot avoid them in `scenario`, `fleet` or `evidence`. `h0-study` models converter trips
  per asset.
- The counterfactual verbs run only on `gb-2019-08-09` and on the placeholder screening event among the built-ins. The
  2023 event is `Marginal` and locked.
- The avoided-cost test and the risk curve use the nominal corner only. `evidence` has no `--corners`.
- The model is a single-mass swing equation. It has no network, no regional frequency and no per-unit protection
  except in `h0-study`.
- `--inertia-model distributed` is not honoured by these verbs. They print a warning and run the lumped model.

## See also

- [ch. 18, Replaying recorded events](<0018 Replaying recorded events.md>) - the gate, the verdicts and the event catalogue
- [ch. 14, Frequency studies](<0014 Frequency studies.md>) - the swing model, the GB limits and the full H=0 study
- [ch. 19, CLI reference](<0019 CLI reference.md#scenario>) - every `scenario` flag
- [ch. 19, CLI reference](<0019 CLI reference.md#fleet>) - every `fleet` flag
- [ch. 19, CLI reference](<0019 CLI reference.md#evidence>) - every `evidence` flag
- [ch. 19, CLI reference](<0019 CLI reference.md#h0-study>) - every `h0-study` flag
- [ch. 31, Exports and formats](<0031 Exports and formats.md>) - `evidence.json`, the frequency export and the CSV layouts
- [Evidence engine](<../technical/0007 The evidence engine.md>) - the scenario, fleet and evidence engines as implemented
- [Provenance and invariants](<../technical/0008 Provenance and invariants.md>) - the provenance tags in full
- [9 August 2019 dossier](../validation/evidence-gb-2019-08-09.md) - the sourced event, the TSGB fleet counterfactual and its avoided cost
- [22 December 2023 dossier](../validation/evidence-gb-2023-12-22.md) - why the 2023 baseline is `Marginal`
