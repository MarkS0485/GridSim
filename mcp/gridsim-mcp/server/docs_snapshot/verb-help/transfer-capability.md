### transfer-capability

`gridsim transfer-capability <study.json> [--out <result.json>]`

AC-verified directional transfer capability (RC3). Positive transfer raises the declared source units' generation and
lowers the declared sink units' generation, each by the transfer times its normalised weight (weights summed in
declared order); nothing else in the case changes, and the single slack balances the losses. Every trial is built
from the same loaded case and verified exactly as `transmission-security` verifies a state: the intact network and
each listed branch outage are solved with the full AC Newton solver and checked against the absolute voltage band,
branch MVA at both ends (RATE_A intact, the requested post-fault rating after an outage) and each unit's realised P
and Q range. A trial is feasible (every state evaluated and within limits), a violation (every state evaluated, at
least one limit breached), or unknown (a state that islands, does not converge or lacks evidence). Unknown is never
reported as a physical limit. A participant whose schedule would leave its own P range is a violation of that
transfer (`ScheduledGeneratorP`); it is never clipped.

A separate verb rather than a mode of `transmission-security`, so that command's strict schema, flags and exit codes
stay exactly as published; the case identity, strict parsing and result-file rules are the same.

Search: zero transfer is evaluated first. If it already violates a limit the result is `NOT SECURE` /
`NoAdmissibleStart` (no headroom is reported, which is different from a solved zero-margin case); if it lacks evidence,
`INCOMPLETE` / `ReferenceIncomplete`. Otherwise the search walks up at k x `stepMw` (the last point clamped to
`maxMw`) until the first violation, the first unknown, the cap or the trial budget; evaluates up to `probes` further
coarse points above a first violation only to detect a non-monotone feasible region; then bisects between the last
feasible and first violating trial until the bracket is no wider than `toleranceMw`. Changes in solved control state
(a PV-to-PQ switch) or in the first binding limit across the sampled trials, a feasible probe above a violation, an
unknown midpoint and an unfinished bisection each qualify the result. Both bracket endpoints are then re-solved from
the case and must reproduce their evidence bit for bit. The reported capability is the lower feasible MW; the first
observed limit (state, element, criterion, observed value, limit and signed margin) comes from the re-solved upper
endpoint, with any simultaneous violations in stable order (intact first, then outages in study order). Intervals
between trials are not independently proven feasible, and the result is never a global maximum.

The study file is strict JSON, schema version 1: `schemaVersion` (1), `case` (`path` or `builtIn`, plus `sha256`, as
for `transmission-security`), `sources` and `sinks` (each row a zero-based in-service unit `index`, its expected `bus`
ID and a positive `weight`; a unit may appear once), `outages` (as for `transmission-security`), `voltage` (`minPu`,
`maxPu`), `postFaultRating` (`A`, `B` or `C`) and `search` (`maxMw`, `stepMw`, `toleranceMw` no larger than `stepMw`,
`maxTrials` from 2 to 10000 including the zero-transfer trial, optional `probes` 0 to 10, default 1). Optional
`interfaces`: each a `name`, `branches` (`index`, `from`, `to`, `sign` +1 or -1; the interface MW is the sum of sign
times the active power leaving each branch's From terminal) and an optional `limitMw` that caps that signed sum in
every state. Unknown or duplicated fields, non-finite numbers and mismatched identities are refused before any solve.

The result JSON (`schemaVersion`, `study`, `provenance`, `effectiveOptions`, `inputGenerators`, `result`, `comparator`)
is printed to stdout and, with `--out`, written to a new file via a temporary file and a final move; an existing path
is refused. `result` holds `status`, `outcome` (`Limited`, `AtLeastMax`, `UnknownAbove`, `BudgetExhausted`,
`NoAdmissibleStart`, `ReferenceIncomplete` or `Unsupported`), `qualifications`, `lowerMw`, `upperMw`, `firstLimit`,
the full `reference`, `lower` and `upper` states, every trial in execution order (with the realised slack balancing
and each interface's MW and change from zero transfer), the participation shares and exact trial accounting.
`comparator` is the record shape for a paid-tool comparison; GridSim fills only its own side and marks it
`UNVERIFIED`.

Exit 0 `VERIFIED` (a limit bracketed to the tolerance, or the cap reached, with no qualification). Exit 1 for input it
cannot accept (as `transmission-security`). Exit 2 for an invalid invocation. Exit 3 `QUALIFIED`, `INCOMPLETE` or
`NOT SECURE`.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |
