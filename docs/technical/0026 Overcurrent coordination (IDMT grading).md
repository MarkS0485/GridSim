# 26. Overcurrent coordination (IDMT grading)

This chapter specifies how GridSim grades inverse-time overcurrent (ANSI 51) relays against one another. It covers
the time-current-curve (TCC) library for the IEC 60255-151 and IEEE C37.112 families and for definite time, the
grading algorithm that compares a primary (downstream) relay with its backup (upstream) relay at a fault current,
the `gridsim grade` verb that exposes it, and how `gridsim protection-check --cascade --alarms` sits beside it.
The code is `src/GridSim.Core/Protection/Coordination/OvercurrentCoordination.cs`. Every function in it is a pure
closed form with no state and no solver, so a given set of relay settings and a given current always produce the
same verdict and, for a given runtime, the same operating times (the times use `Math.Pow`, and no test compares
their bits across CPU modes).

The 51 element inside the protection assessment ([ch. 17](<0017 Protection assessment (a passive relay overlay).md>)) answers a different
question: whether one relay, set to one fixed curve, picks up on a solved operating state. Grading answers whether two
relays that see the same fault trip in the right order with enough margin between them. The asset-level protection
stages are in [ch. 15](<0015 Protections.md>). The fault currents that feed a grading study are produced by
[ch. 25](<0025 IEC 60909 short-circuit duty study.md>).

## Model

### Elements

A relay is an `OvercurrentElement`: a curve family (`OvercurrentCurve`), a pickup current `Is`, and a setting. The
pickup and every fault current are in the same unit, amps or per unit; the library only ever uses their ratio

```
M = I / Is
```

For an inverse-time curve the setting is the time dial (`TimeDial`, default 1.0): the IEC time-multiplier setting
`TMS` or the IEEE time dial `TD`. For a definite-time element the setting is the fixed delay
(`DefiniteTimeSeconds`, default 0.0 s) and the time dial is not read.

### Operating time

`OvercurrentCoordination.OperateTimeSeconds(element, current)` returns the operating time in seconds. As implemented:

```
M = current / Is
definite time:   t = DefiniteTimeSeconds   if M > 1,  else t = +infinity
inverse time:    t = +infinity             if M <= 1   (no operation at or below pickup)
IEC family:      t = TMS * k / (M^alpha - 1)
IEEE family:     t = TD * ( A / (M^p - 1) + B )
```

`M^alpha` is `Math.Pow(m, alpha)`. The pickup test is strict: a current exactly equal to `Is` never operates, and a
current of exactly zero gives `M = 0` and never operates. `+infinity` is `double.PositiveInfinity`, and the CLI prints
it as `never`.

Because `M^alpha - 1` tends to zero as `M` tends to 1 from above, the IEC time tends to infinity at pickup. The
IEEE form has the same pole in its `A` term and adds the constant offset `B` that the time dial multiplies, so an
IEEE curve flattens to `TD * B` at very high current while an IEC curve tends to zero.

### Curve constants

IEC 60255-151 (`OvercurrentCoordination.IecConstants`, returns `(K, Alpha)`):

| Curve | `OvercurrentCurve` | CLI name | `k` | `alpha` |
|---|---|---|---|---|
| Standard inverse | `IecStandardInverse` | `iec-si` | 0.14 | 0.02 |
| Very inverse | `IecVeryInverse` | `iec-vi` | 13.5 | 1.0 |
| Extremely inverse | `IecExtremelyInverse` | `iec-ei` | 80.0 | 2.0 |
| Long-time inverse | `IecLongTimeInverse` | `iec-lti` | 120.0 | 1.0 |

IEEE C37.112 (`OvercurrentCoordination.IeeeConstants`, returns `(A, B, P)`):

| Curve | `OvercurrentCurve` | CLI name | `A` | `B` | `p` |
|---|---|---|---|---|---|
| Moderately inverse | `IeeeModeratelyInverse` | `ieee-mi` | 0.0515 | 0.1140 | 0.02 |
| Very inverse | `IeeeVeryInverse` | `ieee-vi` | 19.61 | 0.491 | 2.0 |
| Extremely inverse | `IeeeExtremelyInverse` | `ieee-ei` | 28.2 | 0.1217 | 2.0 |

Definite time is `DefiniteTime`, CLI name `dt`, with the delay in place of a time dial.

Asking `IecConstants` for an IEEE curve, or `IeeeConstants` for an IEC curve, throws `ArgumentOutOfRangeException`.
The IEC pairs, `(alpha, k)` in the ABB table and `(k, alpha)` in the returned tuple, are the four pairs printed in Table 8.2.1 of the ABB Distribution Automation Handbook,
section 8.2; the IEEE constants are the C37.112 values and no document in `docs/reference` prints them.

The 51 element of the protection assessment reads its standard-inverse constants from `IecConstants`, and uses the
same expression `tms * k / (Math.Pow(bigM, alpha) - 1.0)`, so the relay and this library use one table and one
formula.

### Input validation

`OperateTimeSeconds` refuses meaningless inputs with `ArgumentOutOfRangeException` rather than returning a number
that looks plausible:

- the pickup must be finite and greater than 0;
- the current must be finite and at least 0 (NaN, a negative value and `+infinity` are refused; zero is allowed and
  never operates);
- an inverse-time time dial must be finite and greater than 0;
- a definite-time delay must be finite and at least 0 s.

## Algorithm

### Grading a pair

`Grade(primary, backup, faultCurrent, requiredMarginSeconds)` evaluates both elements at the same current:

```
tP     = OperateTimeSeconds(primary, faultCurrent)
tB     = OperateTimeSeconds(backup,  faultCurrent)
margin = tB - tP
coordinated = (tP is finite) AND (tB is finite) AND (margin >= requiredMarginSeconds)
```

It returns a `GradingResult` carrying `PrimaryTimeSeconds`, `BackupTimeSeconds`, `MarginSeconds`,
`RequiredMarginSeconds` and `Coordinated`. The comparison is `>=`, so a margin exactly equal to the required
interval passes. The required interval must be finite and at least 0 s, or `Grade` throws
`ArgumentOutOfRangeException`.

The required margin is the coordination time interval (CTI). Its default is
`OvercurrentCoordination.DefaultCoordinationTimeIntervalS`, 0.40 s. National Grid PS(T)010 Issue 1 (July 2017), Appendix A.5, sets 400 ms as the minimum acceptable margin at National
Grid/DNO interfaces (larger when its formula gives more), and 0.4 s is the typical margin with electromechanical relays and oil
circuit breakers in Electricity North West EPD350 Issue 6 (March 2023), section 4.3. EPD350 allows 0.2 s where modern
electronic relays and vacuum breakers are used on both sides; a generic study cannot assume that, so the 0.2 s value is
not the default and a caller who has those relays passes a smaller interval. The CLI default
`CliDefaults.GradeCtiS` (`src/GridSim.Cli/CliDefaults.cs`) is that same constant, not a second number.

Three consequences follow directly from the two lines above.

- Identical relays have zero margin, so they never coordinate against a positive CTI.
- A primary that does not operate at the fault current (a fault below its pickup) has `tP = +infinity` and cannot
  coordinate, whatever the backup does.
- A backup that does not operate while the primary does gives `margin = +infinity`, but `tB` is not finite, so the
  pair is not coordinated: nothing would back the primary up. The margin is still reported as `inf`, and the CLI
  verdict is `NOT COORDINATED (backup does not operate)`.

If both elements are at or below pickup, `margin = infinity - infinity = NaN`, `Coordinated` is false because the
primary is infinite, and the CLI prints the margin as `-`.

### Chains

`GradeChain(downstreamToUpstream, faultCurrent, requiredMarginSeconds)` takes a list ordered from the relay nearest
the fault to the relay furthest from it. It grades each consecutive pair `(i, i+1)` with `Grade` at the one fault
current and returns the pairwise results in order. A chain of `n` elements gives `n - 1` results; a chain of one or
none gives an empty list. The chain is coordinated when every result is. Non-adjacent pairs are not graded: R1 against
R3 is not checked, because on a radial feeder R2 operates between them.

A radial feeder carries the whole fault current through every relay between the source and the fault, so one common
current is correct for the chain. For a meshed network the per-branch currents differ; obtain them from
`Iec60909.BranchFaultCurrents` ([ch. 25](<0025 IEC 60909 short-circuit duty study.md>)) and grade each pair at the current
its own branch carries with `Grade`.

### How settings are chosen

GridSim does not choose or optimise relay settings. Every pickup, time dial and delay is supplied by the caller, and
the library only evaluates and checks them. A study therefore runs by trying settings and reading the margins: the
`grade` verb takes several fault currents in one run so that the margin can be seen across the range from the minimum
to the maximum fault level, and the smallest margin in the output is the one that limits the dial.

### Failure modes

| Condition | Result |
|---|---|
| Primary at or below pickup | `Coordinated` false, `tP = +infinity`; CLI verdict `NOT COORDINATED (primary does not operate)` |
| Margin below the CTI, primary operates | `Coordinated` false; CLI verdict `NOT COORDINATED (margin below CTI)` |
| Margin negative (backup faster than primary) | Same as the line above: the margin is below the CTI |
| Backup at or below pickup, primary operates | `Coordinated` false, margin `inf`; CLI verdict `NOT COORDINATED (backup does not operate)` |
| NaN, negative or infinite current; non-positive pickup or time dial; negative delay; negative or non-finite CTI | `ArgumentOutOfRangeException` from the library, exit 2 from the CLI |
| Malformed relay spec | `FormatException` naming the spec, exit 2 from the CLI |

## The `grade` verb

```
gridsim grade --chain CURVE:PICKUP:TD,... --current I[,I...] [--cti S]
```

