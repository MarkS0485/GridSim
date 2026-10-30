# 31. Audit log and diagnostics

This chapter specifies the evidence machinery in `src/GridSim.Core/Diagnostics/`: the persistent, hash-chained audit
log (`gridsim-audit/1`) with its writer, run recorder, verifier and anchors; the solve trace that records the internals
of a power-flow solve; the forward-inference guard that refuses any dated input newer than 169 hours; the provenance
tags; and the restriction review gate that fences the one seam where dynamics could be fused into estimation. It gives
the exact hash payload, the line format, the order of the verifier's checks and what each check can and cannot
detect, every dated seam that calls the guard, and the tests that pin each rule.

How to use the log (running a verb with `--audit-log`, reading and verifying a log, keeping the anchor) is in the user
chapter [26, Audit log, deployment and assurance](<../manual/0026 Audit log, deployment and assurance.md>). The
file schema in the context of every other format is in [ch. 10, File and wire formats](<0010 File and wire formats.md>).
The meaning and one-way flow rules of the provenance tags are in
[ch. 8, Provenance and invariants](<0008 Provenance and invariants.md>) and are not repeated here.

## Model

### The record and its hash

An audit entry is the record `AuditLogEntry` (`src/GridSim.Core/Diagnostics/AuditLog.cs`):

```
AuditLogEntry(Sequence, RunTimestampUtc, StateTimeUtc, Provenance, Summary, PrevHash, Hash)
```

The hash is computed by one function, `AuditLog.ComputeHash`. The in-memory `AuditLog`, the on-disk
`AuditLogWriter` and the verifier `AuditLogFile` all call it; there is no second copy. The payload is the six fields
joined with the vertical bar `|`, encoded as UTF-8:

```
payload = seq | runUtc | stateUtc | provenance | summary | prevHash

seq        decimal integer, invariant culture
runUtc     UtcTime.Normalise(runTimestampUtc).ToString("o")      e.g. 2026-10-04T11:01:57.2144688Z
stateUtc   UtcTime.Normalise(stateTimeUtc).ToString("o"), or the single character "-" when there is no state time
provenance ProvenanceTag name, e.g. Synthetic
summary    the summary text exactly as stored
prevHash   hash of the previous entry, or the literal GENESIS for entry 0

hash       = upper-case hexadecimal of SHA256(UTF8(payload))      64 characters
```

The `-` stands in for a null `stateUtc` inside the payload only. In the file a null state time is the JSON value
`null`, and the line never contains a `-` field. The payload has no trailing separator and no field for the format tag.

`UtcTime.Normalise` (`src/GridSim.Core/Diagnostics/UtcTime.cs`) is the single rule that turns a `DateTime` into UTC:
`Unspecified` is relabelled as UTC with no shift, `Local` is converted, `Utc` is unchanged. The same rule is used when
a line is written and when its hash is computed, so an entry hashes identically on a machine on British Summer Time and
on a UTC server.

The chain is:

```
prevHash(0) = "GENESIS"
prevHash(n) = hash(n-1)
hash(n)     = SHA256( n | runUtc(n) | stateUtc(n) or "-" | provenance(n) | summary(n) | prevHash(n) )
```

There is no key. Anyone who can write the file can recompute the whole chain; what detects that is the anchor (below).

### The file format

`AuditLogFile.FormatTag` is `gridsim-audit/1`. A log is UTF-8 with no byte-order mark, one compact JSON object per
line, each line ended by a single LF. Each object has exactly these eight fields, in this order
(`AuditLogFile.EncodeLine`):

| Field | JSON type | Content |
|---|---|---|
| `format` | string | Always `gridsim-audit/1`. |
| `seq` | number | 0-based position; a non-negative 32-bit integer; must equal the line's position in the file. |
| `runUtc` | string | The time the entry was written, `o` format, UTC, ending in `Z`. A fact about the run, not a grid claim. |
| `stateUtc` | string or null | The present or past grid instant the entry describes, same notation, or `null`. The field is always present. |
| `provenance` | string | A `ProvenanceTag` name. A number, a lower-case spelling or an undefined name is refused. |
| `summary` | string | Plain text; for product events the layout below. |
| `prevHash` | string | The previous entry's `hash`, or `GENESIS`. |
| `hash` | string | The 64-character upper-case hex hash. |

The writer uses `Utf8JsonWriter` with its default options: compact, no indentation, and the default encoder, which
escapes every non-ASCII character and the HTML-sensitive ASCII characters, so a line is plain ASCII and the same bytes
are produced for the same entry on every machine. The hash is over the unescaped summary text, not over the escaped JSON.

### The summary layout

`AuditEvent.FormatSummary` (`src/GridSim.Core/Diagnostics/AuditEvent.cs`) builds the summary for every product event:

```
actor=<who> action=<verb> inputs=<name>:<SHA-256>,<name>:<SHA-256>,... outcome=<text>
inputs=-        when there are no inputs
```

- `actor`, `action` and every input `name` must be a non-empty run of ASCII letters, digits and `. _ - @ /`, so the
  layout can always be split back into fields. Anything else throws `ArgumentException` before a byte is written.
- `AuditEvent.DefaultActor()` is `user@machine` from `Environment.UserName` and `Environment.MachineName`, with every
  character outside that set replaced by `_`; an empty user name becomes `unknown` and an empty machine name becomes
  `unknown`, each on its own.
- Each input hash must be 64 upper-case hex characters (`AuditInput`). Two inputs with the same name throw.
- `outcome` is free text on one line, last in the summary, with no control character (no newline or tab).
- The summary is stored in the clear. It is hashed, not encrypted, and no code path removes a secret that a caller puts in it.

`AuditInput` builds a hash from a file's bytes (`OfFile`), a byte buffer (`OfBytes`) or a string's UTF-8 bytes
(`OfText`). `AuditRunRecorder.InputForArgument` hashes a case given on the command line by what it is: a file by its
bytes (name as given), a directory by `HashDirectory` (name as given), and anything else, such as a bundled case name,
by the UTF-8 bytes of the name itself under the name `<name>-id`, so the log never claims a content hash it did not
take. `HashDirectory` forms one line `<relative path with / separators>:<SHA-256 of the file>` plus LF for every file
below the directory, sorts the lines ordinally, and hashes their concatenation, so the result does not depend on
enumeration order, and any change of content, name or presence changes it. An empty directory hashes the empty string.
Decision (GR-847, October 2026): a file name holding a control character (a line break could forge a second line) or,
on Linux and macOS, a backslash (it would become `/`, so `a\b` would hash as `b` in folder `a`) is refused with an
`InvalidDataException` rather than escaped, so the lines stay unambiguous and every hash recorded before keeps its
meaning.

### The estimate outcome text

`EstimationAuditTrail.Outcome` (`src/GridSim.Core/Estimation/EstimationAuditTrail.cs`) is the single source of the
outcome text of an estimate:

```
method=<m> status=<s> observable=<bool> J=<objective> chi2=<PASS|FAIL|n/a> dof=<n> removed=<n> measurements=<n>
```

followed, only when true, by ` irls_cap_reached=true`, then by the caller's `extra`. `J` is written with the
round-trip format `R`, so the logged objective is the exact double. `chi2` is `n/a` when the estimate did not
converge, when the chi-square threshold is undefined, or when the test did not run; a test that did not run is never
recorded as `PASS`. The `estimate` verb passes the hypothesis-testing and FDIA fields as `extra`
(` bad_data=hti hti_removed=<n> hti_passes=<n> hti_stop=<reason>` and ` fdia=<verdict>`), present only when those
flags were given. `crossval` writes `verdict=AGREES|MISMATCH buses=<n> max_dvm_pu=<R> max_dva_deg=<R> tol=<R>`.
`interpose` records one input, `crosswalk` (the SHA-256 of the map file, taken before the file is loaded), and writes
`started protocol=<name> listen=<host>:<port> monitor=<True|False>` at the start (no `exit=`; `monitor` is the .NET text
of a `bool`). Its final entry repeats the input and the outcome is `exit=<code> stopped accepted=<n> rejected=<n>
decode-errors=<n> map-errors=<n> points=<n> windows-written=<n> duplicate-names=<n> client-resets=<n>
unsupported-type-ids=<list or none>`. `crossval` records the input `case` (the case, or the solve-export it was given)
and, when it solves a named case itself, `solve-export`, the export it wrote. The captured `interpose` entries are in
[ch. 26](<../manual/0026 Audit log, deployment and assurance.md>).

`EstimationAuditTrail.Record` also exists: it appends a stand-alone entry to an in-memory `AuditLog`, tagged
`DerivedFromRecorded`, with the text `estimate ` followed by `Outcome`. Nothing in `src` calls it (see Limits).

### Verification

`AuditLogFile.Verify(path, anchor)` reads the whole file (opened with `FileShare.ReadWrite`, so a log can be read while
a writer holds it) and calls `VerifyBytes`. It stops at the first broken entry and returns `AuditLogVerification`
(`IsValid`, `VerifiedCount`, `FirstBrokenIndex`, `Reason`, `HeadHash`, `Entries`); `Entries` holds only the clean prefix.
The checks run in this order, and the first failure ends the run:

| Order | Check | Reason reported | Detects |
|---|---|---|---|
| 0 | The file starts with `EF BB BF` | byte-order mark | a re-save by another program |
| 1 | The line has a terminating LF | the last line has no terminating newline | a crash during a write, a truncated copy |
| 2 | The line is not empty | a blank line stands where an entry was expected | an inserted or deleted blank line |
| 3 | The line does not end in CR | the line ends with CR LF | an editor re-saving as CRLF |
| 4 | The line parses: a JSON object, exactly the eight known fields, no duplicate, `seq` a non-negative integer, `format` equal to the tag, timestamps round-trip UTC, `provenance` a defined name, strings present | one reason per failure | damaged, extra, missing or mistyped fields |
| 4b | The line equals the writer's bytes for the parsed entry (run inside step 4, after the fields parse) | the line is not in the writer's canonical form | spacing, field order, a different notation for an equal value |
| 5 | `seq` equals the line's position | the sequence number is N but it is at position P | deletion, insertion, reordering |
| 6 | `prevHash` equals the previous `hash` (`GENESIS` for entry 0) | prevHash does not equal the hash of entry N-1; or the first entry's prevHash is not GENESIS | an earlier entry edited and re-hashed, removed or reordered; the start of the log removed |
| 7 | The stored `hash` equals the recomputed hash | the stored hash does not match the hash recomputed from the entry's fields | an edited entry |

Step 4b sits inside the parser: once the eight fields are read, the entry is re-encoded by `EncodeLine` and the line must
match byte for byte, so a change the hash does not cover is reported as well as one it does.

After the last line, when an anchor is given (`AuditAnchor(Count, HeadHash)`):

```
if entries < anchor.Count              -> broken at index entries      (tail truncation)
anchoredHead = GENESIS if anchor.Count == 0 else hash(entries[anchor.Count - 1])
if anchoredHead != anchor.HeadHash     -> broken at index max(Count-1, 0)   (whole-log rewrite, or another log's anchor)
```

A log may hold more entries than the anchor records. An anchor covers exactly the entries that existed when it was
taken. A zero-byte file is a valid empty log; a missing file throws `FileNotFoundException`, never an empty result.
`AuditLogFile.Load` verifies and throws `AuditLogTamperException` (an `IOException`) on failure.

What verification detects, derived from the order above:

| Damage | Without an anchor | With an anchor |
|---|---|---|
| An entry edited, hash left alone | Yes, step 7 at that entry | Yes |
| An entry edited and its hash recomputed | Yes, step 6 at the next entry (or no, if it is the last entry) | Yes |
| An entry deleted, inserted or moved | Yes, step 5 | Yes |
| A hand edit the hash does not cover | Yes, step 4b | Yes |
| A cut-off last line | Yes, step 1 | Yes |
| Whole entries removed from the end | No | Yes, for every entry the anchor covers |
| The whole log rewritten with recomputed hashes | No | Yes, for every entry the anchor covers |
| Entries appended after the anchor was taken | Not an error | Not an error; they are not covered |

An edited and re-hashed last entry has no successor to disagree with it, so only an anchor whose count covers that entry
detects it.

### The writer and its lock

`AuditLogWriter.Open(path, expectedAnchor)` (`src/GridSim.Core/Diagnostics/AuditLogWriter.cs`) performs these steps in
order and fails loud at the first that cannot be done:

1. Resolve the full path. If its directory does not exist, throw `DirectoryNotFoundException`. The directory is never created.
2. Open `<log>.lock` with `FileMode.OpenOrCreate`, `FileAccess.ReadWrite`, `FileShare.None`. The lock is the open handle, not the file's existence: the file stays on disk between runs and holds no data. If another writer in this or any other process holds it, throw `InvalidOperationException` ("already open for writing").
3. Open the log with `FileMode.OpenOrCreate` and `FileShare.Read`: verifiers may read it, nobody else may write it. A missing log is created empty here, before verification. Failure throws `InvalidOperationException`.
4. Read the existing bytes and run `VerifyBytes` against `expectedAnchor` (when given). A log that does not verify throws `AuditLogTamperException` and is not touched.
5. Seek to the end. Every later write is an append.

The lock file is created in step 2 and stays on disk on every path, including a run whose verification then fails or whose
process is killed.

`Append(runUtc, stateUtc, provenance, summary)` normalises both timestamps with `UtcTime.Normalise`, refuses an
undefined provenance (`ArgumentOutOfRangeException`), then, under the writer's lock: refuses a disposed or faulted
writer, refuses `int.MaxValue` entries, computes the hash from the current head, encodes the line, writes it and calls
`Flush(flushToDisk: true)` before returning. The head and count advance only after the write succeeds. If the write
throws, the writer marks itself faulted and refuses every later append: it cannot know how much of the line reached
the file. Several threads may append to one writer; the lock serialises them and the chain stays gap-free.

`Anchor` returns `AuditAnchor(count, head)` taken atomically. `Dispose` releases the log and the lock.

### The run recorder

`AuditRunRecorder` (`src/GridSim.Core/Diagnostics/AuditRunRecorder.cs`) records one run of one verb.

| Member | Behaviour |
|---|---|
| `Start(path, actor, action, provenance, anchorPath = null)` | Validates actor, action and provenance first, reads the anchor file (`anchorPath`, default `<log>.anchor`), then opens the log against it (verifying it) before the verb does any work. Throws on a bad token, an undefined provenance, a tampered or locked log (fewer entries than the anchor, or a different hash at the anchored entry: `AuditLogTamperException`), a malformed anchor file, or a missing directory. A log with entries but no anchor file is accepted: `AnchorWasMissing` is set, the CLI and the web host warn on stderr, and the entry ends ` audit-anchor=missing`. |
| `AddInput(input)` | Adds one named input. Throws on a duplicate name, and after `Finish`. |
| `SetOutcome(text)` | Sets the run's one-line result, replacing any earlier text. Throws on a blank text or any control character, at the call, not at exit. |
| `StateTimeUtc` | The grid instant the result describes; set by `estimate` to the measurement set's `TakenUtc` after a completed run when the set has one. A set with no stamp, a refused run and a failed load leave it null. |
| `RecordNow(outcome)` | Writes an extra entry immediately with the inputs added so far; `interpose` uses it for its `started` entry. Throws after `Finish`. |
| `Finish(exitCode)` | Writes the single entry with outcome `exit=<code>` followed by a space and the run's outcome text when one was set, stores `FinalAnchor`, and disposes the writer. Only the first call writes; later calls return `null`. |

