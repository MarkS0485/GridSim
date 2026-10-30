# 17. Harmonic studies

A harmonic study asks what the non-sinusoidal currents injected by power electronics (rectifiers, drives, inverters,
chargers) do to the bus voltages of a network. The fundamental power flow ([ch. 5](<0005 Running power flows.md>))
gives the 50 Hz operating point; `gridsim harmonics` takes that network, adds a set of harmonic current sources, and
solves the network's response one harmonic order at a time. It reports the voltage distortion (THD) at every bus,
the frequencies at which the network resonates, and a per-bus IEEE 519 verdict. You need it when you are siting or
accepting a converter, checking a capacitor bank for resonance, or asking whether a network with fewer synchronous
machines has become more resonant. The network side is always computed from the case. The one input GridSim cannot
compute is the source: the harmonic current spectrum of the connected equipment. You either supply it (`--spectra`)
or accept a labelled placeholder (the default).

## Before you start

- A case that loads (see [ch. 2, Loading networks](<0002 Loading networks.md>)). Every built-in case works,
  including `gb-full`.
- For a grounded result, a `gridsim-harmonics/1` file of metered spectra (see [The --spectra file](#the---spectra-file)).
  Without one, the study runs on one assumed six-pulse source (the G5 Issue 5 Table B.3 profile) and says so in its
  export, or, with `--converter-sources`, on one G5 source per converter unit of the case (see
  [Converter sources](#converter-sources---converter-sources)).
- Cases with no base kV on their buses (`case14` is one) are judged at the strictest IEEE 519 tier and never certified
  compliant; see [Pitfalls](#pitfalls).

## Worked example

Run the default study on the WSCC 9-bus case:

```bash
gridsim harmonics case9
```

```
$ gridsim harmonics case9
harmonics: case IEEE 9-bus (WSCC), 1 source(s), orders 24
  synthetic source : 6-pulse-g5 at bus 9, 0.5 pu fundamental (G5 Issue 5 Table B.3 six-pulse profile, the default; --synth-spectrum ideal for the idealised 1/h)
  worst voltage THD : 6.31% at bus 9 (relative to the solved fundamental voltage)
  resonance (bus 9): order 7, |Z|=0.658 pu, sharpness 3.8
  fine sweep peak  : h=7.4750 (448.50 Hz), |Z|=0.688 pu, positive sequence
  IEEE-519         : 0/9 buses compliant
[exit 0]
```

The run injected one assumed 6-pulse source (0.5 pu fundamental current) at the largest-load bus, bus 9. The worst
voltage distortion is 6.31 %, and it is at the source bus. The whole-number scan puts the resonance at the 7th harmonic (the sweep refines it to h = 7.475), and a
6-pulse drive injects a 7th harmonic, so the resonance amplifies the source's own emission. No bus meets IEEE 519 at
345 kV, whose limits are 1.5 % THD and 1.0 % for any single harmonic. The exit code is 0 because the study ran to
completion; a failed IEEE 519 verdict never changes the exit code.

The default source is the G5 six-pulse profile `6-pulse-g5` (5th 31.4 %, 7th 10.9 %, orders up to 49; see
[The default source](#the-default-source---synth-sources)), and the `synthetic source :` line names it, its bus and its
fundamental current. `--synth-spectrum ideal` selects the idealised 1/h spectrum (orders 5 to 25, 7th 14.3 %) instead.

## Reading the output

The summary has five lines, always in this order, a `synthetic source :` line after the header when the default
source runs, and two optional lines at the end.

**Header.** `harmonics: case IEEE 9-bus (WSCC), 1 source(s), orders 24`.

- The case name is the model's name.
- `1 source(s)` is the number of harmonic sources in the set. It is 1 for the default and equals the number of entries
  in `sources` for a `--spectra` file.
- `orders 24` is the number of harmonic orders in the study: the odd orders 3, 5, 7, ... 49. Eight of them are
  multiples of 3 (3, 9, 15, 21, 27, 33, 39, 45). Those carry no current and no voltage in this study; see
  [Pitfalls](#pitfalls).

**Worst voltage THD.** `worst voltage THD : 6.31% at bus 9 (relative to the solved fundamental voltage)` is the largest
voltage total harmonic distortion over all buses and the bus that has it. THD at a bus is the root-sum-square of its
harmonic voltages (orders 3 to 49) divided by that bus's solved fundamental voltage magnitude, in percent. The
fundamental is the power flow `gridsim <case> --once` reports; the Web HARMONICS tab divides by the same quantity
through the same function, so the two agree. A fundamental that does not converge stops the run with
`harmonics: FAILED - the fundamental power flow did not converge ...` and exit 3. When two
buses tie, the first in model order is named. This line also chooses the scan bus used by the next two lines when you
give no `--bus`.

**Resonance.** `resonance (bus 9): order 7, |Z|=0.658 pu, sharpness 3.8` describes the driving-point impedance
`|Z|` seen looking into the scan bus, evaluated at the whole-number orders of the study (the multiples of 3 are
skipped, leaving 16 orders).

- `bus 9` is the scan bus: the worst-THD bus, or the bus you passed with `--bus`.
- `order 7` is the whole-number order with the largest `|Z|`.
- `|Z|=0.658 pu` is that largest value, in per-unit on the system base. A source current of `I` pu at this bus
  produces a voltage of `I x |Z|` pu at this order, so a larger `|Z|` means more distortion per unit of injected current.
- `sharpness 3.8` is the peak `|Z|` divided by the median `|Z|` over the same 16 orders. A value of 1 means a flat
  response with no resonance. The larger the value, the taller and more lightly damped the peak. The code defines no
  pass or fail threshold for it; use it to compare cases and to compare one case before and after a change. case9
  reads 3.8; `gb-full` reads 83.5 (below).

**Fine sweep peak.** `fine sweep peak  : h=7.4750 (448.50 Hz), |Z|=0.688 pu, positive sequence` repeats the search on a
fine grid, because a resonance sits on a whole-number order only by coincidence. The grid runs from h = 1.5 to h = 50
in steps of 0.1 and is not restricted to non-multiples of 3, so the sweep peak can sit on a multiple of 3 that the
whole-number scan skips. A golden-section search then narrows the highest point to a bracket of 1e-6 orders.

- `h=7.4750` is the refined resonance as a multiple of the fundamental. `(448.50 Hz)` is `h` times the case frequency.
- `|Z|=0.688 pu` is the refined peak. It is never below the highest grid point, and the grid holds every whole-number
  order to within floating-point rounding, so it matches or exceeds the whole-number peak on the line above.
- `positive sequence` or `negative sequence` names the network that holds the peak. The negative-sequence network is
  swept only when the case has a phase-shifting transformer; otherwise the peak is always `positive sequence`.

**IEEE-519.** `IEEE-519         : 0/9 buses compliant` counts the buses that pass. A bus passes only when its voltage
THD is at or below the THD limit for its voltage class and its largest single-harmonic voltage is at or below the
individual limit for that class (limits in [What non-compliance means](#what-non-compliance-means)).

**WARNING line (optional).** `WARNING          : 14 bus(es) have no base kV - judged at the strictest tier and NOT certified`
appears only when at least one bus has a missing, zero, negative or non-finite base kV. Those buses are counted in the
total but cannot pass.

**export line (optional).** `export -> <path>` appears only with `--enableExport`; the CLI prints the absolute
path of the file written. The captures in this chapter show that path relative to the repository.

Timings and stamps in captured output, where they appear, differ by machine and run. The numbers on the five lines
above are deterministic: the same case, source and build give the same digits on every run. The mechanism is that the
harmonic solve performs scalar double-precision operations in a fixed order, writes each result by index, and does not
use the SIMD kernels or fused multiply-add. No automated test compares the harmonic output across CPU types.

### Reading a resonance

Three captured runs show how to read the resonance lines. The first is `case30` on the default source:

```
$ gridsim harmonics case30
harmonics: case IEEE 30-bus, 1 source(s), orders 24
  synthetic source : 6-pulse-g5 at bus 8, 0.5 pu fundamental (G5 Issue 5 Table B.3 six-pulse profile, the default; --synth-spectrum ideal for the idealised 1/h)
  worst voltage THD : 12.17% at bus 8 (relative to the solved fundamental voltage)
  resonance (bus 8): order 49, |Z|=2.951 pu, sharpness 3.1
  fine sweep peak  : h=50.0000 (2500.00 Hz), |Z|=2.998 pu, positive sequence
  IEEE-519         : 1/30 buses compliant
[exit 0]
```

The peak is at order 49, the top of the study, and the sweep peak is `h=50.0000`, the last grid point. No in-band
resonance at this bus is as tall as the value at the top of the band; a smaller in-band resonance can still exist. The
impedance of an inductive network grows with frequency, so the maximum lands on the boundary. A peak on the boundary
marks no resonance.

The second asks `case30` for a different bus:

```
$ gridsim harmonics case30 --bus 1
harmonics: case IEEE 30-bus, 1 source(s), orders 24
  synthetic source : 6-pulse-g5 at bus 8, 0.5 pu fundamental (G5 Issue 5 Table B.3 six-pulse profile, the default; --synth-spectrum ideal for the idealised 1/h)
  worst voltage THD : 12.17% at bus 8 (relative to the solved fundamental voltage)
  resonance (bus 1): order 37, |Z|=5.608 pu, sharpness 3.8
  fine sweep peak  : h=37.5864 (1879.32 Hz), |Z|=6.009 pu, positive sequence
  IEEE-519         : 1/30 buses compliant
[exit 0]
```

Bus 1 has a genuine resonance at h = 37.59, between the whole-number orders 37 and 38. The whole-number scan reports
order 37 with 5.608 pu; the sweep finds the true peak of 6.009 pu. Everything else is unchanged: `--bus` moves the
two resonance lines and leaves THD and the IEEE 519 count alone, because the penetration solve does not depend on
where you look.

The third is the full GB model, which carries the case where the resonance matters:

```
$ gridsim harmonics gb-full
harmonics: case GB full (3539-bus, 5918-branch), 1 source(s), orders 24
  synthetic source : 6-pulse-g5 at bus 297, 0.5 pu fundamental (G5 Issue 5 Table B.3 six-pulse profile, the default; --synth-spectrum ideal for the idealised 1/h)
  worst voltage THD : 5.95% at bus 4992 (relative to the solved fundamental voltage)
  resonance (bus 4992): order 5, |Z|=112.064 pu, sharpness 83.5
  fine sweep peak  : h=4.9818 (249.09 Hz), |Z|=157.445 pu, positive sequence
  IEEE-519         : 3535/3539 buses compliant
[exit 0]
```

At bus 4992 the network resonates at h = 4.9818, 249.09 Hz, 0.018 orders (0.91 Hz) below the 5th harmonic, which the
default G5 six-pulse profile injects at 31.4 % of its fundamental (20 % in the idealised spectrum).
The sharpness of 83.5 says the peak is very tall against the rest of the band, and
the sweep peak (157.445 pu) is 40 % above the whole-number value (112.064 pu): the integer scan alone understates it.
Read the two together. A tall sharp peak close to an order the source emits is the dangerous case; a tall peak
where none of the listed sources emits carries no emission from those sources, and matters once a source emits there or
when background distortion, which this study does not model, reaches it.

## Variations

### Metered spectra: `--spectra`

```bash
gridsim harmonics case9 --spectra docs/_captures/harmonics/spectra-case9.json
```

```
$ gridsim harmonics case9 --spectra docs/_captures/harmonics/spectra-case9.json
harmonics: case IEEE 9-bus (WSCC), 1 source(s), orders 24
  worst voltage THD : 4.71% at bus 9 (relative to the solved fundamental voltage)
  resonance (bus 9): order 7, |Z|=0.658 pu, sharpness 3.8
  fine sweep peak  : h=7.4750 (448.50 Hz), |Z|=0.688 pu, positive sequence
  IEEE-519         : 1/9 buses compliant
[exit 0]
```

A lower, metered spectrum (below) gives a THD of 4.71 % instead of 6.31 %, and one bus, bus 1, passes. The
resonance lines are identical to the default run: they describe the network, not the source. The source set loads,
is validated against the case, and is then the only change.

### The `--spectra` file

A source set is a JSON document with schema `gridsim-harmonics/1`. Each source is the harmonic current spectrum
injected at one bus. This is the file used above, exactly as stored:

```json
{
  "schema": "gridsim-harmonics/1",
  "case": "IEEE 9-bus (WSCC)",
  "baseMva": 100.0,
  "takenUtc": "2026-08-01T12:00:00Z",
  "note": "Metered 6-pulse drive at bus 9 (example)",
  "sources": [
    { "bus": 9, "sourceType": "6-pulse", "fundamentalCurrentPu": 0.5, "provenance": "Recorded",
      "spectrum": [ { "order": 5, "magnitudePercent": 18.0, "phaseDeg": 0.0 },
                    { "order": 7, "magnitudePercent": 11.0, "phaseDeg": 0.0 },
                    { "order": 11, "magnitudePercent": 4.5, "phaseDeg": 0.0 },
                    { "order": 13, "magnitudePercent": 3.0, "phaseDeg": 0.0 } ] }
  ]
}
```

| Field | Meaning |
|---|---|
| `schema` | Must be exactly `gridsim-harmonics/1`; any other value is refused. |
| `case` | The case the set describes. Informational; it is not checked against the case you run. |
| `baseMva` | The system base the author assumed, in MVA. Default 100 when omitted. When both this value and the case's base are above 0 they must be equal (100 for case9), or the load fails. |
| `takenUtc` | When the spectra were measured. Optional. When present it must be at least 169 hours in the past, or the load is rejected (see [Pitfalls](#pitfalls)). |
| `note` | Free text. Carried in the file only. |
| `sources[].bus` | The bus the equipment is connected to. It must exist in the case. |
| `sources[].sourceType` | A label for the equipment. It is echoed into the export and does not select a library spectrum. |
| `sources[].fundamentalCurrentPu` | The source's fundamental current in per-unit on the system base. It is the reference for the percentages. |
| `sources[].provenance` | `Recorded` (metered), `DerivedFromRecorded` (metered, then rescaled or aggregated) or `CuratedPlaceholder` (assumed). It is echoed into the export. |
| `sources[].spectrum[].order` | The harmonic order, a whole number of at least 1. |
| `sources[].spectrum[].magnitudePercent` | Current magnitude at that order as a percentage of `fundamentalCurrentPu`. |
| `sources[].spectrum[].phaseDeg` | Phase of that current in degrees. Optional; 0 when absent. |

Names are camelCase and matched without regard to case; the provenance is a name, not a number. The injected current
at order `h` is `magnitudePercent / 100 x fundamentalCurrentPu` at angle `phaseDeg`. Two sources on one bus add as
phasors. Several buses can carry sources in one file: give each its own entry.

The provenance label is a promise about the data. The default study is `CuratedPlaceholder`. The export's headline
`provenance` reads `Recorded` only when the set holds at least one source and every source is `Recorded` or `DerivedFromRecorded`;
one assumed source, or an empty set, makes the whole study `CuratedPlaceholder`. A file can mix metered spectra where you have meters and placeholders elsewhere,
and each bus upgrades in place when data arrives.

### The default source: `--synth-sources`

Without `--spectra` the study injects one library six-pulse source: 0.5 pu fundamental current at the bus with the
largest demand `Pd`, tagged `CuratedPlaceholder`. Its spectrum is the cited ENA
EREC G5 Issue 5 Table B.3 six-pulse profile (`6-pulse-g5`, orders 5 to 49, THDI 34.4 %), and the Web HARMONICS tab
labels it `typical - synthetic six-pulse source, G5 Issue 5 Table B.3 profile [VERIFY]`. `--synth-spectrum ideal`
(and the tab's "idealised 1/h" choice) gives the idealised spectrum instead: `100 / h` percent at the orders 5, 7, 11,
13, 17, 19, 23 and 25 (5th 20 %, 7th 14.3 %, THDI 29.0 %), labelled `assumed-typical - synthetic 6-pulse source
[VERIFY]`. The output names the spectrum used on a `synthetic source :` line. The sample output below is the idealised
spectrum's run without that line. `--synth-sources` selects the default explicitly:

```
$ gridsim harmonics case9 --synth-sources
harmonics: case IEEE 9-bus (WSCC), 1 source(s), orders 24
  synthetic source : 6-pulse-g5 at bus 9, 0.5 pu fundamental (G5 Issue 5 Table B.3 six-pulse profile, the default; --synth-spectrum ideal for the idealised 1/h)
  worst voltage THD : 6.31% at bus 9 (relative to the solved fundamental voltage)
  resonance (bus 9): order 7, |Z|=0.658 pu, sharpness 3.8
  fine sweep peak  : h=7.4750 (448.50 Hz), |Z|=0.688 pu, positive sequence
  IEEE-519         : 0/9 buses compliant
[exit 0]
```

The output is identical to the run without the flag. It exists so a script can state its intent. Giving
`--synth-sources` together with `--spectra` is a contradiction and exits 2:

```
$ gridsim harmonics case9 --spectra docs/_captures/harmonics/spectra-case9.json --synth-sources
error: --spectra and --synth-sources are mutually exclusive (a measured source file, or the synthetic default).
[exit 2]
```

The default is a screen on an assumed spectrum: it answers "what could resonate here" before you have meters. It is
not a measurement of any real converter.

### Converter sources: `--converter-sources`

`--converter-sources rated` or `dispatched` places one source at every in-service converter-interfaced unit (battery,
PV, wind, tidal, HVDC or interconnector; not a power-flow-only device), each with the ENA EREC G5 Issue 5 Table B.3
profile for its technology: the active-front-end profile (`afe-g5`) for a self-commutated converter, and the
twelve-pulse profile (`12-pulse-g5`, `[VERIFY]`) for a unit marked as feeding no fault current, a line-commutated HVDC
link. `rated` scales each profile by the unit's rated current (`MBase / BaseMva` pu, the G5 Stage 1C / 2A basis);
`dispatched` scales it by the unit's output at the solved operating point (the fundamental is solved first, so the
slack's P and each PV unit's Q are the solved values), and lists a unit at zero output as not modelled:

```bash
gridsim harmonics gb-synthetic --converter-sources dispatched
```

Sources add linearly (G5 s.6.3.3: no declared diversity). The study then analyses orders 2 to 50, Table B.3's range;
the triplen components (zero sequence) are left out of the balanced study, and their count and the first one are
printed. A case with no injecting converter unit exits 1, and `--converter-sources` with `--spectra` or
`--synth-sources` exits 2. Every flag is in [ch. 19, harmonics](<0019 CLI reference.md#harmonics>).

### Converters as resonant plant: `--afe-reactance`

G5 Issue 5 s.3.29 says that active-front-end converters may be resonant plant through their internal impedance.
`--afe-reactance <pu>` (and `--afe-resistance <pu>`, default 0) models every active-front-end unit as a shunt `R + j h X`
to ground at its bus, on the unit's own rating; with `--converter-sources` each such unit becomes a Norton equivalent,
its source in parallel with that impedance. No document gives a value, so the flag has no default and the result is a
`[VERIFY]` study: without it every converter stays an ideal current source. The output adds an `AFE impedance` line. A
case with no active-front-end unit exits 1.

### Another scan bus: `--bus`

`--bus <id>` points the resonance scan and the fine sweep at a bus of your choice, such as the point of connection of
a planned capacitor bank. The worked example in [Reading a resonance](#reading-a-resonance) shows `--bus 1` on `case30`.
THD and the IEEE 519 count do not change. A non-integer value exits 2 with `error: --bus expected an integer but got '<value>'`, where `<value>` is the text you passed (for example `abc`). A bus id the case does not contain exits 2 with `error: harmonics: --bus <id> is not a bus of <case>.`, before the study runs.

### The full export: `--enableExport`

```bash
gridsim harmonics case9 --enableExport
gridsim harmonics case9 --enableExport=exports/case9-harmonics.json
```

`--enableExport` writes the whole study to one JSON file, `results/harmonic-export.json` under the current directory by default,
or the path given after `=`. Missing directories are created. A run that writes the file prints the extra line:

```
$ gridsim harmonics case9 --enableExport=docs/_captures/harmonics/case9-export.json
harmonics: case IEEE 9-bus (WSCC), 1 source(s), orders 24
  synthetic source : 6-pulse-g5 at bus 9, 0.5 pu fundamental (G5 Issue 5 Table B.3 six-pulse profile, the default; --synth-spectrum ideal for the idealised 1/h)
  worst voltage THD : 6.31% at bus 9 (relative to the solved fundamental voltage)
  resonance (bus 9): order 7, |Z|=0.658 pu, sharpness 3.8
  fine sweep peak  : h=7.4750 (448.50 Hz), |Z|=0.688 pu, positive sequence
  IEEE-519         : 0/9 buses compliant
  export -> docs\_captures\harmonics\case9-export.json
[exit 0]
```

The CLI prints the absolute path; the capture shows it relative to the repository.

The document has schema `gridsim-harmonic-export/1`. Its top-level fields, in order:

| Field | Content |
|---|---|
| `schema` | `gridsim-harmonic-export/1`. |
| `meta` | `Case`, `Backend` (always `harmonics`), `GitCommit`, `TimestampUtc` (the time of the run), `ElapsedMs` and `Machine`. |
| `provenance` | The headline tag: `Recorded` only when every source is `Recorded` or `DerivedFromRecorded`, else `CuratedPlaceholder`. |
| `model` | `name`, `baseMva`, `busCount` and one entry per bus with `id`, `baseKv` and `name` (a bus with no name omits it). |
| `orders` | The harmonic orders solved: 3, 5, ... 49. |
| `harmonicModel` | How `Y(h)` was built: `includeGeneratorSources`, `includeLoadShunts`, `skinCoefficient`, `cableSkinCoefficient`, `longLineCorrection`, `balancedHarmonicSequences`; then `generatorSources` (each machine's `bus`, `name` (omitted when the machine has none) and `xSystemPu`, its source reactance in pu on the system base) and `loads` (each load bus's `pdMw` and `qdMvar`). It is everything beyond the case itself that the solve used. |
| `sources` | The input spectra: per source `bus`, `sourceType`, `provenance`, `fundamentalCurrentPu` and the `spectrum` entries (`order`, `magnitudePercent`, `phaseDeg`). |
| `voltage` | Every non-zero harmonic voltage phasor: `order`, `bus`, `vmPu` (magnitude, pu) and `vaDeg` (angle, degrees). Zero entries are left out, so the multiples of 3 never appear. |
| `indices` | `worstBusId`, `worstThdPct`, and `thd`: the voltage THD of every bus as `bus` and `thdPct`. |
| `resonance` | The whole-number scan: `bus`, peak `order`, `zMag` (pu) and `sharpness`. |
| `resonanceSweep` | The fine sweep: `bus`, `peakOrder`, `peakHz`, `zMag`, `sequence` (`Positive` or `Negative`), `minOrder`, `maxOrder` and `gridPoints`. |
| `compliance` | One entry per bus, the IEEE 519 verdict: `bus`, `baseKv`, `thdPct`, `thdLimitPct`, `worstIhdPct`, `worstIhdOrder`, `ihdLimitPct`, `compliant`, `voltageLevelKnown`. |

The `resonance` and `resonanceSweep` blocks of the run above read:

```
  "resonance": {
    "bus": 9,
    "order": 7,
    "zMag": 0.6575669200741651,
    "sharpness": 3.83763679770113
  },
  "resonanceSweep": {
    "bus": 9,
    "peakOrder": 7.47497016807171,
    "peakHz": 448.4982100843026,
    "zMag": 0.687623565377445,
    "sequence": "Positive",
    "minOrder": 1.5,
    "maxOrder": 50,
    "gridPoints": 486
  },
```

The sweep grid has 486 points: `(50 - 1.5) / 0.1 + 1`. The export carries full precision; the console rounds
(`0.658` is `zMag` 0.6575669...). The export is not self-contained: it carries no branch impedances, charging, taps or bus shunts. The independent
checker `tools/crossval/crossval_harmonics.py` needs three inputs: `--case`, a case directory holding `system.json`,
`buses.json`, `transmission.json` and optionally `transformer.json` (a built-in case name such as `case9` is not
accepted); `--sources`, a `gridsim-harmonics/1` file (a default run has none, so write one from the export's `sources`
block); and `--export`, the export. It rebuilds `Y(h)` from the case directory plus `harmonicModel` and diffs the
voltages and THD without calling GridSim. The
[technical chapter](<../technical/0019 Harmonics.md>) recomputes bus 9's THD from this file by hand.

## What non-compliance means

The verdict is IEEE 519-2014 voltage-distortion limits at the bus, chosen by the bus's nominal voltage `baseKv`:

| Nominal voltage | THD limit | Single-harmonic limit |
|---|---|---|
| up to 1 kV | 8.0 % | 5.0 % |
| above 1 kV, up to 69 kV | 5.0 % | 3.0 % |
| above 69 kV, up to 161 kV | 2.5 % | 1.5 % |
| above 161 kV | 1.5 % | 1.0 % |

A bus is compliant when `thdPct` is at or below `thdLimitPct` and `worstIhdPct` (the largest single-order voltage as a
percentage of the solved fundamental) is at or below `ihdLimitPct`. Both tests must hold. In the case9 run every bus
fails both: bus 1, the least distorted, has a THD of 1.978 %, over the 1.5 % limit, and a 5th harmonic of 1.545 %, over
the 1.0 % limit.

What the verdict does and does not say:

- It is a statement about the sources you gave. A failure means those injections, on this network, push that bus over
  the limit. A pass means only that those injections do not.
- On the default source it is a screen on an assumed spectrum. The `CuratedPlaceholder` provenance in the export says so.
  Act on a pass or fail only as far as the spectrum is grounded.
- IEEE 519 limits distortion at a point of common coupling, and the study adds only the sources you listed. Distortion
  already on the network from other equipment is not in the number.
- It is voltage distortion, not current distortion. Per-customer current limits are not assessed.
- The Web HARMONICS tab also shows a G5/5 planning-level verdict (headline THD levels only). The command line prints
  IEEE 519 only. The G5/5 levels are in [ch. 19 of the technical manual](<../technical/0019 Harmonics.md>).

## Pitfalls

- **A source order the study does not analyse is refused.** The study solves the odd orders 3 to 49 (orders 2 to 50
  under `--converter-sources`). A spectrum line
  with a non-zero magnitude at any other order (even orders such as 2, 4, 8, or orders above 49) would never enter the
  solve and the THD would be understated, so the run stops with exit 1 and names every such line (order 1, the
  reference of the percentages, is allowed). This is the output for a file with orders 4, 2, 50 and 52:

  ```
  $ gridsim harmonics case9 --spectra docs/_captures/harmonics/spectra-case9-even.json
  error: harmonics: 4 source component(s) are at orders this study does not analyse (it analyses orders 3,5,7,9,11,13,15,17,19,21,23,25,27,29,31,33,35,37,39,41,43,45,47,49): bus 9 (6-pulse) order 4 at 18 %; bus 9 (6-pulse) order 2 at 11 %; bus 9 (6-pulse) order 50 at 4.5 %; bus 9 (6-pulse) order 52 at 3 %. They would be ignored and the THD understated; remove them from the spectra file.
  [exit 1]
  ```

  Remove those lines (and accept that their distortion is not studied), or put the source energy at odd orders, 5 to
  49, that are not multiples of 3.

- **A source that injects a triplen order is refused.** Orders 3, 9, 15, ... (and 6, 12, ...), at any order, are zero-sequence
  quantities that the positive-sequence network of this study cannot carry. A source with a non-zero entry at one stops
  the run with a message naming the bus and the order:

  ```
  $ gridsim harmonics case9 --spectra docs/_captures/harmonics/spectra-case9-triplen.json
  error: harmonics: Harmonic source at bus 9 (6-pulse) injects order 3, a triplen (zero sequence). The balanced positive-sequence study cannot model it (that needs the zero-sequence network); use the three-phase harmonic study. (Parameter 'sources')
  [exit 1]
  ```

  A triplen above 49 (51, 57) is outside the analysed set and is refused by the previous check. The library 6-pulse and 12-pulse spectra have no triplens; the `ev-charger`, `pv-inverter`,
  `heat-pump-inverter`, `afe-g5`, `arc-furnace` and `saturated-transformer` spectra do (the last two also carry even orders). A
  `--spectra` file cannot name a library spectrum, because `sourceType` is a label only, so using one means copying its
  values by hand, and its triplen lines must be removed first. The full library is in
  [technical ch. 19](<../technical/0019 Harmonics.md>).

- **A dated file is rejected if it is too recent.** GridSim replays history and does not forward-infer. A `takenUtc`
  inside the last 169 hours is refused and the run exits 2:

  ```
  $ gridsim harmonics case9 --spectra docs/_captures/harmonics/spectra-case9-future.json
  REJECTED (forward inference): harmonic source set timestamp 2099-08-01T12:00:00.0000000Z is newer than the historical horizon 2026-09-26T07:28:31.2951662Z (data must lag real time by >=169 h). GridSim replays history only - it never processes recent or future data, so it cannot forward-infer.
  [exit 2]
  ```

  The horizon in the message is computed from the clock at the time of the run.

- **A source set that does not fit the case is refused with every offender named.** A wrong `baseMva`, a bus that is not
  in the case, a non-finite value or an order below 1 exits 2:

  ```
  $ gridsim harmonics case9 --spectra docs/_captures/harmonics/spectra-case9-bad.json
  error: failed to load --spectra 'docs/_captures/harmonics/spectra-case9-bad.json': Harmonic source set 'IEEE 9-bus (WSCC)' failed validation:
    set baseMva 50 does not match model BaseMva 100
    source #0 (6-pulse @ bus 99): bus 99 does not exist in the model
  [exit 2]
  ```

  A missing file, an unreadable file and a wrong `schema` also exit 2 with `error: failed to load --spectra`.
  `--spectra` with no path exits 2 with `error: --spectra expected a gridsim-harmonics/1 file path`.

- **A case with no path to ground fails; it never reports 0 %.** If part of the network has no machine, load, shunt or
  line charging to ground it at some injected order, the run prints `harmonics: FAILED - ` and the message to standard
  error, exits 1, and prints no THD and no verdict. This capture runs a two-bus case with one line and no machine, load
  or shunt:

  ```
  $ gridsim harmonics docs/_captures/harmonics/case-singular
  harmonics: FAILED - Harmonic penetration: the harmonic network admittance Y(h) is singular at order 5, so no harmonic voltage can be computed. Part of the network has no path to ground at this frequency (a radial feeder or island with no line charging, shunts or source impedance). Model the machine source impedance (HarmonicModelOptions.IncludeGeneratorSources, or HarmonicModelOptions.Study) or the loads (IncludeLoadShunts) to ground it.
  [exit 1]
  ```

- **A bus with no base kV can never pass.** `case14` has no base kV on any bus. It is judged at the strictest tier
  (1.5 % THD, 1.0 % single harmonic) and counted as not compliant, and a warning says so:

  ```
  $ gridsim harmonics case14
  harmonics: case IEEE 14-bus, 1 source(s), orders 24
    worst voltage THD : 7.66% at bus 3 (relative to the solved fundamental voltage)
    resonance (bus 3): order 25, |Z|=1.028 pu, sharpness 1.2
    fine sweep peak  : h=24.2371 (1211.85 Hz), |Z|=1.031 pu, positive sequence
    IEEE-519         : 0/14 buses compliant
    WARNING          : 14 bus(es) have no base kV - judged at the strictest tier and NOT certified
  [exit 0]
  ```

  `0/14` here is a statement that GridSim cannot certify, not that all 14 buses exceed a limit.

- **A peak on the sweep boundary is not a resonance.** `h=50.0000` as the fine sweep peak means no in-band resonance is as tall as the value
  at h = 50; see [Reading a resonance](#reading-a-resonance).

## Limits

- The study covers the whole-number odd orders 3 to 49, and the fine sweep covers h = 1.5 to 50 (75 Hz to 2.5 kHz at
  50 Hz). The supraharmonic band (2 to 150 kHz) is not modelled.
- The study is positive-sequence and balanced. Triplen sources cannot be entered here; the three-phase harmonic study
  on a distribution feeder is `dist3ph harmonics` ([ch. 7](<0007 Three-phase distribution feeders.md>)).
- Spectra are one fixed current per order at each source. A converter whose emission depends on its terminal voltage is
  not modelled on the command line; the coupled harmonic flow is Core API only.
- A spectrum line at an order outside the analysed set cannot be studied: it is refused, not included.
- The THD in the console, the export and the Web HARMONICS tab is relative to each bus's solved fundamental voltage. The
  console solves the fundamental itself (as `--once` does); the Web tab uses the live frame, so the two agree only when
  the operating points agree.
- Only IEEE 519 voltage limits are checked at the command line; per-customer current limits are not.
- The Web HARMONICS tab and the `cycle` harmonics job skip cases above 1,500 buses.

## See also

- [Technical ch. 19, Harmonics](<../technical/0019 Harmonics.md>) - `Y(h)`, penetration, the sweep, indices, the
  limit tables and a hand-recomputed THD.
- [ch. 19, CLI reference: harmonics](<0019 CLI reference.md#harmonics>) - the verb's flags and exit codes.
- [ch. 5, Running power flows](<0005 Running power flows.md>) - the fundamental solve and `--enableExport` for it.
- [ch. 2, Loading networks](<0002 Loading networks.md>) - case selection in full.
- [Technical ch. 10, File and wire formats](<../technical/0010 File and wire formats.md>) - the versioned-schema
  conventions of `gridsim-harmonics/1` and `gridsim-harmonic-export/1`.
- `gridsim experimental harmonic-kcl <case>` audits `Y(h) V(h) = I(h)` on the study penetration, and
  `gridsim experimental passivity <case> [--bus ID]` checks that the driving-point impedance is passive over 5 to 2500 Hz.
