### harmonic-assess

`gridsim harmonic-assess <study.json> [--out <result.json>]`
`gridsim harmonic-assess --report-template --out <template.json>`

Result files: `--json <file.json>` or `--csv <file.csv>` write the result to a file through the shared writer ([Result files](#result-files)); the document is built from the same result this verb prints, and `--json` is the form `gridsim.run` and `gridsim rpc` use. `--out` is unchanged: it still names its result JSON, not a shared-writer file.

UK harmonic connection assessment by the staged procedure of ENA Engineering Recommendation G5 Issue 5 (2020; docs/reference
S139; the ENA publishes it free of charge), RC4 R4-21. It is new alongside `harmonics`, which is unchanged. For a PCC of 0.4 kV
and below: Stage 1A (product-standard compliance, declared by the user), 1B (not evaluated: the IEC 61000-3-12 short-circuit-ratio
rule is not implemented, which can only send a connection on), 1C and 1D (aggregate rated power or minimum short-circuit power,
Table 19 and equations 10 to 20), then 2C. Above 0.4 kV and below 33 kV: Stage 2A, 2B (Table 21, equations 21 to 29, for 6.6,
11, 20 and 22 kV) and 2C, the prediction of the harmonic voltage against the planning levels with the worst-case source impedance
(equations 30 to 43, Table 22). A 2C failure at an LV PCC is "no connection possible without mitigation"; above LV it goes to
Stage 3. A PCC of 33 kV or above is Stage 3 outright. An optional clause 9 resonant-plant test (equations 44 to 49) is included.

The study file is strict JSON, schema version 1: `schemaVersion` (1), `study` (`harmonic-assess`), `pcc` (`lineKv`, `sscMva`
the minimum three-phase short-circuit power, optional `sscSinglePhaseMva`, `xOverR`), `equipment` (each `id`, `technology`
`six-pulse`, `active-front-end`, `twelve-pulse` or `single-phase-rectifier`, `ratingKva`), optional `declaredCompliantIec61000_3_2`,
optional `background` (`mode` `measured` with `pct`, the level by order in percent of the fundamental, and optionally
`missingOrdersAreZero`; or `mode` `assumed-75`, the 75 percent of the planning level of clause 8.4.1, subject to the network
operator's agreement), optional `emissionPct` (the current by order; default the rating-weighted G5 Annex B Table B.3 profile of
the technology), optional `diversityDeclared` (Table 16 exponents; default linear addition, clause 6.3.3), `maxOrder` (default
100), `resonantPlant` (`sscXMva`, `qrpMvar`, `pdMw`) and `stage3`. `stage3` runs the clause 10 assessment on a case: `case`
(`path` or `builtIn` with `sha256`, bound as for `transmission-security`), `pccBus`, `sources` (each `bus`, a library `type` or an
explicit `spectrum` of `order`, `pct`, and a `currentA` or `fundamentalCurrentPu`), `options` (`orders`, `balanced`,
`loadShunts`, `generatorSources`), `pccBackgroundPct`, `remoteBackground` (per bus), `assumedRemoteBackgroundFraction`,
`mvaCapacity` (needed above 132 kV), `transferThreshold` and `missingBackgroundIsZero`. Unknown or duplicated fields and
non-finite numbers are refused before any physics.

Stage 3 uses the existing penetration solver for the new user's voltage at every bus, the transfer coefficient
`|Z_PCC,n| / |Z_PCC|` from a unit current at the PCC (equation 50), the headroom of equations 51 to 53, the apportionment
multiplier (0.5 up to 132 kV; equations 56 to 60 above), the incremental and total limits (54, 63, 64), the resonant allowance
(65) and compliance by 67 to 69. A missing background is `INCOMPLETE` and named, never a pass. Where the document's text is
ambiguous the result's notes say how it was read (the 75 percent assumption is applied to THDV as one level; the Table 22
factor `k` by order). The final decision rests with the network operator. PowerFactory's power-quality module was not compared
(COMPARATOR-UNVERIFIED).

`--report-template --out <file>` writes a `gridsim-report-template/1` bound to this result, so `gridsim report <template> <result>
--out <file.md|html|csv>` renders the assessment through the R4-10 generator.

The result JSON (`schemaVersion`, `format` `gridsim-harmonic-assess/1`, `provenance`, `result` with `verdict`, `route`,
`planningTable`, `thdPlanningLevelPct`, `substages`, `stage2C`, `resonantPlant`, `notes`, `stage3`) is printed to stdout and,
with `--out`, written to a new file via a temporary file and a final move; an existing `--out` path is refused.

Exit 0 `ACCEPTED`. Exit 3 for `STAGE 3 REQUIRED`, `NO CONNECTION WITHOUT MITIGATION` or `INCOMPLETE` (the result is still
printed). Exit 1 for input it cannot accept (a malformed or unknown-field study, a case identity mismatch, an unreadable file, an
existing output path). Exit 2 for an invalid invocation.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file (with `--report-template`, the template). |
| `--report-template` | off | Write the report template for this verb's result to `--out` and exit; no study is read. |
