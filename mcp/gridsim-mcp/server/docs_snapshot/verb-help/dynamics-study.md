### dynamics-study

`gridsim dynamics-study <study.json> [--out <result.json>] [--unit-protection <unit-protection.json>] [--model-assignments <assignments.json>] [--model-plugins <plugins.json>]`

Native RMS dynamics study over a PSS/E RAW case and its DYR file (RC3 P6). It runs the same engine and model bindings as
`stability --rms --dyn-data` (the RAW parse, the PSS/E DYR reader and the gridsim-dynamics/2 loader, unchanged) from one
strict study file, and reports a model coverage report before any verdict. The existing `stability` verb, its
`--dyn-data` refusal of unmodelled DYR data and its `--allow-unmodelled` permissive path are not changed.

The study file is strict JSON, schema version 1: `schemaVersion` (1); `raw` and `dyr` (`path`, relative to the study
file, and `sha256` of the file's exact bytes; a mismatch is refused and prints the resolved identity);
`initialisation` (`powerFlow.tolerancePu`, `powerFlow.maxIterations`, and `unmodelled`: `refuse` or `leaveOut`);
`integration` (`stepSeconds`, `durationSeconds`, a whole number of steps); `criteria` (`slipLimitRad`, the engine's
centre-of-inertia synchronism limit, and `maxAngleSpreadDeg`); `angleReference` (a machine `bus` and `id`); `channels`
(each `rotorAngle`, `speed` or `frequency` of a `machine`, or `busVoltage` of a `bus`); `cases` (the batch, in result
order); optional `execution.maxParallelism` (default 1, serial). Every equipment reference is by native RAW identity: a
machine by bus and machine ID (so two units at one bus stay distinct), a branch by `from`, `to` and `circuit`, a bus by
number. Unknown or duplicated fields, non-finite numbers, an unknown identity, an out-of-service target, an ambiguous
branch, a `seq` attachment (not supported in v1) and the global RAW switches (`--allow-unmodelled`, `--seq-data`,
`--tap-control`, `--shunt-control`, `--psse-revision`, `--frequency-hz`) are refused before any solve.

A case is `events` (an event list: `busFault` and `faultClear` with `bus`, `branchTrip` and `branchReclose` with
`branch`, `machineTrip` with `machine`, `loadStep` with `bus`, `pMw`, `qMvar`, `sequenceFault` with `bus` and `type`,
`lineFault` with `branch`, `fraction` and `type`, `lineFaultClear` with `branch`; each with `at` in seconds) or `cct`
(`faultBus`, `clearingTrips`, `onsetSeconds`, `maxClearingSeconds`, optional `refinedStepSeconds`, `iterations`,
`scanSteps`). A `sequenceFault` or `lineFault` `type` is `threePhase`, `slg`, `ll` or `llg`, with optional `rPu` and
`xPu` (the fault impedance, pu on the system base); a `sequenceFault` is cleared by a `faultClear` on its bus. Any other
event kind is refused.

**Step schedule and error-controlled step (RC5).** `integration` takes two optional members for the full model; give one
or neither. Without them the study, its output and every number are exactly as before.

- `stepSchedule`: a list of windows `{ "fromS": 0, "dtS": 0.001 }, { "fromS": 0.5, "dtS": 0.005 }`. The events rows take
  fixed steps of `dtS` from each `fromS` to the next. The first window starts at 0, starts rise, and each window must be a
  whole number of its own steps. An event strictly inside a window whose step is larger than the schedule's smallest is
  refused (start a window at the event). A one-window schedule at `stepSeconds` gives the plain rows bit for bit.
- `errorControl` (EXPERIMENTAL: opt-in, and not part of the verified contract): GridSim chooses the windows.
  Members, all optional: `relativeTolerance` (1e-3), `absoluteTolerance` (1e-4, pu, rad for angles), `maxStepSeconds`
  (0.02), `minStepSeconds` (1e-4), `segmentSeconds` (0.1), `maxPasses` (12) and `safetyFactor` (0.5, in (0, 1]). The run
  is cut into segments, with one starting at every event time. Each pass runs the schedule and the same schedule with
  every step halved, compares every recorded state at every shared sample (the machine states, each converter's
  voltage, powers and currents, each network device's states), and halves the step of every segment whose scaled
  difference is above the safety factor. The error figure is a first-order step-doubling estimate, not a bound, and a
  run can cost up to 2 x `maxPasses` full simulations (24 with the defaults). Each events row's `stepControl` and
  `effectiveOptions.integration.errorControlStatus` carry the EXPERIMENTAL label. The reported run is the finer of the last
  pass. A row whose tolerance was not met is INCOMPLETE, unless both runs of the last pass lost synchronism (then it stays
  UNSTABLE, with the note).

The no-event interval and the `cct` rows keep `stepSeconds`. Each events row gets `stepControl` (the mode, the windows
with each one's scaled difference under error control, the number of steps, and for error control whether the tolerance
was met, the largest scaled difference, the passes, the runs and a note). `effectiveOptions.integration` echoes the
member given. Refused as input errors: both members at once, a malformed or off-grid schedule, an event in a coarse
window, error-control values out of range, and either member with `--model-assignments` (GridSim cannot check that a user
or plug-in model takes the step on each call).

**IEC 61400-27-1 wind turbines (RC5, opt-in).** An optional top-level `iec61400` list makes units of the case IEC 61400-27-1
style Type 4A or 4B wind turbines (2020 edition structure), one entry per unit: `machine` (`bus`, `id`), `type` (`4A` or
`4B`), `edition` (2020), and the members `genSystem`, `pControl`, `qControl`, `currentLimit`, `pll`, `measurement`,
`mechanical` (Type 4B only) and optional `protection`, with the parameters listed in chapter 15. The models are implemented
from public open-source implementations (Dynawo) and the CGMES DY definitions, not checked against the IEC 61400-27-1 text.
The unit's coverage row reads BOUND with the model's description, and `effectiveOptions.iec61400` lists each unit with that
source note. Without the member the study, its output and every number are exactly as before. Refused as input errors: an
unknown or missing field, a parameter the type does not read (`tpaero` or `mechanical` on Type 4A, `tpwtref` on Type 4B), the
2015 edition, ride-through mode 3, a value out of range, a unit given twice or with DYR records, a user or plug-in model on
the same unit, and the member together with `adaptive`, `stepSchedule` or `errorControl`.

**Variable step and relays in the loop (RC4 R4-14).** An optional `adaptive` member of the version 1 study file adds a second,
opt-in integration path beside the fixed-step one; without it the study, its output and every number are exactly as before.
Its members are all optional: `relativeTolerance` (default 1e-6), `absoluteTolerance` (1e-8), `initialStepSeconds` (1e-3),
`minStepSeconds` (1e-9), `maxStepSeconds` (0.05), `eventToleranceSeconds` (1e-6, the width relay events are located to),
`cctToleranceSeconds` (1e-4, the width of the adaptive critical clearing time bracket), `devices` (`path` of a
`gridsim-devices/1` placed device set, relative to the study file, and the `sha256` of its exact bytes) and `curves` (a
curve library directory). Every row is then also run by the variable-step kernel and the result gains an `adaptive` section
(and `effectiveOptions.adaptive`) that declares `integration: adaptive`, the scheme, the tolerances, the accepted and rejected
step counts, the smallest and largest step, the model it integrates, and per row the events as applied. The kernel is the
Dormand-Prince 5(4) embedded pair with a step rule that is a pure function of the step just tried and its error, so a run is
bit-identical across repeats and thread counts. The error test scales each state by `atol + rtol * max(|y|, |y_new|)`, and
each rotor angle by the fixed `atol + rtol * 1 rad`, so the angle tolerance does not loosen as the angles drift off frequency. A step with a NaN in any state fails the error test, so a run whose state goes non-finite stops with
the minimum-step error "(non-finite state)" rather than ending as a loss of synchronism. Scheduled events are applied at exactly their requested times (the step is cut
to land on them), where the fixed-step engine moves them to its step grid; each event is listed beside the time the
fixed-step engine applies it and the difference. Relays from the device set (overcurrent relays and reclosers, functions 50
and 51, and fuses) are driven by the curve equation or table of their placed device at the simulated primary current: each
carries the fraction of its operate time used, which grows at 1/t_op(I) while it is picked up, and trips when it reaches 1; at
a constant current that is exactly t_op(I). Pick-up, drop-out and trip events are located by bisection to
`eventToleranceSeconds` and applied at the high end of the bracket, and a trip opens the protected branch. Reclosing is not run
in the loop, other functions and breakers are listed as not evaluated, and the loop reads currents from the balanced RMS network.
A `cct` row also gets an adaptive bracket whose clearing times are not rounded to a step. The adaptive path integrates the
electromechanical swing of the prepared machines with their subtransient EMFs frozen and the mechanical power held at its initial
value: exciters, governors, flux decay and converter plant are not represented (a case with converter plant is refused for the
adaptive rows), so the adaptive rows are additional evidence from that reduced model; the fixed-step rows are unchanged and
still decide `status`. Equivalence with the PowerFactory variable-step integration is not claimed (COMPARATOR-UNVERIFIED).

**Unit protection in the loop (RC5, opt-in).** `--unit-protection <file>`, or `adaptive.unitProtection` in the study file
(`path`, relative to the study file, and the `sha256` of its exact bytes), puts the generating units' own relays in the
adaptive rows: 81U / 81O, 81R, 27 / 59, 32 and 78 stages act during the run, an operating stage disconnects its unit's
machine and the run continues (40 is listed as not evaluated: the swing model has no field model; critical clearing time
rows do not run unit relays). It is given once. Without the `adaptive` member, `--unit-protection` runs the relays in the
full-model run alone (below). The
file is a `gridsim-unit-protection/1` document (`schemas/unitprotection/unit-protection.schema.json`): `templates`, each a
template of kind `unitProtection` in the v2 library shape, and `units`, each with `unit`, `template`, and optionally the
unit's own `params` (with a `reason`, and a `provenance` for values that do not carry their own). Its templates and values
are checked and resolved exactly as a v2 case's unit protection (unit value, else template value, else no relay; a value
with no stated source is a `[VERIFY]` placeholder quoting the reason). A unit is named by its PSS/E machine key
`<bus>:<id>`: the RAW generator record's bus number and machine ID as written (quotes and surrounding blanks removed, case
kept), for example `101:1`. A unit the RAW case does not have is refused before any solve, each one listed. The result
gains `unitProtection` (the source, file name, SHA-256 and schema, the units, the templates, and every unit trip with its
row, case, unit, stage, trip time, the quantity read and the settings with their grades and sources),
`effectiveOptions.adaptive.unitProtection`, and in the adaptive section `unitRelayRule`, `unitRelaysNotEvaluated` and per
row `unitRelays` (every stage: picked up, tripped, when, the quantity) and `stopReason`; every other member is as without
it, and a study without unit protection is byte-identical to before.