The verb's flags, defaults and exit codes are also listed in the `grade` section of
[0019 CLI reference.md](<../manual/0019 CLI reference.md#grade>). The behaviour that matters for this chapter is below.

- `--chain` is required: two or more relay specs separated by commas, downstream first. A spec is
  `CURVE:PICKUP:TIMEDIAL`, or `dt:PICKUP:DELAY_S` for definite time. `CURVE` is one of the CLI names in the constants
  tables, case-insensitive. The three fields are split on `:` with no extra or missing field, numbers are read in
  invariant culture, and whitespace around fields is ignored. `OvercurrentCoordination.ParseElement`
  does the parsing and throws `FormatException` naming the spec and the field.
- A decimal comma cannot be used on the command line: `--chain` and `--current` are split on commas first, so
  `--current 2000,5` grades at 2000 and at 5, and `--chain iec-si:4,00:0.1,...` fails with a field-count error for
  `iec-si:4`. `--cti 0,4` is refused as not a finite number.
- `--current` is required: one or more fault currents, comma-separated, each finite and at least 0, in the same unit
  as the pickups. The chain is graded at every current in turn.
- `--cti` is the required coordination time interval in seconds. The default is 0.4. It must be finite and at least 0.
- `--json`, `--csv` and `--out` write the result to a file. The `pairs` table has one row per pair per current
  (`fault_current`, `pair`, `primary_s`, `backup_s`, `margin_s`, `required_margin_s`, `coordinated`); a time that never
  operates is `Infinity` and an undefined margin is NaN there. The `relays` table lists each relay's curve, pickup,
  time dial and definite-time delay, and the summary carries `relays`, `cti_s`, `all_coordinated` and
  `binding_margin_s` (`null` when any pair is not coordinated; a coordinated pair always has a finite margin).

Output is a header with the relay count and the CTI, one line per relay, then for each current a table of pairs
(`R1->R2` is relay 1 as primary and relay 2 as backup) with times and margin to four decimals and a verdict, then a
final line. Times print as `F4`; the file output carries full precision. The times are computed from the settings, not measured;
the printed four-decimal values are stable.

Exit codes:

| Code | Meaning |
|---|---|
| 0 | Every pair is coordinated at every current. The last line is `GRADED:` with the binding margin. |
| 3 | At least one pair is not coordinated at some current. The last line is `NOT GRADED:`. |
| 2 | Usage error: a missing `--chain` or `--current`, a malformed spec, fewer than two relays, a bad current or CTI. |

The binding margin on the `GRADED:` line is the smallest margin among the coordinated pairs across all currents; both
times of a coordinated pair are finite, so it is always a number. The `NOT GRADED:` line prints no binding margin.

### A captured run

Three relays on the IEC standard-inverse curve, all with pickup 400, time dials 0.1, 0.3 and 0.6, graded at 2000 and
4000 (the same unit as the pickup):

```
$ gridsim grade --chain iec-si:400:0.1,iec-si:400:0.3,iec-si:400:0.6 --current 2000,4000
Overcurrent grading  -  3 relays (downstream first), CTI 0.4 s
  R1: IecStandardInverse, pickup 400, time dial 0.1
  R2: IecStandardInverse, pickup 400, time dial 0.3
  R3: IecStandardInverse, pickup 400, time dial 0.6

  fault current 2000
  | pair | primary s | backup s | margin s | verdict |
  |------|-----------|----------|----------|---------|
  | R1->R2 | 0.4280 | 1.2839 | 0.8559 | OK |
  | R2->R3 | 1.2839 | 2.5678 | 1.2839 | OK |

  fault current 4000
  | pair | primary s | backup s | margin s | verdict |
  |------|-----------|----------|----------|---------|
  | R1->R2 | 0.2971 | 0.8912 | 0.5941 | OK |
  | R2->R3 | 0.8912 | 1.7824 | 0.8912 | OK |

GRADED: every pair coordinated at every current; binding margin 0.5941 s (CTI 0.4 s).
[exit 0]
```

(Capture `docs/_captures/overcurrent-coordination/grade-chain.txt`.) A primary below its pickup:

```
$ gridsim grade --chain iec-si:1000:0.1,iec-si:400:0.5 --current 500
Overcurrent grading  -  2 relays (downstream first), CTI 0.4 s
  R1: IecStandardInverse, pickup 1000, time dial 0.1
  R2: IecStandardInverse, pickup 400, time dial 0.5

  fault current 500
  | pair | primary s | backup s | margin s | verdict |
  |------|-----------|----------|----------|---------|
  | R1->R2 | never | 15.6500 | -inf | NOT COORDINATED (primary does not operate) |

NOT GRADED: at least one pair is not coordinated (see above).
[exit 3]
```

The margin is `15.65 - infinity`, printed with its sign as `-inf`.

A usage error and a bad spec both exit 2:

```
$ gridsim grade --chain iec-si:400:0.1 --current 2000
error: --chain needs at least two relays (a primary and its backup), downstream first.
[exit 2]
```

```
$ gridsim grade --chain iec-xx:400:0.1,iec-si:400:0.3 --current 2000
error: --chain: Relay 'iec-xx:400:0.1': unknown curve 'iec-xx' (iec-si, iec-vi, iec-ei, iec-lti, ieee-mi, ieee-vi, ieee-ei or dt).
[exit 2]
```

## `protection-check --alarms`

`gridsim protection-check <case> --cascade --alarms <soe.csv>` is not a grading feature. Its `--alarms` step (`AlarmLogCsv`,
`SequenceOfEvents` in `Protection/`) does not call `OvercurrentCoordination`; the cascade's 51 element reads its
standard-inverse constants from `IecConstants`. The cascade trips
branch relays in the order of their assessed operating times, re-solves, and repeats ([ch. 17](<0017 Protection assessment (a passive relay overlay).md>));
`--alarms` then compares that predicted order with a real sequence-of-events log. The comparison is by branch and
order, not by wall-clock time, because the cascade ranks trips by relay operating time and has no real clock.

The log is CSV with the exact header `at_s,device,from,to,description`. `AlarmLogCsv` (`src/GridSim.Core/Protection/AlarmLogCsv.cs`)
reads it strictly: `at_s` is a finite number of seconds from the event start, `device` is non-empty, `from` and `to` are
non-negative integer bus ids that are either both non-zero (a branch alarm) or both 0 (a non-branch alarm such as a
bus voltage alarm, which is counted but not matched), and the description is everything after the fourth comma.
Blank lines and lines starting with `#` are skipped. Any other malformed line throws `InvalidDataException` naming
the line, so an alarm is never silently dropped.

`SequenceOfEvents.CrossReference` (`src/GridSim.Core/Protection/SequenceOfEvents.cs`) matches each predicted step, in
predicted order, to the earliest unmatched alarm on the same branch; a branch is identified by its unordered pair of
bus ids, so `6-8` and `8-6` are the same branch. The matches are classed as `Agreement`, `PredictedNotObserved`,
`ObservedNotPredicted` or `OrderMismatch` (an observed time earlier than the previous matched step's, with a
tolerance of `1e-9` s). The result is fully consistent when the last three counts are all zero.

Exit codes: 0 when the prediction reproduces the log; 3 when it diverges; 2 when `--alarms` is given without
`--cascade`; 1 when the file is missing or malformed.

An agreeing log (`docs/_captures/overcurrent-coordination/soe-agree.csv`), showing the tail of the output:

```
...
Cascade propagation (2 trip(s)):
  1. relay 50 (Instantaneous overcurrent) opens branch 6-8 in inst  ->  2 branch(es) then overloaded
  2. relay 50 (Instantaneous overcurrent) opens branch 8-28 in inst  ->  re-solve did NOT converge
  stop: non-convergent after opening 8-28 - the network has split / collapsed (blackout).

Sequence of events vs prediction (3 alarm(s), 1 non-branch not matched): 2 agree, 0 predicted not observed, 0 observed not predicted, 0 out of order
  Agreement: predicted and observed on branch 6-8 (predicted #1, observed at 0.1 s)
  Agreement: predicted and observed on branch 8-28 (predicted #2, observed at 0.35 s)
SOE: FULLY CONSISTENT - the predicted cascade reproduces the alarm log.
[exit 0]
```

The command was `gridsim protection-check case30 --cascade --oc 1.0 --alarms docs/_captures/overcurrent-coordination/soe-agree.csv`.
A diverging log (`soe-diverge.csv`) on the same case:

```
...
Sequence of events vs prediction (2 alarm(s), 0 non-branch not matched): 1 agree, 1 predicted not observed, 1 observed not predicted, 0 out of order
  PredictedNotObserved: predicted 50 trip on branch 8-28 not seen in the alarm log
  ObservedNotPredicted: observed 51 trip on branch 2-6 was not predicted
  Agreement: predicted and observed on branch 6-8 (predicted #1, observed at 0.1 s)
SOE: DIVERGES - the predicted cascade does not reproduce the alarm log.
[exit 3]
```

`--alarms` without `--cascade` is refused:

```
$ gridsim protection-check case30 --alarms docs/_captures/overcurrent-coordination/soe-agree.csv
error: --alarms cross-references the predicted cascade; add --cascade.
[exit 2]
```

## Parameters and defaults

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| Default CTI | 0.40 | s | Required margin between primary and backup | `OvercurrentCoordination.DefaultCoordinationTimeIntervalS` (`src/GridSim.Core/Protection/Coordination/OvercurrentCoordination.cs`) |
| CLI default CTI | 0.40 | s | `--cti` default; the Core constant | `CliDefaults.GradeCtiS` (`src/GridSim.Cli/CliDefaults.cs`) |
| Time dial default | 1.0 | - | `TMS` or `TD` when not given to the record | `OvercurrentElement.TimeDial` |
| Definite delay default | 0.0 | s | Fixed delay of a `DefiniteTime` element | `OvercurrentElement.DefiniteTimeSeconds` |
| `k`, `alpha` | table above | s, - | IEC 60255-151 constants | `IecConstants` |
| `A`, `B`, `p` | table above | s, s, - | IEEE C37.112 constants | `IeeeConstants` |
| Pickup | caller | A or pu | `Is`, same unit as the current | `OvercurrentElement.PickupCurrent` |
| Time dial | caller | - | `TMS` (IEC) or `TD` (IEEE), greater than 0 | `OvercurrentElement.TimeDial` |
| Order tolerance | 1e-9 | s | Out-of-order test in the SOE match | `SequenceOfEvents.CrossReference` |
| Alarm log header | `at_s,device,from,to,description` | - | Required first non-comment line | `AlarmLogCsv.Header` |

## Invariants

Each invariant is pinned by a test in `tests/GridSim.Tests`. The curve tests assert to 12 decimal places
(`Assert.Equal(expected, actual, 12)`), which is the closed-form check. The four IEC pairs are asserted exactly; the
IEEE constants are checked only through a closed form that repeats them.

| Invariant | Test |
|---|---|
| IEC standard inverse equals `TMS * 0.14 / (M^0.02 - 1)`, the form the 51 relay uses | `OvercurrentCoordinationTests.Iec_standard_inverse_matches_the_closed_form` |
| The other three IEC families equal their `(k, alpha)` closed forms | `OvercurrentCoordinationTests.Iec_families_use_their_tabulated_constants` |
| The IEEE families equal `TD * (A / (M^p - 1) + B)` | `OvercurrentCoordinationTests.Ieee_families_match_the_c37_112_closed_form` |
| At or below pickup the time is `+infinity`; doubling the dial doubles the time; time falls as current rises | `OvercurrentCoordinationTests.Below_pickup_is_infinite_and_the_dial_scales_time_linearly` |
| Definite time is the fixed delay above pickup at any level, `+infinity` below | `OvercurrentCoordinationTests.Definite_time_is_a_fixed_delay_above_pickup` |
| `MarginSeconds = backup - primary`; identical relays give margin 0 and are not coordinated | `OvercurrentCoordinationTests.Grading_margin_is_backup_minus_primary` |
| A primary below pickup is never coordinated | `OvercurrentCoordinationTests.A_non_operating_primary_is_not_coordinated` |
| A chain grades each consecutive pair, and each result equals the standalone `Grade` | `OvercurrentCoordinationTests.Chain_grades_each_consecutive_pair` |
| The four IEC pairs equal the ABB handbook table exactly; the anti-drift test pins the standard-inverse pair | `S3ProtectionGroundingTests.Iec_curve_constants_match_the_ABB_handbook_table`, `Ws13CoreConstantsAntiDriftTests` |
| The default CTI is 0.40 s, `Grade` and `GradeChain` report it as `RequiredMarginSeconds`, and 0.35 s fails it | `S3ProtectionGroundingTests.Default_coordination_time_interval_is_400_ms`, `Grade_without_a_cti_uses_the_400_ms_default` |
| The CLI default is the Core constant and the usage text says 0.4 | `S3ProtectionGroundingTests.The_cli_grade_default_is_the_core_constant_not_its_own_number` |
| Each valid spec parses to the exact element, and each bad spec fails naming the spec | `GradeAndSoeCliTests.Idmt_specs_parse_to_the_exact_element`, `A_definite_time_spec_carries_its_delay`, `Bad_specs_fail_loud_naming_the_spec` |
| A NaN, negative or infinite current, a bad time dial, a bad delay, an infinite pickup and a bad CTI are refused; zero current never operates | `GradeAndSoeCliTests.A_meaningless_fault_current_is_refused`, `Zero_current_never_operates_rather_than_failing`, `A_meaningless_time_dial_is_refused`, `A_meaningless_definite_time_delay_is_refused`, `An_infinite_pickup_and_a_meaningless_cti_are_refused` |
| A network fault current from `BranchFaultCurrents` feeds `GradeChain` end to end | `Iec60909Tests.Branch_fault_current_feeds_the_coordination_grading` |
| The alarm log parses each row exactly and refuses each malformed row with its line number; an empty file and a bad header are refused naming the problem | `GradeAndSoeCliTests.A_well_formed_log_parses_every_row_exactly`, `A_malformed_log_is_refused_with_its_line_never_dropped`, `A_missing_file_fails_loud` |
| A parsed log cross-references a cascade by unordered branch | `GradeAndSoeCliTests.A_parsed_log_cross_references_a_cascade` |
| The CLI source contains the wiring strings: the flags, the `GradeChain` call, the exit-code assignments for `grade` and the diverging log, and the message for `--alarms` without `--cascade` (a source-text check that runs nothing) | `GradeAndSoeCliTests.Cli_grade_and_alarms_are_wired_with_their_exit_codes` |
| `grade` without its flags exits 2 with the usage line; the result writer handles the verb name. No test runs `grade --chain ... --json` | `CliProcessSmokeTests`, `CliResultWriterTests` |

## Worked example

The first captured run above, recomputed by hand. The relays are R1 `iec-si:400:0.1` and R2 `iec-si:400:0.3`; the
fault current is 2000. All three relays share the pickup `Is = 400` and the IEC standard-inverse constants
`k = 0.14`, `alpha = 0.02`.

Operating time of R1 at 2000:

```
M     = 2000 / 400                      = 5
ln 5                                    = 1.6094379124341003
M^0.02 = exp(0.02 * 1.6094379124341003) = exp(0.032188758248682) = 1.032712419896443
M^0.02 - 1                              = 0.032712419896443
t(R1) = TMS * k / (M^0.02 - 1)
      = 0.1 * 0.14 / 0.032712419896443
      = 0.014 / 0.032712419896443       = 0.4279720070945375 s
```

The capture prints `0.4280`, which is that value rounded to four decimals.

The CTI check for the pair R1->R2 uses the same `M` and the same denominator, so `t(R2) = 0.3 * 0.14 / 0.032712419896443`:

```
t(R2)  = 0.042 / 0.032712419896443      = 1.2839160212836123 s
margin = t(R2) - t(R1)
       = 1.2839160212836123 - 0.4279720070945375
       = 0.8559440141890748 s
CTI    = 0.4 s (the default, printed in the capture header)
margin >= CTI  ->  0.8559 >= 0.4  ->  coordinated, verdict OK
```

The capture prints `1.2839` and `0.8559`. With the same pickup and curve the margin is `(0.3 - 0.1) * 0.14 / (M^0.02 - 1)`,
so it is proportional to the dial difference and falls as the current rises. At 4000, `M = 10`, `M^0.02 - 1 = 0.0471285...`
and the margin is `0.5941 s`; it is the smaller of the two currents' margins, which is why it is the binding margin on the
`GRADED:` line.

A failing check recomputed the same way, from `docs/_captures/overcurrent-coordination/grade-families.txt`:
`gridsim grade --chain ieee-vi:200:0.5,iec-ei:200:0.3,dt:200:1.5 --current 1600 --cti 0.2`. The fault current is 1600 and
`Is = 200`, so `M = 8` for all three relays.

```
R1 (IEEE very inverse, TD 0.5):  t = 0.5 * (19.61 / (8^2 - 1) + 0.491)
                                   = 0.5 * (19.61 / 63 + 0.491)
                                   = 0.5 * (0.31126984126984125 + 0.491)
                                   = 0.4011349206349206 s
R2 (IEC extremely inverse, TMS 0.3): t = 0.3 * 80.0 / (8^2 - 1)
                                   = 24 / 63
                                   = 0.38095238095238093 s
margin R1->R2 = 0.38095238095238093 - 0.4011349206349206 = -0.020182539682539... s
CTI = 0.2 s;  -0.0202 < 0.2  ->  NOT COORDINATED (margin below CTI)
```

The capture prints `0.4011`, `0.3810` and `-0.0202` with the verdict `NOT COORDINATED (margin below CTI)`; the backup
R2 is faster than its primary at this current, so the margin is negative. The second pair is definite time: R3 is a
fixed `1.5000 s` and `1.5 - 0.38095238095238093 = 1.1190 s`, above the 0.2 s CTI, so R2->R3 is `OK`. The overall exit
is 3. An extremely inverse backup behind a very inverse primary is a mismatch of curve shapes. With these settings the two
curves cross where `9.805/(M^2 - 1) + 0.2455 = 24/(M^2 - 1)`, at `M = 7.67`, a fault current of about 1534; above it
the extremely inverse R2 is the faster relay, so at 1600 the order is inverted. The fix is a larger dial on R2 or a
different family, which the user chooses because GridSim does not select settings.

The printed times are computed from the supplied settings; the four-decimal values are stable.

## Source map

| File | Type | Responsibility |
|---|---|---|
| `src/GridSim.Core/Protection/Coordination/OvercurrentCoordination.cs` | `OvercurrentCurve`, `OvercurrentElement`, `GradingResult`, `OvercurrentCoordination` | Curve constants, operating time, `Grade`, `GradeChain`, `ParseElement`, default CTI |
| `src/GridSim.Cli/Program.cs` (`RunGrade`) | CLI verb | `grade`: flag parsing, per-current tables, verdict, exit codes, result file |
| `src/GridSim.Cli/CliDefaults.cs` | `CliDefaults.GradeCtiS` | CLI default CTI, bound to the Core constant |
| `src/GridSim.Cli/Program.cs` (`RunProtectionCheck`) | CLI verb | `protection-check --cascade --alarms`: reads the log before the solve, prints the cross-reference, exit codes |
| `src/GridSim.Core/Protection/AlarmLogCsv.cs` | `AlarmLogCsv` | Strict reader for the alarm log CSV |
| `src/GridSim.Core/Protection/SequenceOfEvents.cs` | `AlarmEvent`, `SoeCrossReference`, `SequenceOfEvents` | Match predicted cascade steps against observed alarms |
| `src/GridSim.Core/Protection/ProtectionRelays.cs` | `TimeOvercurrentRelay` | The 51 element, which reads `IecConstants` |
| `src/GridSim.Core/Protection/ShortCircuit/Iec60909.cs` | `Iec60909.BranchFaultCurrents` | Per-branch fault current used as the grading input |
| `tests/GridSim.Tests/OvercurrentCoordinationTests.cs` | tests | Curves, definite time, grading, chains |
| `tests/GridSim.Tests/GradeAndSoeCliTests.cs` | tests | Spec parser, input checks, alarm CSV, SOE, CLI wiring |
| `tests/GridSim.Tests/S3ProtectionGroundingTests.cs` | tests | Default CTI, ABB constants |

## Limits

- A backup that does not pick up at the fault current is graded not coordinated, whatever the primary does; the grade does not say whether some other device further upstream would clear the fault.
- `GradeChain` grades one fault current for the whole chain; a meshed network needs `Grade` called per pair with each branch's own current from `Iec60909.BranchFaultCurrents`.
- Only the IEC and IEEE curves above and definite time exist; fuse and recloser curves, instantaneous (50) elements in a grade, and fuse-recloser coordination are not modelled.
- Relay reset time, breaker clearing time and overshoot are not modelled separately; the CTI is one number the user supplies.
- GridSim does not choose or optimise relay settings; every pickup, time dial and delay is an input.
- `--alarms` matches by branch and order only; it does not compare times, and non-branch alarms are counted but never matched.

## See also

- [Protection assessment (passive relay overlay)](<0017 Protection assessment (a passive relay overlay).md>) - the 50/51 elements, the cascade that `--alarms` is compared with.
- [Protections](<0015 Protections.md>) - the asset-level protection stages.
- [IEC 60909 short-circuit duty study](<0025 IEC 60909 short-circuit duty study.md>) - `BranchFaultCurrents`, the per-branch fault currents that feed the grading.
- [CLI reference: grade](<../manual/0019 CLI reference.md#grade>) - the verb's flags and exit codes.
- [Validation](<0011 Validation.md>) - the machine-epsilon standard the closed forms are held to.
