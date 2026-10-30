# 22. Estimating GB history

GridSim reconstructs the state of the Great Britain grid for a past settlement period from the GDA data lake. Three
steps make that happen, and each has its own tool. `gridsim gda` reads a distribution network operator's Long Term
Development Statement (LTDS) tables from the lake and materialises a network. `gridsim gda-replay` runs a demand
history through a network, one power flow per timestamp. `gridsim estimate`, driven day by day by the GDA repository's
Python builders, fits the network to that period's real telemetry and stores the result in the estimated-state lake
that the bridge streams ([ch. 24](<0024 The GDA bridge.md>)) and the apps replay
([ch. 23](<0023 Replaying estimated states.md>)). Use this chapter to build a network from the lake, to replay a
demand history, or to produce estimated states for a day of your own. Every step refuses data newer than 169 hours,
so the pipeline reconstructs the past and never nowcasts.

## Before you start

- **The lake.** `$GDA_ROOT` stands for the GDA root: the lake directory that holds `NationalGrid/`, `NESO/` and
  `Derived/` (in the GDA repository it is `v1/DataSources`). `gridsim gda` takes the root from `--root`, or from the
  `GDA_ROOT` environment variable when `--root` is absent. The lake is read-only to GridSim. The LTDS tables the verb
  reads sit under `NationalGrid/RAWData/LTDS Tabular Model/` in the lake.
- **The GDA repository.** The estimated-state builders are Python scripts in the `v1` directory of the GDA repository
  (`NetworkModel/` and `Scripts/`). Run them from `v1`, with Python and the `pandas` and `pyarrow` packages installed.
  Each builder finds the lake at `DataSources/` beside `NetworkModel/`, from its own location; none of them reads
  `GDA_ROOT` or takes a `--root` flag. The builders call the GridSim command line, so set the `GRIDSIM_CLI`
  environment variable to the full path of your Release `GridSim.Cli` executable. The default path inside the script
  points at a build location that exists only on the GDA host.
- **Time.** Every timestamp is UTC. A settlement period is 30 minutes; a settlement day has 46, 48 or 50 periods
  depending on the clock change. The builders read the day's actual period list from the measurement file, so a day
  with a gap in the lake has fewer.
