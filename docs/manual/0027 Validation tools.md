# 27. Validation tools

GridSim checks its own answers with code that shares none of its arithmetic. A GridSim verb solves, writes an export
of its inputs and its answer, and an independent tool (pandapower, NumPy, SciPy) re-derives the answer from the
recorded inputs. Agreement is the trust signal. Disagreement means the build, the input or the independent tool is
wrong, and this chapter shows how to tell which. Two verbs audit a state against physical laws without any outside
tool (`physics-check`, `conformance`); three check the machine rather than the physics (`simdcheck`, `batchsolve`,
`gpucheck`). The Python sidecars live in `tools/crossval/`. The numbers every instrument is held to are in
[technical ch. 11, Validation](<../technical/0011 Validation.md>) and [technical ch. 16, Physics
conformance](<../technical/0016 Physics conformance and the constrained estimator.md>).

## Before you start

The GridSim verbs `physics-check`, `conformance`, `simdcheck`, `batchsolve` and `gpucheck` need nothing beyond the
build. The sidecars need Python (the `crossval` verb uses `GRIDSIM_PYTHON` when set, else the active virtual
environment's interpreter, else the first `python` on PATH in PATH order, else the `py` launcher, and prints the one it
used) and these packages:

```bash
pip install numpy scipy pandapower
```

- `residual_check.py`, `physics_audit.py`, `crossval_harmonics.py` and `crossval_impedance.py` need NumPy only.
- `se_residual_check.py`, `crossval_freq.py`, `crossval_classical.py` and `crossval_smallsignal.py` need NumPy and SciPy.
- `crossval_h0.py` needs nothing for its arithmetic checks and NumPy and SciPy for the forming re-integration, which it
  skips when SciPy is absent.
- `crossval.py` (the `crossval` verb) and `crossval_estimation.py` need pandapower, which brings NumPy and SciPy.
- `lake_conformance.py` needs NumPy, pyarrow and a GDA lake.
- `mc_estimate_sweep.py` needs only the Python standard library and the CLI (this checkout's Release build, or the one
  `--cli` or `GRIDSIM_CLI` names).

### Exit codes

The exit-code meanings are not uniform across the instruments, so each one is listed here. Python's own usage-error
exit is 2 for every script (a bad argument, or a script that cannot open a file). An exit 77 means nothing was
validated and is never a pass: a CI job that wants "validated" must require 0.

