### reliability-study

`gridsim reliability-study <study.json> [--out <result.json>]`

Analytical radial-feeder reliability study (RC3 P8). It reads no case and runs no power flow: the feeder topology,
failure rates, repair and switching times, customers, average load and dependency assumptions are all stated in the
study file before any index is computed.

Each component is a series element fed from the downstream end of its `parent` (null: the source, behind the feeder
breaker). A fault on a component is cleared by the nearest `fused` component on its path to the source (itself first),
otherwise by the feeder breaker; every load point downstream of that device is interrupted. A load point at or
downstream of the failed component waits for its repair (`repairHours`). Any other interrupted load point waits only
for `switchingHours` when a `switchable` component lies on the failed component's path below the point where it joins
the load point's path (that disconnector isolates the fault), otherwise for the repair. Per load point j: failure rate
lambda_j = sum lambda_k (interruptions/yr), annual outage time U_j = sum lambda_k t_kj (h/yr), r_j = U_j / lambda_j
(h/interruption), energy not supplied ENS_j = L_j U_j (MWh/yr). System indices over the customers N_j: SAIFI
(interruptions/(customer.yr)), SAIDI (h/(customer.yr)), CAIDI = SAIDI / SAIFI (h/interruption), ASAI = 1 - SAIDI /
hoursPerYear, ENS (MWh/yr) and AENS (MWh/(customer.yr)). Each load point lists every failure mode affecting it.

The study file is strict JSON, schema version 1: `schemaVersion` (1), `assumptions` (`failureDependency`, which must
be `IndependentFirstOrder`: independent failures, overlapping outages neglected; `alternateSupply`, which must be
`None`: no back-feed; `hoursPerYear`, stated explicitly), optional `switchingHours`, `components` (each `id`, `parent`
(required, may be null), `fused`, `switchable`, and `failuresPerYear` and `repairHours`, which may be omitted or null)
and `loadPoints` (each `id`, `component`, `customers`, `averageLoadMw`). Unknown or duplicated fields, non-finite or
negative numbers, an unknown parent or component, a cycle, an unsupported assumption and no customers are refused.

Missing rate data is reported, never assumed: a component without `failuresPerYear` or `repairHours`, or a missing
`switchingHours` where isolation applies, is listed with the load points whose indices it withholds; those values and
the affected system indices are null and the status is `INCOMPLETE`. The result also lists the scope (radial, sustained
interruptions only, perfect selective protection, no alternate supply or transfer-capacity check, constant average
load) and the largest lambda x r product, as a check on the first-order approximation.

The result JSON (`schemaVersion`, `study`, `provenance`, `effectiveOptions`, `stepOrder`, `result`) is printed to stdout
and, with `--out`, written to a new file via a temporary file and a final move; an existing `--out` path is refused.

Exit 0 `COMPLETE`. Exit 3 `INCOMPLETE` (missing rate data; the result is still printed). Exit 1 for input it cannot
accept (malformed, unknown-field or invalid study, unsupported assumption, unreadable file, existing output path).
Exit 2 for an invalid invocation.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |
