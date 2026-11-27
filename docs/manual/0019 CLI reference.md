# 19. CLI reference

Every verb and flag the `gridsim` command line tool accepts, checked against the argument parsing in
`src/GridSim.Cli/Program.cs`, `VerbHelp.cs`, `VerbFlags.cs`, `LicenceCli.cs` and `StorageCli.cs` (the project is
`GridSim.Cli`; the binary it builds is `gridsim`). `gridsim` means the installed binary; from a checkout it is
`dotnet run --project src/GridSim.Cli -c Release --` ([ch. 1](<0001 Getting started.md>)).

This chapter is compiled into the binary. `gridsim <verb> --help` prints the verb's section, and the flags a section
declares (in its usage line or the first column of its flag table) are the flags the verb accepts (see [Parsing rules](#parsing-rules-and-exit-codes)), so a flag the code reads
always has a row here.

## Verb index

Verb names are case-insensitive (`licence` excepted, which must be lower case). Every verb dispatched in `Program.cs`
has a section below.

| Group | Verbs |
|---|---|
| Software licence | [`licence`](#licence) |
| Help | [`help`](#help) (and `gridsim <verb> --help`) |
| Running a case | [no verb](#running-a-case-no-verb) |
| Contingency and analysis | [`trip`](#trip), [`screen`](#screen), [`nminus1`](#nminus1), [`nminus2`](#nminus2), [`n-1-1`](#n-1-1), [`nminus1-sweep`](#nminus1-sweep), [`stress`](#stress), [`chain`](#chain), [`cycle`](#cycle), [`corrective-sequence`](#corrective-sequence) |
| Network analysis | [`pf`](#pf), [`ptdf`](#ptdf), [`lodf`](#lodf), [`sensitivity`](#sensitivity), [`qv-curve`](#qv-curve), [`pv-contingency`](#pv-contingency), [`dc-contingency`](#dc-contingency), [`fault-level-n1`](#fault-level-n1), [`line-constants`](#line-constants), [`dist3ph-mesh`](#dist3ph-mesh), [`substation-state`](#substation-state), [`cgmes-export`](#cgmes-export), [`cgmes-conform`](#cgmes-conform), [`cgmes-shacl`](#cgmes-shacl), [`dgs-import`](#dgs-import), [`dcpf`](#dcpf), [`dcopf`](#dcopf), [`acopf`](#acopf), [`acopf-full`](#acopf-full), [`scuc`](#scuc), [`scopf`](#scopf), [`acopf-opt`](#acopf-opt), [`acscopf`](#acscopf), [`reconfig`](#reconfig), [`dist-opt`](#dist-opt), [`reduce`](#reduce), [`qv-modal`](#qv-modal), [`transmission-security`](#transmission-security), [`transfer-capability`](#transfer-capability), [`cgmes-study`](#cgmes-study), [`planning-study`](#planning-study), [`study-case`](#study-case), [`case-diff`](#case-diff), [`result-diff`](#result-diff), [`report`](#report), [`restoration-study`](#restoration-study), [`reliability-study`](#reliability-study), [`reliability-network`](#reliability-network), [`probabilistic`](#probabilistic), [`qsts`](#qsts), [`hosting`](#hosting), [`cpf`](#cpf), [`taps`](#taps), [`restore`](#restore), [`losses`](#losses), [`dist3ph`](#dist3ph) (experimental) |
| Physics and protection | [`physics-check`](#physics-check), [`conformance`](#conformance), [`protection-check`](#protection-check), [`grade`](#grade), [`devices-check`](#devices-check), [`fault-line`](#fault-line), [`sc-trace`](#sc-trace), [`protection-audit`](#protection-audit), [`protection-eval`](#protection-eval), [`protection-grade`](#protection-grade), [`fault`](#fault), [`fault-series`](#fault-series), [`fault-simultaneous`](#fault-simultaneous), [`iec60909`](#iec60909), [`g74`](#g74), [`arc-flash`](#arc-flash), [`cable-rating`](#cable-rating), [`experimental`](#experimental) |
| Frequency and dynamics | [`freq`](#freq), [`replay`](#replay), [`transient`](#transient), [`domains`](#domains), [`stability`](#stability), [`modal`](#modal), [`modal-full`](#modal-full), [`fit-frequency`](#fit-frequency), [`inertia-placement`](#inertia-placement), [`dynamics-study`](#dynamics-study), [`grid-code`](#grid-code), [`motor-start`](#motor-start), [`user-model`](#user-model) |
| Harmonics, impedance and EMT | [`harmonics`](#harmonics), [`harmonic-assess`](#harmonic-assess), [`flicker`](#flicker), [`filter-design`](#filter-design), [`impedance-stability`](#impedance-stability), [`emt`](#emt), [`emt-line`](#emt-line), [`emt3`](#emt3), [`cosim`](#cosim), [`emt-partition`](#emt-partition), [`harmonic-pf`](#harmonic-pf) (experimental), [`harmonic-se`](#harmonic-se) (experimental) |
| Evidence | [`replay-event`](#replay-event), [`scenario`](#scenario), [`h0-study`](#h0-study), [`fleet`](#fleet), [`evidence`](#evidence) |
| Data | [`gda`](#gda), [`gda-replay`](#gda-replay), [`synth`](#synth), [`export`](#export), [`convert`](#convert), [`validate`](#validate), [`zonal`](#zonal), [`pmu-ingest`](#pmu-ingest) (experimental), [`pmu-estimate`](#pmu-estimate), [`estimate`](#estimate), [`estimate-noise-floor`](#estimate-noise-floor), [`crossval`](#crossval) |
| Real-time and telemetry | [`serve`](#serve), [`rpc`](#rpc), [`interpose`](#interpose) |
| Benchmarking and preflight | [`bench`](#bench), [`bench --estimate`](#bench---estimate), [`bench --perf`](#bench---perf), [`gpucheck`](#gpucheck), [`simdcheck`](#simdcheck), [`batchsolve`](#batchsolve), [`audit`](#audit), [`compliance`](#compliance), [`breaking-change`](#breaking-change) |
| Electrical references | [`ref`](#ref) |

`gridsim --help` (or `-h`, or `gridsim help`) prints the verb list, then exits 0. `gridsim <verb> --help`,
`gridsim <verb> -h` and `gridsim help <verb>` print that verb's section of this chapter (see [help](#help)).

## Parsing rules and exit codes

- **Unknown flag names fail loud.** Every `--flag` is checked against the set of flags the CLI knows. A misspelling
  prints `error: unknown flag '<flag>' (misspelled?)` and exits 2. The message names `gridsim <verb> --help` when the
  first word is a verb (`gridsim --help` otherwise) and this chapter, `docs/manual/0019 CLI reference.md`.
- **A flag of another verb fails loud too.** Each verb takes the flags its own section below declares in its usage
  line or in the first column of its flag table (the text `gridsim <verb> --help` prints), those of its group's shared
  notes, the [global switches](#global-switches) and, on the [result-file](#result-files) verbs, `--json`, `--csv` and
  `--out`. A flag named only in passing in a section's prose (another verb's, for example) is not one of that verb's
  flags (GR-194; it used to be accepted and ignored). Any other flag prints `error: <verb> does not
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
| 1 | Input error: a missing or unloadable file or case folder, invalid data in a case (for example a generator whose Pmin is above its Pmax, refused by `dcopf` and `acopf`), an invalid measurement set or window file (`serve` skips it and exits 1 at the end), a case that fails to load inside a batch run (`bench`, `nminus1-sweep`, `stress`, `transient --cases`), a requested [result file](#result-files) or any other output that cannot be written (read-only, locked by another program, access denied, a full disk), named in one line (`error: could not write the output ...`), never an internal error. Also a failed check: the physics-law gate (`physics-check`, `conformance`), a right-half-plane eigenvalue (`modal`), a missed High tier (`cycle`), a batched result off the CPU reference (`batchsolve`), a singular harmonic network (`harmonics`), a violated experimental law, a bus the `iec60909 --all-buses` sweep cannot study. |
| 2 | Usage error: an unknown flag, a flag of another verb, a bad flag value, an unknown verb, an unknown case name ([one rule on every verb](#case-arguments)), a bus id that is not in the case (`stability --fault-bus`, `fault --bus`, `protection-check --fault-bus`, `iec60909 --bus`, a `--bess` site), a licence refusal (a network above the tier's bus limit, or a key `licence import` rejects), a forward-inference rejection (`REJECTED (forward inference)`, on every verb that reads dated data). |
| 3 | A verdict or convergence failure: a baseline that is not `Reproduced`, an estimate that is not converged or not observable, a `crossval` mismatch, non-converged steps (`gda-replay`, `transient`, `chain`), an EMT numerical failure, an infeasible dispatch (`dcopf`, `acopf`), a contingency verdict that is not secure (`nminus1`, `nminus2`, `n-1-1`, `nminus1-sweep`: the base case or any screened outage violates a limit, islands or does not solve; `screen`: any loss breaches a frequency limit), and a power flow that did not converge on the verbs that report it: the default solve report, `--enableExport`, a `--ticks` run with any tick that did not converge, `pf`, `losses`, `taps`, `cpf` (the base load does not converge), `gda --solve` and `--all-gsps`, `serve` with failed windows, `fault` and `protection-check` (including a converter current iteration that does not converge), the stability and modal verbs, `impedance-stability`, the experimental models and `conformance` (a case that stops converging or cannot be run). Also a failed probe or solve in `gpucheck`, `bench` and `bench --estimate`. |
| 4 | `breaking-change validate` only: review needed. The only results that moved did so under a breaking change with no bound (UNBOUNDED, see [breaking-change](#breaking-change)), and nothing else is wrong. |
| 77 | `crossval` only: the environment cannot run the check (no Python, or no pandapower). |
| 78 | The telemetry guard refused to start, before any verb, `--version` or help ran: a telemetry library or injected agent is loaded, an environment variable would switch on an exporter, configure an APM agent or load an agent (for example `SENTRY_DSN`, `OTEL_EXPORTER_OTLP_ENDPOINT`, `DD_AGENT_HOST`, `DOTNET_STARTUP_HOOKS`, `CORECLR_ENABLE_PROFILING=1`), or the binary lacks the build marker. One line on stderr names the reason: `gridsim: telemetry guard: <reason>. Exiting with code 78.` The full lists are in [technical ch. 35, section Telemetry](<../technical/0035 Deployment and security.md#telemetry>). |

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

The verbs: `dcopf`, `acopf`, `acopf-full`, `scuc`, `scopf`, `qsts`, `hosting`, `cpf`, `taps`, `restore`, `ptdf`, `lodf`, `dcpf`, `iec60909`, `g74`, `fault`, `protection-check`,
`grade`, `nminus1`, `nminus2`, `n-1-1`, `cycle`, `freq`, `domains`, `inertia-placement`, `dist3ph`, `losses`, `physics-check`,
`experimental`, `convert` (`--json` and `--csv` only: its second argument is already its output), `validate`, `zonal`,
`probabilistic`, `reliability-network`, `flicker`, `filter-design`, `harmonic-assess`, `connection-assess`, `study-case`, `case-diff`,
`result-diff` and `report` (`--json` and `--csv` only: their own `--out` still names their own output),
`pf`, `sensitivity`, `qv-curve`, `pv-contingency`, `dc-contingency`, `fault-level-n1`, `pmu-ingest`, `pmu-estimate`,
`protection-eval`, `protection-grade`, `cgmes-shacl`, `breaking-change`, and `acopf-opt`, `acscopf`, `reconfig`, `grid-code` and `reduce`
(`--json` and `--csv` only: their own `--out` still names their own output). `serve` does not take them: it is a resident stream that reports each measurement window as it
arrives and may run without end, so one document written when it stops does not fit it. On every other verb `--json`
and `--csv` exit 2 (`modal`'s own `--json` and `screen`'s own `--csv` excepted: `screen --csv <file>` writes its
screening table, and `modal --json` prints JSON on the console and takes no file, `modal --export <file.json>` being
its file output).

- **What is written.** The verb's summary values and its tables, from the same values it prints, but complete: where
  the console lists the top N rows (dispatch, distribution factors, outages, branch losses, traced loads), the file
  has every row, and no number is rounded to the console's display precision.
- **JSON.** One document: `{"schema": "gridsim-cli-result/1", "tool": "gridsim <version>+<commit>", "buildStatus",
  "verb", "args", "exitCode", "summary": {key: value}, "tables": {name: [{column: value}, ...]}}`. Summary keys and
  column names are snake_case. `buildStatus` says what kind of build wrote the file: `"RELEASE"` for a release build
  that names its exact release checkout (built by the release workflow, with the SBOM hash injected, from a clean
  checkout whose full commit is the one stamped in `tool`), `"UNSUPPORTED BUILD"` for any other build, such as a
  local build or a build of changed source. Compliance is a separate statement, reported by `gridsim compliance
  verify`; it does not change `buildStatus`. Like `tool`, it describes the build, not the result. With the global switch `--timings` the document ends with one more property, `"timings": {...}`:
  wall-clock milliseconds per stage (`startup`, `parse`, `build`, `assemble`, `factor`, `solve`, `write`, `total`),
  the counts the verb exposes, `stages_merged` (stages folded into the combined `solve`) and `stages_not_measured`;
  see [Global switches](#global-switches). Without the switch the document has no such property.
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

For many calls on one case, `gridsim.Session` starts `gridsim rpc` once and keeps it: `sess.load("case9")` returns a
`CaseHandle`, `handle.solve()` re-solves it without reloading, `sess.run_verb("pf", "case14")` returns the same `Result`
as `gs.run`, and `sess.line_constants(geometry)` evaluates a line geometry in process. The numbers are the same bits as
the subprocess path; see [rpc](#rpc). `GridSim.run` is unchanged. `run_verb` makes the same flag check as `gs.run`:
`--json` and `--csv` are refused, and `--out` passes for the verbs whose own output it names (`OWN_OUT_VERBS`).

A `CaseHandle` (and a `Case` below) belongs to the session that loaded it and to that session's host instance. Every
host numbers its level 1 handles `case-1`, `case-2`, ..., so the id alone cannot tell two hosts apart: a session refuses a
handle of another session, or one loaded before its host was restarted (`close()` then `start()`), with `GridSimError`
before anything is sent, and `release()` of it marks nothing released. A plain id string is still accepted.

**The object model and psspy.** `gridsim.ModelSession` starts `gridsim rpc --protocol 2` (see [rpc](#rpc)) and refuses a host of another protocol.
`ms.open(case)` gives a `Case` whose `buses`, `generators`, `loads`, `branches` and `shunts` are read-only mappings of
`Element` objects, keyed and with the attributes the rpc section lists. Reading an attribute asks the host; assigning one
writes it, and a refused write raises `GridSimError` and changes nothing. `case.solve()` gives a `LoadFlowResult`
(`converged`, `bus(id).vm`, `bus(id).va`, `branch(key).p_from`, `generator(key)` for a unit's solved P and Q);
`case.contingency(...)`, `case.short_circuit(...)` and
`case.opf(...)` run `nminus1`, `iec60909` and `acopf-full` on the case as edited and give the verb's own `Result`;
`case.save(path, "raw33"|"raw34"|"raw35"|"v2")` writes it; `case.rms(study.json)` runs `dynamics-study`.
`case.add(kind, key, **values)` and `case.delete(kind, key, cascade=False)` add and delete elements: a bus by its id
(`base_kv` required), a generator as `bus:id`, a load or shunt by its bus id (one per bus), a branch as `from-to:circuit`
(`x` required). A bus with branches, units, a load or a shunt is deleted only with `cascade=True`, which deletes them first.
Technical chapter 41 gives the rules.

```python
with gridsim.ModelSession() as ms:
    case = ms.open("data/matpower/lib/case9.m")
    case.loads["9"].pd = 0.0               # a set request; solve() then solves the edited case
    r = case.solve()
    r.bus(9).vm, r.branch("8-9").p_from
    case.short_circuit("--bus", 5).summary["ikpp_ka"]
```

`from gridsim import psspy` (never imported by `import gridsim`) maps a subset of psspy calls onto the object model. Return
values follow psspy, `ierr` or `(ierr, value)`. For most calls the non-zero ierr values are GridSim's own, not PSS/E's: 1
element not found, 2 bad argument or string, 3 not solved or stale (the case was edited after the last solve), 9 not
supported. The deletes (`purgbrn`, `purgmac`, `purgload`, `purgshunt`), the complex arrays (`abuscplx`, `amachcplx`,
`abrncplx`) and `inimac` return PSS/E's own codes for every condition the API manual names (listed below); a request
PSS/E would answer and GridSim cannot returns 9, or 11 in `abrncplx`, where PSS/E's 9 means an invalid string. `_i`,
`_f` and `_s` are this module's "no change" defaults (`getdefaultint/real/char()`). Each argument layout is taken from a
public source, named in the code beside the call: the PSS/E 34 Application Program Interface manual as publicly posted
(its section), or a public example call on the psspy.org forum. A layout no public source confirms is refused with ierr 9,
never guessed. The mappings are checked against GridSim's own verbs, not against PSS/E. Only the forms below are
accepted. `psspy.last_error()` says why the last dynamic call returned ierr 9.

| psspy call | GridSim |
|---|---|
| `psseinit(buses)` | Starts the ModelSession; the bus count is ignored. |
| `read(numnam, ifile)`, `readrawx(ifile)` | `load` of a RAW (30, 33 to 36) or RAWX file. |
| `readrawx(...)` | ierr 9: no public source confirms its arguments. Open a RAWX file with `ModelSession.open`. |
| `writerawversion(vernum, out, ofile)` | `save` as RAW 33, 34 or 35 to `ofile` with `out` 0 (a file); `out` 1 (the report window) or any other version: ierr 9. |
| `rawd_2(sid, all, out=0, ofile=...)` | `save` as RAW 33, keyword form only; a positional status or option array: ierr 9 (its layout is unconfirmed). |
| `fnsl(options)` | `solve` (Newton, reactive limits on); ierr 0 when it ran, converged or not (`solved()` tells). Any option other than the defaults: ierr 9, never ignored. |
| `solved()` | 0 converged, 1 not converged, 3 no solve since the last edit. |
| `busdat(ibus, string)` | `PU`, `KV`, `ANGLE` (radians), `ANGLED`, `BASE`. |
| `busint(ibus, string)` | `TYPE`. |
| `gendat(ibus)` | The in-service generation at the bus in the solved state: the bus's net injection plus its demand. |
| `macdat(ibus, id, string)` | `P`, `Q`, `MVA`: the machine's solved output (ierr 3 before a solve; ierr 9 for a unit sharing its bus with another in-service unit). `PMAX`, `PMIN`, `QMAX`, `QMIN`, `MBASE`: the unit's data. |
| `loddt2(ibus, id, string1, string2)` | string1 `MVA`, `IL`, `YL` or `TOTAL`, string2 `NOM` or `ACT`. Answered for a bus holding exactly one load record with this id, constant power only: `MVA` and `TOTAL` are its P + jQ, `IL` and `YL` 0. `ACT` needs a solve and at least 0.7 pu at the bus (PSS/E's default PQBRAK). Several load records at the bus, an I or Y part, the `O_` units, `LDDGN`, `YNEG`, `YZERO`: ierr 9; an id the file does not hold there: ierr 1. |
| `brnmsc(ibus, jbus, ckt, string)`, `brnflo(ibus, jbus, ckt)` | `P`, `Q`, `MVA`, or P + jQ, at the ibus end of the solved case. The percent loadings (`PCTRTA` and the rest): ierr 9. |
| `abusint`, `abusreal` (sid negative; flag 1 or 2) | `NUMBER`, `TYPE`; `PU`, `KV`, `ANGLE`, `ANGLED`, `BASE`. |
| `abrnint`, `abrnreal`, `abrnchar` (sid negative; flag 3 or 4, entry 1) | `FROMNUMBER`, `TONUMBER`, `STATUS`; `P`, `Q`, `MVA` (from end), `MXPCTMVARATA`, `RATEA`; `ID`. GridSim does not tell a line from a two-winding transformer, so flags 1, 2, 5 and 6, and a case with a three-winding transformer, are ierr 9; so are the current-based percentages. |
| `aareaint`, `aareareal` (sid negative; flag 1, read as the areas with a bus in the case, in ascending number) | `NUMBER`; `PINT` (the solved net export in MW, tie lines only, each tie measured at its metered end as `pf --area-control` counts it). A RAW case only: the areas are read from the file. `PINT` needs a solve (ierr 3) and is ierr 9 for a case with three-winding transformers. The public example (psspy.org question 5541) gives the call shape and `PINT` with flag 1 only; flag 2 and the strings `SWING`, `PDES` and `PTOL` are confirmed by no public source, so they are ierr 9. |
| `amachint`, `amachreal`, `amachchar` (sid negative; flag 1 to 4) | `NUMBER`, `STATUS`; `PGEN`, `QGEN` (solved, as `macdat`), `MBASE`, `PMAX`, `PMIN`, `QMAX`, `QMIN`; `ID`. |
| `machine_chng_2(ibus, id, intgar, realar)` | intgar[0] status; realar[0..6] PG, QG, QT, QB, PT, PB, MBASE. |
| `load_chng_5(ibus, id, intgar, realar, lodtyp)` | intgar[0] status; realar[0..1] PL, QL; as `loddt2`, only for a bus holding one constant-power load record with this id. `lodtyp` other than blank: ierr 9. |
| `branch_chng_3(ibus, jbus, ckt, intgar, realari, ratings)` | intgar[0] status; realari[0..2] R, X, B; ratings[0] RATE1 (rate A). PSS/E refuses a two-winding transformer here; GridSim, which does not tell one from a line, edits either. |
| `bus_chng_4(ibus, inode, intgar, realar, name)` | intgar[0] type; realar[1] VM, realar[2] VA (the public layout: BASKV, VM, VA, ...). |
| `bus_data_4(ibus, inode, intgar, realar, name)` | Adds the bus or changes it. intgar [IDE, AREA, ZONE, OWNER]; realar [BASKV, VM, VA, NVHI, NVLO, EVHI, EVLO]. A new bus needs BASKV (ierr 2). Areas, zones and owners other than 1, the emergency limits, a node, a name, or a new BASKV for an existing bus: ierr 9. |
| `load_data_6(ibus, id, intgar, realar, lodtyp)` | Adds the load or changes it. intgar [STATUS, AREA, ZONE, OWNER, SCALE, INTRPT, DGENM]; realar [PL, QL, IP, IQ, YP, YQ, DGENP, DGENQ]: constant power only. A second load id at a bus (one load per bus), any other part, or a load type: ierr 9. |
| `machine_data_4(ibus, id, intgar, realar, name)` | Adds machine `ibus:id` or changes it. intgar[0] STAT; realar[0..6] PG, QG, QT, QB, PT, PB, MBASE; later slots or a droop name: ierr 9. A new unit takes the plant's VS; a PQ bus with an in-service unit becomes PV. |
| `plant_data_4(ibus, inode, intgar, realar)` | realar[0] VS sets the voltage set-point of the units at the bus, and of units added there later. Remote regulation, or RMPCT other than 100: ierr 9. The bus type is not changed. |
| `branch_data_3(ibus, jbus, ckt, intgar, realar, ratings, namear)` | Adds the line or changes it: R, X, B; RATE1 to RATE3 as rate A, B, C. Line shunts, LEN, other ratings, owners other than O1 = 1, or a name: ierr 9. |
| `two_winding_data_6(ibus, jbus, ckt, intgar, realari, ratings, namear, vgrpar)` | Adds the transformer or changes it: R1-2, X1-2 (system base), tap WINDV1/WINDV2, shift ANG1. CW, CZ or CM other than 1, tap control (COD1), a correction table, magnetising admittance, a name or vector group: ierr 9. |
| `shunt_data(ibus, id, intgar, realar)` | Adds a fixed shunt (GL, BL at 1 pu) or changes one added this way. One shunt per bus; a status other than 1: ierr 9. |
| `purgbrn`, `purgmac`, `purgload`, `purgshunt` | Delete the element. PSS/E's codes: 1 bus not found; `purgbrn` 2 branch not found; `purgmac` 2 no machines at the bus, 3 machine not found; `purgload` 2 no load at the bus, 3 another load id; `purgshunt` 2 no fixed shunt at the bus, 3 another shunt id. A branch a tap changer names, the last unit at a slack bus, a bus whose file holds several loads, or a shunt of the file (its id is not held): ierr 9. |
| `abuscplx`, `amachcplx`, `abrncplx` | `VOLTAGE` (solved, pu); `PQGEN` (solved, MW + j MVAr); `RX` (pu). Flags as the real arrays. PSS/E's codes: 2 SID above 11; 3 a flag PSS/E does not have (`abuscplx`, `amachcplx`), 5 (`abrncplx`); 6 an entry other than 1 or 2 (`abrncplx`); 6 (`abuscplx`, `amachcplx`) or 9 (`abrncplx`) a string PSS/E does not have. A subsystem, a valid flag or string GridSim does not map, or an unsolved case: ierr 9 (11 in `abrncplx`). The public `abrncplx` call sites use flag 1, which GridSim does not answer (it does not tell a line from a two-winding transformer); flags 3 and 4 follow the API manual. |
| `two_winding_data_6` WN1BUS | intgar[8] (WN1BUS) is the bus that carries WINDV1 and ANG1, ibus by default for a new transformer. A new transformer is held from that bus, so its tap and shift sit where PSS/E puts them. A transformer already held keeps its stored winding one when WN1BUS is not given, in whichever order the call names the ends: the PSS/E API defines a defaulted argument as "no input" (`getdefaultint`, as excerpted in the PowerMCP repository), so a defaulted WN1BUS gives no new winding one. WN1BUS that is neither end, or a given WN1BUS on the other end of a held transformer (with a winding voltage or angle): ierr 9. intgar[1] (METBUS) must be one of the two ends, as in `branch_data_3`: ierr 9 otherwise. |
| `inibus`/`nxtbus`, `inimac`/`nxtmac`, `inibrn`/`nxtbrn` | Iterate over the buses (number order), the machine ids at a bus (ascending) and the branches at a bus (by to bus and circuit). `nxtbus` gives GridSim's bus name, without PSS/E's base-voltage suffix. `inimac` gives PSS/E's 1 for no such bus and 2 for a bus whose type is not 2 or 3. |
| `dyre_new(status, dyrfile)` | Records the DYR file for `dynamics-study`. |
| `strt_2(options, outfile)`, `strt(option, outfile)` | Records t = 0 and checks the initialisation with a 0.05 s run with no event (ierr 3 when the machines cannot start). |
| `run(0, tpause)` | Re-runs one `dynamics-study` row from t = 0 to `tpause` with every event so far; `psspy.channels()` holds it. A run in steps gives the same bits as one run to the same time. With no event yet the row holds only the undisturbed time series to `tpause`: dynamics-study needs an event, so a branch trip is placed one step later and the samples after `tpause` are cut off. An edited case is first written as RAW 33, read back and compared (attribute by attribute, bit for bit; then written again and compared byte for byte apart from the titles); a case that does not survive: ierr 9. |
| `dist_bus_fault(ibus, units, basekv, values)` | A three-phase fault now: values [G, B] in MVA (units 1), Y = (G + jB)/SBASE pu with the case's own SBASE read from the RAW or RAWX file, Zf = 1/Y; G + jB of magnitude 1e9 MVA or more (PSS/E's default is -2e11) is a bolted fault. Units 2 and 3: ierr 9. |
| `dist_clear_fault(ifault)` | `faultClear` of the ifault-th fault applied. |
| `dist_branch_trip(ibus, jbus, ckt)`, `dist_machine_trip(ibus, id)` | `branchTrip`, `machineTrip`. |
| `voltage_channel`, `machine_array_channel` | Bus voltage; machine ANGLE (1) and SPEED (7). PELEC and the rest: ierr 9 (machine power is not traced). |
| `progress_output`, `report_output`, `alert_output` | Accepted and ignored. |
| `solv` | Gauss-Seidel (`pf --engine gs`): options as `fnsl` (any non-default is ierr 9). Bus results are the Gauss-Seidel solution; branch flows and machine outputs are read from the host's Newton solve of the same case, the same solution to the tolerance. After a solve that does not converge they are not served (ierr 3). |

Any slot of an array not listed holding a value other than the default is ierr 9. Refused with `NotImplementedError` and
the reason: `case` and `save` (the .sav binary is proprietary); `fdns`, `fncl` and `nsol` (PSS/E's decoupled
Newton methods are not confirmed to match GridSim's fast-decoupled load flow, so nothing is substituted; for that solution use
`case.study("pf", "--engine", "fdxb")`), `mslv` (no modified Gauss-Seidel engine); `accc*` and `dfax*` (use `case.contingency()`); `ascc*`,
`scmu*` and `iecs_4` (use `case.short_circuit()`); `opf*` (use `case.opf()`); `dist_scmu_fault`, `dist_spcb_fault`,
`dist_branch_fault` (unverified layouts; use `case.rms()` with a `sequenceFault` or `lineFault` event); `bsys` and any
subsystem other than sid -1 (the object model has no areas, zones or owners to select by); and any other psspy name. The PowerFactory Python API
is not mirrored: its object model is proprietary.

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

The study verbs that write their own result file keep a never-overwrite rule as well: an existing `--out` (file or
folder) is refused before the run, and nothing is replaced. Two exit codes apply. The JSON result verbs `harmonic-pf`,
`harmonic-se`, `gic`, `cosim`, `emt-partition` and `qv-modal` exit 2 with `error: <verb>: '<path>' already exists; name
a new file (it is never overwritten).` The study-file verbs (the specialist studies, the harmonic planning verbs,
`long-term`, `dynamics-study`, the reliability, restoration, transfer-capability and transmission-security studies, and
the other verbs that take a study JSON) exit 1 with `Result path already exists; use a new path for this invocation.`
Each verb's own section states its rule.

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
| A positional case only; `--dir` exits 2 as a flag of another verb. | `pf`, `ptdf`, `lodf`, `dcpf`, `dcopf`, `acopf`, `cpf`, `taps`, `restore`, `losses`, `physics-check`, `protection-check`, `fault`, `fault-series`, `fault-simultaneous`, `iec60909`, `g74`, `experimental`, `estimate`, `crossval`, `serve`, `convert` (first argument) |
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
read `--cpu` as the verb). The study verbs that read their options from a study file (`dynamics-study`,
`transmission-security`, `cable-rating`, `planning-study`, `study-case`, `breaking-change` and the others dispatched with
them) take `--cpu` before or after the verb (they run on the CPU), apply `--frequency-hz`, refuse `--gpu` naming it
(exit 2), and accept `--name=value` like every other verb.

| Flag | Default | Meaning |
|---|---|---|
| `--cpu` | on | CPU linear-solver backends: dense LU, and a direct sparse LU (`CpuSparseLuSolver`, minimum-degree ordering, partial pivoting) for the sparse engine. ILU0-BiCGSTAB runs only if the LU reports the step singular. |
| `--gpu` | off | Auto-select a GPU backend, AMD-first: ROCm, then OpenCL, then CUDA, else fall back to CPU with a warning. A GPU sparse solve that gives up is retried on the exact CPU LU before the ILU0-BiCGSTAB fallback. GPU arithmetic is outside the bit-identity contract. |
| `--gpu=rocm` / `--gpu=opencl` / `--gpu=cuda` | off | Force a vendor; an absent device (or an OpenCL device without FP64) exits 2 rather than falling back to the CPU (`bench` and `gpucheck` report it instead). `--gpu=auto` and `--gpu=cpu` are also accepted; any other vendor name exits 2. |
| `--inertia-model lumped\|distributed` | `lumped` | Frequency-dynamics inertia model (`lumped-scalar` and `scalar` are aliases of `lumped`; any other value exits 2). Only `trip` integrates the distributed model; `transient` reports the domain split but integrates lumped; `evidence`, `scenario`, `h0-study`, `fleet` and `replay-event` warn and run lumped. |
| `--asset-dynamics[=on\|off]` | off | Opt into the authored per-asset dynamics fleet (inverter FFR, Loss-of-Mains trips, placed protection devices). A bare flag means `on`. Honoured by `trip` and `transient`. A case with no asset bundle (no `asset-classes.json` / `asset-bindings.json` and no `protections.json`) has nothing to enable: exit 1. `trip` refuses it together with `--inertia-model distributed` (exit 2). |
| `--forming none\|vsm\|droop` | `none` | Grid-forming frequency formation. Honoured by `trip`, `transient`, `scenario` and `fleet`. `vsm` requires `--forming-inertia` above 0 (exit 2 otherwise). With `--inertia-model distributed` it warns and runs the lumped forming integrator. |
| `--forming-inertia <GVA.s>` | 0 | Virtual former inertia Ev. `vsm` only: with `--forming droop` it is refused (exit 2), a droop former carrying no virtual inertia (N311). |
| `--forming-damping <MW/Hz>` | 0 | Virtual former damping Dv. |
| `--frequency-hz <Hz>` | 50 (assumed) | The nominal frequency of every MATPOWER `.m` file the run loads (a path, a bundled `.m` name, or a built-in name backed by a file such as `gb-reduced-29`). A `.m` file carries no system frequency, so without the flag it is assumed to be 50 Hz and a parse note says so; the IEEE, WSCC and most MATPOWER library cases are 60 Hz systems (`gridsim modal data/matpower/lib/case9.m --frequency-hz 60`). It sets the f0 of every dynamic study of that model: the small-signal state matrix (`modal`), the swing equation of `stability` (single study and CCT), `trip`'s RoCoF, EMT branch inductances (`emt`), impedance scans and IEC 60909's frequency terms. It changes no power-flow, OPF or state-estimation number: the frequency enters none of those equations. Like `--cpu` it may stand anywhere on the line. The value must be a finite number above 0 Hz, given once (otherwise exit 2). For a PSS/E `.raw` file it is the base frequency when the file's BASFRQ is 0 or missing (otherwise 60 Hz, PSS/E's default, is assumed and noted); a file that states a different BASFRQ is refused (exit 1). It does not apply to a JSON case folder (its `system.json` carries `frequencyHz`) or to a built-in case name (each carries its own frequency); when the run loads no `.m` file at all, `warning: --frequency-hz <Hz> had no effect ...` is printed on stderr at the end and the exit code is unchanged. (N187, October 2026.) |
| `--psse-revision <n>` | from the file | The revision a PSS/E `.raw` file is read as when its first line does not say (no REV field and no `PSS/E-NN` comment, as in the UKGDS cases: `gridsim ehv2.raw --psse-revision 30 --frequency-hz 50`). One of 30, 33, 34, 35, given once (otherwise exit 2). A file that states a different revision is refused (exit 1). When the run loads no `.raw` file, `warning: --psse-revision had no effect: this run loaded no PSS/E .raw file.` is printed on stderr at the end. See [ch. 2](<0002 Loading networks.md#psse-raw-files>). |
| `--seq-data <file.seq>` | none | A PSS/E sequence data file (revision 30 layout) applied to every `.raw` case the run loads, so `fault` and `iec60909` use its machine reactances (X''d, X2), line and transformer zero-sequence impedances and transformer winding connections. The name must end in `.seq` and be given once (otherwise exit 2). Its notes are printed on stderr as `note: <file>.seq: ...`, ending with the counts applied. Machine zero-sequence impedances and mutual coupling between whole lines are applied. Sequence data GridSim's networks have no element for (sequence shunt loads, mutual coupling over part of a line, zero-sequence switched shunts) refuses the load (exit 1) unless `--allow-unmodelled` is given. When the run loads no `.raw` file, `warning: --seq-data had no effect: this run loaded no PSS/E .raw file.` is printed on stderr at the end. See [ch. 2](<0002 Loading networks.md#sequence-data-seq>). |
| `--tap-control` | off | Apply a PSS/E `.raw` file's automatic voltage control by tap (COD 1): each controlled winding becomes an on-load tap changer (band VMI..VMA, limits RMI..RMA, step (RMA - RMI) / (NTP - 1)) that the solve steps from the file's ratios, each winding regulating its own reactive power flow (COD 2, by tap) or active power flow (COD 3, by phase shift angle) is stepped the same way until the flow into it is in VMI..VMA, and a case that does not converge from the file's taps is solved from a ramp start (10 %, 20 %, ... of its demand and generation first; the report says so on a `Ramp start:` line). Without it the taps stay at the file's ratios. Given once (otherwise exit 2). When the run loads no `.raw` file, `warning: --tap-control had no effect: this run loaded no PSS/E .raw file.` is printed on stderr at the end. See [ch. 2](<0002 Loading networks.md#tap-control>). |
| `--shunt-control` | off | Switch a PSS/E `.raw` file's switched shunts that have discrete local voltage control (MODSW 1, one block size, BINIT a whole number of blocks) in whole blocks to hold their bus in VSWLO..VSWHI: in the case solve (inside the tap loop; the report adds a `Switched shunts:` line) and in `pf` (as a `--switched-shunts` file's banks; giving both exits 2). Those with continuous local voltage control (MODSW 2) become static var compensators solved with the power flow (the report prints a `Static var compensator` line for each). Every other switched shunt stays fixed at BINIT, noted. Given once (otherwise exit 2). When the run loads no `.raw` file, `warning: --shunt-control had no effect: this run loaded no PSS/E .raw file.` is printed on stderr at the end. See [ch. 2](<0002 Loading networks.md#shunt-control>). |
| `--area-control` | off | Keep a PSS/E `.raw` file's area interchange data for `pf`: each AREA record's swing bus (ISW), desired export (PDES, MW) and tolerance (PTOL, MW), the area of every bus, and each branch's metered end (the MET field of revision 33 and later, a negative J, or NMETR 1 on a two-winding transformer; otherwise the from end). `pf` then controls each area as a `--area-interchange` file would (giving both exits 2). An area that holds a slack bus, has ISW 0 or names an ISW outside the area is reported only, noted. The model is the same with or without it. Given once (otherwise exit 2). When the run loads no `.raw` file, or no `pf` run reads the data, a warning says it had no effect. |
| `--remote-vreg` | off | Keep a PSS/E `.raw` file's remote voltage regulation for `pf`: each in-service unit at a PV bus whose IREG names another bus, a PQ bus, holds that bus at VS, as a `--remote-vreg-file` file would (giving both exits 2). One control per controlling bus (the first unit's IREG and VS); a second bus naming the same regulated bus is noted and holds its own bus, as PSS/E RMPCT sharing is not modelled. Without it the unit holds VS at its own bus, as before. The model is the same with or without it. Given once (otherwise exit 2). When the run loads no `.raw` file, or no `pf` run reads the data, a warning says it had no effect. |
| `--dyr-extended` | off | A PSS/E `.dyr` file given with `--dyn-data` is also read for the extended model library: GENCLS, ESST1A, EXST1, SCRX, ESAC1A, ESDC1A, EXDC2, IEEEX1, ESAC8B, ESST3A, IEEEST, PSS2A, PSS2B, the GGOV1, WSIEG1 and IEEEG3 governors and the CIM5BL motor load, each in its PSS/E record order (GENTPJ, PSS2C, ESAC7B, HYGOV4 and GENQEC records are refused by name: their order is not confirmed by two public sources). Without it those records are models the reader does not map, exactly as before. Given twice it exits 2. When the run reads no `.dyr` file, `warning: --dyr-extended had no effect: this run read no PSS/E .dyr file.` is printed on stderr at the end and the exit code is unchanged. See [ch. 2](<0002 Loading networks.md#dynamic-data-dyr>). |
| `--allow-unmodelled` | off | Read a PSS/E `.raw` file that holds in-service devices GridSim does not model (capacitor-commutated two-terminal DC links, VSC converters regulating a remote bus, multi-terminal DC converters on two DC buses, FACTS devices in modes 4 to 8 or regulating a remote bus, or induction machines with a standard design code, a double cage or saturation). Without it such a file is refused (exit 1), the message naming each kind and the line of its first record. With it those devices are read past, each kind counted in a `note:` line on stderr, and the power flow differs from the file's. GNE devices are refused either way. With `--seq-data` it also lets sequence data GridSim's fault networks do not model be read past, each kind counted in a note; for a UCTE-DEF `.uct` file it reads transformers that have special tables (##TT), using their own impedances. Like `--cpu` it may stand anywhere on the line; given twice it exits 2. When the run loads no `.raw` file, `warning: --allow-unmodelled had no effect: this run loaded no PSS/E .raw file.` is printed on stderr at the end and the exit code is unchanged. See [ch. 2](<0002 Loading networks.md#psse-raw-files>). |
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
| `--journal <file.jsonl>` | off | Record this run in a run journal: one JSON line (`gridsim-journal/1`) written when the run ends, with the command line, the SHA-256 of every input file, the path and SHA-256 of every output, the exit code and the build. `breaking-change validate` re-runs these entries on a later build. It is taken off the command line before anything else reads it, so the run is byte for byte what it is without it: same stdout, stderr, exit code and output files, and a result file's `args` do not show it. Nothing is printed when the entry is written; if it cannot be written, one `warning: journal: ...` line goes to stderr and the exit code is unchanged. Many runs may write one journal at once (a lock file `<journal>.lock` beside it, which holds no data). The active file is rotated into `<name>-<first>-<last>.jsonl` at 64 MiB. A network or device path, a missing value, or the flag given twice exits 2. Not recorded: `--version`, help, `licence`, `--importLicence`, `breaking-change` and the interactive live view. The journal holds absolute paths and file names; it is a local file only. See [technical ch. 42](<../technical/0042 Run journal and breaking-change validation.md>). |
| `--timings` | off | Time the run stage by stage, in wall-clock milliseconds. With it, a verb's `--json` result gains a final `timings` block (see [Result files](#result-files)) and a short table is printed on stderr when the run ends (`timings (wall-clock ms):`, then one line per stage). The stages are `startup` (process start to the first statement of the CLI), `parse` (case load), `build`, `assemble`, `factor`, `solve`, `write` (end of the last measured stage to the end of the run: the console report and the result document being filled and rendered; for a `--json` run it stops when the document has been rendered, so the file write itself is not in it) and `total` (process start to that instant; it also holds untimed work between the stages, such as flag checks and the licence check, so the stages add to less than it). A verb whose code path calls one solver entry point that builds, assembles, factors and solves inside it reports one combined `solve` figure, and the block says so in `stages_merged`; a stage a verb does not measure is absent and named in `stages_not_measured`. Counts the verb exposes are added (`iterations`; `solves` for `cpf`). Measured verbs: `pf`, `dcpf`, `iec60909`, `estimate`, `nminus1`, `acopf`, `dcopf`, `cpf`, `stability`, `dynamics-study`, `crossval` and `harmonics`; on any other verb the block holds `startup`, `parse` where the verb loads a case through the shared loader, `write` and `total`, and `stages_not_measured` lists the rest. The `pf` Q-limit pass count is not exposed by the solver result, so it is not reported. Without the switch every output is byte for byte what it was; with it, stdout and the result file differ only by the `timings` block, and stderr only by the table after the run. It is taken off the command line first, so a result file's `args` and the run journal do not show it. Not a reproducibility figure: the numbers are wall-clock times of this run on this machine. |
| `--journal-snapshot` | off | With a journal, also keep a copy of each input file (and each file of a case folder) in the journal's snapshot store, `objects/` beside the journal, by SHA-256: written once, read-only, files over 256 MiB skipped. A later `validate` re-runs from the copy when the original has gone or changed. Outputs are never kept. Nothing is deleted automatically: delete `objects/` (or old objects in it) to free the space; entries whose inputs were only there then cannot be re-run. |
| `GRIDSIM_JOURNAL=<file>` / `GRIDSIM_JOURNAL=off` | unset | The environment form of `--journal` (used when `--journal` is not given). `off` switches the journal off for the run, whatever `--journal` or the config file say. A network or device path gives a warning and no journal. |

The audited verbs (`estimate`, `crossval`, `interpose`, `serve`) also write one `action=security` entry per security
event at the point of detection, besides the run's own entry: an input refused by the horizon guard, an infeasible
estimate, a suspected false-data injection, an output-guard refusal, a quarantined live window and a breached
service-level objective. The `audit:` line at the end counts them (`security-events=N`).
| `--once` | off | Bare/case/dir forms: solve once, print, exit. |
| `--ticks N` | interactive | Bare/case/dir forms: run N headless live ticks then exit. N is at least 1: leave the flag out for the interactive loop; `--ticks 0` or a negative count exits 2. Every tick is run and printed; when any tick did not converge (`DIV`) the run ends with `error: <n> of <N> tick(s) did not converge (DIV)` and exit 3. `interpose` uses it as a frame cap. |
| `--importLicence <file.gridsim>` | - | Install a software licence from a `.gridsim` file, then exit (see the `licence` verb and [ch. 4](<0004 Licensing and activation.md>)). Fully offline. The value must be a file path: an empty value (`--importLicence=`), a following flag (`--importLicence --once`) or no value at all exits 2 with `error: --importLicence expected a licence file path (<file.gridsim>) but got '...'`. Distinct from the DNO `--licence` distribution-area selector of `gda`. |
| `--version` | - | Print `gridsim <version>+<commit>` (the release number and the full git commit the binary was built from) and exit 0. Answered before anything else but the telemetry guard (exit 78, see the exit-code table), so it needs no licence and works alongside any other token. |

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

`gridsim nminus1 [<case>] [--dir <path>] [--global-vlimits] [--outage-types branch,gen,bus] [--outage-dispatch reference|droop|inertia] [--post-fault-rating A|B|C] [--batched-lanes N] [--ras <file>]` (alias `n-1`)

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

`--ras <file>` (`nminus1` and `nminus2`) runs remedial action schemes inside each contingency. The file is a
`gridsim-ras/1` document; [ch. 11](<0011 Contingency analysis.md#remedial-action-schemes---ras>) describes it. For each
contingency the schemes are armed from the base case and the outage, then level by level every armed scheme whose
triggers hold operates and the state is solved again, up to the file's `maxLevels` (default 5). The output keeps the
screen's table (headed `Before schemes:`) and adds a second table (`After schemes:`) with the last level, the status
after the schemes (`SECURE AFTER RAS`, `VIOLATIONS AFTER RAS`, `NOT CONVERGED AFTER RAS` or `RAS LOOP`), the final
loading and voltages, the schemes that operated and one line per action (`ACTION SKIPPED: <reason>` for one that
could not act). Both tables carry the screen's lost load and generation, reference-move and worsened base-case
violation lines for their state. The verdict is judged on the state after the schemes: exit 0 only when the base case and every
contingency are secure after them. The result file adds the summary keys `ras_schemes`, `ras_max_levels`,
`secure_after_ras`, `violations_after_ras`, `not_converged_after_ras` and `ras_loop`, the columns `ras_fired`,
`ras_level`, `post_ras_status`, `post_ras_max_loading_pct`, `post_ras_vmin_pu`, `post_ras_vmax_pu` and
`post_ras_violations` on `outages`, and a `ras_actions` table (`outage,level,scheme,action,kind,element,applied,detail`;
with `--csv`, `<stem>-ras_actions.csv`). A file that cannot be read, or that names an element the case does not have, is
an input error (exit 1). `--ras` on another verb, with `--batched-lanes`, or with an `--outage-dispatch` other than
`reference` exits 2. The file is an input of the run, so the run journal hashes it and `breaking-change validate`
re-runs the job with it. Without `--ras` both verbs are unchanged.

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
| `--ras` | off | Run the remedial action schemes of a `gridsim-ras/1` file inside each pair (see [nminus1](#nminus1)). |
| `--global-vlimits` | off | Judge every bus against the operational band 0.94-1.06 pu, ignoring the case's per-bus limits (see [nminus1](#nminus1)). `--bus-vlimits` is accepted as a no-op alias of the default. |
| `--bus-vlimits` | on | The default (each bus against its own limits), accepted as a no-op so existing scripts keep working; with `--global-vlimits` it exits 2. |

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
| `--bus-vlimits` | on | The default (each bus against its own limits), accepted as a no-op so existing scripts keep working; with `--global-vlimits` it exits 2. |

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
| `--bus-vlimits` | on | The default (each bus against its own limits), accepted as a no-op so existing scripts keep working; with `--global-vlimits` it exits 2. |
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

### corrective-sequence

`gridsim corrective-sequence <study.json> [--out <result.json>]`

Ordered corrective N-1-1 study (RC3). Each requested sequence is a first branch outage, corrective generator actions,
then a second branch outage, run as an ordered list of auditable steps: `IntactBase`, `FirstOutage`, `FirstAcSolve`,
`ActionProposal`, `ActionValidation`, `ActionApplication`, `IntermediateAcVerification`, `SecondOutage`,
`FinalAcVerification`. A step runs only when every earlier step completed. Every step of every sequence is in the
result: a failed step keeps its evidence and every later step is reported `Blocked` with the step that stopped it, so a
plausible final solve can never hide a failed or missing intermediate state. Every AC state is solved and checked by
the same absolute verifier as `transmission-security` (bus voltage against the study band, branch MVA at both ends, each
unit's realised P and Q against its own range). It is separate from `n-1-1`, whose screen, ranking and corrective
default are unchanged.

The study file is strict JSON, schema version 1: `schemaVersion` (1), `case` (`path` or `builtIn`, plus `sha256`, with
the same identity rules as `transmission-security`), `voltage` (`minPu`, `maxPu`), `ratings` (`firstStage`,
`intermediate` and `final`, each `A`, `B` or `C`, falling back to A where a branch has none), `policy` and `sequences`.
`policy.eligible` is the permitted action set: one envelope per unit with its model `generator` index and expected
`bus`, `direction` (`Up`, `Down` or `Both`), `minFinalMw`, `maxFinalMw`, `maxDeltaMw`, `rampMwPerMin` and
`windowMinutes`; `policy.availableMinutes` is the time assumed between the two outages, and the optional
`policy.balanceToleranceMw` (default 1e-6) bounds the net MW of a proposal. A unit at the slack bus cannot be eligible:
the slack balances losses in every state. Each sequence has `first` and `second` (zero-based branch `index` with its
expected `from` and `to` bus IDs), `proposal` (`None`, `Prescribed` or `PlannedRedispatch`) and, for `Prescribed` only,
`actions` (`generator`, `bus`, `deltaMw`). Sequences are run in input order, both orders of a pair when both are
listed, each from a fresh copy of the case. Unknown or duplicated fields, non-finite numbers, an invalid outage, a
repeated sequence or an invalid envelope are refused before any solve.

The intact state is checked against RATE_A, the first post-outage state against `ratings.firstStage` (the temporary
emergency rating; loading above `ratings.intermediate` there is listed as temporary emergency loading, not a failure,
and loading above `firstStage` is a prohibited state), the intermediate state (first outage plus actions) against
`ratings.intermediate`, and the final state (both outages, actions retained, no further control) against
`ratings.final`. Actions are simultaneous independent ramps over a common window: each change must have an allowed
sign, a final setpoint within the envelope and the unit's own Pmin and Pmax, and a magnitude within the tighter of
`maxDeltaMw` and `rampMwPerMin` times the smaller of `windowMinutes` and `availableMinutes`; a rejected action reports
its exact MW shortfall. A proposal is applied whole or not at all, and its MW changes must balance. `Prescribed`
replays the given actions; `PlannedRedispatch` takes the changes of the incumbent DC minimum-change corrective
redispatch on the first post-outage network as a candidate, with no authority outside the permitted set. Neither finds
feasible corrective actions. Breaker and switching actions are not supported in this version.

Sequence outcomes: `Verified`, `Violations`, `ActionRejected`, `Incomplete` (an `ISLANDED`, `NO SOLUTION` or
`INCOMPLETE` state) or `Blocked` (the intact base is not secure), with the first failed step and its disposition. The
study `status` is `SECURE` only when the intact base and every sequence are verified; otherwise `NOT SECURE`, with
`outcome` by precedence `Incomplete`, `ActionRejected`, `Violations`. Counts give requested, attempted, completed,
verified and incomplete sequences. The result JSON (`schemaVersion`, `study`, `provenance`, `effectiveOptions`,
`stepOrder`, `inputGenerators`, `result`) is printed to stdout and, with `--out`, written to a new file via a temporary
file and a final move; an existing `--out` path is refused.

Exit 0 `SECURE`. Exit 1 for input it cannot accept (malformed or unknown-field study, case identity mismatch, invalid
outage, sequence or policy, unreadable case, existing output path). Exit 2 for an invalid invocation. Exit 3 `NOT SECURE`.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |

## Network analysis verbs

These verbs take their case as a positional argument only (a built-in name, a `.m` file, a JSON case folder or, for
`dist3ph`, a feeder file; see [Case arguments](#case-arguments)). A missing case exits 2, a case that does not load
exits 1. The verbs are used in [ch. 5](<0005 Running power flows.md>) (`pf`) and
[ch. 6](<0006 OPF, CPF, taps, distribution factors and losses.md>) (`dcpf`, `dcopf`, `acopf`, `cpf`, `taps`, `ptdf`,
`lodf`, `losses`); `dist3ph` is in [ch. 7](<0007 Three-phase distribution feeders.md>).

### pf

`gridsim pf <case> [--distributed-slack pmax|pg|equal] [--switched-shunts <file.json>] [--area-interchange <file.json>] [--remote-vreg-file <file.json>] [--top 12] [--engine newton|fdxb|fdbx|gs] [--gs-accel 1.0] [--max-q-passes 100] [--q-revert] [--max-iter 100] [--diverge-limit 10|off]`

One AC power flow on the engine the shared rule picks (radial sweep, dense or sparse Newton), 100 iterations with
reactive limits on, printed: convergence, iterations, mismatch, losses, the voltage range and the `--top` lowest bus
voltages. Four opt-in controls wrap the unchanged engine as outer loops (like the tap-changer loop), so the dense and
sparse engines give the same bits with them too. They nest in this order: engine, distributed slack, switched shunts,
remote voltage, area interchange.

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
- `--remote-vreg-file <file.json>`: a PV bus holds the voltage of another bus. The controlling bus stays PV in the
  Newton solve and its set point is moved between solves: e = target - V at the regulated bus, then a secant step on
  that control's own history (the first step adds e), the set point kept in [0.8, 1.2] pu, until every regulated bus is
  within 1e-8 pu of its target (30 passes at most). A controlling bus the reactive-limit loop turned PQ, at the limit
  the target pushes against, is AT Q LIMIT and its set point is frozen. Refused (exit 1): two controlling buses for one
  regulated bus, a regulated bus that is PV or slack, or one in another island. The file:
  `{ "format": "gridsim-remote-vreg/1", "controls": [ { "generatorBus": 2, "regulatedBus": 3, "targetVpu": 1.0 } ] }`
  (every key required and checked). The output lists each control as `bus 2 -> bus 3: OK` (or AT Q LIMIT, AT SET-POINT
  LIMIT, NOT SETTLED) with the regulated voltage and the set point.
- `--area-interchange <file.json>`: each controlled area holds its net export at the desired value by moving the units
  at its swing bus. The export is the sum, over the in-service branches with one end in the area, of the active power at
  the branch's metered end: as it is when that end is in the area, negated when it is not, so a tie's losses fall on the
  area at the far end from the meter. After each solve, an area more than its tolerance off has the difference added to
  its swing units, shared by (Pmax - Pmin) within their limits (equally when a range is zero or unbounded), and the case
  is solved again (20 passes at most). An area whose units all reach a limit is AT LIMIT and is not moved again; that is
  still exit 0. The area holding the slack bus cannot be controlled (exit 1): the slack absorbs the remainder. List it
  under `reportAreas` to have its export printed. The file:
  `{ "format": "gridsim-area-interchange/1", "areas": [ { "area": 2, "swingBus": 2, "desiredExportMw": -30,
  "toleranceMw": 0.1, "buses": [2, 3] } ], "reportAreas": [ { "area": 1, "buses": [1] } ], "meteredEnds": [ { "from": 1,
  "to": 2, "meteredBus": 2 } ] }` (`reportAreas` and `meteredEnds` optional; a metered end may add `name` to pick one of
  parallel branches; an unlisted branch is metered at its from end). Area interchange and `--distributed-slack` both move
  unit real power, so together they exit 2. The output lists each area as `area 2: OK` (or AT LIMIT, NOT SETTLED,
  REPORTED) with its export, desired value, tolerance and the MW added.

A PSS/E `.raw` case gives the same data with the global switches `--area-control` and `--remote-vreg` (giving a file as
well exits 2).

`--engine` picks the solution method by name. `newton` (or no `--engine`) is the shared rule above, unchanged. `fdxb`
and `fdbx` are the fast-decoupled load flow, XB and BX versions, with B' and B'' built as MATPOWER's `makeB.m` builds
them; `gs` is Gauss-Seidel as MATPOWER's `gausspf.m` iterates it (PQ buses, then PV buses, in model order), with the
acceleration factor `--gs-accel` (1.0 is plain Gauss-Seidel). All three use Newton's convergence test (the largest P and
Q mismatch below the tolerance, in pu) and the same reactive-limit loop, so a converged answer is the same solution as
Newton's to the tolerance. The fast-decoupled engines take up to 100 iterations, Gauss-Seidel up to 10000. They are
offered for comparison and for PSS/E script migration; Newton stays the recommended engine. They do not run with the
outer-loop controls (`--distributed-slack`, switched shunts, remote voltage control, area interchange: exit 2) or on a
case with HVDC links, FACTS devices, static var compensators or induction machines (exit 1). The fast-decoupled engines
also refuse a branch with X = 0 (purely resistive), naming it (exit 1): both B' variants drop R from one matrix, so such a
branch has no admittance there; Newton and Gauss-Seidel solve it. The header line names the
engine, and the result file gains `engine` (and `gs_acceleration`); without `--engine` both are as before.

The reactive-limit loop switches a generator bus from PV to PQ when its reactive output passes a limit, and solves again,
for at most 100 passes by default. A very large case can need more: if the last permitted pass still switched a bus, `pf` reports NOT CONVERGED
although the mismatch of that last solve is tiny, because the limits had not settled. Two flags change that. With
`--max-q-passes <N>` (a whole number, 1 or more) the loop may run N passes. With `--q-revert` a bus that was switched to
PQ returns to PV (once per solve) when its solved voltage shows the limit is no longer binding, so the loop settles in fewer passes (about
half on the large synthetic cases). Both apply to every `--engine`. `--q-revert` is off unless given. Without either flag the loop runs up to 100 passes with no
revert. A case that settles in fewer passes gives the same bytes at any cap. The two can be combined.

A solve that is running away is stopped early. When the largest power mismatch of an iteration rises above both 10 pu and
the mismatch the pass started from, `pf` stops that pass and reports `DIVERGED` instead of `NOT CONVERGED`, with the
iteration it stopped at and the state at that point, which is not a solution. Above 10 pu the simulation is
fundamentally broken and no starting point will fix it, so continuing to the iteration cap only spends time. The limit is
10 pu and not lower on purpose: a run that peaks at a few pu (the synthetic 90,000-bus case peaked at 4.1 pu) is still a
plausible but wrong state, and that state is useful evidence of an error in the model. The starting mismatch is part of
the test because a flat start on a large case can itself be above 10 pu. `--diverge-limit <X>` sets the limit in pu
(above 0), so a user who knows their models can lower it to save time. `--diverge-limit off` runs every pass to the
iteration cap, as before. `--max-iter <N>` (a whole number, 1 or more) sets that cap; without it the cap is 100, and 10000
for `--engine gs`. The stop applies to every `--engine`, but not to the backward/forward sweep that solves a radial
feeder, which tests the voltage change and not the power mismatch. It reads only the mismatch the iteration has already computed, and
changes no arithmetic, so a solve that converges gives the same bytes with the stop on or off. The exit status of a
diverged run is 3, as for any run that did not converge; the result file is written only for a converged run.

| Flag | Default | Meaning |
|---|---|---|
| `--distributed-slack` | off | `pmax`, `pg` or `equal`; any other value exits 2. |
| `--switched-shunts` | - | A `gridsim-switched-shunts/1` file; a missing or invalid file exits 1. |
| `--area-interchange` | - | A `gridsim-area-interchange/1` file; a missing or invalid file exits 1. |
| `--remote-vreg-file` | - | A `gridsim-remote-vreg/1` file; a missing or invalid file exits 1. |
| `--top` | 12 | Lowest voltages and largest slack moves to list; 0 or more (a negative value exits 2). |
| `--engine` | newton | `newton`, `fdxb`, `fdbx` or `gs`; any other value exits 2. |
| `--gs-accel` | 1.0 | Gauss-Seidel acceleration factor, above 0 and below 2; only with `--engine gs` (otherwise exit 2). |
| `--max-q-passes` | 100 | The most reactive-limit (PV to PQ) passes; a whole number, 1 or more (anything else exits 2). |
| `--q-revert` | off | A switch with no value: a bus switched to PQ may return to PV. Off keeps the one-way switching. |
| `--max-iter` | 100 (Gauss-Seidel 10000) | The most iterations per pass; a whole number, 1 or more (anything else exits 2). |
| `--diverge-limit` | 10 | The largest mismatch (pu, above 0) a pass may reach, above its starting mismatch, before it stops with `DIVERGED`; `off` never stops it early. Anything else exits 2. |

Exit 0 converged (and shunts, remote controls and areas settled; AT LIMIT and AT Q LIMIT count as settled); 3 not
converged or not settled; 1 on a load error, a bad file or a refused control; 2 on a usage error (an unknown
`--distributed-slack` rule, a flag of another verb, area interchange with `--distributed-slack`, a file and the matching
`.raw` switch together).

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary (case, convergence, losses, voltage range, the slack and shunt outcome), a
`buses` table (every bus: |V|, angle, net injection), and with the options a `slack_units` table (every participating
unit), a `switched_shunts` table, a `remote_vreg` table (each control) and an `areas` table (each area), with the pass
counts and settled flags in the summary. Without those options the file is exactly as before. A solve that does not converge writes no file; unsettled shunts write the file
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

### sensitivity

`gridsim sensitivity <case> --kind otdf|psdf|tap|dvdq|effectiveness [--branch <idx>] [--bus <id>] [--top 12]`

First-order sensitivities of a case. `--kind` chooses the quantity:

- `otdf` (needs `--branch` and `--bus`): outage transfer distribution factors, the DC flow on every branch per MW
  injected at `--bus` (withdrawn at the reference of its island) after branch `--branch` is out:
  `PTDF[l, bus] + LODF[l, k] * PTDF[k, bus]`, from the same factored B' as `ptdf` and `lodf`. A bridge outage prints that it
  ISLANDS the system and the factors are undefined (NaN in the result file), never an invented number. The outaged
  branch's own factor is 0.
- `psdf` (needs `--branch`): phase-shifter distribution factors, the DC change of every branch flow in MW per radian of
  phase shift on `--branch`. The sign is the solver's own (`PhaseShiftDeg` enters as `tap = a e^(j phi)`, so a positive shift
  reduces the From-to-To flow): for two parallel branches the shifted one gives `-1/(X1 + X2)` per unit (-250 MW/rad for
  two 0.2 pu branches on a 100 MVA base).
- `tap` (needs `--branch`): the AC change of every bus voltage per unit and per per cent of the tap ratio of `--branch`,
  the exact derivative of the solved state with the regulated set points and any reactive-limit pins held as they ended
  the solve.
- `dvdq` (needs `--bus`): the AC voltage change at every bus per MVAr and per MW injected at `--bus`. A bus that holds its
  voltage at the solved state (slack, or a regulating unit not at a limit) has a zero reactive response.
- `effectiveness` (needs `--bus` as the target): the effect on the target bus voltage of every candidate control, from one
  adjoint solve: reactive injection at each PQ bus (pu per MVAr), the tap ratio of each transformer (pu per unit ratio)
  and the voltage set point of each regulating bus (pu per pu), largest first.

The AC kinds solve the base case with the contingency study solver and options (as `cpf` and `nminus1` do) and linearise
the polar Newton equations there (the analytic Jacobian of the pi-model admittances, with ZIP load slopes). They are
refused with `OUT OF SCOPE` (exit 3) for a case with on-load tap changers, DC links, FACTS devices, static var
compensators, induction machines or transformer flow controls, whose outer loops are not in the Jacobian, and exit 3 when
the base case does not converge. The DC kinds use only series reactance, as `ptdf`. Exit 2 for a missing or unknown
`--kind`, a flag the kind does not use, a bus or branch the case does not have, or a branch that is out of service.

| Flag | Default | Meaning |
|---|---|---|
| `--kind` | required | `otdf`, `psdf`, `tap`, `dvdq` or `effectiveness`. |
| `--branch` | - | 0-based branch index: the outage (`otdf`), the shifter (`psdf`) or the transformer (`tap`). |
| `--bus` | - | Bus id: the injection (`otdf`, `dvdq`) or the target (`effectiveness`). |
| `--top` | 12 | Rows to list on the console (the result file has every row). |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): a `factors`, `voltage` or `effectiveness` table with every row.

### qv-curve

`gridsim qv-curve <case> --bus <id> [--vmin 0.4] [--vmax 1.2] [--step 0.01]`

The QV curve of one bus: the reactive power a synthetic voltage-controlling unit at the bus must supply to hold each
voltage. A copy of the case holds the unit with an unbounded reactive range and makes the bus a PV bus; units already on
the bus are held at their solved base-case reactive output, so the curve is the EXTRA compensation required and is zero at
the operating point. The voltage is swept down and up from the operating point on a grid of `--step` (anchored there),
each point solved with the contingency study solver. A point that does not converge ends that branch of the sweep and is
recorded in the table (`converged` false), not skipped. The report gives the grid minimum, a nose estimate (the parabola
through the three samples round the minimum, exact when Q(V) is quadratic, as it is for a lossless line with no real
power) and the reactive margin at the operating point: `-Qnose`, the reactive power the bus can still lose before the
curve no longer meets the zero line (negative: the operating point is already past the nose). For a slack bus the verb
refuses (`OUT OF SCOPE`, exit 2); exit 3 when the base case does not converge or the minimum is not bracketed by the
converged points.

| Flag | Default | Meaning |
|---|---|---|
| `--bus` | required | Bus id (not the slack). |
| `--vmin` | 0.4 | Lowest voltage swept (pu); above 0. |
| `--vmax` | 1.2 | Highest voltage swept (pu); above `--vmin`. |
| `--step` | 0.01 | Voltage step (pu); above 0. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary (operating voltage, margin, nose) and a `curve` table.

### pv-contingency

`gridsim pv-contingency <case> [--branches <idx,idx,...>] [--step 0.25] [--min-step 1e-3] [--max-steps 200] [--parallel N] [--top 20]`

Maximum loadability (the nose of the PV curve) under each single-branch outage: the `cpf` walk (load multiplier stepped
up and bisected, every point solved with the contingency study solver and options) run on the base case and on the outage
copy of each branch in index order (every in-service branch, or the `--branches` list in the order given). An outage that
splits the network is classified from the topology first and printed `ISLANDED` (no solve); an outage state that does
not converge at multiplier 1 is `NO SOLUTION` (its multiplier is then the highest lower one that does, as `cpf` prints for a base case that does not solve, with a negative margin). A row's `bracketed` flag is the walk's own statement: false means the
multiplier is only a lower bound. The items are independent and each has its own solver, so `--parallel` returns the same
rows with the same bits as the serial run. Exit 3 when the base case does not converge or any outage is `NO SOLUTION`.

| Flag | Default | Meaning |
|---|---|---|
| `--branches` | all in service | Comma list of 0-based branch indices to outage; an out-of-service or repeated branch exits 2. |
| `--step` | 0.25 | Initial multiplier step, as `cpf`. |
| `--min-step` | 1e-3 | Bisection stops below this step; in (0, `--step`]. |
| `--max-steps` | 200 | Cap on solves per walk; at least 1. |
| `--parallel` | 1 | Outages walked at once; 0 means one per processor. |
| `--top` | 20 | Outages to list on the console, lowest loadability first (the result file has every row). |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary and an `outages` table.

### dc-contingency

`gridsim dc-contingency <case> [--branches <idx,idx,...>] [--post-rating A|B|C] [--top 20]`

DC N-1 screen: one factorisation of B', the base DC flows (in-service generation minus demand, each DC island's reference
absorbing its imbalance, as `dcpf`), then for each outage `flow_after = flow_before + LODF * flow_outaged` in MW. It reports DC MW and never
claims AC MVA. Limits follow the RC3 effective-rating rule: the intact state against RateA, an outage state against RateB
or RateC when `--post-rating` asks for it and the branch has one, else RateA (the rating source, `A`, `B`, `C` or
`fallback A`, is in the violations table). A branch with no RateA is not monitored (counted in `unrated_branches`). A
bridge outage is `ISLANDED` (the LODF is undefined; threshold 1e-9) and no algebra is done. A row reads `SECURE`,
`VIOLATIONxN` or `ISLANDED`; limits are absolute, so a violation the base case already has appears on the base row and on
every outage row. Exit 0 when the base case and every outage are `SECURE`; exit 3 (`NOT SECURE`) when any violates or
islands; exit 1 when the DC model cannot be formed.

| Flag | Default | Meaning |
|---|---|---|
| `--branches` | all in service | Comma list of 0-based branch indices to outage; an out-of-service or repeated branch exits 2. |
| `--post-rating` | A | `A`, `B` or `C`: the rating an outage state is judged against. |
| `--top` | 20 | Rows to list on the console (the result file has every row). |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary, an `outages` table (the base case is outage -1) and a `violations` table.

### fault-level-n1

`gridsim fault-level-n1 <case> [--buses <id,id,...>] [--branches <idx,idx,...>] [--objective max|min] [--parallel N] [--top 20]`

Three-phase fault level under N-1: for the intact network and then each single-branch outage in index order, the outage
copy is built and the IEC 60909 initial symmetrical current Ik'' is evaluated at every bus (or the `--buses` list). Each
fault is restricted to the fault bus's own island, so an outage that splits the network leaves a bus in an island with no
source with no value in that state (counted in `states_without_source`, never a number). Per bus the report gives the
intact value, the largest and smallest Ik'' over the states in which the bus has a source and the state that sets each
(`intact` or `branch <idx> <name>`; a tie names the first state). The outage states are independent, so `--parallel`
returns the same bits as the serial run. Needs a base kV at every fault bus, as `iec60909`.

| Flag | Default | Meaning |
|---|---|---|
| `--buses` | every bus | Comma list of fault bus ids. |
| `--branches` | all in service | Comma list of 0-based branch indices to outage. |
| `--objective` | max | `max` (voltage factor c_max) or `min` (c_min), as `iec60909`. |
| `--parallel` | 1 | States evaluated at once; 0 means one per processor. |
| `--top` | 20 | Buses to list on the console, largest maximum first (the result file has every bus). |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary and a `buses` table.

### substation-state

`gridsim substation-state <case.json> [--open <switch>,...] [--close <switch>,...] [--unspecified refuse|normal] [--numbering node-rank|compact] [--emit <projected.m>] [--base-mva <MVA>] [--sequence <steps.json>]`

Node-breaker topology processing (RC4 R4-11 part A, Core `GridSim.Core.Model.Substations`). `<case.json>` is a native
`gridsim-substations/1` file: `substations` (each with `voltageLevels`, `bays`, connectivity `nodes` and `switches`, a
switch being a `breaker`, `disconnector`, `earthSwitch` or `loadBreak` with `from`, `to` and `normalOpen`, the words of
the asset overlay's substation records) and the equipment on the nodes: `lines`, `transformers`, `loads`, `generators`
and `shunts`, and optionally `transformers3w` (three-winding transformers: `id`, the `hv`, `mv` and `lv` nodes, the pair
impedances `r12Pu`, `x12Pu`, `r13Pu`, `x13Pu`, `r23Pu`, `x23Pu` on the model base, the star-point magnetising `bPu`, and
optional three-entry lists `ratio`, `phaseShiftDeg` and `ratedMva`; each is projected as a star bus and three branches with
the PSS/E reader's star impedances), and optionally `notes` (an array of strings: provenance, such as the switches an
[scl-import](#scl-import) with `--assume-closed` took as closed; no calculation reads them). `<case.json>` may instead be a CGMES exchange, a `.zip` or a folder of EQ, TP, SSH and SV profile files, which
Core's `CgmesSubstationReader` reads into the same node-breaker model (the inverse of [cgmes-export](#cgmes-export)); the
SSH switch states are then the starting states, `--open` and `--close` override them, and a switch the SSH does not state is
handled by `--unspecified`. CGMES carries no system base, so the model is read on 100 MVA (noted) unless `--base-mva` gives
one; what the reader mapped only in part or defaulted is printed as `cgmes note:` lines and written to a `cgmes_notes` table. A
CGMES object the node-breaker model has no place for (a three-winding transformer, a series compensator, ...) is refused
by name, exit 1. A `.json` input behaves exactly as before. The verb applies the switch states, joins the nodes that closed switches connect into electrical buses and
prints the resulting bus-branch model: the bus groups, the bus ids, which terminal sits on which bus, what became of every
piece of equipment and the demand and generation accounting. The input is never altered. Nothing else in GridSim changes:
the existing bus-branch model, solvers, importers and exporters behave exactly as before; this verb produces an ordinary
case beside them.

A group of nodes becomes a bus when two or more equipment terminals sit on it and no closed earth switch earths it. A
group with one terminal is isolated (an open breaker cuts it off) and its equipment is left out of the bus-branch model
with the reason recorded; a branch with an isolated end is left out whole; a closed earth switch earths its group. A
load or generator that is not connected is listed with its MW, and the served plus not-served demand always equals the
total. Bus ids are stable: with `--numbering node-rank` (default) a bus id is 1 plus the ordinal rank, among all nodes of
the file, of the lowest node of its group, so splitting one group never renumbers another; `compact` numbers the buses
1 to n in group order. Every ordering is by ordinal id, never by hash order, so the same file and states give the same
buses and bits on every run.

`--sequence <steps.json>` runs Core's `SwitchingScenario` from the starting state the other flags give. The file is
`{"schema": "gridsim-switching-sequence/1", "steps": [{"action": "open", "switch": "CPL", "note": "optional"}, ...]}`, an `action` being
`open` or `close`, read strictly (an unknown key, an action other than those two or a switch the model does not have exits 1). The report
of the starting state is printed as before, then one line per step with the bus and node-group counts, the demand served and the
generation connected, and the node groups the step removed and added. Every step is projected afresh from the node-breaker graph and the
cumulative switch states, so opening a switch and reclosing it returns the starting buses and bus ids; asking for the state a switch
is already in is recorded as `(already in that state)`, not refused. The result adds the summary member `sequence_steps` and the tables
`sequence` (one row per step, step 0 being the start) and `sequence_buses` (the buses of every step). Each step's projected case is
admitted by the licence's bus limit as the starting one is. `--sequence` cannot be combined with `--emit` (exit 2): `--emit` writes the one
case of the starting state, and a step's case is written by naming its switch states in `--open` and `--close`. The overlay projection
of a v2 case's substation records (`SubstationOverlayProjection`) is not reached from this verb.

Every switch must have a state: a switch named in neither `--open` nor `--close` is refused (a closed switch is never
inferred) unless `--unspecified normal` declares that unnamed switches take their `normalOpen` state. The topology is
refused, with every problem named, when a connectivity node is dangling (no switch and no equipment terminal, or held by
one switch only), when a switch joins two nodes of different nominal voltage, when a line joins two voltage levels, or
when an id is repeated or a reference does not resolve. A solve on the projected case goes through the existing verbs:
`--emit` writes the projected case to a NEW MATPOWER file (never over an existing file; a name in use gets a numbered
suffix; never the input), then `gridsim pf projected.m` solves it. Exit 0 done; 1 the file cannot be read or its
topology or states are refused; 2 usage.

| Flag | Default | Meaning |
|---|---|---|
| `--open` | none | Comma list of switch ids to open. |
| `--close` | none | Comma list of switch ids to close. |
| `--unspecified` | `refuse` | `refuse` or `normal`: what to do with a switch named in neither list. |
| `--numbering` | `node-rank` | `node-rank` (stable ids) or `compact` (1 to n). |
| `--emit` | none | Write the projected case as a new MATPOWER `.m` file. |
| `--base-mva` | 100 | CGMES input only: the system base the model is read on. |
| `--sequence` | none | A `gridsim-switching-sequence/1` file: run the ordered open and close steps from the starting state and report each. Not with `--emit`. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary (bus, group and branch counts, the demand and generation accounting, the
grouping) and the tables `buses`, `node_groups`, `terminals` (the terminal to bus map), `equipment` (with dispositions)
and `switches`.

### cgmes-export

`gridsim cgmes-export <substations.json> --dir <folder> [--cgmes-version 2.4.15|3.0] [--open <switch>,...] [--close <switch>,...] [--unspecified refuse|normal] [--sv none|solve]`

CGMES per-profile export of a native node-breaker file (RC4 R4-11 part B; Core `GridSim.Core.IO.Cim.Profiles`).
`<substations.json>` is a `gridsim-substations/1` file (see [substation-state](#substation-state)). The verb writes separate
documents into `--dir`: EQ (substations, voltage levels, bays, connectivity nodes, switches, terminals, lines,
two-winding transformers with ratio and linear phase tap changers, loads, synchronous machines with their voltage control,
shunts and permanent apparent-power limits), TP (the TopologicalNodes the closed switches make), SSH (switch states,
set-points, terminal flags) and, with `--sv solve`, SV (the Newton-Raphson voltages of the projected case). Files are
`<case>_EQ.xml`, `_TP.xml`, `_SSH.xml` and `_SV.xml`; an existing file is never overwritten (a numbered suffix is used).
`--cgmes-version 2.4.15` (default) writes the CIM16 namespace with `rdf:ID="_mRID"`; `3.0` writes CIM100 with `urn:uuid:`
identifiers. Impedances are written in ohms and siemens, set-points in kV and MW (CGMES units); the reader converts them
back to the same bits wherever a double exists that does so, and counts the values that have none. GridSim's own
identities travel in `IdentifiedObject.description` as `gridsim-id=<id>`, and the model name, base and frequency in the
EQ `md:Model.description`, so reading the documents back gives an identical model.

Every class and data item the files do not carry is listed (the structured omissions record: diagram layout, geographical
location, short circuit and dynamics profiles, ratedS and machine type, other limit kinds, ...): each with its profile and
the reason; nothing is left out silently. The CGMES specifications used are ENTSO-E's free CGMES 2.4.15 and CGMES 3.0
(see the CGMES Library at https://www.entsoe.eu/data/cim/cim-for-grid-models-exchange/ and
https://github.com/entsoe/application-profiles-library); no schema validator was run against the output (see
[cgmes-conform](#cgmes-conform) for what is checked). Exit 0 done; 1 the file cannot be read, its topology or states are
refused, or a file cannot be written; 2 usage.

| Flag | Default | Meaning |
|---|---|---|
| `--dir` | required | Folder the documents are written to (created if needed). |
| `--cgmes-version` | `2.4.15` | `2.4.15` or `3.0`. |
| `--open` | none | Comma list of switch ids to open (written as the SSH state). |
| `--close` | none | Comma list of switch ids to close. |
| `--unspecified` | `normal` | `normal`: a switch named in neither list takes its `normalOpen` state; `refuse`: refuse it. |
| `--sv` | `none` | `solve` writes an SV document from a Newton-Raphson solve of the projected case. |
| `--profiles` | none | A comma list of `dl`, `gl`, `sc` and `dy`: the bus-branch export with these profiles (below). |
| `--geo` | none | With `--profiles gl`: the `gridsim-geo/1` sidecar holding the bus sites and line routes. |
| `--dyr` | none | With `--profiles dy`: the PSS/E DYR file whose records are written as the DY profile. |
| `--model` | none | `bus-branch`, with `--cgmes-version 3.0`: a bus-branch case written as CGMES 3.0 EQ, TP, SSH and SV documents (below). |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary and the tables `files` and `omissions`.

**Diagram, geographical, short-circuit and dynamics profiles.**
`gridsim cgmes-export <case> --dir <folder> --profiles dl,gl,sc,dy [--geo <gridsim-geo.json>] [--dyr <file.dyr>]`

With `--profiles` the input is a bus-branch case (any case file the CLI reads). It is written as `<case>_EQ.xml`, the same
bytes `convert` writes to a `.xml` file (one CGMES 2.4.15 document with the equipment, topology, set-points and starting
voltages), and each named profile as its own document with an `md:FullModel` header:

- `dl`, `<case>_DL.xml`: one Diagram (orientation negative, y growing downwards as the single-line diagram draws it) and,
  per bus, a DiagramObject on its TopologicalNode with one point at the bus's layout position. Refused when every bus is
  at (0, 0).
- `gl`, `<case>_GL.xml`: WGS 84 (`urn:ogc:def:crs:EPSG::4326`, x = longitude, y = latitude) Locations from the `--geo`
  sidecar. A bus-branch case has no Substation to carry a location, so each line whose two buses have sites gets a
  Location running from its from site through its route to its to site; a site that no such line touches is refused.
- `sc`, `<case>_SC.xml`: the short-circuit data: line and transformer zero-sequence impedances in ohms, transformer
  winding connections (D, Y, Yn) with the neutral reactance, and machine X''d, X2 and zero-sequence impedance (per unit on
  the machine base). Each ohm value is the double GridSim's reader converts back to the model's bits; any that has none
  is counted in a note.
- `dy`, `<case>_DY.xml`: the `--dyr` records of GENROU, GENSAL, GENCLS, SEXS, SCRX and TGOV1 as their IEC 61970-302
  classes, linked to their machines. The case must be a PSS/E `.raw` file (the DYR names machines by bus and ID). Any
  other model is refused by name.

`--profiles` on its own writes CGMES 2.4.15 only, and refuses `--cgmes-version 3.0` and the node-breaker options. The gridsim-geo/1
sidecar is strict JSON:
`{"format":"gridsim-geo/1","crs":"EPSG:4326","sites":[{"bus":1,"latDeg":51.5072,"lonDeg":-0.1276}],"lines":[{"branch":2,"points":[[51.51,-0.12]]}]}`
(`branch` is the 1-based branch number, `points` the route between the two end sites). Reading the profiles back is a
library step (`CgmesDiagramLayout`, `CgmesGeographicalLocation`, `CgmesShortCircuitProfile`, `CgmesDynamicsProfile`;
see [technical ch. 32](<../technical/0032 CIM and CGMES import and export.md>)). Exit 0 done; 1 the case, the sidecar or
the DYR cannot be read or a profile is refused; 2 usage.

**CGMES 3.0 bus-branch documents.**
`gridsim cgmes-export <case> --dir <folder> --model bus-branch --cgmes-version 3.0 [--profiles dl,gl,sc,dy] [--geo <gridsim-geo.json>] [--dyr <file.dyr>]`

With `--model bus-branch` a bus-branch case (any case file the CLI reads) is written as four CGMES 3.0 documents,
`<case>_EQ.xml`, `_TP.xml`, `_SSH.xml` and `_SV.xml`, each with an `md:FullModel` header naming its CGMES 3.0 profile and
the models it depends on. The values are the ones the 2.4.15 document holds (the same writer, the same exact-value search),
split by profile as the CGMES 3.0 RDFS places each attribute: equipment, terminals, limits and tap changers in EQ;
TopologicalNodes in TP; p and q, targets, steps, `Equipment.inService` and terminal flags in SSH; starting voltages in SV.
Every IdentifiedObject carries its `IdentifiedObject.mRID`; objects are named `urn:uuid:` identifiers. Each bus gets a
Substation and a VoltageLevel, which CGMES 3.0 needs as the TopologicalNode's container. GridSim's CIM reader rebuilds the
same model from the four documents as from the 2.4.15 one. Not written: the machine inertia (in 3.0 a dynamics-profile
attribute; use `--profiles dy`), `GeneratingUnit.normalPF` (the case holds none: distributed-slack factors are a solve option, not model data) and the SV flows;
`SynchronousMachine.type` and `operatingMode` are written as `generator`. Each omission is listed. With `--profiles` the
named profiles are written in CGMES 3.0 too and refer to the 3.0 objects; the short-circuit profile then holds the
machine r0, x0, r2 and x2 in ohms (on the machine's rated voltage and MVA), as CGMES 3.0 does. `--model bus-branch`
without `--cgmes-version 3.0` is a usage error (exit 2); the 2.4.15 routes above are unchanged.

### cgmes-conform

`gridsim cgmes-conform <bundle.zip|folder>`

Checks a CGMES bundle against GridSim's own, hand-coded subset of the published profile rules (RC4 R4-11 part B). The
bundle is a ZIP (read in sorted entry order; path traversal, repeated entries, duplicate profiles and a DTD are refused) or
a folder of `.xml` documents. The checks, each named in the result with the rule it follows: C01 well-formed XML without a
DTD; C02 a recognised `md:Model.profile` in every document; C03 one CIM namespace (CIM16 for CGMES 2.4.15, CIM100 for
CGMES 3.0); C04 a coherent profile set; C05 `md:Model.DependentOn` resolves; C06 identifiers unique and in the edition's
form; C07 every reference resolves; C08 mandatory attributes of the classes GridSim writes and reads; C09 attributes sit in
their owning profile; C10 numeric and boolean values well formed; C11 terminal counts per class. The ENTSO-E SHACL
constraint files and the RDFS / OCL sets are NOT applied and the result lists them as `notrun` (S01, S02): a pass here is
not a conformity certificate, and conformity against a vendor tool or the ENTSO-E conformity test models is
comparator-unverified. Exit 0 when no check fails; 1 when the bundle cannot be read; 3 when a check fails (the verdict);
2 usage.

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary and the table `checks` (id, name, basis, status, findings).

### cgmes-shacl

`gridsim cgmes-shacl <bundle.zip|folder> --shapes <folder|file> [--shapes <folder|file>] [--rdfs <folder|file>] [--profile-filter eq,tp,ssh,sv,dl,gl,sc,dy] [--report <file.csv|file.json>]`

`gridsim cgmes-shacl --census <folder|file> [<folder|file> ...]`

Runs SHACL shapes on a CGMES bundle with GridSim's own SHACL Core checker (no third-party library). The shapes are the
user's: for example the ENTSO-E CGMES 3.0 files of https://github.com/entsoe/CGMES-AP (`*-AP-Con-Simple-SHACL.ttl` and
the others) or the CGMES 2.4 sets of https://github.com/entsoe/QoCDC-AP; none ship with GridSim. Shapes and RDFS files
are Turtle (`.ttl`) or RDF/XML (`.rdf`, `.xml`); a folder is read for those files. The checker covers targets by class
(with the rdfs:subClassOf closure of the `--rdfs` files; without them the exact rdf:type only, said in the output), by
node, by subject and by object; paths that are a property, an inverse or a sequence; and the constraints sh:class,
sh:datatype, sh:nodeKind, sh:minCount, sh:maxCount, the four range bounds, sh:minLength, sh:maxLength, sh:pattern,
sh:in, sh:hasValue, sh:node, sh:property, sh:and, sh:or, sh:not, sh:xone, sh:equals and sh:disjoint, with severity,
message and deactivation. Every sh:sparql constraint, SPARQL-based target and other constraint is listed as NOT RUN by
name (many ENTSO-E constraints are SHACL-SPARQL). The verdict is one of three. "conforms" means every constraint that
applies was run and none gave a result. "does not conform" means a constraint that was run gave a result. "not
determined" means no result was found, but a constraint that applies was not run. A constraint that holds a part not run
(for example sh:node or sh:not of a shape with sh:closed) is decided only when that part cannot change the answer;
otherwise it is listed as not run too. Even "conforms" covers the SHACL Core subset only and is not a conformity
certificate. CGMES documents carry no datatypes, so an untyped value satisfies sh:datatype when its text is a
valid value of that type. Files are read in path order and the results are sorted, so the report does not depend on the
order the files are named in. `--report` writes every result and every constraint not run as CSV or JSON, never over an
existing file. The exit codes are those of `cgmes-conform`: 0 conforms; 1 the shapes, the RDFS or the bundle cannot be
read, or the report cannot be written; 2 usage; 3 no pass (the bundle does not conform, or the verdict is not
determined).

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary (`conforms`, `verdict`, the counts) and the tables `results` (severity, component, focus, path, value,
shape, message), `not_run` (what, shape, reason) and `notes`.

| Flag | Default | Meaning |
|---|---|---|
| `--shapes` | required | A shapes file or folder; may be given more than once. |
| `--rdfs` | none | The profile RDFS (file or folder) for the rdfs:subClassOf closure. |
| `--profile-filter` | all | Only the bundle's documents of these profiles (eq, tp, ssh, sv, dl, gl, sc, dy, eqbd, tpbd). |
| `--report` | none | Write the report to this new `.csv` or `.json` file. |
| `--census` | none | Read only the shapes named after it and list every SHACL feature they use: the count of each, whether the checker runs it, is inert (labels, targets, the path) or does not run it, the number of `sh:sparql` constraints and SPARQL targets, and the path forms. Exit 0 read; 1 unreadable; 2 usage. The result files hold the summary and the table `features`. |

### line-constants

`gridsim line-constants <geometry.json> [--length-km <km> --base-kv <kV> --base-mva <MVA>]`

`gridsim line-constants --cable <cables.json> [--length-km <km> --base-kv <kV> --base-mva <MVA>]`

Overhead line constants from conductor geometry (Core `GridSim.Core.Lines`): the series impedance and potential
coefficient matrices of the cross-section, reduced to what a power-flow or fault study needs. `<geometry.json>` is a
strict `gridsim-lineconstants/1` document (an unknown or missing key, or a wrong type, exits 1 naming it):

```json
{
  "schema": "gridsim-lineconstants/1",
  "method": "perfect-ground",
  "frequency_hz": 50,
  "earth_resistivity_ohm_m": 100,
  "transposed": true,
  "conductors": [
    {"name": "A", "role": "phase", "x_m": -3.0, "height_m": 12.0,
     "resistance_ohm_per_km": 0.06, "gmr_m": 0.011, "radius_m": 0.014}
  ]
}
```

`method` is `perfect-ground` (image conductors only; earth resistivity is ignored and may be left out) or `earth-integral`
(the exact Pollaczek and Carson earth-return integral by fixed-order quadrature, with its truncation bound reported;
`earth_resistivity_ohm_m` is then required). The series expansions of Carson's integral (`carson-series` and
`carson-modified` in the plan) are not implemented. `role` is `phase` or `ground-wire`; a ground wire is eliminated by
Kron reduction. `resistance_ohm_per_km` is the AC resistance at the stated temperature, an input: no skin-effect or
temperature model is applied. `gmr_m` is the geometric mean radius used for the self reactance and `radius_m` the physical
radius used for the capacitance. `transposed` (default true) averages the phase matrices over a full transposition before
the sequence transform; for an untransposed three-phase line the sequence values use the mean self and mean mutual terms,
stated in `sequence_note`.

The calculation runs in a fixed order and every stage is kept in the result: primitive Z (ohm/km) and Maxwell potential
coefficients P (km/F) over every conductor; the Kron-reduced phase Z and the phase capacitance C (F/km, the inverse of the
reduced P); the transposition average; the sequence constants Z1, Z0 and C1, C0 (three phase conductors only). With
`--length-km`, `--base-kv` and `--base-mva` (all three) the positive-sequence R1, X1 and B1 are also given in per unit
(Zbase = kV squared over MVA; B = omega C1 length Zbase). Exit 0 done; 1 the file is missing or not a valid geometry, or
the geometry is unusable (no phase, coincident or overlapping conductors, a conductor not wholly above ground, a
non-positive frequency); 2 usage (a missing file, a flag the verb does not take, one or two of the three per-unit flags, a
value that is not above 0); 3 per-unit values were asked for but the line has not three phase conductors (the matrices are
still printed and written).

| Flag | Default | Meaning |
|---|---|---|
| `--length-km` | - | Line length for the per-unit values (km). Needs `--base-kv` and `--base-mva`. |
| `--base-kv` | - | Base voltage for the per-unit values (kV). |
| `--base-mva` | - | Base power for the per-unit values (MVA). |
| `--cable` | - | Read a `gridsim-cableconstants/1` file of buried single-core cables instead of an overhead geometry (see below). |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary (method, frequency, sequence constants, tail bound and, when asked, the
per-unit values) and one table per stage (`primitive_z`, `primitive_p`, `phase_z`, `phase_c`, `transposed_z`,
`transposed_c`), each as `row`, `col`, value rows.

#### Single-core cables (`--cable`)

With `--cable <cables.json>` the verb solves buried single-core cables, each with a core, an optional metallic sheath and
an optional armour, instead of an overhead line. The file is a strict `gridsim-cableconstants/1` document:

```json
{
  "schema": "gridsim-cableconstants/1",
  "earth": "wedepohl",
  "bonding": "both",
  "frequency_hz": 50,
  "earth_resistivity_ohm_m": 100,
  "transposed": true,
  "cables": [
    {"name": "A", "x_m": -0.1, "depth_m": 1.0, "outer_radius_m": 0.0365,
     "conductors": [
       {"inner_radius_m": 0, "outer_radius_m": 0.0145, "resistivity_ohm_m": 1.72e-8,
        "insulation": {"relative_permittivity": 2.5, "loss_tangent": 0.0004}},
       {"inner_radius_m": 0.0315, "outer_radius_m": 0.0325, "resistivity_ohm_m": 2.83e-8,
        "insulation": {"relative_permittivity": 2.3}}]}
  ]
}
```

Each cable lists its conductors from the inside out: the core first (an `inner_radius_m` of 0 is a solid core, above 0 a
hollow one), then the sheath, then the armour. `insulation` is the layer outside that conductor; it runs to the next
conductor's inner radius, or to `outer_radius_m` for the outermost layer, so every cable needs an outer jacket.
`relative_permeability` (conductor or insulation) defaults to 1 and `loss_tangent` to 0. A stranded core is entered with
the resistivity that gives its DC resistance on the stated radius. Semiconducting screens count as part of the
insulation: enter a permittivity already corrected for them if a correction is wanted. `depth_m` is the depth of the
cable's centre below the surface.

The calculation follows Ametani's general formulation (IEEE Trans. PAS-99, 1980) and the cable constants chapter of the
EMTP Theory Book. Each conductor's surface impedances are Schelkunoff's, with the modified Bessel functions of complex
argument computed natively; the loop impedances (core to sheath, sheath to armour, outermost layer to earth) are
transformed to the conductor matrix; the insulation gives C = 2 pi eps0 eps_r / ln(r_o / r_i) and G = omega C tan(delta)
per layer. `earth` picks the earth-return formula: `wedepohl` is Wedepohl and Wilcox's closed form, which is accurate
while |m_e| times the larger of the spacing and the depth sum is small (a warning is printed above 0.05, where its error
reaches about 0.1%); `pollaczek` is the exact Pollaczek integral by fixed-order quadrature, with its truncation bound
reported. `bonding` is `both` (sheaths and armour earthed at both ends and eliminated by Kron reduction), `single` (bonded
at one point: no current in the metallic layers) or `cross` (ideal cross-bonding: the impedance averaged over the three
positions each metallic layer takes along a major section, then eliminated; three cables with the same metallic layers:
a sheath, and an armour on all or none). In every
case the metallic layers are at earth potential for the capacitance, so the phase C and G are those of the core
insulation. `transposed` (default true) works as for overhead lines.

Every stage is kept: the primitive Z and P over every conductor, named `<cable>.core`, `<cable>.sheath` and
`<cable>.armour`; the conductor admittance (`conductor_y`, G and B in S/km); the bonded phase Z, C and G (`phase_g`); the
transposition average and the sequence constants. The summary adds `earth`, `bonding`, `cables`,
`wedepohl_argument_max` and `warnings`. Exit codes are as for overhead lines; a cable that overlaps another, lies above
ground, has layers out of order, or a cross-bonded system that is not three cables with the same metallic layers, exits 1
naming it.
Scope: single-core cables in homogeneous earth. Pipe-type and three-core cables are not modelled, and there is no
proximity effect between cables beyond the earth-return coupling. The values are verified against closed forms (the DC
resistance, the skin-effect asymptote, the coaxial capacitance) and against an independent 30-digit mpmath calculation of
the same formulas; no published worked example with complete data was available to compare against.

### dgs-import

`gridsim dgs-import <file.dgs> --out <case-dir> [--accept-unconfirmed] [--strict]`

Imports an ASCII PowerFactory DGS file (RC4 R4-11 part C, Core `GridSim.Core.IO.Dgs`) through the node-breaker model of
[substation-state](#substation-state): every DGS terminal is a connectivity node, every coupler and cubicle switch is a
switch, and the bus-branch case is the projection at the switch states the file states (`on_off`). Every object keeps its
DGS class and ID. **Status: built from public descriptions only.** No real PowerFactory export has been tested
(acceptance, the DGS write and round trip, and the migration report on real data are AWAITING FILES), and nothing here
claims PowerFactory behaviour. Every result and the report carry the provenance label `FormatFromPublicDescription` (an
output tag: never an input to anything).

The reader accepts ONE documented dialect: tables headed `$$Class;column(type[:length]);...` (types `a` text, `i`
integer, `r` real, `p` pointer), `;`-separated rows (optional double quotes), a `General` table with a `Version` row (only
`5.0`), every other table keyed by an `ID` that is unique across the file. What public sources confirm is the table
structure, the mandatory version, DGS 5.0 and the attribute names listed as Confirmed in `IO/Dgs/DgsClassMap.cs` (sources:
PowSyBl's PowerFactory import documentation and OPAL-RT's ePHASORSIM documentation; the DIgSILENT DGS manual itself was not
read). Everything else (the delimiter, the cubicle `StaCubic` connection scheme, `ElmSubstat` folders, the transformer and
generator attribute names) is UNCONFIRMED and is a stated choice. A file outside the dialect is refused with its line number
(exit 1); the reader never guesses.

The class map covers `ElmTerm`, `ElmSubstat`, `StaCubic`, `StaSwitch`, `ElmCoup`, `ElmLne` with `TypLne`, `ElmTr2` with
`TypTr2`, `ElmLod`, `ElmSym`, `ElmXnet` and `ElmShnt`, each attribute mapped, approximated, carried or refused. An object
that needs an UNCONFIRMED attribute is refused by name unless `--accept-unconfirmed` is given, and every use of one is then
listed. Every table of a class the importer does not interpret is listed by name with its row count (and marked
"known, not implemented" for classes such as `ElmTr3`, `ElmVsc`, `ElmGenstat`): nothing is dropped silently. Out-of-service
elements (`outserv` = 1) are left out and listed; columns present but not used are listed per class; a terminal no imported
object uses is left out of the model and listed. The case is INCOMPLETE when anything is refused or unsupported: the files are
still written and `--strict` then exits 3.

`--out` is the case folder (this is not a result-file verb: `--json` and `--csv` are refused). It receives
`substations.json` (the native `gridsim-substations/1` node-breaker file), `case.m` (the projected bus-branch case: solve it
with `gridsim pf case.m`) and `dgs-import-report.json` (`gridsim-dgs-import/1`: provenance, every object with its outcome and
the GridSim ids it produced, unsupported classes, unread columns, UNCONFIRMED mappings used, the General entries and the
bus map). Existing files are never overwritten (an existing file in the folder is exit 1). Exit 0 done; 1 the file is refused,
an output exists or nothing could be imported; 2 usage; 3 with `--strict` when the import is incomplete.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | required | The case folder to write (created if needed). |
| `--accept-unconfirmed` | off | Use attribute mappings whose name or meaning no public source confirms (each use is listed). |
| `--strict` | off | Exit 3 when any object was refused or any class is unsupported (the files are still written). |

### scl-import

`gridsim scl-import <file.scd> --params <params.json> --out <case.substations.json> [--assume-closed]`

Imports the substation section of an IEC 61850 SCL file (an SSD, SCD or any other SCL file; Core `GridSim.Core.IO.Scl`)
into the node-breaker model of [substation-state](#substation-state) and writes it as a `gridsim-substations/1` file. SCL
describes topology and equipment, not electrical values, so impedances and set points come from a `gridsim-scl-params/1`
file keyed by SCL path. See [technical ch. 45](<../technical/0045 IEC 61850 SCL substation import.md>).

What is imported: each `ConnectivityNode` becomes a node (its id is the `pathName`); a `CBR` becomes a breaker and a `DIS`
a disconnector, or an earth switch when one terminal is grounded; `LIN` and `CAB` become lines (one piece with two
terminals, or the two one-terminal halves a file holds for a line between substations); `GEN` becomes a generator and
`IFL` an external grid (a slack generator); a `PowerTransformer` with two windings becomes a transformer and with three a
three-winding transformer. `CTR` and `VTR` are kept as measurement points and play no part in the topology; a current
transformer with two terminals sits in series, so its two nodes are merged into one. Loads are not in SCL: they come from
the params file, at a node's `pathName`. Every other type code is refused by name, and so is the whole import: leaving out
a capacitor or a motor would change the network. The SCL substation section holds no switch state, so every switch needs
its state (`normalOpen`) in the params file: a switch without one is refused, each named. With `--assume-closed` such a
switch is taken as closed instead, listed in the report and recorded in the written file's `notes`. A current transformer
whose two terminals are in different voltage levels is refused (it has no ratio to join them).

The Communication, IED and DataTypeTemplates sections are read past and counted. GOOSE control blocks are listed by name
only. Elements of the substation section that are not read (`Function`, `LNode`, `GeneralEquipment`, `SubEquipment`,
`TapChanger`, `Private`, ...) are listed as NOT IMPORTED with their count. Any such element that holds conducting
equipment or a power transformer (a `Function` or `SubFunction` may) is refused, as is a top-level `Line` or `Process`
section that holds equipment: that equipment would otherwise be lost. The params file's values are range-checked: `vPu`
and `tapRatio` above 0, a branch impedance not exactly 0, ratings at least 0, `qMinMvar` not above `qMaxMvar` and
`pMinMw` not above `pMaxMw`.

The file is untrusted XML: a file with a DTD (so any entity, internal or external) is refused, elements may nest at most
64 deep, and the file size is capped like every other input file.

The report (mapped, measurement points, NOT IMPORTED, read past, GOOSE control blocks, notes) is printed, with the bus and
branch count of the projection at the imported switch states. The output is never an existing file. Not on the shared
`--json`/`--csv` writer. Exit 0 imported; 1 the SCL file or the params file is refused or unreadable, or the output cannot
be written; 2 usage (a missing flag, or an output that already exists).

| Flag | Default | Meaning |
|---|---|---|
| `--params` | required | The `gridsim-scl-params/1` file: lines, transformers, three-winding transformers, generators, infeeds, loads and switch states. |
| `--out` | required | The `gridsim-substations/1` file to write (never an existing one). |
| `--assume-closed` | off | Take a switch with no state in the params file as closed instead of refusing it; each is listed and recorded in the output file's `notes`. |

### dist3ph-mesh

`gridsim dist3ph-mesh <case.json> [--steps <steps.json>] [--warm-start] [--tol <A>] [--max-iter <N>] [--scale <F>] [--pin-floating] [--hold-taps]`

`gridsim dist3ph-mesh --from-dist3ph <feeder.json> [--switch-z-pu <pu>] [--steps <steps.json>] [--tol <A>] [--max-iter <N>] [--scale <F>]`

Meshed, unbalanced load flow in the explicit phase frame (Core `GridSim.Core.Distribution.Meshed`). It is a new verb beside
`dist3ph`, which is unchanged: `dist3ph` is the radial forward-backward sweep on Kron-reduced 3x3 matrices, `dist3ph-mesh` solves
any topology (rings included) with every conductor a named wire (a, b, c, n, l1, l2, e, ...), the full primitive impedance matrix
of each branch (a four-wire line is 4x4, neutral included, nothing pre-reduced), explicit earthing and loads by connection. A
radial case agrees with `dist3ph` within a stated tolerance (a different algorithm, so not bit identity); `dist3ph` is not replaced.
Units are SI: volts to earth, ohms, siemens, volt-amperes, amperes. `<case.json>` is a strict `gridsim-dist3ph-mesh/1` document
(an unknown or missing key, a duplicate key or a wrong type exits 1 naming it). The licence's bus limit applies to it as to
every network, one bus per node of `nodes` (two for a `swer` case): a case over the limit is refused with exit 2 before it
is solved, as `--from-dist3ph` refuses a feeder. An example:

```json
{
  "schema": "gridsim-dist3ph-mesh/1",
  "name": "two-bus four-wire feeder",
  "nodes": [ {"name": "S", "wires": ["a","b","c","n"]}, {"name": "L", "wires": ["a","b","c","n"]} ],
  "sources": [ {"node": "S", "wires": ["a","b","c"], "magnitude_v": [7200,7200,7200], "angle_deg": [0,-120,120]} ],
  "branches": [ {"name": "line", "from": "S", "to": "L", "wires": ["a","b","c","n"],
                 "r_ohm": [[0.50,0.10,0.09,0.10],[0.10,0.52,0.11,0.10],[0.09,0.11,0.49,0.09],[0.10,0.10,0.09,0.80]],
                 "x_ohm": [[1.20,0.50,0.42,0.46],[0.50,1.15,0.48,0.44],[0.42,0.48,1.18,0.40],[0.46,0.44,0.40,1.30]]} ],
  "loads": [ {"name": "load", "node": "L", "connection": "wye", "nominal_v": 7200,
              "p_w": [60000,40000,90000], "q_var": [25000,12000,30000], "zip": [1,0,0]} ],
  "earthing": [ {"node": "S", "wire": "n", "r_ohm": 0} ]
}
```

Members: `nodes` (name and wires; a wire is a separate unknown at every node that lists it), `sources` (an ideal source fixes the
wires it names; `series_r_ohm` and `series_x_ohm`, one per wire, put it behind an impedance, for example a source earth electrode),
`branches` (`wires` in matrix order; `r_ohm` and `x_ohm` are the total matrices; optional `shunt_g_siemens` and `shunt_b_siemens`
to earth, half at each end; optional `kron_wire` declares that wire a multi-grounded neutral, eliminated by a recorded Kron
reduction, the only reduction ever applied), `loads` (`connection` is `wye` with a `return_wire`, default n, `wye-earthed`,
`wye-floating` with an internal star point, `delta` with legs a-b, b-c, c-a, or `line-line` with `from` and `to`; `p_w`, `q_var` per
leg at `nominal_v`, the leg's nominal voltage; `zip` is the constant-power, constant-current and constant-impedance fractions,
summing to 1, default constant power), `earthing` (a grounding impedance from a wire to earth, or to a named `grid`; zero is a
solid connection), `grids` (an earth grid with an impedance to remote earth, whose voltage is the ground potential rise) and
`transformers` (single-core units of two or more windings, each between `wire` and `return_wire`, or earth when `return_wire` is
omitted, with signed `turns` proportional to rated voltage and a non-zero leakage impedance; a three-phase bank is three units; a
centre-tapped 120/240 V service is one unit of three windings).

Three more optional members carry the elements of a distribution feeder that the primitive units alone would make you
build by hand (Core `RegulatedFrame`, `StepRegulators`, `TransformerBanks`). They are additive: a case without them
is read and solved exactly as before.

- `regulators`: step-voltage regulators between two nodes. Members: `name`, `from`, `to`, `connection` (`wye`, `open-delta` or
  `closed-delta`), `phases` (wye only: the regulated wires, for example `["a","b","c"]`), `taps` (integers, one per listed
  phase for wye, `[ab, cb]` for open delta, three equal values for closed delta; the ratio of a wye phase is
  `1 + step_pu * tap`), and optional `step_pu` (default 0.00625), `max_tap` (default 16), `winding_ohm` (default 1e-5, the
  ideal unit's tiny winding impedance) and `return_wire`. A wye regulator may add `ldc`, one line-drop compensator
  setting per listed phase (`vset_volts`, `bandwidth_volts`, `pt_ratio`, `ct_primary_amps`, optional `r_volts` and `x_volts`, on the
  120 V relay base), and `ganged` (the first phase's compensator controls the bank). Without `ldc` the taps are fixed.
  With `ldc` the single solve runs the compensator tap search: the load flow is repeated, one tap step per compensated
  phase and pass, until every phase is in its band or at a tap limit (Core `RegulatorTapSearch`); the taps found, the relay
  voltage and the band are printed and written. The search is refused with `--steps` (exit 2): give fixed taps there, or add
  `--hold-taps`.
- `banks`: three-phase transformer banks built from single-phase units. Members: `name`, `from`, `to`, `connection`
  (`grounded-wye-grounded-wye`, `delta-grounded-wye`, `wye-delta`, `delta-delta`, `open-wye-open-delta` or
  `open-delta-open-delta`), `kv_ll_from`, `kv_ll_to` (rated line-to-line kV), `unit_kva` (one single-phase unit),
  the impedance either as `r_percent` and `x_percent` (the total, split equally between the windings) or as
  `z_from_pu` and `z_to_pu` (`{"r": .., "x": ..}` on each winding's own base), optional `magnetising_pu` (`{"g": .., "b": ..}` on the
  from winding's base) and `from_return` / `to_return` (the return wire of a grounded-wye side; earth when omitted). An
  ungrounded-wye bank adds an internal star node, and a delta secondary is galvanically separate, so give it an earthed
  load or use `--pin-floating`. The bank's units and nodes are part of the result (their names start with the bank's name).
- `capacitors`: shunt capacitor banks, wye to earth. Members: `name`, `node`, `phases` (the wires with a unit), `q_var`
  (the reactive power supplied per phase at `nominal_v`, not negative) and `nominal_v` (line to earth). A bank is a
  constant-impedance leg (the model the incumbent `dist3ph` adapters use), so it scales with the voltage squared. It is not
  a load: the load factors (`--scale`, a step's `scale`) are divided back out of it, so only a step's `capacitors` member
  changes it. A zero load factor with a capacitor in the case is refused (exit 1), because it cannot be divided out.

Three more optional members are Core's North American split-phase and rural single-wire builders (`SplitPhase`, `Swer`). Each is
additive: a case without them is read as before.

- `split_phase_sources`: a 120/240 V source. Members: `node` (a node whose wires include `l1`, `l2` and `n`), `volts` (the full winding
  voltage, for example 240, above 0) and optional `series_r_ohm` and `series_x_ohm` (an impedance in each line). It fixes `l1` at
  +volts/2, `l2` at -volts/2 and the neutral `n` at 0 V, so with it the `sources` member may be left out.
- `centre_tap_transformers`: a centre-tap service transformer as one three-winding unit. Members: `name`, `primary_node`,
  `primary_wire`, optional `primary_return` (earth when omitted), `primary_volts` (rated, above 0), `secondary_node` (wires `l1`, `l2`, `n`),
  `secondary_volts` (the full secondary, for example 240; each half is half of it), `primary_r_ohm` and optional `primary_x_ohm`, `half_r_ohm`
  and optional `half_x_ohm` (leakage on each winding's own side). Magnetising current is neglected. The centre tap is earthed by an
  `earthing` entry on the secondary's `n`, as in the other transformer members.
- `swer`: a complete single-wire earth-return feeder, and the only member of its case (with `schema` and `name`; any other member is
  refused). Members: `emf_v` and optional `emf_angle_deg`, `source_earth_r_ohm` and `source_earth_x_ohm` (the source electrode),
  `line`, `load_earth_r_ohm` and `load_earth_x_ohm` (the load-end electrode) and `load` (`p_w`, `q_var`, `nominal_v` and an optional
  `zip`, default constant impedance). `line` is either the total impedance, `r_ohm`, `x_ohm` and an optional `shunt_b_siemens`, or a
  conductor and a length, `length_km`, `frequency_hz`, `earth_resistivity_ohm_m` and `conductor` (`resistance_ohm_per_km`, `gmr_m`,
  `radius_m`, `height_m`), from which Core's earth-return line constants give the impedance (the earth-return resistance included) and,
  with `"charging": true`, the shunt capacitance. The nodes are `S` (wire `a`) and `L` (wires `a` and `e`), the branch is `line` and the
  load `load`; the earth return of the line is the `branch_earth_return` table.

A step may switch banks: in the steps file a step's `"capacitors": {"C1": 0}` takes bank `C1` out of service for that
step (`1` puts it in, and is the default); any other value is refused. Taps are not switched by a step: a regulator
keeps the taps written in the case (or found by the search) for every step.

The calculation runs in a fixed order: validate wires and earthing; build the primitive admittance in element order; eliminate
nothing implicitly; start from the no-load solution (flat per phase when there are no transformers or shunts; flat by wire name
if that is singular); Newton in the phase frame with a fixed policy (full steps, no damping); the residual in amperes; report.
A case that cannot be solved as given is reported INCOMPLETE with the reasons named, never a number: a wye load whose neutral wire
is absent, a delta load with a wire missing (no return path), a connected set of wires with no reference to earth (a neutral that
is earthed nowhere and tied to nothing), a branch whose impedance matrix is singular. `--pin-floating` instead pins the first wire
of each unreferenced component at 0 V and records that in the notes (the usual case is an isolated, ungrounded secondary, where only
differences are physical). With `--steps` the verb runs a quasi-static series: `<steps.json>` is a strict
`gridsim-dist3ph-mesh-steps/1` document, `{"schema": "gridsim-dist3ph-mesh-steps/1", "steps": [{"label": "t0", "scale": 0.4,
"load_scale": {"load": 0.5}}]}`. By default every step is independent, so any step is reproducible alone and bit-identical to a
single run with the same factors; `--warm-start` starts each step from the previous converged step's solution instead (the same
answer within the tolerance, not claimed bit-identical). Per node and wire the result gives the complex voltage; per branch the
current of every conductor at both ends, neutral included, and the earth-return residual (the sum of the conductor currents,
which must return through earth; for a single-wire earth-return line it is the line current); per load leg the voltage, current and
power; source, earthing and grid currents. Exit 0 every step converged; 1 the file is missing or not a valid case; 2 usage; 3 a
step did not converge or is INCOMPLETE, or a compensator tap search did not settle (or, with `--hold-taps`, a compensated phase is
outside its band).

| Flag | Default | Meaning |
|---|---|---|
| `--steps` | - | A `gridsim-dist3ph-mesh-steps/1` file: solve one step per entry with its load factors. |
| `--warm-start` | off | Start each step from the previous converged step (needs `--steps`). |
| `--tol` | 1e-6 | Convergence limit on the largest current mismatch at any free unknown (amperes, above 0). |
| `--max-iter` | 40 | The iteration limit (at least 1). |
| `--scale` | 1 | A factor on every load's power (not below 0), applied before any step factors. |
| `--pin-floating` | off | Pin a reference on each unreferenced wire component instead of reporting INCOMPLETE. |
| `--from-dist3ph` | - | A `dist3ph` feeder file to lift instead of a mesh case (see below). |
| `--switch-z-pu` | - | With `--from-dist3ph`: the per-unit series impedance given to a closed switch (above 0). A closed switch is refused without it. |
| `--hold-taps` | off | With a regulator that has `ldc`: keep the written taps and report where each compensated phase stands against its band, without searching (single solve; also lets `--steps` run a case with `ldc`). |

`--from-dist3ph <feeder.json>` takes the place of `<case.json>`: it reads an existing `dist3ph` feeder file (v1 or `gridsim-dist3ph/2`,
the file the [dist3ph](#dist3ph) verb reads) and lifts it into the phase frame with Core's `MultiPhaseFeederAdapter`, so a feeder does not
have to be written again as a mesh case. The per-unit feeder becomes SI on each bus's own base (nodes named `bus<id>`, wires a, b, c), and its
transformers (delta / grounded wye, ungrounded wye / delta, delta / delta, open wye / open delta, open delta / open delta) and
step-voltage regulators come with it, at the taps the feeder file states (no compensator, so the taps are fixed). A closed switch is refused by
name unless `--switch-z-pu` gives it a series impedance (the Core tests use 1e-6). A feeder that holds something the frame does not carry (a
regulator ratio off the 0.00625 tap grid, a tap beyond 16, more than one transformer flag on a branch) is not solved: each such element is
listed on standard error and the exit code is 1. A delta secondary leaves a floating component, so the first wire is pinned as the
reference automatically (a note says so; compare the delta nodes line to line). Giving both `<case.json>` and `--from-dist3ph`, or
`--switch-z-pu` without `--from-dist3ph`, exits 2. The result is the usual one with the summary member `lifted_from_dist3ph` and an
`adapter_notes` table; `--steps`, `--scale` and the solver options work as for a mesh case. `dist3ph` is unchanged, and a lifted radial
feeder agrees with it only within the solver tolerances (a different algorithm).

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary (case, `case_sha256`, steps, whether all converged and, for a single solve, the status,
iterations and mismatch) and the tables `steps`, `voltages`, `branch_currents`, `branch_earth_return`, `loads`, `sources`,
`earthing` and `grids`, one row set per step (the `step` column reads `-` for a single solve). A case with regulators adds a
`regulators` table (`regulator,connection,phase,tap,ratio,relay_v,band_low_v,band_high_v,in_band,at_limit`, the last four
filled for a compensated phase) and, when a search ran, the summary members `tap_search` (`SETTLED`, `HUNTING`, `NOTSETTLED` or
`LOADFLOWFAILED`), `tap_search_load_flows` and `taps_held`. A tap search that does not settle (the controllers would hunt, the
pass limit is reached, or a load flow inside it fails) exits 3.

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
(series reactance with tap ratios, phase shifts and bus shunt conductance, as MATPOWER `makeBdc`). See technical chapter 29 for the formulation and its validation.

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

### acopf-opt

`gridsim acopf-opt <case> [--objective cost|losses|rpo] [--controls <file.json>] [--rounding flip|neighbours] [--max-iter 150] [--out <file.csv|file.json>]`

The AC optimal power flow of [`acopf-full`](#acopf-full) (the same MIPS interior point method, copied, not changed)
with a choice of objective and with continuous transformer taps and bus shunts as controls (`AcOpfExtended`).
`cost` minimises the units' polynomial cost curves, as `acopf-full`. `losses` minimises the sum of the units' output,
which is the losses plus the fixed demand (a linear cost of 1 per MW on every unit). `rpo` (reactive power optimisation)
is `losses` with every unit that is not at a slack bus held at its case output, so the controls are the units' reactive
output (through the voltages), the taps and the shunts. `losses` and `rpo` accept units with no cost curve; `cost`
still refuses them. With no controls file, `cost` gives the same bits as `acopf-full`.

The controls file (`gridsim-opf-controls/1`, every member checked, all optional):

```
{ "format": "gridsim-opf-controls/1",
  "taps": ["T1", "T2"],
  "shunts": [ { "bus": 2, "bMinMvar": 0, "bMaxMvar": 100, "stepMvar": 10, "initialMvar": 0 } ] }
```

`taps` lists transformers by their unique branch name, or is the word `"oltcs"` for every OLTC of the case. A tap's
range and step come from its OLTC record, else 0.9 .. 1.1 in steps of 0.0125; the variable is 1/tau on the from side
(the MATPOWER branch model). A shunt adds a susceptance (MVAr at 1 pu, capacitive positive) to the bus's own case shunt.

`pst` adds phase-shifter angle controls, one per branch, after the shunts:

```
"pst": [ { "branch": "PS1", "minDeg": -30, "maxDeg": 30, "stepDeg": 1 } ]
```

The angle is the MATPOWER shift (positive as SHIFT) in degrees. `minDeg` and `maxDeg` default to the range of the
branch's active power flow control (PSS/E COD 3) and are needed when it has none; `stepDeg` defaults to that control's
step, else 0, which means the angle is continuous and is not rounded. The branch keeps its case tap ratio, and a branch
cannot have a tap control as well. An optional `"kind": "pst"` member is accepted.

After the continuous optimum each control is rounded to its grid (tau = MinTap + n step, B = bMinMvar + n step), to the
nearest point; a tie goes to the point nearer the case value (`initialMvar` for a shunt), then to the lower. The controls
are then fixed and the same objective is solved again. If that does not converge or breaks a limit, each control in
turn (taps first, in file order) is flipped to its other neighbour, and the first flip that converges within every limit
is kept. If none does, the run prints `rounded point infeasible` and the continuous optimum. This is a stated heuristic,
not a mixed-integer optimum.

`--rounding neighbours` solves every floor/ceil combination of the discrete controls instead (2^n solves for n controls
with two neighbours, in Gray-code order) and keeps the best feasible one; a tie goes to the lowest combination index.
More than 12 discrete controls are refused. The result is optimal over the floor/ceil neighbours of the continuous
optimum. It is not a global mixed-integer optimum. It ends `BestNeighbour`, or `NoNeighbourFeasible` (exit 3) when no
combination is feasible, and the result table gains `rounding_mode`, `rounding_combinations` and
`rounding_combination` in its summary.

Prints the continuous optimum (objective, generation, load, losses, every unit's P and Q, every control's value, the
binding limits), the rounding of each control, how the rounding ended and the discrete re-solve. Exit 0 done. Exit 1 for
a case or controls file it refuses (the reasons of `acopf-full`, an unknown member or name, a branch that is not a
transformer, crossed ranges). Exit 2 for an unknown `--rounding` mode. Exit 3 when the continuous optimum does not
converge (the iteration log is printed on stderr, no dispatch is shown) or the rounded point is infeasible.

When the continuous optimum fails, stderr names the status: `acopf-opt: not converged: ...` (the iteration cap),
`acopf-opt: numerical failure: ...` (the iteration broke down), or `acopf-opt: no solution: likely infeasible: ...`.
The last is status `Infeasible`: the run broke down after the feasibility measure stayed above its tolerance and above
half its value of four iterations before while the complementarity grew. That is the signature an interior point
method shows on a problem with no feasible point. It is evidence of infeasibility, not a proof. The message gives the
iterations and values of that window, the reason the iteration stopped, and the inequality constraints with the largest
final multipliers (at least a thousandth of the largest, eight at most), which are usually the ones in conflict. An
independent phase-1 solve (for example raising the ratings until a solution exists) confirms it.

Result files: `--json <file.json>` or `--csv <file.csv>` write the result to a file through the shared writer ([Result files](#result-files)): the `summary` rows as summary values and every row of the result table as the table `rows` (record, element, field, value). `--out` is unchanged: it still names its own result table, not a shared-writer file.

| Flag | Default | Meaning |
|---|---|---|
| `--objective` | cost | `cost`, `losses` or `rpo`. |
| `--controls` | none | A `gridsim-opf-controls/1` file of taps, shunts and phase-shifter angles to optimise. |
| `--rounding` | flip | `flip` (nearest point, then one flip at a time) or `neighbours` (the best floor/ceil combination, at most 12 controls). |
| `--max-iter` | 150 | Iteration cap of each interior point solve; at least 1. |
| `--out` | none | Write the result table (`record,element,field,value`) as CSV, or as JSON for a `.json` file. |

### acscopf

`gridsim acscopf <case> [--mode preventive|corrective] [--ramp-mw <MW> | --ramp-file <file.json>] [--contingencies all|<i,j,...>] [--outages <file.json> | --n2 pairs|<file.json> [--max-outages 5000]] [--post-rating A|B|C] [--objective cost|losses] [--max-rounds 20] [--max-iter 150] [--out <file.csv|file.json>]`

AC security-constrained optimal power flow (`AcScopf`). The base case and each secured branch outage are network
blocks of one stacked AC-OPF, solved by the MIPS interior point method of `acopf-full`. Preventive: every unit not at a
slack bus keeps its base-case output after the outage. Corrective: each such unit may move by at most its ramp
(`--ramp-mw` for every unit, or `--ramp-file`, `{"format": "gridsim-opf-ramps/1", "rampMw": [...]}` with one ramp per
in-service unit). Slack-bus units take the change in losses; reactive output and voltages are free within their limits
in every block (post-outage voltage control is allowed). The objective is the base case's. Post-outage branch limits use
the rating rule of [`scopf`](#scopf).

Outages enter by constraint generation: after each solve every listed outage not yet in the problem is screened with an
AC Newton power flow (the base-case dispatch, the base-case voltages as set-points, reactive limits on, the slack taking
the losses); every outage that does not converge, breaks a post-outage rating by more than 1e-6 MVA or a voltage limit
by more than 1e-6 pu is added, in index order, and the problem is solved again from scratch, until none is added. An
outage that islands the network is listed and not secured. Then every secured outage is screened again at the final
dispatch (in corrective mode with its block's unit outputs). Its block let the unit voltages move after the outage and
the screen holds them, so one can still fail; the run then ends `SecuredOutageFailsScreen`, naming those outages.

Generator and N-2 outages: `--outages <file.json>` reads a list of outages, each removing branches, units or both:

```
{ "format": "gridsim-scopf-outages/1",
  "outages": [ { "branches": [3, 4], "name": "double circuit" }, { "generators": [1] }, { "branches": [2], "generators": [0] } ] }
```

Branches are indices into the case's branch list and units into its in-service unit list, both from 0. In a block the
removed units' P and Q are held at 0 and have no coupling row. Their output is shared by governor droop: each remaining
synchronous unit with a dispatch range takes a fixed share in proportion to its effective MVA base (its MBASE, or the
system base when it has none), held there (preventive) or within its ramp
of it (corrective), and the units at the reference bus take their own share and the change in losses. Each unit's limits
hold after the outage, so it keeps room for its share. An outage of every unit at a slack bus moves the reference to the
bus of the largest remaining unit; taking out every unit is refused. Power-flow-only units and converter-interfaced
units (wind, solar, battery) take no share, as in the contingency screen's governor-droop mode. The screen of an outage that removes a unit shares
the output the same way, and fails when a unit's share takes it outside its range or the reference bus's P or Q leaves
its units' range.
`--n2 pairs` makes every pair of the `--contingencies` branches (or of every in-service branch) in index order; more than
5000 pairs are refused unless `--max-outages` raises the cap. `--n2 <file.json>` reads an outage file whose every entry
is a pair of branches. With either flag the outage numbers in the output are positions in the list, and the result
table gains an `outage` / `label` row for each.

Prints each round (outages in the problem, iterations, objective, outages added), the secured, screened-ok and islanding
outages, the base-case optimum, each secured block's dispatch and binding limits, and the binding ramp limits. Exit 0
optimal. Exit 1 for input it refuses (a bad outage list, outage file or ramp file, an outage of every unit, a pair
count above the cap, the reasons of `acopf-full`). Exit 2 for `--outages` with `--n2` or `--contingencies`, or
`--max-outages` without `--n2`. Exit 3 when a solve
does not converge, the round cap is reached or a secured outage fails its screen at the final dispatch; no dispatch is
printed.

Result files: `--json <file.json>` or `--csv <file.csv>` write the result to a file through the shared writer ([Result files](#result-files)): the `summary` rows as summary values and every row of the result table as the table `rows`; a run that is not optimal writes nothing. `--out` is unchanged: it still names its own result table, not a shared-writer file.

| Flag | Default | Meaning |
|---|---|---|
| `--mode` | preventive | `preventive` or `corrective`. |
| `--ramp-mw` | none | Corrective: the ramp (MW) of every unit. |
| `--ramp-file` | none | Corrective: a `gridsim-opf-ramps/1` file with one ramp per in-service unit. |
| `--contingencies` | all | `all`, or a comma list of branch indices (from 0). With `--n2 pairs`, the branches to pair. |
| `--outages` | none | A `gridsim-scopf-outages/1` file of outages (branches, units or both) in place of `--contingencies`. |
| `--n2` | none | `pairs` (every pair of the listed branches) or a `gridsim-scopf-outages/1` file of branch pairs. |
| `--max-outages` | 5000 | With `--n2`: the most pairs allowed. |
| `--post-rating` | A | The rating after an outage: A, B or C (A where B or C is not given). |
| `--objective` | cost | `cost` or `losses` of the base case. |
| `--max-rounds` | 20 | The most solves constraint generation may take; at least 1. |
| `--max-iter` | 150 | Iteration cap of each interior point solve; at least 1. |
| `--out` | none | Write the result table as CSV, or as JSON for a `.json` file. |

### reconfig

`gridsim reconfig <case> [--switchable <i,j,...>] [--respect-ratings] [--min-gain-mw 1e-6] [--max-swaps 100] [--out <file.csv|file.json>]`

Distribution feeder reconfiguration for loss reduction by branch exchange, best improvement (`FeederReconfiguration`;
Civanlar et al. 1988, Baran and Wu 1989). The start is the case's in-service branches, which must be radial and connected
(a meshed or disconnected start is refused, naming the loop or the bus). The switchable branches are every branch that
is not a transformer, or `--switchable` (a transformer there is refused). Each pass tries, for every open switchable
branch in index order, closing it and opening each in-service switchable branch on the loop it makes, in index order;
each trial is solved by the backward/forward sweep of `pf` and rejected if it does not converge, a bus falls below its
lower voltage limit (else 0.94 pu) or, with `--respect-ratings`, a branch is above RATE_A. The trial of lowest loss is
taken (a later one wins only when lower by more than 1e-9 MW, so ties go to the lower closed, then opened, index), and it
stops when the best saving is below `--min-gain-mw` or at `--max-swaps`. Branch exchange is a local search: the result
is not guaranteed to be the global optimum. Balanced feeders only (not `dist3ph`). A MATPOWER `.m` case is read with
its out-of-service (status 0) branches kept as open switches (every other verb drops them), so the tie switches of the
published feeders, such as case33bw's branches 33 to 37, are there to close.

Prints the base losses, each swap (closed, opened, losses after), the final open branches, losses and minimum voltage.
Exit 0 done. Exit 1 for a start or switchable list it refuses.

Result files: `--json <file.json>` or `--csv <file.csv>` write the result to a file through the shared writer ([Result files](#result-files)): the `summary` rows as summary values and every row of the result table as the table `rows`. `--out` is unchanged: it still names its own result table, not a shared-writer file.

| Flag | Default | Meaning |
|---|---|---|
| `--switchable` | every non-transformer branch | A comma list of branch indices (from 0) that may be switched. |
| `--respect-ratings` | off | Reject a configuration with a branch above RATE_A (no value). |
| `--min-gain-mw` | 1e-6 | Stop when the best swap saves less than this (MW). |
| `--max-swaps` | 100 | The most swaps to make; reaching it is reported. |
| `--out` | none | Write the result table (`record,element,field,value`) as CSV, or as JSON for a `.json` file. |

### dist-opt

`gridsim dist-opt <case> --mode capplace|vvo|phase-balance [--params <file.json>] [--out <file.csv|file.json>]`

Distribution optimisation in three modes. Each is deterministic: every search runs in a fixed order, uses no random
numbers, and gives ties to the lowest index. The settings come from a `gridsim-distopt/1` JSON file (`--params`), which
is read strictly: an unknown member, or a section for another mode, is refused by name.

- `capplace`: shunt capacitor placement and sizing on a balanced case. The objective (after Grainger and Lee, IEEE
  Trans. PAS-100(3), 1981) is the annual loss cost (losses x `energyCostPerMwh` x `hoursPerYear`) plus the capital cost
  (`fixedCostPerBank` per installed bank plus `costPerMvar` per Mvar). Each candidate bus has a list of bank sizes in
  Mvar, starting at 0 (`candidates`, or `defaultStepsMvar` for every PQ bus). A bank is a constant admittance added to
  the bus shunt. The search is a heuristic: greedy by marginal benefit (each round tries every candidate at its next
  size with a full Newton power flow and keeps the best), then a confirmation over every combination of one size down,
  no change or one size up on the buses holding a bank (3^m power flows). Above 8 banks the confirmation covers the 8
  the greedy search changed last, and the result lists the others as `unconfirmed`. A case with an OLTC or a
  transformer flow control is refused (the trial power flows do not regulate them).
- `vvo`: volt/VAr optimisation over the case's OLTC taps (`taps`, by transformer name; default every OLTC) and
  switched capacitor banks (`capacitors`: name, bus, Mvar, starting state). The objective is `losses` or `demand`
  (conservation voltage reduction: the real power the ZIP loads draw at their solved voltages), with every bus voltage
  in its band. A tap moves on the grid MinTap + k x StepPu; an OLTC with an impedance correction table takes its R and
  X from the table at each trial ratio. Every OLTC of the case must be among `taps`, and a transformer flow control is
  refused, since the trial power flows do not regulate them. Discrete coordinate descent in a fixed order (taps, then
  capacitors), then the same neighbour confirmation over every control (refused above 8 controls unless `confirm` is
  false). The result is a local discrete optimum.
- `phase-balance`: moves single-phase loads between phases on a `dist3ph-mesh` case file. The proxy objective is the
  largest deviation of a phase's real power from the mean. When there are at most 3^12 = 531 441 assignments (the
  product of each movable load's allowed phase count), all are searched (the lexicographically smallest best assignment
  wins); above that, greedy, largest load first, a load to the lightest phase and a generating load to the heaviest. One `dist3ph-mesh` solve before
  and one after report the head-branch neutral current and the voltage unbalance factor at the head node.

In `capplace` and `vvo`, trials are compared on the total voltage violation first, then the objective, so an
infeasible start first moves into the band. The bus limits are the case's own where it has them, else `vminPu` and
`vmaxPu` (default 0.95 and 1.05). The `common` section sets `vminPu`, `vmaxPu`, `tolerancePu` (Newton, default 1e-10),
`maxIterations` (30), `confirm` (true) and `maxRounds` (100). It is not read by `phase-balance`, whose section is
`phaseBalance`: `movable` (load names, each with optional `phases`), `phases` (default a, b, c), `headBranch` and
`neutralWire` (default n).

Prints each step, the result and how the search ended. Exit 0 done. Exit 1 for a file or setting it refuses. Exit 3 when
the result is outside the voltage band, the search stopped at its round cap, or a `phase-balance` solve did not converge.

| Flag | Default | Meaning |
|---|---|---|
| `--mode` | none (required) | `capplace`, `vvo` or `phase-balance`. |
| `--params` | none | The `gridsim-distopt/1` settings file. |
| `--out` | none | Write the result table (`record,element,field,value`) as CSV, or as JSON for a `.json` file. |

### long-term

`gridsim long-term <case> <long-term.json> [--out <long-term.csv>]`

Long-term (quasi-steady-state) simulation. The fast dynamics are replaced by their equilibrium, a power flow with one
system frequency, and the slow elements are stepped with a fixed step: AGC, tap changer timers and recovering loads. See
[technical ch. 43](<../technical/0043 Long-term dynamics.md>) for the equations.

The input is strict JSON with `schema` `gridsim-long-term/1`:

- `stepSeconds` and `durationSeconds` (a whole number of steps).
- `units`: the governed units, each `generator` (index in the case's in-service generators, or a unique name), `droopPu`
  and optional `mbaseMva`.
- Optional `loadDampingMwPerHz` (D, default 0).
- Optional `agc`: `kiPerSecond`, `units` (each `generator` and `participation`), optional `biasMwPerHz` (default the
  natural response beta) and `rateLimitMwPerSecond`.
- Optional `oltcTimers`: each `transformer` (the name of a tap changer of the case), `firstDelaySeconds` and
  `nextDelaySeconds`.
- Optional `loads`: each `bus`, `tpSeconds`, `tqSeconds`, `alphaS`, `alphaT`, `betaS`, `betaT` (the transient exponents
  0, 1 or 2).
- Optional `events`: `branchTrip` (`branch`), `generatorTrip` (`generator`), `loadStep` (`bus`, `pMw`, `qMvar`) and
  `setPoint` (`generator`, `pMw`), each with `atSeconds` after 0. An event lands on the first step at or after its time.
- Optional `equilibriumToleranceMw` (1e-6), `powerFlowTolerancePu` (1e-10) and `maxEquilibriumPasses` (30).

Prints the step, the natural response, the end state, the lowest voltage, the tap moves and each event as applied, and
writes one CSV row per step: `t_s`, `df_hz`, `min_v_pu`, `min_v_bus`, the AGC set point change of each AGC unit, the tap
of each timed tap changer, P and Q of each recovering load, the equilibrium passes, the residual and the events applied at
that step. `--out` names the CSV (default `long-term.csv` beside the input); an existing file is never overwritten. The
CSV is the result file: the verb is not on the shared `--json`/`--csv` writer.

Exit 0 when the run reaches its end. Exit 1 for a case or input it cannot read or refuses. Exit 2 for usage. Exit 3 when
an equilibrium power flow fails and the run stops ("loss of short-term equilibrium" with the time), or when GridSim's
own frequency loop does not settle within `maxEquilibriumPasses` ("numerical failure" with the time: raise the cap or
the tolerance).

### qv-modal

`gridsim qv-modal <case> [--modes 5] [--out <file.json>]`

QV modal analysis (Gao, Morison and Kundur, IEEE Trans. PWRS 7(4), 1992; Kundur section 14.3) at the case's Newton power
flow (the shared engine rule, 100 iterations, reactive limits on, as `pf`). The power-flow Jacobian is reduced to the
reactive-power and voltage relation at the PQ buses, J_R = J_QV - J_Qtheta J_Ptheta^-1 J_PV (a PV bus held at a reactive
limit counts as PQ), and its eigenvalues are found with the general real eigen solver that `modal-full` uses. Each mode
prints its eigenvalue and the ten buses with the largest participation factor (P_k = left_k x right_k, scaled so a mode's
factors sum to 1). An eigenvalue above 0 is voltage stable, below 0 unstable, and near 0 close to the voltage stability
limit; the bus with the largest participation is where that mode is weakest. See
[technical ch. 29](<../technical/0029 OPF, CPF, taps, distribution factors and loss allocation.md>).

| Flag | Default | Meaning |
|---|---|---|
| `--modes` | 5 | How many modes to report, smallest absolute eigenvalue first; a whole number of 1 or more. |
| `--out` | none | Write the result as JSON (`gridsim-qv-modal/1`: the PQ buses and, per mode, the eigenvalue and every bus's participation); an existing file is never overwritten. |

The JSON from `--out` is the result file: the verb is not on the shared `--json`/`--csv` writer. Scope: the standard
power-flow Jacobian only (reactive limits as solved, no dynamic models, no branch or generator participation). A case
with HVDC links, FACTS devices, static var compensators or induction machines is refused: the reduced Jacobian is built
from the network alone, so it would describe a different system.

J_R is not symmetric, so a stable state can have complex pairs of eigenvalues; each mode is judged on its real part, and
a complex mode prints as `a + jb`. Exit 0 when every reported eigenvalue has a positive real part. Exit 3 when the power
flow does not converge or a reported eigenvalue has a real part of 0 or below. Exit 1 for a case that cannot be loaded
or analysed (a singular J_Ptheta, a defective J_R, or a case with the coupled devices above). Exit 2 for usage.

### reduce

`gridsim reduce <case> --keep <ids|file> --method ward|xward|rei [--rei-groups gen|gen,load] [--out <case.m|case.json>]`

`gridsim reduce <case> --keep <ids|file> --method sc [--out <case.json>]`

`gridsim reduce <case> --method dynamic --groups <file> | --coherency <r> --xdp <pu> [--out <case.m|case.json>]`

A static network equivalent (`NetworkReduction`, `GridSim.Core.PowerFlow.Equivalents`): the buses named by `--keep` stay
as they are and every other bus (the external network) is replaced by elements on the boundary, the kept buses with a
branch to an external bus. `ward` is the Ward equivalent: equivalent branches, shunts and constant-power injections.
`xward` is the extended Ward: Ward plus one voltage-controlled source per boundary bus standing in for the external PV
buses. `rei` is the REI equivalent: the external generators (and, with `--rei-groups gen,load`, the external loads as a
second group) are moved to one fictitious bus per group through a zero-power-balance network, then Ward. The base case is
solved with reactive limits off and a 1e-10 pu tolerance; the reduced case is solved from a flat start with the same
options. See [technical ch. 40](<../technical/0040 Network equivalents.md>) for the equations and the error bounds.

Prints the boundary, each element (pu on the system base), the notes (what was removed and not carried) and the base-case
check, the largest |V_reduced - V_full| at the boundary. `--out` writes the reduced case as MATPOWER (`.m`) or a v2 JSON
case (`.json`) through the existing writers, never over an existing file; each writer keeps its own rules (a v2 JSON
case needs every bus base kV above 0, so a case such as case14 with base kV 0 is written as `.m`). Refused, naming the element: a slack bus outside
the kept set, an external network not connected to the boundary, a phase shifter in the external network (the equivalent
is not reciprocal), a ZIP load, HVDC, FACTS, SVC, induction machine, OLTC, flow control or boundary touching the external
network, and a kept set that is empty or holds every bus. Exit 0 done. Exit 1 for a case it cannot load or a reduction it
refuses. Exit 3 when the base case or the reduced case does not converge.

Result files: `--json <file.json>` or `--csv <file.csv>` write the result to a file through the shared writer ([Result files](#result-files)): the summary (case, method, bus counts, the base-case check), the tables `elements` (kind, name, buses, real and imaginary part in pu), `boundary` and `notes`. `--out` is unchanged: it still names the reduced case, not a shared-writer file.

| Flag | Default | Meaning |
|---|---|---|
| `--keep` | required | The bus ids to keep: a comma list (`1,2,3,4,5`) or a file of ids separated by commas, spaces or new lines. Not used by `dynamic`. |
| `--method` | required | `ward`, `xward` (extended Ward), `rei`, `sc` (short-circuit equivalent) or `dynamic` (coherency-based dynamic equivalent). |
| `--rei-groups` | `gen` | REI only: `gen` (one group, the external generators) or `gen,load` (a second group for the external loads). |
| `--out` | none | Write the reduced case to this new `.m` or `.json` file. With `dynamic`, also `<name>.dynamics.json` beside it. |
| `--groups` | none | `dynamic` only: a file of coherent groups, one per line, each a list of bus ids whose in-service units form the group (`#` starts a comment line). |
| `--coherency` | none | `dynamic` only: find this many groups by slow coherency instead of reading `--groups`. |
| `--xdp` | required with `dynamic` | The classical transient reactance X'd given to every machine, pu on its own base. |

**`--method sc`**, the short-circuit equivalent (`ShortCircuitEquivalent`). No power flow is run. The positive-, negative-
and zero-sequence networks of the IEC 60909 studies are Kron-reduced onto the boundary and written as equivalent branches
(with zero-sequence data, or delta/delta windings where the zero sequence is not coupled) and one fictitious source bus
per boundary bus (a branch and a synchronous machine carrying the positive-sequence row sum, with a grounded-wye/delta
winding carrying the zero-sequence one). The fault studies on the reduced case then give the full case's Ik'' at every
kept bus, for three-phase, line-to-line and earth faults. It prints the elements, the external network's Thevenin
impedances Z1, Z2 and Z0 at each boundary bus, the notes and a check, the IEC 60909 three-phase Ik'' at each boundary bus
with a base kV on the full and the reduced case (`NOT RUN` when no boundary bus has a base kV). Refused, naming the
element: a converter-interfaced unit or a machine with X2 different from X''d in the external network, a zero-sequence
mutual coupling across the boundary, and the external elements the Ward equivalent refuses except ZIP loads. Loads are
not represented and the reduced case's power flow is not the base case. Apply the IEC 60909 correction factors to the
full case before reducing it, never to the reduced case. A boundary bus that the external network gives no path in a
sequence has an infinite Thevenin impedance there; the other buses keep theirs. `--out` writes a JSON v2 case that
carries every sequence field (the equivalent branches' windings, machine zero-sequence impedances and kept mutual
couplings), reads it back and prints `reload check`, the largest relative difference of the three-phase, line-to-line,
single- and double-line-to-earth Ik'' at the kept buses between the equivalent and the written case (0 expected: the
numbers are written exactly). A `.m` output is refused (exit 2): MATPOWER holds no X''d and no sequence data, so the
written case's fault currents would differ. Result files add the table `thevenin` and the summary value
`max_reload_ikpp_rel`.

**`--method dynamic`**, the coherency-based dynamic equivalent (`CoherencyGrouping`, `InertialAggregation`). Every bus is
kept. Each group of coherent machines, from `--groups` or found by slow coherency with `--coherency`, becomes one
classical (GENCLS) machine: S_eq the summed rating, H_eq and D_eq rating-weighted, X'd_eq the parallel combination. A
group on several buses moves to a new bus at the rating-weighted voltage, its terminal buses absorbed through ideal
transformers, so the base case solves the reduced case. Every machine takes the X'd of `--xdp` and no damping (tagged
Synthetic in the dynamics file). It prints the groups, the notes and the base-case check, the largest
|V_reduced - V_full| over the kept buses (V_eq at an equivalent bus). Exciters, governors and stabilisers are not
aggregated. Result files: the summary and the tables `groups` and `notes`. Exit codes as above.

### transmission-security

`gridsim transmission-security <study.json> [--out <result.json>]`

Preventive branch N-1 study with AC verification (RC3). A DC candidate dispatch is formed by `scopf`'s optimiser
(`DcScopf`) against an explicit list of branch outages, then the intact network and each listed outage are solved
independently from that same candidate with the full AC Newton solver and checked against absolute limits: bus voltage
against the study band, branch MVA at both ends against RATE_A intact and the requested post-fault rating after an
outage (B or C, falling back to A where the branch has none), and each unit's realised P and Q against its own range.
The DC optimiser's status and objective are kept separate from the AC verdict. It is a DC-propose, AC-verify study, not
a full AC SCOPF and not a claim of global optimality.

The study file is strict JSON, schema version 1: `schemaVersion` (1), `case` (`path`, relative to the study file, or
`builtIn`, plus `sha256`), `outages` (ordered; each a zero-based branch `index` with its expected `from` and `to` bus
IDs, so parallel circuits stay distinct), `voltage` (`minPu`, `maxPu`) and `postFaultRating` (`A`, `B` or `C`).
Optional: `dc.maxRounds`, and `costs.perMwh` (one linear cost per in-service unit, in model order). Unknown or
duplicated fields, non-finite numbers, inverted limits, a duplicated outage or one whose endpoints do not match are
refused before any solve. `case.sha256` is the SHA-256 of the case file's exact bytes; for a native case directory or
v2 manifest it is the SHA-256 of the UTF-8 manifest lines (path, TAB, lowercase file SHA-256, LF) in consumption order;
for a built-in it binds the build identity and the model. A mismatch is refused and prints the resolved identity.

Controls are declared in the result: taps fixed, reactive limits enforced (a PV-to-PQ switch is a valid solution, not a
failure), and the single slack supplies the AC losses, so a slack unit whose realised P exceeds its range is reported.
Unit P and Q are recovered from converged bus injections and are compared with a declared tolerance of the Newton bus
mismatch times base MVA; margins are reported unrounded. Where several units share a bus their individual realised
outputs cannot be observed and the row is incomplete rather than assumed. An outage that islands the network is left
out of the DC candidate and reported as `ISLANDED`.

Each listed outage has exactly one result row. Row dispositions: `SECURE`, `VIOLATIONxN`, `ISLANDED`, `NO SOLUTION`,
`INCOMPLETE` or `UNEVALUATED`, each with its reason. The study `status` is `SECURE` only when the intact network and
every listed outage are evaluated and meet every limit; otherwise `NOT SECURE`, with `outcome` `Violations`,
`Incomplete` (missing evidence takes precedence over violations) or `CandidateFailure` (no usable DC candidate, with
the optimiser's message).

The result JSON (`schemaVersion`, `provenance` with study and case SHA-256, inputs and engine identity,
`effectiveOptions`, `inputGenerators`, `result`) is printed to stdout and, with `--out`, written to a new file via a
temporary file and a final move. An existing `--out` path is refused, and a failed run writes no result, so an earlier
result can never be mistaken for the current one.

Exit 0 `SECURE`. Exit 1 for input it cannot accept: a malformed or unknown-field study, a case identity mismatch, an
invalid outage, an unreadable case or an existing output path. Exit 2 for an invalid invocation. Exit 3 `NOT SECURE`.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |

### transfer-capability

`gridsim transfer-capability <study.json> [--out <result.json>]`

AC-verified directional transfer capability (RC3). Positive transfer raises the declared source units' generation and
lowers the declared sink units' generation, each by the transfer times its normalised weight (weights summed in
declared order); nothing else in the case changes, and the single slack balances the losses. Every trial is built
from the same loaded case and verified exactly as `transmission-security` verifies a state: the intact network and
each listed branch outage are solved with the full AC Newton solver and checked against the absolute voltage band,
branch MVA at both ends (RATE_A intact, the requested post-fault rating after an outage) and each unit's realised P
and Q range. A trial is feasible (every state evaluated and within limits), a violation (every state evaluated, at
least one limit breached), or unknown (a state that islands, does not converge or lacks evidence). Unknown is never
reported as a physical limit. A participant whose schedule would leave its own P range is a violation of that
transfer (`ScheduledGeneratorP`); it is never clipped.

A separate verb rather than a mode of `transmission-security`, so that command's strict schema, flags and exit codes
stay exactly as published; the case identity, strict parsing and result-file rules are the same.

Search: zero transfer is evaluated first. If it already violates a limit the result is `NOT SECURE` /
`NoAdmissibleStart` (no headroom is reported, which is different from a solved zero-margin case); if it lacks evidence,
`INCOMPLETE` / `ReferenceIncomplete`. Otherwise the search walks up at k x `stepMw` (the last point clamped to
`maxMw`) until the first violation, the first unknown, the cap or the trial budget; evaluates up to `probes` further
coarse points above a first violation only to detect a non-monotone feasible region; then bisects between the last
feasible and first violating trial until the bracket is no wider than `toleranceMw`. Changes in solved control state
(a PV-to-PQ switch) or in the first binding limit across the sampled trials, a feasible probe above a violation, an
unknown midpoint and an unfinished bisection each qualify the result. Both bracket endpoints are then re-solved from
the case and must reproduce their evidence bit for bit. The reported capability is the lower feasible MW; the first
observed limit (state, element, criterion, observed value, limit and signed margin) comes from the re-solved upper
endpoint, with any simultaneous violations in stable order (intact first, then outages in study order). Intervals
between trials are not independently proven feasible, and the result is never a global maximum.

The study file is strict JSON, schema version 1: `schemaVersion` (1), `case` (`path` or `builtIn`, plus `sha256`, as
for `transmission-security`), `sources` and `sinks` (each row a zero-based in-service unit `index`, its expected `bus`
ID and a positive `weight`; a unit may appear once), `outages` (as for `transmission-security`), `voltage` (`minPu`,
`maxPu`), `postFaultRating` (`A`, `B` or `C`) and `search` (`maxMw`, `stepMw`, `toleranceMw` no larger than `stepMw`,
`maxTrials` from 2 to 10000 including the zero-transfer trial, optional `probes` 0 to 10, default 1). Optional
`interfaces`: each a `name`, `branches` (`index`, `from`, `to`, `sign` +1 or -1; the interface MW is the sum of sign
times the active power leaving each branch's From terminal) and an optional `limitMw` that caps that signed sum in
every state. Unknown or duplicated fields, non-finite numbers and mismatched identities are refused before any solve.

The result JSON (`schemaVersion`, `study`, `provenance`, `effectiveOptions`, `inputGenerators`, `result`, `comparator`)
is printed to stdout and, with `--out`, written to a new file via a temporary file and a final move; an existing path
is refused. `result` holds `status`, `outcome` (`Limited`, `AtLeastMax`, `UnknownAbove`, `BudgetExhausted`,
`NoAdmissibleStart`, `ReferenceIncomplete` or `Unsupported`), `qualifications`, `lowerMw`, `upperMw`, `firstLimit`,
the full `reference`, `lower` and `upper` states, every trial in execution order (with the realised slack balancing
and each interface's MW and change from zero transfer), the participation shares and exact trial accounting.
`comparator` is the record shape for a paid-tool comparison; GridSim fills only its own side and marks it
`UNVERIFIED`.

Exit 0 `VERIFIED` (a limit bracketed to the tolerance, or the cap reached, with no qualification). Exit 1 for input it
cannot accept (as `transmission-security`). Exit 2 for an invalid invocation. Exit 3 `QUALIFIED`, `INCOMPLETE` or
`NOT SECURE`.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |

### cgmes-study

`gridsim cgmes-study <study.json> [--out <result.json>] [--export <model.xml>] [--archive <bundle.zip>]`

CGMES as a normal study job (RC3). One CGMES profile bundle runs the whole job in a fixed order, each stage over the
existing CIM code that owns it: bundle inspection; import (`CimImporter`), with any switch actions applied before the
topology processor builds the buses; the connected electrical model and its identity maps (`CimGridModelBridge`);
control application and the AC solve (`CimControlledPowerFlow` around the Newton engine, reactive limits enforced);
absolute intact limits; optionally the `transmission-security` preventive N-1 verification over the imported model;
and export of the realised model (`CgmesWriter`) with a read-back that is compared with what was solved. A later stage
never bypasses a failed earlier one.

The study file is strict JSON, schema version 1. Required: `schemaVersion` (1), `bundle` (`sha256` and `profiles`) and
`voltage` (`minPu`, `maxPu`, applied to every solved bus). Each profile names its `profile` type (`EQ`, `TP`, `SSH`,
`SV`, `EQ_BD`, `TP_BD`, `DY`, `DL` or `GL`), its declared `version`, its `path` (relative to the study file) and the
`sha256` of its exact bytes. `bundle.sha256` is the SHA-256 of the UTF-8 manifest lines (profile, TAB, version, TAB,
path, TAB, lowercase file SHA-256, LF) in manifest order; a mismatch is refused and prints the resolved identity.
`--archive <bundle.zip>` (RC4 R4-11b) reads the profiles from a ZIP instead of from files: each profile `path` then names
an entry of the archive (relative, `/`-separated; one level of nested zip is `outer.zip/inner.xml`), the archive is read in sorted
entry order, and an entry path with `..`, a rooted path, a repeated entry or two documents of one profile is refused. The
digests and the bundle identity are checked exactly as for files; without the flag a `.zip` profile path is still refused.
Optional: `import` (`topology` `auto`, `topologicalNodes` or `switches`;
`tapPositions` and `shuntSections` `ssh` or `sv`; `startingVoltages`, `apparentPowerRatings` and
`requireSshSetPoints`, all true by default), `system` (`baseMva`, default 100; `frequencyHz`, default 50: CGMES
carries neither, and the resolved values and their source are recorded), `controls` (`apply`, default true; `shunts`
`asWritten`, `discrete`, `continuous` or `held`; `tolerancePu`; `maxSolves`), `switchActions` (each a switch `mrid`
and `open` true or false; needs `topology` `switches`) and `security` (`outages`, each a branch `mrid`;
`postFaultRating` `A`, `B` or `C`; optional `dc.maxRounds`).

The bundle is refused, naming the profile and the record, before anything is imported when: a digest does not match;
a required profile is missing (EQ and SSH always, TP unless `topology` is `switches`, SV when an option reads it); a
profile type is listed twice; a header's `md:Model.profile` contradicts the declared type; a `md:Model.DependentOn`
names no model of the bundle; a reference names an object no profile defines; or two profiles give one property of one
object different values. A missing load or machine set-point is refused by the importer (`requireSshSetPoints`); an
absent optional profile is only listed. No conformity to an external CGMES profile specification is checked or
claimed.

The result records each profile (declared and header identity, whether it is consumed by the import, what is preserved
and what the solve uses), the importer and bridge notes, the identity of every solved bus, branch (with the source of
its rating) and generator, the switch actions with the equipment terminals that changed bus, every control represented
(local and remote voltage control, static var compensators, regulating shunts) with where it ended, and the automatic
controls the study does not represent (automatic tap control: taps are held at the selected step). Ratings are the
permanent (PATL) apparent-power limits; temporary limits are not imported, so a post-fault `B` or `C` falls back to
`A`. The exchange output is one RDF/XML document holding the EQ, TP, SSH and SV content, written from the realised
model (each control folded in at its realised value, the solved voltages as the starting state); it is read back and
compared for equipment identity, topology, ratings bit for bit, model data (reported with exact differences) and the
controlled AC output (voltage within 1e-6 pu, angle within 1e-5 degrees, terminal P and Q within 1e-4 MW or MVAr, the
same limit disposition). The source mRIDs are not carried into the exported document; the result maps each one to its
exported identity. The deferred items are listed in the result.

The study `status` is `SECURE` only when the intact controlled state (and every listed outage, with `security`) is
evaluated within its limits and the read-back is equivalent; otherwise `NOT SECURE`, with `outcome` `Violations` or
`Incomplete` (missing evidence takes precedence). The intact state's disposition is `SECURE`, `VIOLATIONxN`,
`NO SOLUTION` or `INCOMPLETE`, as in `transmission-security`. The result JSON (`schemaVersion`, `provenance`,
`effectiveOptions`, `result`) is printed to stdout and, with `--out`, written to a new file; `--export` writes the
exchange document to a new file. Both go through a temporary file and a final move, and an existing path is refused.

Exit 0 `SECURE`. Exit 1 for input it cannot accept: a malformed or unknown-field study, a bundle identity or digest
mismatch, a refused bundle, an unreadable profile or an existing output path. Exit 2 for an invalid invocation. Exit 3
`NOT SECURE`.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |
| `--export` | none | Write the exchange CGMES document to this new file. |

### planning-study

`gridsim planning-study <manifest.json> --out <dir> [--parallel 1] [--max-jobs N]`

Versioned planning snapshots and repeatable studies (RC3). One immutable master case, dated changes authored as
ordered operations, and named snapshot instants are expanded deterministically into resolved cases; each snapshot is
then run through each study in the manifest, and every job writes its own result into its own folder under one ordered
result manifest.

The manifest is strict JSON, `manifestVersion` `"gridsim-planning/1"`. Unknown or duplicated fields, non-finite
numbers and wrong types are refused. Its separate objects are:

- `master`: `case` (`path` to a native JSON case directory or v2 case manifest, relative to the manifest file, and
  `sha256`, the SHA-256 of the UTF-8 consumed-file lines (path, TAB, lowercase file SHA-256, LF) in load order, the
  same value `transmission-security` takes for that case) and `interpretation` (`loader` `"native-json"`, `dispatch`
  `false`). Loader balancing is never run: only declared operations change the model. The master identity
  (`masterSha256`) binds the bundle digest to the interpretation.
- `projects`: each an `id` and ordered `operations`. An operation has an `id`, a `target`, a `field`, an
  `expectedPrior` and a replacement `value`, an `effectiveFrom` and an optional `effectiveTo`. Targets: a branch
  (`element` `branch`, zero-based `index`, expected `from` and `to` bus IDs), a generator (`generator`, zero-based
  `index` in the whole fleet including out-of-service units, expected `bus`) or a load (`load`, `bus`: the bus's
  aggregate constant-power demand); an optional `name` is an identity check. Fields: `inService` (branch, true or
  false), `rateAMva` (normal rating, MVA), `rateBMva` and `rateCMva` (emergency ratings, MVA; 0 means not given),
  `pgMw` and `qgMvar` (unit setpoints, within the unit's range), `pdMw` and `qdMvar` (bus demand).
- `projectOrder`: the explicit order of projects. Two projects active at one instant that change the same field are
  refused unless both are listed here; the earlier listed applies first.
- `snapshots`: each an `id` and an `instant`.
- `studies`: each an `id`, a `mode` and its `parameters`, which are the fields of that verb's own study file without
  `schemaVersion` and `case`, bound to the master. Mode `transmission-security` takes the `transmission-security`
  fields (`outages`, `voltage`, `postFaultRating`, optional `dc`, `costs`); mode `transfer` the `transfer-capability`
  fields (`sources`, `sinks`, `outages`, `voltage`, `postFaultRating`, `search`, optional `interfaces`); mode
  `corrective-n-1-1` the `corrective-sequence` fields (`voltage`, `ratings`, `policy`, `sequences`). Study unit
  indices follow those verbs (the in-service unit list); operation generator indices count the whole fleet.

Times are ISO 8601 with an explicit offset (`Z` or `+hh:mm`) and up to seven fractional digits; a local time without
an offset is refused as ambiguous. Every instant is normalised to UTC. An operation is active at an instant when
`effectiveFrom <= instant < effectiveTo` (from inclusive, to exclusive; no `effectiveTo` is open-ended), so a change
ends by expiring and the master value returns.

At each snapshot the active operations are applied to a copy of the master, projects in `projectOrder` then unlisted
projects in declaration order, operations in authoring order. Each `expectedPrior` must equal exactly the value it
replaces; a change shadowed by an earlier one is refused naming both operations. Any refusal stops the run before a
job starts. Each resolved case then has its topology (one connected network) and ratings checked; a case that fails is
`INVALID CASE` and is never given to a study. A requested outage of a branch that is out of service in the snapshot
(a planned outage) keeps an explicit `NOT APPLICABLE` row and is not studied; a corrective sequence needing such a
branch is likewise `NOT APPLICABLE`. `transmission-security` replaces unit P with its own DC candidate, so an authored
`pgMw` changes the resolved case and its identity but not that study's candidate; `transfer` and `corrective-n-1-1`
start from the scheduled unit P, so it moves their reference point.

Outputs, in `--out` (a new folder, an empty one, or an earlier planning run folder, recognised by its marker file):
`jobs/<NNN>-<snapshot>--<study>/result.json` per job (the audit of every operation with its original, prior and
effective value, the validation, the study parameters and the study's unaltered result, and the case identities),
`summary.txt`, `run-manifest.json` (every job in order with its state, verdict, result path and SHA-256, the master
input files and digests, `caseSha256` content identity and `derivationSha256` per snapshot) and `evidence.json`
(elapsed times and reuse decisions only; the other files are byte-identical between runs). The run manifest is written
last and the previous one is removed first, so an interrupted run never leaves an older manifest looking current. On a
rerun a job's earlier result is reused only when its completion record matches this build, the job's request and input
fingerprints, and the file's recorded length and SHA-256; otherwise the earlier files are deleted before the job runs,
so a failed job is never represented by an older result.

Exit 0 when every job completed with its study's passing status (`SECURE`; `VERIFIED` for `transfer`). Exit 3 when
the run completed with any other verdict (`NOT SECURE`, `QUALIFIED`, `INCOMPLETE`, `UNVERIFIED`), an `INVALID CASE` or
a `FAILED` job (the engine refused the request, with its message), or when jobs were left unattempted (`--max-jobs` or Ctrl+C: run state `CANCELLED`, with each completed job
kept). Exit 1 for input it cannot accept (malformed manifest, digest mismatch, unresolvable target, conflict, an
unusable output folder) or when a result could not be written. Exit 2 for an invalid invocation.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | required | Planning run folder. |
| `--parallel` | 1 | Jobs run at once (1-64). Results and their order do not depend on it. |
| `--max-jobs` | all | Attempt at most this many jobs, in declared order; the rest are recorded as not attempted. |

### study-case

`gridsim study-case <study-case.json> --out <dir> [--parallel 1] [--max-jobs N]`

Result files: `--json <file.json>` or `--csv <file.csv>` write the result to a file through the shared writer ([Result files](#result-files)); the document is built from the same result this verb prints, and `--json` is the form `gridsim.run` and `gridsim rpc` use. `--out` is unchanged: it still names its output folder, not a shared-writer file.

Study cases (RC4 R4-01): one immutable master case, dated variations, named operation scenarios, grouping objects and
studies, expanded deterministically and run as one ordered batch. It builds on the `planning-study` machinery
(`gridsim-planning/1`, unchanged): variations are planning projects with an expansion-stage label, scenarios are expanded
to explicit per-element operations in the same vocabulary, and dated application, expiry, conflict refusal and the
topology and rating checks are those of `planning-study`. Groups are held in a sidecar document bound to the case, never
in the case itself.

The study case is strict JSON, `manifestVersion` `"gridsim-studycase/1"`. Unknown or duplicated fields, non-finite numbers
and wrong types are refused. Its members are:

- `master`: `case` (`path` and `sha256`) and `interpretation`, exactly as for `planning-study`.
- `groups` (optional): `path` (relative to the study case) and `sha256`, the SHA-256 of the groups file's bytes.
- `instants`: each an `id` and an `instant` (ISO 8601 with an explicit offset, as for `planning-study`).
- `variations`: each an `id`, an optional `expansionStage` label and ordered `operations`, written as the operations of a
  `planning-study` project (`target`, `field`, `expectedPrior`, `value`, `effectiveFrom`, optional `effectiveTo`).
  `variationOrder` lists variations whose operations may change one field at one instant; two unlisted variations that
  both do are refused as a conflict, naming both operations.
- `scenarios`: each an `id`, optional `instants` (the instant IDs it runs at; default all) and ordered `operations`. A
  scenario operation has an `id`, an optional `group` (default: every element of the kind in the case), a `field`, an
  `action` (`scale` or `set`) and a `value`. Fields: `load` (demand, MW and MVAr together; scale only), `generation` (the
  active-power set point; the same as `pgMw`), `pdMw`, `qdMvar`, `pgMw`, `qgMvar`, and `inService` (availability of the
  group's branches; set only, `true` or `false`). A scale multiplies the value the element holds once the variations
  active at the instant are applied; the new value is computed once in IEEE double, groups in file order and elements in
  model order. A scenario always applies after the variations. The name `base` is reserved for a study that names no
  scenario.
- `studies`: each an `id`, an optional `scenario`, a `mode` and `parameters`. The modes of `planning-study`
  (`transmission-security`, `transfer`, `corrective-n-1-1`) take the same parameters. Mode `group-summary` takes
  optional `groups` (group IDs; default all) and `solve` (default false) and reports per group the load, the scheduled
  generation and their balance; with `solve` the AC power flow is run and the solved net injection, the tie flow out of
  the group, the internal loss and the shunt loss are added (`netInjection = tieFlow + internalLoss + shuntLoss`).

The groups document is `gridsim-groups/1`: `case.sha256` (the master bundle digest) and `groups`, each an `id`, a `kind`
(`area`, `zone`, `owner`, `feeder` or `free`), and any of `buses` (bus IDs), `generators` (`index` into the whole fleet
and the expected `bus`) and `branches` (`index` and the expected `from` and `to`; parallel circuits stay distinct). A
feeder is ordered and has a `head` (`bus`, `branch` or `generator`, one of its members). Area, zone and owner are
exclusive: an element in two groups of one of those kinds is refused. An unresolved or repeated member, an empty group or
a case digest that does not match is refused before any study runs.

Jobs run scenario by scenario (`base` first when used), then by instant, then by study, each in declared order. For each
(scenario, instant) the case is resolved in three steps: the variations alone give the case the scenario scales, the
scenario is expanded to explicit element operations, and variations plus scenario are applied through the
`planning-study` expansion. Any refusal stops the run before a job starts and writes nothing.

Outputs, in `--out` (a new folder, an empty one, or an earlier study-case run folder, recognised by its marker file):
`jobs/<NNN>-<scenario>.<instant>--<study>/result.json` per job (as for `planning-study`, the audit of every operation
including the scenario's), `summary.txt`, `derivation.json` (every operation of every resolved case: variations with
their expansion stage, and each scenario's element-level changes with before and after values, with the dispositions
`Applied`, `NotYetEffective` and `Expired`), `run-manifest.json` (`gridsim-studycase-result/1`: every job in order with
its state, verdict, result path and SHA-256, the master and groups identities, and the case and derivation digests of each
resolved case) and `evidence.json` (elapsed times and reuse decisions only; the other files are byte-identical between
runs). Reuse of an earlier job result follows the `planning-study` rules.

Exit 0 when every job completed with its mode's passing status (`SECURE`, `VERIFIED`, or `SUMMARISED` for
`group-summary`). Exit 3 when the run completed with any other verdict, an `INVALID CASE` or a `FAILED` job, or left jobs
unattempted. Exit 1 for input it cannot accept (malformed document, digest mismatch, unresolvable target or group member,
conflict, an unusable output folder). Exit 2 for an invalid invocation.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | required | Study-case run folder. |
| `--parallel` | 1 | Jobs run at once (1-64). Results and their order do not depend on it. |
| `--max-jobs` | all | Attempt at most this many jobs, in declared order; the rest are recorded as not attempted. |

### case-diff

`gridsim case-diff <a> <b> [--out <file.json>]`

Result files: `--json <file.json>` or `--csv <file.csv>` write the result to a file through the shared writer ([Result files](#result-files)); the document is built from the same result this verb prints, and `--json` is the form `gridsim.run` and `gridsim rpc` use. `--out` is unchanged: it still names its diff JSON, not a shared-writer file.

Read-only comparison of the authored JSON files of two native cases (RC4 R4-01): a v1 case folder, or a v2 case
manifest with its network and asset files. No case is loaded as a model or solved. Records inside an array are matched
by identity (`id`, then `name`, then `from` and `to`, then `bus`, with repeats numbered so parallel circuits stay
distinct), so a permuted record order is reported as `REORDERED` (note that branch and unit indices follow file order),
not as a change. Each changed field is listed with its path, old value, new value and unit (read from the case schema);
`ADDED` and `REMOVED` list records and members, `FILEADDED` and `FILEREMOVED` whole files. Numbers compare as IEEE
doubles, so `1` and `1.0` are the same. A case compared with itself reports no differences; a pair that differs only in
formatting reports none and says so.

Exit 0 when no authored value differs, 3 when they do, 1 for a case it cannot read or an existing `--out` path, 2 for an
invalid invocation.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the differences as JSON (`gridsim-case-diff/1`) to this new file. |

### result-diff

`gridsim result-diff <a.json> <b.json> [--out <file.json>] [--max-differences 100]`

Result files: `--json <file.json>` or `--csv <file.csv>` write the result to a file through the shared writer ([Result files](#result-files)); the document is built from the same result this verb prints, and `--json` is the form `gridsim.run` and `gridsim rpc` use. `--out` is unchanged: it still names its diff JSON, not a shared-writer file.

Read-only comparison of two result documents (RC4 R4-01), member by member in document order. A number is the same only
when it is the identical IEEE double; a differing number is reported with its distance in units in the last place (ULP)
and its absolute difference, and the first difference in document order is marked `FIRST`. Two runs of one request
compare equal and byte-identical.

Exit 0 when equal, 3 when they differ, 1 for a document it cannot read or an existing `--out` path, 2 for an invalid
invocation.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the differences as JSON (`gridsim-result-diff/1`) to this new file. |
| `--max-differences` | 100 | List at most this many differences (all are counted); 0 lists every one. |

### report

`gridsim report <template.json> <result.json> --out <file> [--format md|html|csv]`
`gridsim report --template-for <result.json> --out <template.json>`

Result files: `--json <file.json>` or `--csv <file.csv>` write the result to a file through the shared writer ([Result files](#result-files)); the document is built from the same result this verb prints, and `--json` is the form `gridsim.run` and `gridsim rpc` use. `--out` is unchanged: it still names the report file, not a shared-writer file.

Report generator of the workbench (RC4 R4-10). It renders a result document through a `gridsim-report-template/1` template
and writes the report, so a report made in the Avalonia or web workbench can be made again headless, byte for byte. The
template is JSON: a `title`, an optional `resultSha256`, and `sections` of blocks that are bound to the result document by
JSON pointers (RFC 6901): `text` (literal), `value` (a label and a `path`), `table` (a `path` to an array of objects, with
optional `columns` of `key`, `title`, `unit` and `decimals`) and `plot` (`x` and `y` pointers to number arrays; `kind` is
`line`, `spectrum` or `eigen`; `logX` and `logY` select log axes). Any result JSON can be reported, including the result of
another verb and the workbench's own `gridsim-workbench-result/1` document. The reader is strict: an unknown or duplicate
member is refused, naming the place.

Order of operations: load the template; verify the result's SHA-256 against `resultSha256` when the template names one (a
mismatch writes nothing); resolve the bindings in template order; build the tables and plots; render; write once. A binding
that finds nothing is a visible gap (`n/a` in a cell, a "not drawn" or "no data" note for a block), never a zero. The report
records the SHA-256 of the template and of the result and the generator (`gridsim-report/1`); it holds no timestamp, so the
same inputs give the same bytes. Markdown lists the tables and gives each plot's SHA-256; HTML is self-contained (inline CSS
and SVG, no script); CSV carries every table. The output format follows the `--out` extension (`.md`, `.html`, `.csv`)
unless `--format` is given. `--template-for` writes the default template for a result: a summary, then a section per table
and per plot. It takes a workbench result or a verb's `--json` result (`gridsim-cli-result/1`). For a verb's result the
summary lists the verb, the exit code and every summary value, and each table section binds the table's rows, with the
columns of its first row.

Exit 0 when written, 1 for an unusable template or result, a result that does not match the template's hash, or an existing
`--out` path, 2 for an invalid invocation.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | required | The new file to write; an existing path is refused. |
| `--format` | from the `--out` extension | `md`, `html` or `csv`. |
| `--template-for` | none | Write the default template for this result document instead of a report. |

### restoration-study

`gridsim restoration-study <study.json> [--out <result.json>]`

Restoration switching-sequence study (RC3 P8). It searches for the order in which to close a set of candidate branches
so that every intermediate network is admissible and the unserved energy during restoration is least. It is a separate
study from `restore`, which checks one given order and is unchanged.

Every candidate branch starts open, whatever its stored state; every other branch keeps its state in the case. An
admissible sequence closes every candidate once, respects every precedence constraint, and leaves the initial state and
the state after each action admissible: the live (slack-connected) network solves and passes the same absolute AC
verifier as `transmission-security` (every live bus inside the voltage band, branch MVA at both ends within RATE_A,
each unit's realised P and Q within its own range). A bus that is not live carries no load; units at a live bus run at
their stated setpoint and the slack balances. A unit at its reactive limit is a valid solution reported by its margin,
not a failure (unlike `restore`). The objective is the unserved energy in MWh: for each action, the net demand (positive
Pd) of the buses still dead before it, times the action's minutes, divided by 60.

The search is a deterministic finite enumeration: depth-first over the candidates in input order, each switching state
(a set of closed candidates) solved once, every sequence through an inadmissible state pruned, and a branch-and-bound
cut using the exact optimum of the same problem without the AC constraints (a lower bound found from connectivity
alone). Outcomes are distinct: `OPTIMAL` (the enumeration finished and the reported sequence is a proven optimum;
lower and upper bounds equal), `FEASIBLE` (an admissible, fully verified sequence, but optimality is not proven
because the node budget ran out or a state could not be verified and the bound does not exclude it; both bounds are
reported), `INFEASIBLE` (proven: no admissible sequence exists, for example the initial state already fails) and
`UNRESOLVED` (no admissible sequence found and none proven impossible, or the target network, with every candidate
closed, is outside the verifier's envelope: one connected network, one slack, positive RATE_A on every live branch, no
coupled devices, flow controls, boundaries or ZIP demand). A state with no solution or incomplete evidence is never
admissible and is counted as unverified. Of several optimal orders the first in input order is reported.

The study file is strict JSON, schema version 1: `schemaVersion` (1), `case` (`path` or `builtIn`, plus `sha256`, with
the same identity rules as `transmission-security`), `voltage` (`minPu`, `maxPu`) and `actions` (1 to 12, each an `id`,
a `branch` with its zero-based `index` and expected `from` and `to` bus IDs, and `minutes`, the positive time the
action takes). Optional: `precedence` (each `before` and `after`, two action ids) and `search.maxNodes` (default
100000, the most prefix extensions the search may expand). Unknown or duplicated fields, non-finite numbers, a
repeated action or branch, mismatched endpoints and cyclic precedence are refused before any solve.

The result JSON (`schemaVersion`, `study`, `provenance`, `effectiveOptions`, `stepOrder`, `inputBranches`, `result`)
is printed to stdout and, with `--out`, written to a new file via a temporary file and a final move; an existing `--out`
path is refused. `result` holds the status and outcome with its reason, total, initial and final unserved load (MW),
`objectiveMwh`, `lowerBoundMwh`, `upperBoundMwh` and `relaxedLowerBoundMwh`, the initial state's verifier evidence,
the sequence (per step: action, branch, start and end minute, unserved MW before and after, step energy, energised
buses and the full verifier evidence with original branch and unit indices), every evaluated state with its
disposition, and the search counts (nodes, states, pruned, unverified).

Exit 0 `OPTIMAL`. Exit 3 `FEASIBLE`, `INFEASIBLE` or `UNRESOLVED` (the result is still printed). Exit 1 for input it
cannot accept (malformed or unknown-field study, case identity mismatch, invalid action or precedence, unreadable case,
existing output path). Exit 2 for an invalid invocation.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |

### reliability-study

`gridsim reliability-study <study.json> [--out <result.json>]`

Analytical radial-feeder reliability study (RC3 P8). It reads no case and runs no power flow: the feeder topology,
failure rates, repair and switching times, customers, average load and dependency assumptions are all stated in the
study file before any index is computed.

Each component is a series element fed from the downstream end of its `parent` (null: the source, behind the feeder
breaker). A fault on a component is cleared by the nearest `fused` component on its path to the source (itself first),
otherwise by the feeder breaker; every load point downstream of that device is interrupted. A load point at or
downstream of the failed component waits for its repair (`repairHours`). Any other interrupted load point waits only
for `switchingHours` when a `switchable` component lies on the failed component's path below the point where it joins
the load point's path (that disconnector isolates the fault), otherwise for the repair. Per load point j: failure rate
lambda_j = sum lambda_k (interruptions/yr), annual outage time U_j = sum lambda_k t_kj (h/yr), r_j = U_j / lambda_j
(h/interruption), energy not supplied ENS_j = L_j U_j (MWh/yr). System indices over the customers N_j: SAIFI
(interruptions/(customer.yr)), SAIDI (h/(customer.yr)), CAIDI = SAIDI / SAIFI (h/interruption), ASAI = 1 - SAIDI /
hoursPerYear, ENS (MWh/yr) and AENS (MWh/(customer.yr)). Each load point lists every failure mode affecting it.

The study file is strict JSON, schema version 1: `schemaVersion` (1), `assumptions` (`failureDependency`, which must
be `IndependentFirstOrder`: independent failures, overlapping outages neglected; `alternateSupply`, which must be
`None`: no back-feed; `hoursPerYear`, stated explicitly), optional `switchingHours`, `components` (each `id`, `parent`
(required, may be null), `fused`, `switchable`, and `failuresPerYear` and `repairHours`, which may be omitted or null)
and `loadPoints` (each `id`, `component`, `customers`, `averageLoadMw`). Unknown or duplicated fields, non-finite or
negative numbers, an unknown parent or component, a cycle, an unsupported assumption and no customers are refused.

Missing rate data is reported, never assumed: a component without `failuresPerYear` or `repairHours`, or a missing
`switchingHours` where isolation applies, is listed with the load points whose indices it withholds; those values and
the affected system indices are null and the status is `INCOMPLETE`. The result also lists the scope (radial, sustained
interruptions only, perfect selective protection, no alternate supply or transfer-capacity check, constant average
load) and the largest lambda x r product, as a check on the first-order approximation.

The result JSON (`schemaVersion`, `study`, `provenance`, `effectiveOptions`, `stepOrder`, `result`) is printed to stdout
and, with `--out`, written to a new file via a temporary file and a final move; an existing `--out` path is refused.

Exit 0 `COMPLETE`. Exit 3 `INCOMPLETE` (missing rate data; the result is still printed). Exit 1 for input it cannot
accept (malformed, unknown-field or invalid study, unsupported assumption, unreadable file, existing output path).
Exit 2 for an invalid invocation.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |

### probabilistic

`gridsim probabilistic <study.json> [--out <result.json>] [--threads N]`

Result files: `--json <file.json>` or `--csv <file.csv>` write the result to a file through the shared writer ([Result files](#result-files)); the document is built from the same result this verb prints, and `--json` is the form `gridsim.run` and `gridsim rpc` use. `--out` is unchanged: it still names its result JSON, not a shared-writer file.

Probabilistic load flow and generation adequacy (RC4 R4-09), with the sampling method a request field: plain Monte Carlo
(`monte-carlo`), quasi-Monte Carlo (`qmc`, unscrambled Sobol; `qmc-shifted`, with an independent random digital shift per
replicate) or analytical `convolution`. One study can list several methods to compare them. Every method rests on the one
seeded generator of RC4 (`gridsim-rng/1`: counter based, so a draw depends only on seed, domain and index): a result is
bit-repeatable from its study file, and one thread gives the same bits as sixteen. The seed is mandatory; there is no
default seed.

The study file is strict JSON, schema version 1: `schemaVersion` (1), `study` (`load-flow` or `adequacy`), `seed`
(unsigned integer), `methods` (a list of rows, each with `method` and `samples`; `samples` is required in EVERY row, including
`convolution`, which ignores its value, so give any positive integer there; `qmc-shifted` also takes `replicates`, default 8,
at least 2; `convolution` optionally takes `cellsPerSd`, default 100), and optionally `domain` (the stream label, default the study kind).
Unknown or duplicated fields and non-finite numbers are refused before any solve. `samples` is per replicate; a
`qmc-shifted` run draws `samples x replicates` points. Use a power of two for `qmc`.

`load-flow` fields: `case` (`path`, relative to the study file, or `builtIn`, plus `sha256`, which is required for both and
bound exactly as for `transmission-security`: for a built-in name it is the identity hash the loader reports as `case_sha256`
(`gridsim rpc` `load`), which depends on the GridSim build, so re-read it after upgrading), `evaluator` (`dc` or `ac`), `inputs` (each `id`, `target` `bus-load` or `generator-pg`, `bus` ID,
`unit` for a generator: its zero-based position among the in-service generators at that bus, optional `scale`: the law is
a multiplier of the case value, and `distribution`), optional `correlation` (rows of input positions `a`, `b` and `rho`,
a Gaussian copula applied through a Cholesky factor in fixed row order), `quantiles` (default 0.05, 0.5, 0.95),
`loadingLimitPercent` (default 100), `voltage` (`minPu`, `maxPu`, default 0.95 and 1.05), `monitorBranches` (zero-based
branch indices), `monitorBuses` (bus IDs, AC only), `qFollowsP` (AC: a load keeps its power factor, default true) and `stepMw`
(an explicit convolution lattice step). Distributions: `normal` (`mean`, `sd`), `lognormal` (`mu`, `sigma` of the
logarithm), `weibull` (`scale`, `shape`), `beta` (`alpha`, `beta`, optional `lo`, `hi`), `discrete` (`atoms` of `value`,
`probability`), `two-state` (`capacity`, `availability`) and `empirical` (`values`).

`adequacy` fields: `units` (each `id` with `capacityMw` and `availability`, or `states` of `capacityMw`, `probability`),
`load` (`hourlyMw`, or `constantMw` with `hours`) and optionally `stepMw`, the capacity lattice step for convolution
(default: the greatest common divisor of whole-MW capacities; a capacity off the lattice makes convolution OUT OF SCOPE).
Results: `lole` in HOURS per period (the sum over hours of the loss-of-load probability, loss when available capacity is
below the load), `lolp` (the average hourly probability), `eens` in MWh per period. Static adequacy only: no
transmission and no chronology.

Result per method: mean, standard deviation, requested quantiles (type 7 estimator on the sorted sample) and the exceedance
probability per monitored branch (loading above the limit, with RateA) and bus voltage (below the band, above the band).
Sampling error is stated per method: Monte Carlo the standard error (sd over the square root of N, and the binomial error
of a probability); `qmc-shifted` the spread of the replicate estimates over the square root of R; `qmc` "deterministic
point set, no error bar"; `convolution` no sampling error, with the lattice step per branch (`latticeStepMw`). The
convolution of continuous inputs inflates the variance by about step squared over 12 per term (a stated discretisation
tolerance); discrete inputs on the lattice are exact. A sample whose AC power flow does not converge is kept as a row in
`failedSamples` with its status, and every statistic (mean, deviation, quantiles and the probabilities) is taken over
the converged samples, whose count is given, so it is conditional on convergence. A run with any failed sample is
`INCOMPLETE`, and its reason gives the failed count; with every sample converged it is `COMPLETE`. Convolution applies only where the quantity is a sum of independent terms (DC
flows, capacity): an AC evaluator or correlated inputs return `OUT OF SCOPE` with the reason, and nothing is approximated.
Quasi-Monte Carlo accepts at most 64 dimensions (inputs or units); above that use Monte Carlo or convolution.

The result JSON (`schemaVersion`, `study`, `provenance` with study and case SHA-256, seed, domain and engine, `status`,
`methods`) records, per method, the seed, domain, method, sample count, replicates, generator version, direction-number
table id, a digest of the drawn samples and a digest of the result values. It is printed to stdout and, with `--out`, written
to a new file via a temporary file and a final move; an existing `--out` path is refused.

Exit 0 every method `COMPLETE`. Exit 3 a method `OUT OF SCOPE` or `INCOMPLETE` (the result is still printed). Exit 1 for
input it cannot accept (a missing seed, a malformed or unknown-field study, a case identity mismatch, a non-finite or
out-of-range parameter, a correlation matrix that is not positive definite, an unreadable file, an existing output path).
Exit 2 for an invalid invocation. PowerFactory's probabilistic modules were not compared (COMPARATOR-UNVERIFIED).

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |
| `--threads` | runtime default | Most worker threads; the result is bit-identical for any value. |

### reliability-network

`gridsim reliability-network <study.json> [--out <result.json>] [--threads N]`

Result files: `--json <file.json>` or `--csv <file.csv>` write the result to a file through the shared writer ([Result files](#result-files)); the document is built from the same result this verb prints, and `--json` is the form `gridsim.run` and `gridsim rpc` use. `--out` is unchanged: it still names its result JSON, not a shared-writer file.

Reliability and restoration joined on the real network (RC4 R4-13). The RC3 `reliability-study` computes radial-feeder
indices from a stated list of components and reads no case; this verb reads a case, attaches failure data to the R4-01
groups, isolates each failure with the R4-07 placed devices and declared switches, restores the de-energised loads by
re-closing the source or by closing a normally-open tie, and verifies every restored network with the incumbent
`restoration-study` enumerator (AC power flow, voltage band, branch RATE_A). Both RC3 verbs are unchanged.

The study file is strict JSON, schema version 1: `schemaVersion` (1), `study` (`reliability-network`), `method`
(`enumeration` or `monte-carlo`), `case` (`path`, relative to the study file, or `builtIn`, plus `sha256`, bound exactly as
for `transmission-security`), `groups` (`path` of a `gridsim-groups/1` document, bound to the case by branch identity),
optional `devices` (`path` of a `gridsim-devices/1` document: every device clears faults on its branch and below; a breaker
or recloser can also isolate), `failureData` (per group: `group`, and any of `failuresPerKmYear`, `failuresPerItemYear`,
`repairHours`, `switchingHours`; the rate of a branch is the per-km rate times its circuit length plus the per-item rate, and
a per-km rate on a branch with no stated length is reported missing), optional `overrides` (a `branch` of `index`, `from`,
`to` with `failuresPerYear`, `repairHours`, `switchingHours`), optional `switches` and `ties` (each `id` and `branch`; a tie
is declared normally open and the study opens it whatever the case stores, a switch must be in service), `loadPoints` (each `id`, `bus`, `customers` and optionally
`averageLoadMw`, default the bus Pd), `switchingHours` (the default switching time), `hoursPerYear` (for ASAI, stated
explicitly), `acVerification` (`enabled` with `minPu` and `maxPu`, or `enabled` false: restoration is then decided by
connectivity and every event says the AC state was NOT RUN) and optionally `order` (only 1; 2 is reported OUT OF SCOPE).
`method` `monte-carlo` also needs `monteCarlo` (`seed`, mandatory, `years`, optional `repairDistribution` `exponential`
(default, mean = the repair time) or `deterministic`, and `domain`). Unknown or duplicated fields and non-finite numbers
are refused before any physics.

Order: bind groups and failure data, then for each component in branch order: outage event, clearing by the nearest placed
device (otherwise the feeder breaker), isolation by the nearest switching device, the unsupplied loads, restoration
alternatives, AC verification, accumulation in a fixed order, indices. The in-service network must be radial from the
single slack bus (a loop is OUT OF SCOPE). The intact network must itself pass the AC verifier, or the result is OUT OF
SCOPE with the verifier's verdict. Per load point the result gives the failure frequency, the annual outage duration and
the average outage duration, with one exposure per component that interrupts it (`Repair`, `Switching` for source
re-closure, `BackFeed` for a tie). System SAIFI, SAIDI, CAIDI, ASAI, ENS and AENS use the RC3 expressions and summation
order, so on a network with no ties the figures equal `reliability-study` on the same data bit for bit. Each outage event
lists its clearing device, the isolating switch, the interrupted buses, each interrupted load's restoration (a load that
cannot be restored before the repair is reported `Repair` with the reason), the restoration path with the AC verdict after
each step, and the final AC state. Monte Carlo samples the same first-order event model (Poisson failures per year, repair
times per failure) on the seeded streams of RC4 (`gridsim-rng/1`), reports a standard error for each system index and the
seed, domains and result digest, and is bit-identical for any `--threads`.

First order only: one component fails at a time and overlapping outages are neglected; second-order enumeration,
chronological adequacy with storage, protection failure modes and weather-dependent rates are deferred. A breaker or
recloser or declared switch makes its branch a section isolated at both ends. PowerFactory's reliability module was not
compared (COMPARATOR-UNVERIFIED).

The result JSON (`schemaVersion`, `format` `gridsim-reliability-network/1`, `provenance` with study, groups, devices and
case SHA-256 and engine, `effectiveOptions`, `stepOrder`, `result`) is printed to stdout and, with `--out`, written to a
new file via a temporary file and a final move; an existing `--out` path is refused.

Exit 0 `COMPLETE`. Exit 3 `INCOMPLETE` (rate or time data missing; the computable values are still printed) or `OUT OF
SCOPE`. Exit 1 for input it cannot accept (a malformed or unknown-field study, a case identity mismatch, a group or device
that does not bind, a missing seed, an unreadable file, an existing output path). Exit 2 for an invalid invocation.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |
| `--threads` | runtime default | Most worker threads for the Monte Carlo years; the result is bit-identical for any value. |

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

### devices-check

`gridsim devices-check <case> --devices <set.json> [--curves <dir>]`

Validate a placed-device file and bind it to a case (RC4 R4-07, Core `GridSim.Core.Protection.Devices`). `<set.json>` is a
strict `gridsim-devices/1` document: `manifestVersion`, then `devices`, each with `id`, `type` (`relay`, `fuse`, `recloser`
or `breaker`), optional `function` (ANSI number: `50`, `51`, `50N`, `51N`, `67`, `21`, `87`; default `51` for a relay or
recloser), `branch` (`index`, `from`, `to`, `end` = `from` or `to`: the stable branch identity, so a parallel circuit stays
distinct), `ct` (`primaryA`, `secondaryA`), optional `vt`, `shots` (each `curve`, `pickupA` in CT secondary amps,
`timeDial`, `definiteTimeS`), `instantaneous` (`pickupA`, `delayS`), `recloseIntervalsS` (one fewer than the shots),
`fuse` (`meltCurve`, `clearCurve`, `ratingA`) and `rating` (a breaker's `ratedVoltageKv`, `makingPeakKa`, `breakingSymKa`,
`shortTimeWithstandKa`, `shortTimeSeconds`). A curve is a built-in equation family (`iec-si`, `iec-vi`, `iec-ei`,
`iec-lti`: IEC 60255-151; `ieee-mi`, `ieee-vi`, `ieee-ei`: IEEE C37.112; `dt`) or a tabulated curve from the curve data
files. Curve data lives in `data/protection-curves/*.json` (`gridsim-protection-curve/1`); every file must carry a `source`
(kind `standard`, `manufacturer` or `synthetic`, title, publisher, url, locator, retrieved, optional sha256 of the source
document) and a file without one is refused. Only publicly documented vendor data is accepted, as data, never as code;
proprietary settings-transfer formats are not supported. Distance, directional, earth-fault and differential functions are
accepted and reported as not evaluated by the three-phase study (`sc-trace`, `fault-line`, `protection-audit`);
`protection-eval` and `protection-grade` evaluate them. Three optional members carry their settings, each accepted only with
its own function: `distance` with `21` (`characteristic` `mho` or `quad`; `loops` `phase`, `earth` or `both`, default
`both`; optional `k0` as `magnitude` and `angleDeg`, default from the line's own Z0 and Z1; `zones`, one to three, each
`reachOhms`, `angleDeg`, `timeS` and for a quad `resistiveReachOhms` and optional `leftReachOhms`; `directionalLowDeg` and
`directionalHighDeg`, default -30 and 120, for a quad), `directional` with `67` (`polarisation` `quadrature`,
`zero-sequence` or `negative-sequence`; `characteristicAngleDeg`; `direction` `forward` or `reverse`, default `forward`;
`minPolarisingV` in pu, default 0.01) and `differential` with `87` (`is1A`, `k1`, `is2A`, `k2`; optional `remoteCt` as
`primaryA` and `secondaryA`, default the device's own CT). Reaches are CT/VT secondary ohms, currents CT secondary amps. A
`21` or `67` with these settings needs a `vt`; zone reaches and times must not fall from one zone to the next; `k2` must
not be below `k1`; an `87` with a `differential` setting on a transformer is refused (use `transformerDifferential`), and
so is a `21` with settings unless its `distance` sets `onTransformer: true`. An `87` may instead carry one of two
optional members (RC5 WP18):
- `transformerDifferential` (87T, on the device's own two-winding transformer): `vectorGroup` (`YNd1`, `Yd11`, `Dyn1`,
  `Dyn11`, `YNyn0`, `Yy0` or `Dd0`), `ratedMva`, and `is1`, `k1`, `is2`, `k2` in pu of the rating. Optional `hvCt` and
  `lvCt`, each `{ "branch": { index, from, to, end } }`, put that side's CT on a branch from the transformer's bus, at its
  other end; that bus must carry no load, shunt, unit, HVDC converter, static var compensator or FACTS device. The HV side is the bus with the higher base kV. The case's
  phase shift must be the group's, and its windings, where given, must match; a case without both windings is accepted
  for `YNyn0` only. Three-winding legs are refused;
- `busDifferential` (87B): `zone` (bus IDs), `cts` (each `branch` with its `end` at the zone, and `ct` as `primaryA` and
  `secondaryA`), `is1A` and `k1` in secondary amps of the CT with the largest primary, optional `is2A` and `k2` (together,
  for a dual slope), `bias` `sum` (default) or `max`, and optional `checkZone` (bus IDs, a superset of the zone). A `21` placed on a two-winding transformer that
way needs a phase shift that is a whole multiple of 30 degrees; its earth loops need no phase shift and an explicit `k0`.
The `distance` object takes these optional members (RC5 WP17):
- a zone may carry `reverse: true` (it looks behind the relay; reverse zones follow the forward zones, at most one). A
  reverse zone sends the DCB block and also trips on its own `timeS`, as a time-delayed back-up;
- `scheme`: `type` (`DUTT`, `PUTT`, `POTT` or `DCB`), `remote` (the 21 at the other end of the same branch), `channelS`
  and `coordinationS` (default 0). PUTT, POTT and DCB need a zone 2, POTT a remote zone 2, and DCB a remote reverse zone;
- `powerSwing`: `outerR`, `innerR`, `outerX`, `innerX` (ohm sec, outer larger than inner), `deltaTS`, `blockZones` (zone
  numbers) and `unblockS` (default 2);
- `loadEncroachment`: `fwdAngleDeg` and `revAngleDeg` (0 to 90) and `minLoadOhms`, for phase loops;
- `sotf`: `windowS` and `mode` (`Z2` or `nondirectional`). `windowS` (above 0) is how long the logic stays armed after
  the line closes. The static fault study is at the moment of closing, so every energised fault lies inside the window
  and the value is checked but does not change the result;
- `mutual`: `branch` (a parallel line between the same two buses) and optional `k0m` (`magnitude` and `angleDeg`, taken
  from the case's zero-sequence mutual data when absent), for earth loops.

Exit 0 valid; 1 the case, device file or curve data cannot be loaded or
is invalid (a branch whose ends differ from the case, an unknown curve, a duplicate ID, a recloser with the wrong number of
intervals); 2 usage.

| Flag | Default | Meaning |
|---|---|---|
| `--devices` | required | The `gridsim-devices/1` file. |
| `--curves` | the installed `data/protection-curves` | A directory of curve data files to use instead. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).

### fault-line

`gridsim fault-line <case> --branch <i> --devices <set.json> [--fractions 0,0.25,0.5,0.75,1] [--objective max|min] [--cti 0.4] [--curves <dir>]`

Three-phase fault along a line (RC4 R4-07). For each fraction `d` of branch `i` from its From end, a bus is inserted by
splitting the branch (R, X, B, R0, X0 and the length in the ratio d : 1 - d; both segments keep the thermal rating), the
fault current comes from the IEC 60909 engine (read only), and every placed device's current and operating time are
traced; at `d` = 0 or 1 the fault is at the end bus and nothing is inserted. A transformer is refused for an interior
point (not a line). The line per fraction gives Ik'', the first and second device to operate and the worst pair margin
against `--cti`. Exit 0 every fraction coordinated; 3 a pair is not coordinated; 1 invalid input; 2 usage (a branch outside
the case, a fraction outside 0 to 1, a bad objective).

| Flag | Default | Meaning |
|---|---|---|
| `--branch` | required | Zero-based branch index to fault along. |
| `--devices` | required | The `gridsim-devices/1` file. |
| `--fractions` | `0,0.25,0.5,0.75,1` | Comma list of positions in [0, 1], evaluated in ascending order. |
| `--objective` | `max` | `max` (c = 1.10 HV) or `min` (c = 1.00 HV, the protection-sensitivity case). |
| `--cti` | 0.40 | Required coordination time interval (s). |
| `--curves` | installed data | Curve data directory. |

Result files: `--json`, `--csv` or `--out` write the table `fault_line` ([Result files](#result-files)).

### sc-trace

`gridsim sc-trace <case> --devices <set.json> (--bus <id> | --branch <i> --fraction <d>) [--objective max|min] [--cti 0.4] [--curves <dir>]`

Short-circuit trace for one fault (RC4 R4-07): the current each placed device measures through its CT, its operating time,
its confidence (`Grounded` for a tabulated manufacturer curve, `Generic` for an equation on a user setting, `NotCheckable`
for a function not evaluated here: a 21, 67, 50N, 51N or 87 is evaluated by [`protection-eval`](#protection-eval)), the sequence of operation (earliest first, ties by device order), the primary and backup
device, and the grading margin of each device against the nearest upstream device on its supply path. A device's direction
is decided by the bridge rule on the fault network; a device in a mesh is marked `meshed` and takes no part in pairing. A
fuse is timed at its total clear time as a primary and its minimum melt as a backup. A device on an out-of-service branch is
reported not evaluated. Exit 0 traced; 3 the fault could not be applied (no source in its island, or a fault along a
transformer); 1 invalid input; 2 usage.

| Flag | Default | Meaning |
|---|---|---|
| `--devices` | required | The `gridsim-devices/1` file. |
| `--bus` | - | Fault bus ID. Exactly one of `--bus` or `--branch`. |
| `--branch` | - | Branch index for a fault along a line; needs `--fraction`. |
| `--fraction` | 0.5 | Position along the branch in [0, 1]. |
| `--objective` | `max` | `max` or `min`. |
| `--cti` | 0.40 | Required coordination time interval (s). |
| `--curves` | installed data | Curve data directory. |

Result files: `--json`, `--csv` or `--out` write the tables `devices` and `pairs` ([Result files](#result-files)).

### protection-audit

`gridsim protection-audit <study.json> [--curves <dir>]`

Coordination audit of a placed-device set (RC4 R4-07). `<study.json>` is a strict `gridsim-protection-audit/1` document:
`manifestVersion`, `case` (a path relative to the study file, or a case name), `devices` (path), optional `curves`
(directory), optional `coordinationTimeIntervalS` (default 0.40), optional `faults` (`buses`: a list of bus IDs; `alongLine`:
a list of `{branch, fractions}`; default every bus in model order). Order: validate and bind; for each fault, maximum-fault
trace then minimum-fault trace; pair margins aggregated (worst margin and the fault that sets it); each device's
sensitivity at its smallest in-zone fault (current over pickup; a device that would not operate there is a failure); the
instantaneous reach (the current at a fault at the far end of the device's own branch over its instantaneous pickup: at or
above 1 it overreaches); each breaker's `CheckBreaker` margins against the maximum three-phase duty at its bus; and every
check that could not be made, with its reason (missing evidence is never a pass). The verdict is `Coordinated`,
`NotCoordinated` or `Incomplete` (no pair could be graded). Exit 0 coordinated and every device operating at its minimum
fault with no instantaneous overreach and every evaluated breaker adequate; 3 otherwise (an `Incomplete` audit and a breaker
printed NOT ADEQUATE included); 1 invalid input; 2 usage.

| Flag | Default | Meaning |
|---|---|---|
| `--curves` | study's `curves`, else installed data | Curve data directory; overrides the study file's. |

Result files: `--json`, `--csv` or `--out` write the summary (`format` = `gridsim-protection-audit/1`, verdict, counts) and the
tables `pairs`, `device_sensitivity`, `breakers` and `not_evaluated` ([Result files](#result-files)).

### protection-eval

`gridsim protection-eval <case> --devices <set.json> (--bus <id> | --branch <i> --fraction <d>) [--type 3ph|slg|ll|llg] [--xf 0] [--energised true|false] [--curves <dir>]`

`gridsim protection-eval <case> --devices <set.json> --swing <trace.csv|result.json> [--curves <dir>]`

Every placed device measured on the phasors of one fault (RC5 WP4, Core `GridSim.Core.Protection.Placed`). The prefault
power flow is solved on the fault network (the split copy for a fault along a line) and must converge; the fault is the
three-phase study for `3ph` and the sequence-network study for `slg`, `ll` and `llg`, through Zf = j`--xf`. Each relay
uses its own bus voltage and the branch current into the branch at its end. 50/51 operate on the largest phase current
and 50N/51N on |3I0|, both on their curves (IEC 60255-151, IEEE C37.112). A `67` with quadrature polarisation is a phase
element (Ia with Vbc, Ib with Vca, Ic with Vab), with zero-sequence polarisation a 67N (3I0 against -3V0), with
negative-sequence polarisation a 67Q (I2 against -V2); it is forward when cos(arg Iop - arg Vpol - RCA) > 0 and operates
only when the direction matches its setting and its overcurrent element operates; below `minPolarisingV` it reports no
polarising voltage. A `21` measures the phase loops (Vx - Vy) / (Ix - Iy) and the earth loops Vp / (Ip + k0 3I0), the
earth loops released only when |3I0| is above 10 % of the largest phase current, against mho circles (operate when
Re(Z conj(Zr - Z)) >= 0) or quadrilaterals (reactance line, two resistive blinders parallel to the zone angle, and the
directional limits); the fastest zone hit over all loops wins. A loop whose voltage is at or below 1e-6 of its nominal
(1 pu for an earth loop, sqrt(3) pu for a phase loop) has collapsed, a bolted fault at the relay point where V / I is
rounding noise: its direction is taken from the prefault (memory) voltage, forward when Vmem / I lies within 90 degrees
of the zone-1 angle, and the loop is then in every zone of that direction (reported as Z = 0, with "close-in fault, loop
voltage collapsed" in the reason). The generic `21` of `protection-check` and `fault`, which has no memory voltage,
reports such an end as not measured. An `87` is a dual-slope biased line differential on the two
ends of its own branch (Id = |I_near + I_far|, Ib = (|I_near| + |I_far|) / 2; operate above k1 Ib + Is1 up to Is2 and
above k2 Ib - (k2 - k1) Is2 + Is1 beyond), instantaneous, with no charging-current compensation. The result names, per
device, the element, the measured quantity (A or ohm secondary), the loop or phase, the zone, the direction and the time.
Converter-interfaced units keep their prefault current in an unbalanced study and are named in a note.

The optional `distance` members (see [`devices-check`](#devices-check)) work as follows:
- A reverse zone tests -Z against its characteristic. It trips on its own `timeS` (a time-delayed back-up for faults
  behind the relay), so it counts in the time and in `protection-grade` like any other zone.
- With `mutual`, the earth loop current adds k0m 3I0m, where I0m is the parallel line's zero-sequence current at the
  relay's bus.
- With `loadEncroachment`, a phase loop with |Z| >= `minLoadOhms` within `fwdAngleDeg` of 0 degrees or `revAngleDeg` of
  180 degrees is blocked.
- With `sotf` and `--energised true`, the relays on the faulted branch (the line just closed) trip at 0 s: mode `Z2` when
  zone 1 or 2 picks up, and mode `nondirectional` when a phase current exceeds the CT primary. Relays on other lines keep
  their zone timers. `--energised true` needs `--branch`; with `--bus` it is a usage error, since no line was closed.
- With a `scheme`, each end's aided trip time follows: DUTT t_Z1,r + channel; PUTT max(p_Z2, t_Z1,r + channel); POTT
  max(p_Z2, p_Z2,r + channel); DCB p_Z2 + coordination unless a block is received. The remote end sends the block when
  its reverse zone picks up and its zone 1 or zone 2 does not (the forward pickup stops the carrier). The carrier stop
  and the POTT permissive come from the remote end's faulted-phase loops only. The pickup p is 0 s when
  the element picks up. The end trips at the smaller of its zone time and this time, and a line `scheme ...` is printed
  for it.

`--swing <trace>` takes no fault. It reads a bus-voltage trace in one of two forms:
- a `dynamics-study` result file (JSON), read as it is: the one simulated case's `timeSeconds`, and each pair of
  `busVoltage N` channels (unit `pu` for the magnitude, `deg` for the angle) as bus N. A result with more than one
  simulated case is refused, so keep one fixed-time case in the study;
- a CSV: lines starting `#` are skipped; the header is `t_s` then `vm_<bus>` (pu) and `va_<bus>` (degrees).

The times must increase. For each 21 with `powerSwing` it forms Z(t) = V / I at the relay, with the current from the
branch's pi stamp on its two bus voltages. The stamp uses the case's fixed tap, so a tap changer that moves during the
RMS run is not followed. It then runs the
two-blinder detector: a pass that stays between the outer and inner rectangles (|R| <= R, |X| <= X) for `deltaTS` is a
swing. The `blockZones` are then blocked until Z leaves the outer rectangle or `unblockS` has passed. One line is printed
per pass. A relay whose two buses are not both in the trace is listed as not evaluated.

Not modelled: fault resistance in the unbalanced study (Zf is a reactance), channel failure, the current-reversal guard,
echo and weak-infeed logic, out-of-step tripping, swing detection during an unbalanced fault, CVT transients, 87T and
87B. Exit 0 evaluated; 3 the fault could not be applied; 1 invalid input (including an unreadable trace); 2 usage
(including `--swing` with a fault flag, and `--energised true` with `--bus`).

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary (case, fault, type, xf, fault bus) and the table `devices` (every row printed, with the
measured values in round-trip form). When a device carries a scheme, the table adds the columns `scheme`, `aided` and
`aided_trip_s`. With `--swing`, the summary is the case, `mode` = `swing` and the sample count, and the table is `swing`
(`device`, `pass`, `outer_entry_s`, `inner_entry_s`, `swing`, `block_from_s`, `block_to_s`, `release`, `block_zones`,
`reason`).

| Flag | Default | Meaning |
|---|---|---|
| `--devices` | required | The `gridsim-devices/1` file. |
| `--bus` | - | Fault bus ID. Exactly one of `--bus` or `--branch`. |
| `--branch` | - | Branch index for a fault along a line; with `--fraction`. |
| `--fraction` | 0.5 | Position along the branch in [0, 1]. |
| `--type` | `3ph` | `3ph`, `slg` (phase a to earth), `ll` (b to c) or `llg` (b and c to earth). |
| `--xf` | 0 | Fault reactance, pu (Zf = j xf). |
| `--energised` | `false` | `true` marks the faulted branch as just closed onto the fault, for the `sotf` setting of the 21s on it. Needs `--branch`. |
| `--swing` | - | A bus-voltage trace for power-swing blocking (CSV or a `dynamics-study` result file); takes no fault flags. |
| `--curves` | installed data | Curve data directory. |

### protection-grade

`gridsim protection-grade <case> --devices <set.json> [--types 3ph,slg] [--fractions 0.05,...,0.95] [--cti 0.4] [--curves <dir>]`

Coordination checks over placed devices (RC5 WP4). Faults are every bus of the case in model order, for each listed type.
G1, grading margin: at each fault, for every device with the fault beyond it, the nearest upstream device on its supply
path is its backup (the `sc-trace` rule); when both operate, the backup must be at least `--cti` slower (distance zone
times, curve times for 50/51, 50N/51N and 67, a fuse's total clear as primary and minimum melt as backup, 0 for 87); a
pair where either does not operate is listed as not graded. G2, zone-1 underreach: a bolted three-phase fault at the far
bus of a 21's branch must not be in zone 1. G3, zone-2 cover: the same fault must be in zone 2 or faster. G4, zone-1
coverage: the largest swept fraction of the protected line, from the relay's end, at which zone 1 operates (none is a
failure). G5, earth-fault sensitivity: for a 50N/51N, the smallest |3I0| over the `slg` faults beyond it over its pickup,
passing when the device operates there. G6, zone reaches load, only for a 21 with `loadEncroachment`: the phase-loop
impedance at the prefault load point must lie in no forward zone, or else in the blinder's load region. G7, zone
completeness, only for an 87B: every branch leaving the zone (or the check zone) has a CT at its zone end; a missing CT
fails, naming the branch, and the element is not evaluated. G8, stability, for an 87T or 87B: it does not operate for any
fault of the study outside its zone. A check that could not be made is listed as not evaluated with its reason, never
as a pass. The verdict is `Coordinated`, `NotCoordinated` or `Incomplete` (nothing could be graded). Exit 0 coordinated; 3
not coordinated or incomplete; 1 invalid input; 2 usage.

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary (verdict, required margin, counts) and the tables `pairs`, `checks` and `not_evaluated`.

| Flag | Default | Meaning |
|---|---|---|
| `--devices` | required | The `gridsim-devices/1` file. |
| `--types` | `3ph,slg` | Comma list of fault types: `3ph`, `slg`, `ll`, `llg`. |
| `--fractions` | 0.05 to 0.95 in steps of 0.05 | The G4 sweep, numbers inside (0, 1). |
| `--cti` | 0.40 | Required coordination time interval (s). |
| `--curves` | installed data | Curve data directory. |

### fault

`gridsim fault <case|case.m|case-dir> --bus <id> [--zf 0.0] [--type 3ph|slg|ll|dlg]`

Balanced three-phase fault at `--bus` through fault reactance `--zf` (pu, Zf = j`--zf`, printed `Zf=j...`; D-DOC-166:
`dist3ph fault --zf` is a resistance instead): fault current (pu, and kA when the bus has a
base kV), Thevenin impedance, faulted-bus voltage, the converter-interfaced units' current-limited infeed, and the
relays that operate on the faulted state. Generator subtransient reactance is synthesised at 0.20 pu where a unit
carries none. The prefault solve must converge: a non-converged prefault state is refused with `fault: the pre-fault
power flow did not converge (...)` and exit 3. Exit 2 without a case or `--bus`, for an unknown `--type`, or for a `--bus`
that is not in the case (`error: fault: fault bus <id> is not in case '<name>'.`, checked before any solve, the same
rule as `protection-check --fault-bus` and `iec60909 --bus`); exit 1 on a load error or a fault-analysis error. A
fault bus whose island has no fault source (no synchronous unit with a source reactance and, for the three-phase fault,
no converter-interfaced unit) is refused with exit 1, as `iec60909` refuses it: the loads alone would otherwise appear
to feed the fault. When
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

### fault-series

`gridsim fault-series <case|case.m|case-dir> --branch <index> --open 1|2 [--json <file.json>] [--csv <file.csv>]`

Open-conductor (series) fault in the series element of one branch: one conductor open (`--open 1`, phase a) or two open
(`--open 2`, phases b and c). It runs on the operating point as `fault --type` does: the prefault power flow, then the
same sequence networks (the positive sequence with every unit as X''d, the negative with X2 and the phase shifts
reversed, the zero sequence from the winding connections, line Z0 = 3 x Z1 where a line carries none). `--branch` is the
branch's index in the case (the `index` column of `fault --type`'s `branches` table; `0` is the first branch). The break
sits in series with the element, inside its pi section: the element's series impedance is taken out of the network, its
line-charging halves stay at its two buses, the Thevenin impedance between its two buses is
`Zth = Zii + Zjj - 2 Zij` of what remains in each sequence (the Zbus columns `fault --type` solves), and the element's own
series impedance is added back, so `Zs = Zth + Zb`. With `I` the prefault current through the element,
`(V_from - V_to) / Zb`, the open-circuit voltage across the break is `Zs1 I`, and the sequence connection is the textbook
one (Grainger and Stevenson; Anderson): one conductor open puts the three sequence networks in parallel across the break,
`I1 = Zs1 I / (Zs1 + Zs2 || Zs0)`; two open puts them in series, `I1 = I2 = I0 = Zs1 I / (Zs0 + Zs1 + Zs2)`. The result is
the sequence and phase currents through the break and the phase voltages across it (Core `SeriesFault.OpenConductors`;
the network side is `FaultAnalysis.SeriesBreak`).

A break is defined for a line, or a grounded-wye / grounded-wye transformer without mutual coupling, with a tap ratio of 0
or 1 and no phase shift. Anything else is refused by name with exit 1: a branch out of service, an off-nominal or
phase-shifting branch, a delta or ungrounded winding (its zero-sequence model is a shunt, so the series element has no
zero-sequence impedance), a break that leaves one side with no source, load or shunt, and a break whose zero-sequence
network is not earthed on both sides. The last is a common case (a radial feeder behind a delta transformer): the
zero-sequence impedance across the break is infinite, `SeriesFault.OpenConductors` takes a finite one, and no number is
invented. A MATPOWER or JSON v1 case carries no winding connection, so it has no earthed neutral and every break is refused
for that reason; use a JSON v2 case that gives windings (see [fault](#fault)). Converter-interfaced units are open in all three
sequence networks. The prefault solve must converge: otherwise `fault-series: the pre-fault power flow did not converge (...)`
and exit 3. The output gives the prefault current through the element, `Zs1`, `Zs2` and `Zs0`, the three phase currents
(pu, and kA when the bus has a base kV) and the three phase voltages across the break.

| Flag | Default | Meaning |
|---|---|---|
| `--branch` | - | The index of the branch whose series element is broken (required). |
| `--open` | - | The number of conductors open: `1` (phase a) or `2` (phases b and c) (required). |

Exit 0 done; 1 the case cannot be loaded or the break is refused (the reason is named); 2 usage, including a `--branch` that
is not in the case; 3 the prefault power flow did not converge. Result files: `--json <file.json>`, `--csv <file.csv>` or
`--out <file.json|file.csv>` write the result to a file ([Result files](#result-files)): the summary (case, branch, buses,
open conductors, the prefault current, `zs1`, `zs2` and `zs0` real and imaginary parts, the sequence current magnitudes) and a `phases`
table (`phase,i_re_pu,i_im_pu,i_mag_pu,i_mag_ka,v_across_re_pu,v_across_im_pu,v_across_mag_pu`).

### fault-simultaneous

`gridsim fault-simultaneous <case|case.m|case-dir> --at <bus>:<3ph|slg|ll|dlg>[,<bus>:<type>...] [--json <file.json>] [--csv <file.csv>]`

Solid shunt faults at several buses at the same instant (a cross-country fault, or one fault with a second developing at
another bus), on the operating point and the same sequence networks as `fault --type`. `--at` lists the buses and the fault at each:
`3ph`, `slg` (phase a to ground), `ll` (phases b-c) or `dlg` (phases b-c to ground); each bus takes one fault. The study
assembles the transfer-impedance matrices of the three sequence networks between the fault buses (one Zbus column per
bus, Core `FaultAnalysis.SequencePorts`), puts them in the phase domain and solves the per-bus boundary conditions
together (Core `SimultaneousFault.Solve`: a dense complex Gaussian elimination over `3N` phase unknowns, with partial
pivoting). A single `--at` entry is the ordinary unbalanced fault and agrees with `fault --type` to rounding. For each bus
it prints the phase currents into the fault (pu, and kA when the bus has a base kV), the earth current `|3I0|`, the sequence
current magnitudes and the phase voltages after the faults. The faults are solid: there is no `--zf`, and the relay overlay
of `fault` is not run. An earth fault (`slg`, `dlg`) at a bus that reaches no earthed neutral in the zero sequence has no earth
current to carry: it is refused with exit 3 and a message (and, on a case with no winding connection at all, that
the result would reflect missing data), as `fault` exits 3 for the same case; `3ph` and `ll` faults do not use the zero sequence.
Converter-interfaced units keep their prefault current, as in `fault --type`. A shunt fault and a series fault at the
same time is not offered: Core has no combined solution of the two (see [fault-series](#fault-series)). The prefault solve
must converge (exit 3 otherwise).

| Flag | Default | Meaning |
|---|---|---|
| `--at` | - | The fault buses and types, `<bus>:<type>` separated by commas (required). Types: `3ph`, `slg`, `ll`, `dlg`. |

Exit 0 done; 1 the case cannot be loaded or the study fails (a singular boundary-condition system is named); 2 usage, including a
bus that is not in the case or is named twice; 3 the prefault power flow did not converge, or an earth fault where no earth
current can flow. Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a
file ([Result files](#result-files)): the summary (case, buses, ports) and a `ports` table
(`bus,type,base_kv,phase_a_pu,phase_b_pu,phase_c_pu,phase_a_ka,phase_b_ka,phase_c_ka,earth_current_pu,i1_pu,i2_pu,i0_pu,va_pu,vb_pu,vc_pu,zero_sequence_grounded`).

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
| `--type` | `3ph` | `3ph` (three-phase), `ll` (line-to-line), `slg` (single-line-to-earth, on the zero-sequence network) or `dlg` (double-line-to-earth, the earth current `\|3 I0\|`). |
| `--objective` | `max` | `max` (voltage factor c = 1.10 HV / 1.05 LV, equipment rating) or `min` (c = 1.00 / 0.95, protection sensitivity). |
| `--tb` | 0.10 | Breaker minimum time delay (s) for Ib and the DC component; above 0. |
| `--tk` | 1.0 | Fault duration (s) for Ith; above 0. |
| `--breaker` | - | Check a switchgear rating against the duty (`Iec60909.CheckBreaker`): rated voltage kV, peak making kA, symmetrical breaking kA, short-time withstand kA, optionally its duration in s (default 1) and optionally the rated asymmetrical breaking kA (which adds the DC breaking check against `sqrt(Ib^2 + idc^2)`), comma-separated, every value above 0. The rated voltage must cover the highest system voltage `Um` of the bus (IEC 60038: 145 kV for 132 kV, 420 kV for 400 kV), not its nominal voltage; a double-line-to-earth duty is checked at its largest line-conductor current. Prints each check with its signed margin (rating minus demand; the withstand is referred to `--tk` on an equal-energy basis). An inadequate breaker exits 3. |
| `--branches` | off | List every in-service branch's from- and to-end current for the three-phase fault (`Iec60909.BranchFaultCurrents`, the current a relay on the branch sees; c_max source), every branch of the fault bus's island, largest first. Three-phase at one `--bus` only (else exit 2). The result file gets a `branch_currents` table. (D-DOC-168: the console list used to stop at 12 rows while its header counted every branch.) |
| `--top N` | all | With `--branches`: print only the N largest rows, and a closing line saying how many were left out (the result file still holds every branch). N at least 1; exit 2 without `--branches`. |
| `--all-buses` | off | The duty at every bus with a base kV instead of one `--bus` (not both, else exit 2). Prints one row per bus and the largest Ik''; the result file gets a `buses` table. A bus that cannot be studied (no base kV, no source) is listed with the reason and the run exits 1 after the table; with `--breaker`, an inadequate breaker at any bus (checked at the bus's own voltage) exits 3. |
| `--corrections` | off | Apply the IEC 60909-0 impedance-correction factors before the study (maximum objective only, else exit 2): `K_G` to a directly connected generator's X''d and X2, `K_S` (or `K_SO` with `--no-unit-oltc`) to a power-station unit (a unit with a unit transformer reactance) as a whole, `K_T` to each network transformer's impedance with x_T taken on its own rating from RateA and c_max that of the network on the transformer's low-voltage side (IEC 60909-0:2016, 6.3.3; 1.05 for an LV side at or below 1 kV), (a transformer with no RateA, and any element on a bus with no base kV, is listed as not corrected). The model is per unit on nominal voltages, so the voltage ratios in the formulas are taken as 1. Needs `--rated-pf`. The factors applied are printed (range per factor) and written to a `corrections` table. [VERIFY]: the K_S / K_SO forms and the zero-sequence correction against the standard's text (IEC 60909-0 is not in docs/reference). |
| `--rated-pf` | - | With `--corrections` (required): the generators' rated power factor cos(phi_rG), in (0, 1]; GridSim carries none, so it is never assumed. |
| `--no-unit-oltc` | off | With `--corrections`: unit transformers without an on-load tap changer (`K_SO` at the main tap, p_G = 0, instead of `K_S`). |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).

### g74

`gridsim g74 <case|case.m|case-dir> (--bus <id> | --all-buses) [--time <s>[,<s>...]] [--tb 0.10] [--tk 1.0] [--g74-data <file.json>] [--no-decay] [--objective max|min] [--peak-method b|c] [--breaker kV,making,breaking,withstand[,s]]`

A G74-style short-circuit duty built over the IEC 60909 machinery (RC4 R4-06; `Protection/ShortCircuit/Ena`). It reuses
`Iec60909` read-only, so `iec60909` is unchanged. For each fault bus it prints the initial symmetrical current Ik'', the
peak ip, the AC break current at each `--time` and at `--tb`, the steady current, and every contribution
(synchronous machine, induction motor, converter) with its own initial, break-time and steady value, in a fixed
element order, plus an optional breaker check with signed margins (as `iec60909 --breaker`).

**Status of the standard.** ENA ER G74 Issue 2:2021 ("Procedure to meet the requirements of IEC 60909 for calculating
short-circuit currents in three-phase AC power systems") could not be obtained when this was built. No G74 clause,
coefficient or worked example is used, and every run prints the list of items marked AWAITING STANDARD TEXT: G74's
machine decay, motor, converter (including negative sequence) and peak treatment. What is computed is the classical
two-exponential envelope per synchronous machine, `I(t) = I'' [ (1 - X''d/X'd) e^(-t/T''d) + (X''d/X'd - X''d/Xd) e^(-t/T'd) + X''d/Xd ]`
with the unit transformer reactance added to each reactance and the time constants taken as supplied (no
external-impedance adjustment); an induction motor as an equivalent source of impedance `1 / (I_LR / I_rM)` on its own
base, decaying either exponentially with a supplied time constant or by the IEC 60909-0 asynchronous-motor factor q
(`Iec60909.MotorBreakingFactorQ`); a converter as the incumbent current-limited positive-sequence source, constant in
time, with no negative-sequence infeed. The break current is the sum of the contribution magnitudes (exact for in-phase
partial currents, conservative otherwise). The peak is the IEC 60909 peak. Nothing is defaulted: a machine without decay
data, or whose X''d is the synthesised default, makes the bus INCOMPLETE and no number is printed.

`--g74-data` reads `gridsim-g74-data/1`: `{"schema": "gridsim-g74-data/1", "generators": [{"bus": 1, "name": "G1", "xdTransPu": 0.3,
"xdSyncPu": 1.2, "tdSubS": 0.05, "tdTransS": 1.0}], "motors": [{"name": "M1", "bus": 3, "ratedMva": 10,
"lockedRotorCurrentRatio": 6, "decay": "exponential", "timeConstantS": 0.04}]}`. Reactances are per unit on the generator's MBase
(X''d is the case's own); a motor's `decay` is `exponential` (needs `timeConstantS`) or `iec60909-q` (needs `ratedMw` and
`polePairs`). `iec60909-q` is the IEC 60909-0 motor breaking current mu q Ik'', with mu from the motor's own Ik''/IrM; at
time 0 the motor gives Ik''. IEC gives mu and q from 0.02 s, so with an `iec60909-q` motor and decay on, a time or `--tb`
between 0 and 0.02 s is refused (exit 1). With `--no-decay` and no motors, Ik'' and ip equal `iec60909` bit for bit. Exit 0 done; 1 unreadable case or
data file or a study error; 2 usage (no bus, unknown bus, bad flag); 3 INCOMPLETE data or an inadequate breaker.

| Flag | Default | Meaning |
|---|---|---|
| `--bus` | required (or `--all-buses`) | Fault bus id. |
| `--all-buses` | off | Run every bus with a base kV (not both with `--bus`, else exit 2). |
| `--time` | the `--tb` value | Comma list of break times in seconds (0 or more) at which the AC current is evaluated. |
| `--tb` | 0.10 | Breaker time (s) for the break current, the breaker check and the IEC DC and peak terms; above 0. |
| `--tk` | 1.0 | Fault duration (s) for the thermal terms of the underlying IEC duty; above 0. |
| `--g74-data` | none | Decay data file (`gridsim-g74-data/1`: generator X'd, Xd, T''d, T'd; induction motors). Required for decay with a synchronous machine in the island. |
| `--no-decay` | off | Switch machine and motor decay off: every contribution stays at its initial value. |
| `--objective` | `max` | `max` (c_max) or `min` (c_min), as `iec60909`. |
| `--peak-method` | `b` | The IEC 60909 peak method, `b` or `c`, as `iec60909`. |
| `--breaker` | - | Breaker rating: rated voltage kV, making kA, breaking kA, withstand kA, optionally its duration in s; every value above 0. The breaking check uses the G74-style break at `--tb`. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result (tables `buses`,
`contributions`, `break_times`; [Result files](#result-files)).

### ansi-sc

`gridsim ansi-sc <case|case.m|case-dir> (--bus <id> | --all-buses) --ansi-data <file.json> [--parting-cycles <n>] [--prefault 1.0] [--breaker kV,closing_latch_kA,interrupting_kA]`

An ANSI/IEEE C37.010-style breaker duty by the E/X method (RC4 R4-20a; `Protection/ShortCircuit/Ansi`). It is a new calculation
beside `iec60909` (which it only calls to find the faulted island) and reuses the fault network assembly. **Verification status:
implemented from public documentation, equations only; not verified against the standard.** The IEEE C37 texts are paid and were
not read; every run prints the list of items marked AWAITING STANDARD TEXT.

What is computed. Two reactance networks are built from the case: the first-cycle (momentary) network and the interrupting
(contact-parting) network, in each of which every source's X''d is multiplied by that source's multiplier (a unit transformer
reactance is not multiplied); loads, line charging and branch resistance are left out. A separate R network (every reactance zero,
branch R kept, each machine R = X''d / (X/R)) gives the fault-point resistance, so the fault X/R is X(network) / R(network) (a branch
with no resistance is held at 1e-9 pu there, which only matters as a numerical floor). Symmetrical currents are E/X with E the
prefault voltage. The first-cycle (close and latch) current is `MF x E/X`, with MF = 1.6 for a first-cycle X/R up to 25 (a published
constant, see below) and above that the half-cycle rms expression `sqrt(1 + 2 e^(-2 pi/(X/R)))`. The crest value is
`sqrt(2) (1 + e^(-pi/(X/R))) E/X`. The interrupting duty is `MF x E/X` of the interrupting network with the remote-source factor
`sqrt(1 + 2 e^(-4 pi n/(X/R)))` at `n` = `--parting-cycles` (DC offset physics: the DC part starts at the AC peak and decays with the
fault X/R). NACD is the share of the interrupting-network current fed by sources marked `local`. The current of each source is the
E/X current divider of each network.

Out of scope, said in the output rather than guessed: when NACD is above 0 (any local generation feeding the fault) the local
multiplying factors of the standard are AWAITING, so no interrupting duty is given; the remote-factor value is printed as a
reference only and is not a duty. The same holds when `--parting-cycles` is not given or a source has `interruptingMultiplier: null`.
The momentary duty is still given. Converter-interfaced plant is neglected and listed in the notes.

`--ansi-data` reads `gridsim-ansi-data/1` and nothing is defaulted: `{"schema": "gridsim-ansi-data/1", "sources": [{"bus": 1,
"name": "G1", "class": "turbine-generator", "xOverR": 40, "local": true, "firstCycleMultiplier": 1.0, "interruptingMultiplier": 1.0}],
"motors": [{"name": "M1", "bus": 3, "ratedMva": 5, "xdppPu": 0.2, "class": "induction-motor", "xOverR": 10, "local": false,
"firstCycleMultiplier": 1.0, "interruptingMultiplier": null}]}`. A `sources` entry describes the case's generator at that bus (by
`name`, or the only one at the bus); the case's X''d must be real, not the synthesised default. A motor entry adds a source with X''d
on its own base. The multiplier per machine type is the owner's to fill from the standard (`class` is only a label); public
descriptions confirm the idea (a larger multiplier in the interrupting network, for example 1.5 for large 1800 r/min induction
motors) but the full table is AWAITING STANDARD TEXT. A missing source makes the bus INCOMPLETE. Exit 0 done; 1 unreadable case or
data file or a study error; 2 usage (no bus, no data file, unknown bus, bad flag); 3 INCOMPLETE data or an inadequate breaker.

Public sources used (none is the standard): EasyPower ANSI short-circuit help, "Calculation and Theory" (separate R network for X/R;
multipliers on rotating-machine subtransient reactance for the interrupting network; NACD from local and remote contributions);
EC&M, "Short Circuit Current Duties of Circuit Breakers and Fuses, Part 1" (first-cycle MF = 1.6 for X/R up to 25 on constant-MVA
breakers); Siemens TechTopics on asymmetrical fault current (S = sqrt(1 + 2 (%dc/100)^2), DC decay set by the fault X/R). The
1.6 is cross-checked in the tests against the half-cycle rms expression (1.598 at X/R 25).

| Flag | Default | Meaning |
|---|---|---|
| `--bus` | required (or `--all-buses`) | Fault bus id. |
| `--all-buses` | off | Run every bus with a base kV (not both with `--bus`, else exit 2). |
| `--ansi-data` | required | Source data file (`gridsim-ansi-data/1`): X/R, local or remote, first-cycle and interrupting multipliers; optional motors. |
| `--parting-cycles` | none | Breaker contact parting time in cycles of system frequency (0 or more). Without it the interrupting duty is OUT OF SCOPE. |
| `--prefault` | 1.0 | Prefault voltage E in per unit; above 0. |
| `--breaker` | - | Breaker rating: rated maximum voltage kV, closing and latching capability kA rms, symmetrical interrupting capability kA; every value above 0. No credit is taken for a voltage below rated (the range factor K is AWAITING). |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result (tables `buses`,
`contributions`; [Result files](#result-files)).

### sc-61363

`gridsim sc-61363 <case|case.m|case-dir> (--bus <id> | --all-buses) --iec61363-data <file.json> [--time <s>[,<s>...]] [--tb 0.10] [--objective max|min] [--breaker making_peak_kA,breaking_rms_kA]`

An IEC 61363-1-style short circuit for ship and offshore installations (RC4 R4-20b; `Protection/ShortCircuit/Iec61363`): the decaying
AC component of each generator plus its decaying DC component, summed at the fault bus. It is a new calculation beside `iec60909` and
`g74` (it calls `iec60909` only for the per-source initial currents, as `g74` does). **Verification status: implemented from public
documentation, equations only; not verified against the standard.** IEC 61363-1 is a paid text and was not read; every run prints the
list of items marked AWAITING STANDARD TEXT.

What is computed. Each source's initial current comes from the case (a motor in the data file is added as an equivalent source of
impedance 1 / (I_LR / I_rM) on its own rating). The AC current of a synchronous machine follows the two-exponential envelope
`(I'' - I') e^(-t/T''d) + (I' - Ik) e^(-t/T'd) + Ik` (reactances include the unit transformer); a motor decays as `I'' e^(-t/T)`; a
converter holds a constant current and has no DC component. The DC component of a machine or motor is `sqrt(2) I'' e^(-t/Ta)` (worst-case
switching instant: the offset starts at the AC peak). Totals are in-phase sums in a fixed order (exact for a lossless network, conservative
otherwise); the asymmetrical rms is `sqrt(AC^2 + (DC/sqrt(2))^2)`. The peak is the maximum of
`|sqrt(2) sum A_i(t) cos(wt) - sum sqrt(2) I''_i e^(-t/Ta_i)|` over the first 0.2 s (400 samples per cycle, refined by ternary search).
The breaker check compares the peak with the making capacity and the symmetrical AC at `--tb` with the breaking capacity (which quantity
the standard actually compares is AWAITING). Network meshing is not checked; the method is for unmeshed systems.

`--iec61363-data` reads `gridsim-iec61363-data/1` and nothing is defaulted: `{"schema": "gridsim-iec61363-data/1", "generators": [{"bus": 1,
"name": "G1", "xdTransPu": 0.3, "xdSyncPu": 1.2, "tdSubS": 0.05, "tdTransS": 1.0, "taS": 0.04}], "motors": [{"name": "M1", "bus": 1,
"ratedMva": 10, "lockedRotorCurrentRatio": 6, "acTimeConstantS": 0.04, "taS": 0.03}]}`. The case's X''d must be real. A source with no data
makes the bus INCOMPLETE. Exit 0 done; 1 unreadable case or data file or a study error; 2 usage; 3 INCOMPLETE data or an inadequate breaker.

Public sources used (none is the standard): the scope and structure of IEC 61363-1 (VDE Verlag preview of the 1998 edition; SKM
and ETAP product notes describing a decaying AC plus decaying DC sum); textbook machine theory for the time constants and armature DC
component (Kundur, Power System Stability and Control, chapter 4; Grainger and Stevenson, Power System Analysis, chapter 10; Anderson,
Analysis of Faulted Power Systems). Vendor claims about the standard were not relied on.

| Flag | Default | Meaning |
|---|---|---|
| `--bus` | required (or `--all-buses`) | Fault bus id. |
| `--all-buses` | off | Run every bus with a base kV (not both with `--bus`, else exit 2). |
| `--iec61363-data` | required | Machine and motor data file (`gridsim-iec61363-data/1`): X'd, Xd, T''d, T'd, Ta per generator; motors with AC and DC time constants. |
| `--time` | the `--tb` value | Comma list of times in seconds (0 or more) at which AC, DC and asymmetrical rms are reported. |
| `--tb` | 0.10 | Break time in seconds for the breaker check; above 0. |
| `--objective` | `max` | `max` (c_max) or `min` (c_min), as `iec60909`. |
| `--breaker` | - | Breaker rating: making capacity kA peak, breaking capacity kA rms; both above 0. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result (tables `buses`, `times`,
`contributions`; [Result files](#result-files)).

### sc-dc

`gridsim sc-dc <network.json> [--time <s>[,<s>...]] [--step <s>] [--end-time <s>] [--breaker making_A]`

An IEC 61660-style short circuit of a DC auxiliary installation: batteries, rectifiers, charged capacitors and DC motors feeding a fault
through a resistive-inductive network (RC4 R4-20c; `Protection/ShortCircuit/Dc`). The case format carries no DC data, so the network is
its own study file and this verb takes that file, not a case. **Verification status: implemented from public documentation, equations
only; not verified against the standard.** IEC 61660-1 is a paid text and was not read; every run prints the list of items marked
AWAITING STANDARD TEXT.

What is computed. The network is solved as a circuit rather than through the standard's equivalent-circuit coefficients (which are
AWAITING): modified nodal analysis with the trapezoidal rule (one backward-Euler start step, so that the voltage jump at fault
inception does not enter the first step), a fixed step, a matrix factored once with partial-pivot LU, and no transcendental function (so
results are bit-identical between runs). The prefault state is the DC steady state with the fault open (inductors short, capacitors
charged, motors at their EMF). Each source is a voltage behind R and L: a battery or rectifier holds a constant EMF, giving
`i = (E/R)(1 - e^(-R t/L))` alone; a capacitor has `C dU/dt = -i`, giving the second-order discharge with `L C s^2 + R C s + 1 = 0`; a
motor has back EMF `k w` with `J dw/dt = -k i` (load torque and field decay neglected), giving `L J s^2 + R J s + k^2 = 0`. The report
gives the fault current peak and its time, the current at the end time and at each `--time`, and each source's peak, time and end value.
The step defaults to the shortest time constant divided by 400 and the end time to 8 times the longest (L over R, R C, J R over k^2 of
the summed loop); if either cannot be chosen, or more than 5 million steps would be needed, the run is INCOMPLETE and says what to give.
Both conductors are lumped into each branch's R and L; the return is the node named `"0"`.

`network.json` is `gridsim-dc-network/1` and nothing is defaulted: `{"schema": "gridsim-dc-network/1", "branches": [{"name": "cab",
"from": "1", "to": "2", "rOhm": 0.03, "lH": 0.002}], "sources": [{"name": "B1", "kind": "battery", "node": "1", "eV": 110, "rOhm": 0.02,
"lH": 0.001}, {"name": "C1", "kind": "capacitor", "node": "1", "uV": 110, "rOhm": 0.05, "lH": 0.0005, "cF": 0.01}, {"name": "M1", "kind":
"motor", "node": "1", "eV": 200, "rOhm": 0.1, "lH": 0.02, "jKgM2": 0.5, "kVsPerRad": 2}], "fault": {"node": "2", "rOhm": 0}}`. Source kinds:
`battery`, `rectifier` (a constant EMF here; its commutation and AC-side behaviour are AWAITING), `capacitor` (`uV`, `cF`, needs `lH` above
0) and `motor` (`eV` the back EMF at the fault instant, `jKgM2`, `kVsPerRad`, needs `lH` above 0). A node with no path to a source makes the
prefault matrix singular and the run INCOMPLETE. Exit 0 done; 1 unreadable or invalid network file; 2 usage; 3 INCOMPLETE or a making
capacity exceeded.

Public sources used (none is the standard): the scope of IEC 61660-1 (auxiliary DC installations in power plants and substations;
secondary descriptions only) and standard circuit theory (series R-L charging, series R-L-C discharge, a separately excited DC machine
as generator behind its armature R and L; for example Fitzgerald, Kingsley and Umans, Electric Machinery, chapter on DC machines).

| Flag | Default | Meaning |
|---|---|---|
| `--time` | none | Comma list of times in seconds (0 or more) at which the fault current and each source current are reported. |
| `--step` | shortest time constant / 400 | Fixed time step in seconds; above 0. |
| `--end-time` | 8 times the longest time constant | End of the simulated window in seconds; above 0. |
| `--breaker` | - | Making capacity in amperes compared with the fault current peak; above 0. Exit 3 if exceeded. |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result (tables `sources`, `times`;
[Result files](#result-files)).

### arc-flash

`gridsim arc-flash <study.json> [--out <result.json>]`

Incident energy and arc-flash boundary of one piece of equipment to IEEE 1584-2018 (RC3 P8): equations 1 to 25 with the
coefficients of Tables 1 to 7, three-phase AC only. The coefficients are as transcribed in the open-source (MIT licence) `arcflash`
library (Li-aung Yip); they were not checked against a copy of the standard here. The `fault` and `iec60909` verbs, their
duties and their equipment checks are independent and unchanged: this verb only reads `Iec60909.ThreePhase` outputs.

The study file is strict JSON, schema version 1: `schemaVersion` (1); `label`; `equipment` (`systemVoltageKv` Voc,
`configuration` `VCB`, `VCBB`, `HCB`, `VOA` or `HOA`, `gapMm`, `workingDistanceMm`, `frequencyHz`, `phases`, and
`enclosure` with `heightMm`, `widthMm`, `depthMm` for the box configurations only); `boltedFault`, either `ka` with
`source` (where the current came from) or `iec60909` (`case` with `builtIn` or a single case file `path` and its
`sha256`, `bus`, `objective` `Maximum` or `Minimum`: the bolted current is that bus's three-phase Ik'', and its base
voltage must match Voc to 0.5 %); `clearingTime` (`fullArcingCurrentMs`, `reducedArcingCurrentMs` and `source`: the
arc durations at the full and reduced arcing currents, read by the user from the protective device's curve; no device
curve is evaluated here).

Envelope (clause 4.2): Voc 0.208 kV to 15 kV, 50 or 60 Hz, three-phase; Ibf 0.5 kA to 106 kA at or below 0.6 kV and
0.2 kA to 65 kA above; gap 6.35 mm to 76.2 mm at or below 0.6 kV and 19.05 mm to 254 mm above; working distance at
least 305 mm; enclosure width at least four times the gap (a height or width above 1244.6 mm is taken at 1244.6 mm, as
Table 6 directs). Anything outside it gives `status` `OUT OF SCOPE` listing every breach, and no numbers.

The result gives VarCf, the enclosure correction (type, equivalent width and height, EES, CF), and for the full and the
reduced arcing current the intermediate and final arcing current (kA), the clearing time used (ms), the intermediate and
final incident energy (J/cm2 and cal/cm2) at the working distance, and the arc-flash boundary (mm, the distance at which
the energy is 1.2 cal/cm2); then the governing scenario (the higher energy) with its energy, boundary and time. Exit 0
`EVALUATED`, 3 `OUT OF SCOPE`, 1 for input it cannot accept (a malformed study, a case identity mismatch, a voltage
mismatch, an existing output path), 2 for an invalid invocation or an unknown built-in case name.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |

### cable-rating

`gridsim cable-rating <study.json> [--out <result.json>]`

Steady-state current rating of one single-core unarmoured cable laid direct in the ground, isolated from other cables
(RC3 P8): IEC 60287-1-1 (rating equation with n = 1, T2 = 0 and lambda2 = 0; AC resistance with the skin effect for xs
up to 2.8 and no proximity effect; dielectric losses) and IEC 60287-2-1 (T1, T3, and T4 of a single isolated buried
cable, `rho/(2 pi) ln(u + sqrt(u^2 - 1))`, `u = 2L/De`). Groups, ducts, troughs, soil drying, the screen loss factor
from the bonding arrangement and transient or cyclic ratings (IEC 60853) are not implemented, and no published ampacity
example has been checked here.

The study file is strict JSON, schema version 1: `schemaVersion` (1); `label`; `cable` (`material` `Copper` or
`Aluminium`, `dcResistance20OhmPerKm`, `skinEffectKs`, `conductorDiameterMm`, `insulationDiameterMm`,
`screenDiameterMm`, `overallDiameterMm`, `insulationThermalResistivity` and `oversheathThermalResistivity` in K.m/W,
`relativePermittivity`, `lossTangent`, `maxConductorTemperatureC`); `system` (`u0Kv`, `frequencyHz` 50 or 60);
`installation` (`depthToAxisM` to the cable axis, `soilThermalResistivity`, `ambientGroundTemperatureC`);
`screenLoss` (`lambda1` and its `source`); optional `nameplate` (`amperes` and `source`); `selection` (`None`,
`Calculated`, `Nameplate` or `Lower`).

The result gives every intermediate quantity (resistance at the maximum temperature, xs, ys, AC resistance,
capacitance, dielectric loss, T1, T3, u, T4, the permissible rise, the conductor and screen losses at the rating), the
calculated rating in A, the nameplate rating with its source as separate evidence, their ratio, and the selected rating
and its basis (none unless `selection` asks for one). An input outside the method's scope gives `status` `OUT OF SCOPE`
with every reason. Exit 0 `EVALUATED`, 3 `OUT OF SCOPE`, 1 for input it cannot accept, 2 for an invalid invocation.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |

### cable-size

`gridsim cable-size <study.json> [--out <result.json>]`

Cable cross-section selection by the design rule of IEC 60364-5-52 and BS 7671 (RC4 R4-20d; `Analysis/CableRating/Sizing`):
`Ib <= In <= Iz` with `Iz = It Ca Cg ...`, so the tabulated current needed is `It >= In / (Ca Cg ...)`; then voltage drop and the
adiabatic short-circuit check. **Verification status: the method is implemented from public documentation, equations only; every
table number is AWAITING STANDARD TEXT and is supplied by the owner; not verified against the standard.** The tables of the two
standards are paid text, so nothing is shipped filled: `data/cable-tables` holds empty, flagged template files (see its README).
A quantity whose table is missing or empty is reported `OUT OF SCOPE` with the reason, never defaulted, and no size is selected
unless every requested check was evaluated. `cable-rating` (IEC 60287) and its code are unchanged; the two verbs are independent.

The study file is strict JSON, schema version 1: `schemaVersion` (1); `label`; `tables` (path of a `gridsim-cable-tables/1`
file, relative to the study file); `circuit`: `designCurrentA` (Ib), optional `deviceRatingA` (In, default Ib is stated, not silent),
`systemVoltageV`, `phases` (1 or 3), `lengthM`, `powerFactor`, `conductor`, `insulation` and `installationMethod` (labels matched
exactly against the table file), `loadedConductors`; optional `ambientC` and `groupCount` (null means that correction is not
requested and the result says so), `extraFactors` (named factors with a value and a source), `voltDropLimitPercent` (required for the
voltage-drop check: no limit is assumed), `faultCurrentA` with `faultDurationS` (adiabatic check `S >= sqrt(I^2 t) / k`), and
`iec60287`: candidate cross-sections each with a full `CableConstruction` as in `cable-rating`, plus `system`, `installation` and
`screenLoss`, in which case the base ratings are computed by IEC 60287 (the soil temperature is then in the installation, so no
ambient factor is applied; grouping and named factors are applied on top) instead of read from the ampacity table.

Voltage drop is `mV/A/m x Ib x L / 1000` as a percentage of the system voltage when a voltage-drop table is held, otherwise
`K Ib L (R cos(phi) + X sin(phi))` with K = sqrt(3) (three-phase) or 2 (single-phase) from the impedance table. Correction factors
are taken at exact tabulated keys only (no interpolation). The smallest size that satisfies every requested check is selected, and the
result lists every candidate with its tabulated and permitted current and each check's figure. Result fields: `status`
(`EVALUATED`, `OUT OF SCOPE`, `NO SIZE`), `outOfScope`, the table file id, status and provenance, `requiredTabulatedAmps`,
`combinedFactor`, `checks` (with clause references to the public design rule, not checked against the paid texts), `selectedSizeMm2`,
`candidates`, `factors`, `awaiting`. Exit 0 `EVALUATED`, 3 otherwise, 1 for input it cannot accept, 2 for an invalid invocation.

Public sources used (none is the standard): the design rule `Ib <= In <= Iz` and the structure of the tables as summarised in
public guidance (for example the IET and NICEIC guidance notes and the free IEC 60364 preview pages); the adiabatic equation
`S = sqrt(I^2 t)/k` and the three-phase and single-phase voltage-drop formulae are standard circuit theory.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |

### connection-assess

`gridsim connection-assess <study.json> --rules <id|rules.json> [--out <result.json>]` and `gridsim connection-assess --list-rules`

Result files: `--json <file.json>` or `--csv <file.csv>` write the result to a file through the shared writer ([Result files](#result-files)); the document is built from the same result this verb prints, and `--json` is the form `gridsim.run` and `gridsim rpc` use. `--out` is unchanged: it still names its result JSON, not a shared-writer file.

A rule-set driven connection assessment (RC4 R4-20e; `Harmonics/Planning/RuleSetEngine.cs`, rule files in
`Harmonics/Planning/Rules`), built on the R4-21 assessment framework: a PCC voltage and named quantities are compared with the rules of a
rule file. The German connection rules are the intended content: VDE-AR-N 4105 (low voltage), 4110 (medium voltage), 4120 (high voltage), 4130
(extra-high voltage) and the older BDEW medium-voltage guideline. **Verification status: structure and publicly documented parts only; every
limit value, curve and test condition is AWAITING STANDARD TEXT; not verified against the standards.** The rule files shipped with GridSim
(`--list-rules` prints their ids) are EMPTY and flagged: each lists the topics that public summaries name (steady-state voltage change, rapid
voltage change, flicker, harmonic currents, reactive power capability, fault ride-through, over-frequency active power reduction,
short-circuit contribution, and phase unbalance for 4105) with the limit left null, so every rule is reported `OUT OF SCOPE`
(AWAITING STANDARD TEXT) and the verdict is `INCOMPLETE`. The only values filled are the voltage ranges of the four VDE rule sets, from the
VDE FNN overview of the technical connection rules by voltage level (4105 up to 1 kV, 4110 1 to 60 kV, 4120 60 to 220 kV, 4130 220 kV and
above), cited in the file; the BDEW file gives no range. Whether a topic sits in a given edition is for the owner to confirm.

A rule is `{id, topic, clause, quantity, comparison, limit, limitHigh, unit, when, note}`: `comparison` `<=`, `>=` or `range`; `limit` null
means awaiting; `when` lists conditions on other supplied quantities (`<`, `<=`, `>`, `>=`, `==`). A rule file with any limit must carry
provenance (source, edition, filledBy, date) and each limit its clause, or it is refused. Per rule the result is `Pass`, `Fail` (with the
signed margin, positive inside the limit), `NotApplicable` (a condition is not met), `OutOfScope` (limit awaiting, or the PCC is outside the
rule set's voltage range) or `NotEvaluated` (a quantity was not supplied). The verdict is `NON-COMPLIANT` on any failure, else `INCOMPLETE`
while anything applicable is unevaluated, else `COMPLIANT`; a PCC outside the rule set's range is `OUT OF SCOPE`. Nothing is defaulted or passed
silently. To fill a rule set, copy a shipped file, set the limits with their clauses and provenance, and pass it as `--rules <file.json>`.

The study file is strict JSON: `schemaVersion` (1), `label`, `pccVoltageKv`, `quantities` (a name to number map; the names are those of the
rules' `quantity` fields), and optionally `harmonicAssess`, the path (relative to the study file) of a `harmonic-assess` result file whose
THD figures are added as `harmonic.thd_planning_level_pct`, `harmonic.thd_predicted_pct` and `harmonic.thd_incremental_pct`. Exit 0
`COMPLIANT`, 3 anything else, 1 for input it cannot accept, 2 for an invalid invocation.

Public sources used (none is the standard): VDE FNN, technical connection rules overview by voltage level (vde.com/en/fnn); the R4-21 G5/5
framework in this repository. The BDEW guideline and the limit values were not obtained.

| Flag | Default | Meaning |
|---|---|---|
| `--rules` | required | A built-in rule set id (see `--list-rules`) or the path of a `gridsim-connection-rules/1` file. |
| `--list-rules` | off | Print the built-in rule set ids and exit; no study is read. |
| `--out` | none | Also write the result JSON to this new file. |

### grid-code

`gridsim grid-code <study.json> [--out <result.json>]` and `gridsim grid-code --list`

The grid-code compliance suite (`GridSim.Core.Compliance.GridCode`): fault ride-through, reactive capability, frequency
response, RoCoF and frequency withstand of one unit against the GB Grid Code European Connection Conditions (Issue 6
Revision 33, 11 June 2025) or Regulation (EU) 2016/631 (RfG). GB values are fixed by the code; RfG values are the TSO's
choices, each refused unless it lies inside the Regulation's range (the message names the parameter, the range and the
article). The requirement data ships as `gridsim-gridcode-requirements/1` files, one per edition, every value with its
clause; `--list` prints their ids and titles and the check ids. See [ch. 15](<0015 RMS, modal and impedance stability.md#grid-code-compliance>)
for the checks and their methods.

The study file is strict JSON, `gridsim-gridcode-study/1` (schema `schemas/gridcode/gridsim-gridcode-study-1.schema.json`):
`schema`, `label`, `case` (a case path relative to the study file, read as any case argument), `unit` (`bus`, and `name`
when the bus has several generators), `pccBus`, `code` (`gb-ecc` or `eu-rfg`), `moduleType` (`B`, `C` or `D`),
`technology` (`synchronous`, `ppm`, `hvdc`, `dc-ppm` or `ppm-gfm`), `connectionKv` and `checks`; optionally `dynData` (a
`gridsim-dynamics/2` file, or a `.dyr` file for a `.raw` case, binding every generator; needed by the RMS and governor
checks), `plantBuses` (for `q-capability`), `frtProfile` (RfG: the TSO's `uret` ... `trec3`), `extendedClearingTime`
(RfG: allows tclear up to 0.25 s), `area` (RfG: `CE`, `Nordic`, `GB`, `IE-NI` or `Baltic`), `fsmMode` (GB LFSM-O from
50.5 Hz) and `stepSeconds` (RMS step, default 0.01). The checks are `frt-envelope`, `frt-retained-voltage`,
`frt-recovery`, `frt-uv-settings`, `q-capability`, `lfsm-o`, `lfsm-u`, `fsm-step`, `rocof-settings`, `rocof-event` and
`frequency-relays`, each `{ "id": ..., inputs }` as the schema lists.

Four more GB checks run only when named, with `code` `gb-ecc`. Their values come from the extension file
`gb-ecc-issue6-rev33-ext`, and a result that runs one records its hash as `provenance.requirementsExtSha256`:

- `ffci-envelope`: optional `fault` and `durationSeconds`, as `frt-envelope`.
- `frt-long`: optional `durationsSeconds` (default: the profile's labelled corners over 0.14 s), `stageSeconds` (0.05),
  `afterSeconds` (1.0), `maxPasses` (8), `bus` (default the PCC) and `atSeconds` (0.1). Times must be whole steps.
- `avr-step`: optional `stepSeconds` (default the study step) and `durationSeconds` (10, longer than 3 s). It needs the
  unit's exciter in `dynData`.
- `vctrl-step`: `events` (as `rocof-event`) and `durationSeconds`; optional `leadToLag` (the 2 s limit) and `qmaxMvar`
  and `qminMvar` (default the unit's case values).

Under `eu-rfg` these four are refused.

The result is `gridsim-gridcode/1`: per check the id, case, clause, outcome, status, value, limit, signed margin (positive
inside the requirement), unit, the time of the margin and the reason, then the counts and the verdict: `NON-COMPLIANT` on
any failure, else `INCOMPLETE` while any check is not evaluated or not demonstrated, else `COMPLIANT`. A check that cannot
be evaluated is never a pass. Numbers are written in invariant culture with shortest round-trip formatting, so two runs give
the same bytes. Exit 0 `COMPLIANT`, 3 anything else, 1 for input it cannot accept, 2 for an invalid invocation.

Result files: `--json <file.json>` or `--csv <file.csv>` write the result to a file through the shared writer ([Result files](#result-files)): the `gridsim-gridcode/1` document itself, its scalar members as the summary and each array as a table. `--out` is unchanged: it still names its result file, not a shared-writer file.

| Flag | Default | Meaning |
|---|---|---|
| `--list` | off | Print the built-in requirement sets and the check ids, and exit; no study is read. |
| `--list-ext` | off | Print the GB extension requirement set and the extension check ids (`ffci-envelope`, `frt-long`, `avr-step`, `vctrl-step`), and exit; no study is read. |
| `--out` | none | Also write the result JSON to this new file. |

### experimental

`gridsim experimental [<subcommand> <case> [flags]]`

Opt-in experimental models, tagged unvalidated-at-scale and kept off the verified solve and estimate paths. With no
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

### gic

`gridsim gic <case> --gic <file.json> [--field N,E] [--direction-sweep <degrees>] [--out <file.json>]`

Geomagnetically induced currents for a uniform geoelectric field (Core `GicSolver`), by the method of the NERC
application guide (December 2013): each line gets the DC source V = E_N L_N + E_E L_E from its northward and eastward
length, and the DC network of lines, transformer windings and substation grounds is solved by nodal analysis. The data a
load-flow case does not hold comes from a `gridsim-gic/1` file: substation coordinates and ground resistance, the buses in
each substation, the transformers' winding type and DC resistance, an optional reactive-loss factor k and an optional
blocking device, and line ohms where the case's R is not the DC resistance. See
[technical ch. 44](<../technical/0044 Geomagnetically induced currents.md>).

| Flag | Default | Meaning |
|---|---|---|
| `--gic` | required | The `gridsim-gic/1` file. Every in-service transformer of the case must be described in it (type `open` for no DC path), and every line's buses must be in a substation. |
| `--field` | none | The northward and eastward field, V/km, as `N,E`. |
| `--direction-sweep` | none | A sweep at 1 V/km over directions 0, step, 2 step, ... below 360 degrees (from north towards east); reports each transformer's largest effective GIC and its direction. Step above 0, at most 180. |
| `--out` | none | Write the result as JSON (`gridsim-gic-result/1`: the gic file's SHA-256, per substation the neutral current and voltage, per transformer I_H, I_X, the neutral current, the effective GIC and Q, per line the distances, source voltage, ohms and current, and the sweep); an existing file is never overwritten. |

At least one of `--field` and `--direction-sweep` is required. Transformer currents are per phase; line and neutral
currents are all three phases. The JSON from `--out` is the result file: the verb is not on the shared `--json`/`--csv`
writer.

Exit 0 when solved. Exit 1 for a case or gic file that cannot be read or does not fit the case. Exit 2 for usage.

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
| `--events` | - | N351, `--rms` only: one scheduled RMS event study. A comma list of `kind:arguments@seconds` items: `fault:BUS@t` (bolted fault), `clear:BUS@t` (its clearance), `trip:K@t` (branch K trips, 0-based), `reclose:K@t` (it closes again: auto-reclose), `load:BUS:P:Q@t` (a load step of P MW and Q Mvar, negative to shed), `gentrip:G@t` (generator G trips, 0-based), `sfault:TYPE:BUS@t` or `sfault:TYPE:BUS:R:X@t` (a `3ph`, `slg`, `ll` or `llg` fault at the bus, bolted or through R + jX pu; cleared by `clear:BUS@t`), `lfault:TYPE:K:D@t` or `lfault:TYPE:K:D:R:X@t` (the same fault a fraction D along line K) and `lclear:K@t` (its clearance). For example `--events "fault:7@0.1,clear:7@0.183,trip:5@0.183,reclose:5@0.683"`. With a sequence fault the run also prints the fault point (sequence and phase currents, phase voltages, Z2, Z0) and `--events-out` adds `rms-fault-points.csv`. Prints the events and a STABLE / UNSTABLE verdict over the connected machines. The run lasts 3 s, or 3 s past the last event. It owns the whole disturbance: with `--screen`, `--fault-bus`, `--clear` or `--clear-by-trip` it exits 2; a malformed list exits 2 naming the item; an event the study refuses (a bus not in the case, a reclose of a branch that is not tripped, a time after the end) exits 2 with the reason. |
| `--events-out` | - | N363, with `--rms --events` only (otherwise exit 2): writes the event study's export to the folder (created if missing): `rms-events-machines.csv` (every machine trace at every integration step: `t_s,trace,bus,delta_rad,speed_pu,eqp_pu,edp_pu,eqpp_pu,eddp_pu`, each number in its shortest round-trip form), `rms-events-converters.csv` when the fleet has converter plant (`t_s,converter,name,bus,v_pu,p_pu,q_pu,ip_pu,iq_pu,in_dip_mode`), `rms-devices.csv` when the run has SVC, STATCOM or HVDC models (`t_s,device,name,model,state,value`), `rms-events.json` (schema `gridsim-rms-events-export/1`: case, nominal frequency, step, duration, the events in time order, the verdict, converter-iteration failures, the machine list with island references marked, provenance `Synthetic` and whether the result is INDICATIVE) and `run-metadata.json`. Both CSVs start with the run header and a `# provenance:` line (`Synthetic`, a model trajectory, not a record; INDICATIVE when the GENROU data is synthesised). A file that cannot be written exits 1. The run and its verdict are unchanged. |
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

### motor-start

`gridsim motor-start <study.json> [--out <result.json>]`

Motor acceleration study from a native JSON operating point (RC3 P8). It starts one placed induction motor of a
gridsim-case/2 case (a `motors` row of its network file: see the v2 schema guide), direct-on-line or through a
reduced-voltage starter, and integrates its acceleration with the network solved at every step. A group study starts
several motors, each at its own time and by its own method, on one network. It never calls the steady-state induction machine solve: the slip
is derived from rotor speed (1 at standstill) and the single-cage equivalent circuit is evaluated at that slip. The
existing power flow, the steady-state induction machine model and every other verb are unchanged.

The study file is strict JSON, schema version 1: `schemaVersion` (1); `case` (`path` of a gridsim-case/2 manifest,
relative to the study file, and `sha256`, the digest of the consumed-file manifest exactly as `transmission-security`
computes it for a native case: one `path<TAB>sha256` line per consumed file, the manifest, its network, asset and library
files and the v2 schema set; a mismatch is refused and prints the resolved identity); `motor` (the motor row's name);
`initial` (`speedPu`, at least 0 and below the running speed, and `connected`, which must be false: the start event
connects the motor, and a motor already connected before it is refused); `start` (`atSeconds`, `method`: `directOnLine`,
`starDelta`, `autotransformer`, `reactor`, `softStarter` or `vfd`, and for every method but `directOnLine` a `starter`
object, below); `integration.stepSeconds`; `termination` (`runningSpeedPu`, `horizonSeconds`,
`stallDwellSeconds`, `stallAcceleratingTorquePu`); `preStart` (`pgToleranceMw`, `vmTolerancePu`, `maxSlackMovementMw`);
optional `report` (`sampleEverySteps`, default 1, and `maxVoltageDipPercent`, a reported flag). The start time and the
horizon must be whole numbers of steps. Unknown or duplicated fields, non-finite numbers, a v1 case folder (it has no
motor rows) and an unknown motor are refused before any solve.

The `starter` object takes only the fields of its method; a field of another method is refused by name. Every time in
it must be a whole number of steps. For `starDelta`, `autotransformer` and `reactor`, an `initial.speedPu` at or above
the switch speed is refused: the starter would switch at once and the start would be direct-on-line.

| Method | Fields | What the motor sees |
|---|---|---|
| `starDelta` | `switchSpeedPu` (default 0.8), `switchTimeSeconds` (optional), `deadTimeSeconds` (default 0) | V/sqrt(3) on each winding until the switch to delta, at the switch speed or time, whichever comes first. The line current and the torque are a third of direct-on-line. The transition is open: during the dead time the motor is disconnected and coasts under its load. A connection in delta below the breakdown (pull-out torque) speed is noted as "transition current peak"; with a dead time the note is on the reconnection in delta, where the peak is. |
| `autotransformer` | `tapRatio` k (0 to 1), `switchSpeedPu`, `switchTimeSeconds` | k V. Motor current k times direct-on-line, line current and torque k squared. Ideal transformer, magnetising current ignored; the switch to full voltage is a step (the Korndorfer closed transition approximated as instantaneous). |
| `reactor` | `reactorRPu` and `reactorXPu` (pu on the motor base), or `reactorROhm` and `reactorXOhm` (ohms are taken only for a reactor; on another method they are refused by name); `switchSpeedPu`, `switchTimeSeconds` | A series impedance: I = V / \|Z_motor + Z_r\|, bypassed at the switch. |
| `softStarter` | `initialVoltageFraction` V0, `rampSeconds`, `currentLimitPu` (optional, pu of rated current) | A voltage ramping linearly from V0 to 1 over the ramp, then bypassed. With a current limit the fraction is lowered so the line current stays at or below the limit (settled to 1e-9 relative). It is a series controller: the line current is the motor current, the active power is the motor's, and the rest of the apparent power is reactive. Fundamental frequency only. |
| `vfd` | `initialFrequencyHz` f0, `ratedFrequencyHz` fn, `rampSeconds`, `driveEfficiency` (default 0.97), `currentLimitPu` (optional) | Plain constant V/f: V = f/fn of rated voltage, with no stator-resistance (IR) compensation, so the torque at low frequency is lower than a drive with boost would give; the frequency ramps from f0 to fn; the circuit's reactances scale with f/fn and the slip is taken against the supply frequency. The line side draws the motor's input power over the efficiency at unity power factor, a constant-power load at the bus (refused on a bus whose demand varies with voltage). The ramp pauses while the motor current is above the limit. A start with `initial.speedPu` above f0/fn is refused: the slip would be negative and the drive would regenerate (a flying restart), which is not modelled. |

Starter inputs are held over each step, and the predictor solve of Heun's method uses the same stage. With a starter the
sample's current, P and Q are what the bus supplies, and its terminal voltage is the motor's own; `starterSamples` (one
per sample: stage, voltage factor, drive frequency, motor voltage, motor current, line current and motor input power) and
`starterTransitions` (each stage change with the line current just before and just after, at the same speed) are added
to the result. A run without a starter writes neither. `RUNNING` needs the starter in its final stage (switched,
bypassed, or the drive at rated frequency); a motor already above the running speed then is `RUNNING` at that step. The
stall dwell is counted in whole steps and does not run while the motor is disconnected.

A group study replaces `motor`, `initial` and `start` with `group`: an array of objects, each with its own `motor`,
`initial` and `start` (method and starter included). The network is solved each step with every connected motor. The
result has one entry per motor in `motors` (status, acceleration and decision times, starting, peak and final states,
samples, starter samples and transitions) and the deepest dip at any bus over the whole run in `voltageDip`. The group
status is `RUNNING` only when every motor runs; otherwise `NO SOLUTION` if a step failed, `STALLED` if any motor
stalled, else `HORIZON ENDED`. The exit code follows the group status. A motor whose outcome is decided stays on the
network while the others run, and a stage change its starter still makes then (a timed switch after a stall, say) is
recorded in its transitions with a note. Results are deterministic for a given member order; with three or more motors
on one bus the order of the list fixes the order in which their loads are summed, so reordering the list can change
results at rounding level.

The starter models are fundamental-frequency and quasi-steady, as the direct-on-line study: no switching transients, no
closed-transition detail, no drive harmonics, single-cage motors only. They are verified against the closed forms above
on a stiff bus; they have not been compared with ETAP or PowerFactory motor-start runs.

The case is loaded with loader balancing suppressed at the loader seam, whatever its `system.dispatch` says, and the
authored generator schedule is read from the network files: every in-service generator must give `outputMw`, `pMinMw`
and `pMaxMw`. The result keeps three states apart: the authored schedule against the resolved model (a moved unit
stops the study), and the realised pre-start AC solution with the motor disconnected (incumbent Newton, 100 iterations,
reactive limits enforced, no OLTC loop): each non-slack generating bus at its schedule within `pgToleranceMw`, each
voltage-controlled bus at its setpoint within `vmTolerancePu` (a bus released at a reactive limit fails), each
generating bus within its summed unit bounds and the slack movement within `maxSlackMovementMw`. A failed check is
`INCOMPLETE` and no event is run.

From the event, at each step: the motor is the constant-impedance load its circuit is at the step's slip, the network is
solved once and verified (a step with no converged solution is `NO SOLUTION` with the solver's exact status), and the
swing equation 2 H dw/dt = Te - Tl is advanced by Heun's method (a predictor network solve, then the averaged
accelerating torque). Torques are per unit of the motor torque base (rated MVA over synchronous mechanical speed);
speed is per unit of synchronous speed. Every solve starts from the authored bus voltages.

Outcomes (`status`): `RUNNING` (the speed reached `runningSpeedPu`; the acceleration time is the interpolated crossing
less the start time), `STALLED` (the accelerating torque stayed at or below `stallAcceleratingTorquePu` for
`stallDwellSeconds` below the running speed: locked, decelerating or crawling), `NO SOLUTION`, `HORIZON ENDED` (no
decision by the horizon) and `INCOMPLETE` (pre-start point not reproduced). The result JSON (`schemaVersion`,
`provenance` with the study and case digests, every consumed input and the engine identity, and `result`: the status,
outcome and termination criterion, the motor row, the request, the load policy, the declared method, the generator
states, the pre-start verification, the acceleration and decision times, the starting, peak-current and final states,
the voltage dip at the motor bus and the deepest at any bus, the number of network solves, the sampled trajectory with
current in pu and kA, P and Q, speed, slip and torques, and the reasons) is printed to stdout and, with `--out`, written
to a new file via a temporary file and a final move. An existing `--out` path is refused.

Exit 0 `RUNNING`. Exit 1 for input it cannot accept (a malformed study, a case identity mismatch, an unreadable or
invalid case, a generator without an explicit schedule, an existing output path). Exit 2 for an invalid invocation.
Exit 3 `STALLED`, `NO SOLUTION`, `HORIZON ENDED` or `INCOMPLETE`.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |

### user-model

`gridsim user-model list`

`gridsim user-model check <model.json | LIBRARY-ID>`

`gridsim user-model check-plugin <plugins.json>`

Declarative dynamic models (RC4 R4-08, Core `GridSim.Core.Dynamics.UserModels`). A user-defined controller is a
`gridsim-model/1` JSON document describing a block diagram, not code: ports (inputs and outputs), parameters with units and
bounds, blocks from a closed catalogue, wires, and an initialisation block. It is interpreted by the same step operators
the coded controllers use (`ControlBlocks.FirstOrderLag`, `LeadLag`, `Washout`, `Integrator`, `PiController`), inside the
same integration machinery, so a declarative model is inside GridSim's bit-identity guarantee: a model that re-expresses a
coded one (TGOV1, SEXS, PSS1A, GAST, IEEET1, IEEEG1 without valve rate limits, SEXS with PSS1A) gives the coded model's
output bit for bit (every sample of 10^5 steps at three step sizes, and whole studies on IEEE 9 and the 14-bus dynamics
fixture). Vendor binaries (FMU, DLL) are not part of this format; GridSim's own .NET plug-in interface is a separate route,
described under "Model plug-ins" below.

`list` prints the shipped library, one model per line: id, role, document SHA-256 and title. `check` reads a document (a
file, or a shipped id) and validates it against the closed vocabulary, compiles it, and prints what the model coverage
report records for it: the id, role, source citation, the document SHA-256 (the hash of the exact bytes, so any edit or
block reordering changes it), the compiled-order digest, the coverage status `SUPPORTED (USER MODEL, sha256:<hash>)`, the
ports, the parameters with units and bounds, and the blocks in evaluation order. Exit 0 valid; exit 1 the file or id does
not exist or the document is refused (every refusal is printed with its code and the object it names, for example
`[algebraic-loop]`, `[unknown-block]`, `[unit-mismatch]`, `[undeclared-port]`, `[out-of-bounds]`); exit 2 usage (a missing
argument, an unknown subcommand, any flag: the verb takes none).

`check-plugin` loads a `gridsim-plugins/1` configuration under every loading rule below, binds each listed model at its
declared defaults (which runs the replay check) and prints, per model, the id, role, assembly, assembly SHA-256, the status
`SUPPORTED (PLUGIN, sha256:<hash>, OUTSIDE THE VERIFIED CONTRACT)` and the parameters. Exit 0 every model passed; exit 1 a
refusal, printed with the entry it names.

The format. A document has `format` (`gridsim-model/1`), `id`, `role` (`exciter`, `governor`, `stabiliser` or `generic`),
`source` (the public source of the structure, required; the roles `injection`, `load` and `relay` are described below), `parameters` (name, `unit` of `pu`, `s` or `1/s`, `value`, optional
`min`, `max`, `minExclusive`, `maxExclusive`, `allowInfinite`), optional `derived` constants (`add`, `sub`, `mul`, `div` of
parameters, evaluated in listed order so a coded constructor's arithmetic can be reproduced) and `constraints` (a parameter
sum), `ports` (`inputs`; `outputs` each bound with `from`), `blocks` and `initialisation`. A model role fixes its ports: an
exciter takes `verr` and `speed` and gives `efd` (initialised from `efd0`, and it names the initialisation value that is its
steady regulator error); a governor takes `speed` and gives `pm` (from `pm0`); a stabiliser takes `u` and gives `vs` (from
`u0`). Unknown or duplicate members are refused with their JSON path.

The block catalogue is closed: `gain`, `div`, `sum` (with a `signs` text), `product`, `lag`, `leadlag`, `washout`,
`integrator` (windup-limited when given limits), `pi`, `limit`, `ratelimit`, `deadband`, `table1d`, `expsat`, `min`, `max`,
`switch`, `const`, `hold`, `delay` (whole steps), `satpole` (the IEEE type 1 saturated exciter pole), `expr` (below), and
`ontimer` and `latch` (below, with the relay role). A block outside it is refused naming the block; it is never approximated. A wire is a port name or a block id; `block.prev` reads a stateful
block's output as it stood at the start of the step, and is the only way to close a loop: an algebraic loop without it is
refused naming the blocks. A `washout` with a second input updates its state from that value after the step's outputs are
evaluated (the coded `OutputFor` then `Step` split used by the rate-feedback loops). Units are checked: time constants are
`s`, an integrator output is the input times `s`, the operands of a sum, min, max or switch must agree, and a limit must be in
the unit of its signal.

Evaluation order is part of the contract: parse, validate vocabulary, units and ports, compile (blocks are taken in document
order among those whose inputs are ready, so the compiled order is a function of the document), initialise from the solved
operating point (the initialisation steps run in their listed order, with the equilibrium-within-limits checks the coded
models make), then per step: write inputs, snapshot the `.prev` values, evaluate the compiled operation list, apply deferred
washout updates, read outputs. There is no scripting language, no user code, no reflection and no file access at run time.

Core use: `UserModelLibrary.Load(id, parameters)` or `UserModel.FromDocument(...)`, then
`UserModelGovernorData` (the `SubtransientInit.MachineData.Governor` of a machine), `RmsExciter.FromUser(new
UserModelFieldController(model), ...)` (the RMS studies' exciter list), or `DynamicsStudyRequest.UserModels` (a list of
`UserModelAssignment`: one governor or exciter model per machine, by bus and id, in place of the DYR record's controller).
The study's model coverage report then lists the record as `SUPPORTED (USER MODEL, sha256:<hash>)` and its notes carry the
model id, hash, order digest and bound parameter values. The dynamics-study JSON request (`schemaVersion` 1) is unchanged:
binding user models from a request file is not offered by this verb; the sidecar `--model-assignments` file is (see
[`dynamics-study`](#dynamics-study)). Equivalence with PowerFactory DSL models is not claimed (COMPARATOR-UNVERIFIED): the
claim is bit identity with GridSim's own coded models.

**The `expr` block (RC5).** `{ "id": "cmd", "type": "expr", "inputs": ["pref", "speed"], "expr": "in0 - in1 / @R", "unit": "pu" }`.
The text is numbers (no exponent), the block's inputs `in0` to `in7`, declared parameters `@Name`, `+ - * /` (left to right,
`*` and `/` before `+` and `-`), unary minus, brackets, and the functions `min` and `max` (two or more arguments, taken left to
right), `abs`, `sqrt`, `exp` (the deterministic `DetMath.Exp`) and `clamp(x, lo, hi)`. It is compiled once into a fixed
evaluation order that is the text read left to right, so `in0 - in1 / @R` does exactly the coded TGOV1's `Pref - dw / R`.
Nesting and the evaluation stack are capped at 64. The output unit is declared with `unit` and not inferred. A non-finite
result (a division by zero, the square root of a negative number) refuses the step with the code `non-finite`, naming the
block. An `expr` TGOV1 gives the coded TGOV1's output bit for bit (tested).

**Injection and load roles (RC5).** An `injection` model takes `vm` (the terminal |V|) and gives `ip` and `iq`, the active
and reactive current exporting positive on the unit's MBASE; the current is `(ip - j iq) v / |v|` times MBASE / Sb. A `load`
model takes `vm` and gives `p` and `q`, the drawn active and reactive power as multiples of the load's P and Q; the current
drawn is `conj(S / v)`. Both are initialised from `p0`, `q0` (on the MBASE for an injection, system per unit for a load) and
`v0`. Inside the network iteration the model is evaluated with every state held (`ModelInstance.EvaluateHeld`), so its
current is a pure function of the voltage; the states advance once per step. A load model's Norton admittance is the one the
constant-admittance bus load would have had, so `p = q = (vm / v0)^2` leaves the network exactly as the static load did
(tested to 1e-12 pu through a fault). They reach the RMS study through the API: `SubtransientInit.MachineData.UserInjection`
(a `UserDeviceModel`) on a generator, and `UserDynamicLoadData` in `RmsLoadedMachineData.Loads` for a bus load (which may be
purely reactive). At its start each must reproduce the power-flow P + jQ to 1e-10 (1 + |S|) pu, or it is refused.

**Model plug-ins (RC5).** A plug-in is a .NET class library that references `GridSim.Core`, carries
`[assembly: GridSimModelPlugin(1)]`, and holds one public class per model implementing `IUserModelFactory`: an `Id` (1 to 16
of A-Z, 0-9 and underscore), a `Role` (exciter, governor, stabiliser, injection or load), the `Parameters` it takes (name,
unit, default, optional bounds) and `Create(parameters)`, which returns a fresh instance of the role interface:
`IUserExciter` (`Initialize(efd0)`, `EquilibriumErrorPu(efd0)` without touching the states, `Step(verr, speedDev, dt)`),
`IUserGovernor` (`Initialize(pm0)`, `Step(speedDev, dt)`), `IUserStabiliser`, `IUserInjection` or `IUserLoad` (a constant
Norton admittance, `Initialize` at the power-flow start, `Current(v)` with the states held, `Commit(v)`, `Step(t, v, dt)`).
Quantities are per unit: the machine base for the control roles, the system base for currents; phasors are (re, im) pairs.

Contract 2 (`[assembly: GridSimModelPlugin(2)]`) keeps every contract 1 interface and adds two roles. `machine`:
`IUserMachine`, a Norton source with a constant admittance on the system base (`NortonAdmittancePu`), `Initialise` at the
power-flow P0 + jQ0, V0 and MBASE, `NortonCurrentPu` from the present states, `Step(dt, inputs)` from the step's terminal
voltage, and the speed, rotor angle, field current and electrical power it reports. It runs on the injection route, keeps its
own rotor, field and mechanical power, and is not judged in the synchronism verdict. `relay`: `IUserRelay`, `Initialise()`
and `Evaluate(inputs)` once per step (step number and size, time, unit bus |V|, the machine's electrical frequency and its
rate of change, P, Q, speed and |I|), returning no action or a trip with a reason code; it trips its unit by the unit relays'
path. Bound to a branch instead, it reads |V| at one end and the |I|, P and Q flowing into the branch there (frequency, its
rate and speed are NaN: a branch has no machine), and its trip opens the branch from the next step.
A configuration entry may add `"contract": 1` or `2`, which must match the assembly. A contract 1 assembly cannot give the
machine or relay role: a role number outside the five contract 1 roles is refused when the model is bound, with the words
contract 1 always used, and an assembly that declares a contract other than 1 or 2 is refused with "this build implements
contract 1", as before. The sample `tests/GridSim.ModelPlugin.Contract2Sample` writes a classical machine and two
under-voltage relays.

**Relay role (RC5).** A `relay` document takes, in this order, `vm` (pu), `f` (Hz, declared `1`), `p`, `q` (system pu), `dfdt`
(Hz/s, declared `1/s`), `speed` (pu) and `i` (system pu), the fields a relay plug-in reads, and gives `trip`: 0 for no
action, otherwise the reason code (a whole number). It has no initialisation inputs; its stateful blocks are initialised with
literals. `ontimer` (parameter `time`, seconds) gives 1 once its input has been non-zero for `time`, counted in whole steps by
the built-in relay stages' rule (operation at the first step k with `(k - k_pickup) h + h 1e-6 >= time`, a zero step resets
it, one fixed step size, initialised with 0); `latch` gives 1 from the first step its input is non-zero to the end of the run
(initialised with 0 or 1). A relay document runs through `UserRelayBinding.ForUnit` or `ForBranch` with
`RmsTransientStability.StudyEventsWithUserRelays`, or as a `deviceModels` entry of role `relay` in a dynamics study (see
[`dynamics-study`](#dynamics-study)); bound to a branch it may not read `f`, `dfdt` or `speed`. Its records carry its id and
document hash, without the OUTSIDE THE VERIFIED CONTRACT label.

How to write one:

1. Make a class library for the same .NET version as GridSim, reference `GridSim.Core` (do not copy it beside the plug-in:
   the host's copy is the one the plug-in binds to), and add `[assembly: GridSimModelPlugin(1)]`.
2. Write the model as a class that keeps all its state in its fields, on the calling thread, using the public
   `ControlBlocks` operators (`FirstOrderLag`, `LeadLag`, `Washout`, `Integrator`, `PiController`) where they fit and
   `DetMath` for any transcendental function. The sample `tests/GridSim.ModelPlugin.Sample` writes TGOV1 and SEXS this way.
3. Write a factory class with a public parameterless constructor that returns a new instance from `Create`.
4. Build it, put the DLL under a folder you control, and write the configuration:
   `{ "schema": "gridsim-plugins/1", "root": "plugins", "assemblies": [ { "path": "MyModels.dll", "sha256": "<hex>", "models": ["MY_GOV"] } ] }`.
5. Run `gridsim user-model check-plugin plugins.json`, then use the model through `dynamics-study --model-plugins
   plugins.json --model-assignments assignments.json`.

The contract a plug-in must keep: no threads, tasks, async, timers or locks; no clock; no randomness and no `HashCode`; no
enumeration of `Dictionary`, `HashSet`, concurrent collections or `Hashtable` (use arrays, `List<T>` or `SortedDictionary`);
no file, network, console, process or environment access, no P/Invoke, no unsafe code, no reflection emit; no mutable
static state (an instance holds all its state, and instances share nothing); `DetMath` for transcendental functions and, of
`System.Math`, only Abs, Min, Max, Sqrt, Floor, Ceiling, Round (to even), Sign, Clamp and CopySign; no
`Math.FusedMultiplyAdd`, vectors, intrinsics, `float` or `decimal`; and `Current(v)` pure. What GridSim enforces: a lint of
the assembly's metadata before it is loaded (every member, type and signature it references, and single-precision opcodes),
which refuses a breach naming the member and the type that references it, and a replay check when a model is bound, which
refuses it as "not deterministic (replay differs at step k)". Every number a plug-in returns is checked finite; a
non-finite one refuses the step, naming the model and the time.

The lint refuses, by name, wherever the plug-in's IL, signatures or metadata reference them:

- GridSim.Core outside the namespaces `GridSim.Core.Dynamics.Models`, `GridSim.Core.Dynamics.Plugins` and
  `GridSim.Core.Numerics` (so no `Save(path)` or other file API of Core, and no Core table), and in those the loader's own
  types (`PluginConfig`, `PluginLoader`, `PluginSet`, `PluginVerifier`, `PluginReplay`, `PluginDenylist`) and the SIMD
  capability readings (`SimdCapabilities`, `SimdTier`).
- Writing a static field of another assembly, taking its address, or reading one that holds anything but a number, bool,
  char or string (an array or object there could be written through). A plug-in's own statics are its own business; the
  replay checks their effect.
- Members of `System.Double` other than the exact ones (`IsNaN`, `IsFinite` and the other classifications, comparisons,
  `Abs`, `Min`, `Max`, `Clamp`, `CopySign`, `Sign`, `Sqrt`, `Floor`, `Ceiling`, `Round`, `Truncate`, `BitIncrement`,
  `BitDecrement`, `GetHashCode`): so `double.Sin`, `double.Exp`, `double.FusedMultiplyAdd` and the rest of its maths, and
  parsing and formatting. `System.Half`. `Parse` and `TryParse` on the integer types, and `System.Convert`.
- `GetHashCode` on any type but the numbers, bool, char and enums (string, comparer, tuple and object hash codes are
  randomised per process or follow object identity).
- `IEnumerable`, `IEnumerable<T>`, `ICollection`, `ICollection<T>`, the dictionary and set interfaces, LINQ
  (`System.Linq`), `Dictionary` and `HashSet` enumeration (`GetEnumerator`, `Keys`, `Values`, their enumerator and key and
  value collection types, `HashSet.CopyTo`), `ImmutableHashSet`, `ImmutableDictionary`, the frozen, concurrent and
  specialised collections, and the JSON, XML and serialisation namespaces. Iterate arrays, `List<T>` and
  `SortedDictionary` directly (an `IReadOnlyList<T>` by index).
- Environment readings: `OperatingSystem`, the current culture (`CultureInfo.CurrentCulture` and its relatives,
  `NumberFormatInfo.CurrentInfo`, `RegionInfo.CurrentRegion`), `TimeZoneInfo`, `JitInfo`, `GCSettings`, `RuntimeFeature`,
  weak references.
- Reflection: `System.Type`, the `System.Reflection` namespace (its attribute types excepted: the SDK writes them on every
  assembly), expression trees (`System.Linq.Expressions`), `Delegate.DynamicInvoke`, `UnsafeAccessor`, an
  `IgnoresAccessChecksTo` attribute (defined or referenced), and any method without a body of its own (extern).
- Every framework namespace but the few a numerical model needs. A framework type (`System.*`, `Microsoft.*`) is allowed
  only in `System`, `System.Collections.Generic`, `System.Collections.Immutable`, `System.Collections.ObjectModel`,
  `System.Diagnostics.CodeAnalysis`, `System.Globalization`, `System.Runtime.CompilerServices`, `System.Runtime.Versioning`
  and `System.Text`, and the attribute types of `System.ComponentModel`, `System.Diagnostics` and `System.Reflection` (with
  the enums `EditorBrowsableState` and `DebuggerBrowsableState` they take);
  the lists above still apply inside them. So a file or network route in any other namespace is refused without being
  named: `System.Formats.Tar`, `System.Data` (`DataSet.WriteXml` and `ReadXml`), `System.Resources`,
  `TextWriterTraceListener`, `Trace` and `FileVersionInfo` in `System.Diagnostics`, and `System.Buffers` (`ArrayPool`, state
  shared with the host). A plug-in references only `GridSim.Core` and the framework assemblies that hold those namespaces
  (`System.Runtime`, `System.Collections`, `System.Collections.Immutable`, `System.Memory`, `System.ObjectModel`,
  `System.Runtime.Extensions`, `System.Private.CoreLib`, `System`, `mscorlib`, `netstandard`).

A plug-in is trusted code. The lint and the replay check catch accidents against the determinism contract; .NET has no
in-process sandbox, so load only plug-ins you would run as a program of your own. `breaking-change validate` never re-runs
a plug-in job, and `rpc --listen` never loads one.

What the lint does not see: it checks names, not data flow. Text formatted with the current culture without naming it
(string interpolation, `ToString()` without a provider, culture-sensitive string comparison) is not refused, nor a
framework method that takes `IReadOnlyCollection<T>` or `object` and enumerates a dictionary or set handed to it. A
record type is refused (its generated members use `System.Type` and `EqualityComparer` hash codes).

The replay check runs each model 200 steps from two operating points of its role (a 0.01 pu input step at step 100): one
instance alone from the first point, one alone from the second, then two fresh instances side by side, one from each
point, initialised one after the other and stepped in turn. Each paired instance must repeat its solo run bit for bit, and
an injection's current asked twice at the same voltage must give the same bits. A relay is replayed twice, with a unit's
inputs and with a branch's (no frequency, rate of change or speed), since it may be bound to either. Because the paired instances start at
different points, state shared through a static (a value one instance writes and the other reads, or a shared counter)
shows as a difference. It cannot prove determinism: sharing that changes no output in those 200 steps from those two
points passes.

Loading rules. A plug-in is loaded only from a `gridsim-plugins/1` configuration named on the command line: no default
folder, no environment variable, no discovery. Each path must resolve inside the configuration's `root` (default: the
configuration's folder): no `..` escape, no absolute path outside, no network or device path, no symbolic link or junction
on any component (the scripting `--confine` rule). The SHA-256 of the file must equal `sha256`; the bytes are read once and
the same bytes are checked and loaded. The assembly must carry the attribute and reference only the framework and
GridSim.Core, and may not take the name of an assembly GridSim grants its internals to (an `InternalsVisibleTo` target of a
GridSim assembly, such as GridSim.Tests; the names are read from the attributes, ignoring case). The listed ids must be unique and differ from every shipped model. Every rule is checked for every entry before
anything is loaded; the assemblies then load into one collectible context per configuration. `rpc` refuses the plug-in routes
over `--listen`, and the web app does not offer them.

What this is not. The lint and the replay are a determinism check, not a security boundary: .NET has no in-process sandbox,
so a plug-in runs with your rights. Load only assemblies you trust; the hash pin protects against a swapped file, not against
its author. Results with plug-ins are outside the verified contract: GridSim verifies the route (a plug-in that re-expresses
TGOV1 or SEXS gives the coded output bit for bit), not the plug-in's maths. Out of scope: the PSS/E USRMDL/CONEC/CONET
compiled-model interface, PowerFactory DSL import, the IEEE/Cigre real-code DLL interface for EMT, user machine (rotor and
flux) models and relay models. A plug-in stabiliser is attached through the API (`RmsExciter.WithLibraryStabiliser(new
PluginStabiliser(model))`); a study binds governor and exciter plug-ins. Injection and load plug-ins are reached through the
API (as for the declarative roles above), not yet from the `dynamics-study` file. `modal-full` takes declarative models
only, and a model with an `expr` block is reported NOT LINEARISED there (no linearisation is defined for it).

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

### modal-full

`gridsim modal-full <case|case.m|case-dir> --models <dir> [--region re,im,re,im] [--count N] [--method auto|dense|arnoldi] [--dense-limit N] [--residual-gate x]`

Full-model small-signal (modal) analysis (RC4 R4-12; `GridSim.Core.Dynamics.SmallSignal.Full`). Where `modal` linearises the
classical swing model, `modal-full` adds the machine field flux and the controllers: each machine is either classical (delta, omega)
or the one-axis flux-decay machine of Heffron and Phillips (delta, omega, E'q, with saliency Xq different from X'd and armature
resistance neglected), the network algebra is eliminated by the same Kron reduction `modal` uses (the internal node is placed behind
Xq, and the loop `E_Q = E'q + (Xq - X'd) id` between the internal voltage and the d axis current is solved in closed form), and the
exciter, governor and stabiliser declarative models (`gridsim-model/1`, [`user-model`](#user-model)) are linearised block by block
about the initial state with analytic Jacobians (no finite differences). The classical `modal` verb and every RMS integrator are
unchanged. With no flux machine and no controller the matrix is `modal`'s own, bit for bit.

`--models` names a directory holding `machines.json` (`gridsim-modal-machines/1`) and any `<id>.model.json` documents it names (a
name not found there is looked up in the shipped library). Nothing is defaulted: `{"format": "gridsim-modal-machines/1", "machines":
[{"bus": 2, "unit": 0, "model": "flux-decay", "xdp": 0.32, "xd": 1.6, "xq": 1.55, "tdop": 6.0, "damping": 2.0, "exciter": {"model":
"SEXS", "parameters": {"K": 50}}, "governor": {"model": "TGOV1"}, "stabiliser": {"model": "PSS1A"}, "coded": ["Esst4b"]}]}`.
Reactances are per unit on the machine base; damping is on the system base; `infiniteBus: true` makes a unit an ideal voltage source
(a single-machine study). A machine the file does not name is classical with X'd = 0.30 pu, said in the notes. The exciter reads
`Vref - Vt + Vs` and the speed deviation, the stabiliser (which needs an exciter) reads the speed deviation, the governor reads the
speed deviation and its output is scaled from the machine base to the system base. Loads are constant impedance as in `modal`;
magnetic saturation of the machine, sub-transient dynamics and converter plant are not in this model.

What is linearised and what is not. A controller that the block catalogue cannot express (a `coded` name such as AcExciter, Esst4b or
IEEEG1 with rate limits) or that cannot be linearised (a fixed delay has no finite-order state space; a state that is not at
equilibrium at the operating inputs) is reported under NOT LINEARISED with the reason and held at its operating output; it is never
approximated, and the result is INCOMPLETE. A limit that is active at the operating point freezes the limited state and a rate limiter
is taken as inactive; both are printed in the notes.

How it solves. The order is: base case solve, initialise the models, linearise the blocks in compiled order, assemble A, then the
dense `GeneralEigen` up to `--dense-limit` states (default 200) and a deterministic shift-invert implicitly restarted Arnoldi above it,
a residual check `||A v - lambda v|| / ||A||_1` of every returned pair against `--residual-gate`, participation of every state (machine,
exciter, governor, stabiliser) and the report. The Arnoldi start vector is drawn from the seeded R4-02 stream (seed and domain are
recorded in the result), the restart rule is fixed, and the result is bit-identical across runs and thread counts. Above the dense limit
only the eigenvalues nearest the centre of `--region` are computed (the electromechanical band when no region is given): the output says
how many were found, within what distance of the centre the list is complete, and whether that covers the region; raise `--count` if it
does not. A pair that fails the residual gate, an Arnoldi run that does not converge, or a controller that is not linearised makes the
result INCOMPLETE and the reasons are printed. PowerFactory modal analysis comparison is COMPARATOR-UNVERIFIED.

| Flag | Default | Meaning |
|---|---|---|
| `<case>` | required | [Case argument](#case-arguments); the base case is solved first. |
| `--models` | required | Directory with `machines.json` (`gridsim-modal-machines/1`) and optional `<id>.model.json` declarative models. |
| `--region` | all (dense), electromechanical band (Arnoldi) | Two opposite corners of a rectangle of the s-plane, `re,im,re,im` (1/s, rad/s). The Arnoldi search is centred on it and only eigenvalues inside it are listed. |
| `--count` | 20 | Eigenvalues the Arnoldi path computes (the nearest the centre). |
| `--method` | auto | `auto` (dense up to `--dense-limit`, Arnoldi above), `dense` or `arnoldi`. |
| `--dense-limit` | 200 | Largest state order handled by the dense solver in `auto`. |
| `--residual-gate` | 1e-9 | Relative residual every returned eigenpair must meet. |

Exit 0 COMPLETE with no right-half-plane eigenvalue listed; 1 the case, the bindings or a model cannot be loaded or are invalid, or the
base solve did not converge; 2 usage; 3 INCOMPLETE or at least one right-half-plane eigenvalue listed. Result files: `--json <file.json>`,
`--csv <file.csv>` or `--out <file.json|file.csv>` write the result (tables `modes`, `participation`, `models`, `not_linearised`;
[Result files](#result-files)).

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

`gridsim impedance-stability [<case>] [--dir <path>] [--bus <id>] [--converter gfl|gfm] [--criterion auto|siso|mimo] [--grid canonical|real] [flags]`

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
| `--criterion` | `auto` | `auto` (MIMO for `gfl`, SISO for `gfm` and for `--outer-loop-only`), `siso` or `mimo` (the generalised criterion); any other value exits 2, and `mimo` with `--outer-loop-only` exits 2. The grid-following admittance couples the two sequences, which SISO drops, so its SISO verdict can be wrong; the grid-forming admittance does not, so SISO is exact there and keeps the margins. With the ideal PLL of `--outer-loop-only` the dq loop is improper and its MIMO winding misses a right-half-plane root. |
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

### emt-line

`gridsim emt-line <study.json> [--out <result.json>]`

Lossy travelling-wave line impulse study (RC3 P8). One constant-parameter line between a Thevenin source and a
termination is run on the same EMT engine as `emt`, unchanged: the line is a cascade of Bergeron sections with the
EMTP lumped-resistance split (per section `R/4`, a lossless half-section, `R/2`, a lossless half-section, `R/4`, and
`G/4` to ground at each half-section terminal), built from the engine's own lossless line, resistor and conductance
elements. The `emt` verb and its lossless travelling-wave line model are not changed. Frequency-dependent and
multi-phase line models are not implemented, and nothing has been compared with PSCAD or EMTP (the result says
`comparator: unverified`).

The study file is strict JSON, schema version 1: `schemaVersion` (1); `label`; `line` (`lengthM`, `rOhmPerM`,
`lHenryPerM`, `cFaradPerM`, `gSiemensPerM`, SI units per metre); `segments` (sections for the base run); `source`
(`kind` `Step` or `DoubleExponential`, `amplitudeV`, `matched` true for a source resistance equal to Zc or false with
`resistanceOhm`, and `alphaPerS`, `betaPerS` for the double exponential `A (exp(-alpha t) - exp(-beta t))`);
`termination` (`kind` `Open`, `Short`, `Resistance` or `Capacitance`, with `value` in ohm or farad for the last two; a
short is a 1e-6 ohm resistance); `integration` (`dtSeconds`, `durationSeconds`, a whole number of steps);
`refinement` (`timestepDivisors`, starting at 1 and increasing, and `segmentCounts`, increasing); `output`
(`sampleEvery`, the trace decimation). Unknown, duplicated or missing fields are refused. Every run's half-section
travel time `tau/(2n)` must be a whole number of its timesteps (the engine would otherwise round the delay).

The result gives Zc `sqrt(L'/C')`, the velocity, the travel time `length/velocity`, the front attenuation
`exp(-(R'/(2Zc) + G'Zc/2) length)` and the source and load reflection coefficients `(Z - Zc)/(Z + Zc)`; for the base
run and every refinement pair the measured delay, front attenuation and reflection beside them; the base-run traces
(sending and receiving voltage, sending current, and the analytic values when they exist); and, when the oracle is
`EXACT` (a distortionless or lossless line with a matched source, where the travelling-wave solution is closed form),
the largest error over every step and the observed convergence order along the timestep and section axes. A line that
distorts (`R'C' != G'L'`) or an unmatched source has `FRONT AND DELAY ONLY`. `status` is `EVALUATED`.

Exit 0 when evaluated. Exit 1 for input it cannot accept (a malformed or refused study, a delay that is not a whole
number of steps, an existing output path). Exit 2 for an invalid invocation.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |

### emt3

`gridsim emt3 <study.json> [--out <file>] [--json <file>] [--csv <file>]`

Three-phase electromagnetic transients on the RC4 nodal solver (Core `GridSim.Core.Dynamics.Emt.Nodal`, `Nonlinear` and
`FrequencyDependent`), in SI units with a fixed step and the trapezoidal rule. It is new alongside `emt` and `emt-line`, which are
unchanged. `<study.json>` is a strict `gridsim-emt3/1` document (an unknown key, a missing key or a wrong type exits 1 naming it).
A bus has three nodes, one per phase. Keys: `time_step_s`, `duration_s`, `sample_every` (default 1), `frequency_hz` (default
50), `integration` (`trapezoidal` or `backward-euler`), `event_rule` (`trapezoidal` or `backward-euler-step`: one backward-Euler step
at each switching event), `newton` (`max_iterations` 100, `voltage_abs_tol` 1e-9, `voltage_rel_tol` 1e-10, `residual_abs_tol`
1e-9, `residual_rel_tol` 1e-10, `max_backtracks` 30), `buses` (names), and the element lists, all optional but `outputs`:

- `sources`: `name`, `bus`, `v_ll_rms` (V), `frequency_hz`, `angle_deg`, `rs_ohm`, `ls_h` (a balanced wye source behind a series R-L;
  both zero gives an ideal source, whose current output is not measured);
- `branches`: `name`, `from`, `to`, `r1_ohm`, `l1_h`, `r0_ohm`, `l0_h` (total sequence impedance of a series three-phase branch; the
  zero-sequence values default to the positive ones);
- `shunts`: `name`, `bus`, `r_ohm`, `c_f`, `l_h` (wye to ground, per phase);
- `lines`: `name`, `from`, `to`, `length_km`, `model` (`bergeron` or `fd`), `geometry` (an inline `gridsim-lineconstants/1` document
  for a transposed three-phase line, see `line-constants`), and for `fd` `band_hz` ([low, high], default [1, 10000]), `order` (real
  poles, default 8), `samples` (default 80), `iterations` (default 20), `enforce_passivity` (default true);
- `transformers`: `name`, `primary_bus`, `secondary_bus`, `r1_ohm`, `ll1_h`, `r2_ohm`, `ll2_h`, `core_loss_ohm`, `curve` (the
  magnetising current-flux points `[flux_wb, current_a]`, positive branch, increasing), `residual_flux_wb` (one number or three);
- `saturable_inductors`: `name`, `bus`, `phases` (letters of `abc`, default all), `curve`, `initial_flux_wb`;
- `arresters`: `name`, `bus`, `phases`, `model` (`power-law` with `vref_v`, `iref_a`, `alpha`; or `piecewise` with `points`
  `[volts, amps]`);
- `switches`: `name`, `from`, `to` (omit for a switch to ground), `phases`, `ron_ohm`, `roff_ohm`, `closed`, `events`
  (`at_s`, `close`);
- `faults`: `name`, `bus`, `phases`, `r_ohm`, `to_ground` (default true), `at_s`, `clear_s`;
- `machines`, `dc_buses`, `converters` and `hvdc` (optional, see below);
- `outputs`: `name`, `kind` (`voltage` of a bus, `current` of an element, `flux` of a saturable device, for a machine `speed`,
  `torque`, `rotor-angle` and `field-current`, for a DC bus `dc-voltage-positive` and `dc-voltage-negative`, for an average converter
  `dc-current`, which also names the current of an LCC bridge, a DC line and a station's DC supply), `target`, `phase` (`a`, `b` or
  `c`; required for `voltage`, `current` and `flux`, refused for the machine, DC bus and `dc-current` kinds) and, for a line current or
  the `dc-current` of a DC line, `end` (`from` or `to`). The `name` is the trace column and, as `max_abs_<name>`, a summary
  key, so it must be lower-case snake_case (a letter, then letters, digits or underscores) and may not be `time_s`; anything else
  exits 1 before the run. Kind names added to `gridsim-emt3/1` are lower-case words joined by hyphens, as the other enumerated
  values are (`power-law`, `backward-euler`); trace columns and summary keys are always snake_case.

`machines` entries (the Core R4-17 machine models; a document without the key runs exactly as before). Every entry has `name`, `kind`
(`synchronous` or `induction`), `bus`, `rating_mva`, `rated_kv_ll` (kV), `parameters` and optionally `frequency_hz` (default the
study frequency). A machine's `current` output takes a phase and is the line current of the phase delivered to the network for a
synchronous machine (generator convention) and absorbed from it for an induction machine (motor convention). The machine outputs
are `speed` (pu of synchronous speed), `torque` (electromagnetic, pu of the machine base), `rotor-angle` (load angle, rad,
synchronous only) and `field-current` (pu, synchronous only); they take no `phase`. Angles in a machine entry use the source
convention: phase a is `sin(w t + angle_deg)`.

- `kind` `synchronous`: a dq machine with field and damper windings, no saturation. `neutral` is `grounded` (default) or `isolated`.
  `parameters` are the RMS data set on the machine base: `inertia_s`, `damping_pu` (default 0), `xd_pu`, `xdp_pu`, `xdpp_pu`, `xq_pu`,
  `xqp_pu`, `xqpp_pu`, `tdop_s`, `tdopp_s`, `tqop_s`, `tqopp_s`, `xl_pu` (leakage; 0 or absent takes half the smaller subtransient
  reactance), `ra_pu` (default 0) and `x0_pu`. `initial` is `{"mode": "no-load", "v_pu", "angle_deg"}` (default 1 pu at angle 0, no
  current) or `{"mode": "phasor", "v_pu", "angle_deg", "p_mw", "q_mvar"}`: the machine starts in the steady state that delivers that
  power at that terminal voltage, with the field voltage and mechanical torque that balance it (the result's `machines` table lists
  them). The network must hold the terminal voltage at that value for the start to be a steady state; a source or a line has no initial
  current, so a machine behind a line starts with a transient. `torque_steps` and `field_voltage_steps` are lists of `{"at_s", "delta_pu"}`:
  from `at_s` the input is its initial balanced value plus `delta_pu`.
- `kind` `induction`: a single-cage machine in the stationary frame, three-wire (no `neutral`). `parameters`: `ra_pu`, `xa_pu`,
  `xm_pu`, `r1_pu`, `x1_pu`, `inertia_s`. `start_speed_pu` (default 0: a direct start from standstill with zero flux) and
  `load_torque` `{"k_pu", "speed_exponent"}` (shaft load `k_pu * w^speed_exponent` in pu of rated torque, exponent default 2; absent means
  no load).

Core refusals (a machine whose data cannot form a circuit, for example X'd not below Xd) exit 1 with the Core's own text. With machines
present the summary adds `comparator_status` (`COMPARATOR-UNVERIFIED`) and the result has a `machines` table: `machine`, `kind`,
`initial_load_angle_rad`, `initial_field_voltage_pu`, `initial_field_current_pu`, `initial_mechanical_torque_pu`, `initial_id_pu`,
`initial_iq_pu`, `start_speed_pu` (empty where a kind has no such quantity).

`dc_buses` and `converters` (optional; the Core R4-17 power-electronics models on the nodal solver). A DC bus is a split DC link:
`name`, `capacitance_f` (the series value of the two equal capacitors), `initial_v` (pole to pole), `supply_v` (pole to pole, default 0
for no supply) and `supply_ohm` (the supply's Thevenin resistance, default 0 for an ideal supply). Its rails are `<name>.p` and
`<name>.n` about a grounded mid-point; the outputs `dc-voltage-positive` and `dc-voltage-negative` give each rail to the mid-point in
volts (the pole-to-pole voltage is their difference). A converter is a three-phase two-level voltage-source converter between an AC `ac_bus`
and a `dc_bus`: `name`, `kind`, `rs_ohm` (default 1e-3), `nominal_vdc_v` (default the DC bus `initial_v`), an optional `filter`
(`r_ohm`, `l_h`: a series R-L from the converter terminals to the AC bus) and exactly one of `modulation` and `control`.

- `kind` `average`: the average-value model, a controlled voltage source `m Vdc/2` behind `rs_ohm` in each leg with no switching ripple;
  the DC current follows from the power balance and the DC voltage is the one of the previous step.
- `kind` `switched`: a bridge of gated diodes with `rs_ohm` as the on resistance and `roff_ohm` (default 1e7) as the off resistance,
  gated by a sine PWM `pwm` (`carrier_hz`, `carrier_phase` as a fraction of a carrier period, default 0; required). Switching instants
  are quantised up to the step grid, with no interpolation and no dead time; the summary states this as `switching_timing`.
- `modulation`: an open-loop reference `index * sin(2 pi f t + phase_deg - 120 deg k)` for phase k (`frequency_hz` default the study
  frequency, `phase_deg` default 0), limited to plus or minus 1.
- `control`: the sampled dq current controller, which needs a `filter`: `bandwidth_rad_s` (Kp = alpha L, Ki = alpha R on the filter),
  `control_every` (solver steps per control sample, default 1), `frequency_hz`, `phase_deg` (the d axis lies on the voltage of a
  source with that `angle_deg`), `id_a` and `iq_a` (initial references, amperes peak) and `steps` (`at_s`, `id_a`, `iq_a`).

A `current` output of a converter is the filter current of the phase (converter to bus) or, for an unfiltered average converter, the leg
current; an unfiltered switched converter has no single current and the output is refused. The result gains the `converters` table
(`converter`, `model_class` `AverageValue` or `Switched`, `description`) and the summary keys `switching_timing` and
`comparator_status`. A load that needs a floating star point cannot be written in a study (a branch load returns to a bus, and a
grounded bus gives the star point a ground path); the verified cases use a grounded star point. The Core grid-following and
grid-forming inverter models live in the older phase-domain engine and are not on the nodal solver, so they are not available here.

`hvdc` (optional; the Core R4-19 HVDC models, a link described as entries of three kinds; every converter is listed in the `converters`
table). Each station and bridge carries its own AC source, so an `hvdc` entry needs no AC bus of the study (`buses` is still required and may be an empty list; a bus with no element would leave its nodes floating and the matrix singular).

- `kind` `vsc`: a VSC station, the Core `VscHvdcStation`: an ideal grid source (`grid_v_ll_rms`, `frequency_hz`, `grid_angle_deg`) behind
  `grid_r_ohm` and `grid_l_h`, the converter reactor `filter_r_ohm` and `filter_l_h`, a converter (`converter`: `average`, the two-level
  average-value model with `converter_rs_ohm`, default 1e-3; `mmc-average` or `mmc-detailed`, with `mmc`: `submodules_per_arm`,
  `submodule_cap_f`, `arm_resistance_ohm`, `arm_inductance_h`), a DC link (`dc_nominal_v` pole to pole, `dc_cap_f`, `initial_dc_v` default
  `dc_nominal_v`), the measurement, the dq current controller (`current_bandwidth_rad_s` 1000, `control_every` 2, `current_limit_a`
  1e6, `reactive_current_a` 0) and the outer loop: `mode` `power` (`set_point` is the active power injected into the grid at the PCC in
  watts, negative when drawn) or `dc-voltage` (`set_point` is the pole-to-pole voltage in volts), `outer_bandwidth_rad_s` 60. For an MMC,
  `suppression_bandwidth_rad_s` (default 0) enables the circulating-current control. `dc_supply_v` adds a stiff DC supply of that
  voltage pole to pole, each pole behind `dc_supply_ohm` (default 0, an ideal supply as the Core MMC cases use; the current of an ideal
  source is not measured, so give a small resistance to read it). Outputs: `dc-voltage-positive` and `dc-voltage-negative` (each pole of the station's DC side to
  the mid-point), `current` with a phase (the reactor current, converter to grid) and, with a `dc_supply_v` and a `dc_supply_ohm` above zero, `dc-current` (the supply's current).
- `kind` `lcc`: a 6- or 12-pulse thyristor bridge group, the Core `LccBridgeEmt`, with its own transformer secondary sources
  (`v_ll_rms`, `frequency_hz`, `source_r_ohm` 1e-3, `commutation_l_h`, `pulses` 6 or 12, `valve` `ron_ohm` 1e-3 and `roff_ohm` 1e7,
  `neutral_reference_ohm` 1e6), a firing delay `alpha_deg` with optional `alpha_steps` (`at_s`, `alpha_deg`), into a series R-L DC `load`
  (`r_ohm`, `l_h`). Outputs: `dc-voltage-positive` and `dc-voltage-negative` and `dc-current` (the load current).
- `kind` `dc-line`: `from` and `to` (two `vsc` stations of the study), `r_ohm` (the loop resistance, half in each pole at the `from` end),
  `surge_ohm`, `delay_s` (the travel time) and `initial_current_a` (default 0): a Bergeron line per pole, started in the steady state that
  carries that current from `from` to `to` at the stations' initial DC voltages. Output: `dc-current` with `end` `from` or `to`.

A refusal of the Core (a pulse count other than 6 or 12, a DC voltage set point that is not positive, a line shorter than the step) exits 1
with the Core's own text. The grid-forming and grid-following inverters, a DC line with sheath or frequency dependence and multi-terminal
DC grids are not available. A `partition` key (parallel runs cut at Bergeron lines) is not offered: the Core partitioned engine cuts only
single-conductor Bergeron lines of a network built per partition, while a study builds one network of three-phase buses whose lines are modal
(`bergeron`, `fd`) and cannot be cut, so a faithful `partition` key would need a new three-phase cuttable line element. The
partitioned engine has its own verb, [`emt-partition`](#emt-partition), on a single-conductor network; [`cosim`](#cosim) runs an
emt3 study against an RMS phasor side.

An `fd` line is fitted for the study's step: the delay removed from the propagation function is the group delay at the top of the band
rounded down to whole steps, and the step must not exceed the shortest line travel time. The fit is refused (exit 1) when the fitted
characteristic admittance or propagation function is not passive and a perturbation of at most 5 per cent cannot correct it. The
result gives, per line and mode, the fit error (maximum and rms, separately for the characteristic admittance and the propagation
function), the delay and any passivity correction. Nothing is compared with PSCAD or EMTP (COMPARATOR-UNVERIFIED); chapter 16 of the
manual lists what is and is not verified.

Exit 0 when the run completed; 1 when the file is missing or invalid, or the network or a line fit is refused; 2 for a usage error;
3 when the run ended with a named status other than completed (the Newton iteration did not converge, or the nodal matrix was
singular): the traces up to the last good step are still written and the status and time are in the summary.

| Flag | Default | Meaning |
|---|---|---|
| `--out` / `--json` / `--csv` | - | Write the result through the shared [result writer](#result-files): the summary (status, step, method, factorisation and Newton counts and tolerances, the largest absolute value of each output) and the tables `line_fits` (one row per line and mode) and `traces` (`time_s` and one column per output). |

### cosim

`gridsim cosim <study.json> [--out <file.json>] [--boundary-csv <file.csv>]`

RMS-EMT co-simulation through the Core interface (`RmsEmtInterface`). `<study.json>` is a strict `gridsim-cosim/1` document:

- `emt3`: an inline `gridsim-emt3/1` study (the detailed part, exactly as [`emt3`](#emt3) reads it);
- `boundary_bus`: the bus of that study where the RMS side connects;
- `form`: `thevenin` (the RMS side hands over an EMF behind a series R-L and takes back the boundary current) or `norton`
  (a current source with a shunt R and L, taking back the boundary voltage);
- `exchange_every_steps`: EMT steps between exchanges (1 or more);
- `rms`: the RMS side, a source phasor `source_re_v`, `source_im_v` (peak volts, cosine convention: phase a is
  `Re{E e^(j w t)}`) behind either `line` (`surge_ohm`, `travel_s`: a lossless line, shorter than a quarter wave) or
  `series` (`r_ohm`, `x_ohm`), with optional `source_changes` (`at_s`, `re_v`, `im_v`), each applied at the first exchange
  at or after its time.

The RMS side is the Core phasor side, not the RMS transient kernel: that kernel runs a whole case and has no entry to
exchange a boundary quantity part-way through. The interface is added after every element of the EMT study. One fundamental
cycle must be a whole number (at least 8) of EMT steps, and the equivalent impedance must be inductive. The result is
bit-repeatable with itself and is not bit-comparable with an all-EMT run (the equivalent is fitted at the fundamental only).
See [ch. 16](<0016 Electromagnetic transients (EMT).md>).

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Write the EMT traces and the exchange log as JSON (`gridsim-cosim/1`); an existing file is never overwritten. |
| `--boundary-csv` | none | Write the exchange log as CSV: `time_s`, `valid`, the voltage and current phasors the EMT side measured, the voltage the RMS side computed, and the EMF and impedance it handed over (peak values, exact round-trip numbers). |

The verb is not on the shared `--json`/`--csv` writer.

Exit 0 when the run completed. Exit 3 when the EMT run ended with a named status other than completed. Exit 1 for a missing
or invalid file, or a network or interface the Core refuses. Exit 2 for usage.

### emt-partition

`gridsim emt-partition <study.json> [--partition auto|<cut.json>] [--threads 1] [--monolithic] [--out <file.json>]`

The partitioned parallel EMT run of the Core (`PartitionedEngine`, `LinePartitioner`). The Core cuts a network only at
single-conductor Bergeron lines, while an `emt3` study builds three-phase buses whose lines are modal, so this verb has its
own input. `<study.json>` is a strict `gridsim-emt-partition/1` document: `time_step_s`, `duration_s`, `sample_every`
(default 1), and the lists

- `sources`: `name`, `node`, `r_ohm` (default 0), `amplitude_v`, `frequency_hz`, `phase_deg` (default 0): the EMF
  `amplitude_v sin(2 pi f t + phase)`;
- `branches`: `name`, `kind` (`resistor`, `inductor`, `capacitor` or `series-rl`), `from`, `to`, and `r_ohm`, `l_h` or
  `c_f` as the kind needs;
- `switches`: `name`, `from`, `to`, `ron_ohm` (default 1e-6), `roff_ohm` (default 1e9), `closed` (default false),
  `events` (`at_s`, `close`);
- `lines`: `name`, `from`, `to`, `surge_ohm`, `travel_s`, `attenuation` (default 1): a Bergeron line with ground return;
- `outputs` (required): `name`, `kind` (`voltage` of a node, `current` of a branch or switch), `target`.

A node named `gnd` or `ground` is the reference. The network is split into partitions: the connected parts left when the cut
lines are removed, named p1, p2, ... in the order their first node appears in the document. A cut line must have a travel
time of at least one step; partitions then run between exchanges for the shortest cut travel time in whole steps, and the
result does not depend on the thread count. Nonlinear elements and the backward-Euler event rule are not offered (the Core
refuses them in a partitioned run). See [ch. 16](<0016 Electromagnetic transients (EMT).md>).

| Flag | Default | Meaning |
|---|---|---|
| `--partition` | none (nothing cut) | `auto` cuts every line whose travel time is at least the step; otherwise a `gridsim-emt-cut/1` file, `{"schema": "gridsim-emt-cut/1", "cut": ["line names"]}`. |
| `--threads` | 1 | Worker threads for the partitions. The traces are the same bits for any count; the result file records the count used (`threads`), as provenance, so two files made with different counts differ in that field only. |
| `--monolithic` | off | Build the same partitions as one network and run the single-network nodal solver (the engine's reference: the same bits). |
| `--out` | none | Write the result as JSON (`gridsim-emt-partition/1`: the plan, the times and one trace per output); an existing file is never overwritten. |

The verb is not on the shared `--json`/`--csv` writer.

Exit 0 when the run completed. Exit 3 when the run ended with a named status other than completed. Exit 1 for a missing or
invalid file, or a network or cut the Core refuses (a cut line shorter than the step). Exit 2 for usage.

### harmonic-assess

`gridsim harmonic-assess <study.json> [--out <result.json>]`
`gridsim harmonic-assess --report-template --out <template.json>`

Result files: `--json <file.json>` or `--csv <file.csv>` write the result to a file through the shared writer ([Result files](#result-files)); the document is built from the same result this verb prints, and `--json` is the form `gridsim.run` and `gridsim rpc` use. `--out` is unchanged: it still names its result JSON, not a shared-writer file.

UK harmonic connection assessment by the staged procedure of ENA Engineering Recommendation G5 Issue 5 (2020; docs/reference
S139; the ENA publishes it free of charge), RC4 R4-21. It is new alongside `harmonics`, which is unchanged. For a PCC of 0.4 kV
and below: Stage 1A (product-standard compliance, declared by the user), 1B (not evaluated: the IEC 61000-3-12 short-circuit-ratio
rule is not implemented, which can only send a connection on), 1C and 1D (aggregate rated power or minimum short-circuit power,
Table 19 and equations 10 to 20), then 2C. Above 0.4 kV and below 33 kV: Stage 2A, 2B (Table 21, equations 21 to 29, for 6.6,
11, 20 and 22 kV) and 2C, the prediction of the harmonic voltage against the planning levels with the worst-case source impedance
(equations 30 to 43, Table 22). A 2C failure at an LV PCC is "no connection possible without mitigation"; above LV it goes to
Stage 3. A PCC of 33 kV or above is Stage 3 outright. An optional clause 9 resonant-plant test (equations 44 to 49) is included.

The study file is strict JSON, schema version 1: `schemaVersion` (1), `study` (`harmonic-assess`), `pcc` (`lineKv`, `sscMva`
the minimum three-phase short-circuit power, optional `sscSinglePhaseMva`, `xOverR`), `equipment` (each `id`, `technology`
`six-pulse`, `active-front-end`, `twelve-pulse` or `single-phase-rectifier`, `ratingKva`), optional `declaredCompliantIec61000_3_2`,
optional `background` (`mode` `measured` with `pct`, the level by order in percent of the fundamental, and optionally
`missingOrdersAreZero`; or `mode` `assumed-75`, the 75 percent of the planning level of clause 8.4.1, subject to the network
operator's agreement), optional `emissionPct` (the current by order; default the rating-weighted G5 Annex B Table B.3 profile of
the technology), optional `diversityDeclared` (Table 16 exponents; default linear addition, clause 6.3.3), `maxOrder` (default
100), `resonantPlant` (`sscXMva`, `qrpMvar`, `pdMw`) and `stage3`. `stage3` runs the clause 10 assessment on a case: `case`
(`path` or `builtIn` with `sha256`, bound as for `transmission-security`), `pccBus`, `sources` (each `bus`, a library `type` or an
explicit `spectrum` of `order`, `pct`, and a `currentA` or `fundamentalCurrentPu`), `options` (`orders`, `balanced`,
`loadShunts`, `generatorSources`), `pccBackgroundPct`, `remoteBackground` (per bus), `assumedRemoteBackgroundFraction`,
`mvaCapacity` (needed above 132 kV), `transferThreshold` and `missingBackgroundIsZero`. Unknown or duplicated fields and
non-finite numbers are refused before any physics.

Stage 3 uses the existing penetration solver for the new user's voltage at every bus, the transfer coefficient
`|Z_PCC,n| / |Z_PCC|` from a unit current at the PCC (equation 50), the headroom of equations 51 to 53, the apportionment
multiplier (0.5 up to 132 kV; equations 56 to 60 above), the incremental and total limits (54, 63, 64), the resonant allowance
(65) and compliance by 67 to 69. A missing background is `INCOMPLETE` and named, never a pass. Where the document's text is
ambiguous the result's notes say how it was read (the 75 percent assumption is applied to THDV as one level; the Table 22
factor `k` by order). The final decision rests with the network operator. PowerFactory's power-quality module was not compared
(COMPARATOR-UNVERIFIED).

`--report-template --out <file>` writes a `gridsim-report-template/1` bound to this result, so `gridsim report <template> <result>
--out <file.md|html|csv>` renders the assessment through the R4-10 generator.

The result JSON (`schemaVersion`, `format` `gridsim-harmonic-assess/1`, `provenance`, `result` with `verdict`, `route`,
`planningTable`, `thdPlanningLevelPct`, `substages`, `stage2C`, `resonantPlant`, `notes`, `stage3`) is printed to stdout and,
with `--out`, written to a new file via a temporary file and a final move; an existing `--out` path is refused.

Exit 0 `ACCEPTED`. Exit 3 for `STAGE 3 REQUIRED`, `NO CONNECTION WITHOUT MITIGATION` or `INCOMPLETE` (the result is still
printed). Exit 1 for input it cannot accept (a malformed or unknown-field study, a case identity mismatch, an unreadable file, an
existing output path). Exit 2 for an invalid invocation.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file (with `--report-template`, the template). |
| `--report-template` | off | Write the report template for this verb's result to `--out` and exit; no study is read. |

### harmonic-pf

`gridsim harmonic-pf <case> --spectra <file.json> [--orders 5,7,11] [--norton-g 0] [--norton-b 0] [--out <file.json>]`

Experimental: the console headline carries `[EXPERIMENTAL]` and the result file `"experimental": true`, because the
coupling model is an assumed placeholder and the solver has not been checked against an independent coupled harmonic
solver.

Coupled harmonic power flow (Core `CoupledHarmonicPowerFlow`). The network is the study model the `harmonics` verb uses:
machine source reactance, loads as damping shunts, balanced sequences. The sources come from a `gridsim-harmonics/1` file, the
same file `harmonics --spectra` reads. Each source bus carries a Norton coupling admittance `g + j b h` (pu on the case base)
in parallel with its current source. With both values 0 (the default) the result is the plain penetration solve
`V_h = Z_h I_h`. The fixed-point loop over the source injections runs to 1e-10 pu, at most 50 passes per order. The
voltage THD of each bus is taken over the solved fundamental (Newton, 100 iterations, reactive limits on). See
[ch. 17](<0017 Harmonic studies.md>).

| Flag | Default | Meaning |
|---|---|---|
| `--spectra` | required | The `gridsim-harmonics/1` source file. A component at an order the study does not analyse is refused (exit 1), as `harmonics` refuses it. |
| `--orders` | 3 to 49, odd | The orders to solve, comma separated, each 2 or more. |
| `--norton-g` | 0 | Coupling conductance at each source bus, pu (not negative). |
| `--norton-b` | 0 | Coupling susceptance per harmonic order at each source bus, pu (the admittance at order h is g + j b h). |
| `--out` | none | Write the result as JSON (`gridsim-harmonic-pf/1`: per bus the fundamental voltage, the THD and, per order, the real and imaginary parts, magnitude and angle of V_h; the iteration count and the convergence flag); an existing file is never overwritten. |

The JSON from `--out` is the result file: the verb is not on the shared `--json`/`--csv` writer. The coupling model is an
assumed placeholder (provenance CuratedPlaceholder), not a measured converter characteristic.

Exit 0 when the fixed-point loop converged at every order. Exit 3 when it did not, or when the fundamental power flow did
not converge. Exit 1 for a case or source file that cannot be read, or a singular Y(h). Exit 2 for usage.

### harmonic-se

`gridsim harmonic-se <case> <measurements.json> [--out <file.json>]`

Experimental: the console headline carries `[EXPERIMENTAL]` and the result file `"experimental": true`, because the
estimator has not been checked against an independent reference.

Harmonic state estimation (Core `HarmonicStateEstimator`, weighted least squares). At each order in the measurement file it
estimates the harmonic current injected at each candidate bus from measured bus voltage phasors, on the same study model as
`harmonic-pf`. The measurement file is a strict `gridsim-harmonic-meas/1` document: `takenUtc` (required, not in the
future), `baseMva` (must match the case), and `orders`, each with `order`, `candidates` (bus numbers) and `voltages`
(`bus`, `re`, `im` in pu, `sigma` in pu). `sigma` is the standard deviation of each of the real and imaginary parts. Only bus
voltages are read: the estimator has no branch current measurement, so a `currents` key is refused.

Per order the verb reports the estimated injections, the estimated voltage at every bus, each measurement's residual and
normalised residual (`|r| / sqrt(Omega_ii)`, with Omega_ii the variance of each of the residual's real and imaginary
parts), whether the candidates are observable, and the chi-square test: `J = sum |r|^2 / sigma^2` against the exact 99 %
quantile with `2 (m - s)` degrees of freedom (m measurements, s candidates; 13.2767 at 4 degrees of freedom). Because the
residual is complex, the square of a normalised residual follows chi-square with 2 degrees of freedom under Gaussian
errors, not the square of a standard normal: a value above 3 has a 1.1 % chance from noise alone (exp(-9/2)), not 0.27 %.
The verb uses it only to name the largest. A measurement the estimate must pass through (a critical measurement) has no normalised
residual. See [ch. 17](<0017 Harmonic studies.md>).

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Write the result as JSON (`gridsim-harmonic-se/1`); an existing file is never overwritten. |

The verb is not on the shared `--json`/`--csv` writer.

Exit 0 when every order is observable and passes the chi-square test. Exit 3 when an order is not observable or fails the
test (bad data suspected; the largest normalised residual names the likely measurement). Exit 1 for a case or measurement
file that cannot be read, fewer measurements than candidates, or a singular Y(h). Exit 2 for usage.

### flicker

`gridsim flicker <study.json> [--out <result.json>]`

Result files: `--json <file.json>` or `--csv <file.csv>` write the result to a file through the shared writer ([Result files](#result-files)); the document is built from the same result this verb prints, and `--json` is the form `gridsim.run` and `gridsim rpc` use. `--out` is unchanged: it still names its result JSON, not a shared-writer file.

UK flicker and rapid-voltage-change assessment by ENA Engineering Recommendation P28 Issue 2 (2019; docs/reference S141; free), and
the structure of the IEC 61000-4-15 flickermeter (RC4 R4-21). The study file is strict JSON, schema version 1: `schemaVersion` (1),
`study` (`flicker`), `nominalKv` (selects the Table 2 planning levels), optional `sources` and `background` (each `id`, `pst`,
optionally `plt`; summated by equation 1 with `exponent`, default 3, Table 8), `transfer` (`fromKv`, or an explicit `coefficient`,
for the Table 3 transfer from a higher voltage; flicker is not transferred upward, and a pair the table does not list is refused),
`steps` (a voltage step as `stepPct`, or `deltaSMva` with `sscMva` by equation 4, with `intervalSeconds`; judged by the simplified
rule of clause 6.3.3.2), `pst05Curve` (points read from Figure B.1.2: `intervalSeconds`, `stepPct`), `inrush` (annex C equation C.1:
`m`, `k`, `ratedMva`, `sscMva`), `rvc` (a rapid voltage change: `category` 1, 2 or 3, `samples` of `timeS` and `deviationPct`, and
`steadyStateChangePct`, checked against the envelopes of Table 4 and Figures 5 to 7 and the 3 percent step limit), `stage1`
(the LV Stage 1 conditions of clause 6.3.2) and `flickermeter`.

The Pst = 0.5 curve of Figure B.1.2 and the shape-factor curves are figures, not tables, in the document: the rule decides without a
curve only at the points the text states (3 percent once every 475 s; 1 percent not more than every 20 s) and is otherwise
`INCOMPLETE` until curve points or a flickermeter result are supplied. The flickermeter (`parameters` and a synthetic `signal`)
runs the block structure (input adaptor, squaring demodulator, high-pass, Butterworth low-pass and weighting filter, squaring and a
first-order sliding mean, percentiles and the Pst combination). IEC 61000-4-15 is a paid standard: no filter coefficient is built
in, a `flickermeter` without `parameters` reports `AWAITING STANDARD TEXT`, and any result computed carries the same status. It
is checked here only by derivation (analytic responses, linearity), not against the standard's test points.

Exit 0 `ACCEPTED`. Exit 3 `NOT ACCEPTED` or `INCOMPLETE` (the result is printed). Exit 1 for input it cannot accept. Exit 2 for an
invalid invocation.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |

### filter-design

`gridsim filter-design <study.json> [--out <result.json>]`

Result files: `--json <file.json>` or `--csv <file.csv>` write the result to a file through the shared writer ([Result files](#result-files)); the document is built from the same result this verb prints, and `--json` is the form `gridsim.run` and `gridsim rpc` use. `--out` is unchanged: it still names its result JSON, not a shared-writer file.

Passive harmonic filter design by closed forms (RC4 R4-21): single-tuned, high-pass (second-order damped) and C-type. The study file
is strict JSON, schema version 1: `schemaVersion` (1), `study` (`filter-design`), `filters` (each `id`, `kind` `single-tuned`,
`high-pass` or `c-type`, `voltageKv` phase-phase, `fundamentalMvar`, `tunedOrder`, optionally `qualityFactor` and
`fundamentalHz`), optional `network` (a `source` of `sscMva` and `xOverR`, or a `case` with `sha256` and a `bus`, whose driving-point
impedance is taken from the existing frequency sweep) and `sweep` (`minOrder`, `maxOrder`, `step`).

For a single-tuned filter at order h generating Q at the fundamental on a bus of voltage V, `X_C = V^2 h^2 / (Q (h^2 - 1))` and
`X_L = X_C / h^2`; the impedance is the series resistance, zero without one, at h exactly, and the parallel resonance with a
source reactance `X_s` is at `sqrt(X_C / (X_L + X_s))`. The high-pass filter solves `X_C` so that exactly Q is generated at the
fundamental; the C-type has `X_C1 = V^2 / Q`, `X_C2 = X_C1 / (h^2 - 1)` and `X_L = X_C2`, so its resistor carries no fundamental
current. The result gives the components (ohm, microfarad, millihenry), the fundamental real power, and with a network the
parallel and series resonance orders of the network with the filter in parallel. Quality factor `q = R / (h X_L)`.

Exit 0. Exit 1 for input it cannot accept (a malformed study, a case identity mismatch, a non-physical filter, an existing output
path). Exit 2 for an invalid invocation.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |

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
`fleet --device-ffr`, `evidence --corners`, `replay-event --device-ffr`) exits 2 with `error: <verb> does not take
<flag> ...` (the per-verb flag gate, [Parsing rules](#parsing-rules-and-exit-codes)), or, for a flag the gate passes
but the verb does not read, `error: <verb> does not use <flag> ... <verb> accepts: ...`. `--asset-dynamics` is refused by all five, and `--forming`, `--forming-inertia` and
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
| `--dense` / `--sparse` | the shared rule | With `--solve`: force the dense or the sparse Newton engine. |
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

`--fleet` is checked before `--schema`, and `--all-gsps` replaces the single build. A flag the chosen mode does not
use exits 2 naming it, before the lake is read (GR-696): `--fleet` takes only `--root` and `--as-of`; `--schema` only
`--root`; `--all-gsps` takes `--root`, `--dno`, `--as-of`, `--minkv`, `--base`, `--neg-gen`, `--all-gen`, `--no-oltc`,
`--no-geo` and `--solve` (each area is always solved) but not `--licence`, `--gsp`, `--out`, `--dense` or `--sparse`;
`--dense` and `--sparse` need `--solve`; and `--as-of` (the NGED register vintage) is refused with `--dno ukpn` or
`npg`, whose readers take no register date. An unknown `--dno` or an
unparseable `--as-of` exits 2; a root, load or build failure exits 1; a carve that keeps no live element (0 buses)
exits 3 and writes nothing; `--solve` exits 3 when the solve does not converge.

### gda-replay

`gridsim gda-replay (--series <ts.json> | --synth) [flags]`

The historical state estimator: replay a demand time series through the verified solver, reconstructing grid state at
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

`gridsim convert <input.m|input.raw|input.uct|case-dir|case> <output.m|output.raw|output.rawx|output.uct|output.xml|output.json|output-dir> [--raw-revision 33|34|35|36] [--ucte-country <CC>] [--public [--provenance <manifest.json>]]`

Convert a case between MATPOWER, PSS/E RAW, CGMES, a JSON case folder (v1) and a JSON v2 case. An output ending in `.m` is written as
MATPOWER (version 2); an output ending in `.raw` is written as PSS/E RAW revision 33, or the revision `--raw-revision` names (33 to 36; see below); an output ending in `.rawx` is written as PSS/E RAWX (JSON), revision 35; an output ending in `.uct` is written as UCTE-DEF (see below); an output ending in `.xml` is written as CGMES RDF/XML (see below); an output ending in `.json` is written as a JSON v2 case (N299, October 2026): the manifest at that
path and its network file beside it (`uk.case.json` writes `uk.network.json`); any other output is a JSON case folder. The input is loaded as every verb loads it (a `.m` file through the unit
normaliser, a PSS/E `.raw` file through the RAW reader ([ch. 2](<0002 Loading networks.md#psse-raw-files>)), a UCTE-DEF `.uct` file through the UCTE reader ([ch. 2](<0002 Loading networks.md#ucte-def-files>)), a CIM / CGMES input (below), a JSON folder dispatched as usual, or a built-in name). The conversion is then proved: the written case is
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

A CIM or CGMES input is a single RDF/XML `.xml` document, a `.zip` exchange, or a folder of `.xml` profile files that is not
a GridSim case folder (no `system.json`, `buses.json`). It is read with the Core importers the desktop app uses
(`CimImporter` with its default options, then `CimGridModelBridge` on a 100 MVA base; the profile documents EQ, TP, SSH and SV
are used, DL, GL and SC are left out): the bus-branch model that the TopologicalNodes make. The bridge's notes are printed on stderr as
`note:` lines. CGMES carries no system frequency, so `--frequency-hz` applies as for a `.m` file. The licence's bus limit applies as
for `cgmes-study`. The conversion and its round-trip proof then run as for any other input; what the output format cannot hold is
named in the `note:` lines, and a CIM object the importer does not model is dropped by the importer with its note. A node-breaker model
that you want to keep as switches goes to [substation-state](#substation-state) instead.

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

A revision 36 RAW output holds the revision 35 records under the revision 36 header, each record group under an `@!` heading
line that names its fields; the fields revision 36 added (load, generator and shunt names, DROOPNAME, BYPASS) are left to
their defaults. GridSim's revision 36 reader places fields by those headings, so it reads the file to the revision 35
model bit for bit. A RAWX output holds the same records as a revision 35 RAW output, field by field, with the section and
field names of the public RAWX layout and every number in the same round-trip form, and is read back the same way.

A UCTE-DEF output holds nodes (under `##Z` country blocks), lines, transformers and their regulation in the fixed
columns of the UCTE-DEF version 02 format. A bus whose base voltage is not one of the ten UCTE levels (750, 500, 380,
330, 220, 150, 120, 110, 70 or 27 kV, so 0 kV too) is refused, as are HVDC links, FACTS devices, static var
compensators, induction machines, ZIP loads, a second unit at one bus and a bus shunt with no transformer to carry it
(UCTE-DEF has no node shunt; a shunt rides on a transformer whose node 1 is the bus). A `.uct` input is written back
with its own node codes, country blocks and names, and each transformer with the file's own rated voltages, nominal
power and regulation record, so it reads back to the same model. Each line and transformer keeps its status code (0
real, 1 equivalent, 2 busbar coupler, or the matching out-of-operation code 8, 9 or 7 for one switched out), and the
file's own out-of-operation records are written back as they were. A branch with a negative R or X from another
format is written as an equivalent element (status 1), the only kind UCTE-DEF lets carry a negative impedance, and the
notes name it. For any other input the node codes are synthesised:
the country's code character, the bus id in five digits, the level code and busbar 1, under the country's `##Z` block.
`--ucte-country` names the country as its two-letter UCTE block name (for example `FR`, giving codes such as
`F0001251`); it is required when codes are synthesised (exit 1 without it, naming the flag), and a name that is not a
UCTE country, or `XX`, exits 2. A synthesised code never starts with `X`: UCTE keeps `X` for X-nodes, the boundary nodes
that other UCTE tools treat as boundary points. Transformers are written with nominal power from rating A (100 MVA,
the system base, when rating A is 0). Numbers are fixed-width text, written with as many decimals as their columns
hold, so a case from another format comes back to that precision, not bit for bit, and the proof then reports the
difference (exit 3) with the largest voltage difference; the notes name what UCTE-DEF cannot hold (start voltages of PQ
buses, voltage limits, ratings B and C, unit data beyond the power-flow record).

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

`gridsim pmu-ingest --live tcp://host:port|udp://[bind]:port --live-frames N [--live-seconds S] [--live-idcode ID] [--live-udp-source IP | --live-udp-any-sender] [--live-quarantine <dir>] [--channels <map.json> --measurements-dir <dir>]`

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
configuration change, data modified) and the leap-second bits do not decide. STAT bit 10 (configuration change)
warns that the PMU will change its configuration; when any DATA frame carries it, one stderr line gives the count and
asks for the CONFIG frame (CFG-2) to be re-sent after the change (the data itself is used).

| Flag | Default | Meaning |
|---|---|---|
| `--channels` | - | A PMU channel map (`gridsim-pmu-channels/1`): `schema`, `case`, `baseMva`, the angle `reference` (`pmu`, `phasor`, `anchorDeg`, a mapped voltage channel) and per PMU (`pmu`) its `voltages` (`phasor`, `bus`, `baseVolts`, `vmSigmaPu`, `vaSigmaDeg`) and `currents` (`phasor`, `from`, `to`, optional `circuit`, `baseAmps`, `iMagSigmaPu`, `iAngSigmaDeg`, and optional `end`, `from` (the default) or `to`, the branch end the current is metered at). Bases and sigmas must be finite and above 0. Checked against every configuration in force for a decoded DATA frame (each CONFIG frame of the capture that decoded at least one DATA frame; the last one when none did): every PMU present, every phasor index inside its PMU's count, a voltage channel on a voltage phasor and a current channel on a current phasor (PHUNIT). When the configuration changed during the capture, each one the map does not fit is named by the frame it arrived in (`against the configuration from CONFIG frame N (in force for K decoded DATA frame(s)): ...`). Needs `--measurements-dir`. |
| `--live` | off | N340: read a LIVE IEEE C37.118.2 stream instead of a capture file (give no file with it). `tcp://host:port` connects to the PMU or PDC, sends COMMAND frames "send CONFIG-2" and "turn on transmission" to `--live-idcode`, frames the stream (a byte where SYNC 0xAA must start a frame, or a frame size below 16, stops the run: exit 1, no resynchronising) and sends "turn off transmission" when it stops. `udp://[bind]:port` listens (bind address default 127.0.0.1, this machine only; it was 0.0.0.0 before bug #173) for the device's stream, one frame per datagram. A bind address that is not loopback (for example `udp://0.0.0.0:4712` for a PMU on another machine) needs `--live-udp-source <pmu-ip>` or, on purpose, `--live-udp-any-sender`; without either the run stops before listening, exit 2; a datagram that is not exactly one frame is dropped and counted (exit 3). The received frames then go through exactly the recorded path above (CRC, CONFIG, decoding, quality, alignment, the channel map and the horizon: a device streaming the present is refused, `REJECTED (forward inference)`, exit 2, like a fresh capture; a PDC replaying history is read). Prints a `live:` line with the frames received. |
| `--live-frames` | - | With `--live` (required): stop after this many DATA frames (1 or more). |
| `--live-seconds` | 30 | With `--live`: the longest the receive runs (above 0, at most 86400). With `tcp://` the budget applies to each phase in turn (bug #160): the connection gets S seconds, the device's first frame gets S seconds from the connection, and the S seconds of receiving start at that first frame, so a slow connect no longer eats the receive. Stopping on it before `--live-frames` DATA frames arrived exits 3 after the summary; no frame at all exits 3. A device that sends more than 1024 frames that are not DATA frames (CONFIG, HEADER, COMMAND) stops the run, exit 1, instead of filling memory until the timeout (bug #177). |
| `--live-idcode` | 1 | With a `tcp://` `--live`: the device IDCODE (0 to 65535) the COMMAND frames address. Refused with `udp://`. |
| `--live-udp-source` | any sender on loopback; required off loopback | GR-818: with a `udp://` `--live`, read only datagrams whose source address is this IP address; datagrams from any other sender are dropped and counted on the `live:` line, never captured. Refused with `tcp://`. Required (or `--live-udp-any-sender`) when the bind address is not loopback (bug #173). |
| `--live-udp-any-sender` | off | Bug #173 (explicit opt-out, no value): with a `udp://` `--live` on a bind address that is not loopback, read datagrams from any sender. A CRC is not authentication, so any host that can reach the port can then inject frames; the run warns. Refused with `tcp://` or together with `--live-udp-source`. |
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
| `--distrust` | - | `<source>[,<source>...]`: measurement source tags (the rows' `source`, matched trimmed and without regard to case) the operator no longer trusts, for incident containment. Every row of a named source is demoted to a pseudo-measurement (its sigma multiplied by 10000, so its weight falls by 1e8; its value is unchanged) and left out of the chi-square redundancy: its residual is not added to J and it is not counted in the degrees of freedom, so the printed `redundancy (dof)` falls by one per demoted row. The rows stay in the fit and in `residuals.csv`. One line per source says how many rows were demoted, and with `--audit-log` one `DistrustedSource` security event is recorded per source (raised as an incident with `--incident-log`). Exits 2 on an empty item, a tag with a space, a repeated tag, the tags GridSim writes itself (`prior-solved`, `removal-backfill-prior`), a tag no row of the set carries (the message lists the tags it does carry), and with `--hti`. Without the flag nothing changes. |
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
PATH order; else, on Windows, the `py` launcher by its full path (in the Windows folder, or under
`Programs\Python\Launcher` in the local application data; `py.exe` on PATH is found too). No bare name is ever
started, on any system, and a relative PATH folder (`.`, `bin`) is not searched: Windows, and .NET on Linux and macOS,
look in the current folder before PATH, so a `python` in the folder of a downloaded case would run. For the same reason
a relative `GRIDSIM_PYTHON` path is refused (exit 1) and a relative `VIRTUAL_ENV` is skipped, and the `py` launcher is
started with `-3` before the script and `PYLAUNCHER_NO_SEARCH_PATH=1` in its environment, so it does not follow the
script's `#!/usr/bin/env python3` line to a search that also looks in the current folder. With no interpreter
found the verb exits 77. Voltage-dependent (ZIP) loads in the export are
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

`gridsim serve <case> --measurements-dir <dir> [--method wls|ekf|ukf|enkf] [--utc-now <iso8601>] [--max-windows N] [--distrust <source>[,<source>...]]`

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
| `--distrust` | - | `<source>[,<source>...]`: as for `estimate --distrust`, applied to every window: each named source's rows are demoted to pseudo-measurements (sigma x 10000) and left out of the chi-square redundancy. A line per source is printed before the first window, and with `--audit-log` one `DistrustedSource` security event is recorded per source for the run (not per window). A tag that no loaded window carried gives a `warning:` line on stderr after the summary. `--method wls` only: with `ekf`, `ukf` or `enkf` it exits 2; a malformed list exits 2 as for `estimate`. |

Exit 2 on missing arguments, a bad `--method`, `--utc-now` (malformed, or later than the real clock) or `--max-windows`
(below 1), a malformed `--distrust` or one given with a recursive filter, a filter size refusal, or a forward-inference rejection (an undated window);
exit 1 if the case does not load or the directory holds no `*.json` windows; exit 3 if any window failed, or if no
window produced a usable estimate (none converged: every window unobservable or not converged, or every window inside
the horizon), with the reason on stderr. A window file that cannot be loaded (not JSON, not a measurement set) is named
on stderr and skipped, as the web feed does, and the other windows run; the run then ends with exit 1 (an input error,
ahead of any exit 3). An estimate the output guard refuses as newer than the horizon gives `REJECTED (output guard)`
and exit 2.

### rpc

`gridsim rpc [--listen <host:port>] [--allow-non-loopback] [--idle-seconds <S>] [--protocol 1|2]`

The in-process access endpoint the Python package drives (`gridsim.Session`, see [Python](#python)): a resident request
and response service, `gridsim-rpc/1`, that holds loaded cases between requests. It is not `serve` (the state-estimation
stream) and runs no study engine of its own: `solve` is the solver `pf` uses, `line-constants` is the Core line-constants
solver, and `run-verb` runs any other verb exactly as the command line would, as a child process of the host, so every
existing output stays the CLI's own. The licence gate is the one the CLI resolved at start-up.

Without `--listen` the requests are lines on standard input and the responses lines on standard output (anything an engine
would print goes to standard error), so there is no network at all. With `--listen` the same lines travel over one TCP
connection at a time, and the endpoint refuses (exit 2) any address that is not loopback (`127.0.0.1`, `::1`, `localhost`)
unless `--allow-non-loopback` is also given: the token and the data travel unencrypted, so listening beyond loopback is
an explicit choice, with a warning on standard error. The first standard output line in `--listen` mode is
`{"protocol":"gridsim-rpc/1","listening":"<address:port>","token_file":"<path>"}` (use port 0 to let the system choose).

**The session token (`--listen` only).** When it starts listening the host draws a random token (64 hexadecimal
characters) and writes it to a new file that only the user running the host can read, `GridSim/rpc/rpc-<machine>-<process id>-<random>.token`
(the machine part is the machine name's letters, digits and hyphens followed by 8 hex digits of its SHA-256, so two
machines never share it) in the per-user local application-data folder (`%LOCALAPPDATA%` on Windows; the platform's equivalent elsewhere, created
with mode 0600 in a 0700 folder). The first output line names the file; the token itself is never printed. Every request
over the socket must carry it as a `"token"` member, checked before anything else in the request: a request without
it, or with another, gets an error of kind `unauthorised` (exit code 2) and ends the connection. So another user of the same machine, who can reach a loopback port
but cannot read the file, cannot use the endpoint. The file is removed when the host stops. Over standard input and
output there is no token, and a `"token"` member is refused as a key the operation does not take. The file is removed
when the host stops (by `shutdown`, Ctrl+C, a termination signal or the console closing), and a token file left by a host
on the same machine that is no longer running is removed when the next one starts (only that machine's files: the folder
may be one home folder shared by the nodes of a cluster). A file of the older `rpc-<process id>.token` shape, which has
no machine part, is removed the same way when no process of that id runs on this machine, and so is a file of the
`rpc-<machine>-<process id>-<random>.token` shape whose machine part is this machine's name without the hash. Both
older shapes are swept best effort: they cannot say which node wrote them, so on a shared home folder a host of such an
older version still running on another node can lose its token file, and its clients then cannot find it.

**Time limits (`--listen` only).** A new connection must complete its first request, carrying the token, within 10
seconds of being accepted (or `--idle-seconds`, when that is shorter), however it sends the bytes, so a peer that connects
and sends nothing, or trickles a byte at a time, cannot hold the endpoint (connections are served one at a time). After
that the connection is closed when no request has arrived for `--idle-seconds` since the last response (default 120; 0
never closes it); a partial line does not count as a request, and a verb that runs for a long time does not count as
silence.

Over `--listen` the endpoint speaks only its own protocol. A request line is read up to the line limit and no further; a line
over the limit, a line that is not JSON and a line that is not a JSON object get their error response and then end the
connection (the server goes on accepting connections), so a client speaking another protocol at the port, such as a web
browser, cannot reach an operation. Over `--listen` every file a request names must lie inside the host's working
folder, the rule `save` applies: the case of `load`, the verb of `run-verb` and every value of its arguments (an argument
itself, the value of a `--flag=value` argument, and each item of a comma list such as `bench --cases a.m,b.m`) must resolve
inside that folder without passing through a link, and a network (UNC) or device path is refused in any of them. A value
that is not a path (a number, a bus id, an event) resolves inside the folder and passes. On Windows a value such as
`v:5` (a probe) has the shape of a drive-relative path; it is taken as a path, and refused, only when that drive exists.
The standard input transport is unchanged.

A request is one JSON object per line, `{"id": <string|number|null>, "op": "<operation>", ...}`; a key the operation does not
take is an error. The response is one line carrying `protocol`, the request `id`, `op`, `status` (`ok` or `error`),
`exitCode` (the CLI exit-code equivalent: 0 done, 1 input, 2 usage, 3 verdict), `case_sha256` (the held case's identity, or
null) and `result` or `error` (`kind` and `message`). A malformed line or a failed operation is an error response and the
session continues; a finished study whose verdict is a failure (`exitCode` 3) is `status` `ok` with its result. The
operations:

- `hello`: the protocol, the tool version, the build status (`build_status`: `RELEASE` or `UNSUPPORTED BUILD`, as in
  the result files) and the operation names.
- `load` (`case`): load a case once (a built-in name, a `.m` file or a case folder, as every verb takes it) and return its
  `handle` (`case-1`, `case-2`, ... per session; at level 2 `case-<host token>-1`, ...), size and `case_identity`. The SHA-256 is of the consumed files for a
  folder or `.json` case, of the file for another file, and of the name and build for a built-in name (`case_identity`
  `files`, `file` or `built-in-name`). A licence refusal is an `error` of kind `licence-refused` with the message the CLI
  prints after `error:`, exit 2. The licence's bus limit holds at solve time too: a level 2 case that has been edited
  (`set`, `add`, `delete`) is a user network, whatever it was loaded from, and is checked again before `solve`, the
  solved attributes of `get`, `study` and `save`; one over the limit gets the same `licence-refused` error.
- `solve` (`handle`): the `pf` solve of the held case, again and again without reloading: `converged`, `iterations`,
  `max_mismatch_pu`, `loss_mw`, `loss_mvar` and the bus columns `bus`, `vm_pu`, `va_deg`, `p_inj_mw`, `q_inj_mvar`, with the
  digits `pf --json` writes. `exitCode` 3 when it does not converge.
- `release` (`handle`), `stats` (requests, loads, solves, verb runs and handles held: the evidence that a handle is reused)
  and `shutdown` (ends the session; in `--listen` mode, the server).
- `run-verb` (`verb`, `args`, optional `timeout_s`): run a verb as `gridsim <verb> <args>`. For a verb that writes a result
  document the response carries it as `document` with the closing `--json <temporary file>` pair removed from its `args`,
  and the verb's `stdout` and `stderr` (the temporary path appears as `<result>`). `--json`, `--csv` and `--out` are refused
  for these verbs; `rpc` and `serve` cannot be run this way.
- `line-constants` (`geometry`, optional `per_unit` with `length_km`, `base_kv`, `base_mva`): the `line-constants` verb's
  result document for a geometry given inline.

**Protocol level 2 (`--protocol 2`, `gridsim-rpc/2`): the object model.** Level 1 is the default and is unchanged byte for
byte (`--protocol 1` is the same). Level 2 answers every operation above as level 1 does apart from the label
`gridsim-rpc/2` and the handle, lists eight more in `hello` after the eight above (the last two, `add` and `delete`, since 2.1.110), and holds an editable copy of each loaded
case. A level 2 host draws a random token when it starts (one per process, or per connection over `--listen`); `hello`
gives it as `host`, and every handle carries it: `case-<token>-1`, `case-<token>-2`, ... A handle issued by another host,
or by the same program before a restart, is refused as "not issued by this host", so it never reaches another case
([technical ch. 41](<../technical/0041 Scripting object model and API.md>)). Elements are keyed as follows, in model order:
a bus by its id; a generator by `<bus>:<machine id>` for a PSS/E case, else its name, else `G<bus>` (`#2`, `#3` on a
repeat); a load by the id of a bus with demand when the case was loaded; a branch by `<from>-<to>:<circuit>` for a PSS/E
case, else its name, else `<from>-<to>`; a shunt by the id of a bus with a fixed shunt. The attributes: bus `type` (1 PQ,
2 PV, 3 slack), `vm`, `va_deg`, `base_kv` (read only), `name` (read only), `vmin`, `vmax`; generator `pg`, `qg`, `vg`,
`qmin`, `qmax`, `pmin`, `pmax`, `mbase`, `status`; load `pd`, `qd`, `status` (0 keeps the set-point for re-energising);
branch `r`, `x`, `b`, `tap`, `shift_deg`, `rate_a`, `rate_b`, `rate_c`, `status`; shunt `gs`, `bs`; in MW, MVAr, MVA, pu on
the system base and degrees.

- `elements` (`handle`, `kind`): every element of the kind with every attribute.
- `get` (`handle`, `kind`, `key`, optional `attributes`): the attributes asked for. On a branch, `p_from_mw`, `q_from_mvar`,
  `p_to_mw`, `q_to_mvar` and `loading_pct` read the `solve` of the case as it stands (read only, by name only). On a
  generator, `p_solved_mw` and `q_solved_mvar` read the unit's output from the same solve: the bus net injection plus
  the bus load at the solved voltage (a lone unit takes the whole bus; several share it); an out-of-service unit is refused.
- `set` (`handle`, `kind`, `key`, `values`: attribute names and numbers): write them in one revision and return
  `revision`; `solve` then solves the edited case. A refused write (an unknown kind or attribute, a read-only attribute,
  status other than 0 or 1, an edit that makes the case invalid) changes nothing.
- `edits` (`handle`): the edit log.
- `study` (`handle`, `verb`, `args`, optional `timeout_s`): `pf`, `nminus1`, `nminus2`, `iec60909`, `fault`, `acopf-full`,
  `dcopf`, `scopf` or `stability` on the case as it stands, answered as `run-verb`. With no edit the host passes the case
  it loaded, so the document is the CLI's own. With edits it writes the case as a JSON v2 case to a temporary folder,
  reads it back and writes it again. It runs the verb only when every object model attribute of the copy read back equals
  the held case's, bit for bit, and the two writes are byte-identical (the write-read-write is idempotent); otherwise the
  study is refused naming the first difference. These checks cover every value an edit can change, not fields outside the
  object model. The host's case path is taken out of the document's `args` and shown as `<case>`
  in the printed text. A case the JSON v2 writer refuses (a bus with no positive base kV, as in case14) cannot be studied
  after an edit: `save` it as RAW and use `run-verb`. `dynamics-study` names its files by SHA-256, so it too goes through
  `save` and `run-verb`.
- `save` (`handle`, `path`, `format`: `raw33`, `raw34`, `raw35` or `v2`): write the case as it stands, never over a file
  with other content (the same bytes are accepted) and never where a link stands; files are only ever created new. Over
  `--listen` every file written (for `v2`, the manifest and its sibling `.network.json`) must lie inside the host's
  working folder and must not be a link, and network and device paths are refused. Every target is checked before any is
  written.

A response is a function of the request and the session's earlier requests only: no timestamps, process ids or temporary
paths, handles numbered per session, numbers in round-trip form. The one exception is level 2's host token, which makes
the handles and the `hello` differ from run to run. A verb that itself prints times is not repeatable through
`run-verb` either. Sessions are sequential; parallel work is parallel sessions (separate processes). Exit 0 when the input
ends or after `shutdown`; 2 on a bad flag or a refused listen address; 1 if the socket cannot be bound or the token file
cannot be written.

| Flag | Default | Meaning |
|---|---|---|
| `--listen` | none (standard input and output) | `host:port`, `[IPv6]:port` or a bare port (host `127.0.0.1`), port 0 to 65535: serve over TCP on that address. Non-loopback hosts are refused unless `--allow-non-loopback`. |
| `--allow-non-loopback` | off | Accept a `--listen` address beyond loopback (the token and the data travel unencrypted). Only with `--listen`. |
| `--idle-seconds` | 120 | Close a `--listen` connection after this many seconds without a request (0: never). Only with `--listen`. |
| `--protocol` | 1 | `1` (`gridsim-rpc/1`) or `2` (`gridsim-rpc/2`, the object model operations). |

### interpose

`gridsim interpose [<case>] --protocol <name> (--listen host:port | --port N) --crosswalk <map.json> [--allow-client IP[,IP...] | --allow-any-client] [--idle-seconds S] [flags]`
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

Who may connect (bug #174). With `--port N` or a bare port, interpose listens on 127.0.0.1 only (it used to listen on
every interface). A listener reachable from other machines (`--listen 0.0.0.0:N`, or any address that is not loopback)
needs `--allow-client <ip>[,<ip>...]`, the only RTU addresses that may connect (any other connection is closed at once
and counted), or, on purpose, `--allow-any-client`; with neither the run stops before listening, exit 2. A client that
sends nothing for `--idle-seconds` (default 120; 0 never drops one) is dropped so the next can connect: a peer that
connects and stays silent can no longer hold the single slot and lock the real RTU out. A healthy IEC 104 link is never
idle (TESTFR every t3 = 20 s). When any client was refused or dropped idle, a line `clients: refused N ..., idle-dropped
N ...` follows the stop line.

A malformed frame (rejected by frame correction, or framed but undecodable) is named once on stderr when the first one
arrives (`WARNING: interpose: malformed ...`); later ones are counted (`rejected`, `decode-errors`). Any malformed frame
makes the run exit 1 at its end, unless `--tolerate-malformed` is given (N230).

For IEC 104 the interposer is the controlling station and drives the link (N230): on each connection it sends STARTDT
act (and records STARTDT con), answers TESTFR act with TESTFR con, acknowledges received I-frames with an S-frame at the
latest after w = 8 of them or t2 = 10 s after the first unacknowledged one, and sends TESTFR act after t3 = 20 s with no
frame received (IEC 60870-5-104 clause 5 defaults [VERIFY]). A STARTDT act or TESTFR act not confirmed within t1 = 15 s,
or an I-frame out of sequence (its N(S) is not V(R)), closes the connection; the next outstation connection starts afresh
(GR-803, GR-823). The interposer sends no I-frames, so an S- or I-frame from the outstation whose N(R) is not 0
(acknowledging frames that were never sent) also closes the connection (the k window, k = 12). The verb does not send
STOPDT; it closes the connection when it stops.
Modbus TCP is received passively, as before.

IEC 104 decodes the monitor types 1, 3, 5, 7, 9, 11, 13, 15, 30 to 37 (N337: step position 5 / 32 and bitstring 7 / 33
are decoded, no longer counted unsupported). A step position (a tap position, -64 to +63; a moving tap is flagged
transient) and a 32-bit bitstring are points but not electrical measurements: they never enter a window, and a crosswalk
that binds one of their addresses is refused as a map error.

| Flag | Default | Meaning |
|---|---|---|
| `--protocol` | required | `iec104` (default port 2404) or `modbus` (default port 502). `--list-protocols` prints the registry and exits. DNP3 and IEC 61850 GOOSE are not selectable: they exist only as link-framing scaffolding with no application layer, and the registry listing says so. |
| `--listen` | `127.0.0.1:<default port>` | Endpoint `host:port`, `[IPv6]:port` or a bare port (a bare port listens on 127.0.0.1); the port is 0 to 65535 (exit 2 otherwise). A host that is not loopback needs `--allow-client` or `--allow-any-client` (bug #174). |
| `--port` | protocol default | Port on 127.0.0.1 (this machine only; it was every interface before bug #174), when `--listen` is absent. |
| `--allow-client` | any client on loopback; required off loopback | Bug #174: the only remote IP addresses (comma-separated) that may connect; any other connection is closed at once and counted. |
| `--allow-any-client` | off | Bug #174 (explicit opt-out, no value): on a listener that is not loopback, accept a client from any address. The run warns. Refused together with `--allow-client`. |
| `--idle-seconds` | 120 | Bug #174: drop a client that sends nothing for this long, so the next one can connect (0 to 86400; 0 never drops one). |
| `--crosswalk` | required | Point map (`.json`) from protocol addresses to measurements. |
| `<case>` | the crosswalk's case, else `rtu` | Case label for the windows (the case is not loaded). |
| `--measurements-dir` | - | Write each window older than the horizon as a JSON file here. A window newer than the horizon is not written (held); a run that held any exits 2 (see below). |
| `--out-store` | - | Also write windows to the quarantine Parquet store (refuses to open inside the GDA public lake). Windows newer than the horizon go here only, as quarantine captures. |
| `--source-id` | `rtu_live` | Source id for the quarantine store. |
| `--window-ms` | 1000 | Window length (ms); at least 1. |
| `--max-stamp-spread-ms` | off | Opt-in (GR-305): refuse a window whose device time tags span more than N ms (a whole number, 0 or more), so a point stamped long before the others is not merged into one snapshot. The window is discarded with a `window discarded (time-tag spread ...)` line, counted on a `spread-refused:` line after the stop summary, and the run exits 3. Points with no time tag take no part. |
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
| `--out` | `results/benchmark-results.md` | Report path (CSV alongside). Pass `docs/developer/benchmarks/history/bench-and-perf-yardsticks/benchmark-results.md` to refresh the committed copy. |
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
| `--out` | `results/perf-baseline.md` | Report path (CSV alongside). Pass `docs/developer/benchmarks/history/bench-and-perf-yardsticks/perf-baseline.md` to refresh the committed copy. |
| `--note` | - | Free-text note stamped into the header. |

### gpucheck

`gridsim gpucheck`

Fail-fast GPU preflight: reports CPU, ROCm, OpenCL and CUDA availability and runs one tiny case9 solve (dense and
sparse) on each present backend, flushing every step so a crash names the failing call. No flags. Exit 3 when any
probe that ran failed (a `FAIL` line, or a solve that ran but did not converge), listed on stderr; a backend that is not
present is skipped, not failed, so a machine with no GPU exits 0 (`gpucheck: OK - every probe that ran passed`). Each
GPU probe line ends with where its linear solves ran, for example
`[4 linear solves: 4 on the device, 0 on the CPU (0 gave up to the CPU LU, 0 on the host)]`; the CPU probe lines are
unchanged. A solve on a GPU backend (`--gpu`) likewise prints a `GPU:` line after the convergence line, and the run
metadata of a GPU run carries `gpuSolves` (`linearSolves`, `onDevice`, `gaveUpToCpuLu`, `onHost`, `onCpu`,
`ranEntirelyOnCpu`); a run whose every step fell back is the CPU result. See
[ch. 27, Validation tools](<0027 Validation tools.md>) and
[technical ch. 4, GPU accuracy](<../technical/0004 Sparse and GPU numerics.md#the-gpu-backends>).

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

### breaking-change

`gridsim breaking-change validate [--journal <file.jsonl>] [--since <version>] [--verb <verb>] [--case <glob>] [--parallel N] [--scratch <dir>] [--keep-scratch] [--accept <Bn>] [--json <file>|--csv <file>|--out <file>]`
`gridsim breaking-change import <dir|file.json>... [--journal <file.jsonl>]`
`gridsim breaking-change list [--since <version>]`

Re-run the jobs in your run journal on this build and see, bit for bit, what changed and which breaking change explains
it. The journal records each run you make while it is switched on (`--journal`, `GRIDSIM_JOURNAL` or the config file,
see Global switches and [ch. 3](<0003 Configuration.md>)). It is off by default, so switch it on before you upgrade:
the first useful `validate` compares the release you recorded with against the one you upgrade to.

`validate` reads every entry (or those the filters keep) in journal order. Each entry is re-run as a fresh
`gridsim` process of this build, in a scratch folder of its own, with its inputs read from where they were (or from the
snapshot store) and every output path rewritten into the scratch folder, so your original results are only read, never
written. Each output is then compared with the recorded one, field by field, bit for bit. Fields that carry only build
or run identity (the `tool` and `args` of a result file, a study's `provenance.engine`, timings) are left out; the list
is in technical chapter 42. Each entry gets one status:

| Status | Meaning |
|---|---|
| `IDENTICAL` | Same exit code, same output files, every compared field the same bits. |
| `CHANGED` | Something moved. The report names each field that moved, with the largest absolute and relative difference, the largest distance in ulps and the fewest agreeing bits (53 when identical, 52 for one ulp, 0 for a sign change), and checks it against the breaking changes (`B1`, `B2`, ...) that can explain it (the claim check, below). |
| `CANNOT-REPLAY` | It could not be re-run, with the reason: an input missing or changed since the run (including a file named inside a study, model assignments or plug-in configuration file), a job that would load plug-in code (`--model-plugins`, `user-model check-plugin`: a re-run never loads code), a console-only run (no result file), a verb that is never re-run (live, network, timing and hardware verbs, verbs that write outside their output flags, and the dynamics verbs listed below), a GPU run, a result file that changed since the run, or a re-run that newly exits 1 or 2 when no breaking change claims that refusal (one that does makes the entry `CHANGED` on `(exit code)` alone, explained by it). |
| `SKIPPED-SAME-BUILD` | Recorded by this very build (same version and commit). By determinism it cannot change, so it is not re-run. |

| Flag | Default | Meaning |
|---|---|---|
| `--journal <file.jsonl>` | the configured journal | The journal to read (`validate`) or append to (`import`). A network or device path exits 2; no journal at all exits 1. |
| `--since <version>` | all | Only entries recorded by this release or later (`2.1.090`; a `v` prefix and an `-RC-n` suffix are allowed). |
| `--verb <verb>` | all | Only entries of this verb (aliases resolve: `n-1` is `nminus1`). |
| `--case <glob>` | all | Only entries with an input whose path or file name matches the glob (`*` and `?`; ignoring case on Windows). |
| `--parallel N` | 0 (automatic) | Re-run up to N entries at once. Automatic is the smallest of the processor count, 80 % of installed memory in 256 MiB slots, and the number of entries. A job starts only when its memory estimate (1.25 x its recorded peak, at least 256 MiB) fits in 80 % of installed memory; a larger one runs alone. The report is the same for every N. |
| `--scratch <dir>` | a new folder in the temporary folder | Where the re-runs write. It must be new or empty, and not inside the journal's folder (exit 2 otherwise). |
| `--keep-scratch` | off | Keep the scratch folder (it is deleted at the end otherwise). |
| `--accept <Bn>` | none | Acknowledge the jobs of this run that are UNBOUNDED under Bn (only a breaking change with no bound, B3, can be accepted; any other ID exits 2). They are reported REVIEWED, and the acknowledgement is written to `<journal name>.accept.json` beside the journal. |

**The claim check.** Matching a breaking change is not enough to explain a change, or a second regression in the same
field could hide behind its ID. Each breaking change states what it does to the fields it moves: a bound (the largest
absolute or relative change of each value, from the magnitude measured in the CHANGELOG), an exact transform (new = f x
old, such as a sign flip or a ratio squared), or, where no bound means anything, the exact transition of a status or
the exit code (`Optimal` to `SecuredOutageFailsScreen`). Each changed field gets one verdict:

| Verdict | Meaning |
|---|---|
| `EXPLAINED (Bn)` | The field moved within what the breaking changes in its window claim. When several claim bounds on one field, their bounds add. |
| `EXCEEDS-CLAIM (Bn)` | The field moved, but beyond the claim: past the bound, off the transform, or through another transition. The report gives what was observed and what was claimed. |
| `AMBIGUOUS` | Several changes claim the field and their claims cannot be added (a transform or a transition beside another claim). |
| `UNEXPLAINED` | No breaking change in the window claims the field. |
| `UNBOUNDED (Bn)` | The field moved under a breaking change that has no bound (B3, below). Expected, but no number can check it: it needs your review. |
| `REVIEWED (Bn)` | An UNBOUNDED field you acknowledged with `--accept Bn`, that has moved no further since. |

An entry takes the worst verdict of its fields. Only an entry whose every changed field is EXPLAINED counts as
explained; EXCEEDS-CLAIM, AMBIGUOUS and UNEXPLAINED are flagged on the console and in the summary, and are likely a bug:
please report them. All of them are CHANGED, so the exit code is 3.

**Dynamics jobs.** `validate` re-runs `stability`, `dynamics-study`, `long-term`, `trip` and `modal`. Their result
files are the ones they write: `stability --events-out` (the RMS event export and its `run-metadata.json`) and
`--export` (the classical trajectory), the `dynamics-study --out` result, the `long-term --out` CSV (without `--out` the
CSV goes beside the input, so such a run is not re-run), and the `trip` and `modal` `--export` files. A run that printed
its result only is not re-run. The DYR or dynamics file of `--dyn-data` is an input like any other. The journal also
hashes the files an input names inside itself: a study file's RAW case, DYR, unit protection, device and curve files;
the user model files of `--model-assignments`; the assemblies of `--model-plugins`. With `--journal-snapshot` they are
kept in the snapshot store, apart from plug-in assemblies, which are code and are never copied there. On a re-run each
is read where it was if its bytes are unchanged; otherwise the input and every file it names are placed together in the
scratch folder, in the same layout, from the snapshot store. A file that is gone or changed with no copy makes the entry
`CANNOT-REPLAY: input missing: <file>` (or `input changed`). A plug-in job (`--model-plugins`, or `user-model
check-plugin`) is never re-run: a journal line or an imported result file carries its own command line, so a plug-in
configuration and its assembly could arrive with it, and a re-run never loads code. Such an entry is `CANNOT-REPLAY: not
replayable: --model-plugins loads plug-in code, and a re-run never loads code` whatever the state of its assemblies, and
nothing is loaded; run the job again by hand to compare it. These dynamics verbs are
never re-run, and the entry says why: `transient`, `emt`, `replay-event`, `scenario`, `h0-study` and `evidence` (their
result files carry wall time or the run header, and markdown is compared byte for byte), `impedance-stability` (its
export carries the time and host), `replay` and `fit-frequency` (no result file) and `fleet` (not yet shown to give the
same result twice). The run header lines at the top of a result CSV and the host, build and time fields of
`run-metadata.json` are left out of the comparison; the run description and the provenance line are compared. A
`stability` job given its case folder with a relative `--dir` names the folder in its run description as typed, and the
re-run is given its full path, so that job shows CHANGED on the run description (the `run` field and the `# run:` line)
with nothing else moved. Give the folder as a full path, or the case positionally, and the job replays IDENTICAL. A
plug-in assembly is never taken from the snapshot store, whatever the journal says.

**B3 and `--accept`.** B3 (with `--dyr-extended --allow-unmodelled`, a PSS/E DYR CSVGN1 record is now modelled as an
SVC) has no bound: how far the CSVGN1 control moves a trajectory depends on the unit's data and the disturbance. It
appears only for a job recorded before 2.1.083 whose command line holds both `--dyr-extended` and
`--allow-unmodelled` and whose DYR holds a CSVGN1 record: in practice a `stability`, `trip` or `modal` job with a
`.dyr` file as its `--dyn-data`. The DYR is always inspected: one that cannot be read makes the job CANNOT-REPLAY
before it is re-run, so B3 is never assumed. A `dynamics-study` job is never raised: that verb refuses
`--allow-unmodelled` and reads its DYR without the extended library. A job with no DYR input at all (a `pf` run, or one whose `--dyn-data` is a JSON file) read no DYR,
so B3 cannot have acted and is not raised. Any other job is judged without B3. For such a job, every field the other changes do not explain is UNBOUNDED, and the entry reads
`UNBOUNDED (B3)` once, with each moved field and its size. Nothing else wrong, the exit code is 4 (review needed).
Look at the fields; when you are content, run `validate --accept B3`. Those jobs then read REVIEWED, exit 0, and the
acknowledgement is kept in `<journal name>.accept.json` beside the journal. Later runs read REVIEWED only for the same
job with every field moved no further than accepted; a new job, a new field or a larger movement reads UNBOUNDED
again. B27 (from 2.1.117, EXDC2, IEEEX1, ESAC8B, ESST3A and IEEEG3 records modelled under the same two options)
follows the same rules, with those five records in place of CSVGN1. B28 (an RMS run refused when a model that starts
from the first step's measurements meets a disturbance at t = 0) is reached through the library API only: every
`stability` disturbance starts after the first step (a `--events` time of 0 is refused) and `dynamics-study` refuses
an event at 0, so it explains no journal job.
B33 (from 2.1.144, the DC model of MATPOWER's `makeBdc`) has no bound either: how far a tap ratio, a phase shift or
a bus Gs moves a DC result depends on the whole network. It appears only for a job that runs the DC model on a case
that has one of those terms: an in-service tap other than 0 or 1, a non-zero phase shift, or a bus Gs. A case with none
is computed exactly as before, so a change there is not B33's and reads UNEXPLAINED. The case is the job's case
argument (a case file, a case folder or a built-in name), or the case its study file names for `transmission-security`,
`transfer-capability`, `corrective-sequence` and `probabilistic`, loaded where the re-run loads it; a case that cannot
be read makes the job CANNOT-REPLAY before it is re-run. The AC modes of B33's verbs never raise it: `nminus1` (an AC
study in every mode) and `sensitivity --kind tap`, `dvdq` or `effectiveness`. A `planning-study` job is raised whatever
its master case holds, because its projects can add or change branches.

The entries are re-run in parallel, longest recorded run first; each re-run is single-threaded and is killed after ten
times its recorded run time (at least 60 s). A re-run that crashes or is killed is run once more on its own after the
others, and only that result counts. Progress (`[n/N] seq S done`) goes to stderr as runs finish; the report lists the
entries in journal order and never contains the parallelism, timings or scratch paths.

Exit codes: 0 when every entry is IDENTICAL, REVIEWED or skipped as the same build; 3 when anything else CHANGED; 4
(review needed) when the only changes are UNBOUNDED; 1 when anything cannot be re-run (this wins over 3 and 4, as in
every batch verb, and 3 wins over 4); 2 on a usage error, or when the filters name nothing.

The report (`--json`, `--csv`, `--out`) has the summary `changed_unexplained`, `changed_exceeds_claim`,
`changed_ambiguous`, `journal_entries`, `selected`, `identical`, `changed`, `changed_explained`, `cannot_replay`,
`skipped_same_build`, `build_version`, `oldest_recorded_version`, `map_sha256`, `changed_unbounded`,
`changed_reviewed` and `accepted_now`, and the tables `reviews` (one row per UNBOUNDED or REVIEWED job: accepted now,
accepted earlier, or needs review with the reason), `entries` (one row
per entry, with its status, claim check, reason, explaining IDs and worst field), `field_differences` (one row per
field that moved, with its verdict, the claim and the observation) and `breaking_changes` (how many entries each
breaking change explains, and how many it was matched to but did not cover).

```
gridsim --journal work/journal.jsonl nminus1 case14 --json out/n1.json
# ... upgrade GridSim ...
gridsim breaking-change validate --journal work/journal.jsonl --json validate.json
breaking-change validate on 2.1.104: unexplained=0 exceeds-claim=0 ambiguous=0 identical=1 changed=0 cannot-replay=0 skipped-same-build=0
  seq 1 nminus1 (2.1.104): IDENTICAL
```

`import` builds journal entries from result files you already have (`gridsim-cli-result/1` documents written with
`--json`), so results kept from an earlier release can be checked at once. Each document records its verb, command line
and exit code. The inputs are found at the recorded paths and hashed when you import, not when the run happened, and
the entry says so; an input that is gone makes that entry CANNOT-REPLAY. A file that is not such a document is skipped.
Exit 0 when at least one file was imported, 1 otherwise.

`list` prints the breaking-change map built into this release: each ID, the release it landed in, its kind, title and
scope (the verbs, options and input formats it can affect). Its claims are in the `--json` form of the map file,
`src/GridSim.Core/Journal/breaking-changes.json`. `--since` keeps those after a release.

The journal is a convenience log, not evidence: it is a plain file anyone with access can edit. For a tamper-evident
record use `--audit-log`. A re-run proves bit identity on this build and this machine; it does not validate results
against the real grid.

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
