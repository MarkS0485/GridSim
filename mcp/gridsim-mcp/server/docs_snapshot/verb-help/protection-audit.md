### protection-audit

`gridsim protection-audit <study.json> [--curves <dir>]`

Coordination audit of a placed-device set (RC4 R4-07). `<study.json>` is a strict `gridsim-protection-audit/1` document:
`manifestVersion`, `case` (a path relative to the study file, or a case name), `devices` (path), optional `curves`
(directory), optional `coordinationTimeIntervalS` (default 0.40), optional `faults` (`buses`: a list of bus IDs; `alongLine`:
a list of `{branch, fractions}`; default every bus in model order). Order: validate and bind; for each fault, maximum-fault
trace then minimum-fault trace; pair margins aggregated (worst margin and the fault that sets it); each device's
sensitivity at its smallest in-zone fault (current over pickup; a device that would not operate there is a failure); the
instantaneous reach (the current at a fault at the far end of the device's own branch over its instantaneous pickup: at or
above 1 it overreaches); each breaker's `CheckBreaker` margins against the maximum three-phase duty at its bus; and every
check that could not be made, with its reason (missing evidence is never a pass). The verdict is `Coordinated`,
`NotCoordinated` or `Incomplete` (no pair could be graded). Exit 0 coordinated and every device operating at its minimum
fault with no instantaneous overreach and every evaluated breaker adequate; 3 otherwise (an `Incomplete` audit and a breaker
printed NOT ADEQUATE included); 1 invalid input; 2 usage.

| Flag | Default | Meaning |
|---|---|---|
| `--curves` | study's `curves`, else installed data | Curve data directory; overrides the study file's. |

Result files: `--json`, `--csv` or `--out` write the summary (`format` = `gridsim-protection-audit/1`, verdict, counts) and the
tables `pairs`, `device_sensitivity`, `breakers` and `not_evaluated` ([Result files](#result-files)).
