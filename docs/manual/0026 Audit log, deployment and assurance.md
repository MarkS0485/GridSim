# 26. Audit log, deployment and assurance

This chapter covers what you need when GridSim stops being a tool on your own machine and becomes something other
people rely on. Three things change. First, a result may have to be defended later, so the run that produced it must
leave a record that cannot be quietly altered: the tamper-evident audit log. Second, the web app may have to run
unattended behind a proxy: the Docker image, the compose file, nginx, single sign-on and the CI that builds them.
Third, someone outside the team may ask what assurance GridSim offers: the three documents under `docs/assurance/`
say what is claimed and where the gaps are, and this chapter tells you how to read them. The chapter ends with the
limits that remain.

The `serve` verb belongs to the second topic only by name. It is not a web server; see
[Do not confuse `serve` with the web host](#do-not-confuse-serve-with-the-web-host).

Timings, run times and hashes in the captured output below differ by machine and by run, because each audit entry
carries its own time and the name of the account that ran it. The commands are real; the specific hashes are not
repeatable.

## The audit log

### Which verbs write one

Exactly four verbs accept `--audit-log <file>`: [`estimate`](<0019 CLI reference.md#estimate>),
[`crossval`](<0019 CLI reference.md#crossval>), [`interpose`](<0019 CLI reference.md#interpose>) and
[`serve`](<0019 CLI reference.md#serve>). Any other verb, or the flag given twice, exits 2 before it does anything (the
message reads `--audit-log applies to estimate, crossval, interpose and serve only`):

```
$ gridsim case14 --once --audit-log results/ch26x.log
error: --audit-log applies to estimate, crossval, interpose and serve only (got 'case14').
[exit 2]
```

The log is opened, and verified if it already exists, before the verb starts, against its anchor file (see
[Anchors](#anchors)). A log that does not verify, a log that lost entries or was rewritten since the anchor was
written, a damaged anchor file, a log that another run holds open, a log in a directory that does not exist, or (with
`--audit-verify-key`) a missing or bad anchor signature stops the run with exit 1 and no work is done:

```
$ gridsim estimate case14 --synth --audit-log results/audit-docs-tampered.log

=== WLS state estimate: case14 ===
  status: Converged   iterations: 5   max |dx|: 2.72E-009
  measurements: 122   states: 27   redundancy (dof): 95
  observability: observable (rank 27/27)
  consistency: J = 80.01 vs chi2(95%) = 118.75 -> PASS
  vs synthetic truth: max |dVm| = 1.330E-003 pu   max |dVa| = 2.248E-001 deg

  -> results\estimate-case14
audit: results\audit-docs-tampered.log entries=1 head=3FD84D1919BD434198149447EF57A11F3DBDD54F2D9980115D10D49B448CA9B9
[exit 0]
```

A damaged log is never appended to. You investigate it, then start a new log file.

### Worked example: two runs, one log

Two estimates, appended to the same file. The log is created by the first run; the directory must already exist.

```bash
gridsim estimate case14 --synth --audit-log results/ch26.log
gridsim estimate case30 --synth --audit-log results/ch26.log
```

```
$ gridsim estimate case14 --synth --audit-log results/ch26.log
...
  -> results/estimate-case14
audit: results/ch26.log entries=1 head=1B78AE0F3653078034D6F6D46E71BDC42CB5B2156DFDD7FAFED0C3B80347E8A4
[exit 0]
```

```
$ gridsim estimate case30 --synth --audit-log results/ch26.log
...
  -> results/estimate-case30
audit: results/ch26.log entries=2 head=A5875A1F529483C92876C3ED0CC801B6EE0F452C5C033E3B1BAB7317D18D45DA
[exit 0]
```

The estimate report itself is unchanged by the flag (it is the report described in
[ch. 21](<0021 State estimation.md>)). The extra line, printed to stderr when the run ends, is the log's
**anchor**: the number of entries and the hash of the last one. The same pair is written to the anchor file, one line
`gridsim-audit-anchor/1 count=N head=H`, by default `<log>.anchor` beside the log.

### Anchors

After each entry the run writes the anchor to the anchor file, and the next run opens the log against it, so a log that
lost entries from its end, was replaced by an older copy, or was rewritten with recomputed hashes is refused before
anything is appended. `--audit-anchor <file>` names the anchor file; its folder must exist. The default beside the log
catches accidental truncation and a restored copy; an anchor file the log's writer cannot change (another account's
share, write-once media) also catches a deliberate edit of both. A log that has entries but no anchor file is accepted
with a warning on stderr, that run's entry ends `audit-anchor=missing`, and the anchor is written from then on. Keep the
printed `audit:` line elsewhere too: it is the evidence worth copying somewhere the log's writer cannot reach (see
[Where to keep the log](#where-to-keep-the-log)).

Read the log with `gridsim audit show`, which verifies the whole chain first and prints no entries, only the
`TAMPERED` line, if it does not verify:

```
$ gridsim audit show results/ch26.log
#0 run=2026-10-04T11:01:57.2144688Z state=2024-03-01T12:00:00.0000000Z provenance=Synthetic hash=1B78AE0F3653078034D6F6D46E71BDC42CB5B2156DFDD7FAFED0C3B80347E8A4 actor=analyst@workstation action=estimate inputs=case-id:23CF1F80D96A7350F3BB4001DC5422B2B3BC021EA4EFB31264FE199A68D4C271,synthetic-measurements:A59ED1067B27834A405D499E1C4090C10BAD6DF51800370A6923B10F54D6F53F outcome=exit=0 method=Wls status=Converged observable=True J=80.01052231478454 chi2=PASS dof=95 removed=0 measurements=122
#1 run=2026-10-04T11:01:58.0092487Z state=2024-03-01T12:00:00.0000000Z provenance=Synthetic hash=A5875A1F529483C92876C3ED0CC801B6EE0F452C5C033E3B1BAB7317D18D45DA actor=analyst@workstation action=estimate inputs=case-id:8246AD3D73FF1439A2EDEEE666AC1FC4597B0B3E5A6060668744249C17BFFA1A,synthetic-measurements:2DFB87DA105B331B051C047EB2186C4D37A06F69D6F519F2B6FCD2E7C40A3007 outcome=exit=0 method=Wls status=Converged observable=True J=203.68481285118722 chi2=PASS dof=195 removed=0 measurements=254
OK results/ch26.log: 2 entries, head A5875A1F529483C92876C3ED0CC801B6EE0F452C5C033E3B1BAB7317D18D45DA
[exit 0]
```

The head hash in the `OK` line equals the head in the anchor printed by the second run. That equality is the check
you repeat later.

### What one entry records

The file is `gridsim-audit/1` (field-by-field in technical ch. 10, [File and wire
formats](<../technical/0010 File and wire formats.md>)): UTF-8, one JSON object per line, each line ended by a
single LF. The first line of the log above, exactly as stored:

```
$ head -n 1 results/ch26.log
{"format":"gridsim-audit/1","seq":0,"runUtc":"2026-10-04T11:01:57.2144688Z","stateUtc":"2024-03-01T12:00:00.0000000Z","provenance":"Synthetic","summary":"actor=analyst@workstation action=estimate inputs=case-id:23CF1F80D96A7350F3BB4001DC5422B2B3BC021EA4EFB31264FE199A68D4C271,synthetic-measurements:A59ED1067B27834A405D499E1C4090C10BAD6DF51800370A6923B10F54D6F53F outcome=exit=0 method=Wls status=Converged observable=True J=80.01052231478454 chi2=PASS dof=95 removed=0 measurements=122","prevHash":"GENESIS","hash":"1B78AE0F3653078034D6F6D46E71BDC42CB5B2156DFDD7FAFED0C3B80347E8A4"}
[exit 0]
```

Every line carries exactly these eight fields, in this order. A ninth field, a missing field or a different order is a
verification failure.

| Field | Meaning |
|---|---|
| `format` | Always `gridsim-audit/1`. |
| `seq` | The entry's position in the log, counting from 0. |
| `runUtc` | The wall-clock time the entry was written, UTC, in the round-trip form `2026-10-04T11:01:57.2144688Z`. It is the writer's clock, a fact about the run and not a claim about the grid. |
| `stateUtc` | The grid instant the result describes, in the same form, or `null`. `estimate` sets it to the measurement set's `takenUtc` when the run completes (the `2024-03-01T12:00:00` above is the synthetic set's stamp), and `serve` to the newest window it estimated; a run that ends before the estimate (a refusal, a failed load) leaves it `null`, as do `crossval` and `interpose`. The instant is always in the past, because the estimators refuse data inside the 169-hour horizon. |
| `provenance` | The trust class of what the run produced, one of the provenance tags (technical ch. 8, [Provenance and invariants](<../technical/0008 Provenance and invariants.md>)). `estimate` writes `DerivedFromRecorded` for a measurement file and `Synthetic` under `--synth`; `serve` writes `DerivedFromRecorded`; `crossval` writes `Synthetic`; `interpose` writes `Recorded`. |
| `summary` | One line of plain text, `actor=<who> action=<verb> inputs=<name>:<sha256>,... outcome=<text>`. Explained below. |
| `prevHash` | The `hash` of the previous entry, or the literal `GENESIS` for entry 0. |
| `hash` | SHA-256 of this entry, 64 upper-case hexadecimal characters. |

The `summary` has four parts.

- **`actor`** is `user@machine`, taken from the operating-system account and machine name. Any character other than
  letters, digits and `. _ - @ /` is written as `_`, so a Windows account called `A B` records as `A_B`. It is a label
  from the environment, not an authenticated identity.
- **`action`** is the verb: `estimate`, `crossval`, `interpose` or `serve` (`web-realtime` for the web host's resident
  estimator), or `security` for a security-event entry (see [Security events](#security-events)).
- **`inputs`** lists each input by name and the SHA-256 of its bytes. The names are fixed: `case`, `case-id`,
  `measurements`, `synthetic-measurements`, `crosswalk` and `solve-export`; a path is never recorded. `estimate` records
  the case and either the measurement file (`measurements`) or, under `--synth`, the measurement file it generated
  (`synthetic-measurements`).
  A case given as a file is hashed by its bytes (`case`); a case given as a directory is hashed over every file below
  it, independent of listing order (`case`); a bundled case name such as `case14` has no file to hash, so the log
  records `case-id`, the SHA-256 of the name itself. The log never claims a content hash it did not take. `crossval`
  records `case` (the case, or the solve-export you gave it) and, when it solves a named case itself, `solve-export`,
  the export it wrote. `interpose` records `crosswalk`, the hash of the map file, before it loads it. `inputs=-` means
  none were read before the run ended.
- **`outcome`** is always led by `exit=<code>`, the process exit code, so the exit code is part of the hashed record.
  After it comes the verb's own one-line result. For `estimate` that is
  `method=Wls status=Converged observable=True J=80.01052231478454 chi2=PASS dof=95 removed=0 measurements=122`: the
  method, the final status, observability, the objective `J` written so that it round-trips to the exact double, the
  chi-square verdict (`PASS`, `FAIL`, `not-run` when the test did not run, or `no-verdict` for a LAV estimate, then
  followed by `lav_objective=`), the degrees of freedom, the number of
  measurements removed by bad-data handling and the size of the measurement set. When a robust method stops on its
  iteration cap, `irls_cap_reached=true` follows. When `--hti` or `--fdia-screen` was given, their fields
  (`bad_data=hti hti_removed=... fdia=...`) follow that. For `crossval` it is
  `verdict=AGREES|MISMATCH buses=... max_dvm_pu=... max_dva_deg=... tol=...`. For `interpose` it is `stopped
  accepted=... rejected=... decode-errors=... map-errors=... points=... windows-written=... duplicate-names=...
  client-resets=... unsupported-type-ids=...`.

One run writes one entry when it ends, however it ends. Which failures are recorded depends on where they happen:

- A flag the verb refuses inside the verb, a failed load and an unhandled exception are recorded, with the exit code they
  produced.
- An unknown flag, a missing `--audit-log` value, `--audit-log` given twice and `--audit-log` on a verb that does not
  take it are refused before the log is opened, so they write nothing: no entry and no `.lock` file.
- A missing file or folder, or a case that cannot be loaded, records `exit=1` and no `unhandled=` text. Any other
  unhandled exception records `unhandled=<exception type>` and the exit code `-532462766` (0xE0434352, the code the
  .NET runtime exits with on Windows for an unhandled exception), and also writes a crash log.

A failed load of a named case (the case name is still recorded, as `case-id`):

```
$ gridsim estimate nosuchcase.m --synth --audit-log results/ch26-missing.log
error loading case 'nosuchcase.m': MATPOWER case file not found: 'nosuchcase.m' (full path nosuchcase.m).
audit: results\ch26-missing.log entries=1 head=C2C9569D290798AA444762E7C2AA3765C75D035C4BF19D3BB6E3A4CBE834CA0E
[exit 1]
```

```
$ gridsim audit show results/ch26-missing.log
#0 run=2026-10-04T11:52:59.7249314Z state=- provenance=Synthetic hash=F489C44CF01BE428ED3093ECA7B947716D6B2F6C43002000BFE2A3C562941EA9 actor=analyst@workstation action=estimate inputs=case-id:A5A6EB947C96579B810C3D5FC23FBEF8841907F7B7FB15960183E04BFA872822 outcome=exit=1
OK results/ch26-missing.log: 1 entries, head F489C44CF01BE428ED3093ECA7B947716D6B2F6C43002000BFE2A3C562941EA9
[exit 0]
```

An unknown flag is refused before the log opens:

```
$ gridsim estimate case14 --synth --bogusflag --audit-log results/ch26-bogus.log
error: unknown flag '--bogusflag' (misspelled?). Run 'gridsim estimate --help' for the flags estimate takes, or see docs/manual/0019 CLI reference.md.
[exit 2]
```

`interpose` is a listener that can run for days, so it writes a second, earlier entry when it begins listening. That entry
carries the `crosswalk` input and the outcome `started protocol=<name> listen=<host>:<port> monitor=True` (or
`monitor=False`), with no `exit=`. The final entry carries the same input and an outcome that opens with `exit=<code>
stopped` and ends with the counters. Frame contents are never logged. The run below used the loopback client of
[ch. 25](<0025 Interposing live telemetry.md>) and stopped after 12 frames:

```
$ gridsim interpose case14 --protocol iec104 --listen 127.0.0.1:2404 --crosswalk docs/_captures/interpose/iec104-case14.json --monitor --ticks 12 --audit-log results/ch26-interpose.log
interpose IEC 60870-5-104: listening 127.0.0.1:2404, case 'case14', 6 binding(s)
...
stopped: accepted 11, rejected 1, decode-errors 1, map-errors 0, points 10, windows-written 0, duplicate-names 0, client-resets 0, unsupported-type-ids 5x1
audit: results/ch26-interpose.log entries=2 head=1DD22B9CB0793868602D39EE484B6AED8D025E90F182866E4F7CCB3474651DC9
[exit 0]
```

```
$ gridsim audit show results/ch26-interpose.log
#0 run=2026-10-04T11:53:12.6645240Z state=- provenance=Recorded hash=1F5D5E446B6D0C44EFF7A804E0A5BCA5B242697487874D858797EF837AA529DE actor=analyst@workstation action=interpose inputs=crosswalk:8A758EDA1F5FB76D5886D6BBF45819DA7D63BDB0AA078EA41CC8A958F933C6E3 outcome=started protocol=iec104 listen=127.0.0.1:2404 monitor=True
#1 run=2026-10-04T11:53:19.5097560Z state=- provenance=Recorded hash=1DD22B9CB0793868602D39EE484B6AED8D025E90F182866E4F7CCB3474651DC9 actor=analyst@workstation action=interpose inputs=crosswalk:8A758EDA1F5FB76D5886D6BBF45819DA7D63BDB0AA078EA41CC8A958F933C6E3 outcome=exit=0 stopped accepted=11 rejected=1 decode-errors=1 map-errors=0 points=10 windows-written=0 duplicate-names=0 client-resets=0 unsupported-type-ids=5x1
OK results/ch26-interpose.log: 2 entries, head 1DD22B9CB0793868602D39EE484B6AED8D025E90F182866E4F7CCB3474651DC9
[exit 0]
```

Nothing in the summary is encrypted. The `actor` (account and machine name) and an `interpose` listen address are
written in clear; file names and case names are not. The input names are fixed tokens, a path is never recorded and a
bundled case name appears only as the SHA-256 of the name.

### Security events

Besides the run's own entry, an audited run writes one extra entry for each security event, at the moment it is
detected. Its `action` is `security`, it has no inputs, and its outcome is `event=<kind> severity=<severity>
reason=<one line>`. The kinds written today:

| Kind | Written by |
|---|---|
| `HorizonViolation` | `estimate` when its input is refused by the 169-hour guard; `serve` when `--utc-now` is refused, for each window refused as too fresh when it is loaded, for windows the engine refused (one entry naming the count) and when the run is stopped by the guard; the web resident estimator when it is stopped by the guard and for windows the engine refused |
| `InfeasibleState` | `estimate`, each infeasible `serve` window (the `wls` method) and each infeasible web resident-estimator window |
| `SuspectedFdia` | `estimate --fdia-screen` when the screen reads suspected |
| `RestrictionRefused` | `serve` when the output guard refuses an estimate |
| `LiveDataQuarantined` | `interpose`, for each window held as a quarantine capture |
| `AvailabilityDegraded` | the end of a `serve` run, and of the web resident estimator's run, that breached its service-level objective |

`AccessDenied`, `DistrustedSource` and `DependencyAlert` exist as kinds but nothing in the CLI or the apps writes them
yet. A log that does not verify is refused before the run starts (above) and reported on stderr; that is not written
into the same log, because its chain cannot be trusted. When the run ends, the `audit:` line adds
`security-events=<n>` when there were any. A failure to write a security entry prints an error and the run exits 1.

### Forwarding, incidents and the signed anchor

Three optional additions act on the audit log, so each needs `--audit-log` (on the web host, `Audit:LogPath`); given
without it, the CLI exits 2 and the web host refuses to start.

| CLI flag | Web setting | Effect |
|---|---|---|
| `--audit-forward <file>` | `Audit:ForwardPath` | Every entry is also appended, as one JSON line with all its fields and both hashes, to `<file>`: the input for a SIEM collector or a copy on a write-once mount. A failed forward never stops the run or the log; the CLI reports it at the end and exits 1, and the web host writes `GridSim.Web: error: audit delivery: ...` to stderr when the resident run ends. |
| `--incident-log <file>` | `Audit:IncidentLogPath` | Security events are raised as incidents and appended to `<file>` as JSON lines: one for each `SuspectedFdia`, `RestrictionRefused`, `DistrustedSource`, `AvailabilityDegraded` or `DependencyAlert` event, and one when a burst crosses its threshold (by default 20 `HorizonViolation`, 3 `SuspectedFdia`, 1 `AuditChainBroken`). Each incident carries the hash of the audit entry that evidences it; incidents are not written into the audit log itself. The end of a CLI run prints `incidents: <n> raised -> <file>`. |
| `--audit-sign-key <private.pem>` | `Audit:AnchorSigningKeyPath` | An ECDSA P-256 private key in PEM form: the anchor is signed after every entry, into `<log>.anchor.sig`. |
| `--audit-verify-key <public.pem>` | `Audit:AnchorVerificationKeyPath` | The matching public key: before the run starts, the anchor's signature must exist and verify, or the run exits 1 (the web host does not start). A log and anchor rewritten together without the private key are refused here. |

`gridsim audit keygen <private.pem> <public.pem>` makes a key pair (it never overwrites an existing file),
`gridsim audit sign <log> --key <private.pem>` verifies the log against its anchor file and then signs that anchor
(the offline step of the strong form below; a log that does not verify is not signed, exit 3), and
`gridsim audit verify <log> --pubkey <public.pem>` checks the signature offline (see [Verifying a log](#verifying-a-log)).

Be clear about what the signature protects. A host that holds the private key (`--audit-sign-key`) is protected only
against someone who does not have that key; an account that can read the key can re-sign a rewritten anchor. The
strong form keeps the private key off the host and gives the host only the public key. A host run with only
`--audit-verify-key` rewrites the anchor unsigned at the end of each run, so its next run is refused until that new
anchor has been signed off the host: copy the log and its anchor to the signing station, run
`gridsim audit sign <log> --key <private.pem>` there, and return the `.anchor.sig` file. The signature file is one
line, `gridsim-audit-anchor-sig/1 count=<N> head=<hash> sig=<base64>`, where the signature is ECDSA P-256 with SHA-256
over the exact anchor line, in the fixed 64-byte form.

### How the hash chain works

Each entry's hash covers the entry's own content and the previous entry's hash:

```
hash(n) = SHA256( seq | runUtc | stateUtc or "-" | provenance | summary | prevHash(n) )
prevHash(0) = "GENESIS"
prevHash(n) = hash(n-1)
```

The fields are joined with `|` and the text is encoded as UTF-8. Because `prevHash(n)` is inside `hash(n)`, changing
anything in entry k changes `hash(k)`, which no longer matches the `prevHash` stored in entry k+1, and so on to the
end. Removing, inserting or moving an entry breaks the sequence numbers and the links. To make an edit survive
verification an attacker must recompute the hash of the edited entry and of every entry after it.

That last point is the whole limit of the design: the chain has no secret key. Anyone who can write the file can
recompute it. What stops that is not the chain but the anchor, which holds the entry count and head hash from a moment
when you trusted the log, kept somewhere the attacker cannot write, or signed with a key the attacker does not hold
(see [Forwarding, incidents and the signed anchor](#forwarding-incidents-and-the-signed-anchor)).

### Verifying a log

```bash
gridsim audit verify results/ch26.log
```

`audit verify` recomputes every hash and every link and reads the whole file. On a good log it prints one `OK` line
and exits 0. A log that does not verify prints `TAMPERED <file>: entry <i>: <reason> (<n> entries verified before it)`
to stderr and exits 3. The reason names the first broken entry; nothing after it can be trusted, so verification stops
there. A tampered log, checked with the existing capture, is shown here next to a good one:

```
$ gridsim audit verify results/audit-docs.log
OK results/audit-docs.log: 1 entries, head 6451AC6679837D9B2BF5BBB635A309D83107CC719F1B654A13E9CCB7E1BF7EC7
[exit 0]
```

```
$ gridsim audit verify results/audit-docs-tampered.log
TAMPERED results/audit-docs-tampered.log: entry 0: the stored hash does not match the hash recomputed from the entry's fields: this entry's content was edited (0 entries verified before it)
[exit 3]
```

The second file is the first with one word changed in the summary: `status=Converged` became `status=Diverged`. The
hash on the line was left as it was, so it no longer matches the content.

Run with no file, `audit verify` prints its usage line and exits 2:

```
$ gridsim audit verify
usage: gridsim audit verify|show <file> [--anchor-count N --anchor-head HEX] [--pubkey <public.pem> [--anchor <file>] [--sig <file>]]
       gridsim audit sign <file> --key <private.pem> [--anchor <file>] [--sig <file>]
       gridsim audit keygen <private.pem> <public.pem>
[exit 2]
```

With `--pubkey`, `audit verify` first checks the detached signature over the anchor file (by default `<log>.anchor`
and `<log>.anchor.sig`; `--anchor` and `--sig` name others), prints `signature OK: ...`, and then verifies the log
against that anchor. A missing anchor file, a missing signature or one that does not verify prints `TAMPERED` and exits
3. `--pubkey` does not combine with `--anchor-count` / `--anchor-head` (exit 2).

`audit show` runs the same verification and, only when the whole log verifies, prints the entries before the `OK`
line. A log that does not verify prints no entries, not even the clean ones before the break:

```
$ gridsim audit show results/audit-docs-tampered.log
TAMPERED results/audit-docs-tampered.log: entry 0: the stored hash does not match the hash recomputed from the entry's fields: this entry's content was edited (0 entries verified before it)
[exit 3]
```

The exit codes of both verbs are the same.

| Exit | Meaning |
|---|---|
| 0 | The log verified (and matched the anchor, when one was given, and its signature, with `--pubkey`). |
| 1 | The file is missing or unreadable, or the `--pubkey` file is missing or not a P-256 public key in PEM form. A deleted log is an error, never an empty log. |
| 2 | Usage error: no file, a verb other than `verify`, `show` or `keygen`, only one of the two anchor flags, a bad `--anchor-count` (negative or not a number), or `--pubkey` with the anchor flags. |
| 3 | The log did not verify: `TAMPERED`, with the first broken entry and why, or (with `--pubkey`) a missing anchor, a missing signature or a bad one. |

An empty file (zero bytes) is a valid empty log. The flags are in [ch. 19, audit](<0019 CLI reference.md#audit>).

### What verification detects

Each case below was made by damaging `results/ch26.log` (the two-entry log above) and running `audit verify` on the
damaged copy. Messages are pasted from the captures.

**An edited entry.** One character changed in entry 1 (`measurements=254` became `measurements=253`):

```
$ gridsim audit verify results/ch26-edit.log
TAMPERED results/ch26-edit.log: entry 1: the stored hash does not match the hash recomputed from the entry's fields: this entry's content was edited (1 entries verified before it)
[exit 3]
```

**A deleted entry.** Entry 0 removed, so what is now line 1 still says `seq` 1:

```
$ gridsim audit verify results/ch26-delete.log
TAMPERED results/ch26-delete.log: entry 0: the entry's sequence number is 1 but it is at position 0: an entry was deleted, inserted or moved here (0 entries verified before it)
[exit 3]
```

**A reordered log.** The two lines swapped:

```
$ gridsim audit verify results/ch26-reorder.log
TAMPERED results/ch26-reorder.log: entry 0: the entry's sequence number is 1 but it is at position 0: an entry was deleted, inserted or moved here (0 entries verified before it)
[exit 3]
```

**An inserted entry.** A copy of entry 0 placed between the two:

```
$ gridsim audit verify results/ch26-insert.log
TAMPERED results/ch26-insert.log: entry 1: the entry's sequence number is 0 but it is at position 1: an entry was deleted, inserted or moved here (1 entries verified before it)
[exit 3]
```

**A hand edit that leaves the hash alone.** Verification also requires every line to be exactly the bytes the writer
would produce. One extra space after a comma, a re-ordered field, a Windows line ending (an editor re-saving the file
as CRLF) or a byte-order mark all fail, even though the hash does not cover them:

```
$ gridsim audit verify results/ch26-space.log
TAMPERED results/ch26-space.log: entry 1: the line is not in the writer's canonical form (fields reordered, spacing changed or a value re-written in a different notation): it was edited by hand (1 entries verified before it)
[exit 3]
```

**A cut-off line.** The file ends part-way through an entry (a crash during a write, or a truncated copy):

```
$ gridsim audit verify results/ch26-torn.log
TAMPERED results/ch26-torn.log: entry 1: the last line has no terminating newline: the file was cut off part-way through this entry, or a write was interrupted (1 entries verified before it)
[exit 3]
```

A blank line, a duplicated field, an unknown field and a timestamp that is not UTC are also refused, each with its own
message.

**Tail truncation.** Whole entries removed from the end leave a shorter log that is still internally consistent, so
the file alone cannot show it. Without an anchor the truncated copy verifies:

```
$ gridsim audit verify results/ch26-trunc.log
OK results/ch26-trunc.log: 1 entries, head 1B78AE0F3653078034D6F6D46E71BDC42CB5B2156DFDD7FAFED0C3B80347E8A4
[exit 0]
```

Given the anchor taken when the log held two entries (`--anchor-count 2` and the head hash the second run printed), the
same file is caught:

```
$ gridsim audit verify results/ch26-trunc.log --anchor-count 2 --anchor-head A5875A1F529483C92876C3ED0CC801B6EE0F452C5C033E3B1BAB7317D18D45DA
TAMPERED results/ch26-trunc.log: entry 1: the file holds 1 entries but the anchor records 2: the entries from index 1 onward were removed (tail truncation) (1 entries verified before it)
[exit 3]
```

**A whole-log rewrite.** An attacker who edits entry 0 and then recomputes every hash and link produces a log that is
fully self-consistent. The copy below was made that way, changing `status=Converged` to `status=Diverged` and
recomputing the chain with the formula above. Without an anchor it verifies, with a different head hash:

```
$ gridsim audit verify results/ch26-rewrite.log
OK results/ch26-rewrite.log: 2 entries, head 627216100033C2216BE7253C90F0CE95298F3E034112FC5F3253BF2916DE25BC
[exit 0]
```

With the anchor, it is caught, because the head hash of the first two entries is not the one you wrote down:

```
$ gridsim audit verify results/ch26-rewrite.log --anchor-count 2 --anchor-head A5875A1F529483C92876C3ED0CC801B6EE0F452C5C033E3B1BAB7317D18D45DA
TAMPERED results/ch26-rewrite.log: entry 1: the hash of entry 1 does not match the externally anchored head hash: the log was rewritten with recomputed hashes (or the anchor belongs to a different log) (1 entries verified before it)
[exit 3]
```

The summary of what verification can prove:

| Damage | Detected without an anchor | Detected with an anchor |
|---|---|---|
| An entry edited | Yes | Yes |
| An entry deleted, inserted or moved | Yes | Yes |
| A hand edit or re-save that changes bytes the hash does not cover | Yes | Yes |
| A cut-off last line | Yes | Yes |
| Whole entries removed from the end | No | Yes |
| The whole log rewritten with recomputed hashes | No | Yes, for every entry the anchor covers |

An anchor covers the entries that existed when it was taken. Entries appended later are not covered until you take a
newer anchor; the file may hold more entries than the anchor records. Passing only one of `--anchor-count` and
`--anchor-head` exits 2:

```
$ gridsim audit verify results/ch26.log --anchor-count 2
error: --anchor-count and --anchor-head go together (the anchor is the pair).
[exit 2]
```

### Where to keep the log

- **The log file** goes on local storage, in a directory that exists and that the account running the verb can write.
  The directory is never created for you, so a mistyped path is an error and not a log in the wrong place.
- **One writer at a time.** A run takes an exclusive lock on a companion file, `<log>.lock`, for as long as it runs. A
  second run against the same log, from any process, exits 1 and says another writer holds it. The `.lock` file stays
  on disk between runs and holds no data; do not delete it while a run is active. Readers (`audit verify`,
  `audit show`) can read the log while a writer has it open, but a check taken in the middle of a write may report
  the half-written last line as cut off; verify a closed log for a final answer.
- **One log per purpose.** Appending is cheap and every verify reads the whole file, so keep one log per project or
  per month and start a new file rather than letting one grow without bound.
- **The anchor** goes somewhere the account that writes the log cannot change: point `--audit-anchor` at another
  account's share or a write-once store, and copy the `audit:` line from stderr (`entries=N head=H`) to a ticket or
  another machine. To check later, pass those two values to `audit verify`. The default anchor file beside the log,
  writable by the same account, catches an accident, not an attacker who can edit both. A signed anchor
  (`--audit-sign-key`) and a forwarded copy (`--audit-forward` to a write-once mount or a collector) narrow that gap.
- **The signing key.** Keep the private key off the host where you can, and give the host only the public key
  (`--audit-verify-key`); a private key on the host protects only against someone who cannot read it.
- **Copies.** Take a copy of a log as soon as you find it does not verify, and investigate the copy. The original is
  evidence.
- **Editing.** Never open a log in an editor. Even a save with no change can add a byte-order mark or CRLF line
  endings, and either fails verification.

The log is also readable without the command line. The desktop app's AUDIT LOG... button and the web console's AUDIT
LOG tab verify a log, with an optional anchor, and list its entries in the same form as `audit show`; a log that does
not verify shows only the verdict. The web tab belongs to a live session and is not on the raw replay (see
[Sessions](#sessions)). See
[ch. 8, the desktop app](<0008 The desktop app.md>).

## Running GridSim as a service

### Do not confuse `serve` with the web host

`gridsim serve` is not a web server and binds no port. It is the real-time estimator run over a directory of
measurement windows: it reads the `gridsim-measurements/1` files in file-name order, checks each window's timestamp
against the 169-hour horizon, estimates the ones old enough, prints one line per window and a final
processed / converged / rejected-fresh / failed summary, and exits. The clock the horizon is judged against is the real
clock; `--utc-now <iso>` may only move it back (a stricter, reproducible run), and a value later than the real clock is
refused with `REJECTED (forward inference)` and exit 2. It is the consumer of what `interpose` writes
([ch. 25](<0025 Interposing live telemetry.md>)) and runs the estimators described in
[ch. 21](<0021 State estimation.md>). Its flags and exit codes are in [ch. 19, serve](<0019 CLI reference.md#serve>).
It accepts `--audit-log`.

The program that serves pages and a WebSocket is the separate web host, `GridSim.Web`, described next.

### The web host

`GridSim.Web` is a Blazor Server application with an optional REST and WebSocket API. Production runs it in one of two
ways, both on `127.0.0.1:5540` behind a reverse proxy, and both configured only through environment variables
(names, never values, are shown below; see [ch. 3, Configuration](<0003 Configuration.md>) for the settings and
their defaults):

- the container image, built by the `Dockerfile` at the repository root and run by `compose.yml`;
- a systemd service on a host without Docker, from `docs/developer/deploy/gridsim-web.example.service`, with the same address,
  so one nginx file serves both.

`docs/developer/deploy/README.md` is the deployment guide; `docs/developer/deploy/compose.override.example.yml` shows the shape of a local
override.

The commands below are shown without captured output; see Limits.

```bash
docker compose up -d --build                                          # then open http://127.0.0.1:5540/
GRIDSIM_SOURCE_REVISION=$(git rev-parse HEAD) docker compose build    # stamp the commit into the build
docker build -t gridsim-web --build-arg SOURCE_REVISION=$(git rev-parse HEAD) .   # without compose
```

**The image.** The `Dockerfile` has two stages. The .NET 10 SDK image restores packages from the project files alone
(so the download layer is cached until a project file changes), then publishes `GridSim.Web` with the same `portable`
publish profile the release uses. The ASP.NET Core 10 runtime image runs the result as the unprivileged `app` user,
listens on 8080 inside the container, creates `/keys` and `/app/App_Data/keys` owned by that user, and installs `curl`
for the health check. The build context leaves out `.git`, so the commit is passed in as the build argument
`SOURCE_REVISION` and stamped into the assemblies; without it the version shows no commit. Nothing machine-specific is
baked into the image.

**The compose service.** `compose.yml` defines one service, `web`:

- The image is `gridsim-web:latest`, the container `gridsim-web`, and the restart policy `unless-stopped`.
- The port mapping is `127.0.0.1:${GRIDSIM_PORT:-5540}:8080`. The host side is bound to loopback, so the app is
  reachable only from the machine itself, which is where nginx runs. The app believes the `X-Forwarded-For` and
  `X-Forwarded-Proto` headers only from the proxies `GridSim__TrustedProxies` names: `loopback` by default, and
  `loopback,private` in `compose.yml` (`GRIDSIM_TRUSTED_PROXIES`), because the host's proxy reaches the container from
  the bridge gateway, a private address. The value is a comma list of `loopback`, `private`, addresses and CIDR
  networks, or `any` alone (every caller); a malformed value stops start-up naming the setting, and the start-up log
  names the trusted networks. Keep the loopback binding as well.
- The container runs as `${GRIDSIM_UID:-1654}:${GRIDSIM_GID:-1654}`, the .NET image's `app` user. When the key ring is
  a host folder, set both to that folder's owner.
- One volume holds the data-protection key ring, mounted at `/keys`: `${GRIDSIM_KEYS_DIR:-gridsim-keys}`, which is a
  host folder or, by default, the named volume `gridsim-keys`. `GRIDSIM_KEYS_MODE` is `rw` by default and `ro` for a
  shared ring that another application owns.
- The health check runs `curl -fsS http://localhost:8080/` every 30 seconds with a 5-second timeout, allows 3 retries
  and waits 20 seconds after start before counting failures. The image carries the same check.

The environment the service sets, by name, each read from a `GRIDSIM_*` variable in the shell or in a `.env` file
beside `compose.yml` (never committed):

| Setting in the container | Compose variable | Effect |
|---|---|---|
| `GridSim__TrustedProxies` | `GRIDSIM_TRUSTED_PROXIES` (default here `loopback,private`) | The proxies whose forwarded headers are believed (above). |
| `GridSim__ReplayOnly` | `GRIDSIM_REPLAY_ONLY` (default `true`) | `true` offers only the public-data replay; `/api/*`, `/ws/sim` and the resident estimator are not mapped. `false` exposes the solve API, which has no authorisation yet: use it only on a trusted network. |
| `GridSim__BridgeEndpoint` | `GRIDSIM_BRIDGE_ENDPOINT` | `wss://` (or, with the next setting, `ws://`) address of a GDA bridge ([ch. 24](<0024 The GDA bridge.md>)). Empty: bridge sessions are refused with a message. Not an absolute `ws://` / `wss://` URI: start-up stops naming the setting. |
| `GridSim__BridgeAllowUnencrypted` | `GRIDSIM_BRIDGE_ALLOW_UNENCRYPTED` | `true` allows a `ws://` bridge address (private network or loopback only). Default `false`: a `ws://` address stops start-up. Anything but `true` / `false` stops start-up. |
| `Sso__Enabled`, `Sso__Authority` | `GRIDSIM_SSO_ENABLED`, `GRIDSIM_SSO_AUTHORITY` | Join a single sign-on estate. Off by default; every visitor is anonymous. |
| `Auth__ApplicationName`, `Auth__CookieDomain` | `GRIDSIM_AUTH_APP_NAME`, `GRIDSIM_COOKIE_DOMAIN` | The estate's shared data-protection name and cookie domain. Empty: the name `GridSim` and a host-only cookie. |
| `Auth__KeysDirectory` | (fixed) | `/keys`, the mount above. |
| `Site__GoogleTagId`, `Site__AnalyticsScriptUrl`, `Site__AnalyticsCollectorUrl`, `Site__ShareImageUrl` | `GRIDSIM_GOOGLE_TAG_ID`, `GRIDSIM_ANALYTICS_SCRIPT_URL`, `GRIDSIM_ANALYTICS_COLLECTOR`, `GRIDSIM_SHARE_IMAGE_URL` | Analytics and the share card. All empty: no third-party script and no image tags. |
| `Site__SecurityTxt__Contact`, `Site__SecurityTxt__Expires` | `GRIDSIM_SECURITY_CONTACT`, `GRIDSIM_SECURITY_EXPIRES` | Serve `/.well-known/security.txt`. Without a contact it answers 404; a contact needs a future expiry. |
| `ASPNETCORE_ENVIRONMENT`, `ASPNETCORE_URLS` | (fixed) | `Production` and `http://+:8080`. |

A malformed `Site__*` value stops the app at start-up with a message that names the setting. A licence key, if the
instance needs one, is supplied the same way as for the CLI, through `Licence__Key` or `GRIDSIM_LICENCE`; see
[ch. 4, Licensing](<0004 Licensing and activation.md>). Keep keys in the environment or an `EnvironmentFile`
readable only by the service account, never in the repository. `Secrets__Directory` names a folder of
one-file-per-secret (Docker or Compose secrets at `/run/secrets`, a Kubernetes secret volume, systemd
`$CREDENTIALS_DIRECTORY`); the key-ring certificate password is looked up there first and in the environment second,
so it need not be in either the configuration or the environment. A folder that does not exist stops start-up. No
OS keychain or vault backend ships. `compose.yml` sets neither this nor the `Audit__*` settings of the
[audit log](#forwarding-incidents-and-the-signed-anchor); add them in `compose.override.yml`.

**Checking a deployment.** Three surfaces print the version, each in its own form. `gridsim --version` prints
`gridsim <release>+<40-character commit>`, the footer of every page prints `v<release> (<8-character commit>)`, and
`GET /api/openapi.json` carries the release number alone in `info.version` when the API is enabled:

```
$ gridsim --version
gridsim 1.95.467+203c6b9cc0edff55f2bd73642c94dc0963a82b92
[exit 0]
```

```
$ cd src/GridSim.Web/bin/Release/net10.0 && ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://127.0.0.1:5540 GridSim__ReplayOnly=false dotnet GridSim.Web.dll
$ curl -s "http://127.0.0.1:5540/api/openapi.json" | python -c "import json,sys; print(json.load(sys.stdin)[\"info\"][\"version\"])"
1.95.149
$ curl -s "http://127.0.0.1:5540/dashboard/case9" | grep -o "v1\.95\.[^<]*"
v1.95.149 (5e6796e4)
[exit 0]
```

### Behind nginx

`docs/developer/deploy/nginx-gridsim.example.conf` is one `server` block. Replace `gridsim.example.com` with your host name. It
sends everything to `http://127.0.0.1:5540` and does five jobs.

- **WebSocket upgrade.** Blazor Server keeps one SignalR WebSocket open for each browser tab, at `/_blazor`. The
  `location /` block, which carries every request that is not a static asset, sets `Upgrade $http_upgrade` and
  `Connection "upgrade"` and uses HTTP/1.1 upstream. Without those headers the page loads and then never becomes
  interactive.
- **A long read timeout.** `proxy_read_timeout 3600s` in the same block keeps an idle circuit open for an hour.
- **Forwarding headers.** `Host`, `X-Real-IP`, `X-Forwarded-For` and `X-Forwarded-Proto` reach the app, which uses
  `X-Forwarded-Proto` to see the request as HTTPS. That is what makes the sign-on cookie's `Secure` policy
  (`SameAsRequest`) correct when nginx terminates TLS.
- **Caching.** `/_framework/` assets are content-fingerprinted, so they get `expires 365d`. Other static files (css,
  js, images, fonts, wasm, json and geojson) are not fingerprinted and get `expires 1d`, which keeps repeat visits cheap
  without trapping an update for long. Everything else carries no cache header.
- **Compression.** `gzip` is on for text, json, svg, wasm, manifest and geojson responses of 256 bytes or more.

**TLS.** The example listens on port 80 only. Obtain a certificate for your host, for example
`certbot --nginx -d gridsim.example.com`, which adds the `listen 443` and `ssl_certificate` lines and the redirect from
port 80 to this file. TLS ends at nginx; the hop to the app is plain HTTP on loopback.

### Single sign-on across an estate

GridSim.Web can recognise a visitor who is signed in elsewhere in an estate of applications, and can receive a user
from the estate's login site. It has no user store, no login form and no password of its own, and no page requires a
signed-in user: with sign-on off (the default) or no cookie, every page works anonymously. A recognised visitor sees
`signed in as <name>` in the footer.

Two mechanisms exist, and both depend on the same thing: every application in the estate mounts the same
data-protection key ring and uses the same `Auth__ApplicationName`.

1. **Shared cookie.** Applications under one parent domain read the same encrypted sign-in cookie. Set
   `Auth__CookieDomain` to the parent domain, point `Auth__KeysDirectory` at the shared ring (mount it with `GRIDSIM_KEYS_MODE=ro` when another
   application owns it) and use the estate's `Auth__ApplicationName`. If the ring or the name differs, the cookie cannot be decrypted and the
   visitor is anonymous; the app still runs.
2. **Ticket hand-off, for applications on other domains.** A browser never sends one domain's cookie to another, so the
   login site (the authority, `Sso__IsAuthority=true`, on `GET /sso/bridge`) mints a ticket, and the application
   (the client, `GET /sso/land`) turns it into its own cookie on its own domain:
   1. A client that has protected pages redirects an unauthenticated visitor to `<Sso__Authority>/sso/bridge`, telling it where to return.
   2. The authority signs the visitor in if needed, then issues a ticket for that one client origin. The client origin
      must be in the authority's `Sso__AllowedClients` list.
   3. The browser lands on the client's `/sso/land` with the ticket. The client checks that it is genuine, unexpired,
      bound to its own origin and not used before, and signs the visitor into a host-only cookie. A refused ticket sends
      the visitor home, anonymous, and the reason goes to the log.

   GridSim.Web has no protected page, so it never sends a visitor to the authority; it receives visitors at `/sso/land`
   when another estate application sends them. It cannot be the authority itself: `Sso__IsAuthority=true` stops it at
   start-up, because it has no login page. Behind a reverse proxy that does not pass on `X-Forwarded-Proto`, set
   `Sso__PublicOrigin` to the address visitors use (for example `https://gridsim.example.com`); otherwise the app sees
   `http`, the ticket says `https`, and the log says so on every refused sign-in.

   A ticket is a data-protection payload protected with the shared ring, bound to one audience, carrying the user's
   claims, and valid for `Sso__TicketSeconds` (120 seconds by default). There is no OIDC server, no client secret and no
   second user database. `/sso/logout` clears only that application's own cookie.

The shipped settings turn sign-on off. Turning it on for a client is `Sso__Enabled=true`, `Sso__Authority=<login origin>`
and the three `Auth__*` settings above. `compose.yml` has no variable for `Sso__IsAuthority`, `Sso__AllowedClients` or
`Sso__TicketSeconds`, so a compose deployment is a client unless `compose.override.yml` sets them; a list setting in an
environment variable uses the indexed form, `Sso__AllowedClients__0`. Each ticket signs in once: a second use within its
lifetime is refused, with nothing to configure. Several instances behind one name each keep their own record, so such a
deployment also needs a shared distributed cache for a ticket spent on one instance to be refused on the others.

**Key ring encryption.** New data-protection keys are encrypted at rest when the platform allows. On Windows the default
(`Auth__KeyProtection=auto`) uses DPAPI for the account the app runs as. On Linux, including the container, set
`Auth__KeyCertificatePath` to a `.pfx` certificate with its private key (and `Auth__KeyCertificatePassword` from a
secret, if it has one); without it the keys stay plain and the app says so at start-up. `Auth__KeyProtection=none`
records that plain keys protected by file permissions are intended. Every application sharing an estate ring needs the
same certificate. Keys already in the folder are not rewritten. To rotate the certificate, put the new one in
`Auth__KeyCertificatePath`, the old one in `Auth__KeyPreviousCertificatePath` (with `Auth__KeyPreviousCertificatePassword`
if it has one), and restart every application sharing the ring: keys encrypted to the old certificate are still read,
and nothing new is encrypted to it. The two settings naming the same certificate stop start-up.

### Sessions

Each browser tab (one Blazor circuit) and each raw WebSocket connection to `/ws/sim` owns one simulation session. A
session is private to its connection, steps its own model under its own lock, and is disposed when the connection ends;
nothing is shared between visitors and nothing survives a disconnect. A session is one of three kinds:

- **Live**: the case is solved in-process, either held steady (the IEEE and MATPOWER benchmarks) or driven along the GB
  day curve (the JSON GB cases). The solver engine can be chosen with `?solver=auto|dense|sparse|sweep` on the
  dashboard URL.
- **Replay**: a pre-solved run directory played back frame by frame, 30 minutes of grid time per frame.
- **Bridge**: a stream from a GDA bridge server, started by a case id such as `bridge:gb-full@raw`. It needs
  `GridSim__BridgeEndpoint`.

On a replay-only instance `/` redirects to the raw public-data replay of the first case in `GridSim__ReplayCases` (a
comma list of remote case names; default `gb-full`, so `bridge:gb-full@raw`), and the raw replay shows only the raw
tabs. Every other dashboard address, such as `/dashboard/case9`, `/dashboard/live-bridge` or the raw replay of a case
the setting does not list, answers 404, and no live session can be opened by any route. A malformed or repeated name in
the setting stops start-up. Replay-only also unmaps `/api/*` and `/ws/sim`:

```
$ cd src/GridSim.Web/bin/Release/net10.0 && ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://127.0.0.1:5540 dotnet GridSim.Web.dll
$ curl -s -o /dev/null -w "%{http_code}\n" "http://127.0.0.1:5540/dashboard/case9"
404
$ curl -s "http://127.0.0.1:5540/dashboard/bridge:gb-full@raw" | grep -o "AUDIT LOG" | wc -l
0
$ curl -s "http://127.0.0.1:5540/dashboard/bridge:gb-full@raw" | grep -o "v1\.95\.[^<]*"
v1.95.186 (e848f826)
[exit 0]
```

At most `GridSim__MaxSessions` sessions (64 by default) run at once; one more shows a message saying the server is at
its limit until another tab or connection closes.

 A full instance (`GridSim__ReplayOnly=false`) can also run the
resident real-time estimator, which is off unless `Realtime__Enabled=true` and is fed from the windows directory named by
`Realtime__WindowsDir`; its latest output is at `GET /api/realtime`. The licence tier shows in the footer.

### What CI runs

The workflows are in `.github/workflows/`.

- **CI** (`ci.yml`) builds the solution and runs the full test suite on Ubuntu and Windows, each in five CPU modes:
  the machine's normal vector tier, AVX2 off, hardware intrinsics off, AVX-512 forced (`simdcheck --expect Avx512`
  fails the leg on a runner without AVX-512) and FMA off; and on macOS (Apple silicon) in the normal and
  hardware-intrinsics-off modes. Each reduced leg checks that its switch took effect (`simdcheck` must report
  `Avx2=False`, the `Scalar` tier or `Fma=False`), so a runtime that ignored the variable fails the leg instead of
  re-testing the normal tier. The tests pin exact values, so a kernel that gives a different answer on one tier or
  operating system fails the run, and no leg is advisory. It also fails on any NuGet package with a published
  vulnerability, and builds the Docker image, starts it and checks that the home page renders the version footer. It
  runs when the release workflow calls it and when started by hand; it does not run on every push.
- **Release** (`release.yml`) runs on a `v*` tag. It requires the tag to equal the version in `Directory.Build.props`
  and a matching `CHANGELOG.md` heading, runs the test suite and the vulnerability gate, and calls the whole CI matrix
  above; every publishing job waits for it, so nothing is built for shipping unless every leg passed. It builds the two
  CycloneDX software bills of materials (the command-line tool and the web app) first, then publishes the
  command-line binaries for each platform with the bill of materials' SHA-256 built in and runs each on its own
  platform (`--version` must answer, `case9` must solve with exit 0, and `gridsim compliance verify` must report
  `release build  yes` with exactly that SHA-256). It signs the Windows binary (Authenticode) when the signing
  certificate is configured, trusting for the check only the committed self-signed certificate by its thumbprint;
  without the certificate secret the binary is released unsigned with a warning, or, when the repository variable
  `GRIDSIM_ALLOW_UNSIGNED_RELEASE` is `false`, the release fails in its first job. It builds and probes the
  replay-only web container image (with the web bill of materials' SHA-256 built in), attaches the binaries, their
  SHA-256 checksums, the bills of materials and the changelog notes to the GitHub Release, and gives every one of
  those files a signed build-provenance attestation (check one with `gh attestation verify <file> -R <owner>/GridSim`).
  The container image, which is neither signed nor attested, is pushed to the GitHub Container Registry once the
  Release exists. The desktop app is not a release artefact.
- **Publish packages** (`publish.yml`) packs the core library (`GridSim.Core`, the only package that ships) to the
  repository's GitHub Packages feed, built with the command-line tool's bill-of-materials SHA-256 when the release
  calls it, signs the package when the signing certificate is configured (verifying it against the same pinned
  certificate), and refuses any version that differs from the source version. Started by hand or by a published
  GitHub Release, it first runs the whole CI workflow on the commit and packs nothing unless that passes; called by
  the release, it relies on the release's own gates, which have already passed.
- **CodeQL** (`codeql.yml`) scans the C# code and runs only when started by hand. **Dependabot auto-merge**
  (`dependabot-auto-merge.yml`) is disabled as an automatic trigger and, as written, does nothing when started by hand:
  its job runs only for the Dependabot actor and merges the pull request that started it, and a hand-started run has
  neither.

## The assurance documents

The documents in `docs/assurance/` answer the question "what can GridSim evidence about its own security, and what
can it not?" They are written for a reviewer from a regulated operator or a security function, not for day-to-day use.
There are eleven. The three below are the core assessments, and this section says what each is for; the others are
`security-governance.md` (ownership, review points and the recorded deviations), `risk-register.md`,
`incident-response.md` (the runbook), `ssdf-mapping.md` and `slsa-provenance.md` (secure development and build
provenance), `nasa-std-7009-credibility.md` (model credibility), `cyber-essentials-note.md` and `tier3-roadmap.md`. GridSim is not an operator of essential services
and claims no certification. The framing throughout is that GridSim is a historical state estimator, a filter over data
at least 169 hours old or over replay, and that the structural refusal of newer data is itself the most important
security control.

- **[`caf-mapping.md`](../assurance/caf-mapping.md)** maps GridSim to the four objectives of the NCSC Cyber Assessment
  Framework (managing risk, protecting against attack, detecting events, minimising impact), principle by principle.
  Every row has one of four statuses: Achieved, Partially achieved, Not achieved, or out of scope because it belongs
  to the deploying party; every Achieved row names a type and a test. It holds the control-to-type traceability matrix
  and the ordered gap register, and section 6 is the long account of this chapter's audit log, including its limits.
  Where GridSim stands: the data boundary (the 169-hour guard), the restriction review gate, the physics audit,
  provenance labelling, the persistent and anchored audit log with per-event security entries, forwarding and the
  optional signed anchor, incident raising, the secrets store, deterministic estimates and replay, and the
  false-data-injection screen are Achieved; web sign-in, single sign-on and the replay-only gate are in place as
  compensating access controls; role-based access control, the distrusted-source control and service resilience are
  Partially achieved, because each is built in the library but nothing in the CLI or the apps applies it (no endpoint
  enforces the role gate, nothing marks a source distrusted, and no host drives the reconnect schedule).
- **[`nis-oes-gap-assessment.md`](../assurance/nis-oes-gap-assessment.md)** assesses GridSim as a component inside an
  operator of essential services under the NIS Regulations 2018, against the CAF outcomes as Ofgem adapts them. Its
  rows are Met, Partial, Gap or organisational, and each gap was turned into a named seam (incident reporting, access
  control, supply-chain assurance, availability objectives). Its distinctive argument is that the 169-hour horizon
  shrinks the attack surface, because week-old state gives an attacker nothing operational. Where GridSim stands: the
  data boundary, input validation, supply chain, data security, monitoring and incident reporting are Met for the
  component; access control (the role gate is not enforced) and resilience (no supervised reconnect) are Partial, and
  so is the distrusted-source part of incident containment.
- **[`rbac-sbom-secrets-notes.md`](../assurance/rbac-sbom-secrets-notes.md)** records, for access control, the bill
  of materials, secrets and availability, what is in place and what is built but not yet applied: role-based access
  to be enforced in the hosting layer and never in the core library, a bill of materials with a deliberately small
  dependency surface, and no secrets in the core path (feed credentials arrive from the environment or a secrets
  directory at the edges). It ends with what is already in code. Where GridSim stands: sign-in, single sign-on, the
  replay-only gate, the release bills of materials, provenance attestation and signing, the secrets store, the guard,
  the review gate, the FDIA screen and the audit log exist; the role gate exists but no host enforces it.

`gridsim compliance verify` (and the web `/compliance` endpoint) reports the same position from a running binary: the
version, commit, build hash, bill-of-materials hash and the recorded control set. Only a release build can read
`COMPLIANT`; a local build reads `NOT ATTESTED` and the verb exits 1, and while the three controls above are Partially
achieved a release build reads `NOT COMPLIANT` and lists them. The statuses are an inventory kept in the source, not a
measurement, and the build hash is an identity label, not an integrity check: whether a downloaded file is the
released one is checked with the provenance attestation, `SHA256SUMS.txt` and the signatures.

The audit log is the evidence trail all three point to: replay is the way to reproduce a result, and the log is how you
show which inputs and which code path produced the result you are defending. The assessments read the log as a control
for detecting and reconstructing events; the optional anchor signature adds protection against a joint rewrite only
as far as the private key is kept from the attacker.

## Pitfalls

- **`audit verify` says `OK` after someone deleted the last entries.** That is tail truncation, which `audit verify`
  can see only against an anchor. Pass the `audit:` line of a run, or the anchor file's pair, back with
  `--anchor-count` and `--anchor-head`. The audited verbs check the anchor file themselves before they append.
- **A run that wrote nothing to the log.** An unknown flag, a missing `--audit-log` value and `--audit-log` on the wrong
  verb are refused before the log opens, so no entry and no `.lock` file appear.
- **Exit 1 and `already open for writing`.** Another run holds the log (check for a still-running `interpose`). The lock
  is released when that process ends.
- **Exit 1 and `failed verification` on a log you did not touch.** An editor re-saved it (CRLF or a byte-order mark), a
  copy tool cut it, or a run was killed part-way through a write and left a cut-off last line. Keep a copy, find the
  entry the message names, and start a new log.
- **`--audit-log` given to a verb other than `estimate`, `crossval`, `interpose` and `serve`.** The verb exits 2 and
  does not run. So does `--audit-forward`, `--incident-log`, `--audit-sign-key` or `--audit-verify-key` given without
  `--audit-log`.
- **Exit 1 and `anchor signature is for a different count or head hash` on the next run.** The runs have
  `--audit-verify-key` but no `--audit-sign-key`, so the previous run moved the anchor on without signing it (the
  signature still names the older anchor). Sign the new anchor off the host, or also give the host the signing key
  (accepting the weaker protection). With no signature file at all the message is `has no signature file`.
- **Exit 1 at the end of a run that otherwise succeeded.** The forwarding file or the incident file could not be
  written; the message names it. The audit log itself is complete.
- **A run that was killed forcibly or lost power has no entry.** The entry is written when the process ends or fails
  with an exception it can see. A forced kill leaves no record of that run, except that `interpose` has already written
  its `started` entry.
- **The Blazor page loads and does nothing.** nginx is not sending the WebSocket upgrade headers on `/_blazor`.
- **Every visitor is anonymous after enabling sign-on.** The key ring, `Auth__ApplicationName` or cookie domain differs
  from the estate's, so the cookie cannot be decrypted.
- **`/api/...` returns 404 in production.** The instance is replay-only, which is the production default.

## Limits

- The chain uses plain SHA-256 with no key, so a log rewritten end to end with recomputed hashes verifies unless you
  hold an anchor kept out of the writer's reach, or a signed anchor whose private key the rewriter does not have.
- A signing key held on the host protects only against someone who cannot read it, and there is no offline signing
  verb for the stronger off-host form.
- The default anchor file sits beside the log, so it protects against accidents, not against an account that can edit
  both; put it elsewhere with `--audit-anchor`.
- `runUtc` is the writing machine's clock and the `actor` is the operating-system account name; neither is attested.
- Only `estimate`, `crossval`, `interpose` and `serve` write audit entries, and the web host its resident real-time
  estimator run (one run entry plus its security events) when `Audit:LogPath` is set. The dashboards and the API write
  none. No access denial, distrusted source or dependency alert is ever written, because nothing produces them yet.
- Incidents go to a JSON-lines file only; routing them to a SOC, ticketing or notification system is yours.
- A run killed forcibly or cut by power loss leaves no entry.
- `audit verify` reads the whole log into memory, so a log larger than the .NET array limit cannot be verified in one
  pass, and the AUDIT LOG tab refuses an uploaded log larger than 64 MiB.
- `EstimationAuditTrail.Record` writes a stand-alone entry to an in-memory log and nothing calls it; the CLI uses
  `EstimationAuditTrail.Outcome`.
- The solve API and `/ws/sim` have no authorisation or rate limit (only the session cap bounds them), which is why
  production starts replay-only.
- SSO single use is per instance; a deployment with several instances needs a shared distributed cache.
- On Linux without `Auth__KeyCertificatePath`, data-protection keys are still stored unencrypted (a start-up warning
  says so).
- Role-based access control and the distrusted-source control exist in the library but nothing in the CLI or the apps
  applies them, and no host drives the bridge reconnect schedule, so there is no supervised failover; the assurance
  documents list these as Partially achieved. Secrets come from the environment or a secrets directory; no OS
  keychain or vault backend ships.
- `gridsim compliance verify` reads `NOT COMPLIANT` even on a release build while those controls are Partially
  achieved, and `NOT ATTESTED` (exit 1) on any local build.
- The container image, the compose file and the health check are read from the files and are not run here, and the
  `app` user's id 1654 is a compose default, not a value stored in the repository.

## See also

- [ch. 19, audit](<0019 CLI reference.md#audit>), [estimate](<0019 CLI reference.md#estimate>),
  [crossval](<0019 CLI reference.md#crossval>), [interpose](<0019 CLI reference.md#interpose>) and
  [serve](<0019 CLI reference.md#serve>) - the verbs' flags and exit codes.
- [ch. 21, State estimation](<0021 State estimation.md>) - what `estimate` computes and the audit log from the
  estimator's side.
- [ch. 25, Interposing live telemetry](<0025 Interposing live telemetry.md>) - `interpose` and `serve` end to end.
- [ch. 3, Configuration](<0003 Configuration.md>) - the web app's keys and the CLI environment variables.
- [ch. 4, Licensing and activation](<0004 Licensing and activation.md>) - tiers and keys.
- [ch. 8, The desktop app](<0008 The desktop app.md>) - the AUDIT LOG... view.
- Technical ch. 8, [Provenance and invariants](<../technical/0008 Provenance and invariants.md>) - the provenance
  tags and the 169-hour guard.
- Technical ch. 10, [File and wire formats](<../technical/0010 File and wire formats.md>) - `gridsim-audit/1`.
- Technical ch. 31 and technical ch. 35 - the matching technical treatment.
- `docs/developer/deploy/README.md`, `compose.yml`, `Dockerfile` and `docs/assurance/` - the files this chapter describes.
