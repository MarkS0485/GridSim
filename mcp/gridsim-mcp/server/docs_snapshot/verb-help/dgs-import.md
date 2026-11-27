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
