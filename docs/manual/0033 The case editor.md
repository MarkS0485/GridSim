# 33. The case editor

The case editor (`GridSim.SchemaEditor`) is a full-screen, keyboard-driven inventory manager for case files. It opens
a v1 case folder, a v2 case manifest or a v2 network file, shows the case as a hierarchy (Generation, then fuel type,
then station, then unit; Buses by voltage level; Demand; Lines; and so on), and lets you change values, add,
duplicate, delete and rename records, then save them back. It edits the files themselves, byte for byte: a one-field
change alters one value in one file and nothing else, so `git diff` shows exactly what you did. Use it when you want to
adjust a case by hand without hunting through thousands of lines of JSON, and when you want every change checked
against the schema (and, for a v1 case folder or a v2 case manifest that imports no library templates, the loader)
before it reaches disk. A lone v2 network file, and a v2 manifest that imports library templates, are checked against
the v2 schemas and the editor's semantic checks only: the loader is not run on save, the save report says so in a
note, and you confirm the case with `gridsim validate` or `gridsim pf` (see [Saving and checking](#saving-and-checking)).

## Before you start

- Build the solution (chapter 1). The editor is the project `src/GridSim.SchemaEditor`; when it is not installed,
  `GridSim.SchemaEditor` in this chapter means `dotnet run --project src/GridSim.SchemaEditor -c Release --`.
- Use a real console window of at least 80 columns by 24 rows (Windows Terminal or the Windows console host). The
  editor refuses to start when its input or output is redirected; scripts use `--check` instead.
- The licence applies as it does to every verb (chapter 4): the case's bus count is checked when it opens and again
  when it is saved, because Add and Duplicate can take a case over the limit. Bundled Networks never count, and a
  bundled case is the shipped files themselves: after a save that changes one of its files it no longer matches the
  shipped-content manifest `data/bundled.sha256`, and it counts like any user network the next time it is loaded.
- Editing a bundled case (anything under `data/`) moves numbers that tests and documentation pin. The title bar shows
  `[BUNDLED]` and the first change says so once. Work on a copy unless you mean to change the shipped data.

## Worked example

Open the bundled `data/uk` case:

```
GridSim.SchemaEditor data/uk
```

The top level lists the case's sections, one per file the loader reads, with each file's record count. Files the
loader does not read are listed under "Not part of the case" and are never written.

```
GRIDSIM CASE EDITOR 1.95.113  data/uk (v1)  20 buses  [BUNDLED]
Case
  section               records   file
  System                1         system.json
  Buses                 20        buses.json
  Generation            29        generation.json
  Demand                10        demand.json
  Lines                 14        transmission.json
  Transformers          10        transformer.json
  Tap changers          10        onloadtapchanger.json
  Asset classes         -         asset-classes.json (absent)
  Asset bindings        -         asset-bindings.json (absent)
  Protections           -         protections.json (absent)
  Boundaries            4         boundaries.json
  Not part of the case  0         files the loader does not read (never written)








Case opened. F1 lists the keys.
F1 Help  Enter Open  Esc Up  F2 Edit  U Undo  F9 Check  F10 Save  F12 Quit
```

Enter on Generation lists the fuel types with their station and unit counts and the summed capacity:

```
GRIDSIM CASE EDITOR 1.95.113  data/uk (v1)  20 buses  [BUNDLED]
Case > Generation
fuel type             stations  units   capacity MVA
biomass               1         1       2600.0
ccgt                  7         7       25000.0
hydro                 1         1       1300.0
interconnector        10        10      10300.0
nuclear               4         4       8100.0
offshore              2         2       10000.0
onshore               3         3       8000.0
solar                 1         1       4000.0












row 1/8
F1 Help  Enter Open  Esc Up  F2 Edit  U Undo  F9 Check  F10 Save  F12 Quit
```

Enter on `ccgt`, then on Pembroke CCGT, then on its one unit opens the unit's form. Each row is one field in schema
order: its label with the unit, the value exactly as stored in the file, and its state. An absent field shows what the
loader uses instead; the message line shows the help for the highlighted field.

```
GRIDSIM CASE EDITOR 1.95.113  data/uk (v1)  20 buses  [BUNDLED]
Case > Generation > ccgt > Pembroke CCGT (1 units) > #26 'Pembroke CCGT'
field                                 value                         state
Name                                  "Pembroke CCGT"
Bus                                   10                            required
Technology                            "ccgt"
Capacity (MVA)                        2000
Output (MW)                           (absent: derived, see help)
Capacity factor (0..1)                0.60
Voltage setpoint (pu)                 1.04
Minimum reactive power (Mvar)         (absent: derived, see help)
Maximum reactive power (Mvar)         (absent: derived, see help)
Inertia constant H (s)                5.0
Role                                  (absent: "pv")
Dynamics class                        (absent: derived, see help)
Converter interfaced                  (absent: derived, see help)
Converter fault current (x rated)     (absent: derived, see help)
Power-flow-only device                (absent: false)
Unit transformer reactance (pu on uni (absent: 0)
Grid forming                          (absent: false)



name: Unit or station name. Units sharing a type and a name form one station in
F1 Help  Enter Open  Esc Up  F2 Edit  U Undo  F9 Check  F10 Save  F12 Quit
```

Enter on Capacity (MVA) starts an in-place edit. Typing replaces the value (F2 or E keeps the current text instead):

```
GRIDSIM CASE EDITOR 1.95.113  data/uk (v1)  20 buses  [BUNDLED]
Case > Generation > ccgt > Pembroke CCGT (1 units) > #26 'Pembroke CCGT'
field                                 value                         state
Name                                  "Pembroke CCGT"
Bus                                   10                            required
Technology                            "ccgt"
Capacity (MVA)                        2100
Output (MW)                           (absent: derived, see help)
Capacity factor (0..1)                0.60
Voltage setpoint (pu)                 1.04
Minimum reactive power (Mvar)         (absent: derived, see help)
Maximum reactive power (Mvar)         (absent: derived, see help)
Inertia constant H (s)                5.0
Role                                  (absent: "pv")
Dynamics class                        (absent: derived, see help)
Converter interfaced                  (absent: derived, see help)
Converter fault current (x rated)     (absent: derived, see help)
Power-flow-only device                (absent: false)
Unit transformer reactance (pu on uni (absent: 0)
Grid forming                          (absent: false)



Capacity (MVA): Enter accepts, Esc cancels.
Enter Accept  Esc Cancel  Left/Right Move  Home/End  Bksp/Del Erase
```

Enter accepts. The value is checked (a number, valid for the field's schema), written as the shortest literal that
reads back as the same number, and the message line reports the change; the title bar shows the modified file:

```
data/uk (v1)  20 buses  [BUNDLED]  [MODIFIED: 1 file(s)]
Case > Generation > ccgt > Pembroke CCGT (1 units) > #26 'Pembroke CCGT'
field                                 value                         state
Name                                  "Pembroke CCGT"
Bus                                   10                            required
Technology                            "ccgt"
Capacity (MVA)                        2100
Output (MW)                           (absent: derived, see help)
Capacity factor (0..1)                0.60
Voltage setpoint (pu)                 1.04
Minimum reactive power (Mvar)         (absent: derived, see help)
Maximum reactive power (Mvar)         (absent: derived, see help)
Inertia constant H (s)                5.0
Role                                  (absent: "pv")
Dynamics class                        (absent: derived, see help)
Converter interfaced                  (absent: derived, see help)
Converter fault current (x rated)     (absent: derived, see help)
Power-flow-only device                (absent: false)
Unit transformer reactance (pu on uni (absent: 0)
Grid forming                          (absent: false)



capacityMva 2000 -> 2100 (U to undo). Note: this is a Bundled Network; tests and
F1 Help  Enter Open  Esc Up  F2 Edit  U Undo  F9 Check  F10 Save  F12 Quit
```

F12 (or Q, Esc at the top, or Ctrl+C) quits. With unsaved changes it asks first:

```
data/uk (v1)  20 buses  [BUNDLED]  [MODIFIED: 1 file(s)]
Case > Generation > ccgt > Pembroke CCGT (1 units) > #26 'Pembroke CCGT'
field                                 value                         state
Name                                  "Pembroke CCGT"
Bus                                   10                            required
Technology                            "ccgt"
Capacity (MVA)                        2100
Output (MW)                           (absent: derived, see help)
Capacity factor (0..1)                0.60
Voltage setpoint (pu)                 1.04
Minimum reactive power (Mvar)         (absent: derived, see help)
Maximum reactive power (Mvar)         (absent: derived, see help)
Inertia constant H (s)                5.0
Role                                  (absent: "pv")
Dynamics class                        (absent: derived, see help)
Converter interfaced                  (absent: derived, see help)
Converter fault current (x rated)     (absent: derived, see help)
Power-flow-only device                (absent: false)
Unit transformer reactance (pu on uni (absent: 0)
Grid forming                          (absent: false)



Unsaved changes (1 file(s)). Save before quitting? Y yes, N no, Esc cancel.
Answer with the letters shown  Esc Cancel
```

Integrity is enforced at each operation. Deleting a bus that generators, demands or branches refer to is refused, and
the message names the referrers (W lists them all):

```
data/uk (v1)  20 buses  [BUNDLED]  [MODIFIED: 1 file(s)]
Case > Buses > 400 kV
#      id      name                          kV       type   vm
#0     1       North Scotland 400kV          400
#1     2       South Scotland 400kV          400
#2     3       North England 400kV           400
#3     4       Yorkshire & Humber 400kV      400
#4     5       North Wales & Mersey 400kV    400
#5     6       Midlands 400kV                400
#6     7       East Anglia 400kV             400
#7     8       London & South East 400kV     400
#8     9       South 400kV                   400
#9     10      South West & S Wales 400kV    400










Delete refused: 4 record(s) refer to buses.json #0 bus 1 'North Scotland 400kV':
F1 Help  Enter Open  Esc Up  F2 Edit  U Undo  F9 Check  F10 Save  F12 Quit
```

`--check` runs the save gate without opening the editor and without writing anything:

```
GridSim.SchemaEditor data/uk --check
```

```
CHECK PASSED: the case loads, validates and is within the licence (20 buses).
exit code 0
```

## Reading the screen

| Row | What it shows |
|---|---|
| Top | Version, the case path and format (v1 or v2), the bus count, the licence tier when there is room, `[BUNDLED]` for a Bundled Network and `[MODIFIED: ...]` for files with unsaved changes (a count when the names do not fit). |
| Second | Where you are: `Case > Generation > ccgt > ...`. When it is too long the leading levels give way to `... > `. |
| Third | The column headers of the list. |
| Body | The list. The highlighted row is in reverse video. A `*` before a section marks unsaved changes in its file; a `*` after a fuel type marks units with no type member, which the loader reads as `ccgt`. |
| Message line | The last result or refusal, a question, a prompt, or the help for the highlighted row. |
| Bottom | The keys for the current mode. |

In a form the state column says `required` for a member the schema requires, `spelt 'X' (L normalises)` for a member
whose name differs from the schema's spelling only by case, and `not in the schema (read-only)` for a member the schema
does not know.

## Keys

Every command has a plain letter as well as any function key, because terminals capture function keys and Ctrl
chords. F1 or `?` lists them all.

| Key | Action |
|---|---|
| Up, Down, PgUp, PgDn, Home, End | Move |
| Enter or Right | Open the row; on a field, edit it (typing replaces the value) |
| Esc, Backspace or Left | Up one level; at the top, quit |
| F2 or E | Edit the field keeping its current text |
| Space | Toggle a yes / no field, or step a list field to its next value |
| F4 or P | Pick a value of a list or yes / no field |
| Del | Reset the field to absent, so it takes its default |
| Ins or A | Add a record |
| F5 or D | Duplicate the record |
| F8 or X | Delete the record (asks first) |
| R | Rename a station, or a key (bus id, line, transformer or asset-class name) with every reference |
| / | Filter the list (text anywhere in the row, any case); an empty filter shows all |
| F3 or N | Next row matching the filter |
| F | Find a name in every section |
| G | Go to a bus by id |
| J | Jump from a bus field to that bus |
| W | Where used: everything that refers to this record |
| U or Ctrl+Z | Undo; Shift+U or Ctrl+Y redo |
| L | Normalise member-name spelling to the schema's |
| F9 or K | Check the case (no write) |
| F10 or S | Save |
| F12, Q or Ctrl+C | Quit (asks about unsaved changes) |

## Editing values

- Whole numbers are digits with an optional minus sign, inside the 32-bit range. Numbers follow JSON: `1300`, `-0.5`,
  `1.2e-3`; no leading `+`, no leading zeros, no thousands separators, no comma decimal point. The editor writes the
  shortest form that reads back as exactly the same number, so `1300.0` is written `1300`.
- Typing the value a field already holds (`0`, `0.0` or `0.00` over a zero) changes nothing and adds no undo step.
- Text is written as typed; only quotes, backslashes and control characters are escaped, so names with `&` or accented
  letters look like their neighbours in the file.
- A list field accepts one of its listed values (any case; it is written in the schema's spelling).
- An object, list or map field is edited as JSON text with F2 or E.
- A change is refused, with the reason, when the record would gain a schema error, when a bus field names a bus that
  does not exist (G goes to a bus), or when a line, transformer or asset-class name names nothing. A record's existing
  problems never block an edit to another of its fields.
- Changing a unit's name or type moves it to another station; the message says which.
- Each change is one undo step; up to 1000 are kept, and they survive a save. A file whose changes are all undone is
  no longer modified.

## Adding, duplicating, deleting and renaming

- Add asks for each required member the context does not supply. In a station the name, type and the bus of the
  selected unit are filled in; in a voltage level the base voltage; a new bus gets the next free id. A section whose
  file does not exist yet is created by its first record.
- Duplicate copies the record directly after itself, keeping its layout. A copied bus gets the next free id; a copied
  line, transformer or asset class keeps its name, and the message reminds you to rename it.
- Delete asks first and is refused while anything refers to the record.
- Rename on a station renames every unit of it. Rename on a key shows how many references follow and asks before it
  changes them all in one step. It is refused when the new key is already used, and for a line or transformer name
  shared by several branches when a boundary or protection refers to it, because those match by name only. Of the tap
  changers on a shared name, only those the solver assigns to the renamed branch follow it.

## Saving and checking

F10 saves only the files you changed, after these checks, in order:

1. No changes: "No changes to save", and nothing is written.
2. Any file of the case that changed on disk since you opened it (one you edited, or one someone else changed: another
   editor, a `git checkout`) refuses the save; reopen the case to see the other change. Save and F9 Check both compare
   every file with disk, so the case that is saved is always the combination that was checked.
3. The changed files are checked against their schemas. The whole case is staged in a temporary folder and loaded
   exactly as every `gridsim` verb loads it. The licence is checked for the bus count. Warnings from the model's
   structural checks that the case did not have when it opened need an explicit Y.
4. Any error: "NOT SAVED: n error(s)", the files on disk are unchanged, and the error list opens; Enter on an error
   opens the record it names.
5. Each file is written beside its target as `<file>.gridsim-new`, read back and compared, and only then renamed over
   the target. If anything fails part-way, the files already replaced are restored from memory and the message says so.
   A `.gridsim-new` file left behind by an interrupted save is reported the next time the case is opened.

A member name spelt differently from the schema (the loader ignores case, the schema does not) is reported by the
check; once that file has unsaved changes it cannot be saved until L normalises the spelling.

F9 runs the same checks over every file without writing. `--check` does so from the command line. Exit codes: 0 the
check passed; 1 the case cannot be read (a missing folder or file, malformed JSON); 2 a usage error, a licence refusal
or a console that is not interactive; 3 the check found errors.

## v2 documents

Give the editor a `*.case.json` manifest (`gridsim-case/2`) or a `*.network.json` file (`gridsim-network/2`) instead
of a folder. A manifest opens with every network file it lists; a listed file outside the manifest's folder, or listed
twice, is refused, and a listed file that does not exist opens as absent and is reported. The top level shows a Case
form for the manifest, then the network sections merged across the network files (with a file column when there is
more than one), then the list of network files. Bus references, renames and delete refusals span every network file of
the case.

A v2 save checks the manifest and every network file against their schemas and then the case as a whole: every
listed network file exists, bus ids are unique, bus references resolve, line and transformer names and boundary names
are unique (generators may share names, as stations do), and each ZIP triple sums to 1. A tap changer, boundary branch
or protection target that names no branch is a warning. A provenance tag that is an output tag (`Counterfactual`,
`UnvalidatedAtScale`) written anywhere in a case file is refused. The save then runs the v2 loader on a staged copy of a
case manifest: a case the loader refuses is refused at save with the loader's message, and the report notes the
verdict. A manifest that imports library templates is not loaded, because its imports resolve against the shipped data
folder of the program that loads it, which the editor cannot know; the note says so.

## Limits

- A name is drawn one character per column; wide East Asian characters would misalign the columns.
- v2 generators are one merged list, not the fuel type and station tree of a v1 case, and v2 template references are
  not renamed with the templates.
- There is no "save as" to a new folder.
- The loader no longer has the leniencies it once had (since October 2026, N096 / N097 / N098): an unknown or null bus
  type or generator role, a boundary branch naming no branch, and a boundary branch name that several branches share
  are all refused at load, naming the file, the element and the value, as the editor's schema already refused the first
  two.
