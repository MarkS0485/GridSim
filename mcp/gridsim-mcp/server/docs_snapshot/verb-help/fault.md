### fault

`gridsim fault <case|case.m|case-dir> --bus <id> [--zf 0.0] [--type 3ph|slg|ll|dlg]`

Balanced three-phase fault at `--bus` through fault reactance `--zf` (pu, Zf = j`--zf`, printed `Zf=j...`; D-DOC-166:
`dist3ph fault --zf` is a resistance instead): fault current (pu, and kA when the bus has a
base kV), Thevenin impedance, faulted-bus voltage, the converter-interfaced units' current-limited infeed, and the
relays that operate on the faulted state. Generator subtransient reactance is synthesised at 0.20 pu where a unit
carries none. The prefault solve must converge: a non-converged prefault state is refused with `fault: the pre-fault
power flow did not converge (...)` and exit 3. Exit 2 without a case or `--bus`, for an unknown `--type`, or for a `--bus`
that is not in the case (`error: fault: fault bus <id> is not in case '<name>'.`, checked before any solve, the same
rule as `protection-check --fault-bus` and `iec60909 --bus`); exit 1 on a load error or a fault-analysis error. A
fault bus whose island has no fault source (no synchronous unit with a source reactance and, for the three-phase fault,
no converter-interfaced unit) is refused with exit 1, as `iec60909` refuses it: the loads alone would otherwise appear
to feed the fault. When
the converter current iteration of the three-phase study does not converge, the study is printed for diagnosis, the
relays are not assessed (`relays: not assessed (the faulted state did not converge, ...)`: a non-converged state is not
a network state), stderr says the faulted state is not reliable, and the run exits 3. A negative
or non-finite `--zf` exits 2 (`error: --zf expected a finite fault reactance of 0 pu or more (Zf = j zf) ...`), as
`protection-check --zf` does (D-DOC-089: it used to be accepted with exit 0, `fault case9 --bus 5 --zf -0.1` printing a
faulted-bus voltage of 2.776 pu). The relays are listed fastest first (see `protection-check`), and the closing line
names the primary clearance (`ProtectionReport.PrimaryClearance`), with its time: the faulted busbar's differential
`87` when it operates (instantaneous unit protection; its time now prints `inst`), otherwise the fastest operating branch
element with an operating time, or it says that no such element picks up (D-DOC-158: it used to name the first row of a
list sorted by raw margin, which on `fault uk --bus 1` was the Moyle reverse-power element). The faulted
state's line loadings (`LineFlow.LoadingPercent`, read by the result's callers, not by the relays) are taken on the
heavier end, as every power-flow engine takes them (D-DOC-090: they used the from end only).

`--type slg|ll|dlg` solves an unbalanced fault instead: single line (phase a) to ground, line to line (phases b-c), or
double line (b-c) to ground, on the three sequence networks superposed on the prefault state. The positive sequence is
the balanced study's network; the negative sequence the same with each unit's X2 (default X''d) and every phase shift
reversed; the zero sequence is built from the transformer winding connections and neutral reactances (the network
`iec60909` uses; line Z0 = 3 x Z1 where a line carries no R0 / X0), with loads neglected and no line charging. With
Zf = j`--zf`: SLG `I1 = I2 = I0 = V/(Z1+Z2+Z0+3Zf)`; LL `I1 = -I2 = V/(Z1+Z2+Zf)`; DLG
`I1 = V/(Z1 + Z2 (Z0+3Zf)/(Z2+Z0+3Zf))`. It prints the phase currents (pu and kA), the earth current `|3I0|`, the
sequence currents, the faulted bus's phase voltages and the driving-point Z1 / Z2 / Z0, then the relays that operate:
the phase elements on the phase quantities built from the sequence study and the earth-fault and negative-sequence
elements on the sequence currents (`50N` residual instantaneous at 1.0 x rated, `51N` residual IEC standard inverse at
0.2 x rated and TMS 0.1, `46` negative sequence at 0.2 x rated with 1.0 s, `21N` residual-compensated earth-loop
distance on lines with the 21 zones; generic values, [VERIFY] against a cited setting policy). A fault bus with no
earth path in the zero sequence carries no earth current: an SLG fault then gives zero current and a DLG fault is
solved as LL (both said). A model with no transformer winding connection anywhere (every MATPOWER and JSON v1 case,
so no bundled MATPOWER case) has no earthed neutral at all: `--type slg` and `--type dlg` print the study, then stderr
says the result reflects missing data and the run exits 3. `--type ll` does not use the zero sequence and exits 0.
A JSON v2 case can carry the data (N234, October 2026: phase P5 opened for fault sequence data): a transformer's
`windings`, `r0Pu` / `x0Pu` and `neutralXPu`, and a generator's `subtransientXdPu` / `negativeSeqXPu`; a v2 case that
gives windings runs `slg` and `dlg` with its own earth paths and exits 0 (see
[technical ch. 28](<../technical/0028 The v2 JSON schema.md>)).

Phase elements: `50` / `51` / `37` read the largest of each branch end's three phase currents, `49` that current over
the rated current, `27` / `59` each bus's lowest / highest phase-voltage magnitude, and `21` each end's three
phase-to-phase loops, `Z = (Vx - Vy) / (Ix - Iy)`, with the loop in the fastest zone deciding (named in the verdict,
for example `[b-c loop]`). A balanced fault and a power flow are assessed on the positive sequence alone. Converter-
interfaced units add no fault current in the unbalanced study: the model defines no negative- or zero-sequence fault
behaviour for them, so they stay at their prefault current in all three sequence networks. A `note:` line says how many
and their rated MVA whenever the case has any, and the result file adds `converters_without_fault_infeed` and
`converters_without_fault_infeed_mva`; the `3ph` study models their positive-sequence fast fault current. The result
file adds the phase, earth and sequence currents and a `branches` table (`index,name,from,to,max_phase_from_pu,
max_phase_to_pu,residual_from_pu,residual_to_pu,negative_sequence_from_pu,negative_sequence_to_pu`).

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).