With unit protection each events row is also re-run on the full-model engine (the fixed-step six-state machines with
flux decay, their exciters and governors, the same events and step) with the units' relays acting, including loss of
excitation (40: the apparent impedance V_t / I at the machine terminals each step, in pu of the unit rating, against the
`loe1` / `loe2` offset mho zones and their delays). A stage operates at the first step at least its delay after it picked
up (never early; one-step resolution) and its unit leaves at the next step; a trip of the last machine ends that run with
the reason. The result's `unitProtection` gains `fullModel`: `engine`, `rule`, `statusRule`, `notEvaluated` (each stage
the engine cannot evaluate, with the reason: for example 40 on a unit with no rating or with no field dynamics, 78 with
a delay, a frequency stage for another nominal; on a unit with a unit transformer folded in, 40 reads the machine
terminal, behind the transformer), `trips` (row, case, unit, stage, operate
and disconnection times, the quantity, for 40 the apparent R and X, the settings) and `rows` (per row the disposition
`EVALUATED`, `FAILED` or `NOT RUN` for a CCT row, the relay run's synchronism verdict, any early end and every stage's
record). `unitProtection.trips` stays the adaptive rows' list, and the `cases` rows and `status` are unchanged.

Without the `adaptive` member, `--unit-protection` runs the units' relays in this full-model run only: the result gains
`effectiveOptions.unitProtection` (`source`, `units`) and `unitProtection` with the file's identity, its units and
templates and `fullModel` as above (no `trips` list, since no adaptive row was run). `fullModel` is the same, byte for
byte, as with the `adaptive` member, and every other member is the study's own without unit protection.

