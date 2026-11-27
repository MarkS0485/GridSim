# 41. Scripting object model and API

GridSim's numbers can be driven from programs in three ways that share one rule: nothing is computed twice. A script, a
Python program or any other client asks the same Core entry points the command line uses, and every number comes back as
the bits GridSim computed. This chapter describes the object model in Core, protocol level 2 of `gridsim rpc`, the Python
`ModelSession` and `psspy` layer, and the STUDIES extension of the scripting language. User-facing detail is in
[manual ch. 19](<../manual/0019 CLI reference.md>) (rpc, Python) and [manual ch. 35](<../manual/0035 Scripting.md>)
(scripting).

## 41.1 The object model (`GridSim.Core.Api`)

`CaseEditor` holds one immutable `GridModel`, an ordered edit log and a revision counter. Five kinds of element are
addressed, by text keys compared ordinally and listed in model order (never hash order):

| Kind | Key | Attributes (read only marked ro) |
|---|---|---|
| bus | the bus id, `"5"` | `type` (1 PQ, 2 PV, 3 slack), `vm`, `va_deg`, `base_kv` (ro), `name` (ro), `vmin`, `vmax` |
| generator | `<bus>:<machine id>` for a PSS/E case, else the model name, else `G<bus>`; a repeat gets `#2`, `#3`, ... | `pg`, `qg`, `vg`, `qmin`, `qmax`, `pmin`, `pmax`, `mbase`, `status` |
| load | the id of a bus with demand when the case was loaded | `pd`, `qd`, `status` |
| branch | `<from>-<to>:<circuit>` for a PSS/E case (a three-winding winding adds `-<third>` and `:<winding>`), else the name, else `<from>-<to>`, with `#n` on a repeat | `r`, `x`, `b`, `tap`, `shift_deg`, `rate_a`, `rate_b`, `rate_c`, `status` |
| shunt | the id of a bus with Gs or Bs when the case was loaded | `gs`, `bs` |

Values are the model's own units: MW, MVAr, MVA, per unit on the system base, degrees. A write takes finite numbers; status
is 0 or 1 and a bus type 1, 2 or 3. Every write builds new element records with C# `with` and replaces the model, so a
study never sees a half-applied edit; the values of one write are all checked before any is applied, and the edited model
is checked as the loaders check one (an error the case did not already have refuses the write). A refused write leaves
the model, the log and the revision exactly as they were. `Set` never adds or removes an element: status 0 takes one out
of service, which keeps every key stable. `Add` and `Delete` (41.1a) add and remove elements. A load taken out keeps its set-point, as the scripting session does: the bus
draws nothing and `pd` / `qd` read and write the kept value until status 1 restores it.

Because an edit replaces records and an undo restores equal records, an edit followed by its undo solves to the same bits
as the case as loaded, and an edit made through the object model solves to the same bits as the same edit made in the
source file (tested on case9: bus 9 demand to zero and back).

### 41.1a Adding and deleting elements

`CaseEditor.Add(kind, key, values)` and `CaseEditor.Delete(kind, key, cascade)` work on a draft of the model and change
nothing unless every rule holds and the edited model passes the validity check. One accepted call is one revision; every
change it makes is logged under that revision, in order, with the attribute `add` or `delete` (before null, after 1 or 0).

| Kind | Add: key and attributes (`ElementAttributes.AddFieldsOf`) | Delete |
|---|---|---|
| bus | an unused id; `base_kv` (required, above 0), `type` (1), `vm` (1), `va_deg` (0), `vmin`, `vmax`. A slack is refused when the case has one. Appended. | Refused while a branch, unit, load or shunt refers to it, unless `cascade`, which deletes its branches (ascending index), its units, its load and its shunt first, each logged. |
| generator | `bus:id`, new at an existing bus; `pg`, `qg`, `vg`, `qmin`, `qmax`, `pmin`, `pmax`, `mbase`, `status` (the `Generator` defaults). Appended to the fleet. A PQ bus that gets an in-service unit becomes PV, logged as its own edit. | Removed. A PV bus left with no in-service unit becomes PQ (logged). The last unit, or the last in-service unit, at a slack bus is refused. |
| load | the id of a bus with no demand; `pd`, `qd` (one not zero), `status`. A second load is refused: "GridSim holds one load per bus; use set to change it". | The bus demand is zeroed and the key removed. |
| branch | `from-to:circuit`, both buses existing, the key new; `x` (required), `r`, `b`, `tap`, `shift_deg`, `rate_a`, `rate_b`, `rate_c`, `status`. Giving `tap` or `shift_deg` makes it a transformer. Appended. | Removed. A tap changer, transformer flow control or zero-sequence mutual record that names it makes the delete refused, unless `cascade`, which removes those records too; a record kept must still find the branch it drove. |
| shunt | the id of a bus with no fixed shunt; `gs`, `bs` (one not zero). | Gs and Bs are zeroed and the key removed. |

