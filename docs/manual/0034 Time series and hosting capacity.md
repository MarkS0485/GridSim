# 34. Time series and hosting capacity

A single power flow answers "what happens at this instant". Many questions are about a day or a year instead: how often
a tap changer works, how long a feeder sits above its voltage limit, how much PV energy volt-watt curtails, whether a
battery runs out before the evening peak. `gridsim qsts` answers them with a quasi-static time series (QSTS): one power
flow per step over load, generation, PV and storage profiles, warm-started from the step before, with tap changers,
capacitor banks, inverters and storage acting in time and carrying their state from step to step. `gridsim hosting`
builds on it to answer a planning question: how much PV (or new demand) can each bus take before a limit breaks.

## Before you start

- A case: any built-in name, MATPOWER file, JSON case folder or PSS/E file (ch. 2). The examples use the 33-bus
  Baran-Wu feeder `case33bw.m`, which ships with GridSim.
- A study file (`gridsim-qsts/1`, JSON): the step length and count, the profiles, and the devices and controls to add.
  Every key is checked and an unknown key is refused. The full schema is in the
  [`qsts` section of the CLI reference](<0019 CLI reference.md#qsts>). The study files used below are in the repository
  at `docs/_captures/qsts/study33.json` (a winter-style day with PV, a battery and a capacitor bank) and
  `docs/_captures/qsts/summer33.json` (a light summer day with no devices).

## Worked example

`study33.json` runs 48 half-hour steps of a daily load shape over `case33bw.m`, with a 1.5 MW PV unit at bus 18 on
IEEE 1547-2018 Category B volt-var (5 s response), a 0.3 MW / 1.2 MWh battery at bus 33 covering its own bus's demand,
and a three-step 0.3 MVAr capacitor bank at bus 30 that closes below 0.93 pu after a 60 s delay:

```json
{
  "format": "gridsim-qsts/1",
  "stepSeconds": 1800, "steps": 48,
  "profiles": {
    "load": { "stepSeconds": 3600, "interpolation": "linear", "wrap": true,
              "values": [0.55,0.5,0.48,0.47,0.48,0.52,0.62,0.75,0.82,0.85,0.86,0.86,0.85,0.84,0.84,0.86,0.92,1.0,1.0,0.95,0.88,0.78,0.68,0.6] },
    "solar": { "stepSeconds": 3600, "wrap": true,
               "values": [0,0,0,0,0,0,0.05,0.2,0.4,0.6,0.75,0.85,0.9,0.85,0.75,0.6,0.4,0.2,0.05,0,0,0,0,0] }
  },
  "loads": [ { "buses": "all", "profile": "load" } ],
  "pv": [ { "name": "PV18", "bus": 18, "ratedMw": 1.5, "ratedMva": 1.65, "profile": "solar",
            "control": { "function": "volt-var", "voltVar": "1547-B", "responseSeconds": 5 } } ],
  "storage": [ { "name": "B33", "bus": 33, "ratedMw": 0.3, "capacityMwh": 1.2, "initialSoc": 0.5, "socMin": 0.1, "socMax": 0.9,
                 "chargeEfficiency": 0.95, "dischargeEfficiency": 0.95, "dispatch": { "mode": "self-consumption" } } ],
  "capacitors": [ { "name": "C30", "bus": 30, "stepMvar": 0.3, "maxSteps": 3, "control": "voltage",
                    "onPu": 0.93, "offPu": 1.0, "delaySeconds": 60 } ],
  "limits": { "vminPu": 0.94, "vmaxPu": 1.05 }
}
```

```bash
gridsim qsts case33bw.m --study docs/_captures/qsts/study33.json --top 5
```

```
$ gridsim qsts case33bw.m --study docs/_captures/qsts/study33.json --top 5
QSTS - case33bw (33 buses, 32 branches), engine Backward-forward sweep, mode timed
  48 step(s) of 1800 s (24:00:00), 48 solved in 0.18 s wall (263.2 steps/s)
  power-flow solves 1276, iterations 7291; unsettled steps 0
  energy (MWh): load 66.721, losses 2.171, source 58.536, PV 9.900 of 9.900 available (curtailed 0.000), storage charge 0.000 / discharge 0.456
  voltage range 0.9245 pu (bus 33, t = 61200 s) to 1.0197 pu (bus 18, t = 45000 s); limits 0.940-1.050 pu
  tap operations 0, capacitor operations 1
      bank C30                       1
      storage B33               final SOC 0.1000
  buses outside their voltage limits: 5; branches above 100 %: 0
      bus 33       above    0:00:00 (max 0.9649), below    5:00:00 (min 0.9245), pu.h above 0.00000 below 0.03851
      bus 32       above    0:00:00 (max 0.9649), below    5:00:00 (min 0.9248), pu.h above 0.00000 below 0.03718
      bus 31       above    0:00:00 (max 0.9653), below    4:30:00 (min 0.9257), pu.h above 0.00000 below 0.03330
      bus 30       above    0:00:00 (max 0.9670), below    4:00:00 (min 0.9298), pu.h above 0.00000 below 0.01675
      bus 29       above    0:00:00 (max 0.9685), below    3:00:00 (min 0.9333), pu.h above 0.00000 below 0.00651
note: case33bw.m: nominal frequency not given (MATPOWER files carry none); assumed 50 Hz - pass the real value if this is, for example, a 60 Hz system (gridsim: --frequency-hz 60).
note: case33bw.m: the file has 2 indexed assignment(s) after the matrices (mpc.branch, mpc.bus); MATLAB statements are not run, so they are not applied (a raw-unit distribution feeder is converted by the unit normaliser instead).
note: case33bw.m: read mpc.gencost (1 row(s)); the costs are carried on the generators and used by dcopf / acopf when asked (--costs case); without it the OPF uses the fuel-type merit order.
note: case33bw.m: skipped 5 out-of-service branch(es).
note: case33bw.m: raw engineering units detected (branch impedance in ohms, demand in kW / kVAr, as in the published distribution feeders); converted to per-unit and MW: R and X divided by Zbase = baseKV^2 / baseMVA of the from bus, Pd, Qd, Gs and Bs divided by 1000.
[exit 0]
```

The wall time and steps per second change with the machine and the run; every other number is the same on every run
and every machine (the digest in the result file pins them, see below).

## Reading the output

- **Header.** The case, its size, the power-flow engine the case picked (the backward/forward sweep here, because the
  feeder is radial; meshed cases use the Newton engine) and the control mode (`timed`, the default, or `steady`).
- **Steps.** How many steps of what length, the simulated span (h:mm:ss) and how many solved, then the wall time.
- **Solves and iterations.** Every power flow the run made: one per step plus one after each round of control actions
  (an inverter settling, a bank switching, a tap moving). 1276 solves for 48 steps come mostly from the volt-var unit
  and the battery settling to a fixed point at each step. `unsettled steps` counts steps whose controls were still acting when the round limit was
  reached; any unsettled step makes the run exit 3 (the result file is still written) unless `--allow-unsettled`
  is given.
- **Energy.** Over the whole run, in MWh: the demand served, the network losses, the energy drawn through the source
  (slack), the PV energy delivered out of what was available (the difference is the curtailment by volt-watt or by
  var priority), and the storage charge and discharge.
- **Voltage range.** The lowest and highest bus voltage of the run, with the bus and the time, and the limits the run is
  judged against (the study's `limits` block; 0.94-1.05 pu here).
- **Operations.** Tap and capacitor operations in total, then per device with its count; a storage unit shows its final
  state of charge. Here the bank switched once (it closed at 17:30, step 35) and the battery ended at its 0.1 floor.
- **Violations.** How many buses left their voltage limits and how many branches went above the loading limit, then the
  `--top` worst buses: the time above and below the limit, the extreme voltage, and the integral of the excess in pu
  hours (`pu.h`), which weighs a small excursion for a long time against a large one for a short time.

The result file (`--json`, `--csv` or `--out`) holds everything the report summarises:

```bash
gridsim qsts case33bw.m --study docs/_captures/qsts/study33.json --top 0 --csv results/qsts-case33bw-day.csv
```

```
$ gridsim qsts case33bw.m --study docs/_captures/qsts/study33.json --top 0 --csv results/qsts-case33bw-day.csv
QSTS - case33bw (33 buses, 32 branches), engine Backward-forward sweep, mode timed
  48 step(s) of 1800 s (24:00:00), 48 solved in 0.19 s wall (247.7 steps/s)
  power-flow solves 1276, iterations 7291; unsettled steps 0
  energy (MWh): load 66.721, losses 2.171, source 58.536, PV 9.900 of 9.900 available (curtailed 0.000), storage charge 0.000 / discharge 0.456
  voltage range 0.9245 pu (bus 33, t = 61200 s) to 1.0197 pu (bus 18, t = 45000 s); limits 0.940-1.050 pu
  tap operations 0, capacitor operations 1
      bank C30                       1
      storage B33               final SOC 0.1000
  buses outside their voltage limits: 5; branches above 100 %: 0
      ... (--top N lists more; the result file has all)
note: case33bw.m: nominal frequency not given (MATPOWER files carry none); assumed 50 Hz - pass the real value if this is, for example, a 60 Hz system (gridsim: --frequency-hz 60).
note: case33bw.m: the file has 2 indexed assignment(s) after the matrices (mpc.branch, mpc.bus); MATLAB statements are not run, so they are not applied (a raw-unit distribution feeder is converted by the unit normaliser instead).
note: case33bw.m: read mpc.gencost (1 row(s)); the costs are carried on the generators and used by dcopf / acopf when asked (--costs case); without it the OPF uses the fuel-type merit order.
note: case33bw.m: skipped 5 out-of-service branch(es).
note: case33bw.m: raw engineering units detected (branch impedance in ohms, demand in kW / kVAr, as in the published distribution feeders); converted to per-unit and MW: R and X divided by Zbase = baseKV^2 / baseMVA of the from bus, Pd, Qd, Gs and Bs divided by 1000.
[qsts] wrote results\qsts-case33bw-day.csv, results\qsts-case33bw-day-devices.csv, results\qsts-case33bw-day-operations.csv, results\qsts-case33bw-day-bus_violations.csv, results\qsts-case33bw-day-branch_violations.csv, results\qsts-case33bw-day-taps.csv, results\qsts-case33bw-day-banks.csv, results\qsts-case33bw-day-pv.csv, results\qsts-case33bw-day-storage.csv, results\qsts-case33bw-day-voltages.csv, results\qsts-case33bw-day-loadings.csv, results\qsts-case33bw-day-summary.csv
[exit 0]
```

| File (CSV) or table (JSON) | Columns | What it holds |
|---|---|---|
| main file (`steps`) | `step, time_s, settled, solves, iterations, vmin_pu, vmin_bus, vmax_pu, vmax_bus, max_loading_pct, max_loading_branch, loss_mw, loss_mvar, load_mw, source_mw, pv_available_mw, pv_mw, pv_mvar, storage_mw` | one row per step |
| `devices` | `kind, name, operations` | operation count per tap changer and bank |
| `operations` | `step, time_s, kind, device, from, to, sensed, control_iteration` | every operation, with the quantity the controller sensed when it acted |
| `bus_violations` | `bus_id, seconds_above, seconds_below, pu_seconds_above, pu_seconds_below, max_pu, min_pu` | every bus that left its limits |
| `branch_violations` | `branch, name, seconds_over, percent_seconds_over, max_percent` | every branch that went above the loading limit |
| `taps`, `banks` | `step, transformer, tap` / `step, bank, steps` | the device state at every step |
| `pv`, `storage` | `step, unit, p_mw, q_mvar` / `step, unit, p_mw, soc_start` | the inverter set points and the battery power and state of charge |
| `voltages`, `loadings` | `step, bus_id, vm_pu` / `step, branch, loading_pct` | the full state at every step (omitted with `"recordStates": false`) |
| `summary` | `key, value` | the totals of the report and the run's `digest` |

The `digest` is a SHA-256 over every number of the run in a fixed order. Two runs with the same digest agree to the
last bit; the tests pin it and require it to be identical on every CPU instruction set.

## Hosting capacity

`gridsim hosting` finds, for each candidate bus on its own, the largest PV unit (or added load) the bus can take before
a limit breaks, by bisection on the size. The time-series method runs the whole study with the resource added, driven
by one of the study's profiles. On the light summer day, PV at three buses of the feeder:

```bash
gridsim hosting case33bw.m --method qsts --study docs/_captures/qsts/summer33.json --resource-profile solar --buses 6,18,33 --max-mw 8 --tolerance-mw 0.01
```

```
$ gridsim hosting case33bw.m --method qsts --study docs/_captures/qsts/summer33.json --resource-profile solar --buses 6,18,33 --max-mw 8 --tolerance-mw 0.01
Hosting capacity (PV, qsts method) - case33bw (33 buses), engine Backward-forward sweep
  3 candidate bus(es), search 0..8 MW to 0.01 MW, 33 size evaluations in 0.29 s (1 thread(s))
  time series: 48 step(s) of 1800 s, resource profile solar, allowed time outside limits 0 s
  limits: 0.94-1.05 pu, loading 100 %, no reverse-power check
  bus        hosting MW  fails at MW  limiting constraint
  18             1.5703       1.5781  overvoltage: bus 18 at 1.0502 pu for 1800 s
  33             2.5703       2.5781  overvoltage: bus 33 at 1.0502 pu for 1800 s
  6              6.4375       6.4453  overvoltage: bus 6 at 1.0501 pu for 1800 s
note: case33bw.m: nominal frequency not given (MATPOWER files carry none); assumed 50 Hz - pass the real value if this is, for example, a 60 Hz system (gridsim: --frequency-hz 60).
note: case33bw.m: the file has 2 indexed assignment(s) after the matrices (mpc.branch, mpc.bus); MATLAB statements are not run, so they are not applied (a raw-unit distribution feeder is converted by the unit normaliser instead).
note: case33bw.m: read mpc.gencost (1 row(s)); the costs are carried on the generators and used by dcopf / acopf when asked (--costs case); without it the OPF uses the fuel-type merit order.
note: case33bw.m: skipped 5 out-of-service branch(es).
note: case33bw.m: raw engineering units detected (branch impedance in ohms, demand in kW / kVAr, as in the published distribution feeders); converted to per-unit and MW: R and X divided by Zbase = baseKV^2 / baseMVA of the from bus, Pd, Qd, Gs and Bs divided by 1000.
[hosting] --reverse-branch auto: no named branch leaves the slack bus, so there is no reverse-power check (name one with --reverse-branch).
[exit 0]
```

Reading it:

- **Search.** Three candidates, a search from 0 to 8 MW to a 0.01 MW bracket: 8 MW is tried first, then the bracket is
  halved until it is at most 0.01 MW wide (eleven sizes per candidate, 33 in all).
- **Time series.** The study's steps, the profile that drives the resource, and the time a limit may be broken before it
  counts (`--allowed-violation-s`, 0 here).
- **Limits.** The study's voltage limits and the loading limit; the reverse-power check needs a named branch leaving
  the source, which `case33bw.m` does not have (the note at the end says so).
- **Rows**, lowest capacity first. `hosting MW` is the largest size that passed; `fails at MW` is the smallest size that
  failed; the true limit lies between the two. The limiting constraint names the element, the value and how long it
  lasted. Here every bus is limited by its own over-voltage at the midday sun: 1.5703 MW at bus 18 (the far end of the
  main feeder), 2.5703 MW at bus 33 (the end of a lateral) and 6.4375 MW at bus 6, close to the source.

Inverter control changes the answer. The same study with the PV on Category B volt-var and 1.1 MVA of inverter per MW:

```bash
gridsim hosting case33bw.m --method qsts --study docs/_captures/qsts/summer33.json --resource-profile solar --buses 6,18,33 --max-mw 8 --tolerance-mw 0.01 --pv-function volt-var --mva-per-mw 1.1
```

```
$ gridsim hosting case33bw.m --method qsts --study docs/_captures/qsts/summer33.json --resource-profile solar --buses 6,18,33 --max-mw 8 --tolerance-mw 0.01 --pv-function volt-var --mva-per-mw 1.1
Hosting capacity (PV, qsts method) - case33bw (33 buses), engine Backward-forward sweep
  3 candidate bus(es), search 0..8 MW to 0.01 MW, 23 size evaluations in 2.16 s (1 thread(s))
  time series: 48 step(s) of 1800 s, resource profile solar, allowed time outside limits 0 s
  limits: 0.94-1.05 pu, loading 100 %, no reverse-power check
  bus        hosting MW  fails at MW  limiting constraint
  18             2.1719       2.1797  overvoltage: bus 18 at 1.0501 pu for 1800 s
  33             3.5234       3.5313  overvoltage: bus 33 at 1.0500 pu for 1800 s
  6                   8           >8  none within the search range
note: case33bw.m: nominal frequency not given (MATPOWER files carry none); assumed 50 Hz - pass the real value if this is, for example, a 60 Hz system (gridsim: --frequency-hz 60).
note: case33bw.m: the file has 2 indexed assignment(s) after the matrices (mpc.branch, mpc.bus); MATLAB statements are not run, so they are not applied (a raw-unit distribution feeder is converted by the unit normaliser instead).
note: case33bw.m: read mpc.gencost (1 row(s)); the costs are carried on the generators and used by dcopf / acopf when asked (--costs case); without it the OPF uses the fuel-type merit order.
note: case33bw.m: skipped 5 out-of-service branch(es).
note: case33bw.m: raw engineering units detected (branch impedance in ohms, demand in kW / kVAr, as in the published distribution feeders); converted to per-unit and MW: R and X divided by Zbase = baseKV^2 / baseMVA of the from bus, Pd, Qd, Gs and Bs divided by 1000.
[hosting] --reverse-branch auto: no named branch leaves the slack bus, so there is no reverse-power check (name one with --reverse-branch).
[exit 0]
```

Absorbing reactive power above 1.02 pu raises the capacity by 38 % at bus 18 (2.1719 MW) and 37 % at bus 33
(3.5234 MW); bus 6 takes the whole 8 MW range.

## Variations

The snapshot method (the default) needs no study file: one regulated power flow at minimum demand (0.3 of the case
demand) with full sun and one at full demand, every control delay taken as zero. It is quicker and, at minimum demand
and full sun, no less conservative than the time series:

```bash
gridsim hosting case33bw.m --buses 6,18,33 --max-mw 10 --tolerance-mw 0.01
```

```
$ gridsim hosting case33bw.m --buses 6,18,33 --max-mw 10 --tolerance-mw 0.01
Hosting capacity (PV, snapshot method) - case33bw (33 buses), engine Backward-forward sweep
  3 candidate bus(es), search 0..10 MW to 0.01 MW, 33 size evaluations in 0.09 s (1 thread(s))
  snapshots (demand x, resource x): (0.3, 1), (1, 1)
  limits: 0.94-1.06 pu, loading 100 %, no reverse-power check
  the base case already breaks 16 limit(s) (counted only where the resource makes them worse): bus 9 at 0.9351 pu for 1 s; bus 10 at 0.9292 pu for 1 s; bus 11 at 0.9284 pu for 1 s ...
  bus        hosting MW  fails at MW  limiting constraint
  18             1.3184       1.3281  overvoltage: bus 18 at 1.0603 pu for 1 s
  33              2.168       2.1777  overvoltage: bus 33 at 1.0600 pu for 1 s
  6              5.8887       5.8984  overvoltage: bus 6 at 1.0601 pu for 1 s
note: case33bw.m: nominal frequency not given (MATPOWER files carry none); assumed 50 Hz - pass the real value if this is, for example, a 60 Hz system (gridsim: --frequency-hz 60).
note: case33bw.m: the file has 2 indexed assignment(s) after the matrices (mpc.branch, mpc.bus); MATLAB statements are not run, so they are not applied (a raw-unit distribution feeder is converted by the unit normaliser instead).
note: case33bw.m: read mpc.gencost (1 row(s)); the costs are carried on the generators and used by dcopf / acopf when asked (--costs case); without it the OPF uses the fuel-type merit order.
note: case33bw.m: skipped 5 out-of-service branch(es).
note: case33bw.m: raw engineering units detected (branch impedance in ohms, demand in kW / kVAr, as in the published distribution feeders); converted to per-unit and MW: R and X divided by Zbase = baseKV^2 / baseMVA of the from bus, Pd, Qd, Gs and Bs divided by 1000.
[hosting] --reverse-branch auto: no named branch leaves the slack bus, so there is no reverse-power check (name one with --reverse-branch).
[exit 0]
```

The base case of `case33bw.m` at full demand already breaks 16 limits (buses below 0.94 pu). Those limits count only where the
resource makes them strictly worse, so they neither mask the capacity nor are blamed on the PV; the report lists them.

Added load (electric vehicle charging, heat pumps) instead of PV:

```bash
gridsim hosting case33bw.m --resource load --buses 18,33 --max-mw 10 --tolerance-mw 0.01
```

```
$ gridsim hosting case33bw.m --resource load --buses 18,33 --max-mw 10 --tolerance-mw 0.01
Hosting capacity (added load, snapshot method) - case33bw (33 buses), engine Backward-forward sweep
  2 candidate bus(es), search 0..10 MW to 0.01 MW, 22 size evaluations in 0.07 s (1 thread(s))
  snapshots (demand x, resource x): (1, 1)
  limits: 0.94-1.06 pu, loading 100 %, no reverse-power check
  the base case already breaks 16 limit(s) (counted only where the resource makes them worse): bus 9 at 0.9351 pu for 1 s; bus 10 at 0.9292 pu for 1 s; bus 11 at 0.9284 pu for 1 s ...
  bus        hosting MW  fails at MW  limiting constraint
  18                  0       0.0098  undervoltage: bus 9 at 0.9348 pu for 1 s
  33                  0       0.0098  undervoltage: bus 9 at 0.9349 pu for 1 s
note: case33bw.m: nominal frequency not given (MATPOWER files carry none); assumed 50 Hz - pass the real value if this is, for example, a 60 Hz system (gridsim: --frequency-hz 60).
note: case33bw.m: the file has 2 indexed assignment(s) after the matrices (mpc.branch, mpc.bus); MATLAB statements are not run, so they are not applied (a raw-unit distribution feeder is converted by the unit normaliser instead).
note: case33bw.m: read mpc.gencost (1 row(s)); the costs are carried on the generators and used by dcopf / acopf when asked (--costs case); without it the OPF uses the fuel-type merit order.
note: case33bw.m: skipped 5 out-of-service branch(es).
note: case33bw.m: raw engineering units detected (branch impedance in ohms, demand in kW / kVAr, as in the published distribution feeders); converted to per-unit and MW: R and X divided by Zbase = baseKV^2 / baseMVA of the from bus, Pd, Qd, Gs and Bs divided by 1000.
[hosting] --reverse-branch auto: no named branch leaves the slack bus, so there is no reverse-power check (name one with --reverse-branch).
[exit 0]
```

At full demand the feeder has no room: the smallest step (0.0098 MW) already deepens the existing under-voltage at
bus 9, so the capacity is 0.

Other common forms (each flag is described in the [`hosting` section of the CLI reference](<0019 CLI reference.md#hosting>)):

- `--threads N` studies N candidates at once; the answers are the same as with one thread.
- `--allowed-violation-s 1800` lets a limit be broken for up to half an hour before it counts.
- `--tap-increase-pct 20` adds the tap-wear criterion: at most 20 % more tap operations than the base run.
- `--criteria overvoltage,thermal` judges only the listed constraints.
- `"mode": "steady"` in a study file makes every control settle instantly at each step (the case-solve tap loop), for
  comparison with the timed controls.

## Pitfalls

- **A study that names something the case does not have is refused**, with every problem listed, and exits 1:

  ```bash
  gridsim qsts case33bw.m --study docs/_captures/qsts/bad-study.json
  ```

  ```
  $ gridsim qsts case33bw.m --study docs/_captures/qsts/bad-study.json
  note: case33bw.m: nominal frequency not given (MATPOWER files carry none); assumed 50 Hz - pass the real value if this is, for example, a 60 Hz system (gridsim: --frequency-hz 60).
  note: case33bw.m: the file has 2 indexed assignment(s) after the matrices (mpc.branch, mpc.bus); MATLAB statements are not run, so they are not applied (a raw-unit distribution feeder is converted by the unit normaliser instead).
  note: case33bw.m: read mpc.gencost (1 row(s)); the costs are carried on the generators and used by dcopf / acopf when asked (--costs case); without it the OPF uses the fuel-type merit order.
  note: case33bw.m: skipped 5 out-of-service branch(es).
  note: case33bw.m: raw engineering units detected (branch impedance in ohms, demand in kW / kVAr, as in the published distribution feeders); converted to per-unit and MW: R and X divided by Zbase = baseKV^2 / baseMVA of the from bus, Pd, Qd, Gs and Bs divided by 1000.
  [qsts] QSTS study refused (2 problems):
    - loads entry 1: profile 'x' is not defined.
    - loads entry 1: bus 999 is not in the case.
  [exit 1]
  ```

- **A discrete control in the study can set the hosting capacity.** With `study33.json` and the time-series method, PV
  at bus 33 is limited at 0 MW (`fails at` 0.0313 MW) by an under-voltage at bus 13, a bus that is inside its limits in
  the base run:

  ```bash
  gridsim hosting case33bw.m --method qsts --study docs/_captures/qsts/study33.json --resource-profile solar --buses 18,33 --max-mw 8 --tolerance-mw 0.05
  ```

  ```
  $ gridsim hosting case33bw.m --method qsts --study docs/_captures/qsts/study33.json --resource-profile solar --buses 18,33 --max-mw 8 --tolerance-mw 0.05
  Hosting capacity (PV, qsts method) - case33bw (33 buses), engine Backward-forward sweep
    2 candidate bus(es), search 0..8 MW to 0.05 MW, 18 size evaluations in 1.48 s (1 thread(s))
    time series: 48 step(s) of 1800 s, resource profile solar, allowed time outside limits 0 s
    limits: 0.94-1.05 pu, loading 100 %, no reverse-power check
    the base case already breaks 5 limit(s) (counted only where the resource makes them worse): bus 29 at 0.9333 pu for 10800 s; bus 30 at 0.9298 pu for 14400 s; bus 31 at 0.9257 pu for 16200 s ...
    bus        hosting MW  fails at MW  limiting constraint
    33                  0       0.0313  undervoltage: bus 13 at 0.9391 pu for 1800 s
    18             0.0625       0.0938  undervoltage: bus 13 at 0.9392 pu for 1800 s
  note: case33bw.m: nominal frequency not given (MATPOWER files carry none); assumed 50 Hz - pass the real value if this is, for example, a 60 Hz system (gridsim: --frequency-hz 60).
  note: case33bw.m: the file has 2 indexed assignment(s) after the matrices (mpc.branch, mpc.bus); MATLAB statements are not run, so they are not applied (a raw-unit distribution feeder is converted by the unit normaliser instead).
  note: case33bw.m: read mpc.gencost (1 row(s)); the costs are carried on the generators and used by dcopf / acopf when asked (--costs case); without it the OPF uses the fuel-type merit order.
  note: case33bw.m: skipped 5 out-of-service branch(es).
  note: case33bw.m: raw engineering units detected (branch impedance in ohms, demand in kW / kVAr, as in the published distribution feeders); converted to per-unit and MW: R and X divided by Zbase = baseKV^2 / baseMVA of the from bus, Pd, Qd, Gs and Bs divided by 1000.
  [hosting] --reverse-branch auto: no named branch leaves the slack bus, so there is no reverse-power check (name one with --reverse-branch).
  [exit 0]
  ```

  The 0.0313 MW unit keeps bus 30 above the bank's 0.93 pu closing threshold at 17:30, so the bank closes an hour
  later (step 37 instead of 35) and bus 13 sags to 0.9391 pu at 18:00 (0.9412 pu in the base run). Without the bank
  bus 18 takes 1 MW and bus 33 takes 2.9063 MW, both limited by over-voltage. The answer is right for the controls as set; move
  the bank's threshold, or study without it, to see the PV limit alone.
- **A transmission case needs generation to follow load.** Scaling demand to 0.3 of a meshed transmission case while the
  generators keep their case output puts the whole swing on the slack, and the first step may not converge. Set
  `"generationFollowsLoad": true` in the study (the re-dispatch the `chain` verb uses).
- **Reactive functions need a PQ bus.** A PV unit with volt-var, volt-watt or a fixed power factor on a bus whose
  voltage a generator holds is refused: the generator sets the voltage, so the function has nothing to act on.
- **Coarse steps and timers.** With 15 or 30 minute steps a tap changer whose delay is shorter than the step can make
  several operations at one step, as the real device would during the interval; use short steps (1 to 10 s) to study
  the timing itself.

## Limits

- QSTS runs on the balanced positive-sequence model; the unbalanced three-phase feeder model of ch. 7 is not
  time-stepped.
- Capacitor bank timers differ from OpenDSS on purpose. GridSim starts a bank's delay only for an operation the voltage
  asks for and the bank can make, and resets it when there is nothing to do. OpenDSS starts the delay at any exit of
  the band, even one asking for a state the bank is already in, and when the delay ends acts on whatever the voltage
  then asks for. On the reference feeder this makes OpenDSS close a bank at 95 s where GridSim closes it at 120 s;
  every other operation and voltage agrees (technical ch. 37).
- Hosting capacity studies each candidate on its own; it does not share a limit among several new resources.

## See also

- [Technical ch. 37, Quasi-static time series and hosting capacity](<../technical/0037 Quasi-static time series and hosting capacity.md>):
  the time model, the order of events in a step, every controller's equations, the hosting search and the validation
  against OpenDSS.
- [Ch. 19, CLI reference: `qsts`](<0019 CLI reference.md#qsts>) (the study file schema) and
  [`hosting`](<0019 CLI reference.md#hosting>) for every flag, default and exit code.
- [Ch. 13, Battery storage studies](<0013 Battery storage studies.md>) for the chemistry-level battery model of `chain`.
- [Ch. 5, Running power flows](<0005 Running power flows.md>) for the single power flow and the case-solve tap loop.