| Instrument | Exit codes |
|---|---|
| `gridsim crossval` | 0 agrees; 1 the case cannot be loaded, `crossval.py` cannot be found, or `GRIDSIM_PYTHON` names nothing; 2 no case argument; 3 mismatch, or the sidecar failed (its exit 2, 3 or 4); 77 no Python interpreter, or NumPy / pandapower absent from the one used |
| `crossval.py` | 0 within tolerance; 1 mismatch; 3 unreadable export; 4 the pandapower solve raised, or the translation guard fired (pandapower would not be solving the same network); 77 NumPy, SciPy or pandapower absent |
| `residual_check.py` | 0 within tolerance; 1 not within tolerance; 3 the export could not be read or parsed, or is not a solve export; 77 NumPy missing |
| `physics_audit.py` | 0 pass; 1 fail; 3 the export could not be read; 77 NumPy missing, or no independent comparison applied to the export (a plain WLS `estimate-export.json`: it fixes no injection outside its own state) |
| `se_residual_check.py` | 0 all checks pass; 1 a check fails, or an uncaught exception (a missing file, invalid JSON); 3 wrong schema tag, an unresolvable branch or an unsupported measurement kind; 77 NumPy or SciPy missing |
| `crossval_estimation.py` | 0 within tolerance (or, with `--bad-data`, the two tools agree on whether bad data is present); 1 mismatch; 3 unreadable export or an unsupported kind; 4 the pandapower estimation raised; 77 NumPy or pandapower absent |
| `mc_estimate_sweep.py` | 0 the sweep ran; 2 the CLI (the Release build, or the one `--cli` names) is missing |
| `crossval_freq.py` | 0 within tolerance; 1 mismatch; 3 setup error (unreadable export, wrong schema); 77 not checked, or NumPy / SciPy missing |
| `crossval_classical.py` | 0 within tolerance; 1 mismatch; 3 setup error (unreadable export, wrong schema); 77 NumPy or SciPy missing |
| `crossval_smallsignal.py` | 0 eigenvalues within tolerance; 1 mismatch; 3 setup error (unreadable export, wrong schema, no valid pairing); 77 NumPy or SciPy missing |
| `crossval_harmonics.py` | 0 within tolerance; 1 mismatch; 3 unreadable input or a failed comparison; 77 NumPy missing |
| `crossval_impedance.py` | 0 within tolerance; 1 mismatch; 3 unreadable input or a failed comparison; 77 not checked (no converter parameters) or NumPy missing. A `--grid real` export compares Y_conv and L only (the network's own Z_grid is not rebuilt) |
| `crossval_h0.py` | 0 every runnable check passes; 1 mismatch; 3 unreadable export |
| `lake_conformance.py` | 0 the sweep ran, including when no stored period matched; with `--gate`, 1 when any audited period is not physically admissible; 2 no lake configured or lake directory missing; 77 a package missing |
| `gridsim physics-check` | 0 `GATE: PASS`; 1 `GATE: FAIL` on a converged state, or the case cannot be loaded; 2 no case argument or a bad `--tol`; 3 the power flow did not converge |
| `gridsim conformance` | 0 every converged case passed and every unconverged case is on the allowance list (the shipped corpus); 1 a converged case failed the gate; 3 a case did not converge without an allowance, or could not be run (3 wins over 1); 2 a bad `--tol` |
| `gridsim simdcheck` | 0; 3 `--expect` names a wider tier than the active one; 2 an unknown `--expect` value |
| `gridsim batchsolve` | 0 the largest difference is at most `1e-6` and both sides agree on which systems are singular; 1 otherwise; 2 `count x n x n` exceeds the single-dispatch limit |
| `gridsim gpucheck` | 0 whatever it prints |

Files go to `results/` beside the executable. Run from a build and that is `src/GridSim.Cli/bin/Release/net10.0/results/`,
which is the path every capture below prints. Timings, commit stamps and machine lines in captured output differ by
machine and run. The captured blocks transliterate non-ASCII characters in the console output (the console
prints a Greek capital delta in the `batchsolve` line, the block shows `Delta`).

## crossval: power flow against pandapower

### Worked example

```bash
gridsim crossval case14
```

```
$ gridsim crossval case14
[crossval] solved 'case14' (converged, 3 iters) -> results\crossval-case14\solve-export.json
[crossval] solve tolerance 1E-14 pu (the tightest of 1e-8 ... 1e-15 that converged; final mismatch 4.302E-015 pu)
[crossval] Python: C:/.../venv/Scripts/python.exe (from GRIDSIM_PYTHON)

=== Cross-validation vs pandapower: case14 ===
  buses compared: 14
  max |delta Vm|: 4.441E-016 pu   max |delta Va|: 1.085E-015 rad (6.217E-014 deg)
  tolerance: 9.095E-013 pu, 9.095E-013 rad   -> AGREES

  -> ...\results\crossval-case14
[exit 0]
```

The verb solves the case with the selected engine (generator reactive limits on, up to 100 iterations), then solves
it again afresh at 1e-9, 1e-10, ... 1e-15 pu and keeps the tightest converged state (at the everyday 1e-8 pu Newton
stops with a residual just under the tolerance, and the state carries about J^-1 times that, 5.5e-12 pu on case14;
that is a convergence setting, not an error). It writes `solve-export.json` and hands it to
`tools/crossval/crossval.py`, which rebuilds the network as a MATPOWER case, makes it the same mathematical problem
for pandapower (generators aggregated per bus as GridSim holds them, transformer charging as bus shunts, transformers
written high-voltage end first, pi transformer model, reactive limits on both sides, no distributed slack), checks
that the admittance matrix pandapower solved equals MATPOWER's `makeYbus` on the exported rows, solves to pandapower's
own tightest tolerance and diffs the bus voltages.

### Reading the output

- The `[crossval] solved` line is GridSim's own solve: converged or `DID NOT CONVERGE`, the iteration count and the
  export path. The `solve tolerance` line names the tightest tolerance that converged and the exported state's own
  final mismatch. The export is the input to the diff and stays on disk.
- `buses compared` counts the buses present on both sides.
- `max |delta Vm|` is the largest voltage-magnitude difference in per-unit. `max |delta Va|` is the largest angle
  difference, in radians and degrees, after both angle sets are referred to the slack bus (an angle reference is a
  choice, not an error). `crossval-diff.json` carries the worst buses, one row per bus, and a `like_for_like` block:
  generator units before and after aggregation, the transformer rewrites, the admittance-matrix guard, both tools'
  tolerances, GridSim's final mismatch and both tools' reactive-limit outcomes.
- `tolerance` is 4096 machine epsilons (9.09e-13) unless `--tol` is given, applied to |V| in pu and to the angle in
  radians: the number the impedance, small-signal and harmonic sidecars use.
- `AGREES` means both differences are inside their tolerances (exit 0); `MISMATCH` means one is not (exit 3).

Measured on every bundled case the verb takes (2026-10-05, pandapower 3.5.5): the worst |V| difference is 2.64e-13 pu
and the worst angle difference 3.94e-13 rad, both on gb-full, a 2.3x margin inside the tolerance; the IEEE cases sit
at 4e-16 to 5e-14 pu. One case is judged under a documented allowance, `gb-network-2224`: its admittance matrix spans
up to 4.1e5 pu per row, so neither tool can drive the mismatch below about 4e-11 pu, and pandapower disagrees with
itself by 3.6e-12 rad between a flat and a DC start; its bound (2e-12 pu, 4e-12 rad) is that self-spread. Agreement
worse than the tolerance on any other case means something is wrong. Do not loosen `--tol` to make it pass.

### Variations

```bash
gridsim crossval data/matpower/lib/case30.m --tol 1e-6      # a MATPOWER file, a tighter tolerance
gridsim crossval data/gb-synthetic                          # a JSON case directory (has system.json)
gridsim crossval path/to/solve-export.json --out my-diff    # diff an export you already have
```

A case directory loads the way the app loads it (demand folded in, generators dispatched, taps applied). An existing
`solve-export.json` skips the solve. `--out` sets the output directory; the default is
`results/crossval-<case name>/`. The flags are in [the CLI reference](<0019 CLI reference.md#crossval>).

### Pitfalls

Pandapower absent. The export is written, the diff is not, and the exit code is 77:

```
$ gridsim crossval case9
[crossval] solved 'case9' (converged, 4 iters) -> src\GridSim.Cli\bin\Release\net10.0\results\crossval-case9\solve-export.json
[crossval] sidecar unavailable (python): crossval: pandapower not installed (pip install pandapower)
           install pandapower to run the diff (pip install pandapower). The export is at src\GridSim.Cli\bin\Release\net10.0\results\crossval-case9\solve-export.json.
[exit 77]
```

No Python at all gives the same exit code with the message `no Python interpreter found on PATH`. A Python exit 2
from the sidecar (the script could not start, or it refused its arguments) is a failure, exit 3, not a skip.

Pandapower fails where GridSim converges. The second solver may not reach a solution, and a non-convergence there is
not evidence against a state that satisfies the equations: the sidecar exits 4 (`pandapower solve failed`) and the
verb exits 3. Settle it with the residual check below, which needs no second solver.

The translation guard fires. `crossval: pandapower solve failed: translation guard: ...` (exit 4) means pandapower's
admittance matrix is not the exported network's, so the two tools would not be solving the same problem and no verdict
is given.

An export written by another verb. A `solve-export.json` from `--enableExport` is solved at the everyday 1e-8 pu, and
its state carries that residual (case14: 5.5e-12 pu, uk: 1.2e-11 pu). The verdict is MISMATCH and the sidecar says
why (`this export was converged only to a mismatch of ...`). Run `gridsim crossval <case>` instead.

The reactive-limit outcomes differ. `like_for_like.q_limit_outcome_agrees` false means one tool pinned a generator at a
limit the other did not, so they solved different final problems. The report's `passed` field (the exit code and the
CLI's AGREES / MISMATCH line) requires both `within_tolerance` and this agreement, so such a run fails even when the
voltages happen to agree. A bus pinned by pandapower only, whose GridSim
output sits within GridSim's 1e-3 MVAr limit slack, is listed under `q_limit_boundary_buses` and is the same state
(gb-network-2224 has six, units with Qmin = 0 solving at Q = 0).

A GridSim solve that did not converge still goes to the sidecar. The first line says `DID NOT CONVERGE`; read it
before the verdict.

## residual_check.py: does the answer satisfy the equations

### Worked example

```bash
python tools/crossval/residual_check.py src/GridSim.Cli/bin/Release/net10.0/results/crossval-case14/solve-export.json
```

```
$ python tools/crossval/residual_check.py src/GridSim.Cli/bin/Release/net10.0/results/crossval-case14/solve-export.json
{
  "schema": "gridsim-residual-check/1",
  "case": "?",
  "buses": 14,
  "tolerance_mva": 1e-06,
  "max_p_mismatch_mw": 2.0109451881467066e-09,
  "max_q_mismatch_pq_mvar": 1.3157705591737567e-08,
  "pv_qlimit_violations": 0,
  "violations": [],
  "within_tolerance": true
}
[exit 0]
```

This is the primary power-flow instrument. It rebuilds the bus admittance matrix in NumPy from the exported branch and
shunt data, evaluates `S = V conj(Y V)` at every bus from GridSim's solved voltages and compares it with the specified
injection (generation minus demand). It asks a stronger question than the pandapower diff: not "does a second solver
agree" but "does the answer satisfy the equations", and the question stays valid where a second solver cannot converge.

### Reading the output

- `max_p_mismatch_mw` is the largest real-power mismatch at any non-slack bus, in MW. The specified injection is the
  generation minus the load drawn at the solved voltage (a bus's `zip` block is honoured).
- `max_q_mismatch_pq_mvar` is the largest reactive mismatch at any bus the solve ended as PQ, in MVAr, read from the
  export's own solve trace (`bus_types`): a PV bus pinned at a reactive limit is checked against that limit
  (`pinned_pv_buses_checked`), a PV bus with no generator as PQ. The slack and buses still regulating as PV carry no
  reactive specification, so they are not tested.
- `pv_qlimit_violations` counts buses still regulating as PV whose implied reactive output sits outside the summed
  generator limits by more than `max(1e-3, tol)` MVAr; `violations` lists the first ten.
- `within_tolerance` is true when both mismatches are at most `--tol` and there are no violations. The default is the
  one documented rule, BaseMva x 1e-8 MVA (the solver's convergence tolerance on the case's base: 1e-6 MVA on a 100 MVA
  case, 1e-4 MVA on the 10,000 MVA `uk`). Exit 0 passes, 1 fails, 3 means the export could not be read or parsed, 77
  that NumPy is missing.

The same script settles the case300 failure above:

```
$ python tools/crossval/residual_check.py src/GridSim.Cli/bin/Release/net10.0/results/crossval-case300/solve-export.json
{
  "schema": "gridsim-residual-check/1",
  "case": "?",
  "buses": 300,
  "tolerance_mva": 1e-06,
  "max_p_mismatch_mw": 1.7928528649602968e-11,
  "max_q_mismatch_pq_mvar": 9.025656317832819e-11,
  "pv_qlimit_violations": 0,
  "violations": [],
  "within_tolerance": true
}
[exit 0]
```

GridSim's case300 state balances to 1.8e-11 MW. Pandapower's failure to converge is the second solver's basin of
attraction.

### Pitfalls

The default tolerance follows the case's base. The `uk` case (`gridsim crossval data/uk`, 10,000 MVA base) reads
1.06e-6 MW real and 2.48e-6 MVAr reactive mismatch: inside its default 1e-4 MVA, which is what its solve promised (1e-8
pu). An explicit `--tol` is one absolute line.

## physics-check: audit one state against the laws

### Worked example

```bash
gridsim physics-check case14
```

```
$ gridsim physics-check case14
Physics-law audit  -  case14  (14 buses, converged=True, tol 1.0E-006 MVA = BaseMva 100 x 1e-8)
| # | law | status | residual | unit | detail |
|---|-----|--------|----------|------|--------|
| 1 | Conservation of energy | Holds | 2.38E-013 | MW | independent dissipation 13.393272 MW (branch 13.3933 + shunt 0.0000) vs reported loss 13.393272 MW |
| 2 | Ohm's law | Holds | 9.59E-014 | MVA | independent per-branch flow vs solver-reported line flow |
| 3 | Joule heating | Holds | 2.34E-013 | MW | total network loss 13.393272 MW vs reported 13.393272 MW |
| 4 | Faraday induction | Constitutive | - | - | embodied in transformer tap ratio and generator EMF E' = V + jX'I; no field model to check |
| 5 | Ampere's law (passivity) | Constitutive | 3.33E-014 | MW | series reactance X embodies the lumped inductance; all branches passive (I^2 R >= 0) |
| 6 | Kirchhoff's current law | Holds | 2.01E-009 | MW | network real injection vs specified, at constrained buses |
| 7 | Kirchhoff's voltage law | Holds | 0.00E+000 | V | structural: nodal voltages are single-valued potentials, so every loop sum vanishes identically |
| 8 | Complex power S = V I* | Holds | 1.32E-008 | MVA | same residual as law 6, reactive component included at 9 PQ bus(es), by the bus types the solve ended with |
| 9 | AC phase & frequency synchronism | NotApplicable | - | Hz | NOT SCORED: steady-state snapshot at 50 Hz with no trajectory; law 9 is scored only when a dynamics caller supplies a SynchronismSample: stability --clear (classical or --rms) scores it from its own run, no steady-state verb or export does |
| 10 | Gauss's law / Coulomb | NotModeled | - | - | field/geometry law outside a positive-sequence phasor model; not represented |

GATE: PASS  -  state is physically admissible (all hard laws hold).
[exit 0]
```

The verb solves the case (generator reactive limits on, up to 100 iterations), recomputes every branch flow from the
solved voltages by its own per-branch pi-model path, and scores ten laws. It never calls the solver again, so a pass is
corroboration, not the solver agreeing with itself.

### Reading the output

- The header gives the case as you typed it, the bus count, whether the solve converged and the tolerance in MVA.
- `status` is one of five values. `Holds`: the residual is inside the tolerance. `Violated`: it is not.
  `Constitutive`: the law is embodied in model data (a transformer ratio, a series reactance) and is reported with its
  best consistency residual, never blocking. `NotModeled`: outside a positive-sequence phasor model. `NotApplicable`:
  the law needs an input this state does not carry. Law 9 (synchronism) reads `NOT SCORED: steady-state snapshot at
  <f> Hz with no trajectory; ...`: it is scored only from a dynamics run, and `stability --clear` (classical or `--rms`)
  scores it from its own trajectory ([ch. 15](<0015 RMS, modal and impedance stability.md>)).
- `residual` is in the unit of the next column. A `-` means the law has no residual.
- Laws 1, 2, 3, 6 and 8 are the hard laws. `GATE: PASS` means none of them is `Violated`. The other five are reported
  and never gate. Law 7 always reads `Holds` with residual 0, because nodal voltages are single-valued by construction.
- Laws 1 and 3 read the same total-loss sum two ways and agree by construction. Law 6 and law 8 share one residual:
  law 6 is the real part, law 8 the larger of the real and reactive parts.
- Exit 0 on `GATE: PASS`, exit 1 on `GATE: FAIL` for a converged state, exit 3 when the power flow did not converge,
  exit 2 without a case argument or for a bad `--tol`.

The residual definitions, and the reason each law is hard or not, are in technical ch. 16.

### Variations

```bash
gridsim physics-check case118 --tol 1e-6          # a tighter pass line (MVA)
gridsim physics-check data/matpower/lib/case30.m  # a MATPOWER file
gridsim physics-check data/uk                     # a JSON case directory
```

The flags are in [the CLI reference](<0019 CLI reference.md#physics-check>).

### Pitfalls

A negative-resistance branch breaks law 5 and leaves the gate green. case145 carries one, and the gate still passes
because passivity is a property of the model, not of the state:

```
$ gridsim physics-check case145
...
| 5 | Ampere's law (passivity) | Violated | 2.80E+002 | MW | NON-PASSIVE branch in the model: most-negative branch loss -2.80E+002 MW (R < 0 in the data) |
| 6 | Kirchhoff's current law | Holds | 1.35E-007 | MW | network real injection vs specified, at constrained buses |
...
GATE: PASS  -  state is physically admissible (all hard laws hold).
[exit 0]
```

The gate requires a converged solve (D-DOC-042, October 2026; before, it read the five hard laws alone, so an iterate
that did not converge but happened to satisfy them passed). A non-converged solve prints its laws for diagnosis and
ends `GATE: FAIL  -  the power flow did not converge (...)`, exit 3. The PGLib operating point `pglib_opf_case3_lmbd`
is an optimisation solution that a plain power flow does not converge on; its last iterate also fails law 6 at 676 MW:

```
$ gridsim physics-check data/matpower/nesta/pglib_opf_case3_lmbd.m
...
| 6 | Kirchhoff's current law | Violated | 6.76E+002 | MW | network real injection vs specified, at constrained buses |
...
GATE: FAIL  -  the power flow did not converge (status MaxIterations, max mismatch 6.76E+000 pu after 100 iterations); a non-converged state is not audited as admissible. Worst law at the last iterate: law 6 Kirchhoff's current law: residual 6.76E+002 MW above the tolerance 1.00E-006 MW (network real injection vs specified, at constrained buses)
...
[exit 3]
```

The audit is working, and the iterate it audited is not a power-flow solution. The worst law is named in a sentence
(`law 6 Kirchhoff's current law: residual ... MW above the tolerance ... MW (...)`). The `note:` lines on the error stream come from the `.m` loader (assumed
nominal frequency, gencost block read, angle-difference limits not read) and do not affect the verdict.

## conformance: the audit across the whole corpus

### Worked example

```bash
gridsim conformance
```

```
$ gridsim conformance
    GridSim  -  2026-10-20 05:22:24 UTC
    code:    git v1.95.294-173-g203c6b9c  |  GridSim.Core v1.95.467.0  |  build Release
    runtime: .NET 10.0.12  X64  GC=Server
    machine: LAPTOP  Microsoft Windows 10.0.26300
    cpu:     Intel(R) Core(TM) i3-N305  -  8 logical cores  -  8 GB RAM
    simd:    Avx256  (4 doubles/step)  FMA=True
    backend: cpu
    run:     conformance  cases=27  tol=BaseMva*1e-8  allowances=on

Physical-law conformance sweep. GATE = every hard law (energy/Ohm/Joule/KCL/complex-power)
holds, i.e. the solved state is physically admissible. NOTE: lake-scale sweep over stored
estimated/presolved states runs via tools/crossval/physics_audit.py (this verb sweeps cases).
Tolerance: BaseMva x 1e-8 MVA per case (the solver's convergence tolerance on that case's base).

| case | buses | conv | gate | worst hard law | residual |
|------|------:|:----:|:----:|----------------|---------:|
| GBnetwork | 2224 | yes | PASS | Complex power S = V I* | 7.29E-009 |
| GBreducednetwork | 29 | yes | PASS | Complex power S = V I* | 1.72E-010 |
| pglib_opf_case3_lmbd | 3 | no | allowed | (unconverged: MaxIterations; known, see below) | - |
| pglib_opf_case5_pjm | 5 | yes | PASS | Complex power S = V I* | 3.57E-009 |
| case9 (WSCC) | 9 | yes | PASS | Kirchhoff's current law | 1.69E-012 |
| case14 | 14 | yes | PASS | Complex power S = V I* | 1.32E-008 |
...
| pglib_opf_case162_ieee_dtc | 162 | no | allowed | (unconverged: MaxIterations; known, see below) | - |
| pglib_opf_case240_pserc | 240 | no | allowed | (unconverged: MaxIterations; known, see below) | - |
...
| case9241pegase | 9241 | yes | PASS | Complex power S = V I* | 9.95E-009 |

Conformance: **24/24** converged cases physically admissible (all hard laws hold); 0 skipped; 3 known unconverged (allowed); 0 unconverged and not allowed.

Allowed unconverged cases (the documented list in src/GridSim.Cli/ConformanceAllowances.cs; --no-allowances turns it off):
- pglib_opf_case3_lmbd: PGLib-OPF 3-bus optimisation operating point (data/matpower/nesta); Newton from its stored voltages does not converge in 100 iterations
- pglib_opf_case162_ieee_dtc: PGLib-OPF 162-bus optimisation operating point (data/matpower/nesta); Newton from its stored voltages does not converge in 100 iterations
- pglib_opf_case240_pserc: PGLib-OPF 240-bus optimisation operating point (data/matpower/nesta); Newton from its stored voltages does not converge in 100 iterations
...
[conformance] 24/24 admissible -> results\conformance\conformance.md
[exit 0]
```

The verb runs the `physics-check` audit on the nine embedded cases (`case9 (WSCC)`, `case14`, `case30`, `case57`,
`case118`, `case145`, `case300`, `case24_ieee_rts` and `cigre_mv`) and on every `.m` file under `data/matpower/` whose
file stem is not one of those nine, which are skipped as copies of the embedded cases. The corpus is 9 embedded cases
and 18 files. Rows are ordered by a size key and then by label: the key is the bus count for an embedded case and, for
a file, the first six digits of all the digits in the stem joined together (0 when the stem has none), so the two GB
files, which carry no digits, come first.

### Reading the output

- The indented header is the run stamp: commit, runtime, machine, SIMD tier, backend, case count and tolerance.
- `conv` is whether the power flow converged within 100 iterations. A case that did not converge shows `no` and its
  solver status in the `worst hard law` column, and is not audited.
- `gate` is `PASS` when all five hard laws hold, `**FAIL**` otherwise.
- `worst hard law` and `residual` name the hard law with the largest residual and its value. Laws 6 and 8 share a
  residual, so a row reads `Kirchhoff's current law` when the real-power term dominates and `Complex power S = V I*`
  when the reactive term does.
- The `Conformance:` line counts passes over converged cases. The `note:` lines are the `.m` loader's remarks (an assumed
  50 Hz nominal frequency, a gencost block read, raw engineering units converted) and go to the error stream with the
  `[conformance]` line, which gives the path of `conformance.md` (the table above). `conformance.csv` (one row per
  converged case per law: case, buses, converged, gate_pass, law_number, law_name, status, residual, unit, tolerance,
  commit) and `run-metadata.json` are written beside `conformance.md`.

### The known unconverged cases

The three unconverged cases are `pglib_opf_case3_lmbd`, `pglib_opf_case162_ieee_dtc` and `pglib_opf_case240_pserc`.
Each is a PGLib optimal-power-flow operating point: an optimisation solution rather than a power-flow start, and plain
Newton-Raphson from its stored voltages does not converge in 100 iterations (status `MaxIterations`). They are on the
verb's explicit allowance list (`src/GridSim.Cli/ConformanceAllowances.cs`), each with its
reason, so they show `allowed` in the gate column, are listed with their reasons under the table, and the shipped corpus
exits 0. An allowance covers only that case ending with that status: a fourth unconverged case, one of the three ending
`NonFinite`, a case that cannot be loaded, or a `FAIL` in the gate column still fails the run (exit 3 or 1).

### Variations

```bash
gridsim conformance --tol 1e-6 --out results/conf-strict
```

`--tol` sets one absolute pass line for every case (by default each case uses its own base: BaseMva x 1e-8 MVA, the
solver's convergence tolerance); `--out` changes the directory; `--no-allowances` fails the run on the known PGLib
cases too. Exit codes: 0 every converged case passed and every unconverged case is allowed; 1 a converged case failed
the gate; 3 a case did not converge without an allowance or could not be loaded. When both happen the verb exits 3.
The flags are in [the CLI reference](<0019 CLI reference.md#conformance>).

### Pitfalls

An `allowed` row is still an unconverged case: it was not audited. Run with `--no-allowances` when every case must
converge.

## Estimation checks: se_residual_check.py, crossval_estimation.py, mc_estimate_sweep.py

### Worked example

Make a deterministic noisy measurement set from a solved case, estimate it, and check the estimate:

```bash
gridsim estimate case14 --synth --out results/est-case14
python tools/crossval/se_residual_check.py results/est-case14 --leverage --fd
python tools/crossval/crossval_estimation.py results/est-case14
```

```
$ gridsim estimate case14 --synth --out results/est-case14

=== WLS state estimate: case14 ===
  status: Converged   iterations: 5   max |dx|: 2.72E-009
  measurements: 122   states: 27   redundancy (dof): 95
  observability: observable (rank 27/27)
  consistency: J = 80.01 vs chi2(95%) = 118.75 -> PASS
  vs synthetic truth: max |dVm| = 1.330E-003 pu   max |dVa| = 2.248E-001 deg

  -> results\est-case14
[exit 0]
```

```
$ python tools/crossval/se_residual_check.py results/est-case14 --leverage --fd
[se-check] residual honesty: max |(z - h_indep) - reported| = 3.877e-13
[se-check] objective: recomputed J = 80.010522  reported = 80.010522  |dJ| = 2.132e-13
[se-check] --fd self-check: max |analytic - central-difference| = 6.594e-08
[se-check] optimality: independent gauss-newton step |dx|_inf = 5.736e-12
[se-check] leverage: max K = 0.751022659   export flags 0 critical

[se-check] PASS: residuals honest, objective consistent, x_hat is the independent WLS optimum
[exit 0]
```

```
$ python tools/crossval/crossval_estimation.py results/est-case14
There are 3 branches which are considered as trafos - due to ratio unequal 0 or 1 - but connect same voltage levels.
{
  "schema": "gridsim-crossval-estimation/1",
  "tool": "pandapower.estimation",
  "case": "case14",
  "buses_compared": 14,
  "max_dvm_pu": 5.5642157548163595e-12,
  "max_dva_deg": 7.739942020634771e-11,
  "within_tolerance": true,
  "rows": [
...
[exit 0]
```

`se_residual_check.py` reads the `estimate-export.json` in the run directory and uses no GridSim arithmetic and no
second estimator. `crossval_estimation.py` feeds the identical measurement set to pandapower's WLS estimator and diffs
the two estimated states. The estimator itself is the subject of [ch. 21, State estimation](<0021 State estimation.md>).

### Reading the output

`se_residual_check.py` prints one line per check and a verdict:

- `residual honesty` compares the residuals the export reports with `z - h(x_hat)` recomputed from an independently
  built admittance matrix. It fails above `--tol` (default `1e-6`, in the measurement's engineering unit). A failure
  means the export misreports its own residuals.
- `objective` recomputes `J = sum((r / sigma)^2)` and compares it with the reported value. It fails above
  `max(1e-6, 1e-9 * max(J, 1))`.
- `--fd` checks the script's own analytic Jacobian against central differences and fails above `1e-6`. It evaluates the
  measurement function twice per state, so use it on small cases.
- `optimality` takes one Gauss-Newton step from `x_hat` with the independent Jacobian. At a true weighted-least-squares
  minimum the step is negligible; it fails above `--tol` (pu and radians). A failure means the estimator stopped
  somewhere that is not the minimum.
- `leverage` (only with `--leverage`) recomputes the diagonal of the residual covariance and arbitrates the export's
  critical flags: a flagged measurement is `CONFIRMED` when its leverage `K` exceeds `1 - 1e-6`, otherwise it prints
  `NOT critical (numerical artifact)` and fails; an unflagged measurement with `K` above that line also fails.

The script handles every measurement kind the estimator accepts: `VmPu`, `VaDeg`, `PInjMw`, `QInjMvar`, `PFlowMw`,
`QFlowMvar`, `IMagPu` and `IAngDeg`. Any other kind exits 3 and names the kind. Exit 0 passes and 1 fails. Exit 3 means
the schema tag is wrong, a measurement's branch cannot be resolved or a kind is unsupported. A missing or malformed
file is an uncaught exception and exits 1.

`crossval_estimation.py` prints a JSON report. `max_dvm_pu` and `max_dva_deg` are the largest differences after both
state sets are referred to the slack; `within_tolerance` compares them with `--tol-vm` (default `1e-6` pu) and
`--tol-va` (default `1e-5` degrees); `rows` has one entry per bus. It compares the measurements the estimate was
finally computed from, that is, after GridSim's bad-data removals. Exit 0 within tolerance, 1 mismatch, 2 pandapower
absent, 3 unreadable export or an unsupported kind (`IMagPu` and `IAngDeg` are refused because pandapower's current
measurements are in kA on its own base), 4 pandapower estimation crashed. It accepts any `gridsim-estimate-export/1`,
synthetic or real-data; the agreement recorded in [technical ch. 11](<../technical/0011 Validation.md>) was measured
on seeded synthetic sets.

With `--bad-data` the script feeds pandapower the original set, gross errors included, runs its chi-square test and
largest-normalised-residual removal, and checks that both tools agree on whether bad data is present: both removed
something, or neither did. It does not compare which measurement each tool removed. The report lists GridSim's first
removal for you to compare by eye.

### The Monte-Carlo sweep

```bash
python tools/crossval/mc_estimate_sweep.py                  # case118, 10 seeds
python tools/crossval/mc_estimate_sweep.py --case case30 --seeds 5
```

```
$ python tools/crossval/mc_estimate_sweep.py
  full/n0.5  runs=10 dof=863 dVm mean 1.34e-03 max 2.54e-03 pu   chi pass 10/10
  full/n1    runs=10 dof=863 dVm mean 2.68e-03 max 5.07e-03 pu   chi pass 10/10
  full/n2    runs=10 dof=843 dVm mean 6.28e-03 max 1.12e-02 pu   chi pass 0/10
  half-flow  runs=10 dof=491 dVm mean 3.06e-03 max 3.98e-03 pu   chi pass 10/10
  no-flow    runs=10 dof=119 dVm mean 3.64e-03 max 4.92e-03 pu   chi pass 10/10
  sparse     runs=10 dof=244 dVm mean 3.79e-03 max 5.30e-03 pu   chi pass 10/10
  inj-only   runs=10 dof=30 dVm mean 5.25e-03 max 8.66e-03 pu   chi pass 10/10
[ok] tools\crossval\mc_estimate_sweep_case118.json
[exit 0]
```

The sweep runs `gridsim estimate <case> --synth` over seeds and seven fixed configurations (noise scale and
measurement coverage) through this checkout's Release build, so build it first, or through the CLI that `--cli <path>`
(or the `GRIDSIM_CLI` environment variable) names, such as a published build; a path ending in `.dll` is run through
`dotnet`. Each row is the mean and worst estimate-versus-truth voltage error over the seeds that converged, with the
number of runs whose chi-square test passed. It writes the same numbers to `tools/crossval/mc_estimate_sweep_<case>.json`,
or to the file `--out <path>` names (for a read-only tree). It is an analysis, not a gate: it exits 0 when it ran and 2
when the CLI is missing. Read three properties in the table:

- Error scales with noise: `full/n1` to `full/n2` doubles the noise and the mean `dVm` rises from 2.68e-03 to
  6.28e-03 pu, a factor of 2.34. The `dof` column is the first seed's value. For `full/n2` it is 843, not 863: the
  bad-data loop removed 20 of the 1,098 measurements, so the two rows differ in redundancy as well as noise.
- Error rises as redundancy falls, but not monotonically: from `dof=863` to `dof=30` (`full/n1` to `inj-only`) the mean
  `dVm` rises by a factor of 1.96, yet `sparse` (`dof=244`) reads 3.79e-03 and `no-flow` (`dof=119`) reads 3.64e-03.
  The configurations change which measurement kinds are present, not only how many.
- The chi-square gate follows the declared sigmas: `full/n2` corrupts the data to twice its declared noise and fails
  0/10, while every correctly declared configuration passes 10/10.

### Pitfalls

A missing NumPy or SciPy is a skip: `se_residual_check.py` checks both before any work, prints `SKIPPED (exit 77)` with
the package and the interpreter, and exits 77 (nothing validated). `crossval_estimation.py` does the same for a
missing NumPy or pandapower, so a skip is never confused with a failed check or a bad argument.

A chi-square pass rate that stays high under `full/n2`, or an error that explodes rather than doubling as coverage
falls, means the sigma model or the gain matrix is mis-built.

## Other sidecars

Each of these reads an export a GridSim verb writes and re-derives the physics with no GridSim code.

- `crossval_freq.py` re-integrates the swing equation of a `gridsim-frequency-export/1` document with SciPy RK45 and
  compares the initial rate of change of frequency and the nadir. `replay-event` writes the export as
  `baseline-frequency.json` and `scenario` as `counterfactual-frequency.json`.
- `crossval_classical.py` re-integrates the classical multi-machine swing of a `gridsim-classical-export/1`
  document (`gridsim stability <case> --export file.json`).
- `crossval_smallsignal.py` recomputes the eigenvalues of a `gridsim-smallsignal/1` state matrix with
  `scipy.linalg.eig` (`gridsim modal <case> --export file.json`).
- `crossval_harmonics.py` rebuilds the harmonic admittance `Y(h)` and solves `Y(h) V(h) = I(h)` per order from a case
  directory, a `gridsim-harmonics/1` source set and a `gridsim-harmonic-export/1` study.
- `crossval_impedance.py` rebuilds the converter admittance, grid impedance, return ratio and critical short-circuit
  ratio of a `gridsim-impedance-stability/1` export.
- `crossval_h0.py` re-derives the arithmetic of every cell of a `gridsim-h0-study/1` export and re-integrates the
  forming swing of representative cells.

### Worked example

`gb-largest-loss-1320` is a curated screening event, not a record, so `replay-event` refuses it (only an event sourced
from a record is replayed); `scenario` studies it and writes its counterfactual swing as a frequency export:

```bash
gridsim scenario --event gb-largest-loss-1320 --out results/scen-gb-largest-loss-1320
python tools/crossval/crossval_freq.py results/scen-gb-largest-loss-1320/counterfactual-frequency.json
```

```
$ python tools/crossval/crossval_freq.py results/scen-gb-largest-loss-1320/counterfactual-frequency.json
{
  "schema": "gridsim-crossval-freq/1",
  "tool": "scipy.integrate.solve_ivp",
  "checked": true,
  "rocof_gridsim": -0.41250000000000003,
  "rocof_scipy": -0.4120334790402467,
  "d_rocof": 0.00046652095975335817,
  "nadir_gridsim": 49.44284811245624,
  "nadir_scipy": 49.441093818852316,
  "d_nadir": 0.0017542936039234291,
  "within_tolerance": true
}
[exit 0]
```

GridSim's 20 ms explicit-Euler trace and SciPy's high-order integrator agree to 4.7e-4 Hz/s on the initial RoCoF and
1.8e-3 Hz on the nadir. The script's gates are 0.02 Hz/s (`--tol-rocof`) and 0.05 Hz (`--tol-nadir`), so the agreement
is more than an order of magnitude inside them. The recorded events' `replay-event` exports (`gb-2019-08-09`,
`gb-2023-12-22`) carry recorded response services, so the script reports them `NOT CHECKED` (exit 77). When the export carries recorded response services or an offset pre-event start
the re-integration does not model that physics: the script prints `NOT CHECKED` with the reason and exits 77 (the other
exit codes are in the table above).

The classical and small-signal checks print JSON with the same shape (`within_tolerance`, or
`eigenvalues_within_tolerance`):

```
$ python tools/crossval/crossval_classical.py results/cl9.json
{
  "schema": "gridsim-crossval-classical/1",
  "tool": "scipy.integrate.solve_ivp (RK45)",
  "case": "WSCC-9",
  "machines": 3,
  "samples": 101,
  "max_abs_angle_diff_rad": 3.74656974333476e-08,
  "tolerance_rad": 0.001,
  "within_tolerance": true
}
[exit 0]
```

```
$ python tools/crossval/crossval_smallsignal.py results/ss9.json
{
  "schema": "gridsim-crossval-smallsignal/1",
  "tool": "scipy.linalg.eig (LAPACK)",
  "case": "WSCC-9",
  "states": 6,
  "inverterStatesIncluded": false,
  "pairing": "optimal assignment (scipy.optimize.linear_sum_assignment) on |gs_i - sp_j| / max(|sp_j|, 1), the per-mode relative distance the gate uses",
  "max_relative_eig_diff": 3.120884673715049e-08,
  "worst_pair": {
    "gridsim": [
      0.0,
      0.0
    ],
    "scipy": [
      1.2888762506743327e-15,
      3.120884673715046e-08
    ]
  },
  "rtol": 9.094947017729282e-13,
  "condV": 19279336511.714058,
  "accept_threshold_rtol_times_condV": 0.017534454411101304,
  "eigenvalues_within_tolerance": true,
  "participation_max_abs_diff": 0.38713555137155176,
  "participation_compared": true,
  "participation_unavailable_reason": null,
  "gridsim_exit_code": 0,
  "note": "non-symmetric eig agreement is cond(V)-limited, not machine-eps; condV ~ 1 means machine-eps-clean, condV >> 1 means conditioning-limited."
}
[exit 0]
```

### Reading the output

For the classical check, `max_abs_angle_diff_rad` is the largest rotor-angle difference over every machine and sample
against `tolerance_rad` (`--tol`, default `1e-3`). For the small-signal check, `max_relative_eig_diff` is the largest
per-mode relative eigenvalue difference. A non-symmetric eigensolve agrees only to the conditioning of the eigenvector
matrix, so the gate is `rtol * max(1, condV)` and `condV` is printed; `condV` near 1 means the agreement is machine
epsilon clean, `condV` far above 1 means it is conditioning-limited. `participation_max_abs_diff` is reported and does
not gate; `participation_compared` says whether they were compared, and `participation_unavailable_reason` carries the
export's reason when it marks its participation rows unavailable (a defective state matrix). GridSim's eigenvalues and SciPy's are paired by the
one-to-one assignment that minimises the total relative distance (`scipy.optimize.linear_sum_assignment`); `pairing`
names the method and `worst_pair` gives the worst-paired eigenvalues. The default `--rtol` is 4096 machine epsilons,
9.09e-13.

The harmonic and impedance checks pass a comparison when `|mine - GridSim| <= atol + rtol * |GridSim|`, with `--tol`
(`rtol`) and `--atol` each defaulting to 4096 machine epsilons, 9.09e-13 (the absolute one in pu). Both exit 3 on
unreadable input. `crossval_impedance.py` also exits 2 when NumPy is missing and exits 77 when the export has no
`converterParams` (a grid-forming, asset-class or real-network run), because no independent derivation ran. The
measured agreement behind the defaults is in [technical ch. 11](<../technical/0011 Validation.md>).

### Pitfalls

The small-signal gate scales with `condV`, so on a near-defective state matrix it is far looser than on a
well-conditioned one. The case9 output above accepts a relative difference up to 0.0175 against a measured 3.1e-08.
Judge the run by `max_relative_eig_diff` and by `condV`, not by the verdict alone.

## simdcheck: which vector width is running

### Worked example

```bash
gridsim simdcheck
```

```
$ gridsim simdcheck
GridSim SIMD preflight
======================
CPU cores: 8   OS: Microsoft Windows 10.0.26300
process arch: X64
commit: v1.95.294-173-g203c6b9c

SIMD: Avx256 (4 doubles/step)  FMA=yes  [Avx512F=False Avx2=True Fma=True Sse2=True AdvSimd=False]

Active vector tier: Avx256 (4 doubles per vector step)
Dense-LU / normal-equations / covariance kernels dispatch to this width at run time.
If this reports Avx256 on a box you expect to be AVX-512, the CPU or the runtime is capping the width.
[exit 0]
```

### Reading the output

The `SIMD:` line names the active tier and the number of doubles per vector step, then each instruction-set flag the
runtime reports. The tiers, widest first, are `Avx512` (8 doubles), `Avx256` (4), `Sse128` (2) and `Scalar` (1). The
dense LU, the normal equations and the covariance kernels dispatch to the active tier at run time. The row-elimination
kernel of the dense LU (`SubtractScaledRow`) and the normal-equation accumulation without fused multiply-add
(`AddScaledRow` with `fastFma: false`) do a separate multiply and add per lane, so they match the scalar loop to the bit
at every tier (`SimdKernelsTests`, and `SimdTierSeamTests`, which drives all four tiers). Every numerical caller,
the dense WLS normal-equation accumulation (`DenseWlsStep.FormNormalEquations`) included, passes `fastFma: false`. The
dot-product kernel (`Dot`, used by the dense bad-data covariance and the classical multi-machine swing) sums in one
defined order of four partial sums with no fused multiply-add, and the batch sine and cosine kernel is the
deterministic fdlibm function per element, so both give the same bits at every tier and on every CPU
(`D1CrossTierTests`). The tier changes speed, not the last bits.
When the CPU supports AVX-512F but the active tier is
narrower (the .NET default on Skylake-SP and Cascade Lake Xeons), the verb prints a warning that names the fix: set
`DOTNET_PreferredVectorBitWidth=512` before start-up.

### Variations

`--expect <tier>` turns the preflight into a gate. It exits 0 when the active tier is at least the expected one and 3
when it is narrower:

```
$ gridsim simdcheck --expect Avx512
...
simdcheck: FAILED - expected at least Avx512, but the active tier is Avx256.
[exit 3]
```

```
$ gridsim simdcheck --expect Avx256
...
simdcheck: OK - active tier Avx256 meets the expected Avx256.
[exit 0]
```

Use it in CI on a machine that must run 8-wide. A value other than `Avx512`, `Avx256`, `Sse128` or `Scalar` exits 2.
The flag is in [the CLI reference](<0019 CLI reference.md#simdcheck>).

## batchsolve: the batched dense solver against its reference

### Worked example

```bash
gridsim batchsolve --count 4 --n 32 --runs 2
```

```
$ gridsim batchsolve --count 4 --n 32 --runs 2
GridSim  -  batched dense solve: 4 systems x 32x32   backend batched-dense-lu (cpu)
==============================================================================
correctness: 4/4 solved   max|Delta| vs CPU reference = 0.000E+000
timing (median of 2): backend 15.1 ms   cpu-reference 0.1 ms   backend 265 solves/s
[exit 0]
```

The verb builds `--count` deterministic, strongly diagonally dominant `n x n` systems (fixed seed), solves them in one
batched dispatch on the selected backend and again with the CPU reference LU, and compares the answers.

### Reading the output

- The title names the batch and the backend that ran it (`batched-dense-lu (cpu)` here; a CUDA or OpenCL device
  appears when `--gpu` selects one and the device answers).
- `correctness` counts the systems solved and prints the largest absolute difference between the backend's answer and
  the CPU reference. Zero here: the CPU batched solver and the reference are the same arithmetic.
- `timing` is the median over `--runs` repeats for the backend and for the CPU reference, and the backend's throughput.
  On a batch this small the CPU reference is faster than the dispatch. On a GPU the first call is dominated by kernel
  compilation, not the solve.
- Exit 0 when the largest absolute difference is at most `1e-6` and the backend and the CPU reference agree on which
  systems are singular. Exit 1 when the difference is larger or the two sides disagree on a singular system. Exit 2 when
  `count x n x n` exceeds the single-dispatch limit (reduce `--count` or `--n`). A singular system returns `NaN` for its
  segment, which is not counted as solved, and never fails the rest of the batch: a system that is singular on both sides
  still exits 0.

The flags are in [the CLI reference](<0019 CLI reference.md#batchsolve>).

## gpucheck: a fail-fast backend preflight

```bash
gridsim gpucheck
```

```
$ gridsim gpucheck
GridSim GPU preflight
=====================
CPU cores: 8   OS: Microsoft Windows 10.0.26300
commit: v1.95.294-173-g203c6b9c

[CPU] cpu   (vendor=Cpu)
  CPU dense: build solver... solve case9... PASS  (4 it, 61.0 ms, |mis|=1.6E-014)
  CPU sparse: build solver... solve case9... PASS  (4 it, 3.3 ms, |mis|=1.6E-014)

[ROCm] cpu (rocm requested but unavailable)   (vendor=Cpu)
[ROCm] raw probe: ROCm unavailable: HIP runtime library (amdhip64) not found
[ROCm]   not present - skipped.
...
[OpenCL] raw probe: Intel(R) UHD Graphics [OpenCL] rejected: no usable FP64 (cl_khr_fp64) - GridSim needs double precision
[OpenCL]   not present - skipped.
...
Preflight done. A FAIL line names the failing call; if the process died mid-line, that call
segfaulted (a P/Invoke signature mismatch) - send that last line and I'll fix the binding.
...
[exit 0]
```

The verb reports each backend's availability and runs one small case9 solve on each backend that answers, flushing every
line. The CPU dense and sparse lines show the same iteration count and mismatch, because the two engines are
bit-identical. A backend that is absent prints `not present - skipped`; an OpenCL device without double precision is
rejected. The `[gpu]` lines at the end are the backend selector's fall-back messages on the error stream. A `FAIL` line
names the failing call; if the process dies mid-line, that call crashed and the last printed line identifies it. `ran
but NOT converged` on case9 means the backend's arithmetic is wrong, because case9 converges on a healthy build. The
verb never sets an exit code: it exits 0 whether it prints `PASS`, `FAIL` or `ran but NOT converged`, so judge it by its
lines.

## bench

`gridsim bench` runs each engine that is available over the corpus (the same 27 cases as `conformance`, Q limits off,
100 iterations) and records convergence, iterations, mismatch and mean time per case. The GPU engines run only with
`--gpu` and skip cases above 3,000 buses, and `bfs` runs only on radial feeders. `gridsim bench --estimate` does the same
for the state estimator. They are performance instruments: `bench` reports and does not assert, and exits 0 whatever the
cells read. The requirement that the CPU dense and sparse engines agree to the bit lives in
`DenseSparseParityTests` ([technical ch. 11](<../technical/0011 Validation.md>)). [Chapter 28, Benchmarking and GPU](<0028 Benchmarking and GPU.md>),
covers them in full. The flags are in [the CLI reference](<0019 CLI reference.md#bench>).

## Limits

- `se_residual_check.py`: a missing or malformed export is an uncaught exception, exit 1 (a missing package is exit 77).
- `physics_audit.py` on a plain WLS `estimate-export.json` exits 77: such an estimate fixes no injection outside its own
  state and the export carries no reported loss, so no independent comparison applies (the exported block is still
  checked for consistency with its basis).
- `mc_estimate_sweep.py` scores only the seeded synthetic measurement sets it generates. The recorded agreement of
  `crossval_estimation.py` was measured on synthetic sets against one pandapower version; the last digits depend on that
  version.
- `gridsim gpucheck` always exits 0, even when it prints `FAIL` or `ran but NOT converged`.
- `Dot` and the dense WLS normal-equation accumulation (`DenseWlsStep.FormNormalEquations`) are not bit-identical across
  vector tiers or between CPUs with and without FMA (see simdcheck above).

## See also

- [Technical ch. 11, Validation](<../technical/0011 Validation.md>) - every cross-check, its tolerance and its recorded result.
- [Technical ch. 16, Physics conformance and the constrained estimator](<../technical/0016 Physics conformance and the constrained estimator.md>) - the ten
  laws, their residuals and the hard-gate set.
- [Ch. 21, State estimation](<0021 State estimation.md>) - the `estimate` verb these checks validate.
- [Ch. 30, Troubleshooting](<0030 Troubleshooting.md>) - expected non-convergence and chi-square failures.
- [Ch. 19, CLI reference](<0019 CLI reference.md>) - flag tables for [`crossval`](<0019 CLI reference.md#crossval>),
  [`physics-check`](<0019 CLI reference.md#physics-check>), [`conformance`](<0019 CLI reference.md#conformance>),
  [`simdcheck`](<0019 CLI reference.md#simdcheck>), [`batchsolve`](<0019 CLI reference.md#batchsolve>) and
  [`bench`](<0019 CLI reference.md#bench>).
