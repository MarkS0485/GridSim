### corrective-sequence

`gridsim corrective-sequence <study.json> [--out <result.json>]`

Ordered corrective N-1-1 study (RC3). Each requested sequence is a first branch outage, corrective generator actions,
then a second branch outage, run as an ordered list of auditable steps: `IntactBase`, `FirstOutage`, `FirstAcSolve`,
`ActionProposal`, `ActionValidation`, `ActionApplication`, `IntermediateAcVerification`, `SecondOutage`,
`FinalAcVerification`. A step runs only when every earlier step completed. Every step of every sequence is in the
result: a failed step keeps its evidence and every later step is reported `Blocked` with the step that stopped it, so a
plausible final solve can never hide a failed or missing intermediate state. Every AC state is solved and checked by
the same absolute verifier as `transmission-security` (bus voltage against the study band, branch MVA at both ends, each
unit's realised P and Q against its own range). It is separate from `n-1-1`, whose screen, ranking and corrective
default are unchanged.

The study file is strict JSON, schema version 1: `schemaVersion` (1), `case` (`path` or `builtIn`, plus `sha256`, with
the same identity rules as `transmission-security`), `voltage` (`minPu`, `maxPu`), `ratings` (`firstStage`,
`intermediate` and `final`, each `A`, `B` or `C`, falling back to A where a branch has none), `policy` and `sequences`.
`policy.eligible` is the permitted action set: one envelope per unit with its model `generator` index and expected
`bus`, `direction` (`Up`, `Down` or `Both`), `minFinalMw`, `maxFinalMw`, `maxDeltaMw`, `rampMwPerMin` and
`windowMinutes`; `policy.availableMinutes` is the time assumed between the two outages, and the optional
`policy.balanceToleranceMw` (default 1e-6) bounds the net MW of a proposal. A unit at the slack bus cannot be eligible:
the slack balances losses in every state. Each sequence has `first` and `second` (zero-based branch `index` with its
expected `from` and `to` bus IDs), `proposal` (`None`, `Prescribed` or `PlannedRedispatch`) and, for `Prescribed` only,
`actions` (`generator`, `bus`, `deltaMw`). Sequences are run in input order, both orders of a pair when both are
listed, each from a fresh copy of the case. Unknown or duplicated fields, non-finite numbers, an invalid outage, a
repeated sequence or an invalid envelope are refused before any solve.

The intact state is checked against RATE_A, the first post-outage state against `ratings.firstStage` (the temporary
emergency rating; loading above `ratings.intermediate` there is listed as temporary emergency loading, not a failure,
and loading above `firstStage` is a prohibited state), the intermediate state (first outage plus actions) against
`ratings.intermediate`, and the final state (both outages, actions retained, no further control) against
`ratings.final`. Actions are simultaneous independent ramps over a common window: each change must have an allowed
sign, a final setpoint within the envelope and the unit's own Pmin and Pmax, and a magnitude within the tighter of
`maxDeltaMw` and `rampMwPerMin` times the smaller of `windowMinutes` and `availableMinutes`; a rejected action reports
its exact MW shortfall. A proposal is applied whole or not at all, and its MW changes must balance. `Prescribed`
replays the given actions; `PlannedRedispatch` takes the changes of the incumbent DC minimum-change corrective
redispatch on the first post-outage network as a candidate, with no authority outside the permitted set. Neither finds
feasible corrective actions. Breaker and switching actions are not supported in this version.

Sequence outcomes: `Verified`, `Violations`, `ActionRejected`, `Incomplete` (an `ISLANDED`, `NO SOLUTION` or
`INCOMPLETE` state) or `Blocked` (the intact base is not secure), with the first failed step and its disposition. The
study `status` is `SECURE` only when the intact base and every sequence are verified; otherwise `NOT SECURE`, with
`outcome` by precedence `Incomplete`, `ActionRejected`, `Violations`. Counts give requested, attempted, completed,
verified and incomplete sequences. The result JSON (`schemaVersion`, `study`, `provenance`, `effectiveOptions`,
`stepOrder`, `inputGenerators`, `result`) is printed to stdout and, with `--out`, written to a new file via a temporary
file and a final move; an existing `--out` path is refused.

Exit 0 `SECURE`. Exit 1 for input it cannot accept (malformed or unknown-field study, case identity mismatch, invalid
outage, sequence or policy, unreadable case, existing output path). Exit 2 for an invalid invocation. Exit 3 `NOT SECURE`.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |
