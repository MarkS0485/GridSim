# gridsim (Python)

GridSim from Python: a small, pure-Python package that runs the `gridsim` command-line program and reads its result
document. There is no in-process interop and no network service, so every number is the CLI's own, bit for bit. It
uses the Python standard library only (tested on Python 3.12).

```python
import gridsim

gs = gridsim.GridSim()              # finds the CLI (see below)
r = gs.pf("case14")                 # the same as gs.run("pf", "case14")
r.summary["converged"]              # True
r.summary["loss_mw"]                # 13.393272357432002
r.table("buses")[0]                 # {'bus': 1, 'vm_pu': 1.06, 'va_deg': 0, 'p_inj_mw': ..., 'q_inj_mvar': ...}

r = gs.dcopf("case30", "--no-limits")
r = gs.nminus1("case14")
r.ok, r.exit_code                   # a NOT SECURE screen is still a result, with a non-zero exit code
```

## How it works

`run(verb, *args)` runs `gridsim <verb> <args...> --json <temporary file>`, waits, and reads the file: a
`gridsim-cli-result/1` document (manual chapter 19, "Result files"). The arguments go to the CLI as they are, in order;
`--json`, `--csv` and `--out` are refused, because the wrapper names the result file.

A `Result` has `verb`, `args`, `tool`, `exit_code`, `ok`, `summary` (a dict), `tables` (table name to a list of row
dicts), `table(name)`, `stdout`, `stderr` and `document` (the JSON as read). Numbers are written by the CLI in round-trip
form, so the floats are exactly the bits GridSim computed. JSON has no NaN or infinity: the CLI writes them as the
strings `"NaN"`, `"Infinity"` and `"-Infinity"`, which `summary` and `tables` turn back into floats (`document` keeps
the strings).

A run that writes no result (a usage error, a case that does not load, a solve that does not converge) raises
`GridSimError`, with `exit_code`, `stdout`, `stderr` and the CLI's last stderr line in the message.

## Finding the CLI

In order: `GridSim(cli=...)` (a path, or a list such as `["dotnet", "path/to/gridsim.dll"]`); the `GRIDSIM_CLI`
environment variable; `gridsim` on the PATH; a Release, then Debug, build of `src/GridSim.Cli` in the checkout this
folder sits in. `GridSim(cwd=...)` sets the folder runs start in (default outputs such as `results/` land there),
`timeout=` a limit in seconds per run, `env=` extra environment variables.

## Wrapped verbs

The verbs whose result the CLI writes through its shared result writer, each with a method of the same name
(underscores for hyphens): `acopf`, `acopf-full` (`acopf_full`), `convert`, `cpf`, `cycle`, `dcopf`, `dcpf`, `dist3ph`, `domains`, `experimental`,
`fault`, `freq`, `grade`, `hosting`, `iec60909`, `inertia-placement` (`inertia_placement`), `lodf`, `losses`, `n-1-1` (`n_1_1`), `nminus1`, `nminus2`, `pf`,
`physics-check`, `pmu-estimate`, `pmu-ingest`, `protection-check`, `ptdf`, `qsts`, `restore`, `scopf`, `scuc`, `taps`, `validate`, `zonal`.
The aliases `n-1`, `n-2` and `nminus1minus1` work in `run`. A GridSim test keeps this list equal to the CLI's own.

Not wrapped, because they write no complete result document: the live run, `serve` (a resident stream), and the
verbs that write their own folders or reports (`trip`, `screen`, `stress`, `chain`, `transient`, `replay`, `scenario`,
`emt`, `harmonics`, `estimate`, `gda-replay`, `export`, `bench` and the rest). Their outputs are files: run them with
`subprocess` and read what they write (manual chapters 19 and 31).

## Tests

From the repository root: `python -m unittest discover -s python/tests`. The tests that run the real CLI skip, saying
so, when none is found; the others drive a stand-in CLI and need nothing but Python.
