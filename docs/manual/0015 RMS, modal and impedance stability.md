# 15. RMS, modal and impedance stability

A stability study asks whether the system stays together, or settles, after something disturbs it. GridSim answers
three different versions of that question with three verbs. `stability` applies a three-phase fault and finds the
longest time the fault can stay on before a machine loses synchronism (the critical clearing time, CCT). `modal`
linearises the machine swing about the solved operating point and reports the frequency and damping of every
electromechanical oscillation. `impedance-stability` puts one converter against the grid impedance and applies the
Nyquist criterion to find out whether the two interact into an unstable oscillation. This chapter shows each on real
output, explains every line it prints, and says which one to run when. `domains` and `h0-study`, the two frequency
verbs that sit next to these in the command list, are covered in
[ch. 14, Frequency studies](<0014 Frequency studies.md>).

## Which study answers which question

| Question | Use | Model |
|---|---|---|
| How long can a three-phase fault last before the fleet loses synchronism? | `gridsim stability` | Classical swing: constant E' behind X'd, Kron-reduced network. |
| The same, with sub-transient machines, exciters, a stabiliser and converter plant. | `gridsim stability --rms` (`--avr`, `--pss`) | Six-state GENROU machines, SEXS or IEEET1 exciters, PSS1A, REGC_A/REEC-style converters. |
| Which generator bus has the least margin? | `gridsim stability --screen` | One CCT per generator bus, worst first. |
| Does a fault cleared after X ms hold synchronism? | `gridsim stability --clear X` | One fault-and-clear run with a STABLE or UNSTABLE verdict. |
| Are the electromechanical oscillations damped, at what frequency, and which machines drive them? | `gridsim modal` | Linearised swing, eigenvalues and participation factors. |
| Does a converter oscillate against a weak grid, and at what short-circuit ratio does it start? | `gridsim impedance-stability` | Generalised Nyquist criterion on the converter-grid loop gain. |
| How does the fleet divide into speed domains, and what happens as synchronous inertia goes to zero? | `domains`, `h0-study` | See [ch. 14](<0014 Frequency studies.md>). |
| What does a fault do to the waveform in the first milliseconds? | `emt` | See [ch. 16, Electromagnetic transients](<0016 Electromagnetic transients (EMT).md>). |

The three verbs are not substitutes. `stability` is a large-disturbance, time-domain answer for one fault location and
one clearing time. `modal` is a small-disturbance answer that holds only at the solved operating point and says nothing
about a fault. Only `impedance-stability` models a converter's PLL and current loop against a network. `modal
--enable-inverters` adds a linear archetype of the droop and PLL loops coupled to the centre of inertia, and `stability`
holds converter plant as a constant admittance (classical) or a current source (`--rms`). A stable `modal` run
does not imply a long CCT, and a long CCT does not imply a damped swing. Run the one whose question you are asking, and
read the others as separate evidence.

## Before you start

`stability` and `modal` need a case with a synchronous machine that has an inertia constant. Four facts about the data:

- **A generator is integrated as a rotor** when its inertia constant `H` is above 0 and it is neither
  converter-interfaced nor power-flow-only. Every other generator is held: it stays in the network as a constant
  admittance fixed by its solved injection. A case with no rotor exits 2.
- **Cases that carry inertia.** `case9` (also `ieee9`), `uk` (also `gb` and `gb-reduced`), `gb-full`, `gb-synthetic`, and
  any JSON case folder whose generators give `inertiaH`. The MATPOWER-derived cases (`case14`, `case30`, `case57`,
  `case118`, `case300`, `case145`, `case24`, `cigre`, `gb-reduced-29`, `gb-network-2224`, `pypsa-gb-reduced`) carry no
  inertia and exit 2 for `stability` and `modal`. The reason is shown with real output under [Pitfalls](#pitfalls).
- **Transient reactance X'd.** `case9` uses the Anderson-Fouad values (bus 1 0.0608, bus 2 0.1198, bus 3 0.1813 pu on
  the machine base). Every other case uses a synthetic 0.30 pu, and the output says so (`X'd = synthetic 0.30 pu
  (unvalidated)`). A result built on the synthetic value is a demonstration of the method, not a study of the network.
  A machine dynamic data file (`--dyn-data`, see [Machine data from a file](#machine-data-from-a-file)) replaces the
  value for the machines it binds.
- **GENROU data for `--rms`.** No built-in case carries sub-transient data, so without `--dyn-data` the RMS engine
  synthesises each machine from X'd and the case's `H`: `Xd = 3.0 X'd`, `X''d = 0.8 X'd`, `Xq = 2.5 X'd`,
  `X'q = 1.5 X'd`, `X''q = 0.8 X'd`, `T'do = 8.0 s`, `T''do = 0.03 s`, `T'qo = 0.4 s`, `T''qo = 0.05 s`, no damping.
  Such a result is labelled INDICATIVE, including on `case9`.
- **Nominal frequency.** Every engine integrates at the case's own nominal frequency. `case9` is the 60 Hz WSCC system
  and runs at 60 Hz; the other bundled cases are 50 Hz.

`impedance-stability` needs no inertia: it works on any case with a generator to attach the converter to. Case
selection, `--dir` and the case-argument rules are in [ch. 2, Loading networks](<0002 Loading networks.md>).

Wall-clock times printed in a capture (the `0.1 s` of a `--clear` run, the `3.7 s` of the GB run) differ by machine
and by run. Every other number in this chapter is the same on every repeated run on one machine. The classical swing kernel
sums the electrical power in a vector-width-dependent order, so for 4 or more machines (`uk` has 13), or for 3 on a CPU
with 2-double vectors, its trajectories can
differ in the last bits between CPU tiers, and no test pins that. A CCT is an integer step count, so it changes only if a
trial sits within rounding of the 2 pi slip limit. The full statement is the Determinism section of
[technical ch. 30](<../technical/0030 Small-signal and impedance stability.md>).

## Worked example

### stability: the critical clearing time

Find how long a three-phase fault at bus 7 of the WSCC 9-bus case can last before the generators pull out of step:

```bash
gridsim stability case9
```

```
$ gridsim stability case9
GridSim  -  TRANSIENT STABILITY (classical multi-machine)  *  WSCC-9
==================================================================
3 machines (0 converter / power-flow-only / zero-inertia units held)  *  fault bus 7  *  X'd = Anderson-Fouad

  Bus   H sys (s)   |E'| pu  delta0 (deg)
    1      58.509     1.047           0.9
    2      12.288     1.034          14.8
    3       3.853     1.017          11.3

Critical clearing time for a 3-phase fault at bus 7:  550 ms
(the longest the fault can persist before the fleet loses synchronism on self-clear)
  (bracket: clearing at 550 ms holds synchronism, at 552 ms it is lost)
  NON-MONOTONE: clearing at 558 ms holds synchronism again (a stable pocket above the first loss); the reported CCT is the first loss from below, so do not read a longer clearing time as safe.
CCT = longest clearing time on the 2 ms integration-step grid that keeps synchronism (clearing one step, 2 ms, later loses it); resolution 2 ms.
A CCT of 0 ms means even an instantaneous clear loses synchronism; 1000 ms means no clearing time in the 1000 ms search window loses it.
It is the first loss from below: the 16 steps (32 ms) below it are checked to hold and as many above it are probed; NON-MONOTONE marks a later clearing time that holds again (a stable pocket).
Each trial is judged over the 5 s after the fault clears (the documented default window: first-swing stability, the range in which the classical constant-E' model holds); an undamped system can lose synchronism on a later swing, so a longer window can give a shorter CCT.

Classical model: constant E' behind X'd over the Kron-reduced network, RK4 swing.
[exit 0]
```

Bus 7 carries no generator, so this is a fault on a load bus. The longest clearing time that keeps the three machines
in synchronism is 550 ms; clearing at 552 ms loses it. The exit code is 0 because the study ran, whatever the
margin.

To rank every generator bus at once, add `--screen`:

```bash
gridsim stability case9 --screen
```

```
$ gridsim stability case9 --screen
GridSim  -  TRANSIENT STABILITY SCREEN (classical multi-machine)  *  WSCC-9
==================================================================
3 machines (0 converter / power-flow-only / zero-inertia units held)  *  3 generator-bus faults  *  X'd = Anderson-Fouad

 Rank  Fault bus   CCT (ms)
    1          3        302   <- most critical
    2          2        312   NON-MONOTONE: holds again at 320 ms
    3          1        498   NON-MONOTONE: holds again at 502 ms

Self-clearing 3-phase fault at each generator bus; lower critical clearing time = less margin.
CCT = longest clearing time on the 2 ms integration-step grid that keeps synchronism (clearing one step, 2 ms, later loses it); resolution 2 ms.
A CCT of 0 ms means even an instantaneous clear loses synchronism; 1000 ms means no clearing time in the 1000 ms search window loses it.
It is the first loss from below: the 16 steps (32 ms) below it are checked to hold and as many above it are probed; NON-MONOTONE marks a later clearing time that holds again (a stable pocket).
Each trial is judged over the 5 s after the fault clears (the documented default window: first-swing stability, the range in which the classical constant-E' model holds); an undamped system can lose synchronism on a later swing, so a longer window can give a shorter CCT.
[exit 0]
```

Bus 3, with the smallest inertia (3.853 s on the system base), has the least margin at 302 ms. Bus 2 shows the
`NON-MONOTONE` marker, which is explained next, because it changes how the number must be read.

A single fault at bus 2 shows the same case in full:

```bash
gridsim stability case9 --fault-bus 2
```

```
$ gridsim stability case9 --fault-bus 2
...
3 machines (0 converter / power-flow-only / zero-inertia units held)  *  fault bus 2  *  X'd = Anderson-Fouad
...
Critical clearing time for a 3-phase fault at bus 2:  312 ms
(the longest the fault can persist before the fleet loses synchronism on self-clear)
  (bracket: clearing at 312 ms holds synchronism, at 314 ms it is lost)
  NON-MONOTONE: clearing at 320 ms holds synchronism again (a stable pocket above the first loss); the reported CCT is the first loss from below, so do not read a longer clearing time as safe.
```

