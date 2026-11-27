### grid-code

`gridsim grid-code <study.json> [--out <result.json>]` and `gridsim grid-code --list`

The grid-code compliance suite (`GridSim.Core.Compliance.GridCode`): fault ride-through, reactive capability, frequency
response, RoCoF and frequency withstand of one unit against the GB Grid Code European Connection Conditions (Issue 6
Revision 33, 11 June 2025) or Regulation (EU) 2016/631 (RfG). GB values are fixed by the code; RfG values are the TSO's
choices, each refused unless it lies inside the Regulation's range (the message names the parameter, the range and the
article). The requirement data ships as `gridsim-gridcode-requirements/1` files, one per edition, every value with its
clause; `--list` prints their ids and titles and the check ids. See [ch. 15](<0015 RMS, modal and impedance stability.md#grid-code-compliance>)
for the checks and their methods.

The study file is strict JSON, `gridsim-gridcode-study/1` (schema `schemas/gridcode/gridsim-gridcode-study-1.schema.json`):
`schema`, `label`, `case` (a case path relative to the study file, read as any case argument), `unit` (`bus`, and `name`
when the bus has several generators), `pccBus`, `code` (`gb-ecc` or `eu-rfg`), `moduleType` (`B`, `C` or `D`),
`technology` (`synchronous`, `ppm`, `hvdc`, `dc-ppm` or `ppm-gfm`), `connectionKv` and `checks`; optionally `dynData` (a
`gridsim-dynamics/2` file, or a `.dyr` file for a `.raw` case, binding every generator; needed by the RMS and governor
checks), `plantBuses` (for `q-capability`), `frtProfile` (RfG: the TSO's `uret` ... `trec3`), `extendedClearingTime`
(RfG: allows tclear up to 0.25 s), `area` (RfG: `CE`, `Nordic`, `GB`, `IE-NI` or `Baltic`), `fsmMode` (GB LFSM-O from
50.5 Hz) and `stepSeconds` (RMS step, default 0.01). The checks are `frt-envelope`, `frt-retained-voltage`,
`frt-recovery`, `frt-uv-settings`, `q-capability`, `lfsm-o`, `lfsm-u`, `fsm-step`, `rocof-settings`, `rocof-event` and
`frequency-relays`, each `{ "id": ..., inputs }` as the schema lists.

Four more GB checks run only when named, with `code` `gb-ecc`. Their values come from the extension file
`gb-ecc-issue6-rev33-ext`, and a result that runs one records its hash as `provenance.requirementsExtSha256`:

- `ffci-envelope`: optional `fault` and `durationSeconds`, as `frt-envelope`.
- `frt-long`: optional `durationsSeconds` (default: the profile's labelled corners over 0.14 s), `stageSeconds` (0.05),
  `afterSeconds` (1.0), `maxPasses` (8), `bus` (default the PCC) and `atSeconds` (0.1). Times must be whole steps.
- `avr-step`: optional `stepSeconds` (default the study step) and `durationSeconds` (10, longer than 3 s). It needs the
  unit's exciter in `dynData`.
- `vctrl-step`: `events` (as `rocof-event`) and `durationSeconds`; optional `leadToLag` (the 2 s limit) and `qmaxMvar`
  and `qminMvar` (default the unit's case values).

Under `eu-rfg` these four are refused.

The result is `gridsim-gridcode/1`: per check the id, case, clause, outcome, status, value, limit, signed margin (positive
inside the requirement), unit, the time of the margin and the reason, then the counts and the verdict: `NON-COMPLIANT` on
any failure, else `INCOMPLETE` while any check is not evaluated or not demonstrated, else `COMPLIANT`. A check that cannot
be evaluated is never a pass. Numbers are written in invariant culture with shortest round-trip formatting, so two runs give
the same bytes. Exit 0 `COMPLIANT`, 3 anything else, 1 for input it cannot accept, 2 for an invalid invocation.

Result files: `--json <file.json>` or `--csv <file.csv>` write the result to a file through the shared writer ([Result files](#result-files)): the `gridsim-gridcode/1` document itself, its scalar members as the summary and each array as a table. `--out` is unchanged: it still names its result file, not a shared-writer file.

| Flag | Default | Meaning |
|---|---|---|
| `--list` | off | Print the built-in requirement sets and the check ids, and exit; no study is read. |
| `--list-ext` | off | Print the GB extension requirement set and the extension check ids (`ffci-envelope`, `frt-long`, `avr-step`, `vctrl-step`), and exit; no study is read. |
| `--out` | none | Also write the result JSON to this new file. |