A new element takes its key as its model name, so a fresh `CaseEditor` over the edited model keys it the same way. Every
existing key keeps its element. Adds append, so they never move an index. A delete moves the `ElementKey.Index` of later
elements of the same list down by one (for a bus: every later bus, load and shunt) and of no other element; loads and
shunts stay listed in bus order, and a load taken out of service keeps its set-point across a bus delete.

The validity check is `Set`'s, with one exception: an island with no slack bus. Building or taking apart a network passes
through such states (a new bus has no branch yet; deleting the branch to a bus leaves it alone), so a structural edit
accepts them. The level 1 `solve` of such a case does not converge, and a `study` refuses it, naming the island. The
islands an accepted add or delete leaves are then counted with the case's own errors, so a later `Set` is checked against
the case as it stands; a `Set` that makes a new island is still refused.

A bus is not deleted from a case that carries HVDC, FACTS, SVC or induction machine records, and nothing is deleted from a
case that carries authored protection or asset dynamics, because those records name elements the editor does not re-map.

Verification (`Rc5Wp15ObjectModelAddDeleteTests`): a one-bus slack case with bus 2, branch 1-2:1 (X 0.1 pu) and a 50 MW,
20 MVAr load added equals the same model built directly, record for record, and solves to the same bits, with
V2 = 0.9782482306186262 and angle -2.929765360158135 degrees, the closed form V2^2 = [(1 - 2QX) + sqrt((1 - 2QX)^2 -
4X^2(P^2 + Q^2))]/2 evaluated with mpmath to 40 digits (asserted to 1e-9); deleting the three again leaves no difference
from the case as loaded (`AttributeDifference`); a cascade logs branch, load and bus in that order; after a branch delete on
case9 every other branch key reads the same attributes, bit for bit.

`LoadFlowView` reads a `PowerFlowResult` in the same terms: per bus the solved magnitude and angle by id, per branch the
result's own `LineFlows` (index-aligned with the model's branches). Nothing is re-derived.

## 41.2 `gridsim rpc --protocol 2`

Level 1 (`gridsim-rpc/1`, the default and `--protocol 1`) is unchanged byte for byte: the same operations, the same pinned
operation list, the same responses, handles `case-1`, `case-2`, ... per session. Level 2 answers every level 1 operation as
level 1 does apart from the label `gridsim-rpc/2` and the handle. A level 2 host draws a random 64-bit token when it starts
(one per process on stdio, one per connection over `--listen`); its handles are `case-<token>-1`, `case-<token>-2`, ...,
and its `hello` gives the token as `host`. A handle issued by another host, or by the same program before a restart, is
refused as "not issued by this host", so it can never reach another case. Level 2 adds eight operations over the
`CaseEditor` of a held case, all listed by `hello` (`add` and `delete` last, since 2.1.110):

- `elements` {handle, kind}: every element of the kind, key first, with every attribute.
- `get` {handle, kind, key, attributes?}: the attributes asked for. On a branch, `p_from_mw`, `q_from_mvar`, `p_to_mw`,
  `q_to_mvar` and `loading_pct` read the pf solve (the solver and options of `solve`) of the case as it stands at the
  current revision; they are read only and requested by name. On a generator, `p_solved_mw` and `q_solved_mvar` read the
  unit's output from the same solve (`SolvedGeneration.UnitOutput`: the bus net injection plus the bus load at the solved
  voltage; a lone unit takes the whole bus, several share it by that method's rule; an out-of-service unit is refused).
- `set` {handle, kind, key, values}: write attributes, return the new revision. `solve` then solves the edited case.
- `edits` {handle}: the log (revision, kind, key, attribute, before, after).
- `add` {handle, kind, key, values} and `delete` {handle, kind, key, cascade?}: `CaseEditor.Add` and `CaseEditor.Delete`
  (41.1a), with `set`'s reply (kind, key, revision). They change only the held model in memory, read and write no file,
  and take the same handle check as `set`; a refused one changes nothing. At level 1 they are unknown operations.