The CCT is 312 ms, and the next step up, 314 ms, loses synchronism. A fault cleared at 320 ms holds synchronism
again. The three single-clearing runs below, from `--clear`, are at 342, 344 and 350 ms, above that pocket:

```bash
gridsim stability case9 --fault-bus 2 --clear 342
gridsim stability case9 --fault-bus 2 --clear 344
gridsim stability case9 --fault-bus 2 --clear 350
```

```
$ gridsim stability case9 --fault-bus 2 --clear 342
...
3-phase fault at bus 2, cleared after 342 ms  ->  UNSTABLE (loss of synchronism)
```
```
$ gridsim stability case9 --fault-bus 2 --clear 344
...
3-phase fault at bus 2, cleared after 344 ms  ->  UNSTABLE (loss of synchronism)
```
```
$ gridsim stability case9 --fault-bus 2 --clear 350
...
3-phase fault at bus 2, cleared after 350 ms  ->  UNSTABLE (loss of synchronism)
```

All three lose synchronism. The reported 312 ms is the first loss from below: every clearing time up to it holds, and
314 ms is the first that does not. The marker warns that stability is not monotone in the clearing time at this bus, so
a clearing time above the CCT is not reliably unstable (320 ms holds), and a clearing time that happens to hold above it
is not a safe design point.

### stability --rms: sub-transient machines

The same question on the six-state machine model:

```bash
gridsim stability case9 --rms
```

```
$ gridsim stability case9 --rms
GridSim  -  RMS TRANSIENT STABILITY (salient/subtransient GENROU)  *  WSCC-9
==================================================================
3 machines  *  6-state machines  *  field: held (no AVR)  *  GENROU params synthesised from X'd

fault bus 7  *  critical clearing time = 554 ms
  (bracket: clearing at 554 ms holds synchronism, at 556 ms it is lost)
CCT = longest clearing time on the 2 ms integration-step grid that keeps synchronism (clearing one step, 2 ms, later loses it); resolution 2 ms.
A CCT of 0 ms means even an instantaneous clear loses synchronism; 1000 ms means no clearing time in the 1000 ms search window loses it.
It is the first loss from below: the 16 steps (32 ms) below it are checked to hold and as many above it are probed; NON-MONOTONE marks a later clearing time that holds again (a stable pocket).
Each trial is judged over the 5 s after the fault clears (the documented default window: first-swing stability, the same window as the classical search); an undamped system can lose synchronism on a later swing, so a longer window can give a shorter CCT.

Self-clearing 3-phase fault; the subtransient engine is validated (reduces to the classical CCT),
but these GENROU parameters are synthesised from X'd - the result is INDICATIVE, not validated.
[exit 0]
```

At bus 7 the RMS engine gives 554 ms against the classical 550 ms. The difference comes from the machine model:
sub-transient reactances and flux decay instead of a constant E' behind X'd. The footer states that the numbers are
INDICATIVE because the GENROU parameters are synthesised from X'd.

### modal: electromechanical modes

```bash
gridsim modal case9
```

```
$ gridsim modal case9
GridSim  -  SMALL-SIGNAL / MODAL STABILITY  *  WSCC-9
==================================================================
3 machine(s), 6 state(s)  *  X'd = Anderson-Fouad  *  inverters off  *  damping D = 0 (undamped)

   sigma (1/s)  omega (rad/s)    f (Hz)      zeta   mode
        0.0000       -12.4975    1.9890   -0.0000   oscillatory
        0.0000        -6.9578    1.1074   -0.0000   oscillatory
        0.0000         6.9578    1.1074   -0.0000   oscillatory
        0.0000        12.4975    1.9890   -0.0000   oscillatory
        0.0000         0.0000    0.0000    0.0000   real
        0.0000         0.0000    0.0000    0.0000   real

Least-damped oscillatory mode: 1.989 Hz, zeta = -0.0000  (poorly damped)
Right-half-plane eigenvalues (small-signal unstable): 0
Minimum oscillatory damping ratio: -0.0000

Dominant participation (which state drives each oscillatory mode):
    1.989 Hz (zeta -0.0000) : d.delta_gen3 45%, d.omega_gen3 45%, d.delta_gen2 5%
    1.107 Hz (zeta -0.0000) : d.delta_gen2 45%, d.omega_gen2 45%, d.delta_gen3 5%
    1.107 Hz (zeta -0.0000) : d.omega_gen2 45%, d.delta_gen2 45%, d.delta_gen3 5%
    1.989 Hz (zeta -0.0000) : d.omega_gen3 45%, d.delta_gen3 45%, d.omega_gen2 5%

s-plane (imag up, real right; '*' stable pole, 'X' RHP/unstable, left of axis = damped):
                         |                         
                         *                         
                         |                         
                         |                         
                         |                         
                         *                         
                         |                         
                         |                         
                         |                         
                         |                         
-------------------------*-------------------------
                         |                         
                         |                         
                         |                         
                         |                         
                         *                         
                         |                         
                         |                         
                         |                         
                         *                         
                         |

Eigenvalues of A = [[0, ws I],[-M^-1 K_L, -M^-1 D]] (true asymmetric K_L; Francis-QR, biorthonormal participation).
[exit 0]
```

The case has three machines and six states. There are two oscillatory pairs, at 1.107 Hz and 1.989 Hz, and two zero
eigenvalues that belong to the rigid-body rotation of the whole system. Nothing damps the swing (`damping D = 0`), so
every oscillatory eigenvalue lies exactly on the imaginary axis and the right-half-plane count is 0. Exit code 0.

### impedance-stability: a converter against the grid

```bash
gridsim impedance-stability case9
```

```
$ gridsim impedance-stability case9
impedance-stability: case IEEE 9-bus (WSCC), inverter bus 2, mode GridFollowingInverter, detail PllOuter, criterion Mimo
  operating point : Vd0=1.0250 pu, Id0=0.8283, Iq0=-0.0338 pu
  nyquist         : encirclements 0  unstable-modes 0  GM inf dB  PM inf deg  minDist 0.874  -> STABLE
  SCR boundary    : none in [1.0, 10.0]  (stable across the swept SCR range)
  cross-check (C) : RHP eigenvalues 0  agreement OK  [swing model: X'd Anderson-Fouad, zero damping (no damping data)]
  provenance      : CuratedPlaceholder [VERIFY] pll/current-loop params literature-typical
[exit 0]
```

The verb attaches a grid-following converter to the bus of the generator with the largest scheduled `Pg` (bus 2, 163 MW), builds its small-signal
admittance from the solved operating point, forms the loop gain against a Thevenin grid at short-circuit ratio 3, and
counts encirclements of the critical point with the generalised (MIMO) criterion, the default for a grid-following
converter. Zero encirclements, a minimum distance of 0.874 to the critical point and no critical short-circuit ratio
anywhere in the swept range 1 to 10 give a STABLE verdict.

## Reading the output

### stability, classical

**Header.** `GridSim  -  TRANSIENT STABILITY (classical multi-machine)  *  WSCC-9` names the engine and the case. The
label is `WSCC-9` for `case9` and `ieee9`, the model's own name for any other case, and the folder text for `--dir`;
a user case whose label would be `WSCC-9` is labelled `WSCC-9 (user case)`, because the Anderson-Fouad machine data
goes to the built-in case only (GR-684).
The `=` rule closes the header.

