### protection-grade

`gridsim protection-grade <case> --devices <set.json> [--types 3ph,slg] [--fractions 0.05,...,0.95] [--cti 0.4] [--curves <dir>]`

Coordination checks over placed devices (RC5 WP4). Faults are every bus of the case in model order, for each listed type.
G1, grading margin: at each fault, for every device with the fault beyond it, the nearest upstream device on its supply
path is its backup (the `sc-trace` rule); when both operate, the backup must be at least `--cti` slower (distance zone
times, curve times for 50/51, 50N/51N and 67, a fuse's total clear as primary and minimum melt as backup, 0 for 87); a
pair where either does not operate is listed as not graded. G2, zone-1 underreach: a bolted three-phase fault at the far
bus of a 21's branch must not be in zone 1. G3, zone-2 cover: the same fault must be in zone 2 or faster. G4, zone-1
coverage: the largest swept fraction of the protected line, from the relay's end, at which zone 1 operates (none is a
failure). G5, earth-fault sensitivity: for a 50N/51N, the smallest |3I0| over the `slg` faults beyond it over its pickup,
passing when the device operates there. G6, zone reaches load, only for a 21 with `loadEncroachment`: the phase-loop
impedance at the prefault load point must lie in no forward zone, or else in the blinder's load region. G7, zone
completeness, only for an 87B: every branch leaving the zone (or the check zone) has a CT at its zone end; a missing CT
fails, naming the branch, and the element is not evaluated. G8, stability, for an 87T or 87B: it does not operate for any
fault of the study outside its zone. A check that could not be made is listed as not evaluated with its reason, never
as a pass. The verdict is `Coordinated`, `NotCoordinated` or `Incomplete` (nothing could be graded). Exit 0 coordinated; 3
not coordinated or incomplete; 1 invalid input; 2 usage.

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary (verdict, required margin, counts) and the tables `pairs`, `checks` and `not_evaluated`.

| Flag | Default | Meaning |
|---|---|---|
| `--devices` | required | The `gridsim-devices/1` file. |
| `--types` | `3ph,slg` | Comma list of fault types: `3ph`, `slg`, `ll`, `llg`. |
| `--fractions` | 0.05 to 0.95 in steps of 0.05 | The G4 sweep, numbers inside (0, 1). |
| `--cti` | 0.40 | Required coordination time interval (s). |
| `--curves` | installed data | Curve data directory. |
