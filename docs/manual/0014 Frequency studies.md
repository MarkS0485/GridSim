# 14. Frequency studies

A frequency study answers four questions about a loss of generation or infeed. How fast does frequency fall
straight after the loss (the RoCoF, in Hz/s)? How low does it go before the response arrests it (the nadir)? Where
does it settle? Which services (governor droop, Dynamic Containment, Dynamic Moderation, Dynamic Regulation) did the
arresting? GridSim answers them with five verbs and one live view. `freq` and `replay` read a recorded frequency trace.
`transient` runs a case through a time-domain simulation with an optional infeed trip. `domains` reports how the
generator fleet divides into speed domains for the distributed inertia model. `h0-study` sweeps a recorded event
toward zero synchronous inertia with a growing grid-forming converter fleet. The desktop app plays the same
physics live. This chapter shows each verb on real output, explains every line it prints, and states what each
verb does with the inertia model and the response services.

## Which verb answers which question

| Question | Use | Chapter |
|---|---|---|
| Did a recorded NESO trace breach the GB limits, and how steep was its worst RoCoF? | `freq` | this chapter |
| Play a recorded trace back at 20 ms, slice by slice. | `replay` | this chapter |
| What RoCoF and nadir does a case give when an infeed of N MW is lost at time T, with a full time series and solve exports? | `transient` | this chapter |
| One generator trips: RoCoF, nadir and settling frequency, plus the same trip at half the inertia. | `trip` | [contingency analysis](<0011 Contingency analysis.md>) |
| How does the fleet divide into speed domains, and does one domain dominate? | `domains` | this chapter |
| Does the model reproduce a recorded event? | `replay-event` | [replaying recorded events](<0018 Replaying recorded events.md>) |
| What happens to a recorded event with a device, or with less inertia? | `scenario`, `fleet`, `evidence` | [counterfactuals](<0020 Counterfactuals.md>) |
| What if the synchronous inertia goes to zero and converters form the grid? | `h0-study` | this chapter, then [counterfactuals](<0020 Counterfactuals.md>) |
| Trip a generator by hand and watch frequency live. | the desktop app | [the desktop app](<0008 The desktop app.md>) |
| Rotor-angle stability, machine by machine. | `stability` | [ch. 19, CLI reference](<0019 CLI reference.md#stability>) |
| Sub-cycle waveforms, not frequency. | `emt` | [electromagnetic transients](<0016 Electromagnetic transients (EMT).md>) |

The frequency verbs model the electromechanical timescale: seconds to minutes, one frequency for the system (or one
per speed domain), with the network held at its steady-state solution. Waveform behaviour inside a cycle is outside
them and belongs to the EMT chapter. The equations are in
[the frequency dynamics technical chapter](<../technical/0005 Frequency dynamics.md>).

## Before you start

- `freq` and `replay` read a NESO frequency CSV with one `timestamp,hertz` row per line and a `dtm,f` header. Blank
  lines are skipped, and so is the first non-blank line when it is not a data row and its first field holds no digit
  (the header). Every other line must be a timestamp, a comma and a finite frequency above 0 Hz; any other line stops
  the read with exit 1 and a message naming the file, the line number and the line. Timestamps are read as UTC. The bundled
  files are `data/neso/frequency-sample.csv` (300 s of ambient frequency on 2024-08-01, the default),
  `data/neso/gb-2019-08-09-frequency.csv` and `data/neso/gb-2023-12-22-frequency.csv`.
- Every timestamp is checked against the forward-inference guard: GridSim reads history only, so a row newer than 169
  hours before now is refused (see [Pitfalls](#pitfalls)).
- The relative file name `data/neso/...` resolves against the directory that holds the executable for the default
  input, and against the current directory for a path you type.
- `transient`, `h0-study` and `replay-event` write into `results/` under the current directory unless `--out` names a
  directory (see [Run directories](#run-directories)).
- Timings, stamps and run-directory names in the captured output differ by machine and by run.
- The CLI prints a run directory as an absolute path. The captures in this chapter are written by
  `tools/docs/capture.ps1`, which removes the repository root from every path, so a run directory appears
  relative to the repository (`results\transient-uk-...`, `results\h0-study-...`).
- A capture is the standard output of the run followed by its standard error. On a terminal the two streams
  interleave in the order written, so a `transient` run shows its progress line (standard error) before its `done in`
  line (standard output); the captured blocks list the `done in` line first.

## Worked example

### Score a recorded excursion

On 22 December 2023 an interconnector trip and a power-station trip pulled GB frequency down to 49.275 Hz. NESO's
one-second trace for that window is bundled. `freq` scores it against the GB limits:

```bash
gridsim freq data/neso/gb-2023-12-22-frequency.csv
```

```
$ gridsim freq data/neso/gb-2023-12-22-frequency.csv
NESO frequency  -  gb-2023-12-22-frequency.csv
================================================================
Samples      601  (1 s cadence)
Frequency    min 49.275 Hz  mean 49.778 Hz  max 49.930 Hz
Max |RoCoF|  0.1020 Hz/s  at 2023-12-22 13:09:53

vs GB limits:
  RoCoF 0.125 Hz/s: within  (worst 0.1020 Hz/s)
  operational 49.8 Hz: 299 s below    statutory 49.5 Hz: 60 s below    LFDD 48.8 Hz: clear

Upsample demo  -  1 s frames expanded to 20 ms by RoCoF interpolation:
  time            Hz         RoCoF (Hz/s)
  13:07:00.000   49.93000    -0.0080
  13:07:00.020   49.92984    -0.0080
  13:07:00.040   49.92968    -0.0080
  13:07:00.060   49.92952    -0.0080
  13:07:00.080   49.92936    -0.0080
  13:07:00.100   49.92920    -0.0080
  13:07:00.120   49.92904    -0.0080
  13:07:00.140   49.92888    -0.0080
[exit 0]
```

The trace spent 299 samples below the 49.8 Hz operational floor and 60 below the 49.5 Hz statutory floor, never
reached the 48.8 Hz demand-disconnection stage, and its steepest one-second fall was 0.1020 Hz/s, inside the 0.125
Hz/s limit. The minimum, 49.275 Hz, and the 0.1020 Hz/s are the two numbers the event record holds as the
outcome that `replay-event` and the counterfactual verbs try to reproduce.

### Simulate a loss of infeed

`transient` runs a case forward in time at a fixed slice, tripping an infeed part-way through. This run takes the
reduced GB model (`uk`, 171.7 GVA.s of inertia), runs it for one simulated minute in 100 ms slices, and trips
1,000 MW at 10 s:

```bash
gridsim transient uk --minutes 1 --dt-ms 100 --trip 1000 --trip-at 10
```

```
$ gridsim transient uk --minutes 1 --dt-ms 100 --trip 1000 --trip-at 10
[transient] uk: done in 1.4s  nadir 49.448Hz  maxRoCoF -0.147Hz/s  exports 42  -> results\transient-uk-20261019-183826
[transient] uk: 600 slices @ 100ms (1 min) on Newton-Raphson (dense)...
[exit 0]
```

The run lost 1,000 MW of a 39,500 MW system carrying 171.7 GVA.s. The frequency fell at 0.147 Hz/s, reached 49.448 Hz
at t=17 s and was recovering at the end of the minute (49.769 Hz at t=60 s). The first line of the block is
written to standard output when the run ends; the second is the progress line on standard error, which the run writes
first, so a terminal shows the progress line before the `done` line (see [Before you start](#before-you-start)). All
600 slices converged, so the exit code is 0.

## Reading the output

### freq

- **Title line.** `NESO frequency  -  <file name>` names the file read, without its directory.
- **Samples.** The number of parsed rows. The text `(1 s cadence)` is fixed; `freq` does not measure the cadence.
- **Frequency.** The minimum, mean and maximum over every sample, to three decimals.
- **Max |RoCoF|.** The largest absolute value of `(f2 - f1) / dt` over consecutive sample pairs, and the timestamp of
  the later sample of that pair. A trace with one-second rows gives the one-second average slope, not an
  instantaneous one.
- **RoCoF 0.125 Hz/s.** `within` or `EXCEEDED`, with the worst figure repeated. 0.125 Hz/s is the legacy GB
  loss-of-mains relay setting (`FrequencyLimits.RocofLimitLegacyHzPerS`). The modern 1.0 Hz/s setting is not printed
  by this verb.
- **operational 49.8 Hz, statutory 49.5 Hz.** `held` when no sample is below the floor, otherwise `N s below`, where N
  is the count of samples strictly below it. N equals seconds only at a one-second cadence. The upper limits are on the
  `over-frequency` line below.
- **LFDD 48.8 Hz.** `clear`, or `REACHED` when any sample is at or below the first low-frequency demand-disconnection
  stage.
- **over-frequency.** Printed only when some sample is above 50.2 Hz: `operational 50.2 Hz: N s above` and
  `statutory 50.5 Hz: held` or `N s above`, counted as for the low side. A trace that stays at or below 50.2 Hz has no
  such line.
- **Upsample demo.** The first three samples expanded to 20 ms: 50 sub-samples per second. Each interval's RoCoF is
  `(f2 - f1) / dt` and the sub-samples follow `f(t) = f1 + RoCoF * t`, so the RoCoF column holds one value for each
  1 s interval. In the capture the first interval falls at -0.0080 Hz/s, and 49.93000 - 0.0080 * 0.02 gives the 49.92984 of
  the second row. `replay` plays these frames.

### replay

`replay` prints no statistics headlessly; it plays frames. A five-frame headless run:

```bash
gridsim replay --frames 5
```

```
$ gridsim replay --frames 5
Loading frequency-sample.csv (600 s) and upsampling to 20 ms...
[     0/14951] 00:00:00.000  50.1050 Hz  RoCoF  0.0010  @50 sps
[     1/14951] 00:00:00.020  50.1050 Hz  RoCoF  0.0010  @50 sps
[     2/14951] 00:00:00.040  50.1050 Hz  RoCoF  0.0010  @50 sps
[     3/14951] 00:00:00.060  50.1051 Hz  RoCoF  0.0010  @50 sps
[     4/14951] 00:00:00.080  50.1051 Hz  RoCoF  0.0010  @50 sps
[exit 0]
```

- **Loading line.** The file name and the `--seconds` value (default 600). It reads `--seconds` + 1 samples, or all
  of them when the file is shorter, so the `(600 s)` is the limit and not the file length. The sample file holds 300
  samples, which is 299 intervals of 50 frames plus one closing frame: 14,951 frames.
- **`[index/count]`.** The zero-based frame index and the total frame count.
- **Time, Hz, RoCoF.** The frame's timestamp, its frequency to four decimals, and the RoCoF of its source interval in Hz/s.
- **`@50 sps`.** The playback rate in slices per second, after clamping to the range 0.25 to 2,000. Headless mode does
  not pace itself, so the rate is a label there.

Headless mode stops after the requested number of frames or at the end of the data, whichever is first, and exits 0.
The interactive form (no `--frames`) is a console display: a title line, the timestamp and slice counter, a gauge from
49.5 Hz to 50.5 Hz with `|` at 50 Hz and `#` at the current frequency, the frequency with its deviation from 50 Hz
and the RoCoF, and a progress bar. The gauge is green inside 0.10 Hz of nominal, dark yellow inside 0.20 Hz,
yellow inside 0.50 Hz and red beyond. Keys: space plays or pauses, up and down (or `+` and `-`) double or halve
the speed, left and right step one frame and pause, Home restarts, and Q or Esc quits. At the end of the data the
display pauses.

### transient

**Console summary.** `done in 1.4s` is wall-clock time. `nadir 49.448Hz` is the lowest frequency over the run.
`maxRoCoF -0.147Hz/s` is the RoCoF of largest magnitude, with its sign (negative is falling). `exports 42` is the
number of full solve exports written, and the path is the run directory. The progress line on standard error names
the case, slice count, step, duration and engine, and repeats every 100,000 slices on long runs.

**What each slice does.** The case is solved by power flow, warm-started from the last converged state, and the
frequency integrator then advances one step against the net imbalance. Loads are held constant (no daily demand
curve). Three things move the frequency:

- **The trip.** At `--trip-at` the lost MW enters the integrator as a deficit. The deficit decays with a 300 s time
  constant of simulated time, the stand-in for reserve and redispatch picking up the loss (`SecondaryTauS` in
  `src/GridSim.Core/Simulation/LiveSim.cs`). The inertia of the tripped unit stays in the system energy, and the
  power flow does not see the trip: `gen_mw` and `demand_mw` do not change.
- **Ambient jitter.** A small random load imbalance of up to 0.08 % of base demand (`AmbientNoiseFraction`) is added
  each slice so the dial moves in steady state. The generator is seeded with a fixed value (`LiveSimSeed`), so a rerun
  gives the same sequence. A run with no trip therefore shows a nadir at the case's nominal frequency (50.000 Hz on a
  50 Hz case; `case9` is a 60 Hz system and runs at 60 Hz). The largest RoCoF is the amplitude
  `AmbientNoiseFraction * demand * f0 / (2E)`; the captured runs print -0.001 Hz/s (`case9`, 10 ms slices) and
  -0.005 Hz/s (`uk`, 100 ms slices).
- **Response.** The integrator carries governor droop (5 % droop on 20 % of demand with an 8 s lag) and load damping
  (1 % of demand per Hz). It carries no Dynamic Containment, Moderation or Regulation. See
  [Response services](#response-services) and [How far to trust a nadir](#how-far-to-trust-a-nadir).
- **Grid-forming and asset dynamics.** `--forming` (with `--forming-inertia` and `--forming-damping`) runs the
  integrator's grid-forming kernel, and `--asset-dynamics` runs the asset-aware integrator on the case's asset bundle:
  inverter and protection trips are taken from the running dispatch, and the balance they remove is restored over the
  same 300 s secondary time constant as the trip. `--asset-dynamics` on a case with no asset bundle exits 1. The run
  description in `run-metadata.json` and the file headers records both switches.
- **Machine data.** `--dyn-data <file>` gives the machines the file binds the file's inertia `H`; its reactances,
  exciters and governors are not part of this model, and a `[transient]` line on standard error says so. A batch run
  (`--cases`) refuses it with exit 2.

**Inertia.** The system energy is the sum of `H x MBase` over the rotating machines (a converter-type unit adds nothing,
whatever `H` its data carries). A case that carries no inertia data (every
MATPOWER case) has less than 100 MW.s, and `transient` then substitutes 4 s on the generators' MVA base
(`LiveSolveSettings.FallbackInertiaH`, `MinInertiaMwS`); read the frequency of such a case as a demonstration. The
reduced GB model carries its own data. A step longer than 20 ms is integrated in sub-steps of at most 20 ms (capped at
4,000 sub-steps per slice), so a 100 ms slice is five sub-steps. The RoCoF logged for a slice is the one at its last
sub-step.

**`timeseries.csv`** has a `#` header block (commit, machine, run description) and then one row per logged slice:

| Column | Meaning |
|---|---|
| `tick` | Slice number, from 1. |
| `t_seconds` | `tick x dt`, in seconds. |
| `freq_hz` | Frequency after the slice. |
| `rocof_hz_s` | RoCoF at the slice, Hz/s; negative is falling. |
| `gen_mw` | Sum of the positive bus net injections of the solved state. |
| `demand_mw` | Total real demand of the solved state. |
| `iters` | Newton iterations the slice needed. 0 means the warm start already met the tolerance. |
| `max_mismatch` | Largest power mismatch at the end of the slice, per-unit on the system base. |
| `vmin_pu`, `vmax_pu` | Lowest and highest bus voltage magnitude. |
| `solve_ms` | Wall-clock time of the power-flow solve, in milliseconds. Differs on every run. |
| `converged` | `True` or `False`. A slice that does not converge is never used as the next warm start. |

Values from this run's file show the shape of the event. Before the trip the frequency sits at 50.000 Hz plus jitter
(50.00015 Hz and 0.00146 Hz/s at slice 1). At slice 100 (t=10.0 s, the trip) the RoCoF is -0.14693 Hz/s, which is
the swing-equation figure plus jitter. The deficit decays by `exp(-0.1 / 300)` before the step, so it is
999.667 MW and the figure is `f0 x 999.667 / (2 x 171,700) = 0.14555 Hz/s`; the jitter is the remaining -0.00138 Hz/s. By t=11.0 s frequency is 49.846 Hz
and RoCoF -0.130 Hz/s as governor and damping act. At t=15.0 s frequency is 49.490 Hz with the RoCoF down to
-0.038 Hz/s, and the nadir of 49.4475 Hz falls at t=17.0 s, where RoCoF crosses zero. After that the frequency recovers
as the deficit decays and the governor holds the response.

**`summary.md`** repeats the headline: slices, wall time, engine, nadir and maximum |RoCoF| to four
decimals, each with its time to three decimals, the count of non-converged slices (and the first one), and the export count.

**`exports/`** holds `slice-NNNNNNNNN.json` solve exports: slice 1, every slice within `--trip-window` seconds (default
2.0) of the trip, and every `--export-every`th slice. This run wrote 42: slice 1 and the 41 slices from 8.0 s to
12.0 s. The format is in [exports and formats](<0031 Exports and formats.md>).

A quiet run, with no trip, shows the ambient floor:

```
$ gridsim transient case9 --minutes 0.1 --dt-ms 10
[transient] case9: done in 0.2s  nadir 60.000Hz  maxRoCoF -0.001Hz/s  exports 1  -> results\transient-case9-20261019-183822
[transient] case9: 600 slices @ 10ms (0.1 min) on Newton-Raphson (dense)...
[exit 0]
```

### domains

`domains` groups the case's generators into speed domains and ranks them by stiffness. It loads the positional case or
the folder named by `--dir`, and otherwise `data/uk`, the reduced GB model.

```bash
gridsim domains
```

```
$ gridsim domains
GridSim  -  SPEED DOMAINS on GB reduced (10-zone)
==================================================================
3 speed domains  *  deltaGVa 56 %  *  dominant = Domain 1

Domain   Speed         Kind               Mach      GVA.s   GVad GVA
Domain 1 2p/3000rpm    Synchronous          12     167.80      24.34  *
Domain 0 inverter      Pll                  16       0.00      15.61
Domain 2 8p/750rpm     Synchronous           1       3.90       0.65

GVad = apparent-power stiffness sum(S*C); GVA.s = real spinning inertia. A domain is dominant
when deltaGVa >= 10% (LOS sec. 5.5). Distributed model: spatial spread & propagation are unvalidated at scale.
[exit 0]
```

- **Header.** The model name, then the count of speed domains, `deltaGVa` and the dominant domain.
- **Grouping.** Each generator is mapped from its type to a machine speed: nuclear, coal, biomass, CCGT, OCGT, CHP and gas
  are 2-pole turbo-alternators at 3,000 rpm; reciprocating engines are 4-pole at 1,500 rpm; hydro and pumped storage
  are 8-pole at 750 rpm. Storage, battery and BESS units form a synthetic-inertia domain. Every other
  inverter-based type (wind, solar, converter, interconnector, tidal, unknown) falls into a PLL domain. A machine of
  unknown type that carries inertia (`H` above zero) is placed in the 2-pole synchronous domain. Domains are numbered
  by pole count and kind, so inverter domains take the lowest numbers.
- **Domain, Speed, Kind.** The domain id, its speed (`2p/3000rpm`, or `inverter`), and `Synchronous`,
  `SyntheticInertia` or `Pll`.
- **Mach.** The count of generators in the domain.
- **GVA.s.** Real stored kinetic energy, the sum of `H x MBase` over the members, in GVA.s. Inverter domains show 0.00.
- **GVad GVA.** The apparent-power stiffness: the sum of `MBase x C` over the members, where the availability
  coefficient `C` is `|Pg| / MBase` clamped to 0 to 1. Rows are ordered by this column, largest first, and `*` marks the
  dominant domain.
- **deltaGVa.** The gap between the largest and second-largest GVad as a fraction of the second: `(top - second) /
  second`. The top domain is dominant when this is at least 10 %. When the runner-up carries no dispatch, the verb
  prints `sole domain` and the top domain dominates. When no domain dominates it prints `no dominant domain (inertia too
  evenly spread - the LOS fragmentation mode)`.

The 56 % in the capture means the 2-pole synchronous domain is 56 % stiffer than the next (the PLL converters at
15.61 GVA). `--dir` selects a JSON case directory; the synthetic GB model gives a different split:

```
$ gridsim domains --dir data/gb-synthetic
GridSim  -  SPEED DOMAINS on GB synthetic
==================================================================
4 speed domains  *  deltaGVa 52 %  *  dominant = Domain 2

Domain   Speed         Kind               Mach      GVA.s   GVad GVA
Domain 2 2p/3000rpm    Synchronous          23     167.80      23.92  *
...
```

The full output of that run is in `docs/_captures/frequency/domains-synthetic.txt`; its second row is `Domain 1
inverter Pll`, with 35 machines, 0.00 GVA.s and 15.76 GVad GVA, and its fourth is `Domain 0 inverter
SyntheticInertia` with 15 machines and 0.05 GVad GVA.

### h0-study

The H=0 study asks how a recorded loss would have played out if the real synchronous inertia were scaled toward zero
while an increasing share of the converter fleet were grid-forming. It first re-simulates the recorded event with no
device and refuses to continue unless that baseline reproduces the record (the gate described in
[replaying recorded events](<0018 Replaying recorded events.md>)). It needs a case directory that ships an
asset-dynamics bundle (`asset-classes.json` and `asset-bindings.json`); the default is `data/gb-synthetic`.

```bash
gridsim h0-study --event gb-largest-loss-1320
```

```
$ gridsim h0-study --event gb-largest-loss-1320

=== H=0 counterfactual: GB normal infeed loss risk screening (SQSS): single 1320 MW infeed trip [VERIFY] ===
  input provenance: [CuratedPlaceholder]  (event 'gb-largest-loss-1320' is effectively CuratedPlaceholder, the weakest of its parts): a design-basis study, not a reproduction of history; every figure below inherits it and none is Recorded.
  baseline gate: Reproduced   dP=1320 MW (first 5 s; 1320 MW over the whole event)   LFDD stage: 48.8 Hz
  GFM class: gfm-vsm-study-2024   GFL class: gfl-wind-2024   (Ev is virtual control inertia, NEVER in real E)
  GFM fraction realised by whole units: 0.25 -> 0.392, 0.50 -> 0.588, 0.75 -> 0.833

  corner Best: cell = nadir Hz / verdict  (HELD=formed & clear of LFDD, LFDD=below stage, CASC=Loss-of-Mains cascade, NaN=un-formable)
   scale\gfm |       0.00       0.25       0.50       0.75       1.00
  -------------------------------------------------------------------
        1.00 | 49.76 HELD 49.67 HELD 49.56 HELD 49.41 HELD 49.51 HELD
        0.50 | 49.58 HELD 49.48 HELD 49.40 HELD 49.25 HELD 49.47 HELD
        0.25 | 49.31 HELD 49.21 HELD 49.16 HELD 49.06 HELD 49.44 HELD
        0.00 |   -    NaN 41.09 CASC 44.66 CASC 48.70 CASC 49.40 HELD

  corner Worst: cell = nadir Hz / verdict  (HELD=formed & clear of LFDD, LFDD=below stage, CASC=Loss-of-Mains cascade, NaN=un-formable)
   scale\gfm |       0.00       0.25       0.50       0.75       1.00
  -------------------------------------------------------------------
        1.00 | 49.74 HELD 49.40 HELD 49.09 HELD 48.80 CASC 49.21 HELD
        0.50 | 49.51 HELD 49.19 HELD 48.84 HELD 48.79 CASC 49.17 HELD
        0.25 | 49.13 HELD 48.85 HELD 48.80 CASC 48.70 CASC 49.14 HELD
        0.00 |   -    NaN 28.42 CASC 37.11 CASC 44.52 CASC 49.10 HELD

  28/40 cells HELD (formed, clear of LFDD); 2 forming-dependent (H=0, no grid-forming source).
  Note: forming-dependent cells are expected DATA, not an error - the run still exits 0.

  -> results\h0-study-gb-largest-loss-1320
[exit 0]
```

- **Title and gate line.** The event title and `baseline gate: Reproduced`, the size of the exogenous loss in MW (`dP`) and the
  LFDD stage (48.8 Hz). The study runs only when the gate says `Reproduced`.
- **Class line.** The asset classes used for the grid-forming (`--gfm-class`) and grid-following (`--gfl-class`)
  converters. The virtual inertia `Ev` of the grid-forming units enters only the forming denominator; it is never
  added to the real inertia.
- **One table per corner.** `Best` and `Worst` are the two bracketing sets of the unfitted constants (see
  [How far to trust a nadir](#how-far-to-trust-a-nadir)). Rows are the synchronous-inertia scale, from 1.00 (the case
  as it is) to 0.00 (no real inertia). Columns are the fraction of the converter fleet made grid-forming, from 0.00 to 1.00.
- **A cell** is `nadir Hz` then a verdict.
  - `HELD`: the frequency was formed and its nadir stayed above the 48.8 Hz LFDD stage. It does not mean the nadir
    stayed above the 49.5 Hz statutory floor: 49.25 Hz reads `HELD`.
  - `CASC`: grid-following converters tripped on Loss-of-Mains protection and the nadir reached 48.8 Hz or lower. The cascade
    removes infeed as the frequency falls; a nadir far below 48.8 Hz (the 28.42 Hz cell) ranks severity and is not a
    forecast, because the model sheds no load and trips no further plant to arrest it.
  - `LFDD`: the nadir reached the LFDD stage with no converter trips.
  - `NaN`, with `-` in place of a nadir: zero real inertia and no grid-forming source, so nothing forms the frequency.
    RoCoF, nadir and settling are undefined, and these cells count as a collapse of every grid-following unit. In the capture
    they are the scale 0.00, fraction 0.00 cells of both corners.
- **Summary line.** The number of cells that were `HELD`, out of all cells (here 2 corners x 4 scales x 5 fractions
  = 40), and the count of forming-dependent cells. Those cells are data, so the run exits 0.
- **Path line.** The run directory.

The run directory holds `h0-study.json` (every cell with its separate real and virtual inertia, RoCoF, nadir,
settling frequency, trip counts and assessment), `matrix.csv` (one row per cell), `summary.md` and `run-metadata.json`.

## Variations

### Replay a trace interactively

```bash
gridsim replay data/neso/gb-2023-12-22-frequency.csv --sps 100 --seconds 120
```

This loads the first 121 samples of the 2023 trace and plays them at twice real time. Press Q to quit. Never start
the interactive form from a script: it waits for keys. Use `--frames N` there.

### Batch and sweep runs

`transient` runs several cases in one command. `--cases suite` (or `all`) runs case9, case14, case30, case57,
case118, case300 and `pegase/case1354pegase.m`; `--cases a,b,c` takes a list. Each case gets a subdirectory of
`results/transient-<stamp>/`, named after the case with path separators replaced by `_`.

```bash
gridsim transient --cases case14,case118 --minutes 1 --dt-ms 20 --trip 200 --trip-at 5
```

`--log-every N` thins the time series (the first slice, the last, and every slice within the trip window are always
logged). `--export-every N` adds full solve exports. `--solver dense|sparse|bfs` forces a power-flow engine. A run
directory written by `transient` loads in the desktop app through LOAD RUN and plays back frame by frame.

### Try different sweeps in `h0-study`

```bash
gridsim h0-study --event gb-largest-loss-1320 --sync-inertia-scale 1,0.5,0 --gfm-fraction 0,1 --corners best
```

`--sync-inertia-scale` and `--gfm-fraction` take comma lists and `--corners` takes `best`, `nominal`, `worst`. The
verbs `scenario`, `fleet` and `evidence` use the same gate and the same event file; see
[counterfactuals](<0020 Counterfactuals.md>).

### Fit the unfitted constants to a measured trace

`fit-frequency` fits load damping, the responsive fraction and the governor time constant of the lumped swing model to
one measured loss-of-infeed trace, by least squares on the trajectory for the given loss, inertia and demand. The form
is `gridsim fit-frequency <trace.csv> --inertia <GVA.s> --demand <MW> --loss <MW> [--loss-at <s>] [--fit damping,...]`.
No trace in this form ships with GridSim, so bring your own measured window. The trace is a two-column `seconds,Hz` CSV on the model's clock. The report gives each constant's seed (the shipped
default), its fitted value, the RMS error and the nadir error. It is a calibration check: the shipped defaults do not
change. The nominal frequency comes from the trace (50 or 60 Hz) or `--nominal-hz`. Damping alone is well identified
from one trace; fitting all three is weakly identified and a `Note:` line says so. Every flag and exit code is in
[ch. 19, fit-frequency](<0019 CLI reference.md#fit-frequency>).

### Bracket a trip's nadir

`trip --band` adds the nadir band over the same three unfitted constants, each moved by +/- 20 % one at a time and over
every corner of the joint box; `trip --gb-services` adds the GB Dynamic Containment, Moderation and Regulation layer at
the typical volumes; and `trip --asset-dynamics --envelope <class>:<param>=<low>..<high>` sweeps asset-class control
parameters around the trip:

```bash
gridsim trip --band
gridsim trip --dir data/gb-synthetic --asset-dynamics --envelope dg-alomcp:rocofTripHzPerS=0.5..2
```

See [ch. 19, trip](<0019 CLI reference.md#trip>) and [contingency analysis](<0011 Contingency analysis.md>).

### The live frequency view in the desktop app

The desktop app integrates frequency on every tick of the live sim. To run an event by hand, load a case, pick a fine
step in the transport bar (1 ms to 20 min), and right-click a bus on the network map, or press `T` to trip the
largest online generator. The lost MW enters the same integrator that `transient` uses, with the same 300 s decay and
the same ambient jitter, at the case's own nominal frequency. The OPERATOR tab shows the frequency and RoCoF, the NESO
RP1 tab shows the RoCoF (on a 50 Hz case only), the MECHANICAL tab shows the RoCoF margin against the 0.125 and
1.0 Hz/s relay settings, and SCADA raises its P2 frequency alarm when the excursion leaves the band 0.1 Hz either side
of nominal (49.9 Hz to 50.1 Hz on a 50 Hz case). The event log records `TRIP <unit> (<MW> MW)  RoCoF <x> Hz/s  nadir <y> Hz`; those two
figures are an analytical estimate, not the live reading: a batch `FrequencyDynamics.Simulate` of the same loss at
t = 1 s, on the inertia with the tripped unit removed and a default `DynamicsConfig`, so they differ from the live
trace. The GENERATION & INERTIA tab runs an analytical comparison of the same trip with
the configured TSGB device; that analysis removes the tripped unit's inertia from the system, where the live integrator
leaves it in. In replay mode and on a bridge stream there is no live integrator, and a right-click logs `TRIP is not available on
this feed (replay or bridge stream)`. When the case ships an
asset-dynamics bundle, the live integrator runs the asset-aware variant: asset protection trips are taken from the
running dispatch, and the imbalance they leave is restored over the same 300 s secondary time constant as a manual
trip; the tripped units stay tripped. Details are in [the desktop app](<0008 The desktop app.md>).

## Inertia models

Frequency studies run on one of two inertia models, chosen with the global switch `--inertia-model lumped|distributed`
(see [ch. 19, CLI reference](<0019 CLI reference.md#global-switches>)).

**Lumped (the default).** The whole system is one mass with energy `E = sum(H x MBase)`, one frequency, and
`RoCoF = f0 x deltaP / (2E)` at the instant of the loss. Every verb in this chapter that integrates frequency does this
unless told otherwise.

**Distributed (experimental).** Generators are grouped into speed domains (the grouping `domains` prints). Each
synchronous domain is a coherent mass with its own frequency and rotor angle, coupled to the others by
synchronising power derived from the network. The system-level forcing (the loss, governor, damping) is computed from
the inertia-weighted centre-of-inertia frequency exactly as in the lumped model, so the centre-of-inertia frequency
equals the lumped trajectory to rounding. What the distributed model adds is per-domain traces and a per-bus
RoCoF map. With one synchronous domain it delegates to the lumped model, so the reduction is exact.

What each verb does with the switch:

| Verb | With `--inertia-model distributed` |
|---|---|
| `trip` | Integrates the multi-domain model on the post-trip network and prints the centre-of-inertia result, preceded by an `Inertia model:` line. The half-inertia comparison line runs lumped. |
| `transient` | Prints the speed-domain split on standard error and integrates lumped. |
| `domains` | Unaffected: it always reports the domain split. |
| `replay-event`, `scenario`, `fleet`, `evidence`, `h0-study` | Print a warning on standard error and run lumped. |
| `freq`, `replay` | Not applicable; they read recorded data. |
| the desktop app | Lumped. |

The same trip on the reduced GB model under each model gives the same centre-of-inertia numbers. Under the distributed
model the verb adds an `Inertia model:` line naming the swing masses at the trip and a `Loss placed:` line stating where
the loss lands: on the speed domain whose machines remain at the tripped unit's bus, or, when the unit was the only
machine there, shared among the domains by their synchronising coupling to that bus. When the coupling cannot be derived
from the network a `Coupling:` line says so and why:

```
$ gridsim trip --inertia-model distributed
Inertia model:  distributed  (2 speed domain(s), dGVa 0.37)
Loss placed:    bus 10 hosts the machines of speed domain 1, which takes the whole step
GridSim  -  contingency on GB reduced (10-zone)
================================================================
Tripped:        Hinkley Point Nuclear  (2880 MW lost, nuclear)
System inertia: 171.7 -> 158.9 GVAs after the trip
Peak RoCoF:     -0.453 Hz/s  (largest |df/dt| over the run)
Nadir:          48.340 Hz  (t+6.9 s)
Settling:       49.144 Hz
...
```

The full output for both models is in `docs/_captures/frequency/trip-lumped.txt` and `trip-distributed.txt`; the lines
from `Peak RoCoF` to the half-inertia line are the same in both. `dGVa 0.37` is the dominance ratio of the post-trip
network (37 %); the 56 % that `domains` prints is for the network before the trip, which still has Hinkley Point in its
dominant domain. On `trip`, `Settling` is the frequency at the end of the 30 s window, not a separately detected
steady state.

The `2` in that line is the number of swing masses (synchronous domains that carry inertia) at the trip. `domains`
counts every speed domain, including the `Pll` domain, so it prints 3, and so does the `transient` line below.

`transient` under the distributed switch reports the split and does not use it:

```
$ gridsim transient uk --minutes 0.1 --dt-ms 100 --inertia-model distributed
[transient] uk: done in 0.3s  nadir 50.000Hz  maxRoCoF -0.005Hz/s  exports 1  -> results\transient-uk-20261019-183828
[transient] uk: 60 slices @ 100ms (0.1 min) on Newton-Raphson (dense)...
[transient] distributed inertia: 3 speed domains, deltaGVa 56 % (per-tick integration remains the lumped live model; a distributed live integrator is a follow-up)
[exit 0]
```

The other verbs print this warning, taken from an `h0-study` run:

```
warning: --inertia-model distributed is not honored by 'h0-study' - it runs the lumped scalar model. Only 'trip' integrates the distributed multi-domain model (and 'transient' reports the speed-domain split but still integrates lumped).
```

Spatial spread between domains and propagation delay are unvalidated at scale. Propagation is off by default
(`DistributedConfig.EnablePropagation`).

## Response services

After a loss, three layers arrest the fall.

1. **Stored energy** sets the initial slope (the RoCoF) and does nothing to arrest it.
2. **Load damping** (demand falls as frequency falls, 1 % of demand per Hz) and **governor droop** (5 % droop on a
   responsive share of demand, 20 %, with an 8 s lag) are the generic model of the conventional system's response.
3. **Procured frequency response**: Dynamic Containment (DC), Dynamic Moderation (DM) and Dynamic Regulation (DR). Each
   is a block of MW with a published delivery curve and a response lag.

A service delivers nothing inside its **deadband** around 50 Hz. Past the deadband the delivery rises along the
published curve until the **full-delivery deviation**, then stays at its MW cap. A **knee** is a point where the slope
changes: below it the service delivers only a small fraction of its volume, past it the slope steepens. The delivered
power then follows its target through a first-order lag, with the service's time constant. Low frequency makes a
service inject; high frequency makes it absorb.

| Service | Deadband | Knee | Full delivery | Shape |
|---|---|---|---|---|
| DC | 0.015 Hz | 0.2 Hz, at 5 % of volume | 0.5 Hz | Low-gain segment to the knee, steep segment to full. |
| DM | 0.1 Hz | at the deadband, 0 % | 0.2 Hz | Ramp from the deadband to full. |
| DR | 0.015 Hz | at the deadband, 0 % | 0.2 Hz | Ramp from the deadband to full. |

For the 22 December 2023 event the event record holds the volumes the system had cleared in the EAC auction for that
delivery block: DC 873 MW (lag 0.3 s), DM 150 MW (0.3 s) and DR 203 MW (4 s), 1,226 MW in all. At a deviation of 0.3 Hz,
DC's target is 873 x (0.05 + 0.95 x (0.3 - 0.2) / (0.5 - 0.2)) = 320.1 MW. An event that lists its services as
complete (`responseServicesComplete`, true for events from the start of EAC on 2 November 2023) switches the generic
governor off, so the response is not counted twice; load damping always stays. The event also records the frequency
the trip landed on (49.879 Hz for 22 December 2023), and the simulation starts in equilibrium there, with services
partly deployed.

Where the services apply:

- **Applied:** `replay-event`, `scenario`, `fleet`, `evidence` and `h0-study`, from the event record, in the baseline and in every counterfactual (a
  device is additional to what the system held).
- **Not applied:** `transient` and the live view in the apps use the generic governor and damping only, and so does
  `trip` without `--gb-services`.
- The library also holds a typical GB fleet at DC 950 MW, DM 520 MW and DR 490 MW (`GbResponseServices.Typical`), with
  lags of 0.5 s, 2 s and 10 s. `trip --gb-services[=<scale>]` adds it, times the scale, on top of the generic governor.

Services are changed through the event file: `gridsim replay-event <id> --dump-catalog <file>` writes the built-in
events as JSON, the `responseServices` array holds `mw`, `deadbandHz`, `fullDeliveryDeviationHz`, `timeConstantS`,
`kneeDeviationHz` and `kneeFraction` for each, and `--catalog <file>` loads an edited copy (see
[replaying recorded events](<0018 Replaying recorded events.md>)).

## How far to trust a nadir

The RoCoF is set by the inertia and the size of the loss. For a step loss from nominal the swing equation fixes it
exactly. The ambient NESO sample is a consistency check on the scale only: its 0.0080 Hz/s implies a 64 MW imbalance
at 200 GVA.s, and the calibration test round-trips that figure through the same equation. The nadir and the recovery also depend on three constants that no
measured loss-of-infeed trace has yet fitted: load damping (1 % of demand per Hz), the responsive fraction (0.20 of
demand) and the governor time constant (8 s). They are literature-typical GB values, and the code documents their
uncertainty as a factor of two. The governor droop (5 %) is the GB Grid Code figure and is firm. The delivery curves
of DC, DM and DR are the published service shapes; their time constants are literature-typical. `fit-frequency` fits
the three constants to a measured trace and `trip --band` shows how far each moves a nadir (see
[Variations](#variations)); neither changes the shipped values.

Read a nadir as an indicative figure bracketed by the `Best` and `Worst` corners, never as a validated prediction. The
corners scale the three constants (`Best`: damping x1.5, responsive fraction x1.5, governor lag x0.7; `Worst`:
damping x0.6, responsive fraction x0.6, governor lag x1.4). On the 22 December 2023 event the corners give a nadir
envelope of 48.65 Hz to 49.20 Hz against a recorded 49.275 Hz, and `replay-event` reports the record as outside the
envelope.

The tag `[VERIFY]` on an event figure means a curated placeholder awaiting a source. The event
`gb-largest-loss-1320`, the SQSS normal infeed loss risk of 1,320 MW, is such a screening case. Its recorded outcome is a
placeholder, so `Reproduced` on its gate says only that the model agrees with the placeholder. `replay-event` refuses
it, because a replay presents its output as a reproduction of history; `h0-study`, `scenario`, `fleet` and `evidence`
study it, print an `input provenance: [CuratedPlaceholder]` line and tag every figure they write with it.

## Run directories

| Verb | Default location | Contents |
|---|---|---|
| `transient` | `results/transient-<case>-<stamp>/` in the current directory; a batch uses `results/transient-<stamp>/<case>/` | `timeseries.csv`, `summary.md`, `run-metadata.json`, `exports/slice-*.json` |
| `h0-study` | `results/h0-study-<event>/` in the current directory | `h0-study.json`, `matrix.csv`, `summary.md`, `run-metadata.json` |
| `replay-event` | `results/replay-<event>/` in the current directory | `baseline-frequency.json`, `summary.md`, `run-metadata.json` |
| `freq`, `replay`, `domains` | none | They print only. |

`--out <dir>` names the directory for `transient` and `h0-study`. `run-metadata.json` holds the commit, backend,
machine, runtime and the run description, so a result traces to the build that produced it. Run directories under a
`results/` folder are discovered by the desktop app and can be stepped frame by frame
([exports and formats](<0031 Exports and formats.md>)).

## Exit codes

The general table is in [ch. 19, CLI reference](<0019 CLI reference.md#parsing-rules-and-exit-codes>). For this chapter's verbs:

| Verb | Code | Meaning |
|---|---|---|
| `freq` | 0 | Printed. |
| | 1 | The CSV does not exist. |
| `replay` | 0 | Played, or printed `--frames` frames. |
| | 1 | The CSV does not exist, or it holds no data. |
| `transient` | 0 | Every slice converged. |
| | 1 | No case loaded, or some cases of a batch failed to load (the rest still ran). |
| | 2 | `--dt-ms` or `--minutes` is zero or negative, the run is shorter than one slice, `--trip-at` falls past the end of the run, `--solver` is not one of `auto`, `dense`, `sparse`, `bfs`, `sweep`, or a numeric flag is malformed. |
| | 3 | One or more slices did not converge. The run completes and `summary.md` records the count. |
| `domains` | 0 | Printed. |
| | 2 | `--dir` has no value. |
| `h0-study` | 0 | The study ran, including cells that read `NaN`. |
| | 1 | The event id is not a built-in event or in the `--catalog` file, the case fails to load, the case ships no asset-dynamics bundle, or the engine fails. |
| | 2 | No `--event` (usage is printed), a malformed list or `--corners` value, or an event whose timestamp is newer than 169 hours before now. |
| | 3 | The no-device baseline did not reproduce the record. |

### Why `h0-study` exits 3 on `gb-2023-12-22`

```bash
gridsim h0-study --event gb-2023-12-22 --sync-inertia-scale 1,0 --gfm-fraction 0,1
```

```
$ gridsim h0-study --event gb-2023-12-22 --sync-inertia-scale 1,0 --gfm-fraction 0,1
GATE FAILED: Refusing to run counterfactuals: the no-device baseline is 'Marginal', not 'Reproduced'. Reproduce reality first. Marginal: RoCoF sim -0.123 vs rec -0.102 Hz/s (20 % err); nadir sim 48.98 vs rec 49.27 Hz (0.30 Hz err); RMSE n/a (no trace)
Fix the event's [VERIFY] figures (or tolerances) until 'replay-event' reports Reproduced, then re-run.
[exit 3]
```

`h0-study` first calls the replay validator (`ReplayValidator.Validate`) with its default tolerances. The validator
re-simulates the recorded event with no device and scores two figures. The RoCoF must agree within 15 % of the
recorded value, and the nadir within 0.15 Hz. The verdict is `Reproduced` when both are inside those bounds,
`Marginal` when both are inside twice those bounds (30 % and 0.30 Hz) but not both inside the first, and `Failed`
otherwise. A per-sample RMSE against a trace above 0.05 Hz also demotes a `Reproduced` result to `Marginal`; `h0-study`
attaches no trace, which the message states as `RMSE n/a (no trace)`.

Here the simulated RoCoF is -0.123 Hz/s against a recorded -0.102 Hz/s, an error of 20 %: outside 15 %, inside 30 %.
The simulated nadir is 48.98 Hz against 49.275 Hz recorded, an error of 0.295 Hz: outside 0.15 Hz, inside 0.30 Hz. Neither
figure is inside the first bound, both are inside the second, so the verdict is `Marginal`. Only `Reproduced` unlocks a
counterfactual, so the engine throws `BaselineNotReproducedException`, the CLI prints a `GATE FAILED` line and a one-line hint, and
the exit code is 3. This is the only condition that gives `h0-study` exit 3. `replay-event` on the same event, with the
real 1 s trace attached, reports the same verdict and a trace RMSE of 0.281 Hz. Its full output is in
`docs/_captures/frequency/replay-event-gb-2023-12-22.txt`. The gate message points at the cause: this event is blocked
by the model's own figures and tolerances, not by anything in the study.

The h0-study with no arguments prints its usage and exits 2:

```
$ gridsim h0-study
usage: gridsim h0-study --event <id> [--catalog f] [--dir case]
                        [--sync-inertia-scale 1,0.5,0.25,0] [--gfm-fraction 0,0.25,0.5,0.75,1]
                        [--gfm-class gfm-vsm-study-2024] [--gfl-class gfl-wind-2024] [--corners best,worst]
                        [--trace <neso.csv>] [--out <dir>]
Built-in events: gb-2019-08-09, gb-2023-12-22, gb-largest-loss-1320
Scales REAL synchronous inertia toward 0 x makes the converter fleet grid-forming. REQUIRES a reproduced baseline.
[exit 2]
```

## Pitfalls

- **`domains` takes a case or `--dir`, not both.** `gridsim domains case9` reports `SPEED DOMAINS on IEEE 9-bus (WSCC)`;
  with no case it loads `data/uk`. A positional case and a `--dir` folder that name different networks exit 2, and a
  `--dir` folder that does not exist exits 1 ([ch. 19, Case arguments](<0019 CLI reference.md#case-arguments>)).
- **`transient` with no flags is a long run.** The defaults are 60 minutes at 1 ms, which is 3.6 million power-flow
  solves. The `uk` run in this chapter solved 600 slices in 1.4 s, about 429 slices per second, so 3.6 million slices take
  `3,600,000 x 1.4 / 600 = 8,400 s` (2.3 hours) at that rate. Set `--minutes` and `--dt-ms` for the question you are asking.
- **`--trip-at` needs `--trip`.** Without `--trip` MW above zero no trip is scheduled, so `--trip-at` or `--trip-window`
  without it is refused (exit 2) rather than ignored. A
  `--trip-at` of 0 trips on the first slice. A `--trip-at` past the end of the run is refused:

  ```
  $ gridsim transient case9 --minutes 0.1 --dt-ms 100 --trip 100 --trip-at 30
  [transient] case9: --trip-at 30s is past the run end (0.1 min = 60 slices @ 100ms) - the contingency never fires. Aborting.
  [exit 2]
  ```

- **A trip changes the frequency and nothing else.** In `transient` and the live view the trip enters the swing
  integrator only. The power flow keeps the unit, so `gen_mw`, voltages and flows do not respond. Use `trip` for the
  contingency that removes the unit and its inertia.
- **A `CASC` nadir is not a Hz forecast.** A cell far below 48.8 Hz tells you a Loss-of-Mains cascade ran away in the
  model. Compare the verdict, not the figure.
- **`HELD` is relative to LFDD.** It means the nadir stayed above 48.8 Hz.
- **A recent CSV is refused.** A row dated within 169 hours of now stops `freq` and `replay` with
  `REJECTED (forward inference): ...` and exit 2, as on every event verb. Use data that is at least a week old.
- **A damaged or empty CSV is refused.** A line after the header that is not a data row exits 1 naming the line.
  `freq` on a file with no data rows exits 1 with `error: frequency file '<file>' holds no data rows ...`; `replay`
  prints `No data.` and exits 1.
- **The interactive `replay` never ends by itself.** It waits for Q or Esc. Always pass `--frames N` from a script.
- **Run-directory names and timings change on every run.** The stamp in a `transient` directory name, `done in ...`
  and `solve_ms` differ by machine and by run. The frequencies, RoCoF and nadir repeat on the same machine and build with the same case and flags,
  because the ambient jitter is seeded. No test compares machines.

## Limits

- `transient` integrates the lumped model under `--inertia-model distributed`; it prints the speed-domain split only.
- The live view and `transient` carry no response services (DC, DM, DR); the event-driven verbs apply the event's recorded services, and only `trip --gb-services` applies the typical GB fleet.
- The shipped damping, responsive-fraction and governor-lag constants are not fitted to a measured loss-of-infeed trace (`fit-frequency` fits them to one trace but does not change them), so a nadir is indicative within the Best and Worst corners.
- Spatial spread and propagation between speed domains are unvalidated at scale, and the distributed model has no grid-forming path (a forming run falls back to the lumped forming integrator).
- A `transient` trip is a deficit in the swing integrator; the power-flow state does not respond to it.
- `h0-study` runs only on events whose no-device baseline is `Reproduced`; `gb-2023-12-22` is `Marginal` and cannot be studied.

## See also

- [The frequency dynamics technical chapter](<../technical/0005 Frequency dynamics.md>) - the swing equation, `DynamicsConfig`, the distributed model and the response-service kernel
- [ch. 19, CLI reference](<0019 CLI reference.md#freq>) - `freq`
- [ch. 19, CLI reference](<0019 CLI reference.md#replay>) - `replay`
- [ch. 19, CLI reference](<0019 CLI reference.md#transient>) - every `transient` flag
- [ch. 19, CLI reference](<0019 CLI reference.md#domains>) - `domains`
- [ch. 19, CLI reference](<0019 CLI reference.md#fit-frequency>) - `fit-frequency`
- [ch. 19, CLI reference](<0019 CLI reference.md#h0-study>) - every `h0-study` flag
- [ch. 19, CLI reference](<0019 CLI reference.md#global-switches>) - `--inertia-model`, `--forming`, `--asset-dynamics`
- [ch. 19, CLI reference](<0019 CLI reference.md#parsing-rules-and-exit-codes>) - the exit-code table
- [18. Replaying recorded events](<0018 Replaying recorded events.md>) - the credibility gate and the event file
- [20. Counterfactuals](<0020 Counterfactuals.md>) - `scenario`, `fleet`, `evidence` and the H=0 study in context
- [16. Electromagnetic transients](<0016 Electromagnetic transients (EMT).md>) - waveform-level studies
- [11. Contingency analysis](<0011 Contingency analysis.md>) - `trip`, `screen` and the right-click workflows
- [8. The desktop app](<0008 The desktop app.md>) - the live frequency view, transport bar, LOAD RUN
- [Exports and formats](<0031 Exports and formats.md>) - run-directory and solve-export layouts