**Machine line.** `3 machines (0 converter / power-flow-only / zero-inertia units held)  *  fault bus 7  *  X'd =
Anderson-Fouad`. The first number counts the generators integrated as rotors. The parenthesis counts the generators
that were held instead (see [Before you start](#before-you-start)); on `uk` it reads `13 machines (16 converter /
power-flow-only / zero-inertia units held)`. The fault bus is `--fault-bus`, else 7 on `case9`, else the first bus in
model order that has no generator, else the first bus of the model. The last field is `Anderson-Fouad` or `synthetic 0.30 pu (unvalidated)`.

**Machine table.** One row per integrated machine, in generator order. A bus with two machines appears twice.

| Column | Meaning |
|---|---|
| `Bus` | The machine's terminal bus. |
| `H sys (s)` | Inertia constant on the system base: `H x MBase / BaseMva`. Bus 1 of `case9` is 23.64 s on 247.5 MVA at 100 MVA, which is 58.509 s. |
| `|E'| pu` | Magnitude of the internal EMF `E' = Vt + j X'd It`, with X'd moved to the system base and `It` from the solved generator output. It is held constant through the run. |
| `delta0 (deg)` | Angle of `E'` in the power-flow angle frame (slack bus at 0 degrees). It is the rotor angle the swing starts from. |

The mechanical power of each machine is set to its electrical output at `delta0`, so a run with no fault stays at
rest.

**Verdict block.** `Critical clearing time for a 3-phase fault at bus 7:  550 ms` is the CCT. The next line gives
the bracket, `clearing at 550 ms holds synchronism, at 552 ms it is lost`, which are two adjacent points on the 2 ms
step grid. Two other lines can replace the bracket, and a `NON-MONOTONE` line can follow it:

- `(even an instantaneous clear loses synchronism)`: the CCT is 0 ms; the fault loses synchronism even when cleared at
  once.
- `(no clearing time up to the 1000 ms search window loses synchronism)`: the CCT is 1000 ms; nothing in the window
  loses it.
- A `NON-MONOTONE: clearing at N ms holds synchronism again ...` line follows the bracket when the search found a stable
  pocket (see [How the CCT is found](#how-the-cct-is-found)).

The three lines beginning `CCT = longest clearing time ...` say what the number is: the longest clearing time on the
2 ms integration-step grid that keeps synchronism, with one step (2 ms) more losing it. A CCT of 0 ms and one of
1000 ms are the two ends of the search. The third line states that the CCT is the first loss from below, that the 16
steps (32 ms) under it were checked to hold, and that as many above it were probed for a stable pocket.

**Footer.** `Classical model: constant E' behind X'd over the Kron-reduced network, RK4 swing.` A case with synthetic
X'd adds `Provenance: X'd is a synthetic default - clearing times are INDICATIVE, not validated.`

**With `--clear`.** The machine table is followed by the verdict line,
`3-phase fault at bus 7, cleared after 400 ms  ->  STABLE (holds synchronism)`, or `UNSTABLE (loss of synchronism)`.
There is no bracket and no search; this is one run. A second line scores physics law 9 from the run's own rotor-angle
trajectory: `law 9 (synchronism, scored from this run): HOLDS` or `VIOLATED`, then the largest rotor angle from the
centre of inertia against the 180 degree out-of-step criterion of PSS/E and PowerFactory, and the peak speed deviation
(reported, not scored). The verdict itself is the integrator's pole-slip test at 360 degrees; when a machine passes 180
but not 360 degrees a `note:` line says the two criteria differ. `--clear` takes 0 to 1000 ms; a negative, non-finite
or larger value exits 2.

```
$ gridsim stability case9 --clear 400
...
3-phase fault at bus 7, cleared after 400 ms  ->  STABLE (holds synchronism)
```

#### The model

The classical study is built in five steps, all on the system base.

1. Each load becomes a constant shunt admittance `y = conj(S) / |V|^2` at its bus, with `V` the solved voltage.
2. Each integrated machine becomes an internal node behind `jX'd`. Each held generator becomes a constant admittance
   from its solved injection.
3. The network is Kron-reduced onto the internal nodes. The fault is a `1e7` pu shunt to ground at the fault bus,
   reduced the same way.
4. Each machine follows the swing equation, integrated with RK4 at 2 ms:

```
d(delta_i)/dt   = ws * omega_i                       ws = 2 pi f0
2 H_i d(omega_i)/dt = Pm_i - Pe_i(delta)             no damping, no governor, no exciter
Pe_i = |E_i|^2 G_ii + sum_j |E_i||E_j| (G_ij cos(d_ij) + B_ij sin(d_ij))      d_ij = delta_i - delta_j
```

5. The fault starts at 0.1 s and is cleared after the clearing time. The faulted network applies between those two
   instants, the healthy one before and after. A trial runs for 5 s after clearing.

A trial **loses synchronism** when, at any step, any rotor angle is more than 2 pi rad away from the inertia-weighted
centre-of-inertia angle, or a state stops being finite. A swing that separates only after more than 5 s past clearing
is not seen.

### stability --screen

The screen runs the CCT search for a fault at every distinct generator bus and prints one row per bus, worst first.

| Column | Meaning |
|---|---|
| `Rank` | 1 is the lowest CCT and carries `<- most critical`. |
| `Fault bus` | The generator bus the fault is applied at. |
| `CCT (ms)` | The CCT on the 2 ms grid. |
| (suffix) | `NON-MONOTONE: holds again at N ms` when that bus's search found a stable pocket at N ms. |

The header line counts the integrated machines and the units held as constant injections, in the single-bus run's
wording, then the distinct generator buses: on `uk` it reads `13 machines (16 converter / power-flow-only /
zero-inertia units held)  *  10 generator-bus faults`. `--screen` has no single fault bus and no fixed clearing time, so
with `--fault-bus`, `--clear` or `--export` it exits 2 before any work, naming the flag. With `--rms` the same screen
runs on the RMS engine.

### How the CCT is found

Both engines share one search over whole integration steps (`CctSearch`, `src/GridSim.Core/Dynamics/Models/CctSearch.cs`).
A trial with `m` steps holds the fault for exactly `m x 2 ms`. The window is 1000 ms, which is 500 steps. The search
has three stages and never simulates a step count twice.

1. **Bisection.** It tests step 0, then step 500, then halves until the longest stable step count and the shortest
   unstable one are adjacent. For a case where stability is monotone in the clearing time, this stage alone is the
   answer.
2. **Downward verification.** It simulates the 16 step counts below the stable end. If one of them loses synchronism,
   the bisection landed on a stable pocket above an earlier loss, and the search bisects again below that loss. After 4
   such rounds it stops guessing and walks up from step 1 to the first loss.
3. **Upward probe.** It simulates the 16 step counts above the first loss. Any that holds synchronism is a stable pocket;
   the CCT does not move, but the result carries the pocket and the output prints the `NON-MONOTONE` marker. The marker
   names the highest clearing time in the probe window that held.

The reported CCT is the first loss from below provided no stable pocket is wider than 16 steps (32 ms). A monotone case
costs at most 32 simulations more than the bisection alone. The `--rms` runs use the same single search per bus: its
stable end is the CCT and the same search gives the pocket flag.

On `case9` bus 2 the search reports a pocket: 312 ms holds, 314 ms loses synchronism, and 320 ms holds (the
`--fault-bus 2` capture above).

### stability --rms

**Header.** `RMS TRANSIENT STABILITY (salient/subtransient GENROU)` names the engine.

**Fleet line.** `3 machines  *  6-state machines  *  field: held (no AVR)  *  GENROU params synthesised from X'd`. Each
machine has six states: rotor angle, speed, `E'q`, `E'd`, and the `q`- and `d`-axis sub-transient fluxes. The field
entry is `held (no AVR)`, `SEXS`, `IEEET1`, or the exciter name followed by ` + PSS1A`.

Two more lines appear when the case needs them.

- `16 converter-interfaced units  *  REGC_A/REEC-style current sources (K=2, reactive priority, LVACM)`: converter plant
  is not a machine; it enters as a current source on a retained bus with a voltage-dip mode.
- `1 WECC renewable unit(s) from the dynamics file:` then one line per unit, for example
  `PV1 @ bus 2: REGC_A + REEC_A + REPC_A  (PSS/E DYR plant.dyr, line(s) 9, 10, 13)`: a unit whose `--dyn-data` DYR
  file gives it a WECC chain (REGC_A with REEC_A, REEC_B or REEC_C, and optionally the REPC_A plant controller) runs on
  that chain instead (technical ch. 24); it is counted in neither the machines nor the line above.
- `1072 power-flow-only + 1 zero-inertia units held as constant injections`: units that have no rotor to integrate.

**Result lines.** For a single fault, `fault bus 7  *  critical clearing time = 554 ms`, then the bracket line (or the 0 ms
or 1000 ms variants), then a `NON-MONOTONE` line if the search found a pocket, then the three `CCT = ...` lines. They mean
exactly what they mean for the classical engine; the step is 2 ms, the window 1000 ms, and each trial runs for 5 s after
clearing. The footer repeats that the engine is validated (with every reactance collapsed to X'd it matches the
classical CCT to within 20 ms on WSCC-9, the tolerance of its pinning test) and that these machine parameters are not.

**With `--screen`** the lines are the ranked table described above, under the same header.

**With `--clear`** the run is one fault-and-clear study, not a search:

```
$ gridsim stability case9 --rms --clear 400
GridSim  -  RMS TRANSIENT STABILITY (salient/subtransient GENROU)  *  WSCC-9
==================================================================
3 machines  *  6-state machines  *  field: held (no AVR)  *  GENROU params synthesised from X'd

fault bus 7  *  cleared after 400 ms  *  STABLE (fleet stays in synchronism)  *  0.1 s
...
```

`fault bus 7  *  cleared after 400 ms  *  STABLE (fleet stays in synchronism)  *  0.1 s` ends with the wall time of the
run. This single study integrates with a 5 ms step for 3 s, a different grid and window from the CCT search (2 ms, 5 s past
clearing). A clearing time right at the CCT can therefore give a different verdict here than the search gave. The `law 9`
line of the classical `--clear` run follows the verdict, scored over the machines the RMS verdict judges. A case with
converter plant adds a converter line:

```
$ gridsim stability uk --rms --clear 150
GridSim  -  RMS TRANSIENT STABILITY (salient/subtransient GENROU)  *  GB reduced (10-zone)
==================================================================
13 machines  *  6-state machines  *  field: held (no AVR)  *  GENROU params synthesised from X'd
16 converter-interfaced units  *  REGC_A/REEC-style current sources (K=2, reactive priority, LVACM)

fault bus 101  *  cleared after 150 ms  *  STABLE (fleet stays in synchronism)  *  0.2 s
converters: 10 nodes, 9 in fault ride-through mid-fault, 10 recovered by 3 s; active power 15305 MW before, 15306 MW at 3 s
...
```

`converters: 10 nodes, 9 in fault ride-through mid-fault, 10 recovered by 3 s; active power 15305 MW before, 15306 MW at
3 s`. The count of converter nodes in fault ride-through is taken half a clearing time after the fault starts. Recovered
means out of the dip mode at the end of the 3 s run. The two power figures are the fleet's active output at the start and
at the end.

### modal

```
GridSim  -  SMALL-SIGNAL / MODAL STABILITY  *  WSCC-9
3 machine(s), 6 state(s)  *  X'd = Anderson-Fouad  *  inverters off  *  damping D = 0 (undamped)
```

**Header and model line.** The model line gives the machine count, the state count (two per machine, plus the inverter states
with `--enable-inverters`), the X'd source, `inverters off` or `ON`, and the damping `D`: `0 (undamped)` or the
`--damping` value to three decimals.

**The model.** The states are, for each machine, the rotor-angle deviation `d.delta_gen<bus>` and the speed deviation
`d.omega_gen<bus>`. The state matrix is

```
A = [   0        ws I  ]        ws = 2 pi f0
    [ -M^-1 K_L  -M^-1 D ]      M = diag(2 H_i) on the system base,  D = diag(D_i)

K_L[i,j] = -K[i,j] (i != j),  K_L[i,i] = sum over j != i of K[i,j]
K[i,j]   = |E_i||E_j| (B_ij cos(d_ij0) - G_ij sin(d_ij0))     from the same Kron-reduced network as stability
```

`K_L` keeps the transfer conductances, so it is not symmetric; that is why the eigenvalue solver is a general (Francis
QR) one. `D` is the same for every machine, in per-unit power on the system base per per-unit speed. The row sums of
`K_L` are zero, which is what makes the common rotation of all machines a free (zero) mode.

**Eigenvalue table.** One row per eigenvalue of `A`.

| Column | Meaning |
|---|---|
| `sigma (1/s)` | Real part. Negative is damped; positive is growing. |
| `omega (rad/s)` | Imaginary part. Oscillatory modes come in conjugate pairs, so both signs appear. |
| `f (Hz)` | `abs(omega) / 2 pi`. |
| `zeta` | Damping ratio `-sigma / abs(s)`, defined as 0 when `abs(s)` is at most `1e-12`. |
| `mode` | `oscillatory` when `abs(omega)` is above `1e-9` rad/s, otherwise `real`. |

The rows are sorted: oscillatory modes first, lowest `zeta` first, then the real modes by ascending `sigma`. In the
undamped capture every `sigma` is exactly zero. The `-0.0000` printed under `zeta` is a zero with a minus sign (it is
`-sigma / abs(s)` with `sigma = 0`), not a small negative damping; the export writes it as 0. The two `real` rows are the
rigid-body mode of the whole system.

**Summary lines.**

- `Least-damped oscillatory mode: 1.989 Hz, zeta = -0.0000  (poorly damped)` is the first oscillatory row. The tag is
  `(UNSTABLE)` when its `zeta` is below 0, `(poorly damped)` when it is below 0.05, and absent otherwise. An undamped run on `case9` reads `poorly damped`
  because its `zeta` is exactly zero.
- `Right-half-plane eigenvalues (small-signal unstable): 0` counts eigenvalues with `sigma` above `1e-9`.
- `Minimum oscillatory damping ratio` is the lowest `zeta` over the oscillatory modes, or `n/a` when there is none.

**Participation.** `Dominant participation (which state drives each oscillatory mode)` prints, for each oscillatory mode,
the three states with the largest participation factor. The factor of state `k` in a mode is

```
p_k = |w_k| |v_k| / sum_k( |w_k| |v_k| )      v = right eigenvector, w = left eigenvector, sum over states = 1
```

and the line shows it as a percentage; states below `1e-6` are dropped. Only the first six oscillatory rows are printed,
and a conjugate pair prints twice. In the capture the 1.107 Hz mode is machine 2 (bus 2) swinging against the rest, with
45 % on its angle and 45 % on its speed, and the 1.989 Hz mode is machine 3, the lightest. Machine 1 never appears in the
undamped run because the undamped path removes the rigid-body mode first, by measuring every angle and speed against one
reference machine (bus 1 on `case9`); the reference machine's states carry zero participation by construction. The damped
run below includes machine 1.

**s-plane.** A 51-column by 21-row character plot. The vertical axis is `omega`, the horizontal axis `sigma`, the centre is
the origin, and the scales are set by the largest `abs(sigma)` and `abs(omega)` among the modes. `*` marks a stable pole and
`X` a pole with `sigma` above `1e-9`. With `sigma` zero for every pole, all the stars sit on the vertical axis.

**Footer.** `Eigenvalues of A = [[0, ws I],[-M^-1 K_L, -M^-1 D]] (true asymmetric K_L; Francis-QR, biorthonormal
participation).` Further lines follow when they apply: `Provenance: X'd is a synthetic default - damping/participation
are INDICATIVE, not validated.` for a synthetic X'd, and `Inverter states are a documented archetype ...` when inverters
are on.

**Exit code.** 1 when the right-half-plane count is above 0, 3 for a power flow that does not converge, 2 for a case that
cannot be assembled. An unstable result is still a complete analysis: the tables and any `--export` are written.

### impedance-stability

**Header line.** `impedance-stability: case IEEE 9-bus (WSCC), inverter bus 2, mode GridFollowingInverter, detail
PllOuter, criterion Mimo`.

- `inverter bus` is `--bus`, or the bus of the generator with the largest scheduled `Pg`.
- `mode` is `GridFollowingInverter` for `--converter gfl` (the default) and `GridFormingInverter` for `gfm`.
- `detail` is `PllOuter` by default, `PllCurrentOuter` when `--current-bw` is given, and `OuterLoopOnly` with
  `--outer-loop-only`. A grid-forming model always prints `PllOuter`; its admittance does not use the detail level.
- `criterion` is `Siso` or `Mimo`. With `--criterion auto` (the default) it is `Mimo` for a grid-following converter
  and `Siso` for a grid-forming one and for `--outer-loop-only`; `--criterion siso` or `mimo` forces either, except that
  `mimo` with `--outer-loop-only` exits 2 (see the loop below).

**Operating point.** `Vd0=1.0250 pu, Id0=0.8283, Iq0=-0.0338 pu`. `Vd0` is the solved voltage magnitude at the converter
bus, with a floor of 0.05 pu; a floored value prints `(CLAMPED - weak/depressed)`. `Id0 = P / Vd0` and `Iq0 = -Q / Vd0`, in
per-unit on the converter's own MVA rating, from the unit's solved output: the bus's solved generation, split between
co-located units by scheduled P and by reactive range. On `case9` bus 2, P is 163 MW on a 192 MVA rating, which is
0.8490 pu, and divided by 1.0250 pu gives 0.8283.

**The loop.** The scan forms the loop gain from the grid impedance and the converter admittance, both on the system base
(the converter admittance is multiplied by its rating over the system base, 1.92 for bus 2 of `case9`). The converter's
admittance is a dq-frame quantity: at dq perturbation frequency f it is the admittance at stationary frequency f + f0 in the
positive sequence and f - f0 in the negative sequence. The SISO criterion therefore pairs each sequence with
the grid impedance at the same stationary frequency, `L_pp(f) = Z_grid(f + f0) * Y_pp(f)` and
`L_nn(f) = Z_grid(f - f0) * Y_nn(f)`, and neglects the coupling between the two sequences. The MIMO criterion pairs the full
2x2 dq matrices and keeps that coupling.

Which one decides depends on the converter, and `--criterion auto` (the default) picks it:

- **Grid-following, PLL modelled: MIMO.** The PLL acts on the q axis only, so the admittance couples the two sequences and
  SISO can give the wrong verdict. Checked against an independent Routh-Hurwitz count of the closed-loop right-half-plane
  roots on `case9` bus 2: with an 80 Hz PLL, SISO is wrong at seven of ten swept SCRs (UNSTABLE at 0.1 to 0.375, STABLE at
  0.925 to 1.475) and MIMO agrees at all ten; with a 300 Hz PLL, SISO calls SCR 3 STABLE where the closed loop has a
  right-half-plane root, which MIMO counts.
- **Grid-following, `--outer-loop-only`: SISO.** The ideal PLL never rolls off, so the dq return difference grows without
  bound along the jw axis and the MIMO winding misses the real right-half-plane root the closed loop has on `case9` at every
  SCR from 1 to 10. SISO counts it. `--criterion mimo` with `--outer-loop-only` exits 2.
- **Grid-forming: SISO.** Its admittance does not couple the sequences, so SISO is exact and keeps the gain and phase
  margins.

```
Canonical grid:   Z_grid(f) = R + j X (f/f0)          |Z| = 1 / (SCR * P_rated_pu),  X/R = 10
SISO pairing:     Z_pp(f) = Z_grid(f + f0)             Z_nn(f) = Z_grid(f - f0)
Grid-following:   Y_pp = 1/2 (G_ci/Vd0 - G_pll) (Id0 + j Iq0)
                  Y_nn = 1/2 (G_ci/Vd0 - G_pll) (Id0 - j Iq0)
                  G_pll(s) = (kp s + ki) / (s^2 + Vd0 kp s + Vd0 ki)
                  kp = 2 zeta wn / Vd0,  ki = wn^2 / Vd0,  wn = 2 pi f_bw
                  G_ci(s)  = wci / (s + wci)          wci = 2 pi 1000 Hz, or 2 pi (--current-bw) at PllCurrentOuter
                  OuterLoopOnly: G_pll = 1 / Vd0 (ideal PLL)
Grid-forming:     Z(s) = [Rv + s Lv + Dp/(1 + s tau_p)] I + w0 Lv J     Y = Z^-1, Xv = 0.20 pu, Rv = 0.05 Xv,
                  Dp = 0.30 pu, tau_p = 1/(2 pi f_pl), f_pl = 5 Hz
```

The flag-built grid-following model uses a 20 Hz PLL bandwidth and 0.707 damping (`ConverterDefaults.PllBandwidthHz`,
`PllDampingRatio`, placeholders) unless `--pll-bw` and `--pll-damping` override them; each must be a finite number
above 0, else exit 2. When the case binds an asset class to the chosen generator, the class supplies the parameters, and a flag
overrides only its own parameter. The grid-forming model reads `--pll-bw` only as its power-loop bandwidth.

**Nyquist line.** `encirclements 0  unstable-modes 0  GM inf dB  PM inf deg  minDist 0.874  -> STABLE`.

- `encirclements` is the net winding of `(1 + L_pp)(1 + L_nn)` (SISO) or of `det(I + Y_conv Z_grid)` (MIMO, the 2x2 dq form) around the
  origin over the full contour: the positive frequencies and their conjugate mirror. The contour runs from 1e-3 Hz to 1e5 Hz
  at no fewer than 60 points per decade, whatever the reporting band is. The converter and the grid are each stable on their
  own, so a count of 0 means the closed loop is stable. An unstable loop reads as a non-zero count, negative in this
  implementation (the `--outer-loop-only` run under [Pitfalls](#pitfalls) prints `-1`).
- `unstable-modes` is the absolute value of `encirclements`. An oscillatory closed-loop mode is a complex pair of
  eigenvalues and counts as 2, the convention of the modal check.
- `GM` is the gain margin in dB at the phase crossover (the phase of `L_pp` crosses -180 degrees), `inf` when there is none.
- `PM` is the phase margin in degrees at the gain crossover (`abs(L_pp)` crosses 1), `inf` when there is none. `GM` and `PM` are
  SISO quantities read from the positive-sequence loop; a MIMO run prints `inf` for both.
- `minDist` is the distance to the critical point: the smaller of `abs(1 + L_pp)` and `abs(1 + L_nn)` over the contour (SISO),
  or the smallest `abs(det(I + Y_conv Z_grid))` (MIMO).
- The verdict is STABLE when `encirclements` is 0 and UNSTABLE otherwise. The margins support the verdict and do not decide it:
  a negative `PM` on a STABLE line (`--criterion siso` on `case9` prints `PM -113.2 deg`) is a result the encirclement count
  overrides.

The verdict is computed on the contour above, so `--fmin`, `--fmax` and `--ppd` change the frequency band written to
`--enableExport` and nothing else, except that on `--grid real` an `--fmin` at or below 0.005 Hz (1e-4 of f0), or a MIMO
reporting point on f0 under either criterion, reaches the DC arm of the driving-point solve and exits 3.

**SCR boundary.** With `--grid canonical` the scan repeats the Nyquist count for 37 short-circuit ratios from 1.0 to 10.0
(`--scr-min`, `--scr-max`, `--scr-steps`). `critical SCR 1.12  (weak-grid instability below this)` is the midpoint between the
last unstable and the first stable sweep point, found on the first change from unstable at low SCR to stable at high SCR.
Without such a change the line says what the sweep found: `none in [1.0, 10.0]  (stable across the swept SCR range)`,
`(UNSTABLE at every swept SCR: any boundary lies above 10.0)`, or `(no unstable-to-stable crossing; N of M swept SCRs
UNSTABLE)`. With `--grid real` it reads `n/a (--grid real: the network's own impedance is used, no SCR sweep runs)`, and
with fewer than 2 sweep steps `n/a (no SCR sweep ran: --scr-steps below 2)`.

**Cross-check.** `RHP eigenvalues 0  agreement OK  [swing model: ...]` compares the Nyquist unstable-mode count with the
right-half-plane count of a small-signal model built the way `modal` builds it, with inverter states only when the case
binds inverter classes. The bracket names that model's basis: `swing model: X'd Anderson-Fouad, zero damping (no damping
data)` on `case9`, and `swing model: X'd synthetic 0.30 pu, zero damping (no damping data); INDICATIVE` elsewhere.
`agreement OK` means the two counts are equal. `agreement differs (structures differ; Routh oracle is decisive - see tests)`
means they are not; because the two models have different structures, a difference is a flag to look closer, and the
analytic check in the tests is the decisive one. `n/a on this case (no modal model)` means the modal model could not be built,
and stderr carries the reason.

**Provenance.** `CuratedPlaceholder [VERIFY] pll/current-loop params literature-typical` marks a converter built from the flags
or from defaults: its control parameters are placeholders from the literature, not data about any real unit. `Recorded` marks
a model whose PLL bandwidth or virtual reactance came from a bound asset class that carries recorded values and has no flag
override. `[VERIFY]` is printed only with `CuratedPlaceholder`.

**Exit code.** 0 once the scan completes, STABLE or UNSTABLE. A script must read the verdict text. 3 for a power flow that
does not converge or a singular real-grid solve, 2 for a usage error, 1 for a case file that cannot be read.

## Variations

### Exciters and a stabiliser on the RMS engine

`--avr sexs` or `--avr ieeet1` puts an exciter on every integrated machine, and `--pss` adds a PSS1A stabiliser to each. The
exciter reference is set from each machine's terminal voltage at the pre-fault equilibrium, so the undisturbed run stays at
rest. Converter plant and held units get none. The settings are fixed typical values:

- SEXS: lead 0.1 s, lag 1.0 s, gain 100, exciter time constant 0.05 s, field limits 0 and 8 pu.
- IEEET1: regulator gain 50 and time constant 0.02 s, exciter constant 1.0 and time constant 0.2 s, feedback gain 0.05 and
  time constant 1.0 s, regulator limits -10 and 10 pu, no saturation.
- PSS1A: gain 20, washout 10 s, two lead-lag stages with lead 0.15 s and lag 0.05 s, output limits -0.1 and 0.1 pu.

```bash
gridsim stability case9 --rms --avr sexs --screen
```

```
$ gridsim stability case9 --rms --avr sexs --screen
GridSim  -  RMS TRANSIENT STABILITY (salient/subtransient GENROU)  *  WSCC-9
==================================================================
3 machines  *  6-state machines  *  field: SEXS  *  GENROU params synthesised from X'd

 Rank  Fault bus   CCT (ms)
    1          3        308   <- most critical
    2          2        372
    3          1        616

CCT = longest clearing time on the 2 ms integration-step grid that keeps synchronism (clearing one step, 2 ms, later loses it); resolution 2 ms.
A CCT of 0 ms means even an instantaneous clear loses synchronism; 1000 ms means no clearing time in the 1000 ms search window loses it.
It is the first loss from below: the 16 steps (32 ms) below it are checked to hold and as many above it are probed; NON-MONOTONE marks a later clearing time that holds again (a stable pocket).
Each trial is judged over the 5 s after the fault clears (the documented default window: first-swing stability, the same window as the classical search); an undamped system can lose synchronism on a later swing, so a longer window can give a shorter CCT.

Self-clearing 3-phase fault; the subtransient engine is validated (reduces to the classical CCT),
but these GENROU parameters are synthesised from X'd - the result is INDICATIVE, not validated.
[exit 0]
```

Against the held-field screen (bus 3 at 292 ms, bus 2 at 326 ms, bus 1 at 484 ms) the SEXS exciter lifts every bus: 308, 372
and 616 ms. At the default fault bus 7 the held field gives 554 ms (the `--rms` capture above), and SEXS alone gives 1000 ms:

```bash
gridsim stability case9 --rms --avr sexs
```

```
$ gridsim stability case9 --rms --avr sexs
GridSim  -  RMS TRANSIENT STABILITY (salient/subtransient GENROU)  *  WSCC-9
==================================================================
3 machines  *  6-state machines  *  field: SEXS  *  GENROU params synthesised from X'd

fault bus 7  *  critical clearing time = 1000 ms
  (no clearing time up to the 1000 ms search window loses synchronism)
...
```

IEEET1 with PSS1A gives 878 ms, with a stable pocket at 882 ms:

```bash
gridsim stability case9 --rms --avr ieeet1 --pss
```

```
$ gridsim stability case9 --rms --avr ieeet1 --pss
GridSim  -  RMS TRANSIENT STABILITY (salient/subtransient GENROU)  *  WSCC-9
==================================================================
3 machines  *  6-state machines  *  field: IEEET1 + PSS1A  *  GENROU params synthesised from X'd

fault bus 7  *  critical clearing time = 878 ms
  (bracket: clearing at 878 ms holds synchronism, at 880 ms it is lost)
  NON-MONOTONE: clearing at 882 ms holds synchronism again (a stable pocket above the first loss); the reported CCT is the first loss from below, so do not read a longer clearing time as safe.
...
```

A CCT of 1000 ms is the top of the search window. It means no clearing time up to 1000 ms loses synchronism, not that the
margin is 1000 ms. `--avr` and `--pss` apply to `--rms` only: `--avr` without `--rms` or `--pss` without an exciter exits 2.

### A single fault at a chosen bus and clearing time

`--fault-bus N` moves the fault; `--clear MS` replaces the search with one run and prints a verdict (see the `--clear`
paragraphs above). Both work with and without `--rms`. A `--fault-bus` that is not in the case exits 2 before any solve.
`--export <file.json>` writes the classical engine's no-fault trajectory for the scipy cross-check described in
[ch. 27, Validation tools](<0027 Validation tools.md>); it applies to the classical single-bus run only, and with
`--rms` or `--screen` it exits 2.

### Clearing by tripping, and RMS event studies

On the RMS engine a fault can be cleared the way a line fault is: `--clear-by-trip K[,K...]` trips the listed branches
(0-based indices into the case's branch list) at the clearing instant. Without `--clear` the CCT search runs with those
trips at every trial's clearing instant; with `--clear` it is one verdict:

```bash
gridsim stability case9 --rms --fault-bus 7 --clear-by-trip 5
gridsim stability case9 --rms --fault-bus 7 --clear 120 --clear-by-trip 5
```

`--events` runs one scheduled disturbance instead: a comma list of `kind:arguments@seconds` items, `fault:BUS@t`,
`clear:BUS@t`, `trip:K@t`, `reclose:K@t` (auto-reclose of a tripped branch), `load:BUS:P:Q@t` (a load step in MW and
Mvar) and `gentrip:G@t`. The run lasts 3 s, or 3 s past the last event, and prints the events and a STABLE or UNSTABLE
verdict over the connected machines:

```bash
gridsim stability case9 --rms --events "fault:7@0.1,clear:7@0.183,trip:5@0.183,reclose:5@0.683"
```

`--events` owns the whole disturbance, so `--screen`, `--fault-bus`, `--clear` and `--clear-by-trip` with it exit 2.
`--events-out <dir>` writes the trajectories: `rms-events-machines.csv` (every machine at every step),
`rms-events-converters.csv` when the case has converter plant, `rms-events.json` (schema `gridsim-rms-events-export/1`)
and `run-metadata.json`, each tagged `Synthetic` (a model trajectory). `--events-every N` writes every N-th step only;
on a large case the full-resolution files grow with every machine and every step, so thin them. Both flags are refused
without the flag they qualify. Every flag is in [ch. 19, stability](<0019 CLI reference.md#stability>).

### Machine data from a file

`--dyn-data <file>` reads a machine dynamic data file (`gridsim-dynamics/2`). The classical engine (single bus and
`--screen`) and `modal` take X'd, H and the damping D for each machine the file binds; `--rms` takes the GENROU
reactances and time constants, inertia, damping, saturation and leakage, and the exciter (SEXS, IEEET1, EXAC1, ESAC1A or ESST4B, with a
PSS1A) and governor (TGOV1, IEEEG1, HYGOV or GAST) the machine's template names. Machines the file does not bind keep the
built-in data. The header counts the values that came from the file and adds a `machine data:` line; the INDICATIVE
label goes only when the file covers every machine and every value is tagged `Recorded` or `DerivedFromRecorded`. A
file can give a MATPOWER case the inertia it lacks, so `modal case14` with a file that binds its five units runs where
the bare case exits 2. No such file ships with GridSim; the format is in `schemas/schemaNotes.md`. A missing file, or one that fails the schema, kind or binding checks, exits 1 with every problem listed.

### Renewable models from a PSS/E DYR file

With a PSS/E `.raw` case, `--dyn-data` may be a `.dyr` file. A unit whose DYR records give it a WECC renewable model
chain runs on that chain instead of the built-in converter (chapter 2 lists what the reader accepts; technical ch. 24
has the equations). The library:

| Model | DYR record names read | Role |
|---|---|---|
| REGC_A | REGCA1, REGCAU1, REGC_A | Generator/converter, current source (LVPL, LVACM, HVRCM, current rate limits) |
| REGC_B | `'REGC_B'` | Generator/converter, voltage source behind `re + jXe`, current limit at the network interface |
| REEC_A, REEC_B, REEC_C | REECA1, REECB1, REECCU1 (and the V33 and WECC names) | Electrical control (voltage-dip logic, Q/V control, P/Q priority current limit; REEC_C adds the store) |
| REEC_D | `'REEC_D'` | Electrical control with ten-point VDL tables, compensation, post-dip holds and converter blocking |
| REPC_A | REPCA1, REPCAU1, REPC_A | Plant controller: voltage or Q at a regulated bus, frequency droop, optional branch-flow measurement |
| WTGT_A, WTGAR_A, WTGPT_A, WTGTRQ_A | WTDTA1, WTARA1, WTPTA1, WTTQA1 | Wind: drive train (type 4), and aerodynamics, pitch and torque control (type 3, with REECA1 PFLAG = 1) |
| DER_A | `'DER_A'` | Aggregated distributed resources, stand-alone |
| REGFM_A1 | `'REGFM_A1'` | Droop grid-forming inverter, stand-alone |

**Record orders.** Where a public specification gives the parameter order, GridSim reads the PSS/E record. Where only
the WECC or PNNL table is public, GridSim reads a record carrying the WECC name in quotes, with the values in the order
of that table: `'REGC_B'` the 11 values of Table 1 of the WECC REMWG memo Rev. 27b; `'REEC_D'` six flags (PfFlag VFlag
QFlag PFlag PqFlag Vcmpflag), then the 78 values of Table 5 of the EPRI REGC_B/REEC_D test protocol; `'REGFM_A1'` the 22
data values of PNNL-35110 Table 1 (the set points Vref, Qref and Pref come from the power flow); `'DER_A'` the 48
values of the DER_A memo's Table 1. The PSS/E names REGCB1, REGCBU1, REECD1, REECDU1, REGFMA1, REGFMAU1, DERA1 and
DERAU1 are refused, and the message says why: their record orders are not public, and GridSim does not guess an order.

**Parameters and checks.** Every value is range-checked, and the file is refused with the full list of problems. At
the start each model must hold the solved operating point: a unit the model cannot hold, for example a current above
its limit or a voltage inside the dip band, is refused and the quantity named, and every state must then be at rest to
1e-9 pu. REGC_A's Accel is read and has no effect, because GridSim solves the converter and network to 1e-10 pu
whatever it is. REPC_A's frequency estimate goes through a 0.02 s filter (`RepcaParameters.FrequencyFilterSeconds`, an
API setting rather than DYR data). The filter changes no steady state. WTGTRQ_A's TRATE must equal the unit's MBASE.

**Conventions to know.**
- A REGC_B unit's source sits in the frame of its terminal voltage in each network solution, so an off-nominal
  frequency does not bias its power. Its network interface is solved exactly with the network, also on weak
  connections and with several REGC_B units. Where a near-terminal fault leaves no consistent terminal angle, the
  frame is held at its last angle until one exists again.
- REGC_B's current limit at the network interface (Imax, pqflag) follows GridSim's reading of the memo: the EPRI
  method it cites is not public.
- REEC_D blocking sets REGC_B's current to zero at once; REGC_A ignores the blocking flag.
- REGFM_A1 controls are not frozen while its fault-current limit acts (the specification does not say to freeze them).
- REGFM_B1, REPC_B and REPC_C are not modelled. REGFM_B1's public text leaves its steady-state current limiter and
  several units unspecified, REPC_B has no public block diagram, and REPC_C needs shunt data no case format carries.
  GridSim's own grid-forming converter (the virtual synchronous machine) stays available.

**Grid-following against grid-forming.** One GridSim study on WSCC-9 (case9) puts an 85 MW unit on 128 MVA at bus 3 in
three forms: grid-following type 4 wind (REGC_A + REEC_A + WTGT_A), REGFM_A1 droop grid-forming, and GridSim's virtual
synchronous machine (VSM). The machines have no governors and D = 0. The numbers are GridSim's; no external reference
exists for this case.

| 50 MW load step at bus 5, t = 0.5 s | Grid-following | REGFM_A1 | VSM |
|---|---|---|---|
| Converter power change in the first 5 ms step | -0.8992 MW | +11.0267 MW | +11.0267 MW |
| Converter power change at 1 s | +0.0104 MW | +26.6928 MW | +8.3920 MW |
| Centre-of-inertia RoCoF over the first 100 ms | -0.190438 Hz/s | -0.158745 Hz/s | -0.159318 Hz/s |
| Frequency change at 10 s | -1.813306 Hz (still falling) | -0.253148 Hz (on its droop) | -0.830151 Hz |

| 100 ms bolted fault at bus 9 | Grid-following | REGFM_A1 | VSM |
|---|---|---|---|
| Largest angle, machine 2 minus machine 1 (start 13.8611 deg) | 27.9893 deg at 4.785 s | 24.6103 deg at 0.385 s | 23.9407 deg at 0.375 s |
| Converter current mid-fault (0.15 s), on its rating | 0.7645 pu | 0.9601 pu | 1.2432 pu |

A current-source unit holds current, not power, so its power falls with the voltage at the load step. The two
voltage-source units pick up 22 % of the 50 MW step within the first step, and the first-100 ms RoCoF falls by 16.6 %.
REGFM_A1 carries inertia only through its 10 ms power filter (equivalent H = 0.5 s, damping 100 pu on its rating),
against the VSM's H = 2 s and 5 % droop. Both units start identically and then follow through differently.

### Other cases

`uk` is the reduced GB model. The classical run uses a fault at bus 101, the first bus without a generator, a synthetic X'd and
13 machines. It reports a CCT of 764 ms with a stable pocket at 788 ms. The machine table, elided below, has two rows
for a bus that carries two machines (buses 3, 4 and 10):

```
$ gridsim stability uk
...
13 machines (16 converter / power-flow-only / zero-inertia units held)  *  fault bus 101  *  X'd = synthetic 0.30 pu (unvalidated)
...
Critical clearing time for a 3-phase fault at bus 101:  764 ms
(the longest the fault can persist before the fleet loses synchronism on self-clear)
  (bracket: clearing at 764 ms holds synchronism, at 766 ms it is lost)
  NON-MONOTONE: clearing at 788 ms holds synchronism again (a stable pocket above the first loss); the reported CCT is the first loss from below, so do not read a longer clearing time as safe.
```

`gb-full`, the full 3,539-bus GB case, runs on the sparse RMS network. This captured run clears a fault at bus 35 after 150
ms:

```bash
gridsim stability gb-full --rms --fault-bus 35 --clear 150
```

```
$ gridsim stability gb-full --rms --fault-bus 35 --clear 150
GridSim  -  RMS TRANSIENT STABILITY (salient/subtransient GENROU)  *  GB full (3539-bus, 5918-branch)
==================================================================
952 machines  *  6-state machines  *  field: held (no AVR)  *  GENROU params synthesised from X'd
3293 converter-interfaced units  *  REGC_A/REEC-style current sources (K=2, reactive priority, LVACM)
1072 power-flow-only + 1 zero-inertia units held as constant injections

fault bus 35  *  cleared after 150 ms  *  STABLE (fleet stays in synchronism)  *  3.7 s
law 9 (synchronism, scored from this run): HOLDS: largest rotor angle from the centre of inertia 49.1 deg (limit 180 deg, the out-of-step criterion); peak speed deviation 0.675 Hz, reported, not scored (no frequency band in a fault study)
converters: 628 nodes, 369 in fault ride-through mid-fault, 628 recovered by 3 s; active power 23941 MW before, 23944 MW at 3 s

Self-clearing 3-phase fault; the subtransient engine is validated (reduces to the classical CCT),
but these GENROU parameters are synthesised from X'd - the result is INDICATIVE, not validated.
[exit 0]
```

952 machines are integrated, 3,293 converter units enter as current sources, and 1,073 units are held. The 3.7 s is wall time
on the capture machine.

### Damping in `modal`

`--damping D` applies the same `D` to every machine:

```bash
gridsim modal case9 --damping 5
```

```
$ gridsim modal case9 --damping 5
GridSim  -  SMALL-SIGNAL / MODAL STABILITY  *  WSCC-9
==================================================================
3 machine(s), 6 state(s)  *  X'd = Anderson-Fouad  *  inverters off  *  damping D = 5.000

   sigma (1/s)  omega (rad/s)    f (Hz)      zeta   mode
       -0.0995        -6.9568    1.1072    0.0143   oscillatory
       -0.0995         6.9568    1.1072    0.0143   oscillatory
       -0.2967       -12.4929    1.9883    0.0237   oscillatory
       -0.2967        12.4929    1.9883    0.0237   oscillatory
       -0.1027         0.0000    0.0000    1.0000   real
        0.0000         0.0000    0.0000    0.0000   real

Least-damped oscillatory mode: 1.107 Hz, zeta = 0.0143  (poorly damped)
Right-half-plane eigenvalues (small-signal unstable): 0
Minimum oscillatory damping ratio: 0.0143

Dominant participation (which state drives each oscillatory mode):
    1.107 Hz (zeta  0.0143) : d.delta_gen2 36%, d.omega_gen2 36%, d.omega_gen1 11%
    1.107 Hz (zeta  0.0143) : d.delta_gen2 36%, d.omega_gen2 36%, d.omega_gen1 11%
    1.988 Hz (zeta  0.0237) : d.delta_gen3 44%, d.omega_gen3 44%, d.omega_gen2 6%
    1.988 Hz (zeta  0.0237) : d.delta_gen3 44%, d.omega_gen3 44%, d.omega_gen2 6%
```

The pairs move into the left half-plane (`sigma` -0.0995 and -0.2967), the damping ratios become 0.0143 and 0.0237, the
least-damped mode is the 1.107 Hz pair, and machine 1 appears in the participation table. One real eigenvalue,
-0.1027, is the decaying common speed; the other, 0, is the free common angle.

### Inverters in `modal`

`--enable-inverters` adds the linearised control states of the grid-forming and grid-following classes the case binds. It
needs a case with an asset-dynamics bundle, such as `--dir data/gb-synthetic`; on a case without one it exits 2. The inverters
couple to the centre of inertia, a documented archetype, so the machine swing block is the same as without the flag.

```bash
gridsim modal --dir data/gb-synthetic --enable-inverters
```

```
$ gridsim modal --dir data/gb-synthetic --enable-inverters
GridSim  -  SMALL-SIGNAL / MODAL STABILITY  *  GB synthetic
==================================================================
13 machine(s), 116 state(s)  *  X'd = synthetic 0.30 pu (unvalidated)  *  inverters ON  *  damping D = 0 (undamped)

   sigma (1/s)  omega (rad/s)    f (Hz)      zeta   mode
        0.0158         3.7573    0.5980   -0.0042   oscillatory
        0.0158        -3.7573    0.5980   -0.0042   oscillatory
...
Least-damped oscillatory mode: 0.598 Hz, zeta = -0.0042  (UNSTABLE)
Right-half-plane eigenvalues (small-signal unstable): 2
Minimum oscillatory damping ratio: -0.0042

Dominant participation (which state drives each oscillatory mode):
  unavailable: the state matrix is defective, so participation factors are undefined (eigenvalues only; the reason is on stderr).

...
Eigenvalues of A = [[0, ws I],[-M^-1 K_L, -M^-1 D]] (true asymmetric K_L; Francis-QR, biorthonormal participation).
Provenance: X'd is a synthetic default - damping/participation are INDICATIVE, not validated.
Inverter states are a documented archetype (COI-coupled; GFL PLL is an SRF-PLL substitution of the transport delay).
modal: participation factors unavailable - damped / inverter-augmented path: the state matrix is defective, so participation factors are undefined and are reported as all zero (eigenvalues only). Decomposition said: eigenvalue <-10.000000403857998; 3.432645684750273E-08> is repeated but its eigenspace has too few independent directions: the deflated eigenvector leaves a residual |A v - lambda v| = 6.647E+001, above the tolerance 5.333E-002. A is defective, so biorthonormal participation is undefined.
[exit 1]
```

This case has 116 states and two right-half-plane eigenvalues, a pair at +0.0158 +/- j3.7573 (0.598 Hz, `zeta = -0.0042`),
so the least-damped line reads `(UNSTABLE)` and the exit code is 1. The participation table reads `unavailable` and stderr
says why: the state matrix is defective (the eigenvalue -10 is repeated without enough independent eigenvectors), so
participation factors do not exist; the eigenvalues are still valid. The inverter parameters are placeholders, so read this as a
demonstration of the output, not as a statement about GB.

### Exporting `modal`

`--export <file.json>` writes the model and modes as a `gridsim-smallsignal/1` document and prints one line,
`Exported small-signal model (6 states) -> <path>`, before the tables. The top-level fields are `schema`, `label`, `f0`,
`inverterStatesIncluded`, `states`, `aMatrix`, `angleNodeBuses`, `kext`, `eigenvalues`, `dampingRatios`, `frequenciesHz`,
`participation`, `rhpEigenvalueCount`, `minDampingRatio`, `participationAvailable` and `exitCode`. `participationAvailable` is false
when the matrix is defective, and `participationUnavailableReason` is added then. `--json` prints the same document to the
console instead of the tables and takes no value. `--json file.json` exits 2 and points at `--export`. The schema list is in
[the file and wire formats chapter](<../technical/0010 File and wire formats.md>).

### Grid-forming and MIMO scans

```bash
gridsim impedance-stability case9 --converter gfm
gridsim impedance-stability case9 --criterion mimo
```

```
$ gridsim impedance-stability case9 --converter gfm
impedance-stability: case IEEE 9-bus (WSCC), inverter bus 2, mode GridFormingInverter, detail PllOuter, criterion Siso
  operating point : Vd0=1.0250 pu, Id0=0.8283, Iq0=-0.0338 pu
  nyquist         : encirclements 0  unstable-modes 0  GM inf dB  PM -122.5 deg  minDist 1.705  -> STABLE
  SCR boundary    : none in [1.0, 10.0]  (stable across the swept SCR range)
  cross-check (C) : RHP eigenvalues 0  agreement OK  [swing model: X'd Anderson-Fouad, zero damping (no damping data)]
  provenance      : CuratedPlaceholder [VERIFY] pll/current-loop params literature-typical
[exit 0]
```

The grid-forming loop runs under SISO (exact for it) and has no phase crossover (`GM inf`). Its phase margin of -122.5
degrees is a read-out on a STABLE line, which the encirclement count overrides. `--criterion mimo` on the grid-following
default is the default run itself: it winds the 2x2 dq return difference and prints `inf` margins, with `minDist` of 0.874
on `case9`.

### A weak grid and the critical SCR

The flag-built PLL has a 20 Hz bandwidth. A 300 Hz PLL is unstable at the base SCR of 3 and finds a boundary inside the
default sweep:

```bash
gridsim impedance-stability case9 --pll-bw 300
```

```
$ gridsim impedance-stability case9 --pll-bw 300
impedance-stability: case IEEE 9-bus (WSCC), inverter bus 2, mode GridFollowingInverter, detail PllOuter, criterion Mimo
  operating point : Vd0=1.0250 pu, Id0=0.8283, Iq0=-0.0338 pu
  nyquist         : encirclements -1  unstable-modes 1  GM inf dB  PM inf deg  minDist 0.927  -> UNSTABLE
  SCR boundary    : critical SCR 5.62  (weak-grid instability below this)
  cross-check (C) : RHP eigenvalues 0  agreement differs (structures differ; Routh oracle is decisive - see tests)  [swing model: X'd Anderson-Fouad, zero damping (no damping data)]
  provenance      : CuratedPlaceholder [VERIFY] pll/current-loop params literature-typical
[exit 0]
```

The base scan at SCR 3 is UNSTABLE (one encirclement: one real closed-loop root in the right half plane), and the sweep is
unstable below the critical SCR of 5.62: the loop is unstable at SCR 5.5 and stable at 5.75, and their midpoint 5.625
prints as 5.62. The cross-check reads `agreement differs` because the `case9` modal model holds no converter and counts 0.
Under `--criterion siso` the same scan reads STABLE with a critical SCR of 1.12; the Routh-Hurwitz count of the closed
loop finds a right-half-plane root at SCR 1.25, 3 and 5.5 and none at 5.75, as MIMO does. An 80 Hz PLL scanned at SCR 0.3,
with the sweep floor lowered to 0.1:

```bash
gridsim impedance-stability case9 --pll-bw 80 --scr 0.3 --scr-min 0.1
```

```
$ gridsim impedance-stability case9 --pll-bw 80 --scr 0.3 --scr-min 0.1
impedance-stability: case IEEE 9-bus (WSCC), inverter bus 2, mode GridFollowingInverter, detail PllOuter, criterion Mimo
  operating point : Vd0=1.0250 pu, Id0=0.8283, Iq0=-0.0338 pu
  nyquist         : encirclements 0  unstable-modes 0  GM inf dB  PM inf deg  minDist 2.183  -> STABLE
  SCR boundary    : critical SCR 1.61  (weak-grid instability below this)
  cross-check (C) : RHP eigenvalues 0  agreement OK  [swing model: X'd Anderson-Fouad, zero damping (no damping data)]
  provenance      : CuratedPlaceholder [VERIFY] pll/current-loop params literature-typical
[exit 0]
```

The scan at SCR 0.3 reads STABLE. The sweep finds an unstable band between SCR 0.925 and 1.475 and is stable again from
1.75, so the critical SCR, the last unstable-to-stable crossing, is 1.61. The Routh-Hurwitz count of the closed loop agrees
at every swept point: no right-half-plane root at 0.1 to 0.65, one from 0.925 to 1.475, none from 1.6. `--criterion siso`
with the same flags reads UNSTABLE at SCR 0.3 (two encirclements, gain margin -5.2 dB) with a critical SCR of 0.51: the
SISO pairing drops the PLL's cross-sequence coupling, and here that turns both verdicts round. That is why `auto` runs
MIMO for a grid-following converter.

### Real network impedance

`--grid real` replaces the canonical Thevenin with the network's own harmonic driving-point impedance at the converter bus:

```bash
gridsim impedance-stability case9 --grid real
```

```
$ gridsim impedance-stability case9 --grid real
impedance-stability: case IEEE 9-bus (WSCC), inverter bus 2, mode GridFollowingInverter, detail PllOuter, criterion Mimo
  operating point : Vd0=1.0250 pu, Id0=0.8283, Iq0=-0.0338 pu
  nyquist         : encirclements 0  unstable-modes 0  GM inf dB  PM inf deg  minDist 0.020  -> STABLE
  SCR boundary    : n/a (--grid real: the network's own impedance is used, no SCR sweep runs)
  cross-check (C) : RHP eigenvalues 0  agreement OK  [swing model: X'd Anderson-Fouad, zero damping (no damping data)]
  provenance      : CuratedPlaceholder [VERIFY] pll/current-loop params literature-typical
WARNING: impedance-stability: the Nyquist contour turns 177.9 deg between two grid points (trusted up to 90 deg), so the encirclement count may be wrong; rerun with a larger --ppd (or a band that resolves the resonance).
[exit 0]
```

There is no SCR to vary, so `--scr`, `--scr-min`, `--scr-max` and `--scr-steps` exit 2 with this grid, and the `SCR boundary`
line always reads `n/a`. A singular solve at some frequency exits 3. The `WARNING` line comes from the negative-sequence
loop near f0, where `Z_grid(f - f0)` is read close to DC: the network's reactance there is small and changes sign across
the points dropped round f0, so the contour turns almost half a circle in one step. Both criteria read the same
`Z_grid(f - f0)` and print the same kind of warning, and a larger `--ppd` does not remove it; both count 0 encirclements on
this case. The MIMO `minDist` of 0.020 (0.341 under `--criterion siso`) says the dq return difference passes close to
singular near f0; the verdict rests on the encirclement count.

### The inner-loop detail levels

`--current-bw HZ` adds a first-order current loop of that bandwidth and `--pll-bw` and `--pll-damping` set the PLL:

```bash
gridsim impedance-stability case9 --pll-bw 40 --current-bw 400
```

```
$ gridsim impedance-stability case9 --pll-bw 40 --current-bw 400
impedance-stability: case IEEE 9-bus (WSCC), inverter bus 2, mode GridFollowingInverter, detail PllCurrentOuter, criterion Mimo
  operating point : Vd0=1.0250 pu, Id0=0.8283, Iq0=-0.0338 pu
  nyquist         : encirclements 0  unstable-modes 0  GM inf dB  PM inf deg  minDist 0.837  -> STABLE
  SCR boundary    : none in [1.0, 10.0]  (stable across the swept SCR range)
  cross-check (C) : RHP eigenvalues 0  agreement OK  [swing model: X'd Anderson-Fouad, zero damping (no damping data)]
  provenance      : CuratedPlaceholder [VERIFY] pll/current-loop params literature-typical
[exit 0]
```

`--outer-loop-only` freezes the PLL at its DC gain and takes no inner-loop flag. Its `case9` result is in
[Pitfalls](#pitfalls).

### Domains and the H=0 study

`gridsim domains` and `gridsim h0-study` are frequency-study verbs. Their output and use are in
[ch. 14, Frequency studies](<0014 Frequency studies.md>).

## Pitfalls

- **`No synchronous machines with inertia` and exit 2.** The case has no rotor to integrate. `case14` is the example:

```
$ gridsim stability case14
error: stability: No synchronous machines with inertia: the classical stability model has no rotor to integrate.
[exit 2]
```

  The built-in `case14` is the MATPOWER IEEE 14-bus case, which carries no inertia: all five generators load with `H = 0`.
  `stability` holds every generator that has no inertia, nothing is left to integrate, and the classical model has no rotor, so
  the verb exits 2. `modal` exits 2 for the same reason with a longer message (the capture is `docs/_captures/stability/modal-case14.txt`).
  The fix is a case that carries inertia (see [Before you start](#before-you-start)). Nothing in GridSim invents an inertia for it.
- **`error: --avr and --pss apply to the RMS engine only (add --rms).`** Exit 2. `--pss` without an exciter exits 2 with
  `error: --pss needs an AVR (--avr sexs or --avr ieeet1).`, and an unknown `--avr` value exits 2 with `error: --avr expected none,
  sexs or ieeet1 but got 'foo'`.
- **A CCT of 1000 ms is not a margin.** It is the search window. A fault cleared after 1000 ms would still hold, and no longer
  clearing time was tried.
- **`NON-MONOTONE` means the CCT is not a boundary.** Clearing times above the CCT can hold or lose. Design to the CCT, with
  margin, and do not read the pocket as a safe region.
- **A `--clear` verdict and a CCT can disagree at the boundary** on `--rms`, because the single run uses a 5 ms step and 3 s
  and the search uses 2 ms and 5 s past clearing. Classical `--clear` and the classical search use the same 2 ms grid and
  the case's own nominal frequency, so they agree exactly.
- **`INDICATIVE` is a statement about data.** The engines are validated; the parameters are not. Every `--rms` result
  and every classical result with a synthetic X'd carries it.
- **`--export` with `--rms` or `--screen` exits 2.** It writes the classical single-bus trajectory only.
- **`modal` exit 1 is a result.** It means a right-half-plane eigenvalue, and the export is still written.
- **`impedance-stability` exits 0 for an UNSTABLE verdict.** Read the `-> UNSTABLE` text; the exit code reports only that the
  scan completed.
- **`agreement OK` is weak evidence on a case without inverter classes.** On `case9` the modal model holds no converter, so `OK`
  means both counts are 0. The cross-check corroborates where the two models share a structure.
- **`--outer-loop-only` on `case9` reports UNSTABLE.** The ideal PLL model counts one encirclement at every SCR in the sweep:

```
$ gridsim impedance-stability case9 --outer-loop-only
impedance-stability: case IEEE 9-bus (WSCC), inverter bus 2, mode GridFollowingInverter, detail OuterLoopOnly, criterion Siso
  operating point : Vd0=1.0250 pu, Id0=0.8283, Iq0=-0.0338 pu
  nyquist         : encirclements -1  unstable-modes 1  GM inf dB  PM 141.8 deg  minDist 0.998  -> UNSTABLE
  SCR boundary    : none in [1.0, 10.0]  (UNSTABLE at every swept SCR: any boundary lies above 10.0)
  cross-check (C) : RHP eigenvalues 0  agreement differs (structures differ; Routh oracle is decisive - see tests)  [swing model: X'd Anderson-Fouad, zero damping (no damping data)]
  provenance      : CuratedPlaceholder [VERIFY] pll/current-loop params literature-typical
[exit 0]
```

  The sweep is unstable at every point, which the `SCR boundary` line states (`UNSTABLE at every swept SCR`), and the
  cross-check differs. Treat the outer-loop-only level as a first screen for a case that has no inner-loop data, not as a
  stability proof or a finding.
- **A converter flag the model cannot use exits 2.** `--pll-damping` and `--current-bw` with `--converter gfm`, `--outer-loop-only`
  with any inner-loop flag or with `--converter gfm`, and `--pll-bw` with a grid-forming asset class each stop the run. For example:

```
$ gridsim impedance-stability case9 --converter gfm --pll-damping 0.5
error: --pll-damping expected no value: the grid-forming model does not take a PLL damping ratio but got '0.5'
[exit 2]
```

- **`--grid real` with an SCR flag exits 2.**

```
$ gridsim impedance-stability case9 --grid real --scr 2
error: --scr expected no value: the SCR sweep applies to --grid canonical only but got '2'
[exit 2]
```

- **An unknown `--bus` exits 2** with `impedance-stability: no generator to attach a converter model to.` Without a case with
  modal data, the cross-check line reads `n/a on this case` and stderr gives the reason, as `impedance-stability case14` shows.

## Limits

- The classical model holds `|E'|` constant and has no governor or exciter, and damping only from a `--dyn-data` file;
  `modal` shares that structure. The RMS engine takes exciters and PSS1A from `--avr` / `--pss` or a file, and
  governors from a file only.
- No bundled case carries sub-transient machine data, so an `--rms` result without `--dyn-data` uses synthesised GENROU
  parameters, and every case but `case9` uses a synthetic 0.30 pu X'd.
- The classical study applies one self-clearing three-phase bolted fault at a bus (a `1e7` pu shunt), with no fault
  impedance. Branch trips, reclosing, load steps and generator trips exist on the RMS engine only (`--clear-by-trip`,
  `--events`).
- A trial ends 5 s after clearing, so a loss of synchronism that develops later is not seen.
- The CCT is the first loss from below only when no stable pocket is wider than 16 steps (32 ms).
- `modal` applies one damping value to every machine, treats a state-of-charge integrator as out of scope (`--include-soc`
  exits 2), and has no exciter or governor states. Participation factors do not exist for a defective state matrix.
- `impedance-stability` models one converter at one bus against a canonical Thevenin grid or the real network's driving-point
  impedance; it does not scan several converters together. Its converter parameters are placeholders unless a bound asset class
  gives recorded values.
- The `SCR boundary` line reports a critical SCR only for a sweep that changes from unstable to stable as SCR rises.

## See also

- [ch. 14, Frequency studies](<0014 Frequency studies.md>) - `domains`, `h0-study`, `transient` and the frequency view
- [ch. 16, Electromagnetic transients](<0016 Electromagnetic transients (EMT).md>) - sub-cycle waveforms with `emt`
- [Technical ch. 24, Standard dynamic-model library](<../technical/0024 Standard dynamic-model library.md>) - the machine,
  exciter, stabiliser and RMS converter models behind `--rms`
- [Technical ch. 30, Small-signal and impedance stability](<../technical/0030 Small-signal and impedance stability.md>) - the
  equations, invariants and tests behind `modal` and `impedance-stability`
- [ch. 19, CLI reference](<0019 CLI reference.md#stability>) - every `stability` flag, with the exit codes
- [ch. 19, CLI reference](<0019 CLI reference.md#modal>) - every `modal` flag
- [ch. 19, CLI reference](<0019 CLI reference.md#impedance-stability>) - every `impedance-stability` flag
- [ch. 19, CLI reference](<0019 CLI reference.md#domains>) and [ch. 19, CLI reference](<0019 CLI reference.md#h0-study>) - the two frequency verbs
- [ch. 27, Validation tools](<0027 Validation tools.md>) - the scipy and NumPy cross-checks of the classical, modal and impedance exports
