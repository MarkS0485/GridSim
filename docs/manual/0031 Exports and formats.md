# 31. Exports and formats

GridSim writes a file for almost every result it computes, and reads a small number of documents you author yourself.
This chapter is the field guide to those files: for each one, which verb writes it, where it lands by default, its
schema id, what every top-level field means to you, and a real excerpt from a captured run. It is the practical map.
The field-by-field wire reference, with serialiser names and reader tolerances, is
[technical chapter 10](<../technical/0010 File and wire formats.md>); the v2 case and library schemas are
[technical chapter 28](<../technical/0028 The v2 JSON schema.md>) and are only pointed to here.

Every excerpt below is pasted from a run of the current build under `docs/_captures/exports/`. The `$` line is the
command that produced it. Timings, timestamps, host names, commit stamps and build paths differ by machine and by run.
Iteration counts, mismatches and estimates are reproducible: the acceptance rule is bit-identical results across runs
and CPU modes (see [chapter 30](<0030 Troubleshooting.md>)).

## Conventions that hold for every file

**Where files land.** A verb with an output directory defaults to a folder under `results/` in the current directory
(`results/estimate-case14`, `results/emt-out`, `results/replay-gb-2019-08-09`). The path is relative, so it resolves
against the directory you ran the command from. An explicit `--out` (or `--enableExport=<path>`) is used exactly as
given. A missing folder is created. The desktop and web apps look for run directories in a `results/` folder near
the application, so runs started from the repository root are found.

**Casing and numbers.** JSON field names are camelCase and enums are written by name (`"Slack"`, `"Recorded"`), with
two exceptions. The estimate export writes `chiSquareTest` as a number (0, 1 or 2, see
[the estimate export](#gridsim-estimate-export1---the-estimate-export)). The `meta` block of the solve, estimate, harmonic and impedance-stability exports, and the three trace
records of the solve export (`ybus`, `busSpec`, `qLimitEvents`), keep their C# PascalCase property names
(`meta.Case`, `meta.GitCommit`, `ybus[].G`). The `meta` block of the frequency export and the H=0 study is camelCase.
Readers bind names case-insensitively, so both spellings read back. Numbers are invariant-culture (a `.` decimal
point), and the JSON exports write doubles in round-trip form, so reading a file back gives the exact bits GridSim
computed. Timestamps are UTC ISO-8601.

**Nulls.** Exports omit a field whose value is null, with two deliberate exceptions. The H=0 study has no omit rule:
it keeps every null field, including the figures that are undefined (RoCoF, nadir, settle frequency and the other
cell figures). The estimate export writes an explicit `null` for a chi-square test that did not run.

**Non-finite values.** Each writer has its own rule for a value that is not finite.

| Writer | A NaN, `Infinity` or `-Infinity` |
|---|---|
| Solve, estimate, harmonic, impedance-stability and small-signal exports | Written as the string `"NaN"`, `"Infinity"` or `"-Infinity"` (a generator limit of infinity, the normalised residual of a critical measurement). |
| `--json` result files (`gridsim-cli-result/1`) | The same three strings; the CSV files write the same spellings unquoted. |
| H=0 study | A NaN is written as `null`. An infinity makes the writer throw. |
| Every other JSON file GridSim writes (frequency, evidence, event catalogue, measurement set, spectra, point map, demand series, classical export, `fdia-screen.json`, `gridviz.json`) | The serialiser throws, so the verb fails rather than write a number that JSON cannot hold. |

**Additive fields.** A schema id ends in a version (`/1`). New fields are added inside the same version, and readers
ignore fields they do not know, so a file written by a newer build still loads in an older reader. Your own files are
held to the opposite rule where a typo would silently change a result: the cost, switched-shunt and three-phase feeder
files refuse an unknown key and name it.

**The 169-hour horizon.** Every dated input is checked when it is read: a measurement set's `takenUtc`, a harmonic
set's `takenUtc`, an event's `whenUtc` and every timestamp of a GDA demand series. A date less than 169 hours before
now, or in the future, is refused. GridSim replays history and never nowcasts; see
[chapter 22](<0022 Estimating GB history.md>).

## Where each file comes from

| File | Written by | Default path | Schema id or format | Section |
|---|---|---|---|---|
| `solve-export.json` and `exports/slice-*.json`, `exports/state-*.json` | `gridsim <case> --once --enableExport`, `transient`, `gda-replay`, `crossval`, `estimate --export-solve` | `results/exports/<case>-solve.json`; the others in their run directory | `gridsim-solve-export/1` | [Solve export](#gridsim-solve-export1---the-solve-math-export) |
| `estimate-export.json` and its sidecars | `estimate` | `results/estimate-<case>` | `gridsim-estimate-export/1` | [Estimate export](#gridsim-estimate-export1---the-estimate-export) |
| `fdia-screen.json` | `estimate --fdia-screen` | `results/estimate-<case>` | `gridsim-fdia-screen/1` | [The estimate sidecars](#the-estimate-sidecars) |
| `measurements.json`, interposer windows | `estimate --synth`, `interpose` | `results/estimate-<case>`; the `--measurements-dir` you name | `gridsim-measurements/1` | [Measurement sets](#gridsim-measurements1---the-estimators-input) |
| spectra file, `harmonic-export.json` | you, `harmonics` | none; `results/harmonic-export.json` | `gridsim-harmonics/1`, `gridsim-harmonic-export/1` | [Harmonic files](#harmonic-files) |
| `smallsignal.json`, `impedance-stability.json`, classical export | `modal`, `impedance-stability`, `stability` | the `--export` path you name; `results/impedance-stability-export.json`; the `--export` path you name | `gridsim-smallsignal/1`, `gridsim-impedance-stability/1`, `gridsim-classical-export/1` | [Stability exports](#stability-exports) |
| RMS event study export | `stability --rms --events --events-out` | the `--events-out` folder you name | `gridsim-rms-events-export/1` and CSV | [Stability exports](#stability-exports) |
| `timeseries.csv`, `summary.md` | `emt` | `results/emt-out` | CSV and Markdown | [EMT run files](#emt-run-files) |
| event catalogue | you, `replay-event --dump-catalog` | the path you name | `gridsim-event-catalog/1` | [Event catalogue](#the-event-catalogue) |
| frequency exports, evidence, H=0 study | `replay-event`, `scenario`, `evidence`, `h0-study` | `results/replay-<event>`, `results/scenario-<event>`, `results/evidence-<event>`, `results/h0-study-<event>` | `gridsim-frequency-export/1`, `gridsim-evidence/1`, `gridsim-h0-study/1` | [Event study outputs](#event-study-outputs) |
| `fleet.csv` | `fleet` | `results/fleet-<event>` | CSV | [Run directories](#run-directories-and-run-metadatajson) |
| time-series CSVs, frequency trace, PV curves | `transient`, `gda-replay`, `trip --export`, `cpf --pv-out`, `stress --pv-out` | `results/transient-<case>-<stamp>`, `results/gda-replay-<case>`; the path you name | CSV | [Time-series CSVs](#time-series-csvs) |
| reports | `bench`, `chain`, `stress`, `nminus1-sweep`, `conformance` | `results/benchmark-results.md`, `results/chain-<case>.md`, `results/stress.md`, `results/nminus1-sweep.md`, `results/conformance` | Markdown and CSV | [Reports](#markdown-and-csv-reports) |
| RoCoF screening table | `screen --csv` | the path you name | CSV only | [Reports](#markdown-and-csv-reports) |
| `crossval-diff.json` | `crossval` (its Python sidecar) | `results/crossval-<case>` | JSON, no schema id | [Run directories](#run-directories-and-run-metadatajson) |
| `--json` / `--csv` results | `losses`, `dcopf`, `dcpf`, `cpf`, `nminus1` and the other verbs of ch. 19 | the paths you name | `gridsim-cli-result/1` | [Result files](#gridsim-cli-result1---result-files) |
| cost, switched-shunt, device, demand-series files | you | none | `gridsim-costs/1`, `gridsim-switched-shunts/1`, device JSON, GDA series JSON | [Authored inputs](#authored-inputs) |
| point map (crosswalk), quarantine marker | you, `interpose` | none; `<dir>/Parquet/<source id>/_QUARANTINE.json` | `gridsim-rtu-pointmap/1`, `_QUARANTINE.json` | [Crosswalks](#crosswalks-and-the-interposer-files) |
| audit log | `--audit-log` | the file you name | `gridsim-audit/1` | [Audit log](#gridsim-audit1---the-audit-log) |
| v2 documents | the case editor, `convert` (an output ending in `.json`), `dist3ph to-v2`; checked by `validate` | the path you save or name | `gridsim-case/2`, `gridsim-network/2`, `gridsim-library/2`, `gridsim-kind/2`, `gridsim-dist3ph/2` | [v2 documents](#v2-documents) |
| MATPOWER `.m` file or v1 JSON case folder | `convert` | the output you name | MATPOWER `.m`, or the v1 case folder (`system.json`, `buses.json`, ...) | [Other files you may meet](#other-files-you-may-meet) |
| PMU measurement sets, live quarantine capture | `pmu-ingest --measurements-dir`, `pmu-ingest --live --live-quarantine` | the folders you name | `gridsim-measurements/1`; raw C37.118 frames with a `gridsim-pmu-quarantine/1` sidecar | [ch. 25](<0025 Interposing live telemetry.md>) |
| `gridviz.json` | `export` | `results/gridviz.json` | none | [Visualiser scene](#gridvizjson---the-visualiser-scene) |

## Run directories and run-metadata.json

A verb that writes a report writes it into a run directory (or next to a report file) together with a
`run-metadata.json` sidecar, so the run is self-describing: when it ran, on what code, on what machine, with what
parameters. You read it months later when two results disagree.

| Verb | Default location | Files written |
|---|---|---|
| `transient` | `results/transient-<case>-<stamp>` (several cases: `results/transient-<stamp>/<case>`) | `timeseries.csv`, `summary.md`, `exports/slice-NNNNNNNNN.json`, `run-metadata.json` |
| `replay-event` | `results/replay-<event>` | `baseline-frequency.json`, `summary.md`, `run-metadata.json` |
| `scenario` | `results/scenario-<event>` | `matrix.csv`, `risk-curve.csv`, `counterfactual-frequency.json`, `summary.md`, `run-metadata.json` |
| `h0-study` | `results/h0-study-<event>` | `h0-study.json`, `matrix.csv`, `summary.md`, `run-metadata.json` |
| `evidence` | `results/evidence-<event>` | `evidence.json`, `summary.md`, `run-metadata.json` |
| `fleet` | `results/fleet-<event>` | `fleet.csv`, `run-metadata.json` |
| `gda-replay` | `results/gda-replay-<case>` | `state-estimate.csv`, `exports/state-NNNNNN.json`, `storage-counterfactual.csv` (with `--bess`), `run-metadata.json` |
| `emt` | `results/emt-out` | `timeseries.csv`, `summary.md`, `run-metadata.json` |
| `estimate` | `results/estimate-<case>` | `estimate-export.json`, `residuals.csv`, `run-metadata.json`, and the sidecars listed under the estimate export |
| `estimate-noise-floor` | `results/estimate-noise-floor` | `noise-floor.csv`, `run-metadata.json` |
| `conformance` | `results/conformance` | `conformance.md`, `conformance.csv`, `run-metadata.json` |
| `bench` | `results/benchmark-results.md` | the report, `benchmark-results.csv`, and `benchmark-results.run-metadata.json` beside it |
| `nminus1-sweep` | `results/nminus1-sweep.md` | the report, a `.csv` and a `<report>.run-metadata.json` beside it |
| `stress` | `results/stress.md` | the report, a `.csv` and a `<report>.run-metadata.json` beside it |
| `chain` | `results/chain-<case>.md` | the report, a `.csv` and a `<report>.run-metadata.json` beside it |

`crossval` writes `solve-export.json` and `crossval-diff.json` into `results/crossval-<case>`, and no
`run-metadata.json`: it records the same provenance in the export's `meta` block. `crossval-diff.json` is the
comparison written by the Python sidecar `tools/crossval/crossval.py`. It carries no GridSim schema id, and it is
absent when the sidecar did not run. `chain` with `--bess` writes `results/chain-storage-<case>.md` and its
`.csv` and no metadata sidecar. `bench --perf` writes `results/perf-baseline.md` and `.csv` and no sidecar: the header
lines are in the Markdown, and the CSV (`path`, `case`, `buses`, `metric`, `value`, `runs`, `commit`) has a `commit`
column and no `#` lines. `bench --estimate` writes `results/bench-estimate/bench-estimate.csv` (`case`, `buses`,
`measurements`, `states`, `backend`, `converged`, `iterations`, `mean_ms`, `runs`, `commit`), also with a `commit`
column and no header lines. `losses`, `dcopf` and the other verbs of
[chapter 19](<0019 CLI reference.md#result-files>) that take `--json` and `--csv` write only the files you name.

`fleet.csv` has one row per fleet size after the `#` header: `units`, `rocof_hz_s`, `nadir_hz`, `ffr_at_nadir_mw`,
`peak_fleet_ffr_mw` and `lfdd_at_risk`.

`run-metadata.json` has the same fields for every verb. This is the one `transient` wrote:

```
$ cat results/exports-docs/transient/run-metadata.json
{
  "timestampUtc": "2026-10-20T05:26:20.0011475Z",
  "backend": "cpu",
  "gitCommit": "v1.95.294-173-g203c6b9c",
  "buildConfig": "Release",
  "coreVersion": "1.95.467.0",
  "runtime": ".NET 10.0.12",
  "arch": "X64",
  "serverGc": true,
  "simdTier": "Avx256",
  "simdLanes": 4,
  "simdFma": true,
  "machineName": "LAPTOP",
  "os": "Microsoft Windows 10.0.26300",
  "cpu": "Intel(R) Core(TM) i3-N305",
  "logicalCores": 8,
  "ramGb": 8.3,
  "run": "transient  case=case9  minutes=1  dt=100ms  slices=600  solver=Newton-Raphson (dense)"
}
[exit 0]
```

| Field | Meaning |
|---|---|
| `timestampUtc` | The instant the sidecar was written. |
| `backend` | The solver backend: `cpu`, `gpu/<vendor> (<device>)`, or `cpu (<vendor> requested but unavailable)`. |
| `gitCommit` | The short commit baked into the build; a `-dirty` suffix means uncommitted changes. |
| `buildConfig` | `Debug` or `Release`. |
| `coreVersion` | The `GridSim.Core` assembly version. |
| `runtime`, `arch`, `serverGc` | The .NET runtime, the process architecture, and whether server garbage collection is on. |
| `simdTier`, `simdLanes`, `simdFma` | The active vector tier, doubles per vector step, and whether fused multiply-add is available, as `gridsim simdcheck` reports them. |
| `machineName`, `os`, `cpu`, `logicalCores`, `ramGb` | The host. `cpu` is the model name on Linux and the architecture elsewhere. |
| `run` | The verb and its key parameters as one string. |

The same information heads the reports. A Markdown report starts with the header as plain lines indented by four
spaces. A CSV starts with the header as `# `-prefixed lines, then the column names. These CSVs carry the `#` header:
`timeseries.csv` (`transient` and `emt`), `state-estimate.csv`, `matrix.csv` and `risk-curve.csv`, `fleet.csv`,
`residuals.csv`, `hti-removals.csv`, `hti-corrections.csv`, `noise-floor.csv` and `conformance.csv`.
`storage-counterfactual.csv` has no such header: its first line is a single `# COUNTERFACTUAL` comment, then the
column names. The CSVs of `bench`, `chain`, `stress` and `nminus1-sweep` have no header lines. They carry a `commit`
column on every row instead, so rows pooled from several runs stay attributable. The `screen` CSV, the `--bess`
variant of `chain`, the `--pv-out` curves and the shared-writer CSVs (`--csv`) have neither a header nor a `commit`
column. A CSV with `#` lines needs `comment='#'` in a pandas or R reader.

## gridsim-solve-export/1 - the solve-math export

The full record of one power-flow solve: the network as solved, the solver's per-iteration working, and the converged
state, with a physical-law audit. Recording it is observational: attaching the recorder does not change the solution.

- **Written by:** `gridsim <case> --once --enableExport[=path]` (default `results/exports/<case>-solve.json`, for
  example `results/exports/case9-solve.json`; `--export-jacobian` forces the Jacobian); `transient`
  (`exports/slice-NNNNNNNNN.json`, one per `--export-every` slice, plus slice 1 and the trip window); `gda-replay`
  (`exports/state-NNNNNN.json`); `crossval` (`solve-export.json`); `estimate --export-solve`
  (`estimated-solve-export.json`, written only when the estimate converged). GDA's `gridsim_export.to_solve_export`
  writes the same shape on the lake side.
- **Read by:** `tools/crossval/crossval.py` and `residual_check.py`; `SolveImport` (pre-solved replay in the apps,
  [chapter 8](<0008 The desktop app.md>)); the GDA bridge ([chapter 24](<0024 The GDA bridge.md>)), whose
  topology and query payloads reuse the `model` block.
- **Produced in:** [chapter 5](<0005 Running power flows.md>), [chapter 14](<0014 Frequency studies.md>),
  [chapter 21](<0021 State estimation.md>), [chapter 27](<0027 Validation tools.md>).

```bash
gridsim case9 --once --enableExport=results/exports-docs/solve-export.json
```

```
$ gridsim case9 --once --enableExport=results/exports-docs/solve-export.json
[export] dense-newton: converged=True iters=4 maxMis=1.65E-014  ->  results\exports-docs\solve-export.json
[exit 0]
```

The document has six top-level fields.

| Field | Meaning |
|---|---|
| `schema` | `gridsim-solve-export/1`. |
| `meta` | Provenance: `Case`, `Backend` (the CLI backend, or the verb name for `harmonics` and `impedance-stability`), `GitCommit`, `TimestampUtc`, `ElapsedMs` (the wall time of the computation that produced the document, measured by every writer), `Machine`. |
| `model` | The network exactly as solved. |
| `solve` | The solver's working. |
| `result` | The converged state and every branch flow. |
| `physics` | The physical-law audit of the state. |

```
$ sed -n '1,24p' results/exports-docs/solve-export.json
{
  "schema": "gridsim-solve-export/1",
  "meta": {
    "Case": "case9",
    "Backend": "cpu",
    "GitCommit": "v1.95.294-173-g203c6b9c",
    "TimestampUtc": "2026-10-20T05:26:16.4773195Z",
    "ElapsedMs": 31.8409,
    "Machine": "LAPTOP / Microsoft Windows 10.0.26300"
  },
  "model": {
    "name": "IEEE 9-bus (WSCC)",
    "baseMva": 100,
    "frequencyHz": 60,
    "busCount": 9,
    "buses": [
      {
        "id": 1,
        "type": "Slack",
        "baseKv": 345,
        "pd": 0,
        "qd": 0,
        "gs": 0,
        "bs": 0,
[exit 0]
```

**`model`** carries the solve-relevant network, and an exported state re-imports as the same network.

| Block | Fields |
|---|---|
| top level | `name`, `baseMva`, `frequencyHz` (the nominal frequency), `busCount` |
| `buses[]` | `id`, `type` (`Slack`, `Pv` or `Pq`), `baseKv`, `pd` and `qd` (demand, MW and Mvar), `gs` and `bs` (shunt, MW and Mvar at 1 pu), `vm` (voltage set-point or start), `name`, `zip` (the six coefficients `ap bp cp aq bq cq` of a ZIP load; omitted for constant power), `vaDeg` (initial angle), `x` and `y` (layout), `vminPu` and `vmaxPu` (the bus's own voltage limits; omitted when the global band applies) |
| `generators[]` | Every unit, in service or not: `bus`, `pg`, `qg`, `vg`, `qmin`, `qmax`, `pmin`, `pmax`, `mBase` (MVA), `h` (inertia constant, seconds), `type`, `name`, `inService`, `dynamicsClass`, the fault data `subtransientXdPu`, `negativeSeqXPu`, `unitTransformerXPu`, the converter flags `converterInterfaced`, `converterFaultCurrentPu`, `gridForming`, `powerFlowOnly`, and the cost curves `cost` and `reactiveCost` |
| `branches[]` | `from`, `to`, `r`, `x`, `b` (per unit on `baseMva`), `tap`, `phaseShiftDeg`, `rateAMva`, `name`, `isTransformer`, `inService`, the short-circuit data `r0`, `x0`, `fromWinding`, `toWinding`, `fromNeutralXPu`, `toNeutralXPu`, and `lengthKm`, `equipment`, `rateBMva`, `rateCMva`, `dynamicsClass` |
| `oltcs[]` | `transformer` (the branch name), `controlsBus`, `targetVpu`, `minTap`, `maxTap`, `stepPu`, `deadbandPu` |
| `boundaries[]` | `name`, `limitMw`, `branches[]` of `{branch, direction}` (the transfer boundaries of the GB models) |

A limit such as `qmax` may be the string `"Infinity"`. Per-asset dynamics classes and protection bundles are not part
of the export.

**`solve`** is what the solver did.

| Field | Meaning |
|---|---|
| `solver` | `dense-newton`, `sparse-newton` or `bfs`. |
| `n` | The number of buses solved. |
| `converged`, `status`, `iterations`, `maxMismatch` | The outcome; `status` is `Converged`, `MaxIterations` or `NonFinite`; `maxMismatch` is the largest power mismatch in per unit. |
| `jacobianOmittedForSize` | True when a dense solve left the Jacobian out because the system exceeds 300 buses. |
| `slackQNote` | A note on the slack bus reactive output; omitted when there is none. |
| `ybus[]` | The non-zero admittance entries `{I, J, G, B}` (zero-based bus indices, per unit). Empty for a BFS solve, which forms no Y-bus. |
| `busSpec[]` | Per bus `{Index, Id, Type, VmInit, VaInitDeg, Pspec, Qspec, Qd, Vg, Qmin, Qmax}`: the specification and start point (`Pspec` and `Qspec` per unit, `Qd`, `Qmin` and `Qmax` MVAr, on every engine). |
| `qLimitEvents[]` | Every switch of a bus between PV and PQ in the reactive-limit loop: `{BusIndex, BusId, PinnedAt, Pass}`. |
| `passes[]` | One entry per reactive-limit pass: `pass`, `effectiveTypes`, `pvpqIndices` and `pqIndices` (the equation ordering), and `iterations[]` of `{iteration, maxMismatch, mismatch[], correction[], jacobian[][]}`. |

```
$ sed -n '418,432p' results/exports-docs/solve-export.json
  "solve": {
    "solver": "dense-newton",
    "n": 9,
    "converged": true,
    "status": "Converged",
    "iterations": 4,
    "maxMismatch": 1.6456133601332757E-14,
    "jacobianOmittedForSize": false,
    "ybus": [
      {
        "I": 0,
        "J": 0,
        "G": 0,
        "B": -17.36111111111111
      },
[exit 0]
```

`mismatch` and `correction` follow the pass ordering: the P equations of every non-slack bus first, then the Q
equations of the PQ buses. The Jacobian, captured before factorisation, appears only for the dense engine, for
systems of up to 300 buses unless you pass `--export-jacobian`. A sparse solve records the mismatch and correction
vectors without it. A BFS solve records the per-bus voltage change as `mismatch` and leaves `correction` empty.

**`result`** is the converged state.

| Field | Meaning |
|---|---|
| `totalLossMw`, `totalLossMvar` | System losses. For Newton solves these are the sums of the bus net injections, so they include bus-shunt consumption; for BFS they are the sums of the branch losses. |
| `buses[]` | `id`, `vm` (pu), `vaDeg`, `pInjMw`, `qInjMvar`. |
| `lineFlows[]` | `from`, `to`, `pFromMw`, `qFromMvar`, `pToMw`, `qToMvar`, `pLossMw`, `qLossMvar`, `name`, `ratingMva`, `loadingPercent`. An out-of-service branch is an all-zero row. |

```
$ sed -n '1795,1817p' results/exports-docs/solve-export.json
  "result": {
    "totalLossMw": 4.6410214744828835,
    "totalLossMvar": -92.16012521906433,
    "buses": [
      {
        "id": 1,
        "vm": 1.04,
        "vaDeg": 0,
        "pInjMw": 71.64102147448251,
        "qInjMvar": 27.045923533493067
      },
      {
        "id": 2,
        "vm": 1.025,
        "vaDeg": 9.280005481642787,
        "pInjMw": 162.99999999999997,
        "qInjMvar": 6.653660318427831
      },
      {
        "id": 3,
        "vm": 1.025,
        "vaDeg": 4.664751333136755,
        "pInjMw": 84.99999999999999,
[exit 0]
```

**`physics`** is the audit of that state against the physical laws: `gatePass` (true when every hard law holds),
`hardGateLaws` (the numbers of the laws that gate), and `laws[]` of `{number, name, statement, status, residual, unit,
tolerance, detail}`. `tools/crossval/physics_audit.py` recomputes each law from `model` and `result` and checks these
values independently.

```
$ sed -n '1983,2005p' results/exports-docs/solve-export.json
  "physics": {
    "gatePass": true,
    "hardGateLaws": [
      1,
      2,
      3,
      6,
      8
    ],
    "basis": "case-dispatch",
    "laws": [
      {
        "number": 1,
        "name": "Conservation of energy",
        "statement": "Sigma(generation - load) = Sigma losses",
        "status": "Holds",
        "residual": 4.263256414560601E-14,
        "unit": "MW",
        "tolerance": 1E-06,
        "detail": "independent dissipation 4.641021 MW (branch 4.6410 \u002B shunt 0.0000) vs reported loss 4.641021 MW"
      },
      {
        "number": 2,
[exit 0]
```

## gridsim-estimate-export/1 - the estimate export

Everything one state estimation computed: the network, the measurement set it was asked to fit, the estimated state,
the residual table, observability and removals. It is the whole input contract of the estimation sidecars.

- **Written by:** `gridsim estimate` as `estimate-export.json` in the run directory.
- **Read by:** `tools/crossval/crossval_estimation.py` and `se_residual_check.py`; `estimate-noise-floor`.
- **Produced in:** [chapter 21](<0021 State estimation.md>).

```bash
gridsim estimate case14 --synth --trace --export-solve --out results/exports-docs/estimate
```

```
$ gridsim estimate case14 --synth --trace --export-solve --out results/exports-docs/estimate

=== WLS state estimate: case14 ===
  status: Converged   iterations: 5   max |dx|: 2.72E-009
  measurements: 122   states: 27   redundancy (dof): 95
  observability: observable (rank 27/27)
  consistency: J = 80.01 vs chi2(95%) = 118.75 -> PASS
  vs synthetic truth: max |dVm| = 1.330E-003 pu   max |dVa| = 2.248E-001 deg

  -> results\exports-docs\estimate
[exit 0]
```

| Field | Meaning |
|---|---|
| `schema` | `gridsim-estimate-export/1`. |
| `meta` | The same `meta` as the solve export; `ElapsedMs` is the time of the estimate. |
| `model` | `name`, `baseMva`, `busCount`, `buses[]`, `generators[]` and `branches[]` with the fields of the solve export's blocks except `pmin`, `zip` and the short-circuit data. It has no `frequencyHz`, `oltcs` or `boundaries`. |
| `measurements` | The set as handed to the estimator: `measurementSchema` (`gridsim-measurements/1`), `case`, `takenUtc`, `note`, `count`, `items[]`. Gross errors are still in it; so are the replaced voltage-schedule rows under `--prior solve`. |
| `estimate` | The outcome, below. |
| `residuals[]` | One entry for every measurement that survived to the final fit: `measurement`, `predicted`, `residual` (engineering units), `normalizedResidual`, `critical`. |
| `observability` | `observable`, `rank` (-1 only when the sparse backend skipped the rank proof because the state size exceeds 2,500), `stateSize`, `unobservableBuses[]`, `unobservableStates[]`, `unmeasuredIslandBuses[]`. |
| `removed[]` | The bad-data removals in order: `measurement`, `normalizedResidual`, `pass`. |
| `trace[]` | Only with `--trace`: the per-iteration path `{pass, iteration, maxStateUpdate, stepScale}`. |
| `physics` | The same audit as the solve export, evaluated at the estimated state. |

`estimate` is the verdict:

```
$ sed -n '1495,1506p' results/exports-docs/estimate/estimate-export.json
  "estimate": {
    "status": "Converged",
    "converged": true,
    "iterations": 5,
    "maxStateUpdate": 2.7236422301635937E-09,
    "objectiveJ": 80.01052231478407,
    "degreesOfFreedom": 95,
    "chiSquareThreshold": 118.74899343100537,
    "chiSquarePassed": true,
    "chiSquareTest": 1,
    "irlsCapReached": false,
    "buses": [
[exit 0]
```

`status` is the estimation status (`Converged`, `MaxIterations`, `NonFinite`, `Unobservable`, ...). `maxStateUpdate`
is the last Gauss-Newton step, `objectiveJ` the weighted sum of squared residuals, `degreesOfFreedom` the redundancy,
and `chiSquareThreshold` the 95 % chi-square limit that `objectiveJ` is tested against (`NaN` when no test ran).
`irlsCapReached` is true when a reweighting method (`huber`, `schweppe-gm`, `lav-irls`) stopped re-weighting on its cap
before the weights settled, so the state is not a fixed point of that estimator; it is always present, and `false` for
the other methods. `buses[]` is the estimated state: `id`,
`vm`, `vaDeg`.

`chiSquarePassed` is `true` or `false` only when the chi-square test ran. It is `null`, never a pass, when the test
did not run: no redundancy, `--no-bad-data`, an unobservable fit or a fit that did not converge. `chiSquareTest` says
the same as a number: 0 not run, 1 passed, 2 failed. A script that treats the field as a verdict must accept `null`.

A measurement inside `items[]`, `residuals[]` or `removed[]` is `{kind, value, sigma, bus | from + to + end, circuit,
source}`, with the bus fields absent for branch kinds and the branch fields absent for bus kinds.

```
$ sed -n '1579,1592p' results/exports-docs/estimate/estimate-export.json
  "residuals": [
    {
      "measurement": {
        "kind": "VmPu",
        "value": 1.0603366153490539,
        "sigma": 0.004,
        "bus": 1,
        "source": "synthetic"
      },
      "predicted": 1.0600177496207248,
      "residual": 0.00031886572832906523,
      "normalizedResidual": 0.08517669708450266,
      "critical": false
    },
[exit 0]
```

When bad-data removal runs (the default for `wls`), each removal is recorded with the normalised residual that
condemned it and the pass it happened in. This is the export of the same case with three gross errors planted, run
without `--hti`:

```
$ sed -n '3284,3299p' results/exports-docs/estimate-lnr/estimate-export.json
  "removed": [
    {
      "measurement": {
        "kind": "VmPu",
        "value": 0.9791469785239025,
        "sigma": 0.004,
        "bus": 9,
        "source": "synthetic"
      },
      "normalizedResidual": 18.347391438257883,
      "pass": 0
    },
    {
      "measurement": {
        "kind": "PInjMw",
        "value": -17.94245975275534,
[exit 0]
```

`observability` is the proof the estimate means something:

```
$ sed -n '3327,3345p' results/exports-docs/estimate/estimate-export.json
  "observability": {
    "observable": true,
    "rank": 27,
    "stateSize": 27,
    "unobservableBuses": [],
    "unobservableStates": [],
    "unmeasuredIslandBuses": []
  },
  "removed": [],
  "trace": [
    {
      "pass": 0,
      "iteration": 0,
      "maxStateUpdate": 0.2,
      "stepScale": 0.6483659052714864
    },
    {
      "pass": 0,
      "iteration": 1,
[exit 0]
```

With `--backfill-removals`, each removed real-power row is replaced in the fit by a loose pseudo-measurement from the
prior, with `source` set to `removal-backfill-prior`, and that row appears in `residuals[]`.

### The estimate sidecars

| File | Written when | Content |
|---|---|---|
| `residuals.csv` | always | The residual table flattened for a spreadsheet. |
| `measurements.json` | `--synth` | The sampled set, so the sidecars consume the identical input. A `gridsim-measurements/1` file. |
| `estimated-solve-export.json` | `--export-solve` and the estimate converged | The estimate projected into a `gridsim-solve-export/1` document. Its `solve` block names the estimator and carries its status and last state update, because no power-flow solve ran. |
| `hti-removals.csv` | `--hti` | The Hypothesis-Testing-Identification removals. |
| `hti-corrections.csv` | `--hti-correct` | The in-place corrections. |
| `fdia-screen.json` | `--fdia-screen` | The false-data-injection verdict, schema `gridsim-fdia-screen/1`. |
| `noise-floor.csv` | `estimate-noise-floor` | Pooled residual statistics per source and kind. |

The `solve` block of `estimated-solve-export.json`:

```
  "solve": {
    "solver": "state-estimate (Wls)",
    "converged": true,
    "status": "Converged",
    "iterations": 5,
    "maxStateUpdate": 2.7236424064891863E-09
  },
```

`maxStateUpdate` is the estimator's last state update, not a power mismatch, so the block has no `maxMismatch`.

**`residuals.csv`.** One row per measurement in the final fit, after the `#` header. `value` and `predicted` are
round-trip (`G17`), `sigma` is `G6`, `residual` is `E6` and `normalized_residual` is `F3` (`NaN` for a critical row,
or with `--lean` on a passing fit). `location` reads `bus 4`, `branch 1->2 (from)` or `branch 1->2 'name' (from)`.

```
$ head -n 14 results/exports-docs/estimate/residuals.csv
# GridSim  -  2026-10-20 05:26:11 UTC
# code:    git v1.95.294-173-g203c6b9c  |  GridSim.Core v1.95.467.0  |  build Release
# runtime: .NET 10.0.12  X64  GC=Server
# machine: LAPTOP  Microsoft Windows 10.0.26300
# cpu:     Intel(R) Core(TM) i3-N305  -  8 logical cores  -  8 GB RAM
# simd:    Avx256  (4 doubles/step)  FMA=True
# backend: cpu
# run:     estimate  case=case14  measurements=122  source=synthetic
kind,location,value,sigma,predicted,residual,normalized_residual,critical
VmPu,bus 1,1.0603366153490539,0.004,1.0600177496207248,3.188657E-004,0.085,False
PInjMw,bus 1,230.50994250325303,1,231.53597775062065,-1.026035E+000,1.633,False
QInjMvar,bus 1,-16.387551951145515,1,-16.02951780147939,-3.580341E-001,0.538,False
VmPu,bus 2,1.0458945760543186,0.004,1.0448823911434171,1.012185E-003,0.269,False
PInjMw,bus 2,17.170793445468924,1,17.517053380283386,-3.462599E-001,0.669,False
[exit 0]
```

**`hti-removals.csv`.** The `#` lines are followed by a `# passes=... total_iterations=... stop_reason=...` line, then
one row per removal: `pass` (counted from 0, pass 0 being the first estimate; N256, it was 1-based), `kind`, `location`, `value`, `sigma`, `estimated_error` with its `estimated_error_unit`
(`pu` for voltage and current magnitudes, `rad` for angles, `pu_base_mva` for powers and flows),
`estimated_error_measured` with its `measured_unit` (`pu`, `deg`, `MW` or `Mvar`), and `test_statistic`.
`hti-corrections.csv` has the same shape with `original_value` and `corrected_value` in place of `value`.

```
$ head -n 14 results/exports-docs/estimate-planted/hti-removals.csv
# GridSim  -  2026-10-20 05:26:10 UTC
# code:    git v1.95.294-173-g203c6b9c  |  GridSim.Core v1.95.467.0  |  build Release
# runtime: .NET 10.0.12  X64  GC=Server
# machine: LAPTOP  Microsoft Windows 10.0.26300
# cpu:     Intel(R) Core(TM) i3-N305  -  8 logical cores  -  8 GB RAM
# simd:    Avx256  (4 doubles/step)  FMA=True
# backend: cpu
# run:     estimate  case=case14  measurements=122  source=docs/_captures/estimation/case14-planted.json
# passes=2 total_iterations=10 stop_reason=ChiSquarePassed
pass,kind,location,value,sigma,estimated_error,estimated_error_unit,estimated_error_measured,measured_unit,test_statistic
0,VmPu,bus 9,0.97914697852390253,0.004,-0.076494769013953995,pu,-0.076494769013953995,pu,17.926056699398455
0,PFlowMw,branch 2->3 (from),96.567423708461746,1.6,0.23807965107852463,pu_base_mva,23.807965107852464,MW,13.597861020478678
0,PInjMw,bus 4,-17.94245975275534,1,0.27292734923735545,pu_base_mva,27.292734923735544,MW,7.2052784005966659
[exit 0]
```

**`fdia-screen.json`.** `schema`, `verdict` (`suspected`, `clear`, `chi-square-failed` or `no-verdict`), `summary`
(one sentence), `status` (the estimation status) and `chiSquareTest` (`Passed`, `Failed` or `NotRun`, by name). A
`suspected` or `no-verdict` result makes `estimate` exit 3.

```
$ cat results/exports-docs/estimate-planted/fdia-screen.json
{
  "schema": "gridsim-fdia-screen/1",
  "verdict": "clear",
  "summary": "chi-square PASSED and the state is physically plausible and anchor-consistent - no FDIA indicator.",
  "status": "Converged",
  "chiSquareTest": "Passed"
}
[exit 0]
```

**`noise-floor.csv`.** One row per measurement class (source tag and kind): `count` of finite samples,
`spread_robust` and `spread_std` (the ratio k of true noise to declared sigma, by the median and by the standard
deviation), `bias`, `suggested_sigma_scale` and `miscalibrated`. It is a study, not a gate.

```
$ head -n 16 results/exports-docs/noise-floor/noise-floor.csv
# GridSim  -  2026-10-04 14:35:09 UTC
# code:    git 98048023  |  GridSim.Core v1.95.154.0  |  build Release
# runtime: .NET 10.0.12  X64  GC=Server
# machine: LAPTOP  Microsoft Windows 10.0.26200
# cpu:     X64  -  8 logical cores  -  8 GB RAM
# simd:    Avx256  (4 doubles/step)  FMA=True
# backend: cpu
# run:     estimate-noise-floor  exports=1  skipped-non-converged=0  samples=122
source,kind,count,spread_robust,spread_std,bias,suggested_sigma_scale,miscalibrated
synthetic,QInjMvar,14,1.1825479925667144,0.93302204184988125,-0.17391889373940495,1.1825479925667144,False
synthetic,PFlowMw,40,1.0758801392421329,0.79909543407213657,-0.11921574898423581,1.0758801392421329,False
synthetic,QFlowMvar,40,1.0481207722311126,0.8865119790428293,-0.05958625259041006,1.0481207722311126,False
synthetic,VmPu,14,0.98388255398924207,0.84922574108164928,-0.18734780464406331,0.98388255398924207,False
synthetic,PInjMw,14,1.0109505299492152,0.87123781863344785,-0.0057258981188841318,1.0109505299492152,False
[exit 0]
```

## gridsim-measurements/1 - the estimator's input

A versioned, self-describing measurement set over one case. You can write one by hand, `estimate --synth` samples one,
and `interpose` writes one per window.

- **Written by:** `estimate --synth` (`measurements.json`); `interpose` (one file per window, named
  `<UTC>-<sequence>.json`; a clash is kept as `-dupN`); GDA's `NetworkModel/build_estimator_measurements.py` for real
  GB telemetry; your editor.
- **Read by:** `estimate --measurements <file>`, `serve --measurements-dir <dir>`.
- **Produced in:** [chapter 21](<0021 State estimation.md>), [chapter 25](<0025 Interposing live telemetry.md>).

```
$ head -n 20 results/exports-docs/estimate/measurements.json
{
  "schema": "gridsim-measurements/1",
  "case": "IEEE 14-bus",
  "baseMva": 100,
  "takenUtc": "2024-03-01T12:00:00Z",
  "note": "synthetic seed=20260713 noise=1 coverage=vm:1/inj:1/flow:1",
  "measurements": [
    {
      "kind": "VmPu",
      "value": 1.0603366153490539,
      "sigma": 0.004,
      "bus": 1,
      "from": 0,
      "to": 0,
      "end": "From",
      "source": "synthetic",
      "isBranch": false
    },
    {
      "kind": "PInjMw",
[exit 0]
```

| Field | Meaning |
|---|---|
| `schema` | Must equal `gridsim-measurements/1`; any other value is refused. |
| `case` | The case the set was taken on. Informational. |
| `baseMva` | Checked against the model (default 100). |
| `takenUtc` | Optional. When present it must clear the 169-hour horizon. |
| `note` | Free provenance text; the sampler records its seed and coverage here. |
| `measurements[]` | The rows below. |

A row needs `kind` and `value`; a row without either is refused and named. `sigma` must be finite and greater than 0.

| Row field | Meaning |
|---|---|
| `kind` | `VmPu` (voltage magnitude, pu), `PInjMw` and `QInjMvar` (bus injection), `PFlowMw` and `QFlowMvar` (branch flow), `VaDeg` (PMU phase angle in degrees against the slack), `IMagPu` (branch current magnitude, pu) and `IAngDeg` (branch current angle, degrees). |
| `value`, `sigma` | The reading and its standard deviation, in the units above. |
| `bus` | The bus id, for the bus kinds. |
| `from`, `to`, `end` | The branch ends and which terminal is measured (`From`, the default, or `To`), for the branch kinds. |
| `circuit` | Picks one of several parallel branches: the branch name, or `"#k"`, where k is the zero-based position of the branch in the case's branch list. A `"#k"` branch must have the row's `from` and `to`, or the row is refused. |
| `source` | A provenance tag, default `scada`. Some tags act: `virtual-zero-injection` rows are hard equalities under `--method constrained`, `pseudo-voltage-schedule` rows are the ones `--prior solve` replaces. |

A file GridSim writes carries every field of every row, including `from`, `to` and `isBranch` as 0, 0 and `false` on a
bus row. A file you write needs only the fields its kind uses.

## Harmonic files

**The spectra file (`gridsim-harmonics/1`)** is the input of `harmonics --spectra`: a set of harmonic current sources
on a case. Measured spectra are tagged `Recorded` and assumed library spectra `CuratedPlaceholder`; the solver is the
same either way. Produced in [chapter 17](<0017 Harmonic studies.md>).

```
$ cat docs/_captures/harmonics/spectra-case9.json
{
  "schema": "gridsim-harmonics/1",
  "case": "IEEE 9-bus (WSCC)",
  "baseMva": 100.0,
  "takenUtc": "2026-08-01T12:00:00Z",
  "note": "Metered 6-pulse drive at bus 9 (example)",
  "sources": [
    { "bus": 9, "sourceType": "6-pulse", "fundamentalCurrentPu": 0.5, "provenance": "Recorded",
      "spectrum": [ { "order": 5, "magnitudePercent": 18.0, "phaseDeg": 0.0 },
                    { "order": 7, "magnitudePercent": 11.0, "phaseDeg": 0.0 },
                    { "order": 11, "magnitudePercent": 4.5, "phaseDeg": 0.0 },
                    { "order": 13, "magnitudePercent": 3.0, "phaseDeg": 0.0 } ] }
  ]
}
[exit 0]
```

| Field | Meaning |
|---|---|
| `schema` | `gridsim-harmonics/1`. |
| `case`, `baseMva` | The case and system base the set was authored against; `baseMva` is checked against the model. |
| `takenUtc`, `note` | Optional measurement time (horizon-checked) and provenance text. |
| `sources[]` | `bus`, `sourceType`, `fundamentalCurrentPu`, `provenance`, and `spectrum[]` of `{order, magnitudePercent, phaseDeg}`. Magnitude is a percentage of the fundamental current. |

A component at an order outside the analysed range, or a triplen source, is refused (exit 1); see
[chapter 19](<0019 CLI reference.md#harmonics>).

**The harmonic export (`gridsim-harmonic-export/1`)** is written by `harmonics --enableExport[=path]` (default
`results/harmonic-export.json`).

```bash
gridsim harmonics case9 --spectra docs/_captures/harmonics/spectra-case9.json --enableExport=results/exports-docs/harmonic-export.json
```

```
$ gridsim harmonics case9 --spectra docs/_captures/harmonics/spectra-case9.json --enableExport=results/exports-docs/harmonic-export.json
harmonics: case IEEE 9-bus (WSCC), 1 source(s), orders 24
  worst voltage THD : 4.71% at bus 9 (relative to the solved fundamental voltage)
  resonance (bus 9): order 7, |Z|=0.658 pu, sharpness 3.8
  fine sweep peak  : h=7.4750 (448.50 Hz), |Z|=0.688 pu, positive sequence
  IEEE-519         : 1/9 buses compliant
  export -> results\exports-docs\harmonic-export.json
[exit 0]
```

| Field | Meaning |
|---|---|
| `schema`, `meta` | As for the solve export; `meta.Backend` is `harmonics`. |
| `provenance` | `Recorded` only when every source is recorded or derived from a recording, else `CuratedPlaceholder`. |
| `model` | `name`, `baseMva`, `busCount`, and `buses[]` of `{id, baseKv, name}`. |
| `orders` | The harmonic orders analysed. |
| `harmonicModel` | Every input beyond the network that the harmonic admittance used: which model options were on, the skin-effect coefficients, `generatorSources[]` (`bus`, `name`, `xSystemPu`) and `loads[]` (`bus`, `pdMw`, `qdMvar`), so an independent checker can rebuild the same matrix. |
| `sources[]` | The injected sources, as in the spectra file. |
| `voltage[]` | The non-zero harmonic bus voltages: `order`, `bus`, `vmPu`, `vaDeg`. |
| `indices` | `worstBusId`, `worstThdPct`, and `thd[]` of `{bus, thdPct}` (voltage THD against each bus's solved fundamental). |
| `resonance` | The driving-point resonance scan at one bus: `bus`, `order`, `zMag`, `sharpness`. |
| `resonanceSweep` | The fine sweep's refined peak: `bus`, `peakOrder`, `peakHz`, `zMag`, `sequence`, `minOrder`, `maxOrder`, `gridPoints`. |
| `compliance[]` | The IEEE 519 verdict per bus: `bus`, `baseKv`, `thdPct`, `thdLimitPct`, `worstIhdPct`, `worstIhdOrder`, `ihdLimitPct`, `compliant`, `voltageLevelKnown`. |

```
$ sed -n '448,474p' results/exports-docs/harmonic-export.json
  "resonance": {
    "bus": 9,
    "order": 7,
    "zMag": 0.6575669200741651,
    "sharpness": 3.83763679770113
  },
  "resonanceSweep": {
    "bus": 9,
    "peakOrder": 7.47497016807171,
    "peakHz": 448.4982100843026,
    "zMag": 0.687623565377445,
    "sequence": "Positive",
    "minOrder": 1.5,
    "maxOrder": 50,
    "gridPoints": 486
  },
  "compliance": [
    {
      "bus": 1,
      "baseKv": 345,
      "thdPct": 1.3947496157044652,
      "thdLimitPct": 1.5,
      "worstIhdPct": 0.9086523860832977,
      "worstIhdOrder": 7,
      "ihdLimitPct": 1,
      "compliant": true,
      "voltageLevelKnown": true
[exit 0]
```

## Stability exports

**`smallsignal.json` (`gridsim-smallsignal/1`)** is written by `gridsim modal --export <file.json>`. It has no
`meta` block. It holds the linearised model and its modes; produced in
[chapter 15](<0015 RMS, modal and impedance stability.md>).

```bash
gridsim modal case9 --damping 0.05 --export results/exports-docs/smallsignal.json
```

```
$ head -n 20 results/exports-docs/smallsignal.json
{
  "schema": "gridsim-smallsignal/1",
  "label": "WSCC-9",
  "f0": 60,
  "inverterStatesIncluded": false,
  "states": [
    {
      "index": 0,
      "kind": "SwingAngle",
      "ownerId": 1,
      "name": "d.delta_gen1",
      "synthetic": false
    },
    {
      "index": 1,
      "kind": "SwingAngle",
      "ownerId": 2,
      "name": "d.delta_gen2",
      "synthetic": false
    },
[exit 0]
```

| Field | Meaning |
|---|---|
| `label`, `f0` | The case label and the nominal frequency. |
| `inverterStatesIncluded` | True with `--enable-inverters`. |
| `states[]` | The state list: `index`, `kind`, `ownerId`, `name`, `synthetic`. |
| `aMatrix` | The state matrix, row by row. |
| `angleNodeBuses`, `kext` | The bus order and the external coupling matrix the model was built from. |
| `eigenvalues[]`, `dampingRatios`, `frequenciesHz` | One entry per mode: the eigenvalue as `{re, im}`, its damping ratio and its frequency in Hz. |
| `participation` | The participation factors, one row per mode, one entry per state. |
| `rhpEigenvalueCount`, `minDampingRatio` | The right-half-plane count (non-zero means small-signal unstable) and the smallest damping ratio. |
| `participationAvailable` | False when the state matrix is defective; the `participation` rows are then all-zero placeholders and `participationUnavailableReason` says why. |
| `exitCode` | The exit code the run ended with, so the file carries its own verdict (1 for an unstable result). |

```
$ sed -n '214,217p' results/exports-docs/smallsignal.json
  "rhpEigenvalueCount": 0,
  "minDampingRatio": 0.00014298128375036154,
  "participationAvailable": true,
  "exitCode": 0
[exit 0]
```

**`impedance-stability.json` (`gridsim-impedance-stability/1`)** is written by
`impedance-stability --enableExport[=path]` (default `results/impedance-stability-export.json`).

```
$ head -n 30 results/exports-docs/impedance-stability.json
{
  "schema": "gridsim-impedance-stability/1",
  "meta": {
    "Case": "IEEE 9-bus (WSCC)",
    "Backend": "impedance-stability",
    "GitCommit": "v1.95.294-104-g5fd850f6",
    "TimestampUtc": "2026-10-29T18:21:03.7392753Z",
    "ElapsedMs": 31.4133,
    "Machine": "LAPTOP"
  },
  "provenance": "CuratedPlaceholder",
  "model": {
    "name": "IEEE 9-bus (WSCC)",
    "baseMva": 100,
    "busCount": 9
  },
  "inverterBus": 2,
  "mode": "GridFollowingInverter",
  "modelDetail": "PllOuter",
  "criterion": "Siso",
  "operatingPoint": {
    "vd0Pu": 1.025,
    "vq0Pu": 0,
    "id0Pu": 0.8282520325203251,
    "iq0Pu": -0.033809249585507274,
    "pPu": 0.8489583333333331,
    "qPu": 0.034654480825144954,
    "vd0Clamped": false
  },
  "converterParams": {
[exit 0]
```

| Field | Meaning |
|---|---|
| `meta`, `model` | Provenance and `{name, baseMva, busCount}`. |
| `provenance` | `Recorded` for measured inner-loop parameters, `CuratedPlaceholder` for flag-built ones. |
| `inverterBus`, `mode`, `modelDetail`, `criterion` | The attached converter's bus, kind (`GridFollowingInverter` or grid-forming), detail level and criterion (`Siso` or `Mimo`). |
| `operatingPoint` | `vd0Pu`, `vq0Pu`, `id0Pu`, `iq0Pu`, `pPu`, `qPu`, `vd0Clamped`. |
| `converterParams` | The converter parameters used (PLL gains, current-loop bandwidth, SCR, X/R, rating, frequency). |
| `frequenciesHz`, `gridImpedance[]`, `converterAdmittance[]`, `minorLoop[]` | The scan: the frequency grid, the grid impedance and converter admittance as `{fHz, re, im}`, and the loop gain and its eigenvalues per frequency. At reporting frequency f, `gridImpedance` is the positive-sequence impedance in the converter's dq frame, the stationary-frame impedance at f + f0; `converterAdmittance` is `Y_pp` and the loop gain is their product `L_pp`. `converterImpedance[]` repeats the admittance under its old, misleading name for older readers; it is deprecated. |
| `nyquist` | `encirclements`, `unstableModeCount`, `stable`, `gainMarginDb`, `phaseMarginDeg`, `criticalFreqHz`, `minDistToMinusOne`. |
| `scrSweep[]`, `boundary` | The short-circuit-ratio sweep and the weak-grid boundary (`criticalScr`, `found`). |
| `crossval` | The right-half-plane count of the small-signal model and whether it agrees with the Nyquist verdict. |

```
$ sed -n '4326,4337p' results/exports-docs/impedance-stability.json
  "nyquist": {
    "encirclements": 0,
    "unstableModeCount": 0,
    "stable": true,
    "gainMarginDb": 19.106647182661302,
    "phaseMarginDeg": -113.23481644618866,
    "criticalFreqHz": 18.456253226988178,
    "minDistToMinusOne": 0.8602759878103645
  },
  "scrSweep": [
    {
      "scr": 1,
[exit 0]
```

**The classical export (`gridsim-classical-export/1`)** is written by `stability --export <file>` for the classical
engine as one unindented line: `label`, `f0`, `stepSeconds`, `durationSeconds`, `bumpMachine`, `bumpRad`, `machines[]`
(`bus`, `h`, `pm`, `eq`, `delta0`), `delta0Perturbed`, the reduced admittance as `yReducedG` and `yReducedB`, and the
sampled trajectory as `sampleTimes` and `sampleDelta`. It has no `meta` block.

```
$ head -c 420 results/exports-docs/classical.json
{"schema":"gridsim-classical-export/1","label":"WSCC-9","f0":60,"stepSeconds":0.001,"durationSeconds":5,"bumpMachine":2,"bumpRad":0.1,"machines":[{"bus":1,"h":58.50900000000001,"pm":0.7164102147448185,"eq":1.0465252938930831,"delta0":0.016170595145975637},{"bus":2,"h":12.288000000000002,"pm":1.6300000000000003,"eq":1.0338230694271975,"delta0":0.2580929329264051},{"bus":3,"h":3.8528,"pm":0.85,"eq":1.0168004124084893,"
[exit 0]
```

**RMS event study export (`gridsim-rms-events-export/1`)** is written by `gridsim stability --rms --events <list>
--events-out <dir>` ([chapter 15](<0015 RMS, modal and impedance stability.md>)). The folder holds:

| File | Content |
|---|---|
| `rms-events-machines.csv` | Every machine trace at every written step: `t_s,trace,bus,delta_rad,speed_pu,eqp_pu,edp_pu,eqpp_pu,eddp_pu`, each number in its shortest round-trip form. |
| `rms-events-converters.csv` | Written when the case has converter plant: `t_s,converter,name,bus,v_pu,p_pu,q_pu,ip_pu,iq_pu,in_dip_mode`. |
| `rms-events.json` | The case, the nominal frequency, the step, the duration, the events in time order, the verdict, the converter-iteration failures, the machine list with island references marked, `everySteps` and `samplesWritten`, provenance `Synthetic` and whether the result is INDICATIVE. |
| `run-metadata.json` | The shared run record ([Run directories](#run-directories-and-run-metadatajson)). |

Both CSVs start with the run header and a `# provenance:` line: `Synthetic`, a model trajectory and not a record, and
INDICATIVE when the GENROU data is synthesised. `--events-every N` writes every N-th integration step (steps 0, N,
2N, ...); the default 1 writes every step, and on a large case the files grow with every machine at every step.

## EMT run files

`gridsim emt` writes `timeseries.csv`, `summary.md` and `run-metadata.json` into `results/emt-out` (or `--out`).
Produced in [chapter 16](<0016 Electromagnetic transients (EMT).md>).

```bash
gridsim emt case14 --dt-us 20 --duration 0.05 --source 1 --amp 1 --freq 50 --event fault:5@0.02:0.01 --out results/exports-docs/emt
```

```
$ gridsim emt case14 --dt-us 20 --duration 0.05 --source 1 --amp 1 --freq 50 --event fault:5@0.02:0.01 --out results/exports-docs/emt
emt case14: 2,500 steps @ 20us, 3 factorization(s), peak |probe| 6.903, 15 ms -> results/exports-docs/emt
emt: 2 floating node(s) pinned at 0 V in the initial-condition solve.
[exit 0]
```

`timeseries.csv` has `t_seconds` and one column per probe, after the `#` header. The default probes are the
source-bus voltage, named `v:<bus>`, and the first series branch current, named `i:<from>-><to>`. With `--probe` a
column is named exactly as you typed the probe (`i:1-2` stays `i:1-2`). Values are instantaneous, in per unit.

```
$ head -n 12 results/exports-docs/emt/timeseries.csv
# GridSim  -  2026-10-20 05:26:10 UTC
# code:    git v1.95.294-173-g203c6b9c  |  GridSim.Core v1.95.467.0  |  build Release
# runtime: .NET 10.0.12  X64  GC=Server
# machine: LAPTOP  Microsoft Windows 10.0.26300
# cpu:     Intel(R) Core(TM) i3-N305  -  8 logical cores  -  8 GB RAM
# simd:    Avx256  (4 doubles/step)  FMA=True
# backend: cpu
# run:     emt case14 dt=20us dur=0.05s src=bus1 amp=1 freq=50Hz rule=Trapezoidal
t_seconds,v:1,i:1->2
0,0,0
2E-05,0.999980261,0.105867021
4E-05,0.999921044,0.210673229
[exit 0]
```

`summary.md` records the step count and timestep, the source, the number of matrix refactorisations (one for the
initial matrix plus one per switching event), the peak absolute probe value, the wall time, the integration rule and whether critical damping
adjustment was on. Its last line says the run is a forward model trajectory and not a claim about the real grid.

```
$ cat results/exports-docs/emt/summary.md
# EMT run: case14

- steps: 2,500 @ 20 us (dur 0.05s)
- source: bus 1, amp 1 pu, 50 Hz
- refactorizations: 3
- peak |probe|: 6.90333
- wall: 15.3 ms (163,564 steps/s)
- rule: Trapezoidal, CDA: on

A forward model trajectory, not a claim about the real grid.
[exit 0]
```

## The event catalogue

The schema `gridsim-event-catalog/1` is the catalogue of recorded grid events that the evidence verbs replay. You author new events here.

- **Written by:** `replay-event --dump-catalog <path>` writes the three built-ins (`gb-2019-08-09`, `gb-2023-12-22`,
  `gb-largest-loss-1320`) so you can copy and edit them; hand-authored files, for example under `data/events/`.
- **Read by:** `replay-event`, `scenario`, `h0-study`, `fleet` and `evidence` through `--catalog <file>`. Every
  `whenUtc` is checked against the 169-hour horizon on load.
- **Produced in:** [chapter 18](<0018 Replaying recorded events.md>), [chapter 20](<0020 Counterfactuals.md>).

```
$ head -n 26 results/exports-docs/catalog.json
{
  "schema": "gridsim-event-catalog/1",
  "events": [
    {
      "id": "gb-2019-08-09",
      "title": "GB 9 Aug 2019 disturbance (Hornsea \u002B Little Barford)",
      "whenUtc": "2019-08-09T15:52:33Z",
      "preEventInertiaMwS": 215000,
      "preEventDemandMw": 21100,
      "losses": [
        {
          "atSeconds": 0.1,
          "deltaPMw": -150,
          "what": "distributed generation vector-shift trip (Ofgem 2.4.2)",
          "provenance": "Recorded"
        },
        {
          "atSeconds": 0.5,
          "deltaPMw": -737,
          "what": "Hornsea 1 deload 799-\u003E62 MW (Ofgem 2.4.3)",
          "provenance": "Recorded"
        },
        {
          "atSeconds": 1,
          "deltaPMw": -244,
          "what": "Little Barford steam turbine trip (Ofgem 2.4.4)",
[exit 0]
```

The file is `{schema, events[]}`. Each event has these fields.

| Field | Meaning |
|---|---|
| `id`, `title`, `whenUtc` | The event's key (matched case-insensitively), its name and its onset in UTC. |
| `preEventInertiaMwS`, `preEventDemandMw` | System inertia (MW.s) and demand (MW) before the event. |
| `losses[]` | Power steps `{atSeconds, deltaPMw, what, provenance}`. A negative `deltaPMw` is a loss and a positive one a restoration. |
| `recordedActions[]` | The same shape for protective and operator actions. Low-frequency demand disconnection enters as a positive step because load is removed. |
| `outcome` | What the record shows: `initialRocofHzPerS`, `nadirHz`, `settleHz`, `lfddMwShed`, `provenance`, `notes`, `rocofMeasurementWindowS` (0 for an instantaneous figure, above 0 for a windowed average that the simulation is scored on too) and `nesoTraceCsvPath` (a 1-second frequency trace that enables the per-sample RMSE check; `replay-event --trace` sets it for one run). |
| `provenance`, `notes` | The event's provenance tag, and free text that carries `[VERIFY]` where a figure is unsourced. |
| `responseServices[]` | Frequency-response services the system held: `mw`, `deadbandHz`, `fullDeliveryDeviationHz`, `timeConstantS`, `what`, `provenance`, `kneeDeviationHz`, `kneeFraction`. |
| `responseServicesComplete` | True when `responseServices` lists all the response held, so the generic governor term is zeroed. Default false. |
| `preEventFrequencyHz` | The recorded frequency the trip landed on; the replay starts in equilibrium there. Absent means nominal. |

A figure without a source carries `CuratedPlaceholder` and a `[VERIFY]` note; the tags are features, and
they stay on the figure until it is sourced. Null fields are omitted on write.

## Event study outputs

**The frequency export (`gridsim-frequency-export/1`)** is one frequency-dynamics simulation with its full
recurrence, so RoCoF and nadir can be checked by hand from the numbers in the file. `replay-event` writes
`baseline-frequency.json`. `scenario` writes `counterfactual-frequency.json`: the device at the lowest swept inertia,
on the worst corner if swept, otherwise the first corner. `tools/crossval/crossval_freq.py` re-integrates the same
physics independently ([chapter 27](<0027 Validation tools.md>)). It integrates the governor, the generic fast
response and an optional forming block, starting from nominal frequency. It checks exports without recorded response
services (the `scenario` counterfactual of `gb-largest-loss-1320`). It reports `NOT CHECKED` with exit 77 for an export
that has recorded services (the `replay-event` exports of `gb-2019-08-09`, 6 services, and `gb-2023-12-22`) and for one
that starts off nominal (`config.initialFrequencyHz`).

```
$ head -n 34 results/exports-docs/replay-2019/baseline-frequency.json
{
  "schema": "gridsim-frequency-export/1",
  "meta": {
    "event": "gb-2019-08-09",
    "gitCommit": "v1.95.294-173-g203c6b9c",
    "timestampUtc": "2026-10-20T05:26:17.2316358Z",
    "elapsedMs": 0,
    "machine": "LAPTOP / Microsoft Windows 10.0.26300",
    "provenance": "DerivedFromRecorded"
  },
  "inertia": {
    "systemInertiaMwS": 215000,
    "deviceRotationalInertiaMwS": 0,
    "effectiveInertiaMwS": 215000,
    "note": "E excludes fast response; FFR is a power injection (see fastResponse), never stored energy."
  },
  "fastResponse": {
    "fastResponseMw": 0,
    "timeConstantS": 0.5,
    "deadbandHz": 0.015,
    "note": "Separate labelled column - a power injection that arrests the nadir, distinct from inertia."
  },
  "demandMw": 21100,
  "config": {
    "nominalHz": 50,
    "droopPu": 0.05,
    "dampingPctPerHz": 3.3175355450236967,
    "responsiveFraction": 0,
    "governorTimeConstantS": 8,
    "stepSeconds": 0.02,
    "durationSeconds": 120
  },
  "systemResponseServices": [
    {
[exit 0]
```

| Block | Fields |
|---|---|
| `meta` | `event`, `gitCommit`, `timestampUtc`, `elapsedMs`, `machine`, `provenance`. The scenario export names the event as `<id>+<device>@<inertia>GVAs` and tags it `Counterfactual`. |
| `inertia` | `systemInertiaMwS`, `deviceRotationalInertiaMwS` (real stored energy), `effectiveInertiaMwS` (the E of the swing equation, never below 1), `note`. |
| `forming` | Only under a grid-forming mode: `mode`, `virtualFormingInertiaMwS` (a control parameter, never summed into real inertia), `formingDampingMwPerHz`, `formingSetpointMw`, `effectiveFormingInertiaMwS`, `note`. |
| `fastResponse` | `fastResponseMw`, `timeConstantS`, `deadbandHz`, `note`. Fast frequency response is a power injection and is not part of E. |
| `demandMw` | The demand the recurrence used. |
| `config` | `nominalHz`, `droopPu`, `dampingPctPerHz`, `responsiveFraction`, `governorTimeConstantS`, `stepSeconds`, `durationSeconds`, and `initialFrequencyHz` when the event began off nominal. |
| `systemResponseServices[]` | Only when the event's recorded services were delivered: `mw`, `deadbandHz`, `fullDeliveryDeviationHz`, `timeConstantS`, `kneeDeviationHz`, `kneeFraction`. |
| `events[]` | The power steps, time-ordered: `{atSeconds, deltaPMw}`. |
| `recurrence[]` | The discrete recurrence as plain-text lines (more under a forming mode). |
| `headline` | `initialRocofHzPerS` (the peak rate), `nadirHz`, `nadirSeconds`, `settleHz`. |
| `points[]` | The whole trajectory: `{t, hz, rocofHzPerS}`, `t` in seconds from the first sample. |

```
$ sed -n '125,144p' results/exports-docs/replay-2019/baseline-frequency.json
  "recurrence": [
    "df = f - f0",
    "pEvent(t) = sum of deltaPMw over events with atSeconds \u003C= t \u002B 1e-6*dt",
    "pGovTarget = -(df/f0)/droopPu * responsiveFraction*demand ;  pGov \u002B= (pGovTarget-pGov)/governorTau * dt",
    "ffrTarget = clamp(-(FFR/0.5)*df, -FFR, \u002BFFR) if |df|\u003Edeadband else 0 ;  pFfr \u002B= (ffrTarget-pFfr)/max(dt,ffrTau) * dt",
    "pImbalance = pEvent \u002B pGov \u002B pFfr - damping*df",
    "rocof = f0/(2E) * pImbalance ;  f \u002B= rocof*dt"
  ],
  "headline": {
    "initialRocofHzPerS": -0.17097330951595094,
    "nadirHz": 48.84096136852519,
    "nadirSeconds": 76,
    "settleHz": 48.89159962877177
  },
  "points": [
    {
      "t": 0,
      "hz": 50,
      "rocofHzPerS": 0
    },
[exit 0]
```

**The evidence dossier (`gridsim-evidence/1`)** is written by `evidence` as `evidence.json`, with `summary.md`
beside it, and has the shape `{schema, report}`. Produced in [chapter 20](<0020 Counterfactuals.md>).

```
$ gridsim evidence --event gb-2019-08-09 --out results/exports-docs/evidence-2019

=== Evidence dossier: GB 9 Aug 2019 disturbance (Hornsea + Little Barford) ===
  when: 2019-08-09T15:52:33.0000000Z   event provenance: DerivedFromRecorded   baseline gate: Reproduced

  column                                          RoCoF    nadir  LFDD   dev inertia  dev FFR
  --------------------------------------------------------------------------------------------
  recorded                                    -0.170Hz/s  48.80Hz   YES        0.0GVAs  0.00GW   [Recorded]
  simulated (no device)                       -0.171Hz/s  48.84Hz    no        0.0GVAs  0.00GW   [DerivedFromRecorded]
  counterfactual (device @ 215 GVA.s)         -0.147Hz/s  49.07Hz    no       30.0GVAs  1.00GW   [Counterfactual]

  single-vs-fleet @ 215 GVA.s: aggregate nadir 49.07 Hz vs 100-unit fleet 49.07 Hz (FFR@nadir 1000 vs 1000 MW).

  AVOIDED COST: 892 MW shed x 0.50 h x GBP 6,000/MWh = GBP 2,676,000  [CuratedPlaceholder]
                (recorded LFDD energy avoided (MW shed x duration h) x VoLL (GBP/MWh) - grounded in the record, not a marketing figure)
                VoLL [CuratedPlaceholder]   duration [CuratedPlaceholder]

  Baseline Reproduced; counterfactual device DOES keep the nadir above LFDD; avoided cost GBP 2,676,000 (CuratedPlaceholder)

  -> results\exports-docs\evidence-2019
[exit 0]
```

| `report` field | Meaning |
|---|---|
| `eventId`, `eventTitle`, `whenUtc`, `eventProvenance` | The replayed event. |
| `baselineVerdict` | `Reproduced`, `Marginal` or `Failed`. |
| `counterfactualUnlocked` | True exactly when the baseline is `Reproduced`. |
| `columns[]` | Recorded, simulated and (when unlocked) counterfactual: `label`, `provenance`, `rocofHzPerS`, `nadirHz`, `settleHz`, `lfddAtRisk` (nadir at or below 48.8 Hz), `deviceRotationalInertiaGvaS`, `deviceFfrGw`. The two device fields are separate on purpose and are non-zero only on the counterfactual column. |
| `riskCurve` | `deviceName` and `points[]`, one per inertia level, comparing no device with the device: `inertiaGvaS`, `rocofNoDevice`, `nadirNoDevice`, `lfddNoDevice`, `rocofWithDevice`, `nadirWithDevice`, `lfddWithDevice`, `lfddAvoidedByDevice`. |
| `fleet` | `inertiaGvaS`, `units`, `staggerSeconds`, `aggregateNadirHz`, `fleetNadirHz`, `aggregateFfrAtNadirMw`, `fleetFfrAtNadirMw`. |
| `avoidedCost` | `lfddMwShedAvoided`, `vollPerMwh`, `durationHours`, `avoidedCostGbp`, `provenance`, `basis`. |
| `summary` | One line of verdict. |

```
$ sed -n '97,115p' results/exports-docs/evidence-2019/evidence.json
    "fleet": {
      "inertiaGvaS": 215,
      "units": 100,
      "staggerSeconds": 10,
      "aggregateNadirHz": 49.06951716271372,
      "fleetNadirHz": 49.06953264157351,
      "aggregateFfrAtNadirMw": 999.9999999999986,
      "fleetFfrAtNadirMw": 999.9999999999993
    },
    "avoidedCost": {
      "lfddMwShedAvoided": 892,
      "vollPerMwh": 6000,
      "durationHours": 0.5,
      "avoidedCostGbp": 2676000,
      "provenance": "CuratedPlaceholder",
      "basis": "recorded LFDD energy avoided (MW shed x duration h) x VoLL (GBP/MWh) - grounded in the record, not a marketing figure",
      "vollProvenance": "CuratedPlaceholder",
      "durationProvenance": "CuratedPlaceholder"
    },
[exit 0]
```

The gating is structural. When the baseline is not `Reproduced`, `columns` stops at two, `riskCurve`, `fleet` and
`avoidedCost` are absent, and `evidence` exits 3, so the file cannot carry an ungated claim. `avoidedCost` appears
only when the device keeps the nadir above the disconnection threshold and the record actually shed load. The cost is
recorded MW shed x duration x value of lost load; the defaults (6,000 GBP per MWh, 0.5 h) are `CuratedPlaceholder`
until you name a source with `--voll-source`.

**The H=0 study (`gridsim-h0-study/1`)** is written by `h0-study` as `h0-study.json`, with `matrix.csv`. It sweeps
synchronous inertia towards zero against a grid-forming fleet fraction. Produced in
[chapter 20](<0020 Counterfactuals.md>).

```
$ sed -n '1,22p' results/exports-docs/h0-2019/h0-study.json
{
  "schema": "gridsim-h0-study/1",
  "meta": {
    "event": "gb-2019-08-09",
    "gitCommit": "v1.95.294-173-g203c6b9c",
    "timestampUtc": "2026-10-20T05:26:14.4192800Z",
    "machine": "LAPTOP / Microsoft Windows 10.0.26300",
    "gfmClass": "gfm-vsm-study-2024",
    "gflClass": "gfl-wind-2024",
    "provenance": "Counterfactual"
  },
  "baseline": {
    "verdict": "Reproduced"
  },
  "system": {
    "largestLossMw": 2768,
    "totalExogenousLossMw": 2768,
    "rocofSettingLossMw": 1521,
    "rocofSettingWindowS": 5,
    "largestSingleLossMw": 737,
    "lfddFirstStageHz": 48.8,
    "nominalHz": 50
[exit 0]
```

| Field | Meaning |
|---|---|
| `meta` | `event`, `gitCommit`, `timestampUtc`, `machine`, `gfmClass`, `gflClass`, `provenance` (`Counterfactual`). |
| `baseline` | `verdict`: the study runs only from a reproduced baseline. |
| `system` | `largestLossMw` (kept for existing readers: it is the total of every exogenous loss, not the largest single one), `totalExogenousLossMw` (the same total), `rocofSettingLossMw` (the losses within the first `rocofSettingWindowS` seconds, the dP of `bareInertiaRocofHzPerS`), `rocofSettingWindowS`, `largestSingleLossMw`, `lfddFirstStageHz`, `nominalHz`. |
| `invariantNote`, `idealizationNote` | Plain-text statements of what is real inertia and what the grid-forming idealisation leaves out. |
| `swing` | What a sidecar needs to re-integrate a cell: `demandMw`, `config`, `services[]`, `events[]`, `cornerFactors`. |
| `axes` | `syncInertiaScales`, `gfmFractions`, `corners`. |
| `inertiaTable[]` | The untransformed per-unit inertia: `name`, `bus`, `type`, `hMwsPerMva`, `mBaseMva`. |
| `cells[]` | One per grid point, below. |
| `recurrence[]` | The relations the cells follow, in plain text. |

A cell holds `syncInertiaScale`, `gfmFraction` (the requested share), `realisedGfmFraction` (the share whole units
realise, which can exceed the request by up to one unit), `corner`, `systemInertiaGvaS` (real), `deviceRotationalInertiaMwS` (always 0: the
study has no device), `virtualInertiaMwS` (the
grid-forming fleet's control inertia, kept apart from the real figure), `formingDampingMwPerHz`,
`effectiveFormingInertiaMwS` (real plus virtual, with no floor), the bare-system figures `bareInertiaRocofHzPerS`,
`bareFormedRocofHzPerS` and `bareFormedNadirHz`, the simulated `rocofHzPerS`, `nadirHz`, `nadirSeconds` and `settleHz`,
a `fleet` block (`trippedCount`, `totalTrippedMw`, `netFleetMwAtNadir`), `formingDependent`, `formedBy`,
`lfddAtRisk`, `cascadeLossOfMains` and a text `assessment`. A figure that is undefined, because nothing forms the
grid, is an explicit `null`; so is every other unset field in the file.

```
$ sed -n '689,711p' results/exports-docs/h0-2019/h0-study.json
  "cells": [
    {
      "syncInertiaScale": 1,
      "gfmFraction": 0,
      "realisedGfmFraction": 0,
      "corner": "Best",
      "systemInertiaGvaS": 171.7,
      "deviceRotationalInertiaMwS": 0,
      "virtualInertiaMwS": 0,
      "formingDampingMwPerHz": 0,
      "effectiveFormingInertiaMwS": 171700,
      "bareInertiaRocofHzPerS": -0.2214618520675597,
      "bareFormedRocofHzPerS": -0.2088442503075672,
      "bareFormedNadirHz": 48.40425498654036,
      "rocofHzPerS": -0.24433439907031654,
      "nadirHz": 49.58837208544303,
      "nadirSeconds": 79.74,
      "settleHz": 49.60001254694652,
      "fleet": {
        "trippedCount": 1,
        "totalTrippedMw": 800,
        "netFleetMwAtNadir": 2297.16961723434
      },
[exit 0]
```

**The device file** that `scenario --device` and `evidence --device` read is plain JSON with no schema id:
`name`, `rotationalInertiaMwS`, `fastResponseMw`, `fastResponseTimeConstantS` and `fastResponseDeadbandHz`. Unknown
keys, such as a `_provenance` note, are ignored. Real stored inertia and fast response are separate fields and are
never merged.

```
$ head -n 6 data/devices/tsgb-fleet.json
{
  "name": "tsgb-fleet-1.14GW",
  "rotationalInertiaMwS": 13700,
  "fastResponseMw": 1140,
  "fastResponseTimeConstantS": 0.05,
  "fastResponseDeadbandHz": 0.015,
[exit 0]
```

## Time-series CSVs

**`transient` writes `timeseries.csv`**: one row per logged slice (`--log-every`), after the `#` header. The slice at
the start, the end and the trip window are always logged. The same slices are the telemetry that pre-solved replay
plays back in the apps. A run with any non-converged slice exits 3.

```
$ gridsim transient case9 --minutes 1 --dt-ms 100 --export-every 100 --out results/exports-docs/transient
[transient] case9: done in 0.3s  nadir 60.000Hz  maxRoCoF -0.001Hz/s  exports 7  -> results\exports-docs\transient
[transient] case9: 600 slices @ 100ms (1 min) on Newton-Raphson (dense)...
[exit 0]
```

```
$ head -n 14 results/exports-docs/transient/timeseries.csv
# GridSim  -  2026-10-20 05:26:20 UTC
# code:    git v1.95.294-173-g203c6b9c  |  GridSim.Core v1.95.467.0  |  build Release
# runtime: .NET 10.0.12  X64  GC=Server
# machine: LAPTOP  Microsoft Windows 10.0.26300
# cpu:     Intel(R) Core(TM) i3-N305  -  8 logical cores  -  8 GB RAM
# simd:    Avx256  (4 doubles/step)  FMA=True
# backend: cpu
# run:     transient  case=case9  minutes=1  dt=100ms  slices=600  solver=Newton-Raphson (dense)
tick,t_seconds,freq_hz,rocof_hz_s,gen_mw,demand_mw,iters,max_mismatch,vmin_pu,vmax_pu,solve_ms,converged
1,0.1000,60.00003,0.00032,319.64,315.00,4,1.646E-014,0.99563,1.04000,36.539,True
2,0.2000,60.00004,0.00006,319.64,315.00,0,1.600E-014,0.99563,1.04000,1.640,True
3,0.3000,60.00001,-0.00032,319.64,315.00,0,1.600E-014,0.99563,1.04000,0.074,True
4,0.4000,60.00000,-0.00007,319.64,315.00,0,1.600E-014,0.99563,1.04000,0.155,True
5,0.5000,59.99999,-0.00012,319.64,315.00,0,1.600E-014,0.99563,1.04000,0.086,True
[exit 0]
```

| Column | Meaning |
|---|---|
| `tick`, `t_seconds` | The slice number and the simulated time. |
| `freq_hz`, `rocof_hz_s` | The swing-equation frequency and its rate of change. |
| `gen_mw`, `demand_mw` | Total generation and demand. |
| `iters`, `max_mismatch`, `converged` | The slice's power-flow outcome; `iters` is 0 when the warm start was already converged. |
| `vmin_pu`, `vmax_pu` | The voltage range. |
| `solve_ms` | The solve time of the slice on this machine. |

`summary.md` beside it holds the run totals: slice count, wall time, solver, frequency nadir and its time, maximum
RoCoF, the number of non-converged slices and the number of math exports.

```
$ head -n 24 results/exports-docs/transient/summary.md
# Transient - case9

    GridSim  -  2026-10-20 05:26:20 UTC
    code:    git v1.95.294-173-g203c6b9c  |  GridSim.Core v1.95.467.0  |  build Release
    runtime: .NET 10.0.12  X64  GC=Server
    machine: LAPTOP  Microsoft Windows 10.0.26300
    cpu:     Intel(R) Core(TM) i3-N305  -  8 logical cores  -  8 GB RAM
    simd:    Avx256  (4 doubles/step)  FMA=True
    backend: cpu
    run:     transient  case=case9  minutes=1  dt=100ms  slices=600  solver=Newton-Raphson (dense)

- slices: 600 @ 100 ms = 1 sim-min
- wall: 0.3 s (600 slices/s)
- solver: Newton-Raphson (dense)
- frequency nadir: 59.9998 Hz at t=38.700 s
- max |RoCoF|: -0.0011 Hz/s at t=57.400 s
- non-convergence: 0
- math exports: 7
[exit 0]
```

**`gda-replay` writes `state-estimate.csv`**: one row per reconstructed timestamp (`index`, `time_utc`, `load_factor`,
`converged`, `iterations`, `max_mismatch`, `total_loss_mw`, `vmin_pu`, `vmax_pu`). With `--bess`, `state-estimate.csv`
gains one `# COUNTERFACTUAL` comment line after its column names, and the run adds `storage-counterfactual.csv`, which
starts with its own single `# COUNTERFACTUAL` line and then its column names, with no `#` header block. The comment
marks hypothetical storage on recorded history, never an input to another replay. The columns of
`storage-counterfactual.csv` are `index`, `site`, `request_mw`, `delivered_mw`,
`aux_mw`, `soc`, `temp_c` and `limited` (1 when a limit curtailed the request). Produced in
[chapter 22](<0022 Estimating GB history.md>) and [chapter 13](<0013 Battery storage studies.md>).

This `state-estimate.csv` is from a `--bess` run:

```bash
gridsim gda-replay --synth --case case9 --bess lfp:5:5:10 --out results/exports-docs/gda-replay
```

```
$ head -n 12 results/exports-docs/gda-replay/state-estimate.csv
# GridSim  -  2026-10-20 05:26:12 UTC
# code:    git v1.95.294-173-g203c6b9c  |  GridSim.Core v1.95.467.0  |  build Release
# runtime: .NET 10.0.12  X64  GC=Server
# machine: LAPTOP  Microsoft Windows 10.0.26300
# cpu:     Intel(R) Core(TM) i3-N305  -  8 logical cores  -  8 GB RAM
# simd:    Avx256  (4 doubles/step)  FMA=True
# backend: cpu
# run:     gda-replay  case=case9  series=synthetic-case9  steps=48
index,time_utc,load_factor,converged,iterations,max_mismatch,total_loss_mw,vmin_pu,vmax_pu
# COUNTERFACTUAL: hypothetical storage added to recorded history (ProvenanceTag.Counterfactual) - never an input to another replay
0,2024-01-01T00:00:00.0000000Z,0.7000,True,4,3.698E-015,2.189,1.02356,1.04076
1,2024-01-01T00:30:00.0000000Z,0.6782,True,2,2.242E-009,2.086,1.02500,1.04117
[exit 0]
```

```
$ head -n 12 results/exports-docs/gda-replay/storage-counterfactual.csv
# COUNTERFACTUAL: hypothetical storage on recorded history (ProvenanceTag.Counterfactual)
index,site,request_mw,delivered_mw,aux_mw,soc,temp_c,limited
0,LFP 5MW/10MWh @5,0.000,0.000,0.0100,0.49998,25.00,0
1,LFP 5MW/10MWh @5,-5.000,-5.000,0.0100,0.71318,27.58,0
2,LFP 5MW/10MWh @5,-5.000,-5.000,0.0117,0.92458,30.00,0
3,LFP 5MW/10MWh @5,-5.000,-0.606,0.0144,0.95000,29.93,1
4,LFP 5MW/10MWh @5,-5.000,-0.000,0.0100,0.95000,29.81,1
5,LFP 5MW/10MWh @5,-5.000,-0.000,0.0100,0.95000,29.70,1
6,LFP 5MW/10MWh @5,-5.000,-0.000,0.0100,0.95000,29.58,1
7,LFP 5MW/10MWh @5,-5.000,-0.000,0.0100,0.95000,29.47,1
8,LFP 5MW/10MWh @5,-5.000,-0.000,0.0100,0.95000,29.37,1
9,LFP 5MW/10MWh @5,-5.000,-0.000,0.0100,0.95000,29.26,1
[exit 0]
```

**The demand series that `gda-replay --series` reads** is JSON: a `name` and `steps[]` of `{timeUtc,
systemLoadFactor}`, with an optional `perBusFactor` object (bus id to factor) on a step for per-bus demand. Every
timestamp is checked against the 169-hour horizon.

```
$ cat results/exports-docs/inputs/series.json
{
  "name": "docs-three-steps",
  "steps": [
    { "timeUtc": "2024-01-01T00:00:00Z", "systemLoadFactor": 0.70 },
    { "timeUtc": "2024-01-01T00:30:00Z", "systemLoadFactor": 0.68 },
    { "timeUtc": "2024-01-01T01:00:00Z", "systemLoadFactor": 0.66 }
  ]
}
[exit 0]
```

```bash
gridsim gda-replay --series results/exports-docs/inputs/series.json --case case9 --out results/exports-docs/gda-series
```

```
$ gridsim gda-replay --series results/exports-docs/inputs/series.json --case case9 --out results/exports-docs/gda-series

=== Historical state estimate: case9 over docs-three-steps ===
  reconstructed 3 timestamps  (2024-01-01T00:00:00.0000000Z .. 2024-01-01T01:00:00.0000000Z)
  non-converged: 0
  system loss over the series: 1.9 .. 2.2 MW

  -> results\exports-docs\gda-series
[exit 0]
```

**`trip --export <file.csv>` writes a frequency trace**: two columns, `dtm` (a timestamp with millisecond
resolution) and `f` (hertz), at the 20 ms step of the simulation. It is the same format as the NESO trace in
`data/neso/`, so `gridsim replay <file>` plays it back and `gridsim freq <file>` summarises it.

```
$ head -n 4 results/exports-docs/trip.csv
dtm,f
2000-01-01 00:00:00.000,50.00000
2000-01-01 00:00:00.020,50.00000
2000-01-01 00:00:00.040,50.00000
[exit 0]
```

**`cpf --pv-out` and `stress --pv-out` write PV curves**: one row per converged point and bus, in solved order, in
round-trip form, with no header lines. `cpf` names the multiplier `lambda`; `stress` names it `load_factor`.

```
$ head -n 3 results/exports-docs/pv.csv
case,lambda,bus_id,vm_pu
IEEE 9-bus (WSCC),1,1,1.04
IEEE 9-bus (WSCC),1,2,1.025
[exit 0]
```

**`dcpf --out <file.csv>`** writes every branch flow (`index`, `name`, `from`, `to`, `flow_mw`, `rate_mva`,
`loading_pct`) and, in `<stem>-buses.csv`, every bus angle and net injection (`bus_id`, `va_deg`, `p_inj_mw`). The
shared writer adds `<stem>-islands.csv` and `<stem>-summary.csv`, and `--json` writes the same tables as one
document; see [the result files](#gridsim-cli-result1---result-files).

## Markdown and CSV reports

The reports of `bench`, `chain`, `stress`, `nminus1-sweep` and `conformance` are a Markdown table for reading and a
CSV for scripts. The Markdown starts with the indented header block; the CSVs of the first four carry a `commit`
column instead of a header. The exceptions are `chain --bess`, whose files have neither, and `screen`, which writes
only a CSV. Produced in [chapter 28](<0028 Benchmarking and GPU.md>),
[chapter 11](<0011 Contingency analysis.md>) and [chapter 27](<0027 Validation tools.md>).

**`chain`** writes `results/chain-<case>.md` and `.csv`: one row per warm-started step.

```bash
gridsim chain case14 --steps 5 --mode ramp --out results/exports-docs/chain-case14.md
```

```
$ head -n 24 results/exports-docs/chain-case14.md
    GridSim  -  2026-10-20 05:26:07 UTC
    code:    git v1.95.294-173-g203c6b9c  |  GridSim.Core v1.95.467.0  |  build Release
    runtime: .NET 10.0.12  X64  GC=Server
    machine: LAPTOP  Microsoft Windows 10.0.26300
    cpu:     Intel(R) Core(TM) i3-N305  -  8 logical cores  -  8 GB RAM
    simd:    Avx256  (4 doubles/step)  FMA=True
    backend: cpu
    run:     chain  case=case14  steps=5  mode=ramp  ramp=0.5%

Each step feeds the previous solved voltages in as the initial condition (warm start).
mode=fixed: same operating point re-solved (expect iters->1, no drift).  ramp: +0.5% load/step.  daily: 24h demand curve.

| Step | Load x | Warm-start iters | Mismatch | Vmin | Vmax |
|-----:|-------:|-----------------:|---------:|-----:|-----:|
| 0 | 1.000 | 2 | 1.32E-010 | 1.010 | 1.090 |
| 1 | 1.005 | 2 | 8.02E-013 | 1.010 | 1.090 |
| 2 | 1.010 | 2 | 7.94E-013 | 1.010 | 1.090 |
| 3 | 1.015 | 2 | 8.09E-013 | 1.010 | 1.090 |
| 4 | 1.020 | 2 | 8.00E-013 | 1.010 | 1.090 |

Cold (flat-start) iters: 4.  Max warm-start iters over 4 warm-started step(s) (steps 1-4; step 0 starts from the case): 2.  
[exit 0]
```

```
$ head -n 6 results/exports-docs/chain-case14.csv
step,load_factor,iters,mismatch,vmin,vmax,commit
0,1.0000,2,1.316E-010,1.0100,1.0900,v1.95.294-173-g203c6b9c
1,1.0050,2,8.017E-013,1.0100,1.0900,v1.95.294-173-g203c6b9c
2,1.0100,2,7.936E-013,1.0100,1.0900,v1.95.294-173-g203c6b9c
3,1.0150,2,8.088E-013,1.0100,1.0900,v1.95.294-173-g203c6b9c
4,1.0200,2,8.003E-013,1.0100,1.0900,v1.95.294-173-g203c6b9c
[exit 0]
```

| Column | Meaning |
|---|---|
| `step`, `load_factor` | The step number and the demand multiplier. |
| `iters`, `mismatch` | Newton iterations and the final mismatch. |
| `vmin`, `vmax` | The voltage range. |
| `commit` | The commit that produced the row. |

With `--bess`, `chain` writes `results/chain-storage-<case>.md` and `.csv` instead. Neither file has the header block,
and the CSV has no `commit` column. The CSV has `step`, `hour`,
`load_factor`, `demand_mw`, `price`, then seven columns per site prefixed `s0_`, `s1_`, ... (`request_mw`,
`delivered_mw`, `aux_mw`, `soc`, `temp_c`, `fade`, `limited`), then `slack_mw`, `vmin`, `vmax`, `loss_mw` and
`converged`.

```
$ head -n 4 results/exports-docs/chain-storage.csv
step,hour,load_factor,demand_mw,price,s0_request_mw,s0_delivered_mw,s0_aux_mw,s0_soc,s0_temp_c,s0_fade,s0_limited,slack_mw,vmin,vmax,loss_mw,converged
0,0,1,315,,5,5,0.01,0.265934,26.9214,0.000145671,0,66.6067,0.995624,1.04,4.59671,1
1,0.5,1,315,,5,4.48479,0.01,0.0499972,28.9905,0.000206137,1,67.1262,0.995625,1.04,4.60094,1
2,1,1,315,,5,0,0.01,0.0499764,28.9346,0.000249144,1,71.6511,0.995631,1.04,4.64112,1
[exit 0]
```

**`bench`** writes `results/benchmark-results.md`, `benchmark-results.csv` and `benchmark-results.run-metadata.json` (or the paths you
give with `--out`): the solver-by-backend matrix over the whole case archive. The CSV columns are `engine`, `case`,
`buses`, `gens`, `branches`, `conv`, `iters`, `mismatch`, `mean_ms`, `runs` and `commit`.

```
$ head -n 5 results/exports-docs/bench.csv
engine,case,buses,gens,branches,conv,iters,mismatch,mean_ms,runs,commit
cpu-dense,case14,14,5,20,True,2,1.316E-010,32.72,1,98048023
cpu-dense,case18,18,1,17,True,4,5.969E-014,7.37,1,98048023
cpu-dense,pglib_opf_case3_lmbd,3,3,3,False,100,6.763E+000,18.39,1,98048023
cpu-dense,case9 (WSCC),9,3,9,True,4,1.646E-014,32.16,1,98048023
[exit 0]
```

**`stress`** writes `results/stress.md` and `.csv` (`case`, `buses`, `max_loadability`, `vmin_at_max`, `commit`).
**`nminus1-sweep`** writes `results/nminus1-sweep.md` and `.csv` (`case`, `buses`, `branches`, `screened`, `secure`,
`violations`, `islanded`, `solves_per_s`, `wall_s`, `sampled`, `commit`, `base_violations`, `not_solved`).

```
$ head -n 4 results/exports-docs/stress.csv
case,buses,max_loadability,vmin_at_max,commit
case14,14,1.75,0.714,v1.95.294-173-g203c6b9c
[exit 0]
```

```
$ head -n 4 results/exports-docs/sweep.csv
case,buses,branches,screened,secure,violations,islanded,solves_per_s,wall_s,sampled,commit,base_violations,not_solved
case14,14,20,20,16,3,0,477.4,0.04,False,v1.95.294-173-g203c6b9c,1,1
[exit 0]
```

**`conformance`** writes `conformance.md`, `conformance.csv` and `run-metadata.json` into `results/conformance`. The CSV
has `#` header lines, then `case`, `buses`, `converged`, `gate_pass`, `law_number`, `law_name`, `status`, `residual`,
`unit`, `tolerance` and `commit`: one row per case and law.

**`screen --csv <file>`** writes the only file `screen` produces, with no Markdown report, no header lines and no
`commit` column. It is the RoCoF screening table: `name`, `lost_mw`, `remaining_inertia_gva_s`,
`peak_rocof_hz_per_s`, `nadir_hz`, `rocof_breach`, `statutory_breach`, `lfdd_risk`.

```
$ head -n 5 results/exports-docs/screen-uk.csv
name,lost_mw,remaining_inertia_gva_s,peak_rocof_hz_per_s,nadir_hz,rocof_breach,statutory_breach,lfdd_risk
Midlands CCGT (marginal),5625.0,126.70,-1.1099,46.486,True,True,True
East Anglia Offshore Wind,4000.0,171.70,-0.5824,47.757,True,True,True
Hinkley Point Nuclear,2880.0,158.90,-0.4531,48.340,True,True,True
Yorkshire CCGT,2400.0,151.70,-0.3955,48.594,True,True,True
[exit 0]
```

## gridsim-cli-result/1 - result files

The verbs that print a console table can also write their result as a file. `--json <file.json>` writes one document,
`--csv <file.csv>` writes CSV files, and `--out <file.json|file.csv>` picks the format by extension. The console output
does not change; every message about the files goes to stderr. [Chapter 19](<0019 CLI reference.md#result-files>) lists
the verbs and the rules, including that the files are written only once the verb has a result, and that a failing
verdict is still written with its exit code inside.

```bash
gridsim losses case14 --json results/exports-docs/losses.json --csv results/exports-docs/losses.csv
```

| Field | Meaning |
|---|---|
| `schema` | `gridsim-cli-result/1`. |
| `tool` | `gridsim <version>+<commit>`. |
| `verb`, `args` | The verb and the full argument list as run. |
| `exitCode` | The exit code the run ends with. |
| `summary` | The verb's scalar results, as `{key: value}`. |
| `tables` | The verb's tables, each an array of `{column: value}` rows. |

```
$ head -n 26 results/exports-docs/losses.json
{
  "schema": "gridsim-cli-result/1",
  "tool": "gridsim 1.95.467+203c6b9cc0edff55f2bd73642c94dc0963a82b92",
  "verb": "losses",
  "args": [
    "losses",
    "case14",
    "--json",
    "results/exports-docs/losses.json",
    "--csv",
    "results/exports-docs/losses.csv"
  ],
  "exitCode": 0,
  "summary": {
    "case": "IEEE 14-bus",
    "buses": 14,
    "total_p_loss_mw": 13.393272357431766,
    "total_q_loss_mvar": 30.122388044873993,
    "shunt_p_mw": 0,
    "reconciliation_residual_mw": 2.362554596402333E-13
  },
  "tables": {
    "branches": [
      {
        "name": "",
        "from": 1,
[exit 0]
```

Keys and column names are snake_case, and numbers are in round-trip form. With `--csv` the main table goes to the
file you name, every other table to `<stem>-<table>.csv`, and the summary to `<stem>-summary.csv` (`key,value`,
beginning with `verb` and `exit_code`). The CSV files carry no `#` header.

```
$ head -n 4 results/exports-docs/losses.csv
name,from,to,p_loss_mw,q_loss_mvar,loading_pct,type
,1,2,4.297600336020395,7.271958043463716,0,line
,1,5,2.762872592516459,6.084349851734967,0,line
,2,3,2.3232693341752366,5.162435824094027,0,line
[exit 0]
```

```
$ head -n 12 results/exports-docs/dcopf-summary.csv
key,value
verb,dcopf
exit_code,0
case,IEEE 14-bus
buses,14
line_limits_enforced,true
feasible,true
total_load_mw,258.99999999999994
total_generation_mw,258.99999999999994
total_cost_gbp_per_h,12949.999999999996
network_constrained,false
[exit 0]
```

## Authored inputs

**The generator cost file (`gridsim-costs/1`)** is read by `dcopf --costs <file.json>` and `acopf --costs <file.json>` and laid over the case's
generators. A unit is named by `index` (1-based, in the case's generator order, out-of-service units included) or by
`name`, never both. `model` is `polynomial` (`coefficients`, highest order first, as MATPOWER writes them) or
`piecewiseLinear` (`points` of `[MW, cost per hour]`); `startup` and `shutdown` are optional and carried only. A unit
the file does not name keeps the case's own curve. An unknown key is refused, and a bad curve is refused naming its
entry. Produced in [chapter 6](<0006 OPF, CPF, taps, distribution factors and losses.md>).

```
$ cat results/exports-docs/inputs/costs.json
{
  "format": "gridsim-costs/1",
  "generators": [
    { "index": 1, "model": "polynomial", "coefficients": [0.11, 5, 150] },
    { "index": 2, "model": "piecewiseLinear", "points": [[0, 0], [100, 2500], [163, 6000]] }
  ]
}
[exit 0]
```

```
$ gridsim dcopf case9 --costs results/exports-docs/inputs/costs.json
DC-OPF - IEEE 9-bus (WSCC) (9 buses)  [cheapest secure dispatch on generator cost curves: costs.json]
  demand 315 MW  generation 315 MW  total cost 6,314.98 cost units/h  (no line limit binds)
  dispatch (by output):
      gen bus 3        110.32 MW  marginal   28.0290 cost units/MWh
      gen bus 1        104.68 MW  marginal   28.0290 cost units/MWh
      gen bus 2        100.00 MW  marginal   25.0000 cost units/MWh
[exit 0]
```

**The switched-shunt file (`gridsim-switched-shunts/1`)** is read by `pf --switched-shunts <file.json>`. Each shunt
needs `bus`, `stepMvar`, `maxSteps`, `vminPu` and `vmaxPu`; `minSteps`, `initialSteps` and `name` are optional. The
solver switches steps until the bus voltage sits inside the band. An unknown key is refused.

```
$ cat results/exports-docs/inputs/shunts.json
{
  "format": "gridsim-switched-shunts/1",
  "shunts": [
    { "bus": 5, "stepMvar": 10, "minSteps": 0, "maxSteps": 4, "initialSteps": 0, "vminPu": 1.02, "vmaxPu": 1.04, "name": "C5" }
  ]
}
[exit 0]
```

```
$ gridsim pf case9 --switched-shunts results/exports-docs/inputs/shunts.json
Power flow - IEEE 9-bus (WSCC) (9 buses)  [1 switched shunt(s)]
  converged in 4 iteration(s), mismatch 2.16E-014 pu   losses 4.619 MW / -103.799 MVAr
  voltage range 0.9984 - 1.0400 pu
  switched shunts: 2 pass(es), settled
      C5               bus 5      steps   1 of 0..4      10.00 MVAr at 1 pu   V 1.0221 pu (band 1.02-1.04)
  lowest voltages:
      bus 9      0.9984 pu     -3.967 deg
      bus 7      1.0175 pu      0.738 deg
      bus 5      1.0221 pu     -3.720 deg
      bus 2      1.0250 pu      9.281 deg
      bus 3      1.0250 pu      4.658 deg
      bus 8      1.0269 pu      3.727 deg
      bus 4      1.0292 pu     -2.209 deg
      bus 6      1.0345 pu      1.966 deg
      bus 1      1.0400 pu      0.000 deg
[exit 0]
```

The three-phase feeder files `gridsim-dist3ph/1` and `gridsim-dist3ph-measurements/1` are the inputs of the
experimental `dist3ph` verb. They are matched exactly by schema id, case-sensitively, and an unknown member is
refused; see [technical chapter 18](<../technical/0018 Real-time, distribution and dynamic estimation.md>).

## Crosswalks and the interposer files

**The point map (`gridsim-rtu-pointmap/1`)** is the crosswalk from a protocol's point addresses to estimator
measurements. You write one per RTU, because an address has no meaning of its own; `gridsim interpose --crosswalk
<map.json>` reads it. Produced in [chapter 25](<0025 Interposing live telemetry.md>).

```
$ cat docs/_captures/interpose/iec104-case14.json
{
  "schema": "gridsim-rtu-pointmap/1",
  "case": "case14",
  "baseMva": 100.0,
  "bindings": [
    { "address": "IOA:1001", "kind": "VmPu",    "bus": 4,               "sigma": 0.01 },
    { "address": "IOA:1002", "kind": "VmPu",    "bus": 5,               "sigma": 0.01 },
    { "address": "IOA:1003", "kind": "VmPu",    "bus": 9, "baseKv": 132.0, "sigma": 0.01 },
    { "address": "IOA:2001", "kind": "PFlowMw", "from": 1, "to": 2, "scale": 0.1, "sigma": 2.0 },
    { "address": "IOA:2002", "kind": "PFlowMw", "from": 1, "to": 5, "scale": 0.1, "sigma": 2.0 },
    { "address": "IOA:3001", "kind": "VaDeg",   "bus": 4, "datumAngleDeg": 10.0, "sigma": 0.5 }
  ]
}
[exit 0]
```

| Field | Meaning |
|---|---|
| `schema`, `case`, `baseMva` | The schema id, the case the bindings were authored against, and the system base (default 100). |
| `bindings[].address` | The native address: `IOA:1001` for IEC 104, `MB:U<unit>:<fc>:@<register>` for a Modbus register by its absolute address, for example `MB:U1:3:@100`, or `MB:U<unit>:<fc>:<index>` for its position in the response. An address bound twice is refused. |
| `bindings[].kind` | The measurement kind, as in a measurement set. |
| `bindings[].bus` or `from`, `to`, `circuit`, `end` | What the point measures. |
| `bindings[].scale` | A raw-to-engineering factor applied first (default 1). Modbus registers commonly hold the value times 10 or 100, so set `scale` to the reciprocal (`0.1` or `0.01`). |
| `bindings[].baseKv`, `baseAmps` | The divisors that convert a reading in kV or amperes to per unit (default 1, meaning the RTU already reports per unit). |
| `bindings[].sigma` | The measurement standard deviation (default 0.01). |
| `bindings[].datumAngleDeg` | Subtracted from angle kinds, to re-reference an external feed to the slack. |
| `bindings[].dataType`, `wordOrder` | Modbus only: `AsDecoded` (the default: the value as the protocol decoded it), `U16`, `S16`, `U32`, `S32` or `F32`, and `HighWordFirst` or `LowWordFirst` for the 32-bit types. A signed quantity needs `S16`. |

`interpose --measurements-dir <dir>` writes each window as a `gridsim-measurements/1` file; its rows carry
`"source": "rtu"`.

```
$ head -n 22 docs/_captures/interpose/iec104-window-1.json
{
  "schema": "gridsim-measurements/1",
  "case": "case14",
  "baseMva": 100,
  "takenUtc": "2026-07-20T13:45:13.25Z",
  "note": "mapped from RTU interposer",
  "measurements": [
    {
      "kind": "VmPu",
      "value": 1.0210000276565552,
      "sigma": 0.01,
      "bus": 4,
      "from": 0,
      "to": 0,
      "end": "From",
      "source": "rtu",
      "isBranch": false
    },
    {
      "kind": "VmPu",
      "value": 0.9869999885559082,
      "sigma": 0.04,
[exit 0]
```

`--out-store <dir>` also archives each window as Parquet under `<dir>/Parquet/<source id>/year=YYYY/week=WW/`, and
writes a firewall marker, `<dir>/Parquet/<source id>/_QUARANTINE.json`, beside the `year=` folders. The marker states that the store is live SCADA telemetry,
not evidence, and must never be merged into the GDA public tables. GridSim refuses to open a store inside the GDA
lake.

```
$ cat docs/_captures/interpose/quarantine-sidecar.json
{
  "store": "rtu_live",
  "source_class": "telemetry",
  "evidential": false,
  "reason": "live SCADA/RTU telemetry - GDA explicit exclude-by-rule (README \u0027does NOT use SCADA data\u0027; manual Table 1.3)",
  "never_merge_into": "GDA unified (MERGE-only) tables",
  "horizon_hours": 169,
  "written_by": "gridsim interpose"
}[exit 0]
```

## gridsim-audit/1 - the audit log

The global `--audit-log <file>` records every `estimate`, `crossval` and `interpose` run in a tamper-evident log. It
is UTF-8 with no byte-order mark, one compact JSON object per line, and every line has these fields in this order.

| Field | Meaning |
|---|---|
| `format` | Always `gridsim-audit/1`. |
| `seq` | The line's zero-based position; it must equal the position in the file. |
| `runUtc` | When the record was written. |
| `stateUtc` | The grid instant the record describes, or `null`. |
| `provenance` | A provenance tag name. |
| `summary` | `actor=<who> action=<verb> inputs=<name>:<sha256>,... outcome=<text>`. A file input is hashed by its bytes, a case folder by a hash over its files, a bundled case by the hash of its name. |
| `prevHash` | The `hash` of the previous line, or `GENESIS` on the first. |
| `hash` | The SHA-256, in upper-case hex, over the line's fields and `prevHash`. |

```bash
gridsim --audit-log results/exports-docs/audit.log estimate case9 --synth --out results/exports-docs/estimate-audit
```

```
$ gridsim --audit-log results/exports-docs/audit.log estimate case9 --synth --out results/exports-docs/estimate-audit

=== WLS state estimate: case9 ===
  status: Converged   iterations: 5   max |dx|: 1.82E-011
  measurements: 63   states: 17   redundancy (dof): 46
  observability: observable (rank 17/17)
  consistency: J = 43.07 vs chi2(95%) = 62.83 -> PASS
  vs synthetic truth: max |dVm| = 5.920E-004 pu   max |dVa| = 6.102E-002 deg

  -> results\exports-docs\estimate-audit
audit: results\exports-docs\audit.log entries=1 head=1408D399580FD9521E26323D5CCA2A9C00DDB3D2E225D20E11665701BA2A0BA9
[exit 0]
```

```
$ head -n 1 results/exports-docs/audit.log
{"format":"gridsim-audit/1","seq":0,"runUtc":"2026-10-04T14:31:32.5952612Z","stateUtc":"2024-03-01T12:00:00.0000000Z","provenance":"Synthetic","summary":"actor=analyst@workstation action=estimate inputs=case-id:D784A646F9FED50DEE41E22A244DBAC3BF6B790D2476876CCB2B3A737613CBEB,synthetic-measurements:E9281C961D5E9D1471BB0178820A6EBED4DC3FA850FF67BB3486DA351A69CBEE outcome=exit=0 method=Wls status=Converged observable=True J=43.07228520174633 chi2=PASS dof=46 removed=0 measurements=63","prevHash":"GENESIS","hash":"E9701F8399B010179142F145A733B2C2BA2BD19931AECCEE808A071299B8068A"}
[exit 0]
```

`gridsim audit verify <file>` recomputes the chain and prints `OK` or `TAMPERED` with the first broken entry's index
and reason; `audit show` prints every entry when the log verifies. Verification catches an edited, deleted, inserted
or reordered line. Removing entries from the end, or rewriting the whole file with recomputed hashes, leaves a
consistent chain, and is caught only against an anchor (the entry count and head hash from an earlier verify) kept
where the writer cannot change it. See [chapter 26](<0026 Audit log, deployment and assurance.md>) and
[technical chapter 31](<../technical/0031 Audit log and diagnostics.md>).

```
$ gridsim audit verify results/exports-docs/audit.log
OK results/exports-docs/audit.log: 1 entries, head 1408D399580FD9521E26323D5CCA2A9C00DDB3D2E225D20E11665701BA2A0BA9
[exit 0]
```

## v2 documents

The v2 schema describes a case as a manifest (`gridsim-case/2`) over network files (`gridsim-network/2`), with
libraries (`gridsim-library/2`) and kind specifications (`gridsim-kind/2`). The documents are validated against the
JSON Schema files under `schemas/v2`, and a `gridsim-case/2` manifest loads as a case through the same model builder
as a v1 case folder. Every other file in this chapter is v1. [Technical chapter 28](<../technical/0028 The v2 JSON schema.md>)
defines the members. You meet these files in two places. The case editor writes them
([chapter 33](<0033 The case editor.md>)), and `gridsim validate` checks any of them and picks the schema from the
document's `schema` tag:

```bash
gridsim validate tests/GridSim.Tests/fixtures/v2/networks/good.network.json tests/GridSim.Tests/fixtures/v2/networks/bad-bus-type.network.json
```

```
$ gridsim validate tests/GridSim.Tests/fixtures/v2/networks/good.network.json tests/GridSim.Tests/fixtures/v2/networks/bad-bus-type.network.json
OK    tests/GridSim.Tests/fixtures/v2/networks/good.network.json (gridsim-network/2)
FAIL  tests/GridSim.Tests/fixtures/v2/networks/bad-bus-type.network.json (gridsim-network/2): 1 error(s)
        /buses/0/type: value "swing" is not one of the allowed values ["pq", "pv", "slack"].
validate: 1 of 2 document(s) valid (schemas: src\GridSim.Cli\bin\Release\net10.0\schemas\v2)
[exit 3]
```

A v2 file allows `//` comments and trailing commas, and a network file names its members in plain words (`baseKv`,
`inertiaH`, `rPu`, `ratingMva`). The writer of the v1 case folder is `gridsim convert`
([chapter 2](<0002 Loading networks.md>)).

## gridviz.json - the visualiser scene

`gridsim export [<case>] [<out.json>] [--dir <folder>] [--frames N]` solves the model over a simulated day (72 frames by
default, model `data/uk`) and writes `results/gridviz.json` for an external visualiser. It carries no `schema` string
and is written unindented. The network is static: `name`, `baseMva`, `buses[]` of `{id, name, kv, type, x, y}`, and
`branches[]` of `{from, to, name, transformer}`. Each entry of `frames[]` is one load level: `hour`, `loadGw`, `genGw`,
`lossMw`, `freqHz` (the model's nominal frequency), `converged`, and per-bus arrays `vm`, `vaDeg`, `pInjMw` and
`qInjMvar` in `buses` order. A frame with `converged` false is not a network state and must not be drawn as one.

```
$ gridsim export results/exports-docs/gridviz.json --frames 4
Exported 20 buses, 24 branches, 4 frames -> results\exports-docs\gridviz.json
[exit 0]
```

```
$ head -c 520 results/exports-docs/gridviz.json
{"name":"GB reduced (10-zone)","baseMva":10000,"buses":[{"id":1,"name":"North Scotland 400kV","kv":400,"type":"Pv","x":0.45,"y":0.97},{"id":2,"name":"South Scotland 400kV","kv":400,"type":"Pv","x":0.42,"y":0.85},{"id":3,"name":"North England 400kV","kv":400,"type":"Pv","x":0.45,"y":0.72},{"id":4,"name":"Yorkshire \u0026 Humber 400kV","kv":400,"type":"Pv","x":0.57,"y":0.66},{"id":5,"name":"North Wales \u0026 Mersey 400kV","kv":400,"type":"Pv","x":0.36,"y":0.6},{"id":6,"name":"Midlands 400kV","kv":400,"type":"Slack",
[exit 0]
```

## Other files you may meet

- **Crash logs.** An unhandled exception writes a plain-text crash log (the exception, build, runtime, operating
  system and arguments) to the folder named by `GRIDSIM_CRASH_DIR`, else to `%LOCALAPPDATA%\GridSim\crash` on Windows.
  See [chapter 30](<0030 Troubleshooting.md>).
- **The GDA data products.** The estimation pipeline's Parquet tables (`estimated_state`, `state_divergence`,
  `estimation_closure` and the GSP measurement tables) and the bridge's wire messages are defined in
  [technical chapter 10](<../technical/0010 File and wire formats.md>) and described in
  [chapter 24](<0024 The GDA bridge.md>).
- **MATPOWER and JSON case folders.** These are loader inputs, documented in
  [chapter 2](<0002 Loading networks.md>). `gridsim convert <input> <output>` also writes them: an output that
  ends in `.m` is written as a MATPOWER file, an output that ends in `.json` as a JSON v2 case (a `gridsim-case/2`
  manifest and its `gridsim-network/2` file, with the asset classes as case-local templates, the binding rules and the
  protective devices), and any other output as a v1 JSON case folder. It never overwrites, and
  it reads the result back and solves both to prove the conversion (see [chapter 19](<0019 CLI reference.md#convert>)).
- **Case-folder documents for dynamics and protection.** `asset-classes.json` and `protections.json` sit in a case
  folder and are read with it. Each is a bare JSON array, with no `schema` member and no schema id. They are described
  in [chapter 29](<0029 Extending GridSim.md>).

## Pitfalls

- **A CSV that will not parse.** Reports with a `#` header need `comment='#'`; reports without one (`bench`, `chain`,
  `stress`, `nminus1-sweep`, `screen`, the shared-writer CSVs) do not. `storage-counterfactual.csv` has a single
  `# COUNTERFACTUAL` line and needs it too.
- **`chiSquarePassed` is `null`.** The test did not run. Read `chiSquareTest` or the `status`; do not treat `null` as
  a pass.
- **A date is refused on load.** A `takenUtc` or `whenUtc` within 169 hours of now, or in the future, is rejected.
  Use a date older than that.
- **A measurement row is refused.** A row without `kind` or `value` fails, naming the row. A `schema` other than
  `gridsim-measurements/1` fails.
- **A cost or shunt file is refused.** An unknown key fails by name; a misspelt field cannot silently drop a cost.
- **No Jacobian in an export.** A sparse solve, a BFS solve and a system above 300 buses omit it; pass
  `--export-jacobian` with the dense engine to force it.
- **`rank` is -1 in an estimate export.** The state size exceeds 2,500 (`DenseEstimationLimits.RankProofMaxState`), so
  the sparse backend proved observability by an island sweep and skipped the rank. At or below 2,500 states it
  reports a real rank, as the dense backend always does.
- **An audit log shorter than you expect.** Verification cannot see entries removed from the end; keep an anchor.
- **Two runs overwrite each other.** A run directory is reused as given, and an existing output is replaced (an input,
  such as the case being read, never is: [ch. 19, Output files](<0019 CLI reference.md#output-files>)). Pass a distinct
  `--out` per run.

## Limits

- The solve export omits per-asset dynamics classes and protection bundles, so a re-imported export runs power flow
  and short-circuit studies but not asset-driven dynamics.
- The estimate export's `model` block omits `frequencyHz`, ZIP loads, OLTCs, boundaries and the short-circuit data
  (it carries `pmin`), so it rebuilds the network for the estimation sidecars but not for a full solve replay; a reader
  that needs the frequency must take it from elsewhere.
- The classical export has no `meta` block and no provenance tag.
- Tail truncation of an audit log is invisible without an external anchor.

## See also

- [Technical chapter 10, File and wire formats](<../technical/0010 File and wire formats.md>) - the field-by-field reference.
- [Technical chapter 28, The v2 schema](<../technical/0028 The v2 JSON schema.md>) - the case, network, library and kind documents.
- [Chapter 19, CLI reference](<0019 CLI reference.md>) - every verb, its flags and its output paths, and [the result files](<0019 CLI reference.md#result-files>).
- [Chapter 5, Running power flows](<0005 Running power flows.md>) - the solve export.
- [Chapter 21, State estimation](<0021 State estimation.md>) - the estimate export, sidecars and measurement sets.
- [Chapter 18, Replaying recorded events](<0018 Replaying recorded events.md>) and [chapter 20, Counterfactuals](<0020 Counterfactuals.md>) - the event catalogue and the study outputs.
- [Chapter 17, Harmonic studies](<0017 Harmonic studies.md>), [chapter 16, Electromagnetic transients](<0016 Electromagnetic transients (EMT).md>) and [chapter 15, RMS, modal and impedance stability](<0015 RMS, modal and impedance stability.md>) - their exports.
- [Chapter 25, Interposing live telemetry](<0025 Interposing live telemetry.md>) - the point map and window files.
- [Chapter 26, Audit log, deployment and assurance](<0026 Audit log, deployment and assurance.md>) - the audit log.
- [Chapter 27, Validation tools](<0027 Validation tools.md>) - the tools that consume the exports.
