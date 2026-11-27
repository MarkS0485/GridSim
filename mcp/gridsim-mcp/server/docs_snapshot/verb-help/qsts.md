### qsts

`gridsim qsts <case> --study <file.json> [--top 10] [--allow-unsettled]`

Quasi-static time series (`QstsEngine`): a sequence of power flows, one per step, over the load, generation, PV and
storage profiles of a study file, warm-started from the previous step, with the controllers acting in time and their
state (tap positions, bank steps, delay timers, inverter set points, storage energy) carried from step to step. The
engine is the one the case solve picks: the backward/forward sweep for a radial feeder (or with `--bfs`), otherwise the
shared Newton engine on the selected backend, with the default power-flow options. Step 0 is solved from the case as
given, so a one-step study with nothing varying gives exactly the case's own power flow. PV units, storage and load
additions are constant-power injections: on a bus with a voltage-dependent (ZIP) load only the bus's own load follows
the ZIP characteristic (the ZIP is re-based on the bus demand so that the devices keep their set power), and a bus where
the devices cancel the load exactly while its ZIP part still varies is refused by name. A tap changer whose own
transformer is out of service is not stepped (and counts no operation), here and in the case solve's tap loop.

Two control modes (`"mode"` in the file):

- `timed` (the default): the time-aware controllers below. Within a step: the timers and storage energy advance over
  the elapsed interval; the step is solved; then, repeatedly, inverters and storage settle to a fixed point, else every
  capacitor bank whose delay has run out switches one step, else every tap changer whose delay has run out moves one
  tap, re-solving after each round, until nothing acts (or `maxControlIterations`, default 25, discrete rounds;
  `maxContinuousIterations`, default 200, continuous ones; when the per-unit inverter iteration reaches that cap
  without settling, as with many coupled volt-var units, an accelerated iteration over all units takes over with a fresh
  cap of the same size, technical ch. 37).
- `steady`: each step is solved by the existing steady-state loops (the tap loop of the case solve, around the
  switched-shunt loop of `pf --switched-shunts` when banks are given), so the controls settle instantly; taps and bank
  steps carry to the next step. Volt-var, volt-watt, peak shaving, line drop compensation and var/time bank control need
  `timed` and are refused here.

Controllers (timed mode):

- **Tap changers**: the case's OLTC records (target, deadband, step, range), with timing from the file's `oltc` block:
  `delaySeconds` (initial delay), `interTapSeconds` (later steps of the same excursion under `"sequence": "continuous"`;
  `"sequential"` waits the full delay before every step), `"characteristic": "inverse"` (delay x half-band / |deviation|,
  not below `minDelaySeconds`), line drop compensation `ldcRPu` / `ldcXPu` (pu on the system base: the regulated voltage
  is |V - (R + jX) I| with I the transformer current into the regulated bus), and `"coordination": "sequential"` (only
  the first ready regulator by `priority` moves per round) or `"independent"`. A delay counts from the first step at which
  the voltage is seen outside the band; with steps longer than the delay the regulator makes, at that step, every
  operation its timers allow (catch-up). An OLTC regulating a generator-held bus holds, as in the case solve.