After every append (`RecordNow`, `Finish`) the recorder rewrites the anchor file (`AuditAnchorFile`: one ASCII line
`gridsim-audit-anchor/1 count=N head=H`, written atomically through a flushed temporary file and a move). A failed
anchor write is kept in `AnchorWriteError` and printed (`error: audit anchor ... could not be updated`); it is never
thrown from the exit handler. `--audit-anchor <file>` (needs `--audit-log`) keeps the anchor elsewhere. A truncated log
therefore stops the next run with exit 1 before anything is appended.

All members are serialised by one lock, because `Finish` can be reached from a process-exit handler on another thread.

The CLI wiring is in `src/GridSim.Cli/Program.cs`. The order of events decides what a log can contain:

1. The unknown-flag check runs first. An unknown flag prints `error: unknown flag ...` and exits 2 before the log is
   opened, so no log, no lock file and no entry exist.
2. `--audit-log <file>` (and `--audit-anchor <file>`) is removed from the argument list before the verb is
   dispatched. A missing value (`error: --audit-log expected an audit log file path but got ''`), the flag given twice,
   `--audit-anchor` without `--audit-log`, and any verb other than `estimate`, `crossval`, `interpose` or `serve` exit 2,
   still before the log is opened.
3. The provenance is chosen by the verb: `estimate` writes `Synthetic` under `--synth` and `DerivedFromRecorded`
   otherwise, `crossval` writes `Synthetic`, `interpose` writes `Recorded`, `serve` writes `DerivedFromRecorded`
   (inputs the case and a hash of every window file's name and bytes; outcome `method=... processed=... converged=...
   rejected-fresh=... failed=... unloadable=... now=<instant>`; state time the newest estimated window; a refusal
   records `rejected=ForwardInferenceException reason=<Reason>`). The web host writes one entry (action
   `web-realtime`) per resident-estimator run when `Audit:LogPath` (and optionally `Audit:AnchorPath`) is set; the log
   is opened and verified at start-up, and either setting on a host with no resident estimator stops start-up. `AuditRunRecorder.Start` failures that are
   `IOException`, `UnauthorizedAccessException`, `InvalidOperationException` or `ArgumentException` print
   `error: --audit-log '<file>': <message>` and exit 1 with no work done.
4. The recorder's `Finish` is attached to `AppDomain.ProcessExit` with the final `Environment.ExitCode`, so a normal
   return, an early return, a flag the verb refuses and `Environment.Exit` all record the run.
5. A process-wide handler turns a missing-file or missing-folder exception and a `CaseLoadException` that nothing caught
   into `error: <message>` and `Environment.Exit(1)`; the run is recorded as `exit=1` with no `unhandled=` text. Any
   other unhandled exception writes a crash log (`CrashLog`: the folder named by `GRIDSIM_CRASH_DIR` when it is set, else
   the per-user application-data folder `GridSim/crash`, else the system temporary folder; on Linux and macOS a folder
   it creates is owner-only (0700) and the log is created new and owner-only (0600), so another user of a shared
   `/tmp` cannot read or redirect it, GR-839), then sets the outcome
   `unhandled=<exception type name>` and finishes with the exit code `0xE0434352` (`-532462766`), the code the .NET
   runtime exits with on Windows.

When the entry is written the CLI prints `audit: <full path> entries=<count> head=<head hash>` to stderr: the anchor.

### The audit verbs and the shells

`gridsim audit verify|show <file> [--anchor-count N --anchor-head HEX]` (`RunAudit` in `Program.cs`) calls
`AuditLogFile.Verify`. A valid log prints `OK <file>: <n> entries, head <hash>` and exits 0; `show` first prints one
`AuditLogFile.Describe` line per entry. An invalid log prints
`TAMPERED <file>: entry <i>: <reason> (<n> entries verified before it)` to stderr and exits 3, with no entry listed.
A missing or unreadable file prints `audit: cannot read '<file>': <message>` and exits 1. A missing file, a verb other
than `verify` or `show`, or only one of the two anchor flags is a usage error and exits 2. An `--anchor-count` that is
negative or not a number is refused as a flag error and also exits 2.

| Exit | Meaning |
|---|---|
| 0 | The log verified, and matched the anchor when one was given. |
| 1 | The file is missing or unreadable. |
| 2 | Usage error. |
| 3 | The log did not verify. |

`AuditLogFile.Describe` is the one display form, used by the CLI, the desktop app and the web console:

```
#<seq> run=<runUtc> state=<stateUtc or -> provenance=<tag> hash=<hash> <summary>
```

The summary is printed with every character outside 32 to 126 replaced by `?` and each line break replaced by a space.
`AuditLogDisplay` (`src/GridSim.Core/Presentation/AuditLogDisplay.cs`) wraps verification for the shells: on success
the entry lines and the `OK` verdict, on failure no entries and only the `TAMPERED` verdict. It refuses a log larger
than `MaxLogBytes` (64 MiB), so a stray upload cannot fill the web server's memory.

### SolveTrace

`SolveTrace` (`src/GridSim.Core/Diagnostics/SolveTrace.cs`) is an opt-in recorder for what one power-flow solve
computes. A solver fills it through `PowerFlowOptions.Trace`; when `Trace` is null the solver does no capture work.
The recorder holds raw numbers and primitive indices only; `GridSim.Core.IO.SolveExport` turns it into the
`gridsim-solve-export/1` JSON document.

| Captured | Source call | Content |
|---|---|---|
| Header | `Header(solver, n, baseMva)` | Engine name (`dense-newton`, `sparse-newton`, `bfs`), bus count, system base. |
| Y-bus | `AddYBus(i, j, g, b)` | Every non-zero admittance entry (dense engine: every `(i, j)` with `G` or `B` non-zero; sparse engine: every stored entry; the sweep records none). |
| Spec | `CaptureSpec(...)` | One `BusSpecRow` per bus: type, initial `Vm` and `Va` (degrees), `Pspec`, `Qspec`, `Qd`, `Vg`, `Qmin`, `Qmax`. |
| Passes | `BeginPass(pass, effType, pvpq, pq)` | One `SolvePass` per reactive-limit outer pass: effective bus types and the P-equation and Q-equation bus index lists. |
| Iterations | `CaptureIteration(iter, maxMismatch, mismatch, correction, jacobian)` | Per Newton iteration: the largest mismatch, the mismatch vector, the correction vector (ordered P equations over the non-slack buses, then Q equations over the `Pq` buses) and, for the dense engine only, the Jacobian. The sparse engine passes no Jacobian. The sweep records its voltage delta as the "mismatch" and an empty correction. |
| Q-limit events | `AddQLimitEvent(busIndex, busId, pinnedAt, pass)` | Each `Pv` to `Pq` switch (with the limit it pinned at) and each revert (`pinnedAt` is `Revert`). |

Jacobian capture in the dense engine is `trace.CaptureJacobian || n <= trace.JacobianMaxN`. `CaptureJacobian` defaults
to false and `JacobianMaxN` to 300, so a dense solve of up to 300 buses records its Jacobian by default and a larger
one records none and sets `JacobianOmittedForSize`, unless `CaptureJacobian` forces the capture. A dense Jacobian is
`O(n^2)` per iteration, which is why the gate exists.

Where a trace is created: `gridsim <case> --enableExport[=path]` (one traced solve; `--export-jacobian` sets
`CaptureJacobian`), the `transient` verb (sampled slices of the run), the `crossval` solve export, the `estimate`
verb's `--export-solve` (an empty trace beside the projected state), `GdaReplayLoop` when an `onSolved` callback is
given, and `LiveSim.Step` when a caller passes one. The estimator has a sibling for its own iterations,
`EstimationTrace`, documented in [ch. 6, State estimation](<0006 State estimation.md>). A trace only observes: the recorder
receives primitives and clones every array it is given, and `SolveExportTests.Attaching_a_trace_does_not_change_the_solution`
compares `Vm` and `VaDeg` with and without a trace on case14, for the dense and the sparse engine. That test does not run
the sweep engine.

### The forward-inference guard

`ForwardInferenceGuard` (`src/GridSim.Core/Diagnostics/ForwardInferenceGuard.cs`) makes GridSim a historical estimator.
The horizon is one constant:

```
HorizonHours = 169.0                                  (7 days + 1 hour)
Horizon(utcNow) = utcNow - 169 h
RejectFuture(stamp, utcNow, what):
    stamp' = UtcTime.Normalise(stamp)
    throw ForwardInferenceException if stamp' > utcNow - 169 h        (utcNow defaults to DateTime.UtcNow)
AssertHistorical(stamps, utcNow, what): RejectFuture on each stamp in order; the first violation throws
```

The comparison is strict: a stamp exactly on the horizon passes and one tick later throws. A stamp equal to or later
than `utcNow` throws. A `Local` stamp is compared by the instant it names; an `Unspecified` stamp is read as UTC. The
extra hour beyond seven days keeps a full settlement week behind the boundary across a daylight-saving change; the
rationale is in the class remarks and in [ch. 8, The forward-inference guard](<0008 Provenance and invariants.md>).

No configuration key, role or per-source option changes the horizon, and no overload skips the check. The values a
caller supplies are `utcNow` (the clock the stamp is judged against) and `what` (the label in the message). The constant is
fixed, and the effective limit moves only backwards: `ForwardInferenceGuard.RequireNotAfterRealClock(requested, what)`
refuses a supplied clock later than the real one (`REJECTED (forward inference): --utc-now ... is later than the real
clock ...`, reason `NowLaterThanRealClock`, exit 2) and returns the effective now. `gridsim serve <case>
--measurements-dir <dir> --utc-now <iso>` accepts only an ISO 8601 instant (`UtcTime.TryParseIso8601`) at or before the
real clock, so a window stamped 2099-01-01 is counted as rejected-fresh with the real clock and the 2100 clock is
refused before any window is read:

```
$ gridsim serve case9 --measurements-dir results/ch31-future-dir
serving wls estimator over 1 window(s) from results/ch31-future-dir
horizon guard: now=2026-10-04T11:53:30.3707312Z, newest processable=2026-09-27T10:53:30.3707312Z
done: processed 0, converged 0, rejected-fresh 1, failed 0
error: no window produced a usable estimate: all 1 window(s) are newer than the 169 h horizon.
[exit 3]
```

`gridsim serve case9 --measurements-dir results/ch31-future-dir --utc-now 2100-01-01T00:00:00Z` exits 2 with that
refusal and reads no window. An earlier `--utc-now` is allowed: it makes a run reproducible and only tightens the
horizon. Widening `HorizonHours` itself is a source change, which `RestrictionInvariantTests` fails. An undated input
is refused too (`ForwardInferenceGuard.RequireDated`, reason `Undated`).

The message is `<what> timestamp <stamp> is newer than the historical horizon <horizon> (data must lag real time by
>=169 h). GridSim replays history only - it never processes recent or future data, so it cannot forward-infer.` The CLI
verbs that load dated input catch `ForwardInferenceException` and print `REJECTED (forward inference): <message>`, exit 2.

The dated seams that call it:

| Seam | Call | What is checked | Clock |
|---|---|---|---|
| `MeasurementSet.Load` | `RequireDated`, `RejectFuture` | `TakenUtc`; an undated set is refused | `utcNow` argument, else the real clock; `serve` passes its effective now |
| `HarmonicSourceSet.Load` | `RejectFuture` | `TakenUtc` when dated; an undated set with a measured source (`Recorded` or `DerivedFromRecorded`) is refused, an all-assumed library set may stay undated | `utcNow` argument |
| `EventCatalog.Validate` (called by `Load` and by the built-in catalogue) | `RejectFuture` | every event's `WhenUtc` | `utcNow` argument |
| `NesoFrequencyCsv.Read` | `RejectFuture` | every parsed row, as it is enumerated | the real clock only |
| `GdaTimeSeries.Load` | `AssertHistorical` | every step's `TimeUtc` | `utcNow` argument |
| `GdaReplayLoop.Replay` | `AssertHistorical` | every step's `TimeUtc`, again at the ingestion boundary | `utcNow` argument |
| `RtuMeasurementSetMapper.Map` | `RejectFuture` | the window instant, before any RTU value is admitted | `utcNow` argument; the live `interpose` flush judges against the real clock |
| `PmuMeasurementSetMapper.Map` | `RejectFuture` | the superframe's `TimeUtc`, before any PMU value is admitted | `utcNow` argument, else the real clock |
| `pmu-ingest` | `RejectFuture` | every decoded DATA frame's time, before anything is printed | the real clock |
| `PresolvedReplay.Load(dir, utcNow)` | `RejectFuture` | every `time_utc` row of the sidecar (named by CSV line) | `utcNow` argument (not after the real clock), else the real clock |
| web `ReplayDirectoryWindowFeed` | `RejectFuture`, `RequireDated` | each window; a fresh or undated window is skipped and listed in `SkippedFiles` | the real clock |
| `GridBridgeClient` state handler | `RejectFuture` | the tick timestamp, before the frame is built; `DateTime.MinValue` (an undated frame) is refused first | `DateTime.UtcNow`, no allowance |
| `RealtimeEstimatorEngine.Run` | `RequireNotAfterRealClock`, `RejectFuture` | `opt.UtcNow` not after the real clock; each window's `TakenUtc`; an undated window throws (reason `Undated`); a window inside the horizon is counted as rejected-fresh and skipped | `opt.UtcNow` |
| `RealtimeDseEngine` | the same | the same, per window | `opt.UtcNow` |
| `estimate --phaseangle` | `RejectFuture` | the target time of the phase-angle override rows | the real clock |

No seam is switched off on the live paths. `serve` loads every window with its effective now and counts a fresh one
as rejected-fresh. The live `interpose` flush (`InterposePipeline.FlushWindow(fallback, InterposeFreshPolicy, utcNow)`)
judges each window against the real horizon: a window older than the horizon (a replayed capture) maps through the
guarded `Map` and is written; a window newer than it (live telemetry) is never written to `--measurements-dir`, is
counted `held-fresh`, and is kept only as a quarantine capture in `--out-store`
(`RtuMeasurementSetMapper.MapQuarantineCapture`, never an estimator input). A run with `--measurements-dir` that held
any window ends `REJECTED (forward inference)`, exit 2. `interpose` has no `--utc-now`.

Two engines also hold an output guard. `RestrictionReview.AssertOutputHistorical` (below) is called by
`RealtimeEstimatorEngine` and `RealtimeDseEngine` on the stamp of each estimate before it is emitted, with the same
horizon. The serve loop's rejected-fresh count is the `serve` verb's `rejected-fresh` figure.

Code that reads `ForwardInferenceGuard.Horizon` or `HorizonHours` without judging a stamp: the web session's bridge
start time (`Horizon(UtcNow)` less `PresentationLimits.SettlementLagMarginDays`, 2 days), the default register as-of
date of the model materialiser (`Horizon(UtcNow)` unless `--as-of` is given), the quarantine store's sidecar marker
(`horizon_hours`), and the messages printed by `serve` and `interpose`.

### Provenance

`ProvenanceTag` (`src/GridSim.Core/Diagnostics/Provenance.cs`) has seven members. The audit log stores the member
name; the numeric value is never written.

| Value | Member |
|---|---|
| 0 | `Recorded` |
| 1 | `DerivedFromRecorded` |
| 2 | `CuratedPlaceholder` |
| 3 | `Synthetic` |
| 4 | `Counterfactual` |
| 5 | `Interpolated` |
| 6 | `UnvalidatedAtScale` |

The meaning of each member, the one-way flow (trust only moves down, never up) and the `[VERIFY]` pairing for
`CuratedPlaceholder` are specified in [ch. 8, The `ProvenanceTag` taxonomy](<0008 Provenance and invariants.md>).
In the audit log the tag is the trust class of what the run produced: `DerivedFromRecorded` for an estimate from a
measurement file and for `serve` and the web host's resident estimator, `Synthetic` for `--synth` and for `crossval`,
`Recorded` for `interpose`. A log entry with a value
outside the enum is refused at write time and at read time.

### RestrictionReview

`RestrictionReview` (`src/GridSim.Core/Diagnostics/RestrictionReview.cs`) is the review gate for invariant 5 of
`UnderstandingRestrictions.md`: fusing a dynamic process model into the estimator (recursive or forecasting-aided
estimation) is the one place where an estimator could quietly become a predictor.

```
AssertPermitted(method, usesDynamicsModel):
    if not usesDynamicsModel                          -> return (static, measurement-only estimation is always allowed)
    entry = Reviewed[method]                          (ordinal name match)
    if entry is missing or not entry.UsesDynamicsModel -> throw RestrictionViolationException
    if not entry.OutputPresentPastOnly                 -> throw RestrictionViolationException

AssertOutputHistorical(outputStamp, utcNow, what):
    throw RestrictionViolationException if UtcTime.Normalise(outputStamp) > Horizon(utcNow)
```

`Reviewed` is a code-resident table of `RestrictionReviewEntry(Method, UsesDynamicsModel, OutputPresentPastOnly,
ReviewNote)`. It holds four entries: `Ekf`, `Ukf`, `EnKf` and `FrequencyRocofKf`, each marked as using a dynamics
model and as certifying present or past output only. Each of the four filters calls
`AssertPermitted(MethodName, usesDynamicsModel: true)` when it is constructed
(`KalmanStateEstimator`, `UnscentedKalmanStateEstimator`, `EnsembleKalmanStateEstimator`, `FrequencyRocofFilter`), so an
unreviewed method fails before it runs. `AssertPermitted` checks a reviewer's attestation; `AssertOutputHistorical` is
the mechanical check at the emit boundary that a predict step cannot leak a future-dated state into an output frame.
The emitted estimate of a filter is the filtered present, stamped at the window time.

## Parameters and defaults

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| Format tag | `gridsim-audit/1` | text | Written into every line; any other value is a parse failure | `AuditLogFile.FormatTag` |
| Genesis | `GENESIS` | text | `prevHash` of entry 0 | `AuditLog.Genesis` |
| Hash | SHA-256, upper-case hex | 64 characters | Over the six-field payload | `AuditLog.ComputeHash` |
| Payload separator | `\|` | character | Joins the six fields | `AuditLog.ComputeHash` |
| Null state time in the payload | `-` | character | Stands for a null `stateUtc` in the payload only | `AuditLog.ComputeHash` |
| Timestamp notation | `o`, UTC, `Z` | text | `runUtc`, `stateUtc` in the file and in the payload | `AuditLogFile.FormatUtc` |
| Line terminator | LF | byte | Exactly one per entry; CRLF, a BOM and a missing final LF fail verification | `AuditLogFile.EncodeLine` |
| Lock file | `<log>.lock` | path | Exclusive handle held for the life of a writer; left on disk | `AuditLogWriter.Open` |
| Entry limit | `int.MaxValue` | entries | `Append` refuses to go beyond it | `AuditLogWriter.Append` |
| Verifier read limit | `Array.MaxLength` | bytes | A larger file cannot be verified in one pass | `AuditLogFile.ReadAll` |
| Shell display cap | 64 MiB (`64 * 1024 * 1024`) | bytes | Largest log the desktop and web shells accept | `AuditLogDisplay.MaxLogBytes` |
| Token characters | letters, digits, `. _ - @ /` | characters | Allowed in actor, action and input names | `AuditEvent.RequireToken` |
| Input hash | 64 upper-case hex characters | text | `AuditInput` refuses anything else | `AuditInput` |
| Unhandled-exception exit code | `0xE0434352` (`-532462766`) | int | Recorded when the CLI sees an unhandled exception | `Program.cs`, `AuditRunRecorder` wiring |
| Horizon | 169.0 | hours | Minimum age of any processed datum | `ForwardInferenceGuard.HorizonHours` |
| Bridge start margin | 2 | days | Extra age beyond the horizon for a web bridge session's start | `PresentationLimits.SettlementLagMarginDays` |
| `SolveTrace.CaptureJacobian` | `false` | bool | Force Jacobian capture whatever the size | `SolveTrace.CaptureJacobian` |
| `SolveTrace.JacobianMaxN` | 300 | buses | Dense Jacobian is captured up to this size | `SolveTrace.JacobianMaxN` |
| Reviewed dynamics-fused methods | `Ekf`, `Ukf`, `EnKf`, `FrequencyRocofKf` | names | The only dynamics-fused methods that may run | `RestrictionReview.Reviewed` |

## Invariants

Every test below is in `tests/GridSim.Tests`.

| Invariant | Pinned by |
|---|---|
| The file and in-memory logs use one hash function: a file round-trips to the same hashes, and a saved in-memory log loads back and can be appended to | `AuditLogFileTests.Round_trip_file_entries_equal_the_in_memory_log_hash_for_hash`, `Save_of_an_in_memory_log_loads_back_identically_and_can_be_appended_to`, `AuditLogTests.A_clean_log_verifies_and_chains_from_genesis` |
| An `Unspecified` timestamp is stored as UTC with no shift and hashes as in memory | `AuditLogFileTests.An_unspecified_kind_timestamp_is_stored_as_utc_with_no_shift_and_hashes_as_in_memory` |
| A line is one compact canonical JSON object; any re-spelling is refused | `Line_format_is_one_compact_canonical_json_object_per_line`, `A_hand_reformatted_line_is_rejected_even_though_its_values_are_unchanged`, `A_timestamp_rewritten_as_non_utc_is_rejected`, `A_line_that_is_not_json_is_rejected`, `An_extra_field_is_rejected`, `Crlf_line_endings_from_an_editor_are_rejected`, `A_byte_order_mark_from_an_editor_is_rejected` (all `AuditLogFileTests`) |
| An edit is found at the edited entry, or at the next entry when its hash was recomputed | `Editing_an_entry_is_detected_at_that_entry`, `Editing_an_entry_and_recomputing_its_hash_is_detected_at_the_next_entry`, `AuditLogTests.Tampering_with_an_entry_breaks_verification` |
| Deletion, reordering, a blank line and a cut-off line are found at the exact entry | `Deleting_a_middle_entry_is_detected_where_it_was`, `Deleting_the_first_entry_is_detected_at_index_zero`, `Reordering_entries_is_detected_at_the_first_moved_entry`, `Inserting_a_blank_line_is_detected`, `Truncating_through_the_last_entry_is_detected_at_that_entry`, `Truncating_through_a_middle_entry_is_detected_at_that_entry`, `AuditLogTests.Reordering_entries_is_detected` |
| Tail truncation and a whole-log rewrite pass verification alone and are caught by the anchor; an older anchor still verifies a grown log | `Removing_whole_entries_from_the_end_is_caught_by_the_anchor_and_only_by_the_anchor`, `Rewriting_the_whole_log_with_fresh_hashes_is_caught_by_the_anchor`, `An_anchor_taken_earlier_still_verifies_a_log_that_has_grown_since` |
| An empty file is a valid empty log; a missing file is an error | `An_empty_log_verifies_with_zero_entries_and_the_genesis_head`, `A_missing_log_is_an_error_never_an_empty_log` |
| A damaged, torn or anchor-mismatched log is never appended to, and is left untouched; a missing directory is an error | `Opening_a_tampered_log_for_writing_fails_loud_and_leaves_it_untouched`, `Opening_a_log_with_a_torn_last_line_fails_loud`, `Opening_with_an_anchor_the_log_no_longer_matches_fails_loud`, `A_missing_directory_is_an_error_not_created_silently` |
| One writer at a time; concurrent appends keep a gap-free chain; a disposed writer refuses to append | `A_second_writer_on_the_same_log_fails_loud_while_the_first_is_open`, `Concurrent_appends_from_two_threads_keep_a_valid_gap_free_chain`, `A_disposed_writer_refuses_to_append` |
| An undefined provenance is refused before anything is written | `An_undefined_provenance_is_refused_before_anything_is_written` |
| The summary layout is fixed; ambiguous fields fail before a write; input hashes are upper-case SHA-256 | `Recorded_event_has_the_fixed_summary_layout_and_verifies`, `Event_with_no_inputs_records_a_dash`, `Ambiguous_or_multi_line_event_fields_fail_loud_and_write_nothing`, `Input_names_and_hashes_are_validated`, `Input_hashes_are_sha256_upper_hex`, `Default_actor_is_a_valid_token` |
| A run writes exactly one entry, with its exit code, however it ends; the log is opened before any work and released after `Finish` | `AuditRunRecorderTests.A_finished_run_writes_one_verifiable_entry_with_exit_code_inputs_and_outcome`, `Finish_writes_exactly_once_however_often_it_is_reached`, `A_run_without_an_outcome_still_records_its_exit_code`, `The_log_is_released_after_finish_so_the_next_run_can_append`, `Start_holds_the_single_writer_lock_until_finish`, `Start_refuses_a_tampered_log_before_any_work`, `Start_refuses_a_missing_directory_and_a_bad_actor_or_action_without_creating_a_file`, `RecordNow_writes_a_start_entry_before_the_final_one` |
| A bundled case name is recorded as an id, never as a content hash; a directory hash covers content, names and presence and ignores enumeration order | `A_case_name_is_recorded_as_an_id_never_as_a_content_hash`, `A_directory_hash_covers_content_names_and_presence_and_ignores_enumeration_order` |
| `--audit-log` is refused on every other verb and the verbs record their inputs and outcome (pinned by assertions on the text of `Program.cs`, not by running the verbs) | `Cli_audit_log_is_a_known_flag_refused_on_unaudited_verbs_and_opened_before_the_verb_runs`, `Cli_audited_verbs_record_their_inputs_and_outcome` |
| The estimate outcome text has one source and an unchanged form for a run that did not hit the IRLS cap | `N064AuditShowAndIrlsExportTests.Outcome_of_a_run_that_did_not_hit_the_cap_is_unchanged`, `Outcome_names_the_irls_cap_when_it_was_reached_before_the_extra_fields`, `EstimationAuditTrailTests.Records_estimates_as_a_verifiable_chained_trail` |
| `audit show` prints every entry then `OK`, prints nothing of a tampered log, exits 3, and the usage errors exit 2 | `N064AuditShowAndIrlsExportTests.Cli_audit_show_prints_every_entry_then_the_ok_line`, `Cli_audit_show_prints_nothing_of_a_tampered_log_and_exits_3`, `Cli_audit_verify_prints_no_entries`, `Cli_audit_usage_errors_exit_2`, `Describe_gives_one_ascii_line_with_every_field` |
| The shells show a log only through `AuditLogDisplay`, line for line as the CLI, and refuse an oversize upload | `N151ShellAuditIrlsHarmonicsTests.The_shell_display_matches_the_built_cli_audit_show_line_for_line`, `Both_shells_render_the_audit_log_only_through_the_core_display`, `An_upload_over_the_cap_is_refused`, `A_missing_log_fails_loud_never_an_empty_clean_log` |
| The horizon is one constant, 169 hours | `RestrictionInvariantTests.Inv1_The_horizon_is_a_single_knob`, `ForwardInferenceGuardTests.The_horizon_boundary_is_exactly_HorizonHours` |
| The comparison is strict: the horizon instant passes, one tick later throws; recent and future stamps throw | `ForwardInferenceGuardTests.The_exact_horizon_instant_passes_and_one_tick_inside_throws`, `A_recent_stamp_inside_the_horizon_is_rejected`, `A_future_stamp_is_rejected`, `A_stamp_beyond_the_horizon_passes` |
| `Unspecified` is UTC and `Local` is compared by instant | `ForwardInferenceGuardTests.Unspecified_kind_is_treated_as_utc`, `A_local_kind_stamp_is_compared_by_true_instant_not_naive_ticks` |
| `AssertHistorical` throws on the first violation and passes a clean series | `ForwardInferenceGuardTests.AssertHistorical_throws_on_the_first_future_row`, `AssertHistorical_passes_an_all_historical_series` |
| The ingestion seams refuse a fresh stamp | `EstimationMeasurementTests.Rejects_a_timestamp_newer_than_the_historical_horizon`, `RestrictionInvariantTests.Inv1_A_dated_ingestion_seam_rejects_a_fresh_stamp`, `Inv2_The_state_time_channel_is_guarded_end_to_end`, `GdaReplayLoopTests.A_future_dated_series_is_rejected_at_ingestion`, `Load_itself_rejects_a_series_dated_just_inside_the_horizon`, `EventCatalogTests.A_future_dated_event_is_rejected_on_load`, `HarmonicSourceSetTests.A_fresh_timestamp_is_refused_but_a_historical_one_is_accepted`, `RtuMeasurementSetMapperTests.Present_time_window_is_rejected_by_the_horizon_guard`, `PmuMeasurementSetMapperTests.A_fresh_superframe_is_refused_by_the_guard`, `BridgeClientRobustnessTests.A_frame_inside_the_horizon_by_ten_minutes_is_refused` |
| A violation at the bridge ends the receive loop; an undated real-time window is refused | `BridgeClientRobustnessTests.A_forward_inference_violation_is_still_loop_fatal`, `RealtimeEstimatorEngineTests.An_undated_window_is_refused`, `RealtimeDseEngineTests.An_undated_window_is_refused`, `RestrictionInvariantTests.Inv4_A_realtime_window_stamped_fresh_is_refused` |
| An unreviewed dynamics-fused method is refused; a static method is permitted; every reviewed entry certifies present or past output and carries a note | `RestrictionInvariantTests.Inv5_A_dynamics_fused_method_without_review_is_rejected`, `Inv5_A_static_measurement_only_method_is_permitted_without_review`, `Inv5_Every_reviewed_entry_certifies_present_past_output_and_carries_a_note` |
| The output guard uses the input horizon: a future-dated output is refused, a historical one passes, the boundary matches | `RestrictionInvariantTests.Inv5_A_present_past_stamped_output_is_permitted`, `Inv5_A_future_dated_output_is_rejected_mechanically`, `Inv5_The_output_guard_boundary_matches_the_input_horizon` |
| Model-output provenance labels are distinct from `Recorded` | `RestrictionInvariantTests.Inv3_Model_output_provenance_labels_are_distinct_from_recorded` |
| The trace records Y-bus, spec, iterations and Jacobian; the Jacobian is gated by size and forceable; the sweep records sweeps and no Y-bus | `SolveExportTests.Dense_trace_captures_ybus_spec_iterations_and_jacobian`, `Jacobian_is_gated_by_size_and_forceable`, `Bfs_trace_has_no_ybus_but_records_sweeps` |
| Attaching a trace changes no result bit, dense or sparse | `SolveExportTests.Attaching_a_trace_does_not_change_the_solution` |
| Reactive-limit switching is recorded | `S4SolverConvergenceTests` (the `QLimitEvents` assertions) |

No test pins that `NesoFrequencyCsv.Read` refuses a fresh row, that `MeasurementSet.Load` accepts a set with no
`takenUtc`, or that a rejected input is itself written to the audit log; see Limits.

## Worked example

Entry 0 of a log written by `gridsim estimate case14 --synth --audit-log results/ch26.log`, exactly as stored (the
capture is the file `docs/_captures/audit/log-line.txt`):

```
$ head -n 1 results/ch26.log
{"format":"gridsim-audit/1","seq":0,"runUtc":"2026-10-04T11:01:57.2144688Z","stateUtc":"2024-03-01T12:00:00.0000000Z","provenance":"Synthetic","summary":"actor=analyst@workstation action=estimate inputs=case-id:23CF1F80D96A7350F3BB4001DC5422B2B3BC021EA4EFB31264FE199A68D4C271,synthetic-measurements:A59ED1067B27834A405D499E1C4090C10BAD6DF51800370A6923B10F54D6F53F outcome=exit=0 method=Wls status=Converged observable=True J=80.01052231478454 chi2=PASS dof=95 removed=0 measurements=122","prevHash":"GENESIS","hash":"1B78AE0F3653078034D6F6D46E71BDC42CB5B2156DFDD7FAFED0C3B80347E8A4"}
[exit 0]
```

The hash is recomputed by hand with the payload rule above. The script is
`docs/_captures/audit/recompute-hash.py`; it reads the JSON line from the file it is given, builds the payload and
hashes it:

```python
import hashlib, json, sys

for line in open(sys.argv[1], encoding="utf-8"):
    if not line.startswith("{"):
        continue
    e = json.loads(line)
    state = "-" if e["stateUtc"] is None else e["stateUtc"]
    payload = "|".join([str(e["seq"]), e["runUtc"], state, e["provenance"], e["summary"], e["prevHash"]])
    print("payload:", payload)
    print("stored:    ", e["hash"])
    print("recomputed:", hashlib.sha256(payload.encode("utf-8")).hexdigest().upper())
    if "status=Converged" in payload:
        tampered = payload.replace("status=Converged", "status=Diverged")
        print("one word changed (status=Diverged):", hashlib.sha256(tampered.encode("utf-8")).hexdigest().upper())
```

```
$ python docs/_captures/audit/recompute-hash.py docs/_captures/audit/log-line.txt
payload: 0|2026-10-04T11:01:57.2144688Z|2024-03-01T12:00:00.0000000Z|Synthetic|actor=analyst@workstation action=estimate inputs=case-id:23CF1F80D96A7350F3BB4001DC5422B2B3BC021EA4EFB31264FE199A68D4C271,synthetic-measurements:A59ED1067B27834A405D499E1C4090C10BAD6DF51800370A6923B10F54D6F53F outcome=exit=0 method=Wls status=Converged observable=True J=80.01052231478454 chi2=PASS dof=95 removed=0 measurements=122|GENESIS
stored:     1B78AE0F3653078034D6F6D46E71BDC42CB5B2156DFDD7FAFED0C3B80347E8A4
recomputed: 1B78AE0F3653078034D6F6D46E71BDC42CB5B2156DFDD7FAFED0C3B80347E8A4
one word changed (status=Diverged): 92EAC185FC4348F4E00437959545B6EB7838873D9AADF90BE6F1569F39F82D2D
[exit 0]
```

The recomputed hash equals the stored hash to the last character. Changing one word of the summary gives a completely
different hash, which is why `audit verify` reports an entry with that edit as `the stored hash does not match`.
The payload has `prevHash` last and no field for `format`, and the state time appears as the full `o` timestamp.

A null state time hashes as `-`. This is the case of a run refused by the guard before it measured anything. The command
below points `estimate` at a measurement set stamped in 2099 with `--audit-log`; the guard rejects it, the run exits 2,
and the run is still recorded, with `exit=2` and no state time. The log's own hash and the head printed on stderr agree
(the run time, and so every hash, differs on every run):

```
$ gridsim estimate case9 --measurements docs/_captures/estimation/case9-telemetry-future.json --out results/ch31-future --audit-log results/ch31-future.log
REJECTED (forward inference): measurement set timestamp 2099-01-01T12:00:00.0000000Z is newer than the historical horizon 2026-10-13T04:23:45.3695438Z (data must lag real time by >=169 h). GridSim replays history only - it never processes recent or future data, so it cannot forward-infer.
audit: results\ch31-future.log entries=2 head=C796C9B48CCF2321C435514F86A0C301C925F3466C844CA0D1B31235BB9A5581 security-events=1
[exit 2]
```

The horizon in the message is 169 hours before the moment of the run (`2026-10-20T05:23:45` less 7 days and 1 hour).
The `audit:` line counts two entries, one of them a security event (`security-events=1`). The `head` capture below is
of an earlier run of the same command, whose log held the one estimate entry.

```
$ gridsim audit show results/ch31-future.log
#0 run=2026-10-04T11:16:34.0620693Z state=- provenance=DerivedFromRecorded hash=FB0C45EDA313ADBC307CE54F6FD886E1B7B628AB82046E312549FD19EC0906B6 actor=analyst@workstation action=estimate inputs=case-id:D784A646F9FED50DEE41E22A244DBAC3BF6B790D2476876CCB2B3A737613CBEB,measurements:7B6797C69054B59D7E3D8A232C68A43C1C7C686EF486892F378C16FD661A25EA outcome=exit=2
OK results/ch31-future.log: 1 entries, head FB0C45EDA313ADBC307CE54F6FD886E1B7B628AB82046E312549FD19EC0906B6
[exit 0]
```

The stored line has `"stateUtc":null`:

```
$ head -n 1 results/ch31-future.log
{"format":"gridsim-audit/1","seq":0,"runUtc":"2026-10-04T11:16:34.0620693Z","stateUtc":null,"provenance":"DerivedFromRecorded","summary":"actor=analyst@workstation action=estimate inputs=case-id:D784A646F9FED50DEE41E22A244DBAC3BF6B790D2476876CCB2B3A737613CBEB,measurements:7B6797C69054B59D7E3D8A232C68A43C1C7C686EF486892F378C16FD661A25EA outcome=exit=2","prevHash":"GENESIS","hash":"FB0C45EDA313ADBC307CE54F6FD886E1B7B628AB82046E312549FD19EC0906B6"}
[exit 0]
```

and the same script recomputes it with the `-` in the third field:

```
$ python docs/_captures/audit/recompute-hash.py docs/_captures/audit/guard-refused-line.txt
payload: 0|2026-10-04T11:16:34.0620693Z|-|DerivedFromRecorded|actor=analyst@workstation action=estimate inputs=case-id:D784A646F9FED50DEE41E22A244DBAC3BF6B790D2476876CCB2B3A737613CBEB,measurements:7B6797C69054B59D7E3D8A232C68A43C1C7C686EF486892F378C16FD661A25EA outcome=exit=2|GENESIS
stored:     FB0C45EDA313ADBC307CE54F6FD886E1B7B628AB82046E312549FD19EC0906B6
recomputed: FB0C45EDA313ADBC307CE54F6FD886E1B7B628AB82046E312549FD19EC0906B6
[exit 0]
```

Every timestamp, hash and the actor in these captures differ by machine and by run; the commands are real and the
structure is exact. The measurement file's hash (`7B67...`) is in the entry although the run was refused, because the
file was hashed as an input before it was loaded. The same refusal in a run with no `--audit-log` is captured in
`docs/_captures/estimation/err-future-stamp.txt`. Verification of damaged logs, with and without an anchor, is shown
in the user chapter [26](<../manual/0026 Audit log, deployment and assurance.md>).

## Source map

| File | Type | Responsibility |
|---|---|---|
| `src/GridSim.Core/Diagnostics/AuditLog.cs` | `AuditLogEntry`, `AuditLog` | The entry record, the in-memory chain, `Genesis`, and the one `ComputeHash`. |
| `src/GridSim.Core/Diagnostics/AuditLogFile.cs` | `AuditLogFile`, `AuditAnchor`, `AuditLogVerification`, `AuditLogTamperException` | Line encoding and strict parsing, `Verify`, `Load`, `Save`, `Describe`, anchor checks. |
| `src/GridSim.Core/Diagnostics/AuditLogWriter.cs` | `AuditLogWriter` | Single-writer lock, open-time verification, durable append, fault state, `Anchor`. |
| `src/GridSim.Core/Diagnostics/AuditEvent.cs` | `AuditEvent`, `AuditInput` | Summary layout, token rules, input hashes, default actor. |
| `src/GridSim.Core/Diagnostics/AuditRunRecorder.cs` | `AuditRunRecorder` | One-run lifecycle, `RecordNow`, `Finish`, `InputForArgument`, `HashDirectory`. |
| `src/GridSim.Core/Diagnostics/UtcTime.cs` | `UtcTime` | The one `DateTime` to UTC rule. |
| `src/GridSim.Core/Estimation/EstimationAuditTrail.cs` | `EstimationAuditTrail` | `Outcome` text for an estimate; stand-alone `Record`. |
| `src/GridSim.Core/Presentation/AuditLogDisplay.cs` | `AuditLogDisplay` | Shell display of a log, anchor boxes, the 64 MiB cap. |
| `src/GridSim.Cli/Program.cs` | `--audit-log` block, `RunAudit` | Flag parsing, provenance choice, process-exit recording, `audit verify` and `audit show`. |
| `src/GridSim.Core/Diagnostics/SolveTrace.cs` | `SolveTrace` and its row types | Opt-in solve recorder. |
| `src/GridSim.Core/IO/SolveExport.cs` | `SolveExport` | Serialises a trace as `gridsim-solve-export/1`. |
| `src/GridSim.Core/Diagnostics/ForwardInferenceGuard.cs` | `ForwardInferenceGuard`, `ForwardInferenceException` | The 169 hour horizon. |
| `src/GridSim.Core/Diagnostics/Provenance.cs` | `ProvenanceTag` | The seven trust classes. |
| `src/GridSim.Core/Diagnostics/RestrictionReview.cs` | `RestrictionReview`, `RestrictionReviewEntry`, `RestrictionViolationException` | Review table, `AssertPermitted`, `AssertOutputHistorical`. |

The tests are `AuditLogFileTests.cs`, `AuditLogTests.cs`, `AuditRunRecorderTests.cs`, `EstimationAuditTrailTests.cs`,
`N064AuditShowAndIrlsExportTests.cs`, `N151ShellAuditIrlsHarmonicsTests.cs`, `ForwardInferenceGuardTests.cs`,
`RestrictionInvariantTests.cs`, `SolveExportTests.cs` and the seam tests named in the invariants table.

## Limits

- The chain uses plain SHA-256 with no key: a log rewritten from end to end with recomputed hashes verifies unless an anchor kept out of the writer's reach is supplied.
- The anchor file sits beside the log by default; an attacker who can write both can rewrite both. `--audit-anchor` (or `Audit:AnchorPath`) puts it out of the writer's reach. A log whose anchor file was deleted is accepted once, with the gap recorded in the next entry.
- `runUtc` is the writing machine's clock and `actor` is the operating-system account name; neither is attested.
- A run killed forcibly, or cut by power loss, leaves no entry; only `interpose` writes an entry before the end of the run.
- Only `estimate`, `crossval`, `interpose`, `serve` and the web host's resident estimator write audit entries; live dashboard sessions and `POST /api/solve` are model solves and are not audited. An unknown flag, a missing `--audit-log` value and `--audit-log` on an unaudited verb are refused before the log opens and leave no entry.
- Verification reads the whole file into one array, so a log larger than `Array.MaxLength` cannot be verified, and the desktop and web shells refuse one larger than 64 MiB.
- The summary is stored in clear text, and no code removes a secret placed in an outcome string.
- `EstimationAuditTrail.Record` writes a stand-alone entry to an in-memory `AuditLog` and nothing in `src` calls it; the CLI uses `EstimationAuditTrail.Outcome`.
- An all-assumed harmonic source set (no `Recorded` or `DerivedFromRecorded` source) may be undated and then has no horizon check.
- `NesoFrequencyCsv.Read` judges every row against the real clock only (`Doc3HorizonGuardTests.D194_a_fresh_row_is_refused_and_a_historical_file_reads` pins the refusal).
- The review register is the code table `RestrictionReview.Reviewed`; no separate document mirrors it.
- The web AUDIT LOG tab belongs to a live session's tab list; a replay-only instance opens no live session (its `/dashboard` gate answers 404 for every id but a configured raw replay), so the tab is not reachable there.
- The solve trace of the sparse engine carries no Jacobian, and the sweep records no Y-bus.

## See also

- [Ch. 26, Audit log, deployment and assurance](<../manual/0026 Audit log, deployment and assurance.md>) - the user view: running, reading and verifying a log.
- [Ch. 10, File and wire formats](<0010 File and wire formats.md>) - `gridsim-audit/1` among the other formats.
- [Ch. 8, Provenance and invariants](<0008 Provenance and invariants.md>) - the tags, their flow rules and the guard's rationale.
- [Ch. 6, State estimation](<0006 State estimation.md>) - what `estimate` computes and the estimator trace.
- [Ch. 9, GDA integration](<0009 GDA integration.md>) and [ch. 20, The RTU/SCADA interposer](<0020 The RTU and SCADA interposer.md>) - the data lake and live-telemetry seams.
- [Ch. 18, Real-time, distribution and dynamic estimation](<0018 Real-time, distribution and dynamic estimation.md>) - the real-time engines and the filters that `RestrictionReview` gates.
- [Ch. 35, Deployment and security](<0035 Deployment and security.md>) - the web host, CI and the assurance documents.
- [Ch. 19, audit](<../manual/0019 CLI reference.md#audit>), [estimate](<../manual/0019 CLI reference.md#estimate>), [crossval](<../manual/0019 CLI reference.md#crossval>) and [interpose](<../manual/0019 CLI reference.md#interpose>) - the verbs' flags and exit codes.
