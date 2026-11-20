# 35. Scripting

`gridsim-script` runs GridSim from a text script, one statement per line, the way a person would type it at a console:
load a case, solve it, trip a generator, step a simulation, report, export. On top of those verbs the language has loops
over the elements of a case (`FOR EACH`), decisions on results, state and time (`IF`), named values with unit-checked
arithmetic (`LET`), and named procedures (`PROC`, `CALL`). Five engines can be stepped, with eight models between them: the live simulation, a frequency
swing, the protection bundle of a case, an RMS multi-machine run (classical, subtransient or governed) and an EMT run
(single-phase or three-phase). The same grammar serves an interactive
session and a `.gss` script file, and every interactive session is saved as a `.gss` file, so anything done by hand can be
run again byte for byte. A run leaves a record (JSON) naming the GridSim version, the grammar version, the SHA-256 of the
script and of the model, the solver and engine settings, the step and event sequence, every decision and loop, and the
SHA-256 of every output.

The scripting layer is the project `GridSim.Scripting` (a library that references `GridSim.Core` only) and the
executable `gridsim-script` (`GridSim.Scripting.Console`). It reads Core and changes nothing in it that an existing
caller can see: the one addition, `LiveSim.ReplaceNetwork`, is a method that nothing calls unless it wants to change the
network of a running live simulation.

**Grammar version 1** is the version described here. A script may open with `GRAMMAR 1`; a script that asks for any other
number is refused. Grammar 1 is frozen for the whole of the v2 contract: a later grammar adds statements and never changes
the meaning of a statement written in grammar 1. Every script terminates: there is no `WHILE`, no unbounded loop, no
recursion, and no clock, random number, file or network access beyond the named verbs (`LOAD CASE` reads a case and
`EXPORT RESULT` writes a result).

## Before you start

- Build the console: `dotnet build src/GridSim.Scripting.Console -c Release`. The binary is
  `src/GridSim.Scripting.Console/bin/Release/net10.0/gridsim-script` (`.exe` on Windows). The examples write
  `gridsim-script` for it.
- A case: a MATPOWER `.m` file, a PSS/E `.raw` file or a JSON case folder (ch. 2). The examples use
  `data/matpower/lib/case9.m`, the WSCC 9-bus case.

## Worked example

`docs/_captures/scripting/outage.gss` loads case9, solves it, exports the solve, selects the live engine with a 20 ms
integration step, schedules the loss of generator `G2` 100 ms in, steps half a second and reports the frequency:

```text
GRAMMAR 1
# Lose generator G2 of the WSCC 9-bus case 100 ms into a live run and watch the frequency.
LOAD CASE "data/matpower/lib/case9.m" FREQUENCY 60 HZ
SOLVE POWERFLOW
EXPORT RESULT "results/outage-base.json"
SELECT ENGINE LIVE STEP 20 MS
AT 100 MS TRIP GENERATOR "G2"
STEP SIM 500 MS
REPORT SIM
```

Run it from the repository root, with the repository root as the folder that relative paths are taken from, and keep the
run record:

```text
gridsim-script run docs/_captures/scripting/outage.gss --base-dir . --record results/outage.run.json
```

```text
loaded "case9": 9 buses, 9 branches, 3 generators, 3 loads, base 100 MVA, 60 Hz, sha256 db4af3785d6a
power flow converged in 4 iterations, largest mismatch 1.640E-014 pu, losses 4.6410 MW, solver sparse-newton
exported 27997 bytes to "results/outage-base.json", sha256 c32b69ca9c33
engine LIVE selected: integration step 20 ms, nominal 60 Hz, flow solver Newton-Raphson (dense); script clock 0 ms
scheduled at 100 ms: TRIP GENERATOR "G2"
stepped 25 x 20 ms to t = 500 ms: frequency 58.398622 Hz, rocof -3.926716 Hz/s
t 500 ms, engine LIVE, step 20 ms
tick 24, frequency 58.398622 Hz, rocof -3.926716 Hz/s, generation 319.4347 MW, demand 315.0000 MW
```

The exit code is 0 when every statement ran, 1 when a statement was refused (the message names its line and column), and
2 for a usage or file error. Under `LIVE` the trip of `G2` also takes it out of the live network, so the generation that
the last tick reports is what the remaining machines produce, with the losses of that network.

### Loops, decisions, values and procedures

`docs/_captures/scripting/layers.gss` loses line `4-5`, lists the lines above a loading limit, and sheds load by a
procedure when the voltage at bus 5 is low:

```text
GRAMMAR 1
# Lose line 4-5 of the WSCC 9-bus case, list the lines above 60 % loading, and take load off the weakest bus by procedure.
LOAD CASE "data/matpower/lib/case9.m" FREQUENCY 60 HZ
PROC shed(LOAD l, MW by)
  SET LOAD l P P OF LOAD l - by
END
TRIP LINE "4-5"
SOLVE POWERFLOW
LET limit = 0.6 PU
FOR EACH LINE ln WHERE LINE ln IS IN SERVICE AND LOADING OF LINE ln > limit
  PRINT LOADING OF LINE ln
END
IF VM OF BUS "5" < 0.95 PU
  CALL shed("L5", 10 MW)
  SOLVE POWERFLOW
  PRINT VM OF BUS "5"
ELSE
  PRINT VM OF BUS "5"
END
```

```text
gridsim-script run docs/_captures/scripting/layers.gss --base-dir .
```