- **Capacitor and reactor banks**: equal steps of `stepMvar` (MVAr at 1 pu, negative for a reactor) between `minSteps` and
  `maxSteps`, with `"control"`: `voltage` (in below `onPu`, out above `offPu`, at `controlsBus` or the bank's bus), `var`
  (in when the reactive power into `branch` at its From end exceeds `onMvar`, out below `offMvar`) or `time` (in from
  `onTime` to `offTime`, "HH:MM"); `delaySeconds` / `offDelaySeconds`; `deadTimeSeconds` (no switching back in until that
  long after switching out). A bank that reverses within a step is frozen for the rest of the step.
- **PV inverters**: `ratedMw` at profile value 1, `ratedMva`, availability `profile`; `control.function` `unity`, `pf`
  (`powerFactor`, `pfMode` absorbing or injecting), `volt-var`, `volt-watt` or `volt-var-volt-watt`, with curves
  `"voltVar": "1547-A" | "1547-B" | {v1,q1,...,v4,q4}` (Q as a fraction of `ratedMva`, positive injecting; default IEEE
  1547-2018 Category B) and `"voltWatt": "1547" | {v1,p1,v2,p2}` (default 1.06 / 1.10 pu); `responseSeconds` (open-loop
  response time: 90 % of a change after that long); `priority` `var` (P gives way) or `watt` (Q gives way) within
  `ratedMva`.
- **Storage**: `ratedMw`, `capacityMwh`, `initialSoc`, `socMin`, `socMax`, `chargeEfficiency`, `dischargeEfficiency`, and
  `dispatch.mode` `schedule` (MW `profile`, discharge positive), `peak-shave` (keep the active power into `branch` at its
  From end between `lowMw` and `highMw`) or `self-consumption` (cover its bus's demand net of PV there). The power is held
  for the step; E(k+1) = E(k) + eta_c Pc dt - Pd / eta_d dt; the power is limited so the energy stays within the SOC limits.

The study file (`gridsim-qsts/1`; every key checked, unknown keys refused):

```json
{
  "format": "gridsim-qsts/1",
  "stepSeconds": 900, "steps": 96, "mode": "timed", "startTime": "00:00",
  "profiles": {
    "res":   { "stepSeconds": 1800, "values": [0.62, 0.60, 0.58], "interpolation": "linear", "wrap": true },
    "solar": { "stepSeconds": 900, "csv": "solar.csv", "column": "pv" }
  },
  "loads": [ { "buses": "all", "profile": "res" } ],
  "generators": [ { "name": "G1", "profile": "wind", "absoluteMw": false } ],
  "loadAdditions": [ { "name": "EV7", "bus": 7, "mw": 0.5, "profile": "ev", "powerFactor": 0.98 } ],
  "pv": [ { "name": "PV5", "bus": 5, "ratedMw": 2, "ratedMva": 2.2, "profile": "solar",
            "control": { "function": "volt-var", "voltVar": "1547-B", "responseSeconds": 5 } } ],
  "storage": [ { "name": "B7", "bus": 7, "ratedMw": 1, "capacityMwh": 2, "initialSoc": 0.5, "socMin": 0.1, "socMax": 0.9,
                 "chargeEfficiency": 0.95, "dischargeEfficiency": 0.95,
                 "dispatch": { "mode": "peak-shave", "branch": "T1", "highMw": 5, "lowMw": 2 } } ],
  "oltc": { "coordination": "independent",
            "default": { "delaySeconds": 30, "interTapSeconds": 5, "sequence": "continuous", "characteristic": "definite" },
            "settings": [ { "transformer": "T1", "ldcRPu": 0.01, "ldcXPu": 0.02 } ] },
  "capacitors": [ { "name": "C9", "bus": 9, "stepMvar": 0.6, "maxSteps": 1, "control": "voltage",
                    "onPu": 0.97, "offPu": 1.04, "delaySeconds": 60, "deadTimeSeconds": 300 } ],
  "limits": { "vminPu": 0.94, "vmaxPu": 1.06, "loadingPercent": 100, "perBusVoltageLimits": false }
}
```

Required: `format`, `stepSeconds` (whole seconds, at least 1), `steps`. A profile gives one sample per its own
`stepSeconds` from t = 0, inline (`values`) or from a CSV column (`csv`, a path relative to the study file (a local absolute path is accepted; a network (UNC) or device path is refused before anything is opened, and the file may be at most 64 MiB), and `column`,
a header name or an index from 0; `"header": true` or `false` says whether the first row names the columns, and when it is left out a column chosen by name needs a header row and one chosen by index has a header when the first row's cell in that column is not a number (bug #162: it used to look at the first cell only, so a headerless file starting with a timestamp column lost its first sample); an unreadable cell is reported by its file line); `"interpolation"`
`hold` (default) or `linear`; `"scale"` multiplies every value; a study longer than a profile is refused unless the
profile has `"wrap": true`. Load profiles multiply the bus demand (`scaleP` / `scaleQ`, default both; `"buses": "all"`
means every bus with demand, and a bus takes one load profile); generator profiles multiply Pg (or give it in MW with
`absoluteMw`), the generator named by `name` or by `index` among the in-service units (from 0). PV, storage and added
loads enter their bus as a negative / positive load. A reactive function on a PV unit at a PV or slack bus is refused
(the generator holds that voltage). `"generationFollowsLoad": true` scales every generator off the slack bus that has no profile of its own by the
step's demand ratio (the profile value when one profile scales every bus) and re-balances the dispatch, as `chain` does,
so a transmission case's slack does not absorb the whole demand swing; without it generation stays as in the case.

Prints the run size and speed, the energy totals, the voltage range and when it occurred, the tap and bank operation
counts per device, each storage unit's final state of charge and the buses and branches outside their limits, with the
time spent outside (h:mm:ss; each step stands for one step length) and the duration-weighted excess (pu.h above / below).

Exit 0 when every step converged and settled. Exit 1 when the study file is refused (every problem is listed). Exit 3
when a step does not converge (the run stops there, no result file is written) or, without `--allow-unsettled`, when a
step's controls did not settle within the iteration caps (the result file is written, with exit 3 inside).

| Flag | Default | Meaning |
|---|---|---|
| `--study` | (required) | The `gridsim-qsts/1` study file. |
| `--top` | 10 | Buses and branches outside their limits to list. |
| `--allow-unsettled` | off | Exit 0 even when a step's controls did not settle within their caps. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary (`case`, `buses`, `engine`, `mode`, `step_seconds`, `steps`, `completed`,
`unsettled_steps`, `tap_operations`, `capacitor_operations`, `vmin_pu`, `vmax_pu`, `load_mwh`, `loss_mwh`, `source_mwh`,
`pv_available_mwh`, `pv_mwh`, `pv_curtailed_mwh`, `storage_charge_mwh`, `storage_discharge_mwh`, `buses_violating`,
`branches_violating`, `digest`: a SHA-256 over every number of the run, equal between two runs exactly when they agree to
the bit) and the tables `steps` (`step,time_s,settled,solves,iterations,vmin_pu,vmin_bus,vmax_pu,vmax_bus,
max_loading_pct,max_loading_branch,loss_mw,loss_mvar,load_mw,source_mw,pv_available_mw,pv_mw,pv_mvar,storage_mw`),
`devices` (`kind,name,operations`), `operations` (the log: `step,time_s,kind,device,from,to,sensed,control_iteration`),
`bus_violations` (`bus_id,seconds_above,seconds_below,pu_seconds_above,pu_seconds_below,max_pu,min_pu`),
`branch_violations` (`branch,name,seconds_over,percent_seconds_over,max_percent`), `taps` (`step,transformer,tap`),
`banks` (`step,bank,steps`), `pv` (`step,unit,p_mw,q_mvar`), `storage` (`step,unit,p_mw,soc_start`) and, unless the file
sets `"recordStates": false`, `voltages` (`step,bus_id,vm_pu`) and `loadings` (`step,branch,loading_pct`, rated branches).
