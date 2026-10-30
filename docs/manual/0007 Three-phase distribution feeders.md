# 7. Three-phase distribution feeders

A distribution feeder is radial, carries unequal loads on its three phases, and has laterals that run on one or two
phases only. The positive-sequence verbs of the other chapters assume a balanced network and cannot represent any of
that. `gridsim dist3ph` is the verb for it. It reads a feeder written in the `gridsim-dist3ph/1` file format or its
JSON v2 form `gridsim-dist3ph/2`, or a radial positive-sequence case that it lifts into a balanced three-phase feeder,
and runs one of four studies on it: `sweep` (the unbalanced power flow, the default), `fault` (sequence-network fault
currents at one bus), `estimate` (three-phase state estimation from a measurement file) and `harmonics` (harmonic
voltages from one balanced harmonic source). A fifth mode, `to-v2`, writes the feeder in the v2 form. The verb is
marked EXPERIMENTAL in its own banner. This chapter covers the feeder file, the measurement file,
the bundled feeders, each study with a captured run, and the figures that measure unbalance.

Every command here exits 0 on success, 1 for an input error, 2 for a usage error and 3 when a solve does not converge
(see [Exit codes](#exit-codes)). Timings do not appear in this verb's output. The iteration counts in the captures are
those of this machine; a test checks that two runs on one machine give the same count.

## Before you start

### The feeder file

A feeder is one JSON file whose `schema` member is exactly `gridsim-dist3ph/1`. The reader is strict. Member names are
case-sensitive, and an unknown member, a duplicate member, a missing required member, a wrong JSON type, a number that
is not finite, a matrix that is not 3 by 3, a duplicate id, an unknown bus or line-code reference, a branch that joins a
bus to itself and a network that is not one radial tree from the single source are all refused. Every refusal names the
file and the JSON path of the offending value and exits 1. Comments are allowed in the file; trailing commas are not.

The top level holds these members. `schema`, `name`, `description`, `baseMva` and `buses` are required; the others are
optional.

| Member | Holds |
|---|---|
| `name`, `description` | Free text. Put the citation of the data source in `description`. |
| `baseMva` | The three-phase MVA base, above 0. Per-phase power is per-unit on `baseMva` / 3. |
| `buses` | At least two buses. Each has `id` (an integer), `baseKvLL` (line-to-line kV, above 0), an optional `name` (unique, shown in place of the id in reports, as `RG1` is in the 123-node feeder) and, on exactly one bus, a `source` block. |
| `lineCodes` | Impedance data shared by lines: `id` (unique, non-empty, named by a line's `code`), a 3 by 3 series matrix as `r` and `x` with `unit` `ohm/mile` or `ohm/km`, and optionally a symmetric 3 by 3 shunt susceptance `b` with `bUnit` `uS/mile` or `uS/km` (give both or neither). |
| `lines` | `id`, `from`, `to`, `code`, `length` (above 0), `lengthUnit` (`ft`, `mile`, `m` or `km`) and an optional `phases`. |
| `transformers` | `id`, `from`, `to`, `connection`, `kva`, `kvLLFrom`, `kvLLTo`, `rPercent`, `xPercent`. |
| `switches` | `id`, `from`, `to`, `closed` (true or false, required) and an optional `phases`. |
| `regulators` | `id`, `from`, `to`, an optional `phases`, `tapStepPercent` (above 0) and `taps` (one integer per phase). |
| `capacitors` | `id`, `bus` and `kvar`, one value per phase. |
| `loads` | `id`, `bus`, `connection`, `model`, `kw` and `kvar`, one value per phase. |
| `converters` | Active-front-end converter units for the harmonic study: `id`, `bus`, `kva`, `xPercent` and an optional `rPercent`, the internal impedance in percent on the unit's own rating. Read only by `harmonics --afe feeder`. |

Every element kind the reader supports is below. Nothing else is accepted.

**The source.** One bus carries `source`. It is an infinite bus of zero impedance. `magnitudePu` (three values, each
above 0) and `angleDeg` (three values) are optional; with neither the source is the balanced 1.0 pu set at 0, -120 and
+120 degrees. Give unequal magnitudes or angles to feed the feeder from an unbalanced source.

**Lines.** The impedance of a line is its code's matrix times its length, converted to per-unit on the line's own
voltage level (impedance base `baseKvLL`^2 / `baseMva` ohm). A line joins two buses of the same `baseKvLL`; a line
between voltage levels is refused with a message telling you to use a transformer. `phases` is `abc` when omitted. A
single-phase or two-phase lateral gives `a`, `b`, `c`, `ab`, `ac` or `bc`; its code must be zero on every other phase
and must have a non-zero self impedance on each phase it carries, or the file is refused. A bus has the phases of the
branch that feeds it, and a branch that carries a phase its upstream bus lacks is refused by name. A `b` matrix adds
line charging as a pi section, half the shunt admittance at each end.

**Transformers.** Six connections exist. `gy-gy`, grounded wye to grounded wye: in per-unit
its leakage impedance on every phase, `z = (rPercent + j xPercent) / 100 x (baseMva x 1000 / kva)`, with all three
sequences passing and no phase shift. `d-gy`, delta to grounded wye with the delta on the `from` side: a step-down (1.95.251)
when the delta side is the higher voltage, the grounded-wye side then lagging by 30 degrees, and a step-up (1.95.252)
when it is the lower voltage, the grounded-wye side then leading by 30 degrees (the ANSI rule: the higher-voltage side
leads); the delta side carries no zero-sequence current, and the leakage impedance sits on the wye side. Both reproduce
the IEEE 4 Node Test Feeder's published D-GrY results (`data/dist3ph/ieee4-dGy-stepdown-*.json` and
`ieee4-dGy-stepup-*.json`). Equal rated voltages on the two sides are refused (no ANSI direction). `y-d`
(1.95.253), ungrounded wye (`from`) to delta, step-down or step-up by the rated voltages with the same 30 degree rule,
and `d-d` (1.95.253), delta to delta with no phase shift: both with equal winding impedances, and both make the
secondary a three-wire system (no neutral), so everything fed from it must be delta connected: a wye load, a capacitor
bank, line charging, a regulator or a `gy-gy` transformer there is refused by name (a `d-gy` grounds it again). Voltages
on a three-wire bus are reported as the equivalent line-to-neutral ones with no zero sequence. They reproduce the IEEE 4
Node Test Feeder's published Y-D and D-D results (`data/dist3ph/ieee4-yD-*.json`, `ieee4-dD-*.json`). Two open banks
complete the set: `oy-od`, open wye / open delta with the two primaries on `from`-side phases A and B to earth, modelled
as the step-down connection only (a step-up `oy-od`, the higher-voltage side leading, is refused), and `od-od`, open
delta / open delta. `y-d`, `d-d`, `oy-od` and `od-od` take an optional `windings` array of per-winding `rPercent` and
`xPercent` on the bank's kVA (three, `ab` / `bc` / `ca`, for `y-d` and `d-d`; two, `ab` / `bc`, for the open banks) in
place of the bank's `rPercent` and `xPercent`, for unequal winding impedances; giving both is refused. Only the sweep
models the transformer elements other than `gy-gy`; the estimators, the fault study and the harmonic penetration
refuse them by name. `kvLLFrom` and `kvLLTo`
are the rated voltages: equal to the two buses' `baseKvLL` for a transformer at its rated ratio, or (1.95.254) within 20 %
of them for an off-nominal ratio (a tap), which is modelled as the per-unit ideal ratio
`t = (baseKvLL_from / kvLLFrom) x (kvLLTo / baseKvLL_to)` with the nameplate impedance moved to the `to` bus base by
`(kvLLTo / baseKvLL_to)^2` (for `gy-gy` it is the regulator element's ideal ratio on every phase). A rated voltage more
than 20 % from its bus base is refused as a data error. `rPercent` and `xPercent` must not be negative and must not both
be 0. Any other `connection` is refused with a message listing the six, never approximated. The transformer element
has no magnetising member; one given is refused as an unknown member.

**Switches.** A closed switch is a branch of zero impedance; the voltage and current pass through it unchanged. An open
switch is not a branch: it is read, counted in the banner line and left out of the network, so a normally open tie
never closes a loop. A switch joins two buses of one voltage level.

**Regulators.** An ideal step-voltage regulator from the `from` bus (the bus nearer the source) to its own output bus
`to`, with no series impedance. Per phase the voltage ratio is `1 + (tapStepPercent / 100) x tap`; `taps` holds one
integer per phase, and a phase the regulator does not carry must read 0. Taps are given in the file and stay fixed: the
regulator does not control anything.

**Capacitors.** A wye bank at a bus with `kvar` per phase, delivered at the bus's nominal voltage and held as a constant
susceptance, so the reactive power it supplies falls with the square of the voltage. A value must not be negative, at
least one must be above 0 and a bank cannot put a unit on a phase its bus lacks.

**Loads.** `kw` and `kvar` are per phase, taken at nominal voltage. `connection` is `wye` or `delta`; a delta load's
three entries are the a-b, b-c and c-a branches. `model` is `constant-power`, `constant-current` or
`constant-impedance`. A load with a non-zero entry on a phase its bus lacks is refused. A ZIP load is written as up to
three loads on one bus, one of each model; the model name `zip` is refused with a message saying so.

**Radial tree.** After all the elements are read, `n` buses need exactly `n - 1` branches (lines, transformers, closed
switches and regulators together) and every bus must be reachable from the source. A wrong branch count (a loop, or a bus
cut off) exits 1 with the counts, for example `4 buses need exactly 3 branches ... found 4`. A feeder with `n - 1`
branches that still leaves buses unreached, because a loop sits elsewhere, exits 1 with the unreached bus numbers.

### The measurement file

The `estimate` study reads a `gridsim-dist3ph-measurements/1` file under the same strict rules. It holds up to three
members and at least one of them must be present.

- `voltageMagnitudes`: each entry gives `bus`, `phase` (`a`, `b` or `c`), `valuePu` (above 0) and `sigmaPu` (above 0).
  Voltages are per-unit on the bus's line-to-neutral base.
- `currents`: each entry gives `from`, `to`, `phase`, `realPu`, `imagPu` and `sigmaPu`. The value is the current phasor
  into the `from` end, in rectangular form, per-unit on the per-phase power base `baseMva` / 3 and the bus line-to-neutral voltage base (the current base
  that `fault` prints). For `wls` and `wlav` the branch can be named in either orientation, and the reversed
  orientation is negated. `--method branch-current` does the same on a pure series branch, and refuses a reversed one
  across a regulator or a charged line (exit 1, naming the branch; see Branch-current estimation). A pair of buses that no branch joins is refused.
  `sigmaPu` applies to each rectangular component.
- `pseudoVoltage`: `valuePu` and `sigmaPu`. Each non-source bus with no voltage measurement receives one
  pseudo-measurement per phase it has (a lateral only on its own phases) at that value. They are priors, not observations, and the sigma should be loose.

An optional `description` is read and ignored. The bundled `data/dist3ph/ieee4-gyGy-stepdown-unbalanced.measurements.json`
is a synthetic set made from the sweep solution of the unbalanced 4-node feeder, rounded to 6 decimals, with sigma
0.001 pu throughout. It is not recorded telemetry.

### The bundled feeders

`data/dist3ph` holds the IEEE 4 Node Test Feeder in each of its published transformer connections, the IEEE 123 Node
Test Feeder, and the published results that the sweep is checked against. The shipped files are exempt from the bus
limit of the installed licence tier.

| File | Feeder | Content |
|---|---|---|
| `ieee4-gyGy-stepdown-balanced.json` | IEEE 4 Node Test Feeder, grounded-wye / grounded-wye step-down, balanced load | 4 buses, 3 branches, 1 load of 1800 kW per phase. 12.47 kV source, 2000 ft line, 6000 kVA 12.47/4.16 kV transformer, 2500 ft line. |
| `ieee4-gyGy-stepdown-unbalanced.json` | The same feeder with the published unbalanced load | Load of 1275, 1800 and 2375 kW on phases a, b and c. |
| `ieee4-dGy-stepdown-balanced.json` | IEEE 4 Node Test Feeder, delta / grounded-wye step-down, balanced load (1.95.251) | As the gy-gy feeder, but a `d-gy` transformer and a three-wire line 1-2 (the delta side has no neutral). |
| `ieee4-dGy-stepdown-unbalanced.json` | The same D-GrY feeder with the published unbalanced load (1.95.251) | Load of 1275, 1800 and 2375 kW on phases a, b and c. |
| `ieee4-dGy-stepup-balanced.json`, `ieee4-dGy-stepup-unbalanced.json` | IEEE 4 Node Test Feeder, delta / grounded-wye step-up, balanced and unbalanced (1.95.252) | As the step-down D-GrY feeders, but a 12.47/24.9 kV transformer (24.9 kV at nodes 3 and 4). |
| `ieee4-yD-{stepdown,stepup}-{balanced,unbalanced}.json` | IEEE 4 Node Test Feeder, ungrounded-wye / delta, the four published cases (1.95.253) | Four-wire line 1-2, three-wire line 3-4, delta load at node 4 (1275 / 1800 / 2375 kW on a-b / b-c / c-a when unbalanced). |
| `ieee4-dD-{stepdown,stepup}-{balanced,unbalanced}.json` | IEEE 4 Node Test Feeder, delta / delta, the four published cases (1.95.253) | Three-wire lines 1-2 and 3-4, delta load at node 4. |
| `ieee123.json` | IEEE 123 Node Test Feeder | 128 buses (including the four regulator output nodes RG1 to RG4), 127 branches, 85 loads (56 constant-power wye, 12 constant-current wye, 14 constant-impedance wye, and one each of constant-power, constant-current and constant-impedance delta), 4 capacitor banks, 4 regulators on the published taps, 5 closed and 2 open switches, 60 single- or two-phase branches. |
| `ieee123.published-voltages.csv`, `ieee123.published-currents.csv` | The published 123-node voltage profile and branch currents | The reference values the sweep is compared with. |
| `ieee4-gyGy-stepdown-unbalanced.measurements.json` | Synthetic measurement set for the unbalanced 4-node feeder | 9 voltage magnitudes and 9 current phasors. |

The 4-node line code is the published conductor geometry reduced by Kersting's modified Carson equations and Kron
reduction of the neutral, computed offline (GridSim has no Carson or phase Kron routine, so the values are data); the phase-a self impedance is 0.4576 + j1.0780 ohm/mile, and the file
neglects line shunt capacitance. The sweep agrees with the published 4-node node voltages to the last printed digit of
the publication (half a volt, 0.05 degree), and with the published 123-node voltages and currents within the asserted bounds of 6e-4 pu, 0.06
degree and 0.1 A (`MultiPhaseJsonLoaderTests`, `MultiPhaseIeee123Tests`). The remaining difference is attributed to
the switches, which are ideal here while the published run gives them an impedance the data does not carry. The 123-node file
omits the substation transformer (the published results start at node 150 at 1.0 pu), the switches that tie to nodes outside
the feeder and the transformer XFM-1 with node 610, which serve no load; its `description` lists each choice.

The feeder argument can also be a radial positive-sequence case, a MATPOWER `.m` file or a JSON case folder, which is
lifted into a balanced three-phase feeder (see [Lifting a case](#lifting-a-case)).

## Worked example

Solve the unbalanced IEEE 4-node feeder. The mode word defaults to `sweep`:

```bash
gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json
```

```
$ gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json
THREE-PHASE DISTRIBUTION (sweep) - IEEE 4-node test feeder, grounded-wye / grounded-wye step-down, unbalanced load   [EXPERIMENTAL]
  file: data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json
  4 buses, 3 branches, 1 loads, base 6 MVA (three-phase)
  sweep CONVERGED in 39 iteration(s), max voltage change 7.191E-011 pu (tolerance 1.000E-010)
  | bus | kV LL | phase | |V| pu | |V| V (LN) | angle deg | VUF % |
  |-----|-------|-------|--------|------------|-----------|-------|
  | 1 | 12.47 | a | 1.000000 | 7199.56 | 0.000 | 0.0000 |
  | 1 | 12.47 | b | 1.000000 | 7199.56 | -120.000 |  |
  | 1 | 12.47 | c | 1.000000 | 7199.56 | 120.000 |  |
  | 2 | 12.47 | a | 0.995022 | 7163.72 | -0.140 | 0.3049 |
  | 2 | 12.47 | b | 0.987628 | 7110.48 | -120.185 |  |
  | 2 | 12.47 | c | 0.983675 | 7082.03 | 119.265 |  |
  | 3 | 4.16 | a | 0.959910 | 2305.49 | -2.258 | 1.9708 |
  | 3 | 4.16 | b | 0.938745 | 2254.66 | -123.625 |  |
  | 3 | 4.16 | c | 0.917157 | 2202.81 | 114.788 |  |
  | 4 | 4.16 | a | 0.905567 | 2174.97 | -4.123 | 6.3762 |
  | 4 | 4.16 | b | 0.803496 | 1929.82 | -126.798 |  |
  | 4 | 4.16 | c | 0.763044 | 1832.66 | 102.845 |  |
[exit 0]
```

The sweep converged in 39 iterations. Phase c at the load bus sits at 0.763 pu, the lowest voltage in the table, and phase a
at 0.906 pu, a spread of 14.3 percentage points between two phases of the same bus. The positive-
sequence tools of the other chapters would show one number for this bus. The voltage unbalance factor at bus 4 is
6.3762 %.

## Reading the output

The sweep, the estimators and the lift all print the same layout, in this order.

**Banner.** `THREE-PHASE DISTRIBUTION (sweep) - <feeder name>   [EXPERIMENTAL]`. The word in brackets after
`DISTRIBUTION` is the mode that ran. The feeder name is the `name` member of the file, or the case name for a lifted
case.

**File line.** The path as you gave it.

**Size line.** `4 buses, 3 branches, 1 loads, base 6 MVA (three-phase)`. Branches are the lines, transformers, closed
switches and regulators that are in the network. `base` is `baseMva`. An open switch is not a branch.

**Element line.** Printed when the feeder holds a regulator, a closed or open switch, a capacitor, a single- or
two-phase branch, line charging, a transformer element other than `gy-gy`, or a load that is not constant-power wye.
A feeder with a `d-gy` transformer adds `, N delta / grounded-wye transformer(s)` at the end. It counts the regulators, the closed switches, the open switches (marked `(not branches)`), the capacitor banks and the single- or two-phase branches, so
you can see that each was read. The 123-node feeder prints
`4 regulator(s), 5 closed switch(es), 2 open switch(es) (not branches), 4 capacitor bank(s), 60 single- or two-phase branch(es)`.
A feeder of three-phase lines, transformers and loads only prints no such line.

**Convergence line.** `sweep CONVERGED in 39 iteration(s), max voltage change 7.191E-011 pu (tolerance 1.000E-010)`.
One sweep is one backward pass that sums the load currents up the tree and one forward pass that drops voltage down it.
The sweep stops when the largest change of any phase voltage between two successive sweeps is below the tolerance
(`--tol`, default `1e-10` pu), or fails when it reaches `--max-iter` sweeps (default 200). The line reports the sweeps
used and the last change. A feeder that does not converge prints no table (see [Pitfalls](#pitfalls)).

**Voltage table.** One row per bus and phase, in the order the buses appear in the file.

| Column | Meaning |
|---|---|
| `bus` | The bus id, or the bus `name` where the file gives one. |
| `kV LL` | The bus's `baseKvLL`. |
| `phase` | `a`, `b` or `c`. |
| `\|V\| pu` | Voltage magnitude, per-unit of the bus's line-to-neutral base `baseKvLL` / sqrt(3), to 6 decimals. |
| `\|V\| V (LN)` | The same magnitude in line-to-neutral volts, to 2 decimals: `\|V\| pu` times `baseKvLL` x 1000 / sqrt(3). |
| `angle deg` | The phase angle in degrees, to 3 decimals. The balanced source puts phase a at 0. |
| `VUF %` | The voltage unbalance factor of the bus, on the first row of a three-phase bus only (see below). |

A phase that a bus does not have, because the bus is on a lateral, prints `-` in the three value columns and has no row
in a result file. The `VUF %` cell is empty on the second and third rows of a bus and on every row of a bus with fewer
than three phases, because the factor needs all three. In the 123-node feeder node 2 carries phase b only, so its a
and c rows read `-`:

```
$ gridsim dist3ph data/dist3ph/ieee123.json
THREE-PHASE DISTRIBUTION (sweep) - IEEE 123 Node Test Feeder   [EXPERIMENTAL]
  file: data/dist3ph/ieee123.json
  128 buses, 127 branches, 85 loads, base 5 MVA (three-phase)
  4 regulator(s), 5 closed switch(es), 2 open switch(es) (not branches), 4 capacitor bank(s), 60 single- or two-phase branch(es)
  sweep CONVERGED in 10 iteration(s), max voltage change 1.167E-011 pu (tolerance 1.000E-010)
  | bus | kV LL | phase | |V| pu | |V| V (LN) | angle deg | VUF % |
  |-----|-------|-------|--------|------------|-----------|-------|
  | 1 | 4.16 | a | 1.031303 | 2476.96 | -0.636 | 0.2534 |
  | 1 | 4.16 | b | 1.041294 | 2500.96 | -120.310 |  |
  | 1 | 4.16 | c | 1.034887 | 2485.57 | 119.622 |  |
  | 2 | 4.16 | a | - | - | - | |
  | 2 | 4.16 | b | 1.041073 | 2500.42 | -120.314 |  |
  | 2 | 4.16 | c | - | - | - | |
...
  | RG1 | 4.16 | a | 1.043750 | 2506.85 | 0.000 | 0.0000 |
  | RG1 | 4.16 | b | 1.043750 | 2506.85 | -120.000 |  |
  | RG1 | 4.16 | c | 1.043750 | 2506.85 | 120.000 |  |
...
[exit 0]
```

The rows `RG1` show the output node of regulator RG1, named in the file. Its three phases sit at 1.043750 pu, which is
`1 + 0.00625 x 7`: the published tap 7 on every phase at 0.625 % per step. Across the 123-node solution the lowest
voltage is 0.985855 pu (bus 65, phase a), the highest 1.043870 pu (bus 83, phase a) and the highest unbalance
factor 1.0781 % (bus 95).

### Unbalance metrics

The verb reports unbalance through symmetrical components, the Fortescue transform with `a` = 1 at 120 degrees:

```
V0 = (Va + Vb + Vc) / 3
V1 = (Va + a Vb + a^2 Vc) / 3
V2 = (Va + a^2 Vb + a Vc) / 3
```

The voltage unbalance factor is `VUF % = 100 x |V2| / |V1|`, the ratio of the negative-sequence to the positive-sequence
voltage. It is 0 for a perfectly balanced bus, and undefined for a dead bus with no positive-sequence voltage (printed and written as `NaN`).
A sweep in which a constant-power or constant-current load sits on a phase at exactly zero voltage (a collapse) stops
not converged with a non-finite status rather than letting that load draw nothing. It is printed in the sweep and estimate tables once per three-phase bus
and stored unrounded in the result files (column `vuf_pct`). The two other unbalance views are in the other studies:
`harmonics` prints `|V0|`, `|V1|` and `|V2|` at each harmonic order, and `fault` prints the Thevenin impedances `Z0`,
`Z1` and `Z2` of the sequence networks.

Unbalance has three sources: the line, the load and the source. The bundled feeders show the first two. The balanced-load 4-node feeder, which draws 1800 kW on every
phase, is not balanced at its load bus:

```
$ gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-balanced.json sweep
THREE-PHASE DISTRIBUTION (sweep) - IEEE 4-node test feeder, grounded-wye / grounded-wye step-down, balanced load   [EXPERIMENTAL]
  file: data/dist3ph/ieee4-gyGy-stepdown-balanced.json
  4 buses, 3 branches, 1 loads, base 6 MVA (three-phase)
  sweep CONVERGED in 25 iteration(s), max voltage change 5.279E-011 pu (tolerance 1.000E-010)
  | bus | kV LL | phase | |V| pu | |V| V (LN) | angle deg | VUF % |
  |-----|-------|-------|--------|------------|-----------|-------|
  | 1 | 12.47 | a | 1.000000 | 7199.56 | 0.000 | 0.0000 |
  | 1 | 12.47 | b | 1.000000 | 7199.56 | -120.000 |  |
  | 1 | 12.47 | c | 1.000000 | 7199.56 | 120.000 |  |
  | 2 | 12.47 | a | 0.987081 | 7106.55 | -0.339 | 0.1523 |
  | 2 | 12.47 | b | 0.991687 | 7139.71 | -120.344 |  |
  | 2 | 12.47 | c | 0.989056 | 7120.77 | 119.629 |  |
  | 3 | 4.16 | a | 0.935730 | 2247.41 | -3.694 | 0.2928 |
  | 3 | 4.16 | b | 0.944511 | 2268.51 | -123.476 |  |
  | 3 | 4.16 | c | 0.939245 | 2255.86 | 116.395 |  |
  | 4 | 4.16 | a | 0.798479 | 1917.77 | -9.073 | 2.3654 |
  | 4 | 4.16 | b | 0.858224 | 2061.26 | -128.315 |  |
  | 4 | 4.16 | c | 0.824709 | 1980.77 | 110.856 |  |
[exit 0]
```

Its VUF at bus 4 is 2.3654 %, produced entirely by the untransposed line, whose self and mutual impedances differ
between phases. The unbalanced-load run raises it to 6.3762 % at the same bus. (An unbalanced source, set in the `source` block,
unbalances every bus.) The balanced-load figures also show why a feeder model needs three phases: the load is
identical on every phase, yet phase a sits at 0.798 pu and phase b at 0.858 pu.

## Variations

### Fault currents

`fault` computes the fault currents at one bus from the sequence networks. Name the bus with `--bus`:

```bash
gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json fault --bus 4
```

```
$ gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json fault --bus 4
THREE-PHASE DISTRIBUTION (fault) - IEEE 4-node test feeder, grounded-wye / grounded-wye step-down, unbalanced load   [EXPERIMENTAL]
  file: data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json
  4 buses, 3 branches, 1 loads, base 6 MVA (three-phase)
  fault at bus 4 (4.16 kV LL), Zf = 0 pu (resistive), prefault E1 = 1.0 pu, infinite source;
  each branch reduced to its transposed-equivalent sequence impedances (z1 = z2 = zs - zm, z0 = zs + 2 zm).
  Thevenin Z0 = 0.148286 + j0.406336 pu   Z1 = 0.064718 + j0.172093 pu   Z2 = 0.064718 + j0.172093 pu
  current base at this bus 832.717 A
  | fault | current pu | current A |
  |-------|------------|-----------|
  | 3ph | 5.438923 | 4529.08 |
  | slg | 3.748789 | 3121.68 |
  | ll | 4.710245 | 3922.30 |
  | llg (ground) | 2.860006 | 2381.57 |
[exit 0]
```

Reading the fault block, line by line:

- `fault at bus 4 (4.16 kV LL), Zf = 0 pu, prefault E1 = 1.0 pu, infinite source;` names the bus, the fault resistance
  `--zf` in per-unit (a real number, at least 0; a reactive fault impedance cannot be given) (default 0, a bolted fault) and the fixed assumptions: a prefault positive-sequence voltage of 1.0
  pu, behind an infinite source of zero impedance.
- The next line states how each branch is reduced. A 3 by 3 phase impedance becomes its transposed-equivalent
  sequence impedances: `z1 = z2 = zs - zm` and `z0 = zs + 2 zm`, where `zs` is the mean of the three self impedances
  and `zm` the mean of the six mutual impedances. The Thevenin impedance at the fault bus is the sum of these along the
  path from the source.
- `Thevenin Z0`, `Z1` and `Z2` are the zero-, positive- and negative-sequence impedances at the bus, in per-unit, each
  as a real part and an imaginary part. `Z1` equals `Z2` by construction. `Z0` is the larger here because the mutual
  impedance between conductors adds in the zero sequence and subtracts in the other two; a lifted balanced case has no
  mutuals, so its `Z0`, `Z1` and `Z2` are equal. The grounded-wye transformer
  passes all three sequences, so it adds its leakage impedance to each.
- `current base at this bus 832.717 A` is `baseMva x 10^6 / (sqrt(3) x baseKvLL x 1000)` at the faulted bus's voltage
  level. The amps column is the per-unit column times this number.
- The table gives the magnitude of the fault current. `3ph` is `E1 / (Z1 + Zf)`. `slg`, single line to ground, is
  `3 E1 / (Z0 + Z1 + Z2 + 3 Zf)`. `ll`, line to line, is `sqrt(3) E1 / (Z1 + Z2 + Zf)`. `llg (ground)`, double line to
  ground, is the ground current `3 I0` with `Zf` as the impedance to ground. At bus 4, `3ph` is 5.438923 pu, which is
  `1 / |Z1|` for the printed `Z1`.

`--type` limits the table to one row (`3ph`, `slg`, `ll` or `llg`; the default is `all`). A fault resistance adds to
the loop impedance and lowers the current:

```bash
gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json fault --bus 3 --type slg --zf 0.05
```

```
$ gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json fault --bus 3 --type slg --zf 0.05
THREE-PHASE DISTRIBUTION (fault) - IEEE 4-node test feeder, grounded-wye / grounded-wye step-down, unbalanced load   [EXPERIMENTAL]
  file: data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json
  4 buses, 3 branches, 1 loads, base 6 MVA (three-phase)
  fault at bus 3 (4.16 kV LL), Zf = 0.05 pu (resistive), prefault E1 = 1.0 pu, infinite source;
  each branch reduced to its transposed-equivalent sequence impedances (z1 = z2 = zs - zm, z0 = zs + 2 zm).
  Thevenin Z0 = 0.021305 + j0.088314 pu   Z1 = 0.014473 + j0.069164 pu   Z2 = 0.014473 + j0.069164 pu
  current base at this bus 832.717 A
  | fault | current pu | current A |
  |-------|------------|-----------|
  | slg | 9.919461 | 8260.10 |
[exit 0]
```

The study neglects every shunt element: loads of every kind, shunt capacitors and line charging. The fault current is
that of the series path alone. A closed switch is a zero impedance. A single-phase or two-phase branch, or a regulator,
on the path from the source to the faulted bus is refused by name (exit 1); the same elements elsewhere in the feeder do
not enter the study. A `--zf` below 0 exits 2. A fault at the source bus exits 1, because the infinite bus has zero
impedance and the current has no finite value:

```
$ gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json fault --bus 1
THREE-PHASE DISTRIBUTION (fault) - IEEE 4-node test feeder, grounded-wye / grounded-wye step-down, unbalanced load   [EXPERIMENTAL]
  file: data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json
  4 buses, 3 branches, 1 loads, base 6 MVA (three-phase)
error: bus 1 is the source (an infinite bus with zero impedance); a fault there has no finite current in this model.
[exit 1]
```

### Estimating the state from measurements

`estimate` finds the three-phase voltages that best fit a measurement file. The default method is `wls`, a weighted
least-squares fit of the node voltages. The bundled set measures every phase voltage magnitude at buses 2 to 4 and
every phase current of the three branches:

```bash
gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json estimate --measurements data/dist3ph/ieee4-gyGy-stepdown-unbalanced.measurements.json
```

```
$ gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json estimate --measurements data/dist3ph/ieee4-gyGy-stepdown-unbalanced.measurements.json
THREE-PHASE DISTRIBUTION (estimate) - IEEE 4-node test feeder, grounded-wye / grounded-wye step-down, unbalanced load   [EXPERIMENTAL]
  file: data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json
  4 buses, 3 branches, 1 loads, base 6 MVA (three-phase)
  measurements: 9 voltage magnitude(s), 9 current phasor(s), 0 nominal-voltage pseudo-measurement(s); method wls
  estimate CONVERGED in 4 iteration(s), objective J = 7.057361E-007
  | bus | kV LL | phase | |V| pu | |V| V (LN) | angle deg | VUF % |
  |-----|-------|-------|--------|------------|-----------|-------|
  | 1 | 12.47 | a | 1.000000 | 7199.56 | 0.000 | 0.0000 |
  | 1 | 12.47 | b | 1.000000 | 7199.56 | -120.000 |  |
  | 1 | 12.47 | c | 1.000000 | 7199.56 | 120.000 |  |
  | 2 | 12.47 | a | 0.995022 | 7163.72 | -0.140 | 0.3049 |
  | 2 | 12.47 | b | 0.987628 | 7110.48 | -120.185 |  |
  | 2 | 12.47 | c | 0.983675 | 7082.03 | 119.265 |  |
  | 3 | 4.16 | a | 0.959910 | 2305.49 | -2.258 | 1.9708 |
  | 3 | 4.16 | b | 0.938745 | 2254.66 | -123.625 |  |
  | 3 | 4.16 | c | 0.917157 | 2202.81 | 114.788 |  |
  | 4 | 4.16 | a | 0.905567 | 2174.97 | -4.123 | 6.3762 |
  | 4 | 4.16 | b | 0.803496 | 1929.82 | -126.798 |  |
  | 4 | 4.16 | c | 0.763044 | 1832.66 | 102.845 |  |
  | measurement | phase | measured pu | estimate pu | r/sigma |
  |-------------|-------|-------------|-------------|---------|
  | |V| bus 2 | a | 0.995022 | 0.995022 | 0.000 |
  | |V| bus 2 | b | 0.987628 | 0.987628 | 0.000 |
  | |V| bus 2 | c | 0.983675 | 0.983675 | 0.000 |
  | |V| bus 3 | a | 0.959910 | 0.959910 | 0.000 |
  | |V| bus 3 | b | 0.938745 | 0.938745 | 0.000 |
  | |V| bus 3 | c | 0.917157 | 0.917157 | 0.000 |
  | |V| bus 4 | a | 0.905567 | 0.905567 | 0.000 |
  | |V| bus 4 | b | 0.803496 | 0.803496 | 0.000 |
  | |V| bus 4 | c | 0.763044 | 0.763044 | 0.000 |
  | I 1->2 | a | 0.670785-j0.485777 | 0.670785-j0.485777 | 0.000 |
  | I 1->2 | b | -1.105339-j0.571979 | -1.105339-j0.571979 | 0.000 |
  | I 1->2 | c | 0.152737+j1.631040 | 0.152737+j1.631040 | 0.000 |
  | I 2->3 | a | 0.670785-j0.485777 | 0.670785-j0.485777 | 0.000 |
  | I 2->3 | b | -1.105339-j0.571979 | -1.105339-j0.571979 | 0.000 |
  | I 2->3 | c | 0.152737+j1.631040 | 0.152737+j1.631040 | 0.000 |
  | I 3->4 | a | 0.670785-j0.485777 | 0.670785-j0.485777 | 0.000 |
  | I 3->4 | b | -1.105339-j0.571979 | -1.105339-j0.571979 | 0.000 |
  | I 3->4 | c | 0.152737+j1.631040 | 0.152737+j1.631040 | 0.000 |
[exit 0]
```

The `measurements:` line counts what was read: voltage magnitudes, current phasors, and the nominal-voltage
pseudo-measurements added for unmetered buses. After the voltage table a converged `wls` or `wlav` estimate prints one
residual row per measurement in the fit, `| measurement | phase | measured pu | estimate pu | r/sigma |`: the voltage
magnitudes first (`|V| bus <id>`), then the pseudo-measurements, then the currents (`I <from>-><to>`, written
`re+jim`), with `r/sigma` the absolute residual over its sigma. A magnitude at the source bus is not in the fit and is
not listed. The result file holds the same rows as a `residuals` table. `estimate CONVERGED in 4 iteration(s), objective J = 7.057361E-007` gives
the Gauss-Newton iterations and the objective. `J` is the sum over every measurement of the squared residual divided by
the squared `sigmaPu`, where a current residual adds its real and imaginary parts. This set gives 7.06e-7; its values are rounded to 6 decimals against a `sigmaPu` of 0.001. The voltage table is the estimated
state in the layout of the sweep, with the same `VUF %`. It equals the sweep solution of the same feeder to the printed
digits, because the measurements were made from that solution.

The state is the complex voltage of every non-source bus and phase; the source is the fixed reference. Voltage
magnitudes carry no angle, so the angles come from the current phasors, which are linear in the voltages through the
inverse of each branch's 3 by 3 impedance. A set of magnitudes without enough currents is not observable, and the
estimator says so:

```
$ gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json estimate --measurements docs/_captures/dist3ph/ieee4-vmag-only.measurements.json
THREE-PHASE DISTRIBUTION (estimate) - IEEE 4-node test feeder, grounded-wye / grounded-wye step-down, unbalanced load   [EXPERIMENTAL]
  file: data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json
  4 buses, 3 branches, 1 loads, base 6 MVA (three-phase)
  measurements: 9 voltage magnitude(s), 0 current phasor(s), 0 nominal-voltage pseudo-measurement(s); method wls
dist3ph: the three-phase estimator did not converge after 0 iteration(s) (final max state update Infinity); the measurement set may be unobservable. No estimate reported.
[exit 3]
```

The message goes to stderr, no table is printed and the exit code is 3.

**Pseudo-measurements.** A `pseudoVoltage` member adds a prior for every non-source bus that has no voltage magnitude.
This run uses the nine currents, the bus 4 magnitudes and a prior of 1.0 pu with sigma 0.05 pu for buses 2 and 3:

```
$ gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json estimate --measurements docs/_captures/dist3ph/ieee4-pseudo.measurements.json
THREE-PHASE DISTRIBUTION (estimate) - IEEE 4-node test feeder, grounded-wye / grounded-wye step-down, unbalanced load   [EXPERIMENTAL]
  file: data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json
  4 buses, 3 branches, 1 loads, base 6 MVA (three-phase)
  measurements: 3 voltage magnitude(s), 9 current phasor(s), 6 nominal-voltage pseudo-measurement(s); method wls
  estimate CONVERGED in 4 iteration(s), objective J = 5.066665E+000
...
  | 4 | 4.16 | c | 0.763044 | 1832.66 | 102.845 |  |
...
[exit 0]
```

The state is unchanged to the printed digits except at bus 4 phase a, 0.905568 against 0.905567, because the currents
pin it. `J` rises to 5.07 because the true bus 2 and
bus 3 voltages (0.917 to 0.995 pu) sit away from the 1.0 pu prior, and the prior's residual is counted. A
pseudo-measurement is a loose prior, not an observation of the feeder.

**Robust estimation.** `--method wlav` wraps the fit in a reweighting loop. A measurement with a large standardised
residual gets a smaller weight (the reciprocal of the standardised residual, residual / sigma, with a floor of 0.001), for up to 8 outer passes or
until no weight moves by more than 0.001. It is
the method to use when a gross error is possible. The next file is the bundled set with one bad value: bus 4 phase b
reads 0.9 pu where the truth is 0.803496 pu.

```
$ gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json estimate --measurements docs/_captures/dist3ph/ieee4-gross-error.measurements.json
...
  estimate CONVERGED in 5 iteration(s), objective J = 8.855827E+003
...
  | 4 | 4.16 | a | 0.903777 | 2170.67 | -4.306 | 6.4139 |
  | 4 | 4.16 | b | 0.808234 | 1941.20 | -126.797 |  |
  | 4 | 4.16 | c | 0.761118 | 1828.04 | 103.022 |  |
...
  | |V| bus 4 | b | 0.900000 | 0.808234 | 91.766 |
...
[exit 0]
```

```
$ gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json estimate --measurements docs/_captures/dist3ph/ieee4-gross-error.measurements.json --method wlav
...
  estimate CONVERGED in 23 iteration(s), objective J = 9.313081E+003
...
  | 4 | 4.16 | a | 0.905567 | 2174.97 | -4.123 | 6.3762 |
  | 4 | 4.16 | b | 0.803496 | 1929.82 | -126.798 |  |
  | 4 | 4.16 | c | 0.763044 | 1832.66 | 102.845 |  |
...
  | |V| bus 4 | b | 0.900000 | 0.803496 | 96.504 |
...
[exit 0]
```

The residual rows locate the bad value: its `r/sigma`, 91.766 in the least-squares fit and 96.504 in the robust
one, stands out from the rest. The least-squares fit follows the bad value: phase b at bus 4 reads 0.808234 pu and the error spreads into phases a
and c. The robust fit recovers the true state to the printed digits. Its `J` is the higher, 9313.081 against 8855.827,
because `J` is always evaluated with the original weights at the final state, and the robust state leaves the bad
measurement's residual in full: ((0.9 - 0.803496) / 0.001) squared is 9313. `J` after `wlav` is always at least the `wls` value, because `wls` minimises it; compare the two states instead, and a
large shift with a rise in `J` points at a gross error. The iteration count of the robust run, 23, is the total over all outer passes.
`--robust-passes N` (default 8, at least 1) and `--robust-floor X` (default 0.001, above 0) set the pass limit and the
floor, with `wlav` only. When the last pass still moves a weight by 0.001 or more, the run prints `WARNING: the robust
re-weighting (IRLS) reached its cap of N pass(es) before the weights settled; ...`; the exit code stays 0 and the result
file carries `irls_cap_reached`.

**Branch-current estimation.** `--method branch-current` is a different estimator. It combines the current phasors of
each branch and phase by weighted least squares, then makes one forward sweep from the source to give every voltage.
It uses currents only and needs a current phasor on every phase of every tree branch. A branch phase with no phasor
leaves the estimate not converged (exit 3), and the message lists the unobservable branch phases (`1-2 b`). A current
named against the reversed endpoints is accepted as minus the `from`-end current, except across a regulator or a
charged line (exit 1, naming the branch). Give it a measurement file with no voltage magnitudes and no
`pseudoVoltage`:

```
$ gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json estimate --measurements docs/_captures/dist3ph/ieee4-currents-only.measurements.json --method branch-current
THREE-PHASE DISTRIBUTION (estimate) - IEEE 4-node test feeder, grounded-wye / grounded-wye step-down, unbalanced load   [EXPERIMENTAL]
  file: data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json
  4 buses, 3 branches, 1 loads, base 6 MVA (three-phase)
  measurements: 9 current phasor(s); method branch-current
  estimate CONVERGED (one forward sweep over 3 measured branch(es))
...
  | 4 | 4.16 | a | 0.905568 | 2174.97 | -4.123 | 6.3762 |
  | 4 | 4.16 | b | 0.803496 | 1929.82 | -126.798 |  |
  | 4 | 4.16 | c | 0.763044 | 1832.66 | 102.845 |  |
[exit 0]
```

There is no iteration count and no `J`, because nothing iterates. Bus 4 phase a reads 0.905568 pu here and 0.905567 pu
in the least-squares run, a difference in the last printed digit. A file that also holds voltage magnitudes is refused
rather than having them ignored (exit 1), and `--tol`, `--max-iter`, `--robust-passes` and `--robust-floor` are refused
with this method (exit 2):

```
$ gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json estimate --measurements data/dist3ph/ieee4-gyGy-stepdown-unbalanced.measurements.json --method branch-current
THREE-PHASE DISTRIBUTION (estimate) - IEEE 4-node test feeder, grounded-wye / grounded-wye step-down, unbalanced load   [EXPERIMENTAL]
  file: data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json
  4 buses, 3 branches, 1 loads, base 6 MVA (three-phase)
error: dist3ph estimate --method branch-current uses current phasors only; data/dist3ph/ieee4-gyGy-stepdown-unbalanced.measurements.json also has 9 voltage magnitude(s), which it would ignore.
[exit 1]
```

The estimators differ in which feeder elements they accept, and a refused element is named, never dropped. The
least-squares methods accept loads and capacitors, which do not enter a measurement model that has no injection
measurements, and refuse laterals, line charging, regulators and closed switches. Pseudo-measurements go to buses with no voltage
measurement on any phase, so a bus metered on one phase gets no prior on the others. The branch-current method models
every element: a regulator and line charging are undone from the measured current at the From end, a lateral drops
voltage on its own phases only and a closed switch has no drop. A regulator or a charged line must face away from the
source, and a current on a phase that its branch does not carry is refused. The least-squares estimator is dense (a
6n by 6n gain inverted every iteration), so a feeder with more than 416 non-source buses (2,500 states) is refused with
exit 1.

### Harmonic voltages

`harmonics` places one balanced harmonic current source at a bus and reports the harmonic voltages it creates. It
needs three flags: `--bus`, `--spectrum` and `--current`. The spectrum is a profile of the harmonic source library, the
same names the `harmonics` verb of [ch. 17](<0017 Harmonic studies.md>) uses: `6-pulse`, `12-pulse`,
`pv-inverter`, `ev-charger`, `heat-pump-inverter`, `arc-furnace`, `saturated-transformer`, `6-pulse-g5`, `12-pulse-g5`,
`afe-g5` and `ideal`. There is no default. `--current` is the fundamental current of the source in per-unit on the
feeder base, above 0.

```bash
gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json harmonics --bus 4 --spectrum 6-pulse-g5 --current 0.2
```

```
$ gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json harmonics --bus 4 --spectrum 6-pulse-g5 --current 0.2
THREE-PHASE DISTRIBUTION (harmonics) - IEEE 4-node test feeder, grounded-wye / grounded-wye step-down, unbalanced load   [EXPERIMENTAL]
  file: data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json
  4 buses, 3 branches, 1 loads, base 6 MVA (three-phase)
  balanced harmonic source at bus 4: spectrum 6-pulse-g5 (library profile, assumed-typical unless a G5-cited one [VERIFY]), fundamental current 0.2 pu;
  network only (series impedances, R + j h X; loads not modelled), source bus as the harmonic reference.
  | order | |Va| pu | |Vb| pu | |Vc| pu | |V0| pu | |V1| pu | |V2| pu | worst bus | worst |V| pu |
  |-------|--------|--------|--------|--------|--------|--------|-----------|-------------|
  | 5 | 0.054520 | 0.052327 | 0.056204 | 0.001835 | 0.004071 | 0.054190 | 4 | 0.056204 |
  | 7 | 0.026788 | 0.025126 | 0.027202 | 0.000855 | 0.026299 | 0.001979 | 4 | 0.027202 |
  | 11 | 0.026787 | 0.025516 | 0.027483 | 0.000888 | 0.001997 | 0.026518 | 4 | 0.027483 |
  | 13 | 0.017741 | 0.016711 | 0.018068 | 0.000573 | 0.017458 | 0.001315 | 4 | 0.018068 |
  | 17 | 0.015981 | 0.015189 | 0.016373 | 0.000527 | 0.001190 | 0.015802 | 4 | 0.016373 |
  | 19 | 0.012619 | 0.011905 | 0.012865 | 0.000409 | 0.012428 | 0.000936 | 4 | 0.012865 |
  | 23 | 0.008012 | 0.007607 | 0.008203 | 0.000264 | 0.000596 | 0.007917 | 4 | 0.008203 |
  | 25 | 0.008734 | 0.008247 | 0.008910 | 0.000284 | 0.008606 | 0.000648 | 4 | 0.008910 |
  | 29 | 0.006063 | 0.005753 | 0.006205 | 0.000199 | 0.000451 | 0.005989 | 4 | 0.006205 |
  | 31 | 0.007579 | 0.007160 | 0.007734 | 0.000247 | 0.007469 | 0.000563 | 4 | 0.007734 |
  | 35 | 0.007318 | 0.006941 | 0.007488 | 0.000240 | 0.000545 | 0.007228 | 4 | 0.007488 |
  | 37 | 0.006460 | 0.006105 | 0.006593 | 0.000210 | 0.006368 | 0.000480 | 4 | 0.006593 |
  | 41 | 0.005716 | 0.005420 | 0.005847 | 0.000188 | 0.000425 | 0.005645 | 4 | 0.005847 |
  | 43 | 0.006005 | 0.005676 | 0.006130 | 0.000196 | 0.005920 | 0.000446 | 4 | 0.006130 |
  | 47 | 0.004915 | 0.004660 | 0.005027 | 0.000161 | 0.000366 | 0.004853 | 4 | 0.005027 |
  | 49 | 0.003421 | 0.003234 | 0.003493 | 0.000112 | 0.003373 | 0.000254 | 4 | 0.003493 |
  RSS harmonic voltage at bus 4: a 0.074643 pu, b 0.071145 pu, c 0.076603 pu
[exit 0]
```

The description line names the source bus, the spectrum and the fundamental current. The library profiles are typical
shapes, not measurements of a particular converter, and the `[VERIFY]` in the line marks that. The `6-pulse-g5`,
`12-pulse-g5` and `afe-g5` profiles carry a citation for their shape, ENA Engineering Recommendation G5 Issue 5,
Annex B. The second line states the network: the feeder's series impedances with the reactance scaled by the order,
`R + j h X`, the source bus of the feeder as the harmonic reference (zero harmonic voltage), and no loads. A feeder
with shunt capacitors, line charging or closed switches says so on this line and includes them: capacitors and line
charging as susceptances scaled by the order, closed switches as one node.

One table row is printed for each order in the spectrum. The columns are:

| Column | Meaning |
|---|---|
| `order` | The harmonic order `h`. |
| `\|Va\| pu`, `\|Vb\| pu`, `\|Vc\| pu` | Harmonic voltage magnitude at the injection bus on each phase, per-unit on the line-to-neutral base. |
| `\|V0\| pu`, `\|V1\| pu`, `\|V2\| pu` | The zero-, positive- and negative-sequence magnitudes of those three phase voltages. |
| `worst bus`, `worst \|V\| pu` | The bus and magnitude of the largest harmonic voltage of this order over every bus and phase of the feeder. |

The source is rotated by `h` x 120 degrees on phases b and c, so the sequence of each order follows from the order.
Orders 5, 11, 17, 23 and 29 (6k - 1) come out negative-sequence: `|V2|` is large and `|V1|` small, as the table shows.
Orders 7, 13, 19, 25 and 31 (6k + 1) come out positive-sequence. A triplen order (3, 9, 15) is in phase on all three
phases and comes out zero-sequence, which a positive-sequence study cannot represent. The `pv-inverter` profile has an
order 3 component:

```
$ gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json harmonics --bus 4 --spectrum pv-inverter --current 0.2
...
  | order | |Va| pu | |Vb| pu | |Vc| pu | |V0| pu | |V1| pu | |V2| pu | worst bus | worst |V| pu |
  |-------|--------|--------|--------|--------|--------|--------|-----------|-------------|
  | 3 | 0.004970 | 0.004990 | 0.004776 | 0.004912 | 0.000071 | 0.000066 | 4 | 0.004990 |
  | 5 | 0.006945 | 0.006666 | 0.007160 | 0.000234 | 0.000519 | 0.006903 | 4 | 0.007160 |
...
```

At order 3 the voltage is almost entirely `|V0|`. The small entries in the sequences that a pure rotation would leave at
zero come from the line matrix, which is not perfectly symmetric. The last line, `RSS harmonic voltage at bus 4: a
0.074643 pu, b 0.071145 pu, c 0.076603 pu`, is the root-sum-square over every order in the table of the harmonic voltage
at the injection bus, per phase. It does not include the fundamental.

**Converter plant.** `--afe feeder` stamps the feeder file's `converters` units into the harmonic admittance: each
phase gets R + j h X between the phase and the converter's floating star point (positive and negative sequence, no
zero sequence), and a line names the units. No default impedance exists for a converter unit, so every value comes from
the file and is a `[VERIFY]` figure until sourced. A feeder with no `converters` section exits 1; any other value of
the flag exits 2. Without the flag the units are not stamped.

`harmonics` refuses a bus that is not in the feeder, the source bus, a bus tied to the source through closed switches
and a singular network (exit 1), and refuses a
feeder with laterals or regulators by name (exit 1). It exits 2 for a missing flag, an unknown spectrum or a
`--current` that is not above 0. The 123-node feeder holds both laterals and regulators:

```
$ gridsim dist3ph data/dist3ph/ieee123.json harmonics --bus 100 --spectrum 6-pulse --current 0.01
THREE-PHASE DISTRIBUTION (harmonics) - IEEE 123 Node Test Feeder   [EXPERIMENTAL]
  file: data/dist3ph/ieee123.json
  128 buses, 127 branches, 85 loads, base 5 MVA (three-phase)
  4 regulator(s), 5 closed switch(es), 2 open switch(es) (not branches), 4 capacitor bank(s), 60 single- or two-phase branch(es)
dist3ph harmonics: the three-phase harmonic penetration cannot model this feeder's single- or two-phase branches and step-voltage regulators, and will not solve it with them left out. It supports three-phase series-impedance lines and accepts constant-power wye loads, plus line charging, shunt capacitors, constant-current or constant-impedance (ZIP) loads, delta loads and closed switches (zero-impedance branches) (capacitors and line charging as susceptances scaled by the order, closed switches as one node; loads of every kind, constant-power wye included, are accepted and left out of Y(h): the study is network only). Use the three-phase sweep for this feeder.
[exit 1]
```

### Lifting a case

`dist3ph` accepts a radial positive-sequence case in place of a feeder file: a MATPOWER `.m` file or a JSON case
folder (one that holds `system.json`). The case is lifted into a balanced three-phase feeder. Each branch's resistance,
reactance and charging go on every phase, each bus load is split equally per phase, a ZIP load becomes its
constant-impedance, constant-current and constant-power parts, shunt capacitors are kept and the slack bus is the
source. A generator at a PQ bus becomes a constant-power negative load, a generator at the slack is dropped, and
out-of-service branches are left out. A case the sweep cannot model is refused with exit 1: a voltage-controlled (PV) bus, an off-nominal tap or
phase shift, a shunt conductance or reactor, a bus without a kV base, or a network that is not one radial tree.

```bash
gridsim dist3ph data/matpower/dist/case33bw.m
```

```
$ gridsim dist3ph data/matpower/dist/case33bw.m
THREE-PHASE DISTRIBUTION (sweep) - case33bw   [EXPERIMENTAL]
  file: data/matpower/dist/case33bw.m
  33 buses, 32 branches, 32 loads, base 10 MVA (three-phase)
  sweep CONVERGED in 9 iteration(s), max voltage change 9.431E-011 pu (tolerance 1.000E-010)
  | bus | kV LL | phase | |V| pu | |V| V (LN) | angle deg | VUF % |
  |-----|-------|-------|--------|------------|-----------|-------|
  | 1 | 12.66 | a | 1.000000 | 7309.25 | 0.000 | 0.0000 |
...
  | 33 | 12.66 | a | 0.916590 | 6699.59 | 0.380 | 0.0000 |
  | 33 | 12.66 | b | 0.916590 | 6699.59 | -119.620 |  |
  | 33 | 12.66 | c | 0.916590 | 6699.59 | 120.380 |  |
note: case33bw.m: nominal frequency not given (MATPOWER files carry none); assumed 50 Hz - pass the real value if this is, for example, a 60 Hz system (gridsim: --frequency-hz 60).
note: case33bw.m: the file has 2 indexed assignment(s) after the matrices (mpc.branch, mpc.bus); MATLAB statements are not run, so they are not applied (a raw-unit distribution feeder is converted by the unit normaliser instead).
note: case33bw.m: read mpc.gencost (1 row(s)); the costs are carried on the generators and used by dcopf / acopf when asked (--costs case); without it the OPF uses the fuel-type merit order.
note: case33bw.m: skipped 5 out-of-service branch(es).
note: case33bw.m: raw engineering units detected (branch impedance in ohms, demand in kW / kVAr, as in the published distribution feeders); converted to per-unit and MW: R and X divided by Zbase = baseKV^2 / baseMVA of the from bus, Pd, Qd, Gs and Bs divided by 1000.
[exit 0]
```

The `note:` lines come from the case loader (see [ch. 2](<0002 Loading networks.md>)). A lifted case is balanced,
so its three phases carry equal magnitudes and the unbalance factor is 0.0000 at every bus. The lifted sweep agrees with
the Newton power flow of `gridsim data/matpower/dist/case33bw.m --once`, which reads 0.917 p.u. at bus 33 and 0.38
degrees. The sweep, `fault`, `estimate` and `harmonics` all accept a lifted case, with the element limits above.

### The JSON v2 form

`to-v2 --to <out.json>` writes the feeder as a `gridsim-dist3ph/2` document (`schemas/v2/dist3ph.schema.json`), the
per-unit, solver-authoritative form with provenance, then reads the written file back and compares it with the feeder
bit for bit:

```bash
gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json to-v2 --to results/ieee4-unbalanced.v2.json
```

It prints `wrote <file> (gridsim-dist3ph/2); read back: the same feeder, bit for bit` and the provenance tag. A feeder
read from a v1 file, or lifted from a case, carries no provenance, so it is written as `CuratedPlaceholder` with a note
naming the source and `[VERIFY]`; set the tag before relying on the file. The v2 form holds the converter units too.
`to-v2` never overwrites a file (exit 2 when `--to` exists or is missing), exits 1 when the feeder holds something the v2
form cannot, and exits 3 when the written file does not read back to the same feeder. Every mode reads a v2 feeder as
it reads a v1 one, and `gridsim validate <file>` checks one against the schema and the reader.

### Result files

`--json <file>`, `--csv <file>` and `--out <file>` write the study's figures at full precision in addition to the
console report ([ch. 19, Result files](<0019 CLI reference.md#result-files>)). A sweep or an estimate writes a `voltages`
table with the columns `bus`, `kv_ll`, `phase`, `vm_pu`, `v_ln_volts`, `angle_deg` and `vuf_pct`, plus a summary with
the convergence figures. A fault study writes the Thevenin impedances, the current base and a `faults` table. A
harmonics study writes a `harmonic_voltages` table (`order`, `bus`, `phase`, `vm_pu`, `angle_deg`) of every non-source bus, phase and order. The JSON document records the full
command line, including the output path you gave.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | The study ran and, for `sweep` and `estimate`, converged. |
| 1 | An input error: the feeder or measurement file is unreadable or invalid, a bus is not in the feeder, a study refuses the input (the source bus, a refused element, a singular network), or a case cannot be lifted. |
| 2 | A usage error: no feeder, a mode word that is not `sweep`, `fault`, `estimate`, `harmonics` or `to-v2`, a flag that belongs to another mode, a missing required flag, or a flag value out of range. A feeder of your own over the licence's bus limit also exits 2. |
| 3 | The sweep, the estimator or the branch-current estimate did not converge, or a `to-v2` file did not read back to the same feeder. Nothing is reported as a result. |

A flag from another mode is refused, never ignored, so a sweep cannot pass for a fault study:

```
$ gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json sweep --bus 4
error: dist3ph sweep does not take --bus (allowed: --tol, --max-iter).
[exit 2]
```

An unknown mode word prints the usage:

```
$ gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json transient
error: dist3ph mode 'transient' is not one of sweep, fault, estimate, harmonics, to-v2.
usage: gridsim dist3ph <feeder.json> [sweep] [--tol 1e-10] [--max-iter 200]
       gridsim dist3ph <feeder.json> fault --bus ID [--type all|3ph|slg|ll|llg] [--zf PU]
       gridsim dist3ph <feeder.json> estimate --measurements <m.json> [--method wls|wlav|branch-current] [--tol 1e-10] [--max-iter 50]
                                      [--robust-passes 8] [--robust-floor 0.001]   (wlav only)
       gridsim dist3ph <feeder.json> harmonics --bus ID --spectrum TYPE --current PU [--afe feeder]
       gridsim dist3ph <feeder.json> to-v2 --to <out.json>   (N339: write the JSON v2 form, then prove it reads back bit for bit)
       (experimental; feeder format gridsim-dist3ph/1 or /2, see user manual ch. 7 and technical ch. 18; a radial .m case or JSON case folder is lifted balanced)
[exit 2]
```

The flags of every mode are in [ch. 19, CLI reference](<0019 CLI reference.md#dist3ph>); the exit codes shared by every
verb are in [ch. 19](<0019 CLI reference.md#parsing-rules-and-exit-codes>).

## Pitfalls

- **A sweep that does not converge reports nothing.** The sweep prints the banner and the size line, then one message
  on stderr, and exits 3. It never prints the last iterate as if it were a solution.

  ```
  $ gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json sweep --max-iter 2
  THREE-PHASE DISTRIBUTION (sweep) - IEEE 4-node test feeder, grounded-wye / grounded-wye step-down, unbalanced load   [EXPERIMENTAL]
    file: data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json
    4 buses, 3 branches, 1 loads, base 6 MVA (three-phase)
  dist3ph: the three-phase sweep did not converge (MaxIterations) after 2 sweep(s); final max voltage change 8.399E-002 pu against tolerance 1.000E-010. No result reported.
  [exit 3]
  ```

  The status word names the cause: `MaxIterations` for the iteration cap, `NonFinite` when a voltage became NaN or
  infinite. The unbalanced 4-node feeder needs 39 sweeps at the default tolerance, so a low `--max-iter` is a likely
  cause of a failure on a feeder that has a solution.
- **A transformer connection outside the six is refused, not approximated.** The reader names the JSON path and exits
  1 with `$.transformers[0].connection: only "gy-gy" (grounded wye / grounded wye), "d-gy" (delta / grounded wye, the
  delta on the From side), "y-d" (ungrounded wye / delta), "d-d" (delta / delta), "oy-od" (open wye / open delta,
  primaries on phases A and B) and "od-od" (open delta / open delta) are supported (got "...")`. A step-up `oy-od` is
  refused the same way. A rated voltage more than 20 % from its bus base is refused as a data error (an off-nominal
  ratio within 20 % loads).
- **A misspelt member is an error.** An unknown member, such as `sigma` for `sigmaPu`, fails with its JSON path instead
  of falling back to a default.
- **A missing file exits 1 with a message that names it.**

  ```
  $ gridsim dist3ph data/dist3ph/nothere.json
  error loading three-phase feeder: three-phase feeder file not found: 'data/dist3ph/nothere.json' (full path data/dist3ph/nothere.json).
  [exit 1]
  ```

  For a relative path the message adds the full path in brackets; the capture shows it repo-relative.
- **A regulator does not regulate.** Its taps are the ones in the file. A change of load does not move them, so a
  heavily loaded feeder can sit outside any voltage band that a real regulator would hold.
- **A fault study ignores load and source impedance.** The current comes from the series path and an infinite source at
  1.0 pu, with no load, capacitor or line-charging contribution. Use `iec60909`
  ([ch. 19](<0019 CLI reference.md#iec60909>)) where the source impedance matters.
- **A fault behind a regulator or on a lateral is refused.** The 123-node feeder refuses a fault at bus 100, because
  the path from the source passes through a regulator:

  ```
  $ gridsim dist3ph data/dist3ph/ieee123.json fault --bus 100
  THREE-PHASE DISTRIBUTION (fault) - IEEE 123 Node Test Feeder   [EXPERIMENTAL]
    file: data/dist3ph/ieee123.json
    128 buses, 127 branches, 85 loads, base 5 MVA (three-phase)
    4 regulator(s), 5 closed switch(es), 2 open switch(es) (not branches), 4 capacitor bank(s), 60 single- or two-phase branch(es)
  dist3ph fault: the sequence fault study cannot model the path to bus 100: branch 160-9004 has a step-voltage regulator, whose off-nominal (per-phase) ratio the series sum of sequence impedances does not hold. Use the three-phase sweep.
  [exit 1]
  ```
- **A bad `--spectrum` exits 2 after the header.** The message lists every valid name.

  ```
  $ gridsim dist3ph data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json harmonics --bus 4 --spectrum nope --current 0.2
  THREE-PHASE DISTRIBUTION (harmonics) - IEEE 4-node test feeder, grounded-wye / grounded-wye step-down, unbalanced load   [EXPERIMENTAL]
    file: data/dist3ph/ieee4-gyGy-stepdown-unbalanced.json
    4 buses, 3 branches, 1 loads, base 6 MVA (three-phase)
  error: --spectrum expected one of 6-pulse, 12-pulse, pv-inverter, ev-charger, heat-pump-inverter, arc-furnace, saturated-transformer, 6-pulse-g5, 12-pulse-g5, afe-g5, ideal but got 'nope'
  [exit 2]
  ```
- **The `VUF %` column is empty on most rows.** The factor is printed once per three-phase bus, on its phase a row, and
  not at all on a bus with fewer than three phases.
- **`--bus` takes the numeric id.** A bus that the file names, such as `RG1`, is addressed by its id (9001 for RG1).
- **A feeder over the licence limit is refused.** A feeder of your own whose bus count is above the installed tier's
  limit exits 2. The `data/dist3ph` folder is never counted ([ch. 4](<0004 Licensing and activation.md>)).
- **Feeders are not network cases.** The bare run and the positive-sequence verbs do not read `gridsim-dist3ph/1`
  files ([ch. 2](<0002 Loading networks.md>)).

## Limits

- The verb is experimental and its banner says so.
- The transformer connections are `gy-gy`, `d-gy`, `y-d`, `d-d`, `oy-od` (step-down only) and `od-od`; magnetising
  branches are refused. Only the sweep models a connection other than `gy-gy`.
- A regulator has fixed taps. There is no regulator control, line-drop compensator or voltage band, and no capacitor
  switching.
- A switch has no impedance, and there is no distributed load along a line.
- The feeder must be one radial tree with one source and no generation in a feeder file. A meshed feeder, a second
  source and a distributed generator are not supported.
- The sweep prints voltages and unbalance only. It prints no branch currents, flows, losses or loading.
- `fault` models an infinite source of zero impedance at 1.0 pu, neglects every shunt element, and refuses a lateral or
  regulator on the path to the fault.
- `estimate` with `wls` or `wlav` refuses laterals, line charging, regulators and closed switches. It has no injection or
  angle measurement and no bad-data test: the residual rows show where `J` comes from, and the reading of them is yours.
  It is dense, so a feeder above 416 non-source buses is refused.
- `estimate --method branch-current` needs a current phasor on every phase of every tree branch and takes no voltage
  measurement.
- `harmonics` takes one balanced source, models no loads, and refuses laterals and regulators. Converter units enter
  only with `--afe feeder`.

## See also

- [../technical/0018 Real-time, distribution and dynamic estimation.md](<../technical/0018 Real-time, distribution and dynamic estimation.md>) - the distribution stack, the file format and the validation against the IEEE feeders
- [../technical/design/three-phase-dsse.md](../technical/design/three-phase-dsse.md) - the three-phase estimator design
- [ch. 19, CLI reference](<0019 CLI reference.md#dist3ph>) - every `dist3ph` flag and the exit codes
- [ch. 19, CLI reference](<0019 CLI reference.md#result-files>) - `--json`, `--csv` and `--out`
- [ch. 2, Loading networks](<0002 Loading networks.md>) - the case formats that `dist3ph` lifts
- [ch. 5, Running power flows](<0005 Running power flows.md>) - the positive-sequence solve that a lifted case agrees with
- [ch. 17, Harmonic studies](<0017 Harmonic studies.md>) - the balanced harmonic verb and the source library
- [ch. 21, State estimation](<0021 State estimation.md>) - the transmission estimator
- [ch. 30, Troubleshooting](<0030 Troubleshooting.md>) - what a non-converged run means
