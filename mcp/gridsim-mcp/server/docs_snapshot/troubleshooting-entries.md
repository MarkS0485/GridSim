GridSim manual ch. 30 Troubleshooting: the entries the server's message map points at.

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

### `ISLANDED`, `NO SOLUTION` and `solver error:` rows

- `ISLANDED` means the outage separated part of the network from the slack bus.
- `NO SOLUTION` means the network stayed in one piece and the power flow found no converged state. A voltage collapse is one cause.
  The capture above reads `1 not solved` for the outage of branch 1-2 on `case14`.
- `solver error: <type>: <message>` is the text of the violation entry of an outage for which the solver threw. The Status
  column reads `ISLANDED` when the outage split the network and `NO SOLUTION` when it did not.

Fix. Read the status before reading the loading columns of that row.

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
error: This network has 9,241 buses; your Personal licence allows 1,500. A larger licence removes the limit. Licences are free at present: ask mark@gridsimdigitaltwin.co.uk
[exit 2]
```

Fix. Check the active tier with `gridsim licence status`. At present licences are issued gratis: email
`mark@gridsimdigitaltwin.co.uk` for one, then install it with `gridsim --importLicence <file>` ([ch. 4, Licensing and activation](<0004 Licensing and activation.md>)). An
expired licence falls back to the Personal tier and the message names the expiry date.

### `error: unknown command or case '<name>'.`

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

### `Taps NOT SETTLED: ...` under a converged report

Cause. The line is printed only for a converged solve. The on-load tap changer loop hit its pass cap with taps still
moving, or has tap changers at the end of their range with the controlled bus outside its deadband. The line names the
changers at their limits. A non-converged solve never steps a tap, and its report reads `DID NOT CONVERGE` instead.

Fix. Treat the voltages as not regulated. Fix the base solve, or inspect which transformers are hunting.
