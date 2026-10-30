# 13. Battery storage studies

A battery storage study answers one question: if a battery of this chemistry, power and energy were connected at this
bus, what would it do over a day of demand, and what would that cost the battery? GridSim carries each site through a
time series. State of charge, cell temperature and ageing persist from step to step, a dispatch policy decides what the
site is asked to do, the chemistry model decides what it can actually deliver, and the delivered power is injected into
the network before the AC power flow is solved. The report gives, for every site, the power it delivered at each step,
the state of charge and temperature it ended each step at, which limit stopped it, the energy it moved, the balance-of-plant
energy it drew, its round-trip efficiency, and how much capacity it aged. It gives, for the
network, the slack-bus power and the lowest bus voltage at each step.

Two verbs take a battery. `gridsim chain` runs the study on a case under a synthetic or ramped load, and is the verb
for sizing and comparing sites (the verb itself is in [ch. 11](<0011 Contingency analysis.md>)).
`gridsim gda-replay` adds the same battery to a replayed day of recorded demand, and tags the result
`COUNTERFACTUAL` (see [ch. 22](<0022 Estimating GB history.md>)). Both build the site from the same `--bess`
specification and report it with the same summary lines. The model behind them (cell equivalent circuit, pack, ageing,
dispatch) is in [technical ch. 27](<../technical/0027 Battery energy storage.md>).

## Before you start

- A case that loads. The examples use `case9` (three generators, nine buses, 315 MW peak demand), whose bus numbers
  run 1 to 9. A site on a bus that is not in the case is refused.
- A site specification, `chemistry:bus:MW:MWh` with an optional fifth field for the starting state of charge.
  `lfp:5:5:10` is an LFP battery at bus 5, rated 5 MW, holding 10 MWh.
- Nothing else. With no policy given, `chain` runs the `peak-shave` policy with thresholds derived from the case's
  peak demand.

The chemistry presets are representative cells, tagged `CuratedPlaceholder` with a `[VERIFY]` note, and every `chain` report
prints that reminder on its second line (the `gda-replay` console output does not). Use them to compare chemistries and exercise a study. Replace them with a
datasheet before quoting a result for a real asset. Technical ch. 27 owns that description.

## Worked example

A 5 MW / 10 MWh LFP battery at bus 5 of `case9`, over a 24 h demand day in six 4 h steps, shaving the peak:

```bash
gridsim chain case9 --mode daily --steps 6 --bess lfp:5:5:10 --policy peak-shave
```

```
$ gridsim chain case9 --mode daily --steps 6 --bess lfp:5:5:10 --policy peak-shave
GridSim - storage time series  *  case9  *  6 x 4 h  *  mode=daily  *  policy=peak-shave
prices: none.  Chemistry presets are CuratedPlaceholder [VERIFY] (representative cells).

...
|---:|---:|---:|---:|---:|---|---:|---:|:-:|
| 0 | 0.0 | 0.700 | 220 | - |     0.0 / 0.500 / 25.0 | 49 | 1.024 | y |
| 1 | 4.0 | 0.600 | 189 | - |    -1.3 / 0.950 / 29.4 (SoC ceiling) | 43 | 1.025 | y |
| 2 | 8.0 | 0.700 | 220 | - |     0.0 / 0.950 / 28.6 | 49 | 1.024 | y |
| 3 | 12.0 | 0.900 | 283 | - |     0.0 / 0.950 / 28.0 | 64 | 1.005 | y |
| 4 | 16.0 | 1.000 | 315 | - |     2.4 / 0.050 / 29.6 (SoC floor) | 69 | 0.996 | y |
| 5 | 20.0 | 0.900 | 284 | - |     0.0 / 0.050 / 28.8 | 64 | 1.005 | y |

LFP 5MW/10MWh @5: out 9.7 MWh, in 5.3 MWh, aux 0.27 MWh, stored change -5.0 MWh, round trip 93.5 %, 0.75 equivalent full cycles
    ageing over the run: calendar 0.1372 %, cycle 0.0026 % (rainflow, depth-aware: 0.0020 %), deepest cycle 90 % SoC
[chain] done -> results\chain-storage-case9.md
[exit 0]
```

The report holds no timings and has no clock or random input, so the same command prints the same numbers run to run on one machine; no test compares
a run across CPUs. The `[chain] done` line goes to standard error and names the report file.

What happened. The case's peak demand is 315 MW, so the default thresholds are 283.5 MW (90 %) to discharge above
and 220.5 MW (70 %) to recharge below. Step 0 demand is exactly the 220.5 MW low threshold and the comparison is strict, so the battery is asked for nothing.
Step 3 demand is 283.5 MW to the printed precision but one unit in the last place below the high threshold, so the battery
is asked for nothing there too. At step 1 the demand is 189 MW, the battery is asked
for the full 5 MW of recharge, and it stops at the SoC window ceiling: the mean over the 4 h step is 1.3 MW (the
column shows it as `-1.3`, negative for charging), and the state of charge goes from 0.500 to 0.950. At step 4
the demand is 315 MW, the battery is asked for 5 MW and the SoC window floor stops it after a mean 2.4 MW, leaving
0.050. The slack bus falls from 72 MW with the battery idle at that load (see `gridsim chain case9 --steps 3
--bess lfp:5:5:10 --policy idle`, below) to 69 MW at step 4 (both rounded to whole MW). The battery moved 9.7 MWh
out and 5.3 MWh in, finished 5.0 MWh down on the energy it started with, and returned 93.5 % through the round trip.

## Reading the output

The report is a header, one table row per step, then a summary per site. The same text is written to a Markdown file.

**Title line.** `GridSim - storage time series * case9 * 6 x 4 h * mode=daily * policy=peak-shave` gives the case, the
number of steps, the step length in hours, the load mode and the dispatch policy in force.

**Prices line.** `prices: none.`, `prices: Synthetic GB-shaped price day (not a market record).` or `prices: file
<name>.`, followed by the reminder that the chemistry presets are `CuratedPlaceholder` `[VERIFY]` cells.

**Step table.** One row per step. The table has one column group per site, in the order the `--bess` flags were given.

| Column | Meaning |
|---|---|
| `Step` | Step index from 0. |
| `Hour` | Hour of day at the start of the step. Step index times the step length, wrapped at 24. |
| `Load x` | The demand multiplier applied to every load this step. `fixed` is 1.000 throughout, `ramp` adds `--ramp-pct` percent per step, `daily` follows the 24 h demand curve (0.600 at 04:00, 1.000 at 16:00). |
| `Demand MW` | Total system demand at that multiplier, before any battery auxiliary load. |
| `Price` | The step's price in GBP/MWh, or `-` when there is no price series (see [Policies](#policies)). |
| Site column | `<chemistry> <MW>MW/<MWh>MWh @<bus>: MW / SoC / T deg C`. Three values per step, described below. |
| `Slack MW` | Real power the slack bus generates once the battery is in the case. It falls when the battery discharges and rises when it charges. |
| `Vmin` | Lowest bus voltage of the solved step, in per-unit. `-` when the step did not converge, and so is `Slack MW`: such a step has no solved state. |
| `Conv` | `y` if the power flow converged, `N` if not. |

The site column reads `MW / SoC / T`, for example `-1.3 / 0.950 / 29.4 (SoC ceiling)`.

- **MW** is the AC power the site delivered to the bus, averaged over the step. Positive is discharge, negative is
  charge. It is after the converter, whose one-way efficiency is 0.98. A value of `-0.0` is a small charge that rounds
  to zero (the battery is full and only self-discharge acts).
- **SoC** is the state of charge at the end of the step, as a fraction of the cell's present capacity, so it already
  reflects fade. The window each chemistry may use is in [the chemistry table](#the-six-chemistries).
- **T** is the cell temperature at the end of the step in degrees Celsius (the header reads `T deg C`). The pack starts
  at an ambient of 25 C, heats under load, and sheds heat to ambient and to its cooling system.
- **The bracket**, when present, names the first limit that stopped the site delivering what was asked at any point
  inside the step. The values are `SoC floor`, `SoC ceiling`, `current limit` (the chemistry's continuous C-rate),
  `cut-off voltage`, `charge voltage`, `temperature derate`, `too cold to charge` and `maximum power transfer`
  (the point beyond which no current delivers more power). No bracket means the request was met.

Each step is solved in 30 sub-steps inside the chemistry model, so a step that starts with room in the SoC window and
ends at its edge is reported against the edge and delivers a lower mean power, as in step 4 above.

**Summary lines.** Two lines per site, after the table.

```
LFP 5MW/10MWh @5: out 9.7 MWh, in 5.3 MWh, aux 0.27 MWh, stored change -5.0 MWh, round trip 93.5 %, 0.75 equivalent full cycles
    ageing over the run: calendar 0.1372 %, cycle 0.0026 % (rainflow, depth-aware: 0.0020 %), deepest cycle 90 % SoC
```

- `out` and `in` are the AC energy the site delivered and absorbed over the run, in MWh. `out` sums the positive
  deliveries and `in` the negative ones, each times the step length.
- `aux` is the balance-of-plant energy the site drew from the grid, in MWh: a standby load (0.2 % of the rated MW for
  lithium chemistries, 0.1 % for lead-acid, 0.3 % for the flow battery), plus cooling when the cells exceed the
  chemistry's setpoint (heat removed divided by a coefficient of performance of 3.0, lithium chemistries only), plus
  the electrolyte pumps of a flow battery (4 % of the power moved). It is added to the site's bus as demand, and it
  is why an idle battery still shows an `aux` figure.
- `stored change` is the energy above the SoC floor at the end less at the start, in MWh, at the nominal pack voltage
  and the present capacity, printed with its sign. Negative means the battery finished emptier than it began. A change
  that rounds to zero (self-discharge on an idle battery) prints `+0.0`.
- `round trip` is `(out + max(0, change)) / (in + max(0, -change))`, which credits the net change in stored energy so
  that a run that ends empty or full is not flattered or penalised. Auxiliary energy is not in it. It prints `-`
  when `in` is below 1e-9 MWh, because a run that only discharges has no round trip.
- `equivalent full cycles` is `(out + in) / (2 x nominal pack energy)`. The nominal energy is the pack as sized (see
  Pitfalls), not the energy you asked for.
- `calendar` is the capacity lost to time, in percent, following the square root of elapsed time at the cell's
  temperature and state of charge. One day is `sqrt(1/365)`, 5.2 %, of a year's calendar fade at constant stress, so a
  one-day figure cannot be scaled linearly (LFP: 0.1369 % over the captured day, against `0.001 x sqrt(365)` = 1.91 % for a
  year at 25 C and 50 % SoC).
- `cycle` is the capacity lost to throughput, in percent: 20 % over the chemistry's `N80` equivalent full cycles,
  scaled by temperature and C-rate.
- `rainflow, depth-aware` is the cycle fade recomputed from the SoC trace with ASTM E1049-85 rainflow counting, each
  counted cycle costing `(0.2 / N80) x depth^beta`, so many shallow cycles age a cell less than one deep one.
- `deepest cycle` is the depth of the largest rainflow cycle as a percentage of full SoC. 90 % is the whole LFP
  window (0.05 to 0.95).

Total fade is capped at 80 %, and the fade feeds back into the cell: capacity falls and resistance rises with it, so a
long run delivers less than a short one.

**Footer.** `WARNING: N step(s) did not converge.` appears after the summary when any step failed, and the exit code is
3. The `[chain] done -> <path>` line names the Markdown report on standard error.

**Files.** `chain` writes `results/chain-storage-<case>.md` and, beside it, a CSV of the same run. `--out <path>` moves
both (the CSV takes the same name with a `.csv` extension). The CSV carries every number to six significant figures, with NaN
as an empty field, the fade as a fraction and the voltage extremes and losses the table omits:

```
$ head -4 results/bess-demo.csv
step,hour,load_factor,demand_mw,price,s0_request_mw,s0_delivered_mw,s0_aux_mw,s0_soc,s0_temp_c,s0_fade,s0_limited,slack_mw,vmin,vmax,loss_mw,converged
0,0,0.7,220.5,,0,0,0.01,0.499833,25,0.000408227,0,49.0992,1.02356,1.04076,2.18915,1
1,4,0.6,189,,-5,-1.32387,0.0109119,0.949994,29.3712,0.000796927,1,43.1443,1.025,1.04385,1.60956,1
2,8,0.7,220.5,,0,0,0.01,0.949828,28.5984,0.0010474,0,49.0992,1.02356,1.04076,2.18915,1
[exit 0]
```

There are seven columns per site, numbered `s0`, `s1`, ... in `--bess` order: `request_mw` (what the policy asked for),
`delivered_mw` (what the pack gave), `aux_mw`, `soc`, `temp_c`, `fade` (total capacity fade as a fraction, so
`0.000796927` is 0.0797 %) and `limited` (1 if a limit bound). After them come `slack_mw`, `vmin`, `vmax`, `loss_mw`
(total network loss) and `converged`. In step 1 above the request is -5 MW and the delivery -1.32 MW, with
`limited` 1: the policy asked for a full-rate recharge and the SoC ceiling cut it.

## Variations

### The six chemistries

`--bess` takes one of six chemistries, matched case-insensitively. The aliases are `sodium-ion`, `naion` and `sib` for
`na-ion`, `flow` and `vanadium` for `vrfb`, and `lead` and `vrla` for `lead-acid`.

| Chemistry | What distinguishes it | Cell (Ah) | Nominal V | Max C (discharge / charge) | SoC window | Cycles to 80 % (`N80`) | Calendar fade (per sqrt day) |
|---|---|---|---|---|---|---|---|
| `lfp` | Flat voltage plateau, thermally robust, the long-life lithium choice for 1C-class duty | 280 | 3.29 | 1.0 / 0.5 | 5 - 95 % | 6000 | 0.0010 |
| `nmc` | Higher voltage and power capability than LFP, shorter life, more temperature-sensitive | 64 | 3.78 | 2.0 / 1.0 | 5 - 95 % | 3500 | 0.0016 |
| `lto` | Very high rate (fast response duty), very long life, charges down to -20 C, lowest energy density | 30 | 2.30 | 6.0 / 6.0 | 3 - 97 % | 20000 | 0.0005 |
| `na-ion` | LFP-like energy, keeps more power in the cold, life still maturing | 160 | 3.10 | 2.0 / 1.0 | 3 - 97 % | 4000 | 0.0014 |
| `vrfb` | Vanadium flow: energy set by the tank, slow, pumps are a real load, lowest cycle fade (0.0006 % over the daily run below), `CRateSensitivity` 0 | 1000 (tank share) | 1.40 | 0.25 / 0.25 | 15 - 85 % | 20000 | 0.0002 |
| `lead-acid` | Valve-regulated lead-acid: strong rate effect, 90 % coulombic efficiency, short life at depth | 1000 | 2.04 | 0.3 / 0.2 | 20 - 98 % | 1200 | 0.0025 |

`Nominal V` is the open-circuit voltage at 50 % SoC. The C-rate is the BMS continuous limit as a multiple of the cell's Ah
capacity, per cell, so the pack limit is that current times the parallel strings at the pack voltage. The 10 MWh LFP pack
(304 x 40 cells, 280 Ah each) has a limit of 280 A per cell at discharge and 140 A at charge, which is 11.2 MW and 5.6 MW at
the nominal 1000 V before sag, so the 5 MW rating applies. The effective power of a site is the lower of its `MW` rating
and the pack's limit: a 2 MW / 4 MWh lead-acid site (490 x 5 cells, 1000 Ah each) has a 0.3C limit of 300 A per cell, which
is 1.4 MW AC at 0.5 SoC. The report shows `(current limit)` and the first step of the capture below averages 1.3 MW. The full constants (open-circuit
curves, impedances, time constants, thermal data, activation energies, auxiliary load) are in
[technical ch. 27](<../technical/0027 Battery energy storage.md>).

The same 5 MW / 10 MWh site at bus 5 of `case9`, run through the same 24 h demand day in twelve 2 h steps under the
default peak-shave thresholds, gives the comparison below (one capture per chemistry in `docs/_captures/bess/`, for
example `gridsim chain case9 --mode daily --steps 12 --bess vrfb:5:5:10`):

| Chemistry | out MWh | in MWh | aux MWh | Round trip | Calendar fade | Cycle fade | Rainflow fade | Deepest cycle |
|---|---|---|---|---|---|---|---|---|
| `lfp` | 9.6 | 5.3 | 0.27 | 93.3 % | 0.1369 % | 0.0026 % | 0.0020 % | 90 % |
| `nmc` | 9.6 | 5.4 | 0.28 | 93.3 % | 0.1980 % | 0.0043 % | 0.0035 % | 90 % |
| `lto` | 9.8 | 5.3 | 0.24 | 94.6 % | 0.0558 % | 0.0007 % | 0.0006 % | 94 % |
| `na-ion` | 9.5 | 5.6 | 0.32 | 90.1 % | 0.1894 % | 0.0042 % | 0.0032 % | 94 % |
| `vrfb` | 7.6 | 6.1 | 0.91 | 68.9 % | 0.0259 % | 0.0006 % | 0.0004 % | 70 % |
| `lead-acid` | 7.7 | 7.7 | 0.12 | 66.6 % | 0.4087 % | 0.0124 % | 0.0082 % | 78 % |

Read it as the chemistries behave. LTO returns the most. The flow battery ages the least in all three fade columns and lead-acid the most. NMC matches LFP on round trip but fades
1.45 times faster by calendar and 1.65 times faster by cycle in this run. The flow battery and lead-acid return two
thirds of what they absorb: the flow battery because its pumps draw 0.91 MWh and its power is capped well below the
rating (`maximum power transfer` at 2.3 MW and 1.5 MW), lead-acid because its resistance and 90 % coulombic efficiency
lose energy at rate and its 0.3C limit shows as `current limit`. Lead-acid also ends with a smaller stored change
(-3.9 MWh against -5.0 MWh) because its SoC floor is 0.2: the battery holds only 3.9 MWh above the floor at the start,
and it reaches the floor at step 8. The flow battery's -4.9 MWh follows from its 0.15 floor in the same way.

A single-chemistry run with the sodium-ion preset, for a short fixed-load case, is a second look at a site that is
discharge-limited by its SoC floor rather than its rate:

```
$ gridsim chain case9 --steps 4 --bess na-ion:5:8:15
GridSim - storage time series  *  case9  *  4 x 0.5 h  *  mode=fixed  *  policy=peak-shave
prices: none.  Chemistry presets are CuratedPlaceholder [VERIFY] (representative cells).

...
|---:|---:|---:|---:|---:|---|---:|---:|:-:|
| 0 | 0.0 | 1.000 | 315 | - |     8.0 / 0.228 / 29.8 | 64 | 0.996 | y |
| 1 | 0.5 | 1.000 | 315 | - |     5.2 / 0.030 / 30.0 (SoC floor) | 66 | 0.996 | y |
| 2 | 1.0 | 1.000 | 315 | - |     0.0 / 0.030 / 29.8 (SoC floor) | 72 | 0.996 | y |
| 3 | 1.5 | 1.000 | 315 | - |     0.0 / 0.030 / 29.7 (SoC floor) | 72 | 0.996 | y |

Na-ion 8MW/15MWh @5: out 6.6 MWh, in 0.0 MWh, aux 0.07 MWh, stored change -7.5 MWh, round trip -, 0.22 equivalent full cycles
    ageing over the run: calendar 0.0414 %, cycle 0.0013 % (rainflow, depth-aware: 0.0009 %), deepest cycle 47 % SoC
[chain] done -> results\chain-storage-case9.md
[exit 0]
```

Here the demand is 315 MW at every step (`fixed` mode). The 8 MW battery delivers its full rating in step 0, runs
into the SoC floor of 0.030 partway through step 1 (mean 5.2 MW), and delivers nothing afterwards. With no charge
phase there is no round trip, so that field prints `-`. The cells heat to 30.0 C, the sodium-ion cooling setpoint, and the cooling load is part of the `aux` figure.

### Policies

`--policy` selects the dispatch rule. Without it `chain` runs `peak-shave`. Every site in the run uses the same
policy. The policy states what the site is asked for each step, in MW, discharge positive, capped at the site's
rating; the chemistry then decides what is delivered.

**`idle`.** Request 0 MW. The site self-discharges and draws its standby load, so it is the baseline for what the
battery costs by existing:

```
$ gridsim chain case9 --steps 3 --bess lfp:5:5:10 --policy idle
GridSim - storage time series  *  case9  *  3 x 0.5 h  *  mode=fixed  *  policy=idle
prices: none.  Chemistry presets are CuratedPlaceholder [VERIFY] (representative cells).

...
|---:|---:|---:|---:|---:|---|---:|---:|:-:|
| 0 | 0.0 | 1.000 | 315 | - |     0.0 / 0.500 / 25.0 | 72 | 0.996 | y |
| 1 | 0.5 | 1.000 | 315 | - |     0.0 / 0.500 / 25.0 | 72 | 0.996 | y |
| 2 | 1.0 | 1.000 | 315 | - |     0.0 / 0.500 / 25.0 | 72 | 0.996 | y |

LFP 5MW/10MWh @5: out 0.0 MWh, in 0.0 MWh, aux 0.02 MWh, stored change +0.0 MWh, round trip -, 0.00 equivalent full cycles
    ageing over the run: calendar 0.0250 %, cycle 0.0000 % (rainflow, depth-aware: 0.0000 %), deepest cycle 0 % SoC
[chain] done -> results\chain-storage-case9.md
[exit 0]
```

The SoC stays at 0.500, the site draws 0.015 MWh of standby power over 1.5 h (0.01 MW, printed 0.02), and the calendar fade is
0.0250 %, which is `0.001 x sqrt(1.5 / 24)` as a percentage: calendar fade follows the square root of time.

**`peak-shave`.** Request the demand above a high threshold as discharge, and the headroom below a low threshold as
charge, each capped at the rating: `demand > high` asks for `min(rating, demand - high)`, `demand < low` asks for
`-min(rating, low - demand)`, anything between asks for nothing. The thresholds default to 90 % and 70 % of the peak
demand of the run (the case's total load times the largest load multiplier). `--shave-high MW` and `--shave-low MW`
set them in absolute MW:

```bash
gridsim chain case9 --mode daily --steps 6 --bess lfp:5:5:10 --policy peak-shave --shave-high 250 --shave-low 200
```

```
$ gridsim chain case9 --mode daily --steps 6 --bess lfp:5:5:10 --policy peak-shave --shave-high 250 --shave-low 200
GridSim - storage time series  *  case9  *  6 x 4 h  *  mode=daily  *  policy=peak-shave
prices: none.  Chemistry presets are CuratedPlaceholder [VERIFY] (representative cells).

...
|---:|---:|---:|---:|---:|---|---:|---:|:-:|
| 0 | 0.0 | 0.700 | 220 | - |     0.0 / 0.500 / 25.0 | 49 | 1.024 | y |
| 1 | 4.0 | 0.600 | 189 | - |    -1.3 / 0.950 / 29.4 (SoC ceiling) | 43 | 1.025 | y |
| 2 | 8.0 | 0.700 | 220 | - |     0.0 / 0.950 / 28.6 | 49 | 1.024 | y |
| 3 | 12.0 | 0.900 | 283 | - |     2.4 / 0.050 / 29.6 (SoC floor) | 62 | 1.005 | y |
| 4 | 16.0 | 1.000 | 315 | - |     0.0 / 0.050 / 28.8 (SoC floor) | 72 | 0.996 | y |
| 5 | 20.0 | 0.900 | 284 | - |     0.0 / 0.050 / 28.1 (SoC floor) | 64 | 1.005 | y |

LFP 5MW/10MWh @5: out 9.7 MWh, in 5.3 MWh, aux 0.28 MWh, stored change -5.0 MWh, round trip 93.5 %, 0.75 equivalent full cycles
    ageing over the run: calendar 0.1269 %, cycle 0.0026 % (rainflow, depth-aware: 0.0020 %), deepest cycle 90 % SoC
[chain] done -> results\chain-storage-case9.md
[exit 0]
```

With the high threshold at 250 MW the battery discharges at step 3 (demand 283 MW) and is empty before the true peak
at step 4. The slack is 62 MW at step 3 against 64 MW in the default run, and 72 MW at the 315 MW peak in step 4
against 69 MW. A lower threshold draws the battery down earlier, so the thresholds, and not only the size of the
battery, decide whether it is available at the peak.

**`hold`.** Response holding. The site keeps its state of charge near 0.5 so that it stays ready to deliver a
frequency-response service in either direction: above 0.55 it discharges at 20 % of its rating, below 0.45 it
charges at 20 % of its rating, inside the band it does nothing. The target 0.5, the band 0.05 and the 20 % are fixed.
Start the site away from 0.5 to see it trickle back:

```bash
gridsim chain case9 --mode daily --steps 6 --bess nmc:5:5:10:0.75 --policy hold
```

```
$ gridsim chain case9 --mode daily --steps 6 --bess nmc:5:5:10:0.75 --policy hold
GridSim - storage time series  *  case9  *  6 x 4 h  *  mode=daily  *  policy=hold
prices: none.  Chemistry presets are CuratedPlaceholder [VERIFY] (representative cells).

...
|---:|---:|---:|---:|---:|---|---:|---:|:-:|
| 0 | 0.0 | 0.700 | 220 | - |     1.0 / 0.384 / 23.7 | 48 | 1.024 | y |
| 1 | 4.0 | 0.600 | 189 | - |    -1.0 / 0.734 / 27.3 | 43 | 1.025 | y |
| 2 | 8.0 | 0.700 | 220 | - |     1.0 / 0.367 / 25.3 | 48 | 1.024 | y |
| 3 | 12.0 | 0.900 | 283 | - |    -1.0 / 0.718 / 28.0 | 65 | 1.005 | y |
| 4 | 16.0 | 1.000 | 315 | - |     1.0 / 0.349 / 25.7 | 71 | 0.996 | y |
| 5 | 20.0 | 0.900 | 284 | - |    -1.0 / 0.701 / 28.0 | 65 | 1.005 | y |

NMC 5MW/10MWh @5: out 12.0 MWh, in 12.0 MWh, aux 0.24 MWh, stored change -0.5 MWh, round trip 95.6 %, 1.20 equivalent full cycles
    ageing over the run: calendar 0.1823 %, cycle 0.0065 % (rainflow, depth-aware: 0.0045 %), deepest cycle 40 % SoC
[chain] done -> results\chain-storage-case9.md
[exit 0]
```

The policy reads the SoC at the start of each step. The site starts at 0.750, above the band, so it discharges 1.0 MW
(20 % of 5 MW) for the whole 4 h step and ends at 0.384. That is below the band, so the next step charges at 1.0 MW
and ends at 0.734, and the SoC swings in a sawtooth either side of the band. A 4 h step moves the SoC 0.35, far more
than the 0.10 band is wide, so it never settles. A step of 0.5 h moves it 0.04 and does. The policy does not deliver
the response service. It keeps the SoC where a service could be delivered.

**`price`.** The price-threshold policy charges at full rating when the price is at or below `--buy-below` (default
40 GBP/MWh) and discharges at full rating when it is at or above `--sell-above` (default 90 GBP/MWh), and holds in
between. It acts on a price series, so it needs `--prices`; without one it exits 2 with `--policy price needs --prices
(a price series): without one it never charges or discharges.`

**`lp`.** The optimal-schedule policy plans the whole run as one storage schedule linear programme over the price series,
with `--degradation-gbp-per-mwh` (default 0) as the cost of each MWh of throughput, then runs the schedule through the
cell model step by step. It is the default policy when `--prices` is given. Without prices it exits 2 (`--policy lp
needs --prices`); a schedule LP that does not solve to an optimum exits 3 and no schedule runs.

**`--prices synthetic|<file>`.** The price series, in GBP/MWh, one value per step:

```bash
gridsim chain case9 --mode daily --steps 6 --bess nmc:5:5:10 --policy price --prices synthetic
gridsim chain case9 --mode daily --steps 6 --bess nmc:5:5:10 --prices synthetic
```

`synthetic` is the labelled synthetic GB-shaped price day (`SyntheticPrices.GbDay`, sampled at the start of each step);
it is not a market record, and the prices line says `prices: Synthetic GB-shaped price day (not a market record).` A
file holds one price per line, or CSV lines whose last field is the price; blank lines and lines starting with `#` are
skipped, values use `.` as the decimal point and may be negative, and the prices line reads `prices: file <name>.`
Fewer values than steps exits 1; more is allowed, the first `--steps` are used and a `note:` on standard error says how
many were not. A file that is missing, unreadable or holds a value that is not a number exits 1, naming the file and the
line. With a price series the `Price` column holds each step's price and each site's summary line ends with
`revenue GBP <n>`, the value of the energy it traded at the step prices.

The storage flags (`--prices`, `--policy`, `--shave-high`, `--shave-low`, `--buy-below`, `--sell-above`,
`--degradation-gbp-per-mwh`, `--step-hours`) need at least one `--bess`; on plain `chain` each exits 2.

### Several sites

Repeat `--bess` for each site. Each site has its own chemistry, bus, rating, starting state, SoC, temperature and
ageing, and its own column in the table and its own summary lines. All sites share the verb's single policy and
thresholds. Two sites at the same bus are allowed and add.

```bash
gridsim chain case9 --steps 3 --bess lead-acid:3:2:4 --bess lfp:5:5:10
```

```
$ gridsim chain case9 --steps 3 --bess lead-acid:3:2:4 --bess lfp:5:5:10
GridSim - storage time series  *  case9  *  3 x 0.5 h  *  mode=fixed  *  policy=peak-shave
prices: none.  Chemistry presets are CuratedPlaceholder [VERIFY] (representative cells).

...
|---:|---:|---:|---:|---:|---|---|---:|---:|:-:|
| 0 | 0.0 | 1.000 | 315 | - |     1.3 / 0.323 / 27.0 (current limit) |     5.0 / 0.266 / 26.9 | 65 | 0.995 | y |
| 1 | 0.5 | 1.000 | 315 | - |     0.9 / 0.200 / 28.8 (current limit) |     4.5 / 0.050 / 29.0 (SoC floor) | 66 | 0.996 | y |
| 2 | 1.0 | 1.000 | 315 | - |     0.0 / 0.200 / 28.8 (SoC floor) |     0.0 / 0.050 / 28.9 (SoC floor) | 72 | 0.996 | y |

Lead-acid 2MW/4MWh @3: out 1.1 MWh, in 0.0 MWh, aux 0.00 MWh, stored change -1.5 MWh, round trip -, 0.14 equivalent full cycles
    ageing over the run: calendar 0.0646 %, cycle 0.0023 % (rainflow, depth-aware: 0.0012 %), deepest cycle 30 % SoC
LFP 5MW/10MWh @5: out 4.7 MWh, in 0.0 MWh, aux 0.02 MWh, stored change -5.0 MWh, round trip -, 0.24 equivalent full cycles
    ageing over the run: calendar 0.0241 %, cycle 0.0008 % (rainflow, depth-aware: 0.0006 %), deepest cycle 45 % SoC
[chain] done -> results\chain-storage-case9.md
[exit 0]
```

The lead-acid site at bus 3 is held to a mean 1.3 MW by its 0.3C limit in step 0 (`current limit`) and again at 0.9 MW
in step 1, and is at its SoC floor of 0.200 from step 1 onward. The LFP site at bus 5 delivers its full 5.0 MW in
step 0, then reaches its SoC floor of 0.050 in step 1. Both sites then sit at their floors and the slack climbs back
to 72 MW. Each site's summary is reported separately. Sites are compared by reading their two lines each.

### Replaying recorded demand: `gda-replay`

`gridsim gda-replay` accepts `--bess` and `--policy` with the same site syntax. It adds the battery to a day of
recorded demand and reports what the battery would have done, with the hypothetical status in the output:

```bash
gridsim gda-replay --synth --case case9 --bess lfp:5:5:10 --out results/bess-day
```

```
$ gridsim gda-replay --synth --case case9 --bess lfp:5:5:10 --out results/bess-day

=== Historical state estimate: case9 over synthetic-case9 ===
  reconstructed 48 timestamps  (2024-01-01T00:00:00.0000000Z .. 2024-01-01T23:30:00.0000000Z)
  non-converged: 0
  system loss over the series: 1.6 .. 4.6 MW
  COUNTERFACTUAL (Counterfactual): hypothetical storage on recorded history - not a reconstruction.
  LFP 5MW/10MWh @5: out 9.6 MWh, in 5.3 MWh, aux 0.27 MWh, stored change -5.0 MWh, round trip 93.1 %, 0.75 equivalent full cycles
      ageing over the run: calendar 0.1365 %, cycle 0.0026 % (rainflow, depth-aware: 0.0020 %), deepest cycle 90 % SoC

  -> results\bess-day
[exit 0]
```

`--synth` builds a synthetic daily load shape so the command runs without recorded data. With `--series <ts.json>` the
replay runs on a real series, as in [ch. 22](<0022 Estimating GB history.md>). The differences from `chain`:

- **Policies are `idle`, `peak-shave` and `hold`.** `gda-replay` takes no price series, so `price` and `lp` exit 2:
  `--policy price needs a price series, and gda-replay takes none; use idle, peak-shave or hold.`
- **Step length comes from the series.** Each step is the gap to the next timestamp, the last step reuses the
  previous gap, and a one-step series uses 0.5 h. A replay with `--step-min 120` runs the battery in 2 h steps and
  books its standby load accordingly (0.12 MWh over six steps in `docs/_captures/bess/gda-replay-hourly.txt`).
  The hour of day the policy sees is the UTC clock time of the timestamp. `chain` has `--step-hours`;
  `gda-replay` takes its step length from the series.
- **The peak-shave thresholds** are 90 % and 70 % of the case's total load times the largest load factor in the
  series. `--shave-high` and `--shave-low` apply.
- **The output is a summary, not a step table.** The console shows the state-estimate lines (timestamps
  reconstructed, non-converged count, the range of system losses), the `COUNTERFACTUAL` line, and the two summary
  lines for each site. The step-by-step values are in the files below.
- **Provenance.** The result is tagged `Counterfactual`. The counterfactual CSV is not a time-series file that `--series` reads, so it cannot
  feed another replay. The line
  `COUNTERFACTUAL (Counterfactual): hypothetical storage on recorded history - not a reconstruction.` is the tag.

`gda-replay` writes to the directory given by `--out` (default `results/gda-replay-<case>`): `state-estimate.csv`,
`run-metadata.json`, `exports/`, and, when `--bess` is given, `storage-counterfactual.csv`:

```
$ head -8 results/bess-gda/storage-counterfactual.csv
# COUNTERFACTUAL: hypothetical storage on recorded history (ProvenanceTag.Counterfactual)
index,site,request_mw,delivered_mw,aux_mw,soc,temp_c,limited
0,NMC 5MW/10MWh @5,1.000,1.000,0.0100,0.70571,24.80,0
1,NMC 5MW/10MWh @5,1.000,1.000,0.0100,0.66100,24.62,0
2,NMC 5MW/10MWh @5,1.000,1.000,0.0100,0.61586,24.45,0
3,NMC 5MW/10MWh @5,1.000,1.000,0.0100,0.57030,24.29,0
4,NMC 5MW/10MWh @5,1.000,1.000,0.0100,0.52429,24.13,0
5,NMC 5MW/10MWh @5,0.000,0.000,0.0100,0.52428,24.17,0
[exit 0]
```

Columns: `index`, `site`, `request_mw`, `delivered_mw`, `aux_mw`, `soc`, `temp_c` and `limited` (1 if a limit bound).
The first line is a comment that marks the file as counterfactual. Exit code 3 means a step did not converge.

## Refusals and exit codes

Every refusal below prints one line to standard error and exits 2, except where the table says 1.

| Cause | Message | Exit |
|---|---|---|
| `--bess` with no value | `error: --bess expects chemistry:bus:MW:MWh[:soc]` | 2 |
| Not four or five fields | `error: chain storage: --bess 'lfp:5:5': expected chemistry:bus:MW:MWh[:soc] (4 or 5 fields separated by ':'), got 3.` | 2 |
| Unknown chemistry | `error: chain storage: --bess 'unknown-chem:5:5:10': unknown chemistry 'unknown-chem' (lfp, nmc, lto, na-ion, vrfb, lead-acid).` | 2 |
| Bus not a number | `error: chain storage: --bess '...': bus 'abc' is not a bus number.` | 2 |
| MW or MWh not a number above 0 | `error: chain storage: --bess '...': <MW or MWh> '<field>' must be a number above 0.` | 2 |
| Starting SoC outside 0 to 1 | `error: chain storage: --bess '...': starting state of charge '1.5' must be a number from 0 to 1.` | 2 |
| Bus not in the case | `error: chain storage: storage site 'LFP 5MW/10MWh @99' is at unknown bus 99.` | 2 |
| Unknown policy | `error: chain storage: --policy 'magic': expected idle, peak-shave, price, hold or lp` | 2 |
| `--policy price` or `lp` without prices | `error: chain storage: --policy lp needs --prices` | 2 |
| `--prices` with no value | `error: chain storage: --prices expects a value: 'synthetic' or a price file (one GBP/MWh value per step).` | 2 |
| A price file missing, unreadable, short or with a bad value | `error: chain storage: --prices <file>: ...` naming the file and the line | 1 |
| A storage flag without `--bess` | `error: chain --policy is a storage flag: it needs at least one --bess site ...` | 2 |
| `--prices` on `gda-replay` | `error: --prices applies to dcopf, acopf and chain (with --bess) only (got 'gda-replay').` | 2 |
| Unknown `--mode` | `error: --mode expected 'fixed', 'ramp' or 'daily' but got 'weird'` | 2 |
| Unknown case name | the shared unknown-case message | 2 |

Three captures show the usual ones:

```
$ gridsim chain case9 --steps 2 --bess malformed-spec
error: chain storage: --bess 'malformed-spec': expected chemistry:bus:MW:MWh[:soc] (4 or 5 fields separated by ':'), got 1.
[exit 2]
```

```
$ gridsim chain case9 --steps 4 --bess unknown-chem:5:5:10
error: chain storage: --bess 'unknown-chem:5:5:10': unknown chemistry 'unknown-chem' (lfp, nmc, lto, na-ion, vrfb, lead-acid).
[exit 2]
```

```
$ gridsim chain case9 --steps 2 --bess lfp:99:5:10
error: chain storage: storage site 'LFP 5MW/10MWh @99' is at unknown bus 99.
[exit 2]
```

On `gda-replay` the same site errors read `error: gda-replay storage: ...`, and `--policy lp` and `--policy price` exit
2 because the verb takes no prices:

```
$ gridsim gda-replay --synth --case case9 --steps 4 --bess lfp:5:5:10 --policy lp
error: gda-replay storage: --policy lp needs a price series, and gda-replay takes none; use idle, peak-shave or hold.
[exit 2]
```

The run exit codes are:

- **0**: every step converged.
- **3**: at least one step did not converge, or the `lp` schedule LP did not solve to an optimum. A step that did not
  converge shows `N` in `Conv` and `-` in `Slack MW` and `Vmin`, and the footer reads `WARNING: N step(s) did not
  converge.` Nothing that did not converge is reported as success.

```
$ gridsim chain case9 --steps 3 --bess lfp:5:20000:40000:0.95 --shave-high -30000 --shave-low -40000
GridSim - storage time series  *  case9  *  3 x 0.5 h  *  mode=fixed  *  policy=peak-shave
prices: none.  Chemistry presets are CuratedPlaceholder [VERIFY] (representative cells).

...
|---:|---:|---:|---:|---:|---|---:|---:|:-:|
| 0 | 0.0 | 1.000 | 315 | - | 20000.0 / 0.720 / 26.9 | - | - | N |
| 1 | 0.5 | 1.000 | 315 | - | 20000.0 / 0.488 / 28.8 | - | - | N |
| 2 | 1.0 | 1.000 | 315 | - | 20000.0 / 0.253 / 30.0 | - | - | N |

LFP 20000MW/40000MWh @5: out 30000.0 MWh, in 0.0 MWh, aux 73.54 MWh, stored change -31136.6 MWh, round trip -, 0.38 equivalent full cycles
    ageing over the run: calendar 0.0316 %, cycle 0.0013 % (rainflow, depth-aware: 0.0010 %), deepest cycle 70 % SoC
WARNING: 3 step(s) did not converge.
[chain] done -> results\chain-storage-case9.md
[exit 3]
```

That run asks a single 20 GW / 40 GWh battery at bus 5 to inject 20 GW into a 315 MW case, which the power flow
cannot solve. It shows the exit code, not a realistic study.

`gda-replay` also exits 2 when the series is rejected by the forward-inference guard or a `--bess` site is on a bus the
replay case does not have (`storage site '...' is at unknown bus N.`), and 1 when the case or series file cannot be
loaded.

## Pitfalls

- **`--prices` makes `lp` the default policy.** Give `--policy` as well when you want another one.
- **The synthetic price day is not a market record.** Quote a revenue figure only from a price file you can source.
- **A bus number is the case's own number, not an index.** `case9` runs 1 to 9. Use the bus ids the case prints.
- **The pack is rounded to whole strings.** The pack is sized to a series count (1000 V divided by the cell's nominal
  voltage, rounded) and a parallel count (energy divided by one string's energy, rounded), so the nominal energy is
  close to, not equal to, the `MWh` you gave. `10` MWh of LFP is 40 strings of 304 cells (12160 cells), 10.0247 MWh. The cycle count
  is measured against that nominal energy. The `MW` rating is exact.
- **A small battery is held by its C-rate, not its rating.** A site whose `MW` exceeds its C-rate limit on its
  `MWh` (a 5 MW / 4 MWh lead-acid battery) is limited to the C-rate and reports `current limit`. Raise the `MWh`
  or lower the `MW`.
- **The step length changes the story.** In `daily` mode the step is `24 / steps` hours, in `fixed` and `ramp` mode it
  is 0.5 h, unless `--step-hours` is given. A 4 h step sees the SoC window edge mid-step and reports a low mean power. A 0.5 h step does not.
  `--steps` defaults to 48 with a storage site, not 50.
- **`fixed` mode holds demand at the peak.** The load multiplier is 1.0 every step, so the peak-shave default
  thresholds (283.5 MW and 220.5 MW on `case9`) put the demand above the high threshold at every step. The battery
  discharges to its floor in the first steps and then stays there. Use `--mode daily` for a battery study.
- **The starting SoC is checked against 0 to 1, not against the chemistry's window.** A value inside 0 to 1 but outside
  the chemistry's window (for LFP, outside 0.05 to 0.95) starts the site outside the window, where it can only move
  toward it.
- **Threshold flags act under their own policy only.** `--shave-high` and `--shave-low` act under `peak-shave`, and
  `--buy-below` and `--sell-above` under `price`; under another policy they change nothing.
- **`stored change` reads `+0.0` on an idle battery.** Self-discharge leaves a change too small to show at one decimal.
- **Calendar fade over a short run is front-loaded.** A day on a fresh pack reads 0.0259 % to 0.4087 % across the six
  chemistries above (0.1369 % for LFP) because the square-root law gives one day `sqrt(1/365)`, 5.2 %, of a year's fade at constant stress (LFP: 0.1369 % for
  the day, 1.91 % for a year at 25 C and 50 % SoC). Do not scale a one-day figure linearly to a year.

## Limits

- `gridsim gda-replay` takes no price series, so its storage runs `idle`, `peak-shave` or `hold` only.
- All sites in a run share one policy and one set of thresholds, and the demand is scaled uniformly across every load in the case.
- The converter efficiency (0.98 one way), the pack DC voltage (1000 V) and the ambient temperature (25 C) are fixed; no flag changes them.
- A storage site injects real power only: its reactive range is zero, and it holds the voltage set-point its bus already had.
- `hold` keeps the SoC in a band for a response service; the study does not simulate the response itself.

## See also

- [Ch. 11, Contingency analysis](<0011 Contingency analysis.md>), where `chain` and its plain, no-battery form live.
- [Ch. 22, Estimating GB history](<0022 Estimating GB history.md>), where `gda-replay` lives.
- [Ch. 20, Counterfactuals](<0020 Counterfactuals.md>), for the replayed-event counterfactuals, a different family from the storage counterfactual here.
- [Technical ch. 27, Battery energy storage](<../technical/0027 Battery energy storage.md>), for the cell, pack, ageing and dispatch model.
- [Ch. 19, CLI reference: `chain`](<0019 CLI reference.md#chain>) and [`gda-replay`](<0019 CLI reference.md#gda-replay>) for every flag.
