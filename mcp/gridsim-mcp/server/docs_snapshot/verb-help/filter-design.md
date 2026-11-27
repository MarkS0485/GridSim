### filter-design

`gridsim filter-design <study.json> [--out <result.json>]`

Result files: `--json <file.json>` or `--csv <file.csv>` write the result to a file through the shared writer ([Result files](#result-files)); the document is built from the same result this verb prints, and `--json` is the form `gridsim.run` and `gridsim rpc` use. `--out` is unchanged: it still names its result JSON, not a shared-writer file.

Passive harmonic filter design by closed forms (RC4 R4-21): single-tuned, high-pass (second-order damped) and C-type. The study file
is strict JSON, schema version 1: `schemaVersion` (1), `study` (`filter-design`), `filters` (each `id`, `kind` `single-tuned`,
`high-pass` or `c-type`, `voltageKv` phase-phase, `fundamentalMvar`, `tunedOrder`, optionally `qualityFactor` and
`fundamentalHz`), optional `network` (a `source` of `sscMva` and `xOverR`, or a `case` with `sha256` and a `bus`, whose driving-point
impedance is taken from the existing frequency sweep) and `sweep` (`minOrder`, `maxOrder`, `step`).

For a single-tuned filter at order h generating Q at the fundamental on a bus of voltage V, `X_C = V^2 h^2 / (Q (h^2 - 1))` and
`X_L = X_C / h^2`; the impedance is the series resistance, zero without one, at h exactly, and the parallel resonance with a
source reactance `X_s` is at `sqrt(X_C / (X_L + X_s))`. The high-pass filter solves `X_C` so that exactly Q is generated at the
fundamental; the C-type has `X_C1 = V^2 / Q`, `X_C2 = X_C1 / (h^2 - 1)` and `X_L = X_C2`, so its resistor carries no fundamental
current. The result gives the components (ohm, microfarad, millihenry), the fundamental real power, and with a network the
parallel and series resonance orders of the network with the filter in parallel. Quality factor `q = R / (h X_L)`.

Exit 0. Exit 1 for input it cannot accept (a malformed study, a case identity mismatch, a non-physical filter, an existing output
path). Exit 2 for an invalid invocation.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |
