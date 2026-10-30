# 10. File and wire formats

This chapter is the field-by-field reference for every document GridSim writes or reads: the versioned JSON schemas
(`gridsim-*`), the sidecar and CSV files a run leaves behind, the authored input files, the bridge and web wire
messages, and the pointers to the protocol frames and interchange formats that have their own chapter. For each
document it gives the schema id and version rule, the writer and reader classes with their files, every field with its
type, unit, nullability and meaning, how a non-finite number is written, the ordering guarantees, the compatibility
rules, and a real excerpt. The user-level guide to the same files, organised by task, is
[manual chapter 31](<../manual/0031 Exports and formats.md>); the two agree, and this chapter carries the detail.

Three families have their own chapter and are not repeated here. The v2 documents (`gridsim-case/2`,
`gridsim-network/2`, `gridsim-library/2`, `gridsim-kind/2`) are [chapter 28](<0028 The v2 JSON schema.md>). The bridge
protocol's server side, query operations and sources are [chapter 9](<0009 GDA integration.md>). The RTU protocol
frames are [chapter 20](<0020 The RTU and SCADA interposer.md>). The hand-editable v1 case folder (`buses.json`,
`generation.json`, ...) is a loader input, specified with the data model in
[chapter 2](<0002 Data model and per-unit conventions.md>) and
[manual chapter 2](<../manual/0002 Loading networks.md>).

**v1 schemas.** `schemas/v1/` holds one JSON Schema per v1 loader file (`asset-bindings`, `asset-classes`,
`boundaries`, `buses`, `demand`, `generation`, `onloadtapchanger`, `protections`, `system`, `transformer`,
`transmission`) plus the shared `v1-common.schema.json`: twelve files, ASCII, with no `default` and no `null`. They are
embedded in GridSim.Core (resources `GridSim.Core.schemas.v1.*`, read by `BundledSchemas`, so the case editor needs no
`schemas/` folder beside the program) and a test pins the embedded bytes equal to the committed files (`V1SchemaTests`).
The numeric ranges mirror the v2 network schema, so a v1 case the editor saves always projects to a valid v2 network.
The v1 loaders themselves (`JsonGridLoader`) do not read these schemas; the case editor (`GridSim.SchemaEditor`)
validates the files an edit touches against them.

Every excerpt is pasted from `docs/_captures/exports/` or `docs/_captures/formats/`. The first line of an excerpt is the
command that produced it. Timestamps, host names, commit stamps, build paths and timings in an excerpt differ by
machine and by run; iteration counts, mismatches and estimated values follow the bit-identity rule of the CPU engines
([chapter 4](<0004 Sparse and GPU numerics.md>)).

## Schema index

