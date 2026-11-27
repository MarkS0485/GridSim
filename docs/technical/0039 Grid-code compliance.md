# Grid-code compliance

The grid-code compliance suite checks one generating unit against the GB Grid Code European Connection Conditions
(ECC, Issue 6 Revision 33, 11 June 2025) or Commission Regulation (EU) 2016/631 (RfG). It covers fault ride-through,
reactive capability, frequency response, RoCoF withstand and frequency withstand. The code is in
`src/GridSim.Core/Compliance/GridCode/`, the verb is `gridsim grid-code` ([user manual ch. 19](<../manual/0019 CLI reference.md#grid-code>)),
and the tests are `tests/GridSim.Tests/Rc5Wp8GridCodeTests.cs`.

The suite is additive. Its checks are pure functions over their inputs and over traces of the existing study entry
points (`RmsTransientStability.StudyEventsObserved`, `StudyEventsWithFaults`, `StudyEventsWithUnitRelays`,
`FaultAnalysis.ThreePhase`, the default Newton power flow), called unchanged. A study run through the suite gives the
same bits as the same study run directly; a test pins this for the PCC voltage and the rotor angles.

## Requirement data

Each code edition is one embedded file, `gridsim-gridcode-requirements/1`, in `Compliance/GridCode/Requirements/`:
`gb-ecc-issue6-rev33.json` and `eu-rfg-2016-631.json` (resource ids `GridSim.Core.gridcode.<id>.json`). Every value
carries the clause it was read from. `GridCodeRequirements.Load(id)` parses a file; `BuiltInIds` lists them.

- **GB values are fixed.** `GbProfile(moduleType, technology, connectionKv)` selects the FRT table: ECC.6.3.15.2 (type B
  synchronous), .3 (type C and D synchronous below 110 kV), .4 (type D synchronous at or above 110 kV), .5 (power park
  modules below 110 kV), .6 (type D PPM at or above 110 kV, and DC-connected PPM) and .7 (HVDC systems and remote-end
  converters). A type B or C module at 110 kV or above is refused, because RfG Art. 5 makes it type D.
- **RfG values are ranges.** The study gives the TSO's choice and `RfgRanges` refuses it unless it lies inside the range,
  naming the parameter, the range as printed and the article. A bound may name another parameter ("trec1-0,7" means from
  trec1 up to 0.7 s). The 0.14 to 0.25 s clearing time is taken only with `extendedClearingTime: true`. Tables 3.1 and 3.2
  apply to types B and C and to type D below 110 kV; Tables 7.1 and 7.2 to type D at or above 110 kV. HVDC systems and
  DC-connected PPMs are in Regulation (EU) 2016/1447 and are refused under `eu-rfg`.

| GB table | Uret | Uclear | Urec1 | Urec2 | tclear | trec1 | trec2 | trec3 |
|---|---|---|---|---|---|---|---|---|
| ECC.6.3.15.2 | 0.3 | 0.7 | 0.7 | 0.9 | 0.14 | 0.14 | 0.45 | 1.5 |
| ECC.6.3.15.3 | 0.1 | 0.7 | 0.7 | 0.9 | 0.14 | 0.14 | 0.45 | 1.5 |
| ECC.6.3.15.4 | 0 | 0.25 | 0.5 | 0.9 | 0.14 | 0.25 | 0.45 | 1.5 |
| ECC.6.3.15.5 | 0.10 | 0.10 | 0.10 | 0.85 | 0.14 | 0.14 | 0.14 | 2.2 |
| ECC.6.3.15.6 | 0 | 0 | 0 | 0.85 | 0.14 | 0.14 | 0.14 | 2.2 |
| ECC.6.3.15.7 | 0 | 0 | 0 | 0.85 | 0.14 | 0.14 | 0.14 | 2.2 |

The RfG file carries Tables 3.1, 3.2, 7.1 and 7.2 (FRT), Table 2 for all five synchronous areas exactly as printed
(frequency withstand), the Art. 13(2) and 15(2)(c) LFSM ranges, Tables 4 and 5 (FSM), and Tables 8 and 9 (the largest
U-Q/Pmax profile per area).

## The FRT line

`FrtProfile` is the voltage-against-time line of Figure 3 of the Regulation, which GB Figures ECC.6.3.15.2 to .7 use.
The shape is read from the figure: t is measured from fault inception; U(t) = Uret for 0 <= t <= Tclear (the lower value
is held at Tclear itself); a line from (Tclear, Uclear) to (Trec1, Urec1); Urec1 to Trec2; a line from (Trec2, Urec1) to
(Trec3, Urec2); Urec2 after Trec3. A zero-length segment is skipped. A line is evaluated as
`a + (b - a) * (t - t0) / (t1 - t0)` in that order, and the tests pin its bits. `TimeBelow(v)` is the total time the
line is strictly below v. Adjoining stretches below v are joined and each is taken as end minus start, so a line that
rises through v once gives exactly the crossing time (for GB type D synchronous, 0.8 pu is crossed at 0.45 + 0.3 x 1.05
/ 0.4 = 1.2375 s).

## The checks

Each check returns `GridCodeCheckResult(Id, Clause, Outcome, Value, Limit, Margin, Unit, Reason)` with a status word
and the time of the margin where it has one. The margin is signed, positive inside the requirement. A check that cannot
be evaluated is never a pass.

| Id | Kind | Method |
|---|---|---|
| `frt-envelope` | RMS | `RmsSequenceFault` at the PCC (or the given bus), cleared Tclear later, run through `StudyEventsObserved` with the PCC measured. The samples with t in [0, Trec3 + 1 s] are split into the fault period and the time after clearing; the margin reported is min of V(t) - U(t) over both. In order: a fault-period voltage below Uret by more than 1e-9 pu is NOT DEMONSTRATED (the test is more severe than the line, so a trip is allowed); a lowest fault-period voltage more than 1e-3 pu above Uret is NOT DEMONSTRATED (the test is milder than the line); a GB run that misses the ECC.6.3.15.8 pre-fault conditions is NOT DEMONSTRATED; a unit that did not stay connected FAILs; a voltage below the line after clearing, with the unit still connected, is NOT DEMONSTRATED; otherwise PASS. For an unbalanced fault at the PCC, V is the smallest phase voltage from `StudyEventsWithFaults`; away from the fault point V is the positive-sequence magnitude and the result says phase voltages there are not represented. |
| `frt-retained-voltage` | helper | Xf = Uret Xth / (1 - Uret) for a flat pre-fault Thevenin view, Xth the positive-sequence Thevenin of `FaultAnalysis.ThreePhase` at the PCC; Uret = 0 is a bolted fault. A three-phase `frt-envelope` with no impedance given uses this Xf. |
| `frt-recovery` | RMS | ECC.6.3.15.8(vii). tv is the first sample after clearing with V_PCC >= 0.9 pu; margin = min over t >= tv + 0.5 s of P(t) - 0.9 P(0-), P from the converter trace at the unit's bus. A synchronous unit is NOT EVALUATED: machine electrical power is not in the RMS traces. A GB run that misses the ECC.6.3.15.8 pre-fault conditions is NOT DEMONSTRATED. The result holds for the one fault applied. Under RfG the recovery is the TSO's specification and is not evaluated. |
| `frt-uv-settings` | static | Each 27 stage: margin = delay - TimeBelow(pickup) (s). A pickup above Urec2 FAILs. |
| `q-capability` | static power flow | Sub-model of `plantBuses` and the branches among them, the PCC as the slack at each test voltage, the unit as a fixed injection at (Pmax, Qmax), then (Pmax, Qmin), default Newton. Margins Q_pcc(Qmax) - Q_req,lag and Q_req,lead - Q_pcc(Qmin) (Mvar). GB type B: 0.95 power factor at 1.0 pu (ECC.6.3.2.1.1, .2.2.1; Q/Pmax = 0.3286841051788632). Those clauses state no test voltage; 1.0 pu is GridSim's choice. GB type C/D synchronous: 0.92 power factor at 0.95, 1.0 and 1.05 pu (Figure ECC.6.3.2.3, read from the figure; Q/Pmax = 0.42599821613620475). Otherwise the corners of the user's `uqProfile`, refused under RfG if its Q/Pmax or voltage span exceeds the area's Table 8 or 9 maximum. |
| `lfsm-o`, `lfsm-u` | governor injection | A fresh governor from the unit's `GovernorData` is driven with speed deviation (f - fn)/fn after a step to the test frequency, the on-site test method (`FrequencyInjection.Run`). dP_req = -(f - f1) / (fn s) Pref beyond the threshold f1. Margin = dP(T_end) in the required direction - \|dP_req\| (pu, machine base). GB: 50.4 Hz (50.5 Hz in FSM), 10 % droop, Pref the output before the event. GB LFSM-O is read 10 s after the step, the time ECC.6.3.7.1.2(iii) allows, and any other `tEndSeconds` is refused. GB LFSM-U: 49.5 Hz for types C and D; the requirement set gives it no response time, so it is read at `tEndSeconds` and the result says so. RfG: the TSO's threshold, droop and Pref (`output` or `pmax`), range-checked, read at `tEndSeconds`. With `events`, the network variant reads the PCC frequency from `BusFrequencyMeter` and a converter's P from its trace at the end of the run; under GB LFSM-O the run must end 10 s after the first event. A synchronous unit is then NOT EVALUATED. |
| `fsm-step` | governor injection | Margin = min over t in [t1, T_end] of dP(t) - line(t), with the required line 0 up to t1, (t1, 0) to (t2, dP1), then dP1 (Figure 6, read from the figure), and dP(t2) >= dP1. GB: dP1 = 10 % of Pmax, t1 <= 2 s with inertia (1 s without), t2 = 10 s. RfG: dP1 1.5-10 %, t1 <= 2 s with inertia, t2 <= 30 s, insensitivity, deadband and droop range-checked when given. A governor at droop s settles at dP/Pmax = abs(df) / (fn s), so a step smaller than dP1 fn s is refused: s is the given `droop` (the unit's setting), else the code's largest droop (GB 5 %, so 0.25 Hz). |
| `rocof-settings` | static | Each 81R stage: margin = pickup - limit (Hz/s); a stage measuring over another window carries a note. GB: 1 Hz/s over 500 ms (2 Hz/s for a grid-forming PPM), HVDC 2.5 Hz/s over 1 s, DC-connected PPM 2.0 Hz/s over 1 s (ECC.6.3.13.2-.4). RfG: the TSO's value with its source (Art. 13(1)(b)). |
| `rocof-event` | RMS | A generator trip or load step: the worst RoCoF at the PCC over the code's window (`BusFrequencyMeter`) and the unit's own 81R stages in `StudyEventsWithUnitRelays`. At or below the limit with an 81R trip: FAIL; without: PASS; above the limit: NOT DEMONSTRATED. NOT EVALUATED when the case carries no 81R stage for the unit, or when the RMS run does not evaluate it (a converter unit's own relays are not integrated). |
| `frequency-relays` | static | Each 81U / 81O stage: a pickup inside the no-trip range (GB 47.5-51.5 Hz, ECC.6.1.2.1.3; RfG the unlimited band of Table 2) FAILs; in a timed band the margin is delay - the band's minimum time (GB 47.0-47.5 Hz: 20 s). |

"Stayed connected" is synchronism for machines. When the case carries unit protection for the unit, the same events run
through `StudyEventsWithUnitRelays` and any trip of the unit FAILs the check. If that run does not evaluate one of the
unit's stages (it does not integrate a converter unit's own relays), staying connected is NOT EVALUATED. A converter unit
with a DER_A model is not evaluated for staying connected, because the RMS trace does not carry the DER_A trip.

What `frt-envelope` shows. The fault is applied at the PCC and cleared after Tclear. During the fault the check needs the
PCC voltage at Uret: not below it by more than 1e-9 pu, and not above it by more than 1e-3 pu (the bolted fault the RMS
engine stamps as a 1e7 pu shunt leaves a few micro-pu). After clearing, the PCC voltage follows the network's own
recovery. The check compares it with the line but does not shape it to the line, so a PASS shows ride-through of a fault
at Uret for Tclear followed by that network's recovery, not of every voltage on the line.

Pre-fault conditions. ECC.6.3.15.8 tests a GB unit at its rated MW with the PCC at 1.0 pu before the fault. A GB
`frt-envelope` or `frt-recovery` run whose unit is below Pmax, has no Pmax, or sits at the slack bus, or whose PCC is more
than 1e-3 pu from 1.0 pu, is NOT DEMONSTRATED with the reason. The maximum leading power factor of ECC.6.3.15.8(i) is
not imposed; the result says so. Under RfG the TSO sets the pre-fault conditions, and the result notes that the study
runs at the case's operating point.

## Verdict and document

The verdict follows `RuleSetEngine`: NON-COMPLIANT on any failure, else INCOMPLETE while any check is NOT EVALUATED or
NOT DEMONSTRATED (or nothing with a verdict was checked), else COMPLIANT. `frt-retained-voltage` is INFO and does not
count. `GridCodeStudy.Write` gives the `gridsim-gridcode/1` document with a fixed key order, invariant culture,
shortest round-trip numbers and `\n` line ends, so two runs, and runs under another culture, give the same bytes.

## Verification

Each pinned value was recomputed in Python (IEEE double) before it was pinned.

- The GB lines are evaluated bit for bit at the hand points of each segment, and every GB table reads back exactly.
- UV settings against GB type D synchronous: margins -0.23750000000000004, 0.26249999999999996 and 0.009999999999999981 s, exact.
- FRT on RMS, analytical boundary. A machine with H = 1000 behind X'd = 0.1 feeds the PCC through X = 0.2, and the unit
  at the PCC is a GENCLS with X'd = 0.1 and no transfer. Xth = 0.3 in parallel with 0.1 = 0.075 pu. With Xf from the
  retained-voltage rule, V(PCC) = 0.3 during the fault and 1.0 after it to 1e-12, and the margin is 0 to 1e-9; the unit
  is at 0 MW of its 100 MW, so the result is NOT DEMONSTRATED on the pre-fault condition. With 0.9 Xf, V = 27/97 =
  0.27835051546391754 and the margin is -0.021649484536082453 (1e-12, NOT DEMONSTRATED).
- FRT pass and fail, equal-area criterion. A unit at its rated 50 MW behind X'd = 0.1 feeds an infinite bus through
  0.3 pu, on the GB type D line (bolted fault, 0.14 s). The critical clearing time is 0.681 s for H = 10 (PASS) and
  0.096 s for H = 0.2, where the angle at clearing is past the unstable equilibrium (FAIL, synchronism lost). An
  undervoltage stage at 0.5 pu, 0.05 s trips the stable unit (FAIL).
- Unbalanced fault. A line-to-line fault at the PCC through the Thevenin reactance gives a smallest phase voltage of
  1/sqrt(3) (the positive sequence is 2/3); the check reports 1/sqrt(3) to 1e-9, NOT DEMONSTRATED as milder than the line.
- Converter recovery: the active current is held through the dip, so the output returns to its 0.5 pu and the margin is
  0.05 pu to 1e-6.
- Q capability on a two-bus case against the closed form Vg^2 = (a + sqrt(a^2 - 4 X^2 (P^2 + Q^2))) / 2, a = Vp^2 + 2 X Q,
  Q_pcc = Q - X (P^2 + Q^2) / Vg^2, at 0.95, 1.0 and 1.05 pu, to the 1e-6 Mvar power-flow tolerance.
- LFSM-O and FSM against the exact zero-order-hold response of TGOV1 (dP = -0.36 (1 - e^(-t/0.5)) for a step to
  50.9 Hz; 0.12 (1 - e^(-t/T1)) for 49.7 Hz) at every sample, to 1e-12. GB LFSM-U (a step to 49.1 Hz, margin
  0.3199999992579848) and a GB LFSM-O failure at 10 s with T1 = 50 s (margin -0.014743071108073463) to 1e-12; the FSM
  step bound and a governor at 3 % droop (dP(10) = 0.13333333305851286).
- `rocof-event` on two machines whose inertias match their share of a load step, so they turn together at the closed-form
  RoCoF f0 G b^2 / ((G^2 + b^2) 2 (H1 + H2)): 0.49955040463582784 Hz/s (a unit 81R trip FAILs, no trip PASSes) and
  1.2430080795525171 Hz/s (NOT DEMONSTRATED), to 1e-6.
- The LFSM-O network variant: a converter with no frequency response FAILs at the frequency a load reduction drives the
  grid machine to after 10 s.

## GB extension checks: fast fault current injection, faults over 140 ms, AVR and PPM voltage steps

Four more GB checks run only when a study names them: `ffci-envelope`, `frt-long`, `avr-step` and `vctrl-step`. They
read their values from a second embedded file, `gb-ecc-issue6-rev33-ext.json` (schema
`gridsim-gridcode-requirements-ext/1`, resource `GridSim.Core.gridcode-ext.<id>.json`), so the WP8 file, its hash and
the `BuiltInIds` list are unchanged. The file is stored with no line-ending conversion and its sha256 is pinned by a
test. A result that runs one of these checks records the file's hash as `requirementsExtSha256`; other results are byte
for byte what they were. The code is in `FfciCheck.cs`, `LongFaultCheck.cs`, `StepResponseChecks.cs` and
`StepResponseMetrics.cs`, and the tests are `tests/GridSim.Tests/Rc5Wp22GridCodeExtTests.cs`.

The values come from the same issue as the WP8 file (Issue 6 Revision 33). NESO publishes that section as a Word
document, and its page numbers depend on how it is rendered, so every value is located by clause and figure and its
`page` is null. Several of the figures are marked NOT TO SCALE. Only their labelled numbers are transcribed and marked
`figureRead`. A value the figure does not label is never interpolated: the check returns NOT EVALUATED and says why.
The GC0111 consultation drafts had two further rules: the injection observable within 30 ms, and an average over 30 to
60 ms. Neither is in Issue 6 Revision 33, so neither is checked.

### ffci-envelope (ECC.6.3.16)

The check runs the same simulation as `frt-envelope`: `RunFrt` with the same events, so the same bits. It reads the
unit's reactive current from its converter trace and divides by its rating, because ECC.6.3.16.1.7 puts 1.0 pu current on
the MVA rating. The current is positive when the unit exports reactive power, the sign of REGC_A.

- Fault inception is the first sample with the PCC below 0.9 pu (ECC.6.3.16.1.4). The pre-fault current I0 is the
  sample before it.
- The target current is read from Figure ECC.6.3.16(a), which is marked NOT TO SCALE (figure-read). Its two labelled
  points are (0.9 pu, -0.312 pu) and (0.5 pu, 1.0 pu). The line runs straight from (0.9 pu, Is) to (0.5 pu, 1.0 pu), with
  Is = max(I0, -0.312). It is never below I0 and never above 1.0 pu (ECC.6.3.16.1.5):
  `IR(V) = min(1, max(I0, Is + (1 - Is) (0.9 - V) / 0.4))`, with V the PCC voltage at that sample. For a pre-fault current
  above -0.312 pu the line starts at the pre-fault current. This is GridSim's reading of ECC.6.3.16.1.3: the current must
  increase with the fall in retained voltage, and depends on the pre-fault condition. The requirement file and the
  result notes say so.
- The edge of the forbidden area is `I0 + f(t) (IR(V) - I0)`. Here f is read from Figures ECC.6.3.16(b) and (c): 0 up
  to 20 ms, 0.65 at 60 ms, 1 at 120 ms, linear between, and t is the time from inception.
- Only samples after the envelope's leading zero part (20 ms) are judged, because the forbidden area starts there.
  Before it the edge is I0 itself, which a unit in steady state meets with a margin of exactly 0.
- Samples at or after clearance are not judged, because blocking is permitted there (ECC.6.3.16.1.8). Samples more than
  140 ms after inception are not judged either, because the figures stop there.

The check passes when the current is on or above the edge at every judged sample. It fails otherwise, and reports the
worst margin and its time from inception. No tolerance is applied. A fault cleared before the envelope asks for any
increase is NOT DEMONSTRATED. The check is NOT APPLICABLE to a synchronous unit, and to a grid-forming unit, which comes
under ECC.6.3.19 instead. It is NOT EVALUATED for an unbalanced fault (ECC.6.3.16.1.13 gives no envelope), and when the
RMS study shares one trace among several converters at the unit's bus. Deviations agreed with the company under
ECC.6.3.16.1.10 are not allowed for.

### frt-long (ECC.6.3.15.9)

Figures ECC.6.3.15.9(a) and (b) are voltage-duration profiles (ECC.A.4A.3). Each point is a dip level and how long the
unit must ride it through; it is not a voltage-against-time trace. The labelled corners are:

| Profile | Applies to | Corners (duration s, voltage pu) |
|---|---|---|
| ECC.6.3.15.9.2.1(a), Figure ECC.6.3.15.9(a) | type C and D synchronous | (0.14, 0.33) sloped to (1.088, 0.85), 0.85 to 180 s |
| ECC.6.3.15.9.2.1(b), Figure ECC.6.3.15.9(b) | type C and D power park modules | (0.14, 0.15) sloped to (1.2, 0.80), 0.80 to 2.5 s, 0.85 to 180 s |

The 140 ms points belong to `frt-envelope`. Each duration asked for (by default every labelled corner longer than
0.14 s) is tested as a balanced dip at the PCC. The dip is held at the profile's voltage for that duration and then
cleared. A duration on a sloped segment has no labelled voltage, so its row is NOT EVALUATED. At the 2.5 s step the
lower voltage is used, since it is on the line. A duration that is not a whole number of steps (1.088 s with 0.01 s
steps) is rounded up to the next whole step. The dip is then longer than the profile asks: staying connected still
counts as PASS, but a trip gives NOT DEMONSTRATED rather than FAIL.

The dip is held by a sequence of three-phase fault stages, 50 ms each by default. Each stage starts with the
retained-voltage reactance `X = U Xth / (1 - U)`. After each pass over the whole run, each stage is corrected from its
mean PCC voltage Vm: the source that gives Vm through X behind Xth is `E = Vm (X + Xth) / X`, and the new reactance is
`U Xth / (E - U)`. At a fixed point Vm = U exactly, whatever the error in Xth. The passes stop when every dip sample is
within 1e-3 pu of U (the `frt-envelope` depth rule), or after 8 passes.

The verdict rules use the tolerances of `frt-envelope`:

- If the ECC.6.3.15.8 pre-fault point is not met, the result is NOT DEMONSTRATED.
- If staying connected cannot be judged, the result is NOT EVALUATED.
- If synchronism is lost or a unit relay trips: NOT DEMONSTRATED when the PCC went below U by more than 1e-9 pu (the
  test was more severe, so a trip is allowed) or the dip ran longer than asked; otherwise FAIL. A trip in a milder test
  (the whole dip above U) is a FAIL.
- If the unit stays connected and stable: NOT DEMONSTRATED when the lowest PCC voltage of the dip is above U by more
  than 1e-3 pu (the test was milder); otherwise PASS. A ride-through of a dip that went below U is a harsher test
  passed, and the notes say so.
- A converter with no unit protection in the case has nothing that can trip it, so staying connected rests on
  synchronism alone, as in `frt-envelope`.

The active-power rules (ii) and (iii) and the 30-minute rule (iv) are not checked. HVDC equipment is NOT EVALUATED,
because ECC.6.3.15.9.1.1 leaves it to the Bilateral Agreement. A DC-connected PPM is NOT EVALUATED, because neither
profile names it. Type B is NOT APPLICABLE.

### avr-step (ECC.A.6.2.4.1)

The clause asks for a step from 90 % to 100 % of nominal terminal voltage with the unit on open circuit. The voltage
must first reach 100 % in less than 0.6 s, and settle within 5 % of the change (|V - 1.0| <= 0.005 pu) in less than 3 s.

The RMS kernel always has a network, so the open circuit is not run through it. With no stator current the kernel's own
flux equations reduce to `T'do dE'q/dt = Efd - E'q - E''q Se(E''q)` and `T''do dE''q/dt = E'q - E''q`, with E'd and E''d
at 0 and the terminal voltage equal to E''q. `StepResponseChecks.OpenCircuitStep` integrates these on the same exact
zero-order hold as the kernel. The machine starts at equilibrium at 0.9 pu. At t = 0 the exciter's reference steps to
the value that holds 1.0 pu in steady state: 1.0 plus the exciter's steady error at the field that holds 1.0 pu. A
proportional AVR then settles at 1.0 pu, the voltage it is judged against.

The two rows pass only when they are strictly under their limits. The overshoot is reported in a note, because the
clause sets no limit on it. A unit without an exciter model is NOT EVALUATED. So is an exciter that reads the terminal
phasors on a bound machine base (ESST4B, ESST3A and the like), which the open-circuit run does not set up.

### vctrl-step (ECC.A.7.2.3.1)

The study gives the events that step the voltage, such as a load step near the PCC. The check runs the RMS study and
reads the unit's own reactive power from its converter trace. It reports four rows:

- **commencement (i):** within 0.2 s, the first sample whose reactive current has moved from its pre-step value
  towards its steady change by more than 1 % of that change. The current is used, not Q: Q = V Iq moves with the voltage
  alone, so a passive move of Q is no control action.
- **90 % (ii):** the time to 90 % of the change, within 1 s for zero to maximum, or within 2 s for maximum leading to
  maximum lagging (`leadToLag`). It is NOT DEMONSTRATED when the step changes Q by less than the range the limit is
  stated for (the unit's Qmax, -Qmin, or Qmax - Qmin).
- **oscillation (iv):** for 5 s after reaching 90 %, the peak-to-peak of any oscillation must be less than 5 % of the
  steady change. The oscillation is measured from the first turning point in the window, so a monotone approach counts
  as no oscillation. The clause says "the change in steady state maximum Reactive Power"; it is read here as the
  steady-state change of the step.
- **MVAr seconds (i):** always NOT EVALUATED, because Figure ECC.A.7.2.3.1a labels no level for "the required response
  at 1 second".

The reactive power is read at the unit's terminals, while the clause measures at the connection point. The result says
so.

### Step-response measurements

`StepResponseMetrics` reads a sampled trace without fitting anything. A crossing is the first sample interval that
brackets the level, placed by linear interpolation, so it lies within one sample of the true crossing. Overshoot is
`(peak - final) / |change|`. Settling is the sample after the last one outside the band, the first from which the
trace stays inside it, or null when the run ends outside it.

### Tests

- t90 and t10 of `1 - e^(-t/0.4)` sampled at 0.1 ms, against `0.4 ln 10` and `0.4 ln(10/9)`, to one sample.
- `tau = 0.45` gives t90 = 1.0361632918473207 s, which FAILs the 1 s limit.
- The overshoot of a `zeta = 0.5` second-order step, against `e^(-pi zeta / sqrt(1 - zeta^2)) = 0.16303353482158048`,
  to 1e-4.
- The FFCI logic on a synthetic requirement: a pass with its margin pinned, a fail at a known time with a known margin,
  and the exclusion after clearance.
- REGC_A Iq at 60 ms against `1.1 (1 - e^(-0.06/Tg))`, to 1e-6.
- The staged dip recomputed from the trace.
- Open-circuit AVR runs whose steady state is 1.0 pu, and the overdamped run's settling time against the continuous
  solution.
- A passive Q move with a constant reactive current, which does not commence.
- The file's sha256.
- The existing checks' output hashed against the build before this change.

## Scope

Covered: the GB ECC and RfG FRT lines, reactive capability at the code's test voltages, LFSM-O, LFSM-U, FSM, RoCoF
withstand of relay settings and of RMS events, and the frequency withstand bands and no-trip range of relay settings,
with the code values named above. When a study names them, the GB extension checks also cover faults over 140 ms
(staying connected at the labelled points of ECC.6.3.15.9), fast fault current injection for balanced faults
(ECC.6.3.16), the open-circuit AVR step (ECC.A.6.2.4.1) and the PPM or HVDC voltage step (ECC.A.7.2.3.1). The result's
scope note keeps the earlier "not covered" line word for word, because it is still true of a run without these checks;
a run with them adds a line naming the topics they take up. `grid-code --list` prints what it always did; the
extension checks are listed by `grid-code --list-ext`. Not covered:

- the PPM reactive polygons of Figures ECC.6.3.2.4(a)-(d) and ECC.6.3.2.5/6 (offshore); these are user-supplied only;
- for faults over 140 ms (ECC.6.3.15.9): the active-power rules and the 30-minute rule (only staying connected is
  checked), the durations on sloped segments, and HVDC equipment;
- for fast fault current injection (ECC.6.3.16): unbalanced faults, grid-forming units (ECC.6.3.19) and agreed
  deviations;
- for AVR and voltage-control performance (ECC Appendices E6/E7): everything but the open-circuit AVR step
  (ECC.A.6.2.4.1) and the PPM voltage step (ECC.A.7.2.3.1, without its MVAr-seconds rule); also the FSM monitoring and
  response services;
- unbalanced voltages away from the fault point (the RMS engine is positive-sequence);
- active-power recovery of synchronous units (no electrical power in the trace);
- the maximum leading power factor before the fault (ECC.6.3.15.8(i)), and the TSO's pre-fault conditions under RfG;
- a frequency-withstand check of RMS events (staying connected for the times of the withstand bands);
- one fault per FRT check.

The verdicts are study evidence. They are not a compliance certificate, and they were not checked against an ESO
compliance submission or a vendor tool.

## See also

- [ch. 24, Standard dynamic-model library](<0024 Standard dynamic-model library.md>) - the RMS engine and the sequence faults the FRT checks run.
- [user manual ch. 19, connection-assess](<../manual/0019 CLI reference.md#connection-assess>) - the connection-rule engine whose outcomes and verdict words the suite reuses.
- [user manual ch. 15](<../manual/0015 RMS, modal and impedance stability.md#grid-code-compliance>) - running the suite.
