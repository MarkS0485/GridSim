# CIM / IEC 61970 (CGMES) network + measurement exchange

> **Status (verified 1.95.276):** Partly implemented as a Core API with no CLI verb: an RDF/XML importer that takes one document or one per profile (EQ, TP, SSH, SV, DY, boundary), with a node-breaker topology processor, loads of every CGMES class, machines and injections with their reactive limits, voltage control and type, shunts, static var and series compensators, every concrete tap changer, and the SV tap and shunt positions by option; a bridge to `GridModel` with a slack rule and de-energised islands; a controlled solve for remote voltage control, SVCs and regulating shunts; and an exporter. The `GridModel` export, solved-state (SV voltage) import and export, measurement (Meas) mapping to `MeasurementSet`, operational limits, the `md:FullModel` header and the `ForwardInferenceGuard` gating of dated inputs are not implemented.
> **Implemented in:** `src/GridSim.Core/IO/Cim/` (`CimImporter.Parse` and its `.Topology`, `.Equipment`, `.Controls` parts, `CimNetwork`, `CimImportOptions`; `CimGridModelBridge.ToGridModel` and `Bridge`; `CimControlledPowerFlow`; `CimExporter.Export`). The behaviour is specified in technical [ch. 32, CIM/CGMES import and export](<../0032 CIM and CGMES import and export.md>).
> **Design-to-code name map:**
>
> | Design name | Implemented as |
> |---|---|
> | namespace `GridSim.Core.IO.Cgmes` | `GridSim.Core.IO.Cim` |
> | `CgmesImporter` (`ImportFiles`, `ImportWithDiagnostics`) | `CimImporter.Parse(string rdfXml)` or `Parse(IEnumerable<string> documents)`; merged by mRID across the documents; diagnostics in `CimNetwork.Notes` |
> | `CgmesImportResult` | `CimNetwork` (no `GridModel` or `MeasurementSet` member) |
> | `CgmesExporter` | `CimExporter.Export(CimNetwork)`, returns one RDF/XML string |
> | `CgmesNetworkBuilder`, `CgmesUnits`, the `GridModel` build | `CimGridModelBridge.ToGridModel` (ohm to per-unit through `BaseVoltage`) |
> | `CgmesProfile`, `CgmesProfileSchema`, `CgmesModelHeader`, `CgmesDataset` | not implemented; profiles are passed as separate documents and merged by mRID |
> | `CimRdfReader`, `CimTriple`, `CimObject.cs` | not implemented as files; a private `CimObject` class inside `CimImporter` over `System.Xml.Linq` (`XDocument`), not a streaming `XmlReader` |
> | `CgmesMapping`, `CgmesExportOptions` | not implemented |
> | SV, SSH, Meas mapping (`SvVoltage`, `SvPowerFlow`, `SvTapStep`, `Analog`/`Discrete`) | SSH values are read; `SvTapStep` and `SvShuntCompensatorSections` by `CimImportOptions`; `SvVoltage`, `SvPowerFlow` and Meas not implemented |
> | `ShuntCompensator`, `OperationalLimitSet`, `RegulatingControl` | shunts and `RegulatingControl` read; `OperationalLimitSet` not implemented |
>
> This is a design record. The current behaviour is documented in technical [ch. 32, CIM/CGMES import and export](<../0032 CIM and CGMES import and export.md>).

This document specifies a CGMES / CIM subset importer and exporter that maps to and
from `GridModel` (`src/GridSim.Core/Model/Model.cs`) and `MeasurementSet`
(`src/GridSim.Core/Estimation/MeasurementSet.cs`). It is an implementation blueprint,
not code: it fixes the CIM classes in scope, the RDF/XML parsing approach (no external
dependencies), the exact field mapping and per-unit handling, the round-trip equivalence
contract, the test plan, and how every dated input is routed through
`ForwardInferenceGuard` so the no-forward-inference invariant holds by construction.

---

## 1. Purpose and scope

GridSim reads MATPOWER `.m` cases (`IO/Matpower`), a JSON case folder, and its own
solve/estimate exports. CGMES is the lingua franca of European TSO/EMS toolchains
(ENTSO-E's profiled subset of IEC 61970/61968 CIM). Supporting a CGMES subset lets
GridSim:

- ingest a published or replayed network snapshot (EQ + TP + SV + SSH) as a `GridModel`
  and estimate on it;
- ingest CIM `Meas` analog values as a `MeasurementSet`;
- export an estimated / solved `GridModel` state back out as CGMES SV (+ a light EQ/TP)
  for consumption by a standards-conformant tool or an independent validator.

**In scope:** a steady-state, balanced positive-sequence subset sufficient to reconstruct
a `GridModel` and a `MeasurementSet`, and to write back the estimated bus voltages and
branch flows. **Out of scope** (listed in section 17): three-phase / short-circuit
sequence data, dynamics (DY) profile, diagram layout (DL/GL), CIM difference models,
multi-authority boundary-set stitching, and the `61968` distribution-management packages.

This spec deliberately reuses GridSim's existing import idioms so the CGMES path reads
like the MATPOWER path: a static entry class with a `...WithDiagnostics` overload that
returns a model plus human-readable notes (`MatpowerParser.ParseWithDiagnostics`,
`MatpowerParseResult`), private DTO/intermediate types, and a post-build
`GridModel.Validate()` gate that fails loud on structural faults.

---

## 2. Governing constraint: no forward inference

`UnderstandingRestrictions.md` draws one line: GridSim runs inference **backward or on the
present**, never forward on the real grid. The estimator is a filter, not a predictor;
every dated input must lag real time by at least `ForwardInferenceGuard.HorizonHours`
(169 h = 7 days + 1 h). A CGMES importer is a **dated ingestion seam** and therefore sits
squarely under invariant #1 ("no ingester accepts a timestamp newer than `UtcNow - 169h`").

CGMES timestamps live in three places, all of which this design routes through the guard
**before any model or measurement object is constructed**:

1. `md:Model.scenarioTime` in every profile's `md:FullModel` header - the instant the
   snapshot describes. This is the authoritative "as-of" stamp for an EQ/TP/SV/SSH set.
2. `md:Model.created` - when the file was produced (secondary; checked when present).
3. `cim:MeasurementValue.timeStamp` on each analog value in a `Meas` file - the per-sample
   telemetry stamp, mapped to (and no newer than) `MeasurementSet.TakenUtc`.

The guard is applied exactly the way `MeasurementSet.Load` applies it today
(`ForwardInferenceGuard.RejectFuture(taken, utcNow, "measurement set")`): parse the stamp,
call the guard, and let `ForwardInferenceException` propagate. There is **no** code path
that reads a CGMES snapshot without first proving its `scenarioTime` is historical.

The exporter is the mirror obligation (invariant #2: "no public-data code path produces an
output stamped later than its newest input"). It writes `md:Model.scenarioTime` equal to
the source state's own as-of stamp (carried on the `GridModel` / estimate provenance), never
`DateTime.UtcNow`, and asserts `scenarioTime <= newestInputStamp` before serialising. CGMES
export is a pure rendering of an already-estimated present/past state; it introduces no new
inference and no forward step.

Because the importer and exporter contain **no solve and no dynamics fusion**, they do not
touch the `Dynamics/ -> Estimation/` review gate (`RestrictionReview`, invariant #5). They
are pure format translation on guard-checked data. This is stated explicitly so a reviewer
does not have to re-derive it.

---

## 3. CGMES background and the profile subset in scope

A CGMES dataset is a set of RDF/XML files, one per **profile**, sharing object identity by
`mRID` (a UUID carried as `rdf:ID` / `rdf:about`). The profiles this design consumes:

| Profile | Code | What it carries | GridSim use |
|---|---|---|---|
| Equipment (core) | EQ | Topology-independent equipment: lines, transformers, machines, loads, shunts, base voltages, terminals | branch/gen/load/shunt structure and impedances |
| Topology | TP | `TopologicalNode`s and the terminal-to-node association (the bus model) | the bus set (one `Bus` per `TopologicalNode`) |
| Steady-State Hypothesis | SSH | The operating point: load p/q, machine p/q, tap steps, shunt sections, controls | injections and setpoints |
| State Variables | SV | The solved state: `SvVoltage` (v, angle), `SvPowerFlow` (p, q), `SvTapStep`, islands | `Vm`/`VaDeg`, cross-check flows |
| Measurement | Meas | `Analog`/`AnalogValue` (+ `Discrete`) tied to a terminal or PSR | the `MeasurementSet` |

CGMES versions differ only in the CIM schema namespace and a few attribute spellings; this
design targets **CGMES 2.4.15 (CIM16)** as the primary dialect and **CGMES 3.0 (CIM100)** as
a recognised alternate, selected by the schema namespace URI found in the file (section 5).
The differences are localised to the `CgmesProfileSchema` constants and never leak into the
mapping logic.

The SSH profile is *strongly recommended but optional*: when absent, injections fall back to
SV `SvPowerFlow` terminal flows aggregated per node (section 8.4), and the importer records a
note. EQ + TP are mandatory to build a `GridModel`; SV is required to populate a solved state
(else buses take flat/nominal `Vm`); Meas is required only for `MeasurementSet` import.

---

## 4. CIM classes and attributes in scope

All names are CIM16 (`cim:` = `http://iec.ch/TC57/2013/CIM-schema-cim16#`). Association
targets are given by `rdf:resource="#<mRID>"`. Attributes not listed are ignored on import
(and not written on export) unless section 17 promotes them later.

### 4.1 Equipment (EQ) - core

| Class | Attributes / associations read | Notes |
|---|---|---|
| `IdentifiedObject` | `.mRID` (from `rdf:ID`/`rdf:about`), `.name` | base of every object; `name` -> `Name` fields |
| `BaseVoltage` | `.nominalVoltage` (kV) | the per-unit voltage base; referenced by nodes and equipment |
| `Terminal` (`ACDCTerminal`) | `.ConductingEquipment`, `.ConnectivityNode`, `.sequenceNumber` | the incidence glue; end 1/2 by `sequenceNumber` |
| `ConnectivityNode` | `.ConnectivityNodeContainer` | node-breaker granularity; collapsed via TP to a `TopologicalNode` |
| `ACLineSegment` (`Conductor`) | `.r`, `.x`, `.bch`, `.gch` (SI: ohm, S), `.length` | pi-model line; `.gch` usually 0 |
| `PowerTransformer` | (container of ends) | two-winding in scope; N-winding -> note + skip (section 17) |
| `PowerTransformerEnd` | `.r`, `.x`, `.g`, `.b` (ohm/S), `.ratedU` (kV), `.ratedS` (MVA), `.endNumber`, `.Terminal`, `.BaseVoltage` | per-end impedance; folded to a single pi branch |
| `RatioTapChanger` | `.stepVoltageIncrement`, `.neutralStep`, `.lowStep`, `.highStep`, `.neutralU`, `.TransformerEnd` | -> `Branch.TapRatio` (with SSH/SV step) |
| `PhaseTapChangerLinear` (and `...Asymmetrical`) | `.stepPhaseShiftIncrement` (or geometry), `.TransformerEnd` | -> `Branch.PhaseShiftDeg` |
| `SynchronousMachine` (`RotatingMachine`, `RegulatingCondEq`) | `.ratedS` (MVA), `.minQ`, `.maxQ`, `.type`, `.RegulatingControl`, `.GeneratingUnit` | -> `Generator` |
| `GeneratingUnit` | `.minOperatingP`, `.maxOperatingP`, `.initialP` | -> `Generator.Pmin/Pmax` |
| `RegulatingControl` | `.mode`, `.targetValue`, `.Terminal`, `.enabled` | `mode=voltage` -> `Generator.Vg`, PV/Slack typing |
| `EnergyConsumer` (`ConductingEquipment`) | (p/q from SSH) | -> bus `Pd`/`Qd` (folded) |
| `LinearShuntCompensator` (`ShuntCompensator`) | `.bPerSection`, `.gPerSection` (S), `.nomU` (kV), `.maximumSections` | -> bus `Gs`/`Bs` (folded, with section count) |
| `EnergySource` / `ExternalNetworkInjection` | `.governorSCD` (ignored); its terminal node -> slack candidate | grid-boundary injection -> slack (section 8.5) |
| `CurrentLimit` / `OperationalLimitSet` / `OperationalLimitType` | `.value` (A), `.direction` | -> `Branch.RateAMva` via `sqrt(3)*U*I` |

### 4.2 Topology (TP)

| Class | Attributes / associations | Notes |
|---|---|---|
| `TopologicalNode` | `.mRID`, `.name`, `.BaseVoltage`, `.ConnectivityNodeContainer` | **one `Bus` per node** - the GridSim bus model |
| `Terminal.TopologicalNode` | association | maps each equipment terminal to its bus |
| `ConnectivityNode.TopologicalNode` | association (node-breaker inputs) | resolves CN-level terminals to a TN |

### 4.3 Steady-State Hypothesis (SSH)

| Class | Attributes | Maps to |
|---|---|---|
| `EnergyConsumer` | `.p`, `.q` (MW, MVAr) | bus `Pd`, `Qd` (summed per node) |
| `RotatingMachine` / `SynchronousMachine` | `.p`, `.q` (MW, MVAr; CGMES sign: injection negative) | `Generator.Pg`, `Qg` (sign-normalised) |
| `TapChanger` | `.step` | resolves `RatioTapChanger` to `TapRatio`; `PhaseTapChanger` to `PhaseShiftDeg` |
| `ShuntCompensator` | `.sections` | shunt susceptance = `sections * bPerSection` |
| `RegulatingControl` | `.targetValue`, `.enabled` | `Generator.Vg` (pu = targetValue/BaseKv), enabled -> typing |
| `EquivalentInjection` | `.p`, `.q` | boundary injection at a border node |

### 4.4 State Variables (SV)

| Class | Attributes | Maps to |
|---|---|---|
| `SvVoltage` | `.TopologicalNode`, `.v` (kV), `.angle` (deg) | bus `Vm` (= v/BaseKv), `VaDeg` |
| `SvPowerFlow` | `.Terminal`, `.p`, `.q` (MW, MVAr) | injection fallback + flow cross-check |
| `SvTapStep` | `.TapChanger`, `.position` | tap when SSH absent |
| `SvShuntCompensatorSections` | `.ShuntCompensator`, `.sections` | shunt when SSH absent |
| `TopologicalIsland` | `.AngleRefTopologicalNode`, `.TopologicalNodes` | slack reference bus (per island) |
| `SvStatus` | `.ConductingEquipment`, `.inService` | `Branch.InService` |

### 4.5 Measurement (Meas)

| Class | Attributes / associations | Maps to |
|---|---|---|
| `Analog` (`Measurement`) | `.measurementType`, `.unitSymbol`, `.unitMultiplier`, `.phases`, `.Terminal`, `.PowerSystemResource` | `MeasurementKind` + location |
| `AnalogValue` (`MeasurementValue`) | `.value`, `.Analog`, `.sensorAccuracy` | `Measurement.Value`, `Sigma` |
| `MeasurementValue` | `.timeStamp` | `MeasurementSet.TakenUtc` (guarded) |
| `MeasurementValueSource` | `.name` (e.g. "SCADA", "PMU", "CALCULATED") | `Measurement.Source` |
| `Discrete` / `DiscreteValue` | `.value` (switch state) | optional: `Branch.InService` (section 11.4) |

---

## 5. RDF/XML parsing approach (no external dependencies)

CGMES files are RDF/XML but constrained to a **flat, striped serialisation**: a document is a
sequence of top-level typed resource elements, each with literal-valued property children and
`rdf:resource` reference children. GridSim does **not** need a general RDF/OWL reasoner - it
needs a striped-RDF reader. That is implementable on `System.Xml` alone (already available in
.NET; no NuGet, satisfying the "no external deps" rule and the zero-warning gate).

### 5.1 Reader

Use `System.Xml.XmlReader` over a `FileStream` (streaming, low-allocation, matches the
"throttle disk I/O" house style) to produce a flat list of `CimTriple(subject, predicate,
object)` where:

- **subject** = the `rdf:ID` (prefix `_` -> mRID) or `rdf:about` (`#mRID` or full URI) of the
  enclosing typed element;
- **predicate** = the element-qualified name, either `rdf:type` (from the element name of the
  typed resource, e.g. `cim:ACLineSegment`) or a property QName (e.g. `cim:ACLineSegment.r`);
- **object** = a literal (element text) for value properties, or a resource mRID (from the
  child element's `rdf:resource`) for associations.

Enumeration values arrive as `rdf:resource="http://.../CIM-schema-cim16#RegulatingControlModeKind.voltage"`;
the reader keeps the fragment after `#` (`RegulatingControlModeKind.voltage`) as the object.

### 5.2 Graph

Fold the triples into `CimObject` records keyed by mRID:

```
CimObject:
    string   Mrid
    string   CimType                 // e.g. "ACLineSegment" (namespace stripped)
    IReadOnlyDictionary<string,string>  Literals     // predicate localName -> text
    IReadOnlyDictionary<string,string>  References    // predicate localName -> target mRID
    ILookup or multimap for repeated predicates (e.g. TopologicalIsland.TopologicalNodes)
```

`predicate localName` is the tail after the class-qualified dot: `ACLineSegment.r` -> `r`,
`Terminal.TopologicalNode` -> `TopologicalNode`. Because CIM property names are unique within a
class, storing by localName is safe and keeps the mapping code terse. Repeated associations
(one-to-many, e.g. `TopologicalIsland.TopologicalNodes`) go into a separate multimap.

### 5.3 Dataset

`CgmesDataset` is the parsed multi-profile object set: it merges the `CimObject`s from every
supplied file **by mRID** (an object may be *defined* in EQ and *augmented* in SSH/SV - CGMES
carries the same mRID across profiles with different property subsets), and records which
`CgmesProfile` each file declared (from `md:Model.profile` / the `rdf:type` mix). The header
records (`md:FullModel` per file) are collected into `CgmesModelHeader` values carrying
`ScenarioTimeUtc`, `CreatedUtc`, `Version`, `Profile`, `ModelingAuthoritySet`.

### 5.4 Robustness rules

- Namespace-agnostic on the CIM prefix value: bind `cim` from the document's own `xmlns:cim`,
  and recognise CIM16 vs CIM100 by that URI. Fail loud (`InvalidDataException`) on an
  unrecognised CIM schema namespace rather than silently mis-parsing.
- `rdf:ID` vs `rdf:about`: normalise both to a bare mRID (strip a leading `_`, `#`, or the
  `urn:uuid:` / full-URI prefix). Identity is by this normalised mRID everywhere.
- Invariant culture for every numeric parse (`CultureInfo.InvariantCulture`,
  `NumberStyles.Float`), mirroring `MatpowerParser.ParseNum`.
- Unknown classes and unknown properties are skipped and counted into the diagnostics notes
  ("skipped N objects of M unmapped classes"), never fatal - forward-compatible with richer
  files.

---

## 6. Namespace and file layout

New code lives in `GridSim.Core.IO.Cgmes`, mirroring `GridSim.Core.IO.Matpower`. All types are
`internal` except the two entry classes and the public result/option records, matching the
existing IO surface.

```
src/GridSim.Core/IO/Cgmes/
  CgmesProfile.cs         enum { Eq, Tp, Ssh, Sv, Meas, Unknown } + schema-namespace constants
  CimTriple.cs            readonly record struct (subject, predicate, object, IsResource)
  CimObject.cs            parsed resource (mRID, type, literals, refs, multimap)
  CgmesModelHeader.cs     md:FullModel header (ScenarioTimeUtc, CreatedUtc, Version, Profile, Mas)
  CimRdfReader.cs         XmlReader -> IEnumerable<CimTriple>; header extraction
  CgmesDataset.cs         merge triples across files by mRID; expose typed selectors
  CgmesUnits.cs           SI<->per-unit helpers (Zbase, shunt, tap, rating, angle)
  CgmesMapping.cs         static maps: measurementType/unitSymbol -> MeasurementKind; enum tables
  CgmesImporter.cs        static ImportFiles(...) / ImportWithDiagnostics(...) -> CgmesImportResult
  CgmesImportResult.cs    record (GridModel Model, MeasurementSet? Measurements, IReadOnlyList<string> Notes)
  CgmesExporter.cs        static Export(GridModel, EstimationResult|PowerFlowResult, options) -> files
  CgmesExportOptions.cs   record (profiles to write, mRID policy, scenario time source, author set)
```

Test project: `tests/GridSim.Tests/Cgmes/` (section 15).

The `CgmesImporter` public surface, matching `MatpowerParser`'s idiom:

```
CgmesImportResult ImportWithDiagnostics(
    IReadOnlyList<string> profilePaths,   // EQ/TP/SSH/SV/Meas files, any order
    DateTime? utcNow = null,               // for the guard; defaults to DateTime.UtcNow
    double frequencyHz = 50.0)

GridModel Import(IReadOnlyList<string> profilePaths, DateTime? utcNow = null)   // .Model of the above
```

`utcNow` is threaded exactly like `MeasurementSet.Load(path, utcNow)` so the guard is testable
with a fixed clock (section 15.5).

---

## 7. Intermediate data structures

Between the `CgmesDataset` and the emitted `GridModel` sits one internal builder that resolves
CIM's node-breaker/associative graph into GridSim's bus-branch arrays. Named to fit the codebase
(`...Builder`, private nested DTOs like `SolveImport`):

- `CgmesNetworkBuilder` - orchestrates the passes of section 8; owns the mRID->index maps.
- `NodeRecord` - one `TopologicalNode`'s accumulation: `Mrid`, `Name`, `BaseKv`, running
  `Pd`, `Qd`, `Gs`, `Bs`, `SvVm`, `SvVaDeg`, `IsSlack`, incident terminal mRIDs.
- `BranchRecord` - one line or transformer reduced to a pi-model: `FromNodeMrid`, `ToNodeMrid`,
  `RPu`, `XPu`, `BPu`, `TapRatio`, `PhaseShiftDeg`, `RateAMva`, `Name`, `IsTransformer`,
  `InService`.
- `GenRecord` - one `SynchronousMachine`: `NodeMrid`, `Pg`, `Qg`, `Vg`, `Qmin`, `Qmax`,
  `Pmin`, `Pmax`, `MBase`, `Name`, `Type`.

The builder maps `TopologicalNode` mRID -> a stable 1-based integer `Bus.Id` (CGMES has no
integer bus numbers). The mRID<->id crosswalk is retained on `CgmesImportResult` (via a note and
an optional out-map) so export can round-trip identity (section 13.2). Bus `Id` assignment is
deterministic: nodes sorted by mRID ordinal, then numbered - so two imports of the same dataset
produce byte-identical models (determinism, section 15.4).

---

## 8. Import pipeline (EQ + TP + SSH + SV -> GridModel)

Ordered passes over the merged `CgmesDataset`. Each pass is pure and side-effect-free except for
accumulation into the builder.

### 8.0 Guard first

Extract every `CgmesModelHeader.ScenarioTimeUtc` (and `CreatedUtc` when present) and call
`ForwardInferenceGuard.AssertHistorical(stamps, utcNow, "cgmes snapshot")` **before pass 8.1**.
A future/recent stamp throws `ForwardInferenceException` and no model is built. The newest
accepted `scenarioTime` becomes the dataset's as-of instant, carried onto the result for the
export mirror (section 2) and stored as `MeasurementSet.TakenUtc` when Meas is imported.

### 8.1 Base voltages

Index `BaseVoltage.mRID -> nominalVoltage` (kV). This is the per-unit voltage base for every
node and every SI impedance conversion downstream (section 9).

### 8.2 Nodes (buses)

One `NodeRecord` per `TopologicalNode`. `BaseKv` from `TopologicalNode.BaseVoltage`
(fallback: the node's equipment `BaseVoltage`, else the container's). Build the
`Terminal.mRID -> TopologicalNode.mRID` map from TP (resolving `ConnectivityNode` terminals via
`ConnectivityNode.TopologicalNode` when the input is node-breaker).

### 8.3 Loads and shunts (folded onto the bus)

MATPOWER/GridSim fold demand and shunts onto the bus (`Model.cs` remarks). For each
`EnergyConsumer`, find its terminal's node and add SSH `.p`/`.q` to that `NodeRecord`'s
`Pd`/`Qd`. For each `LinearShuntCompensator`, add `sections * gPerSection` (times `nomU^2`
scaling to the node base, section 9.4) to `Gs` and `sections * bPerSection` to `Bs`. `sections`
comes from SSH `ShuntCompensator.sections`, else SV `SvShuntCompensatorSections.sections`, else
`0` with a note.

### 8.4 Machines (generators)

One `GenRecord` per `SynchronousMachine` (and `ExternalNetworkInjection`/`EnergySource` treated
as generators for typing). `Pg`/`Qg` from SSH with the **sign convention normalised** (CGMES
`RotatingMachine.p` is load-positive at the terminal, i.e. generation is negative; GridSim
`Generator.Pg` is generation-positive - negate on import, section 9.5). `Vg` from
`RegulatingControl.targetValue / BaseKv` when `mode = voltage` and `enabled`. `Qmin`/`Qmax` from
`SynchronousMachine.minQ`/`.maxQ` (else `+/-Infinity`, the GridSim "no limit" default).
`Pmin`/`Pmax` from `GeneratingUnit.minOperatingP`/`.maxOperatingP`. `MBase` from `ratedS`
(else `BaseMva`).

### 8.5 Bus typing (Slack / PV / PQ)

CIM has no explicit bus type; derive it (this is the one genuinely inferential step, and it is a
**model-structure** inference, not a grid-state one, so it is unrestricted):

1. **Slack**: the `TopologicalIsland.AngleRefTopologicalNode` (SV) is the reference bus for its
   island - `BusType.Slack`. If SV/island is absent, the node hosting an
   `ExternalNetworkInjection` / `EnergySource` is the slack; if several, the largest `ratedS`
   wins and the rest become PV (a note records the choice). Multiple islands -> multiple slacks,
   which `GridModel.Validate` treats as a benign multi-island warning.
2. **PV**: a non-slack node with at least one `SynchronousMachine` whose `RegulatingControl`
   is voltage-mode and enabled -> `BusType.Pv`.
3. **PQ**: everything else.

### 8.6 Branches (lines)

One `BranchRecord` per in-service `ACLineSegment`. Resolve its two terminals to
`FromNodeMrid`/`ToNodeMrid` (terminal `sequenceNumber` 1 = From, 2 = To). Convert SI to per-unit
against the line's `BaseVoltage` (section 9.2): `RPu`, `XPu`, `BPu = bch * Zbase`. `RateAMva`
from the branch's `CurrentLimit` via `sqrt(3) * BaseKv * I_amp / 1000` when present.

### 8.7 Branches (transformers)

One `BranchRecord` per two-winding `PowerTransformer`, `IsTransformer = true`. A CGMES transformer
carries impedance on its `PowerTransformerEnd`s (SI, referred to each end's `ratedU`). Reduce to a
single pi branch on the network base (section 9.3):

- Series impedance = sum of the two ends' `r + jx`, each referred to a common base voltage
  (choose the From end's `ratedU`), then per-unitised on `BaseMva`.
- `TapRatio` from the `RatioTapChanger`: `1 + (step - neutralStep) * stepVoltageIncrement/100`,
  with `step` from SSH `TapChanger.step` (else SV `SvTapStep.position`). Off-nominal turns ratio
  also folds the two ends' `ratedU` mismatch: `ratedU_from / ratedU_to` scaled to the node base.
- `PhaseShiftDeg` from a `PhaseTapChangerLinear`:
  `(step - neutralStep) * stepPhaseShiftIncrement`.
- `RateAMva` from the end `ratedS` or a `CurrentLimit`.

An N-winding (3+) transformer is **not** reduced in this phase: skip it, emit a diagnostic note,
and (section 17) leave a seam for a star-point auxiliary bus. This matches the "fail loud / note,
never silently corrupt" stance of `MatpowerUnitNormalizer`.

### 8.8 Solved state (SV)

For each `SvVoltage`, set the node's `SvVm = v / BaseKv` (per-unit) and `SvVaDeg = angle`. These
populate `Bus.Vm`/`Bus.VaDeg` (initial/fixed voltages) and, when a solved `PowerFlowResult` is
requested (an `ImportOptions.WithSolvedState` flag), a `PowerFlowResult` assembled through the
same `NewtonShell.AssembleResult` path `EstimationResult.ToPowerFlowResult` uses - so SV import
and estimate export share one assembly seam and cannot drift.

### 8.9 Emit and validate

Assemble the `GridModel` (sealed class - copy every property explicitly, as
`MatpowerUnitNormalizer.Normalize` does), then run `GridModel.Validate()` and **throw on any
`ValidationSeverity.Error`** (`InvalidDataException` naming every offender), exactly as
`MatpowerParser.ParseWithDiagnostics` does. Warnings (multi-island, OLTC) become notes.

---

## 9. Unit and per-unit handling

GridSim is per-unit on `BaseMva` with impedances per-unit and power in MW/MVAr
(`docs/technical/02-data-model-and-per-unit.md`, `MatpowerUnitNormalizer`). CGMES is **SI**:
impedances in ohms, susceptance/conductance in siemens, voltage in kV, power in MW/MVAr, angle in
degrees. Conversion is the textbook per-unit reduction, centralised in `CgmesUnits`.

### 9.1 Bases

```
BaseMva := dataset system base. CGMES has no single system base; adopt 100 MVA
           (GridSim default) unless every ratedS agrees on another value. Record the
           choice in a note; it is the invariant divisor for all power/impedance pu.
Zbase(kV) := kV^2 / BaseMva          (ohms)          // same formula as MatpowerUnitNormalizer
Ybase(kV) := 1 / Zbase(kV)           (siemens)
```

### 9.2 Lines

```
RPu = r_ohm / Zbase(BaseKv)
XPu = x_ohm / Zbase(BaseKv)
BPu = bch_siemens * Zbase(BaseKv)     // susceptance scales by *Zbase (=/Ybase), NOT /Zbase
GPu = gch_siemens * Zbase(BaseKv)
```

`BaseKv` is the line's `BaseVoltage.nominalVoltage`. (Note the `MatpowerUnitNormalizer` remark:
charging susceptance converts by multiplying `Zbase`, not dividing - the design follows that.)

### 9.3 Transformers

Refer each end's SI `r+jx` to a common winding (the From end's `ratedU`) using the turns-ratio
squared, sum, then per-unitise on `BaseMva` and the From node `BaseKv`. The off-nominal tap and
the `ratedU_from/ratedU_to` ratio combine into `Branch.TapRatio` (dimensionless), so the pi-model
matches `MeasurementModel`'s transformer stamps (`yff = (ys+bc)/tapA^2`, `yft = -ys/conj(tap)`).

### 9.4 Shunts

`LinearShuntCompensator` susceptance is quoted at `nomU`. Convert to the node base:
`Bs_mvar_at_1pu = sections * bPerSection * nomU^2` (MVAr at 1.0 pu on the node), matching the
`Bus.Bs` definition ("the MW/MVAr it would draw at 1.0 pu voltage", `Model.cs`). Likewise `Gs`.

### 9.5 Power and sign

Load `EnergyConsumer.p/q` are demand-positive -> add directly to `Pd`/`Qd`. Machine
`RotatingMachine.p/q` follow the CIM terminal convention (flow into the equipment); GridSim
`Generator.Pg` is generation-out-of-machine positive -> **negate**. This single sign flip is
unit-tested against a case with a known dispatch (section 15.3).

### 9.6 Voltage and angle

`SvVoltage.v` (kV) -> `Vm = v / BaseKv` (pu). `SvVoltage.angle` is already degrees, matching
`Bus.VaDeg` and `MeasurementKind.VaDeg` (the estimator holds angles in radians internally but the
model/measurement surface is degrees - `MeasurementModel` converts on ingest). No re-referencing
is needed: CGMES angles are already in the island's slack frame
(`TopologicalIsland.AngleRefTopologicalNode`), which the importer makes the GridSim slack - so
the angle datum is consistent by construction (contrast the Gridradar per-feed offset problem in
`docs/pmu-gridradar-design-note.md`, which does not arise here).

---

## 10. GridModel <-> CGMES field mapping (authoritative table)

| GridModel field | CGMES source (import) | CGMES target (export) | Transform |
|---|---|---|---|
| `GridModel.BaseMva` | adopted (100 or agreed `ratedS`) | (not a CIM object; implicit) | section 9.1 |
| `Bus.Id` | assigned from `TopologicalNode.mRID` (deterministic) | mRID via crosswalk | 1-based, mRID-sorted |
| `Bus.Type` | inferred (section 8.5) | island ref / regulating control | derived |
| `Bus.BaseKv` | `TopologicalNode.BaseVoltage.nominalVoltage` | `BaseVoltage.nominalVoltage` | kV, direct |
| `Bus.Pd` / `Qd` | sum SSH `EnergyConsumer.p/q` at node | one `EnergyConsumer` per node | MW/MVAr |
| `Bus.Gs` / `Bs` | `LinearShuntCompensator` (sections) | `LinearShuntCompensator` | section 9.4 |
| `Bus.Vm` | `SvVoltage.v / BaseKv` | `SvVoltage.v = Vm * BaseKv` | pu <-> kV |
| `Bus.VaDeg` | `SvVoltage.angle` | `SvVoltage.angle` | degrees, direct |
| `Bus.Name` | `TopologicalNode.name` | `.name` | text |
| `Branch.From/To` | terminal `sequenceNumber` 1/2 -> node ids | terminals to nodes | id crosswalk |
| `Branch.R/X` | `ACLineSegment.r/x` or transformer ends | `.r/.x` (SI) | / and * `Zbase` |
| `Branch.B` | `ACLineSegment.bch` | `.bch` | * / `Zbase` |
| `Branch.TapRatio` | `RatioTapChanger` + step | `RatioTapChanger` + `SvTapStep` | section 9.3 |
| `Branch.PhaseShiftDeg` | `PhaseTapChangerLinear` + step | `PhaseTapChanger` | degrees |
| `Branch.RateAMva` | `CurrentLimit` / end `ratedS` | `OperationalLimit` | `sqrt(3)*U*I` |
| `Branch.Name` | `ACLineSegment.name` / `PowerTransformer.name` | `.name` | text |
| `Branch.IsTransformer` | class is `PowerTransformer` | class choice | boolean |
| `Branch.InService` | `SvStatus.inService` (else true) | `SvStatus.inService` | boolean |
| `Generator.Bus` | machine terminal -> node id | terminal to node | id crosswalk |
| `Generator.Pg/Qg` | SSH machine `p/q`, negated | `p/q`, negated back | sign flip (9.5) |
| `Generator.Vg` | `RegulatingControl.targetValue/BaseKv` | `RegulatingControl.targetValue` | pu <-> kV |
| `Generator.Qmin/Qmax` | `SynchronousMachine.minQ/maxQ` | `.minQ/.maxQ` | MVAr |
| `Generator.Pmin/Pmax` | `GeneratingUnit.min/maxOperatingP` | `.min/maxOperatingP` | MW |
| `Generator.MBase` | `RotatingMachine.ratedS` | `.ratedS` | MVA |
| `Generator.Name` | `SynchronousMachine.name` | `.name` | text |
| `GridModel.Oltcs` | `RatioTapChanger` regulating -> `Oltc` | `RatioTapChanger.TapChangerControl` | optional (section 17) |

Fields with no CGMES equivalent (`Bus.X/Y` layout, `Generator.H` inertia, `DynamicsClass`,
`Protections`, `Boundaries`) are **not** written and default sensibly on import, exactly as
`SolveImport` handles export-absent fields. `Generator.H` in particular is a DY-profile concern,
out of scope here.

---

## 11. Measurement mapping (MeasurementSet <-> Meas profile)

### 11.1 Kind resolution

A CIM `Analog` carries `measurementType` + `unitSymbol` + `unitMultiplier` and is tied to a
`Terminal` (branch-end quantity) or a `PowerSystemResource` (bus quantity). Resolve to
`MeasurementKind` via `CgmesMapping`:

| CIM `measurementType` | unitSymbol | Terminal? | `MeasurementKind` | Notes |
|---|---|---|---|---|
| `Voltage` / `LineToLineVoltage` / `PhaseVoltage` | `V` | on a node/PSR | `VmPu` | value/BaseKv -> pu |
| `Angle` / `VoltageAngle` | `deg` | node/PSR | `VaDeg` | already slack-frame |
| `ThreePhaseActivePower` / `ActivePower` | `W` | PSR (bus) | `PInjMw` | net injection |
| `ThreePhaseReactivePower` / `ReactivePower` | `VAr` | PSR (bus) | `QInjMvar` | net injection |
| `ThreePhaseActivePower` / `ActivePower` | `W` | Terminal (branch) | `PFlowMw` | end from terminal seq |
| `ThreePhaseReactivePower` / `ReactivePower` | `VAr` | Terminal (branch) | `QFlowMvar` | end from terminal seq |
| `Current` | `A` | Terminal (branch) | `IMagPu` | value/Ibase -> pu |

`unitMultiplier` (`M` mega, `k` kilo) scales the raw value to MW/MVAr/kV before the per-unit or
engineering-unit conversion the `Measurement` record expects (`Measurement` values are in
engineering units: pu for voltage/current, MW/MVAr for power, degrees for angle -
`Measurement.cs`).

### 11.2 Location resolution

- **Bus quantity** (`PInjMw`, `QInjMvar`, `VmPu`, `VaDeg`): resolve the `Analog`'s
  `PowerSystemResource` or its `Terminal.TopologicalNode` to a `Bus.Id` and set
  `Measurement.Bus`.
- **Branch quantity** (`PFlowMw`, `QFlowMvar`, `IMagPu`): resolve the `Analog.Terminal` to its
  `ConductingEquipment` (the branch) and its two nodes -> `Measurement.From`, `Measurement.To`,
  and `Measurement.End` from the terminal `sequenceNumber` (1 -> `BranchEnd.From`, 2 ->
  `BranchEnd.To`). Parallel circuits are disambiguated with `Measurement.Circuit` set to the
  branch `Name` (or `"#k"` ordinal), exactly as `MeasurementSet.TryResolveBranch` expects.

### 11.3 Value, sigma, source, time

- `Measurement.Value` = `AnalogValue.value` (scaled by `unitMultiplier`, per-unitised where the
  kind is pu).
- `Measurement.Sigma`: CGMES rarely ships a per-sample sigma. Priority: `Analog.sensorAccuracy`
  if present (interpreted as a 1-sigma in the measurement's unit); else a **kind-default sigma
  table** in `CgmesMapping` (e.g. `VmPu` 0.005 pu, `PFlowMw` 1% of value floored at a small
  absolute, `VaDeg` per the PMU note). The chosen sigma policy is recorded in
  `MeasurementSet.Note`. Sigma must be finite and `> 0` or `MeasurementSet.Validate` throws.
- `Measurement.Source` = `MeasurementValueSource.name` lowercased (`"scada"`, `"pmu"`,
  `"calculated"`), matching the existing `Measurement.Source` convention (default `"scada"`).
- `MeasurementValue.timeStamp`: the **newest** timestamp across accepted values becomes
  `MeasurementSet.TakenUtc`. All timestamps are guard-checked (section 11.5); a set may not mix
  ages that straddle the horizon.

### 11.4 Discrete (optional)

A `Discrete` switch-status measurement can drive `Branch.InService`. In scope only as an
importer-side note in this phase; wiring it to topology is deferred (section 17).

### 11.5 Guard on measurement timestamps

Before any `Measurement` is constructed, `ForwardInferenceGuard.AssertHistorical` is called over
every `MeasurementValue.timeStamp`. This is the Meas-profile analogue of `MeasurementSet.Load`'s
single-stamp check, and it means the CGMES measurement path is a registered dated ingestion seam
(to be pinned by `RestrictionInvariantTests`, section 16).

---

## 12. Import algorithm summary (pseudocode, not C#)

```
ImportWithDiagnostics(paths, utcNow, frequencyHz):
    files   = paths.map(CimRdfReader.ReadFile)          // streaming XmlReader -> triples + header
    dataset = CgmesDataset.Merge(files)                 // by mRID; classify profiles
    # --- GUARD FIRST (no-forward-inference) ---
    ForwardInferenceGuard.AssertHistorical(dataset.Headers.ScenarioTimes, utcNow, "cgmes snapshot")
    asOf = max(dataset.Headers.ScenarioTimes)

    require(dataset.HasProfile(Eq) and dataset.HasProfile(Tp))   // else InvalidDataException
    b = new CgmesNetworkBuilder(dataset, frequencyHz)
    b.IndexBaseVoltages()          # 8.1
    b.BuildNodes()                 # 8.2  -> Bus.Id crosswalk (mRID-sorted, deterministic)
    b.FoldLoadsAndShunts()         # 8.3
    b.BuildGenerators()            # 8.4
    b.InferBusTypes()              # 8.5
    b.BuildLines()                 # 8.6
    b.BuildTransformers()          # 8.7  (2-winding; note+skip N-winding)
    b.ApplySvState()               # 8.8  (optional)
    model = b.EmitModel()          # 8.9  -> GridModel.Validate(); throw on Error

    measurements = dataset.HasProfile(Meas)
                 ? ImportMeasurements(dataset, model, b.Crosswalk, utcNow, asOf)   # section 11
                 : null
    return CgmesImportResult(model, measurements, b.Notes)
```

---

## 13. Export pipeline (GridModel + estimated state -> CGMES)

### 13.1 What is written

A minimal, self-consistent CGMES set sufficient for an independent tool (or GridSim's own
importer) to reconstruct the state:

- **EQ**: `BaseVoltage` per distinct kV; `TopologicalNode` (also serves as the bus in a
  bus-branch export); `Terminal`s; `ACLineSegment` / `PowerTransformer` (+ ends, tap changers);
  `SynchronousMachine`; `EnergyConsumer`; `LinearShuntCompensator`.
- **TP**: `TopologicalNode`s and terminal associations (bus-branch: one TN per bus).
- **SSH**: injections and setpoints (`EnergyConsumer.p/q`, machine `p/q`, tap `step`).
- **SV**: `SvVoltage` (from the **estimated** `Vm`/`VaDeg`), `SvPowerFlow` per terminal (from the
  estimate's recomputed flows via `EstimationResult.ToPowerFlowResult` -> `LineFlow`), `SvTapStep`,
  `TopologicalIsland` with the slack as `AngleRefTopologicalNode`.
- **Meas** (optional): the `MeasurementSet` back out as `Analog`/`AnalogValue` (inverse of
  section 11) - useful for a validator to check the estimator consumed what it claims.

### 13.2 Identity round-trip

When the source `GridModel` came from a CGMES import, the mRID<->id crosswalk on
`CgmesImportResult` is reused so exported objects carry their **original mRIDs** - a true
round-trip. When the source is a MATPOWER/JSON model with no mRIDs, deterministic UUIDv5-style
mRIDs are minted from a fixed namespace + the bus id / branch endpoints (stable across runs, so
export is reproducible - section 15.4). Minting uses only `System.Security.Cryptography` (in-box;
no dependency).

### 13.3 Scenario time (the export mirror of the guard)

`md:Model.scenarioTime` is set from the source state's own as-of instant:
`CgmesExportOptions.ScenarioTimeUtc`, which defaults to the imported dataset's `asOf` (section
8.0) or, for an estimate, the `MeasurementSet.TakenUtc` the estimate was computed from. The
exporter **asserts** `scenarioTime <= newestSourceInputStamp` and **never** substitutes
`DateTime.UtcNow`. A caller trying to stamp an export in the present/future fails loud
(`InvalidOperationException`) - the code-level enforcement of invariant #2. `md:Model.created`
may be `UtcNow` (it describes file creation, not the modelled instant) but is clearly separated
from `scenarioTime`.

### 13.4 Serialisation

Write striped RDF/XML with `System.Xml.XmlWriter` (indented, invariant culture). Deterministic
output ordering: objects sorted by (class, mRID); properties in a fixed schema order. This makes
export byte-for-byte reproducible (the same doctrine as `gridsim-solve-export/1`:
"byte-for-byte reproducible given a deterministic solve").

---

## 14. Round-trip equivalence contract

The central correctness property: **import then export then import is a fixed point on the
`GridModel`, and export then import then export is a fixed point on the CGMES bytes.**

Define `M` = model, `C` = CGMES file set.

1. `import(C) = M0`; `export(M0, sv0) = C1`; `import(C1) = M1`. Require `M1 == M0` under the
   numeric equivalence of section 14.1 (structure exact, reals to machine epsilon).
2. `export(M0) = C1`; `import(C1) = M1`; `export(M1) = C2`. Require `C2 == C1` **byte-for-byte**
   (determinism + fixed ordering).

### 14.1 Numeric equivalence

- Integer/enum/string fields (`Bus.Id` after crosswalk, `Bus.Type`, `Branch.IsTransformer`,
  names): **exact**.
- Real fields (`R`, `X`, `B`, `TapRatio`, `Pd`, `Vm`, ...): equal to within a relative tolerance
  at the SI<->pu conversion's floating-point round-trip limit. Because pu conversion is a single
  multiply/divide by `Zbase`, the round trip `x -> x/Zbase -> *Zbase` recovers `x` to a few ULPs;
  the test tolerance is `1e-12` relative (the same machine-epsilon class as the solver validation
  gates), not an engineering tolerance.
- Bus voltages carried through SV: `Vm` round-trips through `v = Vm*BaseKv` then `Vm = v/BaseKv`
  to a few ULPs.

---

## 15. Validation and test plan

Test project `tests/GridSim.Tests/Cgmes/`, xUnit, matching the existing suite conventions
(`ForwardInferenceGuardTests`, `MatpowerParserTests`). All tests run under the zero-warning gate.

### 15.1 Fixtures

- The **CGMES conformity mini-cases** (ENTSO-E's published "MicroGrid" / "MiniGrid" test
  configurations) as the primary real-format fixtures, checked into `tests/.../Cgmes/fixtures/`.
  These ship with a known SV solution - the independent reference (below).
- A GridSim-authored round-trip fixture: export `IeeeCases.Case9()` / `Case14()` /`Case118()` to
  CGMES, then read back.

### 15.2 Machine-epsilon cross-checks against an independent reference

Two independent references, both giving machine-epsilon (not engineering) agreement:

1. **Against the CGMES SV file** (the format's own published solution): import a MicroGrid
   configuration, and for every `TopologicalNode` assert the imported `Bus.Vm`/`VaDeg` equals the
   `SvVoltage` value to `1e-9` (SV is stored to full precision; the only arithmetic is the single
   `v/BaseKv` divide). This proves the parser and unit handling are exact, independent of any
   GridSim solve.
2. **Against GridSim's own power flow**: import the network (EQ+TP+SSH), run
   `SteadyStateSolver` / Newton-Raphson on the reconstructed `GridModel`, and assert the solved
   `Vm`/`VaDeg` matches the CGMES SV solution to the solver's convergence tolerance. This proves
   the impedance/injection mapping is physically correct - the CGMES snapshot and GridSim's
   physics agree on the same state. (This is a *solve on guard-checked historical data*, permitted
   reconstruction, not forward inference.)

The MATPOWER path already cross-validates against published IEEE solutions; the CGMES path adds
CGMES SV as a second independent oracle for the same networks where both formats exist.

### 15.3 Unit and sign tests

- `Zbase` reduction: a line with known ohmic `r/x/bch` and a known `BaseVoltage` imports to the
  hand-computed per-unit values (exact to `1e-12`).
- Shunt: `sections * bPerSection * nomU^2` -> `Bus.Bs` at 1.0 pu.
- Transformer tap: `step`/`neutralStep`/`stepVoltageIncrement` -> `TapRatio`; phase shifter ->
  `PhaseShiftDeg`.
- Machine sign flip: a case with a known dispatch (generation-positive `Pg`) imports with the
  correct sign; export negates it back.

### 15.4 Determinism

- Import the same file set twice (and in **shuffled file order**, and with shuffled
  intra-file object order): assert the two `GridModel`s are structurally identical and the
  `Bus.Id` assignment is byte-identical (mRID-sorted numbering).
- Export the same model twice: assert byte-for-byte identical CGMES output (fixed ordering +
  minted-mRID stability).

### 15.5 No-forward-inference tests (the load-bearing suite)

- A CGMES set whose `md:Model.scenarioTime` is `UtcNow` (or `UtcNow - 1h`, inside the 169 h
  horizon) throws `ForwardInferenceException` on import, with a fixed `utcNow` injected - and
  **no `GridModel` is constructed** (assert the exception precedes any partial build).
- A set at exactly `UtcNow - 169h` is on the boundary; at `UtcNow - 170h` it imports cleanly.
- A `Meas` file with a value timestamped inside the horizon throws before any `Measurement` is
  built.
- Export refuses a `scenarioTime` newer than the source's newest input stamp
  (`InvalidOperationException`), and never emits `DateTime.UtcNow` as `scenarioTime`.
- A `RestrictionInvariantTests` addition exercises `CgmesImporter.Import` and
  `ImportMeasurements` as dated seams, pinning them alongside the existing per-seam guard tests
  (the review log's "Note on invariant #1's marker" lists the seams; CGMES import joins them).

### 15.6 Round-trip tests

The two fixed-point properties of section 14, at `1e-12` relative for reals and exact for
structure/bytes.

### 15.7 Robustness / negative tests

- Unknown CIM schema namespace -> `InvalidDataException` (not a mis-parse).
- Missing EQ or TP -> `InvalidDataException`.
- N-winding transformer -> imported model omits it with a diagnostic note (asserted), never a
  corrupt branch.
- A structurally invalid reconstructed model (dangling terminal, no slack) -> the
  `GridModel.Validate()` error path throws, naming offenders (mirrors `MatpowerParser`).

---

## 16. How the restriction invariants are honoured

| Invariant (`UnderstandingRestrictions.md`) | How this design satisfies it |
|---|---|
| #1 no ingester accepts a stamp newer than `UtcNow - 169h` | `ForwardInferenceGuard.AssertHistorical` runs on every `scenarioTime` and every `MeasurementValue.timeStamp` **before** any model/measurement is built (sections 8.0, 11.5). CGMES import is registered as a dated seam in `RestrictionInvariantTests`. |
| #2 no output stamped later than its newest input | The exporter sets `md:Model.scenarioTime` from the source's as-of instant and asserts `<= newest input`; it never uses `UtcNow` for `scenarioTime` (section 13.3). |
| #3 solver forward trajectories are model output only | Not applicable - the importer/exporter run no solver forward step. The optional SV cross-check solve (test 15.2) is a reconstruction on historical data, seeded from and stamped at the guard-permitted `scenarioTime`. |
| #4 real-time features on permitted-age / replay data | CGMES snapshots are historical by the guard; there is no live CGMES feed path. |
| #5 Dynamics -> Estimation fusion gated by review | Not applicable - format translation fuses no dynamics process model; `RestrictionReview.AssertPermitted` is untouched. Stated explicitly (section 2) so no re-derivation is needed. |

The importer is, like every other GridSim ingester, **structurally incapable** of nowcasting: it
cannot construct a model from a snapshot it has not first proven is at least a week old.

---

## 17. Zero-warning build gate and coding-standard compliance

`Directory.Build.props` enforces `TreatWarningsAsErrors=true`, `EnableNETAnalyzers=true`,
`AnalysisMode=Recommended`, `Nullable=enable`. The design is written to pass it with no
suppressions:

- **No external dependencies**: only `System.Xml`, `System.Text`, `System.Globalization`,
  `System.Security.Cryptography` (all in-box). No NuGet package is added, so no new analyser
  surface and no supply-chain review.
- **Nullable-clean**: intermediate records use non-null defaults; optional CGMES data
  (`MeasurementSet?`, absent SSH) is modelled with explicit nullable reference types and handled,
  never null-forgiven without cause (the one `!` idiom used in `MeasurementSet.Save` for a known
  directory is the pattern to follow if needed).
- **Culture-invariant** every parse/format (CA1305), as `MatpowerParser` and `SolveImport` do.
- **`XmlReader`/`XmlWriter`** configured with `DtdProcessing = Prohibit` and no external entity
  resolution (CA3075 / XXE safety) - a security-review-clean default.
- Static entry classes, `internal` helpers, `sealed` records/DTOs - the exact surface shape of
  the existing `IO` code, so no new analyser categories are triggered.
- Diagnostics are returned as `IReadOnlyList<string>` notes (the `MatpowerParseResult` pattern),
  not thrown for soft issues; only structural errors throw.

A green `dotnet build` with zero warnings is a merge precondition, asserted by the same CI gate
that guards the rest of the tree.

---

## 18. Seams to extend and explicit out-of-scope

Left as clearly-marked extension points, each behind a note-and-skip today (never a silent
approximation):

- **N-winding transformers**: a star-point auxiliary `Bus` + N pi branches. The
  `CgmesNetworkBuilder.BuildTransformers` pass is the seam.
- **Full node-breaker topology processing**: this phase collapses `ConnectivityNode`s via the TP
  association; a native breaker/switch model (with `Discrete` switch states driving
  `Branch.InService`) is a later phase.
- **Sequence / short-circuit data (r0/x0)**: for a future three-phase / unbalanced DSSE
  (Phase 4). The reader already carries the triples; only the mapping is deferred.
- **DY (dynamics) profile**: `Generator.H` and machine models. Fusing DY into estimation would
  cross the `Dynamics -> Estimation` review gate (invariant #5) and must go through
  `RestrictionReview` first - out of scope for a format translator.
- **CIM difference models, boundary-set stitching, multi-authority merges, DL/GL diagram
  layout**: recognised, unmapped, counted in notes.
- **`Oltc` export** from `RatioTapChanger.TapChangerControl`: the import path can already build
  `GridModel.Oltcs`; the export inverse is a small follow-up.

---

*End of specification. Implementation follows this document; deviations are recorded back into it
and, where they touch a dated seam, cross-checked against `UnderstandingRestrictions.md` and
`RestrictionReview.Reviewed` (`src/GridSim.Core/Diagnostics/RestrictionReview.cs`) before merge.*
