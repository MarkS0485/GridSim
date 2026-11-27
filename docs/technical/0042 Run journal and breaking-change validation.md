# 42. Run journal and breaking-change validation

A breaking-change release changes some results on purpose. Each change is listed in the CHANGELOG with its proof and
its magnitude, but a user still needs to know which of their own results moved, by how much, and whether each move is
one of the listed changes. The run journal and `gridsim breaking-change validate` answer that. The journal records the
runs you make. After an upgrade, `validate` re-runs every recorded job on the new build and compares each result with
the recorded one bit for bit. Each change it finds is checked against what the breaking changes in its window claim:
EXPLAINED when it lies within a claim, flagged otherwise (EXCEEDS-CLAIM, AMBIGUOUS or UNEXPLAINED).

The command-line reference is [manual chapter 19](<../manual/0019 CLI reference.md#breaking-change>); configuration
is in [manual chapter 3](<../manual/0003 Configuration.md>). The code is in `src/GridSim.Core/Journal` and, for the
command line, `src/GridSim.Cli/JournalRecorder.cs` and `src/GridSim.Cli/BreakingChangeCommand.cs`.

## The journal

The journal is off by default. It is switched on by the first of these that is set:

1. `--journal <file.jsonl>` on the command line;
2. the environment variable `GRIDSIM_JOURNAL=<file>`;
3. the per-user config file `GridSim/journal.json` (`gridsim-journal-config/1`, with `enabled: true`).

`GRIDSIM_JOURNAL=off` switches it off whatever the other two say. Re-runs started by `validate` use this to keep their
own records out of your journal.

**A journaled run is the same run.** `--journal` and `--journal-snapshot` are removed from the command line before
anything else reads it. The output policy, the result files (their `args` member) and the crash log therefore never
see them. The entry is written when the process exits, after every output has been written. Nothing is printed when
it succeeds. If the entry cannot be written, one `warning: journal: ...` line goes to stderr and the exit code is left
alone. A test (T1) runs `pf`, `nminus1`, `dynamics-study` and a `--once --enableExport` case run with the journal off,
on through the switch and on through the variable, and checks that stdout, stderr, the exit code and every output file
are equal. The solve export of the case run carries the time it was written and the solve's wall time, so its bytes
differ between any two runs. For that file the test compares every other field bit for bit.

Not journaled: `--version`, help, `licence`, `--importLicence`, `breaking-change` itself and the interactive live
view.

### The entry

One JSON line per run (`gridsim-journal/1`). Keys are written in a fixed order, numbers and times in the invariant
culture:

```
{"schema":"gridsim-journal/1","seq":412,"recordedUtc":"2026-11-23T10:15:02.1234567Z",
 "version":"2.1.104","informationalVersion":"2.1.104+<commit>","commit":"<commit>","buildStatus":"UNSUPPORTED BUILD",
 "verb":"nminus1","argv":["nminus1","case14.m","--json","out/n1.json"],"cwd":"/abs/work",
 "tokens":[{"i":1,"kind":"input","raw":"case14.m","abs":"/abs/work/case14.m","rel":"../work/case14.m","sha256":"..","bytes":4821},
           {"i":3,"kind":"output","raw":"out/n1.json","abs":"/abs/work/out/n1.json","rel":"../work/out/n1.json"}],
 "caseFiles":[],
 "outputs":[{"abs":"..","rel":"..","cwdRel":"out/n1.json","sha256":"..","bytes":9123}],
 "exitCode":3,"backend":"cpu","gpu":false,"elapsedMs":1840,"peakWorkingSetBytes":212000000,
 "replay":{"class":"safe"}}
```

- `tokens` comes from the classification the output policy uses (`CliFiles.Classify`, which `CliFiles.Init` is built
  on). An `input` is an existing file named on the command line. A `case-folder` holds `system.json`, and its files
  are listed in `caseFiles` with their hashes. An `output` is the value of an output flag (`--out`, `--json`,
  `--csv`, `--export`, `--enableExport=`, ...). A `builtin` is a bundled case name; it is part of the build and
  carries the hash of the build's bundled-data manifest as provenance. A v2 manifest's `network` and `assets` files
  are listed in `caseFiles` too. Each path is recorded absolute and relative to the journal's folder, so a work tree
  moved together with its journal still re-runs.
- `outputs` lists every file the run passed through the output policy (`CliFiles.Written`, in call order), every
  output-flag value that is a file, and every file written during the run into an output-flag folder. Each is hashed
  when the run ends.
- `commit` is the full commit of the build. `buildStatus` is `RELEASE` or `UNSUPPORTED BUILD`, the same build status
  the result files carry (from the build's provenance). An entry made by `breaking-change import`, or by a
  build that did not write the field, has none; a reader treats it as unknown. `recordedUtc`, `elapsedMs` and
  `peakWorkingSetBytes` are provenance only. The scheduler uses the last two for start order and memory admission;
  nothing else reads them, and neither can change a status.

### Concurrent writers, rotation and damage

Many processes may append to one journal at once. Each append opens `<journal>.lock` with no sharing, retrying with a
back-off that starts at 25 ms and doubles, for at most 10 s. Under the lock it reads the last sequence number from
the end of the active file, appends its line with its line feed, flushes it to disk and lets the lock go. The lock
file holds no data. Readers never take the lock; they read only lines that end in a line feed. A writer that died
mid-line leaves a torn last line. The next append closes it with a line feed first, and readers report it as a damaged
line (`CANNOT-REPLAY`, `damaged entry`). Before an append that would take the active file past 64 MiB, the file is
renamed `<name>-<firstSeq>-<lastSeq>.jsonl`, an immutable segment beside it; sequence numbers continue across
segments. A test (T9) starts 16 writer processes at once and finds 16 lines with sequence numbers 1 to 16.

### Snapshot store

With `--journal-snapshot` (or `"snapshot": true` in the config file), each input file and each case-folder file is
also kept in `objects/` beside the journal, under its SHA-256 (`objects/ab/cdef...`). An object is written once,
create-new, and made read-only. Files over 256 MiB are not kept (`"snapshotSkipped":"size"`). The other reasons a snapshot is not taken are
`"damaged"` (a damaged object of the same hash is in the store and cannot be removed), `"busy"` (another program held the
file, a sharing or lock violation, through every short retry of the move) and `"write-failed"` (the object could not be
written for any other reason, such as a full disk or a read-only volume). Outputs are never kept.
Nothing is deleted automatically: delete `objects/`, or objects in it, to free space. Entries whose inputs were only
in the store then cannot be re-run.

### Privacy

The journal is a local file. No part of this feature uses the network. On Linux and macOS the folder is created
0700 and the files 0600; on Windows the per-user folder's access list is inherited. The journal holds absolute paths
and file names.

The journal is a convenience log, not evidence: it has no hash chain and anyone who can write the file can edit it.
The tamper-evident record is the audit log ([chapter 31](<0031 Audit log and diagnostics.md>)).

## Re-running: `breaking-change validate`

### One child process per entry

The command line is a top-level program that ends runs with `Environment.Exit` in many places and keeps process-wide
state (the result writer, the output policy's input set, the licence gate, the solver backend). No verb can be
re-entered safely in the same process. Every re-run is therefore a child process of the same binary, with the
recorded command line apart from the path rewrites below: exactly a fresh run.

Each child runs in a scratch folder of its own, `<scratch>/<seq>/`, with:

- `DOTNET_PROCESSOR_COUNT=1` and `DOTNET_gcServer=0`, so it is single-threaded;
- `GRIDSIM_REPLAY_SCRATCH=<scratch>/<seq>`, which makes the output policy refuse any output outside that folder;
- `GRIDSIM_JOURNAL=<scratch>/<seq>/replay-journal.jsonl`, a journal of its own in the scratch folder. It names the
  files the re-run wrote, so an output the recorded run did not write is reported. The user's journal is never
  written by a re-run.

Inputs are read from their recorded paths when they still have their recorded bytes, else from the snapshot store
(copied under `<scratch>/<seq>/in/`, with a case folder's or manifest's layout kept). Every output-flag value is
rewritten to `<scratch>/<seq>/out/<n>-<name>`, and a default output under the working folder lands under the child's
own working folder. A recorded output is only read and hashed, never opened for writing.

An entry recorded by this very build (same version and commit) is `SKIPPED-SAME-BUILD` and not re-run, since by
determinism it cannot change. Version and commit are both compared, so a local or development build that shares a
release number is still re-run.

### The report does not depend on the parallelism

Re-runs run side by side, longest recorded run first, with at most `--parallel` at once (by default the smallest of
the processor count, the number of 256 MiB slots in 80 % of installed memory, and the number of entries). A job
starts only when the sum of the running jobs' memory estimates plus its own, `max(256 MiB, 1.25 x recorded peak)`,
fits in 80 % of installed memory. A job larger than that runs alone. A child is killed after `max(10 x recorded wall
time, 60 s)`.

The report is the same for every `--parallel`:

1. Each entry's status is a function of the entry, its input bytes, this build and the child's outputs. Children
   share no writable state: separate processes, working folders and scratch folders; inputs are only read (snapshot
   objects are read-only).
2. A child's outputs are bit-identical whatever runs beside it. That is GridSim's determinism contract (identical
   across runs, parallel paths and CPU tiers), and a single-threaded child removes even its internal parallelism.
3. Admission, time limits and memory pressure can change only whether a parallel attempt finished. A child that
   crashed or was killed is run again alone after the parallel phase, and only that result is classified. That retry
   is the same for every N.
4. Results are stored in an array indexed by the entry's position in the sequence-ordered selection. The report is
   built from that array after every job has finished, and N appears nowhere in it. Reasons have the scratch path
   replaced by `<scratch>`.

So the CSV report is byte-identical for every N, and the JSON report too apart from its `args`. Test T5 checks this
on a journal of 20 mixed jobs at `--parallel 1` and `8`, and a Core test checks the scheduler with shuffled completion
orders, a job that fails beside others, and parallelism 1, 3, 8 and 12.

One verb is never re-run for this reason: `cycle`. It is a real-time scheduler: what completes in each window, and its
utilisation figures, depend on wall-clock timing, so two runs on one build can differ. The README states this as the
one scheduling exception to bit identity.

### Comparing a result

For each recorded output, the re-run's file at its rewritten path is compared with the recorded file:

- JSON is compared member by member. A field is the JSON path with array indices collapsed
  (`$.tables.buses[*].vm_pu`). A number is the same only when it is the identical IEEE double, as in `result-diff`.
- CSV is compared column by column (`<file>:<column>`); a `key,value` summary file key by key.
- Any other format (`.m`, `.md`, `.xml`, `.raw`, `.txt`) is compared byte for byte, and a difference is reported as
  `bytes differ` with no field figures.

For each field that moved the report gives, over all its values:

- `max_abs_diff` = max |b - a|;
- `max_rel_diff` = max |b - a| / max(|a|, |b|), 0 when both are 0;
- `max_ulps` = max `ResultDiff.Ulps(a, b)`, the number of doubles between the two;
- `min_agreeing_bits` = min(53 - bitlen(ulps)), clamped to [0, 53]: 53 when identical, 52 for one ulp, 0 for a sign
  change, NaN against a number, or 2^53 ulps and more.

It also counts changed strings, added and removed members, and changed row counts. An exit-code change is itself a
change. The entry's figures are those of its worst field (fewest agreeing bits). Test T3 perturbs a recorded `pf`
result by one ulp in one field, by a relative 2^-20 in a second and by a sign change in a third, and checks each figure
exactly.

A path in a result (a case argument, a file named in a summary) differs between the recorded run and the re-run by
construction. A string equal to a recorded token (as written, or absolute, or as rewritten for the re-run) is replaced
by a placeholder such as `<input:1>` on each side before it is compared.

### Fields that carry no result

Some fields carry build or run identity, never a computed value: the `tool`, `buildStatus` and `args` of a shared result file, a
study's `provenance.engine`, a solve export's `meta.gitCommit`, `meta.timestampUtc`, `meta.elapsedMs` and
`meta.machine`, and the timings of the `study-case` and `planning-study` manifests. Old and new results always differ in
these, so they are left out of the comparison. The list (`src/GridSim.Core/Journal/volatile-fields.json`, embedded) is
derived from the result schemas: each entry names the writer that puts the field there. A field may be listed only if
it is identity or timing.

The dynamics result files add two kinds. The run-metadata sidecar (`run-metadata.json` in a run folder, and a report
verb's `<report>.run-metadata.json`) has no schema tag, so its entries name the file: its time, build (commit, Core
version, build configuration), runtime, architecture, garbage collector, SIMD tier and width, machine, operating
system, processor, core count, memory and backend fields are identity (`RunInfo.WriteJson`). Its `run` description and
any `parameters` are compared: the description can carry a computed value, such as the stability verdict. A dynamics
export CSV starts with comment lines, the run header of `RunInfo.HeaderLines` (`# GridSim  -  <time> UTC`, `# code:`,
`# runtime:`, `# machine:`, `# cpu:`, `# simd:`, `# backend:`), then `# run:`, and for the RMS event export a
`# provenance:` line. A list path that starts with `#` names such a line by the text after `#` and its spaces; the run
header lines are left out, and every other comment line is compared as text, in order (a difference is reported as the
field `(comment line)`). A CSV with no leading comment line is compared exactly as before. The result schemas of the
re-run dynamics verbs (`gridsim-rms-events-export/1`, `gridsim-classical-export/1`, the `dynamics-study` result, the
long-term CSV, the trip and small-signal exports) carry no other identity or timing field; the list says so for each,
and the `dynamics-study` result's one, `provenance.engine`, is covered by `**.provenance.engine`.

Test T7, the census, checks that the list is complete. Every writer verb that `validate` re-runs (71 verbs) is run once
with a journal in one folder, then re-run by `validate` in its scratch folder, later. Every entry must be IDENTICAL, so
a field the list misses fails the census and is named. The dynamics census does the same for `stability` (the
classical export, an RMS event export, one with a sequence fault and one with a DYR under both extended options),
`dynamics-study` (plain and with a model plug-in), `long-term`, `trip` and `modal`.

### Statuses and reasons

`CANNOT-REPLAY` reasons, in the order they are checked: `entry schema unknown`; `damaged entry`; `recorded by a newer
build`; `verb removed`; `not replayable: <reason>`; `GPU backend (outside the bit-identity contract)`; `input
missing: <path>`; `input changed: <path> sha256 <recorded> != <now>`; `no result file recorded (console-only run)`;
`recorded result missing` or `recorded result changed since the run`; `refused: writes outside the scratch folder`;
and, after the re-run, `replay exit 1 (input): ...`, `replay exit 2 (usage error or refused run): ...` (exit 2 is a
usage error for most verbs, but `stability` also reports a refused run with it), `replay timed out`, `replay crashed`, `recorded result schema not readable by this build (<tag>)` and `indirect input
changed: <field> sha256 differs`. The last is a `*Sha256` field inside a study document's `provenance` (or its
`provenance_*` keys in a shared result file) that differs: an input the study file names changed, so the re-run is
not the same job.

A re-run that newly exits 1 or 2 is first checked against the exit-code claims of the breaking changes that can explain
the job (a qualitative claim on `(exit code)`, such as B9's `0 -> 1`). When one claims that transition, the entry is
`CHANGED` with the single field `(exit code)`, and the claim check explains it. The recorded outputs are not compared,
since the claim is that the run is now refused. Only when no breaking change claims the transition is it
`CANNOT-REPLAY`.

Exit codes follow the batch rule of the other verbs: 1 when anything cannot be re-run, else 3 when anything changed
(other than UNBOUNDED or REVIEWED, below), else 4 when anything is UNBOUNDED, else 0; 2 for a usage error or filters
that name nothing. 4 is used by no other verb. It means "review needed": the only results that moved did so under a
breaking change with no bound, and nothing else is wrong. A REVIEWED entry counts as nothing wrong.

## Dynamics jobs

`validate` re-runs five dynamics verbs. Each was audited the same way as the writer verbs: every file it writes goes
through the output policy (or, for the two that write their result themselves, its re-run guard), and is named by an
output flag, by an option the replay rewrites, or lies under the working folder.

| Verb | Result files compared | Not re-run |
|---|---|---|
| `stability` | `--events-out` (the RMS event export: machine, converter, device and fault-point CSVs, `rms-events.json`, `run-metadata.json`) and `--export` (the classical trajectory) | a run that printed its result only |
| `dynamics-study` | its `--out` result | a run with no `--out` |
| `long-term` | its `--out` CSV | a run with no `--out`: the CSV goes beside the input, outside the output flags |
| `trip` | its `--export` trajectory, time-stamped from the fixed epoch 2000-01-01, not the clock | a run with no `--export` |
| `modal` | its `--export` small-signal file | a run with no `--export` |

`--events-out` is not one of the output policy's own output flags (that list is pinned), so the replay rewrites its value
into the scratch folder from a list of its own (`ReplayPolicy.OutputPathFlags`), as it rewrites an output-flag value.

These dynamics verbs are never re-run, each with its reason in the report: `transient` (a per-slice solve time in its
time series, and the wall time and run header in its `summary.md`), `emt` (the wall time in its `summary.md`),
`replay-event`, `scenario`, `h0-study` and `evidence` (the run header in their `summary.md`; markdown is compared byte
for byte, so none of these could ever compare IDENTICAL), `impedance-stability` (its export carries the time and host
in a schema the list does not cover), `replay` and `fit-frequency` (no result file), and `fleet` (no two-run census
covers it yet).

### Files named inside an input

A dynamics job reads files that are not on its command line. The journal hashes them with the input that names them,
as it does a v2 manifest's files (`caseFiles`, with the label of what names them and the path as written):

- a `dynamics-study` file: `raw`, `dyr`, and the `adaptive` member's `unitProtection`, `devices` and every `*.json`
  under its `curves` folder (the curve library reads the folder whole), and the `curves` folder itself by its file list
  (label `study:adaptive.curves`, the SHA-256 of the sorted relative names), so a curve file added later is seen even
  when the folder was empty when the run was recorded;
- a `--model-assignments` file: each `userModels[].file`;
- a `--model-plugins` configuration: each assembly, resolved against the configuration's `root` as the loader resolves
  it.

With `--journal-snapshot` they are kept in the snapshot store, except a plug-in assembly: it is code, and the store never
holds code. On a re-run an input that names files is read in place only when it and every file it names still have
their recorded bytes where the run would read them (and a curves folder still lists exactly the recorded files). Otherwise the
input and all its files are placed under `<scratch>/<seq>/in/`, the input deep enough that every relative reference,
`../` included, stays inside, each file from where it was if its bytes are unchanged, else from the store. A reference
written as an absolute path is read where it points and must be unchanged; an absolute curves folder is listed again
where it is and must hold exactly the recorded files (`input changed: <folder> (the curves folder no longer holds exactly
the recorded files)`), and a relative one is placed holding exactly the recorded files, made even when empty. A file that cannot be had is `input missing:
<file>` or `input changed: <file> sha256 <recorded> != <now>`, naming the file. A study file pins its RAW, DYR and
device files by SHA-256 itself, so a copy with the same bytes is the same job.

### Plug-in jobs

Before a plug-in job is re-run, `validate` checks every assembly its configuration names against the bytes recorded
with the job and against the configuration's own pin (parsed with the loader's parser, at the path the re-run will
read). A missing assembly is `CANNOT-REPLAY: plug-in assembly missing: <file>`; one whose bytes differ is `plug-in
assembly changed: <file> sha256 <recorded> != <now>` (or, from a job recorded before 2.1.127, `... sha256 <now> !=
pinned <pin>`). The re-run does not start, so the assembly is never loaded. `validate` itself loads no assembly, and the
re-run's loader checks the pins again before it loads one. An assembly is copied into the scratch folder only when its
configuration must be, and then only its verified bytes from where it was; never from the snapshot store, whatever the
journal says: a journal edited to mark an assembly as kept, with a store object holding its exact bytes, still gives
`plug-in assembly missing` when the assembly is gone (a test runs exactly that).

### Determinism of the dynamics re-runs

The report does not depend on `--parallel` for the dynamics verbs either. Every input, named file and pin is resolved
and checked in journal order before any child starts. Each child is a process of its own with its own scratch folder,
single-threaded (`DOTNET_PROCESSOR_COUNT=1`), and loads plug-ins into its own process only. A study file's own
`execution.maxParallelism` is part of the job, the same in both runs, and the study's results do not depend on it (the
determinism contract). A test checks that the console, the CSV report and the JSON report apart from `args` are the
same bytes at `--parallel 1` and `8` on a journal of dynamics jobs of every re-run kind except a plug-in job (stability
with an RMS event export, with a DYR and with the classical export; dynamics-study; long-term; trip; modal).

### B3, B27 and B28 on dynamics jobs

B3 and B27 are raised only for a job whose command line holds both `--dyr-extended` and `--allow-unmodelled` and whose
DYR holds one of their records: a `stability`, `trip` or `modal` job whose `--dyn-data` is a PSS/E `.dyr` file. A
`dynamics-study` job refuses `--allow-unmodelled`. The DYR is a recorded input, checked by hash or taken from the store,
and one that cannot be read makes the job CANNOT-REPLAY (`input unreadable`) before it is re-run, so the condition is
always decided on the DYR itself.

B28 (2.1.120: an RMS run refused when a model that finishes its initialisation on the first step's measurements meets a
disturbance at t = 0) still has no CLI route, and its map scope names no verb. The stability verb's single fault and
CCT search start at 0.1 s or later, its `--events` refuses an event at or before the first step (the RMS event stage
tolerance), `dynamics-study` refuses an onset or event time of 0, and the scripting `STUDY RMS` builds the same
`dynamics-study` request. Only the library API can start such a run. If a future verb reaches it, B28's claim (exit 0 to
1 or 2) already judges the refusal.

### B33 on DC jobs

B33 (2.1.144) moved the DC model to MATPOWER's `makeBdc`: tap ratios, phase-shift injections and bus Gs. A network with
none of these is computed by the earlier instructions bit for bit, so B33 is raised only for a job that runs the DC model
(`UnboundedChanges.RunsDcModel`: every verb of its scope except `nminus1`, which is AC in every mode, and `sensitivity`
other than `--kind otdf` or `psdf`) on a case for which `DcPowerFlow.UsesMakeBdcTerms` is true. When B33 is a candidate
for a job, validate loads the job's case before the re-run, from the bytes the re-run reads (the recorded file or its
snapshot copy), through the CLI's own case loader with the job's `--allow-unmodelled` and `--psse-revision`. The case is
the job's one case argument: a `.m`, `.raw`, `.rawx` or `.uct` file, a case folder, a built-in name, or a `.json` case
manifest that is not the value of an option. For `transmission-security`, `transfer-capability`, `corrective-sequence`
and `probabilistic` it is the case the study file names (`case.path`, relative to the study file, or `case.builtIn`); a
study that names no case has no network and is not raised. No case, more than one, or one that does not load makes the
job CANNOT-REPLAY (`input unreadable`), so the condition is never assumed. `planning-study` is not narrowed: its
projects can add or change branches, so its master case alone does not decide B33, and its jobs are raised as before.

## The breaking-change map

`src/GridSim.Core/Journal/breaking-changes.json` (`gridsim-breaking-changes/1`, embedded, stored without line-ending
conversion; its SHA-256 is in every report as `map_sha256` and pinned by a test) lists every BREAKING CHANGE since RC-4
(2.1.073) with an ID, B1 to B31, in version order. Each entry has the release it landed in, its kind (behaviour, bug
fix or security), a title, a magnitude, the CHANGELOG section that states it, and a scope: the verbs, options and
input formats it can affect. Each CHANGELOG bullet carries its ID after the label, `BREAKING CHANGE (B5, bug fix):`, so
the label still comes first. Test T8 checks that the CHANGELOG bullets and the map name the same IDs.
`gridsim breaking-change list` prints the map.

Bk can explain a CHANGED entry when recorded version < Bk.version <= this build's version, and the entry's verb is in
Bk's verbs, or one of its flags is in Bk's options, or one of its input files has an extension in Bk's input formats.
A match only through `*` (B2, the Newton retry, which can touch any verb) is a candidate too.

### The claim check

A matching ID alone would let a second regression in the same field hide behind it. So each map entry also claims its
effect on each class of field it moves (fields are matched by name patterns such as `*angle*|*delta*`):

- a **bound**: the largest absolute and/or relative change of each value, taken from the magnitude measured in the
  CHANGELOG (B26, the adaptive angle scale: angles 1.9e-4 degrees, frequency 5.4e-6 Hz, power 1.9e-3 MW);
