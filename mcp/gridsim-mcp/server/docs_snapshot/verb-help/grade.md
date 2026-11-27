### grade

`gridsim grade --chain CURVE:PICKUP:TD,... --current I[,I...] [--cti 0.4]`

Inverse-time overcurrent grading (`OvercurrentCoordination.GradeChain`): a radial chain of relays, listed downstream
first, graded pair by pair (primary then its backup) at each fault current. Each relay is `CURVE:PICKUP:TIMEDIAL` with
CURVE one of `iec-si`, `iec-vi`, `iec-ei`, `iec-lti` (IEC 60255-151, TMS) or `ieee-mi`, `ieee-vi`, `ieee-ei` (IEEE
C37.112, TD), or `dt:PICKUP:DELAY_S` for definite time. Pickup and current share one unit (amps or per unit); network
fault currents per branch come from `iec60909` (the current is an input here, which keeps the curves exact). A pair is
coordinated when the primary operates, the backup operates too, and the backup lags the primary by at least the CTI
(D-DOC-092: a backup that never operates at the current, below its pickup, reads an infinite margin and
`NOT COORDINATED (backup does not operate)`). Prints each pair's times, margin and verdict (a time
that never comes is `never`; an infinite margin prints `inf` or `-inf` with its sign, D-DOC-169), then the binding
(smallest) margin. Exit 0 when every pair is coordinated at every current, 3 when any is not, 2 on a usage error (an
unknown curve, a non-positive pickup or time dial, a negative delay, current or CTI, fewer than two relays, or a
`--current` list that is not strictly ascending). See [technical ch. 26](<../technical/0026 Overcurrent coordination (IDMT grading).md>).

| Flag | Default | Meaning |
|---|---|---|
| `--chain` | required | The relays, comma-separated, downstream first. |
| `--current` | required | One or more fault currents, comma-separated, finite and at least 0, in strictly ascending order (D-DOC-174). The comma separates currents, so a decimal comma such as `2000,5` used to be graded as the two currents 2000 and 5; out of order or repeated it now exits 2 asking for a decimal point (`2000.5`). Each current is printed as its own `fault current` section, so what was graded is always visible. |
| `--cti` | 0.40 | Required coordination time interval (s), finite and at least 0 (`OvercurrentCoordination.DefaultCoordinationTimeIntervalS`). |

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).
