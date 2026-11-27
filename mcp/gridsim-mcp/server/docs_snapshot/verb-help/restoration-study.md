### restoration-study

`gridsim restoration-study <study.json> [--out <result.json>]`

Restoration switching-sequence study (RC3 P8). It searches for the order in which to close a set of candidate branches
so that every intermediate network is admissible and the unserved energy during restoration is least. It is a separate
study from `restore`, which checks one given order and is unchanged.

Every candidate branch starts open, whatever its stored state; every other branch keeps its state in the case. An
admissible sequence closes every candidate once, respects every precedence constraint, and leaves the initial state and
the state after each action admissible: the live (slack-connected) network solves and passes the same absolute AC
verifier as `transmission-security` (every live bus inside the voltage band, branch MVA at both ends within RATE_A,
each unit's realised P and Q within its own range). A bus that is not live carries no load; units at a live bus run at
their stated setpoint and the slack balances. A unit at its reactive limit is a valid solution reported by its margin,
not a failure (unlike `restore`). The objective is the unserved energy in MWh: for each action, the net demand (positive
Pd) of the buses still dead before it, times the action's minutes, divided by 60.

The search is a deterministic finite enumeration: depth-first over the candidates in input order, each switching state
(a set of closed candidates) solved once, every sequence through an inadmissible state pruned, and a branch-and-bound
cut using the exact optimum of the same problem without the AC constraints (a lower bound found from connectivity
alone). Outcomes are distinct: `OPTIMAL` (the enumeration finished and the reported sequence is a proven optimum;
lower and upper bounds equal), `FEASIBLE` (an admissible, fully verified sequence, but optimality is not proven
because the node budget ran out or a state could not be verified and the bound does not exclude it; both bounds are
reported), `INFEASIBLE` (proven: no admissible sequence exists, for example the initial state already fails) and
`UNRESOLVED` (no admissible sequence found and none proven impossible, or the target network, with every candidate
closed, is outside the verifier's envelope: one connected network, one slack, positive RATE_A on every live branch, no
coupled devices, flow controls, boundaries or ZIP demand). A state with no solution or incomplete evidence is never
admissible and is counted as unverified. Of several optimal orders the first in input order is reported.

The study file is strict JSON, schema version 1: `schemaVersion` (1), `case` (`path` or `builtIn`, plus `sha256`, with
the same identity rules as `transmission-security`), `voltage` (`minPu`, `maxPu`) and `actions` (1 to 12, each an `id`,
a `branch` with its zero-based `index` and expected `from` and `to` bus IDs, and `minutes`, the positive time the
action takes). Optional: `precedence` (each `before` and `after`, two action ids) and `search.maxNodes` (default
100000, the most prefix extensions the search may expand). Unknown or duplicated fields, non-finite numbers, a
repeated action or branch, mismatched endpoints and cyclic precedence are refused before any solve.

The result JSON (`schemaVersion`, `study`, `provenance`, `effectiveOptions`, `stepOrder`, `inputBranches`, `result`)
is printed to stdout and, with `--out`, written to a new file via a temporary file and a final move; an existing `--out`
path is refused. `result` holds the status and outcome with its reason, total, initial and final unserved load (MW),
`objectiveMwh`, `lowerBoundMwh`, `upperBoundMwh` and `relaxedLowerBoundMwh`, the initial state's verifier evidence,
the sequence (per step: action, branch, start and end minute, unserved MW before and after, step energy, energised
buses and the full verifier evidence with original branch and unit indices), every evaluated state with its
disposition, and the search counts (nodes, states, pruned, unverified).

Exit 0 `OPTIMAL`. Exit 3 `FEASIBLE`, `INFEASIBLE` or `UNRESOLVED` (the result is still printed). Exit 1 for input it
cannot accept (malformed or unknown-field study, case identity mismatch, invalid action or precedence, unreadable case,
existing output path). Exit 2 for an invalid invocation.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |
