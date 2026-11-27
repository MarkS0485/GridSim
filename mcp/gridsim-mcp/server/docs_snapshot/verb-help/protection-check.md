### protection-check

`gridsim protection-check <case|case.m|case-dir> [--uv pu] [--ov pu] [--uf Hz] [--of Hz] [--oc xIn] [--thermal frac] [--rocof Hz/s] [--cascade [--alarms <soe.csv>] | --fault-bus ID [--zf pu] [--fault-type 3ph|slg|ll|dlg]]`

Passive protection assessment of the solved state with generic relay settings: which elements would operate, with
pickup, time and basis. The solve itself is unchanged. Elements that need a trajectory (frequency derivatives) are
listed as not evaluated on a steady snapshot. The assessment requires a converged solve: a non-converged state never
gets a verdict. The verb refuses it with `protection-check: the power flow did not converge (...)` and exit 3. Exit 2
without a case; exit 1 on a load error.

With `--fault-bus ID` the same settings are assessed on a balanced three-phase fault at that bus
(`ProtectionAssessment.AssessThreePhaseFault`: the fault superposed on the converged prefault state, as the `fault`
verb does), so the current, voltage, distance and busbar-differential elements see a real fault; the fault current and
Thevenin impedance are printed first. Exit 0 with the verdict list; 2 for an unknown fault bus, a non-integer
`--fault-bus`, `--zf` without `--fault-bus`, a negative or non-finite `--zf`, or `--fault-bus` with `--cascade`; 1 when the fault
study itself fails; 3 when the prefault power flow does not converge, or when the converter current iteration of the
fault study does not converge (the study is printed, the relays are reported `not assessed`: no verdict is computed from a
state that did not converge).

The operating elements are listed fastest first (D-DOC-158): those with an operating time in ascending time
(instantaneous first, the busbar differential `87` among them; equal times in row order, that is the relay registry
order and then the bus / branch order of the case: N270, it used to be the margin, whose last bit decided between
distance zones that have the same margin in exact arithmetic), then the rest (voltage, frequency, reverse power,
undercurrent) by their margin relative to their own pickup. They used to be sorted by the raw margin, whose unit differs per element, so a
reverse-power margin in MW headed a fault report; the `e.g.` line of the verdict names the first row. The undercurrent
element `37` watches in-service branches only (D-DOC-091: it used to operate on every out-of-service branch, whose
flow entry is zero). The earth-fault distance element `21N` measures a branch end's earth loops only when the residual
current there exceeds 10 % of the largest phase current (`ProtectionSettings.EarthDistanceResidualReleaseFraction`,
generic; the 0.10 is the preset that the Siemens 7SA522 V4.3 manual recommends, address 1207A "3I0>/ Iphmax";
D-DOC-088: it used to operate on a line-to-line fault, which has no residual current).

| Flag | Default | Meaning |
|---|---|---|
| `--uv` / `--ov` | 0.80 / 1.10 | Under- and over-voltage pickup (pu). The defaults are the G99 Issue 2 Table 10.1 HV settings (`ProtectionSettings.UnderVoltagePu`, `OverVoltagePu`). |
| `--uf` / `--of` | 47.5 / 52.0 | Under- and over-frequency pickup (Hz). The defaults are G99 settings for a 50 Hz system: on a case at another nominal frequency (`case9`, 60 Hz; a `.m` file declared with `--frequency-hz`) `81U` and `81O` are reported not checkable, naming both frequencies, instead of operating on the healthy steady state. Giving both `--uf` and `--of` supplies settings for the case, taken as written for its own nominal frequency (D2, October 2026). |
| `--oc` | 2.50 | Instantaneous overcurrent pickup (multiple of rating). |
| `--thermal` | 1.00 | Thermal pickup (fraction of rating). |
| `--rocof` | 1.0 | RoCoF trip setting (Hz/s). |
| `--cascade` | off | Follow the propagation: trip the first branch element, re-solve, repeat, and print each step and the stop reason. |
| `--fault-bus ID` | - | Assess the protection on a balanced three-phase fault at bus ID instead of the steady state. Not with `--cascade`. |
| `--zf pu` | 0 | With `--fault-bus`: the fault reactance (pu, Zf = j`--zf`, 0 is bolted; printed `Zf=j...`); a negative or non-finite value exits 2. `dist3ph fault --zf` is a resistance instead (D-DOC-166). |
| `--fault-type` | `3ph` | With `--fault-bus`: `slg`, `ll` or `dlg` assess an unbalanced fault from the [`fault`](#fault) verb's sequence-network study: the earth-fault (`50N`, `51N`, `21N`) and negative-sequence (`46`) elements are evaluated on the sequence currents, the phase elements on the phase currents and voltages built from them. `3ph` is the balanced study. Exit 2 without `--fault-bus` or for another value. On a model with no transformer winding connection anywhere (every MATPOWER and JSON v1 case) `slg` and `dlg` print the verdicts, then say on stderr that the earth-fault verdicts reflect missing data and exit 3. |
| `--alarms <soe.csv>` | - | With `--cascade`: cross-reference the predicted cascade against a real sequence-of-events log (`SequenceOfEvents.CrossReference`; matched by branch and order, not exact time). Prints agreements, predicted-not-observed, observed-not-predicted and out-of-order trips, divergences first. Exit 3 when the prediction does not reproduce the log; exit 2 without `--cascade`; exit 1 when the file is missing or malformed (named line). |

The alarm log is CSV with the header `at_s,device,from,to,description`: seconds from the event start (finite), the ANSI
device id as logged (non-empty), the branch's two end-bus ids (both 0 for a non-branch alarm such as a bus voltage or
frequency alarm, which is listed but not matched), and free text (everything after the fourth comma). Blank lines and
lines starting with `#` are skipped; any other malformed line is refused, never dropped.

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).
