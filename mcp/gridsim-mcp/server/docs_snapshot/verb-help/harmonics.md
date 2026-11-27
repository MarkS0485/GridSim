### harmonics

`gridsim harmonics [<case>] [--dir <path>] [--spectra <file.json> | --synth-sources | --converter-sources rated|dispatched] [--synth-spectrum g5|ideal] [--afe-reactance <pu> [--afe-resistance <pu>]] [--bus <id>] [--enableExport[=path]]`

Frequency-domain harmonic study on the study model (network, machine source impedance, loads as damping shunts):
penetration (Y(h) V = I), the driving-point resonance scan, a fine frequency sweep for the true peak between integer
orders, voltage-THD indices (relative to each bus's solved fundamental voltage, from the same solve as `--once`; the
Web HARMONICS tab uses the same normalisation) and an IEEE-519 compliance verdict. A bus with no base kV is judged at
the strictest tier, never certified compliant, and counted in a printed warning. See
[ch. 17, Harmonic studies](<0017 Harmonic studies.md>).

| Flag | Default | Meaning |
|---|---|---|
| `<case>` / `--dir` | `case9` | [Case argument](#case-arguments) or JSON case directory (`-d` is a short alias of `--dir`). |
| `--spectra` | - | A `gridsim-harmonics/1` source set. A non-zero component at an order outside the analysed odd orders 3 to 49 is refused (exit 1, each named); a triplen source is refused (exit 1). A set holding any measured source (provenance `Recorded` or `DerivedFromRecorded`) must carry `takenUtc`; an undated one, or any set dated inside the 169 h horizon, is refused with `REJECTED (forward inference): ...` and exit 2. An all-assumed library set (equipment spectra) may be undated. |
| `--synth-sources` | - | Select the synthetic default explicitly: one six-pulse source (0.5 pu fundamental) at the largest-demand bus (also the default without `--spectra`). Given together with `--spectra`, exits 2. |
| `--synth-spectrum` | `g5` | The spectrum of that synthetic source: `g5`, the cited ENA EREC G5 Issue 5 Table B.3 six-pulse profile (`6-pulse-g5`: 5th 31.4 %, 7th 10.9 %, 11th 7.0 %, ... to the 49th, THDI 34.4 %), or `ideal`, the idealised 1/h six-pulse spectrum (orders 5 to 25, 5th 20 %, 7th 14.3 %, THDI 29.0 %). The output names the spectrum used (`synthetic source : ...`). Any other value exits 2; with `--spectra` or `--converter-sources` (which bring their own spectra) it exits 2. |
| `--converter-sources` | - | `rated` or `dispatched`: one source per in-service converter-interfaced unit (battery, PV, wind, tidal, HVDC / interconnector; not a power-flow-only device), with the ENA EREC G5 Issue 5 Table B.3 profile for its technology: active front end (`afe-g5`) for a self-commutated converter, twelve-pulse (`12-pulse-g5`, `[VERIFY]`) for a unit marked as feeding no fault current (a line-commutated HVDC link). `rated` scales each profile by the unit's rated current (`MBase / BaseMva` pu, the G5 Stage 1C / 2A basis); `dispatched` by its output at the solved operating point, `sqrt(P^2 + Q^2) / BaseMva` (the fundamental power flow is solved first, so the slack's P and every PV unit's Q are the solved values, not the case's Pg / Qg; a fundamental that does not converge exits 3; a unit at zero output is listed as not modelled). Sources add linearly (G5 s.6.3.3: no declared diversity). The study then analyses orders 2 to 50 (Table B.3's range); the triplen components (zero sequence) are left out of the balanced study, and their count and the first one are printed. Any other value exits 2; with `--spectra` or `--synth-sources`, exits 2; a case with no converter unit that injects, or a unit with a non-positive rating (`rated`) or non-finite output (`dispatched`), exits 1. |
| `--afe-reactance` | - | N149: model every active-front-end converter (the self-commutated units, the `afe-g5` class; line-commutated units are untouched) as resonant plant, ENA EREC G5 Issue 5 s.3.29 p.17 ("may be classified as resonant plant due to their internal impedance"): a shunt of R + jhX to ground at its bus, the reactance X given here in pu on the unit's own rating (`MBase`), converted to the system base. With a converter source (`--converter-sources`) the unit becomes a Norton equivalent, its source in parallel with this impedance; with the other source choices the units are resonant plant only. No value is given by any document on disk (the converter's own coupling reactor or filter, `[VERIFY]`), so there is no default: without the flag converters stay ideal current sources and the study is unchanged. The output adds an `AFE impedance` line (unit count and the impedance). Not finite or not above 0 exits 2; a case with no active-front-end unit, or such a unit with a non-positive rating, exits 1. |
| `--afe-resistance` | `0` | N149: the resistance R in series with `--afe-reactance` (pu on the unit's rating, frequency-independent); 0 is the conservative choice (no converter damping). Negative or not finite exits 2; without `--afe-reactance`, exits 2. |
| `--bus` | worst-THD bus | Bus for the resonance scan and fine sweep; a non-integer value or a bus not in the case exits 2. |
| `--enableExport[=path]` | `results/harmonic-export.json` | Write the `gridsim-harmonic-export/1` document. |

Exit 2 on an unknown case name, a missing or unreadable `--spectra` file, an unknown `--bus`, a bad or contradictory
source choice, or a forward-inference rejection; exit 1 for a missing case file or folder, a spectra component at an
unanalysed order, a triplen source, no injecting converter unit under `--converter-sources`, no active-front-end unit
(or one with no usable rating) under `--afe-reactance`, or a singular Y(h) (no THD
and no verdict are reported); exit 3 when the fundamental power flow does not converge.