```json
{
  "schema": "gridsim-unit-protection/1",
  "templates": [
    { "id": "gridsim.local.sync", "rev": 1, "kind": "unitProtection", "status": "active", "title": "Synchronous unit",
      "params": { "uf1Hz": 47.0, "uf1DelayS": 2.0, "relayNominalHz": 50 },
      "provenance": { "_default": { "tag": "CuratedPlaceholder", "note": "[VERIFY] class default, not a unit schedule" } } }
  ],
  "units": [
    { "unit": "101:1", "template": "gridsim.local.sync@1" },
    { "unit": "102:G2", "template": "gridsim.local.sync@1", "params": { "uf1Hz": 47.5 },
      "reason": "the unit's protection schedule", "provenance": { "tag": "Recorded", "note": "unit 102 G2 protection schedule" } }
  ]
}
```

The coverage report lists every DYR record (line, bus, model, ID, role, generator index) with its status: `SUPPORTED`
(every value carried, exactly or by the exact derivations it lists), `APPROXIMATED` (a value left out), `SKIPPED` (an
unmapped model, a value its controller cannot honour, a controller with no machine), `UNMATCHED` (names a machine the RAW
case does not hold: always refused), `OUT OF SERVICE` or `OUT OF SCOPE` (a renewable converter chain: v1 carries
synchronous machine chains only), with the reasons and the traces the record's unit drives; and every in-service unit as
`BOUND`, `APPROXIMATED` or `UNBOUND` (the study never synthesises machine data). Any gap makes the report `BLOCKED` and
`likeForLike` false. With `refuse` nothing is simulated; with `leaveOut` the rows run with the gaps left out, labelled
indicative, and the study is `INCOMPLETE` whatever they show.

