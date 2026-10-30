# 12. Faults, short circuit and protection grading

A fault study answers three questions about a network: how much current flows when a bus is shorted, which protection
operates to clear it, and whether the protection along a feeder trips in the right order. GridSim answers each with its
own verb. `fault` shorts one bus on top of the solved operating state and lists the relays that operate. `iec60909`
computes the short-circuit duty that a switchgear rating is checked against: initial, peak, breaking and thermal current.
`protection-check` assesses the generic relay set on the steady solved state, on a fault at a bus, or along a cascade of
trips that it follows until the network settles or collapses, and it can compare that cascade with a real alarm log.
`grade` checks that a chain of inverse-time overcurrent relays coordinates. Last, the protection stages that an asset
bundle ships (loss-of-mains, low-frequency demand disconnection and the rest) act inside `trip --asset-dynamics`. Use
this chapter when you need a fault level, a relay verdict, a breaker check or a grading margin.

## Before you start

- `fault` and `protection-check` solve the power flow first and refuse a case that does not converge (exit 3).
  `iec60909` needs no solved state (it ignores the pre-fault flow, as the standard does), and `trip` runs a lumped
  frequency model with no network solve. Run the case through [running power flows](<0005 Running power flows.md>) first if you are unsure.
- `iec60909` needs the faulted bus to carry a nominal voltage. `case9` (345 kV) does; `case14` carries none and is
  refused. `fault` works without one and then prints no kA.
- MATPOWER cases carry no subtransient reactance, so `fault` and `protection-check --fault-bus` give every synchronous
  generator that carries no X''d of its own 0.20 pu on its own rating; power-flow-only units are skipped, and
  converter-interfaced units take part through their current limit instead. The fault levels of those cases are driven by that stated default.
- The relays that measure current (49, 50 and 51) need a branch rating (`RateA`) and skip a branch without one. `case14`
  and `case118` carry no ratings; `case9`, `case30` and `uk` do.
- `--zf` is a reactance in per unit: the fault impedance is `j` times the value.
- The relay settings can be changed per run with `protection-check`'s `--uv`, `--ov`, `--uf`, `--of`, `--oc`,
  `--thermal` and `--rocof` (defaults 0.80 pu, 1.10 pu, 47.5 Hz, 52.0 Hz, 2.50 x rated, 1.00 and 1.0 Hz/s), and the
  IEC 60909 times with `--tb` and `--tk` (defaults 0.10 s and 1.0 s); every flag is in
  [ch. 19](<0019 CLI reference.md>).
- Case selection, MATPOWER files and JSON folders are in [loading networks](<0002 Loading networks.md>).

None of the commands below prints a timing or a stamp, and re-running them reproduces the captured text. The one
exception is a message that names a file you gave, which prints its full path.

## Worked example

### A three-phase fault with `fault`

Short bus 7 of the WSCC 9-bus case with a bolted fault (zero fault impedance):

```bash
gridsim fault case9 --bus 7
```

```
$ gridsim fault case9 --bus 7
Balanced three-phase fault  -  case9  bus 7  Zf=j0.000 pu
  fault current: 9.26 pu (1.55 kA)   Thevenin Z: 0.1098 pu   faulted bus voltage: 0.000 pu
  (generator subtransient reactance synthesised at 0.20 pu; positive-sequence, so three-phase only)
  relays operating on the faulted state: 17
  | device | function | location | quantity | pickup | time | basis |
  |--------|----------|----------|----------|--------|------|-------|
  | 50 | Instantaneous overcurrent | branch 6-7 | 2.655 xIn | 2.500 | inst | branch 6-7 current 2.65x rated exceeds the 2.50x instantaneous pickup (trips instantly) |
  | 87 | Differential | bus 7 | 9.256 pu | 0.100 | inst | busbar differential 9.26E+000 pu at bus 7 exceeds the 0.10 pu bias pickup (internal fault) |
  | 21-Z2 | Distance | branch 6-7 | 0.103 pu | 0.127 | 0.30s | branch 6-7 apparent Z 0.103 pu is inside Zone 2 (reach 0.127 pu, line Z 0.102); operates in 0.3 s |
  | 21-Z2 | Distance | branch 7-8 | 0.073 pu | 0.091 | 0.30s | branch 7-8 apparent Z 0.073 pu is inside Zone 2 (reach 0.091 pu, line Z 0.072); operates in 0.3 s (relay at the to end, bus 8) |
  | 51 | Time overcurrent | branch 6-7 | 2.655 xIn | 1.200 | 0.87s | branch 6-7 current 2.65x rated over the 1.20x pickup; IEC standard-inverse trips in 0.9 s |
  | 51 | Time overcurrent | branch 7-8 | 2.084 xIn | 1.200 | 1.26s | branch 7-8 current 2.08x rated over the 1.20x pickup; IEC standard-inverse trips in 1.3 s |
  | 51 | Time overcurrent | branch 8-2 | 1.621 xIn | 1.200 | 2.32s | branch 8-2 current 1.62x rated over the 1.20x pickup; IEC standard-inverse trips in 2.3 s |
  | 49 | Thermal overload | branch 6-7 | 1.084 xRateA | 1.000 | 1711.28s | branch 6-7 loaded to 108% of RateA (150 MVA); thermal replica trips in 1711 s |
  | 49 | Thermal overload | branch 8-2 | 1.016 xRateA | 1.000 | 3126.35s | branch 8-2 loaded to 102% of RateA (250 MVA); thermal replica trips in 3126 s |
  | 27 | Undervoltage | bus 7 | 0.000 pu | 0.800 | - | Vm 0.000 pu at bus 7 is below the 0.80 pu undervoltage pickup |
  | 27 | Undervoltage | bus 8 | 0.380 pu | 0.800 | - | Vm 0.380 pu at bus 8 is below the 0.80 pu undervoltage pickup |
  | 27 | Undervoltage | bus 6 | 0.408 pu | 0.800 | - | Vm 0.408 pu at bus 6 is below the 0.80 pu undervoltage pickup |
  | 27 | Undervoltage | bus 3 | 0.573 pu | 0.800 | - | Vm 0.573 pu at bus 3 is below the 0.80 pu undervoltage pickup |
  | 27 | Undervoltage | bus 9 | 0.575 pu | 0.800 | - | Vm 0.575 pu at bus 9 is below the 0.80 pu undervoltage pickup |
  | 27 | Undervoltage | bus 5 | 0.593 pu | 0.800 | - | Vm 0.593 pu at bus 5 is below the 0.80 pu undervoltage pickup |
  | 27 | Undervoltage | bus 2 | 0.627 pu | 0.800 | - | Vm 0.627 pu at bus 2 is below the 0.80 pu undervoltage pickup |
  | 27 | Undervoltage | bus 4 | 0.706 pu | 0.800 | - | Vm 0.706 pu at bus 4 is below the 0.80 pu undervoltage pickup |

PROTECTION SIMULATION: a three-phase fault at bus 7 would drive 17 relay operation(s) - primary clearance by 87 (Differential) on bus 7, instantaneously.
[exit 0]
```

The same fault through 0.5 pu of fault impedance:

```bash
gridsim fault case9 --bus 7 --zf 0.5
```

```
$ gridsim fault case9 --bus 7 --zf 0.5
Balanced three-phase fault  -  case9  bus 7  Zf=j0.500 pu
  fault current: 1.67 pu (0.28 kA)   Thevenin Z: 0.1098 pu   faulted bus voltage: 0.836 pu
  (generator subtransient reactance synthesised at 0.20 pu; positive-sequence, so three-phase only)
  relays operating on the faulted state: 1
  | device | function | location | quantity | pickup | time | basis |
  |--------|----------|----------|----------|--------|------|-------|
  | 87 | Differential | bus 7 | 1.671 pu | 0.100 | inst | busbar differential 1.67E+000 pu at bus 7 exceeds the 0.10 pu bias pickup (internal fault) |

PROTECTION SIMULATION: a three-phase fault at bus 7 would drive 1 relay operation(s) - primary clearance by 87 (Differential) on bus 7, instantaneously.
[exit 0]
```

### The short-circuit duty with `iec60909`

```bash
gridsim iec60909 case9 --bus 7
```

```
$ gridsim iec60909 case9 --bus 7
IEC 60909 - IEEE 9-bus (WSCC), bus 7, three-phase, Maximum (60 Hz, tb 0.1 s, Tk 1 s)
  Ik''  = 1.647443765211713 kA  (9.844424248659447 pu)
  ip    = 4.659674632018803 kA  (kappa 2, R/X 0.059150094882557964, meshed network: 1.15 x kappa(R/X), Method B)
  Ib    = 1.630308759669193 kA    Ik = 1.647443765211713 kA  (near a generator, fed from several sources: IEC 60909-0 Ik = Ik''M, the initial current without motors; no motors are modelled)
  Ith   = 1.686527169992305 kA  (m 0.048010143260821356)    idc(tb) = 0.2505472887517321 kA
  Zth   = 0.006597803772079404 + j0.11154341823422752 pu
[exit 0]
```

`case9` carries its WSCC nominal frequency, 60 Hz (the header reads `60 Hz`), and both `case9` and `uk` are meshed,
so the peak takes the Method B factor `kappa_b = min(1.15 x kappa, 2.0)` and the `ip` line carries the `meshed network`
note: at bus 7, `1.15 x 1.8407` is above 2.0, so `kappa_b` is 2 ([ch. 25](<../technical/0025 IEC 60909 short-circuit duty study.md>)).

### A fault assessed with `protection-check`

```bash
gridsim protection-check case9 --fault-bus 7 --zf 0.5
```

```
$ gridsim protection-check case9 --fault-bus 7 --zf 0.5
Protection assessment  -  case9  (9 buses)  three-phase fault at bus 7, Zf=j0.500 pu  [passive; prefault solve unchanged]
  fault current: 1.67 pu (0.28 kA)   Thevenin Z: 0.1098 pu
  (generator subtransient reactance synthesised at 0.20 pu; positive-sequence, so three-phase only)
  operating elements: 1
  | device | function | location | quantity | pickup | time | basis |
  |--------|----------|----------|----------|--------|------|-------|
  | 87 | Differential | bus 7 | 1.671 pu | 0.100 | inst | busbar differential 1.67E+000 pu at bus 7 exceeds the 0.10 pu bias pickup (internal fault) |
  not evaluated without a trajectory or the needed sequence data: 81U (Underfrequency); 81O (Overfrequency); 81R (RoCoF); 78 (Vector shift / LoM); 50N/51N/21N/46 (Ground / negative-sequence)

PROTECTION SIMULATION suggests 1 protection event(s) would occur. e.g. on bus 7, relay 87 (Differential) would likely operate: busbar differential 1.67E+000 pu at bus 7 exceeds the 0.10 pu bias pickup (internal fault).
[exit 0]
```

### A cascade with `protection-check --cascade`

Tighten the thermal pickup to half of rating so that a branch of `case9` operates, then follow what its trip does to the
network:

```bash
gridsim protection-check case9 --cascade --thermal 0.5
```

```
$ gridsim protection-check case9 --cascade --thermal 0.5
Protection assessment  -  case9  (9 buses, converged=True)  [passive; solve unchanged]
  operating elements: 1
  | device | function | location | quantity | pickup | time | basis |
  |--------|----------|----------|----------|--------|------|-------|
  | 49 | Thermal overload | branch 8-2 | 0.653 xRateA | 0.500 | 794.21s | branch 8-2 loaded to 65% of RateA (250 MVA); thermal replica trips in 794 s |
  not evaluated on a steady snapshot: 81U (Underfrequency); 81O (Overfrequency); 81R (RoCoF); 78 (Vector shift / LoM); 50N/51N/21N/46 (Ground / negative-sequence)

PROTECTION SIMULATION suggests 1 protection event(s) would occur. e.g. on branch 8-2, relay 49 (Thermal overload) would likely operate: branch 8-2 loaded to 65% of RateA (250 MVA); thermal replica trips in 794 s.

Cascade propagation (1 trip(s)):
  1. relay 49 (Thermal overload) opens branch 8-2 in 794.21s  ->  re-solve did NOT converge
  stop: non-convergent after opening 8-2 - the network has split / collapsed (blackout).
[exit 0]
```

### Grading relays with `grade`

Three inverse-time relays on a radial feeder, downstream first, graded at two fault currents in amps:

```bash
gridsim grade --chain iec-si:400:0.1,iec-si:400:0.3,iec-si:400:0.6 --current 1647,4000
```

```
$ gridsim grade --chain iec-si:400:0.1,iec-si:400:0.3,iec-si:400:0.6 --current 1647,4000
Overcurrent grading  -  3 relays (downstream first), CTI 0.4 s
  R1: IecStandardInverse, pickup 400, time dial 0.1
  R2: IecStandardInverse, pickup 400, time dial 0.3
  R3: IecStandardInverse, pickup 400, time dial 0.6

  fault current 1647
  | pair | primary s | backup s | margin s | verdict |
  |------|-----------|----------|----------|---------|
  | R1->R2 | 0.4876 | 1.4629 | 0.9753 | OK |
  | R2->R3 | 1.4629 | 2.9259 | 1.4629 | OK |

  fault current 4000
  | pair | primary s | backup s | margin s | verdict |
  |------|-----------|----------|----------|---------|
  | R1->R2 | 0.2971 | 0.8912 | 0.5941 | OK |
  | R2->R3 | 0.8912 | 1.7824 | 0.8912 | OK |

GRADED: every pair coordinated at every current; binding margin 0.5941 s (CTI 0.4 s).
[exit 0]
```

### Asset protection stages in `trip --asset-dynamics`

The synthetic GB case ships two placed protections in `protections.json`. Trip its largest unit with the fleet off, then
on:

```bash
gridsim trip data/gb-synthetic
gridsim trip data/gb-synthetic --asset-dynamics
```

```
$ gridsim trip data/gb-synthetic
GridSim  -  contingency on GB synthetic
================================================================
Tripped:        Hinkley Point Nuclear  (2880 MW lost, nuclear)
System inertia: 171.7 -> 158.9 GVAs after the trip
Peak RoCoF:     -0.453 Hz/s  (largest |df/dt| over the run)
Nadir:          48.334 Hz  (t+6.9 s)
Settling:       49.142 Hz

50.05 Hz |***+==-:,,..           ....,,,::::------========------------::::| 48.33 Hz (nadir)

...at HALF the inertia (79.5 GVAs): RoCoF -0.906 Hz/s, nadir 47.872 Hz

Frequency protection on this trajectory (G99 Issue 2 Table 10.1 settings; system-wide, not per site):
  81U  Underfrequency       holds: run minimum frequency 48.334 Hz (>47.5 Hz)
  81O  Overfrequency        holds: run maximum frequency 50.000 Hz (<52.0 Hz)
  81R  RoCoF                holds: peak |df/dt| 0.453 Hz/s (<1.00 Hz/s)
  78   Vector shift / LoM   not evaluated: vector shift / loss-of-mains needs a voltage-angle transient; this run is a frequency (centre-of-inertia) trajectory with no bus voltage angle
[exit 0]
```

```
$ gridsim trip data/gb-synthetic --asset-dynamics
Asset dynamics: on  (8 class(es), 2 protection(s); 1 unit(s) tripped, 800 MW of infeed lost: 0 inverter(s) 0 MW (Loss-of-Mains or ride-through), 1 protection device(s) 800 MW)
  note: 40 asset(s) carry voltage or loading trip stages (LVRT, under- / over-voltage, overload); this lumped run solves no network, so those stages are not evaluated (transient and the live views evaluate them against the solved state).
GridSim  -  contingency on GB synthetic
================================================================
Tripped:        Hinkley Point Nuclear  (2880 MW lost, nuclear)
System inertia: 171.7 -> 158.9 GVAs after the trip
Peak RoCoF:     -0.576 Hz/s  (largest |df/dt| over the run)
Nadir:          49.052 Hz  (t+5.5 s)
Settling:       49.525 Hz

50.05 Hz |***=-:,..           ....,,,:::------===========------------:::::| 49.05 Hz (nadir)

...at HALF the inertia (79.5 GVAs): RoCoF -0.906 Hz/s, nadir 47.872 Hz

Frequency protection on this trajectory (G99 Issue 2 Table 10.1 settings; system-wide, not per site):
  81U  Underfrequency       holds: run minimum frequency 49.052 Hz (>47.5 Hz)
  81O  Overfrequency        holds: run maximum frequency 50.000 Hz (<52.0 Hz)
  81R  RoCoF                holds: peak |df/dt| 0.576 Hz/s (<1.00 Hz/s)
  78   Vector shift / LoM   not evaluated: vector shift / loss-of-mains needs a voltage-angle transient; this run is a frequency (centre-of-inertia) trajectory with no bus voltage angle
[exit 0]
```

## Reading the output

### `fault`

**Title line.** `Balanced three-phase fault  -  case9  bus 7  Zf=j0.000 pu` names the case, the faulted bus and the fault
impedance in per unit, written as a complex value (`j0.500` for `--zf 0.5`). An unbalanced `--type` changes the title (see [Unbalanced faults](#unbalanced-faults)).

**Fault line.** `fault current: 9.26 pu (1.55 kA)   Thevenin Z: 0.1098 pu   faulted bus voltage: 0.000 pu`.

- The fault current is the current into the fault, in per unit on the system base and, when the bus has a base voltage,
  in kA. 9.26 pu on 100 MVA at 345 kV is 1.55 kA.
- `Thevenin Z` is the driving-point impedance the fault sees at the bus, with every generator at its subtransient
  reactance and every load as a constant impedance.
- The faulted bus voltage is the fault current times the fault reactance. It is 0.000 pu for a bolted fault and 0.836 pu
  for the 0.5 pu fault (the magnitude of 1.67 pu times j0.5 pu).

**Source line.** `(generator subtransient reactance synthesised at 0.20 pu; positive-sequence, so three-phase only)`.
`case9` carries no X''d, so each of its three generators contributes 0.20 pu on its own MVA base.

**Converter line.** Printed only when the case has converter-interfaced units. In the `uk` case it reads
`converter-interfaced units: 16 (current-limited, K=2.0 reactive priority)  total infeed 1.96 pu, 5 at current limit`:
the number of units, the reactive-current gain of their fault control, their summed fault infeed and how many sit at their
current limit. A trailing `WARNING: converter current iteration did not converge` marks an unreliable faulted state.

**Relay table.** `relays operating on the faulted state: 17` counts the rows. Each row is one element at one location.

| Column | Meaning |
|---|---|
| `device` | The ANSI device number. A distance element carries its zone: `21-Z2` is zone 2. `21N` is the earth-loop form. |
| `function` | The element name: `Differential`, `Time overcurrent`, `Distance`, `Undervoltage`, `Thermal overload` and the rest. |
| `location` | A bus, a named branch (`branch 6-7` when the case names none) or a named device such as `Moyle`. |
| `quantity` | The measured value with its unit: `pu`, `xIn` (multiple of rated current), `xRateA` (loading against rating) or `MW`. |
| `pickup` | The setting the quantity is compared with, in the same unit. |
| `time` | `inst` for an instantaneous operation, a time in seconds for an element with a curve or a zone delay, `-` for an element with no time. |
| `basis` | One sentence that restates the comparison in words. |

Rows are ordered fastest first: the elements with an operating time (`inst` first, then by time), then the elements
with no time, largest margin relative to the pickup first; equal times keep the relay order. Reading the case9 table:

- `87 Differential bus 7, 9.256 pu`: the busbar differential at the faulted bus sees the whole fault current against its
  0.10 pu pickup. It operates on every balanced and line-to-line fault shown here.
- `51 Time overcurrent branch 6-7, 2.655 xIn, 0.87s`: the branch carries 2.65 times its rated current. The 1.2 x rated
  pickup is exceeded and the standard-inverse curve at time multiplier 0.1 operates in 0.87 s.
- `50 Instantaneous overcurrent branch 6-7, 2.655 xIn, inst`: the same current is above the 2.5 x rated instantaneous
  pickup.
- `21-Z2 Distance branch 7-8, 0.073 pu`: the apparent impedance at the 8 end of the line, looking into it, lies inside
  zone 2 (reach 1.25 times the line impedance) and operates after the 0.3 s zone delay. A line is judged from both ends
  and reported from the end that operates in the fastest zone; the basis says when that end is the `to` end.
- `27 Undervoltage`: one row per bus below 0.80 pu. Bus 7 is at 0.000 pu and bus 4 at 0.706 pu.
- `49 Thermal overload branch 6-7, 1.084 xRateA, 1711.28s`: the fault current is a snapshot, not a duration. The
  thermal replica reports how long the branch would take to trip if it carried that loading continuously. 1711 s is a
  statement about the loading, not a clearing time.

**Closing line.** `PROTECTION SIMULATION: a three-phase fault at bus 7 would drive 17 relay operation(s) - primary
clearance by 87 (Differential) on bus 7, instantaneously.` The device named is the primary clearance: the busbar
differential (87) when one operates, otherwise the fastest timed element on a branch. Here it is the second row, after
the equally instantaneous `50` on branch 6-7. It is a rule on the table, not the result of a grading study (see
[Pitfalls](#pitfalls)).

### `iec60909`

The first line states the study: `IEC 60909 - IEEE 9-bus (WSCC), bus 7, three-phase, Maximum (60 Hz, tb 0.1 s, Tk 1 s)`.
The objective (`Maximum` or `Minimum`), the breaker delay `tb` and the fault duration `Tk` are the three settings that
change the numbers. Values print at round-trip precision.

| Field | Meaning |
|---|---|
| `Ik''` | The initial symmetrical short-circuit current, in kA and in per unit: 1.647 kA at bus 7. |
| `ip` | The peak current, the largest instantaneous value including the DC offset: 4.660 kA. It follows from `Ik''` through the peak factor `kappa` (2, the cap, because 1.15 x 1.8407 is above it) of the fault-loop `R/X` (0.0592). In a meshed fault network (a ring, a parallel circuit, or more than one source) `kappa` is IEC 60909-0 Method B's `1.15 x kappa(R/X)`, capped at 2.0 (1.8 at 1 kV and below), and the line says `meshed network: 1.15 x kappa(R/X), Method B`. |
| `Ib` | The symmetrical breaking current at the breaker delay `tb`. It is below `Ik''` (1.630 kA against 1.647 kA) because the generators near bus 7 decay during the first 0.1 s. |
| `Ik` | The steady-state current, with a note saying which rule it follows: far from every generator `Ik` equals `Ik''`; near a generator fed from several sources it is the standard's `Ik''M` (the initial current without motors; GridSim models none); near a generator that is the fault's only synchronous source it reads `UPPER BOUND`, because the standard's `lambda x IrG` needs a saturated reactance no case carries (`Ith` is then an upper bound too). |
| `Ith` | The thermal-equivalent current over `Tk`: 1.687 kA, with the DC heating factor `m` (0.0480). |
| `idc(tb)` | The DC component remaining at the breaker delay: 0.251 kA. |
| `Zth` | The positive-sequence Thevenin impedance at the bus, `R + jX` in per unit. |

The duty and the `fault` current are different quantities. `iec60909` drives the fault from the equivalent voltage
source `c x Un` with `c` = 1.10, neglects the loads and ignores the prefault voltages: 9.84 pu at bus 7. `fault`
superposes the fault on the solved operating state with the loads as constant impedances: 9.26 pu. Use the duty for
equipment ratings and the `fault` current for the relay picture of one operating point. The method and its factors are in
[the technical chapter](<../technical/0025 IEC 60909 short-circuit duty study.md>).

The study covers the fault bus's own island only (the buses joined to it through in-service branches); a fault bus whose
island has no source is refused by name (exit 1).

`--peak-method c` finds `kappa` by Method C, the equivalent-frequency method NESO uses for peak-make: the driving-point
impedance is recomputed with every reactance at `fc = 0.4 f`, and `kappa = 1.02 + 0.98 e^(-3 R/X)` with no 1.15 factor
and no cap. Method C also takes `idc(tb)` from the network at a second equivalent frequency set by `f x tb`, and a duty
with `f x tb` of 10 cycles or more is refused (exit 1). It works for every `--type`, on each fault's own sequence loop.
`Ik''`, `Ib`, `Ith` and `m` are Method B's either way, and the `ip` line names the method:

```bash
gridsim iec60909 case9 --bus 7 --peak-method c
```

When the case has converter-interfaced units, a converter share is appended to the `Ik''` line, as in
`gridsim iec60909 uk --bus 1`:

```
$ gridsim iec60909 uk --bus 1
IEC 60909 - GB reduced (10-zone), bus 1, three-phase, Maximum (50 Hz, tb 0.1 s, Tk 1 s)
  Ik''  = 77.47444481582528 kA  (5.367586988368024 pu), of which converters 15.657490469631155 kA
...
[exit 0]
```

### `protection-check`

The steady form prints a header, the operating elements, the elements that could not be evaluated and a verdict:

```bash
gridsim protection-check uk
```

```
$ gridsim protection-check uk
Protection assessment  -  uk  (20 buses, converged=True)  [passive; solve unchanged]
  operating elements: 1
  | device | function | location | quantity | pickup | time | basis |
  |--------|----------|----------|----------|--------|------|-------|
  | 32 | Reverse power | Moyle | -150.000 MW | -5.000 | - | Moyle is absorbing 150.0 MW (reverse/motoring past the 5 MW pickup) |
  not evaluated on a steady snapshot: 81R (RoCoF); 78 (Vector shift / LoM); 50N/51N/21N/46 (Ground / negative-sequence)

PROTECTION SIMULATION suggests 1 protection event(s) would occur. e.g. on Moyle, relay 32 (Reverse power) would likely operate: Moyle is absorbing 150.0 MW (reverse/motoring past the 5 MW pickup).
[exit 0]
```

- **Header.** `Protection assessment  -  uk  (20 buses, converged=True)  [passive; solve unchanged]`. The assessment reads
  the solved state and never alters it.
- **Operating elements.** The same seven columns as the `fault` table. Here one element operates: relay 32 (reverse
  power) on `Moyle`, which is absorbing 150 MW against the 5 MW reverse-power pickup. The table is omitted when the
  count is 0.
- **Not evaluated.** `81R (RoCoF); 78 (Vector shift / LoM); 50N/51N/21N/46 (Ground / negative-sequence)` are the elements
  that cannot be judged on a steady snapshot: the first two need a frequency trajectory and the last four need sequence
  currents. They are listed, never silently passed. A `--fault-bus` run words the line `not evaluated without a
  trajectory or the needed sequence data`; a balanced fault still lists the ground elements, because it has no sequence
  currents, and an unbalanced `--fault-type` evaluates them.
- **Verdict.** `PROTECTION SIMULATION suggests 1 protection event(s) would occur. e.g. on Moyle, relay 32 ...` names the
  first operating element. With nothing operating the line reads `PROTECTION SIMULATION: no relay would operate on this
  state (within the generic settings).`

With `--fault-bus 7 --zf 0.5` (the worked example above) the header reads
`three-phase fault at bus 7, Zf=j0.500 pu  [passive; prefault solve unchanged]` and a fault line and a source line follow
it exactly as `fault` prints them. The fault through 0.5 pu draws 1.67 pu, which stays under every current pickup, so
only the differential operates. The bolted fault operates 17 elements.

With `--cascade` the steady assessment is followed by the cascade block, as in the worked example:

- `Cascade propagation (1 trip(s)):` lists one numbered line per trip. Each line gives the relay that tripped, the branch
  it opened, its operating time and the result of re-solving the network without that branch.
- `1. relay 49 (Thermal overload) opens branch 8-2 in 794.21s  ->  re-solve did NOT converge`. The thermal element on
  branch 8-2 is the first to operate, with the shortest time of any branch element, and the network without that branch
  has no power-flow solution.
- `stop: non-convergent after opening 8-2 - the network has split / collapsed (blackout).` is the stop reason. The other
  two are `settled - no further branch trips` and `reached the 12-step cap (still cascading)`.
- A step whose re-solve converges reads `-> N branch(es) then overloaded` instead, with the count of rated branches loaded
  above the thermal pickup after the trip (`--thermal` times 100 percent of `RateA`; the rating itself at the default
  1.00). The next step trips the first of the elements that now operate.
- Each step trips the branch element with the shortest operating time. Elements with the same operating time (for
  example several instantaneous 50s) operate at the same instant; the cascade takes the one listed first, in relay
  order and then branch order, the same rule as the operating list.

When nothing operates the block is the single line `Cascade: stable - no branch relay would operate.` Only the
branch-tripping elements (49, 50, 51 and 21) propagate. Voltage and frequency elements are left to the asset layer.

### `grade`

The top of the worked example reads:

```
Overcurrent grading  -  3 relays (downstream first), CTI 0.4 s
  R1: IecStandardInverse, pickup 400, time dial 0.1
```

The header gives the relay count and the coordination time interval (CTI) in force. Each `R<n>` line echoes one relay as
parsed: its curve, its pickup and its time dial, or for definite time its delay. Relays are numbered in the order given,
downstream first.

Each fault current gets one table with one row per adjacent pair:

| Column | Meaning |
|---|---|
| `pair` | `R1->R2`: relay 1 is the primary, relay 2 its backup. |
| `primary s` | The primary's operating time at that current. `never` when the current is at or below its pickup. |
| `backup s` | The backup's operating time at that current. |
| `margin s` | Backup time minus primary time. `inf` when the backup never operates, `-inf` when the primary never operates but the backup does, `-` when neither does. |
| `verdict` | `OK`, `NOT COORDINATED (margin below CTI)`, `NOT COORDINATED (primary does not operate)` or `NOT COORDINATED (backup does not operate)`. |

A pair is coordinated when the primary operates, the backup operates, and the backup lags the primary by at least the CTI. At 1647 A the primary R1
operates in 0.4876 s and R2 in 1.4629 s: a margin of 0.9753 s, which clears 0.4 s. The closing line
`GRADED: every pair coordinated at every current; binding margin 0.5941 s (CTI 0.4 s).` gives the smallest margin among
the coordinated pairs, here R1->R2 at 4000 A. `NOT GRADED: at least one pair is not coordinated (see above).` replaces it
when any pair fails, and the exit code is 3.

### The asset stages in `trip`

With `--asset-dynamics` and a case that ships an asset bundle, `trip` prints a first line before the usual report:

`Asset dynamics: on  (8 class(es), 2 protection(s); 1 unit(s) tripped, 800 MW of infeed lost: 0 inverter(s) 0 MW
(Loss-of-Mains or ride-through), 1 protection device(s) 800 MW)` (one line in the output)

- `class(es)` is the size of the asset-class library in `asset-classes.json`.
- `protection(s)` is the number of placed protection devices, one per entry in `protections.json`. A device's class can
  hold several stages.
- `unit(s) tripped` and `MW of infeed lost` count every unit whose infeed-trip stage fired during the run and the infeed
  it removed, then split them: inverter-based units tripped by their own loss-of-mains or ride-through stages, and
  placed protection devices. Here the 800 MW is one protection device, the embedded-generation loss-of-mains stage
  `embedded-dg-lom`.
- A demand-shed stage (LFDD) acts but is not counted: it removes load, not infeed, so it raises neither number.
- The `note:` line that follows counts the assets whose voltage or loading stages the lumped run cannot evaluate.

The rest of the report is the plain `trip` report, which [frequency studies](<0014 Frequency studies.md>) reads line by
line, run on the fleet. Compare the two runs above: the nadir moves from 48.334 Hz to 49.052 Hz, the settling frequency
from 49.142 Hz to 49.525 Hz and the peak RoCoF from -0.453 to -0.576 Hz/s. The fleet adds converter frequency response,
which lifts the nadir, and the loss-of-mains stage removes 800 MW of embedded generation, which deepens the fall; the net
is the improvement shown. The `...at HALF the inertia` line is always the plain lumped model without the fleet, so it
reads 47.872 Hz in both runs.

The stages themselves are described in [protections](<../technical/0015 Protections.md>); the placement file and an authoring
recipe are in [extending GridSim](<0029 Extending GridSim.md>).

## Variations

### Unbalanced faults

`--type ll`, `--type slg` and `--type dlg` replace the balanced study with the sequence-network study. Line to line on
`case14`, bus 4:

```bash
gridsim fault case14 --bus 4 --type ll
```

```
$ gridsim fault case14 --bus 4 --type ll
Line to line (phases b-c) fault  -  case14  bus 4  Zf=j0.000 pu
  phase currents: Ia 0.000 pu  Ib 10.752 pu  Ic 10.752 pu
  earth current |3I0|: 0.000 pu   sequence |I1| 6.207  |I2| 6.207  |I0| 0.000 pu
  faulted bus phase voltages: Va 1.018  Vb 0.509  Vc 0.509 pu
  driving-point Z1 0.0166+j0.0803  Z2 0.0166+j0.0803  Z0 infinite (no earth path in the zero sequence)
  (generator X''d synthesised at 0.20 pu where not supplied, X2 = X''d where not supplied; line Z0 = 3 x Z1 where not supplied; loads neglected in the zero sequence; converter units keep their prefault current)
  relays operating on the faulted state: 22 (phase elements on the phase currents and voltages; 50N/51N/21N/46 on the sequence currents)
  | device | function | location | quantity | pickup | time | basis |
  |--------|----------|----------|----------|--------|------|-------|
  | 21-Z1 | Distance | branch 2-4 | 0.000 pu | 0.149 | inst | branch 2-4 apparent Z 0.000 pu is inside Zone 1 (reach 0.149 pu, line Z 0.186); operates in 0.0 s (relay at the to end, bus 4) [b-c loop] |
  | 21-Z1 | Distance | branch 3-4 | 0.000 pu | 0.147 | inst | branch 3-4 apparent Z 0.000 pu is inside Zone 1 (reach 0.147 pu, line Z 0.184); operates in 0.0 s (relay at the to end, bus 4) [b-c loop] |
  | 21-Z1 | Distance | branch 4-5 | 0.000 pu | 0.035 | inst | branch 4-5 apparent Z 0.000 pu is inside Zone 1 (reach 0.035 pu, line Z 0.044); operates in 0.0 s [b-c loop] |
  | 87 | Differential | bus 4 | 10.752 pu | 0.100 | inst | busbar differential 1.08E+001 pu at bus 4 exceeds the 0.10 pu bias pickup (internal fault) |
...

PROTECTION SIMULATION: a line to line (phases b-c) fault at bus 4 would drive 22 relay operation(s) - primary clearance by 87 (Differential) on bus 4, instantaneously.
[exit 0]
```

The title names the fault, `Line to line (phases b-c)`. The extra lines read as follows.

- `phase currents`: the three phase currents. A line-to-line fault leaves phase a at 0 and drives 10.752 pu, equal and
  opposite, in phases b and c. That is the square root of 3 times the sequence current `|I1|` of 6.207 pu.
- `earth current |3I0|`: the residual current. It is 0 here because a line-to-line fault has no earth path.
- `faulted bus phase voltages`: phase a keeps 1.018 pu; b and c fall to half of it.
- `driving-point Z1 ... Z2 ... Z0`: the three sequence impedances at the bus. `Z2` equals `Z1` because a generator with no
  separate negative-sequence reactance takes its X''d. `Z0` is `infinite` because the case holds no earthed neutral.
- The parenthesis lists the stated defaults the study applied: X''d 0.20 pu, X2 equal to X''d, line zero-sequence
  impedance three times the positive-sequence value, loads neglected in the zero sequence, converter units at their
  prefault current.
- The relay count line says which quantities fed which elements. Phase elements read the phase currents and voltages;
  the earth-fault and negative-sequence elements (`50N`, `51N`, `21N` and `46`) read the sequence currents. They are
  evaluated here; `protection-check` lists them as not evaluated for a balanced fault.

`protection-check` takes the same study with `--fault-type`:

```bash
gridsim protection-check case9 --fault-bus 7 --fault-type ll
```

```
$ gridsim protection-check case9 --fault-bus 7 --fault-type ll
Protection assessment  -  case9  (9 buses)  ll fault at bus 7, Zf=j0.000 pu  [passive; prefault solve unchanged]
  phase currents Ia 0.00  Ib 8.02  Ic 8.02 pu   earth current |3I0| 0.00 pu   (no earth path in the zero sequence at the fault bus)
  (phase elements on the phase currents and voltages; 50N/51N/21N/46 on the sequence currents; generic pickups)
  operating elements: 27
  | device | function | location | quantity | pickup | time | basis |
  |--------|----------|----------|----------|--------|------|-------|
  | 21-Z1 | Distance | branch 6-7 | 0.000 pu | 0.081 | inst | branch 6-7 apparent Z 0.000 pu is inside Zone 1 (reach 0.081 pu, line Z 0.102); operates in 0.0 s (relay at the to end, bus 7) [b-c loop] |
  | 21-Z1 | Distance | branch 7-8 | 0.000 pu | 0.058 | inst | branch 7-8 apparent Z 0.000 pu is inside Zone 1 (reach 0.058 pu, line Z 0.072); operates in 0.0 s [b-c loop] |
  | 87 | Differential | bus 7 | 8.016 pu | 0.100 | inst | busbar differential 8.02E+000 pu at bus 7 exceeds the 0.10 pu bias pickup (internal fault) |
...
  not evaluated without a trajectory or the needed sequence data: 81U (Underfrequency); 81O (Overfrequency); 81R (RoCoF); 78 (Vector shift / LoM)

PROTECTION SIMULATION suggests 27 protection event(s) would occur. e.g. on branch 6-7, relay 21-Z1 (Distance) would likely operate: branch 6-7 apparent Z 0.000 pu is inside Zone 1 (reach 0.081 pu, line Z 0.102); operates in 0.0 s (relay at the to end, bus 7) [b-c loop].
[exit 0]
```

The full table also lists the `46` negative-sequence element on every branch whose negative-sequence current is more
than 0.2 times its rated current.

### The duty at every bus

```bash
gridsim iec60909 case9 --all-buses
```

```
$ gridsim iec60909 case9 --all-buses
IEC 60909 sweep - IEEE 9-bus (WSCC), every bus, three-phase, Maximum (60 Hz, tb 0.1 s, Tk 1 s)
   bus      kV        Ik'' kA        ip kA        Ib kA       Ith kA
       1   345.0        2.9344       8.2997       2.3509       3.1412
       2   345.0        2.4939       7.0539       2.0413       2.6870
       3   345.0        1.9393       5.4851       1.6375       2.0352
       4   345.0        2.1550       6.0953       2.0600       2.2268
       5   345.0        1.4684       4.1438       1.4684       1.4882
       6   345.0        1.8603       5.2616       1.7482       1.9115
       7   345.0        1.6474       4.6597       1.6303       1.6865
       8   345.0        2.0685       5.8507       1.9707       2.1454
       9   345.0        1.5445       4.3685       1.5445       1.5716
  9 bus(es) studied, 0 not studied; largest Ik'' 2.934393338019216 kA at bus 1
[exit 0]
```

One row per bus that has a base voltage, in kA, then the largest `Ik''` and its bus. A bus that cannot be studied is
listed with its reason and the run exits 1 after the table.

### Branch currents for a relay

`--branches` lists the 12 largest in-service branch currents for a three-phase fault at the bus, which is the current
a relay on that branch measures. The header counts every in-service branch in the fault bus's island, so on a case with more than 12 the list
is shorter than the count. The currents are always for the `c_max` source, whatever `--objective` says:

```bash
gridsim iec60909 case9 --bus 7 --branches
```

```
$ gridsim iec60909 case9 --bus 7 --branches
IEC 60909 - IEEE 9-bus (WSCC), bus 7, three-phase, Maximum (60 Hz, tb 0.1 s, Tk 1 s)
...
  branch currents (9 in-service branch(es) in the fault bus's island, largest first; c_max source):
    branch 7-8                    7 -> 8       from end 0.932 kA  to end 0.932 kA
    branch 6-7                    6 -> 7       from end 0.716 kA  to end 0.716 kA
    branch 8-2                    8 -> 2       from end 0.700 kA  to end 0.700 kA
    branch 3-6                    3 -> 6       from end 0.519 kA  to end 0.519 kA
    branch 1-4                    1 -> 4       from end 0.429 kA  to end 0.429 kA
    branch 8-9                    8 -> 9       from end 0.232 kA  to end 0.232 kA
    branch 9-4                    9 -> 4       from end 0.232 kA  to end 0.232 kA
    branch 4-5                    4 -> 5       from end 0.197 kA  to end 0.197 kA
    branch 5-6                    5 -> 6       from end 0.197 kA  to end 0.197 kA
[exit 0]
```

The rows are largest first, each with the current at the from and to end. `branch 7-8` carries 0.932 kA of the 1.647 kA
fault current. These are the currents to feed `grade` for a relay on that branch, in amps.

### Minimum duty and line to line

`--objective min` uses the minimum voltage factor (1.00 against 1.10 above 1 kV; 0.95 against 1.05 at or below), the
figure that decides whether a relay still picks up. `--type ll` gives the line-to-line duty, which is `sqrt(3)/2` of the
three-phase value when `Z2` equals `Z1`, as here:

```bash
gridsim iec60909 case9 --bus 7 --objective min
gridsim iec60909 case9 --bus 7 --type ll
```

```
$ gridsim iec60909 case9 --bus 7 --objective min
IEC 60909 - IEEE 9-bus (WSCC), bus 7, three-phase, Minimum (60 Hz, tb 0.1 s, Tk 1 s)
  Ik''  = 1.4976761501924665 kA  (8.949476589690406 pu)
...
[exit 0]
```

```
$ gridsim iec60909 case9 --bus 7 --type ll
IEC 60909 - IEEE 9-bus (WSCC), bus 7, line-to-line, Maximum (60 Hz, tb 0.1 s, Tk 1 s)
  Ik''  = 1.4267281519796298 kA  (8.525521484970616 pu)
...
[exit 0]
```

The minimum `Ik''` is 1.4977 kA, which is the maximum 1.6474 kA scaled by 1.00/1.10. The line-to-line `Ik''` is 1.4267 kA.

### A breaker rating check

`--breaker kV,making,breaking,withstand[,seconds]` checks a switchgear rating against the duty and prints each margin as
rating minus demand, in kA. The withstand is first referred to `Tk` on an equal-energy basis (times
`sqrt(seconds / Tk)`), so a rating quoted for another duration is scaled before the comparison:

```bash
gridsim iec60909 case9 --bus 7 --breaker 362,5,2,2,1
gridsim iec60909 case9 --bus 7 --breaker 362,3,1.5,1.5,1
gridsim iec60909 case9 --bus 7 --breaker 345,5,2,2,1
```

```
$ gridsim iec60909 case9 --bus 7 --breaker 362,5,2,2,1
IEC 60909 - IEEE 9-bus (WSCC), bus 7, three-phase, Maximum (60 Hz, tb 0.1 s, Tk 1 s)
...
  breaker 362 kV / making 5 kA / breaking 2 kA / 2 kA for 1 s: ADEQUATE
    voltage ok (system 345 kV, highest 362 kV)   making margin 0.3403253679811966 kA   breaking margin 0.369691240330807 kA   thermal margin 0.313472830007695 kA
[exit 0]
```

```
$ gridsim iec60909 case9 --bus 7 --breaker 362,3,1.5,1.5,1
IEC 60909 - IEEE 9-bus (WSCC), bus 7, three-phase, Maximum (60 Hz, tb 0.1 s, Tk 1 s)
...
  breaker 362 kV / making 3 kA / breaking 1.5 kA / 1.5 kA for 1 s: NOT ADEQUATE
    voltage ok (system 345 kV, highest 362 kV)   making margin -1.6596746320188034 kA   breaking margin -0.13030875966919298 kA   thermal margin -0.186527169992305 kA
[exit 3]
```

```
$ gridsim iec60909 case9 --bus 7 --breaker 345,5,2,2,1
IEC 60909 - IEEE 9-bus (WSCC), bus 7, three-phase, Maximum (60 Hz, tb 0.1 s, Tk 1 s)
...
  breaker 345 kV / making 5 kA / breaking 2 kA / 2 kA for 1 s: NOT ADEQUATE
    voltage TOO LOW (system 345 kV, highest 362 kV)   making margin 0.3403253679811966 kA   breaking margin 0.369691240330807 kA   thermal margin 0.313472830007695 kA
[exit 3]
```

The rated voltage must cover the highest system voltage `Um` of the bus (IEC 60038), not its nominal voltage: 362 kV
for the 345 kV bus here, 145 kV for 132 kV, 420 kV for 400 kV. The first breaker, rated 362 kV, passes: its margins
are 0.340 kA (making, against `ip`), 0.370 kA (breaking, against `Ib`) and 0.313 kA (short-time withstand, against
`Ith`), so it is `ADEQUATE` and the run exits 0. The second has the voltage but not the current: all three margins are
negative, so it is `NOT ADEQUATE` and the run exits 3. The third has the first breaker's currents but a 345 kV rating,
below `Um`, so it prints `voltage TOO LOW` and is `NOT ADEQUATE` (exit 3) whatever its current margins. A sixth value, the rated asymmetrical breaking current in kA, adds a check of the DC
breaking duty `sqrt(Ib^2 + idc^2)`. A double-line-to-earth duty is checked at its largest line-conductor current (the
`Ik''` it prints is the earth current `3 I0`).

### Impedance-correction factors

`--corrections --rated-pf 0.85` applies the IEC 60909-0 correction factors before the study: to the generators and,
where the case has them, to power-station units and rated transformers. On `case9` only `K_G` applies. The rated power factor is required because the model carries none:

```bash
gridsim iec60909 case9 --bus 7 --corrections --rated-pf 0.85
```

```
$ gridsim iec60909 case9 --bus 7 --corrections --rated-pf 0.85
IEC 60909-0 impedance corrections (rated power factor 0.85): 3 applied
  K_G: 3 element(s), 0.9952 to 0.9952
IEC 60909 - IEEE 9-bus (WSCC), bus 7, three-phase, Maximum (60 Hz, tb 0.1 s, Tk 1 s)
  Ik''  = 1.6502883782007547 kA  (9.861422447735318 pu)
...
[exit 0]
```

The first block lists the factors applied, here `K_G` on the three generators at 0.9952. `Ik''` rises from 1.6474 kA to
1.6503 kA.

### A cascade against an alarm log

`--alarms` compares the predicted cascade with a sequence-of-events log. The log is CSV with the header
`at_s,device,from,to,description`: seconds from the start of the event, the ANSI device as logged, the two end buses of
the branch (both 0 for an alarm that is not a branch, such as a bus voltage alarm), and free text that may itself contain
commas. Blank lines and lines starting with `#` are skipped; any other malformed line is refused.

```
at_s,device,from,to,description
0.10,50,6,8,instantaneous trip, line 6-8
0.35,50,28,8,instantaneous trip, line 8-28
0.60,27,0,0,undervoltage at bus 8
```

This is `docs/_captures/protection-user/inputs/soe-agree.csv`. Tighten the instantaneous pickup to 1.0 times rating on
`case30` and run the cascade against it:

```bash
gridsim protection-check case30 --cascade --oc 1.0 --alarms docs/_captures/protection-user/inputs/soe-agree.csv
```

```
$ gridsim protection-check case30 --cascade --oc 1.0 --alarms docs/_captures/protection-user/inputs/soe-agree.csv
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

The block header counts the alarms (`3 alarm(s), 1 non-branch not matched`) and the four match classes. Matching is by
branch and order, not by time, and the device is not compared (a logged `51` agrees with a predicted `50` on the
same branch): each `Agreement` line gives the predicted position and the observed time. The two branch
trips agree with the two predicted trips, and the undervoltage alarm, which has no branch, is listed but not matched.
`SOE: FULLY CONSISTENT` exits 0.

Against a log in which the second trip is on another branch, the verdict reverses (`soe-diverge.csv` has the first trip on
6-8 as predicted and the second on 2-6):

```
$ gridsim protection-check case30 --cascade --oc 1.0 --alarms docs/_captures/protection-user/inputs/soe-diverge.csv
...
Cascade propagation (2 trip(s)):
  1. relay 50 (Instantaneous overcurrent) opens branch 6-8 in inst  ->  2 branch(es) then overloaded
  2. relay 50 (Instantaneous overcurrent) opens branch 8-28 in inst  ->  re-solve did NOT converge
  stop: non-convergent after opening 8-28 - the network has split / collapsed (blackout).

Sequence of events vs prediction (2 alarm(s), 0 non-branch not matched): 1 agree, 1 predicted not observed, 1 observed not predicted, 0 out of order
  PredictedNotObserved: predicted 50 trip on branch 8-28 not seen in the alarm log
  ObservedNotPredicted: observed 51 trip on branch 2-6 was not predicted
  Agreement: predicted and observed on branch 6-8 (predicted #1, observed at 0.1 s)
SOE: DIVERGES - the predicted cascade does not reproduce the alarm log.
[exit 3]
```

The divergences print first, because they are what to investigate: `PredictedNotObserved` for a predicted trip that is
missing from the log, `ObservedNotPredicted` for a logged trip the model did not produce, and `OrderMismatch` where both
exist in a different sequence. A divergence exits 3.

### Grading variations

A tighter CTI, a mix of curve families and a definite-time backup:

```bash
gridsim grade --chain ieee-vi:200:0.5,iec-ei:200:0.3,dt:200:1.5 --current 1600 --cti 0.2
```

```
$ gridsim grade --chain ieee-vi:200:0.5,iec-ei:200:0.3,dt:200:1.5 --current 1600 --cti 0.2
Overcurrent grading  -  3 relays (downstream first), CTI 0.2 s
  R1: IeeeVeryInverse, pickup 200, time dial 0.5
  R2: IecExtremelyInverse, pickup 200, time dial 0.3
  R3: DefiniteTime, pickup 200, delay 1.5 s

  fault current 1600
  | pair | primary s | backup s | margin s | verdict |
  |------|-----------|----------|----------|---------|
  | R1->R2 | 0.4011 | 0.3810 | -0.0202 | NOT COORDINATED (margin below CTI) |
  | R2->R3 | 0.3810 | 1.5000 | 1.1190 | OK |

NOT GRADED: at least one pair is not coordinated (see above).
[exit 3]
```

Relay specifications are `CURVE:PICKUP:TIMEDIAL`, with the curves `iec-si`, `iec-vi`, `iec-ei`, `iec-lti` (IEC 60255-151,
time multiplier) and `ieee-mi`, `ieee-vi`, `ieee-ei` (IEEE C37.112, time dial), or `dt:PICKUP:DELAY_S` for definite time.
The IEEE very-inverse primary operates in 0.4011 s and the IEC extremely-inverse backup in 0.3810 s: the backup is faster
than its primary at 1600 A, so the margin is negative (-0.0202 s), the pair is not coordinated and the exit code is 3.
The pair R2->R3 passes with 1.1190 s. The curve constants are in
[overcurrent coordination](<../technical/0026 Overcurrent coordination (IDMT grading).md>).

Pickup and current share one unit: use amps or per unit, not both.

### Protection stages: remove one, change one

A case folder is an ordinary directory, so a variant is a copy with `protections.json` or `asset-classes.json` edited.
Copy `data/gb-synthetic` to `results/protection-user/lom-only` and `results/protection-user/lfdd-only`, and give each the
single stage it is named for. The LFDD copy holds:

```json
[
  { "name": "system-lfdd-1", "class": "prot-lfdd-stage1", "monitorBus": 1, "capacityMw": 632 }
]
```

```bash
gridsim trip results/protection-user/lom-only --asset-dynamics
gridsim trip results/protection-user/lfdd-only --asset-dynamics
```

```
$ gridsim trip results/protection-user/lom-only --asset-dynamics
Asset dynamics: on  (8 class(es), 1 protection(s), 1 unit(s) tripped, 800 MW lost to LoM)
...
Peak RoCoF:     -0.576 Hz/s  (largest |df/dt| over the run)
Nadir:          49.052 Hz  (t+5.5 s)
Settling:       49.525 Hz
...
[exit 0]
```

```
$ gridsim trip results/protection-user/lfdd-only --asset-dynamics
Asset dynamics: on  (8 class(es), 1 protection(s), 0 unit(s) tripped, 0 MW lost to LoM)
...
Peak RoCoF:     -0.450 Hz/s  (largest |df/dt| over the run)
Nadir:          49.480 Hz  (t+4.2 s)
Settling:       49.762 Hz
...
[exit 0]
```

With only the loss-of-mains stage, 800 MW trips and the nadir is 49.052 Hz, the same as the two-stage run: the LFDD stage
never fired there, because the nadir stayed above its 48.8 Hz setting. With only the LFDD stage nothing trips (the header
reads `0 unit(s) tripped`) and the nadir is 49.480 Hz, again above 48.8 Hz, so the stage is idle. In both cases the
fleet's converter response lifts the nadir from the 48.334 Hz of the plain run.

To see a stage act, raise its setting. Copy `lfdd-only` to `results/protection-user/lfdd-49p6` and change `lfddStageHz`
in its `prot-lfdd-stage1` class from 48.8 to 49.6:

```bash
gridsim trip results/protection-user/lfdd-49p6 --asset-dynamics
```

```
$ gridsim trip results/protection-user/lfdd-49p6 --asset-dynamics
Asset dynamics: on  (8 class(es), 1 protection(s), 0 unit(s) tripped, 0 MW lost to LoM)
GridSim  -  contingency on GB synthetic
================================================================
Tripped:        Hinkley Point Nuclear  (2880 MW lost, nuclear)
System inertia: 171.7 -> 158.9 GVAs after the trip
Peak RoCoF:     -0.450 Hz/s  (largest |df/dt| over the run)
Nadir:          49.595 Hz  (t+1.6 s)
Settling:       49.951 Hz

50.05 Hz |***-.    ..,,::--==+++++++++++++++++++++++++++++++++++++++++++++| 49.59 Hz (nadir)

...at HALF the inertia (79.5 GVAs): RoCoF -0.906 Hz/s, nadir 47.872 Hz
[exit 0]
```

The header still reads `0 unit(s) tripped`, because a shed is not a trip, but the report shows it: the 632 MW block
leaves at 49.6 Hz, the nadir is 49.595 Hz and the frequency settles at 49.951 Hz.

## Pitfalls

- **`fault` is not the equipment duty.** It superposes the fault on the solved state with the loads as impedances; the
  `iec60909` duty is higher (9.84 pu against 9.26 pu at bus 7 of `case9`). Rate switchgear from `iec60909`, never from
  `fault`.
- **Earth faults end in exit 3 on every MATPOWER case and JSON v1 folder.** A single-line-to-ground or
  double-line-to-ground study needs transformer winding connections, which neither format can hold. The verb prints the
  result and then says why it is not one:

  ```
  $ gridsim fault case14 --bus 4 --type slg
  Single line to ground (phase a) fault  -  case14  bus 4  Zf=j0.000 pu
    phase currents: Ia 0.000 pu  Ib 0.000 pu  Ic 0.000 pu
    earth current |3I0|: 0.000 pu   sequence |I1| 0.000  |I2| 0.000  |I0| 0.000 pu
    faulted bus phase voltages: Va 1.018  Vb 1.018  Vc 1.018 pu
    driving-point Z1 0.0166+j0.0803  Z2 0.0166+j0.0803  Z0 infinite (no earth path in the zero sequence)
    the fault bus reaches no earthed neutral in the zero sequence: no earth current can flow.
    (generator X''d synthesised at 0.20 pu where not supplied, X2 = X''d where not supplied; line Z0 = 3 x Z1 where not supplied; loads neglected in the zero sequence; converter units keep their prefault current)
    relays operating on the faulted state: 0 (phase elements on the phase currents and voltages; 50N/51N/21N/46 on the sequence currents)
    | device | function | location | quantity | pickup | time | basis |
    |--------|----------|----------|----------|--------|------|-------|

  PROTECTION SIMULATION: a single line to ground (phase a) fault at bus 4 would drive 0 relay operation(s).
  fault: the model carries no transformer winding connections at all (MATPOWER and JSON v1 cannot hold them), so it has no earthed neutral and no earth-fault current can be computed from it; the result above reflects missing data. Exit 3.
  [exit 3]
  ```

  `protection-check --fault-type slg|dlg` and `iec60909 --type slg|dlg` fail the same way. Line-to-line studies are not
  affected. A JSON v2 case can carry winding connections, X''d and X2 (its fault sequence data), and one that does is
  not refused: its earth faults use its own earth paths and exit 0 ([ch. 2](<0002 Loading networks.md>)).
- **`iec60909` refuses a bus with no base voltage.** `gridsim iec60909 case14 --bus 4` prints a line containing `fault bus 4 has no base
  kV; IEC 60909 needs a nominal voltage.` and exits 1. Use `fault`, which prints per-unit values, or a case that carries
  voltages.
- **An unknown bus exits 2 before any solve,** on `fault` (`error: fault: fault bus 99 is not in case 'IEEE 9-bus
  (WSCC)'.`), `iec60909` and `protection-check`.
- **The closing line of `fault` names the primary clearance:** a differential (87) element when one operates, otherwise
  the fastest timed element on a branch. It is not the row with the largest margin; read the table for the full picture.
- **A cascade that collapses still exits 0.** `Cascade propagation` ending in `the network has split / collapsed
  (blackout)` is a result of the what-if, not an error. Only `--alarms` turns a cascade into a pass or a fail.
- **`--cascade` and `--fault-bus` do not combine** (exit 2): the cascade follows the steady state. `--zf` and
  `--fault-type` need `--fault-bus` (exit 2). A negative or non-finite `--zf` exits 2 on both `fault` and
  `protection-check`.
- **`--alarms` needs `--cascade`** (exit 2). A missing file or a malformed line exits 1 with the problem named; the message contains
  `line 3: expected 5 fields (at_s,device,from,to,description), got 3: '0.35,50,28'`.
- **A relay that never operates is a verdict on `grade`, not an error.** A current at or below the primary's pickup reads
  `never` and `NOT COORDINATED (primary does not operate)`. Identical relays read a margin of 0.0000 and fail a positive
  CTI. A backup that never operates reads a margin of `inf` and `NOT COORDINATED (backup does not operate)`: nothing
  would back the primary up. A primary that never operates against a backup that does prints `-inf`, with the verdict
  `NOT COORDINATED (primary does not operate)`. Either way the run ends `NOT GRADED:` and exits 3.
- **The default CTI of `grade` is 0.4 s.** The header always prints the CTI in force. A grade that passes at `--cti 0.2`
  can fail at the default.
- **`--asset-dynamics` needs an asset bundle.** On a case with none (`uk`, the IEEE cases) `trip --asset-dynamics` exits 1
  rather than run a plain trip under an asset-dynamics label.
- **Without `--asset-dynamics` the protections take no part in the run, but `protections.json` is still read.** A
  mistake in it refuses the case (exit 1) with or without the flag.
- **The header counts trips, not sheds.** An LFDD stage that fires leaves the count at 0. Only the nadir and the settling
  frequency show it.
- **Voltage and loading stages are not evaluated in `trip`.** The lumped frequency run solves no network, so it hands
  every asset an unknown voltage and loading (NaN), on which no voltage, LVRT or overload stage operates, whatever its
  setting: an under-voltage stage with `uvTripPu` 1.2 on a 500 MW placement trips nothing, as the same stage at 0.8
  does. When the bundle carries such stages the header is followed by `note: N asset(s) carry voltage or loading trip
  stages (LVRT, under- / over-voltage, overload); this lumped run solves no network, so those stages are not evaluated
  ...`. Voltage and thermal stages are evaluated in the live simulator and the desktop app, which feed the solved state.
- **A mistake in the bundle fails loud, exit 1.** A placement naming an unknown class, a `monitorBus` that is not a bus, or
  a class parameter the stage does not read stops the run and names the file and the problem. A `monitorBus` of 99999:

  ```
  $ gridsim trip results/protection-user/bad-bus --asset-dynamics
  error: cannot load case folder 'results/protection-user/bad-bus': Protection placement is invalid:
    device 'system-lfdd-1' monitors bus 99999, which is not a bus of model 'GB synthetic'
  [exit 1]
  ```

## Limits

- Earth faults (`slg` and `dlg`) need a JSON v2 case that carries winding connections; on a MATPOWER case or a JSON v1
  folder the verbs exit 3.
- The relay assessment is positive-sequence and phasor-based: 81R and 78 need a frequency trajectory and are not evaluated
  on a snapshot or a fault.
- Relay settings are generic and are overridden per run only through the verb's flags; per-branch settings are reachable
  through the Core API and not on the command line.
- The cascade follows the branch elements (49, 50, 51 and 21) one trip per step through steady re-solves; it models no
  dynamics, no redispatch and no voltage or frequency protection.
- `--fault-bus` cannot be combined with `--cascade`.
- `iec60909` models no asynchronous motors, and near a single synchronous source its `Ik` is an upper bound (no case
  carries the saturated reactance the standard's `lambda` needs).
- In the unbalanced study converter-interfaced units add no fault current: the model has no negative- or zero-sequence
  behaviour for them.
- `grade` takes the fault current as an input: no verb couples the per-branch currents of `iec60909 --branches` into a
  grading chain, and fuse and recloser curves are not modelled.
- `trip --asset-dynamics` runs the lumped frequency model, so voltage and thermal protection stages are not evaluated
  (they read an unknown voltage and loading), and the half-inertia comparison line runs without the fleet.

## See also

- [Protections (technical 15)](<../technical/0015 Protections.md>) - the asset protection stages, placement and topology recloser
- [Protection assessment (technical 17)](<../technical/0017 Protection assessment (a passive relay overlay).md>) - the relay library, fault injection and cascade
- [IEC 60909 short-circuit duty (technical 25)](<../technical/0025 IEC 60909 short-circuit duty study.md>) - the duty currents, factors and sequence networks
- [Overcurrent coordination (technical 26)](<../technical/0026 Overcurrent coordination (IDMT grading).md>) - the curve library and grading
- [ch. 19, CLI reference](<0019 CLI reference.md#fault>) - the `fault` verb
- [ch. 19, CLI reference](<0019 CLI reference.md#iec60909>) - the `iec60909` verb
- [ch. 19, CLI reference](<0019 CLI reference.md#protection-check>) - the `protection-check` verb
- [ch. 19, CLI reference](<0019 CLI reference.md#grade>) - the `grade` verb
- [ch. 19, CLI reference](<0019 CLI reference.md#trip>) - `trip` and `--asset-dynamics`
- [ch. 19, CLI reference](<0019 CLI reference.md#result-files>) - writing any of these results to JSON or CSV
- [ch. 5, Running power flows](<0005 Running power flows.md>) - the solve every study starts from
- [ch. 11, Contingency analysis](<0011 Contingency analysis.md>) - the steady-state outage screens
- [ch. 14, Frequency studies](<0014 Frequency studies.md>) - the `trip` report in full
- [ch. 29, Extending GridSim](<0029 Extending GridSim.md>) - authoring a protection class