- a **transform**, where the change is exact: new = f x old for every changed value, with one factor (f = -1 for the
  VSC DCSET and DGS capacitor sign fixes) or a stated range (w2^2 for the PSS/E two-winding fix, 0.81 to 1.21);
- a **qualitative** transition, only where no bound means anything: the field's old and new value must be the stated
  pair (B12: status `Optimal` to `SecuredOutageFailsScreen`, exit code 0 to 3). Every changed value of the field must
  make the same transition.

`validate` records, for each changed number field, the largest absolute and relative change and the smallest and
largest new / old ratio, and for each changed string field (and the exit code) its transition. Each field is then
judged against the claims of the candidate IDs that cover it:

| Claims covering the field | Verdict |
|---|---|
| none | UNEXPLAINED |
| bounds only (one or more) | the bounds add; EXPLAINED when the observed change is within the sum, else EXCEEDS-CLAIM |
| one transform | EXPLAINED when every new / old ratio lies in its range (4 ulps of rounding allowed), else EXCEEDS-CLAIM; a value that moved from 0 has no ratio and exceeds it |
| one transition | EXPLAINED when the field's transition matches, else EXCEEDS-CLAIM |
| a transform or transition beside any other claim | AMBIGUOUS: such claims do not compose |

An EXCEEDS-CLAIM field reports both what was observed and what was claimed. The entry takes the worst verdict of its
fields (UNEXPLAINED, then EXCEEDS-CLAIM, then AMBIGUOUS). Only an all-EXPLAINED entry counts as explained;
EXCEEDS-CLAIM and AMBIGUOUS count like UNEXPLAINED: flagged first on the console and in the summary, and still CHANGED
(exit 3). Every map entry carries a claim. B1, B7 and B13 have no command-line route, so `validate` never matches
them; their claims state the exact effect for the record (exit 1 from `gridsim-script` for a script past the limits,
the reversible heat negated, the tap prices x 1/tau^2 for taps between 0.9 and 1.1). The map refuses an incomplete
claim when it is read.

### Changes with no bound: UNBOUNDED and `--accept`

B3 cannot be bounded: how far a CSVGN1 unit's control moves a trajectory depends on the unit's data and the
disturbance. Its one claim is a change of model on every field (a qualitative claim on `*`), which no number can check.
A map entry whose claims are all of that kind is an unbounded change; B3 and B27 are the two.

When it appears. B3 is raised for a job only when all of these hold:

- the job was recorded before 2.1.083 and this build is 2.1.083 or later (the usual window);
- its recorded command line holds both `--dyr-extended` and `--allow-unmodelled`;
- one of its DYR inputs holds a CSVGN1 record. A DYR input is a recorded input with the extension `.dyr`, or the value
  of `--dyn-data` when it is not JSON. Its bytes are the recorded ones: the file passed its SHA-256 check, or came from
  the snapshot store. The model name must stand as a whole word before the record's `/` on its line, in or out of
  quotes; text after `/` is a comment.

Nothing is assumed. A job with no DYR input read no DYR, so B3 cannot have acted, and a DYR that cannot be read makes
the job CANNOT-REPLAY before it is re-run. A job that fails any condition is judged by the bounded changes alone, so B3
never adds noise to it: a field they do not explain is UNEXPLAINED. B27 (2.1.117: EXDC2, IEEEX1, ESAC8B, ESST3A and
IEEEG3 records modelled under the same two options) is the second unbounded change, with the same rules and those five
records in place of CSVGN1.

For a job where B3 is raised, the bounded changes judge each field first. Every field they do not explain is UNBOUNDED,
and the entry is `UNBOUNDED (B3)`. It is reported once, on one console line that lists every moved field with its
largest absolute and relative change. It is never EXCEEDS-CLAIM: B3 has no claim to exceed. The summary counts it as
`changed_unbounded`.

How to acknowledge it. Look at the listed fields. When you are content, run validate again with `--accept B3`. Each
UNBOUNDED (B3) job of that run becomes `REVIEWED (B3)`. The report records it (summary `accepted_now`, and a `reviews`
table row `accepted now`), and so does a small file beside the journal, `<journal name>.accept.json`
(`gridsim-breaking-change-accept/1`). For each job and change, it holds the job's sequence number and recorded time,
and each moved field with its largest absolute and relative change (for a field that is not a number, its count and
transition). Accepting the same job again keeps the larger size per field. Records are written in (sequence, ID) order,
fields in (file, field) order, by a temporary file then a rename.

Every later validate of that journal reads the file. An UNBOUNDED job is REVIEWED only when the file holds that same job
(same sequence number and recorded time) and every field that moved now, each by no more than accepted: absolute and
relative change both within the accepted ones, or, for another kind, the same transition and no larger count. A new
job, a field that did not move before, or any larger movement leaves it UNBOUNDED, and the `reviews` table says which
field and by how much (`needs review`). Only an unbounded change can be accepted: `--accept` with a bounded ID, or an
ID that names nothing, exits 2. A malformed accept file exits 1.

## `breaking-change import`

The journal can only hold runs made while it was switched on, and RC-5 is the first release with a journal.
`breaking-change import <dir|files>` builds entries from `gridsim-cli-result/1` documents you already have (written
with `--json`): each records its tool (release and commit), verb, command line and exit code. The working folder is not
recorded in the document. It is taken as the folder that makes the document's own `--json` (or `--out`) path land on
the document, else the document's folder. The inputs are found at the recorded paths and hashed at import, not at
run, and the entry says so (`"imported"`). An input that is gone makes that entry `CANNOT-REPLAY`. This makes RC-4 to
RC-5 checks possible now for anyone who kept their result files.

## Security

- Which runs are re-run is an allow-list keyed by verb (`ReplayPolicy`). Safe are the shared writer's verbs and the
  study verbs with a result document of their own, whose every written file is named by an output flag or lies under
  the working folder. Never re-run, with the reason in the report: live and network verbs (`serve`, `rpc`,
  `interpose`), external bridges and tools (`gda`, `gda-replay`, `crossval`), timing and hardware verbs (`bench`,
  `gpucheck`, `simdcheck`, `batchsolve`, `cycle`), `synth` (it writes the bundled data), `audit`, `licence`,
  `compliance`, `ref`, `help`, and verbs or options that write outside the output flags (`convert`, `cgmes-export`,
  `dgs-import`, `substation-state --emit`, `cgmes-shacl --report`, `dist3ph to-v2`, `harmonic-assess --template`).
  Runs with `--audit-forward`, `--incident-log`, `--audit-sign-key` or `--audit-verify-key` are never re-run: they
  touch external logs and keys. The dynamics verbs that are never re-run are listed under Dynamics jobs, each with
  its reason. A verb not in the table is not re-run ("not yet classified").