- Every flag of the three verbs is in [ch. 19, CLI reference](<0019 CLI reference.md#gda>),
  [`gda-replay`](<0019 CLI reference.md#gda-replay>) and [`estimate`](<0019 CLI reference.md#estimate>). This chapter shows
  the flags in use.

Timings in the captures differ by machine and run. The counts and numbers hold on the reference lake and change as
a lake is refreshed.

## The pipeline at a glance

| Stage | Tool | Reads | Writes |
|---|---|---|---|
| 1. Network | `gridsim gda` | LTDS tables in the lake | a case directory (`system.json`, `buses.json`, ...) |
| 2. Demand replay | `gridsim gda-replay` | a network and a demand series | `state-estimate.csv`, `exports/state-NNNNNN.json` |
| 3. Measurements | `NetworkModel/build_gsp_measurements.py` | the day's B1610 (or PN) generation | per-GSP generation, group demand and interconnector flows |
| 4. Measurement set | `NetworkModel/build_estimator_measurements.py` | stage 3 output, the national demand series and group demand | a measurement set for one settlement period |
| 5. Estimate | `NetworkModel/build_estimated_state.py` | stage 4, once per settlement period | `Derived/estimated_state/<case>/...` |
| 6. Comparison | `build_state_divergence.py`, `report_estimation_closure.py` | divergence: the estimated and presolved lakes; closure: the estimated lake and `demand_all_srcs` | `Derived/state_divergence`, `Derived/estimation_closure` |

Stages 1 and 2 run entirely inside GridSim. Stages 3 to 6 are the GDA repository's, stage 5 imports stage 4 and calls
`gridsim estimate` once per settlement period. The estimated lake is built on `gb-spine`, the 316-bus fold of the
national `gb-full` case; the assembler that builds both cases is described in
[ch. 9, GDA integration](<../technical/0009 GDA integration.md>). Stage 1 is a separate route: it builds one
network operator's own network (NGED, UKPN or NPg) from its LTDS tables.

There are two reconstruction routes for any day. The solve route (the presolved lake) scales the demand of the static
network to the national demand shape and re-solves. The estimate route (the estimated lake) fits the network to that
period's measurements. The divergence product compares them.

## Worked example

The example reconstructs 1 September 2026. The four parts use the lake held on the reference machine.

### 1. Materialise a network from the lake

```bash
gridsim gda --root $GDA_ROOT --licence EMIDS --out results/gda-emids --solve
```

```
$ gridsim gda --root $GDA_ROOT --licence EMIDS --out results/gda-emids --solve
GridSim  -  GDA ingestion from $GDA_ROOT  (NGED)
==========================================================================
Built in 1433 ms  -  GB/NGED ex-GDA

  Raw LTDS nodes          22,590
  Electrical buses         2,253   (busbar/disconnector ties merged: 4,790)
  Series circuits          2,027   (cables & lines)
  Transformers (2w/3w)       940 / 2   (+2 star nodes)
  Total branches           2,973
  Demand points              824   matched 824, total 8.90 GW
  Generators                   2   (0.00 GW capacity, 894 unmatched)
  System inertia             0.0 GVAs
  OLTCs                      939
  Connected islands            1   (largest 2,253 buses)

  Buses by voltage:
      132 kV       280
       66 kV         1
       33 kV     1,511
       25 kV         5
       11 kV       438
        7 kV        18

   15 GSP infeed(s) modelled as slack buses; primary = bus 3 (Warwick 132kV Main 2, 132 kV).
   WARNING OLTC: 1 transformer(s) publish an invalid tap range and got NO OLTC: 'TX SWIB3/SWIB5 33/11' (-16 %/-1 %: range excludes the nominal 0 % tap).
   ...
   Licence = EMIDS.
   Geolocated 606/2253 buses from substations.csv.
   validation: warning: 15 slack (reference) buses  -  treating as a multi-island system.

Materialised replica -> results\gda-emids (git-ignored). Run it: gridsim --dir "results/gda-emids"

Solving with the sparse (ILU0/BiCGSTAB) backend...
Converged in 530 ms (4 NR, 11 OLTC, mismatch 7.31E-009)
Generation 9.01 GW  Demand 8.90 GW  Losses 111.8 MW  V 0.850-1.010 p.u.
[exit 0]
```

The 22,590 LTDS nodes of the East Midlands licence collapse to 2,253 electrical buses once busbar and disconnector ties
are merged. The model is one connected island, it carries 8.90 GW of matched demand, and it solves in 4 Newton
iterations. The case directory written to `results/gda-emids` opens like any other case: `gridsim --dir results/gda-emids`.

### 2. Replay a day of demand through a network

`gda-replay` takes a demand series: a JSON file of timestamps and a system load factor for each. The script below
builds one for 1 September 2026 from the lake's NESO national demand (`nd_mw`), dividing each value by the total
demand of the `gb-full` case so that the factor is a plain multiplier on the case's stated demand. Run it from the GridSim
repository root, where `data/gb-full/` sits, with `GDA_ROOT` set.

```bash
python make_series.py
```

```python
import json, os, pandas as pd

root = os.environ["GDA_ROOT"]
base_mw = sum(r["pMw"] for r in json.load(open("data/gb-full/demand.json")))
nd = pd.read_parquet(f"{root}/Derived/demand_all_srcs/year=2026/week=36/demand_all_srcs_2026w36.parquet")
nd = nd[(nd.source == "neso_demand") & (nd.timestamp_utc >= "2026-09-01") & (nd.timestamp_utc < "2026-09-02")]
nd = nd.sort_values("timestamp_utc")
steps = [{"timeUtc": t.strftime("%Y-%m-%dT%H:%M:%SZ"), "systemLoadFactor": round(float(v) / base_mw, 4)}
         for t, v in zip(nd.timestamp_utc, nd.nd_mw)]
os.makedirs("results", exist_ok=True)
json.dump({"name": "nd-2026-09-01", "steps": steps}, open("results/nd-2026-09-01.json", "w"), indent=1)
print(f"case demand {base_mw:.0f} MW, {len(steps)} steps, first {steps[0]}, last {steps[-1]}")
```

```
$ python make_series.py
case demand 53285 MW, 48 steps, first {'timeUtc': '2026-09-01T00:00:00Z', 'systemLoadFactor': 0.403}, last {'timeUtc': '2026-09-01T23:30:00Z', 'systemLoadFactor': 0.4193}
[exit 0]
```

Replay it on the national case:

```bash
gridsim gda-replay --series results/nd-2026-09-01.json --case gb-full --export-every 24 --out results/gda-replay-nd
```

```
$ gridsim gda-replay --series results/nd-2026-09-01.json --case gb-full --export-every 24 --out results/gda-replay-nd

=== Historical state estimate: gb-full over nd-2026-09-01 ===
  reconstructed 48 timestamps  (2026-09-01T00:00:00.0000000Z .. 2026-09-01T23:30:00.0000000Z)
  non-converged: 0
  system loss over the series: 71.2 .. 174.2 MW

  -> results\gda-replay-nd
[exit 0]
```

All 48 half-hours converged, and the system loss moves between 71.2 MW (overnight) and 174.2 MW (the evening peak)
as the load factor moves. The per-step record is the CSV:

```
$ Get-Content results/gda-replay-nd/state-estimate.csv -TotalCount 14
# GridSim  -  2026-10-03 12:33:43 UTC
# code:    git afedfedf  |  GridSim.Core v1.95.99.0  |  build Release
# runtime: .NET 10.0.12  X64  GC=Server
# machine: LAPTOP  Microsoft Windows 10.0.26200
# cpu:     X64  -  8 logical cores  -  8 GB RAM
# simd:    Avx256  (4 doubles/step)  FMA=True
# backend: cpu
# run:     gda-replay  case=gb-full  series=nd-2026-09-01  steps=48
index,time_utc,load_factor,converged,iterations,max_mismatch,total_loss_mw,vmin_pu,vmax_pu
0,2026-09-01T00:00:00.0000000Z,0.4030,True,6,3.715E-011,87.714,0.99442,1.04576
1,2026-09-01T00:30:00.0000000Z,0.4021,True,2,2.598E-011,87.338,0.99449,1.04576
2,2026-09-01T01:00:00.0000000Z,0.3914,True,2,7.533E-011,82.936,0.99525,1.04577
3,2026-09-01T01:30:00.0000000Z,0.3915,True,2,4.453E-011,82.976,0.99524,1.04577
4,2026-09-01T02:00:00.0000000Z,0.3754,True,2,3.783E-010,76.576,0.99637,1.04577
[exit 0]
```

The first step takes 6 iterations from the flat start; every later step warm-starts from the previous converged state and
needs 2.

### 3. Estimate one settlement period from telemetry

The estimate route does not scale demand. It fits the network to a measurement set built from that period's metered
generation, interconnector flows and demand pseudo-measurements. Run from `v1` in the GDA repository, the first command
writes the measurement set for settlement period 25 (11:00 UTC on 1 September 2026) on `gb-spine`, and the second is
the `gridsim estimate` call the day builder makes for every period:

```bash
python NetworkModel/build_estimator_measurements.py --date 2026-09-01 --period 25 --out results/gda-estimate/m-sp25.json
```

```
$ python NetworkModel/build_estimator_measurements.py --date 2026-09-01 --period 25 --out results/gda-estimate/m-sp25.json
{
  "date": "2026-09-01",
  "period": 25,
  "takenUtc": "2026-09-01T11:00:00Z",
  "measurements": 948,
  "buses_with_metered_gen": 42,
  "metered_gen_mw": 11057.6,
  "ic_buses": 0,
  "ic_net_mw": 0,
  "national_demand_mw": 19885.0,
  "demand_scale": 0.3732,
  "groups_used": 0,
  "group_demand_total_mw": 0.0,
  "unallocated_gen_mw": 6038.8,
  "output": "results\\gda-estimate\\m-sp25.json"
}
[exit 0]
```

The set holds 948 measurements. Metered B1610 generation binds to 42 spine buses (11,057.6 MW), 6,038.8 MW of metered
generation could not be bound to a Grid Supply Point and is reported as unallocated rather than spread across the
network, and the demand pseudo-measurements are scaled by 0.3732 to the day's 19,885 MW national demand. `ic_buses` is
0 because the lake holds no interconnector-flow partition for that week.

```bash
gridsim estimate NetworkModel/out/gb-spine --measurements results/gda-estimate/m-sp25.json --prior solve --warm-start --export-solve --max-iter 100 --lean --backfill-removals --out results/gda-estimate/sp25
```

```
$ gridsim estimate NetworkModel/out/gb-spine --measurements results/gda-estimate/m-sp25.json --prior solve --warm-start --export-solve --max-iter 100 --lean --backfill-removals --out results/gda-estimate/sp25
error loading case 'NetworkModel/out/gb-spine': unknown case 'NetworkModel/out/gb-spine' (built-in names: case118, case14, case145, case24, case30, case300, case57, case9, cigre, gb, gb-full, gb-network-2224, gb-reduced, gb-reduced-29, gb-synthetic, ieee9, pypsa-gb-reduced, pypsa-gb-zonal, rts, synthetic, uk; or a case folder, or a MATPOWER .m file, or a JSON v2 case manifest .json)
[exit 2]
```

[Chapter 21](<0021 State estimation.md>) explains every line. Here, 948 measurements fit 631 unknowns (316 buses give
`2 * 316 - 1` states), leaving 317 degrees of freedom. The fit statistic `J` is 330.37 against a 95 per cent chi-square
threshold of 359.52 (the estimator's Wilson-Hilferty approximation of the quantile, 359.5207; the exact quantile,
359.5218, also rounds to 359.52), so the fit is consistent with the sigmas the measurements declared.

This capture is from an earlier build. The current estimator leaves the `prior-solved` rows that `--prior solve` adds
out of `J` and out of the degrees of freedom ([ch. 21](<0021 State estimation.md>)), so on this run the `dof`, the
threshold, `J` and possibly the verdict differ from the figures above: the degrees of freedom fall by the number of
`prior-solved` rows (315 on the `--prior solve` set of chapter 21), which leaves almost no redundancy. The capture has
not been retaken.

### 4. Estimate a whole day and store it

The builder runs that estimate for every settlement period in the day's stage 3 measurement file (stage 3 must have run
for the date), collects each period's quality columns and writes them to the estimated lake:

```bash
python NetworkModel/build_estimated_state.py --date 2026-09-01
```

```
$ python NetworkModel/build_estimated_state.py --date 2026-09-01
  SP02 ok  iters=23  J=301.6  chi=PASS  removed=1
  SP03 ok  iters=23  J=288.8  chi=PASS  removed=1
  SP04 ok  iters=22  J=278.4  chi=PASS  removed=1
...
  SP45 ok  iters=18  J=300.8  chi=PASS  removed=0
  SP46 ok  iters=158  J=357.5  chi=PASS  removed=12
  SP47 ok  iters=29  J=202.8  chi=PASS  removed=1
  SP48 ok  iters=29  J=220.9  chi=PASS  removed=1
{
  "date": "2026-09-01",
  "case": "gb-spine",
  "periods": 47,
  "converged": 47,
  "chi_passed": 47,
  "removed_total": 96,
...
}
[exit 0]
```

The day has 47 periods because the day's measurement file carries periods 2 to 48, one fewer than the 48 the clock
allows. All 47 converged and passed the chi-square test, with 96 measurements removed as bad data across the day. The
builder merges the rows into one weekly partition,
`Derived/estimated_state/gb-spine/year=2026/week=36/estimated_state.parquet` under the lake. In the captured run the
elided `output` line names a scratch tree, `results/estimated_state/gb-spine/year=2026/week=36/estimated_state.parquet`,
so the reference lake keeps its single stored period.

## Reading the output

### `gridsim gda`

The report is a build summary, a list of notes, the written location and, with `--solve`, a power-flow result.

- **Header.** `GDA ingestion from ... (NGED)` names the lake and the source reader (`--dno`, `nged` by default). `Built
  in N ms` is the build time and the name that follows is the model name; a carve appends `[<GSP group>]` or
  `>=132kV`.
- **`Raw LTDS nodes`.** Rows in the LTDS node table. **`Electrical buses`** is the bus count after the builder merges
  every zero-impedance circuit: a busbar or disconnector tie or, with a warning, any other equipment. The bracket
  counts the ties merged.
- **`Series circuits`** are cables and lines. **`Transformers (2w/3w)`** counts two- and three-winding units. Each
  three-winding unit becomes a new star node with one branch per winding it has, so `+2 star nodes` is two units. Both
  have three windings, and `Total branches` is `2,027 + 940 + 6 = 2,973`.
- **`Demand points`.** The count of demand rows joined to a bus, and the total of their maximum demand in GW. `matched`
  counts those that found a bus.
- **`Generators`.** The embedded generation placed on the network: the count, the export capacity in GW, and the
  register rows that found no bus (`unmatched`). By default only plant whose status is `Connected` enters the model;
  `--all-gen` admits every row.
- **`System inertia`** in GVAs, summed from the placed generators. **`OLTCs`** is one on-load tap changer per
  step-down transformer with a valid published tap range; each regulates its low-voltage bus to 1.0 per unit.
- **`Connected islands`** and the size of the largest. **`Buses by voltage`** lists the bus count at each nominal kV.
- **Notes.** Every row the builder drops or repairs is counted, and the notes that carry examples name the first five.
  In the example: 15 Grid Supply Point infeeds
  are modelled as slack buses (one per GSP group, with the largest island's primary slack named); one transformer
  published a tap range that excludes 0 per cent, so it has no OLTC; 37 zero-impedance circuits that are not
  busbars or switches were merged as ties (a sign of missing R and X cells); and 1,294 demand rows lie on nodes outside
  the carve.
- **Solve block.** `--solve` solves the model with the sparse backend (ILU0 with BiCGSTAB) when the largest island
  exceeds 200 buses and the dense backend otherwise. `Converged in 530 ms (4 NR, 11 OLTC, mismatch 7.31E-009)` gives
  Newton iterations, OLTC passes and the largest remaining mismatch in per unit. `Generation 9.01 GW` is the sum of
  the positive bus injections, which here the slack infeeds supply because the model carries 0.00 GW of embedded
  generation; `Demand` is the sum of the buses' demand. A solve
  that does not converge prints `DID NOT CONVERGE` and exits 3.

### `gridsim gda-replay`

`reconstructed 48 timestamps (first .. last)` echoes the series; `non-converged` counts steps whose power flow failed
(any non-zero count exits 3); `system loss over the series` is the lowest and highest `total_loss_mw`. The CSV has
eight comment lines naming the build and machine, then one row per timestamp:

| Column | Meaning |
|---|---|
| `index` | Step number from 0. |
| `time_utc` | The series timestamp. |
| `load_factor` | The multiplier applied to the case. Every bus's real and reactive demand is scaled by it, every generator not on the first slack bus is scaled by it, and the slack bus re-balances. |
| `converged`, `iterations`, `max_mismatch` | The solve's outcome; the mismatch is per unit. |
| `total_loss_mw` | Network loss at that step. |
| `vmin_pu`, `vmax_pu` | Lowest and highest bus voltage. |

`run-metadata.json` repeats the build and machine header. With `--export-every N`, step 0 and every N-th step also
write `exports/state-NNNNNN.json`, a `gridsim-solve-export/1` document holding the model, the solve and the full
result at that step (`state-000024.json` is the 12:00 UTC step). The warm start is only ever taken from a converged
state.

### The stored estimate and its lake row

Each settlement period becomes one row of `estimated_state.parquet`. This is the stored row for 11:00 UTC on 1
September 2026 in the reference lake:

```
$ python -c "import os, pandas as pd; d = pd.read_parquet(os.environ['GDA_ROOT'] + '/Derived/estimated_state/gb-spine/year=2026/week=36/estimated_state.parquet').drop(columns=['removed_json', 'boundary_json', 'result_json']); print(d.T.to_string(header=False))"
datetime_utc      2026-09-01 11:00:00+00:00
case                               gb-spine
status                            Converged
converged                              True
iterations                                8
max_state_update                        0.0
objective_j                      225.414917
chi_threshold                    359.520725
chi_passed                             True
dof                                     317
measurements_n                          948
removed_n                                 0
vm_min                             0.999362
vm_max                             1.051805
[exit 0]
```

The transposed print shows `max_state_update` as 0.0 because pandas rounds the row to six decimals; the stored value is
1.4822059173716063e-09.

| Column | Meaning |
|---|---|
| `datetime_utc` | The instant the state describes. |
| `status`, `converged`, `iterations`, `max_state_update` | The estimator's outcome, as in [ch. 21](<0021 State estimation.md>). A period that runs and fails to converge is stored with `converged` false and no result block. |
| `objective_j`, `chi_threshold`, `chi_passed`, `dof` | The fit statistic, its 95 per cent chi-square threshold, the verdict and the degrees of freedom. The pill in the apps shows these. |
| `measurements_n` | Measurements the fit consumed. |
| `removed_n`, `removed_json` | Bad-data removals: the count and, per removal, the measurement kind, bus and normalised residual. |
| `vm_min`, `vm_max` | Lowest and highest estimated voltage, per unit. |
| `boundary_json` | When the case carries `boundaries.json`, the ETYS boundary transfers (flow and limit in MW) evaluated at the estimated state. |
| `result_json` | The slim state block the bridge streams: each bus's `vm`, `vaDeg`, `pInjMw`, `qInjMvar` and each line flow. |

The stored row is a separate run from the re-run in step 3: its `J` is 225.41 after 8 iterations, the re-run's is
330.37 after 10, on the same 948 measurements and 317 degrees of freedom. Both were made on an earlier build, before
the `prior-solved` rows were left out of `J` and `dof` (see step 3). The apps and the bridge show the stored row.

### The day summary, divergence and closure

The builder's final JSON counts the periods, how many converged and passed chi-square, and the removals. Its exit code
is 0 when every period converged and 3 when any did not. Periods that ran and did not converge are stored and counted,
so exit code 3 means "stored with failures", not "nothing stored". A period whose `gridsim estimate` call writes no
export (an invalid measurement set, or a horizon rejection) aborts the whole day: nothing is stored and the exit code
is 1. A missing GridSim CLI and a day with no stage 3 measurement file also exit 1.

Divergence and closure then compare the day against the other route and against national records:

```
$ python NetworkModel/report_estimation_closure.py --case gb-spine --date 2026-09-01
{
  "date": "2026-09-01",
  "case": "gb-spine",
  "periods": 47,
  "converged_n": 47,
  "chi_passed_n": 47,
  "removed_total": 96,
  "est_gen_gwh": 471.94,
  "est_load_gwh": 459.9,
  "nd_gwh": 586.47,
  "load_vs_nd_pct": 78.42,
  "outlier_league": "PInjMw@bus89 x11; PInjMw@bus20 x10; PInjMw@bus40 x9; PInjMw@bus255 x7; PInjMw@bus176 x7",
...
}
[exit 0]
```

- `est_gen_gwh` and `est_load_gwh` are the day's energy from the positive and negative estimated bus injections of
  the stored periods in the window; `nd_gwh` is the NESO national demand over the same window, and `load_vs_nd_pct` is
  `est_load_gwh` as a percentage of `nd_gwh`. The window runs from one hour before 00:00 UTC on the date to 00:00 UTC
  the next day (25 hours). Here `nd_gwh` sums 50 half-hours and the estimate totals sum the 47 stored periods, so 78.42
  per cent compares unequal windows; the report applies no pass or fail threshold to it.
- `outlier_league` ranks the measurements removed most often across the day. A measurement that is removed period
  after period at the same bus is a measurement or model defect, not noise.

```
$ python NetworkModel/build_state_divergence.py --case gb-spine --date 2026-09-01
{
  "case": "gb-spine",
  "date": "2026-09-01",
  "periods_compared": 47,
  "dvm_max_pu": 0.26789,
  "dva_max_deg": 33.314,
  "dflow_max_mw": 3816.9,
  "worst_vm_bus_mode": 11,
...
}
[exit 0]
```

Divergence joins the estimate and presolved routes per period and reports the largest voltage-magnitude difference
(`dvm_max_pu`), angle difference (`dva_max_deg`) and flow difference (`dflow_max_mw`), plus the bus that is the worst
voltage offender most often. Persistent disagreement localises model or dispatch error. Treat it as evidence.

## Variations

**A voltage cut.** `--minkv 132` builds only the 132 kV network and folds the demand below the cut onto the nearest
retained bus. The notes report how much was folded:

```bash
gridsim gda --root $GDA_ROOT --licence EMIDS --minkv 132 --solve
```

```
$ gridsim gda --root $GDA_ROOT --licence EMIDS --minkv 132 --solve
...
  Electrical buses           474   (busbar/disconnector ties merged: 770)
...
  Connected islands            3   (largest 418 buses)
...
   Demand: 814 row(s), 8699.8 MW below the 132 kV cut were folded onto their nearest boundary bus (losses and voltage drop below the cut are not modelled).
...
Converged in 126 ms (4 NR, 10 OLTC, mismatch 1.76E-009)
Generation 8.97 GW  Demand 8.90 GW  Losses 84.9 MW  V 0.921-1.007 p.u.
[exit 0]
```

The 132 kV carve has 474 buses instead of 2,253. The losses fall from 111.8 MW to 84.9 MW because the 33 kV and 11 kV
networks, and the losses in them, are no longer modelled.

**One GSP supply area.** `--gsp` carves one Grid Supply Point group. Use the names `--all-gsps` prints:

```bash
gridsim gda --root $GDA_ROOT --gsp "Coventry 132kV" --solve
```

**Every GSP area in turn.** `--all-gsps` builds and solves each GSP group independently and prints one line per group:

```bash
gridsim gda --root $GDA_ROOT --all-gsps
```

```
$ gridsim gda --root $GDA_ROOT --all-gsps
GridSim  -  solving every GSP supply area of GB/NGED ex-GDA (60 groups, 61 spellings)
==============================================================================
GSP group                         Buses  Demand   Loss   Vmin   Vmax   Status
------------------------------------------------------------------------------
Abham 132 kV                        141   0.22GW      2MW  0.975  1.010  converged
Alverdiscott 132 kV                 184   0.20GW      4MW  0.942  1.009  converged
...
Feckenham 66kV                       82   0.30GW     18MW  0.578  1.000  DIV 3E-003
...
------------------------------------------------------------------------------
50/51 GSP areas converged in 4.6s
  'Iron Acton 132 kV' also covers 'Iron Acton 132kV' (same group, built once)
9 group name(s) select no live element and were skipped (not counted):
  Alverdiscott 132   (spelling variant of 'Alverdiscott 132 kV')
  Axminster 132   (spelling variant of 'Axminster 132 kV')
  ...
  Seabank 132   (spelling variant of 'Seabank 132 kV')
Demand: 19.26 GW solved of 19.55 GW total  losses 247 MW (1.28%)
[exit 3]
```

The 61 group spellings in the circuit table are 60 groups: `Iron Acton 132 kV` and `Iron Acton 132kV` have the same
canonical key, so they select the same elements and the area is built once. Nine names, such as
`Alverdiscott 132` beside `Alverdiscott 132 kV`, select no live element; they are listed after the table and not
counted. Of the 51 areas that build, Feckenham 66kV diverged
(`DIV 3E-003`, the remaining mismatch in per unit), so the run still exits 3: any area that builds and does not
converge fails the run, and so does a run in which no area builds.

**Another operator.** `--dno ukpn` and `--dno npg` read the UK Power Networks and Northern Powergrid tables; `--licence`
carves one licence area. `--neg-gen` folds embedded generation into negative demand, `--no-oltc` builds without tap
changers, and `--no-geo` skips locating buses from the substation table.

**A register vintage.** `--as-of 2026-05-01` picks the newest generation and capacity registers published on or before
that date. Without it, the as-of instant is now minus 169 hours. Files whose names carry no publication date are never
excluded.

**Inspect a table.** `--schema <path>` prints a CSV or Parquet table's columns and row count and exits:

```
$ gridsim gda --root $GDA_ROOT --schema "$GDA_ROOT/NationalGrid/RAWData/LTDS Tabular Model/ltds-table-2a-two-winding-transformer.csv"
$GDA_ROOT\NationalGrid\RAWData\LTDS Tabular Model\ltds-table-2a-two-winding-transformer.csv
  17 columns  2,705 rows
    Licence
    GSP Group
    Node 1
...
[exit 0]
```

**Replay variations.** `gda-replay --synth` replaces the series with a synthetic day (load factor 0.60 to 1.00, trough at
04:00 and peak at 16:00): `gridsim gda-replay --synth --steps 6`. `--passive-plant` runs the delayed tap changers and
thermal derating between steps. `--bess` adds hypothetical storage and tags the result as a counterfactual
([ch. 20, Counterfactuals](<0020 Counterfactuals.md>)).

**Estimate variations.** Pass a single settlement period with `--periods 25` or another case with
`--case-dir NetworkModel/out/gb-full`. For a day before the settlement-metering (B1610) record, `--source pn` in
`build_gsp_measurements.py` builds the measurements from Physical Notification levels, which are submitted intent
rather than metering; they carry looser sigmas (the larger of 10 MW and 10 per cent of the value, against 2 MW and 2
per cent for B1610, each divided by the weakest crosswalk confidence in the aggregate, at least 0.6) and no group-demand
product. The estimable history therefore starts at 2016-03-01, the first PN day. With `--source auto`, a day uses
B1610 when its file has at least 40 periods and PN otherwise.

**Keep the lake current.** The GDA host runs `Scripts/refresh_estimated_state.py` daily. Each run builds the newest
settlement day that is 8 or more days old (searching back at most 30 days), has a B1610 file and has no estimate,
through the full route
(measurements, presolve, estimate, divergence, closure). `--backfill N` also estimates up to N earlier days, walking a
cursor in `Scripts/state/estimated_backfill.json` backwards towards the 2016-03-01 floor; a day that fails is recorded
and retried on later runs, and manual and scheduled runs share the cursor:

```bash
python Scripts/refresh_estimated_state.py --backfill 20
```

**Calibrate the sigmas.** `python NetworkModel/calibrate_sigmas.py` runs the estimator with full residual output over
a spread of periods and writes `NetworkModel/resolved/sigma_calibration.json`. Read `rms_lev`, the leverage-corrected
residual RMS per measurement class: 1 means the declared sigma matches the noise, well below 1 means the sigma
overstates the noise, well above 1 means the class is overconfident. It reports; it never changes the constants.

## The 169-hour horizon

The pipeline reconstructs the past only. The rule is one number, 169 hours (7 days and 1 hour), enforced
independently at every stage:

- `gridsim gda` picks registers published on or before now minus 169 hours unless you pass `--as-of`.
- `gridsim gda-replay` rejects a series with any timestamp newer than now minus 169 hours, both when the file is
  loaded and again when the loop starts. The exit code is 2.
- `gridsim estimate` refuses a measurement set dated newer than the horizon (exit 2).
- The GDA builders reject a day unless the whole day, to the following midnight, is older than the horizon.
- The bridge rejects a start newer than the horizon and sends no tick newer than it, and the GridSim client refuses
  any tick newer than it ([ch. 24](<0024 The GDA bridge.md>)).

```
$ gridsim gda-replay --series results/nd-too-recent.json --case gb-full --out results/gda-replay-recent
REJECTED (forward inference): GDA time-series timestamp 2026-10-02T12:00:00.0000000Z is newer than the historical horizon 2026-09-26T11:33:47.1181361Z (data must lag real time by >=169 h). GridSim replays history only - it never processes recent or future data, so it cannot forward-infer.
[exit 2]
```

```
$ python NetworkModel/build_gsp_measurements.py --date 2026-10-02
Traceback (most recent call last):
...
horizon.ForwardInferenceError: 2026-10-03T00:00:00+00:00 is newer than the no-forward-inference horizon (2026-09-26T11:38:11.703146+00:00, now - 7.041666666666667d). This is a replay system: it may only reconstruct the settled past.
[exit 1]
```

The horizon moves with the clock, so the newest estimable day is always at least 8 days old. The daily refresh uses an
8-day lag for that reason, which also clears the settlement publication lag.

## Pitfalls

- **Wrong or unreadable lake.** `GDA root error: GDA root not found: no-such-lake` (exit 1) means `--root` and `GDA_ROOT`
  point at a directory that does not exist. A root that exists but lacks a table fails at load with `Load failed:
  GDA dataset not found: <path>` (exit 1). `--fleet` needs the NESO Transmission Entry Capacity register folder and
  fails when the lake lacks it (here a root, `GDA_ROOT_NO_TEC`, that has no TEC register at all):

  ```
  $ gridsim gda --root $GDA_ROOT_NO_TEC --fleet
  Fleet build failed: GDA dataset folder not found: $GDA_ROOT_NO_TEC\NESO\RAWData\Transmission Entry Capacity (TEC) register
  [exit 1]
  ```

  A lake that holds the register only as its Parquet conversion (`NESO/Parquet/transmission_entry_capacity_tec_register`)
  fails the same way (exit 1), and the message says so: GridSim reads the published `tec-register-*.csv` files under
  `NESO/RAWData`, not the Parquet copy (capture `docs/_captures/gda/gda-fleet.txt`).

- **No root at all.** `gridsim gda` with neither `--root` nor `GDA_ROOT` prints its usage and exits 2.
- **A mistyped option value.** `--dno foo` exits 2 with `error: --dno expected 'nged', 'ukpn' or 'npg' but got 'foo'`.
  An `--as-of` that is not an ISO-8601 UTC timestamp (`yyyy-MM-dd[THH:mm[:ss]]`) exits 2 the same way.
- **A mistyped GSP name.** `--gsp` is checked against the groups in the data (by the same canonical key the carve uses,
  so case, spacing and `kV` spelling do not matter) before anything is built. An unknown name exits 2 with the nearest
  names:

  ```
  $ gridsim gda --root $GDA_ROOT --gsp "Exter 132 kV" --solve
  error: --gsp 'Exter 132 kV' is not a GSP group of GB/NGED ex-GDA. Nearest: 'Exeter 132 kV', 'Enderby 132kV', 'Exeter 132'. gda --all-gsps lists every group.
  [exit 2]
  ```

- **A carve that keeps nothing.** A known name whose elements are all out of service or planned, or a `--licence` /
  `--minkv` combination that keeps no element, prints the build summary and then
  `error: the carve selected no live element (0 buses) ... No model was written.` and exits 3. Nothing is written to
  `--out` and nothing is solved. `--gsp "Exeter 132"` is an example on the
  reference lake: use `Exeter 132 kV`.
- **A materialised case goes into `gda-replay` as a directory.** `gda-replay --case` takes a built-in name, a file under
  `data/matpower/`, or a directory holding `system.json`, such as one written by `gda --out`:

  ```
  $ gridsim gda --root $GDA_ROOT --gsp "Exeter 132 kV" --out results/gda-exeter
  $ gridsim gda-replay --synth --steps 4 --case results/gda-exeter
  === Historical state estimate: results/gda-exeter over synthetic-gda-exeter ===
    reconstructed 4 timestamps  (2024-01-01T00:00:00.0000000Z .. 2024-01-01T01:30:00.0000000Z)
    non-converged: 0
    system loss over the series: 1.3 .. 1.5 MW

    -> ...\results\gda-replay-gda-exeter
  [exit 0]
  ```

  The default output folder is named after the directory's last component.
- **Exit 3 is not exit 0.** `gda-replay` exits 3 when any step fails to converge and `gda --all-gsps` when any group does
  not. The day builder exits 3 when any period runs and fails to converge, and the wrappers treat that as "stored". Exit 1
  from the day builder means the day aborted and nothing was stored.
- **Chi-square pass is not plausibility.** `chi_passed` says the fit agrees with the declared sigmas. It does not say
  the state is physical. The re-run above passed every one of its 47 periods while its first period carries a
  `vm_max` of 1.267891 per unit and a worst-case divergence from the presolved route of 0.26789 per unit. Read
  `vm_min`, `vm_max`, the removals and the divergence product beside the verdict. The cause of that PASS is known
  (October 2026): the re-run had no interconnector-flow partition for week 36 (`ic_buses` 0) and 6,567 MW of
  unallocated generation, the `--prior solve` power flow of those injections settled at up to 1.2639 pu, and its
  voltages entered the fit as 315 `prior-solved` rows at sigma 0.02. Those rows agree with the injections by
  construction, so the fit passed (`J = 301.59`; the 315 voltage rows contribute 32.2 and the 305 reactive pseudo rows
  30.8, about a tenth of their expected share each). The same set with `--prior flat` fails (`J = 557.40`, 20
  removals at the buses that sat at 1.27 pu). `gridsim estimate` now warns when the prior leaves the 0.9 to 1.1 pu band
  ([ch. 21](<0021 State estimation.md#priors-and-warm-starts>)), and `--fdia-screen` reads `suspected` on that
  period.
- **Missing builder path.** `error: GridSim CLI not found: GRIDSIM_CLI is not set and 'gridsim' is not on PATH.` means
  the builders cannot find the executable; set `GRIDSIM_CLI` or put `gridsim` on PATH. When `GRIDSIM_CLI` is set to a file
  that does not exist, the message reads `error: the GridSim CLI set by GRIDSIM_CLI does not exist: <path>`.
- **A day that is too recent.** The builders raise `ForwardInferenceError`. Choose a day at least 8 days old.
- **Embedded generation joins.** The generation register joins to the demand table by GSP group and primary-substation
  name, compared by a canonical key that drops voltage tokens (`33 11 kV`, `6.6`) and station words (`S Stn`) so the two
  tables' spellings of one primary meet. A row whose group does not list the primary joins by name alone when the name
  is unique to one group; at a site whose demand sits on several buses it joins the bus of its own voltage. A row whose
  primary or voltage picks no bus, or several, is left unplaced with a `WARNING generation join:` note naming it and
  its export capacity. The `Generation join` notes count each route; rows that find no bus add nothing to the model,
  and the slack infeeds supply the demand.

## Limits

- The estimated lake is built on `gb-spine`, a fold of `gb-full` that drops impedances below the Grid Supply Point; the
  other zone cases have no estimated lake.
- `gda --all-gsps` sets aside a group name that selects no live element; it does not tell a spelling variant from a
  genuinely missing area except by the `spelling variant of` hint, which only matches a name that another group's name
  begins with.
- Generation rows whose primary-substation name matches no bus of their GSP group, by the canonical key, are not
  placed; the notes count them.
- A period whose `gridsim estimate` call writes no export ends the day builder with exit 1 and stores none of that day's
  periods.

## See also

- [ch. 9, GDA integration](<../technical/0009 GDA integration.md>) - the model builder, the assembler, the lake products
- [ch. 21, State estimation](<0021 State estimation.md>) - the `estimate` verb and the chi-square test
- [ch. 23, Replaying estimated states](<0023 Replaying estimated states.md>) - viewing the estimated lake in the apps
- [ch. 24, The GDA bridge](<0024 The GDA bridge.md>) - streaming the lake into GridSim
- [ch. 19, CLI reference: gda](<0019 CLI reference.md#gda>), [gda-replay](<0019 CLI reference.md#gda-replay>), [estimate](<0019 CLI reference.md#estimate>)
- [ch. 30, Troubleshooting](<0030 Troubleshooting.md>) - non-convergence and pipeline faults
