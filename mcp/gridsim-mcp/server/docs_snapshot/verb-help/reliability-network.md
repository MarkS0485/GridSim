### reliability-network

`gridsim reliability-network <study.json> [--out <result.json>] [--threads N]`

Result files: `--json <file.json>` or `--csv <file.csv>` write the result to a file through the shared writer ([Result files](#result-files)); the document is built from the same result this verb prints, and `--json` is the form `gridsim.run` and `gridsim rpc` use. `--out` is unchanged: it still names its result JSON, not a shared-writer file.

Reliability and restoration joined on the real network (RC4 R4-13). The RC3 `reliability-study` computes radial-feeder
indices from a stated list of components and reads no case; this verb reads a case, attaches failure data to the R4-01
groups, isolates each failure with the R4-07 placed devices and declared switches, restores the de-energised loads by
re-closing the source or by closing a normally-open tie, and verifies every restored network with the incumbent
`restoration-study` enumerator (AC power flow, voltage band, branch RATE_A). Both RC3 verbs are unchanged.

The study file is strict JSON, schema version 1: `schemaVersion` (1), `study` (`reliability-network`), `method`
(`enumeration` or `monte-carlo`), `case` (`path`, relative to the study file, or `builtIn`, plus `sha256`, bound exactly as
for `transmission-security`), `groups` (`path` of a `gridsim-groups/1` document, bound to the case by branch identity),
optional `devices` (`path` of a `gridsim-devices/1` document: every device clears faults on its branch and below; a breaker
or recloser can also isolate), `failureData` (per group: `group`, and any of `failuresPerKmYear`, `failuresPerItemYear`,
`repairHours`, `switchingHours`; the rate of a branch is the per-km rate times its circuit length plus the per-item rate, and
a per-km rate on a branch with no stated length is reported missing), optional `overrides` (a `branch` of `index`, `from`,
`to` with `failuresPerYear`, `repairHours`, `switchingHours`), optional `switches` and `ties` (each `id` and `branch`; a tie
is declared normally open and the study opens it whatever the case stores, a switch must be in service), `loadPoints` (each `id`, `bus`, `customers` and optionally
`averageLoadMw`, default the bus Pd), `switchingHours` (the default switching time), `hoursPerYear` (for ASAI, stated
explicitly), `acVerification` (`enabled` with `minPu` and `maxPu`, or `enabled` false: restoration is then decided by
connectivity and every event says the AC state was NOT RUN) and optionally `order` (only 1; 2 is reported OUT OF SCOPE).
`method` `monte-carlo` also needs `monteCarlo` (`seed`, mandatory, `years`, optional `repairDistribution` `exponential`
(default, mean = the repair time) or `deterministic`, and `domain`). Unknown or duplicated fields and non-finite numbers
are refused before any physics.

Order: bind groups and failure data, then for each component in branch order: outage event, clearing by the nearest placed
device (otherwise the feeder breaker), isolation by the nearest switching device, the unsupplied loads, restoration
alternatives, AC verification, accumulation in a fixed order, indices. The in-service network must be radial from the
single slack bus (a loop is OUT OF SCOPE). The intact network must itself pass the AC verifier, or the result is OUT OF
SCOPE with the verifier's verdict. Per load point the result gives the failure frequency, the annual outage duration and
the average outage duration, with one exposure per component that interrupts it (`Repair`, `Switching` for source
re-closure, `BackFeed` for a tie). System SAIFI, SAIDI, CAIDI, ASAI, ENS and AENS use the RC3 expressions and summation
order, so on a network with no ties the figures equal `reliability-study` on the same data bit for bit. Each outage event
lists its clearing device, the isolating switch, the interrupted buses, each interrupted load's restoration (a load that
cannot be restored before the repair is reported `Repair` with the reason), the restoration path with the AC verdict after
each step, and the final AC state. Monte Carlo samples the same first-order event model (Poisson failures per year, repair
times per failure) on the seeded streams of RC4 (`gridsim-rng/1`), reports a standard error for each system index and the
seed, domains and result digest, and is bit-identical for any `--threads`.

First order only: one component fails at a time and overlapping outages are neglected; second-order enumeration,
chronological adequacy with storage, protection failure modes and weather-dependent rates are deferred. A breaker or
recloser or declared switch makes its branch a section isolated at both ends. PowerFactory's reliability module was not
compared (COMPARATOR-UNVERIFIED).

The result JSON (`schemaVersion`, `format` `gridsim-reliability-network/1`, `provenance` with study, groups, devices and
case SHA-256 and engine, `effectiveOptions`, `stepOrder`, `result`) is printed to stdout and, with `--out`, written to a
new file via a temporary file and a final move; an existing `--out` path is refused.

Exit 0 `COMPLETE`. Exit 3 `INCOMPLETE` (rate or time data missing; the computable values are still printed) or `OUT OF
SCOPE`. Exit 1 for input it cannot accept (a malformed or unknown-field study, a case identity mismatch, a group or device
that does not bind, a missing seed, an unreadable file, an existing output path). Exit 2 for an invalid invocation.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |
| `--threads` | runtime default | Most worker threads for the Monte Carlo years; the result is bit-identical for any value. |
