# GridSim 2.1.105 RC-5

> **BREAKING CHANGE RELEASE.** RC-5 as a whole is a BREAKING CHANGE release. Twenty-six changes since RC-4, B1 to B26
> in the table below, can change a result, an outcome or an exit code that RC-4 gave you. Read the table before you
> rely on a stored result, and check your own results with `gridsim breaking-change validate`.

| | |
|---|---|
| Version | 2.1.105 (release candidate RC-5) |
| Source | the commit tagged `v2.1.105-RC-5`; every binary reports `2.1.105+<commit>` from `--version` |
| Platforms | Windows x64, Linux x64 and macOS arm64 (as RC-4), built by the release workflow |
| Previous | RC-4 (2.1.073), with the RC-4-Fixes security release of 2.1.074 |

RC-5 gives every generating unit protection of its own, closes the RC-4-Fixes security advisories, and adds unbalanced
RMS faults, a wider dynamic-model library, SVC, STATCOM and HVDC dynamics, distance, directional, earth-fault and
differential protection, loss and reactive-power OPF with AC-SCOPF, a GB and RfG grid-code suite, Ward and REI
equivalents, more CGMES profiles with a SHACL checker, a scripting object model with a psspy-style Python layer, a
.NET plug-in route for user dynamic models, and a run journal that re-checks your earlier results on a new build. All
of it arrives as **new verbs, options, model types and event kinds**. Every command, flag, default and exit code that
RC-4 shipped behaves as before, except for the breaking changes B1 to B26 and the behaviour changes set out below.
Every build is Release and self-contained (it carries the .NET 10 runtime).

v2.1.097-RC-5 and v2.1.101-RC-5 were earlier RC-5 cuts on this branch. This re-cut, 2.1.105, supersedes both.

## Breaking changes since RC-4

Why every one of these is labelled BREAKING. GridSim is too large and too configurable for anyone to prove that a
change has no effect on every path a user can run. We can only measure the paths we look at, and people will run it in
ways the documentation does not cover. So any change to a numerical path is a BREAKING CHANGE, whatever the measured
cases show. The measurements tell you how big a change is; they never decide whether it is labelled.

| ID | What changed | Why (proof or source) | Magnitude | Who is affected | Keep the old behaviour |
|---|---|---|---|---|---|
| B1 (2.1.074) | Script expressions may nest at most 64 deep and 256 operations deep; script host limits are checked before every statement, block and loop iteration | Security: a deep expression could exhaust the parser's stack and end the process (GHSA-38hc-fggf-q864); limits could be run past inside blocks and loops (GHSA-2h96-qcc2-c9gh) | A script past the depth limits is a syntax error naming its line and column (`gridsim-script` exit 1); a web console script stops at its budget (200,000 statements, blocks and loop iterations, 4 M characters of output). Scripts within the limits run as before; `gridsim-script` and desktop console run records are byte-identical | Script hosts: `gridsim-script`, the desktop console, the web console | Not available (security fix) |
| B2 (2.1.075) | A Newton power flow stopped by its iteration cap while still converging is not retried from a DC-angle start; a cap of 1 or less is never retried | The retry judged a capped solve by a thousandfold mismatch cut that a quadratically converging solve needs about three steps to make, so a small cap looked like a stall | An outcome change, not a rounding difference: case9 from flat start at `MaxIterations = 2`, `Tolerance = 1e-4` is `MaxIterations`, where RC-4 reported it converged after four steps. Every solve that converges within its cap, and every stalled solve the retry is for, gives the same bits | Any Newton solve (dense or sparse) with a small iteration cap (`MaxIterations`, `--max-iter`) | Not available. A larger cap lets the solve converge within it |
| B3 (2.1.083) | With `--dyr-extended --allow-unmodelled`, a PSS/E DYR CSVGN1 record is modelled as an SVC; before, the unit was held at its power-flow Q | CSVGN1 joined the extended DYR library (technical chapter 24) | Same start; after a disturbance the trajectories differ by the action of the CSVGN1 control. No bound holds for every case, because it depends on the SVC's data and the disturbance | `stability`, `dynamics-study` and the library reading DYR with both options | Run without `--dyr-extended`: the record is skipped as before (and the other extended models are left out too) |
| B4 (2.1.095) | The storage time series counts a voltage-dependent load at the slack bus at the solved slack voltage | Bug fix: it used nominal Pd, where the solver and `SolvedGeneration` use the load at the solved voltage | Slack output + Pd x (multiplier at the slack voltage - 1): +8.16 MW (+8.16 %) for 100 MW constant-impedance at 1.04 pu. Constant-power loads and loads elsewhere: same bits | `chain --bess`, `StorageTimeSeries.Run` | Not available (bug fix) |
| B5 (2.1.098) | PSS/E RAW two-winding R and X are referred through the winding-2 ratio, Z x w2^2, for every CW and CZ and for an impedance table's base | PSS/E PAG eq. 4.21b | R and X +10.25 % at WINDV2 = 1.05 pu, -9.75 % at 0.95 pu; w2 = 1 and three-winding units: same bits | Every verb reading `.raw` or `.rawx` with such a unit | Not available (bug fix) |
| B6 (2.1.098) | DGS two-winding impedance is referred to the LV terminal base, x (utrn_l / uknom)^2 | Bug fix: DGS gives the impedance on the winding's rated voltage; the bus base is the terminal's nominal voltage | +9.30 % in R and X for an 11.5 kV winding on an 11 kV terminal; a winding rated at its terminal voltage: same bits | `dgs-import`, `.dgs` inputs | Not available (bug fix) |
| B7 (2.1.098) | Battery cell reversible heat is -I T dU/dT with discharge-positive current | Bernardi, Pawlikowski and Newman 1985 | The reversible term changes sign: an LFP cell (dU/dT = -0.05 mV/K) at 100 A and 25 C heats by 1.49 W where it cooled by 1.49 W; cell temperatures follow. dU/dT = 0: same bits | The battery cell and pack thermal model (library and apps; no CLI verb) | Not available (bug fix) |
| B8 (2.1.098) | CGMES node-breaker import applies a transformer's rated voltages against its nodes' base voltages | The rule the bus-branch import already uses | 120 / 10.5 kV on 110 / 10 kV nodes: ratio +3.90 %, R and X +10.25 %; a unit rated at its node voltages (what GridSim's export writes): same bits | `cgmes-study`, `cgmes-conform`, node-breaker CGMES imports | Not available (bug fix) |
| B9 (2.1.098) | `g74` with an `iec60909-q` motor gives the breaking current mu q Ik'', with Ik'' at time 0 | IEC 60909-0 | Motor part x mu: -29 % at Ik''/IrM = 6.6 and 0.1 s. A time between 0 and 0.02 s with such a motor and decay on is refused (exit 1), because IEC gives no factor there | `g74` with `iec60909-q` motors and decay on | Not available (bug fix) |
| B10 (2.1.098) | The negative-sequence network of an RMS sequence fault carries each dynamic load by its Norton admittance | The positive sequence already carries it; an induction motor's negative-sequence admittance is its locked-rotor admittance | Z2 at the fault: 0.0625 pu where it was 0.091 to 0.100 pu (45 to 59 % high) for a 100 MW motor beside a j0.1 pu source; LL and earth-fault currents follow. No dynamic loads, or three-phase faults: same bits | `stability --rms --events`, `dynamics-study` unbalanced faults with dynamic loads | Not available (bug fix) |
| B11 (2.1.098) | Two unbalanced sequence faults in place at once are refused; a bolted or three-phase fault beside an unbalanced one is in its negative- and zero-sequence networks | One equivalent shunt per fault cannot represent a cross-country fault; the balanced fault's shunt in Z2 and Z0 is exact | Such event lists were accepted; now refused (exit 1), or Z0 = j0.35 pu for an earth fault one j0.1 pu line from a three-phase fault through j0.05 pu, where it drew nothing. One fault at a time: same bits | RMS event studies with overlapping faults | Not available (bug fix) |
| B12 (2.1.099) | `acscopf` screens every secured outage again at the final dispatch; a failing one ends `SecuredOutageFailsScreen`, exit 3 | The outage block lets unit voltages move after the outage, the screen holds them, so a secured outage could still fail | case9 at RateA x 0.75: `SecuredOutageFailsScreen` [7, 8], exit 3, where it was `Optimal`, exit 0 (outage 7: branch 5-6 at 115.35 MVA against 112.5; outage 8: bus 4 at 1.1021 pu). Runs whose outages pass: same bits | `acscopf` | Not available (bug fix) |
| B13 (2.1.099) | `acopf-opt` tap shadow prices (`MuMin`, `MuMax`) are per unit of the ratio tau, not of u = 1/tau | Bug fix: the tap's value and limits are in tau, so its prices must be too; du/dtau = -1/tau^2 | x 1/tau^2: +23.5 % at tau = 0.9, -17.4 % at 1.1. Shunt prices and every other number: same bits | `OpfControlValue` from the library (the CLI and report files do not print them) | Multiply a tap price by tau^2 at its bound |
| B14 (2.1.099) | The AC-OPF interior point method ends as converged at the barrier floor when every other tolerance is met | Below machine epsilon the barrier cannot move; the point already meets the feasibility, gradient and cost tolerances | A numerical failure becomes converged: case9 losses at complementarity tolerance 1e-15 converge at iteration 18, 2.3157971721728 MW (SciPy 2.3157971721609). Runs that converged before: same bits | `acopf-opt`, `acscopf` with a complementarity tolerance below what the barrier can reach | Not available (bug fix) |
| B15 (2.1.099) | `acopf-opt` and `acscopf` refuse a tap control on a transformer with an impedance correction table | The OPF holds the impedance at its case value, so the optimum was for the wrong impedance | Refused (exit 1) where an optimum was returned | Tap controls on such transformers | Not available; leave that tap out of the controls |
| B16 (2.1.099) | QSTS self-consumption units on one bus share its net demand in proportion to their ratings | Bug fix: each unit asked for the whole net demand | 1 MW of demand and two 1 MW units: 0.5 MW each and the bus draws 0, where each gave 1 MW and the bus exported 1 MW. A lone unit: same bits | `qsts`, `hosting` with two or more such units on one bus | Not available (bug fix) |
| B17 (2.1.099) | VSC1's DC voltage converter is driven by the power the power converter actually exchanges after its current limit | Energy balance on the DC side | 100 MW order, bus at 0.1 pu, Imax 1.1 pu: 10.761 MW at the voltage converter where it gave 99.248 MW. Current never clipped: same bits | RMS runs and `dynamics-study` with VSC1 | Not available (bug fix) |
| B18 (2.1.099) | The adaptive-step RMS run rejects a step with a NaN in any state | Bug fix: the error norm's running maximum forgot a NaN followed by a finite value | Such a run stops with the minimum-step error "(non-finite state)" where it was reported as loss of synchronism. Finite runs: same bits | Adaptive rows of `dynamics-study` and the library | Not available (bug fix) |
| B19 (2.1.099) | REEC_A, REEC_B and REEC_C link the non-windup of s2 and s3 | WECC model description (as REEC_D already did) | VFlag = QFlag = 1 with a sustained Q error: Vsel holds near 0.975 pu where it wound up to 1.1 pu. s3 never at its limit: same bits | RMS runs with these models (`.dyr`) | Not available (bug fix) |
| B20 (2.1.099) | REEC_A, REEC_B and REEC_C with Tpord = 0 bypass the power order's freeze during a dip; Pmax and Pmin still apply | WECC model description | Pref 0.8 to 0.6 pu in a dip: Pord 0.6 pu where it stayed at 0.8. Tpord above 0: same bits | RMS runs with these models and Tpord = 0 | Not available (bug fix) |
| B21 (2.1.100) | PSS/E VSC DCSET (TYPE 2): a positive DCSET feeds power into the AC network | PSS/E data format (two public copies) | The link reverses: DCSET = +100 at bus A injects 100 MW there where it drew 100 MW, a swing of 200 MW plus twice the losses. RAW files GridSim writes and reads back, and links built through the API: same bits | Every verb reading `.raw` or `.rawx` with a TYPE 2 VSC converter | Negate DCSET in the file |
| B22 (2.1.100) | DGS `ElmShnt` capacitors are Bs = +qcapn | YBus stamps +jBs, so a capacitor has positive Bs (the MATPOWER convention, and the CGMES reader's) | 2 x qcapn x V^2 MVAr at the bus: a 12 MVAr capacitor injects 12 MVAr at 1 pu where it absorbed 12 | `dgs-import`, `.dgs` inputs | Negate qcapn in the file |
| B23 (2.1.100) | REEC_C at or below SOCmin stops discharge only; Ipmin keeps -Ipmax | EPRI 3002027129, Fig. 4-7 | Imax 1.2 pu: Ipmin -1.2 pu where it was 0; a -0.5 pu charging order gives -0.5 pu where it gave 0 | RMS runs with REEC_C storage at SOCmin | Not available (bug fix) |
| B24 (2.1.100) | Small-signal analysis with inverters: the RoCoF a grid-forming unit's synthetic inertia reads includes the inverters' own power | Finite-difference check of the A matrix: the column was 0.03125 where the code gave 0 | One machine with a grid-forming unit (tau = 0.05 s, x = 1): actuator pole -40 where it was -20; data/gb-synthetic has no right-half-plane eigenvalue and exits 0, where it had two and exited 1. No synthetic inertia: same bits | `modal --enable-inverters` | Not available (bug fix) |
| B25 (2.1.102) | The losses and reactive-power objectives of `acopf-opt` and `acscopf` converge when a post-outage block leaves a direction free | Only the barrier held that direction, and each interior point step closes a tenth of the gap, so the residual fell by 0.81 per iteration and the barrier reached machine epsilon first; a Newton projection onto the power balance now finishes it | A numerical failure (feasibility 7e-9, a 7e-7 pu mismatch) becomes converged (5e-16) on the two-bus cable case, the block moving 1.4e-7 pu; base loss 0.25292247083168 MW against SciPy's 0.25292247083157. Runs that converged before, and the cost objective: same bits | `acopf-opt`, `acscopf` with `losses` or `rpo` | Not available (bug fix) |
| B26 (2.1.103) | Adaptive-step RMS rotor angles get a fixed error scale, atol + rtol x 1 rad, in place of atol + rtol x \|delta\| | Absolute rotor angles drift off frequency (about 94 rad after 60 s at 0.5 %), so the old scale loosened about 93 times and depended on the angle reference | Against 2.1.102 on case14, case39 and case118: rotor angles at most 4.4e-6, 3.5e-6 and 1.9e-4 degrees; frequency 1.2e-7, 1.1e-7 and 5.4e-6 Hz; power 1.4e-5, 5.8e-5 and 1.9e-3 MW. Angles agree to 24.0, 21.4 and 16.5 of 53 bits over the run, and 37.0, 33.9 and 30.2 bits at 120 s. No stability verdict or critical clearing time bracket changed | Every adaptive (variable-step) RMS row; fixed-step rows give the same bits | Not available |

**How to check your own results.** If you kept `--json` results from RC-4 or an earlier build,
`gridsim breaking-change import <folder>` turns them into journal entries, hashing their inputs as they are now; from
this release on, `--journal <file.jsonl>` records every run as you make it. `gridsim breaking-change validate` then
re-runs each entry on this build in a scratch folder and compares every output with the recorded one, bit for bit.
Each entry comes back IDENTICAL; CHANGED, with every field that moved, by how much and in how many bits; CANNOT-REPLAY,
with the reason; or SKIPPED-SAME-BUILD. A CHANGED field is EXPLAINED (Bn) when it lies within what breaking change Bn
claims, AMBIGUOUS when the claims of several changes on it cannot be combined, EXCEEDS-CLAIM when it moved more than
the change claims, and UNEXPLAINED when no breaking change covers it. EXCEEDS-CLAIM and UNEXPLAINED mean "tell us":
send the report to the address at the end of this note. The expected exceptions are B3 and B27, which have no bound.
They appear only for a job run with both `--dyr-extended` and `--allow-unmodelled` whose DYR file holds one of their
records (CSVGN1 for B3; EXDC2, IEEEX1, ESAC8B, ESST3A or IEEEG3 for B27). Such a job reads UNBOUNDED once, with every
field that moved and by how much, and validate exits 4: review needed, not a bug. When you have looked and are
content, run `gridsim breaking-change validate --accept B3` (or `B27`). The job then reads REVIEWED and validate exits
0; the acknowledgement is kept beside the journal, and a new job or any larger movement is flagged again.
`gridsim breaking-change list` prints every change with its scope and claims.

Dynamics jobs are re-run too: `stability` (its `--events-out` RMS event export or its `--export` trajectory),
`dynamics-study`, `long-term` (with `--out`), `trip` and `modal` (with `--export`). The journal hashes the DYR or dynamics
file, the study file and every file it names (RAW case, DYR, unit protection, devices, curves), the model assignments
and the user model files they name, and keeps them with `--journal-snapshot`. A file that is gone or changed with no
copy makes the job CANNOT-REPLAY and names the file. A job with model plug-ins is re-run only when every assembly still
has its pinned SHA-256; otherwise it is CANNOT-REPLAY, names the assembly and loads nothing. So validate checks B3, B10,
B11, B19, B20, B23 and B27 on your own stability and dynamics-study jobs. Switch the journal on before you upgrade, and
keep the result files: a run that only printed its result cannot be re-run.

Each breaking change is set out in more detail below.

### BREAKING CHANGE (B2): Newton retry and the iteration cap

**BREAKING CHANGE:** a Newton power flow (dense or sparse) stopped by `MaxIterations` while it is still converging is
no longer retried from a DC-angle start, so its reported outcome changes from converged to `MaxIterations`. This
changed in 2.1.075.

What changed. RC-4 retried any failed solve whose final mismatch was above 1e-3 of its starting mismatch. A
quadratically converging solve needs about three steps to make that thousandfold cut, so a small cap looked like a
stall and the retry spent a second iteration budget. RC-5 judges a capped solve by its last step as well:

- a cap of 1 or less is never retried (one step is no evidence of a stall, and a cap-1 solve is usually a warm start
  such as a state estimate);
- a solve of two or more steps whose last step at least halved its mismatch is still converging and is reported as it
  ended, status `MaxIterations`.

Magnitude. This is an outcome change on capped solves, not a rounding difference. case9 from flat start at
`MaxIterations = 2` and `Tolerance = 1e-4` is now `MaxIterations`; RC-4 reported it converged after four Newton steps.
Every solve that converges within its cap gives the same bits as RC-4, and every stalled solve the retry exists for
(case13659pegase, pglib case162_ieee_dtc, case240_pserc, case3_lmbd) is retried exactly as before.
`PowerFlowOptions.RetryFromDcStart = false` still turns the retry off.

### BREAKING CHANGE (B3): CSVGN1 under `--dyr-extended --allow-unmodelled`

**BREAKING CHANGE:** with both `--dyr-extended` and `--allow-unmodelled`, a PSS/E DYR CSVGN1 record is now modelled as
a static var compensator. Before 2.1.083 it was skipped, and the unit, with no model and no inertia, was held at its
power-flow reactive power as a constant.

Magnitude. The start of every run is the same. After a disturbance the unit follows the CSVGN1 control, so the
trajectories differ by the action of that control. This is a modelling change on an opt-in path, not a rounding
difference. Runs without both options are unchanged bit for bit, and `--dyr-extended` stays off by default.

### BREAKING CHANGE (B4): storage slack output (bug fix)

**BREAKING CHANGE (bug fix):** the storage time series (`chain --bess`, `StorageTimeSeries.Run`) now counts the slack
bus's own load at the solved slack voltage, by the same rule as the solver and `SolvedGeneration`. Before, a
voltage-dependent (ZIP) load at the slack bus was counted at its nominal Pd, so each step's slack output was understated.

Magnitude. The slack output rises by Pd x (load multiplier at the slack voltage - 1). For a constant-impedance 100 MW
load at a slack held at 1.04 pu it rises by 8.16 MW (100 x 1.04^2 = 108.16 MW in place of 100 MW). Constant-power
loads, and loads at any other bus, give the same bits as before.

### BREAKING CHANGEs (B5 to B25): bug fixes from the core review

**BREAKING CHANGE (bug fix):** a review of the numerical core found the errors below, fixed in 2.1.098 to 2.1.100 and
2.1.102. Each one changes results for the inputs it names, and none of these changes is at rounding level. Inputs
outside each fix give the same bits as before. The table above gives each one's ID.

Interchange

- PSS/E RAW two-winding transformers refer R and X through the winding-2 ratio, Z x w2^2 (PSS/E PAG eq. 4.21b), for
  every CW and CZ and for the base of an impedance correction table. R and X rise by 10.25 % at WINDV2 = 1.05 pu and
  fall by 9.75 % at 0.95 pu. Units with w2 = 1, and three-winding units, are unchanged.
- DGS transformers refer their impedance to the LV terminal base, x (utrn_l / uknom)^2: +9.30 % in R and X for an
  11.5 kV winding on an 11 kV terminal. A winding rated at its terminal voltage is unchanged.
- DGS capacitors (`ElmShnt`, `qcapn` MVAr) are Bs = +qcapn, a capacitor. They were read as reactors: a 12 MVAr
  capacitor now injects 12 MVAr at 1 pu where it absorbed 12, a swing of 2 x qcapn x V^2 MVAr.
- CGMES node-breaker import applies a transformer's rated voltages against its nodes' base voltages, as the bus-branch
  import does. For a 120 / 10.5 kV unit on 110 / 10 kV nodes the ratio rises by 3.90 % and R and X by 10.25 %. A unit
  rated at its node voltages, which is what GridSim's own export writes, is unchanged.
- PSS/E VSC DCSET (TYPE 2 converter): a positive DCSET feeds power into the AC network, as in PSS/E. This reverses
  the direction of every VSC link read from a RAW file with a TYPE 2 converter: DCSET = +100 at bus A now injects
  100 MW there, where it drew 100 MW, a swing of 200 MW plus twice the losses at each end. A RAW file GridSim writes
  and reads back keeps its link, and links built through the API are unchanged.

Short circuit and faults

- `g74` with an `iec60909-q` motor gives the IEC 60909-0 breaking current mu q Ik'', with Ik'' at time 0. The motor
  part falls by the factor mu (0.707, so -29 %, at Ik''/IrM = 6.6 and 0.1 s). A time between 0 and 0.02 s with such a
  motor and decay on is refused (exit 1), because IEC gives no factor there.
- RMS sequence faults carry each dynamic load in the negative-sequence network by its Norton admittance. With a
  100 MW motor beside a j0.1 pu source, Z2 at the fault is 0.0625 pu where it was 0.091 to 0.100 pu (45 to 59 % high),
  and line-to-line and earth-fault currents change with it. Studies without dynamic loads, and three-phase faults,
  are unchanged.
- Two unbalanced sequence faults in place at the same time are refused, naming the first. A bolted or three-phase
  fault beside an unbalanced fault is now in that fault's negative- and zero-sequence networks, which is exact: an
  earth fault one j0.1 pu line from a three-phase fault through j0.05 pu sees Z0 = j0.35 pu, where it drew nothing.
  One fault at a time is unchanged.

Dynamics

- VSC1 balances its DC side: the voltage converter is driven by the power the power converter actually exchanges after
  its current limit. A 100 MW order with the power converter's bus at 0.1 pu and Imax 1.1 pu gives 10.761 MW at the
  voltage converter, where it gave 99.248 MW. Runs where the current is never clipped are unchanged.
- The adaptive-step RMS run rejects a step with a NaN in any state and stops with the minimum-step error
  "(non-finite state)", where the step could be accepted and the run reported as loss of synchronism. Finite runs are
  unchanged.
- REEC_A, REEC_B and REEC_C link the non-windup of s2 and s3, as the WECC description says. With VFlag = QFlag = 1 and
  a sustained Q error, Vsel holds near 0.975 pu where s3 reaches Iqmax, instead of winding up to 1.1 pu.
- REEC_A, REEC_B and REEC_C with Tpord = 0 bypass the power-order freeze during a voltage dip; Pmax and Pmin still
  apply. With Pref stepped from 0.8 to 0.6 pu in a dip, Pord is 0.6 pu where it stayed at 0.8. Tpord above 0 is
  unchanged.
- REEC_C at or below SOCmin stops discharge only (EPRI 3002027129): Ipmin stays at -Ipmax, so the store can charge.
  With Imax = 1.2 pu, Ipmin is -1.2 pu where it was 0, and a -0.5 pu charging order gives -0.5 pu where it gave 0.
- Small-signal analysis with inverters (`modal --enable-inverters`): the RoCoF a grid-forming unit's synthetic inertia
  reads now includes the inverters' own power. One machine with a grid-forming unit (tau = 0.05 s, x = 1) has its
  actuator pole at -40 where it was -20; data/gb-synthetic now has no right-half-plane eigenvalue and exits 0, where
  it had two and exited 1. Units with no synthetic inertia are unchanged.
- Battery cell reversible heat is -I T dU/dT with discharge-positive current (Bernardi, Pawlikowski and Newman 1985);
  the sign was reversed. An LFP cell at 100 A and 25 C now heats by 1.49 W on discharge where it was cooled by 1.49 W.

Optimisation and time series

- `acscopf` screens every secured outage again at the final dispatch. If one still fails, the run ends with the new
  status `SecuredOutageFailsScreen`, lists the outages in `UnresolvedSecuredOutages`, prints no optimum and exits 3.
  case9 with every RateA x 0.75 was reported optimal; outage 7 leaves branch 5-6 at 115.35 MVA against 112.5 and
  outage 8 puts bus 4 at 1.1021 pu, so it now ends `SecuredOutageFailsScreen` [7, 8]. Runs whose outages pass are
  unchanged.
- `acopf-opt` tap shadow prices (`MuMin`, `MuMax`) are per unit of the ratio tau, not of 1/tau: x 1/tau^2, so +23.5 %
  at tau = 0.9 and -17.4 % at 1.1. API only; the CLI and report files do not print them.
- The AC-OPF interior point method ends as converged at the barrier floor when every other tolerance is already met,
  where it reported a numerical failure: case9 losses with a complementarity tolerance of 1e-15 now converge at
  iteration 18 (2.3157971721728 MW; SciPy 2.3157971721609). Runs that converged before are unchanged.
- `acopf-opt` and `acscopf` refuse a tap control on a transformer with an impedance correction table, naming it,
  where they returned an optimum for the wrong impedance.
- QSTS self-consumption storage: units on one bus share its net demand by rating. With 1 MW of demand and two 1 MW
  units, each gives 0.5 MW and the bus draws 0, where each gave 1 MW and the bus exported 1 MW. A lone unit is
  unchanged.
- The losses and reactive-power objectives of `acopf-opt` and `acscopf` converge when a post-outage block leaves a
  direction of its state free, such as a slack unit's voltage after the outage. When the barrier reaches its floor
  with only feasibility unmet, the point is projected onto the power-balance equations by Newton steps and held to the
  same 1e-12 tolerances; if that fails, the run still ends as a numerical failure and says why. On the two-bus cable
  case the run went from a numerical failure to converged, with a base loss of 0.25292247083168 MW against SciPy's
  0.25292247083157 MW. Runs that converged before, and the cost objective, are unchanged.

### BREAKING CHANGE (B26): adaptive-step rotor-angle error scale

**BREAKING CHANGE:** the variable-step (adaptive) RMS integration gives rotor angles a fixed error scale,
`atol + rtol * 1 rad`, in place of `atol + rtol * |delta|`. Every adaptive step sequence can change.

Why. Rotor angles are integrated as absolute angles, and they drift while the frequency is off nominal: about 94 rad
after 60 s at a 0.5 % offset. A scale that grew with the angle was about 93 times looser there than at 1 rad, and
depended on where the network's angle reference sat. With the fixed scale, shifting every angle by 2 pi k gives the
same steps, bit for bit.

Magnitude, against 2.1.102 on case14, case39 and case118 (GENROU machines, a load step to a 0.5 % offset, 120 s,
default tolerances): rotor angles move by at most 4.4e-6, 3.5e-6 and 1.9e-4 degrees, frequency by at most 1.2e-7,
1.1e-7 and 5.4e-6 Hz, and electrical power by at most 1.4e-5, 5.8e-5 and 1.9e-3 MW. Over the run the angles agree to
24.0, 21.4 and 16.5 of 53 bits, and at 120 s to 37.0, 33.9 and 30.2 bits. The 2.1.102 error of the same runs against
a converged reference is larger, up to 1.2e-3 degrees on case118. No stability verdict and no critical clearing time
bracket changed. Fixed-step rows and every non-adaptive result give the same bits.

### Behaviour RC-4 users will see

These are not changes to the numerical contract, but an input RC-4 accepted can now be refused, stopped or gated. The
two script items are labelled B1 in the table above:

- Script expressions: an expression or condition may nest at most 64 deep (brackets, minus signs, `NOT`, function
  arguments) and at most 256 operations deep. RC-4 accepted any depth, and a deep enough one ended the process; now it
  is a syntax error naming its line and column, and the script does not run. Every script within those limits parses
  and runs exactly as before.
- Script host limits: a host's cancellation and limits are now checked before every statement, block and loop
  iteration, where RC-4 checked them only before simple statements, so a loop or block could run past them. The web
  console sets limits (200,000 statements, blocks and loop iterations and 4 M characters of output per session, an open
  interactive block of at most 64 KB) and a script that reaches one now stops there with the reason.
  `gridsim-script` and the desktop console set none: their scripts run as before and their run records are
  byte-identical. Both apply the licence bus limit to a loaded model, as the command line does.
- Files: case and study files over 256 MiB are refused before they are read; CGMES archives expanding to more than 2 GB
  are refused.
- `gridsim rpc --listen`: a line over the limit, or one that is not a JSON object, ends the connection after its error;
  network and device paths are refused in `load` and `run-verb`. The standard input transport is unchanged.
- Web: a dashboard live session needs Operator, a workbench study Analyst; a request relayed by a reverse proxy is an
  anonymous Viewer; each client gets 600 requests a minute on `/api` and `/console` (429 with `Retry-After`); the web
  console refuses `SELECT ENGINE ... SCHEMAS`.

Coming from RC-3: the two RC-4 BREAKING CHANGEs still apply (minimum-degree ordering and the 0.01 pu voltage floor, at
most 9.7e-13 pu in voltage magnitude on 66 MATPOWER cases; and the automatic Newton retry). RC4.md sets them out.

## What is new in RC-5

| Job | Command or surface | Scope tested for this release candidate |
|---|---|---|
| Per-unit protection | `unitProtection` template kind, `protectionTemplate`, `unit` overrides, `protection-check`, `trip` | 27/59, 32, 40, 81U/81O, 81R and 78 on every generating unit, with provenance on every value; ENA RoCoF and EREC G99 interface presets; cases without it unchanged |
| Unit relays in RMS | `dynamics-study --unit-protection`, `gridsim-unit-protection/1`, library entry points | Variable-step and full-model engines; 40 by offset mho zones on the full model; 27/59 and 40 at the machine terminal behind a unit transformer; off by default, byte-identical without it |
| Unbalanced RMS faults | `stability --rms --events` (`sfault`, `lfault`, `lclear`), `dynamics-study` fault events, script `FAULT BUS` | 3ph, SLG, LL, LLG through R + jX on a bus or along a line; matches the static fault studies at the fault instant |
| Dynamic-model library | `gridsim-dynamics/2`, DYR with `--dyr-extended` | GENCLS, GENTPJ, ESST1A, EXST1, SCRX, ESAC1A, ESDC1A, IEEEST, PSS2A, PSS2B, GGOV1, WSIEG1; induction motor, ZIP with recovery, CMLD subset; step responses against SciPy |
| SVC, STATCOM and HVDC | `MachineData.Facts`, `RmsLoadedMachineData` devices, `rms-devices.csv`, DYR CSVGN1 and CDC4T | CSVGN1, STATCOM1, LCC1 and VSC1 against analytical steady states and closed-form lag responses; opt-in |
| Placed protection | `protection-eval`, `protection-grade` | 50/51, 50N/51N, 67/67N/67Q, 21 mho and quadrilateral, dual-slope 87L, coordination checks; hand-derived cases |
| Optimisation | `acopf-opt`, `acscopf`, `reconfig` | Cost, losses and reactive-power objectives with taps and shunts; preventive and corrective AC-SCOPF; case33bw to its published optimum (139.55 kW) |
| Grid code | `grid-code` | GB ECC Issue 6 Revision 33 and EU 2016/631: FRT, retained voltage, recovery, reactive capability, LFSM-O/U, FSM, RoCoF, frequency relays; study evidence, not a certificate |
| Network reduction | `reduce` | Ward, extended Ward and REI; case14 against an independent NumPy implementation to 1e-12 pu |
| CGMES | `cgmes-export --profiles dl,gl,sc,dy`, `cgmes-shacl` | DL, GL, SC and DY in CGMES 2.4.15, round-tripped to the bits; native SHACL Core subset with three-valued verdicts, unsupported parts reported not run |
| Interchange | PSS/E RAW 36 and RAWX, SEQ 33 to 36, COMTRADE | Fixtures written from the published format descriptions; RAW 36 and RAWX read to their revision 35 model bit for bit |
| Scripting and API | `GridSim.Core.Api`, `rpc --protocol 2`, Python `gridsim.ModelSession` and `gridsim.psspy`, `GRAMMAR 1 WITH STUDIES` | Edit, solve and undo bit for bit against the CLI; level 1 rpc byte-identical; a listed psspy subset, each call with its public source |
| Plug-in dynamic models | `gridsim-plugins/1`, `dynamics-study --model-assignments --model-plugins`, `user-model check-plugin`, declarative `injection`, `load` and `expr` | Sample plug-in TGOV1 and SEXS and an `expr` TGOV1 bit for bit against the coded models at three step sizes; closed-form injection and load cases to 1e-12 pu |
| Study output formats | `--json`, `--csv` | The eight new study verbs write through the shared result writer; existing writer files unchanged byte for byte |
| Re-checking your results | `--journal`, `breaking-change import`, `breaking-change validate`, `breaking-change list` | A run is byte for byte the same with the journal on or off; the validate report is the same for every `--parallel`; two runs of every writer verb on one build compare IDENTICAL |

Each new verb reads a strict, versioned study file where it takes one, writes its result only to a new file, and
reports what it could not establish as incomplete or not demonstrated, never as a pass. Full details are in the CLI
reference (`docs/manual/0019 CLI reference.md`), technical chapters 24, 26, 29, 32, 39, 40 and 41, and CHANGELOG.md,
versions 2.1.074 to 2.1.105.

### Per-asset protection

A generating unit can carry its own under/over-voltage (27/59), reverse power (32), loss of excitation (40),
under/over-frequency (81U/81O), RoCoF (81R) and vector shift (78) relays. In a JSON v2 case the class default is a
`unitProtection` template; a generator takes it through `protectionTemplate` or a binding rule, and its own values go
on top through a `unit` override. Every value keeps its provenance, and resolution never invents a relay. Presets give
the ENA settings with their sources: `rocofPreset` `legacy` (K2 x 0.125 Hz/s, 0.5 s, EREC G59), `legacy-high-impedance`
(0.2 Hz/s) and `modern` (1 Hz/s, 0.5 s), and `interfacePreset` `g99-hv` and `g99-lv` (EREC G99 Issue 2 Table 10.1).

`protection-check` and the protection assessment judge each unit against its own settings and name the unit, the stage
and where each setting came from; `trip` lists the units' frequency relays on its trajectory. In the RMS loop the
variable-step engine runs 81U/81O, 81R, 27/59, 32 and 78; the full-model engine adds 40 against two offset mho zones,
so a field failure trips its unit and a normal fault-and-clear does not. Behind a folded unit transformer, 27/59 and 40
read the machine terminal, V_t = V_hv + j X_t I. A RAW-bound `dynamics-study` takes the same settings from a
`gridsim-unit-protection/1` file keyed by PSS/E machine `<bus>:<id>`.

Scope: steady-state and both RMS engines; 40 is not evaluated on the variable-step engine, which has no field model.

### Security

- Script parser depth: nesting is capped at 64 and 256 operations, so a script can no longer exhaust the parser's
  stack and end the process (GHSA-38hc-fggf-q864).
- Script host limits are enforced inside blocks and loops, with new per-session budgets for statements, blocks, loop
  iterations and output (GHSA-2h96-qcc2-c9gh).
- `gridsim-script` and the desktop console apply the licence bus limit as the command line does;
  `gridsim-script --confine` refuses paths outside the base folder and network, device and linked paths.
- Web: role gates on the dashboard live session and workbench studies, matching `/ws/sim` and `POST /api/solve`; the
  loopback anonymous default is granted per request and never through a proxy; a per-client request limit
  (`GridSim:MaxRequestsPerClientPerMinute`); a host's own audit log with every denial and bridge reconnect recorded;
  opt-in `Auth:RequireKeyProtection` and `GridSim:ScriptCsp`.
- Inputs: the 256 MiB file cap, the 2 GB CGMES expansion cap, the PSS/E multi-terminal DC count guard and the
  `rpc --listen` line limit and path refusals.
- `rpc --protocol 2` over `--listen`: `save` checks every file it writes, the sibling `.network.json` included, against
  the working folder, refuses a link at any target name and creates files new. A case handle carries a token of the
  host that issued it, so a handle from another host, or from before a restart, is refused and releases nothing. Both
  plug-in routes are refused over `--listen`.
- Model plug-ins: the determinism lint's lists are private and immutable, a plug-in may reach GridSim.Core only in
  `GridSim.Core.Dynamics.Models`, `GridSim.Core.Dynamics.Plugins` and `GridSim.Core.Numerics` (the loader's own types
  excepted), and it may not write or take the address of another assembly's static field.
- Container: `compose.yml` runs the web container read-only with no Linux capabilities and `no-new-privileges`.
- Compliance: the role-based access-control control (`rbac`, CAF B2) is recorded Achieved.

Scope: each fix has its own tests, including the parser limits on a small stack, the rpc TCP transport, two hosts
refusing each other's handles, and hostile plug-ins refused before anything loads.

### Dynamics

- Unbalanced and impedance RMS faults on a bus (`RmsSequenceFault`) or along a line (`RmsBranchSequenceFault`,
  cleared by `RmsBranchFaultClear`), by the positive-sequence method of PSS/E and PowerFactory RMS, with the sequence and
  phase quantities at the fault point and a critical clearing time search. A bolted three-phase fault is the RC-4 bus
  fault bit for bit.
- New models: GENCLS and GENTPJ machines; ESST1A, EXST1, SCRX and ESDC1A exciters; IEEEST, PSS2A and PSS2B stabilisers,
  usable with any exciter; GGOV1 and WSIEG1 governors; a third-order induction motor (CIM5, CIMW single cage), a ZIP
  load with exponential recovery and a WECC CMLD subset. Limits follow IEEE Std 421.5.
- PSS/E DYR maps the new models in their published record order under `--dyr-extended`; without it the reader maps
  exactly what it did before.

Scope: flat starts hold to rounding, step responses agree with a SciPy reference integration, and the three SIMD modes
give the same bits; the fault method is balanced, with no negative-sequence braking torque or per-phase detail.

### Protection

`protection-eval` measures every device in a `gridsim-devices/1` file on one fault at a bus or along a line: 50/51,
50N/51N on residual current, 67 with quadrature, zero- or negative-sequence polarisation, 21 mho and quadrilateral on
phase and earth loops, and dual-slope 87L. `protection-grade` runs the coordination checks over every bus: grading
margins, zone 1 reach and coverage, zone 2 cover and earth-fault sensitivity, listing every check it could not make.
The device file schema stays `gridsim-devices/1`; existing files and the RC-4 protection verbs are unchanged.

Scope: hand-derived cases for every element and grading pair.

### HVDC and FACTS

RMS models for CSVGN1 (PSS/E SVC), STATCOM1, LCC1 (a quasi-steady two-terminal link after CDC4T, with VDCOL, blocking
and recovery ramps) and VSC1 (P and Q control, optional AC voltage control, current limit with priority). Each starts on
its power-flow state; device states are written to `rms-devices.csv`, and `dynamics-study` takes a `devices` list. DYR
maps CSVGN1 and CDC4T under `--dyr-extended`.

Scope: analytical steady states, limits and closed-form lag responses; with no device data every number is unchanged.

### Optimisation

`acopf-opt` adds loss and reactive-power objectives to the full AC OPF, with transformer taps and bus shunts as controls
rounded to their step grid. `acscopf` secures branch outages preventively or correctively, entering them by constraint
generation with an AC Newton screen. `reconfig` reduces feeder losses by branch exchange and reaches the published
case33bw optimum. With no controls and the cost objective, `acopf-opt` gives the same bits as `acopf-full`.

Scope: SciPy loss optima on case9 and case30, hand-derived two-bus cases and the case33bw sequence.

### Grid code

`grid-code` checks one unit against the GB Grid Code European Connection Conditions (Issue 6 Revision 33) or
Regulation (EU) 2016/631: FRT envelope, retained voltage and recovery, undervoltage settings, reactive capability,
LFSM-O, LFSM-U, FSM, RoCoF settings and events, and frequency relays. Each result gives the clause, value, limit, signed
margin and verdict. Requirement data ships as embedded files, every value with its clause, and RfG choices are refused
outside the Regulation's ranges. A check passes only when the study demonstrates it: an FRT test passes only on the
line, under the ECC.6.3.15.8 pre-fault conditions, and a unit whose relays the run cannot evaluate is reported not
evaluated.

Scope: equal-area single-machine cases, closed-form governor and RoCoF responses and two-bus reactive capability; the
verdicts are study evidence, not a compliance certificate.

### Network reduction

`reduce` keeps the named buses and replaces the rest with Ward, extended Ward or REI boundary elements, prints a
base-case check against the full solve, and writes the reduced case as MATPOWER or JSON v2.

Scope: case14 against an independent NumPy implementation; the reduced case from a flat start lies within 1e-12 pu of
the full voltages; the REI zero-power-balance node voltage within a rounding bound counted from the operations.

### CGMES

`cgmes-export --profiles dl,gl,sc,dy` writes the diagram layout, geographical location, short-circuit and dynamics
profiles beside the EQ file, each as its own CGMES 2.4.15 document; the library reads them back. The DY profile carries
each machine's armature resistance from the RAW file's ZR. `cgmes-shacl` runs your SHACL shape files, such as the
ENTSO-E CGMES-AP shapes, on a bundle with a native SHACL Core checker and no third-party library. Its verdict is
"conforms", "does not conform" or "not determined" (a part the checker does not run could change the answer). Its exit
codes match `cgmes-conform`: 0 conforms, 3 does not conform or not determined, 1 an input cannot be read, 2 usage.

Scope: round trips to the bits, one attribute literal pinned per PSS/E CON position for each DY model, and
hand-derived SHACL results; SHACL-SPARQL and other unsupported components are listed as not run by name.

### Scripting and API

- `GridSim.Core.Api`: `CaseEditor` holds an immutable case with an ordered edit log; a refused write changes nothing.
  `LoadFlowView` reads results by bus and branch.
- `gridsim rpc --protocol 2` adds `elements`, `get`, `set`, `edits`, `study` and `save`; `get` also reads a unit's solved
  output. A `study` of an edited case compares its JSON v2 copy with the held case attribute by attribute, bit for bit.
  Level 1 is unchanged byte for byte.
- Python: `gridsim.ModelSession` and `gridsim.psspy`, a psspy-style layer for a listed set of calls in PSS/E's argument
  order, each mapped call citing its public source, with GridSim's own ierr codes; a layout no public source confirms,
  and any other psspy name, is refused with the reason. Standard library only.
- Scripts: `GRAMMAR 1 WITH STUDIES` adds `STUDY CONTINGENCY`, `STUDY SHORTCIRCUIT BUS`, `STUDY OPF` and their readings;
  grammar 1 is unchanged.

Scope: edit, solve and undo against `pf` bit for bit, each STUDY against the Core calls and the CLI verbs bit for bit,
and Python sessions that refuse each other's case handles.

### Plug-ins and user models

- A .NET plug-in interface for dynamic models (contract version 1, `GridSim.Core.Dynamics.Plugins`), in the roles
  exciter, governor, stabiliser, injection and load. Plug-ins load only from a `gridsim-plugins/1` configuration you
  name: every path confined to the configured root, every assembly pinned by SHA-256, every id unique, all of it
  checked before anything loads.
- A determinism lint reads each assembly before it loads and refuses, by name, the members that can make a run differ
  between machines or runs: threads, clocks, random numbers, hash-ordered enumeration, platform maths, I/O, reflection
  and the rest listed in technical chapter 24, which also lists what the lint does not see. A replay check then runs
  each model from two operating points, alone and side by side, and binds it only when every run repeats bit for bit.
  The lint is a guard on determinism, not a sandbox: a plug-in runs with the user's rights.
- `dynamics-study --model-assignments <file>` binds shipped, file and (with `--model-plugins <config>`) plug-in models
  to machines by RAW bus and id; the result records each plug-in's assembly hash and marks it outside the verified
  contract. `gridsim user-model check-plugin <config>` checks a configuration under every rule.
- Declarative models (`gridsim-model/1`) gain the `injection` and `load` roles and an `expr` block: arithmetic, `min`,
  `max`, `abs`, `sqrt`, `exp` and `clamp` compiled once to a fixed evaluation order with a declared unit.

Scope: the sample plug-in TGOV1 and SEXS and an `expr` TGOV1 equal the coded models bit for bit at three step sizes, on
IEEE 9 and through the dynamics study; a constant-power injection and a constant-impedance load hold their closed-form
operating points to 1e-12 pu; hostile plug-ins are refused before anything loads. Without the new flags every result is
byte-identical.

### Fixes

- `crossval.py` refuses a result that does not hold exactly one row per model bus, and keeps which reactive limit each
  tool pinned, so one tool at Qmax and the other at Qmin is a disagreement, not a boundary case.
- `gridsim report --template-for` works on every verb's `--json` result.
- `DcPowerFlow.SolveBatch` groups models into families in linear time; the families and every result are the same bits.
- Python: `Session.run_verb` accepts `--out` for the verbs whose own output it names, as `GridSim.run` does.
- State estimation: `EstimationOptions.LnrDetection` (off by default) also tests the largest normalised residual when
  the chi-square test passes, so on case14's full set an active flow wrong by 8 sigma is now found.
  `EstimationResult.MaxNormalizedResidual` reports the largest one. With it off, every result is unchanged.
- PSS/E RAW import lists the load (IDE 1) buses that have an in-service machine; their output is still solved as a
  fixed injection.
- The HYGOV governor is described as GridSim's simplified hydro governor, not the PSS/E HYGOV structure. No output
  changes.
- The API docs and the manual's PSS/E chapter state the bus shunt convention (Bs is the MVAr injected at 1 pu) and the
  VSC DCSET sign.

## Artefacts

The release workflow builds the archives, SBOMs and `SHA256SUMS.txt` from the tagged commit and attaches them to the
release, as for RC-4: the command line tool for Windows x64, Linux x64 and macOS arm64, the WPF app for Windows x64,
the Avalonia app for Windows x64 and Linux x64, and CycloneDX SBOMs for the tool, the apps, the core library and the
web image. Check every download against `SHA256SUMS.txt` with `sha256sum <file>` (Linux, macOS) or
`Get-FileHash <file> -Algorithm SHA256` (Windows PowerShell).

The .NET runtime in these builds is tracked by a weekly check: a newer .NET 10.0 SDK opens an issue, and the update
ships by rebuilding and releasing.

## System requirements

Unchanged from RC-1 (see RC1.md): Windows 10 or 11 x64, x64 Linux with glibc 2.27 or later, 8 GB RAM minimum and
16 GB recommended, no .NET install needed.

## Verification of this release candidate

GridSim's numerical core is verified to machine epsilon against analytic, published and independent references. The
new work is validated against independent tools: SciPy and NumPy reference implementations, MATPOWER cases and
published optima. It is not validated against measurements from a real power system.

On one Windows 11 x64 machine (8 cores, 8 GB RAM), .NET SDK 10.0.401:

- Release build of every project with 0 warnings.
- Each package ran its own test slices and the documentation slice in Release on its own commit, and each core-review
  fix its own test slices, with no failures.
- On this commit, before the tag, every RC-5 test class together and every slice the programme touched were run in
  Release, one at a time: the power-flow, DC, batch, storage, OPF, SCOPF, MATPOWER, loss, reduction, interchange
  (PSS/E, DYR, COMTRADE, CGMES, CIM, SHACL), dynamics, RMS, exciter, governor, load, FACTS and HVDC, protection, fault,
  grid-code, compliance, scripting, rpc, user-model, plug-in, report, cross-validation, CLI contract and version slices,
  the run journal and breaking-change slices, the GridSim.Scripting.Tests project, and the Python tests of `python/`
  and `tools/crossval/tests`. None failed. Every one of the 66 GridSim.Tests slices passed, among them the 369 RC-5
  tests; so did the breaking-change (26), run journal (9), loss-objective OPF convergence (3) and adaptive angle scale
  (9) tests, the 221 GridSim.Scripting.Tests, 53 Python tests, 16 cross-validation tests and all 1,145 documentation
  tests. The three longest documentation tests ran on their own, with no time limit.
- Tests that RC-4 shipped and this release changes are listed with their reasons in the CHANGELOG under 2.1.074 and
  2.1.075 (audit-log start, compliance attestation, unit protection, template kind counts, the Newton retry), and under
  2.1.100 for the core-review fixes (the DGS capacitor sign, the REEC_C SOCmin limit, the PSS/E VSC test data and the
  defective-matrix modal export), each with its derivation beside it. The shared-writer verb list gained the eight new
  verbs (2.1.091). Every other test change is to a test added in this release series.
- Documentation checks: `tools/docs/lint.py` (0 failing) and `tools/wiki/gen_cli_reference.py --check`.

**Not done for RC-5, and why.** These need the build box, which was not available for this candidate:

- The full test suite in Release (GridSim.Tests, GridSim.App.Tests, GridSim.Scripting.Tests as part of it), including
  the large cases the RAM gate skips on an 8 GB machine.
- The determinism slice in all three CPU modes: normal (AVX2 with FMA), `DOTNET_EnableAVX2=0` and
  `DOTNET_EnableHWIntrinsic=0`.
- The bit-identical case run against the RC-4 base, and the legacy command compatibility capture against RC-4. Each
  package instead pins the existing outputs it could touch by digest in its own tests.
- Linux and macOS builds, the Docker smoke test, the documentation re-captures and `mkdocs build --strict`.
- No comparison with a commercial tool (PSS/E, PowerWorld, PowerFactory, PSCAD): no licence is available, so no parity
  with any of them is claimed (COMPARATOR-UNVERIFIED). No vendor-written RAW 36, RAWX, SEQ or COMTRADE file has been
  run; the public CGMES-AP shape files were read without error, but select no nodes in a CGMES 2.4.15 export.

## Known limits

- RMS faults use the balanced positive-sequence method: no negative-sequence braking torque or per-phase detail. The
  adaptive-step study refuses the new fault events.
- DYR: GENTPJ is refused (its record order is not public; use a `gridsim-dynamics/2` file); CMLD is a subset (no
  motors B to D, electronic load, distributed generation, tap changer or relays); SVSMO1 to 3, CSVGN3 to 6, CSTCON,
  CSTATT, VSCDCT, CDC1T, CDC6, CDC6T, CDC7T and CDCMC are refused by name.
- Placed protection: no mutual-coupling compensation, teleprotection, power-swing blocking, load encroachment,
  switch-onto-fault, CVT transients, 87T or 87B; a 21 or 87 on a transformer is refused.
- OPF: no phase-shifter angle control or mixed-integer optimality of the rounding; SCOPF has no generator outages or
  N-2; `reconfig` handles balanced feeders only.
- Grid code: PPM reactive polygons are user-supplied; no faults over 140 ms, fast fault current injection or AVR
  performance.
- `reduce`: no dynamic or short-circuit equivalents; not compared with PSS/E or PowerFactory equivalents.
- CGMES 3.0 is refused for the extra profiles; RAWX reads the common sections only; the SEQ LOAD section is refused when
  not empty.
- psspy: a listed subset only; injection and load user models are reached through the library, not the study file.
- Plug-ins: the lint checks names, not data flow; it does not see text formatted with the current culture without
  naming it, or a framework method that enumerates a dictionary or set handed to it.
- The adaptive-step RMS run integrates rotor angles as absolute angles, not against the centre of inertia. Their error
  scale is fixed at `atol + rtol * 1 rad` (B26), so the angle tolerance no longer loosens on a long off-frequency run
  and does not depend on where the angle reference sits.
- VSC1: the voltage converter's own current limit is not fed back to the DC side (a fault at its bus).
- QSTS: self-consumption units share their bus's demand, but storage in another mode on the same bus is not
  subtracted from it.
- Carried from RC-4: AWAITING STANDARD TEXT, AWAITING FILES and UNCONFIRMED items as listed in RC4.md; GPU paths are
  deterministic run to run but not bit-identical to the CPU path; `gridsim rpc` has no authentication and refuses a
  non-loopback listener unless `--allow-non-loopback` is given.

## Reporting bugs

Send every bug report to **mark@gridsimdigitaltwin.co.uk**. Please give the version (`gridsim --version`), the operating system,
the command or the steps, the case (or a small case that shows the problem) and what you expected.
