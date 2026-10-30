# Case editor design (GridSim.SchemaEditor)

The case editor is a text-mode inventory manager for GridSim case files: it opens a v1 case folder or a v2 case
manifest or network file, shows the case as a browsable hierarchy, edits values, adds, duplicates and deletes
records, and saves them back. This note records how it is built and why. The user guide is
[manual chapter 33](<../../manual/0033 The case editor.md>); the plan and Mark's decisions are in
`review-progress/2026-10/SCHEMA-EDITOR-PLAN.md`.

## Split between Core and the app

| Layer | Where | What it holds |
|---|---|---|
| Lossless text layer | `src/GridSim.Core/IO/Json/Lossless` | `JsonSourceText`, `JsonSourceNode`, `JsonTextEdit`, `JsonTextOps`, `JsonLayout`, `JsonLiteral` |
| Case engine | `src/GridSim.Core/IO/Json/CaseEditing` | `CaseFileSpec`, `BundledSchemas`, `FieldCatalogue`, `CaseSession`, `CaseOps`, `ReferenceIndex`, `StationIndex`, `SaveGate`, `CaseCommitter`, `V2CaseChecks` |
| User interface | `src/GridSim.SchemaEditor` | `EntryPoint`, `ConsoleHost`, `Ui/*` (state machine, renderer, views, key map) |

The engine is in Core because Core and the CLI hold all capability; a later `convert` verb or a lossless model writer
can reuse it. The app references Core only, uses no package, and is the only code that touches `System.Console`
(`ConsoleHost`). Everything in the engine is `internal`; Core grants `InternalsVisibleTo` to the app and the tests.

## The bytes are the model

A hand-maintained case file is not a serialisation of anything: the bundled cases use at least four layouts (hand
aligned `data/uk`, Python `indent=2` `data/gb-full`, STJ-written `data/gb-synthetic` with `&`, Python `data/intl`
with raw UTF-8 names). `JsonNode` would re-escape strings, drop alignment and comments and rewrite every byte of a file
for a one-field edit (pinned by `LosslessTextTests.JsonNode_is_not_lossless_on_the_bundled_files`). So:

- each file keeps its original bytes; `Utf8JsonReader` builds a span tree over them (comments allowed and left in the
  bytes, trailing commas allowed, duplicate member names refused under the file's comparer);
- every change is a list of sorted, disjoint byte patches (`JsonTextEdit`), after which the file is parsed again;
  applying a patch returns its exact inverse, which is the undo step;
- a scalar edit replaces only the value token; a member insert goes after its schema-order predecessor in the record's
  own style; an element removal takes the separator before it; a duplicate copies the source's bytes;
- the BOM, the newline style and the presence of a trailing newline are detected and never changed.

Number input follows Q2: the shortest round-trip literal (`"R"`, invariant) is written, never the typed characters,
and reading it back gives the same 64 bits (P7, ten thousand seeded doubles plus the awkward ones). Strings escape only
`"`, `\` and control characters and refuse unpaired surrogates (P8).

## Schemas: the hybrid model

`schemas/v1` holds one schema per v1 loader file plus `v1-common.schema.json`, under the same validator subset as v2
(no `default` keyword). The schema gives the field list and order, labels (`title`, with the unit), help
(`description`), types, enums, ranges and required members; the loader DTOs give the value an absent field takes, by
reflection over a fresh DTO. Drift guards in `V1SchemaTests` pin every DTO member and its type to its `$def`, the DTO
set to the test projector's, and the embedded copies to the files on disk. Q1 is resolved strict: member names are
case-sensitive in the schema (the loader ignores case), bus `type` and generator `role` are enums, and null is refused;
the `L` command normalises spelling in one undo step. The loader refuses the same nulls by name (a bus `type`, a
generator `role` or a generator `type`: N098, N137) and reads an unlimited generator limit only as the schemas' two
spellings, `"Infinity"` and `"-Infinity"` (`"+Infinity"` is refused since N139). Numeric ranges mirror the v2 network schema, so a v1 case the
editor saves always projects to a valid v2 network. The schema files are embedded in Core
(`GridSim.Core.schemas.v1.*`, `GridSim.Core.schemas.v2.*`).

## Session, operations and integrity

`CaseSession` holds the documents (absent files included), the files a v1 case does not read, leftover
`*.gridsim-new` files, pre-flight results and the undo history (1000 levels). Every change is a `CaseTransaction`:
atomic, over one file or several, one undo step; creating a file is itself an undoable step. Record collections carry
a role (`CaseRoles`: buses, generators, lines, ...), so integrity rules are shared by v1 and v2:

- a bus reference must name an existing bus (in v2, in any network file of the case); a branch reference (an OLTC's
  transformer, a boundary branch, a protection's target) an existing line or transformer; a v1 class reference an
  existing asset class;
- a record is refused only for schema errors an edit ADDS, so an existing problem never blocks an unrelated edit;
- delete is refused while anything refers to the record; a key (bus id, branch name, class name) with referrers
  changes only by Rename, which cascades in one transaction after showing the count;
- a branch rename is refused when the old name is shared and a boundary or protection refers to it by name (gb-full
  has 58 shared transformer names); of the OLTCs on a shared name only those the solver drives on this branch follow,
  by a file-level mirror of `SteadyStateSolver.MapOltcRecordsToBranches` pinned equal on every bundled case.

Stations (decision D3) group generators by effective type and name, ordinal, then file index.

## Save gate and commit

1. No dirty file: "No changes to save", nothing written.
2. A target that changed on disk since open refuses the save.
3. v1: the dirty files against their schemas; the case staged in a temporary folder and loaded with
   `JsonGridLoader.LoadDirectory(dispatch: true)`; the licence for the bus count at the case's real path; model
   warnings pre-flight did not have need an explicit yes. v2: the manifest and every network file against their
   schemas, `V2CaseChecks` (files present and inside the folder, unique bus ids, references resolving, unique branch
   and boundary names per Q4, ZIP sums), the licence, and new semantic warnings. When those pass and the manifest
   imports nothing, the gate runs `V2CaseLoader` on a staged copy and a case the loader refuses is not saved (the
   loader's verdict is also shown as a note); a case that imports library templates is not loaded at save time. Per Q3
   a v2 save does not project to v1. An output provenance tag (`Counterfactual`, `UnvalidatedAtScale`) written anywhere
   in a case file refuses the save.
4. Any error: "NOT SAVED: n error(s)", the disk unchanged, and the error list (Enter opens the record).
5. Commit: each new file is written as `<file>.gridsim-new` with write-through, read back and compared, and only then
   renamed over its target; a failure part-way rewrites the replaced files from the in-memory baselines and says so.
   A multi-file save is still not atomic if the process dies mid-rename; leftovers are reported at the next open.

## Exit codes and modes

`GridSim.SchemaEditor <case-folder | x.case.json | x.network.json> [--check]`: 0 ok, 1 input error, 2 usage error,
licence refusal or a non-interactive console, 3 check failed (PLAN.md binding rule 3). `--check` runs the gate over
every file and writes nothing.

## Limits

- Display width counts one `char` as one column; wide CJK names would misalign (risk R3).
- v2 generators are a flat merged list, not the fuel and station tree of v1; v2 template references are not cascaded.
- "Save as" to a new folder is not offered (Q6, later).
- The loader leniencies the editor exposed were recorded as findings, not fixed here (Q5), and were fixed in the
  October 2026 CLI and loader stream (N096, N097, N098): an unknown or null bus type or generator role, an unknown
  boundary branch name and a boundary branch name shared by several branches are now refused at load.
