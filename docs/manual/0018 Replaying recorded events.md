# 18. Replaying recorded events

`replay-event` re-simulates a recorded grid disturbance and scores the result against what the record says happened.
The simulation starts from the event's recorded operating point (inertia and demand), applies its recorded losses and
operator actions, carries no device, and compares the simulated rate of change of frequency (RoCoF) and frequency
nadir with the recorded ones. The score is a verdict, `Reproduced`, `Marginal` or `Failed`. The verdict is the
credibility gate for every counterfactual in [ch. 20](<0020 Counterfactuals.md>): `scenario`, `fleet` and
`h0-study` refuse an event whose baseline is not `Reproduced`, and `evidence` withholds every counterfactual section.
A replay presents its output as a reproduction of history, so `replay-event` takes only an event sourced from a record
(see [Provenance of an event](#provenance-of-an-event)). You need this chapter to check an event before
you build on it, to read why a counterfactual refused to run, and to add an event of your own.

## Before you start

- `gridsim` means `dotnet run --project src/GridSim.Cli -c Release --` when GridSim is not installed (see
  [ch. 1](<0001 Getting started.md>)). Run every command in this chapter from the repository root, because the
  trace files and catalogue files are given as paths relative to it.
- Without `--out`, the run directory is `results/replay-<id>` under the current directory. Every example here passes
  `--out results/...` to name it. `results/` is not tracked by git.
- Three events are built into the program. `gridsim replay-event` with no id prints them:

```
$ gridsim replay-event
usage: gridsim replay-event <id> [--catalog <file.json>] [--trace <neso.csv>] [--out <dir>]
Built-in events: gb-2019-08-09, gb-2023-12-22, gb-largest-loss-1320
Reproduces the RECORDED RoCoF/nadir with NO device. A counterfactual is only credible
once the baseline is 'Reproduced'. Placeholder catalog figures are [VERIFY] until sourced.
[exit 2]
```

## Worked example

Replay the 9 August 2019 disturbance, the event GridSim treats as its credibility target:

```bash
gridsim replay-event gb-2019-08-09 --out results/replay-gb-2019-08-09
```

```
$ gridsim replay-event gb-2019-08-09 --out results/replay-gb-2019-08-09

=== Replay: GB 9 Aug 2019 disturbance (Hornsea + Little Barford) ===
  when (UTC):        2019-08-09T15:52:33.0000000Z   provenance: DerivedFromRecorded (event tagged Recorded; the weakest of its parts)
  pre-event inertia: 215.0 GVA.s   demand: 21.1 GW   total loss: 2768 MW
  inferred inertia from recorded RoCoF: 223.7 GVA.s (cross-check vs 215.0)

  RoCoF:  sim  -0.171   recorded  -0.170 Hz/s   (1 % error)
  Nadir:  sim   48.84   recorded   48.80 Hz   (0.04 Hz error)
  Nadir envelope (declared-uncertainty corners): [48.20, 49.17] Hz - recorded INSIDE (consistent within the model's own error bars)
  RMSE vs trace: n/a (no 1-sec NESO trace attached - data-gated)
  assessment: BELOW STATUTORY (49.5 Hz)

  VERDICT: REPRODUCED  - baseline reproduces the record; counterfactuals unlocked.

  -> results\replay-gb-2019-08-09
[exit 0]
```

The simulated RoCoF is 1 % away from the recorded one and the simulated nadir is 0.04 Hz away. Both sit inside the
tolerances (15 % and 0.15 Hz), so the verdict is `Reproduced`, the exit code is 0, and the counterfactual verbs
accept this event. The timestamps in the run metadata and in the file headers differ by machine and run.

## Reading the output

The console report has the same layout for every event.

**Header.** `=== Replay: <title> ===` is the event's title from the catalogue.

**`when (UTC)` and `provenance`.** The event onset (`whenUtc`) and the event's effective provenance: the weakest tag
of all its parts (the event itself, its outcome, every loss, recorded action, response service and demand effect). A
figure computed from the event carries no more trust than its weakest input. When the effective tag differs from the
event's own, the line adds `(event tagged <tag>; the weakest of its parts)`; the built-in recorded events carry
`DerivedFromRecorded` losses, so they replay as `DerivedFromRecorded`. The full set of tags is in
[the provenance chapter](<../technical/0008 Provenance and invariants.md>).

**`pre-event inertia`, `demand`, `total loss`.** The recorded operating point: stored kinetic energy in GVA.s and
transmission-net demand in GW, then the sum of the magnitudes of every loss in the event, in MW. The 2019 event lists
nine losses that sum to 2768 MW. Recorded actions, such as the 892 MW of load disconnection, are not part of the
total.

**`inferred inertia from recorded RoCoF`.** The inertia that the recorded RoCoF implies, `E = f0 * |dP| / (2 * |RoCoF|)`
with `f0` = 50 Hz, set against the event's stated inertia in brackets. `dP` is the sum of the losses that occur
within the first 5 s of the sequence (for 2019: 150 + 737 + 244 + 390 = 1521 MW), so that the later gas-turbine trips
of a staged event do not distort the initial slope. When no loss falls inside 5 s, the total loss is used. This line
is a cross-check on the operating point, not part of the verdict. In 2019 it reads 223.7 GVA.s against a stated
215.0 GVA.s, 4 % apart. When the recorded RoCoF is a windowed figure, it is scaled by the replay's own instantaneous /
windowed ratio before the inversion, and an indented line under it says so: for 2023 the ratio is 1.266 and the line
reads 193.6 GVA.s against 161.0 GVA.s.

**`RoCoF`.** The simulated and recorded RoCoF in Hz/s, and the relative error `||sim| - |rec|| / |rec|`. The
comparison is on magnitudes, so the sign is informational. The simulated figure is the largest absolute RoCoF over the
whole simulated window, which for a staged sequence is the steepest step, not necessarily the first. Its measurement
basis follows the event's `rocofMeasurementWindowS`:

- `0`, the default, declares an instrument figure. The simulation is scored on its 20 ms maximum.
- A positive window `w`, as `1.0` for the 2023 event, declares a figure read off a logger that samples every `w`
  seconds. The simulation is sampled on the same grid. Its statistic is the median, over every sub-second phase of
  that grid, of the largest consecutive-sample slope. A logger's phase against the event is unknown, so the median
  over phases is the unbiased single figure. A continuous sliding-window maximum would always find the steepest
  window and read steeper than any real sampled trace.

**`Nadir`.** The simulated and recorded minimum frequency in Hz, and the absolute difference. The values print to two
decimals, so the 2023 recorded 49.275 Hz prints as 49.27. Only the value is scored. When the record gives the time of
its nadir (`outcome.nadirSeconds`, 75.9 s for 2019) and the simulated nadir falls more than 2.0 s from it
(`ReplayTolerance.NadirTimeToleranceS`), the one-line summary in `summary.md` adds `[nadir time NOT reproduced:
simulated nadir at <s> s, recorded at <s> s, ...]`, so a verdict reached on the value alone is never presented as a
reproduced trajectory.

**`Nadir envelope`.** The same baseline replayed at the model's Best and Worst corners, which scale the three
constants the model has not fitted to a record (load damping, responsive fraction, governor time constant). The
bracket is `[worst, best]`. `INSIDE` means the recorded nadir lies within the model's own error bars. `OUTSIDE` means
that something the corners do not scale, such as the inertia basis, is off. The envelope is reporting only. The
verdict always comes from the nominal run.

**`RMSE vs trace`.** The root-mean-square difference in Hz between the simulated trajectory and a recorded 1-second
trace, with the number of simulated samples scored, or `n/a (no 1-sec NESO trace attached - data-gated)` when no trace
is attached. A trace that ends before the scored window adds a `WARNING: trace ends <s> s before the scored window: ...`
line, and the RMSE covers only the samples the trace reaches; a trace that does not contain the event instant prints
`n/a` and a `WARNING` naming both. See [Replaying against a recorded trace](#replaying-against-a-recorded-trace).

**`back-test vs trace`.** Printed only when a trace covering the window is attached: the acceptance gate of the
back-test (`AssetValidation.Compare`) against the trace, `PASS` or `FAIL`, with the nadir error in Hz, the simulated
and recorded RoCoF on the trace's own 1 s basis, and the simulated and recorded nadir times. The gate is 0.05 Hz on the
nadir, 0.05 Hz/s on the RoCoF and 2.0 s on the nadir time. A `FAIL` caps the verdict at `Marginal`.

**`assessment`.** The GB frequency-limit classification of the simulated response, taken in this order: `LFDD RISK -
demand disconnection` when the nadir is at or below 48.8 Hz; `BELOW STATUTORY (49.5 Hz)` when it is below 49.5 Hz;
`RoCoF OVER <limit> Hz/s` when RoCoF exceeds the active limit of 1.0 Hz/s; `RoCoF OVER legacy 0.125 Hz/s`; `below
operational (49.8 Hz)`; otherwise `within limits`. It describes the simulated trajectory and does not feed the verdict.
The 2019 sim bottoms at 48.84 Hz, four hundredths above the 48.8 Hz line, so it reads `BELOW STATUTORY`, while the
record touched 48.8 Hz and shed load.

**`VERDICT`.** One of `REPRODUCED`, `MARGINAL`, `FAILED`, with a one-line consequence. The last line is the run
directory.

### The verdict ladder

| Verdict | Condition | Exit code | Counterfactual verbs |
|---|---|---|---|
| `Reproduced` | RoCoF error <= 15 % and nadir error <= 0.15 Hz, and with a trace attached an RMSE at or below 0.05 Hz and a back-test `PASS` | 0 | run |
| `Marginal` | not Reproduced, but RoCoF error <= 30 % and nadir error <= 0.30 Hz | 3 | refuse |
| `Failed` | anything else | 3 | refuse |

The comparisons are inclusive. Both errors must satisfy the same rung, so a RoCoF error of 10 % with a nadir error of
0.40 Hz is `Failed`. `Marginal` and `Failed` unlock exactly the same thing, which is nothing. The difference is
diagnostic: `Marginal` says the model is close, `Failed` says it is not. The tolerances are
`ReplayTolerance.Default` (`RocofRelTol` 0.15, `NadirAbsToleranceHz` 0.15, `RmseWarnHz` 0.05) and cannot be changed
from the command line. There is no flag that lets a counterfactual run on a baseline short of `Reproduced`.

### Run directory

```
results/replay-gb-2019-08-09/
|-- run-metadata.json         what ran: commit, backend, machine, runtime, the run description and verdict
|-- baseline-frequency.json   the full baseline swing export: inputs, config, recurrence, headline, trajectory
`-- summary.md                provenance, verdict, one-line summary, inferred inertia
```

`run-metadata.json` for the run above:

```
# results/replay-gb-2019-08-09/run-metadata.json
{
  "timestampUtc": "2026-10-19T17:38:38.5276498Z",
  "backend": "cpu",
  "gitCommit": "v1.95.294-168-g786a2162",
  "buildConfig": "Release",
  "coreVersion": "1.95.462.0",
  "runtime": ".NET 10.0.12",
  "arch": "X64",
  "serverGc": true,
  "simdTier": "Avx256",
  "simdLanes": 4,
  "simdFma": true,
  "machineName": "LAPTOP",
  "os": "Microsoft Windows 10.0.26300",
  "cpu": "Intel(R) Core(TM) i3-N305",
  "logicalCores": 8,
  "ramGb": 8.3,
  "run": "replay-event  id=gb-2019-08-09  provenance=DerivedFromRecorded  verdict=Reproduced"
}
```

The `run` field carries the verdict, so a collection of run directories can be sorted by outcome without opening the
reports. `summary.md` holds the same facts as the console, as a short list:

```
# results/replay-gb-2019-08-09/summary.md
# Replay - GB 9 Aug 2019 disturbance (Hornsea + Little Barford)

    GridSim  -  2026-10-19 17:38:38 UTC
    code:    git v1.95.294-168-g786a2162  |  GridSim.Core v1.95.462.0  |  build Release
    runtime: .NET 10.0.12  X64  GC=Server
    machine: LAPTOP  Microsoft Windows 10.0.26300
    cpu:     Intel(R) Core(TM) i3-N305  -  8 logical cores  -  8 GB RAM
    simd:    Avx256  (4 doubles/step)  FMA=True
    backend: cpu
    run:     replay-event  id=gb-2019-08-09  provenance=DerivedFromRecorded  verdict=Reproduced

- provenance: **DerivedFromRecorded**  (Credibility-gate target event, sourced from the Ofgem investigation report. The curated operating point (215 GVA*s / 21.1 GW) mirrors the measured catalog data/events/gb-2019-08-09-estimated.json.)
- verdict: **Reproduced**
- Reproduced: RoCoF sim -0.171 vs rec -0.170 Hz/s (1 % err); nadir sim 48.84 vs rec 48.80 Hz (0.04 Hz err); RMSE n/a (no trace)
- inferred inertia from recorded RoCoF: 223.7 GVA.s
- baseline swing export: `baseline-frequency.json`
```

`baseline-frequency.json` (schema `gridsim-frequency-export/1`, described in
[ch. 31](<0031 Exports and formats.md>)) is the file to open when you want to hand-check the arithmetic. It
holds the event steps, the exact dynamics configuration the validator used (device terms zero, recorded response
services applied, window extended), the discrete recurrence in plain text, a `headline` block and the full 20 ms
trajectory. For the 2019 run the window is 120 s, the last event at 90 s plus the 30 s tail described below. The
`headline` block reports the 20 ms RoCoF and the nadir with its time. For a windowed event such as 2023 the `headline`
RoCoF (-0.155 Hz/s) is the 20 ms figure, and the verdict uses the 1-second figure on the console (-0.123 Hz/s).

### Exit codes

| Code | Meaning |
|---|---|
| 0 | verdict `Reproduced`, or `--dump-catalog` wrote its file |
| 1 | the event id is not in the catalogue, the catalogue could not be read, or the event is not sourced from a record (its effective provenance is below `DerivedFromRecorded`) |
| 2 | no id given, a `--trace` file that does not exist, an unknown flag or a malformed flag value, or an event rejected by the horizon guard |
| 3 | verdict `Marginal` or `Failed` |

A CI job that gates on `replay-event` therefore passes only on a `Reproduced` baseline. With a trace attached, a result that would be `Reproduced` but has an RMSE above 0.05 Hz or a back-test `FAIL` is `Marginal` (see [Replaying against a recorded trace](#replaying-against-a-recorded-trace)).

## Variations

### A marginal baseline

The 22 December 2023 event is measured end to end: losses from settlement metering, operating point from the recorded
inertia and the estimated demand, response services from the auction record, balancing-mechanism recovery from the
bid-offer record, and the outcome from NESO's public 1-second trace. The built-in event carries no trace path, so the
gate does not read that trace unless you attach it with `--trace` (see
[Replaying against a recorded trace](#replaying-against-a-recorded-trace)).

```bash
gridsim replay-event gb-2023-12-22 --out results/replay-gb-2023-12-22
```

```
$ gridsim replay-event gb-2023-12-22 --out results/replay-gb-2023-12-22

=== Replay: GB 22 Dec 2023 excursion (IFA + Cottam, settlement-measured) ===
  when (UTC):        2023-12-22T13:09:51.0000000Z   provenance: DerivedFromRecorded (event tagged Recorded; the weakest of its parts)
  pre-event inertia: 161.0 GVA.s   demand: 22.4 GW   total loss: 1345 MW
  inferred inertia from recorded RoCoF: 193.6 GVA.s (cross-check vs 161.0)
    (recorded RoCoF is a 1 s windowed figure; inverted after scaling by the replay's instantaneous / windowed ratio 1.266)

  RoCoF:  sim  -0.123   recorded  -0.102 Hz/s   (20 % error)
  Nadir:  sim   48.98   recorded   49.27 Hz   (0.30 Hz error)
  Nadir envelope (declared-uncertainty corners): [48.65, 49.20] Hz - recorded OUTSIDE (a constant the corners don't cover - e.g. the inertia basis - is off)
  RMSE vs trace: n/a (no 1-sec NESO trace attached - data-gated)
  assessment: BELOW STATUTORY (49.5 Hz)

  VERDICT: MARGINAL  - NOT reproduced; do not run counterfactuals until the baseline matches (check [VERIFY] figures / tolerances).

  -> results\replay-gb-2023-12-22
[exit 3]
```

RoCoF is 20 % out and the nadir 0.30 Hz out. Both are inside twice the tolerance, so the verdict is `Marginal` and the
exit code is 3. The recorded nadir lies outside the Best/Worst envelope, so no setting inside the declared corners
closes the gap, and the inferred inertia (193.6 GVA.s) is 20 % above the stated 161.0 GVA.s. The
[2023 dossier](../validation/evidence-gb-2023-12-22.md) infers that the published outturn figure understates the stored energy
by 20 to 40 GVA.s and isolates the residual to the inertia measurement basis. GridSim does not test that inference and does not fit an
inertia value to close the gate. The record carries the published figure, the gate reports `Marginal` against it, and
every counterfactual on this event stays locked. That verdict is the gate working as designed.

### Replaying against a recorded trace

`--trace <file.csv>` attaches a 1-second frequency recording to the replay. The file has two comma-separated columns,
a timestamp and the frequency in Hz (`dtm,f`). Blank lines and a header line are skipped; any other line that is not a
data row is refused with exit 1, naming the line. A timestamp without an offset is read as UTC. The bundled recordings are `data/neso/gb-2019-08-09-frequency.csv` and
`data/neso/gb-2023-12-22-frequency.csv`. With a trace the validator also computes a per-sample RMSE. It aligns the
simulation's first loss with the trace sample nearest `whenUtc`, then compares the 20 ms trajectory with the trace on
every simulated sample from that instant to the end of the window.

**An RMSE above 0.05 Hz, or a back-test `FAIL`, demotes `Reproduced` to `Marginal`.** Headline metrics that match
while the trajectory shape diverges are not a reproduction. The 2019 event shows it:

```bash
gridsim replay-event gb-2019-08-09 --trace data/neso/gb-2019-08-09-frequency.csv --out results/replay-gb-2019-08-09-trace
```

```
$ gridsim replay-event gb-2019-08-09 --trace data/neso/gb-2019-08-09-frequency.csv --out results/replay-gb-2019-08-09-trace

=== Replay: GB 9 Aug 2019 disturbance (Hornsea + Little Barford) ===
  when (UTC):        2019-08-09T15:52:33.0000000Z   provenance: DerivedFromRecorded (event tagged Recorded; the weakest of its parts)
  pre-event inertia: 215.0 GVA.s   demand: 21.1 GW   total loss: 2768 MW
  inferred inertia from recorded RoCoF: 223.7 GVA.s (cross-check vs 215.0)

  RoCoF:  sim  -0.171   recorded  -0.170 Hz/s   (1 % error)
  Nadir:  sim   48.84   recorded   48.80 Hz   (0.04 Hz error)
  Nadir envelope (declared-uncertainty corners): [48.20, 49.17] Hz - recorded INSIDE (consistent within the model's own error bars)
  RMSE vs trace: 0.099 Hz over 5996 sample(s)
  back-test vs trace (gate 0.05 Hz / 0.05 Hz/s / 2.0 s): FAIL  nadir 0.054 Hz, RoCoF sim -0.154 vs rec -0.151 Hz/s, nadir time sim 76.0 s vs rec 76.1 s (0.1 s)
  assessment: BELOW STATUTORY (49.5 Hz)

  VERDICT: MARGINAL  - NOT reproduced; do not run counterfactuals until the baseline matches (check [VERIFY] figures / tolerances).

  -> results\replay-gb-2019-08-09-trace
[exit 3]
```

RoCoF and nadir errors are unchanged at 1 % and 0.04 Hz, but the RMSE is 0.099 Hz and the back-test fails on the nadir
(0.054 Hz against the 0.05 Hz gate), so the verdict drops to `Marginal` and the exit code to 3. A single-mass swing model driven by nine staged power steps reproduces the headline physics of
the excursion and not its sample-by-sample shape. The built-in 2019 event carries no trace path, so its
headline gate stays the operative one. The [2019 dossier](../validation/evidence-gb-2019-08-09.md) discusses the shape gap.

A trace can only move a verdict downwards. On the 2023 event it adds the RMSE and back-test lines and leaves the verdict at
`Marginal`:

```
$ gridsim replay-event gb-2023-12-22 --trace data/neso/gb-2023-12-22-frequency.csv --out results/replay-gb-2023-12-22-trace

=== Replay: GB 22 Dec 2023 excursion (IFA + Cottam, settlement-measured) ===
  when (UTC):        2023-12-22T13:09:51.0000000Z   provenance: DerivedFromRecorded (event tagged Recorded; the weakest of its parts)
  pre-event inertia: 161.0 GVA.s   demand: 22.4 GW   total loss: 1345 MW
  inferred inertia from recorded RoCoF: 193.6 GVA.s (cross-check vs 161.0)
    (recorded RoCoF is a 1 s windowed figure; inverted after scaling by the replay's instantaneous / windowed ratio 1.266)

  RoCoF:  sim  -0.123   recorded  -0.102 Hz/s   (20 % error)
  Nadir:  sim   48.98   recorded   49.27 Hz   (0.30 Hz error)
  Nadir envelope (declared-uncertainty corners): [48.65, 49.20] Hz - recorded OUTSIDE (a constant the corners don't cover - e.g. the inertia basis - is off)
  RMSE vs trace: 0.281 Hz over 7926 sample(s)
  back-test vs trace (gate 0.05 Hz / 0.05 Hz/s / 2.0 s): FAIL  nadir 0.295 Hz, RoCoF sim -0.123 vs rec -0.102 Hz/s, nadir time sim 49.0 s vs rec 20.5 s (28.5 s)
  assessment: BELOW STATUTORY (49.5 Hz)

  VERDICT: MARGINAL  - NOT reproduced; do not run counterfactuals until the baseline matches (check [VERIFY] figures / tolerances).
...
```

A catalogue event can carry its own trace path in `outcome.nesoTraceCsvPath`, and `--trace` overrides it.

### The measured 2019 catalogue

`data/events/gb-2019-08-09-estimated.json` carries the same event with the operating point taken from the state
estimator: demand 21,147 MW (the built-in rounds it to 21,100 MW) and onset 15:52:00 UTC. Load it with `--catalog`:

```bash
gridsim replay-event gb-2019-08-09 --catalog data/events/gb-2019-08-09-estimated.json --out results/replay-estimated
```

```
$ gridsim replay-event gb-2019-08-09 --catalog data/events/gb-2019-08-09-estimated.json --out results/replay-estimated

=== Replay: GB 9 Aug 2019 disturbance (Hornsea + Little Barford) - measured operating point ===
  when (UTC):        2019-08-09T15:52:00.0000000Z   provenance: DerivedFromRecorded
  pre-event inertia: 215.0 GVA.s   demand: 21.1 GW   total loss: 2768 MW
  inferred inertia from recorded RoCoF: 223.7 GVA.s (cross-check vs 215.0)

  RoCoF:  sim  -0.171   recorded  -0.170 Hz/s   (1 % error)
  Nadir:  sim   48.84   recorded   48.80 Hz   (0.04 Hz error)
  Nadir envelope (declared-uncertainty corners): [48.20, 49.17] Hz - recorded INSIDE (consistent within the model's own error bars)
  RMSE vs trace: n/a (no 1-sec NESO trace attached - data-gated)
  assessment: BELOW STATUTORY (49.5 Hz)

  VERDICT: REPRODUCED  - baseline reproduces the record; counterfactuals unlocked.
...
```

Both catalogues reproduce the record. The title, the onset and the provenance line differ (the measured catalogue is
itself tagged `DerivedFromRecorded`); RoCoF, nadir and the declared envelope (`[48.20, 49.17]`) agree to the printed
digits. The file
`data/events/gb-events.json` is the built-in catalogue as `replay-event --dump-catalog` writes it: `gb-2019-08-09`,
`gb-2023-12-22` and `gb-largest-loss-1320`, field for field, so `--catalog data/events/gb-events.json` replays the same
events as the built-ins.

### A screening event

`gb-largest-loss-1320` is a generic single 1,320 MW infeed trip on a nominal operating point, titled for the SQSS
normal infeed loss risk. Every figure, the recorded outcome included, is a `CuratedPlaceholder` marked `[VERIFY]`, so
the event is effectively `CuratedPlaceholder`. `replay-event gb-largest-loss-1320` refuses it with exit 1 and writes
nothing: `error: event 'gb-largest-loss-1320' (...) is effectively CuratedPlaceholder (its weakest part: the event
itself (CuratedPlaceholder)): a replay presents its output as a reproduction of a recorded event, so only an event
sourced from a record (Recorded or DerivedFromRecorded throughout) is replayed ...`.

The study verbs (`scenario`, `fleet`, `evidence`, `h0-study`) take it as a design-basis input. They run the same gate
on it, which reports `Reproduced` because the model agrees with the placeholder outcome: a consistency check that
grounds nothing. Their console output adds an `input provenance: [CuratedPlaceholder]` line, and every figure they
write carries that tag. Use this event to explore a device, never to quote a result.

### Provenance of an event

The effective provenance of an event is the weakest tag of its parts. `replay-event` runs only on an event whose
effective provenance is `Recorded` or `DerivedFromRecorded`; anything weaker (`CuratedPlaceholder`, `Synthetic`,
`Interpolated`, or a recorded event with one placeholder part) exits 1 naming the weakest part. A part tagged as an
output (`Counterfactual` or `UnvalidatedAtScale`) is refused as an input by name on every event verb. The baseline
export a replay writes is a simulation of the event, so it is tagged at best `DerivedFromRecorded`, and never above
the event's effective tag.

## The event catalogue

A catalogue is a JSON file with the schema tag `gridsim-event-catalog/1` and a list of events. Every figure carries a
provenance tag, and every figure not taken from a primary source stays `CuratedPlaceholder` with `[VERIFY]` in its
text. An event holds these fields.

| Field | Meaning |
|---|---|
| `id`, `title` | The id is the argument of `replay-event` and the `--event` of the counterfactual verbs, matched without regard to case. The title prints in every report. |
| `whenUtc` | The onset, UTC. It must be at least 169 hours old (see [The horizon rule](#the-horizon-rule)). |
| `preEventInertiaMwS` | Stored kinetic energy before the event, in MW.s (215,000 is 215 GVA.s). |
| `preEventDemandMw` | Transmission-net demand before the event, in MW. |
| `losses` | The generation losses. Each has `atSeconds` from the onset, `deltaPMw` (negative for a loss), a `what` that cites its source, and a `provenance`. |
| `recordedActions` | Operator or protection actions the record shows, in the same form plus a required `kind`. A positive `deltaPMw` is load removed. `DemandDisconnection` (LFDD, a consequence of the frequency itself) is replayed in the baseline and left out of every counterfactual; `OperatorInstruction` (for example the balancing-mechanism recovery of 2023) is kept in every counterfactual too, because the lost energy is replaced with or without a device. A catalogue entry without a `kind` is refused. |
| `outcome` | The headline figures the replay must reproduce: `initialRocofHzPerS`, `nadirHz`, `settleHz`, `lfddMwShed`, `provenance`, `notes`, an optional `nesoTraceCsvPath`, `rocofMeasurementWindowS` (`0` for an instrument figure, `1.0` for a 1-second logger figure), and an optional `nadirSeconds`, the time of the recorded nadir on the event clock (reporting only). |
| `provenance`, `notes` | The tag and the source citations for the event as a whole. |
| `responseServices`, `responseServicesComplete` | The frequency-response services the system held, each with `mw`, `deadbandHz`, `fullDeliveryDeviationHz`, `timeConstantS`, `what`, `provenance` and an optional low-gain knee (`kneeDeviationHz`, `kneeFraction`). `responseServicesComplete: true` declares the list to be all the response the system held. |
| `preEventFrequencyHz` | The frequency the trip landed on, read off the trace. When set, the replay starts in equilibrium there. |
| `demandEffect` | Optional: the demand reduction the record gives per Hz of frequency fall, `mwPerHz` (above 0), `what` and `provenance`. When set, the swing model's load damping is set to this figure in MW per Hz in the baseline and in every counterfactual. |

**Response services.** Recent GB events are arrested by procured sub-second response (Dynamic Containment, Dynamic
Moderation, Dynamic Regulation), not by governor droop. An event can carry the service volumes the system held, taken
from the auction record, each on its published delivery shape. The replay integrates each as a proportional responder.
When `responseServicesComplete` is true, as it is for GB events after the November 2023 go-live of the enduring
auction capability and for 9 August 2019 (whose ESO report lists the whole low-frequency response held), the generic
governor term is switched off so that response the record already lists is not counted
twice. Counterfactuals keep the services as well. A device is evaluated on top of what the real system held, never as
a replacement for it.

**Pre-event frequency.** When an event records the frequency the trip landed on, the replay starts at that frequency
with the services partly deployed on their published curves and the standing imbalance carried as the balancing
mechanism was carrying it. Recorded balancing-mechanism dispatch enters as recorded actions, by the same route as
LFDD.

**The window.** The simulated window always covers the whole recorded sequence: it runs to the time of the last
event plus 30 s, and never shorter than the 30 s default. The 2019 event plays out over 90 s, so a clipped window would
replay a different event than the record describes. `scenario`, `fleet`, `evidence` and `h0-study` use the same rule,
so they agree with the baseline about what covers an event.

### The built-in events

| Id | What it is | Verdict |
|---|---|---|
| `gb-2019-08-09` | The 9 August 2019 disturbance (Hornsea 1 and Little Barford). Nine losses, the 892 MW LFDD action and the outcome come from the Ofgem investigation report (January 2020), paragraphs 2.4.1 to 2.4.16, and each loss cites its paragraph. Operating point: 215 GVA.s (NESO recorded outturn for the half-hour) and 21,100 MW. The response the system delivered comes from the ESO report's Table 3 (six services, 1,090 MW delivered, listed as complete), and its demand effect, 700 MW/Hz, from the same report. Outcome: -0.17 Hz/s (E3C interim trace; Ofgem records only more than 0.125 Hz/s), nadir 48.8 Hz at 75.9 s, 892 MW shed. | `Reproduced` |
| `gb-2023-12-22` | The 22 December 2023 excursion (IFA and Cottam), measured end to end. IFA -1,000 MW and Cottam -345 MW from settlement metering; 161 GVA.s and 22,400 MW; 49.879 Hz pre-event; DCL 873, DML 150 and DRL 203 MW from EAC auction 70; balancing-mechanism recovery of +904 MW by +129 s as recorded actions. Outcome from NESO's public 1-second trace: -0.102 Hz/s as a 1-second figure, nadir 49.275 Hz, no LFDD. | `Marginal` |
| `gb-largest-loss-1320` | The screening case described above. | refused by `replay-event`; `Reproduced` against placeholders in the study verbs |

The sourcing behind the first two is in the [2019](../validation/evidence-gb-2019-08-09.md) and
[2023](../validation/evidence-gb-2023-12-22.md) dossiers.

## Authoring an event

1. Dump the built-in catalogue as a starting point:

   ```bash
   gridsim replay-event --dump-catalog results/dump-events.json
   ```

```
$ gridsim replay-event --dump-catalog results/dump-events.json
wrote built-in catalog (3 events) -> results\dump-events.json
[exit 0]
```

   The dump holds all three events. It also writes a `totalLossMw` field on each, which is computed on load and
   ignored.

2. Cut the file down to your event and set its fields. This is a complete catalogue that holds one illustrative event,
   saved as `results/my-events.json` (the copy used here is `docs/_captures/events/my-events.json`). Every figure in it
   is a placeholder, and it describes no real event, so `replay-event` refuses it until its figures are sourced and
   tagged `Recorded` or `DerivedFromRecorded` (see [Provenance of an event](#provenance-of-an-event)); the study verbs
   take it as it is:

```json
{
  "schema": "gridsim-event-catalog/1",
  "events": [
    {
      "id": "example-1000",
      "title": "Illustrative 1000 MW infeed loss [VERIFY]",
      "whenUtc": "2022-03-15T12:00:00Z",
      "preEventInertiaMwS": 180000,
      "preEventDemandMw": 30000,
      "losses": [
        {
          "atSeconds": 1,
          "deltaPMw": -1000,
          "what": "single infeed trip [VERIFY]",
          "provenance": "CuratedPlaceholder"
        }
      ],
      "recordedActions": [],
      "outcome": {
        "initialRocofHzPerS": -0.139,
        "nadirHz": 49.4,
        "settleHz": 49.8,
        "lfddMwShed": 0,
        "provenance": "CuratedPlaceholder",
        "notes": "Illustrative figures, not a real event [VERIFY].",
        "rocofMeasurementWindowS": 0
      },
      "provenance": "CuratedPlaceholder",
      "notes": "Illustrative example for the manual. Every figure is a placeholder [VERIFY].",
      "responseServicesComplete": false
    }
  ]
}
```

3. Check it against the gate. While its figures are placeholders,
   `replay-event example-1000 --catalog results/my-events.json` refuses it with exit 1, naming the weakest part.
   `scenario` runs the same gate on it as a design-basis input:

   ```bash
   gridsim scenario --event example-1000 --catalog results/my-events.json --out results/scenario-example-1000
   ```

   The report opens with the `input provenance: [CuratedPlaceholder]` line and `baseline gate: Reproduced`, and every
   figure it writes carries the placeholder tag.

A verdict on placeholder figures says nothing about the world: here it is `Reproduced` only because the recorded
outcome was written to be consistent with the model. The gate measures the model against the outcome you wrote, so the
outcome must come from a source. Replace each `CuratedPlaceholder` with a sourced figure and tag it `Recorded` or
`DerivedFromRecorded`; `replay-event` then replays it. If the verdict then falls short, the finding is about the model
or the operating point, as the 2023 event shows. Do not edit the recorded outcome to make the verdict turn green.
`Marginal` and `Failed` are the correct output when a record and the model disagree.

### A failed baseline

Copy `my-events.json` to `results/off-event.json`, change the id to `example-off`, and set the recorded
`initialRocofHzPerS` to -0.2 and `nadirHz` to 49.0. The model cannot produce those figures from 180 GVA.s and a
1,000 MW loss, and the ladder drops to `Failed` (the file used here is `docs/_captures/events/off-event.json`):

```bash
gridsim scenario --event example-off --catalog results/off-event.json --out results/scenario-example-off
```

The study refuses to run counterfactuals and exits 3. Standard error carries `GATE FAILED: Refusing to run
counterfactuals: the no-device baseline is 'Failed', not 'Reproduced'. Reproduce reality first.` followed by the
replay summary (RoCoF and nadir, simulated against recorded, with their errors). The recorded RoCoF alone already
implies `E = 50 x 1000 / (2 x 0.2)` = 125.0 GVA.s against the stated 180.0, so the recorded RoCoF and the stated
inertia disagree before any dynamics are involved.

### The horizon rule

GridSim replays history only. Every timestamp it reads is checked against the forward-inference horizon, and a
timestamp newer than 169 hours (7 days and 1 hour) before the present is rejected. The check sits at the point where
data enters: an event's `whenUtc` is checked when a catalogue loads, the built-in events included, and a trace sample
is checked as the trace is read. The horizon is `now - 169 h`, so it moves with the clock. An event is accepted once its
stamp is 169 hours old, so an event dated yesterday is rejected today and accepted after 6 days and 1 hour. There is no flag that disables it.

Replay an event dated 2 October 2026 (the capture was made on 3 October 2026):

```bash
gridsim replay-event example-recent --catalog results/recent-event.json --out results/replay-example-recent
```

```
$ gridsim replay-event example-recent --catalog results/recent-event.json --out results/replay-example-recent
REJECTED (forward inference): event 'example-recent' timestamp 2026-10-02T12:00:00.0000000Z is newer than the historical horizon 2026-09-26T07:16:48.3517197Z (data must lag real time by >=169 h). GridSim replays history only - it never processes recent or future data, so it cannot forward-infer.
[exit 2]
```

The message names the event, its timestamp and the horizon that applied. It goes to standard error, the exit code is 2,
and no run directory is written. The horizon timestamp in the message differs on every run. The check covers the whole
file, so one recent event in a catalogue stops every replay from that file, including a replay of an older event in the
same file. `scenario`, `fleet`, `evidence` and `h0-study` load events the same way and reject the same way, with the
same message and exit code.

## Pitfalls

- **`REJECTED (forward inference)` and exit 2.** An event in the catalogue is newer than 169 hours. Correct its
  `whenUtc`, or remove the event. You cannot replay a recent event. A catalogue that you edited this week with an event
  from this week is the usual cause.
- **Exit 3 with a printed report.** The verdict is `Marginal` or `Failed`. The report is complete and the run directory
  is written, but the counterfactual verbs refuse the event until the baseline is `Reproduced`. A script must read the
  exit code, not look for a report.
- **An RMSE demotion looks like a regression.** The same event moves from exit 0 to exit 3 when you add `--trace`.
  The headline errors have not changed. See [Replaying against a recorded trace](#replaying-against-a-recorded-trace).
- **A placeholder event exits 1.** `replay-event` refuses an event that is not sourced from a record, naming its
  weakest part. Source and re-tag the figures, or study the event with `scenario`, `fleet`, `evidence` or `h0-study`.
- **An unknown id exits 1, not 2.** `error: Event 'nope' not found. Available: ...` lists the ids in the catalogue that
  was read. Check that `--catalog` points to the file you meant, because a catalogue file replaces the built-ins
  rather than extending them.
- **A missing trace exits 2.** `error: --trace file not found: <path>` is printed instead of an RMSE line, so a
  mistyped path never silently drops the per-sample check. A path is relative to the current directory.
- **A flag meant for another verb exits 2.** `replay-event` accepts only `--catalog`, `--trace`, `--out` and
  `--dump-catalog`; `replay-event gb-2023-12-22 --device-ffr 1` prints
  `error: replay-event does not use --device-ffr (it belongs to another verb, so it would be silently ignored). ...`.
  `--forming` and `--asset-dynamics` are refused the same way.
- **Default output goes under `results/` in the current directory.** Pass `--out` for
  any run you intend to keep.
- **The 2023 verdict is a result.** `Marginal` on `gb-2023-12-22` is a finding about the model, not a fault to repair by
  editing the event.

## Limits

- A trace shorter than the simulated window gives an RMSE over the part it covers only (with a `WARNING` line), so two
  RMSE figures over different coverage are not comparable.
- The tolerances, the RMSE bar and the 5 s inertia cross-check window are fixed in the program and have no flags.
- The model is a single-mass swing equation with three unfitted constants. It reproduces headline RoCoF and nadir, not
  the sample-by-sample shape of a long excursion (2019 RMSE 0.099 Hz).
- Only the three built-in events ship with a gate result. The scoring of any other event depends on the sourcing of the
  figures you supply.
- `--inertia-model distributed` is not honoured by `replay-event`. It prints a warning and runs the lumped scalar model.

## See also

- [ch. 20, Counterfactuals](<0020 Counterfactuals.md>) - `scenario`, `fleet`, `evidence` and `h0-study`, which run only on a `Reproduced` baseline (`evidence` still writes a report without the counterfactual sections)
- [ch. 14, Frequency studies](<0014 Frequency studies.md>) - the swing model these replays run on, and the H=0 study
- [ch. 19, CLI reference](<0019 CLI reference.md#replay-event>) - every `replay-event` flag
- [ch. 31, Exports and formats](<0031 Exports and formats.md>) - the event-catalogue and frequency-export schemas
- [Evidence engine](<../technical/0007 The evidence engine.md>) - the event model, the validator and the gate as implemented
- [Provenance and invariants](<../technical/0008 Provenance and invariants.md>) - provenance tags, `[VERIFY]` and the forward-inference guard
- [9 August 2019 dossier](../validation/evidence-gb-2019-08-09.md) - the sourced event record and its replay
- [22 December 2023 dossier](../validation/evidence-gb-2023-12-22.md) - the measured event record and the inertia-basis finding