- `study` {handle, verb, args, timeout_s?}: one of `pf`, `nminus1`, `nminus2`, `iec60909`, `fault`, `acopf-full`, `dcopf`,
  `scopf`, `stability`, run as `run-verb` runs a verb. With no edit the host passes the case argument it loaded, so the
  document is the command line's own. With edits it writes the model as a JSON v2 case to a temporary folder, reads it
  back and writes it again. It runs the verb on the first copy only when two checks pass. First, every object model
  attribute of every element of the copy read back equals the held model's, doubles bit for bit
  (`CaseEditor.AttributeDifference`); this covers every value an edit can change. Second, the two writes are
  byte-identical: write-read-write is idempotent, so the writer and reader agree with each other. Otherwise the study is
  refused, naming the first difference. Neither check shows that a field outside the object model (a dynamics class, a
  zero-sequence value) survives the JSON v2 writer; a study verb that reads such a field should be run on the saved case
  with `run-verb`. The host's
  own case path is taken out of the document's `args` and replaced by `<case>` in the printed text. A model the JSON v2
  writer cannot hold (for example a bus with no positive base voltage, as in case14) is refused for a study after an edit;
  save it as RAW and run the verb on the file.
- `save` {handle, path, format}: `raw33`, `raw34`, `raw35` or `v2`, never over a file with other content (the same bytes
  are accepted, unchanged), never at a name where a link stands, and always by creating the file new, so nothing is ever
  written through a link. Over `--listen` every file written (for `v2`, the manifest and its sibling `.network.json`) must
  lie inside the host's working folder (the scripting path rule, `PathConfinement`) and must not be a link, and network
  and device paths are refused. Every target is checked before any is written.

`dynamics-study` names its RAW and DYR files by SHA-256 inside its study file, so it is reached through `save` and
`run-verb`. Responses carry no time, process id or temporary path. At level 2 the host token makes the handles and the
`hello` differ from run to run; nothing else does.

Verification: level 1 transcripts are compared byte for byte with and without `--protocol 1` and against the RC4 hello
line, and the level 1 `load` and `solve` lines are pinned in full against the documented built-in identity and the `pf`
verb's result file; an edit then `solve` equals `pf` on the MATPOWER file edited by text, and the undo equals `pf` on the
original, bit for bit; `study pf` after a no-op edit equals `pf` on the original file, bit for bit, for case9, case118 and
a RAW 35 case; a one-ulp change is found by the attribute comparison; two simultaneous hosts, and a host restarted, refuse
each other's handles; a generator's solved output equals the solve's bus injection bit for bit where the bus has no load;
and a link at the `.network.json` name is refused over `--listen`. The level 1 and level 2 transcripts of
`Rc5Wp15ObjectModelAddDeleteTests` (hello, load, elements of every kind, get with flows and solved output, set and a
refused set, edits, solve, study pf, an unknown operation) hash to the values the 2.1.108 build gave before `add` and
`delete` existed, once the built-in case identity, which names the build's version, is set to that build's, and once
the two names 2.1.110 added to level 2's `hello` list and unknown-operation message are taken out again.

## 41.3 Python: `ModelSession` and `psspy`

`gridsim.ModelSession` starts `gridsim rpc --protocol 2` and refuses a host of another protocol. `open(case)` gives a
`Case` whose `buses`, `generators`, `loads`, `branches` and `shunts` are read-only mappings of `Element` objects;
reading an attribute is `get`, assigning one is `set`. `solve()` gives a `LoadFlowResult` (`bus(id).vm`,
`branch(key).p_from`, `generator(key)` for a unit's solved P and Q); `contingency()`, `short_circuit()` and `opf()` give
the verb's own `Result`; `save()` and `rms(study)` are the `save` operation and a `dynamics-study` run. `add(kind, key, **values)` and
`delete(kind, key, cascade=False)` add and delete elements and return the new revision. Standard library only.

A case object belongs to the session that loaded it and to that session's host instance. A level 1 `CaseHandle` and a
level 2 `Case` remember both, and a session refuses one that is not its own, or that was loaded before its host
restarted, before anything is sent; `release()` of such a case marks nothing released. This matters most at level 1,
whose handles are `case-1`, `case-2`, ... in every host, so the id alone cannot tell two hosts apart; at level 2 the
host's token in the id is a second guard. A plain id string is still accepted and sent as it is. `Session.run_verb` makes
the one-shot wrapper's flag check, so `--out` passes for the verbs whose own output it names.

`gridsim.psspy` maps a listed subset of psspy calls onto these operations (manual ch. 19 has the table). The calls that
add, change and delete elements (`bus_data_4`, `load_data_6`, `machine_data_4`, `plant_data_4`, `branch_data_3`,
`two_winding_data_6`, `shunt_data`, `purgbrn`, `purgmac`, `purgload`, `purgshunt`), the complex arrays (`abuscplx`
`VOLTAGE`, `amachcplx` `PQGEN`, `abrncplx` `RX`) and the iteration calls (`inibus`/`nxtbus`, `inimac`/`nxtmac`,
`inibrn`/`nxtbrn`) each name two public sources for their argument order: open-source repositories that call them, and the
API manual's text as excerpted in a public repository. `load_data_7` (no public source) and `extr` (it removes a
subsystem, and subsystems are not mapped) are refused. Return values
follow psspy (`ierr` or `(ierr, value)`). For most calls the non-zero ierr values are GridSim's own (1 not found, 2 bad
argument, 3 not solved or stale, 9 not supported), not PSS/E's. The deletes, the complex arrays and `inimac` return
PSS/E's own codes for every condition the API manual names, and GridSim's 9 (11 in `abrncplx`, where PSS/E's 9 is an
invalid string) for a request PSS/E would answer and GridSim cannot. Each mapped call names its source in the code: the section of the
PSS/E 34 Application Program Interface manual, as publicly posted, or a public example call on the psspy.org forum. A
layout no public source confirms is refused with ierr 9 (`readrawx`, the positional form of `rawd_2`, the `lodtyp` of
`load_chng_5` other than blank), never guessed. Every mapping is verified against GridSim's own verbs, not against PSS/E.
Where the object model cannot answer exactly as PSS/E would, the call returns ierr 9: a load id on a bus with more than
one load record (loads are aggregated per bus), a load with a constant-current or constant-admittance part, the solved
output of a unit that shares its bus, a line-only or transformer-only branch flag. A call GridSim cannot
honour exactly returns ierr 9 or raises NotImplementedError with the reason; Newton is never substituted for another
solver, and an option array is never ignored silently. `dist_bus_fault` divides by the case's own system base, read from
the RAW or RAWX file. The dynamic calls check an edited case first (written as RAW 33, read back and compared attribute by
attribute, bit for bit, then written again and compared byte for byte apart from the two title lines); a case that does
not survive is ierr 9. A run with no event yet holds only the undisturbed time series: dynamics-study needs an event, so a
branch trip is placed one step after the pause and every later sample is cut off. The dynamic calls re-run one
`dynamics-study` row from t = 0 to
each pause with every event so far, so a run in steps gives the same bits as one run to the same time.

## 41.4 The STUDIES extension of grammar 1

Grammar 1 is frozen (manual ch. 35, "Grammar 1 compatibility promise"). A script that starts with
`GRAMMAR 1 WITH STUDIES` turns on an extension; without that header the parser, the session and the run record are
exactly grammar 1's, `GRAMMAR 2` is still refused and `STUDY` is still an unknown verb. The header was a syntax error in
grammar 1 ("unexpected WITH"), so accepting it keeps the promise.

| Statement | Core entry point, as the CLI calls it | Readings |
|---|---|---|
| `STUDY CONTINGENCY AS a` | `nminus1`: the study solver in the OLTC loop, `ContingencyAnalysis.StudyOptions` with per-bus voltage limits and rating A, netted against the base case | `VIOLATIONS OF STUDY a` (the failing outages, a plain number), `WORST LOADING OF STUDY a` (PU) |
| `STUDY SHORTCIRCUIT BUS "5" AS s` | `iec60909 --bus 5`: three-phase, maximum, tb 0.10 s, Tk 1.0 s, Method B, f0 the case's | `IK OF BUS "5" IN STUDY s` (KA) |
| `STUDY OPF AS o` | `acopf-full`: the interior point, 150 iterations | `OBJECTIVE OF STUDY o` (a plain number), `P OF GENERATOR "G1" IN STUDY o` (MW) |
| `STUDY SCOPF AS c` | `scopf`: the DC preventive SCOPF, every branch outage, rating A, the fuel-type costs, constraint generation, 50 rounds | `OBJECTIVE OF STUDY c` (the total cost per hour), `SECURED OF STUDY c` (the outages secured; plain numbers) |
| `STUDY RMS DYNAMICS "f.dyr" FAULT BUS "4" AT 0.1 S CLEAR AT 0.15 S FOR 3 S AS r` | `dynamics-study`: one events row, a bolted fault at the bus cleared in place | `STABLE OF STUDY r` (1 or 0), `FINAL FREQUENCY OF STUDY r` (HZ), `MIN VOLTAGE OF BUS "4" IN STUDY r` (PU) |
| `STUDY CCT DYNAMICS "f.dyr" FAULT BUS "4" AT 0.1 S FOR 3 S AS c` | `dynamics-study`: one critical clearing row, the fault cleared in place, searched up to 1.0 s | `CCT OF STUDY c` (S: the longest clearing time found stable) |

`REPORT STUDY a` prints the study's values. A study runs on the plant as edited and keeps its values under its name; a
study with no answer (a base case or an OPF that does not converge) is an execution error and keeps nothing. KA is the unit
of the IK reading only: no number can be written in it. The extension's words are keywords only in such a script. The run
record gains `grammarExtensions: ["STUDIES"]` and a `studies` array (each value in round-trip text) only when the header is
present; `grammarVersion` stays "1". `SCOPF` and `SECURED` are read in place (`SECURED` only when `OF STUDY` follows) and
are not reserved, so a STUDIES script that uses either as a name runs as before; scripts without `STUDY SCOPF` give the run
record they gave in 2.1.108 (pinned by hash).

`STUDY RMS` and `STUDY CCT` (2.1.110) build the `DynamicsStudyRequest` the `dynamics-study` verb builds from a study
file and run it through the same `DynamicsStudyRunner.Run`, so the numbers are the verb's by construction. The case must
be a PSS/E .raw file, since the request names machines and branches by RAW identity. It is read again from its file,
checked against the SHA-256 that `LOAD CASE` recorded, and parsed as the verb parses it; when the script has edited the
plant, the plant as edited takes the case's place (same elements, same order). The settings are fixed: initialisation
1e-8 pu and 100 iterations with unmodelled records refused, step 0.005 s, slip limit 2 pi rad, angle spread 180 degrees,
the first in-service machine as angle reference, channels its frequency then every bus voltage in bus order; CCT 18
halvings and 16 scan steps. `FINAL FREQUENCY` is the reference machine's frequency at the last sample, `MIN VOLTAGE` a
bus's least voltage magnitude over the run, `CCT` the coarse bracket's stable end (a run that found no bracket has no
CCT to read). The DYR file is recorded as an input of the run. `RMS` was already a keyword; `CCT`, `DYNAMICS`, `STABLE`,
`FINAL` and `VOLTAGE` are read in place and are not reserved. A study that does not run (a refused model, a power flow or
equilibrium that fails, a failed row) is an execution error and keeps nothing.

Verification: each study's values equal the Core calls made directly on the same model, bit for bit (in the record's
round-trip text), and the same Core calls equal the CLI verbs' result files on the same MATPOWER file, bit for bit.
`STUDY RMS` and `STUDY CCT` on case14 with its study DYR give the final frequency, every bus's least voltage and the CCT
bracket of the `dynamics-study` verb's result file, bit for bit (`Rc5Wp15DynamicStudyTests`). A
reading converts the double to the script's decimal as every grammar 1 reading does.

## 41.5 Scope

Not covered: more than one load or one fixed shunt per bus (the model folds them onto the bus); adding or deleting
three-winding transformers, HVDC, FACTS or switched shunts (these stay editable only through their files); STUDY RMS and
STUDY CCT in scripts (an RMS run needs the dynamic data a script's RMS engine loads, and its equivalence with
`dynamics-study` is not yet shown; use `SELECT ENGINE RMS` and `STEP`, or `Case.rms`); areas, zones and owners; switched shunts, tap changers, HVDC and FACTS
attributes; the PowerFactory Python API (its object model is proprietary and not mirrored); psspy's ierr numbers outside the
deletes, the complex arrays and `inimac`, and its full option arrays. The psspy layer is checked against GridSim's own verbs only.
