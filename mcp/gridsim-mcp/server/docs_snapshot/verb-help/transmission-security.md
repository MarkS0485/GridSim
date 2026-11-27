### transmission-security

`gridsim transmission-security <study.json> [--out <result.json>]`

Preventive branch N-1 study with AC verification (RC3). A DC candidate dispatch is formed by `scopf`'s optimiser
(`DcScopf`) against an explicit list of branch outages, then the intact network and each listed outage are solved
independently from that same candidate with the full AC Newton solver and checked against absolute limits: bus voltage
against the study band, branch MVA at both ends against RATE_A intact and the requested post-fault rating after an
outage (B or C, falling back to A where the branch has none), and each unit's realised P and Q against its own range.
The DC optimiser's status and objective are kept separate from the AC verdict. It is a DC-propose, AC-verify study, not
a full AC SCOPF and not a claim of global optimality.

The study file is strict JSON, schema version 1: `schemaVersion` (1), `case` (`path`, relative to the study file, or
`builtIn`, plus `sha256`), `outages` (ordered; each a zero-based branch `index` with its expected `from` and `to` bus
IDs, so parallel circuits stay distinct), `voltage` (`minPu`, `maxPu`) and `postFaultRating` (`A`, `B` or `C`).
Optional: `dc.maxRounds`, and `costs.perMwh` (one linear cost per in-service unit, in model order). Unknown or
duplicated fields, non-finite numbers, inverted limits, a duplicated outage or one whose endpoints do not match are
refused before any solve. `case.sha256` is the SHA-256 of the case file's exact bytes; for a native case directory or
v2 manifest it is the SHA-256 of the UTF-8 manifest lines (path, TAB, lowercase file SHA-256, LF) in consumption order;
for a built-in it binds the build identity and the model. A mismatch is refused and prints the resolved identity.

Controls are declared in the result: taps fixed, reactive limits enforced (a PV-to-PQ switch is a valid solution, not a
failure), and the single slack supplies the AC losses, so a slack unit whose realised P exceeds its range is reported.
Unit P and Q are recovered from converged bus injections and are compared with a declared tolerance of the Newton bus
mismatch times base MVA; margins are reported unrounded. Where several units share a bus their individual realised
outputs cannot be observed and the row is incomplete rather than assumed. An outage that islands the network is left
out of the DC candidate and reported as `ISLANDED`.

Each listed outage has exactly one result row. Row dispositions: `SECURE`, `VIOLATIONxN`, `ISLANDED`, `NO SOLUTION`,
`INCOMPLETE` or `UNEVALUATED`, each with its reason. The study `status` is `SECURE` only when the intact network and
every listed outage are evaluated and meet every limit; otherwise `NOT SECURE`, with `outcome` `Violations`,
`Incomplete` (missing evidence takes precedence over violations) or `CandidateFailure` (no usable DC candidate, with
the optimiser's message).

The result JSON (`schemaVersion`, `provenance` with study and case SHA-256, inputs and engine identity,
`effectiveOptions`, `inputGenerators`, `result`) is printed to stdout and, with `--out`, written to a new file via a
temporary file and a final move. An existing `--out` path is refused, and a failed run writes no result, so an earlier
result can never be mistaken for the current one.

Exit 0 `SECURE`. Exit 1 for input it cannot accept: a malformed or unknown-field study, a case identity mismatch, an
invalid outage, an unreadable case or an existing output path. Exit 2 for an invalid invocation. Exit 3 `NOT SECURE`.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |
