# The 9 August 2019 GB Disturbance - an Estimated-State Dossier

*GridSim + GDA evidence artefact, assembled 2026-07-14. Every figure carries its
source inline; nothing here is a placeholder.*

---

## 1. What this document is

On 9 August 2019 at 15:52:33 UTC a lightning strike on the Eaton Socon-Wymondley
400 kV circuit triggered the near-simultaneous loss of Hornsea 1 (737 MW) and the
Little Barford steam unit (244 MW), a cascade of embedded-generation
loss-of-mains tripping, and - a minute later, after two further gas-turbine trips
- the first stage of Low Frequency Demand Disconnection: 892 MW of net demand,
1.15 million customers. It is the event GB frequency-security policy has argued
about ever since, and it is GridSim's **credibility-gate target**: the recorded
event the simulator must reproduce with no device attached before any
counterfactual it produces is worth reading.

This dossier records the point at which that gate closed with **sourced data end
to end**: the loss timeline from the public record, the operating point from the
measurement apparatus (NESO's recorded inertia outturn and the WLS state
estimator's demand), the estimator's own view of the event sites reconstructed
from settlement metering, and the replay verdict - **REPRODUCED**.

Primary source for the record: Ofgem, *"9 August 2019 power outage report"*
(January 2020), paragraphs 2.4.1-2.4.16. Measurement sources: the GDA data lake
(`Derived/inertia_all_srcs`, `Derived/estimated_state`, `Derived/demand_all_srcs`),
built exclusively from archived public telemetry (Elexon B1610 settlement
metering, interconnector settlement flows, NESO operational data).

## 2. The recorded event

The timeline as the Ofgem report states it (all times relative to the 15:52:33 UTC
fault; BST in the report is 16:52:33):

| t | Event | MW | Ofgem para |
|---|---|---:|---|
| 0 s | Lightning strike, Eaton Socon-Wymondley 400 kV; cleared in 80 ms | - | 2.4.1 |
| ~0 s | Distributed generation vector-shift protection trips | -150 | 2.4.2 |
| <1 s | Hornsea 1 voltage-control instability; deloads 799 -> 62 MW | -737 | 2.4.3 |
| ~1 s | Little Barford steam turbine trips (speed-sensor discrepancy) | -244 | 2.4.4 |
| seconds | Distributed generation on legacy 0.125 Hz/s RoCoF protection trips | -350 to -430 | 2.4.6 |
| 25 s | Frequency arrested at 49.1 Hz; recovers, plateaus 49.2 Hz at 45 s | - | 2.4.8 |
| ~30-60 s | Unattributed net reduction | -100 | 2.4.9 |
| ~60 s | Little Barford GT1a shut down (steam pressure) | -210 | 2.4.10 |
| ~65 s | Distributed generation trips at 49 Hz | -200 | 2.4.11 |
| ~76 s | Frequency below 48.8 Hz -> **LFDD stage 1**: 892 MW net demand shed | +892 | 2.4.12 |
| ~76 s | Additional distributed generation lost at the LFDD instant (inferred: only 350 MW of the 892 reached the transmission system) | -550 | 2.4.13 |
| ~90 s | Little Barford GT1b manually tripped | -187 | 2.4.15 |
| 5 min | 50 Hz restored; all supplies restored within 45 min | - | 2.4.16 |

Cumulative generation loss >=1,990 MW (Ofgem 2.4.11) before LFDD; the catalogue's
nine steps total 2,768 MW including the LFDD-instant DG loss and GT1b.

Two figures in GridSim's earlier placeholder version of this event were wrong in
instructive ways, and the record corrects them: the Little Barford **steam unit
was 244 MW** (the 641 MW figure was the station's whole CCGT block), and the
event **played out over ~90 seconds**, not 2.5 - the placeholder compressed the
timeline, which is precisely why it failed the replay gate (33% RoCoF error).

## 3. The measured operating point

The two scalars that set the physics of the frequency excursion - how much
spinning energy resisted the fall, and how much demand damped it - come from
measurement, not curation:

- **Pre-event inertia: 215 GVA.s** - NESO's recorded outturn system inertia for
  the half-hour containing the fault (GDA `Derived/inertia_all_srcs`,
  `source=neso_inertia`, 2019-08-09 15:30 UTC). Provenance: `Recorded`.
- **Pre-event demand: 21,147 MW** - the WLS state estimator's transmission-net
  national demand for the same settlement period, the sum of negative bus
  injections in the estimated state (GDA `Derived/estimated_state/gb-spine`).
  Provenance: `DerivedFromRecorded`. Recorded gross national demand for the
  period was **28,112 MW** (`Derived/demand_all_srcs`, `source=neso_demand`);
  the ~7 GW difference is embedded generation netted behind the GSPs on a
  bright August afternoon - visible, not hidden.

These two numbers close the loop with the record through the swing equation. The
losses inside the RoCoF measurement window (~1,521 MW: vector-shift DG + Hornsea
+ Little Barford ST + RoCoF-protection DG) against the recorded -0.17 Hz/s imply
**223.7 GVA.s** of stored energy; NESO's independently recorded outturn is
**215 GVA.s**. Two sources, one physics relation, 4% apart.

## 4. The estimator sees the event in the metering

The estimated-state lake reconstructs the grid's operating point for every
settlement period of the day from B1610 settlement metering and interconnector
flows - **46/46 periods converged, 46/46 chi-square-consistent, zero bad-data
removals**, through the most violent disturbance in a decade. That clean sheet
was earned, not assumed: two earlier builds of this day failed in instructive
ways the limitation note below records. At the two event sites (estimated bus
P injections, MW):

| Period (UTC start) | Hornsea 1 @ Grimsby West (GRIW_1) | Little Barford @ Eaton Socon (EASO_1) |
|---|---:|---:|
| 15:00 (pre-event) | **1,566** | **609** |
| 15:30 (onset 15:52) | 1,372 | 452 |
| 16:00 (post-event) | **755** | **-77** |

The period-average arithmetic corroborates the recorded trip timing. Hornsea's
737 MW deload 8 minutes before the 15:30-16:00 period's end should depress that
period's average by ~ (8/30)*737 ~ 197 MW; the estimate shows -194 MW
(1,566 -> 1,372). Little Barford's post-event row is the sharper signature: the
site flips from +609 MW injection to **-77 MW net offtake** - a tripped station
drawing auxiliary load - reconstructed purely from settlement metering by an
estimator that knows nothing about the event.

The Eaton Socon attribution itself is a product of this work: the BMU crosswalk
had bound Little Barford to *Pembroke* (a lead-party name-match on "RWE
Generation UK Plc" - a crosswalk defect, since fixed), so before the fix the 641 MW
signature sat in South Wales. The same fix family re-sited Keadby, Staythorpe,
Grain, Didcot (lead-party fallback removed) and Beatrice/Moray East (offshore
farms bound by cable landing, not farm centroid - since fixed).

**ETYS boundary transfers at the estimated pre-event state** (from
`boundary_json`, limits are NESO 24-months-ahead constraint limits - anachronistic
for 2019, used as scale reference): B4 SHETL-SPT +1,000 MW, B6 SCOTEX +758 MW,
B7 +79 MW, B8 FLOWSTH +5,230 MW, SEIMP -3,040 MW - a ~0.8 GW Scottish export, a
loaded North-to-Midlands corridor and a heavy South East import (the
interconnector flows now measured explicitly), all inside the reference limits.

**Two defects this day found, and their fixes** (both found by the estimator itself,
both resolved): in the first build, the post-event periods' bad-data pass
removed the Keith P injection and the under-constrained north drifted to a B4
transfer of +5.9 GW - **with a passing chi-square**. The observability-restoration
work (hardening plan A-6, `--backfill-removals`) anchored removals to the
solved prior, which exposed the second, deeper defect honestly: the post-event
periods then *failed* chi-square outright. The root cause of that residual
inconsistency was the model, not the data - the cases carried **none of the
~23 GVAr of switched shunt compensation** the real network operates (Hutton
hosts a 225 MVAr SVC, Harker 1.4 GVAr of switchable plant - exactly the buses
whose reactive pseudos the estimator kept rejecting all week). Ingesting the
ETYS Appendix B compensation tables and carrying each device range as declared
Q-pseudo uncertainty (a switched device's state is unobservable from
settlement data) made the whole day close: 46/46 chi-square PASS, zero removals,
post-event boundary transfers physical (B4 ~ +1.2 GW). The estimator found
both defects itself - the outlier league and the runaway are what a
measurement-conditioned gate is *for*.

## 5. The replay verdict

`replay-event gb-2019-08-09` (built-in, sourced) and
`replay-event gb-2019-08-09 --catalog data/events/gb-2019-08-09-estimated.json`
(the measured catalogue emitted by GDA `NetworkModel/build_event_catalog.py`) both report:

```
pre-event inertia: 215.0 GVA.s   demand: 21.1 GW   total loss: 2768 MW
inferred inertia from recorded RoCoF: 223.7 GVA.s (cross-check vs 215.0)
RoCoF:  sim  -0.171   recorded  -0.170 Hz/s   (1 % error)
Nadir:  sim   48.84   recorded   48.80 Hz   (0.04 Hz error)
Nadir envelope (declared-uncertainty corners): [48.20, 49.17] Hz - recorded INSIDE (consistent within the model's own error bars)
VERDICT: REPRODUCED  - baseline reproduces the record; counterfactuals unlocked.
```

Tolerances: +/-15% RoCoF, +/-0.15 Hz nadir (`ReplayTolerance.Default`). The sim
window extends automatically to cover the full 90 s recorded sequence. For
contrast, both earlier placeholder variants **FAILED** the same gate (27-33%
RoCoF error, 0.40-0.46 Hz nadir error): the gate correctly rejected the
compressed placeholder timeline and correctly accepted the sourced one. Nothing
was tuned in between - the swing model, tolerances and verdict logic are
unchanged; only the inputs went from curated to sourced.

## 6. Why this matters

The chain now reads, in full: **public settlement metering -> bounded lake
ingestion (horizon-guarded, 7 d + 1 h) -> BMU->GSP crosswalk -> per-GSP measurement
sets -> WLS state estimation (chi-square-gated, cross-validated to machine epsilon against
pandapower) -> a measured operating point -> a sourced event record -> a reproduced
baseline -> unlocked counterfactuals.** Every arrow is code in this repository, every
figure carries a provenance tag, and the one step that used to be curation
(the operating point) is now measurement.

The counterfactual apparatus this unlocks - `scenario --event gb-2019-08-09`
(inertia x device sweep, risk curve), `fleet --event` (single-vs-fleet
non-additivity), `evidence --event` (the umbrella dossier with avoided cost
grounded in the recorded 892 MW LFDD shed) - now runs on a baseline whose inputs
a reviewer can trace to an Ofgem paragraph or a lake partition, not to a
`[VERIFY]` marker.

## 7. The counterfactual: what a TSGB fleet would have done

With the baseline REPRODUCED, the counterfactual verbs run on a sourced record
for the first time. The device is the corpus's proposed fleet -
`data/devices/tsgb-fleet.json`: 123 units / 1.14 GW continuous, 13.7 GJ of
stored inertial energy, <50 ms fleet response. **Every one of those figures is
a design/aspirational number** (the formal status document records TRL 6,
bench-tested subsystems; the white paper calls its own fleet tables "a
conceptual what-if sketch") - the counterfactual column is tagged
`Counterfactual` and the device figures never claim measured status.

**The dossier columns at the measured 215 GVA.s operating point:**

| column | RoCoF | nadir | LFDD |
|---|---:|---:|---|
| recorded | -0.170 Hz/s | 48.80 Hz | **YES - 892 MW, 1.15 M customers** |
| simulated (no device) | -0.171 Hz/s | 48.84 Hz | no* |
| counterfactual (Generic Fleet) | -0.110 Hz/s | **49.55 Hz** | **no** |

*The no-device simulation's 48.84 Hz (48.841 Hz at 76.0 s; the record is
48.8 Hz at 75.9 s) sits four hundredths above the 48.8 Hz threshold the
recorded event touched - a hair's-width classification on a boundary the
record crossed; the 0.04 Hz nadir error is the gate's measured accuracy, not a
claim that LFDD would not have fired.

At the measured operating point the fleet keeps the nadir **0.75 Hz clear of
the LFDD floor**. Swept across the corpus-cited GB inertia trajectory
(260 -> 155 -> 120 -> 102 -> 50 GVA.s, sourced against the white paper sec. 2.6 tables
and the FRCR 2025 floor), the no-device replay sheds load at every level at or
below 155 GVA.s - 2025's average - while the fleet holds the nadir at ~49.5 Hz
and avoids LFDD at four of the five levels, only losing the ride-through at
the 50 GVA.s recorded-minima band.

**Single unit vs fleet honesty** (`fleet --event`): intrinsic inertia is
N-additive, but staggered fast response is not - at the nadir the 123-unit
fleet has 866 MW online versus 1,044 MW for one aggregate device, so the fleet
nadir sits 0.20 Hz deeper (49.34 vs 49.54 Hz). Still no LFDD, but the dossier
carries the fleet number, not the flattering aggregate.

**Avoided cost, grounded in the record:** the 892 MW the record says LFDD
actually shed, over the 0.5 h assumed restoration, valued at the REGULATORY
VoLL - GBP 6,000/MWh, the cash-out Value of Lost Load set by Ofgem's Electricity
Balancing Significant Code Review (BSC P305, in force 1 November 2018):

> 892 MW x 0.5 h x GBP 6,000/MWh = **GBP 2.68 M** for this single event.

Sensitivity at consumers' average "true" VoLL (~GBP 17,000/MWh, the
DECC/Ofgem-commissioned London Economics 2013 figure Ofgem's own EBSCR
analysis cites): **GBP 7.58 M**. Both figures are what the *recorded* shed cost -
not a projection; the counterfactual's claim is only that the fleet's nadir
clears the threshold that triggered it.

Artefacts: `results/scenario-gb-2019-08-09/` (matrix + risk curve),
`results/fleet-gb-2019-08-09/`, `results/evidence-gb-2019-08-09/`
(`evidence.json`, `summary.md`) and the GBP 17k sensitivity sibling.

**The real 1-second trace, and the model's own limit.** NESO's published
1-second frequency recording for the event window is committed at
`data/neso/gb-2019-08-09-frequency.csv` (601 samples, 15:50-16:00 UTC, from
the GDA lake's `neso_1s` ingestion of the NESO historic frequency dataset).
It corroborates the record independently: minimum **48.787 Hz at 15:53:49 -
exactly 76 seconds after the fault**, where the Ofgem narrative puts the LFDD
trigger. Run the gate against it with
`replay-event gb-2019-08-09 --trace data/neso/gb-2019-08-09-frequency.csv`:
the headline metrics hold (1% RoCoF, 0.04 Hz nadir) and the nadir time agrees
(sim 76.0 s against 76.1 s in the trace), but the per-sample RMSE is
**0.099 Hz**, above the 0.05 Hz bar, and the back-test misses its 0.05 Hz nadir
gate by 0.054 Hz, so the gate demotes the verdict to MARGINAL for
trajectory-shape purposes. That is the gate working as designed:
a single-mass swing model with nine staged power steps reproduces the
excursion's headline physics, not its sample-by-sample shape (the governor
plateau and recovery dynamics differ). The counterfactual claims in this
section are headline-level - nadir clearance of the LFDD threshold - and the
trace is deliberately not attached to the catalogue record, so the headline
gate remains the operative one; any future per-sample claim would first need a
richer dynamic model to clear the RMSE bar.

## 8. Reproduce this dossier

```bash
# GDA side (W:\Code\GDA\v1) - rebuild the lake products
python NetworkModel/build_gsp_measurements.py --date 2019-08-09
python NetworkModel/build_estimated_state.py  --date 2019-08-09
python NetworkModel/report_estimation_closure.py --date 2019-08-09
python NetworkModel/build_event_catalog.py    # emits the measured catalog

# GridSim side (W:\Code\GridSim) - run the gate
dotnet run --project src/GridSim.Cli -c Release -- replay-event gb-2019-08-09
dotnet run --project src/GridSim.Cli -c Release -- replay-event gb-2019-08-09 \
    --catalog data/events/gb-2019-08-09-estimated.json
dotnet run --project src/GridSim.Cli -c Release -- scenario --event gb-2019-08-09 \
    --device data/devices/tsgb-fleet.json
dotnet run --project src/GridSim.Cli -c Release -- fleet --event gb-2019-08-09 \
    --units 1,123 --total-inertia 13.7 --total-ffr 1.14
dotnet run --project src/GridSim.Cli -c Release -- evidence --event gb-2019-08-09 \
    --device data/devices/tsgb-fleet.json --voll 6000 \
    --voll-source "Ofgem EBSCR / BSC P305 regulatory cash-out VoLL, in force 1 Nov 2018"
```

Related reading: manual ch. 31 (the event catalogue), ch. 32 (the replay gate),
ch. 35 (provenance), ch. 78 (the WLS state estimator), ch. 36 (the
forward-inference guard). Ofgem report:
`ofgem.gov.uk/publications/investigation-9-august-2019-power-outage`.
