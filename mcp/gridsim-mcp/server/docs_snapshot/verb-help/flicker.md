### flicker

`gridsim flicker <study.json> [--out <result.json>]`

Result files: `--json <file.json>` or `--csv <file.csv>` write the result to a file through the shared writer ([Result files](#result-files)); the document is built from the same result this verb prints, and `--json` is the form `gridsim.run` and `gridsim rpc` use. `--out` is unchanged: it still names its result JSON, not a shared-writer file.

UK flicker and rapid-voltage-change assessment by ENA Engineering Recommendation P28 Issue 2 (2019; docs/reference S141; free), and
the structure of the IEC 61000-4-15 flickermeter (RC4 R4-21). The study file is strict JSON, schema version 1: `schemaVersion` (1),
`study` (`flicker`), `nominalKv` (selects the Table 2 planning levels), optional `sources` and `background` (each `id`, `pst`,
optionally `plt`; summated by equation 1 with `exponent`, default 3, Table 8), `transfer` (`fromKv`, or an explicit `coefficient`,
for the Table 3 transfer from a higher voltage; flicker is not transferred upward, and a pair the table does not list is refused),
`steps` (a voltage step as `stepPct`, or `deltaSMva` with `sscMva` by equation 4, with `intervalSeconds`; judged by the simplified
rule of clause 6.3.3.2), `pst05Curve` (points read from Figure B.1.2: `intervalSeconds`, `stepPct`), `inrush` (annex C equation C.1:
`m`, `k`, `ratedMva`, `sscMva`), `rvc` (a rapid voltage change: `category` 1, 2 or 3, `samples` of `timeS` and `deviationPct`, and
`steadyStateChangePct`, checked against the envelopes of Table 4 and Figures 5 to 7 and the 3 percent step limit), `stage1`
(the LV Stage 1 conditions of clause 6.3.2) and `flickermeter`.

The Pst = 0.5 curve of Figure B.1.2 and the shape-factor curves are figures, not tables, in the document: the rule decides without a
curve only at the points the text states (3 percent once every 475 s; 1 percent not more than every 20 s) and is otherwise
`INCOMPLETE` until curve points or a flickermeter result are supplied. The flickermeter (`parameters` and a synthetic `signal`)
runs the block structure (input adaptor, squaring demodulator, high-pass, Butterworth low-pass and weighting filter, squaring and a
first-order sliding mean, percentiles and the Pst combination). IEC 61000-4-15 is a paid standard: no filter coefficient is built
in, a `flickermeter` without `parameters` reports `AWAITING STANDARD TEXT`, and any result computed carries the same status. It
is checked here only by derivation (analytic responses, linearity), not against the standard's test points.

Exit 0 `ACCEPTED`. Exit 3 `NOT ACCEPTED` or `INCOMPLETE` (the result is printed). Exit 1 for input it cannot accept. Exit 2 for an
invalid invocation.

| Flag | Default | Meaning |
|---|---|---|
| `--out` | none | Also write the result JSON to this new file. |