```text
loaded "case9": 9 buses, 9 branches, 3 generators, 3 loads, base 100 MVA, 60 Hz, sha256 db4af3785d6a
power flow converged in 4 iterations, largest mismatch 9.187E-012 pu, losses 6.1360 MW, solver sparse-newton
LOADING OF LINE ln = 0.632455532026636 PU
LOADING OF LINE ln = 0.653893297204988 PU
power flow converged in 4 iterations, largest mismatch 9.390E-013 pu, losses 5.3793 MW, solver sparse-newton
VM OF BUS "5" = 0.952920135919414 PU
```

The run record shows the `FOR EACH` (which lines matched), the `IF` (what was compared and the outcome), the call with its
arguments as they ran, and the final value of every named value.

### Another engine

`docs/_captures/scripting/engines.gss` applies a bolted fault at bus 7 for 100 ms as an RMS multi-machine run and steps it
in two halves of half a second:

```text
GRAMMAR 1
# A three-phase fault at bus 7 of the WSCC 9-bus case, cleared after 100 ms, stepped as an RMS run.
LOAD CASE "data/matpower/lib/case9.m" FREQUENCY 60 HZ
SOLVE POWERFLOW
SELECT ENGINE RMS CLASSICAL STEP 5 MS WITH INERTIA 5 S
AT 100 MS FAULT BUS "7"
AT 200 MS CLEAR FAULT BUS "7"
STEP SIM 500 MS
REPORT SIM
STEP SIM 500 MS
REPORT SIM
```

The first `REPORT SIM` prints:

```text
t 500 ms, engine RMS, step 5 ms
t 500 ms, 3 machine(s), rotor angle against the inertia-weighted centre (deg) and speed deviation (pu)
  "G1" angle -11.1317 deg, speed 1.4604E-002 pu
  "G2" angle 14.5957 deg, speed 4.7218E-003 pu
  "G3" angle -3.4640 deg, speed 1.2328E-002 pu
since t = 0: in synchronism; fault at bus 7 from 100 ms to 200 ms; events so far: 2
```

Running the same script with one `STEP SIM 1 S` gives the same numbers at 1000 ms as the two halves do (see Stepping the
whole-horizon engines).

`docs/_captures/scripting/emt3.gss` runs the three-phase EMT engine with a bolted fault at bus 5 and reports the three phases
of that bus:

```text
GRAMMAR 1
# A bolted three-phase fault at bus 5 of the WSCC 9-bus case for 10 ms, watched on all three phases of bus 5.
LOAD CASE "data/matpower/lib/case9.m" FREQUENCY 60 HZ
SELECT ENGINE EMT THREE_PHASE WITH PROBE "5"
AT 10 MS FAULT BUS "5"
AT 20 MS CLEAR FAULT BUS "5"
STEP SIM 30 MS
REPORT SIM
```

```text
stepped 300 x 0.1 ms to t = 30 ms: bus "5" phase peaks 3.442314, 3.436349, 0.992245 pu
t 30 ms, engine EMT, step 0.1 ms
t 30 ms, three-phase EMT at 0.1 ms steps, source at bus "1"; phase voltages (pu) at bus "5" and their largest magnitude since t = 0
  phase a v 0.046646 pu, peak 3.442314 pu
  phase b v -0.842633 pu, peak 3.436349 pu
  phase c v 0.795987 pu, peak 0.992245 pu
events so far: 2
```

## Reading the output

Each statement prints at most a few lines. `loaded` gives the case size, the base MVA, the nominal frequency and the first
12 hex digits of the case's SHA-256. `power flow converged` gives the Newton iteration count, the largest power mismatch
(pu), the real-power losses and the solver the Core selector chose. `stepped` gives the number of integration steps
taken, the script clock after them, and a summary of the engine at the end of the last step. `PRINT` shows the expression
as written, an equals sign and its value with the unit.

The run record (`schema` `gridsim-script-run/1`) has these members:

| Member | Content |
|---|---|
| `gridsimVersion` | `BuildInfo.Version` of the build that ran the script. |
| `grammarVersion` | `"1"`. |
| `scriptSha256` | SHA-256 of the script file's bytes. |
| `canonicalScriptSha256` | SHA-256 of the canonical form of the statements that ran, one per line. Comments, spacing and keyword case do not move it; a change of meaning does. |
| `modelSha256` | SHA-256 of the case that was loaded last (a file's bytes; for a case folder, the hash of its sorted file hashes). `null` when no case was loaded. |
| `inputs` | Each LOAD: sequence number, line, kind (`matpower`, `psse-raw`, `json-folder`), the path as written, SHA-256. |
| `solver` | Power-flow solver name and options (`tolerancePu`, `maxIterations`, `flatStart`, `enforceQLimits`, `maxQLimitPasses`, `retryFromDcStart`), the engine, and for the live engine its flow solver, `integrationStepUs`, `nominalHz` and `liveSimSeed`. |
| `statements` | Every statement that ran, in order: sequence number, line, column, script time in microseconds, canonical text, the SHA-256 of the text it printed, and for a statement inside a block or procedure its `depth` and, when names or values were filled in as it ran, its `resolved` text. A block or call has an entry of its own, before the entries of its body. |
| `engines` | Every `SELECT ENGINE`: engine, step in microseconds and every effective setting. |
| `procedures` | Every `PROC`: name, header and the SHA-256 of the canonical body. |
| `decisions` | Every `IF`: the condition, the values it compared, and the outcome. |
| `loops` | Every `FOR EACH`: the noun, the `WHERE` condition, the number of elements and the names that matched, in order. |
| `events` | Every scheduled event as it was applied: the step statement that applied it, its time in microseconds, its text and the line that scheduled it. |
| `steps` | Every STEP SIM: from and to (microseconds), tick count, the engine, its final values (`final`, shortest round-trip decimal text) and `seriesSha256`, the SHA-256 of the engine's primary series over the new interval (the 64-bit pattern of each value, in order: frequency for the swing engines, rotor angle and speed for RMS, bus voltages for EMT). The three members `finalFrequencyHz`, `finalRocofHzPerS` and `frequencySeriesSha256` repeat the frequency values for the engines that have one. |
| `variables` | The named values at the end, by name, with their units. |
| `outputs` | Every REPORT, PRINT and EXPORT: kind, name, size and SHA-256. |
| `result` | `status` (`ok` or `error`), `finalClockUs`, and for an error its line, column, phase and message. |

The record holds no clock reading, host name or absolute path of its own, so the same script, the same GridSim version and
the same model give the same record and the same exported bytes. A path appears as it is written in the script.

## Grammar 1

### Lexical rules

- One statement per line, except that `FOR`, `IF` and `PROC` open a block of lines closed by `END`. A line may be blank. `#`
  starts a comment that runs to the end of the line.
- Keywords and units are case-insensitive: `trip generator "G1"` is `TRIP GENERATOR "G1"`.
- A name or path is in double quotes and is case-sensitive. Inside the quotes `\"` is a double quote and `\\` is a
  backslash; no other escape exists.
- A number is digits and an optional decimal part (`12`, `90.5`). There is no exponent form and no thousands separator. A
  minus sign is an operator: `-12 MVAR` is the negation of a literal and is read as one number.
- Units are `MW`, `MVAR`, `MVA`, `PU`, `KV`, `HZ`, `MS`, `S` and `DEG`. A number may stand without a unit (a count or a
  factor).
- Punctuation is `+ - * / ( ) , = < > <= >= !=`.
- A value name (for `LET`, a loop variable, a procedure or a parameter) is a letter followed by letters, digits and
  underscores. It is case-sensitive and may not be a keyword: the verbs, `END`, `ELSE`, `WHERE`, `AND`, `OR`, `NOT`, the
  nouns, the readings (including the single letters `P`, `Q` and `V`), the engine names and the units (including `S`).
- The canonical form of a statement is upper-case keywords, single spaces, numbers without trailing zeros; a block is
  indented by two spaces per level and closed by `END`. The run record and the console's recording use it.

### Statements

| Statement | Effect |
|---|---|
| `GRAMMAR 1` | Optional first statement. Any other version number is refused. |
| `LOAD CASE "path" [FREQUENCY n HZ]` | Loads a case and resets the session (solved state, engine, clock and scheduled events; named values and procedures stay). `FREQUENCY` sets the nominal frequency of a MATPOWER file, which carries none (Core assumes 50 Hz without it); it is refused for other formats. Relative paths are taken from the base folder (the script's folder, or `--base-dir`). |
| `SOLVE POWERFLOW` | Solves the plant as the script has left it, with the Core steady-state solver and its default options, and keeps the result as the solved state. A solve that does not converge is an error and the solved state stays as it was. |
| `TRIP GENERATOR\|LINE\|LOAD name` | Takes an element out of service. A tripped load keeps its set-point for `CLOSE`. `name` is a quoted name or a value that holds one. |
| `CLOSE GENERATOR\|LINE\|LOAD name` | Puts a tripped element back. |
| `SET GENERATOR name P\|Q\|V expression` | Sets a generator's active power (`MW`), reactive power (`MVAR`) or voltage set-point (`PU`, or `KV` on a bus with a base voltage). |
| `SET LOAD name P\|Q expression` | Sets a load's active power (`MW`, not negative) or reactive power (`MVAR`). |
| `FAULT BUS name` | A bolted three-phase fault at a bus. RMS and EMT engines only. |
| `CLEAR FAULT BUS name` | Clears it. |
| `REPORT BUSES` | Voltage magnitude and angle and net injection at every bus (see Reports and the solved state). |
| `REPORT BRANCHES` | From-end and to-end flows, loss and loading of every branch. |
| `REPORT ELEMENTS` | Every generator, load, line and bus by name, with its set-points and whether it is in service. Needs no solve. |
| `REPORT SIM` | The selected engine's script time and its state: the last tick of the live engine, or the summary of the last run of another engine. |
| `EXPORT RESULT "file"` | Writes the solved state as the Core solve export (`gridsim-solve-export/1`, ch. 31), with an empty metadata block (no time, host or timing), UTF-8, `\n` line ends. A file that exists with identical bytes is left alone; a file that exists with other bytes is an error: a script never overwrites. |
| `SELECT ENGINE kind [model] [STEP n MS\|S] [WITH key value ...]` | Selects the stepping engine (see Engines). `RMS`, `EMT`, `FREQUENCY` and `PROTECTION` require their model word. Resets the clock to zero and drops scheduled events. |
| `STEP SIM expression` | Runs the given simulated time as whole integration steps. |
| `AT expression statement` | Schedules a `TRIP`, `CLOSE`, `SET`, `FAULT` or `CLEAR FAULT` at the script time given. |
| `LET name = expression` | A new named value in the current scope. |
| `SET name = expression` | A new value for a name that exists, in the unit it has. |
| `PRINT expression` | Shows a value. |
| `IF condition ... [ELSE ...] END` | Runs one of two bodies. |
| `FOR EACH noun name [WHERE condition] ... END` | Runs the body once for each element of the collection that satisfies the condition. |
| `PROC name[(type name, ...)] ... END` | Defines a procedure. |
| `CALL name[(argument, ...)]` | Runs a procedure. |

### Names

Names are fixed when the case is loaded and do not change when an element trips.

- A generator is its model name, or `G<bus>` when it has none.
- A load is any bus with demand, named by the bus name, or `L<bus>` when the bus has none.
- A line is any branch, transformers included, named by its model name, or `<from>-<to>` when it has none.
- A bus is its model name, or its number as text (`"5"`) when it has none.
- A name met a second time gets `#2`, then `#3`, in model order (parallel lines `1-4`, `1-4#2`).

For case9 the generators are `G1`, `G2`, `G3`, the loads `L5`, `L7`, `L9`, the buses `"1"` to `"9"` and the lines `1-4`,
`4-5` and so on. `REPORT ELEMENTS` lists the names of any case.

### Units and arithmetic

A unit is a type. Each position accepts the units of one quantity and nothing else:

| Position | Accepted units |
|---|---|
| `LOAD CASE ... FREQUENCY` | `HZ`, from 10 to 100 |
| `SET ... P` | `MW` |
| `SET ... Q` | `MVAR` |
| `SET GENERATOR ... V` | `PU` (0.5 to 1.5) or `KV` |
| `STEP SIM`, `AT`, the `STEP` of `SELECT ENGINE` | `MS` or `S`; a whole number of microseconds |

A value in another unit (`SET LOAD "L5" P 90 MVAR`) is refused at validation, at the value's line and column. `MVA` and
`DEG` are in the type system; `DEG` is the unit of the bus angle reading and of the `SLIP` setting, and no grammar 1 statement
takes `MVA`.

An expression is a number with or without a unit, a named value, a reading, a bracket, or an operation on those. The rules,
checked before anything runs:

| Operation | Allowed when | Result |
|---|---|---|
| `a + b`, `a - b` | `a` and `b` have the same unit, or both are plain numbers, or both are times (`MS` and `S` mix; the result has the unit of `a`) | that unit |
| `a * b` | one side is a plain number | the unit of the other |
| `a / b` | `b` is a plain number; or `a` and `b` have the same unit (or both are times) | the unit of `a`; a plain number |
| `-a` | always | the unit of `a` |
| `ABS(a)`, `MIN(a, b)`, `MAX(a, b)` | `MIN` and `MAX` take the same unit twice | that unit |

So `5 MW + 3 MVAR` is refused (`unit mismatch: cannot add MVAR to MW`), and so are `2 MW * 3 MW` and `2 / 3 MW`, which would
need units grammar 1 does not have. Arithmetic is decimal, so `3 * 20 MS` is exactly 60 000 microseconds. Division by zero
and a result outside the range of a decimal are execution errors at the operator.

### Readings

A reading is the value of something in the session, with its unit.

| Reading | Unit | Value |
|---|---|---|
| `P OF GENERATOR n`, `Q OF GENERATOR n` | `MW`, `MVAR` | The set-point; 0 when the generator is tripped. |
| `V OF GENERATOR n` | `PU` | The voltage set-point. |
| `P OF LOAD n`, `Q OF LOAD n` | `MW`, `MVAR` | The demand now (0 when tripped). |
| `VM OF BUS n`, `VA OF BUS n` | `PU`, `DEG` | Voltage magnitude and angle of the solved state. |
| `PFROM OF LINE n`, `QFROM OF LINE n`, `LOSS OF LINE n` | `MW`, `MVAR`, `MW` | From-end flows and loss of the solved state. |
| `LOADING OF LINE n` | `PU` | Loading as a fraction of the rating (1 is 100 %). |
| `TIME` | `MS` | The script clock. |
| `FREQUENCY` | `HZ` | The frequency at the end of the last step of the live, frequency or protection engine. An error before the first step. |
| `LOSSES` | `MW` | Real-power losses of the solved state. |
| `GENERATION`, `DEMAND` | `MW` | Sum of the in-service set-points; sum of the demand. |

The solved state a reading uses is the last `SOLVE POWERFLOW`, which must be current (an edit since it makes it out of
date, an execution error), or under `LIVE` the live network as of the last step.

### Conditions

A condition compares two expressions with `=`, `!=`, `<`, `<=`, `>` or `>=` (same unit on both sides; `TIME >= 0.1 S` works
because both are times), or tests an element's status: `GENERATOR n IS TRIPPED`, `LINE n IS IN SERVICE`, `LOAD n IS
TRIPPED`. Conditions combine with `NOT`, `AND` and `OR` (in that order of binding) and brackets.

### Blocks

`IF condition`, `FOR EACH` and `PROC` open a block that ends at `END`; `IF` may have an `ELSE` body. Blocks nest to a depth
of 8. `LOAD CASE`, `SELECT ENGINE`, `GRAMMAR` and `PROC` stand on their own line and may not sit inside a block, so the case
and the engine are the same for every statement of a block.

A block is one statement. It is checked whole before it runs (units, scopes, literal names, the verb and the engine), and
if any statement in it fails at run time the session returns to the state before the block.

`FOR EACH noun name [WHERE condition]` runs its body for the elements of the noun in model order (`GENERATOR`, `LINE`,
`LOAD` or `BUS`), with `name` bound to the element. The collection is fixed when the loop starts; the `WHERE` condition is
evaluated for each element at its turn, against the state then, so earlier iterations can change later answers. In the
body, `name` stands wherever a quoted name of that noun is accepted (`TRIP GENERATOR g`, `P OF GENERATOR g`).

### Named values and scopes

`LET name = expression` makes a value in the current scope: the session at the top level, otherwise the block it is in.
`name` may not already name a value in scope. `SET name = expression` changes the value that exists (in whatever scope holds
it) and keeps its unit. A value made inside a block ends with it; a loop variable ends with its iteration. The values made
at the top level stay for the session and are listed in the run record. A value holds a number or a quantity; a model
element is held only by a loop variable or a procedure parameter.

### Procedures

`PROC name(type name, ...)` defines a procedure; the parameter types are `GENERATOR`, `LINE`, `LOAD`, `BUS` or a unit. `CALL
name(argument, ...)` runs it with one argument per parameter: for an element parameter a quoted name or a value that holds
one, for a unit parameter an expression of that unit.

- A procedure body sees its parameters and the session's named values as they are when it is called. It does not see the
  loop variables or local values of the caller.
- A procedure is defined once and never redefined. It is checked when it is defined, and its body is checked again against
  the arguments at each call.
- A body may call a procedure defined later in the script; the call is looked up when it runs.
- No procedure may call itself, directly or through other procedures. A script with a cycle is refused whole, before
  anything runs, at the `CALL` that closes the cycle. In the console, where procedures are defined one at a time, the
  definition that would close the cycle is refused.

### Engines

`SELECT ENGINE kind [model] [STEP n MS|S] [WITH key value ...]` selects one engine. An engine that could have more than one model needs its model word, so a bare name never changes meaning. The clause names settings, each once, typed:

| Engine | Core model | Default step | Settings | Edits during a run |
|---|---|---|---|---|
| `STATIC` | none | none | none | any (the plant only) |
| `LIVE` | `LiveSim` in hold-steady mode, the network re-solved every tick | 20 ms | `SOLVER AUTO\|DENSE\|SPARSE\|SWEEP`, `ASSETS ON\|OFF` | `TRIP`, `CLOSE`, `SET` on generators, loads and lines |
| `FREQUENCY LUMPED` | `FrequencyDynamics.Simulate`: one swing for the whole system | 20 ms | `DROOP n PU`, `RESPONSIVE n PU`, `GOVERNOR n S` | `TRIP`, `CLOSE`, `SET P` on generators and loads (a step of infeed) |
| `PROTECTION FREQUENCY` | `AssetFrequencyDynamics.Simulate`: the swing with the case's protection devices and asset fleet, frequency stages only | 20 ms | `DROOP`, `RESPONSIVE`, `GOVERNOR` | as `FREQUENCY` |
| `RMS CLASSICAL` | `ClassicalInit` and `ClassicalMultiMachine`: constant-E' machines on the reduced network, RK4 | 5 ms | `INERTIA n S`, `DAMPING n PU`, `SLIP n DEG` | `FAULT BUS`, `CLEAR FAULT BUS` (one fault window) |
| `RMS SUBTRANSIENT` | `RmsTransientStability.StudyEventsObserved`: six-state subtransient machines on the sparse full network, from a machine dynamic data file | 5 ms | `DYNAMICS "file"` (required), `SCHEMAS "folder"`, `SLIP n DEG` | `FAULT BUS`, `CLEAR FAULT BUS` (any number of windows) |
| `RMS GOVERNED` | the same, with the governors and exciters (and stabilisers) of the file | 5 ms | as `SUBTRANSIENT` | as `SUBTRANSIENT` |
| `EMT SINGLE_PHASE` | `EmtNetwork.FromGridModel` and `EmtEngine`: single-phase trapezoidal with critical damping adjustment | 0.1 ms | `SOURCE "bus"` | `FAULT BUS`, `CLEAR FAULT BUS` |
| `EMT THREE_PHASE` | `ThreePhaseEmtEngine`: abc nodes with coupled branches | 0.1 ms | `SOURCE "bus"`, `PROBE "bus"` | `FAULT BUS`, `CLEAR FAULT BUS` (one window per bus) |

Notes on each:

- **LIVE** starts at the plant as it stands. A trip, close or set-point change made while it is selected changes the
  network the live simulation re-solves, from the next tick on, and (for infeed) the frequency swing. `ASSETS OFF` builds
  the engine without the authored asset fleet of a case that carries one; with the fleet running, a generator cannot be
  tripped or closed, because the fleet binds units by position (a set-point change and line and load edits are fine).
  Core's `LiveSim.ReplaceNetwork` is what takes the changed network.
- **RMS SUBTRANSIENT** and **RMS GOVERNED** take machine dynamic data from a `gridsim-dynamics/2` file (the JSON format
  Core reads): `DYNAMICS "file"`, relative to the base folder, and `SCHEMAS "folder"` for the v2 schemas, which are found
  above the base folder or the program when not given. Core's loader checks the file against the schemas and binds its
  machines to the case's generators. The selection is refused, naming what is missing, when the file is not valid, a
  generator has no machine, a unit is converter plant, an integrated machine has no inertia constant in the file or the
  case, or (for `GOVERNED`) no machine has a governor. There is no fallback to `CLASSICAL`. The machines are the six-state
  subtransient model on the sparse full network. `SUBTRANSIENT` uses the machines only and does not use the controllers in
  the file (the engine record says so); `GOVERNED` adds the governors and, where the file gives them, exciters and
  stabilisers. A governor or exciter that cannot start at the solved operating point (a valve limit below the unit's
  output) is refused by Core with its reason. The dynamic data file is an input of the run record (kind `dynamics`, with its
  SHA-256). A fault needs a time above 0 ms. Only faults are events; the line trips and load steps that Core's event study
  has are not in grammar 1 for this engine.
- **EMT THREE_PHASE** runs Core's `ThreePhaseEmtEngine`, which has no case builder of its own; the scripting layer builds the
  network from the case: each in-service branch as a coupled three-phase R-L branch with its charging as per-phase
  capacitors, each bus's demand and shunt as per-phase impedances, and one ideal three-phase source at the first generator
  bus (or `SOURCE`). A branch with `R0` and `X0` gives the coupled form whose zero-sequence impedance is the case's; a
  branch without them has zero-sequence equal to positive. Transformer taps, phase shifts and negative demand are refused.
  Faults are bolted three-phase faults at a bus (one window per bus), and the engine records the three phase voltages of
  one `PROBE` bus (the first bus by default), not every bus. Unbalanced faults and the other devices of the Core engine are
  not reachable from grammar 1 yet; each would be a new optional clause.
- **PROTECTION FULL is not provided.** Voltage and loading stages need a network solve at every step. Core has no batch
  whole-horizon protection run that does that: `AssetFrequencyDynamics.Simulate` is lumped, and the only Core path that
  feeds network state to the protection devices and the asset fleet is `LiveSim`, which `SELECT ENGINE LIVE` already drives
  (with `ASSETS ON`, the default). `PROTECTION FREQUENCY` therefore refuses what it cannot evaluate and names `LIVE`.
- **FREQUENCY** and **PROTECTION** treat the system as one mass. Inertia is the case's own; a case without inertia data
  (a MATPOWER file) takes the live simulation's substitute of 4 s on the machine ratings. Line edits, `SET Q` and `SET V`
  have no meaning for a lumped swing and are refused. `PROTECTION FREQUENCY` needs a case with a protection bundle and
  operates its devices (under-frequency load shedding, loss-of-mains trips). It evaluates frequency stages only and never
  skips a stage: a case whose devices or inverters carry voltage or loading stages (ride-through) is refused at selection,
  naming how many, and `SELECT ENGINE LIVE` is the engine that evaluates them against the network. The GB synthetic case
  has ride-through stages; the tests run it with those stages set to zero.
- **RMS** needs a current `SOLVE POWERFLOW`. Machines are the in-service generators with an inertia constant; a generator
  with none is held as a constant admittance, so a MATPOWER case needs `WITH INERTIA n S`, which gives that constant to every
  machine that has none. `DAMPING` is per unit on the system base, `SLIP` the angle from the inertia-weighted centre at which
  synchronism is declared lost (default 360 degrees). The transient reactance is Core's 0.30 pu default. The classical model
  takes one fault window per run.
- **EMT** energises the network from one ideal AC source at the nominal frequency, at the first generator bus or the bus
  given by `SOURCE`. Loads are constant impedances; every bus has an open fault switch so the network is the same whatever
  faults the script applies. It records every bus voltage.

A setting outside its range, in the wrong unit, repeated, or not an option of the engine is refused at validation at the
setting's column.

### Stepping

`SELECT ENGINE` creates the engine at script time 0. Its integration step is fixed for the life of the engine. A `STEP SIM`
duration, an `AT` time and the step must all be whole multiples of it; any other value is refused at validation, naming the
integration step.

- `STEP SIM n` takes `n / step` integration steps.
- `AT t` events apply at the start of the step that begins at `t`, so an event always lands on a step boundary. An event at
  the current script time applies at the start of the next `STEP SIM`. Events at the same time apply in script order. An
  event at or after the end of a step stays scheduled for a later one. An event cannot be scheduled in the past.
- An immediate edit or fault while an engine is selected happens at the current script time and is felt from the next step.
- The events a `STEP SIM` will apply are checked, in order, before the engine moves. If one cannot apply (it trips an
  element a previous event already tripped, or clears a fault that is not there), the statement is refused at that event's
  line and column and nothing has moved. If the live engine itself fails part-way (a network solve that does not
  converge), it is rebuilt and replayed to the state before the statement; the live simulation is deterministic, so the
  rebuild is bit-identical.

**Stepping the whole-horizon engines.** The live simulation can be driven one tick at a time. `FREQUENCY`, `PROTECTION`,
`RMS` and `EMT` are computed over a whole time range, so they are stepped by deterministic re-run: a step to a new time
re-runs the engine from t = 0 to that time with every event so far, checks that the part already shown is reproduced to the
bit (a digest of the earlier part of the new run must equal the digest taken when it was first shown, or the step fails),
and reports only the new interval. Cost grows with the time reached; it is fine for short experiments. Because the engine
is a pure function of its settings and events, stepping 100 ms ten times, or 1 s once, gives the same numbers at 1 s.
To keep the earlier part untouched, an event at time `t` acts from the step that begins at `t`: for `RMS` its edge sits a few
millionths of a step after `t` (the second stage of that step's RK4), for `EMT` it is applied one step later in the run's own
clock.

### Reports and the solved state

Under `STATIC` and the whole-horizon engines, `REPORT BUSES` and `REPORT BRANCHES` show the last `SOLVE POWERFLOW`, which must
be current. Under `LIVE`, once a step has run, they show the live network's solved state as of the last step, with a first
line naming the script time and tick (`live network as of the last step, t = 100 ms (tick 4)`); before the first step they
show the last solve. The readings `VM`, `VA`, `PFROM`, `QFROM`, `LOSS`, `LOADING` and `LOSSES` use the same state. `EXPORT
RESULT` always writes the last `SOLVE POWERFLOW`.

### Grammar 1 compatibility promise

Grammar 1 is frozen from this release for all of 2.x. A script that runs under it computes the same results, prints the
same text and writes the same record members in every later 2.x release, unless a bug in Core itself is fixed (Core's
own numerical changes are announced in the changelog and move the results of the Core verbs equally).

What is frozen:

- the statements, their word order and their canonical form, the units and the unit rules of every position, the
  arithmetic rules and the reading table, the naming rule for elements, and the order in which collections are visited;
- the meaning of each engine as named: `LIVE`, `FREQUENCY LUMPED`, `PROTECTION FREQUENCY`, `RMS CLASSICAL`,
  `RMS SUBTRANSIENT`, `RMS GOVERNED`, `EMT SINGLE_PHASE`, `EMT THREE_PHASE`, with their defaults (steps, settings) and the event-landing rules in Stepping;
- the words reserved in grammar 1 (the verbs, keywords, nouns, readings, units and the model words);
- the existing members and meanings of the run record (`gridsim-script-run/1`).

How additions stay additive:

- A new capability is a new statement, a new model word after an existing engine name, a new `WITH` setting, a new reading
  or a new unit, never a changed meaning for an existing form. The engines that could have more than one model require
  their model word now, so a bare engine name is never a statement whose meaning can drift.
- A statement that grammar 1 refuses (an unknown verb, an unknown model word, an unknown setting) may become accepted later; a
  statement that grammar 1 accepts is never refused later and never computes differently.
- A new keyword is recognised only where a keyword is expected, so a value name that already works keeps working.
- The run record gains members and never loses or changes one. A new engine setting appears in `engines[].settings` only
  when the script sets it or the engine has it, and the defaults of existing settings do not change.
- Something that cannot be added without changing existing results (for example, evaluating a stage that
  `PROTECTION FREQUENCY` refuses today) gets a new model word, not a change to the old one.

### Stages and failure

A script runs in three stages. The whole text is parsed first; a syntax error anywhere refuses the script and nothing
runs. The procedures of the script are then checked (defined once, no cycle). Each top-level statement is then checked as a
whole (a case must be loaded, every literal name must exist, the verb, noun and engine must go together, every unit must
fit, every named value must exist) and executed, each simple statement inside it validated against the state it runs in
(ranges, whole multiples of the step, events in the past).

An error names the line and column, the stage (`syntax`, `validation` or `execution`) and the reason, and stops the
script. The session is left exactly as it was before the failing top-level statement: the plant, the solved state, the engine
(including its internal state), the clock, the scheduled events, the named values and the run log. The statement's output is
discarded.

```text
gridsim-script run docs/_captures/scripting/refused.gss --base-dir .
loaded "case9": 9 buses, 9 branches, 3 generators, 3 loads, base 100 MVA, 60 Hz, sha256 db4af3785d6a
power flow converged in 4 iterations, largest mismatch 1.640E-014 pu, losses 4.6410 MW, solver sparse-newton
refused.gss:4:17: validation error: unit mismatch: expected MW, got MVAR
(exit code 1)
```

### The console

`gridsim-script` with no arguments (or `repl`) opens an interactive session. The prompt is `gss> `; `help` lists the verbs
and `exit` ends the session. A line is parsed, validated and executed on its own; a line that opens a block (`FOR`, `IF`,
`PROC`) changes the prompt to `...> ` and the block runs when its `END` is entered. The session is recorded as
`session-<UTC time>.gss` in the record folder (`--record-dir`, default `gridsim-sessions` under the base folder), starting
with `GRAMMAR 1`. A line or block that ran is written as typed. A refused one is written as comments, the error and the
text, so the recording keeps its line numbers and, run again, reaches the same session. When the session ends, the run
record is written beside it as `session-<UTC time>.run.json`.

`gridsim-script run FILE.gss` runs a file, `gridsim-script check FILE.gss` parses it and checks its procedures without
running anything (exit 0 or 1), and `gridsim-script --version` prints the GridSim version and the grammar version.

### The console panels

The desktop app and the web app each host the same console as a panel. Neither has a second interpreter: both call
`GridSim.Scripting`, the library `gridsim-script` is built on. An interactive session is the library's `InteractiveConsole`
(the loop `gridsim-script` itself runs) and a script file is `Runner`, so the same lines give the same `.gss` recording and the
same run record, byte for byte, in the console, the desktop panel and the web panel. The library's hosting hooks are
additive: `Session.Gate` lets a host refuse a statement before it runs, `Session.Limits` caps the engine ticks of a step and
of a session and carries a cancellation token, and `StatementEffects` says which statements change state. `gridsim-script` sets none of
them.

**Desktop.** **File > Script console...** opens a window with an input line (Up and Down walk the history), an output pane, a
box for a `.gss` path with **Run file** and **Check only**, **Load open case** (types `LOAD CASE` for the MATPOWER file or case
folder open in the main window) and **End session**. A refusal is shown as `refused: line 4, column 1: validation error: ...`
for a typed line and as `file.gss:4:1: validation error: ...` for a file. The session is recorded as
`session-<UTC time>.gss` and `.run.json` in `Documents\GridSim\sessions` (a script file's run record is written there as
`NAME.run.json`), when the session ends: by `exit`, **End session** or closing the window. Statements run on a worker thread,
one at a time, so the window stays responsive. See [ch. 8](<0008 The desktop app.md>).

**Web.** `/console` has a script box with **Run** and **Check only**, and an interactive session with history that is
recorded for download as the `.gss` and the `.run.json`. The host applies its role gate before a statement runs:

| Verb | Needed |
|---|---|
| `GRAMMAR`, `LOAD CASE` (a model the catalogue lists), `REPORT`, `PRINT`, `LET`, assignment, and the check-only mode | any caller (Viewer) |
| `SOLVE POWERFLOW` | Analyst, as `POST /api/solve` |
| `TRIP`, `CLOSE`, `SET`, `FAULT`, `CLEAR FAULT`, `SELECT ENGINE`, `STEP SIM`, `AT`, and any `IF`, `FOR EACH`, `PROC` or `CALL` that contains one | Operator |
| `LOAD CASE` of a model under `results/` or `gda-out/` | Operator, as for the dashboard and the workbench |
| `EXPORT RESULT`, and `LOAD CASE` of a file or folder the catalogue does not list | refused for every role (the web console reads and writes no other file) |

A script that holds a statement the caller may not run is refused whole, naming the line and column, before any statement
of it runs; a line typed in a session is refused the same way and the session goes on. Each role refusal is one `AccessDenied` audit
entry, as on the other web surfaces, and the licence gate applies to every model loaded. The limits are stated on the page: a script
of at most 64 KB, one `STEP SIM` of at most 100000 engine ticks and a session of 400000, output cut at 256 KB, one free solve slot,
and the host's solve timeout. See [ch. 9](<0009 The web app and REST and WebSocket API.md>).

## Variations

- Re-run a recorded session: `gridsim-script run gridsim-sessions/session-20261118-101500.gss`.
- Compare two runs: `scriptSha256`, `canonicalScriptSha256`, `modelSha256`, every `outputs[].sha256` and the
  `steps[].seriesSha256` of two runs are equal exactly when their scripts, models and results are.
- Static what-if: `TRIP LINE "4-5"`, `SOLVE POWERFLOW`, `REPORT BRANCHES`, `CLOSE LINE "4-5"`.
- Every unit above a size: `FOR EACH GENERATOR g WHERE P OF GENERATOR g > 500 MW` followed by `AT 1 S TRIP GENERATOR g` and
  `END` schedules the loss of all of them at once, which `SELECT ENGINE PROTECTION FREQUENCY` plays through the
  under-frequency and loss-of-mains devices of a case that has frequency stages only.
- Find which bus has the lowest voltage: `LET low = 2 PU`, then a `FOR EACH BUS b` that does `IF VM OF BUS b < low`, `SET low =
  VM OF BUS b` and `END`.

## Pitfalls

- A MATPOWER file without `FREQUENCY` is a 50 Hz case. Core assumes 50 Hz for a file that carries no frequency, and the live
  frequency swing runs at that nominal.
- `REPORT BUSES` after an edit and before a new `SOLVE POWERFLOW` is refused under `STATIC`: the solved state is out of date,
  and the report does not show the old numbers as current.
- A `SOLVE POWERFLOW` that does not converge is an error, and the plant keeps the trips that led to it; `CLOSE` the element
  or `SET` a smaller load and solve again. Under `LIVE` a step whose network solve does not converge fails the same way.
- `EXPORT RESULT` to a path that exists with other content is refused. Remove the file, or export under another name.
- Paths are taken from the base folder, not the shell's current folder, unless the script is in it.
- A reading is a decimal with 15 significant digits, not the raw double of the engine; comparisons in a script are exact on
  those decimals.
- `P`, `Q`, `V` and `S` are keywords, so they cannot name a value; use `p_gen` or `volts`.

## Limits

- The whole-horizon engines are re-run from t = 0 at every step. A long `EMT` run at a fine step, stepped in many small
  pieces, repeats work quadratically.
- `RMS CLASSICAL` is the classical model (constant EMF behind 0.30 pu transient reactance by default, one fault window). It reads the
  inertia from the case or from `WITH INERTIA`; it does not read dynamic data files, exciters or governors. The dynamic models read `gridsim-dynamics/2` JSON only (PSS/E DYR data is not read by the scripting layer), integrate no converter plant and take no line, load or generator events.
- `EMT SINGLE_PHASE` and `EMT THREE_PHASE` have fault switches only; line and equipment edits belong before `SELECT ENGINE`. The three-phase engine is a positive-sequence build with optional zero-sequence mutuals and one probe bus.
- `FREQUENCY LUMPED` and `PROTECTION FREQUENCY` solve no network, so they carry no voltages or flows.
- With the live engine's asset fleet running, a generator trip or close is refused (see Engines); build the engine with
  `WITH ASSETS OFF` to trip generators in a case that carries authored assets.
- A named value holds a number or a quantity; there are no text values, lists or user-defined types.
- `gridsim-script` itself has no role gate: it runs on the machine that starts it, with that user's rights. The role gate and the
  limits above belong to the web panel; the desktop panel is the same local use as the executable.
- The web panel stops a run at the host's solve timeout between statements; a single statement already running finishes, and keeps its
  solve slot until it does.

## See also

- Chapter 2, loading networks; chapter 5, running power flows; chapter 14, frequency studies; chapter 31, exports and
  formats (the solve export that `EXPORT RESULT` writes).