Before any disturbance the study runs a no-event interval over the study duration and checks the initial voltage and
accelerating-power residuals and the speed, relative rotor angle and voltage drift against limits derived from the
Newton tolerance (stated in the result); a drift beyond them stops the rows. Each event row records every event as
requested and as applied (the first integration step at or after its time), with its processing order and simultaneous
group, the traces of the monitored channels (angles in degrees, speed in pu, frequency in Hz, voltage in pu), the largest
angle spread, and the post-event branch topology checked by its own power flow. A CCT row reports the bracket (stable and
unstable endpoints, both re-run and verified, the search criteria, a non-monotone pocket if seen) and, with
`refinedStepSeconds`, the bracket again at that step with the shifts: the engine's own timestep sensitivity, not a
comparison with another tool. Rows run independently from the same solved case, each with fresh controllers; serial and
parallel runs give the same numbers bit for bit.

Row dispositions: `STABLE`, `UNSTABLE`, `CCT BRACKETED`, `CCT NON-MONOTONE`, `UNSTABLE AT ZERO CLEARING`, `STABLE THROUGH
WINDOW`, `INCOMPLETE`, `FAILED`, `NOT RUN`, `NOT ATTEMPTED`. Study `status`: `STABLE` (coverage complete, every row
evaluated, every events row stable), `UNSTABLE`, `EVALUATED` (a CCT-only batch evaluated in full) or `INCOMPLETE` (a
coverage gap, a power flow with no solution, a drifting initialisation, or a failed, incomplete or unattempted row).

The result JSON (`schemaVersion`, `provenance` with the study, RAW and DYR SHA-256 and engine identity,
`effectiveOptions`, `coverage`, `engine`, `initialisation`, `cases`, `counts`, `status`, `likeForLike`, `reasons`) is
printed to stdout and, with `--out`, written to a new file via a temporary file and a final move. An existing `--out`
path is refused.