- `--journal`, `--scratch` and every journal path must not be network or device paths (exit 2). The scratch folder must
  be new or empty and not inside the journal's folder.
- Snapshot objects are read only through `PathConfinement.Refusal` against `objects/`: a link or junction on the way
  is refused.
- Every rewritten output and every copied input is checked against `<scratch>/<seq>` before the child starts; a path
  outside it makes the entry `CANNOT-REPLAY`, `refused: writes outside the scratch folder`.
- In the child, `GRIDSIM_REPLAY_SCRATCH` makes the output policy refuse any output outside the scratch folder (exit 2).
  The check is inert when the variable is unset, so ordinary runs are unchanged. It is a second line of defence; the
  allow-list is the first.

## Scope

A re-run proves bit identity, or measures the difference, between two builds on this machine. It says nothing about
how either result compares with the real grid. GridSim's core is verified to machine epsilon and validated against
independent tools; it is not validated against the real grid. These runs cannot be re-run, and the report says why:
console-only runs (no result file; add `--json`), runs whose inputs are gone with no snapshot, GPU runs (GPU arithmetic
is outside the bit-identity contract), and the verbs on the never list. The files a dynamics study, model assignments
or plug-in configuration names inside itself are hashed with it (Dynamics jobs). A file named inside any other input
file is seen only through that study's own provenance hashes; one read that way with no provenance hash can change
unseen, and a result that moved for that reason shows as CHANGED and UNEXPLAINED, which errs loud. A stability run
given its case with `--dir` names that folder in its run description as typed, so a re-run (which is given the folder's
full path) differs there: the `run` field of `run-metadata.json` and the `# run:` comment line show as CHANGED strings.
Give such a case by its full path, or positionally, to replay it IDENTICAL. A result that names an input by its file name
alone (`dynamics-study`'s `provenance.modelAssignments.file`) compares equal: the re-run's file name maps to the same
placeholder as the recorded token.
A positional case file does not: its label is the case's own name. A bundled
case is part of the build, so a changed bundled case shows as CHANGED and needs a map entry. Each re-run costs a process
start, about a tenth of a second. The snapshot store grows until you prune it. The first useful `validate` compares
RC-5 with the release after it, unless results kept from RC-4 are imported.
