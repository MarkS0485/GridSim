# 30. Troubleshooting

This chapter is a lookup table for things that go wrong. Find the exit code or the message you saw, read the cause, and
do what the fix says. Every message below is quoted from a run of the current build, and every output block is a
captured run. Captures show paths relative to the repository root where the real message prints the full absolute path. Timings, stamps and horizon dates inside the captures differ by machine and by run. Detail about a
feature lives in its own chapter, and each entry links there instead of repeating it.

Find your symptom:

| You saw | Go to |
|---|---|
| A number in `$?`, `%ERRORLEVEL%` or `$LASTEXITCODE` | [Exit codes](#exit-codes) |
| `MSB3027`, `error CA...`, a test that fails in one CPU mode, a version that does not match | [Build and test problems](#build-and-test-problems) |
| `unknown command or case`, `not found`, `case folder not found`, a refused `--dir`, a bus limit | [Loading networks](#loading-networks) |
| `DID NOT CONVERGE`, `did not converge` from a study, `Taps NOT SETTLED` | [Power flow does not converge](#power-flow-does-not-converge) |
| `ISLANDED`, `NO SOLUTION`, `solver error:`, `found no eligible generator` | [Contingency](#contingency) |
| `No synchronous machines with inertia`, `REJECTED (forward inference)` on `freq` or `replay`, `--trip-at` refused | [Dynamics and stability](#dynamics-and-stability) |
| `emt:` messages, a run that grows without bound | [Electromagnetic transients](#electromagnetic-transients) |
| A harmonics verb that stops with `error: harmonics:` or `harmonics: FAILED` | [Harmonics](#harmonics) |
| `UNOBSERVABLE`, `consistency: ... FAIL`, `--hti could not identify` | [State estimation](#state-estimation) |
| `GDA root error`, `REJECTED (forward inference)`, a status pill that turns red | [GDA and the bridge](#gda-and-the-bridge) |
| `interpose` silent, `map-errors`, `windows-written 0` | [Interposer](#interposer) |
| A web page that loads and does nothing, a missing `Live bridge` source, an app that closes with a message | [Web host and desktop app](#web-host-and-desktop-app) |
| `[gpu] no usable gpu device found` | [GPU fallback](#gpu-fallback) |
| `gridsim: internal error - this is a defect in GridSim` | [Crash logs](#crash-logs) |

## Exit codes

Every verb ends with one of the codes below. A script gates on the code, and the table holds across all verbs; the
verb's own section in [ch. 19, CLI reference](<0019 CLI reference.md#parsing-rules-and-exit-codes>) lists its exceptions.
The command line tool sets no other value. Read the code from `$LASTEXITCODE` in PowerShell or `%ERRORLEVEL%` in `cmd`.

| Code | Meaning | What to do |
|---|---|---|
| `0` | The run finished and produced its report. A finding is not always a failure: a chi-square `FAIL` on `estimate`, an `UNSTABLE` verdict on `impedance-stability` and a `--gpu` fallback to the CPU all exit 0. | Read the report as well as the code. |
| `1` | Input error, for example a file or folder that is missing or unloadable, invalid data in a case, an invalid measurement set, a result file that cannot be written, an `--audit-log` file that cannot be opened or verified. Also a failed check, for example: `physics-check` or `conformance` gate, a right-half-plane eigenvalue in `modal`, a singular harmonic network, `batchsolve` off the CPU reference. | Read the message on standard error; it names the file, flag or check. |
| `2` | Usage error: an unknown flag or verb, a flag another verb owns, a malformed or missing flag value, an unknown case name (on every verb), a bus id that is not in the case, a case over the licence bus limit, a key that `licence import` rejects, or `REJECTED (forward inference)` for data newer than 169 hours. | Fix the command line, the case name or the data date. |
| `3` | A finding, for example a power flow that did not converge (on any verb except `convert`, whose result is the round trip: a case that does not converge, converted bit-identically, exits 0), a baseline that is not `Reproduced`, an estimate that is not converged and observable, a `crossval` mismatch, a contingency verdict that is not secure, a failed `gda-replay` step, an EMT numerical failure, a carve that selects no element, an infeasible `dcopf` dispatch, a discarded interposer window, a failed probe in `gpucheck` or `bench`. | The run completed and the answer is a failure. Read the report; do not use its numbers as results. |
| `77` | `gridsim crossval` only: the check never ran because there is no Python interpreter or no pandapower. | Install Python and `pandapower` (see [ch. 27](<0027 Validation tools.md>)); 77 is a skip, not a pass. |
| `-532462766` (`0xE0434352`) | An unhandled exception: a defect in GridSim. This is the code the .NET runtime gives a crashed process on Windows, and the desktop app exits with the same code after its crash message. The run is never reported as success. | Keep the crash log and report it ([Crash logs](#crash-logs)). |

On Linux and macOS an unhandled exception ends the process with the runtime's abort status instead, which is a
non-zero code that is not in this table. A script treats any code outside 0, 1, 2, 3 and 77 as a crash.

The missing-file case is a one-line message and exit 1, not a crash. An unknown case name is exit 2 on every verb,
and a named case file or folder that is missing or cannot be loaded is exit 1 (see [Loading networks](#loading-networks)).

Two exit 2 messages name the flag. A misspelt flag, and a flag that belongs to another verb, are refused before
any work starts. A value that does not parse is refused the same way, and an absent value (a shell variable that
expanded to nothing) reads `expected <kind> but got ''`:

```
$ gridsim case14 --once --bogus
error: unknown flag '--bogus' (misspelled?). Run 'gridsim --help' for the verb list, or see docs/manual/0019 CLI reference.md.
[exit 2]
```

```
$ gridsim case14 --ticks 5O
error: --ticks expected an integer but got '5O'
[exit 2]
```

## Build and test problems

### `MSB3027` or `MSB3021` copy errors during `dotnet build`

Cause. The desktop app is running, especially under the Visual Studio debugger, and holds its output DLLs open. Any
rebuild that re-links a downstream assembly of `GridSim.Core` fails to copy over them.

Fix. Stop the debugger, or `taskkill /F /IM GridSim.Wpf.exe` (write `//F //IM` in Git Bash), then rebuild. To
compile-check without touching the running app, build to a scratch folder with `-p:BaseOutputPath=obj/verifybin/`,
delete the folder afterwards and never launch what it produces. See
[ch. 1, The rebuild file-lock gotcha](<0001 Getting started.md#the-rebuild-file-lock-gotcha>).

### A build error `error CA...` or `error CS...` on a warning

Cause. `Directory.Build.props` sets `TreatWarningsAsErrors` and `AnalysisMode` to `Recommended`. The repository builds at
0 warnings and 0 errors, so any warning stops the build.

Fix. Fix the warning in the code. Do not suppress it inline. A rule that is noise for numerical code is tuned in the
repository-root `.editorconfig`.

### A test fails in one CPU mode only

The suite runs in three modes that differ only in the vector instructions the runtime may use: normal,
`DOTNET_EnableAVX2=0` (the 128-bit SSE kernels) and `DOTNET_EnableHWIntrinsic=0` (the scalar fallback). The expected
values are the same in all three ([ch. 1, The three modes](<0001 Getting started.md#the-three-modes>)).

Cause. A test that passes in normal mode and fails in `avx2-off` or `hwintrinsic-off` has found a kernel whose result
depends on the vector width. That is a defect in the code under test, not in the test and not in your machine.

Fix. Reproduce it by setting the variable for the test run only, because the build is always done in normal mode:

```bash
DOTNET_EnableAVX2=0 dotnet test GridSim.slnx -c Release --no-build
```

Report the failing test name, the mode, and the output of `gridsim simdcheck` for the same environment. Never edit or
loosen the test.

### `simdcheck: FAILED - expected at least Avx512, but the active tier is Avx256.`

Cause. `gridsim simdcheck --expect <tier>` exits 3 when the active vector tier is narrower than the one named. On a CPU
without AVX-512 that is correct.

```
$ gridsim simdcheck --expect Avx512
...
If this reports Avx256 on a box you expect to be AVX-512, the CPU or the runtime is capping the width.
simdcheck: FAILED - expected at least Avx512, but the active tier is Avx256.
[exit 3]
```

Fix. On a CPU that has AVX-512F, `simdcheck` prints the cure: set `DOTNET_PreferredVectorBitWidth=512` before start-up.
The tier changes the speed; the three-mode suite above pins the numbers. See [ch. 3, CPU vector tier](<0003 Configuration.md#cpu-vector-tier>).

### Tests or `gridsim` report the old version after a version bump

Cause. The release number lives in `Directory.Build.props` and is stamped into every assembly at build time. A bump
without a rebuild leaves the old stamp in the binaries. `dotnet test --no-build` then fails
`Core_build_info_reports_the_props_version` (the test reads the number from `Directory.Build.props` and compares it with the
stamp compiled into the old assemblies), and `gridsim --version` prints the old number:

```
$ gridsim --version
gridsim 1.95.467+203c6b9cc0edff55f2bd73642c94dc0963a82b92
[exit 0]
```

Fix. Rebuild everything with `dotnet build GridSim.slnx -c Release`, then run the tests again with `--no-build`. The
number before the `+` is the release and the hash after it is the commit the binary was built from; both go in a
problem report ([How to report a problem](#how-to-report-a-problem)).

## Loading networks

The loaders, the case names and the folder layouts are in [ch. 2, Loading networks](<0002 Loading networks.md>).
The rules for the case argument are in [ch. 19, Case arguments](<0019 CLI reference.md#case-arguments>).

### `error: unknown command or case '<name>'.`

```
$ gridsim nosuchcase --once
error: unknown command or case 'nosuchcase'.
Run a case (case9|case14|case30|case57|case118|case300|case145|case24|cigre|uk), a .m file,
or a verb (transient, stability, modal, replay-event, scenario, fleet, evidence, gda-replay, crossval, estimate, bench, ...).
[exit 2]
```

Cause. The first word is neither a verb, a built-in case name nor a path ending in `.m`. Exit 2.

Fix. Use one of the listed names, a `.m` path, or `--dir <folder>`. A verb that takes a case prints its own message with
the full list of names (`error: screen: unknown case '<name>' (built-in names: ...)`, exit 2).

```
$ gridsim screen nosuchcase_xyz
error: screen: unknown case 'nosuchcase_xyz' (built-in names: case118, case14, case145, case24, case30, case300, case57, case9, cigre, gb, gb-full, gb-network-2224, gb-reduced, gb-reduced-29, gb-synthetic, ieee9, pypsa-gb-reduced, pypsa-gb-zonal, rts, synthetic, uk; or a case folder, or a MATPOWER .m file, or a JSON v2 case manifest .json)
[exit 2]
```

Every verb that takes a case follows one rule ([ch. 19, Case arguments](<0019 CLI reference.md#case-arguments>)): an
argument that names no case at all (not a built-in name, not an existing folder, not a `.m` or `.json` path) is a usage
error, exit 2, with the message naming the case and listing the built-in names. `trip` reads an unmatched word as a
generator name and exits 2 listing the generators; `export` reads it as an output path and exits 2. In a batch run over
several cases (`nminus1-sweep --all`, `stress --all`, `transient --cases`, `bench`) a case that does not load is a
`LOAD FAIL` row and the run exits 1 at the end. The verbs whose argument is a file rather than a case name (`dist3ph`,
`zonal`, `validate`, `pmu-ingest`) report a missing one as an input error, exit 1.

### `error: MATPOWER case file not found: '<name>.m' (full path ...).`

```
$ gridsim nosuch.m --once
error: MATPOWER case file not found: 'nosuch.m' (full path nosuch.m).
[exit 1]
```

Cause. A name ending in `.m` is looked up as a path, then as a path under `data/matpower/`, and for a bare file name
such as `case33bw.m` in the `data/matpower/` subfolders in sorted order. The message is the same for a path and for a
bare name that matched nothing. Exit 1.

```
$ gridsim nosuchbare.m --once
error: MATPOWER case file not found: 'nosuchbare.m' (full path nosuchbare.m).
[exit 1]
```

A bare name that matches two files in different subfolders is refused with exit 2 and the list of candidates; give the
relative path. No two shipped files share a name.

Fix. Check the spelling and the working directory (a relative path starts at the directory you ran the command in).

### `error: case folder not found: '<folder>' (full path ...).`

```
$ gridsim --dir nosuchdir --once
error: case folder not found: 'nosuchdir' (full path nosuchdir).
[exit 1]
```

Cause. `--dir` names a folder that does not exist. Exit 1. The verbs that choose their network through `--dir` or a
positional case add their name: `error: nminus1: case folder not found: ...`. A `--dir` with no value exits 2. A folder
that exists but is not a case is also exit 1:

```
$ gridsim --dir docs --once
error: cannot load case folder 'docs': buses.json is empty or missing.
[exit 1]
```

Fix. A case folder holds `buses.json` and `generation.json` at least ([ch. 2, JSON case directories](<0002 Loading networks.md#json-case-directories>)).
A JSON file with a misspelt key stops the load and names the file and the key.

### A positional case and `--dir` disagree, or one is ignored

Which of the two a run honours depends on the verb. This is the state of the build, verb by verb.

**Sixteen verbs take both and refuse a conflict.** `trip`, `screen`, `nminus1`, `nminus2`, `n-1-1`, `domains`, `export`,
`nminus1-sweep`, `stress`, `chain`, `cycle`, `emt`, `harmonics`, `impedance-stability`, `stability` and `modal` take
the network as a positional case or as `--dir <folder>`. Both together exit 2 unless they name the same folder
(`uk --dir data/uk`), and a second positional case exits 2.

```
$ gridsim nminus1 case14 --dir data/uk
error: nminus1: a positional case 'case14' and --dir 'data/uk' were both given and name different networks. Give one of them.
[exit 2]
```

**The bare run, with no verb, takes one case and refuses a second.** `gridsim case14 --dir data/uk --once` names the
`--dir` it would ignore, and `gridsim --dir data/uk case14 --once` names the `case14` it would ignore. Both exit 2.

```
$ gridsim case14 --dir data/uk --once
error: the case run (no verb) takes one case and the global switches only, so '--dir' would be silently ignored. A flag of a verb needs that verb first (gridsim <verb> <case> ...); run 'gridsim --help' for the verb list, or see docs/manual/0019 CLI reference.md.
[exit 2]
```

```
$ gridsim --dir data/uk case14 --once
error: the case run (no verb) takes one case and the global switches only, so 'case14' would be silently ignored. A flag of a verb needs that verb first (gridsim <verb> <case> ...); run 'gridsim --help' for the verb list, or see docs/manual/0019 CLI reference.md.
[exit 2]
```

**Every other verb refuses `--dir`** with exit 2 and names the flag, so a `--dir` is never silently dropped there:

```
$ gridsim dcopf --dir data/uk
error: dcopf does not take --dir (it is not one of dcopf's flags, so it would be silently ignored). Run 'gridsim dcopf --help' for the flags dcopf takes, or see docs/manual/0019 CLI reference.md.
[exit 2]
```

The exception is `h0-study`, whose `--dir` names the case folder it studies (default `data/gb-synthetic`). The short
form `-d` is an alias of `--dir` for `harmonics`, `impedance-stability` and the first word of the bare run.

Fix. Give one network, in the form the verb takes.

### `error: This network has N buses; your Personal licence allows 1,500. ...`

Cause. A network that is not bundled has more buses than the installed licence tier allows. The free Personal tier
allows 1,500. The bundled cases (the built-in names and the shipped files of the program's own `data/matpower`,
`data/uk`, `data/gb-synthetic`, `data/gb-full`, `data/intl` and `data/dist3ph` folders, checked against the manifest
`data/bundled.sha256`) are never counted, so a copy of a bundled file anywhere else, or an edited or added file in a
bundled folder, is counted. The refusal is exit 2 in every verb that loads a user network. This capture
runs a copy of `case9241pegase.m` kept outside `data/`, with the licence forced to the Personal tier by a rejected key
in `GRIDSIM_LICENCE` (a key that is present but rejected leaves the run on the free tier):

```
$ gridsim results/trouble-tmp/mycase9241.m --once
error: This network has 9,241 buses; your Personal licence allows 1,500. A larger or commercial licence removes the limit: gdatooling@proton.me
[exit 2]
```

Fix. Check the active tier with `gridsim licence status`. A larger or commercial licence is installed with
`gridsim --importLicence <file>` ([ch. 4, Licensing and activation](<0004 Licensing and activation.md>)). An
expired licence falls back to the Personal tier and the message names the expiry date.

## Power flow does not converge

### `DID NOT CONVERGE` and exit 3

```
$ gridsim docs/_captures/opf/overloaded-case9.m --once
GridSim  -  overloaded-case9
9 buses  9 branches  3 generators  base 100 MVA  50 Hz
========================================================================
DID NOT CONVERGE (30 iters, max mismatch 7.45E-001 p.u.)
...
[exit 3]
```

Cause. The solver reached its iteration cap, met a NaN, or declined the network. Every verb whose power flow does not
converge exits 3. The tables of a non-converged report are not a solution: do not read voltages from them. The generation
line reads `Generator output n/a (not converged)`.

What to check, in order:

1. An unsupplied island: a bus or group of buses with load and no path to a generator or slack.
2. A generator set-point the network cannot support, or a load beyond what the network can carry. The case above is
   `case9` with its loads multiplied by 2.5.
3. The engine. Radial feeders need the backward/forward sweep, and a meshed network needs Newton-Raphson.
4. Whether the input is one that is expected not to converge (the next entries).

[ch. 5, Running power flows](<0005 Running power flows.md>) explains the report. Pass `--once` in scripts: a bare
`gridsim <case>` runs the live view and waits for a key press, which a process with redirected input never receives.

### A meshed case diverges under `--bfs`

Cause. `--bfs` forces the backward/forward sweep (`BackwardForwardSweep`, `src/GridSim.Core/PowerFlow/BackwardForwardSweep.cs`).
The sweep needs one slack and a connected in-service network. It solves a radial feeder and a weakly meshed feeder with
up to six independent loops (`MaxCompensatedLoops`), and declines a network with more. `case14` has 7 independent loops, so
the sweep stops at once:

```
$ gridsim case14 --once --bfs
GridSim  -  IEEE 14-bus
14 buses  20 branches  5 generators  base 100 MVA  50 Hz
========================================================================
DID NOT CONVERGE (0 iters, max mismatch Infinity p.u.)
...
Protection simulation: skipped - the power flow did not converge, so there is no state to assess.
[exit 3]
```

The sweep solves the same problem as Newton-Raphson. It draws ZIP loads at the swept voltage, honours the flat-start,
Q-limit and cancellation options, and keeps the phase shift of a branch stored with tap 0 (a ratio of 0 or below means 1).
Without a flag, the sweep is chosen automatically when the model is a connected tree with one slack
(`SolverSelector.LooksRadial`, exactly buses - 1 in-service branches).

Fix. `gridsim <case> --once --bfs` for a feeder, no flag for a meshed network.

### A `pglib_opf_case*` does not converge

Cause. PGLib-OPF files are optimisation solutions, not valid flat-start power flows, and some do not converge as a power
flow. Among the shipped files `pglib_opf_case3_lmbd`, `pglib_opf_case162_ieee_dtc`, `pglib_opf_case240_pserc` and
`pglib_opf_case588_sdet` fail. The bare run caps at 30 iterations (`gridsim data/matpower/nesta/pglib_opf_case3_lmbd.m
--once` prints `DID NOT CONVERGE (30 iters ...)`), and `bench` and the conformance runs cap at 100. This is a property of
the input.

Fix. Accept the `NO` or `DID NOT CONVERGE` ([ch. 28](<0028 Benchmarking and GPU.md>) and
[ch. 27](<0027 Validation tools.md>)).

### `Dense engine reports OOM on a large case`

Cause. The dense engine stores n x n matrices, so a case of thousands of buses needs gigabytes for the matrix alone. Every
default CPU solve path selects the sparse engine (`SolverSelector.DefaultSparseThreshold` is 0). The dense engine is used
where it is forced, such as the `cpu-dense` column of `bench`, on a GPU backend up to 200 buses
(`SolverBackend.SparseThreshold`), and in the `--enableExport` solve up to 200 buses (`SolverBackend.ExportSolver`).

Fix. Use the sparse engine. Cap the heap with `DOTNET_GCHeapHardLimit` so a sweep fails with a catchable error instead of
exhausting the machine.

### `<study>: the power-flow state it starts from did not converge (status ..., max mismatch ... p.u. after N iterations)`

Cause. Fault analysis, protection assessment and cascade, loss-contingency sweeps, small-signal assembly, subtransient
initialisation and the RMS transient studies refuse a non-converged starting state and exit 3. They do not compute a
result from it.

```
$ gridsim fault docs/_captures/opf/overloaded-case9.m --bus 5
...
fault: the pre-fault power flow did not converge (MaxIterations, mismatch 4.72E-001); no fault study is possible.
[exit 3]
```

Fix. Make the base case converge first, then re-run the study. The `note:` lines before the message are the MATPOWER
reader's notes and do not affect the run.

### `Taps NOT SETTLED: ...` under a converged report

Cause. The line is printed only for a converged solve. The on-load tap changer loop hit its pass cap with taps still
moving, or has tap changers at the end of their range with the controlled bus outside its deadband. The line names the
changers at their limits. A non-converged solve never steps a tap, and its report reads `DID NOT CONVERGE` instead.

Fix. Treat the voltages as not regulated. Fix the base solve, or inspect which transformers are hunting.

### A Q-limit study reports `MaxIterations` although the last Newton pass converged

Cause. The Q-limit outer loop hit `MaxQLimitPasses` (default 12) right after a bus changed type. The engines solve once
more and report non-convergence rather than accept voltages computed with the previous bus types.

Fix. Raise `MaxQLimitPasses` (a value below 1 is refused), or look for a bus that oscillates between PV and PQ.

### A case that converged before no longer converges

Cause. A regression, or a changed input.

Fix. Run `gridsim crossval <case>` and `tools/crossval/residual_check.py` on the export before touching solver code;
they show whether the last good solution was real ([ch. 27](<0027 Validation tools.md>)). Exit 77 from
`crossval` means pandapower is missing and the check did not run.

## Contingency

The screens are in [ch. 11, Contingency analysis](<0011 Contingency analysis.md>). The exit code is part of the
verdict: `nminus1`, `nminus2`, `n-1-1`, `nminus1-sweep` and `screen` exit 3 when anything is not secure, and a script that
only wants the table ignores it.

```
$ gridsim nminus1 case14
GridSim  -  N-1 branch screening: IEEE 14-bus
==========================================================================
Base case: 1 pre-existing violation(s), reported once here and not counted against any outage: overvoltage 1.090 p.u.
Outage                          Status        MaxLoad  WorstBnd  Vmin   Vmax
--------------------------------------------------------------------------
1-2                             NO SOLUTION   no solution
9-10                            VIOLATIONx1       0%       0%  1.010  1.090
...
20 outages screened  16 secure  3 with violations  0 islanded  1 not solved
(16 of the secure outages are 'base only': they add no violation, but the base case's own violations remain in their state)
Verdict: NOT SECURE, 1 base-case violation(s) and 4 insecure outage(s) (exit 3).
[exit 3]
```

### `ISLANDED`, `NO SOLUTION` and `solver error:` rows

- `ISLANDED` means the outage separated part of the network from the slack bus.
- `NO SOLUTION` means the network stayed in one piece and the power flow found no converged state. A voltage collapse is one cause.
  The capture above reads `1 not solved` for the outage of branch 1-2 on `case14`.
- `solver error: <type>: <message>` is the text of the violation entry of an outage for which the solver threw. The Status
  column reads `ISLANDED` when the outage split the network and `NO SOLUTION` when it did not.

Fix. Read the status before reading the loading columns of that row.

### `base only` is not violation-free

Cause. A violation the base case already has is printed once at the top and is not counted against any outage, so an
outage row reads `base only` while the base violation remains in its state.

Fix. Read `Vmin` and `Vmax` on `base only` rows. The verdict still fails because the base case violates.

### `trip found no eligible generator to trip in <case>`

```
$ gridsim trip case14
error: trip found no eligible generator to trip in IEEE 14-bus (no non-slack synchronous unit). Name one explicitly, or trip a different model.
[exit 2]
```

Cause. `trip` needs a non-slack synchronous unit with inertia data. MATPOWER cases carry no inertia, so `case14` has
none. Exit 2.

Fix. Use a case that carries inertia (`case9`, `uk`) or name the unit. One word after `trip` is read as the case when it names one (`gridsim trip case14`). Any other
word is read as a generator name on the default `data/uk` model, and a name that matches nothing lists the generators:

```
$ gridsim trip nosuchcase_xyz
...
[exit 2]
```

Write `gridsim trip <case> <gen>` to name both.

### `error: stress --from (2) is above --to (1); the load range is empty ...`

```
$ gridsim stress case14 --from 2 --to 1
error: stress --from (2) is above --to (1); the load range is empty and nothing would be solved.
[exit 2]
```

Cause. `stress --from` above `--to` leaves an empty load range and exits 2. A second case argument to `screen`,
`nminus1`, `nminus2`, `n-1-1` or `domains` exits 2 (`error: <verb> takes at most one case argument but got: ...`).

Fix. Correct the range, or give one case.

## Dynamics and stability

### `stability: No synchronous machines with inertia: the classical stability model has no rotor to integrate.`

```
$ gridsim stability case14
error: stability: No synchronous machines with inertia: the classical stability model has no rotor to integrate.
[exit 2]
```

Cause. The case carries no inertia data. Every generator of the built-in MATPOWER cases loads with `H = 0`, and only
`case9` carries `H` (the published WSCC set). The classical model holds a machine with no inertia, nothing is left to
integrate, and the verb exits 2. `modal` exits 2 for the same reason with a longer message. Nothing in GridSim invents
an inertia for these verbs.

Fix. Use a case that carries inertia ([ch. 15, RMS, modal and impedance stability](<0015 RMS, modal and impedance stability.md>)).
The live simulator and `transient` substitute a nominal `H = 4 s` on the fleet's machine base instead.

### `error: stability: fault bus N is not in case '<name>'.`

Cause. `--fault-bus` names a bus the case does not have. The run stops with exit 2 before any solve, on the classical
engine, `--rms` and `--screen` alike. `fault --bus`, `protection-check --fault-bus`, `iec60909 --bus` and a `--bess`
site follow the same rule.

Fix. Use a bus id the case prints.

### `error: --avr and --pss apply to the RMS engine only (add --rms).`

Cause. `--avr` or `--pss` without `--rms`. Exit 2. `--pss` without an exciter exits 2 with
`error: --pss needs an AVR (--avr sexs or --avr ieeet1).`

Fix. Add `--rms`, and give `--avr sexs` or `--avr ieeet1` with `--pss`.

### `impedance-stability` exits 0 on an `UNSTABLE` verdict

Cause. The exit code reports that the scan completed. `modal` is the reverse: exit 1 is a right-half-plane eigenvalue, a
result, and the export is still written.

Fix. Read the `-> UNSTABLE` text. A flag the model cannot use (for example `--pll-damping` with `--converter gfm`, or an
SCR flag with `--grid real`) exits 2 and names the flag.

### `REJECTED (forward inference)` from `freq`, `replay` or an event verb

Cause. A row, an event or a trace sample is dated within 169 hours of now. GridSim replays history only; it never
processes data newer than now minus 169 hours. Every verb that reads dated data refuses it the same way, with
`REJECTED (forward inference): ...` and exit 2: `freq`, `replay`, `replay-event` (an event timestamp or a `--trace`
row), the counterfactual verbs, `estimate`, `pmu-ingest` and the rest.

```
$ gridsim replay-event example-recent --catalog results/recent-event.json --out results/replay-example-recent
REJECTED (forward inference): event 'example-recent' timestamp 2026-10-02T12:00:00.0000000Z is newer than the historical horizon 2026-09-26T07:16:48.3517197Z (data must lag real time by >=169 h). GridSim replays history only - it never processes recent or future data, so it cannot forward-infer.
[exit 2]
```

Fix. Use data that is at least 8 days old. Remove the recent rows, or correct the dates.

### `error: frequency file '<file>' holds no data rows ...` or `... line N is not a 'timestamp,Hz' data row ...`

Cause. The file is empty or holds only a header, or a line after the header is not a timestamp, a comma and a frequency
above 0 Hz. `freq` exits 1 on either; `replay` prints `No data.` for an empty file and exits 1.

Fix. Repair or replace the file; the message names the line.

### `--trip-at` is refused or ignored

```
$ gridsim transient case9 --minutes 0.1 --dt-ms 100 --trip 100 --trip-at 30
[transient] case9: --trip-at 30s is past the run end (0.1 min = 60 slices @ 100ms) - the contingency never fires. Aborting.
[exit 2]
```

Cause. `--trip-at` past the end of the run exits 2, because the contingency would never fire. `--trip-at` without
`--trip MW` above zero is ignored. `transient` with no flags is a 60-minute run at 1 ms, which is 3.6 million solves.

Fix. Set `--minutes` and `--dt-ms` for the question you are asking. See
[ch. 14, Frequency studies](<0014 Frequency studies.md>).

### An event verb exits 3 with a complete report

Cause. The baseline replay is `Marginal` or `Failed`, so the counterfactual verbs refuse the event (`GATE FAILED`). The
`Marginal` verdict on `gb-2023-12-22` is a finding about the model.

Fix. Run `gridsim replay-event <id>` to see why. Read the exit code, not just the report. See
[ch. 18](<0018 Replaying recorded events.md>) and [ch. 20](<0020 Counterfactuals.md>).

## Electromagnetic transients

The `emt` verb is in [ch. 16, Electromagnetic transients](<0016 Electromagnetic transients (EMT).md>).

### `emt: EMT build: branch #N (a->b) is a phase-shifting transformer ...`

```
$ gridsim emt cigre --duration 0.01
emt: EMT build: branch #12 (0->1) is a phase-shifting transformer (shift 30 deg). A phase shift multiplies the voltage phasor by a complex number, which a real-valued single-phase time-domain network cannot represent; it would be silently dropped. Remove the shift or use a phasor study. (Parameter 'model')
[exit 2]
```

Cause. A phase shift multiplies a voltage phasor by a complex number, which a real-valued single-phase time-domain network
cannot represent, so the build refuses it. The CIGRE MV benchmark and the PEGASE cases carry one. Exit 2.

Fix. Remove the shift, or use a phasor study.

### `emt: EMT element ... has a non-finite companion conductance (Infinity) ...`

```
$ gridsim emt case14 --event fault:5@0.01:0 --duration 0.02
error: emt: 'fault:5@0.01:0': fault resistance 0 pu must be a positive number (a bolted fault is a small one, for example 1e-4)
[exit 2]
```

A `fault:` resistance of 0, a negative one or a non-finite one is refused before the network is built, as above,
exit 2. The heading's message appears when some other element's resistance is zero or cancels its companion
resistance (exit 3).

Cause. A zero-resistance element is an infinite conductance.

Fix. Give a positive `Rpu` (a small one, such as `1e-4`, for a bolted fault).

### `error: emt: --duration expected a positive number of seconds but got ...`

Cause. A `--duration` that is zero, negative or not a number. Every such value is refused before the run, naming the
flag, exit 2.

Fix. Give a positive `--duration`.

### Other `emt:` messages

| Message | Cause |
|---|---|
| `error: emt: probe '<token>': ...` (exit 2) | A `--probe` that is not `v:<bus>` or `i:<from>-<to>[#k]`, a bus the case does not have, or a branch that is missing, ambiguous or has no `#k`. Every bad probe is listed, one line each, and the run does not start. |
| `emt: no valid probes.` (exit 2) | The probe list ended empty. |
| `emt: bad --event '<token>' (use fault:<bus>@<t>:<Rpu>, clear:<bus>@<t>, or switch:<id>@<t>:close\|open).` (exit 2) | The event token is malformed. |
| `emt: source bus 99 is not in the model.` (exit 2) | `--source` names a bus the case does not have. |
| A peak that is lower than expected | A coarse step under-reads peaks without any message. Run at a second, smaller `--dt-us` and compare. |

## Harmonics

The verb is in [ch. 17, Harmonic studies](<0017 Harmonic studies.md>).

### `error: harmonics: Harmonic source at bus N (...) injects order 3, a triplen (zero sequence). ...`

```
$ gridsim harmonics case9 --spectra docs/_captures/harmonics/spectra-case9-triplen.json
error: harmonics: Harmonic source at bus 9 (6-pulse) injects order 3, a triplen (zero sequence). The balanced positive-sequence study cannot model it (that needs the zero-sequence network); use the three-phase harmonic study. (Parameter 'sources')
[exit 1]
```

Cause. A source with a non-zero entry at a triplen order (3, 9, 15, ... 45) cannot be carried by the
positive-sequence network of this study. The run stops before it solves, exit 1. A source at an even order such as 6 or 12 is refused first, by the entry below. The library `ev-charger`, `pv-inverter`,
`heat-pump-inverter`, `arc-furnace` and `saturated-transformer` spectra carry triplens.

Fix. Remove the triplen lines from the `--spectra` file and accept that their distortion is not studied.

### `error: harmonics: N source component(s) are at orders this study does not analyse ...`

```
$ gridsim harmonics case9 --spectra docs/_captures/harmonics/spectra-case9-even.json
error: harmonics: 4 source component(s) are at orders this study does not analyse (it analyses orders 3,5,7,9,11,13,15,17,19,21,23,25,27,29,31,33,35,37,39,41,43,45,47,49): bus 9 (6-pulse) order 4 at 18 %; bus 9 (6-pulse) order 2 at 11 %; bus 9 (6-pulse) order 50 at 4.5 %; bus 9 (6-pulse) order 52 at 3 %. They would be ignored and the THD understated; remove them from the spectra file.
[exit 1]
```

Cause. The study solves the odd orders 3 to 49. A line at any other order (even orders, or orders above 49) would not
enter the solve and the THD would be understated, so the run stops with exit 1 and names every such line.

Fix. Remove those lines, or put the source energy at odd orders, 5 to 49, that are not multiples of 3.

### `error: harmonics: --bus N is not a bus of <case>.`

```
$ gridsim harmonics case9 --bus 999
error: harmonics: --bus 999 is not a bus of IEEE 9-bus (WSCC).
[exit 2]
```

Cause. `--bus` names a bus the case does not have. Exit 2. A `--bus` that is not an integer exits 2 with
`error: --bus expected an integer but got 'abc'`.

Fix. Use a bus id the case prints.

### `harmonics: FAILED - Harmonic penetration: the harmonic network admittance Y(h) is singular at order 5 ...`

```
$ gridsim harmonics docs/_captures/harmonics/case-singular
harmonics: FAILED - Harmonic penetration: the harmonic network admittance Y(h) is singular at order 5, so no harmonic voltage can be computed. Part of the network has no path to ground at this frequency (a radial feeder or island with no line charging, shunts or source impedance). Model the machine source impedance (HarmonicModelOptions.IncludeGeneratorSources, or HarmonicModelOptions.Study) or the loads (IncludeLoadShunts) to ground it.
[exit 1]
```

Cause. Part of the network has no path to ground at some injected order: a radial feeder or island with no machine, load,
shunt or line charging. The run prints no THD and no verdict, exit 1. A harmonic study never reports 0 % for a network it
could not solve.

Fix. Give the case a path to ground (a machine, a load or a shunt), as the message says.

### Other harmonics refusals

| Message | Exit | Cause |
|---|---|---|
| `REJECTED (forward inference): harmonic source set timestamp ... is newer than the historical horizon ...` | 2 | The `takenUtc` of a `--spectra` file is inside the last 169 hours. |
| `error: failed to load --spectra '<file>': Harmonic source set '<case>' failed validation:` followed by one line per fault | 2 | A wrong `baseMva`, a bus that is not in the case, a non-finite value or an order below 1. Every offender is listed. |
| `WARNING          : N bus(es) have no base kV - judged at the strictest tier and NOT certified` | 0 | The case gives no base voltage (`case14`). `0/14 buses compliant` says GridSim cannot certify, not that every bus fails a limit. |

## State estimation

The verb is in [ch. 21, State estimation](<0021 State estimation.md>).

### `observability: UNOBSERVABLE`, exit 3

```
$ gridsim estimate case9 --measurements docs/_captures/estimation/case9-telemetry-thin.json --out results/trouble-tmp/thin

=== WLS state estimate: case9 ===
  status: Unobservable   iterations: 0   max |dx|: Infinity
  measurements: 13   states: 17   redundancy (dof): -4
  observability: UNOBSERVABLE
    free state directions: Vm@bus 5, Vm@bus 7, Vm@bus 8, Vm@bus 9

  -> results\trouble-tmp\thin
[exit 3]
```

Cause. The measurements are too thin to pin every state. The console lists the `free state directions`, here the
voltage magnitudes at buses 5, 7, 8 and 9. The files are still written, but the state in `estimate-export.json` is the
start point and not an estimate.

Fix. Add measurements where it says: a voltage or injection measurement at each listed bus, or a flow on a branch into the
unobserved region. With `--synth`, raise `--coverage-vm`, `--coverage-inj` and `--coverage-flow`.

### `consistency: J = ... vs chi2(95%) = ... -> FAIL`, exit 0

```
$ gridsim estimate case14 --synth --noise 1.6 --out results/trouble-tmp/noise

=== WLS state estimate: case14 ===
  status: Converged   iterations: 25   max |dx|: 2.78E-011
  measurements: 122   states: 27   redundancy (dof): 89
  observability: observable (rank 27/27)
  consistency: J = 137.92 vs chi2(95%) = 112.02 -> FAIL
...
[exit 0]
```

Cause. The measurements are mutually inconsistent at their declared sigmas: a gross error survived the removals, the
sigmas are optimistic, or the model does not match the era of the telemetry. The exit code is 0, so a script that tests
only the code cannot see it. A robust method (`huber`, `lav`, `schweppe-gm`) reads `FAIL` whenever the set has outliers,
because it keeps the down-weighted rows in `J`.

Fix. Read the consistency line, or `chiSquareTest` in `estimate-export.json` (`Passed`, `Failed` or `NotRun`). Look at
what bad-data removal named: the `removed` block of `estimate-export.json` and the worst rows of `residuals.csv`.
Recurrent offenders at reactive-compensated sites point at model fidelity, not telemetry. If you set `SPINE_ETYS=1` for the
GB history build, that era mismatch alone regresses chi-square: turn it off
([ch. 3, Configuration](<0003 Configuration.md>)).

### `error: --hti could not identify the bad data on this set: singular matrix in DSE covariance inverse ...`

```
$ gridsim estimate case9 --measurements docs/_captures/estimation/case9-telemetry-bad.json --hti --out results/trouble-tmp/hti
error: --hti could not identify the bad data on this set: singular matrix in DSE covariance inverse (pivot 1.344E-014 at column 2).
       the suspect measurements are not jointly identifiable (too little redundancy around them); run without --hti for largest-normalised-residual removal, or add measurements.
[exit 3]
```

Cause. `--hti` needs the suspect measurements to be jointly identifiable. Where there is too little redundancy around
them the covariance block is singular. Exit 3.

Fix. Run without `--hti` for largest-normalised-residual removal, or add measurements around the suspects.

### Other estimation messages

| Message or symptom | Cause and fix |
|---|---|
| `REJECTED (forward inference): measurement set timestamp ...` (exit 2) | The set is dated inside the last 169 hours. The estimator reconstructs history and does not nowcast. Use data at least 8 days old. |
| `estimate` on real data diverges or crawls from a flat start | Real operating points are far from flat. Pass `--prior solve` or `--warm-start`. |
| Every removal makes a weak area worse | Removal in a weakly redundant region lets the state drift from the prior. Use `--backfill-removals`. |
| `--synth` and `--measurements` together (exit 2) | The two are refused; give one. |
| `error: estimate: --method qr is dense only (fails loud above 3000 buses; ...)` (exit 2) | `constrained`, `feasible` and `qr` are dense only. Use the default method on a larger case. |
| `not tested (chi-square not run: --no-bad-data ...)` | There is no verdict. Compare with a run without `--no-bad-data` before trusting the state. |

A set dated in the future is refused like this:

```
$ gridsim estimate case9 --measurements docs/_captures/estimation/case9-telemetry-future.json --out results/trouble-tmp/fut
REJECTED (forward inference): measurement set timestamp 2099-01-01T12:00:00.0000000Z is newer than the historical horizon 2026-10-13T04:26:38.8901745Z (data must lag real time by >=169 h). GridSim replays history only - it never processes recent or future data, so it cannot forward-infer.
[exit 2]
```

## GDA and the bridge

The ingestion verbs are in [ch. 22, Estimating GB history](<0022 Estimating GB history.md>), the stored-state replay
in [ch. 23](<0023 Replaying estimated states.md>), and the server protocol in
[ch. 24, The GDA bridge](<0024 The GDA bridge.md>).

### `GDA root error: ...`, `GDA_ROOT` and `GDA_DATA_ROOT`

The lake folder is chosen in this order: `--root`, then `GDA_DATA_ROOT`, then `GDA_ROOT`. `GDA_ROOT` is read only when
`GDA_DATA_ROOT` is unset or empty. A variable that is set but names no folder stops the run with exit 1, naming the
variable and not its value, and does not fall back to the next one.

```
$ GDA_DATA_ROOT=missing-lake gridsim gda
GDA root error: the environment variable GDA_DATA_ROOT is set but does not name an existing folder (its value is not printed). Point GDA_DATA_ROOT at the GDA lake folder (the one holding NationalGrid/ and NESO/), unset it, or pass --root <path>. GridSim does not fall back to GDA_ROOT while GDA_DATA_ROOT is set.
[exit 1]
```

An explicit `--root` that does not exist stops the run too (exit 1, `GDA root error: GDA root not found: <path>`). With no
`--root` and neither variable set, `gridsim gda` prints its usage and exits 2.

Fix. Point `GDA_DATA_ROOT` at the folder that holds `NationalGrid/` and `NESO/`, unset it, or pass `--root`.

### Errors from `gda` options

| Message | Exit | Cause and fix |
|---|---|---|
| `error: --dno expected 'nged', 'ukpn' or 'npg' but got '<value>'` | 2 | A mistyped `--dno`. An `--as-of` that is not an ISO-8601 UTC timestamp exits 2 the same way. |
| `error: --gsp '<name>' is not a GSP group of <dataset>. Nearest: '...'. gda --all-gsps lists every group.` | 2 | A mistyped group name. The match ignores case, spacing and `kV` spelling. Use a suggested name. |
| `error: the carve selected no live element (0 buses): check --licence, --gsp and --minkv ... No model was written.` | 3 | The name is known but every element is out of service or planned, or the `--licence` and `--minkv` combination keeps none. Nothing is written or solved. Use `--gsp "Exeter 132 kV"` rather than `"Exeter 132"`. |
| `Load failed: GDA dataset not found: <path>` | 1 | The root exists and lacks a table. |
| `Fleet build failed: GDA dataset folder not found: ...` | 1 | `--fleet` needs the NESO Transmission Entry Capacity register folder (the published `tec-register-*.csv` files under `NESO/RAWData`); when the lake holds it only as Parquet the message says so. |
| `error: GridSim CLI not found: GRIDSIM_CLI is not set and 'gridsim' is not on PATH. Set GRIDSIM_CLI to the full path of gridsim(.exe) ...` | 1 | A builder script finds neither the variable nor `gridsim` on `PATH`. Set `GRIDSIM_CLI` to the full path of the executable. When the variable is set to a path that does not exist, the message reads `error: the GridSim CLI set by GRIDSIM_CLI does not exist: <path>. ...`. |

`gda-replay` exits 3 when any step fails to converge, and `gda --all-gsps` when any group does. The day builder exits 3
when any period ran and failed to converge, and the wrappers treat that as stored. Exit 1 from the day builder means the
day aborted and nothing was stored. A `--bess` site on a bus that is not in the replay case is refused before the replay
starts: `error: gda-replay storage: storage site '...' is at unknown bus N.`, exit 2.

### Horizon rejections

GridSim replays history and never forward-infers. A timestamp newer than now minus 169 hours is refused, on both ends of
the bridge independently.

```
$ gridsim gda-replay --synth --start 2026-10-01 --steps 4 --out results/gda-replay-rejected

=== Historical state estimate: case118 over synthetic-case118 ===
  reconstructed 4 timestamps  (2026-10-01T00:00:00.0000000Z .. 2026-10-01T01:30:00.0000000Z)
  non-converged: 0
  system loss over the series: 56.7 .. 66.6 MW

  -> results\gda-replay-rejected
[exit 0]
```

In the verbs the message is `REJECTED (forward inference): ...` and the exit code is 2 (`gda-replay`, `estimate`,
`replay-event`, `scenario` and `harmonics --spectra`). The bridge server answers a `start` or `seek` newer
than the cutoff with an `error` message:

```
$ python bridge_probe.py '{"action":"start","case":"gb-spine","start":"2026-10-02T00:00:00Z","fps":144,"source":"estimated"}' 3
info      {"message": "GridSim-Bridge connected", "cases": ["gb-full", "gb-spine", "zone-enwl", "zone-nged", "zone-npg", "zone-spen", "zone-ssen", "zone-ukpn"]}
error     {"message": "start 2026-10-02T00:00:00+00:00 is past the no-forward-inference horizon (cutoff 2026-09-26T11:35:06.513504+00:00, now - 7d 1h). Rejected."}
[exit 0]
```

The desktop app turns a received tick newer than the cutoff into the status pill `HORIZON REJECTION  *  stream stopped`. The
receive loop has ended, so a picker change does not revive it: select another model source and then **Live bridge** again.
Pick starts at least 8 days back; every desktop preset is.

### Status pills and `info` messages

| Symptom | Cause and fix |
|---|---|
| Status pill `CONNECT FAILED  *  <reason>` | No bridge server is listening at the endpoint, or the endpoint is wrong. Start the bridge and check `BridgeEndpoint` in `%LOCALAPPDATA%\GridSim\settings.json` (desktop) or `GridSim:BridgeEndpoint` (web). Neither has a default. A local bridge speaks `ws://127.0.0.1:8770/ws/bridge`; the `wss://` scheme needs TLS in front of the server. |
| The `Live bridge` source is missing from the picker | `BridgeEndpoint` is empty, which is its default, so the source is hidden. |
| `No GDA bridge endpoint is configured (GridSim:BridgeEndpoint).` | The web host has no bridge endpoint, and a `bridge:<case>` session was asked for. Set `GridSim:BridgeEndpoint`. |
| Status pill `BRIDGE FAULT  *  disconnected` | The receive loop faulted: the server sent an `error`, restarted, or the socket closed. The event log carries the message. Reconnect and read the server console. |
| Status pill `HORIZON REJECTION  *  stream stopped` | See above. A server clock ahead of the client's can stream a tick the client refuses; keep the clocks aligned. |
| The view looks like a live stream although you chose `ESTIMATED` or `presolved` | The server sent an `info`, for example `No estimated lake for 'zone-nged'; streaming the live on-demand solve instead (...)` or `No <source> frames for '<case>' in <start> -> <end>; streaming the live solve instead. ...`. Neither app shows `info` messages, so the only sign is the missing chi-square pill. Build the lake or the window, or choose `gb-spine`. |
| `SRC: estimated` shows nothing for a recent day | The estimated lake is built day by day and that day has not been built. Run the daily task ([ch. 22](<0022 Estimating GB history.md>)) and reconnect. |
| Pill `ESTIMATED  *  ...  *  chi-square FAIL` | Not a bridge fault: the stored estimate failed consistency. Treat it as the chi-square entry under [State estimation](#state-estimation). |
| A PASS pill and a bus voltage well outside the band | A chi-square PASS says the fit agrees with the declared sigmas, not that the state is physical. Read the voltages beside it ([ch. 22, Pitfalls](<0022 Estimating GB history.md#pitfalls>)). |

## Interposer

The verb is in [ch. 25, Interposing live telemetry](<0025 Interposing live telemetry.md>).

### Nothing arrives and the listener sits idle

Cause. `interpose` listens; it never connects to a device and never answers. A station that waits for its master to connect,
or for a `STARTDT act` to be answered, never sends.

Fix. Point a forwarder or relay at the port.

### A client disconnects or resets and the next one is not served

Cause. The interposer serves one client at a time. A client that closes or resets its connection is dropped, counted in
`client-resets` on the stop line, and the next client is accepted. A half-received frame of the old client is discarded and
never joined to the new one. A second client that connects while the first is still connected waits until the first
disconnects.

Fix. Disconnect the first client. A stop line with `client-resets` above 0 and a later `accepted` count confirms the new
client was served.

### `error opening <protocol> transport on <host>:<port>: ...`

Cause. The port is in use or the host is not bindable. Exit 1.

Fix. Pick another `--listen`.

### Crosswalk and window errors

| Message or symptom | Cause and fix |
|---|---|
| `error loading crosswalk '<file>': ...` (exit 1) | The file is missing, is not valid JSON, declares a schema other than `gridsim-rtu-pointmap/1`, or has a `dataType` on an address that is not a Modbus register. |
| `error: --crosswalk <map.json> is required.` (exit 2) | Every run needs a crosswalk. |
| `error: unknown protocol '<name>'. Try --list-protocols.` (exit 2) | Only `iec104` and `modbus` can be selected. |
| `windows-written 0` with points decoded | No decoded address matched a binding, or every matched point was dropped for quality. Compare the monitor listing with the `address` strings in the crosswalk character for character. |
| A Modbus address that never matches | Without the poll requests an address is a position in the response (`MB:U1:3:0`); with them it is the register (`MB:U1:3:@100`), zero-based as sent on the wire. Run `--monitor` and copy the address it prints. |
| A negative flow reads as a large positive number | A Modbus register is unsigned unless the binding says `"dataType": "S16"` (or `S32`). |
| `serve` shows `rejected-fresh` for every window and exits 3 | The windows are present-time and the 169-hour rule applies. Wait, or feed device-timestamped history. |
| `serve` reports `Unobservable` and exits 3 | The window has too few measurements ([State estimation](#state-estimation)). |

### `window discarded (crosswalk mismatch): ...` and exit 3

Message. `window discarded (crosswalk mismatch): Binding 'MB:U1:3:@103' is F32 (two registers) but register 'MB:U1:3:@104'
is not in the same response. Poll a block that includes both registers.` The stop line then reads `map-errors 1`.

Cause. A 32-bit binding (`F32`, `U32`, `S32`) needs the next register in the same response. When it is missing, that window
is discarded whole, the reason is printed on standard error, `map-errors` counts it and the listener carries on. The verb
exits 3 when it stops if any window was discarded. The captured run is in
[ch. 25, Pitfalls](<0025 Interposing live telemetry.md>).

Fix. Poll a block that includes both registers.

## Web host and desktop app

### The Blazor page loads and does nothing

Cause. Either the static asset `blazor.web.js` is not served (the host was started with `dotnet run --no-build` outside the
Development environment), or, behind nginx, the WebSocket upgrade headers are not sent on `/_blazor`.

Fix. Launch with `ASPNETCORE_ENVIRONMENT=Development` or a full `dotnet run`. Behind a proxy, pass the upgrade headers
on `/_blazor` ([ch. 26](<0026 Audit log, deployment and assurance.md>)).

### `/api/...` returns 404, or only the public-data replay is offered

Cause. `GridSim:ReplayOnly` is `true`, which is the Production default: the solve API and the simulation WebSocket are not
mapped. `/dashboard/<case>` still opens a live session in that mode, because the tab list is chosen per session.

Fix. Set `GridSim__ReplayOnly=false` for a host that should serve the solve API, on a network you control.

### Every visitor is anonymous after enabling sign-on

Cause. The key ring, `Auth__ApplicationName` or the cookie domain differs from the estate's, so the cookie cannot be
decrypted ([ch. 3](<0003 Configuration.md#web-app-gridsimweb>)).

### A new run folder does not appear in the web catalogue

Cause. The case catalogue scans for `results/` once per process.

Fix. Restart the host.

### The desktop app opens on the wrong model

Cause. The app takes the first token that does not start with `-` as the model text, so a flag's value (`OPERATOR`,
`zone-ukpn`) given without model text is matched as model text, and when no label contains it the app opens on the first
discovered source.

Fix. Put the model text first: `GridSim.Wpf case118 --tab OPERATOR`.

### A loaded run shows 50 Hz and 0 RoCoF, or is rejected

Cause. A replay takes frequency, RoCoF, generation and demand from the run's sidecar CSV (`timeseries.csv` or
`state-estimate.csv`). Without one, generation and demand are summed from each frame, and frequency and RoCoF read 50 Hz
and 0. A folder without an `exports/` directory of `slice-*.json` or `state-*.json` frames is not a run: the app rejects it
with a log line.

Fix. Keep the CSV beside the `exports/` folder, or re-run the verb that wrote the run
([ch. 8, Loading a pre-solved run](<0008 The desktop app.md#loading-a-pre-solved-run>)).

### A desktop launch flag has no effect

Cause. The pre-selection flags ignore a value that matches nothing, without a message. `--back` takes one of 9, 12, 14,
21, 30 or 60 (days behind now). `--zone` matches a bridge case by substring, `--bridge-source` takes `presolved`,
`estimated` or `live`, and `--tab` matches the first tab whose header contains the text.

Fix. Use a value from the list ([ch. 3, Desktop app](<0003 Configuration.md#desktop-app-gridsimwpf>)).

### The status lamp reads NOT CONVERGED

Cause. The frame's power flow stopped without converging. The lamp grades a frame in this order: NOT CONVERGED,
FREQUENCY EVENT, VOLTAGE ALERT, CORRIDOR ALERT, SYSTEM NORMAL, so a non-converged frame never reads normal. The event log
records the onset.

Fix. See [Power flow does not converge](#power-flow-does-not-converge).

### The app closes with `GridSim hit an internal error and has to close`

Cause. An unhandled exception. The desktop app writes a crash log, shows a message that names it, and exits with
`-532462766`. A background task whose failure nobody awaited writes a log named `GridSim.Wpf-task-crash-...` and does not
close the app. The web host writes no crash log; its exceptions go to the `/Error` page.

Fix. Report the crash log ([Crash logs](#crash-logs)).

## GPU fallback

`--gpu` (auto-detect) asks for a device and falls back to the CPU when none answers. The fallback is silent in the
report. The only sign is one line on standard error, and the exit code stays 0. A named vendor (`--gpu=rocm`,
`--gpu=opencl`, `--gpu=cuda`) does not fall back: with no usable device of that kind the run exits 2 (except `bench`
and `gpucheck`, which report the absent vendor).

```
$ gridsim case14 --once --gpu
...
Protection simulation: no relay would operate on this state (generic settings).
[gpu] no usable gpu device found - falling back to the CPU backend.
[exit 0]
```

Cause. No usable CUDA, ROCm or OpenCL device answered. Everything computed after that line ran on the CPU backend. A script that discards standard error cannot tell. A `bench`
run with `--gpu` records the backend as `cpu (gpu requested but unavailable)` and lists only `cpu-*` engines.

Fix. Run `gridsim gpucheck` to see what the machine offers and read its lines. It exits 3 when any probe that ran
printed `FAIL` (or a solve that ran did not converge), and 0 when every probe that ran passed; a backend that is not
present is skipped, not failed, so exit 0 does not mean a GPU is there: a script that needs one gates on `PASS`
appearing for that backend. An unknown vendor (`--gpu=metal`) exits 2 before any solve:

```
$ gridsim batchsolve --gpu=metal
error: --gpu expected one of auto, cpu, rocm, opencl, cuda but got 'metal'
[exit 2]
```

Driver and rented-box problems are in [ch. 28, Benchmarking and GPU](<0028 Benchmarking and GPU.md>).

## Crash logs

Any unhandled exception in `gridsim` is a defect in GridSim and not a problem with your input. The tool writes a crash log,
prints three lines on standard error that name it, and lets the runtime print the stack and end the process with
`-532462766`. The crash is never turned into success and never into exit 1.

```
$ gridsim dcopf results/trouble-tmp/pmin-above-pmax.m
...
gridsim: internal error - this is a defect in GridSim, not a problem with your input.
gridsim: InvalidDataException: DC-OPF: generator 0 ('' at bus 1) has Pmin 300 MW above Pmax 250 MW.
gridsim: crash log written to results/crash\gridsim-crash-20261004-142052-20932.log - please include it when reporting the problem.
Unhandled exception. System.IO.InvalidDataException: DC-OPF: generator 0 ('' at bus 1) has Pmin 300 MW above Pmax 250 MW.
...
[exit -532462766]
```

The capture set `GRIDSIM_CRASH_DIR=results/crash`, so the third line names that folder; a run without it names the default folder below, and its process id and stack lines differ. This capture is `dcopf` on a copy of `case9` whose first generator has `Pmin` 300 MW above `Pmax` 250 MW,
taken on an earlier build. That input is no longer a crash: the current build prints `[dcopf] DC-OPF: generator 0 ('' at
bus 1) has Pmin 300 MW above Pmax 250 MW.` and exits 1, with or without `--costs`. The capture is kept to show the
form of a crash report; the capture has not been retaken with another trigger.

The log is one plain-text file. It is written to the first of these folders that can be created:

1. the folder named by the `GRIDSIM_CRASH_DIR` environment variable, when it is set;
2. `%LOCALAPPDATA%\GridSim\crash` on Windows (`~/.local/share/GridSim/crash` on Linux,
   `~/Library/Application Support/GridSim/crash` on macOS);
3. the system temporary folder, under `GridSim/crash`.

The file name is `<app>-crash-<yyyyMMdd-HHmmss>-<process id>.log`, so two crashes never overwrite each other: `gridsim` for
the command line tool, `GridSim.Wpf` for the desktop app. If no folder can be written, the third line says the log could
not be written and the stack trace on the screen is the only record. The file holds the app, the UTC time, the exact
build (the `--version` line), the runtime, the operating system, the command-line arguments, and the exception with its
stack and every inner exception. [ch. 3](<0003 Configuration.md>) lists `GRIDSIM_CRASH_DIR`.

The triggers that used to end in a crash log are now refusals with an exit code:

| Trigger | Now |
|---|---|
| `dcopf` without `--costs` or `--prices`, or `acopf` without `--costs`, on a case with a generator whose `Pmin` is above `Pmax` (or non-finite) | `[dcopf] <message>` or `[acopf] <message>`, exit 1. Correct the generator data. |
| `stability <case> --fault-bus N` with a bus that is not in the case | `error: stability: fault bus N is not in case '<name>'.`, exit 2, on every engine. |
| `freq` or `replay` on a file with a row newer than 169 hours | `REJECTED (forward inference): ...`, exit 2. Use older data. |
| `replay-event --trace <file>` with a timestamp in the trace newer than 169 hours | `REJECTED (forward inference): ...`, exit 2. Use older data. |
| `gda-replay --bess` naming a bus that is not in the replay case | `error: gda-replay storage: storage site '...' is at unknown bus N.`, exit 2. |

## How to report a problem

Send these, in this order, and the report can be reproduced without a question back:

1. **The version line.** The output of `gridsim --version`, whole, with the commit hash. A bug that depends on the vector
   tier also needs the output of `gridsim simdcheck`.
2. **The exact command**, with every flag, and the working directory it ran in.
3. **The whole output**, standard output and standard error, and the exit code. Do not paste only the last line.
4. **The crash log**, when the run printed `gridsim: internal error`. Its path is on the third line of the message.
5. **The input.** The case name, or the `.m` file or case folder, and any measurement, spectra, catalogue or crosswalk
   file. If it cannot be shared, say how many buses it has and which verb reads it.
6. **The environment.** The operating system, and any `DOTNET_*` or `GRIDSIM_*` variable that is set, which includes
   `DOTNET_EnableAVX2`, `DOTNET_EnableHWIntrinsic` and `GRIDSIM_LICENCE` (name the variable, never send a key).
7. **For a result that differs between machines or between runs**, the two outputs side by side, the `simdcheck` output of
   each machine and the `run-metadata.json` of each run. GridSim's acceptance rule is bit-identical results across runs and CPU modes
   ([ch. 1, The three modes](<0001 Getting started.md#the-three-modes>)), so a difference is a finding.

## Limits

- The bare run (`gridsim <case>` with no verb) takes its first word only: `--dir` after a case is ignored, and a case after
  `--dir` is ignored, both without a message.
- `gridsim estimate` exits 0 on a chi-square `FAIL`, and `impedance-stability` exits 0 on an `UNSTABLE` verdict.
- A fallback from `--gpu` to the CPU is reported on standard error only; the exit code and the report do not show it.
- The desktop app and the web console show no `info` message from the bridge, so a fallback to a live stream is
  invisible there.

## See also

- [ch. 19, CLI reference](<0019 CLI reference.md#parsing-rules-and-exit-codes>) - every verb, flag and exit code.
- [ch. 3, Configuration](<0003 Configuration.md>) - environment variables, `GRIDSIM_CRASH_DIR`, endpoints and paths.
- [ch. 27, Validation tools](<0027 Validation tools.md>) - the instruments that separate a bug from an input property.
- [ch. 21, State estimation](<0021 State estimation.md>), [ch. 24, The GDA bridge](<0024 The GDA bridge.md>) and
  [ch. 25, Interposing live telemetry](<0025 Interposing live telemetry.md>) - the pitfalls of each in depth.
- [Technical ch. 11, Validation](<../technical/0011 Validation.md>) - the inputs that are expected not to converge.