| Schema id | Written by | Read by | Section |
|---|---|---|---|
| `gridsim-solve-export/1` | `SolveExport` (`src/GridSim.Core/IO/SolveExport.cs`) | `SolveImport` (`IO/SolveImport.cs`), `BridgeProtocol.BuildModel`, `tools/crossval/crossval.py`, `residual_check.py`, `physics_audit.py` | [Solve export](#gridsim-solve-export1) |
| `gridsim-estimate-export/1` | `EstimateExport` (`IO/EstimateExport.cs`) | `EstimateExportResiduals` (`IO/EstimateExportResiduals.cs`), `tools/crossval/crossval_estimation.py`, `se_residual_check.py`, `physics_audit.py` | [Estimate export](#gridsim-estimate-export1) |
| `gridsim-measurements/1` | `MeasurementSet.Save`, `SaveWithoutOverwrite` (`Estimation/MeasurementSet.cs`) | `MeasurementSet.Load` | [Measurements](#gridsim-measurements1) |
| `gridsim-event-catalog/1` | `EventCatalog.Save` (`Events/EventCatalog.cs`) | `EventCatalog.Load` | Event catalogue, below |
| `gridsim-evidence/1` | `EvidenceReport.ToJson` (`Evidence/EvidenceReport.cs`) | none in C# | [Evidence](#gridsim-evidence1) |
| `gridsim-frequency-export/1` | `FrequencyExport` (`IO/FrequencyExport.cs`) | `tools/crossval/crossval_freq.py` | [Frequency export](#gridsim-frequency-export1) |
| `gridsim-h0-study/1` | `H0StudyExport` (`IO/H0StudyExport.cs`) | `tools/crossval/crossval_h0.py` | [H=0 study](#gridsim-h0-study1) |
| `gridsim-harmonics/1` | `HarmonicSourceSet.Save` (`IO/HarmonicSourceSet.cs`) | `HarmonicSourceSet.Load` | [Harmonic source set](#gridsim-harmonics1) |
| `gridsim-harmonic-export/1` | `HarmonicExport` (`IO/HarmonicExport.cs`) | `tools/crossval/crossval_harmonics.py` | [Harmonic export](#gridsim-harmonic-export1) |
| `gridsim-impedance-stability/1` | `ImpedanceStabilityExport` (`IO/ImpedanceStabilityExport.cs`) | `tools/crossval/crossval_impedance.py` | [Impedance-stability export](#gridsim-impedance-stability1) |
| `gridsim-smallsignal/1` | `SmallSignalExport` (`IO/SmallSignalExport.cs`) | `tools/crossval/crossval_smallsignal.py` | [Small-signal export](#gridsim-smallsignal1) |
| `gridsim-classical-export/1` | `RunStability` in `src/GridSim.Cli/Program.cs` | `tools/crossval/crossval_classical.py` | [Classical export](#gridsim-classical-export1) |
| `gridsim-fdia-screen/1` | the `estimate` handler in `Program.cs` | none | [FDIA screen](#gridsim-fdia-screen1) |
| `gridsim-cli-result/1` | `CliResultWriter` (`src/GridSim.Cli/CliResultWriter.cs`) | none in C# | [CLI result](#gridsim-cli-result1) |
| `gridsim-costs/1` | `GeneratorCostFile.Write` (`IO/Json/GeneratorCostFile.cs`) | `GeneratorCostFile.Apply` | [Generator costs](#gridsim-costs1) |
| `gridsim-switched-shunts/1` | none (authored) | `SwitchedShuntPowerFlow.ReadFile` (`PowerFlow/SwitchedShuntPowerFlow.cs`) | [Switched shunts](#gridsim-switched-shunts1) |
| `gridsim-rtu-pointmap/1` | `RtuPointMap.Save` (`Interpose/RtuPointMap.cs`) | `RtuPointMap.Load` | [Point map](#gridsim-rtu-pointmap1) |
| `gridsim-audit/1` | `AuditLogFile`, `AuditLogWriter` (`Diagnostics/`) | `AuditLogFile.Verify`, `AuditLogFile.Load` | [Audit log](#gridsim-audit1) |
| `gridsim-dist3ph/1`, `gridsim-dist3ph/2`, `gridsim-dist3ph-measurements/1` | none (authored); `dist3ph to-v2` writes `/2` | `MultiPhaseJsonLoader` (`Distribution/MultiPhaseJsonLoader.cs`) | [Other authored inputs](#other-authored-inputs) |
| `gridsim-dynamics/2` | none (authored) | `MachineDynamicsLoader` (`IO/Json/MachineDynamicsLoader.cs`), `stability --dyn-data` | [chapter 24](<0024 Standard dynamic-model library.md>), [chapter 28](<0028 The v2 JSON schema.md>) |
| `gridsim-rms-events-export/1` | `RmsEventsExport` (`IO/RmsEventsExport.cs`), `stability --rms --events ... --events-out` | none in C# | [chapter 24](<0024 Standard dynamic-model library.md>), RMS event studies |
| `gridsim-case/2`, `gridsim-network/2`, `gridsim-library/2`, `gridsim-kind/2` | the case editor | `V2CaseLoader`, `V2DocumentValidator` | [v2 documents](#v2-documents) |

Files with no schema id are covered under [Files without a schema id](#files-without-a-schema-id): the run-metadata
sidecar, the CSV files, the quarantine marker and Parquet rows, `gridviz.json`, the device file, the demand-series file,
the web usage log and the trip trajectory. The result documents written by the Python tools
(`gridsim-crossval/1`, `gridsim-crossval-classical/1`, `gridsim-crossval-estimation/1`, `gridsim-crossval-estimation-baddata/1`,
`gridsim-crossval-freq/1`, `gridsim-crossval-h0/1`, `gridsim-crossval-smallsignal/1`, `gridsim-residual-check/1`,
`gridsim-physics-audit/1`) are outputs of `tools/crossval`; no C# code reads them, and
[chapter 11](<0011 Validation.md>) describes what they compare.

## Conventions

### Serialiser rules

Each writer fixes its own rules, so they differ between documents. This table is the reference; the sections below
point back to it.

| Document | Field names | Layout | Null fields | Enums | A non-finite `double` |
|---|---|---|---|---|---|
| Solve export | camelCase, except `meta` and the trace records `ybus`, `busSpec`, `qLimitEvents` (PascalCase) | indented | omitted | by name (`ToString()`) | string `"NaN"`, `"Infinity"` or `"-Infinity"` |
| Estimate export | as the solve export | indented | omitted; `chiSquarePassed` is an explicit `null` | by name, except `chiSquareTest`, which is a number | named strings, as above |
| Measurement set | camelCase | indented | omitted | by name | the serialiser throws |
| Event catalogue, evidence | camelCase | indented | omitted | by name | the serialiser throws |
| Frequency export | camelCase, `meta` included | indented | omitted | by name | the serialiser throws |
| H=0 study | camelCase, `meta` included | indented | kept as `null` | by name | NaN is written `null`; an infinity throws |
| Harmonic set, point map | camelCase | indented | omitted | by name | the serialiser throws |
| Harmonic export, impedance-stability export | camelCase body, PascalCase `meta` | indented | omitted | by name | named strings |
| Small-signal export | camelCase, no `meta` | indented | omitted | `kind` by name | named strings |
| Classical export | camelCase, no `meta` | compact | kept | not used | the serialiser throws |
| FDIA screen | camelCase | indented | kept | by name (`ToString()`) | not used |
| CLI result | the envelope keys camelCase; keys inside `summary` and `tables` snake_case | indented | `null` is written | by name | named strings; CSV writes the same spellings unquoted |
| Audit log | camelCase | one compact object per line | `stateUtc` is an explicit `null` | by name | not used |
| `gridviz.json` | camelCase | compact | kept | by name | the serialiser throws (values are `float`) |
| Bridge messages (`BridgeProtocol.JsonOptions`) | camelCase | compact on send | omitted | by name | named strings are accepted on read |
| Web API and `/ws/sim` | camelCase (web defaults) | compact | kept | by name | written as `null` (`NonFiniteDoubleConverter`) |
| `run-metadata.json` | camelCase | indented | not applicable | not used | not used |

The `meta` record of the solve, estimate, harmonic and impedance-stability exports is `SolveExportMeta`
(`IO/SolveExport.cs`). It is serialised with no naming policy, so its keys are `Case`, `Backend`, `GitCommit`,
`TimestampUtc`, `ElapsedMs` and `Machine`. The frequency export and the H=0 study have their own camelCase meta
records. A reader that binds names case-insensitively reads either spelling.

A `double` is written in round-trip form, so reading the file back gives the exact bits GridSim computed. Numbers use
the invariant culture; the CLI sets it for the whole process. Timestamps are UTC ISO-8601 strings.

### Version and compatibility rules

Every `gridsim-*` id in this chapter other than the four v2 documents ends in `/1`, the only version of that family. The rules are the same for every document.

- **Additive fields stay in the version.** A new field is added inside `/1`, is optional, and changes no existing value.
  The solve export's `physics` block, `model.boundaries` and `model.oltcs`, and the estimate export's `trace` and
  `irlsCapReached`, are examples. A reader ignores a member it does not know, so a file from a newer build loads in an
  older reader.
- **A value whose meaning must change is made explicit rather than reused.** The estimate export's `chiSquarePassed` is
  `true` or `false` only when the chi-square test ran and is `null` when it did not. A reader must store `null` as
  "did not run" and never as a pass. See [the estimate export](#gridsim-estimate-export1).
- **Strict loaders refuse unknown members** where a misspelling would silently change a result: the v1 case folder
  (`JsonGridLoader`), `gridsim-costs/1`, `gridsim-switched-shunts/1`, `gridsim-dist3ph/1` with its measurements file,
  and the v2 documents. Export readers (`SolveImport`, `BridgeProtocol`, `MeasurementSet`, `EventCatalog`,
  `RtuPointMap`, `HarmonicSourceSet`) ignore unknown members.
- **The version gate differs per reader.**

| Reader | Gate |
|---|---|
| `SolveImport.Read` | `schema` must be present and start with `gridsim-solve-export/`; `model` and `result` must be present |
| `EstimateExportResiduals.Load` | `schema` must equal `gridsim-estimate-export/1`; `estimate.converged` and `residuals` must be present |
| `MeasurementSet.Load`, `HarmonicSourceSet.Load`, `RtuPointMap.Load` | `schema` must be present (`SchemaMember.Require`, member name compared case-insensitively) and must equal the id exactly; a document with no `schema` member is refused with `InvalidDataException` naming the file and the id it must declare |
| `EventCatalog.Load` | `schema` and `events` must be present; the value of `schema` is not compared |
| `MultiPhaseJsonLoader` | `schema` must equal the id exactly, case-sensitively |
| `GeneratorCostFile`, `SwitchedShuntPowerFlow.ReadFile` | the `format` member (not `schema`) must equal the id |
| `AuditLogFile.Verify` | every line's `format` must be `gridsim-audit/1` |

### Ordering

Arrays are written in the model's own order and never sorted. `model.buses` follows `GridModel.Buses` and
`model.branches` follows `GridModel.Branches`. `result.buses` follows the solver's bus index order.
`result.lineFlows` has one row per model branch in branch order, so row `k` belongs to branch `k`; an out-of-service
branch is an all-zero row that keeps its name and rating. `solve.ybus` lists the non-zero entries row by row.
`events[]` in the frequency export is sorted by `atSeconds`. A document written twice from the same deterministic solve
is byte-identical apart from the `meta` timestamp, host and timing fields.

### Horizon

Every dated input is checked on read against the 169-hour forward-inference horizon
(`ForwardInferenceGuard.HorizonHours`, `src/GridSim.Core/Diagnostics/ForwardInferenceGuard.cs`): a measurement set's
`takenUtc`, a harmonic set's `takenUtc`, an event's `whenUtc`, every timestamp of a demand series and every row of a
NESO frequency CSV. A timestamp later than now minus 169 hours (within the last 169 hours, or in the future) is refused.
[Chapter 8](<0008 Provenance and invariants.md>) owns the rule.

## `gridsim-solve-export/1`

The full record of one power-flow solve: the network as solved, the solver's per-iteration working, the converged
state and a physical-law audit. Recording is observational; attaching the recorder does not change the solution.

- **Writer:** `SolveExport.ToJson` and `WriteJson` (`src/GridSim.Core/IO/SolveExport.cs`). Callers: `gridsim <case>
  --once --enableExport[=path]` (`RunExport` in `Program.cs`), `transient` (`exports/slice-NNNNNNNNN.json`),
  `gda-replay` (`exports/state-NNNNNN.json`), `crossval` (`solve-export.json`) and `estimate --export-solve`
  (`estimated-solve-export.json`, written only when the estimate converged). GDA's `gridsim_export.to_solve_export`
  writes the same shape on the lake side.
- **Reader:** `SolveImport.Read` and `ReadFile` (`IO/SolveImport.cs`) return a `SolveSnapshot` (`GridModel`,
  `PowerFlowResult`, case label, timestamp). The `model` block is read by `BridgeProtocol.BuildModel`, the same code
  that reads a bridge `topology`.
- **Top level:** `schema`, `meta`, `model`, `solve`, `result`, `physics`, in that order.

```
$ gridsim case9 --once --enableExport=results/exports-docs/solve-export.json
[export] dense-newton: converged=True iters=4 maxMis=1.65E-014  ->  results\exports-docs\solve-export.json
[exit 0]
```

### `meta`

| Field | Type | Meaning |
|---|---|---|
| `Case` | string | The case label as the CLI was given it (a bundled name, or the path of a folder or file); the model name for the harmonic and impedance-stability exports |
| `Backend` | string | The CLI backend description: `cpu`, `gpu/<vendor> (<device>)`, `cpu (<vendor> requested but unavailable)`, or the verb name for the harmonic and impedance-stability exports. The solver is `solve.solver` |
| `GitCommit` | string | Short commit baked into the build; `unknown` when none |
| `TimestampUtc` | string | Round-trip ISO-8601 instant the export was made |
| `ElapsedMs` | number, ms | Wall time of the computation that produced the document, measured by the writer: the solve (`--enableExport`), the slice's solve (`transient`), the step from the end of the previous step's export to this one, which covers deriving the step, the warm-started solve and its OLTC passes (`gda-replay`; step 0 includes setting up the solver), the solve whose state is exported, that is the kept rung of the tolerance ladder (`crossval`), the estimate including `--hti` (estimate exports), the whole study from the fundamental solve to the fine sweep (harmonic export), or the scan (impedance-stability export). A measurement of the run on its machine, never a computed result |
| `Machine` | string | `<machine name> / <OS description>` for the solve and estimate exports; the machine name alone for the harmonic and impedance-stability exports |

### `model`

The network as solved. Impedances are per unit on `baseMva`. Powers are MW and MVAr. A `double` limit may be a named
infinity string.

| Field | Type | Unit | Meaning |
|---|---|---|---|
| `name` | string | | Model name |
| `baseMva` | number | MVA | System base. Absent reads as 100; a present value that is not finite and positive fails loud (N268; it used to read as 100) |
| `frequencyHz` | number | Hz | Nominal frequency. Absent reads as 50 Hz; a present value that is not finite and positive fails loud |
| `busCount` | integer | | Number of buses; informational, the reader counts `buses` |
| `buses[]`, `generators[]`, `branches[]`, `oltcs[]`, `boundaries[]` | arrays | | Below |

**`buses[]`**

| Field | Type | Unit | Null | Meaning |
|---|---|---|---|---|
| `id` | integer | | no | Bus id |
| `type` | string | | no | `Slack`, `Pv` or `Pq`, in any letter case. An absent or null `type` reads as `Pq`; any other string (a misspelling, a number, a comma list) is refused with `InvalidDataException` naming the bus and the value |
| `baseKv` | number | kV | no | Nominal voltage; 0 when unknown |
| `pd`, `qd` | number | MW, MVAr | no | Demand at 1.0 pu voltage |
| `gs`, `bs` | number | MW, MVAr at 1 pu | no | Shunt conductance and susceptance |
| `vm` | number | pu | no | Voltage set-point or start value |
| `name` | string | | omitted when null | Asset name |
| `zip` | object | | omitted for constant power | `{ap, bp, cp, aq, bq, cq}`, the six ZIP coefficients; the reader's defaults are `0, 0, 1, 0, 0, 1` |
| `vaDeg` | number | deg | no | Initial angle; the reference angle of a slack bus. Absent reads as 0 |
| `x`, `y` | number | | no | Layout coordinates (0..1); 0 when the case gives none |
| `vminPu`, `vmaxPu` | number | pu | omitted when null | The bus's own voltage limits; absent means the global band applies on that side |

**`generators[]`** (the whole fleet, in the model's order; an out-of-service unit is written with `inService` false)

| Field | Type | Unit | Meaning |
|---|---|---|---|
| `bus` | integer | | Bus id |
| `pg`, `qg` | number | MW, MVAr | Output |
| `vg` | number | pu | Voltage set-point |
| `qmin`, `qmax` | number | MVAr | Reactive limits; `-Infinity` and `Infinity` are written as named strings. Reader defaults are infinite |
| `pmin`, `pmax` | number | MW | Active limits; `pmax` may be `Infinity` |
| `mBase` | number | MVA | Machine base (default 100) |
| `h` | number | s | Inertia constant on `mBase`; 0 for non-synchronous plant |
| `type` | string | | Fuel or plant type |
| `name` | string | | Omitted when null |
| `inService` | boolean | | `false` for a unit held out of service (kept in the model, never solved). Absent reads as `true`. A reader that rebuilds the network skips `false` units; `crossval.py` and `residual_check.py` do |
| `dynamicsClass` | string | | The asset-dynamics class bound to the unit; omitted when none |
| `subtransientXdPu`, `negativeSeqXPu` | number | pu on `mBase` | X''d and X2 for the fault studies; 0 means the synthesised default |
| `converterInterfaced` | boolean | | Full-converter plant; omitted when null (inferred from `type`) |
| `converterFaultCurrentPu` | number | x rated | Converter fault-current multiple; omitted when null (the default) |
| `powerFlowOnly` | boolean | | A power-flow device (slack balancer, voltage-control unit), not physical plant |
| `unitTransformerXPu` | number | pu on `mBase` | Step-up transformer reactance; 0 when none is modelled |
| `gridForming` | boolean | | Grid-forming converter control |
| `cost`, `reactiveCost` | object | | The MATPOWER `gencost` curves, `{model, startupCost, shutdownCost, coefficients[]}` with `model` `PiecewiseLinear` or `Polynomial`; omitted when absent. An unknown `model` fails loud |

**`branches[]`**

| Field | Type | Unit | Meaning |
|---|---|---|---|
| `from`, `to` | integer | | Bus ids |
| `r`, `x`, `b` | number | pu | Series resistance, series reactance, total line charging |
| `tap` | number | | Off-nominal ratio. A value of 0 reads as 1 |
| `phaseShiftDeg` | number | deg | Phase shift |
| `rateAMva` | number | MVA | Continuous rating; 0 means unrated |
| `name` | string | | Omitted when null; the key of boundaries and OLTCs |
| `isTransformer` | boolean | | |
| `inService` | boolean | | `false` for an opened branch. Absent reads as `true` |
| `r0`, `x0` | number | pu | Zero-sequence series impedance; 0 when not supplied |
| `fromWinding`, `toWinding` | string | | `Delta`, `WyeGrounded` or `WyeUngrounded`; omitted for a line. An unrecognised name fails loud |
| `fromNeutralXPu`, `toNeutralXPu` | number | pu | Neutral grounding reactance on each side |
| `lengthKm` | number | km | Circuit length; 0 when unknown |
| `equipment` | string | | `ohl` or `cable`; omitted when null |
| `rateBMva`, `rateCMva` | number | MVA | Short-term and emergency ratings (MATPOWER `RATE_B`, `RATE_C`); 0 when not given |
| `dynamicsClass` | string | | The passive-plant dynamics class bound to the branch; omitted when none |

**`oltcs[]`**: `transformer` (the branch `name`; required, a missing one fails loud), `controlsBus` (bus id),
`targetVpu` (1.0), `minTap` (0.9), `maxTap` (1.1), `stepPu` (0.0125), `deadbandPu` (0.01). The defaults are the reader's.

**`boundaries[]`**: `name`, `limitMw` (MW), `branches[]` of `{branch, direction}`: the branch `name` and the sign
(`1` or `-1`) with which its from-end flow counts across the cut. A boundary with no branches is dropped on read, and a
branch name that does not resolve to exactly one branch fails loud.

The generator, branch, OLTC and boundary excerpts come from the export of the reduced GB case, the command shown in the
worked example below. The ZIP excerpt comes from the same case with a ZIP block added to bus 101
(`docs/_captures/formats/inputs/uk-zip`):

```
$ gridsim --dir docs/_captures/formats/inputs/uk-zip --once --enableExport=results/formats/uk-zip-solve.json
[export] dense-newton: converged=True iters=3 maxMis=2.48E-010  ->  results\formats\uk-zip-solve.json
[exit 0]
```

```
$ sed -n '238,251p' results/formats/uk-solve.json
    "generators": [
      {
        "bus": 1,
        "pg": 650,
        "qg": 0,
        "vg": 1.04,
        "qmin": -1300,
        "qmax": 1300,
        "pmin": 0,
        "pmax": 1300,
        "mBase": 1300,
        "h": 3,
        "type": "hydro",
        "name": "North Scotland Hydro"
[exit 0]
```

```
$ sed -n '646,662p' results/formats/uk-solve.json
    "branches": [
      {
        "from": 1,
        "to": 2,
        "r": 0.01,
        "x": 0.1,
        "b": 0.06,
        "tap": 1,
        "phaseShiftDeg": 0,
        "rateAMva": 4000,
        "name": "L1-2 N-S Scotland",
        "isTransformer": false,
        "inService": true,
        "r0": 0,
        "x0": 0,
        "fromNeutralXPu": 0,
        "toNeutralXPu": 0
[exit 0]
```

```
$ sed -n '1056,1064p' results/formats/uk-solve.json
    "oltcs": [
      {
        "transformer": "T1 GSP N Scotland",
        "controlsBus": 101,
        "targetVpu": 1,
        "minTap": 0.9,
        "maxTap": 1.1,
        "stepPu": 0.0125,
        "deadbandPu": 0.01
[exit 0]
```

```
$ sed -n '1148,1158p' results/formats/uk-solve.json
    "boundaries": [
      {
        "name": "B4 Upper North (Scotland)",
        "limitMw": 3200,
        "branches": [
          {
            "branch": "L1-2 N-S Scotland",
            "direction": 1
          }
        ]
      },
[exit 0]
```

```
$ sed -n '129,145p' results/formats/uk-zip-solve.json
        "type": "Pq",
        "baseKv": 132,
        "pd": 600,
        "qd": 150.3741746120811,
        "gs": 0,
        "bs": 0,
        "vm": 1,
        "name": "North Scotland 132kV",
        "zip": {
          "ap": 0.2,
          "bp": 0.3,
          "cp": 0.5,
          "aq": 0.2,
          "bq": 0.3,
          "cq": 0.5
        }
      },
[exit 0]
```

A generator with infinite limits, from a MATPOWER case (`pglib` and PEGASE files carry them):

```
$ gridsim data/matpower/pegase/case1354pegase.m --once --enableExport=results/formats/pegase-solve.json
[export] sparse-newton: converged=True iters=9 maxMis=7.43E-009  ->  results\formats\pegase-solve.json
note: case1354pegase.m: nominal frequency not given (MATPOWER files carry none); assumed 50 Hz - pass the real value if this is, for example, a 60 Hz system (gridsim: --frequency-hz 60).
note: case1354pegase.m: read mpc.gencost (260 row(s)); the costs are carried on the generators and used by dcopf / acopf when asked (--costs case); without it the OPF uses the fuel-type merit order.
[exit 0]
```

```
$ sed -n '15184,15196p' results/formats/pegase-solve.json
      {
        "bus": 4231,
        "pg": 2641.24,
        "qg": 771.6,
        "vg": 1.049182,
        "qmin": "-Infinity",
        "qmax": "Infinity",
        "pmin": 1333.33,
        "pmax": 4188.95,
        "mBase": 100,
        "h": 0,
        "type": ""
      },
[exit 0]
```

**Carried and not carried.** The export carries every member of every bus, generator and branch, the OLTCs and
boundaries, `baseMva` and `frequencyHz`, and `SolveImport` reads every one back: a re-imported model has the same
buses, the same generators (out-of-service units included, still out of service) and the same branches, field for
field. `SolveExportRoundTripTests` and `Doc8ExportsAndFormatsTests` pin the round trip, including a re-solve of the
re-imported model to the same bits. It does not carry the asset-dynamics model and the protection bundle (the content
of `asset-classes.json`, `asset-bindings.json` and `protections.json`); a generator or branch keeps the name of its
dynamics class. The excerpts above were captured before the fields added in October 2026 (bus `vaDeg`, `x`, `y`,
`vminPu`, `vmaxPu`; generator `inService` and the fault, converter, cost and class fields; branch `lengthKm`,
`equipment`, `rateBMva`, `rateCMva`, `dynamicsClass`), which follow the fields shown.

### `solve`

| Field | Type | Meaning |
|---|---|---|
| `solver` | string | `dense-newton`, `sparse-newton` or `bfs`. The export routes a radial network to `bfs`, up to 200 buses to `dense-newton` and above that to `sparse-newton` (`SolverBackend.ExportSolver`); on the CPU backend the numbers are the same bits either way |
| `n` | integer | Buses solved |
| `converged`, `iterations` | boolean, integer | The outcome |
| `status` | string | The solve's `PowerFlowStatus`: `Converged`, `MaxIterations` (the iteration cap or the reactive-limit pass cap was reached) or `NonFinite` (a NaN or infinity appeared). Written after `converged` |
| `maxMismatch` | number, pu | Largest power mismatch on `baseMva` at the returned state; `Infinity` for a non-converged sweep |
| `jacobianOmittedForSize` | boolean | `true` only for a dense solve whose bus count exceeds the gate (300) |
| `slackQNote` | string | A note on the slack reactive output; omitted when none |
| `ybus[]` | array | Non-zero admittance entries `{I, J, G, B}`: zero-based bus indices, per unit on `baseMva`. Empty for `bfs` |
| `busSpec[]` | array | Per bus `{Index, Id, Type, VmInit, VaInitDeg, Pspec, Qspec, Qd, Vg, Qmin, Qmax}` |
| `qLimitEvents[]` | array | Every PV-to-PQ switch and its reversion: `{BusIndex, BusId, PinnedAt, Pass}` |
| `passes[]` | array | One entry per reactive-limit pass |

`busSpec` units, the same on every engine: `VmInit` and `Vg` are pu, `VaInitDeg` is degrees, `Pspec` and `Qspec` are
the net specified injection in pu on `baseMva`, and `Qd`, `Qmin` and `Qmax` are MVAr. The sweep holds no generator
control, so it leaves `Vg`, `Qmin` and `Qmax` at 0. `Type` is the bus's original type; the type a pass actually used is
in `passes[].effectiveTypes`.

A pass is `{pass, effectiveTypes[], pvpqIndices[], pqIndices[], iterations[]}`. `pass` counts the reactive-limit
passes from 0 (the first solve is pass 0) and `iteration` the Newton iterations within the pass from 0, the same
0-based count as the estimate export's `trace[]` and `removed[]`. `pvpqIndices` lists the bus indices that
carry a P equation and `pqIndices` those that carry a Q equation, both in row order. An iteration is
`{iteration, maxMismatch, mismatch[], correction[], jacobian[][]}`: the mismatch the step started from, the correction it
applied, and the Jacobian it used. `mismatch[k]` and `correction[k]` share the pass ordering: the first
`pvpqIndices.length` entries belong to the P equations and the rest to the Q equations. The converged mismatch is
`solve.maxMismatch`; it is not an iteration entry. The Jacobian is written only by the dense engine, for systems up to
300 buses, or for any size when `--export-jacobian` forces it (`--export-jac` for `transient`). The sparse engine never
writes it. For `bfs` each iteration records the per-bus voltage change as `mismatch` and an empty `correction`.

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

### `result`

| Field | Type | Unit | Meaning |
|---|---|---|---|
| `totalLossMw`, `totalLossMvar` | number | MW, MVAr | For the Newton engines the sum of the bus net injections, which includes shunt consumption as well as branch loss; for `bfs` the sum of the branch losses |
| `buses[]` | array | | `{id, vm, vaDeg, pInjMw, qInjMvar}`: voltage (pu), angle (deg) and net injection (MW, MVAr, generation positive) |
| `lineFlows[]` | array | | One row per branch, below |

A `lineFlows` row is `{from, to, pFromMw, qFromMvar, pToMw, qToMvar, pLossMw, qLossMvar, name, ratingMva,
loadingPercent}`. `pFromMw` and `qFromMvar` are the flow into the branch at the from end, `pToMw` and `qToMvar` the flow
into the branch at the to end, so the loss is the sum: `pLossMw = pFromMw + pToMw`, `qLossMvar = qFromMvar + qToMvar`
(negative for charging). `loadingPercent` is `max(|S_from|, |S_to|) / ratingMva * 100`, and 0 when `ratingMva` is 0.

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

### `physics`

The physics-law audit of the solved state (`SolveExport.PhysicsBlock`, `PhysicsLaws.Audit`): `gatePass` (boolean),
`hardGateLaws[]` (the law numbers that gate: `1, 2, 3, 6, 8`), `basis` and `laws[]`, ten entries in law-number order,
each `{number, name, statement, status, residual, unit, tolerance, detail}`. `status` is `Holds`, `Violated`,
`Constitutive`, `NotModeled` or `NotApplicable`. `residual` is `"NaN"` for a law that has no numerical residual.
`tolerance` is `PhysicsLaws.DefaultTolMva(model)`, `BaseMva x 1e-8` (`1e-06` on a 100 MVA base, `1e-04` on the
10,000 MVA `uk` base), the same rule `physics-check`, `conformance`, the web and desktop panels and the sidecars use.
`basis` says what laws 6 and 8 were scored against: `case-dispatch` (a solve: the case file's `Pg` minus load),
`zero-injection-constraints` (an estimate audited by the Feasible gate: its hard zero-injection rows) or
`estimate-state` (any other estimate: nothing outside the state fixes an injection, so laws 6 and 8 read
`NotApplicable`). An estimate export is audited against its own state. `tools/crossval/physics_audit.py` recomputes each
law from `model` and `result` and checks these values.

```
$ sed -n '5307,5316p' results/formats/uk-solve.json
  "physics": {
    "gatePass": false,
    "hardGateLaws": [
      1,
      2,
      3,
      6,
      8
    ],
    "laws": [
[exit 0]
```

```
$ sed -n '5346,5356p' results/formats/uk-solve.json
      },
      {
        "number": 4,
        "name": "Faraday induction",
        "statement": "changing B induces EMF",
        "status": "Constitutive",
        "residual": "NaN",
        "unit": "-",
        "tolerance": 1E-06,
        "detail": "embodied in transformer tap ratio and generator EMF E\u0027 = V \u002B jX\u0027I; no field model to check"
      },
[exit 0]
```

### Reading it back

`SolveImport.Read` rebuilds a `GridModel` through `BridgeProtocol.BuildModel` and a `PowerFlowResult` from `result`
plus `solve.converged`, `solve.iterations`, `solve.maxMismatch` and `solve.status`. The `solve` block and its
`converged` flag are required: a document without either is refused, never read as converged (GR-180). An absent
`iterations` or `maxMismatch` reads as 0, an absent `status` as `Converged` when `converged` is true and
`MaxIterations` when it is false, and a `status` that is not a `PowerFlowStatus` name fails loud. For the estimate's solve export (below),
which has `maxStateUpdate` and no `maxMismatch`, the state update is read into the result's mismatch slot, where the
in-memory projection holds it, so that round trip is exact too. The file's
`meta.Case` and `meta.TimestampUtc` become the snapshot's case and timestamp. It reads no `ybus`, `busSpec`, `passes` or
`physics`. A document from another producer therefore needs only `schema`, `model`, `result` and `solve.converged`. The sparse export of a
large case shows the named-literal rule at work:

```
$ sed -n '85762,85774p' results/formats/pegase-solve.json
      {
        "Index": 639,
        "Id": 4231,
        "Type": "Slack",
        "VmInit": 1.049182,
        "VaInitDeg": 0,
        "Pspec": 26.412399999999998,
        "Qspec": 7.716,
        "Qd": 0,
        "Vg": 1.049182,
        "Qmin": "-Infinity",
        "Qmax": "Infinity"
      },
[exit 0]
```

### The estimate's solve export

`estimate --export-solve` writes `estimated-solve-export.json`: the estimated state projected through
`EstimationResult.ToPowerFlowResult` and written by `SolveExport.WriteJson` with an empty `SolveTrace` and a
`SolveExportEstimate`. Its `model` block is the full solve-export model. There was no power-flow solve, so its `solve`
block says what produced the state and carries no trace and no power mismatch: `solver` is `state-estimate (<method>)`
(for example `state-estimate (Wls)`), then `converged`, `status` (the estimator's `EstimationStatus`, for example
`Converged`), `iterations` (Gauss-Newton) and `maxStateUpdate`, the last Gauss-Newton state update, equal to
`estimate.maxStateUpdate` in `estimate-export.json`. Until October 2026 this block was the solve block with empty trace
arrays and carried that state update under the key `maxMismatch`, which reads as a power mismatch; it is no longer
written. A reader of the old key falls back to its own default.

```
  "solve": {
    "solver": "state-estimate (Wls)",
    "converged": true,
    "status": "Converged",
    "iterations": 5,
    "maxStateUpdate": 2.7236424064891863E-09
  },
```

## `gridsim-estimate-export/1`

Everything one state estimation computed. It is the whole input contract of the `tools/crossval/crossval_estimation.py`
and `se_residual_check.py` sidecars.

- **Writer:** `EstimateExport.ToJson` and `WriteJson` (`src/GridSim.Core/IO/EstimateExport.cs`), called by the `estimate`
  verb for `estimate-export.json`.
- **Readers:** `EstimateExportResiduals.Load` (`IO/EstimateExportResiduals.cs`) reads the residual table back for the
  `estimate-noise-floor` verb; the two Python sidecars read the rest.
- **Top level:** `schema`, `meta`, `model`, `measurements`, `estimate`, `residuals`, `observability`, `removed`,
  `trace`, `physics`, in that order.

| Block | Content |
|---|---|
| `meta` | `SolveExportMeta` (PascalCase keys). `ElapsedMs` is the wall time of the estimate (with `--hti`, of the whole HTI loop) |
| `model` | The network the estimate ran on, a subset of the solve-export model (below) |
| `measurements` | The set the run was asked to fit, as handed to the estimator after any `--prior solve` replacement of voltage-schedule rows and after any `--phaseangle` rows are appended, gross
errors included: `measurementSchema` (`gridsim-measurements/1`), `case`, `takenUtc`, `note`, `count`, `items[]` |
| `estimate` | The fit, below |
| `residuals[]` | One row per measurement that survived to the final fit, below |
| `observability` | The observability report, below |
| `removed[]` | Bad-data removals in the order they were made: `{measurement, normalizedResidual, pass}`. `pass` counts from 0: the first estimate is pass 0 and each removal starts the next pass (the console prints the same number, "removed bad data (pass N)") |
| `trace[]` | Optional, written only with `--trace`: one `{pass, iteration, maxStateUpdate, stepScale}` per Gauss-Newton iteration (`Estimation/EstimationTrace.cs`). `pass` is the same 0-based pass as `removed[].pass`; `iteration` counts from 0 within the pass and, for a robust method, runs on across the IRLS outer loops of the pass. Omitted when no trace was captured. (The `--hti` loop is a different loop with its own `hti-removals.csv` and `hti-corrections.csv`; since N256 it numbers its passes from 0 in the same way: it used to count from 1.) |
| `physics` | The solve-export `physics` block, run on the estimated state evaluated as a power-flow result |

**`model`** carries `name`, `baseMva`, `busCount`, `buses[]` (`id`, `type`, `baseKv`, `pd`, `qd`, `gs`, `bs`, `vm`,
`name`), `generators[]` (`bus`, `pg`, `qg`, `vg`, `qmin`, `qmax`, `pmin`, `pmax`, `mBase`, `h`, `type`, `name`, `inService`: the whole fleet, out-of-service units included, since N256) and `branches[]`
(`from`, `to`, `r`, `x`, `b`, `tap`, `phaseShiftDeg`, `rateAMva`, `name`, `isTransformer`, `inService`), with the same
meanings and units as the solve export. It does not carry `frequencyHz`, `zip`, the zero-sequence and
winding data, `oltcs` or `boundaries`. That is enough for the estimation sidecars to rebuild the network, and not enough
for a full solve replay; the estimate's solve export (`estimated-solve-export.json`) carries the full model.

The model excerpts below come from a synthetic-measurement estimate of `case14`:

```
$ gridsim estimate case14 --synth --export-solve --trace --out results/formats/estimate

=== WLS state estimate: case14 ===
  status: Converged   iterations: 5   max |dx|: 2.72E-009
  measurements: 122   states: 27   redundancy (dof): 95
  observability: observable (rank 27/27)
  consistency: J = 80.01 vs chi2(95%) = 118.75 -> PASS
  vs synthetic truth: max |dVm| = 1.330E-003 pu   max |dVa| = 2.248E-001 deg

  -> results\formats\estimate
[exit 0]
```

```
$ sed -n '11,15p' results/formats/estimate/estimate-export.json
  "model": {
    "name": "IEEE 14-bus",
    "baseMva": 100,
    "busCount": 14,
    "buses": [
[exit 0]
```

```
$ sed -n '157,168p' results/formats/estimate/estimate-export.json
    "generators": [
      {
        "bus": 1,
        "pg": 232.4,
        "qg": -16.9,
        "vg": 1.06,
        "qmin": 0,
        "qmax": 10,
        "pmax": 332.4,
        "mBase": 100,
        "h": 0,
        "type": ""
[exit 0]
```

```
$ sed -n '219,231p' results/formats/estimate/estimate-export.json
    "branches": [
      {
        "from": 1,
        "to": 2,
        "r": 0.01938,
        "x": 0.05917,
        "b": 0.0528,
        "tap": 1,
        "phaseShiftDeg": 0,
        "rateAMva": 0,
        "isTransformer": false,
        "inService": true
      },
[exit 0]
```

**A serialised measurement** (`EstimateExport.MeasurementDoc`) is `{kind, value, sigma, bus, from, to, end, circuit,
source}`. A bus kind writes `bus` and omits `from`, `to` and `end`; a branch kind writes `from`, `to` and `end` and omits
`bus`; `circuit` is omitted when null. This differs from the rows `MeasurementSet.Save` writes (below), which carry every
field.

```
$ sed -n '462,484p' results/formats/estimate/estimate-export.json
  "measurements": {
    "measurementSchema": "gridsim-measurements/1",
    "case": "IEEE 14-bus",
    "takenUtc": "2024-03-01T12:00:00Z",
    "note": "synthetic seed=20260713 noise=1 coverage=vm:1/inj:1/flow:1",
    "count": 122,
    "items": [
      {
        "kind": "VmPu",
        "value": 1.0603366153490539,
        "sigma": 0.004,
        "bus": 1,
        "source": "synthetic"
      },
      {
        "kind": "PInjMw",
        "value": 230.50994250325303,
        "sigma": 1,
        "bus": 1,
        "source": "synthetic"
      },
      {
        "kind": "QInjMvar",
[exit 0]
```

**`estimate`**

| Field | Type | Unit | Meaning |
|---|---|---|---|
| `status` | string | | `EstimationStatus` name: `Converged`, `MaxIterations`, `NonFinite`, `Unobservable` or `Infeasible` |
| `converged` | boolean | | |
| `iterations` | integer | | Gauss-Newton iterations |
| `maxStateUpdate` | number | rad and pu | Largest absolute state update of the last iteration; `Infinity` when the fit did not run |
| `objectiveJ` | number | | The weighted least-squares objective |
| `degreesOfFreedom` | integer | | Redundancy-counting measurements minus states (rows tagged `prior-solved` are not counted); negative without redundancy |
| `chiSquareThreshold` | number | | The 95 % chi-square threshold; `NaN` when the test did not run |
| `chiSquarePassed` | boolean or null | | `true` or `false` only when the test ran; `null` when it did not |
| `chiSquareTest` | integer | | The `ChiSquareOutcome` value: 0 `NotRun`, 1 `Passed`, 2 `Failed` |
| `irlsCapReached` | boolean | | A robust method (Huber, LAV, Schweppe GM) stopped re-weighting on its cap before the weights settled; `false` for the other methods |
| `buses[]` | array | | `{id, vm, vaDeg}`: the estimated state in pu and degrees |

`chiSquarePassed` is always present, in the same place. It is `null` for a fit that did not converge, was not finite or
not observable, had no redundancy (`degreesOfFreedom` 0 or less), or ran with `--no-bad-data`. A reader that stores it
as a verdict must store `null` as "did not run". `chiSquareTest` carries the same outcome as a number.

**`residuals[]`** rows are `{measurement, predicted, residual, normalizedResidual, critical}`. `predicted` and `residual`
are in the measurement's own engineering units (pu, MW, MVAr or degrees). `normalizedResidual` is dimensionless and is
`"NaN"` for a critical measurement, for a fit with no redundancy, and under `--lean` on a passing fit. `critical` is a
boolean. Without `--backfill-removals`, `residuals` is `measurements.items` minus `removed`. Under `--hti`, `removed` is empty,
the removals are in `hti-removals.csv`, and `residuals` still leaves them out. With it, each removed
real-power row (`PInjMw`, `PFlowMw`) is replaced in the fit by a prior-evaluated pseudo-measurement with
`source = "removal-backfill-prior"`, and that row appears in `residuals`.

**`observability`**: `observable`, `rank` (-1 when the sparse backend ran the island sweep instead of a rank proof; the
dense backend and any state of at most 2,500 variables report a real rank, `DenseEstimationLimits.RankProofMaxState`),
`stateSize`, `unobservableBuses[]` (bus ids), `unobservableStates[]` (strings such as `"Vm@bus 5"`) and
`unmeasuredIslandBuses[]`.

A fit that cannot run still writes a complete document:

```
$ sed -n '356,366p' results/formats/estimate-thin/estimate-export.json
  "estimate": {
    "status": "Unobservable",
    "converged": false,
    "iterations": 0,
    "maxStateUpdate": "Infinity",
    "objectiveJ": 74302.14,
    "degreesOfFreedom": -4,
    "chiSquareThreshold": "NaN",
    "chiSquarePassed": null,
    "chiSquareTest": 0,
    "irlsCapReached": false,
[exit 0]
```

```
$ sed -n '417,428p' results/formats/estimate-thin/estimate-export.json
      "measurement": {
        "kind": "VmPu",
        "value": 1.04,
        "sigma": 0.004,
        "bus": 1,
        "source": "scada"
      },
      "predicted": 1,
      "residual": 0.040000000000000036,
      "normalizedResidual": "NaN",
      "critical": false
    },
[exit 0]
```

```
$ sed -n '586,602p' results/formats/estimate-thin/estimate-export.json
  "observability": {
    "observable": false,
    "rank": 13,
    "stateSize": 17,
    "unobservableBuses": [
      5,
      7,
      8,
      9
    ],
    "unobservableStates": [
      "Vm@bus 5",
      "Vm@bus 7",
      "Vm@bus 8",
      "Vm@bus 9"
    ],
    "unmeasuredIslandBuses": []
[exit 0]
```

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

### Reading the residual table

`EstimateExportResiduals.Load` fails loud with the file name for a file that is not JSON, carries another schema, has no
`estimate.converged` or no `residuals` array, or has a row whose `kind` is not a `MeasurementKind`. It accepts a
number, or the strings `"NaN"`, `"Infinity"` and `"-Infinity"`, for `value`, `sigma`, `predicted`, `residual` and
`normalizedResidual`. It returns `converged` with the rows; the noise-floor study skips a non-converged export and says
so. A row must carry the location its kind needs, as the export always writes it: an integer `bus` for `VmPu`,
`PInjMw`, `QInjMvar` and `VaDeg`; integer `from` and `to` and an `end` of exactly `From` or `To` for `PFlowMw`,
`QFlowMvar`, `IMagPu` and `IAngDeg`. A missing, null or non-integer one is refused, naming the file and row (until
October 2026 it read as 0, so a damaged row was grouped as a measurement at bus 0).

## `gridsim-measurements/1`

One case's telemetry, the estimator's input.

- **Writer:** `MeasurementSet.Save` (`src/GridSim.Core/Estimation/MeasurementSet.cs`), used by `estimate --synth` for
  `measurements.json`; `SaveWithoutOverwrite`, used by the interposer, adds a `-dupN` suffix before the extension rather
  than replace a file (`NeverOverwrite`). The Python builders in the GDA repository and hand authoring produce the same
  shape.
- **Reader:** `MeasurementSet.Load(path, utcNow)`, then `Validate(model)`.

| Field | Type | Unit | Meaning |
|---|---|---|---|
| `schema` | string | | `gridsim-measurements/1` |
| `case` | string | | Case name; informational |
| `baseMva` | number | MVA | The base the authoring side assumed (default 100). When both are positive, `Validate` refuses a value that differs from the model's by more than `1e-9` |
| `takenUtc` | string | | Optional instant the telemetry was taken. When present it must clear the horizon, or `Load` throws `ForwardInferenceException` |
| `note` | string | | Free provenance text; the synthetic sampler records its seed and coverage here. Omitted when null |
| `measurements[]` | array | | The rows, below |

A row:

| Field | Type | Unit | Meaning |
|---|---|---|---|
| `kind` | string | | Required. `VmPu`, `PInjMw`, `QInjMvar`, `PFlowMw`, `QFlowMvar`, `VaDeg`, `IMagPu` or `IAngDeg` (`Estimation/Measurement.cs`, `MeasurementKind`) |
| `value` | number | pu, MW, MVAr or deg | Required. Engineering units; converted to per unit once, at ingestion |
| `sigma` | number | the value's unit | Standard deviation. Absent reads as 0, which `Validate` refuses: it must be finite and above 0 |
| `bus` | integer | | Bus id, for the bus kinds `VmPu`, `PInjMw`, `QInjMvar`, `VaDeg` |
| `from`, `to` | integer | | Endpoint bus ids, for the branch kinds `PFlowMw`, `QFlowMvar`, `IMagPu`, `IAngDeg`; matched in the authored orientation |
| `end` | string | | `From` (default) or `To`: the terminal the flow or current is measured into |
| `circuit` | string | | Disambiguates parallel branches: the branch `name`, or `"#k"` for the model branch index `k` of an unnamed parallel. Omitted when null |
| `source` | string | | Free provenance tag, default `scada` |
| `isBranch` | boolean | | Written for the reader's convenience; derived from `kind` and ignored on read |

`MeasurementSet.Save` writes every field of the record, so a row it writes carries `bus`, `from`, `to`, `end`, `source`
and `isBranch` whatever its kind: a bus row has `from: 0`, `to: 0` and `end: "From"`, and a branch row has `bus: 0`.
`PFlowMw`, `QFlowMvar`, `IMagPu` and `IAngDeg` address a branch, and `VaDeg` and `IAngDeg` are degrees against the
estimator's slack reference. Some `source` tags are load-bearing: `virtual-zero-injection` rows become hard equalities
under `--method constrained`, `pseudo-voltage-schedule` rows are the ones `--prior solve` replaces (with
`source = "prior-solved"`), and the estimator writes `removal-backfill-prior` for the pseudo-measurements of
`--backfill-removals`.

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

**Reading.** `Load` first checks that the document has a `schema` member and that every row has `kind` and `value`
(property names compared case-insensitively), and throws one `InvalidDataException` naming the file, or every offending
row by index and bus or branch; a missing value would otherwise read as a confident 0, and a file with no `schema`
member would otherwise read as this format. It then checks the schema gate and the horizon. `Validate` throws one
`InvalidDataException` naming every offender: non-finite values, non-positive sigmas, unknown bus ids, branch references
that resolve to no branch or ambiguously to several parallel circuits, and a base-MVA mismatch.

## `gridsim-event-catalog/1`

The store of recorded frequency events: `{schema, events[]}`.

- **Writer:** `EventCatalog.Save` (`src/GridSim.Core/Events/EventCatalog.cs`), used by `replay-event --dump-catalog` to
  write the three built-in events.
- **Reader:** `EventCatalog.Load(path, utcNow)`, which applies `ForwardInferenceGuard` to every event's `whenUtc`. A
  catalogue cannot carry an event inside the horizon.
- **Shipped files:** `data/events/gb-events.json` and `data/events/gb-2019-08-09-estimated.json`. The built-in events are
  `gb-2019-08-09`, `gb-2023-12-22` and `gb-largest-loss-1320`; the last is a nominal screening case whose figures carry
  `CuratedPlaceholder` and `[VERIFY]` until sourced. `gb-events.json` is exactly the built-in catalogue, as
  `replay-event --dump-catalog` writes it, so `--catalog data/events/gb-events.json` replays the same three events as the
  built-ins (`Doc8ExportsAndFormatsTests` pins it; until October 2026 the file lacked `gb-2023-12-22`).

`GridEvent` (`Events/GridEvent.cs`):

| Field | Type | Unit | Meaning |
|---|---|---|---|
| `id` | string | | Stable id, matched case-insensitively |
| `title` | string | | |
| `whenUtc` | string | | Onset instant, horizon-checked on load |
| `preEventInertiaMwS` | number | MW.s | System inertia before the event |
| `preEventDemandMw` | number | MW | Pre-event demand |
| `losses[]` | array | | `PowerStep {atSeconds, deltaPMw, what, provenance}`; negative `deltaPMw` is a loss, positive a restoration |
| `recordedActions[]` | array | | The same four fields; protective and operator actions. Low-frequency demand disconnection enters as a positive `deltaPMw` |
| `outcome` | object | | `RecordedOutcome`, below |
| `provenance` | string | | `ProvenanceTag` name |
| `notes` | string | | Free text; carries `[VERIFY]` where unsourced. Omitted when null |
| `responseServices[]` | array | | Frequency-response services the system held: `{mw, deadbandHz, fullDeliveryDeviationHz, timeConstantS, what, provenance, kneeDeviationHz, kneeFraction}`. Kept in baseline and counterfactual runs. Omitted when null |
| `responseServicesComplete` | boolean | | `true` when `responseServices` lists all response held, so the generic governor term is zeroed. Default `false` |
| `preEventFrequencyHz` | number | Hz | The recorded frequency the trip landed on; the replay starts in equilibrium there. Omitted for a nominal start |
| `demandEffect` | object | | `RecordedDemandEffect {mwPerHz, what, provenance}`: the demand effect the record gives, in MW of demand reduction per Hz of frequency fall. It replaces the generic load damping for the event: `dampingPctPerHz` becomes `mwPerHz / preEventDemandMw x 100`, so the swing model's damping is exactly `mwPerHz`. `mwPerHz` must be a finite number above 0 and `preEventDemandMw` above 0, or the catalogue is refused at load. Omitted when null (N317; `gb-2019-08-09`: 700, from the ESO interim report's "approximately 350MW at 49.5 Hz" for the event's 29 GW demand) |

`RecordedOutcome`: `initialRocofHzPerS` (Hz/s), `nadirHz` (Hz), `settleHz` (Hz, nullable), `lfddMwShed` (MW),
`nesoTraceCsvPath` (nullable path of a 1-second trace that enables the per-sample RMSE check), `provenance`, `notes`
(nullable), `rocofMeasurementWindowS` (s; 0 means an instantaneous figure and a positive value a windowed average that
the simulation is scored on too).

`GridEvent.ToFrequencyEvents()` merges `losses` and `recordedActions` in time order and is the baseline replay input.
`GridEvent.ExogenousLossEvents()` returns the losses only and is the counterfactual input, because a device replaces the
recorded demand disconnection. `GridEvent.ApplyRecordedServices` stamps `responseServices` and `preEventFrequencyHz` into
a `DynamicsConfig`, which is why the frequency export records both (below), and a `demandEffect` into its
`dampingPctPerHz`, which the export's `config` already carries.

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

## `gridsim-evidence/1`

The evidence dossier, `{schema, report}`. It is written and never read by C# code.

- **Writer:** `EvidenceReport.ToJson` and `WriteJson` (`src/GridSim.Core/Evidence/EvidenceReport.cs`), called by the
  `evidence` verb for `evidence.json`.

| Field of `report` | Type | Meaning |
|---|---|---|
| `eventId`, `eventTitle`, `whenUtc`, `eventProvenance` | | The replayed event |
| `baselineVerdict` | string | `Reproduced`, `Marginal` or `Failed` |
| `counterfactualUnlocked` | boolean | `true` exactly when `baselineVerdict` is `Reproduced` |
| `columns[]` | array | `EvidenceColumn`; two always (recorded, simulated with no device) and a third when unlocked |
| `riskCurve` | object | `{deviceName, points[]}`; each point is `{inertiaGvaS, rocofNoDevice, nadirNoDevice, lfddNoDevice, rocofWithDevice, nadirWithDevice, lfddWithDevice, lfddAvoidedByDevice}` over the inertia axis 260, 155, 120, 102 and 50 GVA.s (`SweepAxes.GbInertiaTrajectoryGvaS`) |
| `fleet` | object | `{inertiaGvaS, units, staggerSeconds, aggregateNadirHz, fleetNadirHz, aggregateFfrAtNadirMw, fleetFfrAtNadirMw}`; the default is 100 units staggered over 10 s |
| `avoidedCost` | object | `{lfddMwShedAvoided, vollPerMwh, durationHours, avoidedCostGbp, provenance, basis}`. Present only when the device keeps the nadir above the demand-disconnection stage and the record actually shed load. The value of lost load (6,000 GBP per MWh) and the 0.5 h duration are `CuratedPlaceholder` policy numbers (`Evidence/AvoidedCost.cs`) |
| `summary` | string | One-line verdict |

An `EvidenceColumn` is `{label, provenance, rocofHzPerS, nadirHz, settleHz, lfddAtRisk, deviceRotationalInertiaGvaS,
deviceFfrGw}`. The two device quantities are separate fields by design: intrinsic inertia is never blurred with the
fast-response injection, and both are non-zero only on the counterfactual column. When the baseline does not reproduce,
`columns` stops at two and `riskCurve`, `fleet` and `avoidedCost` are omitted.

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

## `gridsim-frequency-export/1`

The swing-solve analogue of the solve export: everything a frequency solve used and produced, so that
`RoCoF = f0 * deltaP / (2 E)` can be checked by hand.

- **Writer:** `FrequencyExport.ToJson` and `WriteJson` (`src/GridSim.Core/IO/FrequencyExport.cs`), called by
  `replay-event` for `baseline-frequency.json` and by `scenario` for `counterfactual-frequency.json`.
- **Reader:** `tools/crossval/crossval_freq.py` re-integrates the same swing equation from the document. It checks that
  `schema` starts with `gridsim-frequency-export/` and refuses to compare when the document carries physics the
  re-integration does not model.

| Block | Fields |
|---|---|
| `meta` | `event`, `gitCommit`, `timestampUtc`, `elapsedMs`, `machine`, `provenance` (`FrequencyExportMeta`; default `Synthetic`) |
| `inertia` | `systemInertiaMwS` (MW.s), `deviceRotationalInertiaMwS` (real (1/2) J omega^2, never synthetic), `effectiveInertiaMwS` (the `E` of the swing equation, `max(1, system + device)`), `note` |
| `forming` | Present only when a grid-forming mode is set: `mode`, `virtualFormingInertiaMwS` (a control parameter, never summed into real inertia), `formingDampingMwPerHz`, `formingSetpointMw`, `effectiveFormingInertiaMwS` (system + device + virtual, with no floor), `note` |
| `fastResponse` | `fastResponseMw` (MW), `timeConstantS` (s), `deadbandHz` (Hz), `note`. Fast response is a separate power injection and is not part of `E` |
| `demandMw` | MW |
| `config` | `nominalHz`, `droopPu`, `dampingPctPerHz`, `responsiveFraction`, `governorTimeConstantS`, `stepSeconds`, `durationSeconds`, and `initialFrequencyHz` (Hz) when the replay starts in equilibrium at an offset frequency; omitted for a nominal start |
| `systemResponseServices[]` | The recorded response services the simulation delivered: `{mw, deadbandHz, fullDeliveryDeviationHz, timeConstantS, kneeDeviationHz, kneeFraction}`. Omitted when there are none |
| `events[]` | `{atSeconds, deltaPMw}`, time-ordered |
| `recurrence[]` | The discrete recurrence as six plain-text lines, eight under a grid-forming mode, so the trajectory can be recomputed by hand |
| `headline` | `initialRocofHzPerS` (the largest-magnitude df/dt of the run), `nadirHz`, `nadirSeconds`, `settleHz` |
| `points[]` | `{t, hz, rocofHzPerS}`: the whole trajectory, `t` in seconds from the first sample |

`replay-event` and `scenario` simulate with the event's recorded `responseServices` and `preEventFrequencyHz`
(`GridEvent.ApplyRecordedServices`), and the export records both: `config.initialFrequencyHz` and
`systemResponseServices[]` are present exactly when the event has them, and absent for a service-free nominal start, so
those exports are byte-identical to an export that never had the fields. When `responseServicesComplete` is set the
governor term is zeroed, which shows as `config.responsiveFraction` of 0. `crossval_freq.py` reads the two fields to
decide whether its re-integration would be the same physics, and declines to compare when either is present.

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

The two fields come from the 22 December 2023 event, which records its response services and starts at 49.879 Hz:

```
$ gridsim replay-event gb-2023-12-22 --out results/formats/replay-2023
...
  -> results\formats\replay-2023
[exit 3]
```

The 2023 baseline is Marginal, so the verb exits 3; the export is still written.

```
$ sed -n '24,50p' results/formats/replay-2023/baseline-frequency.json
  "config": {
    "nominalHz": 50,
    "droopPu": 0.05,
    "dampingPctPerHz": 1,
    "responsiveFraction": 0,
    "governorTimeConstantS": 8,
    "stepSeconds": 0.02,
    "durationSeconds": 159,
    "initialFrequencyHz": 49.879
  },
  "systemResponseServices": [
    {
      "mw": 873,
      "deadbandHz": 0.015,
      "fullDeliveryDeviationHz": 0.5,
      "timeConstantS": 0.3,
      "kneeDeviationHz": 0.2,
      "kneeFraction": 0.05
    },
    {
      "mw": 150,
      "deadbandHz": 0.1,
      "fullDeliveryDeviationHz": 0.2,
      "timeConstantS": 0.3,
      "kneeDeviationHz": 0.1,
      "kneeFraction": 0
    },
[exit 0]
```

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

## `gridsim-h0-study/1`

The grid-forming counterfactual sweep: the passed baseline gate, the untransformed per-generator inertia table, the
sweep axes and every cell.

- **Writer:** `H0StudyExport.ToJson` and `WriteJson` (`src/GridSim.Core/IO/H0StudyExport.cs`), called by `h0-study` for
  `h0-study.json`. **Reader:** `tools/crossval/crossval_h0.py` (schema prefix `gridsim-h0-study/`).
- **Nulls:** this writer has no omit rule. A figure that is undefined is written as `null` and is present: NaN becomes
  `null` through one helper, so a forming-dependent cell reads as "undefined" instead of a huge finite number. An
  infinity makes the writer throw.

| Block | Fields |
|---|---|
| `meta` | `event`, `gitCommit`, `timestampUtc`, `machine`, `gfmClass`, `gflClass`, `provenance` (default `Counterfactual`) |
| `baseline` | `verdict` (`Reproduced`, `Marginal` or `Failed`) |
| `system` | `largestLossMw` (MW; kept for existing readers: the absolute sum of the exogenous losses, not the largest loss), `totalExogenousLossMw` (the same total), `rocofSettingLossMw` (the loss within the first `rocofSettingWindowS` = 5 s, the analytic `dP` of `bareInertiaRocofHzPerS`), `rocofSettingWindowS`, `largestSingleLossMw` (the largest single loss step), `lfddFirstStageHz` (Hz), `nominalHz` (Hz) |
| `invariantNote`, `idealizationNote` | Fixed text: real inertia is summed into `E`, virtual inertia only into `effectiveFormingInertiaMwS` |
| `swing` | What a sidecar needs to re-integrate each cell: `demandMw`, `config` (`nominalHz`, `droopPu`, `dampingPctPerHz`, `responsiveFraction`, `governorTimeConstantS`, `fastResponseMw`, `fastResponseTimeConstantS`, `fastResponseDeadbandHz`, `stepSeconds`, `durationSeconds`, `initialFrequencyHz`), `services[]` (as the frequency export's `systemResponseServices`), `events[]`, `cornerFactors` (`best`, `nominal`, `worst`, each `{damping, responsive, governor}`) |
| `axes` | `syncInertiaScales[]`, `gfmFractions[]`, `corners[]` |
| `inertiaTable[]` | Per synchronous unit `{name, bus, type, hMwsPerMva, mBaseMva}` |
| `cells[]` | One per axis combination, below |
| `recurrence[]` | Five plain-text lines stating the arithmetic |

A cell carries `syncInertiaScale`, `gfmFraction` (the requested axis value), `realisedGfmFraction` (the share whole
units realised; it can exceed the request by one unit), `corner`, `systemInertiaGvaS` (real, GVA.s),
`deviceRotationalInertiaMwS` (always 0: the study has no device), `virtualInertiaMwS` (Ev, MW.s), `formingDampingMwPerHz`,
`effectiveFormingInertiaMwS` (real plus virtual, no floor), `bareInertiaRocofHzPerS` (`null` at zero real inertia),
`bareFormedRocofHzPerS`, `bareFormedNadirHz`, `rocofHzPerS`, `nadirHz`, `nadirSeconds`, `settleHz`, `fleet`
(`trippedCount`, `totalTrippedMw`, `netFleetMwAtNadir`), `formingDependent`, `formedBy`, `lfddAtRisk`,
`cascadeLossOfMains` and `assessment`. On a forming-dependent cell `rocofHzPerS` and `nadirHz` are `null`.

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

## `gridsim-harmonics/1`

A set of harmonic current sources over one case, the input of `harmonics --spectra`.

- **Writer:** `HarmonicSourceSet.Save` (`src/GridSim.Core/IO/HarmonicSourceSet.cs`). **Reader:** `HarmonicSourceSet.Load`,
  then `Validate(model)`; `tools/crossval/crossval_harmonics.py` also reads it.
- **Version gate:** a present `schema` must equal `gridsim-harmonics/1`; the horizon applies to `takenUtc`.

| Field | Type | Unit | Meaning |
|---|---|---|---|
| `schema` | string | | |
| `case` | string | | Informational |
| `baseMva` | number | MVA | Default 100; `Validate` refuses a mismatch with the model by more than `1e-9` |
| `takenUtc` | string | | Optional; when present it must clear the horizon |
| `note` | string | | Omitted when null |
| `sources[]` | array | | The sources, below |

A source is `{bus, sourceType, fundamentalCurrentPu, spectrum[], provenance, assetId, name}`: the bus id, a free type
label (`6-pulse`), the reference current in pu, the spectrum, a `ProvenanceTag` name (default `CuratedPlaceholder`, the
tag of an assumed-typical spectrum; a measured spectrum is `Recorded`) and two optional strings. A spectrum entry is
`{order, magnitudePercent, phaseDeg}`: the harmonic order (at least 1), the injection as a percentage of
`fundamentalCurrentPu`, and the phase in degrees (default 0). `Validate` throws one `InvalidDataException` naming every
offender: a bus the model lacks, a non-finite fundamental, a non-positive order, a non-finite magnitude or phase.

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

## `gridsim-harmonic-export/1`

A harmonic study: sources, per-order bus voltages, distortion indices and, optionally, the resonance scan and compliance
verdicts.

- **Writer:** `HarmonicExport.ToJson` and `WriteJson` (`src/GridSim.Core/IO/HarmonicExport.cs`), called by
  `harmonics --enableExport`. **Reader:** `tools/crossval/crossval_harmonics.py` (exact schema match).

| Block | Fields |
|---|---|
| `meta` | `SolveExportMeta`; `Backend` is the verb name and `ElapsedMs` the wall time of the study (the fundamental solve through the fine sweep) |
| `provenance` | `Recorded` only when every source is `Recorded` or `DerivedFromRecorded`; otherwise `CuratedPlaceholder` |
| `model` | `name`, `baseMva`, `busCount`, `buses[]` of `{id, baseKv, name}` |
| `orders[]` | The harmonic orders studied |
| `harmonicModel` | Optional. The inputs beyond the network that `Y(h)` used, so an independent checker can rebuild it: `includeGeneratorSources`, `includeLoadShunts`, `skinCoefficient`, `cableSkinCoefficient`, `longLineCorrection`, `balancedHarmonicSequences`, `generatorSources[]` of `{bus, name, xSystemPu}` and `loads[]` of `{bus, pdMw, qdMvar}` |
| `sources[]` | `{bus, sourceType, provenance, fundamentalCurrentPu, spectrum[]}` |
| `voltage[]` | Sparse: `{order, bus, vmPu, vaDeg}` for each non-zero harmonic voltage, orders outermost |
| `indices` | `worstBusId`, `worstThdPct`, `thd[]` of `{bus, thdPct}` |
| `resonance` | Optional. The driving-point scan peak: `{bus, order, zMag, sharpness}` |
| `resonanceSweep` | Optional. The refined sweep peak: `{bus, peakOrder, peakHz, zMag, sequence, minOrder, maxOrder, gridPoints}` |
| `compliance[]` | Optional. `{bus, baseKv, thdPct, thdLimitPct, worstIhdPct, worstIhdOrder, ihdLimitPct, compliant, voltageLevelKnown}` |

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

## `gridsim-impedance-stability/1`

An impedance-based stability scan of one inverter.

- **Writer:** `ImpedanceStabilityExport.ToJson` and `WriteJson` (`src/GridSim.Core/IO/ImpedanceStabilityExport.cs`),
  called by `impedance-stability --enableExport`. **Reader:** `tools/crossval/crossval_impedance.py` (exact match).

| Field | Content |
|---|---|
| `meta` | `SolveExportMeta` (PascalCase); `ElapsedMs` is the wall time of the scan |
| `provenance` | `Recorded` for measured inner-loop parameters, otherwise `CuratedPlaceholder` |
| `model` | `{name, baseMva, busCount}` |
| `inverterBus`, `mode`, `modelDetail`, `criterion` | The bus id and the converter and analysis choices, as enum names |
| `operatingPoint` | `vd0Pu`, `vq0Pu`, `id0Pu`, `iq0Pu`, `pPu`, `qPu`, `vd0Clamped` |
| `converterParams` | Omitted when null: `pllKp`, `pllKi`, `currentLoopWciRadS`, `idealPll`, `scr`, `xOverR`, `pRatedPu`, `f0Hz`, `canonical` |
| `frequenciesHz[]` | The scan grid |
| `gridImpedance[]`, `converterAdmittance[]` | Per frequency `{fHz, re, im}`. `gridImpedance` is `ZGrid`; `converterAdmittance` is the converter's small-signal admittance `YConv` in pu on the system base (the return ratio is `L = ZGrid * YConv`) |
| `converterImpedance[]` | Deprecated: the same `YConv` values as `converterAdmittance`, bit for bit, under the old and misleading name, kept so readers of earlier exports keep working. Read `converterAdmittance`; this key goes at the next schema version. `crossval_impedance.py` reads `converterAdmittance` and falls back to this key for an older export |
| `minorLoop[]` | Per frequency `{fHz, lRe, lIm, eig1Re, eig1Im, eig2Re, eig2Im}` |
| `nyquist` | `encirclements`, `unstableModeCount`, `stable`, `gainMarginDb`, `phaseMarginDeg`, `criticalFreqHz`, `minDistToMinusOne` |
| `scrSweep[]` | `{scr, encirclements, stable, minDistToMinusOne}` |
| `boundary` | `{criticalScr, found}` |
| `crossval` | `{rhpEigenvalueCountModal, agreement}`: the modal cross-check |

```
$ head -n 30 results/exports-docs/impedance-stability.json
{
  "schema": "gridsim-impedance-stability/1",
  "meta": {
    "Case": "IEEE 9-bus (WSCC)",
    "Backend": "impedance-stability",
    "GitCommit": "v1.95.294-173-g203c6b9c",
    "TimestampUtc": "2026-10-20T05:26:15.3323878Z",
    "ElapsedMs": 31.5601,
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

## `gridsim-smallsignal/1`

A small-signal and modal analysis.

- **Writer:** `SmallSignalExport.ToJson` and `WriteJson` (`src/GridSim.Core/IO/SmallSignalExport.cs`), called by
  `modal --export <file>`. **Reader:** `tools/crossval/crossval_smallsignal.py` (schema prefix `gridsim-smallsignal/`).
  The document has no `meta` block.

| Field | Content |
|---|---|
| `label`, `f0` | Case label and nominal frequency (Hz) |
| `inverterStatesIncluded` | Boolean |
| `states[]` | `{index, kind, ownerId, name, synthetic}`; `kind` is a `StateKind` name (`SwingAngle`, `SwingSpeed`, `VsmAngle`, `VsmSpeed`, `PllAngle`, ...) and `synthetic` separates virtual states from real ones |
| `aMatrix` | The real state matrix as an array of rows |
| `angleNodeBuses[]`, `kext` | The angle-node bus ids and the synchronising matrix |
| `eigenvalues[]` | `{re, im}` per mode |
| `dampingRatios[]`, `frequenciesHz[]` | Per mode |
| `participation[][]` | Per mode, per state. All-zero placeholders when `participationAvailable` is `false` |
| `rhpEigenvalueCount`, `minDampingRatio` | Right-half-plane count and the smallest damping ratio |
| `participationAvailable` | `false` when the state matrix is defective and participation factors do not exist |
| `participationUnavailableReason` | The decomposition's explanation; omitted when available |
| `exitCode` | The exit code the run ends with (1 when any eigenvalue is in the right half-plane, else 0); omitted when not given |

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

## `gridsim-classical-export/1`

The classical-model perturbed trajectory that `tools/crossval/crossval_classical.py` reproduces with an independent
integrator.

- **Writer:** the `RunStability` handler in `src/GridSim.Cli/Program.cs`, for `stability --export <file>` (the classical engine; `--rms` and `--screen` write none). It builds an
  anonymous object and writes it compact with the default serialiser, so the document has no `meta` block and a
  non-finite number makes the verb throw.

| Field | Content |
|---|---|
| `label`, `f0` | Case label and nominal frequency (Hz) |
| `stepSeconds`, `durationSeconds` | The integration step and span (s) |
| `bumpMachine`, `bumpRad` | The index of the lowest-inertia machine and the angle kick applied to it (rad) |
| `machines[]` | `{bus, h, pm, eq, delta0}`: bus id, inertia constant, mechanical power, internal EMF and initial rotor angle, all per unit or radians |
| `delta0Perturbed[]` | The initial angles with the kick applied |
| `yReducedG[][]`, `yReducedB[][]` | The Kron-reduced admittance matrix, real and imaginary parts |
| `sampleTimes[]`, `sampleDelta[][]` | Every 50th step: its time and the rotor angle of every machine |

```
$ head -c 420 results/exports-docs/classical.json
{"schema":"gridsim-classical-export/1","label":"WSCC-9","f0":60,"stepSeconds":0.001,"durationSeconds":5,"bumpMachine":2,"bumpRad":0.1,"machines":[{"bus":1,"h":58.50900000000001,"pm":0.7164102147448185,"eq":1.0465252938930831,"delta0":0.016170595145975637},{"bus":2,"h":12.288000000000002,"pm":1.6300000000000003,"eq":1.0338230694271975,"delta0":0.2580929329264051},{"bus":3,"h":3.8528,"pm":0.85,"eq":1.0168004124084893,"
[exit 0]
```

## `gridsim-fdia-screen/1`

The false-data-injection screen of `estimate --fdia-screen`, written as `fdia-screen.json` beside the estimate export.
No C# code reads it.

| Field | Content |
|---|---|
| `verdict` | `clear`, `suspected`, `chi-square-failed` or `no-verdict` (the estimate did not converge, or the chi-square test did not run) |
| `summary` | The screen's one-line explanation |
| `status` | The `EstimationStatus` name |
| `chiSquareTest` | The `ChiSquareOutcome` name as a string (`NotRun`, `Passed`, `Failed`). This differs from the estimate export, which writes the number |

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

## `gridsim-cli-result/1`

The structured result of a console verb, written by the shared result writer when a verb is given `--json`, `--csv` or
`--out`. The verbs, the flag rules and the exit-code behaviour are in
[chapter 19](<../manual/0019 CLI reference.md#result-files>); this section is the file format.

- **Writer:** `CliResultWriter.JsonText`, `WriteJson`, `WriteCsv` (`src/GridSim.Cli/CliResultWriter.cs`), filled by each
  verb through `CliResult.Set` and `CliResult.Table`. There is no C# reader.
- **Writer verbs:** `CliOutput.WriterVerbs`: `dcopf`, `acopf`, `cpf`, `taps`, `ptdf`, `lodf`, `iec60909`, `fault`,
  `protection-check`, `grade`, `nminus1`, `nminus2`, `n-1-1`, `cycle`, `freq`, `domains`, `dist3ph`, `losses`,
  `physics-check`, `experimental`, `dcpf`, `convert`, `validate`, `zonal`, `pf`, `restore`, `pmu-ingest`. `serve` is
  excluded because it is a resident stream.
- **Atomic:** each file is written to a temporary sibling and moved into place, so a failed write never leaves a half
  file. The files are UTF-8 with no byte-order mark. CSV lines end with the platform newline.

| Field | Type | Meaning |
|---|---|---|
| `schema` | string | `gridsim-cli-result/1` |
| `tool` | string | `gridsim <informational version>`, which carries the commit |
| `verb` | string | The canonical verb name (`nminus1` for `n-1`) |
| `args` | string array | The whole argument list as run |
| `exitCode` | integer | The exit code the run ends with. A failing verdict is still written, with its exit code |
| `summary` | object | The verb's scalar results as `{snake_case_key: value}`, in the order the verb set them |
| `tables` | object | `{table_name: [ {column: value} ]}`, tables in creation order |

A value is `null`, a string, a boolean, an integer (written as a JSON integer), or a double (written in round-trip form
as raw digits). A non-finite double is written as the string `"NaN"`, `"Infinity"` or `"-Infinity"`, because JSON has no
literal for them. Keys, table names and column names are lower-case `snake_case`; a name that is not is a defect in the
verb and throws. Setting a summary key or creating a table twice throws, and `summary` is a reserved table name. A run
that stopped before its result was complete (a usage error, a case that did not load, a solve that did not converge)
writes no file and says so on stderr.

CSV: the first table is the main file at the path given; every other table is `<stem>-<table>.csv` beside it; the
summary is `<stem>-summary.csv` with a `key,value` header whose first rows are `verb` and `exit_code`. A result with no
table writes the summary as the main file. A field is quoted, with doubled quotes, only when it contains a comma, a
quote or a line break; a boolean is `true` or `false`; `null` is an empty field. The files carry no `#` header lines.

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

## Authored inputs

### `gridsim-costs/1`

A generator cost file laid over a case's generators by `dcopf --costs` and `acopf --costs`.

- **Reader:** `GeneratorCostFile.Apply(model, path)` (`src/GridSim.Core/IO/Json/GeneratorCostFile.cs`). **Writer:**
  `GeneratorCostFile.Write(model)`, the inverse by index, with round-trip numbers.
- **Strict:** the version member is `format`, and an unknown key at the top level or in an entry is refused, naming the
  key and the entry. A bad curve is refused naming its entry, when the file is read.

| Field | Type | Meaning |
|---|---|---|
| `format` | string | `gridsim-costs/1` |
| `generators[]` | array | The entries |
| `generators[].index` | integer | 1-based position in the case's generator list in file order, out-of-service units included. Give `index` or `name`, never both. Refused on a case whose loader skipped a generator row (a MATPOWER `mpc.gen` row on an isolated or undefined bus, or a unit on a bus the island pre-pass removed), because the positions no longer match the file: address those units by `name` |
| `generators[].name` | string | Matches `Generator.Name` exactly and must name one unit |
| `generators[].model` | string | `polynomial` or `piecewiseLinear`; required |
| `generators[].coefficients` | number array | For `polynomial`: highest order first, as MATPOWER writes them. All finite |
| `generators[].points` | array of `[MW, cost per hour]` | For `piecewiseLinear`. Each point is a two-number array |
| `generators[].startup`, `shutdown` | number | Optional, default 0; carried and not used by the single-period DC-OPF |

A unit may be named once. A unit the file does not name keeps the case's own curve, so the file overlays the case. The
file is parsed as plain JSON: comments and trailing commas are refused.

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

### `gridsim-switched-shunts/1`

Switched capacitor and reactor banks read by `pf --switched-shunts`.

- **Reader:** `SwitchedShuntPowerFlow.ReadFile(json, label)` (`src/GridSim.Core/PowerFlow/SwitchedShuntPowerFlow.cs`).
  Authored only. The version member is `format`; an unknown key is refused.

| Field | Type | Unit | Meaning |
|---|---|---|---|
| `format` | string | | `gridsim-switched-shunts/1` |
| `shunts[].bus` | integer | | Bus id; required |
| `shunts[].stepMvar` | number | MVAr at 1 pu | Size of one step; required, finite |
| `shunts[].minSteps`, `initialSteps` | integer | | Default 0 |
| `shunts[].maxSteps` | integer | | Required |
| `shunts[].vminPu`, `vmaxPu` | number | pu | The voltage band the bank holds; required, finite |
| `shunts[].name` | string | | Optional label |

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

### `gridsim-rtu-pointmap/1`

The crosswalk from a protocol's point addresses to estimator measurements, read by `interpose --crosswalk`.

- **Reader:** `RtuPointMap.Load` (`src/GridSim.Core/Interpose/RtuPointMap.cs`). **Writer:** `RtuPointMap.Save`. The
  version gate is the one for `MeasurementSet`: the `schema` member is required and must be the id.
  [Chapter 20](<0020 The RTU and SCADA interposer.md>) describes how the bindings are applied.

| Field | Type | Unit | Meaning |
|---|---|---|---|
| `schema`, `case`, `baseMva` | | | Id, informational case name, system base (default 100) |
| `bindings[].address` | string | | The native address, such as `IOA:1001` (IEC 104), `MB:U1:3:@100` (a Modbus register by absolute address) or `MB:U1:3:0` (by position). Each address may be bound once; a repeat is refused naming the address (ordinal, case-sensitive comparison) |
| `bindings[].kind` | string | | A `MeasurementKind` name |
| `bindings[].bus` or `from`, `to`, `circuit`, `end` | | | What the point measures, as in a measurement row |
| `bindings[].scale` | number | | Raw-to-engineering factor applied first; default 1 |
| `bindings[].baseKv`, `baseAmps` | number | kV, A | Divisors that convert kV or amperes to per unit; default 1 |
| `bindings[].sigma` | number | the value's unit | Standard deviation of a good-quality reading; default 0.01 |
| `bindings[].datumAngleDeg` | number | deg | Subtracted from angle kinds |
| `bindings[].dataType` | string | | `AsDecoded` (default), `U16`, `S16`, `U32`, `S32` or `F32`. A non-default type applies only to a Modbus address; a 32-bit type also occupies the next register, which may not be bound separately, and cannot start on register 65535 |
| `bindings[].wordOrder` | string | | `HighWordFirst` (default) or `LowWordFirst`, for the 32-bit types |

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

### Device file and demand series

Two further authored inputs have no schema id. The **device file** read by
`DeviceConfig.Load` (`src/GridSim.Core/Scenarios/ScenarioSpec.cs`) is camelCase JSON with `name`, `rotationalInertiaMwS`
(MW.s), `fastResponseMw` (MW), `fastResponseTimeConstantS` (s, default 0.5) and `fastResponseDeadbandHz` (Hz, default
0.015); other members, such as the `_provenance` notes in `data/devices/tsgb-fleet.json`, are ignored. The **demand
series** read by `GdaTimeSeries.Load` (`src/GridSim.Core/Gda/GdaTimeSeries.cs`) is `{name, steps[]}` with each step
`{timeUtc, systemLoadFactor, perBusFactor}`, where `perBusFactor` is an optional object from bus id to factor; every
`timeUtc` passes the horizon. A document with no `steps` array, or with a `null` entry in it, is refused with
`InvalidDataException` naming the file (it used to fail with the framework's "Value cannot be null (Parameter
'source')"); an empty array is a series of no steps.

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

### Other authored inputs

`gridsim-dist3ph/1` and `gridsim-dist3ph-measurements/1` are read by `MultiPhaseJsonLoader` for the experimental
`dist3ph` verb. The loader is strict: `schema` must match exactly and case-sensitively, member names are case-sensitive,
and an unknown, duplicate or missing member, a wrong type, a non-finite number and a topology that is not one radial
tree are all errors naming the file and the JSON path. Comments are allowed and trailing commas are not. The members
are specified in [chapter 18](<0018 Real-time, distribution and dynamic estimation.md>), in the section on the
`gridsim-dist3ph/1` feeder file.
`asset-classes.json` and `protections.json` in a case folder are bare JSON arrays with no `schema` member;
[chapter 29](<../manual/0029 Extending GridSim.md>) describes them.

## `gridsim-audit/1`

The persistent, hash-chained audit log. The hash function, the verification rules, the anchor and the product wiring are
in [chapter 31](<0031 Audit log and diagnostics.md>); this section is the file format.

- **Writer:** `AuditLogWriter` appends and `AuditLogFile.Save` writes an in-memory `AuditLog` to a new file
  (`src/GridSim.Core/Diagnostics/AuditLogFile.cs`, `AuditLogWriter.cs`, `AuditEvent.cs`). **Reader:**
  `AuditLogFile.Verify` and `Load`.
- **Encoding:** UTF-8 with no byte-order mark, one compact JSON object per line, each ended by a single LF. The encoder
  escapes every non-ASCII character, so each line is plain ASCII and reproducible byte for byte.

Every line has exactly these fields, in this order. The verifier rejects any other set of names or order.

| Field | Type | Meaning |
|---|---|---|
| `format` | string | `gridsim-audit/1` |
| `seq` | integer | 0-based position; it must equal the line's position in the file |
| `runUtc` | string | When the record was written, round-trip `o` form ending in `Z` |
| `stateUtc` | string or null | The grid instant the record describes, same form, or an explicit `null` |
| `provenance` | string | A `ProvenanceTag` name |
| `summary` | string | The event text. A product event reads `actor=<who> action=<what> inputs=<name>:<sha256>,... outcome=<text>`, with `inputs=-` when there are none |
| `prevHash` | string | The previous line's `hash`, or `GENESIS` on the first line |
| `hash` | string | SHA-256, upper-case hex, over `seq`, `runUtc`, `stateUtc` (`-` when null), `provenance`, `summary` and `prevHash` joined by `\|` |

The verifier re-encodes each parsed entry and requires the line to match byte for byte, so a hand edit that the hash does
not cover (spacing, field order, timestamp notation, CR LF, a byte-order mark) is also reported. An empty file is a valid
empty log and a missing file is an error. Removing whole entries from the end, or rewriting the file with recomputed
hashes, leaves a consistent chain and is caught only against an `AuditAnchor` kept outside the file.

```
$ head -n 1 results/exports-docs/audit.log
{"format":"gridsim-audit/1","seq":0,"runUtc":"2026-10-04T14:31:32.5952612Z","stateUtc":"2024-03-01T12:00:00.0000000Z","provenance":"Synthetic","summary":"actor=analyst@workstation action=estimate inputs=case-id:D784A646F9FED50DEE41E22A244DBAC3BF6B790D2476876CCB2B3A737613CBEB,synthetic-measurements:E9281C961D5E9D1471BB0178820A6EBED4DC3FA850FF67BB3486DA351A69CBEE outcome=exit=0 method=Wls status=Converged observable=True J=43.07228520174633 chi2=PASS dof=46 removed=0 measurements=63","prevHash":"GENESIS","hash":"E9701F8399B010179142F145A733B2C2BA2BD19931AECCEE808A071299B8068A"}
[exit 0]
```

## Files without a schema id

### `run-metadata.json` and the report header

`RunInfo.WriteJson` (`src/GridSim.Cli/RunInfo.cs`) writes the run-metadata sidecar: indented camelCase, with the same
fields for every verb. Where it goes depends on the kind of verb:

- A verb that writes a run **folder** (for example `transient`, `replay-event`, `h0-study`, `gda-replay`, `emt` and
  `estimate`) writes `run-metadata.json` inside it. The folder belongs to the one run.
- A verb that writes a **report file** (`bench`, `nminus1-sweep`, `stress`, `chain`) writes `<report>.run-metadata.json`
  beside the report, named after it (`RunInfo.MetadataPathFor`): `results/bench.md` gets
  `results/bench.run-metadata.json`. Until October 2026 these verbs wrote a fixed `run-metadata.json` into the report's
  folder, so several reports in one folder (the default `results/`, or `tools/run-regime.sh`) kept only the last run's
  metadata. `bench` also records its run settings in a `parameters` object: `runs`, `parallel`, `cases[]` (in order),
  `casesFlag`, `gpuRequested` (`auto`, a vendor, `cpu`, or null when no `--gpu` was given), `engines[]`,
  `warmupSolvesPerCell` (1) and the full `report` and `csv` paths.

| Field | Type | Meaning |
|---|---|---|
| `timestampUtc` | string | ISO-8601 instant the sidecar was written |
| `backend` | string | `cpu`, `gpu/<vendor> (<device>)` or `cpu (<vendor> requested but unavailable)` |
| `gitCommit`, `buildConfig`, `coreVersion` | string | Short commit, `Debug` or `Release`, and the `GridSim.Core` assembly version |
| `runtime`, `arch`, `serverGc` | string, string, boolean | The .NET runtime, process architecture and GC mode |
| `simdTier`, `simdLanes`, `simdFma` | string, integer, boolean | The SIMD tier, doubles per vector step, and whether fused multiply-add is available |
| `machineName`, `os`, `cpu`, `logicalCores`, `ramGb` | | The host; `cpu` is the processor's model name (Linux: `/proc/cpuinfo`; Windows: the registry's `ProcessorNameString`; macOS: `sysctl machdep.cpu.brand_string`), and the process architecture only when the OS does not answer; `ramGb` is rounded to one decimal |
| `run` | string | The verb and its key parameters as one string |

`RunInfo.HeaderLines` produces the same information as eight lines: the first four are `GridSim  -  <UTC time>`,
`code:`, `runtime:` and `machine:`, then `cpu:`, `simd:`, `backend:` and `run:`. A Markdown report indents them by four
spaces and a CSV prefixes each with `# `.

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

### CSV files

| File | Written by | Header line (after any `#` lines) |
|---|---|---|
| `residuals.csv` | `estimate` | `kind,location,value,sigma,predicted,residual,normalized_residual,critical` |
| `hti-removals.csv` | `estimate --hti` | `pass,kind,location,value,sigma,estimated_error,estimated_error_unit,estimated_error_measured,measured_unit,test_statistic` |
| `hti-corrections.csv` | `estimate --hti --hti-correct` | `pass,kind,location,original_value,corrected_value,sigma,estimated_error,estimated_error_unit,estimated_error_measured,measured_unit,test_statistic` |
| `noise-floor.csv` | `estimate-noise-floor` | `source,kind,count,spread_robust,spread_std,bias,suggested_sigma_scale,miscalibrated` |
| `timeseries.csv` | `transient` | `tick,t_seconds,freq_hz,rocof_hz_s,gen_mw,demand_mw,iters,max_mismatch,vmin_pu,vmax_pu,solve_ms,converged` |
| `state-estimate.csv` | `gda-replay` | `index,time_utc,load_factor,converged,iterations,max_mismatch,total_loss_mw,vmin_pu,vmax_pu` |
| `storage-counterfactual.csv` | `gda-replay --bess` | one `# COUNTERFACTUAL` comment, then `index,site,request_mw,delivered_mw,aux_mw,soc,temp_c,limited` |
| `matrix.csv` (`scenario`) | `scenario` | `inertia_gvas,device,corner,rocof_hz_s,nadir_hz,settle_hz,lfdd_at_risk,assessment` |
| `risk-curve.csv` | `scenario` | `inertia_gvas,rocof_no_device,nadir_no_device,lfdd_no_device,rocof_with_device,nadir_with_device,lfdd_with_device,lfdd_avoided_by_device` |
| `matrix.csv` (`h0-study`) | `h0-study` | `sync_inertia_scale,gfm_fraction,corner,system_inertia_gvas,virtual_inertia_mws,effective_forming_inertia_mws,bare_inertia_rocof,rocof_hz_s,nadir_hz,settle_hz,tripped_count,total_tripped_mw,forming_dependent,formed_by,lfdd_at_risk,cascade_lom` |
| `fleet.csv` | `fleet` | `units,rocof_hz_s,nadir_hz,ffr_at_nadir_mw,peak_fleet_ffr_mw,lfdd_at_risk` |
| `timeseries.csv` (`emt`) | `emt` | `t_seconds`, then one column per probe name; values written with `G9` |
| PV curves | `cpf --pv-out`, `stress --pv-out` | `case,lambda,bus_id,vm_pu` for `cpf`, `case,load_factor,bus_id,vm_pu` for `stress` |
| screening table | `screen --csv` | `name,lost_mw,remaining_inertia_gva_s,peak_rocof_hz_per_s,nadir_hz,rocof_breach,statutory_breach,lfdd_risk` |
| `conformance.csv` | `conformance` | `case,buses,converged,gate_pass,law_number,law_name,status,residual,unit,tolerance,commit` |

The files in this table carry the `# ` header lines of `RunInfo.HeaderLines` except `storage-counterfactual.csv`, the
PV-curve CSVs and the screening table. The CSVs of `bench`, `chain`, `stress` and `nminus1-sweep` carry no header
lines; `bench`, `chain` and `stress` end every row with a `commit` column, and `nminus1-sweep` carries `commit` before
its last two columns (`base_violations`, `not_solved`); their layouts are in [manual chapter 31](<../manual/0031 Exports and formats.md#markdown-and-csv-reports>).
`hti-removals.csv` and `hti-corrections.csv` carry one more `#` line after the header lines,
`# passes=<n> total_iterations=<n> stop_reason=<reason>`. A reader needs `comment='#'` for the files with header lines.

`residuals.csv` has one row per measurement in the final fit. `location` is `Measurement.Describe()`: `bus 4`,
`branch 1->2 (from)`, or `branch 1->2 'name' (from)` when a circuit is named. `value` and `predicted` are written with
`G17`, `sigma` with `G6`, `residual` with `E6`, `normalized_residual` with `F3` (`NaN` for a critical row, and under
`--lean` on a passing fit) and `critical` as `True` or `False`. The `hti-removals.csv` error columns carry the estimator's
internal units in `estimated_error` with the unit named in `estimated_error_unit` (`pu`, `rad` or `pu_base_mva`), and the
same error in the measurement's own units in `estimated_error_measured` with `measured_unit`.

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

### Other files

The v1 case folder is also a writer's output: `gridsim convert` and the GDA case writer (`GdaModelWriter`) write it,
and `convert` writes a MATPOWER `.m` file for an output that ends in `.m`; both are specified in
[chapter 2](<0002 Data model and per-unit conventions.md>) and [chapter 9](<0009 GDA integration.md>). The licence file
holds one key line ([chapter 22](<0022 Licensing - key format and trust model.md>)); the desktop app keeps its settings as one
JSON file (`AppSettings`); the case editor's save gate stages the case in a temporary `gridsim-edit-<guid>` folder to run the loader
against it before anything is written ([manual chapter 33](<../manual/0033 The case editor.md>)); an unhandled exception writes a plain-text crash log
([manual chapter 30](<../manual/0030 Troubleshooting.md>)). The `summary.md` reports are Markdown with the header
lines indented by four spaces.

### The trip trajectory (`trip --export`)

`trip --export <file>` writes the post-trip frequency trajectory as a NESO-shaped CSV that `NesoFrequencyCsv.Read`
(`src/GridSim.Core/Frequency/NesoFrequencyCsv.cs`) reads back, so `replay` can play it. The header is `dtm,f`. Each row is
`yyyy-MM-dd HH:mm:ss.fff,Hz`: a timestamp counted from the fixed instant 2000-01-01 00:00:00 UTC at the integration step,
and the frequency in Hz with five decimals (`F5`). The reader skips a line that has no comma or does not parse, and
passes every row through the horizon guard; the year 2000 base keeps a trajectory from ever looking like current data.

```
$ head -n 4 results/exports-docs/trip.csv
dtm,f
2000-01-01 00:00:00.000,50.00000
2000-01-01 00:00:00.020,50.00000
2000-01-01 00:00:00.040,50.00000
[exit 0]
```

### `_QUARANTINE.json` and the quarantine store

`interpose --out-store <dir>` archives each measurement window as Parquet in a GDA-shaped store that is firewalled from
the GDA evidence lake. `QuarantineLiveStore` (`src/GridSim.Gda/Interpose/QuarantineLiveStore.cs`) writes it.

- **Layout:** `<dir>/Parquet/<source id>/year=YYYY/week=WW/part-<yyyyMMddTHHmmssfffZ>-<seq>.parquet`, with the ISO year
  and week of the window's `takenUtc` and `<seq>` a six-digit counter. A file is never overwritten; a taken name gets a
  `-dupN` suffix. An empty window writes nothing. The default source id is `rtu_live`.
- **Firewall:** the constructor throws `InvalidOperationException` when the target is at or below a directory that holds
  a `DataSchema.json`, the master-contract file of the GDA lake.
- **Marker:** the first window written also writes `_QUARANTINE.json` beside the `year=` folders, once per store session.

| Marker field | Value |
|---|---|
| `store` | The source id |
| `source_class` | `telemetry` |
| `evidential` | `false` |
| `reason` | The text stating that live SCADA telemetry is excluded by rule from the GDA public tables |
| `never_merge_into` | `GDA unified (MERGE-only) tables` |
| `horizon_hours` | `169` |
| `written_by` | `gridsim interpose` |

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

A Parquet row (`QuarantineRow`) has these columns, named in snake_case with the unit in the name:

| Column | Type | Meaning |
|---|---|---|
| `timestamp_utc` | datetime | The window's `takenUtc` |
| `source`, `source_class` | string | The source id; always `telemetry` |
| `cadence_min` | double | The window cadence in minutes |
| `vm_pu`, `va_deg`, `p_mw`, `q_mvar`, `i_pu`, `i_ang_deg` | double, nullable | Only the column that matches the measurement kind is filled: `VmPu`, `VaDeg`, `PInjMw` and `PFlowMw`, `QInjMvar` and `QFlowMvar`, `IMagPu`, `IAngDeg` |
| `bus` | int, nullable | The bus id of a bus kind |
| `from_bus`, `to_bus` | int, nullable | The endpoints of a branch kind |
| `sigma` | double | The measurement standard deviation |
| `provenance` | string | `<protocol>:<kind>@<location>` |
| `min_confidence` | double | Always 0: live telemetry carries no settlement confidence |
| `year`, `week` | int | The partition keys |

### `gridviz.json`

`gridsim export [<case>] [<out.json>] [--dir <folder>] [--frames N]` writes a scene for an external visualiser
(`VizExport`, `VizModels` in `src/GridSim.Core/Viz/`; default `results/gridviz.json`, 72 frames over one day). It carries
no schema id, is written compact and uses camelCase. Numbers are single-precision `float`. `name`, `baseMva`,
`buses[]` of `{id, name, kv, type, x, y}`, `branches[]` of `{from, to, name, transformer}` and `frames[]`. Each frame is
one daily-profile load level solved with `SteadyStateSolver`: `hour`, `loadGw`, `genGw`, `lossMw`, `freqHz` (the model's
nominal frequency), `converged`, and per-bus arrays `vm`, `vaDeg`, `pInjMw`, `qInjMvar` in `buses` order. A frame with
`converged` false is not a network state and must not be drawn as one.

```
$ head -c 520 results/exports-docs/gridviz.json
{"name":"GB reduced (10-zone)","baseMva":10000,"buses":[{"id":1,"name":"North Scotland 400kV","kv":400,"type":"Pv","x":0.45,"y":0.97},{"id":2,"name":"South Scotland 400kV","kv":400,"type":"Pv","x":0.42,"y":0.85},{"id":3,"name":"North England 400kV","kv":400,"type":"Pv","x":0.45,"y":0.72},{"id":4,"name":"Yorkshire \u0026 Humber 400kV","kv":400,"type":"Pv","x":0.57,"y":0.66},{"id":5,"name":"North Wales \u0026 Mersey 400kV","kv":400,"type":"Pv","x":0.36,"y":0.6},{"id":6,"name":"Midlands 400kV","kv":400,"type":"Slack",
[exit 0]
```

### The web usage log

`PresenceService` (`src/GridSim.Web/Sessions/PresenceService.cs`) appends one JSON line per completed viewer session of
at least 2 seconds to `usage.jsonl` (the path `Analytics:LogPath`, else `../gridsim-usage/usage.jsonl` beside the
deploy directory). A line is `{t, ev, id, case, raw, connected, dur_s, replay}`: the end instant, the event name
(`visit`), the viewer handle, the case label, whether the viewer was on the raw replay, the connect instant, the duration
in seconds and the replay instant being viewed (`null` when none). Writing is best-effort and never stops a session;
the file is opened for append at start-up, and a log that cannot be written is switched off with a warning on stderr
(`PresenceService.LogDisabledReason` holds the reason), as is the first append that fails later.

## The bridge wire protocol

The bridge is a WebSocket between GridSim and a GDA server. Chapter 9 owns the protocol: transport, roles, the query
operations, the sources and the horizon on both ends. This section lists the message types and the field blocks. Every
shape binds through `BridgeProtocol` (`src/GridSim.Core/IO/BridgeProtocol.cs`) and the client is `GridBridgeClient`
(`src/GridSim.Core/Net/GridBridgeClient.cs`). The server is in the GDA repository.

### Messages

Every message is a JSON text frame discriminated by a top-level `type`, found by `BridgeProtocol.PeekType`. The
options are `BridgeProtocol.JsonOptions`: names bind case-insensitively, `//` comments and trailing commas are skipped,
and named float literals are accepted.

| Direction | Message | Fields |
|---|---|---|
| client to server | `start` | `action:"start"`, `case`, `start` (an ISO instant, or `"first"` or `"earliest"` for a stored source), `fps`, `source`, and `loop` for a literal `start` |
| client to server | `stop` | `action:"stop"` |
| client to server | `set_speed` | `action:"set_speed"`, `fps` |
| client to server | `seek` | `action:"seek"`, `start`, `source` |
| client to server | `query` | `action:"query"`, `op`, `id`, `at`, `params` |
| server to client | `topology` | `type`, `case`, `model` |
| server to client | `state` | `type`, `t`, `result`, `scalars`, `overlays`, `operator`, `market`, `generationMix`, `weather`, `interconnectors`, `source`, `estimate` |
| server to client | `result` | `type`, `op`, `id`, `at`, `payload` |
| server to client | `error` | `type`, `message` |
| server to client | `info` | `type`, `message` |
| server to client | `done` | `type`, `framesSent`, optionally `source` |

`source` is `live`, `presolved`, `estimated`, `solves` or `raw`. The client sends `fps` and instants as ISO-8601 UTC. The
client acts on `topology`, `state`, `result` and `error`; `error` faults the client stream, and a well-formed message of
any other type, `info` and `done` included, is ignored, so a newer server stays compatible. A message that is not JSON or
has no string `type` is rejected, counted and reported through `MessageRejected`, and the stream continues; so is a
`state` with no `t` or with an unreadable one, and a `state` that arrives before any `topology`. A `state` whose `t` is
later than now minus 169 hours ends the stream.

```
$ sed -n '26,45p' tests/GridSim.Tests/BridgeProtocolTests.cs
    {
      "type": "topology",
      "model": {
        "name": "toy3",
        "baseMva": 100,
        "buses": [
          { "id": 1, "type": "Slack", "baseKv": 400, "name": "Slack400" },
          { "id": 2, "type": "Pv",    "baseKv": 400, "name": "Gen400" },
          { "id": 3, "type": "Pq",    "baseKv": 132, "pd": 80, "qd": 20, "name": "Load132" }
        ],
        "generators": [
          { "bus": 1, "pg": 0,   "name": "SlackGen" },
          { "bus": 2, "pg": 120, "vg": 1.02, "mBase": 200, "h": 4.0, "name": "UnitA", "type": "CCGT" }
        ],
        "branches": [
          { "from": 1, "to": 2, "r": 0.004, "x": 0.04, "b": 0.02, "rateAMva": 500, "name": "L1-2" },
          { "from": 2, "to": 3, "r": 0.006, "x": 0.08, "tap": 1.0, "rateAMva": 250, "name": "T2-3", "isTransformer": true }
        ]
      }
    }
[exit 0]
```

### `topology`

`model` is the `model` block of a solve export, read by `BridgeProtocol.BuildModel`, so every field of the
[solve export `model`](#model) is valid here. Reader defaults for an omitted field: `baseMva` 100, `frequencyHz` 50,
bus `vm` 1.0, `x` and `y` 0, bus `type` `Pq`, generator `vg` 1.0, `qmin` and `qmax` infinite, `pmax` infinite, `mBase`
100, branch `tap` 1.0, `inService` true, boundary `direction` 1. A present `baseMva` that is not finite and above 0 is
refused, and so is a bus `type` that is not a bus type name (see the bus table). Bus `x` and `y` are the 0..1 layout
coordinates; the solve export writes them.

### `state`

```
$ sed -n '50,78p' tests/GridSim.Tests/BridgeProtocolTests.cs
    {
      "type": "state",
      "t": "{{isoTimestamp}}",
      "result": {
        "totalLossMw": 1.5,
        "buses": [
          { "id": 1, "vm": 1.010, "vaDeg": 0.0,   "pInjMw": 40.0,  "qInjMvar": 12.0 },
          { "id": 2, "vm": 1.020, "vaDeg": -1.5,  "pInjMw": 120.0, "qInjMvar": 8.0 },
          { "id": 3, "vm": 0.978, "vaDeg": -6.2,  "pInjMw": -80.0, "qInjMvar": -20.0 }
        ],
        "lineFlows": [
          { "from": 1, "to": 2, "pFromMw": 40.0, "pToMw": -39.8, "loadingPercent": 8.0 },
          { "from": 2, "to": 3, "pFromMw": 80.2, "pToMw": -80.0, "loadingPercent": 32.0, "name": "T2-3", "ratingMva": 250 }
        ]
      },
      "scalars": {
        "freqHz": 49.98,
        "rocofHzPerS": -0.03,
        "genMw": 160.0,
        "demandMw": 80.0,
        "inertiaMwS": 800.0,
        "conditionIndex": 72.0,
        "rocofHeadroomHzS": 0.09
      },
      "overlays": {
        "stressByBranch": { "T2-3": 0.64 },
        "conditionByBus": { "3": 0.42 }
      }
    }
[exit 0]
```

The `t` member is an ISO-8601 instant, parsed by `BridgeProtocol.ParseUtc` with the invariant culture and normalised to
UTC. An empty `t` reads as `DateTime.MinValue` and is refused by the client.

| Block | Fields |
|---|---|
| `result` | `converged` (absent reads as `true`), `iterations`, `maxMismatch`, `totalLossMw`, `totalLossMvar` (absent read as 0), `buses[]` of `{id, vm, vaDeg, pInjMw, qInjMvar}`, `lineFlows[]` in the solve-export flow shape. A flow with an empty `name` or a `ratingMva` of 0 takes them from the cached topology: the k-th flow on a bus pair takes the k-th branch on that pair, and a flow with no branch left on its pair fails loud |
| `scalars` | All nullable: `freqHz`, `rocofHzPerS`, `genMw`, `demandMw`, `inertiaMwS`, `conditionIndex`, `rocofHeadroomHzS`. `conditionIndex` is GDA's system fragility index on its 0..100 scale; GridSim divides it by 100 into its own 0..1 condition index (0 healthy, 1 critical) for the HUD colour, and a value that is not a finite number in 0..100 reads as no reading |
| `overlays` | `stressByBranch` (branch name to fraction) and `conditionByBus` (bus id to fraction) |
| `operator` | All nullable: `largestLossMw`, `responseMw`, `reserveMw`, `deratedMarginMw`, `netInterconnectorMw`, `synchronousInertiaGvaS`, `outturnInertiaGvaS`, `marketInertiaGvaS`, `warning`, `warnings[]`, `balancingActions[]` of `{bmUnit, kind, levelMw, priceGbpMwh, fuelType, synchronous}` (`kind` is `BID` or `OFFER`) and `constraints[]` of `{name, flowMw, limitMw, costGbp, binding}` |
| `market` | All nullable: `imbalancePriceGbpMwh`, `systemSellPriceGbpMwh`, `marketIndexGbpMwh`, `netImbalanceVolumeMwh`, `dayAheadGbpMwh`, `bsuosGbpMwh`, `constraintCostGbp`, `carbonIntensityGco2PerKwh`, `demandForecastMw`, `balancingCostGbp`, `balancingVolumeMwh`, `balancingActionsN`, `bmCostGbp`, `bmVolumeMwh`, `bmUnitsN`, `actions[]` of `{t, unit, from, to, so}` (acceptances, newest first) and `interconnectors[]` of `{name, flowMw, capacityMw, priceGbpMwh}` with import positive |
| `generationMix` | The `raw` source only, per-fuel MW, all nullable: `gas`, `coal`, `nuclear`, `wind`, `windEmb`, `hydro`, `imports`, `biomass`, `other`, `solar`, `storage` |
| `weather` | The `raw` source only: `temperatureC`, `windSpeed100mKph`, `solarRadiationWm2` |
| `interconnectors` | The `raw` source only, link flows in MW: `ifa`, `ifa2`, `britNed`, `moyle`, `eastWest`, `nemo`, `nsl`, `elecLink`, `viking`, `greenlink`, `net` |
| `source` | `presolved`, `estimated` or `solves`; the `live` and `raw` paths send none |
| `estimate` | `estimated` and `solves` frames: `objectiveJ`, `chiThreshold`, `chiPassed`, `dof`, `measurementsN`, `removedN`, `iterations`, and the optional `irlsCapReached`. `chiPassed` is a boolean, so an absent key reads as `false`; the four integer keys must be JSON integers |

An interconnector whose absolute flow is at or below `0.001` MW is left out of the raw interconnector rows
(`BridgeProtocol.MinDisplayFlowMw`).

### `result`

`{type, op, id, at, payload}`. `id` echoes the request's correlation id on every reply, rejections included, and the
client matches replies to queries by it. `payload` (`QueryPayload`):

| Field | Meaning |
|---|---|
| `solveExport` | A whole `gridsim-solve-export/1` document, captured raw and read by `SolveImport.Read` |
| `verdict`, `bindingConstraint` | Strings; `ERROR` and `INFEASIBLE_HORIZON` are server verdicts (chapter 9) |
| `violations[]` | `{kind, element, value, limit, message}` |
| `bmUnit`, `busId`, `envelope`, `bessSoc`, `tapWear`, `consumers` | `op:"asset"`: the resolved ids and four lists of rows, each a map from column name to value |
| `corridors[]`, `provenance` | `op:"propagation"`: `{from, to, tau, km, vKmS, class}` per corridor and a provenance note. The client drops a `tau` that is negative, non-finite or above 10 s (`PropagationData.MaxPlausibleTauSeconds`) and counts the drops in the note |

```
$ sed -n '354,366p' tests/GridSim.Tests/BridgeProtocolTests.cs
        {
          "type": "result",
          "op": "propagation",
          "at": "2026-07-01T00:00:00Z",
          "payload": {
            "corridors": [
              { "from": 101, "to": 102, "tau": 0.0421, "km": 42.3, "vKmS": 1004.0, "class": "branch" },
              { "from": 102, "to": 110, "tau": 0.1240, "km": 130.5, "vKmS": 1052.0, "class": "gen_pair" },
              { "from": 110, "to": 999, "tau": null,   "km": null,  "vKmS": null,   "class": "branch" }
            ],
            "provenance": "distances + inertia REAL; wave_speed + tau MODELLED - unvalidated at scale"
          }
        }
[exit 0]
```

## The web API

`GridSim.Web` exposes a REST group and a raw WebSocket for external consumers (`src/GridSim.Web/Api/RestApi.cs`,
`SimWebSocket.cs`); the Blazor UI drives its session in process instead. Neither is mapped on a replay-only instance
(`GridSim:ReplayOnly`). Both serialise with web (camelCase) defaults and `NonFiniteDoubleConverter`, which writes a
non-finite `double` as `null`.

| Endpoint | Shape |
|---|---|
| `GET /api/cases` | `[{id, label, kind, replay}]` from `CaseCatalog` |
| `GET /api/topology?case=<id>` | `TopologyDto`; 400 when no case is named, 404 for an unknown case, 403 on a licence refusal. A replay run's topology is built once and kept until the run's files change |
| `POST /api/solve` with `{case}` | `{converged, status, iterations, maxMismatch, topology, state}`: one solve with the shared dense or sparse rule and 25 OLTC passes, projected to a `FrameEnvelope`. 200 only when it converged; a solve that did not converge answers 422 with `converged: false`, `status` (`MaxIterations` or `NonFinite`), an `error` text and the unconverged `topology` and `state` for inspection. 400 when no case is named or for a replay case, 404 for an unknown case, 403 on a licence refusal |
| `GET /api/realtime` | `{summary, latest, skippedFiles}` of the resident real-time estimator, where `latest` is `{sequence, stateTimeUtc, status, converged, objectiveJ, chiSquare}` or `null`; 404 when `Realtime:Enabled` is off |
| `GET /api/openapi.json` | A hand-written OpenAPI 3.0.3 description of the surface, carrying the release version |
| `/ws/sim` | Control frames in; `topology`, `state` and `error` frames out |

`TopologyDto` (`type:"topology"`) is `caseId`, `caseLabel`, `baseMva`, `busCount`, `replay`, `frameCount`, `nodes[]`
(`id`, `x`, `y`, `baseKv`, `name`, `busType`), `edges[]` (`index`, `from`, `to`, `isTransformer`, `name`, `rateAMva`),
`fleet[]` (`bus`, `mw`, `fuel`, `fuelColor`, `synchronous`, `name`, `powerFlowOnly`), `boundaries[]` (`name`, `limitMw`,
`edgeIndices`) and `isGeolocated`. `FrameEnvelope` (`type:"state"`) is `tick`, `hour`, `clock`, `label`, `headline`
(`frequencyHz`, `rocofHzPerS`, `generationMw`, `demandMw`, `lossMw`, `inertiaGvaS`, `avgVoltagePu`, `converged`,
`minVoltagePu`, `maxVoltagePu`), `nodes[]` (`id`, `vm`, `vaDeg`, `pInjMw`, `qInjMvar`), `edges[]` (`index`, `pFromMw`,
`qFromMvar`, `pLossMw`, `loadingFrac`, joined to the topology by `index`), `boundaries[]` (`name`, `flowMw`, `limitMw`,
`loadingPercent`, `overloaded`), then one nullable sub-block per dashboard tab, all defined in
`src/GridSim.Web/Projection/Dtos.cs`.

`/ws/sim` control frames are `{action, case, source, fps, value, solver, assetGraph, waveField}`. `action` is `start`,
`play`, `pause`, `step`, `seek` (`value` is a fraction), `speed` (`value` is frames per second) or `trip` (`value` is
MW; refused with an error frame on a replay or bridge session). The case id decides the feed (a `run-*` id is a replay,
`bridge:<case>[@<source>]` or `live-bridge` the bridge, any other case live); `source` is optional and, when given, must
be `live`, `replay` or `bridge` and match that feed, or the start is refused with an error frame. An unknown bridge
`@source` is refused too. Every frame the server cannot act on is answered with an error frame: a binary frame, text
that is not a JSON object, a frame with no or an unknown `action`, `start` with no case, any other action before a
successful `start`, and `seek`, `speed` or `trip` without a numeric `value`. On `start`, `solver` picks a live case's engine: `auto`
(the default), `dense`, `sparse` or `sweep`; another name, or any solver but `auto` for a replay or bridge case, is
refused with an error frame. `assetGraph: true` and `waveField: true` ask for the per-frame `assetGraph` block and the
experimental `experimental.waveField`, which are otherwise `null` on this socket because they are the largest blocks.
`start` queues the topology ahead of the first `state`, and every `start` drops the frames still queued from the replaced
session. State frames go through a 256-frame channel that drops the oldest frame under back-pressure. A failed dispatch
returns `{type:"error", message}`. A control frame larger than 16 KiB closes the socket with `MessageTooBig`, and a client
close is answered with the server's own close frame carrying the client's status code. On a bridge session the `state`
frame also carries `mapOverlays` (`stressByBranch`, `conditionByBus`, finite entries only) and `mapHud`
(`conditionIndex` on GridSim's 0..1 scale, `rocofHeadroomHzPerS`); both are `null` on live and replay sessions.

## Pointers

### RTU wire formats

The telemetry codecs are in `src/GridSim.Core/IO/Telemetry/`: IEC 60870-5-104 (`Iec104Frame` for the APCI and
`Iec104Asdu` for the ASDU) and Modbus TCP (`ModbusFrame`) are the two protocols the `interpose` verb serves over TCP;
DNP3 (`Dnp3Frame`) and IEC 61850 GOOSE (`Iec61850GooseFrame`) are codecs that no transport selects.
[Chapter 20](<0020 The RTU and SCADA interposer.md>) specifies the framing, the ASDU and register layouts, the quality
fold and the windowing. The documents the interposer reads and writes are the point map and the measurement windows
above.

### C37.118 frames

IEEE C37.118.2 data and configuration frames are decoded by `src/GridSim.Core/Synchrophasor/` (`C37118Decoder`,
`C37118ConfigParser`, `PhasorDataConcentrator`, `PmuMeasurementSetMapper`). The frame layout, the format-word bits, the
CRC, the concentrator and the mapper are in [chapter 33](<0033 C37.118 synchrophasor ingest.md>). The decoded
capture is exposed by the experimental `pmu-ingest` verb.

### CIM

`src/GridSim.Core/IO/Cim/` imports and exports the CIM equipment and topology subset as one RDF/XML document
(`CimImporter.Parse`, `CimExporter.Export`, `CimGridModelBridge.ToGridModel`). It has no CLI verb. The importer merges
every description of one mRID inside the single document it is given, so a merged EQ plus TP document imports and
separate profile files are not read as a set. The format and the mapping are in
[chapter 32](<0032 CIM and CGMES import and export.md>).

### v2 documents

The v2 family (`gridsim-case/2`, `gridsim-network/2`, `gridsim-library/2`, `gridsim-kind/2`) is
[chapter 28](<0028 The v2 JSON schema.md>). A `gridsim-case/2` manifest loads as a case through `V2CaseLoader`, and
`gridsim validate` checks any v2 document against `schemas/v2`. Every other loader in this chapter reads v1.

### The GDA lake products

The estimation pipeline's Parquet products are written by scripts in the GDA repository, outside this one; GridSim reads
none of them directly, and the bridge streams their rows. [Chapter 9](<0009 GDA integration.md>) names the
builders. All are weekly hive partitions `year=YYYY/week=WW/`, written to a temporary file and moved into place, with
merge-on-write deduplication. The column definitions below are the GDA contract that the bridge's `estimate` block and
the estimate export's fields mirror.

**`Derived/estimated_state/<case>/.../estimated_state_YYYYMMDD.parquet`**: one row per settlement period. The GDA
builder also writes `irls_cap_reached`, `fdia_verdict` and `chi_test` (`passed`, `failed` or `not-run`).

| Column | Type | Content |
|---|---|---|
| `datetime_utc` | datetime | Period instant (UTC) |
| `case` | string | `gb-spine` or `gb-full` |
| `status` | string | `EstimationStatus` name |
| `converged` | bool | Only a `Converged` row carries a result block |
| `iterations` | int | Gauss-Newton iterations |
| `max_state_update` | double | Final maximum state update |
| `objective_j` | double | Weighted least-squares objective |
| `chi_threshold` | double | The 95 % chi-square threshold; null when NaN (no redundancy) |
| `chi_passed` | bool | True only when the test ran and J is at or below the threshold |
| `dof` | int | Degrees of freedom |
| `measurements_n`, `removed_n` | int | Measurements in the set; bad-data removals |
| `removed_json` | string | `[{kind, bus, rn}]`; `[]` when none |
| `vm_min`, `vm_max` | double | Estimated voltage envelope |
| `boundary_json` | string | `[{name, flowMw, limitMw}]`: transfer boundary flows at the estimated state; null without `boundaries.json` |
| `result_json` | string | Slim result block `{buses:[{id, vm, vaDeg, pInjMw, qInjMvar}], lineFlows:[...]}`; null when not converged |

A per-case `topology.json` (a solve-export `model` block plus the merged `boundaries`) sits beside the partitions, and the
bridge's `estimated` source streams both.

**`state_divergence`**: one row per period both lakes cover: `datetime_utc`, `case`, `buses_compared`, `flows_compared`,
`dvm_max_pu`, `dvm_mean_pu`, `dva_max_deg`, `dflow_max_mw`, `worst_vm_bus`, `worst_va_bus`, `worst_flow_from`,
`worst_flow_to`, `est_objective_j`, `est_chi_passed`, `est_removed_n`. Flows join on `(from, to, name)` and buses on id.

**`estimation_closure`**: one row per backfilled day: `date`, `case`, `periods`, `converged_n`, `chi_passed_n`,
`removed_total`, `est_gen_gwh`, `est_load_gwh`, `nd_gwh`, `load_vs_nd_pct` and `outlier_league`, the top repeat-removed
measurements as `"kind@busN xCount; ..."`.

**`gsp_generation_measurements`**: one row per settlement period and GSP, plus a per-period `gsp = NULL` unallocated
bucket: `datetime_utc`, `settlement_date`, `settlement_period`, `gsp`, `generation_mw`, `sigma_mw`, `units_n`,
`min_confidence`, `provenance` (`observed` for B1610 metering, `pn` for the Physical Notification fallback).
**`gsp_group_demand`**: one row per period and PES group: `datetime_utc`, `settlement_date`, `settlement_period`,
`gsp_group`, `demand_mw`, `sigma_mw`, `units_n`, `provenance`. **`gsp_ic_flows`**: one row per period and landing GSP:
`datetime_utc`, `settlement_date`, `settlement_period`, `gsp`, `ic_mw` (net import, positive), `units_n`.

## Parameters and defaults

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| Forward-inference horizon | 169 | h | A dated input later than now minus this is refused | `ForwardInferenceGuard.HorizonHours` |
| Jacobian capture gate | 300 | buses | A dense solve above this omits the Jacobian unless forced | `SolveTrace.JacobianMaxN` |
| Export dense and sparse crossover | 200 | buses | Dense up to and including this, sparse above (radial cases use the sweep) | `SolverSelector.DenseSparseCrossoverBuses` |
| Export iteration cap | 100 | iterations | The cap of the traced solve in `--enableExport` | `RunExport` in `Program.cs` |
| Measurement base-MVA tolerance | `1e-9` | MVA | The set's `baseMva` must match the model's within this | `MeasurementSet.BaseMvaMatchToleranceMva` |
| Rank-proof limit | 2500 | states | Above this the sparse backend reports `rank` -1; the dense backend has no cap | `DenseEstimationLimits.RankProofMaxState` |
| Hard-gate laws | 1, 2, 3, 6, 8 | law numbers | The laws whose status decides `physics.gatePass` | `PhysicsCheckRegistry.HardGateLaws` |
| Physics law tolerance | `BaseMva x 1e-8` | MW, MVA | Residual below which a law holds (`1e-06` at 100 MVA) | `PhysicsLaws.DefaultTolMva`, `LawResult.Tolerance` |
| ZIP defaults | `0, 0, 1, 0, 0, 1` | | `ap, bp, cp, aq, bq, cq`: constant power | `ZipLoad` |
| OLTC defaults | 1.0, 0.9, 1.1, 0.0125, 0.01 | pu | `targetVpu`, `minTap`, `maxTap`, `stepPu`, `deadbandPu` | `BridgeProtocol.OltcDto` |
| Point-map sigma | 0.01 | measurement unit | Default standard deviation of a binding | `RtuPointBinding.Sigma` |
| Audit genesis | `GENESIS` | | The `prevHash` of the first line | `AuditLog.Genesis` |
| Bridge query timeout | 60 | s | A query with no reply faults with `TimeoutException` | `GridBridgeClient.QueryTimeout` |
| Propagation delay cap | 10 | s | A corridor `tau` above this is dropped | `PropagationData.MaxPlausibleTauSeconds` |
| Interconnector display cut | 0.001 | MW | A link at or below this absolute flow is left out | `BridgeProtocol.MinDisplayFlowMw` |
| `/ws/sim` control-frame limit | 16 | KiB | A larger frame closes the socket with `MessageTooBig` | `maxControlBytes` in `SimWebSocket.cs` |
| `/ws/sim` state queue | 256 | frames | Bounded channel, oldest frame dropped first | `SimOutbound` |
| REST solve OLTC passes | 25 | passes | The pass cap of `POST /api/solve` | `RestApi.RestOltcPassCap` |
| Visualiser frames | 72 | frames | Default `--frames` of `export` | `VizExport.Build` |
| Usage-log minimum session | 2 | s | Shorter sessions are not logged | `PresenceService.LogLeave` |

## Invariants

Each invariant names the test that pins it. The tests are in `tests/GridSim.Tests`.

| Invariant | Test |
|---|---|
| A solve export is deterministic: the same solve writes the same bytes apart from `meta` | `SolveExportTests.Export_is_deterministic` |
| Attaching the recorder does not change the solution, dense or sparse | `SolveExportTests.Attaching_a_trace_does_not_change_the_solution` |
| The export holds every section and parses back; the Jacobian is gated by size and forceable; a sweep records no `ybus` | `SolveExportTests.Export_json_has_all_sections_and_roundtrips`, `Jacobian_is_gated_by_size_and_forceable`, `Bfs_trace_has_no_ybus_but_records_sweeps` |
| Every carried model field round-trips exactly | `SolveExportRoundTripTests.Every_carried_network_field_round_trips_exactly` |
| A re-imported model solves to the bit-identical state | `SolveExportRoundTripTests.The_reimported_model_resolves_to_the_bit_identical_state` |
| An opened branch stays out of service through export and import | `SolveExportRoundTripTests.An_opened_branch_stays_out_of_service_through_export_and_import`, `The_export_writes_the_service_state_explicitly` |
| A document without the optional fields reads with the model defaults; an unknown winding name fails loud | `SolveExportRoundTripTests.An_older_document_without_the_new_fields_reads_with_model_defaults`, `An_unknown_winding_name_fails_loud` |
| Parallel circuits keep their own name and rating; a flow with no branch left on its pair fails loud | `SolveExportRoundTripTests.Parallel_circuits_each_get_their_own_name_and_rating`, `More_unnamed_flows_than_circuits_on_a_pair_fails_loud` |
| `SolveImport` rejects a document that is not a solve export and rebuilds the model and result | `SolveImportTests.Rejects_a_non_solve_export_document`, `Round_trips_the_model`, `Round_trips_the_solved_result` |
| The `physics` block carries the gate verdict and the full field set of every law, in order | `PhysicsExportContractTests.Physics_block_is_present_with_the_gate_verdict`, `Every_law_carries_the_full_field_set_in_order` |
| The estimate export carries the schema, meta, model, measurements, estimate, residuals, observability and removals | `EstimateExportContractTests.Export_carries_the_versioned_schema_and_meta`, `Model_block_has_everything_a_sidecar_needs_to_rebuild_the_network`, `Estimate_block_carries_state_fit_and_verdicts`, `Residuals_observability_and_removals_are_present` |
| NaN normalised residuals serialise without throwing | `EstimateExportContractTests.Nan_normalized_residuals_serialize_without_throwing` |
| `chiSquarePassed` is an explicit `null` when the test did not run and never `true` for it | `N208ChiSquareNotRunTests.A_test_that_did_not_run_exports_chiSquarePassed_as_an_explicit_null`, `A_test_that_ran_exports_exactly_its_verdict_as_before`, `The_wire_value_follows_the_outcome_and_never_says_true_for_not_run` |
| `irlsCapReached` is additive and changes no other member of `estimate` | `N064AuditShowAndIrlsExportTests.The_new_field_changes_no_other_value_in_the_export` |
| A measurement row without `kind` or `value` is refused naming every row; a complete set loads unchanged | `MeasurementSetRequiredFieldsTests.A_missing_kind_or_value_is_refused_naming_each_entry`, `A_complete_set_loads_unchanged` |
| The frequency export's schema tag is locked, a forming-off export is byte-identical, and services and an offset start are exported exactly | `FrequencyExportContractTests.Schema_tag_is_locked`, `Forming_off_export_is_byte_identical_regardless_of_unused_forming_params`, `FrequencyExportServicesTests.Recorded_services_and_offset_start_are_exported_exactly`, `A_service_free_nominal_start_export_has_neither_key` |
| The frequency export is deterministic and carries the split inertia budget | `FrequencyExportTests.Export_is_deterministic`, `Export_carries_schema_and_split_inertia_budget` |
| An un-formable H=0 cell is `null` for RoCoF and nadir | `H0StudyExportContractTests.Un_formable_cell_sentinels_rocof_and_nadir_as_null` |
| The built-in catalogue round-trips and a future-dated event is rejected on load | `EventCatalogTests.Builtins_round_trip_through_json`, `A_future_dated_event_is_rejected_on_load` |
| The evidence report carries its schema tag and withholds the counterfactual when the baseline fails | `EvidenceReportTests.Report_serialises_with_the_schema_tag`, `The_counterfactual_is_withheld_when_the_baseline_fails` |
| A harmonic set round-trips, refuses a wrong schema and a fresh timestamp | `HarmonicSourceSetTests.Round_trips_through_json`, `Rejects_a_wrong_schema_version`, `A_fresh_timestamp_is_refused_but_a_historical_one_is_accepted` |
| The harmonic, impedance-stability and small-signal exports keep the contract their sidecars read | `HarmonicExportContractTests.Export_locks_the_harmonic_export_contract`, `ImpedanceExportContractTests.Export_locks_the_impedance_stability_contract`, `SmallSignalExportContractTests.The_schema_tag_is_what_the_sidecar_expects` |
| The solve export keeps the contract `crossval.py` reads | `CrossvalContractTests.The_schema_tag_is_what_the_sidecar_expects`, `The_model_inputs_the_sidecar_reads_are_all_present` |
| A CLI result number round-trips to the same bits; non-finite values are named strings; files are invariant-culture | `CliResultWriterTests.Number_text_round_trips_every_tricky_double_to_the_same_bits`, `Json_numbers_round_trip_exactly_and_non_finite_values_are_strings`, `Files_are_invariant_culture_whatever_the_thread_culture` |
| A failing verdict is written with its exit code, and a run that stopped early writes no file | `CliResultFilesProcessTests.A_verdict_that_fails_is_still_written_with_its_exit_code`, `A_run_that_stops_before_its_result_writes_no_file_and_says_so` |
| A cost file overlays the case and round-trips; a bad one is refused naming the entry | `GeneratorCostsAndPricesTests.The_cost_file_overlays_the_case_by_index_and_by_name_and_round_trips`, `A_bad_cost_file_is_refused_naming_the_entry` |
| A bad switched-shunt file is refused, and a good one reads | `DistributedSlackAndSwitchedShuntTests.A_bad_switched_shunt_file_is_refused`, `A_good_switched_shunt_file_reads_and_bad_banks_are_refused` |
| A point-map address bound twice is refused; unique addresses resolve | `RtuPointMapDuplicateAddressTests.Two_bindings_for_one_address_are_refused_naming_the_address`, `Unique_addresses_still_load_and_resolve` |
| The quarantine store writes snake_case Parquet and the marker, and refuses to open inside the GDA lake | `QuarantineLiveStoreTests.Writes_gda_shaped_partition_with_snake_case_columns_and_sidecar`, `Refuses_to_open_inside_the_gda_public_lake` |
| An audit line is one compact canonical object; any edit, deletion, insertion, reordering, byte-order mark or CR LF is detected; only an anchor catches tail removal | `AuditLogFileTests.Line_format_is_one_compact_canonical_json_object_per_line`, `Editing_an_entry_is_detected_at_that_entry`, `A_hand_reformatted_line_is_rejected_even_though_its_values_are_unchanged`, `Removing_whole_entries_from_the_end_is_caught_by_the_anchor_and_only_by_the_anchor` |
| A bridge topology rebuilds the network; a state maps buses, flows and scalars; a query result runs its solve export through `SolveImport` | `BridgeProtocolTests.Topology_message_rebuilds_the_network`, `State_maps_line_flows_and_backfills_name_and_rating_from_topology`, `Query_result_runs_the_payload_solveExport_through_SolveImport` |
| A bridge state later than now minus 169 hours is refused before a frame is built; an unknown message type is a no-op; a malformed message is rejected and the stream continues | `BridgeProtocolTests.A_state_with_a_future_timestamp_is_rejected_before_a_frame_is_built`, `Dispatch_routes_by_type_and_ignores_unknown_messages`, `BridgeClientRobustnessTests.A_malformed_message_is_rejected_and_counted_and_the_stream_carries_on` |
| The web API lists and solves case 9 and is unmapped on a replay-only instance; `/ws/sim` sends the asset graph only when asked and completes the close handshake | `WebHostTests.Api_lists_cases_and_solves_case9_when_not_replay_only`, `Replay_only_maps_neither_the_api_nor_the_sim_websocket`, `Sim_websocket_sends_the_asset_graph_only_when_asked`, `Sim_websocket_completes_the_close_handshake_when_the_client_closes` |
| The visualiser scene is camelCase JSON that round-trips | `VizExportTests.Json_is_camelcase_and_round_trips` |
| A malformed three-phase feeder file is refused naming its JSON path | `MultiPhaseJsonLoaderTests.Malformed_input_is_refused_naming_the_path` |

## Worked example

One export of a bundled case shows most of the rules. The reduced GB case in `data/uk` (20 buses, base 10,000 MVA) is
solved and exported:

```
$ gridsim --dir data/uk --once --enableExport=results/formats/uk-solve.json
[export] dense-newton: converged=True iters=3 maxMis=2.48E-010  ->  results\formats\uk-solve.json
[exit 0]
```

In the document the `model` block carries 20 buses, 29 generators, 24 branches, 10 OLTCs and 4 boundaries, and
`frequencyHz` is 50. The `solve` block names the engine, `dense-newton`, with `n` 20, `converged` true and
`iterations` 3. `solve.maxMismatch` is `2.4818128108172743E-10` pu. `ybus` has 68 non-zero entries and `busSpec` 20
rows. There is one pass. It has 19 `pvpqIndices` (every non-slack bus carries a P equation) and 10 `pqIndices`, so each
`mismatch` and `correction` vector has 29 entries and each `jacobian` is 29 by 29. The three iterations start from the
mismatches `0.8228314068936309`, `0.03524775712645303` and `6.899927841866327e-05` pu; the converged
`2.4818128108172743E-10` is the `solve.maxMismatch`, not a fourth iteration entry. The Jacobian is present because 20 is
at most 300.

In `result`, `totalLossMw` is `53.68209269980389` MW. The 20 bus `pInjMw` values sum to `53.682092699805025` MW, and the
24 branch `pLossMw` values to `53.68209269980008` MW; the three agree within 5e-12 MW, the difference being the order of
summation. `totalLossMvar` is `-5974.150777658105` MVAr, the exact sum of the bus `qInjMvar`, and negative because the
network's line charging exceeds its series loss. The first branch, `L1-2 N-S Scotland`, has `pFromMw`
`1549.9397675137548` and `pToMw` `-1547.6996070473188`, so `pLossMw` is their sum `2.2401604664360093` MW. Its
`loadingPercent` is the larger end's apparent power, `max(1619.1010347231984, 1555.7839682263805)` MVA, over the 4000 MVA
`ratingMva`: `40.47752586807996`.

In the `physics` block law 6 (Kirchhoff's current law) has a residual of `1.0637e-06` MW and law 8 (complex power)
`2.4818e-06` MVA. The tolerance is `BaseMva x 1e-8` = `1e-04` on this 10,000 MVA base, the scale of the solver's own
stopping rule (its mismatch `2.48e-10` pu is `2.48e-06` MW here), so both laws hold and `gatePass` is true.

The estimate export shows the non-finite rules. Estimating `case9` from a thin measurement set leaves the system
unobservable:

```
$ gridsim estimate case9 --measurements docs/_captures/estimation/case9-telemetry-thin.json --out results/formats/estimate-thin

=== WLS state estimate: case9 ===
  status: Unobservable   iterations: 0   max |dx|: Infinity
  measurements: 13   states: 17   redundancy (dof): -4
  observability: UNOBSERVABLE
    free state directions: Vm@bus 5, Vm@bus 7, Vm@bus 8, Vm@bus 9

  -> results\formats\estimate-thin
[exit 3]
```

The document is complete. `estimate.maxStateUpdate` is the string `"Infinity"`, `chiSquareThreshold` is `"NaN"`,
`chiSquarePassed` is `null` and `chiSquareTest` is `0`; every `normalizedResidual` is `"NaN"`; and `observability` lists
buses 5, 7, 8 and 9 as the free directions with `rank` 13 of `stateSize` 17. A reader that parses the strings with
`double.Parse` recovers the values, and one that stores `chiSquarePassed` stores "did not run".

## Source map

| File | Type | Responsibility |
|---|---|---|
| `src/GridSim.Core/IO/SolveExport.cs` | `SolveExport`, `SolveExportMeta` | Writes `gridsim-solve-export/1` and the `physics` block |
| `src/GridSim.Core/IO/SolveImport.cs` | `SolveImport`, `SolveSnapshot` | Reads a solve export into a model and a result |
| `src/GridSim.Core/IO/BridgeProtocol.cs` | `BridgeProtocol` and its message, block and DTO classes | Bridge wire shapes; `BuildModel`, the one model reader; `PeekType`; `ParseUtc` |
| `src/GridSim.Core/Net/GridBridgeClient.cs` | `GridBridgeClient` | Bridge client: control messages, dispatch, horizon guard, query correlation |
| `src/GridSim.Core/Diagnostics/SolveTrace.cs` | `SolveTrace`, `YBusEntry`, `BusSpecRow`, `SolvePass`, `IterationTrace`, `QLimitEvent` | The solver recorder serialised in `solve` |
| `src/GridSim.Core/IO/EstimateExport.cs` | `EstimateExport` | Writes `gridsim-estimate-export/1` |
| `src/GridSim.Core/IO/EstimateExportResiduals.cs` | `EstimateExportResiduals` | Reads the residual table of an estimate export |
| `src/GridSim.Core/Estimation/MeasurementSet.cs`, `Measurement.cs` | `MeasurementSet`, `Measurement`, `MeasurementKind` | Reads and writes `gridsim-measurements/1`; validation |
| `src/GridSim.Core/Events/EventCatalog.cs`, `GridEvent.cs` | `EventCatalog`, `GridEvent`, `PowerStep`, `ResponseService`, `RecordedOutcome` | Reads and writes `gridsim-event-catalog/1` |
| `src/GridSim.Core/Evidence/EvidenceReport.cs`, `AvoidedCost.cs` | `EvidenceReport`, `EvidenceColumn`, `FleetComparison`, `AvoidedCostResult` | Writes `gridsim-evidence/1` |
| `src/GridSim.Core/IO/FrequencyExport.cs` | `FrequencyExport`, `FrequencyExportMeta` | Writes `gridsim-frequency-export/1` |
| `src/GridSim.Core/IO/H0StudyExport.cs` | `H0StudyExport`, `H0StudyMeta` | Writes `gridsim-h0-study/1` |
| `src/GridSim.Core/IO/HarmonicSourceSet.cs` | `HarmonicSourceSet` | Reads and writes `gridsim-harmonics/1` |
| `src/GridSim.Core/IO/HarmonicExport.cs` | `HarmonicExport` | Writes `gridsim-harmonic-export/1` |
| `src/GridSim.Core/IO/ImpedanceStabilityExport.cs` | `ImpedanceStabilityExport` | Writes `gridsim-impedance-stability/1` |
| `src/GridSim.Core/IO/SmallSignalExport.cs` | `SmallSignalExport` | Writes `gridsim-smallsignal/1` |
| `src/GridSim.Cli/Program.cs` | `RunStability`, the `estimate` handler, `RunExport` | The classical export, `fdia-screen.json`, `residuals.csv` and the other CSVs, the solve-export call |
| `src/GridSim.Cli/CliResultWriter.cs` | `CliResult`, `CliResultWriter`, `CliOutput` | `gridsim-cli-result/1` and its CSV files |
| `src/GridSim.Cli/RunInfo.cs` | `RunInfo` | `run-metadata.json` and the `#` header lines |
| `src/GridSim.Core/IO/Json/GeneratorCostFile.cs` | `GeneratorCostFile` | Reads and writes `gridsim-costs/1` |
| `src/GridSim.Core/PowerFlow/SwitchedShuntPowerFlow.cs` | `SwitchedShuntPowerFlow.ReadFile` | Reads `gridsim-switched-shunts/1` |
| `src/GridSim.Core/Interpose/RtuPointMap.cs` | `RtuPointMap`, `RtuPointBinding` | Reads and writes `gridsim-rtu-pointmap/1` |
| `src/GridSim.Gda/Interpose/QuarantineLiveStore.cs` | `QuarantineLiveStore`, `QuarantineRow` | The quarantine Parquet store and `_QUARANTINE.json` |
| `src/GridSim.Core/Diagnostics/AuditLogFile.cs`, `AuditLogWriter.cs`, `AuditLog.cs` | `AuditLogFile`, `AuditLogWriter`, `AuditLog` | `gridsim-audit/1` encoding, verification and the hash |
| `src/GridSim.Core/Distribution/MultiPhaseJsonLoader.cs` | `MultiPhaseJsonLoader` | Reads the `gridsim-dist3ph` documents |
| `src/GridSim.Core/Frequency/NesoFrequencyCsv.cs` | `NesoFrequencyCsv` | Reads the `dtm,f` CSV the trip export writes |
| `src/GridSim.Core/Scenarios/ScenarioSpec.cs` | `DeviceConfig` | Reads the device file |
| `src/GridSim.Core/Gda/GdaTimeSeries.cs` | `GdaTimeSeries` | Reads and writes the demand series |
| `src/GridSim.Core/Viz/VizExport.cs`, `VizModels.cs` | `VizExport`, `VizScene` | Writes `gridviz.json` |
| `src/GridSim.Web/Api/RestApi.cs`, `SimWebSocket.cs`, `SimOutbound.cs`, `NonFiniteDoubleConverter.cs` | `RestApi`, `SimWebSocket` | The REST and WebSocket surface |
| `src/GridSim.Web/Projection/Dtos.cs` | `TopologyDto`, `FrameEnvelope` | The web frame shapes |
| `src/GridSim.Web/Sessions/PresenceService.cs` | `PresenceService` | Writes `usage.jsonl` |
| `src/GridSim.Core/IO/Telemetry/` | `Iec104Frame`, `Iec104Asdu`, `ModbusFrame`, `Dnp3Frame`, `Iec61850GooseFrame` | RTU wire codecs (chapter 20) |
| `src/GridSim.Core/Synchrophasor/` | `C37118Decoder`, `C37118ConfigParser` | C37.118 frames (chapter 33) |
| `src/GridSim.Core/IO/Cim/` | `CimImporter`, `CimExporter` | CIM import and export (chapter 32) |
| `tools/crossval/` | `crossval*.py`, `physics_audit.py`, `se_residual_check.py`, `residual_check.py` | The independent readers of the exports |

## Limits

- The solve export does not carry the asset-dynamics model or the protection bundle, so a re-imported export has every
  bus, generator and branch field but not the dynamics classes' parameters or the placed protective devices.
- The estimate export's `model` omits `frequencyHz`, `zip`, the short-circuit data, `oltcs` and `boundaries`, so it
  rebuilds the network for the estimation sidecars and not for a full solve replay. It lists the whole generator fleet
  with `inService` and `pmin`, as the solve export does.
- `BridgeProtocol.BuildModel` reads an absent `baseMva` as 100 and an absent bus `type` as `Pq`; a present value that is
  not usable is refused.
- The classical export and the small-signal export have no `meta` block, so they carry no timing.
- `impedance-stability` writes the converter admittance twice, under `converterAdmittance` and the deprecated
  `converterImpedance`, until the next schema version.
- The bridge server and the GDA lake builders are in the GDA repository, so this repository has no code that writes the
  lake tables or the server messages; their columns and fields are the contract the client reads.
- A removal of entries from the end of an audit log is invisible without an external anchor.

## See also

- [Manual chapter 31, Exports and formats](<../manual/0031 Exports and formats.md>) - the user-level guide to the same files.
- [Chapter 9, GDA integration](<0009 GDA integration.md>) - the bridge protocol, query operations, sources and the lake.
- [Chapter 6, State estimation](<0006 State estimation.md>) - what the estimate, residual and observability blocks mean.
- [Chapter 7, The evidence engine](<0007 The evidence engine.md>) - how the evidence dossier is computed and gated.
- [Chapter 8, Provenance and invariants](<0008 Provenance and invariants.md>) - the `ProvenanceTag` taxonomy and the horizon.
- [Chapter 11, Validation](<0011 Validation.md>) - the sidecars that read the export contracts.
- [Chapter 18, Real-time, distribution and dynamic estimation](<0018 Real-time, distribution and dynamic estimation.md>) - the three-phase feeder files.
- [Chapter 32, CIM/CGMES import and export](<0032 CIM and CGMES import and export.md>) and [chapter 33, C37.118 synchrophasor ingest](<0033 C37.118 synchrophasor ingest.md>) - the interchange and frame formats that have their own chapter.
- [Chapter 19, CLI reference](<../manual/0019 CLI reference.md>) - every verb, its flags and output paths.
- [Chapter 20, The RTU/SCADA interposer](<0020 The RTU and SCADA interposer.md>) - the RTU frames, the point map and the quarantine store.
- [Chapter 28, The v2 schema](<0028 The v2 JSON schema.md>) - the case, network, library and kind documents.
- [Chapter 31, Audit log and diagnostics](<0031 Audit log and diagnostics.md>) - the audit hash and verification.
