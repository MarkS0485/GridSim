# 19. CLI reference

Every verb and flag the `gridsim` command line tool accepts, checked against the argument parsing in
`src/GridSim.Cli/Program.cs`, `VerbHelp.cs`, `VerbFlags.cs`, `LicenceCli.cs` and `StorageCli.cs` (the project is
`GridSim.Cli`; the binary it builds is `gridsim`). `gridsim` means the installed binary; from a checkout it is
`dotnet run --project src/GridSim.Cli -c Release --` ([ch. 1](<0001 Getting started.md>)).

This chapter is compiled into the binary. `gridsim <verb> --help` prints the verb's section, and the flags a section
names are the flags the verb accepts (see [Parsing rules](#parsing-rules-and-exit-codes)), so a flag the code reads
always has a row here.

## Verb index

Verb names are case-insensitive (`licence` excepted, which must be lower case). Every verb dispatched in `Program.cs`
has a section below.

| Group | Verbs |
|---|---|
| Software licence | [`licence`](#licence) |
| Help | [`help`](#help) (and `gridsim <verb> --help`) |
| Running a case | [no verb](#running-a-case-no-verb) |
| Contingency and analysis | [`trip`](#trip), [`screen`](#screen), [`nminus1`](#nminus1), [`nminus2`](#nminus2), [`n-1-1`](#n-1-1), [`nminus1-sweep`](#nminus1-sweep), [`stress`](#stress), [`chain`](#chain), [`cycle`](#cycle) |
| Network analysis | [`pf`](#pf), [`ptdf`](#ptdf), [`lodf`](#lodf), [`dcpf`](#dcpf), [`dcopf`](#dcopf), [`acopf`](#acopf), [`acopf-full`](#acopf-full), [`scuc`](#scuc), [`scopf`](#scopf), [`qsts`](#qsts), [`hosting`](#hosting), [`cpf`](#cpf), [`taps`](#taps), [`restore`](#restore), [`losses`](#losses), [`dist3ph`](#dist3ph) (experimental) |
| Physics and protection | [`physics-check`](#physics-check), [`conformance`](#conformance), [`protection-check`](#protection-check), [`grade`](#grade), [`fault`](#fault), [`iec60909`](#iec60909), [`experimental`](#experimental) |
| Frequency and dynamics | [`freq`](#freq), [`replay`](#replay), [`transient`](#transient), [`domains`](#domains), [`stability`](#stability), [`modal`](#modal), [`fit-frequency`](#fit-frequency), [`inertia-placement`](#inertia-placement) |
| Harmonics, impedance and EMT | [`harmonics`](#harmonics), [`impedance-stability`](#impedance-stability), [`emt`](#emt) |
| Evidence | [`replay-event`](#replay-event), [`scenario`](#scenario), [`h0-study`](#h0-study), [`fleet`](#fleet), [`evidence`](#evidence) |
| Data | [`gda`](#gda), [`gda-replay`](#gda-replay), [`synth`](#synth), [`export`](#export), [`convert`](#convert), [`validate`](#validate), [`zonal`](#zonal), [`pmu-ingest`](#pmu-ingest) (experimental), [`pmu-estimate`](#pmu-estimate), [`estimate`](#estimate), [`estimate-noise-floor`](#estimate-noise-floor), [`crossval`](#crossval) |
| Real-time and telemetry | [`serve`](#serve), [`interpose`](#interpose) |
| Benchmarking and preflight | [`bench`](#bench), [`bench --estimate`](#bench---estimate), [`bench --perf`](#bench---perf), [`gpucheck`](#gpucheck), [`simdcheck`](#simdcheck), [`batchsolve`](#batchsolve), [`audit`](#audit), [`compliance`](#compliance) |
| Electrical references | [`ref`](#ref) |

`gridsim --help` (or `-h`, or `gridsim help`) prints the verb list, then exits 0. `gridsim <verb> --help`,
`gridsim <verb> -h` and `gridsim help <verb>` print that verb's section of this chapter (see [help](#help)).

## Parsing rules and exit codes

- **Unknown flag names fail loud.** Every `--flag` is checked against the set of flags the CLI knows. A misspelling
  prints `error: unknown flag '<flag>' (misspelled?)` and exits 2. The message names `gridsim <verb> --help` when the
  first word is a verb (`gridsim --help` otherwise) and this chapter, `docs/manual/0019 CLI reference.md`.
- **A flag of another verb fails loud too.** Each verb takes the flags its own section below names (the text
  `gridsim <verb> --help` prints), its group's shared notes, the [global switches](#global-switches) and, on the
  [result-file](#result-files) verbs, `--json`, `--csv` and `--out`. Any other flag prints `error: <verb> does not
  take <flag> (it is not one of <verb>'s flags, so it would be silently ignored)` and exits 2, so `trip --top 5`
  stops instead of running without `--top`. The no-verb run (`gridsim case14 --once`) takes its one case and the global
  switches only: any other flag, a second case or an extra word prints `error: the case run (no verb) takes one case and
  the global switches only, so '<token>' would be silently ignored` and exits 2 (GR-202).
- **Each value flag once.** A value flag given twice (`--top 5 ... --top 10`) prints `error: <flag> given more than
  once` and exits 2; it used to take the first and drop the second without a word (GR-670). The repeatable flags
  (`--event`, `--bess`) say so in their rows.
- **Flags have two dashes.** A single-dash word (`-bus 5`) prints `error: unknown option '-bus': flags start with two
  dashes` and exits 2 (GR-683). A negative number (`-5`, `-0.25`) is a value, and `-h` (help) and `-d` (`--dir`) are
  the two short forms.
- **`--name=value` works for every flag** and is treated exactly like `--name value`.
- **Numeric flags fail loud.** A flag present with an unparseable value (`--ticks 5O`) prints `error: <flag> expected
  <kind> but got '<value>'` and exits 2. Numbers parse in the invariant culture (`.` is the decimal point). `NaN` and
  `Infinity` are refused.
- **A value flag present with no value fails loud.** If a numeric or string flag (`--minutes`, `--out`, `--catalog`,
  ...) is the last token, or is followed by another `--flag`, the CLI prints `error: <flag> expected <kind> but got
  ''` and exits 2. Only an absent flag takes its default, so a shell variable that expanded to nothing is caught.
- **Enum flags fail loud.** `--rule`, `--converter`, `--criterion`, `--solver`, `--method` and `--gpu=<vendor>` accept
  only the values listed for them; anything else exits 2.
- **Boolean flags** are presence-only and may appear anywhere on the line.
- **Positional arguments** (a case name, an event id) are the first free token that is neither a flag nor a flag's
  value. A verb that takes one case refuses a second (exit 2) rather than ignoring it (GR-676).
- **Usage banners go to stderr.** A verb run without a required argument prints its usage lines on stderr and exits
  2; stdout stays empty (GR-687).

Captured refusals (the text is the program's own):

```
$ gridsim nminus1 case14 --no-such-flag
error: unknown flag '--no-such-flag' (misspelled?). Run 'gridsim nminus1 --help' for the flags nminus1 takes, or see docs/manual/0019 CLI reference.md.
[exit 2]
```

```
$ gridsim trip --top 5
error: trip does not take --top (it is not one of trip's flags, so it would be silently ignored). Run 'gridsim trip --help' for the flags trip takes, or see docs/manual/0019 CLI reference.md.
[exit 2]
```

| Exit code | Meaning |
|---|---|
| 0 | Success. |
| 1 | Input error: a missing or unloadable file or case folder, invalid data in a case (for example a generator whose Pmin is above its Pmax, refused by `dcopf` and `acopf`), an invalid measurement set or window file (`serve` skips it and exits 1 at the end), a case that fails to load inside a batch run (`bench`, `nminus1-sweep`, `stress`, `transient --cases`), a requested [result file](#result-files) that cannot be written. Also a failed check: the physics-law gate (`physics-check`, `conformance`), a right-half-plane eigenvalue (`modal`), a missed High tier (`cycle`), a batched result off the CPU reference (`batchsolve`), a singular harmonic network (`harmonics`), a violated experimental law, a bus the `iec60909 --all-buses` sweep cannot study. |
| 2 | Usage error: an unknown flag, a flag of another verb, a bad flag value, an unknown verb, an unknown case name ([one rule on every verb](#case-arguments)), a bus id that is not in the case (`stability --fault-bus`, `fault --bus`, `protection-check --fault-bus`, `iec60909 --bus`, a `--bess` site), a licence refusal (a network above the tier's bus limit, or a key `licence import` rejects), a forward-inference rejection (`REJECTED (forward inference)`, on every verb that reads dated data). |
| 3 | A verdict or convergence failure: a baseline that is not `Reproduced`, an estimate that is not converged or not observable, a `crossval` mismatch, non-converged steps (`gda-replay`, `transient`, `chain`), an EMT numerical failure, an infeasible dispatch (`dcopf`, `acopf`), a contingency verdict that is not secure (`nminus1`, `nminus2`, `n-1-1`, `nminus1-sweep`: the base case or any screened outage violates a limit, islands or does not solve; `screen`: any loss breaches a frequency limit), and a power flow that did not converge on the verbs that report it: the default solve report, `--enableExport`, a `--ticks` run with any tick that did not converge, `pf`, `losses`, `taps`, `cpf` (the base load does not converge), `gda --solve` and `--all-gsps`, `serve` with failed windows, `fault` and `protection-check` (including a converter current iteration that does not converge), the stability and modal verbs, `impedance-stability`, the experimental models and `conformance` (a case that stops converging or cannot be run). Also a failed probe or solve in `gpucheck`, `bench` and `bench --estimate`. |
| 77 | `crossval` only: the environment cannot run the check (no Python, or no pandapower). |

A verb's own section lists its exceptions. `physics-check` exits 1, not 3, on a converged state that fails the
hard-law gate (a physically impossible state); a power flow that did not converge exits 3.

**Default output location.** A verb that writes files writes its default output under `results/` in the current
directory (`results/<verb>-<case>` folders, `results/stress.md`, `results/gridviz.json`, `results/exports/...`, as each
section's `--out` default says) and creates the folder when needed. An explicit `--out <path>` (or
`--enableExport=<path>`, `--json`, `--csv`) is used exactly as given. The desktop and web apps find run folders in a
`results/` folder up to seven levels above themselves, so runs started from the repository root are discovered.
`synth` follows the same rule: with no `--out` it writes `results/synth/gb-synthetic` (N256; it used to write the
bundled `data/gb-synthetic` beside the executable, replacing the shipped case).

**MATPOWER parse notes.** Every verb that loads a MATPOWER `.m` file (a path, a bundled `.m` name, or a built-in name
backed by one such as `gb-reduced-29`) prints the parser's notes on stderr, one `note: <file>: <text>` line each,
before its own output: the nominal frequency it assumed (MATPOWER files carry none), DC lines read but not modelled
(with a `warning:` line as well), isolated buses dropped, out-of-service generators kept, reference buses with no
in-service unit, PV buses solved as PQ and similar. stdout is not affected. The built-in IEEE names (`case14`, ...) are
parsed inside the program from embedded copies and print no notes; their nominal frequency is the assumed 50 Hz, except
`case9` / `ieee9`: the WSCC 9-bus system is a 60 Hz system and carries 60 Hz (October 2026, N017). A `.m` file's frequency can be declared with the global `--frequency-hz <Hz>` switch (N187, below);
with it the "nominal frequency not given" note is not printed.

## Result files

The verbs listed below can write their result to a file as well as print it. The console output does not change: every
message about the files goes to stderr, so stdout is the same with or without these flags.

| Flag | Default | Meaning |
|---|---|---|
| `--json <file.json>` | - | The whole result as one JSON document. |
| `--csv <file.csv>` | - | The result as CSV files (layout below). |
| `--out <file.json\|file.csv>` | - | The same, with the format chosen by the extension. |

The verbs: `dcopf`, `acopf`, `acopf-full`, `scuc`, `scopf`, `qsts`, `hosting`, `cpf`, `taps`, `restore`, `ptdf`, `lodf`, `dcpf`, `iec60909`, `fault`, `protection-check`,
`grade`, `nminus1`, `nminus2`, `n-1-1`, `cycle`, `freq`, `domains`, `inertia-placement`, `dist3ph`, `losses`, `physics-check`,
`experimental`, `convert` (`--json` and `--csv` only: its second argument is already its output), `validate`, `zonal`,
`pf`, `pmu-ingest` and `pmu-estimate`. `serve` does not take them: it is a resident stream that reports each measurement window as it
arrives and may run without end, so one document written when it stops does not fit it. On every other verb `--json`
and `--csv` exit 2 (`modal`'s own `--json` and `screen`'s own `--csv` excepted: `screen --csv <file>` writes its
screening table, and `modal --json` prints JSON on the console and takes no file, `modal --export <file.json>` being
its file output).

- **What is written.** The verb's summary values and its tables, from the same values it prints, but complete: where
  the console lists the top N rows (dispatch, distribution factors, outages, branch losses, traced loads), the file
  has every row, and no number is rounded to the console's display precision.
- **JSON.** One document: `{"schema": "gridsim-cli-result/1", "tool": "gridsim <version>+<commit>", "verb", "args",
  "exitCode", "summary": {key: value}, "tables": {name: [{column: value}, ...]}}`. Keys and column names are
  snake_case.
- **CSV.** The verb's main table goes to the named file, every other table beside it as `<stem>-<table>.csv`, and the
  summary as `<stem>-summary.csv` (`key,value`, starting with the rows `verb` and `exit_code`). A verb with no table
  writes only the summary, to the named file. Invariant culture (`.` decimal point); a field holding a comma, a quote
  or a line break is quoted; booleans are `true` / `false`; an empty field is a value that does not apply.
- **Numbers** are written in round-trip form, so reading the file back gives exactly the bits GridSim computed. JSON
  has no NaN or infinity, so those are written as the strings `"NaN"`, `"Infinity"` and `"-Infinity"` (in CSV, the
  same words).
- **When.** The files are written once the verb has finished its result, including a result whose verdict fails
  (`NOT SECURE`, `GATE: FAIL`, `INFEASIBLE`, `NOT GRADED`, a failed round trip): the exit code is inside, so a script
  can read why. A run that stops before it has a result (a usage error, a case that does not load, a solve that does
  not converge) writes nothing. When the verb itself ends without a result it says so on stderr (`[<verb>] no result
  file written: ...`); a refusal that ends the run at once (a usage error or an unknown case, exit 2) prints only its
  own error line. A file of the same name
  is replaced; a missing folder is created. The rules of [Output files](#output-files) apply.
- **Errors.** A flag with no value, a name with the wrong extension, the same flag twice, or two flags naming one file
  exit 2 before anything runs. A file that cannot be written exits 1.

## Python

`python/gridsim` is a pure-Python package (standard library only) that runs these verbs and reads their result
document, so scripts get the CLI's numbers bit for bit without scraping the console. It runs
`gridsim <verb> <args...> --json <temporary file>` and returns a `Result` with `verb`, `args`, `tool`, `exit_code`,
`summary` (a dict), `tables` (table name to a list of row dicts), `stdout` and `stderr`; the strings `"NaN"`,
`"Infinity"` and `"-Infinity"` come back as floats. A run that writes no result raises `GridSimError` with its exit
code and its last stderr line.

```python
import sys; sys.path.insert(0, "python")   # or put python/ on PYTHONPATH
import gridsim
gs = gridsim.GridSim()          # the CLI: cli=..., GRIDSIM_CLI, gridsim on the PATH, or this checkout's build
r = gs.pf("case14")             # gs.run("pf", "case14"); every result-file verb has a method (n_1_1, physics_check, ...)
r.summary["loss_mw"], r.table("buses")[0]["vm_pu"]
```

It wraps exactly the verbs listed above (a test keeps its list equal to the CLI's) and refuses `--json`, `--csv` and
`--out`, which it gives itself. The other verbs write their own folders or reports, or run without end, so they are run
with `subprocess` and their files read. Its tests: `python -m unittest discover -s python/tests` (the ones that run the
real CLI skip when none is found). See `python/README.md`.

## Output files

Every file and folder the CLI writes follows one rule (`src/GridSim.Cli/CliFiles.cs`), whether its path is a default
under `results/` or one you named with `--out`, `--export`, `--csv`, `--json`, `--pv-out`, `--out-store`,
`--dump-catalog` or `--enableExport`:

- **The folder is created.** A missing parent folder is created, so `--export results/new/x.json` works.
- **An existing output is replaced.** A re-run replaces its own outputs, default or named: naming a file asks for it.
- **An input is never replaced.** The CLI refuses, with exit 2 and `error: refusing to write the <what> to '<path>':
  <why>`, before anything is written to that path, any output that is a file the command line names as an input (any
  existing file named by an argument that is not the value of one of the output flags above), an existing file inside a
  case folder the command line names (a folder holding `system.json`), an existing file of the bundled `data/` folder
  next to the executable, or an existing folder.

Three writers keep stricter rules of their own. `interpose` never replaces a captured window: a clash is kept under a
`-dupN` name. `convert` and the JSON case writer never write over an existing case (exit 1). `synth` regenerates its case
folder (by default `results/synth/gb-synthetic`); the bundled `data/gb-synthetic` is rewritten only when `--out` names it.

A run that writes a report FILE (`bench`, `nminus1-sweep`, `stress`, `chain`) writes its metadata beside the report as
`<report>.run-metadata.json` (`results/bench.md` gets `results/bench.run-metadata.json`), so reports that share a folder
keep their own metadata. A run that writes a run FOLDER writes `run-metadata.json` inside it.

## Case arguments

Verbs that take a `<case>` resolve it through one loader: the built-in names `case9`/`ieee9`, `case14`, `case30`,
`case57`, `case118`, `case300`, `case145`, `case24`/`rts`, `cigre`; `uk`/`gb`/`gb-reduced` (`data/uk`); `gb-full`
(`data/gb-full`); `gb-synthetic`/`synthetic` (`data/gb-synthetic`, with its asset and protection bundle, exactly as
`--dir data/gb-synthetic`, for every verb; N316, October 2026: the name used to build the network in memory, a
different branch order, an unbalanced dispatch and no bundle, for every verb but `trip`); the published GB reference networks under
`data/matpower/gb` (provenance and licences in its `README.md`): `gb-reduced-29` (Strathclyde GBreducednetwork.m, 29
buses), `gb-network-2224` (Edinburgh GBnetwork.m, 2,224 buses), `pypsa-gb-reduced` (PyPSA-GB's own 29-bus network from
its CSV files, with GBreducednetwork.m's demand and generation; its import notes are printed on stderr) and
`pypsa-gb-zonal` (a transport model: every solving verb refuses it with exit 1, see [zonal](#zonal)); a JSON case
folder (any folder with a `system.json`); a MATPOWER `.m` file; or a JSON v2 case manifest (`.json`).

A `.m` argument is taken as a path first, then as a path under `data/matpower/` (for example
`pegase/case1354pegase.m`), then, for a bare file name such as `case33bw.m`, searched for through the `data/matpower/`
subfolders in sorted order. A bare name that matches two files exits 2 and lists them.

**JSON v2 cases.** A `<case>` argument ending in `.json` is a JSON v2 case manifest (`"schema": "gridsim-case/2"`,
[technical ch. 28](<../technical/0028 The v2 JSON schema.md>)). The manifest, the network files it lists (relative to the
manifest, inside its folder) and the library files it imports (relative to the shipped `data` folder) are validated
exactly as [`validate`](#validate) validates them, then the case is built by the same model builder as a v1 case
folder, so a v2 case and its v1 equivalent give the same model and the same numbers bit for bit. Any problem refuses
the load with every problem listed (exit 1): a schema error (for example a bus without `baseKv`, required in v2), a
template reference that does not resolve, a placeholder template bound to an element, an override of an unknown
parameter, and v2 content the model does not carry (generator `subtransientXdPu` / `negativeSeqXPu`, line and
transformer `type`, transformer zero-sequence data, an out-of-service line or transformer, a grid-code or solver
profile). Notes, such as the revision an unpinned import resolved to, are printed on stderr as `note: <file>: ...`.

**How each verb takes its case.**

| Form | Verbs |
|---|---|
| Positional `<case>` or `--dir <folder>`. Both together exit 2 unless they name the same folder (`uk --dir data/uk`). A `--dir` folder that does not exist exits 1 with `error: <verb>: case folder not found: '<folder>' (full path ...)` before any report file is opened; a `--dir` with no value exits 2. At most one positional case (a second exits 2). | `trip`, `screen`, `nminus1`, `nminus2`, `n-1-1`, `domains`, `export`, `nminus1-sweep`, `stress`, `chain`, `cycle`, `emt`, `harmonics`, `impedance-stability`, `stability`, `modal` |
| A positional case only; `--dir` exits 2 as a flag of another verb. | `pf`, `ptdf`, `lodf`, `dcpf`, `dcopf`, `acopf`, `cpf`, `taps`, `restore`, `losses`, `physics-check`, `protection-check`, `fault`, `iec60909`, `experimental`, `estimate`, `crossval`, `serve`, `convert` (first argument) |
| A named option instead of a positional. | `gda-replay` (`--case`), `h0-study` (`--dir`, default `data/gb-synthetic`), `transient` (a positional `.m` resolved as every `.m` argument is, or `--cases`), `dist3ph` (a feeder file, a radial `.m` or a case folder) |

**An unknown case name: one rule on every verb.** A case argument that names no case at all (not a built-in name, not
an existing folder, and not a `.m` or `.json` path) is a usage error: exit 2, naming the case and listing the built-in
names. This holds on every verb that takes a case (`chain`, `emt`, `physics-check`, `protection-check`, `fault`,
`iec60909`, `ptdf`, `lodf`, `dcpf`, `pf`, `dcopf`, `acopf`, `cpf`, `taps`, `restore`, `experimental`, `estimate`,
`crossval`, `serve`, `gda-replay`, `convert` and the rest), and on the no-verb run (`error: unknown command or case`).
`trip` reads an unmatched word as a generator name and exits 2 listing the generators; `export` reads it as an output
path and exits 2. In a batch run over several cases (`nminus1-sweep --all`, `stress --all`, `transient --cases`,
`bench`), a case that does not load is a `LOAD FAIL` row and the run exits 1 at the end; one named case that is unknown
(`stress nosuchcase`, `transient nosuchcase`) exits 2 before the run starts. The verbs whose argument is a file or folder
rather than a case name (`dist3ph`, `zonal`, `validate`, `pmu-ingest`) report one that is missing as an input error
(exit 1).

A case file or folder that is named but missing or cannot be loaded (a `.m` path that does not exist, a folder with no
`buses.json`, a malformed `.m`) exits 1 on every verb, `stability` and `modal` included. An existing folder that is not
a built-in name is always read as a case folder, even with no `system.json`: `gridsim pf docs` exits 1 naming the file
the folder lacks (D-DOC-151; the network analysis verbs, `dcopf`, `ptdf` / `lodf`, `experimental`, `physics-check`,
`protection-check`, `fault` and `crossval` used to answer `unknown case 'docs'`, exit 2).

```
$ gridsim screen nosuchcase
error: screen: unknown case 'nosuchcase' (built-in names: case118, case14, case145, case24, case30, case300, case57, case9, cigre, gb, gb-full, gb-network-2224, gb-reduced, gb-reduced-29, gb-synthetic, ieee9, pypsa-gb-reduced, pypsa-gb-zonal, rts, synthetic, uk; or a case folder, or a MATPOWER .m file, or a JSON v2 case manifest .json)
[exit 2]
```

**Licence limit.** A user network above the tier's bus limit is refused with exit 2 when it is loaded
([licence](#licence)). Built-in cases and the bundled networks are never counted.

## Solver selection

Where a verb picks the Newton-Raphson engine itself it uses the shared rule in `SolverSelector`
(`src/GridSim.Core/PowerFlow/SolverSelector.cs`, reached through `SolverBackend.Solver`, `NewtonSolver` and `Pick`).
On the CPU every Newton power flow runs on the sparse engine (`SolverSelector.DefaultSparseThreshold` is 0). Sites
that route through `SolverBackend.Solver` send a strictly radial network to the backward/forward sweep first. On the
CPU the dense and sparse engines are bit-identical, so the rule changes speed and memory, not numbers. The dense engine
stays available by explicit choice: `gda --solve --dense`, the `bench` verbs, and the web and desktop solver pickers.

The exceptions:

- On a GPU backend (`--gpu`) the routing uses the crossover `SolverSelector.DenseSparseCrossoverBuses` (200 buses):
  dense up to it, sparse above, because the dense engine there is the device's dense LU, which is outside the
  bit-identity contract.
- A solve export (`--enableExport`, and the solve `crossval` exports) keeps that crossover on every backend, because
  only the dense engine records the Jacobian in the trace.
- The live per-tick loop in `LiveSim` (`transient`, the `cycle` base solve) switches to sparse above 1,500 buses; there
  the engine also picks the solve options.
- `bench --estimate` forces each estimator backend explicitly, and the state estimator keeps its own 200-bus dense /
  sparse switch.
- `gda --solve --dense` or `--sparse` forces the engine.

**Contingency options.** `nminus1`, `nminus2`, `n-1-1`, `nminus1-sweep`, `stress`, `chain` and `cpf` all solve with one
option set (`ContingencyAnalysis.StudyOptions`): 100 iterations, generator reactive limits enforced, tolerance 1e-8
pu, and the on-load tap changer loop around the engine, so each post-outage or scaled state is the tap-regulated steady
state that the default case solve reports. NETS SQSS v2.10 section 6.2 judges post-fault voltages after the Grid Supply
Point tap changers have acted. On a case with no tap changers the loop is skipped and the numbers are the engine's own,
bit for bit.

## Global switches

Parsed before the verb and usable with any command. `--cpu` and `--gpu` may also come before the verb
(`gridsim --cpu dcopf case9 --prices`): they are taken off the argument list before any verb-specific flag check, so a
verb's own flags are checked against the verb (before October 2026 that run was refused, because the `--prices` check
read `--cpu` as the verb).

| Flag | Default | Meaning |
|---|---|---|
| `--cpu` | on | CPU linear-solver backends: dense LU, and a direct sparse LU (`CpuSparseLuSolver`, reverse Cuthill-McKee ordering, partial pivoting) for the sparse engine. ILU0-BiCGSTAB runs only if the LU reports the step singular. |
| `--gpu` | off | Auto-select a GPU backend, AMD-first: ROCm, then OpenCL, then CUDA, else fall back to CPU with a warning. A GPU sparse solve that gives up is retried on the exact CPU LU before the ILU0-BiCGSTAB fallback. GPU arithmetic is outside the bit-identity contract. |
| `--gpu=rocm` / `--gpu=opencl` / `--gpu=cuda` | off | Force a vendor; an absent device (or an OpenCL device without FP64) exits 2 rather than falling back to the CPU (`bench` and `gpucheck` report it instead). `--gpu=auto` and `--gpu=cpu` are also accepted; any other vendor name exits 2. |
| `--inertia-model lumped\|distributed` | `lumped` | Frequency-dynamics inertia model (`lumped-scalar` and `scalar` are aliases of `lumped`; any other value exits 2). Only `trip` integrates the distributed model; `transient` reports the domain split but integrates lumped; `evidence`, `scenario`, `h0-study`, `fleet` and `replay-event` warn and run lumped. |
| `--asset-dynamics[=on\|off]` | off | Opt into the authored per-asset dynamics fleet (inverter FFR, Loss-of-Mains trips, placed protection devices). A bare flag means `on`. Honoured by `trip` and `transient`. A case with no asset bundle (no `asset-classes.json` / `asset-bindings.json` and no `protections.json`) has nothing to enable: exit 1. `trip` refuses it together with `--inertia-model distributed` (exit 2). |
| `--forming none\|vsm\|droop` | `none` | Grid-forming frequency formation. Honoured by `trip`, `transient`, `scenario` and `fleet`. `vsm` requires `--forming-inertia` above 0 (exit 2 otherwise). With `--inertia-model distributed` it warns and runs the lumped forming integrator. |
| `--forming-inertia <GVA.s>` | 0 | Virtual former inertia Ev. `vsm` only: with `--forming droop` it is refused (exit 2), a droop former carrying no virtual inertia (N311). |
| `--forming-damping <MW/Hz>` | 0 | Virtual former damping Dv. |
| `--frequency-hz <Hz>` | 50 (assumed) | The nominal frequency of every MATPOWER `.m` file the run loads (a path, a bundled `.m` name, or a built-in name backed by a file such as `gb-reduced-29`). A `.m` file carries no system frequency, so without the flag it is assumed to be 50 Hz and a parse note says so; the IEEE, WSCC and most MATPOWER library cases are 60 Hz systems (`gridsim modal data/matpower/lib/case9.m --frequency-hz 60`). It sets the f0 of every dynamic study of that model: the small-signal state matrix (`modal`), the swing equation of `stability` (single study and CCT), `trip`'s RoCoF, EMT branch inductances (`emt`), impedance scans and IEC 60909's frequency terms. It changes no power-flow, OPF or state-estimation number: the frequency enters none of those equations. Like `--cpu` it may stand anywhere on the line. The value must be a finite number above 0 Hz, given once (otherwise exit 2). For a PSS/E `.raw` file it is the base frequency when the file's BASFRQ is 0 or missing (otherwise 60 Hz, PSS/E's default, is assumed and noted); a file that states a different BASFRQ is refused (exit 1). It does not apply to a JSON case folder (its `system.json` carries `frequencyHz`) or to a built-in case name (each carries its own frequency); when the run loads no `.m` file at all, `warning: --frequency-hz <Hz> had no effect ...` is printed on stderr at the end and the exit code is unchanged. (N187, October 2026.) |
| `--psse-revision <n>` | from the file | The revision a PSS/E `.raw` file is read as when its first line does not say (no REV field and no `PSS/E-NN` comment, as in the UKGDS cases: `gridsim ehv2.raw --psse-revision 30 --frequency-hz 50`). One of 30, 33, 34, 35, given once (otherwise exit 2). A file that states a different revision is refused (exit 1). When the run loads no `.raw` file, `warning: --psse-revision had no effect: this run loaded no PSS/E .raw file.` is printed on stderr at the end. See [ch. 2](<0002 Loading networks.md#pss-e-raw-files>). |
| `--seq-data <file.seq>` | none | A PSS/E sequence data file (revision 30 layout) applied to every `.raw` case the run loads, so `fault` and `iec60909` use its machine reactances (X''d, X2), line and transformer zero-sequence impedances and transformer winding connections. The name must end in `.seq` and be given once (otherwise exit 2). Its notes are printed on stderr as `note: <file>.seq: ...`, ending with the counts applied. Machine zero-sequence impedances and mutual coupling between whole lines are applied. Sequence data GridSim's networks have no element for (sequence shunt loads, mutual coupling over part of a line, zero-sequence switched shunts) refuses the load (exit 1) unless `--allow-unmodelled` is given. When the run loads no `.raw` file, `warning: --seq-data had no effect: this run loaded no PSS/E .raw file.` is printed on stderr at the end. See [ch. 2](<0002 Loading networks.md#sequence-data-seq>). |
| `--tap-control` | off | Apply a PSS/E `.raw` file's automatic voltage control by tap (COD 1): each controlled winding becomes an on-load tap changer (band VMI..VMA, limits RMI..RMA, step (RMA - RMI) / (NTP - 1)) that the solve steps from the file's ratios, each winding regulating its own reactive power flow (COD 2, by tap) or active power flow (COD 3, by phase shift angle) is stepped the same way until the flow into it is in VMI..VMA, and a case that does not converge from the file's taps is solved from a ramp start (10 %, 20 %, ... of its demand and generation first; the report says so on a `Ramp start:` line). Without it the taps stay at the file's ratios. Given once (otherwise exit 2). When the run loads no `.raw` file, `warning: --tap-control had no effect: this run loaded no PSS/E .raw file.` is printed on stderr at the end. See [ch. 2](<0002 Loading networks.md#tap-control>). |
| `--shunt-control` | off | Switch a PSS/E `.raw` file's switched shunts that have discrete local voltage control (MODSW 1, one block size, BINIT a whole number of blocks) in whole blocks to hold their bus in VSWLO..VSWHI: in the case solve (inside the tap loop; the report adds a `Switched shunts:` line) and in `pf` (as a `--switched-shunts` file's banks; giving both exits 2). Those with continuous local voltage control (MODSW 2) become static var compensators solved with the power flow (the report prints a `Static var compensator` line for each). Every other switched shunt stays fixed at BINIT, noted. Given once (otherwise exit 2). When the run loads no `.raw` file, `warning: --shunt-control had no effect: this run loaded no PSS/E .raw file.` is printed on stderr at the end. See [ch. 2](<0002 Loading networks.md#shunt-control>). |
| `--allow-unmodelled` | off | Read a PSS/E `.raw` file that holds in-service devices GridSim does not model (capacitor-commutated two-terminal DC links, VSC converters regulating a remote bus, multi-terminal DC converters on two DC buses, FACTS devices in modes 4 to 8 or regulating a remote bus, or induction machines with a standard design code, a double cage or saturation). Without it such a file is refused (exit 1), the message naming each kind and the line of its first record. With it those devices are read past, each kind counted in a `note:` line on stderr, and the power flow differs from the file's. GNE devices are refused either way. With `--seq-data` it also lets sequence data GridSim's fault networks do not model be read past, each kind counted in a note; for a UCTE-DEF `.uct` file it reads transformers that have special tables (##TT), using their own impedances. Like `--cpu` it may stand anywhere on the line; given twice it exits 2. When the run loads no `.raw` file, `warning: --allow-unmodelled had no effect: this run loaded no PSS/E .raw file.` is printed on stderr at the end and the exit code is unchanged. See [ch. 2](<0002 Loading networks.md#pss-e-raw-files>). |
| `--bfs` | off | Bare/case/dir forms: route the solve through the radial backward/forward-sweep solver (auto-selected for radial models anyway). The sweep declines a meshed or disconnected network (nothing is solved), so `--bfs` on a meshed case does not converge: exit 3 with `--once`, `--enableExport` or `--ticks`. |
| `--enableExport[=path]` | off | Bare/case/dir forms: dump the full math trace of one solve to JSON (`results/exports/<case>-solve.json` by default, where `<case>` is the built-in name, or for a path the file or folder name without `.m`: `data/matpower/lib/case14.m` writes `case14-solve.json`; D-DOC-154, it used to be `data_matpower_lib_case14.m-solve.json`; the export's `meta` still records the argument as given). The path is given only in the `=` form (`--enableExport=out.json`); on the bare run the space form (`--enableExport out.json`) exits 2 saying so, instead of writing the default file (GR-688). A solve that does not converge still writes the export, says so on stderr and exits 3; when the sweep declined, the solver is named `BackwardForwardSweep (declined: ...)`. `harmonics` and `impedance-stability` use it to write their own exports. |
| `--export-jacobian` | off | Force the (large) Jacobian past the size gate into the export. |
| `--export-tight` | off | With `--enableExport` on a case (N251, October 2026): export GridSim's tightest converged state instead of the state at the everyday 1e-8 pu stopping test, which carries that convergence residual (up to about 1e-9 pu in voltage magnitude) and so can never meet a machine-epsilon comparison on its own. After the ordinary solve converges, the case is solved afresh with the same engine and options at 1e-9, 1e-10, ... 1e-15 pu and the tightest rung that converges is exported (the `crossval` verb's own rule, N243); the export's `solve.tightenedTolerancePu` names it, `meta.elapsedMs` is that rung's solve time, and a line `[export] tightened: solve tolerance ... pu (... final mismatch ... pu)` is printed. When no rung below 1e-8 converges the ordinary state is exported, without the field, and a line says so. Without the switch nothing changes (same solve, same bytes). Without `--enableExport`, or with `harmonics` or `impedance-stability` (their exports are not solve exports), it exits 2. |
| `--audit-log <file>` | off | Record the run in a tamper-evident audit log (`gridsim-audit/1`, [technical ch. 31](<../technical/0031 Audit log and diagnostics.md>)). Accepted with `estimate`, `crossval`, `interpose` and `serve` only; with any other verb, or given twice, it exits 2. The log is opened and verified before the verb does any work, against its anchor file (below): a tampered, truncated, restored or locked log, a damaged anchor file, or a missing directory, exits 1 and nothing runs. One entry is written when the run ends, however it ends once the log is open (including a refused flag or a failure; the few global checks that run before the log is opened, the unknown-flag gate, `--importLicence` and `--frequency-hz`, end a refused run with no entry), holding who ran it, the verb, each input's SHA-256 (a bundled case name is recorded as `case-id`, the hash of the name; a folder such as `serve`'s `--measurements-dir` by a hash of every file's name and bytes), `exit=<code>` and the verb's one-line result; `interpose` also writes a `started` entry when it begins listening. After each entry the anchor (entry count and head hash) is written to the anchor file, one line `gridsim-audit-anchor/1 count=N head=H`, and printed to stderr as `audit: <file> entries=N head=H`. The next run opens the log against that anchor, so a log that lost entries since (tail truncation), was replaced by an older copy, or was rewritten with recomputed hashes is refused. A log that has entries but no anchor file is accepted with a warning on stderr, and that run's entry ends `audit-anchor=missing`; the anchor is written from then on. Keep the printed anchor elsewhere too and pass it to `gridsim audit verify`. |
| `--audit-anchor <file>` | `<log>.anchor` | The anchor file of `--audit-log` (needs `--audit-log`; alone, or given twice, it exits 2). The default sits beside the log, which catches accidental truncation and a restored copy; keep it somewhere the log's writer cannot change (another account's share, write-once media) to catch a deliberate edit of both. Its folder must exist (exit 1 otherwise). |
| `--audit-forward <file>` | off | Also append every audit entry, as one JSON line, to `<file>` (a SIEM collector's input or a write-once copy). Needs `--audit-log`. A failed forward never stops the run; it is reported at the end and the run exits 1. |
| `--incident-log <file>` | off | Raise security events as incidents (one per significant event, one per burst of routine ones) and append them to `<file>` as JSON lines, each naming the audit entry that evidences it. Needs `--audit-log`. A failed write is reported at the end and the run exits 1. |
| `--audit-sign-key <private.pem>` | off | ECDSA P-256 private key (PEM): sign the anchor after every entry, into `<log>.anchor.sig`. Needs `--audit-log`; a key that is not a P-256 private key exits 1. |
| `--audit-verify-key <public.pem>` | off | ECDSA P-256 public key (PEM): before the run starts, the anchor's signature must exist and verify, or exit 1. Needs `--audit-log`. |

The audited verbs (`estimate`, `crossval`, `interpose`, `serve`) also write one `action=security` entry per security
event at the point of detection, besides the run's own entry: an input refused by the horizon guard, an infeasible
estimate, a suspected false-data injection, an output-guard refusal, a quarantined live window and a breached
service-level objective. The `audit:` line at the end counts them (`security-events=N`).
| `--once` | off | Bare/case/dir forms: solve once, print, exit. |
| `--ticks N` | interactive | Bare/case/dir forms: run N headless live ticks then exit. N is at least 1: leave the flag out for the interactive loop; `--ticks 0` or a negative count exits 2. Every tick is run and printed; when any tick did not converge (`DIV`) the run ends with `error: <n> of <N> tick(s) did not converge (DIV)` and exit 3. `interpose` uses it as a frame cap. |
| `--importLicence <file.gridsim>` | - | Install a software licence from a `.gridsim` file, then exit (see the `licence` verb and [ch. 4](<0004 Licensing and activation.md>)). Fully offline. The value must be a file path: an empty value (`--importLicence=`), a following flag (`--importLicence --once`) or no value at all exits 2 with `error: --importLicence expected a licence file path (<file.gridsim>) but got '...'`. Distinct from the DNO `--licence` distribution-area selector of `gda`. |
| `--version` | - | Print `gridsim <version>+<commit>` (the release number and the full git commit the binary was built from) and exit 0. Answered before anything else, so it needs no licence and works alongside any other token. |

An unexpected internal error (a defect, not bad input) prints `gridsim: internal error ...` and the path of a crash
log on stderr, then the process ends with the runtime's crash exit code (not 0, 1, 2 or 3). The crash log goes to
`%LOCALAPPDATA%\GridSim\crash` (Linux `~/.local/share/GridSim/crash`, macOS `~/Library/Application Support/GridSim/crash`),
or to the folder named by the `GRIDSIM_CRASH_DIR` environment variable when it is set. It holds the build, runtime, OS,
arguments and the full exception; attach it to a bug report.

## help

`gridsim help [<verb> [<mode>]]`, `gridsim <verb> --help`, `gridsim <verb> -h`

With a verb, prints that verb's section of this chapter as plain text: its usage line, every flag with its default, its
exit codes and the notes its group shares, then the general exit-code rule, and exits 0. Because the chapter is compiled
into the binary, the help is this text. Aliases work (`gridsim n-1 --help` prints the `nminus1` section), and a verb
with modes takes the mode (`gridsim bench --estimate --help`). `gridsim help` with no verb is `gridsim --help` (the
verb list). `gridsim help <word>` for a word that is not a verb exits 2; `gridsim <word> --help` for a word that is
not a verb prints the verb list. Help needs no licence and is answered before the unknown-flag check.

## licence

Inspect and install the software licence ([ch. 4](<0004 Licensing and activation.md>)). Fully offline.

| Form | Does |
|---|---|
| `licence` / `licence status` | Print the active tier, holder, bus limit, expiry, and the licence-file path. |
| `licence import <file.gridsim>` | Validate a `.gridsim` key file and install it to `%LOCALAPPDATA%\GridSim\licence.gridsim`. |
| `licence path` | Print the licence-file path. |

The global `--importLicence <file.gridsim>` switch is shorthand for `licence import`. A key can also be supplied for a
single run through the `GRIDSIM_LICENCE` environment variable. With no licence installed, GridSim runs at the free
Personal tier: 1,500 buses on user networks. The built-in cases and every network loaded from the bundled folders
`data/matpower`, `data/uk`, `data/gb-synthetic`, `data/gb-full`, `data/intl` and `data/dist3ph` are
Bundled Networks and are never counted (`BundledNetworks`, `src/GridSim.Core/Licensing/BundledNetworks.cs`). Bundled
means the shipped files themselves, by content (N229, October 2026): the folder must be the program's own `data/`
folder (N216), and every file loaded must match the shipped-content manifest `data/bundled.sha256` (SHA-256 of each
shipped file). A file added to a bundled folder, or a shipped file edited, is a user network and is counted. A user
network over the limit is refused with exit 2. A `GRIDSIM_LICENCE` or installed key that fails verification leaves the
run at the Personal tier, and `licence status` shows the reason. A key is bound to the major version it was issued for:
`licence status` shows it (`for GridSim 1.x`), and a key for another major version is rejected like any other, naming
both versions. `licence import` exits 2 on a rejected key; an unknown subcommand or bad usage also exits 2. The licence
verb is matched in lower case only.

A key applies from its issue date to its expiry date, both inclusive (UTC). Outside that window the free Personal tier
applies, exactly as with no key: a key dated in the future shows `NOT YET VALID (starts <date>)` and applies from that
day (it used to apply at once), and an expired key shows `EXPIRED <date>`. In both cases, and for a key of another major
version, the status line shows the limit that applies, `1,500 buses (the free tier applies)`, not the key's own limit
(an expired unlimited key used to show `unlimited buses`).

## Running a case (no verb)

| Argument | Loads |
|---|---|
| *(none)* | UK reduced 10-zone model (`data/uk`), live loop. |
| `case9`/`ieee9`, `case14`, `case30`, `case57`, `case118`, `case300`, `case145`, `case24`/`rts`, `cigre` | Embedded IEEE and CIGRE cases, each read from its MATPOWER file (`case9` adds the published WSCC machine data, MBase and H, that no `.m` file carries). `case9` without `--enableExport` prints a detailed solve and exits. |
| `uk` | The UK reduced model. |
| `<file>.m` or `--mpc <file>.m` | Any MATPOWER case (raw ohm/kW distribution files are unit-normalised, with a `note:` line on stderr saying so). |
| `--dir <path>` / `-d <path>` | A JSON case directory. |
| *(anything else)* | `error: unknown command or case`, exit 2. The other built-in names (`gb`, `gb-full`, `gb-synthetic`, ...) are for the verbs, not this form. |

The detailed solve report (`--once`, and `case9`) says what the data does not give rather than printing a zero. A case
whose generators carry no inertia constant (every MATPOWER case: the format has no H column) prints `Inertia not in
the data (no generator H)`, and a bus with no base voltage (MATPOWER `baseKV` 0, as in `case14`) shows `-` in the kV
column, with a line under the table counting such buses. When the model has on-load tap changers and their loop did
not finish, a line under the convergence line says so: `Taps NOT SETTLED: N tap changer(s) at the end of their range
with the controlled bus still outside its deadband: <names>`, or that the loop stopped at its pass cap with taps still
moving. That line is a report, not a failure: the power flow decides the exit code (3 when it did not converge). The
live dashboard shows the same line, and the WPF and web shells mark the OLTC fleet `NOT SETTLED`. Without `--once` or
`--ticks` the run is the interactive live loop, which stops on a key press. See
[ch. 5](<0005 Running power flows.md>).

## Contingency and analysis verbs

These verbs are used in [ch. 11, Contingency analysis](<0011 Contingency analysis.md>).

### trip

`gridsim trip [<case>] [<gen>] [--dir <path>] [--export <csv>] [--gb-services[=<scale>]] [--band[=<fraction>]] [--envelope <class>:<param>=<low>..<high>[,...]] [--dyn-data <file>]`

Trip a generator and report the peak RoCoF (`Peak RoCoF`, the largest |df/dt| over the run), the nadir and the settling
frequency, plus the same trip at half inertia. `<gen>` is a case-insensitive substring of a generator name; no match
lists the available names and exits 2. With two free tokens the first is the case and the second the generator. A
single free token is the case when it names one (built-in name, `.m` file, case folder) and the generator name
otherwise; it is found wherever it sits, so `trip --dir data/uk Torness` trips Torness. More than two free tokens exit
2. Without `<gen>` the largest non-slack unit with inertia is tripped (exit 2 if there is none). `trip` is the only
verb that integrates `--inertia-model distributed`; it also honours `--asset-dynamics` and `--forming`. The
half-inertia comparison runs the lumped model. The run is integrated at the case's own nominal frequency (D2, October
2026): RoCoF = f0 * deltaP / (2E), so the 60 Hz `case9` reports 1.2 times the RoCoF a 50 Hz run of the same loss and
inertia would, and its trajectory starts at 60 Hz (before D2 every case ran at 50 Hz).

The system inertia on the `System inertia:` line is the rotating mass only (N314, October 2026; the N289 rule the
lumped total, the distributed model and the asset fleet already use): a unit whose type is a known converter technology
(wind, solar, storage, interconnector, ...) adds nothing whatever H its data carries, and the tripped unit takes out only
what it put in. It used to sum H x MBase over every unit. No bundled case has a converter-typed unit with H above 0, so
every bundled trip prints the same numbers.

With `--inertia-model distributed` the loss is placed at the tripped unit's bus (R060, October 2026): it lands on the
speed domain whose machines remain at that bus, or, when the tripped unit was the only machine there, it is shared among
the domains by their synchronising coupling to that bus (the distribution of a power impact). A `Loss placed:` line
under `Inertia model:` states the rule that placed it. Before R060 every loss landed on the domain with the largest total
MVA, whichever unit tripped; the per-domain nadirs and RoCoFs depend on the placement, the centre-of-inertia figures do
not. When the inter-domain coupling cannot be derived from the network (no branch reactance, a failed reduction, or
negligible coupling between the domains) the run uses the uniform 0.5 Hz target-mode stiffness and a `Coupling:` line
says so and why (R025; this used to be silent). Buses that no machine is connected to, such as one isolated by a
topology edit, are left out of the reduction instead of making it fail.

`--asset-dynamics` needs a case with an asset bundle: on one without (`data/uk`, the IEEE cases) it exits 1 instead of being ignored. The name `gb-synthetic`
is the shipped `data/gb-synthetic` folder, with its asset bundle (N238 for `trip`, N316 for every verb, October 2026; it
used to build the bundle-less network in memory). It cannot be combined with `--inertia-model distributed`
(exit 2): the multi-domain model carries no asset fleet yet, so one of the two would be dropped. With asset dynamics on,
the `Asset dynamics: on` line splits the infeed lost into inverter trips (Loss-of-Mains RoCoF or vector shift, or
under-voltage ride-through) and protection-device operations; a `note:` line counts the assets whose voltage or loading
stages this lumped run does not evaluate (it solves no network: they are neither operated nor held).

After the run, `trip` assesses the system frequency protection on the run's trajectory (`ProtectionAssessment.
AssessTrajectory`, D-DOC-220): `81U` against the run minimum, `81O` against the run maximum and `81R` against the peak
|df/dt|, with the `protection-check` defaults (G99 Issue 2 Table 10.1: 47.5 Hz, 52.0 Hz, 1.0 Hz/s). Each line says
`holds`, `WOULD OPERATE` or `not evaluated`; `78` (vector shift) is not evaluated, because the run is a
centre-of-inertia frequency trajectory with no bus voltage angle. On a case whose nominal frequency is not 50 Hz
(`case9`, 60 Hz) `81U` and `81O` are `not evaluated` too: the G99 pickups are settings for a 50 Hz system and do not
apply (D2). The lines are informational: the exit code does not change.

| Flag | Default | Meaning |
|---|---|---|
| `<case>` | `data/uk` | Case to load ([Case arguments](#case-arguments)). |
| `<gen>` | largest unit | Generator-name substring to trip. |
| `--dir` | `data/uk` | JSON case directory. |
| `--export` | - | Write the frequency trajectory CSV (`dtm,f`), playable with `gridsim replay <file>`. A missing folder is created ([Output files](#output-files)). |
| `--gb-services[=<scale>]` | off | Add the GB Dynamic Containment / Moderation / Regulation arrest layer at the EAC auction volumes (950 / 520 / 490 MW, `GbResponseServices.Typical`), times `<scale>` (default 1; a finite number above 0, else exit 2), on top of the generic governor. A `Response:` line states the volumes. `--gb-services 0.5` also works. |
| `--band[=<fraction>]` | off | Add the nadir's uncertainty band over the swing constants that are literature-typical and not fitted to GB data (load damping, responsive fraction, governor time constant; `FrequencySensitivitySweep`, R079): each is moved by +/- `<fraction>` (default 0.20; strictly between 0 and 1, else exit 2) one at a time, most leverage first, and over every corner of the joint box. The band follows the half-inertia line; every line before it is unchanged. Lumped model only: with `--asset-dynamics` or `--inertia-model distributed` it exits 2. `--band 0.3` also works. |

With `--band` the output gains a block such as:

```
Uncertainty band (--band 0.2): the unfitted swing constants each +/- 20 % (literature-typical, not fitted to GB data):
  nadir over every corner:  <min> .. <max> Hz (width <w> Hz); peak RoCoF band <r> Hz/s (inertia and the loss set the RoCoF, not these constants)
  one at a time, most leverage first:
    load damping (%/Hz)              0.8 -> 1.2       nadir <a> -> <b> Hz  (swing <s> Hz)
    ...
```

The sweep's nominal run is the trip run itself (checked bit for bit). The constants can be fitted to a measured trace
with [`fit-frequency`](#fit-frequency).

`--envelope <class>:<param>=<low>..<high>[,...]` (with `--asset-dynamics`, R095) sweeps control parameters of the
case's asset classes (`asset-classes.json`) over their plausible ranges around this trip (`AssetEnvelope.Sweep`): every
corner of the ranges (2^k runs for k parameters, at most 12), then a tornado (each parameter low and high with the others
at their midpoints, most leverage first). It prints the nadir band (minimum, median, maximum), the peak RoCoF band,
whether a cascade is possible (some corners trip infeed and others do not: the data cannot say which, and that is the
finding) and the tornado, after the half-inertia line; every line before it is unchanged. The result is tagged
`UnvalidatedAtScale`. Example: `trip --dir data/gb-synthetic --asset-dynamics --envelope dg-alomcp:rocofTripHzPerS=0.5..2`.
The class must be in the case's library and the parameter one its kind is simulated with (the accepted keys of the
asset-class loader); low below high, both finite; no item twice. Without `--asset-dynamics` it exits 2; on a case whose
bundle holds protection devices only it exits 1; a bad item, or a swept value the asset-class checks refuse, exits 2
naming it.

| Flag | Default | Meaning |
|---|---|---|
| `--envelope` | off | With `--asset-dynamics`: the asset-class parameters to sweep, `<class>:<param>=<low>..<high>`, comma-separated. |
| `--dyn-data` | - | A machine dynamic data file (`gridsim-dynamics/2`, the file [`stability --rms`](#stability) reads). The trip integrates the system frequency swing, whose only per-machine number is the inertia H, so each generator the file binds takes the file's H (the case's own H where the template gives none); every engine of the run (lumped, `--inertia-model distributed`, `--asset-dynamics`) and the choice of the unit to trip see that inertia. A `Machine data:` line under the title names the file, counts the generators and how many H values differ from the case, and says that the file's GENROU reactances, exciters and governors are not used by this swing. A file whose H values equal the case's gives the same trip, bit for bit. A missing file or one that fails the v2 schema, kind-spec or binding checks exits 1 with every problem listed, before any run. |

### screen

`gridsim screen [<case>] [--dir <path>] [--top 12] [--csv <path>]`

N-1 RoCoF screening: rank every credible single infeed loss against the GB limits (RoCoF 0.125 Hz/s, statutory
49.5 Hz, LFDD risk) and report the binding case's inertia floor and headroom.

Each loss is integrated at the case's own nominal frequency (N275, October 2026), as `trip` is: RoCoF =
f0 * deltaP / (2E), so the 60 Hz `case9` screens at 1.2 times the RoCoF a 50 Hz run of the same loss and inertia gives
(before N275 the screen ran every case at 50 Hz). The nadir is graded against the GB bands about the case's nominal
(N297, October 2026: the GB edges as hertz offsets, so 59.5 Hz statutory and 58.8 Hz LFDD for `case9`; before, a
non-50 Hz case was graded against 49.5 / 48.8 Hz, which its nadirs never reach). On such a case the header's statutory
figure and the `<... Hz` verdict show the moved edge, and a `note:` line under the header names both edges. A 50 Hz
case prints exactly as before.

| Flag | Default | Meaning |
|---|---|---|
| `<case>` / `--dir` | `data/uk` | Case to load, or a JSON case directory ([Case arguments](#case-arguments)). |
| `--top` | 12 | Rows to print (all losses are still screened and counted). |
| `--csv` | - | Write the full screening table to CSV: `name,lost_mw,remaining_inertia_gva_s,peak_rocof_hz_per_s,nadir_hz,rocof_breach,statutory_breach,lfdd_risk`. The RoCoF column is the peak over the run. |

The last line is the verdict. Exit 3 (`Verdict: NOT SECURE, N of M single loss(es) breach a limit`) when any loss is
`LFDD RISK`, `<49.5 Hz` or `>0.125 Hz/s`; exit 0 (`Verdict: SECURE`) when every loss is `ok`. The CSV is written first
either way.

A case with no inertia data (every in-service generator has `H` = 0, as in the IEEE MATPOWER cases) is refused before
anything is printed or written: `error: screen: case '<name>' carries no inertia data ...`, exit 1. The screen floors
the remaining inertia at 1 MWs to avoid a division by zero, so such a case used to report every loss at an absurd RoCoF
(case14: -5810 Hz/s). The system inertia is the rotating-mass total, as `trip` uses: a unit whose type is a known
converter technology (wind, solar, storage, interconnector and the like) counts no inertia whatever `H` its data carries,
and its loss removes none (N321, October 2026; it used to count; no bundled case has such a unit with `H` above 0, so no
bundled screen moves). A case whose only `H` sits on converter-type units is refused like one with no `H`. A loss that leaves less than 1 MWs on a case that has inertia is still screened (it breaches every
limit) and named on stderr in a `warning: screen: ...` line, because its RoCoF and nadir are the floor's artefact.

### nminus1

`gridsim nminus1 [<case>] [--dir <path>] [--global-vlimits] [--outage-types branch,gen,bus] [--outage-dispatch reference|droop|inertia] [--post-fault-rating A|B|C] [--batched-lanes N]` (alias `n-1`)

Sequential N-1 branch outage screening on one model: each branch out in turn, re-solved, classified secure /
violations / islanded. Default model `data/uk`; a positional case or `--dir` selects another
([Case arguments](#case-arguments)). An outage that does not solve prints `ISLANDED` when it splits the network (the
post-outage network has more connected parts than the case) and `NO SOLUTION` when the network is still connected but
the power flow found no solution (a voltage collapse, for example `case14` branch `1-2`), with its reason: `islanded`
or `no solution` for a non-converged solve, or `solver error: <type>: <message>` when the solver threw. The count line
says `N islanded`, and `M not solved` when there are any. Both fail the verdict (exit 3). The result file's `islanded`
counts only the splits and `not_solved` counts the rest. `nminus2` and `n-1-1` label rows the same way.

The three branch screens share these rules:

- **Options.** The contingency option set and the tap-changer loop ([Solver selection](#solver-selection)).
- **Base case first.** The intact network is solved with the same options and printed once: `Base case: secure (...)`
  or `Base case: N pre-existing violation(s), reported once here and not counted against any outage: <list>`. A base
  case that does not converge exits 3 with `error: <verb>: the base case of <model> did not converge (...)`, before any
  outage is screened.
- **Netting.** A violation counts against an outage only when its branch, boundary or bus was not already violating in
  the base case. Status `VIOLATIONxN` counts those new violations; `base only` marks a converged outage that adds none
  while the base case's own violations remain in its state (counted as secure, and noted under the footer); `secure`
  means no violation at all.
- **Verdict.** The last line: exit 0 with `Verdict: SECURE (exit 0).` when the base case and every screened outage are
  secure; exit 3 with `Verdict: NOT SECURE, ... (exit 3).` when the base case has a violation or any outage violates,
  islands or does not solve (and, for `nminus2` and `n-1-1`, when a single outage was left out of the pairing because
  it already fails).
- **Voltage limits.** Each bus is judged against its own limits where the case gives them (MATPOWER bus columns 12 and
  13, VMAX and VMIN; JSON `vminPu` and `vmaxPu` in `buses.json`), and against the one operational band (0.94-1.06 pu)
  on any side a bus leaves unset; a case with no per-bus limits is judged against the band throughout.
  `--global-vlimits` judges every bus against the band and ignores the per-bus data. `--bus-vlimits` is accepted and
  does nothing (per-bus limits are on by default); giving it with `--global-vlimits` exits 2. Both flags are accepted
  by `nminus1`, `nminus2`, `n-1-1` and `nminus1-sweep` only; on any other verb they exit 2. Because every MATPOWER file carries VMAX and
  VMIN, the choice changes verdicts on bundled MATPOWER cases. The ranking of outages for `nminus2` and `n-1-1`
  candidates uses the band.
- **Post-fault rating.** By default every state's branch loading is judged against rating A (`RateA`, MATPOWER column
  6, JSON `ratingMva`). `--post-fault-rating B` or `C` judges each outage state against the branch's rating B
  (short-term: MATPOWER RATE_B, JSON `ratingBMva`) or C (emergency: RATE_C, `ratingCMva`), and a branch that has no B or
  C rating (0) against its rating A; the base case stays on rating A. This follows NETS SQSS v2.10 (docs/reference
  S036, 8 April 2025): no loading above the pre-fault rating before a fault (for example paragraph 2.9.1) and no
  "unacceptable overloading" after a secured event (paragraph 5.1.8), which the Definitions put at loading beyond the
  equipment's "specified time-related capability". The meaning of MATPOWER's rating columns (A long term, B short
  term, C emergency) is MATPOWER's caseformat convention [VERIFY: not in docs/reference]. `A` is the default and
  changes nothing. Accepted by `nminus1`, `nminus2` and `n-1-1` only; elsewhere, or with any other value, it exits 2.
  With it the header says which rating is used and the result file adds `post_fault_rating`.

`--batched-lanes N` (N349, `nminus1` only): solve the branch outages with the batched contingency sweep, N lanes posting
their Newton steps to one batched linear solve (0 = one lane per processor, at most 1024). Every number is bit-identical
to the default sequential screen (the CPU batched backend is the same sparse LU on each system); only the speed differs,
and a `note:` line on stderr says the batched sweep ran. Generator and bus outages (`--outage-types`) are screened as
before. A value that is not a whole number from 0 to 1024 exits 2.

`nminus1` alone also takes `--outage-types`: a comma list of `branch`, `gen` and `bus` (default `branch`, the screen
above). `gen` takes each in-service generating unit out in turn (NETS SQSS v2.10 paragraph 5.1.2, a single generating
unit): its scheduled output is lost and the reference bus picks it up in the re-solve. `bus` takes each bus out with
every branch touching it, every unit on it and its load: the bus-branch model's stand-in for a section of busbar
(paragraph 5.1.5). The load and generation an outage took are printed under its row (`lost load ... MW`, `lost
generation ... MW (picked up by the reference)`), reported but not judged (the SQSS loss-of-supply limits of Table 5.1
are not modelled). When an outage takes the last unit of the reference bus, or the reference bus itself, the reference
moves to the bus of the largest remaining unit by |Pg| in the same connected part of the network, and the row says
`reference moved from bus X to bus Y`; with no unit left there the outage is `ISLANDED`. Rows are printed worst first
(branch outages first among equals, then generators, then buses). With a non-default choice the header reads `N-1
screening (<kinds> outages)`, and the result file adds an `outage_details` table
(`outage,kind,lost_load_mw,lost_generation_mw,note`). `--outage-types` on another verb, or with an unknown kind, exits 2.

`--outage-dispatch` (N124, `nminus1` with `gen` or `bus` outages only) chooses who picks up the real power such an
outage takes away before the re-solve. `reference` (the default, also `slack`) is the screen above: the reference bus
takes it all, as PSS/E AC contingency analysis does with its dispatch disabled and PowerFactory with "as dispatched".
`droop` shares the net lost power (lost generation less lost load) among the remaining synchronous units in proportion
to their MBase, the primary-response gain MBase / R with one droop R for every unit (the case carries no per-unit
droop; PSS/E dispatch by governor droop, PowerFactory "according to primary control"); `inertia` shares it in
proportion to H x MBase (PSS/E dispatch by inertia, PowerFactory "according to inertias"). Converter plant,
power-flow-only units and units with no path to a reference bus take no share. Each unit stays within its Pmin and
Pmax: a unit whose share exceeds its headroom is held at its limit and the rest goes to the others; what no unit can
take is left to the reference. The reference then carries only its own share, the change in losses and any remainder.
The detail line reads `lost generation ... MW (shared by governor droop)` (or `by inertia`) and the note says how many
units shared it, how many met a limit and any MW left to the reference, for example `lost 60.0 MW shared by governor
droop over 2 unit(s)`. With `--outage-dispatch` on another verb, without `gen` or `bus` in `--outage-types`, or with
another value, it exits 2.

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).

### nminus2

`gridsim nminus2 [<case>] [--dir <path>] [--candidates N] [--exhaustive] [--top N] [--global-vlimits]` (alias `n-2`)

N-2 branch screening: two elements lost at once. A full pairwise sweep is C(n,2), so by default it screens N-1 first,
ranks the single outages that solve by how hard they stress the system, keeps the `--candidates` most-stressed, and
pairs only those (fewer when fewer solve). The single outages that do not solve (islanded or no solution) are left out
of the pairing and listed under the table (`N single outage(s) left out of the pairing because they already fail at
N-1`). Results are printed worst first. Default model `data/uk`. Exit codes as for [nminus1](#nminus1).

| Flag | Default | Meaning |
|---|---|---|
| `<case>` / `--dir` | `data/uk` | Case to load, or a JSON case directory ([Case arguments](#case-arguments)). |
| `--candidates` | 20 | How many of the most-stressed single outages to pair (ignored with `--exhaustive`). |
| `--exhaustive` | off | Enumerate every pair; use only on small cases. |
| `--top` | 20 | Rows to print (all pairs are still screened and counted). |
| `--post-fault-rating` | A | Judge the outage states against rating A, B or C (see [nminus1](#nminus1)). |
| `--global-vlimits` | off | Judge every bus against the operational band 0.94-1.06 pu, ignoring the case's per-bus limits (see [nminus1](#nminus1)). `--bus-vlimits` is accepted as a no-op alias of the default. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).

### n-1-1

`gridsim n-1-1 [<case>] [--dir <path>] [--candidates N] [--exhaustive] [--top N] [--global-vlimits]` (alias `nminus1minus1`)

N-1-1 sequential screening: lose one element, let the operator take corrective action, then lose a second element
from that corrected state. The corrective action acts only when the first outage leaves a rated line or a limited
boundary over its limit in the DC model; it then moves the fewest MW of generation that bring every rated line and
limited boundary back inside its limit, keeping each DC island's total generation (island by island). With nothing
over its limit the dispatch is unchanged, and when no redispatch within the generator limits can relieve it the
dispatch is also unchanged. Pairs are ordered (`first => second`), and results print worst first. The candidate set
and the failing single outages are as for [nminus2](#nminus2). Default model `data/uk`. Exit codes as for
[nminus1](#nminus1).

| Flag | Default | Meaning |
|---|---|---|
| `<case>` / `--dir` | `data/uk` | Case to load, or a JSON case directory ([Case arguments](#case-arguments)). |
| `--candidates` | 20 | How many of the most-stressed single outages to use as first and second outages (ignored with `--exhaustive`). |
| `--exhaustive` | off | Enumerate every ordered pair; small cases only. |
| `--top` | 20 | Rows to print (all pairs are still screened and counted). |
| `--post-fault-rating` | A | Judge the outage states against rating A, B or C (see [nminus1](#nminus1)). |
| `--global-vlimits` | off | Judge every bus against the operational band 0.94-1.06 pu, ignoring the case's per-bus limits (see [nminus1](#nminus1)). `--bus-vlimits` is accepted as a no-op alias of the default. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).

### nminus1-sweep

`gridsim nminus1-sweep <case|--dir <path>|--all> [--parallel N] [--sample N] [--losses] [--global-vlimits] [--out <path>]`

Parallel N-1 branch sweep with throughput reporting, over one case or the whole case registry (`--all` is the embedded
classics plus every `.m` under `data/matpower/`). Writes Markdown, CSV and `<report>.run-metadata.json` beside the
report ([Output files](#output-files)). Each case is screened
with the contingency option set and the tap-changer loop on the shared dense / sparse rule
([Solver selection](#solver-selection)). The base case is solved first; its violation count is the last column, `Base
viol` (CSV `base_violations`, after `commit`), and those violations are netted out of every outage as for
[nminus1](#nminus1). `Islanded` counts the outages that split the network and `Not solved` the connected outages with
no solution (CSV `islanded` and `not_solved`, the last column). A base case that does not converge gives a `BASE FAIL`
row. Bus voltages are judged as [nminus1](#nminus1) judges them: against each bus's own limits where the case gives
them (the default), or against the operational band with `--global-vlimits`, so the sweep and `nminus1` give the same
verdicts on the same case (the header line records `vlimits=per-bus` or `vlimits=global`). Before October 2026 the
sweep always judged the band, so on a case with per-bus limits it disagreed with `nminus1`. Exit 1 if no case loaded or after a partial load failure; otherwise exit 3 when any case is not N-1 secure (a
base-case violation, an outage that violates, islands or does not solve, or a `BASE FAIL`), and 0 when every case is.

| Flag | Default | Meaning |
|---|---|---|
| `<case>` / `--dir` | `case118` | Case name, `.m` file or case folder, or a JSON case directory ([Case arguments](#case-arguments)). |
| `--all` | off | Sweep the whole registry (exit 2 with a case or `--dir`). |
| `--parallel` | 0 (all cores) | Worker threads; a negative value is refused. |
| `--sample` | 0 (full) | Screen an evenly spaced sample of N branches (logged, flagged `(sampled)`). |
| `--losses` | off | Also run the loss-contingency sweep and append the 20 outages that raise network losses most. |
| `--global-vlimits` | off | Judge every bus against the operational band 0.94-1.06 pu, ignoring the case's per-bus limits (see [nminus1](#nminus1)). `--bus-vlimits` is accepted as a no-op alias of the default. |
| `--out` | `results/nminus1-sweep.md` | Report path (CSV alongside). |

### stress

`gridsim stress <case|--dir <path>|--all> [--from F] [--to F] [--step F] [--parallel N] [--out <path>] [--pv-out <file.csv>]`

Load-to-collapse: scale the operating point until the flow stops converging and report max loadability. Each load
point solves with the contingency option set and the tap-changer loop ([Solver selection](#solver-selection));
Q-limits are enforced so collapse is real reactive exhaustion. Writes Markdown, CSV and `<report>.run-metadata.json`
beside the report ([Output files](#output-files)). Exit 1 if
no case loaded or after a partial load failure; exit 2 for a bad `--step` or `--from` above `--to`; exit 3 when any case
has no converged load point at all (its row reads `<none>` and `NO LOAD POINT CONVERGED`), unless exit 1 already applies.
A load point whose solve fails numerically (a singular or non-finite Jacobian, typically past the nose) counts as not
converged and is counted in the Notes column and on stderr; any other exception stops the run.

Max loadability is the highest load factor that converged before the first one that did not. The rule is the shared
loadability loop the [`cpf`](#cpf) verb also runs (`ContinuationPowerFlow.ScanGrid` here, on this verb's fixed grid of
factors, every point solved in parallel). The two verbs answer different questions on purpose: `stress` re-dispatches
the generators at each load factor (`GridScenario.AtLoad`) and steps through a fixed grid, while `cpf` lets the slack
absorb the extra load and bisects to the nose.

| Flag | Default | Meaning |
|---|---|---|
| `<case>` / `--dir` | `case118` | Case name, `.m` file or case folder, or a JSON case directory (`--all` sweeps the registry; exit 2 with a case or `--dir`). |
| `--from` / `--to` / `--step` | 0.5 / 2.0 / 0.05 | Load-multiplier sweep. `--step` must be positive and `--from` must not be above `--to` (exit 2 otherwise). |
| `--parallel` | 0 (all cores) | Load points solved in parallel; a negative value is refused. |
| `--out` | `results/stress.md` | Report path (CSV alongside). |
| `--pv-out` | - | Also write the PV curves: for each case, every bus voltage at each load factor from `--from` up to the reported max loadability (the converged factors before the first failure), as `case,load_factor,bus_id,vm_pu`. A name not ending in `.csv` exits 2. |

### chain

`gridsim chain <case|--dir <path>> [--steps N] [--mode fixed|ramp|daily] [--ramp-pct F] [--out <path>] [--bess <site> ... storage flags]`

Consecutive warm-started solves: each step feeds the previous solution in as the initial condition. `fixed` re-solves
the same point (at most 1 iteration, no drift expected); `ramp` adds `--ramp-pct` % load per step; `daily` follows the
24 h demand curve. Step 0 solves from the case's own voltages, so the footer's warm-start maximum covers steps 1
onwards, and `fixed` mode prints `Warm start converges in <=1 iter with no drift - as expected.` when that maximum is 1
or below. Every step solves with the contingency option set and the tap-changer loop
([Solver selection](#solver-selection)). Exit 1 if the case does not load, 2 for an unknown `--mode`, 3 if any step
does not converge (the table still lists every step).

| Flag | Default | Meaning |
|---|---|---|
| `<case>` / `--dir` | `case118` | Case name, `.m` file or case folder, or a JSON case directory ([Case arguments](#case-arguments)). |
| `--steps` | 50 | Number of consecutive solves; at least 1. |
| `--mode` | `fixed` | `fixed`, `ramp` or `daily`. |
| `--ramp-pct` | 0.5 | Percent load increase per step (`ramp`). |
| `--out` | `results/chain-<case>.md` | Report path (CSV and `<report>.run-metadata.json` alongside); `<case>` is the last path element without `.m`. |

**Storage time series.** One or more `--bess` flags switch `chain` into the storage time series: each battery site
carries state of charge, temperature and ageing from step to step under a dispatch policy
([ch. 13](<0013 Battery storage studies.md>)). With storage the default `--steps` is 48 and the report goes to
`results/chain-storage-<case>.md` (CSV alongside). Each step solves with the same contingency option set and
tap-changer loop as plain `chain`. A step that does not converge prints `-` for its slack output and lowest voltage (the
CSV leaves slack, voltages and losses empty): it has no solved state. Exit 3 if any step fails to converge, or if a storage schedule LP does not solve to an optimum; exit 2 on a malformed site or policy, a site on a bus the case does not have, `--policy price` without `--prices`, or `--prices` with no value; exit 1 for a price file that is missing, unreadable or holds a value that is not a number (the message names the file and the line). An unknown case name exits 2 and a case file or folder that is missing or cannot be loaded exits 1, as on plain `chain`. The storage flags below need at least one `--bess`: on plain `chain` each of them exits 2 rather than being ignored.

| Flag | Default | Meaning |
|---|---|---|
| `--bess chem:bus:MW:MWh[:soc]` | - | A battery site; repeat for more. Chemistry `lfp`, `nmc`, `lto`, `na-ion`, `vrfb`, `lead-acid`; initial SoC 0.5. Four or five fields; MW and MWh must be numbers above 0 and the starting SoC a number from 0 to 1; anything else (a sixth field, 0 MW, a negative MW, SoC 1.5, an unknown chemistry, a bus that is not a number) exits 2 naming the spec and the field. Chemistry presets are `CuratedPlaceholder` `[VERIFY]`. |
| `--policy` | `peak-shave`, or `lp` when `--prices` is given | `idle`, `peak-shave`, `price`, `hold` or `lp`. `price` and `lp` act on the price series, so each needs `--prices` (exit 2 without: `--policy price needs --prices`, `--policy lp needs --prices`). `lp` plans the whole run as one storage schedule LP over the prices (exit 3 if that LP has no optimum). |
| `--prices synthetic\|<file>` | - | The price series (GBP/MWh, one per step) for the `price` and `lp` policies, and for the revenue line of the summary. `synthetic` is the labelled synthetic GB-shaped day (`SyntheticPrices.GbDay`, sampled at the start of each step; not a market record, and every output says so). A file holds one price per line, or CSV lines whose last field is the price; blank lines and lines starting with `#` are skipped; values use `.` as the decimal point and may be negative. Fewer values than steps exits 1; more is allowed (the first `--steps` are used, and a note on stderr says how many were not). `dcopf` and `acopf` take `--prices` as a switch of their own (nodal prices); every other verb refuses it (exit 2). |
| `--step-hours` | 24/steps (`daily`), else 0.5 | Hours per step. |
| `--shave-high` / `--shave-low` | 90 % / 70 % of peak demand | Peak-shave discharge and recharge thresholds (MW). |
| `--buy-below` / `--sell-above` | 40 / 90 | `price` policy thresholds (GBP/MWh): charge at full rating at or below the first, discharge at full rating at or above the second, hold between. |
| `--degradation-gbp-per-mwh` | 0 | Degradation cost per MWh throughput in the `lp` schedule's objective. |

### cycle

`gridsim cycle <case|--dir <path>> [--window-ms 1000] [--workers N] [--cycles K] [--shards N] [--jobs n-1,harmonics,rocof,physics,watch] [--hold-steady]`

The real-time prioritised parallel analysis cycle: one single-core Newton-Raphson base solve per cycle, then a
worker-pool fan-out of every downstream analysis (N-1 sweep, harmonics, RoCoF screening, physics conformance and the
watch list) over the shared immutable solved frame. High-priority work (base solve, hot N-1, harmonics, watch list)
completes every cycle; Low-priority work (rotating N-1 shards, RoCoF, physics) is admitted best-effort while the
core-millisecond budget (`workers x window-ms`) lasts, and any Low work deferred in a cycle keeps its place at the front
of the rotating pool for a later one (round-robin coverage, no starvation). Per cycle it prints base-solve ms, High-tier
completion, Low done/admitted/deferred, utilisation %, backlog depth and N-1 shard coverage. It is the soak driver of
the performance programme. Exit 1 if High is ever missed or the case file or folder is missing or unloadable; exit 2
for an unknown case name, a positional case that disagrees with `--dir`, `--pipeline`, or a `--jobs` list with an
unknown job or no job at all (`error: cycle --jobs: Unknown analysis job 'bogus'. Choose from: watch, n-1, nminus1,
harmonics, rocof, physics, conformance.`). An empty job set used to run every cycle with nothing to do and report
`HP met every cycle: YES` with exit 0.

| Flag | Default | Meaning |
|---|---|---|
| `<case>` / `--dir` | `data/uk` | Case name or JSON case directory to run the cycle on. |
| `--window-ms` | 1000 | Wall-clock deadline per cycle (ms), at least 1; the compute budget is `workers x window-ms` core-ms. |
| `--workers` | cores - 1 | Fan-out worker pool size, at least 1 (one core reserved for the single-threaded base solve). Results do not depend on the worker count; only throughput changes. |
| `--cycles` | 10 | Number of cycles to run; at least 1. |
| `--shards` | 8 | Rotating Low-priority N-1 shard count, at least 1 (full branch coverage is reached over this many cycles). |
| `--jobs` | all | Comma list of analyses to schedule: `n-1` (alias `nminus1`), `harmonics`, `rocof`, `physics` (alias `conformance`), `watch`, ignoring case. An unknown key, or a list with none, exits 2. The tiers are fixed: `watch`, `harmonics` and the hot-branch N-1 job are High; the N-1 shards, `rocof` and `physics` are Low. |
| `--hot-loading` | 80 | Branch loading (%) at or above which a branch joins the High-priority hot N-1 set. |
| `--hold-steady` | off | Hold the operating point fixed across cycles rather than stepping the live sim (comparable cycle-to-cycle timing). |
| `--pipeline` | - | Not implemented: exits 2 (the pipelined backlog drain does not exist; every job runs on the current frame). |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).

## Network analysis verbs

These verbs take their case as a positional argument only (a built-in name, a `.m` file, a JSON case folder or, for
`dist3ph`, a feeder file; see [Case arguments](#case-arguments)). A missing case exits 2, a case that does not load
exits 1. The verbs are used in [ch. 5](<0005 Running power flows.md>) (`pf`) and
[ch. 6](<0006 OPF, CPF, taps, distribution factors and losses.md>) (`dcpf`, `dcopf`, `acopf`, `cpf`, `taps`, `ptdf`,
`lodf`, `losses`); `dist3ph` is in [ch. 7](<0007 Three-phase distribution feeders.md>).

### pf

`gridsim pf <case> [--distributed-slack pmax|pg|equal] [--switched-shunts <file.json>] [--top 12]`

One AC power flow on the engine the shared rule picks (radial sweep, dense or sparse Newton), 100 iterations with
reactive limits on, printed: convergence, iterations, mismatch, losses, the voltage range and the `--top` lowest bus
voltages. Two opt-in controls wrap the unchanged engine as outer loops (like the tap-changer loop), so the dense and
sparse engines give the same bits with them too:

- `--distributed-slack pmax|pg|equal`: the real-power balance (losses and any scheduling error) is shared among the
  in-service units by participation factors proportional to Pmax, to scheduled Pg, or equal, instead of all landing on
  the reference bus. Each pass allocates the imbalance K, solves, and adds the reference bus's excess over its own
  allocation to K, until that excess is at most 1e-4 MW; units are held within [Pmin, Pmax] (water filling). The output
  lists the passes, K and each unit's move (the `--top` largest). A model with several reference buses, one in each
  network island, shares one imbalance per island among that island's units; two reference buses in one connected
  network are refused, naming them (exit 1). Every unit at a limit with imbalance left, or no settling within 50
  passes, is NOT CONVERGED (exit 3).
- `--switched-shunts <file.json>`: discrete shunt banks switched in whole steps to keep their bus voltage in a band.
  After each converged solve a bank below its band switches one step more capacitive, above it one step less, within
  its step range; a bank that reverses direction is frozen (it hunts across a band narrower than one step). One step
  adds `stepMvar` (MVAr at 1 pu, positive capacitive) to the bus Bs. The file:
  `{ "format": "gridsim-switched-shunts/1", "shunts": [ { "bus": 5, "stepMvar": 10, "minSteps": 0, "maxSteps": 4,
  "initialSteps": 0, "vminPu": 0.97, "vmaxPu": 1.03, "name": "C5" } ] }` (bus, stepMvar, maxSteps, vminPu, vmaxPu
  required; every key checked). The output lists each bank's steps and bus voltage. Not settled (a bank frozen outside
  its band, or 50 passes) exits 3.

| Flag | Default | Meaning |
|---|---|---|
| `--distributed-slack` | off | `pmax`, `pg` or `equal`; any other value exits 2. |
| `--switched-shunts` | - | A `gridsim-switched-shunts/1` file; a missing or invalid file exits 1. |
| `--top` | 12 | Lowest voltages and largest slack moves to list; 0 or more (a negative value exits 2). |

Exit 0 converged (and shunts settled); 3 not converged or not settled; 1 on a load error or a bad file; 2 on a usage
error (an unknown `--distributed-slack` rule, a flag of another verb).

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary (case, convergence, losses, voltage range, the slack and shunt outcome), a
`buses` table (every bus: |V|, angle, net injection), and with the options a `slack_units` table (every participating
unit) and a `switched_shunts` table. A solve that does not converge writes no file; unsettled shunts write the file
with exit code 3 inside.

### ptdf

`gridsim ptdf <case> --bus <id> [--top 12]`

Power transfer distribution factors: +1 MW injected at `--bus` and withdrawn at the slack, and the share that flows on
each branch, largest first (factors below 1e-4 are not listed). In a case with several DC islands the injection is
withdrawn at the reference bus of the injection bus's own island (its slack, otherwise its largest generator), and the
header says how many islands there are. Exit 2 without `--bus`, or for a `--bus` the case does not have (`error: ptdf
--bus <id>: case '<name>' has no bus <id>.`; it used to print an empty list and exit 0); exit 1 if the factors cannot be
built.

| Flag | Default | Meaning |
|---|---|---|
| `--bus` | required | Injection bus id. |
| `--top` | 12 | Branches to list. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).

### lodf

`gridsim lodf <case> --branch <idx> [--top 12]`

Line-outage distribution factors: how the flow of the outaged branch redistributes onto the others, largest first.
`--branch` is the 0-based branch index; out of range exits 2. A bridge outage that islands the system prints that the
LODF is undefined and exits 0.

| Flag | Default | Meaning |
|---|---|---|
| `--branch` | required | 0-based branch index to outage. |
| `--top` | 12 | Branches to list. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).

### dcpf

`gridsim dcpf <case> [--top 12] [--out <file.csv|file.json>] [--json <file.json>] [--csv <file.csv>]`

DC power flow: bus angles and branch flows from the linearised network, through the same DC model the PTDF, LODF and
DC-OPF use (`DistributionFactors`; Core `DcPowerFlow`). Only series reactance enters; resistance, line charging,
shunts, tap ratios and phase shifts are ignored, voltage magnitudes are 1 pu and there are no losses, so each DC
island's reference bus (its slack, else its largest generator) injects exactly the island's imbalance. Injections are
the in-service generators' output minus the bus demand. Prints a line per island (reference bus, buses, demand,
scheduled generation, what the reference injects), how many rated branches are above 100% of RateA, and the `--top`
largest flows with their loading. Overloads are reported, not an error.

| Flag | Default | Meaning |
|---|---|---|
| `--top` | 12 | Branches to list, largest flow first; 0 lists none, a negative value exits 2. |
| `--out` / `--json` / `--csv` | - | Write the result through the shared [result writer](#result-files). With a `.csv` name: every branch flow to that file (`index,name,from,to,flow_mw,rate_mva,loading_pct`) and every bus angle and net injection to `<stem>-buses.csv` beside it (`bus_id,va_deg,p_inj_mw`), plus `<stem>-islands.csv` and `<stem>-summary.csv`; with a `.json` name (or `--json`), the same tables as one document. Numbers in round-trip form. An `--out` name ending in neither `.csv` nor `.json` exits 2. |

Exit 0 on a solve; 1 when the case does not load or its DC model cannot be formed (an in-service zero-reactance branch
that is the only link between two islands, or a singular B'); 3 when an island carries load but has no generator (the
island line says `NO GENERATION: load cannot be supplied`); 2 for a usage error or a flag another verb owns.

### dcopf

`gridsim dcopf <case> [--no-limits] [--top 12] [--costs case|<file.json>] [--prices]`

DC optimal power flow: the cheapest dispatch that meets demand, with line limits enforced unless `--no-limits`. Prints
total cost (GBP/h), the dispatch by output and any binding line limits. The dispatch is feasible only when generation
can meet demand and, with limits on, the security-constrained LP finishes Optimal. An infeasible, unbounded or
iteration-limited LP prints `INFEASIBLE` and exits 3 (a verdict failure). Exit 1 if the case does not load or the
solve throws. On a connected case the `INFEASIBLE` line says why: `no dispatch within the units' output limits keeps
every rated line within its rating (security LP Infeasible)` (`--no-limits` drops the line limits), `the security LP
stopped without proving an optimum (status <s>)`, or `generation capability cannot meet demand (<x> MW of in-service
capability for <y> MW of demand)` when the merit order itself falls short.

**Lines no dispatch can relieve.** Before the LP runs, every rated line's DC flow is bounded over every unit anywhere
in its output range (the balance row dropped, so a superset of every dispatch the LP could choose). A line whose whole
range lies above its rating (or below minus its rating) by more than 1e-6 MW cannot be brought within its rating by any
dispatch: it is left out of the LP, as the corrective redispatch leaves out what no generator can move. Each one is
printed whatever the outcome, `<n> rated line(s) cannot be brought within rating by any dispatch; left out of the
security LP and still overloaded:` with its rating and the range of flows any dispatch gives it, and the summary says
`secure for every rated line except the <n> listed above`; they also appear among the binding lines with their flow
over the rating. The result file gains an `unrelievable_lines` table and `unrelievable_line_count` only then.
`dcopf gb-synthetic` leaves out 2 lines, the meshed supergrid transformers `MID SGT 400/275 1` (rating 1,000 MW, any
dispatch 1,409.0 .. 1,446.0 MW) and `LDN SGT 400/275 1` (rating 1,000 MW, any dispatch 1,258.1 .. 1,757.7 MW), and
dispatches the rest at 602,945 GBP/h (2.7 s wall time for the whole run on the reference machine).

A case with several DC islands (`gb-full` has two) is dispatched island by island: each island meets its own demand
from its own generation, against its own reference bus. The output lists every island (reference bus, buses, demand,
generation, verdict and LP status) before the dispatch. An island with no generator is listed as `NO GENERATION: load
cannot be supplied` when it carries load, and the run is then `INFEASIBLE`. With `--no-limits` the islands are
dispatched by merit order. `dcopf gb-full` dispatches the 3,536-bus main island (reference bus 194, 53,279.4 MW) and the
3-bus island (reference bus 6774, 5.6 MW), leaves out 3 lines (`COPP C1-_COPP T1 132kV`, rating 109 MW, any dispatch
-164.4 .. -125.7 MW; the parallel pair `ENW-ENW 132kV#23` and `#24`, rating 150 MW, 168.8 MW whatever the dispatch) and
dispatches 53,285 MW at 312,021 GBP/h; the run took 2 min 29 s of wall time on the reference machine. With `--no-limits` the same case dispatches both islands
by merit order at 53,560 GBP/h (4.9 s).

| Flag | Default | Meaning |
|---|---|---|
| `--no-limits` | off | Ignore branch ratings (unconstrained merit order). |
| `--top` | 12 | Generators, binding lines, and (with `--prices`) buses to list. |
| `--costs` | off | Dispatch on each generator's own cost curve instead of the fuel-type merit order. `case` uses the case's MATPOWER `mpc.gencost`; any other value is a `gridsim-costs/1` JSON cost file laid over the case (below). Every in-service unit must end up with a curve, else exit 1. The built-in IEEE names read their MATPOWER files and so carry the files' curves (`dcopf case9 --costs case` and `dcopf case9.m --costs case` give the same dispatch). |
| `--prices` | off | Also report the nodal prices (LMPs) and the binding lines' shadow prices, from the optimum's dual values. The dispatch is the same bits with or without it. |

**Cost curves (`--costs`).** Polynomial curves up to quadratic (MATPOWER MODEL 2 with NCOST 1-3; a longer row is
accepted only when the terms above the quadratic are exactly zero) and convex piecewise-linear curves (MODEL 1) are
optimised exactly, as MATPOWER's own DC OPF accepts them; a cubic or higher term, a negative quadratic coefficient or a
non-convex piecewise curve is refused (exit 1), naming the generator. Linear and piecewise-linear costs are solved by
the same structured simplex as the fuel-type dispatch (a piecewise curve becomes one LP column per segment); any
quadratic term is finished by an exact active-set quadratic program started at the simplex's vertex. The total cost is
the sum of the curves at the dispatch (constant terms included, as MATPOWER reports its objective) and each unit's
figure is its marginal cost there; the units are the case's own currency, printed as `cost units` (MATPOWER's cost
figures are in $/h by convention). Outside a piecewise curve's points its first and last segments are extended in a straight line. Startup
and shutdown costs are read and carried but play no part (one period, no commitment). The cost file:

```json
{ "format": "gridsim-costs/1",
  "generators": [
    { "index": 1, "model": "polynomial", "coefficients": [0.11, 5, 150] },
    { "name": "G2", "model": "piecewiseLinear", "points": [[0, 0], [100, 2500], [200, 6000]], "startup": 0 } ] }
```

`index` is 1-based in the case's generator list in file order (out-of-service units included: the order of MATPOWER's
`mpc.gen` rows or of `generation.json`); `name` must name exactly one unit; give one of the two. Coefficients are
highest order first, as in MATPOWER. Every key is checked: an unknown key, a unit named twice, a bad curve or a missing
format tag is refused (exit 1) naming the entry. A unit the file does not name keeps the case's own curve, if any.

**Prices (`--prices`).** With y0 the balance row's dual and y_r the dual of rated line r's thermal row, the price at
bus k is `LMP = y0 + sum_r y_r PTDF[r, k]`: y0 is the energy component (the price at the island's reference bus) and
the sum the congestion component; the DC model is lossless, so there is no loss component. A binding line's shadow
price is the cost saved per MW of extra rating. Each island is priced on its own; a dead island's buses get no price.
At a degenerate optimum the duals are not unique and those of the optimal basis found are reported. **Degenerate dispatch (N240).** When several dispatches are equally cheap (every MATPOWER case on the fuel-type merit order prices all its units at 50 GBP/MWh) the LP has many optimal vertices. The simplex breaks ties between entering columns whose reduced costs are equal to within 1e-10 (relative) by taking the lowest index, so the dispatch reported does not depend on the last bits of the PTDF sums (before October 2026, a change in summation order could move pglib case588's dispatch by up to 251.7 MW at the same cost). Every LP whose path meets no such near-tie gives the same bits as before. The console prints
the price range, the `--top` dearest buses and every binding line's shadow price; `prices: not available` when the
dispatch is infeasible. Validated against the published PJM 5-bus example (Li and Bo, 2010;
`data/matpower/nesta/pglib_opf_case5_pjm.m`, whose header records the paper's dispatch 40 / 170 / 323.49 / 0 / 466.51
MW: matched to the printed 0.01 MW) and against the HiGHS solver on pglib case3_lmbd, case5_pjm, case30_as and MATPOWER
case9 / case30 (dispatch, cost and every LMP within 1e-6). `gridsim dcopf pglib_opf_case5_pjm.m --costs case --prices`
prints a total cost of 17,479.90, LMPs 16.9774 (bus 1), 26.3845 (bus 2), 30.0000 (bus 3), 39.9427 (bus 4, the
reference: its LMP is the energy component) and 10.0000 (bus 5), and branch 5 (bus 4 - bus 5) binding at -240.0 MW
with a shadow price of 62.3220 per MW of rating.

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)). Without `--costs` the money is GBP and the keys say so (`total_cost_gbp_per_h`, dispatch
column `cost_gbp_per_mwh`). With `--costs` the money is the case's own cost units, so the keys carry no currency:
`total_cost_per_h` and the dispatch column `marginal_cost_per_mwh` (each unit's marginal cost), and the summary adds
`cost_source` and `cost_units` (`cost units`); before October 2026 the keys said GBP whatever the curves' currency. The
same rule applies to `acopf --costs`. A branch with no name (every MATPOWER branch) is shown in the console as its end
buses, `from-to`. With `--prices` two tables are added, `prices`
(`bus_id,lmp_per_mwh,energy_per_mwh,congestion_per_mwh`) and `shadow_prices`
(`branch,name,flow_mw,rate_mw,shadow_price_per_mwh`).

### acopf

`gridsim acopf <case> [--loss-tol 1e-3] [--max-iter 20] [--top 12] [--costs case|<file.json>] [--prices]`

DC dispatch with an AC loss loop (`AcOptimalPowerFlow`): NOT a full AC optimal power flow. It runs the DC-OPF dispatch
inside an outer loop that inflates demand by the AC losses, re-dispatches and re-solves the AC power flow until the
losses change by less than `--loss-tol` MW, so the dispatch pays for its own losses. Bus voltages, reactive power and
AC branch flows are neither optimised nor limited. The header says so: `ACOPF (DC dispatch + AC loss loop) - <case>
(<n> buses)  [not a full AC-OPF: no voltage, reactive-power or AC branch-flow limits]`. Prints the iterations, losses,
cost and dispatch. Exit 3 if the run does not converge, exit 1 if the solve throws.

A run that does not converge says which of three causes stopped it: `the AC power flow at the DC-OPF dispatch of loss
pass <k> ... did not converge` (with the solver status, iterations, largest mismatch and the lowest voltage where it
stopped; the DC dispatch ignores voltages and reactive power, so it can be a point the network cannot carry within the
units' reactive limits), `the losses settled after <n> pass(es), but the DC-OPF dispatch of the final pass is
infeasible` (run `dcopf` to see why), or `the losses did not settle within <n> pass(es)`. A run whose first DC dispatch
is infeasible in a way the loss loop cannot repair stops there, before any AC solve, with `the DC-OPF dispatch of loss
pass 0 (passes count from 0) is infeasible ..., and no later pass can be feasible` (exit 3). The loop only ever raises
demand, so this is decided only where it is proved: demand above the units' total Pmax, an island with demand and no
unit, or a security LP that is Infeasible while every unit may produce nothing (Pmin 0 or below), and only when no
branch resistance or bus shunt conductance is negative. Demand below the units' total Pmin, an LP that stalled, or line
limits with a unit whose Pmin is above 0 still run the full loop. When every unit is priced the same (a case with no
fuel types, such as every MATPOWER file), the first message adds that the dispatch is one arbitrary optimum of a
degenerate LP and that `--costs case` dispatches on the case's own cost curves. `acopf case118` and `acopf case300`
stop at pass 0 (the flat 50 GBP/MWh dispatch puts 25 and 50 units at Pmax, and the AC solve with reactive limits stops
with lowest voltages 0.7966 and 0.7806 pu); with `--costs case` `acopf case118` converges in 4 passes (losses
132.504 MW). `acopf gb-synthetic` converges in 6 passes (losses 1,235.507 MW, 675,277 GBP/h; 4 passes and 1,385.085 MW before the N240 tie-break chose a different optimal vertex of its degenerate DC dispatch) and its final pass leaves
out 5 rated lines that no dispatch can relieve (MID SGT 400/275 1 and 2, LDN SGT 400/275 1, YRK G8P4 132/33, MID G2P1S3
33/11). Since N316 (October 2026) the name is the shipped folder, whose branch order differs from the in-memory builder
it used to run; the folder stops at the same vertex and prints the same figures (measured at 1.95.259; `acopf
data/gb-synthetic` prints the same output byte for byte). When the final DC pass left out rated lines that no dispatch can relieve (see [`dcopf`](#dcopf)), the run
names them first (`the final DC pass left out <n> rated line(s) no dispatch can bring within rating (still overloaded;
...)`, up to `--top` of them).

| Flag | Default | Meaning |
|---|---|---|
| `--loss-tol` | 1e-3 | Loss convergence tolerance (MW); must be above 0. |
| `--max-iter` | 20 | Outer-loop cap; at least 1. |
| `--top` | 12 | Generators to list. |
| `--costs` | off | Every DC pass dispatches on the generators' own cost curves, exactly as [`dcopf --costs`](#dcopf); the reported cost is then the sum of the curves at the final dispatch, in the case's cost units. |
| `--prices` | off | Also print the nodal prices (LMPs, with their energy and congestion parts) and line shadow prices of the final DC pass, as [`dcopf --prices`](#dcopf) prints them. They are lossless DC prices at the loss-inflated demand that pass dispatched: the loss loop only scales demand, so there is no marginal-loss component, and voltage and reactive power do not enter them. The dispatch is the same with or without the flag. The result file gains the `prices` and `shadow_prices` tables and `prices_source`. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).

### acopf-full

`gridsim acopf-full <case> [--max-iter 150] [--top 12] [--cost-file <file.json>]`

The full AC optimal power flow (`AcOpfInteriorPoint`): a primal-dual interior point method, MATPOWER's MIPS with its
defaults, starting point and stopping conditions. It chooses every bus voltage (magnitude and angle) and every unit's
active and reactive output to minimise the units' polynomial cost curves, subject to the AC power balance at every bus,
each rated branch's MVA limit (RATE_A) at both ends, each bus's own voltage limits (else 0.94 .. 1.06 pu) and each unit's
P and Q limits, with every slack angle fixed. Transformer taps and phase shifts stay at their case values. The costs are
the case's own curves (a `.m` file's `mpc.gencost`), or `--cost-file` (the cost file format of [`dcopf --costs`](#dcopf)).
Unlike [`acopf`](#acopf) it optimises voltages and reactive power and enforces the AC limits.

Prints the iterations, the objective (in the case's cost units per hour), generation, load and losses, the largest units'
P and Q, the highest nodal prices (the multipliers of the active and reactive power balance at each bus, cost units per
MWh and per MVArh) with each bus's voltage, and the binding constraints with their shadow prices (`Vmin`/`Vmax` per pu,
`Pmin`/`Pmax`/`Qmin`/`Qmax` per MW or MVAr, `Sf`/`St` per MVA of the branch's from or to end, each per hour). On
`case9`, `case14`, `case30`, `case57`, `case118`, `case300`, `case1354pegase` and `case2869pegase` the objective rounds to
the figure MATPOWER's `runopf` prints (5296.69, 8081.53, 576.89, 41737.79, 129660.70, 719725.11, 74069.35 and 133999.29).

Exit 0 converged. Exit 1 for a case the method refuses, with the reason: a unit with no cost curve (give `--cost-file`)
or a piecewise-linear one, a dispatchable load (Pmin below 0 with Pmax 0), a voltage-dependent load, crossed or
non-numeric limits, a zero-impedance branch, no slack bus, or more than one island. Exit 3 when it does not converge
(`not converged: no convergence within <n> iteration(s)`, or `numerical failure: ...` for a singular Newton system, a step
below 1e-8 or a barrier parameter out of range); the iteration log is printed on stderr and no dispatch is shown, because
the point it stopped at is not an optimum. Branch angle difference limits are not enforced (the MATPOWER reader notes
them when a file has them).

| Flag | Default | Meaning |
|---|---|---|
| `--max-iter` | 150 | Iteration cap (MIPS `max_it`); at least 1. |
| `--top` | 12 | Units, buses and binding constraints to list. |
| `--cost-file` | the case's curves | A cost file (JSON, as `dcopf --costs <file.json>`) giving every unit's curve. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary (`objective_per_h`, `total_generation_mw`, `total_load_mw`, `losses_mw`,
`iterations`, `cost_source`) and the tables `buses` (`bus_id,vm_pu,va_deg,lmp_p_per_mwh,lmp_q_per_mvarh,mu_vmin,mu_vmax`),
`generators` (`unit,bus,pg_mw,qg_mvar,mu_pmin,mu_pmax,mu_qmin,mu_qmax`), `branches`
(`branch,from,to,pf_mw,qf_mvar,pt_mw,qt_mvar,rate_mva,mu_sf,mu_st`), `binding` (`kind,element,shadow_price`) and
`iterations` (the log). A run that does not converge writes no result file.

### scuc

`gridsim scuc <case> [--uc-data <file.json>] [--periods N] [--no-line-limits] [--node-cap 100000] [--gap 1e-6] [--top 12]`

Security-constrained unit commitment (`UnitCommitment`): which units run in each period, and at what output, at least
total cost. It is a mixed-integer linear program solved by `MilpBranchAndBound`, an LP-based branch-and-bound over the
sparse interior point LP, both in GridSim's own numerics (no external solver). Per unit and period: on/off and start
decisions (whole), output between Pmin and Pmax while on, minimum up and down times, ramp limits (a start may reach
max(ramp, Pmin) in its first period), start-up and no-load costs; per period: the demand balance (the case's bus loads
times the period's scale) and a spinning reserve requirement (the committed units' headroom Pmax - P); per rated branch
and period: the DC flow (PTDF times generation plus the flow the loads drive, as `dcpf` solves it) within RATE_A; and for
each listed outage, every rated branch's post-outage DC flow (by the line outage distribution factors) within RATE_A.

Costs: a unit's `marginalCost` from the data file, else the case's cost curve (a `.m` file's `mpc.gencost`: linear and
piecewise-linear curves are used exactly; a quadratic curve is cut into 4 chords between Pmin and Pmax, exact at the
break points and slightly above the curve between them, and a note says so), else the fuel-type cost of `dcopf`. A
polynomial curve's constant term is paid in every period the unit is on. The start-up cost is the data file's, else the
gencost STARTUP column, else 0. Without a data file every unit starts on (so nothing starts) and the run is one period
of the case's own load.

The data file (`gridsim-uc/1`, every key checked, every top-level key but `format` optional):

```
{ "format": "gridsim-uc/1", "periods": 24, "periodHours": 1,
  "demandScale": [0.7, 0.65, ...], "reserveMw": [100, 100, ...],
  "units": [ { "unit": 0, "marginalCost": 12.5, "noLoadCost": 100, "startupCost": 500, "minUp": 3, "minDown": 2,
               "rampUpMw": 80, "rampDownMw": 80, "initiallyOn": false, "initialPeriods": 5, "initialMw": 0,
               "mustRun": false },
             { "name": "G2", "startupCost": 200 } ],
  "contingencies": [ 4, "Line 7-8" ] }
```

A unit is named by `unit` (its index among the in-service generators, from 0) or `name` (unique); a contingency by a
branch index (from 0) or a unique branch name. `initialPeriods` is how long the unit has been in its initial state (a
unit inside its initial minimum time is held there); `initialMw` ramp-limits the first period. A unit whose Pmin is
below 0 (storage charging, an exporting link) is always on.

Prints the problem size, the total cost with its energy, no-load and start-up parts, each period's demand, generation,
headroom, reserve, system price and units on, the largest units' schedules (`1` on, `*` started, `0` off) and the binding
branch limits (intact, or after an outage). The system price is the balance row's dual in the LP with the commitment
fixed at the answer (the usual pricing of a commitment), per MWh.

Exit 0 optimal (within the gap). Exit 1 for input the commitment cannot build exactly, with the reason: a malformed data
file, a committable unit with no finite Pmax, several DC islands, an outage that islands the network, per-period arrays
of the wrong length, or more than 200,000 columns. Exit 3 when no optimum is proven: infeasible (no schedule meets the
demand, reserve and limits), the node cap reached (the best schedule found so far is printed, labelled `NOT PROVEN
OPTIMAL`, with the best bound), or a node relaxation that ended without an answer (the node is named).

| Flag | Default | Meaning |
|---|---|---|
| `--uc-data` | none | The unit commitment data file (`gridsim-uc/1`). |
| `--periods` | the file's, else 1 | The number of periods (must agree with the file's per-period arrays). |
| `--no-line-limits` | off | Leave the branch ratings out (a copper plate); listed outages are still secured. |
| `--node-cap` | 100000 | Branch-and-bound nodes before stopping; at least 1. |
| `--gap` | 1e-6 | Relative optimality gap. |
| `--top` | 12 | Units and binding limits to list. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary (`status`, `optimal`, `total_cost`, `energy_cost`, `no_load_cost`,
`startup_cost`, `bound`, `nodes`, `lp_iterations`, `rows`, `columns`, `integer_columns`, `line_limits`, `contingencies`,
`uc_data`) and the tables `periods` (`period,demand_mw,generation_mw,headroom_mw,reserve_mw,system_price_per_mwh`),
`schedule` (`unit,bus,name,period,on,started,output_mw`) and `binding` (`period,branch,name,contingency,flow_mw,rate_mw`;
contingency -1 is the intact network). A run with no schedule writes no result file.

### scopf

`gridsim scopf <case> [--contingencies all|<i,j,...>] [--post-rating A|B|C] [--linear-costs case|<file.json>] [--full-lp] [--max-rounds 50] [--top 12]`

Preventive security-constrained DC optimal power flow (`DcScopf`): the cheapest dispatch that keeps every rated branch
within RATE_A in the intact network and within its post-outage rating after each secured single-branch outage, with no
action after the outage. It is a sparse linear program on bus angles (each bus's balance and each branch's DC flow), and
each secured (outage, branch) pair adds the post-outage flow through the line outage distribution factors, solved by
GridSim's own sparse two-phase simplex. By default the pairs enter the LP only once the dispatch breaks them (constraint
generation; each round re-solves with the broken limits added, until none is broken by more than 1e-6 MW); `--full-lp`
puts every pair in from the start and gives the same optimal cost. The DC model is that of `dcopf`, `ptdf` and `lodf`
(series reactance only). See technical chapter 29 for the formulation and its validation.

Outages: `all` (the default) secures every in-service branch whose outage leaves the network connected; outages that
would island it are listed and not secured. A comma list of branch indices (from 0) secures those; a listed outage that
islands the network, an unknown or out-of-service branch, or a duplicate is refused (exit 1). Costs: the fuel-type cost of
`dcopf` (GBP/MWh), or with `--linear-costs` each unit's own curve (`case` for a `.m` file's `mpc.gencost`, or a
`gridsim-costs/1` file), which must be linear: a quadratic or piecewise-linear curve is refused (exit 1), not
approximated. A curve's constant term is added to the total cost.

Prints the outages secured and not secured, the LP size and solves, the total cost, the range of nodal prices (each bus's
balance dual: the cost of one more MW of demand there), the largest units' dispatch and the binding limits (intact, or
after the outage of a branch, against the rating used).

Exit 0 optimal. Exit 1 for input the SCOPF cannot build: several DC islands, a refused outage, a cost curve that is not
linear, a unit with no cost curve under `--linear-costs case`, invalid generator limits. Exit 3 when there is no secure dispatch:
the security set is infeasible, the round cap is reached, or an LP solve failed (the cause is printed); no dispatch is
printed and no result file is written.

| Flag | Default | Meaning |
|---|---|---|
| `--contingencies` | all | `all`, or a comma list of branch indices (from 0) to secure. |
| `--post-rating` | A | The rating after an outage: A, B (short-term) or C (emergency); A on a branch where B or C is not given. The intact network always uses A. |
| `--linear-costs` | fuel-type costs | `case` or a `gridsim-costs/1` file: each unit's cost curve, which must be linear. |
| `--full-lp` | off | Every post-outage limit in the LP from the start (no constraint generation). |
| `--max-rounds` | 50 | The most LP solves constraint generation may take; at least 1. |
| `--top` | 12 | Units, binding limits and islanding outages to list. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary (`status`, `optimal`, `total_cost_per_h`, `cost_units`, `demand_mw`,
`post_fault_rating`, `outages_secured`, `islanding_outages`, `rounds`, `post_outage_rows`, `rows`, `columns`,
`lp_iterations`, `full_lp`) and the tables `dispatch` (`unit,bus,name,p_mw,marginal_cost_per_mwh`), `prices`
(`bus_id,lmp_per_mwh`), `binding` (`branch,name,contingency,flow_mw,rate_mw`; contingency -1 is the intact network) and
`islanding_outages` (`branch`). A run with no secure dispatch writes no result file.

### qsts

`gridsim qsts <case> --study <file.json> [--top 10] [--allow-unsettled]`

Quasi-static time series (`QstsEngine`): a sequence of power flows, one per step, over the load, generation, PV and
storage profiles of a study file, warm-started from the previous step, with the controllers acting in time and their
state (tap positions, bank steps, delay timers, inverter set points, storage energy) carried from step to step. The
engine is the one the case solve picks: the backward/forward sweep for a radial feeder (or with `--bfs`), otherwise the
shared Newton engine on the selected backend, with the default power-flow options. Step 0 is solved from the case as
given, so a one-step study with nothing varying gives exactly the case's own power flow.

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
`stepSeconds` from t = 0, inline (`values`) or from a CSV column (`csv`, a path relative to the study file, and `column`,
a header name or an index from 0; a header row is recognised when the first cell is not a number); `"interpolation"`
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

### hosting

`gridsim hosting <case> [--study <file.json>] [--method snapshot|qsts] [--resource pv|load] [--buses all|<ids>] [--max-mw X] [--tolerance-mw T] [--snapshots d:r,...] [--resource-profile NAME] [--pv-function unity|pf|volt-var|volt-watt] [--mva-per-mw 1] [--load-pf 1] [--reverse-branch auto|none|NAME] [--reverse-allowance-mw 0] [--allowed-violation-s 0] [--tap-increase-pct P] [--criteria list] [--threads N] [--top 20]`

Hosting capacity (`HostingCapacity`): the largest PV (or, with `--resource load`, added demand such as electric vehicle
charging or heat pumps) at each candidate bus before a limit is broken, found by bisection on the size in MW. Each
candidate is studied on its own, with the resource added at that bus only.

Methods:

- `snapshot` (the default): one regulated power flow per snapshot, each a demand multiplier (on every bus with demand)
  and a resource multiplier (PV availability or the added load's level). Default snapshots: for PV, minimum demand
  `0.3:1` and full demand `1:1`; for load, `1:1`. Tap changers and capacitor banks of the study (or the case's OLTCs)
  settle instantly (every delay is taken as zero).
- `qsts`: the whole time series of `--study` (a `gridsim-qsts/1` file, see [`qsts`](#qsts)) with the resource added,
  driven by `--resource-profile` (a profile of the study; constant 1 when not given). A bus or branch may be outside its
  limits for `--allowed-violation-s` seconds before it counts, and `--tap-increase-pct` adds the tap-operation
  criterion: the run may make at most that percentage more tap operations than the base run.

Constraints (all by default; `--criteria` takes a comma list of `overvoltage`, `undervoltage`, `thermal`,
`reverse-power`, `tap-operations`, `non-convergence`): a bus above or below the study's voltage limits (default 0.94 and
1.06 pu), a rated branch above the loading limit (default 100 % of RATE_A), active power flowing back through the
`--reverse-branch` (the protection proxy: feeder protection and the source tap changer are set for forward flow;
`auto`, the default, takes the first named branch leaving the slack bus; `none` turns it off), more tap operations than
allowed, and a power flow that does not converge. A limit the base case (no added resource) already breaks counts only
where the resource makes it strictly worse, so an existing problem does not mask the capacity and is not blamed on the
resource; such limits are listed.

Search: `--max-mw` (default twice the case demand, at least 1 MW) is tried first; if it passes, the capacity is reported
as above it. Otherwise the bracket [0, max] is halved (mid = lo + (hi - lo) / 2) until it is at most `--tolerance-mw`
wide (default max / 1024). The reported capacity is the passing end, the "fails at" size the failing end, and the
limiting constraints are those broken at the failing end; for a limit that grows steadily with the size, the true
capacity lies between the two. The same sizes are evaluated in the same order on every run; `--threads` studies several
candidates at once (each with its own engine) without changing any number.

Prints the method, search, limits and any base-case violations, then one row per candidate (lowest capacity first):
the capacity, the failing size and the limiting constraint with where and how far.

Exit 0 when the study ran (a location limited within the range is a result). Exit 1 when the study is refused (every
problem is listed) or the base case does not solve. Exit 2 for a usage error (`--method qsts` without `--study`, a bad
flag value).

| Flag | Default | Meaning |
|---|---|---|
| `--study` | none | The `gridsim-qsts/1` study: the time series (qsts method), and the controls and limits (both methods). |
| `--method` | snapshot | `snapshot` or `qsts`. |
| `--resource` | pv | `pv` or `load`. |
| `--buses` | all | Candidate buses: `all` (every PQ bus) or a comma list of bus ids. |
| `--max-mw` | 2 x demand | Top of the search (MW). |
| `--tolerance-mw` | max / 1024 | Width of the final bracket (MW). |
| `--snapshots` | see above | Snapshot conditions as `demand:resource` pairs. |
| `--resource-profile` | constant 1 | The study profile driving the resource (qsts method only). |
| `--pv-function` | unity | The hosted PV's inverter function: `unity`, `pf`, `volt-var` (IEEE 1547-2018 Category B) or `volt-watt`. |
| `--mva-per-mw` | 1 | The hosted PV's apparent power rating per MW (at least 1). |
| `--load-pf` | 1 | The hosted load's power factor (lagging). |
| `--reverse-branch` | auto | The reverse-power branch: `auto`, `none` or a branch name. |
| `--reverse-allowance-mw` | 0 | Reverse power allowed before it counts (MW). |
| `--allowed-violation-s` | 0 | Time a bus or branch may spend outside its limits (qsts method, s). |
| `--tap-increase-pct` | off | Tap operations allowed above the base run (qsts method, %). |
| `--criteria` | all | The constraints judged (comma list). |
| `--threads` | 1 | Candidates studied at once. |
| `--top` | 20 | Rows to list. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary (`case`, `buses`, `engine`, `resource`, `method`, `max_mw`, `tolerance_mw`,
`candidates`, `evaluations`, `min_hosting_mw`, `base_violations`) and the tables `locations` (`bus_id,hosting_mw,
failing_mw,limited,limiting,detail,evaluations`; `failing_mw` is Infinity where the maximum passed), `limiting`
(`bus_id,constraint,element,value,detail`: every constraint broken at the failing size) and `base_violations`
(`constraint,element,value,detail`).

### cpf

`gridsim cpf <case> [--step 0.25] [--min-step 1e-3] [--max-steps 200] [--pv-out <file.csv>]`

Maximum loadability by stepping and bisection (`ContinuationPowerFlow.MaxLoadability`). Despite the verb's name it is
not a predictor-corrector continuation power flow (no tangent predictor, no parameterised corrector, no tracing round
the nose): it scales every bus's P and Q demand by a multiplier lambda, with the slack absorbing the extra generation,
solves an ordinary power flow at each lambda, steps lambda up while it converges and, on a failure, backs off and halves
the step. The answer is the largest lambda at which the Newton power flow still converges, a slightly conservative
estimate of the voltage-collapse nose (Newton fails as the Jacobian becomes singular). Prints the largest converged
lambda (round-trip precision) and the margin above base load. The line ends `nose bracketed to <min-step>` only when
the walk ended because its step fell below `--min-step`. When `--max-steps` runs out first it says `NOT bracketed:
--max-steps <n> ran out first; the nose lies between this lambda and <the smallest lambda that failed>` (or, when every
solve converged, that lambda is only a lower bound), the run explains on stderr and exits 3; before October 2026 it
printed `nose bracketed` either way and exited 0. Exit 3 when no lambda converges. A case whose base load (lambda 1) does not converge but whose lower loads do prints
the lambda below 1 and the negative margin (how far the load must fall before the case solves), then says on stderr
that the base case did not converge, and exits 3: the case as given has no loadability margin (`cpf data/intl/france`:
lambda 0.986328125, margin -1.37%, exit 3). This answers a
different question from `stress`, which re-dispatches generation at each load factor on a fixed grid of factors; the
two definitions are deliberately distinct. What counts as a converged point is the same for both: every point is
solved with the contingency option set and the tap-changer loop ([Solver selection](#solver-selection)), on the
selected backend. `cpf case9` prints `max loadability lambda = 2.373046875` and `cpf uk` `6.052734375`.

| Flag | Default | Meaning |
|---|---|---|
| `--step` | 0.25 | Initial lambda step; above 0. |
| `--min-step` | 1e-3 | Bisection stops below this step (the nose is bracketed to it); in (0, `--step`]. |
| `--max-steps` | 200 | Cap on power-flow solves; at least 1. Running out before the nose is bracketed to `--min-step` exits 3 (the lambda printed is then a lower bound). |
| `--pv-out` | - | Also write the PV curves: every converged point's voltage at every bus, one row each, as `case,lambda,bus_id,vm_pu`, in the order solved (lambda rises strictly). The points come from the same loop, so the printed lambda is the same. A name not ending in `.csv` exits 2. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)); the summary carries `nose_bracketed`, `solves` and `lowest_failed_lambda` (null when
every solve converged).

### taps

`gridsim taps <case> [--target 1.0]`

Coordinated tap schedule (`TapOptimizer.Flatten`): one set of tap ratios across every OLTC that minimises the sum of
squared bus-voltage deviations from `--target`, rounded to each OLTC's step grid. Prints the objective before and after
and each transformer's current and recommended tap.

Every OLTC record must name exactly one branch of the case and control a bus the case has; otherwise the run exits 1
before any solve, naming each bad record (an OLTC that matched no branch used to be dropped from the schedule without a
word). The schedule minimises every bus's deviation from the one `--target`, so it can leave the bus an OLTC controls
outside that OLTC's own target plus or minus its deadband, where its local regulator would move the tap away from the
schedule: such OLTCs are listed after the schedule (`<n> OLTC(s) would leave this schedule: ...`, with the controlled
bus, its voltage and the OLTC's own target and deadband); nothing is printed when every OLTC accepts the schedule. The
tap search (downhill simplex, at most 2,000 iterations) must meet its convergence test: when it stops at the cap the
schedule found is still printed, stderr says it is not a proven optimum, and the run exits 3 (it used to exit 0). Exit
1 when the case has no OLTCs or an OLTC record does not resolve, exit 3 when the base case does not converge or the
search does not converge. The result file adds `optimizer_converged`, `optimizer_iterations` and a `regulation` table
(`transformer,controls_bus,vm_pu,target_pu,deadband_pu,within_deadband`).

| Flag | Default | Meaning |
|---|---|---|
| `--target` | 1.0 | Target bus voltage (pu); above 0. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).

### restore

`gridsim restore <case> --order <b1,b2,...> [--vmin 0.90] [--vmax 1.10] [--qtol 1.0]`

Restoration (black-start) sequence check (`RestorationSequencer.Evaluate`). The branches named in `--order` start open,
whatever their stored state, and are energised one per step in that order; every other branch keeps its state in the
case. Each step re-solves the live (slack-connected) network with generator reactive limits enforced and the live
on-load tap changers regulating, then reports the lowest and highest bus voltage, the buses outside [`--vmin`,
`--vmax`], and the generator buses whose reactive output is within `--qtol` Mvar of a limit (the machine can absorb or
supply no more, which is what makes an energisation step unsafe, for example a Ferranti over-voltage on a lightly
loaded line). A step is feasible when it solves with no band breach and no generator at a limit.

A branch is named by its name in the case, or by `#k`, its 1-based row in the case's branch table (MATPOWER files carry
no branch names). Exit 0 when every step is feasible; 3 when any step does not solve or is not feasible (the table is
still printed, with the first unsafe step named); 1 for a branch that is not in the case, a branch named twice, a `#k`
outside the table or one that is also another branch's name, or a load error; 2 without `--order`, for an empty entry in
the list, or for a bad flag value.

| Flag | Default | Meaning |
|---|---|---|
| `--order` | - (required) | The energisation order: branch names or `#k` rows, comma-separated, no empty entries. |
| `--vmin` / `--vmax` | 0.90 / 1.10 | The voltage band checked at each step (pu; the statutory band of `VoltageLimits`). `--vmin` above 0, `--vmax` above `--vmin`. |
| `--qtol` | 1.0 | A generator bus whose reactive output is within this many Mvar of its summed Qmin or Qmax counts as at its limit; 0 or more. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): one row per step, with null voltages for a step that did not solve.

### losses

`gridsim losses [<case>]`

Loss allocation on the solved base case, under a header naming the case (`Loss allocation - <case> (N buses, M
branches)`): total P and Q losses, the real power drawn by bus shunt conductances (`Bus shunt conductance`, only when
the case has any: it is in the solver's injection total but in no branch), the reconciliation residual (rounding once
the shunt draw is accounted for), losses by zone (when the case has zones) and the 20 lossiest branches. `<case>`
defaults to `case9` and is resolved like every other case argument ([Case arguments](#case-arguments)): an unknown
name exits 2 naming it, a missing or unloadable `.m` file or case folder exits 1, and a second case argument exits 2.
Solves on the shared dense / sparse rule. Exit 3 if the base case does not converge. No flags.

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).

### dist3ph

`gridsim dist3ph <feeder.json> [sweep] [--tol 1e-10] [--max-iter 200]`
`gridsim dist3ph <feeder.json> fault --bus ID [--type all|3ph|slg|ll|llg] [--zf PU]`
`gridsim dist3ph <feeder.json> estimate --measurements <m.json> [--method wls|wlav|branch-current] [--tol 1e-10] [--max-iter 50] [--robust-passes 8] [--robust-floor 0.001]`
`gridsim dist3ph <feeder.json> harmonics --bus ID --spectrum TYPE --current PU [--afe feeder]`
`gridsim dist3ph <feeder.json> to-v2 --to <out.json>`

EXPERIMENTAL. The unbalanced three-phase distribution stack (`GridSim.Core.Distribution`) on a radial feeder read from
a `gridsim-dist3ph/1` JSON file, or (N339, October 2026) its JSON v2 form `gridsim-dist3ph/2` (format in [technical ch. 18](<../technical/0018 Real-time, distribution and dynamic estimation.md>);
bundled examples under `data/dist3ph/`, including the IEEE 4-node and the IEEE 123-node test feeders; user guide in
[ch. 7](<0007 Three-phase distribution feeders.md>)). The mode word follows the file and defaults to `sweep`. Each
mode accepts only its own flags; a flag from another mode is a usage error (exit 2), not silently ignored.

- `sweep`: the three-phase backward/forward sweep (`ThreePhaseSweep`). Prints per bus and phase |V| (pu and
  line-to-neutral volts), angle and the voltage unbalance factor. A non-converged sweep prints its status and the final
  voltage change on stderr, reports no voltages, and exits 3. The sweep solves single- and two-phase laterals (a phase
  a bus does not have prints `-` and has no result row; the unbalance factor is printed for three-phase buses only),
  line charging, regulators on fixed taps, closed switches, capacitors, and constant-current, constant-impedance and
  delta loads; a feeder with any of these (closed switches alone included) prints one line counting them, and a named
  bus (such as RG1) is shown by its name.
- `to-v2` (N339): writes the feeder as a `gridsim-dist3ph/2` document (`schemas/v2/dist3ph.schema.json`) at `--to`,
  then proves it: the written file is read back with the v2 reader and compared with the feeder bit for bit (every
  per-unit number by its 64 bits, ids, kinds, phases, transformer elements, order). The v2 form is the
  solver-authoritative per-unit model (closed unit vocabulary: `pu@system`, MVA, kV; nothing converted at load) with
  provenance: a document default and optional per-element entries. A feeder read from a v1 file (or lifted from a case)
  carries no provenance and is written with `CuratedPlaceholder` and a note naming the source and carrying `[VERIFY]`
  (GridSim invents no tag; set it before relying on the file); a v2 feeder keeps its own. Prints
  `wrote <file> (gridsim-dist3ph/2); read back: the same feeder, bit for bit` and the provenance tag. Exit 2 without
  `--to` or when the output exists (never overwritten); 1 when the feeder holds something the v2 form cannot (named,
  nothing written) or the file cannot be written; 3 when the written file does not read back to the same feeder (the
  difference named, the file kept). `gridsim validate <file>` checks a `gridsim-dist3ph/2` file against the schema and
  then with the reader itself. Every mode reads a v2 feeder as it reads a v1 one; the v1 format is unchanged.
- `fault`, `estimate` and `harmonics` model the elements above that they can define exactly and refuse the rest (exit
  1, naming each refused kind), never leaving an element out silently. `fault`: shunt capacitors, line charging and
  loads of every kind are neglected (shunt elements, as the constant-power loads are; the fault current is that of the
  series path) and a closed switch is a zero impedance; a single- or two-phase branch or a regulator on the path to the
  faulted bus is refused, naming the branch (elsewhere in the feeder it does not enter the study). `estimate` (WLS and
  WLAV): loads and capacitors are accepted (its measurement model has no injection measurements); laterals, line
  charging, regulators and closed switches are refused. `estimate --method branch-current`: every element is modelled
  (a regulator and line charging are undone from the measured From-end current in the forward sweep, a lateral drops
  only on its phases, a closed switch has no drop); a regulator or a charged line must face away from the source, and a
  current on a phase the branch does not carry is refused. `harmonics`: shunt capacitors and line charging (G + j h B)
  and closed switches (their buses one node) are in Y(h), and the printed network line says so; laterals and regulators
  are refused.
- The feeder may also be a radial positive-sequence case, a MATPOWER `.m` file or a JSON case folder: it is lifted into
  a balanced three-phase feeder (`MultiPhaseGridModelAdapter`: each branch's r + jx and charging on every phase, each
  bus load per phase, ZIP loads as constant-impedance, constant-current and constant-power parts, shunt capacitors, the
  slack as the source). A case the sweep cannot model is refused (exit 1): a voltage-controlled (PV) bus, an off-nominal
  tap or phase shift, a shunt conductance or reactor, a bus without a kV base, or a network that is not one radial
  tree. On such a lift the sweep agrees with the `pf` Newton solution.
- `fault`: sequence-network fault currents at `--bus` (`SequenceFaultStudy`): three-phase, single-line-to-ground,
  line-to-line and double-line-to-ground (ground current), in pu and amps, from a 1.0 pu prefault voltage behind an
  infinite source. Each branch is reduced to its transposed-equivalent sequence impedances. A fault at the source bus
  is refused (exit 1): the infinite bus has zero impedance. Every fault type refuses a zero loop impedance (for example
  a bolted fault at a bus joined to the source only by closed switches) and a non-finite current, naming the type,
  rather than printing Infinity or NaN.
- `estimate`: the three-phase node-voltage WLS estimator (`ThreePhaseWlsEstimator`, robust WLAV with `--method wlav`)
  on a `gridsim-dist3ph-measurements/1` file (per-phase voltage magnitudes, per-phase branch current phasors, optional
  nominal-voltage pseudo-measurements for unmetered buses). Non-convergence (including an unobservable set) exits 3
  with no estimate reported. A converged estimate prints the voltage table and then one residual row per measurement
  in the fit (`| measurement | phase | measured pu | estimate pu | r/sigma |`, magnitudes first, then currents as
  `re+jim`; a magnitude at the source bus is not in the fit and is not listed); the result file has the same rows as
  a `residuals` table. The estimator is dense (a 6n x 6n gain inverted every iteration), so a feeder with more than
  416 non-source buses (2,500 states) is refused, exit 1. `--method wlav` re-weights the measurements by
  1 / max(|r / sigma|, floor) for at most `--robust-passes` passes (default 8, at least 1) with the floor
  `--robust-floor` (default 0.001, above 0); both flags are refused (exit 2) with another method. When the last pass
  still moves a weight by 0.001 or more, the estimate prints `WARNING: the robust re-weighting (IRLS) reached its cap of
  N pass(es) before the weights settled; ...` (exit stays 0, as for the single-phase `estimate`; the result file has
  `irls_cap_reached`). With `--method branch-current` it is the branch-current estimator instead
  (`BranchCurrentDsse`): the current phasors are WLS-combined per branch and phase and one forward sweep from the
  source gives the voltages. It uses current phasors only, so a measurement file that also has voltage magnitudes or
  pseudo-measurements is refused (exit 1), as are `--tol`, `--max-iter`, `--robust-passes` and `--robust-floor`
  (exit 2). A current named against the reversed endpoints (`from` the To bus) is accepted as minus the From-end
  current, as `wls` and `wlav` accept it, except across a regulator or a charged line (exit 1, naming the branch). Every
  phase a tree branch carries needs a current phasor: a branch with no phasor, or with phasors on some of its phases
  only, leaves the estimate not converged (exit 3), and the message lists the unobservable branch phases (`1-2 b`).
- `harmonics`: the three-phase harmonic penetration (`ThreePhaseHarmonicPenetration`) with one balanced harmonic
  current source at `--bus`: the `--spectrum` profile of the harmonic source library (the same names as the
  `harmonics` verb's sources, for example `6-pulse-g5`; there is no default) at a fundamental current of `--current` pu,
  rotated by h x 120 degrees on phases b and c. The network is the feeder's series impedances with the reactance scaled
  by the order (with any shunt capacitors and line charging, and closed switches as one node), the source as the
  harmonic reference, and no loads. Prints, per order, the harmonic voltage at the source bus on each phase and in
  sequence components, and the bus with the largest harmonic voltage; then the root-sum-square harmonic voltage at the
  source bus per phase. Exit 1 for a bus not in the feeder, the source bus, or a singular network; 2 without one of the
  three flags or for an unknown spectrum.

| Flag | Default | Meaning |
|---|---|---|
| `--tol` | 1e-10 | sweep: max per-phase voltage change (pu); estimate: max state update. Above 0. |
| `--max-iter` | 200 (sweep), 50 (estimate) | Iteration cap; at least 1. |
| `--bus` | required (fault, harmonics) | Fault or harmonic-source bus id; must be in the feeder and not the source. |
| `--type` | `all` | `all`, `3ph`, `slg`, `ll` or `llg`. |
| `--zf` | 0 | Fault resistance (pu on the feeder base), at least 0: Zf = `--zf` + j0. The fault header prints it as `Zf = ... pu (resistive)`. Note that `fault` and `protection-check` read `--zf` as a fault REACTANCE (Zf = j`--zf`, printed `Zf=j...`); the same flag name means a resistance here and a reactance there (D-DOC-166). |
| `--measurements` | required (estimate) | The measurement file. |
| `--method` | `wls` | `wls`, `wlav` (robust IRLS outer loop) or `branch-current` (the branch-current estimator). |
| `--spectrum` | required (harmonics) | A harmonic source library profile: `6-pulse`, `12-pulse`, `pv-inverter`, `ev-charger`, `heat-pump-inverter`, `arc-furnace`, `saturated-transformer`, `6-pulse-g5`, `12-pulse-g5`, `afe-g5` or `ideal` (the idealised 1/h six-pulse spectrum, the same as `6-pulse`). |
| `--current` | required (harmonics) | The source's fundamental current (pu on the feeder base), above 0. |
| `--afe` | - | N354, harmonics only: `feeder` stamps the feeder file's active-front-end converter units (its v1 `converters` section: `id`, `bus`, `kva`, `xPercent`, optional `rPercent`, the internal impedance in percent on the unit's own rating; no default value exists [VERIFY]) into Y(h), each phase R + j h X between the phase and the converter's floating star point (a three-wire shunt: positive and negative sequence, no zero sequence). A line names the units. A feeder with no `converters` section exits 1; any other value exits 2. Without the flag the units are not stamped and the study is unchanged. N360: the v2 form holds the units too (its `converters` section: `id`, `bus`, `ratingMva`, `xOnRating`, optional `rOnRating`, per unit on the unit's own rating, the same doubles the v1 file gives), so `to-v2` writes them and reads them back bit for bit, and `--afe feeder` works on a v2 feeder. |

Exit 0 on success; 1 on an input error (unreadable or invalid feeder or measurement file, every message naming the file
and JSON path; a bus the feeder does not have; a study that refuses its input); 2 on a usage error; 3 when the sweep or
the estimator does not converge.

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).

## Physics and protection verbs

These verbs take `<case|case.m|case-dir>` as a positional argument. Protection and short-circuit work is in
[ch. 12](<0012 Faults, short circuit and protection grading.md>); the physics audit is in
[ch. 27](<0027 Validation tools.md>).

### physics-check

`gridsim physics-check <case|case.m|case-dir> [--tol MVA]`

Audit one solved state against the physical laws (energy balance, Ohm, Joule, KCL, complex power, and the rest of the
registry) and print one row per law. The gate is the hard laws (1, 2, 3, 6, 8 in `PhysicsCheckRegistry.HardGateLaws`)
AND a converged solve: `GATE: PASS` means the power flow converged and the state is physically admissible. The header
prints `converged=<true|false>` and the tolerance. A solve that did not converge prints its laws for diagnosis, then
`GATE: FAIL  -  the power flow did not converge (status ..., max mismatch ... pu after N iterations)` and exits 3. A
converged state that fails the gate prints `GATE: FAIL  -  physically-impossible state. Worst: law N <name>: residual
R <unit> above the tolerance T <unit> (<detail>)` and exits 1. Exit 1 also on a load error, 2 without a case or for a
bad `--tol`.

The reactive balance (law 8) is checked at every bus the solve ended as PQ, including a PV bus pinned at a reactive
limit (checked against that limit) and a PV bus with no generator. Law 9 (synchronism) reads `NotApplicable` with
`NOT SCORED` in its detail: a power flow has no trajectory.

| Flag | Default | Meaning |
|---|---|---|
| `--tol` | BaseMva x 1e-8 | Residual tolerance (MVA), one absolute line for every law. The default is the power-flow convergence tolerance (1e-8 pu) on the case's own base: 1e-6 MVA on a 100 MVA case, 1e-4 MVA on the 10,000 MVA `uk`. A negative value exits 2. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).

### conformance

`gridsim conformance [--tol MVA] [--out <dir>] [--no-allowances]`

Run the physics-law audit over the whole case registry (embedded classics plus every `.m` under `data/matpower/`) and
write `conformance.md`, `conformance.csv` and `run-metadata.json`. A case that did not converge is listed as such and
not counted in the pass total; a case that fails to load is skipped with a stderr line.

Known failures. Three shipped PGLib optimisation cases never converge as power flows (`pglib_opf_case3_lmbd`,
`pglib_opf_case162_ieee_dtc`, `pglib_opf_case240_pserc`, status `MaxIterations`: each stores an optimisation operating
point, not a power-flow start). They are on an explicit allowance list (`src/GridSim.Cli/ConformanceAllowances.cs`), each
with its reason: such a case shows `allowed` in the gate column and is listed with its reason under the table, and does
not fail the run. An allowance covers only that case ending with that status; the same case ending `NonFinite`, any
other unconverged case, and any case that cannot be loaded or run still fail. An allowed case that converges is
audited like any other and a stderr note says the allowance was not needed. `--no-allowances` switches the list off.

Exit 0 when every converged case passes and every unconverged case is allowed (the shipped corpus); 1 if any converged
case fails the hard-law gate; 3 if any case did not converge without an allowance or could not be run (when both
happen the exit is 3); 2 for a bad `--tol`.

| Flag | Default | Meaning |
|---|---|---|
| `--tol` | BaseMva x 1e-8 per case | Residual tolerance (MVA). The default is each case's power-flow convergence tolerance (1e-8 pu) on its own base; a value sets one absolute line for every case. A negative value exits 2. |
| `--out` | `results/conformance` | Output directory. |
| `--no-allowances` | off | Fail the run on every unconverged case, the known PGLib ones included. |

### protection-check

`gridsim protection-check <case|case.m|case-dir> [--uv pu] [--ov pu] [--uf Hz] [--of Hz] [--oc xIn] [--thermal frac] [--rocof Hz/s] [--cascade [--alarms <soe.csv>] | --fault-bus ID [--zf pu] [--fault-type 3ph|slg|ll|dlg]]`

Passive protection assessment of the solved state with generic relay settings: which elements would operate, with
pickup, time and basis. The solve itself is unchanged. Elements that need a trajectory (frequency derivatives) are
listed as not evaluated on a steady snapshot. The assessment requires a converged solve: a non-converged state never
gets a verdict. The verb refuses it with `protection-check: the power flow did not converge (...)` and exit 3. Exit 2
without a case; exit 1 on a load error.

With `--fault-bus ID` the same settings are assessed on a balanced three-phase fault at that bus
(`ProtectionAssessment.AssessThreePhaseFault`: the fault superposed on the converged prefault state, as the `fault`
verb does), so the current, voltage, distance and busbar-differential elements see a real fault; the fault current and
Thevenin impedance are printed first. Exit 0 with the verdict list; 2 for an unknown fault bus, a non-integer
`--fault-bus`, `--zf` without `--fault-bus`, a negative or non-finite `--zf`, or `--fault-bus` with `--cascade`; 1 when the fault
study itself fails; 3 when the prefault power flow does not converge, or when the converter current iteration of the
fault study does not converge (the study is printed, the relays are reported `not assessed`: no verdict is computed from a
state that did not converge).

The operating elements are listed fastest first (D-DOC-158): those with an operating time in ascending time
(instantaneous first, the busbar differential `87` among them; equal times in row order, that is the relay registry
order and then the bus / branch order of the case: N270, it used to be the margin, whose last bit decided between
distance zones that have the same margin in exact arithmetic), then the rest (voltage, frequency, reverse power,
undercurrent) by their margin relative to their own pickup. They used to be sorted by the raw margin, whose unit differs per element, so a
reverse-power margin in MW headed a fault report; the `e.g.` line of the verdict names the first row. The undercurrent
element `37` watches in-service branches only (D-DOC-091: it used to operate on every out-of-service branch, whose
flow entry is zero). The earth-fault distance element `21N` measures a branch end's earth loops only when the residual
current there exceeds 10 % of the largest phase current (`ProtectionSettings.EarthDistanceResidualReleaseFraction`,
generic; the 0.10 is the preset that the Siemens 7SA522 V4.3 manual recommends, address 1207A "3I0>/ Iphmax";
D-DOC-088: it used to operate on a line-to-line fault, which has no residual current).

| Flag | Default | Meaning |
|---|---|---|
| `--uv` / `--ov` | 0.80 / 1.10 | Under- and over-voltage pickup (pu). The defaults are the G99 Issue 2 Table 10.1 HV settings (`ProtectionSettings.UnderVoltagePu`, `OverVoltagePu`). |
| `--uf` / `--of` | 47.5 / 52.0 | Under- and over-frequency pickup (Hz). The defaults are G99 settings for a 50 Hz system: on a case at another nominal frequency (`case9`, 60 Hz; a `.m` file declared with `--frequency-hz`) `81U` and `81O` are reported not checkable, naming both frequencies, instead of operating on the healthy steady state. Giving both `--uf` and `--of` supplies settings for the case, taken as written for its own nominal frequency (D2, October 2026). |
| `--oc` | 2.50 | Instantaneous overcurrent pickup (multiple of rating). |
| `--thermal` | 1.00 | Thermal pickup (fraction of rating). |
| `--rocof` | 1.0 | RoCoF trip setting (Hz/s). |
| `--cascade` | off | Follow the propagation: trip the first branch element, re-solve, repeat, and print each step and the stop reason. |
| `--fault-bus ID` | - | Assess the protection on a balanced three-phase fault at bus ID instead of the steady state. Not with `--cascade`. |
| `--zf pu` | 0 | With `--fault-bus`: the fault reactance (pu, Zf = j`--zf`, 0 is bolted; printed `Zf=j...`); a negative or non-finite value exits 2. `dist3ph fault --zf` is a resistance instead (D-DOC-166). |
| `--fault-type` | `3ph` | With `--fault-bus`: `slg`, `ll` or `dlg` assess an unbalanced fault from the [`fault`](#fault) verb's sequence-network study: the earth-fault (`50N`, `51N`, `21N`) and negative-sequence (`46`) elements are evaluated on the sequence currents, the phase elements on the phase currents and voltages built from them. `3ph` is the balanced study. Exit 2 without `--fault-bus` or for another value. On a model with no transformer winding connection anywhere (every MATPOWER and JSON v1 case) `slg` and `dlg` print the verdicts, then say on stderr that the earth-fault verdicts reflect missing data and exit 3. |
| `--alarms <soe.csv>` | - | With `--cascade`: cross-reference the predicted cascade against a real sequence-of-events log (`SequenceOfEvents.CrossReference`; matched by branch and order, not exact time). Prints agreements, predicted-not-observed, observed-not-predicted and out-of-order trips, divergences first. Exit 3 when the prediction does not reproduce the log; exit 2 without `--cascade`; exit 1 when the file is missing or malformed (named line). |

The alarm log is CSV with the header `at_s,device,from,to,description`: seconds from the event start (finite), the ANSI
device id as logged (non-empty), the branch's two end-bus ids (both 0 for a non-branch alarm such as a bus voltage or
frequency alarm, which is listed but not matched), and free text (everything after the fourth comma). Blank lines and
lines starting with `#` are skipped; any other malformed line is refused, never dropped.

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).

### grade

`gridsim grade --chain CURVE:PICKUP:TD,... --current I[,I...] [--cti 0.4]`

Inverse-time overcurrent grading (`OvercurrentCoordination.GradeChain`): a radial chain of relays, listed downstream
first, graded pair by pair (primary then its backup) at each fault current. Each relay is `CURVE:PICKUP:TIMEDIAL` with
CURVE one of `iec-si`, `iec-vi`, `iec-ei`, `iec-lti` (IEC 60255-151, TMS) or `ieee-mi`, `ieee-vi`, `ieee-ei` (IEEE
C37.112, TD), or `dt:PICKUP:DELAY_S` for definite time. Pickup and current share one unit (amps or per unit); network
fault currents per branch come from `iec60909` (the current is an input here, which keeps the curves exact). A pair is
coordinated when the primary operates, the backup operates too, and the backup lags the primary by at least the CTI
(D-DOC-092: a backup that never operates at the current, below its pickup, reads an infinite margin and
`NOT COORDINATED (backup does not operate)`). Prints each pair's times, margin and verdict (a time
that never comes is `never`; an infinite margin prints `inf` or `-inf` with its sign, D-DOC-169), then the binding
(smallest) margin. Exit 0 when every pair is coordinated at every current, 3 when any is not, 2 on a usage error (an
unknown curve, a non-positive pickup or time dial, a negative delay, current or CTI, fewer than two relays, or a
`--current` list that is not strictly ascending). See [technical ch. 26](<../technical/0026 Overcurrent coordination (IDMT grading).md>).

| Flag | Default | Meaning |
|---|---|---|
| `--chain` | required | The relays, comma-separated, downstream first. |
| `--current` | required | One or more fault currents, comma-separated, finite and at least 0, in strictly ascending order (D-DOC-174). The comma separates currents, so a decimal comma such as `2000,5` used to be graded as the two currents 2000 and 5; out of order or repeated it now exits 2 asking for a decimal point (`2000.5`). Each current is printed as its own `fault current` section, so what was graded is always visible. |
| `--cti` | 0.40 | Required coordination time interval (s), finite and at least 0 (`OvercurrentCoordination.DefaultCoordinationTimeIntervalS`). |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).

### fault

`gridsim fault <case|case.m|case-dir> --bus <id> [--zf 0.0] [--type 3ph|slg|ll|dlg]`

Balanced three-phase fault at `--bus` through fault reactance `--zf` (pu, Zf = j`--zf`, printed `Zf=j...`; D-DOC-166:
`dist3ph fault --zf` is a resistance instead): fault current (pu, and kA when the bus has a
base kV), Thevenin impedance, faulted-bus voltage, the converter-interfaced units' current-limited infeed, and the
relays that operate on the faulted state. Generator subtransient reactance is synthesised at 0.20 pu where a unit
carries none. The prefault solve must converge: a non-converged prefault state is refused with `fault: the pre-fault
power flow did not converge (...)` and exit 3. Exit 2 without a case or `--bus`, for an unknown `--type`, or for a `--bus`
that is not in the case (`error: fault: fault bus <id> is not in case '<name>'.`, checked before any solve, the same
rule as `protection-check --fault-bus` and `iec60909 --bus`); exit 1 on a load error or a fault-analysis error. When
the converter current iteration of the three-phase study does not converge, the study is printed for diagnosis, the
relays are not assessed (`relays: not assessed (the faulted state did not converge, ...)`: a non-converged state is not
a network state), stderr says the faulted state is not reliable, and the run exits 3. A negative
or non-finite `--zf` exits 2 (`error: --zf expected a finite fault reactance of 0 pu or more (Zf = j zf) ...`), as
`protection-check --zf` does (D-DOC-089: it used to be accepted with exit 0, `fault case9 --bus 5 --zf -0.1` printing a
faulted-bus voltage of 2.776 pu). The relays are listed fastest first (see `protection-check`), and the closing line
names the primary clearance (`ProtectionReport.PrimaryClearance`), with its time: the faulted busbar's differential
`87` when it operates (instantaneous unit protection; its time now prints `inst`), otherwise the fastest operating branch
element with an operating time, or it says that no such element picks up (D-DOC-158: it used to name the first row of a
list sorted by raw margin, which on `fault uk --bus 1` was the Moyle reverse-power element). The faulted
state's line loadings (`LineFlow.LoadingPercent`, read by the result's callers, not by the relays) are taken on the
heavier end, as every power-flow engine takes them (D-DOC-090: they used the from end only).

`--type slg|ll|dlg` solves an unbalanced fault instead: single line (phase a) to ground, line to line (phases b-c), or
double line (b-c) to ground, on the three sequence networks superposed on the prefault state. The positive sequence is
the balanced study's network; the negative sequence the same with each unit's X2 (default X''d) and every phase shift
reversed; the zero sequence is built from the transformer winding connections and neutral reactances (the network
`iec60909` uses; line Z0 = 3 x Z1 where a line carries no R0 / X0), with loads neglected and no line charging. With
Zf = j`--zf`: SLG `I1 = I2 = I0 = V/(Z1+Z2+Z0+3Zf)`; LL `I1 = -I2 = V/(Z1+Z2+Zf)`; DLG
`I1 = V/(Z1 + Z2 (Z0+3Zf)/(Z2+Z0+3Zf))`. It prints the phase currents (pu and kA), the earth current `|3I0|`, the
sequence currents, the faulted bus's phase voltages and the driving-point Z1 / Z2 / Z0, then the relays that operate:
the phase elements on the phase quantities built from the sequence study and the earth-fault and negative-sequence
elements on the sequence currents (`50N` residual instantaneous at 1.0 x rated, `51N` residual IEC standard inverse at
0.2 x rated and TMS 0.1, `46` negative sequence at 0.2 x rated with 1.0 s, `21N` residual-compensated earth-loop
distance on lines with the 21 zones; generic values, [VERIFY] against a cited setting policy). A fault bus with no
earth path in the zero sequence carries no earth current: an SLG fault then gives zero current and a DLG fault is
solved as LL (both said). A model with no transformer winding connection anywhere (every MATPOWER and JSON v1 case,
so no bundled MATPOWER case) has no earthed neutral at all: `--type slg` and `--type dlg` print the study, then stderr
says the result reflects missing data and the run exits 3. `--type ll` does not use the zero sequence and exits 0.
A JSON v2 case can carry the data (N234, October 2026: phase P5 opened for fault sequence data): a transformer's
`windings`, `r0Pu` / `x0Pu` and `neutralXPu`, and a generator's `subtransientXdPu` / `negativeSeqXPu`; a v2 case that
gives windings runs `slg` and `dlg` with its own earth paths and exits 0 (see
[technical ch. 28](<../technical/0028 The v2 JSON schema.md>)).

Phase elements: `50` / `51` / `37` read the largest of each branch end's three phase currents, `49` that current over
the rated current, `27` / `59` each bus's lowest / highest phase-voltage magnitude, and `21` each end's three
phase-to-phase loops, `Z = (Vx - Vy) / (Ix - Iy)`, with the loop in the fastest zone deciding (named in the verdict,
for example `[b-c loop]`). A balanced fault and a power flow are assessed on the positive sequence alone. Converter-
interfaced units add no fault current in the unbalanced study: the model defines no negative- or zero-sequence fault
behaviour for them, so they stay at their prefault current in all three sequence networks. A `note:` line says how many
and their rated MVA whenever the case has any, and the result file adds `converters_without_fault_infeed` and
`converters_without_fault_infeed_mva`; the `3ph` study models their positive-sequence fast fault current. The result
file adds the phase, earth and sequence currents and a `branches` table (`index,name,from,to,max_phase_from_pu,
max_phase_to_pu,residual_from_pu,residual_to_pu,negative_sequence_from_pu,negative_sequence_to_pu`).

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).

### iec60909

`gridsim iec60909 <case|case.m|case-dir> (--bus <id> | --all-buses) [--type 3ph|ll|slg|dlg] [--objective max|min] [--tb 0.10] [--tk 1.0] [--breaker kV,making,breaking,withstand[,s[,asym]]] [--branches [--top N]] [--corrections --rated-pf <cos phi> [--no-unit-oltc]] [--peak-method b|c]`

IEC 60909 short-circuit duty at `--bus` (`Iec60909`): initial symmetrical current Ik'', peak ip with its factor kappa
and the fault-loop R/X, breaking Ib, steady Ik, thermal-equivalent Ith over `--tk` with its factor m, the DC component
at `--tb`, the Thevenin impedance, and the converter share of Ik''. Values print at round-trip precision. Line charging
and bus shunts are excluded from the positive and negative networks, as the standard directs. For `3ph`, Ib carries
each machine's near-to-generator factor mu at `--tb` through the IEC 60909-0:2016 multiple-fed formula, so it is below
Ik'' whenever a machine feeds more than twice its rated current; `ll` and `slg` report Ib = Ik'' (no decay for
unbalanced faults). Exit 2 without `--bus` (or `--all-buses`) or for an unknown bus; exit 1 on a load or study error. An
earth fault (`slg`, `dlg`) on a model that carries no transformer winding connection anywhere (no MATPOWER or JSON v1
case can hold them, so no bundled MATPOWER case does) has no earthed neutral: the duty is printed, then stderr says it
reflects missing data and the run exits 3. The `--all-buses` sweep, `fault` and `protection-check` with an earth-fault
type refuse the same way. A JSON v2 case can hold winding connections (N234, October 2026), and one that does is not
refused: its earth faults use its own earth paths and exit 0. See [technical ch. 25](<../technical/0025 IEC 60909 short-circuit duty study.md>).

`--peak-method b|c` (N134): how the three-phase peak factor kappa is found. `b`, the default, is IEC 60909-0 Method B
(below; every result without the flag is unchanged). `c` is Method C, the equivalent frequency method that NESO uses for
peak-make (ETYS Appendix D, docs/reference S242 p.4): the fault network's driving-point impedance is recomputed with every
reactance at fc = 0.4 f (20 Hz on a 50 Hz system, 24 Hz on 60 Hz), R/X = (Rc / Xc) x 0.4, and kappa = 1.02 + 0.98
e^(-3 R/X) with no 1.15 safety factor and no cap. Method C also gives the DC component idc(tb): its R/X comes from the
fault network at a second equivalent frequency set by f x tb (fc / f = 0.27 below 1 cycle, 0.15 below 2.5, 0.092
below 5, 0.055 below 10; `Iec60909.MethodCDcFrequencyRatio`, [VERIFY] the ratios), and a duty with f x tb at 10 cycles
or more (for example `--tb 0.2` at 50 Hz) is refused with exit 1, because the method gives no ratio there. Ik'', Ib,
Ith, m and the printed R/X are the same as with `b`. The `ip` line then ends `Method C: kappa(R/X_C), R/X_C <value> at fc = 0.4 f)` and the result file adds
`peak_method` and `r_over_x_c`. Every `--type` takes it (N353: `ll`, `slg` and `dlg` apply Method C on the fault's own
sequence loop, Z1 + Z2, Z1 + Z2 + Z0 or Z1 + Z2 || Z0, with every reactance at fc; with `--bus` or `--all-buses`); any
other value exits 2. Method C and fc / f = 0.4 are in IEC 60909-0:2016 subclause 8.1.2 (2001: 4.3.1.2 c)), read from the
2016 contents list; [VERIFY] the clause text, and the numbering of the new 2026 Edition 3.0.

October 2026 (D-DOC-096 / 097 / 098):

- Only the fault bus's island is studied: the buses connected to it through in-service branches. A separate island
  with no generator has no earth path in the IEC 60909 networks (no loads, charging or shunts), which made the whole
  matrix singular on the sparse path (above 1,500 buses): `iec60909 gb-full --bus 35` exited 1 at every bus while the
  dense path tolerated it. Every value of a connected case is unchanged; a fault bus whose own island has no source is
  refused by name (`... the island of fault bus N (k bus(es) ...) has none.`, exit 1, or listed in the sweep).
- In a MESHED fault network (a ring, a parallel circuit, or more than one source; one source on a radial network is not
  meshed) kappa is IEC 60909-0 Method B's `1.15 x kappa(R/X)`, capped at 2.0 (1.8 at 1 kV and below) but never below kappa(R/X), and the `ip`
  line says `meshed network: 1.15 x kappa(R/X), Method B`. It feeds ip only: the thermal factor m (and so Ith), idc
  and R/X keep the network's own R/X (with the cap binding, m would jump to its non-decaying limit of 2). The result file adds `kappa_meshed`. The clause is IEC 60909-0:2016 subclause 8.1.2 (2001: 4.3.1.2 b)), read from the 2016 contents list; [VERIFY] its text.
- The `Ik` line says which rule the steady-state current follows (result file `ik_basis`): far from every generator
  nothing is added (Ik = Ik'' exactly); near a generator fed from several sources it is the standard's Ik = Ik''M (the
  initial current without motors; GridSim models none); near a generator that is the fault's only synchronous source it
  says `UPPER BOUND`, because the standard's `Ik = lambda x IrG` needs the machine's saturated reactance x_dsat, which
  no case carries (Ith with n = 1 is then an upper bound too); an unbalanced fault takes no decay (Ik = Ib = Ik'').

| Flag | Default | Meaning |
|---|---|---|
| `--bus` | required (or `--all-buses`) | Fault bus id. |
| `--type` | `3ph` | `3ph` (three-phase), `ll` (line-to-line), `slg` (single-line-to-earth, on the zero-sequence network) or `dlg` (double-line-to-earth, the earth current `|3 I0|`). |
| `--objective` | `max` | `max` (voltage factor c = 1.10 HV / 1.05 LV, equipment rating) or `min` (c = 1.00 / 0.95, protection sensitivity). |
| `--tb` | 0.10 | Breaker minimum time delay (s) for Ib and the DC component; above 0. |
| `--tk` | 1.0 | Fault duration (s) for Ith; above 0. |
| `--breaker` | - | Check a switchgear rating against the duty (`Iec60909.CheckBreaker`): rated voltage kV, peak making kA, symmetrical breaking kA, short-time withstand kA, optionally its duration in s (default 1) and optionally the rated asymmetrical breaking kA (which adds the DC breaking check against `sqrt(Ib^2 + idc^2)`), comma-separated, every value above 0. The rated voltage must cover the highest system voltage `Um` of the bus (IEC 60038: 145 kV for 132 kV, 420 kV for 400 kV), not its nominal voltage; a double-line-to-earth duty is checked at its largest line-conductor current. Prints each check with its signed margin (rating minus demand; the withstand is referred to `--tk` on an equal-energy basis). An inadequate breaker exits 3. |
| `--branches` | off | List every in-service branch's from- and to-end current for the three-phase fault (`Iec60909.BranchFaultCurrents`, the current a relay on the branch sees; c_max source), every branch of the fault bus's island, largest first. Three-phase at one `--bus` only (else exit 2). The result file gets a `branch_currents` table. (D-DOC-168: the console list used to stop at 12 rows while its header counted every branch.) |
| `--top N` | all | With `--branches`: print only the N largest rows, and a closing line saying how many were left out (the result file still holds every branch). N at least 1; exit 2 without `--branches`. |
| `--all-buses` | off | The duty at every bus with a base kV instead of one `--bus` (not both, else exit 2). Prints one row per bus and the largest Ik''; the result file gets a `buses` table. A bus that cannot be studied (no base kV, no source) is listed with the reason and the run exits 1 after the table; with `--breaker`, an inadequate breaker at any bus (checked at the bus's own voltage) exits 3. |
| `--corrections` | off | Apply the IEC 60909-0 impedance-correction factors before the study (maximum objective only, else exit 2): `K_G` to a directly connected generator's X''d and X2, `K_S` (or `K_SO` with `--no-unit-oltc`) to a power-station unit (a unit with a unit transformer reactance) as a whole, `K_T` to each network transformer's impedance with x_T taken on its own rating from RateA (a transformer with no RateA, and any element on a bus with no base kV, is listed as not corrected). The model is per unit on nominal voltages, so the voltage ratios in the formulas are taken as 1. Needs `--rated-pf`. The factors applied are printed (range per factor) and written to a `corrections` table. [VERIFY]: the K_S / K_SO forms and the zero-sequence correction against the standard's text (IEC 60909-0 is not in docs/reference). |
| `--rated-pf` | - | With `--corrections` (required): the generators' rated power factor cos(phi_rG), in (0, 1]; GridSim carries none, so it is never assumed. |
| `--no-unit-oltc` | off | With `--corrections`: unit transformers without an on-load tap changer (`K_SO` at the main tap, p_G = 0, instead of `K_S`). |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).

### experimental

`gridsim experimental [<subcommand> <case> [flags]]`

Opt-in experimental models, tagged unvalidated-at-scale and kept off the validated solve and estimate paths. With no
subcommand it lists them. `<case>` must come straight after the subcommand and accepts `<case|case.m|case-dir>`.

| Subcommand | Does |
|---|---|
| `reciprocity <case>` | Physical-consistency law Y = Y^T (law 11). |
| `wave <case>` | Continuum electromechanical-wave model: inter-area normal modes, dominant mode and its shape. Needs per-machine inertia: a case with no generators or a generator with no inertia constant (H not above 0, as MATPOWER data loads) is refused with exit 1 right after the power flow and before the classical reduction builds its network matrices (about 1.46 GB on gb-full). The dominant mode is the first mode after the rigid-body (zero) modes, one per island of machines (N282, October 2026; it was the first mode above 1e-6 Hz, which could pick the centre-of-inertia mode or an island's); an islanded case prints a `rigid-body (island) modes` line and its inter-area list starts after them. The modes are those of the symmetric part of the synchronising coupling (R035, October 2026: the transfer conductances make the coupling asymmetric, and the symmetric eigen-solver used to be handed the asymmetric matrix, which lifted the centre-of-inertia mode off zero); a `coupling asymmetry set aside` line gives the largest \|k_ij - k_ji\| in MW/rad, and the mean stiffness is now in MW/rad (it was the per-unit figure). |
| `modal-consistency <case>` | Small-signal structural residuals (COI null mode, law 16, and the K_ext row sum). |
| `harmonic-kcl <case>` | Harmonic KCL, Y(h) V(h) = I(h), on the study-model penetration with a synthetic six-pulse source at the largest-demand bus (law 12): the G5 Issue 5 Table B.3 six-pulse profile `6-pulse-g5`, named in the header line. Exit 1 on a violation or a singular network. |
| `passivity <case> [--bus ID]` | The grid driving-point impedance is positive-real over 5-2500 Hz in 5 Hz steps (law 14). `--bus` defaults to the largest generator's bus; exit 2 if there is no generator and no `--bus`, or the bus is not in the model; exit 1 on a violation. |
| `trace <case> [--losses] [--top 5]` | Bialek proportional-sharing trace: where each generator's power went, or with `--losses` which generators cause the network losses. |
| `carbon <case> [--top 8]` | Carbon tracing: the gCO2/kWh each load consumes, from traced generation. |
| `boundaries <case> [--top 6]` | Which generators supply each ETYS boundary's transfer. |

Exit 2 on a missing case or an unknown subcommand; exit 1 on a load error or a flow graph with a loop (`trace`,
`carbon`); exit 3 on a non-converged solve.

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).

## Frequency and dynamics verbs

These verbs are used in [ch. 14, Frequency studies](<0014 Frequency studies.md>) (`freq`, `replay`, `transient`,
`domains`) and [ch. 15](<0015 RMS, modal and impedance stability.md>) (`stability`, `modal`).

### freq

`gridsim freq [csv]`

NESO 1-second frequency statistics plus a GB limit-breach scan (RoCoF 0.125 Hz/s, operational 49.8 Hz, statutory
49.5 Hz, LFDD 48.8 Hz) and a 20 ms upsample demo. Default input: `data/neso/frequency-sample.csv` beside the
executable. Exit 1 if the file is missing. A row newer than the 169 h forward-inference horizon is refused:
`REJECTED (forward inference): ...` and exit 2. The first non-blank line may be a column header (`dtm,f`) and blank
lines are skipped; any other line that is not `timestamp,Hz` with a finite frequency above 0 exits 1 naming the line
number and the line, and a file with no data rows at all exits 1 (it used to print zero statistics and exit 0). A
breach is reported, not an error: the exit is 0. The file may sit anywhere
on the line, before or after the flags; a second file argument exits 2.

The high side of the same bands is scanned too (R081, October 2026): when any sample is above 50.2 Hz an
`over-frequency:` line follows the limits line with the seconds above the operational 50.2 Hz and the statutory 50.5 Hz
edges. A trace that stays at or below 50.2 Hz prints no such line (the output is as before). The result file carries
`seconds_above_operational` and `seconds_above_statutory` beside the `seconds_below_...` keys.

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).

### replay

`gridsim replay [csv] [--sps F] [--seconds N] [--frames N]`

Play a recorded frequency trace in the terminal (gauge and RoCoF), upsampled to 20 ms. Keys: space play/pause, up/down
speed, left/right step, Home restart, Q quit. Exit 1 if the file is missing or empty, or if a line in the loaded
window is not a valid `timestamp,Hz` row (named in the message, as for `freq`); exit 2 with `REJECTED (forward
inference): ...` for a row newer than the 169 h horizon. The file may sit anywhere on the
line (`replay --sps 100 run.csv` plays `run.csv`); a second file argument exits 2.

| Flag | Default | Meaning |
|---|---|---|
| `[csv]` | `data/neso/frequency-sample.csv` | Source trace. |
| `--sps` | 50 | Slices per second (50 is real time), 0.25 to 2000; outside that range exit 2. |
| `--seconds` | 600 | Seconds of source to load; at least 1. |
| `--frames` | 0 | Above 0 runs headless for N slices then exits. |

### transient

`gridsim transient <case|--cases a,b,c|--cases suite> [flags]`

Headless demanding time-domain run: a simulated duration at a fine slice, integrating swing-equation frequency each
slice. Writes `timeseries.csv`, sampled solve-math exports (`exports/slice-*.json`), `summary.md` and
`run-metadata.json`. `--cases suite` (or `all`) runs case9, case14, case30, case57, case118, case300 and
`pegase/case1354pegase.m`. The global `--forming` (with `--forming-inertia` and `--forming-damping`) and
`--asset-dynamics` switches are honoured: they reach the live frequency integrator, and the run metadata names them.
`--asset-dynamics` on a case with no asset bundle exits 1 for that case. The frequency is integrated at the case's own
nominal frequency (case9 and the two-area case at 60 Hz, every GB and MATPOWER-file case at 50 Hz unless
`--frequency-hz` says otherwise), so a 60 Hz run's `freq_hz` column and nadir sit about 60 Hz. With
`--asset-dynamics`, inverter Loss-of-Mains trips book the dispatch actually running, and both those trips and the
protection devices' demand sheds are restored by secondary redispatch over about 5 minutes, as a `--trip` loss is.

| Flag | Default | Meaning |
|---|---|---|
| `<case>` | `case9` | Case name, or a `.m` file under `data/matpower/`. |
| `--cases` | - | Comma list, or `suite` / `all`. |
| `--minutes` (alias `--duration`) | 60 | Simulated duration in minutes; above 0. |
| `--dt-ms` | 1.0 | Slice length, milliseconds; above 0. |
| `--log-every` | 1 | Write every N-th slice to the CSV; at least 1. |
| `--export-every` | 0 (off) | Full solve-math export every N-th slice (slice 1 and the trip window always export). |
| `--export-jac` | off | Include the Jacobian in exports. |
| `--trip` | 0 (none) | Infeed MW to trip mid-run. |
| `--trip-at` | 0 | Trip time (seconds). Exit 2 if past the run end: the contingency would never fire, or without a `--trip` above 0 (nothing to time). |
| `--trip-window` | 2.0 | Seconds each side of the trip always logged and exported. Exit 2 without a `--trip` above 0. |
| `--solver` | `auto` | `dense`, `sparse`, `bfs` / `sweep` or `auto` (any other value exits 2). `auto` sends a radial network to the sweep, uses sparse above 1,500 buses (the `LiveSim` per-tick threshold) and dense otherwise. |
| `--dyn-data` | - | A machine dynamic data file (`gridsim-dynamics/2`, as for [`trip`](#trip)): each generator it binds takes the file's inertia H for the live swing (the only per-machine number it integrates); a stderr line says so, and the run metadata and summary header add `dyn-data=<file> (inertia H only)`. One case only: with `--cases` it exits 2. A missing or invalid file exits 1, before the run. |
| `--out` | `results/transient-<case>-<stamp>` | Output directory (batch runs use `results/transient-<stamp>/<case>`). |

Exit 1 if no case loaded at all, if some cases of a batch failed to load, for `--asset-dynamics` on a case with no
asset bundle, or for a `--dyn-data` file that is missing or invalid; exit 2 for a bad `--dt-ms`, `--minutes`
or `--solver`, a `--trip-at` past the run end, `--trip-at` or `--trip-window` without a `--trip` above 0, or `--dyn-data` with `--cases`; exit 3 if any slice failed to converge (frequency and RoCoF from
unconverged states are not trusted).

### domains

`gridsim domains [<case>] [--dir <path>]`

Report the speed domains of the distributed-inertia model: per-domain machine count, GVA.s and GVAd stiffness, and
whether a dominant domain exists (deltaGVA at least 10 %). Default model `data/uk`; a positional case or `--dir`
selects another ([Case arguments](#case-arguments)). It is the diagnostic companion to `--inertia-model distributed`.

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).

### stability

`gridsim stability [<case>] [--dir <path>] [--rms] [--avr none|sexs|ieeet1] [--pss] [--dyn-data <file>] [--screen] [--fault-bus N] [--clear ms] [--clear-by-trip K[,K...]] [--events <list> [--events-out <dir> [--events-every N]]] [--export <json>]`

Classical multi-machine transient stability (constant E' behind X'd, Kron-reduced network, RK4 swing). Default case
`case9` (Anderson-Fouad X'd; any other case uses a synthetic 0.30 pu default and is flagged INDICATIVE). `<case>` is a
[case argument](#case-arguments); `--dir` loads a JSON case directory instead. A positional case and `--dir` together
exit 2 unless they name the same folder, and a second positional case exits 2.

| Flag | Default | Meaning |
|---|---|---|
| `--rms` | off | Use the salient/subtransient **RMS engine** (six-state GENROU machines) instead of the classical swing. GENROU parameters are synthesised from X'd, so the result is INDICATIVE; the engine itself is validated (it reduces to the classical CCT). Works with `--screen`, `--fault-bus` and `--clear`; with `--clear` it also reports the converter fleet's fault ride-through. |
| `--avr` | `none` | With `--rms`: `none` (field held), `sexs` or `ieeet1` exciters on every synchronous machine (typical, indicative settings); any other value exits 2. |
| `--pss` | off | With `--rms` and an AVR: add a PSS1A stabiliser to each exciter. Without an AVR it exits 2. |
| `--dyn-data` | - | A machine dynamic data file (`gridsim-dynamics/2`, see `schemas/schemaNotes.md`), or a PSS/E `.dyr` file when the case is a PSS/E `.raw` file (its records are mapped onto the case's machines, then checked and bound the same way; [ch. 2](<0002 Loading networks.md#dynamic-data-dyr>); `--allow-unmodelled` leaves out what GridSim cannot represent; every `--dyn-data` verb takes a `.dyr` file this way). Classical engine (no `--rms`, single bus and `--screen`; N314, October 2026, it used to exit 2 there): the classical swing is built from X'd, H and the damping D, and each machine the file binds takes all three from it, exactly as [`modal`](#modal) does (X'd on the machine base; D per unit on the system base, the same quantity as the RMS study's; H the case's own where the template gives none); the other machines keep the X'd map (Anderson-Fouad on case9, else the synthetic 0.30 pu) and D = 0. The header says how many X'd values came from the file and adds a `machine data:` line with the loader's notes; the result stays INDICATIVE unless no X'd is synthetic and every value is tagged `Recorded` or `DerivedFromRecorded`. A file whose values equal the built-in ones gives the same clearing times. `--export` stays the undamped kernel check (the export records the file's H and X'd). With `--rms` (F23): Each machine it binds takes the file's GENROU reactances and time constants, inertia (the case's own H when the template gives none, said in a `note:` line), damping, saturation and leakage, and the exciter (SEXS, IEEET1, EXAC1, ESAC1A or ESST4B, with a PSS1A), and governor (TGOV1, IEEEG1, HYGOV or GAST) its template names; the other machines keep the synthesised data. A file exciter replaces `--avr` on its machine; `--avr` and `--pss` fill the rest. ([`modal`](#modal), [`trip`](#trip) and [`transient`](#transient) take the same file for the part of it their models use.) The header says how many machines the file covers, which governors it brought and the provenance tags of its values. The INDICATIVE footer goes only when the file covers every machine and every value is tagged `Recorded` or `DerivedFromRecorded`. On either engine a missing file or one that fails the v2 schema, kind-spec or binding checks exits 1 with every problem listed. |
| `--screen` | off | Critical clearing time at every generator bus, worst first. It has no single fault bus and no fixed clearing time, so with `--fault-bus` or `--clear` it exits 2 before any work, naming the flag (N266, October 2026; both used to be accepted and silently ignored); with `--export` it exits 2. The header counts the integrated machines and the units held as constant injections (converter, power-flow-only and zero-inertia units), as the single-bus run does. |
| `--fault-bus` | 7 (case9) / first non-generator bus | Fault location. |
| `--clear` | - | Fixed clearing time in ms, giving a STABLE / UNSTABLE verdict; omit for the critical clearing time. Classical and `--rms`. Must be a finite number, 0 or more (0 is an instantaneous clear); a negative value, `NaN` or `Infinity` exits 2 (a negative value used to run a study with no fault in it and report STABLE). At most 1000 ms (N266, October 2026): above it exits 2. Why 1000 ms: the slowest clearance GB plant coordinates with is back-up protection within 800 ms in England and Wales (300 ms in Scotland), Grid Code CC.6.2.2.2.2; 1000 ms is the window both CCT searches use, so a fixed clearing time stays inside the range the CCT answers; and the RMS study runs a fixed 3 s, so a much later clear would end the run with the fault still on. Without the bound a large value also grew the run's time and memory without limit. Both engines (N246, October 2026; the `--rms` engine since CF2): under the verdict a `law 9 (synchronism, scored from this run): HOLDS` (or `VIOLATED`) line scores the physics law from the run's own rotor-angle trajectory with the out-of-step criterion of PSS/E and PowerFactory, any machine more than 180 degrees from the centre of inertia at any sample: `largest rotor angle from the centre of inertia X deg (limit 180 deg, the out-of-step criterion); peak speed deviation Y Hz, reported, not scored (no frequency band in a fault study)`. The verdict itself is unchanged (the integrator's pole-slip test, 360 degrees from the centre of inertia); when a machine passes 180 but not 360 degrees a `note:` line says the two criteria differ. On `--rms` the centre of inertia and the maximum cover the machines the RMS verdict judges (island reference machines are left out of both, as the verdict leaves them out). The steady-state law 9 row of `physics-check` names this verb as the one that scores it. |
| `--clear-by-trip` | - | N351, `--rms` only: the fault at `--fault-bus` is cleared by also tripping these branches (comma list of 0-based indices into the case's branch list), as a line fault is cleared by its breakers. Without `--clear` it is the critical clearing time search with those trips at every trial's clearing instant (the same step grid, window and first-loss rule as below); with `--clear` a STABLE / UNSTABLE verdict for that clearing time. Not with `--screen` or `--events` (exit 2). |
| `--events` | - | N351, `--rms` only: one scheduled RMS event study. A comma list of `kind:arguments@seconds` items: `fault:BUS@t` (bolted fault), `clear:BUS@t` (its clearance), `trip:K@t` (branch K trips, 0-based), `reclose:K@t` (it closes again: auto-reclose), `load:BUS:P:Q@t` (a load step of P MW and Q Mvar, negative to shed) and `gentrip:G@t` (generator G trips, 0-based). For example `--events "fault:7@0.1,clear:7@0.183,trip:5@0.183,reclose:5@0.683"`. Prints the events and a STABLE / UNSTABLE verdict over the connected machines. The run lasts 3 s, or 3 s past the last event. It owns the whole disturbance: with `--screen`, `--fault-bus`, `--clear` or `--clear-by-trip` it exits 2; a malformed list exits 2 naming the item; an event the study refuses (a bus not in the case, a reclose of a branch that is not tripped, a time after the end) exits 2 with the reason. |
| `--events-out` | - | N363, with `--rms --events` only (otherwise exit 2): writes the event study's export to the folder (created if missing): `rms-events-machines.csv` (every machine trace at every integration step: `t_s,trace,bus,delta_rad,speed_pu,eqp_pu,edp_pu,eqpp_pu,eddp_pu`, each number in its shortest round-trip form), `rms-events-converters.csv` when the fleet has converter plant (`t_s,converter,name,bus,v_pu,p_pu,q_pu,ip_pu,iq_pu,in_dip_mode`), `rms-events.json` (schema `gridsim-rms-events-export/1`: case, nominal frequency, step, duration, the events in time order, the verdict, converter-iteration failures, the machine list with island references marked, provenance `Synthetic` and whether the result is INDICATIVE) and `run-metadata.json`. Both CSVs start with the run header and a `# provenance:` line (`Synthetic`, a model trajectory, not a record; INDICATIVE when the GENROU data is synthesised). A file that cannot be written exits 1. The run and its verdict are unchanged. |
| `--events-every` | 1 | With `--events-out` only (otherwise exit 2): write every N-th integration step to the export CSVs (steps 0, N, 2N, ...; a whole number from 1 to 1000000, anything else exits 2). The default 1 writes every step, so an export without the flag is the full-resolution one. `rms-events.json` records `everySteps` and `samplesWritten`; the run itself is unchanged. |
| `--export` | - | Write a `gridsim-classical-export/1` trajectory for the scipy cross-check (`tools/crossval/crossval_classical.py`). Classical single-bus run only: with `--rms` or `--screen` it exits 2 before any work (`error: stability: --export writes the classical single-bus trajectory and is not available with --rms (no file would be written).`); it used to be accepted with no file written. A missing folder is created ([Output files](#output-files)). |

With `--rms` the critical clearing time is step-quantised: the fault is switched only on whole 2 ms integration steps,
so the reported CCT is the longest clearing time on that step grid that keeps synchronism, and clearing one step (2 ms)
later loses it. The output says so, and a single-bus run prints the two ends of the bracket. Digits below the step
carry no information and are not reported.

The classical critical clearing time (no `--rms`) is step-quantised in the same way: the longest clearing time on the
2 ms step grid that keeps synchronism, with one step (2 ms) more losing it. Both the single-bus run and `--screen` say
so, and a single-bus run prints the bracket (for example "clearing at 550 ms holds synchronism, at 552 ms it is lost"
for case9 bus 7). 0 ms means even an instantaneous clear loses synchronism; 1000 ms means nothing in the 1000 ms
search window loses it.

Both CCTs (classical and `--rms`) are the first loss of synchronism from below. Stability is not always monotone in
the clearing time: after the bisection, the 16 step counts (32 ms) below the reported CCT are checked to hold and as
many above the first loss are probed. A later clearing time that holds again (a stable pocket) is flagged: `--screen`
adds `NON-MONOTONE: holds again at N ms` to that bus's row, and a single-bus run prints a warning line. On case9
(classical) bus 2 reports 312 ms with a pocket at 320 ms, and bus 1 reports 498 ms with a pocket at 502 ms:

```
$ gridsim stability case9 --screen
GridSim  -  TRANSIENT STABILITY SCREEN (classical multi-machine)  *  WSCC-9
==================================================================
3 machines (0 converter / power-flow-only / zero-inertia units held)  *  3 generator-bus faults  *  X'd = Anderson-Fouad

 Rank  Fault bus   CCT (ms)
    1          3        302   <- most critical
    2          2        312   NON-MONOTONE: holds again at 320 ms
    3          1        498   NON-MONOTONE: holds again at 502 ms

Self-clearing 3-phase fault at each generator bus; lower critical clearing time = less margin.
CCT = longest clearing time on the 2 ms integration-step grid that keeps synchronism (clearing one step, 2 ms, later loses it); resolution 2 ms.
A CCT of 0 ms means even an instantaneous clear loses synchronism; 1000 ms means no clearing time in the 1000 ms search window loses it.
It is the first loss from below: the 16 steps (32 ms) below it are checked to hold and as many above it are probed; NON-MONOTONE marks a later clearing time that holds again (a stable pocket).
Each trial is judged over the 5 s after the fault clears (the documented default window: first-swing stability, the range in which the classical constant-E' model holds); an undamped system can lose synchronism on a later swing, so a longer window can give a shorter CCT.
[exit 0]
```

The checks cost up to 32 extra trial runs per bus (3.5 times the bisection alone on case9). With `--rms` the verb runs
one search per bus as well; its stable end is the CCT and the same search gives the flag.

The criterion and the window (N341 / N343, October 2026). The CCT is the first loss: the largest clearing time below
which every trial holds synchronism, which is what PSS/E and PowerFactory report; a stable pocket above it is printed as
a note (`NON-MONOTONE`), never as the CCT. Each trial (classical and `--rms`) is judged over a fixed window after the
fault clears, 5 s by default (`ClassicalInit.CctPostClearWindowSeconds`, `RmsTransientStability.PostClearWindowSeconds`):
first-swing stability, the range in which the classical constant-E' model is valid. Every CCT explanation ends with a line
saying so: `Each trial is judged over the 5 s after the fault clears (the documented default window: first-swing
stability, ...); an undamped system can lose synchronism on a later swing, so a longer window can give a shorter CCT.`
The undamped classical swing keeps every swing it starts, so the number depends on the window: WSCC-9 bus 7 at 60 Hz is
550 ms with the 5 s window and 436 ms with a 20 s window (the Core API takes the window as an optional argument; the CLI
uses the default). The stable pockets case9 shows at 60 Hz (buses 1, 2, 5, 7 and 9) are the same multi-swing effect.

Exit 3 on a non-converged base solve; exit 2 on a zero-inertia case, an unknown case name, a `--fault-bus` that is
not in the case (`error: stability: fault bus <id> is not in case '<name>'.`, checked before any solve), a negative or
non-finite `--clear` or one above 1000 ms, `--export` with `--rms` or `--screen`, `--fault-bus` or `--clear` with `--screen`, a positional case that
disagrees with `--dir`, or an RMS study error (including a governor or exciter whose limits cannot hold the solved
operating point); exit 1 for a case file or `--dir` folder that is missing or cannot be loaded, and for a `--dyn-data`
file that is missing or invalid. A STABLE or UNSTABLE verdict does not change the exit code.

### modal

`gridsim modal [<case>] [--dir <path>] [--damping D] [--enable-inverters] [--include-soc] [--json] [--export <json>] [--dyn-data <file>]`

Small-signal (modal) stability of the linearised multi-machine swing: eigenvalues, frequency and damping ratio of every
mode, the least-damped oscillatory mode, right-half-plane count, the dominant participating states per mode and an
ASCII s-plane. Default case `case9`; the X'd map is the same as `stability` (Anderson-Fouad for case9, else a
synthetic 0.30 pu, flagged INDICATIVE).

| Flag | Default | Meaning |
|---|---|---|
| `<case>` / `--dir` | `case9` | [Case argument](#case-arguments) or JSON case directory; both together exit 2 unless they name the same folder. |
| `--damping` | 0 (undamped) | Machine damping D applied to every machine (MW per rad/s). |
| `--enable-inverters` | off | Add the inverter states of the case's bound converter classes (a documented archetype). A grid-following class's frequency estimator takes the class's `pllBandwidthHz` / `pllDampingRatio` when authored; otherwise its detection latency as a critically damped delay stand-in (every bundled class); otherwise the one PLL default, 20 Hz at 0.707 (R138, October 2026; it was 5 Hz, critically damped). |
| `--include-soc` | - | Not implemented: exits 2 (a state-of-charge integrator is a decoupled origin pole, left out of the stability matrix by design). |
| `--json` | off | Print the model and modes as JSON on the console instead of the tables. Takes no value: unlike the shared writer's `--json <file.json>` ([Result files](#result-files)), it writes no file; `--export <file.json>` does. `--json <name>.json` exits 2 and points at `--export`. |
| `--export` | - | Write the small-signal model and modes to a JSON file (`gridsim-smallsignal/1`). The file also carries `participationAvailable` (false when the state matrix is defective: the `participation` rows are then all-zero placeholders, not participation), `participationUnavailableReason` (only when false) and `exitCode` (the exit code the run ends with: 1 for a right-half-plane eigenvalue, else 0). `--json` prints the same fields. |
| `--dyn-data` | - | A machine dynamic data file (`gridsim-dynamics/2`, the file [`stability --rms`](#stability) reads). The classical small-signal model is built from three numbers per machine, X'd, H and the damping D, and each machine the file binds takes all three from it (X'd on the machine base, as the built-in map; D per unit on the system base, the same quantity as the RMS study's; H the case's own where the template gives none); `--damping` and the X'd map fill the other machines. The header says how many X'd values came from the file and adds a `machine data:` line (the file's subtransient data, exciters and governors are not part of this model) with the loader's notes; the result stays INDICATIVE unless every machine is bound and every value is tagged `Recorded` or `DerivedFromRecorded`. A file can give a MATPOWER case the inertia it lacks. A missing or invalid file exits 1, before any solve. |

Exit 1 if any eigenvalue lies in the right half-plane (small-signal unstable), for a case file or `--dir` folder
that is missing or cannot be loaded, or for a `--dyn-data` file that is missing or invalid; exit 3 on a non-converged base solve; exit 2 on an unknown case name, a positional
case that disagrees with `--dir`, or a model that cannot be assembled. An unstable result is still a
complete analysis, so the export is written and says `exitCode: 1`. When the state matrix is defective (for example
`data/gb-synthetic --enable-inverters`) the eigenvalues are reported but participation factors are undefined: stderr
says why, the participation table prints `unavailable`, and the export says `participationAvailable: false`.

A MATPOWER case (the GB ones `gb-reduced-29`, `gb-network-2224`, `pypsa-gb-reduced`, and the IEEE cases other than
`case9` / `ieee9`) exits 2: a `.m` file carries no inertia data, so every unit loads with H = 0 and the swing model has
no rotor. The parser's "nominal frequency not given ... assumed 50 Hz" note printed above it is a separate assumption
(right for GB) and not the cause; the error message says so. Use a JSON case with `inertiaH` per machine (`gb`,
`gb-reduced`, `gb-synthetic`, `uk`, `gb-full`). `gb-full` (952 machines, 1,902 reduced states) is far beyond the dense
eigenvector path's size: participation needs one complex dense LU per eigenvalue (1,902 of them), so a participation run
takes hours.

### fit-frequency

`gridsim fit-frequency <trace.csv> --inertia <GVA.s> --demand <MW> --loss <MW> [--loss-at <s>] [--fit damping,responsive,governor] [--max-iter N] [--nominal-hz <Hz>]`

Fit the lumped swing model's nadir and recovery constants to one measured loss-of-infeed trace (R079, October 2026):
load self-damping (`DampingPctPerHz`, % of demand per Hz), the responsive headroom fraction and the governor time
constant, by least squares between the trace and the model's trajectory for the given loss, inertia and demand
(`FrequencyConstantsFitter`, Nelder-Mead). The report shows each constant's seed (the shipped default), its result and
whether it was fitted or held, then the RMS error over the samples, the nadir error (simulated minus measured) and the
number of model evaluations. It is a calibration check: the shipped defaults are not changed, and adopting a fitted value
is a separate, deliberate step. The initial RoCoF depends only on the inertia and the loss, so it carries no information
about these constants; the nadir depth and the recovery shape do.

The trace is a CSV of two numeric columns, `seconds,Hz`: the time in seconds on the model's clock (0 s is the start of
the window) and the measured frequency. The first non-blank line may be a header; blank lines are skipped. Every other
line must have exactly two fields, a finite time of 0 s or more that increases on the row before, and a finite frequency
less than 5 Hz from the trace's nominal frequency; at least 3 rows. A bad row exits 1 naming the line number and the line.
The time is not a timestamp: give the loss instant on the same clock with `--loss-at`.

The nominal frequency (N314, October 2026): the swing model runs at the trace's own nominal. Without `--nominal-hz` the
trace names it: its first sample must lie less than 5 Hz from 50 Hz or from 60 Hz (the two standard system frequencies,
10 Hz apart, so never both), and that nominal is used for every row; a first sample near neither exits 1 and asks for
`--nominal-hz`. With `--nominal-hz <Hz>` (finite, above 5 Hz) that nominal is used instead, for a system on another
frequency. The `Event:` line prints the nominal used. A 50 Hz trace is fitted exactly as before.

The model starts at the trace's pre-event frequency: the last sample before `--loss-at`, in equilibrium there (the
replay's rule; services respond to the absolute frequency). A trace with no sample before the loss starts at the nominal. The
`Start:` line says which.

| Flag | Default | Meaning |
|---|---|---|
| `<trace.csv>` | - | The measured trace (required). |
| `--inertia` | - | System inertia at the event, GVA.s, above 0 (required). |
| `--demand` | - | System demand at the event, MW, above 0 (required). |
| `--loss` | - | Infeed lost, MW, above 0 (required). |
| `--loss-at` | 1.0 | The loss instant on the trace's clock, seconds, 0 or more, before the last sample. |
| `--fit` | `damping` | Which constants to fit: a comma list of `damping`, `responsive`, `governor`, each at most once. Damping alone is well identified from one trace; fitting all three from one trace is weakly identified (they trade off), and a `Note:` line says so. |
| `--max-iter` | 2000 | The optimiser's iteration cap, at least 1. |
| `--nominal-hz` | from the trace (50 or 60) | The trace's nominal frequency, Hz, finite and above 5 Hz. Every sample must lie less than 5 Hz from it. |

Exit 0 for a converged fit; exit 1 for a trace file that is missing or invalid (including a first sample near neither
50 nor 60 Hz without `--nominal-hz`); exit 2 for a missing required flag, a value that is not a number above 0, a
`--nominal-hz` that is not finite and above 5 Hz, a negative `--loss-at` or one at or after the last sample, an unknown or repeated
`--fit` word, no trace argument or a second one, and the global `--forming`, `--asset-dynamics` and
`--inertia-model distributed` switches (the fitter runs the plain lumped swing, so they would be ignored); exit 3 when
the optimiser does not converge within `--max-iter` (the last point is printed for diagnosis, with a `NOT CONVERGED`
line on stderr, and is not a fit).

### inertia-placement

`gridsim inertia-placement [<case>] [--placements fleet,demand,spine-t,south,optimised] [--budgets 120,180,240,360,480,700,900,1200] [--losses sizewell,hinkley,kent,scotland] [--residual 0.25] [--condenser-h 8] [--rocof-limits 0.125,0.25,0.5,1] [--post-event 6] [--avr none|sexs|ieeet1] [--optimise-budget 240] [--optimise-passes 1]`

The inertia placement study: with a network's synchronous inertia in better places, can it run on less of it? Default
case `gb-full`. Today's synchronous fleet keeps `--residual` of its inertia (each machine's H scaled; dispatch and
locations unchanged); the rest of each budget is synchronous condensers of inertia constant `--condenser-h`, at no
power and no reactive power at the solved state (so the power flow is the case's), placed by each placement:

- `fleet`: where today's synchronous inertia is, in proportion to it;
- `demand`: in proportion to demand, each bus's demand gathered to its nearest 275/400 kV bus;
- `spine-t`: equal shares at eleven sites forming an inverted T / ladder (Inverness, the central belt, the Borders, the
  east and west England corridors, the Midlands, Cornwall, the South West, the Thames, Kent and East Anglia), each on its
  nearest 275/400 kV bus;
- `south`: everything at the Thames site, a deliberately concentrated contrast;
- `optimised`: the same sites with weights from a coordinate search at `--optimise-budget` (each pass tries each site's
  weight doubled and halved and keeps the change that lowers the worst bus RoCoF over every loss most; up to
  `--optimise-passes` passes, stopping early when a pass changes nothing). A local search, not a proven optimum.

Each loss is a 1.8 GW step (the SQSS infeed loss; constant admittance at the bus's solved voltage, the RMS engine's load
step) at the 275/400 kV bus nearest Sizewell, Hinkley Point, Sellindge (the Kent interconnectors) or Torness (Scotland
over B6), at t = 1 s, run on the RMS engine (`stability --rms`'s synthetic GENROU machines with `--avr` exciters on every
machine and condenser, mechanical power held: no frequency response) for `--post-event` seconds after it on a 5 ms step. Every bus at 132 kV and above is
measured: its frequency from its voltage angle over one nominal cycle, its RoCoF over a 500 ms window (the GB
loss-of-mains window) with the voltage angle jump at the loss taken out and reported on its own.

Prints, for each run, the centre-of-inertia RoCoF and the worst bus RoCoF of the RMS run and K, their ratio (the
spatial penalty); the loss the machines felt (`2 E RoCoF / f0`: not exactly 1.8 GW, because the RMS loads are constant
admittances and the converter plant holds its current, so both change their power as the voltage moves); the full-loss
worst bus RoCoF (K times the swing-equation figure `f0 x 1.8 GW / 2E`); the worst angle jump; the north-south
inter-area oscillation (damping ratio and frequency, fitted over every half-cycle of the difference of the north and
south machines' mean speeds, smoothed over 0.4 s to average out local swings; `NaN` with fewer than four half-cycles);
and the time the RMS run's centre-of-inertia frequency took to fall to
49.8 Hz. Then, per placement, the smallest scanned budget meeting criterion A (the full-loss worst bus RoCoF at or below
each of `--rocof-limits`) and criterion B (at least 3 s and 5 s to 49.8 Hz for the full loss with no response, from the
swing equation, so the same for every placement), with the linear estimate between the budget that fails and the one
that passes; and the time to 49.8 Hz with the case's frequency response from the lumped model (the default GB response,
which placement does not change). Each run is a full RMS run of the network (about 10 s each on `gb-full`), so the
default study takes a long time; it prints one line per run as it goes.

The results of the study on `gb-full`, with its method, sensitivity runs and limits, are in
[`docs/validation/studies/inertia-placement.md`](<../validation/studies/inertia-placement.md>).

| Flag | Default | Meaning |
|---|---|---|
| `--placements` | all five | A comma list of `fleet`, `demand`, `spine-t`, `south`, `optimised`, each at most once. |
| `--budgets` | 120,180,240,360,480,700,900,1200 | The total synchronous inertia budgets (GVA.s), ascending; each must exceed the residual. |
| `--losses` | all four | A comma list of `sizewell`, `hinkley`, `kent`, `scotland`, each at most once. |
| `--residual` | 0.25 | The share of today's fleet inertia kept on the fleet, above 0 and at most 1. |
| `--condenser-h` | 8 | The added condensers' inertia constant (s, on their own rating), above 0. |
| `--rocof-limits` | 0.125,0.25,0.5,1 | Criterion A's limits on the worst bus RoCoF (Hz/s), each above 0; one minimum per limit. |
| `--post-event` | 6 | Simulated seconds after the loss, 1 to 60. |
| `--avr` | `ieeet1` | The exciter on every synchronous machine and condenser: `ieeet1`, `sexs` or `none` (field held); the typical, indicative settings of `stability --rms --avr`. |
| `--optimise-budget` | 240 | The budget (GVA.s) the `optimised` placement's weights are searched at. |
| `--optimise-passes` | 1 | Passes of the coordinate search, at least 1. |

Exit 0 when the study ran; exit 1 for a case it cannot study (no 275/400 kV bus with a map position, a budget at or below
the residual, a power flow that does not converge; the reason is printed); exit 2 for a malformed flag.

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary and the tables `runs` (one row per placement, budget and loss),
`minimums`, `lumped`, `sites` and `placements` (every bus share).

## Harmonics, impedance and EMT verbs

These verbs are used in [ch. 17](<0017 Harmonic studies.md>) (`harmonics`),
[ch. 15](<0015 RMS, modal and impedance stability.md>) (`impedance-stability`) and
[ch. 16](<0016 Electromagnetic transients (EMT).md>) (`emt`).

### harmonics

`gridsim harmonics [<case>] [--dir <path>] [--spectra <file.json> | --synth-sources | --converter-sources rated|dispatched] [--synth-spectrum g5|ideal] [--afe-reactance <pu> [--afe-resistance <pu>]] [--bus <id>] [--enableExport[=path]]`

Frequency-domain harmonic study on the study model (network, machine source impedance, loads as damping shunts):
penetration (Y(h) V = I), the driving-point resonance scan, a fine frequency sweep for the true peak between integer
orders, voltage-THD indices (relative to each bus's solved fundamental voltage, from the same solve as `--once`; the
Web HARMONICS tab uses the same normalisation) and an IEEE-519 compliance verdict. A bus with no base kV is judged at
the strictest tier, never certified compliant, and counted in a printed warning. See
[ch. 17, Harmonic studies](<0017 Harmonic studies.md>).

| Flag | Default | Meaning |
|---|---|---|
| `<case>` / `--dir` | `case9` | [Case argument](#case-arguments) or JSON case directory (`-d` is a short alias of `--dir`). |
| `--spectra` | - | A `gridsim-harmonics/1` source set. A non-zero component at an order outside the analysed odd orders 3 to 49 is refused (exit 1, each named); a triplen source is refused (exit 1). A set holding any measured source (provenance `Recorded` or `DerivedFromRecorded`) must carry `takenUtc`; an undated one, or any set dated inside the 169 h horizon, is refused with `REJECTED (forward inference): ...` and exit 2. An all-assumed library set (equipment spectra) may be undated. |
| `--synth-sources` | - | Select the synthetic default explicitly: one six-pulse source (0.5 pu fundamental) at the largest-demand bus (also the default without `--spectra`). Given together with `--spectra`, exits 2. |
| `--synth-spectrum` | `g5` | The spectrum of that synthetic source: `g5`, the cited ENA EREC G5 Issue 5 Table B.3 six-pulse profile (`6-pulse-g5`: 5th 31.4 %, 7th 10.9 %, 11th 7.0 %, ... to the 49th, THDI 34.4 %), or `ideal`, the idealised 1/h six-pulse spectrum (orders 5 to 25, 5th 20 %, 7th 14.3 %, THDI 29.0 %). The output names the spectrum used (`synthetic source : ...`). Any other value exits 2; with `--spectra` or `--converter-sources` (which bring their own spectra) it exits 2. |
| `--converter-sources` | - | `rated` or `dispatched`: one source per in-service converter-interfaced unit (battery, PV, wind, tidal, HVDC / interconnector; not a power-flow-only device), with the ENA EREC G5 Issue 5 Table B.3 profile for its technology: active front end (`afe-g5`) for a self-commutated converter, twelve-pulse (`12-pulse-g5`, `[VERIFY]`) for a unit marked as feeding no fault current (a line-commutated HVDC link). `rated` scales each profile by the unit's rated current (`MBase / BaseMva` pu, the G5 Stage 1C / 2A basis); `dispatched` by its output at the solved operating point, `sqrt(P^2 + Q^2) / BaseMva` (the fundamental power flow is solved first, so the slack's P and every PV unit's Q are the solved values, not the case's Pg / Qg; a fundamental that does not converge exits 3; a unit at zero output is listed as not modelled). Sources add linearly (G5 s.6.3.3: no declared diversity). The study then analyses orders 2 to 50 (Table B.3's range); the triplen components (zero sequence) are left out of the balanced study, and their count and the first one are printed. Any other value exits 2; with `--spectra` or `--synth-sources`, exits 2; a case with no converter unit that injects, or a unit with a non-positive rating (`rated`) or non-finite output (`dispatched`), exits 1. |
| `--afe-reactance` | - | N149: model every active-front-end converter (the self-commutated units, the `afe-g5` class; line-commutated units are untouched) as resonant plant, ENA EREC G5 Issue 5 s.3.29 p.17 ("may be classified as resonant plant due to their internal impedance"): a shunt of R + jhX to ground at its bus, the reactance X given here in pu on the unit's own rating (`MBase`), converted to the system base. With a converter source (`--converter-sources`) the unit becomes a Norton equivalent, its source in parallel with this impedance; with the other source choices the units are resonant plant only. No value is given by any document on disk (the converter's own coupling reactor or filter, `[VERIFY]`), so there is no default: without the flag converters stay ideal current sources and the study is unchanged. The output adds an `AFE impedance` line (unit count and the impedance). Not finite or not above 0 exits 2; a case with no active-front-end unit, or such a unit with a non-positive rating, exits 1. |
| `--afe-resistance` | `0` | N149: the resistance R in series with `--afe-reactance` (pu on the unit's rating, frequency-independent); 0 is the conservative choice (no converter damping). Negative or not finite exits 2; without `--afe-reactance`, exits 2. |
| `--bus` | worst-THD bus | Bus for the resonance scan and fine sweep; a non-integer value or a bus not in the case exits 2. |
| `--enableExport[=path]` | `results/harmonic-export.json` | Write the `gridsim-harmonic-export/1` document. |

Exit 2 on an unknown case name, a missing or unreadable `--spectra` file, an unknown `--bus`, a bad or contradictory
source choice, or a forward-inference rejection; exit 1 for a missing case file or folder, a spectra component at an
unanalysed order, a triplen source, no injecting converter unit under `--converter-sources`, no active-front-end unit
(or one with no usable rating) under `--afe-reactance`, or a singular Y(h) (no THD
and no verdict are reported); exit 3 when the fundamental power flow does not converge.

### impedance-stability

`gridsim impedance-stability [<case>] [--dir <path>] [--bus <id>] [--converter gfl|gfm] [--criterion siso|mimo] [--grid canonical|real] [flags]`

Impedance-based (generalised Nyquist) inverter-grid stability screen: forms the loop gain of the grid impedance and the
converter admittance over frequency (SISO: the positive- and negative-sequence loops in the converter's dq frame, with the
grid read at f + f0 and f - f0; MIMO: the 2x2 dq matrices), counts encirclements of (-1, 0), reports gain and phase
margins, and sweeps SCR for the weak-grid instability boundary.
The converter operating point is the chosen unit's solved output (the bus's solved generation, split between co-located
units by scheduled P and by reactive range), not its scheduled Pg / Qg, at the solved bus voltage.
Cross-checks against the right-half-plane eigenvalue count of the case's small-signal model where one can be built:
that swing model uses the WSCC-9 Anderson-Fouad X'd for `case9` (as `stability` and `modal` do), else the synthetic
0.30 pu, and zero damping (no bundled case carries damping data); the cross-check line names its basis in brackets.
The `SCR boundary` line reports what the sweep found: the critical SCR at an unstable-to-stable crossing; "stable
across the swept SCR range" only when every swept SCR is stable; "UNSTABLE at every swept SCR" when none is; the count of
unstable SCRs when the sweep has no crossing; and `n/a` with `--grid real`, where no sweep runs.
The converter model comes from the case's bound asset class when there is one, otherwise from the flags (tagged
`CuratedPlaceholder` `[VERIFY]`). See [ch. 15](<0015 RMS, modal and impedance stability.md>).

| Flag | Default | Meaning |
|---|---|---|
| `<case>` / `--dir` | `case9` | [Case argument](#case-arguments) or JSON case directory (`-d` is a short alias of `--dir`). |
| `--bus` | largest generator's bus | Generator bus to attach the converter model to. |
| `--converter` | `gfl` | `gfl` (grid-following) or `gfm` (grid-forming); any other value exits 2. The grid-forming model's power loop is per unit on the unit's rating (MBase): a bound class's damping D (MW/Hz) and synthetic inertia J (MW.s/Hz) give the droop S / (D f0) and the lag J / D (R140, October 2026; they used to be scaled as 0.01 D pu and 1e-4 J s whatever the unit's size). The flag-built grid-forming model (no damping, no synthetic inertia) is unchanged; its power-loop bandwidth is `--pll-bw` when given, else the grid-forming placeholder 5 Hz. |
| `--criterion` | `siso` | `siso` or `mimo` (the generalised criterion); any other value exits 2. |
| `--fmin` / `--fmax` | 0.1 / 250 | Frequency range (Hz). |
| `--ppd` | 50 | Points per decade. |
| `--grid` | `canonical` | Grid impedance: `canonical` (the exact single-inverter-vs-Thevenin model, swept over SCR) or `real` (the network's own harmonic driving-point impedance at the converter bus; fails with exit 3 if that solve is singular). Any other value exits 2. |
| `--scr` | 3.0 | Short-circuit ratio of the Nyquist scan (`--grid canonical` only; with `--grid real` the SCR flags exit 2). |
| `--scr-min` / `--scr-max` / `--scr-steps` | 1 / 10 / 37 | SCR sweep for the boundary search. |
| `--pll-bw` | 20 Hz | PLL bandwidth (flag-built model, or an override of a grid-following asset class). The value must be a finite number above 0: 0, a negative value, `NaN` or `Infinity` exits 2 (N266, October 2026; it used to be floored silently by the flag-built model and crash a bound asset class). With a grid-forming asset class it exits 2 (the class sets its own power-loop bandwidth); with the flag-built grid-forming model it sets the power-loop bandwidth (default 5 Hz). The default is the one grid-following PLL default the EMT and small-signal models also use (R138, October 2026; it was 5 Hz here, 20 Hz in EMT): a faster PLL is the destabilising direction on a weak grid, so an unauthored device is screened on the conservative side. A grid-following asset class without `pllBandwidthHz` takes the same 20 Hz. |
| `--pll-damping` | 0.707 | PLL damping ratio of the grid-following model: the flag-built model's value, or an override of a bound grid-following asset class's authored damping (without the flag the class's own damping is used, and any override tags the model `CuratedPlaceholder`, not `Recorded`). The value must be a finite number above 0 (0 or below, `NaN` or `Infinity` exits 2; N266). With `--converter gfm` (bound class or not) it exits 2: the grid-forming model takes no PLL damping ratio. |
| `--current-bw` | - (no default) | Current-loop bandwidth (Hz); setting it adds the current loop to the model detail (`PllCurrentOuter`). There is no default value: without the flag the model has no current-loop bandwidth (the `PllOuter` level and its fast stand-in roll-off), and the flag without a value exits 2. The value must be a finite number above 0 (0 or below exits 2). Grid-following only; with `--converter gfm` it exits 2. |
| `--outer-loop-only` | off | The outer-loop-only detail level (`ImpedanceModelDetail.OuterLoopOnly`): the grid-following PLL is ideal (gain frozen at 1/Vd0) and the current loop is the fast stand-in roll-off, a first screen when no inner-loop data exist. With `--pll-bw`, `--pll-damping` or `--current-bw` (which it would ignore), or with `--converter gfm` (whose admittance does not use the detail level), it exits 2. |
| `--enableExport[=path]` | `results/impedance-stability-export.json` | Write the scan to JSON. |

Exit 3 on a non-converged power flow; exit 2 on an unknown case name, a second case argument, a positional case and a
different `--dir`, or no generator at the chosen bus; exit 1 for a missing or unloadable file or folder. A completed
scan exits 0 whether the verdict is STABLE or UNSTABLE. The case may sit anywhere on the line and may be a built-in
name, a `.m` file or a case folder.

### emt

`gridsim emt [<case>] [--dir <path>] [--dt-us 20] [--duration 0.1] [--source <bus>] [--amp 1.0] [--freq 0] [--probe <list>] [--event <spec> ...] [--rule trap|be] [--no-cda] [--lines] [--no-loads] [--allow-non-passive] [--out <dir>]`

Electromagnetic-transient time-domain solve: a Dommel nodal companion-model engine steps the instantaneous network at a
fixed microsecond timestep. The output is a forward model trajectory. Writes `timeseries.csv`, `summary.md` and
`run-metadata.json`. See [ch. 16, Electromagnetic transients](<0016 Electromagnetic transients (EMT).md>).

| Flag | Default | Meaning |
|---|---|---|
| `<case>` / `--dir` | `data/uk` | [Case argument](#case-arguments) or JSON case directory. |
| `--dt-us` | 20 | Timestep (microseconds); must be positive (exit 2). |
| `--duration` | 0.1 | Simulated time (s). Must be positive and at least half of one `--dt-us` step (the run takes `round(duration / dt)` steps, at least one); anything else exits 2. |
| `--source` | first generator's bus | Source bus. |
| `--amp` | 1.0 | Source amplitude (pu). |
| `--freq` | 0 | Source frequency (Hz); 0 is DC. A negative value exits 2. |
| `--probe` | source-bus voltage + first series branch current | Comma list of `v:<bus>` and `i:<from>-<to>` (or `i:<from>-><to>`, in the case's from -> to direction). Every token must name a bus or an in-service series branch of the case; a token that does not (an unknown bus, a non-numeric bus, no such branch, or neither `v:` nor `i:`) exits 2 and nothing runs. Where several in-service series branches join the same two buses (parallel circuits, such as 49-54 in case118), `i:<from>-<to>` is ambiguous and exits 2 naming how many there are; `i:<from>-<to>#<k>` picks the k-th of them in case order (N241; it used to read the first without a word). The default branch probe is labelled `#1` when its branch has a parallel twin. |
| `--event` | - | Repeatable: `fault:<bus>@<t>:<Rpu>`, `clear:<bus>@<t>`, `switch:<id>@<t>:close\|open`. See the event rules below. |
| `--rule` | `trap` | `trap` (trapezoidal) or `be` (backward Euler); any other value exits 2. |
| `--no-cda` | off | Disable Critical Damping Adjustment (backward-Euler half-steps at switching events that remove the trapezoidal rule's spurious oscillation). CDA runs only under `--rule trap`; under `--rule be` it is off whatever this flag says. |
| `--lines` | off | Model charged, non-transformer branches as lossless Bergeron travelling-wave lines. |
| `--no-loads` | off | Leave out the constant-impedance load shunts (by default each bus's demand is one). |
| `--allow-non-passive` | off | Run a network that contains a non-passive element: a branch with a negative series resistance or a bus shunt with a negative conductance. Network-reduction equivalents carry these on purpose (IEEE 145 has 232, pglib case588 has 5). They generate energy, so the run may grow without bound. Without the flag such a case exits 2, naming the first ten elements; with it, stderr gives a warning with the count. |
| `--out` | `results/emt-out` | Output directory. |

Event rules. Each event time must be a number of seconds from 0 up to the end of the run. An event between 0 and one
step is applied at the first step; a negative time, or a time after the last step, exits 2. A
fault resistance must be a positive number of per-unit ohms: 0 and negative values exit 2 (a bolted fault is a small
resistance, for example `1e-4`). Each fault bus and resistance pair gets its own fault switch. A later `fault:` at a bus
that is already faulted changes that bus's fault to the new resistance: the bus's other fault switches open at that
instant and the new one closes. `clear:<bus>` opens every fault switch at the bus and needs a `fault:` at that bus
somewhere on the command line. Two `fault:` events at one bus at the same instant exit 2. The `switch:` action must be
`close` or `open` (any letter case); any other word exits 2. Every problem on the command line is listed before the
command exits.

`summary.md` reports `CDA: on`, `CDA: off (--no-cda)` or `CDA: off (not used under backward Euler)`. Its
`refactorizations` count is 1, plus 2 for every event that changes a switch when CDA is on, or plus 1 when CDA is off.
`run-metadata.json` holds the shared run fields and a `parameters` object. That object records the case, `dt`, the
duration and step count, the source, the rule, whether CDA ran, the `--no-cda`, `--lines`, `--no-loads` and
`--allow-non-passive` switches, every probe (name, kind and target), and every event. For each event it keeps the token
as given and what it became: the bus, the time, the resistance and the fault switch id.

Exit 1 on a case file or folder that cannot be loaded; exit 2 on an unknown case name, a network that cannot be built
(including a non-passive one without `--allow-non-passive`), a malformed or out-of-range `--event`, a probe that names
nothing, or a `--duration` that gives no step; exit 3 on an engine failure or a non-finite value in the trajectory.

## Evidence verbs

All five share the event catalogue (`--catalog <f.json>`, schema `gridsim-event-catalog/1`; built-in events
`gb-2019-08-09`, `gb-2023-12-22` and `gb-largest-loss-1320`) and the forward-inference guard: any event or trace
timestamp newer than 169 h ago is rejected with exit 2. A catalogue whose `schema` is not exactly
`gridsim-event-catalog/1` exits 1. Every recorded action in a catalogue states its `kind`: `DemandDisconnection`
(automatic LFDD, never replayed in a counterfactual) or `OperatorInstruction` (for example the balancing-mechanism
recovery, kept in every counterfactual because it replaces the lost energy whatever the nadir); an action without one
exits 1, naming the event and the action.

All five take `--trace <neso.csv>`, which attaches a 1-second NESO trace to the credibility gate they all run, so the
gate scores the per-sample RMSE and the back-test (nadir within 0.05 Hz, RoCoF within 0.05 Hz/s on the trace's own 1 s
basis, nadir time within 2.0 s) exactly as `replay-event --trace` does; an RMSE above 0.05 Hz or a failed back-test
caps `Reproduced` at `Marginal`. A trace file that does not exist exits 2, a trace row newer than the horizon exits 2
(`REJECTED (forward inference): ...`), and a trace line that is not a valid `timestamp,Hz` row exits 1. See [ch. 18](<0018 Replaying recorded events.md>)
(`replay-event`) and [ch. 20, Counterfactuals](<0020 Counterfactuals.md>) (`scenario`, `h0-study`, `fleet`,
`evidence`). A missing event id prints the usage and the built-in event ids and exits 2; an event id the
catalogue does not hold exits 1.

Each of the five accepts only the flags in its own section: a flag that belongs to another verb (for example
`fleet --device-ffr`, `evidence --corners`, `replay-event --device-ffr`) exits 2 with `error: <verb> does not use
<flag> ... <verb> accepts: ...`. `--asset-dynamics` is refused by all five, and `--forming`, `--forming-inertia` and
`--forming-damping` by all but `scenario` and `fleet`. `scenario` and `evidence` refuse `--device` together with `--device-inertia`
or `--device-ffr` (exit 2).

### replay-event

`gridsim replay-event <id> [--catalog <f.json>] [--trace <neso.csv>] [--out <dir>] [--dump-catalog <path>]`

The credibility gate: reproduce a recorded event's RoCoF and nadir with no device. Writes `baseline-frequency.json`,
`summary.md` and `run-metadata.json` to `results/replay-<id>` by default. `--trace` attaches a real 1-second NESO CSV
to enable the per-sample RMSE check, printed with the number of samples scored; a trace file that does not exist exits
2, and so does a trace with a row newer than the 169 h forward-inference horizon (`REJECTED (forward inference): ...`,
before any output is written); a trace that ends before the scored window is scored only where it reaches, with a `WARNING` line, and a trace that
does not contain the event instant gives `n/a` and a `WARNING`. With a trace that covers the window a `back-test vs
trace` line gives the back-test (PASS or FAIL, the nadir error, the RoCoF on the trace's 1 s basis, the nadir times).
The `inferred inertia from recorded RoCoF` line inverts the swing equation; for a windowed record (gb-2023-12-22's 1 s
figure) the recorded RoCoF is first scaled to an instantaneous slope by the replay's own instantaneous / windowed ratio,
stated on the next line. `--dump-catalog` writes the built-in catalogue to disk and exits. Exit 3 for any verdict
short of `Reproduced` (a `Marginal` baseline unlocks nothing).

### scenario

`gridsim scenario --event <id> [flags]`

The counterfactual sweep (inertia x device x corners) plus the inertia risk curve. Refuses to run (exit 3, `GATE
FAILED`) unless the no-device baseline reproduces the record. Writes `matrix.csv`, `risk-curve.csv`,
`counterfactual-frequency.json`, `summary.md` to `results/scenario-<id>`.

| Flag | Default | Meaning |
|---|---|---|
| `--event` | required | Event id (exit 2 if missing). |
| `--inertia` | `260,155,120,102,50` | GVA.s levels to sweep (the GB trajectory). |
| `--device <f.json>` | - | Device from a JSON file. |
| `--device-inertia` / `--device-ffr` | 30.0 GVA.s / 1.0 GW | Build the device from flags instead (a `[VERIFY]` placeholder TSGB). |
| `--corners` | `best,worst` | Comma list of `best`, `nominal`, `worst`. |
| `--trace` | - | 1-second NESO trace for the gate (see the notes above). |
| `--catalog` / `--out` | built-ins / `results/scenario-<id>` | Catalogue file / output directory. |

The global `--forming`, `--forming-inertia` and `--forming-damping` switches carry a virtual former into every cell, so
a low-inertia counterfactual is formed rather than floored. A `--device` file that does not load exits 2.

### h0-study

`gridsim h0-study --event <id> [flags]`

The H=0 counterfactual: scale the real synchronous inertia toward zero while making a growing fraction of the converter
fleet grid-forming, per asset (fleet-routed). Gated like `scenario` (exit 3 unless the baseline reproduces). Prints a
scale x GFM-fraction table per corner (`HELD`, `LFDD`, `CASC` for a Loss-of-Mains cascade, `NaN` for a
forming-dependent cell with no grid-forming source); forming-dependent cells are data and the run still exits 0.
Writes `h0-study.json`, `matrix.csv`, `summary.md` and `run-metadata.json` to `results/h0-study-<id>`.

The header line `dP=<MW> MW (first 5 s; <MW> MW over the whole event)` gives the loss that sets the initial RoCoF (the
loss steps in the first 5 s, the same window the replay's inertia cross-check uses) and the whole staged total (R287,
October 2026: `dP` and each cell's analytic `bare_inertia_rocof` used the whole total; `gb-2019-08-09`: 1,521 MW, not
2,768 MW). `summary.md` also gives the largest single loss step. Converter units are made grid-forming whole, largest
first, so a fraction can be overshot by up to one unit's MW (R289): `matrix.csv` carries `realised_gfm_fraction` after
`gfm_fraction`, `h0-study.json` carries `realisedGfmFraction` per cell (and `system.totalExogenousLossMw`,
`rocofSettingLossMw`, `rocofSettingWindowS`, `largestSingleLossMw`; the old `largestLossMw` key keeps the total), and
the console prints `GFM fraction realised by whole units: <requested> -> <realised>` when they differ.

| Flag | Default | Meaning |
|---|---|---|
| `--event` | required | Event id (exit 2 if missing). |
| `--dir` | `data/gb-synthetic` | Case directory. It must ship `asset-classes.json` and `asset-bindings.json` (exit 1 otherwise). |
| `--sync-inertia-scale` | `1,0.5,0.25,0` | Synchronous-inertia scale factors. |
| `--gfm-fraction` | `0,0.25,0.5,0.75,1` | Fractions of the converter fleet made grid-forming. |
| `--gfm-class` / `--gfl-class` | `gfm-vsm-study-2024` / `gfl-wind-2024` | Asset classes for the grid-forming and grid-following units. |
| `--corners` | `best,worst` | Comma list of `best`, `nominal`, `worst`. |
| `--trace` | - | 1-second NESO trace for the gate (see the notes above). |
| `--catalog` / `--out` | built-ins / `results/h0-study-<id>` | Catalogue / output directory. |

### fleet

`gridsim fleet --event <id> [flags]`

Single-versus-fleet emergent behaviour: the same total device budget split across N staggered units. Intrinsic inertia
is N-additive; FFR is not (fewer MW online at the nadir). Gated like `scenario` (exit 3 unless the baseline
reproduces). Writes `fleet.csv` to `results/fleet-<id>`.

| Flag | Default | Meaning |
|---|---|---|
| `--event` | required | Event id. |
| `--units` | `1,100` | Unit counts to compare. |
| `--total-inertia` | 40.0 | Total device inertia budget (GVA.s). |
| `--total-ffr` | 1.5 | Total device FFR budget (GW). |
| `--stagger` | 10.0 | Fleet onset spread (s). |
| `--tau` | 0.5 | Per-unit FFR time constant (s). |
| `--deadband` | 0.015 | Per-unit frequency deadband (Hz). |
| `--inertia` | event pre-event inertia | System inertia (GVA.s). |
| `--trace` | - | 1-second NESO trace for the gate (see the notes above). |
| `--catalog` / `--out` | built-ins / `results/fleet-<id>` | Catalogue / output directory. |

The global `--forming`, `--forming-inertia` and `--forming-damping` switches (N291, October 2026; the fleet engines
refused grid-forming before) run every row with the same aggregate virtual former: `vsm` is a virtual synchronous
machine (virtual inertia plus damping), `droop` a P-f droop former; the units add real inertia and fast response on
top. A `grid-forming:` line under the header names the former, and `run-metadata.json` records it; without the switches
the output is unchanged.

The three lines under the table state what the first and last rows show, compared at the printed precision: whether
the RoCoF is the same (when it differs, the inertia total is the same in both rows, so the difference is the timing of
the fast response), whether the staggered fleet has fewer, the same or more MW online at the nadir, and whether its
nadir is deeper, the same or shallower.

### evidence

`gridsim evidence --event <id> [flags]`

The umbrella dossier (`gridsim-evidence/1`): recorded versus simulated versus counterfactual columns, risk curve, fleet
comparison and avoided cost. The counterfactual sections are withheld (and the exit is 3) unless the baseline
reproduces. Writes `evidence.json` and `summary.md` to `results/evidence-<id>`. The RoCoF in the simulated and
counterfactual columns is on the recorded figure's measurement basis (the 1 s windowed figure for gb-2023-12-22), the
same figure the gate scores. The fleet section's units carry the device's own response time constant and dead-band.

| Flag | Default | Meaning |
|---|---|---|
| `--event` | required | Event id. |
| `--device` / `--device-inertia` / `--device-ffr` | - / 30.0 / 1.0 | Device file or flag-built placeholder. |
| `--inertia` | `260,155,120,102,50` | Risk-curve inertia levels (GVA.s). |
| `--cf-inertia` | event pre-event inertia | Inertia for the headline counterfactual column (GVA.s). |
| `--units` | 100 | Fleet size for the fleet section. |
| `--stagger` | 10.0 | Fleet onset spread (s). |
| `--voll` | 6000.0 | Value of lost load, GBP/MWh (a curated placeholder, labelled as such in the output). |
| `--voll-source` | - | The reference the `--voll` figure is taken from: `S324` (Ofgem BSC P305 decision: 3000 or 6000 GBP/MWh), `S322` (London Economics 2013: 16940) or `S323` (DECC EMR annex C 2013: 16940), all in `docs/reference/voll`. Any other value, or a `--voll` the reference does not state, exits 2 naming what it does state. A valid source is printed (`VoLL source: ...`) but does not raise the VoLL tag, which stays `CuratedPlaceholder` (N359: a citation records where a policy figure is stated, it does not re-source it; before N359 it tagged the VoLL `Recorded`); the duration has no source and stays `CuratedPlaceholder`, and the avoided cost takes the weaker tag of the two (printed per input). |
| `--duration` | 0.5 | Assumed shed duration (h) for avoided cost (a `CuratedPlaceholder`). |
| `--trace` | - | 1-second NESO trace for the gate (see the notes above). |
| `--catalog` / `--out` | built-ins / `results/evidence-<id>` | Catalogue / output directory. |

## Data verbs

These verbs read, write and estimate from data. GDA verbs are in [ch. 22](<0022 Estimating GB history.md>) and
[ch. 24](<0024 The GDA bridge.md>); `estimate` and `estimate-noise-floor` are in
[ch. 21](<0021 State estimation.md>); the file formats are in [ch. 31](<0031 Exports and formats.md>).

### gda

`gridsim gda --root <path> [flags]`

Read the read-only GDA data lake and materialise a GridSim replica (data stays out of the repo). Without `--root` the
lake comes from the `GDA_DATA_ROOT` environment variable (the GDA estate's own name for it), else from `GDA_ROOT`; with
none of the three, usage prints and the verb exits 2. A variable that is set but does not name an existing folder is
refused with `GDA root error: the environment variable <name> is set but does not name an existing folder ...` and exit
1, naming the variable but never printing its value; a wrong `GDA_DATA_ROOT` is not replaced by `GDA_ROOT`.

| Flag | Default | Meaning |
|---|---|---|
| `--root` | `$GDA_DATA_ROOT`, else `$GDA_ROOT` | Path to the `/gda` lake (the folder holding `NationalGrid/`, `NESO/`). |
| `--dno` | `nged` | Source reader: `nged`, `ukpn` or `npg`. |
| `--licence` | - | Restrict to one licence area (for example `EMIDS`). |
| `--gsp` | - | Restrict to one GSP group. The name is checked first (case, spacing and `kV` spelling do not matter); an unknown name exits 2 with the nearest names. |
| `--all-gsps` | off | Build and solve every GSP supply area in turn. Spellings of one group are built once; names that select no live element are listed separately and not counted. Exit 3 if any area that builds fails to build or converge, or if none builds. |
| `--minkv` | 0 | Build only the network at or above N kV; demand below the cut (and its embedded generation) is folded onto the nearest boundary bus and reported in the notes. |
| `--base` | 100 | System base MVA. |
| `--neg-gen` | off | Embedded generation as negative load. |
| `--all-gen` | off | Admit every embedded-generation register row whatever its connection status (planning what-if). By default only connected plant enters the model; accepted, offered and unknown-status rows are left out and reported with their MW. |
| `--no-oltc` | off | Do not create OLTC controllers. |
| `--no-geo` | off | Skip geolocating buses from the substation table. |
| `--as-of <date>` | now - 169 h | Register as-of date (the no-forward-inference horizon; override for a specific replay date). ISO `yyyy-MM-dd[THH:mm[:ss]]`. |
| `--out` | - | Materialise the replica as a JSON case directory (lossless for every generator field a JSON v1 case carries; see [synth](#synth) for the rule). |
| `--solve` | off | Solve the built model (60 iterations, reactive limits off): the engine of the shared rule ([Solver selection](#solver-selection)); `--sparse` / `--dense` force the backend. |
| `--fleet` | off | National NESO fleet report instead (inertia, largest loss, RoCoF screen, ETYS boundary limits). |
| `--schema <path>` | - | Print a CSV/Parquet dataset's columns and row count, then exit. |

`--dno ukpn` reads each LTDS table (circuits, two-winding transformers, observed peak demand, generation) from its
published CSV under `UkPowerNetworks/RAWData`. When that CSV is missing or has only its header line (the full GDA lake
keeps the rows in its Parquet conversion), the table is read from the lake's Parquet copy under
`UkPowerNetworks/Parquet` instead (the same file name ending `.parquet`), cell for cell with the CSV reader's meaning,
and a `note:` line names each table read that way. A CSV that has rows always wins. A Parquet text column that the
conversion turned into numbers is refused (exit 1), because the original text cannot be recovered; numbers with more
than 15 significant digits are counted in a `note:` line, because the conversion's parser may differ from a CSV read in
their last binary place. With neither rows in the CSV nor a Parquet copy with rows, the build is refused (exit 1)
naming the tables and both places searched.

`--fleet` is checked before `--schema`, and `--all-gsps` replaces the single build. An unknown `--dno` or an
unparseable `--as-of` exits 2; a root, load or build failure exits 1; a carve that keeps no live element (0 buses)
exits 3 and writes nothing; `--solve` exits 3 when the solve does not converge.

### gda-replay

`gridsim gda-replay (--series <ts.json> | --synth) [flags]`

The historical state estimator: replay a demand time series through the validated solver, reconstructing grid state at
each past timestamp (horizon-guarded, never nowcasting). Writes `state-estimate.csv`, `exports/state-*.json` and
`run-metadata.json`. Exit 3 if any step fails to converge; exit 2 with neither `--series` nor `--synth` or on a
forward-inference rejection; exit 1 if the case or series does not load.

| Flag | Default | Meaning |
|---|---|---|
| `--series` | - | A GDA time-series JSON (real per-timestamp demand is data-gated). |
| `--synth` | - | Synthetic daily load shape instead. |
| `--start` | `2024-01-01` | Synthetic series start instant, ISO 8601 (`yyyy-MM-dd`, `yyyy-MM-ddTHH:mm[:ss[.f]]`, optional `Z` or offset), read as UTC whatever the machine's time zone. Any other text exits 2. |
| `--steps` | 48 | Synthetic timestamps. |
| `--step-min` | 30.0 | Minutes between synthetic timestamps. |
| `--case` | `case118` | Network to replay on: a case name, a `.m` file, or a JSON case directory (a folder holding `system.json`, such as one written by `gda --out`). |
| `--export-every` | 0 (off) | Full solve export every N-th step (step 0 always exports when tracing). |
| `--out` | `results/gda-replay-<case>` | Output directory, relative to the current directory (for a case directory, `<case>` is its last folder name). |
| `--bess chem:bus:MW:MWh[:soc]` | - | Hypothetical storage on recorded history, repeatable (same site spec as `chain`, [ch. 13](<0013 Battery storage studies.md>)). The result is tagged `Counterfactual` and also writes `storage-counterfactual.csv`. A malformed site, or a site on a bus the case does not have (`storage site '...' is at unknown bus N.`, as `chain` says), exits 2. |
| `--policy` | `peak-shave` | Storage dispatch policy: `idle`, `peak-shave` or `hold`. `gda-replay` takes no price series, so `price` and `lp` exit 2 (`--policy <p> needs a price series, and gda-replay takes none`). |
| `--shave-high` / `--shave-low` | 90 % / 70 % of peak demand | Peak-shave discharge and recharge thresholds (MW), as for `chain`. |
| `--passive-plant` | off | Run the case's bound passive plant at each step (delayed on-load tap changer taps and line / transformer thermal derating, advanced from the previous converged step). Off: the replay is exactly as without it. |

### synth

`gridsim synth [flags]`

Build the synthetic GB-at-scale case (no data held under a licence), solve it, and materialise it as a JSON case directory. Exit 3
if the check solve does not converge (the folder is still written).

| Flag | Default | Meaning |
|---|---|---|
| `--seed` | 20260605 | Build RNG seed. |
| `--scale` | 1.0 | Network size scale factor: multiplies the 11 kV secondaries under each 33 kV primary (at least 2 each). Below 1.0 the result is a smaller GB: each zone's demand, embedded plant and transmission fleet are multiplied by the zone's secondaries built over those it gets at 1.0, so the load per secondary transformer stays at its 1.0 level. Every scale up to 0.36 builds the same 1,082-bus network with 14.5 GW of demand; with seed 20260605 every scale tried from 0.02 to 2.0 converges (0.02: 4 Newton iterations, V 0.951-1.040 pu; 0.5: 1,309 buses, 19.9 GW). At 1.0 and above nothing is scaled: a larger scale spreads the same GB demand over more secondaries (2.0: 3,946 buses). Must be a finite number above 0 (else exit 2). |
| `--iters` | 60 | Max Newton iterations for the check solve. |
| `--oltc` | 30 | Max OLTC passes. |
| `--qlimits` | off | Enforce Q-limits in the check solve. |
| `--worst` | off | Print the ten worst spec-vs-solved buses even when converged. |
| `--out` | `results/synth/gb-synthetic` | Output case directory. N256: the default used to be the bundled `data/gb-synthetic` beside the executable; to regenerate the shipped case, name it (`--out data/gb-synthetic`). `--out` with no value exits 2. |

The folder is written by the same writer as `gda --out`, and it is lossless for every generator field a JSON v1 case
carries: `pMinMw`, `pMaxMw` (when not the capacity; an unlimited limit as `"Infinity"`), `qOutputMvar`,
`dynamicsClass`, `converterInterfaced`, `converterFaultCurrentPu`, `powerFlowOnly`, `unitTransformerXPu`,
`gridForming` and `inService: false` (out-of-service units are written too), each only when it differs from the
loader's default. What JSON v1 cannot hold (a generator's X''d and X2, gencost curves) is not dropped silently: a
`note:` line on stderr names how many generators carry it.

### export

`gridsim export [<case>] [out.json] [--dir <path>] [--frames 72]`

Write a visualisation scene (buses, branches, animation frames) for external renderers. Default output
`results/gridviz.json`, default model `data/uk`. A free token that names a case is the case; any other free token is
the output path and must end in `.json` (exit 2 otherwise, so a mistyped case name is not used as a file name).

| Flag | Default | Meaning |
|---|---|---|
| `<case>` / `--dir` | `data/uk` | [Case argument](#case-arguments) or JSON case directory. |
| `[out.json]` | `results/gridviz.json` | Output path. |
| `--frames` | 72 | Animation frames. |

### convert

`gridsim convert <input.m|input.raw|input.uct|case-dir|case> <output.m|output.raw|output.xml|output.json|output-dir> [--public [--provenance <manifest.json>]]`

Convert a case between MATPOWER, PSS/E RAW, CGMES, a JSON case folder (v1) and a JSON v2 case. An output ending in `.m` is written as
MATPOWER (version 2); an output ending in `.raw` is written as PSS/E RAW revision 33 (see below); an output ending in `.xml` is written as CGMES RDF/XML (see below); an output ending in `.json` is written as a JSON v2 case (N299, October 2026): the manifest at that
path and its network file beside it (`uk.case.json` writes `uk.network.json`); any other output is a JSON case folder. The input is loaded as every verb loads it (a `.m` file through the unit
normaliser, a PSS/E `.raw` file through the RAW reader ([ch. 2](<0002 Loading networks.md#pss-e-raw-files>)), a UCTE-DEF `.uct` file through the UCTE reader ([ch. 2](<0002 Loading networks.md#ucte-def-files>)), a JSON folder dispatched as usual, or a built-in name). The conversion is then proved: the written case is
read back and both are solved with the contingency option set and the tap-changer loop, and every bus voltage, angle
and injection, both loss totals and every line flow must be the same bits. The last line says so (`round trip: the
written case solves bit-identically (...)`).

Everything a power flow reads is written exactly and in model order (buses, generators including out-of-service ones,
branches), so a MATPOWER case converts both ways without changing a bit. What the target format cannot hold is printed
as `note:` lines (and written into a `.m` file as comments): to MATPOWER, names, layout coordinates, ZIP loads,
generator types, inertia and fault data, OLTCs, boundaries and the asset bundles; to JSON, MATPOWER cost curves
(`gencost`, carried by the model but not by JSON v1) and DC lines. A JSON folder written by `convert` sets
`"dispatch": false` in `system.json` (the written outputs are the operating point) and keeps transformers in
`transmission.json`, in branch order (see [ch. 2](<0002 Loading networks.md>)). A bus with no per-bus voltage
limits is written to MATPOWER with VMAX / VMIN 1.06 / 0.94, the band GridSim's checks fall back to.

A PSS/E RAW output carries the devices the RAW reader models, each in its own section: two-terminal LCC, VSC and
multi-terminal DC links, FACTS devices (a STATCOM, a UPFC, a constant series impedance with its series element),
static var compensators (a continuous switched shunt whose BINIT the reader adds back to the bus shunt), induction
machines (custom single-cage design), on-load tap changers (COD 1) and transformer flow controls (COD 2 and 3). It is
read back with tap and shunt control applied. A tap changer or flow control is written only when the reader's formulas
give back its numbers to the bit (target and deadband from VMA and VMI, step from RMA, RMI and a whole NTP); one that
cannot be is left at its present tap and named in a `note:` line, so the proof then shows the difference. RAW keeps
lines and transformers in separate sections, so the read-back lists the branches in that order (lines, then
transformers, then FACTS series elements); the proof compares it with the input put in that order, and a second
`round trip:` line says whether the input in its own order also gives the same bits (a different branch order can
change the last bits of the admittance sums). Not written, with a note: generator dynamics and fault data, boundaries,
the asset bundles, MATPOWER DC lines; a name holding a single quote is written in double quotes. Refused (exit 1):
ZIP loads, zero-sequence data, an infinite generator limit, a transformer with line charging and a ratio or phase shift.

A CGMES output is one RDF/XML file holding the equipment, topology, steady-state hypothesis and state-variable content:
buses as TopologicalNodes with their base voltage and their stored voltage as an `SvVoltage` (where a solve starts),
one `EnergyConsumer` per bus with demand, one `LinearShuntCompensator` per bus with a fixed shunt, the in-service
generators as `SynchronousMachine`s (voltage control and target at slack and PV buses, the slack's machines with
`referencePriority` 1, reactive and active limits, `ratedS`, inertia), in-service branches as `ACLineSegment`s when
they are plain lines on one base voltage and otherwise as two-winding `PowerTransformer`s (ratio and phase shift as tap
changers; a ratio the linear changer cannot give back exactly as a one-point ratio table), and rate A as a permanent
`ApparentPowerLimit`. It is read back through GridSim's CIM reader with the starting voltages and the ratings, on the
input's system base (CGMES carries none); the read-back numbers buses 1..n in model order and lists lines before
transformers, so the proof compares it with the input put in that form, and a second `round trip:` line says whether the
input in its own numbering and order gives the same bits. CGMES holds ohms, siemens and kV, and the per-unit
conversion (a product or quotient by a base that is not a power of two) does not reach every double: about half the
values of a typical MATPOWER case have no CGMES value that converts back to the same bits. Each such value is written as
the nearest one (one ulp off after read-back), a `note:` counts them by kind and names the first, and the proof then
fails (exit 3) naming the first difference; a model whose values are images of physical values (one read from CIM, or
one already passed through CGMES once) converts exactly. Refused, every reason listed and nothing written (exit 1):
devices GridSim's CIM reader does not rebuild into the model the power flow solves (on-load tap changers and flow
controls, HVDC links, FACTS devices, static var compensators, induction machines), ZIP loads, a bus with no base
voltage, a slack bus with no in-service generator, machines at one voltage-controlled bus with different set-points.
Not written, with a note: out-of-service equipment, generator types outside the CGMES unit classes, converter flags,
fault and sequence data, cost curves, rates B and C, per-bus voltage limits, layout, boundaries and the asset bundles.

`--public` writes a public export: only what a `gridsim-provenance/1` manifest traces to published sources. The
manifest (`--provenance <file>`; by default `provenance.json` in the case folder, or `<file>.provenance.json` beside a
case file) lists its sources (id, title, publisher, URL, licence, and `published` true only for a source anyone can
obtain from its publisher) and, for each element, the sources it comes from: buses by id, branches and generators by
name, and optionally a bus's demand and fixed shunt by bus id and an on-load tap changer by its transformer's name. Any
other key, a cited source the manifest does not list, or an element citing no source is refused naming it. A bus is
kept when every source it cites is published; a branch, generator or tap changer when its name is unique, every source
it cites is published and its buses are kept. Unsourced demand and shunts on a kept bus are zeroed, layout coordinates
are zeroed, a PV bus left with no generator becomes PQ, and devices the manifest has no section for (HVDC links, FACTS
devices, static var compensators, induction machines, flow controls, mutual couplings, boundaries, the asset bundles,
MATPOWER DC lines) are stripped. Every strip is printed as a `public:` line (and in the result file's `public_stripped`
table), and the public model is then written and proved as any other conversion. No manifest, a manifest for another
case, or a public model with no slack bus or with a kept slack bus whose generators were stripped exits 1 with nothing
written: a public export is never made without evidence, and the built-in IEEE cases (no case folder) need
`--provenance`. `--provenance` without `--public` is a usage error (exit 2).

A JSON v2 output carries the fault sequence data JSON v1 cannot (N234: generator `subtransientXdPu` /
`negativeSeqXPu`, transformer `windings`, `r0Pu` / `x0Pu` and `neutralXPu`), so a v2 case converts to v2 and back
with its earth-fault data. Its manifest sets `"dispatch": false`; every branch is written in place as a line (a
transformer with `isTransformer`), except the trailing run of branches from the first one with winding data, which are
written as `transformers` elements (the v2 loader always builds those after the lines, so this keeps the branch order).
Its proof also compares every generator's X''d and X2 and every branch's R0, X0, windings and neutral reactances bit
for bit, and a second `round trip:` line says so. The files are byte-stable: the same model writes the same bytes on
every OS ("\n" line ends). The asset-dynamics and protection bundles are written too (N321, October 2026; they used to
be left out with a note): each asset class as a case-local template `gridsim.local.<class name>@1` (kinds
gridFollowingInverter, gridFormingInverter, oltc for a transformer class, lineType for a line class, protection), the
binding rules as the manifest's `bindings` (match types in lower case), each element's class as its `template`, and
the placed protective devices as the network file's `protections`, so the written case loads with the same classes,
rules and devices. A bundle that cannot be written exactly (a class name that cannot be a template id, such as one
taken from an imported template `id@rev`) is not written at all, and a note says why. Notes name what is not written:
such a bundle and the element template references into it, cost curves, DC lines, out-of-service branches (v2 refuses them for now), winding data
that would need the branches reordered, and the names given to unnamed generators (`unit-<n>`) and branches
(`branch-<n>`), since v2 requires names. A bus without a base voltage above 0 kV cannot be written to v2 (exit 1). The
v2 output needs the v2 schemas to be proved (found as [`validate`](#validate) finds them); without them nothing is
written (exit 1). An existing manifest or network file is never overwritten (exit 2).

Exit 0 when the round trip is bit-identical; 3 when it is not (a ZIP load or OLTCs written to MATPOWER, or tap changers
whose band RAW cannot give back exactly, or values CGMES cannot hold exactly, for example),
naming the first difference and, where the buses match, the largest voltage difference (|dVm|, |dVa|) and how many buses differ (the output is kept); 1 when the input does not load or the output cannot be written (a
NaN, or a value the format cannot hold, is refused naming it); 2 for a usage error or when the output already exists
(an existing `.m` file or a non-empty folder is never overwritten).

What each writer carries, notes and refuses for the full GB case, with the counts and the size of the resulting
difference: [gb-full through every writer](<../validation/studies/gb-full-export-loss-list.md>).

Result files: `--json <file.json>` or `--csv <file.csv>` write the result, including the round-trip verdict, to a file
([Result files](#result-files)); `convert` does not take `--out`, because its second argument is already its output.

### validate

`gridsim validate <file.json|folder>... [--schemas <dir>]`

Check GridSim JSON v2 documents with the Phase 1 validator, choosing the check by each document's `"schema"` tag:
`gridsim-case/2` against `case.schema.json` (and, when the manifest is valid, each network file it lists, read relative
to it: a missing one is an error on the manifest, an existing one is checked as a network file); `gridsim-network/2`
against `network.schema.json`; `gridsim-library/2` files all together (schema, kind specs, unique `id@rev`, controller
references resolving within the set); `gridsim-kind/2` against `kind.schema.json`; `gridsim-dynamics/2` (F23, machine
dynamic data for `stability --rms --dyn-data`) against `dynamics.schema.json` and then its templates as one library
(the binding to a case's generators is checked when a study loads it); `gridsim-dist3ph/2` (N339, the three-phase
feeder in its v2 form) against `dist3ph.schema.json` and then with the feeder reader itself (ids, references, the
branch kind rules, one radial tree, phases; its first refusal is reported). A folder is searched for `*.json`
files. A document with no tag (a v1 case file, for example) or an unknown tag, and a file that is not JSON, are
reported as invalid. Library imports in a manifest are not resolved (they name files relative to a data root a lone
manifest does not know); pass the library files to check them. See
[technical ch. 28](<../technical/0028 The v2 JSON schema.md>).

Each document prints `OK <path> (<tag>)` or `FAIL <path> (<tag>): N error(s)` followed by every error as
`<pointer>: <message>`, then a summary line.

| Flag | Default | Meaning |
|---|---|---|
| `--schemas` | `schemas/v2` beside the program, else the nearest above it or above the current folder | The folder holding the v2 schemas (with the kind specs in `kinds/`). |

Exit 0 when every document is valid; 3 when any is not; 1 when a path does not exist, no `.json` file is found or the
schemas cannot be found or loaded; 2 for a usage error.

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).

### zonal

`gridsim zonal <pypsa-gb-zonal|folder>`

Show a PyPSA-GB zonal transport model: the bundled `pypsa-gb-zonal` (PyPSA-GB @ 8e084af, MIT, 20 zones and 37 links,
under `data/matpower/gb/pypsa-gb/zonal`) or a folder holding the same two files, `buses.csv`
(`name,y,x,v_nom,carrier`: zone name, latitude, longitude) and `links.csv` (`name,bus0,bus1,carrier,p_nom,p_min_pu`).
The headers must be exactly these. Prints the zones, every link with its capacity each way (p_nom forwards, -p_min_pu x
p_nom backwards; an interconnector to a foreign node is marked), and, when the folder has a `boundaries.json`, the
transfer capacity across each boundary cut: the bundled cuts B4, B6, B7 and B8 were derived from NESO's ETYS boundary
GIS lines (March 2025) by `tools/gb-reference/derive_boundaries.py`.

A zonal model has links with a MW capacity and no impedance, so it has no power flow. This verb only reports what the
files hold, and every verb that solves a network refuses `pypsa-gb-zonal` as an input error (`error: case
'pypsa-gb-zonal' cannot be solved: it is a zonal transport model ...`, exit 1).

Exit 0 when the model is shown; 1 when the files are missing or malformed (the message names the file, line and
column) or the argument is neither the bundled name nor a folder; 2 for a usage error.

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the zones, links and
boundary capacities to a file ([Result files](#result-files)).

### pmu-ingest

`gridsim pmu-ingest <capture.bin> [--channels <map.json> --measurements-dir <dir>]`

`gridsim pmu-ingest --live tcp://host:port|udp://[bind]:port --live-frames N [--live-seconds S] [--live-idcode ID] [--live-udp-source IP] [--live-quarantine <dir>] [--channels <map.json> --measurements-dir <dir>]`

EXPERIMENTAL. Decode a recorded IEEE C37.118.2 synchrophasor stream: the frames as sent, back to back, a CONFIG-1 or
CONFIG-2 frame followed by DATA frames ([technical ch. 33](<../technical/0033 C37.118 synchrophasor ingest.md>)).
Every frame's CRC is checked; the CONFIG frame is parsed (TIME_BASE, each PMU's FORMAT, channel counts, PHUNIT and
FNOM); every DATA frame is decoded with one block per PMU of that configuration, in its order (a phasor data
concentrator's multi-PMU frame; integer fields scaled by FNOM and PHUNIT); and the usable samples are aligned on their
reporting instants by the phasor data concentrator. Prints the frame counts, one line per PMU (phasors, samples, first
and last time, frequency range, how many STAT words were non-zero, how many samples were dropped), the number of aligned
instants with a PMU missing, and a `quality:` line counting the dropped samples by reason. HEADER, COMMAND and CONFIG-3
frames are counted and not decoded; if the CONFIG frame changes during the capture, the change is reported and later
frames use the new one: the configuration line then reads `last configuration:` and says each DATA frame was decoded
with the configuration in force when it arrived. The per-PMU table lists every PMU of every configuration in force (a
PMU whose phasor count changed shows each count, `2/3`), and an instant counts as having a PMU missing only when a PMU
of the configuration in force at that instant did not report (N260; the table and the expected list used to follow
the capture's last configuration only). Analogue and digital channels are sized, not decoded. The whole file is split into frames
before any frame is decoded, so a capture that does not frame cleanly is refused without decoding any of it.

Time: each DATA frame's instant is SOC + FRACSEC / TIME_BASE, computed exactly in whole 100 ns ticks (a fraction finer
than one tick is rounded down to the tick at or before it). A CONFIG frame with TIME_BASE 0 is refused by name (exit
1), and a DATA frame whose FRACSEC count is not below TIME_BASE (a second or more, not a fraction) is malformed:
counted and not used (exit 3).

Horizon: every decoded DATA frame's time must be older than the 169 h horizon (real clock). A capture with any frame
newer than that, or in the future, is refused whole before anything is printed: `REJECTED (forward inference): ...`,
exit 2.

Sample quality (STAT and the FRACSEC time quality, IEEE C37.118.2-2011 [VERIFY]): a sample is dropped, never aligned or
mapped, when its frame's FRACSEC message time quality code is above 0x5 (time error 100 us or more, an undefined code,
or 0xF clock failure: every block of that frame); when its STAT data-error bits 15-14 are not 00 (PMU error, test
mode, absent data); or when it is unsynchronised (STAT bit 13 set, a STAT time quality, bits 8-6, of 100 us or worse,
or an unlocked time, bits 5-4, of 10 s or more). 10 us is the line because a 100 us time error turns a 50 Hz phasor by
1.8 degrees, three times the angle share of the 1 % total vector error budget. The other STAT bits (sorting, trigger,
configuration change, data modified) and the leap-second bits do not decide.

| Flag | Default | Meaning |
|---|---|---|
| `--channels` | - | A PMU channel map (`gridsim-pmu-channels/1`): `schema`, `case`, `baseMva`, the angle `reference` (`pmu`, `phasor`, `anchorDeg`, a mapped voltage channel) and per PMU (`pmu`) its `voltages` (`phasor`, `bus`, `baseVolts`, `vmSigmaPu`, `vaSigmaDeg`) and `currents` (`phasor`, `from`, `to`, optional `circuit`, `baseAmps`, `iMagSigmaPu`, `iAngSigmaDeg`). Bases and sigmas must be finite and above 0. Checked against every configuration in force for a decoded DATA frame (each CONFIG frame of the capture that decoded at least one DATA frame; the last one when none did): every PMU present, every phasor index inside its PMU's count, a voltage channel on a voltage phasor and a current channel on a current phasor (PHUNIT). When the configuration changed during the capture, each one the map does not fit is named by the frame it arrived in (`against the configuration from CONFIG frame N (in force for K decoded DATA frame(s)): ...`). Needs `--measurements-dir`. |
| `--live` | off | N340: read a LIVE IEEE C37.118.2 stream instead of a capture file (give no file with it). `tcp://host:port` connects to the PMU or PDC, sends COMMAND frames "send CONFIG-2" and "turn on transmission" to `--live-idcode`, frames the stream (a byte where SYNC 0xAA must start a frame, or a frame size below 16, stops the run: exit 1, no resynchronising) and sends "turn off transmission" when it stops. `udp://[bind]:port` listens (bind address default 0.0.0.0) for the device's stream, one frame per datagram; a datagram that is not exactly one frame is dropped and counted (exit 3). The received frames then go through exactly the recorded path above (CRC, CONFIG, decoding, quality, alignment, the channel map and the horizon: a device streaming the present is refused, `REJECTED (forward inference)`, exit 2, like a fresh capture; a PDC replaying history is read). Prints a `live:` line with the frames received. |
| `--live-frames` | - | With `--live` (required): stop after this many DATA frames (1 or more). |
| `--live-seconds` | 30 | With `--live`: the longest the receive runs (above 0, at most 86400). Stopping on it before `--live-frames` DATA frames arrived exits 3 after the summary; no frame at all exits 3. |
| `--live-idcode` | 1 | With a `tcp://` `--live`: the device IDCODE (0 to 65535) the COMMAND frames address. Refused with `udp://`. |
| `--live-udp-source` | any sender | GR-818: with a `udp://` `--live`, read only datagrams whose source address is this IP address; datagrams from any other sender are dropped and counted on the `live:` line, never captured. Refused with `tcp://`. |
| `--live-quarantine` | off | N358: with `--live` only (refused alone, exit 2). A live device streams the present, which the forward-inference horizon (169 h) refuses: without this flag such a run is REJECTED whole, exit 2. With it, every decoded DATA frame newer than the horizon is held back and NOT ingested (no quality judgement, no alignment, no channel map, no measurement set, no report row): the received frames, except the decoded DATA frames already older than the horizon, are written byte for byte to `<dir>/pmu-live-<received UTC>.quarantine.c37` with a sidecar `<capture>.quarantine.json` (schema `gridsim-pmu-quarantine/1`: SHA-256, frame counts, source, received time, horizon, oldest and newest frame time, `sourceClass` telemetry, `evidential` false, `recorded` false, provenance tag `CuratedPlaceholder`, never `Recorded`). A line `quarantine: ...` names the file; the JSON report gains `quarantined_data_frames`. Frames older than the horizon continue through the recorded pipeline as before. Never overwrites (a clash gets `-dupN`). A folder inside the GDA public lake (a `DataSchema.json` at or above it) is refused, exit 1. `pmu-ingest <file>` refuses a quarantine capture (named `*.quarantine.c37` or with the sidecar beside it), exit 1, whatever its age: it becomes recorded data only by re-sourcing the same instants from an archive. |
| `--measurements-dir` | - | With `--channels`: write one `gridsim-measurements/1` set per aligned instant here (`pmu-<yyyyMMddTHHmmssfffffff>Z.json`, never overwriting: a clash gets `-dupN`), each VmPu / VaDeg per voltage channel and IMagPu / IAngDeg per current channel, angles re-referenced to the reference channel, dated with the instant and passed through the horizon guard. An instant whose reference channel did not report usably gives no set (its angles cannot be referenced); an instant with no mapped PMU gives none either. Prints `measurement sets: N written to <dir> (...)`. Needs `--channels`. |

Exit 0 when every frame decoded and every sample was usable; 3 when any DATA frame failed its CRC, was malformed (its
length does not match the configuration, or its FRACSEC is not below TIME_BASE), or came before any CONFIG frame, when any sample was dropped by the quality
rule, or when an aligned instant had no usable reference channel (each counted and reported, the summary still
printed); 2 for usage (including `--channels` or `--measurements-dir` alone) and for a capture with a frame newer than
the horizon; 1 when the file cannot be read, cannot be split into frames (a byte where a SYNC 0xAA should start a frame,
or a frame running past the end), holds no CONFIG frame with a good CRC, or holds a CONFIG frame that passes its CRC
but cannot be parsed (truncated, or TIME_BASE 0), and when the channel map cannot be read, is malformed (every problem
named) or does not fit a configuration of the capture. The IEEE C37.118.2-2011 text is not in docs/reference; the
field layout follows the decoder's cited tables [VERIFY].

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write every phasor of every
decoded block (time, PMU, STAT, frequency, ROCOF, rectangular and polar values, and its `quality` verdict: `Usable`,
`InvalidData`, `Unsynchronised` or `UnreliableFrameTime`) to a file, with the dropped counts in the header fields
([Result files](#result-files)).

### pmu-estimate

`gridsim pmu-estimate <case> --measurements <set.json> [--json <file>] [--csv <file>] [--out <file.json|file.csv>]`

The linear PMU state estimate (1.95.245). Reads a `gridsim-measurements/1` set (for example one `pmu-ingest
--channels ... --measurements-dir` wrote) against the case and pairs its polar phasor rows into rectangular phasors: a
bus's `VmPu` and `VaDeg` rows make its voltage phasor, and a branch's `IMagPu` and `IAngDeg` rows metered at the From
end make its current phasor. Each pair is weighted by its exact first-order error covariance (the magnitude sigma in
pu and the angle sigma in degrees, carried through the polar to rectangular transform at the measured value). Because
both phasor kinds are linear in the rectangular bus voltages, the estimate is one non-iterative weighted least-squares
solve (no flat start, no iterations, no convergence test).

Prints the phasors used, every row not used with its reason (power and flow rows, and a magnitude or angle without
its partner: listed, never dropped silently), the objective J = sum (r / sigma)^2 with its degrees of freedom, and each
bus's |V| and angle. There is no bad-data test (use `estimate` for chi-square and bad-data handling). The result
writer's tables are `buses` (bus_id, vm_pu, va_deg) and `not_used` (row), with the counts and J in the summary.

Refused: two rows of one kind at one place (which to pair is ambiguous), a current metered at the To end, and a phasor
of magnitude 0 (exit 1); a set that fails validation against the case, or holds no phasor at all (exit 1); a set newer
than the horizon (`REJECTED (forward inference)`, exit 2).

| Flag | Default | Meaning |
|---|---|---|
| `--measurements <set.json>` | (required) | The `gridsim-measurements/1` set. |
| `--json`, `--csv`, `--out` | - | The shared result writer ([Result files](#result-files)). |

Exit codes: 0 estimated; 1 a case or set that cannot be read, fails validation or holds no phasor; 2 usage, or a set
newer than the horizon; 3 not observable (the phasors do not determine every bus voltage: the gain matrix is singular).
### estimate

`gridsim estimate <case> (--measurements <m.json> | --synth) [flags]`

The measurement-based WLS state estimator. `<case>` is a built-in name, a `.m` file under `data/matpower/`, or a JSON
case directory (anything with a `system.json`, so gb-spine and gb-full materialisations estimate directly). See
[ch. 21, State estimation](<0021 State estimation.md>) for the workflow.

| Flag | Default | Meaning |
|---|---|---|
| `--measurements` | - | A `gridsim-measurements/1` file. It must carry `takenUtc`, an instant older than the 169 h horizon: an undated set, or one newer than the horizon, is refused with `REJECTED (forward inference): ...` and exit 2. |
| `--synth` | - | Solve the case as ground truth, sample a deterministic noisy set, and score the estimate against it. With `--measurements` it exits 2. |
| `--seed` | 20260713 | Synthetic sampler seed. |
| `--noise` | 1.0 | Noise scale multiplier. |
| `--coverage-vm` / `--coverage-inj` / `--coverage-flow` | 1.0 / 1.0 / 1.0 | Fraction of each measurement class sampled. |
| `--method` | `wls` | `wls`, `huber`, `lav` (least absolute values solved exactly by successive linear programming, N331 / N356; dense, refused above 2,000 states; prints the LAV objective sum \|r\| / sigma and no chi-square verdict, with a NOTE saying why), `lav-lp` (alias of `lav`, identical output), `lav-irls` (least absolute values by IRLS, the pre-N356 `lav`: an approximation, its weights do not settle and every run prints a NOTE saying so), `schweppe-gm` (alias `gm`: Schweppe-Huber generalised M on the leverage-normalised residual), `qr` (orthogonal Householder QR; dense, refused above 3,000 buses), `constrained` or `feasible` (alias `constrained-physics`). Any other value exits 2. |
| `--prior` | `flat` | `flat` keeps the set's voltage-schedule rows; `solve` solves the measured-injection power flow, replaces the set's `pseudo-voltage-schedule` rows (if any; the line says how many) with the solved profile and warm-starts from it. |
| `--warm-start` | off | Warm-start from the case's own solved operating point (needed for real data). |
| `--phaseangle` | - | One or more space-separated `BUS:ANGLE:UTC:FREQ` per-bus voltage-angle observations (angle in degrees, UTC in ISO-8601 `Z` form, frequency in Hz), authored as `VaDeg` rows and merged before the prior. Genuine per-bus angle differences directly observe the estimator's weakest directions; a common-mode system angle cancels against the slack (used only as a reference frame). |
| `--phaseangle-ref` | `auto` | `auto` re-references to the slack via a datum bus and time-projects via frequency; `referenced` projects only (subtracts `--phaseangle-from-gda`'s common-mode); `raw` injects as given. |
| `--phaseangle-sigma` | 2.0 | 1-sigma of each authored angle row, degrees. |
| `--phaseangle-at` | set instant | Common instant the angles are projected to; defaults to the set's `takenUtc`. ISO 8601 only, read with the invariant culture: `yyyy-MM-dd`, `yyyy-MM-ddTHH:mm`, `yyyy-MM-ddTHH:mm:ss` or with one to seven fraction digits, each with `Z`, an offset `+HH:mm`, or nothing (UTC). Any other shape (`01/02/2100`, `2 Jan 2100`, a space for `T`) exits 2. The UTC part of each `--phaseangle` tuple follows the same rule. |
| `--phaseangle-from-gda` | - | GDA v1 lake root: read `PHASEANGLE_INFERED` (the system COI angle and true frequency/RoCoF) at the target instant and use it as the absolute reference frame / common-mode de-rotation. |
| `--tol` | 1e-8 | Convergence tolerance on the state update. |
| `--max-iter` | 30 | Iteration cap. |
| `--no-bad-data` | off | Skip bad-data detection: no chi-square test and no removal. The consistency line reads `not tested (chi-square not run: --no-bad-data; J = ...)`. |
| `--hti` | off | Use Hypothesis-Testing Identification as the bad-data method instead of sequential largest-normalised-residual (LNR) removal: when chi-square fails, every suspect (normalised residual above 3) has its gross error estimated jointly and is removed only if its test statistic exceeds 3; repeat until chi-square passes, nothing is identified, or 20 removals. Separates interacting errors that LNR can mis-attribute. Dense: refused above 2,000 states. Applies to `wls`, `qr`, `constrained` and `feasible`; refused (exit 2) with the robust methods (`huber`, `lav`, `lav-lp`, `lav-irls`, `schweppe-gm`, which down-weight or fit instead of removing), with `--backfill-removals` and with `--no-bad-data`. Removals are printed and written to `hti-removals.csv` (pass, measurement, estimated error, test statistic); the pass counts from 0 (pass 0 is the first estimate), as the LNR loop, the estimate trace and `hti-corrections.csv` count (N256; it used to count from 1). The estimated error is labelled with its unit per row: columns `estimated_error` (internal units) and `estimated_error_unit` (`pu` for voltage and current magnitudes, `rad` for angles, `pu_base_mva` for powers and flows), then `estimated_error_measured` and `measured_unit` (the same error in `pu`, `deg`, `MW` or `Mvar`); the console prints the internal unit and, for angles and powers, the measurement-unit value in brackets; the export's `removed` list (LNR removals) is empty under `--hti`. Under `constrained` and `feasible` a `virtual-zero-injection` row is a hard constraint and is never removed. |
| `--hti-correct` | off | With `--hti`: correct each identified measurement in place instead of removing it, z <- z - e^ (its jointly estimated gross error, converted to the measurement's own units), keeping the row so observability is preserved; then re-estimate and re-run chi-square. A row identified again after its correction (the correction did not clear it) is removed. Corrections and removals together are capped at 20. Corrections are printed and written to `hti-corrections.csv` (pass, measurement, original and corrected value, estimated error with the same four unit-labelled columns as `hti-removals.csv`, test statistic). Without `--hti` it is a usage error (exit 2). The design is robust-gm-leverage.md section 6.2; there the default action is to correct, here removal stays the default. |
| `--fdia-screen` | off | Screen the final estimate for a residual-cloaked false-data injection: chi-square PASSED yet the state is physically impossible or outside the operational envelope (voltage band, thermal rating, generator Q capability). A limit breach counts only when it exceeds three times the estimate's uncertainty for that quantity (the sigma of the meter that measures it); smaller breaches are listed as not counted. Prints the verdict (`suspected`, `clear`, `chi-square-failed` or `no-verdict`) and writes `fdia-screen.json`. A `suspected` or `no-verdict` result exits 3 (see below). Refused (exit 2) with `--no-bad-data`. |
| `--backfill-removals` | off | Replace each removed measurement with a loose prior-evaluated pseudo (observability restoration). Needs a prior: refused (exit 2) without `--prior solve` or `--warm-start`, whose absence would make the flat start the prior. |
| `--lean` | off | Skip residual-covariance solves when chi-square already passed (backfill throughput switch; normalised residuals become NaN). |
| `--trace` | off | Capture the per-iteration convergence path into the export. |
| `--export-solve` | off | Also project the estimate into a `gridsim-solve-export/1` document (`estimated-solve-export.json`), the estimated-state lake feed. |
| `--out` | `results/estimate-<case>` | Output directory (`estimate-export.json`, `residuals.csv`, `measurements.json` when synthetic). |

The summary header names the method (`=== Huber M-estimator state estimate: case14 ===`), and a robust method whose
reweighting stopped at its cap prints a `WARNING` line (`IrlsCapReached`); the same flag is written to
`estimate-export.json` as `estimate.irlsCapReached` (always present, `false` for the non-robust methods) and, only when
it is true, to the `--audit-log` outcome as `irls_cap_reached=true`.

Exit 0 only when the estimate converged and the network is observable; 3 otherwise, and also when `--hti` cannot
identify the bad data because the suspect block is singular; 1 for load errors, an invalid measurement set or a
ground-truth solve that does not converge; 2 for usage and flag errors, forward-inference rejections and a dense
method (`qr`, `constrained`, `feasible`) refused above 3,000 buses. A chi-square failure on its own does not change the
exit code (bad data is reported, not a run failure). With `--fdia-screen`, a `suspected` verdict, or a screen that has
no verdict (estimate not converged, or no redundancy so chi-square never ran), exits 3: the screen was asked for and
the estimate failed it or could not be judged. `clear` and `chi-square-failed` leave the exit code as above.

### estimate-noise-floor

`gridsim estimate-noise-floor <estimate-export.json|dir> [<estimate-export.json|dir> ...] [flags]`

The measurement noise-floor study. Reads the residual tables of one or many `estimate-export.json` files written by
`estimate` (a directory is searched recursively for files of that name), pools the normalised residuals by (source tag,
measurement kind) and reports, per class, the scale factor k = true noise / declared sigma (robust median-based and
std-based), a shape check (bias), the suggested sigma rescaling and whether the class is miscalibrated. Run it over
many timestamps: a single snapshot rarely has enough samples per class. Exports of non-converged estimates are skipped
with a warning; NaN normalised residuals (critical measurements, `--lean` runs) are counted and skipped. Put the paths
before any flags.

| Flag | Default | Meaning |
|---|---|---|
| `--miscal-tol` | 0.25 | Flag a class whose robust k leaves [1 - tol, 1 + tol] or whose bias exceeds tol; above 0. |
| `--min-samples` | 8 | Classes with fewer finite samples are reported but never flagged; at least 1. |
| `--out` | `results/estimate-noise-floor` | Output directory (`noise-floor.csv`, `run-metadata.json`). |

A study, not a gate: exit 0 whenever a report is produced, miscalibrated classes included (they are the finding). Exit
1 for an unreadable or wrong-schema file, a path with no export, or a corpus with no usable (finite, converged)
residual; exit 2 for no path or a bad flag value.

### crossval

`gridsim crossval <case|case-dir|solve-export.json> [--tol F] [--out <dir>]`

An existing `solve-export.json` is diffed as it is: one written by another verb at the everyday 1e-8 pu carries that
solve's own residual and will not agree to machine epsilon for that reason alone (the sidecar says so when its final
mismatch is above 1e-10 pu).

Solve a case (or take an existing export), then hand it to the pandapower sidecar (`tools/crossval/crossval.py`, shipped beside the executable in a build and in a
release, found there first, then in the source tree the build came from; never in the current folder) for an
independent re-solve and diff. Requires Python with pandapower. The interpreter is chosen in this order, and the verb
prints the one it used (`[crossval] Python: <path> (from <source>)`): `GRIDSIM_PYTHON` when set (a full path, or a name
looked up on PATH; if it names nothing the verb exits 1 and runs no other interpreter); else the active virtual
environment's own interpreter (`VIRTUAL_ENV`); else the first `python` (then `python3`) found on PATH, folder by folder in
PATH order; else the `py` launcher (Windows); else the bare names. Voltage-dependent (ZIP) loads in the export are
given to pandapower as its own ZIP load model. See [ch. 27, Validation tools](<0027 Validation tools.md>).

The comparison is held to machine epsilon (N243, October 2026). After GridSim's ordinary solve (1e-8 pu) converges,
the verb solves the same case afresh at 1e-9, 1e-10, ... 1e-15 pu with the same engine and options and exports the
tightest one that converges; it prints `[crossval] solve tolerance <T> pu (... final mismatch <M> pu)`. (Newton stops
with a residual just under its tolerance, and the state carries about J^-1 times that residual: up to 1e-9 pu at
1e-8. That is a convergence setting, not an error.) The sidecar gives pandapower the same problem: generators
aggregated per bus as GridSim holds them (summed P, Q and reactive limits, the last unit's voltage setpoint),
transformer charging as bus shunts, transformers written high-voltage end first, pandapower's pi transformer model, Q
limits on, no distributed slack, and pandapower's own tolerance ladder. It checks that the admittance matrix
pandapower solved equals MATPOWER's `makeYbus` on the exported rows to 4096 machine epsilons of the largest entry, and
stops with no verdict (sidecar exit 4, verb exit 3) when it does not. The summary prints both differences in full
precision (|V| in pu, the angle in rad and deg), the tolerance applied and, when one applies, the case's documented
allowance (`gb-network-2224` only: a floating-point floor, see ch. 27).

| Flag | Default | Meaning |
|---|---|---|
| `--tol` | 4096 machine epsilons (9.09e-13) | Agreement tolerance, applied to the voltage magnitude in pu and to the angle in radians. Without the flag the sidecar's default applies; a negative value exits 2. |
| `--out` | `results/crossval-<name>` | Output directory (`solve-export.json`, `crossval-diff.json`). |
| `--tool` | `pandapower` | The cross-validation sidecar. pandapower is the only one; any other value exits 2. |

Exit 0 when the two solves agree; 3 on a mismatch or a sidecar validation failure; 77 when the environment cannot run
the check (no Python, or no NumPy / pandapower in the interpreter used); 2 for a missing argument; 1 for a case that does
not load, or a `GRIDSIM_PYTHON` that names nothing. The sidecar's own exit 1 means "out of tolerance", so a sidecar that
crashes (a Python traceback, which also exits 1) is reported as a comparison failure, exit 3, with the sidecar's error
text on stderr.

## Real-time and telemetry verbs

Both verbs keep to the forward-inference horizon: a measurement window newer than 169 h before `now` is never
estimated. See [ch. 25, Interposing live telemetry](<0025 Interposing live telemetry.md>).

### serve

`gridsim serve <case> --measurements-dir <dir> [--method wls|ekf|ukf|enkf] [--utc-now <iso8601>] [--max-windows N]`

Run the real-time estimator as a resident stream over a directory of `gridsim-measurements/1` files, taken in
file-name order. Each window is loaded against the horizon (the clock, or `--utc-now`): a window inside the horizon is
skipped, never estimated, and counted as `rejected-fresh`; an undated window (no `takenUtc`) stops the run with
`REJECTED (forward inference): ...`, exit 2. Each estimated window prints its sequence, state time and status; a
converged (`wls`) or finite (`ekf`, `ukf`, `enkf`) window line ends with its largest 1-sigma bound,
`max-sigma |V|=<pu> pu angle=<deg> deg` (`sigma=n/a` when the WLS state covariance was not computed, above 2,000
states). Ends with a processed / converged / rejected-fresh / failed summary, then the state of the last converged or
finite window, one row per bus: `| bus | |V| pu | sigma |V| pu | angle deg | sigma angle deg |` under a heading
`estimated state at window #N (<time>), wls:` (`filtered state ...` for the recursive filters). `<case>` is a built-in name, a `.m` file or a JSON case
directory, given positionally ([Case arguments](#case-arguments)). With `--audit-log` the run is recorded (the case,
the window folder's hash, the summary and the state time of the newest estimated window).

| Flag | Default | Meaning |
|---|---|---|
| `--measurements-dir` | required | Directory of `*.json` measurement windows. |
| `--method` | `wls` | `wls` (static WLS per window), or a recursive tracking estimator: `ekf` (extended Kalman, information form), `ukf` (unscented Kalman, derivative-free sigma points) or `enkf` (ensemble Kalman, 80 members, fixed seed so runs repeat). Any other value exits 2. The recursive filters are dense: all three refuse a case above 2,500 states, and `ukf` and `enkf` also refuse a window with more than 2,500 measurements (they invert an m x m innovation covariance every window); each refusal exits 2 with the reason. |
| `--utc-now` | the clock | The `now` the horizon is measured from, for a reproducible run. It may only be EARLIER than the real clock (a stricter horizon): a later one would let recent data through and is refused with `REJECTED (forward inference): --utc-now ... is later than the real clock ...`, exit 2, before any window is read. ISO 8601 only, as for `estimate --phaseangle-at` (`01/02/2100` and other shapes exit 2). |
| `--max-windows` | all | Process at most N windows (the first N by file name). N must be at least 1: `0` or a negative count exits 2 naming the flag. |

Exit 2 on missing arguments, a bad `--method`, `--utc-now` (malformed, or later than the real clock) or `--max-windows`
(below 1), a filter size refusal, or a forward-inference rejection (an undated window);
exit 1 if the case does not load or the directory holds no `*.json` windows; exit 3 if any window failed, or if no
window produced a usable estimate (none converged: every window unobservable or not converged, or every window inside
the horizon), with the reason on stderr. A window file that cannot be loaded (not JSON, not a measurement set) is named
on stderr and skipped, as the web feed does, and the other windows run; the run then ends with exit 1 (an input error,
ahead of any exit 3). An estimate the output guard refuses as newer than the horizon gives `REJECTED (output guard)`
and exit 2.

### interpose

`gridsim interpose [<case>] --protocol <name> (--listen host:port | --port N) --crosswalk <map.json> [flags]`
`gridsim interpose --list-protocols`

The RTU/SCADA interposer: receive live protocol frames, validate and correct each frame, decode it to typed points,
map the points through the crosswalk, and emit `gridsim-measurements/1` windows. Each window is judged against the
169 h horizon (real clock) when it is flushed. Only a window older than that (a replayed, recorded capture) is written
to `--measurements-dir`. A window newer than that (live telemetry) is held: never written to `--measurements-dir` (the
first one is named on stderr, the rest counted), and kept only as a quarantine capture when `--out-store` is given
(its `Note` says so). Stops on a key press, after `--ticks N` frames (the global switch; N is at least 1), or after
`--live-seconds S` of listening, whichever comes first. Ends with
accepted / rejected / decode-error / map-error / point / window / duplicate-name / client-reset counts (after `points`,
which counts every decoded point, `step-positions` and `bitstrings` count those two kinds among them, N346), the IEC 104 ASDU
types received but not decoded (`unsupported-type-ids`), the `held-fresh` and `quarantined` window counts, and
`discarded-bytes` (octets thrown away without being part of a frame: junk skipped while resynchronising, and a partial
frame left when a client went away; N230). A client that closes or resets its connection is dropped and the next one
accepted. A window file never overwrites another: a clash is kept as `<name>-dupN.json`.

A malformed frame (rejected by frame correction, or framed but undecodable) is named once on stderr when the first one
arrives (`WARNING: interpose: malformed ...`); later ones are counted (`rejected`, `decode-errors`). Any malformed frame
makes the run exit 1 at its end, unless `--tolerate-malformed` is given (N230).

For IEC 104 the interposer is the controlling station and drives the link (N230): on each connection it sends STARTDT
act (and records STARTDT con), answers TESTFR act with TESTFR con, acknowledges received I-frames with an S-frame at the
latest after w = 8 of them or t2 = 10 s after the first unacknowledged one, and sends TESTFR act after t3 = 20 s with no
frame received (IEC 60870-5-104 clause 5 defaults [VERIFY]). A STARTDT act or TESTFR act not confirmed within t1 = 15 s,
or an I-frame out of sequence (its N(S) is not V(R)), closes the connection; the next outstation connection starts afresh
(GR-803, GR-823). k and STOPDT are not implemented.
Modbus TCP is received passively, as before.

IEC 104 decodes the monitor types 1, 3, 5, 7, 9, 11, 13, 15, 30 to 37 (N337: step position 5 / 32 and bitstring 7 / 33
are decoded, no longer counted unsupported). A step position (a tap position, -64 to +63; a moving tap is flagged
transient) and a 32-bit bitstring are points but not electrical measurements: they never enter a window, and a crosswalk
that binds one of their addresses is refused as a map error.

| Flag | Default | Meaning |
|---|---|---|
| `--protocol` | required | `iec104` (default port 2404) or `modbus` (default port 502). `--list-protocols` prints the registry and exits. DNP3 and IEC 61850 GOOSE are not selectable: they exist only as link-framing scaffolding with no application layer, and the registry listing says so. |
| `--listen` | `0.0.0.0:<default port>` | Endpoint `host:port`, `[IPv6]:port` or a bare port; the port is 0 to 65535 (exit 2 otherwise). |
| `--port` | protocol default | Port on all interfaces, when `--listen` is absent. |
| `--crosswalk` | required | Point map (`.json`) from protocol addresses to measurements. |
| `<case>` | the crosswalk's case, else `rtu` | Case label for the windows (the case is not loaded). |
| `--measurements-dir` | - | Write each window older than the horizon as a JSON file here. A window newer than the horizon is not written (held); a run that held any exits 2 (see below). |
| `--out-store` | - | Also write windows to the quarantine Parquet store (refuses to open inside the GDA public lake). Windows newer than the horizon go here only, as quarantine captures. |
| `--source-id` | `rtu_live` | Source id for the quarantine store. |
| `--window-ms` | 1000 | Window length (ms); at least 1. |
| `--monitor` | off | Decode to the console only; no windows written. Refused (exit 2) with `--measurements-dir` or `--out-store`. |
| `--no-parquet` | off | Accepted; reserved, no effect. |
| `--tolerate-malformed` | off | Exit 0 even when frames were rejected or undecodable (N230); the warning and the counts still report them. |
| `--live-seconds` | no limit | Stop after S seconds of listening (above 0, at most 86400; N230), whatever has arrived. With `--ticks N`, stopping on time with fewer than N frames is exit 1 (after the summary); without `--ticks`, a run that received no frame at all says so on stderr. |

With neither `--measurements-dir` nor `--out-store` it decodes only. Exit 2 on a missing or unknown protocol, a missing
`--crosswalk`, `--monitor` with a sink, or a bad `--live-seconds`; exit 1 if the crosswalk, the store or the transport cannot be opened (the
crosswalk load also refuses a 32-bit binding on register 65535 or one whose second register is bound separately, and
an address bound more than once, naming each such address), if any frame was malformed (without
`--tolerate-malformed`), or if `--live-seconds` ran out short of `--ticks`; exit 3 if any window was discarded because a point did not
fit its binding (`map-errors`; the listener keeps running and prints why); otherwise exit 2 with `REJECTED (forward
inference): N window(s) newer than the 169 h horizon were not written to --measurements-dir` when `--measurements-dir`
was given and any window was held (the listener still runs to the end and prints the summary first). A run without
`--measurements-dir` (decode only, `--monitor`, or `--out-store` alone) holds live windows by design and does not set
that exit code.

## Benchmarking and preflight verbs

These verbs are used in [ch. 28, Benchmarking and GPU](<0028 Benchmarking and GPU.md>); `audit` is in
[ch. 26](<0026 Audit log, deployment and assurance.md>).

### bench

`gridsim bench [--runs N] [--parallel N] [--out <path>] [--note "text"] [--cases a,b,c] [--gpu]`

The solver x backend matrix over the whole case archive (embedded classics plus every `.m` under `data/matpower/`).
Writes Markdown, CSV and `<report>.run-metadata.json` beside the report (`results/benchmark-results.run-metadata.json`
by default), with the run's settings under `parameters` ([Output files](#output-files)). GPU engines are opt-in through
the global switch and skip cases above 3,000 buses: `--gpu` (or `--gpu=auto`) probes ROCm, OpenCL and CUDA,
`--gpu=rocm`, `--gpu=opencl` or `--gpu=cuda` probes that vendor only, and `--gpu=cpu` adds none. A vendor asked for
whose device does not answer gets a report line saying so. The CPU engines are `cpu-dense` (dense LU), `cpu-sparse`
(direct sparse LU, ILU0-BiCGSTAB fallback) and `bfs` (radial cases only).

Each cell first runs one untimed warm-up solve of the same case on the same engine, so the mean excludes JIT
compilation, GPU kernel compilation and first-touch allocation (`bench --perf` and `bench --estimate` warm up the same
way). The cell's `conv / iters / mismatch` are those of the first timed run. A cell timed once (cases above 4,000
buses) therefore takes twice its solve time.

Every cell is run and reported whatever happens to the others; the exit code is the batch rule. Exit 1 when any case
could not be loaded (a `LOAD FAIL` row); otherwise exit 3 when any solve did not converge (`**NO**`), ran out of
memory or threw (`OOM`, `ERR:`); otherwise 0. `n/a` cells (the radial sweep on a meshed case) and GPU size skips are
not failures. The failed cells are listed on stderr at the end.

| Flag | Default | Meaning |
|---|---|---|
| `--runs` | 10 | Timing repeats per case (auto-reduced for big cases); at least 1. |
| `--parallel` | 0 (all cores) | Worker threads (GPU engines always run serially); a negative value is refused. |
| `--out` | `results/benchmark-results.md` | Report path (CSV alongside). Pass `docs/developer/benchmarks/benchmark-results.md` to refresh the committed copy. |
| `--note` | - | Free-text note stamped into the header. |
| `--cases` | the whole archive | Bench only these cases, a comma list of case arguments (built-in names, `.m` files, case folders), in the order given. A case that does not load is a `LOAD FAIL` row (exit 1 at the end). |

### bench --estimate

`gridsim bench --estimate [--runs N] [--cases a,b,c] [<case-dir> ...]`

Times the WLS estimator per case per step backend (dense versus sparse, forced through the sparse threshold) on a
fixed-seed synthetic set; sampling and the truth solve sit outside the timed region. Extra positional JSON case
directories are appended.

| Flag | Default | Meaning |
|---|---|---|
| `--runs` | 3 | Timing repeats (reduced above 1,000 buses). |
| `--cases` | `case14,case30,case57,case118,case300` | Case list. |

Output: `results/bench-estimate/bench-estimate.csv` plus a console table. Dense is skipped above 1,500 buses. Exit 1
when a case could not be loaded (`[skip]`); otherwise exit 3 when a truth solve or an estimate did not converge;
otherwise 0. The failures are listed on stderr at the end.

### bench --perf

`gridsim bench --perf [--runs N] [--out <path>] [--note "text"]`

The focused before/after yardstick for the performance programme: single-threaded, warmed once, median of N runs, with
managed allocations per run on the measuring thread. Rows: sparse Newton-Raphson end to end (case118, case300), the
three-phase fault study (case300), the OLTC outer loop (case118) and the many-order harmonic scan (case118). Writes
Markdown and CSV keyed by commit.

| Flag | Default | Meaning |
|---|---|---|
| `--runs` | 20 | Timed repeats (minimum 1). |
| `--out` | `results/perf-baseline.md` | Report path (CSV alongside). Pass `docs/developer/benchmarks/perf-baseline.md` to refresh the committed copy. |
| `--note` | - | Free-text note stamped into the header. |

### gpucheck

`gridsim gpucheck`

Fail-fast GPU preflight: reports CPU, ROCm, OpenCL and CUDA availability and runs one tiny case9 solve (dense and
sparse) on each present backend, flushing every step so a crash names the failing call. No flags. Exit 3 when any
probe that ran failed (a `FAIL` line, or a solve that ran but did not converge), listed on stderr; a backend that is not
present is skipped, not failed, so a machine with no GPU exits 0 (`gpucheck: OK - every probe that ran passed`). See
[ch. 27, Validation tools](<0027 Validation tools.md>).

### simdcheck

`gridsim simdcheck [--expect <tier>]`

Report the CPU's vector capabilities and the active tier (`Avx512`, `Avx256`, `Sse128` or `Scalar`, with the number of
doubles per vector step) that the dense-LU, normal-equations and covariance kernels dispatch to at run time. When the
CPU supports AVX-512F but the active tier is narrower (the .NET default on Skylake-SP and Cascade Lake Xeons), it prints
a warning naming the fix: set `DOTNET_PreferredVectorBitWidth=512` before start-up.

| Flag | Default | Meaning |
|---|---|---|
| `--expect` | - | Gate mode: exit 3 when the active tier is narrower than the given one (`Avx512`, `Avx256`, `Sse128`, `Scalar`; case-insensitive). Any other value exits 2. Use it in CI on a box that should run 8-wide. |

### audit

`gridsim audit verify <file> [--anchor-count N --anchor-head HEX] [--pubkey <public.pem> [--anchor <file>] [--sig <file>]]`
`gridsim audit show <file> [--anchor-count N --anchor-head HEX]`
`gridsim audit sign <file> --key <private.pem> [--anchor <file>] [--sig <file>]`
`gridsim audit keygen <private.pem> <public.pem>`

Verify a tamper-evident audit log (`gridsim-audit/1`, [technical ch. 31](<../technical/0031 Audit log and diagnostics.md>)):
recompute the hash chain line by line. Prints `OK <file>: N entries, head H` and exits 0 when it verifies; prints
`TAMPERED <file>: entry i: <reason>` and exits 3 at the first edited, deleted, inserted, reordered or truncated entry;
exits 1 when the file is missing or unreadable and 2 on a usage error. Tail truncation and a whole-log rewrite keep a
self-consistent chain, so they are caught only against an anchor kept outside the log: pass the entry count and head
hash from an earlier verify, from the `audit:` line a run printed, or from the log's anchor file
(`gridsim-audit-anchor/1 count=N head=H`, see `--audit-anchor`). The audited verbs and the web host also check the
anchor file themselves before they append.

`show` runs the same verification and, only when the whole log verifies, first prints every entry, one per line, as
`#<seq> run=<UTC> state=<UTC or -> provenance=<tag> hash=<hash> <summary>` (the summary is the recorded `actor=...
action=... inputs=... outcome=...` text), then the same `OK` line. A log that does not verify prints no entries at all,
only the `TAMPERED` line, and exits 3: nothing after a broken link can be trusted. Exit codes are as for `verify`.

| Flag | Default | Meaning |
|---|---|---|
| `--anchor-count` | - | The entry count of a trusted earlier state (with `--anchor-head`; one without the other exits 2, and a negative count exits 2). |
| `--anchor-head` | - | The head hash of that state (with `--anchor-count`). |
| `--pubkey` | - | `verify` only: an ECDSA P-256 public key (PEM). The signature file over the anchor file must exist and verify (`TAMPERED`, exit 3, otherwise), then the log is verified against that anchor. Does not combine with `--anchor-count` / `--anchor-head` (exit 2). |
| `--anchor` / `--sig` | `<log>.anchor` / `<log>.anchor.sig` | `verify --pubkey` and `sign`: the anchor file and its detached signature file. |
| `--key` | - | `sign` only: the ECDSA P-256 private key (PEM). `sign` verifies the log against its anchor first and signs nothing when it does not verify (exit 3). |

`keygen` writes a fresh P-256 key pair (the private key for the signing station, the public key for the host) and
refuses to overwrite an existing file (exit 1).

### compliance

`gridsim compliance verify`

Print this build's compliance attestation and exit 0 when the build is compliant, 1 when it is not. The attestation
binds the build identity (the release version, the git commit, and a deterministic build hash) to the compliance
manifest (the inventory of controls the build provides, each tied to the test or artefact that evidences it), reports
the SBOM hash a release build was produced against (`unset` on a local build), and states how many of the controls
GridSim owns are Achieved. A release build is compliant only when every in-scope control is Achieved and its evidencing
test is green, so a non-compliant build cannot truthfully attest compliance. Needs no licence and makes no network
call, so a reviewer can always ask a binary to attest itself. `show` is an accepted alias of `verify`; any other
subcommand exits 2. See [technical ch. 31](<../technical/0031 Audit log and diagnostics.md>).

### batchsolve

`gridsim batchsolve [--count N] [--n M] [--runs K] [--gpu]`

Solve N same-sized `MxM` dense systems in one batched dispatch on the selected backend (the GPU batched
Gaussian-elimination kernel on a CUDA or OpenCL device, else a parallel CPU loop over the reference LU; ROCm uses the
CPU loop), then verify the result against the CPU reference and report timing. A demonstrator and micro-benchmark for
the batched dense solver seam that turns a contingency sweep of many same-sized outages into a handful of batched
dispatches instead of hundreds of locked single solves. A singular system is isolated (its segment returns `NaN`) and
never fails the whole batch.

| Flag | Default | Meaning |
|---|---|---|
| `--count` | 64 | Number of independent systems in the batch (N); values below 1 are raised to 1. |
| `--n` | 64 | Dimension M of each dense `MxM` system; values below 1 are raised to 1. |
| `--runs` | 5 | Timing repeats (median reported); values below 1 are raised to 1. |
| `--gpu` | off | Select the GPU batched backend (the global `--gpu`); falls back to the CPU batched reference with no device. |

Exit 1 if the batched result differs from the CPU reference by more than 1e-6 or disagrees on which systems are
singular; exit 2 if `count x n x n` exceeds the single-dispatch limit.

## Electrical references

### ref

`gridsim ref encode <value> [--words N] [--check]`
`gridsim ref decode <reference> [--words N] [--check]`
`gridsim ref scatter <number|reference> --seed S [--words N] [--check]`
`gridsim ref from-id <id> [--words N] [--check]`

Electrical references: a number written as electrical words that can be read aloud and typed back, one word per byte
from a fixed list of 256 (units, pioneers, materials, physics, signals and more; no switching, safety, phase, colour,
asset, fuel or alarm words), most significant byte first, joined by dots: `gridsim ref encode 0x3C042C` prints
`copper.tesla.kron`. Three words give 16,777,216 references, four give 4,294,967,296.

- `encode` writes a value (decimal, or hexadecimal after `0x`) as words; a value too large for the word count is
  refused.
- `decode` reads a reference back and prints its value in decimal and hexadecimal. It is strict: lower case only, dots
  between words and nowhere else, exactly the expected number of words. An unknown word is refused with the nearest
  valid words by edit distance.
- `scatter` maps sequential asset numbers to unrelated-looking references with no two ever the same (a keyed
  permutation; the same seed gives the same references on every machine). Given a number it prints the reference;
  given a reference it prints the number back.
- `from-id` prints the reference that follows from an existing id (the leading bytes of the SHA-256 of its UTF-8
  text). Different ids can share a reference; the verb does not check, so compare references across a set of ids
  before relying on them.

| Flag | Default | Meaning |
|---|---|---|
| `--words` | 3 | Data words (1 to 8; 2 to 4 for `scatter`). |
| `--check` | off | Append (on decode: expect and verify) a check word, the CRC-8/SAE-J1850 of the data bytes, which catches any single wrong word and any swap of two neighbouring words. |
| `--seed` | none | The key of the `scatter` arrangement (decimal, or hexadecimal after `0x`); required by `scatter`. |

Needs no case, no licence and no network. Exit 1 when a reference is refused (the reason is printed); exit 2 on a
usage error (unknown subcommand or flag, missing or malformed value, value too large for the word count).

## Limits

- `acopf` solves the DC problem and adds a loss loop; it does not solve the full AC equations. It neither optimises
  nor limits bus voltages, reactive power or AC branch flows. `acopf-full` is the full AC optimal power flow.
- `acopf-full` optimises polynomial costs only (a piecewise-linear cost is refused), keeps taps and phase shifts at their
  case values and does not enforce branch angle difference limits.
- `gda-replay` takes no price series, so its `--policy price` and `--policy lp` exit 2.
- `cycle --pipeline` and `modal --include-soc` are not implemented and exit 2.
- `interpose` selects `iec104` and `modbus` only; DNP3 and IEC 61850 GOOSE exist as link-framing scaffolding with no
  application layer (R089).
- `stability`, `modal` and `impedance-stability` use the Anderson-Fouad X'd on `case9` and a synthetic 0.30 pu on every
  other case, flagged INDICATIVE. `modal` refuses MATPOWER cases because they carry no inertia constants.
- `inertia-placement` uses the synthetic GENROU machines with the field and the mechanical power held (no AVR, no
  governor) and models a loss as a constant-admittance load step; its answers are INDICATIVE in the same way.

## See also

- [ch. 1, Getting started](<0001 Getting started.md>) - what `gridsim` means when it is not installed.
- [ch. 2, Loading networks](<0002 Loading networks.md>) - built-in cases, MATPOWER files, JSON folders.
- [ch. 3, Configuration](<0003 Configuration.md>) - environment variables and paths.
- [ch. 4, Licensing and activation](<0004 Licensing and activation.md>) - tiers and the `licence` verb.
- [ch. 5, Running power flows](<0005 Running power flows.md>) - solver selection and reading solve output.
- [ch. 6, OPF, CPF, taps, distribution factors and losses](<0006 OPF, CPF, taps, distribution factors and losses.md>) - the network analysis verbs in use.
- [ch. 7, Three-phase distribution feeders](<0007 Three-phase distribution feeders.md>) - `dist3ph`.
- [ch. 11, Contingency analysis](<0011 Contingency analysis.md>) - trip, screen, nminus1, stress, chain, cycle in use.
- [ch. 12, Faults, short circuit and protection grading](<0012 Faults, short circuit and protection grading.md>) - `fault`, `iec60909`, `protection-check`, `grade`.
- [ch. 13, Battery storage studies](<0013 Battery storage studies.md>) - the `--bess` sites of `chain` and `gda-replay`.
- [ch. 14, Frequency studies](<0014 Frequency studies.md>) - `freq`, `replay`, `transient`.
- [ch. 15, RMS, modal and impedance stability](<0015 RMS, modal and impedance stability.md>) - `stability`, `modal`, `impedance-stability`.
- [ch. 16, Electromagnetic transients](<0016 Electromagnetic transients (EMT).md>) - the `emt` verb in depth.
- [ch. 17, Harmonic studies](<0017 Harmonic studies.md>) - the `harmonics` verb in depth.
- [ch. 18, Replaying recorded events](<0018 Replaying recorded events.md>) - `replay-event`.
- [ch. 20, Counterfactuals](<0020 Counterfactuals.md>) - the reproduced-baseline gate as a usage rule.
- [ch. 21, State estimation](<0021 State estimation.md>) - `estimate`.
- [ch. 22, Estimating GB history](<0022 Estimating GB history.md>) and [ch. 24, The GDA bridge](<0024 The GDA bridge.md>) - `gda`, `gda-replay`.
- [ch. 25, Interposing live telemetry](<0025 Interposing live telemetry.md>) - `interpose` and `serve`.
- [ch. 26, Audit log, deployment and assurance](<0026 Audit log, deployment and assurance.md>) - `--audit-log` and `audit`.
- [ch. 27, Validation tools](<0027 Validation tools.md>) - `crossval`, `physics-check`, `conformance`, `simdcheck`.
- [ch. 28, Benchmarking and GPU](<0028 Benchmarking and GPU.md>) - `bench`, `batchsolve`, `gpucheck`.
- [ch. 30, Troubleshooting](<0030 Troubleshooting.md>) - messages and exit codes.
- [ch. 31, Exports and formats](<0031 Exports and formats.md>) - every export and run-directory file.