Exit 0 `STABLE` or `EVALUATED`. Exit 1 for input it cannot accept (a malformed study, an identity or SHA-256 mismatch, an
unreadable RAW or DYR file, an existing output path). Exit 2 for an invalid invocation or a refused global switch. Exit 3
`UNSTABLE` or `INCOMPLETE`.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |
| `--unit-protection` | none | A `gridsim-unit-protection/1` file whose units' own relays act in the full-model re-run of each events row and, with the study's `adaptive` member, in the adaptive rows (units named `<bus>:<id>`). |
| `--model-assignments` | none | A `gridsim-model-assignments/1` file binding user models (shipped or file declarative models, and plug-in models) to machines as governor or exciter. See [`user-model`](#user-model). |
| `--model-plugins` | none | A `gridsim-plugins/1` configuration: the only way a plug-in model is loaded. Needs `--model-assignments`. Refused through `rpc` over `--listen`. |

**User models in a study (RC5).** `--model-assignments <file>` binds user models to machines, in place of the governor or
exciter their DYR records give:

```json
{
  "schema": "gridsim-model-assignments/1",
  "userModels": [
    { "machine": { "bus": 1, "id": "1" }, "model": "TGOV1", "parameters": { "R": 0.05, "T1": 0.49 } },
    { "machine": { "bus": 2, "id": "1" }, "file": "my-exciter.model.json", "sha256": "<hex>", "replacesStabiliser": true }
  ],
  "pluginModels": [
    { "machine": { "bus": 3, "id": "1" }, "model": "MY_GOV", "parameters": { "R": 0.05 } }
  ]
}
```

A declarative entry names a shipped model (`model`) or a document (`file`, relative to the assignments file, pinned by its
`sha256`; a network or device path is refused, the document may be at most 16 MiB, and a pin that does not match is
refused naming the pin, not the hash of the file read). A plug-in entry names a model of the `--model-plugins` configuration; without that flag it is refused with
"plug-in models need --model-plugins". The coverage report lists a record bound to a declarative model as
`SUPPORTED (USER MODEL, sha256:<document hash>)` and one bound to a plug-in as
`SUPPORTED (PLUGIN, sha256:<assembly hash>, OUTSIDE THE VERIFIED CONTRACT)`; the reader notes carry the plug-in id,
assembly, hash, contract version, parameter values and the same label. The result's `provenance` gains `modelAssignments`
(the file name, its SHA-256 and the declarative bindings) and, with plug-ins, `plugins` (the configuration's SHA-256, the
contract version, the label and each model's machine, role, assembly, SHA-256 and parameters). Without the two flags the
result is byte-identical to a run without them.

The file may also hold `deviceModels`: injection, machine and load models with no DYR record of their own. Each entry is
`{ "role": "injection" | "machine" | "load", "bind": ..., "plugin": "<id>" }`, or `"file"` and `"sha256"` in place of
`"plugin"` for a declarative document, with optional `"parameters"`. An injection or machine model binds to a unit,
`"bind": { "machine": { "bus": 3, "id": "1" } }`, which then runs as a current source on its own bus without its DYR machine,
exciter or governor; a load model binds to part of a bus load, `"bind": { "bus": 4, "pMw": 10, "qMvar": 0 }`. The role must
be the model's own; a machine is a contract 2 plug-in only. The reader notes name each model and where it is bound, with
OUTSIDE THE VERIFIED CONTRACT for a plug-in, and the
result's `provenance` gains `deviceModels` (role, binding, model, SHA-256, source, assembly, contract version, configuration
SHA-256, parameters and label). Not with `adaptive`, `stepSchedule` or `errorControl`; the unit may not also take a governor
or exciter model, an IEC 61400 model, or be the angle reference.

An entry of role `relay` is a user relay: a contract 2 relay plug-in, or a declarative document of role `relay` (inputs `vm`,
`f`, `p`, `q`, `dfdt`, `speed`, `i`; output `trip`, 0 or the reason code; the `ontimer` and `latch` blocks give whole-step
timers and seal-in). It binds to a unit, `"bind": { "machine": { "bus": 2, "id": "1" } }`, or to a branch, `"bind": { "branch":
{ "from": 2, "to": 4, "circuit": "1" } }`, read at its `from` end as written; a branch relay's trip opens the branch. With
relays, each events row is run again on the full-model fixed-step engine with them acting: the result gains `userRelays.fullModel`
(or, with unit protection, the relays join `unitProtection.fullModel`), with each row's `unitRelays` and `branchRelays` records
and the `trips` and `branchTrips` lists, and `provenance.relayModels` (the same fields as `deviceModels`). The study's own rows,
status and exit code are unchanged. A declarative relay's file is journalled and re-checked by `breaking-change validate`.
Not with `adaptive`, `stepSchedule` or `errorControl`.
